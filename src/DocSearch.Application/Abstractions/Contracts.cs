using DocSearch.Application.Options;
using DocSearch.Domain;

namespace DocSearch.Application.Abstractions;

public sealed record PageText(int PageNumber, string Text, double Quality, bool NeedsOcr, bool LooksReversed = false);

public interface IPdfExtractor
{
    Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct);
}

public interface ITextNormalizer
{
    string NormalizeForDisplay(string text);
    string NormalizeForEmbedding(string text);
    string NormalizeForSearch(string text);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Represents text-layer defect flags per ADR 0003")]
[Flags]
public enum ChunkDefectFlags
{
    None = 0,
    CorruptLigatureLamAlef = 1 << 0,
    SuspectReversedDate = 1 << 1
}

public sealed record ChunkDraft(
    int Index,
    int PageStart,
    int PageEnd,
    string? SectionTitle,
    string Text,
    string SearchText,
    string EmbeddingText,
    int TokenCount,
    ChunkDefectFlags DefectFlags = ChunkDefectFlags.None,
    IReadOnlyList<string>? ArticleKeys = null);

public interface IChunker
{
    IReadOnlyList<ChunkDraft> Chunk(IReadOnlyList<PageText> pages, ChunkingOptions options);
}

public interface ITokenCounter
{
    int Count(string text);
}

public interface IFileStorage
{
    Task<string> SaveAsync(Stream content, string suggestedFileName, CancellationToken ct);
    Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct);
    Task DeleteAsync(string storagePath, CancellationToken ct);
}

public sealed record SearchRequest(
    float[] QueryEmbedding,
    string QueryText,
    IReadOnlyCollection<Guid> CollectionIds,
    string EmbeddingModel,
    int CandidateK,
    int TopK,
    bool UseHybrid);

public sealed record ScoredChunk(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    int PageStart,
    int PageEnd,
    string? SectionTitle,
    string Text,
    double VectorScore,
    double? FtsScore,
    double FusedScore);

public sealed record ChunkRecord(ChunkDraft Draft, float[] Embedding, string EmbeddingModel);

public sealed record HybridSearchRequest(
    float[] QueryEmbedding,
    string QueryText,
    string? TsQuery,
    IReadOnlyList<string> ExtractedArticleKeys,
    int? ExtractedNoteNumber,
    IReadOnlyCollection<Guid> CollectionIds,
    string EmbeddingModel,
    int CandidateK,
    int TopK,
    bool UseHybrid);

public sealed record CandidateChunk(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    int PageStart,
    int PageEnd,
    string? SectionTitle,
    string Text,
    string SearchText,
    IReadOnlyList<string> ArticleKeys,
    double VectorScore,
    double? FtsScore);

public sealed record HybridSearchResult(
    IReadOnlyList<CandidateChunk> VectorCandidates,
    IReadOnlyList<CandidateChunk> FtsCandidates,
    IReadOnlyList<CandidateChunk> RefCandidates,
    double BestVectorScore);

public interface IVectorStore
{
    Task AddChunksAsync(Guid documentId, Guid collectionId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct);
    Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct);
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(SearchRequest request, CancellationToken ct);
    Task<HybridSearchResult> SearchHybridAsync(HybridSearchRequest request, CancellationToken ct);
}

public sealed record RetrievalResult(IReadOnlyList<ScoredChunk> Chunks, double BestVectorScore, bool HasFtsHit);

public interface IRetriever
{
    Task<RetrievalResult> RetrieveAsync(string question, IReadOnlyCollection<Guid> collectionIds, int? topK, CancellationToken ct);
}

public sealed record AskRequest(
    string UserId,
    string Question,
    IReadOnlyCollection<Guid> CollectionIds,
    Guid? ConversationId,
    int? TopK);

public sealed record SourceCitation(
    int Index,
    Guid DocumentId,
    string FileName,
    int Page,
    string? SectionTitle,
    string Snippet,
    double Score);

public sealed record AnswerResult(
    string? Answer,
    bool NotFound,
    bool Degraded,
    IReadOnlyList<SourceCitation> Sources,
    Guid? ConversationId);

public interface IRagService
{
    Task<AnswerResult> AskAsync(AskRequest request, CancellationToken ct);
}

public interface IIngestionService
{
    Task ProcessDocumentAsync(Guid documentId, CancellationToken ct);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Required by CONVENTIONS.md core contracts")]
public interface IIngestionQueue
{
    ValueTask EnqueueAsync(Guid documentId, CancellationToken ct);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
}

public interface IAccessService
{
    Task<IReadOnlyCollection<Guid>> GetReadableCollectionIdsAsync(
        string userId,
        IReadOnlyCollection<Guid>? requested,
        CancellationToken ct);
}
