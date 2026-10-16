using System.Net;
using System.Text;
using System.Text.Json;
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
    }

    [Fact]
    public async Task ExtractAsync_PersianSample_MatchesInvariantsAndPhrase()
    {
        var (sampleBytes, testDataDir) = FindTestDataFile("persian-sample.pdf");
        Assert.NotEmpty(sampleBytes);

        // Simulated PyMuPDF extraction matching page 1 & 2 of persian-sample.pdf
        var p1Text = "آﯾﯿﻦ ﻧﺎﻣﻪ آﻣﻮزﺷﯽ \n دوره ﻫﺎي\nﮐﺎرﺷﻨﺎﺳﯽ   \n ورودي ﻫﺎي91\n و ﺑﻌﺪ از آن  \nﻣﻘﺪﻣﻪ  \n رﺷﺪ روزاﻓﺰون ﭘﯿﺸﺮﻓﺖ ﻫﺎي ﻋﻠﻤﯽ در ﺟﻬﺎن اﻣﺮوز ﻫﻤﺮاه ﺑﺎ ﺗﺤﻮﻻت ﺷﮕﺮف ﺻﻮرت ﮔﺮﻓﺘﻪ\n در آن، ﺿﺮورت ﺗﻮﺟﻪ ﺑﻪ ﺑﺮﻧﺎﻣﻪ رﯾﺰي دﻗﯿﻖ، اﺳﺎﺳﯽ و ﻣﻨﻄﺒﻖ ﺑﺎ واﻗﻌﯿﺖ ﻫﺎي ﻣﻮﺟﻮد و\nﺳﻨﺪ ﭼﺸﻢ اﻧﺪاز ﺑﯿﺴﺖ ﺳﺎﻟﻪ ﺟﻤﻬﻮري اﺳﻼﻣﯽ اﯾﺮان را ﺑﯿﺶ از ﭘﯿﺶ روﺷﻦ ﺳﺎﺧﺘﻪ اﺳﺖ .";
        var p2Text = "فصل اول : کلیات و تعاریف \nماده 1: هدف \nهدف از تدوین این آیین نامه تعیین ضوابط و مقررات آموزشی دوره کارشناسی در دانشگاه ها و موسسات آموزش عالی است.";

        var jsonResponse = JsonSerializer.Serialize(new
        {
            pages = new[]
            {
                new { page = 1, text = p1Text },
                new { page = 2, text = p2Text }
            }
        });

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        var extractor = CreateExtractor(handler);
        using var stream = new MemoryStream(sampleBytes);

        var pages = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Equal(2, pages.Count);
        Assert.False(pages[0].NeedsOcr, "Persian digital page 1 should not require OCR.");
        Assert.False(pages[1].NeedsOcr, "Persian digital page 2 should not require OCR.");

        // NormalizeForDisplay preserves ZWNJ; replace ZWNJ with space and collapse whitespace to assert multi-line title phrase
        var displayNormalized = System.Text.RegularExpressions.Regex.Replace(
            _normalizer.NormalizeForDisplay(pages[0].Text).Replace('\u200C', ' '),
            @"\s+",
            " ");
        Assert.Contains("آیین نامه آموزشی دوره های کارشناسی", displayNormalized);

        // NormalizeForSearch also resolves it cleanly
        var searchNormalized = System.Text.RegularExpressions.Regex.Replace(
            _normalizer.NormalizeForSearch(pages[0].Text),
            @"\s+",
            " ");
        Assert.Contains("آیین نامه آموزشی دوره های کارشناسی", searchNormalized);
    }

    [Fact]
    public async Task ExtractAsync_EnglishSample_MatchesInvariants()
    {
        var (sampleBytes, _) = FindTestDataFile("english-sample.pdf");
        Assert.NotEmpty(sampleBytes);

        var englishText = "Retrieval-Augmented Generation for\nKnowledge-Intensive NLP Tasks\nPatrick Lewis, Ethan Perez, Aleksandra Piktus\nWe explore retrieval-augmented models.";

        var jsonResponse = JsonSerializer.Serialize(new
        {
            pages = new[]
            {
                new { page = 1, text = englishText }
            }
        });

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
        });

        var extractor = CreateExtractor(handler);
        using var stream = new MemoryStream(sampleBytes);

        var pages = await extractor.ExtractAsync(stream, CancellationToken.None);

        Assert.Single(pages);
        Assert.False(pages[0].NeedsOcr);
        Assert.Contains("Retrieval-Augmented Generation", pages[0].Text);
    }

    [Fact]
    public async Task ExtractAsync_ScannedSample_RoutesToOcr()
    {
        var (sampleBytes, _) = FindTestDataFile("scanned-sample.pdf");
        Assert.NotEmpty(sampleBytes);

        // Scanned documents yield empty or near-empty text (< 20 chars)
        var jsonResponse = JsonSerializer.Serialize(new
        {
            pages = new[]
            {
                new { page = 1, text = "" },
                new { page = 2, text = "  \n  " }
            }
        });

        var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json")
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

    private static (byte[] Bytes, string Dir) FindTestDataFile(string fileName)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DocSearch.slnx")) && !File.Exists(Path.Combine(dir.FullName, "DocSearch.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException("Could not locate repository root.");
        }

        var filePath = Path.Combine(dir.FullName, "tests", "TestData", fileName);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Test data file not found: {filePath}");
        }

        return (File.ReadAllBytes(filePath), Path.Combine(dir.FullName, "tests", "TestData"));
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
