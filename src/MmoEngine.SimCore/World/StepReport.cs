using System;

namespace MmoEngine.SimCore.World;

/// <summary>
/// What one world step did, summed over its characters: the sweeps the movement step ran, the slides that
/// used every sweep with motion left, and the fire blocks. Tests use them to check every loop stays within
/// its bound (FR-005); they are not part of the hashed state.
/// </summary>
public readonly record struct StepCounters
{
    /// <summary>The collision sweeps the movement step ran.</summary>
    public int Sweeps { get; init; }

    /// <summary>The slides that reached <c>movement.maxSlideIterations</c> with motion left.</summary>
    public int SlideCapHits { get; init; }

    /// <summary>The fire blocks reported.</summary>
    public int FireBlocks { get; init; }
}

/// <summary>
/// What <c>SimWorld.Step</c> returns (specs/015-shared-simulation-core/data-model.md#stepreport). It is
/// a <c>ref struct</c> over the world's buffers, so returning it allocates nothing, and it stays valid
/// until the next call on the same world.
/// </summary>
public readonly ref struct StepReport
{
    internal StepReport(uint step, StepFault fault, ulong hash, ReadOnlySpan<byte> state, StepEvents events, StepCounters counters)
    {
        Step = step;
        Fault = fault;
        Hash = hash;
        State = state;
        Events = events;
        Counters = counters;
    }

    /// <summary>The new step number; the old one on a fault.</summary>
    public uint Step { get; }

    /// <summary>Code None when the step committed.</summary>
    public StepFault Fault { get; }

    /// <summary>XxHash64 (seed 0, one-shot) of <see cref="State"/>; 0 on a fault.</summary>
    public ulong Hash { get; }

    /// <summary>The canonical state with this step's events (contracts/state-serialization.md).</summary>
    public ReadOnlySpan<byte> State { get; }

    /// <summary>Typed read-only views of the six event lists.</summary>
    public StepEvents Events { get; }

    /// <summary>The step's loop counters.</summary>
    public StepCounters Counters { get; }
}
