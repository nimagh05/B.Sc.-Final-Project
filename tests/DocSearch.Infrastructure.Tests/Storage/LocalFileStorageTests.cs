using System.Text;
using DocSearch.Application.Exceptions;
using DocSearch.Application.Options;
using DocSearch.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Storage;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _tempTestDir;
    private readonly TestTimeProvider _timeProvider;
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _tempTestDir = Path.Combine(Path.GetTempPath(), "DocSearch_Storage_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempTestDir);

        _timeProvider = new TestTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
        var options = Microsoft.Extensions.Options.Options.Create(new StorageOptions
        {
            RootPath = _tempTestDir,
            MaxUploadMb = 50
        });

        _storage = new LocalFileStorage(options, NullLogger<LocalFileStorage>.Instance, _timeProvider);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempTestDir))
        {
            try
            {
                Directory.Delete(_tempTestDir, recursive: true);
            }
            catch
            {
                // Best-effort directory cleanup
            }
        }
    }

    [Fact]
    public async Task SaveAsync_StoresFile_InDatePartitionedFolder_AndReturnsRelativePath()
    {
        var sampleBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nHello World");
        using var stream = new MemoryStream(sampleBytes);

        var storagePath = await _storage.SaveAsync(stream, "sample.pdf", CancellationToken.None);

        Assert.StartsWith("2026/10/", storagePath);
        Assert.EndsWith(".pdf", storagePath);

        // Verify file exists on disk
        var fullDiskPath = Path.Combine(_tempTestDir, storagePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullDiskPath));
        Assert.Equal(sampleBytes, await File.ReadAllBytesAsync(fullDiskPath));
    }

    [Fact]
    public async Task SaveAsync_UsesAtomicMove_TempFileRemovedOnCompletion()
    {
        var sampleBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nAtomic Move Test");
        using var stream = new MemoryStream(sampleBytes);

        var storagePath = await _storage.SaveAsync(stream, "doc.pdf", CancellationToken.None);

        var monthDir = Path.Combine(_tempTestDir, "2026", "10");
        var tmpFiles = Directory.GetFiles(monthDir, "*.tmp");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task OpenReadAsync_ReturnsValidStream_ForExistingFile()
    {
        var sampleBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nReadable Content");
        using var stream = new MemoryStream(sampleBytes);

        var storagePath = await _storage.SaveAsync(stream, "doc.pdf", CancellationToken.None);

        await using var readStream = await _storage.OpenReadAsync(storagePath, CancellationToken.None);
        using var ms = new MemoryStream();
        await readStream.CopyToAsync(ms);

        Assert.Equal(sampleBytes, ms.ToArray());
    }

    [Fact]
    public async Task OpenReadAsync_ThrowsNotFoundException_WhenFileDoesNotExist()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _storage.OpenReadAsync("2026/10/non-existent.pdf", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile_AndIsIdempotent()
    {
        var sampleBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nFile to delete");
        using var stream = new MemoryStream(sampleBytes);

        var storagePath = await _storage.SaveAsync(stream, "delete-me.pdf", CancellationToken.None);
        var fullDiskPath = Path.Combine(_tempTestDir, storagePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullDiskPath));

        await _storage.DeleteAsync(storagePath, CancellationToken.None);
        Assert.False(File.Exists(fullDiskPath));

        // Idempotent: second call should not throw
        await _storage.DeleteAsync(storagePath, CancellationToken.None);
    }

    [Theory]
    [InlineData("../../../evil.txt")]
    [InlineData("..\\..\\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    public async Task PathTraversal_Attempt_ThrowsArgumentException(string maliciousPath)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.OpenReadAsync(maliciousPath, CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _storage.DeleteAsync(maliciousPath, CancellationToken.None));
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
