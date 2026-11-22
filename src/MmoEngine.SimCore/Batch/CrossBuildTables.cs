using System;
using System.Buffers.Binary;
using System.Globalization;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Batch;

/// <summary>
/// The fixed math and hitbox tables every build evaluates, so the agreement suite compares every
/// <see cref="SimMath"/> result bit and every hitbox, which the recorded streams reach only in part (US3
/// scenario 8, US5 scenario 5, SC-004; specs/015-shared-simulation-core/contracts/wasm-api.md#cross-build-tables).
/// <see cref="BuildInputs"/> writes the <c>MMXT</c> inputs; <see cref="Evaluate"/> runs them through the
/// same <see cref="SimMath"/> functions and <see cref="HitboxBuilder"/> the step uses and writes the
/// <c>MMXR</c> results.
/// </summary>
/// <remarks>
/// Neither method is tick-path code: both allocate their output, and <see cref="Evaluate"/> throws on a
/// malformed table. The inputs are integer arithmetic over the core's <see cref="SplitMix64"/>, so every
/// build writes the same bytes; <c>CrossBuildTablesTests.InputsArePinned</c> pins their XxHash64, so a
/// change to the table is a visible edit.
/// </remarks>
public static class CrossBuildTables
{
    /// <summary>The <c>MMXT</c> and <c>MMXR</c> version.</summary>
    public const ushort FormatVersion = 1;

    /// <summary>The header: magic, <c>u16</c> version, <c>u16</c> 0, <c>u32</c> math row count M, <c>u32</c> pose row count P.</summary>
    public const int HeaderLength = 16;

    /// <summary>A math input row: <c>u8</c> function ID, 3 zero bytes, <c>u32</c> a bits, <c>u32</c> b bits.</summary>
    public const int MathRowLength = 12;

    /// <summary>A pose input row: <c>u8</c> stance, <c>u8</c> 0, <c>u16</c> aim yaw, <c>f32</c> x, y, z.</summary>
    public const int PoseRowLength = 16;

    /// <summary>A math result row: <c>u32</c> r0 bits and <c>u32</c> r1 bits.</summary>
    public const int MathResultLength = 8;

    /// <summary>A pose result row: 25 floats (three capsules of A, B and radius, then the sphere's centre and radius).</summary>
    public const int PoseResultLength = 100;

    /// <summary>The highest function ID: 1 Sin, 2 Cos, 3 Tan, 4 SinCos, 5 Asin, 6 Acos, 7 Atan, 8 Atan2, 9 SinCosBinaryAngle, 10 Sqrt.</summary>
    public const byte MaxFunctionId = 10;

    /// <summary>
    /// The longest results <see cref="Evaluate"/> writes, 2,147,483,591 bytes: <see cref="Array.MaxLength"/>,
    /// written as a constant so the limit is the same in every build. Counts whose results would be longer
    /// are refused.
    /// </summary>
    public const int MaxResultsLength = 0x7FFFFFC7;

    private const string Format = "tables";

    private const int VersionOffset = 4;
    private const int ReservedOffset = 6;
    private const int MathCountOffset = 8;
    private const int PoseCountOffset = 12;

    private const byte Sin = 1;
    private const byte Cos = 2;
    private const byte Tan = 3;
    private const byte SinCos = 4;
    private const byte Asin = 5;
    private const byte Acos = 6;
    private const byte Atan = 7;
    private const byte Atan2 = 8;
    private const byte SinCosBinaryAngle = 9;
    private const byte Sqrt = 10;

    /// <summary>The math row count M: 4 × 4,112 + 2 × 4,115 + 4,117 + 4,117 + 65,536 + 1,032.</summary>
    private const int MathRowCount = 99_480;

    /// <summary>Every 64th binary angle (1,024) and each quadrant boundary's two neighbours (8).</summary>
    private const int PoseYawCount = 1_032;

    /// <summary>The pose row count P: both stances × 1,032 yaws × three positions.</summary>
    private const int PoseRowCount = 2 * PoseYawCount * 3;

    /// <summary>The drawn patterns' SplitMix64 seed is this + the function ID.</summary>
    private const ulong DrawSeedBase = 0x5EED0000UL;

    /// <summary>The draws one drawn row may take before the deterministic fallback maps the last one into range.</summary>
    private const int MaxAttemptsPerRow = 64;

    private const uint SignBit = 0x80000000u;
    private const uint MagnitudeMask = 0x7FFFFFFFu;
    private const uint PlusZero = 0x00000000u;
    private const uint MinusZero = 0x80000000u;
    private const uint SmallestSubnormal = 0x00000001u;
    private const uint LargestSubnormal = 0x007FFFFFu;
    private const uint SmallestNormal = 0x00800000u;
    private const uint QuarterPi = 0x3F490FDBu;
    private const uint ReductionLimit = 0x46490C00u;
    private const uint MaxValue = 0x7F7FFFFFu;
    private const uint MinusMaxValue = 0xFF7FFFFFu;
    private const uint PlusInfinity = 0x7F800000u;
    private const uint MinusInfinity = 0xFF800000u;
    private const uint QuietNaN = 0x7FC00000u;
    private const uint Half = 0x3F000000u;
    private const uint MinusHalf = 0xBF000000u;
    private const uint One = 0x3F800000u;
    private const uint MinusOne = 0xBF800000u;
    private const uint Two = 0x40000000u;
    private const uint MinusTwo = 0xC0000000u;
    private const uint SevenSixteenths = 0x3EE00000u;
    private const uint ElevenSixteenths = 0x3F300000u;
    private const uint NineteenSixteenths = 0x3F980000u;
    private const uint ThirtyNineSixteenths = 0x401C0000u;

    private static ReadOnlySpan<byte> InputsMagic => "MMXT"u8;

    private static ReadOnlySpan<byte> ResultsMagic => "MMXR"u8;

    /// <summary>
    /// The <c>MMXT</c> v1 inputs, the same bytes on every call and in every build: the 99,480 math rows of
    /// functions 1 to 10, each function's listed special values, then its drawn bit patterns
    /// (<c>SplitMix64.At(0x5EED0000 + function ID, i)</c>, uniform over the stated range, out-of-range draws
    /// rejected in draw order), then its non-finite and out-of-domain rows; then the 6,192 pose rows by
    /// stance, yaw ascending and position as listed.
    /// </summary>
    public static byte[] BuildInputs()
    {
        var bytes = new byte[HeaderLength + (MathRowLength * MathRowCount) + (PoseRowLength * PoseRowCount)];
        WriteHeader(bytes, InputsMagic, MathRowCount, PoseRowCount);
        var rows = new RowWriter(bytes);

        for (var id = Sin; id <= SinCos; id++)
        {
            WriteRadianRows(ref rows, id);
        }

        WriteInverseRows(ref rows, Asin);
        WriteInverseRows(ref rows, Acos);
        WriteAtanRows(ref rows);
        WriteAtan2Rows(ref rows);
        for (var angle = 0u; angle <= ushort.MaxValue; angle++)
        {
            rows.Pair(SinCosBinaryAngle, angle, 0u);
        }

        WriteSqrtRows(ref rows);
        if (rows.Offset != HeaderLength + (MathRowLength * MathRowCount))
        {
            throw new InvalidOperationException("The math rows do not add up to the table's count.");
        }

        WritePoseRows(ref rows);
        if (rows.Offset != bytes.Length)
        {
            throw new InvalidOperationException("The pose rows do not add up to the table's count.");
        }

        return bytes;
    }

    /// <summary>
    /// Evaluates <paramref name="inputs"/>, an <c>MMXT</c> v1 table, with the configuration of
    /// <paramref name="configBlock"/>, and returns the <c>MMXR</c> v1 results: per math row the
    /// <see cref="SimMath"/> function its ID names on its a (and, for Atan2, b) bits, r0 then r1 (SinCos and
    /// SinCosBinaryAngle give sine then cosine, the others their result and 0); per pose row the 25 floats of
    /// <see cref="HitboxBuilder.Build"/> for that stance's layout, yaw and position. The block is checked
    /// first, then the table.
    /// </summary>
    /// <exception cref="CoreFormatException">
    /// The block is malformed (format <c>config</c>), or the table is (format <c>tables</c>), counts whose
    /// results, 16 + 8·M + 100·P bytes, would be longer than <see cref="MaxResultsLength"/> included.
    /// </exception>
    /// <exception cref="ConfigValidationException">The block's configuration fails the guard.</exception>
    public static byte[] Evaluate(ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> configBlock)
    {
        var layouts = CompiledConfig.Create(ConfigBlock.Decode(configBlock)).HitboxLayouts;
        var (mathRows, poseRows, resultsLength) = CheckFrame(inputs);

        var results = new byte[resultsLength];
        WriteHeader(results, ResultsMagic, mathRows, poseRows);
        var output = HeaderLength;
        var input = HeaderLength;
        for (var i = 0; i < mathRows; i++)
        {
            var row = inputs.Slice(input, MathRowLength);
            var function = row[0];
            var a = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(row[4..]));
            var b = BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(row[8..]));
            float r0;
            var r1 = 0f;
            switch (function)
            {
                case Sin:
                    r0 = SimMath.Sin(a);
                    break;
                case Cos:
                    r0 = SimMath.Cos(a);
                    break;
                case Tan:
                    r0 = SimMath.Tan(a);
                    break;
                case SinCos:
                    SimMath.SinCos(a, out r0, out r1);
                    break;
                case Asin:
                    r0 = SimMath.Asin(a);
                    break;
                case Acos:
                    r0 = SimMath.Acos(a);
                    break;
                case Atan:
                    r0 = SimMath.Atan(a);
                    break;
                case Atan2:
                    r0 = SimMath.Atan2(a, b);
                    break;
                case SinCosBinaryAngle:
                    SimMath.SinCosBinaryAngle((ushort)(BinaryPrimitives.ReadUInt32LittleEndian(row[4..]) & 0xFFFFu), out r0, out r1);
                    break;
                case Sqrt:
                    r0 = SimMath.Sqrt(a);
                    break;
                default:
                    throw Refusal(
                        FormatErrorCode.OutOfRange,
                        input,
                        string.Create(CultureInfo.InvariantCulture, $"mathRows[{i}].function"),
                        string.Create(CultureInfo.InvariantCulture, $"unknown function ID {function}"));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(results.AsSpan(output), BitConverter.SingleToUInt32Bits(r0));
            BinaryPrimitives.WriteUInt32LittleEndian(results.AsSpan(output + 4), BitConverter.SingleToUInt32Bits(r1));
            output += MathResultLength;
            input += MathRowLength;
        }

        var hitboxes = new Hitbox[HitboxLayout.CapsuleCount];
        for (var j = 0; j < poseRows; j++)
        {
            var row = inputs.Slice(input, PoseRowLength);
            var stance = row[0];
            if (stance >= layouts.Length)
            {
                throw Refusal(
                    FormatErrorCode.OutOfRange,
                    input,
                    string.Create(CultureInfo.InvariantCulture, $"poseRows[{j}].stance"),
                    string.Create(CultureInfo.InvariantCulture, $"unknown stance {stance}"));
            }

            var yaw = BinaryPrimitives.ReadUInt16LittleEndian(row[2..]);
            var position = new Vec3(
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(row[4..])),
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(row[8..])),
                BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(row[12..])));
            var bounds = HitboxBuilder.Build(layouts[stance], position, yaw, hitboxes);
            var destination = results.AsSpan(output, PoseResultLength);
            for (var c = 0; c < HitboxLayout.CapsuleCount; c++)
            {
                var capsule = hitboxes[c];
                var at = c * 28;
                WriteVec3(destination[at..], capsule.A);
                WriteVec3(destination[(at + 12)..], capsule.B);
                WriteFloat(destination[(at + 24)..], capsule.Radius);
            }

            WriteVec3(destination[84..], bounds.Centre);
            WriteFloat(destination[96..], bounds.Radius);
            output += PoseResultLength;
            input += PoseRowLength;
        }

        return results;
    }

    // ---------------------------------------------------------------- the math rows

    /// <summary>
    /// Sin, Cos, Tan and SinCos (4,112 rows): ±0, the smallest and largest subnormal, the smallest normal,
    /// π/4 and 12867 each with its two neighbours, ±<c>float.MaxValue</c>; 3,072 finite patterns with
    /// |x| ≤ 12867, then 1,024 above it (the Payne–Hanek path); then NaN, +∞ and −∞.
    /// </summary>
    private static void WriteRadianRows(ref RowWriter rows, byte id)
    {
        rows.Singles(id, PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal, SmallestNormal);
        rows.Neighbours(id, QuarterPi);
        rows.Neighbours(id, ReductionLimit);
        rows.Singles(id, MaxValue, MinusMaxValue);
        var counter = 0UL;
        rows.Drawn(id, ref counter, 3_072, 0u, ReductionLimit, positiveOnly: false);
        rows.Drawn(id, ref counter, 1_024, ReductionLimit + 1u, MaxValue, positiveOnly: false);
        rows.Singles(id, QuietNaN, PlusInfinity, MinusInfinity);
    }

    /// <summary>
    /// Asin and Acos (4,115 rows): ±0 and the smallest and largest subnormal, ±½ each with its two
    /// neighbours, ±1 each with its one neighbour inside the domain; 4,096 patterns in [−1, 1]; then NaN,
    /// just outside +1 and −1, and ±2.
    /// </summary>
    private static void WriteInverseRows(ref RowWriter rows, byte id)
    {
        rows.Singles(id, PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal);
        rows.Neighbours(id, Half);
        rows.Neighbours(id, MinusHalf);
        rows.Singles(id, One - 1u, One, MinusOne - 1u, MinusOne);
        var counter = 0UL;
        rows.Drawn(id, ref counter, 4_096, 0u, One, positiveOnly: false);
        rows.Singles(id, QuietNaN, One + 1u, MinusOne + 1u, Two, MinusTwo);
    }

    /// <summary>
    /// Atan (4,117 rows): ±0 and the smallest and largest subnormal, the four fdlibm breakpoints (7/16,
    /// 11/16, 19/16, 39/16) each with its two neighbours, ±<c>float.MaxValue</c>, ±∞; 4,096 finite patterns;
    /// then NaN.
    /// </summary>
    private static void WriteAtanRows(ref RowWriter rows)
    {
        rows.Singles(Atan, PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal);
        rows.Neighbours(Atan, SevenSixteenths);
        rows.Neighbours(Atan, ElevenSixteenths);
        rows.Neighbours(Atan, NineteenSixteenths);
        rows.Neighbours(Atan, ThirtyNineSixteenths);
        rows.Singles(Atan, MaxValue, MinusMaxValue, PlusInfinity, MinusInfinity);
        var counter = 0UL;
        rows.Drawn(Atan, ref counter, 4_096, 0u, MaxValue, positiveOnly: false);
        rows.Singles(Atan, QuietNaN);
    }

    /// <summary>
    /// Atan2 (4,117 rows, a is y and b is x): the 16 combinations of {+0, −0, +∞, −∞}, a the outer loop;
    /// the axis directions (+0, 1), (1, +0), (+0, −1) and (−1, +0); 4,096 finite pairs; then one row with a
    /// NaN.
    /// </summary>
    private static void WriteAtan2Rows(ref RowWriter rows)
    {
        ReadOnlySpan<uint> values = [PlusZero, MinusZero, PlusInfinity, MinusInfinity];
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < values.Length; j++)
            {
                rows.Pair(Atan2, values[i], values[j]);
            }
        }

        rows.Pair(Atan2, PlusZero, One);
        rows.Pair(Atan2, One, PlusZero);
        rows.Pair(Atan2, PlusZero, MinusOne);
        rows.Pair(Atan2, MinusOne, PlusZero);
        var counter = 0UL;
        rows.DrawnPairs(Atan2, ref counter, 4_096);
        rows.Pair(Atan2, QuietNaN, One);
    }

    /// <summary>
    /// Sqrt (1,032 rows): +0, −0, the smallest and largest subnormal, ±<c>float.MaxValue</c>; 1,024 positive
    /// finite patterns; then NaN and one finite negative argument.
    /// </summary>
    private static void WriteSqrtRows(ref RowWriter rows)
    {
        rows.Singles(Sqrt, PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal, MaxValue, MinusMaxValue);
        var counter = 0UL;
        rows.Drawn(Sqrt, ref counter, 1_024, SmallestSubnormal, MaxValue, positiveOnly: true);
        rows.Singles(Sqrt, QuietNaN, MinusOne);
    }

    // ---------------------------------------------------------------- the pose rows

    /// <summary>
    /// Both stances (0 Standing, 1 Crouched) × the 1,032 yaws ascending × the positions (0, 0, 0),
    /// (4096.25, 37.5, −7000.125) and (−8191.5, −200, 8191.5).
    /// </summary>
    private static void WritePoseRows(ref RowWriter rows)
    {
        for (byte stance = 0; stance < 2; stance++)
        {
            var yaws = 0;
            for (var yaw = 0; yaw <= ushort.MaxValue; yaw++)
            {
                if ((yaw & 63) != 0 && !IsQuadrantNeighbour(yaw))
                {
                    continue;
                }

                yaws++;
                rows.Pose(stance, (ushort)yaw, 0f, 0f, 0f);
                rows.Pose(stance, (ushort)yaw, 4096.25f, 37.5f, -7000.125f);
                rows.Pose(stance, (ushort)yaw, -8191.5f, -200f, 8191.5f);
            }

            if (yaws != PoseYawCount)
            {
                throw new InvalidOperationException("The pose yaws do not add up to the table's count.");
            }
        }
    }

    /// <summary>The two neighbours of each quadrant boundary: 0xFFFF, 0x0001, 0x3FFF, 0x4001, 0x7FFF, 0x8001, 0xBFFF and 0xC001.</summary>
    private static bool IsQuadrantNeighbour(int yaw)
    {
        var low = yaw & 0x3FFF;
        return low == 0x0001 || low == 0x3FFF;
    }

    // ---------------------------------------------------------------- decoding

    /// <summary>
    /// The frame checks: the magic, the version 1, the reserved 0, the results' length 16 + 8·M + 100·P
    /// against <see cref="MaxResultsLength"/>, then the length against 16 + 12·M + 16·P. Returns M, P and
    /// the results' length.
    /// </summary>
    private static (int MathRows, int PoseRows, int ResultsLength) CheckFrame(ReadOnlySpan<byte> inputs)
    {
        var length = (ulong)inputs.Length;
        if (inputs.Length < InputsMagic.Length)
        {
            throw Refusal(FormatErrorCode.Truncated, inputs.Length, "magic", FormatDetail.ExpectedLength, (ulong)InputsMagic.Length, length);
        }

        if (!inputs[..InputsMagic.Length].SequenceEqual(InputsMagic))
        {
            throw Refusal(FormatErrorCode.UnknownMagic, 0, "magic", FrameText.WrongMagic(inputs, InputsMagic, "MMXT"));
        }

        if (inputs.Length < ReservedOffset)
        {
            throw Refusal(FormatErrorCode.Truncated, inputs.Length, "version", FormatDetail.ExpectedLength, ReservedOffset, length);
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(inputs[VersionOffset..]);
        if (version != FormatVersion)
        {
            throw Refusal(FormatErrorCode.UnsupportedVersion, VersionOffset, "version", FormatDetail.UnsupportedVersion, version, FormatVersion);
        }

        if (inputs.Length < HeaderLength)
        {
            throw Refusal(FormatErrorCode.Truncated, inputs.Length, "header", FormatDetail.ExpectedLength, HeaderLength, length);
        }

        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(inputs[ReservedOffset..]);
        if (reserved != 0)
        {
            throw Refusal(FormatErrorCode.Inconsistent, ReservedOffset, "reserved", string.Create(CultureInfo.InvariantCulture, $"the reserved u16 is {reserved}, not 0"));
        }

        var mathRows = BinaryPrimitives.ReadUInt32LittleEndian(inputs[MathCountOffset..]);
        var poseRows = BinaryPrimitives.ReadUInt32LittleEndian(inputs[PoseCountOffset..]);

        // A pose result row is 100 bytes against a 16-byte input row, so counts that a whole input holds can
        // still need more result bytes than an array takes. This is checked before the length, from the
        // header alone. Both counts are below 2^32, so the sum fits in a long.
        var resultsLength = HeaderLength + (MathResultLength * (long)mathRows) + (PoseResultLength * (long)poseRows);
        if (resultsLength > MaxResultsLength)
        {
            throw Refusal(
                FormatErrorCode.Oversized,
                MathCountOffset,
                "counts",
                string.Create(CultureInfo.InvariantCulture, $"the counts need {resultsLength} result bytes, above {MaxResultsLength}"));
        }

        var needed = HeaderLength + (MathRowLength * (long)mathRows) + (PoseRowLength * (long)poseRows);
        if (inputs.Length != needed)
        {
            throw Refusal(FormatErrorCode.Inconsistent, MathCountOffset, "length", string.Create(CultureInfo.InvariantCulture, $"{inputs.Length} bytes, the counts need {needed}"));
        }

        // The length is an int and equals 16 + 12·M + 16·P, so both counts fit in an int; the results'
        // length is at most MaxResultsLength, so it fits too.
        return ((int)mathRows, (int)poseRows, (int)resultsLength);
    }

    private static CoreFormatException Refusal(FormatErrorCode code, long offset, string field, string detail) =>
        new(new FormatError(Format, code, offset, field, detail));

    private static CoreFormatException Refusal(FormatErrorCode code, long offset, string field, FormatDetail detail, ulong a, ulong b) =>
        new(new FormatError(Format, code, offset, field, detail, a, b));

    // ---------------------------------------------------------------- writing

    private static void WriteHeader(Span<byte> destination, ReadOnlySpan<byte> magic, int mathRows, int poseRows)
    {
        magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[VersionOffset..], FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[ReservedOffset..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[MathCountOffset..], (uint)mathRows);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[PoseCountOffset..], (uint)poseRows);
    }

    private static void WriteVec3(Span<byte> destination, Vec3 value)
    {
        WriteFloat(destination, value.X);
        WriteFloat(destination[4..], value.Y);
        WriteFloat(destination[8..], value.Z);
    }

    private static void WriteFloat(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination, BitConverter.SingleToUInt32Bits(value));

    /// <summary>
    /// The smallest all-ones mask covering <paramref name="value"/>: the draws keep only these magnitude bits
    /// before the range check, so a narrow range rejects fewer draws.
    /// </summary>
    private static uint CoveringMask(uint value)
    {
        var mask = value;
        mask |= mask >> 1;
        mask |= mask >> 2;
        mask |= mask >> 4;
        mask |= mask >> 8;
        mask |= mask >> 16;
        return mask;
    }

    /// <summary>Writes the inputs' rows in order from the end of the header.</summary>
    private ref struct RowWriter(Span<byte> bytes)
    {
        private readonly Span<byte> _bytes = bytes;

        /// <summary>Where the next row goes.</summary>
        public int Offset { get; private set; } = HeaderLength;

        /// <summary>One math row: the function ID, 3 zero bytes, a and b.</summary>
        public void Pair(byte id, uint a, uint b)
        {
            var row = _bytes.Slice(Offset, MathRowLength);
            row[0] = id;
            row[1..4].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(row[4..], a);
            BinaryPrimitives.WriteUInt32LittleEndian(row[8..], b);
            Offset += MathRowLength;
        }

        /// <summary>One single-argument row per value, in order, each with b 0.</summary>
        public void Singles(byte id, params ReadOnlySpan<uint> values)
        {
            for (var k = 0; k < values.Length; k++)
            {
                Pair(id, values[k], 0u);
            }
        }

        /// <summary>A value with its two neighbours, in bit order: bits − 1, the value, bits + 1.</summary>
        public void Neighbours(byte id, uint bits) => Singles(id, bits - 1u, bits, bits + 1u);

        /// <summary>
        /// <paramref name="count"/> single-argument rows drawn from <c>SplitMix64.At(0x5EED0000 + id,
        /// counter)</c>, each uniform over the bit patterns whose magnitude is in [<paramref name="low"/>,
        /// <paramref name="high"/>] (with either sign, or positive only). A draw's upper 32 bits are the
        /// candidate: its sign bit is kept (or cleared), its magnitude masked to the bits that cover
        /// <paramref name="high"/>, and an out-of-range candidate is rejected and the next draw taken. After
        /// <see cref="MaxAttemptsPerRow"/> rejections the last candidate is mapped into the range by
        /// remainder, so every build takes the same draws.
        /// </summary>
        public void Drawn(byte id, ref ulong counter, int count, uint low, uint high, bool positiveOnly)
        {
            var seed = DrawSeedBase + id;
            var mask = CoveringMask(high);
            var span = high - low + 1u;
            for (var row = 0; row < count; row++)
            {
                var candidate = 0u;
                var accepted = false;
                for (var attempt = 0; attempt < MaxAttemptsPerRow && !accepted; attempt++)
                {
                    candidate = (uint)(SplitMix64.At(seed, counter) >> 32);
                    counter++;
                    accepted = InRange(candidate & mask, low, high);
                }

                var magnitude = accepted ? candidate & mask : low + ((candidate & MagnitudeMask) % span);
                var sign = positiveOnly ? 0u : candidate & SignBit;
                Pair(id, sign | magnitude, 0u);
            }
        }

        /// <summary>
        /// <paramref name="count"/> Atan2 rows of two finite floats drawn from <c>SplitMix64.At(0x5EED0000 +
        /// id, counter)</c>: a is the draw's upper 32 bits and b its lower 32 bits, and a draw with a
        /// non-finite half is rejected. After <see cref="MaxAttemptsPerRow"/> rejections each half's
        /// magnitude is mapped into the finite range by remainder.
        /// </summary>
        public void DrawnPairs(byte id, ref ulong counter, int count)
        {
            var seed = DrawSeedBase + id;
            for (var row = 0; row < count; row++)
            {
                var draw = 0UL;
                var accepted = false;
                for (var attempt = 0; attempt < MaxAttemptsPerRow && !accepted; attempt++)
                {
                    draw = SplitMix64.At(seed, counter);
                    counter++;
                    accepted = (((uint)(draw >> 32)) & MagnitudeMask) <= MaxValue && (((uint)draw) & MagnitudeMask) <= MaxValue;
                }

                var a = (uint)(draw >> 32);
                var b = (uint)draw;
                if (!accepted)
                {
                    a = (a & SignBit) | ((a & MagnitudeMask) % (MaxValue + 1u));
                    b = (b & SignBit) | ((b & MagnitudeMask) % (MaxValue + 1u));
                }

                Pair(id, a, b);
            }
        }

        /// <summary>One pose row: the stance, a zero byte, the yaw and the position's bits.</summary>
        public void Pose(byte stance, ushort yaw, float x, float y, float z)
        {
            var row = _bytes.Slice(Offset, PoseRowLength);
            row[0] = stance;
            row[1] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(row[2..], yaw);
            WriteFloat(row[4..], x);
            WriteFloat(row[8..], y);
            WriteFloat(row[12..], z);
            Offset += PoseRowLength;
        }

        private static bool InRange(uint magnitude, uint low, uint high) => magnitude >= low && magnitude <= high;
    }
}
