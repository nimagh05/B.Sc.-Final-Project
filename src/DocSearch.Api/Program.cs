using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Asp.Versioning;
using DocSearch.Api.Infrastructure;
using DocSearch.Application;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using DocSearch.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
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

// Graceful shutdown timeout
builder.Services.Configure<HostOptions>(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
});

// Reverse proxy forwarded headers (X-Forwarded-For, X-Forwarded-Proto)
builder.Services.AddOptions<ForwardedHeadersOptions>()
    .Configure<IOptions<NetworkOptions>>((options, networkOptions) =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;

        foreach (var cidr in networkOptions.Value.TrustedProxyCidrs)
        {
            if (!string.IsNullOrWhiteSpace(cidr))
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        }
    });

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

                    await context.Response.WriteAsJsonAsync(
                        problem,
                        options: (JsonSerializerOptions?)null,
                        contentType: "application/problem+json; charset=utf-8",
                        cancellationToken: context.HttpContext.RequestAborted);
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

                    await context.Response.WriteAsJsonAsync(
                        problem,
                        options: (JsonSerializerOptions?)null,
                        contentType: "application/problem+json; charset=utf-8",
                        cancellationToken: context.HttpContext.RequestAborted);
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

// Strict CORS from configuration
var corsSection = builder.Configuration.GetSection(CorsOptions.SectionName);
var corsOptions = corsSection.Get<CorsOptions>() ?? new CorsOptions();
var allowedOrigins = corsOptions.AllowedOrigins is { Length: > 0 }
    ? corsOptions.AllowedOrigins
    : ["http://localhost:5173"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Response compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// Rate limiting (SPEC-DATA-AND-API.md §2.7)
builder.Services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiterOptions.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/problem+json; charset=utf-8";

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfterSpan))
        {
            var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfterSpan.TotalSeconds));
            context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
        }

        var traceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
        var problem = new ProblemDetailsResponse
        {
            Type = "https://docsearch.local/errors/rate.limited",
            Title = "Too Many Requests",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Rate limit exceeded. Please retry later.",
            Instance = context.HttpContext.Request.Path,
            Code = "rate.limited",
            TraceId = traceId
        };

        await context.HttpContext.Response.WriteAsJsonAsync(
            problem,
            options: (JsonSerializerOptions?)null,
            contentType: "application/problem+json; charset=utf-8",
            cancellationToken: cancellationToken);
    };

    // Layer 1: Anti-password-spraying per-IP rate limit on /api/v1/auth/* (default 100 req/min/IP)
    limiterOptions.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var rateOpts = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        if (!rateOpts.Enabled ||
            !httpContext.Request.Path.StartsWithSegments("/api/v1/auth", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"auth-ip:{ip}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateOpts.AuthIpPermitLimit,
                Window = TimeSpan.FromMinutes(rateOpts.AuthWindowMinutes),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    // Layer 2: Auth policy: 10 req/min per IP:Account against single-account brute force
    limiterOptions.AddPolicy("auth", httpContext =>
    {
        var rateOpts = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        if (!rateOpts.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var account = httpContext.Items["AuthAccount"] as string;
        var partitionKey = string.IsNullOrEmpty(account) ? ip : $"{ip}:{account}";

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateOpts.AuthPermitLimit,
                Window = TimeSpan.FromMinutes(rateOpts.AuthWindowMinutes),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    // 2. Ask policy: 30 req/min/user (partitioned by authenticated user ID, fallback to IP)
    limiterOptions.AddPolicy("ask", httpContext =>
    {
        var rateOpts = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        if (!rateOpts.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            userId,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateOpts.AskPermitLimit,
                Window = TimeSpan.FromMinutes(rateOpts.AskWindowMinutes),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    // 3. Upload policy: 20 req/hour/user (partitioned by authenticated user ID, fallback to IP)
    limiterOptions.AddPolicy("upload", httpContext =>
    {
        var rateOpts = httpContext.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        if (!rateOpts.Enabled)
        {
            return RateLimitPartition.GetNoLimiter("none");
        }

        var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? httpContext.User.FindFirst("sub")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            userId,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = rateOpts.UploadPermitLimit,
                Window = TimeSpan.FromHours(rateOpts.UploadWindowHours),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
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
                    (e.Exception is InvalidDataException invEx && invEx.Message.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase)) ||
                    (e.Exception?.InnerException is InvalidDataException innerInvEx && innerInvEx.Message.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase)) ||
                    e.ErrorMessage.Contains("Request body too large", StringComparison.OrdinalIgnoreCase) ||
                    e.ErrorMessage.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase)));

            if (isPayloadTooLarge)
            {
                throw new FileTooLargeException("The uploaded file exceeds the allowed size limit.");
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

app.UseForwardedHeaders();

// Global Exception Handling per RFC 7807
app.UseExceptionHandler();

// StatusCodePages per RFC 7807 (401/403/404/405/413/415/429)
app.UseStatusCodePages(async statusCodeContext =>
{
    var response = statusCodeContext.HttpContext.Response;
    if (response.HasStarted ||
        (response.ContentLength.HasValue && response.ContentLength.Value > 0) ||
        !string.IsNullOrEmpty(response.ContentType))
    {
        return;
    }

    var statusCode = response.StatusCode;
    var path = statusCodeContext.HttpContext.Request.Path;
    var traceId = Activity.Current?.Id ?? statusCodeContext.HttpContext.TraceIdentifier;

    string code;
    string title;
    string detail;

    switch (statusCode)
    {
        case StatusCodes.Status401Unauthorized:
            code = "auth.unauthorized";
            title = "Unauthorized";
            detail = "Authentication is required to access this resource.";
            break;
        case StatusCodes.Status403Forbidden:
            code = "auth.forbidden";
            title = "Forbidden";
            detail = "You do not have permission to access this resource.";
            break;
        case StatusCodes.Status404NotFound:
            code = "resource.not_found";
            title = "Not Found";
            detail = "The requested resource was not found.";
            break;
        case StatusCodes.Status405MethodNotAllowed:
            code = "http.method_not_allowed";
            title = "Method Not Allowed";
            detail = "The HTTP method is not allowed for this resource.";
            break;
        case StatusCodes.Status413PayloadTooLarge:
            code = "document.too_large";
            title = "Payload Too Large";
            detail = "The uploaded file exceeds the allowed size limit.";
            break;
        case StatusCodes.Status415UnsupportedMediaType:
            code = "http.unsupported_media_type";
            title = "Unsupported Media Type";
            detail = "The request media type is not supported.";
            break;
        case StatusCodes.Status429TooManyRequests:
            code = "rate.limited";
            title = "Too Many Requests";
            detail = "Rate limit exceeded. Please retry later.";
            break;
        default:
            code = "http.error";
            title = "HTTP Error";
            detail = $"HTTP error with status code {statusCode}.";
            break;
    }

    response.ContentType = "application/problem+json; charset=utf-8";
    var problem = new ProblemDetailsResponse
    {
        Type = $"https://docsearch.local/errors/{code}",
        Title = title,
        Status = statusCode,
        Detail = detail,
        Instance = path,
        Code = code,
        TraceId = traceId
    };

    await response.WriteAsJsonAsync(
        problem,
        options: (JsonSerializerOptions?)null,
        contentType: "application/problem+json; charset=utf-8",
        cancellationToken: statusCodeContext.HttpContext.RequestAborted);
});

// Security headers
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.XFrameOptions = "DENY";
    await next();
});

app.UseResponseCompression();

app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("TraceId", Activity.Current?.Id ?? httpContext.TraceIdentifier);
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(userId))
            {
                diagnosticContext.Set("UserId", userId);
            }
        }
    };
});

app.UseRouting();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

// Extract target email for auth rate limiting partition without breaking MVC body reading
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/auth", StringComparison.OrdinalIgnoreCase) &&
        HttpMethods.IsPost(context.Request.Method))
    {
        context.Request.EnableBuffering();
        if (context.Request.HasJsonContentType() && context.Request.ContentLength is > 0 and <= 4096)
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var bodyText = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    using var doc = JsonDocument.Parse(bodyText);
                    if (doc.RootElement.TryGetProperty("email", out var emailProp))
                    {
                        var email = emailProp.GetString()?.Trim().ToLowerInvariant();
                        if (!string.IsNullOrEmpty(email))
                        {
                            context.Items["AuthAccount"] = email;
                        }
                    }
                }
            }
            catch
            {
                context.Request.Body.Position = 0;
            }
        }
    }

    await next();
});

app.UseRateLimiter();

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

var skipSeeding = builder.Configuration.GetValue<bool>("SkipStartupDbSeeding");
if (!skipSeeding)
{
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
}

app.Run();

// Required for WebApplicationFactory in tests
public partial class Program;
