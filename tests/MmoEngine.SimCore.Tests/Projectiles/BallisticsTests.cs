using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Projectiles;

/// <summary>
/// FR-009, and US5 scenario 1's "its position at every step equals the closed-form ballistic position for
/// that step, computed by the core's rules": a projectile's position k steps after launch is the closed
/// form of data-model.md#projectile, <c>t = (float)k / (float)stepRateHz</c>, <c>x = x0 + (vx·t)</c>,
/// <c>y = y0 + ((vy·t) + ((HalfG·t)·t))</c>, <c>z = z0 + (vz·t)</c>, in float with that parenthesisation
/// and no fused multiply-add; and the projectile step sweeps p(k) → p(k + 1) with both ends from that one
/// function, storing p(k + 1), so the end of each step's segment is exactly the start of the next. The
/// projectile is a small-arms shot of about 598 m/s from (1.5, 1.62, −3.25), off every axis, so all three
/// components and the gravity term carry bits. Bits are compared through
/// <see cref="BitConverter.SingleToUInt32Bits"/>.
/// </summary>
public sealed class BallisticsTests
{
    private const int SmallArms = 0;

    /// <summary>
    /// The launch step of <see cref="ConsecutiveSegmentsJoinExactly"/>'s projectile: not 0, so a step that
    /// used the world step for k instead of step − LaunchStep would show.
    /// </summary>
    private const uint JoinLaunchStep = 7;

    private static readonly Vec3 Origin = new(1.5f, 1.62f, -3.25f);
    private static readonly Vec3 LaunchVelocity = new(412.7f, 35.1f, -431.9f);

    /// <summary>
    /// Given a projectile with Origin (1.5, 1.62, −3.25), LaunchVelocity (412.7, 35.1, −431.9) and
    /// <c>HalfG = CompiledConfig.HalfG[0]</c> at hz, When <c>Ballistics.PositionAt(in projectile, k, halfG, hz)</c>
    /// is called for every k from 0 to <c>LifetimeSteps[0]</c>, Then each component has exactly the bits of
    /// the contract expression written out here, and k = 0 returns Origin's bits.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void PositionAtHasTheContractBits(int hz)
    {
        var config = TestConfigs.Compiled(hz);
        var halfG = config.HalfG[SmallArms];
        var lifetime = config.LifetimeSteps[SmallArms];
        Assert.True(halfG < 0f, $"{hz} Hz: HalfG[0] must pull the projectile down, not {halfG:R}");
        Assert.True(lifetime > 1, $"{hz} Hz: LifetimeSteps[0] must cover more than one step, not {lifetime}");
        var projectile = Launch(launchStep: 0);

        AssertBits(Origin, Ballistics.PositionAt(in projectile, 0u, halfG, hz), $"{hz} Hz, k 0 (the origin)");

        for (var k = 0; k <= lifetime; k++)
        {
            var t = (float)k / (float)hz;
            var expected = new Vec3(
                Origin.X + (LaunchVelocity.X * t),
                Origin.Y + ((LaunchVelocity.Y * t) + ((halfG * t) * t)),
                Origin.Z + (LaunchVelocity.Z * t));

            AssertBits(expected, Ballistics.PositionAt(in projectile, (uint)k, halfG, hz), $"{hz} Hz, k {k}");
        }
    }

    /// <summary>
    /// Given the same projectile launched at step 7 with Position = Origin, the configuration
    /// <c>TestConfigs.At(hz)</c> with <c>projectileClasses[0].maxRangeM</c> 10000, and
    /// <see cref="ProjectileWorlds.Flat"/> with no targets, When it is stepped with <c>ProjectileStep.Step</c>
    /// for <c>LifetimeSteps[0] − 1</c> steps (k = 0 to <c>LifetimeSteps[0] − 2</c>), Then every call returns
    /// Flying with <c>hit</c> and <c>expiry</c> unset, leaves the launch fields as they were, and stores a
    /// Position with exactly the bits of <c>PositionAt(n − LaunchStep + 1)</c> after step n, so the end of
    /// each step's segment is the start of the next.
    /// </summary>
    /// <remarks>
    /// With the default 600 m range the shot would expire with Range after about one second. With the
    /// override it stays within about 1.8 km of Origin, above the ground and inside ±8192 m, which the
    /// arrange checks in double with <see cref="ProjectileReference.ClosedForm(DVec3, DVec3, double, int, long)"/>,
    /// so only the Lifetime limit could end the flight, and that first applies at k = <c>LifetimeSteps[0] − 1</c>,
    /// just past the walked steps.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ConsecutiveSegmentsJoinExactly(int hz)
    {
        var config = CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = hz;
            c.ProjectileClasses![SmallArms]!.MaxRangeM = 10000f;
        }));
        var halfG = config.HalfG[SmallArms];
        var lifetime = config.LifetimeSteps[SmallArms];
        var walked = lifetime - 1;
        Assert.True(walked >= 1, $"{hz} Hz: LifetimeSteps[0] {lifetime} leaves no step to walk");
        Assert.True(
            config.MaxRangeSq[SmallArms] == 10000f * 10000f,
            $"{hz} Hz: MaxRangeSq[0] must be 10000², not {config.MaxRangeSq[SmallArms]:R}");

        for (var k = 0; k <= walked; k++)
        {
            var point = ProjectileReference.ClosedForm(Origin, LaunchVelocity, halfG, hz, k);
            var fromOrigin = DVec3.Distance(point, Origin);
            Assert.True(
                point.Y > 1.0
                    && System.Math.Abs(point.X) < 8000.0
                    && System.Math.Abs(point.Z) < 8000.0
                    && fromOrigin < 9000.0,
                $"{hz} Hz: p({k}) = {point} ({fromOrigin:R} m from Origin) must be above the ground, inside ±8192 m and inside the 10 km range, so only the Lifetime limit can end the walk");
        }

        var collision = TestWorlds.Load(ProjectileWorlds.Flat());
        ProjectileTargets noTargets = default;
        var launch = Launch(JoinLaunchStep);
        var projectile = launch;

        for (var n = JoinLaunchStep; n < JoinLaunchStep + (uint)walked; n++)
        {
            var k = n - JoinLaunchStep;
            var outcome = ProjectileStep.Step(ref projectile, n, config, collision, noTargets, out var hit, out var expiry);

            Assert.True(
                outcome == ProjectileOutcome.Flying,
                $"{hz} Hz, step {n} (k {k}): the step must keep the projectile Flying, not return {outcome} (hit {hit}, expiry {expiry})");
            Assert.True(hit == default(ProjectileHit), $"{hz} Hz, step {n} (k {k}): a Flying step must leave hit unset, not {hit}");
            Assert.True(expiry == default(ExpiryReason), $"{hz} Hz, step {n} (k {k}): a Flying step must leave expiry unset, not {expiry}");
            Assert.True(
                (projectile with { Position = launch.Position }) == launch,
                $"{hz} Hz, step {n} (k {k}): the step may change only Position, but the projectile is now {projectile} (launched as {launch})");
            AssertBits(
                Ballistics.PositionAt(in launch, k + 1u, halfG, hz),
                projectile.Position,
                $"{hz} Hz, Position after step {n} (p(k + 1) with k {k})");
        }
    }

    /// <summary>The projectile as the weapon step spawns it at <paramref name="launchStep"/>: Position = Origin.</summary>
    private static Projectile Launch(uint launchStep) => new()
    {
        Id = 1,
        OwnerId = 1,
        Class = SmallArms,
        LaunchStep = launchStep,
        Origin = Origin,
        LaunchVelocity = LaunchVelocity,
        Position = Origin,
    };

    private static void AssertBits(Vec3 expected, Vec3 actual, string context) =>
        Assert.True(
            Bits(expected.X) == Bits(actual.X) && Bits(expected.Y) == Bits(actual.Y) && Bits(expected.Z) == Bits(actual.Z),
            $"{context}: expected {expected} (bits {Hex(expected)}), got {actual} (bits {Hex(actual)})");

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static string Hex(Vec3 vector) => $"{Bits(vector.X):x8} {Bits(vector.Y):x8} {Bits(vector.Z):x8}";
}
