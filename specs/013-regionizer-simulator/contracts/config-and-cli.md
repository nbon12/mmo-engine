# Contract: Configuration files and CLIs

## Run configuration (JSON)

A run config names a scenario and overrides any defaults. Every field is validated before the run
starts (FR-026). An invalid field stops the run with exit code 2 and a message that names its JSON
path, for example `regionizer.sectionSize: must be a power of two between 1 and 1024`.

```json
{
  "scenario": "Baseline",
  "seed": 1,
  "scheduling": { "mode": "Lockstep", "baseStepHz": 60, "tickHz": 30, "degradedTickHz": 20,
                  "dropTo20HzProbability": 0.01, "dropTicksMin": 5, "dropTicksMax": 60 },
  "durationTicks": 18000,
  "regionizer": {
    "sectionSize": 16, "anchoring": "Section", "mergeGap": 0,
    "claimRadius": { "infantry": 16, "groundVehicle": 24, "aircraft": 16, "traveller": 16, "capturePointZone": 20 },
    "split": { "minSections": 32, "minDeadPercent": 25, "minAgeSeconds": 2 },
    "timerMode": "TickCounter"
  },
  "movement": { "speedCeiling": { "infantry": 7, "groundVehicle": 30, "aircraft": 120 },
                "redeployProbabilityPerSecond": 0.002 },
  "map": { "widthMetres": 8000, "heightMetres": 8000, "lanes": 3, "file": null },
  "population": { "total": 5000, "limit": 10000 },
  "airLayer": { "enabled": false, "altitudeMetres": 150, "hysteresisMetres": 20 },
  "timers": { "setProbabilityPerEntityPerSecond": 0.01, "delayTicksMin": 30, "delayTicksMax": 900 },
  "metrics": { "metricsEveryTicks": 1, "islandCheckEveryTicks": 1, "crossRegionPairsEveryTicks": 30,
               "deferredRangeMetres": 300, "readOnlyRangeMetres": 1000,
               "targetFightSize": 1200, "bookkeepingBudgetMicros": 1000 },
  "scenarioParameters": { "...": "scenario-specific, see each scenario's defaults file" },
  "trace": { "record": null, "replay": null }
}
```

`baseStepHz` must be a multiple of `tickHz` and of `degradedTickHz`, and `degradedTickHz` must be
lower than `tickHz` (exit 2 otherwise). `dropTo20HzProbability` is the per-tick-end probability of
dropping to `degradedTickHz`; the name keeps the issue's wording.

The defaults for every scenario live in `simulator/scenarios/<scenario>.json`. A run config is
deep-merged over them. The environment name (`DOTNET_ENVIRONMENT`) must be Development, Dev, Test,
Staging or Production, and is Development when unset.

## Sweep specification (JSON)

```json
{
  "name": "infantry-radius",
  "base": { "...": "a run configuration" },
  "seeds": 10,
  "sweepSeed": 20261103,
  "mode": "OneAtATime",
  "parameters": [ { "path": "regionizer.claimRadius.infantry", "values": [8, 12, 16, 20, 24, 28, 32, 36, 40] } ]
}
```

- `mode` is `OneAtATime` (each parameter stepped alone around `base`) or `Grid` (the cartesian
  product).
- Seed *i* is the same in every cell: SplitMix64 of (`sweepSeed`, *i*). This gives common random
  numbers across cells.
- Cells are numbered in a fixed order. Runs execute in parallel up to `--parallelism` (default:
  processor count). The output layout is `<out>/<sweep>/<cellId>/seed-<i>/` (run folders), plus
  `<out>/<sweep>/sweep.json` (the cell table) and `<out>/<sweep>/cells.csv`. The cells file has one
  row per cell and seed, with the summary's deterministic columns and the bookkeeping p99.

`sweep.json` is the authoritative index that the memo generator reads. Run folders follow
[run-output.md](run-output.md).

```json
{
  "formatVersion": 1,
  "name": "infantry-radius",
  "scenario": "Baseline",
  "mode": "OneAtATime",
  "sweepSeed": 20261103,
  "seeds": [ { "index": 0, "seed": 1234 } ],
  "cells": [
    {
      "cellId": "c000",
      "parameters": { "regionizer.claimRadius.infantry": 8 },
      "runs": [ { "seedIndex": 0, "seed": 1234, "folder": "c000/seed-0", "completed": true } ]
    }
  ]
}
```

`parameters` holds only the swept paths and their values for that cell, keyed by JSON path. The
`base` config's other values are in each run's manifest.

## `regionizer-sim` CLI

| Command | Purpose | Exit codes |
| --- | --- | --- |
| `run <config.json> --out <dir> [--seed N] [--ticks N] [--scenarios <dir>]` | One run | 0 clean, 3 invariant violation or timer fault, 2 invalid config, 1 other error |
| `sweep <sweep.json> --out <dir> [--parallelism N] [--scenarios <dir>]` | A sweep | 0 all clean, 3 any violation, 2 invalid spec, 1 a run failed with an error |
| `replay <trace.csv> --config <config.json> --out <dir> [--scenarios <dir>]` | A run driven by a recorded trace | as `run` |
| `check --out <dir> [--options <check-options.json>]` | The analytic checks (floor, merge distance, lane bridging); writes `checks.json` | 0 all within tolerance, 4 any outside |
| `memo <results-dir> --rules <memo-rules.json> --out <memo.md>` | Writes the decision memo from sweep results | 0, or 4 when a value can't be read (the row is marked) |

## `portal-validator` CLI

`portal-validator <map.json> [--json]`. Exit codes are in [map-format.md](map-format.md).
