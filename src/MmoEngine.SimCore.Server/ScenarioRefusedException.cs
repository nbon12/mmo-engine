namespace MmoEngine.SimCore.Server;

/// <summary>
/// Thrown by <see cref="ScenarioDefinition.Build"/> when a planned definition cannot be built on the
/// configuration or the collision world it is given (specs/015-shared-simulation-core/contracts/simcore-cli.md#streams-record):
/// an aim point out of the weapon's reach, a weapon slot the configuration lacks, a mesh or crossing the
/// world does not have, or a shot whose probe run does not end as the plan says. It is a refused input, not a
/// programming error, so <c>simcore streams record</c> prints <c>streams record: &lt;name&gt;: &lt;reason&gt;</c>
/// and exits 2. Its message is <c>&lt;scenario&gt;: &lt;reason&gt;</c>.
/// </summary>
public sealed class ScenarioRefusedException : Exception
{
    /// <summary>An exception for <paramref name="scenario"/>, refused because of <paramref name="reason"/>.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public ScenarioRefusedException(string scenario, string reason)
        : base(string.Concat(scenario ?? throw new ArgumentNullException(nameof(scenario)), ": ", reason ?? throw new ArgumentNullException(nameof(reason))))
    {
        Scenario = scenario;
        Reason = reason;
    }

    /// <summary>The scenario's name.</summary>
    public string Scenario { get; }

    /// <summary>Why the configuration or the world cannot give the scenario, without the scenario's name.</summary>
    public string Reason { get; }
}
