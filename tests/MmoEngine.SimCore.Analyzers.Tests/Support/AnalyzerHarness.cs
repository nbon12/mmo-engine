using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// Compiles C# in the test process and runs <see cref="DeterminismAnalyzer"/> over it, per
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md#tests ("Harness"). Samples compile
/// against <c>Basic.Reference.Assemblies.Net100</c> with the latest language version and nullable on;
/// the analyzer runs through <see cref="CompilationWithAnalyzers"/> with suppressed diagnostics
/// reported, so a <c>[SuppressMessage]</c> cannot hide a diagnostic from a test. Repository files are
/// opened for reading only.
/// </summary>
internal static class AnalyzerHarness
{
    /// <summary>The file name a member sample is compiled under.</summary>
    public const string SamplePath = "Sample.cs";

    private const string SolutionFile = "MmoEngine.sln";

    private const string CoreDirectory = "src/MmoEngine.SimCore";

    /// <summary>The usings every member sample is compiled under, in this order.</summary>
    private const string SamplePrefix =
        "using System;\n" +
        "using System.Collections.Generic;\n" +
        "using System.Collections.Immutable;\n" +
        "using System.Diagnostics;\n" +
        "using System.Linq;\n" +
        "using System.Numerics;\n" +
        "using System.Runtime.Intrinsics;\n" +
        "using System.Threading;\n" +
        "using System.Threading.Tasks;\n" +
        "\n" +
        "namespace Sample;\n" +
        "\n" +
        "public static class C\n" +
        "{\n" +
        "    ";

    private const string SampleSuffix = "\n}\n";

    private static readonly Lazy<string> RootPath = new(FindRoot);

    /// <summary>The directory holding MmoEngine.sln, found by walking up from <see cref="AppContext.BaseDirectory"/>.</summary>
    public static string RepoRoot => RootPath.Value;

    /// <summary>The .NET 10 reference assemblies every sample compiles against.</summary>
    public static ImmutableArray<MetadataReference> References { get; } =
        [.. Basic.Reference.Assemblies.Net100.References.All];

    /// <summary><see cref="References"/> plus System.IO.Hashing, the core's one package reference.</summary>
    public static ImmutableArray<MetadataReference> CoreReferences { get; } =
        References.Add(MetadataReference.CreateFromFile(typeof(System.IO.Hashing.XxHash64).Assembly.Location));

    /// <summary>Latest language version; every tree is parsed with these options.</summary>
    public static CSharpParseOptions ParseOptions { get; } = new(LanguageVersion.Latest);

    /// <summary>The samples' options: a library, nullable on, unsafe allowed (for the pointer and function-pointer samples).</summary>
    public static CSharpCompilationOptions SampleOptions { get; } = new(
        OutputKind.DynamicallyLinkedLibrary,
        nullableContextOptions: NullableContextOptions.Enable,
        allowUnsafe: true);

    /// <summary>The core's own options: <see cref="SampleOptions"/> with unsafe code off, as in src/MmoEngine.SimCore.</summary>
    public static CSharpCompilationOptions CoreOptions { get; } = SampleOptions.WithAllowUnsafe(false);

    /// <summary>Reads a repository file as UTF-8 text, opened with <see cref="FileAccess.Read"/> only.</summary>
    public static string ReadRepoText(string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(relativePath);
        var path = Path.GetFullPath(Path.Combine(RepoRoot, relativePath));
        if (!path.StartsWith(RepoRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{path} is not under the repository root {RepoRoot}.", nameof(relativePath));
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Compiles <paramref name="member"/> as the only member of <c>public static class C</c> in
    /// <c>namespace Sample;</c> under the sample usings, and runs the analyzer over it. The result
    /// carries the member's span in the file, so a test can check every diagnostic lies inside it.
    /// </summary>
    public static async Task<SampleResult> AnalyzeMemberAsync(string member, CSharpCompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        var text = SamplePrefix + member + SampleSuffix;
        var memberSpan = new TextSpan(SamplePrefix.Length, member.Length);
        var tree = Parse(SamplePath, text);
        var compilation = Compile([tree], options, references: null);
        var (errors, diagnostics) = await RunAsync(compilation);
        return new SampleResult(errors, diagnostics, compilation, tree, memberSpan);
    }

    /// <summary>Parses each (path, text) file with <see cref="ParseOptions"/> and runs <see cref="AnalyzeTreesAsync"/>.</summary>
    public static Task<AnalysisResult> AnalyzeSourcesAsync(
        IReadOnlyList<(string Path, string Text)> files,
        CSharpCompilationOptions? options = null,
        IEnumerable<MetadataReference>? references = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        var trees = files.Select(f => Parse(f.Path, f.Text)).ToArray();
        return AnalyzeTreesAsync(trees, options, references);
    }

    /// <summary>
    /// Compiles <paramref name="trees"/> into one library (default <see cref="SampleOptions"/> and
    /// <see cref="References"/>) and runs the analyzer over it, suppressed diagnostics included.
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeTreesAsync(
        IReadOnlyList<SyntaxTree> trees,
        CSharpCompilationOptions? options = null,
        IEnumerable<MetadataReference>? references = null)
    {
        ArgumentNullException.ThrowIfNull(trees);
        var compilation = Compile(trees, options, references);
        var (errors, diagnostics) = await RunAsync(compilation);
        return new AnalysisResult(errors, diagnostics, compilation);
    }

    /// <summary>
    /// Every <c>src/MmoEngine.SimCore/**/*.cs</c> file under <see cref="RepoRoot"/> with no <c>bin</c>
    /// or <c>obj</c> path segment, read with <see cref="FileAccess.Read"/>, in ordinal order of its
    /// repo-relative path (with <c>/</c> separators), and parsed under that path.
    /// </summary>
    public static IReadOnlyList<SyntaxTree> CoreSourceTrees()
    {
        var directory = Path.Combine(RepoRoot, CoreDirectory);
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Select(full => Path.GetRelativePath(RepoRoot, full).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.Split('/').Any(segment => segment is "bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .Select(relative => Parse(relative, ReadRepoText(relative)))
            .ToArray();
    }

    /// <summary>Formats a diagnostic for a failure message: <c>path(line,col): ID message [suppressed]</c>.</summary>
    public static string Describe(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var span = diagnostic.Location.GetLineSpan();
        var start = span.StartLinePosition;
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{span.Path}({start.Line + 1},{start.Character + 1}): {diagnostic.Id} {diagnostic.GetMessage(CultureInfo.InvariantCulture)}");
        return diagnostic.IsSuppressed ? text + " [suppressed]" : text;
    }

    /// <summary>Every diagnostic through <see cref="Describe(Diagnostic)"/>, one per line, or <c>(none)</c>.</summary>
    public static string Describe(IEnumerable<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var lines = diagnostics.Select(Describe).ToArray();
        return lines.Length == 0 ? "(none)" : string.Join(Environment.NewLine, lines);
    }

    private static SyntaxTree Parse(string path, string text) =>
        CSharpSyntaxTree.ParseText(SourceText.From(text, Encoding.UTF8), ParseOptions, path);

    private static CSharpCompilation Compile(
        IReadOnlyList<SyntaxTree> trees,
        CSharpCompilationOptions? options,
        IEnumerable<MetadataReference>? references) =>
        CSharpCompilation.Create(
            "Sample",
            trees,
            references ?? References,
            options ?? SampleOptions);

    private static async Task<(IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Diagnostics)> RunAsync(CSharpCompilation compilation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var errors = Ordered(compilation.GetDiagnostics(cancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error));
        var withAnalyzers = compilation.WithAnalyzers(
            [new DeterminismAnalyzer()],
            new CompilationWithAnalyzersOptions(
                new AnalyzerOptions([]),
                null,
                concurrentAnalysis: true,
                logAnalyzerExecutionTime: false,
                reportSuppressedDiagnostics: true));
        var diagnostics = Ordered(await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken));
        return (errors, diagnostics);
    }

    /// <summary>Orders diagnostics by (file path, span start, ID), all ordinal.</summary>
    private static Diagnostic[] Ordered(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics
            .OrderBy(d => d.Location.SourceTree?.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(d => d.Location.SourceSpan.Start)
            .ThenBy(d => d.Id, StringComparer.Ordinal)];

    private static string FindRoot()
    {
        var start = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return Path.TrimEndingDirectorySeparator(directory.FullName);
            }
        }

        throw new DirectoryNotFoundException($"No directory holding {SolutionFile} above {start}.");
    }
}

/// <summary>
/// The outcome of one analyzer run: the compilation's errors (severity Error only) and the analyzer's
/// diagnostics, suppressed ones included, each ordered by (file path, span start, ID).
/// </summary>
internal sealed record AnalysisResult(
    IReadOnlyList<Diagnostic> CompilerErrors,
    IReadOnlyList<Diagnostic> Diagnostics,
    Compilation Compilation);

/// <summary>
/// The outcome of analyzing one member sample: <see cref="AnalysisResult"/>'s lists, the sample's
/// tree, and the span of the member's own text in it.
/// </summary>
internal sealed record SampleResult(
    IReadOnlyList<Diagnostic> CompilerErrors,
    IReadOnlyList<Diagnostic> Diagnostics,
    Compilation Compilation,
    SyntaxTree Tree,
    TextSpan MemberSpan);
