using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocSearch.Api.Tests.Fixtures;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Models;
using DocSearch.Domain;
using DocSearch.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DocSearch.Api.Tests.Controllers;

[Trait("Category", "Integration")]
[Collection(ApiTestCollection.Name)]
public sealed class DocumentsControllerIntegrationTests(ApiTestFixture fixture)
{
    private static readonly byte[] _validPdfBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nSample valid PDF content for API test\n%%EOF");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task Upload_Pdf_Returns_202_And_LocationHeader()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "test_upload.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var rawJson = await response.Content.ReadAsStringAsync();
        using var jsonDoc = JsonDocument.Parse(rawJson);
        Assert.Equal("Uploaded", jsonDoc.RootElement.GetProperty("status").GetString());

        var docResponse = await response.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(docResponse);
        Assert.Equal(DevDatabaseSeeder.DefaultCollectionId, docResponse.CollectionId);
        Assert.Equal("test_upload.pdf", docResponse.FileName);
        Assert.Equal(DocumentStatus.Uploaded, docResponse.Status);
    }

    [Fact]
    public async Task Upload_NonPdf_Returns_400_ProblemDetails()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("NOT A VALID PDF"));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "invalid.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("document.invalid_file", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Upload_WrongExtension_Returns_400_ProblemDetails()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "test.txt");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("document.invalid_file", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Upload_TooLarge_Returns_413_ProblemDetails()
    {
        // Configure MaxUploadMb = 1 for this test
        using var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Storage:MaxUploadMb"] = "1"
        });
        using var client = factory.CreateClient();

        // 1.5 MB payload starting with valid %PDF- magic bytes
        var largePdfBytes = new byte[1500 * 1024];
        Encoding.UTF8.GetBytes("%PDF-1.7\n").CopyTo(largePdfBytes, 0);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(largePdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "large.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("document.too_large", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Upload_Duplicate_InSameCollection_Returns_409_Conflict()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var uniquePdfBytes = Encoding.UTF8.GetBytes($"%PDF-1.7\nUnique content {Guid.NewGuid()}\n%%EOF");

        var filesBefore = Directory.GetFiles(fixture.TempStorageDir, "*.pdf", SearchOption.AllDirectories).Length;

        // 1. First upload
        using var content1 = new MultipartFormDataContent();
        var fileContent1 = new ByteArrayContent(uniquePdfBytes);
        fileContent1.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content1.Add(fileContent1, "file", "original.pdf");

        var response1 = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content1);
        Assert.Equal(HttpStatusCode.Accepted, response1.StatusCode);

        var filesAfterFirst = Directory.GetFiles(fixture.TempStorageDir, "*.pdf", SearchOption.AllDirectories).Length;
        Assert.Equal(filesBefore + 1, filesAfterFirst);

        // 2. Second upload with same bytes to the same collection
        using var content2 = new MultipartFormDataContent();
        var fileContent2 = new ByteArrayContent(uniquePdfBytes);
        fileContent2.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content2.Add(fileContent2, "file", "duplicate.pdf");

        var response2 = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content2);

        Assert.Equal(HttpStatusCode.Conflict, response2.StatusCode);
        using var json = JsonDocument.Parse(await response2.Content.ReadAsStringAsync());
        Assert.Equal("document.duplicate", json.RootElement.GetProperty("code").GetString());

        // File storage must not contain orphan file for failed upload
        var filesAfterSecond = Directory.GetFiles(fixture.TempStorageDir, "*.pdf", SearchOption.AllDirectories).Length;
        Assert.Equal(filesAfterFirst, filesAfterSecond);
    }

    [Fact]
    public async Task Document_Becomes_Ready_And_Has_Chunks_In_Database()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var uniquePdfBytes = Encoding.UTF8.GetBytes($"%PDF-1.7\nContent for ingestion {Guid.NewGuid()}\n%%EOF");

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(uniquePdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "ingest_test.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var docResponse = await response.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(docResponse);

        // Poll until document becomes Ready (max 10 seconds)
        var ready = false;
        for (var i = 0; i < 50; i++)
        {
            await using var verifyDb = fixture.CreateDbContext();
            var doc = await verifyDb.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docResponse.Id);
            if (doc != null && doc.Status == DocumentStatus.Ready)
            {
                ready = true;
                break;
            }

            await Task.Delay(200);
        }

        await using var assertDb = fixture.CreateDbContext();
        var finalDoc = await assertDb.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docResponse.Id);
        Assert.True(ready, $"Document status did not transition to Ready in background worker. Status: {finalDoc?.Status}, Error: {finalDoc?.ErrorMessage}");

        var chunks = await assertDb.Chunks.Where(c => c.DocumentId == docResponse.Id).ToListAsync();
        Assert.NotEmpty(chunks);
    }

    [Fact]
    public async Task Delete_Removes_StoredFile_And_Cascades_Chunks_In_Database()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var uniquePdfBytes = Encoding.UTF8.GetBytes($"%PDF-1.7\nDelete test {Guid.NewGuid()}\n%%EOF");

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(uniquePdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "delete_test.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var docResponse = await response.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(docResponse);

        // Wait until Ready
        await using var db = fixture.CreateDbContext();
        Document? doc = null;
        for (var i = 0; i < 50; i++)
        {
            doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docResponse.Id);
            if (doc != null && doc.Status == DocumentStatus.Ready)
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.NotNull(doc);
        var diskFilePath = Path.Combine(fixture.TempStorageDir, doc.StoragePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(diskFilePath));

        // Delete via API
        var deleteResponse = await client.DeleteAsync($"/api/v1/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify document, chunks, and disk file are removed
        var docInDb = await db.Documents.FirstOrDefaultAsync(d => d.Id == doc.Id);
        Assert.Null(docInDb);

        var chunksCount = await db.Chunks.CountAsync(c => c.DocumentId == doc.Id);
        Assert.Equal(0, chunksCount);

        Assert.False(File.Exists(diskFilePath));
    }

    [Fact]
    public async Task Get_Document_Returns_Details_With_ChunkCount()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var uniquePdfBytes = Encoding.UTF8.GetBytes($"%PDF-1.7\nDetails test {Guid.NewGuid()}\n%%EOF");

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(uniquePdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "details.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);
        var docResponse = await response.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(docResponse);

        // Wait until Ready
        var ready = false;
        for (var i = 0; i < 50; i++)
        {
            await using var verifyDb = fixture.CreateDbContext();
            var d = await verifyDb.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == docResponse.Id);
            if (d?.Status == DocumentStatus.Ready)
            {
                ready = true;
                break;
            }

            await Task.Delay(200);
        }

        await using var assertDb = fixture.CreateDbContext();
        var finalDoc = await assertDb.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docResponse.Id);
        Assert.True(ready, $"Document did not become Ready. Status: {finalDoc?.Status}, Error: {finalDoc?.ErrorMessage}");

        var detailResponse = await client.GetAsync($"/api/v1/documents/{docResponse.Id}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);

        var rawDetailJson = await detailResponse.Content.ReadAsStringAsync();
        using var jsonDetailDoc = JsonDocument.Parse(rawDetailJson);
        Assert.Equal("Ready", jsonDetailDoc.RootElement.GetProperty("status").GetString());

        var details = await detailResponse.Content.ReadFromJsonAsync<DocumentDetailResponse>(_jsonOptions);
        Assert.NotNull(details);
        Assert.Equal(docResponse.Id, details.Id);
        Assert.Equal(DocumentStatus.Ready, details.Status);
        Assert.True(details.ChunkCount > 0);
    }

    [Fact]
    public async Task Get_Document_File_Downloads_OriginalPdf()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var uniquePdfBytes = Encoding.UTF8.GetBytes($"%PDF-1.7\nDownload test {Guid.NewGuid()}\n%%EOF");

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(uniquePdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "download.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);
        var docResponse = await response.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(docResponse);

        var downloadResponse = await client.GetAsync($"/api/v1/documents/{docResponse.Id}/file");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("application/pdf", downloadResponse.Content.Headers.ContentType?.MediaType);

        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(uniquePdfBytes, downloadedBytes);
    }

    [Fact]
    public async Task List_Documents_Returns_PagedResult_And_Filters_By_Status()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var listResponse = await client.GetAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents?page=1&pageSize=10");

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var paged = await listResponse.Content.ReadFromJsonAsync<PagedResult<DocumentResponse>>(_jsonOptions);
        Assert.NotNull(paged);
        Assert.True(paged.Total >= 0);
    }

    [Fact]
    public async Task Reprocess_Document_Reruns_Ingestion_Successfully()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        var uniquePdfBytes = Encoding.UTF8.GetBytes($"%PDF-1.7\nReprocess test {Guid.NewGuid()}\n%%EOF");

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(uniquePdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(fileContent, "file", "reprocess.pdf");

        var response = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            content);
        var docResponse = await response.Content.ReadFromJsonAsync<DocumentResponse>(_jsonOptions);
        Assert.NotNull(docResponse);

        // Wait until Ready
        for (var i = 0; i < 50; i++)
        {
            await using var verifyDb = fixture.CreateDbContext();
            var d = await verifyDb.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == docResponse.Id);
            if (d?.Status == DocumentStatus.Ready)
            {
                break;
            }

            await Task.Delay(200);
        }

        // Call Reprocess endpoint
        var reprocessResponse = await client.PostAsync($"/api/v1/documents/{docResponse.Id}/reprocess", null);
        Assert.Equal(HttpStatusCode.Accepted, reprocessResponse.StatusCode);

        // Wait until Ready again
        var readyAgain = false;
        for (var i = 0; i < 50; i++)
        {
            await using var verifyDb = fixture.CreateDbContext();
            var d = await verifyDb.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == docResponse.Id);
            if (d?.Status == DocumentStatus.Ready)
            {
                readyAgain = true;
                break;
            }

            await Task.Delay(200);
        }

        Assert.True(readyAgain);
    }

    [Fact]
    public async Task Reprocess_When_Document_Is_Processing_Returns_409_Conflict()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        await using var db = fixture.CreateDbContext();
        var document = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = DevDatabaseSeeder.DefaultCollectionId,
            FileName = "in_progress.pdf",
            StoragePath = "2026/10/dummy.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Processing, // Currently processing!
            UploadedBy = DevCurrentUser.DefaultUserId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.Documents.AddAsync(document);
        await db.SaveChangesAsync();

        var response = await client.PostAsync($"/api/v1/documents/{document.Id}/reprocess", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("document.conflict", json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Worker_Requeues_Stuck_Documents_On_Startup()
    {
        // 1. Manually insert a document in Processing status directly in DB
        await using var db = fixture.CreateDbContext();
        var fileStoragePath = Path.Combine(fixture.TempStorageDir, "2026", "10");
        Directory.CreateDirectory(fileStoragePath);
        var filePath = Path.Combine(fileStoragePath, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(filePath, _validPdfBytes);

        var relativePath = $"2026/10/{Path.GetFileName(filePath)}";

        var stuckDoc = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = DevDatabaseSeeder.DefaultCollectionId,
            FileName = "stuck_worker.pdf",
            StoragePath = relativePath,
            ContentType = "application/pdf",
            SizeBytes = _validPdfBytes.Length,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Processing, // Stuck in Processing
            UploadedBy = DevCurrentUser.DefaultUserId,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await db.Documents.AddAsync(stuckDoc);
        await db.SaveChangesAsync();

        // 2. Start a new factory (simulating app restart)
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient();

        // 3. Worker should recover the stuck document and process it to Ready
        var processedToReady = false;
        for (var i = 0; i < 50; i++)
        {
            await using var verifyDb = fixture.CreateDbContext();
            var d = await verifyDb.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == stuckDoc.Id);
            if (d != null && d.Status == DocumentStatus.Ready)
            {
                processedToReady = true;
                break;
            }

            await Task.Delay(200);
        }

        Assert.True(processedToReady, "Worker failed to recover and process stuck document on startup.");
    }

    [Fact]
    public async Task Viewer_Can_List_Get_Download_But_Forbidden_On_Upload_Delete_Reprocess()
    {
        await using var db = fixture.CreateDbContext();

        // 1. Create a viewer user and add to default collection with Viewer role
        var viewerUserId = Guid.NewGuid().ToString();
        var viewerUser = new ApplicationUser
        {
            Id = viewerUserId,
            UserName = $"viewer_{Guid.NewGuid():N}@test.com",
            Email = $"viewer_{Guid.NewGuid():N}@test.com",
            NormalizedUserName = $"VIEWER_{Guid.NewGuid():N}@TEST.COM",
            NormalizedEmail = $"VIEWER_{Guid.NewGuid():N}@TEST.COM",
            SecurityStamp = Guid.NewGuid().ToString()
        };
        await db.Users.AddAsync(viewerUser);
        await db.CollectionMembers.AddAsync(new CollectionMember
        {
            CollectionId = DevDatabaseSeeder.DefaultCollectionId,
            UserId = viewerUserId,
            Role = CollectionRole.Viewer
        });

        // Seed a document in the collection
        var fileStoragePath = Path.Combine(fixture.TempStorageDir, "2026", "10");
        Directory.CreateDirectory(fileStoragePath);
        var filePath = Path.Combine(fileStoragePath, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(filePath, _validPdfBytes);

        var doc = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = DevDatabaseSeeder.DefaultCollectionId,
            FileName = "viewer_test.pdf",
            StoragePath = $"2026/10/{Path.GetFileName(filePath)}",
            ContentType = "application/pdf",
            SizeBytes = _validPdfBytes.Length,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Ready,
            UploadedBy = DevCurrentUser.DefaultUserId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.Documents.AddAsync(doc);
        await db.SaveChangesAsync();

        // 2. Create factory with Viewer as current user
        using var factory = fixture.CreateFactory(services =>
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ICurrentUser));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }
            services.AddScoped<ICurrentUser>(_ => new TestCurrentUser(viewerUserId, viewerUser.Email!));
        });
        using var client = factory.CreateClient();

        // Viewer allowed operations (200 OK)
        var listResponse = await client.GetAsync($"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var getResponse = await client.GetAsync($"/api/v1/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var downloadResponse = await client.GetAsync($"/api/v1/documents/{doc.Id}/file");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);

        // Viewer forbidden operations (403 Forbidden)
        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        uploadContent.Add(fileContent, "file", "viewer_upload.pdf");

        var uploadResponse = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            uploadContent);
        Assert.Equal(HttpStatusCode.Forbidden, uploadResponse.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/v1/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);

        var reprocessResponse = await client.PostAsync($"/api/v1/documents/{doc.Id}/reprocess", null);
        Assert.Equal(HttpStatusCode.Forbidden, reprocessResponse.StatusCode);
    }

    [Fact]
    public async Task NonMember_Returns_404NotFound_On_Upload_And_Get()
    {
        await using var db = fixture.CreateDbContext();

        // 1. Create a non-member user (not in CollectionMembers)
        var nonMemberUserId = Guid.NewGuid().ToString();
        var nonMemberUser = new ApplicationUser
        {
            Id = nonMemberUserId,
            UserName = $"nonmember_{Guid.NewGuid():N}@test.com",
            Email = $"nonmember_{Guid.NewGuid():N}@test.com",
            NormalizedUserName = $"NONMEMBER_{Guid.NewGuid():N}@TEST.COM",
            NormalizedEmail = $"NONMEMBER_{Guid.NewGuid():N}@TEST.COM",
            SecurityStamp = Guid.NewGuid().ToString()
        };
        await db.Users.AddAsync(nonMemberUser);

        // Seed an existing document
        var fileStoragePath = Path.Combine(fixture.TempStorageDir, "2026", "10");
        Directory.CreateDirectory(fileStoragePath);
        var filePath = Path.Combine(fileStoragePath, $"{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(filePath, _validPdfBytes);

        var doc = new Document
        {
            Id = Guid.CreateVersion7(),
            CollectionId = DevDatabaseSeeder.DefaultCollectionId,
            FileName = "nonmember_test.pdf",
            StoragePath = $"2026/10/{Path.GetFileName(filePath)}",
            ContentType = "application/pdf",
            SizeBytes = _validPdfBytes.Length,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Ready,
            UploadedBy = DevCurrentUser.DefaultUserId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await db.Documents.AddAsync(doc);
        await db.SaveChangesAsync();

        // 2. Create factory with NonMember as current user
        using var factory = fixture.CreateFactory(services =>
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(ICurrentUser));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }
            services.AddScoped<ICurrentUser>(_ => new TestCurrentUser(nonMemberUserId, nonMemberUser.Email!));
        });
        using var client = factory.CreateClient();

        // Upload to collection -> 404
        using var uploadContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(_validPdfBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        uploadContent.Add(fileContent, "file", "nonmember_upload.pdf");

        var uploadResponse = await client.PostAsync(
            $"/api/v1/collections/{DevDatabaseSeeder.DefaultCollectionId}/documents",
            uploadContent);
        Assert.Equal(HttpStatusCode.NotFound, uploadResponse.StatusCode);

        // Get document details -> 404
        var getResponse = await client.GetAsync($"/api/v1/documents/{doc.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    [Fact]
    public async Task StartupRecovery_UnderBackpressure_ProcessesAllPendingDocuments_WithoutDeadlock()
    {
        await using var db = fixture.CreateDbContext();
        var fileStoragePath = Path.Combine(fixture.TempStorageDir, "2026", "10");
        Directory.CreateDirectory(fileStoragePath);

        var docIds = new List<Guid>();

        // 1. One document stuck in Processing
        var processingDocId = Guid.CreateVersion7();
        var procPath = Path.Combine(fileStoragePath, $"{processingDocId:N}.pdf");
        await File.WriteAllBytesAsync(procPath, _validPdfBytes);
        await db.Documents.AddAsync(new Document
        {
            Id = processingDocId,
            CollectionId = DevDatabaseSeeder.DefaultCollectionId,
            FileName = "backpressure_proc.pdf",
            StoragePath = $"2026/10/{Path.GetFileName(procPath)}",
            ContentType = "application/pdf",
            SizeBytes = _validPdfBytes.Length,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Status = DocumentStatus.Processing,
            UploadedBy = DevCurrentUser.DefaultUserId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        docIds.Add(processingDocId);

        // 2. Ten documents in Uploaded status (QueueCapacity 5 + 5 = 10 documents)
        for (var i = 0; i < 10; i++)
        {
            var id = Guid.CreateVersion7();
            var filePath = Path.Combine(fileStoragePath, $"{id:N}.pdf");
            await File.WriteAllBytesAsync(filePath, _validPdfBytes);
            await db.Documents.AddAsync(new Document
            {
                Id = id,
                CollectionId = DevDatabaseSeeder.DefaultCollectionId,
                FileName = $"backpressure_up_{i}.pdf",
                StoragePath = $"2026/10/{Path.GetFileName(filePath)}",
                ContentType = "application/pdf",
                SizeBytes = _validPdfBytes.Length,
                ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                Status = DocumentStatus.Uploaded,
                UploadedBy = DevCurrentUser.DefaultUserId,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i)
            });
            docIds.Add(id);
        }

        await db.SaveChangesAsync();

        // 3. Start factory with QueueCapacity = 5 and MaxConcurrentDocuments = 2
        using var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Ingestion:QueueCapacity"] = "5",
            ["Ingestion:MaxConcurrentDocuments"] = "2"
        });
        using var client = factory.CreateClient();

        // 4. Poll until all 11 documents reach Ready status
        var allReady = false;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(500);
            await using var verifyDb = fixture.CreateDbContext();
            var readyCount = await verifyDb.Documents
                .AsNoTracking()
                .Where(d => docIds.Contains(d.Id) && d.Status == DocumentStatus.Ready)
                .CountAsync();

            if (readyCount == docIds.Count)
            {
                allReady = true;
                break;
            }
        }

        Assert.True(allReady, "All 11 documents must transition to Ready under QueueCapacity=5 backpressure without deadlocking.");
    }
}

public sealed class TestCurrentUser(string userId, string email, bool isAdmin = false) : ICurrentUser
{
    public string UserId => userId;
    public string Email => email;
    public bool IsAdmin => isAdmin;
}
