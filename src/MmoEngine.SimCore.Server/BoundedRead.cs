namespace MmoEngine.SimCore.Server;

/// <summary>
/// Reads a file whose length a check made before the read cannot be trusted: a pipe or a device has no
/// length, and a regular file can grow between the check and the read. <c>config check</c>,
/// <see cref="SimConfigJson.Load"/> and <c>measure validate-report</c> read through it, so none of them
/// reads more than its limit + 1 bytes, whatever the path names
/// (specs/015-shared-simulation-core/contracts/simcore-cli.md#config-check).
/// </summary>
public static class BoundedRead
{
    /// <summary>
    /// Every byte of <paramref name="stream"/> when it holds at most <paramref name="limit"/>, else null. A
    /// stream with a length over the limit is refused from it, before any byte is read; one without a length
    /// (a pipe, or a device such as <c>/dev/zero</c>) is read to its end or until it passes the limit, so no
    /// more than <paramref name="limit"/> + 1 bytes are ever read.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is negative or <see cref="int.MaxValue"/>.</exception>
    public static byte[]? ReadAtMost(Stream stream, int limit)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfEqual(limit, int.MaxValue);
        if (stream.CanSeek && stream.Length - stream.Position > limit)
        {
            return null;
        }

        var buffer = new byte[limit + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return count > limit ? null : buffer[..count];
    }
}
