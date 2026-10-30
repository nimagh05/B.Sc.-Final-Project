using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DocSearch.Application.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace DocSearch.Api.Infrastructure;

public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private static readonly JsonSerializerOptions _problemJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        int statusCode;
        string errorCode;
        string title;
        string detail;
        IReadOnlyDictionary<string, string[]>? errors = null;

        switch (exception)
        {
            case ValidationException validationException:
                statusCode = validationException.StatusCode;
                errorCode = validationException.ErrorCode;
                title = "Validation failed";
                detail = validationException.Message;
                errors = validationException.Errors;
                _logger.LogWarning(exception, "Validation error occurred: {Detail}", detail);
                break;

            case NotFoundException notFoundException:
                statusCode = notFoundException.StatusCode;
                errorCode = notFoundException.ErrorCode;
                title = "Resource not found";
                detail = notFoundException.Message;
                _logger.LogInformation("Resource not found: {Detail}", detail);
                break;

            case ForbiddenException forbiddenException:
                statusCode = forbiddenException.StatusCode;
                errorCode = forbiddenException.ErrorCode;
                title = "Forbidden";
                detail = forbiddenException.Message;
                _logger.LogWarning("Forbidden access attempt: {Detail}", detail);
                break;

            case ConflictException conflictException:
                statusCode = conflictException.StatusCode;
                errorCode = conflictException.ErrorCode;
                title = "Conflict";
                detail = conflictException.Message;
                _logger.LogWarning("Conflict occurred: {Detail}", detail);
                break;

            case FileTooLargeException fileTooLargeException:
                statusCode = fileTooLargeException.StatusCode;
                errorCode = fileTooLargeException.ErrorCode;
                title = "Payload too large";
                detail = fileTooLargeException.Message;
                _logger.LogWarning("File too large: {Detail}", detail);
                break;

            case InvalidFileException invalidFileException:
                statusCode = invalidFileException.StatusCode;
                errorCode = invalidFileException.ErrorCode;
                title = "Invalid file";
                detail = invalidFileException.Message;
                _logger.LogWarning("Invalid file uploaded: {Detail}", detail);
                break;

            case BadHttpRequestException badHttpEx when badHttpEx.StatusCode == StatusCodes.Status413PayloadTooLarge:
                statusCode = StatusCodes.Status413PayloadTooLarge;
                errorCode = "document.too_large";
                title = "Payload too large";
                detail = "The uploaded file exceeds the allowed size limit.";
                _logger.LogWarning("Request body too large: {Detail}", detail);
                break;

            case InvalidDataException invalidDataEx when invalidDataEx.Message.Contains("Multipart body length limit", StringComparison.OrdinalIgnoreCase):
                statusCode = StatusCodes.Status413PayloadTooLarge;
                errorCode = "document.too_large";
                title = "Payload too large";
                detail = "The uploaded file exceeds the allowed size limit.";
                _logger.LogWarning("Multipart body length exceeded: {Detail}", detail);
                break;

            case RateLimitExceededException rateLimitException:
                statusCode = rateLimitException.StatusCode;
                errorCode = rateLimitException.ErrorCode;
                title = "Rate limit exceeded";
                detail = rateLimitException.Message;
                if (rateLimitException.RetryAfterSeconds.HasValue)
                {
                    httpContext.Response.Headers.RetryAfter = rateLimitException.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture);
                }
                _logger.LogWarning("Rate limit exceeded: {Detail}", detail);
                break;

            case DocSearchException docSearchException:
                statusCode = docSearchException.StatusCode;
                errorCode = docSearchException.ErrorCode;
                title = "Application error";
                detail = docSearchException.Message;
                _logger.LogWarning(exception, "Application exception: {Detail}", detail);
                break;

            default:
                statusCode = StatusCodes.Status500InternalServerError;
                errorCode = "server.error";
                title = "Internal Server Error";
                // In production and by default, never leak exception messages, stack traces, or internal server paths
                detail = "An unexpected error occurred. Please contact support or retry later.";
                _logger.LogError(exception, "Unhandled server error occurred.");
                break;
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        var problemDetails = new ProblemDetailsResponse
        {
            Type = $"https://docsearch.local/errors/{errorCode}",
            Title = title,
            Status = statusCode,
            Detail = detail,
            Code = errorCode,
            TraceId = traceId,
            Errors = errors
        };

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            httpContext.Response.Body,
            problemDetails,
            _problemJsonOptions,
            cancellationToken);

        return true;
    }
}

public sealed class ProblemDetailsResponse
{
    public required string Type { get; init; }
    public required string Title { get; init; }
    public required int Status { get; init; }
    public required string Detail { get; init; }
    public required string Code { get; init; }
    public required string TraceId { get; init; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
