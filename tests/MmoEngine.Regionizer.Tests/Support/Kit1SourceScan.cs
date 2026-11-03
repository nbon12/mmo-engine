using System.Text;
using System.Text.RegularExpressions;

namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>Finds the repository and scans C# source text for banned API uses, ignoring comments.</summary>
internal static class Kit1SourceScan
{
    /// <summary>The repository root: the nearest directory above the test binaries that holds global.json.</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No directory holding global.json above {AppContext.BaseDirectory}.");
    }

    /// <summary>The .cs files under <paramref name="directory"/>, excluding build output, sorted.</summary>
    public static string[] SourceFiles(string directory)
    {
        var separator = Path.DirectorySeparatorChar;
        return [.. Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}bin{separator}", StringComparison.Ordinal)
                && !f.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>The 1-based line numbers of <paramref name="source"/> where <paramref name="banned"/> matches outside comments.</summary>
    public static int[] Hits(string source, Regex banned)
    {
        var code = StripComments(source);
        var lines = code.Split('\n');
        var hits = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (banned.IsMatch(lines[i]))
            {
                hits.Add(i + 1);
            }
        }

        return [.. hits];
    }

    /// <summary>
    /// Replaces // and /* */ comments with spaces, keeping line breaks so line numbers survive. String
    /// and character literals are kept as they are, so a "//" inside one does not start a comment, and
    /// a banned name inside a string (or an interpolation hole) is still reported: the cautious side.
    /// </summary>
    public static string StripComments(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    output.Append(' ');
                    i++;
                }
            }
            else if (c == '/' && next == '*')
            {
                output.Append("  ");
                i += 2;
                while (i < source.Length && !(source[i] == '*' && i + 1 < source.Length && source[i + 1] == '/'))
                {
                    output.Append(source[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                output.Append("  ");
                i += 2;
            }
            else if (c == '"' || c == '\'')
            {
                var verbatim = i > 0 && source[i - 1] == '@';
                var raw = c == '"' && next == '"' && i + 2 < source.Length && source[i + 2] == '"';
                i = CopyLiteral(source, i, output, verbatim, raw);
            }
            else
            {
                output.Append(c);
                i++;
            }
        }

        return output.ToString();
    }

    private static int CopyLiteral(string source, int start, StringBuilder output, bool verbatim, bool raw)
    {
        var quote = source[start];
        var i = start;
        if (raw)
        {
            var run = 0;
            while (i < source.Length && source[i] == '"')
            {
                run++;
                i++;
            }

            output.Append('"', run);
            var closing = new string('"', run);
            var end = source.IndexOf(closing, i, StringComparison.Ordinal);
            end = end < 0 ? source.Length : end + run;
            output.Append(source, i, end - i);
            return end;
        }

        output.Append(quote);
        i++;
        while (i < source.Length)
        {
            var c = source[i];
            output.Append(c);
            i++;
            if (!verbatim && c == '\\' && i < source.Length)
            {
                output.Append(source[i]);
                i++;
            }
            else if (c == quote)
            {
                if (verbatim && i < source.Length && source[i] == quote)
                {
                    output.Append(source[i]);
                    i++;
                }
                else
                {
                    break;
                }
            }
            else if (c == '\n' && !verbatim)
            {
                break;
            }
        }

        return i;
    }
}
