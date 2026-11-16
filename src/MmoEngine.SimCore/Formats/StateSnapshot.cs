using System.Collections.Generic;
using System.Collections.Immutable;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Formats;

/// <summary>
/// A canonical state read back by <see cref="StateSerializer.ReadLoaded"/> or
/// <see cref="StateSerializer.ReadCommitted"/>: the header values, the records in serialisation order and
/// the six event lists (contracts/state-serialization.md#layout). Every list is immutable. The weapons
/// are flat in record order: character 0's in slot order, then character 1's, and so on.
/// </summary>
public sealed class StateSnapshot
{
    internal StateSnapshot(
        uint step,
        uint nextProjectileId,
        ImmutableArray<CharacterState> characters,
        ImmutableArray<WeaponState> weapons,
        ImmutableArray<Projectile> projectiles,
        ImmutableArray<ShotEvent> shots,
        ImmutableArray<ProjectileHit> hits,
        ImmutableArray<ExpiryEvent> expiries,
        ImmutableArray<InputRejectionEvent> inputRejections,
        ImmutableArray<FireBlockEvent> fireBlocks,
        ImmutableArray<MovementOutcomeEvent> movementOutcomes)
    {
        Step = step;
        NextProjectileId = nextProjectileId;
        Characters = characters;
        Weapons = weapons;
        Projectiles = projectiles;
        Shots = shots;
        Hits = hits;
        Expiries = expiries;
        InputRejections = inputRejections;
        FireBlocks = fireBlocks;
        MovementOutcomes = movementOutcomes;
    }

    /// <summary>The state's <c>step</c>.</summary>
    public uint Step { get; }

    /// <summary>The state's <c>nextProjectileId</c>.</summary>
    public uint NextProjectileId { get; }

    /// <summary>The characters in ascending ID.</summary>
    public IReadOnlyList<CharacterState> Characters { get; }

    /// <summary>Every character's weapons, flat in record order.</summary>
    public IReadOnlyList<WeaponState> Weapons { get; }

    /// <summary>The projectiles in ascending ID.</summary>
    public IReadOnlyList<Projectile> Projectiles { get; }

    /// <summary>The shot events; always empty from <see cref="StateSerializer.ReadLoaded"/>.</summary>
    public IReadOnlyList<ShotEvent> Shots { get; }

    /// <summary>The hit events; always empty from <see cref="StateSerializer.ReadLoaded"/>.</summary>
    public IReadOnlyList<ProjectileHit> Hits { get; }

    /// <summary>The expiry events; always empty from <see cref="StateSerializer.ReadLoaded"/>.</summary>
    public IReadOnlyList<ExpiryEvent> Expiries { get; }

    /// <summary>The input rejection events; always empty from <see cref="StateSerializer.ReadLoaded"/>.</summary>
    public IReadOnlyList<InputRejectionEvent> InputRejections { get; }

    /// <summary>The fire block events; always empty from <see cref="StateSerializer.ReadLoaded"/>.</summary>
    public IReadOnlyList<FireBlockEvent> FireBlocks { get; }

    /// <summary>The movement outcome events; always empty from <see cref="StateSerializer.ReadLoaded"/>.</summary>
    public IReadOnlyList<MovementOutcomeEvent> MovementOutcomes { get; }
}
