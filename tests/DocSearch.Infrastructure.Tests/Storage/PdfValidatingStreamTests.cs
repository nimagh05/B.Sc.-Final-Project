using System.Security.Cryptography;
using System.Text;
using DocSearch.Application.Exceptions;
using DocSearch.Infrastructure.Storage;
using Xunit;

namespace DocSearch.Infrastructure.Tests.Storage;

public sealed class PdfValidatingStreamTests
{
    private static readonly byte[] _validPdfBytes = Encoding.UTF8.GetBytes("%PDF-1.7\nSample PDF Content for Test");

    [Fact]
    public async Task ValidPdf_ComputesSha256_AndCountsBytesCorrectly()
    {
        using var source = new MemoryStream(_validPdfBytes);
        using var validator = new PdfValidatingStream(source, maxBytes: 1024 * 1024);
        using var destination = new MemoryStream();

        await validator.CopyToAsync(destination);

        var expectedHash = Convert.ToHexString(SHA256.HashData(_validPdfBytes)).ToLowerInvariant();

        Assert.Equal(_validPdfBytes.Length, validator.TotalBytesRead);
        Assert.Equal(expectedHash, validator.ContentHash);
        Assert.Equal(_validPdfBytes, destination.ToArray());
    }

    [Fact]
    public async Task InvalidMagicBytes_ThrowsInvalidFileException()
    {
        var invalidBytes = Encoding.UTF8.GetBytes("NOT A PDF FILE AT ALL");
        using var source = new MemoryStream(invalidBytes);
        using var validator = new PdfValidatingStream(source, maxBytes: 1024 * 1024);
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidFileException>(() => validator.CopyToAsync(destination));
        Assert.Equal("document.invalid_file", ex.ErrorCode);
    }

    [Fact]
    public async Task EmptyStream_ThrowsInvalidFileException()
    {
        using var source = new MemoryStream([]);
        using var validator = new PdfValidatingStream(source, maxBytes: 1024 * 1024);
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidFileException>(() => validator.CopyToAsync(destination));
        Assert.Equal("document.invalid_file", ex.ErrorCode);
    }

    [Fact]
    public async Task HeaderTooShort_ThrowsInvalidFileException()
    {
        var shortBytes = Encoding.UTF8.GetBytes("%PD");
        using var source = new MemoryStream(shortBytes);
        using var validator = new PdfValidatingStream(source, maxBytes: 1024 * 1024);
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidFileException>(() => validator.CopyToAsync(destination));
        Assert.Equal("document.invalid_file", ex.ErrorCode);
    }

    [Fact]
    public async Task ExceedingMaxBytes_ThrowsFileTooLargeException()
    {
        // 10 bytes limit, while content is longer
        using var source = new MemoryStream(_validPdfBytes);
        using var validator = new PdfValidatingStream(source, maxBytes: 10);
        using var destination = new MemoryStream();

        var ex = await Assert.ThrowsAsync<FileTooLargeException>(() => validator.CopyToAsync(destination));
        Assert.Equal("document.too_large", ex.ErrorCode);
        Assert.Equal(413, ex.StatusCode);
    }

    [Fact]
    public void ContentHash_BeforeStreamEnd_ThrowsInvalidOperationException()
    {
        using var source = new MemoryStream(_validPdfBytes);
        using var validator = new PdfValidatingStream(source, maxBytes: 1024 * 1024);

        Assert.Throws<InvalidOperationException>(() => validator.ContentHash);
    }
}
