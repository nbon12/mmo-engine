namespace MmoEngine.SimCore.Batch;

/// <summary>
/// The negative codes of <see cref="BatchStepper.StepQueued"/> and of its stop entry
/// (specs/015-shared-simulation-core/contracts/wasm-api.md#return-codes). <see cref="NotReady"/> and
/// <see cref="CountOutOfRange"/> are returned with a zero report header and no step run;
/// <see cref="CommandRefused"/> and <see cref="StepFault"/> are the codes of the stop entry that follows the
/// committed records after an early stop.
/// </summary>
public enum StepError
{
    /// <summary>No world: <c>Reset</c> has not returned <c>Ok</c> since the last load call.</summary>
    NotReady = -1,

    /// <summary>The count is below 0 or above <c>CommandCapacity</c>.</summary>
    CountOutOfRange = -2,

    /// <summary>A queued command failed to decode.</summary>
    CommandRefused = -3,

    /// <summary>The step after the committed ones produced a non-finite value, or a collision query overflowed.</summary>
    StepFault = -4,
}
