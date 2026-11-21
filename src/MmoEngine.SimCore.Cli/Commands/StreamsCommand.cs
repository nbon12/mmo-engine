using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.Text;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server;
using Serilog;

namespace MmoEngine.SimCore.Cli.Commands;

/// <summary>
/// <c>streams record</c> and <c>streams verify</c> (FR-028, US1.7,
/// specs/015-shared-simulation-core/contracts/simcore-cli.md#streams-record and #streams-verify).
/// <c>streams record</c> is the only way the committed expected hashes change: it builds the selected
/// <see cref="ScenarioCatalog"/> scenarios, runs each through the stream runner it is given (the server
/// build's <see cref="StreamRunner"/>), writes the added and changed streams, removes those that left the
/// catalog, and appends one entry with the reason to the streams folder's regeneration log; every refusal
/// exits 2 (a step fault exits 1) before anything is written. <c>streams verify</c> runs every stream in a
/// folder through <see cref="StreamRunner"/> and compares each step's hash with the stream's expected
/// hash, writing nothing; a stream in another format version or rules revision is not comparable. An empty or whitespace-only path option of either command is refused when the
/// command line is parsed (<see cref="PathOptions"/>), exit 2.
/// </summary>
internal static class StreamsCommand
{
    /// <summary>The committed streams folder, the default <c>--out</c> and <c>--dir</c>.</summary>
    public const string DefaultStreams = "simcore/streams";

    /// <summary>The committed content folder, the default <c>--content</c>.</summary>
    public const string DefaultContent = "simcore/content";

    /// <summary>The default configuration, the default <c>--config</c>.</summary>
    public const string DefaultConfig = "simcore/config/default.json";

    /// <summary>The longest reason <c>streams record</c> accepts, in characters.</summary>
    public const int MaxReasonLength = 500;

    private const string RecordName = "streams record";

    private const string VerifyName = "streams verify";

    /// <summary>The trailer's length: the XxHash64 of everything before it.</summary>
    private const int TrailerLength = 8;

    /// <summary>A stream's own <c>u16 version</c>: its offset, its field and its format's name in a refusal.</summary>
    private const int StreamVersionOffset = 4;

    private const string StreamVersionField = "version";

    private const string StreamFormat = "stream";

    /// <summary>The <c>streams</c> command with its <c>record</c> and <c>verify</c> subcommands.</summary>
    /// <param name="runStream">
    /// How <c>streams record</c> runs a built stream over the loaded content: the server build's
    /// <see cref="StreamRunner"/>, or, in a test, a runner that faults.
    /// </param>
    public static Command Create(
        TextWriter stdout,
        TextWriter stderr,
        ILogger logger,
        Func<ReadOnlyMemory<byte>, IReadOnlyList<CollisionWorld>, byte[]> runStream)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runStream);

        var scenario = new Option<string?>("--scenario") { Description = "The scenario to record, by name." };
        var all = new Option<bool>("--all") { Description = "Record every scenario in the catalog and remove streams no longer in it." };
        var reason = new Option<string?>("--reason") { Description = "Why the expected hashes change: one line of at most 500 characters." };
        var output = PathOptions.RequireNonEmpty(new Option<string>("--out") { Description = "The streams folder.", DefaultValueFactory = _ => DefaultStreams });
        var recordContent = PathOptions.RequireNonEmpty(new Option<string>("--content") { Description = "The folder of collision files.", DefaultValueFactory = _ => DefaultContent });
        var config = PathOptions.RequireNonEmpty(new Option<string>("--config") { Description = "The base configuration.", DefaultValueFactory = _ => DefaultConfig });
        var record = new Command("record", "Regenerates committed streams and their expected hashes.")
        {
            scenario,
            all,
            reason,
            output,
            recordContent,
            config,
        };
        record.SetAction(parse => Record(
            new RecordOptions(
                parse.GetValue(scenario),
                parse.GetValue(all),
                parse.GetValue(reason),
                parse.GetValue(output)!,
                parse.GetValue(recordContent)!,
                parse.GetValue(config)!),
            stdout,
            stderr,
            logger,
            runStream));

        var directory = PathOptions.RequireNonEmpty(new Option<string>("--dir") { Description = "The streams folder.", DefaultValueFactory = _ => DefaultStreams });
        var verifyContent = PathOptions.RequireNonEmpty(new Option<string>("--content") { Description = "The folder of collision files.", DefaultValueFactory = _ => DefaultContent });
        var verify = new Command("verify", "Runs every stream through the server build and checks every expected hash.")
        {
            directory,
            verifyContent,
        };
        verify.SetAction(parse => Verify(parse.GetValue(directory)!, parse.GetValue(verifyContent)!, stdout, stderr, logger));

        return new Command("streams", "Recorded streams.") { record, verify };
    }

    /// <summary>
    /// <c>streams record</c>, steps 1 to 6 of contracts/simcore-cli.md#streams-record: refuse the arguments,
    /// the configuration or the content (exit 2); build and run every selected scenario, in name order,
    /// through <paramref name="runStream"/> (a step fault exits 1, a refusal 2); then, with nothing refused,
    /// build the regeneration entry and the output lines, and only then write the added and changed streams,
    /// remove those that left the catalog (with <c>--all</c>), append the entry when anything changed, and
    /// print one line per stream and the total. An old file that no longer decodes shows its final state as
    /// <c>unreadable</c>.
    /// </summary>
    internal static int Record(
        RecordOptions options,
        TextWriter stdout,
        TextWriter stderr,
        ILogger logger,
        Func<ReadOnlyMemory<byte>, IReadOnlyList<CollisionWorld>, byte[]> runStream)
    {
        // 1: the reason, the selector and the scenario name.
        if (ReasonProblem(options.Reason) is { } problem)
        {
            stderr.Write($"{RecordName}: --reason {problem}\n");
            return CliExitCodes.Usage;
        }

        if (options.Scenario is not null && options.All)
        {
            stderr.Write($"{RecordName}: give either --scenario <name> or --all, not both\n");
            return CliExitCodes.Usage;
        }

        if (options.Scenario is null && !options.All)
        {
            stderr.Write($"{RecordName}: give --scenario <name> or --all\n");
            return CliExitCodes.Usage;
        }

        if (!TrySelect(options, stderr, out var definitions))
        {
            return CliExitCodes.Usage;
        }

        // The base configuration, bound and checked against the rule table.
        var load = SimConfigJson.Load(options.Config);
        var configErrors = load.Config is null ? load.Errors : SimConfigValidator.Errors(load.Config);
        if (configErrors.Count > 0)
        {
            stderr.Write($"{RecordName}: the configuration {options.Config} is invalid\n");
            WriteConfigErrors(stderr, configErrors, indent: string.Empty);
            return CliExitCodes.Usage;
        }

        var baseConfig = load.Config!;

        // The content, loaded before any stream runs.
        if (!ContentFiles.TryLoadAll(options.Content, RecordName, stderr, out var contentNames, out var worlds))
        {
            return CliExitCodes.Usage;
        }

        // 2 and 3: build and run every selected scenario; nothing is written until all have run.
        var recordings = new List<Recording>(definitions.Count);
        foreach (var definition in definitions)
        {
            var file = IndexOf(contentNames, definition.CollisionFileName);
            if (file < 0)
            {
                stderr.Write($"{RecordName}: {definition.Name}: the collision file {Path.Combine(options.Content, definition.CollisionFileName)} does not exist\n");
                return CliExitCodes.Usage;
            }

            try
            {
                var built = definition.Build(baseConfig, worlds[file]);
                var records = runStream(built.Encode(), worlds);
                var recorded = ScenarioDefinition.WithExpectedHashes(built, records);
                recordings.Add(new Recording(definition.Name, recorded.Encode(), recorded));
            }
            catch (StepFaultException ex)
            {
                stderr.Write($"{RecordName}: {definition.Name}: {ex.Message}\n");
                return CliExitCodes.Failed;
            }
            catch (ConfigValidationException ex)
            {
                stderr.Write($"{RecordName}: {definition.Name}: the configuration is invalid\n");
                WriteConfigErrors(stderr, ex.Errors, indent: string.Empty);
                return CliExitCodes.Usage;
            }
            catch (CoreFormatException ex)
            {
                stderr.Write($"{RecordName}: {definition.Name}: {ex.Message}\n");
                return CliExitCodes.Usage;
            }
            catch (ScenarioRefusedException ex)
            {
                // A planned definition that the configuration or the content cannot give is a refused input.
                stderr.Write($"{RecordName}: {definition.Name}: {ex.Reason}\n");
                return CliExitCodes.Usage;
            }
        }

        // 4 and 5: compare with the streams already in the folder.
        List<Change> changes;
        try
        {
            changes = Compare(options.Out, recordings, options.All);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.Write($"{RecordName}: {options.Out}: the streams cannot be read ({ex.Message})\n");
            return CliExitCodes.Usage;
        }

        // The regeneration entry and the output lines are built before the first write, so nothing that
        // runs after a stream file is replaced can fail except the writes themselves.
        var command = options.All ? "simcore streams record --all" : $"simcore streams record --scenario {options.Scenario}";
        var regenerated = changes.Where(change => change.Kind != ChangeKind.Unchanged).ToList();
        var entry = regenerated.Count > 0 ? Entry(DateTime.UtcNow, options.Reason!, command, regenerated) : null;
        var lines = changes.Select(change => change.Line()).ToList();

        // Write, remove and append; each stream file is replaced atomically.
        try
        {
            foreach (var change in regenerated)
            {
                if (change.Kind == ChangeKind.Removed)
                {
                    StreamFiles.RemoveCommitted(options.Out, change.Name);
                }
                else
                {
                    StreamFiles.WriteCommitted(options.Out, change.Name, change.NewBytes!);
                }
            }

            if (entry is not null)
            {
                StreamFiles.AppendRegenerations(options.Out, entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.Write($"{RecordName}: {options.Out}: the streams cannot be written ({ex.Message})\n");
            return CliExitCodes.Usage;
        }

        foreach (var line in lines)
        {
            stdout.Write(line);
            stdout.Write('\n');
        }

        var changed = changes.Count(change => change.Kind == ChangeKind.Changed);
        var added = changes.Count(change => change.Kind == ChangeKind.Added);
        var removed = changes.Count(change => change.Kind == ChangeKind.Removed);
        var unchanged = changes.Count(change => change.Kind == ChangeKind.Unchanged);
        stdout.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"{RecordName}: {changes.Count} streams, {changed} changed, {added} added, {removed} removed, {unchanged} unchanged\n"));
        logger.Information(
            "Recorded streams with {Command}: {Changed} changed, {Added} added, {Removed} removed, {Unchanged} unchanged",
            command,
            changed,
            added,
            removed,
            unchanged);
        return CliExitCodes.Success;
    }

    /// <summary>
    /// <c>streams verify</c> (contracts/simcore-cli.md#streams-verify): loads the content through
    /// <see cref="ContentFiles"/> (a refused file exits 2 before any stream runs), runs every
    /// <c>&lt;dir&gt;/*.mmrs</c> in ordinal order through <see cref="StreamRunner"/>, prints each stream's
    /// outcome (match, the divergence report against the expected hashes, <c>NOT COMPARABLE</c> for a
    /// stream in another format version or rules revision, or <c>REFUSED</c>) and the total, and exits 1
    /// when any diverged, else 2 when any was not comparable or refused or the folder holds no stream, else
    /// 0.
    /// </summary>
    internal static int Verify(string directory, string content, TextWriter stdout, TextWriter stderr, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            stderr.Write($"{VerifyName}: --dir {directory}: the directory does not exist\n");
            return CliExitCodes.Usage;
        }

        if (!ContentFiles.TryLoadAll(content, VerifyName, stderr, out _, out var worlds))
        {
            return CliExitCodes.Usage;
        }

        string[] paths;
        try
        {
            paths = StreamPaths(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.Write($"{VerifyName}: --dir {directory}: the directory cannot be read ({ex.Message})\n");
            return CliExitCodes.Usage;
        }

        var (matched, diverged, notComparable, refused) = (0, 0, 0, 0);
        foreach (var path in paths)
        {
            var verified = VerifyStream(Path.GetFileNameWithoutExtension(path), path, worlds);
            stdout.Write(verified.Text);
            stdout.Write('\n');
            switch (verified.Outcome)
            {
                case VerifyOutcome.Match:
                    matched++;
                    break;
                case VerifyOutcome.Diverged:
                    diverged++;
                    break;
                case VerifyOutcome.NotComparable:
                    notComparable++;
                    break;
                default:
                    refused++;
                    break;
            }
        }

        stdout.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"{VerifyName}: {paths.Length} streams, {matched} match, {diverged} diverged, {notComparable} not comparable, {refused} refused\n"));
        logger.Information(
            "Verified {Count} streams in {Directory}: {Matched} match, {Diverged} diverged, {NotComparable} not comparable, {Refused} refused",
            paths.Length,
            directory,
            matched,
            diverged,
            notComparable,
            refused);
        if (diverged > 0)
        {
            return CliExitCodes.Failed;
        }

        return notComparable > 0 || refused > 0 || paths.Length == 0 ? CliExitCodes.Usage : CliExitCodes.Success;
    }

    /// <summary>
    /// Whether a refusal means the stream is not comparable (contracts/recorded-stream-format.md#outcomes):
    /// the stream's own format version (<c>UnsupportedVersion</c> on its <c>version</c> field at offset 4),
    /// or another rules revision (<c>RulesRevisionMismatch</c>). Every other refusal, an embedded input
    /// command's version among them, is an ordinary refusal. <c>agreement prepare</c> applies the same rule.
    /// </summary>
    internal static bool IsNotComparable(FormatError error) =>
        error.Code == FormatErrorCode.RulesRevisionMismatch
        || (error.Code == FormatErrorCode.UnsupportedVersion
            && error.Offset == StreamVersionOffset
            && string.Equals(error.Format, StreamFormat, StringComparison.Ordinal)
            && string.Equals(error.Field, StreamVersionField, StringComparison.Ordinal));

    /// <summary>
    /// Runs one stream file through the server build and checks every step's hash against the stream's
    /// expected hash, as <c>streams verify</c> does for each stream and <c>agreement prepare</c> does for each
    /// committed stream (contracts/simcore-cli.md#agreement-prepare step 1). The result holds the outcome and
    /// its text, without a final line break: <c>&lt;name&gt;: match, &lt;steps&gt; steps, final &lt;hash&gt;</c>,
    /// the divergence report against the expected hashes, the <c>NOT COMPARABLE stream</c> line, or the
    /// <c>REFUSED stream</c> line (with one indented <c>path: message</c> line per configuration error). A
    /// match also holds the file's bytes, the server build's records and the decoded stream.
    /// </summary>
    internal static VerifiedStream VerifyStream(string name, string path, IReadOnlyList<CollisionWorld> worlds)
    {
        byte[] bytes;
        byte[] records;
        RecordedStream stream;
        try
        {
            bytes = ReadReadOnly(path);
            records = StreamRunner.Run(bytes, worlds);
            stream = RecordedStream.Decode(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerifiedStream.Failed(name, VerifyOutcome.Refused, $"REFUSED stream {name}: the file cannot be read ({ex.Message})");
        }
        catch (CoreFormatException ex) when (IsNotComparable(ex.Error))
        {
            return VerifiedStream.Failed(name, VerifyOutcome.NotComparable, DivergenceReport.NotComparable(name, ex.Error));
        }
        catch (Exception ex) when (ex is CoreFormatException or StepFaultException)
        {
            return VerifiedStream.Failed(name, VerifyOutcome.Refused, $"REFUSED stream {name}: {ex.Message}");
        }
        catch (ConfigValidationException ex)
        {
            var text = new StringBuilder($"REFUSED stream {name}: the configuration is invalid");
            foreach (var error in ex.Errors)
            {
                text.Append($"\n  {error.Path}: {error.Message}");
            }

            return VerifiedStream.Failed(name, VerifyOutcome.Refused, text.ToString());
        }

        // Decode refuses a step entry whose number is not the initial state's step + i + 1, and the runner
        // writes one record per entry numbered by the world's step, so a missing or misnumbered record is a
        // runner bug, not a stream to refuse.
        var offset = 0;
        var last = 0UL;
        foreach (var entry in stream.Steps)
        {
            if (!StepRecord.TryRead(records.AsSpan(offset), out var step, out _, out var hash, out var length) || step != entry.Step)
            {
                throw new InvalidOperationException($"The run of {name} returned no record of step {entry.Step} at byte {offset}.");
            }

            if (hash != entry.ExpectedHash)
            {
                return VerifiedStream.Failed(name, VerifyOutcome.Diverged, DivergenceReport.AgainstExpected(name, step, entry.ExpectedHash, hash));
            }

            last = hash;
            offset += length;
        }

        return new VerifiedStream(
            name,
            VerifyOutcome.Match,
            string.Create(CultureInfo.InvariantCulture, $"{name}: match, {stream.Steps.Count} steps, final {last:x16}"),
            bytes,
            records,
            stream);
    }

    /// <summary>Why <paramref name="reason"/> is refused, or null when it is a usable reason.</summary>
    private static string? ReasonProblem(string? reason)
    {
        if (reason is null)
        {
            return "is required: say why the expected hashes change";
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return "must not be empty or whitespace";
        }

        if (reason.Length > MaxReasonLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"is {reason.Length} characters; the limit is {MaxReasonLength}");
        }

        if (reason.AsSpan().IndexOfAny("\r\n\u0085\u2028\u2029") >= 0)
        {
            return "must be one line";
        }

        return null;
    }

    /// <summary>The selected definitions in name order: every scenario with <c>--all</c>, else the named one.</summary>
    private static bool TrySelect(RecordOptions options, TextWriter stderr, out IReadOnlyList<ScenarioDefinition> definitions)
    {
        if (options.All)
        {
            definitions = [.. ScenarioCatalog.Names.Select(ScenarioCatalog.Get)];
            return true;
        }

        try
        {
            definitions = [ScenarioCatalog.Get(options.Scenario!)];
            return true;
        }
        catch (KeyNotFoundException)
        {
            stderr.Write($"{RecordName}: --scenario {options.Scenario}: there is no such scenario; the known scenarios are {string.Join(", ", ScenarioCatalog.Names)}\n");
            definitions = [];
            return false;
        }
    }

    /// <summary>
    /// Each recording against the stream of its name in <paramref name="outDir"/> (added, changed or
    /// unchanged) and, with <paramref name="removeOthers"/>, every other <c>.mmrs</c> there as removed, in
    /// ordinal name order.
    /// </summary>
    private static List<Change> Compare(string outDir, List<Recording> recordings, bool removeOthers)
    {
        var changes = new List<Change>();
        foreach (var recording in recordings)
        {
            var path = StreamFiles.StreamPath(outDir, recording.Name);
            if (!File.Exists(path))
            {
                changes.Add(new Change(recording.Name, ChangeKind.Added, null, recording.Bytes, null, recording.Stream));
                continue;
            }

            var old = ReadReadOnly(path);
            var kind = old.AsSpan().SequenceEqual(recording.Bytes) ? ChangeKind.Unchanged : ChangeKind.Changed;
            changes.Add(new Change(recording.Name, kind, old, recording.Bytes, TryDecode(old), recording.Stream));
        }

        if (removeOthers && Directory.Exists(outDir))
        {
            var recorded = new HashSet<string>(recordings.Select(recording => recording.Name), StringComparer.Ordinal);
            foreach (var path in StreamPaths(outDir))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!recorded.Contains(name))
                {
                    changes.Add(new Change(name, ChangeKind.Removed, ReadReadOnly(path), null, null, null));
                }
            }
        }

        changes.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return changes;
    }

    /// <summary>
    /// One regeneration entry (contracts/simcore-cli.md#regenerationsmd): the UTC heading to the second, the
    /// reason exactly as given, the command, the rules revision (with the old one when a changed stream
    /// carried another), and the table of the added, changed and removed streams.
    /// </summary>
    private static string Entry(DateTime utcNow, string reason, string command, List<Change> regenerated)
    {
        var revision = SimCoreVersion.RulesRevision.ToString(CultureInfo.InvariantCulture);
        var others = regenerated
            .Where(change => change.Kind == ChangeKind.Changed && change.Old is not null && change.Old.RulesRevision != SimCoreVersion.RulesRevision)
            .Select(change => change.Old!.RulesRevision)
            .Distinct()
            .Order()
            .ToArray();
        if (others.Length > 0)
        {
            revision += $" (was {string.Join(", ", others.Select(r => r.ToString(CultureInfo.InvariantCulture)))})";
        }

        var entry = new StringBuilder();
        entry.Append(CultureInfo.InvariantCulture, $"## {utcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\n");
        entry.Append('\n');
        entry.Append($"- Reason: {reason}\n");
        entry.Append($"- Command: `{command}`\n");
        entry.Append($"- Rules revision: {revision}\n");
        entry.Append('\n');
        entry.Append("| Stream | File hash | Final state hash | First changed step |\n");
        entry.Append("| --- | --- | --- | --- |\n");
        foreach (var change in regenerated)
        {
            entry.Append(change.Row());
            entry.Append('\n');
        }

        return entry.ToString();
    }

    /// <summary>Every <c>*.mmrs</c> in <paramref name="directory"/>, in ordinal order.</summary>
    internal static string[] StreamPaths(string directory)
    {
        var paths = Directory.GetFiles(directory, "*" + StreamFiles.Extension, SearchOption.TopDirectoryOnly)
            .Where(path => path.EndsWith(StreamFiles.Extension, StringComparison.Ordinal))
            .ToArray();
        Array.Sort(paths, StringComparer.Ordinal);
        return paths;
    }

    /// <summary>The whole file, read through a read-only stream.</summary>
    private static byte[] ReadReadOnly(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>The decoded stream, or null when the old file no longer decodes.</summary>
    private static RecordedStream? TryDecode(byte[] bytes)
    {
        try
        {
            return RecordedStream.Decode(bytes);
        }
        catch (CoreFormatException)
        {
            return null;
        }
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

    private static void WriteConfigErrors(TextWriter writer, IReadOnlyList<ConfigError> errors, string indent)
    {
        foreach (var error in errors)
        {
            writer.Write($"{indent}{error.Path}: {error.Message}\n");
        }
    }

    /// <summary>A file's trailer as 16 lowercase hex digits, or <c>none</c> when it is shorter than a trailer.</summary>
    private static string FileHash(byte[] bytes) => bytes.Length < TrailerLength
        ? "none"
        : BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(bytes.Length - TrailerLength)).ToString("x16", CultureInfo.InvariantCulture);

    private static string Hex(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary><c>streams record</c>'s options.</summary>
    internal sealed record RecordOptions(string? Scenario, bool All, string? Reason, string Out, string Content, string Config);

    /// <summary>
    /// One stream's verify result: its name, outcome and printed text (no final line break), and for a match
    /// the file's bytes, the server build's packed step records and the decoded stream (null otherwise).
    /// </summary>
    internal sealed record VerifiedStream(string Name, VerifyOutcome Outcome, string Text, byte[]? Bytes, byte[]? Records, RecordedStream? Stream)
    {
        /// <summary>A stream that did not match: only its name, outcome and text.</summary>
        public static VerifiedStream Failed(string name, VerifyOutcome outcome, string text) => new(name, outcome, text, null, null, null);
    }

    /// <summary>A scenario's new stream: its encoded bytes and the decoded stream with the expected hashes.</summary>
    private sealed record Recording(string Name, byte[] Bytes, RecordedStream Stream);

    /// <summary>A verified stream's outcome (contracts/recorded-stream-format.md#outcomes).</summary>
    internal enum VerifyOutcome
    {
        Match,
        Diverged,
        NotComparable,
        Refused,
    }

    private enum ChangeKind
    {
        Unchanged,
        Changed,
        Added,
        Removed,
    }

    /// <summary>
    /// What recording did to one stream file: its old bytes and decoded stream (null when added, or when the
    /// old file no longer decodes) and its new bytes and stream (null when removed).
    /// </summary>
    private sealed record Change(string Name, ChangeKind Kind, byte[]? OldBytes, byte[]? NewBytes, RecordedStream? Old, RecordedStream? New)
    {
        /// <summary>The stdout line: <c>&lt;name&gt;: changed; …</c>, <c>added; …</c>, <c>removed; …</c> or <c>unchanged; …</c>.</summary>
        public string Line() => Kind switch
        {
            ChangeKind.Changed => $"{Name}: changed; file {FileHash(OldBytes!)} -> {FileHash(NewBytes!)}; final state {OldFinal()} -> {NewFinal()}; first changed step {FirstChangedStep()}",
            ChangeKind.Added => $"{Name}: added; file {FileHash(NewBytes!)}; final state {NewFinal()}",
            ChangeKind.Removed => $"{Name}: removed; file {FileHash(OldBytes!)}",
            _ => $"{Name}: unchanged; file {FileHash(NewBytes!)}; final state {NewFinal()}",
        };

        /// <summary>The regeneration table's row for an added, changed or removed stream.</summary>
        public string Row() => Kind switch
        {
            ChangeKind.Changed => $"| {Name} | {FileHash(OldBytes!)} -> {FileHash(NewBytes!)} | {OldFinal()} -> {NewFinal()} | {FirstChangedStep()} |",
            ChangeKind.Added => $"| {Name} | added: {FileHash(NewBytes!)} | added: {NewFinal()} | |",
            ChangeKind.Removed => $"| {Name} | removed: {FileHash(OldBytes!)} | removed | |",
            _ => throw new InvalidOperationException($"{Name} is unchanged and has no row."),
        };

        private string NewFinal() => Hex(New!.Steps[^1].ExpectedHash);

        private string OldFinal() => Old is null ? "unreadable" : Hex(Old.Steps[^1].ExpectedHash);

        /// <summary>
        /// The first step whose expected hash differs from the old file's: within the steps both hold, the
        /// first different one; else the first step only one of them holds; <c>none</c> when every expected
        /// hash is the same.
        /// </summary>
        private string FirstChangedStep()
        {
            var steps = New!.Steps;
            if (Old is null)
            {
                return steps[0].Step.ToString(CultureInfo.InvariantCulture);
            }

            var common = System.Math.Min(Old.Steps.Count, steps.Count);
            for (var i = 0; i < common; i++)
            {
                if (Old.Steps[i].Step != steps[i].Step || Old.Steps[i].ExpectedHash != steps[i].ExpectedHash)
                {
                    return steps[i].Step.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (steps.Count != Old.Steps.Count)
            {
                var longer = steps.Count > Old.Steps.Count ? steps : Old.Steps;
                return longer[common].Step.ToString(CultureInfo.InvariantCulture);
            }

            return "none";
        }
    }
}
