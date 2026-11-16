using System;
using System.Collections.Generic;
using System.Globalization;

namespace MmoEngine.SimCore.Config;

/// <summary>
/// The check every build runs on a <see cref="SimConfig"/> before any step
/// (FR-029, specs/015-shared-simulation-core/contracts/config-rules.md#check-order-and-the-error-list).
/// The server's FluentValidation adapter and the browser's config loader report its list unchanged, so
/// there is one rule table.
/// </summary>
public static class ConfigGuard
{
    private const string Required = "is required";
    private const string OneToEightEntries = "must have between 1 and 8 entries";
    private const string ThreeEntries = "must have exactly 3 entries";
    private const string MustBeFinite = "must be finite";

    /// <summary>
    /// Every error of <paramref name="config"/>, in the contract's fixed order, each keyed by its camelCase
    /// JSON path. Empty when the configuration is valid. It never throws for a value.
    /// <list type="number">
    /// <item><description>Shape: every missing section, list element and capsule point, and every list
    /// with the wrong number of entries, in section order. Any shape error ends the check, because the
    /// missing parts cannot be read, so the list then holds only shape errors.</description></item>
    /// <item><description>Fields, every row of <see cref="ConfigFields.Expand"/> for the configuration's
    /// weapon and projectile-class counts, in ascending fieldId. A float that is not finite reports only
    /// <c>must be finite</c>.</description></item>
    /// <item><description>Relations R01 to R19, each over its instances in ascending order, only when
    /// the shape and every field passed.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<ConfigError> Check(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<ConfigError>();
        CheckShape(config, errors);
        if (errors.Count != 0)
        {
            return errors.AsReadOnly();
        }

        CheckFields(config, errors);
        if (errors.Count != 0)
        {
            return errors.AsReadOnly();
        }

        ConfigRelations.Check(config, errors);
        return errors.AsReadOnly();
    }

    /// <summary>
    /// The shape errors alone, step 1 of <see cref="Check"/>: empty when every section, list element and
    /// capsule point is present and every list has an allowed number of entries, so every field can be read.
    /// </summary>
    internal static IReadOnlyList<ConfigError> ShapeErrors(SimConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<ConfigError>();
        CheckShape(config, errors);
        return errors.AsReadOnly();
    }

    /// <summary>The shape rules of contracts/config-rules.md#shape, in section order.</summary>
    private static void CheckShape(SimConfig config, List<ConfigError> errors)
    {
        if (config.World is null)
        {
            errors.Add(new ConfigError("world", Required));
        }

        if (config.Movement is null)
        {
            errors.Add(new ConfigError("movement", Required));
        }

        CheckList(config.Weapons, "weapons", 1, ConfigFields.MaxInstances, OneToEightEntries, errors);
        CheckList(config.ProjectileClasses, "projectileClasses", 1, ConfigFields.MaxInstances, OneToEightEntries, errors);

        if (config.Hitboxes is null)
        {
            errors.Add(new ConfigError("hitboxes", Required));
        }
        else
        {
            for (var s = 0; s < ConfigFields.StanceCount; s++)
            {
                var capsules = s == 0 ? config.Hitboxes.Standing : config.Hitboxes.Crouched;
                var path = "hitboxes." + ConfigFields.StanceName(s);
                CheckList(capsules, path, ConfigFields.CapsulesPerStance, ConfigFields.CapsulesPerStance, ThreeEntries, errors);
                if (capsules is null)
                {
                    continue;
                }

                for (var c = 0; c < capsules.Count; c++)
                {
                    var capsule = capsules[c];
                    if (capsule is null)
                    {
                        continue;
                    }

                    if (capsule.A is null)
                    {
                        errors.Add(new ConfigError(ElementPath(path, c) + ".a", Required));
                    }

                    if (capsule.B is null)
                    {
                        errors.Add(new ConfigError(ElementPath(path, c) + ".b", Required));
                    }
                }
            }
        }

        if (config.Input is null)
        {
            errors.Add(new ConfigError("input", Required));
        }
    }

    /// <summary>
    /// A list's entry count, a null list counting as no entries, then each element that is null, in
    /// index order.
    /// </summary>
    private static void CheckList<T>(List<T?>? list, string path, int min, int max, string countMessage, List<ConfigError> errors)
        where T : class
    {
        var count = list?.Count ?? 0;
        if (count < min || count > max)
        {
            errors.Add(new ConfigError(path, countMessage));
        }

        if (list is null)
        {
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
            {
                errors.Add(new ConfigError(ElementPath(path, i), Required));
            }
        }
    }

    /// <summary>Every field of every present instance, in ascending fieldId, against its kind's checks.</summary>
    private static void CheckFields(SimConfig config, List<ConfigError> errors)
    {
        foreach (var field in ConfigFields.Expand(config.Weapons!.Count, config.ProjectileClasses!.Count))
        {
            var template = field.Template;
            var bits = field.GetBits(config);
            var message = template.Kind switch
            {
                ConfigFieldKind.Int => IntMessage(bits, template),
                ConfigFieldKind.Float => FloatMessage(bits, template),
                ConfigFieldKind.Enum => (FireMode)bits is FireMode.Auto or FireMode.Semi ? null : template.RangeMessage,
                _ => throw new InvalidOperationException($"Field {field.Path} has an unknown kind {template.Kind}."),
            };
            if (message is not null)
            {
                errors.Add(new ConfigError(field.Path, message));
            }
        }
    }

    /// <summary>Null when min ≤ v ≤ max; otherwise <c>must be between {min} and {max}</c>.</summary>
    private static string? IntMessage(uint bits, ConfigField template)
    {
        var value = unchecked((int)bits);
        return value >= unchecked((int)template.MinBits) && value <= unchecked((int)template.MaxBits) ? null : template.RangeMessage;
    }

    /// <summary>
    /// <c>must be finite</c> for a NaN or an infinity, and only that; otherwise null when min ≤ v ≤ max,
    /// and <c>must be between {min} and {max}</c> when not.
    /// </summary>
    private static string? FloatMessage(uint bits, ConfigField template)
    {
        var value = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(value))
        {
            return MustBeFinite;
        }

        var inRange = value >= BitConverter.UInt32BitsToSingle(template.MinBits) && value <= BitConverter.UInt32BitsToSingle(template.MaxBits);
        return inRange ? null : template.RangeMessage;
    }

    private static string ElementPath(string path, int index) =>
        string.Concat(path, "[", index.ToString(CultureInfo.InvariantCulture), "]");
}
