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
   object, and adds a P99 column to the console table. `SimCoreBenchmarkConfig.Create(runnerClass,
   job)` builds the configuration (the exporter, the P99 column and `MemoryDiagnoser`); the project's
   `Main` passes `MMO_RUNNER_CLASS` and the default job, and exits 2 when the variable is unset. The
   dry-run test below calls the same method. The step exits 0 only when this run wrote the export with
   every benchmark it ran, because step 6 reads the file. Otherwise it exits 1 and writes one line to
   stderr naming the reason: no benchmark ran (a filter that selects nothing, `--list` or `--help`), a
   critical validation error stopped the run, a report has no statistics (a failed benchmark) or no
   allocation measurement (the export leaves both out), or the export was not written. BenchmarkDotNet
   catches an exporter's exception and only logs it, so the exporter records the path it wrote and the
   entry point checks it; an export left over from an earlier run never passes the step.
2. **The prebuilt interpreted build**, published before the workload is installed:
   `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -p:UsingBrowserRuntimeWorkload=false -o <dir>`,
   the `agreement` job's publish command. Without the workload, publish uses the prebuilt runtime
   [measured]; the flag keeps it there even on a runner image that ships the workload, so this mode is
   the build the `agreement` job tested as `node-interp`.
3. **The workload.** `dotnet workload install wasm-tools`, continuing on error. With the workload,
   `InvariantGlobalization=true` forces a native relink on every publish [measured], so the prebuilt
   build has to come first.
4. **The relinked interpreted build**, `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o <dir>`,
   and **the AOT build**, `dotnet publish src/MmoEngine.SimCore.Wasm -c Release
   -p:RunAOTCompilation=true -o <dir>`, each only when the workload installed. Neither carries step 2's
   `-p:UsingBrowserRuntimeWorkload=false`, which would keep it on the prebuilt runtime pack: both are
   meant to use the workload. A mode that cannot be produced is recorded as
   `{"produced": false, "reason": "…"}`, and so is one whose publish does not
   finish within its bound (`timeout` gives the relinked publish 4 minutes and the AOT publish 5), so
   a hung publish is a report entry, not a cancelled job. The AOT build is published last:
   it leaves the assemblies it compiled, stripped of their IL, in the project's `obj` folder, and an
   interpreted publish after it from the same checkout copies them and stops at boot with
   `MONO interpreter: NIY encountered in method …` [measured].
5. **Agreement on the new modes.** `simcore agreement prepare`, the Node agreement run with
   `AGREEMENT_BUILD=node-interp-relinked` and `node-aot` for the modes produced (each with
   `AGREEMENT_APP` set to that build's `wwwroot` and `AGREEMENT_WORK` to the prepared work directory), then
   `simcore agreement compare --require` with those builds. The agreement of each build produced
   here, relinked interpreted and AOT, gates the job: a divergence in either is a real divergence
   between builds ([research.md](../research.md) R17). The AOT run and compare also run after the
   relinked run failed (not after a cancel), as the `agreement` job's compare does, so the job
   summary and the uploaded `report/` name what disagreed. That upload (`simcore-measure-agreement-report`)
   runs after a failed step only when the work directory was prepared: before that, `WORK` is unset
   and there is no `report/`. The prebuilt build's agreement is the `agreement` job's. The measured
   numbers never gate.
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
   `--unproduced <mode>="<reason>"`. Before it measures anything it reads the `--server-benchmarks`
   export and runs `dotnet --version` and `dotnet --list-runtimes`, so a missing or malformed export,
   or a toolchain without a .NET 10 runtime, fails at once. It exits 0 when the report is written; 1
   when an input or a measurement failed (that export or `dotnet`, a boot error, the content-type
   warning, a missing compressed file, a `measure-node.mjs` process or a Chromium page that failed or
   did not finish within its 2 minutes, a Node cold start whose runtime reported no jiterpreter state
   or another one than the first cold start's, a page whose `booted` message does not say
   `inWorker: true`, a runtime that measured another number of cold starts than 10, or a page's browser
   context or the browser that did not close within 10 seconds), with one `measure: <message>` line,
   which for a process or a page names the mode and the run, such as `measure: aot: chromium cold
   start 3: the page did not finish within 120 s`; 2 on a usage error, including an unset
   `MMO_RUNNER_CLASS`, because a number without its runner class is not usable. Every call of a page
   (`newContext`, `newPage`, the navigation, the wait and `evaluate`) shares its 2 minutes, so a wedged
   browser fails the page with that line rather than holding the job until its step's bound.
7. **Validation**, then the report in the job log and summary, then upload:
   `simcore measure validate-report "$RUNNER_TEMP/simcore-measurement.json"`. Once it exits 0, the
   "Show the measurement report" step prints the report to the job log and appends it to the job
   summary, under a `### simcore-measurement` heading in a fenced `json` block, so its numbers can be
   read without downloading the artifact. Then the report is uploaded as `simcore-measurement`.

The job also runs the exhaustive trigonometric sweeps, with `MMO_EXHAUSTIVE_MATH=1`
([research.md](../research.md) R3). They write each function's maximum error in ulp to the job
summary and are not part of this report. The fixed trig table in the pull-request tests is the gate
for the error bounds (SC-004), and the agreement runs' cross-build tables check is the gate for its
bit agreement across builds ([wasm-api.md](wasm-api.md#cross-build-tables)), in the `agreement` job
and, for the relinked and AOT builds, in this job's agreement step.

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
problem. An integer field is a JSON number written as an optional minus sign and decimal digits, with
no fraction or exponent, in the signed 64-bit range; any other number, such as `4.0`, `1e2` or
`9223372036854775808`, is `must be an integer`. Every count, iteration count and byte size fits that
range, and the download totals are still summed exactly, beyond it.

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
| `dotnet` | object | `sdkVersion` and `runtimeVersion` (`<major>.<minor>.<patch>`, from the benchmark run; `runtimeVersion` is the highest `Microsoft.NETCore.App 10.` runtime that `dotnet --list-runtimes` lists, by SemVer 2.0 precedence, so a release is above its prereleases and `rc.10` above `rc.2`; a prerelease still fails this format), `environment` (`DOTNET_EnableAVX512F` and `DOTNET_TieredPGO`, each the variable's value or `unset`) |
| `node` | object | `version` (not empty), as `process.version` |
| `chromium` | object | `version` (not empty), as Playwright's `browser.version()` |
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
| `build` | object | `aot`, `relinked`, `workload` (booleans that match the mode: all false for `interp-prebuilt`; `aot` false and the other two true for `interp-relinked`; all true for `aot`, which relinks the runtime with the workload) |
| `runtime` | object | `runtimeBuildInfo` (`productVersion`, `gitHash` and `buildConfiguration`, strings that are not empty, and the booleans `wasmEnableSIMD`, `wasmEnableExceptionHandling`, `wasmEnableThreads`), the boot manifest's `debugLevel` (must be 0, the publish output) and `globalizationMode` (not empty), the `runtimeOptions` used (an array of strings; `[]`, since both halves boot with `bootRuntime`'s defaults), and `jiterpreter` (true when the jiterpreter options were left at their defaults: every Node cold start's runtime reported its traces, interpreter entries and JIT calls all on, as `jiterpreterState` reads them; false when it reported any of them off. A cold start whose runtime reports no state, or another state than the first cold start's, fails the measurement) |
| `download.files[]` | array | one entry per file the page downloads: `name` (not empty, unique), `group`, `rawBytes`, `brotliBytes`, `gzipBytes` (each > 0) |
| `download.files[].group` | string | `loader` (exactly one, `dotnet.js`), `jsModuleNative`, `jsModuleRuntime`, `wasmNative` (exactly one), `coreAssembly`, `assembly`, or `icu` |
| `download.total` | object | `files` (the entry count), and `rawBytes`, `brotliBytes`, `gzipBytes`, each the exact sum over the files |
| `startup.coldStarts` | integer | 10: the number of cold starts the summaries cover, counted from the samples each runtime measured; `measure.mjs` fails the measurement unless Node and the Chromium Worker each measured 10 |
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

- **Node:** each cold start is a fresh `node` process. Times come from `performance.now()`. Each
  also reports the jiterpreter state its runtime booted with, from which `runtime.jiterpreter` comes.
- **Chromium Worker:** each cold start is a fresh browser context, page and module Worker, served by
  `lib/server.mjs` with COOP, COEP and `no-store`, so nothing is cached and timers resolve to 5 µs
  [measured]. The Worker takes the times itself, from its module's first line, and posts them to the
  page; the messages are in [wasm-api.md](wasm-api.md#measurement-worker). A page whose `booted`
  message does not say `inWorker: true` fails the measurement, so code that did not run in a
  `WorkerGlobalScope` is never recorded as the Worker's. Startup is measured over
  loopback on uncompressed files, so it measures the runtime's boot, not the network.
- Each interval's `median` and `p90` are over the samples the two runtimes measured, and
  `startup.coldStarts` is their number: `measure.mjs` fails the measurement unless each runtime
  measured exactly 10.

**Step cost.** One entry per step kind. Each kind is driven by a named stream's batch inputs through
`StepQueued`, so it is a whole world step (inputs, movement, weapons, hitboxes, projectiles, hash)
whose work is dominated by that kind:

| Kind | Stream |
| --- | --- |
| `movement` | `flat-run-30hz` |
| `weapon` | `rifle-burst` |
| `projectile` | `launcher-arc` |

The stream's commands are played in passes, with a `Reset` (not timed) before each pass. Each pass
plays the stream's first ⌊*n* / 16⌋ · 16 of its *n* commands, its whole 16-command batches, in both
runtimes: 16 of `flat-run-30hz`'s 30, 112 of `rifle-burst`'s 120 and 32 of `launcher-arc`'s 45. The
first 512 steps are warm-up and not timed. Then 3,008 steps are timed. 512 and 3,008 are multiples of
16, so Node and the Worker time the same steps of the same passes, and their two numbers for a kind
are averages over the same work. Both run the loop of `lib/passes.mjs` (`stepKindCosts`).

- **Node:** one command per `StepQueued(1)` call, each timed with `process.hrtime.bigint()`.
  `meanNs` is the total time divided by the number of timed steps; `p99Ns` is the 99th percentile
  of the per-step times.
- **Chromium Worker:** `StepQueued(16)` per batch, timed with `performance.now()`. The browser's
  timer resolves to 5 µs, which is close to an AOT step's cost [measured], so single steps are not
  timed. A batch's per-step time is its time divided by 16. `meanNs` is the total time divided by
  the number of timed steps; `p99Ns` is the 99th percentile of the batches' per-step times.

**Percentiles** use the nearest-rank method: the *p*-th percentile of *n* samples is the
⌈*p* / 100 · *n*⌉-th smallest. The median is the 50th percentile. With 10 cold starts, the median is
the 5th smallest and the p90 the 9th.

## What `validate-report` checks

Every check prints one `path: problem` line when it fails, for example
`wasm.modes.aot.reason: is required when produced is false`. A value in a message is its raw JSON
text. A member name in a path, and the step named in `is not unique within <step>`, is written
JSON-escaped, as inside a JSON string without its quotes, so a control character, a quote or a
backslash in it appears as its escape and every problem stays on one line. The command exits 0 when
there is no problem, 1 otherwise.

1. The file is UTF-8 (a leading byte-order mark is skipped) and valid JSON, with no member name
   repeated within an object, and an object; `formatVersion` is 1, `kind` is `simcore-measurement` and
   `label` is `measurement`. Bytes that are not UTF-8, including a UTF-16 file, are
   `$: is not valid JSON (…)` like malformed JSON: they are never replaced by U+FFFD and passed. So
   is a member name or string whose escapes leave an unpaired surrogate, such as `"\uD800"`, which
   is not Unicode text (RFC 8259, section 8.2).
2. Every field listed above is present with its type, and no other field is.
3. `runnerClass` is not empty, and `server.runnerClass` equals it (US7 Independent Test: "the runner
   class").
4. `recordedAt`, `commit`, `machine`, `dotnet` and the versions match their formats. Each format
   matches the whole string, so a value with anything after it, such as the trailing newline of a
   command's output that was not trimmed, is a problem.
5. `server.label` is `measurement`, and `server.benchmarks` has at least one entry for each of
   `movement`, `weapon` and `projectile`, each with `meanNs` > 0, `p99Ns` > 0 and `iterations` ≥ 1
   (US7 scenario 1, FR-030).
6. `wasm.modes` has exactly `interp-prebuilt`, `interp-relinked` and `aot` (US7 Independent Test:
   "the build mode").
7. An unproduced mode has a non-empty `reason` and nothing else (FR-031: "name any mode it could not
   build"). A mode whose `produced` is missing or not a boolean reports only that and its unknown
   fields, because which fields it needs depends on `produced`.
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
| `ValidateReportTests.EachProblemIsReported(mutation, expectedLine)` (Theory) | one row per check: a mutation of the fixture (a field removed, an unknown field, an empty runner class, a server runner class that differs, a missing `weapon` benchmark, a missing mode, an unproduced mode without a reason, a raw, brotli or gzip total that does not add up, 9 cold starts, a missing `firstStepMs`, `crossOriginIsolated` false, a missing step kind, a WebAssembly step whose `meanNs` or `p99Ns` is 0, a `p90` below the `median`) gives exactly its `path: problem` line and exit 1. The rows cover every number US7 scenario 2 says the report records: the raw and compressed download sizes, the time to the first completed step and the cost per step |
| `ValidateReportTests.AnUnproducedModeNeedsOnlyItsReason` | `{"produced": false, "reason": "…"}` for `aot` and `interp-relinked` is valid |
| `ValidateReportTests.MalformedJsonIsInvalidAndAMissingFileCannotRun` | exit 1 with `$: is not valid JSON (…)`; exit 2 for a missing file |
| `ValidateReportTests.AReportLongerThanTheLimitIsRefused` (Theory), `AStreamWithoutALengthIsReadOnlyUntilItPassesTheLimit` | the example padded with spaces to exactly 1 MiB is valid, and one byte more is exit 2 with nothing on stdout and the path on stderr ([simcore-cli.md](simcore-cli.md#measure-validate-report)); a stream without a length is refused after exactly limit + 1 bytes, and one of exactly the limit is read whole |
| `ValidateReportTests.BytesThatAreNotUtf8AreNotValidJson` (Theory), `AUtf8ByteOrderMarkIsSkipped` | the fixture with one 0xFF byte inside `machine.cpuModel`, the same after a UTF-8 byte-order mark, and the fixture as UTF-16 are each exit 1 with exactly one `$: is not valid JSON (…)` line; the fixture after a UTF-8 byte-order mark is valid (check 1) |
| `tests/MmoEngine.SimCore.Tests/Cli/MeasurementReportValidatorTests.EveryOtherCheckReportsItsLine(mutation, expectedLine)` (Theory) | every other check, called directly on one mutation of the fixture each, returns exactly its line; among the rows, `recordedAt`, `commit`, both .NET versions and `server.tool` with one trailing newline each fail their format, a `server` benchmark's `meanNs` or `p99Ns` of 0 and a missing `movement` or `projectile` benchmark each fail check 5 (a missing `weapon` one is `EachProblemIsReported`'s row), each `build` boolean of the two interpreted modes that does not match its mode fails check 9, two file sizes of 9223372036854775807 give a total line with their exact sum, and `4.0`, `1e2` and `9223372036854775808` are not integers |
| `MeasurementReportValidatorTests.AProducedAotModeWithEveryBuildFlagAndNodeAotIsValid`, `AProducedAotModeReportsItsLine` (Theory) | the fixture with its `interp-relinked` mode copied to `aot`, `build` all true and `agreement.build` `node-aot`, is valid; each `build` boolean set to false, `agreement.build` `node-interp-relinked` and a null `agreement` each give exactly their line (check 9) |
| `MeasurementReportValidatorTests.RepeatedMemberMakesTheFileInvalidJson` (Theory) | the fixture with `formatVersion`, or `machine.cpuModel`, written twice with the same value is exactly one `$: is not valid JSON (…)` line naming the member (check 1) |
| `ValidateReportTests.AnUnpairedSurrogateEscapeIsNotValidJson` (Theory), `MeasurementReportValidatorTests.TextThatIsNotUnicodeIsNotValidJson` (Theory), `AServerObjectWithAnUnpairedSurrogateIsNotValidJson` | the escape `\uD800` in `machine.cpuModel` or before the name `cpuModel` is exit 1 with exactly one `$: is not valid JSON (…)` line; called directly, a lone surrogate in the text and the escape before a name two objects down each give that one line, and a `server` object whose `tool` is the escape gives one `server: is not valid JSON (…)` line, without throwing (check 1) |
| `ValidateReportTests.TextFromTheFileIsShownEscapedSoEachProblemIsOneLine` (Theory) | an unknown field whose name holds a line feed, or a quote and a backslash, an unknown mode whose name holds a line feed, and two `server` benchmarks with the same case and the step `"x\ny"` each print exactly their lines, with the text escaped, and as many line feeds as problems |
| `tests/MmoEngine.SimCore.Tests/Benchmarks/ServerBenchmarkExportTests.DryRunExportsAValidServerObject` | US7 scenario 1 in the pull-request run: references the benchmark project and runs the movement, weapon and projectile benchmarks in process with `BenchmarkRunner.Run`, using `SimCoreBenchmarkConfig.Create("test-runner", Job.Dry.WithIterationCount(3).WithToolchain(InProcessEmitToolchain.Instance))` with the optimizations validator off and the artifacts in a temporary directory; the exported JSON's `server` object passes check 5 and every `server` field rule above, its `label` is `measurement` and its `runnerClass` is `test-runner`. Each entry, matched to its report by step and case, has exactly that report's `Mean` as `meanNs`, its `Percentiles.Percentile(99)` as `p99Ns`, its `N` (3) as `iterations` and its `GetBytesAllocatedPerOperation` as `allocatedBytesPerOp`. The job measures three iterations because with `Job.Dry`'s one the 99th percentile equals the mean. It runs in the serial collection |
| `ServerBenchmarkExportTests.RunExitsZeroWhenItExportedEveryBenchmarkItRan`, `RunExitsOneWhenItWroteNoExport` (Theory), `RunExitsOneWhenAReportHasNoAllocationMeasurement` | step 1's exit code, through the entry point's `Program.Run(args, runnerClass, stderr, createConfig)` and `BenchmarkSwitcher` with the dry run's configuration: 0 when the movement, weapon and projectile benchmarks ran and were exported; 1 with exactly its stderr line when the filter selects nothing (an export left from an earlier run stays as it was), when a directory at the export's path makes the exporter throw, and when a report has no allocation measurement |
| `tools/agreement/node/measure.test.mjs` (`node --test`) | `'parses the boot manifest from dotnet.js'` on a fixture `dotnet.js`; `'nearest-rank percentile picks the ceil(p/100*n)-th smallest'`; `'download totals are the exact sums'` and `'a missing compressed sibling fails the measurement'` (the `.br` and `.gz` siblings); `'an unproduced mode is only its reason'`; `'arguments need each mode exactly once and the runner class'` (step 6's `--mode` and `--unproduced` rules and their usage messages); `'the entry point exits 2 on a usage error and 1 on a failed measurement, writing no report'`, with `node tools/agreement/lib/measure.mjs` run as a child process: 2 and `measure: MMO_RUNNER_CLASS is not set; …` without the runner class, also through a symlink to the script, 1 and `measure: cannot read _framework/dotnet.js of …` for a produced mode with no build, 1 and `measure: interp-prebuilt: node cold start 1 exited 1: measure-node: cannot read …` for a work directory never prepared, and no `--out` written in any; `'the entry point reads the server export and the dotnet versions before it measures'`: exit 1 with the exact `measure:` line for a missing or malformed `--server-benchmarks` export, a `dotnet` that is not on PATH or fails, and one without a .NET 10 runtime, each before the produced mode's empty folder is read; `'the Node half exits 1 with its usage or measurement error and prints nothing on stdout'`, with `node tools/agreement/lib/measure-node.mjs` run as a child process: exit 1 and the exact `measure-node: <message>` line for each usage error (also through a symlink) and for an unprepared work directory; `'a measurement page that fails is a MeasurementError naming its mode and run'`, with a fake browser: every way a page fails (no context or page, a failed, refused or timed-out navigation, a wait that times out or crashes, unreadable events, `fatal`, the content-type warning from the page or the Worker, a failed close) is exactly its `<mode>: chromium cold start <i>: <message>` line, and the navigation and the wait share the page's one bound; `'build flags match the mode'`; `'each half gives one step entry per kind, in the report order'`; `'assembled report has exactly the contract fields'`, with its `NotRun` rows; `'step passes warm up 512 steps, time 3008 and reset before each pass'`; and `'both halves time the same steps, in passes of whole 16-command batches'`: the pass lengths above, and Node's single steps and the Worker's batches timing the same 3,008 steps of each stream. Added in the PR's reviews: `'a mode is measured over exactly ten cold starts in each runtime'` (US7 scenario 2), which runs `measureMode` with fakes for the `measure-node.mjs` processes, the agreement server and Chromium and asserts ten `measure-node.mjs cold-start` processes and one `steps` process, ten fresh contexts on `measure.html?run=cold-start` and one on `?run=steps`, each closed, and each interval's `median` and `p90`, in the result and in the report, over exactly those ten samples, which no shorter run of consecutive cold starts would give; `'the startup summary needs ten cold starts in each runtime'`: a runtime with 9, 1 or 11 samples is exactly `<mode>: <n> Node and <m> Chromium Worker cold starts were measured, not 10 each`; `'the report's jiterpreter is what every Node cold start's runtime reported'` and the `runtime.jiterpreter` rows of `'assembled report has exactly the contract fields'` (true only with all three options on; a cold start with no state or another one fails before the steps process); `'a Node cold start reports the jiterpreter state its runtime booted with'`, with `measure-node.mjs cold-start` run as a child process on a `dotnet.js` stand-in that refuses any runtime option; `'a Chromium page whose booted message is not from a Worker fails the measurement'`; the `a wedged browser:` rows of `'a measurement page that fails …'` and `'a wedged browser fails the mode within its bounds instead of hanging it'` (a `newContext`, `newPage`, navigation, wait, `evaluate` or close that never returns fails with its line within the bound); and `'the .NET runtime recorded is the highest by SemVer precedence'` (`compareVersions` and `highestRuntime`, `rc.2` below `rc.10` among the rows; and its `dotnetVersions:` rows, which run the exported `dotnetVersions(run)` that the entry point calls, on a fake `dotnet` whose `--version` prints `10.0.115` and whose `--list-runtimes` lists `10.0.0-rc.10.25550.1` above `10.0.0-rc.2.25502.107`, then `10.0.0` between them: the result is exactly `{sdk: '10.0.115', runtime: '10.0.0-rc.10.25550.1'}`, then `{sdk: '10.0.115', runtime: '10.0.0'}`, and a listing of only `Microsoft.NETCore.App 10.0.99x` and 9.0 runtimes is exactly `dotnet --list-runtimes lists no Microsoft.NETCore.App 10. runtime`) |
| `tests/workflows/test_simcore_workflow.py` `MeasureJob` | `test_measure_job_measures_every_mode_and_validates_the_report` (the job's steps in this order, every mode passed to `measure.mjs`, the report validated and uploaded); `test_runner_class_and_pinned_intrinsics_are_set`; `test_unproduced_modes_are_recorded_with_their_reason` (each mode passed as produced or with its reason, a publish that `timeout` ended included); `test_server_benchmarks_run_with_the_runner_class`; `test_relinked_and_aot_agreement_gates_the_job` (step 5: the agreement runs and compare require the builds produced, fail the job when their tool fails, and still run after an agreement run failed, and the agreement report is uploaded after a failed step only once prepare succeeded); `test_prebuilt_build_is_published_before_the_workload`; `test_exhaustive_math_sweeps_run_here_only`; `test_job_times_out_after_thirty_minutes` and `test_every_step_after_the_toolchain_setup_has_its_own_bound` (each step's own bound inside the job's 30 minutes); `test_runner_tests_run_before_the_measurement`; `test_the_report_is_shown_in_the_job_log_and_summary` (step 7: right after validation, the report on stdout and in a fenced `json` block in the job summary) |
| The `measure` job | runs `validate-report` on the real report on the runner class (US7 Independent Test, SC-007) |
