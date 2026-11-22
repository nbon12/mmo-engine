using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Movement;

/// <summary>
/// What one <see cref="MovementStep.Step"/> call reports besides the character it writes: its outcome
/// flags, the work it did (the counters a world step sums into <c>StepReport.Counters</c>, and that
/// sub-spec 5 aggregates), and its fault. A faulted step wrote nothing to the character.
/// </summary>
public readonly struct MovementResult
{
    /// <summary>A result with the given outcome, counters and fault.</summary>
    internal MovementResult(MovementOutcome outcome, int subMoves, int sweeps, int slideCapHits, StepFaultCode fault)
    {
        Outcome = outcome;
        SubMoves = subMoves;
        Sweeps = sweeps;
        SlideCapHits = slideCapHits;
        Fault = fault;
    }

    /// <summary>HeadroomBlocked, Stuck, SlideCapReached and ClampedToBounds, as they happened this step.</summary>
    public MovementOutcome Outcome { get; }

    /// <summary>The sub-moves run, 1 to <c>movement.maxSubMoves</c> (fewer when a fault stopped the step).</summary>
    public int SubMoves { get; }

    /// <summary>
    /// The capsule sweeps run: per sub-move at most <c>maxSlideIterations</c> slide sweeps and a step-up of
    /// at most 3 + <c>maxSlideIterations</c>, plus the ground probe's one.
    /// </summary>
    public int Sweeps { get; }

    /// <summary>The slides that used every one of their <c>maxSlideIterations</c> sweeps with motion left.</summary>
    public int SlideCapHits { get; }

    /// <summary>
    /// <see cref="StepFaultCode.None"/>, or <see cref="StepFaultCode.QueryCapacityExceeded"/> when a
    /// collision query overflowed the candidate buffer; the step then left the character unchanged.
    /// </summary>
    public StepFaultCode Fault { get; }
}
