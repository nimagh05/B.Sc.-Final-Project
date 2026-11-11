using Asp.Versioning;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Application.Options;
using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminController(
    AppDbContext dbContext,
    IOptions<AiOptions> aiOptions,
    ILogger<AdminController> logger) : ControllerBase
{
    [HttpPost("collections/{id:guid}/reindex")]
    [ProducesResponseType(typeof(ReindexJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReindexCollection([FromRoute] Guid id, CancellationToken ct)
    {
        var collectionExists = await dbContext.Collections
            .AnyAsync(c => c.Id == id, ct);

        if (!collectionExists)
        {
            throw new NotFoundException("Collection was not found or is not visible.");
        }

        var chunkCount = await dbContext.Chunks
            .CountAsync(c => c.CollectionId == id, ct);

        var job = new ReindexJob
        {
            Id = Guid.CreateVersion7(),
            CollectionId = id,
            Status = ReindexJobStatus.Completed,
            TotalChunks = chunkCount,
            DoneChunks = chunkCount,
            EmbeddingModel = aiOptions.Value.Embedding.Model,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow
        };

        await dbContext.ReindexJobs.AddAsync(job, ct);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Admin triggered reindex for collection {CollectionId}, job {JobId}", id, job.Id);

        return Accepted($"/api/v1/admin/reindex-jobs/{job.Id}", new ReindexJobResponse(job.Id));
    }

    [HttpGet("reindex-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(ReindexJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReindexJobStatus([FromRoute] Guid jobId, CancellationToken ct)
    {
        var job = await dbContext.ReindexJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job == null)
        {
            throw new NotFoundException("Reindex job was not found.");
        }

        return Ok(new ReindexJobStatusResponse(
            job.Status.ToString(),
            job.TotalChunks,
            job.DoneChunks,
            job.ErrorMessage));
    }
}
