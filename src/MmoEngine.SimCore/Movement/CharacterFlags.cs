using System;

namespace MmoEngine.SimCore.Movement;

/// <summary>A character state's flags (data-model.md#reason-and-outcome-codes).</summary>
[Flags]
public enum CharacterFlags : byte
{
    /// <summary>Bit 0: the character stands on walkable ground.</summary>
    Grounded = 1,

    /// <summary>Bit 1: a boundary plane or the safety clamp stopped the character this step.</summary>
    ClampedToBounds = 2,
}
