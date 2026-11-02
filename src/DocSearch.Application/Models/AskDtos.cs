namespace DocSearch.Application.Models;

public sealed record AskRequestDto(
    string? Question,
    IReadOnlyCollection<Guid>? CollectionIds,
    Guid? ConversationId = null,
    int? TopK = null);

public sealed record AskResponse(
    string? Answer,
    bool NotFound,
    bool Degraded,
    Guid? ConversationId,
    IReadOnlyList<SourceCitationDto> Sources);

public sealed record SourceCitationDto(
    int Index,
    Guid DocumentId,
    string FileName,
    int Page,
    string? SectionTitle,
    string Snippet,
    double Score);

public sealed record ConversationSummaryDto(
    Guid Id,
    string? Title,
    DateTimeOffset CreatedAt);

public sealed record ConversationMessageDto(
    Guid Id,
    Guid ConversationId,
    string Role,
    string Content,
    bool Degraded,
    bool NotFound,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SourceCitationDto>? Sources = null);
