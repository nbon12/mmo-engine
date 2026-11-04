using FluentValidation;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MmoEngine.Harness.Rules;

/// <summary>One accepted baseline: a rule's value for a network profile, with the run it came from (FR-014).</summary>
public sealed record Baseline(string RuleId, string Profile, double Value, string SourceRunId, DateTime SetAt);

/// <summary>
/// Baselines live in committed files (<c>harness/baselines/*.yaml</c>), so they change only through a
/// reviewed commit that names the source run. The runner reads them; it never writes them (FR-014).
/// </summary>
public static class BaselineFile
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    /// <summary>Loads every baseline file in a folder; a missing folder means no baselines. A (rule, profile) pair may appear once.</summary>
    public static IReadOnlyList<Baseline> LoadFolder(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return [];
        }

        var all = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".yaml", StringComparison.Ordinal) || f.EndsWith(".yml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .SelectMany(f => Parse(File.ReadAllText(f), Path.GetFileName(f)))
            .ToList();
        var duplicate = all.GroupBy(b => (b.RuleId, b.Profile)).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new RuleFileException($"Baseline for rule '{duplicate.Key.RuleId}' and profile '{duplicate.Key.Profile}' is defined more than once.");
        }

        return all;
    }

    /// <summary>Parses one baseline file: a YAML list of entries.</summary>
    public static IReadOnlyList<Baseline> Parse(string yaml, string fileName)
    {
        List<BaselineDto>? entries;
        try
        {
            entries = Deserializer.Deserialize<List<BaselineDto>>(yaml);
        }
        catch (YamlException ex)
        {
            throw new RuleFileException($"{fileName}: invalid YAML or unknown field ({ex.Message})");
        }

        var validator = new BaselineValidator();
        var result = new List<Baseline>();
        foreach (var (entry, i) in (entries ?? []).Select((e, i) => (e, i)))
        {
            var validation = validator.Validate(entry);
            if (!validation.IsValid)
            {
                var error = validation.Errors[0];
                throw new RuleFileException($"{fileName}: entry {i}: {error.PropertyName}: {error.ErrorMessage}");
            }

            result.Add(new Baseline(entry.RuleId!, entry.Profile!, entry.Value!.Value, entry.SourceRunId!,
                DateTime.SpecifyKind(entry.SetAt!.Value, DateTimeKind.Utc)));
        }

        return result;
    }

    /// <summary>The baseline for a rule and profile, or null.</summary>
    public static double? Find(IReadOnlyList<Baseline> baselines, string ruleId, string profile)
    {
        ArgumentNullException.ThrowIfNull(baselines);
        return baselines.FirstOrDefault(b => b.RuleId == ruleId && b.Profile == profile)?.Value;
    }

    /// <summary>The <c>baselines</c> table columns.</summary>
    public static IReadOnlyList<string> TableColumns { get; } = ["rule_id", "profile", "value", "source_run_id", "set_at"];

    /// <summary>
    /// Mirrors the committed baselines into the <c>baselines</c> table, for dashboards and trend queries. The
    /// table is a copy: the rule runner reads the committed files, so the table can never change a verdict.
    /// </summary>
    public static Task<long> SyncAsync(Store.IHarnessStore store, IReadOnlyList<Baseline> baselines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(baselines);
        return store.InsertAsync("baselines", TableColumns,
            baselines.Select(b => new object?[] { b.RuleId, b.Profile, b.Value, b.SourceRunId, b.SetAt }), cancellationToken);
    }

    internal sealed class BaselineDto
    {
        public string? RuleId { get; set; }
        public string? Profile { get; set; }
        public double? Value { get; set; }
        public string? SourceRunId { get; set; }
        public DateTime? SetAt { get; set; }
    }

    private sealed class BaselineValidator : AbstractValidator<BaselineDto>
    {
        public BaselineValidator()
        {
            RuleFor(b => b.RuleId).NotEmpty().OverridePropertyName("rule_id");
            RuleFor(b => b.Profile).NotEmpty().OverridePropertyName("profile");
            RuleFor(b => b.Value).NotNull().Must(v => v is null || double.IsFinite(v.Value)).OverridePropertyName("value");
            RuleFor(b => b.SourceRunId).NotEmpty().OverridePropertyName("source_run_id").WithMessage("must name the run the baseline came from");
            RuleFor(b => b.SetAt).NotNull().OverridePropertyName("set_at");
        }
    }
}
