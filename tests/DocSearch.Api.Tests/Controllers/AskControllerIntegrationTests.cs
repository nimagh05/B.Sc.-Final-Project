using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocSearch.Api.Tests.Fixtures;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Models;
using DocSearch.Application.Rag;
using DocSearch.Domain;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Xunit;

namespace DocSearch.Api.Tests.Controllers;

[Trait("Category", "Integration")]
[Collection(ApiTestCollection.Name)]
public sealed class AskControllerIntegrationTests(ApiTestFixture fixture)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private async Task SeedReadyDocumentWithChunkAsync(Guid collectionId, string text)
    {
        await using var db = fixture.CreateDbContext();

        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collectionId,
            FileName = "aeen-name.pdf",
            StoragePath = "test/path.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            ContentHash = Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Ready,
            UploadedBy = DevCurrentUser.DefaultUserId,
            PageCount = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessedAt = DateTimeOffset.UtcNow
        };
        await db.Documents.AddAsync(document);

        // Generate embedding using Fake generator directly without spinning up WebApplicationFactory
        var embedder = new DocSearch.Infrastructure.Ai.Fakes.FakeEmbeddingGenerator(new DocSearch.Application.Text.PersianTextNormalizer());
        var embeddings = await embedder.GenerateAsync([text]);
        var vector = new Vector(embeddings[0].Vector.ToArray());

        var chunk = new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = document.Id,
            CollectionId = collectionId,
            ChunkIndex = 0,
            PageStart = 1,
            PageEnd = 1,
            SectionTitle = "ماده ۱۲",
            Text = text,
            SearchText = text,
            TokenCount = 20,
            ArticleKeys = ["ماده 12"],
            Embedding = vector,
            EmbeddingModel = "bge-m3"
        };
        await db.Chunks.AddAsync(chunk);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Ask_Returns_200_With_Sources_WhenMatchFound()
    {
        var collectionId = DevDatabaseSeeder.DefaultCollectionId;
        const string text = "حداکثر تعداد واحد مجاز در هر نیم‌سال تحصیلی ۲۰ واحد است.";
        await SeedReadyDocumentWithChunkAsync(collectionId, text);

        using var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Rag:MinSimilarity"] = "0.2"
        });
        using var client = factory.CreateClient();

        var payload = new AskRequestDto(
            Question: "حداکثر واحد مجاز در هر نیم‌سال چند واحد است؟",
            CollectionIds: [collectionId],
            TopK: 5);

        var response = await client.PostAsJsonAsync("/api/v1/ask", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<AskResponse>(_jsonOptions);

        Assert.NotNull(data);
        Assert.False(data.NotFound);
        Assert.False(data.Degraded);
        Assert.NotNull(data.Answer);
        Assert.NotEmpty(data.Sources);
        Assert.Equal("aeen-name.pdf", data.Sources[0].FileName);
        Assert.NotNull(data.ConversationId);
    }

    [Fact]
    public async Task Ask_Empty_Question_Returns_400_ProblemDetails()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var payload = new AskRequestDto(
            Question: "   ",
            CollectionIds: [DevDatabaseSeeder.DefaultCollectionId]);

        var response = await client.PostAsJsonAsync("/api/v1/ask", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Ask_Question_Too_Short_Returns_400()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var payload = new AskRequestDto(
            Question: "چ؟",
            CollectionIds: [DevDatabaseSeeder.DefaultCollectionId]);

        var response = await client.PostAsJsonAsync("/api/v1/ask", payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ask_Unreadable_Collection_Returns_404()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        // Random non-existent collection
        var randomCollectionId = Guid.NewGuid();
        var payload = new AskRequestDto(
            Question: "تعداد واحد مجاز؟",
            CollectionIds: [randomCollectionId]);

        var response = await client.PostAsJsonAsync("/api/v1/ask", payload);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Ask_Degraded_Mode_Returns_200_With_Degraded_Flag()
    {
        var collectionId = DevDatabaseSeeder.DefaultCollectionId;
        const string text = "ضوابط دروس پیش‌نیاز در مقطع کارشناسی پیوسته.";
        await SeedReadyDocumentWithChunkAsync(collectionId, text);

        using var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Ai:Chat:Fake:Fail"] = "true",
            ["Rag:MinSimilarity"] = "0.2"
        });
        using var client = factory.CreateClient();

        var payload = new AskRequestDto(
            Question: "دروس پیش‌نیاز کارشناسی؟",
            CollectionIds: [collectionId]);

        var response = await client.PostAsJsonAsync("/api/v1/ask", payload);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await response.Content.ReadFromJsonAsync<AskResponse>(_jsonOptions);

        Assert.NotNull(data);
        Assert.True(data.Degraded);
        Assert.Null(data.Answer);
        Assert.NotEmpty(data.Sources);
    }

    [Fact]
    public async Task Ask_MultiTurn_Conversation_Persists_Messages()
    {
        var collectionId = DevDatabaseSeeder.DefaultCollectionId;
        const string text = "سقف مجاز اخذ واحد تحصیلی برای دانشجویان ممتاز ۲۴ واحد است.";
        await SeedReadyDocumentWithChunkAsync(collectionId, text);

        using var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Rag:MinSimilarity"] = "0.2"
        });
        using var client = factory.CreateClient();

        // Turn 1
        var payload1 = new AskRequestDto(
            Question: "سقف مجاز واحد دانشجویان ممتاز؟",
            CollectionIds: [collectionId]);

        var resp1 = await client.PostAsJsonAsync("/api/v1/ask", payload1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var data1 = await resp1.Content.ReadFromJsonAsync<AskResponse>(_jsonOptions);
        Assert.NotNull(data1);
        var convId = data1.ConversationId;
        Assert.NotNull(convId);

        // Turn 2 with ConversationId
        var payload2 = new AskRequestDto(
            Question: "دانشجویان عادی چند واحد می‌توانند بردارند؟",
            CollectionIds: [collectionId],
            ConversationId: convId);

        var resp2 = await client.PostAsJsonAsync("/api/v1/ask", payload2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var data2 = await resp2.Content.ReadFromJsonAsync<AskResponse>(_jsonOptions);
        Assert.NotNull(data2);
        Assert.Equal(convId, data2.ConversationId);

        // Fetch messages via ConversationsController
        var msgsResp = await client.GetAsync($"/api/v1/conversations/{convId}/messages");
        Assert.Equal(HttpStatusCode.OK, msgsResp.StatusCode);
        var messages = await msgsResp.Content.ReadFromJsonAsync<List<ConversationMessageDto>>(_jsonOptions);

        Assert.NotNull(messages);
        Assert.Equal(4, messages.Count); // Turn 1 (user + assistant) + Turn 2 (user + assistant)
        Assert.Equal("user", messages[0].Role);
        Assert.Equal("assistant", messages[1].Role);
        Assert.Equal("user", messages[2].Role);
        Assert.Equal("assistant", messages[3].Role);
    }

    [Fact]
    public async Task GetConversations_Returns_UserConversations_Ordered_By_CreatedAt_Desc()
    {
        var collectionId = DevDatabaseSeeder.DefaultCollectionId;
        const string text = "مستند تست لیست گفتگوها.";
        await SeedReadyDocumentWithChunkAsync(collectionId, text);

        using var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Rag:MinSimilarity"] = "0.2"
        });
        using var client = factory.CreateClient();

        // Create 2 conversations
        await client.PostAsJsonAsync("/api/v1/ask", new AskRequestDto("گفتگوی اول تستی؟", [collectionId]));
        await client.PostAsJsonAsync("/api/v1/ask", new AskRequestDto("گفتگوی دوم تستی؟", [collectionId]));

        var response = await client.GetAsync("/api/v1/conversations?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var list = await response.Content.ReadFromJsonAsync<List<ConversationSummaryDto>>(_jsonOptions);
        Assert.NotNull(list);
        Assert.True(list.Count >= 2);
        // Verify ordered by most recent (descending)
        Assert.True(list[0].CreatedAt >= list[1].CreatedAt);
    }

    [Fact]
    public async Task Ask_With_Another_Users_ConversationId_Returns_404()
    {
        await using var db = fixture.CreateDbContext();
        var collectionId = DevDatabaseSeeder.DefaultCollectionId;
        var userAId = Guid.NewGuid().ToString();

        // Create a conversation belonging to User A
        var userA = new ApplicationUser
        {
            Id = userAId,
            UserName = $"{userAId}@test.local",
            Email = $"{userAId}@test.local"
        };
        await db.Users.AddAsync(userA);

        var convA = new Conversation
        {
            Id = Guid.CreateVersion7(),
            UserId = userAId,
            Title = "User A Private Conversation",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.Conversations.AddAsync(convA);
        await db.SaveChangesAsync();

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient(); // Default user is DevCurrentUser

        // DevCurrentUser attempts to post into User A's conversation
        var payload = new AskRequestDto(
            Question: "تلاش برای دسترسی به گفتگوی کاربر دیگر؟",
            CollectionIds: [collectionId],
            ConversationId: convA.Id);

        var response = await client.PostAsJsonAsync("/api/v1/ask", payload);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Get_And_Delete_Another_Users_Conversation_Returns_404()
    {
        await using var db = fixture.CreateDbContext();
        var userAId = Guid.NewGuid().ToString();

        var userA = new ApplicationUser
        {
            Id = userAId,
            UserName = $"{userAId}@test.local",
            Email = $"{userAId}@test.local"
        };
        await db.Users.AddAsync(userA);

        var convA = new Conversation
        {
            Id = Guid.CreateVersion7(),
            UserId = userAId,
            Title = "User A Conversation",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.Conversations.AddAsync(convA);
        await db.SaveChangesAsync();

        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient(); // Default user is DevCurrentUser

        // GET another user's messages -> 404
        var getResp = await client.GetAsync($"/api/v1/conversations/{convA.Id}/messages");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        // DELETE another user's conversation -> 404
        var delResp = await client.DeleteAsync($"/api/v1/conversations/{convA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, delResp.StatusCode);
    }

    [Fact]
    public async Task Admin_Accessing_Another_Users_Conversation_Returns_404()
    {
        await using var db = fixture.CreateDbContext();
        var userAId = Guid.NewGuid().ToString();

        var userA = new ApplicationUser
        {
            Id = userAId,
            UserName = $"{userAId}@test.local",
            Email = $"{userAId}@test.local"
        };
        await db.Users.AddAsync(userA);

        var convA = new Conversation
        {
            Id = Guid.CreateVersion7(),
            UserId = userAId,
            Title = "User A Private Conversation",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.Conversations.AddAsync(convA);
        await db.SaveChangesAsync();

        var adminUserId = Guid.NewGuid().ToString();
        var adminUser = new ApplicationUser
        {
            Id = adminUserId,
            UserName = $"{adminUserId}@test.local",
            Email = $"{adminUserId}@test.local"
        };
        await db.Users.AddAsync(adminUser);
        await db.SaveChangesAsync();

        using var factory = fixture.CreateFactory(services =>
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ICurrentUser));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }
            services.AddScoped<ICurrentUser>(_ => new TestCurrentUser(adminUserId, $"{adminUserId}@test.local", isAdmin: true));
        });
        using var client = factory.CreateClient();

        // Admin attempting to GET another user's messages -> 404
        var getResp = await client.GetAsync($"/api/v1/conversations/{convA.Id}/messages");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);

        // Admin attempting to DELETE another user's conversation -> 404
        var delResp = await client.DeleteAsync($"/api/v1/conversations/{convA.Id}");
        Assert.Equal(HttpStatusCode.NotFound, delResp.StatusCode);
    }
}
