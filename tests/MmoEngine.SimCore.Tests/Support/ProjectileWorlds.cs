using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// The projectile test worlds and states (US5). Three <see cref="CollisionData"/> builders share one
/// heightfield: 257 × 257 samples 4 m apart from origin (−512, −512), every height 0, so the ground spans
/// |x|, |z| ≤ <see cref="GroundHalfExtent"/>, inside the 4097-sample and ±8192 m limits of
/// contracts/collision-data-format.md#limits, and every shot of the projectile tests lands on it before
/// its range runs out (a level class-0 shot from 1.62 m meets the ground about 345 m out).
/// <see cref="Flat"/> has no mesh, <see cref="Wall"/> adds one vertical quad and <see cref="Ridge"/>
/// raises a band of rows. A world is built from them only through the file format,
/// <c>CollisionFile.Decode(CollisionFile.Encode(data))</c> (<see cref="TestWorlds.Load"/>, FR-004);
/// <see cref="FlatWorld"/>, <see cref="WallWorld"/> and <see cref="RidgeWorld"/> are those worlds, decoded
/// once and shared, because a <see cref="CollisionWorld"/> is immutable. Every builder returns new arrays,
/// so a test may change what it gets.
/// <para>
/// The states are canonical states written with <see cref="TestStates.Build"/> and loaded with
/// <c>SimWorld.Reset(state, matchSalt)</c> (<see cref="Start"/>): <see cref="Shooter"/> and
/// <see cref="Target"/> characters grounded at rest, and <see cref="InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/>
/// projectiles whose stored position is the contract's closed form, so no weapon step is needed to put a
/// projectile in flight. <c>-0</c> is written as <c>+0</c> in every vector they hold, as a committed state
/// holds it, so <c>Reset</c> never refuses one for a negative zero.
/// </para>
/// </summary>
public static class ProjectileWorlds
{
    /// <summary>Heightfield samples along X and along Z.</summary>
    public const int Samples = 257;

    /// <summary>The heightfield's cell size, in metres.</summary>
    public const float CellSize = 4f;

    /// <summary>The uniform grid's cell size, in metres.</summary>
    public const float GridCellSize = 8f;

    /// <summary>Half the ground's side: the heightfield spans |x|, |z| ≤ 512 m.</summary>
    public const float GroundHalfExtent = 512f;

    /// <summary>The number of heightfield triangles, 2·256·256; the first mesh triangle's ID.</summary>
    public const int HeightfieldTriangleCount = 2 * (Samples - 1) * (Samples - 1);

    /// <summary>The Z of <see cref="Wall"/>'s quad.</summary>
    public const float WallZ = 12.5f;

    /// <summary>Half the width of <see cref="Wall"/>'s quad: it spans x from −4 to 4 m.</summary>
    public const float WallHalfWidth = 4f;

    /// <summary>The height of <see cref="Wall"/>'s top edge; its bottom edge is on the ground, y = 0.</summary>
    public const float WallHeight = 4f;

    /// <summary>The ID of <see cref="Wall"/>'s mesh.</summary>
    public const uint WallMeshId = 1;

    /// <summary>The front edge of <see cref="Ridge"/>'s raised band, where its front face starts to rise.</summary>
    public const float RidgeFrontZ = 20f;

    /// <summary>The line along which <see cref="Ridge"/>'s crest runs, for every X.</summary>
    public const float RidgeCrestZ = 24f;

    /// <summary>The back edge of <see cref="Ridge"/>'s raised band.</summary>
    public const float RidgeBackZ = 28f;

    /// <summary>The crest's height above the flat ground, in metres.</summary>
    public const float RidgeCrestHeight = 3f;

    /// <summary>The match salt of every world <see cref="Start"/> builds.</summary>
    public const ulong Salt = 0x0015_0000_5EED_0005UL;

    private static readonly Lazy<CollisionWorld> FlatValue = new(() => TestWorlds.Load(Flat()));
    private static readonly Lazy<CollisionWorld> WallValue = new(() => TestWorlds.Load(Wall()));
    private static readonly Lazy<CollisionWorld> RidgeValue = new(() => TestWorlds.Load(Ridge()));

    /// <summary>
    /// The triangle IDs of <see cref="Wall"/>'s two triangles, in file order: the heightfield triangle count
    /// plus 0 and 1 (contracts/collision-data-format.md#triangle-ids).
    /// </summary>
    public static IReadOnlyList<int> WallTriangleIds { get; } = [HeightfieldTriangleCount, HeightfieldTriangleCount + 1];

    /// <summary><c>TestWorlds.Load(Flat())</c>, decoded once and shared.</summary>
    public static CollisionWorld FlatWorld => FlatValue.Value;

    /// <summary><c>TestWorlds.Load(Wall())</c>, decoded once and shared.</summary>
    public static CollisionWorld WallWorld => WallValue.Value;

    /// <summary><c>TestWorlds.Load(Ridge())</c>, decoded once and shared.</summary>
    public static CollisionWorld RidgeWorld => RidgeValue.Value;

    /// <summary>
    /// The flat ground: 257 × 257 samples 4 m apart from origin (−512, −512), every height 0, 8 m grid cells,
    /// no mesh.
    /// </summary>
    public static CollisionData Flat() => new(
        OriginX: -GroundHalfExtent,
        OriginZ: -GroundHalfExtent,
        CellSize: CellSize,
        GridCellSize: GridCellSize,
        Columns: Samples,
        Rows: Samples,
        Heights: new float[Samples * Samples],
        Meshes: []);

    /// <summary>
    /// <see cref="Flat"/> plus one mesh (ID <see cref="WallMeshId"/>): a vertical two-triangle quad at
    /// z = <see cref="WallZ"/>, x from −4 to 4 m and y from 0 to 4 m, whose triangles are
    /// <see cref="WallTriangleIds"/>. Triangles are double-sided, so either side can be hit.
    /// </summary>
    public static CollisionData Wall() => Flat() with
    {
        Meshes =
        [
            new MeshData(
                MeshId: WallMeshId,
                Vertices:
                [
                    -WallHalfWidth, 0f, WallZ,
                    WallHalfWidth, 0f, WallZ,
                    WallHalfWidth, WallHeight, WallZ,
                    -WallHalfWidth, WallHeight, WallZ,
                ],
                Indices:
                [
                    0, 1, 2,
                    0, 2, 3,
                ]),
        ],
    };

    /// <summary>
    /// <see cref="Flat"/> with the sample rows at z in [<see cref="RidgeFrontZ"/>, <see cref="RidgeBackZ"/>]
    /// raised, across the whole width, to a tent whose crest is <see cref="RidgeCrestHeight"/> high along
    /// z = <see cref="RidgeCrestZ"/>: each row's height is <see cref="RidgeHeightAt"/> of its Z. Those rows
    /// are 20, 24 and 28 m, so the front face rises from 0 to 3 m over z 20 to 24 m and the back face falls
    /// over 24 to 28 m, with no mesh.
    /// </summary>
    public static CollisionData Ridge()
    {
        var data = Flat();
        for (var j = 0; j < Samples; j++)
        {
            var z = (float)(data.OriginZ + ((float)j * CellSize));
            var height = RidgeHeightAt(z);
            if (height != 0.0)
            {
                data.Heights.AsSpan(j * Samples, Samples).Fill((float)height);
            }
        }

        return data;
    }

    /// <summary>
    /// The height of <see cref="Ridge"/>'s surface at <paramref name="z"/>, for every X, in double:
    /// 3·(1 − |z − 24| / 4) inside the band and 0 outside it. The band's edges and crest are sample rows,
    /// so this is the heightfield's own surface between them as well as at them.
    /// </summary>
    public static double RidgeHeightAt(double z)
    {
        var halfWidth = (double)RidgeCrestZ - RidgeFrontZ;
        var offset = System.Math.Abs(z - RidgeCrestZ);
        return offset >= halfWidth ? 0.0 : RidgeCrestHeight * (1.0 - (offset / halfWidth));
    }

    /// <summary>
    /// A shooter at <paramref name="position"/> (its feet), grounded at rest: Velocity 0, GroundNormal
    /// (0, 1, 0), the given yaw and stance, pitch 0, <c>lastSequence</c> 0, slot 0 active, with the
    /// placeholder weapons Ready. Its eye, where its shots start, is <see cref="Eye"/>.
    /// </summary>
    public static CharacterRecord Shooter(uint id, Vec3 position, ushort yaw = 0, Stance stance = Stance.Standing) =>
        Character(id, position, yaw, stance);

    /// <summary>
    /// A target at <paramref name="position"/> (its feet), grounded at rest, exactly as <see cref="Shooter"/>
    /// builds one. Yaw 32768 faces −Z, toward a shooter on its −Z side. A Crouched target stands up in its
    /// next step unless its command holds Crouch (data-model.md#stance), and stage 4 builds its hitboxes
    /// from the post-movement stance.
    /// </summary>
    public static CharacterRecord Target(uint id, Vec3 position, ushort yaw = 0, Stance stance = Stance.Standing) =>
        Character(id, position, yaw, stance);

    /// <summary>
    /// A projectile in flight at state step <paramref name="step"/>: its stored Position is the closed form
    /// p(step − launchStep + 1) (<see cref="ContractPosition"/>, data-model.md#projectile, with the class's
    /// <c>HalfG</c> and the step rate of <paramref name="config"/>), as the state committed at that step
    /// holds it. The next <c>SimWorld.Step</c> therefore sweeps p(k) → p(k + 1) with
    /// k = step + 1 − launchStep, and its earlier segments are never swept: pass that k as
    /// <c>ProjectileReference.PredictFirstHit</c>'s <c>firstK</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The class is not configured, or <paramref name="launchStep"/> is after <paramref name="step"/>.
    /// </exception>
    public static ProjectileRecord InFlight(
        uint id,
        uint ownerId,
        int classIndex,
        Vec3 origin,
        Vec3 launchVelocity,
        uint launchStep,
        uint step,
        CompiledConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentOutOfRangeException.ThrowIfNegative(classIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(classIndex, config.ProjectileClassCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(launchStep, step);

        var start = Canonical(origin);
        var velocity = Canonical(launchVelocity);
        var position = Canonical(ContractPosition(start, velocity, config.HalfG[classIndex], config.StepRateHz, checked(step - launchStep + 1u)));
        return new ProjectileRecord
        {
            Id = id,
            OwnerId = ownerId,
            Class = (byte)classIndex,
            LaunchStep = launchStep,
            Origin = AsTuple(start),
            LaunchVelocity = AsTuple(velocity),
            Position = AsTuple(position),
        };
    }

    /// <summary><see cref="InFlight(uint, uint, int, Vec3, Vec3, uint, uint, CompiledConfig)"/> with the configuration first.</summary>
    public static ProjectileRecord InFlight(
        CompiledConfig config,
        uint id,
        uint ownerId,
        int classIndex,
        Vec3 origin,
        Vec3 launchVelocity,
        uint launchStep,
        uint step) =>
        InFlight(id, ownerId, classIndex, origin, launchVelocity, launchStep, step, config);

    /// <summary>
    /// The contract's closed form in float (data-model.md#projectile): t = (float)k / (float)hz, then
    /// <c>x0 + (vx·t)</c>, <c>y0 + ((vy·t) + ((halfG·t)·t))</c> and <c>z0 + (vz·t)</c>, with that
    /// parenthesisation. Test code's own copy of the expression, written from the contract.
    /// </summary>
    public static Vec3 ContractPosition(Vec3 origin, Vec3 launchVelocity, float halfG, int hz, uint k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hz);
        var t = (float)k / (float)hz;
        return new Vec3(
            origin.X + (launchVelocity.X * t),
            origin.Y + ((launchVelocity.Y * t) + ((halfG * t) * t)),
            origin.Z + (launchVelocity.Z * t));
    }

    /// <summary>
    /// The eye of a character whose feet are at <paramref name="feet"/>: the feet plus the stance's
    /// <c>EyeHeight</c> on Y (1.62 m standing, 1.05 m crouched in the placeholders), as the weapon step
    /// places a new projectile's origin.
    /// </summary>
    public static Vec3 Eye(Vec3 feet, CompiledConfig config, Stance stance = Stance.Standing)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new Vec3(feet.X, feet.Y + config.HitboxLayouts[(int)stance].EyeHeight, feet.Z);
    }

    /// <summary>
    /// The class's default muzzle speed: that of the first weapon slot firing the class (600 m/s for class
    /// 0 and 120 m/s for class 1 in the placeholders).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No weapon fires the class.</exception>
    public static float MuzzleSpeed(CompiledConfig config, int classIndex)
    {
        ArgumentNullException.ThrowIfNull(config);
        foreach (var weapon in config.Weapons)
        {
            if (weapon.ProjectileClass == classIndex)
            {
                return weapon.MuzzleSpeed;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(classIndex), classIndex, "No weapon fires this projectile class.");
    }

    /// <summary>The unit direction from <paramref name="eye"/> to <paramref name="point"/>, computed in double and stored as float.</summary>
    /// <exception cref="ArgumentException">The point is the eye.</exception>
    public static Vec3 AimAt(DVec3 eye, DVec3 point)
    {
        var offset = point - eye;
        var length = DVec3.Length(offset);
        if (length == 0.0)
        {
            throw new ArgumentException("The point is the eye, so there is no direction to it.", nameof(point));
        }

        return Canonical(new Vec3((float)(offset.X / length), (float)(offset.Y / length), (float)(offset.Z / length)));
    }

    /// <summary>
    /// The unit launch direction whose continuous closed-form path, at <paramref name="muzzleSpeed"/> under
    /// <paramref name="halfG"/>, passes through <paramref name="point"/>: the low root
    /// tan θ = (v² − √(v⁴ − g·(g·D² + 2·H·v²))) / (g·D), with g = −2·halfG, D the horizontal distance and H
    /// the rise, computed in double and stored as float. With halfG 0 it is <see cref="AimAt"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The point is straight above or below the eye, or out of reach.</exception>
    public static Vec3 AimWithDrop(DVec3 eye, DVec3 point, double muzzleSpeed, double halfG)
    {
        if (halfG == 0.0)
        {
            return AimAt(eye, point);
        }

        var offset = point - eye;
        var horizontal = System.Math.Sqrt((offset.X * offset.X) + (offset.Z * offset.Z));
        if (horizontal == 0.0)
        {
            throw new ArgumentException("The point is straight above or below the eye.", nameof(point));
        }

        var g = -2.0 * halfG;
        var v2 = muzzleSpeed * muzzleSpeed;
        var discriminant = (v2 * v2) - (g * ((g * horizontal * horizontal) + (2.0 * offset.Y * v2)));
        if (discriminant < 0.0)
        {
            throw new ArgumentException($"The point is out of reach at {muzzleSpeed:R} m/s.", nameof(point));
        }

        var tan = (v2 - System.Math.Sqrt(discriminant)) / (g * horizontal);
        var cos = 1.0 / System.Math.Sqrt(1.0 + (tan * tan));
        var sin = tan * cos;
        return Canonical(new Vec3(
            (float)(cos * offset.X / horizontal),
            (float)sin,
            (float)(cos * offset.Z / horizontal)));
    }

    /// <summary>
    /// The unit direction with heading <paramref name="headingDegrees"/> (as yaw: 0 is +Z, 90 is +X) and
    /// pitch <paramref name="pitchDegrees"/> (positive up): (sin h·cos p, sin p, cos h·cos p), computed in
    /// double with <c>double.SinPi</c> and <c>double.CosPi</c> (exact at the quadrants) and stored as float.
    /// </summary>
    public static Vec3 Direction(double headingDegrees, double pitchDegrees)
    {
        var sinHeading = double.SinPi(headingDegrees / 180.0);
        var cosHeading = double.CosPi(headingDegrees / 180.0);
        var sinPitch = double.SinPi(pitchDegrees / 180.0);
        var cosPitch = double.CosPi(pitchDegrees / 180.0);
        return Canonical(new Vec3((float)(sinHeading * cosPitch), (float)sinPitch, (float)(cosHeading * cosPitch)));
    }

    /// <summary>The launch velocity <c>direction · muzzleSpeed</c>, in float, as the weapon step computes it.</summary>
    public static Vec3 LaunchVelocity(Vec3 direction, float muzzleSpeed) => Canonical(direction * muzzleSpeed);

    /// <summary>The core <see cref="Projectile"/> holding <paramref name="record"/>'s fields, for a direct <c>ProjectileStep</c> call.</summary>
    public static Projectile ToProjectile(ProjectileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new Projectile
        {
            Id = record.Id,
            OwnerId = record.OwnerId,
            Class = record.Class,
            LaunchStep = record.LaunchStep,
            Origin = AsVector(record.Origin),
            LaunchVelocity = AsVector(record.LaunchVelocity),
            Position = AsVector(record.Position),
        };
    }

    /// <summary>The <see cref="ProjectileRecord"/> holding <paramref name="projectile"/>'s fields, for <see cref="TestStates.Build"/>.</summary>
    public static ProjectileRecord ToRecord(Projectile projectile) => new()
    {
        Id = projectile.Id,
        OwnerId = projectile.OwnerId,
        Class = projectile.Class,
        LaunchStep = projectile.LaunchStep,
        Origin = AsTuple(projectile.Origin),
        LaunchVelocity = AsTuple(projectile.LaunchVelocity),
        Position = AsTuple(projectile.Position),
    };

    /// <summary>
    /// A <see cref="SimWorld"/> over <paramref name="collision"/> with <paramref name="config"/>, reset to
    /// <paramref name="state"/> with match salt <see cref="Salt"/>.
    /// </summary>
    /// <exception cref="CoreFormatException">The world refuses the state.</exception>
    public static SimWorld Start(CollisionWorld collision, CompiledConfig config, byte[] state)
    {
        ArgumentNullException.ThrowIfNull(collision);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(state);
        var world = new SimWorld(config, collision);
        world.Reset(state, Salt);
        return world;
    }

    private static CharacterRecord Character(uint id, Vec3 position, ushort yaw, Stance stance)
    {
        if (stance is not (Stance.Standing or Stance.Crouched))
        {
            throw new ArgumentOutOfRangeException(nameof(stance), stance, "A stance is Standing or Crouched.");
        }

        return new CharacterRecord
        {
            Id = id,
            Position = AsTuple(Canonical(position)),
            Velocity = (0f, 0f, 0f),
            GroundNormal = (0f, 1f, 0f),
            AimYaw = yaw,
            AimPitch = 0,
            Stance = (byte)stance,
            Flags = TestStates.Grounded,
        };
    }

    /// <summary>The vector with every negative zero written as positive zero, by the bit test the commit uses.</summary>
    private static Vec3 Canonical(Vec3 vector) => new(Canonical(vector.X), Canonical(vector.Y), Canonical(vector.Z));

    private static float Canonical(float value) =>
        BitConverter.SingleToUInt32Bits(value) == 0x80000000u ? 0f : value;

    private static (float X, float Y, float Z) AsTuple(Vec3 vector) => (vector.X, vector.Y, vector.Z);

    private static Vec3 AsVector((float X, float Y, float Z) vector) => new(vector.X, vector.Y, vector.Z);
}
