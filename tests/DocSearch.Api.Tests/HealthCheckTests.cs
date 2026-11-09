using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace DocSearch.Api.Tests;

public sealed class HealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public HealthCheckTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Ai:Embedding:Provider"] = "Fake",
                    ["SkipStartupDbSeeding"] = "true"
                });
            });
            builder.ConfigureServices(services =>
            {
                var hostedServices = services.Where(s => s.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)).ToList();
                foreach (var hs in hostedServices)
                {
                    services.Remove(hs);
                }
            });
        });
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task HealthLive_Endpoint_Returns_200_Healthy()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Healthy", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task HealthReady_When_Db_And_Extractor_Healthy_Returns_200_And_No_Secrets()
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    var existingDb = options.Registrations.FirstOrDefault(r => r.Name == "database");
                    if (existingDb != null)
                    {
                        options.Registrations.Remove(existingDb);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Database is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingExtractor = options.Registrations.FirstOrDefault(r => r.Name == "extractor");
                    if (existingExtractor != null)
                    {
                        options.Registrations.Remove(existingExtractor);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "extractor",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Extractor sidecar is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingEmbedding = options.Registrations.FirstOrDefault(r => r.Name == "embedding");
                    if (existingEmbedding != null)
                    {
                        options.Registrations.Remove(existingEmbedding);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "embedding",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Embedding service is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));
                });
            });
        });

        var client = customFactory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var rawBody = await response.Content.ReadAsStringAsync();

        // Never leak credentials or sensitive connection strings
        Assert.DoesNotContain("password", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", rawBody, StringComparison.OrdinalIgnoreCase);

        using var json = JsonDocument.Parse(rawBody);
        var root = json.RootElement;
        Assert.Equal("Healthy", root.GetProperty("status").GetString());

        var checks = root.GetProperty("checks");
        Assert.True(checks.GetArrayLength() > 0);

        var dbCheck = checks.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == "database");
        Assert.Equal("Healthy", dbCheck.GetProperty("status").GetString());
        Assert.True(dbCheck.TryGetProperty("durationMs", out _));

        var extractorCheck = checks.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == "extractor");
        Assert.Equal("Healthy", extractorCheck.GetProperty("status").GetString());
        Assert.True(extractorCheck.TryGetProperty("durationMs", out _));

        var embeddingCheck = checks.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == "embedding");
        Assert.Equal("Healthy", embeddingCheck.GetProperty("status").GetString());
        Assert.True(embeddingCheck.TryGetProperty("durationMs", out _));
    }

    [Fact]
    public async Task HealthReady_When_Db_Fails_Returns_503_And_No_Secrets()
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    var existingDb = options.Registrations.FirstOrDefault(r => r.Name == "database");
                    if (existingDb != null)
                    {
                        options.Registrations.Remove(existingDb);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Unhealthy("Simulated DB connection failure")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingExtractor = options.Registrations.FirstOrDefault(r => r.Name == "extractor");
                    if (existingExtractor != null)
                    {
                        options.Registrations.Remove(existingExtractor);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "extractor",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Extractor sidecar is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingEmbedding = options.Registrations.FirstOrDefault(r => r.Name == "embedding");
                    if (existingEmbedding != null)
                    {
                        options.Registrations.Remove(existingEmbedding);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "embedding",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Embedding service is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));
                });
            });
        });

        var client = customFactory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var rawBody = await response.Content.ReadAsStringAsync();

        // Never leak credentials or sensitive connection strings
        Assert.DoesNotContain("password", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", rawBody, StringComparison.OrdinalIgnoreCase);

        using var json = JsonDocument.Parse(rawBody);
        var root = json.RootElement;
        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());

        var checks = root.GetProperty("checks");
        var dbCheck = checks.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == "database");
        Assert.Equal("Unhealthy", dbCheck.GetProperty("status").GetString());
        Assert.Equal("Simulated DB connection failure", dbCheck.GetProperty("description").GetString());
    }

    [Fact]
    public async Task HealthReady_When_Extractor_Fails_Returns_503_And_No_Secrets()
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    var existingDb = options.Registrations.FirstOrDefault(r => r.Name == "database");
                    if (existingDb != null)
                    {
                        options.Registrations.Remove(existingDb);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Database is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingExtractor = options.Registrations.FirstOrDefault(r => r.Name == "extractor");
                    if (existingExtractor != null)
                    {
                        options.Registrations.Remove(existingExtractor);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "extractor",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Unhealthy("Simulated extractor sidecar failure")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingEmbedding = options.Registrations.FirstOrDefault(r => r.Name == "embedding");
                    if (existingEmbedding != null)
                    {
                        options.Registrations.Remove(existingEmbedding);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "embedding",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Embedding service is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));
                });
            });
        });

        var client = customFactory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var rawBody = await response.Content.ReadAsStringAsync();

        // Never leak credentials or sensitive connection strings
        Assert.DoesNotContain("password", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", rawBody, StringComparison.OrdinalIgnoreCase);

        using var json = JsonDocument.Parse(rawBody);
        var root = json.RootElement;
        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());

        var checks = root.GetProperty("checks");
        var extractorCheck = checks.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == "extractor");
        Assert.Equal("Unhealthy", extractorCheck.GetProperty("status").GetString());
        Assert.Equal("Simulated extractor sidecar failure", extractorCheck.GetProperty("description").GetString());
    }

    [Fact]
    public async Task HealthReady_When_Embedding_Fails_Returns_503_And_No_Secrets()
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    var existingDb = options.Registrations.FirstOrDefault(r => r.Name == "database");
                    if (existingDb != null)
                    {
                        options.Registrations.Remove(existingDb);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Database is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingExtractor = options.Registrations.FirstOrDefault(r => r.Name == "extractor");
                    if (existingExtractor != null)
                    {
                        options.Registrations.Remove(existingExtractor);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "extractor",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Extractor sidecar is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingEmbedding = options.Registrations.FirstOrDefault(r => r.Name == "embedding");
                    if (existingEmbedding != null)
                    {
                        options.Registrations.Remove(existingEmbedding);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "embedding",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Unhealthy("Simulated embedding endpoint failure")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));
                });
            });
        });

        var client = customFactory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var rawBody = await response.Content.ReadAsStringAsync();

        // Never leak credentials or sensitive connection strings
        Assert.DoesNotContain("password", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", rawBody, StringComparison.OrdinalIgnoreCase);

        using var json = JsonDocument.Parse(rawBody);
        var root = json.RootElement;
        Assert.Equal("Unhealthy", root.GetProperty("status").GetString());

        var checks = root.GetProperty("checks");
        var embeddingCheck = checks.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == "embedding");
        Assert.Equal("Unhealthy", embeddingCheck.GetProperty("status").GetString());
        Assert.Equal("Simulated embedding endpoint failure", embeddingCheck.GetProperty("description").GetString());
    }

    [Fact]
    public async Task HealthReady_When_Embedding_Provider_Is_Fake_Returns_200_With_Real_EmbeddingHealthCheck()
    {
        var customFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    var existingDb = options.Registrations.FirstOrDefault(r => r.Name == "database");
                    if (existingDb != null)
                    {
                        options.Registrations.Remove(existingDb);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "database",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Database is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    var existingExtractor = options.Registrations.FirstOrDefault(r => r.Name == "extractor");
                    if (existingExtractor != null)
                    {
                        options.Registrations.Remove(existingExtractor);
                    }
                    options.Registrations.Add(new HealthCheckRegistration(
                        "extractor",
                        _ => new DelegateHealthCheck(() => HealthCheckResult.Healthy("Extractor sidecar is responsive.")),
                        failureStatus: HealthStatus.Unhealthy,
                        tags: ["ready"]));

                    // Notice: "embedding" check is NOT replaced, running real EmbeddingHealthCheck with Provider = "Fake"
                });
            });
        });

        var client = customFactory.CreateClient();
        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal("Healthy", root.GetProperty("status").GetString());

        var checks = root.GetProperty("checks");
        var embeddingCheck = checks.EnumerateArray().FirstOrDefault(c => c.GetProperty("name").GetString() == "embedding");
        Assert.Equal("Healthy", embeddingCheck.GetProperty("status").GetString());
        Assert.True(embeddingCheck.TryGetProperty("durationMs", out _));
    }

    private sealed class DelegateHealthCheck(Func<HealthCheckResult> check) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(check());
        }
    }
}
