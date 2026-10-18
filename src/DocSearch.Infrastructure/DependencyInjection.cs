using DocSearch.Application.Abstractions;
using DocSearch.Application.Options;
using DocSearch.Infrastructure.Extraction;
using DocSearch.Infrastructure.Health;
using DocSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocSearch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default");

        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseNpgsql(connectionString, npgsqlOptions =>
            {
                npgsqlOptions.UseVector();
            });
        });

        var extractionSection = configuration.GetSection(ExtractionOptions.SectionName);
        var extractorBaseUrl = extractionSection.GetValue<string>("ExtractorBaseUrl") ?? "http://127.0.0.1:8000";
        if (!extractorBaseUrl.EndsWith('/'))
        {
            extractorBaseUrl += "/";
        }

        services.AddHttpClient<IPdfExtractor, HttpPdfExtractor>(client =>
        {
            client.BaseAddress = new Uri(extractorBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(120);
        });

        services.AddHttpClient("ExtractorHealthCheck", client =>
        {
            client.BaseAddress = new Uri(extractorBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(3);
        });

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<ExtractorHealthCheck>("extractor", tags: ["ready"]);

        return services;
    }
}
