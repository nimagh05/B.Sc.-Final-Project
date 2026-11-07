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
        Assert.Equal(0, _vectorStore.HybridSearchCallCount);
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
        Assert.Equal(1, _vectorStore.HybridSearchCallCount);
        Assert.NotNull(_vectorStore.LastHybridSearchRequest);

        Assert.Equal("bge-m3", _vectorStore.LastHybridSearchRequest.EmbeddingModel);
        Assert.False(_vectorStore.LastHybridSearchRequest.UseHybrid);
        Assert.Equal(10, _vectorStore.LastHybridSearchRequest.CandidateK);
        Assert.Equal(2, _vectorStore.LastHybridSearchRequest.TopK);
        Assert.Contains(collectionId, _vectorStore.LastHybridSearchRequest.CollectionIds);

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
        Assert.Equal(3, _vectorStore.LastHybridSearchRequest!.TopK);
    }

    [Fact]
    public async Task RetrieveAsync_HybridOn_FusesVectorAndFtsAndRefLists_WithRrf()
    {
        var hybridOptions = Microsoft.Extensions.Options.Options.Create(new RagOptions
        {
            TopK = 3,
            CandidateK = 10,
            UseHybrid = true,
            RrfK = 60
        });

        var retriever = new Retriever(_embedder, _vectorStore, hybridOptions, _aiOptions);
        var collectionId = Guid.NewGuid();

        var chunkA = CreateCandidateChunk(Guid.NewGuid(), "ماده 12", "متن ماده 12 در مورد مرخصی تحصیلی دانشجو", ["ماده 12"], 0.70, null);
        var chunkB = CreateCandidateChunk(Guid.NewGuid(), "ماده 5", "متن ماده 5 در مورد ثبت نام دانشجو", ["ماده 5"], 0.90, null);
        var chunkC = CreateCandidateChunk(Guid.NewGuid(), "ماده 12", "تبصره 1 ماده 12 مرخصی زایمان", ["ماده 12"], 0.60, 0.5);

        // chunkC is in Ref candidates, FTS candidates, and vector candidates
        _vectorStore.HybridSearchResultToReturn = new HybridSearchResult(
            VectorCandidates: [chunkB, chunkA, chunkC],
            FtsCandidates: [chunkC, chunkA],
            RefCandidates: [chunkC, chunkA],
            BestVectorScore: 0.90);

        var result = await retriever.RetrieveAsync("ماده 12 مرخصی تحصیلی", [collectionId], topK: 3, CancellationToken.None);

        Assert.Equal(3, result.Chunks.Count);
        Assert.Equal(0.90, result.BestVectorScore);
        Assert.True(result.HasFtsHit);

        // ChunkA and ChunkC both appeared in multiple lists and receive RRF boosts
        Assert.Contains(result.Chunks, c => c.ChunkId == chunkA.ChunkId);
        Assert.Contains(result.Chunks, c => c.ChunkId == chunkC.ChunkId);
    }

    [Fact]
    public async Task RetrieveAsync_HybridOn_NoteSpecified_OrdersNoteMatchingChunksFirst()
    {
        var hybridOptions = Microsoft.Extensions.Options.Options.Create(new RagOptions
        {
            TopK = 3,
            CandidateK = 10,
            UseHybrid = true,
            RrfK = 60
        });

        var retriever = new Retriever(_embedder, _vectorStore, hybridOptions, _aiOptions);
        var collectionId = Guid.NewGuid();

        // Chunk1 has ماده 7 without تبصره 1 (higher vector score 0.85)
        var chunkWithoutNoteId = Guid.NewGuid();
        var chunkWithoutNote = CreateCandidateChunk(chunkWithoutNoteId, "ماده 7", "متن ماده 7 آیین نامه آموزشی در مورد حداقل نمره قبولی", ["ماده 7"], 0.85, null);
        // Chunk2 has ماده 7 WITH تبصره 1 (lower vector score 0.75, but matches note)
        var chunkWithNoteId = Guid.NewGuid();
        var chunkWithNote = CreateCandidateChunk(chunkWithNoteId, "ماده 7", "ماده 7 حداقل نمره قبولی ۱۰ است. تبصره 1 حداقل نمره برای دانشجویان مشروط.", ["ماده 7"], 0.75, 0.9);

        _vectorStore.HybridSearchResultToReturn = new HybridSearchResult(
            VectorCandidates: [chunkWithoutNote, chunkWithNote],
            FtsCandidates: [chunkWithNote],
            RefCandidates: [chunkWithoutNote, chunkWithNote],
            BestVectorScore: 0.85);

        // Query asks for "تبصره 1 ماده 7"
        var result = await retriever.RetrieveAsync("تبصره 1 ماده 7 آیین نامه آموزشی", [collectionId], topK: 2, CancellationToken.None);

        // In ref list, chunkWithNote comes first because it matches تبصره 1!
        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(chunkWithNoteId, result.Chunks[0].ChunkId);
        Assert.Equal(chunkWithoutNoteId, result.Chunks[1].ChunkId);
        Assert.True(result.HasFtsHit);
    }

    [Fact]
    public async Task RetrieveAsync_OffTopicQuestionWithValidArticleNumber_SetsHasFtsHitFalse()
    {
        var hybridOptions = Microsoft.Extensions.Options.Options.Create(new RagOptions
        {
            TopK = 5,
            CandidateK = 10,
            UseHybrid = true,
            RrfK = 60
        });

        var retriever = new Retriever(_embedder, _vectorStore, hybridOptions, _aiOptions);
        var collectionId = Guid.NewGuid();

        var chunkArticle7 = CreateCandidateChunk(
            Guid.NewGuid(),
            "ماده 7",
            "ماده 7 حداقل نمره قبولی ۱۰ است. تبصره 1 حداقل نمره برای دانشجویان مشروط.",
            ["ماده 7"],
            0.70,
            null);

        _vectorStore.HybridSearchResultToReturn = new HybridSearchResult(
            VectorCandidates: [chunkArticle7],
            FtsCandidates: [],
            RefCandidates: [chunkArticle7],
            BestVectorScore: 0.70);

        var result = await retriever.RetrieveAsync("ماده 7 درباره پیتزا چیست؟", [collectionId], topK: 5, CancellationToken.None);

        Assert.Single(result.Chunks);
        Assert.False(result.HasFtsHit);
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

    private static CandidateChunk CreateCandidateChunk(
        Guid chunkId,
        string sectionTitle,
        string text,
        IReadOnlyList<string> articleKeys,
        double vectorScore,
        double? ftsScore)
    {
        return new CandidateChunk(
            ChunkId: chunkId,
            DocumentId: Guid.NewGuid(),
            FileName: "regulation.pdf",
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: sectionTitle,
            Text: text,
            SearchText: text,
            ArticleKeys: articleKeys,
            VectorScore: vectorScore,
            FtsScore: ftsScore);
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

        public int HybridSearchCallCount { get; private set; }
        public HybridSearchRequest? LastHybridSearchRequest { get; private set; }
        public HybridSearchResult? HybridSearchResultToReturn { get; set; }

        public Task<HybridSearchResult> SearchHybridAsync(HybridSearchRequest request, CancellationToken ct)
        {
            HybridSearchCallCount++;
            LastHybridSearchRequest = request;
            if (HybridSearchResultToReturn != null)
            {
                return Task.FromResult(HybridSearchResultToReturn);
            }

            var vectorCandidates = ChunksToReturn.Select(c => new CandidateChunk(
                c.ChunkId, c.DocumentId, c.FileName, c.PageStart, c.PageEnd, c.SectionTitle, c.Text, c.Text, [], c.VectorScore, c.FtsScore
            )).ToList();
            var bestVector = vectorCandidates.Count > 0 ? vectorCandidates.Max(c => c.VectorScore) : 0.0;
            return Task.FromResult(new HybridSearchResult(vectorCandidates, [], [], bestVector));
        }
    }
}
