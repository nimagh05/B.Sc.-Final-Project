using DocSearch.Domain;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using DocSearch.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Xunit;

namespace DocSearch.Infrastructure.Tests;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public sealed class PersistenceIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Migration_Applies_OnEmptyDatabase()
    {
        var dbName = $"empty_db_{Guid.NewGuid():N}";
        var emptyDbConn = await fixture.CreateEmptyDatabaseAsync(dbName);

        await using var db = fixture.CreateDbContext(emptyDbConn);

        var pendingBefore = (await db.Database.GetPendingMigrationsAsync()).ToList();
        Assert.NotEmpty(pendingBefore);

        await db.Database.MigrateAsync();

        var pendingAfter = (await db.Database.GetPendingMigrationsAsync()).ToList();
        Assert.Empty(pendingAfter);
    }

    [Fact]
    public async Task Chunk_RoundTrip_PreservesEmbedding()
    {
        await using var db = fixture.CreateDbContext();

        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id);

        var expectedEmbedding = new float[EmbeddingConstants.VectorDimensions];
        for (int i = 0; i < expectedEmbedding.Length; i++)
        {
            expectedEmbedding[i] = (float)Math.Round((i % 19) * 0.05f, 4);
        }

        var chunk = new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = document.Id,
            CollectionId = collection.Id,
            ChunkIndex = 0,
            PageStart = 1,
            PageEnd = 2,
            SectionTitle = "ماده ۱ - تعاریف",
            Text = "متن ماده اول آیین نامه آموزشی برای تست بردار",
            SearchText = "متن ماده اول آیین نامه آموزشی برای تست بردار",
            TokenCount = 20,
            Embedding = new Vector(expectedEmbedding),
            EmbeddingModel = "bge-m3"
        };

        await db.Chunks.AddAsync(chunk);
        await db.SaveChangesAsync();

        await using var readDb = fixture.CreateDbContext();
        var loadedChunk = await readDb.Chunks.FindAsync(chunk.Id);

        Assert.NotNull(loadedChunk);
        Assert.Equal(chunk.DocumentId, loadedChunk.DocumentId);
        Assert.Equal(chunk.CollectionId, loadedChunk.CollectionId);
        Assert.Equal(chunk.SectionTitle, loadedChunk.SectionTitle);
        Assert.Equal(expectedEmbedding, loadedChunk.Embedding.ToArray());
    }

    [Fact]
    public async Task Chunk_Tsv_IsGeneratedFromSearchText()
    {
        await using var db = fixture.CreateDbContext();

        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id);

        var chunk = new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = document.Id,
            CollectionId = collection.Id,
            ChunkIndex = 0,
            PageStart = 3,
            PageEnd = 3,
            SectionTitle = "ماده ۴ - شرایط پذیرش",
            Text = "دانشگاه تهران مقررات پذیرش دانشجو در مقطع کارشناسی",
            SearchText = "دانشگاه تهران مقررات پذیرش دانشجو در مقطع کارشناسی",
            TokenCount = 18,
            Embedding = new Vector(new float[EmbeddingConstants.VectorDimensions]),
            EmbeddingModel = "bge-m3"
        };

        await db.Chunks.AddAsync(chunk);
        await db.SaveChangesAsync();

        await using var readDb = fixture.CreateDbContext();
        var loadedChunk = await readDb.Chunks.FindAsync(chunk.Id);

        Assert.NotNull(loadedChunk);
        Assert.NotNull(loadedChunk.Tsv);

        // Verify full-text search matching using the generated stored tsvector column
        var matchCount = await readDb.Chunks
            .FromSqlInterpolated($"SELECT * FROM chunks WHERE id = {chunk.Id} AND tsv @@ to_tsquery('simple', 'تهران')")
            .CountAsync();

        Assert.Equal(1, matchCount);
    }

    [Fact]
    public async Task Document_Duplicate_Hash_InSameCollection_IsRejected()
    {
        await using var db = fixture.CreateDbContext();

        var user = await CreateTestUserAsync(db);
        var collection1 = await CreateTestCollectionAsync(db, user.Id);
        var collection2 = await CreateTestCollectionAsync(db, user.Id);

        var sharedHash = new string('a', 64);

        var doc1 = await CreateTestDocumentAsync(db, collection1.Id, user.Id, sharedHash);

        var doc2SameCollection = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection1.Id,
            FileName = "duplicate.pdf",
            StoragePath = "storage/duplicate.pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            ContentHash = sharedHash,
            Status = DocumentStatus.Uploaded,
            UploadedBy = user.Id
        };

        await db.Documents.AddAsync(doc2SameCollection);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains("IX_documents_collection_id_content_hash", exception.InnerException?.Message ?? exception.Message);

        // Verify that same hash in a DIFFERENT collection is allowed
        await using var newDb = fixture.CreateDbContext();
        var doc3DifferentCollection = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection2.Id,
            FileName = "other_collection.pdf",
            StoragePath = "storage/other_collection.pdf",
            ContentType = "application/pdf",
            SizeBytes = 2048,
            ContentHash = sharedHash,
            Status = DocumentStatus.Uploaded,
            UploadedBy = user.Id
        };

        await newDb.Documents.AddAsync(doc3DifferentCollection);
        await newDb.SaveChangesAsync();

        var savedDoc3 = await newDb.Documents.FindAsync(doc3DifferentCollection.Id);
        Assert.NotNull(savedDoc3);
    }

    [Fact]
    public async Task Delete_Document_Cascades_Chunks()
    {
        await using var db = fixture.CreateDbContext();

        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id);

        var chunk1 = new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = document.Id,
            CollectionId = collection.Id,
            ChunkIndex = 0,
            PageStart = 1,
            PageEnd = 1,
            Text = "تست بند ۱",
            SearchText = "تست بند ۱",
            TokenCount = 5,
            Embedding = new Vector(new float[EmbeddingConstants.VectorDimensions]),
            EmbeddingModel = "bge-m3"
        };

        var chunk2 = new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = document.Id,
            CollectionId = collection.Id,
            ChunkIndex = 1,
            PageStart = 2,
            PageEnd = 2,
            Text = "تست بند ۲",
            SearchText = "تست بند ۲",
            TokenCount = 5,
            Embedding = new Vector(new float[EmbeddingConstants.VectorDimensions]),
            EmbeddingModel = "bge-m3"
        };

        await db.Chunks.AddRangeAsync(chunk1, chunk2);
        await db.SaveChangesAsync();

        var countBefore = await db.Chunks.CountAsync(c => c.DocumentId == document.Id);
        Assert.Equal(2, countBefore);

        // Delete document
        db.Documents.Remove(document);
        await db.SaveChangesAsync();

        // Verify chunks were cascaded by the database
        await using var verifyDb = fixture.CreateDbContext();
        var deletedDoc = await verifyDb.Documents.FindAsync(document.Id);
        var countAfter = await verifyDb.Chunks.CountAsync(c => c.DocumentId == document.Id);

        Assert.Null(deletedDoc);
        Assert.Equal(0, countAfter);
    }

    [Fact]
    public async Task Check_Constraint_Rejects_InvalidStatus()
    {
        await using var db = fixture.CreateDbContext();

        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);

        var docId = Guid.CreateVersion7();
        var invalidStatusSql = $"""
            INSERT INTO documents (id, collection_id, file_name, storage_path, content_type, size_bytes, content_hash, status, uploaded_by, created_at)
            VALUES ('{docId}', '{collection.Id}', 'bad_status.pdf', 'path/bad.pdf', 'application/pdf', 512, '{new string('c', 64)}', 'InvalidStatus', '{user.Id}', now());
        """;

        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(invalidStatusSql));
        Assert.Equal("ck_documents_status", ex.ConstraintName);

        // Also verify role check constraint on collection_members
        var invalidRoleSql = $"""
            INSERT INTO collection_members (collection_id, user_id, role)
            VALUES ('{collection.Id}', '{user.Id}', 'SuperAdmin');
        """;

        var roleEx = await Assert.ThrowsAnyAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(invalidRoleSql));
        Assert.Equal("ck_collection_members_role", roleEx.ConstraintName);

        // Also verify status check constraint on reindex_jobs
        var invalidJobStatusSql = $"""
            INSERT INTO reindex_jobs (id, collection_id, status, total_chunks, done_chunks, embedding_model, created_at)
            VALUES ('{Guid.CreateVersion7()}', '{collection.Id}', 'Paused', 0, 0, 'bge-m3', now());
        """;

        var jobEx = await Assert.ThrowsAnyAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(invalidJobStatusSql));
        Assert.Equal("ck_reindex_jobs_status", jobEx.ConstraintName);
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
            Name = $"مجموعه مقررات {Guid.NewGuid():N}",
            Description = "مجموعه تست آیین نامه‌های دانشگاهی",
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
        string? contentHash = null)
    {
        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collectionId,
            FileName = "regulation.pdf",
            StoragePath = $"storage/{Guid.NewGuid():N}.pdf",
            ContentType = "application/pdf",
            SizeBytes = 4096,
            ContentHash = contentHash ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            PageCount = 10,
            Status = DocumentStatus.Ready,
            UploadedBy = uploadedBy,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await db.Documents.AddAsync(document);
        await db.SaveChangesAsync();
        return document;
    }
}
