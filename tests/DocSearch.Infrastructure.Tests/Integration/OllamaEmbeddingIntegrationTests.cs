using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Ai.Decorators;
using DocSearch.Infrastructure.Ai.Ollama;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Integration;

[Trait("Category", "RequiresOllama")]
public sealed class OllamaEmbeddingIntegrationTests : IAsyncLifetime, IDisposable
{
    private const string _endpoint = "http://127.0.0.1:11434";
    private const string _model = "bge-m3";
    private readonly OllamaApiClient _client;
    private readonly OllamaEmbeddingGenerator _generator;

    public OllamaEmbeddingIntegrationTests()
    {
        _client = new OllamaApiClient(new Uri(_endpoint), _model);

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Ollama",
                Model = _model,
                Endpoint = _endpoint,
                Dimensions = EmbeddingConstants.VectorDimensions,
                BatchSize = 4,
                NumThread = 3,
                KeepAlive = "5m",
                Truncate = false,
                TimeoutSeconds = 120
            }
        });

        _generator = new OllamaEmbeddingGenerator(_client, options, NullLogger<OllamaEmbeddingGenerator>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Unload model to preserve host RAM per Adjustment 7
        try
        {
            await _client.EmbedAsync(new EmbedRequest
            {
                Model = _model,
                Input = ["unload"],
                KeepAlive = "0"
            });
        }
        catch
        {
            // Ignore unload errors during teardown
        }
    }

    public void Dispose()
    {
        _generator.Dispose();
        _client.Dispose();
    }

    [SkippableFact]
    public async Task Ollama_Returns_1024_Dims_For_Persian_Text()
    {
        await EnsureOllamaAvailableAsync();

        const string persianText = "آیین‌نامه آموزشی دانشگاه تهران برای مقطع کارشناسی";

        var result = await _generator.GenerateAsync([persianText]);

        Assert.Single(result);
        var embedding = result[0];
        Assert.Equal(EmbeddingConstants.VectorDimensions, embedding.Vector.Length);

        var vec = embedding.Vector.ToArray();
        double sumSq = 0.0;
        foreach (var v in vec)
        {
            sumSq += (double)v * v;
        }

        var norm = Math.Sqrt(sumSq);
        Assert.True(Math.Abs(norm - 1.0) < 1e-4, $"Expected L2 norm ~1.0, got {norm}");
        Assert.Contains(vec, x => Math.Abs(x) > 0.001f);
    }

    [SkippableFact]
    public async Task Ollama_Truncate_False_Throws_On_Overlength_Input()
    {
        await EnsureOllamaAvailableAsync();

        using var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(_endpoint + "/"),
            Timeout = TimeSpan.FromSeconds(120)
        };
        using var client = new OllamaApiClient(httpClient, _model);

        var options = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Ollama",
                Model = _model,
                Endpoint = _endpoint,
                Dimensions = EmbeddingConstants.VectorDimensions,
                BatchSize = 4,
                NumThread = 3,
                KeepAlive = "0",
                Truncate = false,
                TimeoutSeconds = 120
            }
        });

        using var inner = new OllamaEmbeddingGenerator(client, options, NullLogger<OllamaEmbeddingGenerator>.Instance);
        using var retrying = new RetryingEmbeddingGenerator(inner, NullLogger<RetryingEmbeddingGenerator>.Instance);

        // Generate an over-length input exceeding bge-m3 context length (8192 tokens)
        var giantText = string.Join(" ", Enumerable.Repeat("دانشگاه", 15000));

        var ex = await Assert.ThrowsAsync<ExternalServiceUnavailableException>(() =>
            retrying.GenerateAsync([giantText]));

        Assert.Contains("unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ex.InnerException);
        Assert.Equal(1, handler.Count);
    }

    private async Task EnsureOllamaAvailableAsync()
    {
        bool hasModel;
        try
        {
            var tags = await _client.ListLocalModelsAsync();
            hasModel = tags.Any(m =>
                string.Equals(m.Name, _model, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name, $"{_model}:latest", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new SkipException($"Ollama is unreachable at {_endpoint}: {ex.Message}");
        }

        Skip.IfNot(hasModel, $"Ollama is reachable at {_endpoint}, but model '{_model}' is not pulled.");
    }

    private sealed class CountingHandler : HttpClientHandler
    {
        private int _count;

        public int Count => _count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
