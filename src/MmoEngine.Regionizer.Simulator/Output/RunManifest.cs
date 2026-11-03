using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MmoEngine.Regionizer.Simulator.Config;

namespace MmoEngine.Regionizer.Simulator.Output;

/// <summary>Builds <c>manifest.json</c> (contracts/run-output.md): what is needed to reproduce and compare a run.</summary>
public static class RunManifest
{
    /// <summary>The run ID: <c>&lt;scenario&gt;-s&lt;sectionSize&gt;-r&lt;infantry radius&gt;-seed&lt;seed&gt;-&lt;first 8 hex of the config hash&gt;</c>.</summary>
    public static string RunId(LoadedRunConfig loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var c = loaded.Config;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{c.Scenario.ToLowerInvariant()}-s{c.Regionizer.SectionSize}-r{c.Regionizer.ClaimRadius.Infantry:R}-seed{c.Seed}-{loaded.ConfigHash[..8]}");
    }

    /// <summary>The manifest of a run; <paramref name="finishedUtc"/> and <paramref name="ticks"/> are filled in when it ends.</summary>
    public static JsonObject Build(LoadedRunConfig loaded, string runId, string gitSha, DateTime startedUtc, DateTime? finishedUtc, long ticks)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        var c = loaded.Config;
        return new JsonObject
        {
            ["formatVersion"] = RunFolder.FormatVersion,
            ["runId"] = runId,
            ["kind"] = "simulator",
            ["gitSha"] = gitSha,
            ["configHash"] = loaded.ConfigHash,
            ["scenario"] = c.Scenario,
            ["seed"] = c.Seed,
            ["scheduling"] = c.Scheduling.Mode.ToString(),
            ["environment"] = loaded.EnvironmentName,
            ["parameters"] = JsonNode.Parse(loaded.CanonicalJson),
            ["runtime"] = Runtime(),
            ["startedUtc"] = Utc(startedUtc),
            ["finishedUtc"] = finishedUtc is { } f ? Utc(f) : null,
            ["ticks"] = ticks,
            ["metricsEveryTicks"] = c.Metrics.MetricsEveryTicks,
            ["islandCheckEveryTicks"] = c.Metrics.IslandCheckEveryTicks,
            ["crossRegionPairsEveryTicks"] = c.Metrics.CrossRegionPairsEveryTicks,
        };
    }

    /// <summary>The runtime and hardware-intrinsic settings, which bookkeeping cost depends on (research sources).</summary>
    public static JsonObject Runtime()
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
        intrinsics["Vector<byte>.Count"] = Vector<byte>.Count;

        return new JsonObject
        {
            ["framework"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["processorCount"] = Environment.ProcessorCount,
            ["machineName"] = Environment.MachineName,
            ["hardwareIntrinsics"] = intrinsics,
            ["vectorIsHardwareAccelerated"] = Vector.IsHardwareAccelerated,
        };
    }

    /// <summary>The source revision: <c>git rev-parse HEAD</c> in <paramref name="workingDirectory"/>, or "unknown".</summary>
    public static string GitSha(string? workingDirectory = null)
    {
        string[] candidates = File.Exists("/usr/bin/git") ? ["/usr/bin/git", "git"] : ["git"];
        var directory = workingDirectory ?? Environment.CurrentDirectory;
        return candidates.Select(git => TryGit(git, directory)).FirstOrDefault(sha => sha is not null) ?? "unknown";
    }

    private static readonly string[] IntrinsicVariables =
    [
        "DOTNET_EnableHWIntrinsic", "DOTNET_EnableSSE42", "DOTNET_EnableAVX", "DOTNET_EnableAVX2",
        "DOTNET_EnableAVX512F", "DOTNET_EnableArm64AdvSimd", "DOTNET_PreferredVectorBitWidth",
        "DOTNET_TieredCompilation", "DOTNET_TieredPGO", "DOTNET_ReadyToRun",
    ];

    private static string Utc(DateTime value) => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string? TryGit(string git, string workingDirectory)
    {
        try
        {
            var start = new ProcessStartInfo(git, "rev-parse HEAD")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
            {
                return null;
            }

            return output.Length == 40 && output.All(char.IsAsciiHexDigitLower) ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
