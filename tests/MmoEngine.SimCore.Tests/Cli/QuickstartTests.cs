using System.Buffers;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using System.Text.RegularExpressions;
using MmoEngine.SimCore.Cli;
using MmoEngine.SimCore.Cli.Agreement;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Cli;

/// <summary>
/// The quickstart's commands (specs/015-shared-simulation-core/quickstart.md) are real. Slice 1's
/// <c>simcore</c> commands each parse against the CLI's own command tree
/// (<see cref="Program.CreateRootCommand()"/>) and resolve to the command their first two words name, and
/// the documented refusal refuses. Slice 2's sections (<see cref="QuickstartSections.SliceTwo"/>) hold
/// bash lines whose quotes close, CLI commands that parse, a publishable project, runner files that
/// exist, known build names and report modes, a declared Playwright project and a workflow-tests
/// directory that holds the SimCore workflow's tests. Every path is resolved against the directory its
/// command runs in: each block starts in the repository root, a <c>cd</c> changes it for the rest of the
/// block, and one inside <c>( ... )</c> only until the <c>)</c>. <see cref="SliceTwoChecksReportEachDefect"/>
/// sees each of those checks, and the section check, fail on a copy with one defect.
/// </summary>
/// <remarks>
/// quickstart.md is opened read-only through <see cref="RepoPaths"/>. The refusal runs with
/// <c>--out</c> set to a fresh <see cref="TempDirectories"/> directory, so the test never writes under
/// <c>simcore/</c>. The slice-2 checks only parse and look paths up; the shell variables of their lines
/// stand for paths under a fresh <see cref="TempDirectories"/> directory, which nothing writes to.
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

    /// <summary>The seven commands the <see cref="QuickstartSections.SliceOne"/> sections give, in document order, as their arguments.</summary>
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
    /// Every line of the <see cref="QuickstartSections.SliceOne"/> sections that starts with
    /// <see cref="CommandPrefix"/>, split after <see cref="ArgumentSeparator"/> into arguments. Its comment is
    /// its own trailing <c># ...</c>, or else that of an <see cref="EchoExitCode"/> line right after it.
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
                inSection = QuickstartSections.SliceOne.Contains(heading, StringComparer.Ordinal);
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

        Assert.Equal(QuickstartSections.SliceOne, found);
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

    // Slice 2: the agreement suite, the measurement report and the workflow tests.

    /// <summary>The check that a <c>simcore</c> line has the documented shape and parses.</summary>
    private const string ParseCheck = "parse";

    /// <summary>The check that a <c>dotnet publish</c> line names a directory holding one project file.</summary>
    private const string PublishCheck = "publish";

    /// <summary>The check that a <c>node --test &lt;path&gt;</c> or <c>node &lt;path&gt;</c> line names a file that exists.</summary>
    private const string FileCheck = "file";

    /// <summary>The check that every <c>AGREEMENT_BUILD</c> value and <c>--require</c> name is a known build.</summary>
    private const string BuildNameCheck = "build-name";

    /// <summary>The check that every <c>--mode</c> and <c>--unproduced</c> key is a report mode.</summary>
    private const string ModeKeyCheck = "mode-key";

    /// <summary>The check that every <c>npx playwright test --project</c> names a declared project.</summary>
    private const string PlaywrightCheck = "playwright-project";

    /// <summary>The check that every slice-2 heading is in the file and holds a bash line.</summary>
    private const string SectionCheck = "section";

    /// <summary>What the section check says of a slice-2 heading the file lacks outside its fenced blocks.</summary>
    private const string NotAHeading = "is not a heading of quickstart.md";

    /// <summary>The check that a bash line splits into words (its quotes are closed).</summary>
    private const string ShellCheck = "shell";

    /// <summary>The check that a <c>python3 -m unittest discover -s &lt;dir&gt;</c> line names a directory holding <see cref="WorkflowTestsFile"/>.</summary>
    private const string WorkflowTestsCheck = "workflow-tests";

    /// <summary>The SimCore workflow's tests, which the <c>## Workflow tests</c> section's directory must hold.</summary>
    private const string WorkflowTestsFile = "test_simcore_workflow.py";

    /// <summary>The CLI project every slice-2 <c>dotnet run</c> line names.</summary>
    private const string CliProject = "src/MmoEngine.SimCore.Cli";

    /// <summary>The configuration <c>npx playwright test</c> reads from the directory it runs in.</summary>
    private const string PlaywrightConfigFile = "playwright.config.mjs";

    /// <summary>The environment variable that names the build a Node agreement run records.</summary>
    private const string AgreementBuildPrefix = "AGREEMENT_BUILD=";

    /// <summary>The characters of a shell variable name.</summary>
    private static readonly SearchValues<char> ShellNameCharacters =
        SearchValues.Create("_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");

    /// <summary>The slice-2 <c>##</c> sections, in document order, as <see cref="QuickstartSections.SliceTwo"/> must list them.</summary>
    private static readonly string[] SliceTwoSections = ["Run the agreement suite", "The measurement report", "Workflow tests"];

    /// <summary>The builds of contracts/simcore-cli.md's build table, in its order, as <see cref="BuildNames.Known"/> must list them.</summary>
    private static readonly string[] ContractBuilds =
    [
        "node-interp",
        "node-interp-nojit",
        "chromium-worker",
        "firefox-worker",
        "webkit-worker",
        "node-interp-relinked",
        "node-aot",
    ];

    /// <summary>The report's WebAssembly modes (contracts/measurement-report.md <c>wasm.modes</c>): the keys of <c>--mode</c> and <c>--unproduced</c>.</summary>
    private static readonly string[] ModeKeys = ["interp-prebuilt", "interp-relinked", "aot"];

    /// <summary>The five <c>simcore</c> lines, in document order, as the command each resolves to (and its <c>--require</c>, when it has one).</summary>
    private static readonly string[] ExpectedCliCommands =
    [
        "agreement prepare",
        "agreement compare",
        "agreement prepare",
        "measure validate-report",
        "agreement compare --require node-interp-relinked,node-aot",
    ];

    /// <summary>The four <c>dotnet publish</c> lines, in document order, as the project file each directory holds.</summary>
    private static readonly string[] ExpectedPublishProjects =
    [
        "src/MmoEngine.SimCore.Wasm/MmoEngine.SimCore.Wasm.csproj",
        "src/MmoEngine.SimCore.Wasm/MmoEngine.SimCore.Wasm.csproj",
        "src/MmoEngine.SimCore.Wasm/MmoEngine.SimCore.Wasm.csproj",
        "src/MmoEngine.SimCore.Wasm/MmoEngine.SimCore.Wasm.csproj",
    ];

    /// <summary>The five <c>node</c> lines, in document order: four agreement runs and the measurement runner.</summary>
    private static readonly string[] ExpectedNodeCommands =
    [
        "node --test tools/agreement/node/agreement.test.mjs",
        "node --test tools/agreement/node/agreement.test.mjs",
        "node tools/agreement/lib/measure.mjs",
        "node --test tools/agreement/node/agreement.test.mjs",
        "node --test tools/agreement/node/agreement.test.mjs",
    ];

    /// <summary>The <c>AGREEMENT_BUILD</c> values, in document order.</summary>
    private static readonly string[] ExpectedAgreementBuilds = ["node-interp", "node-interp-nojit", "node-interp-relinked", "node-aot"];

    /// <summary>The <c>--require</c> lists, in document order.</summary>
    private static readonly string[] ExpectedRequireLists = ["node-interp-relinked,node-aot"];

    /// <summary>The <c>--mode</c> and <c>--unproduced</c> keys, in document order.</summary>
    private static readonly string[] ExpectedModeKeys = ["interp-prebuilt", "interp-relinked", "aot"];

    /// <summary>The <c>npx playwright test --project</c> projects, in document order.</summary>
    private static readonly string[] ExpectedPlaywrightProjects = ["chromium"];

    /// <summary>The <c>python3 -m unittest discover -s</c> directories, in document order.</summary>
    private static readonly string[] ExpectedWorkflowTestDirectories = ["tests/workflows"];

    /// <summary>
    /// One deliberate defect per row: the first occurrence of <see cref="QuickstartDefect.Original"/>, which
    /// the real document holds <see cref="QuickstartDefect.Occurrences"/> times, becomes
    /// <see cref="QuickstartDefect.Replacement"/>, and <see cref="QuickstartDefect.Check"/> must report a line
    /// holding <see cref="QuickstartDefect.Needle"/>.
    /// </summary>
    private static readonly QuickstartDefect[] Defects =
    [
        new("compare-misspelt", "agreement compare --work \"$WORK\"", "agreement compar --work \"$WORK\"", 1, ParseCheck, "agreement compar --work", LocatesDefect: true),
        new(
            "publish-dir-misspelt",
            "dotnet publish src/MmoEngine.SimCore.Wasm -c Release -p:UsingBrowserRuntimeWorkload=false -o \"$APP_OUT\"",
            "dotnet publish src/MmoEngine.SimCore.Wsm -c Release -p:UsingBrowserRuntimeWorkload=false -o \"$APP_OUT\"",
            1,
            PublishCheck,
            "dotnet publish src/MmoEngine.SimCore.Wsm",
            LocatesDefect: true),
        new("quote-unclosed", "agreement prepare --seeds 1-50 --work \"$WORK\"", "agreement prepare --seeds 1-50 --work \"$WORK", 1, ShellCheck, "--work \"$WORK", LocatesDefect: true),
        new("test-file-misspelt", "node --test tools/agreement/node/agreement.test.mjs", "node --test tools/agreement/node/agreemnt.test.mjs", 4, FileCheck, "node --test tools/agreement/node/agreemnt.test.mjs", LocatesDefect: true),
        new("agreement-build-unknown", "AGREEMENT_BUILD=node-interp-nojit", "AGREEMENT_BUILD=node-jit", 1, BuildNameCheck, "AGREEMENT_BUILD=node-jit", LocatesDefect: true),
        new("require-build-unknown", "--require node-interp-relinked,node-aot", "--require node-interp-relinked,node-aotx", 1, BuildNameCheck, "--require node-interp-relinked,node-aotx", LocatesDefect: true),
        new("mode-key-unknown", "--mode interp-prebuilt=", "--mode interp=", 1, ModeKeyCheck, "--mode interp=", LocatesDefect: true),
        new("playwright-project-unknown", "--project chromium", "--project chrome", 1, PlaywrightCheck, "--project chrome", LocatesDefect: true),
        new("workflow-tests-dir-misspelt", "-s tests/workflows -v", "-s tests/workflow -v", 1, WorkflowTestsCheck, "discover -s tests/workflow -v", LocatesDefect: true),

        // The directory a line runs in: Playwright run from the root finds no configuration, a node or CLI
        // path given from tools/agreement does not exist there, and a cd on its own line moves the lines
        // after it in its block.
        new(
            "playwright-run-from-the-root",
            "(cd tools/agreement && AGREEMENT_APP=\"$APP_OUT/wwwroot\" AGREEMENT_WORK=\"$WORK\" npx playwright test --project chromium)",
            "AGREEMENT_APP=\"$APP_OUT/wwwroot\" AGREEMENT_WORK=\"$WORK\" npx playwright test --project chromium",
            1,
            PlaywrightCheck,
            "AGREEMENT_WORK=\"$WORK\" npx playwright test --project chromium",
            LocatesDefect: true),
        new(
            "node-test-from-the-runners",
            "AGREEMENT_APP=\"$APP_OUT/wwwroot\" AGREEMENT_WORK=\"$WORK\" AGREEMENT_BUILD=node-interp \\\n  node --test tools/agreement/node/agreement.test.mjs\n",
            "(cd tools/agreement && AGREEMENT_APP=\"$APP_OUT/wwwroot\" AGREEMENT_WORK=\"$WORK\" AGREEMENT_BUILD=node-interp \\\n  node --test tools/agreement/node/agreement.test.mjs)\n",
            1,
            FileCheck,
            "AGREEMENT_BUILD=node-interp node --test tools/agreement/node/agreement.test.mjs",
            LocatesDefect: true),
        new(
            "cli-run-from-the-runners",
            "dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work \"$WORK\"",
            "(cd tools/agreement && dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work \"$WORK\")",
            1,
            ParseCheck,
            "(cd tools/agreement && dotnet run --project src/MmoEngine.SimCore.Cli",
            LocatesDefect: true),
        new(
            "cd-moves-the-next-line",
            "AGREEMENT_APP=\"$APP_OUT/wwwroot\" AGREEMENT_WORK=\"$WORK\" AGREEMENT_BUILD=node-interp-nojit \\",
            "cd tools/agreement\nAGREEMENT_APP=\"$APP_OUT/wwwroot\" AGREEMENT_WORK=\"$WORK\" AGREEMENT_BUILD=node-interp-nojit \\",
            1,
            FileCheck,
            "AGREEMENT_BUILD=node-interp-nojit node --test tools/agreement/node/agreement.test.mjs",
            LocatesDefect: false),

        // A missing heading has no line of its own: the failure names the heading the file lacks. A fenced
        // Markdown sample holding the heading is block content, not the heading, so it does not stand in
        // for the renamed one.
        new("section-renamed", "\n## Workflow tests\n", "\n## Workflow test\n", 1, SectionCheck, "## Workflow tests", LocatesDefect: false, Message: NotAHeading),
        new(
            "section-heading-only-in-a-fence",
            "\n## Workflow tests\n",
            "\n```markdown\n## Workflow tests\n```\n\n## Workflow test\n",
            1,
            SectionCheck,
            "## Workflow tests",
            LocatesDefect: false,
            Message: NotAHeading),
    ];

    /// <summary>The names of <see cref="Defects"/>, one row each.</summary>
    public static TheoryData<string> DefectNames => [.. Defects.Select(defect => defect.Name)];

    [Fact]
    public void EverySliceTwoQuickstartCommandAndPathIsReal()
    {
        var markdown = RepoPaths.ReadAllText("specs", "015-shared-simulation-core", "quickstart.md");
        using var shellPaths = TempDirectories.Create("quickstart-slice-two");

        // Given: the sections and the build list the checks rely on are the ones the contracts name.
        Assert.Equal(SliceTwoSections, QuickstartSections.SliceTwo);
        Assert.Equal(ContractBuilds, BuildNames.Known);

        // When: every slice-2 check runs on the document.
        var failures = CheckSliceTwo(markdown, shellPaths.Path);

        // Then: none fails. The workflow-tests check is the one that finds the workflow tests' file, through
        // the directory the last section's unittest line names.
        Assert.True(failures.Count == 0, $"quickstart.md's slice-2 sections fail {failures.Count} checks:\n{string.Join('\n', failures)}");
    }

    [Theory]
    [MemberData(nameof(DefectNames))]
    public void SliceTwoChecksReportEachDefect(string defect)
    {
        var row = Array.Find(Defects, candidate => candidate.Name == defect)
            ?? throw new ArgumentException($"no defect row named {defect}", nameof(defect));
        var markdown = RepoPaths.ReadAllText("specs", "015-shared-simulation-core", "quickstart.md").ReplaceLineEndings("\n");
        using var shellPaths = TempDirectories.Create("quickstart-defect");

        // Given: the real document passes the row's check, and holds the row's text as often as the row says.
        var real = CheckSliceTwo(markdown, shellPaths.Path).Where(failure => failure.Check == row.Check).ToArray();
        Assert.True(real.Length == 0, $"the real quickstart.md already fails the {row.Check} check:\n{string.Join('\n', real)}");
        var occurrences = Regex.Count(markdown, Regex.Escape(row.Original));
        Assert.True(
            occurrences == row.Occurrences,
            $"defect {defect}: quickstart.md holds '{row.Original}' {occurrences} times, the row expects {row.Occurrences}");

        // When: the first occurrence is replaced, and every check runs on that in-memory copy.
        var at = markdown.IndexOf(row.Original, StringComparison.Ordinal);
        var mutated = string.Concat(markdown.AsSpan(0, at), row.Replacement, markdown.AsSpan(at + row.Original.Length));
        var line = 1 + markdown.AsSpan(0, at).Count('\n');
        var failures = CheckSliceTwo(mutated, shellPaths.Path);

        // Then: the row's check reports the defect's line, by its text and, where it has one, its line number.
        var reported = failures
            .Where(failure => failure.Check == row.Check && failure.Line.Contains(row.Needle, StringComparison.Ordinal))
            .Where(failure => !row.LocatesDefect || (failure.First <= line && line <= failure.Last))
            .Where(failure => row.Message is null || failure.Message == row.Message)
            .ToArray();
        Assert.True(
            reported.Length > 0,
            $"defect {defect} at line {line} should fail the {row.Check} check on a line holding '{row.Needle}'{(row.Message is null ? string.Empty : $" with '{row.Message}'")}; the checks reported:\n{string.Join('\n', failures)}");
    }

    /// <summary>
    /// The Playwright-project check reads a project's <c>name:</c> from the configuration's code only: a
    /// name in a <c>//</c> or <c>/* */</c> comment is not a project, and a <c>//</c> inside a string does not
    /// start a comment. The committed configuration declares its three projects.
    /// </summary>
    [Theory]
    [InlineData("projects: [{ name: 'chromium' }]", "chromium", true)]
    [InlineData("projects: [{ name: \"chromium\" }]", "chromium", true)]
    [InlineData("projects: [{ name: 'firefox' }]", "chromium", false)]
    [InlineData("projects: [{ name: 'chromium\" }]", "chromium", false)]
    [InlineData("projects: [{ name: 'chromium' }]", "", false)]
    [InlineData("// { name: 'chromium' }\nprojects: [{ name: 'firefox' }]", "chromium", false)]
    [InlineData("/* { name: 'chromium' } */ projects: [{ name: 'firefox' }]", "chromium", false)]
    [InlineData("/**\n * `--project chromium`, { name: 'chromium' }\n */\nprojects: []", "chromium", false)]
    [InlineData("projects: [{ url: 'http://127.0.0.1/', name: 'chromium' }]", "chromium", true)]
    [InlineData("projects: [{ testname: 'chromium' }]", "chromium", false)]
    public void PlaywrightProjectsAreReadOutsideComments(string config, string project, bool declared)
    {
        Assert.Equal(declared, DeclaresProject(config, project));
    }

    /// <summary>
    /// <see cref="QuickstartSections.Headings"/>, which the section check reads, takes a <c>## </c> line
    /// as a heading only outside a fenced block, with <see cref="QuickstartSections.BashLines"/>' fence
    /// handling: a block opens on a line whose trimmed start is three backticks and closes on one of only
    /// backticks, and a fence line with an info string inside a block is block content. Each heading keeps
    /// its own line number, so a fenced copy earlier in the file never stands in for it.
    /// </summary>
    [Theory]
    [InlineData("## A\ntext\n## B", "A@1 B@3")]
    [InlineData("```markdown\n## A\n```\n## A", "A@4")]
    [InlineData("```bash\n## A\n```", "")]
    [InlineData("  ```\n## A\n```bash\n## B\n```\n## C", "C@6")]
    [InlineData("### A\n##B\n ## C\n## D ", "D@4")]
    [InlineData("## A\r\n```\r\n## B\r\n```\r\n## C", "A@1 C@5")]
    public void QuickstartHeadingsAreReadOutsideFences(string markdown, string expected)
    {
        var headings = QuickstartSections.Headings(markdown).Select(heading => $"{heading.Heading}@{heading.Line}");

        Assert.Equal(expected, string.Join(' ', headings));
    }

    [Theory]
    [InlineData("chromium")]
    [InlineData("firefox")]
    [InlineData("webkit")]
    public void TheCommittedPlaywrightConfigDeclaresEachBrowser(string project)
    {
        var config = RepoPaths.ReadAllText("tools", "agreement", PlaywrightConfigFile);

        Assert.True(DeclaresProject(config, project), $"tools/agreement/{PlaywrightConfigFile} does not declare the project '{project}' outside its comments");
    }

    /// <summary>
    /// Every slice-2 check run on <paramref name="markdown"/>, the text of quickstart.md or a mutated copy.
    /// Shell variables in its lines stand for paths under <paramref name="shellPaths"/>.
    /// </summary>
    private static List<QuickstartFailure> CheckSliceTwo(string markdown, string shellPaths)
    {
        var failures = new List<QuickstartFailure>();
        failures.AddRange(CheckSections(markdown));
        var commands = SliceTwoCommands(markdown, shellPaths, failures);
        failures.AddRange(CheckCliCommands(commands));
        failures.AddRange(CheckPublishDirectories(commands));
        failures.AddRange(CheckNodeFiles(commands));
        failures.AddRange(CheckBuildNames(commands));
        failures.AddRange(CheckModeKeys(commands));
        failures.AddRange(CheckPlaywrightProjects(commands));
        failures.AddRange(CheckWorkflowTests(commands));
        return failures;
    }

    /// <summary>
    /// Every heading of <see cref="QuickstartSections.SliceTwo"/> is a <c>##</c> heading of the file and
    /// yields a bash line. The headings come from <see cref="QuickstartSections.Headings"/>, which reads the
    /// fences as <see cref="QuickstartSections.BashLines"/> does, so a <c>## </c> line inside a fenced block
    /// is no heading to either.
    /// </summary>
    private static List<QuickstartFailure> CheckSections(string markdown)
    {
        var headings = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (heading, line) in QuickstartSections.Headings(markdown))
        {
            headings.TryAdd(heading, line);
        }

        var failures = new List<QuickstartFailure>();
        foreach (var heading in QuickstartSections.SliceTwo)
        {
            if (!headings.TryGetValue(heading, out var number))
            {
                failures.Add(new QuickstartFailure(SectionCheck, 0, 0, $"## {heading}", NotAHeading));
            }
            else if (!QuickstartSections.BashLines(markdown, heading).Any())
            {
                failures.Add(new QuickstartFailure(SectionCheck, number, number, $"## {heading}", "has no line in a ```bash block"));
            }
        }

        return failures;
    }

    /// <summary>
    /// The simple commands of every bash line of the slice-2 sections, in document order, each with the
    /// directory it runs in. Each <c>```bash</c> block starts in the repository root, as the quickstart says
    /// its commands run, and a <c>cd</c> outside <c>( ... )</c> carries to the block's later lines. A line
    /// whose quotes do not close adds a <see cref="ShellCheck"/> failure to <paramref name="failures"/>
    /// instead, and leaves the directory as it was.
    /// </summary>
    private static List<ShellCommand> SliceTwoCommands(string markdown, string shellPaths, List<QuickstartFailure> failures)
    {
        var commands = new List<ShellCommand>();
        foreach (var heading in QuickstartSections.SliceTwo)
        {
            var fence = 0;
            var directory = RepoPaths.Root;
            foreach (var line in QuickstartSections.BashLines(markdown, heading))
            {
                if (line.Fence != fence)
                {
                    fence = line.Fence;
                    directory = RepoPaths.Root;
                }

                var (simple, after, error) = SplitShell(line.Text, shellPaths, directory);
                if (error is not null)
                {
                    failures.Add(new QuickstartFailure(ShellCheck, line.First, line.Last, line.Text, error));
                    continue;
                }

                directory = after;
                foreach (var (words, runsIn) in simple)
                {
                    var assignments = words.TakeWhile(IsAssignment).ToArray();
                    commands.Add(new ShellCommand(line.First, line.Last, line.Text, runsIn, assignments, words[assignments.Length..]));
                }
            }
        }

        return commands;
    }

    /// <summary>
    /// Every <c>dotnet run</c> is <c>dotnet run --project src/MmoEngine.SimCore.Cli [-c Release] -- &lt;args&gt;</c>,
    /// and its arguments parse against <see cref="Program.CreateRootCommand()"/> with no error.
    /// </summary>
    private static List<QuickstartFailure> CheckCliCommands(List<ShellCommand> commands)
    {
        var failures = new List<QuickstartFailure>();
        var matched = new List<(ShellCommand Command, string Value)>();
        var root = Program.CreateRootCommand();
        foreach (var command in commands.Where(command => command.Words is ["dotnet", "run", ..]))
        {
            var separator = Array.IndexOf(command.Words, "--");
            var options = separator < 0 ? command.Words : command.Words[..separator];
            if (options is not (["dotnet", "run", "--project", CliProject] or ["dotnet", "run", "--project", CliProject, "-c", "Release"]))
            {
                failures.Add(FailureOn(ParseCheck, command, $"is not 'dotnet run --project {CliProject} [-c Release] -- <args>'"));
                matched.Add((command, string.Join(' ', command.Words)));
                continue;
            }

            if (Resolve(command, CliProject) != RepoPaths.Combine(CliProject))
            {
                failures.Add(FailureOn(ParseCheck, command, $"runs in {Where(command)}, where --project {CliProject} is not the CLI project"));
            }

            var arguments = command.Words[(separator + 1)..];
            var parse = root.Parse(arguments);
            if (parse.Errors.Count > 0)
            {
                failures.Add(FailureOn(ParseCheck, command, $"simcore {string.Join(' ', arguments)} does not parse: {string.Join("; ", parse.Errors.Select(e => e.Message))}"));
            }

            var resolved = CommandPath(parse);
            var require = Array.IndexOf(arguments, "--require");
            matched.Add((command, require >= 0 && require + 1 < arguments.Length ? $"{resolved} --require {arguments[require + 1]}" : resolved));
        }

        failures.AddRange(CompareMatched(ParseCheck, "simcore commands", matched, ExpectedCliCommands));
        return failures;
    }

    /// <summary>
    /// Every <c>dotnet publish &lt;dir&gt;</c> names, from the directory it runs in, a repository directory that
    /// holds exactly one <c>.csproj</c>.
    /// </summary>
    private static List<QuickstartFailure> CheckPublishDirectories(List<ShellCommand> commands)
    {
        var failures = new List<QuickstartFailure>();
        var matched = new List<(ShellCommand Command, string Value)>();
        foreach (var command in commands.Where(command => command.Words is ["dotnet", "publish", ..]))
        {
            var directory = command.Words.Length > 2 ? command.Words[2] : string.Empty;
            var resolved = Resolve(command, directory);
            var projects = resolved is not null && Directory.Exists(resolved) ? Directory.GetFiles(resolved, "*.csproj") : null;
            if (projects is not [var project])
            {
                var holds = projects is null ? $"is not a repository directory from {Where(command)}" : $"holds {projects.Length} .csproj files, not one";
                failures.Add(FailureOn(PublishCheck, command, $"'{directory}' {holds}"));
                matched.Add((command, directory));
                continue;
            }

            matched.Add((command, $"{directory.TrimEnd('/')}/{Path.GetFileName(project)}"));
        }

        failures.AddRange(CompareMatched(PublishCheck, "published projects", matched, ExpectedPublishProjects));
        return failures;
    }

    /// <summary>Every <c>node --test &lt;path&gt;</c> and <c>node &lt;path&gt;</c> names, from the directory it runs in, a repository file that exists.</summary>
    private static List<QuickstartFailure> CheckNodeFiles(List<ShellCommand> commands)
    {
        var failures = new List<QuickstartFailure>();
        var matched = new List<(ShellCommand Command, string Value)>();
        foreach (var command in commands.Where(command => command.Words is ["node", ..]))
        {
            var test = command.Words is ["node", "--test", ..];
            var index = test ? 2 : 1;
            var path = command.Words.Length > index ? command.Words[index] : string.Empty;
            matched.Add((command, test ? $"node --test {path}" : $"node {path}"));
            var resolved = Resolve(command, path);
            if (resolved is null || !File.Exists(resolved))
            {
                failures.Add(FailureOn(FileCheck, command, $"'{path}' is not a file in the repository from {Where(command)}"));
            }
        }

        failures.AddRange(CompareMatched(FileCheck, "node commands", matched, ExpectedNodeCommands));
        return failures;
    }

    /// <summary>
    /// Every <c>AGREEMENT_BUILD=&lt;name&gt;</c> value and every name of a <c>--require</c> list is one of
    /// <see cref="BuildNames.Known"/>, the builds <c>agreement compare</c> knows, and is not the
    /// <see cref="BuildNames.Server"/> reference.
    /// </summary>
    private static List<QuickstartFailure> CheckBuildNames(List<ShellCommand> commands)
    {
        var failures = new List<QuickstartFailure>();
        var builds = new List<(ShellCommand Command, string Value)>();
        var requires = new List<(ShellCommand Command, string Value)>();
        foreach (var command in commands)
        {
            foreach (var word in command.Assignments.Concat(command.Words).Where(word => word.StartsWith(AgreementBuildPrefix, StringComparison.Ordinal)))
            {
                var name = word[AgreementBuildPrefix.Length..];
                builds.Add((command, name));
                failures.AddRange(CheckBuildName(command, name, AgreementBuildPrefix.TrimEnd('=')));
            }

            for (var i = 0; i < command.Words.Length; i++)
            {
                if (command.Words[i] != "--require")
                {
                    continue;
                }

                var list = i + 1 < command.Words.Length ? command.Words[i + 1] : string.Empty;
                requires.Add((command, list));
                foreach (var name in list.Split(','))
                {
                    failures.AddRange(CheckBuildName(command, name, "--require"));
                }
            }
        }

        failures.AddRange(CompareMatched(BuildNameCheck, "AGREEMENT_BUILD values", builds, ExpectedAgreementBuilds));
        failures.AddRange(CompareMatched(BuildNameCheck, "--require lists", requires, ExpectedRequireLists));
        return failures;
    }

    private static IEnumerable<QuickstartFailure> CheckBuildName(ShellCommand command, string name, string where)
    {
        if (name == BuildNames.Server)
        {
            yield return FailureOn(BuildNameCheck, command, $"{where} names '{name}', the reference every build is compared with, not a build");
        }
        else if (!BuildNames.Known.Contains(name, StringComparer.Ordinal))
        {
            yield return FailureOn(BuildNameCheck, command, $"{where} names '{name}', which is not one of the builds agreement compare knows ({string.Join(", ", BuildNames.Known)})");
        }
    }

    /// <summary>
    /// Every <c>--mode &lt;key&gt;=</c> and <c>--unproduced &lt;key&gt;=</c> key is one of <see cref="ModeKeys"/>, and
    /// the command that runs <c>measure.mjs</c> names each of them exactly once.
    /// </summary>
    private static List<QuickstartFailure> CheckModeKeys(List<ShellCommand> commands)
    {
        var failures = new List<QuickstartFailure>();
        var matched = new List<(ShellCommand Command, string Value)>();
        foreach (var command in commands)
        {
            var keys = new List<string>();
            for (var i = 0; i < command.Words.Length; i++)
            {
                if (command.Words[i] is not ("--mode" or "--unproduced"))
                {
                    continue;
                }

                var value = i + 1 < command.Words.Length ? command.Words[i + 1] : string.Empty;
                var equals = value.IndexOf('=', StringComparison.Ordinal);
                var key = equals < 0 ? value : value[..equals];
                keys.Add(key);
                matched.Add((command, key));
                if (equals < 0)
                {
                    failures.Add(FailureOn(ModeKeyCheck, command, $"{command.Words[i]} '{value}' has no '=' after its mode"));
                }
                else if (!ModeKeys.Contains(key, StringComparer.Ordinal))
                {
                    failures.Add(FailureOn(ModeKeyCheck, command, $"{command.Words[i]} names '{key}', which is not a report mode ({string.Join(", ", ModeKeys)})"));
                }
            }

            if (command.Words is ["node", var script, ..] && Path.GetFileName(script) == "measure.mjs")
            {
                foreach (var mode in ModeKeys)
                {
                    var count = keys.Count(key => key == mode);
                    if (count != 1)
                    {
                        failures.Add(FailureOn(ModeKeyCheck, command, $"names the mode '{mode}' {count} times, not once"));
                    }
                }
            }
        }

        failures.AddRange(CompareMatched(ModeKeyCheck, "--mode and --unproduced keys", matched, ExpectedModeKeys));
        return failures;
    }

    /// <summary>
    /// Every <c>npx playwright test</c> runs in a repository directory that holds <see cref="PlaywrightConfigFile"/>,
    /// which Playwright reads from the directory it runs in, and every <c>--project &lt;p&gt;</c> names a
    /// project that file declares with <c>name: '&lt;p&gt;'</c> outside a comment (<see cref="DeclaresProject"/>).
    /// </summary>
    private static List<QuickstartFailure> CheckPlaywrightProjects(List<ShellCommand> commands)
    {
        var failures = new List<QuickstartFailure>();
        var matched = new List<(ShellCommand Command, string Value)>();
        foreach (var command in commands.Where(command => command.Words is ["npx", "playwright", "test", ..]))
        {
            var configPath = Resolve(command, PlaywrightConfigFile);
            var config = configPath is not null && File.Exists(configPath)
                ? RepoPaths.ReadAllText(Path.GetRelativePath(RepoPaths.Root, configPath))
                : null;
            if (config is null)
            {
                failures.Add(FailureOn(PlaywrightCheck, command, $"runs in {Where(command)}, which has no {PlaywrightConfigFile}"));
            }

            var at = Array.IndexOf(command.Words, "--project");
            if (at < 0)
            {
                continue;
            }

            var project = at + 1 < command.Words.Length ? command.Words[at + 1] : string.Empty;
            matched.Add((command, project));
            if (config is not null && !DeclaresProject(config, project))
            {
                failures.Add(FailureOn(PlaywrightCheck, command, $"'{project}' is not a project {Path.GetRelativePath(RepoPaths.Root, configPath!)} declares"));
            }
        }

        failures.AddRange(CompareMatched(PlaywrightCheck, "Playwright projects", matched, ExpectedPlaywrightProjects));
        return failures;
    }

    /// <summary>
    /// Every <c>python3 -m unittest discover -s &lt;dir&gt;</c> names, from the directory it runs in, a
    /// repository directory that holds <see cref="WorkflowTestsFile"/>, the tests the <c>## Workflow tests</c>
    /// section describes.
    /// </summary>
    private static List<QuickstartFailure> CheckWorkflowTests(List<ShellCommand> commands)
    {
        var failures = new List<QuickstartFailure>();
        var matched = new List<(ShellCommand Command, string Value)>();
        foreach (var command in commands.Where(command => command.Words is ["python3", "-m", "unittest", "discover", ..]))
        {
            var at = Array.IndexOf(command.Words, "-s");
            var directory = at >= 0 && at + 1 < command.Words.Length ? command.Words[at + 1] : string.Empty;
            matched.Add((command, directory));
            var resolved = Resolve(command, directory);
            if (resolved is null || !File.Exists(Path.Combine(resolved, WorkflowTestsFile)))
            {
                failures.Add(FailureOn(WorkflowTestsCheck, command, $"'{directory}' is not a repository directory from {Where(command)} that holds {WorkflowTestsFile}"));
            }
        }

        failures.AddRange(CompareMatched(WorkflowTestsCheck, "workflow-test directories", matched, ExpectedWorkflowTestDirectories));
        return failures;
    }

    /// <summary>
    /// Exact extraction: the values a check matched, in document order, are <paramref name="expected"/>, so a
    /// check that matches nothing, or a different number of lines, fails.
    /// </summary>
    private static IEnumerable<QuickstartFailure> CompareMatched(
        string check,
        string what,
        List<(ShellCommand Command, string Value)> matched,
        string[] expected)
    {
        if (matched.Select(m => m.Value).SequenceEqual(expected, StringComparer.Ordinal))
        {
            return [];
        }

        var found = matched.Count == 0 ? "none" : string.Join("; ", matched.Select(m => $"line {m.Command.First}: {m.Value}"));
        return [new QuickstartFailure(check, 0, 0, $"[{string.Join(", ", expected)}]", $"matched {matched.Count} {what} ({found}), expected {expected.Length}")];
    }

    /// <summary>
    /// The command a parse resolved to, as the names from below the root down, for example
    /// <c>agreement compare</c>.
    /// </summary>
    private static string CommandPath(ParseResult parse)
    {
        var names = new List<string>();
        for (var result = parse.CommandResult; result is not null && result.Command is not RootCommand; result = result.Parent as CommandResult)
        {
            names.Insert(0, result.Command.Name);
        }

        return string.Join(' ', names);
    }

    /// <summary>
    /// The full path that <paramref name="path"/>, a relative path, names when <paramref name="command"/> runs
    /// in its directory; null when the path is empty, an option or absolute, or leaves the repository.
    /// </summary>
    private static string? Resolve(ShellCommand command, string path)
    {
        if (path.Length == 0 || path.StartsWith('-') || Path.IsPathRooted(path))
        {
            return null;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(command.Directory, path)));
        return full.StartsWith(RepoPaths.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>The directory a command runs in, for a failure message: the repository root, or its path from there.</summary>
    private static string Where(ShellCommand command) =>
        command.Directory == RepoPaths.Root ? "the repository root" : $"'{Path.GetRelativePath(RepoPaths.Root, command.Directory)}'";

    /// <summary>
    /// Whether <paramref name="config"/>, the text of a Playwright configuration, declares the project
    /// <paramref name="project"/> with <c>name: '&lt;project&gt;'</c> (or double quotes) outside its
    /// <c>//</c> and <c>/* */</c> comments.
    /// </summary>
    private static bool DeclaresProject(string config, string project) =>
        project.Length > 0 && Regex.IsMatch(WithoutComments(config), $"\\bname\\s*:\\s*(['\"]){Regex.Escape(project)}\\1");

    /// <summary>
    /// <paramref name="script"/> with its <c>//</c> and <c>/* */</c> comments removed (each block comment
    /// becomes one space, each line comment ends before its newline); a <c>//</c> or <c>/*</c> inside a
    /// quoted or template string is kept.
    /// </summary>
    private static string WithoutComments(string script)
    {
        var text = new StringBuilder(script.Length);
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';
            if (c is '\'' or '"' or '`')
            {
                var end = i + 1;
                while (end < script.Length && script[end] != c)
                {
                    end += script[end] == '\\' ? 2 : 1;
                }

                end = System.Math.Min(end, script.Length - 1);
                text.Append(script, i, end - i + 1);
                i = end;
            }
            else if (c == '/' && next == '/')
            {
                var newline = script.IndexOf('\n', i);
                if (newline < 0)
                {
                    break;
                }

                i = newline - 1;
            }
            else if (c == '/' && next == '*')
            {
                var close = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                text.Append(' ');
                i = close + 1;
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>A leading <c>NAME=value</c> word of a simple command: a shell name, then <c>=</c>.</summary>
    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=', StringComparison.Ordinal);
        return equals > 0
            && (word[0] == '_' || char.IsAsciiLetter(word[0]))
            && !word.AsSpan(0, equals).ContainsAnyExcept(ShellNameCharacters);
    }

    /// <summary>
    /// Splits one bash line into simple commands of words, as bash would for the quickstart's lines: single
    /// quotes are literal, double quotes group and expand, a backslash escapes the next character, a
    /// <c>#</c> that starts a word begins a comment, and <c>&amp;</c>, <c>|</c>, <c>;</c>, <c>(</c> and
    /// <c>)</c> end a command. <c>$NAME</c> and <c>${NAME}</c> become <c>&lt;shellPaths&gt;/NAME</c>; any
    /// other <c>$</c> is kept. The error is set when a quote does not close.
    /// </summary>
    /// <remarks>
    /// Each command comes with the directory it runs in. The line starts in <paramref name="directory"/>;
    /// <c>cd &lt;dir&gt;</c> moves the commands after it to <c>&lt;dir&gt;</c>, resolved from there (a bare
    /// <c>cd</c> goes to <c>$HOME</c>, which is outside the repository), and a <c>(</c> opens a subshell whose
    /// <c>cd</c> lasts until its <c>)</c>. The directory after the line, outside any subshell, is returned
    /// for the next line of the block.
    /// </remarks>
    private static (List<(string[] Words, string Directory)> Commands, string Directory, string? Error) SplitShell(
        string text,
        string shellPaths,
        string directory)
    {
        var commands = new List<(string[] Words, string Directory)>();
        var subshells = new Stack<string>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;

        void EndWord()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }

        void EndCommand()
        {
            EndWord();
            if (words.Count > 0)
            {
                commands.Add(([.. words], directory));
                if (words is ["cd", var target])
                {
                    directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(directory, target)));
                }
                else if (words is ["cd", ..])
                {
                    directory = Path.Combine(shellPaths, "HOME");
                }

                words.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                var close = text.IndexOf('\'', i + 1);
                if (close < 0)
                {
                    return ([], directory, $"the ' at column {i + 1} does not close");
                }

                word.Append(text, i + 1, close - i - 1);
                inWord = true;
                i = close;
            }
            else if (c == '"')
            {
                var close = ReadDoubleQuoted(text, i, word, shellPaths);
                if (close < 0)
                {
                    return ([], directory, $"the \" at column {i + 1} does not close");
                }

                inWord = true;
                i = close;
            }
            else if (c == '\\')
            {
                if (i + 1 < text.Length)
                {
                    word.Append(text[++i]);
                }

                inWord = true;
            }
            else if (c == '$')
            {
                i = Expand(text, i, word, shellPaths);
                inWord = true;
            }
            else if (c == '#' && !inWord)
            {
                break;
            }
            else if (c == '(')
            {
                EndCommand();
                subshells.Push(directory);
            }
            else if (c == ')')
            {
                EndCommand();
                if (subshells.Count > 0)
                {
                    directory = subshells.Pop();
                }
            }
            else if (c is '&' or '|' or ';')
            {
                EndCommand();
            }
            else if (char.IsWhiteSpace(c))
            {
                EndWord();
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        EndCommand();
        return (commands, subshells.Count > 0 ? subshells.Last() : directory, null);
    }

    /// <summary>
    /// Appends the double-quoted string that opens at <paramref name="open"/> to <paramref name="word"/>,
    /// expanding its variables, and returns the index of its closing quote, or -1 when it does not close.
    /// </summary>
    private static int ReadDoubleQuoted(string text, int open, StringBuilder word, string shellPaths)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                return i;
            }

            if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
            {
                word.Append(text[++i]);
            }
            else if (c == '$')
            {
                i = Expand(text, i, word, shellPaths);
            }
            else
            {
                word.Append(c);
            }
        }

        return -1;
    }

    /// <summary>
    /// Appends the expansion of the <c>$</c> at <paramref name="dollar"/> to <paramref name="word"/> and
    /// returns the index of its last character: <c>$NAME</c> and <c>${NAME}</c> become
    /// <c>&lt;shellPaths&gt;/NAME</c>, and any other <c>$</c> stays as it is.
    /// </summary>
    private static int Expand(string text, int dollar, StringBuilder word, string shellPaths)
    {
        var start = dollar + 1;
        var braced = start < text.Length && text[start] == '{';
        if (braced)
        {
            start++;
        }

        var end = start;
        while (end < text.Length && (text[end] == '_' || char.IsAsciiLetter(text[end]) || (end > start && char.IsAsciiDigit(text[end]))))
        {
            end++;
        }

        if (end == start || (braced && (end >= text.Length || text[end] != '}')))
        {
            word.Append('$');
            return dollar;
        }

        word.Append(Path.Combine(shellPaths, text[start..end]));
        return braced ? end : end - 1;
    }

    private static QuickstartFailure FailureOn(string check, ShellCommand command, string message) =>
        new(check, command.First, command.Last, command.Line, message);

    /// <summary>
    /// One simple command of a quickstart bash line: the 1-based lines that line spans, its text as
    /// quickstart.md writes it (continuations joined), the full path of the directory it runs in, its
    /// leading <c>NAME=value</c> words, and the words after them.
    /// </summary>
    private sealed record ShellCommand(int First, int Last, string Line, string Directory, string[] Assignments, string[] Words);

    /// <summary>
    /// One failed check: which check, the 1-based lines of the quickstart line it failed on (0 when it is
    /// about no single line), that line's text or what the check expected, and why.
    /// </summary>
    private sealed record QuickstartFailure(string Check, int First, int Last, string Line, string Message)
    {
        public override string ToString()
        {
            var where = First == 0 ? "(no single line)" : First == Last ? $"line {First}" : $"lines {First}-{Last}";
            return $"quickstart.md {where} [{Check}] {Message}: {Line}";
        }
    }

    /// <summary>
    /// One row of <see cref="SliceTwoChecksReportEachDefect"/>. <see cref="LocatesDefect"/> is false only
    /// for a defect whose failure is not on the line it changes: a renamed heading, which removes the line a
    /// check would name, or a <c>cd</c> on a line of its own, which moves the lines after it. A row with a
    /// <see cref="Message"/> also requires the failure to say exactly that, for a check that can fail the
    /// same line in more than one way.
    /// </summary>
    private sealed record QuickstartDefect(
        string Name,
        string Original,
        string Replacement,
        int Occurrences,
        string Check,
        string Needle,
        bool LocatesDefect,
        string? Message = null);
}
