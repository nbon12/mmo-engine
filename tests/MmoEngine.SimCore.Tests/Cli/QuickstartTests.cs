using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using MmoEngine.SimCore.Cli;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// The quickstart's slice-1 <c>simcore</c> commands (specs/015-shared-simulation-core/quickstart.md) are
/// real: each parses against the CLI's own command tree (<see cref="Program.CreateRootCommand()"/>) and
/// resolves to the command its first two words name, and the documented refusal refuses. The sections
/// <c>Run the agreement suite</c> and <c>The measurement report</c> are left out: they need slice 2's
/// WebAssembly publish and <c>tools/agreement/</c>, and Phase 13 covers them.
/// </summary>
/// <remarks>
/// quickstart.md is opened read-only through <see cref="RepoPaths"/>. The refusal runs with
/// <c>--out</c> set to a fresh <see cref="TempDirectories"/> directory, so the test never writes under
/// <c>simcore/</c>.
/// </remarks>
public sealed class QuickstartTests
{
    /// <summary>What every collected line starts with; its arguments follow <see cref="ArgumentSeparator"/>.</summary>
    private const string CommandPrefix = "dotnet run --project src/MmoEngine.SimCore.Cli";

    private const string ArgumentSeparator = " -- ";

    /// <summary>The line after a command that prints its exit code, whose comment documents that code.</summary>
    private const string EchoExitCode = "echo $?";

    /// <summary>The comment of the documented refusal (contracts/simcore-cli.md#streams-record step 1).</summary>
    private const string RefusalComment = "2: --reason is required";

    /// <summary>What <c>streams record</c> writes to stderr when <c>--reason</c> is missing.</summary>
    private const string MissingReasonLine = "streams record: --reason is required: say why the expected hashes change";

    /// <summary>The index in <see cref="ExpectedCommands"/> of the documented refusal, <c>streams record --all</c>.</summary>
    private const int RefusalIndex = 4;

    /// <summary>The five slice-1 <c>##</c> sections whose commands are collected, in document order.</summary>
    private static readonly string[] Sections =
    [
        "Check a configuration file",
        "Reproduce the test world",
        "Rewrite the golden fixtures",
        "Verify the committed streams",
        "Regenerate the committed streams",
    ];

    /// <summary>The seven commands those sections give, in document order, as their arguments.</summary>
    private static readonly string[][] ExpectedCommands =
    [
        ["config", "check", "simcore/config/default.json"],
        ["content", "generate", "--out", "simcore/content/test-world.mmcd"],
        ["golden", "write", "--dir", "simcore/golden"],
        ["streams", "verify"],
        ["streams", "record", "--all"],
        ["streams", "record", "--all", "--reason", "Explain why the expected hashes change"],
        ["streams", "verify"],
    ];

    private static readonly string[] CommandNames = ["check", "generate", "write", "verify", "record"];

    private static readonly string[] ParentNames = ["config", "content", "golden", "streams"];

    [Fact]
    public void EverySliceOneQuickstartCommandParses()
    {
        var markdown = RepoPaths.ReadAllText("specs", "015-shared-simulation-core", "quickstart.md");

        var commands = Collect(markdown);

        // Then, line count: exactly the seven commands, in order.
        Assert.True(
            commands.Count == ExpectedCommands.Length,
            $"quickstart.md's slice-1 sections give {commands.Count} simcore commands, expected {ExpectedCommands.Length}: {Describe(commands)}");
        for (var i = 0; i < ExpectedCommands.Length; i++)
        {
            Assert.Equal(ExpectedCommands[i], commands[i].Arguments);
        }

        // Then, refusal: exactly one command carries the refusal comment, and it is streams record --all.
        var refusals = commands.Select((command, index) => (command, index)).Where(c => c.command.Comment == RefusalComment).ToArray();
        Assert.True(
            refusals.Length == 1 && refusals[0].index == RefusalIndex,
            $"the refusal comment '# {RefusalComment}' should follow exactly command {RefusalIndex}, found it at [{string.Join(", ", refusals.Select(r => r.index))}]");

        // Then, parsing: every other command parses with no errors and resolves to the command its first two words name.
        var root = Program.CreateRootCommand();
        for (var i = 0; i < commands.Count; i++)
        {
            if (i == RefusalIndex)
            {
                continue;
            }

            var arguments = commands[i].Arguments;
            var parse = root.Parse(arguments);
            var line = string.Join(' ', arguments);
            Assert.True(parse.Errors.Count == 0, $"simcore {line}: {string.Join("; ", parse.Errors.Select(e => e.Message))}");
            var command = parse.CommandResult.Command;
            var parent = Assert.IsType<CommandResult>(parse.CommandResult.Parent, exactMatch: true).Command;
            Assert.Contains(command.Name, CommandNames);
            Assert.Contains(parent.Name, ParentNames);
            Assert.True(
                command.Name == arguments[1] && parent.Name == arguments[0],
                $"simcore {line} resolved to '{parent.Name} {command.Name}', expected '{arguments[0]} {arguments[1]}'");
        }

        using var output = TempDirectories.Create("quickstart-refusal");
        string[] refusal = [.. commands[RefusalIndex].Arguments, "--out", output.Path];

        var (exit, stdout, stderr) = SimcoreCli.Run(refusal);

        Assert.True(exit == 2, $"simcore {string.Join(' ', refusal)} exited {exit}, expected 2; stdout: {stdout}; stderr: {stderr}");
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(MissingReasonLine + "\n", stderr);
        Assert.Empty(Directory.EnumerateFileSystemEntries(output.Path));
    }

    /// <summary>
    /// Every line of <see cref="Sections"/> that starts with <see cref="CommandPrefix"/>, split after
    /// <see cref="ArgumentSeparator"/> into arguments. Its comment is its own trailing <c># ...</c>, or else
    /// that of an <see cref="EchoExitCode"/> line right after it.
    /// </summary>
    private static List<QuickstartCommand> Collect(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var found = new List<string>();
        var commands = new List<QuickstartCommand>();
        var inSection = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var heading = line[3..].Trim();
                inSection = Sections.Contains(heading, StringComparer.Ordinal);
                if (inSection)
                {
                    found.Add(heading);
                }

                continue;
            }

            if (!inSection || !line.StartsWith(CommandPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var separator = line.IndexOf(ArgumentSeparator, StringComparison.Ordinal);
            Assert.True(separator >= 0, $"quickstart.md line {i + 1} has no '{ArgumentSeparator.Trim()}' before its arguments: {line}");
            var (arguments, comment) = Split(line[(separator + ArgumentSeparator.Length)..], i + 1);
            if (comment is null && i + 1 < lines.Length && lines[i + 1].StartsWith(EchoExitCode, StringComparison.Ordinal))
            {
                comment = Split(lines[i + 1], i + 2).Comment;
            }

            commands.Add(new QuickstartCommand(arguments, comment));
        }

        Assert.Equal(Sections, found);
        return commands;
    }

    /// <summary>
    /// Splits <paramref name="text"/> at whitespace into arguments. A double-quoted string is one argument
    /// with its quotes removed; a <c>#</c> that starts a word begins a comment, returned trimmed, which runs
    /// to the end of the line.
    /// </summary>
    private static (string[] Arguments, string? Comment) Split(string text, int lineNumber)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                var close = text.IndexOf('"', i + 1);
                Assert.True(close > i, $"quickstart.md line {lineNumber} has an unterminated quote: {text}");
                current.Append(text, i + 1, close - i - 1);
                inWord = true;
                i = close;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }
            }
            else if (c == '#' && !inWord)
            {
                return ([.. arguments], text[(i + 1)..].Trim());
            }
            else
            {
                current.Append(c);
                inWord = true;
            }
        }

        if (inWord)
        {
            arguments.Add(current.ToString());
        }

        return ([.. arguments], null);
    }

    private static string Describe(IEnumerable<QuickstartCommand> commands) =>
        string.Join(" | ", commands.Select(c => string.Join(' ', c.Arguments)));

    /// <summary>One collected quickstart command: its arguments and the comment that documents it, if any.</summary>
    private sealed record QuickstartCommand(string[] Arguments, string? Comment);
}
