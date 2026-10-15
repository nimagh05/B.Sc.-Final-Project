namespace DocSearch.Domain;

public sealed class CollectionMember
{
    public Guid CollectionId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public CollectionRole Role { get; set; } = CollectionRole.Viewer;

    public Collection? Collection { get; set; }
}
