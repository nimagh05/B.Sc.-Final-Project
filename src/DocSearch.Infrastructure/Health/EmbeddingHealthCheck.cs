using System.Text.Json;
using DocSearch.Application.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Health;

public sealed class EmbeddingHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiEmbeddingOptions _options;
    private readonly ILogger<EmbeddingHealthCheck> _logger;

    public EmbeddingHealthCheck(
        IHttpClientFactory httpClientFactory,
        IOptions<AiOptions> aiOptions,
        ILogger<EmbeddingHealthCheck> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = aiOptions.Value.Embedding;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var provider = _options.Provider;

        if (provider.Equals("Fake", StringComparison.OrdinalIgnoreCase))
        {
            return HealthCheckResult.Healthy("Fake embedding provider is active.");
        }

        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                var client = _httpClientFactory.CreateClient("EmbeddingHealthCheck");
                using var response = await client.GetAsync("api/tags", timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Ollama health check returned status code {StatusCode}.", response.StatusCode);
                    return HealthCheckResult.Unhealthy($"Ollama returned HTTP {(int)response.StatusCode}.");
                }

                var content = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);

                var configuredModel = _options.Model;
                var latestTag = $"{configuredModel}:latest";

                var modelFound = false;
                if (doc.RootElement.TryGetProperty("models", out var modelsElement) &&
                    modelsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var modelItem in modelsElement.EnumerateArray())
                    {
                        if (modelItem.TryGetProperty("name", out var nameElement))
                        {
                            var name = nameElement.GetString();
                            if (string.Equals(name, configuredModel, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(name, latestTag, StringComparison.OrdinalIgnoreCase))
                            {
                                modelFound = true;
                                break;
                            }
                        }
                    }
                }

                if (modelFound)
                {
                    return HealthCheckResult.Healthy($"Ollama is reachable and embedding model '{configuredModel}' is available.");
                }

                _logger.LogWarning("Configured embedding model '{Model}' not found in Ollama tags.", configuredModel);
                return HealthCheckResult.Unhealthy($"Configured embedding model '{configuredModel}' is not installed in Ollama.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError("Embedding health check timed out after 3 seconds.");
                return HealthCheckResult.Unhealthy("Embedding endpoint timed out after 3 seconds.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding health check connection failed.");
                return HealthCheckResult.Unhealthy("Embedding endpoint is unreachable.");
            }
        }

        if (provider.Equals("OpenAICompatible", StringComparison.OrdinalIgnoreCase))
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                var client = _httpClientFactory.CreateClient("EmbeddingHealthCheck");
                using var request = new HttpRequestMessage(HttpMethod.Head, "");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

                return HealthCheckResult.Healthy("OpenAICompatible embedding endpoint is reachable.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return HealthCheckResult.Unhealthy("Embedding endpoint timed out after 3 seconds.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenAICompatible health check failed.");
                return HealthCheckResult.Unhealthy("Embedding endpoint is unreachable.");
            }
        }

        return HealthCheckResult.Unhealthy($"Unknown embedding provider '{provider}'.");
    }
}
