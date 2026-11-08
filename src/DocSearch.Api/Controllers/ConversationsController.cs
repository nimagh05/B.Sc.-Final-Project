using System.Text.Json;
using Asp.Versioning;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[Authorize]
public sealed class ConversationsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(
        AppDbContext dbContext,
        ICurrentUser currentUser,
        ILogger<ConversationsController> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [HttpGet("conversations")]
    [ProducesResponseType(typeof(IReadOnlyList<ConversationSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConversations(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Clamp(pageSize, 1, 100);

        var query = _dbContext.Conversations
            .AsNoTracking()
            .Where(c => c.UserId == _currentUser.UserId)
            .OrderByDescending(c => c.CreatedAt);

        var items = await query
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(c => new ConversationSummaryDto(c.Id, c.Title, c.CreatedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return Ok(items);
    }

    [HttpGet("conversations/{id:guid}/messages")]
    [ProducesResponseType(typeof(IReadOnlyList<ConversationMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMessages([FromRoute] Guid id, CancellationToken ct)
    {
        var conv = await _dbContext.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (conv == null || conv.UserId != _currentUser.UserId)
        {
            throw new NotFoundException("Conversation was not found.");
        }

        var messages = await _dbContext.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        var dtos = messages.Select(m =>
        {
            IReadOnlyList<SourceCitationDto>? sources = null;
            if (!string.IsNullOrEmpty(m.SourcesJson))
            {
                try
                {
                    sources = JsonSerializer.Deserialize<List<SourceCitationDto>>(m.SourcesJson);
                }
                catch
                {
                    // Ignore malformed json
                }
            }

            return new ConversationMessageDto(
                m.Id,
                m.ConversationId,
                m.Role.ToString().ToLowerInvariant(),
                m.Content,
                m.Degraded,
                m.NotFound,
                m.CreatedAt,
                sources);
        }).ToList();

        return Ok(dtos);
    }

    [HttpDelete("conversations/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteConversation([FromRoute] Guid id, CancellationToken ct)
    {
        var conv = await _dbContext.Conversations
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (conv == null || conv.UserId != _currentUser.UserId)
        {
            throw new NotFoundException("Conversation was not found.");
        }

        _dbContext.Conversations.Remove(conv);
        await _dbContext.SaveChangesAsync(ct);

        return NoContent();
    }
}
