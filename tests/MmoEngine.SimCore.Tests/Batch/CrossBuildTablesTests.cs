using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Batch;

/// <summary>
/// The server half of the cross-build tables (contracts/wasm-api.md#cross-build-tables), which support
/// US3 scenario 8, US5 scenario 5 and SC-004; the cross-build halves run the same inputs in every
/// WebAssembly build (Phase 11). <c>CrossBuildTables.BuildInputs()</c> returns the fixed <c>MMXT</c> table:
/// the math rows of every <see cref="SimMath"/> function, then 6,192 pose rows; <c>Evaluate</c> returns the
/// <c>MMXR</c> results. The tests read both with <see cref="BinaryPrimitives"/> at the contract's offsets:
/// a 16-byte header (magic, <c>u16</c> version, <c>u16</c> 0, <c>u32</c> M, <c>u32</c> P); math rows of 12
/// bytes (<c>u8</c> function ID, 3 zero bytes, <c>u32</c> a bits, <c>u32</c> b bits); pose rows of 16 bytes
/// (<c>u8</c> stance, <c>u8</c> 0, <c>u16</c> yaw, <c>f32</c> x, y, z); math results of 8 bytes (r0, r1) and
/// pose results of 25 floats.
/// </summary>
public sealed class CrossBuildTablesTests
{
    private const int HeaderLength = 16;
    private const int MathRowLength = 12;
    private const int PoseRowLength = 16;
    private const int MathResultLength = 8;
    private const int PoseResultFloats = 25;
    private const int PoseResultLength = PoseResultFloats * 4;

    /// <summary>The math rows of wasm-api.md's list: the sum of <see cref="Functions"/>' row counts.</summary>
    private const int MathRowCount = 99_480;

    /// <summary>Both stances × <see cref="PoseYawCount"/> yaws × three positions.</summary>
    private const int PoseRowCount = 6_192;

    /// <summary>Every 64th binary angle (1,024) plus each quadrant boundary's two neighbours (8).</summary>
    private const int PoseYawCount = 1_032;

    /// <summary>
    /// The <c>XxHash64</c> (seed 0) of <c>BuildInputs()</c>, pinned by T129 when the table landed. Any change
    /// to the table is a visible edit of this constant.
    /// </summary>
    private const ulong PinnedInputsXxHash64 = 0x22B4C43A2ACFD77EUL;

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

    /// <summary>12867f: the largest |x| the Cody–Waite reduction handles; Payne–Hanek takes over above it.</summary>
    private const float ReductionLimitValue = 12867f;

    /// <summary>
    /// The test's own tally of wasm-api.md's list, keyed by function ID 1 to 10, with each function's row
    /// count and its rows in the listed order. A value's two neighbours are its bits − 1 and + 1, written
    /// below, at and above it in bit order; ± lists + first. Each function's listed special values come
    /// first, then its drawn rows (checked against their stated range only: the draws are the core's), then
    /// its non-finite and out-of-domain rows.
    /// </summary>
    private static readonly FunctionRows[] Functions =
    [
        Radian(1, "Sin"),
        Radian(2, "Cos"),
        Radian(3, "Tan"),
        Radian(4, "SinCos"),
        Inverse(5, "Asin"),
        Inverse(6, "Acos"),
        new(
            7,
            "Atan",
            4_117,
            [
                Exactly("±0 and the smallest and largest subnormal", PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal),
                Exactly("7/16 and its two neighbours", SevenSixteenths - 1, SevenSixteenths, SevenSixteenths + 1),
                Exactly("11/16 and its two neighbours", ElevenSixteenths - 1, ElevenSixteenths, ElevenSixteenths + 1),
                Exactly("19/16 and its two neighbours", NineteenSixteenths - 1, NineteenSixteenths, NineteenSixteenths + 1),
                Exactly("39/16 and its two neighbours", ThirtyNineSixteenths - 1, ThirtyNineSixteenths, ThirtyNineSixteenths + 1),
                Exactly("±float.MaxValue", MaxValue, MinusMaxValue),
                Exactly("±∞", PlusInfinity, MinusInfinity),
                Drawn("4,096 finite bit patterns", 4_096, float.IsFinite),
                NaNRow(),
            ]),
        new(
            8,
            "Atan2",
            4_117,
            [
                Pairs("every combination of ±0 and ±∞ for a and b, a major", ZeroInfinityCombinations()),
                Pairs(
                    "the axis directions (+0, 1), (1, +0), (+0, −1) and (−1, +0)",
                    (PlusZero, One),
                    (One, PlusZero),
                    (PlusZero, MinusOne),
                    (MinusOne, PlusZero)),
                new("4,096 finite pairs", 4_096, (_, a, b) => float.IsFinite(F(a)) && float.IsFinite(F(b))),
                new("NaN", 1, (_, a, b) => float.IsNaN(F(a)) || float.IsNaN(F(b))),
            ]),
        new(
            9,
            "SinCosBinaryAngle",
            65_536,
            [
                new("every angle 0 to 65,535, ascending", 65_536, (k, a, b) => a == (uint)k && b == 0u),
            ]),
        new(
            10,
            "Sqrt",
            1_032,
            [
                Exactly("+0, −0 and the smallest and largest subnormal", PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal),
                Exactly("±float.MaxValue", MaxValue, MinusMaxValue),
                Drawn("1,024 positive finite bit patterns", 1_024, x => x > 0f && float.IsFinite(x)),
                NaNRow(),
                new("a negative argument", 1, (_, a, b) => b == 0u && float.IsFinite(F(a)) && F(a) < 0f),
            ]),
    ];

    /// <summary>The pose rows' three positions, in the listed order.</summary>
    private static readonly (float X, float Y, float Z)[] PosePositions =
    [
        (0f, 0f, 0f),
        (4096.25f, 37.5f, -7000.125f),
        (-8191.5f, -200f, 8191.5f),
    ];

    /// <summary>The 25 floats of a pose result, in order.</summary>
    private static readonly string[] PoseResultNames =
    [
        "head A.x", "head A.y", "head A.z", "head B.x", "head B.y", "head B.z", "head radius",
        "torso A.x", "torso A.y", "torso A.z", "torso B.x", "torso B.y", "torso B.z", "torso radius",
        "legs A.x", "legs A.y", "legs A.z", "legs B.x", "legs B.y", "legs B.z", "legs radius",
        "sphere centre.x", "sphere centre.y", "sphere centre.z", "sphere radius",
    ];

    /// <summary>Rows of <see cref="RefusesMalformedTables"/>.</summary>
    public static TheoryData<string> MalformedCases =>
    [
        "magic",
        "version",
        "length",
        "function",
        "stance",
    ];

    /// <summary>
    /// <c>BuildInputs()</c> returns the same bytes on every call, with the row counts and the hash the test
    /// pins (contracts/wasm-api.md#tests): two calls give identical bytes; the header is magic <c>MMXT</c>,
    /// version 1, reserved 0, M math rows and P = 6,192 pose rows, and the length is 16 + 12·M + 16·P; every
    /// function ID 1 to 10 has the row count of the test's own tally of wasm-api.md's list, and M is their sum
    /// (99,480); the math rows are ordered by function ID, then as listed, with zero reserved bytes and b 0
    /// for every function but Atan2; the pose rows are ordered by stance (0 Standing, 1 Crouched), then yaw
    /// ascending over the 1,032 yaws (every 64th angle plus each quadrant boundary's two neighbours), then
    /// the three positions as listed; and the bytes' <c>XxHash64</c> equals <see cref="PinnedInputsXxHash64"/>.
    /// </summary>
    [Fact]
    public void InputsArePinned()
    {
        var inputs = CrossBuildTables.BuildInputs();
        var again = CrossBuildTables.BuildInputs();
        AssertSameBytes(inputs, again, "two BuildInputs() calls");

        Assert.True(inputs.Length >= HeaderLength, $"BuildInputs() returned {inputs.Length} bytes, fewer than the {HeaderLength}-byte header");
        Assert.Equal("MMXT", Encoding.ASCII.GetString(inputs, 0, 4));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(inputs.AsSpan(4)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(inputs.AsSpan(6)));
        var m = BinaryPrimitives.ReadUInt32LittleEndian(inputs.AsSpan(8));
        var p = BinaryPrimitives.ReadUInt32LittleEndian(inputs.AsSpan(12));
        Assert.Equal((uint)PoseRowCount, p);
        Assert.Equal(HeaderLength + (MathRowLength * (long)m) + (PoseRowLength * (long)p), inputs.LongLength);

        // The test's own table must add up before the table under test is held to it.
        foreach (var function in Functions)
        {
            Assert.Equal(function.RowCount, function.Groups.Sum(group => group.Count));
        }

        Assert.Equal(MathRowCount, Functions.Sum(function => function.RowCount));

        var mathRows = (int)m;
        var tally = new int[256];
        var malformed = new List<string>();
        var previous = 0;
        for (var i = 0; i < mathRows; i++)
        {
            var row = MathRow(inputs, i);
            var id = row[0];
            tally[id]++;
            if (row[1] != 0 || row[2] != 0 || row[3] != 0)
            {
                Note(malformed, $"math row {i}: reserved bytes {Convert.ToHexString(row.Slice(1, 3))}, not 000000");
            }

            if (id < previous)
            {
                Note(malformed, $"math row {i}: function {id} after function {previous}; rows are ordered by function ID");
            }

            previous = id;
        }

        var counts = new List<string>();
        foreach (var function in Functions)
        {
            if (tally[function.Id] != function.RowCount)
            {
                counts.Add($"{function.Name} (function {function.Id}): {tally[function.Id]} rows, not {function.RowCount}");
            }
        }

        for (var id = 0; id < tally.Length; id++)
        {
            if (tally[id] != 0 && Array.FindIndex(Functions, function => function.Id == id) < 0)
            {
                counts.Add($"function {id}: {tally[id]} rows of an unknown function");
            }
        }

        Assert.True(counts.Count == 0, "The per-function row counts differ from wasm-api.md's list:\n" + string.Join("\n", counts));
        Assert.True(m == MathRowCount, $"M is {m}, not {MathRowCount}, the sum of the per-function counts");
        Assert.True(malformed.Count == 0, "The math rows are malformed:\n" + string.Join("\n", malformed));

        var unlisted = new List<string>();
        var index = 0;
        foreach (var function in Functions)
        {
            foreach (var group in function.Groups)
            {
                for (var k = 0; k < group.Count; k++, index++)
                {
                    var row = MathRow(inputs, index);
                    var a = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
                    var b = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
                    if (row[0] != function.Id || !group.Accepts(k, a, b))
                    {
                        Note(unlisted, $"math row {index}: function {row[0]} a 0x{a:X8} b 0x{b:X8} is not {function.Name}'s {group.Name}, entry {k}");
                    }
                }
            }
        }

        Assert.True(unlisted.Count == 0, "The math rows are not in the listed order:\n" + string.Join("\n", unlisted));

        var yaws = PoseYaws();
        Assert.Equal(PoseYawCount, yaws.Length);
        var misplaced = new List<string>();
        for (var j = 0; j < PoseRowCount; j++)
        {
            var row = PoseRow(inputs, mathRows, j);
            var stance = j / (PoseYawCount * PosePositions.Length);
            var yaw = yaws[(j / PosePositions.Length) % PoseYawCount];
            var (x, y, z) = PosePositions[j % PosePositions.Length];
            var actualYaw = BinaryPrimitives.ReadUInt16LittleEndian(row[2..]);
            var xBits = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            var yBits = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            var zBits = BinaryPrimitives.ReadUInt32LittleEndian(row[12..]);
            if (row[0] != stance || row[1] != 0 || actualYaw != yaw || xBits != Bits(x) || yBits != Bits(y) || zBits != Bits(z))
            {
                Note(
                    misplaced,
                    $"pose row {j}: stance {row[0]} pad {row[1]} yaw 0x{actualYaw:X4} at 0x{xBits:X8} 0x{yBits:X8} 0x{zBits:X8}, not stance {stance} yaw 0x{yaw:X4} at ({R(x)}, {R(y)}, {R(z)})");
            }
        }

        Assert.True(misplaced.Count == 0, "The pose rows are not ordered by stance, then yaw ascending, then position as listed:\n" + string.Join("\n", misplaced));

        var hash = XxHash64.HashToUInt64(inputs, 0);
        Assert.True(
            hash == PinnedInputsXxHash64,
            $"BuildInputs() hashes to 0x{hash:X16}, not the pinned 0x{PinnedInputsXxHash64:X16}: the table changed. Change the pinned constant only together with the change to the table");
    }

    /// <summary>
    /// US3 scenario 8, US5 scenario 5 and SC-004, server half: Given <c>Evaluate(BuildInputs(), the config
    /// block of default.json)</c>, Then the results header is <c>MMXR</c> v1 with the inputs' counts and the
    /// length is 16 + 8·M + 100·P; every math row's r0 and r1 bits equal the <see cref="SimMath"/> call its
    /// function ID names on its a and b bits (SinCos and SinCosBinaryAngle give sine then cosine, Atan2 takes
    /// a as y and b as x, the others give the result and 0); and every pose row's 25 floats equal
    /// <see cref="HitboxBuilder.Build"/> for that stance's <see cref="HitboxLayout"/>, yaw and position (head,
    /// torso and legs, each A, B and radius, then the bounding sphere's centre and radius), bit for bit.
    /// </summary>
    [Fact]
    public void EachRowEqualsTheDirectCall()
    {
        var inputs = CrossBuildTables.BuildInputs();
        var block = ConfigBlock.Encode(TestConfigs.Default());

        var results = CrossBuildTables.Evaluate(inputs, block);

        var m = (int)BinaryPrimitives.ReadUInt32LittleEndian(inputs.AsSpan(8));
        var p = (int)BinaryPrimitives.ReadUInt32LittleEndian(inputs.AsSpan(12));
        Assert.True(results.Length >= HeaderLength, $"Evaluate returned {results.Length} bytes, fewer than the {HeaderLength}-byte header");
        Assert.Equal("MMXR", Encoding.ASCII.GetString(results, 0, 4));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(results.AsSpan(4)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(results.AsSpan(6)));
        Assert.Equal((uint)m, BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(8)));
        Assert.Equal((uint)p, BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(12)));
        Assert.Equal(HeaderLength + (MathResultLength * (long)m) + (PoseResultLength * (long)p), results.LongLength);

        var mathMismatches = 0;
        var mathFailures = new List<string>();
        for (var i = 0; i < m; i++)
        {
            var row = MathRow(inputs, i);
            var function = row[0];
            var a = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
            var b = BinaryPrimitives.ReadUInt32LittleEndian(row[8..]);
            var r0 = BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(HeaderLength + (MathResultLength * i)));
            var r1 = BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(HeaderLength + (MathResultLength * i) + 4));
            if (!TryCallDirectly(function, a, b, out var e0, out var e1))
            {
                mathMismatches++;
                Note(mathFailures, $"math row {i}: unknown function {function}");
            }
            else if (r0 != e0 || r1 != e1)
            {
                mathMismatches++;
                Note(mathFailures, $"math row {i}: {Name(function)}(a 0x{a:X8}, b 0x{b:X8}) gave r0 0x{r0:X8} r1 0x{r1:X8}; the direct call gives r0 0x{e0:X8} r1 0x{e1:X8}");
            }
        }

        Assert.True(mathMismatches == 0, $"{mathMismatches} of {m} math rows differ from the direct SimMath call:\n" + string.Join("\n", mathFailures));

        var layouts = CompiledConfig.Create(ConfigBlock.Decode(block)).HitboxLayouts;
        var hitboxes = new Hitbox[HitboxLayout.CapsuleCount];
        var expected = new float[PoseResultFloats];
        var poseMismatches = 0;
        var poseFailures = new List<string>();
        for (var j = 0; j < p; j++)
        {
            var row = PoseRow(inputs, m, j);
            var stance = row[0];
            var yaw = BinaryPrimitives.ReadUInt16LittleEndian(row[2..]);
            var position = new Vec3(
                F(BinaryPrimitives.ReadUInt32LittleEndian(row[4..])),
                F(BinaryPrimitives.ReadUInt32LittleEndian(row[8..])),
                F(BinaryPrimitives.ReadUInt32LittleEndian(row[12..])));
            if (stance >= layouts.Length)
            {
                poseMismatches++;
                Note(poseFailures, $"pose row {j}: unknown stance {stance}");
                continue;
            }

            var bounds = HitboxBuilder.Build(layouts[stance], position, yaw, hitboxes);
            for (var c = 0; c < HitboxLayout.CapsuleCount; c++)
            {
                var capsule = hitboxes[c];
                expected[(c * 7) + 0] = capsule.A.X;
                expected[(c * 7) + 1] = capsule.A.Y;
                expected[(c * 7) + 2] = capsule.A.Z;
                expected[(c * 7) + 3] = capsule.B.X;
                expected[(c * 7) + 4] = capsule.B.Y;
                expected[(c * 7) + 5] = capsule.B.Z;
                expected[(c * 7) + 6] = capsule.Radius;
            }

            expected[21] = bounds.Centre.X;
            expected[22] = bounds.Centre.Y;
            expected[23] = bounds.Centre.Z;
            expected[24] = bounds.Radius;

            var start = HeaderLength + (MathResultLength * m) + (PoseResultLength * j);
            for (var f = 0; f < PoseResultFloats; f++)
            {
                var actual = BinaryPrimitives.ReadUInt32LittleEndian(results.AsSpan(start + (4 * f)));
                if (actual != Bits(expected[f]))
                {
                    poseMismatches++;
                    Note(poseFailures, $"pose row {j} (stance {stance}, yaw 0x{yaw:X4}): {PoseResultNames[f]} is 0x{actual:X8}; HitboxBuilder gives 0x{Bits(expected[f]):X8}");
                }
            }
        }

        Assert.True(poseMismatches == 0, $"{poseMismatches} pose result floats differ from HitboxBuilder:\n" + string.Join("\n", poseFailures));
    }

    /// <summary>
    /// contracts/wasm-api.md#cross-build-tables: a malformed table throws <see cref="CoreFormatException"/>
    /// with format <c>tables</c> and the message <c>tables &lt;field&gt; at offset &lt;n&gt;: &lt;detail&gt;</c>,
    /// and returns no results. Each row starts from <c>BuildInputs()</c> and the default block:
    /// <c>magic</c> (written <c>MMXX</c>) is <c>UnknownMagic</c> at 0, field <c>magic</c>, detail
    /// <c>the magic is 4d4d5858, not 4d4d5854 (MMXT)</c> (the core's wrong-magic wording, as for <c>MMCD</c>
    /// and <c>MMRS</c>); <c>version</c> 2 is <c>UnsupportedVersion</c> at 4, field <c>version</c>,
    /// <c>unsupported version 2; this reader supports version 1</c>; <c>length</c> (one byte short) is
    /// <c>Inconsistent</c> at 8, field <c>length</c>, <c>&lt;n&gt; bytes, the counts need &lt;m&gt;</c>;
    /// <c>function</c> ID 11 in math row 0 is <c>OutOfRange</c> at 16, field <c>mathRows[0].function</c>,
    /// <c>unknown function ID 11</c>; and <c>stance</c> 2 in pose row 0 is <c>OutOfRange</c> at 16 + 12·M,
    /// field <c>poseRows[0].stance</c>, <c>unknown stance 2</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(MalformedCases))]
    public void RefusesMalformedTables(string caseName)
    {
        var inputs = CrossBuildTables.BuildInputs();
        var block = ConfigBlock.Encode(TestConfigs.Default());
        var m = BinaryPrimitives.ReadUInt32LittleEndian(inputs.AsSpan(8));
        var copy = (byte[])inputs.Clone();
        FormatErrorCode code;
        long offset;
        string field;
        string detail;
        switch (caseName)
        {
            case "magic":
                "MMXX"u8.CopyTo(copy);
                (code, offset, field, detail) = (FormatErrorCode.UnknownMagic, 0, "magic", "the magic is 4d4d5858, not 4d4d5854 (MMXT)");
                break;
            case "version":
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(4), 2);
                (code, offset, field, detail) = (FormatErrorCode.UnsupportedVersion, 4, "version", "unsupported version 2; this reader supports version 1");
                break;
            case "length":
                copy = inputs[..^1];
                (code, offset, field, detail) = (
                    FormatErrorCode.Inconsistent,
                    8,
                    "length",
                    string.Create(CultureInfo.InvariantCulture, $"{copy.Length} bytes, the counts need {inputs.Length}"));
                break;
            case "function":
                copy[HeaderLength] = 11;
                (code, offset, field, detail) = (FormatErrorCode.OutOfRange, HeaderLength, "mathRows[0].function", "unknown function ID 11");
                break;
            case "stance":
                offset = HeaderLength + (MathRowLength * (long)m);
                copy[offset] = 2;
                (code, field, detail) = (FormatErrorCode.OutOfRange, "poseRows[0].stance", "unknown stance 2");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of RefusesMalformedTables.");
        }

        byte[]? results = null;
        var thrown = Assert.Throws<CoreFormatException>(() => results = CrossBuildTables.Evaluate(copy, block));

        Assert.Equal("tables", thrown.Error.Format);
        Assert.Equal(code, thrown.Error.Code);
        Assert.Equal(offset, thrown.Error.Offset);
        Assert.Equal(field, thrown.Error.Field);
        Assert.Equal(detail, thrown.Error.Detail);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"tables {field} at offset {offset}: {detail}"), thrown.Message);
        Assert.Null(results);
    }

    /// <summary>
    /// contracts/wasm-api.md#cross-build-tables: <c>Evaluate</c> compiles the config block before it reads a
    /// row, so a block with <c>movement.gravity</c> NaN throws <see cref="ConfigValidationException"/> whose
    /// errors are exactly [<c>movement.gravity: must be finite</c>], and returns no results.
    /// </summary>
    [Fact]
    public void EvaluateRefusesAnInvalidConfigBlock()
    {
        var inputs = CrossBuildTables.BuildInputs();
        var invalid = ConfigBlock.Encode(TestConfigs.With(config => config.Movement!.Gravity = float.NaN));
        byte[]? results = null;

        var thrown = Assert.Throws<ConfigValidationException>(() => results = CrossBuildTables.Evaluate(inputs, invalid));

        Assert.Equal(new[] { "movement.gravity: must be finite" }, thrown.Errors.Select(error => error.ToString()));
        Assert.Null(results);
    }

    /// <summary>
    /// Sin, Cos, Tan and SinCos (4,112 rows): ±0, the smallest and largest subnormal, the smallest normal,
    /// π/4 and 12867 each with its two neighbours, ±<c>float.MaxValue</c> (13); 3,072 finite patterns with
    /// |x| ≤ 12867 and 1,024 finite above it; then NaN and ±∞ (3).
    /// </summary>
    private static FunctionRows Radian(byte id, string name) => new(
        id,
        name,
        4_112,
        [
            Exactly(
                "±0, the smallest and largest subnormal and the smallest normal",
                PlusZero,
                MinusZero,
                SmallestSubnormal,
                LargestSubnormal,
                SmallestNormal),
            Exactly("π/4 and its two neighbours", QuarterPi - 1, QuarterPi, QuarterPi + 1),
            Exactly("12867 and its two neighbours", ReductionLimit - 1, ReductionLimit, ReductionLimit + 1),
            Exactly("±float.MaxValue", MaxValue, MinusMaxValue),
            Drawn("3,072 finite bit patterns with |x| ≤ 12867", 3_072, x => float.IsFinite(x) && MathF.Abs(x) <= ReductionLimitValue),
            Drawn("1,024 finite bit patterns with |x| > 12867", 1_024, x => float.IsFinite(x) && MathF.Abs(x) > ReductionLimitValue),
            NaNRow(),
            Exactly("±∞", PlusInfinity, MinusInfinity),
        ]);

    /// <summary>
    /// Asin and Acos (4,115 rows): ±0 and the smallest and largest subnormal, ±½ each with its two
    /// neighbours, ±1 each with its one neighbour inside [−1, 1] (14); 4,096 patterns in [−1, 1]; then NaN,
    /// just outside +1 and −1, and ±2 (5).
    /// </summary>
    private static FunctionRows Inverse(byte id, string name) => new(
        id,
        name,
        4_115,
        [
            Exactly("±0 and the smallest and largest subnormal", PlusZero, MinusZero, SmallestSubnormal, LargestSubnormal),
            Exactly("½ and its two neighbours", Half - 1, Half, Half + 1),
            Exactly("−½ and its two neighbours", MinusHalf - 1, MinusHalf, MinusHalf + 1),
            Exactly("1 and its neighbour inside the domain", One - 1, One),
            Exactly("−1 and its neighbour inside the domain", MinusOne - 1, MinusOne),
            Drawn("4,096 bit patterns in [−1, 1]", 4_096, x => x >= -1f && x <= 1f),
            NaNRow(),
            Exactly("just outside +1 and −1, then ±2", One + 1, MinusOne + 1, Two, MinusTwo),
        ]);

    /// <summary>Single-argument rows with exactly these a bits, in this order, each with b 0.</summary>
    private static RowGroup Exactly(string name, params uint[] a) =>
        new(name, a.Length, (k, actualA, actualB) => actualA == a[k] && actualB == 0u);

    /// <summary>Atan2 rows with exactly these (a, b) bits, in this order.</summary>
    private static RowGroup Pairs(string name, params (uint A, uint B)[] rows) =>
        new(name, rows.Length, (k, a, b) => a == rows[k].A && b == rows[k].B);

    /// <summary>Single-argument drawn rows: each a in the stated range, b 0.</summary>
    private static RowGroup Drawn(string name, int count, Func<float, bool> inRange) =>
        new(name, count, (_, a, b) => b == 0u && inRange(F(a)));

    /// <summary>A single-argument function's NaN row: any NaN a, b 0.</summary>
    private static RowGroup NaNRow() => new("NaN", 1, (_, a, b) => b == 0u && float.IsNaN(F(a)));

    /// <summary>The 16 pairs of {+0, −0, +∞, −∞} for (a, b), a major.</summary>
    private static (uint A, uint B)[] ZeroInfinityCombinations()
    {
        uint[] values = [PlusZero, MinusZero, PlusInfinity, MinusInfinity];
        var pairs = new (uint A, uint B)[values.Length * values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            for (var j = 0; j < values.Length; j++)
            {
                pairs[(i * values.Length) + j] = (values[i], values[j]);
            }
        }

        return pairs;
    }

    /// <summary>Every 64th binary angle and each quadrant boundary's two neighbours, ascending: 1,032 yaws.</summary>
    private static ushort[] PoseYaws()
    {
        var yaws = new SortedSet<int>();
        for (var angle = 0; angle < 65_536; angle += 64)
        {
            yaws.Add(angle);
        }

        foreach (var boundary in new[] { 0x0000, 0x4000, 0x8000, 0xC000 })
        {
            yaws.Add((boundary + 0xFFFF) & 0xFFFF);
            yaws.Add((boundary + 1) & 0xFFFF);
        }

        return yaws.Select(yaw => (ushort)yaw).ToArray();
    }

    /// <summary>The <see cref="SimMath"/> call function <paramref name="function"/> names, on the row's a and b bits.</summary>
    private static bool TryCallDirectly(byte function, uint a, uint b, out uint r0, out uint r1)
    {
        var fa = F(a);
        var fb = F(b);
        float sin;
        float cos;
        (r0, r1) = (0u, 0u);
        switch (function)
        {
            case 1:
                r0 = Bits(SimMath.Sin(fa));
                return true;
            case 2:
                r0 = Bits(SimMath.Cos(fa));
                return true;
            case 3:
                r0 = Bits(SimMath.Tan(fa));
                return true;
            case 4:
                SimMath.SinCos(fa, out sin, out cos);
                (r0, r1) = (Bits(sin), Bits(cos));
                return true;
            case 5:
                r0 = Bits(SimMath.Asin(fa));
                return true;
            case 6:
                r0 = Bits(SimMath.Acos(fa));
                return true;
            case 7:
                r0 = Bits(SimMath.Atan(fa));
                return true;
            case 8:
                r0 = Bits(SimMath.Atan2(fa, fb));
                return true;
            case 9:
                SimMath.SinCosBinaryAngle((ushort)(a & 0xFFFFu), out sin, out cos);
                (r0, r1) = (Bits(sin), Bits(cos));
                return true;
            case 10:
                r0 = Bits(SimMath.Sqrt(fa));
                return true;
            default:
                return false;
        }
    }

    private static string Name(byte function)
    {
        var index = Array.FindIndex(Functions, entry => entry.Id == function);
        return index < 0 ? $"function {function}" : Functions[index].Name;
    }

    private static ReadOnlySpan<byte> MathRow(byte[] inputs, int i) =>
        inputs.AsSpan(HeaderLength + (MathRowLength * i), MathRowLength);

    private static ReadOnlySpan<byte> PoseRow(byte[] inputs, int mathRows, int j) =>
        inputs.AsSpan(HeaderLength + (MathRowLength * mathRows) + (PoseRowLength * j), PoseRowLength);

    /// <summary>Adds <paramref name="line"/> while fewer than 20 are listed.</summary>
    private static void Note(List<string> lines, string line)
    {
        if (lines.Count < 20)
        {
            lines.Add(line);
        }
    }

    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static string R(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Asserts two byte arrays are equal, naming the first byte that differs.</summary>
    private static void AssertSameBytes(byte[] expected, byte[] actual, string what)
    {
        Assert.True(expected.Length > 0, $"{what}: no bytes");
        var common = System.Math.Min(expected.Length, actual.Length);
        var first = expected.AsSpan(0, common).CommonPrefixLength(actual.AsSpan(0, common));
        Assert.True(
            expected.Length == actual.Length && first == common,
            $"{what}: {expected.Length} and {actual.Length} bytes, first difference at byte {first}");
    }

    /// <summary>One function's rows in wasm-api.md's list: its ID, its name, its row count and its rows in the listed order.</summary>
    private sealed record FunctionRows(byte Id, string Name, int RowCount, RowGroup[] Groups);

    /// <summary>
    /// A run of <paramref name="Count"/> consecutive math rows; <paramref name="Accepts"/> takes the row's
    /// index within the run and its a and b bits.
    /// </summary>
    private sealed record RowGroup(string Name, int Count, Func<int, uint, uint, bool> Accepts);
}
