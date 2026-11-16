using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Math;

namespace MmoEngine.SimCore.Config;

/// <summary>
/// The validated, compiled configuration every step reads
/// (specs/015-shared-simulation-core/data-model.md#compiledconfig,
/// contracts/config-rules.md#compile-time-conversions). <see cref="Create"/> runs the guard and refuses an
/// invalid configuration, so no step can run on an unvalidated value. Every conversion is done once, in
/// <see cref="float"/> with the contract's parenthesisation or in <see cref="int"/>, from the
/// configuration's bits, so every build gets the same compiled bits. It is immutable and may be shared.
/// </summary>
public sealed class CompiledConfig
{
    /// <summary><see cref="AxisTop"/> and <see cref="HitboxLayouts"/> index 0: standing.</summary>
    public const int Standing = 0;

    /// <summary><see cref="AxisTop"/> and <see cref="HitboxLayouts"/> index 1: crouched.</summary>
    public const int Crouched = 1;

    private readonly SimConfig _config;

    private CompiledConfig(SimConfig config)
    {
        _config = config;
        var hz = config.StepRateHz;
        var world = config.World!;
        var movement = config.Movement!;
        var hitboxes = config.Hitboxes!;

        StepRateHz = hz;
        MaxCharacters = world.MaxCharacters;
        MaxProjectiles = world.MaxProjectiles;
        WeaponSlots = config.Input!.WeaponSlots;

        Dt = 1f / (float)hz;
        WalkSpeed = movement.WalkSpeed;
        RunSpeed = movement.RunSpeed;
        CrouchSpeed = movement.CrouchSpeed;
        GroundAccelPerStep = movement.GroundAccel * Dt;
        GroundDecelPerStep = movement.GroundDecel * Dt;
        AirAccelPerStep = movement.AirAccel * Dt;
        Gravity = movement.Gravity;
        GravityPerStep = movement.Gravity * Dt;
        JumpSpeed = movement.JumpSpeed;
        MaxFallSpeed = movement.MaxFallSpeed;
        CapsuleRadius = movement.CapsuleRadius;
        StandHeight = movement.StandHeight;
        CrouchHeight = movement.CrouchHeight;
        StepHeight = movement.StepHeight;
        SnapDistance = movement.SnapDistance;
        CosSlopeLimit = ConfigRelations.CosSlopeLimit(movement);
        LedgeSupportMinNormalY = movement.LedgeSupportMinNormalY;
        ContactSkin = movement.ContactSkin;
        ContactTolerance = movement.ContactTolerance;
        MaxSlideIterations = movement.MaxSlideIterations;
        MaxSubMoves = movement.MaxSubMoves;
        AxisTop = [movement.StandHeight - movement.CapsuleRadius, movement.CrouchHeight - movement.CapsuleRadius];
        SweepTarget = movement.CapsuleRadius + movement.ContactSkin;
        SweepTolerance = 0.1f * movement.ContactSkin;
        PushOutThreshold = movement.CapsuleRadius - (0.5f * SweepTolerance);
        HeadroomClearance = movement.CapsuleRadius + (0.5f * movement.ContactSkin);

        ShotCost = 60 * hz;
        var weapons = ImmutableArray.CreateBuilder<CompiledWeapon>(config.Weapons!.Count);
        var maxShots = 0;
        foreach (var weapon in config.Weapons)
        {
            var compiled = new CompiledWeapon(weapon!, hz);
            weapons.Add(compiled);
            maxShots = System.Math.Max(maxShots, compiled.MaxShotsPerStep);
        }

        Weapons = weapons.MoveToImmutable();
        MaxShotsPerStepMax = maxShots;

        var classCount = config.ProjectileClasses!.Count;
        var halfG = ImmutableArray.CreateBuilder<float>(classCount);
        var maxRangeSq = ImmutableArray.CreateBuilder<float>(classCount);
        var lifetimeSteps = ImmutableArray.CreateBuilder<int>(classCount);
        foreach (var projectileClass in config.ProjectileClasses)
        {
            halfG.Add((-0.5f * movement.Gravity) * projectileClass!.GravityFactor);
            maxRangeSq.Add(projectileClass.MaxRangeM * projectileClass.MaxRangeM);
            lifetimeSteps.Add(CeilSteps(projectileClass.LifetimeMs, hz));
        }

        HalfG = halfG.MoveToImmutable();
        MaxRangeSq = maxRangeSq.MoveToImmutable();
        LifetimeSteps = lifetimeSteps.MoveToImmutable();

        HitboxLayouts =
        [
            Layout(hitboxes.Standing!, hitboxes.EyeHeightStanding),
            Layout(hitboxes.Crouched!, hitboxes.EyeHeightCrouched),
        ];

        MaxStateLength = StateSerializer.MaxLength(MaxCharacters, MaxProjectiles, WeaponSlots, MaxShotsPerStepMax);
        MaxStepRecordLength = MaxStateLength + StepRecord.OverheadLength;
    }

    // ---------------------------------------------------------------- general

    /// <summary><c>stepRateHz</c>, 20 to 60.</summary>
    public int StepRateHz { get; }

    /// <summary><c>world.maxCharacters</c>.</summary>
    public int MaxCharacters { get; }

    /// <summary><c>world.maxProjectiles</c>.</summary>
    public int MaxProjectiles { get; }

    /// <summary><c>input.weaponSlots</c>, also the number of <see cref="Weapons"/>.</summary>
    public int WeaponSlots { get; }

    /// <summary>The number of projectile classes.</summary>
    public int ProjectileClassCount => HalfG.Length;

    /// <summary>
    /// A deep copy of the validated configuration, so a recorder can write a stream's config block. Each
    /// read returns a new copy, so no caller can change this one.
    /// </summary>
    public SimConfig Config => _config.Clone();

    // ---------------------------------------------------------------- movement

    /// <summary>The step length, <c>1f / (float)Hz</c>, in seconds.</summary>
    public float Dt { get; }

    /// <summary><c>movement.walkSpeed</c>, m/s.</summary>
    public float WalkSpeed { get; }

    /// <summary><c>movement.runSpeed</c>, m/s.</summary>
    public float RunSpeed { get; }

    /// <summary><c>movement.crouchSpeed</c>, m/s.</summary>
    public float CrouchSpeed { get; }

    /// <summary><c>groundAccel * Dt</c>, m/s per step.</summary>
    public float GroundAccelPerStep { get; }

    /// <summary><c>groundDecel * Dt</c>, m/s per step.</summary>
    public float GroundDecelPerStep { get; }

    /// <summary><c>airAccel * Dt</c>, m/s per step.</summary>
    public float AirAccelPerStep { get; }

    /// <summary><c>movement.gravity</c>, m/s².</summary>
    public float Gravity { get; }

    /// <summary><c>gravity * Dt</c>, m/s per step.</summary>
    public float GravityPerStep { get; }

    /// <summary><c>movement.jumpSpeed</c>, m/s.</summary>
    public float JumpSpeed { get; }

    /// <summary><c>movement.maxFallSpeed</c>, m/s.</summary>
    public float MaxFallSpeed { get; }

    /// <summary><c>movement.capsuleRadius</c>, m.</summary>
    public float CapsuleRadius { get; }

    /// <summary><c>movement.standHeight</c>, m.</summary>
    public float StandHeight { get; }

    /// <summary><c>movement.crouchHeight</c>, m.</summary>
    public float CrouchHeight { get; }

    /// <summary><c>movement.stepHeight</c>, m.</summary>
    public float StepHeight { get; }

    /// <summary><c>movement.snapDistance</c>, m.</summary>
    public float SnapDistance { get; }

    /// <summary><c>SimMath.Cos((slopeLimitDegrees * MathF.PI) / 180f)</c>, the same value the guard's relations use.</summary>
    public float CosSlopeLimit { get; }

    /// <summary><c>movement.ledgeSupportMinNormalY</c>.</summary>
    public float LedgeSupportMinNormalY { get; }

    /// <summary><c>movement.contactSkin</c>, m.</summary>
    public float ContactSkin { get; }

    /// <summary><c>movement.contactTolerance</c>, m.</summary>
    public float ContactTolerance { get; }

    /// <summary><c>movement.maxSlideIterations</c>.</summary>
    public int MaxSlideIterations { get; }

    /// <summary><c>movement.maxSubMoves</c>.</summary>
    public int MaxSubMoves { get; }

    /// <summary>
    /// The capsule axis's top above the feet, by stance (<see cref="Standing"/>, <see cref="Crouched"/>):
    /// <c>standHeight - capsuleRadius</c> and <c>crouchHeight - capsuleRadius</c>, m.
    /// </summary>
    public ImmutableArray<float> AxisTop { get; }

    /// <summary><c>capsuleRadius + contactSkin</c>, m.</summary>
    public float SweepTarget { get; }

    /// <summary><c>0.1f * contactSkin</c>, m.</summary>
    public float SweepTolerance { get; }

    /// <summary><c>capsuleRadius - (0.5f * SweepTolerance)</c>, m.</summary>
    public float PushOutThreshold { get; }

    /// <summary><c>capsuleRadius + (0.5f * contactSkin)</c>, m.</summary>
    public float HeadroomClearance { get; }

    // ---------------------------------------------------------------- weapons

    /// <summary>Fire-rate units per shot, <c>60 * Hz</c>.</summary>
    public int ShotCost { get; }

    /// <summary>The compiled weapons, by slot.</summary>
    public ImmutableArray<CompiledWeapon> Weapons { get; }

    /// <summary>S, the largest <c>maxShotsPerStep</c> over the weapons, which sizes the shot event buffer.</summary>
    public int MaxShotsPerStepMax { get; }

    // ---------------------------------------------------------------- projectiles

    /// <summary><c>(-0.5f * movement.gravity) * gravityFactor</c> by class, m/s².</summary>
    public ImmutableArray<float> HalfG { get; }

    /// <summary><c>maxRangeM * maxRangeM</c> by class, m².</summary>
    public ImmutableArray<float> MaxRangeSq { get; }

    /// <summary><c>ceilSteps(lifetimeMs)</c> by class, at least 1.</summary>
    public ImmutableArray<int> LifetimeSteps { get; }

    // ---------------------------------------------------------------- hitboxes and buffers

    /// <summary>The hitbox layout by stance (<see cref="Standing"/>, <see cref="Crouched"/>).</summary>
    public ImmutableArray<HitboxLayout> HitboxLayouts { get; }

    /// <summary>
    /// <c>StateSerializer.MaxLength(maxCharacters, maxProjectiles, weaponSlots, MaxShotsPerStepMax)</c>,
    /// which sizes the world's state buffer.
    /// </summary>
    public int MaxStateLength { get; }

    /// <summary><see cref="MaxStateLength"/> + 16, which sizes every fixed-capacity step record buffer.</summary>
    public int MaxStepRecordLength { get; }

    /// <summary>
    /// Runs <see cref="ConfigGuard.Check"/> on <paramref name="config"/> and, when it finds no error,
    /// compiles a deep copy of it.
    /// </summary>
    /// <exception cref="ConfigValidationException">The guard found errors; they are its list, unchanged.</exception>
    public static CompiledConfig Create(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = ConfigGuard.Check(config);
        if (errors.Count != 0)
        {
            throw new ConfigValidationException(errors);
        }

        return new CompiledConfig(config.Clone());
    }

    /// <summary><c>ceilSteps(ms)</c>: <c>(ms * Hz + 999) / 1000</c> in <see cref="int"/>.</summary>
    internal static int CeilSteps(int milliseconds, int hz) => ((milliseconds * hz) + 999) / 1000;

    /// <summary>
    /// <c>angleUnits(deg)</c>: <c>SimMath.RoundToInt((deg * 65536f) / 360f)</c>, binary angle units with
    /// 65,536 per turn, rounded to nearest with ties to even.
    /// </summary>
    internal static int AngleUnits(float degrees) => SimMath.RoundToInt((degrees * 65536f) / 360f);

    /// <summary>
    /// A stance's layout: the three capsules, the bounding sphere's centre height
    /// <c>(lo + hi) * 0.5f</c> with <c>lo</c> the lowest <c>a.up - radius</c> and <c>hi</c> the highest
    /// <c>b.up + radius</c>, its radius (the largest endpoint distance from the centre plus the capsule's
    /// radius, plus 0.01f) and the eye height.
    /// </summary>
    private static HitboxLayout Layout(List<HitboxCapsuleConfig?> capsules, float eyeHeight)
    {
        var low = 0f;
        var high = 0f;
        for (var c = 0; c < capsules.Count; c++)
        {
            var capsule = capsules[c]!;
            var bottom = capsule.A!.Up - capsule.Radius;
            var top = capsule.B!.Up + capsule.Radius;
            low = c == 0 ? bottom : SimMath.Min(low, bottom);
            high = c == 0 ? top : SimMath.Max(high, top);
        }

        var centerUp = (low + high) * 0.5f;
        var reach = 0f;
        var layouts = ImmutableArray.CreateBuilder<HitboxCapsuleLayout>(capsules.Count);
        for (var c = 0; c < capsules.Count; c++)
        {
            var capsule = capsules[c]!;
            var a = capsule.A!;
            var b = capsule.B!;
            var reachA = EndpointReach(a, centerUp) + capsule.Radius;
            var reachB = EndpointReach(b, centerUp) + capsule.Radius;
            reach = c == 0 ? reachA : SimMath.Max(reach, reachA);
            reach = SimMath.Max(reach, reachB);
            layouts.Add(new HitboxCapsuleLayout(
                new HitboxLocalPoint(a.Fwd, a.Right, a.Up),
                new HitboxLocalPoint(b.Fwd, b.Right, b.Up),
                capsule.Radius));
        }

        return new HitboxLayout(layouts.MoveToImmutable(), centerUp, reach + 0.01f, eyeHeight);
    }

    /// <summary>
    /// <c>SimMath.Sqrt(((e.fwd * e.fwd) + (e.right * e.right)) + ((e.up - centerUp) * (e.up - centerUp)))</c>.
    /// </summary>
    private static float EndpointReach(HitboxPointConfig e, float centerUp)
    {
        var up = e.Up - centerUp;
        return SimMath.Sqrt(((e.Fwd * e.Fwd) + (e.Right * e.Right)) + (up * up));
    }
}
