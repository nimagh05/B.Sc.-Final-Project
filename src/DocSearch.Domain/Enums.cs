using System.Text.Json.Serialization;

namespace DocSearch.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DocumentStatus
{
    Uploaded,
    Processing,
    Ready,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CollectionRole
{
    Viewer,
    Editor,
    Owner
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageRole
{
    User,
    Assistant
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReindexJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}
