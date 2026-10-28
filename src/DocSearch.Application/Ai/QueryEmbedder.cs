using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DocSearch.Application.Ai;

public sealed class QueryEmbedder : IQueryEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ITextNormalizer _normalizer;
    private readonly IOptions<AiOptions> _aiOptions;

    public QueryEmbedder(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ITextNormalizer normalizer,
        IOptions<AiOptions> aiOptions)
    {
        _generator = generator;
        _normalizer = normalizer;
        _aiOptions = aiOptions;
    }

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();

        var normalized = _normalizer.NormalizeForEmbedding(query);
        var prefix = _aiOptions.Value.Embedding.QueryPrefix;
        var input = string.IsNullOrEmpty(prefix) ? normalized : prefix + normalized;

        var response = await _generator.GenerateAsync([input], cancellationToken: ct).ConfigureAwait(false);
        return response[0].Vector.ToArray();
    }
}
