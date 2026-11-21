using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// One field read from a canonical state by <see cref="StateFieldReader"/>: its layout row (which gives its
/// record kind, type and name), its path, its bits zero-extended to 32 and its offset in the state.
/// </summary>
public readonly struct StateFieldValue
{
    internal StateFieldValue(StateField field, string path, uint bits, int offset, StateFieldKey key)
    {
        Field = field;
        Path = path;
        Bits = bits;
        Offset = offset;
        Key = key;
    }

    /// <summary>The field's row of <see cref="StateSerializer.Layout"/>.</summary>
    public StateField Field { get; }

    /// <summary>The field's kind: its little-endian type, which decides how its decimal is shown.</summary>
    public StateFieldType Type => Field.Type;

    /// <summary>The field's path, such as <c>characters[0].weapons[1].fireWait</c> (contracts/state-serialization.md#field-paths).</summary>
    public string Path { get; }

    /// <summary>The field's own bits, zero-extended to 32: an <c>i16</c> of −1 is <c>0x0000FFFF</c>.</summary>
    public uint Bits { get; }

    /// <summary>The field's offset from the state's first byte.</summary>
    public int Offset { get; }

    /// <summary>Where the field sorts in layout order, matching list elements by index.</summary>
    internal StateFieldKey Key { get; }
}

/// <summary>
/// What <see cref="StateFieldReader.Read"/> found in a state: every field it could read whole, in layout
/// order, and where it stopped when it could not read the state to its end.
/// </summary>
public sealed class StateFieldReading
{
    internal StateFieldReading(ImmutableArray<StateFieldValue> fields, int? unreadableOffset, StateFieldKey stop)
    {
        Fields = fields;
        UnreadableOffset = unreadableOffset;
        Stop = stop;
    }

    /// <summary>The fields read, in layout order.</summary>
    public ImmutableArray<StateFieldValue> Fields { get; }

    /// <summary>
    /// The offset of the first field that cannot be read whole, or of the first byte after the last event
    /// list when bytes are left over; null when the layout reads the state exactly to its end.
    /// </summary>
    public int? UnreadableOffset { get; }

    /// <summary>
    /// The layout position of the field that could not be read; <see cref="StateFieldKey.End"/> when every
    /// field of the layout was read (with or without bytes left over).
    /// </summary>
    internal StateFieldKey Stop { get; }
}

/// <summary>
/// Reads a canonical state field by field for the divergence report (FR-026,
/// contracts/state-serialization.md#divergence-report): the header; each character with its own
/// <c>weaponCount</c> weapons; the projectiles; and the six event lists, each its count and its records. It
/// walks <see cref="StateSerializer.Layout"/>, so it cannot disagree with the writer, and checks no value,
/// so a broken state from a buggy build is still read up to the first field that is not there whole.
/// </summary>
/// <remarks>Server-only: it allocates a path per field and is never on the tick path.</remarks>
public static class StateFieldReader
{
    /// <summary>A <c>u16</c> event count.</summary>
    private const int CountLength = 2;

    /// <summary>A character record's <c>weaponCount</c>, from the record's first byte.</summary>
    private const int WeaponCountOffset = 51;

    /// <summary>The event lists in layout order: each list's count kind, its record kind and its record length.</summary>
    private static readonly (StateRecordKind Count, StateRecordKind Record, int Length)[] EventLists =
    [
        (StateRecordKind.ShotCount, StateRecordKind.Shot, StateSerializer.ShotLength),
        (StateRecordKind.HitCount, StateRecordKind.Hit, StateSerializer.HitLength),
        (StateRecordKind.ExpiryCount, StateRecordKind.Expiry, StateSerializer.SmallEventLength),
        (StateRecordKind.InputRejectionCount, StateRecordKind.InputRejection, StateSerializer.SmallEventLength),
        (StateRecordKind.FireBlockCount, StateRecordKind.FireBlock, StateSerializer.SmallEventLength),
        (StateRecordKind.MovementOutcomeCount, StateRecordKind.MovementOutcome, StateSerializer.SmallEventLength),
    ];

    /// <summary>The layout's rows of each record kind, in layout order, indexed by the kind's value.</summary>
    private static readonly StateField[][] RowsByKind = BuildRowsByKind();

    /// <summary>
    /// Reads <paramref name="state"/> in layout order with no value checks: every field that is there whole,
    /// with its path, type, bits and offset, and the offset of the first field that is not (or of the bytes
    /// left after the last event list).
    /// </summary>
    public static StateFieldReading Read(ReadOnlySpan<byte> state)
    {
        var walk = new Walk(state);
        if (!walk.Record(StateRecordKind.Header, StateFieldKey.HeaderGroup, 0, 0, 0))
        {
            return walk.Result();
        }

        int characterCount = BinaryPrimitives.ReadUInt16LittleEndian(state[4..]);
        int projectileCount = BinaryPrimitives.ReadUInt16LittleEndian(state[6..]);
        walk.Advance(StateSerializer.HeaderLength);

        for (var i = 0; i < characterCount; i++)
        {
            if (!walk.Record(StateRecordKind.Character, StateFieldKey.CharacterGroup, i, 0, i))
            {
                return walk.Result();
            }

            int weaponCount = state[walk.Offset + WeaponCountOffset];
            walk.Advance(StateSerializer.CharacterFixedLength);
            for (var s = 0; s < weaponCount; s++)
            {
                if (!walk.Record(StateRecordKind.Weapon, StateFieldKey.CharacterGroup, i, s + 1, i, s))
                {
                    return walk.Result();
                }

                walk.Advance(StateSerializer.WeaponLength);
            }
        }

        for (var j = 0; j < projectileCount; j++)
        {
            if (!walk.Record(StateRecordKind.Projectile, StateFieldKey.ProjectileGroup, j, 0, j))
            {
                return walk.Result();
            }

            walk.Advance(StateSerializer.ProjectileLength);
        }

        for (var list = 0; list < EventLists.Length; list++)
        {
            var (countKind, recordKind, length) = EventLists[list];
            var group = StateFieldKey.FirstEventGroup + list;
            if (!walk.Record(countKind, group, 0, 0, 0))
            {
                return walk.Result();
            }

            int count = BinaryPrimitives.ReadUInt16LittleEndian(state[walk.Offset..]);
            walk.Advance(CountLength);
            for (var k = 0; k < count; k++)
            {
                if (!walk.Record(recordKind, group, k + 1, 0, k))
                {
                    return walk.Result();
                }

                walk.Advance(length);
            }
        }

        return walk.Finish();
    }

    /// <summary>A field's path; a weapon's is built here when its slot is past the eight a path index can carry.</summary>
    private static string PathOf(StateField field, int elementIndex, int slot) => field.Record switch
    {
        StateRecordKind.Weapon when slot >= StateSerializer.MaxWeaponCount => string.Concat(
            string.Create(CultureInfo.InvariantCulture, $"characters[{elementIndex}].weapons[{slot}]."),
            field.Name),
        StateRecordKind.Weapon => StateSerializer.FieldPath(field.Ordinal, (elementIndex * StateSerializer.MaxWeaponCount) + slot),
        _ => StateSerializer.FieldPath(field.Ordinal, elementIndex),
    };

    private static uint ReadBits(ReadOnlySpan<byte> bytes, StateFieldType type) => type switch
    {
        StateFieldType.U8 => bytes[0],
        StateFieldType.U16 or StateFieldType.I16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
    };

    private static StateField[][] BuildRowsByKind()
    {
        var lists = new List<StateField>[(int)StateRecordKind.MovementOutcome + 1];
        for (var kind = 0; kind < lists.Length; kind++)
        {
            lists[kind] = [];
        }

        foreach (var field in StateSerializer.Layout)
        {
            lists[(int)field.Record].Add(field);
        }

        var rows = new StateField[lists.Length][];
        for (var kind = 0; kind < lists.Length; kind++)
        {
            rows[kind] = [.. lists[kind]];
        }

        return rows;
    }

    /// <summary>The walk's position and what it has read so far.</summary>
    private ref struct Walk
    {
        private readonly ReadOnlySpan<byte> _state;
        private readonly ImmutableArray<StateFieldValue>.Builder _fields;
        private int? _unreadable;
        private StateFieldKey _stop;

        public Walk(ReadOnlySpan<byte> state)
        {
            _state = state;
            _fields = ImmutableArray.CreateBuilder<StateFieldValue>();
            _unreadable = null;
            _stop = StateFieldKey.End;
            Offset = 0;
        }

        /// <summary>The first byte of the record being read.</summary>
        public int Offset { get; private set; }

        public void Advance(int length) => Offset += length;

        /// <summary>
        /// Reads every field of a <paramref name="kind"/> record at <see cref="Offset"/>, keyed by
        /// (<paramref name="group"/>, <paramref name="element"/>, <paramref name="sub"/>) and named with
        /// <paramref name="elementIndex"/> and <paramref name="slot"/>. False, with the walk stopped at the
        /// first field that is not there whole, when the record is cut short.
        /// </summary>
        public bool Record(StateRecordKind kind, int group, int element, int sub, int elementIndex, int slot = 0)
        {
            foreach (var field in RowsByKind[(int)kind])
            {
                var offset = Offset + field.Offset;
                var key = new StateFieldKey(group, element, sub, field.Ordinal);
                if (offset > _state.Length - field.Size)
                {
                    _unreadable = offset;
                    _stop = key;
                    return false;
                }

                var bits = ReadBits(_state.Slice(offset, field.Size), field.Type);
                _fields.Add(new StateFieldValue(field, PathOf(field, elementIndex, slot), bits, offset, key));
            }

            return true;
        }

        /// <summary>The reading of a walk that stopped at a field it could not read.</summary>
        public readonly StateFieldReading Result() => new(_fields.ToImmutable(), _unreadable, _stop);

        /// <summary>The reading of a walk that read every field: bytes left over are unreadable from the first of them.</summary>
        public readonly StateFieldReading Finish() =>
            new(_fields.ToImmutable(), Offset < _state.Length ? Offset : null, StateFieldKey.End);
    }
}

/// <summary>
/// A field's place in layout order, which matches list elements by index across two states: the part
/// (the header, the characters with their weapons, the projectiles, then each event list), the element's
/// position in its list (a count sorts before its list's records), the weapon slot + 1 (0 for a
/// character's own fields), and the field's ordinal.
/// </summary>
internal readonly record struct StateFieldKey(int Group, int Element, int Sub, int Ordinal) : IComparable<StateFieldKey>
{
    public const int HeaderGroup = 0;
    public const int CharacterGroup = 1;
    public const int ProjectileGroup = 2;
    public const int FirstEventGroup = 3;

    /// <summary>After every field: the stop of a state read to its end.</summary>
    public static StateFieldKey End { get; } = new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue);

    public int CompareTo(StateFieldKey other)
    {
        var group = Group.CompareTo(other.Group);
        if (group != 0)
        {
            return group;
        }

        var element = Element.CompareTo(other.Element);
        if (element != 0)
        {
            return element;
        }

        var sub = Sub.CompareTo(other.Sub);
        return sub != 0 ? sub : Ordinal.CompareTo(other.Ordinal);
    }
}
