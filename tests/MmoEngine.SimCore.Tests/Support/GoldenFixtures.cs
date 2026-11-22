using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Read-only access to the committed golden fixtures under <c>simcore/golden/</c> (FR-023, US6,
/// contracts/input-command-format.md#shared-fixture-rules): one <c>&lt;case&gt;.bin</c> and one
/// <c>&lt;case&gt;.json</c> per case in <c>input/</c>, <c>collision/</c> and <c>stream/</c>. Every member
/// opens files through <see cref="RepoPaths"/> with <see cref="FileAccess.Read"/> and never writes; only
/// <c>simcore golden write</c> writes the <c>.bin</c> files, and its tests work on copies.
/// </summary>
/// <remarks>
/// The Theory rows are enumerated from the <c>.json</c> files of each format folder, formats in the order
/// of <see cref="Formats"/> and cases in ordinal name order, and are classified by reading <c>kind</c>,
/// <c>refusedAt</c> and <c>expectedError.code</c> with <see cref="JsonDocument"/>, never with the code
/// under test (<c>GoldenJson</c>). A case the classification cannot place (a <c>kind</c> other than
/// <c>valid</c> or <c>refusal</c>, or a <c>refusedAt</c> other than <c>open</c> on a refusal) throws, so
/// no fixture silently drops out of every Theory. The contract's case lists are held separately, as
/// literals in <see cref="ContractCaseNames"/>, so a missing or extra fixture shows up against them.
/// </remarks>
public static partial class GoldenFixtures
{
    private const string ValidKind = "valid";
    private const string RefusalKind = "refusal";
    private const string OpenStage = "open";

    private static readonly Lazy<ImmutableArray<CaseEntry>> Cases = new(ReadCases);

    /// <summary>
    /// The 38 input cases of contracts/input-command-format.md#input-cases, in the contract's table order:
    /// the 29 valid cases, then the 9 refusal cases.
    /// </summary>
    private static readonly ImmutableArray<string> InputCaseNames =
    [
        "neutral",
        "example",
        "button-forward",
        "button-back",
        "button-left",
        "button-right",
        "button-jump",
        "button-crouch",
        "button-walk",
        "button-fire",
        "button-reload",
        "all-buttons",
        "aim-yaw-max",
        "aim-pitch-up",
        "aim-pitch-down",
        "sequence-max",
        "weapon-slot-last",
        "reject-sequence-zero",
        "reject-sequence-repeat",
        "reject-sequence-first",
        "reject-button-9",
        "reject-buttons-reserved",
        "reject-buttons-all",
        "reject-pitch-above",
        "reject-pitch-below",
        "reject-pitch-min",
        "reject-pitch-max",
        "reject-weapon-slot",
        "reject-weapon-slot-max",
        "refuse-empty",
        "refuse-format-id",
        "refuse-only-format-id",
        "refuse-version-0",
        "refuse-version-2",
        "refuse-version-2-longer",
        "refuse-truncated-2",
        "refuse-truncated-16",
        "refuse-oversized-18",
    ];

    /// <summary>
    /// The 22 collision cases of contracts/collision-data-format.md#golden-fixtures, in the contract's
    /// order: the 4 valid cases, then the 18 refusal cases.
    /// </summary>
    private static readonly ImmutableArray<string> CollisionCaseNames =
    [
        "tiny-world",
        "heightfield-only",
        "extent-at-limit",
        "full-cell",
        "unknown-magic",
        "unsupported-version",
        "header-length",
        "short-header",
        "truncated",
        "trailing-bytes",
        "columns-too-few",
        "too-many-meshes",
        "counts-disagree",
        "checksum-mismatch",
        "reserved-nonzero",
        "grid-cell-too-small",
        "extent-out-of-range",
        "non-finite-height",
        "height-out-of-range",
        "vertex-out-of-range",
        "mesh-index-out-of-range",
        "crowded-cell",
    ];

    /// <summary>
    /// The 32 stream cases of contracts/recorded-stream-format.md#golden-fixtures, in the contract's
    /// order: the 4 valid cases, then the 28 refusal cases (the last four refused when opened).
    /// </summary>
    private static readonly ImmutableArray<string> StreamCaseNames =
    [
        "two-steps",
        "two-characters",
        "rejected-command",
        "seeded",
        "unknown-magic",
        "unsupported-version",
        "header-length",
        "short-header",
        "truncated",
        "trailing-bytes",
        "too-many-characters",
        "zero-steps",
        "config-too-long",
        "state-too-long",
        "lengths-disagree",
        "checksum-mismatch",
        "step-hz-out-of-range",
        "step-hz-mismatch",
        "config-count-short",
        "config-not-ascending",
        "config-unknown-field",
        "config-missing-field",
        "state-character-count",
        "state-has-events",
        "step-number-gap",
        "too-many-commands",
        "command-index-out-of-range",
        "command-bad-version",
        "rules-revision",
        "collision-missing",
        "collision-other",
        "config-invalid",
    ];

    /// <summary>The committed golden fixtures: <c>simcore/golden/</c> under the repository root.</summary>
    public static string Root => RepoPaths.Combine("simcore", "golden");

    /// <summary>The three fixture formats, in the order <c>golden write</c> and the Theory rows visit them.</summary>
    public static IReadOnlyList<string> Formats { get; } = ["input", "collision", "stream"];

    /// <summary>Every valid case, rows <c>(format, case)</c>.</summary>
    public static TheoryData<string, string> ValidCases => Rows(entry => entry.Kind == ValidKind);

    /// <summary>Every refusal case, rows <c>(format, case)</c>, open refusals included.</summary>
    public static TheoryData<string, string> RefusalCases => Rows(entry => entry.Kind == RefusalKind);

    /// <summary>The refusal cases a decoder refuses: those with no <c>refusedAt</c>.</summary>
    public static TheoryData<string, string> DecodeRefusalCases => Rows(IsDecodeRefusal);

    /// <summary>
    /// The decode refusals whose <c>expectedError.code</c> is neither <c>UnsupportedVersion</c> nor
    /// <c>ChecksumMismatch</c>, which have Theories of their own.
    /// </summary>
    public static TheoryData<string, string> OtherDecodeRefusals => Rows(entry =>
        IsDecodeRefusal(entry)
        && entry.Code != nameof(FormatErrorCode.UnsupportedVersion)
        && entry.Code != nameof(FormatErrorCode.ChecksumMismatch));

    /// <summary>The refusal cases with <c>"refusedAt": "open"</c>: they decode, and <c>StreamRunner</c> refuses them when it opens them.</summary>
    public static TheoryData<string, string> OpenRefusals => Rows(entry => entry.Kind == RefusalKind && entry.RefusedAt == OpenStage);

    /// <summary>
    /// The valid case names of one format, one row per case, in ordinal order: the rows of
    /// <see cref="ValidCases"/> for that format.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not one of <see cref="Formats"/>.</exception>
    public static TheoryData<string> ValidCaseNames(string format)
    {
        CheckFormat(format);
        var data = new TheoryData<string>();
        foreach (var entry in Cases.Value)
        {
            if (entry.Format == format && entry.Kind == ValidKind)
            {
                data.Add(entry.Case);
            }
        }

        return data;
    }

    /// <summary>The committed <c>&lt;format&gt;/&lt;case&gt;.bin</c>, read through <see cref="RepoPaths.ReadAllBytes"/>.</summary>
    /// <exception cref="ArgumentException">The format is unknown or the case name is not kebab-case.</exception>
    public static byte[] ReadBin(string format, string caseName) => RepoPaths.ReadAllBytes(PathParts(format, caseName, ".bin"));

    /// <summary>The committed <c>&lt;format&gt;/&lt;case&gt;.json</c>, parsed with <see cref="JsonDocument"/>; the caller disposes it.</summary>
    /// <exception cref="ArgumentException">The format is unknown or the case name is not kebab-case.</exception>
    public static JsonDocument ReadJson(string format, string caseName)
    {
        using var stream = RepoPaths.OpenRead(PathParts(format, caseName, ".json"));
        return JsonDocument.Parse(stream);
    }

    /// <summary>The committed <c>&lt;format&gt;/&lt;case&gt;.json</c>'s UTF-8 bytes, as <c>GoldenJson.Parse</c> takes them.</summary>
    /// <exception cref="ArgumentException">The format is unknown or the case name is not kebab-case.</exception>
    public static byte[] ReadJsonBytes(string format, string caseName) => RepoPaths.ReadAllBytes(PathParts(format, caseName, ".json"));

    /// <summary>
    /// A refusal case's <c>expectedError</c>: <c>code</c> parsed with <see cref="Enum.Parse{TEnum}(string)"/>,
    /// <c>offset</c> and <c>field</c>.
    /// </summary>
    /// <exception cref="InvalidDataException">The case has no <c>expectedError</c> object with a string <c>code</c>, a number <c>offset</c> and a string <c>field</c>.</exception>
    public static (FormatErrorCode Code, long Offset, string Field) ExpectedError(string format, string caseName)
    {
        using var json = ReadJson(format, caseName);
        if (!json.RootElement.TryGetProperty("expectedError", out var expected) || expected.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{format}/{caseName}.json has no expectedError object.");
        }

        var code = StringProperty(expected, "code", format, caseName);
        var field = StringProperty(expected, "field", format, caseName);
        if (!expected.TryGetProperty("offset", out var offset) || offset.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"{format}/{caseName}.json's expectedError has no number offset.");
        }

        return (Enum.Parse<FormatErrorCode>(code), offset.GetInt64(), field);
    }

    /// <summary>
    /// The collision worlds a stream case is opened with: <see cref="CollisionFile.Decode"/> of
    /// <c>collision/&lt;name&gt;.bin</c> for each name in its <c>collisionFixtures</c>, in that order.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream case has no <c>collisionFixtures</c> array of strings.</exception>
    public static IReadOnlyList<CollisionWorld> CollisionFixtureWorlds(string caseName)
    {
        const string format = "stream";
        using var json = ReadJson(format, caseName);
        if (!json.RootElement.TryGetProperty("collisionFixtures", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{format}/{caseName}.json has no collisionFixtures array.");
        }

        var worlds = new List<CollisionWorld>();
        foreach (var name in names.EnumerateArray())
        {
            if (name.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"{format}/{caseName}.json's collisionFixtures holds a {name.ValueKind}, not a case name.");
            }

            worlds.Add(CollisionFile.Decode(ReadBin("collision", name.GetString()!)));
        }

        return worlds;
    }

    /// <summary>
    /// The case names of the contract's tables for <paramref name="format"/>, as literals in table order:
    /// input 38 (29 valid, 9 refusal), collision 22 (4 valid, 18 refusal) and stream 32 (4 valid,
    /// 28 refusal).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="format"/> is not one of <see cref="Formats"/>.</exception>
    public static IReadOnlyList<string> ContractCaseNames(string format) => format switch
    {
        "input" => InputCaseNames,
        "collision" => CollisionCaseNames,
        "stream" => StreamCaseNames,
        _ => throw new ArgumentException($"Unknown golden format '{format}'; expected one of {string.Join(", ", Formats)}.", nameof(format)),
    };

    private static bool IsDecodeRefusal(CaseEntry entry) => entry.Kind == RefusalKind && entry.RefusedAt is null;

    private static TheoryData<string, string> Rows(Func<CaseEntry, bool> include)
    {
        var data = new TheoryData<string, string>();
        foreach (var entry in Cases.Value)
        {
            if (include(entry))
            {
                data.Add(entry.Format, entry.Case);
            }
        }

        return data;
    }

    /// <summary>Every <c>.json</c> of every format folder, formats in <see cref="Formats"/> order and cases in ordinal order, classified.</summary>
    private static ImmutableArray<CaseEntry> ReadCases()
    {
        var entries = ImmutableArray.CreateBuilder<CaseEntry>();
        foreach (var format in Formats)
        {
            var directory = RepoPaths.Combine("simcore", "golden", format);
            var names = Directory.GetFiles(directory)
                .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.Ordinal))
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (var name in names)
            {
                entries.Add(Classify(format, name));
            }
        }

        return entries.ToImmutable();
    }

    private static CaseEntry Classify(string format, string caseName)
    {
        using var json = ReadJson(format, caseName);
        var root = json.RootElement;
        var kind = StringProperty(root, "kind", format, caseName);
        if (kind is not (ValidKind or RefusalKind))
        {
            throw new InvalidDataException($"{format}/{caseName}.json has kind '{kind}'; expected '{ValidKind}' or '{RefusalKind}'.");
        }

        string? refusedAt = null;
        if (root.TryGetProperty("refusedAt", out var stage))
        {
            refusedAt = stage.ValueKind == JsonValueKind.String ? stage.GetString() : null;
            if (kind != RefusalKind || refusedAt != OpenStage)
            {
                throw new InvalidDataException($"{format}/{caseName}.json has refusedAt {stage.GetRawText()}; only a refusal case may have one, and it is \"{OpenStage}\".");
            }
        }

        string? code = null;
        if (root.TryGetProperty("expectedError", out var expected)
            && expected.ValueKind == JsonValueKind.Object
            && expected.TryGetProperty("code", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.String)
        {
            code = codeElement.GetString();
        }

        return new CaseEntry(format, caseName, kind, refusedAt, code);
    }

    private static string StringProperty(JsonElement element, string name, string format, string caseName)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{format}/{caseName}.json has no string {name}.");
        }

        return value.GetString()!;
    }

    private static string[] PathParts(string format, string caseName, string extension)
    {
        CheckFormat(format);
        ArgumentNullException.ThrowIfNull(caseName);
        if (!CaseNamePattern().IsMatch(caseName))
        {
            throw new ArgumentException($"'{caseName}' is not a kebab-case golden case name.", nameof(caseName));
        }

        return ["simcore", "golden", format, caseName + extension];
    }

    private static void CheckFormat(string format)
    {
        if (!Formats.Contains(format, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unknown golden format '{format}'; expected one of {string.Join(", ", Formats)}.", nameof(format));
        }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CaseNamePattern();

    /// <summary>One fixture as the classification read it: its format, case name, kind, <c>refusedAt</c> and <c>expectedError.code</c>.</summary>
    private sealed record CaseEntry(string Format, string Case, string Kind, string? RefusedAt, string? Code);
}
