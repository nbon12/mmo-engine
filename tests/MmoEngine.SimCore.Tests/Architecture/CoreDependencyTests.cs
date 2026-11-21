using System.Reflection;
using System.Xml.Linq;
using MmoEngine.SimCore.Tests.Support;

namespace MmoEngine.SimCore.Tests.Architecture;

/// <summary>
/// The shared simulation core depends on nothing but the base class library and System.IO.Hashing,
/// so the continent server and the browser build reuse it unchanged (plan.md, Structure Decision),
/// and its project keeps the settings the determinism rules rely on: trimmable and AOT-compatible, no
/// unsafe code, no implicit usings, and the determinism analyzer attached as an analyzer only
/// (contracts/analyzer-rules.md#wiring).
/// </summary>
public sealed class CoreDependencyTests
{
    private const string HashingPackage = "System.IO.Hashing";

    private const string AnalyzerProject = @"..\MmoEngine.SimCore.Analyzers\MmoEngine.SimCore.Analyzers.csproj";

    private static readonly Assembly Core = typeof(SimCoreVersion).Assembly;

    /// <summary>The shared-framework directory the runtime loads the BCL from.</summary>
    private static readonly string FrameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    /// <summary>
    /// Every assembly the core references loads from the shared framework, except exactly one,
    /// System.IO.Hashing; no reference is a project assembly; and the project file declares exactly
    /// one package reference, System.IO.Hashing.
    /// </summary>
    [Fact]
    public void CoreReferencesOnlyTheBclAndSystemIOHashing()
    {
        var references = Core.GetReferencedAssemblies();
        Assert.NotEmpty(references);

        // A System.* name is not enough: packages such as System.IO.Hashing use one too. A BCL
        // assembly loads from the shared framework, not from the application's own folder.
        var outsideFramework = new List<(string Name, string Location)>();
        foreach (var reference in references)
        {
            var loaded = Assembly.Load(reference);
            var directory = Path.GetDirectoryName(loaded.Location);
            if (!string.Equals(directory, FrameworkDirectory, StringComparison.Ordinal))
            {
                outsideFramework.Add((reference.Name ?? reference.FullName, loaded.Location));
            }
        }

        Assert.True(
            outsideFramework.Count == 1 && outsideFramework[0].Name == HashingPackage,
            $"{Core.GetName().Name} must load every reference from the shared framework {FrameworkDirectory} except exactly one, {HashingPackage}; outside the framework: [{string.Join("; ", outsideFramework.Select(r => $"{r.Name} from {r.Location}"))}]");

        var projectAssemblies = references
            .Select(r => r.Name ?? string.Empty)
            .Where(n => n.StartsWith("MmoEngine", StringComparison.Ordinal))
            .ToArray();
        Assert.True(projectAssemblies.Length == 0, $"{Core.GetName().Name} must not reference any project assembly: {string.Join(", ", projectAssemblies)}");

        var packages = Items(LoadCoreProject(), "PackageReference");
        var package = Assert.Single(packages);
        Assert.Equal(HashingPackage, (string?)package.Attribute("Include"));
    }

    /// <summary>
    /// The core project is trimmable and AOT-compatible, allows no unsafe code, disables implicit
    /// usings, attaches the determinism analyzer privately as an analyzer only, and lets the tests see
    /// its internals.
    /// </summary>
    [Fact]
    public void CoreProjectKeepsTheDeterminismSettings()
    {
        var project = LoadCoreProject();

        AssertProperty(project, "IsTrimmable", "true");
        AssertProperty(project, "IsAotCompatible", "true");
        AssertProperty(project, "AllowUnsafeBlocks", "false");
        AssertProperty(project, "ImplicitUsings", "disable");

        var reference = Assert.Single(Items(project, "ProjectReference"));
        Assert.Equal(AnalyzerProject, (string?)reference.Attribute("Include"));
        Assert.Equal("Analyzer", (string?)reference.Attribute("OutputItemType"));
        Assert.Equal("false", (string?)reference.Attribute("ReferenceOutputAssembly"));
        Assert.Equal("all", (string?)reference.Attribute("PrivateAssets"));

        var internalsVisibleTo = Assert.Single(Items(project, "InternalsVisibleTo"));
        Assert.Equal("MmoEngine.SimCore.Tests", (string?)internalsVisibleTo.Attribute("Include"));
    }

    private static XDocument LoadCoreProject()
    {
        using var stream = RepoPaths.OpenRead("src", "MmoEngine.SimCore", "MmoEngine.SimCore.csproj");
        return XDocument.Load(stream);
    }

    private static XElement[] Items(XDocument project, string item) =>
        project.Descendants().Where(e => e.Name.LocalName == item).ToArray();

    private static void AssertProperty(XDocument project, string name, string expected)
    {
        var values = Items(project, name).Select(e => e.Value.Trim()).ToArray();
        Assert.True(
            values.Length == 1 && values[0] == expected,
            $"src/MmoEngine.SimCore/MmoEngine.SimCore.csproj must set {name} to {expected} exactly once; found [{string.Join(", ", values)}]");
    }
}
