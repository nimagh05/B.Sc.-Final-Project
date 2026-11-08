using Asp.Versioning;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Models;
using DocSearch.Domain;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocSearch.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/collections")]
[Authorize]
public sealed class CollectionsController(
    AppDbContext dbContext,
    IAccessService accessService,
    IFileStorage fileStorage,
    ICurrentUser currentUser,
    UserManager<ApplicationUser> userManager,
    ILogger<CollectionsController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(PagedCollectionsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListCollections(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var effectivePage = Math.Max(1, page);
        var effectivePageSize = Math.Clamp(pageSize, 1, 100);

        var readableIds = await accessService.GetReadableCollectionIdsAsync(currentUser.UserId, null, ct);

        var total = await dbContext.Collections
            .AsNoTracking()
            .Where(c => readableIds.Contains(c.Id))
            .CountAsync(ct);

        var collections = await dbContext.Collections
            .AsNoTracking()
            .Where(c => readableIds.Contains(c.Id))
            .OrderByDescending(c => c.CreatedAt)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.OwnerId,
                c.CreatedAt,
                DocumentCount = dbContext.Documents.Count(d => d.CollectionId == c.Id),
                MemberRole = dbContext.CollectionMembers
                    .Where(m => m.CollectionId == c.Id && m.UserId == currentUser.UserId)
                    .Select(m => (CollectionRole?)m.Role)
                    .FirstOrDefault()
            })
            .ToListAsync(ct);

        var items = collections.Select(c =>
        {
            var role = currentUser.IsAdmin
                ? CollectionRole.Owner
                : (c.OwnerId == currentUser.UserId ? CollectionRole.Owner : (c.MemberRole ?? CollectionRole.Viewer));

            return new CollectionDto(
                c.Id,
                c.Name,
                c.Description,
                role,
                c.DocumentCount,
                c.CreatedAt);
        }).ToList();

        return Ok(new PagedCollectionsResponse(items, total, effectivePage, effectivePageSize));
    }

    [HttpPost]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCollection([FromBody] CreateCollectionRequest? request, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException("name", "Collection name is required.");
        }

        var trimmedName = request.Name.Trim();
        if (trimmedName.Length > 200)
        {
            throw new ValidationException("name", "Collection name cannot exceed 200 characters.");
        }

        var duplicate = await dbContext.Collections
            .AnyAsync(c => c.OwnerId == currentUser.UserId && c.Name == trimmedName, ct);

        if (duplicate)
        {
            throw new ConflictException("A collection with this name already exists.", "collection.name_taken");
        }

        var collection = new Collection
        {
            Id = Guid.CreateVersion7(),
            Name = trimmedName,
            Description = request.Description?.Trim(),
            OwnerId = currentUser.UserId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var member = new CollectionMember
        {
            CollectionId = collection.Id,
            UserId = currentUser.UserId,
            Role = CollectionRole.Owner
        };

        await dbContext.Collections.AddAsync(collection, ct);
        await dbContext.CollectionMembers.AddAsync(member, ct);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Collection {CollectionId} created by user {UserId}", collection.Id, currentUser.UserId);

        var response = new CollectionDto(
            collection.Id,
            collection.Name,
            collection.Description,
            CollectionRole.Owner,
            0,
            collection.CreatedAt);

        return Created($"/api/v1/collections/{collection.Id}", response);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCollection([FromRoute] Guid id, CancellationToken ct)
    {
        await accessService.EnsureAccessAsync(currentUser.UserId, id, CollectionRole.Viewer, ct);

        var collection = await dbContext.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection == null)
        {
            throw new NotFoundException("Collection was not found or is not visible.");
        }

        var role = await accessService.GetCollectionRoleAsync(currentUser.UserId, id, ct) ?? CollectionRole.Viewer;
        var documentCount = await dbContext.Documents.CountAsync(d => d.CollectionId == id, ct);

        var response = new CollectionDto(
            collection.Id,
            collection.Name,
            collection.Description,
            role,
            documentCount,
            collection.CreatedAt);

        return Ok(response);
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateCollection(
        [FromRoute] Guid id,
        [FromBody] UpdateCollectionRequest? request,
        CancellationToken ct)
    {
        await accessService.EnsureAccessAsync(currentUser.UserId, id, CollectionRole.Editor, ct);

        if (request == null || string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException("name", "Collection name is required.");
        }

        var trimmedName = request.Name.Trim();
        if (trimmedName.Length > 200)
        {
            throw new ValidationException("name", "Collection name cannot exceed 200 characters.");
        }

        var collection = await dbContext.Collections
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection == null)
        {
            throw new NotFoundException("Collection was not found or is not visible.");
        }

        if (!string.Equals(collection.Name, trimmedName, StringComparison.Ordinal))
        {
            var duplicate = await dbContext.Collections
                .AnyAsync(c => c.OwnerId == collection.OwnerId && c.Name == trimmedName && c.Id != id, ct);

            if (duplicate)
            {
                throw new ConflictException("A collection with this name already exists.", "collection.name_taken");
            }
        }

        collection.Name = trimmedName;
        collection.Description = request.Description?.Trim();
        await dbContext.SaveChangesAsync(ct);

        var role = await accessService.GetCollectionRoleAsync(currentUser.UserId, id, ct) ?? CollectionRole.Editor;
        var documentCount = await dbContext.Documents.CountAsync(d => d.CollectionId == id, ct);

        return Ok(new CollectionDto(
            collection.Id,
            collection.Name,
            collection.Description,
            role,
            documentCount,
            collection.CreatedAt));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCollection([FromRoute] Guid id, CancellationToken ct)
    {
        await accessService.EnsureAccessAsync(currentUser.UserId, id, CollectionRole.Owner, ct);

        var collection = await dbContext.Collections
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection == null)
        {
            throw new NotFoundException("Collection was not found or is not visible.");
        }

        // Delete all stored physical files for this collection
        var documentPaths = await dbContext.Documents
            .Where(d => d.CollectionId == id)
            .Select(d => d.StoragePath)
            .ToListAsync(ct);

        foreach (var storagePath in documentPaths)
        {
            try
            {
                await fileStorage.DeleteAsync(storagePath, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete physical file {StoragePath} for collection {CollectionId}", storagePath, id);
            }
        }

        dbContext.Collections.Remove(collection);
        await dbContext.SaveChangesAsync(ct);

        logger.LogInformation("Collection {CollectionId} deleted by user {UserId}", id, currentUser.UserId);

        return NoContent();
    }

    [HttpGet("{id:guid}/members")]
    [ProducesResponseType(typeof(IReadOnlyList<CollectionMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembers([FromRoute] Guid id, CancellationToken ct)
    {
        await accessService.EnsureAccessAsync(currentUser.UserId, id, CollectionRole.Owner, ct);

        var members = await (from m in dbContext.CollectionMembers
                             join u in dbContext.Users on m.UserId equals u.Id
                             where m.CollectionId == id
                             select new CollectionMemberDto(m.UserId, u.Email ?? u.UserName ?? m.UserId, m.Role))
                            .ToListAsync(ct);

        return Ok(members);
    }

    [HttpPut("{id:guid}/members")]
    [ProducesResponseType(typeof(CollectionMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpsertMember(
        [FromRoute] Guid id,
        [FromBody] UpsertMemberRequest? request,
        CancellationToken ct)
    {
        await accessService.EnsureAccessAsync(currentUser.UserId, id, CollectionRole.Owner, ct);

        if (request == null || string.IsNullOrWhiteSpace(request.Email))
        {
            throw new ValidationException("email", "Member email is required.");
        }

        var collection = await dbContext.Collections
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection == null)
        {
            throw new NotFoundException("Collection was not found or is not visible.");
        }

        var targetEmail = request.Email.Trim();
        var targetUser = await userManager.FindByEmailAsync(targetEmail);
        if (targetUser == null)
        {
            throw new NotFoundException("User with this email was not found.");
        }

        if (targetUser.Id == collection.OwnerId && request.Role != CollectionRole.Owner)
        {
            throw new ConflictException("Cannot demote the collection owner.", "collection.owner_protected");
        }

        var member = await dbContext.CollectionMembers
            .FirstOrDefaultAsync(m => m.CollectionId == id && m.UserId == targetUser.Id, ct);

        if (member != null)
        {
            member.Role = request.Role;
        }
        else
        {
            member = new CollectionMember
            {
                CollectionId = id,
                UserId = targetUser.Id,
                Role = request.Role
            };
            await dbContext.CollectionMembers.AddAsync(member, ct);
        }

        await dbContext.SaveChangesAsync(ct);

        return Ok(new CollectionMemberDto(targetUser.Id, targetUser.Email ?? targetEmail, member.Role));
    }

    [HttpDelete("{id:guid}/members/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMember(
        [FromRoute] Guid id,
        [FromRoute] string userId,
        CancellationToken ct)
    {
        await accessService.EnsureAccessAsync(currentUser.UserId, id, CollectionRole.Owner, ct);

        var collection = await dbContext.Collections
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        if (collection == null)
        {
            throw new NotFoundException("Collection was not found or is not visible.");
        }

        if (userId == collection.OwnerId)
        {
            throw new ConflictException("Cannot remove the collection owner from the collection.", "collection.owner_protected");
        }

        var member = await dbContext.CollectionMembers
            .FirstOrDefaultAsync(m => m.CollectionId == id && m.UserId == userId, ct);

        if (member != null)
        {
            dbContext.CollectionMembers.Remove(member);
            await dbContext.SaveChangesAsync(ct);
        }

        return NoContent();
    }
}
