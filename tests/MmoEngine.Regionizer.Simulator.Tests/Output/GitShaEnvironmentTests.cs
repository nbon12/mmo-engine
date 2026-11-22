using MmoEngine.Regionizer.Simulator.Output;
using MmoEngine.Regionizer.Simulator.Tests.Support;

namespace MmoEngine.Regionizer.Simulator.Tests.Output;

/// <summary>
/// Runs the tests that set the process-wide <c>MMO_GIT_SHA</c> one at a time, after the parallel tests, so no
/// other test reads the revision while it is set.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GitShaEnvironmentCollection
{
    public const string Name = "Process environment (MMO_GIT_SHA)";
}

/// <summary>
/// The source revision a run records when there is no repository to ask (spec 014, FR-032): the simulator image
/// carries it in <c>MMO_GIT_SHA</c>.
/// </summary>
[Collection(GitShaEnvironmentCollection.Name)]
public sealed class GitShaEnvironmentTests
{
    [Fact]
    public void ThePublicLookupReadsTheFallbackFromMmoGitSha()
    {
        var notARepo = TestRuns.TempDir();
        Directory.CreateDirectory(notARepo);
        const string sha = "fedcba9876543210fedcba9876543210fedcba98";
        var before = Environment.GetEnvironmentVariable("MMO_GIT_SHA");
        try
        {
            Environment.SetEnvironmentVariable("MMO_GIT_SHA", sha);
            Assert.Equal(sha, RunManifest.GitSha(notARepo));
            Environment.SetEnvironmentVariable("MMO_GIT_SHA", null);
            Assert.Equal("unknown", RunManifest.GitSha(notARepo));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MMO_GIT_SHA", before);
        }
    }
}
