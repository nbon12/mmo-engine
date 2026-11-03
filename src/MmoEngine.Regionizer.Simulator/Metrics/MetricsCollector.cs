using System.Diagnostics;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Metrics;

/// <summary>
/// Collects the run metrics (FR-024) as an <see cref="IRegionizerObserver"/>. It keeps per-region
/// player counts and fight-tag counts from <see cref="OnEntityOwnerChanged"/>; because the core raises
/// <see cref="OnMerged"/> before the absorbed region's data moves, both sides are still intact when a
/// merge is classified as a fusion (each side holds a fight tag the other lacks). Events go to the
/// sink as they happen; <see cref="EndTick"/> produces each tick's row.
/// </summary>
public sealed class MetricsCollector : IRegionizerObserver
{
    private readonly WorldState _state;
    private readonly MetricsConfig _config;
    private readonly Action<RunEvent> _emit;
    private readonly int _tagSlots;
    private readonly Dictionary<long, RegionStats> _regions = [];
    private readonly long[] _owner;
    private readonly List<int> _players = [];
    private readonly HashSet<long> _fusedPairs = [];
    private readonly long[] _mergesByClass = new long[ClassCount];
    private readonly long[] _fusionsByClass = new long[ClassCount];
    private readonly Dictionary<string, List<double>> _samples = [];
    private readonly List<double> _fusedPairSamples = [];
    private IslandDetector? _islands;
    private long _tick;
    private long _step;
    private long _rowMerges;
    private long _rowSplits;
    private long _rowFusions;
    private long _rowChurn;

    private const int ClassCount = 6;

    public MetricsCollector(WorldState state, MetricsConfig config, Action<RunEvent> emit)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(emit);
        _state = state;
        _config = config;
        _emit = emit;
        _owner = new long[state.Count];
        var maxTag = -1;
        for (var e = 0; e < state.Count; e++)
        {
            maxTag = Math.Max(maxTag, state.FightTag[e]);
            if (EntitySpec.IsPlayerClass(state.Class[e]))
            {
                _players.Add(e);
            }
        }

        _tagSlots = maxTag + 1;
        foreach (var name in SampledMetrics)
        {
            _samples[name] = [];
        }
    }

    /// <summary>The metrics summarized at p50, p95, p99, max and mean, in summary order.</summary>
    public static IReadOnlyList<string> SampledMetrics { get; } =
    [
        "regionCount", "largestRegionPlayers", "largestRegionShare", "sectionChurn", "crossPairsDeferred",
        "crossPairsReadOnly", "bookkeepingMicros", "serializedMicros", "regionLifetimeSteps", "splitLatencyTicks",
    ];

    public long Merges { get; private set; }

    public long Splits { get; private set; }

    public long Fusions { get; private set; }

    /// <summary>Merge requests whose other region was ticking at that moment (merges landing on a ticking region).</summary>
    public long MergesRequestedAgainstTicking { get; private set; }

    public long MergeRequests { get; private set; }

    /// <summary>Ticks on which some region holding two or more fight tags had more players than the target fight size.</summary>
    public long OversizedFusionTicks { get; private set; }

    /// <summary>Merges by the class of the entity whose claim caused them.</summary>
    public long MergesCausedBy(EntityClass cls) => _mergesByClass[(int)cls];

    /// <summary>Fusions by the class of the entity whose claim caused them.</summary>
    public long FusionsCausedBy(EntityClass cls) => _fusionsByClass[(int)cls];

    /// <summary>The values recorded for one of <see cref="SampledMetrics"/>.</summary>
    public IReadOnlyList<double> Samples(string metric) => _samples[metric];

    /// <summary>Distinct fused fight pairs per recorded tick.</summary>
    public IReadOnlyList<double> FusedFightPairSamples => _fusedPairSamples;

    /// <summary>Players in a live region, or 0.</summary>
    public int PlayersIn(long regionId) => _regions.TryGetValue(regionId, out var r) ? r.Players : 0;

    /// <summary>Distinct fight tags in a live region, or 0.</summary>
    public int FightTagsIn(long regionId) => _regions.TryGetValue(regionId, out var r) ? r.DistinctTags : 0;

    /// <summary>Sets the tick and step that events are stamped with.</summary>
    public void SetClock(long tick, long step)
    {
        _tick = tick;
        _step = step;
    }

    /// <summary>Records a violation or timer fault event.</summary>
    public void Emit(in RunEvent e) => _emit(e);

    /// <summary>
    /// Ends a continent tick: computes its row from the live regions in ascending ID, samples
    /// cross-region pairs and islands when due, and resets the per-row counters when the row is recorded.
    /// </summary>
    public TickRow EndTick(Regionizer regionizer, long tick, long step, double bookkeepingMicros, double serializedMicros, int violations, ulong stateHash, bool record)
    {
        ArgumentNullException.ThrowIfNull(regionizer);
        SetClock(tick, step);
        var regionCount = 0;
        var largest = 0;
        var totalPlayers = 0;
        var oversized = false;
        _fusedPairs.Clear();
        foreach (var id in regionizer.RegionIds)
        {
            if (!_regions.TryGetValue(id, out var stats))
            {
                continue;
            }

            if (stats.Players > 0)
            {
                regionCount++;
                totalPlayers += stats.Players;
                largest = Math.Max(largest, stats.Players);
            }

            if (stats.DistinctTags >= 2)
            {
                oversized |= stats.Players > _config.TargetFightSize;
                for (var a = 0; a < _tagSlots; a++)
                {
                    if (stats.TagCounts[a] == 0)
                    {
                        continue;
                    }

                    for (var b = a + 1; b < _tagSlots; b++)
                    {
                        if (stats.TagCounts[b] > 0)
                        {
                            _fusedPairs.Add(((long)a * _tagSlots) + b);
                        }
                    }
                }
            }
        }

        if (oversized)
        {
            OversizedFusionTicks++;
        }

        if (tick % _config.IslandCheckEveryTicks == 0)
        {
            SampleIslands(regionizer, tick);
        }

        long? deferred = null;
        long? readOnly = null;
        if (tick % _config.CrossRegionPairsEveryTicks == 0)
        {
            var (d, r) = CrossRegionPairs.Count(_players, _state.X, _state.Y, e => _owner[e], _config.DeferredRangeMetres, _config.ReadOnlyRangeMetres);
            deferred = d;
            readOnly = r;
            _samples["crossPairsDeferred"].Add(d);
            _samples["crossPairsReadOnly"].Add(r);
        }

        var share = totalPlayers == 0 ? 0 : largest / (double)totalPlayers;
        var row = new TickRow(
            tick,
            step,
            regionCount,
            largest,
            share,
            _rowMerges,
            _rowSplits,
            _rowFusions,
            _rowChurn,
            deferred,
            readOnly,
            bookkeepingMicros,
            serializedMicros,
            oversized,
            _fusedPairs.Count,
            violations,
            stateHash);
        if (record)
        {
            _samples["regionCount"].Add(regionCount);
            _samples["largestRegionPlayers"].Add(largest);
            _samples["largestRegionShare"].Add(share);
            _samples["sectionChurn"].Add(_rowChurn);
            _samples["bookkeepingMicros"].Add(bookkeepingMicros);
            _samples["serializedMicros"].Add(serializedMicros);
            _fusedPairSamples.Add(_fusedPairs.Count);
            _rowMerges = 0;
            _rowSplits = 0;
            _rowFusions = 0;
            _rowChurn = 0;
        }

        return row;
    }

    public void OnRegionCreated(long regionId, RegionCreationReason reason, long parentId) =>
        _regions[regionId] = new RegionStats(reason, _tagSlots)
        {
            State = reason == RegionCreationReason.Transient ? RegionState.Transient : RegionState.Ready,
        };

    public void OnStateChanged(long regionId, RegionState from, RegionState to)
    {
        if (_regions.TryGetValue(regionId, out var stats))
        {
            stats.State = to;
        }
    }

    public void OnMergeRequested(long a, long b, EntityClass causeClass, int causeEntityId)
    {
        MergeRequests++;
        if (_regions.TryGetValue(b, out var other) && other.State == RegionState.Ticking)
        {
            MergesRequestedAgainstTicking++;
        }
    }

    /// <summary>
    /// Counts a merge between two real regions. Absorbing a transient helper region (created by the
    /// core to hold sections a ticking region needed) is bookkeeping, not a merge of two regions: the
    /// merge it stands for is counted when the regions themselves join.
    /// </summary>
    public void OnMerged(long absorberId, long absorbedId, EntityClass causeClass, int causeEntityId)
    {
        if (!_regions.TryGetValue(absorberId, out var absorber) || !_regions.TryGetValue(absorbedId, out var absorbed))
        {
            return;
        }

        absorber.FirstTwoIslandTick = -1;
        if (absorber.Reason == RegionCreationReason.Transient || absorbed.Reason == RegionCreationReason.Transient)
        {
            return;
        }

        Merges++;
        _rowMerges++;
        _mergesByClass[(int)causeClass]++;
        _emit(new RunEvent(_tick, _step, RunEventType.Merge, absorberId, absorbedId, causeClass, causeEntityId));
        if (IsFusion(absorber.TagCounts, absorbed.TagCounts))
        {
            Fusions++;
            _rowFusions++;
            _fusionsByClass[(int)causeClass]++;
            _emit(new RunEvent(_tick, _step, RunEventType.Fusion, absorberId, absorbedId, causeClass, causeEntityId, absorber.Players + absorbed.Players));
        }
    }

    /// <summary>A merge is a fusion when each side holds a fight tag the other does not (spec, Clarifications).</summary>
    public static bool IsFusion(ReadOnlySpan<int> tagCountsA, ReadOnlySpan<int> tagCountsB)
    {
        var aOnly = false;
        var bOnly = false;
        var n = Math.Max(tagCountsA.Length, tagCountsB.Length);
        for (var t = 0; t < n; t++)
        {
            var a = t < tagCountsA.Length ? tagCountsA[t] : 0;
            var b = t < tagCountsB.Length ? tagCountsB[t] : 0;
            aOnly |= a > 0 && b == 0;
            bOnly |= b > 0 && a == 0;
        }

        return aOnly && bOnly;
    }

    public void OnSplit(long parentId, ReadOnlySpan<long> childIds)
    {
        Splits++;
        _rowSplits++;
        _emit(new RunEvent(_tick, _step, RunEventType.Split, parentId, childIds.Length));
        if (_regions.TryGetValue(parentId, out var parent) && parent.FirstTwoIslandTick >= 0)
        {
            var latency = _tick - parent.FirstTwoIslandTick;
            _samples["splitLatencyTicks"].Add(latency);
            _emit(new RunEvent(_tick, _step, RunEventType.SplitLatency, parentId, Value: latency));
            parent.FirstTwoIslandTick = -1;
        }
    }

    public void OnRegionDied(long regionId, long ageSteps)
    {
        _emit(new RunEvent(_tick, _step, RunEventType.RegionDied, regionId, Value: ageSteps));
        if (_regions.Remove(regionId, out var stats) && stats.Reason != RegionCreationReason.Transient)
        {
            _samples["regionLifetimeSteps"].Add(ageSteps);
        }
    }

    public void OnSectionOwnerChanged(int sectionIndex, long oldOwner, long newOwner)
    {
        _rowChurn++;
    }

    public void OnEntityOwnerChanged(int entityId, long oldOwner, long newOwner)
    {
        _owner[entityId] = newOwner;
        var player = EntitySpec.IsPlayerClass(_state.Class[entityId]);
        var tag = _state.FightTag[entityId];
        if (oldOwner != 0 && _regions.TryGetValue(oldOwner, out var from))
        {
            from.Remove(player, tag);
        }

        if (newOwner != 0 && _regions.TryGetValue(newOwner, out var to))
        {
            to.Add(player, tag);
        }
    }

    public void OnRegionTickStarted(long regionId, long tickCounter, long now)
    {
    }

    public void OnTimerFired(int timerId, int entityId, long regionId, long now, long regionTick)
    {
    }

    /// <summary>Stopwatch ticks as microseconds.</summary>
    public static double TimestampToMicros(long timestampTicks) => timestampTicks * 1_000_000.0 / Stopwatch.Frequency;

    private void SampleIslands(Regionizer regionizer, long tick)
    {
        _islands ??= new IslandDetector(regionizer.Topology);
        foreach (var id in regionizer.RegionIds)
        {
            if (!_regions.TryGetValue(id, out var stats))
            {
                continue;
            }

            if (regionizer.GetRegion(id).Entities.Length < 2)
            {
                stats.FirstTwoIslandTick = -1;
                continue;
            }

            if (_islands.CountIslands(regionizer, id) >= 2)
            {
                if (stats.FirstTwoIslandTick < 0)
                {
                    stats.FirstTwoIslandTick = tick;
                }
            }
            else
            {
                stats.FirstTwoIslandTick = -1;
            }
        }
    }

    private sealed class RegionStats(RegionCreationReason reason, int tagSlots)
    {
        public RegionCreationReason Reason { get; } = reason;

        public RegionState State { get; set; }

        public int Players { get; private set; }

        public int[] TagCounts { get; } = new int[tagSlots];

        public int DistinctTags { get; private set; }

        /// <summary>The first sampled tick at which it held two or more islands, or −1.</summary>
        public long FirstTwoIslandTick { get; set; } = -1;

        public void Add(bool player, int tag)
        {
            if (player)
            {
                Players++;
            }

            if (tag >= 0 && TagCounts[tag]++ == 0)
            {
                DistinctTags++;
            }
        }

        public void Remove(bool player, int tag)
        {
            if (player)
            {
                Players--;
            }

            if (tag >= 0 && --TagCounts[tag] == 0)
            {
                DistinctTags--;
            }
        }
    }
}
