using System.CommandLine;
using MmoEngine.SimCore.Cli.Measure;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>measure validate-report &lt;file&gt;</c> (FR-031, SC-007,
/// specs/015-shared-simulation-core/contracts/simcore-cli.md#measure-validate-report): reads the measurement
/// report's bytes read-only, checks them with <see cref="MeasurementReportValidator.ValidateUtf8"/>, and prints
/// one <c>path: problem</c> line per problem on stdout, in document order, and nothing when the report is
/// valid. It exits 0 when valid and 1 when invalid, malformed JSON and bytes that are not UTF-8 included
/// (<c>$: is not valid JSON (…)</c>). A file that is missing or cannot be read, or that holds more than
/// <see cref="MaxFileLength"/> bytes, exits 2 with nothing on stdout and a logged event naming the path; an
/// empty or whitespace-only path is refused when the command line is parsed (<see cref="PathOptions"/>),
/// also exit 2. The command writes no file.
/// </summary>
internal static class MeasureCommand
{
    /// <summary>
    /// The longest report the command reads, 1 MiB, as <c>config check</c>'s limit. A real report is about
    /// 10 KB, so the limit only refuses a file that is not one, such as a device that never ends.
    /// </summary>
    internal const int MaxFileLength = 1024 * 1024;

    /// <summary>The <c>measure</c> command with its <c>validate-report</c> subcommand.</summary>
    public static Command Create(TextWriter stdout, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(logger);
        var file = PathOptions.RequireNonEmpty(new Argument<string>("file") { Description = "The measurement report to check, for example $RUNNER_TEMP/simcore-measurement.json." });
        var validateReport = new Command("validate-report", "Checks a measurement report against contracts/measurement-report.md.") { file };
        validateReport.SetAction(parse => ValidateReport(parse.GetValue(file)!, stdout, logger));
        return new Command("measure", "Measurement reports.") { validateReport };
    }

    /// <summary>Validates the report at <paramref name="path"/> and returns the exit code.</summary>
    internal static int ValidateReport(string path, TextWriter stdout, ILogger logger)
    {
        byte[]? bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            bytes = ReadAtMost(stream, MaxFileLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("measure validate-report cannot read {Path}: {Reason}", path, ex.Message);
            return CliExitCodes.Usage;
        }

        if (bytes is null)
        {
            logger.Error("measure validate-report refused {Path}: it holds more than {Limit} bytes, the limit for a measurement report", path, MaxFileLength);
            return CliExitCodes.Usage;
        }

        // The bytes go to the validator undecoded: it decodes them strictly, so bytes that are not UTF-8
        // are reported as invalid JSON instead of being replaced by U+FFFD and passing.
        var problems = MeasurementReportValidator.ValidateUtf8(bytes);
        foreach (var problem in problems)
        {
            stdout.Write($"{problem}\n");
        }

        logger.Information("Validated measurement report {Path}: {ProblemCount} problems", path, problems.Count);
        return problems.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failed;
    }

    /// <summary>
    /// Every byte of <paramref name="stream"/> when it holds at most <paramref name="limit"/>, else null. A
    /// stream with a length over the limit is refused from it, before any byte is read; one without a length
    /// (a pipe, or a device such as <c>/dev/zero</c>) is read to its end or until it passes the limit, so no
    /// more than <paramref name="limit"/> + 1 bytes are ever read.
    /// </summary>
    internal static byte[]? ReadAtMost(Stream stream, int limit)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        ArgumentOutOfRangeException.ThrowIfEqual(limit, int.MaxValue);
        if (stream.CanSeek && stream.Length - stream.Position > limit)
        {
            return null;
        }

        var buffer = new byte[limit + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0)
            {
                break;
            }

            count += read;
        }

        return count > limit ? null : buffer[..count];
    }
}
