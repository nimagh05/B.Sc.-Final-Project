using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocSearch.Api.Tests.Fixtures;
using DocSearch.Application.Models;
using Xunit;

namespace DocSearch.Api.Tests.Controllers;

[Trait("Category", "Integration")]
[Collection(ApiTestCollection.Name)]
public sealed class AuthControllerIntegrationTests(ApiTestFixture fixture)
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Register_And_Login_HappyPath()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateAnonymousClient();

        var email = $"user_{Guid.NewGuid():N}@test.local";
        var password = "StrongPassword123!";

        // 1. Register
        var regResp = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, password, "Test User"));
        Assert.Equal(HttpStatusCode.Created, regResp.StatusCode);
        var regData = await regResp.Content.ReadFromJsonAsync<RegisterResponse>(_jsonOptions);
        Assert.NotNull(regData);
        Assert.Equal(email, regData.Email);

        // 2. Login
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var loginData = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions);
        Assert.NotNull(loginData);
        Assert.False(string.IsNullOrWhiteSpace(loginData.AccessToken));
        Assert.Contains("User", loginData.Roles);

        // 3. /me with received token
        using var authedClient = factory.CreateClient();
        authedClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", loginData.AccessToken);
        var meResp = await authedClient.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, meResp.StatusCode);
        var meData = await meResp.Content.ReadFromJsonAsync<CurrentUserResponse>(_jsonOptions);
        Assert.NotNull(meData);
        Assert.Equal(email, meData.Email);
    }

    [Fact]
    public async Task Register_Disabled_Returns_403_When_Config_Off()
    {
        using var factory = fixture.CreateFactory(extraConfiguration: new Dictionary<string, string?>
        {
            ["Auth:AllowSelfRegistration"] = "false"
        });
        using var client = factory.CreateAnonymousClient();

        var regResp = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest($"off_{Guid.NewGuid():N}@test.local", "StrongPassword123!", "Disabled Reg"));

        Assert.Equal(HttpStatusCode.Forbidden, regResp.StatusCode);
        var problem = await regResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.forbidden", problem.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("short", "Short password")]
    [InlineData("alllowercase123!", "Missing uppercase")]
    [InlineData("ALLUPPERCASE123!", "Missing lowercase")]
    [InlineData("NoDigitsInPassword!", "Missing digit")]
    public async Task Weak_Password_Rejected(string weakPassword, string reason)
    {
        _ = reason;
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateAnonymousClient();

        var regResp = await client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new RegisterRequest($"weak_{Guid.NewGuid():N}@test.local", weakPassword, "Weak User"));

        Assert.Equal(HttpStatusCode.BadRequest, regResp.StatusCode);
        var problem = await regResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("validation.failed", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_Wrong_Password_Returns_401()
    {
        using var factory = fixture.CreateFactory();
        using var client = factory.CreateAnonymousClient();

        var email = $"login_wrong_{Guid.NewGuid():N}@test.local";
        var password = "CorrectPassword123!";

        // Register first
        await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, password, "User"));

        // Login with wrong password
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, "WrongPassword123!"));
        Assert.Equal(HttpStatusCode.Unauthorized, loginResp.StatusCode);
        var problem = await loginResp.Content.ReadFromJsonAsync<JsonElement>(_jsonOptions);
        Assert.Equal("auth.invalid_credentials", problem.GetProperty("code").GetString());
    }
}
