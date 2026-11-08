using DocSearch.Domain;

namespace DocSearch.Application.Models;

public sealed record CreateCollectionRequest(string Name, string? Description);

public sealed record UpdateCollectionRequest(string Name, string? Description);

public sealed record CollectionDto(
    Guid Id,
    string Name,
    string? Description,
    CollectionRole Role,
    int DocumentCount,
    DateTimeOffset CreatedAt);

public sealed record PagedCollectionsResponse(
    IReadOnlyList<CollectionDto> Items,
    int Total,
    int Page,
    int PageSize);

public sealed record CollectionMemberDto(string UserId, string Email, CollectionRole Role);

public sealed record UpsertMemberRequest(string Email, CollectionRole Role);
