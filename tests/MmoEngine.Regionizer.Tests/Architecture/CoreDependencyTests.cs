using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Architecture;

/// <summary>
/// The regionizer core depends on nothing but the base class library, so the continent server can
/// reuse it unchanged (FR-015, SC-007).
/// </summary>
public sealed partial class CoreDependencyTests
{
    private static readonly Assembly Core = typeof(Regionizer).Assembly;

    /// <summary>The shared-framework directory the runtime loads the BCL from.</summary>
    private static readonly string FrameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    [Fact]
    public void CoreAssemblyReferencesOnlyBaseClassLibraryAssemblies()
    {
        var references = Core.GetReferencedAssemblies();
        Assert.NotEmpty(references);

        var offenders = new List<string>();
        foreach (var reference in references)
        {
            var name = reference.Name ?? string.Empty;
            if (!BclName().IsMatch(name))
            {
                offenders.Add($"{reference.FullName} (not a BCL assembly name)");
                continue;
            }

            // A System.* name is not enough: packages such as System.IO.Hashing use one too. A BCL
            // assembly loads from the shared framework, not from the application's own folder.
            var loaded = Assembly.Load(reference);
            var directory = Path.GetDirectoryName(loaded.Location);
            if (!string.Equals(directory, FrameworkDirectory, StringComparison.Ordinal))
            {
                offenders.Add($"{reference.FullName} (loaded from {loaded.Location}, outside the shared framework {FrameworkDirectory})");
            }
        }

        Assert.True(offenders.Count == 0, $"{Core.GetName().Name} must reference only BCL assemblies (FR-015): {string.Join("; ", offenders)}");
    }

    [Fact]
    public void CoreAssemblyReferencesNoProjectAssembly()
    {
        var project = Core.GetReferencedAssemblies()
            .Select(r => r.Name ?? string.Empty)
            .Where(n => n.StartsWith("MmoEngine", StringComparison.Ordinal))
            .ToArray();

        Assert.True(project.Length == 0, $"{Core.GetName().Name} must not reference the simulator or any other project (SC-007): {string.Join(", ", project)}");
    }

    [Theory]
    [InlineData("PackageReference")]
    [InlineData("ProjectReference")]
    [InlineData("Reference")]
    [InlineData("FrameworkReference")]
    public void CoreProjectFileDeclaresNoReference(string item)
    {
        var path = Path.Combine(Kit1SourceScan.RepoRoot(), "src", "MmoEngine.Regionizer", "MmoEngine.Regionizer.csproj");
        var project = XDocument.Load(path);

        var declared = project.Descendants().Where(e => e.Name.LocalName == item).Select(e => e.ToString()).ToArray();

        Assert.True(declared.Length == 0, $"{path} must declare no {item} (FR-015): {string.Join(" ", declared)}");
    }

    [GeneratedRegex(@"^(System(\..+)?|Microsoft\.Win32\..+|netstandard|mscorlib)$", RegexOptions.CultureInvariant)]
    private static partial Regex BclName();
}
