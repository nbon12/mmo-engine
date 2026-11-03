namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>Records the region lifecycle events the entity tests assert on. It holds no business rules.</summary>
internal sealed class Kit1EventLog : IRegionizerObserver
{
    public List<(long RegionId, RegionCreationReason Reason, long ParentId)> Created { get; } = [];

    public List<long> Died { get; } = [];

    public List<(long AbsorberId, long AbsorbedId)> Merged { get; } = [];

    public List<(int EntityId, long OldOwner, long NewOwner)> EntityOwnerChanges { get; } = [];

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId) => Created.Add((regionId, reason, parentId));

    public void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
    }

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
    }

    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId) => Merged.Add((absorberId, absorbedId));

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds)
    {
    }

    public void OnRegionDied(long regionId, long ageSteps) => Died.Add(regionId);

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
    }

    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner) => EntityOwnerChanges.Add((entityId, oldOwner, newOwner));

    public void OnRegionTickStarted(long regionId, long tickCounter, long now)
    {
    }

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
    {
    }
}
