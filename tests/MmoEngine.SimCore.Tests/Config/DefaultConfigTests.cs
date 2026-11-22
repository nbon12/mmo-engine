using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Config;

/// <summary>
/// <c>simcore/config/default.json</c> binds, passes the guard and compiles to pinned bits at 20, 30
/// and 60 Hz (FR-029, contracts/config-rules.md#compile-time-conversions).
/// </summary>
/// <remarks>
/// Every float pin is also checked against the contract formula evaluated in double from the same
/// configuration values, so a pin that recorded a wrong value fails on its own. The pins of values
/// computed with basic IEEE 754 operations only (+, −, ×, ÷ and square root, each correctly rounded)
/// were computed by exact binary32 evaluation of the contract's expressions. <c>CosSlopeLimit</c>
/// and <c>VersMax</c> also go through <c>SimMath.Cos</c> and <c>SimMath.Sin</c>; their pins hold the
/// correctly rounded results, which the first green run confirms or re-records (tasks.md T021).
/// </remarks>
public sealed class DefaultConfigTests
{
    private const int Standing = 0;
    private const int Crouched = 1;

    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void DefaultJsonBindsValidatesAndCompilesToPinnedBits(int hz)
    {
        var load = SimConfigJson.Parse(RepoPaths.ReadAllText("simcore", "config", "default.json"));
        Assert.Empty(load.Errors);
        var config = load.Config;
        Assert.NotNull(config);
        config.StepRateHz = hz;

        Assert.Empty(ConfigGuard.Check(config));
        Assert.Empty(SimConfigValidator.Errors(config));

        var compiled = CompiledConfig.Create(config);

        // Integers (contracts/config-rules.md#compile-time-conversions).
        Assert.Equal(60 * hz, compiled.ShotCost);

        var rifle = compiled.Weapons[0];
        var launcher = compiled.Weapons[1];
        Assert.Equal(Steps(hz, 40, 60, 120), rifle.ReloadSteps);
        Assert.Equal(Steps(hz, 70, 105, 210), launcher.ReloadSteps);
        Assert.Equal(Steps(hz, 10, 15, 30), rifle.EquipSteps);
        Assert.Equal(Steps(hz, 15, 23, 45), launcher.EquipSteps);
        Assert.Equal(Steps(hz, 60, 90, 180), compiled.LifetimeSteps[0]);
        Assert.Equal(Steps(hz, 160, 240, 480), compiled.LifetimeSteps[1]);

        // Both placeholder weapons share these values.
        Assert.Equal(Steps(hz, 5, 8, 15), rifle.HeatCooldownDelaySteps);
        Assert.Equal(Steps(hz, 5, 8, 15), launcher.HeatCooldownDelaySteps);
        Assert.Equal(Steps(hz, 2, 3, 6), rifle.RecoilRecoveryDelaySteps);
        Assert.Equal(Steps(hz, 2, 3, 6), launcher.RecoilRecoveryDelaySteps);

        Assert.Equal(46, rifle.RecoilKickPitchUnits);
        Assert.Equal(46, launcher.RecoilKickPitchUnits);
        Assert.Equal(18, rifle.RecoilKickYawUnits);
        Assert.Equal(18, launcher.RecoilKickYawUnits);
        Assert.Equal(1092, rifle.RecoilMaxPitchUnits);
        Assert.Equal(1092, launcher.RecoilMaxPitchUnits);
        Assert.Equal(546, rifle.RecoilMaxYawUnits);
        Assert.Equal(546, launcher.RecoilMaxYawUnits);
        Assert.Equal(Steps(hz, 91, 61, 30), rifle.RecoilRecoveryPerStep);
        Assert.Equal(Steps(hz, 91, 61, 30), launcher.RecoilRecoveryPerStep);

        Assert.Equal(0u, rifle.HeatPerShotScaled);
        Assert.Equal(0u, launcher.HeatPerShotScaled);
        Assert.Equal(100_000u * (uint)hz, rifle.OverheatThresholdScaled);
        Assert.Equal(100_000u * (uint)hz, launcher.OverheatThresholdScaled);
        Assert.Equal(40_000u * (uint)hz, rifle.HeatRecoveryScaled);
        Assert.Equal(40_000u * (uint)hz, launcher.HeatRecoveryScaled);
        Assert.Equal(50_000u, rifle.HeatCooldownPerStep);
        Assert.Equal(50_000u, launcher.HeatCooldownPerStep);

        // Floats: the pinned bits, each within 1 ulp of the contract formula evaluated in double.
        var movement = config.Movement!;
        var weapons = config.Weapons!;
        var classes = config.ProjectileClasses!;
        var hitboxes = config.Hitboxes!;
        double radius = movement.CapsuleRadius;
        double skin = movement.ContactSkin;
        double piF = System.MathF.PI;
        var dt = 1.0 / hz;
        var pins = new PinCheck(hz);

        pins.Check("Dt", compiled.Dt, ByRate(hz, 0x3D4CCCCD, 0x3D088889, 0x3C888889), dt);
        pins.Check("GroundAccelPerStep", compiled.GroundAccelPerStep, ByRate(hz, 0x40600000, 0x40155556, 0x3F955556), movement.GroundAccel * dt);
        pins.Check("GroundDecelPerStep", compiled.GroundDecelPerStep, ByRate(hz, 0x40200000, 0x3FD55556, 0x3F555556), movement.GroundDecel * dt);
        pins.Check("AirAccelPerStep", compiled.AirAccelPerStep, ByRate(hz, 0x3ECCCCCD, 0x3E888889, 0x3E088889), movement.AirAccel * dt);
        pins.Check("GravityPerStep", compiled.GravityPerStep, ByRate(hz, 0x3EFB22D2, 0x3EA76C8C, 0x3E276C8C), movement.Gravity * dt);
        pins.Check("CosSlopeLimit", compiled.CosSlopeLimit, 0x3F3504F3, System.Math.Cos((movement.SlopeLimitDegrees * piF) / 180.0));
        pins.Check("AxisTop[standing]", compiled.AxisTop[Standing], 0x3FB33333, movement.StandHeight - radius);
        pins.Check("AxisTop[crouched]", compiled.AxisTop[Crouched], 0x3F4CCCCE, movement.CrouchHeight - radius);
        pins.Check("SweepTarget", compiled.SweepTarget, 0x3ED1EB85, radius + skin);
        pins.Check("SweepTolerance", compiled.SweepTolerance, 0x3A83126F, (double)0.1f * skin);
        pins.Check("PushOutThreshold", compiled.PushOutThreshold, 0x3ECC8B44, radius - (0.5 * ((double)0.1f * skin)));
        pins.Check("HeadroomClearance", compiled.HeadroomClearance, 0x3ECF5C29, radius + (0.5 * skin));
        pins.Check("VersMax[0]", compiled.Weapons[0].VersMax, 0x39B3A911, VersMax(weapons[0]!.SpreadHalfAngleDeg, piF));
        pins.Check("VersMax[1]", compiled.Weapons[1].VersMax, 0x381FB4CC, VersMax(weapons[1]!.SpreadHalfAngleDeg, piF));
        pins.Check("HalfG[0]", compiled.HalfG[0], 0xC09CF5C3, (-0.5 * movement.Gravity) * classes[0]!.GravityFactor);
        pins.Check("HalfG[1]", compiled.HalfG[1], 0xC09CF5C3, (-0.5 * movement.Gravity) * classes[1]!.GravityFactor);
        pins.Check("MaxRangeSq[0]", compiled.MaxRangeSq[0], 0x48AFC800, (double)classes[0]!.MaxRangeM * classes[0]!.MaxRangeM);
        pins.Check("MaxRangeSq[1]", compiled.MaxRangeSq[1], 0x481C4000, (double)classes[1]!.MaxRangeM * classes[1]!.MaxRangeM);

        var standing = Bounds(hitboxes.Standing!);
        var crouched = Bounds(hitboxes.Crouched!);
        pins.Check("HitboxLayouts[standing].BoundCenterUp", compiled.HitboxLayouts[Standing].BoundCenterUp, 0x3F628F5D, standing.CenterUp);
        pins.Check("HitboxLayouts[standing].BoundRadius", compiled.HitboxLayouts[Standing].BoundRadius, 0x3F720FB1, standing.Radius);
        pins.Check("HitboxLayouts[standing].EyeHeight", compiled.HitboxLayouts[Standing].EyeHeight, 0x3FCF5C29, hitboxes.EyeHeightStanding);
        pins.Check("HitboxLayouts[crouched].BoundCenterUp", compiled.HitboxLayouts[Crouched].BoundCenterUp, 0x3F11EB85, crouched.CenterUp);
        pins.Check("HitboxLayouts[crouched].BoundRadius", compiled.HitboxLayouts[Crouched].BoundRadius, 0x3F2E4A10, crouched.Radius);
        pins.Check("HitboxLayouts[crouched].EyeHeight", compiled.HitboxLayouts[Crouched].EyeHeight, 0x3F866666, hitboxes.EyeHeightCrouched);

        pins.AssertAllHeld();
    }

    private static int Steps(int hz, int at20, int at30, int at60) => hz switch
    {
        20 => at20,
        30 => at30,
        60 => at60,
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "No expected value for this step rate."),
    };

    private static uint ByRate(int hz, uint at20, uint at30, uint at60) => hz switch
    {
        20 => at20,
        30 => at30,
        60 => at60,
        _ => throw new ArgumentOutOfRangeException(nameof(hz), hz, "No pin for this step rate."),
    };

    /// <summary><c>VersMax</c> in double: s = sin((deg · π) / 360), then (2 · s) · s.</summary>
    private static double VersMax(float spreadHalfAngleDeg, double pi)
    {
        var s = System.Math.Sin((spreadHalfAngleDeg * pi) / 360.0);
        return (2.0 * s) * s;
    }

    /// <summary>
    /// <c>BoundCenterUp</c> and <c>BoundRadius</c> in double, from the stance's three capsules:
    /// the centre halfway between the lowest <c>a.up − radius</c> and the highest <c>b.up + radius</c>,
    /// and the largest endpoint distance from it plus the capsule radius, plus 0.01.
    /// </summary>
    private static (double CenterUp, double Radius) Bounds(IReadOnlyList<HitboxCapsuleConfig?> capsules)
    {
        var low = double.PositiveInfinity;
        var high = double.NegativeInfinity;
        foreach (var item in capsules)
        {
            var capsule = item!;
            low = System.Math.Min(low, (double)capsule.A!.Up - capsule.Radius);
            high = System.Math.Max(high, (double)capsule.B!.Up + capsule.Radius);
        }

        var center = (low + high) * 0.5;
        var largest = double.NegativeInfinity;
        foreach (var item in capsules)
        {
            var capsule = item!;
            largest = System.Math.Max(largest, EndpointReach(capsule.A!, capsule.Radius, center));
            largest = System.Math.Max(largest, EndpointReach(capsule.B!, capsule.Radius, center));
        }

        return (center, largest + (double)0.01f);
    }

    private static double EndpointReach(HitboxPointConfig e, float radius, double center)
    {
        double fwd = e.Fwd;
        double right = e.Right;
        var up = e.Up - center;
        return System.Math.Sqrt(((fwd * fwd) + (right * right)) + (up * up)) + radius;
    }

    /// <summary>Collects every pin that does not hold, so one run reports all of them.</summary>
    private sealed class PinCheck(int hz)
    {
        private readonly List<string> _failures = [];

        public void Check(string name, float actual, uint pinnedBits, double reference)
        {
            var pinned = BitConverter.UInt32BitsToSingle(pinnedBits);
            var referenceAsFloat = (float)reference;
            var distance = UlpDistance(pinned, referenceAsFloat);
            if (distance > 1)
            {
                _failures.Add(
                    $"{name} at {hz} Hz: the pin 0x{pinnedBits:X8} is {distance} ulps from the double formula's " +
                    $"0x{BitConverter.SingleToUInt32Bits(referenceAsFloat):X8} ({reference:R})");
            }

            var actualBits = BitConverter.SingleToUInt32Bits(actual);
            if (actualBits != pinnedBits)
            {
                _failures.Add($"{name} at {hz} Hz: compiled 0x{actualBits:X8} ({actual:R}), pinned 0x{pinnedBits:X8} ({pinned:R})");
            }
        }

        public void AssertAllHeld()
        {
            if (_failures.Count != 0)
            {
                Assert.Fail(string.Join("\n", _failures));
            }
        }

        /// <summary>The number of representable floats between a and b (0 when equal, ±0 equal).</summary>
        private static long UlpDistance(float a, float b) => System.Math.Abs(Ordered(a) - Ordered(b));

        private static long Ordered(float value)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            return bits < 0 ? (long)int.MinValue - bits : bits;
        }
    }
}
