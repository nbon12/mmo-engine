using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Math;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Config;

/// <summary>
/// The one rule table (FR-015, FR-029, contracts/config-rules.md): every field row refuses values outside
/// its range, every relation refuses its violation and accepts its boundary, every property of
/// <see cref="SimConfig"/> has exactly one template row, shape errors end the check, and errors come in
/// rule-table order. Every Theory row asserts the same list from <see cref="ConfigGuard.Check"/> and from
/// the server adapter <see cref="SimConfigValidator.Errors"/>, so there is one table.
/// </summary>
/// <remarks>
/// <see cref="ContractRows"/> is this test's own transcription of the contract's tables, one row per field
/// of <c>simcore/config/default.json</c>'s instances (2 weapon slots, 2 projectile classes, 2 stances × 3
/// capsules), so the core's table is compared with a second, separate reading of the contract. Values are
/// written into a <see cref="SimConfig"/> by property path through reflection, independently of
/// <c>ConfigFields</c>' setters.
/// </remarks>
public sealed partial class ConfigRuleTableTests
{
    private const string BelowMin = "below-min";
    private const string AboveMax = "above-max";
    private const string NaN = "nan";
    private const string PositiveInfinity = "positive-infinity";
    private const string NegativeInfinity = "negative-infinity";
    private const string NotAMember = "not-a-member";

    /// <summary>
    /// The contract's rule table for the placeholders' instances, in ascending fieldId: path, kind
    /// (Int, Float or Enum), minimum and maximum as the contract prints them (none for the Enum rows), and
    /// fieldId (contracts/config-rules.md#rule-table and #field-ids).
    /// </summary>
    private static readonly ContractRow[] ContractRows =
    [
        new("stepRateHz", "Int", "20", "60", 0x0001),
        new("world.maxCharacters", "Int", "1", "1024", 0x0002),
        new("world.maxProjectiles", "Int", "1", "16384", 0x0003),
        new("movement.walkSpeed", "Float", "0.1", "20", 0x1001),
        new("movement.runSpeed", "Float", "0.1", "20", 0x1002),
        new("movement.crouchSpeed", "Float", "0.1", "20", 0x1003),
        new("movement.groundAccel", "Float", "0.1", "1000", 0x1004),
        new("movement.groundDecel", "Float", "0.1", "1000", 0x1005),
        new("movement.airAccel", "Float", "0", "1000", 0x1006),
        new("movement.gravity", "Float", "0.1", "100", 0x1007),
        new("movement.jumpSpeed", "Float", "0.1", "20", 0x1008),
        new("movement.maxFallSpeed", "Float", "1", "100", 0x1009),
        new("movement.capsuleRadius", "Float", "0.05", "1", 0x100A),
        new("movement.standHeight", "Float", "0.5", "3", 0x100B),
        new("movement.crouchHeight", "Float", "0.5", "3", 0x100C),
        new("movement.stepHeight", "Float", "0.01", "1", 0x100D),
        new("movement.snapDistance", "Float", "0", "2", 0x100E),
        new("movement.slopeLimitDegrees", "Float", "5", "60", 0x100F),
        new("movement.ledgeSupportMinNormalY", "Float", "0", "1", 0x1010),
        new("movement.contactSkin", "Float", "0.001", "0.05", 0x1011),
        new("movement.contactTolerance", "Float", "0.0001", "0.025", 0x1012),
        new("movement.maxSlideIterations", "Int", "1", "8", 0x1013),
        new("movement.maxSubMoves", "Int", "1", "16", 0x1014),
        new("weapons[0].roundsPerMinute", "Int", "1", "6000", 0x2001),
        new("weapons[0].maxShotsPerStep", "Int", "1", "8", 0x2002),
        new("weapons[0].fireMode", "Enum", null, null, 0x2003),
        new("weapons[0].magazineSize", "Int", "1", "1000", 0x2004),
        new("weapons[0].reserveRounds", "Int", "0", "10000", 0x2005),
        new("weapons[0].reloadMs", "Int", "0", "60000", 0x2006),
        new("weapons[0].equipMs", "Int", "0", "10000", 0x2007),
        new("weapons[0].heatPerShot", "Int", "0", "1000000", 0x2008),
        new("weapons[0].overheatThreshold", "Int", "1", "10000000", 0x2009),
        new("weapons[0].heatRecoveryLevel", "Int", "0", "10000000", 0x200A),
        new("weapons[0].heatCooldownPerSecond", "Int", "0", "10000000", 0x200B),
        new("weapons[0].heatCooldownDelayMs", "Int", "0", "10000", 0x200C),
        new("weapons[0].spreadHalfAngleDeg", "Float", "0", "45", 0x200D),
        new("weapons[0].recoilKickPitchDeg", "Float", "0", "10", 0x200E),
        new("weapons[0].recoilKickYawDeg", "Float", "0", "10", 0x200F),
        new("weapons[0].recoilMaxPitchDeg", "Float", "0", "45", 0x2010),
        new("weapons[0].recoilMaxYawDeg", "Float", "0", "45", 0x2011),
        new("weapons[0].recoilRecoveryDegPerSecond", "Float", "0", "360", 0x2012),
        new("weapons[0].recoilRecoveryDelayMs", "Int", "0", "10000", 0x2013),
        new("weapons[0].muzzleSpeed", "Float", "1", "2000", 0x2014),
        new("weapons[0].projectileClass", "Int", "0", "7", 0x2015),
        new("weapons[1].roundsPerMinute", "Int", "1", "6000", 0x2101),
        new("weapons[1].maxShotsPerStep", "Int", "1", "8", 0x2102),
        new("weapons[1].fireMode", "Enum", null, null, 0x2103),
        new("weapons[1].magazineSize", "Int", "1", "1000", 0x2104),
        new("weapons[1].reserveRounds", "Int", "0", "10000", 0x2105),
        new("weapons[1].reloadMs", "Int", "0", "60000", 0x2106),
        new("weapons[1].equipMs", "Int", "0", "10000", 0x2107),
        new("weapons[1].heatPerShot", "Int", "0", "1000000", 0x2108),
        new("weapons[1].overheatThreshold", "Int", "1", "10000000", 0x2109),
        new("weapons[1].heatRecoveryLevel", "Int", "0", "10000000", 0x210A),
        new("weapons[1].heatCooldownPerSecond", "Int", "0", "10000000", 0x210B),
        new("weapons[1].heatCooldownDelayMs", "Int", "0", "10000", 0x210C),
        new("weapons[1].spreadHalfAngleDeg", "Float", "0", "45", 0x210D),
        new("weapons[1].recoilKickPitchDeg", "Float", "0", "10", 0x210E),
        new("weapons[1].recoilKickYawDeg", "Float", "0", "10", 0x210F),
        new("weapons[1].recoilMaxPitchDeg", "Float", "0", "45", 0x2110),
        new("weapons[1].recoilMaxYawDeg", "Float", "0", "45", 0x2111),
        new("weapons[1].recoilRecoveryDegPerSecond", "Float", "0", "360", 0x2112),
        new("weapons[1].recoilRecoveryDelayMs", "Int", "0", "10000", 0x2113),
        new("weapons[1].muzzleSpeed", "Float", "1", "2000", 0x2114),
        new("weapons[1].projectileClass", "Int", "0", "7", 0x2115),
        new("projectileClasses[0].gravityFactor", "Float", "0", "10", 0x3001),
        new("projectileClasses[0].maxRangeM", "Float", "1", "10000", 0x3002),
        new("projectileClasses[0].lifetimeMs", "Int", "1", "60000", 0x3003),
        new("projectileClasses[1].gravityFactor", "Float", "0", "10", 0x3101),
        new("projectileClasses[1].maxRangeM", "Float", "1", "10000", 0x3102),
        new("projectileClasses[1].lifetimeMs", "Int", "1", "60000", 0x3103),
        new("hitboxes.standing[0].a.fwd", "Float", "-1", "1", 0x4001),
        new("hitboxes.standing[0].a.right", "Float", "-1", "1", 0x4002),
        new("hitboxes.standing[0].a.up", "Float", "0", "3.5", 0x4003),
        new("hitboxes.standing[0].b.fwd", "Float", "-1", "1", 0x4004),
        new("hitboxes.standing[0].b.right", "Float", "-1", "1", 0x4005),
        new("hitboxes.standing[0].b.up", "Float", "0", "3.5", 0x4006),
        new("hitboxes.standing[0].radius", "Float", "0.01", "1", 0x4007),
        new("hitboxes.standing[1].a.fwd", "Float", "-1", "1", 0x4011),
        new("hitboxes.standing[1].a.right", "Float", "-1", "1", 0x4012),
        new("hitboxes.standing[1].a.up", "Float", "0", "3.5", 0x4013),
        new("hitboxes.standing[1].b.fwd", "Float", "-1", "1", 0x4014),
        new("hitboxes.standing[1].b.right", "Float", "-1", "1", 0x4015),
        new("hitboxes.standing[1].b.up", "Float", "0", "3.5", 0x4016),
        new("hitboxes.standing[1].radius", "Float", "0.01", "1", 0x4017),
        new("hitboxes.standing[2].a.fwd", "Float", "-1", "1", 0x4021),
        new("hitboxes.standing[2].a.right", "Float", "-1", "1", 0x4022),
        new("hitboxes.standing[2].a.up", "Float", "0", "3.5", 0x4023),
        new("hitboxes.standing[2].b.fwd", "Float", "-1", "1", 0x4024),
        new("hitboxes.standing[2].b.right", "Float", "-1", "1", 0x4025),
        new("hitboxes.standing[2].b.up", "Float", "0", "3.5", 0x4026),
        new("hitboxes.standing[2].radius", "Float", "0.01", "1", 0x4027),
        new("hitboxes.eyeHeightStanding", "Float", "0.1", "3", 0x4031),
        new("hitboxes.crouched[0].a.fwd", "Float", "-1", "1", 0x4101),
        new("hitboxes.crouched[0].a.right", "Float", "-1", "1", 0x4102),
        new("hitboxes.crouched[0].a.up", "Float", "0", "3.5", 0x4103),
        new("hitboxes.crouched[0].b.fwd", "Float", "-1", "1", 0x4104),
        new("hitboxes.crouched[0].b.right", "Float", "-1", "1", 0x4105),
        new("hitboxes.crouched[0].b.up", "Float", "0", "3.5", 0x4106),
        new("hitboxes.crouched[0].radius", "Float", "0.01", "1", 0x4107),
        new("hitboxes.crouched[1].a.fwd", "Float", "-1", "1", 0x4111),
        new("hitboxes.crouched[1].a.right", "Float", "-1", "1", 0x4112),
        new("hitboxes.crouched[1].a.up", "Float", "0", "3.5", 0x4113),
        new("hitboxes.crouched[1].b.fwd", "Float", "-1", "1", 0x4114),
        new("hitboxes.crouched[1].b.right", "Float", "-1", "1", 0x4115),
        new("hitboxes.crouched[1].b.up", "Float", "0", "3.5", 0x4116),
        new("hitboxes.crouched[1].radius", "Float", "0.01", "1", 0x4117),
        new("hitboxes.crouched[2].a.fwd", "Float", "-1", "1", 0x4121),
        new("hitboxes.crouched[2].a.right", "Float", "-1", "1", 0x4122),
        new("hitboxes.crouched[2].a.up", "Float", "0", "3.5", 0x4123),
        new("hitboxes.crouched[2].b.fwd", "Float", "-1", "1", 0x4124),
        new("hitboxes.crouched[2].b.right", "Float", "-1", "1", 0x4125),
        new("hitboxes.crouched[2].b.up", "Float", "0", "3.5", 0x4126),
        new("hitboxes.crouched[2].radius", "Float", "0.01", "1", 0x4127),
        new("hitboxes.eyeHeightCrouched", "Float", "0.1", "3", 0x4131),
        new("input.weaponSlots", "Int", "1", "8", 0x5001),
    ];

    /// <summary>Every row × its out-of-range cases: Int min − 1 and max + 1; Float one ulp outside each bound, NaN, +∞ and −∞; Enum the value 2.</summary>
    public static TheoryData<string, string> OutOfRangeCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var row in ContractRows)
            {
                string[] cases = row.Kind switch
                {
                    "Int" => [BelowMin, AboveMax],
                    "Float" => [BelowMin, AboveMax, NaN, PositiveInfinity, NegativeInfinity],
                    "Enum" => [NotAMember],
                    _ => throw new InvalidOperationException($"Unknown kind {row.Kind} for {row.Path}."),
                };
                foreach (var valueCase in cases)
                {
                    data.Add(row.Path, valueCase);
                }
            }

            return data;
        }
    }

    public static TheoryData<string> Relations =>
    [
        "R01", "R02", "R03", "R04", "R05", "R06", "R07", "R08", "R09", "R10",
        "R11", "R12", "R13", "R14", "R15", "R16", "R17", "R18", "R19",
    ];

    public static TheoryData<string> ShapeCases =>
    [
        "movement-null-and-step-rate-0",
        "zero-weapons",
        "nine-weapons",
        "zero-projectile-classes",
        "two-standing-capsules",
        "standing-0-a-null",
        "weapon-1-null",
        "world-and-input-null",
        "weapons-null",
        "standing-null",
        "hitboxes-null",
        "crouched-1-null",
        "crouched-2-b-null",
    ];

    public static TheoryData<string> OrderCases => ["field-errors", "relation-errors"];

    /// <summary>
    /// Each row of the rule table, applied to the placeholders one value at a time, gives exactly one error
    /// with that row's path: <c>must be between {min} and {max}</c> with the contract's bounds,
    /// <c>must be finite</c> for a NaN or an infinity, or <c>must be Auto or Semi</c>. The rows include
    /// <c>stepRateHz</c> at 19 and at 61 (FR-015) and <c>movement.gravity</c> at NaN.
    /// </summary>
    [Theory]
    [MemberData(nameof(OutOfRangeCases))]
    public void EveryRowRefusesValuesOutsideItsRange(string path, string valueCase)
    {
        var row = ContractRows.Single(r => r.Path == path);
        var (value, message) = OutOfRange(row, valueCase);
        var config = TestConfigs.With(c => SetLeaf(c, path, value));

        AssertErrors(config, [new ConfigError(path, message)]);
    }

    /// <summary>
    /// Each relation R01–R19 refuses a configuration that breaks it with exactly its error, path and
    /// message (<c>[i]</c>, <c>[c]</c>, <c>{stance}</c> and <c>({n})</c> substituted), and accepts the same
    /// configuration at the boundary value, where it just holds. Float thresholds are computed with the
    /// contract's parenthesisation in <c>float</c>, from the same values the guard reads.
    /// </summary>
    [Theory]
    [MemberData(nameof(Relations))]
    public void EveryRelationRefusesItsViolationAndAcceptsItsBoundary(string relation)
    {
        foreach (var check in RelationChecks(relation))
        {
            var violating = TestConfigs.With(check.Violate);
            AssertErrors(violating, [check.Error], $"{relation} {check.Label}, violated");

            var boundary = TestConfigs.With(check.Boundary);
            AssertErrors(boundary, [], $"{relation} {check.Label}, at the boundary");
        }
    }

    /// <summary>
    /// The template table covers <see cref="SimConfig"/> exactly. Every leaf property path of
    /// <see cref="SimConfig"/> and its sections, with the weapon slot, projectile class, stance and capsule
    /// as placeholders, has exactly one of the 57 template rows (3 general, 20 movement, 21 weapon, 3
    /// projectile class, 7 capsule, 2 eye heights, 1 input), and every row is such a leaf. The rows are in
    /// strictly ascending order of their first instance's fieldId. The placeholders' expansion equals
    /// <see cref="ContractRows"/>, and the largest expansion (8 slots, 8 classes) gives 260 unique,
    /// strictly ascending fieldIds.
    /// </summary>
    [Fact]
    public void EveryConfigPropertyHasExactlyOneRowAndFieldIdsAreUnique()
    {
        var templates = ConfigFields.Templates;
        Assert.Equal(57, templates.Length);

        // Every leaf property has exactly one template row, and every row is a leaf property.
        var leaves = new List<string>();
        CollectLeaves(typeof(SimConfig), string.Empty, leaves);
        var leafTemplates = leaves.Select(Placeholders).Distinct(StringComparer.Ordinal).ToArray();
        var rowTemplates = templates.Select(t => Placeholders(t.PathTemplate)).ToArray();
        foreach (var leaf in leafTemplates)
        {
            var count = rowTemplates.Count(t => t == leaf);
            Assert.True(count == 1, $"The SimConfig property {leaf} has {count} template rows, expected exactly 1");
        }

        foreach (var row in rowTemplates)
        {
            Assert.True(leafTemplates.Contains(row, StringComparer.Ordinal), $"The template row {row} is no property of SimConfig");
        }

        Assert.Equal(57, leafTemplates.Length);

        // Rows in strictly ascending order of their first instance's fieldId.
        var expandedOne = ConfigFields.Expand(1, 1);
        var firstIds = templates
            .Select(t => (int)expandedOne.First(e => ReferenceEquals(e.Template, t)).FieldId)
            .ToArray();
        for (var r = 1; r < firstIds.Length; r++)
        {
            Assert.True(
                firstIds[r] > firstIds[r - 1],
                $"Template row {r} ({templates[r].PathTemplate}) starts at 0x{firstIds[r]:X4}, not above row {r - 1}'s 0x{firstIds[r - 1]:X4}");
        }

        Assert.Equal(0x0001, firstIds[0]);
        int[] lastFour = [0x4007, 0x4031, 0x4131, 0x5001];
        Assert.Equal(lastFour, firstIds[^4..]);

        // The placeholders' instances are exactly the contract's 116 rows.
        var expanded = ConfigFields.Expand(2, 2);
        Assert.Equal(ContractRows.Length, expanded.Length);
        for (var k = 0; k < ContractRows.Length; k++)
        {
            var expected = ContractRows[k];
            var actual = expanded[k];
            var label = $"expanded row {k} ({actual.Path}, 0x{(int)actual.FieldId:X4})";
            Assert.True(actual.Path == expected.Path, $"{label}: path {actual.Path}, the contract's row {k} is {expected.Path}");
            Assert.True((int)actual.FieldId == expected.FieldId, $"{label}: fieldId 0x{(int)actual.FieldId:X4}, the contract gives 0x{expected.FieldId:X4}");
            Assert.True(actual.Template.Kind.ToString() == expected.Kind, $"{label}: kind {actual.Template.Kind}, the contract gives {expected.Kind}");
            if (expected.Kind != "Enum")
            {
                Assert.True(Bound(actual.Template.Kind.ToString(), actual.Template.MinBits) == expected.Min, $"{label}: minimum {Bound(expected.Kind, actual.Template.MinBits)}, the contract gives {expected.Min}");
                Assert.True(Bound(actual.Template.Kind.ToString(), actual.Template.MaxBits) == expected.Max, $"{label}: maximum {Bound(expected.Kind, actual.Template.MaxBits)}, the contract gives {expected.Max}");
            }
        }

        AssertStrictlyAscending(expanded.Select(e => (int)e.FieldId).ToArray(), "Expand(2, 2)");

        var largest = ConfigFields.Expand(8, 8);
        Assert.Equal(260, largest.Length);
        AssertStrictlyAscending(largest.Select(e => (int)e.FieldId).ToArray(), "Expand(8, 8)");
    }

    /// <summary>
    /// Every shape error is listed, in section order, and then the check ends: each row gives exactly its
    /// errors, even when the configuration has other problems. A null list counts as one with no entries
    /// and gets the count message (contracts/config-rules.md#shape, #check-order-and-the-error-list).
    /// </summary>
    [Theory]
    [MemberData(nameof(ShapeCases))]
    public void ShapeErrorsEndTheCheck(string caseName)
    {
        var (edit, errors) = Shape(caseName);
        var config = TestConfigs.With(edit);

        AssertErrors(config, errors);
    }

    /// <summary>
    /// Field errors come in ascending fieldId and suppress every relation; relation errors come in R01 to
    /// R19 order (contracts/config-rules.md#check-order-and-the-error-list).
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderCases))]
    public void ErrorsComeInRuleTableOrder(string caseName)
    {
        switch (caseName)
        {
            case "field-errors":
                AssertErrors(
                    TestConfigs.With(c =>
                    {
                        c.StepRateHz = 10;
                        c.Movement!.Gravity = float.NaN;
                        c.Weapons![0]!.ReloadMs = -1;
                        c.Weapons![1]!.MagazineSize = 0;
                        c.Input!.WeaponSlots = 9;
                    }),
                    [
                        new("stepRateHz", "must be between 20 and 60"),
                        new("movement.gravity", "must be finite"),
                        new("weapons[0].reloadMs", "must be between 0 and 60000"),
                        new("weapons[1].magazineSize", "must be between 1 and 1000"),
                        new("input.weaponSlots", "must be between 1 and 8"),
                    ]);
                break;

            case "relation-errors":
                AssertErrors(
                    TestConfigs.With(c =>
                    {
                        c.Hitboxes!.EyeHeightStanding = 1.8f;
                        c.Movement!.RunSpeed = 3f;
                    }),
                    [
                        new("movement.runSpeed", "must be at least movement.walkSpeed"),
                        new("hitboxes.eyeHeightStanding", "must be less than movement.standHeight"),
                    ]);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case.");
        }
    }

    /// <summary>The guard and the server adapter both give exactly <paramref name="expected"/>, in order.</summary>
    private static void AssertErrors(SimConfig config, ConfigError[] expected, string? label = null)
    {
        var prefix = label is null ? string.Empty : label + ": ";
        var guard = ConfigGuard.Check(config);
        Assert.True(
            guard.SequenceEqual(expected),
            $"{prefix}ConfigGuard.Check gave [{string.Join("; ", guard)}], expected [{string.Join("; ", expected.AsEnumerable())}]");

        var adapter = SimConfigValidator.Errors(config);
        Assert.True(
            adapter.SequenceEqual(expected),
            $"{prefix}SimConfigValidator.Errors gave [{string.Join("; ", adapter)}], expected [{string.Join("; ", expected.AsEnumerable())}]");
    }

    /// <summary>The value a case writes into the row's field, and the message the guard must give for it.</summary>
    private static (object Value, string Message) OutOfRange(ContractRow row, string valueCase)
    {
        var range = $"must be between {row.Min} and {row.Max}";

        // Each value is boxed as its own type. Without the casts the switch's natural type would be
        // (float, string), the best common type of the arms, and the Int and Enum values would be boxed
        // as floats, which their int and FireMode properties refuse.
        return (row.Kind, valueCase) switch
        {
            ("Int", BelowMin) => ((object)(ParseInt(row.Min!) - 1), range),
            ("Int", AboveMax) => ((object)(ParseInt(row.Max!) + 1), range),
            ("Float", BelowMin) => ((object)float.BitDecrement(ParseFloat(row.Min!)), range),
            ("Float", AboveMax) => ((object)float.BitIncrement(ParseFloat(row.Max!)), range),
            ("Float", NaN) => ((object)float.NaN, "must be finite"),
            ("Float", PositiveInfinity) => ((object)float.PositiveInfinity, "must be finite"),
            ("Float", NegativeInfinity) => ((object)float.NegativeInfinity, "must be finite"),
            ("Enum", NotAMember) => ((object)2u, "must be Auto or Semi"),
            _ => throw new ArgumentOutOfRangeException(nameof(valueCase), valueCase, $"No {valueCase} case for a {row.Kind} row."),
        };
    }

    /// <summary>A template row's bound, printed as the contract prints it: an Int's value or a Float's shortest round-trip text.</summary>
    private static string Bound(string kind, uint bits) => kind switch
    {
        "Int" => unchecked((int)bits).ToString(CultureInfo.InvariantCulture),
        "Float" => BitConverter.UInt32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture),
        _ => $"0x{bits:X8}",
    };

    /// <summary>
    /// One violation and its boundary per relation (two for R07, one per side), each applied to the
    /// placeholders. Each edit changes only what the relation reads, so the violation breaks nothing else
    /// and the boundary breaks nothing at all.
    /// </summary>
    private static RelationCheck[] RelationChecks(string relation)
    {
        var movement = TestConfigs.Default().Movement!;
        var cosSlopeLimit = SimMath.Cos((movement.SlopeLimitDegrees * System.MathF.PI) / 180f);

        // The float thresholds, with the contract's parenthesisation.
        var crouchHeightAbove = (2f * movement.CapsuleRadius) + 0.05f;
        var stepHeightAtLeast = movement.CapsuleRadius * (1f - cosSlopeLimit);
        var snapDistanceAtMost = 2f * movement.StepHeight;
        var contactToleranceLow = movement.ContactSkin * 0.05f;
        var contactToleranceHigh = movement.ContactSkin * 0.5f;
        var standingUpAtMost = movement.StandHeight + 0.5f;
        var crouchedUpAtMost = movement.CrouchHeight + 0.5f;

        return relation switch
        {
            // walkSpeed >= crouchSpeed (2).
            "R01" =>
            [
                new(
                    "walkSpeed just below crouchSpeed",
                    c => c.Movement!.WalkSpeed = float.BitDecrement(movement.CrouchSpeed),
                    c => c.Movement!.WalkSpeed = movement.CrouchSpeed,
                    new("movement.walkSpeed", "must be at least movement.crouchSpeed")),
            ],

            // runSpeed >= walkSpeed (3.5).
            "R02" =>
            [
                new(
                    "runSpeed just below walkSpeed",
                    c => c.Movement!.RunSpeed = float.BitDecrement(movement.WalkSpeed),
                    c => c.Movement!.RunSpeed = movement.WalkSpeed,
                    new("movement.runSpeed", "must be at least movement.walkSpeed")),
            ],

            // crouchHeight > ((2f * capsuleRadius) + 0.05f), 0.85. eyeHeightCrouched is lowered to 0.8 in both
            // configurations, so R19 (eyeHeightCrouched < crouchHeight) still holds.
            "R03" =>
            [
                new(
                    "crouchHeight equal to twice capsuleRadius plus 0.05",
                    c =>
                    {
                        c.Movement!.CrouchHeight = crouchHeightAbove;
                        c.Hitboxes!.EyeHeightCrouched = 0.8f;
                    },
                    c =>
                    {
                        c.Movement!.CrouchHeight = float.BitIncrement(crouchHeightAbove);
                        c.Hitboxes!.EyeHeightCrouched = 0.8f;
                    },
                    new("movement.crouchHeight", "must be greater than twice movement.capsuleRadius plus 0.05")),
            ],

            // crouchHeight < standHeight (1.8).
            "R04" =>
            [
                new(
                    "crouchHeight equal to standHeight",
                    c => c.Movement!.CrouchHeight = movement.StandHeight,
                    c => c.Movement!.CrouchHeight = float.BitDecrement(movement.StandHeight),
                    new("movement.crouchHeight", "must be less than movement.standHeight")),
            ],

            // stepHeight >= (capsuleRadius * (1f - CosSlopeLimit)). snapDistance is lowered to 0.1 in both
            // configurations, so R06 (snapDistance <= 2 · stepHeight) still holds.
            "R05" =>
            [
                new(
                    "stepHeight just below capsuleRadius times (1 - cos slopeLimit)",
                    c =>
                    {
                        c.Movement!.StepHeight = float.BitDecrement(stepHeightAtLeast);
                        c.Movement!.SnapDistance = 0.1f;
                    },
                    c =>
                    {
                        c.Movement!.StepHeight = stepHeightAtLeast;
                        c.Movement!.SnapDistance = 0.1f;
                    },
                    new("movement.stepHeight", "must be at least movement.capsuleRadius times (1 - cos movement.slopeLimitDegrees)")),
            ],

            // snapDistance <= (2f * stepHeight), 0.7.
            "R06" =>
            [
                new(
                    "snapDistance just above twice stepHeight",
                    c => c.Movement!.SnapDistance = float.BitIncrement(snapDistanceAtMost),
                    c => c.Movement!.SnapDistance = snapDistanceAtMost,
                    new("movement.snapDistance", "must be at most twice movement.stepHeight")),
            ],

            // (contactSkin * 0.05f) <= contactTolerance <= (contactSkin * 0.5f): 0.0005 to 0.005, each side.
            "R07" =>
            [
                new(
                    "contactTolerance just below 0.05 times contactSkin",
                    c => c.Movement!.ContactTolerance = float.BitDecrement(contactToleranceLow),
                    c => c.Movement!.ContactTolerance = contactToleranceLow,
                    new("movement.contactTolerance", "must be between 0.05 and 0.5 times movement.contactSkin")),
                new(
                    "contactTolerance just above 0.5 times contactSkin",
                    c => c.Movement!.ContactTolerance = float.BitIncrement(contactToleranceHigh),
                    c => c.Movement!.ContactTolerance = contactToleranceHigh,
                    new("movement.contactTolerance", "must be between 0.05 and 0.5 times movement.contactSkin")),
            ],

            // ledgeSupportMinNormalY <= CosSlopeLimit.
            "R08" =>
            [
                new(
                    "ledgeSupportMinNormalY just above cos slopeLimit",
                    c => c.Movement!.LedgeSupportMinNormalY = float.BitIncrement(cosSlopeLimit),
                    c => c.Movement!.LedgeSupportMinNormalY = cosSlopeLimit,
                    new("movement.ledgeSupportMinNormalY", "must be at most the cosine of movement.slopeLimitDegrees")),
            ],

            // (MaxSpeed * 0.05f) <= ((float)maxSubMoves * capsuleRadius). MaxSpeed is (7 / cos 45°) + 50, about
            // 59.9, so 7 sub-moves of 0.4 m (2.8) are too few and 8 (3.2) are enough.
            "R09" =>
            [
                new(
                    "7 sub-moves",
                    c => c.Movement!.MaxSubMoves = 7,
                    c => c.Movement!.MaxSubMoves = 8,
                    new("movement.maxSubMoves", "must be large enough that no sub-move of a 20 Hz step is longer than movement.capsuleRadius")),
            ],

            // The weapon count equals input.weaponSlots (2 placeholder weapons).
            "R10" =>
            [
                new(
                    "3 weapon slots for 2 weapons",
                    c => c.Input!.WeaponSlots = 3,
                    c => c.Input!.WeaponSlots = 2,
                    new("weapons", "must have exactly input.weaponSlots (3) entries")),
            ],

            // roundsPerMinute <= maxShotsPerStep * 60 * stepRateHz: 1 · 60 · 30 = 1800.
            "R11" =>
            [
                new(
                    "1801 rounds per minute at 30 Hz",
                    c => c.Weapons![0]!.RoundsPerMinute = 1801,
                    c => c.Weapons![0]!.RoundsPerMinute = 1800,
                    new("weapons[0].roundsPerMinute", "must be at most weapons[0].maxShotsPerStep times 60 times stepRateHz (1800)")),
            ],

            // heatRecoveryLevel < overheatThreshold (100000).
            "R12" =>
            [
                new(
                    "heatRecoveryLevel equal to overheatThreshold",
                    c => c.Weapons![0]!.HeatRecoveryLevel = 100_000,
                    c => c.Weapons![0]!.HeatRecoveryLevel = 99_999,
                    new("weapons[0].heatRecoveryLevel", "must be less than weapons[0].overheatThreshold")),
            ],

            // heatPerShot == 0 or heatCooldownPerSecond > 0.
            "R13" =>
            [
                new(
                    "a heating weapon that never cools",
                    c =>
                    {
                        c.Weapons![1]!.HeatPerShot = 1;
                        c.Weapons![1]!.HeatCooldownPerSecond = 0;
                    },
                    c =>
                    {
                        c.Weapons![1]!.HeatPerShot = 1;
                        c.Weapons![1]!.HeatCooldownPerSecond = 1;
                    },
                    new("weapons[1].heatCooldownPerSecond", "must be greater than 0 when weapons[1].heatPerShot is greater than 0")),
            ],

            // projectileClass < the number of projectileClasses (2).
            "R14" =>
            [
                new(
                    "projectile class 2 of 2",
                    c => c.Weapons![1]!.ProjectileClass = 2,
                    c => c.Weapons![1]!.ProjectileClass = 1,
                    new("weapons[1].projectileClass", "must be less than the number of projectileClasses (2)")),
            ],

            // b.up >= a.up; the crouched head's a.up is 1.00.
            "R15" =>
            [
                new(
                    "crouched head b.up just below its a.up",
                    c => c.Hitboxes!.Crouched![0]!.B!.Up = float.BitDecrement(1.00f),
                    c => c.Hitboxes!.Crouched![0]!.B!.Up = 1.00f,
                    new("hitboxes.crouched[0].b.up", "must be at least hitboxes.crouched[0].a.up")),
            ],

            // standing b.up <= (movement.standHeight + 0.5f), 2.3.
            "R16" =>
            [
                new(
                    "standing legs b.up just above standHeight plus 0.5",
                    c => c.Hitboxes!.Standing![2]!.B!.Up = float.BitIncrement(standingUpAtMost),
                    c => c.Hitboxes!.Standing![2]!.B!.Up = standingUpAtMost,
                    new("hitboxes.standing[2].b.up", "must be at most movement.standHeight plus 0.5")),
            ],

            // crouched b.up <= (movement.crouchHeight + 0.5f), 1.7.
            "R17" =>
            [
                new(
                    "crouched legs b.up just above crouchHeight plus 0.5",
                    c => c.Hitboxes!.Crouched![2]!.B!.Up = float.BitIncrement(crouchedUpAtMost),
                    c => c.Hitboxes!.Crouched![2]!.B!.Up = crouchedUpAtMost,
                    new("hitboxes.crouched[2].b.up", "must be at most movement.crouchHeight plus 0.5")),
            ],

            // eyeHeightStanding < movement.standHeight (1.8).
            "R18" =>
            [
                new(
                    "eyeHeightStanding equal to standHeight",
                    c => c.Hitboxes!.EyeHeightStanding = movement.StandHeight,
                    c => c.Hitboxes!.EyeHeightStanding = float.BitDecrement(movement.StandHeight),
                    new("hitboxes.eyeHeightStanding", "must be less than movement.standHeight")),
            ],

            // eyeHeightCrouched < movement.crouchHeight (1.2).
            "R19" =>
            [
                new(
                    "eyeHeightCrouched equal to crouchHeight",
                    c => c.Hitboxes!.EyeHeightCrouched = movement.CrouchHeight,
                    c => c.Hitboxes!.EyeHeightCrouched = float.BitDecrement(movement.CrouchHeight),
                    new("hitboxes.eyeHeightCrouched", "must be less than movement.crouchHeight")),
            ],

            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unknown relation."),
        };
    }

    /// <summary>A shape case's edit of the placeholders and the errors it must give, in order.</summary>
    private static (Action<SimConfig> Edit, ConfigError[] Errors) Shape(string caseName)
    {
        var weapons = TestConfigs.Default().Weapons!;
        return caseName switch
        {
            // The step rate is out of range too, but the shape error ends the check.
            "movement-null-and-step-rate-0" => (
                c =>
                {
                    c.Movement = null;
                    c.StepRateHz = 0;
                },
                [new("movement", "is required")]),
            "zero-weapons" => (c => c.Weapons = [], [new("weapons", "must have between 1 and 8 entries")]),
            "nine-weapons" => (
                c => c.Weapons = [.. weapons, .. weapons, .. weapons, .. weapons, weapons[0]],
                [new("weapons", "must have between 1 and 8 entries")]),
            "zero-projectile-classes" => (c => c.ProjectileClasses = [], [new("projectileClasses", "must have between 1 and 8 entries")]),
            "two-standing-capsules" => (
                c => c.Hitboxes!.Standing = [c.Hitboxes.Standing![0], c.Hitboxes.Standing[1]],
                [new("hitboxes.standing", "must have exactly 3 entries")]),
            "standing-0-a-null" => (c => c.Hitboxes!.Standing![0]!.A = null, [new("hitboxes.standing[0].a", "is required")]),
            "weapon-1-null" => (c => c.Weapons![1] = null, [new("weapons[1]", "is required")]),

            // Every shape error is listed, in section order: world comes first and input last.
            "world-and-input-null" => (
                c =>
                {
                    c.World = null;
                    c.Input = null;
                },
                [new("world", "is required"), new("input", "is required")]),

            // A null list counts as a list with no entries: the count message, not "is required".
            "weapons-null" => (c => c.Weapons = null, [new("weapons", "must have between 1 and 8 entries")]),
            "standing-null" => (c => c.Hitboxes!.Standing = null, [new("hitboxes.standing", "must have exactly 3 entries")]),

            // The whole section absent: one error, and no capsule is read.
            "hitboxes-null" => (c => c.Hitboxes = null, [new("hitboxes", "is required")]),

            // A null capsule is the element's error alone: its points are not read.
            "crouched-1-null" => (c => c.Hitboxes!.Crouched![1] = null, [new("hitboxes.crouched[1]", "is required")]),
            "crouched-2-b-null" => (c => c.Hitboxes!.Crouched![2]!.B = null, [new("hitboxes.crouched[2].b", "is required")]),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
    }

    /// <summary>
    /// Writes <paramref name="value"/> into the leaf at <paramref name="path"/> (a camelCase JSON path such
    /// as <c>hitboxes.standing[0].a.fwd</c>) through the PascalCase public properties of
    /// <see cref="SimConfig"/> and its sections. An enum property takes the value's number.
    /// </summary>
    private static void SetLeaf(SimConfig config, string path, object value)
    {
        object target = config;
        var segments = path.Split('.');
        for (var s = 0; s < segments.Length; s++)
        {
            var match = SegmentPattern().Match(segments[s]);
            Assert.True(match.Success, $"{path}: segment {segments[s]} is not a property name with an optional index");
            var property = Property(target.GetType(), match.Groups["name"].Value, path);
            var isLeaf = s == segments.Length - 1 && !match.Groups["index"].Success;
            if (isLeaf)
            {
                var leafValue = property.PropertyType.IsEnum ? Enum.ToObject(property.PropertyType, value) : value;
                property.SetValue(target, leafValue);
                return;
            }

            var next = property.GetValue(target) ?? throw new InvalidOperationException($"{path}: {segments[s]} is null in the placeholders.");
            if (match.Groups["index"].Success)
            {
                var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
                next = ((IList)next)[index] ?? throw new InvalidOperationException($"{path}: {segments[s]} is null in the placeholders.");
            }

            target = next;
        }

        throw new InvalidOperationException($"{path} names no leaf property.");
    }

    private static PropertyInfo Property(Type type, string camelName, string path) =>
        type.GetProperty(char.ToUpperInvariant(camelName[0]) + camelName[1..], BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"{path}: {type.Name} has no public property for {camelName}.");

    /// <summary>
    /// The camelCase paths of every leaf (an <see cref="int"/>, <see cref="float"/> or enum property) of
    /// <paramref name="type"/>, with each collection element written as <c>[{index}]</c> and the two
    /// capsule lists as <c>{stance}</c>.
    /// </summary>
    private static void CollectLeaves(Type type, string prefix, List<string> leaves)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var name = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (propertyType == typeof(int) || propertyType == typeof(float) || propertyType.IsEnum)
            {
                leaves.Add(prefix + name);
                continue;
            }

            var element = ElementType(propertyType);
            if (element is not null)
            {
                var listName = type == typeof(HitboxesConfig) && (name == "standing" || name == "crouched") ? "{stance}" : name;
                CollectLeaves(element, $"{prefix}{listName}[{{index}}].", leaves);
                continue;
            }

            Assert.True(
                propertyType.IsClass && propertyType != typeof(string),
                $"{prefix}{name} is a {propertyType.Name}: neither a leaf (int, float or an enum), a section nor a list");
            CollectLeaves(propertyType, $"{prefix}{name}.", leaves);
        }
    }

    /// <summary>The element type of a collection property, or null for any other type.</summary>
    private static Type? ElementType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsArray)
        {
            return type.GetElementType();
        }

        var enumerable = type.GetInterfaces()
            .Concat(type.IsInterface ? [type] : [])
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    /// <summary>A path with every placeholder (<c>{i}</c>, <c>{k}</c>, <c>{c}</c>, <c>{stance}</c>, …) written as <c>{}</c>.</summary>
    private static string Placeholders(string path) => PlaceholderPattern().Replace(path, "{}");

    private static void AssertStrictlyAscending(int[] fieldIds, string label)
    {
        for (var k = 1; k < fieldIds.Length; k++)
        {
            Assert.True(fieldIds[k] > fieldIds[k - 1], $"{label}: fieldId 0x{fieldIds[k]:X4} at row {k} is not above 0x{fieldIds[k - 1]:X4}");
        }
    }

    private static int ParseInt(string text) => int.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

    private static float ParseFloat(string text) => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^(?<name>[A-Za-z]+)(\[(?<index>[0-9]+)\])?$")]
    private static partial Regex SegmentPattern();

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex PlaceholderPattern();

    /// <summary>One row of the contract's rule table: Min and Max as the contract prints them, null for an Enum row.</summary>
    private sealed record ContractRow(string Path, string Kind, string? Min, string? Max, int FieldId);

    /// <summary>A relation's violating edit, its boundary edit, and the error the violation gives.</summary>
    private sealed record RelationCheck(string Label, Action<SimConfig> Violate, Action<SimConfig> Boundary, ConfigError Error);
}
