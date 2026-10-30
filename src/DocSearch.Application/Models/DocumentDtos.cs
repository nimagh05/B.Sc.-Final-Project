using DocSearch.Domain;

namespace DocSearch.Application.Models;

public sealed record DocumentResponse(
    Guid Id,
    Guid CollectionId,
    string FileName,
    long SizeBytes,
    DocumentStatus Status,
    int? PageCount,
    DateTimeOffset CreatedAt);

public sealed record DocumentDetailResponse(
    Guid Id,
    Guid CollectionId,
    string FileName,
    long SizeBytes,
    DocumentStatus Status,
    string? ErrorMessage,
    int? PageCount,
    int[]? OcrPages,
    int ChunkCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Total,
    int Page,
    int PageSize);
