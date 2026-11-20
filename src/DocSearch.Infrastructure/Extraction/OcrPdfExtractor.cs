using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Extraction;
using DocSearch.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Extraction;

public sealed class OcrPdfExtractor : IPdfExtractor
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IPdfExtractor _innerExtractor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITextNormalizer _normalizer;
    private readonly ExtractionOptions _options;
    private readonly ILogger<OcrPdfExtractor> _logger;

    public OcrPdfExtractor(
        IPdfExtractor innerExtractor,
        IHttpClientFactory httpClientFactory,
        ITextNormalizer normalizer,
        IOptions<ExtractionOptions> options,
        ILogger<OcrPdfExtractor> logger)
    {
        _innerExtractor = innerExtractor ?? throw new ArgumentNullException(nameof(innerExtractor));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ct.ThrowIfCancellationRequested();

        // 1. Off mode: complete passthrough without memory buffering
        if (_options.EffectiveOcrMode == OcrMode.Off)
        {
            return await _innerExtractor.ExtractAsync(pdf, ct);
        }

        // 2. Buffer stream once for active OCR modes (Defective or Always)
        byte[] pdfBytes;
        using (var memoryStream = new MemoryStream())
        {
            if (pdf.CanSeek)
            {
                pdf.Position = 0;
            }

            await pdf.CopyToAsync(memoryStream, ct);
            pdfBytes = memoryStream.ToArray();
        }

        using var baseStream = new MemoryStream(pdfBytes);
        var basePages = await _innerExtractor.ExtractAsync(baseStream, ct);

        if (basePages == null || basePages.Count == 0)
        {
            return basePages ?? [];
        }

        // 3. Candidate page selection
        List<int> pagesToOcr;
        if (_options.EffectiveOcrMode == OcrMode.Always)
        {
            pagesToOcr = basePages.Select(p => p.PageNumber).OrderBy(p => p).ToList();
        }
        else // Defective mode
        {
            var needsOcrPages = new HashSet<int>();
            var hasDocumentCMapDefect = false;

            foreach (var page in basePages)
            {
                if (page.NeedsOcr)
                {
                    needsOcrPages.Add(page.PageNumber);
                }

                var normalized = _normalizer.NormalizeForDisplay(page.Text);
                var defects = TextDefectDetector.DetectPageDefects(normalized);
                if (defects.HasFlag(ChunkDefectFlags.CorruptLigatureLamAlef))
                {
                    hasDocumentCMapDefect = true;
                    needsOcrPages.Add(page.PageNumber);
                }
            }

            if (hasDocumentCMapDefect)
            {
                // Propagate document-wide to all pages with Persian text
                foreach (var page in basePages)
                {
                    if (ContainsPersian(page.Text))
                    {
                        needsOcrPages.Add(page.PageNumber);
                    }
                }
            }

            pagesToOcr = needsOcrPages.OrderBy(p => p).ToList();
        }

        if (pagesToOcr.Count == 0)
        {
            return basePages;
        }

        // 4. Call /ocr endpoint in batches
        var ocrMap = new Dictionary<int, string>();
        var batchSize = Math.Clamp(_options.OcrBatchSize, 1, 50);
        var httpClient = _httpClientFactory.CreateClient("OcrExtractor");

        for (var i = 0; i < pagesToOcr.Count; i += batchSize)
        {
            var batch = pagesToOcr.Skip(i).Take(batchSize).ToList();
            var batchPagesStr = string.Join(",", batch);

            try
            {
                using var content = new MultipartFormDataContent();
                var pdfByteArrayContent = new ByteArrayContent(pdfBytes);
                pdfByteArrayContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
                content.Add(pdfByteArrayContent, "file", "document.pdf");
                content.Add(new StringContent(batchPagesStr), "pages");
                content.Add(new StringContent(_options.OcrDpi.ToString(CultureInfo.InvariantCulture)), "dpi");
                content.Add(new StringContent(_options.OcrLanguages), "lang");

                HttpResponseMessage response;
                try
                {
                    response = await httpClient.PostAsync("ocr", content, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Rethrow host shutdown or caller cancellation
                    throw;
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    // HTTP client timeout for this batch
                    _logger.LogWarning(ex, "OCR extraction request timed out for batch [{BatchPages}]; aborting remaining OCR batches and falling back to base text.", batchPagesStr);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OCR extraction connection failed for batch [{BatchPages}]; aborting remaining OCR batches and falling back to base text.", batchPagesStr);
                    break;
                }

                using (response)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var stream = await response.Content.ReadAsStreamAsync(ct);
                        var ocrResponse = await JsonSerializer.DeserializeAsync<OcrResponseDto>(stream, _jsonOptions, ct);
                        if (ocrResponse?.Pages != null)
                        {
                            foreach (var p in ocrResponse.Pages)
                            {
                                if (!string.IsNullOrWhiteSpace(p.Text))
                                {
                                    ocrMap[p.Page] = p.Text;
                                }
                            }
                        }
                    }
                    else
                    {
                        var errorBody = await response.Content.ReadAsStringAsync(ct);
                        _logger.LogWarning(
                            "OCR extraction endpoint returned status {StatusCode} for batch [{BatchPages}]: {ErrorDetail}. Aborting remaining OCR batches and falling back to base text.",
                            response.StatusCode,
                            batchPagesStr,
                            errorBody);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error during OCR extraction for batch [{BatchPages}]; aborting remaining OCR batches and falling back to base text.", batchPagesStr);
                break;
            }
        }

        // 5. Merge OCR results into base pages
        var mergedPages = new List<PageText>(basePages.Count);
        foreach (var basePage in basePages)
        {
            if (ocrMap.TryGetValue(basePage.PageNumber, out var ocrText) && !string.IsNullOrWhiteSpace(ocrText))
            {
                var rescored = PageQualityScorer.ScorePage(basePage.PageNumber, ocrText, _options.MinPageQuality);
                mergedPages.Add(rescored with
                {
                    ExtractionSource = "ocr",
                    RawText = basePage.Text
                });
            }
            else
            {
                mergedPages.Add(basePage);
            }
        }

        return mergedPages;
    }

    private static bool ContainsPersian(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if ((c >= 0x0600 && c <= 0x06FF) ||
                (c >= 0x0750 && c <= 0x077F) ||
                (c >= 0xFB50 && c <= 0xFDFF) ||
                (c >= 0xFE70 && c <= 0xFEFF))
            {
                return true;
            }
        }

        return false;
    }

    internal sealed class OcrResponseDto
    {
        public List<OcrPageDto> Pages { get; set; } = [];
    }

    internal sealed class OcrPageDto
    {
        public int Page { get; set; }
        public string Text { get; set; } = "";
    }
}
