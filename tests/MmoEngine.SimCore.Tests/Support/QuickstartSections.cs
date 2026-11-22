namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// The <c>##</c> sections of specs/015-shared-simulation-core/quickstart.md whose commands
/// <c>Cli/QuickstartTests</c> checks, and the readers that give the document's headings and a section's
/// bash lines.
/// </summary>
/// <remarks>
/// <see cref="Headings"/> and <see cref="BashLines"/> take the document's text, not its path, so a test can
/// pass an in-memory copy with a deliberate defect as well as the committed file (opened read-only through
/// <see cref="RepoPaths"/>). Both read the document through one scan of its fenced blocks, so they agree on
/// which lines are headings.
/// </remarks>
public static class QuickstartSections
{
    private const string HeadingPrefix = "## ";

    private const string Fence = "```";

    private const string BashLanguage = "bash";

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
    /// The command lines of every <c>```bash</c> block between the line <c>## <paramref name="heading"/></c>
    /// and the next <c>##</c> heading of <paramref name="markdown"/>, in document order.
    /// </summary>
    /// <remarks>
    /// A line that ends in <c>\</c> is joined to the next with one space: the backslash, any whitespace
    /// before it and the next line's leading whitespace are dropped, and the entry spans both lines. Blank
    /// lines are skipped. Each entry carries the line of the fence that opens its block, so a caller can
    /// start each block in the repository root, as the quickstart says its commands run. A <c>## </c> line
    /// inside a fenced block is part of the block, not a heading (<see cref="Headings"/> reads the same
    /// scan). A heading the document lacks yields an empty list, so the caller's section check reports it
    /// rather than an exception.
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
    /// Every line of <paramref name="markdown"/>, in order, with what it is to the fences around it. Outside
    /// a block, a line that starts with <c>## </c> is a heading and one whose trimmed start is three
    /// backticks opens a block, <see cref="ScannedLine.Bash"/> when its info string's first word is
    /// <c>bash</c>. Inside a block, a line of only backticks (after its leading whitespace) closes it, and
    /// every other line, a fence line with an info string or a <c>## </c> line among them, is block content.
    /// </summary>
    private static IEnumerable<ScannedLine> Scan(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var inFence = false;
        var inBash = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!inFence && line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                yield return new ScannedLine(i + 1, line, LineKind.Heading, Bash: false);
            }
            else if (!inFence && trimmed.StartsWith(Fence, StringComparison.Ordinal))
            {
                inFence = true;
                inBash = Language(trimmed) == BashLanguage;
                yield return new ScannedLine(i + 1, line, LineKind.FenceOpen, inBash);
            }
            else if (inFence && trimmed.StartsWith(Fence, StringComparison.Ordinal) && trimmed.TrimEnd().Trim('`').Length == 0)
            {
                yield return new ScannedLine(i + 1, line, LineKind.FenceClose, inBash);
                inFence = false;
                inBash = false;
            }
            else
            {
                yield return new ScannedLine(i + 1, line, inFence ? LineKind.Block : LineKind.Text, inBash);
            }
        }
    }

    /// <summary>A heading line's text after <c>## </c>, trimmed.</summary>
    private static string HeadingText(string line) => line[HeadingPrefix.Length..].Trim();

    /// <summary>The first word of a fence line's info string: <c>```bash docs-test=skip</c> gives <c>bash</c>.</summary>
    private static string Language(string fence)
    {
        var info = fence.TrimStart('`').Trim();
        var space = info.IndexOfAny([' ', '\t']);
        return space < 0 ? info : info[..space];
    }

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
    /// block it opens, closes or lies in is a <c>```bash</c> block.
    /// </summary>
    private readonly record struct ScannedLine(int Number, string Text, LineKind Kind, bool Bash);
}

/// <summary>
/// One command line of a quickstart <c>```bash</c> block: the 1-based lines it spans (<see cref="First"/>
/// to <see cref="Last"/>, equal unless it continues with <c>\</c>), its text, continuations joined with
/// one space, and the 1-based line of the <c>```bash</c> fence that opens its block (<see cref="Fence"/>),
/// the same for every line of one block.
/// </summary>
public sealed record QuickstartBashLine(int First, int Last, string Text, int Fence);
