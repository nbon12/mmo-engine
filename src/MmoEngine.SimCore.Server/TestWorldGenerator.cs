using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The deterministic test world the scenario streams and the agreement suite run on (spec Assumptions
/// "Test content", specs/015-shared-simulation-core/contracts/simcore-cli.md#content-generate,
/// plan.md "The test world's layout constraints are the cross-phase ones only"). <see cref="Generate"/>
/// returns the same <see cref="CollisionData"/> on every call, every run and every machine: the layout is
/// fixed, the only randomness is the open ground's height noise from a fixed seed through a counter-based
/// SplitMix64, and every height and vertex is computed with integer arithmetic, correctly rounded
/// <c>double</c> operations (<see cref="System.Math.Sqrt(double)"/> among them, never a transcendental) and one
/// conversion to <c>float</c>.
/// </summary>
/// <remarks>
/// <para>
/// The heightfield spans X and Z in [−128, 128] m (257 × 257 samples, cell size 1 m, grid cell size 4 m).
/// The north band in front of <see cref="TestWorldFeatures.FlatRunStart"/> is the firing lane: no mesh
/// vertex and no ramp, plateau, ridge or cliff lies at z ≥ <c>FlatRunStart.z</c> − 1, the lane strip
/// within 28 m of <c>FlatRunStart.x</c> is flat at height 0, and the heightfield runs 112 m in +Z from the
/// start. The only height noise (at most 0.1 m) is on the open ground of that band outside the lane strip,
/// which no scenario uses; every scenario start, path and mesh is on ground flat at height 0.
/// </para>
/// <para>
/// South of the lane: the slope lane (a 30° ramp along +X up to a plateau, a 50° ramp rising from the
/// plateau, a short top and a cliff that drops it to the ground), the ridge (a level crest along Z, 4 m
/// high, falling to the ground 8 m either side), and the meshes in ascending ID: 1 the vertical wall,
/// 2 the 0.30 m stair, 3 the 0.50 m stair, 4 the ledge slab, 5 the two walls meeting at 90 degrees and
/// 6 the zero-thickness thin wall. No mesh triangle is degenerate.
/// </para>
/// </remarks>
public static class TestWorldGenerator
{
    /// <summary>The heightfield's first sample, in X and in Z, m.</summary>
    private const float Origin = -128f;

    /// <summary>The samples per side.</summary>
    private const int Samples = 257;

    private const float CellSize = 1f;

    private const float GridCellSize = 4f;

    /// <summary>The fixed seed of the open ground's height noise.</summary>
    private const ulong NoiseSeed = 0x015_7E57_5EED_0001UL;

    /// <summary>The noise's largest magnitude, m.</summary>
    private const float NoiseAmplitude = 0.1f;

    /// <summary>A capsule at rest stands this far above the surface under it: the contact skin, m.</summary>
    private const float RestHeight = 0.01f;

    /// <summary>tan 30° = √3 / 3, correctly rounded.</summary>
    private static readonly double Tan30 = System.Math.Sqrt(3.0) / 3.0;

    /// <summary>tan 50°, as a literal so no platform's tangent can change a height.</summary>
    private const double Tan50 = 1.19175359259421;

    // The slope lane: rows z in [−106, −94], the 30° ramp over x in [−100, −93] (7 cells), the plateau
    // over [−93, −83], the 50° ramp over [−83, −79] (4 cells), the top over [−79, −76] and the cliff cell
    // from −76 to −75.
    private const int SlopeMinZ = -106;
    private const int SlopeMaxZ = -94;
    private const int RampFootX = -100;
    private const int RampCells = 7;
    private const int PlateauEndX = -83;
    private const int SteepCells = 4;
    private const int CliffX = -76;

    // The ridge: rows z in [−110, −90], the crest at x = −30, falling 0.5 m per metre to the ground at −38 and −22.
    private const int RidgeMinZ = -110;
    private const int RidgeMaxZ = -90;
    private const int RidgeX = -30;
    private const float RidgeHeight = 4f;
    private const int RidgeHalfWidth = 8;

    // The noisy open ground: z ≥ 24 and at least 28 m in X from the lane's centre line x = 4.
    private const float NoiseMinZ = 24f;
    private const float LaneHalfWidth = 28f;

    /// <summary>One turn is 65,536 binary-angle units; yaw 0 faces +Z.</summary>
    private const ushort FacingPlusZ = 0;

    /// <summary>Yaw 16,384 faces +X.</summary>
    private const ushort FacingPlusX = 16384;

    /// <summary>Yaw 8,192 faces +X and +Z at 45° to both.</summary>
    private const ushort FacingPlusXPlusZ = 8192;

    /// <summary>The 30° ramp's top and the plateau: (float)(7 · tan 30°), m.</summary>
    private static readonly float Plateau = (float)(RampCells * Tan30);

    /// <summary>
    /// The named locations and the geometry the scenario tests assert, which
    /// <c>TestWorldGeneratorTests</c> checks against the generated triangles.
    /// </summary>
    public static TestWorldFeatures Features { get; } = new()
    {
        FlatRunStart = new Vec3(4f, RestHeight, 16f),
        FlatRunYaw = FacingPlusZ,
        JumpLandStart = new Vec3(4f, RestHeight, 16f),

        WallMeshId = 1,
        WallSlideStart = new Vec3(37.5f, RestHeight, -78f),
        WallSlideYaw = FacingPlusXPlusZ,
        WallShooterStart = new Vec3(27.5f, RestHeight, -60f),
        WallTargetStart = new Vec3(52.5f, RestHeight, -60f),

        StairsClimbStart = new Vec3(-63f, RestHeight, -30f),
        StairsClimbYaw = FacingPlusX,
        StairsClimbFrontEdge = new Vec3(-60f, 0.30f, -30f),
        StairsBlockedStart = new Vec3(-63f, RestHeight, -10f),
        StairsBlockedYaw = FacingPlusX,
        StairsBlockedFrontEdge = new Vec3(-60f, 0.50f, -10f),

        SlopeLimitStart = new Vec3(-103f, RestHeight, -100f),
        SlopeLimitYaw = FacingPlusX,
        PlateauHeight = Plateau,

        RidgeShotStart = new Vec3(-60f, RestHeight, -100f),
        RidgeShotYaw = FacingPlusX,
        RidgeCrest = new TestWorldLine(new Vec3(RidgeX, RidgeHeight, -108f), new Vec3(RidgeX, RidgeHeight, -92f)),

        CrouchUnderLedgeStart = new Vec3(-2f, RestHeight, -30f),
        CrouchUnderLedgeYaw = FacingPlusX,
        LedgeUnderside = 1.5f,
        LedgeNearEdge = new Vec3(0f, 1.5f, -30f),
        LedgeFarEdge = new Vec3(4f, 1.5f, -30f),

        CornerWedgeStart = new Vec3(-4f, RestHeight, -12f),
        CornerWedgeYaw = FacingPlusXPlusZ,
        Corner = new Vec3(0f, 0f, -8f),

        ThinWallMeshId = 6,
        ThinWallFastStart = new Vec3(70f, RestHeight, -30f),
        ThinWallFastYaw = FacingPlusX,
    };

    /// <summary>The test world's collision data, the same on every call; each call returns new arrays.</summary>
    public static CollisionData Generate()
    {
        var heights = new float[Samples * Samples];
        for (var j = 0; j < Samples; j++)
        {
            for (var i = 0; i < Samples; i++)
            {
                var n = (j * Samples) + i;
                heights[n] = Height(i, j, (ulong)n);
            }
        }

        MeshData[] meshes =
        [
            Wall(),
            Stair(2, Features.StairsClimbFrontEdge),
            Stair(3, Features.StairsBlockedFrontEdge),
            Ledge(),
            CornerWalls(),
            ThinWall(),
        ];
        return new CollisionData(Origin, Origin, CellSize, GridCellSize, Samples, Samples, heights, meshes);
    }

    /// <summary>Sample (i, j)'s height: a feature's, the open ground's noise, or 0.</summary>
    private static float Height(int i, int j, ulong counter)
    {
        var x = (int)Origin + i;
        var z = (int)Origin + j;
        if (z >= SlopeMinZ && z <= SlopeMaxZ && x > RampFootX && x <= CliffX)
        {
            return SlopeLane(x);
        }

        if (z >= RidgeMinZ && z <= RidgeMaxZ && System.Math.Abs(x - RidgeX) < RidgeHalfWidth)
        {
            return RidgeHeight - (0.5f * System.Math.Abs(x - RidgeX));
        }

        var start = Features.FlatRunStart;
        if (z >= NoiseMinZ && System.Math.Abs(x - start.X) >= LaneHalfWidth)
        {
            return Noise(counter);
        }

        return 0f;
    }

    /// <summary>
    /// The slope lane's height at x in (−100, −76]: the 30° ramp (row k at (float)(k · tan 30°)), the
    /// plateau, the 50° ramp (row k at (float)(plateau + k · tan 50°)) and its top. The ground beyond, from
    /// x = −75, is at 0, so the cell from −76 to −75 is the cliff.
    /// </summary>
    private static float SlopeLane(int x)
    {
        var ramp = x - RampFootX;
        if (ramp <= RampCells)
        {
            return (float)(ramp * Tan30);
        }

        if (x <= PlateauEndX)
        {
            return Plateau;
        }

        var steep = System.Math.Min(x - PlateauEndX, SteepCells);
        return (float)(Plateau + (steep * Tan50));
    }

    /// <summary>A height in [−0.1, 0.1) m from the top 24 bits of SplitMix64 at <paramref name="counter"/>.</summary>
    private static float Noise(ulong counter)
    {
        var bits = (int)(SplitMix64(NoiseSeed, counter) >> 40);
        return NoiseAmplitude * (((float)bits / 8388608f) - 1f);
    }

    /// <summary>
    /// SplitMix64 (Steele, Lea and Flood) of the counter: the generator's state after counter + 1 steps
    /// from <paramref name="seed"/>, mixed. Pure integer arithmetic, so every machine gets the same bits.
    /// </summary>
    private static ulong SplitMix64(ulong seed, ulong counter)
    {
        unchecked
        {
            var z = seed + ((counter + 1UL) * 0x9E3779B97F4A7C15UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>
    /// Mesh 1, the vertical wall: one zero-thickness quad in the plane x = 40, z in [−80, −50], y in
    /// [0, 3]. <c>wall-slide-45</c> slides along its west face, and the eye line between
    /// <c>WallShooterStart</c> and <c>WallTargetStart</c> (z = −60, 12.5 m either side) crosses it.
    /// </summary>
    private static MeshData Wall()
    {
        var mesh = new MeshBuilder();
        mesh.Quad(new Vec3(40f, 0f, -80f), new Vec3(40f, 0f, -50f), new Vec3(40f, 3f, -50f), new Vec3(40f, 3f, -80f));
        return mesh.Build(1);
    }

    /// <summary>
    /// A stair: a box without its bottom face, its front face in the plane x = <paramref name="frontEdge"/>.x
    /// (facing −X, the walk heading's opposite), its level top at <paramref name="frontEdge"/>.y above the
    /// flat ground, 25 m deep along +X and 6 m wide, centred on z = <paramref name="frontEdge"/>.z.
    /// </summary>
    private static MeshData Stair(uint id, Vec3 frontEdge)
    {
        var mesh = new MeshBuilder();
        mesh.Box(frontEdge.X, 0f, frontEdge.Z - 3f, frontEdge.X + 25f, frontEdge.Y, frontEdge.Z + 3f, bottom: false);
        return mesh.Build(id);
    }

    /// <summary>
    /// Mesh 4, the ledge: a closed slab 0.5 m thick, its level underside 1.5 m above the flat ground,
    /// x in [0, 4] (4 m deep along the <c>crouch-under-ledge</c> heading, +X) and z in [−33, −27].
    /// </summary>
    private static MeshData Ledge()
    {
        var near = Features.LedgeNearEdge;
        var far = Features.LedgeFarEdge;
        var underside = Features.LedgeUnderside;
        var mesh = new MeshBuilder();
        mesh.Box(near.X, underside, near.Z - 3f, far.X, underside + 0.5f, near.Z + 3f, bottom: true);
        return mesh.Build(4);
    }

    /// <summary>
    /// Mesh 5, the concave corner: two zero-thickness vertical walls, 6 m long and 3 m high, meeting at
    /// 90 degrees along the vertical line through <c>Corner</c> (0, −8): one in the plane x = 0 for z in
    /// [−14, −8], the other in the plane z = −8 for x in [−6, 0]. <c>CornerWedgeStart</c> faces the corner
    /// along the bisector, at 45° to both.
    /// </summary>
    private static MeshData CornerWalls()
    {
        var corner = Features.Corner;
        var mesh = new MeshBuilder();
        mesh.Quad(
            new Vec3(corner.X, 0f, corner.Z - 6f),
            new Vec3(corner.X, 0f, corner.Z),
            new Vec3(corner.X, 3f, corner.Z),
            new Vec3(corner.X, 3f, corner.Z - 6f));
        mesh.Quad(
            new Vec3(corner.X - 6f, 0f, corner.Z),
            new Vec3(corner.X, 0f, corner.Z),
            new Vec3(corner.X, 3f, corner.Z),
            new Vec3(corner.X - 6f, 3f, corner.Z));
        return mesh.Build(5);
    }

    /// <summary>
    /// Mesh 6, the thin wall: a single zero-thickness quad in the plane x = 80, z in [−33, −27], y in
    /// [0, 3], 10 m ahead of <c>ThinWallFastStart</c> along +X.
    /// </summary>
    private static MeshData ThinWall()
    {
        var mesh = new MeshBuilder();
        mesh.Quad(new Vec3(80f, 0f, -33f), new Vec3(80f, 0f, -27f), new Vec3(80f, 3f, -27f), new Vec3(80f, 3f, -33f));
        return mesh.Build(6);
    }

    /// <summary>Collects one mesh's vertices and index triples.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<float> _vertices = [];
        private readonly List<uint> _indices = [];

        /// <summary>The quad a, b, c, d (in order around its edge) as the triangles (a, b, c) and (a, c, d).</summary>
        public void Quad(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
        {
            var first = (uint)(_vertices.Count / 3);
            Vertex(a);
            Vertex(b);
            Vertex(c);
            Vertex(d);
            _indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        /// <summary>
        /// The axis-aligned box's top, its four sides and, when <paramref name="bottom"/>, its bottom, each
        /// face a <see cref="Quad"/>.
        /// </summary>
        public void Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, bool bottom)
        {
            Quad(new Vec3(minX, maxY, minZ), new Vec3(minX, maxY, maxZ), new Vec3(maxX, maxY, maxZ), new Vec3(maxX, maxY, minZ));
            if (bottom)
            {
                Quad(new Vec3(minX, minY, minZ), new Vec3(maxX, minY, minZ), new Vec3(maxX, minY, maxZ), new Vec3(minX, minY, maxZ));
            }

            Quad(new Vec3(minX, minY, minZ), new Vec3(minX, minY, maxZ), new Vec3(minX, maxY, maxZ), new Vec3(minX, maxY, minZ));
            Quad(new Vec3(maxX, minY, minZ), new Vec3(maxX, maxY, minZ), new Vec3(maxX, maxY, maxZ), new Vec3(maxX, minY, maxZ));
            Quad(new Vec3(minX, minY, minZ), new Vec3(minX, maxY, minZ), new Vec3(maxX, maxY, minZ), new Vec3(maxX, minY, minZ));
            Quad(new Vec3(minX, minY, maxZ), new Vec3(maxX, minY, maxZ), new Vec3(maxX, maxY, maxZ), new Vec3(minX, maxY, maxZ));
        }

        public MeshData Build(uint id) => new(id, [.. _vertices], [.. _indices]);

        private void Vertex(Vec3 v) => _vertices.AddRange([v.X, v.Y, v.Z]);
    }
}

/// <summary>A segment of the test world, from <paramref name="From"/> to <paramref name="To"/>, such as the ridge's crest line.</summary>
public sealed record TestWorldLine(Vec3 From, Vec3 To);

/// <summary>
/// What <see cref="TestWorldGenerator.Features"/> names: each scenario's start position and facing yaw, and
/// the geometry the scenario tests assert. Every <see cref="Vec3"/> property whose name ends in
/// <c>Start</c> is a start at rest height (the contact skin above the surface under it) on flat ground at
/// height 0; a yaw is a binary angle, 65,536 per turn, facing (sin ψ, 0, cos ψ). <c>TestWorldGeneratorTests</c>
/// reads the properties by reflection, so every public instance property here is a feature.
/// </summary>
public sealed record TestWorldFeatures
{
    /// <summary>
    /// The start the flat runs, the weapon scenarios and <c>launcher-arc</c> share, facing yaw 0 (+Z) along
    /// the firing lane. Its x is at least 1 m, so a flat run's <c>position.x</c> stays positive and one ulp
    /// more is its bits + 1.
    /// </summary>
    public Vec3 FlatRunStart { get; init; }

    /// <summary>The flat runs' heading, yaw 0 (+Z), along the firing lane.</summary>
    public ushort FlatRunYaw { get; init; }

    /// <summary><c>jump-land</c>'s start, on the flat run area.</summary>
    public Vec3 JumpLandStart { get; init; }

    /// <summary>The mesh ID of the vertical wall <c>wall-slide-45</c> slides along and the wall pair shoots across.</summary>
    public uint WallMeshId { get; init; }

    /// <summary><c>wall-slide-45</c>'s start: 2.5 m from the wall's plane, so about 3 m along its heading before contact.</summary>
    public Vec3 WallSlideStart { get; init; }

    /// <summary><c>wall-slide-45</c>'s heading, 45° into the wall.</summary>
    public ushort WallSlideYaw { get; init; }

    /// <summary>The shooter of the wall pair, 12.5 m in front of the wall.</summary>
    public Vec3 WallShooterStart { get; init; }

    /// <summary>The target of the wall pair, 12.5 m behind the wall, 25 m from the shooter.</summary>
    public Vec3 WallTargetStart { get; init; }

    /// <summary><c>stairs-climb</c>'s start, 3 m before the 0.30 m stair's front face.</summary>
    public Vec3 StairsClimbStart { get; init; }

    /// <summary><c>stairs-climb</c>'s heading, perpendicular to the stair's front face.</summary>
    public ushort StairsClimbYaw { get; init; }

    /// <summary>Where the <c>stairs-climb</c> walk line crosses the 0.30 m stair's front face, at its top height.</summary>
    public Vec3 StairsClimbFrontEdge { get; init; }

    /// <summary><c>stairs-blocked</c>'s start, 3 m before the 0.50 m stair's front face.</summary>
    public Vec3 StairsBlockedStart { get; init; }

    /// <summary><c>stairs-blocked</c>'s heading, perpendicular to the stair's front face.</summary>
    public ushort StairsBlockedYaw { get; init; }

    /// <summary>Where the <c>stairs-blocked</c> walk line crosses the 0.50 m stair's front face, at its top height.</summary>
    public Vec3 StairsBlockedFrontEdge { get; init; }

    /// <summary><c>slope-limit</c>'s start, 3 m before the 30° ramp's foot.</summary>
    public Vec3 SlopeLimitStart { get; init; }

    /// <summary><c>slope-limit</c>'s heading, up the 30° ramp, across the plateau and into the 50° ramp.</summary>
    public ushort SlopeLimitYaw { get; init; }

    /// <summary>The plateau's height, where the 30° ramp ends and the 50° ramp begins, m.</summary>
    public float PlateauHeight { get; init; }

    /// <summary>The ridge shooter's start, on flat ground 30 m from the crest.</summary>
    public Vec3 RidgeShotStart { get; init; }

    /// <summary>The ridge shooter's heading, square to the crest.</summary>
    public ushort RidgeShotYaw { get; init; }

    /// <summary>Two points of the ridge's crest line, both at the crest height.</summary>
    public TestWorldLine RidgeCrest { get; init; } = new(default, default);

    /// <summary><c>crouch-under-ledge</c>'s start, 2 m before the ledge's near edge.</summary>
    public Vec3 CrouchUnderLedgeStart { get; init; }

    /// <summary><c>crouch-under-ledge</c>'s heading, across the 4 m deep ledge.</summary>
    public ushort CrouchUnderLedgeYaw { get; init; }

    /// <summary>The height of the ledge's underside, 1.5 m above the floor, m.</summary>
    public float LedgeUnderside { get; init; }

    /// <summary>Where the walk line crosses the ledge's near edge, at the underside.</summary>
    public Vec3 LedgeNearEdge { get; init; }

    /// <summary>Where the walk line crosses the ledge's far edge, 4 m beyond the near edge, at the underside.</summary>
    public Vec3 LedgeFarEdge { get; init; }

    /// <summary><c>corner-wedge</c>'s start, facing the corner along its bisector.</summary>
    public Vec3 CornerWedgeStart { get; init; }

    /// <summary><c>corner-wedge</c>'s heading, at 45° to both walls.</summary>
    public ushort CornerWedgeYaw { get; init; }

    /// <summary>A point on the vertical line where the corner's two walls meet, at the ground.</summary>
    public Vec3 Corner { get; init; }

    /// <summary>The mesh ID of the single zero-thickness quad <c>thin-wall-fast</c> runs into.</summary>
    public uint ThinWallMeshId { get; init; }

    /// <summary><c>thin-wall-fast</c>'s start, 10 m before the thin wall.</summary>
    public Vec3 ThinWallFastStart { get; init; }

    /// <summary><c>thin-wall-fast</c>'s heading, square into the thin wall.</summary>
    public ushort ThinWallFastYaw { get; init; }
}
