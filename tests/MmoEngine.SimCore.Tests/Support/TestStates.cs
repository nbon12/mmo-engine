using System.Buffers.Binary;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Canonical states for tests (contracts/state-serialization.md, serialisation version 1).
/// <see cref="Build"/> writes every field with <see cref="BinaryPrimitives"/> at the contract's offsets,
/// independent of <c>StateSerializer</c>, so a serializer test compares the core with a second, separate
/// reading of the contract. It checks no value, so a test can build a state the core must refuse.
/// Every member returns a new array, so a test may mutate what it gets.
/// </summary>
public static class TestStates
{
    public const int HeaderLength = 12;
    public const int CharacterFixedLength = 52;
    public const int WeaponLength = 30;
    public const int ProjectileLength = 49;
    public const int ShotLength = 25;
    public const int HitLength = 26;
    public const int SmallEventLength = 5;

    /// <summary>The six <c>u16</c> event counts.</summary>
    public const int EventCountsLength = 12;

    /// <summary>Character <c>flags</c> bit 0.</summary>
    public const byte Grounded = 0x01;

    /// <summary>Character <c>flags</c> bit 1.</summary>
    public const byte ClampedToBounds = 0x02;

    /// <summary>The placeholder <c>input.weaponSlots</c>.</summary>
    public const int PlaceholderWeaponSlots = 2;

    // contracts/state-serialization.md#worked-example, one dump row per line.
    private const string WorkedExampleHex =
        "00000000010000000100000001000000" +
        "0000003f00000000000000bf00000000" +
        "0000000000000000000000000000803f" +
        "00000000004000000001000000000002" +
        "0000000000000000000000001e00d200" +
        "00000000ffff00000000000000000000" +
        "00000000000000000000010005000000" +
        "0000ffff000000000000000000000000" +
        "0000000000000000";

    /// <summary>
    /// The worked example: step 0, character 1 standing at (0.5, 0, −0.5), grounded, yaw 16384, with the
    /// placeholder rifle and launcher, no projectiles, <c>nextProjectileId</c> 1 and no events. 136 bytes,
    /// hash <c>35038da2f2d9459c</c>. It is a format vector: no test may step from it.
    /// </summary>
    public static byte[] WorkedExample => Convert.FromHexString(WorkedExampleHex);

    /// <summary>The placeholder rifle, slot 0: Ready, 30 in the magazine, 210 in reserve.</summary>
    public static WeaponRecord Rifle { get; } = new() { Magazine = 30, Reserve = 210 };

    /// <summary>The placeholder launcher, slot 1: Ready, 1 in the magazine, 5 in reserve.</summary>
    public static WeaponRecord Launcher { get; } = new() { Magazine = 1, Reserve = 5 };

    /// <summary>The placeholder weapons in slot order.</summary>
    public static IReadOnlyList<WeaponRecord> PlaceholderWeapons { get; } = [Rifle, Launcher];

    /// <summary>
    /// <see cref="StandingCharacter"/> 1 at (0.5, 0, 0.5) on <see cref="TestWorlds.Flat"/>, at step 0.
    /// 136 bytes.
    /// </summary>
    public static byte[] StandingOnFlat => Build(0, [StandingCharacter(1, 0.5f, 0.5f)]);

    /// <summary>
    /// Characters 1 and 4, both standing on <see cref="TestWorlds.Flat"/> at step 0. Character 1 is at
    /// (0.5, 0, 0.5) with yaw 16384, pitch −512, <c>lastSequence</c> 5 and the launcher active (slot 1),
    /// so its neutral input differs from the default command in every field it copies. Character 4 is at
    /// (4.5, 0, 0.5) with everything else at the defaults.
    /// </summary>
    public static byte[] TwoCharacters => Build(
        0,
        [
            StandingCharacter(1, 0.5f, 0.5f) with { AimYaw = 16384, AimPitch = -512, LastSequence = 5, ActiveSlot = 1 },
            StandingCharacter(4, 4.5f, 0.5f),
        ]);

    /// <summary>
    /// Projectile 1 of class 0, owned by character 1: origin and position (0, 50, 0), launch velocity
    /// (0, 1, 0), launched at step 0.
    /// </summary>
    public static ProjectileRecord FlyingProjectile { get; } = new()
    {
        Id = 1,
        OwnerId = 1,
        Class = 0,
        LaunchStep = 0,
        Origin = (0f, 50f, 0f),
        LaunchVelocity = (0f, 1f, 0f),
        Position = (0f, 50f, 0f),
    };

    /// <summary>
    /// <see cref="StandingOnFlat"/>'s character plus <see cref="FlyingProjectile"/> at step 0, with
    /// <c>nextProjectileId</c> 2. Its <c>launchStep</c> equals the state's step, so a projectile step keeps
    /// it in flight for one step.
    /// </summary>
    public static byte[] WithFlyingProjectile => Build(0, [StandingCharacter(1, 0.5f, 0.5f)], [FlyingProjectile]);

    /// <summary>
    /// A character at rest at (<paramref name="x"/>, 0, <paramref name="z"/>): standing, grounded on a
    /// level surface (normal (0, 1, 0)), yaw 0, pitch 0, <c>lastSequence</c> 0, slot 0 active, with the
    /// placeholder weapons.
    /// </summary>
    public static CharacterRecord StandingCharacter(uint id, float x, float z) =>
        new() { Id = id, Position = (x, 0f, z) };

    /// <summary>
    /// Writes a canonical state: the header, the characters in the order given (each with its weapons),
    /// the projectiles in the order given, and the six event lists. Every value is written as given, so a
    /// state the core refuses can be built too. <c>weaponCount</c> is each character's
    /// <see cref="CharacterRecord.Weapons"/> count, and <paramref name="nextProjectileId"/> defaults to one
    /// more than the largest projectile ID, or 1 with no projectiles.
    /// </summary>
    public static byte[] Build(
        uint step,
        IReadOnlyList<CharacterRecord> characters,
        IReadOnlyList<ProjectileRecord>? projectiles = null,
        uint? nextProjectileId = null,
        EventRecords? events = null)
    {
        ArgumentNullException.ThrowIfNull(characters);
        projectiles ??= [];
        events ??= EventRecords.None;

        var length = HeaderLength + (ProjectileLength * projectiles.Count) + EventCountsLength
            + (ShotLength * events.Shots.Count) + (HitLength * events.Hits.Count)
            + (SmallEventLength * (events.Expiries.Count + events.InputRejections.Count
                + events.FireBlocks.Count + events.MovementOutcomes.Count));
        foreach (var character in characters)
        {
            length += CharacterLength(character.Weapons.Count);
        }

        var state = new byte[length];
        var span = state.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, step);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], checked((ushort)characters.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], checked((ushort)projectiles.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], nextProjectileId ?? DefaultNextProjectileId(projectiles));

        var offset = HeaderLength;
        foreach (var character in characters)
        {
            offset = WriteCharacter(span, offset, character);
        }

        foreach (var projectile in projectiles)
        {
            offset = WriteProjectile(span, offset, projectile);
        }

        offset = WriteShots(span, offset, events.Shots);
        offset = WriteHits(span, offset, events.Hits);
        offset = WriteSmallEvents(span, offset, events.Expiries);
        offset = WriteSmallEvents(span, offset, events.InputRejections);
        offset = WriteSmallEvents(span, offset, events.FireBlocks);
        offset = WriteSmallEvents(span, offset, events.MovementOutcomes);
        if (offset != length)
        {
            throw new InvalidOperationException($"Wrote {offset} bytes of a {length}-byte state.");
        }

        return state;
    }

    /// <summary>Writes <paramref name="bytes"/> into <paramref name="state"/> at <paramref name="offset"/>, in place, and returns the same array.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The bytes do not fit inside the state.</exception>
    public static byte[] Mutate(byte[] state, int offset, ReadOnlySpan<byte> bytes)
    {
        ArgumentNullException.ThrowIfNull(state);
        bytes.CopyTo(state.AsSpan(offset, bytes.Length));
        return state;
    }

    /// <summary>The length of a character record with <paramref name="weaponSlots"/> weapons: 52 + 30·W.</summary>
    public static int CharacterLength(int weaponSlots) => CharacterFixedLength + (WeaponLength * weaponSlots);

    /// <summary>The offset of <c>characters[index]</c> when every character has <paramref name="weaponSlots"/> weapons.</summary>
    public static int CharacterOffset(int index, int weaponSlots = PlaceholderWeaponSlots) =>
        HeaderLength + (CharacterLength(weaponSlots) * index);

    /// <summary>The offset of <c>characters[index].weapons[slot]</c>.</summary>
    public static int WeaponOffset(int index, int slot, int weaponSlots = PlaceholderWeaponSlots) =>
        CharacterOffset(index, weaponSlots) + CharacterFixedLength + (WeaponLength * slot);

    /// <summary>The offset of <c>projectiles[index]</c> after <paramref name="characterCount"/> characters.</summary>
    public static int ProjectileOffset(int characterCount, int index, int weaponSlots = PlaceholderWeaponSlots) =>
        CharacterOffset(characterCount, weaponSlots) + (ProjectileLength * index);

    /// <summary>A float's 4 little-endian bytes, bit for bit.</summary>
    public static byte[] F32(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>A <c>u32</c>'s 4 little-endian bytes; also a float given by its bits.</summary>
    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>An <c>i32</c>'s 4 little-endian bytes.</summary>
    public static byte[] I32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>A <c>u16</c>'s 2 little-endian bytes.</summary>
    public static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>An <c>i16</c>'s 2 little-endian bytes.</summary>
    public static byte[] I16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static uint DefaultNextProjectileId(IReadOnlyList<ProjectileRecord> projectiles)
    {
        var next = 1u;
        foreach (var projectile in projectiles)
        {
            if (projectile.Id >= next)
            {
                next = checked(projectile.Id + 1u);
            }
        }

        return next;
    }

    private static int WriteCharacter(Span<byte> state, int offset, CharacterRecord character)
    {
        var record = state[offset..];
        BinaryPrimitives.WriteUInt32LittleEndian(record, character.Id);
        WriteVector(record[4..], character.Position);
        WriteVector(record[16..], character.Velocity);
        WriteVector(record[28..], character.GroundNormal);
        BinaryPrimitives.WriteUInt16LittleEndian(record[40..], character.AimYaw);
        BinaryPrimitives.WriteInt16LittleEndian(record[42..], character.AimPitch);
        record[44] = character.Stance;
        record[45] = character.Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(record[46..], character.LastSequence);
        record[50] = character.ActiveSlot;
        record[51] = checked((byte)character.Weapons.Count);

        var weaponOffset = CharacterFixedLength;
        foreach (var weapon in character.Weapons)
        {
            WriteWeapon(record[weaponOffset..], weapon);
            weaponOffset += WeaponLength;
        }

        return offset + weaponOffset;
    }

    private static void WriteWeapon(Span<byte> record, WeaponRecord weapon)
    {
        record[0] = weapon.Phase;
        record[1] = weapon.Flags;
        BinaryPrimitives.WriteUInt16LittleEndian(record[2..], weapon.PhaseSteps);
        BinaryPrimitives.WriteInt32LittleEndian(record[4..], weapon.FireWait);
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], weapon.ShotCounter);
        BinaryPrimitives.WriteUInt16LittleEndian(record[12..], weapon.Magazine);
        BinaryPrimitives.WriteUInt16LittleEndian(record[14..], weapon.Reserve);
        BinaryPrimitives.WriteUInt32LittleEndian(record[16..], weapon.Heat);
        BinaryPrimitives.WriteUInt16LittleEndian(record[20..], weapon.StepsSinceShot);
        BinaryPrimitives.WriteInt32LittleEndian(record[22..], weapon.RecoilPitch);
        BinaryPrimitives.WriteInt32LittleEndian(record[26..], weapon.RecoilYaw);
    }

    private static int WriteProjectile(Span<byte> state, int offset, ProjectileRecord projectile)
    {
        var record = state[offset..];
        BinaryPrimitives.WriteUInt32LittleEndian(record, projectile.Id);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], projectile.OwnerId);
        record[8] = projectile.Class;
        BinaryPrimitives.WriteUInt32LittleEndian(record[9..], projectile.LaunchStep);
        WriteVector(record[13..], projectile.Origin);
        WriteVector(record[25..], projectile.LaunchVelocity);
        WriteVector(record[37..], projectile.Position);
        return offset + ProjectileLength;
    }

    private static int WriteShots(Span<byte> state, int offset, IReadOnlyList<ShotRecord> shots)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(state[offset..], checked((ushort)shots.Count));
        offset += 2;
        foreach (var shot in shots)
        {
            var record = state[offset..];
            BinaryPrimitives.WriteUInt32LittleEndian(record, shot.CharacterId);
            record[4] = shot.Slot;
            BinaryPrimitives.WriteUInt32LittleEndian(record[5..], shot.ShotCounter);
            BinaryPrimitives.WriteUInt32LittleEndian(record[9..], shot.ProjectileId);
            WriteVector(record[13..], shot.Direction);
            offset += ShotLength;
        }

        return offset;
    }

    private static int WriteHits(Span<byte> state, int offset, IReadOnlyList<HitRecord> hits)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(state[offset..], checked((ushort)hits.Count));
        offset += 2;
        foreach (var hit in hits)
        {
            var record = state[offset..];
            BinaryPrimitives.WriteUInt32LittleEndian(record, hit.ProjectileId);
            record[4] = hit.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(record[5..], hit.TargetId);
            record[9] = hit.Hitbox;
            WriteVector(record[10..], hit.Point);
            BinaryPrimitives.WriteSingleLittleEndian(record[22..], hit.T);
            offset += HitLength;
        }

        return offset;
    }

    private static int WriteSmallEvents(Span<byte> state, int offset, IReadOnlyList<SmallEventRecord> events)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(state[offset..], checked((ushort)events.Count));
        offset += 2;
        foreach (var smallEvent in events)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(state[offset..], smallEvent.Id);
            state[offset + 4] = smallEvent.Code;
            offset += SmallEventLength;
        }

        return offset;
    }

    private static void WriteVector(Span<byte> destination, (float X, float Y, float Z) vector)
    {
        BinaryPrimitives.WriteSingleLittleEndian(destination, vector.X);
        BinaryPrimitives.WriteSingleLittleEndian(destination[4..], vector.Y);
        BinaryPrimitives.WriteSingleLittleEndian(destination[8..], vector.Z);
    }
}

/// <summary>
/// A character record of contracts/state-serialization.md#character-record, as written by
/// <see cref="TestStates.Build"/>. The defaults are a character at rest at the origin: standing,
/// grounded on a level surface, yaw 0, pitch 0, slot 0 active, with the placeholder weapons.
/// </summary>
public sealed record CharacterRecord
{
    public uint Id { get; init; } = 1;

    /// <summary>The feet.</summary>
    public (float X, float Y, float Z) Position { get; init; }

    public (float X, float Y, float Z) Velocity { get; init; }

    public (float X, float Y, float Z) GroundNormal { get; init; } = (0f, 1f, 0f);

    public ushort AimYaw { get; init; }

    public short AimPitch { get; init; }

    /// <summary>0 Standing, 1 Crouched.</summary>
    public byte Stance { get; init; }

    /// <summary><see cref="TestStates.Grounded"/> and <see cref="TestStates.ClampedToBounds"/>.</summary>
    public byte Flags { get; init; } = TestStates.Grounded;

    public uint LastSequence { get; init; }

    public byte ActiveSlot { get; init; }

    /// <summary>The weapons in slot order; their count is written as <c>weaponCount</c>.</summary>
    public IReadOnlyList<WeaponRecord> Weapons { get; init; } = TestStates.PlaceholderWeapons;
}

/// <summary>
/// A weapon record of contracts/state-serialization.md#weapon-record. The defaults are an empty Ready
/// weapon that has never fired (<c>stepsSinceShot</c> 65535).
/// </summary>
public sealed record WeaponRecord
{
    /// <summary>0 Ready, 1 Firing, 2 Reloading, 3 Switching, 4 Overheated.</summary>
    public byte Phase { get; init; }

    /// <summary>Bit 0 TriggerLatched.</summary>
    public byte Flags { get; init; }

    public ushort PhaseSteps { get; init; }

    public int FireWait { get; init; }

    public uint ShotCounter { get; init; }

    public ushort Magazine { get; init; }

    public ushort Reserve { get; init; }

    public uint Heat { get; init; }

    public ushort StepsSinceShot { get; init; } = ushort.MaxValue;

    public int RecoilPitch { get; init; }

    public int RecoilYaw { get; init; }
}

/// <summary>A projectile record of contracts/state-serialization.md#projectile-record.</summary>
public sealed record ProjectileRecord
{
    public uint Id { get; init; } = 1;

    public uint OwnerId { get; init; } = 1;

    public byte Class { get; init; }

    public uint LaunchStep { get; init; }

    public (float X, float Y, float Z) Origin { get; init; }

    public (float X, float Y, float Z) LaunchVelocity { get; init; }

    public (float X, float Y, float Z) Position { get; init; }
}

/// <summary>A shot event (25 bytes) of contracts/state-serialization.md#step-events.</summary>
public sealed record ShotRecord(uint CharacterId, byte Slot, uint ShotCounter, uint ProjectileId, (float X, float Y, float Z) Direction);

/// <summary>
/// A hit event (26 bytes) of contracts/state-serialization.md#step-events. <c>Kind</c>: 0 Terrain,
/// 1 Mesh, 2 Character. <c>Hitbox</c>: 0 Head, 1 Torso, 2 Legs, 255 None.
/// </summary>
public sealed record HitRecord(uint ProjectileId, byte Kind, uint TargetId, byte Hitbox, (float X, float Y, float Z) Point, float T);

/// <summary>
/// A 5-byte event: an expiry (projectile ID and reason), an input rejection or a fire block (character
/// ID and reason), or a movement outcome (character ID and flags).
/// </summary>
public readonly record struct SmallEventRecord(uint Id, byte Code);

/// <summary>The six step event lists, in serialisation order. <see cref="None"/> is a loaded state's.</summary>
public sealed record EventRecords
{
    public static EventRecords None { get; } = new();

    public IReadOnlyList<ShotRecord> Shots { get; init; } = [];

    public IReadOnlyList<HitRecord> Hits { get; init; } = [];

    public IReadOnlyList<SmallEventRecord> Expiries { get; init; } = [];

    public IReadOnlyList<SmallEventRecord> InputRejections { get; init; } = [];

    public IReadOnlyList<SmallEventRecord> FireBlocks { get; init; } = [];

    public IReadOnlyList<SmallEventRecord> MovementOutcomes { get; init; } = [];
}
