using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Agreement;

/// <summary>
/// The between-builds divergence report (FR-026, US2 scenario 4, SC-002;
/// contracts/state-serialization.md#divergence-report and contracts/simcore-cli.md#divergence-report).
/// <c>DivergenceReport.CompareBuild(stream, build, serverRecords, buildRecords, expectedHashes)</c> walks the
/// server's and a build's packed step records (<c>u32 step | u32 stateLength | state | u64 hash</c>, back to
/// back) and returns a <c>StreamComparison</c> naming the first divergent step: a missing or unreadable build
/// record, different step numbers, different hashes or state bytes (with the field diff), or a build hash
/// that differs from the stream's expected hash, in that order. The report's lines are
/// <c>DIVERGED stream &lt;s&gt;, build &lt;b&gt;, first divergent step &lt;n&gt;</c>,
/// <c>  hash  server &lt;x16&gt;  &lt;b&gt; &lt;x16|absent&gt;</c>, <c>  1 field differs</c> or
/// <c>  &lt;k&gt; fields differ</c>, then one line per differing field,
/// <c>  &lt;path&gt;  server 0x%08X &lt;decimal&gt;  &lt;b&gt; 0x%08X &lt;decimal&gt;</c>, with <c>absent</c> for an
/// element on one side only and a closing <c>  unreadable from offset &lt;n&gt;</c> for a state that cannot be
/// read to its end. The tests read records with <see cref="BinaryPrimitives"/> at the contract's offsets,
/// independent of <c>StepRecord</c>, and build states with <see cref="TestStates"/>, independent of
/// <c>StateSerializer</c>; every expected bit pattern and decimal is computed here from the value written.
/// A report's lines are split on <c>\n</c>, and one trailing line break, if any, ends the last line rather
/// than starting another. The committed streams and the test world are opened read-only through
/// <see cref="RepoPaths"/>.
/// </summary>
public sealed class DivergenceReportTests
{
    private const string BuildName = "node-interp";

    /// <summary>The stream name given to the reports of states built with <see cref="TestStates"/>.</summary>
    private const string StatesStream = "test-states";

    /// <summary>The step of the states built with <see cref="TestStates"/>.</summary>
    private const uint StatesStep = 4;

    /// <summary>A step record's <c>step</c>, <c>stateLength</c> and <c>hash</c>.</summary>
    private const int RecordOverhead = 16;

    /// <summary><c>characters[0].position.x</c>'s offset in a canonical state: the 12-byte header, then the character's <c>id</c>.</summary>
    private const int PositionXOffset = 16;

    /// <summary><c>characters[0].position.y</c>'s offset in a canonical state.</summary>
    private const int PositionYOffset = 20;

    /// <summary>The length the unreadable build state is cut to: inside <c>characters[0].velocity.x</c> (offsets 28 to 31).</summary>
    private const int CutLength = 30;

    /// <summary>The offset of the first field a 30-byte state cannot hold whole: <c>characters[0].velocity.x</c>.</summary>
    private const int UnreadableOffset = 28;

    /// <summary>The perturbed step of flat-run-30hz: its step at index (30 − 1) / 2.</summary>
    private const uint PerturbedStep = 15;

    /// <summary>The steps of the committed flat-run-30hz stream.</summary>
    private const int FlatRun30StepCount = 30;

    /// <summary>The record count the missing-record build keeps: the server's first 10.</summary>
    private const int KeptRecords = 10;

    /// <summary>The step whose expected hash the expected-hash test alters in jump-land.</summary>
    private const uint AlteredExpectedStep = 12;

    /// <summary>The field line T118 requires for the one-ulp perturbation (simcore-cli.md#divergence-report).</summary>
    private static readonly Regex PositionXLine = new(
        @"^  characters\[0\]\.position\.x +server 0x([0-9A-F]{8}) (\S+) +node-interp 0x([0-9A-F]{8}) (\S+)$",
        RegexOptions.CultureInvariant);

    /// <summary>Any step number a report line names.</summary>
    private static readonly Regex StepNumber = new(@"\bstep (\d+)", RegexOptions.CultureInvariant);

    /// <summary>
    /// The one projectile of <see cref="DifferentCountsListAbsentElements"/>: ID 1, owned by character 7,
    /// class 0, launched at step 3, with a distinct value in every float so a field read at the wrong offset
    /// shows.
    /// </summary>
    private static readonly ProjectileRecord Projectile = new()
    {
        Id = 1,
        OwnerId = 7,
        Class = 0,
        LaunchStep = 3,
        Origin = (1.5f, 2.25f, -3.125f),
        LaunchVelocity = (10.5f, -0.75f, 20.0625f),
        Position = (0.1f, 1.5f, 17.0625f),
    };

    /// <summary>
    /// US2 scenario 4 and SC-002: Given the server records <c>StreamRunner.Run(flat-run-30hz, test-world)</c>
    /// and the build records <c>StreamRunner.Run(flat-run-30hz, test-world, perturbStep: 15)</c>, with the
    /// stream's expected hashes, When <c>DivergenceReport.CompareBuild("flat-run-30hz", "node-interp", server,
    /// build, expectedHashes)</c> runs, Then the outcome is <c>Diverged</c> at step 15 with record 15's server
    /// and build hashes and one differing field, and the report is exactly four lines:
    /// <c>DIVERGED stream flat-run-30hz, build node-interp, first divergent step 15</c>,
    /// <c>  hash  server &lt;x16&gt;  node-interp &lt;x16&gt;</c>, <c>  1 field differs</c>, and a
    /// <c>characters[0].position.x</c> line whose build bits are the server bits + 1 (both the record's state
    /// bits at offset 16) and whose decimals are each float's <c>ToString("R", InvariantCulture)</c>, which
    /// parses back to its bits; and no line names a step before 15.
    /// </summary>
    [Fact]
    public void PerturbedRecordsReportTheFirstStepAndField()
    {
        const string name = "flat-run-30hz";
        var stream = ReadStream(name);
        var collision = TestWorldFile();
        var expectedHashes = ExpectedHashes(stream);
        var server = StreamRunner.Run(stream, collision);
        var build = StreamRunner.Run(stream, collision, perturbStep: (int)PerturbedStep);
        var serverRecords = ReadRecords(server, $"{name} server");
        var buildRecords = ReadRecords(build, $"{name} build");
        Assert.True(
            serverRecords.Count == FlatRun30StepCount && buildRecords.Count == FlatRun30StepCount,
            $"{name}: {serverRecords.Count} server and {buildRecords.Count} build records; the stream has {FlatRun30StepCount} steps");
        var serverRecord = serverRecords.Single(record => record.Step == PerturbedStep);
        var buildRecord = buildRecords.Single(record => record.Step == PerturbedStep);

        var comparison = DivergenceReport.CompareBuild(name, BuildName, server, build, expectedHashes);

        Assert.Equal(StreamOutcome.Diverged, comparison.Outcome);
        Assert.Equal((uint?)PerturbedStep, comparison.FirstDivergentStep);
        Assert.Equal((ulong?)serverRecord.Hash, comparison.ServerHash);
        Assert.Equal((ulong?)buildRecord.Hash, comparison.BuildHash);
        Assert.Equal(1, comparison.DifferingFields);
        Assert.Null(comparison.Error);

        var lines = Lines(comparison.Report);
        Assert.True(lines.Length == 4, $"{name}: the report has {lines.Length} lines, not 4:\n{comparison.Report}");
        Assert.Equal("DIVERGED stream flat-run-30hz, build node-interp, first divergent step 15", lines[0]);
        Assert.Equal($"  hash  server {serverRecord.Hash:x16}  node-interp {buildRecord.Hash:x16}", lines[1]);
        Assert.Equal("  1 field differs", lines[2]);

        var match = PositionXLine.Match(lines[3]);
        Assert.True(match.Success, $"{name}: the field line \"{lines[3]}\" does not match {PositionXLine}");
        var serverBits = ParseBits(match.Groups[1].Value);
        var buildBits = ParseBits(match.Groups[3].Value);
        var recordServerBits = BinaryPrimitives.ReadUInt32LittleEndian(serverRecord.State.AsSpan(PositionXOffset));
        var recordBuildBits = BinaryPrimitives.ReadUInt32LittleEndian(buildRecord.State.AsSpan(PositionXOffset));
        Assert.True(
            serverBits == recordServerBits && buildBits == recordBuildBits,
            $"{name}: the line shows 0x{serverBits:X8} and 0x{buildBits:X8}; record 15's states hold 0x{recordServerBits:X8} and 0x{recordBuildBits:X8}");
        Assert.True(buildBits == serverBits + 1u, $"{name}: the build bits 0x{buildBits:X8} are not the server bits 0x{serverBits:X8} + 1");
        AssertRoundTripDecimal(match.Groups[2].Value, serverBits, "the server decimal");
        AssertRoundTripDecimal(match.Groups[4].Value, buildBits, "the build decimal");

        foreach (var line in lines)
        {
            foreach (Match step in StepNumber.Matches(line))
            {
                var number = uint.Parse(step.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                Assert.True(number >= PerturbedStep, $"{name}: the line \"{line}\" names step {number}, before the perturbed step {PerturbedStep}");
            }
        }
    }

    /// <summary>Rows of <see cref="DifferentCountsListAbsentElements"/>: which side holds the element the other lacks.</summary>
    public static TheoryData<string> DifferentCountCases => ["server-has-the-projectile", "build-has-the-projectile", "build-has-fewer-weapons"];

    /// <summary>
    /// FR-026: Given a server state from <see cref="TestStates"/> with one character (ID 7) and one projectile
    /// (ID 1, <c>nextProjectileId</c> 2), and a build state that is the same without the projectile (still
    /// <c>nextProjectileId</c> 2), When the two one-record sequences are compared, Then 14 fields differ: the
    /// report lists <c>projectileCount  server 0x00000001 1  node-interp 0x00000000 0</c>, then each of the
    /// projectile's 13 paths in layout order (<c>projectiles[0].id</c>, <c>.ownerId</c>, <c>.class</c>,
    /// <c>.launchStep</c>, <c>.origin.x</c> to <c>.z</c>, <c>.launchVelocity.x</c> to <c>.z</c>,
    /// <c>.position.x</c> to <c>.z</c>) as <c>server 0x&lt;bits&gt; &lt;decimal&gt;  node-interp absent</c>,
    /// after the summary line <c>  14 fields differ</c>, and nothing else. With the sides swapped
    /// (<c>build-has-the-projectile</c>), the same 14 lines show <c>server absent</c> and the build's values.
    /// With two characters of the placeholder two weapons on the server and the build's first character
    /// holding only the rifle (<c>build-has-fewer-weapons</c>), 12 fields differ:
    /// <c>characters[0].weaponCount</c> (2 against 1) and the launcher's 11 fields of
    /// <c>characters[0].weapons[1]</c> with <c>node-interp absent</c>, while the second character, which follows
    /// at another offset in each state, matches field for field.
    /// </summary>
    [Theory]
    [MemberData(nameof(DifferentCountCases))]
    public void DifferentCountsListAbsentElements(string caseName)
    {
        var character = TestStates.StandingCharacter(7, 0.5f, 0.5f);
        var withProjectile = TestStates.Build(StatesStep, [character], [Projectile]);
        var withoutProjectile = TestStates.Build(StatesStep, [character], [], nextProjectileId: 2);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(withProjectile.AsSpan(8)));
        var projectileValues = new (string Path, string Value)[]
        {
            ("projectiles[0].id", UIntValue(Projectile.Id)),
            ("projectiles[0].ownerId", UIntValue(Projectile.OwnerId)),
            ("projectiles[0].class", UIntValue(Projectile.Class)),
            ("projectiles[0].launchStep", UIntValue(Projectile.LaunchStep)),
            ("projectiles[0].origin.x", FloatValue(Projectile.Origin.X)),
            ("projectiles[0].origin.y", FloatValue(Projectile.Origin.Y)),
            ("projectiles[0].origin.z", FloatValue(Projectile.Origin.Z)),
            ("projectiles[0].launchVelocity.x", FloatValue(Projectile.LaunchVelocity.X)),
            ("projectiles[0].launchVelocity.y", FloatValue(Projectile.LaunchVelocity.Y)),
            ("projectiles[0].launchVelocity.z", FloatValue(Projectile.LaunchVelocity.Z)),
            ("projectiles[0].position.x", FloatValue(Projectile.Position.X)),
            ("projectiles[0].position.y", FloatValue(Projectile.Position.Y)),
            ("projectiles[0].position.z", FloatValue(Projectile.Position.Z)),
        };
        byte[] server;
        byte[] build;
        string summary;
        (string Path, string Server, string Build)[] expectedFields;
        switch (caseName)
        {
            case "server-has-the-projectile":
                (server, build, summary) = (withProjectile, withoutProjectile, "  14 fields differ");
                expectedFields = [("projectileCount", UIntValue(1), UIntValue(0)), .. projectileValues.Select(field => (field.Path, field.Value, "absent"))];
                break;
            case "build-has-the-projectile":
                (server, build, summary) = (withoutProjectile, withProjectile, "  14 fields differ");
                expectedFields = [("projectileCount", UIntValue(0), UIntValue(1)), .. projectileValues.Select(field => (field.Path, "absent", field.Value))];
                break;
            case "build-has-fewer-weapons":
                var second = TestStates.StandingCharacter(9, 4.5f, 0.5f);
                server = TestStates.Build(StatesStep, [character, second]);
                build = TestStates.Build(StatesStep, [character with { Weapons = [TestStates.Rifle] }, second]);
                summary = "  12 fields differ";
                var launcher = TestStates.Launcher;
                expectedFields =
                [
                    ("characters[0].weaponCount", UIntValue(2), UIntValue(1)),
                    ("characters[0].weapons[1].phase", UIntValue(launcher.Phase), "absent"),
                    ("characters[0].weapons[1].flags", UIntValue(launcher.Flags), "absent"),
                    ("characters[0].weapons[1].phaseSteps", UIntValue(launcher.PhaseSteps), "absent"),
                    ("characters[0].weapons[1].fireWait", UIntValue((uint)launcher.FireWait), "absent"),
                    ("characters[0].weapons[1].shotCounter", UIntValue(launcher.ShotCounter), "absent"),
                    ("characters[0].weapons[1].magazine", UIntValue(launcher.Magazine), "absent"),
                    ("characters[0].weapons[1].reserve", UIntValue(launcher.Reserve), "absent"),
                    ("characters[0].weapons[1].heat", UIntValue(launcher.Heat), "absent"),
                    ("characters[0].weapons[1].stepsSinceShot", UIntValue(launcher.StepsSinceShot), "absent"),
                    ("characters[0].weapons[1].recoilPitch", UIntValue((uint)launcher.RecoilPitch), "absent"),
                    ("characters[0].weapons[1].recoilYaw", UIntValue((uint)launcher.RecoilYaw), "absent"),
                ];
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of DifferentCountsListAbsentElements.");
        }

        var comparison = CompareStates(server, build);

        AssertStatesReport(comparison, server, build, summary, expectedFields, unreadableLine: null);
        Assert.Equal(expectedFields.Length, comparison.DifferingFields);
    }

    /// <summary>Rows of <see cref="StateFieldReaderStopsAtTheFirstFieldItCannotHold"/>: where the state is cut.</summary>
    public static TheoryData<string> ReaderStopCases => ["header", "weapon", "projectile", "event-count", "event-record", "whole"];

    /// <summary>
    /// contracts/state-serialization.md#divergence-report ("decoded through the state layout table"): Given a
    /// state of one character with the placeholder two weapons, one projectile, one shot event and one input
    /// rejection event, cut (<c>header</c>) to 6 bytes, (<c>weapon</c>) at its second weapon,
    /// (<c>projectile</c>) at its projectile, (<c>event-count</c>) at the shot count, (<c>event-record</c>) at
    /// the shot record, or (<c>whole</c>) not cut, When <c>StateFieldReader.Read</c> reads it, Then the last
    /// field it reads whole is <c>characterCount</c> at 4, <c>characters[0].weapons[0].recoilYaw</c>,
    /// <c>characters[0].weapons[1].recoilYaw</c>, <c>projectiles[0].position.z</c>,
    /// <c>events.shots.count</c> or <c>events.movementOutcomes.count</c>, each at its layout offset, and its
    /// unreadable offset is the cut (null for the whole state, which also holds
    /// <c>events.shots[0].direction.z</c> and <c>events.inputRejections[0].reason</c> at their offsets).
    /// </summary>
    [Theory]
    [MemberData(nameof(ReaderStopCases))]
    public void StateFieldReaderStopsAtTheFirstFieldItCannotHold(string caseName)
    {
        var state = TestStates.Build(
            StatesStep,
            [TestStates.StandingCharacter(1, 0.5f, 0.5f)],
            [Projectile],
            events: new EventRecords
            {
                Shots = [new ShotRecord(1, 0, 0, Projectile.Id, (0f, 0f, 1f))],
                InputRejections = [new SmallEventRecord(1, 2)],
            });
        var events = TestStates.ProjectileOffset(1, 1);
        var shot = events + 2;
        var rejection = events + 2 + TestStates.ShotLength + 2 + 2 + 2;
        (int? Cut, string LastPath, int LastOffset) row = caseName switch
        {
            "header" => (6, "characterCount", 4),
            "weapon" => (TestStates.WeaponOffset(0, 1), "characters[0].weapons[0].recoilYaw", TestStates.WeaponOffset(0, 0) + 26),
            "projectile" => (TestStates.ProjectileOffset(1, 0), "characters[0].weapons[1].recoilYaw", TestStates.WeaponOffset(0, 1) + 26),
            "event-count" => (events, "projectiles[0].position.z", TestStates.ProjectileOffset(1, 0) + 45),
            "event-record" => (shot, "events.shots.count", events),
            "whole" => (null, "events.movementOutcomes.count", rejection + TestStates.SmallEventLength + 2),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of StateFieldReaderStopsAtTheFirstFieldItCannotHold."),
        };
        Assert.Equal(rejection + TestStates.SmallEventLength + 4, state.Length);

        var reading = StateFieldReader.Read(row.Cut is { } cut ? state.AsSpan(0, cut) : state);

        Assert.Equal(row.Cut, reading.UnreadableOffset);
        var last = reading.Fields[^1];
        Assert.Equal(row.LastPath, last.Path);
        Assert.Equal(row.LastOffset, last.Offset);
        if (row.Cut is null)
        {
            var paths = reading.Fields.ToDictionary(field => field.Path, field => field.Offset, StringComparer.Ordinal);
            Assert.Equal(shot + 21, paths["events.shots[0].direction.z"]);
            Assert.Equal(rejection + 4, paths["events.inputRejections[0].reason"]);
            Assert.Equal(2u, reading.Fields.Single(field => field.Path == "events.inputRejections[0].reason").Bits);
        }
    }

    /// <summary>
    /// contracts/state-serialization.md#field-paths: Given a server state whose one character holds nine
    /// rifles, one past the eight a weapon path index can carry, and a build state with the ninth rifle's
    /// <c>magazine</c> 29, When the two are compared, Then <c>StateFieldReader</c> reads the server state to
    /// its end with <c>characters[0].weapons[8].phase</c> at the ninth weapon's offset, and the report's one
    /// field line is <c>characters[0].weapons[8].magazine  server 0x0000001E 30  node-interp 0x0000001D 29</c>.
    /// </summary>
    [Fact]
    public void WeaponPastTheEighthSlotIsNamedByItsSlot()
    {
        var nine = Enumerable.Repeat(TestStates.Rifle, 9).ToArray();
        var server = TestStates.Build(StatesStep, [TestStates.StandingCharacter(1, 0.5f, 0.5f) with { Weapons = nine }]);
        var build = TestStates.Mutate((byte[])server.Clone(), TestStates.WeaponOffset(0, 8, weaponSlots: 9) + 12, TestStates.U16(29));

        var reading = StateFieldReader.Read(server);
        var comparison = CompareStates(server, build);

        Assert.Null(reading.UnreadableOffset);
        Assert.Equal(TestStates.WeaponOffset(0, 8, weaponSlots: 9), reading.Fields.Single(field => field.Path == "characters[0].weapons[8].phase").Offset);
        AssertStatesReport(
            comparison,
            server,
            build,
            "  1 field differs",
            [("characters[0].weapons[8].magazine", UIntValue(30), UIntValue(29))],
            unreadableLine: null);
    }

    /// <summary>Rows of <see cref="UnreadableStateIsListedToItsOffset"/>.</summary>
    public static TheoryData<string> UnreadableCases => ["cut", "cut-and-changed"];

    /// <summary>
    /// FR-026: Given a server state of one character at step 4 and a build record whose state is that state
    /// cut to 30 bytes, with <c>stateLength</c> 30 (<c>cut</c>), or cut and with
    /// <c>characters[0].position.y</c> changed from 0 to 0.25 (<c>cut-and-changed</c>), When they are compared,
    /// Then the report lists the differing fields the build state holds whole, up to offset 28 (none for
    /// <c>cut</c>; <c>characters[0].position.y  server 0x00000000 0  node-interp 0x3E800000 0.25</c> for
    /// <c>cut-and-changed</c>), then <c>  unreadable from offset 28</c> as its last line, and no field at or
    /// after offset 28. The summary line counts the field lines listed, so it is <c>  0 fields differ</c> and
    /// <c>  1 field differs</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableCases))]
    public void UnreadableStateIsListedToItsOffset(string caseName)
    {
        var server = TestStates.Build(StatesStep, [TestStates.StandingCharacter(1, 0.5f, 0.5f)]);
        var build = server.AsSpan(0, CutLength).ToArray();
        (string Path, string Server, string Build)[] expectedFields;
        string summary;
        switch (caseName)
        {
            case "cut":
                expectedFields = [];
                summary = "  0 fields differ";
                break;

            case "cut-and-changed":
                TestStates.Mutate(build, PositionYOffset, TestStates.F32(0.25f));
                expectedFields = [("characters[0].position.y", FloatValue(0f), FloatValue(0.25f))];
                summary = "  1 field differs";
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of UnreadableStateIsListedToItsOffset.");
        }

        var comparison = CompareStates(server, build);

        AssertStatesReport(
            comparison,
            server,
            build,
            summary,
            expectedFields,
            unreadableLine: $"  unreadable from offset {UnreadableOffset}");
        Assert.Equal(expectedFields.Length, comparison.DifferingFields);
    }

    /// <summary>
    /// Rows <c>(path, offset, littleEndianHex, serverValue)</c> of
    /// <see cref="SignedAndSmallFieldsShowZeroExtendedBits"/>: the bytes written at the field's offset in the
    /// server state, and the value the report shows for them, bits zero-extended to 32 and the decimal signed
    /// or unsigned by the field's type. The build state holds 0 there.
    /// </summary>
    public static TheoryData<string, int, string, string> SmallFieldCases => new()
    {
        { "characters[0].aimPitch", TestStates.CharacterOffset(0) + 42, "FFFF", "0x0000FFFF -1" },
        { "characters[0].aimYaw", TestStates.CharacterOffset(0) + 40, "FFFF", "0x0000FFFF 65535" },
        { "characters[0].stance", TestStates.CharacterOffset(0) + 44, "01", "0x00000001 1" },
        { "characters[0].lastSequence", TestStates.CharacterOffset(0) + 46, "FFFFFFFF", "0xFFFFFFFF 4294967295" },
        { "characters[0].weapons[0].phaseSteps", TestStates.WeaponOffset(0, 0) + 2, "0180", "0x00008001 32769" },
        { "characters[0].weapons[0].fireWait", TestStates.WeaponOffset(0, 0) + 4, "FFFFFFFF", "0xFFFFFFFF -1" },
        { "characters[0].weapons[1].recoilYaw", TestStates.WeaponOffset(0, 1) + 26, "FBFFFFFF", "0xFFFFFFFB -5" },
    };

    /// <summary>
    /// FR-026 (values): Given a server state of one character at step 4 whose field <paramref name="path"/>
    /// holds the row's bytes and a build state holding 0 there, When they are compared, Then the report's one
    /// field line shows the server as <paramref name="serverValue"/> and the build as <c>0x00000000 0</c>: an
    /// <c>i16</c> <c>aimPitch</c> of −1 is <c>0x0000FFFF -1</c> while a <c>u16</c> <c>aimYaw</c> of 65535 with
    /// the same bytes is <c>0x0000FFFF 65535</c>, a <c>u8</c> is zero-extended, and an <c>i32</c> or a
    /// <c>u32</c> keeps its sign rule.
    /// </summary>
    [Theory]
    [MemberData(nameof(SmallFieldCases))]
    public void SignedAndSmallFieldsShowZeroExtendedBits(string path, int offset, string littleEndianHex, string serverValue)
    {
        var build = TestStates.Build(StatesStep, [TestStates.StandingCharacter(1, 0.5f, 0.5f)]);
        var server = TestStates.Mutate((byte[])build.Clone(), offset, Convert.FromHexString(littleEndianHex));

        var comparison = CompareStates(server, build);

        AssertStatesReport(
            comparison,
            server,
            build,
            "  1 field differs",
            [(path, serverValue, UIntValue(0))],
            unreadableLine: null);
        Assert.Equal(1, comparison.DifferingFields);
    }

    /// <summary>
    /// Given the server records of flat-run-30hz (30 steps) and build records that are the server's first 10,
    /// When they are compared, Then the first divergent step is the 11th step's number, the server hash is
    /// its record's hash and the build hash is null, and the report starts
    /// <c>DIVERGED stream flat-run-30hz, build node-interp, first divergent step &lt;n&gt;</c> and
    /// <c>  hash  server &lt;x16&gt;  node-interp absent</c>.
    /// </summary>
    [Fact]
    public void MissingBuildRecordIsTheFirstDivergentStep()
    {
        const string name = "flat-run-30hz";
        var stream = ReadStream(name);
        var server = StreamRunner.Run(stream, TestWorldFile());
        var serverRecords = ReadRecords(server, $"{name} server");
        Assert.True(serverRecords.Count == FlatRun30StepCount, $"{name}: {serverRecords.Count} server records, not {FlatRun30StepCount}");
        var lastKept = serverRecords[KeptRecords - 1];
        var build = server.AsSpan(0, lastKept.Start + lastKept.Length).ToArray();
        var missing = serverRecords[KeptRecords];

        var comparison = DivergenceReport.CompareBuild(name, BuildName, server, build, ExpectedHashes(stream));

        Assert.Equal(StreamOutcome.Diverged, comparison.Outcome);
        Assert.Equal((uint?)missing.Step, comparison.FirstDivergentStep);
        Assert.Equal((ulong?)missing.Hash, comparison.ServerHash);
        Assert.Null(comparison.BuildHash);
        var lines = Lines(comparison.Report);
        Assert.True(lines.Length >= 2, $"{name}: the report has {lines.Length} lines:\n{comparison.Report}");
        Assert.Equal($"DIVERGED stream flat-run-30hz, build node-interp, first divergent step {missing.Step}", lines[0]);
        Assert.Equal($"  hash  server {missing.Hash:x16}  node-interp absent", lines[1]);
    }

    /// <summary>
    /// Given a build that wrote an error file for flat-run-30hz, When
    /// <c>DivergenceReport.BuildError("flat-run-30hz", "chromium-worker", firstStep, "boom")</c> reports it with
    /// the stream's first step number, Then the outcome is <c>Diverged</c> at that step with
    /// <c>Error == "boom"</c>, the report starts
    /// <c>DIVERGED stream flat-run-30hz, build chromium-worker, first divergent step &lt;first&gt;</c> and has
    /// the line <c>  error  boom</c>.
    /// </summary>
    [Fact]
    public void BuildErrorIsReportedWithItsMessage()
    {
        var firstStep = RecordedStream.Decode(ReadStream("flat-run-30hz")).Steps[0].Step;

        var comparison = DivergenceReport.BuildError("flat-run-30hz", "chromium-worker", firstStep, "boom");

        Assert.Equal(StreamOutcome.Diverged, comparison.Outcome);
        Assert.Equal((uint?)firstStep, comparison.FirstDivergentStep);
        Assert.Equal("boom", comparison.Error);
        var lines = Lines(comparison.Report);
        Assert.True(lines.Length >= 1, $"the report is empty: \"{comparison.Report}\"");
        Assert.Equal($"DIVERGED stream flat-run-30hz, build chromium-worker, first divergent step {firstStep}", lines[0]);
        Assert.Contains("  error  boom", lines);
    }

    /// <summary>
    /// Given the server records of the committed jump-land stream, build records equal to them, and the
    /// stream's expected hashes with step 12's altered, When they are compared, Then the outcome is
    /// <c>Diverged</c> at step 12 with no differing field and the build's record hash, and the report is
    /// exactly <c>DIVERGED stream jump-land, build node-interp, first divergent step 12, against the expected hashes</c>
    /// and <c>  hash  expected &lt;altered x16&gt;  node-interp &lt;record x16&gt;</c>: a stream stores
    /// hashes, not states, so the report names the step and both hashes but no fields.
    /// </summary>
    [Fact]
    public void ExpectedHashDivergenceNamesTheStepAndBothHashes()
    {
        const string name = "jump-land";
        var stream = ReadStream(name);
        var server = StreamRunner.Run(stream, TestWorldFile());
        var build = (byte[])server.Clone();
        var records = ReadRecords(server, $"{name} server");
        var expectedHashes = ExpectedHashes(stream);
        var index = records.FindIndex(record => record.Step == AlteredExpectedStep);
        Assert.True(index >= 0, $"{name} has no step {AlteredExpectedStep}");
        Assert.True(
            records.Count == expectedHashes.Length && expectedHashes[index] == records[index].Hash,
            $"{name}: {records.Count} records and {expectedHashes.Length} expected hashes; step {AlteredExpectedStep} must match before it is altered");
        var altered = expectedHashes[index] ^ 0x8000000000000001UL;
        expectedHashes[index] = altered;

        var comparison = DivergenceReport.CompareBuild(name, BuildName, server, build, expectedHashes);

        Assert.Equal(StreamOutcome.Diverged, comparison.Outcome);
        Assert.Equal((uint?)AlteredExpectedStep, comparison.FirstDivergentStep);
        Assert.Equal((ulong?)records[index].Hash, comparison.BuildHash);
        Assert.Equal(0, comparison.DifferingFields);
        Assert.Equal(
            new[]
            {
                "DIVERGED stream jump-land, build node-interp, first divergent step 12, against the expected hashes",
                $"  hash  expected {altered:x16}  node-interp {records[index].Hash:x16}",
            },
            Lines(comparison.Report));
    }

    /// <summary>Rows <c>(caseName, expectedLine)</c> of <see cref="NotComparableLineNamesBothValues"/>.</summary>
    public static TheoryData<string, string> NotComparableCases => new()
    {
        { "version", "NOT COMPARABLE stream jump-land: unsupported version 2; this reader supports version 1" },
        { "rules-revision", "NOT COMPARABLE stream jump-land: rules revision 2 in the stream; this core implements rules revision 1" },
    };

    /// <summary>
    /// US2 scenario 5: Given the refusal of a stream in another format version (the error
    /// <c>RecordedStream.Decode</c> throws for jump-land with the u16 at offset 4 set to 2) or another rules
    /// revision (the <c>RulesRevisionMismatch</c> error at offset 8, field <c>rulesRevision</c>, that
    /// <c>StreamRunner</c> throws for revision 2), When <c>DivergenceReport.NotComparable("jump-land", error)</c>
    /// formats it, Then it is the one line <c>NOT COMPARABLE stream jump-land: &lt;detail&gt;</c>, naming both
    /// values and no step.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotComparableCases))]
    public void NotComparableLineNamesBothValues(string caseName, string expectedLine)
    {
        FormatError error;
        switch (caseName)
        {
            case "version":
                {
                    var copy = ReadStream("jump-land");
                    BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(4), 2);
                    error = Assert.Throws<CoreFormatException>(() => RecordedStream.Decode(copy)).Error;
                    Assert.Equal(FormatErrorCode.UnsupportedVersion, error.Code);
                    break;
                }

            case "rules-revision":
                error = new FormatError(
                    "stream",
                    FormatErrorCode.RulesRevisionMismatch,
                    8,
                    "rulesRevision",
                    "rules revision 2 in the stream; this core implements rules revision 1");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of NotComparableLineNamesBothValues.");
        }

        Assert.Equal(expectedLine, DivergenceReport.NotComparable("jump-land", error));
    }

    /// <summary>
    /// Given the server records of the committed flat-run-&lt;hz&gt;hz stream and build records equal to them,
    /// with the stream's expected hashes, When they are compared, Then the outcome is <c>Match</c> with no
    /// divergent step, no differing field, no error and an empty report.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void EqualRecordsMatchWithAnEmptyReport(int hz)
    {
        var name = $"flat-run-{hz}hz";
        var stream = ReadStream(name);
        var server = StreamRunner.Run(stream, TestWorldFile());
        var build = (byte[])server.Clone();

        var comparison = DivergenceReport.CompareBuild(name, BuildName, server, build, ExpectedHashes(stream));

        Assert.Equal(StreamOutcome.Match, comparison.Outcome);
        Assert.Null(comparison.FirstDivergentStep);
        Assert.Equal(0, comparison.DifferingFields);
        Assert.Null(comparison.Error);
        Assert.Equal(string.Empty, comparison.Report);
    }

    /// <summary>
    /// contracts/state-serialization.md#divergence-report (a record's own step number): Given a server record
    /// of a state at step 4 and a build record of the same state and hash numbered step 5, When they are
    /// compared, Then the outcome is <c>Diverged</c> at the server's step 4 with both (equal) hashes and no
    /// differing field, and the report is exactly the header, the hash line,
    /// <c>  record step  server 4  node-interp 5</c> and <c>  0 fields differ</c>.
    /// </summary>
    [Fact]
    public void DifferentRecordStepNumbersAreListed()
    {
        var state = TestStates.Build(StatesStep, [TestStates.StandingCharacter(1, 0.5f, 0.5f)]);
        var hash = StateHash.Compute(state);

        var comparison = DivergenceReport.CompareBuild(StatesStream, BuildName, Record(StatesStep, state), Record(StatesStep + 1, state), new[] { hash });

        Assert.Equal(StreamOutcome.Diverged, comparison.Outcome);
        Assert.Equal((uint?)StatesStep, comparison.FirstDivergentStep);
        Assert.Equal((ulong?)hash, comparison.ServerHash);
        Assert.Equal((ulong?)hash, comparison.BuildHash);
        Assert.Equal(0, comparison.DifferingFields);
        Assert.Equal(
            new[]
            {
                $"DIVERGED stream {StatesStream}, build {BuildName}, first divergent step {StatesStep}",
                $"  hash  server {hash:x16}  {BuildName} {hash:x16}",
                $"  record step  server {StatesStep}  {BuildName} {StatesStep + 1}",
                "  0 fields differ",
            },
            Lines(comparison.Report));
    }

    /// <summary>
    /// contracts/state-serialization.md#divergence-report (one side has no record): Given the server records
    /// of flat-run-30hz and build records that are the same followed by one more record, numbered the last
    /// step + 1, When they are compared, Then the first divergent step is that extra step, the server hash is
    /// null and the build hash is the extra record's, and the report is exactly
    /// <c>DIVERGED stream flat-run-30hz, build node-interp, first divergent step &lt;n&gt;</c> and
    /// <c>  hash  server absent  node-interp &lt;x16&gt;</c>.
    /// </summary>
    [Fact]
    public void ExtraBuildRecordIsTheFirstDivergentStep()
    {
        const string name = "flat-run-30hz";
        var stream = ReadStream(name);
        var server = StreamRunner.Run(stream, TestWorldFile());
        var last = ReadRecords(server, $"{name} server")[^1];
        var extraStep = last.Step + 1;
        var build = server.Concat(Record(extraStep, last.State)).ToArray();

        var comparison = DivergenceReport.CompareBuild(name, BuildName, server, build, ExpectedHashes(stream));

        Assert.Equal(StreamOutcome.Diverged, comparison.Outcome);
        Assert.Equal((uint?)extraStep, comparison.FirstDivergentStep);
        Assert.Null(comparison.ServerHash);
        Assert.Equal((ulong?)last.Hash, comparison.BuildHash);
        Assert.Equal(
            new[]
            {
                $"DIVERGED stream {name}, build {BuildName}, first divergent step {extraStep}",
                $"  hash  server absent  {BuildName} {last.Hash:x16}",
            },
            Lines(comparison.Report));
    }

    /// <summary>Rows of <see cref="RefusesServerRecordsThatAreNotOnePerExpectedHash"/>.</summary>
    public static TheoryData<string> InconsistentServerRecordCases => ["server-cut", "one-hash-more"];

    /// <summary>
    /// Given the server records of flat-run-30hz and the stream's 30 expected hashes, changed per row
    /// (<c>server-cut</c>: the records 3 bytes short; <c>one-hash-more</c>: a 31st expected hash), When a build
    /// equal to the unchanged server records is compared, Then <c>CompareBuild</c> throws an
    /// <see cref="ArgumentException"/> for <c>serverRecords</c> whose message is exactly
    /// <c>The server records of flat-run-30hz hold &lt;n&gt; whole records and &lt;m&gt; more bytes; the stream has &lt;k&gt; expected hashes.</c>,
    /// instead of comparing a build against them.
    /// </summary>
    [Theory]
    [MemberData(nameof(InconsistentServerRecordCases))]
    public void RefusesServerRecordsThatAreNotOnePerExpectedHash(string caseName)
    {
        const string name = "flat-run-30hz";
        var stream = ReadStream(name);
        var records = StreamRunner.Run(stream, TestWorldFile());
        var lastLength = RecordOverhead + ReadRecords(records, $"{name} server")[^1].State.Length;
        var (server, hashes, whole, remaining) = caseName switch
        {
            "server-cut" => (records[..^3], ExpectedHashes(stream), FlatRun30StepCount - 1, lastLength - 3),
            "one-hash-more" => (records, [.. ExpectedHashes(stream), 0UL], FlatRun30StepCount, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Not a row of RefusesServerRecordsThatAreNotOnePerExpectedHash."),
        };

        StreamComparison? comparison = null;
        var thrown = Assert.Throws<ArgumentException>(() => comparison = DivergenceReport.CompareBuild(name, BuildName, server, records, hashes));

        Assert.Null(comparison);
        Assert.Equal("serverRecords", thrown.ParamName);
        Assert.StartsWith(
            string.Create(
                CultureInfo.InvariantCulture,
                $"The server records of {name} hold {whole} whole records and {remaining} more bytes; the stream has {hashes.Length} expected hashes."),
            thrown.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// contracts/state-serialization.md#divergence-report (a state that cannot be read to its end): Given a
    /// server state of one character at step 4 and a build state that is the same followed by three more
    /// bytes, When they are compared, Then every field of the layout matches, so the report is the header,
    /// the hash line, <c>  0 fields differ</c> and <c>  unreadable from offset &lt;server length&gt;</c>, where
    /// the bytes left after the last event list start.
    /// </summary>
    [Fact]
    public void BytesAfterTheLastEventListAreUnreadable()
    {
        var server = TestStates.Build(StatesStep, [TestStates.StandingCharacter(1, 0.5f, 0.5f)]);
        var build = server.Concat(new byte[] { 0xAA, 0xBB, 0xCC }).ToArray();

        var comparison = CompareStates(server, build);

        AssertStatesReport(comparison, server, build, "  0 fields differ", [], unreadableLine: $"  unreadable from offset {server.Length}");
        Assert.Equal(0, comparison.DifferingFields);
    }

    /// <summary>
    /// Compares one server record and one build record, each <c>(state's step, state length, state,
    /// StateHash.Compute(state))</c>, with the server's hash as the one expected hash.
    /// </summary>
    private static StreamComparison CompareStates(byte[] server, byte[] build)
    {
        var serverRecords = Record(StatesStep, server);
        var buildRecords = Record(StatesStep, build);
        return DivergenceReport.CompareBuild(StatesStream, BuildName, serverRecords, buildRecords, new[] { StateHash.Compute(server) });
    }

    /// <summary>
    /// Asserts a field-diff report for <see cref="CompareStates"/>: <c>Diverged</c> at step 4 with both
    /// states' hashes; then exactly the header, the hash line, <paramref name="summary"/>, one line per
    /// expected field in order, and <paramref name="unreadableLine"/> when it is given.
    /// </summary>
    private static void AssertStatesReport(
        StreamComparison comparison,
        byte[] server,
        byte[] build,
        string summary,
        IReadOnlyList<(string Path, string Server, string Build)> expectedFields,
        string? unreadableLine)
    {
        var serverHash = StateHash.Compute(server);
        var buildHash = StateHash.Compute(build);
        Assert.Equal(StreamOutcome.Diverged, comparison.Outcome);
        Assert.Equal((uint?)StatesStep, comparison.FirstDivergentStep);
        Assert.Equal((ulong?)serverHash, comparison.ServerHash);
        Assert.Equal((ulong?)buildHash, comparison.BuildHash);
        Assert.Null(comparison.Error);

        var lines = Lines(comparison.Report);
        var expectedCount = 3 + expectedFields.Count + (unreadableLine is null ? 0 : 1);
        Assert.True(lines.Length == expectedCount, $"the report has {lines.Length} lines, not {expectedCount}:\n{comparison.Report}");
        Assert.Equal($"DIVERGED stream {StatesStream}, build {BuildName}, first divergent step {StatesStep}", lines[0]);
        Assert.Equal($"  hash  server {serverHash:x16}  {BuildName} {buildHash:x16}", lines[1]);
        Assert.Equal(summary, lines[2]);
        for (var i = 0; i < expectedFields.Count; i++)
        {
            var (path, serverValue, buildValue) = expectedFields[i];
            var pattern = $"^  {Regex.Escape(path)} +server {Regex.Escape(serverValue)} +{Regex.Escape(BuildName)} {Regex.Escape(buildValue)}$";
            Assert.True(
                Regex.IsMatch(lines[3 + i], pattern, RegexOptions.CultureInvariant),
                $"field line {i} is \"{lines[3 + i]}\", not \"  {path}  server {serverValue}  {BuildName} {buildValue}\"");
        }

        if (unreadableLine is not null)
        {
            Assert.Equal(unreadableLine, lines[^1]);
        }
    }

    /// <summary>One packed step record: <c>u32 step | u32 stateLength | state | u64 hash</c>, the hash being <c>StateHash.Compute(state)</c>.</summary>
    private static byte[] Record(uint step, byte[] state)
    {
        var record = new byte[RecordOverhead + state.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, step);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)state.Length);
        state.CopyTo(record.AsSpan(8));
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8 + state.Length), StateHash.Compute(state));
        return record;
    }

    /// <summary>
    /// Splits packed step records with <see cref="BinaryPrimitives"/> at the contract's offsets, requiring them
    /// to fill the bytes exactly.
    /// </summary>
    private static List<PackedRecord> ReadRecords(byte[] records, string what)
    {
        var list = new List<PackedRecord>();
        var offset = 0;
        while (offset < records.Length)
        {
            Assert.True(records.Length - offset >= RecordOverhead, $"{what}: only {records.Length - offset} bytes are left at byte {offset}");
            var step = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset));
            var stateLength = BinaryPrimitives.ReadUInt32LittleEndian(records.AsSpan(offset + 4));
            Assert.True(
                RecordOverhead + (long)stateLength <= records.Length - offset,
                $"{what}: the record at byte {offset} has a {stateLength}-byte state, past the {records.Length}-byte output");
            var length = RecordOverhead + (int)stateLength;
            var state = records.AsSpan(offset + 8, (int)stateLength).ToArray();
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(records.AsSpan(offset + 8 + (int)stateLength));
            list.Add(new PackedRecord(step, state, hash, offset, length));
            offset += length;
        }

        return list;
    }

    /// <summary>The report's lines, split on <c>\n</c>; one trailing line break ends the last line.</summary>
    private static string[] Lines(string report)
    {
        var lines = report.Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>The committed expected hashes of an encoded stream, in step order.</summary>
    private static ulong[] ExpectedHashes(byte[] stream) =>
        RecordedStream.Decode(stream).Steps.Select(step => step.ExpectedHash).ToArray();

    /// <summary>A committed stream's bytes, read-only: <c>simcore/streams/&lt;name&gt;.mmrs</c>.</summary>
    private static byte[] ReadStream(string name) => RepoPaths.ReadAllBytes("simcore", "streams", $"{name}.mmrs");

    /// <summary>The committed test world's collision file, read-only.</summary>
    private static byte[] TestWorldFile() => RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd");

    /// <summary>A float as the report shows it: its bits as <c>0x%08X</c> and its round-trip decimal.</summary>
    private static string FloatValue(float value) => string.Create(
        CultureInfo.InvariantCulture,
        $"0x{BitConverter.SingleToUInt32Bits(value):X8} {value.ToString("R", CultureInfo.InvariantCulture)}");

    /// <summary>An unsigned integer as the report shows it: its bits as <c>0x%08X</c> and its decimal.</summary>
    private static string UIntValue(uint value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:X8} {value}");

    private static uint ParseBits(string hex) => uint.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

    /// <summary>Asserts that a report decimal is the float's "R" form and parses back to the same bits.</summary>
    private static void AssertRoundTripDecimal(string text, uint bits, string what)
    {
        var value = BitConverter.UInt32BitsToSingle(bits);
        Assert.True(
            text == value.ToString("R", CultureInfo.InvariantCulture),
            $"{what} \"{text}\" is not 0x{bits:X8}'s round-trip form {value.ToString("R", CultureInfo.InvariantCulture)}");
        var parsed = float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        Assert.True(
            BitConverter.SingleToUInt32Bits(parsed) == bits,
            $"{what} \"{text}\" parses to 0x{BitConverter.SingleToUInt32Bits(parsed):X8}, not 0x{bits:X8}");
    }

    /// <summary>One packed step record: its step, state and hash, and where it lies in its sequence.</summary>
    private sealed record PackedRecord(uint Step, byte[] State, ulong Hash, int Start, int Length);
}
