using System.ClientModel;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using DocSearch.Application.Prompts;
using DocSearch.Application.Rag;
using DocSearch.Application.Text;
using DocSearch.Domain;
using DocSearch.Infrastructure.Ai.Fakes;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using DocSearch.Infrastructure.Rag;
using DocSearch.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Rag;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public sealed class RagServiceTests(PostgresFixture fixture)
{
    private readonly IOptions<RagOptions> _ragOptions = Microsoft.Extensions.Options.Options.Create(new RagOptions
    {
        TopK = 5,
        CandidateK = 10,
        MinSimilarity = 0.55,
        MaxContextTokens = 4000,
        PromptVersion = "v1",
        ReturnSourcesWhenNotFound = false
    });

    private readonly IOptions<AiOptions> _aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
    {
        Chat = new AiChatOptions
        {
            TimeoutSeconds = 60,
            Temperature = 0.1,
            MaxOutputTokens = 800
        }
    });

    private readonly ITextNormalizer _normalizer = new PersianTextNormalizer();

    private static PromptBuilder CreatePromptBuilder()
    {
        var chunkingOpts = Microsoft.Extensions.Options.Options.Create(new ChunkingOptions());
        return new PromptBuilder(new HeuristicTokenCounter(chunkingOpts));
    }

    [Fact]
    public async Task Normal_Answer_Has_Valid_Sources()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: docId,
            FileName: "test.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: "ماده ۱",
            Text: "حداکثر واحد ۲۰ است.",
            VectorScore: 0.85,
            FtsScore: null,
            FusedScore: 0.85);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.85, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var chatClient = new FakeChatClient();
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chatClient,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "تعداد واحد؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.False(result.NotFound);
        Assert.False(result.Degraded);
        Assert.NotNull(result.Answer);
        Assert.Contains("[1]", result.Answer);
        Assert.Single(result.Sources);
        Assert.Equal(1, result.Sources[0].Index);
        Assert.Equal("test.pdf", result.Sources[0].FileName);
        Assert.NotNull(result.ConversationId);
    }

    [Fact]
    public async Task Below_Threshold_Returns_NotFound_Without_Calling_LLM()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "test.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: "ماده ۱",
            Text: "متن نامربوط",
            VectorScore: 0.35, // Below 0.55 threshold
            FtsScore: null,
            FusedScore: 0.35);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.35, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var mockChat = new CallCountingChatClient("پاسخ نباید تولید شود.");
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            mockChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "آب و هوا چطور است؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.NotFound);
        Assert.False(result.Degraded);
        Assert.Equal(RagConstants.PersianRefusalSentence, result.Answer);
        Assert.Empty(result.Sources);
        Assert.Equal(0, mockChat.CallCount); // Verified: LLM was not called!
    }

    [Fact]
    public async Task Zero_Chunks_Returns_NotFound_Without_Calling_LLM()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var retriever = new StubRetriever(new RetrievalResult([], BestVectorScore: 0.0, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var mockChat = new CallCountingChatClient("پاسخ");
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            mockChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال بدون چانک؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.NotFound);
        Assert.False(result.Degraded);
        Assert.Equal(RagConstants.PersianRefusalSentence, result.Answer);
        Assert.Equal(0, mockChat.CallCount);
    }

    [Fact]
    public async Task LLM_Outage_Returns_Degraded_With_Sources()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "rules.pdf",
            PageStart: 5,
            PageEnd: 5,
            SectionTitle: "ماده ۵",
            Text: "شرایط انصراف از تحصیل",
            VectorScore: 0.82,
            FtsScore: null,
            FusedScore: 0.82);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.82, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var failingChat = new FakeChatClient(fail: true);
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            failingChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "شرایط انصراف؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.False(result.NotFound);
        Assert.Null(result.Answer);
        Assert.Single(result.Sources);
        Assert.Equal("rules.pdf", result.Sources[0].FileName);
    }

    [Fact]
    public async Task Refusal_Sentence_Sets_NotFound()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "test.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "برخی اطلاعات کلی",
            VectorScore: 0.70,
            FtsScore: null,
            FusedScore: 0.70);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.70, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var refusingChat = new CallCountingChatClient("«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»");
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            refusingChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال ناشناخته؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.NotFound);
        Assert.Equal(RagConstants.PersianRefusalSentence, result.Answer);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task Refusal_Without_Guillemets_Sets_NotFound()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "test.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "برخی اطلاعات کلی",
            VectorScore: 0.70,
            FtsScore: null,
            FusedScore: 0.70);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.70, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var refusingChat = new CallCountingChatClient("در اسناد موجود پاسخی برای این پرسش پیدا نشد.");
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            refusingChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال ناشناخته؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.NotFound);
        Assert.Equal(RagConstants.PersianRefusalSentence, result.Answer);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task Refusal_With_Citation_Tag_And_No_Guillemets_Sets_NotFound()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "test.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "برخی اطلاعات کلی",
            VectorScore: 0.70,
            FtsScore: null,
            FusedScore: 0.70);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.70, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var refusingChat = new CallCountingChatClient("در اسناد موجود پاسخی برای این پرسش پیدا نشد [1].");
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            refusingChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال ناشناخته؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.NotFound);
        Assert.Equal(RagConstants.PersianRefusalSentence, result.Answer);
        Assert.Empty(result.Sources);
    }

    [Fact]
    public async Task Answer_Without_Citations_Falls_Back_To_All_Sources()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "doc.pdf",
            PageStart: 2,
            PageEnd: 2,
            SectionTitle: "بخش ۱",
            Text: "پاسخ سوال در این متن وجود دارد اما مدل نقل قول درج نکرده است.",
            VectorScore: 0.88,
            FtsScore: null,
            FusedScore: 0.88);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.88, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var chatWithoutCitations = new CallCountingChatClient("پاسخ کامل به سوال شما داده شد.");
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chatWithoutCitations,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.False(result.NotFound);
        Assert.Equal("پاسخ کامل به سوال شما داده شد.", result.Answer);
        Assert.Single(result.Sources);
        Assert.Equal("doc.pdf", result.Sources[0].FileName);
    }

    [Fact]
    public async Task Invalid_Citation_Is_Stripped()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "doc.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "متن مستند اول",
            VectorScore: 0.80,
            FtsScore: null,
            FusedScore: 0.80);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.80, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var chatWithHallucinatedCite = new CallCountingChatClient("طبق آمار [1] و منبع توهمی [99] صحیح است.");
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chatWithHallucinatedCite,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.Equal("طبق آمار [1] و منبع توهمی صحیح است.", result.Answer);
        Assert.Single(result.Sources);
        Assert.Equal(1, result.Sources[0].Index);
        Assert.DoesNotContain("99", result.Answer);
    }

    [Fact]
    public async Task Conversation_Is_Persisted()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();
        var userId = Guid.NewGuid().ToString();

        var user = new ApplicationUser
        {
            Id = userId,
            UserName = "test@persisted.local",
            Email = "test@persisted.local"
        };
        await db.Users.AddAsync(user);
        await db.SaveChangesAsync();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "doc.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "متن مستند",
            VectorScore: 0.80,
            FtsScore: null,
            FusedScore: 0.80);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.80, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var chat = new FakeChatClient();
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest(userId, "سوال ذخیره‌سازی سابقه چت؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.NotNull(result.ConversationId);

        var savedConv = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c => c.Id == result.ConversationId.Value);

        Assert.NotNull(savedConv);
        Assert.Equal(userId, savedConv.UserId);
        Assert.Equal(2, savedConv.Messages.Count); // User message + Assistant message
        Assert.Contains(savedConv.Messages, m => m.Role == MessageRole.User && m.Content == request.Question);
        Assert.Contains(savedConv.Messages, m => m.Role == MessageRole.Assistant && m.Content == result.Answer);
    }

    [Fact]
    public async Task LLM_Timeout_Returns_Degraded_With_Sources()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "rules.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "متن مستند تستی",
            VectorScore: 0.82,
            FtsScore: null,
            FusedScore: 0.82);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.82, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);

        var shortTimeoutOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Chat = new AiChatOptions
            {
                TimeoutSeconds = 1,
                Temperature = 0.1,
                MaxOutputTokens = 800
            }
        });

        var slowChat = new FakeChatClient(delayMs: 3000);
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            slowChat,
            db,
            _ragOptions,
            shortTimeoutOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال با تاخیر زیاد؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.False(result.NotFound);
        Assert.Null(result.Answer);
        Assert.Single(result.Sources);
    }

    [Fact]
    public async Task Caller_Cancellation_Rethrows_OperationCanceledException()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "rules.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "متن مستند",
            VectorScore: 0.80,
            FtsScore: null,
            FusedScore: 0.80);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.80, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var slowChat = new FakeChatClient(delayMs: 5000);
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            slowChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        using var cts = new CancellationTokenSource(50);
        var request = new AskRequest("user1", "سوال کنسل شده؟", [collectionId], ConversationId: null, TopK: 5);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AskAsync(request, cts.Token));
    }

    [Fact]
    public async Task ClientResultException_Returns_Degraded_With_Sources()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "rules.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "متن مستند",
            VectorScore: 0.80,
            FtsScore: null,
            FusedScore: 0.80);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.80, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var throwingChat = new ThrowingChatClient(new ClientResultException("Quota exceeded", null));
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            throwingChat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "سوال کوئری با خطای کووتا؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.False(result.NotFound);
        Assert.Null(result.Answer);
        Assert.Single(result.Sources);
    }

    [Fact]
    public async Task CrossUser_ConversationId_Throws_NotFoundException()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();
        var userA = Guid.NewGuid().ToString();
        var userB = Guid.NewGuid().ToString();

        var appUserA = new ApplicationUser
        {
            Id = userA,
            UserName = $"{userA}@test.local",
            Email = $"{userA}@test.local"
        };
        await db.Users.AddAsync(appUserA);

        var convA = new Conversation
        {
            Id = Guid.CreateVersion7(),
            UserId = userA,
            Title = "User A Conversation",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.Conversations.AddAsync(convA);
        await db.SaveChangesAsync();

        var chunk = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: Guid.NewGuid(),
            FileName: "doc.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: null,
            Text: "متن مستند",
            VectorScore: 0.80,
            FtsScore: null,
            FusedScore: 0.80);

        var retriever = new StubRetriever(new RetrievalResult([chunk], BestVectorScore: 0.80, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var chat = new FakeChatClient();
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest(userB, "پرسش با شناسه گفتگوی کاربر دیگر؟", [collectionId], ConversationId: convA.Id, TopK: 5);

        await Assert.ThrowsAsync<DocSearch.Application.Exceptions.NotFoundException>(() => service.AskAsync(request, CancellationToken.None));
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public async Task TwoPart_PartialAnswer_WithRefusal_Keeps_Answer_And_Citations()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var chunk1 = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: docId,
            FileName: "regulation.pdf",
            PageStart: 5,
            PageEnd: 5,
            SectionTitle: "ماده ۱۰",
            Text: "حداکثر واحد مجاز در هر ترم ۲۰ واحد است.",
            VectorScore: 0.85,
            FtsScore: null,
            FusedScore: 0.85);

        var retriever = new StubRetriever(new RetrievalResult([chunk1], BestVectorScore: 0.85, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var twoPartAnswer = "حداکثر واحد مجاز در هر ترم ۲۰ واحد است [1]. برای دوره تابستان در اسناد موجود پاسخی برای این پرسش پیدا نشد.";
        var chatClient = new CallCountingChatClient(twoPartAnswer);
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chatClient,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "تعداد واحد در ترم و تابستان چقدر است؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.False(result.NotFound);
        Assert.False(result.Degraded);
        Assert.NotNull(result.Answer);
        Assert.Contains("[1]", result.Answer);
        Assert.Contains("حداکثر واحد مجاز", result.Answer);
        Assert.Single(result.Sources);
        Assert.Equal(1, result.Sources[0].Index);
        Assert.Equal("regulation.pdf", result.Sources[0].FileName);
    }

    [Theory]
    // Exact phrase or with punctuation/brackets (remainder <= 15 chars -> true)
    [InlineData(RagConstants.PersianRefusalCorePhrase, true)]
    [InlineData("«در اسناد موجود پاسخی برای این پرسش پیدا نشد.»", true)]
    [InlineData("متاسفانه در اسناد موجود پاسخی برای این پرسش پیدا نشد.", true)]
    // Remainder exactly 15 chars (<= 15 chars -> true)
    [InlineData("یک دو سه چهارده در اسناد موجود پاسخی برای این پرسش پیدا نشد", true)]
    // Remainder 16 chars (> 15 chars -> false)
    [InlineData("یک دو سه چهارده۱ در اسناد موجود پاسخی برای این پرسش پیدا نشد", false)]
    // Two-part substantive answer (> 15 chars -> false)
    [InlineData("حداکثر واحد مجاز در هر ترم ۲۰ واحد است [1]. برای دوره تابستان در اسناد موجود پاسخی برای این پرسش پیدا نشد.", false)]
    public void IsModelRefusal_BoundaryTests_AroundMaxRefusalRemainderLength(string input, bool expected)
    {
        using var db = fixture.CreateDbContext();
        var chat = new FakeChatClient();
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();
        var retriever = new StubRetriever(new RetrievalResult([], 0, false));
        var accessService = new StubAccessService([]);

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chat,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var result = service.IsModelRefusal(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task Empty_Or_Whitespace_LlmResponse_Returns_DegradedMode(string emptyResponse)
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        var chunk1 = new ScoredChunk(
            ChunkId: Guid.NewGuid(),
            DocumentId: docId,
            FileName: "regulation.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: "ماده ۱",
            Text: "متن مستند آموزشی دانشگاه.",
            VectorScore: 0.82,
            FtsScore: null,
            FusedScore: 0.82);

        var retriever = new StubRetriever(new RetrievalResult([chunk1], BestVectorScore: 0.82, HasFtsHit: false));
        var accessService = new StubAccessService([collectionId]);
        var chatClient = new CallCountingChatClient(emptyResponse);
        var promptBuilder = CreatePromptBuilder();
        var citationParser = new CitationParser();

        var service = new RagService(
            accessService,
            retriever,
            promptBuilder,
            citationParser,
            chatClient,
            db,
            _ragOptions,
            _aiOptions,
            _normalizer,
            NullLogger<RagService>.Instance);

        var request = new AskRequest("user1", "شرایط چیست؟", [collectionId], ConversationId: null, TopK: 5);
        var result = await service.AskAsync(request, CancellationToken.None);

        Assert.True(result.Degraded);
        Assert.False(result.NotFound);
        Assert.Null(result.Answer);
        Assert.Single(result.Sources);
        Assert.Equal("regulation.pdf", result.Sources[0].FileName);
    }
}

internal sealed class ThrowingChatClient(Exception exception) : IChatClient
{
    public ChatClientMetadata Metadata => new(nameof(ThrowingChatClient));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw exception;
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw exception;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}

internal sealed class StubRetriever(RetrievalResult result) : IRetriever
{
    public Task<RetrievalResult> RetrieveAsync(
        string question,
        IReadOnlyCollection<Guid> collectionIds,
        int? topK,
        CancellationToken ct)
    {
        return Task.FromResult(result);
    }
}

internal sealed class StubAccessService(IReadOnlyCollection<Guid> readableIds) : IAccessService
{
    public Task<IReadOnlyCollection<Guid>> GetReadableCollectionIdsAsync(
        string userId,
        IReadOnlyCollection<Guid>? requested,
        CancellationToken ct)
    {
        var filtered = requested != null && requested.Count > 0
            ? readableIds.Where(requested.Contains).ToList()
            : readableIds.ToList();
        return Task.FromResult<IReadOnlyCollection<Guid>>(filtered);
    }
}

internal sealed class CallCountingChatClient(string responseText) : IChatClient
{
    public int CallCount { get; private set; }
    public ChatClientMetadata Metadata => new(nameof(CallCountingChatClient));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent(responseText)] };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return serviceKey == null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }
}
