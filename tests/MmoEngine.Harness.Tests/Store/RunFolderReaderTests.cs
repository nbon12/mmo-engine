using System.Text.Json.Nodes;
using MmoEngine.Harness.Loading;
using MmoEngine.Harness.Store;
using MmoEngine.Harness.Tests.Support;

namespace MmoEngine.Harness.Tests.Store;

/// <summary>Reading run folders without a store: refusals, CSV parsing and the runner class.</summary>
public sealed class RunFolderReaderTests
{
    [Theory]
    [InlineData("a,b,c", new[] { "a", "b", "c" })]
    [InlineData("a,,c", new[] { "a", "", "c" })]
    [InlineData("a,\"b,c\",d", new[] { "a", "b,c", "d" })]
    [InlineData("\"say \"\"hi\"\"\",x", new[] { "say \"hi\"", "x" })]
    [InlineData("", new[] { "" })]
    public void CsvLinesSplitHonouringQuotes(string line, string[] expected) =>
        Assert.Equal(expected, RunFolderReader.SplitCsv(line));

    /// <summary>A malformed file is refused naming the file, line and column, before anything is loaded.</summary>
    [Theory]
    [InlineData("ticks.csv", "regionCount", "many")]
    [InlineData("ticks.csv", "crossPairsDeferred", "-3")]
    public void InvalidValueIsRefusedNamingFileLineAndColumn(string file, string column, string value)
    {
        var (folder, _) = RunFolders.CleanRun();
        var path = Path.Combine(folder, file);
        var lines = File.ReadAllLines(path);
        var header = lines[0].Split(',');
        var fields = lines[1].Split(',');
        fields[Array.IndexOf(header, column)] = value;
        lines[1] = string.Join(',', fields);
        File.WriteAllLines(path, lines);
        var reader = RunFolderReader.Open(folder);

        var error = Assert.Throws<RunFolderException>(() => reader.TickRows().ToList());

        Assert.Contains($"{file} line 2", error.Message, StringComparison.Ordinal);
        Assert.Contains(column, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("ticks.csv")]
    [InlineData("events.csv")]
    public void MissingFileIsRefused(string file)
    {
        var (folder, _) = RunFolders.CleanRun();
        File.Delete(Path.Combine(folder, file));

        var error = Assert.Throws<RunFolderException>(() => RunFolderReader.Open(folder));

        Assert.Contains(file, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFolderAndBadJsonAreRefused()
    {
        Assert.Throws<RunFolderException>(() => RunFolderReader.Open(Path.Combine(RunFolders.TempFolder(), "nope")));
        var (folder, _) = RunFolders.CleanRun();
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{ not json");
        Assert.Contains("not valid JSON", Assert.Throws<RunFolderException>(() => RunFolderReader.Open(folder)).Message, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), "{\"formatVersion\":1}");
        Assert.Contains("runId", Assert.Throws<RunFolderException>(() => RunFolderReader.Open(folder)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnMissingFromHeaderOrRowOfWrongWidthIsRefused()
    {
        var (folder, _) = RunFolders.CleanRun();
        var path = Path.Combine(folder, "events.csv");
        var lines = File.ReadAllLines(path).ToList();
        lines.Add("1,2,Merge");
        File.WriteAllLines(path, lines);
        Assert.Contains("fields, header has", Assert.Throws<RunFolderException>(() => RunFolderReader.Open(folder).EventRows().ToList()).Message, StringComparison.Ordinal);

        File.WriteAllText(path, "tick,timeSteps\n");
        Assert.Contains("no column 'type'", Assert.Throws<RunFolderException>(() => RunFolderReader.Open(folder).EventRows().ToList()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AggregateRowsComeFromSummaryMetrics()
    {
        var (folder, runId) = RunFolders.CleanRun();
        var reader = RunFolderReader.Open(folder);

        var rows = reader.AggregateRows().ToList();

        Assert.Contains(rows, r => (string)r[1]! == "regionCount");
        Assert.All(rows, r => Assert.Equal(runId, r[0]));
    }

    [Fact]
    public void RunRowCarriesManifestIdentityAndSweepKind()
    {
        var (folder, runId) = RunFolders.CleanRun();
        var reader = RunFolderReader.Open(folder);

        var plain = reader.ToRunRow("class-a");
        var cell = reader.ToRunRow("class-a", "sweep-x", "c001");

        Assert.Equal(runId, plain.RunId);
        Assert.Equal("simulator", plain.Kind);
        Assert.Equal("Interleaved", plain.Scheduling);
        Assert.Equal("none", plain.Profile);
        Assert.Equal(Verdicts.Pending, plain.Verdict);
        Assert.Equal("sweep-cell", cell.Kind);
        Assert.Equal("c001", cell.CellId);
    }

    [Fact]
    public void SweepIndexIsRefusedWithoutSweepJsonOrWithAnotherVersion()
    {
        Assert.Throws<RunFolderException>(() => SweepIndex.Open(RunFolders.TempFolder()));
        var (folder, _) = RunFolders.Sweep();
        var path = Path.Combine(folder, "sweep.json");
        var index = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        index["formatVersion"] = 3;
        File.WriteAllText(path, index.ToJsonString());
        Assert.Contains("formatVersion 3", Assert.Throws<RunFolderException>(() => SweepIndex.Open(folder)).Message, StringComparison.Ordinal);
        File.WriteAllText(path, "[");
        Assert.Contains("not valid JSON", Assert.Throws<RunFolderException>(() => SweepIndex.Open(folder)).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The committed first-pass and full-scale results load their cells (the decision memo's evidence, FR-007):
    /// one folder per sweep spec, every run with its cells.csv row.
    /// </summary>
    [Theory]
    [InlineData("first-pass")]
    [InlineData("full")]
    public void CommittedSweepResultsParse(string scale)
    {
        var repo = Rules.RuleFileParserTests.RepoRoot();
        var root = Path.Combine(repo, "docs", "simulator", "results", scale);
        if (scale == "full")
        {
            var specs = Directory.EnumerateFiles(Path.Combine(repo, "simulator", "sweeps"), "*.json")
                .Select(Path.GetFileNameWithoutExtension).Where(n => n != "memo-rules").Order(StringComparer.Ordinal);
            Assert.Equal(specs, Directory.EnumerateDirectories(root).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }

        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var index = SweepIndex.Open(folder);
            var rows = index.CellRows().ToList();
            Assert.Equal(index.Runs.Count, rows.Count);
            Assert.All(rows, r => Assert.Equal(SweepIndex.CellTableColumns.Count, r.Length));
        }
    }
}

/// <summary>US3 scenario 4 and research R5: the runner class fingerprint.</summary>
public sealed class RunnerClassTests
{
    private static JsonObject Manifest(string cpu, string avx2, string name = "") => new()
    {
        ["runtime"] = new JsonObject
        {
            ["framework"] = ".NET 10.0.14",
            ["architecture"] = "X64",
            ["cpuModel"] = cpu,
            ["runnerClass"] = name,
            ["os"] = "any kernel",
            ["hardwareIntrinsics"] = new JsonObject { ["DOTNET_EnableAVX2"] = avx2, ["Avx2.IsSupported"] = true },
        },
    };

    [Fact]
    public void SameFieldsGiveTheSameIdWhateverTheKernel()
    {
        var a = Manifest("Xeon", "unset");
        var b = Manifest("Xeon", "unset");
        b["runtime"]!["os"] = "another kernel";

        Assert.Equal(RunnerClass.FromManifest(a).Id, RunnerClass.FromManifest(b).Id);
    }

    [Theory]
    [InlineData("EPYC", "unset")]
    [InlineData("Xeon", "0")]
    public void ChangedCpuOrPinnedIntrinsicGivesAnotherClass(string cpu, string avx2) =>
        Assert.NotEqual(RunnerClass.FromManifest(Manifest("Xeon", "unset")).Id, RunnerClass.FromManifest(Manifest(cpu, avx2)).Id);

    [Fact]
    public void ClassNamePrefixesTheId()
    {
        var id = RunnerClass.FromManifest(Manifest("Xeon", "unset", "ci-x64")).Id;

        Assert.StartsWith("ci-x64-", id, StringComparison.Ordinal);
        Assert.Equal(16, id["ci-x64-".Length..].Length);
    }

    [Fact]
    public void CpuModelReadsProcCpuInfoOrFallsBackToUnknown()
    {
        var folder = RunFolders.TempFolder();
        var cpuinfo = Path.Combine(folder, "cpuinfo");
        File.WriteAllText(cpuinfo, "processor\t: 0\nmodel name\t: Test CPU @ 1.00GHz\n");

        Assert.Equal("Test CPU @ 1.00GHz", RunnerClass.CpuModel(cpuinfo));
        Assert.Equal("unknown", RunnerClass.CpuModel(Path.Combine(folder, "missing")));
        File.WriteAllText(cpuinfo, "processor\t: 0\nModel without a colon\n");
        Assert.Equal("unknown", RunnerClass.CpuModel(cpuinfo));
    }

    [Fact]
    public void MissingRuntimeFieldsAreUnknownNotAnError()
    {
        var runnerClass = RunnerClass.FromManifest(new JsonObject());

        Assert.Equal("unknown", runnerClass.Fields["cpuModel"]!.GetValue<string>());
        Assert.Equal("unknown", runnerClass.Fields["dotnetMajor"]!.GetValue<string>());
    }
}
