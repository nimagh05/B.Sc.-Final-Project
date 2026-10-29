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

    private static float[] CreateUnitVector(params (int index, float value)[] components)
    {
        var vec = new float[EmbeddingConstants.VectorDimensions];
        foreach (var (index, value) in components)
        {
            vec[index] = value;
        }
        return vec;
    }

    private static ChunkRecord CreateChunkRecord(int index, float[] embedding, string model, string text)
    {
        var draft = new ChunkDraft(
            Index: index,
            PageStart: 1,
            PageEnd: 1,
            SectionTitle: "ماده ۱",
            Text: text,
            SearchText: text,
            EmbeddingText: text,
            TokenCount: 10);

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
