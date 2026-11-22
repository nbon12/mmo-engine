namespace MmoEngine.SimCore.Wasm.Tests.Support;

/// <summary>
/// Runs the tests that call the host's exports one at a time. Every export delegates to the one static
/// <c>BatchStepper</c> in <c>SimCoreExports</c> (contracts/wasm-api.md#host-exports), so a test that loads
/// it (<c>LoadCollision</c>, <c>LoadConfig</c>, <c>Reset</c>) and then steps it or reads its buffers would
/// see another test's load calls land in between if the two interleaved.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SharedStepperCollection
{
    public const string Name = "Shared stepper (the host's one static BatchStepper)";
}
