using System.Text.Json;
using MmoEngine.SimCore.Cli.Measure;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// contracts/measurement-report.md#what-validate-report-checks, called directly: every check that
/// <see cref="ValidateReportTests"/> does not run through the CLI, each on one mutation of the contract's
/// example report (<see cref="ReportMutations"/>), so that <c>MeasurementReportValidator</c> reaches 95%
/// line coverage. Each problem is one <c>path: problem</c> line, in document order; a valid report has
/// none. The checks are structural: there is no threshold on any number (Principle VI).
/// </summary>
public sealed class MeasurementReportValidatorTests
{
    [Theory]
    [InlineData("set formatVersion 2", "formatVersion: is 2; must be 1")]
    [InlineData("set formatVersion \"1\"", "formatVersion: must be an integer")]
    [InlineData("set kind \"x\"", "kind: is \"x\"; must be \"simcore-measurement\"")]
    [InlineData("set label \"estimate\"", "label: is \"estimate\"; must be \"measurement\"")]
    [InlineData("set recordedAt \"2026-11-14 10:00:00\"", "recordedAt: is \"2026-11-14 10:00:00\"; must match YYYY-MM-DDTHH:MM:SSZ")]
    [InlineData("set commit \"ABC\"", "commit: is \"ABC\"; must match 40 lowercase hex digits or \"unknown\"")]
    [InlineData("set machine.logicalCores 0", "machine.logicalCores: is 0; must be at least 1")]
    [InlineData("set machine.architecture \"x86\"", "machine.architecture: is \"x86\"; must be \"x64\" or \"arm64\"")]
    [InlineData("set dotnet.sdkVersion \"10\"", "dotnet.sdkVersion: is \"10\"; must match <major>.<minor>.<patch>")]
    [InlineData("remove dotnet.environment.DOTNET_TieredPGO", "dotnet.environment.DOTNET_TieredPGO: is required")]
    [InlineData("set node.version \"\"", "node.version: must not be empty")]
    [InlineData("set server.tool \"Benchmark\"", "server.tool: is \"Benchmark\"; must match BenchmarkDotNet <version>")]
    [InlineData("set server.label \"estimate\"", "server.label: is \"estimate\"; must be \"measurement\"")]
    [InlineData(
        "set server.benchmarks[0].step \"physics\"",
        "server.benchmarks[0].step: is \"physics\"; must be movement, weapon, projectile, hitboxBuild or stateHash")]
    [InlineData("set server.benchmarks[1].case \"flat-run\"", "server.benchmarks[1].case: \"flat-run\" is not unique within movement")]
    [InlineData("set server.benchmarks[0].meanNs 0", "server.benchmarks[0].meanNs: is 0; must be greater than 0")]
    [InlineData("set server.benchmarks[0].iterations 0", "server.benchmarks[0].iterations: is 0; must be at least 1")]
    [InlineData("set server.benchmarks[0].allocatedBytesPerOp -1", "server.benchmarks[0].allocatedBytesPerOp: is -1; must be at least 0")]
    [InlineData("set wasm.modes.jit {\"produced\": false, \"reason\": \"x\"}", "wasm.modes.jit: is not a known mode")]
    [InlineData("set wasm.modes.aot.reason \"\"", "wasm.modes.aot.reason: must not be empty")]
    [InlineData(
        "set wasm.modes.aot.build {\"aot\": true, \"relinked\": true, \"workload\": true}",
        "wasm.modes.aot.build: is not a known field")]
    [InlineData("set wasm.modes.interp-prebuilt.reason \"x\"", "wasm.modes.interp-prebuilt.reason: must be null when produced is true")]
    [InlineData("set wasm.modes.interp-prebuilt.build.relinked true", "wasm.modes.interp-prebuilt.build.relinked: is true; must be false")]
    [InlineData("set wasm.modes.interp-prebuilt.runtime.debugLevel 1", "wasm.modes.interp-prebuilt.runtime.debugLevel: is 1; must be 0")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.download.files[1].group \"loader\"",
        "wasm.modes.interp-prebuilt.download.files: has 2 loader entries; must have exactly one, named dotnet.js")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.download.files[8].name \"MmoEngine.SimCore.a0eeztrxtm.wasm\"",
        "wasm.modes.interp-prebuilt.download.files[8].name: \"MmoEngine.SimCore.a0eeztrxtm.wasm\" is not unique")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.download.files[6].group \"fonts\"",
        "wasm.modes.interp-prebuilt.download.files[6].group: is \"fonts\"; must be loader, jsModuleNative, jsModuleRuntime, wasmNative, coreAssembly, assembly or icu")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.download.total.files 8",
        "wasm.modes.interp-prebuilt.download.total.files: is 8; there are 9 files")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.startup.node.importMs.median 0",
        "wasm.modes.interp-prebuilt.startup.node.importMs.median: is 0; must be greater than 0")]
    [InlineData("set wasm.modes.interp-prebuilt.steps.node[0].batch 2", "wasm.modes.interp-prebuilt.steps.node[0].batch: is 2; must be 1")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.steps.chromiumWorker[0].steps 2999",
        "wasm.modes.interp-prebuilt.steps.chromiumWorker[0].steps: is 2999; must be at least 3000")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.steps.node[0].scenario \"rifle-burst\"",
        "wasm.modes.interp-prebuilt.steps.node[0].scenario: is \"rifle-burst\"; must be \"flat-run-30hz\" for movement")]
    [InlineData(
        "set wasm.modes.interp-prebuilt.agreement {\"build\": \"node-interp\", \"status\": \"Match\"}",
        "wasm.modes.interp-prebuilt.agreement: must be null for interp-prebuilt")]
    [InlineData(
        "set wasm.modes.interp-relinked.agreement null",
        "wasm.modes.interp-relinked.agreement: is required when the mode was produced")]
    [InlineData(
        "set wasm.modes.interp-relinked.agreement.build \"node-interp\"",
        "wasm.modes.interp-relinked.agreement.build: is \"node-interp\"; must be \"node-interp-relinked\"")]
    [InlineData(
        "set wasm.modes.interp-relinked.agreement.status \"Ok\"",
        "wasm.modes.interp-relinked.agreement.status: is \"Ok\"; must be Match, Diverged or NotRun")]
    [InlineData(
        "set wasm.modes.interp-prebuilt {\"produced\": false, \"reason\": \"x\"} ; set wasm.modes.interp-relinked {\"produced\": false, \"reason\": \"y\"}",
        "wasm.modes: no interpreted mode was produced")]
    public void EveryOtherCheckReportsItsLine(string mutation, string expectedLine)
    {
        var report = ReportMutations.Apply(ReportMutations.ReadFixture(), mutation);

        var problems = MeasurementReportValidator.Validate(report);

        Assert.Equal([expectedLine], problems);
    }

    [Fact]
    public void ARootThatIsNotAnObjectIsReported()
    {
        var problems = MeasurementReportValidator.Validate("[]");

        Assert.Equal(["$: must be an object"], problems);
    }

    /// <summary><c>commit</c> may be <c>unknown</c>, and an environment variable may be recorded as <c>unset</c>.</summary>
    [Fact]
    public void UnknownCommitAndUnsetEnvironmentAreValid()
    {
        var report = ReportMutations.Apply(
            ReportMutations.ReadFixture(),
            "set commit \"unknown\" ; set dotnet.environment.DOTNET_TieredPGO \"unset\"");

        var problems = MeasurementReportValidator.Validate(report);

        Assert.Empty(problems);
    }

    /// <summary>The problems follow the report's member order, not the order the mutation made them in.</summary>
    [Fact]
    public void ProblemsComeInDocumentOrder()
    {
        var report = ReportMutations.Apply(
            ReportMutations.ReadFixture(),
            "set wasm.modes.interp-prebuilt.startup.coldStarts 9 ; set machine.logicalCores 0");

        var problems = MeasurementReportValidator.Validate(report);

        Assert.Equal(
            ["machine.logicalCores: is 0; must be at least 1", "wasm.modes.interp-prebuilt.startup.coldStarts: is 9; must be 10"],
            problems);
    }

    /// <summary>
    /// The benchmark exporter's <c>server</c> object on its own (contracts/measurement-report.md check 5 and
    /// every <c>server</c> field rule), checked against the runner class the caller gives, as
    /// <c>ServerBenchmarkExportTests.DryRunExportsAValidServerObject</c> uses it.
    /// </summary>
    [Fact]
    public void ServerObjectAloneIsCheckedAgainstTheGivenRunnerClass()
    {
        using var document = JsonDocument.Parse(ReportMutations.ReadFixture());
        var server = document.RootElement.GetProperty("server");

        var matching = MeasurementReportValidator.ValidateServerObject(server, "gha-ubuntu-latest-x64");
        var other = MeasurementReportValidator.ValidateServerObject(server, "other-runner");

        Assert.Empty(matching);
        Assert.Equal(["server.runnerClass: is \"gha-ubuntu-latest-x64\"; must equal runnerClass \"other-runner\""], other);
    }
}
