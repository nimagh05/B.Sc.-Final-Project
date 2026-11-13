using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Application.Options;
using DocSearch.Domain;
using DocSearch.Domain.Constants;
using DocSearch.Infrastructure.Common;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace DocSearch.Infrastructure.Reindex;

public sealed class ReindexService : IReindexService
{
    private readonly AppDbContext _dbContext;
    private readonly IReindexQueue _reindexQueue;
    private readonly ITextNormalizer _textNormalizer;
    private readonly IPassageEmbedder _passageEmbedder;
    private readonly IOptions<ChunkingOptions> _chunkingOptions;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ReindexService> _logger;

    public ReindexService(
        AppDbContext dbContext,
        IReindexQueue reindexQueue,
        ITextNormalizer textNormalizer,
        IPassageEmbedder passageEmbedder,
        IOptions<ChunkingOptions> chunkingOptions,
        IOptions<AiOptions> aiOptions,
        ILogger<ReindexService> logger,
        TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _reindexQueue = reindexQueue ?? throw new ArgumentNullException(nameof(reindexQueue));
        _textNormalizer = textNormalizer ?? throw new ArgumentNullException(nameof(textNormalizer));
        _passageEmbedder = passageEmbedder ?? throw new ArgumentNullException(nameof(passageEmbedder));
        _chunkingOptions = chunkingOptions ?? throw new ArgumentNullException(nameof(chunkingOptions));
        _aiOptions = aiOptions ?? throw new ArgumentNullException(nameof(aiOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<Guid> EnqueueReindexAsync(Guid collectionId, CancellationToken ct)
    {
        var collectionExists = await _dbContext.Collections
            .AsNoTracking()
            .AnyAsync(c => c.Id == collectionId, ct)
            .ConfigureAwait(false);

        if (!collectionExists)
        {
            throw new NotFoundException("Collection was not found or is not visible.");
        }

        var hasActiveJob = await _dbContext.ReindexJobs
            .AsNoTracking()
            .AnyAsync(j => j.CollectionId == collectionId &&
                           (j.Status == ReindexJobStatus.Queued || j.Status == ReindexJobStatus.Running), ct)
            .ConfigureAwait(false);

        if (hasActiveJob)
        {
            throw new ConflictException(
                "A reindex job is already queued or running for this collection.",
                "reindex.already_running");
        }

        var targetModel = _aiOptions.Value.Embedding.Model;
        var totalChunks = await CountStaleChunksAsync(collectionId, targetModel, ct)
            .ConfigureAwait(false);

        var job = new ReindexJob
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collectionId,
            Status = ReindexJobStatus.Queued,
            TotalChunks = totalChunks,
            DoneChunks = 0,
            EmbeddingModel = targetModel,
            CreatedAt = _timeProvider.GetUtcNow(),
            FinishedAt = null
        };

        try
        {
            await _dbContext.ReindexJobs.AddAsync(job, ct).ConfigureAwait(false);
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pgEx && pgEx.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ConflictException(
                "A reindex job is already queued or running for this collection.",
                "reindex.already_running");
        }

        await _reindexQueue.EnqueueAsync(job.Id, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Admin triggered reindex for collection {CollectionId}, job {JobId}, model {EmbeddingModel}, totalChunks {TotalChunks}",
            collectionId,
            job.Id,
            targetModel,
            totalChunks);

        return job.Id;
    }

    public async Task<ReindexJobStatusResponse> GetJobStatusAsync(Guid jobId, CancellationToken ct)
    {
        var job = await _dbContext.ReindexJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            .ConfigureAwait(false);

        if (job == null)
        {
            throw new NotFoundException("Reindex job was not found.");
        }

        return new ReindexJobStatusResponse(
            job.Status.ToString(),
            job.TotalChunks,
            job.DoneChunks,
            job.ErrorMessage);
    }

    public async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        _logger.LogInformation("Beginning reindex job {JobId}", jobId);

        ReindexJob? job;
        try
        {
            var rowsAffected = await _dbContext.ReindexJobs
                .Where(j => j.Id == jobId && j.Status == ReindexJobStatus.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, ReindexJobStatus.Running)
                    .SetProperty(j => j.ErrorMessage, (string?)null), ct)
                .ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                _logger.LogInformation("Reindex job {JobId} is not in Queued state (or does not exist); skipping processing.", jobId);
                return;
            }

            job = await _dbContext.ReindexJobs
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == jobId, ct)
                .ConfigureAwait(false);

            if (job == null)
            {
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Reindex job {JobId} cancelled during initialization; leaving recoverable.", jobId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Running status for reindex job {JobId}", jobId);
            await SetJobFailedAsync(jobId, ex).ConfigureAwait(false);
            return;
        }

        var batchSize = Math.Max(1, _aiOptions.Value.Embedding.BatchSize);
        var collectionId = job.CollectionId;
        var targetModel = job.EmbeddingModel;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (_dbContext.Database.CurrentTransaction != null)
                {
                    throw new InvalidOperationException("Reindex embedding must run outside any active database transaction.");
                }

                var batch = await _dbContext.Chunks
                    .AsNoTracking()
                    .Where(c => c.CollectionId == collectionId
                                && c.Document != null
                                && c.Document.Status == DocumentStatus.Ready
                                && c.EmbeddingModel != targetModel)
                    .OrderBy(c => c.Id)
                    .Take(batchSize)
                    .Select(c => new ChunkSlice(c.Id, c.SectionTitle, c.Text))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                if (batch.Count == 0)
                {
                    await CompleteJobAsync(jobId, collectionId, targetModel, ct).ConfigureAwait(false);
                    break;
                }

                var embeddingInputs = batch
                    .Select(c => ChunkEmbeddingTextBuilder.Build(_textNormalizer, _chunkingOptions.Value, c.SectionTitle, c.Text))
                    .ToList();

                var embeddings = await _passageEmbedder.EmbedPassagesAsync(embeddingInputs, ct).ConfigureAwait(false);
                if (embeddings.Count != batch.Count)
                {
                    throw new InvalidOperationException(
                        $"Embedder returned {embeddings.Count} vector(s) for {batch.Count} chunk(s).");
                }

                for (var i = 0; i < embeddings.Count; i++)
                {
                    if (embeddings[i].Length != EmbeddingConstants.VectorDimensions)
                    {
                        throw new InvalidOperationException(
                            $"Embedding dimension mismatch: expected {EmbeddingConstants.VectorDimensions}, got {embeddings[i].Length}.");
                    }
                }

                await PersistBatchAsync(jobId, collectionId, targetModel, batch, embeddings, ct).ConfigureAwait(false);
                _dbContext.ChangeTracker.Clear();
            }

            _logger.LogInformation("Reindex job {JobId} completed for collection {CollectionId} (model {EmbeddingModel}).",
                jobId, collectionId, targetModel);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Reindex job {JobId} cancelled during execution; leaving recoverable.", jobId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reindex job {JobId} failed for collection {CollectionId}", jobId, collectionId);
            await SetJobFailedAsync(jobId, ex).ConfigureAwait(false);
        }
    }

    private async Task PersistBatchAsync(
        Guid jobId,
        Guid collectionId,
        string targetModel,
        IReadOnlyList<ChunkSlice> batch,
        IReadOnlyList<float[]> embeddings,
        CancellationToken ct)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var totalChunks = await _dbContext.ReindexJobs
                .AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => (int?)j.TotalChunks)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (totalChunks == null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }

            // Update each chunk via ExecuteUpdateAsync so concurrent deletion or reprocessing
            // during EmbedPassagesAsync cleanly affects 0 rows instead of throwing DbUpdateConcurrencyException.
            for (var i = 0; i < batch.Count; i++)
            {
                var chunkId = batch[i].Id;
                var newVector = new Vector(embeddings[i]);
                await _dbContext.Chunks
                    .Where(c => c.Id == chunkId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.Embedding, newVector)
                        .SetProperty(c => c.EmbeddingModel, targetModel), ct)
                    .ConfigureAwait(false);
            }

            var remainingStale = await CountStaleChunksAsync(collectionId, targetModel, ct).ConfigureAwait(false);
            var doneChunks = Math.Clamp(totalChunks.Value - remainingStale, 0, totalChunks.Value);

            await _dbContext.ReindexJobs
                .Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.DoneChunks, doneChunks), ct)
                .ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task CompleteJobAsync(
        Guid jobId,
        Guid collectionId,
        string targetModel,
        CancellationToken ct)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            var totalChunks = await _dbContext.ReindexJobs
                .AsNoTracking()
                .Where(j => j.Id == jobId)
                .Select(j => (int?)j.TotalChunks)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (totalChunks == null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }

            var remainingStale = await CountStaleChunksAsync(collectionId, targetModel, ct).ConfigureAwait(false);
            var doneChunks = Math.Clamp(totalChunks.Value - remainingStale, 0, totalChunks.Value);
            var finishedAt = _timeProvider.GetUtcNow();

            await _dbContext.ReindexJobs
                .Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.DoneChunks, doneChunks)
                    .SetProperty(j => j.Status, ReindexJobStatus.Completed)
                    .SetProperty(j => j.ErrorMessage, (string?)null)
                    .SetProperty(j => j.FinishedAt, finishedAt), ct)
                .ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private Task<int> CountStaleChunksAsync(Guid collectionId, string targetModel, CancellationToken ct)
    {
        return _dbContext.Chunks
            .Where(c => c.CollectionId == collectionId
                        && c.Document != null
                        && c.Document.Status == DocumentStatus.Ready
                        && c.EmbeddingModel != targetModel)
            .CountAsync(ct);
    }

    private async Task SetJobFailedAsync(Guid jobId, Exception exception)
    {
        try
        {
            _dbContext.ChangeTracker.Clear();

            var sanitizedMessage = ErrorMessageSanitizer.Sanitize(exception, "reindex processing");

            var job = await _dbContext.ReindexJobs
                .FirstOrDefaultAsync(j => j.Id == jobId, CancellationToken.None)
                .ConfigureAwait(false);

            if (job != null)
            {
                job.Status = ReindexJobStatus.Failed;
                job.ErrorMessage = sanitizedMessage;
                job.FinishedAt = _timeProvider.GetUtcNow();
                await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx, "Failed to persist Failed status for reindex job {JobId}", jobId);
        }
    }

    private sealed record ChunkSlice(Guid Id, string? SectionTitle, string Text);
}
