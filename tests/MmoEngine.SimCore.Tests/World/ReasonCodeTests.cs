using System.Globalization;
using System.Reflection;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.World;

/// <summary>
/// Every reason and outcome code has the value data-model.md#reason-and-outcome-codes gives it, and
/// each enum has exactly the contract's members and underlying type. A value never changes meaning:
/// the codes are written into the hashed state, the formats and the config block, so a renumbered or
/// added member fails here first.
/// </summary>
public sealed class ReasonCodeTests
{
    /// <summary>One row per member: the enum, the member's name and its contract value.</summary>
    [Theory]
    [InlineData(typeof(InputRejection), "None", 0L)]
    [InlineData(typeof(InputRejection), "SequenceNotIncreasing", 1L)]
    [InlineData(typeof(InputRejection), "UnknownButtons", 2L)]
    [InlineData(typeof(InputRejection), "PitchOutOfRange", 3L)]
    [InlineData(typeof(InputRejection), "UnknownWeaponSlot", 4L)]
    [InlineData(typeof(WeaponPhase), "Ready", 0L)]
    [InlineData(typeof(WeaponPhase), "Firing", 1L)]
    [InlineData(typeof(WeaponPhase), "Reloading", 2L)]
    [InlineData(typeof(WeaponPhase), "Switching", 3L)]
    [InlineData(typeof(WeaponPhase), "Overheated", 4L)]
    [InlineData(typeof(FireMode), "Auto", 0L)]
    [InlineData(typeof(FireMode), "Semi", 1L)]
    [InlineData(typeof(FireBlock), "None", 0L)]
    [InlineData(typeof(FireBlock), "Switching", 1L)]
    [InlineData(typeof(FireBlock), "Reloading", 2L)]
    [InlineData(typeof(FireBlock), "Empty", 3L)]
    [InlineData(typeof(FireBlock), "Overheated", 4L)]
    [InlineData(typeof(FireBlock), "AwaitingRelease", 5L)]
    [InlineData(typeof(FireBlock), "ProjectileLimit", 6L)]
    [InlineData(typeof(ExpiryReason), "Range", 1L)]
    [InlineData(typeof(ExpiryReason), "Lifetime", 2L)]
    [InlineData(typeof(ExpiryReason), "OutOfBounds", 3L)]
    [InlineData(typeof(HitKind), "Terrain", 0L)]
    [InlineData(typeof(HitKind), "Mesh", 1L)]
    [InlineData(typeof(HitKind), "Character", 2L)]
    [InlineData(typeof(HitboxPart), "Head", 0L)]
    [InlineData(typeof(HitboxPart), "Torso", 1L)]
    [InlineData(typeof(HitboxPart), "Legs", 2L)]
    [InlineData(typeof(HitboxPart), "None", 255L)]
    [InlineData(typeof(MovementOutcome), "None", 0L)]
    [InlineData(typeof(MovementOutcome), "HeadroomBlocked", 1L)]
    [InlineData(typeof(MovementOutcome), "Stuck", 2L)]
    [InlineData(typeof(MovementOutcome), "SlideCapReached", 4L)]
    [InlineData(typeof(MovementOutcome), "ClampedToBounds", 8L)]
    [InlineData(typeof(Stance), "Standing", 0L)]
    [InlineData(typeof(Stance), "Crouched", 1L)]
    [InlineData(typeof(CharacterFlags), "Grounded", 1L)]
    [InlineData(typeof(CharacterFlags), "ClampedToBounds", 2L)]
    [InlineData(typeof(WeaponFlags), "TriggerLatched", 1L)]
    [InlineData(typeof(StepFaultCode), "None", 0L)]
    [InlineData(typeof(StepFaultCode), "NonFinite", 1L)]
    [InlineData(typeof(StepFaultCode), "QueryCapacityExceeded", 2L)]
    [InlineData(typeof(FormatErrorCode), "None", 0L)]
    [InlineData(typeof(FormatErrorCode), "UnknownMagic", 1L)]
    [InlineData(typeof(FormatErrorCode), "UnsupportedVersion", 2L)]
    [InlineData(typeof(FormatErrorCode), "Truncated", 3L)]
    [InlineData(typeof(FormatErrorCode), "Oversized", 4L)]
    [InlineData(typeof(FormatErrorCode), "Inconsistent", 5L)]
    [InlineData(typeof(FormatErrorCode), "OutOfRange", 6L)]
    [InlineData(typeof(FormatErrorCode), "NonFinite", 7L)]
    [InlineData(typeof(FormatErrorCode), "ChecksumMismatch", 8L)]
    [InlineData(typeof(FormatErrorCode), "CollisionDataMissing", 9L)]
    [InlineData(typeof(FormatErrorCode), "RulesRevisionMismatch", 10L)]
    [InlineData(typeof(Buttons), "Forward", 0x0001L)]
    [InlineData(typeof(Buttons), "Back", 0x0002L)]
    [InlineData(typeof(Buttons), "Left", 0x0004L)]
    [InlineData(typeof(Buttons), "Right", 0x0008L)]
    [InlineData(typeof(Buttons), "Jump", 0x0010L)]
    [InlineData(typeof(Buttons), "Crouch", 0x0020L)]
    [InlineData(typeof(Buttons), "Walk", 0x0040L)]
    [InlineData(typeof(Buttons), "Fire", 0x0080L)]
    [InlineData(typeof(Buttons), "Reload", 0x0100L)]
    [InlineData(typeof(Buttons), "Known", 0x01FFL)]
    public void EveryCodeHasItsContractValue(Type enumType, string member, long value)
    {
        Assert.True(enumType.IsEnum, $"{enumType.FullName} is not an enum");

        var field = enumType.GetField(member, BindingFlags.Public | BindingFlags.Static);
        Assert.True(field is { IsLiteral: true }, $"{enumType.Name} has no member {member}");

        var actual = Convert.ToInt64(field.GetRawConstantValue(), CultureInfo.InvariantCulture);
        Assert.True(actual == value, $"{enumType.Name}.{member} is {actual}, the contract value is {value}");
    }

    /// <summary>
    /// Each enum has exactly the contract's number of members, so the rows of
    /// <see cref="EveryCodeHasItsContractValue"/> name every member. The underlying type is
    /// <see cref="byte"/>, except <see cref="Buttons"/> (<see cref="ushort"/>) and
    /// <see cref="FireMode"/> (<see cref="uint"/>, because the config block carries it as a 32-bit
    /// <c>valueBits</c>). <see cref="Buttons"/>, <see cref="MovementOutcome"/>,
    /// <see cref="CharacterFlags"/> and <see cref="WeaponFlags"/> are <see cref="FlagsAttribute"/> enums
    /// and the others are not.
    /// </summary>
    [Theory]
    [InlineData(typeof(InputRejection), 5, typeof(byte), false)]
    [InlineData(typeof(WeaponPhase), 5, typeof(byte), false)]
    [InlineData(typeof(FireMode), 2, typeof(uint), false)]
    [InlineData(typeof(FireBlock), 7, typeof(byte), false)]
    [InlineData(typeof(ExpiryReason), 3, typeof(byte), false)]
    [InlineData(typeof(HitKind), 3, typeof(byte), false)]
    [InlineData(typeof(HitboxPart), 4, typeof(byte), false)]
    [InlineData(typeof(MovementOutcome), 5, typeof(byte), true)]
    [InlineData(typeof(Stance), 2, typeof(byte), false)]
    [InlineData(typeof(CharacterFlags), 2, typeof(byte), true)]
    [InlineData(typeof(WeaponFlags), 1, typeof(byte), true)]
    [InlineData(typeof(StepFaultCode), 3, typeof(byte), false)]
    [InlineData(typeof(FormatErrorCode), 11, typeof(byte), false)]
    [InlineData(typeof(Buttons), 10, typeof(ushort), true)]
    public void EveryEnumHasExactlyTheContractMembersAndUnderlyingType(Type enumType, int memberCount, Type underlyingType, bool isFlags)
    {
        Assert.True(enumType.IsEnum, $"{enumType.FullName} is not an enum");

        var members = Enum.GetNames(enumType);
        Assert.True(members.Length == memberCount, $"{enumType.Name} has {members.Length} members [{string.Join(", ", members)}], the contract has {memberCount}");

        var actualUnderlying = Enum.GetUnderlyingType(enumType);
        Assert.True(actualUnderlying == underlyingType, $"{enumType.Name} has underlying type {actualUnderlying.Name}, the contract's is {underlyingType.Name}");

        var actualFlags = enumType.IsDefined(typeof(FlagsAttribute), inherit: false);
        Assert.True(actualFlags == isFlags, isFlags ? $"{enumType.Name} must be [Flags]" : $"{enumType.Name} must not be [Flags]");
    }
}
