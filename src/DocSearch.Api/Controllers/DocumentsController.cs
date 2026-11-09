using Asp.Versioning;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Application.Options;
using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using DocSearch.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Authorize]
public sealed class DocumentsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IFileStorage _fileStorage;
    private readonly IIngestionQueue _queue;
    private readonly ICurrentUser _currentUser;
    private readonly IAccessService _accessService;
    private readonly IOptions<StorageOptions> _storageOptions;
    private readonly ILogger<DocumentsController> _logger;

    public DocumentsController(
        AppDbContext dbContext,
        IFileStorage fileStorage,
        IIngestionQueue queue,
        ICurrentUser currentUser,
        IAccessService accessService,
        IOptions<StorageOptions> storageOptions,
        ILogger<DocumentsController> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _accessService = accessService ?? throw new ArgumentNullException(nameof(accessService));
        _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost("collections/{collectionId:guid}/documents")]
    [Consumes("multipart/form-data")]
    [EnableRateLimiting("upload")]
    [ProducesResponseType(typeof(DocumentResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> UploadDocument(
        [FromRoute] Guid collectionId,
        [FromForm] IFormFile? file,
        CancellationToken ct)
    {
        await _accessService.EnsureAccessAsync(_currentUser.UserId, collectionId, CollectionRole.Editor, ct);

        if (file == null || file.Length == 0)
        {
            throw new InvalidFileException("A non-empty PDF file must be provided in the 'file' field.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidFileException("Only PDF files are accepted. File must have a .pdf extension.");
        }

        var maxUploadBytes = (long)_storageOptions.Value.MaxUploadMb * 1024 * 1024;
        if (file.Length > maxUploadBytes)
        {
            throw new FileTooLargeException($"File size exceeds the maximum limit of {_storageOptions.Value.MaxUploadMb} MB.");
        }

        await using var fileStream = file.OpenReadStream();
        using var validatingStream = new PdfValidatingStream(fileStream, maxUploadBytes);

        var storagePath = await _fileStorage.SaveAsync(validatingStream, file.FileName, ct);
        var contentHash = validatingStream.ContentHash;
        var sizeBytes = validatingStream.TotalBytesRead;

        var existingDuplicate = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.CollectionId == collectionId && d.ContentHash == contentHash, ct);

        if (existingDuplicate != null)
        {
            await _fileStorage.DeleteAsync(storagePath, ct);
            throw new ConflictException(
                $"A document with the same content hash already exists in this collection (document id: {existingDuplicate.Id}).",
                "document.duplicate");
        }

        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = collectionId,
            FileName = Path.GetFileName(file.FileName),
            StoragePath = storagePath,
            ContentType = "application/pdf",
            SizeBytes = sizeBytes,
            ContentHash = contentHash,
            Status = DocumentStatus.Uploaded,
            UploadedBy = _currentUser.UserId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _dbContext.Documents.AddAsync(document, ct);
            await _dbContext.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            await _fileStorage.DeleteAsync(storagePath, CancellationToken.None);

            if (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505"
                || ex.Message.Contains("IX_Documents_CollectionId_ContentHash", StringComparison.OrdinalIgnoreCase)
                || ex.InnerException?.Message.Contains("IX_Documents_CollectionId_ContentHash", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new ConflictException(
                    "A document with the same content hash already exists in this collection.",
                    "document.duplicate");
            }

            throw;
        }
        catch
        {
            await _fileStorage.DeleteAsync(storagePath, CancellationToken.None);
            throw;
        }

        await _queue.EnqueueAsync(document.Id, ct);

        _logger.LogInformation("Document {DocumentId} successfully uploaded and enqueued for collection {CollectionId}",
            document.Id, collectionId);

        var response = new DocumentResponse(
            document.Id,
            document.CollectionId,
            document.FileName,
            document.SizeBytes,
            document.Status,
            document.PageCount,
            document.CreatedAt);

        return Accepted($"/api/v1/documents/{document.Id}", response);
    }

    [HttpGet("collections/{collectionId:guid}/documents")]
    [ProducesResponseType(typeof(PagedResult<DocumentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListDocuments(
        [FromRoute] Guid collectionId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] DocumentStatus? status = null,
        CancellationToken ct = default)
    {
        await _accessService.EnsureAccessAsync(_currentUser.UserId, collectionId, CollectionRole.Viewer, ct);

        var query = _dbContext.Documents
            .Where(d => d.CollectionId == collectionId);

        if (status.HasValue)
        {
            query = query.Where(d => d.Status == status.Value);
        }

        var total = await query.CountAsync(ct);
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);

        var items = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .Select(d => new DocumentResponse(
                d.Id,
                d.CollectionId,
                d.FileName,
                d.SizeBytes,
                d.Status,
                d.PageCount,
                d.CreatedAt))
            .ToListAsync(ct);

        return Ok(new PagedResult<DocumentResponse>(items, total, safePage, safePageSize));
    }

    [HttpGet("documents/{id:guid}")]
    [ProducesResponseType(typeof(DocumentDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDocument([FromRoute] Guid id, CancellationToken ct)
    {
        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (document == null)
        {
            throw new NotFoundException("Document was not found or is not visible.");
        }

        await _accessService.EnsureAccessAsync(_currentUser.UserId, document.CollectionId, CollectionRole.Viewer, ct, "Document was not found or is not visible.");

        var chunkCount = await _dbContext.Chunks
            .CountAsync(c => c.DocumentId == id, ct);

        var response = new DocumentDetailResponse(
            document.Id,
            document.CollectionId,
            document.FileName,
            document.SizeBytes,
            document.Status,
            document.ErrorMessage,
            document.PageCount,
            document.OcrPages,
            chunkCount,
            document.CreatedAt,
            document.ProcessedAt);

        return Ok(response);
    }

    [HttpGet("documents/{id:guid}/file")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadFile([FromRoute] Guid id, CancellationToken ct)
    {
        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (document == null)
        {
            throw new NotFoundException("Document was not found or is not visible.");
        }

        await _accessService.EnsureAccessAsync(_currentUser.UserId, document.CollectionId, CollectionRole.Viewer, ct, "Document was not found or is not visible.");

        var stream = await _fileStorage.OpenReadAsync(document.StoragePath, ct);

        return File(stream, "application/pdf", document.FileName, enableRangeProcessing: true);
    }

    [HttpDelete("documents/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDocument([FromRoute] Guid id, CancellationToken ct)
    {
        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (document == null)
        {
            throw new NotFoundException("Document was not found or is not visible.");
        }

        await _accessService.EnsureAccessAsync(_currentUser.UserId, document.CollectionId, CollectionRole.Editor, ct, "Document was not found or is not visible.");

        await _fileStorage.DeleteAsync(document.StoragePath, ct);

        _dbContext.Documents.Remove(document);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted document {DocumentId} and associated storage file {StoragePath}",
            id, document.StoragePath);

        return NoContent();
    }

    [HttpPost("documents/{id:guid}/reprocess")]
    [ProducesResponseType(typeof(DocumentResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReprocessDocument([FromRoute] Guid id, CancellationToken ct)
    {
        var document = await _dbContext.Documents
            .FirstOrDefaultAsync(d => d.Id == id, ct);

        if (document == null)
        {
            throw new NotFoundException("Document was not found or is not visible.");
        }

        await _accessService.EnsureAccessAsync(_currentUser.UserId, document.CollectionId, CollectionRole.Editor, ct, "Document was not found or is not visible.");

        var rowsUpdated = await _dbContext.Documents
            .Where(d => d.Id == id && d.Status != DocumentStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DocumentStatus.Uploaded)
                .SetProperty(d => d.ErrorMessage, (string?)null), ct);

        if (rowsUpdated == 0)
        {
            throw new ConflictException("The document is currently being processed.", "document.conflict");
        }

        await _queue.EnqueueAsync(document.Id, ct);

        _logger.LogInformation("Enqueued document {DocumentId} for reprocessing.", id);

        var response = new DocumentResponse(
            document.Id,
            document.CollectionId,
            document.FileName,
            document.SizeBytes,
            DocumentStatus.Uploaded,
            document.PageCount,
            document.CreatedAt);

        return Accepted($"/api/v1/documents/{document.Id}", response);
    }
}
