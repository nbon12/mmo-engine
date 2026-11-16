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
/// axis through the feet, so it does not depend on yaw. Assertions compare bits through
/// <see cref="BitConverter.SingleToUInt32Bits"/>.
/// </summary>
public sealed class HitboxBuilderTests
{
    private const float Fx = 10.5f;
    private const float Fy = 2f;
    private const float Fz = -3.25f;

    private static readonly Vec3 Position = new(Fx, Fy, Fz);

    /// <summary>{Standing, Crouched} × the four quadrant yaws and one off-axis yaw.</summary>
    public static TheoryData<Stance, ushort> Poses
    {
        get
        {
            var data = new TheoryData<Stance, ushort>();
            foreach (var stance in new[] { Stance.Standing, Stance.Crouched })
            {
                foreach (var yaw in new ushort[] { 0, 16384, 32768, 49152, 12345 })
                {
                    data.Add(stance, yaw);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// US5 scenario 5 (a): the hitboxes follow the stance's layout. Head, Torso and Legs are written in
    /// that order, every endpoint is the contract's rotation of the configured local point, bit for bit,
    /// and every radius is the layout's. Facing +X (yaw 16384), the standing head's A is (10.53, 3.56,
    /// −3.25).
    /// </summary>
    [Theory]
    [MemberData(nameof(Poses))]
    public void WorldPointsFollowTheYawRotation(Stance stance, ushort yaw)
    {
        var config = TestConfigs.At(30);
        var layout = CompiledConfig.Create(config).HitboxLayouts[(int)stance];
        var capsules = stance == Stance.Standing ? config.Hitboxes!.Standing! : config.Hitboxes!.Crouched!;
        var hitboxes = new Hitbox[3];

        HitboxBuilder.Build(layout, Position, yaw, hitboxes);

        SimMath.SinCosBinaryAngle(yaw, out var sy, out var cy);
        HitboxPart[] parts = [HitboxPart.Head, HitboxPart.Torso, HitboxPart.Legs];
        for (var c = 0; c < 3; c++)
        {
            var label = $"{stance} yaw {yaw} {parts[c]}";
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

        if (stance == Stance.Standing && yaw == 16384)
        {
            var head = hitboxes[0].A;
            Assert.True(
                System.MathF.Abs(head.X - 10.53f) <= 1e-5f && System.MathF.Abs(head.Y - 3.56f) <= 1e-5f && System.MathF.Abs(head.Z - -3.25f) <= 1e-5f,
                $"standing head A facing +X is ({head.X:R}, {head.Y:R}, {head.Z:R}), expected (10.53, 3.56, -3.25)");
        }
    }

    /// <summary>
    /// The returned bounding sphere is centred on the vertical axis through the feet at the layout's
    /// <c>BoundCenterUp</c> (0.885 m standing, 0.57 m crouched), with the layout's <c>BoundRadius</c>, for
    /// every one of the 65,536 yaws. It encloses every capsule with the 0.01 m margin: for every endpoint
    /// e, |e − centre| + radius ≤ BoundRadius − 0.01.
    /// </summary>
    [Theory]
    [InlineData(Stance.Standing)]
    [InlineData(Stance.Crouched)]
    public void BoundingSphereEnclosesEveryCapsule(Stance stance)
    {
        var layout = TestConfigs.Compiled(30).HitboxLayouts[(int)stance];
        var expectedCenterUp = stance switch
        {
            Stance.Standing => 0.885,
            Stance.Crouched => 0.57,
            _ => throw new ArgumentOutOfRangeException(nameof(stance), stance, "No expected centre for this stance."),
        };
        Assert.True(
            System.Math.Abs(layout.BoundCenterUp - expectedCenterUp) <= 1e-6,
            $"{stance} BoundCenterUp is {layout.BoundCenterUp:R}, expected {expectedCenterUp:R} within 1e-6");

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

        Assert.True(failures.Count == 0, $"{stance}: {failures.Count} failures; first: {string.Join("; ", failures.Take(5))}");
    }

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
