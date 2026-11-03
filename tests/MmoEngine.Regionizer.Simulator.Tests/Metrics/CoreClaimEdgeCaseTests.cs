namespace MmoEngine.Regionizer.Simulator.Tests.Metrics;

/// <summary>
/// Core behaviour the simulator's metrics depend on, checked against the spec's edge cases. Kept in
/// the suite as a failing test until the core is fixed.
/// </summary>
public sealed class CoreClaimEdgeCaseTests
{
    // CORE-BUG: a claim radius of 0 also claims the sections touching the entity's own (distance 0 <= 0), but the spec's edge case says it claims only its own section.
    [Theory]
    [InlineData(ClaimAnchoring.Section, 8.0, 8.0)]
    [InlineData(ClaimAnchoring.Position, 16.0, 8.0)]
    public void ZeroClaimRadiusClaimsOnlyTheEntitysOwnSection(ClaimAnchoring anchoring, double x, double y)
    {
        // Spec, Edge Cases: "a claim radius of zero claims only the entity's own section"; an entity on
        // a section edge belongs to exactly one section (floor rule) and its claim is computed from it.
        var topology = SectionTopology.OpenWorld(16, 256, 256);
        var options = new RegionizerOptions(16, anchoring, 0, new ClaimRadii(0, 0, 0, 0, 0), 1, 0, 0, TimerMode.TickCounter);
        var regionizer = new Regionizer(topology, options);

        regionizer.AddEntity(0, EntityClass.Infantry, 0, x, y);

        Assert.True(topology.TrySectionAt(0, x, y, out var own));
        Assert.Equal([own], regionizer.GetEntity(0).ClaimedSections.ToArray());
    }
}
