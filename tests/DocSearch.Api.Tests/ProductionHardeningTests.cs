using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocSearch.Api.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DocSearch.Api.Tests;

[Collection(ApiTestCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProductionHardeningTests(ApiTestFixture fixture)
{
    private static readonly JsonSerializerOptions _jsonOptions = ApiTestFixture.JsonOptions;

    [Fact]
    public async Task RateLimit_Returns_429_With_RetryAfter()
    {
        // Configure tight rate limit of 3 requests per minute for auth policy
        var customFactory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:AuthPermitLimit"] = "3",
            ["RateLimiting:AuthWindowMinutes"] = "1"
        });

        var client = customFactory.CreateAnonymousClient();
        var loginPayload = new { email = "ratelimit_probe@docsearch.local", password = "InvalidPassword123!" };

        // Send 3 requests (within limit)
        for (var i = 0; i < 3; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/login", loginPayload);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, res.StatusCode);
        }

        // 4th request must trigger 429 Too Many Requests
        var rateLimitedRes = await client.PostAsJsonAsync("/api/v1/auth/login", loginPayload);
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimitedRes.StatusCode);
        Assert.Equal("application/problem+json", rateLimitedRes.Content.Headers.ContentType?.MediaType);

        // Retry-After header must be present and parse to a positive integer
        Assert.True(
            rateLimitedRes.Headers.Contains("Retry-After") || rateLimitedRes.Content.Headers.Contains("Retry-After"),
            "Retry-After header must be present on 429 responses.");

        var retryAfterValue = rateLimitedRes.Headers.RetryAfter?.Delta?.TotalSeconds
            ?? double.Parse(rateLimitedRes.Headers.GetValues("Retry-After").First(), CultureInfo.InvariantCulture);
        Assert.True(retryAfterValue > 0, "Retry-After delta must be greater than zero.");

        var body = await rateLimitedRes.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("https://docsearch.local/errors/rate.limited", body.GetProperty("type").GetString());
        Assert.Equal("Too Many Requests", body.GetProperty("title").GetString());
        Assert.Equal(429, body.GetProperty("status").GetInt32());
        Assert.Equal("rate.limited", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal("/api/v1/auth/login", body.GetProperty("instance").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Errors_Are_ProblemJson()
    {
        var factory = fixture.CreateFactory();
        var anonClient = factory.CreateAnonymousClient();

        // 1. 401 Unauthorized (calling protected endpoint anonymously)
        var res401 = await anonClient.GetAsync("/api/v1/collections");
        Assert.Equal(HttpStatusCode.Unauthorized, res401.StatusCode);
        Assert.Equal("application/problem+json", res401.Content.Headers.ContentType?.MediaType);

        var json401 = await res401.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.unauthorized", json401.GetProperty("code").GetString());
        Assert.Equal(401, json401.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json401.GetProperty("traceId").GetString()));

        // 2. 403 Forbidden (standard user accessing admin endpoint)
        var userClient = factory.CreateAuthenticatedClient(Guid.NewGuid().ToString("D"), "user@test.local", "User");
        var res403 = await userClient.PostAsync($"/api/v1/admin/collections/{Guid.NewGuid()}/reindex", null);
        Assert.Equal(HttpStatusCode.Forbidden, res403.StatusCode);
        Assert.Equal("application/problem+json", res403.Content.Headers.ContentType?.MediaType);

        var json403 = await res403.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.forbidden", json403.GetProperty("code").GetString());
        Assert.Equal(403, json403.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json403.GetProperty("traceId").GetString()));

        // 3. 404 Not Found on unmapped route
        var res404 = await anonClient.GetAsync("/api/v1/totally-unmapped-route-xyz");
        Assert.Equal(HttpStatusCode.NotFound, res404.StatusCode);
        Assert.Equal("application/problem+json", res404.Content.Headers.ContentType?.MediaType);

        var json404 = await res404.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("resource.not_found", json404.GetProperty("code").GetString());
        Assert.Equal(404, json404.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json404.GetProperty("traceId").GetString()));

        // 4. 429 Rate Limited
        var rateLimitFactory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:AuthPermitLimit"] = "1",
            ["RateLimiting:AuthWindowMinutes"] = "1"
        });
        var rlClient = rateLimitFactory.CreateAnonymousClient();
        await rlClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "single@test.local", password = "p" });
        var res429 = await rlClient.PostAsJsonAsync("/api/v1/auth/login", new { email = "single@test.local", password = "p" });
        Assert.Equal(HttpStatusCode.TooManyRequests, res429.StatusCode);
        Assert.Equal("application/problem+json", res429.Content.Headers.ContentType?.MediaType);

        var json429 = await res429.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("rate.limited", json429.GetProperty("code").GetString());
        Assert.Equal(429, json429.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json429.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task Cors_Allows_Only_Configured_Origin()
    {
        var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins:0"] = "http://localhost:5173"
        });
        var client = factory.CreateAnonymousClient();

        // 1. Allowed origin
        var requestAllowed = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        requestAllowed.Headers.Add("Origin", "http://localhost:5173");

        var responseAllowed = await client.SendAsync(requestAllowed);
        Assert.Equal(HttpStatusCode.OK, responseAllowed.StatusCode);
        Assert.True(responseAllowed.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("http://localhost:5173", responseAllowed.Headers.GetValues("Access-Control-Allow-Origin").First());
        Assert.True(responseAllowed.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.Equal("true", responseAllowed.Headers.GetValues("Access-Control-Allow-Credentials").First());

        // 2. Disallowed origin
        var requestDisallowed = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        requestDisallowed.Headers.Add("Origin", "http://evil.malicious-site.local");

        var responseDisallowed = await client.SendAsync(requestDisallowed);
        Assert.Equal(HttpStatusCode.OK, responseDisallowed.StatusCode);
        Assert.False(responseDisallowed.Headers.Contains("Access-Control-Allow-Origin"));

        // 3. Preflight OPTIONS request for allowed origin
        var preflightAllowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        preflightAllowed.Headers.Add("Origin", "http://localhost:5173");
        preflightAllowed.Headers.Add("Access-Control-Request-Method", "POST");

        var preflightAllowedRes = await client.SendAsync(preflightAllowed);
        Assert.True(preflightAllowedRes.IsSuccessStatusCode);
        Assert.True(preflightAllowedRes.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("http://localhost:5173", preflightAllowedRes.Headers.GetValues("Access-Control-Allow-Origin").First());

        // 4. Preflight OPTIONS request for disallowed origin
        var preflightDisallowed = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/login");
        preflightDisallowed.Headers.Add("Origin", "http://evil.malicious-site.local");
        preflightDisallowed.Headers.Add("Access-Control-Request-Method", "POST");

        var preflightDisallowedRes = await client.SendAsync(preflightDisallowed);
        Assert.False(preflightDisallowedRes.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Security_Headers_Present()
    {
        var factory = fixture.CreateFactory();
        var client = factory.CreateAnonymousClient();

        // Test normal endpoint
        var liveResponse = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);

        Assert.True(liveResponse.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", liveResponse.Headers.GetValues("X-Content-Type-Options").First());

        Assert.True(liveResponse.Headers.Contains("Referrer-Policy"));
        Assert.Equal("no-referrer", liveResponse.Headers.GetValues("Referrer-Policy").First());

        Assert.True(liveResponse.Headers.Contains("X-Frame-Options"));
        Assert.Equal("DENY", liveResponse.Headers.GetValues("X-Frame-Options").First());

        // Test error response (404 unmapped route)
        var errorResponse = await client.GetAsync("/api/v1/not-found-route");
        Assert.Equal(HttpStatusCode.NotFound, errorResponse.StatusCode);

        Assert.True(errorResponse.Headers.Contains("X-Content-Type-Options"));
        Assert.Equal("nosniff", errorResponse.Headers.GetValues("X-Content-Type-Options").First());

        Assert.True(errorResponse.Headers.Contains("Referrer-Policy"));
        Assert.Equal("no-referrer", errorResponse.Headers.GetValues("Referrer-Policy").First());

        Assert.True(errorResponse.Headers.Contains("X-Frame-Options"));
        Assert.Equal("DENY", errorResponse.Headers.GetValues("X-Frame-Options").First());
    }

    [Fact]
    public async Task ConcurrentRequests_SmokeTest_Returns_No_5xx()
    {
        var factory = fixture.CreateFactory();
        var client = factory.CreateClient();

        // 20 concurrent requests firing concurrently
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var path = i % 2 == 0 ? "/health/live" : "/api/v1/collections";
            return await client.GetAsync(path);
        });

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(20, responses.Length);
        foreach (var resp in responses)
        {
            Assert.True((int)resp.StatusCode < 500, $"Expected non-5xx status, got {resp.StatusCode}");
        }
    }

    [Fact]
    public async Task Auth_RateLimit_Blocks_PasswordSpraying_Across_Different_Emails_From_Same_Ip()
    {
        var customFactory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:AuthPermitLimit"] = "10",
            ["RateLimiting:AuthIpPermitLimit"] = "5",
            ["RateLimiting:AuthWindowMinutes"] = "1"
        });

        var client = customFactory.CreateAnonymousClient();

        // First 5 requests with 5 distinct email addresses stay within Layer 1 IP ceiling (5)
        for (var i = 1; i <= 5; i++)
        {
            var payload = new { email = $"spray{i}@example.com", password = "InvalidPassword123!" };
            var res = await client.PostAsJsonAsync("/api/v1/auth/login", payload);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, res.StatusCode);
        }

        // 6th request with yet another distinct email must be blocked by Layer 1 per-IP GlobalLimiter
        var spray6Payload = new { email = "spray6@example.com", password = "InvalidPassword123!" };
        var rateLimitedRes = await client.PostAsJsonAsync("/api/v1/auth/login", spray6Payload);

        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimitedRes.StatusCode);
        Assert.Equal("application/problem+json", rateLimitedRes.Content.Headers.ContentType?.MediaType);
        Assert.True(
            rateLimitedRes.Headers.Contains("Retry-After") || rateLimitedRes.Content.Headers.Contains("Retry-After"),
            "Retry-After header must be present on 429 responses.");

        var retryAfterValue = rateLimitedRes.Headers.RetryAfter?.Delta?.TotalSeconds
            ?? double.Parse(rateLimitedRes.Headers.GetValues("Retry-After").First(), CultureInfo.InvariantCulture);
        Assert.True(retryAfterValue > 0, "Retry-After delta must be greater than zero.");

        var body = await rateLimitedRes.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("rate.limited", body.GetProperty("code").GetString());
        Assert.Equal(429, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
    }
}
