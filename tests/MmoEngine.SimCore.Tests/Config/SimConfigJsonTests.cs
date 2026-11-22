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

    /// <summary>The largest file <c>SimConfigJson.Load</c> reads, 1 MiB.</summary>
    private const long MaxFileLength = 1024 * 1024;

    public static TheoryData<string> FileCases => ["missing-file", "oversized-file", "unreadable-file"];

    public static TheoryData<string> BoundedReadCases => ["no-length-at-the-limit", "no-length-over-the-limit", "grew-after-the-check"];

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

    /// <summary>
    /// <c>SimConfigJson.Load</c> refuses a file it cannot bind with one error at <c>config</c> and no
    /// configuration (contracts/config-rules.md#server): Given (<c>missing-file</c>) a path in a fresh temp
    /// directory that names no file, (<c>oversized-file</c>) a sparse file of 1 MiB + 1 byte, or
    /// (<c>unreadable-file</c>) a copy of <c>simcore/config/default.json</c> that another handle holds open
    /// sharing nothing, When it is loaded, Then the error is exactly <c>config: file '&lt;path&gt;' does not
    /// exist</c> or <c>config: the file is 1048577 bytes; the limit is 1048576</c>, or starts
    /// <c>config: file '&lt;path&gt;' cannot be read (</c> and ends with the reason's closing parenthesis.
    /// </summary>
    [Theory]
    [MemberData(nameof(FileCases))]
    public void LoadRefusesAFileItCannotRead(string caseName)
    {
        using var directory = TempDirectories.Create("config-load");
        var path = directory.Combine("config.json");
        FileStream? hold = null;
        switch (caseName)
        {
            case "missing-file":
                break;
            case "oversized-file":
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.SetLength(MaxFileLength + 1);
                }

                break;
            case "unreadable-file":
                File.WriteAllText(path, RepoPaths.ReadAllText("simcore", "config", "default.json"));
                hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case.");
        }

        SimConfigLoad load;
        using (hold)
        {
            load = SimConfigJson.Load(path);
        }

        Assert.Null(load.Config);
        var error = Assert.Single(load.Errors);
        Assert.Equal("config", error.Path);
        switch (caseName)
        {
            case "missing-file":
                Assert.Equal($"file '{path}' does not exist", error.Message);
                break;
            case "oversized-file":
                Assert.Equal("the file is 1048577 bytes; the limit is 1048576", error.Message);
                break;
            default:
                Assert.StartsWith($"file '{path}' cannot be read (", error.Message, StringComparison.Ordinal);
                Assert.EndsWith(")", error.Message, StringComparison.Ordinal);
                Assert.True(error.Message.Length > $"file '{path}' cannot be read ()".Length, $"'{error.Message}' carries no reason.");
                break;
        }
    }

    /// <summary>
    /// <c>SimConfigJson.Load</c> reads at most 1 MiB + 1 byte, whatever its length check saw
    /// (contracts/config-rules.md#server). Given a short file at the path whose stream, as for a pipe or a
    /// device, has no length and gives (<c>no-length-at-the-limit</c>) <c>simcore/config/default.json</c>
    /// padded with spaces to exactly 1,048,576 bytes or (<c>no-length-over-the-limit</c>) zeros without end,
    /// or (<c>grew-after-the-check</c>) a copy of <c>default.json</c> that grows to 1,048,577 bytes after its
    /// length is checked and before it is opened, When it is loaded, Then the first binds with no error after
    /// reading exactly 1,048,576 bytes, the second is refused with exactly
    /// <c>config: the file holds more than 1048576 bytes; the limit is 1048576</c> after reading exactly
    /// 1,048,577 bytes, and the third with the same error from the open file's length.
    /// </summary>
    [Theory]
    [MemberData(nameof(BoundedReadCases))]
    public void LoadReadsAtMostTheLimitPlusOneByte(string caseName)
    {
        using var directory = TempDirectories.Create("config-load-bounded");
        var path = directory.Combine("config.json");
        File.WriteAllText(path, RepoPaths.ReadAllText("simcore", "config", "default.json"));
        UnseekableStream? pipe = caseName switch
        {
            "no-length-at-the-limit" => new UnseekableStream(ConfigFiles.DefaultPaddedTo(MaxFileLength)),
            "no-length-over-the-limit" => new UnseekableStream(content: null),
            "grew-after-the-check" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };

        var load = SimConfigJson.Load(path, opened => pipe ?? ConfigFiles.GrowThenOpen(opened, MaxFileLength + 1));

        if (caseName == "no-length-at-the-limit")
        {
            Assert.Empty(load.Errors);
            Assert.NotNull(load.Config);
            Assert.True(pipe!.BytesRead == MaxFileLength, $"Load read {pipe.BytesRead} bytes of a {MaxFileLength}-byte stream");
            return;
        }

        Assert.Null(load.Config);
        var error = Assert.Single(load.Errors);
        Assert.Equal("config: the file holds more than 1048576 bytes; the limit is 1048576", error.ToString());
        if (pipe is not null)
        {
            Assert.True(pipe.BytesRead == MaxFileLength + 1, $"Load read {pipe.BytesRead} bytes of an endless stream; it may read at most {MaxFileLength + 1}");
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
