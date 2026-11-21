using System.Diagnostics;
using Asp.Versioning;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Application.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Authorize]
[EnableRateLimiting("ask")]
public sealed class AskController : ControllerBase
{
    private readonly IRagService _ragService;
    private readonly IRetriever _retriever;
    private readonly IAccessService _accessService;
    private readonly ICurrentUser _currentUser;
    private readonly IOptions<DiagnosticsOptions> _diagnosticsOptions;
    private readonly ILogger<AskController> _logger;

    public AskController(
        IRagService ragService,
        IRetriever retriever,
        IAccessService accessService,
        ICurrentUser currentUser,
        IOptions<DiagnosticsOptions> diagnosticsOptions,
        ILogger<AskController> logger)
    {
        _ragService = ragService ?? throw new ArgumentNullException(nameof(ragService));
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        _accessService = accessService ?? throw new ArgumentNullException(nameof(accessService));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _diagnosticsOptions = diagnosticsOptions ?? throw new ArgumentNullException(nameof(diagnosticsOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpPost("ask")]
    [ProducesResponseType(typeof(AskResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Ask([FromBody] AskRequestDto? request, CancellationToken ct)
    {
        if (request == null)
        {
            throw new ValidationException("request", "Request body cannot be null.");
        }

        var trimmedQuestion = request.Question?.Trim() ?? string.Empty;
        if (trimmedQuestion.Length < 3 || trimmedQuestion.Length > 2000)
        {
            throw new ValidationException("question", "Question must be between 3 and 2000 characters.");
        }

        if (request.CollectionIds == null || request.CollectionIds.Count < 1 || request.CollectionIds.Count > 20)
        {
            throw new ValidationException("collectionIds", "collectionIds must contain between 1 and 20 collection IDs.");
        }

        if (request.TopK.HasValue && (request.TopK.Value < 1 || request.TopK.Value > 20))
        {
            throw new ValidationException("topK", "topK must be between 1 and 20.");
        }

        var askRequest = new AskRequest(
            _currentUser.UserId,
            trimmedQuestion,
            request.CollectionIds,
            request.ConversationId,
            request.TopK);

        var result = await _ragService.AskAsync(askRequest, ct);

        var sources = result.Sources.Select(s => new SourceCitationDto(
            s.Index,
            s.DocumentId,
            s.FileName,
            s.Page,
            s.SectionTitle,
            s.Snippet,
            s.Score)).ToList();

        var response = new AskResponse(
            result.Answer,
            result.NotFound,
            result.Degraded,
            result.ConversationId,
            sources);

        if (_diagnosticsOptions.Value.ExposeTimings)
        {
            Response.Headers["X-Timing-Retrieval-Ms"] = result.EmbedRetrieveMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Response.Headers["X-Timing-Generation-Ms"] = result.LlmMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Ok(response);
    }

    [HttpPost("dev/retrieve")]
    [ProducesResponseType(typeof(DevRetrievalResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retrieve([FromBody] DevRetrievalRequestDto? request, CancellationToken ct)
    {
        if (!_diagnosticsOptions.Value.EnableEvalEndpoints)
        {
            throw new NotFoundException("Endpoint not found.");
        }

        if (request == null)
        {
            throw new ValidationException("request", "Request body cannot be null.");
        }

        var trimmedQuestion = request.Question?.Trim() ?? string.Empty;
        if (trimmedQuestion.Length < 3 || trimmedQuestion.Length > 2000)
        {
            throw new ValidationException("question", "Question must be between 3 and 2000 characters.");
        }

        if (request.CollectionIds == null || request.CollectionIds.Count < 1 || request.CollectionIds.Count > 20)
        {
            throw new ValidationException("collectionIds", "collectionIds must contain between 1 and 20 collection IDs.");
        }

        if (request.TopK.HasValue && (request.TopK.Value < 1 || request.TopK.Value > 50))
        {
            throw new ValidationException("topK", "topK must be between 1 and 50.");
        }

        var readableCollectionIds = await _accessService
            .GetReadableCollectionIdsAsync(_currentUser.UserId, request.CollectionIds, ct)
            .ConfigureAwait(false);

        if (readableCollectionIds.Count == 0)
        {
            throw new NotFoundException("None of the requested collections were found or visible.");
        }

        var sw = Stopwatch.StartNew();
        var retrieval = await _retriever
            .RetrieveAsync(trimmedQuestion, readableCollectionIds, request.TopK, request.UseHybrid, ct)
            .ConfigureAwait(false);
        sw.Stop();

        var chunks = retrieval.Chunks.Select((c, idx) => new DevChunkDto(
            c.ChunkId,
            c.DocumentId,
            c.FileName,
            c.PageStart,
            c.PageEnd,
            c.SectionTitle,
            c.Text,
            c.FusedScore,
            c.VectorScore,
            c.FtsScore,
            idx + 1)).ToList();

        var response = new DevRetrievalResponse(
            retrieval.BestVectorScore,
            retrieval.HasFtsHit,
            Math.Round(sw.Elapsed.TotalMilliseconds, 2),
            chunks);

        return Ok(response);
    }
}
