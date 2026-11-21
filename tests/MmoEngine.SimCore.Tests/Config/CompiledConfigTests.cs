using System.Collections;
using System.Globalization;
using System.Reflection;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Config;

/// <summary>
/// <c>CompiledConfig.Create</c>: the contract's conversions at the placeholders, ties-to-even angle
/// units, and the guard's errors (contracts/config-rules.md#compile-time-conversions,
/// contracts/state-serialization.md#maximum-length).
/// </summary>
public sealed class CompiledConfigTests
{
    /// <summary>The number of fields of <c>simcore/config/default.json</c> (contracts/config-rules.md#field-ids).</summary>
    private const int DefaultFieldCount = 116;

    [Fact]
    public void PlaceholderConversionsAtThirtyHzMatchTheContract()
    {
        var input = TestConfigs.At(30);

        var compiled = CompiledConfig.Create(input);

        Assert.Equal(1800, compiled.ShotCost);
        Assert.Equal(600, compiled.Weapons[0].Rpm);
        Assert.Equal(30, compiled.Weapons[1].Rpm);
        Assert.Equal(60, compiled.Weapons[0].ReloadSteps);
        Assert.Equal(105, compiled.Weapons[1].ReloadSteps);
        Assert.Equal(15, compiled.Weapons[0].EquipSteps);
        Assert.Equal(23, compiled.Weapons[1].EquipSteps);
        Assert.Equal(8, compiled.Weapons[0].HeatCooldownDelaySteps);
        Assert.Equal(8, compiled.Weapons[1].HeatCooldownDelaySteps);
        Assert.Equal(3, compiled.Weapons[0].RecoilRecoveryDelaySteps);
        Assert.Equal(3, compiled.Weapons[1].RecoilRecoveryDelaySteps);
        Assert.Equal(90, compiled.LifetimeSteps[0]);
        Assert.Equal(240, compiled.LifetimeSteps[1]);

        // 24 + 64·(67 + 30·2 + 25·1) + 80·1024, and that plus the step record's 16 bytes.
        Assert.Equal(91_672, compiled.MaxStateLength);
        Assert.Equal(91_688, compiled.MaxStepRecordLength);

        // Config is a deep copy: equal field for field, and no object of it is the input's.
        var leaves = 0;
        AssertCopiedFieldForField(input, compiled.Config, "config", ref leaves);
        Assert.Equal(DefaultFieldCount, leaves);
    }

    [Fact]
    public void AngleUnitsRoundTiesToEven()
    {
        // Just below half a unit: (d · 65536f) / 360f is 0.49999997f (0x3EFFFFFF). floor(x + 0.5f)
        // would give 1 here, because 0.49999997f + 0.5f rounds up to 1 (research R9).
        var justBelowHalf = FindKickGivingJustBelowHalfAUnit();
        Assert.Equal(1f, Units(justBelowHalf) + 0.5f);
        Assert.Equal(0, KickPitchUnits(justBelowHalf));

        // Exactly half a unit rounds to even, 0; exactly one and a half rounds to even, 2.
        const float half = 0.00274658203125f;
        const float oneAndAHalf = 0.00823974609375f;
        Assert.Equal(0.5f, Units(half));
        Assert.Equal(0, KickPitchUnits(half));
        Assert.Equal(1.5f, Units(oneAndAHalf));
        Assert.Equal(2, KickPitchUnits(oneAndAHalf));

        // A recovery rate above 0 that rounds to 0 units per step is raised to 1; a rate of 0 stays 0.
        Assert.InRange(Units(0.001f) / 60f, 0f, 0.49f);
        Assert.Equal(1, RecoveryPerStepAtSixtyHz(0.001f));
        Assert.Equal(0, RecoveryPerStepAtSixtyHz(0f));
    }

    [Fact]
    public void CreateThrowsTheGuardsErrors()
    {
        var config = TestConfigs.With(c =>
        {
            c.StepRateHz = 61;
            c.Movement!.Gravity = float.NaN;
        });

        var exception = Assert.Throws<ConfigValidationException>(() => CompiledConfig.Create(config));

        ConfigError[] expected =
        [
            new("stepRateHz", "must be between 20 and 60"),
            new("movement.gravity", "must be finite"),
        ];
        Assert.Equal(expected, exception.Errors);
        Assert.Equal(ConfigGuard.Check(config), exception.Errors);
        Assert.StartsWith("The configuration is invalid: ", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>The contract's angle-unit argument, <c>(deg * 65536f) / 360f</c>, in float.</summary>
    private static float Units(float degrees) => (degrees * 65536f) / 360f;

    /// <summary>
    /// The first <c>recoilKickPitchDeg</c> at or above 0.00274658f, searching up with BitIncrement,
    /// whose angle-unit argument is 0x3EFFFFFF.
    /// </summary>
    private static float FindKickGivingJustBelowHalfAUnit()
    {
        var degrees = 0.00274658f;
        for (var step = 0; step < 1024; step++)
        {
            if (BitConverter.SingleToUInt32Bits(Units(degrees)) == 0x3EFFFFFFu)
            {
                return degrees;
            }

            degrees = float.BitIncrement(degrees);
        }

        throw new InvalidOperationException("No recoilKickPitchDeg from 0.00274658f up gives the angle-unit argument 0x3EFFFFFF.");
    }

    private static int KickPitchUnits(float degrees) =>
        CompiledConfig.Create(TestConfigs.With(c => c.Weapons![0]!.RecoilKickPitchDeg = degrees)).Weapons[0].RecoilKickPitchUnits;

    private static int RecoveryPerStepAtSixtyHz(float degreesPerSecond) =>
        CompiledConfig.Create(TestConfigs.With(c =>
        {
            c.StepRateHz = 60;
            c.Weapons![0]!.RecoilRecoveryDegPerSecond = degreesPerSecond;
        })).Weapons[0].RecoilRecoveryPerStep;

    /// <summary>
    /// Walks the public properties of <paramref name="expected"/> and <paramref name="actual"/> together.
    /// Leaves (numbers and enums) must be equal, floats bit for bit; every section, list and element must
    /// be a different instance from the input's. <paramref name="leaves"/> counts the leaves compared.
    /// </summary>
    private static void AssertCopiedFieldForField(object? expected, object? actual, string path, ref int leaves)
    {
        if (expected is null || actual is null || IsLeaf(expected))
        {
            leaves++;
            Assert.Equal($"{path} = {Describe(expected)}", $"{path} = {Describe(actual)}");
            return;
        }

        Assert.Equal($"{path}: a copy", ReferenceEquals(expected, actual) ? $"{path}: the input's own instance" : $"{path}: a copy");
        Assert.Equal($"{path} is a {expected.GetType()}", $"{path} is a {actual.GetType()}");

        if (expected is IList expectedList)
        {
            var actualList = (IList)actual;
            Assert.Equal($"{path} has {expectedList.Count} entries", $"{path} has {actualList.Count} entries");
            for (var i = 0; i < expectedList.Count; i++)
            {
                AssertCopiedFieldForField(expectedList[i], actualList[i], $"{path}[{i}]", ref leaves);
            }

            return;
        }

        foreach (var property in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            var name = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
            AssertCopiedFieldForField(property.GetValue(expected), property.GetValue(actual), $"{path}.{name}", ref leaves);
        }
    }

    private static bool IsLeaf(object value) => value.GetType().IsPrimitive || value is Enum || value is string;

    private static string Describe(object? value) => value switch
    {
        null => "null",
        float f => $"float 0x{BitConverter.SingleToUInt32Bits(f):X8}",
        IFormattable formattable => $"{value.GetType().Name} {formattable.ToString(null, CultureInfo.InvariantCulture)}",
        _ => $"{value.GetType().Name} {value}",
    };
}
