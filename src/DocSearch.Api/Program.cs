using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Asp.Versioning;
using DocSearch.Api.Infrastructure;
using DocSearch.Application;
using DocSearch.Application.Options;
using DocSearch.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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

// Authentication & JWT Bearer
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
    {
        var jwt = jwtOptions.Value;
        if (!string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
                NameClaimType = ClaimTypes.NameIdentifier,
                RoleClaimType = ClaimTypes.Role
            };
        }

        options.Events = new JwtBearerEvents
        {
            OnChallenge = async context =>
            {
                context.HandleResponse();
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.ContentType = "application/problem+json; charset=utf-8";

                    var traceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
                    var problem = new ProblemDetailsResponse
                    {
                        Type = "https://docsearch.local/errors/auth.unauthorized",
                        Title = "Unauthorized",
                        Status = StatusCodes.Status401Unauthorized,
                        Detail = "Authentication is required to access this resource.",
                        Instance = context.Request.Path,
                        Code = "auth.unauthorized",
                        TraceId = traceId
                    };

                    await context.Response.WriteAsJsonAsync(problem, context.HttpContext.RequestAborted);
                }
            },
            OnForbidden = async context =>
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    context.Response.ContentType = "application/problem+json; charset=utf-8";

                    var traceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
                    var problem = new ProblemDetailsResponse
                    {
                        Type = "https://docsearch.local/errors/auth.forbidden",
                        Title = "Forbidden",
                        Status = StatusCodes.Status403Forbidden,
                        Detail = "You do not have permission to access this resource.",
                        Instance = context.Request.Path,
                        Code = "auth.forbidden",
                        TraceId = traceId
                    };

                    await context.Response.WriteAsJsonAsync(problem, context.HttpContext.RequestAborted);
                }
            }
        };
    });

builder.Services.AddAuthorization();

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
    // Allow socket headroom so Kestrel does not abort TCP connection abruptly before writing 413 response
    options.Limits.MaxRequestBodySize = maxUploadBytes + (50L * 1024 * 1024);
});

// ProblemDetails and global exception handler (RFC 7807)
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(
    o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    })
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
        {
            var isPayloadTooLarge = context.ModelState.Values.Any(v =>
                v.Errors.Any(e =>
                    e.Exception is BadHttpRequestException badHttp && badHttp.StatusCode == StatusCodes.Status413PayloadTooLarge ||
                    e.Exception?.InnerException is BadHttpRequestException innerBadHttp && innerBadHttp.StatusCode == StatusCodes.Status413PayloadTooLarge ||
                    e.Exception is InvalidDataException ||
                    e.Exception?.InnerException is InvalidDataException ||
                    e.ErrorMessage.Contains("Request body too large", StringComparison.OrdinalIgnoreCase) ||
                    e.ErrorMessage.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase)));

            if (isPayloadTooLarge)
            {
                var tooLargeProblem = new ProblemDetailsResponse
                {
                    Type = "https://docsearch.local/errors/document.too_large",
                    Title = "Payload too large",
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Detail = "The uploaded file exceeds the allowed size limit.",
                    Instance = context.HttpContext.Request.Path,
                    Code = "document.too_large",
                    TraceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier
                };

                return new ObjectResult(tooLargeProblem)
                {
                    StatusCode = StatusCodes.Status413PayloadTooLarge,
                    ContentTypes = { "application/problem+json; charset=utf-8" }
                };
            }

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
                Instance = context.HttpContext.Request.Path,
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

app.UseAuthentication();
app.UseAuthorization();

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

try
{
    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<DocSearch.Infrastructure.Persistence.AppDbContext>();
    if (await dbContext.Database.CanConnectAsync())
    {
        if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
        {
            await DocSearch.Infrastructure.Identity.DevDatabaseSeeder.SeedDevUserAndCollectionAsync(dbContext);
        }
        await DocSearch.Infrastructure.Identity.AdminBootstrapSeeder.SeedAdminAsync(app.Services);
    }
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogWarning(ex, "Could not run database startup seeding. Ensure database is running.");
}

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program;
