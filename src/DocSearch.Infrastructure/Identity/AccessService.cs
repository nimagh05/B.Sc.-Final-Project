using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocSearch.Infrastructure.Identity;

public sealed class AccessService(
    AppDbContext dbContext,
    ICurrentUser? currentUser = null) : IAccessService
{
    public async Task<IReadOnlyCollection<Guid>> GetReadableCollectionIdsAsync(
        string userId,
        IReadOnlyCollection<Guid>? requested,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var isAdmin = currentUser != null && currentUser.IsAdmin && currentUser.UserId == userId;

        if (isAdmin)
        {
            var adminQuery = dbContext.Collections.AsNoTracking();
            if (requested != null && requested.Count > 0)
            {
                adminQuery = adminQuery.Where(c => requested.Contains(c.Id));
            }

            return await adminQuery.Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        }

        var memberCollectionIdsQuery = dbContext.CollectionMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.CollectionId);

        var userQuery = dbContext.Collections
            .AsNoTracking()
            .Where(c => c.OwnerId == userId || memberCollectionIdsQuery.Contains(c.Id));

        if (requested != null && requested.Count > 0)
        {
            userQuery = userQuery.Where(c => requested.Contains(c.Id));
        }

        return await userQuery.Select(c => c.Id).Distinct().ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<CollectionRole?> GetCollectionRoleAsync(
        string userId,
        Guid collectionId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var collection = await dbContext.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == collectionId, ct)
            .ConfigureAwait(false);

        if (collection == null)
        {
            return null;
        }

        var isAdmin = currentUser != null && currentUser.IsAdmin && currentUser.UserId == userId;
        if (isAdmin)
        {
            return CollectionRole.Owner;
        }

        if (collection.OwnerId == userId)
        {
            return CollectionRole.Owner;
        }

        var member = await dbContext.CollectionMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.CollectionId == collectionId && m.UserId == userId, ct)
            .ConfigureAwait(false);

        return member?.Role;
    }

    public async Task EnsureAccessAsync(
        string userId,
        Guid collectionId,
        CollectionRole requiredRole,
        CancellationToken ct,
        string notFoundMessage = "Collection was not found or is not visible.")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var collection = await dbContext.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == collectionId, ct)
            .ConfigureAwait(false);

        if (collection == null)
        {
            // First check if collection exists; if not, always 404 even for Admin
            throw new NotFoundException(notFoundMessage);
        }

        var isAdmin = currentUser != null && currentUser.IsAdmin && currentUser.UserId == userId;
        if (isAdmin)
        {
            // Admin has effective CollectionRole.Owner for all existing collections
            return;
        }

        CollectionRole? effectiveRole = null;
        if (collection.OwnerId == userId)
        {
            effectiveRole = CollectionRole.Owner;
        }
        else
        {
            var member = await dbContext.CollectionMembers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.CollectionId == collectionId && m.UserId == userId, ct)
                .ConfigureAwait(false);

            if (member != null)
            {
                effectiveRole = member.Role;
            }
        }

        if (effectiveRole == null)
        {
            // Non-enumeration rule: non-members ALWAYS receive 404 Not Found (NEVER 403 Forbidden)
            throw new NotFoundException(notFoundMessage);
        }

        var hasPermission = requiredRole switch
        {
            CollectionRole.Viewer => true,
            CollectionRole.Editor => effectiveRole is CollectionRole.Editor or CollectionRole.Owner,
            CollectionRole.Owner => effectiveRole == CollectionRole.Owner,
            _ => false
        };

        if (!hasPermission)
        {
            throw new ForbiddenException("You do not have permission to perform this action.");
        }
    }
}
