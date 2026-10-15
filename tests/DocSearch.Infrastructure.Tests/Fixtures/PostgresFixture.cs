using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Fixtures;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply migrations to prepare the default test database
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public AppDbContext CreateDbContext(string? customConnectionString = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(customConnectionString ?? ConnectionString, o => o.UseVector())
            .Options;
        return new AppDbContext(options);
    }

    public async Task<string> CreateEmptyDatabaseAsync(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        builder.Database = "postgres";

        await using (var connection = new NpgsqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{databaseName}\";";
            await cmd.ExecuteNonQueryAsync();
        }

        builder.Database = databaseName;
        return builder.ConnectionString;
    }
}

[CollectionDefinition(Name)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit CollectionDefinition convention")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgresCollection";
}
