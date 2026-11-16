using System;
using System.Buffers.Binary;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Inputs;

/// <summary>
/// The 17-byte input command codec, format version 1
/// (specs/015-shared-simulation-core/contracts/input-command-format.md#layout):
/// <c>formatId</c> <c>0x49</c>, <c>version</c> 1, then <c>u32 sequence</c>, <c>u32 clientStep</c>,
/// <c>u16 buttons</c>, <c>u16 aimYaw</c>, <c>i16 aimPitch</c> and <c>u8 weaponSlot</c>, little-endian.
/// </summary>
/// <remarks>
/// Decoding checks only the frame, in the order of #decoding, and checks no value, so every 17-byte
/// buffer with the identifier and the version decodes and re-encodes to itself. Neither method throws
/// or allocates, on success or on refusal: the codec sits on the tick path, and a refusal's
/// <see cref="FormatError"/> holds string literals and formats its detail only when it is read.
/// </remarks>
public static class InputCodec
{
    /// <summary>The format identifier, ASCII <c>I</c>.</summary>
    public const byte FormatId = 0x49;

    /// <summary>The only version this codec reads and writes.</summary>
    public const byte FormatVersion = 1;

    /// <summary>The encoded length: 1 + 1 + 4 + 4 + 2 + 2 + 2 + 1 bytes.</summary>
    public const int EncodedSize = 17;

    private const string Format = "input";

    private const int SequenceOffset = 2;
    private const int ClientStepOffset = 6;
    private const int ButtonsOffset = 10;
    private const int AimYawOffset = 12;
    private const int AimPitchOffset = 14;
    private const int WeaponSlotOffset = 16;

    /// <summary>
    /// Writes the 17 bytes of <paramref name="command"/> into the start of
    /// <paramref name="destination"/>, the fields as given, without validating them, so commands the
    /// validator would reject can be encoded too. Returns false and writes nothing when the destination
    /// is shorter than 17 bytes.
    /// </summary>
    public static bool TryEncode(in InputCommand command, Span<byte> destination)
    {
        if (destination.Length < EncodedSize)
        {
            return false;
        }

        destination[0] = FormatId;
        destination[1] = FormatVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[SequenceOffset..], command.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[ClientStepOffset..], command.ClientStep);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[ButtonsOffset..], (ushort)command.Buttons);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[AimYawOffset..], command.AimYaw);
        BinaryPrimitives.WriteInt16LittleEndian(destination[AimPitchOffset..], command.AimPitch);
        destination[WeaponSlotOffset] = command.WeaponSlot;
        return true;
    }

    /// <summary>
    /// Decodes a command, running the six frame checks of contracts/input-command-format.md#decoding in
    /// order and stopping at the first failure. True with the command and a default error on success;
    /// false with a <c>default</c> command and the error (format <c>input</c>) on refusal.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> source, out InputCommand command, out FormatError error)
    {
        command = default;
        var length = (ulong)source.Length;

        // 1: no byte at all.
        if (source.Length == 0)
        {
            error = new FormatError(Format, FormatErrorCode.Truncated, 0, "formatId", FormatDetail.ExpectedLength, EncodedSize, length);
            return false;
        }

        // 2: the identifier is checked before anything else.
        if (source[0] != FormatId)
        {
            error = new FormatError(Format, FormatErrorCode.UnknownMagic, 0, "formatId", FormatDetail.UnknownFormatId, source[0], FormatId);
            return false;
        }

        // 3: the identifier alone.
        if (source.Length == 1)
        {
            error = new FormatError(Format, FormatErrorCode.Truncated, 1, "version", FormatDetail.ExpectedLength, EncodedSize, length);
            return false;
        }

        // 4: the version comes before the length, so another version is refused as such at any length.
        if (source[1] != FormatVersion)
        {
            error = new FormatError(Format, FormatErrorCode.UnsupportedVersion, 1, "version", FormatDetail.UnsupportedVersion, source[1], FormatVersion);
            return false;
        }

        // 5: shorter than 17 bytes; the offset is the length.
        if (source.Length < EncodedSize)
        {
            error = new FormatError(Format, FormatErrorCode.Truncated, source.Length, "length", FormatDetail.ExpectedLength, EncodedSize, length);
            return false;
        }

        // 6: longer than 17 bytes; the offset is the first byte too many.
        if (source.Length > EncodedSize)
        {
            error = new FormatError(Format, FormatErrorCode.Oversized, EncodedSize, "length", FormatDetail.ExpectedLength, EncodedSize, length);
            return false;
        }

        command = new InputCommand(
            BinaryPrimitives.ReadUInt32LittleEndian(source[SequenceOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[ClientStepOffset..]),
            (Buttons)BinaryPrimitives.ReadUInt16LittleEndian(source[ButtonsOffset..]),
            BinaryPrimitives.ReadUInt16LittleEndian(source[AimYawOffset..]),
            BinaryPrimitives.ReadInt16LittleEndian(source[AimPitchOffset..]),
            source[WeaponSlotOffset]);
        error = default;
        return true;
    }
}
