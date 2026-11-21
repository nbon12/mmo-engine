using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Drives a <see cref="SimWorld"/> one step at a time for the weapon tests (US4) and copies each step's
/// weapon results out of its <see cref="StepReport"/> as a <see cref="WeaponSnapshot"/>, before the next
/// step reuses the world's buffers. It makes no assertion of its own: a step that faults throws
/// <see cref="InvalidOperationException"/> naming the fault's code and field path, so the calling test
/// fails loudly. It uses no random number generator and writes nothing under <c>simcore/</c>.
/// <para>
/// <see cref="Create"/> builds a world over <see cref="TestWorlds.Flat"/> (through
/// <c>CollisionFile.Encode</c> and <c>CollisionFile.Decode</c>) with one character, ID 1, standing and
/// grounded at the origin, and steps it with <see cref="Step"/> and <see cref="Hold"/>.
/// <see cref="Run"/> steps a recorded stream, such as a <c>ScenarioCatalog</c> definition, on the
/// collision world the caller gives.
/// </para>
/// </summary>
public sealed class WeaponRuns
{
    /// <summary>The match salt of every world <see cref="Create"/> builds.</summary>
    public const ulong Salt = 0x0015_0000_5EED_0004UL;

    private static readonly Lazy<CollisionWorld> FlatWorld = new(() => TestWorlds.Load(TestWorlds.Flat()));

    private readonly SimWorld _world;
    private uint _sequence;

    private WeaponRuns(SimWorld world)
    {
        _world = world;
    }

    /// <summary>
    /// <c>CollisionFile.Decode(CollisionFile.Encode(TestWorlds.Flat()))</c>, decoded once and shared: a
    /// <see cref="CollisionWorld"/> is immutable.
    /// </summary>
    public static CollisionWorld Flat => FlatWorld.Value;

    /// <summary>The world's compiled configuration.</summary>
    public CompiledConfig Config => _world.Config;

    /// <summary>The world's match salt: <see cref="Salt"/>.</summary>
    public ulong MatchSalt => _world.MatchSalt;

    /// <summary>The step number of the last committed state; 0 before the first step.</summary>
    public uint CurrentStep => _world.CurrentStep;

    /// <summary>
    /// Builds a <see cref="SimWorld"/> over <see cref="Flat"/> with <c>CompiledConfig.Create(config)</c>, and
    /// resets it with match salt <see cref="Salt"/> and a one-character canonical state at step 0. The
    /// character is <c>TestStates.StandingCharacter(1, 0, 0)</c>: ID 1, standing and grounded at the origin
    /// of the flat world, yaw 0, pitch 0. Each of its weapons starts as a new character's,
    /// <c>WeaponState.New(magazineSize, reserveRounds)</c> of that slot's compiled configuration (Ready, a
    /// full magazine, StepsSinceShot 65535). The overrides are applied before the state is serialised with
    /// <see cref="StateSerializer.Write"/>.
    /// </summary>
    /// <param name="config">The raw configuration; it is compiled, so it must pass the guard.</param>
    /// <param name="activeSlot">The character's <see cref="CharacterState.ActiveSlot"/>.</param>
    /// <param name="weapon">
    /// Called once per slot with the slot and its new weapon's state; its result is that slot's state, so
    /// a caller can change any <see cref="WeaponState"/> field with <c>with</c>. Null keeps the new weapons.
    /// </param>
    /// <param name="projectiles">The state's projectiles in ascending ID; null or empty for none.</param>
    /// <param name="nextProjectileId">
    /// The state's <c>nextProjectileId</c>; null for one more than the largest projectile ID, or 1 with no
    /// projectiles.
    /// </param>
    /// <exception cref="ConfigValidationException">The configuration does not pass the guard.</exception>
    /// <exception cref="CoreFormatException">The world refuses the arranged state.</exception>
    /// <exception cref="InvalidOperationException">The arranged state does not serialise.</exception>
    public static WeaponRuns Create(
        SimConfig config,
        byte activeSlot = 0,
        Func<int, WeaponState, WeaponState>? weapon = null,
        IReadOnlyList<Projectile>? projectiles = null,
        uint? nextProjectileId = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var compiled = CompiledConfig.Create(config);
        var world = new SimWorld(compiled, Flat);
        world.Reset(InitialState(compiled, activeSlot, weapon, projectiles, nextProjectileId), Salt);
        return new WeaponRuns(world);
    }

    /// <summary>
    /// Builds a <see cref="SimWorld"/> from the stream's configuration over <paramref name="world"/>, resets
    /// it with the stream's initial state and match salt, and steps every <see cref="RecordedStep"/> with
    /// its commands as <see cref="CharacterCommand"/>s (characters without one get the neutral input).
    /// </summary>
    /// <returns>One snapshot per stream entry, in order.</returns>
    /// <exception cref="InvalidOperationException">A step faulted.</exception>
    public static IReadOnlyList<WeaponSnapshot> Run(RecordedStream stream, CollisionWorld world)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(world);
        var sim = new SimWorld(CompiledConfig.Create(stream.Config), world);
        sim.Reset(stream.InitialState.Span, stream.MatchSalt);

        var snapshots = new List<WeaponSnapshot>(stream.Steps.Count);
        foreach (var entry in stream.Steps)
        {
            var commands = new CharacterCommand[entry.Commands.Length];
            for (var c = 0; c < commands.Length; c++)
            {
                commands[c] = new CharacterCommand(entry.Commands[c].CharacterIndex, entry.Commands[c].Command);
            }

            snapshots.Add(Snapshot(sim.Step(commands)));
        }

        return snapshots;
    }

    /// <summary>
    /// Steps the world once with one <c>CharacterCommand(0, command)</c>: the next Sequence (1 for the
    /// first step), ClientStep equal to the step being computed, <paramref name="buttons"/>, the aim
    /// <paramref name="yaw"/> and <paramref name="pitch"/>, and WeaponSlot <paramref name="slot"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The step faulted.</exception>
    public WeaponSnapshot Step(Buttons buttons, byte slot, ushort yaw = 0, short pitch = 0)
    {
        _sequence++;
        var command = new InputCommand(_sequence, unchecked(_world.CurrentStep + 1u), buttons, yaw, pitch, slot);
        CharacterCommand[] commands = [new CharacterCommand(0, command)];
        return Snapshot(_world.Step(commands));
    }

    /// <summary>Calls <see cref="Step"/> <paramref name="steps"/> times with the same arguments.</summary>
    /// <returns>One snapshot per step, in order.</returns>
    /// <exception cref="InvalidOperationException">A step faulted.</exception>
    public IReadOnlyList<WeaponSnapshot> Hold(Buttons buttons, int steps, byte slot, ushort yaw = 0, short pitch = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        var snapshots = new WeaponSnapshot[steps];
        for (var i = 0; i < steps; i++)
        {
            snapshots[i] = Step(buttons, slot, yaw, pitch);
        }

        return snapshots;
    }

    private static byte[] InitialState(
        CompiledConfig config,
        byte activeSlot,
        Func<int, WeaponState, WeaponState>? weapon,
        IReadOnlyList<Projectile>? projectiles,
        uint? nextProjectileId)
    {
        var loaded = StateSerializer.ReadLoaded(
            TestStates.Build(0, [TestStates.StandingCharacter(1, 0f, 0f)]),
            StateErrorContext.State);
        CharacterState[] characters =
        [
            loaded.Characters[0] with { ActiveSlot = activeSlot, WeaponCount = checked((byte)config.WeaponSlots) },
        ];

        var weapons = new WeaponState[config.WeaponSlots];
        for (var slot = 0; slot < weapons.Length; slot++)
        {
            var compiled = config.Weapons[slot];
            var fresh = WeaponState.New(compiled.MagazineSize, compiled.ReserveRounds);
            weapons[slot] = weapon is null ? fresh : weapon(slot, fresh);
        }

        Projectile[] flying = projectiles is null ? [] : [.. projectiles];
        var next = nextProjectileId ?? (flying.Length == 0 ? 1u : checked(flying.Max(p => p.Id) + 1u));

        var buffer = new byte[config.MaxStateLength];
        var fault = StateSerializer.Write(
            0,
            next,
            config.WeaponSlots,
            characters,
            weapons,
            flying,
            Span<ShotEvent>.Empty,
            Span<ProjectileHit>.Empty,
            ReadOnlySpan<ExpiryEvent>.Empty,
            ReadOnlySpan<InputRejectionEvent>.Empty,
            ReadOnlySpan<FireBlockEvent>.Empty,
            ReadOnlySpan<MovementOutcomeEvent>.Empty,
            buffer,
            out var length);
        if (fault.Code != StepFaultCode.None)
        {
            throw new InvalidOperationException(
                $"The arranged weapon state does not serialise: {fault.Code} at '{fault.Path}'.");
        }

        return buffer.AsSpan(0, length).ToArray();
    }

    private static WeaponSnapshot Snapshot(StepReport report)
    {
        if (report.Fault.Code != StepFaultCode.None)
        {
            throw new InvalidOperationException(
                $"The step after step {report.Step} faulted with {report.Fault.Code} at '{report.Fault.Path}'.");
        }

        var events = report.Events;
        var shots = new ShotSnapshot[events.Shots.Length];
        for (var i = 0; i < shots.Length; i++)
        {
            var shot = events.Shots[i];
            shots[i] = new ShotSnapshot(
                shot.CharacterId,
                shot.Slot,
                shot.ShotCounter,
                shot.ProjectileId,
                BitConverter.SingleToUInt32Bits(shot.Direction.X),
                BitConverter.SingleToUInt32Bits(shot.Direction.Y),
                BitConverter.SingleToUInt32Bits(shot.Direction.Z));
        }

        var blocks = new FireBlockSnapshot[events.FireBlocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            blocks[i] = new FireBlockSnapshot(events.FireBlocks[i].CharacterId, events.FireBlocks[i].Reason);
        }

        var committed = StateSerializer.ReadCommitted(report.State);
        return new WeaponSnapshot(
            report.Step,
            shots,
            blocks,
            report.Counters.FireBlocks,
            committed.Weapons,
            committed.Characters[0].ActiveSlot,
            committed.NextProjectileId,
            committed.Projectiles.Count);
    }
}

/// <summary>
/// One committed step of a <see cref="WeaponRuns"/> run, copied out of its <see cref="StepReport"/>.
/// </summary>
/// <param name="Step">The report's step number.</param>
/// <param name="Shots">The step's shot events, in event order.</param>
/// <param name="FireBlocks">The step's fire-block events, in event order.</param>
/// <param name="FireBlockCounter">The report's fire-block counter, <c>Counters.FireBlocks</c>.</param>
/// <param name="Weapons">
/// Every committed <see cref="WeaponState"/>, flat by character index × <c>input.weaponSlots</c> + slot,
/// read with <c>StateSerializer.ReadCommitted</c>; with one character, index by slot.
/// </param>
/// <param name="ActiveSlot">The committed <see cref="CharacterState.ActiveSlot"/> of character index 0.</param>
/// <param name="NextProjectileId">The committed state's <c>nextProjectileId</c>.</param>
/// <param name="ProjectileCount">The number of projectiles in the committed state.</param>
public sealed record WeaponSnapshot(
    uint Step,
    IReadOnlyList<ShotSnapshot> Shots,
    IReadOnlyList<FireBlockSnapshot> FireBlocks,
    int FireBlockCounter,
    IReadOnlyList<WeaponState> Weapons,
    byte ActiveSlot,
    uint NextProjectileId,
    int ProjectileCount);

/// <summary>
/// A shot event of a <see cref="WeaponSnapshot"/>: the shooter, the slot, the counter the spread hash
/// used, the projectile it spawned, and its direction's components as
/// <c>BitConverter.SingleToUInt32Bits</c> values, so equal snapshots mean bit-identical directions.
/// </summary>
public readonly record struct ShotSnapshot(
    uint CharacterId,
    byte Slot,
    uint ShotCounter,
    uint ProjectileId,
    uint DirectionX,
    uint DirectionY,
    uint DirectionZ);

/// <summary>A fire-block event of a <see cref="WeaponSnapshot"/>: the character and the reason.</summary>
public readonly record struct FireBlockSnapshot(uint CharacterId, FireBlock Reason);
