using System.Globalization;
using FluentValidation;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MmoEngine.Harness.Rules;

/// <summary>A rule file the runner refuses (FR-010). The message names the file and the field.</summary>
public sealed class RuleFileException(string message) : Exception(message);

/// <summary>Parses and validates rule files (contracts/rule-file.md).</summary>
public static class RuleFileParser
{
    /// <summary>The run kinds a rule may name.</summary>
    public static IReadOnlyList<string> KnownKinds { get; } = ["simulator", "sweep-cell", "probe", "bot", "golden"];

    /// <summary>The query parameter every rule must use.</summary>
    public const string RunIdParameter = "{run_id:String}";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>Loads every <c>*.yaml</c> and <c>*.yml</c> file in a folder, in file-name order. IDs must be unique.</summary>
    public static IReadOnlyList<RuleDefinition> LoadFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new RuleFileException($"Rules folder not found: {folder}");
        }

        var rules = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(f => Parse(File.ReadAllText(f), Path.GetFileName(f)))
            .ToList();
        var duplicate = rules.GroupBy(r => r.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new RuleFileException($"Rule id '{duplicate.Key}' is defined in more than one file: {string.Join(", ", duplicate.Select(r => r.File))}");
        }

        return rules;
    }

    /// <summary>Parses one rule file's text.</summary>
    public static RuleDefinition Parse(string yaml, string fileName)
    {
        RuleFileDto dto;
        try
        {
            dto = Deserializer.Deserialize<RuleFileDto>(yaml) ?? throw new RuleFileException($"{fileName}: the file is empty.");
        }
        catch (YamlException ex)
        {
            throw new RuleFileException($"{fileName}: invalid YAML or unknown field ({ex.Message})");
        }

        var validation = new RuleFileValidator().Validate(dto);
        if (!validation.IsValid)
        {
            var error = validation.Errors[0];
            throw new RuleFileException($"{fileName}: {error.PropertyName}: {error.ErrorMessage}");
        }

        var mode = ParseMode(dto.Mode!);
        return new RuleDefinition
        {
            Id = dto.Id!,
            File = fileName,
            Description = dto.Description ?? "",
            AppliesTo = dto.AppliesTo!,
            Query = dto.Query!,
            Mode = mode,
            MinSamples = dto.MinSamples ?? 0,
            Threshold = dto.Threshold ?? 0,
            Comparison = mode == RuleMode.Fixed ? ParseComparison(dto.Comparison ?? "<=") : Comparison.LessOrEqual,
            MaxRegression = mode == RuleMode.Baseline ? ParsePercent(dto.MaxRegression!) : 0,
            Direction = mode == RuleMode.Baseline ? ParseDirection(dto.Direction ?? "lower_is_better") : Direction.LowerIsBetter,
        };
    }

    internal static RuleMode ParseMode(string text) => text switch
    {
        "fixed" => RuleMode.Fixed,
        "baseline" => RuleMode.Baseline,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "unknown mode"),
    };

    internal static Comparison ParseComparison(string text) => text switch
    {
        "<=" => Comparison.LessOrEqual,
        "<" => Comparison.Less,
        ">=" => Comparison.GreaterOrEqual,
        ">" => Comparison.Greater,
        "=" or "==" => Comparison.Equal,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "unknown comparison"),
    };

    internal static Direction ParseDirection(string text) => text switch
    {
        "lower_is_better" => Direction.LowerIsBetter,
        "higher_is_better" => Direction.HigherIsBetter,
        _ => throw new ArgumentOutOfRangeException(nameof(text), text, "unknown direction"),
    };

    /// <summary>"10%" or "0.1" to 0.1.</summary>
    internal static double ParsePercent(string text)
    {
        var trimmed = text.Trim();
        var percent = trimmed.EndsWith('%');
        var number = double.Parse(percent ? trimmed[..^1] : trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
        return percent ? number / 100 : number;
    }

    internal static bool IsPercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        var number = trimmed.EndsWith('%') ? trimmed[..^1] : trimmed;
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && double.IsFinite(v);
    }

    /// <summary>The YAML shape of a rule file.</summary>
    internal sealed class RuleFileDto
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public List<string>? AppliesTo { get; set; }
        public ulong? MinSamples { get; set; }
        public string? Query { get; set; }
        public string? Mode { get; set; }
        public double? Threshold { get; set; }
        public string? Comparison { get; set; }
        public string? MaxRegression { get; set; }
        public string? Direction { get; set; }
    }

    private sealed class RuleFileValidator : AbstractValidator<RuleFileDto>
    {
        public RuleFileValidator()
        {
            RuleFor(r => r.Id).NotEmpty().Matches("^[a-z0-9_.-]+$").OverridePropertyName("id")
                .WithMessage("must be lowercase letters, digits, '.', '_' or '-'");
            RuleFor(r => r.AppliesTo).NotEmpty().OverridePropertyName("applies_to").WithMessage("must list at least one run kind");
            RuleForEach(r => r.AppliesTo).Must(k => KnownKinds.Contains(k)).OverridePropertyName("applies_to")
                .WithMessage(k => $"must be one of {string.Join(", ", KnownKinds)}");
            RuleFor(r => r.Query).NotEmpty().OverridePropertyName("query").WithMessage("is required");
            RuleFor(r => r.Query).Must(q => q!.Contains(RunIdParameter, StringComparison.Ordinal)).When(r => !string.IsNullOrEmpty(r.Query))
                .OverridePropertyName("query").WithMessage($"must filter on the {RunIdParameter} parameter");
            RuleFor(r => r.Mode).NotEmpty().Must(m => m is "fixed" or "baseline").OverridePropertyName("mode").WithMessage("must be 'fixed' or 'baseline'");
            When(r => r.Mode == "fixed", () =>
            {
                RuleFor(r => r.Threshold).NotNull().OverridePropertyName("threshold").WithMessage("is required in fixed mode");
                RuleFor(r => r.Comparison).Must(c => c is null or "<=" or "<" or ">=" or ">" or "=" or "==").OverridePropertyName("comparison")
                    .WithMessage("must be one of <=, <, >=, >, =");
                RuleFor(r => r.MaxRegression).Null().OverridePropertyName("max_regression").WithMessage("is only allowed in baseline mode");
            });
            When(r => r.Mode == "baseline", () =>
            {
                RuleFor(r => r.MaxRegression).Must(IsPercent).OverridePropertyName("max_regression")
                    .WithMessage("is required in baseline mode, as a non-negative percentage such as 10%");
                RuleFor(r => r.Direction).Must(d => d is null or "lower_is_better" or "higher_is_better").OverridePropertyName("direction")
                    .WithMessage("must be lower_is_better or higher_is_better");
                RuleFor(r => r.Threshold).Null().OverridePropertyName("threshold").WithMessage("is only allowed in fixed mode");
            });
        }
    }
}
