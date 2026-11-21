using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Config;

/// <summary>
/// The binary config block of recorded streams and of <c>BatchStepper.LoadConfig</c>
/// (specs/015-shared-simulation-core/contracts/recorded-stream-format.md#config-block):
/// <c>u16 count</c>, then <c>count</c> entries of <c>{u16 fieldId, u32 valueBits}</c>, little-endian, in
/// strictly ascending fieldId, with every field of every present instance exactly once. The field IDs,
/// kinds and paths are the rule table's (<see cref="ConfigFields"/>).
/// </summary>
/// <remarks>
/// The block carries every value bit for bit, and its decoder checks the structure only: a NaN or a value
/// outside its range decodes, and <see cref="ConfigGuard.Check"/> refuses it, so a stream and a JSON file
/// are refused for the same reasons. Neither method is on the tick path.
/// </remarks>
public static class ConfigBlock
{
    /// <summary>The <c>u16 count</c> before the entries.</summary>
    public const int CountLength = 2;

    /// <summary>One entry: <c>u16 fieldId</c> and <c>u32 valueBits</c>.</summary>
    public const int EntryLength = 6;

    private const string StandaloneFormat = "config";

    /// <summary>Every field of the rule table with 8 weapons and 8 classes, in ascending fieldId: the known IDs.</summary>
    private static ImmutableArray<ExpandedField> KnownFields { get; } = ConfigFields.Expand(ConfigFields.MaxInstances, ConfigFields.MaxInstances);

    /// <summary>
    /// The block of a shape-valid configuration: one entry per row of
    /// <see cref="ConfigFields.Expand"/> for its weapon and projectile-class counts, in ascending fieldId,
    /// each value as its bits. Values are not checked, so an invalid value is carried as it is.
    /// </summary>
    /// <exception cref="ConfigValidationException">
    /// The configuration breaks a shape rule (a missing section, list element or capsule point, or a list
    /// with a count outside its range), so its fields cannot all be read; the errors are the guard's.
    /// </exception>
    public static byte[] Encode(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var shape = ConfigGuard.ShapeErrors(config);
        if (shape.Count != 0)
        {
            throw new ConfigValidationException(shape);
        }

        var fields = ConfigFields.Expand(config.Weapons!.Count, config.ProjectileClasses!.Count);
        var block = new byte[CountLength + (EntryLength * fields.Length)];
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)fields.Length);
        for (var k = 0; k < fields.Length; k++)
        {
            var entry = block.AsSpan(CountLength + (EntryLength * k));
            BinaryPrimitives.WriteUInt16LittleEndian(entry, fields[k].FieldId);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[2..], fields[k].GetBits(config));
        }

        return block;
    }

    /// <summary>
    /// Decodes a standalone block: format <c>config</c>, offsets from the block's first byte, fields named
    /// without a prefix. It checks the structure completely, in the contract's order, and no value.
    /// <list type="number">
    /// <item><description>The length is <c>2 + 6·count</c>: fewer bytes is <c>Truncated</c> at <c>count</c>,
    /// at the block's end; more is <c>Oversized</c> at <c>count</c>, where the extra bytes start.</description></item>
    /// <item><description>Each <c>entries[k].fieldId</c> is a field of the rule table, then above the one
    /// before: <c>Inconsistent</c> at the entry.</description></item>
    /// <item><description>Every field of every present instance is present, the weapon slots and the
    /// projectile classes 0 to N − 1 with no gap: a missing field is <c>Inconsistent</c>, named by its path,
    /// at the offset where it would sort.</description></item>
    /// </list>
    /// </summary>
    /// <exception cref="CoreFormatException">The block's structure is wrong.</exception>
    public static SimConfig Decode(ReadOnlySpan<byte> block) => Decode(block, StandaloneFormat, 0, string.Empty);

    /// <summary>
    /// <see cref="Decode(ReadOnlySpan{byte})"/> with the error's format, the offset of the block's first byte
    /// and the field prefix given: a recorded stream's block is <c>stream</c>, base 64, prefix <c>config.</c>.
    /// </summary>
    /// <exception cref="CoreFormatException">The block's structure is wrong.</exception>
    internal static SimConfig Decode(ReadOnlySpan<byte> block, string format, long baseOffset, string fieldPrefix)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(fieldPrefix);

        // 1. The length is 2 + 6·count.
        if (block.Length < CountLength)
        {
            throw Error(format, FormatErrorCode.Truncated, baseOffset + block.Length, fieldPrefix + "count", FormatDetail.ExpectedLength, CountLength, (ulong)block.Length);
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var expectedLength = CountLength + (EntryLength * count);
        if (block.Length < expectedLength)
        {
            throw Error(format, FormatErrorCode.Truncated, baseOffset + block.Length, fieldPrefix + "count", FormatDetail.ExpectedLength, (ulong)expectedLength, (ulong)block.Length);
        }

        if (block.Length > expectedLength)
        {
            throw Error(format, FormatErrorCode.Oversized, baseOffset + expectedLength, fieldPrefix + "count", FormatDetail.ExpectedLength, (ulong)expectedLength, (ulong)block.Length);
        }

        // 2. Each fieldId is a field of the table and above the one before; note the instances present.
        var entries = new ExpandedField[count];
        var values = new uint[count];
        var weaponCount = 0;
        var classCount = 0;
        for (var k = 0; k < count; k++)
        {
            var offset = CountLength + (EntryLength * k);
            var fieldId = BinaryPrimitives.ReadUInt16LittleEndian(block[offset..]);
            if (!TryFindKnown(fieldId, out var field))
            {
                throw Error(format, FormatErrorCode.Inconsistent, baseOffset + offset, fieldPrefix + EntryPath(k), FormatDetail.UnknownField, fieldId);
            }

            if (k > 0 && fieldId <= entries[k - 1].FieldId)
            {
                throw Error(format, FormatErrorCode.Inconsistent, baseOffset + offset, fieldPrefix + EntryPath(k), FormatDetail.NotAbove, fieldId, entries[k - 1].FieldId);
            }

            switch (field.Template.Scope)
            {
                case ConfigFieldScope.WeaponSlot:
                    weaponCount = System.Math.Max(weaponCount, field.Instance + 1);
                    break;
                case ConfigFieldScope.ProjectileClass:
                    classCount = System.Math.Max(classCount, field.Instance + 1);
                    break;
                default:
                    break;
            }

            entries[k] = field;
            values[k] = BinaryPrimitives.ReadUInt32LittleEndian(block[(offset + 2)..]);
        }

        // 3. Every field of every present instance is present. The entries are known, ascending and within
        // the instances present, so they are a subset of the expected fields; the first expected field that
        // is not the next entry is missing, and would sort at that entry.
        var expected = ConfigFields.Expand(weaponCount, classCount);
        var next = 0;
        foreach (var field in expected)
        {
            if (next < count && entries[next].FieldId == field.FieldId)
            {
                next++;
                continue;
            }

            var offset = CountLength + (EntryLength * next);
            throw Error(format, FormatErrorCode.Inconsistent, baseOffset + offset, fieldPrefix + field.Path, FormatDetail.Missing, field.FieldId);
        }

        // 4. Every value, bit for bit, into a configuration with every section and instance present.
        var config = Skeleton(weaponCount, classCount);
        for (var k = 0; k < count; k++)
        {
            entries[k].SetBits(config, values[k]);
        }

        return config;
    }

    /// <summary>A configuration with every section, the instances given and both stances' capsules and points, all values 0.</summary>
    private static SimConfig Skeleton(int weaponCount, int classCount)
    {
        var weapons = new List<WeaponConfig?>(weaponCount);
        for (var i = 0; i < weaponCount; i++)
        {
            weapons.Add(new WeaponConfig());
        }

        var classes = new List<ProjectileClassConfig?>(classCount);
        for (var k = 0; k < classCount; k++)
        {
            classes.Add(new ProjectileClassConfig());
        }

        return new SimConfig
        {
            World = new WorldConfig(),
            Movement = new MovementConfig(),
            Weapons = weapons,
            ProjectileClasses = classes,
            Hitboxes = new HitboxesConfig { Standing = Capsules(), Crouched = Capsules() },
            Input = new InputConfig(),
        };
    }

    private static List<HitboxCapsuleConfig?> Capsules()
    {
        var capsules = new List<HitboxCapsuleConfig?>(ConfigFields.CapsulesPerStance);
        for (var c = 0; c < ConfigFields.CapsulesPerStance; c++)
        {
            capsules.Add(new HitboxCapsuleConfig { A = new HitboxPointConfig(), B = new HitboxPointConfig() });
        }

        return capsules;
    }

    /// <summary>A binary search of <see cref="KnownFields"/>, which is in ascending fieldId.</summary>
    private static bool TryFindKnown(ushort fieldId, out ExpandedField field)
    {
        var known = KnownFields;
        var low = 0;
        var high = known.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var id = known[middle].FieldId;
            if (id == fieldId)
            {
                field = known[middle];
                return true;
            }

            if (id < fieldId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        field = default;
        return false;
    }

    private static string EntryPath(int k) => string.Concat("entries[", k.ToString(CultureInfo.InvariantCulture), "].fieldId");

    private static CoreFormatException Error(string format, FormatErrorCode code, long offset, string field, FormatDetail detail, ulong a, ulong b = 0) =>
        new(new FormatError(format, code, offset, field, detail, a, b));
}
