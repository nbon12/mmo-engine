using System;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using MmoEngine.SimCore.Batch;

namespace MmoEngine.SimCore.Wasm;

[SupportedOSPlatform("browser")]
public static partial class SimCoreExports
{
    public const int CommandCapacity = 32;

    private static readonly BatchStepper Stepper = new(CommandCapacity);

    [JSExport]
    public static int LoadCollision(byte[] file) => (int)Stepper.LoadCollision(file);

    [JSExport]
    public static int LoadConfig(byte[] configBlock) => (int)Stepper.LoadConfig(configBlock);

    [JSExport]
    public static int Reset(byte[] initialState, [JSMarshalAs<JSType.BigInt>] long matchSalt) =>
        (int)Stepper.Reset(initialState, unchecked((ulong)matchSalt));

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<byte> CommandBuffer() => Stepper.CommandSegment;

    [JSExport]
    [return: JSMarshalAs<JSType.MemoryView>]
    public static ArraySegment<byte> ReportBuffer() => Stepper.ReportSegment;

    [JSExport]
    public static int StepQueued(int count) => Stepper.StepQueued(count);

    [JSExport]
    public static byte[] RunStream(byte[] stream, byte[] collision, int perturbStep) =>
        StreamRunner.Run(stream, collision, perturbStep);

    [JSExport]
    public static byte[] EvaluateTables(byte[] inputs, byte[] configBlock) =>
        CrossBuildTables.Evaluate(inputs, configBlock);
}
