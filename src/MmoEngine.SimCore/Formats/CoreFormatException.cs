using System;

namespace MmoEngine.SimCore.Formats;

/// <summary>
/// Thrown when a decoder refuses its input (specs/015-shared-simulation-core/contracts/collision-data-format.md#api).
/// It carries the <see cref="FormatError"/>, and its message is the error's message.
/// </summary>
public sealed class CoreFormatException : Exception
{
    public CoreFormatException(FormatError error)
        : base(error.ToString()) => Error = error;

    /// <summary>Why the input was refused.</summary>
    public FormatError Error { get; }
}
