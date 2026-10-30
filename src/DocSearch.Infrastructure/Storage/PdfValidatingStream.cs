using System.Security.Cryptography;
using DocSearch.Application.Exceptions;

namespace DocSearch.Infrastructure.Storage;

public sealed class PdfValidatingStream : Stream
{
    private static readonly byte[] _pdfMagicBytes = [0x25, 0x50, 0x44, 0x46, 0x2D]; // %PDF-
    private readonly Stream _innerStream;
    private readonly long _maxBytes;
    private readonly IncrementalHash _hasher;

    private readonly byte[] _headerBuffer = new byte[5];
    private int _headerBytesRead;
    private int _headerBytesServed;
    private bool _magicChecked;
    private long _totalBytesRead;
    private string? _contentHash;
    private bool _disposed;

    public PdfValidatingStream(Stream innerStream, long maxBytes)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Maximum bytes limit must be greater than zero.");
        }

        _maxBytes = maxBytes;
        _hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    public long TotalBytesRead => _totalBytesRead;

    public string ContentHash => _contentHash ?? throw new InvalidOperationException(
        "Content hash is not available until the entire stream has been read to completion.");

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _innerStream.Length;
    public override long Position
    {
        get => _totalBytesRead;
        set => throw new NotSupportedException("Seeking is not supported on a validating stream.");
    }

    public override void Flush() => _innerStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("Seeking is not supported on a validating stream.");

    public override void SetLength(long value) =>
        throw new NotSupportedException("Setting length is not supported on a validating stream.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Writing is not supported on a validating stream.");

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        EnsureNotDisposed();

        if (buffer.Length == 0)
        {
            return 0;
        }

        EnsureMagicCheckedSync();

        var totalCopied = 0;

        // Drain any unserved header bytes first
        if (_headerBytesServed < _headerBytesRead)
        {
            var headerRemaining = _headerBytesRead - _headerBytesServed;
            var toCopy = Math.Min(buffer.Length, headerRemaining);
            _headerBuffer.AsSpan(_headerBytesServed, toCopy).CopyTo(buffer);
            _headerBytesServed += toCopy;
            totalCopied += toCopy;
            buffer = buffer[toCopy..];

            if (buffer.Length == 0)
            {
                return totalCopied;
            }
        }

        var bytesRead = _innerStream.Read(buffer);
        if (bytesRead > 0)
        {
            _totalBytesRead += bytesRead;
            if (_totalBytesRead > _maxBytes)
            {
                throw new FileTooLargeException(
                    $"The uploaded file exceeds the allowed size limit of {_maxBytes / (1024 * 1024)} MB.");
            }

            _hasher.AppendData(buffer[..bytesRead]);
            totalCopied += bytesRead;
        }
        else if (_totalBytesRead == 0)
        {
            throw new InvalidFileException("The uploaded file is empty.");
        }
        else if (_contentHash == null)
        {
            _contentHash = Convert.ToHexString(_hasher.GetHashAndReset()).ToLowerInvariant();
        }

        return totalCopied;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();

        if (buffer.Length == 0)
        {
            return 0;
        }

        await EnsureMagicCheckedAsync(cancellationToken).ConfigureAwait(false);

        var totalCopied = 0;

        // Drain any unserved header bytes first
        if (_headerBytesServed < _headerBytesRead)
        {
            var headerRemaining = _headerBytesRead - _headerBytesServed;
            var toCopy = Math.Min(buffer.Length, headerRemaining);
            _headerBuffer.AsMemory(_headerBytesServed, toCopy).CopyTo(buffer);
            _headerBytesServed += toCopy;
            totalCopied += toCopy;
            buffer = buffer[toCopy..];

            if (buffer.Length == 0)
            {
                return totalCopied;
            }
        }

        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (bytesRead > 0)
        {
            _totalBytesRead += bytesRead;
            if (_totalBytesRead > _maxBytes)
            {
                throw new FileTooLargeException(
                    $"The uploaded file exceeds the allowed size limit of {_maxBytes / (1024 * 1024)} MB.");
            }

            _hasher.AppendData(buffer.Span[..bytesRead]);
            totalCopied += bytesRead;
        }
        else if (_totalBytesRead == 0)
        {
            throw new InvalidFileException("The uploaded file is empty.");
        }
        else if (_contentHash == null)
        {
            _contentHash = Convert.ToHexString(_hasher.GetHashAndReset()).ToLowerInvariant();
        }

        return totalCopied;
    }

    private void EnsureMagicCheckedSync()
    {
        if (_magicChecked)
        {
            return;
        }

        while (_headerBytesRead < _pdfMagicBytes.Length)
        {
            var read = _innerStream.Read(_headerBuffer.AsSpan(_headerBytesRead, _pdfMagicBytes.Length - _headerBytesRead));
            if (read == 0)
            {
                break;
            }

            _headerBytesRead += read;
        }

        ValidateHeader();
    }

    private async ValueTask EnsureMagicCheckedAsync(CancellationToken ct)
    {
        if (_magicChecked)
        {
            return;
        }

        while (_headerBytesRead < _pdfMagicBytes.Length)
        {
            var read = await _innerStream.ReadAsync(
                _headerBuffer.AsMemory(_headerBytesRead, _pdfMagicBytes.Length - _headerBytesRead),
                ct).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            _headerBytesRead += read;
        }

        ValidateHeader();
    }

    private void ValidateHeader()
    {
        if (_headerBytesRead == 0)
        {
            throw new InvalidFileException("The uploaded file is empty.");
        }

        if (_headerBytesRead < _pdfMagicBytes.Length)
        {
            throw new InvalidFileException("Only PDF files are accepted. File header is too short.");
        }

        if (!_headerBuffer.AsSpan(0, _pdfMagicBytes.Length).SequenceEqual(_pdfMagicBytes))
        {
            throw new InvalidFileException("Only PDF files are accepted. Invalid magic bytes.");
        }

        _magicChecked = true;
        _totalBytesRead += _headerBytesRead;

        if (_totalBytesRead > _maxBytes)
        {
            throw new FileTooLargeException(
                $"The uploaded file exceeds the allowed size limit of {_maxBytes / (1024 * 1024)} MB.");
        }

        _hasher.AppendData(_headerBuffer.AsSpan(0, _headerBytesRead));
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _hasher.Dispose();
                _innerStream.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }
}
