using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Properties;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Movement;

/// <summary>
/// Edge case "a character exactly on a triangle edge or vertex, or wedged in a concave corner, never
/// tunnels and never oscillates between two positions; the slide loop stops within its iteration limit"
/// (spec Edge Cases), at 20, 30 and 60 Hz, through <see cref="MovementStep.Step"/>. The character's capsule
/// bottom (its feet) starts exactly on a feature: a diagonal edge or a shared vertex of a heightfield with
/// a 0.25 m bump, or a top edge or the shared top vertex of <see cref="MovementWorlds.Corner"/>'s two
/// walls. Each feature is convex and level where the feet touch it, so the capsule starts touching it at
/// exactly capsuleRadius without overlapping. Each of the eight movement directions is then held for 60
/// steps from that start. The concave corner is <c>MovementScenarioTests.CornerWedgeNeverTunnelsOrOscillates</c>.
/// </summary>
public sealed class CornerTests
{
    /// <summary>The steps each direction is held.</summary>
    private const int Steps = 60;

    /// <summary>
    /// The bump is samples (k, k) for k from <see cref="BumpFirstSample"/> to <see cref="BumpLastSample"/>
    /// raised to <see cref="BumpHeight"/>: a ridge along the cells' v00 → v11 diagonals from (−2, −2) to its
    /// end at (0, 0), with every other sample at 0.
    /// </summary>
    private const int BumpFirstSample = 30;

    /// <summary>The bump's last raised sample, (32, 32): the world point (0, 0.25, 0).</summary>
    private const int BumpLastSample = 32;

    /// <summary>The bump's height, m.</summary>
    private const float BumpHeight = 0.25f;

    /// <summary>
    /// The eight movement directions relative to yaw 0 (forward +Z, right −X): the four single buttons and
    /// the four diagonals.
    /// </summary>
    private static readonly (string Name, Buttons Buttons)[] Directions =
    [
        ("forward", Buttons.Forward),
        ("back", Buttons.Back),
        ("left", Buttons.Left),
        ("right", Buttons.Right),
        ("forward-left", Buttons.Forward | Buttons.Left),
        ("forward-right", Buttons.Forward | Buttons.Right),
        ("back-left", Buttons.Back | Buttons.Left),
        ("back-right", Buttons.Back | Buttons.Right),
    ];

    /// <summary>
    /// Rows <c>(start, hz)</c>: the bump heightfield's diagonal edge and shared vertex, and the corner's top
    /// edge and shared top vertex, each at 20, 30 and 60 Hz.
    /// </summary>
    public static TheoryData<string, int> Starts =>
        TestRates.WithRateLast("heightfield-diagonal-edge", "heightfield-shared-vertex", "mesh-edge", "mesh-vertex");

    /// <summary>
    /// Given a character whose capsule bottom starts exactly on the row's feature, Grounded at rest (it
    /// touches the feature from above, so the contact normal is (0, 1, 0)), facing yaw 0, When each of the
    /// eight movement directions is held for 60 steps from that start (one sub-case per direction), Then no
    /// step ends with the capsule axis nearer any triangle than capsuleRadius − contactTolerance (double
    /// reference distance, <see cref="CapsuleClearanceReference"/>), no step faults, the per-step
    /// positions, the start included, never contain an A, B, A, B alternation of bit-identical positions
    /// with A ≠ B, and the slide loop stops within its iteration limit: every step runs at most
    /// maxSubMoves sub-moves and at most SubMoves · (2 · maxSlideIterations + 4) + 1 sweeps (the bound of
    /// data-model.md#movement-step). So that no clause passes for a character that never leaves its
    /// start, every direction must sweep at some step and end away from the start. Every failing direction
    /// is reported.
    /// </summary>
    [Theory]
    [MemberData(nameof(Starts))]
    public void StartsOnATriangleEdgeOrVertexNeverTunnelOrOscillate(string start, int hz)
    {
        var (world, feet) = Start(start);
        var config = TestConfigs.Compiled(hz);
        var radius = (double)config.CapsuleRadius;
        var limit = radius - config.ContactTolerance;
        var candidates = new int[world.MaxQueryCandidates((3f * config.CapsuleRadius) + (2f * config.ContactSkin))];

        // The start itself touches the feature without overlapping it, or the row would test nothing.
        var (startBottom, startTop) = CapsuleClearanceReference.Axis(feet, radius, config.StandHeight);
        var startDistance = CapsuleClearanceReference.NearestTriangleDistance(world, startBottom, startTop, radius, out _);
        Assert.True(
            System.Math.Abs(startDistance - radius) <= 1e-5,
            $"{start}: the start's axis is {startDistance:R} m from the nearest triangle; on the feature it is capsuleRadius {radius:R}");

        var sweepsPerSubMove = (2 * config.MaxSlideIterations) + 4;
        var failures = new List<string>();
        foreach (var (name, buttons) in Directions)
        {
            var character = new CharacterState
            {
                Id = 1,
                Position = feet,
                Velocity = new Vec3(0f, 0f, 0f),
                GroundNormal = new Vec3(0f, 1f, 0f),
                AimYaw = 0,
                Stance = Stance.Standing,
                Flags = CharacterFlags.Grounded,
                ActiveSlot = 0,
                WeaponCount = (byte)TestStates.PlaceholderWeapons.Count,
            };
            var positions = new List<Vec3>(Steps + 1) { feet };
            var swept = false;
            for (var k = 1; k <= Steps; k++)
            {
                var input = new InputCommand((uint)k, (uint)k, buttons, 0, 0, 0);
                var before = character;
                var result = MovementStep.Step(ref character, in input, config, world, candidates);
                if (result.Fault != StepFaultCode.None)
                {
                    failures.Add($"{name}: step {k} faulted with {result.Fault}; the character was {before}");
                    break;
                }

                var sweepBound = (result.SubMoves * sweepsPerSubMove) + 1;
                if (result.SubMoves > config.MaxSubMoves || result.Sweeps > sweepBound)
                {
                    failures.Add(
                        $"{name}: step {k} ran {result.SubMoves} sub-moves (at most maxSubMoves {config.MaxSubMoves}) and {result.Sweeps} sweeps " +
                        $"(at most SubMoves · (2 · maxSlideIterations + 4) + 1 = {sweepBound}), past the slide loop's limit; before {before}, after {character}");
                    break;
                }

                swept |= result.Sweeps > 0;

                var height = character.Stance == Stance.Standing ? config.StandHeight : config.CrouchHeight;
                var (bottom, top) = CapsuleClearanceReference.Axis(character.Position, radius, height);
                var distance = CapsuleClearanceReference.NearestTriangleDistance(world, bottom, top, radius, out var triangle);
                if (distance < limit)
                {
                    failures.Add(
                        $"{name}: step {k} ends with the axis {distance:R} m from triangle {triangle}, nearer than capsuleRadius - contactTolerance = {limit:R}; " +
                        $"before {before}, after {character}");
                    break;
                }

                positions.Add(character.Position);
            }

            if (!swept)
            {
                failures.Add($"{name}: no step ran a sweep, so the slide loop's limit was never exercised");
            }

            if (character.Position.Equals(feet))
            {
                failures.Add($"{name}: the character ended where it started, {feet}, so it never left the feature");
            }

            for (var i = 0; i + 3 < positions.Count; i++)
            {
                var (a, b) = (positions[i], positions[i + 1]);
                if (!a.Equals(b) && positions[i + 2].Equals(a) && positions[i + 3].Equals(b))
                {
                    failures.Add($"{name}: the positions after steps {i} to {i + 3} alternate A, B, A, B with A = {a} and B = {b}");
                    break;
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"{start} at {hz} Hz from feet {feet}:\n{string.Join("\n", failures)}");
    }

    /// <summary>
    /// The row's world and the feet exactly on its feature:
    /// <list type="bullet">
    /// <item><c>heightfield-diagonal-edge</c>: the bump heightfield; the midpoint (−0.5, 0.25, −0.5) of cell
    /// (31, 31)'s diagonal from v00 (−1, 0.25, −1) to v11 (0, 0.25, 0), the level crest of the ridge, where the
    /// cell's two triangles meet at a convex crease (v10 and v01 are at 0).</item>
    /// <item><c>heightfield-shared-vertex</c>: the bump heightfield; the ridge's end (0, 0.25, 0), sample
    /// (32, 32), shared by six triangles: one ridge edge runs level from it and every other edge
    /// descends.</item>
    /// <item><c>mesh-edge</c>: <see cref="MovementWorlds.Corner"/>; the midpoint of the −X wall's top edge,
    /// from its far top vertex to the shared top vertex, rounded to float (within half an ulp of the
    /// edge).</item>
    /// <item><c>mesh-vertex</c>: <see cref="MovementWorlds.Corner"/>; the walls' shared top vertex
    /// (0, <see cref="MovementWorlds.WallHeight"/>, <see cref="MovementWorlds.FeatureZ"/>).</item>
    /// </list>
    /// </summary>
    private static (CollisionWorld World, Vec3 Feet) Start(string start)
    {
        switch (start)
        {
            case "heightfield-diagonal-edge":
                return (BumpWorld(), new Vec3(-0.5f, BumpHeight, -0.5f));
            case "heightfield-shared-vertex":
                return (BumpWorld(), new Vec3(0f, BumpHeight, 0f));
            case "mesh-edge":
                {
                    var corner = MovementWorlds.Corner().World;
                    var (far, shared) = CornerTopVertices(corner);
                    var midpoint = new Vec3(
                        (float)(((double)far.X + shared.X) / 2.0),
                        (float)(((double)far.Y + shared.Y) / 2.0),
                        (float)(((double)far.Z + shared.Z) / 2.0));
                    return (corner, midpoint);
                }

            case "mesh-vertex":
                {
                    var corner = MovementWorlds.Corner().World;
                    var (_, shared) = CornerTopVertices(corner);
                    return (corner, shared);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(start), start, "Unknown start.");
        }
    }

    /// <summary>
    /// A flat 65 × 65 heightfield from origin (−32, −32) (<see cref="TestWorlds.Flat"/>, sample k at
    /// k − 32 m) with samples (k, k), k from <see cref="BumpFirstSample"/> to <see cref="BumpLastSample"/>,
    /// raised to <see cref="BumpHeight"/>: a 0.25 m ridge from (−2, 0.25, −2) to (0, 0.25, 0).
    /// </summary>
    private static CollisionWorld BumpWorld()
    {
        var data = TestWorlds.Flat();
        for (var k = BumpFirstSample; k <= BumpLastSample; k++)
        {
            data.Heights[(k * data.Columns) + k] = BumpHeight;
        }

        return TestWorlds.Load(data);
    }

    /// <summary>
    /// The corner's top vertices that bound the −X wall's top edge: its far end (the least X) and the shared
    /// top vertex (on the heading line, x 0, at the largest Z), read from the decoded mesh.
    /// </summary>
    private static (Vec3 Far, Vec3 Shared) CornerTopVertices(CollisionWorld corner)
    {
        var vertices = corner.Data.Meshes[0].Vertices;
        var top = new List<Vec3>();
        for (var v = 0; v + 2 < vertices.Length; v += 3)
        {
            if (vertices[v + 1] == MovementWorlds.WallHeight)
            {
                top.Add(new Vec3(vertices[v], vertices[v + 1], vertices[v + 2]));
            }
        }

        Assert.Equal(3, top.Count);
        var far = top.MinBy(vertex => vertex.X);
        var shared = top.MaxBy(vertex => vertex.Z);
        Assert.True(
            shared.X == 0f && shared.Z == MovementWorlds.FeatureZ,
            $"The corner's shared top vertex is {shared}, not (0, {MovementWorlds.WallHeight}, {MovementWorlds.FeatureZ})");
        return (far, shared);
    }
}
