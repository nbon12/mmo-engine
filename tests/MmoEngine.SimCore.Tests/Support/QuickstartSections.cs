namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// The <c>##</c> sections of specs/015-shared-simulation-core/quickstart.md whose commands
/// <c>Cli/QuickstartTests</c> checks, and the readers that give the document's headings and a section's
/// bash lines.
/// </summary>
/// <remarks>
/// <see cref="Headings"/> and <see cref="BashLines"/> take the document's text, not its path, so a test can
/// pass an in-memory copy with a deliberate defect as well as the committed file (opened read-only through
/// <see cref="RepoPaths"/>). Both read the document through one scan of its fenced blocks, which follows
/// CommonMark's fence rules (backtick and tilde fences, a closing fence at least as long as the opening
/// one), so they agree on which lines are headings and which lie in a block.
/// </remarks>
public static class QuickstartSections
{
    private const string HeadingPrefix = "## ";

    private const string BashLanguage = "bash";

    private const char Backtick = '`';

    private const char Tilde = '~';

    /// <summary>The fewest backticks or tildes that make a fence (CommonMark 4.5).</summary>
    private const int MinFenceLength = 3;

    /// <summary>The most spaces a fence may be indented by; a fourth makes the line no fence (CommonMark 4.5).</summary>
    private const int MaxFenceIndent = 3;

    /// <summary>What may follow a closing fence's run: spaces and tabs only.</summary>
    private const string FenceTrailingWhitespace = " \t";

    /// <summary>The five slice-1 sections <c>EverySliceOneQuickstartCommandParses</c> collects, in document order.</summary>
    public static IReadOnlyList<string> SliceOne { get; } =
    [
        "Check a configuration file",
        "Reproduce the test world",
        "Rewrite the golden fixtures",
        "Verify the committed streams",
        "Regenerate the committed streams",
    ];

    /// <summary>
    /// The three slice-2 sections <c>EverySliceTwoQuickstartCommandAndPathIsReal</c> checks, in document
    /// order: the agreement suite, the measurement report and the workflow tests.
    /// </summary>
    public static IReadOnlyList<string> SliceTwo { get; } =
    [
        "Run the agreement suite",
        "The measurement report",
        "Workflow tests",
    ];

    /// <summary>
    /// Every <c>##</c> heading of <paramref name="markdown"/>, in document order, with its 1-based line:
    /// the text after <c>## </c>, trimmed.
    /// </summary>
    /// <remarks>
    /// A <c>## </c> line inside a fenced block is part of the block, not a heading. The fences are read as
    /// <see cref="BashLines"/> reads them, through the same scan, so the two never disagree on which lines
    /// are headings.
    /// </remarks>
    public static IReadOnlyList<(string Heading, int Line)> Headings(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        return [.. Scan(markdown).Where(line => line.Kind == LineKind.Heading).Select(line => (HeadingText(line.Text), line.Number))];
    }

    /// <summary>
    /// The command lines of every bash block (a fenced block whose info string's first word is <c>bash</c>,
    /// such as <c>```bash</c> or <c>~~~bash</c>) between the line <c>## <paramref name="heading"/></c> and
    /// the next <c>##</c> heading of <paramref name="markdown"/>, in document order.
    /// </summary>
    /// <remarks>
    /// A line that ends in <c>\</c> is joined to the next with one space: the backslash, any whitespace
    /// before it and the next line's leading whitespace are dropped, and the entry spans both lines. Blank
    /// lines are skipped. Each entry carries the line of the fence that opens its block, so a caller can
    /// start each block in the repository root, as the quickstart says its commands run. A <c>## </c> line
    /// inside a fenced block is part of the block, not a heading (<see cref="Headings"/> reads the same
    /// scan), and so is a shorter fence or one of the other character, such as the <c>```bash</c> sample a
    /// <c>````markdown</c> block quotes, whose lines are therefore no bash lines. A heading the document
    /// lacks yields an empty list, so the caller's section check reports it rather than an exception.
    /// </remarks>
    public static IReadOnlyList<QuickstartBashLine> BashLines(string markdown, string heading)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(heading);

        var result = new List<QuickstartBashLine>();
        var inSection = false;
        var fence = 0;
        var pending = (First: 0, Text: string.Empty);
        foreach (var line in Scan(markdown))
        {
            switch (line.Kind)
            {
                case LineKind.Heading when inSection:
                    return result;
                case LineKind.Heading:
                    inSection = HeadingText(line.Text) == heading;
                    break;
                case LineKind.FenceOpen:
                    fence = line.Number;
                    break;
                case LineKind.FenceClose:
                    if (inSection && line.Bash && pending.First > 0)
                    {
                        result.Add(new QuickstartBashLine(pending.First, line.Number - 1, pending.Text, fence));
                        pending = (0, string.Empty);
                    }

                    break;
                case LineKind.Block when inSection && line.Bash && (pending.First > 0 || line.Text.Trim().Length > 0):
                    var continues = line.Text.EndsWith('\\');
                    var text = continues ? line.Text[..^1].TrimEnd() : line.Text;
                    pending = pending.First == 0
                        ? (line.Number, text)
                        : (pending.First, text.TrimStart().Length == 0 ? pending.Text : $"{pending.Text} {text.TrimStart()}");
                    if (!continues)
                    {
                        result.Add(new QuickstartBashLine(pending.First, line.Number, pending.Text, fence));
                        pending = (0, string.Empty);
                    }

                    break;
                default:
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Every line of <paramref name="markdown"/>, in order, with what it is to the fences around it, read
    /// as CommonMark reads fenced code blocks. Outside a block, a line that starts with <c>## </c> is a
    /// heading, and a fence opens a block: up to three spaces, then a run of at least three backticks or
    /// at least three tildes, then the info string, which after backticks holds no backtick. The block is
    /// <see cref="ScannedLine.Bash"/> when the info string's first word is <c>bash</c>. Inside a block,
    /// only a fence of the opening run's character, at least as long as it, with nothing but spaces or tabs
    /// after it closes the block. Every other line is block content: a shorter run, a run of the other
    /// character, a fence line with an info string and a <c>## </c> line among them. A block the document
    /// does not close runs to its end.
    /// </summary>
    private static IEnumerable<ScannedLine> Scan(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        OpenFence? open = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (open is { } block)
            {
                if (Closes(block, line))
                {
                    open = null;
                    yield return new ScannedLine(i + 1, line, LineKind.FenceClose, block.Bash);
                }
                else
                {
                    yield return new ScannedLine(i + 1, line, LineKind.Block, block.Bash);
                }
            }
            else if (line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                yield return new ScannedLine(i + 1, line, LineKind.Heading, Bash: false);
            }
            else if (Opens(line) is { } opened)
            {
                open = opened;
                yield return new ScannedLine(i + 1, line, LineKind.FenceOpen, opened.Bash);
            }
            else
            {
                yield return new ScannedLine(i + 1, line, LineKind.Text, Bash: false);
            }
        }
    }

    /// <summary>
    /// The block <paramref name="line"/> opens outside every block, or <see langword="null"/> when it is
    /// no opening fence: it has no fence run (<see cref="Run"/>), or its run is of backticks and its info
    /// string holds a backtick.
    /// </summary>
    private static OpenFence? Opens(string line)
    {
        if (Run(line) is not { } run || (run.Marker == Backtick && run.After.Contains(Backtick, StringComparison.Ordinal)))
        {
            return null;
        }

        return new OpenFence(run.Marker, run.Length, Language(run.After) == BashLanguage);
    }

    /// <summary>
    /// Whether <paramref name="line"/> closes <paramref name="block"/>: its fence run is of the block's
    /// character, at least as long as the block's opening run, and only spaces or tabs follow it.
    /// </summary>
    private static bool Closes(OpenFence block, string line) =>
        Run(line) is { } run
            && run.Marker == block.Marker
            && run.Length >= block.Length
            && run.After.AsSpan().TrimEnd(FenceTrailingWhitespace).IsEmpty;

    /// <summary>
    /// The fence run that starts <paramref name="line"/>, or <see langword="null"/> when there is none: up
    /// to <see cref="MaxFenceIndent"/> spaces, then at least <see cref="MinFenceLength"/> backticks or
    /// tildes, all of one character. A tab, or a fourth space, before the run makes the line no fence.
    /// <see cref="FenceRun.After"/> is the rest of the line after the run.
    /// </summary>
    private static FenceRun? Run(string line)
    {
        var start = 0;
        while (start < line.Length && start <= MaxFenceIndent && line[start] == ' ')
        {
            start++;
        }

        if (start > MaxFenceIndent || start == line.Length || line[start] is not (Backtick or Tilde))
        {
            return null;
        }

        var marker = line[start];
        var end = start;
        while (end < line.Length && line[end] == marker)
        {
            end++;
        }

        return end - start < MinFenceLength ? null : new FenceRun(marker, end - start, line[end..]);
    }

    /// <summary>A heading line's text after <c>## </c>, trimmed.</summary>
    private static string HeadingText(string line) => line[HeadingPrefix.Length..].Trim();

    /// <summary>
    /// The first word of an opening fence's info string, the text after its run:
    /// <c>```bash docs-test=skip</c> and <c>~~~ bash</c> both give <c>bash</c>.
    /// </summary>
    private static string Language(string info)
    {
        var trimmed = info.Trim();
        var space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>
    /// The run of backticks or tildes that starts a line, after at most three spaces: its character, its
    /// length and the rest of the line after it.
    /// </summary>
    private readonly record struct FenceRun(char Marker, int Length, string After);

    /// <summary>
    /// A block a fence opened: the character and length of its opening run, which its closing fence must
    /// match, and whether its info string's first word is <c>bash</c>.
    /// </summary>
    private readonly record struct OpenFence(char Marker, int Length, bool Bash);

    /// <summary>What a line of a quickstart is to the fences around it (see <see cref="Scan"/>).</summary>
    private enum LineKind
    {
        /// <summary>A line outside every fenced block that is not a heading.</summary>
        Text,

        /// <summary>A <c>## </c> line outside every fenced block.</summary>
        Heading,

        /// <summary>The fence line that opens a block.</summary>
        FenceOpen,

        /// <summary>The fence line that closes a block.</summary>
        FenceClose,

        /// <summary>A line inside a block.</summary>
        Block,
    }

    /// <summary>
    /// One scanned line: its 1-based number, its text, what it is to the fences around it, and whether the
    /// block it opens, closes or lies in is a bash block.
    /// </summary>
    private readonly record struct ScannedLine(int Number, string Text, LineKind Kind, bool Bash);
}

/// <summary>
/// One command line of a quickstart bash block (<see cref="QuickstartSections.BashLines"/>): the 1-based
/// lines it spans (<see cref="First"/> to <see cref="Last"/>, equal unless it continues with <c>\</c>), its
/// text, continuations joined with one space, and the 1-based line of the fence that opens its block
/// (<see cref="Fence"/>), the same for every line of one block.
/// </summary>
public sealed record QuickstartBashLine(int First, int Last, string Text, int Fence);
