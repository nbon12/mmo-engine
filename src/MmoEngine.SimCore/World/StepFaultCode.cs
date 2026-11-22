namespace MmoEngine.SimCore.World;

/// <summary>Why a step was refused (data-model.md#stepfault).</summary>
public enum StepFaultCode : byte
{
    /// <summary>The step committed.</summary>
    None = 0,

    /// <summary>A float in the next state or its events is NaN or infinite.</summary>
    NonFinite = 1,

    /// <summary>A collision query had more candidates than its buffer; a valid configuration makes this impossible.</summary>
    QueryCapacityExceeded = 2,
}
