using System.Text.Json.Nodes;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Config;

/// <summary>
/// <c>SimConfigJson</c> binds JSON to <c>SimConfig</c> with the repo's wording for JSON problems, as
/// <c>src/MmoEngine.Regionizer.Simulator/Config/RunConfigLoader.cs</c> words them
/// (contracts/config-rules.md#server).
/// </summary>
public sealed class SimConfigJsonTests
{
    private const string InvalidValue = "has an invalid value or is not a known field (";

    public static TheoryData<string> JsonCases =>
    [
        "truncated",
        "not-an-object",
        "unknown-field",
        "invalid-value",
        "unknown-fire-mode",
    ];

    [Theory]
    [MemberData(nameof(JsonCases))]
    public void JsonProblemsUseTheRepoWording(string caseName)
    {
        var row = caseName switch
        {
            "truncated" => new JsonCase("{", Path: null, "is not valid JSON (", Kind.ParserMessage),
            "not-an-object" => new JsonCase("[]", "config", "must be a JSON object", Kind.Exact),
            "unknown-field" => new JsonCase(DefaultWith(root => root["bogus"] = 1), "bogus", InvalidValue, Kind.ParserFirstLine),
            "invalid-value" => new JsonCase(DefaultWith(root => root["stepRateHz"] = "fast"), "stepRateHz", InvalidValue, Kind.ParserFirstLine),
            "unknown-fire-mode" => new JsonCase(
                DefaultWith(root => root["weapons"]![0]!["fireMode"] = "Burst"),
                "weapons[0].fireMode",
                InvalidValue,
                Kind.ParserFirstLine),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };

        var load = SimConfigJson.Parse(row.Json);

        var error = Assert.Single(load.Errors);
        Assert.Null(load.Config);
        if (row.Path is not null)
        {
            Assert.Equal(row.Path, error.Path);
        }

        if (row.Kind == Kind.Exact)
        {
            Assert.Equal($"{row.Path}: {row.Message}", error.ToString());
            return;
        }

        // The wording, then the parser's message in parentheses.
        Assert.StartsWith(row.Message, error.Message, StringComparison.Ordinal);
        Assert.EndsWith(")", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length > row.Message.Length + 1, $"'{error.Message}' carries no parser message.");
        if (row.Kind == Kind.ParserFirstLine)
        {
            Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NineDotEightOneBindsToTheLiteralsBits()
    {
        var load = SimConfigJson.Parse(RepoPaths.ReadAllText("simcore", "config", "default.json"));

        Assert.Empty(load.Errors);
        var config = load.Config;
        Assert.NotNull(config);
        Assert.Equal(0x411CF5C3u, BitConverter.SingleToUInt32Bits(9.81f));
        Assert.Equal(0x411CF5C3u, BitConverter.SingleToUInt32Bits(config.Movement!.Gravity));

        // An absent section binds as null; the guard's shape rule refuses it.
        var withoutMovement = SimConfigJson.Parse(DefaultWith(root => root.Remove("movement")));
        var bound = withoutMovement.Config;
        Assert.NotNull(bound);
        Assert.Null(bound.Movement);
        ConfigError[] required = [new("movement", "is required")];
        Assert.Equal(required, ConfigGuard.Check(bound));
    }

    /// <summary><c>simcore/config/default.json</c>, read-only, with <paramref name="edit"/> applied to its JSON.</summary>
    private static string DefaultWith(Action<JsonObject> edit)
    {
        var root = JsonNode.Parse(RepoPaths.ReadAllText("simcore", "config", "default.json"))!.AsObject();
        edit(root);
        return root.ToJsonString();
    }

    /// <summary>How a row's message is checked.</summary>
    private enum Kind
    {
        /// <summary>The whole <c>path: message</c> line.</summary>
        Exact,

        /// <summary>The wording, then the parser's message in parentheses.</summary>
        ParserMessage,

        /// <summary>The wording, then the first line of the parser's message in parentheses.</summary>
        ParserFirstLine,
    }

    /// <summary>JSON text and the one error it must give; a null <c>Path</c> is not checked.</summary>
    private sealed record JsonCase(string Json, string? Path, string Message, Kind Kind);
}
