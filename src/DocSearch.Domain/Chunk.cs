using NpgsqlTypes;
using Pgvector;

namespace DocSearch.Domain;

public sealed class Chunk
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DocumentId { get; set; }
    public Guid CollectionId { get; set; }
    public int ChunkIndex { get; set; }
    public int PageStart { get; set; }
    public int PageEnd { get; set; }
    public string? SectionTitle { get; set; }
    public string Text { get; set; } = string.Empty;
    public string SearchText { get; set; } = string.Empty;
    public int TokenCount { get; set; }
    public Vector Embedding { get; set; } = new(new float[Constants.EmbeddingConstants.VectorDimensions]);
    public string EmbeddingModel { get; set; } = string.Empty;
    public NpgsqlTsVector? Tsv { get; set; }
    public string[] ArticleKeys { get; set; } = [];

    public Document? Document { get; set; }
}
