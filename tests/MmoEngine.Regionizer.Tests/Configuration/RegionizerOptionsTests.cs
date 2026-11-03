using MmoEngine.Regionizer.Tests.Support;
using Xunit;

namespace MmoEngine.Regionizer.Tests.Configuration;

/// <summary>
/// Regionizer options come from validated configuration (constitution Principle VI): the regionizer
/// refuses invalid values when it is built, and claim radii are looked up per entity class. These
/// tests use no randomness; each case names itself in its assertion messages.
/// </summary>
public sealed class RegionizerOptionsTests
{
    private const int S = 16;

    /// <summary>A valid baseline the invalid cases change one field of.</summary>
    private static readonly RegionizerOptions Valid = Kit1Fixtures.Options(S, ClaimAnchoring.Section, new ClaimRadii(8, 24, 40, 8, 20), mergeGap: 1, splitMinSections: 9, splitMinDeadPercent: 20, splitMinAgeSteps: 13);

    /// <summary>Each invalid value, the options carrying it and the start of the message naming the field.</summary>
    public static TheoryData<string, string> InvalidCases() => new()
    {
        { "section size 0", "SectionSize must be a positive power of two, was 0." },
        { "section size -16", "SectionSize must be a positive power of two, was -16." },
        { "section size 12", "SectionSize must be a positive power of two, was 12." },
        { "section size 48", "SectionSize must be a positive power of two, was 48." },
        { "merge gap 2", "MergeGap must be 0 or 1, was 2." },
        { "merge gap -1", "MergeGap must be 0 or 1, was -1." },
        { "infantry radius NaN", "Claim radii must be finite and non-negative, was NaN." },
        { "ground vehicle radius -1", "Claim radii must be finite and non-negative, was -1." },
        { "aircraft radius NaN", "Claim radii must be finite and non-negative, was NaN." },
        { "traveller radius -0.5", "Claim radii must be finite and non-negative, was -0.5." },
        { "capture point zone +infinity", "Claim radii must be finite and non-negative, was Infinity." },
        { "split minimum sections 0", "SplitMinSections must be at least 1, was 0." },
        { "split minimum sections -3", "SplitMinSections must be at least 1, was -3." },
        { "dead percent -1", "SplitMinDeadPercent must be in [0, 100], was -1." },
        { "dead percent 100.5", "SplitMinDeadPercent must be in [0, 100], was 100.5." },
        { "dead percent NaN", "SplitMinDeadPercent must be in [0, 100], was NaN." },
        { "split minimum age -1", "SplitMinAgeSteps must be non-negative, was -1." },
    };

    /// <summary>Each invalid field throws <see cref="ArgumentException"/> naming the field and the value.</summary>
    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void InvalidOptionIsRejected(string invalid, string message)
    {
        var options = Invalid(invalid);
        var topology = SectionTopology.OpenWorld(S, 320, 320);

        var thrown = Record.Exception(() => new Regionizer(topology, options));

        if (thrown is null || thrown.GetType() != typeof(ArgumentException))
        {
            Assert.Fail($"{invalid}: expected ArgumentException, got {thrown?.GetType().Name ?? "no exception"}");
        }

        Assert.True(thrown.Message == message, $"{invalid}: message \"{thrown.Message}\", expected \"{message}\"");
    }

    /// <summary>Missing claim radii are refused before any radius is read.</summary>
    [Fact]
    public void MissingClaimRadiiAreRejected()
    {
        var options = Valid with { ClaimRadius = null! };

        var thrown = Record.Exception(() => new Regionizer(SectionTopology.OpenWorld(S, 320, 320), options));

        Assert.True(thrown is ArgumentNullException { ParamName: "ClaimRadius" }, $"null claim radii: got {thrown?.GetType().Name ?? "no exception"} ({thrown?.Message})");
    }

    /// <summary>The boundary values of every range are accepted, and the regionizer keeps the options it was given.</summary>
    [Theory]
    [InlineData(1, 0, 0.0, 1, 0.0, 0L)]
    [InlineData(64, 1, 0.0, 1, 100.0, 0L)]
    [InlineData(16, 0, 1e6, int.MaxValue, 100.0, long.MaxValue)]
    public void BoundaryOptionValuesAreAccepted(int sectionSize, int mergeGap, double radius, int splitMinSections, double deadPercent, long minAge)
    {
        var options = Kit1Fixtures.Options(sectionSize, ClaimAnchoring.Position, Kit1Fixtures.Radii(radius), mergeGap, splitMinSections, deadPercent, minAge, TimerMode.Absolute);
        var label = $"section {sectionSize}, gap {mergeGap}, radius {radius}, N_min {splitMinSections}, p_dead {deadPercent}, T_min {minAge}";

        var regionizer = new Regionizer(SectionTopology.OpenWorld(sectionSize, 256, 256), options);

        Assert.True(ReferenceEquals(options, regionizer.Options), $"{label}: the regionizer keeps the options instance");
        Assert.True(regionizer.RegionIds.Length == 0, $"{label}: a new regionizer has no regions");
        Assert.True(regionizer.Now == 0, $"{label}: a new regionizer starts at step 0");
    }

    /// <summary>Options whose section size differs from the topology's are refused, naming both sizes.</summary>
    [Theory]
    [InlineData(32, 16)]
    [InlineData(8, 16)]
    public void SectionSizeMustMatchTheTopology(int optionsSize, int topologySize)
    {
        var options = Valid with { SectionSize = optionsSize };

        var thrown = Record.Exception(() => new Regionizer(SectionTopology.OpenWorld(topologySize, 320, 320), options));

        var expected = $"Options section size {optionsSize} differs from the topology's {topologySize}.";
        if (thrown is null || thrown.GetType() != typeof(ArgumentException))
        {
            Assert.Fail($"sizes {optionsSize}/{topologySize}: expected ArgumentException, got {thrown?.GetType().Name ?? "no exception"}");
        }

        Assert.True(thrown.Message == expected, $"sizes {optionsSize}/{topologySize}: message \"{thrown.Message}\", expected \"{expected}\"");
    }

    /// <summary>Each entity class looks up its own radius; heavy projectiles claim nothing and report 0.</summary>
    [Theory]
    [InlineData(EntityClass.Infantry, 1.5)]
    [InlineData(EntityClass.GroundVehicle, 2.5)]
    [InlineData(EntityClass.Aircraft, 3.5)]
    [InlineData(EntityClass.Traveller, 4.5)]
    [InlineData(EntityClass.CapturePoint, 5.5)]
    [InlineData(EntityClass.HeavyProjectile, 0.0)]
    public void ClaimRadiusIsLookedUpPerClass(EntityClass cls, double expected)
    {
        var radii = new ClaimRadii(Infantry: 1.5, GroundVehicle: 2.5, Aircraft: 3.5, Traveller: 4.5, CapturePointZone: 5.5);

        Assert.True(radii.For(cls) == expected, $"{cls}: radius {radii.For(cls)}, expected {expected}");
    }

    /// <summary>The largest radius over every class, whichever class holds it.</summary>
    [Theory]
    [InlineData(9.0, 1.0, 1.0, 1.0, 1.0, 9.0)]
    [InlineData(1.0, 9.0, 1.0, 1.0, 1.0, 9.0)]
    [InlineData(1.0, 1.0, 9.0, 1.0, 1.0, 9.0)]
    [InlineData(1.0, 1.0, 1.0, 9.0, 1.0, 9.0)]
    [InlineData(1.0, 1.0, 1.0, 1.0, 9.0, 9.0)]
    [InlineData(0.0, 0.0, 0.0, 0.0, 0.0, 0.0)]
    public void LargestClaimRadiusCoversEveryClass(double infantry, double ground, double aircraft, double traveller, double zone, double expected)
    {
        var radii = new ClaimRadii(infantry, ground, aircraft, traveller, zone);

        Assert.True(radii.Max == expected, $"radii ({infantry}, {ground}, {aircraft}, {traveller}, {zone}): largest {radii.Max}, expected {expected}");
    }

    private static RegionizerOptions Invalid(string invalid) => invalid switch
    {
        "section size 0" => Valid with { SectionSize = 0 },
        "section size -16" => Valid with { SectionSize = -16 },
        "section size 12" => Valid with { SectionSize = 12 },
        "section size 48" => Valid with { SectionSize = 48 },
        "merge gap 2" => Valid with { MergeGap = 2 },
        "merge gap -1" => Valid with { MergeGap = -1 },
        "infantry radius NaN" => Valid with { ClaimRadius = Valid.ClaimRadius with { Infantry = double.NaN } },
        "ground vehicle radius -1" => Valid with { ClaimRadius = Valid.ClaimRadius with { GroundVehicle = -1 } },
        "aircraft radius NaN" => Valid with { ClaimRadius = Valid.ClaimRadius with { Aircraft = double.NaN } },
        "traveller radius -0.5" => Valid with { ClaimRadius = Valid.ClaimRadius with { Traveller = -0.5 } },
        "capture point zone +infinity" => Valid with { ClaimRadius = Valid.ClaimRadius with { CapturePointZone = double.PositiveInfinity } },
        "split minimum sections 0" => Valid with { SplitMinSections = 0 },
        "split minimum sections -3" => Valid with { SplitMinSections = -3 },
        "dead percent -1" => Valid with { SplitMinDeadPercent = -1 },
        "dead percent 100.5" => Valid with { SplitMinDeadPercent = 100.5 },
        "dead percent NaN" => Valid with { SplitMinDeadPercent = double.NaN },
        "split minimum age -1" => Valid with { SplitMinAgeSteps = -1 },
        _ => throw new ArgumentOutOfRangeException(nameof(invalid), invalid, "unknown case"),
    };
}
