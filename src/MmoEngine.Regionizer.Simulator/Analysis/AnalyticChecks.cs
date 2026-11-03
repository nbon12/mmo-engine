using System.Text.Json;
using System.Text.Json.Serialization;

namespace MmoEngine.Regionizer.Simulator.Analysis;

/// <summary>Everything <see cref="AnalyticChecks.RunAll"/> runs. Every value is a check input, not a tuning value.</summary>
public sealed record AnalyticCheckOptions(
    ulong Seed,
    FloorCheckOptions Floor,
    MergeDistanceCheckOptions MergeDistance,
    LaneBridgingCheckOptions LaneBridging)
{
    /// <summary>
    /// The issue's worked examples: each class closing head-on on an unoccupied capture point, among them an
    /// aircraft at 240 m/s at 30 Hz (research D16), at 1 m sections so that a radius below the floor by more
    /// than the section slack exists for every case; the default infantry and ground-vehicle claim radii (16
    /// and 24 m) at 16 m sections; and the lane-bridging setup FR-031 fixes
    /// (<see cref="LaneBridgingCheckOptions.Fr031"/>). The interaction ranges are placeholders until game
    /// design supplies real values, the same ones as <c>simulator/sweeps/memo-rules.json</c>; closing speeds
    /// are twice each class's default speed ceiling.
    /// </summary>
    public static AnalyticCheckOptions Default { get; } = new(
        Seed: 20261103,
        Floor: new FloorCheckOptions(
            [
                new ClaimRadiusFloorCase("infantry", 10, 14, 30, EntityClass.Infantry),
                new ClaimRadiusFloorCase("groundVehicle", 15, 60, 30, EntityClass.GroundVehicle),
                new ClaimRadiusFloorCase("aircraft", 8, 240, 30, EntityClass.Aircraft),
            ],
            SectionSize: 1,
            TrialsPerAnchoring: 20),
        MergeDistance: new MergeDistanceCheckOptions(SectionSize: 16, RadiusA: 16, RadiusB: 24, Trials: 100),
        LaneBridging: LaneBridgingCheckOptions.Fr031);

    /// <summary>
    /// Reads check options from JSON (camelCase, enums as names), as <c>regionizer-sim check --options</c>
    /// takes them, for example to rerun the floor check with real interaction ranges. Every field is
    /// required and unknown fields are rejected. Throws <see cref="JsonException"/> naming the problem.
    /// </summary>
    public static AnalyticCheckOptions FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<AnalyticCheckOptions>(json, CheckReport.ReadOptions)
            ?? throw new JsonException("The check options are null.");
    }
}

/// <summary>The outcome of every analytic check (the content of <c>checks.json</c>).</summary>
public sealed record CheckReport(
    int FormatVersion,
    ulong Seed,
    IReadOnlyList<FloorCheckResult> ClaimRadiusFloor,
    IReadOnlyList<MergeDistanceCheckResult> MergeDistance,
    LaneBridgingCheckResult LaneBridging)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>How check options are read: camelCase, enums as names, every field required, unknown fields rejected.</summary>
    internal static JsonSerializerOptions ReadOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>True when every check is within tolerance; the <c>check</c> command exits 4 otherwise.</summary>
    public bool Passed => ClaimRadiusFloor.Count > 0 && ClaimRadiusFloor.All(c => c.Passed)
        && MergeDistance.Count > 0 && MergeDistance.All(c => c.Passed)
        && LaneBridging.Passed;

    /// <summary>One line per check outside tolerance, naming the check and its first failure (which carries its seed).</summary>
    public IReadOnlyList<string> Failures()
    {
        var failures = new List<string>();
        failures.AddRange(ClaimRadiusFloor.Where(f => !f.Passed).Select(f => $"claim-radius floor {f.Name} {f.Anchoring}: {f.FirstFailure ?? "not passed"}"));
        if (ClaimRadiusFloor.Count == 0)
        {
            failures.Add("claim-radius floor: no cases");
        }

        failures.AddRange(MergeDistance.Where(m => !m.Passed).Select(m => $"merge distance {m.Anchoring} gap {m.MergeGap}: {m.FirstFailure ?? "a bound was not exercised"}"));
        if (MergeDistance.Count == 0)
        {
            failures.Add("merge distance: no modes");
        }

        if (!LaneBridging.Passed)
        {
            failures.Add($"lane bridging: {LaneBridging.FirstFailure ?? "no traffic steps"}");
        }

        return failures;
    }

    /// <summary>The report as indented camelCase JSON, enums as names.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Writes <c>checks.json</c> into <paramref name="directory"/>, creating it, and returns the file's path.</summary>
    public string WriteJson(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, AnalyticChecks.FileName);
        File.WriteAllText(path, ToJson() + "\n");
        return path;
    }
}

/// <summary>Runs the three analytic checks against the core (FR-031, SC-006).</summary>
public static class AnalyticChecks
{
    /// <summary>The report's file name inside the output folder.</summary>
    public const string FileName = "checks.json";

    /// <summary>The report's format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Runs the claim-radius floor, the merge distance in both anchoring modes with merge gaps 0 and 1,
    /// and the lane-bridging estimate. Writes <c>checks.json</c> when <paramref name="outDirectory"/> is given.
    /// The floor cases, merge-distance modes and lane traffic steps run in parallel: each draws from its own
    /// named stream and drives its own regionizers, so the report is the same as running them one by one,
    /// and it lists them in that order. An invalid input throws the first error in that order.
    /// </summary>
    public static CheckReport RunAll(AnalyticCheckOptions options, string? outDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Floor);
        ArgumentNullException.ThrowIfNull(options.Floor.Cases);
        ArgumentNullException.ThrowIfNull(options.MergeDistance);
        ArgumentNullException.ThrowIfNull(options.LaneBridging);
        ArgumentNullException.ThrowIfNull(options.LaneBridging.Travellers);

        var cases = options.Floor.Cases;
        var floor = new IReadOnlyList<FloorCheckResult>[cases.Count];
        (ClaimAnchoring Anchoring, int Gap)[] modes =
            [(ClaimAnchoring.Section, 0), (ClaimAnchoring.Section, 1), (ClaimAnchoring.Position, 0), (ClaimAnchoring.Position, 1)];
        var merge = new MergeDistanceCheckResult[modes.Length];
        var travellers = options.LaneBridging.Travellers;
        LaneBridgingCheckOptions[] laneSteps = travellers.Count == 0 ? [options.LaneBridging] : travellers.Select(t => options.LaneBridging with { Travellers = [t] }).ToArray();
        var lane = new LaneBridgingCheckResult[laneSteps.Length];

        var jobs = new List<Action>();
        jobs.AddRange(Enumerable.Range(0, cases.Count).Select(i => (Action)(() =>
            floor[i] = SimulatorAgreement.ClaimRadiusFloor(options.Floor with { Cases = [cases[i]] }, options.Seed))));
        jobs.AddRange(Enumerable.Range(0, modes.Length).Select(i => (Action)(() =>
            merge[i] = SimulatorAgreement.MergeDistance(options.MergeDistance, modes[i].Anchoring, modes[i].Gap, options.Seed))));
        jobs.AddRange(Enumerable.Range(0, laneSteps.Length).Select(i => (Action)(() =>
            lane[i] = SimulatorAgreement.LaneBridging(laneSteps[i], options.Seed))));
        RunInParallel(jobs);

        var report = new CheckReport(FormatVersion, options.Seed, [.. floor.SelectMany(f => f)], merge, CombineLane(lane));
        if (outDirectory is not null)
        {
            report.WriteJson(outDirectory);
        }

        return report;
    }

    /// <summary>Runs every job, then throws the first job's exception in list order, if any, so a failure is deterministic.</summary>
    private static void RunInParallel(List<Action> jobs)
    {
        var errors = new Exception?[jobs.Count];
        Parallel.For(0, jobs.Count, i =>
        {
            try
            {
                jobs[i]();
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                errors[i] = e;
            }
        });

        if (errors.FirstOrDefault(e => e is not null) is { } first)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }

    /// <summary>The lane steps run one per job, joined in traffic order as one result.</summary>
    private static LaneBridgingCheckResult CombineLane(LaneBridgingCheckResult[] lane) => lane[0] with
    {
        Steps = [.. lane.SelectMany(l => l.Steps)],
        InvariantViolations = lane.Sum(l => l.InvariantViolations),
        FirstFailure = lane.Select(l => l.FirstFailure).FirstOrDefault(f => f is not null),
    };
}
