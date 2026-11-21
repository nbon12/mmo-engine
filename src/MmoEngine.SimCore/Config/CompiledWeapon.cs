using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Config;

/// <summary>
/// One weapon slot's compiled configuration (specs/015-shared-simulation-core/data-model.md#weapon-configuration-and-state,
/// contracts/config-rules.md#compile-time-conversions). No duration or rate here is a float, so fire
/// timing is exact (FR-007): durations are whole steps, heat is scaled by the step rate, and recoil is in
/// binary angle units (65,536 per turn). <see cref="CompiledConfig.Create"/> builds it; it is immutable.
/// </summary>
public sealed class CompiledWeapon
{
    internal CompiledWeapon(WeaponConfig weapon, int hz)
    {
        Rpm = weapon.RoundsPerMinute;
        MaxShotsPerStep = weapon.MaxShotsPerStep;
        FireMode = weapon.FireMode;
        MagazineSize = weapon.MagazineSize;
        ReserveRounds = weapon.ReserveRounds;
        ReloadSteps = CompiledConfig.CeilSteps(weapon.ReloadMs, hz);
        EquipSteps = CompiledConfig.CeilSteps(weapon.EquipMs, hz);
        HeatPerShotScaled = (uint)(weapon.HeatPerShot * hz);
        OverheatThresholdScaled = (uint)(weapon.OverheatThreshold * hz);
        HeatRecoveryScaled = (uint)(weapon.HeatRecoveryLevel * hz);
        HeatCooldownPerStep = (uint)weapon.HeatCooldownPerSecond;
        HeatCooldownDelaySteps = CompiledConfig.CeilSteps(weapon.HeatCooldownDelayMs, hz);
        SpreadHalfAngleDeg = weapon.SpreadHalfAngleDeg;
        var s = SimMath.Sin((weapon.SpreadHalfAngleDeg * System.MathF.PI) / 360f);
        VersMax = (2f * s) * s;
        RecoilKickPitchUnits = CompiledConfig.AngleUnits(weapon.RecoilKickPitchDeg);
        RecoilKickYawUnits = CompiledConfig.AngleUnits(weapon.RecoilKickYawDeg);
        RecoilMaxPitchUnits = CompiledConfig.AngleUnits(weapon.RecoilMaxPitchDeg);
        RecoilMaxYawUnits = CompiledConfig.AngleUnits(weapon.RecoilMaxYawDeg);
        var recovery = SimMath.RoundToInt(((weapon.RecoilRecoveryDegPerSecond * 65536f) / 360f) / (float)hz);
        RecoilRecoveryPerStep = weapon.RecoilRecoveryDegPerSecond > 0f && recovery == 0 ? 1 : recovery;
        RecoilRecoveryDelaySteps = CompiledConfig.CeilSteps(weapon.RecoilRecoveryDelayMs, hz);
        MuzzleSpeed = weapon.MuzzleSpeed;
        ProjectileClass = weapon.ProjectileClass;
    }

    /// <summary>Fire-rate units gained per step: <c>roundsPerMinute</c>. A shot costs <see cref="CompiledConfig.ShotCost"/>.</summary>
    public int Rpm { get; }

    /// <summary>The most shots one step may fire, the fire loop's bound.</summary>
    public int MaxShotsPerStep { get; }

    /// <summary>Auto or Semi.</summary>
    public FireMode FireMode { get; }

    /// <summary>The magazine's capacity, in rounds.</summary>
    public int MagazineSize { get; }

    /// <summary>A new weapon's reserve, in rounds.</summary>
    public int ReserveRounds { get; }

    /// <summary><c>ceilSteps(reloadMs)</c>.</summary>
    public int ReloadSteps { get; }

    /// <summary><c>ceilSteps(equipMs)</c>.</summary>
    public int EquipSteps { get; }

    /// <summary><c>heatPerShot * Hz</c>.</summary>
    public uint HeatPerShotScaled { get; }

    /// <summary><c>overheatThreshold * Hz</c>.</summary>
    public uint OverheatThresholdScaled { get; }

    /// <summary><c>heatRecoveryLevel * Hz</c>.</summary>
    public uint HeatRecoveryScaled { get; }

    /// <summary>
    /// <c>heatCooldownPerSecond</c>, unchanged: heat is stored × Hz, so one step's cooling of
    /// <c>heatCooldownPerSecond / Hz</c> is exactly <c>heatCooldownPerSecond</c> stored units.
    /// </summary>
    public uint HeatCooldownPerStep { get; }

    /// <summary><c>ceilSteps(heatCooldownDelayMs)</c>.</summary>
    public int HeatCooldownDelaySteps { get; }

    /// <summary>The spread cone's half angle in degrees, as configured.</summary>
    public float SpreadHalfAngleDeg { get; }

    /// <summary>
    /// The spread's versine bound, 2·sin²(θmax/2): <c>s = SimMath.Sin((spreadHalfAngleDeg * MathF.PI) / 360f)</c>,
    /// then <c>(2f * s) * s</c>.
    /// </summary>
    public float VersMax { get; }

    /// <summary><c>angleUnits(recoilKickPitchDeg)</c>.</summary>
    public int RecoilKickPitchUnits { get; }

    /// <summary><c>angleUnits(recoilKickYawDeg)</c>.</summary>
    public int RecoilKickYawUnits { get; }

    /// <summary><c>angleUnits(recoilMaxPitchDeg)</c>.</summary>
    public int RecoilMaxPitchUnits { get; }

    /// <summary><c>angleUnits(recoilMaxYawDeg)</c>.</summary>
    public int RecoilMaxYawUnits { get; }

    /// <summary>
    /// <c>SimMath.RoundToInt(((recoilRecoveryDegPerSecond * 65536f) / 360f) / (float)Hz)</c>, raised to 1
    /// when the rate is above 0 and that is 0.
    /// </summary>
    public int RecoilRecoveryPerStep { get; }

    /// <summary><c>ceilSteps(recoilRecoveryDelayMs)</c>.</summary>
    public int RecoilRecoveryDelaySteps { get; }

    /// <summary>The launch speed, in m/s.</summary>
    public float MuzzleSpeed { get; }

    /// <summary>The index of the projectile class it fires.</summary>
    public int ProjectileClass { get; }
}
