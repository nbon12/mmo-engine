using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Cli.Agreement;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>agreement prepare</c> and <c>agreement compare</c> (FR-026 server half, US2 scenarios 4 and 5;
/// specs/015-shared-simulation-core/contracts/simcore-cli.md#agreement-prepare, #agreement-compare,
/// #agreement-work-directory and #manifestjson). <c>prepare</c> runs every committed stream through the
/// server build and checks its expected hashes, generates the <c>seed-&lt;nnn&gt;</c> streams, and writes the
/// work directory every build runs from, with <c>manifest.json</c> last. <c>compare</c> compares every build
/// folder with the server's records and table results, prints one line per known build and each diverged
/// build's first report, and writes <c>report/</c>. An empty or whitespace-only path option of either command
/// is refused when the command line is parsed (<see cref="PathOptions"/>), exit 2.
/// </summary>
internal static class AgreementCommand
{
    /// <summary>The default <c>--seeds</c>, the 50 seeds CI generates (FR-026, SC-001).</summary>
    public const string DefaultSeeds = "1-50";

    /// <summary>The largest seed <c>--seeds</c> accepts.</summary>
    public const int MaxSeed = 999;

    /// <summary>The perturbation's stream, whose copies are also the refusal fixtures.</summary>
    public const string PerturbationStream = "flat-run-30hz";

    /// <summary>The field a one-ulp perturbation changes (SC-002).</summary>
    public const string PerturbationField = "characters[0].position.x";

    /// <summary>The collision file the generated streams run on, in <c>--content</c>.</summary>
    public const string TestWorldFile = "test-world.mmcd";

    private const string PrepareName = "agreement prepare";

    private const string CompareName = "agreement compare";

    /// <summary>The <c>agreement</c> command with its <c>prepare</c> and <c>compare</c> subcommands.</summary>
    public static Command Create(TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(logger);

        var prepareWork = PathOptions.RequireNonEmpty(new Option<string>("--work") { Description = "The work directory to write: it must not exist or must be empty.", Required = true });
        var seeds = new Option<string>("--seeds") { Description = "The generated seeds: <first>-<last> or one number, 1 to 999.", DefaultValueFactory = _ => DefaultSeeds };
        var streams = PathOptions.RequireNonEmpty(new Option<string>("--streams") { Description = "The committed streams folder.", DefaultValueFactory = _ => StreamsCommand.DefaultStreams });
        var content = PathOptions.RequireNonEmpty(new Option<string>("--content") { Description = "The folder of collision files.", DefaultValueFactory = _ => StreamsCommand.DefaultContent });
        var config = PathOptions.RequireNonEmpty(new Option<string>("--config") { Description = "The configuration of the generated streams and the tables.", DefaultValueFactory = _ => StreamsCommand.DefaultConfig });
        var prepare = new Command("prepare", "Writes the agreement work directory and the server build's records.")
        {
            prepareWork,
            seeds,
            streams,
            content,
            config,
        };
        prepare.SetAction(parse => Prepare(
            new PrepareOptions(parse.GetValue(prepareWork)!, parse.GetValue(seeds)!, parse.GetValue(streams)!, parse.GetValue(content)!, parse.GetValue(config)!),
            stdout,
            stderr,
            logger));

        var compareWork = PathOptions.RequireNonEmpty(new Option<string>("--work") { Description = "The prepared work directory.", Required = true });
        var require = new Option<string>("--require") { Description = "The builds that must have run, comma-separated.", DefaultValueFactory = _ => BuildNames.DefaultRequire };
        var summary = PathOptions.RequireNonEmpty(new Option<string>("--summary") { Description = "A Markdown file to append the summary section to." });
        var compare = new Command("compare", "Compares every build's records with the server's.")
        {
            compareWork,
            require,
            summary,
        };
        compare.SetAction(parse => Compare(parse.GetValue(compareWork)!, parse.GetValue(require)!, parse.GetValue(summary), stdout, logger));

        return new Command("agreement", "The agreement suite's work directory and comparison.") { prepare, compare };
    }

    /// <summary>
    /// <c>agreement prepare</c> (contracts/simcore-cli.md#agreement-prepare). It refuses, before anything is
    /// written and with exit 2: a <c>--work</c> that is a file or a non-empty directory, a bad
    /// <c>--seeds</c>, a missing <c>--streams</c> folder, a refused <c>--content</c>, and an invalid
    /// <c>--config</c>. It then runs every committed stream as <c>streams verify</c> does (a divergence prints
    /// its report and exits 1; a refusal or NotComparable prints its line and exits 2), generates and runs
    /// the seeds, and only then writes, in the contract's order: the streams, the collision files, the server
    /// records, the batch inputs, the refusals, the tables and, last, <c>manifest.json</c>.
    /// </summary>
    internal static int Prepare(PrepareOptions options, TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        if (WorkProblem(options.Work) is { } workProblem)
        {
            stderr.Write($"{PrepareName}: --work {options.Work}: {workProblem}\n");
            return CliExitCodes.Usage;
        }

        if (!TryParseSeeds(options.Seeds, out var firstSeed, out var lastSeed))
        {
            stderr.Write($"{PrepareName}: --seeds {options.Seeds}: give <first>-<last> or one number, with 1 <= first <= last <= {MaxSeed}\n");
            return CliExitCodes.Usage;
        }

        if (!Directory.Exists(options.Streams))
        {
            stderr.Write($"{PrepareName}: --streams {options.Streams}: the directory does not exist\n");
            return CliExitCodes.Usage;
        }

        if (!ContentFiles.TryLoadAll(options.Content, PrepareName, stderr, out var contentNames, out var worlds, out var contentFiles))
        {
            return CliExitCodes.Usage;
        }

        var load = SimConfigJson.Load(options.Config);
        var configErrors = load.Config is null ? load.Errors : SimConfigValidator.Errors(load.Config);
        if (configErrors.Count > 0)
        {
            stderr.Write($"{PrepareName}: the configuration {options.Config} is invalid\n");
            foreach (var error in configErrors)
            {
                stderr.Write($"{error.Path}: {error.Message}\n");
            }

            return CliExitCodes.Usage;
        }

        try
        {
            return Prepare(options, firstSeed, lastSeed, load.Config!, contentNames, worlds, contentFiles, stdout, stderr, logger);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.Write($"{PrepareName}: --work {options.Work}: the work directory cannot be written ({ex.Message})\n");
            return CliExitCodes.Usage;
        }
    }

    /// <summary>
    /// <c>agreement compare</c> (contracts/simcore-cli.md#agreement-compare): refuses an unknown
    /// <c>--require</c> build, a missing or unreadable manifest, and a manifest from another rules revision or
    /// format version (NotComparable), all with exit 2; compares every known build; prints one line per build,
    /// each diverged build's first report and the outcome; writes <c>report/</c> and the summary. Exit 2 when
    /// a required build did not run, else 1 when any build diverged, else 0.
    /// </summary>
    internal static int Compare(string work, string require, string? summary, TextWriter stdout, ILogger logger)
    {
        if (!BuildNames.TryParseRequire(require, out var required, out var unknown))
        {
            return CannotRun(stdout, $"--require names the unknown build {unknown}; the known builds are {string.Join(", ", BuildNames.Known)}");
        }

        var directory = new WorkDirectory(work);
        AgreementResult result;
        try
        {
            var manifestPath = directory.FullPath(WorkDirectory.ManifestFile);
            if (!File.Exists(manifestPath))
            {
                return CannotRun(stdout, $"{WorkDirectory.ManifestFile} does not exist in {work}");
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var differences = AgreementManifest.ReadVersions(document.RootElement).Differences();
            if (differences.Count > 0)
            {
                foreach (var difference in differences)
                {
                    stdout.Write($"NOT COMPARABLE work {work}: {difference}\n");
                }

                stdout.Write("agreement: not comparable\n");
                return CliExitCodes.Usage;
            }

            result = AgreementComparison.Compare(directory, AgreementManifest.Read(document.RootElement), required);
        }
        catch (JsonException ex)
        {
            return CannotRun(stdout, $"{WorkDirectory.ManifestFile} is not valid JSON ({ex.Message})");
        }
        catch (InvalidDataException ex)
        {
            return CannotRun(stdout, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return CannotRun(stdout, ex.Message);
        }

        foreach (var build in result.Builds)
        {
            stdout.Write(BuildLine(build, result));
            stdout.Write('\n');
        }

        foreach (var build in result.Builds.Where(build => build.Status == BuildStatus.Diverged))
        {
            stdout.Write(AgreementReport.FirstReport(build));
            stdout.Write('\n');
        }

        var notRun = result.Builds.Where(build => build.Required && build.Status == BuildStatus.NotRun).Select(build => build.Build).ToArray();
        var diverged = result.Builds.Count(build => build.Status == BuildStatus.Diverged);
        var (outcome, outcomeLine, exit) = notRun.Length > 0
            ? ("CannotRun", $"agreement: cannot run: required build{(notRun.Length == 1 ? string.Empty : "s")} {string.Join(", ", notRun)} did not run", CliExitCodes.Usage)
            : diverged > 0
                ? ("Diverged", "agreement: diverged", CliExitCodes.Failed)
                : ("Match", "agreement: match", CliExitCodes.Success);
        try
        {
            AgreementReport.Write(directory, result, required, outcome);
            if (summary is not null)
            {
                AgreementReport.AppendSummary(summary, AgreementReport.Summary(result, outcomeLine));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return CannotRun(stdout, $"the report cannot be written ({ex.Message})");
        }

        stdout.Write(outcomeLine);
        stdout.Write('\n');
        logger.Information(
            "Compared {Streams} streams in {Work}: {Outcome}, {Diverged} builds diverged, {NotRun} required builds did not run",
            result.Streams,
            work,
            outcome,
            diverged,
            notRun.Length);
        return exit;
    }

    /// <summary>
    /// Steps 1 to 8 of contracts/simcore-cli.md#agreement-prepare, once the arguments, the content and the
    /// configuration are accepted. Nothing is written until every stream has run.
    /// </summary>
    private static int Prepare(
        PrepareOptions options,
        int firstSeed,
        int lastSeed,
        SimConfig config,
        IReadOnlyList<string> contentNames,
        IReadOnlyList<CollisionWorld> worlds,
        IReadOnlyList<byte[]> contentFiles,
        TextWriter stdout,
        TextWriter stderr,
        ILogger logger)
    {
        // 1: every committed stream, run and checked as streams verify does.
        var committed = new List<StreamsCommand.VerifiedStream>();
        var (diverged, refused) = (0, 0);
        foreach (var path in StreamsCommand.StreamPaths(options.Streams))
        {
            var verified = StreamsCommand.VerifyStream(Path.GetFileNameWithoutExtension(path), path, worlds);
            if (verified.Outcome == StreamsCommand.VerifyOutcome.Match)
            {
                committed.Add(verified);
                continue;
            }

            stdout.Write(verified.Text);
            stdout.Write('\n');
            diverged += verified.Outcome == StreamsCommand.VerifyOutcome.Diverged ? 1 : 0;
            refused += verified.Outcome == StreamsCommand.VerifyOutcome.Diverged ? 0 : 1;
        }

        if (diverged > 0 || refused > 0)
        {
            return diverged > 0 ? CliExitCodes.Failed : CliExitCodes.Usage;
        }

        var perturbation = committed.Find(stream => stream.Name == PerturbationStream);
        var testWorld = IndexOf(contentNames, TestWorldFile);
        if (perturbation is null || testWorld < 0)
        {
            stderr.Write(perturbation is null
                ? $"{PrepareName}: --streams {options.Streams}: {PerturbationStream} is missing; the perturbation and the refusals are built from it\n"
                : $"{PrepareName}: --content {options.Content}: {TestWorldFile} is missing; the generated streams run on it\n");
            return CliExitCodes.Usage;
        }

        // 2: the generated streams, run through the server, with their expected hashes filled.
        var generated = new List<(int Seed, RecordedStream Stream, byte[] Records)>();
        for (var seed = firstSeed; seed <= lastSeed; seed++)
        {
            var name = StreamGenerator.NameFor(seed);
            try
            {
                var built = StreamGenerator.Build(seed, config, worlds[testWorld]);
                var records = StreamRunner.Run(built.Encode(), worlds);
                generated.Add((seed, ScenarioDefinition.WithExpectedHashes(built, records), records));
            }
            catch (Exception ex) when (ex is StepFaultException or ConfigValidationException or CoreFormatException)
            {
                stderr.Write($"{PrepareName}: {name}: {ex.Message}\n");
                return CliExitCodes.Usage;
            }
        }

        // Every stream has run; now write, steps 1 to 8, the manifest last.
        var work = new WorkDirectory(options.Work);
        var entries = new List<ManifestStream>();
        foreach (var stream in committed)
        {
            work.Write(WorkDirectory.StreamFile(stream.Name), stream.Bytes!);
            entries.Add(Entry(stream.Name, AgreementManifest.Committed, stream.Stream!.Seed, stream.Stream, stream.Records!));
        }

        foreach (var (seed, stream, records) in generated)
        {
            var name = StreamGenerator.NameFor(seed);
            work.WriteStream(name, stream);
            entries.Add(Entry(name, AgreementManifest.Generated, (ulong)seed, stream, records));
        }

        // 3: every collision file a stream names, and the refusal's small one.
        var smallCollision = RefusalFixtures.SmallCollisionFile();
        var smallChecksum = CollisionFile.Decode(smallCollision).Checksum;
        foreach (var checksum in entries.Select(entry => entry.CollisionChecksum).Distinct())
        {
            // The first world with the checksum is the one StreamRunner ran the stream over.
            work.Write(WorkDirectory.CollisionPath(checksum), contentFiles[worlds.Select(world => world.Checksum).ToList().IndexOf(checksum)]);
        }

        work.Write(WorkDirectory.CollisionPath(smallChecksum), smallCollision);

        // 4: the server build's records.
        for (var i = 0; i < entries.Count; i++)
        {
            work.Write(entries[i].ServerRecords, i < committed.Count ? committed[i].Records! : generated[i - committed.Count].Records);
        }

        // 5: the batch inputs of every batchable stream.
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Batch is { } batch)
            {
                var stream = i < committed.Count ? committed[i].Stream! : generated[i - committed.Count].Stream;
                work.Write(batch.Config, ConfigBlock.Encode(stream.Config));
                work.Write(batch.InitialState, stream.InitialState.ToArray());
                work.Write(batch.Commands, Commands(stream));
            }
        }

        // 6: the refusal fixtures.
        var flatRun = perturbation.Bytes!;
        var flatRunCollision = WorkDirectory.CollisionPath(perturbation.Stream!.CollisionChecksum);
        work.Write(WorkDirectory.RefusalFile(RefusalFixtures.UnsupportedVersionName), RefusalFixtures.UnsupportedVersion(flatRun));
        work.Write(WorkDirectory.RefusalFile(RefusalFixtures.RulesRevisionName), RefusalFixtures.RulesRevision(flatRun));
        ManifestRefusal[] refusals =
        [
            new(RefusalFixtures.UnsupportedVersionName, WorkDirectory.RefusalFile(RefusalFixtures.UnsupportedVersionName), flatRunCollision, RefusalFixtures.UnsupportedVersionExpected),
            new(RefusalFixtures.RulesRevisionName, WorkDirectory.RefusalFile(RefusalFixtures.RulesRevisionName), flatRunCollision, RefusalFixtures.RulesRevisionExpected),
            new(RefusalFixtures.CollisionMissingName, WorkDirectory.StreamFile(PerturbationStream), WorkDirectory.CollisionPath(smallChecksum), RefusalFixtures.CollisionMissingExpected),
        ];

        // 7: the cross-build tables and the server build's results.
        var inputs = CrossBuildTables.BuildInputs();
        var block = ConfigBlock.Encode(config);
        work.Write(WorkDirectory.TablesInputs, inputs);
        work.Write(WorkDirectory.TablesConfig, block);
        work.Write(WorkDirectory.ServerTables, CrossBuildTables.Evaluate(inputs, block));

        // 8: the manifest, last.
        var steps = perturbation.Stream.Steps;
        var manifest = new AgreementManifest(
            AgreementManifest.CurrentFormatVersion,
            SimCoreVersion.RulesRevision,
            ManifestFormats.Current,
            firstSeed,
            lastSeed,
            entries,
            new ManifestPerturbation(PerturbationStream, steps[(steps.Count - 1) / 2].Step, PerturbationField),
            new ManifestTables(WorkDirectory.TablesInputs, WorkDirectory.TablesConfig, WorkDirectory.ServerTables),
            refusals);
        work.Write(WorkDirectory.ManifestFile, manifest.ToJson());

        var stepTotal = entries.Sum(entry => (long)entry.Steps);
        stdout.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"prepared {options.Work}: {entries.Count} streams ({committed.Count} named, {generated.Count} generated), {stepTotal} steps, rules revision {SimCoreVersion.RulesRevision}\n"));
        logger.Information(
            "Prepared {Work}: {Streams} streams ({Named} named, {Generated} generated), {Steps} steps",
            options.Work,
            entries.Count,
            committed.Count,
            generated.Count,
            stepTotal);
        return CliExitCodes.Success;
    }

    /// <summary>A stream's manifest entry; <c>batch</c> is set when every step carries one command, for character index 0.</summary>
    private static ManifestStream Entry(string name, string origin, ulong seed, RecordedStream stream, byte[] records)
    {
        var batchable = stream.Steps.All(step => step.Commands.Length == 1 && step.Commands[0].CharacterIndex == 0);
        return new ManifestStream(
            name,
            origin,
            seed,
            WorkDirectory.StreamFile(name),
            WorkDirectory.CollisionPath(stream.CollisionChecksum),
            stream.CollisionChecksum,
            stream.StepHz,
            stream.Steps.Count,
            stream.CharacterCount,
            WorkDirectory.RecordsFile(BuildNames.Server, name),
            FinalHash(records),
            batchable
                ? new ManifestBatch(
                    WorkDirectory.BatchFile(name, WorkDirectory.BatchConfigFile),
                    WorkDirectory.BatchFile(name, WorkDirectory.BatchInitialStateFile),
                    WorkDirectory.BatchFile(name, WorkDirectory.BatchCommandsFile),
                    stream.MatchSalt)
                : null);
    }

    /// <summary>A batchable stream's commands, 17 bytes a step.</summary>
    private static byte[] Commands(RecordedStream stream)
    {
        var commands = new byte[stream.Steps.Count * InputCodec.EncodedSize];
        for (var s = 0; s < stream.Steps.Count; s++)
        {
            // TryEncode fails only for a destination shorter than 17 bytes, and every slice is 17 bytes.
            var command = stream.Steps[s].Commands[0].Command;
            _ = InputCodec.TryEncode(in command, commands.AsSpan(s * InputCodec.EncodedSize, InputCodec.EncodedSize));
        }

        return commands;
    }

    /// <summary>The hash of the last of the packed step records.</summary>
    private static ulong FinalHash(byte[] records)
    {
        var (offset, hash) = (0, 0UL);
        while (StepRecord.TryRead(records.AsSpan(offset), out _, out _, out var last, out var length))
        {
            (offset, hash) = (offset + length, last);
        }

        return hash;
    }

    /// <summary>Why <c>--work</c> is refused, or null when it does not exist or is an empty directory.</summary>
    private static string? WorkProblem(string work)
    {
        if (File.Exists(work))
        {
            return "the path is a file, not a directory";
        }

        return Directory.Exists(work) && Directory.EnumerateFileSystemEntries(work).Any() ? "the directory is not empty" : null;
    }

    /// <summary><c>&lt;first&gt;-&lt;last&gt;</c> or one number, in decimal digits, with 1 &lt;= first &lt;= last &lt;= 999.</summary>
    private static bool TryParseSeeds(string seeds, out int first, out int last)
    {
        var dash = seeds.IndexOf('-', StringComparison.Ordinal);
        var firstText = dash < 0 ? seeds : seeds[..dash];
        var lastText = dash < 0 ? seeds : seeds[(dash + 1)..];
        var parsed = int.TryParse(firstText, NumberStyles.None, CultureInfo.InvariantCulture, out first)
            & int.TryParse(lastText, NumberStyles.None, CultureInfo.InvariantCulture, out last);
        return parsed && first >= 1 && first <= last && last <= MaxSeed;
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A build's stdout line (contracts/simcore-cli.md#agreement-compare).</summary>
    private static string BuildLine(BuildComparison build, AgreementResult result)
    {
        if (build.Status == BuildStatus.NotRun)
        {
            return $"{build.Build}: not run";
        }

        var tables = build.Tables!.Status.ToString().ToLowerInvariant();
        if (build.Status == BuildStatus.Match)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{build.Build}: match, {result.Streams} streams, {result.Steps} steps, tables match");
        }

        return build.Diverged.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{build.Build}: streams match, {result.Streams} streams, {result.Steps} steps; tables {tables}")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{build.Build}: diverged in {build.Diverged.Count} of {result.Streams} streams; first: {build.Diverged[0].Stream} at step {build.Diverged[0].Comparison.FirstDivergentStep}; tables {tables}");
    }

    /// <summary>Prints <c>agreement: cannot run: &lt;reason&gt;</c> and returns exit 2.</summary>
    private static int CannotRun(TextWriter stdout, string reason)
    {
        stdout.Write($"agreement: cannot run: {reason}\n");
        return CliExitCodes.Usage;
    }

    /// <summary><c>agreement prepare</c>'s options.</summary>
    internal sealed record PrepareOptions(string Work, string Seeds, string Streams, string Content, string Config);
}
