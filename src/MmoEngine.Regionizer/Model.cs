namespace MmoEngine.Regionizer;

/// <summary>What an entity is, which decides its claim radius.</summary>
public enum EntityClass : byte
{
    Infantry,
    GroundVehicle,
    Aircraft,
    Traveller,
    CapturePoint,
    HeavyProjectile,
}

/// <summary>Where a claim is measured from (research D1).</summary>
public enum ClaimAnchoring : byte
{
    /// <summary>From the entity's whole section square; changes only when it crosses a section edge.</summary>
    Section,

    /// <summary>From the entity's exact position.</summary>
    Position,
}

/// <summary>How gameplay timers keep time (research D6).</summary>
public enum TimerMode : byte
{
    /// <summary>Per-region tick counters with merge offsets and split copies (constitution default).</summary>
    TickCounter,

    /// <summary>Absolute deadlines on the continent time base.</summary>
    Absolute,
}

/// <summary>Folia's four region states (invariant 4).</summary>
public enum RegionState : byte
{
    Transient,
    Ready,
    Ticking,
    Dead,
}

/// <summary>Why a region came into existence.</summary>
public enum RegionCreationReason : byte
{
    NewClaim,
    Transient,
    Split,
}

/// <summary>A section's key: its layer and its position in section units.</summary>
public readonly record struct SectionKey(int Layer, int X, int Y);

/// <summary>Claim radius per entity class, in metres.</summary>
public sealed record ClaimRadii(
    double Infantry,
    double GroundVehicle,
    double Aircraft,
    double Traveller,
    double CapturePointZone)
{
    /// <summary>The radius for <paramref name="cls"/>. Heavy projectiles claim nothing and report 0.</summary>
    public double For(EntityClass cls) => cls switch
    {
        EntityClass.Infantry => Infantry,
        EntityClass.GroundVehicle => GroundVehicle,
        EntityClass.Aircraft => Aircraft,
        EntityClass.Traveller => Traveller,
        EntityClass.CapturePoint => CapturePointZone,
        _ => 0,
    };

    internal double Max => Math.Max(Math.Max(Math.Max(Infantry, GroundVehicle), Math.Max(Aircraft, Traveller)), CapturePointZone);
}

/// <summary>Regionizer settings. Tuning values come from validated configuration (Principle VI).</summary>
public sealed record RegionizerOptions(
    int SectionSize,
    ClaimAnchoring Anchoring,
    int MergeGap,
    ClaimRadii ClaimRadius,
    int SplitMinSections,
    double SplitMinDeadPercent,
    long SplitMinAgeSteps,
    TimerMode TimerMode)
{
    internal void Validate()
    {
        if (SectionSize <= 0 || (SectionSize & (SectionSize - 1)) != 0)
        {
            throw new ArgumentException($"SectionSize must be a positive power of two, was {SectionSize}.");
        }

        if (MergeGap is not (0 or 1))
        {
            throw new ArgumentException($"MergeGap must be 0 or 1, was {MergeGap}.");
        }

        ArgumentNullException.ThrowIfNull(ClaimRadius);
        foreach (var r in new[] { ClaimRadius.Infantry, ClaimRadius.GroundVehicle, ClaimRadius.Aircraft, ClaimRadius.Traveller, ClaimRadius.CapturePointZone })
        {
            if (r < 0 || double.IsNaN(r) || double.IsInfinity(r))
            {
                throw new ArgumentException($"Claim radii must be finite and non-negative, was {r}.");
            }
        }

        if (SplitMinSections < 1)
        {
            throw new ArgumentException($"SplitMinSections must be at least 1, was {SplitMinSections}.");
        }

        if (SplitMinDeadPercent is < 0 or > 100 || double.IsNaN(SplitMinDeadPercent))
        {
            throw new ArgumentException($"SplitMinDeadPercent must be in [0, 100], was {SplitMinDeadPercent}.");
        }

        if (SplitMinAgeSteps < 0)
        {
            throw new ArgumentException($"SplitMinAgeSteps must be non-negative, was {SplitMinAgeSteps}.");
        }
    }
}

/// <summary>The outcome of <see cref="Regionizer.MarkNotTicking"/>.</summary>
/// <param name="State">The state of the region after its tick end, or <see cref="RegionState.Dead"/> if it was absorbed or died.</param>
/// <param name="AbsorbedInto">The region that absorbed it, or 0.</param>
/// <param name="SplitChildren">The number of regions it split into (0 or 1 when it did not split).</param>
public readonly record struct TickEndResult(RegionState State, long AbsorbedInto, int SplitChildren);

/// <summary>A gameplay timer as seen from outside the library.</summary>
public readonly record struct TimerView(int TimerId, int EntityId, long Deadline, long Delay, long SetAt);

/// <summary>The invariant a <see cref="Violation"/> breaks.</summary>
public enum InvariantRule
{
    SectionSingleOwner = 1,
    ClaimsInRegion = 2,
    TickingRegionFrozen = 3,
    LegalState = 4,
    EntitySingleOwner = 5,
    PortalCrossing = 6,
}

/// <summary>One broken invariant.</summary>
public readonly record struct Violation(InvariantRule Rule, string Detail);
