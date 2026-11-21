using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Scenarios;

/// <summary>
/// The scenario catalog and the committed streams are one set (contracts/simcore-cli.md#named-scenario-streams,
/// contracts/recorded-stream-format.md): <see cref="ScenarioCatalog.Names"/> lists the 19 named scenarios in
/// ordinal order, and <c>simcore/streams/</c> holds exactly one recorded stream for each of them and no other.
/// The directory is only listed, through <see cref="RepoPaths"/>, never written.
/// </summary>
public sealed class ScenarioCatalogTests
{
    private const string StreamExtension = ".mmrs";

    /// <summary>The 19 named scenarios of contracts/recorded-stream-format.md, in ordinal order.</summary>
    private static readonly string[] NineteenNames =
    [
        "corner-wedge",
        "crouch-under-ledge",
        "flat-run-20hz",
        "flat-run-30hz",
        "flat-run-60hz",
        "fuzz-all-buttons",
        "heat-overheat",
        "jump-land",
        "launcher-arc",
        "mesh-occludes-shot",
        "projectile-hits-hitbox",
        "reload-and-empty",
        "rifle-burst",
        "shots-at-known-distances",
        "slope-limit",
        "stairs-blocked",
        "stairs-climb",
        "thin-wall-fast",
        "wall-slide-45",
    ];

    /// <summary>
    /// Given the scenario catalog and the committed <c>simcore/streams/</c> directory, When the catalog's names
    /// and the directory's <c>.mmrs</c> files are listed, Then the names are exactly the 19 of
    /// contracts/recorded-stream-format.md in ordinal order, and the directory holds exactly one
    /// <c>&lt;name&gt;.mmrs</c> for each of them and no other <c>.mmrs</c> file.
    /// </summary>
    [Fact]
    public void CatalogAndCommittedStreamsAreTheSameNineteen()
    {
        Assert.True(
            NineteenNames.Order(StringComparer.Ordinal).SequenceEqual(NineteenNames),
            "The expected names must themselves be in ordinal order.");
        var names = ScenarioCatalog.Names.ToArray();
        Assert.True(
            names.SequenceEqual(NineteenNames, StringComparer.Ordinal),
            $"ScenarioCatalog.Names is [{string.Join(", ", names)}]; expected the 19 named scenarios [{string.Join(", ", NineteenNames)}] in ordinal order "
                + $"(missing [{string.Join(", ", NineteenNames.Except(names, StringComparer.Ordinal))}], unexpected [{string.Join(", ", names.Except(NineteenNames, StringComparer.Ordinal))}])");

        var directory = RepoPaths.Combine("simcore", "streams");
        var streams = Directory.EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(file => string.Equals(Path.GetExtension(file), StreamExtension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = NineteenNames.Select(name => name + StreamExtension).ToArray();
        Assert.True(
            streams.SequenceEqual(expected, StringComparer.Ordinal),
            $"{directory} holds [{string.Join(", ", streams)}]; expected exactly one {StreamExtension} per named scenario "
                + $"(missing [{string.Join(", ", expected.Except(streams, StringComparer.Ordinal))}], unexpected [{string.Join(", ", streams.Except(expected, StringComparer.Ordinal))}])");
    }
}
