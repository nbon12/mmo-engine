using System.Collections.Immutable;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Hitboxes;

/// <summary>
/// <c>HitboxBuilder.Build</c> places a stance's three capsules (FR-011,
/// data-model.md#hitbox-layout-and-hitboxes): each local point (lf, lr, lu) relative to the feet becomes
/// <c>wx = fx + ((sy·lf) − (cy·lr))</c>, <c>wy = fy + lu</c>, <c>wz = fz + ((cy·lf) + (sy·lr))</c> with
/// <c>(sy, cy)</c> from <c>SimMath.SinCosBinaryAngle</c>, and the bounding sphere sits on the vertical
/// axis through the feet, so it does not depend on yaw. Every check runs on two layouts: the
/// placeholders of <c>simcore/config/default.json</c>, whose endpoints all have <c>right</c> 0, and a
/// sideways layout that gives every endpoint a non-zero <c>right</c> of mixed sign, so the
/// <c>cy·lr</c> and <c>sy·lr</c> terms are exercised too. Assertions compare bits through
/// <see cref="BitConverter.SingleToUInt32Bits"/>.
/// </summary>
public sealed class HitboxBuilderTests
{
    private const float Fx = 10.5f;
    private const float Fy = 2f;
    private const float Fz = -3.25f;

    /// <summary>The layout of <c>simcore/config/default.json</c>: every endpoint's <c>right</c> is 0.</summary>
    private const string PlaceholderLayout = "placeholder";

    /// <summary>The placeholders with a non-zero <c>right</c> on every endpoint (<see cref="SidewaysConfig"/>).</summary>
    private const string SidewaysLayout = "sideways";

    private static readonly Vec3 Position = new(Fx, Fy, Fz);

    /// <summary>{placeholder, sideways} × {Standing, Crouched} × the four quadrant yaws and one off-axis yaw.</summary>
    public static TheoryData<string, Stance, ushort> Poses
    {
        get
        {
            var data = new TheoryData<string, Stance, ushort>();
            foreach (var layout in new[] { PlaceholderLayout, SidewaysLayout })
            {
                foreach (var stance in new[] { Stance.Standing, Stance.Crouched })
                {
                    foreach (var yaw in new ushort[] { 0, 16384, 32768, 49152, 12345 })
                    {
                        data.Add(layout, stance, yaw);
                    }
                }
            }

            return data;
        }
    }

    /// <summary>The destinations shorter than three hitboxes, and the capsule counts other than three.</summary>
    public static TheoryData<string, int> RefusedShapes => new()
    {
        { "destination", 0 },
        { "destination", 2 },
        { "capsules", -1 },
        { "capsules", 2 },
        { "capsules", 4 },
    };

    /// <summary>{placeholder, sideways} × {Standing, Crouched}.</summary>
    public static TheoryData<string, Stance> Layouts
    {
        get
        {
            var data = new TheoryData<string, Stance>();
            foreach (var layout in new[] { PlaceholderLayout, SidewaysLayout })
            {
                foreach (var stance in new[] { Stance.Standing, Stance.Crouched })
                {
                    data.Add(layout, stance);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// US5 scenario 5 (a): the hitboxes follow the stance's layout. Head, Torso and Legs are written in
    /// that order, every endpoint is the contract's rotation of the configured local point, bit for bit,
    /// and every radius is the layout's. The formula is the code's own, so literals pin the axes and
    /// signs as well: facing +X (yaw 16384), the placeholder standing head's A is (10.53, 3.56, −3.25);
    /// the sideways standing head's A (fwd 0.03, right 0.1, up 1.56) is (10.4, 3.56, −3.22) at yaw 0 and
    /// (10.53, 3.56, −3.15) at yaw 16384.
    /// </summary>
    [Theory]
    [MemberData(nameof(Poses))]
    public void WorldPointsFollowTheYawRotation(string layoutName, Stance stance, ushort yaw)
    {
        var config = LayoutConfig(layoutName);
        var layout = CompiledConfig.Create(config).HitboxLayouts[(int)stance];
        var capsules = stance == Stance.Standing ? config.Hitboxes!.Standing! : config.Hitboxes!.Crouched!;
        var hitboxes = new Hitbox[3];

        HitboxBuilder.Build(layout, Position, yaw, hitboxes);

        SimMath.SinCosBinaryAngle(yaw, out var sy, out var cy);
        HitboxPart[] parts = [HitboxPart.Head, HitboxPart.Torso, HitboxPart.Legs];
        for (var c = 0; c < 3; c++)
        {
            var label = $"{layoutName} {stance} yaw {yaw} {parts[c]}";
            var capsule = capsules[c]!;
            Assert.Equal(parts[c], hitboxes[c].Part);
            AssertWorldPoint($"{label} A", hitboxes[c].A, capsule.A!, sy, cy);
            AssertWorldPoint($"{label} B", hitboxes[c].B, capsule.B!, sy, cy);
            Assert.True(
                Bits(hitboxes[c].Radius) == Bits(layout.Capsules[c].Radius),
                $"{label} radius is 0x{Bits(hitboxes[c].Radius):X8}, the layout's is 0x{Bits(layout.Capsules[c].Radius):X8}");
            Assert.True(
                Bits(hitboxes[c].Radius) == Bits(capsule.Radius),
                $"{label} radius is 0x{Bits(hitboxes[c].Radius):X8}, the configuration's is 0x{Bits(capsule.Radius):X8}");
        }

        if (stance == Stance.Standing)
        {
            var expected = (layoutName, yaw) switch
            {
                (PlaceholderLayout, 16384) => (10.53f, 3.56f, -3.25f),
                (SidewaysLayout, 0) => (10.4f, 3.56f, -3.22f),
                (SidewaysLayout, 16384) => (10.53f, 3.56f, -3.15f),
                _ => ((float, float, float)?)null,
            };
            if (expected is { } point)
            {
                var (x, y, z) = point;
                var head = hitboxes[0].A;
                Assert.True(
                    System.MathF.Abs(head.X - x) <= 1e-5f && System.MathF.Abs(head.Y - y) <= 1e-5f && System.MathF.Abs(head.Z - z) <= 1e-5f,
                    $"{layoutName} standing head A at yaw {yaw} is ({head.X:R}, {head.Y:R}, {head.Z:R}), expected ({x:R}, {y:R}, {z:R}) within 1e-5");
            }
        }
    }

    /// <summary>
    /// The returned bounding sphere is centred on the vertical axis through the feet at the layout's
    /// <c>BoundCenterUp</c> (0.885 m standing, 0.57 m crouched), with the layout's <c>BoundRadius</c>, for
    /// every one of the 65,536 yaws. It encloses every capsule with the 0.01 m margin: for every endpoint
    /// e, |e − centre| + radius ≤ BoundRadius − 0.01. On the sideways layout the radius has to reach the
    /// endpoints' <c>right</c> offsets too; the centre height depends only on <c>up</c> and the radii, so
    /// it is the same on both layouts.
    /// </summary>
    [Theory]
    [MemberData(nameof(Layouts))]
    public void BoundingSphereEnclosesEveryCapsule(string layoutName, Stance stance)
    {
        var layout = CompiledConfig.Create(LayoutConfig(layoutName)).HitboxLayouts[(int)stance];
        var expectedCenterUp = stance switch
        {
            Stance.Standing => 0.885,
            Stance.Crouched => 0.57,
            _ => throw new ArgumentOutOfRangeException(nameof(stance), stance, "No expected centre for this stance."),
        };
        Assert.True(
            System.Math.Abs(layout.BoundCenterUp - expectedCenterUp) <= 1e-6,
            $"{layoutName} {stance} BoundCenterUp is {layout.BoundCenterUp:R}, expected {expectedCenterUp:R} within 1e-6");

        var centreX = Bits(Fx);
        var centreY = Bits(Fy + layout.BoundCenterUp);
        var centreZ = Bits(Fz);
        var limit = ((double)layout.BoundRadius - (double)0.01f) + 1e-5;
        var hitboxes = new Hitbox[3];
        var failures = new List<string>();
        for (var yaw = 0; yaw <= ushort.MaxValue; yaw++)
        {
            var bounds = HitboxBuilder.Build(layout, Position, (ushort)yaw, hitboxes);

            if (Bits(bounds.Centre.X) != centreX || Bits(bounds.Centre.Y) != centreY || Bits(bounds.Centre.Z) != centreZ)
            {
                failures.Add($"yaw {yaw}: centre (0x{Bits(bounds.Centre.X):X8}, 0x{Bits(bounds.Centre.Y):X8}, 0x{Bits(bounds.Centre.Z):X8}), expected (0x{centreX:X8}, 0x{centreY:X8}, 0x{centreZ:X8})");
            }

            if (Bits(bounds.Radius) != Bits(layout.BoundRadius))
            {
                failures.Add($"yaw {yaw}: radius 0x{Bits(bounds.Radius):X8}, the layout's BoundRadius is 0x{Bits(layout.BoundRadius):X8}");
            }

            foreach (var hitbox in hitboxes)
            {
                foreach (var (name, endpoint) in new[] { ("A", hitbox.A), ("B", hitbox.B) })
                {
                    var reach = Distance(endpoint, bounds.Centre) + hitbox.Radius;
                    if (reach > limit)
                    {
                        failures.Add($"yaw {yaw}: {hitbox.Part} {name} reaches {reach:R}, beyond BoundRadius - 0.01 = {limit - 1e-5:R}");
                    }
                }
            }
        }

        Assert.True(failures.Count == 0, $"{layoutName} {stance}: {failures.Count} failures; first: {string.Join("; ", failures.Take(5))}");
    }

    /// <summary>
    /// Data-model.md#hitbox-layout-and-hitboxes: a layout has exactly three capsules (Head, Torso, Legs) and
    /// <c>Build</c> writes three hitboxes. Given (<c>destination</c>) a destination of 0 or 2 hitboxes, When
    /// the placeholder standing layout is built into it, Then it throws an <see cref="ArgumentException"/>
    /// whose <c>ParamName</c> is <c>destination</c> and whose message is exactly
    /// <c>The destination holds fewer than three hitboxes. (Parameter 'destination')</c>, and the
    /// destination keeps its default elements; Given (<c>capsules</c>) a default array (count −1), or 2 or 4
    /// capsules, When a <see cref="HitboxLayout"/> is constructed from them, Then it throws an
    /// <see cref="ArgumentException"/> whose <c>ParamName</c> is <c>capsules</c> and whose message is exactly
    /// <c>A hitbox layout has exactly three capsules: head, torso and legs. (Parameter 'capsules')</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedShapes))]
    public void RefusesAShortDestinationOrACapsuleCountOtherThanThree(string parameter, int count)
    {
        var layout = CompiledConfig.Create(TestConfigs.At(30)).HitboxLayouts[(int)Stance.Standing];
        ArgumentException thrown;
        string message;
        if (parameter == "destination")
        {
            var destination = new Hitbox[count];
            thrown = Assert.Throws<ArgumentException>(() => HitboxBuilder.Build(layout, Position, 0, destination));
            message = "The destination holds fewer than three hitboxes. (Parameter 'destination')";
            Assert.All(destination, hitbox => Assert.Equal(default, hitbox));
        }
        else
        {
            var capsules = count < 0 ? default : ImmutableArray.CreateRange(Enumerable.Repeat(layout.Capsules[0], count));
            thrown = Assert.Throws<ArgumentException>(() => new HitboxLayout(capsules, layout.BoundCenterUp, layout.BoundRadius, layout.EyeHeight));
            message = "A hitbox layout has exactly three capsules: head, torso and legs. (Parameter 'capsules')";
        }

        Assert.Equal(parameter, thrown.ParamName);
        Assert.Equal(message, thrown.Message);
    }

    private static SimConfig LayoutConfig(string layoutName) => layoutName switch
    {
        PlaceholderLayout => TestConfigs.At(30),
        SidewaysLayout => SidewaysConfig(),
        _ => throw new ArgumentOutOfRangeException(nameof(layoutName), layoutName, "Unknown layout."),
    };

    /// <summary>
    /// The placeholders at 30 Hz with a non-zero <c>right</c>, of mixed sign and inside the [−1, 1] range
    /// of contracts/config-rules.md, on both endpoints of every capsule of both stances: head a 0.1 and
    /// b −0.05, torso a 0.08 and b −0.08, legs a 0.12 and b −0.12. <c>fwd</c>, <c>up</c> and the radii
    /// are unchanged, so rules R15 to R17 still hold.
    /// </summary>
    private static SimConfig SidewaysConfig() => TestConfigs.With(config =>
    {
        config.StepRateHz = 30;
        var rights = new (float A, float B)[] { (0.1f, -0.05f), (0.08f, -0.08f), (0.12f, -0.12f) };
        foreach (var capsules in new[] { config.Hitboxes!.Standing!, config.Hitboxes.Crouched! })
        {
            for (var c = 0; c < rights.Length; c++)
            {
                capsules[c]!.A!.Right = rights[c].A;
                capsules[c]!.B!.Right = rights[c].B;
            }
        }
    });

    private static void AssertWorldPoint(string label, Vec3 actual, HitboxPointConfig local, float sy, float cy)
    {
        var wx = Fx + ((sy * local.Fwd) - (cy * local.Right));
        var wy = Fy + local.Up;
        var wz = Fz + ((cy * local.Fwd) + (sy * local.Right));
        Assert.True(
            Bits(actual.X) == Bits(wx) && Bits(actual.Y) == Bits(wy) && Bits(actual.Z) == Bits(wz),
            $"{label} is (0x{Bits(actual.X):X8}, 0x{Bits(actual.Y):X8}, 0x{Bits(actual.Z):X8}), expected (0x{Bits(wx):X8}, 0x{Bits(wy):X8}, 0x{Bits(wz):X8})");
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        var dx = (double)a.X - b.X;
        var dy = (double)a.Y - b.Y;
        var dz = (double)a.Z - b.Z;
        return System.Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
