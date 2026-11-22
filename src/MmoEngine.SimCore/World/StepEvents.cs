using System;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Weapons;

namespace MmoEngine.SimCore.World;

/// <summary>
/// A shot event (US4), one element of <c>events.shots</c>, in ascending character ID, then firing order
/// (specs/015-shared-simulation-core/contracts/state-serialization.md#step-events).
/// </summary>
public record struct ShotEvent
{
    /// <summary>The shooter.</summary>
    public uint CharacterId { get; set; }

    /// <summary>The weapon slot.</summary>
    public byte Slot { get; set; }

    /// <summary>The counter value the spread hash used, before the shot increments it.</summary>
    public uint ShotCounter { get; set; }

    /// <summary>The projectile the shot spawned.</summary>
    public uint ProjectileId { get; set; }

    /// <summary>The unit direction inside the spread cone.</summary>
    public Vec3 Direction { get; set; }
}

/// <summary>A projectile expiry, one element of <c>events.expiries</c>, in ascending projectile ID.</summary>
public record struct ExpiryEvent
{
    /// <summary>The projectile that expired.</summary>
    public uint ProjectileId { get; set; }

    /// <summary>Range, Lifetime or OutOfBounds; never 0.</summary>
    public ExpiryReason Reason { get; set; }
}

/// <summary>A rejected input command, one element of <c>events.inputRejections</c>, in ascending character ID.</summary>
public record struct InputRejectionEvent
{
    /// <summary>The character whose command was rejected.</summary>
    public uint CharacterId { get; set; }

    /// <summary>The first validation rule the command failed; never None.</summary>
    public InputRejection Reason { get; set; }
}

/// <summary>A blocked trigger, one element of <c>events.fireBlocks</c>, in ascending character ID.</summary>
public record struct FireBlockEvent
{
    /// <summary>The character whose fire was blocked.</summary>
    public uint CharacterId { get; set; }

    /// <summary>Why no shot fired while Fire was held; never None.</summary>
    public FireBlock Reason { get; set; }
}

/// <summary>
/// A movement outcome, one element of <c>events.movementOutcomes</c>, in ascending character ID; only
/// characters with non-zero flags have one.
/// </summary>
public record struct MovementOutcomeEvent
{
    /// <summary>The character moved.</summary>
    public uint CharacterId { get; set; }

    /// <summary>HeadroomBlocked, Stuck, SlideCapReached and ClampedToBounds; never None.</summary>
    public MovementOutcome Flags { get; set; }
}

/// <summary>
/// Typed read-only views of one step's six event lists, in serialisation order
/// (specs/015-shared-simulation-core/data-model.md#stepreport). The views are over the world's event
/// buffers and stay valid until the next call on the same world.
/// </summary>
public readonly ref struct StepEvents
{
    /// <summary>The six lists in serialisation order.</summary>
    public StepEvents(
        ReadOnlySpan<ShotEvent> shots,
        ReadOnlySpan<ProjectileHit> hits,
        ReadOnlySpan<ExpiryEvent> expiries,
        ReadOnlySpan<InputRejectionEvent> inputRejections,
        ReadOnlySpan<FireBlockEvent> fireBlocks,
        ReadOnlySpan<MovementOutcomeEvent> movementOutcomes)
    {
        Shots = shots;
        Hits = hits;
        Expiries = expiries;
        InputRejections = inputRejections;
        FireBlocks = fireBlocks;
        MovementOutcomes = movementOutcomes;
    }

    /// <summary>1: the shots, in ascending character ID, then firing order.</summary>
    public ReadOnlySpan<ShotEvent> Shots { get; }

    /// <summary>2: the hits, in ascending projectile ID.</summary>
    public ReadOnlySpan<ProjectileHit> Hits { get; }

    /// <summary>3: the expiries, in ascending projectile ID.</summary>
    public ReadOnlySpan<ExpiryEvent> Expiries { get; }

    /// <summary>4: the input rejections, in ascending character ID.</summary>
    public ReadOnlySpan<InputRejectionEvent> InputRejections { get; }

    /// <summary>5: the fire blocks, in ascending character ID.</summary>
    public ReadOnlySpan<FireBlockEvent> FireBlocks { get; }

    /// <summary>6: the movement outcomes, in ascending character ID, only for non-zero flags.</summary>
    public ReadOnlySpan<MovementOutcomeEvent> MovementOutcomes { get; }
}
