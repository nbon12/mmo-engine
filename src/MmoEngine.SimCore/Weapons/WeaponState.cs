using System;

namespace MmoEngine.SimCore.Weapons;

/// <summary>
/// One weapon slot's state (specs/015-shared-simulation-core/data-model.md#weaponstate). It holds no
/// floats, so fire timing is exact (FR-007). Its members are named and ordered as the canonical state's
/// weapon record (contracts/state-serialization.md#weapon-record).
/// </summary>
public record struct WeaponState
{
    /// <summary>
    /// <see cref="StepsSinceShot"/>'s saturation value, which is also a new weapon's: it has never fired.
    /// </summary>
    public const ushort NeverFired = ushort.MaxValue;

    /// <summary>Ready, Firing, Reloading, Switching or Overheated.</summary>
    public WeaponPhase Phase { get; set; }

    /// <summary>TriggerLatched; bits 1 to 7 are 0.</summary>
    public WeaponFlags Flags { get; set; }

    /// <summary>Steps left in Switching or Reloading; while Overheated, the equip time still owed; otherwise 0.</summary>
    public ushort PhaseSteps { get; set; }

    /// <summary>Fire-rate units still to wait; 0 ≤ FireWait &lt; ShotCost after every step.</summary>
    public int FireWait { get; set; }

    /// <summary>Shots this weapon has fired; wraps at 2³²; an input to the spread hash.</summary>
    public uint ShotCounter { get; set; }

    /// <summary>Rounds in the magazine, at most <c>magazineSize</c>.</summary>
    public ushort Magazine { get; set; }

    /// <summary>Rounds outside the magazine.</summary>
    public ushort Reserve { get; set; }

    /// <summary>Heat × <c>stepRateHz</c>.</summary>
    public uint Heat { get; set; }

    /// <summary>Steps since this weapon last fired, saturating at 65535.</summary>
    public ushort StepsSinceShot { get; set; }

    /// <summary>Recoil pitch in binary angle units, [0, RecoilMaxPitchUnits].</summary>
    public int RecoilPitch { get; set; }

    /// <summary>Recoil yaw in binary angle units, [−RecoilMaxYawUnits, RecoilMaxYawUnits].</summary>
    public int RecoilYaw { get; set; }

    /// <summary>
    /// A new character's weapon: Ready, flags 0, <see cref="PhaseSteps"/> 0, <see cref="FireWait"/> 0,
    /// <see cref="ShotCounter"/> 0, a full magazine, <paramref name="reserveRounds"/> in reserve, heat 0,
    /// <see cref="StepsSinceShot"/> 65535 and no recoil.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative or above 65535.</exception>
    public static WeaponState New(int magazineSize, int reserveRounds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(magazineSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(magazineSize, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegative(reserveRounds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(reserveRounds, ushort.MaxValue);
        return new WeaponState
        {
            Phase = WeaponPhase.Ready,
            Magazine = (ushort)magazineSize,
            Reserve = (ushort)reserveRounds,
            StepsSinceShot = NeverFired,
        };
    }
}
