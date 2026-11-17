using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The rules of <see cref="ScenarioDefinition"/> itself (contracts/simcore-cli.md#named-scenario-streams,
/// #streams-record): a definition has at least one character, unique character IDs and a positive duration,
/// and keeps its characters in ascending ID; it builds only a whole number of steps at the rate asked, over
/// a configuration whose weapon slots name its weapons, from a state the serializer accepts; and
/// <see cref="ScenarioDefinition.Record"/> and <see cref="ScenarioDefinition.WithExpectedHashes"/> take the
/// expected hashes from a run whose records match the steps one for one. Every stream is built over
/// <see cref="TestWorlds.TestWorld"/> with <see cref="TestConfigs.Default"/>; the committed stream is read
/// through <see cref="RepoPaths"/>, read-only.
/// </summary>
public sealed class ScenarioDefinitionTests
{
    private const string FlatRun30 = "flat-run-30hz";

    /// <summary>Rows of <see cref="ConstructorRefusesAnInvalidDefinition"/>.</summary>
    public static TheoryData<string> InvalidDefinitionCases => ["no-characters", "zero-duration", "repeated-id"];

    /// <summary>Rows of <see cref="BuildRefusesWhatCannotBecomeAStream"/>.</summary>
    public static TheoryData<string> InvalidBuildCases => ["fractional-steps", "too-few-weapons", "non-finite-position"];

    /// <summary>Rows of <see cref="WithExpectedHashesRefusesRecordsThatDoNotMatchTheSteps"/>.</summary>
    public static TheoryData<string> MismatchedRecordsCases => ["missing-record", "extra-bytes"];

    /// <summary>
    /// Given (<c>no-characters</c>) no characters, (<c>zero-duration</c>) a duration of 0 s, or
    /// (<c>repeated-id</c>) two characters with ID 4, When a <see cref="ScenarioDefinition"/> is constructed,
    /// Then it throws an <see cref="ArgumentException"/> for <c>characters</c>, <c>durationSeconds</c> or
    /// <c>characters</c>, with the messages <c>A scenario has at least one character.</c>,
    /// <c>A scenario's duration is positive.</c> and <c>Character ID 4 repeats.</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidDefinitionCases))]
    public void ConstructorRefusesAnInvalidDefinition(string caseName)
    {
        var (characters, duration, parameter, message) = caseName switch
        {
            "no-characters" => (Array.Empty<ScenarioCharacter>(), 1.0, "characters", "A scenario has at least one character."),
            "zero-duration" => (new[] { Character(1) }, 0.0, "durationSeconds", "A scenario's duration is positive."),
            "repeated-id" => (new[] { Character(4), Character(2), Character(4) }, 1.0, "characters", "Character ID 4 repeats."),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of ConstructorRefusesAnInvalidDefinition."),
        };

        ScenarioDefinition? definition = null;
        var thrown = Assert.Throws<ArgumentException>(() => definition = new ScenarioDefinition("invalid", 30, duration, characters));

        Assert.Null(definition);
        Assert.Equal(parameter, thrown.ParamName);
        Assert.StartsWith(message, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Given characters with IDs 3, 1 and 2, When a definition is constructed, Then its
    /// <see cref="ScenarioDefinition.Characters"/> are IDs 1, 2 and 3, and the stream it builds has their
    /// commands at character indexes 0, 1 and 2 of every step, in that order.
    /// </summary>
    [Fact]
    public void CharactersAreKeptInAscendingId()
    {
        var definition = new ScenarioDefinition("sorted", 30, 1.0, [Character(3), Character(1), Character(2)]);

        var stream = definition.Build(TestConfigs.Default(), TestWorlds.TestWorld);

        Assert.Equal([1u, 2u, 3u], definition.Characters.Select(character => character.Id));
        Assert.All(stream.Steps, step => Assert.Equal([(byte)0, (byte)1, (byte)2], step.Commands.Select(command => command.CharacterIndex)));
    }

    /// <summary>
    /// Given (<c>fractional-steps</c>) a 0.05 s definition built at 30 Hz, 1.5 steps;
    /// (<c>too-few-weapons</c>) a configuration with <c>input.weaponSlots</c> one more than its weapons; or
    /// (<c>non-finite-position</c>) a character at x = NaN, When the definition is built, Then it throws an
    /// <see cref="ArgumentException"/> for <c>rate</c> (<c>odd lasts 0.05 s, which is not a whole number of
    /// steps at 30 Hz.</c>), an <see cref="ArgumentException"/> for <c>config</c>
    /// (<c>input.weaponSlots &lt;n + 1&gt; does not name 1 to &lt;n&gt; weapons.</c>), or an
    /// <see cref="InvalidOperationException"/> (<c>odd's initial state does not serialise: NonFinite at characters[0].position.x.</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidBuildCases))]
    public void BuildRefusesWhatCannotBecomeAStream(string caseName)
    {
        var config = TestConfigs.Default();
        var weapons = config.Weapons!.Count;
        Exception thrown;
        string message;
        switch (caseName)
        {
            case "fractional-steps":
                var odd = new ScenarioDefinition("odd", 20, 0.05, [Character(1)]);
                thrown = Assert.Throws<ArgumentException>(() => odd.Build(config, TestWorlds.TestWorld, 30));
                Assert.Equal("rate", ((ArgumentException)thrown).ParamName);
                message = "odd lasts 0.05 s, which is not a whole number of steps at 30 Hz.";
                break;
            case "too-few-weapons":
                config.Input!.WeaponSlots = weapons + 1;
                var slots = new ScenarioDefinition("odd", 30, 1.0, [Character(1)]);
                thrown = Assert.Throws<ArgumentException>(() => slots.Build(config, TestWorlds.TestWorld));
                Assert.Equal("config", ((ArgumentException)thrown).ParamName);
                message = $"input.weaponSlots {weapons + 1} does not name 1 to {weapons} weapons.";
                break;
            case "non-finite-position":
                var nan = new ScenarioDefinition("odd", 30, 1.0, [new ScenarioCharacter(1, new Vec3(float.NaN, 0f, 0f), 0, _ => default)]);
                thrown = Assert.Throws<InvalidOperationException>(() => nan.Build(config, TestWorlds.TestWorld));
                message = "odd's initial state does not serialise: NonFinite at characters[0].position.x.";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of BuildRefusesWhatCannotBecomeAStream.");
        }

        Assert.StartsWith(message, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// FR-028: Given the flat-run-30hz definition, When it is recorded with the default configuration over the
    /// test world, Then the recorded stream encodes to exactly the committed <c>simcore/streams/flat-run-30hz.mmrs</c>.
    /// </summary>
    [Fact]
    public void RecordGivesTheCommittedStream()
    {
        var recorded = ScenarioCatalog.Get(FlatRun30).Record(TestConfigs.Default(), TestWorlds.TestWorld);

        Assert.Equal(RepoPaths.ReadAllBytes("simcore", "streams", $"{FlatRun30}.mmrs"), recorded.Encode());
    }

    /// <summary>
    /// Given the unrecorded flat-run-30hz stream and the records of its run, with (<c>missing-record</c>) the
    /// last record cut off or (<c>extra-bytes</c>) one byte appended, When
    /// <see cref="ScenarioDefinition.WithExpectedHashes"/> takes the hashes from them, Then it throws an
    /// <see cref="ArgumentException"/> for <c>records</c>: <c>The records hold no record of step 30 at byte &lt;offset&gt;.</c>
    /// or <c>The records run 1 bytes past the stream's 30 steps.</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(MismatchedRecordsCases))]
    public void WithExpectedHashesRefusesRecordsThatDoNotMatchTheSteps(string caseName)
    {
        var built = ScenarioCatalog.Get(FlatRun30).Build(TestConfigs.Default(), TestWorlds.TestWorld);
        var records = StreamRunner.Run(built.Encode(), [TestWorlds.TestWorld]);
        var recordLength = records.Length / built.Steps.Count;
        var (changed, message) = caseName switch
        {
            "missing-record" => (records[..^recordLength], $"The records hold no record of step {built.Steps[^1].Step} at byte {records.Length - recordLength}."),
            "extra-bytes" => ([.. records, (byte)0], $"The records run 1 bytes past the stream's {built.Steps.Count} steps."),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of WithExpectedHashesRefusesRecordsThatDoNotMatchTheSteps."),
        };
        Assert.Equal(0, records.Length % built.Steps.Count);

        var thrown = Assert.Throws<ArgumentException>(() => ScenarioDefinition.WithExpectedHashes(built, changed));

        Assert.Equal("records", thrown.ParamName);
        Assert.StartsWith(message, thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A character standing at the flat-run start, holding nothing.</summary>
    private static ScenarioCharacter Character(uint id) => new(id, TestWorldGenerator.Features.FlatRunStart, 0, _ => default(Buttons));
}
