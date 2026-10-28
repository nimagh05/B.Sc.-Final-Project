using System.Text;
using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Domain.Constants;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Ai.Fakes;

public sealed partial class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly ITextNormalizer _normalizer;
    private readonly int _dimensions;
    private readonly EmbeddingGeneratorMetadata _metadata;

    public FakeEmbeddingGenerator(ITextNormalizer normalizer, IOptions<AiOptions>? aiOptions = null)
    {
        _normalizer = normalizer;
        _dimensions = aiOptions?.Value.Embedding.Dimensions ?? EmbeddingConstants.VectorDimensions;
        _metadata = new EmbeddingGeneratorMetadata(
            nameof(FakeEmbeddingGenerator),
            new Uri("fake://localhost"),
            aiOptions?.Value.Embedding.Model ?? "fake-embedding-model",
            _dimensions);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var embeddings = new List<Embedding<float>>();

        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var vec = new float[_dimensions];

            if (!string.IsNullOrWhiteSpace(value))
            {
                var normalized = _normalizer.NormalizeForSearch(value);
                var tokens = WordRegex().Split(normalized);

                foreach (var token in tokens)
                {
                    if (string.IsNullOrWhiteSpace(token))
                    {
                        continue;
                    }

                    uint h = 2166136261u;
                    var bytes = Encoding.UTF8.GetBytes(token);
                    foreach (var b in bytes)
                    {
                        h ^= b;
                        h *= 16777619u;
                    }

                    int index = (int)(h % (uint)_dimensions);
                    float sign = (h & 0x80000000u) == 0u ? 1.0f : -1.0f;
                    vec[index] += sign;
                }

                double sumSq = 0.0;
                for (int i = 0; i < vec.Length; i++)
                {
                    sumSq += (double)vec[i] * vec[i];
                }

                double norm = Math.Sqrt(sumSq);
                if (norm > 0.0)
                {
                    float inv = (float)(1.0 / norm);
                    for (int i = 0; i < vec.Length; i++)
                    {
                        vec[i] *= inv;
                    }
                }
            }

            embeddings.Add(new Embedding<float>(vec));
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(embeddings);
        return Task.FromResult(result);
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
        // No unmanaged resources
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex WordRegex();
}
