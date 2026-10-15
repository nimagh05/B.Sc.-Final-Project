namespace DocSearch.Domain;

public enum DocumentStatus
{
    Uploaded,
    Processing,
    Ready,
    Failed
}

public enum CollectionRole
{
    Viewer,
    Editor,
    Owner
}
