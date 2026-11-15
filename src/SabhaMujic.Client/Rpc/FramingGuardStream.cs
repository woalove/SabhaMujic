using System.Globalization;
using System.Text;

namespace SabhaMujic.Client.Rpc;

/// <summary>
/// Read-side guard for header-delimited JSON-RPC (<c>Content-Length: n\r\n…\r\n\r\n</c> + body). It watches the bytes as
/// they are read and throws <see cref="InvalidDataException"/> as soon as a frame header is too long, lacks a valid
/// Content-Length, or announces a body larger than the limit, so a peer cannot make the other side buffer unbounded data.
/// Writes pass straight through to the inner stream.
/// </summary>
public sealed class FramingGuardStream : Stream
{
    public const int MaxHeaderBytes = 1024;

    private readonly Stream _inner;
    private readonly int _maxBodyBytes;
    private readonly byte[] _header = new byte[MaxHeaderBytes];
    private int _headerLength;
    private long _bodyRemaining;

    public FramingGuardStream(Stream inner, int maxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBodyBytes, 1);
        _inner = inner;
        _maxBodyBytes = maxBodyBytes;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => _inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Inspect(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => _inner.DisposeAsync();

    private void Inspect(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_bodyRemaining > 0)
            {
                var take = (int)Math.Min(_bodyRemaining, data.Length);
                _bodyRemaining -= take;
                data = data[take..];
                continue;
            }

            if (_headerLength == MaxHeaderBytes)
            {
                throw new InvalidDataException("JSON-RPC frame header is too long.");
            }

            _header[_headerLength++] = data[0];
            data = data[1..];
            if (_headerLength >= 4 && _header.AsSpan(_headerLength - 4, 4).SequenceEqual("\r\n\r\n"u8))
            {
                _bodyRemaining = ParseContentLength();
                _headerLength = 0;
            }
        }
    }

    private long ParseContentLength()
    {
        var text = Encoding.ASCII.GetString(_header, 0, _headerLength);
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (!long.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ||
                    length < 1 || length > _maxBodyBytes)
                {
                    throw new InvalidDataException("JSON-RPC frame has an invalid or too large Content-Length.");
                }

                return length;
            }
        }

        throw new InvalidDataException("JSON-RPC frame has no Content-Length header.");
    }
}
