using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DocSearch.Api.Infrastructure;

public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteLiveResponseAsync(HttpContext context, HealthReport _)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var payload = new
        {
            status = "Healthy"
        };
        await JsonSerializer.SerializeAsync(context.Response.Body, payload, _jsonOptions);
    }

    public static async Task WriteReadyResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            description = entry.Value.Status == HealthStatus.Healthy ? null : entry.Value.Description,
            durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 2)
        }).ToArray();

        var payload = new
        {
            status = report.Status.ToString(),
            checks
        };

        await JsonSerializer.SerializeAsync(context.Response.Body, payload, _jsonOptions);
    }
}
