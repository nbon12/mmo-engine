# Contract: Selection report

One selection run writes one JSON report: every candidate's statuses, behavior results, costs, download
sizes, startup times, divergence results and fit-check verdicts, with the runner class and the commit
(spec Key Entities). The `vehicle-selection` job writes it, validates it, shows it in the job log and
summary, and uploads it as the `vehicle-selection` artifact; an agent commits the run's report as
`simcore/vehicles/selection/report.json` beside the decision record made from it (spec, Executable and
living spec). Every number is a measurement on the named runner class and build mode, never a threshold
in CI (Principle VI); the decision reads the numbers, CI never fails on one.

## How the report is produced

`simcore vehicles select run` ([vehicles-cli.md](vehicles-cli.md#vehicles-select-run)) drives the phases
below for each candidate (`bepu`, `jolt`, `rapier`) and for the fixture pair (`fixture`,
`fixture-perturbed`), in this order. A phase that fails records its reason and the run goes on with the
next candidate (FR-021); a phase is skipped only when an earlier phase's status already decides every
mode, and the skipped phases are listed.

1. **Fit check** (Rapier only, FR-030; [Fit check](#fit-check)).
2. **Build outcomes**: the native builds from the `vehicle-engines` artifact's outcome files and each host
   publish's outcome, per target and mode (`Built`, `BuildFailed`, `BuildTimedOut`, with the build, the
   failing step and the last 50 lines).
3. **License scan** per target ([license-scan.md](license-scan.md)).
4. **Server checks**, in a child process per candidate with the candidate's environment (Bepu's vector
   pin), each check on a fresh engine instance (research R22): US1 scenarios 1 to 7 on the committed
   streams; scenario 8 by `simcore streams verify --engine <engine>` in two more child processes;
   scenario 9 on one engine instance reloaded; scenario 10 at every window start; scenario 11; coverage
   of the generated streams (FR-010).
5. **Lock test** (scenario 12): `simcore vehicles lockcount` in a child process, under `LD_PRELOAD` for a
   native engine, on the instrumented copy for Bepu (research R16).
6. **Zero-allocation evidence**: the managed bytes per 1,000 warmed steps, per candidate (FR-025; the
   gating test is the vehicles test project's).
7. **Agreement per mode**: `simcore agreement prepare --engine`, the required builds' runners, and
   `simcore agreement compare` with that mode's `--require` set.
8. **Windowed runs and divergence** per required build and mode (US5, research R23).
9. **Measurements**: the server benchmarks (one process per candidate) and `lib/measure-vehicles.mjs`
   per mode.
10. **Parallel-phase evidence** (FR-027; research R27).
11. **Statuses**: FR-037's table, per candidate and mode.

## Example (abridged)

```json
{
  "formatVersion": 1,
  "runnerClass": "gha-ubuntu-latest-x64",
  "commit": "0123456789abcdef0123456789abcdef01234567",
  "ciRun": "45066104551",
  "vehicleRulesRevision": 1,
  "selectionConfigFingerprint": "3c1f0e9a7b2d4856",
  "vehicleConfigFingerprint": "9a0b1c2d3e4f5061",
  "modes": ["interp-prebuilt", "interp-relinked", "aot"],
  "candidates": [
    {
      "candidate": "jolt",
      "identity": { "engine": "jolt", "release": "v5.7.0", "fingerprint": "c0ffee0123456789" },
      "fitCheck": null,
      "builds": [
        { "target": "server", "mode": null, "outcome": "Built", "seconds": 41.2 },
        { "target": "browser-module", "mode": null, "outcome": "Built", "seconds": 38.0 },
        { "target": "browser-host", "mode": "aot", "outcome": "Built", "seconds": 71.5 }
      ],
      "license": {
        "server": { "verdict": "Pass", "blocked": [], "boundaryCount": 6, "componentCount": 440 },
        "browser": { "verdict": "Pass", "blocked": [], "boundaryCount": 0, "componentCount": 211 }
      },
      "checks": [
        { "scenario": "US1.1", "stream": "flat-accel-30hz", "verdict": "Pass",
          "measured": { "fractionOfTopSpeed": 0.998, "overshootFraction": 0.0, "maxTiltDeg": 1.4 },
          "bounds": { "minFractionOfTopSpeed": 0.95, "maxOvershootFraction": 0.02, "maxTiltDeg": 10 } }
      ],
      "lock": { "verdict": "Pass", "counted": [], "linkedLockSymbols": [], "nativeAllocations": 0 },
      "allocation": { "bytesPer1000Steps": 0 },
      "coverage": { "verdict": "Pass", "misses": [] },
      "statuses": [
        {
          "mode": "interp-prebuilt", "status": "Qualified", "reasons": [],
          "builds": [
            { "build": "node-interp", "result": "Match" },
            { "build": "node-interp-nojit", "result": "Match" },
            { "build": "chromium-worker", "result": "Match" }
          ],
          "reset": [ { "build": "chromium-worker", "result": "Match" } ]
        }
      ],
      "divergence": [
        { "mode": "interp-prebuilt", "build": "chromium-worker", "stream": "vseed-001", "windowSteps": 10,
          "position": { "max": 0.0, "p99": 0.0, "step": null, "vehicle": null },
          "orientation": { "max": 0.0, "p99": 0.0, "step": null, "vehicle": null },
          "linearVelocity": { "max": 0.0, "p99": 0.0, "step": null, "vehicle": null },
          "verdict": "WithinBounds" }
      ],
      "server": {
        "label": "measurement",
        "vehicleCounts": [
          { "vehicles": 1, "worldStep": { "meanNs": 9800, "p99Ns": 14100 }, "vehicleStep": { "meanNs": 9800, "p99Ns": 14100 } }
        ]
      },
      "browser": [
        {
          "mode": "interp-prebuilt", "produced": true, "reason": null,
          "download": { "rawBytes": 1612034, "brotliBytes": 402113, "gzipBytes": 530982 },
          "startup": { "coldStarts": 10, "totalMs": { "median": 210.4, "p90": 233.1 } },
          "stepCost": [
            { "runtime": "chromium-worker", "vehicles": 1, "meanMicros": 41.250, "p99Micros": 77.500 },
            { "runtime": "node", "vehicles": 1, "meanMicros": 36.875, "p99Micros": 70.125 }
          ]
        }
      ],
      "parallelEvidence": { "documented": true, "citation": "Jolt Docs/Architecture.md, Deterministic Simulation", "made": true, "threads": [1, 4], "sameHashes": true },
      "vectorPin": null
    }
  ],
  "fixtures": [ ]
}
```

The numbers above are placeholders that show the shape, not results.

## Fields

| Field | Rule |
| --- | --- |
| `formatVersion` | 1 |
| `runnerClass` | `MMO_RUNNER_CLASS`; the run refuses to start without it (exit 2) |
| `commit`, `ciRun` | `GITHUB_SHA` and `GITHUB_RUN_ID`, or `local` for both outside CI |
| `modes` | exactly the three modes, in this order |
| `candidates` | exactly `bepu`, `jolt`, `rapier`, in this order (the order Bepu, Jolt, Rapier of FR-026) |
| `fixtures` | exactly `fixture` and `fixture-perturbed`, the same shape as a candidate |
| `identity` | from the candidate's pin file; every artifact's recorded fingerprint must equal it |
| `fitCheck` | null except for Rapier: `{ order: ["c","b","d","f","a","e"], criteria: { "a": {verdict, notEvaluated, evidence}, … } }` |
| `builds[]` | one per target (`server`, `browser-module` for native engines, `browser-host` per mode); a build that did not run is `BuildNotRun` with its reason |
| `license` | per target; `blocked[]` items `{component, version, expression, reason}` |
| `checks[]` | one per US1 scenario 1 to 12 that ran, each with `verdict` (`Pass` or `Fail`), the stream, the measured values and the bounds; a check not run lists its reason |
| `statuses[]` | one per mode: the status (FR-037), every reason `{code, details}`, one result per required build of the mode (`Match`, `Diverged` or `NotRun`) and the browser reset result per required build |
| `divergence[]` | per mode, required build and stream: maxima, p99s, their step and vehicle, the window in steps, and the verdict |
| `server` | the benchmarks at 1, 16, 64 and 256 vehicles, `label: "measurement"`; Bepu's carries `vectorPin` |
| `browser[]` | per mode: `produced` and its `reason`; download added (raw, Brotli, gzip); startup over 10 cold starts; step cost in `node` and `chromium-worker` at 1, 16 and 64 vehicles, `meanMicros` and `p99Micros` with three decimals |
| `parallelEvidence` | FR-027's evidence |
| `vectorPin` | Bepu only: the setting and its server-wide effect (research R8) |

Reason details, per code (FR-037): `FitCheckFailed` `{criterion}`; `BuildFailed`/`BuildTimedOut`
`{build, step, lastLines}`; `ModeNotProduced`/`BuildNotRun` `{build, reason}`; `LicenseNotAllowed`
`{component, version, expression}`; `BehaviorCheckFailed` `{scenario, measured, bound}`;
`ServerNotReproducible` `{stream, step}`; `ResetNotEquivalent` `{stream, restartStep, firstDifferingStep}`;
`RejectionNotNeutral` `{stream, step}`; `TakesLock` `{function}`; `StepFaulted` `{stream, step, fault}`;
`StreamNotComparable` `{stream, found, expected}`; `CoverageNotMet` `{seed, condition}`; `RecordDiverged`
`{build, stream, step, fields}`; `ResetDiverged` `{build, stream, restartStep, firstDifferingStep}`.

## Statuses

`StatusTable.Decide(candidateResult, mode)` applies FR-037's table: every condition that holds adds its
reason, and the status is the first of NotTested, NotBuilt, LicenseBlocked, Failed, Diverged in that
order, else Qualified. A server-side condition (fit check, license, US1 checks, coverage, reproducibility,
server reset, lock, step faults) holds in every mode; a build, agreement or browser-reset condition holds
only in its mode. A required build that did not run is `NotRun` and makes the mode NotBuilt
(`BuildNotRun`).

## Measurements

- **Server** (FR-023): `benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks`, BenchmarkDotNet 0.15.8,
  run once per candidate in its own process (`--engine <engine>`); a world step of n vehicles in the
  arena, every vehicle driving seeded commands; `vehicleStep` is the world step divided by n.
- **Browser** (FR-024), by `tools/agreement/lib/measure-vehicles.mjs` with 015's method
  ([measurement-report.md](../../015-shared-simulation-core/contracts/measurement-report.md#how-each-number-is-measured)):
  download from the boot manifest plus the host's `engine/` files and their `.br` and `.gz` siblings,
  minus 015's `MmoEngine.SimCore.Wasm` download in the same mode; 10 cold starts to the first committed
  vehicle step, each checked against the server's first record of `flat-accel-30hz`; step cost over
  3,008 timed steps after 512 warm-up steps in 16-command batches, in worlds of 1, 16 and 64 vehicles
  (vehicle 0 driven, the others parked).
- A mode or candidate that could not be measured has `produced: false` and its reason (FR-024).

## Fit check

`FitCheck.Evaluate` (`src/MmoEngine.SimCore.Vehicles.Selection/FitCheck/`) runs the criteria in the
order c, b, d, f, a, e (research R12). Each criterion's verdict is `pass` or `fail`, with `notEvaluated`
true when it could not be evaluated without linking a component (c) rejected, and its evidence:

| Criterion | Evaluated by |
| --- | --- |
| (c) | the license scan of the Rapier manifest's `cargo metadata` for both targets |
| (b) | the manifest: an exact `=` version, `enhanced-determinism` on, `simd8` off, no `[patch]`, a `Cargo.lock` with a checksum per crate, and every build command with `--locked` |
| (d) | `RapierIsolationTests`' static check (no file of the scenarios, formats, runners, `IVehicleEngine` or the other adapters references Rapier's library, namespace or adapter types) and the adapter compiling against `IVehicleEngine` |
| (f) | the non-blank lines of `src/MmoEngine.SimCore.Vehicles.Rapier/**` (C#, Rust, scripts, glue), at most 1,500 |
| (a) | the shim building against the crates.io API with no patch, with every `IVehicleEngine` operation exported |
| (e) | each build within 15 minutes, and `flat-accel-30hz` run to completion on the server build and the `interp-prebuilt` browser build |

Rapier is a full candidate only when all six pass. Otherwise it is NotTested (`FitCheckFailed`) in every
mode, with every failed criterion listed and the first one in the order as the status's criterion. The
committed fit-check fixtures in `simcore/vehicles/fixtures/fitcheck/` (one failing per criterion, one all
passing) drive the orchestration in tests (US6 scenarios 2 and 3); with all passing, the orchestrator's
plan schedules Rapier for every phase Bepu and Jolt get, and its report entry has their shape.

## What `validate-report` checks

`simcore vehicles select validate-report <file>` prints one `path: problem` line per problem and exits
0 valid, 1 invalid, 2 unreadable, with 015's `measure validate-report` conventions (strict UTF-8, one line
per problem, a bounded read through `BoundedRead.ReadAtMost`), except that the limit is 16 MiB, because a
report with every divergence result is larger than a measurement report. It checks:

- every field above present with its type and unit, and every enumerated value known;
- exactly the three candidates and the two fixtures, each with exactly one status per mode;
- each status's required builds exactly the mode's (FR-018), each with one result;
- each status consistent with its reasons under the table's order (a Qualified status has no reason; a
  status matches the first reason's row);
- every measurement labelled with the runner class, the build mode and the engine identity; a missing
  measurement has a reason (US4 scenario 5);
- every reason's details present for its code;
- the fit check present for Rapier with six criteria;
- no absolute path and no clock time in any string.

## Tests

In `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/`: a valid fixture report passing; one Theory row per
rule failing on a fixture with that defect; `StatusTable` over a fixture input for every row of FR-037
and for combined rows (US2 scenario 11); the orchestration on fixture inputs for every fault and input
fault (US1 scenario 13, US2 scenarios 9 to 11, US6 scenarios 2 and 3), each exiting 0.
