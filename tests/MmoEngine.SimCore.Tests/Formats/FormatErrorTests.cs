using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// A format error carries <c>{ Format, Code, Offset, Field, Detail }</c> and reads as
/// <c>"&lt;format&gt; &lt;field&gt; at offset &lt;n&gt;: &lt;detail&gt;"</c>
/// (contracts/input-command-format.md#decoding, contracts/collision-data-format.md#api). The detail is
/// formatted from a stored <c>FormatDetail</c> template and its numbers when it is read, so recording
/// a refusal on the tick path allocates nothing. <see cref="CoreFormatException"/> carries the error
/// and uses its message, and the default error has the code <see cref="FormatErrorCode.None"/>.
/// </summary>
public sealed class FormatErrorTests
{
    /// <summary>
    /// The two messages of contracts/input-command-format.md#decoding: the expected-length detail of a
    /// command one byte short (17 and 16), and the version detail of a command of another version
    /// (2 and 1).
    /// </summary>
    [Theory]
    [InlineData("expected length 17, got 16", "input", FormatErrorCode.Truncated, 16L, "length", "17 bytes expected, got 16", "input length at offset 16: 17 bytes expected, got 16")]
    [InlineData("unsupported version 2, supported 1", "input", FormatErrorCode.UnsupportedVersion, 1L, "version", "unsupported version 2; this reader supports version 1", "input version at offset 1: unsupported version 2; this reader supports version 1")]
    public void MessageHasTheContractShape(
        string detailCase,
        string format,
        FormatErrorCode code,
        long offset,
        string field,
        string expectedDetail,
        string expectedMessage)
    {
        var error = detailCase switch
        {
            "expected length 17, got 16" => new FormatError(format, code, offset, field, FormatDetail.ExpectedLength, 17, 16),
            "unsupported version 2, supported 1" => new FormatError(format, code, offset, field, FormatDetail.UnsupportedVersion, 2, 1),
            _ => throw new ArgumentException($"Unknown detail case {detailCase}.", nameof(detailCase)),
        };

        Assert.Equal(format, error.Format);
        Assert.Equal(code, error.Code);
        Assert.Equal(offset, error.Offset);
        Assert.Equal(field, error.Field);
        Assert.Equal(expectedDetail, error.Detail);
        Assert.Equal(expectedMessage, error.ToString());

        var exception = new CoreFormatException(error);
        Assert.Equal(error.ToString(), exception.Message);
        Assert.Equal(error, exception.Error);

        Assert.Equal(FormatErrorCode.None, default(FormatError).Code);
    }
}
