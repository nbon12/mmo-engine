using System.Buffers.Binary;
using System.Globalization;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Agreement;

/// <summary>
/// <c>TablesComparison.Compare</c>, the server's comparison of a build's cross-build table results with its
/// own (contracts/wasm-api.md#cross-build-tables, comparing): the headers must be equal; a math row matches
/// when r0 and r1 are bit-identical, or where the server's value is NaN, when the build's value is any NaN
/// (research R4: NaN bits are not portable); pose rows compare bit for bit; and the first row that does not
/// match is reported on one line. Every test starts from <c>inputs = CrossBuildTables.BuildInputs()</c> and
/// <c>server = CrossBuildTables.Evaluate(inputs, the config block of default.json)</c>, computed once and
/// never written to, copies the server results as the build's results, and changes the copy at the
/// contract's offsets: math result i at 16 + 8·i (r0, then r1), and pose result float k of row j at
/// 16 + 8·M + 100·j + 4·k.
/// </summary>
public sealed class TablesComparisonTests
{
    private const string BuildName = "node-interp";

    private const int HeaderLength = 16;
    private const int MathRowLength = 12;
    private const int PoseRowLength = 16;
    private const int MathResultLength = 8;
    private const int PoseResultLength = 100;

    /// <summary>The results header's <c>u32</c> math row count M.</summary>
    private const int MathRowCountOffset = 8;

    /// <summary>Pose result float 7: the torso capsule's A.x.</summary>
    private const int TorsoAX = 7;

    /// <summary>Pose result float 24: the bounding sphere's radius.</summary>
    private const int SphereRadius = 24;

    private const byte Sin = 1;
    private const byte SinCos = 4;
    private const byte Atan2 = 8;
    private const byte Crouched = 1;

    private const uint PlusZero = 0x00000000u;
    private const uint MinusOne = 0xBF800000u;
    private const uint QuarterPi = 0x3F490FDBu;
    private const uint QuietNaN = 0x7FC00000u;

    private static readonly Lazy<ServerTables> Tables = new(() =>
    {
        var inputs = CrossBuildTables.BuildInputs();
        return new ServerTables(inputs, CrossBuildTables.Evaluate(inputs, ConfigBlock.Encode(TestConfigs.Default())));
    });

    /// <summary>
    /// Rows <c>(caseName, expectedLine)</c>. The line is the exact <c>FirstMismatch</c>, with <c>{row}</c>
    /// replaced by the changed row's index, <c>{server}</c> and <c>{build}</c> by its server and build bits
    /// as eight uppercase hex digits, and <c>{buildLength}</c> and <c>{serverLength}</c> by the two results'
    /// lengths in bytes.
    /// </summary>
    public static TheoryData<string, string> Cases => new()
    {
        {
            "atan2-axis-r0",
            "tables: build node-interp, first mismatch at math row {row}: Atan2(a 0x00000000, b 0xBF800000) r0 server 0x40490FDB build 0x40490FDA"
        },
        {
            "sincos-r1",
            "tables: build node-interp, first mismatch at math row {row}: SinCos(a 0x3F490FDB) r1 server 0x{server} build 0x{build}"
        },
        {
            "pose-torso-a-x",
            "tables: build node-interp, first mismatch at pose row {row}: Crouched yaw 0x4000 at (4096.25, 37.5, -7000.125) torso A.x server 0x{server} build 0x{build}"
        },
        {
            "pose-sphere-radius",
            "tables: build node-interp, first mismatch at pose row {row}: Crouched yaw 0xFFFF at (-8191.5, -200, 8191.5) sphere radius server 0x{server} build 0x{build}"
        },
        {
            "finite-against-nan",
            "tables: build node-interp, first mismatch at math row {row}: Sin(a 0x00000000) r0 server 0x{server} build 0x7FC00000"
        },
        {
            "results-header",
            "tables: build node-interp, results header differs from the server's"
        },
        {
            "results-length",
            "tables: build node-interp, results are {buildLength} bytes; the server's are {serverLength}"
        },
        {
            "two-rows",
            "tables: build node-interp, first mismatch at math row {row}: Atan2(a 0x00000000, b 0xBF800000) r0 server 0x40490FDB build 0x40490FDA"
        },
    };

    /// <summary>
    /// Given the build's results equal to the server's, Then <c>Compare("node-interp", inputs, server,
    /// build)</c> is <c>Match</c> with a null <c>FirstMismatch</c>. When the copy is changed per row, Then it
    /// is <c>Diverged</c> and <c>FirstMismatch</c> is exactly the row's line:
    /// <c>atan2-axis-r0</c>, the low bit of r0 flipped in the first Atan2 row with a = 0x00000000 and
    /// b = 0xBF800000 (the (+0, −1) axis direction; T016 pins Atan2(+0, −1) to 0x40490FDB, so the build has
    /// 0x40490FDA); <c>sincos-r1</c>, the low bit of r1 flipped in the SinCos row with a = π/4 (0x3F490FDB);
    /// <c>pose-torso-a-x</c>, torso A.x one ulp up (bits + 1) in the Crouched, yaw 0x4000,
    /// (4096.25, 37.5, −7000.125) row; <c>pose-sphere-radius</c>, the low bit of the sphere radius flipped in
    /// the Crouched, yaw 0xFFFF, (−8191.5, −200, 8191.5) row; <c>finite-against-nan</c>, the finite r0 of the
    /// Sin row with a = +0 against the build's NaN 0x7FC00000; <c>results-header</c>, the build's header
    /// with M − 1; <c>results-length</c>, the build's results one byte short with the header unchanged,
    /// reported with both lengths; and <c>two-rows</c>, the torso A.x change of <c>pose-torso-a-x</c> together with the
    /// Atan2 change of <c>atan2-axis-r0</c>, of which only the first row, the Atan2 one, is reported.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void ReportsTheFirstDifferingRow(string caseName, string expectedLine)
    {
        var tables = Tables.Value;
        var inputs = tables.Inputs;
        var server = tables.Results;
        var build = (byte[])server.Clone();

        var identical = TablesComparison.Compare(BuildName, inputs, server, build);
        Assert.True(
            identical.Status == TablesStatus.Match,
            $"{caseName}: identical results gave {identical.Status}, not Match: {identical.FirstMismatch}");
        Assert.Null(identical.FirstMismatch);

        var row = -1;
        var serverBits = 0u;
        var buildBits = 0u;
        switch (caseName)
        {
            case "atan2-axis-r0":
                row = MathRowOf(tables, Atan2, PlusZero, MinusOne);
                (serverBits, buildBits) = Change(server, build, MathResult(row, 0), bits => bits ^ 1u);
                break;
            case "sincos-r1":
                row = MathRowOf(tables, SinCos, QuarterPi, 0u);
                (serverBits, buildBits) = Change(server, build, MathResult(row, 1), bits => bits ^ 1u);
                break;
            case "pose-torso-a-x":
                row = PoseRowOf(tables, Crouched, 0x4000, 4096.25f, 37.5f, -7000.125f);
                (serverBits, buildBits) = Change(server, build, PoseResult(tables, row, TorsoAX), bits => bits + 1u);
                break;
            case "pose-sphere-radius":
                row = PoseRowOf(tables, Crouched, 0xFFFF, -8191.5f, -200f, 8191.5f);
                (serverBits, buildBits) = Change(server, build, PoseResult(tables, row, SphereRadius), bits => bits ^ 1u);
                break;
            case "finite-against-nan":
                row = MathRowOf(tables, Sin, PlusZero, 0u);
                (serverBits, buildBits) = Change(server, build, MathResult(row, 0), _ => QuietNaN);
                Assert.True(float.IsFinite(BitConverter.UInt32BitsToSingle(serverBits)), $"the server's Sin(+0) r0 0x{serverBits:X8} is not finite");
                break;
            case "results-header":
                BinaryPrimitives.WriteUInt32LittleEndian(build.AsSpan(MathRowCountOffset), (uint)(tables.MathRows - 1));
                break;
            case "results-length":
                build = build[..^1];
                break;
            case "two-rows":
                var poseRow = PoseRowOf(tables, Crouched, 0x4000, 4096.25f, 37.5f, -7000.125f);
                _ = Change(server, build, PoseResult(tables, poseRow, TorsoAX), bits => bits + 1u);
                row = MathRowOf(tables, Atan2, PlusZero, MinusOne);
                (serverBits, buildBits) = Change(server, build, MathResult(row, 0), bits => bits ^ 1u);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of ReportsTheFirstDifferingRow.");
        }

        var line = expectedLine
            .Replace("{row}", row.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{server}", serverBits.ToString("X8", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{build}", buildBits.ToString("X8", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{buildLength}", build.Length.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{serverLength}", server.Length.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.DoesNotContain("{", line, StringComparison.Ordinal);

        var result = TablesComparison.Compare(BuildName, inputs, server, build);

        Assert.True(result.Status == TablesStatus.Diverged, $"{caseName}: Compare gave {result.Status}, not Diverged");
        Assert.Equal(line, result.FirstMismatch);
    }

    /// <summary>
    /// Research R4: NaN bits are not portable, so where the server's value is NaN any NaN in the build
    /// matches. Given the first math row whose server r0 is NaN, with the server's r0 set to the quiet NaN
    /// 0x7FC00000, When the build's r0 is <paramref name="buildBits"/> (a negative NaN with a payload, or a
    /// signalling NaN), Then <c>Compare</c> is <c>Match</c> with a null <c>FirstMismatch</c>; and a finite
    /// build value (0x00000000) in the same place is not a NaN, so it is <c>Diverged</c>, reported at that row
    /// as <c>r0 server 0x7FC00000 build 0x00000000</c>.
    /// </summary>
    [Theory]
    [InlineData(0xFFC00001u)]
    [InlineData(0x7F800001u)]
    public void AnyNaNMatchesANaNRow(uint buildBits)
    {
        var tables = Tables.Value;
        var row = -1;
        for (var i = 0; i < tables.MathRows; i++)
        {
            if (float.IsNaN(BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(tables.Results.AsSpan(MathResult(i, 0))))))
            {
                row = i;
                break;
            }
        }

        Assert.True(row >= 0, "no math row of the server's results has a NaN r0");
        var server = (byte[])tables.Results.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(server.AsSpan(MathResult(row, 0)), QuietNaN);
        var build = (byte[])server.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(build.AsSpan(MathResult(row, 0)), buildBits);

        var result = TablesComparison.Compare(BuildName, tables.Inputs, server, build);

        Assert.True(
            result.Status == TablesStatus.Match,
            $"server 0x7FC00000 against build 0x{buildBits:X8} at math row {row} gave {result.Status}, not Match: {result.FirstMismatch}");
        Assert.Null(result.FirstMismatch);

        var finite = (byte[])server.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(finite.AsSpan(MathResult(row, 0)), PlusZero);
        var diverged = TablesComparison.Compare(BuildName, tables.Inputs, server, finite);
        Assert.True(diverged.Status == TablesStatus.Diverged, $"server 0x7FC00000 against build 0x00000000 at math row {row} gave {diverged.Status}, not Diverged");
        Assert.NotNull(diverged.FirstMismatch);
        Assert.StartsWith($"tables: build node-interp, first mismatch at math row {row}: ", diverged.FirstMismatch, StringComparison.Ordinal);
        Assert.EndsWith(" r0 server 0x7FC00000 build 0x00000000", diverged.FirstMismatch, StringComparison.Ordinal);
    }

    /// <summary>The first math row with <paramref name="function"/>, a bits <paramref name="a"/> and b bits <paramref name="b"/>.</summary>
    private static int MathRowOf(ServerTables tables, byte function, uint a, uint b)
    {
        for (var i = 0; i < tables.MathRows; i++)
        {
            var row = tables.Inputs.AsSpan(HeaderLength + (MathRowLength * i), MathRowLength);
            if (row[0] == function && BinaryPrimitives.ReadUInt32LittleEndian(row[4..]) == a && BinaryPrimitives.ReadUInt32LittleEndian(row[8..]) == b)
            {
                return i;
            }
        }

        Assert.Fail($"BuildInputs() has no math row with function {function}, a 0x{a:X8} and b 0x{b:X8}");
        return -1;
    }

    /// <summary>The first pose row with <paramref name="stance"/>, <paramref name="yaw"/> and the position's bits.</summary>
    private static int PoseRowOf(ServerTables tables, byte stance, ushort yaw, float x, float y, float z)
    {
        for (var j = 0; j < tables.PoseRows; j++)
        {
            var row = tables.Inputs.AsSpan(HeaderLength + (MathRowLength * tables.MathRows) + (PoseRowLength * j), PoseRowLength);
            if (row[0] == stance
                && BinaryPrimitives.ReadUInt16LittleEndian(row[2..]) == yaw
                && BinaryPrimitives.ReadUInt32LittleEndian(row[4..]) == BitConverter.SingleToUInt32Bits(x)
                && BinaryPrimitives.ReadUInt32LittleEndian(row[8..]) == BitConverter.SingleToUInt32Bits(y)
                && BinaryPrimitives.ReadUInt32LittleEndian(row[12..]) == BitConverter.SingleToUInt32Bits(z))
            {
                return j;
            }
        }

        Assert.Fail($"BuildInputs() has no pose row with stance {stance}, yaw 0x{yaw:X4} and position ({x}, {y}, {z})");
        return -1;
    }

    /// <summary>The offset of math result <paramref name="i"/>'s r0 (<paramref name="r"/> 0) or r1 (1).</summary>
    private static int MathResult(int i, int r) => HeaderLength + (MathResultLength * i) + (4 * r);

    /// <summary>The offset of float <paramref name="k"/> of pose result <paramref name="j"/>.</summary>
    private static int PoseResult(ServerTables tables, int j, int k) =>
        HeaderLength + (MathResultLength * tables.MathRows) + (PoseResultLength * j) + (4 * k);

    /// <summary>Writes <paramref name="change"/> of the server's bits at <paramref name="offset"/> into the build's copy, and returns both.</summary>
    private static (uint Server, uint Build) Change(byte[] server, byte[] build, int offset, Func<uint, uint> change)
    {
        var serverBits = BinaryPrimitives.ReadUInt32LittleEndian(server.AsSpan(offset));
        var buildBits = change(serverBits);
        Assert.True(buildBits != serverBits, $"the change at offset {offset} leaves 0x{serverBits:X8} as it is");
        BinaryPrimitives.WriteUInt32LittleEndian(build.AsSpan(offset), buildBits);
        return (serverBits, buildBits);
    }

    /// <summary>The server's inputs and results, shared read-only by every test of the class.</summary>
    private sealed record ServerTables(byte[] Inputs, byte[] Results)
    {
        /// <summary>The inputs header's M.</summary>
        public int MathRows => (int)BinaryPrimitives.ReadUInt32LittleEndian(Inputs.AsSpan(8));

        /// <summary>The inputs header's P.</summary>
        public int PoseRows => (int)BinaryPrimitives.ReadUInt32LittleEndian(Inputs.AsSpan(12));
    }
}
