namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// A readable stream that cannot seek and has no length: <c>content</c>'s bytes, or endless zeros when it
/// is null, as a device such as <c>/dev/zero</c> gives. It counts the bytes it has given.
/// </summary>
public sealed class UnseekableStream(byte[]? content) : Stream
{
    /// <summary>The bytes the stream has given so far.</summary>
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("The stream has no length.");

    public override long Position
    {
        get => throw new NotSupportedException("The stream cannot seek.");
        set => throw new NotSupportedException("The stream cannot seek.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var left = content is null ? long.MaxValue : content.Length - BytesRead;
        var given = left < count ? (int)left : count;
        if (content is null)
        {
            buffer.AsSpan(offset, given).Clear();
        }
        else
        {
            content.AsSpan((int)BytesRead, given).CopyTo(buffer.AsSpan(offset));
        }

        BytesRead += given;
        return given;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("The stream cannot seek.");

    public override void SetLength(long value) => throw new NotSupportedException("The stream cannot seek.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The stream is read-only.");
}
