using DocSearch.Application.Abstractions;
using DocSearch.Domain;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Tests.Fixtures;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Identity;

[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public sealed class AccessServiceTests(PostgresFixture fixture)
{
    private sealed class StubUser(string userId, bool isAdmin) : ICurrentUser
    {
        public string UserId => userId;
        public string Email => $"{userId}@test.local";
        public bool IsAdmin => isAdmin;
    }

    [Fact]
    public async Task GetReadableCollectionIdsAsync_Owner_CanReadOwnCollection()
    {
        await using var db = fixture.CreateDbContext();
        var ownerId = Guid.NewGuid().ToString();
        var collectionId = Guid.NewGuid();

        var owner = new ApplicationUser { Id = ownerId, UserName = ownerId, Email = $"{ownerId}@test.local" };
        await db.Users.AddAsync(owner);

        var col = new Collection { Id = collectionId, Name = "Owner Collection", OwnerId = ownerId };
        await db.Collections.AddAsync(col);
        await db.SaveChangesAsync();

        var service = new AccessService(db, new StubUser(ownerId, isAdmin: false));
        var readable = await service.GetReadableCollectionIdsAsync(ownerId, [collectionId], CancellationToken.None);

        Assert.Contains(collectionId, readable);
    }

    [Fact]
    public async Task GetReadableCollectionIdsAsync_Viewer_CanReadCollection()
    {
        await using var db = fixture.CreateDbContext();
        var ownerId = Guid.NewGuid().ToString();
        var viewerId = Guid.NewGuid().ToString();
        var collectionId = Guid.NewGuid();

        var owner = new ApplicationUser { Id = ownerId, UserName = ownerId, Email = $"{ownerId}@test.local" };
        var viewer = new ApplicationUser { Id = viewerId, UserName = viewerId, Email = $"{viewerId}@test.local" };
        await db.Users.AddRangeAsync(owner, viewer);

        var col = new Collection { Id = collectionId, Name = "Viewer Collection", OwnerId = ownerId };
        await db.Collections.AddAsync(col);

        var member = new CollectionMember { CollectionId = collectionId, UserId = viewerId, Role = CollectionRole.Viewer };
        await db.CollectionMembers.AddAsync(member);
        await db.SaveChangesAsync();

        var service = new AccessService(db, new StubUser(viewerId, isAdmin: false));
        var readable = await service.GetReadableCollectionIdsAsync(viewerId, [collectionId], CancellationToken.None);

        Assert.Contains(collectionId, readable);
    }

    [Fact]
    public async Task GetReadableCollectionIdsAsync_Editor_CanReadCollection()
    {
        await using var db = fixture.CreateDbContext();
        var ownerId = Guid.NewGuid().ToString();
        var editorId = Guid.NewGuid().ToString();
        var collectionId = Guid.NewGuid();

        var owner = new ApplicationUser { Id = ownerId, UserName = ownerId, Email = $"{ownerId}@test.local" };
        var editor = new ApplicationUser { Id = editorId, UserName = editorId, Email = $"{editorId}@test.local" };
        await db.Users.AddRangeAsync(owner, editor);

        var col = new Collection { Id = collectionId, Name = "Editor Collection", OwnerId = ownerId };
        await db.Collections.AddAsync(col);

        var member = new CollectionMember { CollectionId = collectionId, UserId = editorId, Role = CollectionRole.Editor };
        await db.CollectionMembers.AddAsync(member);
        await db.SaveChangesAsync();

        var service = new AccessService(db, new StubUser(editorId, isAdmin: false));
        var readable = await service.GetReadableCollectionIdsAsync(editorId, [collectionId], CancellationToken.None);

        Assert.Contains(collectionId, readable);
    }

    [Fact]
    public async Task GetReadableCollectionIdsAsync_NonMember_CannotReadCollection()
    {
        await using var db = fixture.CreateDbContext();
        var ownerId = Guid.NewGuid().ToString();
        var strangerId = Guid.NewGuid().ToString();
        var collectionId = Guid.NewGuid();

        var owner = new ApplicationUser { Id = ownerId, UserName = ownerId, Email = $"{ownerId}@test.local" };
        var stranger = new ApplicationUser { Id = strangerId, UserName = strangerId, Email = $"{strangerId}@test.local" };
        await db.Users.AddRangeAsync(owner, stranger);

        var col = new Collection { Id = collectionId, Name = "Private Collection", OwnerId = ownerId };
        await db.Collections.AddAsync(col);
        await db.SaveChangesAsync();

        var service = new AccessService(db, new StubUser(strangerId, isAdmin: false));
        var readable = await service.GetReadableCollectionIdsAsync(strangerId, [collectionId], CancellationToken.None);

        Assert.DoesNotContain(collectionId, readable);
    }

    [Fact]
    public async Task GetReadableCollectionIdsAsync_Admin_CanReadAnyCollection()
    {
        await using var db = fixture.CreateDbContext();
        var ownerId = Guid.NewGuid().ToString();
        var adminId = Guid.NewGuid().ToString();
        var collectionId = Guid.NewGuid();

        var owner = new ApplicationUser { Id = ownerId, UserName = ownerId, Email = $"{ownerId}@test.local" };
        var admin = new ApplicationUser { Id = adminId, UserName = adminId, Email = $"{adminId}@test.local" };
        await db.Users.AddRangeAsync(owner, admin);

        var col = new Collection { Id = collectionId, Name = "Admin Visible Collection", OwnerId = ownerId };
        await db.Collections.AddAsync(col);
        await db.SaveChangesAsync();

        var service = new AccessService(db, new StubUser(adminId, isAdmin: true));
        var readable = await service.GetReadableCollectionIdsAsync(adminId, [collectionId], CancellationToken.None);

        Assert.Contains(collectionId, readable);
    }
}
