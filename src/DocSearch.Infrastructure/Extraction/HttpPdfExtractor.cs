using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Extraction;
using DocSearch.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Extraction;

public sealed class HttpPdfExtractor : IPdfExtractor
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly ExtractionOptions _options;
    private readonly ILogger<HttpPdfExtractor> _logger;

    public HttpPdfExtractor(
        HttpClient httpClient,
        IOptions<ExtractionOptions> options,
        ILogger<HttpPdfExtractor> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        byte[] pdfBytes;
        if (pdf is MemoryStream ms && ms.TryGetBuffer(out var segment))
        {
            pdfBytes = segment.ToArray();
        }
        else
        {
            using var buffer = new MemoryStream();
            if (pdf.CanSeek)
            {
                pdf.Position = 0;
            }

            await pdf.CopyToAsync(buffer, ct);
            pdfBytes = buffer.ToArray();
        }

        const int maxAttempts = 3;
        var delays = new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(800) };

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var content = new MultipartFormDataContent();
            var byteArrayContent = new ByteArrayContent(pdfBytes);
            byteArrayContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            content.Add(byteArrayContent, "file", "document.pdf");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync("extract", content, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HTTP call to extractor failed on attempt {Attempt}/{MaxAttempts}.", attempt, maxAttempts);

                if (attempt == maxAttempts)
                {
                    throw new ExternalServiceUnavailableException(
                        "PdfExtractor",
                        $"Extractor sidecar connection failed after {maxAttempts} attempts: {ex.Message}",
                        ex);
                }

                await Task.Delay(delays[attempt - 1], ct);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var responseStream = await response.Content.ReadAsStreamAsync(ct);
                    var payload = await JsonSerializer.DeserializeAsync<ExtractorResponseDto>(responseStream, _jsonOptions, ct);

                    if (payload?.Pages == null || payload.Pages.Count == 0)
                    {
                        return [];
                    }

                    var results = new List<PageText>(payload.Pages.Count);
                    foreach (var page in payload.Pages)
                    {
                        var pageText = PageQualityScorer.ScorePage(page.Page, page.Text, _options.MinPageQuality);
                        results.Add(pageText);
                    }

                    return results;
                }

                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                {
                    throw new FileTooLargeException("The uploaded PDF file exceeds the maximum allowed extraction size limit.");
                }

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
                {
                    var errorDetail = await response.Content.ReadAsStringAsync(ct);
                    throw new InvalidFileException($"The provided PDF is invalid or corrupt: {errorDetail}");
                }

                // If 5xx transient server error, retry
                if ((int)response.StatusCode >= 500)
                {
                    _logger.LogWarning(
                        "Extractor returned status {StatusCode} on attempt {Attempt}/{MaxAttempts}.",
                        response.StatusCode,
                        attempt,
                        maxAttempts);

                    if (attempt == maxAttempts)
                    {
                        throw new ExternalServiceUnavailableException(
                            "PdfExtractor",
                            $"Extractor returned HTTP {response.StatusCode} after {maxAttempts} attempts.");
                    }

                    await Task.Delay(delays[attempt - 1], ct);
                    continue;
                }

                // Any unexpected non-transient status
                var detail = await response.Content.ReadAsStringAsync(ct);
                throw new ExternalServiceUnavailableException(
                    "PdfExtractor",
                    $"Extractor returned unexpected status {response.StatusCode}: {detail}");
            }
        }

        throw new ExternalServiceUnavailableException(
            "PdfExtractor",
            $"Extractor sidecar failed after {maxAttempts} attempts.");
    }

    private sealed class ExtractorResponseDto
    {
        public List<ExtractorPageDto> Pages { get; set; } = [];
    }

    private sealed class ExtractorPageDto
    {
        public int Page { get; set; }
        public string Text { get; set; } = "";
    }
}
