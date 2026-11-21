using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Projectiles;

/// <summary>
/// T210's tick-path rule for <see cref="ProjectileTargets"/> (Phase 7 review): the world builds the targets on
/// every step, so the constructor throws nothing. The spans are meant to hold three hitboxes and one bounding
/// sphere per ID; the targets are the first n characters, n = min(IDs, ⌊hitboxes / 3⌋, bounds), and entries
/// past the shortest span are ignored, so a projectile step over them never reads past a span.
/// </summary>
public sealed class ProjectileTargetsTests
{
    private const int SmallArms = 0;

    private const uint OwnerId = 1;

    private const uint KeptId = 2;

    private const uint DroppedId = 3;

    private const uint ProjectileId = 1;

    /// <summary>The launch step and the step the projectile is stepped in, so k = 0.</summary>
    private const uint LaunchStep = 5;

    /// <summary>The kept character's feet, 10 m off the shot's line.</summary>
    private static readonly Vec3 OffTheLineFeet = new(10f, 0f, 5f);

    /// <summary>The dropped character's feet, on the shot's line, 5 m along it.</summary>
    private static readonly Vec3 OnTheLineFeet = new(0f, 0f, 5f);

    /// <summary>The shot's origin: the eye of a character standing at the origin.</summary>
    private static readonly Vec3 Origin = new(0f, 1.62f, 0f);

    /// <summary>
    /// Rows of (case, ID count, hitbox count, bounding-sphere count, expected target count): the spans as the
    /// world sizes them, each span the shortest in turn, a hitbox count that is not a multiple of three, and no
    /// IDs.
    /// </summary>
    public static TheoryData<string, int, int, int, int> SpanRows => new()
    {
        { "three-hitboxes-and-one-bound-per-id", 2, 6, 2, 2 },
        { "too-few-hitboxes", 3, 7, 3, 2 },
        { "too-few-bounds", 3, 9, 1, 1 },
        { "too-few-ids", 1, 9, 3, 1 },
        { "no-ids", 0, 6, 2, 0 },
    };

    /// <summary>
    /// Given spans of distinct character IDs, hitboxes and bounding spheres of the row's lengths, When a
    /// <see cref="ProjectileTargets"/> is made over them, Then it does not throw, and it holds the row's
    /// expected count n of characters: <c>CharacterIds</c> is the first n IDs, <c>Hitboxes</c> the first 3n
    /// hitboxes and <c>Bounds</c> the first n bounding spheres, element for element.
    /// </summary>
    [Theory]
    [MemberData(nameof(SpanRows))]
    public void ConstructorKeepsTheCharactersEverySpanCovers(string caseName, int idCount, int hitboxCount, int boundsCount, int expected)
    {
        var ids = Enumerable.Range(0, idCount).Select(i => (uint)(10 + i)).ToArray();
        var hitboxes = Enumerable.Range(0, hitboxCount)
            .Select(i => new Hitbox((HitboxPart)(i % HitboxLayout.CapsuleCount), new Vec3(i, 0f, 0f), new Vec3(i, 1f, 0f), 0.25f))
            .ToArray();
        var bounds = Enumerable.Range(0, boundsCount).Select(i => new HitboxBounds(new Vec3(i, 1f, 0f), 1f)).ToArray();

        var targets = new ProjectileTargets(ids, hitboxes, bounds);

        Assert.True(
            targets.CharacterIds.Length == expected
                && targets.Hitboxes.Length == expected * HitboxLayout.CapsuleCount
                && targets.Bounds.Length == expected,
            $"{caseName}: the targets hold {targets.CharacterIds.Length} IDs, {targets.Hitboxes.Length} hitboxes and {targets.Bounds.Length} bounding spheres; expected {expected}, {expected * HitboxLayout.CapsuleCount} and {expected}");
        Assert.True(
            targets.CharacterIds.SequenceEqual(ids.AsSpan(0, expected)),
            $"{caseName}: the IDs are [{string.Join(", ", targets.CharacterIds.ToArray())}]; expected the first {expected} of [{string.Join(", ", ids)}]");
        Assert.True(
            targets.Hitboxes.SequenceEqual(hitboxes.AsSpan(0, expected * HitboxLayout.CapsuleCount)),
            $"{caseName}: the hitboxes are not the first {expected * HitboxLayout.CapsuleCount} of the span");
        Assert.True(
            targets.Bounds.SequenceEqual(bounds.AsSpan(0, expected)),
            $"{caseName}: the bounding spheres are not the first {expected} of the span");
    }

    /// <summary>
    /// Given a level class-0 shot along +Z from (0, 1.62, 0) at 600 m/s over <see cref="ProjectileWorlds.Flat"/>,
    /// owned by character 1 and launched at step 5 (so k = 0, whose segment passes z = 5 m at every rate);
    /// character 2 standing 10 m off the line at (10, 0, 5) and character 3 standing on it at (0, 0, 5), both
    /// posed by <see cref="HitboxBuilder"/>; and the arrange check that the step over both characters' full
    /// spans hits character 3. When <c>ProjectileStep.Step</c> is called over targets made from both IDs and both
    /// bounding spheres but only character 2's three hitboxes, Then the targets hold one character, character 2,
    /// and the step returns Flying with no hit: character 3, past the shortest span, is never tested, and the
    /// step reads nothing past the spans.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void StepIgnoresTheCharactersPastTheShortestSpan(int hz)
    {
        var context = FormattableString.Invariant($"{hz} Hz");
        var config = TestConfigs.Compiled(hz);
        var layout = config.HitboxLayouts[(int)Stance.Standing];
        var hitboxes = new Hitbox[2 * HitboxLayout.CapsuleCount];
        var keptBounds = HitboxBuilder.Build(layout, OffTheLineFeet, 0, hitboxes.AsSpan(0, HitboxLayout.CapsuleCount));
        var droppedBounds = HitboxBuilder.Build(layout, OnTheLineFeet, 0, hitboxes.AsSpan(HitboxLayout.CapsuleCount));
        uint[] ids = [KeptId, DroppedId];
        HitboxBounds[] bounds = [keptBounds, droppedBounds];
        var launch = new Projectile
        {
            Id = ProjectileId,
            OwnerId = OwnerId,
            Class = SmallArms,
            LaunchStep = LaunchStep,
            Origin = Origin,
            LaunchVelocity = ProjectileWorlds.LaunchVelocity(ProjectileWorlds.Direction(0.0, 0.0), ProjectileWorlds.MuzzleSpeed(config, SmallArms)),
            Position = Origin,
        };

        var full = launch;
        var fullOutcome = ProjectileStep.Step(ref full, LaunchStep, config, ProjectileWorlds.FlatWorld, new ProjectileTargets(ids, hitboxes, bounds), out var fullHit, out _);
        Assert.True(
            fullOutcome == ProjectileOutcome.Hit && fullHit.Kind == HitKind.Character && fullHit.TargetId == DroppedId,
            $"{context}: over both characters' full spans the step must hit character {DroppedId} on the line, not return {fullOutcome} with {fullHit}");

        var cutShort = new ProjectileTargets(ids, hitboxes.AsSpan(0, HitboxLayout.CapsuleCount), bounds);
        var cutIds = cutShort.CharacterIds.ToArray();
        var stepped = launch;
        var outcome = ProjectileStep.Step(ref stepped, LaunchStep, config, ProjectileWorlds.FlatWorld, cutShort, out var hit, out var expiry);

        Assert.True(
            cutIds.Length == 1 && cutIds[0] == KeptId,
            $"{context}: the targets over two IDs, two bounding spheres and three hitboxes hold [{string.Join(", ", cutIds)}]; expected only character {KeptId}");
        Assert.True(
            outcome == ProjectileOutcome.Flying && hit == default(ProjectileHit) && expiry == default(ExpiryReason),
            $"{context}: the step returned {outcome} (hit {hit}, expiry {expiry}); character {DroppedId} is past the hitbox span, so the shot flies on");
    }
}
