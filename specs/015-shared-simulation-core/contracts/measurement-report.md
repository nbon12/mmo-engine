# Contract: Measurement report

The `measure` job (`.github/workflows/simcore.yml`) writes one JSON report per run and uploads it as
the `simcore-measurement` artifact. It records the server build's step costs (US7 scenario 1,
FR-030) and, for each WebAssembly build mode, the download size, the time to the first completed step
and the cost per step, or why the mode could not be produced (US7 scenario 2, FR-031). Every number
is a measurement on the named runner class, never a target or a gate (constitution Principle VI).
Gate 9 chooses the WebAssembly build mode from these numbers later; this feature does not.

`simcore measure validate-report <file>` checks the report ([simcore-cli.md](simcore-cli.md)), which
automates US7's Independent Test and SC-007.

## How the report is produced

These are the `measure` job's steps that feed the report, in order. The environment of both SimCore
jobs sets `MMO_RUNNER_CLASS: gha-ubuntu-latest-x64`, `DOTNET_EnableAVX512F: "0"` and
`DOTNET_TieredPGO: "0"`.

1. **Server benchmarks.** `dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Benchmarks -- --filter '*'`.
   BenchmarkDotNet 0.15.8 with `MemoryDiagnoser`. The project's `SimCoreMeasurementExporter` writes
   `BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json`, which is the report's `server`
   object, and adds a P99 column to the console table.
2. **The prebuilt interpreted build**, published before the workload is installed:
   `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o <dir>`. Without the workload, publish
   uses the prebuilt runtime [measured].
3. **The workload.** `dotnet workload install wasm-tools`, continuing on error. With the workload,
   `InvariantGlobalization=true` forces a native relink on every publish [measured], so the prebuilt
   build has to come first.
4. **The relinked interpreted build** (the same publish command) and **the AOT build** (with
   `-p:RunAOTCompilation=true`), each only when the workload installed. A mode that cannot be
   produced is recorded as `{"produced": false, "reason": "…"}`.
5. **Agreement on the new modes.** `simcore agreement prepare`, the Node agreement run with
   `AGREEMENT_BUILD=node-interp-relinked` and `node-aot` for the modes produced, then
   `simcore agreement compare --require` with those builds. The AOT agreement gates the job whenever
   AOT was produced ([research.md](../research.md) R17). The prebuilt build's agreement is the
   `agreement` job's.
6. **The WebAssembly measurements**, by `tools/agreement/lib/measure.mjs`:

   ```bash
   node tools/agreement/lib/measure.mjs \
     --work "$WORK" \
     --server-benchmarks BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json \
     --mode interp-prebuilt="$PUBLISH/prebuilt" \
     --mode interp-relinked="$PUBLISH/relinked" \
     --unproduced aot="dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log" \
     --out "$RUNNER_TEMP/simcore-measurement.json"
   ```

   Each of the three modes is given exactly once, with `--mode <mode>=<publish dir>` or
   `--unproduced <mode>="<reason>"`. It exits 0 when the report is written; 1 when a measurement
   failed (a boot error, the content-type warning, a missing compressed file); 2 on a usage error,
   including an unset `MMO_RUNNER_CLASS`, because a number without its runner class is not usable.
7. **Validation**, then upload: `simcore measure validate-report "$RUNNER_TEMP/simcore-measurement.json"`.

The job also runs the exhaustive trigonometric sweeps, with `MMO_EXHAUSTIVE_MATH=1`
([research.md](../research.md) R3). They write each function's maximum error in ulp to the job
summary and are not part of this report. The fixed trig table in the pull-request tests is the gate
for the error bounds (SC-004).

## Example

A complete report. The values are illustrative, in the range the research prototypes measured on the
sandbox ([research.md](../research.md) R8, R17); they are not runner-class measurements.

```json
{
  "formatVersion": 1,
  "kind": "simcore-measurement",
  "label": "measurement",
  "recordedAt": "2026-11-14T10:00:00Z",
  "commit": "9b1f0c2d3e4a5b6c7d8e9f0a1b2c3d4e5f6a7b8c",
  "runnerClass": "gha-ubuntu-latest-x64",
  "machine": {
    "cpuModel": "AMD EPYC 7763 64-Core Processor",
    "logicalCores": 4,
    "architecture": "x64",
    "os": "Linux 6.11.0-1018-azure"
  },
  "dotnet": {
    "sdkVersion": "10.0.115",
    "runtimeVersion": "10.0.15",
    "environment": {"DOTNET_EnableAVX512F": "0", "DOTNET_TieredPGO": "0"}
  },
  "node": {"version": "v22.23.0"},
  "chromium": {"version": "153.0.6943.16"},
  "server": {
    "tool": "BenchmarkDotNet 0.15.8",
    "label": "measurement",
    "runnerClass": "gha-ubuntu-latest-x64",
    "benchmarks": [
      {"step": "movement", "case": "flat-run", "meanNs": 2600.4, "p99Ns": 2712.9, "iterations": 100, "allocatedBytesPerOp": 0},
      {"step": "movement", "case": "wall-slide", "meanNs": 3702.1, "p99Ns": 3891.5, "iterations": 100, "allocatedBytesPerOp": 0},
      {"step": "movement", "case": "corner-wedge", "meanNs": 10108.7, "p99Ns": 10633.0, "iterations": 100, "allocatedBytesPerOp": 0},
      {"step": "weapon", "case": "rifle-hold", "meanNs": 180.2, "p99Ns": 192.6, "iterations": 100, "allocatedBytesPerOp": 0},
      {"step": "projectile", "case": "launcher-flight", "meanNs": 1402.8, "p99Ns": 1519.3, "iterations": 100, "allocatedBytesPerOp": 0},
      {"step": "hitboxBuild", "case": "standing", "meanNs": 85.1, "p99Ns": 90.4, "iterations": 100, "allocatedBytesPerOp": 0},
      {"step": "stateHash", "case": "one-character", "meanNs": 129.3, "p99Ns": 134.0, "iterations": 100, "allocatedBytesPerOp": 0}
    ]
  },
  "wasm": {
    "modes": {
      "interp-prebuilt": {
        "produced": true,
        "reason": null,
        "build": {"aot": false, "relinked": false, "workload": false},
        "runtime": {
          "productVersion": "10.0.15",
          "gitHash": "4c8d2e6f1a3b5c7d9e0f2a4b6c8d0e1f3a5b7c9d",
          "buildConfiguration": "Release",
          "wasmEnableSIMD": true,
          "wasmEnableExceptionHandling": true,
          "wasmEnableThreads": false,
          "debugLevel": 0,
          "globalizationMode": "invariant",
          "runtimeOptions": [],
          "jiterpreter": true
        },
        "download": {
          "files": [
            {"name": "dotnet.js", "group": "loader", "rawBytes": 37000, "brotliBytes": 12000, "gzipBytes": 14000},
            {"name": "dotnet.native.kakcbpgnoi.js", "group": "jsModuleNative", "rawBytes": 140000, "brotliBytes": 32000, "gzipBytes": 38000},
            {"name": "dotnet.runtime.v2rwe8u38b.js", "group": "jsModuleRuntime", "rawBytes": 205000, "brotliBytes": 52000, "gzipBytes": 60000},
            {"name": "dotnet.native.fdp95x8kng.wasm", "group": "wasmNative", "rawBytes": 3000000, "brotliBytes": 976000, "gzipBytes": 1190000},
            {"name": "System.Private.CoreLib.2kv5b1q4gv.wasm", "group": "coreAssembly", "rawBytes": 1007893, "brotliBytes": 315000, "gzipBytes": 380000},
            {"name": "System.Runtime.InteropServices.JavaScript.r8sucgoam9.wasm", "group": "coreAssembly", "rawBytes": 45000, "brotliBytes": 17000, "gzipBytes": 20000},
            {"name": "System.IO.Hashing.x8z6mup4sz.wasm", "group": "assembly", "rawBytes": 7957, "brotliBytes": 3460, "gzipBytes": 4100},
            {"name": "MmoEngine.SimCore.a0eeztrxtm.wasm", "group": "assembly", "rawBytes": 20000, "brotliBytes": 8000, "gzipBytes": 9500},
            {"name": "MmoEngine.SimCore.Wasm.mjw9736tm5.wasm", "group": "assembly", "rawBytes": 5000, "brotliBytes": 2200, "gzipBytes": 2600}
          ],
          "total": {"files": 9, "rawBytes": 4467850, "brotliBytes": 1417660, "gzipBytes": 1718200}
        },
        "startup": {
          "coldStarts": 10,
          "node": {
            "importMs": {"median": 38.2, "p90": 44.1},
            "createMs": {"median": 52.6, "p90": 61.0},
            "firstStepMs": {"median": 24.3, "p90": 31.7},
            "totalMs": {"median": 116.4, "p90": 123.9}
          },
          "chromiumWorker": {
            "crossOriginIsolated": true,
            "importMs": {"median": 45.0, "p90": 52.3},
            "createMs": {"median": 58.1, "p90": 66.4},
            "firstStepMs": {"median": 28.7, "p90": 35.2},
            "totalMs": {"median": 133.0, "p90": 148.6}
          }
        },
        "steps": {
          "node": [
            {"kind": "movement", "scenario": "flat-run-30hz", "batch": 1, "steps": 3008, "meanNs": 95120.0, "p99Ns": 141300.0},
            {"kind": "weapon", "scenario": "rifle-burst", "batch": 1, "steps": 3008, "meanNs": 61040.0, "p99Ns": 88220.0},
            {"kind": "projectile", "scenario": "launcher-arc", "batch": 1, "steps": 3008, "meanNs": 74310.0, "p99Ns": 119800.0}
          ],
          "chromiumWorker": [
            {"kind": "movement", "scenario": "flat-run-30hz", "batch": 16, "steps": 3008, "meanNs": 99480.0, "p99Ns": 128700.0},
            {"kind": "weapon", "scenario": "rifle-burst", "batch": 16, "steps": 3008, "meanNs": 63950.0, "p99Ns": 81100.0},
            {"kind": "projectile", "scenario": "launcher-arc", "batch": 16, "steps": 3008, "meanNs": 77020.0, "p99Ns": 104500.0}
          ]
        },
        "agreement": null
      },
      "interp-relinked": {
        "produced": true,
        "reason": null,
        "build": {"aot": false, "relinked": true, "workload": true},
        "runtime": {
          "productVersion": "10.0.15",
          "gitHash": "4c8d2e6f1a3b5c7d9e0f2a4b6c8d0e1f3a5b7c9d",
          "buildConfiguration": "Release",
          "wasmEnableSIMD": true,
          "wasmEnableExceptionHandling": true,
          "wasmEnableThreads": false,
          "debugLevel": 0,
          "globalizationMode": "invariant",
          "runtimeOptions": [],
          "jiterpreter": true
        },
        "download": {
          "files": [
            {"name": "dotnet.js", "group": "loader", "rawBytes": 37000, "brotliBytes": 12000, "gzipBytes": 14000},
            {"name": "dotnet.native.kakcbpgnoi.js", "group": "jsModuleNative", "rawBytes": 140000, "brotliBytes": 32000, "gzipBytes": 38000},
            {"name": "dotnet.runtime.v2rwe8u38b.js", "group": "jsModuleRuntime", "rawBytes": 205000, "brotliBytes": 52000, "gzipBytes": 60000},
            {"name": "dotnet.native.q3m81rzt0v.wasm", "group": "wasmNative", "rawBytes": 1480000, "brotliBytes": 465000, "gzipBytes": 560000},
            {"name": "System.Private.CoreLib.2kv5b1q4gv.wasm", "group": "coreAssembly", "rawBytes": 1007893, "brotliBytes": 315000, "gzipBytes": 380000},
            {"name": "System.Runtime.InteropServices.JavaScript.r8sucgoam9.wasm", "group": "coreAssembly", "rawBytes": 45000, "brotliBytes": 17000, "gzipBytes": 20000},
            {"name": "System.IO.Hashing.x8z6mup4sz.wasm", "group": "assembly", "rawBytes": 7957, "brotliBytes": 3460, "gzipBytes": 4100},
            {"name": "MmoEngine.SimCore.a0eeztrxtm.wasm", "group": "assembly", "rawBytes": 20000, "brotliBytes": 8000, "gzipBytes": 9500},
            {"name": "MmoEngine.SimCore.Wasm.mjw9736tm5.wasm", "group": "assembly", "rawBytes": 5000, "brotliBytes": 2200, "gzipBytes": 2600}
          ],
          "total": {"files": 9, "rawBytes": 2947850, "brotliBytes": 906660, "gzipBytes": 1088200}
        },
        "startup": {
          "coldStarts": 10,
          "node": {
            "importMs": {"median": 31.5, "p90": 36.8},
            "createMs": {"median": 40.2, "p90": 49.7},
            "firstStepMs": {"median": 21.9, "p90": 30.4},
            "totalMs": {"median": 93.6, "p90": 112.0}
          },
          "chromiumWorker": {
            "crossOriginIsolated": true,
            "importMs": {"median": 40.8, "p90": 47.5},
            "createMs": {"median": 51.3, "p90": 60.2},
            "firstStepMs": {"median": 27.1, "p90": 34.0},
            "totalMs": {"median": 126.4, "p90": 140.3}
          }
        },
        "steps": {
          "node": [
            {"kind": "movement", "scenario": "flat-run-30hz", "batch": 1, "steps": 3008, "meanNs": 93870.0, "p99Ns": 138900.0},
            {"kind": "weapon", "scenario": "rifle-burst", "batch": 1, "steps": 3008, "meanNs": 60210.0, "p99Ns": 86950.0},
            {"kind": "projectile", "scenario": "launcher-arc", "batch": 1, "steps": 3008, "meanNs": 73640.0, "p99Ns": 117200.0}
          ],
          "chromiumWorker": [
            {"kind": "movement", "scenario": "flat-run-30hz", "batch": 16, "steps": 3008, "meanNs": 98110.0, "p99Ns": 126300.0},
            {"kind": "weapon", "scenario": "rifle-burst", "batch": 16, "steps": 3008, "meanNs": 63020.0, "p99Ns": 80400.0},
            {"kind": "projectile", "scenario": "launcher-arc", "batch": 16, "steps": 3008, "meanNs": 76350.0, "p99Ns": 103900.0}
          ]
        },
        "agreement": {"build": "node-interp-relinked", "status": "Match"}
      },
      "aot": {
        "produced": false,
        "reason": "dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log"
      }
    }
  }
}
```

## Fields

Every number's unit is in its name: `Ns` nanoseconds, `Ms` milliseconds, `Bytes` bytes. Fields
without a unit suffix are counts or labels. The schema is closed: a field not listed here is a
problem.

**Top level**

| Field | Type | Rule |
| --- | --- | --- |
| `formatVersion` | integer | 1 |
| `kind` | string | `simcore-measurement` |
| `label` | string | `measurement` |
| `recordedAt` | string | UTC, `YYYY-MM-DDTHH:MM:SSZ` |
| `commit` | string | 40 lowercase hex digits (`GITHUB_SHA`), or `unknown` |
| `runnerClass` | string | `MMO_RUNNER_CLASS`; not empty |
| `machine` | object | `cpuModel` (not empty; Node's `os.cpus()[0].model`), `logicalCores` (≥ 1), `architecture` (`x64` or `arm64`), `os` (not empty) |
| `dotnet` | object | `sdkVersion` and `runtimeVersion` (`<major>.<minor>.<patch>`, from the benchmark run), `environment` (`DOTNET_EnableAVX512F` and `DOTNET_TieredPGO`, each the variable's value or `unset`) |
| `node` | object | `version`, as `process.version` |
| `chromium` | object | `version`, as Playwright's `browser.version()` |
| `server` | object | below |
| `wasm` | object | `modes`, below |

**`server`** (written by the benchmark exporter, copied as is)

| Field | Type | Rule |
| --- | --- | --- |
| `tool` | string | `BenchmarkDotNet <version>` |
| `label` | string | `measurement` |
| `runnerClass` | string | equals the top-level `runnerClass` |
| `benchmarks[]` | array | at least one entry each for `movement`, `weapon` and `projectile` (US7 scenario 1) |
| `benchmarks[].step` | string | `movement`, `weapon`, `projectile`, `hitboxBuild` or `stateHash` |
| `benchmarks[].case` | string | the benchmark's case, unique within its step (`flat-run`, `wall-slide`, `corner-wedge`, `rifle-hold`, `launcher-flight`, …) |
| `benchmarks[].meanNs` | number | > 0; BenchmarkDotNet's mean per operation |
| `benchmarks[].p99Ns` | number | > 0; BenchmarkDotNet's 99th percentile of the measured iterations, each iteration's mean per operation |
| `benchmarks[].iterations` | integer | ≥ 1; the number of measured iterations the statistics cover |
| `benchmarks[].allocatedBytesPerOp` | integer | ≥ 0; from `MemoryDiagnoser`. The zero-allocation tests, not this field, enforce FR-032 |

**`wasm.modes`** has exactly three keys: `interp-prebuilt`, `interp-relinked` and `aot`.

| Mode | Built with |
| --- | --- |
| `interp-prebuilt` | the interpreter with the prebuilt runtime, no workload (what the `agreement` job tests) |
| `interp-relinked` | the interpreter with the runtime relinked by the `wasm-tools` workload |
| `aot` | `-p:RunAOTCompilation=true`, with the workload |

A mode that was not produced is exactly `{"produced": false, "reason": "<why>"}`, with a non-empty
reason. A produced mode has every field below and `reason` null.

| Field | Type | Rule |
| --- | --- | --- |
| `produced` | boolean | |
| `reason` | string or null | the reason when not produced, else null |
| `build` | object | `aot`, `relinked`, `workload` (booleans that match the mode) |
| `runtime` | object | `runtimeBuildInfo` (`productVersion`, `gitHash`, `buildConfiguration`, `wasmEnableSIMD`, `wasmEnableExceptionHandling`, `wasmEnableThreads`), the boot manifest's `debugLevel` (must be 0, the publish output) and `globalizationMode`, the `runtimeOptions` used (an array of strings), and `jiterpreter` (true when the jiterpreter options were left at their defaults) |
| `download.files[]` | array | one entry per file the page downloads: `name`, `group`, `rawBytes`, `brotliBytes`, `gzipBytes` (each > 0) |
| `download.files[].group` | string | `loader` (exactly one, `dotnet.js`), `jsModuleNative`, `jsModuleRuntime`, `wasmNative` (exactly one), `coreAssembly`, `assembly`, or `icu` |
| `download.total` | object | `files` (the entry count), and `rawBytes`, `brotliBytes`, `gzipBytes`, each the exact sum over the files |
| `startup.coldStarts` | integer | 10 |
| `startup.node`, `startup.chromiumWorker` | object | `importMs`, `createMs`, `firstStepMs`, `totalMs`, each `{"median", "p90"}`, positive and with `p90` ≥ `median`; `chromiumWorker` also has `crossOriginIsolated`, which must be true |
| `steps.node[]`, `steps.chromiumWorker[]` | array | exactly one entry per kind: `movement`, `weapon`, `projectile` |
| `steps.*[].kind`, `scenario` | string | the kind, and the named stream that drives it: `flat-run-30hz`, `rifle-burst`, `launcher-arc` |
| `steps.*[].batch` | integer | 1 for `node`, 16 for `chromiumWorker` |
| `steps.*[].steps` | integer | ≥ 3,000 timed steps |
| `steps.*[].meanNs`, `p99Ns` | number | > 0 |
| `agreement` | object or null | the Node agreement for this mode in this job: `build` and `status` (`Match`, `Diverged` or `NotRun`), copied from `<work>/report/agreement.json`. Required for `interp-relinked` and `aot` when produced; null for `interp-prebuilt`, whose agreement is the `agreement` job's |

## How each number is measured

**Download size.** `measure.mjs` parses the boot manifest that .NET 10 embeds in
`_framework/dotnet.js` between `/*json-start*/` and `/*json-end*/` [measured]; there is no separate
boot JSON file. The downloaded files are `dotnet.js` plus every `name` in the manifest's
`jsModuleNative`, `jsModuleRuntime`, `wasmNative`, `coreAssembly`, `assembly` and `icu` groups.
`rawBytes` is the file's size in the publish output. `brotliBytes` and `gzipBytes` are the sizes of
its `.br` and `.gz` siblings, which `dotnet publish` writes beside every file [measured], so they are
what a static host serves. A missing sibling fails the measurement.

**Startup.** Ten cold starts per mode and runtime. The inputs (the collision file, config block, initial state
and first command of `flat-run-30hz`, from the work directory's `batch/` inputs) are read before the
clock starts.

| Interval | From | To |
| --- | --- | --- |
| `importMs` | just before `import()` of `dotnet.js` | the import resolves |
| `createMs` | the import resolves | `dotnet.withRuntimeOptions([]).create()` and `getAssemblyExports` return |
| `firstStepMs` | the exports are ready | `LoadCollision`, `LoadConfig`, `Reset` and `StepQueued(1)` have returned, the last with 1 |
| `totalMs` | just before `import()` | the first step has completed: the time to the first completed step (US7 scenario 2) |

- **Node:** each cold start is a fresh `node` process. Times come from `performance.now()`.
- **Chromium Worker:** each cold start is a fresh browser context, page and module Worker, served by
  `lib/server.mjs` with COOP, COEP and `no-store`, so nothing is cached and timers resolve to 5 µs
  [measured]. The Worker takes the times itself, from its module's first line. Startup is measured
  over loopback on uncompressed files, so it measures the runtime's boot, not the network.

**Step cost.** One entry per step kind. Each kind is driven by a named stream's batch inputs through
`StepQueued`, so it is a whole world step (inputs, movement, weapons, hitboxes, projectiles, hash)
whose work is dominated by that kind:

| Kind | Stream |
| --- | --- |
| `movement` | `flat-run-30hz` |
| `weapon` | `rifle-burst` |
| `projectile` | `launcher-arc` |

The stream's commands are played in passes, with a `Reset` (not timed) before each pass. The first
512 steps are warm-up and not timed. Then 3,008 steps are timed.

- **Node:** one command per `StepQueued(1)` call, each timed with `process.hrtime.bigint()`.
  `meanNs` is the total time divided by the number of timed steps; `p99Ns` is the 99th percentile
  of the per-step times.
- **Chromium Worker:** `StepQueued(16)` per batch, timed with `performance.now()`. The browser's
  timer resolves to 5 µs, which is close to an AOT step's cost [measured], so single steps are not
  timed. A batch's per-step time is its time divided by 16. `meanNs` is the total time divided by
  the number of timed steps; `p99Ns` is the 99th percentile of the batches' per-step times. Only
  full batches inside a pass are timed; a pass's last partial batch runs untimed.

**Percentiles** use the nearest-rank method: the *p*-th percentile of *n* samples is the
⌈*p* / 100 · *n*⌉-th smallest. The median is the 50th percentile. With 10 cold starts, the median is
the 5th smallest and the p90 the 9th.

## What `validate-report` checks

Every check prints one `path: problem` line when it fails, for example
`wasm.modes.aot.reason: is required when produced is false`. The command exits 0 when there is no
problem, 1 otherwise.

1. The file is valid JSON and an object; `formatVersion` is 1, `kind` is `simcore-measurement` and
   `label` is `measurement`.
2. Every field listed above is present with its type, and no other field is.
3. `runnerClass` is not empty, and `server.runnerClass` equals it (US7 Independent Test: "the runner
   class").
4. `recordedAt`, `commit`, `machine`, `dotnet` and the versions match their formats.
5. `server.label` is `measurement`, and `server.benchmarks` has at least one entry for each of
   `movement`, `weapon` and `projectile`, each with `meanNs` > 0, `p99Ns` > 0 and `iterations` ≥ 1
   (US7 scenario 1, FR-030).
6. `wasm.modes` has exactly `interp-prebuilt`, `interp-relinked` and `aot` (US7 Independent Test:
   "the build mode").
7. An unproduced mode has a non-empty `reason` and nothing else (FR-031: "name any mode it could not
   build").
8. At least one interpreted mode was produced, because FR-031 requires the interpreted build to be
   measured.
9. For each produced mode:
   - `build` matches the mode;
   - `runtime.debugLevel` is 0;
   - `download.files` has exactly one `loader` named `dotnet.js` and exactly one `wasmNative`, unique
     names, sizes > 0, and `download.total` equals the count and the exact sums (US7 scenario 2:
     "raw and compressed");
   - `startup.coldStarts` is 10; every interval has `median` > 0 and `p90` ≥ `median`;
     `chromiumWorker.crossOriginIsolated` is true;
   - `steps.node` and `steps.chromiumWorker` each have exactly the three kinds with their streams,
     `batch` 1 and 16, `steps` ≥ 3,000, and positive `meanNs` and `p99Ns`;
   - `agreement` is null for `interp-prebuilt`, and for the other two modes names
     `node-interp-relinked` or `node-aot` with a status of `Match`, `Diverged` or `NotRun`.

The checks are structural. They do not judge whether a number is good: there is no threshold, because
no tuning value is set (Principle VI). The zero-allocation tests in
`tests/MmoEngine.SimCore.Tests/Allocation/` cover US7 scenario 3 and FR-032 on the server build;
`GC.GetAllocatedBytesForCurrentThread` was not verified to work under Mono WebAssembly.

## Tests

| Test | Asserts |
| --- | --- |
| `tests/MmoEngine.SimCore.Tests/Cli/ValidateReportTests.TheContractExampleIsValid` | the example above, kept as the fixture `tests/MmoEngine.SimCore.Tests/Cli/Fixtures/measurement-report.json`, validates with exit 0 and no output |
| `ValidateReportTests.EachProblemIsReported(mutation, expectedLine)` (Theory) | one row per check: a mutation of the fixture (a field removed, an unknown field, an empty runner class, a server runner class that differs, a missing `weapon` benchmark, a missing mode, an unproduced mode without a reason, a total that does not add up, 9 cold starts, `crossOriginIsolated` false, a missing step kind, a `p90` below the `median`) gives exactly its `path: problem` line and exit 1 |
| `ValidateReportTests.AnUnproducedModeNeedsOnlyItsReason` | `{"produced": false, "reason": "…"}` for `aot` and `interp-relinked` is valid |
| `ValidateReportTests.MalformedJsonIsInvalidAndAMissingFileCannotRun` | exit 1 with `$: is not valid JSON (…)`; exit 2 for a missing file |
| `tools/agreement/node/measure.test.mjs` (`node --test`) | the boot-manifest parser on a fixture `dotnet.js`, the nearest-rank percentile, and the totals |
| The `measure` job | runs `validate-report` on the real report on the runner class (US7 Independent Test, SC-007) |
