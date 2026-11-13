using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocSearch.Api.Tests.Fixtures;
using DocSearch.Application.Models;
using DocSearch.Domain;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DocSearch.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection(ApiTestCollection.Name)]
public sealed class AclAndSecurityIntegrationTests(ApiTestFixture fixture)
{
    private static readonly JsonSerializerOptions _jsonOptions = ApiTestFixture.JsonOptions;
    private static readonly byte[] _validPdfBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nSample PDF for ACL Tests\n%%EOF");

    [Fact]
    public async Task Unauthenticated_Gets_401()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateAnonymousClient();

        // 1. GET /collections without token -> 401
        var colResp = await client.GetAsync("/api/v1/collections");
        Assert.Equal(HttpStatusCode.Unauthorized, colResp.StatusCode);
        var colProblem = await colResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.unauthorized", colProblem.GetProperty("code").GetString());

        // 2. GET /documents/{id} without token -> 401
        var docResp = await client.GetAsync($"/api/v1/documents/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, docResp.StatusCode);
        var docProblem = await docResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.unauthorized", docProblem.GetProperty("code").GetString());

        // 3. POST /ask without token -> 401
        var askResp = await client.PostAsJsonAsync("/api/v1/ask", new AskRequestDto("تست چیست؟", [Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.Unauthorized, askResp.StatusCode);
        var askProblem = await askResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.unauthorized", askProblem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NonMember_Gets_404_For_Collection_And_Document()
    {
        using var factory = fixture.CreateFactory();

        var userAId = Guid.NewGuid().ToString();
        var userAEmail = $"userA_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(userAId, userAEmail);
        using var clientA = factory.CreateAuthenticatedClient(userAId, userAEmail, "User");

        var userBId = Guid.NewGuid().ToString();
        var userBEmail = $"userB_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(userBId, userBEmail);
        using var clientB = factory.CreateAuthenticatedClient(userBId, userBEmail, "User");

        // User A creates collection
        var colResp = await clientA.PostAsJsonAsync("/api/v1/collections", new CreateCollectionRequest("User A Private Collection", "Private"));
        Assert.Equal(HttpStatusCode.Created, colResp.StatusCode);
        var colData = await colResp.Content.ReadFromJsonAsync<CollectionDto>(_jsonOptions);
        var colId = colData!.Id;

        // User A uploads document
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "secret.pdf");
        var upResp = await clientA.PostAsync($"/api/v1/collections/{colId}/documents", content);
        Assert.Equal(HttpStatusCode.Accepted, upResp.StatusCode);
        var upData = await upResp.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        var docId = upData!.Id;

        // User B tries to read collection -> 404 (non-enumeration rule)
        var getColResp = await clientB.GetAsync($"/api/v1/collections/{colId}");
        Assert.Equal(HttpStatusCode.NotFound, getColResp.StatusCode);

        // User B tries to list documents -> 404
        var listDocsResp = await clientB.GetAsync($"/api/v1/collections/{colId}/documents");
        Assert.Equal(HttpStatusCode.NotFound, listDocsResp.StatusCode);

        // User B tries to read document -> 404
        var getDocResp = await clientB.GetAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.NotFound, getDocResp.StatusCode);

        // User B tries to download document -> 404
        var dlDocResp = await clientB.GetAsync($"/api/v1/documents/{docId}/file");
        Assert.Equal(HttpStatusCode.NotFound, dlDocResp.StatusCode);

        // User B tries to delete document -> 404
        var delDocResp = await clientB.DeleteAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.NotFound, delDocResp.StatusCode);
    }

    [Fact]
    public async Task Viewer_Cannot_Upload_Or_Delete()
    {
        using var factory = fixture.CreateFactory();

        var ownerId = Guid.NewGuid().ToString();
        var ownerEmail = $"owner_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(ownerId, ownerEmail);
        using var ownerClient = factory.CreateAuthenticatedClient(ownerId, ownerEmail, "User");

        var viewerId = Guid.NewGuid().ToString();
        var viewerEmail = $"viewer_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(viewerId, viewerEmail);
        using var viewerClient = factory.CreateAuthenticatedClient(viewerId, viewerEmail, "User");

        // Owner creates collection
        var colResp = await ownerClient.PostAsJsonAsync("/api/v1/collections", new CreateCollectionRequest("Viewer Test Col", null));
        var colData = await colResp.Content.ReadFromJsonAsync<CollectionDto>(_jsonOptions);
        var colId = colData!.Id;

        // Owner uploads a document
        using var contentOwner = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        contentOwner.Add(fileContent, "file", "owner_doc.pdf");
        var upOwner = await ownerClient.PostAsync($"/api/v1/collections/{colId}/documents", contentOwner);
        var docId = (await upOwner.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions))!.Id;

        // Owner adds Viewer
        var addMemberResp = await ownerClient.PutAsJsonAsync(
            $"/api/v1/collections/{colId}/members",
            new UpsertMemberRequest(viewerEmail, CollectionRole.Viewer));
        Assert.Equal(HttpStatusCode.OK, addMemberResp.StatusCode);

        // Viewer can read collection
        var getColResp = await viewerClient.GetAsync($"/api/v1/collections/{colId}");
        Assert.Equal(HttpStatusCode.OK, getColResp.StatusCode);

        // Viewer cannot upload -> 403
        using var contentViewer = new MultipartFormDataContent();
        var vFileContent = new ByteArrayContent(_validPdfBytes);
        vFileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        contentViewer.Add(vFileContent, "file", "viewer_doc.pdf");
        var vUpResp = await viewerClient.PostAsync($"/api/v1/collections/{colId}/documents", contentViewer);
        Assert.Equal(HttpStatusCode.Forbidden, vUpResp.StatusCode);

        // Viewer cannot delete document -> 403
        var vDelResp = await viewerClient.DeleteAsync($"/api/v1/documents/{docId}");
        Assert.Equal(HttpStatusCode.Forbidden, vDelResp.StatusCode);
    }

    [Fact]
    public async Task Editor_Can_Upload()
    {
        using var factory = fixture.CreateFactory();

        var ownerId = Guid.NewGuid().ToString();
        var ownerEmail = $"owner_ed_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(ownerId, ownerEmail);
        using var ownerClient = factory.CreateAuthenticatedClient(ownerId, ownerEmail, "User");

        var editorId = Guid.NewGuid().ToString();
        var editorEmail = $"editor_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(editorId, editorEmail);
        using var editorClient = factory.CreateAuthenticatedClient(editorId, editorEmail, "User");

        // Owner creates collection
        var colResp = await ownerClient.PostAsJsonAsync("/api/v1/collections", new CreateCollectionRequest("Editor Upload Col", null));
        var colId = (await colResp.Content.ReadFromJsonAsync<CollectionDto>(_jsonOptions))!.Id;

        // Owner adds Editor
        await ownerClient.PutAsJsonAsync(
            $"/api/v1/collections/{colId}/members",
            new UpsertMemberRequest(editorEmail, CollectionRole.Editor));

        // Editor can upload -> 202
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "editor_doc.pdf");
        var upResp = await editorClient.PostAsync($"/api/v1/collections/{colId}/documents", content);
        Assert.Equal(HttpStatusCode.Accepted, upResp.StatusCode);
    }

    [Fact]
    public async Task Owner_Can_Manage_Members()
    {
        using var factory = fixture.CreateFactory();

        var ownerId = Guid.NewGuid().ToString();
        var ownerEmail = $"owner_mgt_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(ownerId, ownerEmail);
        using var ownerClient = factory.CreateAuthenticatedClient(ownerId, ownerEmail, "User");

        var targetUserId = Guid.NewGuid().ToString();
        var targetUserEmail = $"target_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(targetUserId, targetUserEmail);
        using var targetClient = factory.CreateAuthenticatedClient(targetUserId, targetUserEmail, "User");

        // 1. Owner creates collection
        var colResp = await ownerClient.PostAsJsonAsync("/api/v1/collections", new CreateCollectionRequest("Manage Members Col", null));
        var colId = (await colResp.Content.ReadFromJsonAsync<CollectionDto>(_jsonOptions))!.Id;

        // 2. Owner adds target user as Viewer
        var addResp = await ownerClient.PutAsJsonAsync(
            $"/api/v1/collections/{colId}/members",
            new UpsertMemberRequest(targetUserEmail, CollectionRole.Viewer));
        Assert.Equal(HttpStatusCode.OK, addResp.StatusCode);

        // 3. Owner lists members
        var listResp = await ownerClient.GetAsync($"/api/v1/collections/{colId}/members");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var members = await listResp.Content.ReadFromJsonAsync<List<CollectionMemberDto>>(_jsonOptions);
        Assert.NotNull(members);
        Assert.Contains(members, m => m.UserId == targetUserId && m.Role == CollectionRole.Viewer);

        // 4. Target user (Viewer) tries to add a member -> 403
        var failAdd = await targetClient.PutAsJsonAsync(
            $"/api/v1/collections/{colId}/members",
            new UpsertMemberRequest("other@test.local", CollectionRole.Viewer));
        Assert.Equal(HttpStatusCode.Forbidden, failAdd.StatusCode);

        // 5. Owner removes target user
        var delResp = await ownerClient.DeleteAsync($"/api/v1/collections/{colId}/members/{targetUserId}");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // 6. Target user now gets 404 for collection
        var afterDelResp = await targetClient.GetAsync($"/api/v1/collections/{colId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDelResp.StatusCode);
    }

    [Fact]
    public async Task Ask_Cannot_Bypass_Acl_Via_CollectionIds()
    {
        using var factory = fixture.CreateFactory();

        var userAId = Guid.NewGuid().ToString();
        var userAEmail = $"ask_userA_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(userAId, userAEmail);
        using var clientA = factory.CreateAuthenticatedClient(userAId, userAEmail, "User");

        var userBId = Guid.NewGuid().ToString();
        var userBEmail = $"ask_userB_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(userBId, userBEmail);
        using var clientB = factory.CreateAuthenticatedClient(userBId, userBEmail, "User");

        // User A creates collection
        var colResp = await clientA.PostAsJsonAsync("/api/v1/collections", new CreateCollectionRequest("Col A Secret", null));
        Assert.Equal(HttpStatusCode.Created, colResp.StatusCode);
        var colAId = (await colResp.Content.ReadFromJsonAsync<CollectionDto>(_jsonOptions))!.Id;

        // User B tries to ask using User A's unreadable collection alone -> 404
        var askResp = await clientB.PostAsJsonAsync("/api/v1/ask", new AskRequestDto("حداکثر واحد مجاز چقدر است؟", [colAId]));
        Assert.Equal(HttpStatusCode.NotFound, askResp.StatusCode);
        var problem = await askResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("resource.not_found", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_Can_Call_Admin_Endpoints()
    {
        using var factory = fixture.CreateFactory();

        var regularUserId = Guid.NewGuid().ToString();
        var regularEmail = $"reg_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(regularUserId, regularEmail);
        using var regularClient = factory.CreateAuthenticatedClient(regularUserId, regularEmail, "User");

        var adminUserId = Guid.NewGuid().ToString();
        var adminEmail = $"admin_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(adminUserId, adminEmail);
        using var adminClient = factory.CreateAuthenticatedClient(adminUserId, adminEmail, "Admin", "User");

        // Create a test collection
        var colResp = await regularClient.PostAsJsonAsync("/api/v1/collections", new CreateCollectionRequest("Admin Test Col", null));
        Assert.Equal(HttpStatusCode.Created, colResp.StatusCode);
        var colId = (await colResp.Content.ReadFromJsonAsync<CollectionDto>(_jsonOptions))!.Id;

        // 1. Regular user calling reindex -> 403 auth.forbidden
        var userAttempt = await regularClient.PostAsync($"/api/v1/admin/collections/{colId}/reindex", null);
        Assert.Equal(HttpStatusCode.Forbidden, userAttempt.StatusCode);
        var problem = await userAttempt.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.forbidden", problem.GetProperty("code").GetString());

        // 2. Admin calling reindex -> 202 Accepted { jobId }
        var adminResp = await adminClient.PostAsync($"/api/v1/admin/collections/{colId}/reindex", null);
        Assert.Equal(HttpStatusCode.Accepted, adminResp.StatusCode);
        var jobData = await adminResp.Content.ReadFromJsonAsync<ReindexJobResponse>(_jsonOptions);
        Assert.NotNull(jobData);
        Assert.NotEqual(Guid.Empty, jobData.JobId);

        // 3. Admin checking job status -> 200 OK
        ReindexJobStatusResponse? statusData = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var statusResp = await adminClient.GetAsync($"/api/v1/admin/reindex-jobs/{jobData.JobId}");
            Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
            statusData = await statusResp.Content.ReadFromJsonAsync<ReindexJobStatusResponse>(_jsonOptions);
            if (statusData?.Status is "Completed" or "Failed")
            {
                break;
            }
            await Task.Delay(100);
        }

        Assert.NotNull(statusData);
        Assert.Equal("Completed", statusData.Status);
    }
}
