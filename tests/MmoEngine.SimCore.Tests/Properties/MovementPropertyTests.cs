using CsCheck;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Properties;

/// <summary>
/// Movement properties over generated worlds (FR-001, FR-003, FR-005; plan.md "Fuzz worlds start clear,
/// not small"). Each property is a CsCheck property with its own declared fixed seed, printed in every
/// failure, run at 20, 30 and 60 Hz with CsCheck's default 100 iterations per rate. A sample is a fuzz
/// world generated in the test (a heightfield with slopes up to 60°, and up to five random wall, box and
/// ramp meshes) with one to four characters, each holding random valid input commands for 500 steps of a
/// <see cref="SimWorld"/>.
/// </summary>
/// <remarks>
/// Every character starts at rest (velocity 0, Standing) at a position drawn inside the playable area. A
/// position is kept only when the standing capsule, axis Position + (0, r, 0) to Position +
/// (0, standHeight − r, 0), is at least capsuleRadius + contactSkin from every triangle and from the
/// boundary planes by the double reference (<see cref="CapsuleClearanceReference"/>); otherwise the next of
/// <see cref="DrawsPerCharacter"/> drawn positions is tried, and a character none of them places discards the
/// whole sample through a CsCheck generator filter (<c>Gen.Where</c>), so no sample starts overlapping. A
/// character starts Grounded, with that triangle's normal as its ground normal, only when a vertical
/// down-probe from its feet finds a walkable triangle (normal y ≥ <c>CosSlopeLimit</c>) within
/// snapDistance; otherwise it starts airborne with ground normal exactly (0, 1, 0). So
/// <c>SimWorld.Reset</c>'s state guard accepts every sample, and the properties cover every valid world the
/// generator produces.
/// </remarks>
public sealed class MovementPropertyTests
{
    /// <summary>CsCheck's default iteration count, run once per step rate.</summary>
    private const int Iterations = 100;

    /// <summary>The steps each sample runs.</summary>
    private const int StepsPerSample = 500;

    /// <summary>The positions drawn per character before the sample is discarded.</summary>
    private const int DrawsPerCharacter = 16;

    private const ulong Salt = 75;

    private const ulong NeverOverlapsSeed = 0x0015_0075_0000_0001UL;
    private const ulong NeverStuckSeed = 0x0015_0075_0000_0002UL;
    private const ulong StepUpSeed = 0x0015_0075_0000_0003UL;
    private const ulong LoopBoundSeed = 0x0015_0075_0000_0004UL;

    private const float GridCellSize = 4f;

    private const int WallKind = 0;
    private const int BoxKind = 1;
    private const int RampKind = 2;

    /// <summary>The placeholders the generator places characters with; none of them depends on the rate.</summary>
    private static readonly CompiledConfig PlacementConfig = TestConfigs.Compiled(30);

    private static readonly Gen<WorldRecipe> WorldRecipes = Gen.SelectMany(
        Gen.Int[12, 33],
        Gen.OneOfConst(1f, 2f),
        (samples, cellSize) => Gen.Select(
            Gen.Select(Gen.Int[-8, 8], Gen.Int[-8, 8]),
            Gen.Double[-20, 20],
            Gen.Double[0, 60],
            Gen.Double[0, 1],
            Gen.Select(Gen.Double[-1, 1], Gen.Double[-1, 1]),
            Gen.Double[0, 1].Array[samples * samples],
            (offset, baseHeight, maxSlope, tiltShare, tilt, noise) => new WorldRecipe(
                samples, cellSize, offset.Item1, offset.Item2, baseHeight, maxSlope, tiltShare, tilt.Item1, tilt.Item2, noise)));

    private static readonly Gen<MeshRecipe> MeshRecipes = Gen.Select(
        Gen.Int[WallKind, RampKind],
        Gen.Double[0.1, 0.9],
        Gen.Double[0.1, 0.9],
        Gen.Double[0, 2 * System.Math.PI],
        Gen.Double[0, 1],
        Gen.Double[0, 1],
        Gen.Double[0, 1],
        (kind, u, v, angle, a, b, c) => new MeshRecipe(kind, u, v, angle, a, b, c));

    private static readonly Gen<PlacementDraw> Draws = Gen.Select(
        Gen.Double[0, 1],
        Gen.Double[0, 1],
        Gen.Double[0, 1],
        (u, v, w) => new PlacementDraw(u, v, w));

    private static readonly Gen<CommandSegment> Segments = Gen.Select(
        Gen.UShort,
        Gen.UShort,
        Gen.Short[-16384, 16384],
        Gen.Byte[0, 1],
        Gen.Int[1, 60],
        (buttons, yaw, pitch, slot, length) => new CommandSegment((Buttons)(buttons & (ushort)Buttons.Known), yaw, pitch, slot, length));

    private static readonly Gen<CharacterRecipe> CharacterRecipes = Gen.Select(
        Draws.Array[DrawsPerCharacter],
        Gen.UShort,
        Segments.Array[1, 16],
        (draws, yaw, segments) => new CharacterRecipe(draws, yaw, segments));

    /// <summary>Every generated sample whose characters were all placed; the rest are discarded.</summary>
    private static readonly Gen<FuzzSample> Samples = Gen.Where(
        Gen.Select(WorldRecipes, MeshRecipes.Array[0, 5], CharacterRecipes.Array[1, 4], FuzzSample.Create),
        sample => sample.Placed);

    /// <summary>
    /// FR-003: after every step every character's capsule axis (Position + (0, r, 0) to Position +
    /// (0, h − r, 0), h the stance's height) is at least capsuleRadius − contactTolerance from every
    /// triangle, by the double reference. Some step must move a character and end with it within
    /// r + 2·contactSkin of a triangle, so the property is not met by characters that never move or never
    /// touch anything (a start may already lie in that shell, so a contact counts only after a move).
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void NeverOverlapsByMoreThanTheContactTolerance(int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var contacts = 0;
        ForEachSample(NeverOverlapsSeed, hz, (sample, context) => RunSteps(sample, config, context, view =>
        {
            var radius = (double)view.Config.CapsuleRadius;
            var limit = radius - view.Config.ContactTolerance;
            var touching = radius + (2.0 * view.Config.ContactSkin);
            for (var c = 0; c < view.After.Length; c++)
            {
                var character = view.After[c];
                var (bottom, top) = CapsuleClearanceReference.Axis(character.Position, radius, Height(character, view.Config));
                var distance = CapsuleClearanceReference.NearestTriangleDistance(sample.World, bottom, top, touching, out var triangle);
                if (distance < limit)
                {
                    Assert.Fail(
                        $"{context}\nafter step {view.Step} character {character.Id}'s axis is {distance:R} m from triangle {triangle}, " +
                        $"nearer than capsuleRadius - contactTolerance = {limit:R}; before the step {view.Before[c]}, after it {character}, command {view.Commands[c].Command}");
                }

                contacts += distance <= touching && !character.Position.Equals(view.Before[c].Position) ? 1 : 0;
            }
        }));

        Assert.True(contacts > 0, $"seed 0x{NeverOverlapsSeed:X16} at {hz} Hz: no step moved a character and ended with it touching a triangle, so the property was not exercised");
    }

    /// <summary>
    /// FR-003, FR-005: a character that starts clear never reports <see cref="MovementOutcome.Stuck"/>, and
    /// no step faults. Every step of every sample runs, and some step must move a character, so the
    /// property is not met by characters that never move.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void NeverGetsStuckOnAFuzzWorld(int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var steps = 0;
        var moves = 0;
        ForEachSample(NeverStuckSeed, hz, (sample, context) => RunSteps(sample, config, context, view =>
        {
            steps++;
            for (var c = 0; c < view.After.Length; c++)
            {
                moves += view.After[c].Position.Equals(view.Before[c].Position) ? 0 : 1;
            }

            foreach (var outcome in view.Outcomes)
            {
                if ((outcome.Flags & MovementOutcome.Stuck) != 0)
                {
                    var index = Array.FindIndex(view.After, character => character.Id == outcome.CharacterId);
                    Assert.Fail(
                        $"{context}\nstep {view.Step} reported Stuck for character {outcome.CharacterId} (outcome flags {outcome.Flags}); " +
                        $"before the step {Describe(view.Before, index)}, after it {Describe(view.After, index)}");
                }
            }
        }));

        Assert.Equal(Iterations * StepsPerSample, steps);
        Assert.True(moves > 0, $"seed 0x{NeverStuckSeed:X16} at {hz} Hz: no step moved a character, so the property was not exercised");
    }

    /// <summary>
    /// FR-002, FR-003: in any step that starts and ends Grounded, <c>Position.y</c> rises by at most
    /// stepHeight + horizontal displacement · tan(slopeLimit) + contactSkin. Some such step must move the
    /// character horizontally, so the bound is exercised.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void StepUpNeverRisesMoreThanTheStepHeight(int hz)
    {
        var movement = TestConfigs.Default().Movement!;
        var tanSlopeLimit = System.Math.Tan(movement.SlopeLimitDegrees * System.Math.PI / 180.0);
        var config = TestConfigs.Compiled(hz);
        var groundedMoves = 0;
        ForEachSample(StepUpSeed, hz, (sample, context) => RunSteps(sample, config, context, view =>
        {
            for (var c = 0; c < view.After.Length; c++)
            {
                var before = view.Before[c];
                var after = view.After[c];
                if (!IsGrounded(before) || !IsGrounded(after))
                {
                    continue;
                }

                var rise = (double)after.Position.Y - before.Position.Y;
                var horizontal = Hypot((double)after.Position.X - before.Position.X, (double)after.Position.Z - before.Position.Z);
                var bound = view.Config.StepHeight + (horizontal * tanSlopeLimit) + view.Config.ContactSkin;
                if (rise > bound)
                {
                    Assert.Fail(
                        $"{context}\nstep {view.Step} starts and ends Grounded but character {after.Id} rises {rise:R} m over {horizontal:R} m horizontally, " +
                        $"above stepHeight + horizontal * tan(slopeLimit) + contactSkin = {bound:R}; before {before}, after {after}, command {view.Commands[c].Command}");
                }

                groundedMoves += horizontal > 0.0 ? 1 : 0;
            }
        }));

        Assert.True(groundedMoves > 0, $"seed 0x{StepUpSeed:X16} at {hz} Hz: no step started and ended Grounded with horizontal motion, so the bound was not exercised");
    }

    /// <summary>
    /// FR-005: <c>StepReport.Counters</c> is the sum over the step's characters (T099), so with
    /// characterCount the sample's characters every step has <c>Counters.SubMoves</c> ≤
    /// characterCount · maxSubMoves and <c>Counters.Sweeps</c> ≤ <c>Counters.SubMoves</c> ·
    /// (2 · maxSlideIterations + 3) + characterCount. The per-character bound is also asserted directly:
    /// <c>MovementStep.Step</c> on a copy of each character's pre-step state, with the step's command, the
    /// sample's <see cref="CollisionWorld"/> and a candidate buffer
    /// <c>int[collision.MaxQueryCandidates((3f * capsuleRadius) + (2f * contactSkin))]</c>, returns
    /// <c>SubMoves</c> ≤ maxSubMoves and <c>Sweeps</c> ≤ <c>SubMoves</c> · (2 · maxSlideIterations + 3) + 1.
    /// Some step must sweep, so the bounds are exercised.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void EveryLoopStaysWithinItsBound(int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var sweeps = 0L;
        ForEachSample(LoopBoundSeed, hz, (sample, context) =>
        {
            var collision = sample.World;
            var candidates = new int[collision.MaxQueryCandidates((3f * config.CapsuleRadius) + (2f * config.ContactSkin))];
            RunSteps(sample, config, context, view =>
            {
                var characterCount = view.After.Length;
                var sweepsPerSubMove = (2 * config.MaxSlideIterations) + 3;
                var counters = view.Counters;
                if (counters.SubMoves > characterCount * config.MaxSubMoves)
                {
                    Assert.Fail(
                        $"{context}\nstep {view.Step} counted {counters.SubMoves} sub-moves for {characterCount} characters, above {characterCount} * maxSubMoves {config.MaxSubMoves}");
                }

                if (counters.Sweeps > (counters.SubMoves * sweepsPerSubMove) + characterCount)
                {
                    Assert.Fail(
                        $"{context}\nstep {view.Step} counted {counters.Sweeps} sweeps over {counters.SubMoves} sub-moves for {characterCount} characters, " +
                        $"above SubMoves * {sweepsPerSubMove} + {characterCount}");
                }

                for (var c = 0; c < characterCount; c++)
                {
                    var character = view.Before[c];
                    var input = view.Commands[c].Command;
                    var result = MovementStep.Step(ref character, in input, config, collision, candidates);
                    if (result.Fault != StepFaultCode.None
                        || result.SubMoves > config.MaxSubMoves
                        || result.Sweeps > (result.SubMoves * sweepsPerSubMove) + 1)
                    {
                        Assert.Fail(
                            $"{context}\nstep {view.Step}, character {view.Before[c].Id} stepped alone from {view.Before[c]} with {input}: fault {result.Fault}, " +
                            $"{result.SubMoves} sub-moves (at most maxSubMoves {config.MaxSubMoves}), {result.Sweeps} sweeps (at most SubMoves * {sweepsPerSubMove} + 1)");
                    }
                }

                sweeps += counters.Sweeps;
            });
        });

        Assert.True(sweeps > 0, $"seed 0x{LoopBoundSeed:X16} at {hz} Hz: no step swept, so the loop bounds were not exercised");
    }

    /// <summary>
    /// Draws <see cref="Iterations"/> samples from <paramref name="seed"/> on PCG stream <paramref name="hz"/>
    /// and hands each to <paramref name="run"/> with the context every failure message starts with: the
    /// seed, the stream, the iteration and the sample.
    /// </summary>
    private static void ForEachSample(ulong seed, int hz, Action<FuzzSample, string> run)
    {
        var pcg = new PCG((uint)hz, seed);
        for (var i = 0; i < Iterations; i++)
        {
            var sample = Samples.Generate(pcg, null!, out _);
            run(sample, $"seed 0x{seed:X16}, PCG stream {hz}, iteration {i} of {Iterations} at {hz} Hz; {sample}");
        }
    }

    /// <summary>
    /// Resets a <see cref="SimWorld"/> over the sample's world with <paramref name="config"/> and the sample's
    /// state and steps it <see cref="StepsPerSample"/> times, every character with its command for the step.
    /// After each step <paramref name="check"/> sees the characters before and after it (decoded from the
    /// committed state), the commands, the movement outcomes and the counters. A faulted step commits
    /// nothing, so there is no state to check: it fails the property.
    /// </summary>
    private static void RunSteps(FuzzSample sample, CompiledConfig config, string context, Action<StepView> check)
    {
        var world = new SimWorld(config, sample.World);
        try
        {
            world.Reset(sample.State, Salt);
        }
        catch (CoreFormatException exception)
        {
            Assert.Fail($"{context}\nSimWorld.Reset refused the sample's state: {exception.Message}");
        }

        var before = StateSerializer.ReadLoaded(sample.State, StateErrorContext.State).Characters.ToArray();
        for (var step = 0; step < StepsPerSample; step++)
        {
            var commands = new CharacterCommand[before.Length];
            for (var c = 0; c < commands.Length; c++)
            {
                commands[c] = new CharacterCommand(c, sample.Command(c, step));
            }

            var report = world.Step(commands);
            if (report.Fault.Code != StepFaultCode.None)
            {
                Assert.Fail(
                    $"{context}\nstep {step + 1} faulted with {report.Fault.Code} at {report.Fault.Path}; the characters before it: {string.Join("; ", before)}");
            }

            var after = StateSerializer.ReadCommitted(report.State).Characters.ToArray();
            check(new StepView(step + 1, config, before, commands, after, report.Events.MovementOutcomes.ToArray(), report.Counters));
            before = after;
        }
    }

    private static double Height(in CharacterState character, CompiledConfig config) =>
        character.Stance == Stance.Standing ? config.StandHeight : config.CrouchHeight;

    private static bool IsGrounded(in CharacterState character) => (character.Flags & CharacterFlags.Grounded) != 0;

    private static double Hypot(double x, double z) => System.Math.Sqrt((x * x) + (z * z));

    private static string Describe(CharacterState[] characters, int index) =>
        index >= 0 ? characters[index].ToString() : "(no such character)";

    /// <summary>One step of a sample: what each property checks.</summary>
    private sealed record StepView(
        int Step,
        CompiledConfig Config,
        CharacterState[] Before,
        CharacterCommand[] Commands,
        CharacterState[] After,
        MovementOutcomeEvent[] Outcomes,
        StepCounters Counters);

    /// <summary>
    /// The heightfield's raw draws: <paramref name="Samples"/> × <paramref name="Samples"/> samples
    /// <paramref name="CellSize"/> apart, centred on a 64 m lattice point (<paramref name="OffsetX"/>,
    /// <paramref name="OffsetZ"/>), with slopes up to <paramref name="MaxSlopeDegrees"/>.
    /// </summary>
    private sealed record WorldRecipe(
        int Samples,
        float CellSize,
        int OffsetX,
        int OffsetZ,
        double BaseHeight,
        double MaxSlopeDegrees,
        double TiltShare,
        double TiltX,
        double TiltZ,
        double[] Noise)
    {
        public override string ToString() =>
            $"heightfield {Samples}x{Samples} samples {CellSize} m apart, lattice offset ({OffsetX}, {OffsetZ}), base {BaseHeight:F2} m, slopes up to {MaxSlopeDegrees:F1} deg";
    }

    /// <summary>One mesh's raw draws: its kind, its centre as fractions of the extent, its heading and three sizes in [0, 1].</summary>
    private sealed record MeshRecipe(int Kind, double U, double V, double Angle, double A, double B, double C)
    {
        public override string ToString() =>
            $"{(Kind == WallKind ? "wall" : Kind == BoxKind ? "box" : "ramp")} at ({U:F3}, {V:F3}) heading {Angle:F3} rad, sizes ({A:F3}, {B:F3}, {C:F3})";
    }

    /// <summary>One drawn start: X and Z as fractions of the placeable extent, and the lift above the terrain from W.</summary>
    private readonly record struct PlacementDraw(double U, double V, double W);

    /// <summary>A command held for <paramref name="Length"/> steps.</summary>
    private sealed record CommandSegment(Buttons Buttons, ushort Yaw, short Pitch, byte Slot, int Length);

    /// <summary>One character's raw draws: its candidate starts, its initial aim yaw and its command segments, repeated to cover the run.</summary>
    private sealed record CharacterRecipe(PlacementDraw[] Draws, ushort Yaw, CommandSegment[] Segments);

    /// <summary>A generated world, its characters' start records (null when one could not be placed) and their commands.</summary>
    private sealed class FuzzSample
    {
        private readonly WorldRecipe _recipe;
        private readonly MeshRecipe[] _meshes;
        private readonly CharacterRecipe[] _characters;

        private FuzzSample(WorldRecipe recipe, MeshRecipe[] meshes, CharacterRecipe[] characters, CollisionWorld world, CharacterRecord[]? records)
        {
            _recipe = recipe;
            _meshes = meshes;
            _characters = characters;
            World = world;
            Records = records;
            State = records is null ? Array.Empty<byte>() : TestStates.Build(0, records);
        }

        /// <summary>The decoded fuzz world.</summary>
        public CollisionWorld World { get; }

        /// <summary>The characters' start records in ascending ID, or null when a character could not be placed.</summary>
        public CharacterRecord[]? Records { get; }

        /// <summary>The canonical state at step 0 holding <see cref="Records"/>.</summary>
        public byte[] State { get; }

        /// <summary>True when every character was placed.</summary>
        public bool Placed => Records is not null;

        /// <summary>
        /// Builds the world from its draws through <see cref="CollisionFile"/>, then places each character at
        /// the first of its drawn starts that is clear.
        /// </summary>
        public static FuzzSample Create(WorldRecipe recipe, MeshRecipe[] meshes, CharacterRecipe[] characters)
        {
            var terrain = Terrain(recipe);
            var ground = TestWorlds.Load(terrain);
            var meshData = new MeshData[meshes.Length];
            for (var m = 0; m < meshes.Length; m++)
            {
                meshData[m] = Mesh((uint)(m + 1), meshes[m], ground);
            }

            var world = TestWorlds.Load(terrain with { Meshes = meshData });
            var records = new CharacterRecord[characters.Length];
            for (var k = 0; k < characters.Length; k++)
            {
                var record = Place(world, (uint)(k + 1), characters[k]);
                if (record is null)
                {
                    return new FuzzSample(recipe, meshes, characters, world, null);
                }

                records[k] = record;
            }

            return new FuzzSample(recipe, meshes, characters, world, records);
        }

        /// <summary>
        /// Character <paramref name="index"/>'s command for step <paramref name="step"/> (0-based): its
        /// segments repeated, with sequence step + 1 and client step <paramref name="step"/>, so every
        /// command is valid.
        /// </summary>
        public InputCommand Command(int index, int step)
        {
            var segments = _characters[index].Segments;
            var total = 0;
            foreach (var segment in segments)
            {
                total += segment.Length;
            }

            var t = step % total;
            foreach (var segment in segments)
            {
                if (t < segment.Length)
                {
                    return new InputCommand((uint)(step + 1), (uint)step, segment.Buttons, segment.Yaw, segment.Pitch, segment.Slot);
                }

                t -= segment.Length;
            }

            throw new InvalidOperationException("The segments cover every step.");
        }

        public override string ToString()
        {
            var characters = Records is null
                ? "not placed"
                : string.Join("; ", Records.Select(r => $"id {r.Id} at {r.Position} flags {r.Flags} normal {r.GroundNormal}"));
            var segments = string.Join(", ", _characters.Select(c => c.Segments.Length));
            return $"{_recipe}; meshes [{string.Join("; ", _meshes.Select(m => m.ToString()))}]; characters [{characters}]; command segments per character [{segments}]";
        }

        /// <summary>
        /// The heightfield: height(i, j) = base + tiltX·x + tiltZ·z + noise(i, j), with |tilt| ≤ g and noise in
        /// [0, a·cellSize], g + a = tan(maxSlope)/√2, so each triangle's two edge gradients are at most
        /// tan(maxSlope)/√2 and its slope is at most maxSlope (≤ 60°).
        /// </summary>
        private static CollisionData Terrain(WorldRecipe recipe)
        {
            var n = recipe.Samples;
            double cell = recipe.CellSize;
            var extent = (n - 1) * cell;
            var originX = (float)((-extent / 2.0) + (64.0 * recipe.OffsetX));
            var originZ = (float)((-extent / 2.0) + (64.0 * recipe.OffsetZ));
            var gradient = System.Math.Tan(recipe.MaxSlopeDegrees * System.Math.PI / 180.0) / System.Math.Sqrt(2.0);
            var tilt = gradient * recipe.TiltShare;
            var noise = gradient * (1.0 - recipe.TiltShare);
            var heights = new float[n * n];
            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++)
                {
                    var height = recipe.BaseHeight
                        + (recipe.TiltX * tilt * i * cell)
                        + (recipe.TiltZ * tilt * j * cell)
                        + (recipe.Noise[(j * n) + i] * noise * cell);
                    heights[(j * n) + i] = (float)height + 0f;
                }
            }

            return new CollisionData(originX, originZ, recipe.CellSize, GridCellSize, n, n, heights, []);
        }

        /// <summary>
        /// One mesh: a wall (a vertical quad 1 to 8 m long, rising 0.5 to 4 m above the terrain and sunk
        /// 0.5 m into it), an axis-aligned box (0.5 to 4 m on a side, top 0.25 to 2.5 m above the terrain,
        /// sunk 0.5 m, without its bottom face) or a ramp (a quad 1 to 4 m wide and 1 to 4 m long rising at 5°
        /// to 60° from a foot edge 0.05 m into the terrain). At most ten triangles, so five meshes stay inside
        /// a grid cell's 64.
        /// </summary>
        private static MeshData Mesh(uint id, MeshRecipe recipe, CollisionWorld ground)
        {
            var heightfield = ground.Heightfield;
            var cx = heightfield.MinX + (recipe.U * ((double)heightfield.MaxX - heightfield.MinX));
            var cz = heightfield.MinZ + (recipe.V * ((double)heightfield.MaxZ - heightfield.MinZ));
            var (cos, sin) = (System.Math.Cos(recipe.Angle), System.Math.Sin(recipe.Angle));
            var vertices = new List<float>();
            var indices = new List<uint>();
            double Ground(double x, double z) => ground.HeightAt((float)x, (float)z);

            switch (recipe.Kind)
            {
                case WallKind:
                    {
                        var half = (1.0 + (recipe.A * 7.0)) / 2.0;
                        var (x0, z0, x1, z1) = (cx - (cos * half), cz - (sin * half), cx + (cos * half), cz + (sin * half));
                        var low = Min(Ground(x0, z0), Ground(x1, z1), Ground(cx, cz)) - 0.5;
                        var high = Max(Ground(x0, z0), Ground(x1, z1), Ground(cx, cz)) + 0.5 + (recipe.B * 3.5);
                        Quad(vertices, indices, (x0, low, z0), (x1, low, z1), (x1, high, z1), (x0, high, z0));
                        break;
                    }

                case BoxKind:
                    {
                        var hx = 0.25 + (recipe.A * 1.75);
                        var hz = 0.25 + (recipe.B * 1.75);
                        var (minX, maxX, minZ, maxZ) = (cx - hx, cx + hx, cz - hz, cz + hz);
                        double[] under = [Ground(minX, minZ), Ground(maxX, minZ), Ground(minX, maxZ), Ground(maxX, maxZ), Ground(cx, cz)];
                        var low = under.Min() - 0.5;
                        var high = under.Max() + 0.25 + (recipe.C * 2.25);
                        Quad(vertices, indices, (minX, high, minZ), (maxX, high, minZ), (maxX, high, maxZ), (minX, high, maxZ));
                        Quad(vertices, indices, (minX, low, minZ), (maxX, low, minZ), (maxX, high, minZ), (minX, high, minZ));
                        Quad(vertices, indices, (minX, low, maxZ), (maxX, low, maxZ), (maxX, high, maxZ), (minX, high, maxZ));
                        Quad(vertices, indices, (minX, low, minZ), (minX, low, maxZ), (minX, high, maxZ), (minX, high, minZ));
                        Quad(vertices, indices, (maxX, low, minZ), (maxX, low, maxZ), (maxX, high, maxZ), (maxX, high, minZ));
                        break;
                    }

                default:
                    {
                        var halfWidth = (1.0 + (recipe.A * 3.0)) / 2.0;
                        var run = 1.0 + (recipe.B * 3.0);
                        var rise = run * System.Math.Tan((5.0 + (recipe.C * 55.0)) * System.Math.PI / 180.0);
                        var foot = Ground(cx, cz) - 0.05;
                        var (sideX, sideZ) = (-sin * halfWidth, cos * halfWidth);
                        var (runX, runZ) = (cos * run, sin * run);
                        Quad(
                            vertices,
                            indices,
                            (cx - sideX, foot, cz - sideZ),
                            (cx + sideX, foot, cz + sideZ),
                            (cx + sideX + runX, foot + rise, cz + sideZ + runZ),
                            (cx - sideX + runX, foot + rise, cz - sideZ + runZ));
                        break;
                    }
            }

            return new MeshData(id, [.. vertices], [.. indices]);
        }

        /// <summary>
        /// The first of the character's drawn starts whose standing capsule is clear: at least capsuleRadius +
        /// contactSkin from every triangle and boundary plane. It is Grounded on the triangle a vertical
        /// down-probe from the feet finds within snapDistance when that triangle is walkable, and airborne
        /// otherwise. Null when no draw is clear.
        /// </summary>
        private static CharacterRecord? Place(CollisionWorld world, uint id, CharacterRecipe recipe)
        {
            var heightfield = world.Heightfield;
            var radius = (double)PlacementConfig.CapsuleRadius;
            var clearance = radius + PlacementConfig.ContactSkin;
            var margin = clearance + 0.01;
            foreach (var draw in recipe.Draws)
            {
                var x = (float)(heightfield.MinX + margin + (draw.U * ((double)heightfield.MaxX - heightfield.MinX - (2.0 * margin)))) + 0f;
                var z = (float)(heightfield.MinZ + margin + (draw.V * ((double)heightfield.MaxZ - heightfield.MinZ - (2.0 * margin)))) + 0f;
                var y = (float)(world.HeightAt(x, z) + 0.02 + (draw.W * draw.W * 3.0)) + 0f;
                var feet = new Vec3(x, y, z);
                if (CapsuleClearanceReference.BoundaryDistance(heightfield, x, z) < clearance)
                {
                    continue;
                }

                var (bottom, top) = CapsuleClearanceReference.Axis(feet, radius, PlacementConfig.StandHeight);
                if (CapsuleClearanceReference.NearestTriangleDistance(world, bottom, top, clearance + 0.05, out _) < clearance)
                {
                    continue;
                }

                var groundNormal = (0f, 1f, 0f);
                var flags = (byte)0;
                if (CapsuleClearanceReference.TryDownProbe(world, CapsuleClearanceReference.D3.From(feet), PlacementConfig.SnapDistance, out _, out _, out var normal))
                {
                    var n = ((float)normal.X + 0f, (float)normal.Y + 0f, (float)normal.Z + 0f);
                    if (n.Item2 >= PlacementConfig.CosSlopeLimit)
                    {
                        groundNormal = n;
                        flags = TestStates.Grounded;
                    }
                }

                return new CharacterRecord
                {
                    Id = id,
                    Position = (x, y, z),
                    GroundNormal = groundNormal,
                    Flags = flags,
                    AimYaw = recipe.Yaw,
                };
            }

            return null;
        }

        private static void Quad(
            List<float> vertices,
            List<uint> indices,
            (double X, double Y, double Z) a,
            (double X, double Y, double Z) b,
            (double X, double Y, double Z) c,
            (double X, double Y, double Z) d)
        {
            var first = (uint)(vertices.Count / 3);
            (double X, double Y, double Z)[] corners = [a, b, c, d];
            foreach (var (x, y, z) in corners)
            {
                vertices.Add((float)x + 0f);
                vertices.Add((float)y + 0f);
                vertices.Add((float)z + 0f);
            }

            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        private static double Min(double a, double b, double c) => System.Math.Min(a, System.Math.Min(b, c));

        private static double Max(double a, double b, double c) => System.Math.Max(a, System.Math.Max(b, c));
    }
}

/// <summary>
/// Double-precision reference geometry for the movement tests (FR-003, research R7): the distance from a
/// capsule's axis segment to the collision world's triangles, the distance to the playable area's
/// boundary planes, and a vertical down-probe. It reads the triangles through the world's public accessors
/// (<see cref="Heightfield.GetTriangle"/> and <see cref="MeshSet.TryGetMeshTriangle"/>, so dropped degenerate
/// triangles are skipped) and computes in double, independently of the core's float closest-point code,
/// so a test compares the core with a second reading of the geometry.
/// </summary>
internal static class CapsuleClearanceReference
{
    /// <summary>
    /// The axis of a capsule of <paramref name="radius"/> and <paramref name="height"/> standing with its
    /// bottom at <paramref name="feet"/>: from feet + (0, r, 0) to feet + (0, h − r, 0).
    /// </summary>
    public static (D3 Bottom, D3 Top) Axis(Vec3 feet, double radius, double height) =>
        (new D3(feet.X, feet.Y + radius, feet.Z), new D3(feet.X, feet.Y + height - radius, feet.Z));

    /// <summary>
    /// The least distance from segment [<paramref name="p"/>, <paramref name="q"/>] to a kept triangle, among
    /// every heightfield and mesh triangle whose bounding box comes within <paramref name="reach"/> of the
    /// segment's (a triangle outside that box is further than <paramref name="reach"/>, so the result is exact
    /// whenever it is at most <paramref name="reach"/>), with the triangle's ID; +∞ and
    /// <see cref="int.MinValue"/> when there is none.
    /// </summary>
    public static double NearestTriangleDistance(CollisionWorld world, D3 p, D3 q, double reach, out int triangleId)
    {
        ArgumentNullException.ThrowIfNull(world);
        var best = double.PositiveInfinity;
        triangleId = int.MinValue;
        var box = new Box(
            System.Math.Min(p.X, q.X) - reach,
            System.Math.Min(p.Y, q.Y) - reach,
            System.Math.Min(p.Z, q.Z) - reach,
            System.Math.Max(p.X, q.X) + reach,
            System.Math.Max(p.Y, q.Y) + reach,
            System.Math.Max(p.Z, q.Z) + reach);
        foreach (var (id, a, b, c) in TrianglesIn(world, box))
        {
            var distance = SegmentTriangle(p, q, a, b, c);
            if (distance < best)
            {
                best = distance;
                triangleId = id;
            }
        }

        return best;
    }

    /// <summary>The least horizontal distance from (x, z) to the four boundary planes; negative outside the area.</summary>
    public static double BoundaryDistance(Heightfield heightfield, double x, double z)
    {
        ArgumentNullException.ThrowIfNull(heightfield);
        return System.Math.Min(
            System.Math.Min(x - heightfield.MinX, heightfield.MaxX - x),
            System.Math.Min(z - heightfield.MinZ, heightfield.MaxZ - z));
    }

    /// <summary>
    /// The nearest triangle a vertical line down from <paramref name="feet"/> crosses within
    /// <paramref name="maxDrop"/>, skipping vertical triangles: its ID, the drop to it and its unit normal
    /// turned to face up. False when the line crosses none.
    /// </summary>
    public static bool TryDownProbe(CollisionWorld world, D3 feet, double maxDrop, out int triangleId, out double drop, out D3 upNormal)
    {
        ArgumentNullException.ThrowIfNull(world);
        triangleId = int.MinValue;
        drop = double.PositiveInfinity;
        upNormal = new D3(0.0, 1.0, 0.0);
        var box = new Box(feet.X, feet.Y - maxDrop, feet.Z, feet.X, feet.Y, feet.Z);
        foreach (var (id, a, b, c) in TrianglesIn(world, box))
        {
            var normal = D3.Cross(b - a, c - a);
            var length = System.Math.Sqrt(D3.Dot(normal, normal));
            if (System.Math.Abs(normal.Y) <= 1e-12 * length || !ContainsInXz(a, b, c, feet.X, feet.Z))
            {
                continue;
            }

            var surface = a.Y - (((normal.X * (feet.X - a.X)) + (normal.Z * (feet.Z - a.Z))) / normal.Y);
            var down = feet.Y - surface;
            if (down < 0.0 || down > maxDrop || down >= drop)
            {
                continue;
            }

            drop = down;
            triangleId = id;
            upNormal = normal * ((normal.Y < 0.0 ? -1.0 : 1.0) / length);
        }

        return triangleId != int.MinValue;
    }

    /// <summary>
    /// The distance from segment [p, q] to triangle (a, b, c): 0 when the segment crosses the triangle,
    /// otherwise the least of the endpoints' distances to the triangle (Ericson 5.1.5) and the segment's
    /// distances to the triangle's three edges (Ericson 5.1.9), where the minimum of a non-crossing pair is
    /// always attained.
    /// </summary>
    public static double SegmentTriangle(D3 p, D3 q, D3 a, D3 b, D3 c)
    {
        if (SegmentCrossesTriangle(p, q, a, b, c))
        {
            return 0.0;
        }

        var squared = System.Math.Min(
            System.Math.Min(PointTriangleSquared(p, a, b, c), PointTriangleSquared(q, a, b, c)),
            System.Math.Min(
                SegmentSegmentSquared(p, q, a, b),
                System.Math.Min(SegmentSegmentSquared(p, q, b, c), SegmentSegmentSquared(p, q, c, a))));
        return System.Math.Sqrt(squared);
    }

    /// <summary>Every kept triangle, heightfield then mesh in ascending ID, whose bounding box overlaps <paramref name="box"/>.</summary>
    private static IEnumerable<(int Id, D3 A, D3 B, D3 C)> TrianglesIn(CollisionWorld world, Box box)
    {
        var heightfield = world.Heightfield;
        var cellsX = heightfield.Columns - 1;
        var cellsZ = heightfield.Rows - 1;
        var i0 = Cell(box.MinX, heightfield.OriginX, heightfield.CellSize, cellsX);
        var i1 = Cell(box.MaxX, heightfield.OriginX, heightfield.CellSize, cellsX);
        var j0 = Cell(box.MinZ, heightfield.OriginZ, heightfield.CellSize, cellsZ);
        var j1 = Cell(box.MaxZ, heightfield.OriginZ, heightfield.CellSize, cellsZ);
        for (var j = j0; j <= j1; j++)
        {
            for (var i = i0; i <= i1; i++)
            {
                var h00 = heightfield.Sample(i, j).Y;
                var h10 = heightfield.Sample(i + 1, j).Y;
                var h01 = heightfield.Sample(i, j + 1).Y;
                var h11 = heightfield.Sample(i + 1, j + 1).Y;
                var low = System.Math.Min(System.Math.Min(h00, h10), System.Math.Min(h01, h11));
                var high = System.Math.Max(System.Math.Max(h00, h10), System.Math.Max(h01, h11));
                if (high < box.MinY || low > box.MaxY)
                {
                    continue;
                }

                for (var k = 0; k < 2; k++)
                {
                    var id = (2 * ((j * cellsX) + i)) + k;
                    heightfield.GetTriangle(id, out var a, out var b, out var c);
                    yield return (id, D3.From(a), D3.From(b), D3.From(c));
                }
            }
        }

        var meshes = world.Meshes;
        var end = meshes.FirstId + meshes.TriangleCount;
        for (var id = meshes.FirstId; id < end; id++)
        {
            if (!meshes.TryGetMeshTriangle(id, out var triangle)
                || triangle.Max.X < box.MinX || triangle.Min.X > box.MaxX
                || triangle.Max.Y < box.MinY || triangle.Min.Y > box.MaxY
                || triangle.Max.Z < box.MinZ || triangle.Min.Z > box.MaxZ)
            {
                continue;
            }

            yield return (id, D3.From(triangle.A), D3.From(triangle.B), D3.From(triangle.C));
        }
    }

    /// <summary>The heightfield cell index holding <paramref name="coordinate"/>, clamped to the cells.</summary>
    private static int Cell(double coordinate, float origin, float cellSize, int cells) =>
        (int)System.Math.Clamp(System.Math.Floor((coordinate - origin) / cellSize), 0.0, cells - 1);

    /// <summary>
    /// Whether segment [p, q] crosses triangle (a, b, c): its endpoints are not strictly on one side of the
    /// plane, and the crossing point is inside or on the triangle. A segment in the plane is left to the
    /// endpoint and edge distances, which are 0 when it touches the triangle.
    /// </summary>
    private static bool SegmentCrossesTriangle(D3 p, D3 q, D3 a, D3 b, D3 c)
    {
        var normal = D3.Cross(b - a, c - a);
        var dp = D3.Dot(normal, p - a);
        var dq = D3.Dot(normal, q - a);
        if ((dp > 0.0 && dq > 0.0) || (dp < 0.0 && dq < 0.0) || dp == dq)
        {
            return false;
        }

        var x = p + ((q - p) * (dp / (dp - dq)));
        return D3.Dot(normal, D3.Cross(b - a, x - a)) >= 0.0
            && D3.Dot(normal, D3.Cross(c - b, x - b)) >= 0.0
            && D3.Dot(normal, D3.Cross(a - c, x - c)) >= 0.0;
    }

    /// <summary>The squared distance from <paramref name="p"/> to triangle (a, b, c) (Ericson 5.1.5).</summary>
    private static double PointTriangleSquared(D3 p, D3 a, D3 b, D3 c)
    {
        var closest = ClosestPointOnTriangle(p, a, b, c);
        var d = p - closest;
        return D3.Dot(d, d);
    }

    private static D3 ClosestPointOnTriangle(D3 p, D3 a, D3 b, D3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var ap = p - a;
        var d1 = D3.Dot(ab, ap);
        var d2 = D3.Dot(ac, ap);
        if (d1 <= 0.0 && d2 <= 0.0)
        {
            return a;
        }

        var bp = p - b;
        var d3 = D3.Dot(ab, bp);
        var d4 = D3.Dot(ac, bp);
        if (d3 >= 0.0 && d4 <= d3)
        {
            return b;
        }

        var vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
        {
            return a + (ab * (d1 / (d1 - d3)));
        }

        var cp = p - c;
        var d5 = D3.Dot(ab, cp);
        var d6 = D3.Dot(ac, cp);
        if (d6 >= 0.0 && d5 <= d6)
        {
            return c;
        }

        var vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
        {
            return a + (ac * (d2 / (d2 - d6)));
        }

        var va = (d3 * d6) - (d5 * d4);
        if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
        {
            return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));
        }

        var denominator = 1.0 / (va + vb + vc);
        return a + (ab * (vb * denominator)) + (ac * (vc * denominator));
    }

    /// <summary>The squared distance between segments [p1, q1] and [p2, q2] (Ericson 5.1.9), degenerate segments included.</summary>
    private static double SegmentSegmentSquared(D3 p1, D3 q1, D3 p2, D3 q2)
    {
        const double Epsilon = 1e-24;
        var d1 = q1 - p1;
        var d2 = q2 - p2;
        var r = p1 - p2;
        var a = D3.Dot(d1, d1);
        var e = D3.Dot(d2, d2);
        var f = D3.Dot(d2, r);
        double s;
        double t;
        if (a <= Epsilon && e <= Epsilon)
        {
            return D3.Dot(r, r);
        }

        if (a <= Epsilon)
        {
            s = 0.0;
            t = System.Math.Clamp(f / e, 0.0, 1.0);
        }
        else
        {
            var c = D3.Dot(d1, r);
            if (e <= Epsilon)
            {
                t = 0.0;
                s = System.Math.Clamp(-c / a, 0.0, 1.0);
            }
            else
            {
                var b = D3.Dot(d1, d2);
                var denominator = (a * e) - (b * b);
                s = denominator > 0.0 ? System.Math.Clamp(((b * f) - (c * e)) / denominator, 0.0, 1.0) : 0.0;
                t = ((b * s) + f) / e;
                if (t < 0.0)
                {
                    t = 0.0;
                    s = System.Math.Clamp(-c / a, 0.0, 1.0);
                }
                else if (t > 1.0)
                {
                    t = 1.0;
                    s = System.Math.Clamp((b - c) / a, 0.0, 1.0);
                }
            }
        }

        var difference = (p1 + (d1 * s)) - (p2 + (d2 * t));
        return D3.Dot(difference, difference);
    }

    /// <summary>Whether (x, z) lies inside or on the triangle's projection onto the X–Z plane.</summary>
    private static bool ContainsInXz(D3 a, D3 b, D3 c, double x, double z)
    {
        var e0 = ((b.X - a.X) * (z - a.Z)) - ((b.Z - a.Z) * (x - a.X));
        var e1 = ((c.X - b.X) * (z - b.Z)) - ((c.Z - b.Z) * (x - b.X));
        var e2 = ((a.X - c.X) * (z - c.Z)) - ((a.Z - c.Z) * (x - c.X));
        return (e0 >= 0.0 && e1 >= 0.0 && e2 >= 0.0) || (e0 <= 0.0 && e1 <= 0.0 && e2 <= 0.0);
    }

    /// <summary>A point or vector in double.</summary>
    internal readonly record struct D3(double X, double Y, double Z)
    {
        public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

        public static double Dot(D3 a, D3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

        public static D3 Cross(D3 a, D3 b) => new(
            (a.Y * b.Z) - (a.Z * b.Y),
            (a.Z * b.X) - (a.X * b.Z),
            (a.X * b.Y) - (a.Y * b.X));

        /// <summary>A float vector's components, exactly.</summary>
        public static D3 From(Vec3 v) => new(v.X, v.Y, v.Z);
    }

    /// <summary>An axis-aligned box in double.</summary>
    private readonly record struct Box(double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);
}
