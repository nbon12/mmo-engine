using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;

namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// Configurations for tests, all derived from the committed placeholders in
/// <c>simcore/config/default.json</c> (contracts/config-rules.md#the-configuration-file). The file is
/// opened read-only through <see cref="RepoPaths"/> and bound by <see cref="SimConfigJson"/>, the
/// server's binder, so a test sees exactly the values a server would load. Every call returns a new
/// <see cref="SimConfig"/>, so a test may change what it gets without affecting any other test.
/// </summary>
public static class TestConfigs
{
    /// <summary>A fresh <see cref="SimConfig"/> bound from <c>simcore/config/default.json</c>.</summary>
    /// <exception cref="InvalidOperationException">The file does not bind; the message lists every error.</exception>
    public static SimConfig Default()
    {
        var load = SimConfigJson.Parse(RepoPaths.ReadAllText("simcore", "config", "default.json"));
        if (load.Config is null || load.Errors.Count != 0)
        {
            throw new InvalidOperationException(
                "simcore/config/default.json does not bind: " + string.Join("; ", load.Errors));
        }

        return load.Config;
    }

    /// <summary>A copy of <see cref="Default"/> with <c>stepRateHz</c> set to <paramref name="hz"/>.</summary>
    public static SimConfig At(int hz) => With(config => config.StepRateHz = hz);

    /// <summary>A copy of <see cref="Default"/> with <paramref name="edit"/> applied to it.</summary>
    public static SimConfig With(Action<SimConfig> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var config = Default().Clone();
        edit(config);
        return config;
    }

    /// <summary><see cref="CompiledConfig.Create"/> of <see cref="At"/>.</summary>
    public static CompiledConfig Compiled(int hz) => CompiledConfig.Create(At(hz));
}
