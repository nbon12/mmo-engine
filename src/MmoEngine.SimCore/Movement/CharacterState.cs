using MmoEngine.SimCore.Geometry;

namespace MmoEngine.SimCore.Movement;

/// <summary>
/// One character's state, the movement step's input and output (FR-001,
/// specs/015-shared-simulation-core/data-model.md#character-state). Its members are named and ordered as
/// the canonical state's character record (contracts/state-serialization.md#character-record). The
/// weapons are not here: the world holds them flat, by character index × <c>input.weaponSlots</c> + slot.
/// </summary>
/// <remarks>
/// Two states are equal when every member is; <see cref="Vec3"/> members compare their components' bits.
/// </remarks>
public record struct CharacterState
{
    /// <summary>Unique in the world and ascending; also the spread hash's player ID (FR-008).</summary>
    public uint Id { get; set; }

    /// <summary>The feet, which is the capsule bottom, in metres; finite, |v| ≤ 8192, inside the playable area.</summary>
    public Vec3 Position { get; set; }

    /// <summary>The velocity in m/s; finite.</summary>
    public Vec3 Velocity { get; set; }

    /// <summary>The support normal while grounded (y ≥ <c>CosSlopeLimit</c>); exactly (0, 1, 0) while airborne.</summary>
    public Vec3 GroundNormal { get; set; }

    /// <summary>The aim yaw from the last applied input, a binary angle (65,536 per turn); yaw 0 faces +Z.</summary>
    public ushort AimYaw { get; set; }

    /// <summary>The aim pitch, a binary angle in [−16384, 16384]; positive looks up.</summary>
    public short AimPitch { get; set; }

    /// <summary>Standing or crouched.</summary>
    public Stance Stance { get; set; }

    /// <summary>Grounded and ClampedToBounds (in the last step); bits 2 to 7 are 0.</summary>
    public CharacterFlags Flags { get; set; }

    /// <summary>The sequence of the last applied input command; 0 before the first.</summary>
    public uint LastSequence { get; set; }

    /// <summary>The active weapon slot, below <see cref="WeaponCount"/>.</summary>
    public byte ActiveSlot { get; set; }

    /// <summary>The number of weapons, equal to <c>input.weaponSlots</c>.</summary>
    public byte WeaponCount { get; set; }
}
