using System;
using System.Collections.Generic;

namespace MmoEngine.SimCore.Config;

/// <summary>
/// The raw, unvalidated simulation configuration, as <c>SimConfigJson</c> binds it from JSON or the
/// config block decoder reads it from a recorded stream
/// (specs/015-shared-simulation-core/data-model.md#configuration, contracts/config-rules.md#shape).
/// </summary>
/// <remarks>
/// Every leaf property is one row of the rule table in <see cref="ConfigFields"/>: an <see cref="int"/>
/// for an Int row, a <see cref="float"/> for a Float row and <see cref="Config.FireMode"/> for the Enum
/// row. Sections, list elements and capsule points are nullable, so a missing one is a shape error that
/// <see cref="ConfigGuard.Check"/> reports instead of a crash. The classes have no other public instance
/// property, because the rule-table tests walk them by reflection. Nothing here is validated:
/// <see cref="ConfigGuard.Check"/> does that, and <c>CompiledConfig.Create</c> refuses an invalid
/// configuration.
/// </remarks>
public sealed class SimConfig
{
    /// <summary><c>stepRateHz</c> (<c>0x0001</c>): the step rate, 20 to 60 Hz.</summary>
    public int StepRateHz { get; set; }

    /// <summary><c>world</c>: the world's maxima.</summary>
    public WorldConfig? World { get; set; }

    /// <summary><c>movement</c>: the character controller (section 1).</summary>
    public MovementConfig? Movement { get; set; }

    /// <summary><c>weapons[i]</c>: one weapon per slot (section 2), 1 to 8 entries.</summary>
    public List<WeaponConfig?>? Weapons { get; set; }

    /// <summary><c>projectileClasses[k]</c>: one ballistic class per index (section 3), 1 to 8 entries.</summary>
    public List<ProjectileClassConfig?>? ProjectileClasses { get; set; }

    /// <summary><c>hitboxes</c>: the capsules and eye heights per stance (section 4).</summary>
    public HitboxesConfig? Hitboxes { get; set; }

    /// <summary><c>input</c>: the input limits (section 5).</summary>
    public InputConfig? Input { get; set; }

    /// <summary>A deep copy: no section, list or element is shared with this configuration. Nulls stay null.</summary>
    public SimConfig Clone() => new()
    {
        StepRateHz = StepRateHz,
        World = World?.Clone(),
        Movement = Movement?.Clone(),
        Weapons = CloneList(Weapons, static w => w.Clone()),
        ProjectileClasses = CloneList(ProjectileClasses, static p => p.Clone()),
        Hitboxes = Hitboxes?.Clone(),
        Input = Input?.Clone(),
    };

    /// <summary>A new list holding a copy of each element, in order; a null list or element stays null.</summary>
    internal static List<T?>? CloneList<T>(List<T?>? source, Func<T, T> clone)
        where T : class
    {
        if (source is null)
        {
            return null;
        }

        var copy = new List<T?>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var element = source[i];
            copy.Add(element is null ? null : clone(element));
        }

        return copy;
    }
}

/// <summary>The <c>world</c> section (section 0, with <c>stepRateHz</c>).</summary>
public sealed class WorldConfig
{
    /// <summary><c>world.maxCharacters</c> (<c>0x0002</c>).</summary>
    public int MaxCharacters { get; set; }

    /// <summary><c>world.maxProjectiles</c> (<c>0x0003</c>).</summary>
    public int MaxProjectiles { get; set; }

    /// <summary>A copy of this section.</summary>
    public WorldConfig Clone() => (WorldConfig)MemberwiseClone();
}

/// <summary>The <c>movement</c> section (section 1): the character controller.</summary>
public sealed class MovementConfig
{
    /// <summary><c>movement.walkSpeed</c> (<c>0x1001</c>), m/s.</summary>
    public float WalkSpeed { get; set; }

    /// <summary><c>movement.runSpeed</c> (<c>0x1002</c>), m/s.</summary>
    public float RunSpeed { get; set; }

    /// <summary><c>movement.crouchSpeed</c> (<c>0x1003</c>), m/s.</summary>
    public float CrouchSpeed { get; set; }

    /// <summary><c>movement.groundAccel</c> (<c>0x1004</c>), m/s².</summary>
    public float GroundAccel { get; set; }

    /// <summary><c>movement.groundDecel</c> (<c>0x1005</c>), m/s².</summary>
    public float GroundDecel { get; set; }

    /// <summary><c>movement.airAccel</c> (<c>0x1006</c>), m/s².</summary>
    public float AirAccel { get; set; }

    /// <summary><c>movement.gravity</c> (<c>0x1007</c>), m/s²; projectiles fall under it too, scaled by their class's gravity factor.</summary>
    public float Gravity { get; set; }

    /// <summary><c>movement.jumpSpeed</c> (<c>0x1008</c>), m/s.</summary>
    public float JumpSpeed { get; set; }

    /// <summary><c>movement.maxFallSpeed</c> (<c>0x1009</c>), m/s.</summary>
    public float MaxFallSpeed { get; set; }

    /// <summary><c>movement.capsuleRadius</c> (<c>0x100A</c>), m.</summary>
    public float CapsuleRadius { get; set; }

    /// <summary><c>movement.standHeight</c> (<c>0x100B</c>), m.</summary>
    public float StandHeight { get; set; }

    /// <summary><c>movement.crouchHeight</c> (<c>0x100C</c>), m.</summary>
    public float CrouchHeight { get; set; }

    /// <summary><c>movement.stepHeight</c> (<c>0x100D</c>), m.</summary>
    public float StepHeight { get; set; }

    /// <summary><c>movement.snapDistance</c> (<c>0x100E</c>), m.</summary>
    public float SnapDistance { get; set; }

    /// <summary><c>movement.slopeLimitDegrees</c> (<c>0x100F</c>), degrees.</summary>
    public float SlopeLimitDegrees { get; set; }

    /// <summary><c>movement.ledgeSupportMinNormalY</c> (<c>0x1010</c>), a cosine.</summary>
    public float LedgeSupportMinNormalY { get; set; }

    /// <summary><c>movement.contactSkin</c> (<c>0x1011</c>), m.</summary>
    public float ContactSkin { get; set; }

    /// <summary><c>movement.contactTolerance</c> (<c>0x1012</c>), m.</summary>
    public float ContactTolerance { get; set; }

    /// <summary><c>movement.maxSlideIterations</c> (<c>0x1013</c>).</summary>
    public int MaxSlideIterations { get; set; }

    /// <summary><c>movement.maxSubMoves</c> (<c>0x1014</c>).</summary>
    public int MaxSubMoves { get; set; }

    /// <summary>A copy of this section.</summary>
    public MovementConfig Clone() => (MovementConfig)MemberwiseClone();
}

/// <summary>One <c>weapons[i]</c> entry (section 2, instance i = slot).</summary>
public sealed class WeaponConfig
{
    /// <summary><c>weapons[i].roundsPerMinute</c> (<c>0x2i01</c>).</summary>
    public int RoundsPerMinute { get; set; }

    /// <summary><c>weapons[i].maxShotsPerStep</c> (<c>0x2i02</c>).</summary>
    public int MaxShotsPerStep { get; set; }

    /// <summary><c>weapons[i].fireMode</c> (<c>0x2i03</c>), Auto or Semi.</summary>
    public FireMode FireMode { get; set; }

    /// <summary><c>weapons[i].magazineSize</c> (<c>0x2i04</c>), rounds.</summary>
    public int MagazineSize { get; set; }

    /// <summary><c>weapons[i].reserveRounds</c> (<c>0x2i05</c>), rounds.</summary>
    public int ReserveRounds { get; set; }

    /// <summary><c>weapons[i].reloadMs</c> (<c>0x2i06</c>), ms.</summary>
    public int ReloadMs { get; set; }

    /// <summary><c>weapons[i].equipMs</c> (<c>0x2i07</c>), ms.</summary>
    public int EquipMs { get; set; }

    /// <summary><c>weapons[i].heatPerShot</c> (<c>0x2i08</c>), heat.</summary>
    public int HeatPerShot { get; set; }

    /// <summary><c>weapons[i].overheatThreshold</c> (<c>0x2i09</c>), heat.</summary>
    public int OverheatThreshold { get; set; }

    /// <summary><c>weapons[i].heatRecoveryLevel</c> (<c>0x2i0A</c>), heat.</summary>
    public int HeatRecoveryLevel { get; set; }

    /// <summary><c>weapons[i].heatCooldownPerSecond</c> (<c>0x2i0B</c>), heat/s.</summary>
    public int HeatCooldownPerSecond { get; set; }

    /// <summary><c>weapons[i].heatCooldownDelayMs</c> (<c>0x2i0C</c>), ms.</summary>
    public int HeatCooldownDelayMs { get; set; }

    /// <summary><c>weapons[i].spreadHalfAngleDeg</c> (<c>0x2i0D</c>), degrees.</summary>
    public float SpreadHalfAngleDeg { get; set; }

    /// <summary><c>weapons[i].recoilKickPitchDeg</c> (<c>0x2i0E</c>), degrees.</summary>
    public float RecoilKickPitchDeg { get; set; }

    /// <summary><c>weapons[i].recoilKickYawDeg</c> (<c>0x2i0F</c>), degrees.</summary>
    public float RecoilKickYawDeg { get; set; }

    /// <summary><c>weapons[i].recoilMaxPitchDeg</c> (<c>0x2i10</c>), degrees.</summary>
    public float RecoilMaxPitchDeg { get; set; }

    /// <summary><c>weapons[i].recoilMaxYawDeg</c> (<c>0x2i11</c>), degrees.</summary>
    public float RecoilMaxYawDeg { get; set; }

    /// <summary><c>weapons[i].recoilRecoveryDegPerSecond</c> (<c>0x2i12</c>), degrees/s.</summary>
    public float RecoilRecoveryDegPerSecond { get; set; }

    /// <summary><c>weapons[i].recoilRecoveryDelayMs</c> (<c>0x2i13</c>), ms.</summary>
    public int RecoilRecoveryDelayMs { get; set; }

    /// <summary><c>weapons[i].muzzleSpeed</c> (<c>0x2i14</c>), m/s.</summary>
    public float MuzzleSpeed { get; set; }

    /// <summary><c>weapons[i].projectileClass</c> (<c>0x2i15</c>), an index into <c>projectileClasses</c>.</summary>
    public int ProjectileClass { get; set; }

    /// <summary>A copy of this entry.</summary>
    public WeaponConfig Clone() => (WeaponConfig)MemberwiseClone();
}

/// <summary>One <c>projectileClasses[k]</c> entry (section 3, instance k = class index).</summary>
public sealed class ProjectileClassConfig
{
    /// <summary><c>projectileClasses[k].gravityFactor</c> (<c>0x3k01</c>), × gravity.</summary>
    public float GravityFactor { get; set; }

    /// <summary><c>projectileClasses[k].maxRangeM</c> (<c>0x3k02</c>), m.</summary>
    public float MaxRangeM { get; set; }

    /// <summary><c>projectileClasses[k].lifetimeMs</c> (<c>0x3k03</c>), ms.</summary>
    public int LifetimeMs { get; set; }

    /// <summary>A copy of this entry.</summary>
    public ProjectileClassConfig Clone() => (ProjectileClassConfig)MemberwiseClone();
}

/// <summary>
/// The <c>hitboxes</c> section (section 4, instance s = stance: 0 standing, 1 crouched): three capsules per
/// stance (0 head, 1 torso, 2 legs) and the eye heights.
/// </summary>
public sealed class HitboxesConfig
{
    /// <summary><c>hitboxes.standing[c]</c>: exactly 3 capsules.</summary>
    public List<HitboxCapsuleConfig?>? Standing { get; set; }

    /// <summary><c>hitboxes.crouched[c]</c>: exactly 3 capsules.</summary>
    public List<HitboxCapsuleConfig?>? Crouched { get; set; }

    /// <summary><c>hitboxes.eyeHeightStanding</c> (<c>0x4031</c>), m above the feet.</summary>
    public float EyeHeightStanding { get; set; }

    /// <summary><c>hitboxes.eyeHeightCrouched</c> (<c>0x4131</c>), m above the feet.</summary>
    public float EyeHeightCrouched { get; set; }

    /// <summary>A deep copy of this section.</summary>
    public HitboxesConfig Clone() => new()
    {
        Standing = SimConfig.CloneList(Standing, static c => c.Clone()),
        Crouched = SimConfig.CloneList(Crouched, static c => c.Clone()),
        EyeHeightStanding = EyeHeightStanding,
        EyeHeightCrouched = EyeHeightCrouched,
    };
}

/// <summary>
/// One hitbox capsule, <c>hitboxes.{stance}[c]</c>: the segment from <see cref="A"/> to <see cref="B"/>,
/// relative to the feet, and its radius.
/// </summary>
public sealed class HitboxCapsuleConfig
{
    /// <summary><c>hitboxes.{stance}[c].a</c>: the lower endpoint (j 1 to 3).</summary>
    public HitboxPointConfig? A { get; set; }

    /// <summary><c>hitboxes.{stance}[c].b</c>: the upper endpoint (j 4 to 6).</summary>
    public HitboxPointConfig? B { get; set; }

    /// <summary><c>hitboxes.{stance}[c].radius</c> (j 7), m.</summary>
    public float Radius { get; set; }

    /// <summary>A deep copy of this capsule.</summary>
    public HitboxCapsuleConfig Clone() => new()
    {
        A = A?.Clone(),
        B = B?.Clone(),
        Radius = Radius,
    };
}

/// <summary>
/// A capsule endpoint in metres relative to the feet: <see cref="Fwd"/> along the aim heading,
/// <see cref="Right"/> to the character's right and <see cref="Up"/> along +Y.
/// </summary>
public sealed class HitboxPointConfig
{
    /// <summary><c>fwd</c>, m along the aim heading.</summary>
    public float Fwd { get; set; }

    /// <summary><c>right</c>, m to the character's right.</summary>
    public float Right { get; set; }

    /// <summary><c>up</c>, m above the feet.</summary>
    public float Up { get; set; }

    /// <summary>A copy of this point.</summary>
    public HitboxPointConfig Clone() => (HitboxPointConfig)MemberwiseClone();
}

/// <summary>The <c>input</c> section (section 5).</summary>
public sealed class InputConfig
{
    /// <summary><c>input.weaponSlots</c> (<c>0x5001</c>): the number of weapon slots, and of <c>weapons</c> entries.</summary>
    public int WeaponSlots { get; set; }

    /// <summary>A copy of this section.</summary>
    public InputConfig Clone() => (InputConfig)MemberwiseClone();
}
