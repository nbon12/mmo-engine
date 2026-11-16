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
/// world produces no shot or hit events.
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

    public static TheoryData<string> FloatPaths => [.. Paths];

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
    /// <c>00 00 00 00</c> there, A's state and hash equal B's, and so do the hashes after a further plain
    /// step.
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
        Assert.Equal(hashB, hashA);
        Assert.Equal(stateB, stateA);

        // The states are equal, so the following plain step is too, whatever the later phases' stages do
        // with the zero they hold.
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

    private static SimWorld NewWorld()
    {
        var world = new SimWorld(TestConfigs.Compiled(30), TestWorlds.Load(TestWorlds.Flat()));
        world.Reset(TestStates.WithFlyingProjectile, Salt);
        return world;
    }

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
