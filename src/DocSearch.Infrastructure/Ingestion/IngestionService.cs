using System.Text.RegularExpressions;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Chunking;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Ingestion;

public sealed partial class IngestionService : IIngestionService
{
    private readonly AppDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IPdfExtractor _pdfExtractor;
    private readonly IChunker _chunker;
    private readonly HeaderFooterCleaner _headerFooterCleaner;
    private readonly IPassageEmbedder _passageEmbedder;
    private readonly IVectorStore _vectorStore;
    private readonly IOptions<ChunkingOptions> _chunkingOptions;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        AppDbContext dbContext,
        IFileStorage fileStorage,
        IPdfExtractor pdfExtractor,
        IChunker chunker,
        IPassageEmbedder passageEmbedder,
        IVectorStore vectorStore,
        IOptions<ChunkingOptions> chunkingOptions,
        IOptions<AiOptions> aiOptions,
        ILogger<IngestionService> logger,
        HeaderFooterCleaner? headerFooterCleaner = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _pdfExtractor = pdfExtractor ?? throw new ArgumentNullException(nameof(pdfExtractor));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _passageEmbedder = passageEmbedder ?? throw new ArgumentNullException(nameof(passageEmbedder));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _chunkingOptions = chunkingOptions ?? throw new ArgumentNullException(nameof(chunkingOptions));
        _aiOptions = aiOptions ?? throw new ArgumentNullException(nameof(aiOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _headerFooterCleaner = headerFooterCleaner ?? new HeaderFooterCleaner();
    }

    [GeneratedRegex(@"([a-zA-Z]:\\[^ \r\n\t]+|/[^ \r\n\t]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"(?i)stack trace", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StackTraceRegex();

    public async Task ProcessDocumentAsync(Guid documentId, CancellationToken ct)
    {
        _logger.LogInformation("Beginning ingestion for document {DocumentId}", documentId);

        Document? document;

        // Phase (a): Atomic claim transitioning state from Uploaded -> Processing
        try
        {
            var rowsAffected = await _dbContext.Documents
                .Where(d => d.Id == documentId && d.Status == DocumentStatus.Uploaded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, DocumentStatus.Processing)
                    .SetProperty(d => d.ErrorMessage, (string?)null), ct)
                .ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                _logger.LogInformation("Document {DocumentId} is not in Uploaded state (or does not exist); skipping processing.", documentId);
                return;
            }

            document = await _dbContext.Documents
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId, ct)
                .ConfigureAwait(false);

            if (document == null)
            {
                _logger.LogWarning("Document {DocumentId} was deleted immediately after claiming; aborting.", documentId);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Ingestion cancelled for document {DocumentId} during status initialization.", documentId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize Processing status for document {DocumentId}", documentId);
            await SetDocumentFailedAsync(documentId, ex).ConfigureAwait(false);
            return;
        }

        // Phase (b): Heavy processing (I/O, extraction, cleaning, chunking, batched embedding) completely OUTSIDE any DB transaction
        IReadOnlyList<PageText> pages;
        IReadOnlyList<ChunkDraft> chunks;
        List<ChunkRecord> chunkRecords;
        int[]? ocrPages;

        try
        {
            // Verify no transaction is open
            if (_dbContext.Database.CurrentTransaction != null)
            {
                throw new InvalidOperationException("Extraction and embedding must run outside any active database transaction.");
            }

            await using var pdfStream = await _fileStorage.OpenReadAsync(document.StoragePath, ct).ConfigureAwait(false);
            pages = await _pdfExtractor.ExtractAsync(pdfStream, ct).ConfigureAwait(false);

            if (pages == null || pages.Count == 0 || pages.All(p => string.IsNullOrWhiteSpace(p.Text)))
            {
                throw new InvalidFileException("Document contains no extractable text.");
            }

            // Note: LegalAwareChunker.Chunk runs HeaderFooterCleaner.Clean(pages) as Step 1 per SPEC §3
            chunks = _chunker.Chunk(pages, _chunkingOptions.Value);
            if (chunks == null || chunks.Count == 0)
            {
                throw new InvalidFileException("Document produced no text chunks.");
            }

            var embeddingTexts = chunks.Select(c => c.EmbeddingText).ToList();
            var embeddings = await _passageEmbedder.EmbedPassagesAsync(embeddingTexts, ct).ConfigureAwait(false);

            var embeddingModel = _aiOptions.Value.Embedding.Model;
            chunkRecords = new List<ChunkRecord>(chunks.Count);
            for (var i = 0; i < chunks.Count; i++)
            {
                chunkRecords.Add(new ChunkRecord(chunks[i], embeddings[i], embeddingModel));
            }

            var detectedOcrPages = pages.Where(p => p.NeedsOcr).Select(p => p.PageNumber).ToArray();
            ocrPages = detectedOcrPages.Length > 0 ? detectedOcrPages : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Ingestion cancelled during extraction/embedding for document {DocumentId}; document left recoverable.", documentId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heavy processing failed for document {DocumentId}", documentId);
            await SetDocumentFailedAsync(documentId, ex).ConfigureAwait(false);
            return;
        }

        // Phase (c): Single short transaction to replace chunks and set status to Ready
        try
        {
            var strategy = _dbContext.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await _dbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

                // Ensure document was not deleted while Phase (b) was executing
                var docToUpdate = await _dbContext.Documents.FirstOrDefaultAsync(d => d.Id == documentId, ct).ConfigureAwait(false);
                if (docToUpdate == null)
                {
                    _logger.LogWarning("Document {DocumentId} was deleted before persistence completed; skipping Phase (c).", documentId);
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return;
                }

                await _vectorStore.DeleteByDocumentAsync(documentId, ct).ConfigureAwait(false);
                await _vectorStore.AddChunksAsync(documentId, document.CollectionId, chunkRecords, ct).ConfigureAwait(false);

                docToUpdate.Status = DocumentStatus.Ready;
                docToUpdate.PageCount = pages.Count;
                docToUpdate.OcrPages = ocrPages;
                docToUpdate.ErrorMessage = null;
                docToUpdate.ProcessedAt = DateTimeOffset.UtcNow;

                await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            _logger.LogInformation("Document {DocumentId} successfully ingested: {PageCount} pages, {ChunkCount} chunks.",
                documentId, pages.Count, chunkRecords.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Ingestion cancelled during persistence for document {DocumentId}; document left recoverable.", documentId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Persistence failed for document {DocumentId}", documentId);
            await SetDocumentFailedAsync(documentId, ex).ConfigureAwait(false);
        }
    }

    private async Task SetDocumentFailedAsync(Guid documentId, Exception exception)
    {
        try
        {
            // Clear any poison/failed entities from the ChangeTracker before updating status
            _dbContext.ChangeTracker.Clear();

            var sanitizedMessage = SanitizeErrorMessage(exception);

            var doc = await _dbContext.Documents
                .FirstOrDefaultAsync(d => d.Id == documentId, CancellationToken.None)
                .ConfigureAwait(false);

            if (doc != null)
            {
                doc.Status = DocumentStatus.Failed;
                doc.ErrorMessage = sanitizedMessage;
                await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception dbEx)
        {
            _logger.LogError(dbEx, "Failed to persist Failed status for document {DocumentId}", documentId);
        }
    }

    private static string SanitizeErrorMessage(Exception exception)
    {
        string rawMessage;
        if (exception is DocSearchException docEx)
        {
            rawMessage = docEx.Message;
        }
        else if (exception is TimeoutException or TaskCanceledException or OperationCanceledException)
        {
            rawMessage = "The operation timed out during document processing.";
        }
        else
        {
            // For non-domain exceptions, never leak raw internal messages, queries, or host information
            rawMessage = $"An error occurred during document processing ({exception.GetType().Name}).";
        }

        // Strip file paths
        var sanitized = FilePathRegex().Replace(rawMessage, "[path]");

        // Strip any stack trace references
        sanitized = StackTraceRegex().Replace(sanitized, "");

        // Truncate to maximum 500 characters
        if (sanitized.Length > 500)
        {
            sanitized = sanitized[..500];
        }

        return sanitized.Trim();
    }
}
