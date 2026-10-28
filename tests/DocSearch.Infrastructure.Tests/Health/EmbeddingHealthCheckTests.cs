using System.Net;
using DocSearch.Application.Options;
using DocSearch.Infrastructure.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Health;

public sealed class EmbeddingHealthCheckTests
{
    [Fact]
    public async Task HealthCheck_Healthy_When_Model_Installed()
    {
        const string tagsJson = """
            {
              "models": [
                { "name": "bge-m3:latest" },
                { "name": "llama3:latest" }
              ]
            }
            """;

        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(tagsJson, System.Text.Encoding.UTF8, "application/json")
        });

        var factory = new MockHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Ollama",
                Model = "bge-m3",
                Endpoint = "http://127.0.0.1:11434"
            }
        });

        var check = new EmbeddingHealthCheck(factory, options, NullLogger<EmbeddingHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("bge-m3", result.Description);
    }

    [Fact]
    public async Task HealthCheck_Unhealthy_When_Model_Missing()
    {
        const string tagsJson = """
            {
              "models": [
                { "name": "llama3:latest" }
              ]
            }
            """;

        var handler = new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(tagsJson, System.Text.Encoding.UTF8, "application/json")
        });

        var factory = new MockHttpClientFactory(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Ollama",
                Model = "bge-m3",
                Endpoint = "http://127.0.0.1:11434"
            }
        });

        var check = new EmbeddingHealthCheck(factory, options, NullLogger<EmbeddingHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not installed", result.Description);
    }

    [Fact]
    public async Task HealthCheck_Unhealthy_When_Endpoint_Unreachable()
    {
        var handler = new MockHttpMessageHandler(_ => throw new HttpRequestException("Connection refused"));
        var factory = new MockHttpClientFactory(handler);

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Ollama",
                Model = "bge-m3",
                Endpoint = "http://127.0.0.1:11434"
            }
        });

        var check = new EmbeddingHealthCheck(factory, options, NullLogger<EmbeddingHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unreachable", result.Description);
    }

    [Fact]
    public async Task HealthCheck_Times_Out_After_3_Seconds()
    {
        var handler = new MockHttpMessageHandler(async (ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var factory = new MockHttpClientFactory(handler);

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Ollama",
                Model = "bge-m3",
                Endpoint = "http://127.0.0.1:11434"
            }
        });

        var check = new EmbeddingHealthCheck(factory, options, NullLogger<EmbeddingHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("timed out", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthCheck_Does_Not_Expose_Secrets()
    {
        var handler = new MockHttpMessageHandler(_ => throw new HttpRequestException("Failed with secret-token in url"));
        var factory = new MockHttpClientFactory(handler);

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Ollama",
                Model = "bge-m3",
                Endpoint = "http://127.0.0.1:11434",
                ApiKey = "secret-super-key"
            }
        });

        var check = new EmbeddingHealthCheck(factory, options, NullLogger<EmbeddingHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.DoesNotContain("secret-super-key", result.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthCheck_Healthy_When_Fake_Provider()
    {
        var factory = new MockHttpClientFactory(new MockHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake"
            }
        });

        var check = new EmbeddingHealthCheck(factory, options, NullLogger<EmbeddingHealthCheck>.Instance);
        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("Fake", result.Description);
    }

    private sealed class MockHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/")
        };
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _sendFunc;

        public MockHttpMessageHandler(HttpResponseMessage response)
        {
            _sendFunc = _ => Task.FromResult(response);
        }

        public MockHttpMessageHandler(Func<CancellationToken, Task<HttpResponseMessage>> sendFunc)
        {
            _sendFunc = sendFunc;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _sendFunc(cancellationToken);
        }
    }
}
