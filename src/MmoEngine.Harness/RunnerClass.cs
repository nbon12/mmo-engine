using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace MmoEngine.Harness;

/// <summary>
/// A runner class (research R5): the fields that make per-tick hashes and timings comparable. These are
/// the architecture, the CPU model, the .NET major version, and the pinned hardware-intrinsic settings
/// and supported-ISA flags. The OS description is left out, because hosted runners update their kernel
/// often. The ID is the first 16 hex digits of the XxHash64 of the canonical fields, prefixed with the
/// class name from <c>MMO_RUNNER_CLASS</c> when that is set.
/// </summary>
public sealed record RunnerClass(string Id, JsonObject Fields)
{
    /// <summary>The environment variable naming the runner class.</summary>
    public const string NameVariable = "MMO_RUNNER_CLASS";

    /// <summary>The runner class recorded in a run manifest's <c>runtime</c> object.</summary>
    public static RunnerClass FromManifest(JsonObject manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var runtime = manifest["runtime"] as JsonObject ?? new JsonObject();
        var intrinsics = runtime["hardwareIntrinsics"] as JsonObject ?? new JsonObject();
        var fields = new JsonObject
        {
            ["architecture"] = Text(runtime, "architecture"),
            ["cpuModel"] = Text(runtime, "cpuModel"),
            ["dotnetMajor"] = Major(Text(runtime, "framework")),
            ["intrinsics"] = Canonical(intrinsics),
        };
        return Create(Text(runtime, "runnerClass", ""), fields);
    }

    /// <summary>The runner class of the current process.</summary>
    public static RunnerClass Current()
    {
        var intrinsics = new JsonObject();
        foreach (var name in IntrinsicVariables)
        {
            intrinsics[name] = Environment.GetEnvironmentVariable(name) ?? "unset";
        }

        intrinsics["Sse42.IsSupported"] = System.Runtime.Intrinsics.X86.Sse42.IsSupported;
        intrinsics["Avx2.IsSupported"] = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        intrinsics["Avx512F.IsSupported"] = System.Runtime.Intrinsics.X86.Avx512F.IsSupported;
        intrinsics["AdvSimd.IsSupported"] = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported;
        intrinsics["Vector<byte>.Count"] = System.Numerics.Vector<byte>.Count;
        var fields = new JsonObject
        {
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["cpuModel"] = CpuModel(),
            ["dotnetMajor"] = Environment.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["intrinsics"] = Canonical(intrinsics),
        };
        return Create(Environment.GetEnvironmentVariable(NameVariable) ?? "", fields);
    }

    /// <summary>The pinned hardware-intrinsic variables the manifest records (FR-017).</summary>
    public static IReadOnlyList<string> IntrinsicVariables { get; } =
    [
        "DOTNET_EnableHWIntrinsic", "DOTNET_EnableSSE42", "DOTNET_EnableAVX", "DOTNET_EnableAVX2",
        "DOTNET_EnableAVX512F", "DOTNET_EnableArm64AdvSimd", "DOTNET_PreferredVectorBitWidth",
        "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun",
    ];

    /// <summary>The CPU model: <c>/proc/cpuinfo</c>'s "model name" on Linux, else "unknown".</summary>
    public static string CpuModel() => CpuModel("/proc/cpuinfo");

    internal static string CpuModel(string cpuInfoPath)
    {
        if (!File.Exists(cpuInfoPath))
        {
            return "unknown";
        }

        var line = File.ReadLines(cpuInfoPath)
            .FirstOrDefault(l => (l.StartsWith("model name", StringComparison.Ordinal) || l.StartsWith("Model", StringComparison.Ordinal)) && l.Contains(':', StringComparison.Ordinal));
        return line is null ? "unknown" : line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    private static RunnerClass Create(string name, JsonObject fields)
    {
        var canonical = fields.ToJsonString();
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(canonical));
        var hex = hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        var id = string.IsNullOrWhiteSpace(name) ? hex : $"{name.Trim()}-{hex}";
        return new RunnerClass(id, fields);
    }

    private static JsonObject Canonical(JsonObject source)
    {
        var sorted = new JsonObject();
        foreach (var (key, value) in source.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sorted[key] = value?.ToJsonString();
        }

        return sorted;
    }

    private static string Major(string framework)
    {
        // ".NET 10.0.0" -> "10"
        var digits = framework.SkipWhile(c => !char.IsAsciiDigit(c)).TakeWhile(char.IsAsciiDigit).ToArray();
        return digits.Length == 0 ? "unknown" : new string(digits);
    }

    private static string Text(JsonObject o, string name, string fallback = "unknown") =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
}
