using System.Net;
using DocSearch.Application.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocSearch.Infrastructure.Ai.Decorators;

public sealed class RetryingEmbeddingGenerator : DelegatingEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ILogger<RetryingEmbeddingGenerator> _logger;
    private readonly int _maxAttempts;
    private readonly IReadOnlyList<TimeSpan> _backoffs;

    public RetryingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> innerGenerator,
        ILogger<RetryingEmbeddingGenerator> logger,
        int maxAttempts = 3,
        IReadOnlyList<TimeSpan>? backoffs = null)
        : base(innerGenerator)
    {
        _logger = logger;
        _maxAttempts = Math.Max(1, maxAttempts);
        _backoffs = backoffs ??
        [
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(800),
            TimeSpan.FromMilliseconds(3000)
        ];
    }

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await InnerGenerator.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (IsNonTransient(ex) || attempt == _maxAttempts)
                {
                    _logger.LogWarning(
                        "Embedding call failed after attempt {Attempt} of {MaxAttempts}: {ErrorMessage}",
                        attempt,
                        _maxAttempts,
                        ex.Message);

                    if (ex is ExternalServiceUnavailableException)
                    {
                        throw;
                    }

                    throw new ExternalServiceUnavailableException("Embedding", "Embedding service is currently unavailable.", ex);
                }

                var delayIndex = Math.Min(attempt - 1, _backoffs.Count - 1);
                var delay = _backoffs[delayIndex];

                _logger.LogWarning(
                    "Embedding transient failure on attempt {Attempt} of {MaxAttempts}. Retrying in {DelayMs} ms. Error: {ErrorMessage}",
                    attempt,
                    _maxAttempts,
                    delay.TotalMilliseconds,
                    ex.Message);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new ExternalServiceUnavailableException("Embedding", "Embedding service is unavailable after retries.");
    }

    private static bool IsNonTransient(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: { } sc } &&
                (int)sc is >= 400 and < 500 &&
                sc is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
            {
                return true;
            }

            if (e.Message.Contains("context length", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
