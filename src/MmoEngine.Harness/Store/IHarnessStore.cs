namespace MmoEngine.Harness.Store;

/// <summary>A row of the <c>runs</c> table (data-model.md).</summary>
public sealed record RunRow
{
    public required string RunId { get; init; }
    public required string Kind { get; init; }
    public string GitSha { get; init; } = "unknown";
    public string ConfigHash { get; init; } = "";
    public string Scenario { get; init; } = "";
    public ulong Seed { get; init; }
    public string Scheduling { get; init; } = "";
    public string Profile { get; init; } = "none";
    public string RunnerClass { get; init; } = "";
    public string Sweep { get; init; } = "";
    public string CellId { get; init; } = "";
    public DateTime Started { get; init; }
    public DateTime? Finished { get; init; }
    public ulong Ticks { get; init; }
    public string Verdict { get; init; } = Verdicts.Pending;
    public bool Loaded { get; init; }
    public bool Retained { get; init; }
    public string Manifest { get; init; } = "{}";
}

/// <summary>Values of <see cref="RunRow.Verdict"/>.</summary>
public static class Verdicts
{
    public const string Pending = "pending";
    public const string None = "none";
}

/// <summary>A row of <c>rule_results</c>.</summary>
public sealed record RuleResultRow(string RunId, string RuleId, double? Value, double? Threshold, ulong Samples, string Outcome, string Note);

/// <summary>What a rule's query returned: whether it returned a row, its value and its sample count.</summary>
public readonly record struct RuleQueryOutcome(bool HasRow, double? Value, ulong Samples);

/// <summary>
/// The store the harness writes to and queries. <see cref="ClickHouseStore"/> is the real one; it is an
/// interface so loaders and runners can be driven without a server where a test doesn't need one.
/// </summary>
public interface IHarnessStore
{
    /// <summary>Applies every schema file; idempotent (FR-001).</summary>
    Task ApplySchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>The latest row for a run, or null.</summary>
    Task<RunRow?> GetRunAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Inserts a new version of a run's row.</summary>
    Task UpsertRunAsync(RunRow run, CancellationToken cancellationToken = default);

    /// <summary>Deletes a run's rows from every raw table (used before re-loading a partial load).</summary>
    Task DeleteRawRowsAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a sweep's <c>sweep_cells</c> rows, so re-loading a sweep leaves one copy.</summary>
    Task DeleteSweepCellsAsync(string sweep, CancellationToken cancellationToken = default);

    /// <summary>Inserts rows in batches of at least <see cref="ClickHouseStore.BatchRows"/> (FR-008).</summary>
    Task<long> InsertAsync(string table, IReadOnlyList<string> columns, IEnumerable<object?[]> rows, CancellationToken cancellationToken = default);

    /// <summary>Runs a rule query with <c>{run_id:String}</c> bound; reads the first two columns of the first row.</summary>
    Task<RuleQueryOutcome> QueryRuleAsync(string sql, string runId, CancellationToken cancellationToken = default);

    /// <summary>Copies a run's raw rows to the retained tables (FR-016).</summary>
    Task RetainAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>Marks every <c>bot_frames</c> row of one host in one run invalid (FR-029).</summary>
    Task MarkBotHostInvalidAsync(string runId, string hostId, CancellationToken cancellationToken = default);

    /// <summary>Every row of a query with <c>{run_id:String}</c> bound.</summary>
    Task<IReadOnlyList<object?[]>> QueryRowsAsync(string sql, string runId, CancellationToken cancellationToken = default);

    /// <summary>A scalar query with <c>{run_id:String}</c> bound, for reports and tests.</summary>
    Task<object?> ScalarAsync(string sql, string runId, CancellationToken cancellationToken = default);
}
