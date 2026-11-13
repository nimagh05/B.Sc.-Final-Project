using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Application.Text;
using DocSearch.Domain;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Ai.Fakes;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using DocSearch.Infrastructure.Reindex;
using DocSearch.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pgvector;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Integration;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public sealed class ReindexServiceIntegrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Reindex_Updates_EmbeddingModel_And_Vectors()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var initialVector = CreateUnitVector((0, 1.0f));
        var chunk1 = CreateChunk(document.Id, collection.Id, 0, "ماده ۱", "حداکثر واحد مجاز در هر نیم‌سال ۲۰ واحد است.", "old-model", initialVector);
        var chunk2 = CreateChunk(document.Id, collection.Id, 1, "ماده ۲", "حداقل واحد انتخابی ۱۲ واحد است.", "old-model", initialVector);
        await db.Chunks.AddRangeAsync(chunk1, chunk2);
        await db.SaveChangesAsync();

        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake",
                Model = "new-model-v2",
                BatchSize = 1
            }
        });

        var embedder = new CountingPassageEmbedder(assertNoTransaction: () =>
        {
            Assert.Null(db.Database.CurrentTransaction);
        });

        var service = CreateReindexService(db, embedder, aiOptions);

        var jobId = await service.EnqueueReindexAsync(collection.Id, CancellationToken.None);
        await service.ProcessJobAsync(jobId, CancellationToken.None);

        var job = await db.ReindexJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.Equal(ReindexJobStatus.Completed, job.Status);
        Assert.Equal(2, job.TotalChunks);
        Assert.Equal(2, job.DoneChunks);
        Assert.Equal("new-model-v2", job.EmbeddingModel);
        Assert.NotNull(job.FinishedAt);
        Assert.Null(job.ErrorMessage);

        var updatedChunks = await db.Chunks.AsNoTracking()
            .Where(c => c.DocumentId == document.Id)
            .OrderBy(c => c.ChunkIndex)
            .ToListAsync();

        Assert.Equal(2, updatedChunks.Count);
        Assert.All(updatedChunks, c =>
        {
            Assert.Equal("new-model-v2", c.EmbeddingModel);
            Assert.NotEqual(initialVector, c.Embedding.ToArray());
        });
        Assert.Equal(2, embedder.EmbeddedTexts.Count);
    }

    [Fact]
    public async Task Search_Ignores_Chunks_Of_Old_Model_During_Reindex()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var doc1 = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);
        var doc2 = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var normalizer = new PersianTextNormalizer();
        using var generator = new FakeEmbeddingGenerator(normalizer);
        var queryText = "قانون آموزشی دانشگاه";
        var queryEmbedding = (await generator.GenerateAsync([queryText])).Single().Vector.ToArray();
        var doc1Vec = (await generator.GenerateAsync(["قانون آموزشی دانشگاه برای دانشجویان"])).Single().Vector.ToArray();
        var doc2Vec = (await generator.GenerateAsync(["قانون آموزشی دانشگاه برای پژوهشگران"])).Single().Vector.ToArray();

        // Both documents start on model-v1, then doc1 is partially migrated to model-v2
        var chunk1 = CreateChunk(doc1.Id, collection.Id, 0, "ماده ۱", "قانون آموزشی دانشگاه برای دانشجویان", "model-v2", doc1Vec);
        var chunk2 = CreateChunk(doc2.Id, collection.Id, 0, "ماده ۲", "قانون آموزشی دانشگاه برای پژوهشگران", "model-v1", doc2Vec);
        await db.Chunks.AddRangeAsync(chunk1, chunk2);
        await db.SaveChangesAsync();

        var store = new PgVectorStore(db);

        // During in-progress reindex to model-v2, model-v1 chunk must be ignored by search
        var partialResults = await store.SearchAsync(new SearchRequest(
            QueryEmbedding: queryEmbedding,
            QueryText: queryText,
            CollectionIds: [collection.Id],
            EmbeddingModel: "model-v2",
            CandidateK: 10,
            TopK: 10,
            UseHybrid: false), CancellationToken.None);

        Assert.Single(partialResults);
        Assert.Equal(chunk1.Id, partialResults[0].ChunkId);

        // Complete reindex for model-v2
        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake",
                Model = "model-v2",
                BatchSize = 4
            }
        });
        var service = CreateReindexService(db, new CountingPassageEmbedder(), aiOptions);
        var jobId = await service.EnqueueReindexAsync(collection.Id, CancellationToken.None);
        await service.ProcessJobAsync(jobId, CancellationToken.None);

        var fullResults = await store.SearchAsync(new SearchRequest(
            QueryEmbedding: queryEmbedding,
            QueryText: queryText,
            CollectionIds: [collection.Id],
            EmbeddingModel: "model-v2",
            CandidateK: 10,
            TopK: 10,
            UseHybrid: false), CancellationToken.None);

        Assert.Equal(2, fullResults.Count);
        Assert.Contains(fullResults, r => r.ChunkId == chunk1.Id);
        Assert.Contains(fullResults, r => r.ChunkId == chunk2.Id);
    }

    [Fact]
    public async Task Reindex_Only_Targets_Stale_Chunks_TotalChunks_Matches_MigratedCount()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var vec = CreateUnitVector((0, 1.0f));
        var chunks = new List<Chunk>();

        // 4 chunks already on targetModel, 6 chunks on oldModel (10 chunks total)
        for (var i = 0; i < 4; i++)
        {
            chunks.Add(CreateChunk(document.Id, collection.Id, i, $"ماده {i + 1}", $"متن مهاجرت‌شده شماره {i + 1}", "target-model", vec));
        }

        for (var i = 4; i < 10; i++)
        {
            chunks.Add(CreateChunk(document.Id, collection.Id, i, $"ماده {i + 1}", $"متن قدیمی نیازمند بازنمایه شماره {i + 1}", "old-model", vec));
        }

        await db.Chunks.AddRangeAsync(chunks);
        await db.SaveChangesAsync();

        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake",
                Model = "target-model",
                BatchSize = 2
            }
        });
        var countingEmbedder = new CountingPassageEmbedder();
        var service = CreateReindexService(db, countingEmbedder, aiOptions);

        var jobId = await service.EnqueueReindexAsync(collection.Id, CancellationToken.None);

        var queuedJob = await db.ReindexJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.Equal(6, queuedJob.TotalChunks);
        Assert.Equal(0, queuedJob.DoneChunks);

        await service.ProcessJobAsync(jobId, CancellationToken.None);

        var completedJob = await db.ReindexJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.Equal(ReindexJobStatus.Completed, completedJob.Status);
        Assert.Equal(6, completedJob.TotalChunks);
        Assert.Equal(6, completedJob.DoneChunks);
        Assert.Equal(6, countingEmbedder.EmbeddedTexts.Count);
        Assert.DoesNotContain(countingEmbedder.EmbeddedTexts, t => t.Contains("متن مهاجرت‌شده"));
    }

    [Fact]
    public async Task Reindex_Is_Resumable()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var vec = CreateUnitVector((0, 1.0f));
        // 10 chunks total: 4 pre-existing on target-model, 2 already migrated during interrupted run, 4 remaining stale
        var chunks = new List<Chunk>();
        for (var i = 0; i < 6; i++)
        {
            chunks.Add(CreateChunk(document.Id, collection.Id, i, $"ماده {i + 1}", $"بند مهاجرت‌یافته شماره {i + 1}", "target-model", vec));
        }
        for (var i = 6; i < 10; i++)
        {
            chunks.Add(CreateChunk(document.Id, collection.Id, i, $"ماده {i + 1}", $"بند نیازمند بازنمایه شماره {i + 1}", "old-model", vec));
        }
        await db.Chunks.AddRangeAsync(chunks);

        var resumedJob = new ReindexJob
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Status = ReindexJobStatus.Queued,
            TotalChunks = 6,
            DoneChunks = 2,
            EmbeddingModel = "target-model",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.ReindexJobs.AddAsync(resumedJob);
        await db.SaveChangesAsync();

        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake",
                Model = "target-model",
                BatchSize = 2
            }
        });
        var countingEmbedder = new CountingPassageEmbedder();
        var service = CreateReindexService(db, countingEmbedder, aiOptions);

        await service.ProcessJobAsync(resumedJob.Id, CancellationToken.None);

        var updatedJob = await db.ReindexJobs.AsNoTracking().FirstAsync(j => j.Id == resumedJob.Id);
        Assert.Equal(ReindexJobStatus.Completed, updatedJob.Status);
        Assert.Equal(6, updatedJob.TotalChunks);
        Assert.Equal(6, updatedJob.DoneChunks);

        // Only the 4 remaining stale chunks must have been embedded; no chunk is embedded twice
        Assert.Equal(4, countingEmbedder.EmbeddedTexts.Count);
        Assert.DoesNotContain(countingEmbedder.EmbeddedTexts, t => t.Contains("بند مهاجرت‌یافته"));
    }

    [Fact]
    public async Task Reindex_Continues_When_Chunk_Deleted_MidBatch()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var docToDelete = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);
        var docToKeep = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var vec = CreateUnitVector((0, 1.0f));
        var deletedChunk = CreateChunk(docToDelete.Id, collection.Id, 0, "ماده ۱", "متن سند در حال حذف", "old-model", vec);
        var keptChunk = CreateChunk(docToKeep.Id, collection.Id, 0, "ماده ۲", "متن سند باقی‌مانده", "old-model", vec);
        await db.Chunks.AddRangeAsync(deletedChunk, keptChunk);
        await db.SaveChangesAsync();

        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake",
                Model = "target-model",
                BatchSize = 4
            }
        });

        var deletedOnFirstBatch = false;
        var embedder = new CallbackPassageEmbedder(async _ =>
        {
            if (!deletedOnFirstBatch)
            {
                deletedOnFirstBatch = true;
                // Simulate concurrent document deletion while EmbedPassagesAsync runs outside transaction
                await using var concurrentDb = fixture.CreateDbContext();
                await concurrentDb.Documents
                    .Where(d => d.Id == docToDelete.Id)
                    .ExecuteDeleteAsync();
            }
        });

        var service = CreateReindexService(db, embedder, aiOptions);
        var jobId = await service.EnqueueReindexAsync(collection.Id, CancellationToken.None);

        await service.ProcessJobAsync(jobId, CancellationToken.None);

        var finishedJob = await db.ReindexJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.Equal(ReindexJobStatus.Completed, finishedJob.Status);
        Assert.Equal(2, finishedJob.TotalChunks);
        Assert.Equal(2, finishedJob.DoneChunks);
        Assert.Null(finishedJob.ErrorMessage);

        var survivingChunk = await db.Chunks.AsNoTracking().FirstAsync(c => c.Id == keptChunk.Id);
        Assert.Equal("target-model", survivingChunk.EmbeddingModel);
        Assert.False(await db.Chunks.AsNoTracking().AnyAsync(c => c.Id == deletedChunk.Id));
    }

    [Fact]
    public async Task ProcessJobAsync_WhenEmbedderThrows_SetsFailedWithSanitizedMessage()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var chunk = CreateChunk(document.Id, collection.Id, 0, "ماده ۱", "متن نمونه", "old-model", CreateUnitVector((0, 1.0f)));
        await db.Chunks.AddAsync(chunk);
        await db.SaveChangesAsync();

        var aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions
        {
            Embedding = new AiEmbeddingOptions
            {
                Provider = "Fake",
                Model = "new-model",
                BatchSize = 4
            }
        });

        var throwingEmbedder = new ThrowingPassageEmbedder(
            new InvalidOperationException(@"Failure reading C:\secret\path\file.bin with Stack trace details"));

        var service = CreateReindexService(db, throwingEmbedder, aiOptions);
        var jobId = await service.EnqueueReindexAsync(collection.Id, CancellationToken.None);

        await service.ProcessJobAsync(jobId, CancellationToken.None);

        var failedJob = await db.ReindexJobs.AsNoTracking().FirstAsync(j => j.Id == jobId);
        Assert.Equal(ReindexJobStatus.Failed, failedJob.Status);
        Assert.NotNull(failedJob.FinishedAt);
        Assert.False(string.IsNullOrWhiteSpace(failedJob.ErrorMessage));
        Assert.DoesNotContain(@"C:\secret", failedJob.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Stack trace", failedJob.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PartialUniqueIndex_Enforces_Single_Active_Job_Per_Collection()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);

        var activeJob = new ReindexJob
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Status = ReindexJobStatus.Running,
            TotalChunks = 1,
            DoneChunks = 0,
            EmbeddingModel = "model-v1"
        };
        await db.ReindexJobs.AddAsync(activeJob);
        await db.SaveChangesAsync();

        var duplicateActiveJob = new ReindexJob
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Status = ReindexJobStatus.Queued,
            TotalChunks = 1,
            DoneChunks = 0,
            EmbeddingModel = "model-v1"
        };
        await db.ReindexJobs.AddAsync(duplicateActiveJob);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Mark first job Completed, then a new Queued job on the same collection succeeds
        var trackedFirst = await db.ReindexJobs.FirstAsync(j => j.Id == activeJob.Id);
        trackedFirst.Status = ReindexJobStatus.Completed;
        trackedFirst.FinishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var nextJob = new ReindexJob
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Status = ReindexJobStatus.Queued,
            TotalChunks = 1,
            DoneChunks = 0,
            EmbeddingModel = "model-v2"
        };
        await db.ReindexJobs.AddAsync(nextJob);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ReindexWorker_StartupRecovery_Resets_Running_Job_And_Completes()
    {
        await using var db = fixture.CreateDbContext();
        var user = await CreateTestUserAsync(db);
        var collection = await CreateTestCollectionAsync(db, user.Id);
        var document = await CreateTestDocumentAsync(db, collection.Id, user.Id, DocumentStatus.Ready);

        var vec = CreateUnitVector((0, 1.0f));
        var chunk = CreateChunk(document.Id, collection.Id, 0, "ماده ۱", "متن بازیابی پس از راه‌اندازی", "old-model", vec);
        await db.Chunks.AddAsync(chunk);

        var stuckJob = new ReindexJob
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collection.Id,
            Status = ReindexJobStatus.Running,
            TotalChunks = 1,
            DoneChunks = 0,
            EmbeddingModel = "recovered-model",
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.ReindexJobs.AddAsync(stuckJob);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(fixture.ConnectionString, npg => npg.UseVector()));
        services.AddSingleton<ITextNormalizer, PersianTextNormalizer>();
        services.AddSingleton<IPassageEmbedder, CountingPassageEmbedder>();
        services.AddSingleton<IReindexQueue, ReindexQueue>();
        services.AddScoped<IReindexService, ReindexService>();
        services.Configure<ChunkingOptions>(_ => { });
        services.Configure<IngestionOptions>(_ => { });
        services.Configure<AiOptions>(o =>
        {
            o.Embedding.Provider = "Fake";
            o.Embedding.Model = "recovered-model";
            o.Embedding.BatchSize = 4;
        });

        await using var provider = services.BuildServiceProvider();
        var config = new ConfigurationBuilder().Build();
        var worker = new ReindexWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IReindexQueue>(),
            config,
            NullLogger<ReindexWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            ReindexJob? finished = null;
            while (DateTime.UtcNow < deadline)
            {
                await using var checkDb = fixture.CreateDbContext();
                var current = await checkDb.ReindexJobs.AsNoTracking().FirstAsync(j => j.Id == stuckJob.Id);
                if (current.Status == ReindexJobStatus.Completed)
                {
                    finished = current;
                    break;
                }
                await Task.Delay(50);
            }

            Assert.NotNull(finished);
            Assert.Equal(1, finished.DoneChunks);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static ReindexService CreateReindexService(
        AppDbContext db,
        IPassageEmbedder embedder,
        Microsoft.Extensions.Options.IOptions<AiOptions> aiOptions)
    {
        var queue = new ReindexQueue(Microsoft.Extensions.Options.Options.Create(new IngestionOptions()));
        return new ReindexService(
            db,
            queue,
            new PersianTextNormalizer(),
            embedder,
            Microsoft.Extensions.Options.Options.Create(new ChunkingOptions()),
            aiOptions,
            NullLogger<ReindexService>.Instance);
    }

    private static Chunk CreateChunk(
        Guid documentId,
        Guid collectionId,
        int index,
        string? sectionTitle,
        string text,
        string embeddingModel,
        float[] vector)
    {
        var normalizer = new PersianTextNormalizer();
        return new Chunk
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            CollectionId = collectionId,
            ChunkIndex = index,
            PageStart = 1,
            PageEnd = 1,
            SectionTitle = sectionTitle,
            Text = normalizer.NormalizeForDisplay(text),
            SearchText = normalizer.NormalizeForSearch(text),
            TokenCount = 10,
            Embedding = new Vector(vector),
            EmbeddingModel = embeddingModel
        };
    }

    private static float[] CreateUnitVector(params (int Index, float Value)[] components)
    {
        var vec = new float[EmbeddingConstants.VectorDimensions];
        foreach (var (index, value) in components)
        {
            vec[index] = value;
        }
        return vec;
    }

    private static async Task<ApplicationUser> CreateTestUserAsync(AppDbContext db)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString("D"),
            UserName = $"user_{Guid.NewGuid():N}@test.local",
            Email = $"user_{Guid.NewGuid():N}@test.local",
            NormalizedEmail = $"USER_{Guid.NewGuid():N}@TEST.LOCAL",
            NormalizedUserName = $"USER_{Guid.NewGuid():N}@TEST.LOCAL",
            EmailConfirmed = true
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
            Description = "تست بازنمایه",
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

    private sealed class CountingPassageEmbedder(Action? assertNoTransaction = null) : IPassageEmbedder
    {
        public List<string> EmbeddedTexts { get; } = [];

        public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(
            IReadOnlyList<string> passages,
            CancellationToken ct)
        {
            assertNoTransaction?.Invoke();
            EmbeddedTexts.AddRange(passages);

            using var generator = new FakeEmbeddingGenerator(new PersianTextNormalizer());
            var generated = await generator.GenerateAsync(passages, cancellationToken: ct);
            return generated.Select(e => e.Vector.ToArray()).ToList();
        }
    }

    private sealed class ThrowingPassageEmbedder(Exception exception) : IPassageEmbedder
    {
        public Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<float[]>>(exception);
    }

    private sealed class CallbackPassageEmbedder(Func<IReadOnlyList<string>, Task> onEmbedAsync) : IPassageEmbedder
    {
        public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(
            IReadOnlyList<string> passages,
            CancellationToken ct)
        {
            await onEmbedAsync(passages);

            using var generator = new FakeEmbeddingGenerator(new PersianTextNormalizer());
            var generated = await generator.GenerateAsync(passages, cancellationToken: ct);
            return generated.Select(e => e.Vector.ToArray()).ToList();
        }
    }
}
