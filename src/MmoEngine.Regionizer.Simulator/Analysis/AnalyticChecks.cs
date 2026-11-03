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
    /// The issue's worked examples: two aircraft closing at 240 m/s at 30 Hz; the default infantry and
    /// ground-vehicle claim radii (16 and 24 m) at 16 m sections; and a 600 m lane with d = 30 m and 30, 60
    /// and 120 travellers, at 400 trials per step (FR-031 asks for at least 200) and a 10-point tolerance. The
    /// interaction ranges are placeholders until game design supplies real values, the same ones as
    /// <c>simulator/sweeps/memo-rules.json</c>; closing speeds are twice each class's default speed ceiling.
    /// </summary>
    public static AnalyticCheckOptions Default { get; } = new(
        Seed: 20261103,
        Floor: new FloorCheckOptions(
            [
                new ClaimRadiusFloorCase("infantry", 10, 14, 30),
                new ClaimRadiusFloorCase("groundVehicle", 15, 60, 30),
                new ClaimRadiusFloorCase("aircraft", 8, 240, 30),
            ],
            SectionSize: 16,
            TrialsPerAnchoring: 50),
        MergeDistance: new MergeDistanceCheckOptions(SectionSize: 16, RadiusA: 16, RadiusB: 24, Trials: 100),
        LaneBridging: new LaneBridgingCheckOptions(
            LaneLengthMetres: 600,
            MergeDistanceMetres: 30,
            SectionSize: 1,
            Travellers: [30, 60, 120],
            TrialsPerStep: 400,
            TolerancePoints: 0.10));
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

    /// <summary>True when every check is within tolerance; the <c>check</c> command exits 4 otherwise.</summary>
    public bool Passed => ClaimRadiusFloor.Count > 0 && ClaimRadiusFloor.All(c => c.Passed)
        && MergeDistance.Count > 0 && MergeDistance.All(c => c.Passed)
        && LaneBridging.Passed;

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
    /// </summary>
    public static CheckReport RunAll(AnalyticCheckOptions options, string? outDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var floor = SimulatorAgreement.ClaimRadiusFloor(options.Floor, options.Seed);
        var mergeDistance = new List<MergeDistanceCheckResult>();
        foreach (var anchoring in new[] { ClaimAnchoring.Section, ClaimAnchoring.Position })
        {
            foreach (var gap in new[] { 0, 1 })
            {
                mergeDistance.Add(SimulatorAgreement.MergeDistance(options.MergeDistance, anchoring, gap, options.Seed));
            }
        }

        var lane = SimulatorAgreement.LaneBridging(options.LaneBridging, options.Seed);
        var report = new CheckReport(FormatVersion, options.Seed, floor, mergeDistance, lane);
        if (outDirectory is not null)
        {
            report.WriteJson(outDirectory);
        }

        return report;
    }
}
