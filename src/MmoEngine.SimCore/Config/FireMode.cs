namespace MmoEngine.SimCore.Config;

/// <summary>
/// A weapon's fire mode (data-model.md#reason-and-outcome-codes). It is a <see cref="uint"/>, because the
/// config block carries it as a 32-bit <c>valueBits</c>.
/// </summary>
public enum FireMode : uint
{
    /// <summary>Fires while the trigger is held.</summary>
    Auto = 0,

    /// <summary>Fires once per trigger press.</summary>
    Semi = 1,
}
