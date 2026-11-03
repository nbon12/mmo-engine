namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>One <see cref="IRegionizerObserver.OnTimerFired"/> call.</summary>
internal readonly record struct Kit3TimerFiring(int TimerId, int EntityId, long RegionId, long Now, long RegionTick);

/// <summary>One <see cref="IRegionizerObserver.OnMerged"/> call.</summary>
internal readonly record struct Kit3Merge(long AbsorberId, long AbsorbedId, EntityClass CauseClass, int CauseEntityId);

/// <summary>
/// Records the observer calls the timer, invariant and property tests read. It holds no rules: it
/// only writes down what the regionizer reported, in order.
/// </summary>
internal sealed class Kit3RecordingObserver : IRegionizerObserver
{
    public List<Kit3TimerFiring> TimersFired { get; } = [];

    public List<Kit3Merge> Merges { get; } = [];

    public List<(long ParentId, long[] ChildIds)> Splits { get; } = [];

    public List<(long RegionId, long TickCounter, long Now)> TickStarts { get; } = [];

    public List<(long RegionId, RegionCreationReason Reason, long ParentId)> Created { get; } = [];

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId) => Created.Add((regionId, reason, parentId));

    public void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
    }

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
    }

    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId) =>
        Merges.Add(new Kit3Merge(absorberId, absorbedId, causeClass, causeEntityId));

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds) => Splits.Add((parentId, childIds.ToArray()));

    public void OnRegionDied(long regionId, long ageSteps)
    {
    }

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
    }

    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
    }

    public void OnRegionTickStarted(long regionId, long tickCounter, long now) => TickStarts.Add((regionId, tickCounter, now));

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick) =>
        TimersFired.Add(new Kit3TimerFiring(timerId, entityId, regionId, now, regionTick));
}

/// <summary>
/// Counts observer calls without allocating, so the zero-allocation test can watch for region
/// creation, merges and splits while it measures.
/// </summary>
internal sealed class Kit3CountingObserver : IRegionizerObserver
{
    public long RegionsCreated { get; private set; }

    public long Merges { get; private set; }

    public long MergeRequests { get; private set; }

    public long Splits { get; private set; }

    public long RegionsDied { get; private set; }

    public long TickStarts { get; private set; }

    public long TimersFired { get; private set; }

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId) => RegionsCreated++;

    public void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
    }

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId) => MergeRequests++;

    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId) => Merges++;

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds) => Splits++;

    public void OnRegionDied(long regionId, long ageSteps) => RegionsDied++;

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
    }

    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
    }

    public void OnRegionTickStarted(long regionId, long tickCounter, long now) => TickStarts++;

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick) => TimersFired++;
}
