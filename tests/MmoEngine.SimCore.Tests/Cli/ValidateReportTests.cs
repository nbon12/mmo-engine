using System.Text;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// US7.2's validator half (FR-031, SC-007), contracts/simcore-cli.md#measure-validate-report and
/// contracts/measurement-report.md#tests: <c>simcore measure validate-report &lt;file&gt;</c> prints one
/// <c>path: problem</c> line per problem on stdout, in document order, and nothing when the report is
/// valid. It exits 0 when valid, 1 when invalid (malformed JSON included) and 2 when the file is missing
/// or unreadable. The scenario itself, validating the runner-class report, belongs to Phase 12's
/// <c>measure</c> job. Every test writes its report into its own <see cref="TempDirectories"/> directory
/// and runs the CLI in process; nothing is written under <c>simcore/</c>.
/// </summary>
public sealed class ValidateReportTests
{
    [Fact]
    public void TheContractExampleIsValid()
    {
        using var directory = TempDirectories.Create("validate-report");
        var report = WriteReport(directory, ReportMutations.ReadFixture());

        var (exit, stdout, stderr) = SimcoreCli.Run("measure", "validate-report", report);

        Assert.True(exit == 0, $"validate-report on the contract example exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(string.Empty, stdout);
    }

    /// <summary>One row per mutation that contracts/measurement-report.md#tests lists.</summary>
    [Theory]
    [InlineData("remove machine.cpuModel", "machine.cpuModel: is required")]
    [InlineData("set extra 1", "extra: is not a known field")]
    [InlineData("set runnerClass \"\" ; set server.runnerClass \"\"", "runnerClass: must not be empty")]
    [InlineData("set server.runnerClass \"other-runner\"", "server.runnerClass: is \"other-runner\"; must equal runnerClass \"gha-ubuntu-latest-x64\"")]
    [InlineData("remove server.benchmarks[3]", "server.benchmarks: has no weapon benchmark")]
    [InlineData("remove wasm.modes.aot", "wasm.modes: has no aot mode")]
    [InlineData("remove wasm.modes.aot.reason", "wasm.modes.aot.reason: is required when produced is false")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.download.total.rawBytes 4467851",
        "wasm.modes.interp-prebuilt.download.total.rawBytes: is 4467851; the files sum to 4467850")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.download.total.brotliBytes 1417661",
        "wasm.modes.interp-prebuilt.download.total.brotliBytes: is 1417661; the files sum to 1417660")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.download.total.gzipBytes 1718201",
        "wasm.modes.interp-prebuilt.download.total.gzipBytes: is 1718201; the files sum to 1718200")]
    [InlineData("set wasm.modes.interp-prebuilt.startup.coldStarts 9", "wasm.modes.interp-prebuilt.startup.coldStarts: is 9; must be 10")]
    [InlineData("remove wasm.modes.interp-prebuilt.startup.node.firstStepMs", "wasm.modes.interp-prebuilt.startup.node.firstStepMs: is required")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.startup.chromiumWorker.crossOriginIsolated false",
        "wasm.modes.interp-prebuilt.startup.chromiumWorker.crossOriginIsolated: is false; must be true")]
    [InlineData("remove wasm.modes.interp-prebuilt.steps.node[1]", "wasm.modes.interp-prebuilt.steps.node: has no weapon entry")]
    [InlineData("set wasm.modes.interp-prebuilt.steps.node[0].meanNs 0", "wasm.modes.interp-prebuilt.steps.node[0].meanNs: is 0; must be greater than 0")]
    [InlineData("set wasm.modes.interp-prebuilt.steps.node[0].p99Ns 0", "wasm.modes.interp-prebuilt.steps.node[0].p99Ns: is 0; must be greater than 0")]
    [InlineData(
        "set wasm.modes.interp-relinked.startup.node.totalMs.p90 90.0",
        "wasm.modes.interp-relinked.startup.node.totalMs.p90: is 90.0; must be at least the median 93.6")]
    public void EachProblemIsReported(string mutation, string expectedLine)
    {
        using var directory = TempDirectories.Create("validate-report");
        var report = WriteReport(directory, ReportMutations.Apply(ReportMutations.ReadFixture(), mutation));

        var (exit, stdout, stderr) = SimcoreCli.Run("measure", "validate-report", report);

        Assert.True(exit == 1, $"{mutation}: validate-report exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal($"{expectedLine}\n", stdout);
    }

    /// <summary>An unproduced mode is exactly <c>{"produced": false, "reason": "…"}</c> (FR-031), and that is valid.</summary>
    [Theory]
    [InlineData("aot")]
    [InlineData("interp-relinked")]
    public void AnUnproducedModeNeedsOnlyItsReason(string mode)
    {
        using var directory = TempDirectories.Create("validate-report");
        var mutation = $"set wasm.modes.{mode} {{\"produced\": false, \"reason\": \"not built on this machine\"}}";
        var report = WriteReport(directory, ReportMutations.Apply(ReportMutations.ReadFixture(), mutation));

        var (exit, stdout, stderr) = SimcoreCli.Run("measure", "validate-report", report);

        Assert.True(exit == 0, $"{mode} unproduced: validate-report exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(string.Empty, stdout);
    }

    [Fact]
    public void MalformedJsonIsInvalidAndAMissingFileCannotRun()
    {
        using var directory = TempDirectories.Create("validate-report");
        var malformed = WriteReport(directory, "{\"formatVersion\": 1,");

        var (malformedExit, malformedStdout, malformedStderr) = SimcoreCli.Run("measure", "validate-report", malformed);

        Assert.True(
            malformedExit == 1,
            $"validate-report on malformed JSON exited {malformedExit}, expected 1; stdout: {malformedStdout}; stderr: {malformedStderr}");
        Assert.EndsWith("\n", malformedStdout, StringComparison.Ordinal);
        var line = Assert.Single(malformedStdout[..^1].Split('\n'));
        Assert.StartsWith("$: is not valid JSON (", line, StringComparison.Ordinal);
        Assert.EndsWith(")", line, StringComparison.Ordinal);

        using var empty = TempDirectories.Create("validate-report-missing");
        var missing = empty.Combine("measurement-report.json");

        var (missingExit, missingStdout, missingStderr) = SimcoreCli.Run("measure", "validate-report", missing);

        Assert.True(
            missingExit == 2,
            $"validate-report on a missing file exited {missingExit}, expected 2; stdout: {missingStdout}; stderr: {missingStderr}");
        Assert.Equal(string.Empty, missingStdout);
        Assert.Contains(missing, missingStderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// Check 1: the file is UTF-8 JSON. Bytes that are not UTF-8 make it invalid, even inside a string that
    /// no other check reads closely, where a lossy decoder would turn them into U+FFFD and the report would
    /// pass. Each row writes the fixture's bytes with one change, and expects exit 1 and exactly one line
    /// that starts with <c>$: is not valid JSON (</c> and ends with <c>)</c>:
    /// <list type="bullet">
    ///   <item><c>invalid-byte</c>: the <c>E</c> of <c>EPYC</c> in <c>machine.cpuModel</c> replaced by
    ///   0xFF, which no UTF-8 sequence holds;</item>
    ///   <item><c>byte-order-mark-then-invalid-byte</c>: the same after a UTF-8 byte-order mark, which is
    ///   skipped, so the bytes after it are decoded just as strictly;</item>
    ///   <item><c>utf-16</c>: the fixture as UTF-16 little-endian with its byte-order mark.</item>
    /// </list>
    /// The decoder's message is the runtime's, so the rest of the line is not pinned.
    /// </summary>
    [Theory]
    [InlineData("invalid-byte")]
    [InlineData("byte-order-mark-then-invalid-byte")]
    [InlineData("utf-16")]
    public void BytesThatAreNotUtf8AreNotValidJson(string variant)
    {
        using var directory = TempDirectories.Create("validate-report");
        var fixture = ReportMutations.ReadFixture();
        var bytes = variant switch
        {
            "invalid-byte" => WithInvalidByte(fixture),
            "byte-order-mark-then-invalid-byte" => [.. Encoding.UTF8.GetPreamble(), .. WithInvalidByte(fixture)],
            "utf-16" => [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(fixture)],
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "not a row of this test"),
        };
        var report = WriteReportBytes(directory, bytes);

        var (exit, stdout, stderr) = SimcoreCli.Run("measure", "validate-report", report);

        Assert.True(exit == 1, $"{variant}: validate-report exited {exit}, expected 1; stdout: {stdout}; stderr: {stderr}");
        Assert.EndsWith("\n", stdout, StringComparison.Ordinal);
        var line = Assert.Single(stdout[..^1].Split('\n'));
        Assert.StartsWith("$: is not valid JSON (", line, StringComparison.Ordinal);
        Assert.EndsWith(")", line, StringComparison.Ordinal);
    }

    /// <summary>A UTF-8 byte-order mark before the contract's example is skipped: exit 0 and nothing on stdout.</summary>
    [Fact]
    public void AUtf8ByteOrderMarkIsSkipped()
    {
        using var directory = TempDirectories.Create("validate-report");
        var report = WriteReportBytes(directory, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(ReportMutations.ReadFixture())]);

        var (exit, stdout, stderr) = SimcoreCli.Run("measure", "validate-report", report);

        Assert.True(exit == 0, $"validate-report on the example after a byte-order mark exited {exit}, expected 0; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(string.Empty, stdout);
    }

    /// <summary>
    /// The fixture's UTF-8 bytes with the <c>E</c> of <c>EPYC</c>, inside <c>machine.cpuModel</c>, replaced
    /// by 0xFF.
    /// </summary>
    private static byte[] WithInvalidByte(string fixture)
    {
        const string Marker = "EPYC";
        var occurrences = fixture.Split(Marker).Length - 1;
        Assert.True(occurrences == 1, $"The fixture holds {Marker} {occurrences} times; the row needs exactly one");
        var bytes = Encoding.UTF8.GetBytes(fixture);
        var index = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Marker));
        bytes[index] = 0xFF;
        return bytes;
    }

    /// <summary>Writes <paramref name="bytes"/> as <c>measurement-report.json</c> in <paramref name="directory"/>.</summary>
    /// <returns>The report's absolute path.</returns>
    private static string WriteReportBytes(TempDirectory directory, byte[] bytes)
    {
        var path = directory.Combine("measurement-report.json");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Writes <paramref name="text"/> as <c>measurement-report.json</c> in <paramref name="directory"/>.</summary>
    /// <returns>The report's absolute path.</returns>
    private static string WriteReport(TempDirectory directory, string text)
    {
        var path = directory.Combine("measurement-report.json");
        File.WriteAllText(path, text);
        return path;
    }
}
