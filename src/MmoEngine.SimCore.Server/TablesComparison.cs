using System.Buffers.Binary;
using System.Globalization;
using MmoEngine.SimCore.Batch;

namespace MmoEngine.SimCore.Server;

/// <summary>How one build's cross-build table results compare with the server's (contracts/wasm-api.md#cross-build-tables).</summary>
public enum TablesStatus
{
    /// <summary>Every result matches the server's.</summary>
    Match = 0,

    /// <summary>The header, the length or a row differs; <see cref="TablesResult.FirstMismatch"/> says where.</summary>
    Diverged = 1,

    /// <summary>The build wrote no results (an error file, or nothing); <see cref="TablesResult.FirstMismatch"/> holds its message.</summary>
    Missing = 2,
}

/// <summary>
/// A build's cross-build tables outcome: the status and, unless it matched, the one line that says why (the
/// first mismatch, or the message of a build that wrote no results).
/// </summary>
/// <param name="Status">Match, Diverged or Missing.</param>
/// <param name="FirstMismatch">The first mismatch's line, or the missing results' message; null for a match.</param>
public sealed record TablesResult(TablesStatus Status, string? FirstMismatch)
{
    /// <summary>A build whose results match the server's.</summary>
    public static TablesResult Matched { get; } = new(TablesStatus.Match, null);

    /// <summary>A build that wrote no results: <c>tables.error.txt</c>'s text, or why there are none.</summary>
    /// <exception cref="ArgumentNullException">The message is null.</exception>
    public static TablesResult Missing(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new TablesResult(TablesStatus.Missing, message);
    }
}

/// <summary>
/// Compares a build's <c>MMXR</c> results of the cross-build tables with the server's (US3 scenario 8, US5
/// scenario 5, SC-004; contracts/wasm-api.md#cross-build-tables, comparing). The headers must be equal. A
/// math row matches when r0 and r1 are bit-identical, or where the server's value is NaN, when the build's
/// is any NaN (research R4: NaN bits are not portable). Pose rows compare bit for bit. The first row that
/// does not match is reported on one line, named from the <c>MMXT</c> inputs.
/// </summary>
public static class TablesComparison
{
    private const int MathCountOffset = 8;
    private const int PoseCountOffset = 12;
    private const int PoseFloats = CrossBuildTables.PoseResultLength / 4;
    private const uint MagnitudeMask = 0x7FFFFFFFu;
    private const uint PlusInfinity = 0x7F800000u;

    /// <summary>Atan2's function ID, the one function with a b argument.</summary>
    private const byte Atan2 = 8;

    /// <summary>The functions by ID (contracts/wasm-api.md#cross-build-tables); index 0 is no function.</summary>
    private static readonly string[] Functions =
    [
        string.Empty,
        "Sin",
        "Cos",
        "Tan",
        "SinCos",
        "Asin",
        "Acos",
        "Atan",
        "Atan2",
        "SinCosBinaryAngle",
        "Sqrt",
    ];

    /// <summary>The 25 floats of a pose result: the head, torso and legs capsules (A, B, radius), then the sphere.</summary>
    private static readonly string[] PoseFields = BuildPoseFields();

    /// <summary>
    /// Compares <paramref name="buildResults"/>, from <paramref name="build"/>, with
    /// <paramref name="serverResults"/>, both evaluations of <paramref name="inputs"/>. Diverged with
    /// <c>tables: build &lt;b&gt;, results header differs from the server's</c> when the 16-byte headers
    /// differ; with <c>tables: build &lt;b&gt;, results are &lt;n&gt; bytes; the server's are &lt;m&gt;</c>
    /// when the headers are equal but the lengths are not; otherwise with the first row that does not match,
    /// <c>tables: build &lt;b&gt;, first mismatch at math row &lt;i&gt;: &lt;function&gt;(a 0x%08X[, b 0x%08X]) r&lt;0|1&gt; server 0x%08X build 0x%08X</c>
    /// or <c>tables: build &lt;b&gt;, first mismatch at pose row &lt;j&gt;: &lt;stance&gt; yaw 0x%04X at (&lt;x&gt;, &lt;y&gt;, &lt;z&gt;) &lt;float&gt; server 0x%08X build 0x%08X</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The build name is null.</exception>
    /// <exception cref="ArgumentException">The inputs and the server's results do not hold the same whole table.</exception>
    public static TablesResult Compare(string build, ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> serverResults, ReadOnlySpan<byte> buildResults)
    {
        ArgumentNullException.ThrowIfNull(build);
        var (mathRows, poseRows) = Counts(inputs, serverResults);
        var header = CrossBuildTables.HeaderLength;
        if (buildResults.Length < header || !buildResults[..header].SequenceEqual(serverResults[..header]))
        {
            return Diverged(string.Concat("tables: build ", build, ", results header differs from the server's"));
        }

        if (buildResults.Length != serverResults.Length)
        {
            return Diverged(string.Create(
                CultureInfo.InvariantCulture,
                $"tables: build {build}, results are {buildResults.Length} bytes; the server's are {serverResults.Length}"));
        }

        for (var i = 0; i < mathRows; i++)
        {
            var offset = header + (CrossBuildTables.MathResultLength * i);
            for (var r = 0; r < 2; r++)
            {
                var server = Bits(serverResults, offset + (4 * r));
                var other = Bits(buildResults, offset + (4 * r));
                if (server != other && !(IsNaN(server) && IsNaN(other)))
                {
                    return Diverged(string.Create(
                        CultureInfo.InvariantCulture,
                        $"tables: build {build}, first mismatch at math row {i}: {MathRow(inputs, i)} r{r} server 0x{server:X8} build 0x{other:X8}"));
                }
            }
        }

        var poseStart = header + (CrossBuildTables.MathResultLength * mathRows);
        for (var j = 0; j < poseRows; j++)
        {
            var offset = poseStart + (CrossBuildTables.PoseResultLength * j);
            for (var k = 0; k < PoseFloats; k++)
            {
                var server = Bits(serverResults, offset + (4 * k));
                var other = Bits(buildResults, offset + (4 * k));
                if (server != other)
                {
                    return Diverged(string.Create(
                        CultureInfo.InvariantCulture,
                        $"tables: build {build}, first mismatch at pose row {j}: {PoseRow(inputs, mathRows, j)} {PoseFields[k]} server 0x{server:X8} build 0x{other:X8}"));
                }
            }
        }

        return TablesResult.Matched;
    }

    /// <summary>The math and pose row counts of the inputs, which the server's results must hold exactly.</summary>
    private static (int MathRows, int PoseRows) Counts(ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> serverResults)
    {
        var header = CrossBuildTables.HeaderLength;
        var mathRows = inputs.Length >= header ? (long)BinaryPrimitives.ReadUInt32LittleEndian(inputs[MathCountOffset..]) : -1;
        var poseRows = inputs.Length >= header ? (long)BinaryPrimitives.ReadUInt32LittleEndian(inputs[PoseCountOffset..]) : -1;
        var inputsLength = header + (CrossBuildTables.MathRowLength * mathRows) + (CrossBuildTables.PoseRowLength * poseRows);
        var resultsLength = header + (CrossBuildTables.MathResultLength * mathRows) + (CrossBuildTables.PoseResultLength * poseRows);
        if (mathRows < 0
            || inputs.Length != inputsLength
            || serverResults.Length != resultsLength
            || BinaryPrimitives.ReadUInt32LittleEndian(serverResults[MathCountOffset..]) != mathRows
            || BinaryPrimitives.ReadUInt32LittleEndian(serverResults[PoseCountOffset..]) != poseRows)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The inputs ({inputs.Length} bytes) and the server's results ({serverResults.Length} bytes) do not hold the same whole table."),
                nameof(serverResults));
        }

        return ((int)mathRows, (int)poseRows);
    }

    /// <summary>A math row as <c>&lt;function&gt;(a 0x%08X)</c>, or <c>Atan2(a 0x%08X, b 0x%08X)</c>.</summary>
    private static string MathRow(ReadOnlySpan<byte> inputs, int i)
    {
        var row = inputs.Slice(CrossBuildTables.HeaderLength + (CrossBuildTables.MathRowLength * i), CrossBuildTables.MathRowLength);
        var id = row[0];
        var name = id is > 0 and <= CrossBuildTables.MaxFunctionId
            ? Functions[id]
            : string.Create(CultureInfo.InvariantCulture, $"function {id}");
        var a = BinaryPrimitives.ReadUInt32LittleEndian(row[4..]);
        return id == Atan2
            ? string.Create(CultureInfo.InvariantCulture, $"{name}(a 0x{a:X8}, b 0x{BinaryPrimitives.ReadUInt32LittleEndian(row[8..]):X8})")
            : string.Create(CultureInfo.InvariantCulture, $"{name}(a 0x{a:X8})");
    }

    /// <summary>A pose row as <c>&lt;Standing|Crouched&gt; yaw 0x%04X at (&lt;x&gt;, &lt;y&gt;, &lt;z&gt;)</c>, in "R" decimals.</summary>
    private static string PoseRow(ReadOnlySpan<byte> inputs, int mathRows, int j)
    {
        var start = CrossBuildTables.HeaderLength + (CrossBuildTables.MathRowLength * mathRows) + (CrossBuildTables.PoseRowLength * j);
        var row = inputs.Slice(start, CrossBuildTables.PoseRowLength);
        var stance = row[0] switch
        {
            0 => "Standing",
            1 => "Crouched",
            var other => string.Create(CultureInfo.InvariantCulture, $"stance {other}"),
        };
        var yaw = BinaryPrimitives.ReadUInt16LittleEndian(row[2..]);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{stance} yaw 0x{yaw:X4} at ({Decimal(row, 4)}, {Decimal(row, 8)}, {Decimal(row, 12)})");
    }

    private static string Decimal(ReadOnlySpan<byte> row, int offset) =>
        BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(row[offset..])).ToString("R", CultureInfo.InvariantCulture);

    private static uint Bits(ReadOnlySpan<byte> results, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(results[offset..]);

    private static bool IsNaN(uint bits) => (bits & MagnitudeMask) > PlusInfinity;

    private static TablesResult Diverged(string line) => new(TablesStatus.Diverged, line);

    private static string[] BuildPoseFields()
    {
        string[] parts = ["head", "torso", "legs"];
        string[] axes = ["x", "y", "z"];
        var fields = new List<string>(PoseFloats);
        foreach (var part in parts)
        {
            foreach (var end in new[] { "A", "B" })
            {
                foreach (var axis in axes)
                {
                    fields.Add(string.Concat(part, " ", end, ".", axis));
                }
            }

            fields.Add(part + " radius");
        }

        foreach (var axis in axes)
        {
            fields.Add("sphere centre." + axis);
        }

        fields.Add("sphere radius");
        return [.. fields];
    }
}
