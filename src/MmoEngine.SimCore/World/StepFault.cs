using MmoEngine.SimCore.Formats;

namespace MmoEngine.SimCore.World;

/// <summary>
/// Why a step was refused, and where (specs/015-shared-simulation-core/data-model.md#stepfault): the
/// code, and the field as the state layout table numbers it (<see cref="FieldOrdinal"/>) with the
/// element's index in its list (<see cref="ElementIndex"/>, its position in ascending ID order).
/// Recording a fault allocates nothing; only reading <see cref="Path"/> formats the text.
/// <c>default(StepFault)</c> is <see cref="StepFaultCode.None"/>: the step committed.
/// </summary>
/// <param name="Code">None, NonFinite or QueryCapacityExceeded.</param>
/// <param name="FieldOrdinal">
/// The field's ordinal in <see cref="StateSerializer.Layout"/>. A <see cref="StepFaultCode.QueryCapacityExceeded"/>
/// fault names the moving character by its <c>id</c> field, such as <c>characters[0].id</c>.
/// </param>
/// <param name="ElementIndex">
/// The element's index in its list; for a weapon field, the character index × 8 + the slot
/// (<see cref="StateSerializer.FieldPath"/>); 0 for a header or count field.
/// </param>
public readonly record struct StepFault(StepFaultCode Code, int FieldOrdinal, int ElementIndex)
{
    /// <summary>
    /// The field's path through the state layout, such as <c>characters[3].velocity.y</c> or
    /// <c>events.hits[0].t</c>; empty when the step committed. Formatted when read.
    /// </summary>
    public string Path => Code == StepFaultCode.None ? string.Empty : StateSerializer.FieldPath(FieldOrdinal, ElementIndex);
}
