using System.Text.RegularExpressions;
using DocSearch.Application.Exceptions;

namespace DocSearch.Infrastructure.Common;

internal static partial class ErrorMessageSanitizer
{
    [GeneratedRegex(@"([a-zA-Z]:\\[^ \r\n\t]+|/[^ \r\n\t]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FilePathRegex();

    [GeneratedRegex(@"(?i)stack trace", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StackTraceRegex();

    public static string Sanitize(Exception exception, string operationDescription = "document processing")
    {
        ArgumentNullException.ThrowIfNull(exception);

        string rawMessage;
        if (exception is DocSearchException docEx)
        {
            rawMessage = docEx.Message;
        }
        else if (exception is TimeoutException or TaskCanceledException or OperationCanceledException)
        {
            rawMessage = $"The operation timed out during {operationDescription}.";
        }
        else
        {
            rawMessage = $"An error occurred during {operationDescription} ({exception.GetType().Name}).";
        }

        var sanitized = FilePathRegex().Replace(rawMessage, "[path]");
        sanitized = StackTraceRegex().Replace(sanitized, string.Empty);

        if (sanitized.Length > 500)
        {
            sanitized = sanitized[..500];
        }

        return sanitized.Trim();
    }
}
