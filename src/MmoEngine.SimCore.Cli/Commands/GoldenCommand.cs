using System.CommandLine;
using System.Globalization;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server.Golden;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>golden write [--dir simcore/golden]</c> (FR-023,
/// specs/015-shared-simulation-core/contracts/simcore-cli.md#golden-write): the only code that writes the
/// golden <c>.bin</c> files. It reads every <c>&lt;case&gt;.json</c> under <c>input/</c>, <c>collision/</c> and
/// <c>stream/</c>, in that format order and in ordinal case-name order within each, with
/// <see cref="GoldenJson.Parse"/>; computes every <c>.bin</c> in memory (a valid case's from its encoder, a
/// refusal case's by <see cref="GoldenMutations.Apply"/> on its base's computed bytes); then writes, through
/// <see cref="AtomicFile"/>, only the files whose bytes differ from what is on disk. It prints one
/// <c>&lt;format&gt;/&lt;case&gt;: written|unchanged, &lt;n&gt; bytes</c> line per case and then
/// <c>golden write: &lt;cases&gt; cases, &lt;w&gt; written, &lt;u&gt; unchanged</c>, and exits 0.
/// </summary>
/// <remarks>
/// A case it cannot build refuses the whole run before anything is written: a missing <c>--dir</c> or format
/// folder, an unreadable file, invalid JSON or a malformed case (including a <c>format</c> that differs from
/// its folder), a refusal case whose <c>base</c> is not a valid case of the same format, a mutation that does
/// not fit, or an encoder that refuses a valid case. It prints
/// <c>golden write: refused, &lt;format&gt;/&lt;case&gt;.json: &lt;problem&gt;</c>, logs an Error event and
/// exits 2. A <c>.bin</c> that cannot be written is refused the same way, naming the <c>.bin</c>; the files
/// written before it keep their new bytes, each written whole. It does not check a refusal case's expected
/// error; the format tests do.
/// </remarks>
internal static class GoldenCommand
{
    /// <summary>The fixtures folder when <c>--dir</c> is not given.</summary>
    public const string DefaultDir = "simcore/golden";

    /// <summary>The format folders, in the order they are read and printed.</summary>
    private static readonly string[] Formats = ["input", "collision", "stream"];

    /// <summary>The <c>golden</c> command with its <c>write</c> subcommand.</summary>
    public static Command Create(TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(logger);
        var dir = PathOptions.RequireNonEmpty(new Option<string>("--dir")
        {
            Description = "The golden fixtures folder, holding input/, collision/ and stream/.",
            DefaultValueFactory = _ => DefaultDir,
        });
        var write = new Command("write", "Writes every golden fixture's .bin from its .json.") { dir };
        write.SetAction(parse => Write(parse.GetValue(dir)!, stdout, logger));
        return new Command("golden", "Golden format fixtures.") { write };
    }

    /// <summary>Writes the fixtures under <paramref name="dir"/> and returns the exit code.</summary>
    internal static int Write(string dir, TextWriter stdout, ILogger logger)
    {
        if (!Directory.Exists(dir))
        {
            return Refuse(stdout, logger, dir, "the folder does not exist");
        }

        var cases = new List<Built>();
        foreach (var format in Formats)
        {
            var folder = Path.Combine(dir, format);
            if (!Directory.Exists(folder))
            {
                return Refuse(stdout, logger, format + "/", "the folder does not exist");
            }

            var built = BuildFormat(format, folder, out var refusal);
            if (built is null)
            {
                return Refuse(stdout, logger, refusal!.Value.Label, refusal.Value.Problem);
            }

            cases.AddRange(built);
        }

        var written = 0;
        foreach (var item in cases)
        {
            var label = $"{item.Format}/{item.Case}";
            bool changed;
            try
            {
                changed = !File.Exists(item.BinPath) || !ReadFile(item.BinPath).AsSpan().SequenceEqual(item.Bin);
                if (changed)
                {
                    AtomicFile.WriteAllBytes(item.BinPath, item.Bin);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refuse(stdout, logger, label + ".bin", $"the file cannot be written ({ex.Message})");
            }

            written += changed ? 1 : 0;
            stdout.Write(string.Create(CultureInfo.InvariantCulture, $"{label}: {(changed ? "written" : "unchanged")}, {item.Bin.Length} bytes\n"));
        }

        var unchanged = cases.Count - written;
        stdout.Write(string.Create(CultureInfo.InvariantCulture, $"golden write: {cases.Count} cases, {written} written, {unchanged} unchanged\n"));
        logger.Information(
            "Wrote the golden fixtures in {Dir}: {Cases} cases, {Written} written, {Unchanged} unchanged",
            dir,
            cases.Count,
            written,
            unchanged);
        return CliExitCodes.Success;
    }

    /// <summary>
    /// Reads and builds every case of one format folder: parses each <c>.json</c> in ordinal case-name order,
    /// encodes the valid cases, then applies each refusal case's mutations to its base's computed bytes.
    /// Returns the cases in ordinal order, or null with <paramref name="refusal"/> naming the case that could
    /// not be built.
    /// </summary>
    private static List<Built>? BuildFormat(string format, string folder, out (string Label, string Problem)? refusal)
    {
        refusal = null;
        var names = Directory.GetFiles(folder)
            .Where(path => string.Equals(Path.GetExtension(path), ".json", StringComparison.Ordinal))
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Order(StringComparer.Ordinal)
            .ToArray();

        var parsed = new List<GoldenCase>(names.Length);
        foreach (var name in names)
        {
            var fileName = name + ".json";
            try
            {
                parsed.Add(GoldenJson.Parse(format, fileName, ReadFile(Path.Combine(folder, fileName))));
            }
            catch (GoldenCaseException ex)
            {
                refusal = ($"{format}/{fileName}", ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                refusal = ($"{format}/{fileName}", $"the file cannot be read ({ex.Message})");
                return null;
            }
        }

        var validBins = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var goldenCase in parsed)
        {
            if (goldenCase is GoldenRefusalCase)
            {
                continue;
            }

            try
            {
                validBins.Add(goldenCase.Case, EncodeValid(goldenCase));
            }
            catch (CoreFormatException ex)
            {
                refusal = ($"{format}/{goldenCase.Case}.json", ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is ConfigValidationException or ArgumentException or InvalidOperationException)
            {
                refusal = ($"{format}/{goldenCase.Case}.json", $"the encoder refuses the case ({ex.Message})");
                return null;
            }
        }

        var built = new List<Built>(parsed.Count);
        foreach (var goldenCase in parsed)
        {
            byte[] bin;
            if (goldenCase is GoldenRefusalCase refusalCase)
            {
                if (!validBins.TryGetValue(refusalCase.Base, out var baseBin))
                {
                    var why = parsed.Any(other => other.Case == refusalCase.Base)
                        ? $"base \"{refusalCase.Base}\" is a refusal case, not a valid case"
                        : $"base \"{refusalCase.Base}\" is not a case of {format}/";
                    refusal = ($"{format}/{refusalCase.Case}.json", why);
                    return null;
                }

                try
                {
                    bin = GoldenMutations.Apply(format, baseBin, refusalCase.Mutations);
                }
                catch (GoldenCaseException ex)
                {
                    refusal = ($"{format}/{refusalCase.Case}.json", ex.Message);
                    return null;
                }
            }
            else
            {
                bin = validBins[goldenCase.Case];
            }

            built.Add(new Built(format, goldenCase.Case, Path.Combine(folder, goldenCase.Case + ".bin"), bin));
        }

        return built;
    }

    /// <summary>A valid case's <c>.bin</c>, from its format's encoder.</summary>
    private static byte[] EncodeValid(GoldenCase goldenCase) => goldenCase switch
    {
        InputValidCase input => input.EncodeBin(),
        CollisionValidCase collision => collision.EncodeBin(),
        StreamValidCase stream => stream.EncodeBin(),
        _ => throw new InvalidOperationException($"{goldenCase.Format}/{goldenCase.Case} is not a valid case of a known shape."),
    };

    /// <summary>A file's bytes, read through a read-only stream.</summary>
    private static byte[] ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Prints <c>golden write: refused, &lt;label&gt;: &lt;problem&gt;</c>, logs it as an Error event, and returns 2.</summary>
    private static int Refuse(TextWriter stdout, ILogger logger, string label, string problem)
    {
        stdout.Write($"golden write: refused, {label}: {problem}\n");
        logger.Error("golden write refused {Case}: {Problem}", label, problem);
        return CliExitCodes.Usage;
    }

    /// <summary>One case's computed <c>.bin</c> and where it goes.</summary>
    private sealed record Built(string Format, string Case, string BinPath, byte[] Bin);
}
