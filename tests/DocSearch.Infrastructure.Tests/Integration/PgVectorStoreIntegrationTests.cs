using DocSearch.Application.Abstractions;
using DocSearch.Domain;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using DocSearch.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Integration;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public sealed class PgVectorStoreIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Search_With_Empty_Collection_List_Returns_Empty()
    {
        await using var db = fixture.CreateDbContext();
        var store = new PgVectorStore(db);

        var request = new SearchRequest(
            QueryEmbedding: new float[EmbeddingConstants.VectorDimensions],
            QueryText: "test query",
            CollectionIds: [],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: false);

        var results = await store.SearchAsync(request, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Score_Is_One_Minus_Distance()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);

        // Orthogonal components: 0.6^2 + 0.8^2 = 1.0 (unit vector)
        var chunkVector = CreateUnitVector((0, 0.6f), (1, 0.8f));
        var chunkRecord = CreateChunkRecord(index: 0, chunkVector, "bge-m3", "متن ماده اول");

        await store.AddChunksAsync(document.Id, collection.Id, [chunkRecord], CancellationToken.None);

        // Query unit vector along component 0: dot product = 1.0 * 0.6 + 0.0 * 0.8 = 0.6000
        var queryVector = CreateUnitVector((0, 1.0f));

        var request = new SearchRequest(
            QueryEmbedding: queryVector,
            QueryText: "ماده اول",
            CollectionIds: [collection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: false);

        var results = await store.SearchAsync(request, CancellationToken.None);

        Assert.Single(results);
        var scored = results[0];
        Assert.Equal(document.FileName, scored.FileName);
        // Cosine distance = 1 - 0.6 = 0.4. Score = 1 - distance = 0.6000
        Assert.Equal(0.6000, scored.VectorScore, precision: 4);
        Assert.Equal(scored.VectorScore, scored.FusedScore);
        Assert.Null(scored.FtsScore);
    }

    [Fact]
    public async Task Search_Returns_Nearest_First()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);

        // Create 3 chunks with similarities ~0.95, ~0.70, ~0.30 to query vector [1, 0, 0, ...]
        var vecHigh = CreateUnitVector((0, 0.95f), (1, 0.3122499f));
        var vecMid = CreateUnitVector((0, 0.70f), (1, 0.7141428f));
        var vecLow = CreateUnitVector((0, 0.30f), (1, 0.9539392f));

        var chunkHigh = CreateChunkRecord(0, vecHigh, "bge-m3", "بند اول بسیار مرتبط");
        var chunkMid = CreateChunkRecord(1, vecMid, "bge-m3", "بند دوم تا حدودی مرتبط");
        var chunkLow = CreateChunkRecord(2, vecLow, "bge-m3", "بند سوم با ارتباط کم");

        // Add them in mixed order
        await store.AddChunksAsync(document.Id, collection.Id, [chunkMid, chunkLow, chunkHigh], CancellationToken.None);

        var queryVector = CreateUnitVector((0, 1.0f));
        var request = new SearchRequest(
            QueryEmbedding: queryVector,
            QueryText: "پرسش تستی",
            CollectionIds: [collection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: false);

        var results = await store.SearchAsync(request, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal(chunkHigh.Draft.Text, results[0].Text);
        Assert.Equal(chunkMid.Draft.Text, results[1].Text);
        Assert.Equal(chunkLow.Draft.Text, results[2].Text);

        Assert.True(results[0].VectorScore > results[1].VectorScore);
        Assert.True(results[1].VectorScore > results[2].VectorScore);
    }

    [Fact]
    public async Task Search_Excludes_Other_Collections()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var allowedCollection = await CreateTestCollectionAsync(db, user.Id);
        var restrictedCollection = await CreateTestCollectionAsync(db, user.Id);

        var docAllowed = await CreateTestDocumentAsync(db, allowedCollection.Id, user.Id, DocumentStatus.Ready);
        var docRestricted = await CreateTestDocumentAsync(db, restrictedCollection.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);

        var vec = CreateUnitVector((0, 1.0f));
        var chunkAllowed = CreateChunkRecord(0, vec, "bge-m3", "سند مجاز");
        var chunkRestricted = CreateChunkRecord(0, vec, "bge-m3", "سند غیرمجاز محرمانه");

        await store.AddChunksAsync(docAllowed.Id, allowedCollection.Id, [chunkAllowed], CancellationToken.None);
        await store.AddChunksAsync(docRestricted.Id, restrictedCollection.Id, [chunkRestricted], CancellationToken.None);

        // Search only within allowedCollection
        var request = new SearchRequest(
            QueryEmbedding: vec,
            QueryText: "سند",
            CollectionIds: [allowedCollection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: false);

        var results = await store.SearchAsync(request, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(docAllowed.Id, results[0].DocumentId);
        Assert.Equal("سند مجاز", results[0].Text);
    }

    [Fact]
    public async Task Search_Excludes_Other_Embedding_Models()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);

        var vec = CreateUnitVector((0, 1.0f));
        var chunkBgeM3 = CreateChunkRecord(0, vec, "bge-m3", "متن بردار bge-m3");
        var chunkOther = CreateChunkRecord(1, vec, "e5-multilingual", "متن بردار e5");

        await store.AddChunksAsync(document.Id, collection.Id, [chunkBgeM3, chunkOther], CancellationToken.None);

        var request = new SearchRequest(
            QueryEmbedding: vec,
            QueryText: "متن",
            CollectionIds: [collection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: false);

        var results = await store.SearchAsync(request, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("متن بردار bge-m3", results[0].Text);
    }

    [Fact]
    public async Task Search_Excludes_NonReady_Documents()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);

        var docReady = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);
        var docUploaded = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);
        var docProcessing = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Processing);
        var docFailed = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Failed);

        var store = new PgVectorStore(db);
        var vec = CreateUnitVector((0, 1.0f));

        await store.AddChunksAsync(docReady.Id, collection.Id, [CreateChunkRecord(0, vec, "bge-m3", "سند آماده")], CancellationToken.None);
        await store.AddChunksAsync(docUploaded.Id, collection.Id, [CreateChunkRecord(0, vec, "bge-m3", "سند آپلود شده")], CancellationToken.None);
        await store.AddChunksAsync(docProcessing.Id, collection.Id, [CreateChunkRecord(0, vec, "bge-m3", "سند در حال پردازش")], CancellationToken.None);
        await store.AddChunksAsync(docFailed.Id, collection.Id, [CreateChunkRecord(0, vec, "bge-m3", "سند ناموفق")], CancellationToken.None);

        var request = new SearchRequest(
            QueryEmbedding: vec,
            QueryText: "سند",
            CollectionIds: [collection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 10,
            UseHybrid: false);

        var results = await store.SearchAsync(request, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(docReady.Id, results[0].DocumentId);
        Assert.Equal("سند آماده", results[0].Text);
    }

    [Fact]
    public async Task AddChunksAsync_RollsBack_OnFailure()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);
        var vec = CreateUnitVector((0, 1.0f));

        // Create two chunks with DUPLICATE index 0 for the same document to trigger unique index violation
        var chunk1 = CreateChunkRecord(0, vec, "bge-m3", "قطعه ۱");
        var chunk2Duplicate = CreateChunkRecord(0, vec, "bge-m3", "قطعه ۲ با اندیس تکراری");

        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            store.AddChunksAsync(document.Id, collection.Id, [chunk1, chunk2Duplicate], CancellationToken.None));

        // Verify that transaction rolled back and ZERO chunks were persisted
        await using var verifyDb = fixture.CreateDbContext();
        var count = await verifyDb.Chunks.CountAsync(c => c.DocumentId == document.Id);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AddChunksAsync_Reuses_OuterTransaction_AndRollsBackWithIt()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);
        var vec = CreateUnitVector((0, 1.0f));
        var chunk = CreateChunkRecord(0, vec, "bge-m3", "قطعه موقت تراکنش خارجی");

        // Begin outer transaction
        await using (var outerTx = await db.Database.BeginTransactionAsync())
        {
            await store.AddChunksAsync(document.Id, collection.Id, [chunk], CancellationToken.None);
            // Explicitly roll back outer transaction
            await outerTx.RollbackAsync();
        }

        // Verify zero chunks persisted
        await using var verifyDb = fixture.CreateDbContext();
        var count = await verifyDb.Chunks.CountAsync(c => c.DocumentId == document.Id);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DeleteByDocumentAsync_Deletes_All_Chunks_For_Document()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var doc1 = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);
        var doc2 = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);
        var vec = CreateUnitVector((0, 1.0f));

        await store.AddChunksAsync(doc1.Id, collection.Id, [
            CreateChunkRecord(0, vec, "bge-m3", "سند ۱ قطعه ۱"),
            CreateChunkRecord(1, vec, "bge-m3", "سند ۱ قطعه ۲")
        ], CancellationToken.None);

        await store.AddChunksAsync(doc2.Id, collection.Id, [
            CreateChunkRecord(0, vec, "bge-m3", "سند ۲ قطعه ۱")
        ], CancellationToken.None);

        await using (var checkDb = fixture.CreateDbContext())
        {
            Assert.Equal(2, await checkDb.Chunks.CountAsync(c => c.DocumentId == doc1.Id));
            Assert.Equal(1, await checkDb.Chunks.CountAsync(c => c.DocumentId == doc2.Id));
        }

        // Delete doc1 chunks
        await store.DeleteByDocumentAsync(doc1.Id, CancellationToken.None);

        await using (var verifyDb = fixture.CreateDbContext())
        {
            Assert.Equal(0, await verifyDb.Chunks.CountAsync(c => c.DocumentId == doc1.Id));
            Assert.Equal(1, await verifyDb.Chunks.CountAsync(c => c.DocumentId == doc2.Id));
        }
    }

    [Fact]
    public async Task Exact_Article_Number_Query_Ranks_Article_First()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);
        var store = new PgVectorStore(db);

        // Vector 1 (Chunk 1): Target merged chunk modeling q015.
        // Title is "ماده 5", but ArticleKeys contains ["ماده 5", "ماده 6", "ماده 7"].
        // Body mentions Article 7 deep in text. Cosine vector similarity is lower (~0.60).
        var vecMerged = CreateUnitVector((0, 0.60f), (1, 0.80f));
        var textMerged = "ماده ۵ شرح وظایف مؤسسه آموزشی است و پس از آن ماده ۶ در مورد تقویم دانشگاهی آمده و در ادامه ماده ۷ با تبصره ۱ در مورد حداقل نمره قبولی تدوین شده است.";
        var chunkMerged = CreateChunkRecord(0, vecMerged, "bge-m3", textMerged, ["ماده 5", "ماده 6", "ماده 7"], "ماده ۵");

        // Vector 2 (Chunk 2): Generic university chunk with HIGHER vector similarity (~0.90) but ArticleKey "ماده 2"
        var vecGeneric = CreateUnitVector((0, 0.90f), (1, 0.4358899f));
        var textGeneric = "ماده ۲ در مورد اهداف دوره های کارشناسی دانشگاه و آیین نامه آموزشی است.";
        var chunkGeneric = CreateChunkRecord(1, vecGeneric, "bge-m3", textGeneric, ["ماده 2"], "ماده ۲");

        await store.AddChunksAsync(document.Id, collection.Id, [chunkMerged, chunkGeneric], CancellationToken.None);

        // Query vector points along component 0 (dot product with vecGeneric = 0.90, with vecMerged = 0.60)
        var qVec = CreateUnitVector((0, 1.0f));

        var request = new HybridSearchRequest(
            QueryEmbedding: qVec,
            QueryText: "تبصره ۱ ماده ۷ آیین نامه آموزشی",
            TsQuery: "ماده | 7 | ماده7 | تبصره | 1 | تبصره1",
            ExtractedArticleKeys: ["ماده 7"],
            ExtractedNoteNumber: 1,
            CollectionIds: [collection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: true);

        var searchResult = await store.SearchHybridAsync(request, CancellationToken.None);

        // Chunk 1 MUST appear in RefCandidates via GIN article_keys query
        Assert.Contains(searchResult.RefCandidates, c => c.ArticleKeys.Contains("ماده 7"));

        // In pure vector search, chunkGeneric (0.90) beats chunkMerged (0.60).
        // But when fused in Retriever with RRF, chunkMerged gains a ref rank boost and ranks #1!
        var mockEmbedder = new StubQueryEmbedder(qVec);
        var retriever = new DocSearch.Application.Retrieval.Retriever(
            mockEmbedder,
            store,
            Microsoft.Extensions.Options.Options.Create(new DocSearch.Application.Options.RagOptions { TopK = 2, UseHybrid = true, RrfK = 60 }),
            Microsoft.Extensions.Options.Options.Create(new DocSearch.Application.Options.AiOptions { Embedding = new DocSearch.Application.Options.AiEmbeddingOptions { Model = "bge-m3" } }));

        var retrievalResult = await retriever.RetrieveAsync("تبصره ۱ ماده ۷ آیین نامه آموزشی", [collection.Id], 2, CancellationToken.None);

        Assert.True(retrievalResult.Chunks.Count >= 2);
        // Target article chunk ranked first!
        Assert.Contains("ماده ۷", retrievalResult.Chunks[0].Text);
    }

    [Fact]
    public async Task Hybrid_Off_Equals_Vector_Only()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);
        var store = new PgVectorStore(db);

        var vec1 = CreateUnitVector((0, 0.95f), (1, 0.3122f));
        var vec2 = CreateUnitVector((0, 0.75f), (1, 0.6614f));

        await store.AddChunksAsync(document.Id, collection.Id, [
            CreateChunkRecord(0, vec1, "bge-m3", "متن قطعه اول با شباهت بالا", ["ماده 1"]),
            CreateChunkRecord(1, vec2, "bge-m3", "متن قطعه دوم با شباهت کمتر", ["ماده 2"])
        ], CancellationToken.None);

        var qVec = CreateUnitVector((0, 1.0f));

        var hybridReqOff = new HybridSearchRequest(
            QueryEmbedding: qVec,
            QueryText: "ماده 2",
            TsQuery: "ماده | 2",
            ExtractedArticleKeys: ["ماده 2"],
            ExtractedNoteNumber: null,
            CollectionIds: [collection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: false);

        var searchResult = await store.SearchHybridAsync(hybridReqOff, CancellationToken.None);

        Assert.Equal(2, searchResult.VectorCandidates.Count);
        Assert.Empty(searchResult.FtsCandidates);
        Assert.Empty(searchResult.RefCandidates);
        Assert.Equal(0.95, searchResult.BestVectorScore, precision: 2);
    }

    [Fact]
    public async Task Fts_Only_Match_Is_Included()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);
        var store = new PgVectorStore(db);

        // Chunk with low vector similarity (~0.10) but distinct keyword
        var vecLow = CreateUnitVector((0, 0.10f), (1, 0.9949f));
        var textFts = "شرایط دانشنامه و گواهی موقت فارغ التحصیلی دانشجویان مقطع کارشناسی.";
        var chunkFts = CreateChunkRecord(0, vecLow, "bge-m3", textFts);

        await store.AddChunksAsync(document.Id, collection.Id, [chunkFts], CancellationToken.None);

        var qVec = CreateUnitVector((0, 1.0f));
        var request = new HybridSearchRequest(
            QueryEmbedding: qVec,
            QueryText: "گواهی موقت فارغ التحصیلی",
            TsQuery: "گواهی | موقت | فارغ | التحصیلی",
            ExtractedArticleKeys: [],
            ExtractedNoteNumber: null,
            CollectionIds: [collection.Id],
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 5,
            UseHybrid: true);

        var searchResult = await store.SearchHybridAsync(request, CancellationToken.None);

        Assert.Single(searchResult.FtsCandidates);
        var ftsHit = searchResult.FtsCandidates[0];
        Assert.Equal(textFts, ftsHit.Text);
        Assert.NotNull(ftsHit.FtsScore);
        Assert.True(ftsHit.FtsScore > 0);
        // VectorScore MUST be populated with real cosine distance (1 - distance = 0.10), NOT 0.0
        Assert.Equal(0.10, ftsHit.VectorScore, precision: 2);
    }

    [Fact]
    public async Task ACL_Still_Enforced_In_Hybrid()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);

        var allowedCol = await CreateTestCollectionAsync(db, user.Id);
        var forbiddenCol = await CreateTestCollectionAsync(db, user.Id);

        var docAllowed = await CreateTestDocumentAsync(db, allowedCol.Id, user.Id, DocumentStatus.Ready);
        var docForbidden = await CreateTestDocumentAsync(db, forbiddenCol.Id, user.Id, DocumentStatus.Ready);

        var store = new PgVectorStore(db);
        var vec = CreateUnitVector((0, 1.0f));

        var allowedChunk = CreateChunkRecord(0, vec, "bge-m3", "ماده 12 محرمانه مجاز", ["ماده 12"]);
        var forbiddenChunk = CreateChunkRecord(0, vec, "bge-m3", "ماده 12 محرمانه غیرمجاز", ["ماده 12"]);

        await store.AddChunksAsync(docAllowed.Id, allowedCol.Id, [allowedChunk], CancellationToken.None);
        await store.AddChunksAsync(docForbidden.Id, forbiddenCol.Id, [forbiddenChunk], CancellationToken.None);

        var request = new HybridSearchRequest(
            QueryEmbedding: vec,
            QueryText: "ماده 12 محرمانه",
            TsQuery: "ماده | 12 | ماده12 | محرمانه",
            ExtractedArticleKeys: ["ماده 12"],
            ExtractedNoteNumber: null,
            CollectionIds: [allowedCol.Id], // Only allowedCol
            EmbeddingModel: "bge-m3",
            CandidateK: 10,
            TopK: 10,
            UseHybrid: true);

        var searchResult = await store.SearchHybridAsync(request, CancellationToken.None);

        // VectorCandidates, FtsCandidates, and RefCandidates must ONLY contain chunks from docAllowed
        Assert.All(searchResult.VectorCandidates, c => Assert.Equal(docAllowed.Id, c.DocumentId));
        Assert.All(searchResult.FtsCandidates, c => Assert.Equal(docAllowed.Id, c.DocumentId));
        Assert.All(searchResult.RefCandidates, c => Assert.Equal(docAllowed.Id, c.DocumentId));
    }

    private sealed class StubQueryEmbedder(float[] vector) : IQueryEmbedder
    {
        public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => Task.FromResult(vector);
    }

    private static float[] CreateUnitVector(params (int index, float value)[] components)
    {
        var vec = new float[EmbeddingConstants.VectorDimensions];
        foreach (var (index, value) in components)
        {
            vec[index] = value;
        }
        return vec;
    }

    private static ChunkRecord CreateChunkRecord(
        int index,
        float[] embedding,
        string model,
        string text,
        string[]? articleKeys = null,
        string? sectionTitle = "ماده ۱")
    {
        var draft = new ChunkDraft(
            Index: index,
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: sectionTitle,
            Text: text,
            SearchText: text,
            EmbeddingText: text,
            TokenCount: 10,
            ArticleKeys: articleKeys);

        return new ChunkRecord(draft, embedding, model);
    }

    private static async Task<ApplicationUser> CreateTestUserAsync(AppDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7().ToString(),
            UserName = $"user_{Guid.NewGuid():N}@test.com",
            Email = $"user_{Guid.NewGuid():N}@test.com",
            NormalizedUserName = $"USER_{Guid.NewGuid():N}@TEST.COM",
            NormalizedEmail = $"USER_{Guid.NewGuid():N}@TEST.COM",
            SecurityStamp = Guid.NewGuid().ToString()
        };

        await db.Users.AddAsync(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Collection> CreateTestCollectionAsync(AppDbContext db, string ownerId)
    {
        var collection = new Collection
        {
            Id = Guid.CreateVersion7(),
            Name = $"مجموعه {Guid.NewGuid():N}",
            Description = "مجموعه تست بردار",
            OwnerId = ownerId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await db.Collections.AddAsync(collection);
        await db.SaveChangesAsync();
        return collection;
    }

    private static async Task<Document> CreateTestDocumentAsync(
        AppDbContext db,
        Guid collectionId,
        string uploadedBy,
        DocumentStatus status)
    {
        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collectionId,
            FileName = $"doc_{Guid.NewGuid():N}.pdf",
            StoragePath = $"storage/{Guid.NewGuid():N}.pdf",
            ContentType = "application/pdf",
            SizeBytes = 4096,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PageCount = 5,
            Status = status,
            UploadedBy = uploadedBy,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await db.Documents.AddAsync(document);
        await db.SaveChangesAsync();
        return document;
    }
}
