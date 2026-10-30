using DocSearch.Application.Abstractions;
using DocSearch.Infrastructure.Identity;
using DocSearch.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

namespace DocSearch.Api.Tests.Fixtures;

public sealed class ApiTestFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .Build();

    private readonly string _tempStorageDir = Path.Combine(
        Path.GetTempPath(),
        "DocSearch_Api_Storage_" + Guid.NewGuid().ToString("N"));

    public string ConnectionString => _container.GetConnectionString();
    public string TempStorageDir => _tempStorageDir;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempStorageDir);
        await _container.StartAsync();

        Environment.SetEnvironmentVariable("ConnectionStrings__Default", ConnectionString);

        // Run migrations
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseVector())
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.MigrateAsync();

        // Seed dev user and default collection
        await DevDatabaseSeeder.SeedDevUserAndCollectionAsync(db);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();

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

    public WebApplicationFactory<Program> CreateFactory(
        Action<IServiceCollection>? configureServices = null,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureAppConfiguration((_, config) =>
            {
                var dict = new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = ConnectionString,
                    ["Ai:Embedding:Provider"] = "Fake",
                    ["Ai:Chat:Provider"] = "Fake",
                    ["Storage:RootPath"] = _tempStorageDir,
                    ["Storage:MaxUploadMb"] = "50",
                    ["Ingestion:MaxConcurrentDocuments"] = "1",
                    ["Ingestion:QueueCapacity"] = "50",
                    ["Jwt:SigningKey"] = "test_environment_jwt_signing_key_at_least_32_chars_long"
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
                // Re-register AppDbContext with container connection string
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
                    options.UseNpgsql(ConnectionString, npgsqlOptions =>
                    {
                        npgsqlOptions.UseVector();
                    });
                });

                // Replace IPdfExtractor with an offline test stub
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IPdfExtractor));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<IPdfExtractor, StubApiPdfExtractor>();

                configureServices?.Invoke(services);
            });
        });
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseVector())
            .Options;
        return new AppDbContext(options);
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
