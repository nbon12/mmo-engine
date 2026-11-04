using MmoEngine.Harness.Network;
using MmoEngine.Harness.Tests.Rules;
using MmoEngine.Harness.Tests.Support;

namespace MmoEngine.Harness.Tests.Network;

/// <summary>US4: network profiles are defined, applied and calibrated (spec 014, User Story 4), without the network.</summary>
public sealed class NetworkProfileTests
{
    private static IReadOnlyDictionary<string, NetworkProfile> Catalog() =>
        ProfileCatalog.Load(Path.Combine(RuleFileParserTests.RepoRoot(), "harness", "profiles"));

    /// <summary>US4 scenario 1: the catalogue holds the four profiles with the issue's values, and a reordering variant of each.</summary>
    [Theory]
    [InlineData("LAN", 1d, 0d, 0d)]
    [InlineData("Typical", 80d, 10d, 0.5d)]
    [InlineData("Cross-region", 160d, 25d, 1d)]
    [InlineData("Poor", 250d, 50d, 3d)]
    public void CatalogueHoldsEachProfileAndItsReorderingVariant(string name, double roundTrip, double jitter, double loss)
    {
        var catalog = Catalog();

        var profile = catalog[name];
        var variant = catalog[name + NetworkProfile.ReorderSuffix];

        Assert.Equal((roundTrip, jitter, loss, false), (profile.RoundTripMs, profile.JitterMs, profile.LossPercent, profile.AllowReordering));
        Assert.Equal((roundTrip, jitter, loss, true), (variant.RoundTripMs, variant.JitterMs, variant.LossPercent, variant.AllowReordering));
        Assert.Equal(8, catalog.Count);
    }

    /// <summary>The tolerances are the issue's: 10% (LAN 0.5 ms) and 0.25 points.</summary>
    [Fact]
    public void TolerancesAreTheIssuesProposals()
    {
        var catalog = Catalog();

        Assert.Equal(0.5, catalog["LAN"].Tolerances.AbsoluteMs);
        Assert.Null(catalog["Typical"].Tolerances.AbsoluteMs);
        Assert.All(catalog.Values, p => Assert.Equal((0.10, 0.25), (p.Tolerances.RelativeFraction, p.Tolerances.LossPoints)));
    }

    [Theory]
    [InlineData("{\"name\":\"X\",\"version\":0,\"roundTripMs\":1,\"jitterMs\":0,\"lossPercent\":0}", "version")]
    [InlineData("{\"name\":\"X\",\"version\":1,\"roundTripMs\":-1,\"jitterMs\":0,\"lossPercent\":0}", "roundTripMs")]
    [InlineData("{\"name\":\"X\",\"version\":1,\"roundTripMs\":1,\"jitterMs\":0,\"lossPercent\":101}", "lossPercent")]
    [InlineData("{\"name\":\"bad name\",\"version\":1,\"roundTripMs\":1,\"jitterMs\":0,\"lossPercent\":0}", "name")]
    [InlineData("{\"name\":\"X\",\"version\":1,\"roundTripMs\":1,\"jitterMs\":0,\"lossPercent\":0,\"extra\":1}", "unknown field")]
    [InlineData("{\"name\":\"X\",\"version\":1,\"roundTripMs\":1,\"jitterMs\":0,\"lossPercent\":0,\"tolerances\":{\"relativeFraction\":2}}", "relativeFraction")]
    [InlineData("null", "empty")]
    public void InvalidProfileIsRefusedNamingTheField(string json, string field)
    {
        var error = Assert.Throws<ProfileException>(() => NetworkProfile.Parse(json, "x.json"));

        Assert.StartsWith("x.json: ", error.Message, StringComparison.Ordinal);
        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFolderAndDuplicateNamesAreRefused()
    {
        Assert.Throws<ProfileException>(() => ProfileCatalog.Load(Path.Combine(RunFolders.TempFolder(), "none")));
        var folder = RunFolders.TempFolder();
        const string json = "{\"name\":\"X\",\"version\":1,\"roundTripMs\":1,\"jitterMs\":0,\"lossPercent\":0}";
        File.WriteAllText(Path.Combine(folder, "a.json"), json);
        File.WriteAllText(Path.Combine(folder, "b.json"), json);
        Assert.Contains("defined twice", Assert.Throws<ProfileException>(() => ProfileCatalog.Load(folder)).Message, StringComparison.Ordinal);
    }

    /// <summary>FR-026: calibrated settings round-trip through the file and replace the arithmetic split.</summary>
    [Fact]
    public void CalibratedSettingsRoundTripAndTakePrecedence()
    {
        var typical = Catalog()["Typical"];
        var calibrated = typical with
        {
            Calibrated = new CalibratedSettings
            {
                PerDirection = new DirectionSettings(38, 6.5, 0.45),
                RunnerClass = "ci-x64-0123456789abcdef",
                Kernel = "6.8.0",
                MeasuredAt = new DateTime(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc),
                SourceRunId = "probe-typical-1",
            },
        };

        var parsed = NetworkProfile.Parse(calibrated.ToJson(), "typical.json");

        Assert.Equal(new DirectionSettings(38, 6.5, 0.45), parsed.PerDirection());
        Assert.Equal("6.8.0", parsed.Calibrated!.Kernel);
        Assert.Equal("ci-x64-0123456789abcdef", parsed.Calibrated.RunnerClass);
        Assert.Null(parsed.ReorderingVariant().Calibrated);
        Assert.Equal(new DirectionSettings(40, 10 / Math.Sqrt(2), 0.5), typical.PerDirection());
    }
}
