using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// The determinism analyzer is wired so that it runs on the core, only on the core, and cannot be
/// switched off or silently dropped (FR-014), per
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md#wiring and #roslyn-pin. Each test runs
/// a private check function, which returns problem lines, over the real files read from the repository
/// root with <see cref="FileAccess.Read"/>, and over in-memory copies with one deliberate defect, so the
/// check cannot pass vacuously.
/// </summary>
public sealed class WiringTests
{
    private const string CoreProjectPath = "src/MmoEngine.SimCore/MmoEngine.SimCore.csproj";

    private const string BuildPropsPath = "Directory.Build.props";

    private const string AnalyzerTestsProjectPath = "tests/MmoEngine.SimCore.Analyzers.Tests/MmoEngine.SimCore.Analyzers.Tests.csproj";

    private const string CliProjectPath = "src/MmoEngine.SimCore.Cli/MmoEngine.SimCore.Cli.csproj";

    private const string GlobalJsonPath = "global.json";

    private const string PackagesPropsPath = "Directory.Packages.props";

    /// <summary>The analyzer project's file name, which a <c>ProjectReference</c> to it ends with.</summary>
    private const string AnalyzerProjectFile = "MmoEngine.SimCore.Analyzers.csproj";

    /// <summary>The analyzer's assembly, which an <c>&lt;Analyzer Include="…" /&gt;</c> item could attach directly.</summary>
    private const string AnalyzerAssemblyFile = "MmoEngine.SimCore.Analyzers.dll";

    private const string RoslynPackage = "Microsoft.CodeAnalysis.CSharp";

    /// <summary>The directories whose project files <see cref="OnlyTheCoreAttachesTheAnalyzer"/> enumerates.</summary>
    private static readonly string[] ProjectRoots = ["src", "tests", "benchmarks"];

    /// <summary>
    /// The compiler version each SDK feature band ships (contracts/analyzer-rules.md#roslyn-pin): an
    /// analyzer built against a newer Roslyn gets CS9057 and, without <c>-warnaserror</c>, silently does
    /// not run. Raise the pin only together with <c>global.json</c>'s band, and add the band here.
    /// </summary>
    private static readonly (string Band, string Compiler)[] CompilerByBand = [("10.0.1xx", "5.0.0")];

    /// <summary>
    /// FR-014: the core's project file attaches the analyzer through exactly one <c>ProjectReference</c>
    /// with <c>OutputItemType="Analyzer"</c>, <c>ReferenceOutputAssembly="false"</c> and
    /// <c>PrivateAssets="all"</c>; <c>ImplicitUsings</c> is <c>disable</c>; and neither it nor
    /// <c>Directory.Build.props</c> sets <c>RunAnalyzers</c> or <c>RunAnalyzersDuringBuild</c>, names an
    /// MMO rule in <c>NoWarn</c> or <c>WarningsNotAsErrors</c>, or has a <c>&lt;Compile Remove&gt;</c>. The
    /// <c>real</c> row checks the files as committed and expects no problem; each other row checks a copy
    /// with one defect and expects exactly that defect's problem line.
    /// </summary>
    [Theory]
    [InlineData("real")]
    [InlineData("private-assets-missing")]
    [InlineData("reference-output-assembly-true")]
    [InlineData("no-warn")]
    [InlineData("run-analyzers-false")]
    [InlineData("run-analyzers-during-build-false")]
    [InlineData("compile-remove")]
    [InlineData("implicit-usings-enable")]
    [InlineData("props-warnings-not-as-errors")]
    public void CoreProjectAttachesTheAnalyzerPrivately(string caseName)
    {
        var core = AnalyzerHarness.ReadRepoText(CoreProjectPath);
        var props = AnalyzerHarness.ReadRepoText(BuildPropsPath);

        var (mutatedCore, mutatedProps, expected) = caseName switch
        {
            "real" => (core, props, Array.Empty<string>()),
            "private-assets-missing" => (
                Mutate(core, d => RequiredAttribute(SingleAnalyzerReference(d), "PrivateAssets").Remove()),
                props,
                [CoreProjectPath + ": the analyzer ProjectReference has no PrivateAssets; it needs PrivateAssets=\"all\""]),
            "reference-output-assembly-true" => (
                Mutate(core, d => SingleAnalyzerReference(d).SetAttributeValue("ReferenceOutputAssembly", "true")),
                props,
                [CoreProjectPath + ": the analyzer ProjectReference has ReferenceOutputAssembly=\"true\"; it needs ReferenceOutputAssembly=\"false\""]),
            "no-warn" => (
                Mutate(core, d => AddProperty(d, "NoWarn", "MMO1002")),
                props,
                [CoreProjectPath + ": NoWarn names an MMO rule (MMO1002); no MMO rule may be silenced or lowered"]),
            "run-analyzers-false" => (
                Mutate(core, d => AddProperty(d, "RunAnalyzers", "false")),
                props,
                [CoreProjectPath + ": RunAnalyzers is set (false); nothing may turn the analyzer off for the core"]),
            "run-analyzers-during-build-false" => (
                Mutate(core, d => AddProperty(d, "RunAnalyzersDuringBuild", "false")),
                props,
                [CoreProjectPath + ": RunAnalyzersDuringBuild is set (false); nothing may turn the analyzer off for the core"]),
            "compile-remove" => (
                Mutate(core, d => AddItem(d, "Compile", "Remove", "Math/**")),
                props,
                [CoreProjectPath + ": <Compile Remove=\"Math/**\" /> takes sources out of the core's build; the analyzer must see every file"]),
            "implicit-usings-enable" => (
                Mutate(core, d => Assert.Single(Named(d, "ImplicitUsings")).Value = "enable"),
                props,
                [CoreProjectPath + ": ImplicitUsings is 'enable'; the core needs 'disable', so the backstop compiles its sources as the build does"]),
            "props-warnings-not-as-errors" => (
                core,
                Mutate(props, d => AddProperty(d, "WarningsNotAsErrors", "MMO1004")),
                [BuildPropsPath + ": WarningsNotAsErrors names an MMO rule (MMO1004); no MMO rule may be silenced or lowered"]),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown wiring case."),
        };

        Assert.Equal(expected, CoreWiringProblems(mutatedCore, mutatedProps));
    }

    /// <summary>
    /// FR-014: no project file under <c>src/</c>, <c>tests/</c> or <c>benchmarks/</c> (outside
    /// <c>bin/</c> and <c>obj/</c>) other than the core's attaches the analyzer, either through a
    /// <c>ProjectReference</c> with <c>OutputItemType="Analyzer"</c> or through an
    /// <c>&lt;Analyzer Include="…MmoEngine.SimCore.Analyzers.dll" /&gt;</c> item. The analyzer test
    /// project's own reference is a normal <c>ProjectReference</c>. The projects are enumerated, so a
    /// project added later is checked too. The <c>real</c> row checks the committed files; each other row
    /// gives one project one attachment and expects exactly that problem line.
    /// </summary>
    [Theory]
    [InlineData("real")]
    [InlineData("analyzer-tests-attach")]
    [InlineData("analyzer-item")]
    public void OnlyTheCoreAttachesTheAnalyzer(string caseName)
    {
        var projects = RepoProjects();
        var paths = projects.Select(p => p.Path).ToArray();
        Assert.Contains(CoreProjectPath, paths);
        Assert.Contains(AnalyzerTestsProjectPath, paths);
        Assert.Contains(CliProjectPath, paths);

        var testsReference = Assert.Single(AnalyzerProjectReferences(XDocument.Parse(Text(projects, AnalyzerTestsProjectPath))));
        Assert.True(
            Metadata(testsReference, "OutputItemType") is null,
            $"{AnalyzerTestsProjectPath} must reference the analyzer as a normal ProjectReference, without OutputItemType.");

        var (mutated, expected) = caseName switch
        {
            "real" => (projects, Array.Empty<string>()),
            "analyzer-tests-attach" => (
                Replace(projects, AnalyzerTestsProjectPath, text => Mutate(text, d => SingleAnalyzerReference(d).SetAttributeValue("OutputItemType", "Analyzer"))),
                [AnalyzerTestsProjectPath + ": attaches MmoEngine.SimCore.Analyzers.csproj with OutputItemType=\"Analyzer\"; only " + CoreProjectPath + " runs the determinism analyzer"]),
            "analyzer-item" => (
                Replace(projects, CliProjectPath, text => Mutate(text, d => AddItem(d, "Analyzer", "Include", @"..\MmoEngine.SimCore.Analyzers\bin\Release\netstandard2.0\MmoEngine.SimCore.Analyzers.dll"))),
                [CliProjectPath + ": attaches MmoEngine.SimCore.Analyzers.dll as an <Analyzer> item; only " + CoreProjectPath + " runs the determinism analyzer"]),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown attachment case."),
        };

        Assert.Equal(expected, AttachmentProblems(mutated));
    }

    /// <summary>
    /// FR-014: <c>Directory.Packages.props</c> pins <c>Microsoft.CodeAnalysis.CSharp</c> to the compiler
    /// version of <c>global.json</c>'s SDK band (10.0.1xx needs 5.0.0), so the analyzer cannot be built
    /// against a newer Roslyn than the SDK runs. The first row reads the real <c>global.json</c>
    /// <c>sdk.version</c> and the real pin and expects no problem; the others are in-memory pairs.
    /// </summary>
    [Theory]
    [InlineData(GlobalJsonPath, PackagesPropsPath, "")]
    [InlineData("10.0.115", "5.0.0", "")]
    [InlineData("10.0.115", "5.3.0", "Directory.Packages.props: Microsoft.CodeAnalysis.CSharp is 5.3.0; SDK band 10.0.1xx in global.json needs 5.0.0")]
    [InlineData("10.0.200", "5.0.0", "global.json: SDK band 10.0.2xx has no known compiler version; update the band table with the pin")]
    [InlineData("10.0", "5.0.0", "global.json: sdk.version '10.0' is not an SDK version of the form major.minor.patch")]
    public void RoslynPinMatchesTheSdkBand(string sdkVersion, string pin, string expectedProblem)
    {
        var version = sdkVersion == GlobalJsonPath ? RealSdkVersion() : sdkVersion;
        var package = pin == PackagesPropsPath ? RealRoslynPin() : pin;
        string[] expected = expectedProblem.Length == 0 ? [] : [expectedProblem];

        Assert.Equal(expected, PinProblems(version, package));
    }

    /// <summary>
    /// The problem lines of the core's analyzer wiring, given the core's project file and
    /// <c>Directory.Build.props</c>, which the core imports: the analyzer reference first, then the
    /// effective <c>ImplicitUsings</c> (the core's own last value, else the imported one), then, file by
    /// file in document order, every property or item that could turn the analyzer off, silence or lower
    /// an MMO rule, or take sources out of the build.
    /// </summary>
    private static string[] CoreWiringProblems(string coreProjectText, string buildPropsText)
    {
        var core = XDocument.Parse(coreProjectText);
        var props = XDocument.Parse(buildPropsText);
        var problems = new List<string>();

        var references = AnalyzerProjectReferences(core).ToArray();
        if (references.Length != 1)
        {
            problems.Add($"{CoreProjectPath}: has {references.Length} ProjectReferences to {AnalyzerProjectFile}; it needs exactly one");
        }
        else
        {
            RequireMetadata(references[0], "OutputItemType", "Analyzer", problems);
            RequireMetadata(references[0], "ReferenceOutputAssembly", "false", problems);
            RequireMetadata(references[0], "PrivateAssets", "all", problems);
        }

        var coreImplicitUsings = Named(core, "ImplicitUsings").LastOrDefault();
        var (implicitUsings, implicitUsingsSource) = coreImplicitUsings is not null
            ? (coreImplicitUsings.Value, CoreProjectPath)
            : (Named(props, "ImplicitUsings").LastOrDefault()?.Value, BuildPropsPath);
        if (!string.Equals(implicitUsings?.Trim(), "disable", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{implicitUsingsSource}: ImplicitUsings is '{implicitUsings ?? "(not set)"}'; the core needs 'disable', so the backstop compiles its sources as the build does");
        }

        foreach (var (path, document) in new[] { (CoreProjectPath, core), (BuildPropsPath, props) })
        {
            foreach (var element in document.Descendants())
            {
                var name = element.Name.LocalName;
                if (name is "RunAnalyzers" or "RunAnalyzersDuringBuild")
                {
                    problems.Add($"{path}: {name} is set ({element.Value.Trim()}); nothing may turn the analyzer off for the core");
                }
                else if (name is "NoWarn" or "WarningsNotAsErrors" && element.Value.Contains("MMO", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{path}: {name} names an MMO rule ({element.Value.Trim()}); no MMO rule may be silenced or lowered");
                }
                else if (name == "Compile" && element.Attribute("Remove") is { } remove)
                {
                    problems.Add($"{path}: <Compile Remove=\"{remove.Value}\" /> takes sources out of the core's build; the analyzer must see every file");
                }
            }
        }

        return [.. problems];
    }

    /// <summary>
    /// The problem lines for every project other than the core that attaches the analyzer, in the
    /// projects' order: a <c>ProjectReference</c> to the analyzer project with
    /// <c>OutputItemType="Analyzer"</c>, or an <c>Analyzer</c> item naming the analyzer's assembly.
    /// </summary>
    private static string[] AttachmentProblems(IReadOnlyList<(string Path, string Text)> projects)
    {
        var problems = new List<string>();
        foreach (var (path, text) in projects.Where(p => p.Path != CoreProjectPath))
        {
            var project = XDocument.Parse(text);
            foreach (var reference in AnalyzerProjectReferences(project))
            {
                if (string.Equals(Metadata(reference, "OutputItemType")?.Trim(), "Analyzer", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{path}: attaches {AnalyzerProjectFile} with OutputItemType=\"Analyzer\"; only {CoreProjectPath} runs the determinism analyzer");
                }
            }

            foreach (var item in Named(project, "Analyzer").Where(e => FileNameIs(e.Attribute("Include")?.Value, AnalyzerAssemblyFile)))
            {
                problems.Add($"{path}: attaches {AnalyzerAssemblyFile} as an <Analyzer> item; only {CoreProjectPath} runs the determinism analyzer");
            }
        }

        return [.. problems];
    }

    /// <summary>
    /// The problem line, if any, of a Roslyn pin against an SDK version: the version's feature band
    /// (<c>10.0.115</c> is <c>10.0.1xx</c>; a prerelease suffix is ignored) must be in
    /// <see cref="CompilerByBand"/>, and the pin must equal that band's compiler version.
    /// </summary>
    private static string[] PinProblems(string sdkVersion, string pin)
    {
        var parts = sdkVersion.Split('-')[0].Split('.');
        var numbers = parts.Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).ToArray();
        if (numbers.Length != 3 || numbers.Any(n => n < 0))
        {
            return [$"{GlobalJsonPath}: sdk.version '{sdkVersion}' is not an SDK version of the form major.minor.patch"];
        }

        var band = string.Create(CultureInfo.InvariantCulture, $"{numbers[0]}.{numbers[1]}.{numbers[2] / 100}xx");
        var compilers = CompilerByBand.Where(b => b.Band == band).Select(b => b.Compiler).ToArray();
        if (compilers.Length == 0)
        {
            return [$"{GlobalJsonPath}: SDK band {band} has no known compiler version; update the band table with the pin"];
        }

        return pin == compilers[0]
            ? []
            : [$"{PackagesPropsPath}: {RoslynPackage} is {pin}; SDK band {band} in {GlobalJsonPath} needs {compilers[0]}"];
    }

    /// <summary>
    /// Adds a problem line when the analyzer reference's <paramref name="name"/> metadata is missing or
    /// is not <paramref name="expected"/> (compared ignoring case, as MSBuild does).
    /// </summary>
    private static void RequireMetadata(XElement reference, string name, string expected, List<string> problems)
    {
        var value = Metadata(reference, name);
        if (string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var actual = value is null ? $"no {name}" : $"{name}=\"{value}\"";
        problems.Add($"{CoreProjectPath}: the analyzer ProjectReference has {actual}; it needs {name}=\"{expected}\"");
    }

    /// <summary>
    /// Every <c>*.csproj</c> under <see cref="ProjectRoots"/> with no <c>bin</c> or <c>obj</c> path
    /// segment, as (repo-relative path with <c>/</c> separators, text), in ordinal path order.
    /// </summary>
    private static (string Path, string Text)[] RepoProjects() =>
        [.. ProjectRoots
            .Select(root => Path.Combine(AnalyzerHarness.RepoRoot, root))
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories))
            .Select(full => Path.GetRelativePath(AnalyzerHarness.RepoRoot, full).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Split('/').Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(relative => (relative, AnalyzerHarness.ReadRepoText(relative)))];

    /// <summary>The <c>sdk.version</c> of the real <c>global.json</c>.</summary>
    private static string RealSdkVersion()
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var json = JsonDocument.Parse(AnalyzerHarness.ReadRepoText(GlobalJsonPath), options);
        var version = json.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        Assert.False(string.IsNullOrEmpty(version), $"{GlobalJsonPath} must set sdk.version.");
        return version;
    }

    /// <summary>The <c>Version</c> of the one <c>Microsoft.CodeAnalysis.CSharp</c> <c>PackageVersion</c> in the real <c>Directory.Packages.props</c>.</summary>
    private static string RealRoslynPin()
    {
        var versions = Named(XDocument.Parse(AnalyzerHarness.ReadRepoText(PackagesPropsPath)), "PackageVersion")
            .Where(e => string.Equals(e.Attribute("Include")?.Value, RoslynPackage, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(versions.Length == 1, $"{PackagesPropsPath} must have exactly one PackageVersion for {RoslynPackage}, found {versions.Length}.");
        var version = versions[0].Attribute("Version")?.Value;
        Assert.False(string.IsNullOrEmpty(version), $"{PackagesPropsPath}'s {RoslynPackage} PackageVersion must have a Version.");
        return version;
    }

    /// <summary>Every element of <paramref name="document"/> with local name <paramref name="name"/>, in document order.</summary>
    private static IEnumerable<XElement> Named(XDocument document, string name) =>
        document.Descendants().Where(e => e.Name.LocalName == name);

    /// <summary>Every <c>ProjectReference</c> whose <c>Include</c> names the analyzer project file.</summary>
    private static IEnumerable<XElement> AnalyzerProjectReferences(XDocument project) =>
        Named(project, "ProjectReference").Where(e => FileNameIs(e.Attribute("Include")?.Value, AnalyzerProjectFile));

    /// <summary>True when an MSBuild <c>Include</c> path's last segment, with either separator, is <paramref name="fileName"/>.</summary>
    private static bool FileNameIs(string? include, string fileName) =>
        include is not null
        && string.Equals(include.Trim().Replace('\\', '/').Split('/')[^1], fileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>An item's metadata value, from its attribute or, failing that, its last child element of that name.</summary>
    private static string? Metadata(XElement item, string name) =>
        item.Attribute(name)?.Value ?? item.Elements().LastOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>The one analyzer <c>ProjectReference</c> of a project being mutated.</summary>
    private static XElement SingleAnalyzerReference(XDocument project) => Assert.Single(AnalyzerProjectReferences(project));

    /// <summary>An attribute the mutation needs, asserted present so a mutation cannot silently do nothing.</summary>
    private static XAttribute RequiredAttribute(XElement element, string name)
    {
        var attribute = element.Attribute(name);
        Assert.NotNull(attribute);
        return attribute;
    }

    /// <summary>Parses <paramref name="text"/>, applies <paramref name="mutation"/> and returns the new XML.</summary>
    private static string Mutate(string text, Action<XDocument> mutation)
    {
        var document = XDocument.Parse(text);
        mutation(document);
        return document.ToString();
    }

    /// <summary>Adds <c>&lt;name&gt;value&lt;/name&gt;</c> to the project's first <c>PropertyGroup</c>.</summary>
    private static void AddProperty(XDocument project, string name, string value)
    {
        var root = project.Root!;
        var group = root.Elements().First(e => e.Name.LocalName == "PropertyGroup");
        group.Add(new XElement(root.Name.Namespace + name, value));
    }

    /// <summary>Adds a new <c>ItemGroup</c> holding one <c>&lt;itemType attribute="value" /&gt;</c> item.</summary>
    private static void AddItem(XDocument project, string itemType, string attribute, string value)
    {
        var root = project.Root!;
        root.Add(new XElement(root.Name.Namespace + "ItemGroup", new XElement(root.Name.Namespace + itemType, new XAttribute(attribute, value))));
    }

    /// <summary>The text of the project at <paramref name="path"/>.</summary>
    private static string Text(IReadOnlyList<(string Path, string Text)> projects, string path) =>
        Assert.Single(projects, p => p.Path == path).Text;

    /// <summary>The projects with <paramref name="path"/>'s text replaced by <paramref name="change"/> of it.</summary>
    private static (string Path, string Text)[] Replace(
        IReadOnlyList<(string Path, string Text)> projects,
        string path,
        Func<string, string> change) =>
        [.. projects.Select(p => p.Path == path ? (p.Path, change(p.Text)) : p)];
}
