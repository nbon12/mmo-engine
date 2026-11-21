using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// End to end, the analyzer fails a real build (US3's Independent Test, FR-014), per
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md#tests: <c>dotnet build</c> of a throwaway
/// class library that attaches the analyzer's own build output and enumerates a <c>Dictionary</c> exits
/// non-zero with exactly one MMO error, MMO1004 on that <c>foreach</c>, and none on the SDK-generated
/// <c>AssemblyInfo.cs</c>. Its output is the text quickstart.md#see-the-analyzer-reject-a-violation
/// promises, and it is written to the test log.
/// </summary>
public sealed partial class BuildTests
{
    private const string ProjectFile = "Sample.csproj";

    private const string SourceFile = "C.cs";

    private const string GlobalJsonPath = "global.json";

    /// <summary>The throwaway library's one source file: a <c>foreach</c> over a <c>Dictionary</c>, which MMO1004 reports.</summary>
    private const string SampleSource =
        "public static class C { public static int Sum(System.Collections.Generic.Dictionary<int, int> d) { int s = 0; foreach (var kv in d) s += kv.Value; return s; } }\n";

    /// <summary>The start of MMO1004's message on that <c>foreach</c>, as quickstart.md promises it.</summary>
    private const string ExpectedError =
        "error MMO1004: 'foreach' enumerates 'System.Collections.Generic.Dictionary<int, int>', whose order is not defined";

    /// <summary>
    /// The whole text of the throwaway build's <c>Directory.Build.props</c> and
    /// <c>Directory.Build.targets</c>: a project with nothing in it. MSBuild stops its upward search at
    /// these files in the temp directory and imports nothing above it. They are not empty files, because
    /// MSBuild refuses a project file with no root element.
    /// </summary>
    private const string EmptyProjectFile = "<Project />\n";

    /// <summary>How long the build may run before it is killed with its process tree and the test fails.</summary>
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The build's arguments: no logo, no terminal logger and no error summary (so each error is printed
    /// once), and no MSBuild node or compiler server left running to hold the temp directory.
    /// </summary>
    private static readonly string[] BuildArguments =
        ["build", ProjectFile, "-nologo", "-tl:off", "-clp:NoSummary", "-nodeReuse:false", "-p:UseSharedCompilation=false"];

    /// <summary>
    /// US3-IT, FR-014: in a unique temp directory, a <c>net10.0</c> library with <c>IsTrimmable</c> and
    /// <c>IsAotCompatible</c> set as in the core (so the SDK generates the <c>System.Reflection</c>
    /// assembly attributes) and <c>ImplicitUsings</c> off, attaching
    /// <c>src/MmoEngine.SimCore.Analyzers/bin/&lt;configuration&gt;/netstandard2.0/MmoEngine.SimCore.Analyzers.dll</c>
    /// with an <c>&lt;Analyzer Include="…" /&gt;</c> item, under a <c>Directory.Build.props</c> and a
    /// <c>Directory.Build.targets</c> that each hold only <c>&lt;Project /&gt;</c>, and a copy of the
    /// repository's <c>global.json</c>. Building it exits
    /// non-zero; exactly one distinct output line is an <c>error MMO####</c>, and it is MMO1004 in
    /// <c>C.cs</c> with the promised message; no output line names both <c>AssemblyInfo.cs</c> and an MMO
    /// rule.
    /// </summary>
    [Fact]
    public async Task ABuildWithAViolationFails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var analyzer = AnalyzerAssemblyPath();
        Assert.True(
            File.Exists(analyzer),
            $"The analyzer's own build output {analyzer} must exist (the test-bin copy is instrumented by coverlet, so the build uses this one).");

        var directory = Path.Combine(Path.GetTempPath(), "mmo-simcore-analyzer-tests", $"build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await WriteAsync(directory, ProjectFile, ProjectText(analyzer), cancellationToken);
            await WriteAsync(directory, "Directory.Build.props", EmptyProjectFile, cancellationToken);
            await WriteAsync(directory, "Directory.Build.targets", EmptyProjectFile, cancellationToken);
            await WriteAsync(directory, GlobalJsonPath, AnalyzerHarness.ReadRepoText(GlobalJsonPath), cancellationToken);
            await WriteAsync(directory, SourceFile, SampleSource, cancellationToken);

            var (exitCode, output) = await BuildAsync(directory, cancellationToken);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"dotnet {string.Join(' ', BuildArguments)} in {directory} exited {exitCode}:{Environment.NewLine}{output}");

            Assert.True(exitCode != 0, $"The build must fail on the MMO1004 violation, but exited 0:{Environment.NewLine}{output}");

            var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            var errors = lines.Where(line => MmoErrorLine().IsMatch(line)).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(
                errors.Length == 1,
                $"Expected exactly one distinct 'error MMO' line, got {errors.Length}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}{Environment.NewLine}Full output:{Environment.NewLine}{output}");
            var error = errors[0];
            Assert.Contains(SourceFile + "(", error, StringComparison.Ordinal);
            Assert.Contains(ExpectedError, error, StringComparison.Ordinal);

            var generated = lines
                .Where(line => line.Contains("AssemblyInfo.cs", StringComparison.Ordinal) && line.Contains("MMO", StringComparison.Ordinal))
                .ToArray();
            Assert.True(
                generated.Length == 0,
                $"The SDK-generated AssemblyInfo.cs must report no MMO rule, got:{Environment.NewLine}{string.Join(Environment.NewLine, generated)}");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [GeneratedRegex(@"error MMO\d{4}", RegexOptions.CultureInvariant)]
    private static partial Regex MmoErrorLine();

    /// <summary>
    /// The analyzer project's own build output for the configuration the tests were built in, which is
    /// the name of the directory above the test's target-framework output directory
    /// (<c>bin/&lt;configuration&gt;/net10.0/</c>).
    /// </summary>
    private static string AnalyzerAssemblyPath()
    {
        var targetDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var configurationDirectory = Path.GetDirectoryName(targetDirectory)
            ?? throw new DirectoryNotFoundException($"{targetDirectory} has no parent directory naming the build configuration.");
        var configuration = Path.GetFileName(configurationDirectory);
        return Path.Combine(
            AnalyzerHarness.RepoRoot,
            "src",
            "MmoEngine.SimCore.Analyzers",
            "bin",
            configuration,
            "netstandard2.0",
            "MmoEngine.SimCore.Analyzers.dll");
    }

    /// <summary>The throwaway library's project file, attaching <paramref name="analyzer"/> as an analyzer only.</summary>
    private static string ProjectText(string analyzer)
    {
        var project = new XElement(
            "Project",
            new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement(
                "PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("IsTrimmable", "true"),
                new XElement("IsAotCompatible", "true"),
                new XElement("ImplicitUsings", "disable")),
            new XElement(
                "ItemGroup",
                new XElement("Analyzer", new XAttribute("Include", EscapeForMsBuild(analyzer)))));
        return project.ToString() + "\n";
    }

    /// <summary>Escapes the characters MSBuild gives a meaning in an item's <c>Include</c> as <c>%XX</c>.</summary>
    private static string EscapeForMsBuild(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '%' or '$' or '@' or '\'' or ';' or '?' or '*')
            {
                builder.Append('%').Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static Task WriteAsync(string directory, string name, string text, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(directory, name), text, cancellationToken);

    /// <summary>
    /// Runs <c>dotnet build</c> in <paramref name="directory"/> with the dotnet host running the tests
    /// (<c>DOTNET_HOST_PATH</c>) when set, without the inherited <c>MSBuild*</c> and <c>MSBUILD*</c>
    /// variables, so the test host's MSBuild settings cannot reach the child. A build still running after
    /// <see cref="BuildTimeout"/> is killed with its process tree and the test fails.
    /// </summary>
    /// <returns>The exit code and everything the build wrote to stdout, then stderr.</returns>
    private static async Task<(int ExitCode, string Output)> BuildAsync(string directory, CancellationToken cancellationToken)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var start = new ProcessStartInfo(string.IsNullOrEmpty(host) ? "dotnet" : host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = directory,
        };
        foreach (var argument in BuildArguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(IsMsBuildVariable).ToArray())
        {
            start.Environment.Remove(name);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"dotnet {string.Join(' ', BuildArguments)} did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        var exited = true;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(BuildTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                exited = false;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
        }

        var output = await stdout + "\n" + await stderr;
        Assert.True(
            exited,
            $"dotnet {string.Join(' ', BuildArguments)} did not exit within {BuildTimeout.TotalMinutes} minutes and was killed:{Environment.NewLine}{output}");
        return (process.ExitCode, output);
    }

    private static bool IsMsBuildVariable(string name) =>
        name.StartsWith("MSBuild", StringComparison.Ordinal) || name.StartsWith("MSBUILD", StringComparison.Ordinal);

    /// <summary>Deletes the temp directory, best effort: it is unique to this run, so a leftover never affects another.</summary>
    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort, as above.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }
    }
}
