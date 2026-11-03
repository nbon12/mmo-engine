namespace MmoEngine.Regionizer.Simulator.Tests.Support;

/// <summary>An <see cref="IRegionizerObserver"/> whose methods do nothing; tests override what they watch.</summary>
internal abstract class ObserverBase : IRegionizerObserver
{
    public virtual void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId)
    {
    }

    public virtual void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
    }

    public virtual void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
    }

    public virtual void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
    {
    }

    public virtual void OnSplit(long parentId, ReadOnlySpan<long> childIds)
    {
    }

    public virtual void OnRegionDied(long regionId, long ageSteps)
    {
    }

    public virtual void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
    }

    public virtual void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
    }

    public virtual void OnRegionTickStarted(long regionId, long tickCounter, long now)
    {
    }

    public virtual void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
    {
    }
}
