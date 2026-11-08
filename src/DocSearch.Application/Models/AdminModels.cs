namespace DocSearch.Application.Models;

public sealed record ReindexJobResponse(Guid JobId);

public sealed record ReindexJobStatusResponse(
    string Status,
    int TotalChunks,
    int DoneChunks,
    string? ErrorMessage);
