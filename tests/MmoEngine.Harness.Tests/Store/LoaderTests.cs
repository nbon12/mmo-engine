using System.Globalization;
using MmoEngine.Harness.Loading;
using MmoEngine.Harness.Store;
using MmoEngine.Harness.Tests.Support;
using Serilog.Core;

namespace MmoEngine.Harness.Tests.Store;

/// <summary>US1: every run becomes rows in the store (spec 014, User Story 1), against a real ClickHouse.</summary>
public sealed class LoaderTests(ClickHouseFixture clickHouse)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>US1 scenario 1: runs, sim_ticks and sim_events hold exactly the folder's values.</summary>
    [Fact]
    public async Task CompletedRunLoadsOneRunRowAndEveryTickAndEventRow()
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();

        var result = await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);

        Assert.False(result.AlreadyLoaded);
        var run = await store.GetRunAsync(runId, Ct);
        Assert.NotNull(run);
        Assert.True(run.Loaded);
        Assert.Equal("simulator", run.Kind);
        Assert.Equal(7UL, run.Seed);
        Assert.Equal("Baseline", run.Scenario);
        Assert.Equal(60UL, run.Ticks);
        Assert.Equal(RunnerClass.FromManifest(RunFolderReader.Open(folder).Manifest).Id, run.RunnerClass);
        Assert.Equal(RunFolderReader.Open(folder).Manifest["configHash"]!.GetValue<string>(), run.ConfigHash);
        Assert.NotEqual(DateTime.UnixEpoch, run.Started);
        Assert.NotNull(run.Finished);

        var tickRows = RunFolders.CsvRows(Path.Combine(folder, "ticks.csv"));
        var eventRows = RunFolders.CsvRows(Path.Combine(folder, "events.csv"));
        Assert.Equal(tickRows, Count(await store.ScalarAsync("SELECT count() FROM sim_ticks WHERE run_id = {run_id:String}", runId, Ct)));
        Assert.Equal(eventRows, Count(await store.ScalarAsync("SELECT count() FROM sim_events WHERE run_id = {run_id:String}", runId, Ct)));

        // Values equal the files: the per-tick hash chain and a column sum, compared with the CSV.
        var lines = File.ReadAllLines(Path.Combine(folder, "ticks.csv"));
        var header = lines[0].Split(',');
        var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
        var lastHash = rows[^1][Array.IndexOf(header, "stateHash")];
        Assert.Equal(lastHash, await store.ScalarAsync(
            "SELECT state_hash FROM sim_ticks WHERE run_id = {run_id:String} ORDER BY tick DESC LIMIT 1", runId, Ct));
        var churn = rows.Sum(r => long.Parse(r[Array.IndexOf(header, "sectionChurn")], CultureInfo.InvariantCulture));
        Assert.Equal(churn, Count(await store.ScalarAsync("SELECT sum(section_churn) FROM sim_ticks WHERE run_id = {run_id:String}", runId, Ct)));
        var merges = File.ReadAllLines(Path.Combine(folder, "events.csv")).Count(l => l.Split(',')[2] == "Merge");
        Assert.Equal(merges, Count(await store.ScalarAsync("SELECT countIf(type = 'Merge') FROM sim_events WHERE run_id = {run_id:String}", runId, Ct)));
    }

    /// <summary>US1 scenario 2: loading the same folder again changes no row counts.</summary>
    [Fact]
    public async Task LoadingTheSameRunAgainAddsNoRows()
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();
        var loader = new RunLoader(store, Logger.None);
        await loader.LoadRunAsync(folder, cancellationToken: Ct);
        var ticks = Count(await store.ScalarAsync("SELECT count() FROM sim_ticks WHERE run_id = {run_id:String}", runId, Ct));
        var events = Count(await store.ScalarAsync("SELECT count() FROM sim_events WHERE run_id = {run_id:String}", runId, Ct));

        var again = await loader.LoadRunAsync(folder, cancellationToken: Ct);

        Assert.True(again.AlreadyLoaded);
        Assert.Equal(ticks, Count(await store.ScalarAsync("SELECT count() FROM sim_ticks WHERE run_id = {run_id:String}", runId, Ct)));
        Assert.Equal(events, Count(await store.ScalarAsync("SELECT count() FROM sim_events WHERE run_id = {run_id:String}", runId, Ct)));
        Assert.Equal(1, Count(await store.ScalarAsync("SELECT count() FROM runs FINAL WHERE run_id = {run_id:String}", runId, Ct)));
    }

    /// <summary>Edge case: a load interrupted before its commit marker is redone in full, without duplicates.</summary>
    [Fact]
    public async Task PartialLoadIsRedoneWithoutDuplicates()
    {
        var (folder, runId) = RunFolders.CleanRun();
        using var store = clickHouse.CreateStore();
        var reader = RunFolderReader.Open(folder);
        await store.InsertAsync("sim_ticks", RunFolderReader.TickTableColumns, reader.TickRows().Take(10), Ct);

        await new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct);

        Assert.Equal(RunFolders.CsvRows(Path.Combine(folder, "ticks.csv")),
            Count(await store.ScalarAsync("SELECT count() FROM sim_ticks WHERE run_id = {run_id:String}", runId, Ct)));
    }

    /// <summary>US1 scenario 3: an incomplete run folder is refused, and nothing is loaded.</summary>
    [Fact]
    public async Task IncompleteRunFolderIsRefusedAndLoadsNothing()
    {
        var (folder, runId) = RunFolders.CleanRun();
        await File.WriteAllTextAsync(Path.Combine(folder, "_incomplete"), "", Ct);
        using var store = clickHouse.CreateStore();

        var error = await Assert.ThrowsAsync<RunFolderException>(() => new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct));

        Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
        Assert.Null(await store.GetRunAsync(runId, Ct));
        Assert.Equal(0, Count(await store.ScalarAsync("SELECT count() FROM sim_ticks WHERE run_id = {run_id:String}", runId, Ct)));
    }

    /// <summary>US1 scenario 4: an unknown format version is refused with a message naming it, and nothing is loaded.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public async Task UnknownFormatVersionIsRefusedNamingTheVersion(int version)
    {
        var (folder, runId) = RunFolders.CleanRun();
        RunFolders.EditManifest(folder, m => m["formatVersion"] = version);
        using var store = clickHouse.CreateStore();

        var error = await Assert.ThrowsAsync<RunFolderException>(() => new RunLoader(store, Logger.None).LoadRunAsync(folder, cancellationToken: Ct));

        Assert.Contains($"formatVersion {version}", error.Message, StringComparison.Ordinal);
        Assert.Null(await store.GetRunAsync(runId, Ct));
        Assert.Equal(0, Count(await store.ScalarAsync("SELECT count() FROM sim_ticks WHERE run_id = {run_id:String}", runId, Ct)));
    }

    /// <summary>US1 scenario 5: a sweep loads every completed run and records sweep name and cell ID on each.</summary>
    [Fact]
    public async Task SweepLoadsEveryCompletedRunWithItsSweepAndCell()
    {
        var (folder, sweep) = RunFolders.Sweep();
        using var store = clickHouse.CreateStore();

        var result = await new RunLoader(store, Logger.None).LoadSweepAsync(folder, Ct);

        Assert.Equal(sweep, result.Sweep);
        Assert.Equal(2, result.Runs.Count);
        Assert.Equal(2, result.CellRows);
        var index = SweepIndex.Open(folder);
        foreach (var listed in index.Runs)
        {
            var runId = RunFolderReader.Open(Path.Combine(folder, listed.Folder)).RunId;
            var run = await store.GetRunAsync(runId, Ct);
            Assert.NotNull(run);
            Assert.Equal(sweep, run.Sweep);
            Assert.Equal(listed.CellId, run.CellId);
            Assert.Equal("sweep-cell", run.Kind);
        }

        Assert.Equal(2, Count(await store.ScalarAsync(
            "SELECT count() FROM sweep_cells FINAL WHERE sweep = {run_id:String}", sweep, Ct)));
        Assert.Equal("{\"regionizer.claimRadius.infantry\":8}", await store.ScalarAsync(
            "SELECT parameters FROM sweep_cells FINAL WHERE sweep = {run_id:String} AND cell_id = 'c000'", sweep, Ct));
    }

    /// <summary>A sweep whose run folders aren't present (the committed first-pass results) still loads its cells.</summary>
    [Fact]
    public async Task SweepWithoutRunFoldersLoadsItsCellsAndCountsMissingFolders()
    {
        var (folder, sweep) = RunFolders.Sweep();
        Directory.Delete(Path.Combine(folder, "c000"), recursive: true);
        Directory.Delete(Path.Combine(folder, "c001"), recursive: true);
        using var store = clickHouse.CreateStore();
        var loader = new RunLoader(store, Logger.None);

        var result = await loader.LoadSweepAsync(folder, Ct);
        await loader.LoadSweepAsync(folder, Ct);

        Assert.Empty(result.Runs);
        Assert.Equal(2, result.RunFoldersMissing);
        Assert.Equal(2, Count(await store.ScalarAsync("SELECT count() FROM sweep_cells WHERE sweep = {run_id:String}", sweep, Ct)));
    }

    /// <summary>US1 scenario 6: applying the schema twice succeeds and every data-model table exists.</summary>
    [Fact]
    public async Task ApplyingTheSchemaTwiceSucceedsAndEveryTableExists()
    {
        using var store = clickHouse.CreateStore();

        await store.ApplySchemaAsync(Ct);
        await store.ApplySchemaAsync(Ct);

        foreach (var table in DataModelTables)
        {
            Assert.Equal(1, Count(await store.ScalarAsync(
                "SELECT count() FROM system.tables WHERE database = currentDatabase() AND name = {run_id:String}", table, Ct)));
        }
    }

    /// <summary>The tables data-model.md lists, plus the retained twins.</summary>
    public static readonly string[] DataModelTables =
    [
        "runs", "sim_ticks", "sim_events", "net_samples", "bot_frames", "rule_results", "baselines", "run_aggregates", "sweep_cells",
        "sim_ticks_retained", "sim_events_retained", "net_samples_retained", "bot_frames_retained",
    ];

    /// <summary>FR-002: raw tables expire after 14 days; retained tables and aggregates don't.</summary>
    [Theory]
    [InlineData("sim_ticks", true)]
    [InlineData("sim_events", true)]
    [InlineData("net_samples", true)]
    [InlineData("bot_frames", true)]
    [InlineData("sim_ticks_retained", false)]
    [InlineData("bot_frames_retained", false)]
    [InlineData("run_aggregates", false)]
    [InlineData("rule_results", false)]
    public async Task RawTablesExpireAfterFourteenDaysAndRetainedTablesDoNot(string table, bool expires)
    {
        using var store = clickHouse.CreateStore();
        var ddl = (string)(await store.ScalarAsync(
            "SELECT create_table_query FROM system.tables WHERE database = currentDatabase() AND name = {run_id:String}", table, Ct))!;

        Assert.Equal(expires, ddl.Contains("TTL loaded_at + toIntervalDay(14)", StringComparison.Ordinal));
    }

    /// <summary>FR-008: inserts are batched at 5,000 rows.</summary>
    [Fact]
    public void InsertBatchIsAtLeastFiveThousandRows() => Assert.True(ClickHouseStore.BatchRows >= 5000);

    private static long Count(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
}
