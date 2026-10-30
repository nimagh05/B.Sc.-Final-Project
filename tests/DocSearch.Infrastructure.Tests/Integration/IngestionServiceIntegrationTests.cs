using System.Text;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using DocSearch.Domain;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Ai.Fakes;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Ingestion;
using DocSearch.Infrastructure.Persistence;
using DocSearch.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Integration;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public sealed class IngestionServiceIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ProcessDocumentAsync_HappyPath_UpdatesStatusToReady_WithChunksAndPageCount()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample PDF");
        var extractor = new StubPdfExtractor(
        [
            new PageText(1, "ماده ۱: این آیین‌نامه اجرایی است.", 1.0, false),
            new PageText(2, "ماده ۲: شرایط ثبت نام دانشجویان.", 1.0, false)
        ]);

        var passageEmbedder = new StubPassageEmbedder(assertNoTransaction: () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
        });

        var vectorStore = new PgVectorStore(db);
        var chunker = new LegalAwareChunker();

        var service = new IngestionService(
            db,
            fileStorage,
            extractor,
            chunker,
            passageEmbedder,
            vectorStore,
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        var updatedDoc = await db.Documents.FirstAsync(d => d.Id == document.Id);
        Assert.Equal(DocumentStatus.Ready, updatedDoc.Status);
        Assert.Equal(2, updatedDoc.PageCount);
        Assert.NotNull(updatedDoc.ProcessedAt);
        Assert.Null(updatedDoc.ErrorMessage);

        var chunks = await db.Chunks.Where(c => c.DocumentId == document.Id).ToListAsync();
        Assert.NotEmpty(chunks);
        Assert.All(chunks, c => Assert.Equal(collection.Id, c.CollectionId));
    }

    [Fact]
    public async Task ProcessDocumentAsync_AssertsNoTransactionActiveDuringExtractionAndEmbedding()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample");
        var extractorAsserted = false;
        var embedderAsserted = false;

        var extractor = new StubPdfExtractor(
            [new PageText(1, "ماده اول متن فارسی", 1.0, false)],
            onExtract: () =>
            {
                Assert.Null(db.Database.CurrentTransaction);
                extractorAsserted = true;
            });

        var passageEmbedder = new StubPassageEmbedder(assertNoTransaction: () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
            embedderAsserted = true;
        });

        var service = new IngestionService(
            db,
            fileStorage,
            extractor,
            new LegalAwareChunker(),
            passageEmbedder,
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        Assert.True(extractorAsserted, "Extractor must run outside any database transaction.");
        Assert.True(embedderAsserted, "Embedding must run outside any database transaction.");

        var updatedDoc = await db.Documents.FirstAsync(d => d.Id == document.Id);
        Assert.Equal(DocumentStatus.Ready, updatedDoc.Status);
    }

    [Fact]
    public async Task ProcessDocumentAsync_WhenPdfExtractorThrows_SetsStatusFailed_WithSanitizedMessage()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample");
        var throwingExtractor = new ThrowingPdfExtractor(
            new InvalidOperationException("Failed extracting C:\\Sensitive\\Internal\\file.pdf with stack trace"));

        var service = new IngestionService(
            db,
            fileStorage,
            throwingExtractor,
            new LegalAwareChunker(),
            new StubPassageEmbedder(),
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        var updatedDoc = await db.Documents.FirstAsync(d => d.Id == document.Id);
        Assert.Equal(DocumentStatus.Failed, updatedDoc.Status);
        Assert.NotNull(updatedDoc.ErrorMessage);
        // Sensitive path must be sanitized
        Assert.DoesNotContain("C:\\Sensitive", updatedDoc.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack trace", updatedDoc.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessDocumentAsync_WhenPdfHasNoExtractableText_SetsStatusFailed_WithClearMessage()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample");
        var emptyExtractor = new StubPdfExtractor([]);

        var service = new IngestionService(
            db,
            fileStorage,
            emptyExtractor,
            new LegalAwareChunker(),
            new StubPassageEmbedder(),
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        var updatedDoc = await db.Documents.FirstAsync(d => d.Id == document.Id);
        Assert.Equal(DocumentStatus.Failed, updatedDoc.Status);
        Assert.Contains("no extractable text", updatedDoc.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessDocumentAsync_OnCancellation_DoesNotMarkFailed_LeavesDocumentRecoverable()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample");
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancelled

        var service = new IngestionService(
            db,
            fileStorage,
            new StubPdfExtractor([]),
            new LegalAwareChunker(),
            new StubPassageEmbedder(),
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ProcessDocumentAsync(document.Id, cts.Token));

        var updatedDoc = await db.Documents.FirstAsync(d => d.Id == document.Id);
        // Must NOT be marked Failed
        Assert.NotEqual(DocumentStatus.Failed, updatedDoc.Status);
    }

    [Fact]
    public async Task ProcessDocumentAsync_Reprocess_DeletesExistingChunks_AndInsertsNewOnes()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample");
        var extractor = new StubPdfExtractor([new PageText(1, "ماده ۱: نسخه اولیه سند.", 1.0, false)]);

        var service = new IngestionService(
            db,
            fileStorage,
            extractor,
            new LegalAwareChunker(),
            new StubPassageEmbedder(),
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        // First run
        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        var initialChunks = await db.Chunks.Where(c => c.DocumentId == document.Id).ToListAsync();
        Assert.Single(initialChunks);
        var initialChunkId = initialChunks[0].Id;

        // Reprocess: update extractor text and mark Uploaded again
        extractor.Pages = [new PageText(1, "ماده ۱: نسخه به روز شده و جدید سند.", 1.0, false)];
        document.Status = DocumentStatus.Uploaded;
        await db.SaveChangesAsync();

        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        var reprocessedChunks = await db.Chunks.Where(c => c.DocumentId == document.Id).ToListAsync();
        Assert.Single(reprocessedChunks);
        // New chunk should have replaced the old one
        Assert.NotEqual(initialChunkId, reprocessedChunks[0].Id);
        Assert.Contains("جدید", reprocessedChunks[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessDocumentAsync_WhenEmbeddingThrows_SetsStatusFailed_WithSanitizedMessage()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample");
        var extractor = new StubPdfExtractor([new PageText(1, "ماده ۱: متن سند.", 1.0, false)]);
        var throwingEmbedder = new ThrowingPassageEmbedder(
            new InvalidOperationException("Failed embedding C:\\Sensitive\\Keys\\emb.bin with stack trace"));

        var service = new IngestionService(
            db,
            fileStorage,
            extractor,
            new LegalAwareChunker(),
            throwingEmbedder,
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        var updatedDoc = await db.Documents.FirstAsync(d => d.Id == document.Id);
        Assert.Equal(DocumentStatus.Failed, updatedDoc.Status);
        Assert.NotNull(updatedDoc.ErrorMessage);
        Assert.DoesNotContain("C:\\Sensitive", updatedDoc.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack trace", updatedDoc.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessDocumentAsync_WhenTimeoutOccursWithoutCancellation_SetsStatusFailed()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Uploaded);

        var fileStorage = new StubFileStorage("%PDF-1.7\nSample");
        var extractor = new StubPdfExtractor([new PageText(1, "ماده ۱: متن سند.", 1.0, false)]);
        var timeoutEmbedder = new ThrowingPassageEmbedder(new TaskCanceledException("HttpClient request timed out"));

        var service = new IngestionService(
            db,
            fileStorage,
            extractor,
            new LegalAwareChunker(),
            timeoutEmbedder,
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        // Caller token is NOT cancelled (timeout is internal)
        await service.ProcessDocumentAsync(document.Id, CancellationToken.None);

        var updatedDoc = await db.Documents.FirstAsync(d => d.Id == document.Id);
        Assert.Equal(DocumentStatus.Failed, updatedDoc.Status);
        Assert.NotNull(updatedDoc.ErrorMessage);
        Assert.Contains("timed out", updatedDoc.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessDocumentAsync_AtomicClaim_SkipsWhenAlreadyProcessingOrReady()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);

        var processingDoc = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Processing);
        var readyDoc = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var extractorCalled = false;
        var extractor = new StubPdfExtractor(
            [new PageText(1, "متن", 1.0, false)],
            onExtract: () => extractorCalled = true);

        var service = new IngestionService(
            db,
            new StubFileStorage("%PDF-1.7"),
            extractor,
            new LegalAwareChunker(),
            new StubPassageEmbedder(),
            new PgVectorStore(db),
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            Microsoft.Extensions.Options.Options.Create(new AiOptions()),
            NullLogger<IngestionService>.Instance);

        // Attempt processing on a document that is already Processing
        await service.ProcessDocumentAsync(processingDoc.Id, CancellationToken.None);
        Assert.False(extractorCalled, "Should not process document that is already Processing.");

        // Attempt processing on a document that is already Ready
        await service.ProcessDocumentAsync(readyDoc.Id, CancellationToken.None);
        Assert.False(extractorCalled, "Should not process document that is already Ready.");

        // Documents maintain their statuses
        var updatedProcessing = await db.Documents.FirstAsync(d => d.Id == processingDoc.Id);
        Assert.Equal(DocumentStatus.Processing, updatedProcessing.Status);

        var updatedReady = await db.Documents.FirstAsync(d => d.Id == readyDoc.Id);
        Assert.Equal(DocumentStatus.Ready, updatedReady.Status);
    }

    private static async Task<ApplicationUser> CreateTestUserAsync(AppDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
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
            Description = "مجموعه تست",
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
            SizeBytes = 1024,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = status,
            UploadedBy = uploadedBy,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await db.Documents.AddAsync(document);
        await db.SaveChangesAsync();
        return document;
    }

    private sealed class StubFileStorage(string sampleContent) : IFileStorage
    {
        public Task<string> SaveAsync(Stream content, string suggestedFileName, CancellationToken ct) =>
            Task.FromResult("2026/10/dummy.pdf");

        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(sampleContent)));

        public Task DeleteAsync(string storagePath, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class StubPdfExtractor(IReadOnlyList<PageText> initialPages, Action? onExtract = null) : IPdfExtractor
    {
        public IReadOnlyList<PageText> Pages { get; set; } = initialPages;

        public Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct)
        {
            onExtract?.Invoke();
            return Task.FromResult(Pages);
        }
    }

    private sealed class ThrowingPdfExtractor(Exception exception) : IPdfExtractor
    {
        public Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<PageText>>(exception);
    }

    private sealed class ThrowingPassageEmbedder(Exception exception) : IPassageEmbedder
    {
        public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<float[]>>(exception);
    }

    private sealed class StubPassageEmbedder(Action? assertNoTransaction = null) : IPassageEmbedder
    {
        public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(
            IReadOnlyList<string> passages,
            CancellationToken ct)
        {
            assertNoTransaction?.Invoke();

            using var generator = new FakeEmbeddingGenerator(new PersianTextNormalizer());
            var generated = await generator.GenerateAsync(passages, cancellationToken: ct);
            return generated.Select(e => e.Vector.ToArray()).ToList();
        }
    }
}
