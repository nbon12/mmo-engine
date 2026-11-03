# Contract: Run output folder (format version 1)

Every `regionizer-sim run` (and every run inside a sweep) writes one folder. Sub-spec 2's loaders read
these files, so changing them means bumping `formatVersion`.

```text
<run-folder>/
├── _incomplete          # present while the run is in progress; removed on clean finish
├── manifest.json
├── ticks.csv            # one row per recorded tick
├── events.csv           # merges, splits, fusions, region deaths, violations, timer faults
└── summary.json
```

A run that starts in a non-empty folder deletes the folder's contents first. A run interrupted
before finishing keeps `_incomplete`.

## manifest.json

```json
{
  "formatVersion": 1,
  "runId": "baseline-s16-r24-seed3-<configHash8>",
  "kind": "simulator",
  "gitSha": "<40 hex or 'unknown'>",
  "configHash": "<16 hex, XxHash64 of the canonical effective config>",
  "scenario": "Baseline",
  "seed": 3,
  "scheduling": "Lockstep",
  "parameters": { "...": "the full effective configuration, canonical JSON" },
  "runtime": {
    "framework": ".NET 9.0.x", "os": "...", "architecture": "Arm64",
    "processorCount": 10, "machineName": "<host>",
    "hardwareIntrinsics": { "DOTNET_EnableAVX2": "<env or unset>", "...": "..." },
    "vectorIsHardwareAccelerated": true
  },
  "startedUtc": "2026-11-03T13:00:00Z", "finishedUtc": "...", "ticks": 18000,
  "metricsEveryTicks": 1, "islandCheckEveryTicks": 1, "crossRegionPairsEveryTicks": 30
}
```

## ticks.csv

Header, then one row every `metricsEveryTicks` ticks (default 1). Columns, in this order:

`tick,timeSteps,regionCount,largestRegionPlayers,largestRegionShare,merges,splits,fusions,`
`fusedFightPairs,sectionChurn,crossPairsDeferred,crossPairsReadOnly,bookkeepingMicros,`
`serializedMicros,oversizedFusion,violations,stateHash`

- `fusedFightPairs` is the number of distinct fight-tag pairs that share a region after this tick.
- Cross pairs are counted once each, under the smallest range they fall within: deferred
  (≤ `deferredRangeMetres`), otherwise read-only (≤ `readOnlyRangeMetres`).

- `tick` is the continent tick (30 Hz) index. `timeSteps` is the time base in 60 Hz steps.
- `regionCount` counts regions holding at least one player (infantry, ground vehicle, aircraft or
  traveller).
- `merges`, `splits`, `fusions` and `sectionChurn` count events since the previous row.
- Cross-pair columns are empty on ticks where they are not sampled.
- `oversizedFusion` is 1 if any region holding two or more fight tags has more players than the
  configured target fight size.
- `stateHash` is the chained 64-bit hash (16 hex digits) after this tick (research D8).
- Numbers use the invariant culture. Doubles are written round-trip (`R`).

## events.csv

`tick,timeSteps,type,regionA,regionB,causeClass,causeEntity,value,detail`

`type` is one of:

| Type | Fields |
| --- | --- |
| `Merge` | A = absorber, B = absorbed |
| `Split` | A = parent, B = number of children |
| `Fusion` | A merge joining fight tags. A = absorber, B = absorbed, value = players after |
| `RegionDied` | A = region, value = age in steps |
| `SplitLatency` | A = region, value = ticks from the first sampled two-island tick to the split |
| `Violation` | detail = rule and message. A run stops at its first violation. |
| `TimerFault` | A = region, detail = timer ID, expected and actual |

## summary.json

```json
{
  "formatVersion": 1, "runId": "...", "completed": true, "ticks": 18000,
  "firstViolation": null,
  "metrics": {
    "regionCount":          { "p50": 0, "p95": 0, "p99": 0, "max": 0, "mean": 0 },
    "largestRegionPlayers": { "...": "same shape" },
    "largestRegionShare":   {}, "sectionChurn": {}, "crossPairsDeferred": {}, "crossPairsReadOnly": {},
    "bookkeepingMicros": {}, "serializedMicros": {}, "regionLifetimeSteps": {}, "splitLatencyTicks": {}
  },
  "rates": { "mergesPerSecondByClass": { "Infantry": 0.0, "...": 0.0 }, "splitsPerSecond": 0.0,
             "fusionsPerSecondByClass": { "...": 0.0 } },
  "totals": { "merges": 0, "splits": 0, "fusions": 0, "oversizedFusionTicks": 0,
              "timersSet": 0, "timersFired": 0 },
  "fusedFightPairs": { "p50": 0, "p95": 0, "p99": 0, "max": 0, "mean": 0 },
  "scheduling": { "baseStepHz": 60, "tickHz": 30, "degradedTickHz": 20, "timerOverheadNanos": 0 },
  "budget": { "bookkeepingP99Micros": 0.0, "budgetMicros": 1000.0, "withinBudget": true },
  "wallClock": { "seconds": 0.0 }
}
```

Percentiles use the nearest-rank method over every recorded tick (or every sample, for sampled
metrics). Every value except `bookkeeping*`, `serialized*`, `budget.bookkeepingP99Micros`,
`budget.withinBudget` and `wallClock` is deterministic for a given manifest.
