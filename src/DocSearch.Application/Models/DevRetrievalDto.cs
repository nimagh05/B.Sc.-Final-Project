namespace DocSearch.Application.Models;

public sealed record DevRetrievalRequestDto(
    string Question,
    IReadOnlyCollection<Guid> CollectionIds,
    int? TopK = null,
    bool? UseHybrid = null);

public sealed record DevChunkDto(
    Guid ChunkId,
    Guid DocumentId,
    string FileName,
    int PageStart,
    int PageEnd,
    string? SectionTitle,
    string Text,
    double Score,
    double VectorScore,
    double? FtsScore,
    int Rank);

public sealed record DevRetrievalResponse(
    double BestVectorScore,
    bool HasFtsHit,
    double ElapsedMs,
    IReadOnlyList<DevChunkDto> Chunks);
