using System.Globalization;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility;

namespace MmoEngine.Harness.Store;

/// <summary>
/// The ClickHouse store, through ClickHouse's official .NET client over HTTP (research R1). Loads are
/// idempotent by run ID: the <c>runs</c> row with <c>loaded = 1</c> is the commit marker (research R3).
/// </summary>
public sealed class ClickHouseStore : IHarnessStore, IDisposable
{
    /// <summary>The minimum insert batch (FR-008; ClickHouse's advice relayed by the exporter docs).</summary>
    public const int BatchRows = 5000;

    /// <summary>The raw tables a run's rows live in; each has a <c>_retained</c> twin.</summary>
    public static readonly IReadOnlyList<string> RawTables = ["sim_ticks", "sim_events", "net_samples", "bot_frames"];

    private static readonly string[] RunColumns =
    [
        "run_id", "kind", "git_sha", "config_hash", "scenario", "seed", "scheduling", "profile", "runner_class",
        "sweep", "cell_id", "started", "finished", "ticks", "verdict", "loaded", "retained", "manifest", "version",
    ];

    private readonly ClickHouseClient client;
    private long lastVersion;

    /// <summary>Connects with a ClickHouse connection string, for example <c>Host=localhost;Port=8123;Username=default</c>.</summary>
    public ClickHouseStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        client = new ClickHouseClient(connectionString);
    }

    public async Task ApplySchemaAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (_, sql) in SchemaScripts.All())
        {
            await client.ExecuteNonQueryAsync(sql, null, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<RunRow?> GetRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        const string sql =
            "SELECT run_id, kind, git_sha, config_hash, scenario, seed, scheduling, profile, runner_class, sweep, cell_id, " +
            "started, finished, ticks, verdict, loaded, retained, manifest FROM runs FINAL WHERE run_id = {run_id:String} LIMIT 1";
        using var reader = await client.ExecuteReaderAsync(sql, Parameters(runId), null, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new RunRow
        {
            RunId = reader.GetString(0),
            Kind = reader.GetString(1),
            GitSha = reader.GetString(2),
            ConfigHash = reader.GetString(3),
            Scenario = reader.GetString(4),
            Seed = Convert.ToUInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
            Scheduling = reader.GetString(6),
            Profile = reader.GetString(7),
            RunnerClass = reader.GetString(8),
            Sweep = reader.GetString(9),
            CellId = reader.GetString(10),
            Started = reader.GetDateTime(11),
            Finished = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
            Ticks = Convert.ToUInt64(reader.GetValue(13), CultureInfo.InvariantCulture),
            Verdict = reader.GetString(14),
            Loaded = Convert.ToByte(reader.GetValue(15), CultureInfo.InvariantCulture) == 1,
            Retained = Convert.ToByte(reader.GetValue(16), CultureInfo.InvariantCulture) == 1,
            Manifest = reader.GetString(17),
        };
    }

    public async Task UpsertRunAsync(RunRow run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        object?[] row =
        [
            run.RunId, run.Kind, run.GitSha, run.ConfigHash, run.Scenario, run.Seed, run.Scheduling, run.Profile,
            run.RunnerClass, run.Sweep, run.CellId, Utc(run.Started), run.Finished is { } f ? Utc(f) : null, run.Ticks,
            run.Verdict, (byte)(run.Loaded ? 1 : 0), (byte)(run.Retained ? 1 : 0), run.Manifest, NextVersion(),
        ];
        await client.InsertBinaryAsync("runs", RunColumns, [row], null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteRawRowsAsync(string runId, CancellationToken cancellationToken = default)
    {
        foreach (var table in RawTables.Append("run_aggregates"))
        {
            // Lightweight delete; synchronous for this query (research R3).
            await client.ExecuteNonQueryAsync(
                $"DELETE FROM {table} WHERE run_id = {{run_id:String}}", Parameters(runId), null, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteSweepCellsAsync(string sweep, CancellationToken cancellationToken = default)
    {
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("sweep", sweep);
        await client.ExecuteNonQueryAsync("DELETE FROM sweep_cells WHERE sweep = {sweep:String}", parameters, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> InsertAsync(string table, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        long total = 0;
        foreach (var batch in rows.Chunk(BatchRows))
        {
            total += await client.InsertBinaryAsync(table, columns, batch, new InsertOptions { BatchSize = BatchRows }, cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    public async Task<RuleQueryOutcome> QueryRuleAsync(string sql, string runId, CancellationToken cancellationToken = default)
    {
        using var reader = await client.ExecuteReaderAsync(sql, Parameters(runId), null, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new RuleQueryOutcome(false, null, 0);
        }

        double? value = reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture);
        var samples = reader.FieldCount > 1 && !reader.IsDBNull(1) ? Convert.ToUInt64(reader.GetValue(1), CultureInfo.InvariantCulture) : 0UL;
        return new RuleQueryOutcome(true, value, samples);
    }

    public async Task RetainAsync(string runId, CancellationToken cancellationToken = default)
    {
        foreach (var table in RawTables)
        {
            // Idempotent: clear any earlier copy of this run first.
            await client.ExecuteNonQueryAsync(
                $"DELETE FROM {table}_retained WHERE run_id = {{run_id:String}}", Parameters(runId), null, cancellationToken).ConfigureAwait(false);
            await client.ExecuteNonQueryAsync(
                $"INSERT INTO {table}_retained SELECT * FROM {table} WHERE run_id = {{run_id:String}}", Parameters(runId), null, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<object?> ScalarAsync(string sql, string runId, CancellationToken cancellationToken = default) =>
        client.ExecuteScalarAsync(sql, Parameters(runId), null, cancellationToken)!;

    public void Dispose() => client.Dispose();

    private static ClickHouseParameterCollection Parameters(string runId)
    {
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("run_id", runId);
        return parameters;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);

    /// <summary>A strictly increasing row version (microseconds since the epoch), so the latest upsert wins.</summary>
    private ulong NextVersion()
    {
        var now = DateTime.UtcNow.Ticks / 10;
        long next;
        long seen;
        do
        {
            seen = Interlocked.Read(ref lastVersion);
            next = Math.Max(now, seen + 1);
        }
        while (Interlocked.CompareExchange(ref lastVersion, next, seen) != seen);

        return (ulong)next;
    }
}
