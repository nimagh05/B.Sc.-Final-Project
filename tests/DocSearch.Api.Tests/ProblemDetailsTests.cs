using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DocSearch.Api.Tests;

public sealed class ProblemDetailsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ProblemDetailsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddControllers().AddApplicationPart(typeof(TestErrorsController).Assembly);
            });
        }).CreateClient();
    }

    [Fact]
    public async Task UnhandledException_Returns_500_ProblemJson_Without_StackTrace_Or_Paths()
    {
        var response = await _client.GetAsync("/api/test-errors/unhandled");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var rawBody = await response.Content.ReadAsStringAsync();

        // Must not leak internal stack traces, paths, or exception details
        Assert.DoesNotContain("Sensitive internal database connection", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/secret/path", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StackTrace", rawBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("at DocSearch.", rawBody, StringComparison.OrdinalIgnoreCase);

        using var jsonDoc = JsonDocument.Parse(rawBody);
        var root = jsonDoc.RootElement;

        Assert.Equal("https://docsearch.local/errors/server.error", root.GetProperty("type").GetString());
        Assert.Equal("Internal Server Error", root.GetProperty("title").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("server.error", root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.Equal("An unexpected error occurred. Please contact support or retry later.", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task NotFoundException_Returns_404_ProblemJson()
    {
        var response = await _client.GetAsync("/api/test-errors/not-found");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = jsonDoc.RootElement;

        Assert.Equal("https://docsearch.local/errors/resource.not_found", root.GetProperty("type").GetString());
        Assert.Equal("resource.not_found", root.GetProperty("code").GetString());
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("Document 123 does not exist.", root.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ValidationException_Returns_400_ProblemJson_With_Errors()
    {
        var response = await _client.GetAsync("/api/test-errors/validation");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = jsonDoc.RootElement;

        Assert.Equal("https://docsearch.local/errors/validation.failed", root.GetProperty("type").GetString());
        Assert.Equal("validation.failed", root.GetProperty("code").GetString());
        Assert.Equal(400, root.GetProperty("status").GetInt32());

        var errors = root.GetProperty("errors");
        Assert.True(errors.TryGetProperty("file", out var fileErrors));
        Assert.Contains(fileErrors.EnumerateArray(), e => e.GetString() == "Only PDF files are accepted.");
    }

    [Fact]
    public async Task ConflictException_Returns_409_ProblemJson()
    {
        var response = await _client.GetAsync("/api/test-errors/conflict");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = jsonDoc.RootElement;

        Assert.Equal("document.duplicate", root.GetProperty("code").GetString());
        Assert.Equal(409, root.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ForbiddenException_Returns_403_ProblemJson()
    {
        var response = await _client.GetAsync("/api/test-errors/forbidden");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = jsonDoc.RootElement;

        Assert.Equal("auth.forbidden", root.GetProperty("code").GetString());
        Assert.Equal(403, root.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task RateLimitException_Returns_429_With_RetryAfter_Header()
    {
        var response = await _client.GetAsync("/api/test-errors/rate-limit");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("60", response.Headers.RetryAfter?.ToString());

        using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = jsonDoc.RootElement;

        Assert.Equal("rate.limited", root.GetProperty("code").GetString());
        Assert.Equal(429, root.GetProperty("status").GetInt32());
    }
}
