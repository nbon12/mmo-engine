using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Wasm.Tests.Support;

/// <summary>
/// The default config block: <c>simcore/config/default.json</c>, opened read-only through
/// <see cref="RepoPaths"/>, bound by <see cref="SimConfigJson"/> (the server's binder, as the CLI binds
/// <c>--config</c>) and encoded with <see cref="ConfigBlock.Encode"/>. It is the block
/// <c>agreement prepare</c> writes to <c>tables/config.bin</c> (contracts/simcore-cli.md#agreement-work-directory).
/// </summary>
public static class DefaultConfig
{
    /// <summary>A new array holding the default config block.</summary>
    /// <exception cref="InvalidOperationException">The file does not bind; the message lists every error.</exception>
    public static byte[] Block()
    {
        var load = SimConfigJson.Parse(RepoPaths.ReadAllText("simcore", "config", "default.json"));
        if (load.Config is null || load.Errors.Count != 0)
        {
            throw new InvalidOperationException("simcore/config/default.json does not bind: " + string.Join("; ", load.Errors));
        }

        return ConfigBlock.Encode(load.Config);
    }
}
