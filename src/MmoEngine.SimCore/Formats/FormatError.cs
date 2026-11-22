using System;
using System.Globalization;

namespace MmoEngine.SimCore.Formats;

/// <summary>
/// The detail templates of a <see cref="FormatError"/>. An error stores its template and up to three
/// <see cref="ulong"/> arguments and formats the text only when <see cref="FormatError.Detail"/> is
/// read, so recording a refusal on the tick path allocates nothing
/// (specs/015-shared-simulation-core/contracts/collision-data-format.md#api).
/// </summary>
internal enum FormatDetail : byte
{
    /// <summary>No template: the detail is the preformatted text, or empty.</summary>
    None = 0,

    /// <summary><c>{a} bytes expected, got {b}</c>.</summary>
    ExpectedLength,

    /// <summary><c>format identifier 0x{a:X2} is not 0x{b:X2}</c>.</summary>
    UnknownFormatId,

    /// <summary><c>unsupported version {a}; this reader supports version {b}</c>.</summary>
    UnsupportedVersion,

    /// <summary><c>the file declares {a} bytes, {b} are present</c>.</summary>
    DeclaredLength,

    /// <summary><c>the counts need {a} bytes; fileLength is {b}</c>.</summary>
    CountsNeed,

    /// <summary><c>the trailer is {a:x16}, the content hashes to {b:x16}</c>.</summary>
    ChecksumMismatch,

    /// <summary><c>{a as float bits} is not finite</c>, for example <c>NaN is not finite</c>.</summary>
    NotFinite,

    /// <summary><c>{a as float bits} is outside [{b as float bits}, {c as float bits}]</c>.</summary>
    OutOfRangeFloat,

    /// <summary><c>field 0x{a:X4} is not above 0x{b:X4}</c>.</summary>
    NotAbove,

    /// <summary><c>field 0x{a:X4} is missing</c>.</summary>
    Missing,

    /// <summary><c>field 0x{a:X4} is not in the rule table</c>.</summary>
    UnknownField,
}

/// <summary>
/// Why a decoder refused its input: <c>{ Format, Code, Offset, Field, Detail }</c>, read as
/// <c>"&lt;format&gt; &lt;field&gt; at offset &lt;n&gt;: &lt;detail&gt;"</c>
/// (specs/015-shared-simulation-core/contracts/collision-data-format.md#api and
/// contracts/input-command-format.md#decoding). <c>default(FormatError)</c> has the code
/// <see cref="FormatErrorCode.None"/>. Two errors with the same fields are equal.
/// </summary>
public readonly struct FormatError : IEquatable<FormatError>
{
    private readonly string? _format;
    private readonly string? _field;
    private readonly string? _text;
    private readonly FormatDetail _template;
    private readonly ulong _a;
    private readonly ulong _b;
    private readonly ulong _c;

    /// <summary>
    /// An error whose detail is formatted from <paramref name="detail"/> and its arguments when it is
    /// read. Creating one allocates nothing, so the tick path may record it.
    /// </summary>
    internal FormatError(string format, FormatErrorCode code, long offset, string field, FormatDetail detail, ulong a = 0, ulong b = 0, ulong c = 0)
    {
        _format = format;
        Code = code;
        Offset = offset;
        _field = field;
        _template = detail;
        _a = a;
        _b = b;
        _c = c;
    }

    /// <summary>An error with a preformatted detail, for decoders off the tick path.</summary>
    public FormatError(string format, FormatErrorCode code, long offset, string field, string detail)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(detail);
        _format = format;
        Code = code;
        Offset = offset;
        _field = field;
        _text = detail;
    }

    /// <summary>The format that refused its input, such as <c>input</c>, <c>collision</c>, <c>state</c> or <c>stream</c>.</summary>
    public string Format => _format ?? string.Empty;

    /// <summary>Why the input was refused.</summary>
    public FormatErrorCode Code { get; }

    /// <summary>The byte offset the error was found at.</summary>
    public long Offset { get; }

    /// <summary>The field that was refused, such as <c>version</c> or <c>steps[3].commands[0].input.version</c>.</summary>
    public string Field => _field ?? string.Empty;

    /// <summary>What was wrong, formatted when read.</summary>
    public string Detail => _template switch
    {
        FormatDetail.ExpectedLength => string.Create(CultureInfo.InvariantCulture, $"{_a} bytes expected, got {_b}"),
        FormatDetail.UnknownFormatId => string.Create(CultureInfo.InvariantCulture, $"format identifier 0x{_a:X2} is not 0x{_b:X2}"),
        FormatDetail.UnsupportedVersion => string.Create(CultureInfo.InvariantCulture, $"unsupported version {_a}; this reader supports version {_b}"),
        FormatDetail.DeclaredLength => string.Create(CultureInfo.InvariantCulture, $"the file declares {_a} bytes, {_b} are present"),
        FormatDetail.CountsNeed => string.Create(CultureInfo.InvariantCulture, $"the counts need {_a} bytes; fileLength is {_b}"),
        FormatDetail.ChecksumMismatch => string.Create(CultureInfo.InvariantCulture, $"the trailer is {_a:x16}, the content hashes to {_b:x16}"),
        FormatDetail.NotFinite => string.Create(CultureInfo.InvariantCulture, $"{FloatFromBits(_a)} is not finite"),
        FormatDetail.OutOfRangeFloat => string.Create(CultureInfo.InvariantCulture, $"{FloatFromBits(_a)} is outside [{FloatFromBits(_b)}, {FloatFromBits(_c)}]"),
        FormatDetail.NotAbove => string.Create(CultureInfo.InvariantCulture, $"field 0x{_a:X4} is not above 0x{_b:X4}"),
        FormatDetail.Missing => string.Create(CultureInfo.InvariantCulture, $"field 0x{_a:X4} is missing"),
        FormatDetail.UnknownField => string.Create(CultureInfo.InvariantCulture, $"field 0x{_a:X4} is not in the rule table"),
        _ => _text ?? string.Empty,
    };

    /// <summary>The message: <c>"&lt;format&gt; &lt;field&gt; at offset &lt;n&gt;: &lt;detail&gt;"</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Format} {Field} at offset {Offset}: {Detail}");

    /// <summary>
    /// True when the two errors have the same format, code, offset, field and detail. Errors built from
    /// the same template and arguments compare without formatting their details.
    /// </summary>
    public bool Equals(FormatError other) =>
        Code == other.Code
        && Offset == other.Offset
        && string.Equals(Format, other.Format, StringComparison.Ordinal)
        && string.Equals(Field, other.Field, StringComparison.Ordinal)
        && (SameDetailSource(other) || string.Equals(Detail, other.Detail, StringComparison.Ordinal));

    public override bool Equals(object? obj) => obj is FormatError other && Equals(other);

    /// <summary>
    /// A hash of the code and the offset only, which equal errors share. It reads no string, so it does
    /// not depend on the per-process string hash seed.
    /// </summary>
    public override int GetHashCode() => (((int)Code) * 397) ^ Offset.GetHashCode();

    private bool SameDetailSource(FormatError other) =>
        _template == other._template
        && _a == other._a
        && _b == other._b
        && _c == other._c
        && string.Equals(_text, other._text, StringComparison.Ordinal);

    private static float FloatFromBits(ulong bits) => BitConverter.UInt32BitsToSingle((uint)bits);
}
