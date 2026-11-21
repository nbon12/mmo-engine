using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Mutations of a measurement report for the <c>validate-report</c> tests
/// (contracts/measurement-report.md#tests), and the committed example they start from.
/// A mutation is one or more operations joined by <c>" ; "</c>:
/// <list type="bullet">
///   <item><c>set &lt;path&gt; &lt;json&gt;</c> replaces the value at the path, or adds it when the last
///   step names a member the object does not have or the index one past an array's end;</item>
///   <item><c>remove &lt;path&gt;</c> deletes an object member or an array element.</item>
/// </list>
/// Paths use the validator's syntax: dot-separated member names, which may contain <c>-</c>, and
/// <c>[i]</c> indexes, for example <c>wasm.modes.interp-prebuilt.download.files[8].name</c>. An unknown
/// path throws <see cref="ArgumentException"/> naming it. Members keep their document order, so a
/// replaced value stays where it was and an added one goes last.
/// </summary>
public static class ReportMutations
{
    private const string Separator = " ; ";
    private const string SetPrefix = "set ";
    private const string RemovePrefix = "remove ";

    /// <summary>
    /// Writes strings as the mutation gave them, with only the escapes JSON requires, so a value a
    /// validator message prints with its raw text reads the same as in the mutation.
    /// </summary>
    private static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The copy of <c>tests/MmoEngine.SimCore.Tests/Cli/Fixtures/measurement-report.json</c>, the
    /// contract's example report, in the test output.
    /// </summary>
    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Cli", "Fixtures", "measurement-report.json");

    /// <summary>The example report's text, read through <see cref="FixturePath"/> for reading only.</summary>
    public static string ReadFixture()
    {
        using var stream = new FileStream(FixturePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// <paramref name="json"/> with every operation of <paramref name="mutation"/> applied in order,
    /// written back as compact JSON.
    /// </summary>
    public static string Apply(string json, string mutation)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutation);
        var root = JsonNode.Parse(json)
            ?? throw new ArgumentException("The report is the JSON literal null; there is nothing to mutate.", nameof(json));
        foreach (var operation in mutation.Split(Separator))
        {
            if (operation.StartsWith(SetPrefix, StringComparison.Ordinal))
            {
                var rest = operation[SetPrefix.Length..];
                var space = rest.IndexOf(' ', StringComparison.Ordinal);
                if (space <= 0)
                {
                    throw new ArgumentException($"\"{operation}\" needs a path and a JSON value.", nameof(mutation));
                }

                Set(root, rest[..space], JsonNode.Parse(rest[(space + 1)..]));
            }
            else if (operation.StartsWith(RemovePrefix, StringComparison.Ordinal))
            {
                Remove(root, operation[RemovePrefix.Length..]);
            }
            else
            {
                throw new ArgumentException($"\"{operation}\" is neither a set nor a remove operation.", nameof(mutation));
            }
        }

        return root.ToJsonString(Output);
    }

    private static void Set(JsonNode root, string path, JsonNode? value)
    {
        var (parent, last) = Parent(root, path);
        switch (parent)
        {
            case JsonObject members when last.Member is { } name:
                members[name] = value;
                return;
            case JsonArray elements when last.Member is null && last.Index < elements.Count:
                elements[last.Index] = value;
                return;
            case JsonArray elements when last.Member is null && last.Index == elements.Count:
                elements.Add(value);
                return;
            default:
                throw UnknownPath(path);
        }
    }

    private static void Remove(JsonNode root, string path)
    {
        var (parent, last) = Parent(root, path);
        switch (parent)
        {
            case JsonObject members when last.Member is { } name && members.Remove(name):
                return;
            case JsonArray elements when last.Member is null && last.Index < elements.Count:
                elements.RemoveAt(last.Index);
                return;
            default:
                throw UnknownPath(path);
        }
    }

    /// <summary>The node that holds the path's last step, and that step.</summary>
    private static (JsonNode Parent, PathStep Last) Parent(JsonNode root, string path)
    {
        var steps = Parse(path);
        var current = root;
        for (var i = 0; i < steps.Count - 1; i++)
        {
            current = Child(current, steps[i]) ?? throw UnknownPath(path);
        }

        return (current, steps[^1]);
    }

    private static JsonNode? Child(JsonNode node, PathStep step) => node switch
    {
        JsonObject members when step.Member is { } name && members.TryGetPropertyValue(name, out var child) => child,
        JsonArray elements when step.Member is null && step.Index < elements.Count => elements[step.Index],
        _ => null,
    };

    /// <summary>The steps of <paramref name="path"/>: each dot-separated member name, then its <c>[i]</c> indexes.</summary>
    private static List<PathStep> Parse(string path)
    {
        var steps = new List<PathStep>();
        foreach (var segment in path.Split('.'))
        {
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var name = bracket < 0 ? segment : segment[..bracket];
            if (name.Length > 0)
            {
                steps.Add(new PathStep(name, 0));
            }
            else if (bracket != 0 || steps.Count > 0)
            {
                // An empty member name is allowed only before a root index, as in "[0].step".
                throw UnknownPath(path);
            }

            var position = name.Length;
            while (position < segment.Length)
            {
                var close = segment.IndexOf(']', position);
                if (segment[position] != '[' || close < 0
                    || !int.TryParse(segment.AsSpan(position + 1, close - position - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    throw UnknownPath(path);
                }

                steps.Add(new PathStep(null, index));
                position = close + 1;
            }
        }

        return steps;
    }

    private static ArgumentException UnknownPath(string path) =>
        new($"\"{path}\" is not a path in the report.", nameof(path));

    /// <summary>One step of a path: a member name, or an array index when <see cref="Member"/> is null.</summary>
    private readonly record struct PathStep(string? Member, int Index);
}
