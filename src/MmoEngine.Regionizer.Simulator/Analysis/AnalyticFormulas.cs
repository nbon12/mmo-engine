namespace MmoEngine.Regionizer.Simulator.Analysis;

/// <summary>
/// The issue's three analytic results (FR-031): the claim-radius floor, the merge distance (research
/// D1) and the lane-bridging estimate. This is analysis, not the simulated path, so the estimate may
/// use <see cref="Math.Exp"/> and <see cref="Math.Pow"/>.
/// </summary>
public static class AnalyticFormulas
{
    /// <summary>
    /// The claim-radius floor r_min = d_interact + v_rel_max · dt, with dt = 1 / tick rate: a claim that
    /// reaches this far covers every target that can come within interaction range by the next tick.
    /// </summary>
    public static double ClaimRadiusFloor(double interactionRangeMetres, double closingSpeedMetresPerSecond, double tickRateHz)
    {
        RequireNonNegative(interactionRangeMetres, nameof(interactionRangeMetres));
        RequireNonNegative(closingSpeedMetresPerSecond, nameof(closingSpeedMetresPerSecond));
        RequirePositive(tickRateHz, nameof(tickRateHz));
        return interactionRangeMetres + (closingSpeedMetresPerSecond / tickRateHz);
    }

    /// <summary>The section diagonals in the merge distance: 3 section-anchored, 1 position-anchored (research D1).</summary>
    public static int AnchoringFactor(ClaimAnchoring anchoring) => anchoring switch
    {
        ClaimAnchoring.Section => 3,
        ClaimAnchoring.Position => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(anchoring), anchoring, "Unknown claim anchoring."),
    };

    /// <summary>
    /// The section slack k·√2·s: how far the section grid can carry two claims beyond the sum of their
    /// radii with a merge gap of 0 (research D1, D16).
    /// </summary>
    public static double SectionSlack(int sectionSize, ClaimAnchoring anchoring) => MergeDistance(0, 0, sectionSize, anchoring, 0);

    /// <summary>
    /// The merge distance d_max = rA + rB + k·√2·s, plus √2·s for a merge gap of 1: no two entities
    /// farther apart than this can share a region because of their claims (research D1).
    /// </summary>
    public static double MergeDistance(double radiusA, double radiusB, int sectionSize, ClaimAnchoring anchoring, int mergeGap)
    {
        RequireNonNegative(radiusA, nameof(radiusA));
        RequireNonNegative(radiusB, nameof(radiusB));
        ArgumentOutOfRangeException.ThrowIfLessThan(sectionSize, 1);
        if (mergeGap is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(mergeGap), mergeGap, "The merge gap must be 0 or 1.");
        }

        var diagonal = Math.Sqrt(2) * sectionSize;
        return radiusA + radiusB + ((AnchoringFactor(anchoring) + mergeGap) * diagonal);
    }

    /// <summary>
    /// The lane-bridging estimate P = (1 − e^(−λd))^(λl + 1), with λ = travellers / lane length: the
    /// chance that every one of the λl + 1 gaps along a lane between two fights is shorter than the
    /// merge distance d, so the fights fuse through the travellers.
    /// </summary>
    public static double LaneBridgingProbability(int travellers, double laneLengthMetres, double mergeDistanceMetres)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(travellers);
        RequirePositive(laneLengthMetres, nameof(laneLengthMetres));
        RequireNonNegative(mergeDistanceMetres, nameof(mergeDistanceMetres));
        var lambda = travellers / laneLengthMetres;
        var gapShort = 1 - Math.Exp(-lambda * mergeDistanceMetres);

        // λl is the traveller count, so the exponent is the number of gaps, travellers + 1.
        return Math.Pow(gapShort, travellers + 1.0);
    }

    private static void RequireNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be finite and non-negative.");
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Must be finite and positive.");
        }
    }
}
