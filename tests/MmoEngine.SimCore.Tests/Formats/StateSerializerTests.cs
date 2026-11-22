using System.Globalization;
using CsCheck;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hashing;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Inputs;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;
using Xunit.Sdk;

namespace MmoEngine.SimCore.Tests.Formats;

/// <summary>
/// The canonical state, serialisation version 1 (FR-016, FR-017, contracts/state-serialization.md):
/// the worked example's bytes and hash, the maximum length, the writer's canonicalisation of the
/// event floats (−0 written and committed as +0, the first non-finite float refusing the write with its
/// path), and every byte-only refusal of the two readers.
/// <para>
/// Every expected byte comes from <see cref="TestStates.Build"/>, which writes the contract's offsets
/// with <c>BinaryPrimitives</c> independently of <see cref="StateSerializer"/>. The writer is called
/// with the world's spans: the header values, the characters, their weapons flat by
/// character index × <c>weaponSlots</c> + slot, the projectiles and the six event lists in
/// serialisation order. It writes <c>weaponCount</c> as <c>weaponSlots</c> (equal to each
/// character's <c>WeaponCount</c> in every state here), returns <see cref="StepFault"/> with code
/// <c>None</c> on success, and reports the state's length.
/// </para>
/// </summary>
public sealed class StateSerializerTests
{
    private const ulong WorkedExampleHash = 0x35038da2f2d9459cUL;

    private const int WorkedExampleLength = 136;

    /// <summary>The worked example's six event counts start here.</summary>
    private const int WorkedExampleEventsOffset = 0x7c;

    /// <summary>Fixed seed of <see cref="WriteThenReadThenWriteIsTheIdentity"/>; a failure prints it.</summary>
    private const ulong PropertySeed = 0x0015_0027_57A7_E5EDUL;

    private const int PropertyIterations = 300;

    private const uint NegativeZeroBits = 0x80000000u;

    /// <summary>
    /// The 7 event float paths. The Phase 2 world produces no shot or hit, so the world-level
    /// canonicalisation Theories (CanonicalisationTests) cannot reach them; they are pinned here.
    /// </summary>
    private static readonly string[] EventFloatPathList =
    [
        "events.shots[0].direction.x",
        "events.shots[0].direction.y",
        "events.shots[0].direction.z",
        "events.hits[0].point.x",
        "events.hits[0].point.y",
        "events.hits[0].point.z",
        "events.hits[0].t",
    ];

    /// <summary>A quiet NaN, +∞ and −∞.</summary>
    private static readonly uint[] NonFiniteBitsList = [0x7FC00000u, 0x7F800000u, 0xFF800000u];

    // ------------------------------------------------------------ generators of loadable states

    /// <summary>Any finite float, never −0: a non-finite draw loses its exponent's top bit.</summary>
    private static readonly Gen<float> AnyFinite = Gen.Select(Gen.UInt, FiniteFromBits);

    /// <summary>A coordinate within ±8192 m, never −0.</summary>
    private static readonly Gen<float> InBounds = Gen.Select(Gen.Float[-8192f, 8192f], InBoundsOrZero);

    private static readonly Gen<(float, float, float)> AnyVector = Gen.Select(AnyFinite, AnyFinite, AnyFinite);

    private static readonly Gen<(float, float, float)> BoundedVector = Gen.Select(InBounds, InBounds, InBounds);

    /// <summary>A weapon record that passes the byte-only checks: phase ≤ 4, flags ≤ 1, every other field free.</summary>
    private static readonly Gen<WeaponRecord> AnyWeapon = Gen.Select(
        Gen.Byte[0, 4],
        Gen.Byte[0, 1],
        Gen.UShort,
        Gen.Int,
        Gen.UInt,
        Gen.Select(Gen.UShort, Gen.UShort, Gen.UInt),
        Gen.Select(Gen.UShort, Gen.Int, Gen.Int),
        (phase, flags, phaseSteps, fireWait, shotCounter, rounds, rest) => new WeaponRecord
        {
            Phase = phase,
            Flags = flags,
            PhaseSteps = phaseSteps,
            FireWait = fireWait,
            ShotCounter = shotCounter,
            Magazine = rounds.Item1,
            Reserve = rounds.Item2,
            Heat = rounds.Item3,
            StepsSinceShot = rest.Item1,
            RecoilPitch = rest.Item2,
            RecoilYaw = rest.Item3,
        });

    /// <summary>A projectile with its ID and launch step still to be placed (see <see cref="Loadable"/>).</summary>
    private static readonly Gen<ProjectileRecord> AnyProjectile = Gen.Select(
        Gen.UInt,
        Gen.Byte,
        Gen.UInt,
        BoundedVector,
        AnyVector,
        BoundedVector,
        (ownerId, projectileClass, launchDraw, origin, launchVelocity, position) => new ProjectileRecord
        {
            OwnerId = ownerId,
            Class = projectileClass,
            LaunchStep = launchDraw,
            Origin = origin,
            LaunchVelocity = launchVelocity,
            Position = position,
        });

    /// <summary>
    /// Loadable states: W from 1 to 3, 0 to 4 characters, 0 to 8 projectiles, finite floats that are
    /// never −0, coordinates within ±8192 m, and no events.
    /// </summary>
    private static readonly Gen<StateRecords> LoadableStates = Gen.SelectMany(
        Gen.Int[1, 3],
        weaponSlots => Gen.Select(
            Gen.UInt,
            AnyCharacter(weaponSlots).Array[0, 4],
            AnyProjectile.Array[0, 8],
            Gen.UInt[1, 1000].Array[13],
            (step, characters, projectiles, gaps) => Loadable(step, weaponSlots, characters, projectiles, gaps)));

    public static TheoryData<string> EventFloatPaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in EventFloatPathList)
            {
                data.Add(path);
            }

            return data;
        }
    }

    public static TheoryData<string, uint> NonFiniteEventFloats
    {
        get
        {
            var data = new TheoryData<string, uint>();
            foreach (var path in EventFloatPathList)
            {
                foreach (var bits in NonFiniteBitsList)
                {
                    data.Add(path, bits);
                }
            }

            return data;
        }
    }

    /// <summary>Every row of contracts/state-serialization.md#loading-a-state's byte-only table.</summary>
    public static TheoryData<string> ByteOnlyRefusals =>
    [
        "length-11",
        "character-count-1025",
        "projectile-count-16385",
        "weapon-count-9",
        "truncated-100",
        "trailing-byte",
        "character-ids-descending",
        "projectile-id-0",
        "next-projectile-id-0",
        "projectile-ids-descending",
        "next-projectile-id-not-above",
        "projectile-origin-y-9000",
        "projectile-position-z-9000",
        "position-x-nan",
        "position-y-negative-zero",
        "position-x-9000",
        "aim-pitch-16385",
        "stance-2",
        "flags-0x04",
        "active-slot-2",
        "weapon-phase-5",
        "weapon-flags-0x02",
        "launch-step-after-step",
        "expiries-count-1",
    ];

    /// <summary>The worked example's character: ID 1 standing at (0.5, 0, −0.5), grounded, yaw 16384, the placeholder weapons.</summary>
    private static CharacterRecord WorkedExampleCharacter => TestStates.StandingCharacter(1, 0.5f, -0.5f) with { AimYaw = 16384 };

    [Fact]
    public void WorkedExampleHasItsBytesAndHash()
    {
        var state = new StateValues
        {
            Step = 0,
            NextProjectileId = 1,
            WeaponSlots = 2,
            Characters =
            [
                new CharacterState
                {
                    Id = 1,
                    Position = new Vec3(0.5f, 0f, -0.5f),
                    Velocity = new Vec3(0f, 0f, 0f),
                    GroundNormal = new Vec3(0f, 1f, 0f),
                    AimYaw = 16384,
                    AimPitch = 0,
                    Stance = Stance.Standing,
                    Flags = CharacterFlags.Grounded,
                    LastSequence = 0,
                    ActiveSlot = 0,
                    WeaponCount = 2,
                },
            ],
            Weapons =
            [
                new WeaponState { Phase = WeaponPhase.Ready, Magazine = 30, Reserve = 210, StepsSinceShot = 65535 },
                new WeaponState { Phase = WeaponPhase.Ready, Magazine = 1, Reserve = 5, StepsSinceShot = 65535 },
            ],
        };

        var bytes = WriteState(state, "the worked example");

        Assert.Equal(WorkedExampleLength, bytes.Length);
        AssertSameBytes(TestStates.WorkedExample, bytes, "the worked example");
        Assert.Equal(WorkedExampleEventsOffset, bytes.Length - TestStates.EventCountsLength);
        Assert.Equal(new byte[TestStates.EventCountsLength], bytes.AsSpan(WorkedExampleEventsOffset).ToArray());

        var hash = StateHash.Compute(bytes);
        Assert.True(hash == WorkedExampleHash, $"the worked example hashes to {hash:x16}, expected {WorkedExampleHash:x16}");
        Assert.Equal("35038da2f2d9459c", hash.ToString("x16", CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(64, 1024, 2, 1, 91672)]
    [InlineData(1024, 16384, 8, 8, 1829912)]
    public void MaxLengthMatchesAStateFilledToEveryMaximum(int c, int p, int w, int s, int expected)
    {
        Assert.Equal(expected, StateSerializer.MaxLength(c, p, w, s));

        // C characters with W weapons, P projectiles, C·S shots, P hits, P expiries, and C each of the
        // input rejections, fire blocks and movement outcomes: every list at its buffer's capacity.
        var records = Filled(c, p, w, s);
        var bytes = WriteState(ToValues(records), $"the state filled for ({c}, {p}, {w}, {s})", slack: 0);

        Assert.Equal(expected, bytes.Length);
        AssertSameBytes(Build(records), bytes, $"the state filled for ({c}, {p}, {w}, {s})");
    }

    [Fact]
    public void WriteThenReadThenWriteIsTheIdentity()
    {
        var pcg = new PCG(1, PropertySeed);
        var withCharacters = 0;
        var withProjectiles = 0;
        for (var i = 0; i < PropertyIterations; i++)
        {
            var records = LoadableStates.Generate(pcg, null!, out _);
            var context = $"seed 0x{PropertySeed:X16}, iteration {i} ({records.Characters.Count} characters with {records.WeaponSlots} weapons, {records.Projectiles.Count} projectiles, step {records.Step})";

            var written = WriteState(ToValues(records), context);
            AssertSameBytes(Build(records), written, $"{context}, written");

            var snapshot = ReadLoadedOrFail(written, context);
            var rewritten = WriteState(FromSnapshot(snapshot, records.WeaponSlots), $"{context}, rewritten");
            AssertSameBytes(written, rewritten, $"{context}, written, read with ReadLoaded and written again");

            withCharacters += records.Characters.Count > 0 ? 1 : 0;
            withProjectiles += records.Projectiles.Count > 0 ? 1 : 0;
        }

        // The generator must reach states with records, or a clean result would mean nothing.
        Assert.True(withCharacters > 0, $"seed 0x{PropertySeed:X16}: no generated state had a character");
        Assert.True(withProjectiles > 0, $"seed 0x{PropertySeed:X16}: no generated state had a projectile");
    }

    [Fact]
    public void WriterRefusesTheFirstNonFiniteFieldInLayoutOrder()
    {
        // The shot comes before the hit in the layout, so its direction.y is the field reported.
        var state = EventState();
        SetEventFloatBits(state, "events.shots[0].direction.y", 0x7FC00000u);
        SetEventFloatBits(state, "events.hits[0].t", 0x7F800000u);

        var fault = Serialize(state, new byte[ExpectedLength(state) + 64], out _);

        Assert.Equal(StepFaultCode.NonFinite, fault.Code);
        Assert.Equal("events.shots[0].direction.y", fault.Path);
    }

    /// <summary>Edge case: negative zero (FR-016), for each event float, at the serializer.</summary>
    [Theory]
    [MemberData(nameof(EventFloatPaths))]
    public void WriterRewritesNegativeZeroInEveryEventFloat(string fieldPath)
    {
        AssertFieldIsKnown(fieldPath, out _, out _);
        var negative = EventState();
        SetEventFloatBits(negative, fieldPath, NegativeZeroBits);
        var positive = EventState();
        SetEventFloatBits(positive, fieldPath, 0u);

        var negativeBytes = WriteState(negative, $"{fieldPath} = -0");
        var positiveBytes = WriteState(positive, $"{fieldPath} = +0");

        // +0 in the buffer, +0 written back into the source event, and the same bytes and hash as +0.
        Assert.Equal("00000000", Convert.ToHexString(negativeBytes, EventFloatOffset(fieldPath), 4));
        var sourceBits = GetEventFloatBits(negative, fieldPath);
        Assert.True(sourceBits == 0u, $"{fieldPath}: the source event holds 0x{sourceBits:X8} after Write, expected +0 (0x00000000)");
        AssertSameBytes(positiveBytes, negativeBytes, $"{fieldPath} written from -0");
        Assert.Equal(StateHash.Compute(positiveBytes), StateHash.Compute(negativeBytes));
    }

    /// <summary>Edge case: NaN and infinity (FR-017), for each event float, at the serializer.</summary>
    [Theory]
    [MemberData(nameof(NonFiniteEventFloats))]
    public void WriterRefusesANonFiniteEventFloat(string fieldPath, uint bits)
    {
        AssertFieldIsKnown(fieldPath, out var fieldOrdinal, out var elementIndex);
        var state = EventState();
        SetEventFloatBits(state, fieldPath, bits);

        var fault = Serialize(state, new byte[ExpectedLength(state) + 64], out _);

        Assert.Equal(StepFaultCode.NonFinite, fault.Code);
        Assert.Equal(fieldPath, fault.Path);
        Assert.Equal(fieldOrdinal, fault.FieldOrdinal);
        Assert.Equal(elementIndex, fault.ElementIndex);
    }

    [Theory]
    [MemberData(nameof(ByteOnlyRefusals))]
    public void LoadRefusesEachByteOnlyRule(string caseName)
    {
        var refusal = ByteOnlyRefusal(caseName);

        var loaded = Assert.Throws<CoreFormatException>(() => StateSerializer.ReadLoaded(refusal.Bytes, StateErrorContext.State));
        AssertRefusal($"{caseName}, ReadLoaded", refusal, loaded);

        if (refusal.OnlyALoadedStateRefusesIt)
        {
            // A committed state carries its step's events, so the committed reader decodes them.
            var snapshot = StateSerializer.ReadCommitted(refusal.Bytes);
            Assert.Equal(new ExpiryEvent { ProjectileId = 1, Reason = ExpiryReason.Range }, Assert.Single(snapshot.Expiries));
        }
        else
        {
            var committed = Assert.Throws<CoreFormatException>(() => StateSerializer.ReadCommitted(refusal.Bytes));
            AssertRefusal($"{caseName}, ReadCommitted", refusal, committed);
            Assert.Equal(loaded.Message, committed.Message);
        }
    }

    [Fact]
    public void ReadCommittedAcceptsAStateWithEvents()
    {
        // The worked example's records plus one shot (character 1, slot 0, shot counter 0, projectile 1,
        // direction (0, 0, 1)) and one movement outcome (character 1, HeadroomBlocked).
        var records = WorkedExampleRecords() with
        {
            Events = new EventRecords
            {
                Shots = [new ShotRecord(1, 0, 0, 1, (0f, 0f, 1f))],
                MovementOutcomes = [new SmallEventRecord(1, (byte)MovementOutcome.HeadroomBlocked)],
            },
        };
        var state = ToValues(records);
        var bytes = WriteState(state, "the worked example with a shot and a movement outcome");
        AssertSameBytes(Build(records), bytes, "the worked example with a shot and a movement outcome");

        var snapshot = StateSerializer.ReadCommitted(bytes);

        Assert.Equal(0u, snapshot.Step);
        Assert.Equal(1u, snapshot.NextProjectileId);
        CharacterState[] characters = [.. snapshot.Characters];
        WeaponState[] weapons = [.. snapshot.Weapons];
        Assert.Equal(state.Characters, characters);
        Assert.Equal(state.Weapons, weapons);
        Assert.Empty(snapshot.Projectiles);
        Assert.Equal(state.Shots[0], Assert.Single(snapshot.Shots));
        Assert.Empty(snapshot.Hits);
        Assert.Empty(snapshot.Expiries);
        Assert.Empty(snapshot.InputRejections);
        Assert.Empty(snapshot.FireBlocks);
        Assert.Equal(state.MovementOutcomes[0], Assert.Single(snapshot.MovementOutcomes));

        var rewritten = WriteState(FromSnapshot(snapshot, TestStates.PlaceholderWeaponSlots), "the snapshot written again");
        AssertSameBytes(bytes, rewritten, "the snapshot written again");

        // A loaded state has no events: the loading reader refuses the first non-zero count.
        var loaded = Assert.Throws<CoreFormatException>(() => StateSerializer.ReadLoaded(bytes, StateErrorContext.State));
        Assert.Equal("state", loaded.Error.Format);
        Assert.Equal(FormatErrorCode.Inconsistent, loaded.Error.Code);
        Assert.Equal("events.shots.count", loaded.Error.Field);
        Assert.Equal((long)WorkedExampleEventsOffset, loaded.Error.Offset);
        Assert.Equal("a loaded state has no events, found 1", loaded.Error.Detail);
    }

    // ------------------------------------------------------------ byte-only refusals

    private static StateRefusal ByteOnlyRefusal(string caseName)
    {
        var flying = TestStates.FlyingProjectile;
        var character = TestStates.StandingCharacter(1, 0.5f, 0.5f);
        return caseName switch
        {
            "length-11" => new(TestStates.WorkedExample.AsSpan(0, 11).ToArray(), FormatErrorCode.Truncated, "step"),
            "character-count-1025" => new(
                TestStates.Mutate(TestStates.WorkedExample, 4, TestStates.U16(1025)),
                FormatErrorCode.Oversized,
                "characterCount"),
            "projectile-count-16385" => new(
                TestStates.Mutate(TestStates.WorkedExample, 6, TestStates.U16(16385)),
                FormatErrorCode.Oversized,
                "projectileCount"),
            "weapon-count-9" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(51), [9]),
                FormatErrorCode.Oversized,
                "characters[0].weaponCount"),

            // weapons[1] starts at 94, so its fireWait (98 to 101) runs past a 100-byte end.
            "truncated-100" => new(
                TestStates.WorkedExample.AsSpan(0, 100).ToArray(),
                FormatErrorCode.Truncated,
                "characters[0].weapons[1].fireWait"),
            "trailing-byte" => new([.. TestStates.WorkedExample, 0x00], FormatErrorCode.Oversized, "length"),
            "character-ids-descending" => new(
                TestStates.Build(0, [TestStates.StandingCharacter(2, 0.5f, 0.5f), TestStates.StandingCharacter(1, 1.5f, 0.5f)]),
                FormatErrorCode.Inconsistent,
                "characters[1].id"),
            "projectile-id-0" => new(
                TestStates.Build(0, [character], [flying with { Id = 0 }], nextProjectileId: 1),
                FormatErrorCode.Inconsistent,
                "projectiles[0].id"),
            "next-projectile-id-0" => new(
                TestStates.Mutate(TestStates.WorkedExample, 8, TestStates.U32(0)),
                FormatErrorCode.Inconsistent,
                "nextProjectileId"),

            // Every launchStep ≤ step and every other field valid.
            "projectile-ids-descending" => new(
                TestStates.Build(0, [character], [flying with { Id = 2 }, flying with { Id = 1 }], nextProjectileId: 3),
                FormatErrorCode.Inconsistent,
                "projectiles[1].id"),
            "next-projectile-id-not-above" => new(
                TestStates.Build(0, [character], [flying with { Id = 5 }], nextProjectileId: 5),
                FormatErrorCode.Inconsistent,
                "nextProjectileId"),
            "projectile-origin-y-9000" => new(
                TestStates.Mutate(TestStates.WithFlyingProjectile, ProjectileField(17), TestStates.F32(9000f)),
                FormatErrorCode.OutOfRange,
                "projectiles[0].origin.y"),
            "projectile-position-z-9000" => new(
                TestStates.Mutate(TestStates.WithFlyingProjectile, ProjectileField(45), TestStates.F32(9000f)),
                FormatErrorCode.OutOfRange,
                "projectiles[0].position.z"),
            "position-x-nan" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(4), TestStates.U32(0x7FC00000u)),
                FormatErrorCode.NonFinite,
                "characters[0].position.x"),

            // A canonical state never holds −0, so decode then encode is the identity.
            "position-y-negative-zero" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(8), TestStates.U32(NegativeZeroBits)),
                FormatErrorCode.OutOfRange,
                "characters[0].position.y"),
            "position-x-9000" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(4), TestStates.F32(9000f)),
                FormatErrorCode.OutOfRange,
                "characters[0].position.x",
                Message: "state characters[0].position.x at offset 16: 9000 is outside [-8192, 8192]"),
            "aim-pitch-16385" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(42), TestStates.I16(16385)),
                FormatErrorCode.OutOfRange,
                "characters[0].aimPitch"),
            "stance-2" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(44), [2]),
                FormatErrorCode.OutOfRange,
                "characters[0].stance"),
            "flags-0x04" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(45), [0x04]),
                FormatErrorCode.OutOfRange,
                "characters[0].flags"),
            "active-slot-2" => new(
                TestStates.Mutate(TestStates.WorkedExample, CharacterField(50), [2]),
                FormatErrorCode.OutOfRange,
                "characters[0].activeSlot"),
            "weapon-phase-5" => new(
                TestStates.Mutate(TestStates.WorkedExample, WeaponField(0, 0), [5]),
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[0].phase"),
            "weapon-flags-0x02" => new(
                TestStates.Mutate(TestStates.WorkedExample, WeaponField(0, 1), [0x02]),
                FormatErrorCode.OutOfRange,
                "characters[0].weapons[0].flags"),
            "launch-step-after-step" => new(
                TestStates.Mutate(TestStates.WithFlyingProjectile, ProjectileField(9), TestStates.U32(1)),
                FormatErrorCode.OutOfRange,
                "projectiles[0].launchStep"),

            // The events start at 124 (0x7c): shots.count, hits.count, then expiries.count at 128.
            "expiries-count-1" => new(
                TestStates.Build(
                    0,
                    [WorkedExampleCharacter],
                    events: new EventRecords { Expiries = [new SmallEventRecord(1, (byte)ExpiryReason.Range)] }),
                FormatErrorCode.Inconsistent,
                "events.expiries.count",
                Message: "state events.expiries.count at offset 128: a loaded state has no events, found 1",
                OnlyALoadedStateRefusesIt: true),

            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
        };
    }

    private static void AssertRefusal(string label, StateRefusal refusal, CoreFormatException exception)
    {
        Assert.True(exception.Error.Format == "state", $"{label}: format {exception.Error.Format}, expected state");
        Assert.True(exception.Error.Code == refusal.Code, $"{label}: {exception.Message}; expected code {refusal.Code}");
        Assert.True(exception.Error.Field == refusal.Field, $"{label}: {exception.Message}; expected field {refusal.Field}");
        Assert.Equal(exception.Error.ToString(), exception.Message);
        Assert.StartsWith($"state {refusal.Field} at offset ", exception.Message, StringComparison.Ordinal);
        if (refusal.Message is not null)
        {
            Assert.Equal(refusal.Message, exception.Message);
        }
    }

    /// <summary>The offset of a field of the worked example's character 0 (contracts/state-serialization.md#character-record).</summary>
    private static int CharacterField(int offsetInRecord) => TestStates.CharacterOffset(0) + offsetInRecord;

    /// <summary>The offset of a field of character 0's weapon in <paramref name="slot"/>.</summary>
    private static int WeaponField(int slot, int offsetInRecord) => TestStates.WeaponOffset(0, slot) + offsetInRecord;

    /// <summary>The offset of a field of <see cref="TestStates.WithFlyingProjectile"/>'s projectile 0.</summary>
    private static int ProjectileField(int offsetInRecord) => TestStates.ProjectileOffset(1, 0) + offsetInRecord;

    // ------------------------------------------------------------ the event-float state

    /// <summary>
    /// The worked example's character at step 1 with one shot and one hit whose floats are all finite
    /// and non-zero; no projectiles. Each call returns new arrays.
    /// </summary>
    private static StateValues EventState() => new()
    {
        Step = 1,
        NextProjectileId = 2,
        WeaponSlots = TestStates.PlaceholderWeaponSlots,
        Characters = [ToCharacter(WorkedExampleCharacter)],
        Weapons = [.. TestStates.PlaceholderWeapons.Select(ToWeapon)],
        Shots =
        [
            new ShotEvent { CharacterId = 1, Slot = 0, ShotCounter = 0, ProjectileId = 1, Direction = new Vec3(0.6f, -0.8f, 0.25f) },
        ],
        Hits =
        [
            new ProjectileHit
            {
                ProjectileId = 1,
                Kind = HitKind.Character,
                TargetId = 4,
                Hitbox = HitboxPart.Torso,
                Point = new Vec3(1.5f, 2.25f, -3.5f),
                T = 0.5f,
            },
        ],
    };

    /// <summary>The offset of an event float in <see cref="EventState"/>'s canonical bytes.</summary>
    private static int EventFloatOffset(string path)
    {
        var shot = TestStates.CharacterOffset(1) + 2;
        var hit = shot + TestStates.ShotLength + 2;
        return path switch
        {
            "events.shots[0].direction.x" => shot + 13,
            "events.shots[0].direction.y" => shot + 17,
            "events.shots[0].direction.z" => shot + 21,
            "events.hits[0].point.x" => hit + 10,
            "events.hits[0].point.y" => hit + 14,
            "events.hits[0].point.z" => hit + 18,
            "events.hits[0].t" => hit + 22,
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Not an event float path."),
        };
    }

    private static void SetEventFloatBits(StateValues state, string path, uint bits)
    {
        var value = BitConverter.UInt32BitsToSingle(bits);
        var shot = state.Shots[0];
        var hit = state.Hits[0];
        switch (path)
        {
            case "events.shots[0].direction.x":
                state.Shots[0] = shot with { Direction = new Vec3(value, shot.Direction.Y, shot.Direction.Z) };
                break;
            case "events.shots[0].direction.y":
                state.Shots[0] = shot with { Direction = new Vec3(shot.Direction.X, value, shot.Direction.Z) };
                break;
            case "events.shots[0].direction.z":
                state.Shots[0] = shot with { Direction = new Vec3(shot.Direction.X, shot.Direction.Y, value) };
                break;
            case "events.hits[0].point.x":
                state.Hits[0] = hit with { Point = new Vec3(value, hit.Point.Y, hit.Point.Z) };
                break;
            case "events.hits[0].point.y":
                state.Hits[0] = hit with { Point = new Vec3(hit.Point.X, value, hit.Point.Z) };
                break;
            case "events.hits[0].point.z":
                state.Hits[0] = hit with { Point = new Vec3(hit.Point.X, hit.Point.Y, value) };
                break;
            case "events.hits[0].t":
                state.Hits[0] = hit with { T = value };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "Not an event float path.");
        }
    }

    private static uint GetEventFloatBits(StateValues state, string path) => path switch
    {
        "events.shots[0].direction.x" => Bits(state.Shots[0].Direction.X),
        "events.shots[0].direction.y" => Bits(state.Shots[0].Direction.Y),
        "events.shots[0].direction.z" => Bits(state.Shots[0].Direction.Z),
        "events.hits[0].point.x" => Bits(state.Hits[0].Point.X),
        "events.hits[0].point.y" => Bits(state.Hits[0].Point.Y),
        "events.hits[0].point.z" => Bits(state.Hits[0].Point.Z),
        "events.hits[0].t" => Bits(state.Hits[0].T),
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Not an event float path."),
    };

    /// <summary>The path maps to a field through <see cref="StateSerializer.TryFindField"/> and back through <c>FieldPath</c>.</summary>
    private static void AssertFieldIsKnown(string path, out int fieldOrdinal, out int elementIndex)
    {
        Assert.True(StateSerializer.TryFindField(path, out fieldOrdinal, out elementIndex), $"TryFindField does not know {path}");
        Assert.Equal(0, elementIndex);
        Assert.Equal(path, StateSerializer.FieldPath(fieldOrdinal, elementIndex));
    }

    // ------------------------------------------------------------ writing and reading

    /// <summary>The one call of <see cref="StateSerializer.Write"/>: the world's spans, in layout order, into a caller buffer.</summary>
    private static StepFault Serialize(StateValues state, byte[] destination, out int length) =>
        StateSerializer.Write(
            step: state.Step,
            nextProjectileId: state.NextProjectileId,
            weaponSlots: state.WeaponSlots,
            characters: state.Characters,
            weapons: state.Weapons,
            projectiles: state.Projectiles,
            shots: state.Shots,
            hits: state.Hits,
            expiries: state.Expiries,
            inputRejections: state.InputRejections,
            fireBlocks: state.FireBlocks,
            movementOutcomes: state.MovementOutcomes,
            destination: destination,
            length: out length);

    /// <summary>
    /// Writes <paramref name="state"/> into a buffer <paramref name="slack"/> bytes longer than its
    /// layout length (the world's buffer is the maximum length), asserts no fault and that length, and
    /// returns the state's bytes.
    /// </summary>
    private static byte[] WriteState(StateValues state, string label, int slack = 64)
    {
        var expectedLength = ExpectedLength(state);
        var buffer = new byte[expectedLength + slack];
        var fault = Serialize(state, buffer, out var length);
        if (fault.Code != StepFaultCode.None)
        {
            Assert.Fail($"{label}: Write refused the state with {fault.Code} at {fault.Path}");
        }

        Assert.True(length == expectedLength, $"{label}: Write reports {length} bytes, the layout gives {expectedLength}");
        return buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>stateLength = 24 + c·(52 + 30·W) + 49·p + 25·shots + 26·hits + 5·(the four 5-byte lists).</summary>
    private static int ExpectedLength(StateValues state) =>
        TestStates.HeaderLength + TestStates.EventCountsLength
        + (state.Characters.Length * TestStates.CharacterLength(state.WeaponSlots))
        + (state.Projectiles.Length * TestStates.ProjectileLength)
        + (state.Shots.Length * TestStates.ShotLength)
        + (state.Hits.Length * TestStates.HitLength)
        + (TestStates.SmallEventLength
            * (state.Expiries.Length + state.InputRejections.Length + state.FireBlocks.Length + state.MovementOutcomes.Length));

    private static StateSnapshot ReadLoadedOrFail(byte[] bytes, string context)
    {
        try
        {
            return StateSerializer.ReadLoaded(bytes, StateErrorContext.State);
        }
        catch (CoreFormatException exception)
        {
            throw new XunitException($"{context}: ReadLoaded refused a loadable state: {exception.Message}", exception);
        }
    }

    private static void AssertSameBytes(byte[] expected, byte[] actual, string label)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            var at = FirstDifference(expected, actual);
            Assert.Fail(
                $"{label}: {actual.Length} bytes where {expected.Length} were expected; first difference at offset {at} (0x{at:x}): " +
                $"{Window(actual, at)}, expected {Window(expected, at)}");
        }
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        var length = a.Length < b.Length ? a.Length : b.Length;
        for (var i = 0; i < length; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return length;
    }

    private static string Window(byte[] bytes, int at) =>
        at >= bytes.Length ? "(end)" : Convert.ToHexString(bytes, at, bytes.Length - at < 8 ? bytes.Length - at : 8);

    // ------------------------------------------------------------ records, core values and snapshots

    private static StateRecords WorkedExampleRecords() =>
        new(0, TestStates.PlaceholderWeaponSlots, [WorkedExampleCharacter], [], 1, EventRecords.None);

    private static byte[] Build(StateRecords records) =>
        TestStates.Build(records.Step, records.Characters, records.Projectiles, records.NextProjectileId, records.Events);

    /// <summary>
    /// A state with every list at its capacity: <paramref name="characters"/> characters (IDs from 1)
    /// with <paramref name="weaponSlots"/> weapons, <paramref name="projectiles"/> projectiles (IDs from
    /// 1), <paramref name="shotsPerCharacter"/> shots per character, a hit and an expiry per projectile,
    /// and an input rejection, a fire block and a movement outcome per character.
    /// </summary>
    private static StateRecords Filled(int characters, int projectiles, int weaponSlots, int shotsPerCharacter)
    {
        var weapons = Enumerable.Repeat(TestStates.Rifle, weaponSlots).ToArray();
        var characterRecords = new CharacterRecord[characters];
        var shots = new List<ShotRecord>(characters * shotsPerCharacter);
        var perCharacter = new List<SmallEventRecord>(characters);
        for (var i = 0; i < characters; i++)
        {
            var id = (uint)(i + 1);
            characterRecords[i] = TestStates.StandingCharacter(id, 0.5f, 0.5f) with { Weapons = weapons };
            for (var k = 0; k < shotsPerCharacter; k++)
            {
                shots.Add(new ShotRecord(id, (byte)(k % weaponSlots), (uint)k, (uint)((i * shotsPerCharacter) + k + 1), (0f, 0f, 1f)));
            }

            perCharacter.Add(new SmallEventRecord(id, 1));
        }

        var projectileRecords = new ProjectileRecord[projectiles];
        var hits = new List<HitRecord>(projectiles);
        var expiries = new List<SmallEventRecord>(projectiles);
        for (var j = 0; j < projectiles; j++)
        {
            var id = (uint)(j + 1);
            projectileRecords[j] = TestStates.FlyingProjectile with { Id = id };
            hits.Add(new HitRecord(id, (byte)HitKind.Terrain, 7, (byte)HitboxPart.None, (0.5f, 0f, -0.5f), 0.5f));
            expiries.Add(new SmallEventRecord(id, (byte)ExpiryReason.Lifetime));
        }

        var events = new EventRecords
        {
            Shots = shots,
            Hits = hits,
            Expiries = expiries,
            InputRejections = perCharacter,
            FireBlocks = perCharacter,
            MovementOutcomes = perCharacter,
        };
        return new StateRecords(0, weaponSlots, characterRecords, projectileRecords, (uint)projectiles + 1, events);
    }

    /// <summary>Places IDs and launch steps: character IDs strictly ascending from 0 or more, projectile IDs from 1, nextProjectileId above them.</summary>
    private static StateRecords Loadable(uint step, int weaponSlots, CharacterRecord[] characters, ProjectileRecord[] projectiles, uint[] gaps)
    {
        var placedCharacters = new CharacterRecord[characters.Length];
        var characterId = gaps[0] - 1;
        for (var i = 0; i < characters.Length; i++)
        {
            characterId += i == 0 ? 0 : gaps[i];
            placedCharacters[i] = characters[i] with { Id = characterId };
        }

        var placedProjectiles = new ProjectileRecord[projectiles.Length];
        var projectileId = 0u;
        for (var j = 0; j < projectiles.Length; j++)
        {
            projectileId += gaps[4 + j];
            var launchStep = (uint)(projectiles[j].LaunchStep % ((ulong)step + 1));
            placedProjectiles[j] = projectiles[j] with { Id = projectileId, LaunchStep = launchStep };
        }

        return new StateRecords(step, weaponSlots, placedCharacters, placedProjectiles, projectileId + gaps[12], EventRecords.None);
    }

    private static Gen<CharacterRecord> AnyCharacter(int weaponSlots) => Gen.Select(
        BoundedVector,
        AnyVector,
        AnyVector,
        Gen.Select(Gen.UShort, Gen.Short[-16384, 16384], Gen.Byte[0, 1], Gen.Byte[0, 3]),
        Gen.UInt,
        Gen.Byte[0, (byte)(weaponSlots - 1)],
        AnyWeapon.Array[weaponSlots],
        (position, velocity, groundNormal, pose, lastSequence, activeSlot, weapons) => new CharacterRecord
        {
            Position = position,
            Velocity = velocity,
            GroundNormal = groundNormal,
            AimYaw = pose.Item1,
            AimPitch = pose.Item2,
            Stance = pose.Item3,
            Flags = pose.Item4,
            LastSequence = lastSequence,
            ActiveSlot = activeSlot,
            Weapons = weapons,
        });

    private static float FiniteFromBits(uint bits)
    {
        if ((bits & 0x7F800000u) == 0x7F800000u)
        {
            bits &= 0xBFFFFFFFu;
        }

        return bits == NegativeZeroBits ? 0f : BitConverter.UInt32BitsToSingle(bits);
    }

    private static float InBoundsOrZero(float value) =>
        value is >= -8192f and <= 8192f && Bits(value) != NegativeZeroBits ? value : 0f;

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static StateValues ToValues(StateRecords records) => new()
    {
        Step = records.Step,
        NextProjectileId = records.NextProjectileId,
        WeaponSlots = records.WeaponSlots,
        Characters = [.. records.Characters.Select(ToCharacter)],
        Weapons = [.. records.Characters.SelectMany(character => character.Weapons).Select(ToWeapon)],
        Projectiles = [.. records.Projectiles.Select(ToProjectile)],
        Shots = [.. records.Events.Shots.Select(ToShot)],
        Hits = [.. records.Events.Hits.Select(ToHit)],
        Expiries = [.. records.Events.Expiries.Select(ToExpiry)],
        InputRejections = [.. records.Events.InputRejections.Select(ToInputRejection)],
        FireBlocks = [.. records.Events.FireBlocks.Select(ToFireBlock)],
        MovementOutcomes = [.. records.Events.MovementOutcomes.Select(ToMovementOutcome)],
    };

    /// <summary>A snapshot's lists as the writer's spans; the weapons are flat in record order.</summary>
    private static StateValues FromSnapshot(StateSnapshot snapshot, int weaponSlots) => new()
    {
        Step = snapshot.Step,
        NextProjectileId = snapshot.NextProjectileId,
        WeaponSlots = weaponSlots,
        Characters = [.. snapshot.Characters],
        Weapons = [.. snapshot.Weapons],
        Projectiles = [.. snapshot.Projectiles],
        Shots = [.. snapshot.Shots],
        Hits = [.. snapshot.Hits],
        Expiries = [.. snapshot.Expiries],
        InputRejections = [.. snapshot.InputRejections],
        FireBlocks = [.. snapshot.FireBlocks],
        MovementOutcomes = [.. snapshot.MovementOutcomes],
    };

    private static Vec3 ToVec3((float X, float Y, float Z) vector) => new(vector.X, vector.Y, vector.Z);

    private static CharacterState ToCharacter(CharacterRecord record) => new()
    {
        Id = record.Id,
        Position = ToVec3(record.Position),
        Velocity = ToVec3(record.Velocity),
        GroundNormal = ToVec3(record.GroundNormal),
        AimYaw = record.AimYaw,
        AimPitch = record.AimPitch,
        Stance = (Stance)record.Stance,
        Flags = (CharacterFlags)record.Flags,
        LastSequence = record.LastSequence,
        ActiveSlot = record.ActiveSlot,
        WeaponCount = (byte)record.Weapons.Count,
    };

    private static WeaponState ToWeapon(WeaponRecord record) => new()
    {
        Phase = (WeaponPhase)record.Phase,
        Flags = (WeaponFlags)record.Flags,
        PhaseSteps = record.PhaseSteps,
        FireWait = record.FireWait,
        ShotCounter = record.ShotCounter,
        Magazine = record.Magazine,
        Reserve = record.Reserve,
        Heat = record.Heat,
        StepsSinceShot = record.StepsSinceShot,
        RecoilPitch = record.RecoilPitch,
        RecoilYaw = record.RecoilYaw,
    };

    private static Projectile ToProjectile(ProjectileRecord record) => new()
    {
        Id = record.Id,
        OwnerId = record.OwnerId,
        Class = record.Class,
        LaunchStep = record.LaunchStep,
        Origin = ToVec3(record.Origin),
        LaunchVelocity = ToVec3(record.LaunchVelocity),
        Position = ToVec3(record.Position),
    };

    private static ShotEvent ToShot(ShotRecord record) => new()
    {
        CharacterId = record.CharacterId,
        Slot = record.Slot,
        ShotCounter = record.ShotCounter,
        ProjectileId = record.ProjectileId,
        Direction = ToVec3(record.Direction),
    };

    private static ProjectileHit ToHit(HitRecord record) => new()
    {
        ProjectileId = record.ProjectileId,
        Kind = (HitKind)record.Kind,
        TargetId = record.TargetId,
        Hitbox = (HitboxPart)record.Hitbox,
        Point = ToVec3(record.Point),
        T = record.T,
    };

    private static ExpiryEvent ToExpiry(SmallEventRecord record) =>
        new() { ProjectileId = record.Id, Reason = (ExpiryReason)record.Code };

    private static InputRejectionEvent ToInputRejection(SmallEventRecord record) =>
        new() { CharacterId = record.Id, Reason = (InputRejection)record.Code };

    private static FireBlockEvent ToFireBlock(SmallEventRecord record) =>
        new() { CharacterId = record.Id, Reason = (FireBlock)record.Code };

    private static MovementOutcomeEvent ToMovementOutcome(SmallEventRecord record) =>
        new() { CharacterId = record.Id, Flags = (MovementOutcome)record.Code };

    /// <summary>A state as <see cref="TestStates"/> records, the independent description it is built from.</summary>
    private sealed record StateRecords(
        uint Step,
        int WeaponSlots,
        IReadOnlyList<CharacterRecord> Characters,
        IReadOnlyList<ProjectileRecord> Projectiles,
        uint NextProjectileId,
        EventRecords Events);

    /// <summary>A state as the world holds it: the header values and the arrays the writer takes as spans.</summary>
    private sealed class StateValues
    {
        public uint Step { get; init; }

        public uint NextProjectileId { get; init; } = 1;

        public int WeaponSlots { get; init; } = TestStates.PlaceholderWeaponSlots;

        public CharacterState[] Characters { get; init; } = [];

        /// <summary>Flat by character index × <see cref="WeaponSlots"/> + slot.</summary>
        public WeaponState[] Weapons { get; init; } = [];

        public Projectile[] Projectiles { get; init; } = [];

        public ShotEvent[] Shots { get; init; } = [];

        public ProjectileHit[] Hits { get; init; } = [];

        public ExpiryEvent[] Expiries { get; init; } = [];

        public InputRejectionEvent[] InputRejections { get; init; } = [];

        public FireBlockEvent[] FireBlocks { get; init; } = [];

        public MovementOutcomeEvent[] MovementOutcomes { get; init; } = [];
    }

    /// <summary>A refused state and the error both readers give, unless only a loaded state is refused.</summary>
    private sealed record StateRefusal(
        byte[] Bytes,
        FormatErrorCode Code,
        string Field,
        string? Message = null,
        bool OnlyALoadedStateRefusesIt = false);
}
