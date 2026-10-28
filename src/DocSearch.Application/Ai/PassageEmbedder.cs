using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DocSearch.Application.Ai;

public sealed class PassageEmbedder : IPassageEmbedder
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly IOptions<AiOptions> _aiOptions;

    public PassageEmbedder(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        IOptions<AiOptions> aiOptions)
    {
        _generator = generator;
        _aiOptions = aiOptions;
    }

    public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(passages);
        ct.ThrowIfCancellationRequested();

        if (passages.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var prefix = _aiOptions.Value.Embedding.PassagePrefix;
        IEnumerable<string> inputs = string.IsNullOrEmpty(prefix)
            ? passages
            : passages.Select(p => prefix + p);

        var response = await _generator.GenerateAsync(inputs, cancellationToken: ct).ConfigureAwait(false);
        return response.Select(e => e.Vector.ToArray()).ToList();
    }
}
