using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;

namespace MmoEngine.Harness.Network;

/// <summary>A network profile file the harness refuses (FR-021). The message names the file and field.</summary>
public sealed class ProfileException(string message) : Exception(message);

/// <summary>Calibration tolerances (FR-025; the issue's proposed values, kept in the profile file).</summary>
public sealed record Tolerances
{
    /// <summary>Median round trip and jitter must be within this fraction of target (0.10 = 10%).</summary>
    public double RelativeFraction { get; init; } = 0.10;

    /// <summary>When set, round trip and jitter use this absolute tolerance in ms instead (LAN: 0.5 ms).</summary>
    public double? AbsoluteMs { get; init; }

    /// <summary>Loss per direction must be within this many percentage points of target.</summary>
    public double LossPoints { get; init; } = 0.25;
}

/// <summary>Per-direction impairment settings: what netem or Chaos Mesh applies on one direction's egress.</summary>
public sealed record DirectionSettings(double DelayMs, double JitterMs, double LossPercent);

/// <summary>Settings that measured within tolerance, stored with the profile (FR-026).</summary>
public sealed record CalibratedSettings
{
    public required DirectionSettings PerDirection { get; init; }
    public required string RunnerClass { get; init; }
    public required string Kernel { get; init; }
    public required DateTime MeasuredAt { get; init; }
    public string SourceRunId { get; init; } = "";
}

/// <summary>
/// A named, versioned network profile (FR-021, contracts/network-profile.md). Round trip is the median;
/// jitter is the standard deviation of round-trip time; loss is per packet in each direction.
/// </summary>
public sealed record NetworkProfile
{
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required double RoundTripMs { get; init; }
    public required double JitterMs { get; init; }
    public required double LossPercent { get; init; }
    public bool AllowReordering { get; init; }
    public Tolerances Tolerances { get; init; } = new();
    public CalibratedSettings? Calibrated { get; init; }

    /// <summary>The file the catalog loaded this profile from; null for a derived reordering variant.</summary>
    [JsonIgnore]
    public string? SourceFile { get; init; }

    /// <summary>The suffix of a reordering variant's name.</summary>
    public const string ReorderSuffix = "-reorder";

    /// <summary>The profile's reordering variant (FR-021): the same targets, with reordering allowed.</summary>
    public NetworkProfile ReorderingVariant() =>
        this with { Name = Name + ReorderSuffix, AllowReordering = true, Calibrated = null, SourceFile = null };

    /// <summary>
    /// The per-direction settings to apply: the calibrated ones when stored, else the arithmetic split
    /// (research R7): half the round trip each way, jitter divided by √2 so the round-trip standard
    /// deviation comes out at the target, and the full per-direction loss.
    /// </summary>
    /// <summary>
    /// This profile with explicit per-direction settings, as calibration tries them (FR-026). They are stored
    /// only once a calibration with them passes.
    /// </summary>
    public NetworkProfile WithPerDirection(DirectionSettings settings) => this with
    {
        Calibrated = new CalibratedSettings
        {
            PerDirection = settings,
            RunnerClass = Calibrated?.RunnerClass ?? "",
            Kernel = Calibrated?.Kernel ?? "",
            MeasuredAt = Calibrated?.MeasuredAt ?? default,
            SourceRunId = Calibrated?.SourceRunId ?? "",
        },
    };

    public DirectionSettings PerDirection() =>
        Calibrated?.PerDirection ?? new DirectionSettings(RoundTripMs / 2, JitterMs / Math.Sqrt(2), LossPercent);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    /// <summary>Parses and validates one profile file.</summary>
    public static NetworkProfile Parse(string json, string fileName)
    {
        NetworkProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<NetworkProfile>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new ProfileException($"{fileName}: invalid JSON or unknown field ({ex.Message})");
        }

        if (profile is null)
        {
            throw new ProfileException($"{fileName}: the file is empty.");
        }

        var result = new Validator().Validate(profile);
        if (!result.IsValid)
        {
            var error = result.Errors[0];
            throw new ProfileException($"{fileName}: {error.PropertyName}: {error.ErrorMessage}");
        }

        return profile;
    }

    /// <summary>The profile as JSON, in the file format.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json) + "\n";

    private sealed class Validator : AbstractValidator<NetworkProfile>
    {
        public Validator()
        {
            RuleFor(p => p.Name).NotEmpty().Matches("^[A-Za-z0-9-]+$").OverridePropertyName("name");
            RuleFor(p => p.Version).GreaterThanOrEqualTo(1).OverridePropertyName("version");
            RuleFor(p => p.RoundTripMs).GreaterThanOrEqualTo(0).Must(double.IsFinite).OverridePropertyName("roundTripMs");
            RuleFor(p => p.JitterMs).GreaterThanOrEqualTo(0).Must(double.IsFinite).OverridePropertyName("jitterMs");
            RuleFor(p => p.LossPercent).InclusiveBetween(0, 100).OverridePropertyName("lossPercent");
            RuleFor(p => p.Tolerances.RelativeFraction).GreaterThan(0).LessThan(1).OverridePropertyName("tolerances.relativeFraction");
            RuleFor(p => p.Tolerances.LossPoints).GreaterThanOrEqualTo(0).OverridePropertyName("tolerances.lossPoints");
            RuleFor(p => p.Tolerances.AbsoluteMs).GreaterThan(0).When(p => p.Tolerances.AbsoluteMs is not null)
                .OverridePropertyName("tolerances.absoluteMs");
        }
    }
}

/// <summary>The profile catalogue: every file in <c>harness/profiles/</c>, plus each one's reordering variant (FR-021).</summary>
public static class ProfileCatalog
{
    /// <summary>Loads the catalogue, keyed by name (case-insensitive).</summary>
    public static IReadOnlyDictionary<string, NetworkProfile> Load(string folder)
    {
        if (!Directory.Exists(folder))
        {
            throw new ProfileException($"Profiles folder not found: {folder}");
        }

        var catalog = new Dictionary<string, NetworkProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            var profile = NetworkProfile.Parse(File.ReadAllText(file), Path.GetFileName(file)) with { SourceFile = Path.GetFullPath(file) };
            foreach (var p in new[] { profile, profile.ReorderingVariant() })
            {
                if (!catalog.TryAdd(p.Name, p))
                {
                    throw new ProfileException($"{Path.GetFileName(file)}: profile name '{p.Name}' is defined twice.");
                }
            }
        }

        return catalog;
    }
}
