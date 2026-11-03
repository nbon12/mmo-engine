namespace MmoEngine.Regionizer.Tests.Support;

/// <summary>One regionizer notification, stamped with the harness step it arrived on.</summary>
internal abstract record Kit2Event(long Step);

internal sealed record Kit2Created(long Step, long Region, RegionCreationReason Reason, long Parent) : Kit2Event(Step);

internal sealed record Kit2StateChanged(long Step, long Region, RegionState From, RegionState To) : Kit2Event(Step);

internal sealed record Kit2MergeRequested(long Step, long A, long B, EntityClass Cause, int CauseEntity) : Kit2Event(Step);

internal sealed record Kit2Merged(long Step, long Absorber, long Absorbed, EntityClass Cause, int CauseEntity) : Kit2Event(Step);

internal sealed record Kit2Split(long Step, long Parent, long[] Children) : Kit2Event(Step);

internal sealed record Kit2Died(long Step, long Region, long AgeSteps) : Kit2Event(Step);

internal sealed record Kit2SectionOwner(long Step, int Section, long Old, long New) : Kit2Event(Step);

internal sealed record Kit2EntityOwner(long Step, int Entity, long Old, long New) : Kit2Event(Step);

internal sealed record Kit2TickStarted(long Step, long Region, long TickCounter, long Now) : Kit2Event(Step);

internal sealed record Kit2TimerFired(long Step, int TimerId, int Entity, long Region, long Now, long RegionTick) : Kit2Event(Step);

/// <summary>
/// Records every regionizer notification in the order it is raised. It never calls back into the
/// regionizer; the harness stamps the current step through <see cref="Step"/>.
/// </summary>
internal sealed class Kit2Observer : IRegionizerObserver
{
    /// <summary>The harness step that new events are stamped with.</summary>
    public long Step { get; set; }

    /// <summary>Every event so far, oldest first.</summary>
    public List<Kit2Event> Log { get; } = [];

    /// <summary>The events of one kind, oldest first, from log position <paramref name="from"/>.</summary>
    public IEnumerable<T> Of<T>(int from = 0)
        where T : Kit2Event => Log.Skip(from).OfType<T>();

    /// <summary>Merges that joined regions <paramref name="a"/> and <paramref name="b"/>, in either role.</summary>
    public IEnumerable<Kit2Merged> MergesBetween(long a, long b) =>
        Of<Kit2Merged>().Where(m => (m.Absorber == a && m.Absorbed == b) || (m.Absorber == b && m.Absorbed == a));

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId) =>
        Log.Add(new Kit2Created(Step, regionId, reason, parentId));

    public void OnStateChanged(long regionId, RegionState from, RegionState to) =>
        Log.Add(new Kit2StateChanged(Step, regionId, from, to));

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId) =>
        Log.Add(new Kit2MergeRequested(Step, a, b, causeClass, causeEntityId));

    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId) =>
        Log.Add(new Kit2Merged(Step, absorberId, absorbedId, causeClass, causeEntityId));

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds) =>
        Log.Add(new Kit2Split(Step, parentId, childIds.ToArray()));

    public void OnRegionDied(long regionId, long ageSteps) =>
        Log.Add(new Kit2Died(Step, regionId, ageSteps));

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner) =>
        Log.Add(new Kit2SectionOwner(Step, sectionIndex, oldOwner, newOwner));

    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner) =>
        Log.Add(new Kit2EntityOwner(Step, entityId, oldOwner, newOwner));

    public void OnRegionTickStarted(long regionId, long tickCounter, long now) =>
        Log.Add(new Kit2TickStarted(Step, regionId, tickCounter, now));

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick) =>
        Log.Add(new Kit2TimerFired(Step, timerId, entityId, regionId, now, regionTick));
}
