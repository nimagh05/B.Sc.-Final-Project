using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocSearch.Application.Abstractions;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

namespace DocSearch.Api.Tests.Fixtures;

public sealed class ApiTestFixture : IAsyncLifetime
{
    public const string TestSigningKey = "test_environment_jwt_signing_key_at_least_32_chars_long";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .Build();

    private readonly string _tempStorageDir = Path.Combine(
        Path.GetTempPath(),
        "DocSearch_Api_Storage_" + Guid.NewGuid().ToString("N"));

    public string ConnectionString => _postgres.GetConnectionString();
    public string TempStorageDir => _tempStorageDir;
    public IServiceProvider? Services { get; set; }

    public static string CreateJwtToken(string userId, string email, params string[] roles)
    {
        var effectiveRoles = roles.Length > 0 ? roles : ["User"];
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.NameIdentifier, userId),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        foreach (var role in effectiveRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var tokenDescriptor = new JwtSecurityToken(
            issuer: "docsearch",
            audience: "docsearch",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(tokenDescriptor);
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempStorageDir);
        await _postgres.StartAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ConnectionString);

        // Run migrations and seed on a standalone AppDbContext, then reload types on its connection
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseVector())
            .Options;

        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();

            var npgsqlConn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
            if (npgsqlConn.State != System.Data.ConnectionState.Open)
            {
                await npgsqlConn.OpenAsync();
            }
            await npgsqlConn.ReloadTypesAsync();

            await DevDatabaseSeeder.SeedDevUserAndCollectionAsync(db);
        }

        if (Services != null)
        {
            using var scope = Services.CreateScope();
            var serviceDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var conn = (Npgsql.NpgsqlConnection)serviceDb.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
            {
                await conn.OpenAsync();
            }
            await conn.ReloadTypesAsync();
        }

        Npgsql.NpgsqlConnection.ClearAllPools();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();

        if (Directory.Exists(_tempStorageDir))
        {
            try
            {
                Directory.Delete(_tempStorageDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    public ApiWebApplicationFactory CreateFactory(
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null)
    {
        return new ApiWebApplicationFactory(ConnectionString, _tempStorageDir, configureServices, extraConfiguration);
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseVector())
            .Options;
        return new AppDbContext(options);
    }

    public async Task SeedUserAsync(string userId, string email)
    {
        await using var db = CreateDbContext();
        var exists = await db.Users.AnyAsync(u => u.Id == userId);
        if (!exists)
        {
            await db.Users.AddAsync(new ApplicationUser
            {
                Id = userId,
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString("D")
            });
            await db.SaveChangesAsync();
        }
    }
}

public sealed class ApiWebApplicationFactory(
    string connectionString,
    string tempStorageDir,
    Action<IServiceCollection>? configureServices = null,
    IReadOnlyDictionary<string, string?>? extraConfiguration = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = connectionString,
                ["Ai:Embedding:Provider"] = "Fake",
                ["Ai:Chat:Provider"] = "Fake",
                ["Storage:RootPath"] = tempStorageDir,
                ["Storage:MaxUploadMb"] = "50",
                ["Ingestion:MaxConcurrentDocuments"] = "1",
                ["Ingestion:QueueCapacity"] = "50",
                ["Jwt:SigningKey"] = ApiTestFixture.TestSigningKey,
                ["RateLimiting:AuthPermitLimit"] = "1000",
                ["RateLimiting:AuthIpPermitLimit"] = "10000",
                ["RateLimiting:AskPermitLimit"] = "1000",
                ["RateLimiting:UploadPermitLimit"] = "1000"
            };

            if (extraConfiguration != null)
            {
                foreach (var (k, v) in extraConfiguration)
                {
                    dict[k] = v;
                }
            }

            config.AddInMemoryCollection(dict);
        });

        builder.ConfigureServices(services =>
        {
            var dbContextDescriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<AppDbContext>) ||
                d.ServiceType == typeof(DbContextOptions) ||
                d.ServiceType == typeof(AppDbContext)).ToList();

            foreach (var d in dbContextDescriptors)
            {
                services.Remove(d);
            }

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseNpgsql(connectionString, npgsqlOptions =>
                {
                    npgsqlOptions.UseVector();
                });
            });

            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IPdfExtractor));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<IPdfExtractor, StubApiPdfExtractor>();

            configureServices?.Invoke(services);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ApiTestFixture.CreateJwtToken(DevDatabaseSeeder.DefaultUserId, DevDatabaseSeeder.DefaultEmail, "User"));
    }

    public HttpClient CreateAnonymousClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    public HttpClient CreateAuthenticatedClient(string userId, string email, params string[] roles)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            ApiTestFixture.CreateJwtToken(userId, email, roles));
        return client;
    }
}

public sealed class StubApiPdfExtractor : IPdfExtractor
{
    public Task<IReadOnlyList<PageText>> ExtractAsync(Stream pdf, CancellationToken ct)
    {
        var pages = new List<PageText>
        {
            new(PageNumber: 1, Text: "ماده ۱: آیین‌نامه دانشجویی دانشگاه تهران.", Quality: 1.0, NeedsOcr: false),
            new(PageNumber: 2, Text: "ماده ۲: ضوابط انتخاب واحد در نیمسال تحصیلی.", Quality: 1.0, NeedsOcr: false)
        };

        return Task.FromResult<IReadOnlyList<PageText>>(pages);
    }
}

[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit CollectionDefinition")]
public sealed class ApiTestCollection : ICollectionFixture<ApiTestFixture>
{
    public const string Name = "ApiTestCollection";
}
