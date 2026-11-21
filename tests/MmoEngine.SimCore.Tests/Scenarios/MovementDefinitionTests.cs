using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The 11 movement definitions of <see cref="ScenarioCatalog"/> (FR-022, FR-028,
/// contracts/simcore-cli.md#named-scenario-streams). Built at any of the three step rates, a definition
/// keeps its duration in simulated seconds and is unrecorded (every expected hash 0). Built at its own
/// rate, it is exactly the committed <c>simcore/streams/&lt;name&gt;.mmrs</c> less the expected hashes, and
/// running it gives that file's expected hashes, so a committed stream always says what its definition
/// does and changes only through <c>streams record</c>.
/// </summary>
/// <remarks>
/// Definitions are built over <see cref="TestWorlds.TestWorld"/>, the generated test world, with
/// <see cref="TestConfigs.Default"/>, the configuration <c>streams record</c> reads by default. The
/// committed stream is read through <see cref="RepoPaths"/>, read-only.
/// </remarks>
public sealed class MovementDefinitionTests
{
    /// <summary>The movement scenarios recorded in Phase 3, in ordinal name order.</summary>
    private static readonly string[] MovementNames =
    [
        "corner-wedge",
        "crouch-under-ledge",
        "flat-run-20hz",
        "flat-run-30hz",
        "flat-run-60hz",
        "jump-land",
        "slope-limit",
        "stairs-blocked",
        "stairs-climb",
        "thin-wall-fast",
        "wall-slide-45",
    ];

    /// <summary>Each movement name crossed with 20, 30 and 60 Hz.</summary>
    public static TheoryData<string, int> Cases => TestRates.WithRateLast(MovementNames);

    /// <summary>
    /// FR-022 and FR-028: Given <c>ScenarioCatalog.Get(name)</c>, When it is built at
    /// <paramref name="hz"/>, Then the stream's <c>StepHz</c> and its configuration's <c>stepRateHz</c>
    /// are <paramref name="hz"/>, every expected hash is 0 and it has the definition's duration in seconds
    /// × <paramref name="hz"/> steps; and When it is built at its own rate and run through
    /// <see cref="StreamRunner"/>, Then its rate, character count, seed, match salt, collision checksum,
    /// configuration entries, initial state and every step's commands equal the committed stream's, and
    /// the run's per-step hashes equal the committed expected hashes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void EachMovementDefinitionBuildsAtEveryRateAndMatchesItsCommittedStream(string name, int hz)
    {
        var definition = ScenarioCatalog.Get(name);
        var label = $"{name} at {hz} Hz";

        // Built at hz: the rate is the one asked for, nothing is recorded, and the duration is kept in seconds.
        var atRate = definition.Build(TestConfigs.Default(), TestWorlds.TestWorld, hz);
        Assert.True(atRate.StepHz == hz, $"{label}: the stream's stepHz is {atRate.StepHz}");
        Assert.True(atRate.Config.StepRateHz == hz, $"{label}: the stream's configuration has stepRateHz {atRate.Config.StepRateHz}");
        for (var i = 0; i < atRate.Steps.Count; i++)
        {
            Assert.True(
                atRate.Steps[i].ExpectedHash == 0UL,
                $"{label}: steps[{i}].expectedHash is {atRate.Steps[i].ExpectedHash:x16}; an unrecorded stream's are 0");
        }

        double durationSeconds = definition.DurationSeconds;
        var stepsInDuration = durationSeconds * hz;
        Assert.True(
            stepsInDuration == System.Math.Floor(stepsInDuration) && stepsInDuration >= 1,
            $"{label}: a duration of {durationSeconds:R} s is not a whole number of steps at {hz} Hz");
        Assert.True(
            atRate.Steps.Count == (int)stepsInDuration,
            $"{label}: {durationSeconds:R} s at {hz} Hz is {stepsInDuration:R} steps, but the stream has {atRate.Steps.Count}");

        // Built at its own rate: the committed stream less its expected hashes, which the run reproduces.
        var built = definition.Build(TestConfigs.Default(), TestWorlds.TestWorld);
        var committed = RecordedStream.Decode(RepoPaths.ReadAllBytes("simcore", "streams", $"{name}.mmrs"));
        var file = $"simcore/streams/{name}.mmrs";

        Assert.True(built.StepHz == committed.StepHz, $"{name}: stepHz {built.StepHz}; {file} has {committed.StepHz}");
        Assert.True(
            built.CharacterCount == committed.CharacterCount,
            $"{name}: characterCount {built.CharacterCount}; {file} has {committed.CharacterCount}");
        Assert.True(built.Seed == committed.Seed, $"{name}: seed {built.Seed}; {file} has {committed.Seed}");
        Assert.True(
            built.MatchSalt == committed.MatchSalt,
            $"{name}: matchSalt {built.MatchSalt:x16}; {file} has {committed.MatchSalt:x16}");
        Assert.True(
            built.CollisionChecksum == committed.CollisionChecksum,
            $"{name}: collisionChecksum {built.CollisionChecksum:x16}; {file} has {committed.CollisionChecksum:x16}");
        Assert.True(
            ConfigBlock.Encode(built.Config).AsSpan().SequenceEqual(ConfigBlock.Encode(committed.Config)),
            $"{name}: the configuration entries differ from {file}'s");
        Assert.True(
            built.InitialState.Span.SequenceEqual(committed.InitialState.Span),
            $"{name}: the initial state differs from {file}'s");
        Assert.True(
            built.Steps.Count == committed.Steps.Count,
            $"{name}: {built.Steps.Count} steps; {file} has {committed.Steps.Count}");
        for (var i = 0; i < committed.Steps.Count; i++)
        {
            Assert.True(
                built.Steps[i].Step == committed.Steps[i].Step,
                $"{name}: steps[{i}].step is {built.Steps[i].Step}; {file} has {committed.Steps[i].Step}");
            Assert.True(
                built.Steps[i].Commands.AsSpan().SequenceEqual(committed.Steps[i].Commands),
                $"{name}: steps[{i}] (step {committed.Steps[i].Step}) has commands [{string.Join(", ", built.Steps[i].Commands)}]; {file} has [{string.Join(", ", committed.Steps[i].Commands)}]");
        }

        var hashes = RecordHashes(StreamRunner.Run(built.Encode(), new[] { TestWorlds.TestWorld }), name);
        Assert.True(
            hashes.Count == committed.Steps.Count,
            $"{name}: the run gave {hashes.Count} records for {committed.Steps.Count} steps");
        for (var i = 0; i < hashes.Count; i++)
        {
            Assert.True(
                hashes[i] == committed.Steps[i].ExpectedHash,
                $"{name}: step {committed.Steps[i].Step} hashed {hashes[i]:x16}; {file} expects {committed.Steps[i].ExpectedHash:x16}");
        }
    }

    /// <summary>The hash of every packed step record in <paramref name="records"/>, in order; the records fill it exactly.</summary>
    private static List<ulong> RecordHashes(byte[] records, string name)
    {
        var hashes = new List<ulong>();
        var offset = 0;
        while (offset < records.Length)
        {
            Assert.True(
                StepRecord.TryRead(records.AsSpan(offset), out _, out _, out var hash, out var length),
                $"{name}: the run's records do not parse at byte {offset} of {records.Length}");
            hashes.Add(hash);
            offset += length;
        }

        return hashes;
    }
}
