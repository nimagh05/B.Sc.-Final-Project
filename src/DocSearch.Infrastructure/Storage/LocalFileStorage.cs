using System.Globalization;
using DocSearch.Application.Abstractions;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocSearch.Infrastructure.Storage;

public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _rootPath;
    private readonly string _normalizedRootPath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalFileStorage> _logger;

    public LocalFileStorage(
        IOptions<StorageOptions> options,
        ILogger<LocalFileStorage> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;

        var configuredRoot = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            configuredRoot = "./data/files";
        }

        _rootPath = Path.GetFullPath(configuredRoot);
        _normalizedRootPath = NormalizeDirectoryPath(_rootPath);

        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }
    }

    public async Task<string> SaveAsync(Stream content, string suggestedFileName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var utcNow = _timeProvider.GetUtcNow();
        var year = utcNow.ToString("yyyy", CultureInfo.InvariantCulture);
        var month = utcNow.ToString("MM", CultureInfo.InvariantCulture);

        var relativeDir = Path.Combine(year, month);
        var targetDir = Path.Combine(_rootPath, relativeDir);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        var fileId = Guid.CreateVersion7();
        var fileName = $"{fileId:N}.pdf";
        var finalFullPath = ResolveAndValidatePath(Path.Combine(relativeDir, fileName));

        var tempFileName = $"{Guid.NewGuid():N}.tmp";
        var tempFullPath = Path.Combine(targetDir, tempFileName);

        try
        {
            await using (var fileStream = new FileStream(
                tempFullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                await content.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                await fileStream.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(tempFullPath, finalFullPath, overwrite: false);
            _logger.LogInformation("Stored file atomically at {RelativePath}", Path.Combine(relativeDir, fileName));

            // Return relative path with forward slashes for cross-platform consistency
            return $"{year}/{month}/{fileName}";
        }
        catch
        {
            if (File.Exists(tempFullPath))
            {
                try
                {
                    File.Delete(tempFullPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up temporary file {TempPath}", tempFullPath);
                }
            }

            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var fullPath = ResolveAndValidatePath(storagePath);

        if (!File.Exists(fullPath))
        {
            throw new NotFoundException($"Stored file '{storagePath}' was not found.");
        }

        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        return Task.FromResult<Stream>(stream);
    }

    public Task DeleteAsync(string storagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        var fullPath = ResolveAndValidatePath(storagePath);

        if (File.Exists(fullPath))
        {
            try
            {
                File.Delete(fullPath);
                _logger.LogInformation("Deleted stored file at {StoragePath}", storagePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete stored file at {StoragePath}", storagePath);
                throw;
            }
        }

        return Task.CompletedTask;
    }

    private string ResolveAndValidatePath(string relativePath)
    {
        // Guard against absolute paths or root traversal
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("Storage path cannot be rooted or absolute.", nameof(relativePath));
        }

        var combined = Path.Combine(_rootPath, relativePath);
        var fullPath = Path.GetFullPath(combined);

        if (!fullPath.StartsWith(_normalizedRootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Path traversal attempt detected.", nameof(relativePath));
        }

        return fullPath;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!normalized.EndsWith(Path.DirectorySeparatorChar) && !normalized.EndsWith(Path.AltDirectorySeparatorChar))
        {
            normalized += Path.DirectorySeparatorChar;
        }

        return normalized;
    }
}
