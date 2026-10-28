using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OllamaSharp.Models;

namespace DocSearch.Infrastructure.Ai.Ollama;

public sealed class OllamaEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly IOllamaApiClient _client;
    private readonly AiEmbeddingOptions _options;
    private readonly ILogger<OllamaEmbeddingGenerator> _logger;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public OllamaEmbeddingGenerator(
        IOllamaApiClient client,
        IOptions<AiOptions> aiOptions,
        ILogger<OllamaEmbeddingGenerator> logger)
    {
        _client = client;
        _options = aiOptions.Value.Embedding;
        _logger = logger;
        _metadata = new EmbeddingGeneratorMetadata(
            nameof(OllamaEmbeddingGenerator),
            new Uri(_options.Endpoint),
            _options.Model,
            _options.Dimensions);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        cancellationToken.ThrowIfCancellationRequested();

        var inputList = values.ToList();
        if (inputList.Count == 0)
        {
            return new GeneratedEmbeddings<Embedding<float>>();
        }

        var model = options?.ModelId ?? _options.Model;

        var request = new EmbedRequest
        {
            Model = model,
            Input = inputList,
            Truncate = _options.Truncate,
            KeepAlive = _options.KeepAlive,
            Options = new RequestOptions
            {
                NumThread = _options.NumThread
            }
        };

        EmbedResponse response;
        try
        {
            response = await _client.EmbedAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Ollama embedding call failed: {ErrorMessage}", ex.Message);
            throw new ExternalServiceUnavailableException("Embedding", "Embedding service is currently unavailable.", ex);
        }

        if (response?.Embeddings == null)
        {
            throw new ExternalServiceUnavailableException("Embedding", "Embedding service returned empty response.");
        }

        var embeddings = response.Embeddings
            .Select(vector => new Embedding<float>(vector))
            .ToList();

        return new GeneratedEmbeddings<Embedding<float>>(embeddings);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return _metadata;
        }

        if (serviceType.IsInstanceOfType(this))
        {
            return this;
        }

        return null;
    }

    public void Dispose()
    {
    }
}
