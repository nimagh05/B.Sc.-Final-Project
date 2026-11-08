namespace DocSearch.Application.Exceptions;

public abstract class DocSearchException : Exception
{
    protected DocSearchException(string message, string errorCode, int statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode;
    }

    public string ErrorCode { get; }
    public int StatusCode { get; }
}

public sealed class NotFoundException : DocSearchException
{
    public NotFoundException(string message = "The requested resource was not found or is not visible.")
        : base(message, "resource.not_found", 404)
    {
    }
}

public sealed class UnauthorizedException : DocSearchException
{
    public UnauthorizedException(string message = "Authentication is required to access this resource.", string errorCode = "auth.unauthorized")
        : base(message, errorCode, 401)
    {
    }
}

public sealed class ForbiddenException : DocSearchException
{
    public ForbiddenException(string message = "You do not have permission to access this resource.", string errorCode = "auth.forbidden")
        : base(message, errorCode, 403)
    {
    }
}

public sealed class ValidationException : DocSearchException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationException(string message, IReadOnlyDictionary<string, string[]> errors)
        : base(message, "validation.failed", 400)
    {
        Errors = errors;
    }

    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : this("One or more validation errors occurred.", errors)
    {
    }

    public ValidationException(string propertyName, string error)
        : this("One or more validation errors occurred.", new Dictionary<string, string[]> { [propertyName] = [error] })
    {
    }
}

public sealed class ConflictException : DocSearchException
{
    public ConflictException(string message, string errorCode = "document.duplicate")
        : base(message, errorCode, 409)
    {
    }
}

public sealed class InvalidFileException : DocSearchException
{
    public InvalidFileException(string message = "The provided file is invalid, corrupt, or not an acceptable PDF.")
        : base(message, "document.invalid_file", 400)
    {
    }
}

public sealed class FileTooLargeException : DocSearchException
{
    public FileTooLargeException(string message = "The uploaded file exceeds the allowed size limit.")
        : base(message, "document.too_large", 413)
    {
    }
}

public sealed class InvalidScopeException : DocSearchException
{
    public InvalidScopeException(string message = "The requested collection scope is invalid or empty.")
        : base(message, "rag.invalid_scope", 400)
    {
    }
}

public sealed class RateLimitExceededException : DocSearchException
{
    public int? RetryAfterSeconds { get; }

    public RateLimitExceededException(string message = "Rate limit exceeded. Please retry later.", int? retryAfterSeconds = null)
        : base(message, "rate.limited", 429)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}

public sealed class ExternalServiceUnavailableException : DocSearchException
{
    public string ServiceName { get; }

    public ExternalServiceUnavailableException(string serviceName, string message, Exception? inner = null)
        : base($"External service '{serviceName}' is unavailable: {message}", "server.error", 503, inner)
    {
        ServiceName = serviceName;
    }

    public ExternalServiceUnavailableException(string message, Exception? inner = null)
        : this("Embedding", message, inner)
    {
    }
}
