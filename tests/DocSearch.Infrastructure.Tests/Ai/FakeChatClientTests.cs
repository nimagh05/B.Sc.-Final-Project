using DocSearch.Application.Rag;
using DocSearch.Infrastructure.Ai.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Ai;

public sealed class FakeChatClientTests
{
    [Fact]
    public async Task GetResponseAsync_WithExcerpts_ReturnsGroundedAnswer()
    {
        using var client = new FakeChatClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Rules..."),
            new(ChatRole.User, "CONTEXT:\n[1] (file: test.pdf, page: 1, section: ماده ۱)\nمتن تست\n\nQUESTION: تست؟")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.Equal("پاسخ آزمایشی بر اساس منبع [1].", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WithoutExcerpts_ReturnsRefusal()
    {
        using var client = new FakeChatClient();
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Rules..."),
            new(ChatRole.User, "CONTEXT:\n(no excerpts)\n\nQUESTION: تست؟")
        };

        var response = await client.GetResponseAsync(messages);

        Assert.NotNull(response);
        Assert.Equal(RagConstants.PersianRefusalSentence, response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WhenFailConfigured_ThrowsHttpRequestException()
    {
        using var client = new FakeChatClient(fail: true);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "سوال؟")
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(messages));
    }

    [Fact]
    public async Task GetResponseAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using var client = new FakeChatClient(delayMs: 5000);
        using var cts = new CancellationTokenSource(50);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "سوال؟")
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetResponseAsync(messages, cancellationToken: cts.Token));
    }
}
