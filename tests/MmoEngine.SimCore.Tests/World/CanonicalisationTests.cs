using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.World;

/// <summary>
/// Stage 6 of the world step over the 18 floats of the character and projectile records
/// (contracts/state-serialization.md#canonicalisation-and-the-finite-check): negative zero is rewritten
/// to positive zero at commit, so equal hashes mean equal states (FR-016), and a NaN or infinity refuses
/// the step naming the field, committing nothing (FR-017). Each case injects the bits into the next
/// state through the internal <c>SimWorld.StepWithInjectedBits</c>, on a world on
/// <see cref="TestWorlds.Flat"/> Reset with <see cref="TestStates.WithFlyingProjectile"/> at 30 Hz. The
/// seven event floats are covered at the serializer by <c>StateSerializerTests</c>, because the Phase 2
/// world produces no shot or hit events. The committed in-memory state is read back through the
/// internal <c>SimWorld.CommittedCharacters</c> and <c>CommittedProjectiles</c>.
/// </summary>
public sealed class CanonicalisationTests
{
    private const ulong Salt = 7;

    /// <summary>The character's 9 floats and the projectile's 9, in layout order.</summary>
    private static readonly string[] Paths =
    [
        "characters[0].position.x",
        "characters[0].position.y",
        "characters[0].position.z",
        "characters[0].velocity.x",
        "characters[0].velocity.y",
        "characters[0].velocity.z",
        "characters[0].groundNormal.x",
        "characters[0].groundNormal.y",
        "characters[0].groundNormal.z",
        "projectiles[0].origin.x",
        "projectiles[0].origin.y",
        "projectiles[0].origin.z",
        "projectiles[0].launchVelocity.x",
        "projectiles[0].launchVelocity.y",
        "projectiles[0].launchVelocity.z",
        "projectiles[0].position.x",
        "projectiles[0].position.y",
        "projectiles[0].position.z",
    ];

    /// <summary>
    /// Floats of the second character and the second projectile, which a fault must name by their
    /// position in the list: index 1, not 0 and not their IDs (4 and 3).
    /// </summary>
    private static readonly string[] LaterElementPaths =
    [
        "characters[1].position.x",
        "characters[1].velocity.y",
        "characters[1].groundNormal.z",
        "projectiles[1].origin.x",
        "projectiles[1].launchVelocity.y",
        "projectiles[1].position.z",
    ];

    public static TheoryData<string> FloatPaths => [.. Paths];

    /// <summary>Each later-element path × quiet NaN, +∞ and −∞.</summary>
    public static TheoryData<string, uint> LaterElementNonFiniteCases
    {
        get
        {
            var data = new TheoryData<string, uint>();
            foreach (var path in LaterElementPaths)
            {
                foreach (var bits in new[] { 0x7FC00000u, 0x7F800000u, 0xFF800000u })
                {
                    data.Add(path, bits);
                }
            }

            return data;
        }
    }

    /// <summary>Each float path × quiet NaN, +∞ and −∞.</summary>
    public static TheoryData<string, uint> NonFiniteCases
    {
        get
        {
            var data = new TheoryData<string, uint>();
            foreach (var path in Paths)
            {
                foreach (var bits in new[] { 0x7FC00000u, 0x7F800000u, 0xFF800000u })
                {
                    data.Add(path, bits);
                }
            }

            return data;
        }
    }

    /// <summary>
    /// Edge case "negative zero" (FR-016): −0 injected into the field commits as +0. World A gets bits
    /// <c>0x80000000</c> and world B bits <c>0x00000000</c>: both commit, A's state holds
    /// <c>00 00 00 00</c> there, A's committed in-memory field holds +0 (the writer writes +0 back into the
    /// next state, which the commit swaps in), A's state and hash equal B's, and so do the hashes after a
    /// further plain step.
    /// </summary>
    [Theory]
    [MemberData(nameof(FloatPaths))]
    public void NegativeZeroIsCommittedAndHashedAsPositiveZero(string fieldPath)
    {
        var (ordinal, index) = Field(fieldPath);
        var offset = StateOffset(fieldPath);
        var a = NewWorld();
        var b = NewWorld();

        var reportA = a.StepWithInjectedBits([], ordinal, index, 0x80000000u);
        Assert.Equal(StepFaultCode.None, reportA.Fault.Code);
        var stateA = reportA.State.ToArray();
        var hashA = reportA.Hash;

        var reportB = b.StepWithInjectedBits([], ordinal, index, 0x00000000u);
        Assert.Equal(StepFaultCode.None, reportB.Fault.Code);
        var stateB = reportB.State.ToArray();
        var hashB = reportB.Hash;

        byte[] positiveZero = [0, 0, 0, 0];
        Assert.Equal(positiveZero, stateA[offset..(offset + 4)]);
        var committed = CommittedBits(a, fieldPath, index);
        Assert.True(committed == 0u, $"{fieldPath}: the committed field holds 0x{committed:X8} after the step, expected +0 (0x00000000)");
        Assert.Equal(hashB, hashA);
        Assert.Equal(stateB, stateA);

        // A and B now hold the same committed state, in memory as well as in bytes, so the following
        // plain step agrees too.
        var nextA = a.Step([]);
        var nextCodeA = nextA.Fault.Code;
        var nextStepA = nextA.Step;
        var nextHashA = nextA.Hash;
        var nextB = b.Step([]);
        Assert.Equal(nextB.Fault.Code, nextCodeA);
        Assert.Equal(nextB.Step, nextStepA);
        Assert.Equal(nextB.Hash, nextHashA);
    }

    /// <summary>
    /// Edge case "NaN or infinity" (FR-017): a non-finite float in the next state refuses the step with
    /// <c>StepFaultCode.NonFinite</c> and that field's path, hash 0 and the previous step number. Nothing
    /// is committed: the world's step is unchanged, and its next plain step gives the step number, state
    /// bytes and hash that an untouched world gives.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonFiniteCases))]
    public void NonFiniteFieldRefusesTheStepAndKeepsThePreviousState(string fieldPath, uint bits)
    {
        var (ordinal, index) = Field(fieldPath);
        var world = NewWorld();
        var previousStep = world.CurrentStep;

        var faulted = world.StepWithInjectedBits([], ordinal, index, bits);

        Assert.Equal(StepFaultCode.NonFinite, faulted.Fault.Code);
        Assert.Equal(fieldPath, faulted.Fault.Path);
        Assert.Equal(0UL, faulted.Hash);
        Assert.Equal(previousStep, faulted.Step);
        Assert.Equal(previousStep, world.CurrentStep);

        var untouched = NewWorld().Step([]);
        Assert.Equal(StepFaultCode.None, untouched.Fault.Code);
        var expectedStep = untouched.Step;
        var expectedState = untouched.State.ToArray();
        var expectedHash = untouched.Hash;

        var next = world.Step([]);

        Assert.Equal(StepFaultCode.None, next.Fault.Code);
        Assert.Equal(expectedStep, next.Step);
        Assert.Equal(expectedStep, world.CurrentStep);
        Assert.Equal(expectedState, next.State.ToArray());
        Assert.Equal(expectedHash, next.Hash);
    }

    /// <summary>
    /// Edge case "NaN or infinity" (FR-017) in a later element: on a world holding characters 1 and 4 and
    /// projectiles 1 and 3, a non-finite float in the second character or projectile refuses the step
    /// naming that element by its index, 1, so the path is neither element 0's nor the element's ID. Hash
    /// 0, the previous step and an unchanged world follow as for element 0: the next plain step gives the
    /// step number, state bytes and hash an untouched world on the same state gives.
    /// </summary>
    [Theory]
    [MemberData(nameof(LaterElementNonFiniteCases))]
    public void NonFiniteFieldOfALaterElementNamesThatElement(string fieldPath, uint bits)
    {
        var (ordinal, index) = Field(fieldPath);
        Assert.Equal(1, index);
        var world = NewTwoElementWorld();
        var previousStep = world.CurrentStep;

        var faulted = world.StepWithInjectedBits([], ordinal, index, bits);

        Assert.Equal(StepFaultCode.NonFinite, faulted.Fault.Code);
        Assert.Equal(fieldPath, faulted.Fault.Path);
        Assert.Equal(1, faulted.Fault.ElementIndex);
        Assert.Equal(0UL, faulted.Hash);
        Assert.Equal(previousStep, faulted.Step);
        Assert.Equal(previousStep, world.CurrentStep);

        var untouched = NewTwoElementWorld().Step([]);
        Assert.Equal(StepFaultCode.None, untouched.Fault.Code);
        var expectedStep = untouched.Step;
        var expectedState = untouched.State.ToArray();
        var expectedHash = untouched.Hash;

        var next = world.Step([]);

        Assert.Equal(StepFaultCode.None, next.Fault.Code);
        Assert.Equal(expectedStep, next.Step);
        Assert.Equal(expectedStep, world.CurrentStep);
        Assert.Equal(expectedState, next.State.ToArray());
        Assert.Equal(expectedHash, next.Hash);
    }

    private static SimWorld NewWorld()
    {
        var world = new SimWorld(TestConfigs.Compiled(30), TestWorlds.Load(TestWorlds.Flat()));
        world.Reset(TestStates.WithFlyingProjectile, Salt);
        return world;
    }

    /// <summary>
    /// A world on <see cref="TestWorlds.Flat"/> at 30 Hz holding two characters (IDs 1 and 4, as in
    /// <see cref="TestStates.TwoCharacters"/>) and two projectiles (IDs 1 and 3, owned by 1 and 4), with
    /// <c>nextProjectileId</c> 4.
    /// </summary>
    private static SimWorld NewTwoElementWorld()
    {
        var world = new SimWorld(TestConfigs.Compiled(30), TestWorlds.Load(TestWorlds.Flat()));
        world.Reset(
            TestStates.Build(
                0,
                [TestStates.StandingCharacter(1, 0.5f, 0.5f), TestStates.StandingCharacter(4, 4.5f, 0.5f)],
                [TestStates.FlyingProjectile, TestStates.FlyingProjectile with { Id = 3, OwnerId = 4, Origin = (4f, 50f, 0f), Position = (4f, 50f, 0f) }]),
            Salt);
        return world;
    }

    /// <summary>The bits of the float <paramref name="fieldPath"/> names, in element <paramref name="index"/> of the world's committed state.</summary>
    private static uint CommittedBits(SimWorld world, string fieldPath, int index)
    {
        var field = fieldPath[(fieldPath.IndexOf(']', StringComparison.Ordinal) + 2)..];
        if (fieldPath.StartsWith("characters[", StringComparison.Ordinal))
        {
            var character = world.CommittedCharacters[index];
            return field switch
            {
                "position.x" => Bits(character.Position.X),
                "position.y" => Bits(character.Position.Y),
                "position.z" => Bits(character.Position.Z),
                "velocity.x" => Bits(character.Velocity.X),
                "velocity.y" => Bits(character.Velocity.Y),
                "velocity.z" => Bits(character.Velocity.Z),
                "groundNormal.x" => Bits(character.GroundNormal.X),
                "groundNormal.y" => Bits(character.GroundNormal.Y),
                "groundNormal.z" => Bits(character.GroundNormal.Z),
                _ => throw new ArgumentOutOfRangeException(nameof(fieldPath), fieldPath, "Not a character float."),
            };
        }

        var projectile = world.CommittedProjectiles[index];
        return field switch
        {
            "origin.x" => Bits(projectile.Origin.X),
            "origin.y" => Bits(projectile.Origin.Y),
            "origin.z" => Bits(projectile.Origin.Z),
            "launchVelocity.x" => Bits(projectile.LaunchVelocity.X),
            "launchVelocity.y" => Bits(projectile.LaunchVelocity.Y),
            "launchVelocity.z" => Bits(projectile.LaunchVelocity.Z),
            "position.x" => Bits(projectile.Position.X),
            "position.y" => Bits(projectile.Position.Y),
            "position.z" => Bits(projectile.Position.Z),
            _ => throw new ArgumentOutOfRangeException(nameof(fieldPath), fieldPath, "Not a projectile float."),
        };
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    private static (int Ordinal, int Index) Field(string fieldPath)
    {
        Assert.True(
            StateSerializer.TryFindField(fieldPath, out var ordinal, out var index),
            $"StateSerializer.TryFindField does not know {fieldPath}");
        return (ordinal, index);
    }

    /// <summary>
    /// The field's offset in the committed state (one character with the placeholder two weapons, then
    /// the projectile), from the layout tables of contracts/state-serialization.md, independent of
    /// <c>StateSerializer</c>.
    /// </summary>
    private static int StateOffset(string fieldPath)
    {
        var character = TestStates.CharacterOffset(0);
        var projectile = TestStates.ProjectileOffset(1, 0);
        return fieldPath switch
        {
            "characters[0].position.x" => character + 4,
            "characters[0].position.y" => character + 8,
            "characters[0].position.z" => character + 12,
            "characters[0].velocity.x" => character + 16,
            "characters[0].velocity.y" => character + 20,
            "characters[0].velocity.z" => character + 24,
            "characters[0].groundNormal.x" => character + 28,
            "characters[0].groundNormal.y" => character + 32,
            "characters[0].groundNormal.z" => character + 36,
            "projectiles[0].origin.x" => projectile + 13,
            "projectiles[0].origin.y" => projectile + 17,
            "projectiles[0].origin.z" => projectile + 21,
            "projectiles[0].launchVelocity.x" => projectile + 25,
            "projectiles[0].launchVelocity.y" => projectile + 29,
            "projectiles[0].launchVelocity.z" => projectile + 33,
            "projectiles[0].position.x" => projectile + 37,
            "projectiles[0].position.y" => projectile + 41,
            "projectiles[0].position.z" => projectile + 45,
            _ => throw new ArgumentOutOfRangeException(nameof(fieldPath), fieldPath, "Not a character or projectile float."),
        };
    }
}
