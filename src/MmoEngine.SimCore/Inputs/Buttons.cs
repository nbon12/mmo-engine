using System;

namespace MmoEngine.SimCore.Inputs;

/// <summary>
/// The buttons an input command holds, one bit each (contracts/input-command-format.md#buttons). Bits 9
/// to 15 are reserved and must be 0; a command with one set is rejected with
/// <see cref="InputRejection.UnknownButtons"/>.
/// </summary>
[Flags]
public enum Buttons : ushort
{
    /// <summary>Wish direction +forward.</summary>
    Forward = 0x0001,

    /// <summary>Wish direction −forward.</summary>
    Back = 0x0002,

    /// <summary>Wish direction −right.</summary>
    Left = 0x0004,

    /// <summary>Wish direction +right.</summary>
    Right = 0x0008,

    /// <summary>Level-triggered: jumps whenever held while grounded.</summary>
    Jump = 0x0010,

    /// <summary>Hold to crouch.</summary>
    Crouch = 0x0020,

    /// <summary>Hold to walk; run is the default, and crouch overrides both.</summary>
    Walk = 0x0040,

    /// <summary>Hold the trigger.</summary>
    Fire = 0x0080,

    /// <summary>Start a reload; level-triggered, ignored while the magazine is full or the reserve is empty.</summary>
    Reload = 0x0100,

    /// <summary>Every known button, bits 0 to 8.</summary>
    Known = 0x01FF,
}
