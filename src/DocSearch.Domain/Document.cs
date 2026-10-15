namespace DocSearch.Domain;

public sealed class Document
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CollectionId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public int? PageCount { get; set; }
    public int[]? OcrPages { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Uploaded;
    public string? ErrorMessage { get; set; }
    public string UploadedBy { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }

    public Collection? Collection { get; set; }
    public ICollection<Chunk> Chunks { get; set; } = new List<Chunk>();
}
