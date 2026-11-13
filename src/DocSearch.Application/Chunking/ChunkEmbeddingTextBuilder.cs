using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;

namespace DocSearch.Application.Chunking;

/// <summary>
/// Builds the normalized passage text passed to the embedding model for a chunk,
/// ensuring ingestion and re-indexing apply identical prefixing and normalization rules.
/// </summary>
public static class ChunkEmbeddingTextBuilder
{
    public static string Build(
        ITextNormalizer normalizer,
        ChunkingOptions options,
        string? sectionTitle,
        string text)
    {
        ArgumentNullException.ThrowIfNull(options);

        return Build(normalizer, sectionTitle, text, options.IncludeSectionTitleInEmbedding);
    }

    public static string Build(
        ITextNormalizer normalizer,
        string? sectionTitle,
        string text,
        bool includeSectionTitleInEmbedding = true)
    {
        ArgumentNullException.ThrowIfNull(normalizer);
        ArgumentNullException.ThrowIfNull(text);

        var prefix = includeSectionTitleInEmbedding && !string.IsNullOrWhiteSpace(sectionTitle)
            ? $"{sectionTitle}\n"
            : string.Empty;

        return normalizer.NormalizeForEmbedding(prefix + text);
    }
}
