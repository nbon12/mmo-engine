using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MmoEngine.SimCore.Cli.Measure;

/// <summary>
/// The checks of <c>simcore measure validate-report</c>
/// (specs/015-shared-simulation-core/contracts/measurement-report.md#what-validate-report-checks) over the
/// report's closed schema (#fields). Each problem is one <c>path: problem</c> line. Paths are dot-separated
/// member names and <c>[i]</c> indexes; a value in a message is its raw JSON text. Every object is walked in
/// the file's member order; after its present members, each missing required member is reported in schema
/// order, and the cross-field checks that belong to the object come last: the runner-class equality and the
/// step coverage of <c>server</c>, the mode set and the interpreted mode of <c>wasm.modes</c>, the loader,
/// <c>wasmNative</c> and totals of each <c>download</c>, and the step kinds of each <c>steps</c>. The checks are
/// structural: no number is judged against a threshold (constitution Principle VI).
/// </summary>
public static partial class MeasurementReportValidator
{
    private const string Produced = "produced";

    private const string InterpPrebuilt = "interp-prebuilt";

    private const string InterpRelinked = "interp-relinked";

    private const string Aot = "aot";

    /// <summary>The three build modes, in schema order.</summary>
    private static readonly string[] ModeNames = [InterpPrebuilt, InterpRelinked, Aot];

    /// <summary>The fields of a produced mode, in schema order.</summary>
    private static readonly string[] ProducedModeFields = [Produced, "reason", "build", "runtime", "download", "startup", "steps", "agreement"];

    /// <summary>The steps a <c>server.benchmarks</c> entry may name.</summary>
    private static readonly string[] ServerSteps = ["movement", "weapon", "projectile", "hitboxBuild", "stateHash"];

    /// <summary>The steps <c>server.benchmarks</c> must cover (US7 scenario 1), and the kinds of every WebAssembly step list.</summary>
    private static readonly string[] StepKinds = ["movement", "weapon", "projectile"];

    /// <summary>The runtimes whose WebAssembly steps are measured, in schema order.</summary>
    private static readonly string[] StepRuntimes = ["node", "chromiumWorker"];

    /// <summary>The named stream that drives each WebAssembly step kind, by <see cref="StepKinds"/> index.</summary>
    private static readonly string[] StepScenarios = ["flat-run-30hz", "rifle-burst", "launcher-arc"];

    /// <summary>The groups of a downloaded file.</summary>
    private static readonly string[] FileGroups = ["loader", "jsModuleNative", "jsModuleRuntime", "wasmNative", "coreAssembly", "assembly", "icu"];

    /// <summary>The size fields of a downloaded file and of the totals.</summary>
    private static readonly string[] Sizes = ["rawBytes", "brotliBytes", "gzipBytes"];

    /// <summary>The startup intervals, in schema order.</summary>
    private static readonly string[] Intervals = ["importMs", "createMs", "firstStepMs", "totalMs"];

    /// <summary>Duplicate member names make the file invalid JSON, so every member is checked exactly once.</summary>
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowDuplicateProperties = false };

    /// <summary>
    /// UTF-8 that throws on a byte sequence it cannot decode, instead of replacing it with U+FFFD, so bytes
    /// that are not UTF-8 cannot pass as a string's text.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Every problem of the report file <paramref name="utf8"/>, as read from disk: a leading UTF-8
    /// byte-order mark is skipped, the rest is decoded strictly, and the text is checked with
    /// <see cref="Validate(string)"/>. Bytes that are not UTF-8 (including UTF-16) are the one problem
    /// <c>$: is not valid JSON (&lt;decoder message&gt;)</c>, as malformed JSON is (check 1).
    /// </summary>
    public static IReadOnlyList<string> ValidateUtf8(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith("\uFEFF"u8))
        {
            utf8 = utf8[3..];
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException ex)
        {
            return [NotValidJson(ex.Message)];
        }

        return Validate(json);
    }

    /// <summary>
    /// Every problem of the report <paramref name="json"/>, in document order; empty when it is valid.
    /// Malformed JSON is the one problem <c>$: is not valid JSON (&lt;message&gt;)</c>, and a root that is
    /// not an object is <c>$: must be an object</c>.
    /// </summary>
    public static IReadOnlyList<string> Validate(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return [NotValidJson(ex.Message)];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ["$: must be an object"];
            }

            ExpectedRunnerClass? runnerClass = null;
            if (root.TryGetProperty("runnerClass", out var value) && value.ValueKind == JsonValueKind.String)
            {
                runnerClass = new ExpectedRunnerClass(value.GetString()!, value.GetRawText());
            }

            var walk = new Walk(runnerClass);
            walk.Report(root);
            return walk.Problems;
        }
    }

    /// <summary>
    /// Every problem of a <c>server</c> object on its own, at path <c>server</c>, with its
    /// <c>runnerClass</c> checked against <paramref name="runnerClass"/>: check 5 and every <c>server</c>
    /// field rule, as the benchmark exporter's file is checked before it is copied into the report.
    /// </summary>
    public static IReadOnlyList<string> ValidateServerObject(JsonElement server, string runnerClass)
    {
        ArgumentNullException.ThrowIfNull(runnerClass);
        var text = JsonSerializer.Serialize(runnerClass, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var walk = new Walk(new ExpectedRunnerClass(runnerClass, text));
        walk.Server(server, "server");
        return walk.Problems;
    }

    // Each format matches the whole string. The patterns end in \z, not $, because $ also matches before
    // a final newline, and a value captured from a command's output without trimming must not pass.
    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\\z", RegexOptions.CultureInvariant)]
    private static partial Regex RecordedAtPattern();

    [GeneratedRegex("^[0-9a-f]{40}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex("^BenchmarkDotNet [0-9]+\\.[0-9]+\\.[0-9]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ToolPattern();

    private static bool IsRecordedAt(string value) =>
        RecordedAtPattern().IsMatch(value)
        && DateTime.TryParseExact(value, "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);

    private static bool IsCommit(string value) => value == "unknown" || CommitPattern().IsMatch(value);

    private static string NotValidJson(string reason) => $"$: is not valid JSON ({reason})";

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static string Index(string path, int index) => string.Create(CultureInfo.InvariantCulture, $"{path}[{index}]");

    /// <summary>Whether <paramref name="element"/> is an object whose member <paramref name="name"/> is a string, and its value.</summary>
    private static bool TryStringMember(JsonElement element, string name, out string value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var member) && member.ValueKind == JsonValueKind.String)
        {
            value = member.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>The number of entries of <paramref name="array"/> whose member <paramref name="name"/> is the string <paramref name="value"/>.</summary>
    private static int CountWith(JsonElement array, string name, string value)
    {
        var count = 0;
        foreach (var entry in array.EnumerateArray())
        {
            if (TryStringMember(entry, name, out var member) && member == value)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// The exact sum of member <paramref name="size"/> over the entries of <paramref name="files"/>, when every
    /// entry is an object whose member is an integer; <see cref="Int128"/>, so no sum of 64-bit sizes wraps.
    /// </summary>
    private static bool TrySum(JsonElement files, string size, out Int128 sum)
    {
        sum = 0;
        foreach (var entry in files.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty(size, out var value)
                || value.ValueKind != JsonValueKind.Number
                || !value.TryGetInt64(out var bytes))
            {
                return false;
            }

            sum += bytes;
        }

        return true;
    }

    /// <summary>The runner class <c>server.runnerClass</c> must equal: its value and its JSON text, as messages print it.</summary>
    private sealed record ExpectedRunnerClass(string Value, string Text);

    /// <summary>One member of an object's schema: its name, its check, and whether and how its absence is reported.</summary>
    private sealed record Field(string Name, Action<JsonElement, string> Check, string Missing = "is required", bool Required = true);

    /// <summary>One validation: the problems found so far, in document order.</summary>
    private sealed class Walk(ExpectedRunnerClass? runnerClass)
    {
        public List<string> Problems { get; } = [];

        /// <summary>The top level (checks 1 to 4, and the objects below).</summary>
        public void Report(JsonElement root) => Object(root, string.Empty, [
            new("formatVersion", (value, path) => IntegerEquals(value, path, 1)),
            new("kind", (value, path) => StringEquals(value, path, "simcore-measurement")),
            new("label", (value, path) => StringEquals(value, path, "measurement")),
            new("recordedAt", (value, path) => StringMatches(value, path, IsRecordedAt, "YYYY-MM-DDTHH:MM:SSZ")),
            new("commit", (value, path) => StringMatches(value, path, IsCommit, "40 lowercase hex digits or \"unknown\"")),
            new("runnerClass", NonEmptyString),
            new("machine", Machine),
            new("dotnet", Dotnet),
            new("node", VersionObject),
            new("chromium", VersionObject),
            new("server", Server),
            new("wasm", Wasm),
        ]);

        /// <summary>The <c>server</c> object (checks 3 and 5), with the runner-class equality and the step coverage at its end.</summary>
        public void Server(JsonElement server, string path) => Object(
            server,
            path,
            [
                new("tool", (value, member) => StringMatches(value, member, ToolPattern().IsMatch, "BenchmarkDotNet <version>")),
                new("label", (value, member) => StringEquals(value, member, "measurement")),
                new("runnerClass", AnyString),
                new("benchmarks", Benchmarks),
            ],
            () => ServerCrossChecks(server, path));

        private void Add(string path, string problem) => Problems.Add($"{path}: {problem}");

        private void AddInvariant(string path, FormattableString problem) => Add(path, problem.ToString(CultureInfo.InvariantCulture));

        /// <summary>
        /// Walks an object: each member in the file's order, an unknown one reported, a known one checked; then
        /// each missing required member in schema order; then <paramref name="crossChecks"/>.
        /// </summary>
        private void Object(JsonElement element, string path, Field[] fields, Action? crossChecks = null)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                Add(path, "must be an object");
                return;
            }

            foreach (var member in element.EnumerateObject())
            {
                var memberPath = Join(path, member.Name);
                var field = Array.Find(fields, candidate => candidate.Name == member.Name);
                if (field is null)
                {
                    Add(memberPath, "is not a known field");
                }
                else
                {
                    field.Check(member.Value, memberPath);
                }
            }

            foreach (var field in fields)
            {
                if (field.Required && !element.TryGetProperty(field.Name, out _))
                {
                    Add(Join(path, field.Name), field.Missing);
                }
            }

            crossChecks?.Invoke();
        }

        /// <summary>Checks every entry of an array with <paramref name="entry"/>, which gets the entry and its path.</summary>
        private void EachEntry(JsonElement element, string path, Action<JsonElement, string> entry)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                Add(path, "must be an array");
                return;
            }

            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                entry(item, Index(path, index));
                index++;
            }
        }

        // ------------------------------------------------------------ the top-level objects

        private void Machine(JsonElement machine, string path) => Object(machine, path, [
            new("cpuModel", NonEmptyString),
            new("logicalCores", (value, member) => IntegerAtLeast(value, member, 1)),
            new("architecture", (value, member) => StringOneOf(value, member, ["x64", "arm64"], "\"x64\" or \"arm64\"", out _)),
            new("os", NonEmptyString),
        ]);

        private void Dotnet(JsonElement dotnet, string path) => Object(dotnet, path, [
            new("sdkVersion", Version),
            new("runtimeVersion", Version),
            new("environment", (environment, member) => Object(environment, member, [
                new("DOTNET_EnableAVX512F", AnyString),
                new("DOTNET_TieredPGO", AnyString),
            ])),
        ]);

        private void VersionObject(JsonElement element, string path) => Object(element, path, [new("version", NonEmptyString)]);

        private void Version(JsonElement value, string path) =>
            StringMatches(value, path, VersionPattern().IsMatch, "<major>.<minor>.<patch>");

        // ------------------------------------------------------------ server

        private void Benchmarks(JsonElement benchmarks, string path)
        {
            var seen = new HashSet<(string Step, string Case)>();
            EachEntry(benchmarks, path, (entry, entryPath) => Object(entry, entryPath, [
                new("step", (value, member) => StringOneOf(value, member, ServerSteps, "movement, weapon, projectile, hitboxBuild or stateHash", out _)),
                new("case", (value, member) =>
                {
                    if (TryString(value, member, out var name) && TryStringMember(entry, "step", out var step) && !seen.Add((step, name)))
                    {
                        Add(member, $"{value.GetRawText()} is not unique within {step}");
                    }
                }),
                new("meanNs", PositiveNumber),
                new("p99Ns", PositiveNumber),
                new("iterations", (value, member) => IntegerAtLeast(value, member, 1)),
                new("allocatedBytesPerOp", (value, member) => IntegerAtLeast(value, member, 0)),
            ]));
        }

        private void ServerCrossChecks(JsonElement server, string path)
        {
            if (runnerClass is not null
                && server.TryGetProperty("runnerClass", out var actual)
                && actual.ValueKind == JsonValueKind.String
                && actual.GetString() != runnerClass.Value)
            {
                Add(Join(path, "runnerClass"), $"is {actual.GetRawText()}; must equal runnerClass {runnerClass.Text}");
            }

            if (server.TryGetProperty("benchmarks", out var benchmarks) && benchmarks.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in StepKinds)
                {
                    if (CountWith(benchmarks, "step", step) == 0)
                    {
                        Add(Join(path, "benchmarks"), $"has no {step} benchmark");
                    }
                }
            }
        }

        // ------------------------------------------------------------ wasm

        private void Wasm(JsonElement wasm, string path) => Object(wasm, path, [new("modes", Modes)]);

        /// <summary>The modes (checks 6 and 8): an unknown mode is reported, and a missing one only by the mode-set check.</summary>
        private void Modes(JsonElement modes, string path)
        {
            if (modes.ValueKind != JsonValueKind.Object)
            {
                Add(path, "must be an object");
                return;
            }

            foreach (var member in modes.EnumerateObject())
            {
                var modePath = Join(path, member.Name);
                if (Array.IndexOf(ModeNames, member.Name) < 0)
                {
                    Add(modePath, "is not a known mode");
                }
                else
                {
                    Mode(member.Value, modePath, member.Name);
                }
            }

            var interpretedProduced = false;
            foreach (var mode in ModeNames)
            {
                if (!modes.TryGetProperty(mode, out var value))
                {
                    Add(path, $"has no {mode} mode");
                }
                else if (mode != Aot
                    && value.ValueKind == JsonValueKind.Object
                    && value.TryGetProperty(Produced, out var produced)
                    && produced.ValueKind == JsonValueKind.True)
                {
                    interpretedProduced = true;
                }
            }

            if (!interpretedProduced)
            {
                Add(path, "no interpreted mode was produced");
            }
        }

        /// <summary>
        /// One mode (checks 7 and 9). An unproduced mode is exactly <c>produced</c> and a non-empty
        /// <c>reason</c>; a produced one has every field. When <c>produced</c> is missing or not a boolean,
        /// only that and unknown fields are reported, because which fields the mode needs depends on it.
        /// </summary>
        private void Mode(JsonElement mode, string path, string name)
        {
            if (mode.ValueKind != JsonValueKind.Object)
            {
                Add(path, "must be an object");
                return;
            }

            var produced = mode.TryGetProperty(Produced, out var flag) ? flag.ValueKind : JsonValueKind.Undefined;
            if (produced == JsonValueKind.False)
            {
                Object(mode, path, [
                    new(Produced, Boolean),
                    new("reason", NonEmptyString, "is required when produced is false"),
                ]);
            }
            else if (produced == JsonValueKind.True)
            {
                Object(mode, path, [
                    new(Produced, Boolean),
                    new("reason", NullReason),
                    new("build", (build, member) => Build(build, member, name)),
                    new("runtime", Runtime),
                    new("download", Download),
                    new("startup", Startup),
                    new("steps", Steps),
                    new("agreement", (agreement, member) => Agreement(agreement, member, name), name == InterpPrebuilt ? "is required" : "is required when the mode was produced"),
                ]);
            }
            else
            {
                var fields = new Field[ProducedModeFields.Length];
                fields[0] = new Field(Produced, Boolean);
                for (var i = 1; i < fields.Length; i++)
                {
                    fields[i] = new Field(ProducedModeFields[i], static (_, _) => { }, Required: false);
                }

                Object(mode, path, fields);
            }
        }

        private void NullReason(JsonElement reason, string path)
        {
            if (reason.ValueKind == JsonValueKind.String)
            {
                Add(path, "must be null when produced is true");
            }
            else if (reason.ValueKind != JsonValueKind.Null)
            {
                Add(path, "must be a string or null");
            }
        }

        /// <summary><c>build</c>'s three booleans match the mode: the prebuilt runtime, the relinked one, or AOT (which relinks, with the workload).</summary>
        private void Build(JsonElement build, string path, string mode)
        {
            var relinked = mode != InterpPrebuilt;
            Object(build, path, [
                new("aot", (value, member) => BooleanEquals(value, member, mode == Aot)),
                new("relinked", (value, member) => BooleanEquals(value, member, relinked)),
                new("workload", (value, member) => BooleanEquals(value, member, relinked)),
            ]);
        }

        private void Runtime(JsonElement runtime, string path) => Object(runtime, path, [
            new("productVersion", NonEmptyString),
            new("gitHash", NonEmptyString),
            new("buildConfiguration", NonEmptyString),
            new("wasmEnableSIMD", Boolean),
            new("wasmEnableExceptionHandling", Boolean),
            new("wasmEnableThreads", Boolean),
            new("debugLevel", (value, member) => IntegerEquals(value, member, 0)),
            new("globalizationMode", NonEmptyString),
            new("runtimeOptions", (value, member) => EachEntry(value, member, AnyString)),
            new("jiterpreter", Boolean),
        ]);

        private void Download(JsonElement download, string path)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            Object(
                download,
                path,
                [
                    new("files", (files, member) => EachEntry(files, member, (entry, entryPath) => Object(entry, entryPath, [
                        new("name", (value, field) =>
                        {
                            if (TryNonEmptyString(value, field, out var name) && !names.Add(name))
                            {
                                Add(field, $"{value.GetRawText()} is not unique");
                            }
                        }),
                        new("group", (value, field) => StringOneOf(value, field, FileGroups, "loader, jsModuleNative, jsModuleRuntime, wasmNative, coreAssembly, assembly or icu", out _)),
                        new("rawBytes", PositiveInteger),
                        new("brotliBytes", PositiveInteger),
                        new("gzipBytes", PositiveInteger),
                    ]))),
                    new("total", (total, member) => Object(total, member, [
                        new("files", Integer),
                        new("rawBytes", Integer),
                        new("brotliBytes", Integer),
                        new("gzipBytes", Integer),
                    ])),
                ],
                () => DownloadCrossChecks(download, path));
        }

        /// <summary>Exactly one <c>loader</c>, named <c>dotnet.js</c>, exactly one <c>wasmNative</c>, and totals that are the count and the exact sums.</summary>
        private void DownloadCrossChecks(JsonElement download, string path)
        {
            if (!download.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var filesPath = Join(path, "files");
            var loaders = CountWith(files, "group", "loader");
            if (loaders != 1 || !LoaderIsDotnetJs(files))
            {
                AddInvariant(filesPath, $"has {loaders} loader entries; must have exactly one, named dotnet.js");
            }

            var wasmNatives = CountWith(files, "group", "wasmNative");
            if (wasmNatives != 1)
            {
                AddInvariant(filesPath, $"has {wasmNatives} wasmNative entries; must have exactly one");
            }

            if (!download.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var totalPath = Join(path, "total");
            var count = files.GetArrayLength();
            if (total.TryGetProperty("files", out var declared) && declared.ValueKind == JsonValueKind.Number && declared.TryGetInt64(out var declaredCount) && declaredCount != count)
            {
                AddInvariant(Join(totalPath, "files"), $"is {declared.GetRawText()}; there are {count} files");
            }

            foreach (var size in Sizes)
            {
                if (total.TryGetProperty(size, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt64(out var declaredSize)
                    && TrySum(files, size, out var sum)
                    && declaredSize != sum)
                {
                    AddInvariant(Join(totalPath, size), $"is {value.GetRawText()}; the files sum to {sum}");
                }
            }
        }

        /// <summary>Whether the one <c>loader</c> entry is the one named <c>dotnet.js</c>.</summary>
        private static bool LoaderIsDotnetJs(JsonElement files)
        {
            foreach (var entry in files.EnumerateArray())
            {
                if (TryStringMember(entry, "group", out var group) && group == "loader")
                {
                    return TryStringMember(entry, "name", out var name) && name == "dotnet.js";
                }
            }

            return false;
        }

        private void Startup(JsonElement startup, string path) => Object(startup, path, [
            new("coldStarts", (value, member) => IntegerEquals(value, member, 10)),
            new("node", (value, member) => Object(value, member, IntervalFields(isolated: false))),
            new("chromiumWorker", (value, member) => Object(value, member, IntervalFields(isolated: true))),
        ]);

        /// <summary>A runtime's startup intervals, and for the Chromium Worker <c>crossOriginIsolated</c>, which must be true.</summary>
        private Field[] IntervalFields(bool isolated)
        {
            var fields = new List<Field>(Intervals.Length + 1);
            if (isolated)
            {
                fields.Add(new Field("crossOriginIsolated", (value, member) => BooleanEquals(value, member, true)));
            }

            foreach (var interval in Intervals)
            {
                fields.Add(new Field(interval, Interval));
            }

            return [.. fields];
        }

        /// <summary>An interval's positive <c>median</c> and its <c>p90</c>, positive and at least the median.</summary>
        private void Interval(JsonElement interval, string path) => Object(interval, path, [
            new("median", PositiveNumber),
            new("p90", (value, member) =>
            {
                if (!TryNumber(value, member, out var p90))
                {
                    return;
                }

                if (!(p90 > 0))
                {
                    Add(member, $"is {value.GetRawText()}; must be greater than 0");
                }
                else if (interval.TryGetProperty("median", out var median)
                    && median.ValueKind == JsonValueKind.Number
                    && median.TryGetDouble(out var medianValue)
                    && medianValue > 0
                    && p90 < medianValue)
                {
                    Add(member, $"is {value.GetRawText()}; must be at least the median {median.GetRawText()}");
                }
            }),
        ]);

        private void Steps(JsonElement steps, string path) => Object(
            steps,
            path,
            [
                new("node", (value, member) => StepEntries(value, member, 1)),
                new("chromiumWorker", (value, member) => StepEntries(value, member, 16)),
            ],
            () =>
            {
                foreach (var runtime in StepRuntimes)
                {
                    if (steps.TryGetProperty(runtime, out var entries) && entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var kind in StepKinds)
                        {
                            if (CountWith(entries, "kind", kind) == 0)
                            {
                                Add(Join(path, runtime), $"has no {kind} entry");
                            }
                        }
                    }
                }
            });

        /// <summary>One runtime's step entries: each kind once, with its stream, the runtime's batch and at least 3,000 timed steps.</summary>
        private void StepEntries(JsonElement entries, string path, int batch)
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            EachEntry(entries, path, (entry, entryPath) => Object(entry, entryPath, [
                new("kind", (value, member) =>
                {
                    if (StringOneOf(value, member, StepKinds, "movement, weapon or projectile", out var kind) && !kinds.Add(kind))
                    {
                        Add(member, $"{value.GetRawText()} is not unique");
                    }
                }),
                new("scenario", (value, member) =>
                {
                    if (TryString(value, member, out var scenario) && TryStringMember(entry, "kind", out var kind))
                    {
                        var index = Array.IndexOf(StepKinds, kind);
                        if (index >= 0 && scenario != StepScenarios[index])
                        {
                            Add(member, $"is {value.GetRawText()}; must be \"{StepScenarios[index]}\" for {kind}");
                        }
                    }
                }),
                new("batch", (value, member) => IntegerEquals(value, member, batch)),
                new("steps", (value, member) => IntegerAtLeast(value, member, 3000)),
                new("meanNs", PositiveNumber),
                new("p99Ns", PositiveNumber),
            ]));
        }

        /// <summary>
        /// The mode's agreement: null for <c>interp-prebuilt</c>, whose agreement is the <c>agreement</c> job's;
        /// for the other two, the build that ran it and a known status.
        /// </summary>
        private void Agreement(JsonElement agreement, string path, string mode)
        {
            if (agreement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            {
                Add(path, "must be an object or null");
            }
            else if (mode == InterpPrebuilt)
            {
                if (agreement.ValueKind == JsonValueKind.Object)
                {
                    Add(path, "must be null for interp-prebuilt");
                }
            }
            else if (agreement.ValueKind == JsonValueKind.Null)
            {
                Add(path, "is required when the mode was produced");
            }
            else
            {
                var build = mode == InterpRelinked ? "node-interp-relinked" : "node-aot";
                Object(agreement, path, [
                    new("build", (value, member) => StringEquals(value, member, build)),
                    new("status", (value, member) => StringOneOf(value, member, ["Match", "Diverged", "NotRun"], "Match, Diverged or NotRun", out _)),
                ]);
            }
        }

        // ------------------------------------------------------------ values

        private bool TryString(JsonElement value, string path, out string text)
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                text = value.GetString()!;
                return true;
            }

            Add(path, "must be a string");
            text = string.Empty;
            return false;
        }

        private void AnyString(JsonElement value, string path) => TryString(value, path, out _);

        private bool TryNonEmptyString(JsonElement value, string path, out string text)
        {
            if (!TryString(value, path, out text))
            {
                return false;
            }

            if (text.Length == 0)
            {
                Add(path, "must not be empty");
                return false;
            }

            return true;
        }

        private void NonEmptyString(JsonElement value, string path) => TryNonEmptyString(value, path, out _);

        private void StringEquals(JsonElement value, string path, string expected)
        {
            if (TryString(value, path, out var text) && text != expected)
            {
                Add(path, $"is {value.GetRawText()}; must be \"{expected}\"");
            }
        }

        private bool StringOneOf(JsonElement value, string path, string[] allowed, string alternatives, out string text)
        {
            if (!TryString(value, path, out text))
            {
                return false;
            }

            if (Array.IndexOf(allowed, text) < 0)
            {
                Add(path, $"is {value.GetRawText()}; must be {alternatives}");
                return false;
            }

            return true;
        }

        private void StringMatches(JsonElement value, string path, Func<string, bool> matches, string format)
        {
            if (TryString(value, path, out var text) && !matches(text))
            {
                Add(path, $"is {value.GetRawText()}; must match {format}");
            }
        }

        private bool TryInteger(JsonElement value, string path, out long number)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number))
            {
                return true;
            }

            Add(path, "must be an integer");
            number = 0;
            return false;
        }

        private void Integer(JsonElement value, string path) => TryInteger(value, path, out _);

        private void IntegerEquals(JsonElement value, string path, long expected)
        {
            if (TryInteger(value, path, out var number) && number != expected)
            {
                AddInvariant(path, $"is {value.GetRawText()}; must be {expected}");
            }
        }

        private void IntegerAtLeast(JsonElement value, string path, long least)
        {
            if (TryInteger(value, path, out var number) && number < least)
            {
                AddInvariant(path, $"is {value.GetRawText()}; must be at least {least}");
            }
        }

        private void PositiveInteger(JsonElement value, string path)
        {
            if (TryInteger(value, path, out var number) && number <= 0)
            {
                Add(path, $"is {value.GetRawText()}; must be greater than 0");
            }
        }

        private bool TryNumber(JsonElement value, string path, out double number)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number))
            {
                return true;
            }

            Add(path, "must be a number");
            number = 0;
            return false;
        }

        private void PositiveNumber(JsonElement value, string path)
        {
            if (TryNumber(value, path, out var number) && !(number > 0))
            {
                Add(path, $"is {value.GetRawText()}; must be greater than 0");
            }
        }

        private bool TryBoolean(JsonElement value, string path, out bool flag)
        {
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                flag = value.ValueKind == JsonValueKind.True;
                return true;
            }

            Add(path, "must be a boolean");
            flag = false;
            return false;
        }

        private void Boolean(JsonElement value, string path) => TryBoolean(value, path, out _);

        private void BooleanEquals(JsonElement value, string path, bool expected)
        {
            if (TryBoolean(value, path, out var flag) && flag != expected)
            {
                Add(path, $"is {value.GetRawText()}; must be {(expected ? "true" : "false")}");
            }
        }
    }
}
