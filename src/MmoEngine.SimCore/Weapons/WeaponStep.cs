using System;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Weapons;

/// <summary>
/// The weapon step (FR-006, specs/015-shared-simulation-core/data-model.md#weapon-step and
/// #weapon-phases): one character's weapons for one step, after movement, in sub-steps W0 to W6. Fire
/// timing is an integer accumulator (FR-007): a step adds <c>Rpm</c> fire-rate units and a shot costs
/// <c>ShotCost</c>, so the shot count never drifts from the configured rate by more than one. Everything is
/// integer arithmetic apart from the spread direction (<see cref="Spread"/>) and the projectile spawn. The
/// step is static, holds no state and allocates nothing, and every loop is bounded by the slot count or
/// <c>MaxShotsPerStep</c>.
/// </summary>
public static class WeaponStep
{
    /// <summary>The lowest aim pitch with recoil, a binary angle: straight down.</summary>
    private const int MinPitch = -16384;

    /// <summary>The highest aim pitch with recoil, a binary angle: straight up.</summary>
    private const int MaxPitch = 16384;

    /// <summary>
    /// Steps one character's weapons: W0 prepare every weapon, W1 the slot change, W2 the active weapon's
    /// count-down, W3 the reload start, W4 cooling and recoil recovery for every weapon, W5 the fire block
    /// and the fire loop, and W6 every weapon's fire wait.
    /// </summary>
    /// <param name="config">The compiled configuration.</param>
    /// <param name="matchSalt">The world's match salt, an input to the spread hash.</param>
    /// <param name="step">The step being computed, each spawned projectile's <c>LaunchStep</c>.</param>
    /// <param name="character">The post-movement next state; only <see cref="CharacterState.ActiveSlot"/> is written.</param>
    /// <param name="weapons">This character's <c>input.weaponSlots</c> weapon states, updated in place.</param>
    /// <param name="input">The accepted command, or the neutral input.</param>
    /// <param name="projectileCount">The world's projectiles, including those spawned earlier in this step.</param>
    /// <param name="nextProjectileId">The next projectile ID; advanced by one per shot.</param>
    /// <param name="spawned">Receives the spawned projectiles, in firing order.</param>
    /// <param name="shots">Receives the shot events, in firing order.</param>
    /// <param name="fireBlock">Why a held trigger fired no shot; <see cref="FireBlock.None"/> otherwise.</param>
    /// <returns>The number of shots, equal to the projectiles written to <paramref name="spawned"/> and the events written to <paramref name="shots"/>.</returns>
    public static int Step(
        CompiledConfig config,
        ulong matchSalt,
        uint step,
        ref CharacterState character,
        Span<WeaponState> weapons,
        in InputCommand input,
        int projectileCount,
        ref uint nextProjectileId,
        Span<Projectile> spawned,
        Span<ShotEvent> shots,
        out FireBlock fireBlock)
    {
        ArgumentNullException.ThrowIfNull(config);
        var fire = (input.Buttons & Buttons.Fire) != 0;
        var reload = (input.Buttons & Buttons.Reload) != 0;

        Prepare(weapons, fire);
        var switched = ChangeSlot(config, ref character, weapons, input.WeaponSlot);

        var slot = character.ActiveSlot;
        var weapon = config.Weapons[slot];
        ref var active = ref weapons[slot];
        if (!switched)
        {
            CountDown(ref active, weapon);
        }

        if (reload)
        {
            StartReload(ref active, weapon);
        }

        CoolAndRecover(config, weapons);

        var fired = 0;
        fireBlock = FireBlock.None;
        if (fire)
        {
            fired = FireLoop(
                config,
                matchSalt,
                step,
                in character,
                slot,
                ref active,
                weapon,
                projectileCount,
                ref nextProjectileId,
                spawned,
                shots,
                out fireBlock);
        }
        else if (active.Phase == WeaponPhase.Firing)
        {
            active.Phase = WeaponPhase.Ready;
        }

        // W6: every weapon's fire wait counts down by its rate, never below 0, so no shot is banked.
        for (var s = 0; s < weapons.Length; s++)
        {
            ref var w = ref weapons[s];
            w.FireWait = System.Math.Max(w.FireWait - config.Weapons[s].Rpm, 0);
        }

        return fired;
    }

    /// <summary>W0: StepsSinceShot += 1, saturating, for every weapon; a released trigger clears TriggerLatched.</summary>
    private static void Prepare(Span<WeaponState> weapons, bool fire)
    {
        for (var s = 0; s < weapons.Length; s++)
        {
            ref var w = ref weapons[s];
            if (w.StepsSinceShot != WeaponState.NeverFired)
            {
                w.StepsSinceShot = (ushort)(w.StepsSinceShot + 1);
            }

            if (!fire)
            {
                w.Flags &= ~WeaponFlags.TriggerLatched;
            }
        }
    }

    /// <summary>
    /// W1: when <paramref name="weaponSlot"/> differs from the active slot, the old weapon leaves Firing,
    /// Reloading or Switching for Ready (cancelling a reload) or stays Overheated, with PhaseSteps 0; the new
    /// weapon starts Switching with PhaseSteps = EquipSteps (Ready at once when EquipSteps is 0), or stays
    /// Overheated owing EquipSteps; then ActiveSlot = <paramref name="weaponSlot"/>.
    /// </summary>
    /// <returns>True when the slot changed, so W2 does not count down the PhaseSteps W1 set.</returns>
    private static bool ChangeSlot(CompiledConfig config, ref CharacterState character, Span<WeaponState> weapons, byte weaponSlot)
    {
        if (weaponSlot == character.ActiveSlot)
        {
            return false;
        }

        ref var old = ref weapons[character.ActiveSlot];
        if (old.Phase is WeaponPhase.Firing or WeaponPhase.Reloading or WeaponPhase.Switching)
        {
            old.Phase = WeaponPhase.Ready;
        }

        old.PhaseSteps = 0;

        ref var incoming = ref weapons[weaponSlot];
        var equipSteps = config.Weapons[weaponSlot].EquipSteps;
        if (incoming.Phase == WeaponPhase.Overheated)
        {
            incoming.PhaseSteps = (ushort)equipSteps;
        }
        else if (equipSteps > 0)
        {
            incoming.Phase = WeaponPhase.Switching;
            incoming.PhaseSteps = (ushort)equipSteps;
        }

        character.ActiveSlot = weaponSlot;
        return true;
    }

    /// <summary>
    /// W2: in Switching, Reloading or Overheated with PhaseSteps above 0, PhaseSteps −= 1; Switching reaching
    /// 0 is Ready, and Reloading reaching 0 completes the reload.
    /// </summary>
    private static void CountDown(ref WeaponState active, CompiledWeapon weapon)
    {
        if (active.Phase is not (WeaponPhase.Switching or WeaponPhase.Reloading or WeaponPhase.Overheated)
            || active.PhaseSteps == 0)
        {
            return;
        }

        active.PhaseSteps = (ushort)(active.PhaseSteps - 1);
        if (active.PhaseSteps != 0)
        {
            return;
        }

        if (active.Phase == WeaponPhase.Switching)
        {
            active.Phase = WeaponPhase.Ready;
        }
        else if (active.Phase == WeaponPhase.Reloading)
        {
            CompleteReload(ref active, weapon);
        }
    }

    /// <summary>
    /// W3: Reload held in Ready or Firing with Magazine below <c>magazineSize</c> and Reserve above 0 starts
    /// Reloading with PhaseSteps = ReloadSteps; a ReloadSteps of 0 completes at entry.
    /// </summary>
    private static void StartReload(ref WeaponState active, CompiledWeapon weapon)
    {
        if (active.Phase is not (WeaponPhase.Ready or WeaponPhase.Firing)
            || active.Magazine >= weapon.MagazineSize
            || active.Reserve == 0)
        {
            return;
        }

        if (weapon.ReloadSteps == 0)
        {
            CompleteReload(ref active, weapon);
            return;
        }

        active.Phase = WeaponPhase.Reloading;
        active.PhaseSteps = (ushort)weapon.ReloadSteps;
    }

    /// <summary>Moves min(<c>magazineSize</c> − Magazine, Reserve) rounds into the magazine and makes the weapon Ready.</summary>
    private static void CompleteReload(ref WeaponState w, CompiledWeapon weapon)
    {
        var moved = System.Math.Min(weapon.MagazineSize - w.Magazine, w.Reserve);
        w.Magazine = (ushort)(w.Magazine + moved);
        w.Reserve = (ushort)(w.Reserve - moved);
        w.Phase = WeaponPhase.Ready;
        w.PhaseSteps = 0;
    }

    /// <summary>
    /// W4, for every weapon: from <c>HeatCooldownDelaySteps</c> steps after its last shot, Heat −=
    /// <c>HeatCooldownPerStep</c>, not below 0, and an Overheated weapon at or below the recovery level exits
    /// to Switching when it still owes equip time, else to Ready; from <c>RecoilRecoveryDelaySteps</c> steps
    /// after its last shot, both recoil offsets move toward 0 by <c>RecoilRecoveryPerStep</c>, never past it.
    /// </summary>
    private static void CoolAndRecover(CompiledConfig config, Span<WeaponState> weapons)
    {
        for (var s = 0; s < weapons.Length; s++)
        {
            ref var w = ref weapons[s];
            var weapon = config.Weapons[s];
            if (w.StepsSinceShot >= weapon.HeatCooldownDelaySteps)
            {
                w.Heat = w.Heat > weapon.HeatCooldownPerStep ? w.Heat - weapon.HeatCooldownPerStep : 0u;
                if (w.Phase == WeaponPhase.Overheated && w.Heat <= weapon.HeatRecoveryScaled)
                {
                    w.Phase = w.PhaseSteps > 0 ? WeaponPhase.Switching : WeaponPhase.Ready;
                }
            }

            if (w.StepsSinceShot >= weapon.RecoilRecoveryDelaySteps)
            {
                w.RecoilPitch = TowardZero(w.RecoilPitch, weapon.RecoilRecoveryPerStep);
                w.RecoilYaw = TowardZero(w.RecoilYaw, weapon.RecoilRecoveryPerStep);
            }
        }
    }

    /// <summary><paramref name="value"/> moved toward 0 by <paramref name="amount"/>, never past it.</summary>
    private static int TowardZero(int value, int amount) =>
        value > 0 ? System.Math.Max(value - amount, 0) : System.Math.Min(value + amount, 0);

    /// <summary>
    /// W5 with Fire held: the first fire block of Switching, Reloading, Overheated, Empty and AwaitingRelease
    /// stops a Firing weapon (Ready) and fires nothing; otherwise the fire loop fires while the fire wait is
    /// below the rate, below <c>MaxShotsPerStep</c> shots, with rounds in the magazine, not overheated and not
    /// latched. It stops at the world's projectile limit or when projectile IDs are used up, reporting
    /// ProjectileLimit only when no shot fired. The weapon ends Overheated, Firing, or Ready when
    /// ProjectileLimit was reported.
    /// </summary>
    private static int FireLoop(
        CompiledConfig config,
        ulong matchSalt,
        uint step,
        in CharacterState character,
        int slot,
        ref WeaponState active,
        CompiledWeapon weapon,
        int projectileCount,
        ref uint nextProjectileId,
        Span<Projectile> spawned,
        Span<ShotEvent> shots,
        out FireBlock fireBlock)
    {
        var semi = weapon.FireMode == FireMode.Semi;
        fireBlock = BlockOf(in active, semi);
        if (fireBlock != FireBlock.None)
        {
            if (active.Phase == WeaponPhase.Firing)
            {
                active.Phase = WeaponPhase.Ready;
            }

            return 0;
        }

        var fired = 0;
        while (active.FireWait < weapon.Rpm
            && fired < weapon.MaxShotsPerStep
            && active.Magazine > 0
            && active.Phase != WeaponPhase.Overheated
            && !(semi && (active.Flags & WeaponFlags.TriggerLatched) != 0))
        {
            if (projectileCount + fired >= config.MaxProjectiles || nextProjectileId == uint.MaxValue)
            {
                if (fired == 0)
                {
                    fireBlock = FireBlock.ProjectileLimit;
                }

                break;
            }

            FireOne(config, matchSalt, step, in character, slot, ref active, weapon, ref nextProjectileId, out spawned[fired], out shots[fired]);
            fired++;
        }

        if (active.Phase != WeaponPhase.Overheated)
        {
            active.Phase = fireBlock == FireBlock.ProjectileLimit ? WeaponPhase.Ready : WeaponPhase.Firing;
        }

        return fired;
    }

    /// <summary>The first fire block that applies, in the order Switching, Reloading, Overheated, Empty, AwaitingRelease.</summary>
    private static FireBlock BlockOf(in WeaponState active, bool semi)
    {
        switch (active.Phase)
        {
            case WeaponPhase.Switching:
                return FireBlock.Switching;
            case WeaponPhase.Reloading:
                return FireBlock.Reloading;
            case WeaponPhase.Overheated:
                return FireBlock.Overheated;
        }

        if (active.Magazine == 0)
        {
            return FireBlock.Empty;
        }

        return semi && (active.Flags & WeaponFlags.TriggerLatched) != 0 ? FireBlock.AwaitingRelease : FireBlock.None;
    }

    /// <summary>
    /// One shot, steps 1 to 8 of data-model.md#weapon-step: the aim with the recoil from before this shot's
    /// kick, the spread hash and direction, the projectile spawned at the eye with the muzzle velocity (the
    /// shooter's velocity is not inherited), the shot event, the counters and heat, the recoil kick, and the
    /// fire wait and trigger latch.
    /// </summary>
    private static void FireOne(
        CompiledConfig config,
        ulong matchSalt,
        uint step,
        in CharacterState character,
        int slot,
        ref WeaponState w,
        CompiledWeapon weapon,
        ref uint nextProjectileId,
        out Projectile projectile,
        out ShotEvent shot)
    {
        // 1. Aim with recoil: the yaw wraps, the pitch is clamped in int.
        var yaw = unchecked((ushort)(character.AimYaw + w.RecoilYaw));
        var pitch = System.Math.Clamp(character.AimPitch + w.RecoilPitch, MinPitch, MaxPitch);

        // 2 and 3. The spread hash and the direction.
        var hash = Spread.Hash(matchSalt, character.Id, slot, w.ShotCounter);
        var direction = Spread.Direction(hash, yaw, (short)pitch, weapon.VersMax);

        // 4. The projectile, at the eye of the post-movement stance.
        var id = nextProjectileId;
        nextProjectileId = id + 1u;
        var position = character.Position;
        var eyeHeight = config.HitboxLayouts[(int)character.Stance].EyeHeight;
        var origin = new Vec3(position.X, position.Y + eyeHeight, position.Z);
        projectile = new Projectile
        {
            Id = id,
            OwnerId = character.Id,
            Class = (byte)weapon.ProjectileClass,
            LaunchStep = step,
            Origin = origin,
            LaunchVelocity = direction * weapon.MuzzleSpeed,
            Position = origin,
        };

        // 5. The shot event, with the counter the hash used.
        shot = new ShotEvent
        {
            CharacterId = character.Id,
            Slot = (byte)slot,
            ShotCounter = w.ShotCounter,
            ProjectileId = id,
            Direction = direction,
        };

        // 6. Counters and heat.
        w.Magazine = (ushort)(w.Magazine - 1);
        w.ShotCounter = unchecked(w.ShotCounter + 1u);
        w.Heat += weapon.HeatPerShotScaled;
        if (w.Heat >= weapon.OverheatThresholdScaled)
        {
            w.Phase = WeaponPhase.Overheated;
        }

        // 7. The recoil kick.
        w.RecoilPitch = System.Math.Min(w.RecoilPitch + weapon.RecoilKickPitchUnits, weapon.RecoilMaxPitchUnits);
        var kick = Spread.KicksYawPositive(hash) ? weapon.RecoilKickYawUnits : -weapon.RecoilKickYawUnits;
        w.RecoilYaw = System.Math.Clamp(w.RecoilYaw + kick, -weapon.RecoilMaxYawUnits, weapon.RecoilMaxYawUnits);

        // 8. The fire wait and the trigger latch.
        w.StepsSinceShot = 0;
        w.FireWait += config.ShotCost;
        if (weapon.FireMode == FireMode.Semi)
        {
            w.Flags |= WeaponFlags.TriggerLatched;
        }
    }
}
