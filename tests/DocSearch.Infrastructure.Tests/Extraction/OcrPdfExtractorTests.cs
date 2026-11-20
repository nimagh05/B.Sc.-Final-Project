using System.Net;
using System.Text;
using System.Text.Json;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using DocSearch.Infrastructure.Extraction;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Extraction;

public sealed class OcrPdfExtractorTests
{
    private readonly ITextNormalizer _normalizer = new PersianTextNormalizer();

    [Fact]
    public async Task OcrPdfExtractor_Calls_Ocr_Only_For_Flagged_Pages_And_Merges_Text()
    {
        var basePages = new List<PageText>
        {
            new(1, "متن تمیز صفحه اول بدون مشکل.", 1.0, false),
            new(2, "", 0.0, true), // Scanned page needing OCR
            new(3, "متن تمیز صفحه سوم بدون مشکل.", 1.0, false)
        };

        var innerExtractor = new StubExtractor(basePages);
        string? capturedContent = null;

        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            capturedContent = await request.Content!.ReadAsStringAsync(ct);

            var ocrResponse = new
            {
                pages = new[]
                {
                    new { page = 2, text = "متن صفحه ۲ که با او سی آر استخراج شد" }
                }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(ocrResponse), Encoding.UTF8, "application/json")
            };
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Defective,
            OcrDpi = 300,
            OcrLanguages = "fas+eng"
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        var result = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.NotNull(capturedContent);
        Assert.Contains("pages", capturedContent);
        Assert.Contains("2", capturedContent);

        Assert.Equal(3, result.Count);
        Assert.Equal("text_layer", result[0].ExtractionSource);
        Assert.False(result[0].IsOcr);

        // Page 2 merged from OCR
        Assert.Equal("ocr", result[1].ExtractionSource);
        Assert.True(result[1].IsOcr);
        Assert.Equal("متن صفحه ۲ که با او سی آر استخراج شد", result[1].Text);
        Assert.Equal("", result[1].RawText);
        Assert.True(result[1].Quality > 0.5);

        Assert.Equal("text_layer", result[2].ExtractionSource);
        Assert.False(result[2].IsOcr);
    }

    [Fact]
    public async Task OcrPdfExtractor_Propagates_CMap_DocumentWide()
    {
        // Construct page 1 with CMap defect: length >= 300 chars, al >= 10, la == 0
        var defectivePageText = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            defectivePageText.Append("الگو و اسناد دانشگاه البرز و امور اداری ");
        }

        var basePages = new List<PageText>
        {
            new(1, defectivePageText.ToString(), 1.0, false),
            new(2, "صفحه دوم حاوی متن فارسی معمولی است.", 1.0, false)
        };

        var innerExtractor = new StubExtractor(basePages);
        string? requestedPages = null;

        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            var content = await request.Content!.ReadAsStringAsync(ct);
            requestedPages = content;

            var ocrResponse = new
            {
                pages = new[]
                {
                    new { page = 1, text = "متن اصلاح شده ۱" },
                    new { page = 2, text = "متن اصلاح شده ۲" }
                }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(ocrResponse), Encoding.UTF8, "application/json")
            };
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Defective
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        var result = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.NotNull(requestedPages);
        // Both pages should be requested due to document-wide CMap propagation
        Assert.Contains("1,2", requestedPages);
        Assert.Equal("ocr", result[0].ExtractionSource);
        Assert.Equal("ocr", result[1].ExtractionSource);
    }

    [Fact]
    public async Task OcrPdfExtractor_Skips_Ocr_When_Mode_Is_Off()
    {
        var basePages = new List<PageText>
        {
            new(1, "", 0.0, true) // Needs OCR
        };

        var innerExtractor = new StubExtractor(basePages);
        var callCount = 0;

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Off
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        var result = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.Equal(0, callCount);
        Assert.Single(result);
        Assert.Equal("text_layer", result[0].ExtractionSource);
    }

    [Fact]
    public async Task OcrPdfExtractor_Skips_Ocr_When_No_Pages_Flagged_In_Defective_Mode()
    {
        var basePages = new List<PageText>
        {
            new(1, "متن سالم و استاندارد بدون هیچ نقصی.", 1.0, false),
            new(2, "صفحه دوم سالم و استاندارد.", 1.0, false)
        };

        var innerExtractor = new StubExtractor(basePages);
        var callCount = 0;

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            callCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Defective
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        var result = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.Equal(0, callCount);
        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.Equal("text_layer", p.ExtractionSource));
    }

    [Fact]
    public async Task OcrPdfExtractor_Degrades_Gracefully_When_Ocr_Fails()
    {
        var basePages = new List<PageText>
        {
            new(1, "متن اولیه", 0.0, true)
        };

        var innerExtractor = new StubExtractor(basePages);

        var handler = new MockHttpMessageHandler((_, _) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"detail\":\"Tesseract not found\"}")
            });
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Defective
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        var result = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        // Fallback to base text without throwing
        Assert.Single(result);
        Assert.Equal("text_layer", result[0].ExtractionSource);
        Assert.Equal("متن اولیه", result[0].Text);
    }

    [Fact]
    public async Task OcrPdfExtractor_Rethrows_When_Cancellation_Requested()
    {
        var basePages = new List<PageText>
        {
            new(1, "", 0.0, true)
        };

        var innerExtractor = new StubExtractor(basePages);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new MockHttpMessageHandler((_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Defective
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(pdfStream, cts.Token));
    }

    [Fact]
    public async Task OcrPdfExtractor_Handles_NonSeekable_Stream()
    {
        var basePages = new List<PageText>
        {
            new(1, "متن تمیز", 1.0, false)
        };

        var innerExtractor = new StubExtractor(basePages);
        var factory = new StubHttpClientFactory(new MockHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Defective
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var nonSeekable = new NonSeekableStream(new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample")));
        var result = await extractor.ExtractAsync(nonSeekable, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("متن تمیز", result[0].Text);
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrModeIsAlways_SendsAllPagesToOcr()
    {
        var basePages = new List<PageText>
        {
            new(1, "متن صفحه ۱ تمیز", 1.0, false),
            new(2, "متن صفحه ۲ تمیز", 1.0, false),
            new(3, "متن صفحه ۳ تمیز", 1.0, false)
        };

        var innerExtractor = new StubExtractor(basePages);
        string? capturedContent = null;

        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            capturedContent = await request.Content!.ReadAsStringAsync(ct);

            var ocrResponse = new
            {
                pages = new[]
                {
                    new { page = 1, text = "متن ocr صفحه ۱" },
                    new { page = 2, text = "متن ocr صفحه ۲" },
                    new { page = 3, text = "متن ocr صفحه ۳" }
                }
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(ocrResponse), Encoding.UTF8, "application/json")
            };
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Always
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        var result = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.NotNull(capturedContent);
        Assert.Contains("1,2,3", capturedContent);
        Assert.Equal(3, result.Count);
        Assert.All(result, p =>
        {
            Assert.Equal("ocr", p.ExtractionSource);
            Assert.True(p.IsOcr);
        });
    }

    [Fact]
    public async Task ExtractAsync_WhenOcrTimesOut_WithoutCallerCancellation_FallsBackGracefullyWithWarning()
    {
        var basePages = new List<PageText>
        {
            new(1, "متن پایه صفحه ۱", 0.0, true) // Needs OCR
        };

        var innerExtractor = new StubExtractor(basePages);

        var handler = new MockHttpMessageHandler((_, ct) =>
        {
            // Simulate HttpClient timeout: TaskCanceledException when caller has NOT canceled
            throw new TaskCanceledException("HttpClient timeout while processing OCR");
        });

        var factory = new StubHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new ExtractionOptions
        {
            OcrMode = OcrMode.Defective
        });

        var extractor = new OcrPdfExtractor(innerExtractor, factory, _normalizer, options, NullLogger<OcrPdfExtractor>.Instance);

        using var pdfStream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 sample"));
        // Caller cancellation token is NOT canceled
        var result = await extractor.ExtractAsync(pdfStream, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("text_layer", result[0].ExtractionSource);
        Assert.False(result[0].IsOcr);
        Assert.Equal("متن پایه صفحه ۱", result[0].Text);
    }

    private sealed class StubExtractor(IReadOnlyList<PageText> pages) : IPdfExtractor
    {
        public Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct) => Task.FromResult(pages);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("http://localhost:8000/") };
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            sendAsync(request, cancellationToken);
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
