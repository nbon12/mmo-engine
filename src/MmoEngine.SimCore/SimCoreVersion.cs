namespace MmoEngine.SimCore;

/// <summary>
/// The version of the shared simulation core's rules (specs/015-shared-simulation-core/data-model.md).
/// A recorded stream carries the rules revision it was recorded under, and a stream recorded under
/// another revision is reported as not comparable rather than diverged.
/// </summary>
public static class SimCoreVersion
{
    /// <summary>
    /// The rules revision. It goes up whenever a change makes the same inputs give a different state:
    /// a step rule, a configuration conversion, or a code that can appear in the hashed state
    /// (data-model.md#reason-and-outcome-codes).
    /// </summary>
    public const uint RulesRevision = 1;
}
