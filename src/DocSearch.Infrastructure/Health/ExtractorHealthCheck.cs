using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace DocSearch.Infrastructure.Health;

public sealed class ExtractorHealthCheck : IHealthCheck
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ExtractorHealthCheck> _logger;

    public ExtractorHealthCheck(
        IHttpClientFactory httpClientFactory,
        ILogger<ExtractorHealthCheck> logger)
    {
        _httpClient = httpClientFactory.CreateClient("ExtractorHealthCheck");
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Healthy("Extractor sidecar is reachable.");
            }

            _logger.LogError("Extractor health check returned status {StatusCode}.", response.StatusCode);
            return HealthCheckResult.Unhealthy($"Extractor sidecar returned HTTP {response.StatusCode}.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extractor sidecar readiness health check failed.");
            return HealthCheckResult.Unhealthy("Extractor sidecar connection failed or timed out.");
        }
    }
}
