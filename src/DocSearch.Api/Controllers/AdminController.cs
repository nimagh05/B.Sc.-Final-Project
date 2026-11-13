using Asp.Versioning;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/admin")]
[Authorize(Roles = "Admin")]
public sealed class AdminController(IReindexService reindexService) : ControllerBase
{
    [HttpPost("collections/{id:guid}/reindex")]
    [ProducesResponseType(typeof(ReindexJobResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReindexCollection([FromRoute] Guid id, CancellationToken ct)
    {
        var jobId = await reindexService.EnqueueReindexAsync(id, ct);
        return Accepted($"/api/v1/admin/reindex-jobs/{jobId}", new ReindexJobResponse(jobId));
    }

    [HttpGet("reindex-jobs/{jobId:guid}")]
    [ProducesResponseType(typeof(ReindexJobStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReindexJobStatus([FromRoute] Guid jobId, CancellationToken ct)
    {
        var status = await reindexService.GetJobStatusAsync(jobId, ct);
        return Ok(status);
    }
}
