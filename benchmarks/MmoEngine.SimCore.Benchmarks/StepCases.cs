using System.Buffers.Binary;
using System.Globalization;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// One named case of one step function, the operation the server benchmarks time and the zero-allocation
/// tests count (FR-030, FR-032; specs/015-shared-simulation-core/contracts/measurement-report.md#how-the-report-is-produced).
/// <see cref="Create"/> builds the case's scenario definition with <see cref="ScenarioCatalog"/> over
/// <see cref="SimCoreContent"/> at the rate and precomputes every input, state and buffer its pass needs,
/// so <see cref="Step"/> and <see cref="Restart"/> only copy structs and call the core's own entry point,
/// the same way <see cref="SimWorld"/> calls it:
/// <list type="bullet">
/// <item><c>movement</c>: character 0 of the stream's initial state, decoded with
/// <see cref="StateSerializer.ReadLoaded"/>, stepped by <see cref="MovementStep.Step"/> with each step's
/// stage 1 input (the command, or the neutral input when there is none or it is rejected) against a
/// candidate buffer sized as the world sizes its own. The pass is every step of the stream.</item>
/// <item><c>weapon</c>: <c>rifle-burst</c>'s initial weapons, stepped by <see cref="WeaponStep.Step"/> with
/// each step's stage 1 input and the character as the world's movement stage leaves it, with a projectile
/// scratch whose count is reset to 0 each step. The pass is every step of the stream.</item>
/// <item><c>projectile</c>: the projectile <c>launcher-arc</c> fires, as the canonical state after its firing
/// step holds it (read from a <see cref="SimWorld"/> run), stepped by <see cref="ProjectileStep.Step"/>
/// against the hitboxes of that state's characters, built once with <see cref="HitboxBuilder"/>. The pass
/// runs from the step after the firing step to the step on which the projectile hits or expires.</item>
/// <item><c>hitboxBuild</c>: one <c>flat-run-&lt;hz&gt;hz</c> pose of character 0 per step, as the world
/// committed it, built by <see cref="HitboxBuilder.Build"/> into a preallocated span.</item>
/// <item><c>stateHash</c>: <c>flat-run-&lt;hz&gt;hz</c>'s canonical state per step, as the world committed
/// it, hashed with <see cref="StateHash.Compute"/>.</item>
/// <item><c>world</c>: a <see cref="SimWorld"/> stepped with one <see cref="CharacterCommand"/> array per
/// stream entry; <see cref="Restart"/> calls <see cref="SimWorld.Reset(ReadOnlySpan{byte}, ulong)"/>.</item>
/// </list>
/// Besides the pass, the case exposes what the last <see cref="Step"/> did, each a plain read of a field the
/// step wrote. <c>StepCaseTests</c> checks every case against a <see cref="SimWorld"/> run of the same
/// definition, and <c>StepAllocationTests</c> that a warmed-up <see cref="Step"/> allocates nothing.
/// </summary>
public sealed class StepCase
{
    /// <summary>The movement step's kind.</summary>
    public const string Movement = "movement";

    /// <summary>The weapon step's kind.</summary>
    public const string Weapon = "weapon";

    /// <summary>The projectile step's kind.</summary>
    public const string ProjectileKind = "projectile";

    /// <summary>The hitbox build's kind.</summary>
    public const string HitboxBuild = "hitboxBuild";

    /// <summary>The state hash's kind.</summary>
    public const string StateHashKind = "stateHash";

    /// <summary>The whole world step's kind.</summary>
    public const string WorldKind = "world";

    private readonly Driver _driver;
    private int _index;

    private uint _lastStep;
    private CharacterState _character;
    private int _lastShotCount;
    private Projectile _projectile;
    private ProjectileOutcome _lastOutcome;
    private ProjectileHit _lastHit;
    private ExpiryReason _lastExpiry;
    private StepFault _lastFault;
    private ulong _lastStepHash;

    private StepCase(string kind, string caseName, int hz, Driver driver)
    {
        Kind = kind;
        CaseName = caseName;
        StepRateHz = hz;
        _driver = driver;
        PassLength = driver.PassLength;
        _driver.Restart(this);
    }

    /// <summary>The step function: <c>movement</c>, <c>weapon</c>, <c>projectile</c>, <c>hitboxBuild</c>, <c>stateHash</c> or <c>world</c>.</summary>
    public string Kind { get; }

    /// <summary>The case's name within its kind, such as <c>flat-run</c>.</summary>
    public string CaseName { get; }

    /// <summary>The step rate the case was built at.</summary>
    public int StepRateHz { get; }

    /// <summary>The number of <see cref="Step"/> calls in one pass; at least 1.</summary>
    public int PassLength { get; }

    /// <summary>The <see cref="Step"/> calls left in the current pass: <see cref="PassLength"/> after <see cref="Restart"/>, then one fewer per step.</summary>
    public int StepsLeftInPass => PassLength - _index;

    /// <summary>The step number the last <see cref="Step"/> computed; 0 before the first.</summary>
    public uint LastStep => _lastStep;

    /// <summary><c>movement</c> and <c>weapon</c>: character 0 as the last step left it (the initial character before the first).</summary>
    public CharacterState Character => _character;

    /// <summary><c>weapon</c>: the shots the last step fired, <see cref="WeaponStep.Step"/>'s return.</summary>
    public int LastShotCount => _lastShotCount;

    /// <summary><c>projectile</c>: the projectile as the last step left it (the launched projectile before the first).</summary>
    public Projectile Projectile => _projectile;

    /// <summary><c>projectile</c>: <see cref="ProjectileStep.Step"/>'s return in the last step.</summary>
    public ProjectileOutcome LastOutcome => _lastOutcome;

    /// <summary><c>projectile</c>: the hit of the last step, when <see cref="LastOutcome"/> is <see cref="ProjectileOutcome.Hit"/>.</summary>
    public ProjectileHit LastHit => _lastHit;

    /// <summary><c>projectile</c>: the expiry reason of the last step, when <see cref="LastOutcome"/> is <see cref="ProjectileOutcome.Expired"/>.</summary>
    public ExpiryReason LastExpiry => _lastExpiry;

    /// <summary><c>world</c>: the last <see cref="StepReport"/>'s <see cref="StepReport.Fault"/>.</summary>
    public StepFault LastFault => _lastFault;

    /// <summary><c>world</c>: the last <see cref="StepReport"/>'s <see cref="StepReport.Hash"/>; <c>stateHash</c>: the last hash computed.</summary>
    public ulong LastStepHash => _lastStepHash;

    /// <summary>
    /// The XxHash64 (seed 0, <see cref="StateHash.Compute"/>) of the case's output state bytes as the last
    /// step left them: for <c>movement</c>, <c>weapon</c> and <c>projectile</c> the canonical state
    /// (<see cref="StateSerializer.Write"/>) of the case's characters, weapons, projectiles and the last
    /// step's events; for <c>hitboxBuild</c> the posed hitboxes and bounding sphere, every float as its
    /// little-endian bits; for <c>stateHash</c> and <c>world</c> the last state hashed, so the hash itself.
    /// It is computed when read, never on the step path.
    /// </summary>
    public ulong LastResultHash => _driver.ResultHash(this);

    /// <summary>
    /// The case <paramref name="caseName"/> of <paramref name="kind"/> at <paramref name="hz"/>, built from
    /// its scenario definition: <c>movement</c> <c>flat-run</c> (<c>flat-run-&lt;hz&gt;hz</c>),
    /// <c>wall-slide</c> (<c>wall-slide-45</c>) and <c>corner-wedge</c> (<c>corner-wedge</c>); <c>weapon</c>
    /// <c>rifle-hold</c> (<c>rifle-burst</c>); <c>projectile</c> <c>launcher-flight</c> (<c>launcher-arc</c>);
    /// <c>hitboxBuild</c> <c>standing</c> and <c>stateHash</c> <c>one-character</c> (both
    /// <c>flat-run-&lt;hz&gt;hz</c>); and <c>world</c> <c>fuzz-all-buttons</c>. It starts at the beginning of
    /// its pass.
    /// </summary>
    /// <exception cref="ArgumentNullException">The kind or the case name is null.</exception>
    /// <exception cref="ArgumentException">The kind, or the case within it, is unknown; the message names it.</exception>
    /// <exception cref="InvalidOperationException">The definition does not give the case its pass, such as a step that faults.</exception>
    public static StepCase Create(string kind, string caseName, int hz)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(caseName);
        var definition = Definition(kind, caseName, hz);
        var world = SimCoreContent.World;
        var stream = ScenarioCatalog.Get(definition).Build(SimCoreContent.BaseConfig, world, hz);
        var config = CompiledConfig.Create(stream.Config);
        var what = string.Create(CultureInfo.InvariantCulture, $"{kind}/{caseName} at {hz} Hz ({definition})");
        Driver driver = kind switch
        {
            Movement => new MovementDriver(stream, config, world, what),
            Weapon => new WeaponDriver(stream, config, world, what),
            ProjectileKind => new ProjectileDriver(stream, config, world, what),
            HitboxBuild => new HitboxDriver(stream, config, world, what),
            StateHashKind => new StateHashDriver(stream, config, world, what),
            _ => new WorldDriver(stream, config, world),
        };
        return new StepCase(kind, caseName, hz, driver);
    }

    /// <summary>Starts the pass again: the case's state is its first step's again, and <see cref="StepsLeftInPass"/> is <see cref="PassLength"/>.</summary>
    public void Restart()
    {
        _driver.Restart(this);
        _index = 0;
    }

    /// <summary>Runs the pass's next step through the core.</summary>
    /// <exception cref="InvalidOperationException">The pass has ended; call <see cref="Restart"/>.</exception>
    public void Step()
    {
        if (_index >= PassLength)
        {
            throw new InvalidOperationException($"The pass of {Kind}/{CaseName} at {StepRateHz} Hz has ended; call Restart() first.");
        }

        _driver.Step(this, _index);
        _index++;
    }

    /// <summary>The benchmarks' operation: <see cref="Restart"/> when the pass has ended, then <see cref="Step"/>.</summary>
    public void StepCycling()
    {
        if (_index >= PassLength)
        {
            Restart();
        }

        Step();
    }

    /// <summary>The scenario definition a case is built from.</summary>
    private static string Definition(string kind, string caseName, int hz)
    {
        var flatRun = string.Create(CultureInfo.InvariantCulture, $"flat-run-{hz}hz");
        string? definition = kind switch
        {
            Movement => caseName switch
            {
                "flat-run" => flatRun,
                "wall-slide" => "wall-slide-45",
                "corner-wedge" => "corner-wedge",
                _ => null,
            },
            Weapon => caseName == "rifle-hold" ? "rifle-burst" : null,
            ProjectileKind => caseName == "launcher-flight" ? "launcher-arc" : null,
            HitboxBuild => caseName == "standing" ? flatRun : null,
            StateHashKind => caseName == "one-character" ? flatRun : null,
            WorldKind => caseName == "fuzz-all-buttons" ? "fuzz-all-buttons" : null,
            _ => throw new ArgumentException($"Unknown step kind '{kind}'.", nameof(kind)),
        };
        return definition ?? throw new ArgumentException($"Unknown {kind} case '{caseName}'.", nameof(caseName));
    }

    // ---------------------------------------------------------------- shared preparation

    /// <summary>The stream's initial state, decoded with <see cref="StateSerializer.ReadLoaded"/>.</summary>
    private static StateSnapshot ReadInitial(RecordedStream stream) =>
        StateSerializer.ReadLoaded(stream.InitialState.Span, StateErrorContext.State);

    /// <summary>The candidate buffer's length, sized as <see cref="SimWorld"/> sizes its own: for the widest box the configured capsule sweeps.</summary>
    private static int CandidateCapacity(CompiledConfig config, CollisionWorld world) =>
        world.MaxQueryCandidates((3f * config.CapsuleRadius) + (2f * config.ContactSkin));

    /// <summary>
    /// Character 0's stage 1 input for <paramref name="entry"/>, as <see cref="SimWorld"/> merges it: the first
    /// command of the entry when it names character 0 (among the first <paramref name="characterCount"/>) and
    /// <see cref="InputValidator.Validate"/> accepts it against <paramref name="character"/>, else the
    /// neutral input (no buttons, the character's aim, active slot and last sequence).
    /// </summary>
    private static InputCommand CharacterZeroInput(RecordedStep entry, in CharacterState character, int characterCount, int weaponSlots)
    {
        var commands = entry.Commands;
        var limit = commands.Length < characterCount ? commands.Length : characterCount;
        if (limit > 0 && commands[0].CharacterIndex == 0)
        {
            var command = commands[0].Command;
            if (InputValidator.Validate(in command, character.LastSequence, weaponSlots) == InputRejection.None)
            {
                return command;
            }
        }

        return new InputCommand(character.LastSequence, 0, 0, character.AimYaw, character.AimPitch, character.ActiveSlot);
    }

    /// <summary>Throws when a movement step of the preparation faulted: a case is built only from a pass without faults.</summary>
    private static void ThrowIfFaulted(StepFaultCode fault, string what, uint step)
    {
        if (fault != StepFaultCode.None)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{what}: step {step} faulted with {fault}, so the case has no pass."));
        }
    }

    /// <summary>
    /// Runs <paramref name="stream"/> on a new <see cref="SimWorld"/> and returns each step's committed state,
    /// copied, with its shot events.
    /// </summary>
    /// <exception cref="InvalidOperationException">A step faulted.</exception>
    private static List<CommittedStep> RunWorld(RecordedStream stream, CompiledConfig config, CollisionWorld world, string what)
    {
        var simWorld = new SimWorld(config, world);
        simWorld.Reset(stream.InitialState.Span, stream.MatchSalt);
        var steps = new List<CommittedStep>(stream.Steps.Count);
        foreach (var entry in stream.Steps)
        {
            var report = simWorld.Step(Commands(entry));
            if (report.Fault.Code != StepFaultCode.None)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{what}: world step {report.Step + 1u} faulted with {report.Fault.Code} at {report.Fault.Path}, so the case has no pass."));
            }

            steps.Add(new CommittedStep(report.Step, report.State.ToArray(), report.Events.Shots.ToArray()));
        }

        return steps;
    }

    /// <summary>An entry's commands as the world takes them.</summary>
    private static CharacterCommand[] Commands(RecordedStep entry)
    {
        var commands = new CharacterCommand[entry.Commands.Length];
        for (var c = 0; c < commands.Length; c++)
        {
            commands[c] = new CharacterCommand(entry.Commands[c].CharacterIndex, entry.Commands[c].Command);
        }

        return commands;
    }

    /// <summary>
    /// The records of a committed state (contracts/state-serialization.md#layout): its header, characters,
    /// weapons and projectiles, decoded with <see cref="StateSerializer.ReadLoaded"/> after its step events are
    /// replaced by six zero counts, because a loaded state holds no events.
    /// </summary>
    private static StateSnapshot ReadRecords(byte[] state, int weaponSlots)
    {
        int characterCount = BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(4));
        int projectileCount = BinaryPrimitives.ReadUInt16LittleEndian(state.AsSpan(6));
        var recordsEnd = StateSerializer.HeaderLength
            + (characterCount * (StateSerializer.CharacterFixedLength + (StateSerializer.WeaponLength * weaponSlots)))
            + (projectileCount * StateSerializer.ProjectileLength);
        var withoutEvents = new byte[recordsEnd + StateSerializer.EventCountsLength];
        state.AsSpan(0, recordsEnd).CopyTo(withoutEvents);
        return StateSerializer.ReadLoaded(withoutEvents, StateErrorContext.State);
    }

    /// <summary>
    /// <see cref="StateHash.Compute"/> of the canonical state <see cref="StateSerializer.Write"/> writes for
    /// copies of the given records and events.
    /// </summary>
    private static ulong HashState(
        uint step,
        uint nextProjectileId,
        int weaponSlots,
        ReadOnlySpan<CharacterState> characters,
        ReadOnlySpan<WeaponState> weapons,
        ReadOnlySpan<Projectile> projectiles,
        ReadOnlySpan<ShotEvent> shots,
        ReadOnlySpan<ProjectileHit> hits,
        ReadOnlySpan<ExpiryEvent> expiries)
    {
        CharacterState[] characterCopy = [.. characters];
        Projectile[] projectileCopy = [.. projectiles];
        ShotEvent[] shotCopy = [.. shots];
        ProjectileHit[] hitCopy = [.. hits];
        var length = StateSerializer.Length(characters.Length, weaponSlots, projectiles.Length, shots.Length, hits.Length, expiries.Length, 0, 0, 0);
        var buffer = new byte[length];
        var fault = StateSerializer.Write(
            step,
            nextProjectileId,
            weaponSlots,
            characterCopy,
            weapons,
            projectileCopy,
            shotCopy,
            hitCopy,
            expiries,
            ReadOnlySpan<InputRejectionEvent>.Empty,
            ReadOnlySpan<FireBlockEvent>.Empty,
            ReadOnlySpan<MovementOutcomeEvent>.Empty,
            buffer,
            out var written);
        if (fault.Code != StepFaultCode.None)
        {
            throw new InvalidOperationException($"The case's output state does not serialise: {fault.Code} at {fault.Path}.");
        }

        return StateHash.Compute(buffer.AsSpan(0, written));
    }

    /// <summary>A step the world committed: its number, its canonical state and its shot events.</summary>
    private sealed record CommittedStep(uint Step, byte[] State, ShotEvent[] Shots);

    // ---------------------------------------------------------------- the drivers

    /// <summary>One kind's pass: its precomputed inputs and buffers, its step and its restart.</summary>
    private abstract class Driver
    {
        public int PassLength { get; protected init; }

        /// <summary>Runs pass step <paramref name="index"/> through the core and writes what it did to <paramref name="owner"/>.</summary>
        public abstract void Step(StepCase owner, int index);

        /// <summary>Sets the case's state back to its first step's.</summary>
        public abstract void Restart(StepCase owner);

        /// <summary>The case's <see cref="LastResultHash"/>.</summary>
        public abstract ulong ResultHash(StepCase owner);
    }

    /// <summary><c>movement</c>: character 0 stepped by <see cref="MovementStep.Step"/>.</summary>
    private sealed class MovementDriver : Driver
    {
        private readonly CompiledConfig _config;
        private readonly CollisionWorld _world;
        private readonly CharacterState _initial;
        private readonly WeaponState[] _weapons;
        private readonly uint _nextProjectileId;
        private readonly uint[] _steps;
        private readonly InputCommand[] _inputs;
        private readonly int[] _candidates;
        private MovementResult _lastResult;

        public MovementDriver(RecordedStream stream, CompiledConfig config, CollisionWorld world, string what)
        {
            _config = config;
            _world = world;
            var initial = ReadInitial(stream);
            _initial = initial.Characters[0];
            _weapons = [.. initial.Weapons.Take(config.WeaponSlots)];
            _nextProjectileId = initial.NextProjectileId;
            _candidates = new int[CandidateCapacity(config, world)];

            var entries = stream.Steps;
            _steps = new uint[entries.Count];
            _inputs = new InputCommand[entries.Count];
            var character = _initial;
            for (var k = 0; k < entries.Count; k++)
            {
                _steps[k] = entries[k].Step;
                _inputs[k] = CharacterZeroInput(entries[k], in character, initial.Characters.Count, config.WeaponSlots);
                ThrowIfFaulted(MovementStep.Step(ref character, in _inputs[k], config, world, _candidates).Fault, what, _steps[k]);
            }

            PassLength = entries.Count;
        }

        /// <summary>The last step's movement result, kept so the work is observed.</summary>
        public MovementResult LastResult => _lastResult;

        public override void Step(StepCase owner, int index)
        {
            _lastResult = MovementStep.Step(ref owner._character, in _inputs[index], _config, _world, _candidates);
            owner._lastStep = _steps[index];
        }

        public override void Restart(StepCase owner) => owner._character = _initial;

        public override ulong ResultHash(StepCase owner) => HashState(
            owner._lastStep,
            _nextProjectileId,
            _config.WeaponSlots,
            [owner._character],
            _weapons,
            [],
            [],
            [],
            []);
    }

    /// <summary><c>weapon</c>: character 0's weapons stepped by <see cref="WeaponStep.Step"/>.</summary>
    private sealed class WeaponDriver : Driver
    {
        private readonly CompiledConfig _config;
        private readonly ulong _matchSalt;
        private readonly CharacterState _initialCharacter;
        private readonly WeaponState[] _initialWeapons;
        private readonly WeaponState[] _weapons;
        private readonly uint _initialNextProjectileId;
        private readonly uint[] _steps;
        private readonly InputCommand[] _inputs;
        private readonly CharacterState[] _postMovement;
        private readonly Projectile[] _spawned;
        private readonly ShotEvent[] _shots;
        private uint _nextProjectileId;
        private FireBlock _lastBlock;

        public WeaponDriver(RecordedStream stream, CompiledConfig config, CollisionWorld world, string what)
        {
            _config = config;
            _matchSalt = stream.MatchSalt;
            var initial = ReadInitial(stream);
            var slots = config.WeaponSlots;
            _initialCharacter = initial.Characters[0];
            _initialWeapons = [.. initial.Weapons.Take(slots)];
            _weapons = new WeaponState[slots];
            _initialNextProjectileId = initial.NextProjectileId;
            _spawned = new Projectile[config.MaxShotsPerStepMax];
            _shots = new ShotEvent[config.MaxShotsPerStepMax];

            // The preparation pass: the world's stages 1 to 3 for character 0, so each step's input is merged
            // against the character the world would hold, and WeaponStep sees the post-movement character.
            var entries = stream.Steps;
            _steps = new uint[entries.Count];
            _inputs = new InputCommand[entries.Count];
            _postMovement = new CharacterState[entries.Count];
            var candidates = new int[CandidateCapacity(config, world)];
            var character = _initialCharacter;
            _initialWeapons.CopyTo(_weapons, 0);
            var nextProjectileId = _initialNextProjectileId;
            for (var k = 0; k < entries.Count; k++)
            {
                _steps[k] = entries[k].Step;
                _inputs[k] = CharacterZeroInput(entries[k], in character, initial.Characters.Count, slots);
                ThrowIfFaulted(MovementStep.Step(ref character, in _inputs[k], config, world, candidates).Fault, what, _steps[k]);
                _postMovement[k] = character;
                WeaponStep.Step(config, _matchSalt, _steps[k], ref character, _weapons, in _inputs[k], 0, ref nextProjectileId, _spawned, _shots, out _);
            }

            PassLength = entries.Count;
        }

        /// <summary>Why the last step's held trigger fired no shot, kept so the work is observed.</summary>
        public FireBlock LastBlock => _lastBlock;

        public override void Step(StepCase owner, int index)
        {
            owner._character = _postMovement[index];
            owner._lastShotCount = WeaponStep.Step(
                _config,
                _matchSalt,
                _steps[index],
                ref owner._character,
                _weapons,
                in _inputs[index],
                0,
                ref _nextProjectileId,
                _spawned,
                _shots,
                out _lastBlock);
            owner._lastStep = _steps[index];
        }

        public override void Restart(StepCase owner)
        {
            owner._character = _initialCharacter;
            owner._lastShotCount = 0;
            _initialWeapons.CopyTo(_weapons, 0);
            _nextProjectileId = _initialNextProjectileId;
        }

        public override ulong ResultHash(StepCase owner)
        {
            var shots = owner._lastShotCount;
            return HashState(
                owner._lastStep,
                _nextProjectileId,
                _config.WeaponSlots,
                [owner._character],
                _weapons,
                _spawned.AsSpan(0, shots),
                _shots.AsSpan(0, shots),
                [],
                []);
        }
    }

    /// <summary><c>projectile</c>: one launched projectile stepped by <see cref="ProjectileStep.Step"/>.</summary>
    private sealed class ProjectileDriver : Driver
    {
        private readonly CompiledConfig _config;
        private readonly CollisionWorld _world;
        private readonly StateSnapshot _fired;
        private readonly Projectile _launched;
        private readonly uint _firstStep;
        private readonly uint[] _characterIds;
        private readonly Hitbox[] _hitboxes;
        private readonly HitboxBounds[] _bounds;

        public ProjectileDriver(RecordedStream stream, CompiledConfig config, CollisionWorld world, string what)
        {
            _config = config;
            _world = world;
            CommittedStep? firing = null;
            foreach (var step in RunWorld(stream, config, world, what))
            {
                if (step.Shots.Length > 0)
                {
                    firing = step;
                    break;
                }
            }

            if (firing is null)
            {
                throw new InvalidOperationException($"{what}: the stream fires no shot, so the case has no pass.");
            }

            var projectileId = firing.Shots[0].ProjectileId;
            _fired = ReadRecords(firing.State, config.WeaponSlots);
            var launched = _fired.Projectiles.Where(projectile => projectile.Id == projectileId).ToList();
            if (launched.Count != 1)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{what}: projectile {projectileId} ended in its firing step {firing.Step}, so the case has no pass."));
            }

            _launched = launched[0];
            _firstStep = firing.Step + 1u;

            var characters = _fired.Characters;
            _characterIds = new uint[characters.Count];
            _hitboxes = new Hitbox[characters.Count * HitboxLayout.CapsuleCount];
            _bounds = new HitboxBounds[characters.Count];
            for (var i = 0; i < characters.Count; i++)
            {
                var character = characters[i];
                _characterIds[i] = character.Id;
                _bounds[i] = HitboxBuilder.Build(
                    config.HitboxLayouts[(int)character.Stance],
                    character.Position,
                    character.AimYaw,
                    _hitboxes.AsSpan(i * HitboxLayout.CapsuleCount, HitboxLayout.CapsuleCount));
            }

            // The pass ends on the step on which the projectile hits or expires, which its lifetime bounds.
            var limit = config.LifetimeSteps[_launched.Class] + 1;
            var projectile = _launched;
            var length = 0;
            var outcome = ProjectileOutcome.Flying;
            while (outcome == ProjectileOutcome.Flying)
            {
                if (length == limit)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{what}: the projectile still flies after {length} steps."));
                }

                outcome = ProjectileStep.Step(ref projectile, _firstStep + (uint)length, config, world, Targets(), out _, out _);
                length++;
            }

            PassLength = length;
        }

        public override void Step(StepCase owner, int index)
        {
            var step = _firstStep + (uint)index;
            owner._lastOutcome = ProjectileStep.Step(ref owner._projectile, step, _config, _world, Targets(), out owner._lastHit, out owner._lastExpiry);
            owner._lastStep = step;
        }

        public override void Restart(StepCase owner)
        {
            owner._projectile = _launched;
            owner._lastOutcome = ProjectileOutcome.Flying;
            owner._lastHit = default;
            owner._lastExpiry = default;
        }

        public override ulong ResultHash(StepCase owner)
        {
            var outcome = owner._lastOutcome;
            ReadOnlySpan<Projectile> projectiles = outcome == ProjectileOutcome.Flying ? [owner._projectile] : [];
            ReadOnlySpan<ProjectileHit> hits = outcome == ProjectileOutcome.Hit ? [owner._lastHit] : [];
            ReadOnlySpan<ExpiryEvent> expiries = outcome == ProjectileOutcome.Expired
                ? [new ExpiryEvent { ProjectileId = owner._projectile.Id, Reason = owner._lastExpiry }]
                : [];
            return HashState(
                owner._lastStep,
                _fired.NextProjectileId,
                _config.WeaponSlots,
                [.. _fired.Characters],
                [.. _fired.Weapons],
                projectiles,
                [],
                hits,
                expiries);
        }

        private ProjectileTargets Targets() => new(_characterIds, _hitboxes, _bounds);
    }

    /// <summary><c>hitboxBuild</c>: character 0's committed pose per step, posed by <see cref="HitboxBuilder.Build"/>.</summary>
    private sealed class HitboxDriver : Driver
    {
        private readonly HitboxLayout[] _layouts;
        private readonly uint[] _steps;
        private readonly CharacterState[] _poses;
        private readonly Hitbox[] _hitboxes = new Hitbox[HitboxLayout.CapsuleCount];
        private HitboxBounds _bounds;

        public HitboxDriver(RecordedStream stream, CompiledConfig config, CollisionWorld world, string what)
        {
            _layouts = [.. config.HitboxLayouts];
            var committed = RunWorld(stream, config, world, what);
            _steps = new uint[committed.Count];
            _poses = new CharacterState[committed.Count];
            for (var k = 0; k < committed.Count; k++)
            {
                _steps[k] = committed[k].Step;
                _poses[k] = ReadRecords(committed[k].State, config.WeaponSlots).Characters[0];
            }

            PassLength = committed.Count;
        }

        public override void Step(StepCase owner, int index)
        {
            ref readonly var pose = ref _poses[index];
            _bounds = HitboxBuilder.Build(_layouts[(int)pose.Stance], pose.Position, pose.AimYaw, _hitboxes);
            owner._lastStep = _steps[index];
        }

        public override void Restart(StepCase owner)
        {
            Array.Clear(_hitboxes);
            _bounds = default;
        }

        public override ulong ResultHash(StepCase owner)
        {
            // Per hitbox: part (1 byte), A, B and radius (7 floats); then the bounding sphere (4 floats).
            const int HitboxBytes = 1 + (7 * sizeof(float));
            var bytes = new byte[(_hitboxes.Length * HitboxBytes) + (4 * sizeof(float))];
            var offset = 0;
            foreach (var hitbox in _hitboxes)
            {
                bytes[offset] = (byte)hitbox.Part;
                offset++;
                offset = WriteVector(bytes, offset, hitbox.A);
                offset = WriteVector(bytes, offset, hitbox.B);
                offset = WriteFloat(bytes, offset, hitbox.Radius);
            }

            offset = WriteVector(bytes, offset, _bounds.Centre);
            WriteFloat(bytes, offset, _bounds.Radius);
            return StateHash.Compute(bytes);
        }

        private static int WriteVector(byte[] bytes, int offset, Vec3 vector)
        {
            offset = WriteFloat(bytes, offset, vector.X);
            offset = WriteFloat(bytes, offset, vector.Y);
            return WriteFloat(bytes, offset, vector.Z);
        }

        private static int WriteFloat(byte[] bytes, int offset, float value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), BitConverter.SingleToUInt32Bits(value));
            return offset + sizeof(float);
        }
    }

    /// <summary><c>stateHash</c>: the world's committed canonical state per step, hashed by <see cref="StateHash.Compute"/>.</summary>
    private sealed class StateHashDriver : Driver
    {
        private readonly uint[] _steps;
        private readonly byte[] _states;
        private readonly int[] _offsets;
        private readonly int[] _lengths;

        public StateHashDriver(RecordedStream stream, CompiledConfig config, CollisionWorld world, string what)
        {
            var committed = RunWorld(stream, config, world, what);
            _steps = new uint[committed.Count];
            _offsets = new int[committed.Count];
            _lengths = new int[committed.Count];
            var total = 0;
            for (var k = 0; k < committed.Count; k++)
            {
                _steps[k] = committed[k].Step;
                _offsets[k] = total;
                _lengths[k] = committed[k].State.Length;
                total += _lengths[k];
            }

            _states = new byte[total];
            for (var k = 0; k < committed.Count; k++)
            {
                committed[k].State.CopyTo(_states, _offsets[k]);
            }

            PassLength = committed.Count;
        }

        public override void Step(StepCase owner, int index)
        {
            owner._lastStepHash = StateHash.Compute(new ReadOnlySpan<byte>(_states, _offsets[index], _lengths[index]));
            owner._lastStep = _steps[index];
        }

        public override void Restart(StepCase owner) => owner._lastStepHash = 0;

        public override ulong ResultHash(StepCase owner) => owner._lastStepHash;
    }

    /// <summary><c>world</c>: a <see cref="SimWorld"/> stepped with each entry's commands.</summary>
    private sealed class WorldDriver : Driver
    {
        private readonly SimWorld _world;
        private readonly byte[] _initialState;
        private readonly ulong _matchSalt;
        private readonly CharacterCommand[][] _commands;

        public WorldDriver(RecordedStream stream, CompiledConfig config, CollisionWorld world)
        {
            _world = new SimWorld(config, world);
            _initialState = stream.InitialState.ToArray();
            _matchSalt = stream.MatchSalt;
            var entries = stream.Steps;
            _commands = new CharacterCommand[entries.Count][];
            for (var k = 0; k < entries.Count; k++)
            {
                _commands[k] = Commands(entries[k]);
            }

            PassLength = entries.Count;
        }

        public override void Step(StepCase owner, int index)
        {
            var report = _world.Step(_commands[index]);
            owner._lastFault = report.Fault;
            owner._lastStepHash = report.Hash;
            owner._lastStep = report.Step;
        }

        public override void Restart(StepCase owner)
        {
            _world.Reset(_initialState, _matchSalt);
            owner._lastFault = default;
            owner._lastStepHash = 0;
        }

        public override ulong ResultHash(StepCase owner) => owner._lastStepHash;
    }
}
