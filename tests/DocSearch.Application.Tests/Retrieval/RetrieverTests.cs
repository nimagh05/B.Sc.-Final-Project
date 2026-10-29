using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Application.Retrieval;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Application.Tests.Retrieval;

public sealed class RetrieverTests
{
    private readonly StubQueryEmbedder _embedder = new();
    private readonly StubVectorStore _vectorStore = new();
    private readonly IOptions<RagOptions> _ragOptions = Microsoft.Extensions.Options.Options.Create(new RagOptions
    {
        TopK = 3,
        CandidateK = 10,
        UseHybrid = false
    });
    private readonly IOptions<AiOptions> _aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
    {
        Embedding = new AiEmbeddingOptions
        {
            Model = "bge-m3"
        }
    });

    [Fact]
    public async Task RetrieveAsync_NullOrWhitespaceQuestion_ThrowsArgumentException()
    {
        var retriever = new Retriever(_embedder, _vectorStore, _ragOptions, _aiOptions);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            retriever.RetrieveAsync(null!, [Guid.NewGuid()], topK: 3, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            retriever.RetrieveAsync("   ", [Guid.NewGuid()], topK: 3, CancellationToken.None));
    }

    [Fact]
    public async Task RetrieveAsync_EmptyCollectionIds_ReturnsEmptyWithoutCallingEmbedder()
    {
        var retriever = new Retriever(_embedder, _vectorStore, _ragOptions, _aiOptions);

        var result = await retriever.RetrieveAsync("پرسش تستی", [], topK: 3, CancellationToken.None);

        Assert.Empty(result.Chunks);
        Assert.Equal(0.0, result.BestVectorScore);
        Assert.False(result.HasFtsHit);
        Assert.Equal(0, _embedder.CallCount);
        Assert.Equal(0, _vectorStore.SearchCallCount);
    }

    [Fact]
    public async Task RetrieveAsync_ValidQuestion_EmbedsQueryAndCallsVectorStore()
    {
        var retriever = new Retriever(_embedder, _vectorStore, _ragOptions, _aiOptions);
        var collectionId = Guid.NewGuid();

        _vectorStore.ChunksToReturn =
        [
            CreateScoredChunk(Guid.NewGuid(), 0.95),
            CreateScoredChunk(Guid.NewGuid(), 0.85),
            CreateScoredChunk(Guid.NewGuid(), 0.75),
            CreateScoredChunk(Guid.NewGuid(), 0.65)
        ];

        var result = await retriever.RetrieveAsync("آیین نامه آموزشی", [collectionId], topK: 2, CancellationToken.None);

        Assert.Equal(1, _embedder.CallCount);
        Assert.Equal("آیین نامه آموزشی", _embedder.LastQuery);
        Assert.Equal(1, _vectorStore.SearchCallCount);
        Assert.NotNull(_vectorStore.LastSearchRequest);

        Assert.Equal("bge-m3", _vectorStore.LastSearchRequest.EmbeddingModel);
        Assert.False(_vectorStore.LastSearchRequest.UseHybrid);
        Assert.Equal(10, _vectorStore.LastSearchRequest.CandidateK);
        Assert.Equal(2, _vectorStore.LastSearchRequest.TopK);
        Assert.Contains(collectionId, _vectorStore.LastSearchRequest.CollectionIds);

        // TopK was 2, so only 2 chunks returned
        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(0.95, result.Chunks[0].VectorScore);
        Assert.Equal(0.85, result.Chunks[1].VectorScore);
        // BestVectorScore is max score across candidates
        Assert.Equal(0.95, result.BestVectorScore);
        Assert.False(result.HasFtsHit);
    }

    [Fact]
    public async Task RetrieveAsync_DefaultTopK_FromOptions()
    {
        var retriever = new Retriever(_embedder, _vectorStore, _ragOptions, _aiOptions);
        var collectionId = Guid.NewGuid();

        _vectorStore.ChunksToReturn =
        [
            CreateScoredChunk(Guid.NewGuid(), 0.90),
            CreateScoredChunk(Guid.NewGuid(), 0.80),
            CreateScoredChunk(Guid.NewGuid(), 0.70),
            CreateScoredChunk(Guid.NewGuid(), 0.60)
        ];

        // Passing null for topK should fall back to ragOptions.Value.TopK (which is 3)
        var result = await retriever.RetrieveAsync("شرایط دفاع پایان نامه", [collectionId], topK: null, CancellationToken.None);

        Assert.Equal(3, result.Chunks.Count);
        Assert.Equal(3, _vectorStore.LastSearchRequest!.TopK);
    }

    private static ScoredChunk CreateScoredChunk(Guid chunkId, double vectorScore)
    {
        return new ScoredChunk(
            ChunkId: chunkId,
            DocumentId: Guid.NewGuid(),
            FileName: "regulation.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: "ماده ۱",
            Text: "متن تستی",
            VectorScore: vectorScore,
            FtsScore: null,
            FusedScore: vectorScore);
    }

    private sealed class StubQueryEmbedder : IQueryEmbedder
    {
        public int CallCount { get; private set; }
        public string? LastQuery { get; private set; }
        public float[] VectorToReturn { get; set; } = new float[1024];

        public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
        {
            CallCount++;
            LastQuery = query;
            return Task.FromResult(VectorToReturn);
        }
    }

    private sealed class StubVectorStore : IVectorStore
    {
        public int SearchCallCount { get; private set; }
        public SearchRequest? LastSearchRequest { get; private set; }
        public IReadOnlyList<ScoredChunk> ChunksToReturn { get; set; } = [];

        public Task AddChunksAsync(Guid documentId, Guid collectionId, IReadOnlyList<ChunkRecord> chunks, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task DeleteByDocumentAsync(Guid documentId, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScoredChunk>> SearchAsync(SearchRequest request, CancellationToken ct)
        {
            SearchCallCount++;
            LastSearchRequest = request;
            return Task.FromResult(ChunksToReturn);
        }
    }
}
