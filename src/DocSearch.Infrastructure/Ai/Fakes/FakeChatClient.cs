using DocSearch.Application.Rag;
using Microsoft.Extensions.AI;

namespace DocSearch.Infrastructure.Ai.Fakes;

public sealed class FakeChatClient : IChatClient
{
    private readonly bool _fail;
    private readonly int _delayMs;
    private readonly ChatClientMetadata _metadata = new(nameof(FakeChatClient));
    private int _callCount;

    public FakeChatClient(bool fail = false, int delayMs = 0)
    {
        _fail = fail;
        _delayMs = delayMs;
    }

    public int CallCount => _callCount;
    public ChatClientMetadata Metadata => _metadata;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _callCount);

        var lastUserMessage = messages
            .LastOrDefault(m => m.Role == ChatRole.User)?
            .Text ?? string.Empty;

        if (_fail)
        {
            throw new HttpRequestException("Simulated chat provider outage.");
        }

        if (_delayMs > 0)
        {
            await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
        }

        string responseText;
        if (lastUserMessage.Contains("[1]", StringComparison.Ordinal))
        {
            responseText = "پاسخ آزمایشی بر اساس منبع [1].";
        }
        else
        {
            responseText = RagConstants.PersianRefusalSentence;
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent(response.Text)]
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // No unmanaged resources
    }
}
