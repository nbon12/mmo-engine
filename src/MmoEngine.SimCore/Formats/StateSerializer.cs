using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Formats;

/// <summary>The record a field of the canonical state belongs to (contracts/state-serialization.md#layout).</summary>
public enum StateRecordKind : byte
{
    /// <summary>The 12-byte header.</summary>
    Header = 0,

    /// <summary><c>characters[i]</c>, 52 bytes before its weapons.</summary>
    Character = 1,

    /// <summary><c>characters[i].weapons[s]</c>, 30 bytes.</summary>
    Weapon = 2,

    /// <summary><c>projectiles[j]</c>, 49 bytes.</summary>
    Projectile = 3,

    /// <summary><c>events.shots.count</c>.</summary>
    ShotCount = 4,

    /// <summary><c>events.shots[k]</c>, 25 bytes.</summary>
    Shot = 5,

    /// <summary><c>events.hits.count</c>.</summary>
    HitCount = 6,

    /// <summary><c>events.hits[k]</c>, 26 bytes.</summary>
    Hit = 7,

    /// <summary><c>events.expiries.count</c>.</summary>
    ExpiryCount = 8,

    /// <summary><c>events.expiries[k]</c>, 5 bytes.</summary>
    Expiry = 9,

    /// <summary><c>events.inputRejections.count</c>.</summary>
    InputRejectionCount = 10,

    /// <summary><c>events.inputRejections[k]</c>, 5 bytes.</summary>
    InputRejection = 11,

    /// <summary><c>events.fireBlocks.count</c>.</summary>
    FireBlockCount = 12,

    /// <summary><c>events.fireBlocks[k]</c>, 5 bytes.</summary>
    FireBlock = 13,

    /// <summary><c>events.movementOutcomes.count</c>.</summary>
    MovementOutcomeCount = 14,

    /// <summary><c>events.movementOutcomes[k]</c>, 5 bytes.</summary>
    MovementOutcome = 15,
}

/// <summary>The little-endian type of a field of the canonical state.</summary>
public enum StateFieldType : byte
{
    /// <summary><c>u8</c>.</summary>
    U8 = 0,

    /// <summary><c>u16 LE</c>.</summary>
    U16 = 1,

    /// <summary><c>i16 LE</c>.</summary>
    I16 = 2,

    /// <summary><c>u32 LE</c>.</summary>
    U32 = 3,

    /// <summary><c>i32 LE</c>.</summary>
    I32 = 4,

    /// <summary><c>f32 LE</c>, IEEE 754 binary32 bits.</summary>
    F32 = 5,
}

/// <summary>
/// One row of the canonical state's layout table (contracts/state-serialization.md#layout): its ordinal
/// (its index in <see cref="StateSerializer.Layout"/>), its record, its offset in the record, its size
/// and type, and its name within the record, such as <c>position.x</c> or <c>count</c>.
/// </summary>
public readonly struct StateField
{
    internal StateField(int ordinal, StateRecordKind record, int offset, StateFieldType type, string name)
    {
        Ordinal = ordinal;
        Record = record;
        Offset = offset;
        Type = type;
        Name = name;
        Size = type switch
        {
            StateFieldType.U8 => 1,
            StateFieldType.U16 or StateFieldType.I16 => 2,
            _ => 4,
        };
    }

    /// <summary>The field's index in <see cref="StateSerializer.Layout"/>, in layout order.</summary>
    public int Ordinal { get; }

    /// <summary>The record the field belongs to.</summary>
    public StateRecordKind Record { get; }

    /// <summary>The field's offset from its record's first byte.</summary>
    public int Offset { get; }

    /// <summary>The field's size in bytes.</summary>
    public int Size { get; }

    /// <summary>The field's type.</summary>
    public StateFieldType Type { get; }

    /// <summary>The field's name within its record, such as <c>groundNormal.y</c>.</summary>
    public string Name { get; }
}

/// <summary>
/// The canonical state, serialisation version 1 (FR-016, FR-017,
/// specs/015-shared-simulation-core/contracts/state-serialization.md). It holds the layout as data,
/// <see cref="Layout"/>, which the writer's fault paths, the readers' refusals and the divergence report
/// all name fields through, so they cannot disagree.
/// </summary>
/// <remarks>
/// A field is named by its ordinal and an element index: the element's position in its list in
/// serialisation order for a character, projectile or event field; the character index × 8 + the slot
/// for a weapon field (a character has at most 8 weapons); and 0 for a header or count field.
/// <see cref="Write"/> is the world's stage 6: it canonicalises, checks and serialises without allocating.
/// The readers allocate and are not on the tick path.
/// </remarks>
public static class StateSerializer
{
    /// <summary>The header: <c>step</c>, <c>characterCount</c>, <c>projectileCount</c>, <c>nextProjectileId</c>.</summary>
    public const int HeaderLength = 12;

    /// <summary>A character record before its weapons.</summary>
    public const int CharacterFixedLength = 52;

    /// <summary>A weapon record.</summary>
    public const int WeaponLength = 30;

    /// <summary>A projectile record.</summary>
    public const int ProjectileLength = 49;

    /// <summary>A shot event.</summary>
    public const int ShotLength = 25;

    /// <summary>A hit event.</summary>
    public const int HitLength = 26;

    /// <summary>An expiry, input rejection, fire block or movement outcome event.</summary>
    public const int SmallEventLength = 5;

    /// <summary>The six <c>u16</c> event counts.</summary>
    public const int EventCountsLength = 12;

    /// <summary>The most characters a state can hold, the largest <c>world.maxCharacters</c>.</summary>
    public const int MaxCharacterCount = 1024;

    /// <summary>The most projectiles a state can hold, the largest <c>world.maxProjectiles</c>.</summary>
    public const int MaxProjectileCount = 16384;

    /// <summary>The most weapons a character can have, the largest <c>input.weaponSlots</c>.</summary>
    public const int MaxWeaponCount = 8;

    /// <summary>The largest |coordinate| of a position or an origin, in metres.</summary>
    public const float MaxCoordinate = 8192f;

    /// <summary>The largest |aimPitch|, a quarter turn.</summary>
    public const int MaxAimPitch = 16384;

    // ---------------------------------------------------------------- field ordinals, in layout order

    internal const int StepField = 0;
    internal const int CharacterCountField = 1;
    internal const int ProjectileCountField = 2;
    internal const int NextProjectileIdField = 3;

    internal const int CharacterIdField = 4;
    internal const int PositionXField = 5;
    internal const int VelocityXField = 8;
    internal const int GroundNormalXField = 11;
    internal const int AimYawField = 14;
    internal const int AimPitchField = 15;
    internal const int StanceField = 16;
    internal const int CharacterFlagsField = 17;
    internal const int LastSequenceField = 18;
    internal const int ActiveSlotField = 19;
    internal const int WeaponCountField = 20;

    internal const int PhaseField = 21;
    internal const int WeaponFlagsField = 22;
    internal const int PhaseStepsField = 23;
    internal const int FireWaitField = 24;
    internal const int ShotCounterField = 25;
    internal const int MagazineField = 26;
    internal const int ReserveField = 27;
    internal const int HeatField = 28;
    internal const int StepsSinceShotField = 29;
    internal const int RecoilPitchField = 30;
    internal const int RecoilYawField = 31;

    internal const int ProjectileIdField = 32;
    internal const int OwnerIdField = 33;
    internal const int ClassField = 34;
    internal const int LaunchStepField = 35;
    internal const int OriginXField = 36;
    internal const int LaunchVelocityXField = 39;
    internal const int ProjectilePositionXField = 42;

    internal const int ShotCountField = 45;
    internal const int ShotCharacterIdField = 46;
    internal const int ShotSlotField = 47;
    internal const int ShotShotCounterField = 48;
    internal const int ShotProjectileIdField = 49;
    internal const int ShotDirectionXField = 50;

    internal const int HitCountField = 53;
    internal const int HitProjectileIdField = 54;
    internal const int HitKindField = 55;
    internal const int HitTargetIdField = 56;
    internal const int HitHitboxField = 57;
    internal const int HitPointXField = 58;
    internal const int HitTField = 61;

    internal const int ExpiryCountField = 62;
    internal const int ExpiryProjectileIdField = 63;
    internal const int ExpiryReasonField = 64;
    internal const int InputRejectionCountField = 65;
    internal const int InputRejectionCharacterIdField = 66;
    internal const int InputRejectionReasonField = 67;
    internal const int FireBlockCountField = 68;
    internal const int FireBlockCharacterIdField = 69;
    internal const int FireBlockReasonField = 70;
    internal const int MovementOutcomeCountField = 71;
    internal const int MovementOutcomeCharacterIdField = 72;
    internal const int MovementOutcomeFlagsField = 73;

    /// <summary>The path a refusal of trailing bytes names.</summary>
    internal const string LengthPath = "length";

    private const uint ExponentMask = 0x7F800000u;
    private const uint NegativeZeroBits = 0x80000000u;
    private const int NoFault = -1;
    private const byte CharacterFlagsMask = (byte)(CharacterFlags.Grounded | CharacterFlags.ClampedToBounds);
    private const byte WeaponFlagsMask = (byte)WeaponFlags.TriggerLatched;
    private const string LoadedStateHasEvents = "a loaded state has no events, found ";

    /// <summary>
    /// The layout table, one row per field in layout order (74 rows): the header, the character record,
    /// the weapon record, the projectile record, then each event list's count and record.
    /// </summary>
    public static ImmutableArray<StateField> Layout { get; } = BuildLayout();

    /// <summary>
    /// The longest state for the given maxima, <c>24 + c·(67 + 30·w + 25·s) + 80·p</c>: every character
    /// with its weapons, its shots and one each of the three 5-byte per-character events, and every
    /// projectile with a hit and an expiry (contracts/state-serialization.md#maximum-length).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">An argument is negative.</exception>
    public static int MaxLength(int maxCharacters, int maxProjectiles, int weaponSlots, int maxShotsPerStep)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(maxProjectiles);
        ArgumentOutOfRangeException.ThrowIfNegative(weaponSlots);
        ArgumentOutOfRangeException.ThrowIfNegative(maxShotsPerStep);
        var perCharacter = checked(CharacterFixedLength + (WeaponLength * weaponSlots) + (ShotLength * maxShotsPerStep) + (3 * SmallEventLength));
        var perProjectile = ProjectileLength + HitLength + SmallEventLength;
        return checked(HeaderLength + EventCountsLength + (maxCharacters * perCharacter) + (maxProjectiles * perProjectile));
    }

    /// <summary>
    /// <c>stateLength = 24 + c·(52 + 30·W) + 49·p + 25·shots + 26·hits + 5·(the four 5-byte lists)</c>.
    /// </summary>
    public static long Length(int characters, int weaponSlots, int projectiles, int shots, int hits, int expiries, int inputRejections, int fireBlocks, int movementOutcomes) =>
        HeaderLength + EventCountsLength
        + ((long)characters * (CharacterFixedLength + ((long)WeaponLength * weaponSlots)))
        + ((long)projectiles * ProjectileLength)
        + ((long)shots * ShotLength)
        + ((long)hits * HitLength)
        + ((long)SmallEventLength * ((long)expiries + inputRejections + fireBlocks + movementOutcomes));

    // ---------------------------------------------------------------- field paths

    /// <summary>
    /// The path of a field: <c>step</c>, <c>characters[3].velocity.y</c>,
    /// <c>characters[0].weapons[1].fireWait</c>, <c>projectiles[0].position.x</c>,
    /// <c>events.shots.count</c> or <c>events.hits[2].t</c>. <paramref name="elementIndex"/> is ignored
    /// for a header or count field; for a weapon field it is the character index × 8 + the slot.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The ordinal is not a row of <see cref="Layout"/>, or the index is negative.</exception>
    public static string FieldPath(int fieldOrdinal, int elementIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fieldOrdinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fieldOrdinal, Layout.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        var field = Layout[fieldOrdinal];
        var name = field.Name;
        return field.Record switch
        {
            StateRecordKind.Header => name,
            StateRecordKind.Character => string.Concat("characters[", Index(elementIndex), "].", name),
            StateRecordKind.Weapon => string.Concat(
                string.Concat("characters[", Index(elementIndex / MaxWeaponCount), "].weapons["),
                string.Concat(Index(elementIndex % MaxWeaponCount), "].", name)),
            StateRecordKind.Projectile => string.Concat("projectiles[", Index(elementIndex), "].", name),
            _ when IsCount(field.Record) => string.Concat("events.", ListName(field.Record), ".count"),
            _ => string.Concat(string.Concat("events.", ListName(field.Record), "["), Index(elementIndex), "].", name),
        };
    }

    /// <summary>
    /// Finds the field a path names, the inverse of <see cref="FieldPath"/>. False for a path that names no
    /// field, has an index that is not a canonical non-negative decimal, or a weapon slot of 8 or more.
    /// </summary>
    public static bool TryFindField(string path, out int fieldOrdinal, out int elementIndex)
    {
        fieldOrdinal = -1;
        elementIndex = 0;
        if (string.IsNullOrEmpty(path) || !TrySplitIndices(path, out var template, out var first, out var second, out var indexCount))
        {
            return false;
        }

        foreach (var field in Layout)
        {
            if (!string.Equals(Template(field), template, StringComparison.Ordinal))
            {
                continue;
            }

            switch (indexCount)
            {
                case 0:
                    elementIndex = 0;
                    break;
                case 1:
                    elementIndex = first;
                    break;
                default:
                    if (second >= MaxWeaponCount || first > (int.MaxValue - MaxWeaponCount) / MaxWeaponCount)
                    {
                        return false;
                    }

                    elementIndex = (first * MaxWeaponCount) + second;
                    break;
            }

            fieldOrdinal = field.Ordinal;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- writing

    /// <summary>
    /// Stage 6 of the world step: canonicalises, checks and serialises a state with its step's events, in
    /// layout order, into the start of <paramref name="destination"/>, which may be longer than the state
    /// (contracts/state-serialization.md#canonicalisation-and-the-finite-check). The weapons are flat by
    /// character index × <paramref name="weaponSlots"/> + slot, and every character's <c>weaponCount</c>
    /// is written as <paramref name="weaponSlots"/>.
    /// <para>
    /// Every float's bits <c>b</c> are tested in layout order: <c>(b &amp; 0x7F800000) == 0x7F800000</c>
    /// (NaN or ±infinity) stops the write and returns <see cref="StepFaultCode.NonFinite"/> naming that
    /// field; <c>0x80000000</c> (negative zero) is written as 0 and also rewritten to +0 in the source
    /// element. On success the fault's code is <see cref="StepFaultCode.None"/> and
    /// <paramref name="length"/> is the state's length; after a fault the buffer holds no state and
    /// <paramref name="length"/> is 0. It allocates nothing.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="weaponSlots"/> is outside [1, 8], or a list is longer than a <c>u16</c> count holds.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The weapons are not characters × <paramref name="weaponSlots"/>, or the destination is shorter than
    /// the state.
    /// </exception>
    public static StepFault Write(
        uint step,
        uint nextProjectileId,
        int weaponSlots,
        Span<CharacterState> characters,
        ReadOnlySpan<WeaponState> weapons,
        Span<Projectile> projectiles,
        Span<ShotEvent> shots,
        Span<ProjectileHit> hits,
        ReadOnlySpan<ExpiryEvent> expiries,
        ReadOnlySpan<InputRejectionEvent> inputRejections,
        ReadOnlySpan<FireBlockEvent> fireBlocks,
        ReadOnlySpan<MovementOutcomeEvent> movementOutcomes,
        Span<byte> destination,
        out int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(weaponSlots, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(weaponSlots, MaxWeaponCount);
        RequireCount(characters.Length, nameof(characters));
        RequireCount(projectiles.Length, nameof(projectiles));
        RequireCount(shots.Length, nameof(shots));
        RequireCount(hits.Length, nameof(hits));
        RequireCount(expiries.Length, nameof(expiries));
        RequireCount(inputRejections.Length, nameof(inputRejections));
        RequireCount(fireBlocks.Length, nameof(fireBlocks));
        RequireCount(movementOutcomes.Length, nameof(movementOutcomes));
        if (weapons.Length != characters.Length * weaponSlots)
        {
            throw new ArgumentException("The weapons must be characters × weaponSlots, flat by character index, then slot.", nameof(weapons));
        }

        var required = Length(
            characters.Length,
            weaponSlots,
            projectiles.Length,
            shots.Length,
            hits.Length,
            expiries.Length,
            inputRejections.Length,
            fireBlocks.Length,
            movementOutcomes.Length);
        if (destination.Length < required)
        {
            throw new ArgumentException("The destination is shorter than the state.", nameof(destination));
        }

        length = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(destination, step);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], (ushort)characters.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)projectiles.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], nextProjectileId);
        var offset = HeaderLength;

        for (var i = 0; i < characters.Length; i++)
        {
            ref var character = ref characters[i];
            var record = destination[offset..];
            BinaryPrimitives.WriteUInt32LittleEndian(record, character.Id);

            var vector = character.Position;
            var bad = WriteVector(record[4..], ref vector, PositionXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, i);
            }

            character.Position = vector;
            vector = character.Velocity;
            bad = WriteVector(record[16..], ref vector, VelocityXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, i);
            }

            character.Velocity = vector;
            vector = character.GroundNormal;
            bad = WriteVector(record[28..], ref vector, GroundNormalXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, i);
            }

            character.GroundNormal = vector;
            BinaryPrimitives.WriteUInt16LittleEndian(record[40..], character.AimYaw);
            BinaryPrimitives.WriteInt16LittleEndian(record[42..], character.AimPitch);
            record[44] = (byte)character.Stance;
            record[45] = (byte)character.Flags;
            BinaryPrimitives.WriteUInt32LittleEndian(record[46..], character.LastSequence);
            record[50] = character.ActiveSlot;
            record[51] = (byte)weaponSlots;

            var weaponRecord = CharacterFixedLength;
            var first = i * weaponSlots;
            for (var s = 0; s < weaponSlots; s++)
            {
                WriteWeapon(record[weaponRecord..], in weapons[first + s]);
                weaponRecord += WeaponLength;
            }

            offset += weaponRecord;
        }

        for (var j = 0; j < projectiles.Length; j++)
        {
            ref var projectile = ref projectiles[j];
            var record = destination[offset..];
            BinaryPrimitives.WriteUInt32LittleEndian(record, projectile.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], projectile.OwnerId);
            record[8] = projectile.Class;
            BinaryPrimitives.WriteUInt32LittleEndian(record[9..], projectile.LaunchStep);

            var vector = projectile.Origin;
            var bad = WriteVector(record[13..], ref vector, OriginXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, j);
            }

            projectile.Origin = vector;
            vector = projectile.LaunchVelocity;
            bad = WriteVector(record[25..], ref vector, LaunchVelocityXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, j);
            }

            projectile.LaunchVelocity = vector;
            vector = projectile.Position;
            bad = WriteVector(record[37..], ref vector, ProjectilePositionXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, j);
            }

            projectile.Position = vector;
            offset += ProjectileLength;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)shots.Length);
        offset += 2;
        for (var k = 0; k < shots.Length; k++)
        {
            ref var shot = ref shots[k];
            var record = destination[offset..];
            BinaryPrimitives.WriteUInt32LittleEndian(record, shot.CharacterId);
            record[4] = shot.Slot;
            BinaryPrimitives.WriteUInt32LittleEndian(record[5..], shot.ShotCounter);
            BinaryPrimitives.WriteUInt32LittleEndian(record[9..], shot.ProjectileId);
            var vector = shot.Direction;
            var bad = WriteVector(record[13..], ref vector, ShotDirectionXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, k);
            }

            shot.Direction = vector;
            offset += ShotLength;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)hits.Length);
        offset += 2;
        for (var k = 0; k < hits.Length; k++)
        {
            ref var hit = ref hits[k];
            var record = destination[offset..];
            BinaryPrimitives.WriteUInt32LittleEndian(record, hit.ProjectileId);
            record[4] = (byte)hit.Kind;
            BinaryPrimitives.WriteUInt32LittleEndian(record[5..], hit.TargetId);
            record[9] = (byte)hit.Hitbox;
            var vector = hit.Point;
            var bad = WriteVector(record[10..], ref vector, HitPointXField);
            if (bad != NoFault)
            {
                return new StepFault(StepFaultCode.NonFinite, bad, k);
            }

            hit.Point = vector;
            var t = BitConverter.SingleToUInt32Bits(hit.T);
            if (IsNonFinite(t))
            {
                return new StepFault(StepFaultCode.NonFinite, HitTField, k);
            }

            if (t == NegativeZeroBits)
            {
                t = 0u;
                hit.T = 0f;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(record[22..], t);
            offset += HitLength;
        }

        offset = WriteSmallEvents(destination, offset, expiries);
        offset = WriteSmallEvents(destination, offset, inputRejections);
        offset = WriteSmallEvents(destination, offset, fireBlocks);
        offset = WriteSmallEvents(destination, offset, movementOutcomes);
        length = offset;
        return default;
    }

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Reads a state being loaded (a <c>SimWorld.Reset</c> or a stream's initial state), running the
    /// byte-only checks of contracts/state-serialization.md#loading-a-state in layout order, the last of
    /// which is that all six event counts are 0. A refusal throws <see cref="CoreFormatException"/> with
    /// the context's format, the field's offset plus the context's base, and the field's path after the
    /// context's prefix. It allocates and is not on the tick path.
    /// </summary>
    /// <exception cref="CoreFormatException">The bytes break a byte-only rule.</exception>
    public static StateSnapshot ReadLoaded(ReadOnlySpan<byte> state, in StateErrorContext context)
    {
        var reader = new Reader(state, context, loaded: true);
        return reader.Read();
    }

    /// <summary>
    /// Reads a state <c>SimWorld.Step</c> committed, such as <c>StepReport.State</c>: the same byte-only
    /// checks in the same order, with format <c>state</c>, except that the event counts may be non-zero,
    /// and it decodes the six event lists (whose floats are checked as every float is). It allocates and
    /// is not on the tick path.
    /// </summary>
    /// <exception cref="CoreFormatException">The bytes break a byte-only rule.</exception>
    internal static StateSnapshot ReadCommitted(ReadOnlySpan<byte> state)
    {
        var reader = new Reader(state, StateErrorContext.State, loaded: false);
        return reader.Read();
    }

    // ---------------------------------------------------------------- the layout table

    private static ImmutableArray<StateField> BuildLayout()
    {
        var rows = new List<StateField>(74);

        void Add(int ordinal, StateRecordKind record, int offset, StateFieldType type, string name)
        {
            if (ordinal != rows.Count)
            {
                throw new InvalidOperationException($"State field {name} has ordinal {ordinal}, but is row {rows.Count} of the layout.");
            }

            rows.Add(new StateField(ordinal, record, offset, type, name));
        }

        void AddVector(int ordinal, StateRecordKind record, int offset, string name)
        {
            Add(ordinal, record, offset, StateFieldType.F32, name + ".x");
            Add(ordinal + 1, record, offset + 4, StateFieldType.F32, name + ".y");
            Add(ordinal + 2, record, offset + 8, StateFieldType.F32, name + ".z");
        }

        Add(StepField, StateRecordKind.Header, 0, StateFieldType.U32, "step");
        Add(CharacterCountField, StateRecordKind.Header, 4, StateFieldType.U16, "characterCount");
        Add(ProjectileCountField, StateRecordKind.Header, 6, StateFieldType.U16, "projectileCount");
        Add(NextProjectileIdField, StateRecordKind.Header, 8, StateFieldType.U32, "nextProjectileId");

        Add(CharacterIdField, StateRecordKind.Character, 0, StateFieldType.U32, "id");
        AddVector(PositionXField, StateRecordKind.Character, 4, "position");
        AddVector(VelocityXField, StateRecordKind.Character, 16, "velocity");
        AddVector(GroundNormalXField, StateRecordKind.Character, 28, "groundNormal");
        Add(AimYawField, StateRecordKind.Character, 40, StateFieldType.U16, "aimYaw");
        Add(AimPitchField, StateRecordKind.Character, 42, StateFieldType.I16, "aimPitch");
        Add(StanceField, StateRecordKind.Character, 44, StateFieldType.U8, "stance");
        Add(CharacterFlagsField, StateRecordKind.Character, 45, StateFieldType.U8, "flags");
        Add(LastSequenceField, StateRecordKind.Character, 46, StateFieldType.U32, "lastSequence");
        Add(ActiveSlotField, StateRecordKind.Character, 50, StateFieldType.U8, "activeSlot");
        Add(WeaponCountField, StateRecordKind.Character, 51, StateFieldType.U8, "weaponCount");

        Add(PhaseField, StateRecordKind.Weapon, 0, StateFieldType.U8, "phase");
        Add(WeaponFlagsField, StateRecordKind.Weapon, 1, StateFieldType.U8, "flags");
        Add(PhaseStepsField, StateRecordKind.Weapon, 2, StateFieldType.U16, "phaseSteps");
        Add(FireWaitField, StateRecordKind.Weapon, 4, StateFieldType.I32, "fireWait");
        Add(ShotCounterField, StateRecordKind.Weapon, 8, StateFieldType.U32, "shotCounter");
        Add(MagazineField, StateRecordKind.Weapon, 12, StateFieldType.U16, "magazine");
        Add(ReserveField, StateRecordKind.Weapon, 14, StateFieldType.U16, "reserve");
        Add(HeatField, StateRecordKind.Weapon, 16, StateFieldType.U32, "heat");
        Add(StepsSinceShotField, StateRecordKind.Weapon, 20, StateFieldType.U16, "stepsSinceShot");
        Add(RecoilPitchField, StateRecordKind.Weapon, 22, StateFieldType.I32, "recoilPitch");
        Add(RecoilYawField, StateRecordKind.Weapon, 26, StateFieldType.I32, "recoilYaw");

        Add(ProjectileIdField, StateRecordKind.Projectile, 0, StateFieldType.U32, "id");
        Add(OwnerIdField, StateRecordKind.Projectile, 4, StateFieldType.U32, "ownerId");
        Add(ClassField, StateRecordKind.Projectile, 8, StateFieldType.U8, "class");
        Add(LaunchStepField, StateRecordKind.Projectile, 9, StateFieldType.U32, "launchStep");
        AddVector(OriginXField, StateRecordKind.Projectile, 13, "origin");
        AddVector(LaunchVelocityXField, StateRecordKind.Projectile, 25, "launchVelocity");
        AddVector(ProjectilePositionXField, StateRecordKind.Projectile, 37, "position");

        Add(ShotCountField, StateRecordKind.ShotCount, 0, StateFieldType.U16, "count");
        Add(ShotCharacterIdField, StateRecordKind.Shot, 0, StateFieldType.U32, "characterId");
        Add(ShotSlotField, StateRecordKind.Shot, 4, StateFieldType.U8, "slot");
        Add(ShotShotCounterField, StateRecordKind.Shot, 5, StateFieldType.U32, "shotCounter");
        Add(ShotProjectileIdField, StateRecordKind.Shot, 9, StateFieldType.U32, "projectileId");
        AddVector(ShotDirectionXField, StateRecordKind.Shot, 13, "direction");

        Add(HitCountField, StateRecordKind.HitCount, 0, StateFieldType.U16, "count");
        Add(HitProjectileIdField, StateRecordKind.Hit, 0, StateFieldType.U32, "projectileId");
        Add(HitKindField, StateRecordKind.Hit, 4, StateFieldType.U8, "kind");
        Add(HitTargetIdField, StateRecordKind.Hit, 5, StateFieldType.U32, "targetId");
        Add(HitHitboxField, StateRecordKind.Hit, 9, StateFieldType.U8, "hitbox");
        AddVector(HitPointXField, StateRecordKind.Hit, 10, "point");
        Add(HitTField, StateRecordKind.Hit, 22, StateFieldType.F32, "t");

        Add(ExpiryCountField, StateRecordKind.ExpiryCount, 0, StateFieldType.U16, "count");
        Add(ExpiryProjectileIdField, StateRecordKind.Expiry, 0, StateFieldType.U32, "projectileId");
        Add(ExpiryReasonField, StateRecordKind.Expiry, 4, StateFieldType.U8, "reason");
        Add(InputRejectionCountField, StateRecordKind.InputRejectionCount, 0, StateFieldType.U16, "count");
        Add(InputRejectionCharacterIdField, StateRecordKind.InputRejection, 0, StateFieldType.U32, "characterId");
        Add(InputRejectionReasonField, StateRecordKind.InputRejection, 4, StateFieldType.U8, "reason");
        Add(FireBlockCountField, StateRecordKind.FireBlockCount, 0, StateFieldType.U16, "count");
        Add(FireBlockCharacterIdField, StateRecordKind.FireBlock, 0, StateFieldType.U32, "characterId");
        Add(FireBlockReasonField, StateRecordKind.FireBlock, 4, StateFieldType.U8, "reason");
        Add(MovementOutcomeCountField, StateRecordKind.MovementOutcomeCount, 0, StateFieldType.U16, "count");
        Add(MovementOutcomeCharacterIdField, StateRecordKind.MovementOutcome, 0, StateFieldType.U32, "characterId");
        Add(MovementOutcomeFlagsField, StateRecordKind.MovementOutcome, 4, StateFieldType.U8, "flags");

        return [.. rows];
    }

    private static bool IsCount(StateRecordKind record) => record is StateRecordKind.ShotCount
        or StateRecordKind.HitCount
        or StateRecordKind.ExpiryCount
        or StateRecordKind.InputRejectionCount
        or StateRecordKind.FireBlockCount
        or StateRecordKind.MovementOutcomeCount;

    private static string ListName(StateRecordKind record) => record switch
    {
        StateRecordKind.ShotCount or StateRecordKind.Shot => "shots",
        StateRecordKind.HitCount or StateRecordKind.Hit => "hits",
        StateRecordKind.ExpiryCount or StateRecordKind.Expiry => "expiries",
        StateRecordKind.InputRejectionCount or StateRecordKind.InputRejection => "inputRejections",
        StateRecordKind.FireBlockCount or StateRecordKind.FireBlock => "fireBlocks",
        StateRecordKind.MovementOutcomeCount or StateRecordKind.MovementOutcome => "movementOutcomes",
        _ => throw new ArgumentOutOfRangeException(nameof(record), record, "Not an event list."),
    };

    /// <summary>The path with every index left out, such as <c>characters[].weapons[].phase</c>.</summary>
    private static string Template(StateField field) => field.Record switch
    {
        StateRecordKind.Header => field.Name,
        StateRecordKind.Character => "characters[]." + field.Name,
        StateRecordKind.Weapon => "characters[].weapons[]." + field.Name,
        StateRecordKind.Projectile => "projectiles[]." + field.Name,
        _ when IsCount(field.Record) => string.Concat("events.", ListName(field.Record), ".count"),
        _ => string.Concat("events.", ListName(field.Record), "[].", field.Name),
    };

    /// <summary>
    /// Splits a path into its template (every <c>[n]</c> written <c>[]</c>) and at most two indices, each a
    /// non-negative decimal int with no sign and no leading zero.
    /// </summary>
    private static bool TrySplitIndices(string path, out string template, out int first, out int second, out int count)
    {
        template = string.Empty;
        first = 0;
        second = 0;
        count = 0;
        var builder = new System.Text.StringBuilder(path.Length);
        var i = 0;
        while (i < path.Length)
        {
            var c = path[i];
            if (c != '[')
            {
                builder.Append(c);
                i++;
                continue;
            }

            var close = path.IndexOf(']', i + 1);
            if (close < 0 || count == 2)
            {
                return false;
            }

            var digits = path.AsSpan(i + 1, close - i - 1);
            if (digits.Length == 0
                || (digits.Length > 1 && digits[0] == '0')
                || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                return false;
            }

            if (count == 0)
            {
                first = index;
            }
            else
            {
                second = index;
            }

            count++;
            builder.Append("[]");
            i = close + 1;
        }

        template = builder.ToString();
        return true;
    }

    private static string Index(int index) => index.ToString(CultureInfo.InvariantCulture);

    private static void RequireCount(int count, string name)
    {
        if (count > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, count, "A state list holds at most 65535 elements, its u16 count.");
        }
    }

    private static bool IsNonFinite(uint bits) => (bits & ExponentMask) == ExponentMask;

    /// <summary>
    /// Writes a vector's three floats, rewriting negative zero to +0 in the bytes and in
    /// <paramref name="value"/>. Returns the ordinal of the first non-finite component, writing nothing,
    /// or <see cref="NoFault"/>.
    /// </summary>
    private static int WriteVector(Span<byte> destination, ref Vec3 value, int firstOrdinal)
    {
        var x = BitConverter.SingleToUInt32Bits(value.X);
        var y = BitConverter.SingleToUInt32Bits(value.Y);
        var z = BitConverter.SingleToUInt32Bits(value.Z);
        if (IsNonFinite(x))
        {
            return firstOrdinal;
        }

        if (IsNonFinite(y))
        {
            return firstOrdinal + 1;
        }

        if (IsNonFinite(z))
        {
            return firstOrdinal + 2;
        }

        if (x == NegativeZeroBits || y == NegativeZeroBits || z == NegativeZeroBits)
        {
            x = x == NegativeZeroBits ? 0u : x;
            y = y == NegativeZeroBits ? 0u : y;
            z = z == NegativeZeroBits ? 0u : z;
            value = new Vec3(BitConverter.UInt32BitsToSingle(x), BitConverter.UInt32BitsToSingle(y), BitConverter.UInt32BitsToSingle(z));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, x);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], y);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], z);
        return NoFault;
    }

    private static void WriteWeapon(Span<byte> record, in WeaponState weapon)
    {
        record[0] = (byte)weapon.Phase;
        record[1] = (byte)weapon.Flags;
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

    private static int WriteSmallEvents(Span<byte> destination, int offset, ReadOnlySpan<ExpiryEvent> events)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)events.Length);
        offset += 2;
        foreach (ref readonly var e in events)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], e.ProjectileId);
            destination[offset + 4] = (byte)e.Reason;
            offset += SmallEventLength;
        }

        return offset;
    }

    private static int WriteSmallEvents(Span<byte> destination, int offset, ReadOnlySpan<InputRejectionEvent> events)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)events.Length);
        offset += 2;
        foreach (ref readonly var e in events)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], e.CharacterId);
            destination[offset + 4] = (byte)e.Reason;
            offset += SmallEventLength;
        }

        return offset;
    }

    private static int WriteSmallEvents(Span<byte> destination, int offset, ReadOnlySpan<FireBlockEvent> events)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)events.Length);
        offset += 2;
        foreach (ref readonly var e in events)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], e.CharacterId);
            destination[offset + 4] = (byte)e.Reason;
            offset += SmallEventLength;
        }

        return offset;
    }

    private static int WriteSmallEvents(Span<byte> destination, int offset, ReadOnlySpan<MovementOutcomeEvent> events)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination[offset..], (ushort)events.Length);
        offset += 2;
        foreach (ref readonly var e in events)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[offset..], e.CharacterId);
            destination[offset + 4] = (byte)e.Flags;
            offset += SmallEventLength;
        }

        return offset;
    }

    /// <summary>
    /// The readers' cursor: it reads the fields in layout order, each through its row of
    /// <see cref="Layout"/>, and refuses at the first field that breaks a byte-only rule.
    /// </summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private readonly StateErrorContext _context;
        private readonly bool _loaded;
        private int _position;

        public Reader(ReadOnlySpan<byte> bytes, in StateErrorContext context, bool loaded)
        {
            _bytes = bytes;
            _context = context;
            _loaded = loaded;
            _position = 0;
        }

        public StateSnapshot Read()
        {
            if (_bytes.Length < HeaderLength)
            {
                throw Error(FormatErrorCode.Truncated, StepField, 0, 0, FormatDetail.ExpectedLength, HeaderLength, (ulong)_bytes.Length);
            }

            var step = U32(StepField, 0);
            var characterCount = U16(CharacterCountField, 0);
            if (characterCount > MaxCharacterCount)
            {
                throw Error(FormatErrorCode.Oversized, CharacterCountField, 0, 4, Above(characterCount, MaxCharacterCount));
            }

            var projectileCount = U16(ProjectileCountField, 0);
            if (projectileCount > MaxProjectileCount)
            {
                throw Error(FormatErrorCode.Oversized, ProjectileCountField, 0, 6, Above(projectileCount, MaxProjectileCount));
            }

            var nextProjectileId = U32(NextProjectileIdField, 0);
            if (nextProjectileId < 1)
            {
                throw Error(FormatErrorCode.Inconsistent, NextProjectileIdField, 0, 8, "0 is below 1");
            }

            var characters = ImmutableArray.CreateBuilder<CharacterState>(characterCount);
            var weapons = ImmutableArray.CreateBuilder<WeaponState>();
            for (var i = 0; i < characterCount; i++)
            {
                characters.Add(ReadCharacter(i, i == 0 ? null : characters[i - 1].Id, weapons));
            }

            var projectiles = ImmutableArray.CreateBuilder<Projectile>(projectileCount);
            for (var j = 0; j < projectileCount; j++)
            {
                projectiles.Add(ReadProjectile(j, j == 0 ? 0u : projectiles[j - 1].Id, step, nextProjectileId));
            }

            var shots = ImmutableArray.CreateBuilder<ShotEvent>(Capacity(ShotCountField, ShotLength, out var shotCount));
            for (var k = 0; k < shotCount; k++)
            {
                shots.Add(new ShotEvent
                {
                    CharacterId = U32(ShotCharacterIdField, k),
                    Slot = U8(ShotSlotField, k),
                    ShotCounter = U32(ShotShotCounterField, k),
                    ProjectileId = U32(ShotProjectileIdField, k),
                    Direction = Vector(ShotDirectionXField, k, ranged: false),
                });
            }

            var hits = ImmutableArray.CreateBuilder<ProjectileHit>(Capacity(HitCountField, HitLength, out var hitCount));
            for (var k = 0; k < hitCount; k++)
            {
                hits.Add(new ProjectileHit
                {
                    ProjectileId = U32(HitProjectileIdField, k),
                    Kind = (HitKind)U8(HitKindField, k),
                    TargetId = U32(HitTargetIdField, k),
                    Hitbox = (HitboxPart)U8(HitHitboxField, k),
                    Point = Vector(HitPointXField, k, ranged: false),
                    T = Float(HitTField, k, ranged: false),
                });
            }

            var expiries = ImmutableArray.CreateBuilder<ExpiryEvent>(Capacity(ExpiryCountField, SmallEventLength, out var expiryCount));
            for (var k = 0; k < expiryCount; k++)
            {
                expiries.Add(new ExpiryEvent { ProjectileId = U32(ExpiryProjectileIdField, k), Reason = (ExpiryReason)U8(ExpiryReasonField, k) });
            }

            var rejections = ImmutableArray.CreateBuilder<InputRejectionEvent>(Capacity(InputRejectionCountField, SmallEventLength, out var rejectionCount));
            for (var k = 0; k < rejectionCount; k++)
            {
                rejections.Add(new InputRejectionEvent
                {
                    CharacterId = U32(InputRejectionCharacterIdField, k),
                    Reason = (InputRejection)U8(InputRejectionReasonField, k),
                });
            }

            var fireBlocks = ImmutableArray.CreateBuilder<FireBlockEvent>(Capacity(FireBlockCountField, SmallEventLength, out var fireBlockCount));
            for (var k = 0; k < fireBlockCount; k++)
            {
                fireBlocks.Add(new FireBlockEvent { CharacterId = U32(FireBlockCharacterIdField, k), Reason = (FireBlock)U8(FireBlockReasonField, k) });
            }

            var outcomes = ImmutableArray.CreateBuilder<MovementOutcomeEvent>(Capacity(MovementOutcomeCountField, SmallEventLength, out var outcomeCount));
            for (var k = 0; k < outcomeCount; k++)
            {
                outcomes.Add(new MovementOutcomeEvent
                {
                    CharacterId = U32(MovementOutcomeCharacterIdField, k),
                    Flags = (MovementOutcome)U8(MovementOutcomeFlagsField, k),
                });
            }

            if (_position != _bytes.Length)
            {
                throw new CoreFormatException(new FormatError(
                    _context.Format,
                    FormatErrorCode.Oversized,
                    _context.BaseOffset + _position,
                    _context.FieldPrefix + LengthPath,
                    FormatDetail.ExpectedLength,
                    (ulong)_position,
                    (ulong)_bytes.Length));
            }

            return new StateSnapshot(
                step,
                nextProjectileId,
                characters.DrainToImmutable(),
                weapons.DrainToImmutable(),
                projectiles.DrainToImmutable(),
                shots.DrainToImmutable(),
                hits.DrainToImmutable(),
                expiries.DrainToImmutable(),
                rejections.DrainToImmutable(),
                fireBlocks.DrainToImmutable(),
                outcomes.DrainToImmutable());
        }

        private static string Above(long value, long limit) =>
            string.Create(CultureInfo.InvariantCulture, $"{value} is above {limit}");

        private CharacterState ReadCharacter(int i, uint? previousId, ImmutableArray<WeaponState>.Builder weapons)
        {
            var idOffset = _position;
            var id = U32(CharacterIdField, i);
            if (previousId is { } previous && id <= previous)
            {
                throw Error(FormatErrorCode.Inconsistent, CharacterIdField, i, idOffset, string.Create(CultureInfo.InvariantCulture, $"{id} is not above {previous}"));
            }

            var position = Vector(PositionXField, i, ranged: true);
            var velocity = Vector(VelocityXField, i, ranged: false);
            var groundNormal = Vector(GroundNormalXField, i, ranged: false);
            var aimYaw = U16(AimYawField, i);

            var pitchOffset = _position;
            var aimPitch = I16(AimPitchField, i);
            if (aimPitch is < -MaxAimPitch or > MaxAimPitch)
            {
                throw Error(FormatErrorCode.OutOfRange, AimPitchField, i, pitchOffset, string.Create(CultureInfo.InvariantCulture, $"{aimPitch} is outside [-{MaxAimPitch}, {MaxAimPitch}]"));
            }

            var stanceOffset = _position;
            var stance = U8(StanceField, i);
            if (stance > (byte)Stance.Crouched)
            {
                throw Error(FormatErrorCode.OutOfRange, StanceField, i, stanceOffset, string.Create(CultureInfo.InvariantCulture, $"{stance} is outside [0, 1]"));
            }

            var flagsOffset = _position;
            var flags = U8(CharacterFlagsField, i);
            if ((flags & ~CharacterFlagsMask) != 0)
            {
                throw Error(FormatErrorCode.OutOfRange, CharacterFlagsField, i, flagsOffset, ReservedBits(flags, CharacterFlagsMask));
            }

            var lastSequence = U32(LastSequenceField, i);
            var activeSlotOffset = _position;
            var activeSlot = U8(ActiveSlotField, i);
            var weaponCountOffset = _position;
            var weaponCount = U8(WeaponCountField, i);
            if (weaponCount > MaxWeaponCount)
            {
                throw Error(FormatErrorCode.Oversized, WeaponCountField, i, weaponCountOffset, Above(weaponCount, MaxWeaponCount));
            }

            if (activeSlot >= weaponCount)
            {
                throw Error(FormatErrorCode.OutOfRange, ActiveSlotField, i, activeSlotOffset, string.Create(CultureInfo.InvariantCulture, $"{activeSlot} is not below weaponCount {weaponCount}"));
            }

            for (var s = 0; s < weaponCount; s++)
            {
                weapons.Add(ReadWeapon((i * MaxWeaponCount) + s));
            }

            return new CharacterState
            {
                Id = id,
                Position = position,
                Velocity = velocity,
                GroundNormal = groundNormal,
                AimYaw = aimYaw,
                AimPitch = aimPitch,
                Stance = (Stance)stance,
                Flags = (CharacterFlags)flags,
                LastSequence = lastSequence,
                ActiveSlot = activeSlot,
                WeaponCount = weaponCount,
            };
        }

        private WeaponState ReadWeapon(int index)
        {
            var phaseOffset = _position;
            var phase = U8(PhaseField, index);
            if (phase > (byte)WeaponPhase.Overheated)
            {
                throw Error(FormatErrorCode.OutOfRange, PhaseField, index, phaseOffset, string.Create(CultureInfo.InvariantCulture, $"{phase} is outside [0, 4]"));
            }

            var flagsOffset = _position;
            var flags = U8(WeaponFlagsField, index);
            if ((flags & ~WeaponFlagsMask) != 0)
            {
                throw Error(FormatErrorCode.OutOfRange, WeaponFlagsField, index, flagsOffset, ReservedBits(flags, WeaponFlagsMask));
            }

            return new WeaponState
            {
                Phase = (WeaponPhase)phase,
                Flags = (WeaponFlags)flags,
                PhaseSteps = U16(PhaseStepsField, index),
                FireWait = I32(FireWaitField, index),
                ShotCounter = U32(ShotCounterField, index),
                Magazine = U16(MagazineField, index),
                Reserve = U16(ReserveField, index),
                Heat = U32(HeatField, index),
                StepsSinceShot = U16(StepsSinceShotField, index),
                RecoilPitch = I32(RecoilPitchField, index),
                RecoilYaw = I32(RecoilYawField, index),
            };
        }

        private Projectile ReadProjectile(int j, uint previousId, uint step, uint nextProjectileId)
        {
            var idOffset = _position;
            var id = U32(ProjectileIdField, j);
            if (id == 0)
            {
                throw Error(FormatErrorCode.Inconsistent, ProjectileIdField, j, idOffset, "0 is below 1");
            }

            if (id <= previousId)
            {
                throw Error(FormatErrorCode.Inconsistent, ProjectileIdField, j, idOffset, string.Create(CultureInfo.InvariantCulture, $"{id} is not above {previousId}"));
            }

            if (id >= nextProjectileId)
            {
                throw Error(FormatErrorCode.Inconsistent, NextProjectileIdField, 0, 8, string.Create(CultureInfo.InvariantCulture, $"{nextProjectileId} is not above projectile ID {id}"));
            }

            var ownerId = U32(OwnerIdField, j);
            var projectileClass = U8(ClassField, j);
            var launchOffset = _position;
            var launchStep = U32(LaunchStepField, j);
            if (launchStep > step)
            {
                throw Error(FormatErrorCode.OutOfRange, LaunchStepField, j, launchOffset, string.Create(CultureInfo.InvariantCulture, $"{launchStep} is after step {step}"));
            }

            return new Projectile
            {
                Id = id,
                OwnerId = ownerId,
                Class = projectileClass,
                LaunchStep = launchStep,
                Origin = Vector(OriginXField, j, ranged: true),
                LaunchVelocity = Vector(LaunchVelocityXField, j, ranged: false),
                Position = Vector(ProjectilePositionXField, j, ranged: true),
            };
        }

        /// <summary>
        /// Reads an event count. A loaded state refuses any count but 0; otherwise the capacity to reserve
        /// is the count, capped at what the remaining bytes can hold, so nothing is allocated for records
        /// that are not there.
        /// </summary>
        private int Capacity(int countOrdinal, int recordLength, out int count)
        {
            var countOffset = _position;
            count = U16(countOrdinal, 0);
            if (_loaded && count != 0)
            {
                throw Error(FormatErrorCode.Inconsistent, countOrdinal, 0, countOffset, LoadedStateHasEvents + count.ToString(CultureInfo.InvariantCulture));
            }

            var fits = (_bytes.Length - _position) / recordLength;
            return count < fits ? count : fits;
        }

        private static string ReservedBits(byte value, byte mask) =>
            string.Create(CultureInfo.InvariantCulture, $"0x{value:X2} sets a reserved bit; only 0x{mask:X2} is defined");

        private Vec3 Vector(int firstOrdinal, int index, bool ranged)
        {
            var x = Float(firstOrdinal, index, ranged);
            var y = Float(firstOrdinal + 1, index, ranged);
            var z = Float(firstOrdinal + 2, index, ranged);
            return new Vec3(x, y, z);
        }

        /// <summary>A float, refused when not finite, when negative zero, and, for a coordinate, outside ±8192.</summary>
        private float Float(int ordinal, int index, bool ranged)
        {
            var offset = _position;
            var bits = U32(ordinal, index);
            if (IsNonFinite(bits))
            {
                throw Error(FormatErrorCode.NonFinite, ordinal, index, offset, FormatDetail.NotFinite, bits);
            }

            if (bits == NegativeZeroBits)
            {
                throw Error(FormatErrorCode.OutOfRange, ordinal, index, offset, "-0 is negative zero, which a canonical state never holds");
            }

            var value = BitConverter.UInt32BitsToSingle(bits);
            if (ranged && (value < -MaxCoordinate || value > MaxCoordinate))
            {
                throw Error(
                    FormatErrorCode.OutOfRange,
                    ordinal,
                    index,
                    offset,
                    FormatDetail.OutOfRangeFloat,
                    bits,
                    BitConverter.SingleToUInt32Bits(-MaxCoordinate),
                    BitConverter.SingleToUInt32Bits(MaxCoordinate));
            }

            return value;
        }

        private byte U8(int ordinal, int index)
        {
            Need(1, ordinal, index);
            var value = _bytes[_position];
            _position += 1;
            return value;
        }

        private ushort U16(int ordinal, int index)
        {
            Need(2, ordinal, index);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes[_position..]);
            _position += 2;
            return value;
        }

        private short I16(int ordinal, int index)
        {
            Need(2, ordinal, index);
            var value = BinaryPrimitives.ReadInt16LittleEndian(_bytes[_position..]);
            _position += 2;
            return value;
        }

        private uint U32(int ordinal, int index)
        {
            Need(4, ordinal, index);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_position..]);
            _position += 4;
            return value;
        }

        private int I32(int ordinal, int index)
        {
            Need(4, ordinal, index);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes[_position..]);
            _position += 4;
            return value;
        }

        /// <summary>Refuses as <c>Truncated</c>, naming the field, when it runs past the end.</summary>
        private readonly void Need(int size, int ordinal, int index)
        {
            if (_bytes.Length - _position < size)
            {
                throw Error(FormatErrorCode.Truncated, ordinal, index, _position, FormatDetail.ExpectedLength, (ulong)(_position + size), (ulong)_bytes.Length);
            }
        }

        private readonly CoreFormatException Error(FormatErrorCode code, int ordinal, int index, int offset, string detail) =>
            new(new FormatError(_context.Format, code, _context.BaseOffset + offset, _context.FieldPrefix + FieldPath(ordinal, index), detail));

        private readonly CoreFormatException Error(FormatErrorCode code, int ordinal, int index, int offset, FormatDetail detail, ulong a = 0, ulong b = 0, ulong c = 0) =>
            new(new FormatError(_context.Format, code, _context.BaseOffset + offset, _context.FieldPrefix + FieldPath(ordinal, index), detail, a, b, c));
    }
}
