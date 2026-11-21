using System.CommandLine;
using System.Text;
using MmoEngine.SimCore.Cli.Measure;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>measure validate-report &lt;file&gt;</c> (FR-031, SC-007,
/// specs/015-shared-simulation-core/contracts/simcore-cli.md#measure-validate-report): reads the measurement
/// report read-only, checks it with <see cref="MeasurementReportValidator.Validate"/>, and prints one
/// <c>path: problem</c> line per problem on stdout, in document order, and nothing when the report is valid.
/// It exits 0 when valid and 1 when invalid, malformed JSON included (<c>$: is not valid JSON (…)</c>). A
/// file that is missing or cannot be read exits 2 with nothing on stdout and a logged event naming the path;
/// an empty or whitespace-only path is refused when the command line is parsed (<see cref="PathOptions"/>),
/// also exit 2. The command writes no file.
/// </summary>
internal static class MeasureCommand
{
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
        string json;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error("measure validate-report cannot read {Path}: {Reason}", path, ex.Message);
            return CliExitCodes.Usage;
        }

        var problems = MeasurementReportValidator.Validate(json);
        foreach (var problem in problems)
        {
            stdout.Write($"{problem}\n");
        }

        logger.Information("Validated measurement report {Path}: {ProblemCount} problems", path, problems.Count);
        return problems.Count == 0 ? CliExitCodes.Success : CliExitCodes.Failed;
    }
}
