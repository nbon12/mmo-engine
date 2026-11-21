using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Server;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// The content the benchmarks and the allocation drivers build their cases from: the committed base
/// configuration and the generated test world, each created once per process. It reads no file from
/// disk: the configuration is <c>simcore/config/default.json</c> embedded in this assembly, and the world
/// is what <see cref="TestWorldGenerator"/> produces, put through the file format. So BenchmarkDotNet's
/// out-of-process toolchain finds the content from any working directory.
/// <c>StepCaseTests.ContentMatchesTheCommittedConfigAndWorld</c> checks both against the committed files.
/// </summary>
public static class SimCoreContent
{
    /// <summary>The logical name of the embedded <c>simcore/config/default.json</c>.</summary>
    public const string ConfigResourceName = "MmoEngine.SimCore.Benchmarks.default.json";

    private static readonly Lazy<SimConfig> BaseConfigValue = new(LoadBaseConfig);

    private static readonly Lazy<CollisionWorld> WorldValue = new(
        static () => CollisionFile.Decode(CollisionFile.Encode(TestWorldGenerator.Generate())));

    /// <summary>
    /// The embedded <c>simcore/config/default.json</c>, bound with <see cref="SimConfigJson"/>. It is one
    /// instance shared by every caller: change a <see cref="SimConfig.Clone"/> of it, never it.
    /// </summary>
    public static SimConfig BaseConfig => BaseConfigValue.Value;

    /// <summary>
    /// The test world: <c>CollisionFile.Decode(CollisionFile.Encode(TestWorldGenerator.Generate()))</c>. A
    /// <see cref="CollisionWorld"/> is immutable, so it is shared.
    /// </summary>
    public static CollisionWorld World => WorldValue.Value;

    /// <summary><see cref="CompiledConfig.Create"/> of a copy of <see cref="BaseConfig"/> with <c>stepRateHz</c> = <paramref name="hz"/>.</summary>
    /// <exception cref="ConfigValidationException">The configuration is invalid at <paramref name="hz"/>.</exception>
    public static CompiledConfig Compiled(int hz)
    {
        var config = BaseConfig.Clone();
        config.StepRateHz = hz;
        return CompiledConfig.Create(config);
    }

    private static SimConfig LoadBaseConfig()
    {
        using var resource = typeof(SimCoreContent).Assembly.GetManifestResourceStream(ConfigResourceName)
            ?? throw new InvalidOperationException($"The embedded resource {ConfigResourceName} is missing.");
        using var reader = new StreamReader(resource);
        var load = SimConfigJson.Parse(reader.ReadToEnd());
        if (load.Config is null || load.Errors.Count != 0)
        {
            throw new InvalidOperationException(
                $"The embedded {ConfigResourceName} does not bind: " + string.Join("; ", load.Errors));
        }

        return load.Config;
    }
}
