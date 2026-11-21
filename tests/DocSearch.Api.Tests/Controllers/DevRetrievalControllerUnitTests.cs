using DocSearch.Api.Controllers;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Application.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Api.Tests.Controllers;

public sealed class DevRetrievalControllerUnitTests
{
    private sealed class StubCurrentUser(string userId) : ICurrentUser
    {
        public string UserId { get; } = userId;
        public string Email => "user@test.local";
        public bool IsAdmin => false;
    }

    private sealed class StubAccessService(IReadOnlyCollection<Guid> readableIds) : IAccessService
    {
        public Task<IReadOnlyCollection<Guid>> GetReadableCollectionIdsAsync(
            string userId,
            IReadOnlyCollection<Guid>? requested,
            CancellationToken ct)
        {
            var result = requested == null
                ? readableIds
                : readableIds.Intersect(requested).ToList();
            return Task.FromResult<IReadOnlyCollection<Guid>>(result);
        }

        public Task EnsureAccessAsync(
            string userId,
            Guid collectionId,
            DocSearch.Domain.CollectionRole requiredRole,
            CancellationToken ct,
            string? notFoundMessage = null) => Task.CompletedTask;

        public Task<DocSearch.Domain.CollectionRole?> GetCollectionRoleAsync(
            string userId,
            Guid collectionId,
            CancellationToken ct)
            => Task.FromResult<DocSearch.Domain.CollectionRole?>(DocSearch.Domain.CollectionRole.Owner);
    }

    private sealed class StubRetriever(RetrievalResult result) : IRetriever
    {
        public bool? LastUseHybrid { get; private set; }
        public int? LastTopK { get; private set; }
        public string? LastQuestion { get; private set; }

        public Task<RetrievalResult> RetrieveAsync(
            string question,
            IReadOnlyCollection<Guid> collectionIds,
            int? topK,
            CancellationToken ct)
            => RetrieveAsync(question, collectionIds, topK, null, ct);

        public Task<RetrievalResult> RetrieveAsync(
            string question,
            IReadOnlyCollection<Guid> collectionIds,
            int? topK,
            bool? useHybrid,
            CancellationToken ct)
        {
            LastQuestion = question;
            LastTopK = topK;
            LastUseHybrid = useHybrid;
            return Task.FromResult(result);
        }
    }

    private sealed class StubRagService(AnswerResult? result = null) : IRagService
    {
        public Task<AnswerResult> AskAsync(AskRequest request, CancellationToken ct)
            => Task.FromResult(result ?? new AnswerResult(null, false, false, [], null));
    }

    [Fact]
    public async Task Retrieve_WhenEvalEndpointsDisabled_ThrowsNotFoundException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiagnosticsOptions { EnableEvalEndpoints = false });
        var controller = new AskController(
            new StubRagService(),
            new StubRetriever(new RetrievalResult([], 0.0, false)),
            new StubAccessService([]),
            new StubCurrentUser("u1"),
            options,
            NullLogger<AskController>.Instance);

        var request = new DevRetrievalRequestDto(
            Question: "تعداد واحد مجاز",
            CollectionIds: [Guid.NewGuid()],
            TopK: 10,
            UseHybrid: true);

        await Assert.ThrowsAsync<NotFoundException>(() => controller.Retrieve(request, CancellationToken.None));
    }

    [Fact]
    public async Task Retrieve_WhenRequestIsNull_ThrowsValidationException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiagnosticsOptions { EnableEvalEndpoints = true });
        var controller = new AskController(
            new StubRagService(),
            new StubRetriever(new RetrievalResult([], 0.0, false)),
            new StubAccessService([]),
            new StubCurrentUser("u1"),
            options,
            NullLogger<AskController>.Instance);

        await Assert.ThrowsAsync<ValidationException>(() => controller.Retrieve(null, CancellationToken.None));
    }

    [Fact]
    public async Task Retrieve_WhenQuestionTooShort_ThrowsValidationException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiagnosticsOptions { EnableEvalEndpoints = true });
        var controller = new AskController(
            new StubRagService(),
            new StubRetriever(new RetrievalResult([], 0.0, false)),
            new StubAccessService([]),
            new StubCurrentUser("u1"),
            options,
            NullLogger<AskController>.Instance);

        var request = new DevRetrievalRequestDto(
            Question: "ab",
            CollectionIds: [Guid.NewGuid()]);

        await Assert.ThrowsAsync<ValidationException>(() => controller.Retrieve(request, CancellationToken.None));
    }

    [Fact]
    public async Task Retrieve_WhenNoReadableCollections_ThrowsNotFoundException()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiagnosticsOptions { EnableEvalEndpoints = true });
        var collectionId = Guid.NewGuid();

        var controller = new AskController(
            new StubRagService(),
            new StubRetriever(new RetrievalResult([], 0.0, false)),
            new StubAccessService([]), // None readable
            new StubCurrentUser("u1"),
            options,
            NullLogger<AskController>.Instance);

        var request = new DevRetrievalRequestDto(
            Question: "تعداد واحد مجاز",
            CollectionIds: [collectionId]);

        await Assert.ThrowsAsync<NotFoundException>(() => controller.Retrieve(request, CancellationToken.None));
    }

    [Fact]
    public async Task Retrieve_WhenEnabled_PassesUseHybridAndReturnsChunksWithScores()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiagnosticsOptions { EnableEvalEndpoints = true });
        var collectionId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();

        var scoredChunks = new List<ScoredChunk>
        {
            new(chunkId, docId, "aeen.pdf", 1, 2, "ماده 1", "متن ماده یک", 0.85, 0.4, 0.032)
        };
        var retrievalResult = new RetrievalResult(scoredChunks, 0.85, HasFtsHit: true);
        var stubRetriever = new StubRetriever(retrievalResult);

        var controller = new AskController(
            new StubRagService(),
            stubRetriever,
            new StubAccessService([collectionId]),
            new StubCurrentUser("u1"),
            options,
            NullLogger<AskController>.Instance);

        var request = new DevRetrievalRequestDto(
            Question: "تعداد واحد مجاز",
            CollectionIds: [collectionId],
            TopK: 10,
            UseHybrid: true);

        var actionResult = await controller.Retrieve(request, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var response = Assert.IsType<DevRetrievalResponse>(okResult.Value);

        Assert.Equal(0.85, response.BestVectorScore);
        Assert.True(response.HasFtsHit);
        Assert.True(stubRetriever.LastUseHybrid);
        Assert.Equal(10, stubRetriever.LastTopK);
        Assert.Equal("تعداد واحد مجاز", stubRetriever.LastQuestion);
        Assert.Single(response.Chunks);

        var chunk = response.Chunks[0];
        Assert.Equal(chunkId, chunk.ChunkId);
        Assert.Equal(docId, chunk.DocumentId);
        Assert.Equal("aeen.pdf", chunk.FileName);
        Assert.Equal(1, chunk.PageStart);
        Assert.Equal(2, chunk.PageEnd);
        Assert.Equal(0.032, chunk.Score);
        Assert.Equal(0.85, chunk.VectorScore);
        Assert.Equal(0.4, chunk.FtsScore);
        Assert.Equal(1, chunk.Rank);
    }

    [Fact]
    public async Task Ask_WhenExposeTimingsDisabled_DoesNotSetTimingHeaders()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiagnosticsOptions
        {
            EnableEvalEndpoints = false,
            ExposeTimings = false
        });

        var stubResult = new AnswerResult(
            Answer: "پاسخ نمونه",
            NotFound: false,
            Degraded: false,
            Sources: [],
            ConversationId: Guid.NewGuid(),
            EmbedRetrieveMs: 125,
            LlmMs: 450);

        var controller = new AskController(
            new StubRagService(stubResult),
            new StubRetriever(new RetrievalResult([], 0.0, false)),
            new StubAccessService([]),
            new StubCurrentUser("u1"),
            options,
            NullLogger<AskController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var request = new AskRequestDto("آیا دوره تابستانی مجاز است؟", [Guid.NewGuid()]);
        var actionResult = await controller.Ask(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(actionResult);

        Assert.False(controller.Response.Headers.ContainsKey("X-Timing-Retrieval-Ms"));
        Assert.False(controller.Response.Headers.ContainsKey("X-Timing-Generation-Ms"));
    }

    [Fact]
    public async Task Ask_WhenExposeTimingsEnabled_SetsTimingHeaders()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiagnosticsOptions
        {
            EnableEvalEndpoints = false,
            ExposeTimings = true
        });

        var stubResult = new AnswerResult(
            Answer: "پاسخ نمونه",
            NotFound: false,
            Degraded: false,
            Sources: [],
            ConversationId: Guid.NewGuid(),
            EmbedRetrieveMs: 125,
            LlmMs: 450);

        var controller = new AskController(
            new StubRagService(stubResult),
            new StubRetriever(new RetrievalResult([], 0.0, false)),
            new StubAccessService([]),
            new StubCurrentUser("u1"),
            options,
            NullLogger<AskController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var request = new AskRequestDto("آیا دوره تابستانی مجاز است؟", [Guid.NewGuid()]);
        var actionResult = await controller.Ask(request, CancellationToken.None);
        Assert.IsType<OkObjectResult>(actionResult);

        Assert.True(controller.Response.Headers.TryGetValue("X-Timing-Retrieval-Ms", out var retrievalMs));
        Assert.Equal("125", retrievalMs.ToString());

        Assert.True(controller.Response.Headers.TryGetValue("X-Timing-Generation-Ms", out var llmMs));
        Assert.Equal("450", llmMs.ToString());
    }
}
