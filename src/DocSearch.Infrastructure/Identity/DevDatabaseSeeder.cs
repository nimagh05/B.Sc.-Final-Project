using DocSearch.Domain;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DocSearch.Infrastructure.Identity;

public static class DevDatabaseSeeder
{
    public static readonly Guid DefaultCollectionId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    public static async Task SeedDevUserAndCollectionAsync(AppDbContext dbContext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var existingUser = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == DevCurrentUser.DefaultUserId, ct)
            .ConfigureAwait(false);

        if (existingUser == null)
        {
            var user = new ApplicationUser
            {
                Id = DevCurrentUser.DefaultUserId,
                UserName = DevCurrentUser.DefaultEmail,
                NormalizedUserName = DevCurrentUser.DefaultEmail.ToUpperInvariant(),
                Email = DevCurrentUser.DefaultEmail,
                NormalizedEmail = DevCurrentUser.DefaultEmail.ToUpperInvariant(),
                SecurityStamp = Guid.NewGuid().ToString()
            };

            await dbContext.Users.AddAsync(user, ct).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var existingCollection = await dbContext.Collections
            .FirstOrDefaultAsync(c => c.Id == DefaultCollectionId, ct)
            .ConfigureAwait(false);

        if (existingCollection == null)
        {
            var collection = new Collection
            {
                Id = DefaultCollectionId,
                Name = "مجموعه پیش‌فرض",
                Description = "مجموعه اسناد پیش‌فرض برای توسعه و آزمایش",
                OwnerId = DevCurrentUser.DefaultUserId,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await dbContext.Collections.AddAsync(collection, ct).ConfigureAwait(false);

            var member = new CollectionMember
            {
                CollectionId = DefaultCollectionId,
                UserId = DevCurrentUser.DefaultUserId,
                Role = CollectionRole.Owner
            };

            await dbContext.CollectionMembers.AddAsync(member, ct).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}
