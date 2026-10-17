using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using DocSearch.Infrastructure.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Tests.Extraction;

public sealed class HttpPdfExtractorTests
{
    private readonly PersianTextNormalizer _normalizer = new();

    private static HttpPdfExtractor CreateExtractor(
        HttpMessageHandler handler,
        ExtractionOptions? options = null)
    {
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8000/")
        };

        var extractionOptions = Options.Create(options ?? new ExtractionOptions
        {
            Provider = "Sidecar",
            ExtractorBaseUrl = "http://localhost:8000/",
            MinPageQuality = 0.35
        });

        return new HttpPdfExtractor(
            client,
            extractionOptions,
            NullLogger<HttpPdfExtractor>.Instance);
    }

    [Fact]
    public async Task ExtractAsync_NullPdfStream_ThrowsArgumentNullException()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var extractor = CreateExtractor(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            extractor.ExtractAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_HappyPath_ReturnsExpectedPagesWithQualityScored()
    {
        var jsonResponse = JsonSerializer.Serialize(new
        {
            pages = new[]
            {
                new { page = 1, text = "This is a clean English document page with sufficient characters to pass quality checks easily." },
                new { page = 2, text = "این یک صفحه آزمایشی به زبان فارسی با متن کافی برای تایید کیفیت متن است." }
            }
        });

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("%PDF-1.4 mock content"u8.ToArray());

        var pages = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.Equal(2, pages.Count);
        Assert.Equal(1, pages[0].PageNumber);
        Assert.True(pages[0].Quality >= 0.9);
        Assert.False(pages[0].NeedsOcr);

        Assert.Equal(2, pages[1].PageNumber);
        Assert.True(pages[1].Quality >= 0.9);
        Assert.False(pages[1].NeedsOcr);
    }

    [Fact]
    public async Task ExtractAsync_OversizedPayload_ThrowsFileTooLargeException()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
        {
            Content = new StringContent("File too large", Encoding.UTF8, "text/plain")
        });

        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("%PDF-1.4 mock content"u8.ToArray());

        await Assert.ThrowsAsync<FileTooLargeException>(() =>
            extractor.ExtractAsync(pdfStream, CancellationToken.None));
    }

    [Fact]
    public async Task ExtractAsync_InvalidPdf_ThrowsInvalidFileException()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("File is not a valid PDF: missing %PDF- header.", Encoding.UTF8, "text/plain")
        });

        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("Not a pdf"u8.ToArray());

        var ex = await Assert.ThrowsAsync<InvalidFileException>(() =>
            extractor.ExtractAsync(pdfStream, CancellationToken.None));

        Assert.Contains("missing %PDF- header", ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_UnprocessableEntity_ThrowsInvalidFileException()
    {
        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent("Password-protected or encrypted PDFs are not supported.", Encoding.UTF8, "text/plain")
        });

        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("%PDF-1.4 encrypted"u8.ToArray());

        var ex = await Assert.ThrowsAsync<InvalidFileException>(() =>
            extractor.ExtractAsync(pdfStream, CancellationToken.None));

        Assert.Contains("encrypted", ex.Message);
    }

    [Fact]
    public async Task ExtractAsync_TransientServerError_RetriesTwiceAndThrowsExternalServiceUnavailableException()
    {
        var callCount = 0;
        var handler = new TestHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Internal server error", Encoding.UTF8, "text/plain")
            };
        });

        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("%PDF-1.4 mock"u8.ToArray());

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            extractor.ExtractAsync(pdfStream, CancellationToken.None));

        Assert.Equal(3, callCount); // 1 initial + 2 retries
        Assert.Equal("PdfExtractor", ex.ServiceName);
    }

    [Fact]
    public async Task ExtractAsync_TransientServerError_RetryResendsFullBody()
    {
        var receivedLengths = new List<long>();
        var jsonResponse = JsonSerializer.Serialize(new
        {
            pages = new[] { new { page = 1, text = "Success after retry" } }
        });

        var handler = new TestHttpMessageHandler(req =>
        {
            var contentBytes = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            receivedLengths.Add(contentBytes.Length);

            if (receivedLengths.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        });

        var extractor = CreateExtractor(handler);
        var originalPdfBytes = "%PDF-1.4 mock content with some non-trivial byte length"u8.ToArray();
        using var stream = new MemoryStream(originalPdfBytes);

        var pages = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(2, receivedLengths.Count);
        Assert.True(receivedLengths[0] > 0, "Attempt 1 request body should not be empty.");
        Assert.Equal(receivedLengths[0], receivedLengths[1]);
        Assert.Single(pages);
    }

    [Fact]
    public async Task ExtractAsync_TransientServerError_RetriesThenSucceeds()
    {
        var callCount = 0;
        var jsonResponse = JsonSerializer.Serialize(new
        {
            pages = new[]
            {
                new { page = 1, text = "Valid text recovered after retry." }
            }
        });

        var handler = new TestHttpMessageHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
            };
        });

        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("%PDF-1.4 mock"u8.ToArray());

        var pages = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.Single(pages);
        Assert.Equal("Valid text recovered after retry.", pages[0].Text);
    }

    [Fact]
    public async Task ExtractAsync_CallerCancellation_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled caller token

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("%PDF-1.4 mock"u8.ToArray());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            extractor.ExtractAsync(pdfStream, cts.Token));
    }

    [Fact]
    public async Task ExtractAsync_ConnectionFailure_ThrowsExternalServiceUnavailableException()
    {
        var callCount = 0;
        var handler = new TestHttpMessageHandler(_ =>
        {
            callCount++;
            throw new HttpRequestException("Connection refused by host: 127.0.0.1:8000");
        });

        var extractor = CreateExtractor(handler);
        using var pdfStream = new MemoryStream("%PDF-1.4 mock"u8.ToArray());

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            extractor.ExtractAsync(pdfStream, CancellationToken.None));

        Assert.Equal(3, callCount);
        Assert.Equal("PdfExtractor", ex.ServiceName);
        Assert.Equal("Extractor sidecar connection failed after 3 attempts.", ex.Message);
        Assert.DoesNotContain("Connection refused", ex.Message);
    }

    [SkippableFact]
    public async Task ExtractAsync_PersianSample_MatchesInvariantsAndPhrase()
    {
        var (sampleBytes, testDataDir) = FindTestDataFile("persian-sample.pdf");
        Assert.NotEmpty(sampleBytes);

        var filePath = Path.Combine(testDataDir, "persian-sample.pdf");
        var realPyMuPdfJson = GetRealPyMuPdfExtractionJson(filePath);

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(realPyMuPdfJson, Encoding.UTF8, "application/json")
        });

        var extractor = CreateExtractor(handler);
        using var stream = new MemoryStream(sampleBytes);

        var pages = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(2, pages.Count);
        Assert.False(pages[0].NeedsOcr, "Persian digital page 1 should not require OCR.");
        Assert.False(pages[1].NeedsOcr, "Persian digital page 2 should not require OCR.");

        // NormalizeForDisplay preserves ZWNJ; replace ZWNJ with space and collapse whitespace to assert multi-line title phrase
        var displayNormalized = Regex.Replace(
            _normalizer.NormalizeForDisplay(pages[0].Text).Replace('\u200C', ' '),
            @"\s+",
            " ");
        Assert.Contains("آیین نامه آموزشی دوره های کارشناسی", displayNormalized);

        // NormalizeForSearch also resolves it cleanly
        var searchNormalized = Regex.Replace(
            _normalizer.NormalizeForSearch(pages[0].Text),
            @"\s+",
            " ");
        Assert.Contains("آیین نامه آموزشی دوره های کارشناسی", searchNormalized);
    }

    [SkippableFact]
    public async Task ExtractAsync_EnglishSample_MatchesInvariants()
    {
        var (sampleBytes, testDataDir) = FindTestDataFile("english-sample.pdf");
        Assert.NotEmpty(sampleBytes);

        var filePath = Path.Combine(testDataDir, "english-sample.pdf");
        var realPyMuPdfJson = GetRealPyMuPdfExtractionJson(filePath);

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(realPyMuPdfJson, Encoding.UTF8, "application/json")
        });

        var extractor = CreateExtractor(handler);
        using var stream = new MemoryStream(sampleBytes);

        var pages = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Single(pages);
        Assert.False(pages[0].NeedsOcr);
        Assert.Contains("Retrieval-Augmented Generation", pages[0].Text);
    }

    [SkippableFact]
    public async Task ExtractAsync_ScannedSample_RoutesToOcr()
    {
        var (sampleBytes, testDataDir) = FindTestDataFile("scanned-sample.pdf");
        Assert.NotEmpty(sampleBytes);

        var filePath = Path.Combine(testDataDir, "scanned-sample.pdf");
        var realPyMuPdfJson = GetRealPyMuPdfExtractionJson(filePath);

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(realPyMuPdfJson, Encoding.UTF8, "application/json")
        });

        var extractor = CreateExtractor(handler);
        using var stream = new MemoryStream(sampleBytes);

        var pages = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(2, pages.Count);
        Assert.Equal(0.0, pages[0].Quality);
        Assert.True(pages[0].NeedsOcr, "Page 1 of scanned document must have NeedsOcr=true.");
        Assert.Equal(0.0, pages[1].Quality);
        Assert.True(pages[1].NeedsOcr, "Page 2 of scanned document must have NeedsOcr=true.");
    }

    private static string? ResolvePythonExecutable()
    {
        var env = Environment.GetEnvironmentVariable("PYTHON_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var repoRoot = FindRepoRoot();
        if (repoRoot != null)
        {
            var windowsVenv = Path.Combine(repoRoot, ".venv", "Scripts", "python.exe");
            if (File.Exists(windowsVenv))
            {
                return windowsVenv;
            }

            var unixVenv = Path.Combine(repoRoot, ".venv", "bin", "python");
            if (File.Exists(unixVenv))
            {
                return unixVenv;
            }
        }

        foreach (var cmd in new[] { "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (p != null)
                {
                    p.WaitForExit();
                    if (p.ExitCode == 0)
                    {
                        return cmd;
                    }
                }
            }
            catch
            {
                // Not on PATH or execution failed
            }
        }

        return null;
    }

    private static string GetRealPyMuPdfExtractionJson(string pdfPath)
    {
        var pythonExe = ResolvePythonExecutable();
        Skip.IfNot(pythonExe != null, "Python executable not found in PYTHON_EXECUTABLE, .venv, or system PATH.");

        var script = "import pymupdf, json, sys; sys.stdout.reconfigure(encoding='utf-8'); doc = pymupdf.open(sys.argv[1]); pages = [{'page': i+1, 'text': p.get_text('text')} for i, p in enumerate(doc)]; print(json.dumps({'pages': pages}, ensure_ascii=False)); doc.close()";

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = pythonExe!,
                Arguments = $"-c \"{script}\" \"{pdfPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            });
        }
        catch (Exception ex)
        {
            Skip.IfNot(false, $"Failed to start python process ({pythonExe}): {ex.Message}");
            return string.Empty;
        }

        Skip.IfNot(process != null, "Failed to start python process for real PyMuPDF extraction.");

        var stdout = process!.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            if (stderr.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase))
            {
                Skip.IfNot(false, $"PyMuPDF is not installed in the resolved Python environment ({pythonExe}): {stderr}");
            }
            else
            {
                Assert.Fail($"Real PyMuPDF extraction process failed (exit code {process.ExitCode}): {stderr}");
            }

            return string.Empty;
        }

        return stdout;
    }

    private static string? FindRepoRoot()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }

    private static (byte[] Bytes, string Dir) FindTestDataFile(string fileName)
    {
        var repoRoot = FindRepoRoot();
        Skip.IfNot(repoRoot != null, "Could not locate repository root containing global.json.");

        var filePath = Path.Combine(repoRoot!, "tests", "TestData", fileName);
        Skip.IfNot(File.Exists(filePath), $"Test data file not found: {filePath}");

        return (File.ReadAllBytes(filePath), Path.Combine(repoRoot!, "tests", "TestData"));
    }

    private sealed class TestHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}
