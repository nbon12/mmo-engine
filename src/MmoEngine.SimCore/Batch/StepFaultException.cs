using System;
using System.Globalization;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Batch;

/// <summary>
/// Thrown by <see cref="StreamRunner"/> when a step of a recorded stream faults
/// (specs/015-shared-simulation-core/contracts/wasm-api.md#streamrunner): it carries the step number that
/// did not commit and the <see cref="StepFault"/>, and its message is
/// <c>step &lt;n&gt; faulted: &lt;code&gt; at &lt;path&gt;</c>, for example
/// <c>step 7 faulted: NonFinite at characters[0].velocity.y</c>.
/// </summary>
public sealed class StepFaultException : Exception
{
    /// <summary>An exception for <paramref name="fault"/> at step <paramref name="step"/>.</summary>
    public StepFaultException(uint step, StepFault fault)
        : base(string.Create(CultureInfo.InvariantCulture, $"step {step} faulted: {CodeName(fault.Code)} at {fault.Path}"))
    {
        Step = step;
        Fault = fault;
    }

    /// <summary>The step number that did not commit.</summary>
    public uint Step { get; }

    /// <summary>Why the step was refused, and the field it names.</summary>
    public StepFault Fault { get; }

    /// <summary>The code's name as data-model.md#reason-and-outcome-codes gives it; an unknown value prints as its number.</summary>
    private static string CodeName(StepFaultCode code) => code switch
    {
        StepFaultCode.None => "None",
        StepFaultCode.NonFinite => "NonFinite",
        StepFaultCode.QueryCapacityExceeded => "QueryCapacityExceeded",
        _ => ((byte)code).ToString(CultureInfo.InvariantCulture),
    };
}
