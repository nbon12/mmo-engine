using System;
using System.Buffers.Binary;

namespace MmoEngine.SimCore.Batch;

/// <summary>
/// One step's record, <c>u32 step | u32 stateLength | state | u64 hash</c>, little-endian, 16 bytes plus
/// the state (specs/015-shared-simulation-core/contracts/state-serialization.md#step-record). Records are
/// packed back to back with no header and no padding, and a reader walks them by <c>stateLength</c>.
/// Neither method allocates.
/// </summary>
public static class StepRecord
{
    /// <summary>The record's bytes besides the state: <c>step</c>, <c>stateLength</c> and <c>hash</c>.</summary>
    public const int OverheadLength = 16;

    private const int StateOffset = 8;

    /// <summary>
    /// Writes the record of <paramref name="step"/>, <paramref name="state"/> and
    /// <paramref name="hash"/> at the start of <paramref name="destination"/>. Returns false and writes
    /// nothing when the destination is shorter than 16 + the state's length.
    /// </summary>
    public static bool Write(uint step, ReadOnlySpan<byte> state, ulong hash, Span<byte> destination, out int written)
    {
        var length = (long)OverheadLength + state.Length;
        if (destination.Length < length)
        {
            written = 0;
            return false;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, step);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)state.Length);
        state.CopyTo(destination[StateOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[(StateOffset + state.Length)..], hash);
        written = (int)length;
        return true;
    }

    /// <summary>
    /// Reads the record at the start of <paramref name="source"/>: its step, a view of its state, its hash
    /// and its whole length, which is where the next packed record starts. Returns false, with every out
    /// value at its default, when the source does not hold a whole record.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> source, out uint step, out ReadOnlySpan<byte> state, out ulong hash, out int length)
    {
        step = 0;
        state = default;
        hash = 0;
        length = 0;
        if (source.Length < OverheadLength)
        {
            return false;
        }

        var stateLength = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        var recordLength = OverheadLength + (long)stateLength;
        if (recordLength > source.Length)
        {
            return false;
        }

        step = BinaryPrimitives.ReadUInt32LittleEndian(source);
        state = source.Slice(StateOffset, (int)stateLength);
        hash = BinaryPrimitives.ReadUInt64LittleEndian(source[(StateOffset + (int)stateLength)..]);
        length = (int)recordLength;
        return true;
    }
}
