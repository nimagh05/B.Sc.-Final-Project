using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocSearch.Api.Tests.Fixtures;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Models;
using DocSearch.Application.Text;
using DocSearch.Domain;
using DocSearch.Infrastructure.Ai.Fakes;
using DocSearch.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DocSearch.Api.Tests.Controllers;

[Trait("Category", "Integration")]
[Collection(ApiTestCollection.Name)]
public sealed class AdminControllerIntegrationTests(ApiTestFixture fixture)
{
    private static readonly byte[] _validPdfBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nAdmin reindex test PDF\n%%EOF");
    private static readonly JsonSerializerOptions _jsonOptions = ApiTestFixture.JsonOptions;

    [Fact]
    public async Task Only_Admin_Can_Reindex()
    {
        using var factory = fixture.CreateFactory();

        var adminId = Guid.NewGuid().ToString("D");
        var adminEmail = $"admin_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(adminId, adminEmail);

        using var userClient = factory.CreateAuthenticatedClient(
            DevDatabaseSeeder.DefaultUserId,
            DevDatabaseSeeder.DefaultEmail,
            "User");
        using var adminClient = factory.CreateAuthenticatedClient(adminId, adminEmail, "Admin");

        // Create a collection and ingest a Ready document
        var colCreateResp = await userClient.PostAsJsonAsync("/api/v1/collections", new
        {
            name = $"reindex_acl_{Guid.NewGuid():N}",
            description = "Admin reindex test"
        });
        Assert.Equal(HttpStatusCode.Created, colCreateResp.StatusCode);
        var colBody = await colCreateResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        var collectionId = colBody.GetProperty("id").GetGuid();

        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(fileContent, "file", "admin_reindex.pdf");

        var uploadResp = await userClient.PostAsync($"/api/v1/collections/{collectionId}/documents", multipart);
        Assert.Equal(HttpStatusCode.Accepted, uploadResp.StatusCode);
        var uploadedDoc = await uploadResp.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(uploadedDoc);

        await WaitForDocumentReadyAsync(userClient, uploadedDoc.Id);

        // Simulate an embedding model swap by marking existing chunks with an older model
        await using (var db = fixture.CreateDbContext())
        {
            var chunks = db.Chunks.Where(c => c.DocumentId == uploadedDoc.Id).ToList();
            Assert.NotEmpty(chunks);
            foreach (var c in chunks)
            {
                c.EmbeddingModel = "old-model";
            }
            await db.SaveChangesAsync();
        }

        // Non-admin user gets 403 auth.forbidden
        var nonAdminReindex = await userClient.PostAsync($"/api/v1/admin/collections/{collectionId}/reindex", null);
        Assert.Equal(HttpStatusCode.Forbidden, nonAdminReindex.StatusCode);
        var forbiddenJson = await nonAdminReindex.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.forbidden", forbiddenJson.GetProperty("code").GetString());

        // Admin on missing collection gets 404 resource.not_found
        var missingColResp = await adminClient.PostAsync($"/api/v1/admin/collections/{Guid.NewGuid()}/reindex", null);
        Assert.Equal(HttpStatusCode.NotFound, missingColResp.StatusCode);

        // Admin triggers reindex -> 202 Accepted { jobId }
        var adminReindex = await adminClient.PostAsync($"/api/v1/admin/collections/{collectionId}/reindex", null);
        Assert.Equal(HttpStatusCode.Accepted, adminReindex.StatusCode);
        var reindexBody = await adminReindex.Content.ReadFromJsonAsync<ReindexJobResponse>(_jsonOptions);
        Assert.NotNull(reindexBody);
        Assert.NotEqual(Guid.Empty, reindexBody.JobId);

        // Non-admin cannot poll job status -> 403
        var nonAdminStatus = await userClient.GetAsync($"/api/v1/admin/reindex-jobs/{reindexBody.JobId}");
        Assert.Equal(HttpStatusCode.Forbidden, nonAdminStatus.StatusCode);

        // Admin polls job status to Completed
        var status = await WaitForReindexCompletedAsync(adminClient, reindexBody.JobId);
        Assert.Equal("Completed", status.Status);
        Assert.True(status.TotalChunks >= 1);
        Assert.Equal(status.TotalChunks, status.DoneChunks);
        Assert.Null(status.ErrorMessage);

        await using (var verifyDb = fixture.CreateDbContext())
        {
            var updatedModels = verifyDb.Chunks
                .Where(c => c.DocumentId == uploadedDoc.Id)
                .Select(c => c.EmbeddingModel)
                .ToList();
            Assert.All(updatedModels, m => Assert.Equal("bge-m3", m));
        }
    }

    [Fact]
    public async Task Concurrent_Reindex_On_Same_Collection_Returns_409()
    {
        var gate = new BlockingPassageEmbedder();
        using var factory = fixture.CreateFactory(
            configureServices: services =>
            {
                var existing = services.FirstOrDefault(d => d.ServiceType == typeof(IPassageEmbedder));
                if (existing != null)
                {
                    services.Remove(existing);
                }

                services.AddSingleton<IPassageEmbedder>(gate);
            },
            extraConfiguration: new Dictionary<string, string?>
            {
                ["Ai:Embedding:Model"] = "initial-model"
            });

        var adminId = Guid.NewGuid().ToString("D");
        var adminEmail = $"admin_409_{Guid.NewGuid():N}@test.local";
        await fixture.SeedUserAsync(adminId, adminEmail);

        using var userClient = factory.CreateAuthenticatedClient(
            DevDatabaseSeeder.DefaultUserId,
            DevDatabaseSeeder.DefaultEmail,
            "User");
        using var adminClient = factory.CreateAuthenticatedClient(adminId, adminEmail, "Admin");

        // Create collection and ingest a document under initial-model
        var colCreateResp = await userClient.PostAsJsonAsync("/api/v1/collections", new
        {
            name = $"reindex_409_{Guid.NewGuid():N}",
            description = "Concurrent reindex conflict test"
        });
        Assert.Equal(HttpStatusCode.Created, colCreateResp.StatusCode);
        var colBody = await colCreateResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        var collectionId = colBody.GetProperty("id").GetGuid();

        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        multipart.Add(fileContent, "file", "conflict_reindex.pdf");

        var uploadResp = await userClient.PostAsync($"/api/v1/collections/{collectionId}/documents", multipart);
        Assert.Equal(HttpStatusCode.Accepted, uploadResp.StatusCode);
        var uploadedDoc = await uploadResp.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(uploadedDoc);

        await WaitForDocumentReadyAsync(userClient, uploadedDoc.Id);

        // Mark existing chunks with an older embedding model and arm the gate so reindex blocks in Running
        await using (var db = fixture.CreateDbContext())
        {
            var chunks = db.Chunks.Where(c => c.DocumentId == uploadedDoc.Id).ToList();
            foreach (var c in chunks)
            {
                c.EmbeddingModel = "stale-model";
            }
            await db.SaveChangesAsync();
        }

        gate.ArmBlock();

        // First reindex returns 202 Accepted and enters Running (blocked inside EmbedPassagesAsync)
        var firstResp = await adminClient.PostAsync($"/api/v1/admin/collections/{collectionId}/reindex", null);
        Assert.Equal(HttpStatusCode.Accepted, firstResp.StatusCode);
        var firstJob = await firstResp.Content.ReadFromJsonAsync<ReindexJobResponse>(_jsonOptions);
        Assert.NotNull(firstJob);

        await gate.WaitUntilEnteredAsync(TimeSpan.FromSeconds(10));

        // Second concurrent reindex on same collection returns 409 Conflict (reindex.already_running)
        var secondResp = await adminClient.PostAsync($"/api/v1/admin/collections/{collectionId}/reindex", null);
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);
        Assert.Equal("application/problem+json", secondResp.Content.Headers.ContentType?.MediaType);

        var conflictJson = await secondResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("reindex.already_running", conflictJson.GetProperty("code").GetString());
        Assert.Equal(409, conflictJson.GetProperty("status").GetInt32());

        // Release the blocked embedder and confirm first job finishes
        gate.Release();
        var finalStatus = await WaitForReindexCompletedAsync(adminClient, firstJob.JobId);
        Assert.Equal("Completed", finalStatus.Status);

        // Once Completed, a subsequent reindex on the same collection is accepted again
        var thirdResp = await adminClient.PostAsync($"/api/v1/admin/collections/{collectionId}/reindex", null);
        Assert.Equal(HttpStatusCode.Accepted, thirdResp.StatusCode);
        var thirdJob = await thirdResp.Content.ReadFromJsonAsync<ReindexJobResponse>(_jsonOptions);
        Assert.NotNull(thirdJob);
        var thirdStatus = await WaitForReindexCompletedAsync(adminClient, thirdJob.JobId);
        Assert.Equal("Completed", thirdStatus.Status);
    }

    private static async Task WaitForDocumentReadyAsync(HttpClient client, Guid documentId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await client.GetAsync($"/api/v1/documents/{documentId}");
            if (resp.IsSuccessStatusCode)
            {
                var doc = await resp.Content.ReadFromJsonAsync<DocumentDetailResponse>(_jsonOptions);
                if (doc?.Status == DocumentStatus.Ready)
                {
                    return;
                }
                if (doc?.Status == DocumentStatus.Failed)
                {
                    Assert.Fail($"Document ingestion failed unexpectedly: {doc.ErrorMessage}");
                }
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Document {documentId} did not become Ready within timeout.");
    }

    private static async Task<ReindexJobStatusResponse> WaitForReindexCompletedAsync(HttpClient adminClient, Guid jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var resp = await adminClient.GetAsync($"/api/v1/admin/reindex-jobs/{jobId}");
            resp.EnsureSuccessStatusCode();
            var status = await resp.Content.ReadFromJsonAsync<ReindexJobStatusResponse>(_jsonOptions);
            Assert.NotNull(status);

            if (status.Status is "Completed" or "Failed")
            {
                return status;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Reindex job {jobId} did not complete within timeout.");
        return null!;
    }

    private sealed class BlockingPassageEmbedder : IPassageEmbedder
    {
        private volatile TaskCompletionSource<bool>? _entered;
        private volatile TaskCompletionSource<bool>? _release;

        public void ArmBlock()
        {
            _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task WaitUntilEnteredAsync(TimeSpan timeout)
        {
            if (_entered == null)
            {
                return;
            }

            using var cts = new CancellationTokenSource(timeout);
            await _entered.Task.WaitAsync(cts.Token);
        }

        public void Release()
        {
            _release?.TrySetResult(true);
            _release = null;
            _entered = null;
        }

        public async Task<IReadOnlyList<float[]>> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
        {
            var entered = _entered;
            var release = _release;
            if (entered != null && release != null)
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(ct);
            }

            using var generator = new FakeEmbeddingGenerator(new PersianTextNormalizer());
            var generated = await generator.GenerateAsync(passages, cancellationToken: ct);
            return generated.Select(e => e.Vector.ToArray()).ToList();
        }
    }
}
