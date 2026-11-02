using System.Diagnostics;
using System.Text.Json.Serialization;
using Asp.Versioning;
using DocSearch.Api.Infrastructure;
using DocSearch.Application;
using DocSearch.Application.Options;
using DocSearch.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serilog structured logging
builder.Host.UseSerilog((context, services, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

// Application & Infrastructure services and validated Options
builder.Services.AddApplication(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHostedService<DocSearch.Infrastructure.Ingestion.IngestionWorker>();

// Register temporary ICurrentUser stub only in Development/Testing per ADR/spec
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddScoped<DocSearch.Application.Abstractions.ICurrentUser, DocSearch.Infrastructure.Identity.DevCurrentUser>();
}

// Request size limits for uploads
var storageSection = builder.Configuration.GetSection(StorageOptions.SectionName);
var maxUploadMb = storageSection.GetValue<int>("MaxUploadMb");
if (maxUploadMb <= 0)
{
    maxUploadMb = 50;
}
var maxUploadBytes = (long)maxUploadMb * 1024 * 1024;

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadBytes;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadBytes;
});

// ProblemDetails and global exception handler (RFC 7807)
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState
                .Where(x => x.Value?.Errors.Count > 0)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value!.Errors.Select(e => e.ErrorMessage).ToArray()
                );

            var problem = new ProblemDetailsResponse
            {
                Type = "https://docsearch.local/errors/validation.failed",
                Title = "Validation failed",
                Status = StatusCodes.Status400BadRequest,
                Detail = "One or more validation errors occurred.",
                Code = "validation.failed",
                TraceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier,
                Errors = errors
            };

            return new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status400BadRequest,
                ContentTypes = { "application/problem+json; charset=utf-8" }
            };
        };
    });

// API Versioning (/api/v1)
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
#pragma warning disable AV0016 // Support test endpoints without explicit version segment
    options.AssumeDefaultVersionWhenUnspecified = true;
#pragma warning restore AV0016
    options.ReportApiVersions = true;
    options.ApiVersionReader = new UrlSegmentApiVersionReader();
})
.AddMvc()
.AddApiExplorer(options =>
{
    options.GroupNameFormat = "'v'VVV";
    options.SubstituteApiVersionInUrl = true;
});

// OpenAPI
builder.Services.AddOpenApi("v1");

var app = builder.Build();

// Global Exception Handling per RFC 7807
app.UseExceptionHandler();

app.UseSerilogRequestLogging();

app.UseRouting();

// Health check endpoints
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
    ResponseWriter = HealthCheckResponseWriter.WriteLiveResponseAsync
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthCheckResponseWriter.WriteReadyResponseAsync
});

// Scalar API documentation when enabled
var docsOptions = app.Services.GetRequiredService<IOptions<DocsOptions>>().Value;
if (docsOptions.Enabled)
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("DocSearch API")
               .WithTheme(ScalarTheme.Mars);
    });
}

app.MapControllers();

if (app.Environment.IsDevelopment())
{
    try
    {
        using var scope = app.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DocSearch.Infrastructure.Persistence.AppDbContext>();
        if (await dbContext.Database.CanConnectAsync())
        {
            await DocSearch.Infrastructure.Identity.DevDatabaseSeeder.SeedDevUserAndCollectionAsync(dbContext);
        }
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not seed development database. Ensure database is running.");
    }
}

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program;
