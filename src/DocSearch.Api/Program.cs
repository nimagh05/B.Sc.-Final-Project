using System.Diagnostics;
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

// ProblemDetails and global exception handler (RFC 7807)
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.Configure<ApiBehaviorOptions>(options =>
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

builder.Services.AddControllers();

// API Versioning (/api/v1)
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1, 0);
    options.AssumeDefaultVersionWhenUnspecified = true;
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

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program;
