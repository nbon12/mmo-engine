using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Weapons;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Runs a named scenario in process: <see cref="ScenarioCatalog"/> builds its stream over
/// <see cref="TestWorlds.TestWorld"/> (the generated test world, never the committed <c>.mmcd</c>) at the
/// given rate, and a <see cref="SimWorld"/> steps it. Every step's committed state is decoded with
/// <c>StateSerializer.ReadCommitted</c>, so a test reads characters, weapons and projectiles as the core
/// committed them, beside the step's event lists, counters and fault.
/// </summary>
public static class ScenarioRuns
{
    /// <summary>
    /// Builds <c>ScenarioCatalog.Get(scenario).Build(TestConfigs.Default(), TestWorlds.TestWorld, hz)</c>,
    /// compiles its configuration, and runs it on a new <see cref="SimWorld"/> over the test world.
    /// </summary>
    public static ScenarioRun Run(string scenario, int hz)
    {
        ArgumentException.ThrowIfNullOrEmpty(scenario);
        var stream = ScenarioCatalog.Get(scenario).Build(TestConfigs.Default(), TestWorlds.TestWorld, hz);
        var world = new SimWorld(CompiledConfig.Create(stream.Config), TestWorlds.TestWorld);
        return Run(world, stream);
    }

    /// <summary>
    /// Resets <paramref name="world"/> with the stream's initial state and match salt, then steps every
    /// entry with its commands (characters without one get the neutral input). A faulted step commits
    /// nothing, so its record keeps the previous characters, weapons and projectiles, with empty events,
    /// an empty state and hash 0, and the run goes on with the next entry.
    /// </summary>
    public static ScenarioRun Run(SimWorld world, RecordedStream stream)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(stream);
        var initialState = stream.InitialState.Span;
        world.Reset(initialState, stream.MatchSalt);
        var initial = StateSerializer.ReadLoaded(initialState, StateErrorContext.State);

        IReadOnlyList<CharacterState> characters = initial.Characters;
        IReadOnlyList<WeaponState> weapons = initial.Weapons;
        IReadOnlyList<Projectile> projectiles = initial.Projectiles;
        var steps = new List<ScenarioStep>(stream.Steps.Count);
        foreach (var entry in stream.Steps)
        {
            var commands = new CharacterCommand[entry.Commands.Length];
            for (var c = 0; c < commands.Length; c++)
            {
                commands[c] = new CharacterCommand(entry.Commands[c].CharacterIndex, entry.Commands[c].Command);
            }

            var report = world.Step(commands);
            if (report.Fault.Code != StepFaultCode.None)
            {
                steps.Add(new ScenarioStep(
                    report.Step, report.Fault, report.Hash, [], characters, weapons, projectiles,
                    [], [], [], [], [], [], report.Counters));
                continue;
            }

            var committed = StateSerializer.ReadCommitted(report.State);
            characters = committed.Characters;
            weapons = committed.Weapons;
            projectiles = committed.Projectiles;
            var events = report.Events;
            steps.Add(new ScenarioStep(
                report.Step,
                report.Fault,
                report.Hash,
                report.State.ToArray(),
                characters,
                weapons,
                projectiles,
                events.Shots.ToArray(),
                events.Hits.ToArray(),
                events.Expiries.ToArray(),
                events.InputRejections.ToArray(),
                events.FireBlocks.ToArray(),
                events.MovementOutcomes.ToArray(),
                report.Counters));
        }

        return new ScenarioRun(stream, world.Config, world.Collision, initial, steps);
    }
}

/// <summary>
/// A scenario run: the stream it ran, the compiled configuration and collision world it ran with, the
/// initial state as loaded, and one <see cref="ScenarioStep"/> per stream entry, in order.
/// </summary>
public sealed record ScenarioRun(
    RecordedStream Stream,
    CompiledConfig Config,
    CollisionWorld Collision,
    StateSnapshot Initial,
    IReadOnlyList<ScenarioStep> Steps)
{
    /// <summary>The characters of the initial state, in ascending ID.</summary>
    public IReadOnlyList<CharacterState> InitialCharacters => Initial.Characters;

    /// <summary>The last step.</summary>
    public ScenarioStep Last => Steps[^1];
}

/// <summary>
/// One step of a <see cref="ScenarioRun"/>: the report's step number, fault and hash; the committed state's
/// bytes and its decoded characters, weapons and projectiles; the six event lists; and the counters.
/// </summary>
public sealed record ScenarioStep(
    uint Step,
    StepFault Fault,
    ulong Hash,
    byte[] State,
    IReadOnlyList<CharacterState> Characters,
    IReadOnlyList<WeaponState> Weapons,
    IReadOnlyList<Projectile> Projectiles,
    IReadOnlyList<ShotEvent> Shots,
    IReadOnlyList<ProjectileHit> Hits,
    IReadOnlyList<ExpiryEvent> Expiries,
    IReadOnlyList<InputRejectionEvent> InputRejections,
    IReadOnlyList<FireBlockEvent> FireBlocks,
    IReadOnlyList<MovementOutcomeEvent> MovementOutcomes,
    StepCounters Counters);
