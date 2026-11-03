# Contract: Regionizer core library API (`MmoEngine.Regionizer`)

The public surface that the simulator uses now and the continent server uses in sub-spec 10. The
library has no package dependencies, and it must not reference the simulator (FR-015). Names are
binding. Members marked *internal* are for tests through `InternalsVisibleTo`.

## Value types

```csharp
public enum EntityClass : byte { Infantry, GroundVehicle, Aircraft, Traveller, CapturePoint, HeavyProjectile }
public enum ClaimAnchoring : byte { Section, Position }
public enum TimerMode : byte { TickCounter, Absolute }
public enum RegionState : byte { Transient, Ready, Ticking, Dead }
public enum RegionCreationReason : byte { NewClaim, Transient, Split }

public readonly record struct SectionKey(int Layer, int X, int Y);   // X, Y in section units

public sealed record ClaimRadii(double Infantry, double GroundVehicle, double Aircraft,
                                double Traveller, double CapturePointZone);  // metres, >= 0

public sealed record RegionizerOptions(
    int SectionSize,                  // metres, power of two
    ClaimAnchoring Anchoring,
    int MergeGap,                     // 0 or 1
    ClaimRadii ClaimRadius,
    int SplitMinSections,             // N_min >= 1
    double SplitMinDeadPercent,       // p_dead in [0, 100]
    long SplitMinAgeSteps,            // T_min in base steps (60 Hz) >= 0
    TimerMode TimerMode);
```

Validation of these values belongs to the caller's configuration layer (FluentValidation in the
simulator). The constructor still throws `ArgumentException` on an impossible value (a section size
that isn't a power of two, a merge gap other than 0 or 1, a negative radius).

## Topology

```csharp
public readonly record struct LayerSpec(int LayerId, int OriginX, int OriginY,
                                        int Width, int Height, ushort[] AreaTags); // row-major W*H
public readonly record struct PortalSpec(int PortalId, SectionKey A, SectionKey B, bool Open);

public sealed class SectionTopology
{
    public static SectionTopology OpenWorld(int sectionSize, double widthMetres, double heightMetres,
                                            params int[] extraOpenLayers); // layer 0 + uniform layers
    public static SectionTopology Build(int sectionSize, IReadOnlyList<LayerSpec> layers,
                                        IReadOnlyList<PortalSpec> portals);
    public int SectionSize { get; }
    public int SectionCount { get; }                  // dense index space over all layers
    public bool TryIndexOf(SectionKey key, out int index);
    public SectionKey KeyOf(int index);
    public ushort AreaOf(int index);
    public bool TrySectionAt(int layer, double x, double y, out int index); // floor(x / s), floor(y / s)
}
```

## Façade

```csharp
public sealed class Regionizer
{
    public Regionizer(SectionTopology topology, RegionizerOptions options,
                      IRegionizerObserver? observer = null,
                      IReadOnlyList<IRegionDataExtension>? extensions = null);

    // Continent time base, in 60 Hz base steps; it only moves forward.
    public long Now { get; }
    public void AdvanceTo(long now);

    // Entities. IDs are caller-assigned, compact and unique.
    public void AddEntity(int entityId, EntityClass cls, int layer, double x, double y,
                          double z = 0, int fightTag = -1);
    public void MoveEntity(int entityId, int layer, double x, double y, double z = 0);
    public void RemoveEntity(int entityId);
    public void SetCapturePointOccupied(int entityId, bool occupied); // unoccupied: own section only

    // Two-step teleport (location queue). Begin removes the entity and its claims. The caller later
    // completes it, either in the destination region's tick (owner exists) or at a step boundary
    // (no owner: a new region is created).
    public void BeginTeleport(int entityId, int layer, double x, double y, double z = 0);
    public long OwnerOfLocation(int layer, double x, double y); // region ID or 0
    public void CompleteTeleport(int entityId);

    // Portals take effect immediately for claims. Callers apply them at step boundaries (D7).
    public void SetPortalOpen(int portalId, bool open);

    // Region ticks.
    public bool TryMarkTicking(long regionId);         // Ready -> Ticking; increments tick counter
    public TickEndResult MarkNotTicking(long regionId); // pending merges, transient, split (D4)
    public void RunDueTimers(long regionId);           // only while the region is Ticking

    // Timers. Delay is in owner ticks (TickCounter) or base steps (Absolute).
    public int AddTimer(int entityId, long delay);     // returns timer ID

    // Reads (no allocation).
    public ReadOnlySpan<long> RegionIds { get; }       // ascending, live (non-dead) regions
    public RegionView GetRegion(long regionId);
    public bool TryGetState(long regionId, out RegionState state); // false once dead
    public EntityView GetEntity(int entityId);
    public bool HasEntity(int entityId);
    public bool IsPortalOpen(int portalId);
    public ReadOnlySpan<int> EntityIds { get; }        // ascending
    public long OwnerOf(int sectionIndex);             // 0 when unowned
    public int ClaimCountOf(int sectionIndex);
    public SectionTopology Topology { get; }
    public RegionizerOptions Options { get; }
    public RegionizerStats Stats { get; }
}

public readonly record struct TickEndResult(RegionState State, long AbsorbedInto, int SplitChildren);

public readonly ref struct RegionView
{
    public long Id { get; }  public RegionState State { get; }  public long TickCounter { get; }
    public long CreatedAt { get; }  public int SectionCount { get; }  public int DeadSectionCount { get; }
    public ReadOnlySpan<int> Sections { get; }   // section indices, unspecified order
    public ReadOnlySpan<int> Entities { get; }   // ascending entity IDs
    public ReadOnlySpan<TimerView> Timers { get; }
    public ReadOnlySpan<long> PendingMergePartners { get; }
}

public readonly record struct TimerView(int TimerId, int EntityId, long Deadline, long Delay, long SetAt);

public readonly ref struct EntityView
{
    public int Id { get; }  public EntityClass Class { get; }  public int Layer { get; }
    public double X { get; }  public double Y { get; }  public double Z { get; }
    public int FightTag { get; }  public long RegionId { get; }   // 0 while in transit
    public bool InTransit { get; }  public ReadOnlySpan<int> ClaimedSections { get; }
}

public sealed class RegionizerStats   // cumulative; the caller takes deltas per step
{
    public long SerializedTimestampTicks { get; }   // Stopwatch ticks spent in cross-region work
    public long Merges { get; }  public long Splits { get; }  public long RegionsCreated { get; }
}
```

## Observer and extension points

```csharp
public interface IRegionizerObserver
{
    void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId);
    void OnStateChanged(long regionId, RegionState from, RegionState to);
    void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId);
    void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId);
    void OnSplit(long parentId, ReadOnlySpan<long> childIds);   // childIds include parentId
    void OnRegionDied(long regionId, long ageSteps);
    void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner);
    void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner);
    void OnRegionTickStarted(long regionId, long tickCounter, long now);
    void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick);
}

public interface IRegionDataExtension   // every new kind of region data defines merge and split
{
    void OnMerge(long absorberId, long absorbedId);
    void OnSplit(long parentId, ReadOnlySpan<long> childIds, Func<int, long> ownerOfEntity);
}
```

A merge's cause is the entity whose claim requested it. Merges that happen later through pending
partners keep the cause recorded at the request.

Order of notifications:
- `OnMerged` is raised *before* the absorbed region's sections, entities and timers move. An
  observer tracking per-region data (for example fight tags, for fusions) still sees both sides as
  they were. The moves then follow as `OnSectionOwnerChanged` and `OnEntityOwnerChanged`.
- A new region's initial state is implied by its creation reason. `NewClaim` and `Split` start
  Ready, `Transient` starts Transient. No `OnStateChanged` is raised for creation.
- At a tick boundary (no region ticking) every live region is Ready with no pending merges. The
  invariant checker enforces this.

## Invariant checker

```csharp
public enum InvariantRule { SectionSingleOwner = 1, ClaimsInRegion = 2, TickingRegionFrozen = 3,
                            LegalState = 4, EntitySingleOwner = 5, PortalCrossing = 6 }
public readonly record struct Violation(InvariantRule Rule, string Detail);

public static class InvariantChecker
{
    // Call at a tick boundary (no region ticking). Recomputes every claim with an independent,
    // straightforward walk (no fast path) and compares it with the library's state.
    // Returns the number of violations appended to `into`.
    public static int Check(Regionizer regionizer, List<Violation> into);
}
```

The library records rule 3 breaches (a ticking region's section set changed) and rule 4 breaches
(an illegal transition was attempted internally). `Check` reports them. Timer exactness (the issue's
seventh monitor) is checked by the simulator's timer monitor against its own shadow model.

## Behavioral guarantees (tested)

- Claims follow research D1 (geometry) and D2 (reachability). Merges follow D3, tick end follows
  D4, states follow D5 and timers follow D6.
- A ticking region never gains a section. New sections needed during its tick go into a transient
  region recorded as a pending merge partner.
- Iteration and tie-breaks are deterministic: ascending IDs, the lower ID absorbs, and the largest
  island keeps the parent ID.
- Steady-state calls (`MoveEntity`, `TryMarkTicking`, `RunDueTimers`, `MarkNotTicking` with no merge
  or split, `AddTimer` within existing capacity) allocate nothing.
