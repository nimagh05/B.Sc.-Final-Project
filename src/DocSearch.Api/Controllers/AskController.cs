using Asp.Versioning;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Authorize]
[EnableRateLimiting("ask")]
public sealed class AskController : ControllerBase
{
    private readonly IRagService _ragService;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AskController> _logger;

    public AskController(
        IRagService ragService,
        ICurrentUser currentUser,
        ILogger<AskController> logger)
    {
        _ragService = ragService ?? throw new ArgumentNullException(nameof(ragService));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
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

        return Ok(response);
    }
}
