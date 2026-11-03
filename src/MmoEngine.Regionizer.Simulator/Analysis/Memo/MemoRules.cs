using System.Text.Json;

namespace MmoEngine.Regionizer.Simulator.Analysis.Memo;

/// <summary>How a value is read off its curve (research D15).</summary>
public enum MemoRuleKind
{
    /// <summary>The largest radius at least N sweep steps below collapse and at or above the analytic floor.</summary>
    ClaimRadius,

    /// <summary>The smallest x whose metric is within the threshold in every seed (and series), plus a margin.</summary>
    SmallestWithin,

    /// <summary>The largest x whose metric is within the threshold in every seed (and series), plus a margin.</summary>
    LargestWithin,

    /// <summary>The cell with the lowest mean metric, among cells that meet an optional constraint in every seed.</summary>
    Lowest,

    /// <summary>Adopt the candidate setting only if the metric drops by at least a percentage and a guard metric does not rise.</summary>
    AdoptIfDrop,

    /// <summary>A reading from one cell: the mean, min and max over seeds.</summary>
    Reading,
}

/// <summary>
/// A metric read from each run's <c>summary.json</c>: the sum of the numbers at <see cref="Paths"/> (a path
/// that names an object sums its numeric properties), divided by the number at <see cref="DivideBy"/> if
/// given. With <see cref="AbsentAsZero"/>, a missing last property of an existing object counts as 0.
/// </summary>
public sealed record MetricSpec(IReadOnlyList<string> Paths, string? DivideBy, bool AbsentAsZero)
{
    public string Label => string.Join(" + ", Paths) + (DivideBy is null ? string.Empty : $" / {DivideBy}");
}

/// <summary>A metric that every seed must keep within a threshold (below it, or at most it when inclusive).</summary>
public sealed record MetricLimit(MetricSpec Metric, double Threshold, bool Inclusive)
{
    public bool Allows(double value) => Inclusive ? value <= Threshold : value < Threshold;

    public string Operator => Inclusive ? "≤" : "<";
}

/// <summary>The analytic claim-radius floor's inputs for a claim-radius row (placeholders until game design supplies them).</summary>
public sealed record FloorInputs(double InteractionRangeMetres, double ClosingSpeedMetresPerSecond, double TickRateHz)
{
    public double Floor => AnalyticFormulas.ClaimRadiusFloor(InteractionRangeMetres, ClosingSpeedMetresPerSecond, TickRateHz);
}

/// <summary>One value the memo recommends, and how to read it.</summary>
/// <param name="Sweeps">The sweep folders the value is read from.</param>
/// <param name="Parameter">The swept JSON path that is the curve's x, or null to use the cells' only parameter.</param>
/// <param name="Limit">The threshold for <see cref="MemoRuleKind.SmallestWithin"/> and <see cref="MemoRuleKind.LargestWithin"/>.</param>
/// <param name="AddMargin">Added to the value read off the curve (the sprawl margin for base spacing).</param>
/// <param name="CollapseAt">The mean metric at which a claim radius collapses fights together.</param>
/// <param name="Constraint">For <see cref="MemoRuleKind.Lowest"/>: a limit every seed of a candidate must meet.</param>
/// <param name="Baseline">For <see cref="MemoRuleKind.AdoptIfDrop"/>: the current setting.</param>
/// <param name="Candidate">For <see cref="MemoRuleKind.AdoptIfDrop"/>: the setting to adopt or not.</param>
/// <param name="Guard">For <see cref="MemoRuleKind.AdoptIfDrop"/>: a metric whose mean must not rise.</param>
/// <param name="OpenDecision">The constitution open decision the value bears on, if any.</param>
public sealed record ValueRule(
    string Id,
    string Title,
    MemoRuleKind Rule,
    IReadOnlyList<string> Sweeps,
    string? Parameter,
    string Unit,
    MetricSpec Metric,
    string Description,
    MetricLimit? Limit,
    double AddMargin,
    double? CollapseAt,
    int StepsBelowCollapse,
    FloorInputs? Floor,
    MetricLimit? Constraint,
    string? Baseline,
    string? Candidate,
    double? MinimumDropPercent,
    MetricSpec? Guard,
    int? OpenDecision);

/// <summary>
/// The memo rules (<c>simulator/sweeps/memo-rules.json</c>): which sweep and metric each value reads, and
/// its margins and thresholds. Loading validates every field and names the bad one.
/// </summary>
public sealed class MemoRules
{
    /// <summary>The rules file's format version.</summary>
    public const int SupportedFormatVersion = 1;

    private const string Root = "$";

    private MemoRules(IReadOnlyList<ValueRule> values) => Values = values;

    /// <summary>The values in memo order.</summary>
    public IReadOnlyList<ValueRule> Values { get; }

    /// <summary>Reads and validates a rules file. Throws <see cref="InvalidDataException"/> naming the bad field.</summary>
    public static MemoRules Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Parses and validates rules JSON. Throws <see cref="InvalidDataException"/> naming the bad field.</summary>
    public static MemoRules Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"memo rules: not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Bad(Root, "must be an object");
            }

            KnownFields(root, Root, "formatVersion", "description", "values");
            var version = RequiredNumber(root, "formatVersion", Root);
            if (version != SupportedFormatVersion)
            {
                throw Bad("formatVersion", $"must be {SupportedFormatVersion}, was {version}");
            }

            var values = RequiredArray(root, "values", Root);
            var rules = new List<ValueRule>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                var at = $"values[{index++}]";
                var rule = ParseValue(value, at);
                if (!ids.Add(rule.Id))
                {
                    throw Bad($"{at}.id", $"duplicate id '{rule.Id}'");
                }

                rules.Add(rule);
            }

            if (rules.Count == 0)
            {
                throw Bad("values", "must list at least one value");
            }

            return new MemoRules(rules);
        }
    }

    private static ValueRule ParseValue(JsonElement value, string at)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Bad(at, "must be an object");
        }

        KnownFields(
            value, at, "id", "title", "rule", "sweep", "sweeps", "parameter", "unit", "metric", "description", "threshold", "inclusive",
            "addMargin", "collapseAt", "stepsBelowCollapse", "floor", "constraint", "baseline", "candidate", "minimumDropPercent", "guard",
            "openDecision");
        var id = RequiredString(value, "id", at);
        var kindText = RequiredString(value, "rule", at);
        if (!Enum.TryParse<MemoRuleKind>(kindText, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
        {
            throw Bad($"{at}.rule", $"unknown rule '{kindText}'; expected one of {string.Join(", ", Enum.GetNames<MemoRuleKind>())}");
        }

        var sweeps = Sweeps(value, at);
        var metric = Metric(value, "metric", at) ?? throw Bad($"{at}.metric", "is required");
        var rule = new ValueRule(
            Id: id,
            Title: OptionalString(value, "title", at) ?? id,
            Rule: kind,
            Sweeps: sweeps,
            Parameter: OptionalString(value, "parameter", at),
            Unit: OptionalString(value, "unit", at) ?? string.Empty,
            Metric: metric,
            Description: OptionalString(value, "description", at) ?? string.Empty,
            Limit: value.TryGetProperty("threshold", out _)
                ? new MetricLimit(metric, RequiredNumber(value, "threshold", at), OptionalBool(value, "inclusive", at) ?? false)
                : null,
            AddMargin: OptionalNumber(value, "addMargin", at) ?? 0,
            CollapseAt: OptionalNumber(value, "collapseAt", at),
            StepsBelowCollapse: OptionalInt(value, "stepsBelowCollapse", at) ?? 1,
            Floor: Floor(value, at),
            Constraint: Constraint(value, at),
            Baseline: OptionalLabel(value, "baseline", at),
            Candidate: OptionalLabel(value, "candidate", at),
            MinimumDropPercent: OptionalNumber(value, "minimumDropPercent", at),
            Guard: Metric(value, "guard", at),
            OpenDecision: OptionalInt(value, "openDecision", at));

        Validate(rule, at);
        return rule;
    }

    private static void Validate(ValueRule rule, string at)
    {
        switch (rule.Rule)
        {
            case MemoRuleKind.ClaimRadius:
                _ = rule.CollapseAt ?? throw Bad($"{at}.collapseAt", "is required for ClaimRadius");
                _ = rule.Floor ?? throw Bad($"{at}.floor", "is required for ClaimRadius");
                if (rule.StepsBelowCollapse < 1)
                {
                    throw Bad($"{at}.stepsBelowCollapse", "must be at least 1");
                }

                break;
            case MemoRuleKind.SmallestWithin or MemoRuleKind.LargestWithin:
                _ = rule.Limit ?? throw Bad($"{at}.threshold", $"is required for {rule.Rule}");
                break;
            case MemoRuleKind.AdoptIfDrop:
                _ = rule.Parameter ?? throw Bad($"{at}.parameter", "is required for AdoptIfDrop");
                _ = rule.Baseline ?? throw Bad($"{at}.baseline", "is required for AdoptIfDrop");
                _ = rule.Candidate ?? throw Bad($"{at}.candidate", "is required for AdoptIfDrop");
                _ = rule.MinimumDropPercent ?? throw Bad($"{at}.minimumDropPercent", "is required for AdoptIfDrop");
                break;
        }
    }

    private static IReadOnlyList<string> Sweeps(JsonElement value, string at)
    {
        if (value.TryGetProperty("sweep", out var one))
        {
            return [NonEmpty(one, $"{at}.sweep")];
        }

        var many = RequiredArray(value, "sweeps", at, "a 'sweep' or 'sweeps' is required");
        var names = many.EnumerateArray().Select((e, i) => NonEmpty(e, $"{at}.sweeps[{i}]")).ToList();
        return names.Count > 0 ? names : throw Bad($"{at}.sweeps", "must name at least one sweep");
    }

    private static MetricSpec? Metric(JsonElement value, string name, string at)
    {
        if (!value.TryGetProperty(name, out var metric))
        {
            return null;
        }

        var path = PathOf(at, name);
        if (metric.ValueKind != JsonValueKind.Object)
        {
            throw Bad(path, "must be an object with 'paths'");
        }

        KnownFields(metric, path, "paths", "divideBy", "absentAsZero");
        var paths = RequiredArray(metric, "paths", path).EnumerateArray().Select((e, i) => NonEmpty(e, $"{path}.paths[{i}]")).ToList();
        if (paths.Count == 0)
        {
            throw Bad($"{path}.paths", "must list at least one summary path");
        }

        return new MetricSpec(paths, OptionalString(metric, "divideBy", path), OptionalBool(metric, "absentAsZero", path) ?? false);
    }

    private static MetricLimit? Constraint(JsonElement value, string at)
    {
        if (!value.TryGetProperty("constraint", out var constraint))
        {
            return null;
        }

        var path = $"{at}.constraint";
        if (constraint.ValueKind != JsonValueKind.Object)
        {
            throw Bad(path, "must be an object");
        }

        KnownFields(constraint, path, "metric", "threshold", "inclusive");
        var metric = Metric(constraint, "metric", path) ?? throw Bad($"{path}.metric", "is required");
        return new MetricLimit(metric, RequiredNumber(constraint, "threshold", path), OptionalBool(constraint, "inclusive", path) ?? false);
    }

    private static FloorInputs? Floor(JsonElement value, string at)
    {
        if (!value.TryGetProperty("floor", out var floor))
        {
            return null;
        }

        var path = $"{at}.floor";
        if (floor.ValueKind != JsonValueKind.Object)
        {
            throw Bad(path, "must be an object");
        }

        KnownFields(floor, path, "interactionRangeMetres", "closingSpeedMetresPerSecond", "tickRateHz");
        var inputs = new FloorInputs(
            RequiredNumber(floor, "interactionRangeMetres", path),
            RequiredNumber(floor, "closingSpeedMetresPerSecond", path),
            RequiredNumber(floor, "tickRateHz", path));
        try
        {
            _ = inputs.Floor;
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw Bad(path, e.Message);
        }

        return inputs;
    }

    private static string? OptionalLabel(JsonElement value, string name, string at) =>
        value.TryGetProperty(name, out var label)
            ? label.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null
                ? throw Bad(PathOf(at, name), "must be a string, number or boolean")
                : SweepResults.Label(label)
            : null;

    private static string RequiredString(JsonElement value, string name, string at) =>
        value.TryGetProperty(name, out var text) ? NonEmpty(text, PathOf(at, name)) : throw Bad(PathOf(at, name), "is required");

    private static string? OptionalString(JsonElement value, string name, string at) =>
        value.TryGetProperty(name, out var text) ? NonEmpty(text, PathOf(at, name)) : null;

    private static string NonEmpty(JsonElement text, string path) =>
        text.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(text.GetString())
            ? text.GetString()!
            : throw Bad(path, "must be a non-empty string");

    private static double RequiredNumber(JsonElement value, string name, string at) =>
        OptionalNumber(value, name, at) ?? throw Bad(PathOf(at, name), "is required");

    private static double? OptionalNumber(JsonElement value, string name, string at)
    {
        if (!value.TryGetProperty(name, out var number))
        {
            return null;
        }

        return number.ValueKind == JsonValueKind.Number && double.IsFinite(number.GetDouble())
            ? number.GetDouble()
            : throw Bad(PathOf(at, name), "must be a number");
    }

    private static int? OptionalInt(JsonElement value, string name, string at) =>
        OptionalNumber(value, name, at) is not { } number ? null
            : number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue ? (int)number
            : throw Bad(PathOf(at, name), "must be a whole number");

    private static bool? OptionalBool(JsonElement value, string name, string at) =>
        value.TryGetProperty(name, out var flag)
            ? flag.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw Bad(PathOf(at, name), "must be true or false"),
            }
            : null;

    private static JsonElement RequiredArray(JsonElement value, string name, string at, string? missing = null)
    {
        var path = PathOf(at, name);
        if (!value.TryGetProperty(name, out var array))
        {
            throw Bad(path, missing ?? "is required");
        }

        return array.ValueKind == JsonValueKind.Array ? array : throw Bad(path, "must be an array");
    }

    /// <summary>Rejects a field the format does not define, so a misspelt option never falls back to its default silently.</summary>
    private static void KnownFields(JsonElement value, string at, params string[] known)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal))
            {
                throw Bad(PathOf(at, property.Name), "is not a known field");
            }
        }
    }

    private static string PathOf(string at, string name) => at == Root ? name : $"{at}.{name}";

    private static InvalidDataException Bad(string path, string message) => new($"memo rules: {path}: {message}");
}
