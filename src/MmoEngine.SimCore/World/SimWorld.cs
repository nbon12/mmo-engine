using System;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Weapons;

namespace MmoEngine.SimCore.World;

/// <summary>
/// One simulated world: one caller's characters and projectiles over one collision world and one compiled
/// configuration (specs/015-shared-simulation-core/data-model.md#world-simworld). <see cref="Step"/> runs
/// the seven stages of #world-step-order and commits the next state only when every stage succeeds, so a
/// fault leaves the previous state, step and hash as they were (FR-017). Every buffer is allocated once, in
/// the constructor, from the configured maxima and the collision world, so a step allocates nothing
/// (FR-032, SC-005).
/// </summary>
/// <remarks>
/// The world has one writer, its caller, and takes no locks. Characters join and leave only through
/// <see cref="Reset(ReadOnlySpan{byte}, ulong)"/>. Stage 1 validates the inputs and stage 2 steps movement;
/// in this phase the weapon and projectile stages (3 and 5) copy the state forward unchanged, and Phases 6
/// and 7 replace their bodies.
/// </remarks>
public sealed class SimWorld
{
    /// <summary>The capsules a character's hitboxes hold: head, torso and legs.</summary>
    private const int HitboxesPerCharacter = HitboxLayout.CapsuleCount;

    /// <summary>A weapon field's element index is the character index × this + the slot (<see cref="StateSerializer.FieldPath"/>).</summary>
    private const int WeaponPathStride = StateSerializer.MaxWeaponCount;

    private readonly int _weaponSlots;

    // The committed state, and the next state the stages compute; swapped at commit.
    private CharacterState[] _characters;
    private CharacterState[] _nextCharacters;
    private WeaponState[] _weapons;
    private WeaponState[] _nextWeapons;
    private Projectile[] _projectiles;
    private Projectile[] _nextProjectiles;
    private int _characterCount;
    private int _projectileCount;
    private int _nextProjectileCount;
    private uint _nextProjectileId;
    private uint _nextNextProjectileId;
    private uint _step;

    // Stage 1: one accepted or neutral input and one rejection reason per character.
    private readonly InputCommand[] _inputs;
    private readonly InputRejection[] _rejections;
    private int _inputCount;

    // Collision query output, sized for the widest box the configured capsule sweeps.
    private readonly int[] _candidates;

    // Stage 4: three hitboxes and one bounding sphere per character.
    private readonly Hitbox[] _hitboxes;
    private readonly HitboxBounds[] _bounds;

    // This step's six event lists.
    private readonly ShotEvent[] _shots;
    private readonly ProjectileHit[] _hits;
    private readonly ExpiryEvent[] _expiries;
    private readonly InputRejectionEvent[] _inputRejections;
    private readonly FireBlockEvent[] _fireBlocks;
    private readonly MovementOutcomeEvent[] _movementOutcomes;
    private int _shotCount;
    private int _hitCount;
    private int _expiryCount;
    private int _inputRejectionCount;
    private int _fireBlockCount;
    private int _movementOutcomeCount;

    // Stage 2's counters, summed over the characters (StepReport.Counters).
    private int _subMoves;
    private int _sweeps;
    private int _slideCapHits;

    // Stage 6: the canonical serialisation that is hashed and reported.
    private readonly byte[] _stateBuffer;

    /// <summary>
    /// A world over <paramref name="collision"/> with <paramref name="config"/>, every buffer allocated from
    /// the maxima: the current and next characters, weapons (<c>maxCharacters × weaponSlots</c>) and
    /// projectiles; the inputs and rejection reasons; the candidate buffer
    /// <c>int[collision.MaxQueryCandidates((3f * capsuleRadius) + (2f * contactSkin))]</c>; the hitboxes
    /// (<c>maxCharacters × 3</c>) and bounding spheres; the six event buffers; and the
    /// <c>byte[MaxStateLength]</c> state buffer. It starts empty: step 0, no characters, and
    /// <c>nextProjectileId</c> 1.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SimWorld(CompiledConfig config, CollisionWorld collision)
        : this(config, collision, WidestBoxCandidates(config, collision))
    {
    }

    /// <summary>
    /// A world like the public constructor's, except that the candidate buffer holds
    /// <paramref name="candidateCapacity"/> triangle IDs: the tests' way to make a collision query overflow
    /// and see the step refused with <see cref="StepFaultCode.QueryCapacityExceeded"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is negative.</exception>
    internal SimWorld(CompiledConfig config, CollisionWorld collision, int candidateCapacity)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentOutOfRangeException.ThrowIfNegative(candidateCapacity);
        Config = config;
        Collision = collision;
        _weaponSlots = config.WeaponSlots;
        var maxCharacters = config.MaxCharacters;
        var maxProjectiles = config.MaxProjectiles;

        _characters = new CharacterState[maxCharacters];
        _nextCharacters = new CharacterState[maxCharacters];
        _weapons = new WeaponState[maxCharacters * _weaponSlots];
        _nextWeapons = new WeaponState[maxCharacters * _weaponSlots];
        _projectiles = new Projectile[maxProjectiles];
        _nextProjectiles = new Projectile[maxProjectiles];
        _inputs = new InputCommand[maxCharacters];
        _rejections = new InputRejection[maxCharacters];
        _candidates = new int[candidateCapacity];
        _hitboxes = new Hitbox[maxCharacters * HitboxesPerCharacter];
        _bounds = new HitboxBounds[maxCharacters];
        _shots = new ShotEvent[maxCharacters * config.MaxShotsPerStepMax];
        _hits = new ProjectileHit[maxProjectiles];
        _expiries = new ExpiryEvent[maxProjectiles];
        _inputRejections = new InputRejectionEvent[maxCharacters];
        _fireBlocks = new FireBlockEvent[maxCharacters];
        _movementOutcomes = new MovementOutcomeEvent[maxCharacters];
        _stateBuffer = new byte[config.MaxStateLength];
        _nextProjectileId = 1;
    }

    /// <summary>The compiled configuration, fixed for the world's life.</summary>
    public CompiledConfig Config { get; }

    /// <summary>The collision world, fixed for the world's life.</summary>
    public CollisionWorld Collision { get; }

    /// <summary>The match salt set with the state at <see cref="Reset(ReadOnlySpan{byte}, ulong)"/>; an input to every shot's spread.</summary>
    public ulong MatchSalt { get; private set; }

    /// <summary>The step number of the committed state.</summary>
    public uint CurrentStep => _step;

    /// <summary>The inputs stage 1 gave each character in the last step, by character index.</summary>
    internal ReadOnlySpan<InputCommand> LastStepInputs => _inputs.AsSpan(0, _inputCount);

    /// <summary>The committed characters in index order: what the last committed step or <c>Reset</c> left.</summary>
    internal ReadOnlySpan<CharacterState> CommittedCharacters => _characters.AsSpan(0, _characterCount);

    /// <summary>The committed projectiles in ascending ID order.</summary>
    internal ReadOnlySpan<Projectile> CommittedProjectiles => _projectiles.AsSpan(0, _projectileCount);

    /// <summary>The candidate buffer's capacity.</summary>
    internal int CandidateCapacity => _candidates.Length;

    /// <summary>The state buffer's length: the configuration's <c>MaxStateLength</c>.</summary>
    internal int StateBufferLength => _stateBuffer.Length;

    /// <summary>The shot event buffer's capacity: <c>maxCharacters × MaxShotsPerStepMax</c>.</summary>
    internal int ShotCapacity => _shots.Length;

    /// <summary>The hit event buffer's capacity: <c>maxProjectiles</c>.</summary>
    internal int HitCapacity => _hits.Length;

    /// <summary>The expiry event buffer's capacity: <c>maxProjectiles</c>.</summary>
    internal int ExpiryCapacity => _expiries.Length;

    /// <summary>The input rejection event buffer's capacity: <c>maxCharacters</c>.</summary>
    internal int InputRejectionCapacity => _inputRejections.Length;

    /// <summary>The fire block event buffer's capacity: <c>maxCharacters</c>.</summary>
    internal int FireBlockCapacity => _fireBlocks.Length;

    /// <summary>The movement outcome event buffer's capacity: <c>maxCharacters</c>.</summary>
    internal int MovementOutcomeCapacity => _movementOutcomes.Length;

    /// <summary>
    /// Loads a canonical state with no events, and the match salt that travels beside it
    /// (contracts/state-serialization.md#loading-a-state). It runs the byte-only checks
    /// (<see cref="StateSerializer.ReadLoaded"/>, format <c>state</c>), then the checks that need the
    /// configuration and the collision world, and only then copies the state in.
    /// </summary>
    /// <exception cref="CoreFormatException">The state is refused; the world keeps what it had.</exception>
    public void Reset(ReadOnlySpan<byte> canonicalState, ulong matchSalt) =>
        Reset(canonicalState, matchSalt, StateErrorContext.State);

    /// <summary>
    /// <see cref="Reset(ReadOnlySpan{byte}, ulong)"/> with the caller's error context, which both the
    /// byte-only checks and the configuration and world checks use for the error's format, base offset and
    /// field prefix: <c>StreamRunner</c> passes a stream's initial state context.
    /// </summary>
    /// <exception cref="CoreFormatException">The state is refused; the world keeps what it had.</exception>
    internal void Reset(ReadOnlySpan<byte> canonicalState, ulong matchSalt, in StateErrorContext context)
    {
        var snapshot = StateSerializer.ReadLoaded(canonicalState, context);
        CheckFits(snapshot, context);

        var characters = snapshot.Characters;
        var weapons = snapshot.Weapons;
        var projectiles = snapshot.Projectiles;
        for (var i = 0; i < characters.Count; i++)
        {
            _characters[i] = characters[i];
        }

        // Every character has weaponSlots weapons, so record order is character index × weaponSlots + slot.
        for (var w = 0; w < weapons.Count; w++)
        {
            _weapons[w] = weapons[w];
        }

        for (var j = 0; j < projectiles.Count; j++)
        {
            _projectiles[j] = projectiles[j];
        }

        _characterCount = characters.Count;
        _projectileCount = projectiles.Count;
        _nextProjectileId = snapshot.NextProjectileId;
        _step = snapshot.Step;
        MatchSalt = matchSalt;
        _inputCount = 0;
    }

    /// <summary>
    /// Computes step n = <see cref="CurrentStep"/> + 1 from <paramref name="commands"/> (ascending character
    /// index, at most one per character): stage 1 validates the commands and merges them with the
    /// characters, stages 2 to 5 step movement, weapons, hitboxes and projectiles, stage 6 canonicalises,
    /// checks, serialises and hashes the next state, and stage 7 commits it. A fault (a collision query
    /// that overflows the candidate buffer in stage 2, or a non-finite float in stage 6) commits nothing
    /// and reports hash 0 with the previous step. It allocates nothing and throws nothing for any commands.
    /// </summary>
    public StepReport Step(ReadOnlySpan<CharacterCommand> commands)
    {
        var fault = RunStagesOneToFive(commands);
        return fault.Code == StepFaultCode.None ? SerialiseAndCommit(unchecked(_step + 1u)) : Refused(fault);
    }

    /// <summary>
    /// <see cref="Step"/> with <paramref name="bits"/> written into one field of the next state after stage
    /// 5, before stage 6 checks and serialises it: the canonicalisation and finite-check tests' way to
    /// reach stage 6 with a value no stage produces. The field is a character, weapon or projectile record
    /// field, named as <see cref="StateSerializer.FieldPath"/> names it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The field is not a character, weapon or projectile field of a present element.</exception>
    internal StepReport StepWithInjectedBits(ReadOnlySpan<CharacterCommand> commands, int fieldOrdinal, int elementIndex, uint bits)
    {
        CheckInjectable(fieldOrdinal, elementIndex);
        var fault = RunStagesOneToFive(commands);
        if (fault.Code != StepFaultCode.None)
        {
            return Refused(fault);
        }

        Inject(fieldOrdinal, elementIndex, bits);
        return SerialiseAndCommit(unchecked(_step + 1u));
    }

    /// <summary>
    /// The one-ulp perturbation of SC-002 (contracts/wasm-api.md#streamrunner): applies
    /// <see cref="MathF.BitIncrement"/> to the committed <c>position.x</c> of character index 0, then re-runs
    /// stage 6 on that state with the committed step's events (canonicalise, finite check, serialise, hash)
    /// and recommits it under the same step number. Later steps continue from the perturbed state. A fault
    /// (a position that becomes non-finite) commits nothing and reports hash 0 with the committed step.
    /// </summary>
    /// <exception cref="InvalidOperationException">The world has no character.</exception>
    internal StepReport PerturbFirstCharacterPositionX()
    {
        if (_characterCount == 0)
        {
            throw new InvalidOperationException("The world has no character to perturb.");
        }

        // The committed state becomes the next state, unchanged but for the one field; the event buffers
        // still hold the committed step's events, because only the next step's BeginStep empties them.
        _characters.AsSpan(0, _characterCount).CopyTo(_nextCharacters);
        _weapons.AsSpan(0, _characterCount * _weaponSlots).CopyTo(_nextWeapons);
        _projectiles.AsSpan(0, _projectileCount).CopyTo(_nextProjectiles);
        _nextProjectileCount = _projectileCount;
        _nextNextProjectileId = _nextProjectileId;
        ref var first = ref _nextCharacters[0];
        var position = first.Position;
        first.Position = new Vec3(MathF.BitIncrement(position.X), position.Y, position.Z);
        return SerialiseAndCommit(_step);
    }

    private static int WidestBoxCandidates(CompiledConfig config, CollisionWorld collision)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(collision);
        return collision.MaxQueryCandidates((3f * config.CapsuleRadius) + (2f * config.ContactSkin));
    }

    // ---------------------------------------------------------------- the stages

    /// <summary>Stages 1 to 5. A stage that faults stops the step there and returns its fault.</summary>
    private StepFault RunStagesOneToFive(ReadOnlySpan<CharacterCommand> commands)
    {
        BeginStep();
        MergeInputs(commands);
        var fault = StepMovement();
        if (fault.Code != StepFaultCode.None)
        {
            return fault;
        }

        StepWeapons();
        BuildHitboxes();
        StepProjectiles();
        return default;
    }

    /// <summary>Empties this step's event lists and counters, so a refused step reports only its own work.</summary>
    private void BeginStep()
    {
        _shotCount = 0;
        _hitCount = 0;
        _expiryCount = 0;
        _inputRejectionCount = 0;
        _fireBlockCount = 0;
        _movementOutcomeCount = 0;
        _subMoves = 0;
        _sweeps = 0;
        _slideCapHits = 0;
    }

    /// <summary>
    /// Stage 1: merges the ascending commands with the characters, taking at most one per character, and
    /// validates each supplied command against its character with <see cref="InputValidator.Validate"/>
    /// (the character's <c>LastSequence</c> and <c>input.weaponSlots</c>). An accepted command is the
    /// character's input, so stage 2 sets its <c>AimYaw</c>, <c>AimPitch</c> and <c>LastSequence</c> from
    /// it. A rejected command adds <c>{CharacterId, Reason}</c> to the input rejection events, in ascending
    /// character ID, and the character, like one with no command, gets the neutral input. Only the first
    /// <c>characterCount</c> commands are read, a command whose index is below the character being merged
    /// is skipped, and one whose index is past the last character is ignored.
    /// </summary>
    private void MergeInputs(ReadOnlySpan<CharacterCommand> commands)
    {
        var count = _characterCount;
        var limit = commands.Length < count ? commands.Length : count;
        var k = 0;
        for (var i = 0; i < count; i++)
        {
            while (k < limit && commands[k].CharacterIndex < i)
            {
                k++;
            }

            ref readonly var character = ref _characters[i];
            var reason = InputRejection.None;
            if (k < limit && commands[k].CharacterIndex == i)
            {
                var command = commands[k].Command;
                k++;
                reason = InputValidator.Validate(in command, character.LastSequence, _weaponSlots);
                _inputs[i] = reason == InputRejection.None ? command : Neutral(in character);
                if (reason != InputRejection.None)
                {
                    _inputRejections[_inputRejectionCount] = new InputRejectionEvent { CharacterId = character.Id, Reason = reason };
                    _inputRejectionCount++;
                }
            }
            else
            {
                _inputs[i] = Neutral(in character);
            }

            _rejections[i] = reason;
        }

        _inputCount = count;
    }

    /// <summary>No buttons, the character's aim and active slot, and its <c>LastSequence</c>.</summary>
    private static InputCommand Neutral(in CharacterState character) =>
        new(character.LastSequence, 0, 0, character.AimYaw, character.AimPitch, character.ActiveSlot);

    /// <summary>
    /// Stage 2: movement, in ascending character ID. Each next-state character is stepped by
    /// <see cref="MovementStep.Step"/> with its stage 1 input and the world's candidate buffer; a non-zero
    /// outcome adds <c>{CharacterId, Flags}</c> to the movement outcome events, and the sub-moves, sweeps and
    /// slide-cap hits are summed into the step's counters. A collision query that overflows the candidate
    /// buffer stops the stage with <see cref="StepFaultCode.QueryCapacityExceeded"/>, naming the character
    /// by its <c>id</c> field and element index, and the step commits nothing.
    /// </summary>
    private StepFault StepMovement()
    {
        var count = _characterCount;
        _characters.AsSpan(0, count).CopyTo(_nextCharacters);
        for (var i = 0; i < count; i++)
        {
            ref var character = ref _nextCharacters[i];
            var result = MovementStep.Step(ref character, in _inputs[i], Config, Collision, _candidates);
            _subMoves += result.SubMoves;
            _sweeps += result.Sweeps;
            _slideCapHits += result.SlideCapHits;
            if (result.Fault != StepFaultCode.None)
            {
                return new StepFault(result.Fault, StateSerializer.CharacterIdField, i);
            }

            if (result.Outcome != MovementOutcome.None)
            {
                _movementOutcomes[_movementOutcomeCount] = new MovementOutcomeEvent { CharacterId = character.Id, Flags = result.Outcome };
                _movementOutcomeCount++;
            }
        }

        return default;
    }

    /// <summary>
    /// Stage 3: weapons, in ascending character ID. This phase copies the weapons and
    /// <c>nextProjectileId</c> forward unchanged and fires nothing.
    /// </summary>
    private void StepWeapons()
    {
        _weapons.AsSpan(0, _characterCount * _weaponSlots).CopyTo(_nextWeapons);
        _nextNextProjectileId = _nextProjectileId;
        _shotCount = 0;
        _fireBlockCount = 0;
    }

    /// <summary>Stage 4: each next character's hitboxes and bounding sphere, from its position, aim yaw and stance.</summary>
    private void BuildHitboxes()
    {
        var layouts = Config.HitboxLayouts;
        for (var i = 0; i < _characterCount; i++)
        {
            ref readonly var character = ref _nextCharacters[i];
            _bounds[i] = HitboxBuilder.Build(
                layouts[(int)character.Stance],
                character.Position,
                character.AimYaw,
                _hitboxes.AsSpan(i * HitboxesPerCharacter, HitboxesPerCharacter));
        }
    }

    /// <summary>Stage 5: projectiles, in ascending projectile ID. This phase copies them forward unchanged.</summary>
    private void StepProjectiles()
    {
        _projectiles.AsSpan(0, _projectileCount).CopyTo(_nextProjectiles);
        _nextProjectileCount = _projectileCount;
        _hitCount = 0;
        _expiryCount = 0;
    }

    /// <summary>
    /// Stage 6 canonicalises, checks and serialises the next state with its events, and hashes it; stage 7
    /// swaps the next state in and sets the step to <paramref name="step"/>: the step after the committed
    /// one, or the committed one itself when <see cref="PerturbFirstCharacterPositionX"/> recommits it. A
    /// non-finite float stops at stage 6 and commits nothing.
    /// </summary>
    private StepReport SerialiseAndCommit(uint step)
    {
        var fault = StateSerializer.Write(
            step,
            _nextNextProjectileId,
            _weaponSlots,
            _nextCharacters.AsSpan(0, _characterCount),
            _nextWeapons.AsSpan(0, _characterCount * _weaponSlots),
            _nextProjectiles.AsSpan(0, _nextProjectileCount),
            _shots.AsSpan(0, _shotCount),
            _hits.AsSpan(0, _hitCount),
            _expiries.AsSpan(0, _expiryCount),
            _inputRejections.AsSpan(0, _inputRejectionCount),
            _fireBlocks.AsSpan(0, _fireBlockCount),
            _movementOutcomes.AsSpan(0, _movementOutcomeCount),
            _stateBuffer,
            out var length);
        if (fault.Code != StepFaultCode.None)
        {
            return Refused(fault);
        }

        var state = new ReadOnlySpan<byte>(_stateBuffer, 0, length);
        var hash = StateHash.Compute(state);

        // Stage 7: commit.
        (_characters, _nextCharacters) = (_nextCharacters, _characters);
        (_weapons, _nextWeapons) = (_nextWeapons, _weapons);
        (_projectiles, _nextProjectiles) = (_nextProjectiles, _projectiles);
        _projectileCount = _nextProjectileCount;
        _nextProjectileId = _nextNextProjectileId;
        _step = step;

        var events = new StepEvents(
            _shots.AsSpan(0, _shotCount),
            _hits.AsSpan(0, _hitCount),
            _expiries.AsSpan(0, _expiryCount),
            _inputRejections.AsSpan(0, _inputRejectionCount),
            _fireBlocks.AsSpan(0, _fireBlockCount),
            _movementOutcomes.AsSpan(0, _movementOutcomeCount));
        return new StepReport(step, default, hash, state, events, Counters());
    }

    /// <summary>A refused step's report: the previous step number, the fault, hash 0, no state and no events.</summary>
    private StepReport Refused(StepFault fault) =>
        new(_step, fault, 0, ReadOnlySpan<byte>.Empty, default, Counters());

    /// <summary>The work this step did so far: stage 2's sums and stage 3's fire blocks.</summary>
    private StepCounters Counters() => new()
    {
        SubMoves = _subMoves,
        Sweeps = _sweeps,
        SlideCapHits = _slideCapHits,
        FireBlocks = _fireBlockCount,
    };

    // ---------------------------------------------------------------- the configuration and world checks

    /// <summary>
    /// The checks of contracts/state-serialization.md#loading-a-state that need the compiled configuration
    /// and the collision world, in layout order: the counts against the maxima; then each character's
    /// position inside the playable area, its ground normal (grounded: y ≥ <c>CosSlopeLimit</c>; airborne:
    /// exactly (0, 1, 0)), its <c>weaponCount</c> against <c>input.weaponSlots</c>, and each weapon (a
    /// holstered weapon is Ready or Overheated, <c>phaseSteps</c> in its phase's range,
    /// 0 ≤ <c>fireWait</c> &lt; <c>ShotCost</c>, <c>magazine</c> ≤ <c>magazineSize</c>, <c>heat</c> below
    /// the overheat threshold plus one shot's heat, and the recoil bounds); then each projectile's class.
    /// </summary>
    private void CheckFits(StateSnapshot snapshot, in StateErrorContext context)
    {
        var characters = snapshot.Characters;
        var weapons = snapshot.Weapons;
        var projectiles = snapshot.Projectiles;
        if (characters.Count > Config.MaxCharacters)
        {
            throw Refusal(context, FormatErrorCode.Oversized, StateSerializer.CharacterCountField, 0, 4, Text($"{characters.Count} is above world.maxCharacters {Config.MaxCharacters}"));
        }

        if (projectiles.Count > Config.MaxProjectiles)
        {
            throw Refusal(context, FormatErrorCode.Oversized, StateSerializer.ProjectileCountField, 0, 6, Text($"{projectiles.Count} is above world.maxProjectiles {Config.MaxProjectiles}"));
        }

        var heightfield = Collision.Heightfield;
        var offset = StateSerializer.HeaderLength;
        var weapon = 0;
        for (var i = 0; i < characters.Count; i++)
        {
            var character = characters[i];
            var position = character.Position;
            if (position.X < heightfield.MinX || position.X > heightfield.MaxX)
            {
                throw OutsideArea(context, StateSerializer.PositionXField, i, offset + 4, position.X, heightfield.MinX, heightfield.MaxX);
            }

            if (position.Z < heightfield.MinZ || position.Z > heightfield.MaxZ)
            {
                throw OutsideArea(context, StateSerializer.PositionXField + 2, i, offset + 12, position.Z, heightfield.MinZ, heightfield.MaxZ);
            }

            var normal = character.GroundNormal;
            if ((character.Flags & CharacterFlags.Grounded) != 0)
            {
                if (normal.Y < Config.CosSlopeLimit)
                {
                    throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.GroundNormalXField + 1, i, offset + 32, Text($"{normal.Y} is below CosSlopeLimit {Config.CosSlopeLimit}, so the ground is not walkable"));
                }
            }
            else if (normal.X != 0f || normal.Y != 1f || normal.Z != 0f)
            {
                throw Refusal(context, FormatErrorCode.Inconsistent, StateSerializer.GroundNormalXField, i, offset + 28, Text($"an airborne character's ground normal is {normal}, not (0, 1, 0)"));
            }

            if (character.WeaponCount != _weaponSlots)
            {
                throw Refusal(context, FormatErrorCode.Inconsistent, StateSerializer.WeaponCountField, i, offset + 51, Text($"{character.WeaponCount} is not input.weaponSlots {_weaponSlots}"));
            }

            var record = offset + StateSerializer.CharacterFixedLength;
            for (var s = 0; s < _weaponSlots; s++)
            {
                CheckWeapon(weapons[weapon], Config.Weapons[s], s != character.ActiveSlot, (i * WeaponPathStride) + s, record, context);
                weapon++;
                record += StateSerializer.WeaponLength;
            }

            offset = record;
        }

        for (var j = 0; j < projectiles.Count; j++)
        {
            var projectileClass = projectiles[j].Class;
            if (projectileClass >= Config.ProjectileClassCount)
            {
                throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.ClassField, j, offset + 8, Text($"{projectileClass} is not below the {Config.ProjectileClassCount} projectile classes"));
            }

            offset += StateSerializer.ProjectileLength;
        }
    }

    /// <summary>
    /// The weapon record's rules, in layout order: phase (a holstered weapon is Ready or Overheated),
    /// phaseSteps (the range its phase allows, <see cref="PhaseStepsRange"/>), fireWait, magazine, heat
    /// (below <c>OverheatThresholdScaled + HeatPerShotScaled</c>), recoilPitch and recoilYaw.
    /// </summary>
    private void CheckWeapon(WeaponState state, CompiledWeapon weapon, bool holstered, int index, int offset, in StateErrorContext context)
    {
        if (holstered && state.Phase is not (WeaponPhase.Ready or WeaponPhase.Overheated))
        {
            throw Refusal(context, FormatErrorCode.Inconsistent, StateSerializer.PhaseField, index, offset, Text($"a holstered weapon is Ready or Overheated, not {state.Phase}"));
        }

        var (minSteps, maxSteps) = PhaseStepsRange(state.Phase, holstered, weapon);
        if (state.PhaseSteps < minSteps || state.PhaseSteps > maxSteps)
        {
            var which = holstered ? "a holstered" : "an active";
            throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.PhaseStepsField, index, offset + 2, Text($"{state.PhaseSteps} is outside [{minSteps}, {maxSteps}] for {which} {state.Phase} weapon"));
        }

        if (state.FireWait < 0 || state.FireWait >= Config.ShotCost)
        {
            throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.FireWaitField, index, offset + 4, Text($"{state.FireWait} is outside [0, {Config.ShotCost - 1}]"));
        }

        if (state.Magazine > weapon.MagazineSize)
        {
            throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.MagazineField, index, offset + 12, Text($"{state.Magazine} is above magazineSize {weapon.MagazineSize}"));
        }

        // Both terms are at most 6 × 10⁸ (contracts/config-rules.md), so the sum fits in a uint.
        var heatLimit = weapon.OverheatThresholdScaled + weapon.HeatPerShotScaled;
        if (state.Heat >= heatLimit)
        {
            throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.HeatField, index, offset + 16, Text($"{state.Heat} is not below the overheat threshold plus one shot's heat, {heatLimit}"));
        }

        if (state.RecoilPitch < 0 || state.RecoilPitch > weapon.RecoilMaxPitchUnits)
        {
            throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.RecoilPitchField, index, offset + 22, Text($"{state.RecoilPitch} is outside [0, {weapon.RecoilMaxPitchUnits}]"));
        }

        if (state.RecoilYaw < -weapon.RecoilMaxYawUnits || state.RecoilYaw > weapon.RecoilMaxYawUnits)
        {
            throw Refusal(context, FormatErrorCode.OutOfRange, StateSerializer.RecoilYawField, index, offset + 26, Text($"{state.RecoilYaw} is outside [{-weapon.RecoilMaxYawUnits}, {weapon.RecoilMaxYawUnits}]"));
        }
    }

    /// <summary>
    /// The <c>phaseSteps</c> a committed state can hold in each phase (data-model.md#weapon-phases): 0 in
    /// Ready and Firing; [1, <c>EquipSteps</c>] in Switching and [1, <c>ReloadSteps</c>] in Reloading,
    /// because reaching 0 completes them and a duration of 0 completes at entry; [0, <c>EquipSteps</c>]
    /// for the active weapon while Overheated (the equip time still owed); and 0 for a holstered
    /// Overheated weapon, because switching away clears it and only the active weapon counts down.
    /// </summary>
    private static (int Min, int Max) PhaseStepsRange(WeaponPhase phase, bool holstered, CompiledWeapon weapon) => phase switch
    {
        WeaponPhase.Switching => (1, weapon.EquipSteps),
        WeaponPhase.Reloading => (1, weapon.ReloadSteps),
        WeaponPhase.Overheated => (0, holstered ? 0 : weapon.EquipSteps),
        _ => (0, 0),
    };

    private static CoreFormatException OutsideArea(in StateErrorContext context, int ordinal, int index, int offset, float value, float min, float max) =>
        new(new FormatError(
            context.Format,
            FormatErrorCode.OutOfRange,
            context.BaseOffset + offset,
            context.FieldPrefix + StateSerializer.FieldPath(ordinal, index),
            FormatDetail.OutOfRangeFloat,
            BitConverter.SingleToUInt32Bits(value),
            BitConverter.SingleToUInt32Bits(min),
            BitConverter.SingleToUInt32Bits(max)));

    private static CoreFormatException Refusal(in StateErrorContext context, FormatErrorCode code, int ordinal, int index, int offset, string detail) =>
        new(new FormatError(context.Format, code, context.BaseOffset + offset, context.FieldPrefix + StateSerializer.FieldPath(ordinal, index), detail));

    private static string Text(FormattableString text) => FormattableString.Invariant(text);

    // ---------------------------------------------------------------- field injection (tests only)

    private void CheckInjectable(int fieldOrdinal, int elementIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldOrdinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldOrdinal, StateSerializer.Layout.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        switch (StateSerializer.Layout[fieldOrdinal].Record)
        {
            case StateRecordKind.Character:
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(elementIndex, _characterCount);
                break;
            case StateRecordKind.Weapon:
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(elementIndex / WeaponPathStride, _characterCount);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(elementIndex % WeaponPathStride, _weaponSlots);
                break;
            case StateRecordKind.Projectile:
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(elementIndex, _projectileCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fieldOrdinal), fieldOrdinal, "Only character, weapon and projectile fields can be injected.");
        }
    }

    private void Inject(int fieldOrdinal, int elementIndex, uint bits)
    {
        switch (StateSerializer.Layout[fieldOrdinal].Record)
        {
            case StateRecordKind.Character:
                InjectCharacter(ref _nextCharacters[elementIndex], fieldOrdinal, bits);
                break;
            case StateRecordKind.Weapon:
                var flat = ((elementIndex / WeaponPathStride) * _weaponSlots) + (elementIndex % WeaponPathStride);
                InjectWeapon(ref _nextWeapons[flat], fieldOrdinal, bits);
                break;
            default:
                InjectProjectile(ref _nextProjectiles[elementIndex], fieldOrdinal, bits);
                break;
        }
    }

    private static void InjectCharacter(ref CharacterState c, int ordinal, uint bits)
    {
        switch (ordinal)
        {
            case StateSerializer.CharacterIdField:
                c.Id = bits;
                break;
            case >= StateSerializer.PositionXField and < StateSerializer.VelocityXField:
                c.Position = WithComponent(c.Position, ordinal - StateSerializer.PositionXField, bits);
                break;
            case >= StateSerializer.VelocityXField and < StateSerializer.GroundNormalXField:
                c.Velocity = WithComponent(c.Velocity, ordinal - StateSerializer.VelocityXField, bits);
                break;
            case >= StateSerializer.GroundNormalXField and < StateSerializer.AimYawField:
                c.GroundNormal = WithComponent(c.GroundNormal, ordinal - StateSerializer.GroundNormalXField, bits);
                break;
            case StateSerializer.AimYawField:
                c.AimYaw = (ushort)bits;
                break;
            case StateSerializer.AimPitchField:
                c.AimPitch = (short)(ushort)bits;
                break;
            case StateSerializer.StanceField:
                c.Stance = (Stance)(byte)bits;
                break;
            case StateSerializer.CharacterFlagsField:
                c.Flags = (CharacterFlags)(byte)bits;
                break;
            case StateSerializer.LastSequenceField:
                c.LastSequence = bits;
                break;
            case StateSerializer.ActiveSlotField:
                c.ActiveSlot = (byte)bits;
                break;
            default:
                c.WeaponCount = (byte)bits;
                break;
        }
    }

    private static void InjectWeapon(ref WeaponState w, int ordinal, uint bits)
    {
        switch (ordinal)
        {
            case StateSerializer.PhaseField:
                w.Phase = (WeaponPhase)(byte)bits;
                break;
            case StateSerializer.WeaponFlagsField:
                w.Flags = (WeaponFlags)(byte)bits;
                break;
            case StateSerializer.PhaseStepsField:
                w.PhaseSteps = (ushort)bits;
                break;
            case StateSerializer.FireWaitField:
                w.FireWait = unchecked((int)bits);
                break;
            case StateSerializer.ShotCounterField:
                w.ShotCounter = bits;
                break;
            case StateSerializer.MagazineField:
                w.Magazine = (ushort)bits;
                break;
            case StateSerializer.ReserveField:
                w.Reserve = (ushort)bits;
                break;
            case StateSerializer.HeatField:
                w.Heat = bits;
                break;
            case StateSerializer.StepsSinceShotField:
                w.StepsSinceShot = (ushort)bits;
                break;
            case StateSerializer.RecoilPitchField:
                w.RecoilPitch = unchecked((int)bits);
                break;
            default:
                w.RecoilYaw = unchecked((int)bits);
                break;
        }
    }

    private static void InjectProjectile(ref Projectile p, int ordinal, uint bits)
    {
        switch (ordinal)
        {
            case StateSerializer.ProjectileIdField:
                p.Id = bits;
                break;
            case StateSerializer.OwnerIdField:
                p.OwnerId = bits;
                break;
            case StateSerializer.ClassField:
                p.Class = (byte)bits;
                break;
            case StateSerializer.LaunchStepField:
                p.LaunchStep = bits;
                break;
            case >= StateSerializer.OriginXField and < StateSerializer.LaunchVelocityXField:
                p.Origin = WithComponent(p.Origin, ordinal - StateSerializer.OriginXField, bits);
                break;
            case >= StateSerializer.LaunchVelocityXField and < StateSerializer.ProjectilePositionXField:
                p.LaunchVelocity = WithComponent(p.LaunchVelocity, ordinal - StateSerializer.LaunchVelocityXField, bits);
                break;
            default:
                p.Position = WithComponent(p.Position, ordinal - StateSerializer.ProjectilePositionXField, bits);
                break;
        }
    }

    /// <summary>The vector with component <paramref name="axis"/> (0 x, 1 y, 2 z) replaced by the float with <paramref name="bits"/>.</summary>
    private static Vec3 WithComponent(Vec3 vector, int axis, uint bits)
    {
        var value = BitConverter.UInt32BitsToSingle(bits);
        return axis switch
        {
            0 => new Vec3(value, vector.Y, vector.Z),
            1 => new Vec3(vector.X, value, vector.Z),
            _ => new Vec3(vector.X, vector.Y, value),
        };
    }
}
