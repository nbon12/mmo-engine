using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Movement;

/// <summary>
/// The two slide rules design review added (research R8, FR-002, data-model.md#movement-step sub-move
/// 2), at 20, 30 and 60 Hz, through <see cref="MovementStep.Step"/>: a falling character that lands on a
/// steep face clips against the true contact normal and slides down it instead of hanging on it, and a
/// character sliding along a concave wall faceted at 10° clips against each new facet alone, keeping
/// cos 10° of its speed, because the crease applies only when the clip would move it back into an
/// earlier plane of the same slide.
/// </summary>
public sealed class SlideTests
{
    /// <summary><c>movement.gravity</c>'s placeholder, m/s², as the frictionless bound uses it.</summary>
    private const double Gravity = 9.81;

    /// <summary>The steep face's height, m.</summary>
    private const float FaceHeight = 2f;

    /// <summary>How far above the face's top height the character is released, m.</summary>
    private const float ReleaseHeight = 0.5f;

    /// <summary>
    /// How far in front of the face's foot line a landing counts as "at the foot", m: a capsule resting
    /// on the floor against the face sits 0.19 m (50°) to 0.29 m (70°) in front of it, and the slide's
    /// horizontal speed carries it a few tenths further before ground deceleration stops it.
    /// </summary>
    private const double FootReach = 1.0;

    /// <summary>The faceted wall's turn per facet, degrees.</summary>
    private const float FacetDegrees = 10f;

    /// <summary>Yaw 8192, a binary angle of 45°: forward (sin 45°, 0, cos 45°), into the first facet at +X.</summary>
    private const ushort FortyFiveDegrees = 8192;

    /// <summary>How long the faceted-wall runs hold Forward: long enough to reach both joins at 45°.</summary>
    private const int FacetedRunSeconds = 3;

    /// <summary>
    /// The largest v·n, m/s, at which a velocity still counts as moving into a facet's plane: after a clip
    /// against a facet, v·n is 0 but for rounding.
    /// </summary>
    private const double IntoPlaneTolerance = 1e-3;

    /// <summary>50° and 70° faces, each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, float> SteepFaces => TestRates.With(50f, 70f);

    /// <summary>Forward held parallel to the first facet (yaw 0), and into it at 45° (yaw 8192); each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<int, string> Approaches => TestRates.With("parallel", "45-degree");

    /// <summary>
    /// Given a character released at rest, airborne (ground normal exactly (0, 1, 0)), 0.5 m above
    /// <see cref="MovementWorlds.SteepFace"/>(2, θ) and over the face, with no input, When it is stepped
    /// for N = ⌈2·(√(1/g) + √(2h/g)/sin θ)·hz⌉ steps (twice the frictionless time, g 9.81, h 2), Then it
    /// is Grounded on the floor at the face's foot (within <see cref="FootReach"/> in front of the foot
    /// line) at rest height within N steps, and still there after the N steps; its Position.y never rises
    /// from one step to the next; and it is never Grounded with GroundNormal.y below
    /// <c>CosSlopeLimit</c>, so it never stands on the face.
    /// </summary>
    [Theory]
    [MemberData(nameof(SteepFaces))]
    public void FallingOntoASteepFaceSlidesToItsFoot(int hz, float degrees)
    {
        var world = MovementWorlds.SteepFace(FaceHeight, degrees);
        var config = TestConfigs.Compiled(hz);
        var start = world.StartCharacter();
        Assert.True(!MovementStepTests.IsGrounded(start) && start.GroundNormal.Equals(new Vec3(0f, 1f, 0f)) && start.Velocity.Equals(new Vec3(0f, 0f, 0f)),
            $"The release must be at rest and airborne with ground normal exactly (0, 1, 0), not {start}");
        Assert.True(start.Position.Y == FaceHeight + ReleaseHeight && start.Position.Z > world.FrontEdge && start.Position.Z < world.BackEdge,
            $"The release must be 0.5 m above the face's top height and over the face, not at {start.Position}");
        var theta = degrees * System.Math.PI / 180.0;
        var limit = (int)System.Math.Ceiling(2.0 * (System.Math.Sqrt(1.0 / Gravity) + (System.Math.Sqrt(2.0 * FaceHeight / Gravity) / System.Math.Sin(theta))) * hz);

        var steps = MovementStepTests.Idle(world, start, limit, config);

        var landed = -1;
        for (var k = 0; k < steps.Length; k++)
        {
            var (before, after, _) = steps[k];
            Assert.True(after.Position.Y <= before.Position.Y,
                $"{degrees:R}° at {hz} Hz: step {k + 1} raised Position.y from {before.Position.Y:R} to {after.Position.Y:R}; position {after.Position}");
            if (!MovementStepTests.IsGrounded(after))
            {
                continue;
            }

            Assert.True(after.GroundNormal.Y >= config.CosSlopeLimit,
                $"{degrees:R}° at {hz} Hz: step {k + 1} left the character Grounded on normal {after.GroundNormal}, steeper than CosSlopeLimit {config.CosSlopeLimit:R}; position {after.Position}");
            if (landed < 0 && MovementStepTests.AtRestOn(after.Position.Y, world.FootPoint.Y, config))
            {
                landed = k;
            }
        }

        var end = steps[^1].After;
        Assert.True(landed >= 0,
            $"{degrees:R}° at {hz} Hz: the character was not Grounded at rest height on the floor within {limit} steps; it ended at {end.Position}, velocity {end.Velocity}, flags {end.Flags}");
        var feet = steps[landed].After.Position;
        var inFront = (double)world.FrontEdge - feet.Z;
        Assert.True(inFront > 0.0 && inFront <= FootReach,
            $"{degrees:R}° at {hz} Hz: it landed (step {landed + 1}) {inFront:R} m in front of the face's foot line, not within {FootReach} m of it; position {feet}");
        Assert.True(MovementStepTests.IsGrounded(end) && MovementStepTests.AtRestOn(end.Position.Y, world.FootPoint.Y, config),
            $"{degrees:R}° at {hz} Hz: after {limit} steps the character must still be Grounded at rest height on the floor; it is at {end.Position}, flags {end.Flags}");
        var endInFront = (double)world.FrontEdge - end.Position.Z;
        Assert.True(endInFront > 0.0 && endInFront <= FootReach,
            $"{degrees:R}° at {hz} Hz: after {limit} steps the character is {endInFront:R} m in front of the face's foot line, not within {FootReach} m of it; position {end.Position}");
    }

    /// <summary>
    /// Given <see cref="MovementWorlds.FacetedConcaveWall"/>(10), whose first facet the start touches at
    /// the skin, and Forward held at run speed parallel to the first facet or into it at 45°, When it is
    /// stepped, Then in the step whose slide reaches each join (the first step whose final velocity no
    /// longer moves into the next facet's plane, which ends near the join) the horizontal speed after the
    /// step is at least cos 10° times the speed at its start, less 1e-4 m/s; the horizontal speed is never
    /// 0 after contact; and no step reports <see cref="MovementOutcome.SlideCapReached"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Approaches))]
    public void FacetedConcaveWallKeepsItsParallelSpeed(int hz, string approach)
    {
        var world = MovementWorlds.FacetedConcaveWall(FacetDegrees);
        var config = TestConfigs.Compiled(hz);
        var yaw = approach switch
        {
            "parallel" => (ushort)0,
            "45-degree" => FortyFiveDegrees,
            _ => throw new ArgumentOutOfRangeException(nameof(approach), approach, "Unknown approach."),
        };
        var start = world.StartCharacter() with { AimYaw = yaw };
        var touching = world.Plane.SignedDistance(start.Position);
        Assert.True(System.Math.Abs(touching - ((double)config.CapsuleRadius + config.ContactSkin)) <= config.ContactTolerance,
            $"The start must touch the first facet at the skin, not {touching:R} m from it");

        var steps = MovementStepTests.Hold(world, start, Buttons.Forward, yaw, FacetedRunSeconds * hz, config);

        for (var k = 0; k < steps.Length; k++)
        {
            var (_, after, result) = steps[k];
            Assert.True(MovementStepTests.HorizontalSpeed(after.Velocity) > 0.0,
                $"{approach} at {hz} Hz: step {k + 1} stopped the character against the wall; position {after.Position}");
            Assert.True((result.Outcome & MovementOutcome.SlideCapReached) == 0,
                $"{approach} at {hz} Hz: step {k + 1} reported {result.Outcome}; position {after.Position}");
        }

        var cosFacet = System.Math.Cos(FacetDegrees * System.Math.PI / 180.0);
        var halfTurn = FacetDegrees * System.Math.PI / 360.0;
        var nearJoin = ((config.CapsuleRadius + (2.0 * config.ContactSkin)) / System.Math.Cos(halfTurn)) + ((double)config.RunSpeed * config.Dt);
        var from = 0;
        for (var join = 0; join < world.Joins.Count; join++)
        {
            var next = world.Planes[join + 1];
            var reached = -1;
            for (var k = from; k < steps.Length && reached < 0; k++)
            {
                if (IntoPlane(steps[k].After.Velocity, next) >= -IntoPlaneTolerance)
                {
                    reached = k;
                }
            }

            Assert.True(reached >= 0,
                $"{approach} at {hz} Hz: no step's slide reached join {join + 1} at {world.Joins[join]} in {steps.Length} steps; it ended at {steps[^1].After.Position}");
            var (before, after, _) = steps[reached];
            var fromJoin = Horizontal(after.Position, world.Joins[join]);
            Assert.True(fromJoin <= nearJoin,
                $"{approach} at {hz} Hz: step {reached + 1}, the first to clip against facet {join + 2}, ended {fromJoin:R} m from join {join + 1} at {world.Joins[join]}, farther than {nearJoin:R} m; position {after.Position}");
            var speedBefore = MovementStepTests.HorizontalSpeed(before.Velocity);
            var speedAfter = MovementStepTests.HorizontalSpeed(after.Velocity);
            Assert.True(speedAfter >= (cosFacet * speedBefore) - 1e-4,
                $"{approach} at {hz} Hz: in step {reached + 1}, whose slide reached join {join + 1}, the horizontal speed fell from {speedBefore:R} to {speedAfter:R} m/s, below cos 10° × {speedBefore:R} − 1e-4; velocity {before.Velocity} -> {after.Velocity}");
            from = reached + 1;
        }
    }

    /// <summary>The horizontal velocity's component along a vertical plane's normal: negative moves into it.</summary>
    private static double IntoPlane(Vec3 velocity, WorldPlane plane) =>
        ((double)velocity.X * plane.Normal.X) + ((double)velocity.Z * plane.Normal.Z);

    /// <summary>The distance between two points in x and z.</summary>
    private static double Horizontal(Vec3 a, Vec3 b)
    {
        var dx = (double)a.X - b.X;
        var dz = (double)a.Z - b.Z;
        return System.Math.Sqrt((dx * dx) + (dz * dz));
    }
}
