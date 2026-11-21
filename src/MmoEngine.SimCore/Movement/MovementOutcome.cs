using System;

namespace MmoEngine.SimCore.Movement;

/// <summary>What happened to a character's movement this step, as flags (data-model.md#reason-and-outcome-codes).</summary>
[Flags]
public enum MovementOutcome : byte
{
    /// <summary>Nothing to report.</summary>
    None = 0,

    /// <summary>Crouch was released but there is no room to stand.</summary>
    HeadroomBlocked = 1,

    /// <summary>Still overlapping after four push-out iterations.</summary>
    Stuck = 2,

    /// <summary>A slide used <c>maxSlideIterations</c> sweeps with motion left.</summary>
    SlideCapReached = 4,

    /// <summary>A boundary plane or the safety clamp stopped the character.</summary>
    ClampedToBounds = 8,
}
