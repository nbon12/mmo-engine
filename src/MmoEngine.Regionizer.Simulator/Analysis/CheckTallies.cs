using System.Globalization;

namespace MmoEngine.Regionizer.Simulator.Analysis;

/// <summary>A check's invariant violations and its first failure, which always names the seed.</summary>
internal sealed class FailureLog(ulong seed)
{
    public string? First { get; private set; }

    public int InvariantViolations { get; private set; }

    public void Record(int trial, int step, string message) => Record($"trial {trial}, step {step}: {message}");

    public void Record(string message) => First ??= string.Create(CultureInfo.InvariantCulture, $"seed {seed}, {message}");

    /// <summary>Counts the violations the invariant checker found after one tick.</summary>
    public void Invariants(int trial, int step, IReadOnlyList<Violation> found)
    {
        if (found.Count == 0)
        {
            return;
        }

        InvariantViolations += found.Count;
        Record(trial, step, $"invariant {found[0].Rule} broken: {found[0].Detail}");
    }

    public static string Pair(double ax, double ay, double bx, double by, double distance) =>
        string.Create(CultureInfo.InvariantCulture, $"A ({ax:R}, {ay:R}), B ({bx:R}, {by:R}), {distance:R} m apart");
}

/// <summary>Classifies merge-distance observations against the two bounds of research D1.</summary>
internal sealed class MergeDistanceTally(double lowerBound, double mergeDistance, FailureLog log)
{
    public int Observations { get; private set; }

    public int WithinLowerBound { get; private set; }

    public int BeyondMergeDistance { get; private set; }

    public int LowerBoundViolations { get; private set; }

    public int UpperBoundViolations { get; private set; }

    public double? ClosestSeparated { get; private set; }

    public double? FarthestShared { get; private set; }

    /// <summary>One tick end: the pair's positions and whether they share a region.</summary>
    public void Observe(int trial, int step, double ax, double ay, double bx, double by, bool shared)
    {
        var distance = SimulatorAgreement.Distance(ax, ay, bx, by);
        Observations++;
        if (shared)
        {
            FarthestShared = Math.Max(FarthestShared ?? double.MinValue, distance);
        }
        else
        {
            ClosestSeparated = Math.Min(ClosestSeparated ?? double.MaxValue, distance);
        }

        if (distance <= lowerBound)
        {
            WithinLowerBound++;
            if (!shared)
            {
                LowerBoundViolations++;
                log.Record(trial, step, $"{FailureLog.Pair(ax, ay, bx, by, distance)}: two regions within rA + rB = {SweepNumber(lowerBound)} m");
            }
        }
        else if (distance > mergeDistance)
        {
            BeyondMergeDistance++;
            if (shared)
            {
                UpperBoundViolations++;
                log.Record(trial, step, $"{FailureLog.Pair(ax, ay, bx, by, distance)}: one region beyond d_max = {SweepNumber(mergeDistance)} m");
            }
        }
    }

    private static string SweepNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>
/// Follows one approach at a time for the claim-radius floor: each tick end that finds the pair within
/// interaction range must follow a tick end at which they already shared a region.
/// </summary>
internal sealed class FloorTally(double interactionRange, FailureLog log)
{
    private double _previous;
    private bool _sharedBefore;

    public int InteractionTicks { get; private set; }

    public int InteractionsWhileSeparated { get; private set; }

    /// <summary>The largest closing over one tick seen so far.</summary>
    public double MaxClosingPerTick { get; private set; }

    /// <summary>One tick end of an approach; tick 0 is the tick that ends right after placement.</summary>
    public void Observe(int trial, int tick, double ax, double ay, double bx, double by, bool shared)
    {
        var distance = SimulatorAgreement.Distance(ax, ay, bx, by);
        if (tick > 0)
        {
            MaxClosingPerTick = Math.Max(MaxClosingPerTick, _previous - distance);
        }

        if (distance <= interactionRange)
        {
            InteractionTicks++;

            // Placed already in range, there is no earlier tick end: the pair must share now.
            if (!(tick > 0 ? _sharedBefore : shared))
            {
                InteractionsWhileSeparated++;
                log.Record(trial, tick, $"{FailureLog.Pair(ax, ay, bx, by, distance)}: within interaction range of a target it did not share a region with at the previous tick end");
            }
        }

        _previous = distance;
        _sharedBefore = shared;
    }
}
