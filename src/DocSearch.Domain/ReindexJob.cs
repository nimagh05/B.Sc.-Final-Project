namespace DocSearch.Domain;

public sealed class ReindexJob
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CollectionId { get; set; }
    public ReindexJobStatus Status { get; set; } = ReindexJobStatus.Queued;
    public int TotalChunks { get; set; }
    public int DoneChunks { get; set; }
    public string EmbeddingModel { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public Collection? Collection { get; set; }
}
