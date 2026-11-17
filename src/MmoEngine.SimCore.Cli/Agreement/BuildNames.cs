namespace MmoEngine.SimCore.Cli.Agreement;

/// <summary>
/// The agreement suite's build names (specs/015-shared-simulation-core/contracts/simcore-cli.md#build-names):
/// the seven WebAssembly builds <c>agreement compare</c> knows, in contract order, and the default
/// <c>--require</c>. <c>server</c> is the reference every build is compared with, so it is not one of them.
/// </summary>
internal static class BuildNames
{
    /// <summary>The server build's folder in a work directory, the reference.</summary>
    public const string Server = "server";

    /// <summary>The default <c>--require</c> of <c>agreement compare</c>.</summary>
    public const string DefaultRequire = "node-interp,node-interp-nojit,chromium-worker";

    /// <summary>The known builds, in contract order: the order <c>agreement compare</c> reports them in.</summary>
    public static IReadOnlyList<string> Known { get; } = Array.AsReadOnly(
    [
        "node-interp",
        "node-interp-nojit",
        "chromium-worker",
        "firefox-worker",
        "webkit-worker",
        "node-interp-relinked",
        "node-aot",
    ]);

    /// <summary>
    /// Parses a comma-separated <c>--require</c> list. True with the builds in the order given, each once,
    /// when every name is a known build; false with the first name that is not (an empty entry included).
    /// </summary>
    public static bool TryParseRequire(string require, out IReadOnlyList<string> builds, out string unknown)
    {
        ArgumentNullException.ThrowIfNull(require);
        var parsed = new List<string>();
        foreach (var name in require.Split(','))
        {
            if (!Known.Contains(name, StringComparer.Ordinal))
            {
                builds = [];
                unknown = name;
                return false;
            }

            if (!parsed.Contains(name, StringComparer.Ordinal))
            {
                parsed.Add(name);
            }
        }

        builds = parsed;
        unknown = string.Empty;
        return true;
    }
}
