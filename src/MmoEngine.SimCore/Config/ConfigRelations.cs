using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Config;

/// <summary>Which instances a relation is checked over, in ascending order.</summary>
public enum ConfigRelationScope : byte
{
    /// <summary>Checked once.</summary>
    Once = 0,

    /// <summary>Checked for each weapon slot i present.</summary>
    EachWeapon = 1,

    /// <summary>Checked for each capsule c of each stance: standing 0 to 2, then crouched 0 to 2.</summary>
    EachCapsule = 2,

    /// <summary>Checked for each standing capsule c, 0 to 2.</summary>
    StandingCapsules = 3,

    /// <summary>Checked for each crouched capsule c, 0 to 2.</summary>
    CrouchedCapsules = 4,
}

/// <summary>
/// One cross-field rule of the rule table (specs/015-shared-simulation-core/contracts/config-rules.md#relations).
/// <see cref="PathTemplate"/> and <see cref="MessageTemplate"/> carry <c>{i}</c> (the weapon slot),
/// <c>{stance}</c> and <c>{c}</c> (the capsule), and the message <c>{n}</c> (the value it names), which
/// the guard replaces for the instance it reports.
/// </summary>
public sealed class ConfigRelation
{
    private readonly Func<SimConfig, int, int, bool> _holds;
    private readonly Func<SimConfig, int, int, int>? _value;

    internal ConfigRelation(
        string id,
        ConfigRelationScope scope,
        string pathTemplate,
        string messageTemplate,
        Func<SimConfig, int, int, bool> holds,
        Func<SimConfig, int, int, int>? value = null)
    {
        Id = id;
        Scope = scope;
        PathTemplate = pathTemplate;
        MessageTemplate = messageTemplate;
        _holds = holds;
        _value = value;
    }

    /// <summary><c>R01</c> to <c>R19</c>.</summary>
    public string Id { get; }

    /// <summary>The instances the relation is checked over.</summary>
    public ConfigRelationScope Scope { get; }

    /// <summary>The path the error names, such as <c>weapons[{i}].roundsPerMinute</c>.</summary>
    public string PathTemplate { get; }

    /// <summary>The message, such as <c>must be at most weapons[{i}].maxShotsPerStep times 60 times stepRateHz ({n})</c>.</summary>
    public string MessageTemplate { get; }

    /// <summary>
    /// Appends this relation's error for every instance of its scope, in ascending order, where it does
    /// not hold. The configuration must have passed the shape and field checks.
    /// </summary>
    internal void Check(SimConfig config, List<ConfigError> errors)
    {
        switch (Scope)
        {
            case ConfigRelationScope.Once:
                CheckInstance(config, 0, 0, errors);
                break;

            case ConfigRelationScope.EachWeapon:
                for (var i = 0; i < config.Weapons!.Count; i++)
                {
                    CheckInstance(config, i, 0, errors);
                }

                break;

            case ConfigRelationScope.EachCapsule:
                for (var s = 0; s < ConfigFields.StanceCount; s++)
                {
                    CheckCapsules(config, s, errors);
                }

                break;

            case ConfigRelationScope.StandingCapsules:
                CheckCapsules(config, 0, errors);
                break;

            case ConfigRelationScope.CrouchedCapsules:
                CheckCapsules(config, 1, errors);
                break;

            default:
                throw new InvalidOperationException($"Relation {Id} has an unknown scope {Scope}.");
        }
    }

    private void CheckCapsules(SimConfig config, int stance, List<ConfigError> errors)
    {
        for (var c = 0; c < ConfigFields.CapsulesPerStance; c++)
        {
            CheckInstance(config, stance, c, errors);
        }
    }

    /// <summary>
    /// Checks one instance: the weapon slot as <paramref name="instance"/>, or the stance as
    /// <paramref name="instance"/> with the capsule <paramref name="capsule"/>.
    /// </summary>
    private void CheckInstance(SimConfig config, int instance, int capsule, List<ConfigError> errors)
    {
        if (_holds(config, instance, capsule))
        {
            return;
        }

        var n = _value is null ? string.Empty : _value(config, instance, capsule).ToString(CultureInfo.InvariantCulture);
        errors.Add(new ConfigError(Substitute(PathTemplate, instance, capsule, n), Substitute(MessageTemplate, instance, capsule, n)));
    }

    private string Substitute(string template, int instance, int capsule, string n)
    {
        var text = template.Replace("{n}", n, StringComparison.Ordinal);
        return Scope switch
        {
            ConfigRelationScope.EachWeapon => text.Replace("{i}", instance.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            ConfigRelationScope.EachCapsule or ConfigRelationScope.StandingCapsules or ConfigRelationScope.CrouchedCapsules => text
                .Replace("{stance}", ConfigFields.StanceName(instance), StringComparison.Ordinal)
                .Replace("{c}", capsule.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            _ => text,
        };
    }
}

/// <summary>
/// The rule table's cross-field rules R01 to R19 (specs/015-shared-simulation-core/contracts/config-rules.md#relations).
/// Every formula is evaluated in <see cref="float"/> with the contract's exact parenthesisation, or in
/// <see cref="int"/>, so every build accepts and refuses the same configurations.
/// </summary>
public static class ConfigRelations
{
    /// <summary>R01 to R19, in the order the guard checks them.</summary>
    public static ImmutableArray<ConfigRelation> Rows { get; } =
    [
        new(
            "R01",
            ConfigRelationScope.Once,
            "movement.walkSpeed",
            "must be at least movement.crouchSpeed",
            static (c, _, _) => c.Movement!.WalkSpeed >= c.Movement!.CrouchSpeed),
        new(
            "R02",
            ConfigRelationScope.Once,
            "movement.runSpeed",
            "must be at least movement.walkSpeed",
            static (c, _, _) => c.Movement!.RunSpeed >= c.Movement!.WalkSpeed),
        new(
            "R03",
            ConfigRelationScope.Once,
            "movement.crouchHeight",
            "must be greater than twice movement.capsuleRadius plus 0.05",
            static (c, _, _) => c.Movement!.CrouchHeight > ((2f * c.Movement!.CapsuleRadius) + 0.05f)),
        new(
            "R04",
            ConfigRelationScope.Once,
            "movement.crouchHeight",
            "must be less than movement.standHeight",
            static (c, _, _) => c.Movement!.CrouchHeight < c.Movement!.StandHeight),
        new(
            "R05",
            ConfigRelationScope.Once,
            "movement.stepHeight",
            "must be at least movement.capsuleRadius times (1 - cos movement.slopeLimitDegrees)",
            static (c, _, _) => c.Movement!.StepHeight >= (c.Movement!.CapsuleRadius * (1f - CosSlopeLimit(c.Movement!)))),
        new(
            "R06",
            ConfigRelationScope.Once,
            "movement.snapDistance",
            "must be at most twice movement.stepHeight",
            static (c, _, _) => c.Movement!.SnapDistance <= (2f * c.Movement!.StepHeight)),
        new(
            "R07",
            ConfigRelationScope.Once,
            "movement.contactTolerance",
            "must be between 0.05 and 0.5 times movement.contactSkin",
            static (c, _, _) => c.Movement!.ContactTolerance >= (c.Movement!.ContactSkin * 0.05f)
                && c.Movement!.ContactTolerance <= (c.Movement!.ContactSkin * 0.5f)),
        new(
            "R08",
            ConfigRelationScope.Once,
            "movement.ledgeSupportMinNormalY",
            "must be at most the cosine of movement.slopeLimitDegrees",
            static (c, _, _) => c.Movement!.LedgeSupportMinNormalY <= CosSlopeLimit(c.Movement!)),
        new(
            "R09",
            ConfigRelationScope.Once,
            "movement.maxSubMoves",
            "must be large enough that no sub-move of a 20 Hz step is longer than movement.capsuleRadius",
            static (c, _, _) => (MaxSpeed(c.Movement!) * 0.05f) <= ((float)c.Movement!.MaxSubMoves * c.Movement!.CapsuleRadius)),
        new(
            "R10",
            ConfigRelationScope.Once,
            "weapons",
            "must have exactly input.weaponSlots ({n}) entries",
            static (c, _, _) => c.Weapons!.Count == c.Input!.WeaponSlots,
            static (c, _, _) => c.Input!.WeaponSlots),
        new(
            "R11",
            ConfigRelationScope.EachWeapon,
            "weapons[{i}].roundsPerMinute",
            "must be at most weapons[{i}].maxShotsPerStep times 60 times stepRateHz ({n})",
            static (c, i, _) => c.Weapons![i]!.RoundsPerMinute <= ShotsPerMinuteLimit(c, i),
            static (c, i, _) => ShotsPerMinuteLimit(c, i)),
        new(
            "R12",
            ConfigRelationScope.EachWeapon,
            "weapons[{i}].heatRecoveryLevel",
            "must be less than weapons[{i}].overheatThreshold",
            static (c, i, _) => c.Weapons![i]!.HeatRecoveryLevel < c.Weapons![i]!.OverheatThreshold),
        new(
            "R13",
            ConfigRelationScope.EachWeapon,
            "weapons[{i}].heatCooldownPerSecond",
            "must be greater than 0 when weapons[{i}].heatPerShot is greater than 0",
            static (c, i, _) => c.Weapons![i]!.HeatPerShot == 0 || c.Weapons![i]!.HeatCooldownPerSecond > 0),
        new(
            "R14",
            ConfigRelationScope.EachWeapon,
            "weapons[{i}].projectileClass",
            "must be less than the number of projectileClasses ({n})",
            static (c, i, _) => c.Weapons![i]!.ProjectileClass < c.ProjectileClasses!.Count,
            static (c, _, _) => c.ProjectileClasses!.Count),
        new(
            "R15",
            ConfigRelationScope.EachCapsule,
            "hitboxes.{stance}[{c}].b.up",
            "must be at least hitboxes.{stance}[{c}].a.up",
            static (c, s, k) => Capsule(c, s, k).B!.Up >= Capsule(c, s, k).A!.Up),
        new(
            "R16",
            ConfigRelationScope.StandingCapsules,
            "hitboxes.{stance}[{c}].b.up",
            "must be at most movement.standHeight plus 0.5",
            static (c, s, k) => Capsule(c, s, k).B!.Up <= (c.Movement!.StandHeight + 0.5f)),
        new(
            "R17",
            ConfigRelationScope.CrouchedCapsules,
            "hitboxes.{stance}[{c}].b.up",
            "must be at most movement.crouchHeight plus 0.5",
            static (c, s, k) => Capsule(c, s, k).B!.Up <= (c.Movement!.CrouchHeight + 0.5f)),
        new(
            "R18",
            ConfigRelationScope.Once,
            "hitboxes.eyeHeightStanding",
            "must be less than movement.standHeight",
            static (c, _, _) => c.Hitboxes!.EyeHeightStanding < c.Movement!.StandHeight),
        new(
            "R19",
            ConfigRelationScope.Once,
            "hitboxes.eyeHeightCrouched",
            "must be less than movement.crouchHeight",
            static (c, _, _) => c.Hitboxes!.EyeHeightCrouched < c.Movement!.CrouchHeight),
    ];

    /// <summary>
    /// <c>SimMath.Cos((movement.slopeLimitDegrees * MathF.PI) / 180f)</c>, the same value
    /// <c>CompiledConfig</c> stores.
    /// </summary>
    public static float CosSlopeLimit(MovementConfig movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return SimMath.Cos((movement.SlopeLimitDegrees * System.MathF.PI) / 180f);
    }

    /// <summary>
    /// A bound on the character's speed,
    /// <c>(movement.runSpeed / CosSlopeLimit) + SimMath.Max(movement.jumpSpeed, movement.maxFallSpeed)</c>.
    /// </summary>
    public static float MaxSpeed(MovementConfig movement)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return (movement.RunSpeed / CosSlopeLimit(movement)) + SimMath.Max(movement.JumpSpeed, movement.MaxFallSpeed);
    }

    /// <summary>Appends every relation's errors, R01 to R19, each over its instances in ascending order.</summary>
    internal static void Check(SimConfig config, List<ConfigError> errors)
    {
        foreach (var relation in Rows)
        {
            relation.Check(config, errors);
        }
    }

    /// <summary>R11's limit, <c>maxShotsPerStep * 60 * stepRateHz</c>, in <see cref="int"/> (at most 8 · 60 · 60 after the field checks).</summary>
    private static int ShotsPerMinuteLimit(SimConfig config, int slot) => config.Weapons![slot]!.MaxShotsPerStep * 60 * config.StepRateHz;

    private static HitboxCapsuleConfig Capsule(SimConfig config, int stance, int capsule) =>
        (stance == 0 ? config.Hitboxes!.Standing : config.Hitboxes!.Crouched)![capsule]!;
}
