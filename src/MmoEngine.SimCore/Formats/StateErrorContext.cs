using System;

namespace MmoEngine.SimCore.Formats;

/// <summary>
/// Where a canonical state being loaded sits, for its refusals
/// (specs/015-shared-simulation-core/contracts/state-serialization.md#loading-a-state): the format the
/// error names, the offset of the state's first byte in the input, and the prefix of every field path.
/// A standalone state is <see cref="State"/> (format <c>state</c>, base 0, no prefix); a stream's
/// initial state is <c>stream</c>, base <c>64 + configLength</c>, prefix <c>initialState.</c>.
/// </summary>
public readonly struct StateErrorContext
{
    private const string StateFormat = "state";

    private readonly string? _format;
    private readonly string? _fieldPrefix;

    /// <exception cref="ArgumentNullException">The format or the prefix is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The base offset is negative.</exception>
    public StateErrorContext(string format, long baseOffset, string fieldPrefix)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(fieldPrefix);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        _format = format;
        BaseOffset = baseOffset;
        _fieldPrefix = fieldPrefix;
    }

    /// <summary>A standalone state: format <c>state</c>, offsets from the state's first byte, no prefix.</summary>
    public static StateErrorContext State { get; } = new(StateFormat, 0, string.Empty);

    /// <summary>The format the error names: <c>state</c>, or <c>stream</c> inside a recorded stream.</summary>
    public string Format => _format ?? StateFormat;

    /// <summary>The offset of the state's first byte in the input the error's offset counts from.</summary>
    public long BaseOffset { get; }

    /// <summary>The prefix of every field path, such as <c>initialState.</c>; empty for a standalone state.</summary>
    public string FieldPrefix => _fieldPrefix ?? string.Empty;

    /// <summary>
    /// A recorded stream's initial state: format <c>stream</c>, the state's file offset
    /// <paramref name="baseOffset"/> (<c>64 + configLength</c>) and the prefix <c>initialState.</c>
    /// (contracts/recorded-stream-format.md#initial-state).
    /// </summary>
    internal static StateErrorContext StreamInitialState(long baseOffset) => new("stream", baseOffset, "initialState.");
}
