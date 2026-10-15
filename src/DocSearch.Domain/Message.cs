namespace DocSearch.Domain;

public sealed class Message
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ConversationId { get; set; }
    public MessageRole Role { get; set; } = MessageRole.User;
    public string Content { get; set; } = string.Empty;
    public string? SourcesJson { get; set; }
    public bool Degraded { get; set; }
    public bool NotFound { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
}
