using DocSearch.Application.Abstractions;
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
}
