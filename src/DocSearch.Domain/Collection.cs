namespace DocSearch.Domain;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Domain entity name matches SPEC-DATA-AND-API.md table and domain model")]
public sealed class Collection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<CollectionMember> Members { get; set; } = new List<CollectionMember>();
    public ICollection<Document> Documents { get; set; } = new List<Document>();
    public ICollection<ReindexJob> ReindexJobs { get; set; } = new List<ReindexJob>();
}
