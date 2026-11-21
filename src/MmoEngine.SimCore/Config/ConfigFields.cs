using System;
using System.Collections.Immutable;
using System.Globalization;

namespace MmoEngine.SimCore.Config;

/// <summary>The kind of a configuration field (specs/015-shared-simulation-core/contracts/config-rules.md#field-kinds).</summary>
public enum ConfigFieldKind : byte
{
    /// <summary>An <see cref="int"/>; its value bits are the two's complement.</summary>
    Int = 0,

    /// <summary>A binary32 <see cref="float"/>; its value bits are the IEEE 754 bits.</summary>
    Float = 1,

    /// <summary>A <see cref="FireMode"/>; its value bits are the member's value.</summary>
    Enum = 2,
}

/// <summary>Which instances a template row is expanded over (contracts/config-rules.md#field-ids).</summary>
public enum ConfigFieldScope : byte
{
    /// <summary>One instance: the general, movement and input fields, and each eye height at its stance.</summary>
    Single = 0,

    /// <summary>One instance per weapon slot i: <c>0x2i..</c>.</summary>
    WeaponSlot = 1,

    /// <summary>One instance per projectile class k: <c>0x3k..</c>.</summary>
    ProjectileClass = 2,

    /// <summary>One instance per stance s (0 standing, 1 crouched) and capsule c (0 head, 1 torso, 2 legs): <c>0x4000 | s &lt;&lt; 8 | c &lt;&lt; 4 | j</c>.</summary>
    Capsule = 3,
}

/// <summary>
/// One template row of the rule table: one field definition of one section
/// (specs/015-shared-simulation-core/contracts/config-rules.md#rule-table). The row is expanded per present
/// instance by <see cref="ConfigFields.Expand"/>. Bounds are inclusive and held as value bits: an Int
/// bound's two's complement, a Float bound's IEEE 754 bits, and 0 and 1 for the Enum row.
/// </summary>
public sealed class ConfigField
{
    private readonly Func<SimConfig, int, int, uint> _get;
    private readonly Action<SimConfig, int, int, uint> _set;

    internal ConfigField(
        int section,
        int fieldNumber,
        int fixedInstance,
        ConfigFieldScope scope,
        string pathTemplate,
        ConfigFieldKind kind,
        string unit,
        uint minBits,
        uint maxBits,
        Func<SimConfig, int, int, uint> get,
        Action<SimConfig, int, int, uint> set)
    {
        Section = section;
        FieldNumber = fieldNumber;
        FixedInstance = fixedInstance;
        Scope = scope;
        PathTemplate = pathTemplate;
        Kind = kind;
        Unit = unit;
        MinBits = minBits;
        MaxBits = maxBits;
        _get = get;
        _set = set;
        RangeMessage = kind == ConfigFieldKind.Enum
            ? "must be Auto or Semi"
            : string.Concat("must be between ", FormatBound(kind, minBits), " and ", FormatBound(kind, maxBits));
    }

    /// <summary>The section: 0 general, 1 movement, 2 weapon, 3 projectile class, 4 hitbox, 5 input.</summary>
    public int Section { get; }

    /// <summary>
    /// The field number within the section: the low byte of the fieldId for every row except the capsule
    /// rows, whose number is j (1 to 7) and whose fieldId also carries the capsule in bits 4 to 7.
    /// </summary>
    public int FieldNumber { get; }

    /// <summary>The instances the row is expanded over.</summary>
    public ConfigFieldScope Scope { get; }

    /// <summary>
    /// The path with its instance placeholders, such as <c>weapons[{i}].roundsPerMinute</c>,
    /// <c>projectileClasses[{k}].maxRangeM</c> or <c>hitboxes.{stance}[{c}].a.fwd</c>.
    /// </summary>
    public string PathTemplate { get; }

    /// <summary>Int, Float or Enum.</summary>
    public ConfigFieldKind Kind { get; }

    /// <summary>The unit, as the contract's table names it.</summary>
    public string Unit { get; }

    /// <summary>The inclusive minimum as value bits.</summary>
    public uint MinBits { get; }

    /// <summary>The inclusive maximum as value bits.</summary>
    public uint MaxBits { get; }

    /// <summary>The instance of a <see cref="ConfigFieldScope.Single"/> row: the stance of an eye height, 0 otherwise.</summary>
    internal int FixedInstance { get; }

    /// <summary><c>must be between {min} and {max}</c> with invariant round-trip numbers, or <c>must be Auto or Semi</c>.</summary>
    internal string RangeMessage { get; }

    /// <summary>
    /// Reads the field of <paramref name="config"/> at the instance (the slot, the class, or the stance with
    /// <paramref name="capsule"/>) as value bits. The configuration must have the section and the instance:
    /// <see cref="ConfigGuard.Check"/> reads fields only after the shape check passed.
    /// </summary>
    public uint GetBits(SimConfig config, int instance, int capsule) => _get(config, instance, capsule);

    /// <summary>
    /// Writes <paramref name="bits"/> into the field of <paramref name="config"/> at the instance, losslessly:
    /// an Int as the two's complement, a Float as its bits, the Enum as <c>(FireMode)bits</c>.
    /// </summary>
    public void SetBits(SimConfig config, int instance, int capsule, uint bits) => _set(config, instance, capsule, bits);

    private static string FormatBound(ConfigFieldKind kind, uint bits) => kind == ConfigFieldKind.Int
        ? unchecked((int)bits).ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{BitConverter.UInt32BitsToSingle(bits):R}");
}

/// <summary>
/// One field of one present instance: its fieldId, its concrete path such as
/// <c>weapons[1].roundsPerMinute</c>, its template row (the same instance as in
/// <see cref="ConfigFields.Templates"/>) and its instance.
/// </summary>
public readonly struct ExpandedField
{
    internal ExpandedField(ushort fieldId, string path, ConfigField template, int instance, int capsule)
    {
        FieldId = fieldId;
        Path = path;
        Template = template;
        Instance = instance;
        Capsule = capsule;
    }

    /// <summary><c>section &lt;&lt; 12 | instance &lt;&lt; 8 | field</c>, or <c>0x4000 | s &lt;&lt; 8 | c &lt;&lt; 4 | j</c> for a capsule field.</summary>
    public ushort FieldId { get; }

    /// <summary>The camelCase JSON path with zero-based indices, the path every error names.</summary>
    public string Path { get; }

    /// <summary>The template row, by reference the same object as in <see cref="ConfigFields.Templates"/>.</summary>
    public ConfigField Template { get; }

    /// <summary>The slot, the class index or the stance; 0 in sections without instances.</summary>
    public int Instance { get; }

    /// <summary>The capsule index (0 head, 1 torso, 2 legs) of a capsule field; 0 otherwise.</summary>
    public int Capsule { get; }

    /// <summary>The field's value bits in <paramref name="config"/>.</summary>
    public uint GetBits(SimConfig config) => Template.GetBits(config, Instance, Capsule);

    /// <summary>Writes the field's value bits into <paramref name="config"/>.</summary>
    public void SetBits(SimConfig config, uint bits) => Template.SetBits(config, Instance, Capsule, bits);
}

/// <summary>
/// The rule table's fields (specs/015-shared-simulation-core/contracts/config-rules.md#rule-table): one
/// template row per field definition of the six sections, and <see cref="Expand"/>, the one expansion per
/// present instance that the guard, the config block and the tests use.
/// </summary>
public static class ConfigFields
{
    /// <summary>The largest number of weapon slots and of projectile classes.</summary>
    public const int MaxInstances = 8;

    /// <summary>The number of hitbox capsules per stance: head, torso and legs.</summary>
    public const int CapsulesPerStance = 3;

    /// <summary>The number of stances: standing and crouched.</summary>
    public const int StanceCount = 2;

    private const uint FireModeMinBits = (uint)FireMode.Auto;
    private const uint FireModeMaxBits = (uint)FireMode.Semi;

    /// <summary>
    /// The 57 template rows in ascending order of each row's first-instance fieldId: 3 general, 20
    /// movement, 21 weapon, 3 projectile class, 7 capsule (j 1 to 7), the 2 eye heights and 1 input.
    /// </summary>
    public static ImmutableArray<ConfigField> Templates { get; } =
    [
        // General (section 0).
        Int(0, 0x01, "stepRateHz", "Hz", 20, 60, static (c, _, _) => c.StepRateHz, static (c, _, _, v) => c.StepRateHz = v),
        Int(0, 0x02, "world.maxCharacters", "characters", 1, 1024, static (c, _, _) => c.World!.MaxCharacters, static (c, _, _, v) => c.World!.MaxCharacters = v),
        Int(0, 0x03, "world.maxProjectiles", "projectiles", 1, 16384, static (c, _, _) => c.World!.MaxProjectiles, static (c, _, _, v) => c.World!.MaxProjectiles = v),

        // Movement (section 1).
        MovementFloat(0x01, "walkSpeed", "m/s", 0.1f, 20f, static m => m.WalkSpeed, static (m, v) => m.WalkSpeed = v),
        MovementFloat(0x02, "runSpeed", "m/s", 0.1f, 20f, static m => m.RunSpeed, static (m, v) => m.RunSpeed = v),
        MovementFloat(0x03, "crouchSpeed", "m/s", 0.1f, 20f, static m => m.CrouchSpeed, static (m, v) => m.CrouchSpeed = v),
        MovementFloat(0x04, "groundAccel", "m/s²", 0.1f, 1000f, static m => m.GroundAccel, static (m, v) => m.GroundAccel = v),
        MovementFloat(0x05, "groundDecel", "m/s²", 0.1f, 1000f, static m => m.GroundDecel, static (m, v) => m.GroundDecel = v),
        MovementFloat(0x06, "airAccel", "m/s²", 0f, 1000f, static m => m.AirAccel, static (m, v) => m.AirAccel = v),
        MovementFloat(0x07, "gravity", "m/s²", 0.1f, 100f, static m => m.Gravity, static (m, v) => m.Gravity = v),
        MovementFloat(0x08, "jumpSpeed", "m/s", 0.1f, 20f, static m => m.JumpSpeed, static (m, v) => m.JumpSpeed = v),
        MovementFloat(0x09, "maxFallSpeed", "m/s", 1f, 100f, static m => m.MaxFallSpeed, static (m, v) => m.MaxFallSpeed = v),
        MovementFloat(0x0A, "capsuleRadius", "m", 0.05f, 1f, static m => m.CapsuleRadius, static (m, v) => m.CapsuleRadius = v),
        MovementFloat(0x0B, "standHeight", "m", 0.5f, 3f, static m => m.StandHeight, static (m, v) => m.StandHeight = v),
        MovementFloat(0x0C, "crouchHeight", "m", 0.5f, 3f, static m => m.CrouchHeight, static (m, v) => m.CrouchHeight = v),
        MovementFloat(0x0D, "stepHeight", "m", 0.01f, 1f, static m => m.StepHeight, static (m, v) => m.StepHeight = v),
        MovementFloat(0x0E, "snapDistance", "m", 0f, 2f, static m => m.SnapDistance, static (m, v) => m.SnapDistance = v),
        MovementFloat(0x0F, "slopeLimitDegrees", "°", 5f, 60f, static m => m.SlopeLimitDegrees, static (m, v) => m.SlopeLimitDegrees = v),
        MovementFloat(0x10, "ledgeSupportMinNormalY", "(cosine)", 0f, 1f, static m => m.LedgeSupportMinNormalY, static (m, v) => m.LedgeSupportMinNormalY = v),
        MovementFloat(0x11, "contactSkin", "m", 0.001f, 0.05f, static m => m.ContactSkin, static (m, v) => m.ContactSkin = v),
        MovementFloat(0x12, "contactTolerance", "m", 0.0001f, 0.025f, static m => m.ContactTolerance, static (m, v) => m.ContactTolerance = v),
        MovementInt(0x13, "maxSlideIterations", "iterations", 1, 8, static m => m.MaxSlideIterations, static (m, v) => m.MaxSlideIterations = v),
        MovementInt(0x14, "maxSubMoves", "sub-moves", 1, 16, static m => m.MaxSubMoves, static (m, v) => m.MaxSubMoves = v),

        // Weapons (section 2, instance i = slot).
        WeaponInt(0x01, "roundsPerMinute", "rounds/min", 1, 6000, static w => w.RoundsPerMinute, static (w, v) => w.RoundsPerMinute = v),
        WeaponInt(0x02, "maxShotsPerStep", "shots", 1, 8, static w => w.MaxShotsPerStep, static (w, v) => w.MaxShotsPerStep = v),
        new ConfigField(
            2,
            0x03,
            0,
            ConfigFieldScope.WeaponSlot,
            "weapons[{i}].fireMode",
            ConfigFieldKind.Enum,
            "Auto = 0, Semi = 1",
            FireModeMinBits,
            FireModeMaxBits,
            static (c, i, _) => (uint)Weapon(c, i).FireMode,
            static (c, i, _, bits) => Weapon(c, i).FireMode = (FireMode)bits),
        WeaponInt(0x04, "magazineSize", "rounds", 1, 1000, static w => w.MagazineSize, static (w, v) => w.MagazineSize = v),
        WeaponInt(0x05, "reserveRounds", "rounds", 0, 10000, static w => w.ReserveRounds, static (w, v) => w.ReserveRounds = v),
        WeaponInt(0x06, "reloadMs", "ms", 0, 60000, static w => w.ReloadMs, static (w, v) => w.ReloadMs = v),
        WeaponInt(0x07, "equipMs", "ms", 0, 10000, static w => w.EquipMs, static (w, v) => w.EquipMs = v),
        WeaponInt(0x08, "heatPerShot", "heat", 0, 1000000, static w => w.HeatPerShot, static (w, v) => w.HeatPerShot = v),
        WeaponInt(0x09, "overheatThreshold", "heat", 1, 10000000, static w => w.OverheatThreshold, static (w, v) => w.OverheatThreshold = v),
        WeaponInt(0x0A, "heatRecoveryLevel", "heat", 0, 10000000, static w => w.HeatRecoveryLevel, static (w, v) => w.HeatRecoveryLevel = v),
        WeaponInt(0x0B, "heatCooldownPerSecond", "heat/s", 0, 10000000, static w => w.HeatCooldownPerSecond, static (w, v) => w.HeatCooldownPerSecond = v),
        WeaponInt(0x0C, "heatCooldownDelayMs", "ms", 0, 10000, static w => w.HeatCooldownDelayMs, static (w, v) => w.HeatCooldownDelayMs = v),
        WeaponFloat(0x0D, "spreadHalfAngleDeg", "°", 0f, 45f, static w => w.SpreadHalfAngleDeg, static (w, v) => w.SpreadHalfAngleDeg = v),
        WeaponFloat(0x0E, "recoilKickPitchDeg", "°", 0f, 10f, static w => w.RecoilKickPitchDeg, static (w, v) => w.RecoilKickPitchDeg = v),
        WeaponFloat(0x0F, "recoilKickYawDeg", "°", 0f, 10f, static w => w.RecoilKickYawDeg, static (w, v) => w.RecoilKickYawDeg = v),
        WeaponFloat(0x10, "recoilMaxPitchDeg", "°", 0f, 45f, static w => w.RecoilMaxPitchDeg, static (w, v) => w.RecoilMaxPitchDeg = v),
        WeaponFloat(0x11, "recoilMaxYawDeg", "°", 0f, 45f, static w => w.RecoilMaxYawDeg, static (w, v) => w.RecoilMaxYawDeg = v),
        WeaponFloat(0x12, "recoilRecoveryDegPerSecond", "°/s", 0f, 360f, static w => w.RecoilRecoveryDegPerSecond, static (w, v) => w.RecoilRecoveryDegPerSecond = v),
        WeaponInt(0x13, "recoilRecoveryDelayMs", "ms", 0, 10000, static w => w.RecoilRecoveryDelayMs, static (w, v) => w.RecoilRecoveryDelayMs = v),
        WeaponFloat(0x14, "muzzleSpeed", "m/s", 1f, 2000f, static w => w.MuzzleSpeed, static (w, v) => w.MuzzleSpeed = v),
        WeaponInt(0x15, "projectileClass", "class index", 0, 7, static w => w.ProjectileClass, static (w, v) => w.ProjectileClass = v),

        // Projectile classes (section 3, instance k = class index).
        ClassFloat(0x01, "gravityFactor", "× gravity", 0f, 10f, static p => p.GravityFactor, static (p, v) => p.GravityFactor = v),
        ClassFloat(0x02, "maxRangeM", "m", 1f, 10000f, static p => p.MaxRangeM, static (p, v) => p.MaxRangeM = v),
        ClassInt(0x03, "lifetimeMs", "ms", 1, 60000, static p => p.LifetimeMs, static (p, v) => p.LifetimeMs = v),

        // Hitboxes (section 4): the seven capsule fields j over every stance s and capsule c.
        CapsuleFloat(1, "a.fwd", -1f, 1f, static k => k.A!.Fwd, static (k, v) => k.A!.Fwd = v),
        CapsuleFloat(2, "a.right", -1f, 1f, static k => k.A!.Right, static (k, v) => k.A!.Right = v),
        CapsuleFloat(3, "a.up", 0f, 3.5f, static k => k.A!.Up, static (k, v) => k.A!.Up = v),
        CapsuleFloat(4, "b.fwd", -1f, 1f, static k => k.B!.Fwd, static (k, v) => k.B!.Fwd = v),
        CapsuleFloat(5, "b.right", -1f, 1f, static k => k.B!.Right, static (k, v) => k.B!.Right = v),
        CapsuleFloat(6, "b.up", 0f, 3.5f, static k => k.B!.Up, static (k, v) => k.B!.Up = v),
        CapsuleFloat(7, "radius", 0.01f, 1f, static k => k.Radius, static (k, v) => k.Radius = v),

        // Hitboxes (section 4): the eye heights, each at its stance.
        EyeHeight(0, "hitboxes.eyeHeightStanding", static h => h.EyeHeightStanding, static (h, v) => h.EyeHeightStanding = v),
        EyeHeight(1, "hitboxes.eyeHeightCrouched", static h => h.EyeHeightCrouched, static (h, v) => h.EyeHeightCrouched = v),

        // Input (section 5).
        Int(5, 0x01, "input.weaponSlots", "slots", 1, 8, static (c, _, _) => c.Input!.WeaponSlots, static (c, _, _, v) => c.Input!.WeaponSlots = v),
    ];

    /// <summary><c>section &lt;&lt; 12 | instance &lt;&lt; 8 | field</c>.</summary>
    public static ushort FieldId(int section, int instance, int field) => (ushort)((section << 12) | (instance << 8) | field);

    /// <summary>A capsule field's id, <c>0x4000 | s &lt;&lt; 8 | c &lt;&lt; 4 | j</c>.</summary>
    public static ushort CapsuleFieldId(int stance, int capsule, int j) => (ushort)(0x4000 | (stance << 8) | (capsule << 4) | j);

    /// <summary>The name a stance has in paths: <c>standing</c> for 0, <c>crouched</c> for 1.</summary>
    public static string StanceName(int stance) => stance == 0 ? "standing" : "crouched";

    /// <summary>
    /// Every field of every present instance, in ascending fieldId: the general, movement and input rows
    /// once each; the weapon rows for slots 0 to <paramref name="weaponCount"/> − 1; the projectile-class
    /// rows for classes 0 to <paramref name="projectileClassCount"/> − 1; the seven capsule rows over both
    /// stances and capsules 0 to 2; and each eye height once, at <c>0x4031</c> and <c>0x4131</c>.
    /// <c>Expand(2, 2)</c> gives the 116 fields of <c>default.json</c>, and <c>Expand(8, 8)</c> gives 260.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is below 0 or above 8.</exception>
    public static ImmutableArray<ExpandedField> Expand(int weaponCount, int projectileClassCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weaponCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weaponCount, MaxInstances);
        ArgumentOutOfRangeException.ThrowIfNegative(projectileClassCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(projectileClassCount, MaxInstances);

        var builder = ImmutableArray.CreateBuilder<ExpandedField>();
        foreach (var template in Templates)
        {
            switch (template.Scope)
            {
                case ConfigFieldScope.Single:
                    builder.Add(new ExpandedField(
                        FieldId(template.Section, template.FixedInstance, template.FieldNumber),
                        template.PathTemplate,
                        template,
                        template.FixedInstance,
                        0));
                    break;

                case ConfigFieldScope.WeaponSlot:
                    for (var i = 0; i < weaponCount; i++)
                    {
                        var path = template.PathTemplate.Replace("{i}", Index(i), StringComparison.Ordinal);
                        builder.Add(new ExpandedField(FieldId(template.Section, i, template.FieldNumber), path, template, i, 0));
                    }

                    break;

                case ConfigFieldScope.ProjectileClass:
                    for (var k = 0; k < projectileClassCount; k++)
                    {
                        var path = template.PathTemplate.Replace("{k}", Index(k), StringComparison.Ordinal);
                        builder.Add(new ExpandedField(FieldId(template.Section, k, template.FieldNumber), path, template, k, 0));
                    }

                    break;

                case ConfigFieldScope.Capsule:
                    for (var s = 0; s < StanceCount; s++)
                    {
                        for (var c = 0; c < CapsulesPerStance; c++)
                        {
                            var path = template.PathTemplate
                                .Replace("{stance}", StanceName(s), StringComparison.Ordinal)
                                .Replace("{c}", Index(c), StringComparison.Ordinal);
                            builder.Add(new ExpandedField(CapsuleFieldId(s, c, template.FieldNumber), path, template, s, c));
                        }
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Template {template.PathTemplate} has an unknown scope {template.Scope}.");
            }
        }

        builder.Sort(static (a, b) => a.FieldId.CompareTo(b.FieldId));
        return builder.ToImmutable();
    }

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static WeaponConfig Weapon(SimConfig config, int slot) => config.Weapons![slot]!;

    private static ProjectileClassConfig Class(SimConfig config, int index) => config.ProjectileClasses![index]!;

    private static HitboxCapsuleConfig Capsule(SimConfig config, int stance, int capsule) =>
        (stance == 0 ? config.Hitboxes!.Standing : config.Hitboxes!.Crouched)![capsule]!;

    private static uint Bits(int value) => unchecked((uint)value);

    private static int IntFromBits(uint bits) => unchecked((int)bits);

    private static ConfigField Int(int section, int field, string path, string unit, int min, int max, Func<SimConfig, int, int, int> get, Action<SimConfig, int, int, int> set) =>
        IntRow(section, field, ConfigFieldScope.Single, path, unit, min, max, get, set);

    private static ConfigField IntRow(int section, int field, ConfigFieldScope scope, string path, string unit, int min, int max, Func<SimConfig, int, int, int> get, Action<SimConfig, int, int, int> set) =>
        new(
            section,
            field,
            0,
            scope,
            path,
            ConfigFieldKind.Int,
            unit,
            Bits(min),
            Bits(max),
            (c, i, j) => Bits(get(c, i, j)),
            (c, i, j, bits) => set(c, i, j, IntFromBits(bits)));

    private static ConfigField FloatRow(int section, int field, int fixedInstance, ConfigFieldScope scope, string path, string unit, float min, float max, Func<SimConfig, int, int, float> get, Action<SimConfig, int, int, float> set) =>
        new(
            section,
            field,
            fixedInstance,
            scope,
            path,
            ConfigFieldKind.Float,
            unit,
            BitConverter.SingleToUInt32Bits(min),
            BitConverter.SingleToUInt32Bits(max),
            (c, i, j) => BitConverter.SingleToUInt32Bits(get(c, i, j)),
            (c, i, j, bits) => set(c, i, j, BitConverter.UInt32BitsToSingle(bits)));

    private static ConfigField MovementFloat(int field, string name, string unit, float min, float max, Func<MovementConfig, float> get, Action<MovementConfig, float> set) =>
        FloatRow(1, field, 0, ConfigFieldScope.Single, "movement." + name, unit, min, max, (c, _, _) => get(c.Movement!), (c, _, _, v) => set(c.Movement!, v));

    private static ConfigField MovementInt(int field, string name, string unit, int min, int max, Func<MovementConfig, int> get, Action<MovementConfig, int> set) =>
        IntRow(1, field, ConfigFieldScope.Single, "movement." + name, unit, min, max, (c, _, _) => get(c.Movement!), (c, _, _, v) => set(c.Movement!, v));

    private static ConfigField WeaponInt(int field, string name, string unit, int min, int max, Func<WeaponConfig, int> get, Action<WeaponConfig, int> set) =>
        IntRow(2, field, ConfigFieldScope.WeaponSlot, "weapons[{i}]." + name, unit, min, max, (c, i, _) => get(Weapon(c, i)), (c, i, _, v) => set(Weapon(c, i), v));

    private static ConfigField WeaponFloat(int field, string name, string unit, float min, float max, Func<WeaponConfig, float> get, Action<WeaponConfig, float> set) =>
        FloatRow(2, field, 0, ConfigFieldScope.WeaponSlot, "weapons[{i}]." + name, unit, min, max, (c, i, _) => get(Weapon(c, i)), (c, i, _, v) => set(Weapon(c, i), v));

    private static ConfigField ClassInt(int field, string name, string unit, int min, int max, Func<ProjectileClassConfig, int> get, Action<ProjectileClassConfig, int> set) =>
        IntRow(3, field, ConfigFieldScope.ProjectileClass, "projectileClasses[{k}]." + name, unit, min, max, (c, k, _) => get(Class(c, k)), (c, k, _, v) => set(Class(c, k), v));

    private static ConfigField ClassFloat(int field, string name, string unit, float min, float max, Func<ProjectileClassConfig, float> get, Action<ProjectileClassConfig, float> set) =>
        FloatRow(3, field, 0, ConfigFieldScope.ProjectileClass, "projectileClasses[{k}]." + name, unit, min, max, (c, k, _) => get(Class(c, k)), (c, k, _, v) => set(Class(c, k), v));

    private static ConfigField CapsuleFloat(int j, string name, float min, float max, Func<HitboxCapsuleConfig, float> get, Action<HitboxCapsuleConfig, float> set) =>
        FloatRow(4, j, 0, ConfigFieldScope.Capsule, "hitboxes.{stance}[{c}]." + name, "m", min, max, (c, s, k) => get(Capsule(c, s, k)), (c, s, k, v) => set(Capsule(c, s, k), v));

    private static ConfigField EyeHeight(int stance, string path, Func<HitboxesConfig, float> get, Action<HitboxesConfig, float> set) =>
        FloatRow(4, 0x31, stance, ConfigFieldScope.Single, path, "m", 0.1f, 3f, (c, _, _) => get(c.Hitboxes!), (c, _, _, v) => set(c.Hitboxes!, v));
}
