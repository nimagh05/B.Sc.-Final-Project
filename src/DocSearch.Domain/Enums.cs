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

public enum MessageRole
{
    User,
    Assistant
}

public enum ReindexJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}
