namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// The <c>##</c> sections of specs/015-shared-simulation-core/quickstart.md whose commands
/// <c>Cli/QuickstartTests</c> checks, and the reader that gives a section's bash lines.
/// </summary>
/// <remarks>
/// <see cref="BashLines"/> takes the document's text, not its path, so a test can pass an in-memory copy
/// with a deliberate defect as well as the committed file (opened read-only through <see cref="RepoPaths"/>).
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
    /// The command lines of every <c>```bash</c> block between the line <c>## <paramref name="heading"/></c>
    /// and the next <c>##</c> heading of <paramref name="markdown"/>, in document order.
    /// </summary>
    /// <remarks>
    /// A line that ends in <c>\</c> is joined to the next with one space: the backslash, any whitespace
    /// before it and the next line's leading whitespace are dropped, and the entry spans both lines. Blank
    /// lines are skipped. Each entry carries the line of the fence that opens its block, so a caller can
    /// start each block in the repository root, as the quickstart says its commands run. A <c>## </c> line
    /// inside a fenced block is part of the block, not a heading. A heading the document lacks yields an
    /// empty list, so the caller's section check reports it rather than an exception.
    /// </remarks>
    public static IReadOnlyList<QuickstartBashLine> BashLines(string markdown, string heading)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(heading);

        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var result = new List<QuickstartBashLine>();
        var inSection = false;
        var inFence = false;
        var inBash = false;
        var fence = 0;
        var pending = (First: 0, Text: string.Empty);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (!inFence && line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            {
                if (inSection)
                {
                    break;
                }

                inSection = line[HeadingPrefix.Length..].Trim() == heading;
                continue;
            }

            if (trimmed.StartsWith(Fence, StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    inFence = true;
                    inBash = Language(trimmed) == BashLanguage;
                    fence = i + 1;
                    continue;
                }

                if (trimmed.TrimEnd().Trim('`').Length == 0)
                {
                    if (inSection && inBash && pending.First > 0)
                    {
                        result.Add(new QuickstartBashLine(pending.First, i, pending.Text, fence));
                        pending = (0, string.Empty);
                    }

                    inFence = false;
                    inBash = false;
                    continue;
                }
            }

            if (!inSection || !inBash)
            {
                continue;
            }

            if (pending.First == 0 && line.Trim().Length == 0)
            {
                continue;
            }

            var continues = line.EndsWith('\\');
            var text = continues ? line[..^1].TrimEnd() : line;
            pending = pending.First == 0
                ? (i + 1, text)
                : (pending.First, text.TrimStart().Length == 0 ? pending.Text : $"{pending.Text} {text.TrimStart()}");
            if (!continues)
            {
                result.Add(new QuickstartBashLine(pending.First, i + 1, pending.Text, fence));
                pending = (0, string.Empty);
            }
        }

        return result;
    }

    /// <summary>The first word of a fence line's info string: <c>```bash docs-test=skip</c> gives <c>bash</c>.</summary>
    private static string Language(string fence)
    {
        var info = fence.TrimStart('`').Trim();
        var space = info.IndexOfAny([' ', '\t']);
        return space < 0 ? info : info[..space];
    }
}

/// <summary>
/// One command line of a quickstart <c>```bash</c> block: the 1-based lines it spans (<see cref="First"/>
/// to <see cref="Last"/>, equal unless it continues with <c>\</c>), its text, continuations joined with
/// one space, and the 1-based line of the <c>```bash</c> fence that opens its block (<see cref="Fence"/>),
/// the same for every line of one block.
/// </summary>
public sealed record QuickstartBashLine(int First, int Last, string Text, int Fence);
