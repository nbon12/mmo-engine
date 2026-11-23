# Contract: CI jobs

This feature adds six jobs to the `SimCore` workflow (`.github/workflows/simcore.yml`) and leaves 015's
`agreement` and `measure` jobs, their steps and their bounds exactly as they are (FR-029, FR-032, US2
scenario 6, SC-002). `auto-merge.yml` already waits for the `SimCore` workflow, so the new jobs gate a
pull request the way `agreement` does; it is not changed. `tests/workflows/test_simcore_workflow.py` pins
every rule below. Research R26 has the reasoning.

## Triggers

`on` keeps 015's `pull_request` and `push` (branch `main`) path lists, adds `tools/vehicles/**` to both,
and adds `workflow_dispatch` (FR-025, "on demand"). The new projects match 015's existing
`src/MmoEngine.SimCore*/**`, `tests/MmoEngine.SimCore*/**`, `benchmarks/MmoEngine.SimCore*/**` and
`simcore/**` filters.

GitHub's path filters are workflow-level, so `vehicle-changes` decides which vehicle jobs run.

| Output | True when the change touches | Or the event is |
| --- | --- | --- |
| `selection` | `src/MmoEngine.SimCore/Vehicles/**`, `src/MmoEngine.SimCore.Vehicles*/**`, `benchmarks/MmoEngine.SimCore.Vehicles*/**`, `tools/agreement/**`, `tools/vehicles/**`, `simcore/vehicles/engines/**`, `simcore/vehicles/streams/**`, `simcore/vehicles/config/**`, `simcore/vehicles/content/**`, `simcore/vehicles/selection.json`, `.github/workflows/simcore.yml` (the vehicle code, a candidate's build, the vehicle streams or the selection configuration, FR-025) | `workflow_dispatch` |
| `gate` | everything `selection` lists, plus `src/MmoEngine.SimCore/**` (the core), `simcore/vehicles/selection/**` (the record), `Directory.Build.props`, `Directory.Packages.props`, `global.json` (FR-029) | `workflow_dispatch`, or a `push` to `main` |

The record's own folder is not a selection path, so committing a report and record does not start
another 120-minute selection run; it does start the vehicle agreement gate.

## Jobs

| Job | Needs | Runs when | `timeout-minutes` |
| --- | --- | --- | --- |
| `agreement` (015) | | unchanged | 10, unchanged |
| `measure` (015) | | unchanged | 90, unchanged |
| `vehicle-changes` | | always | 2 |
| `vehicle-engines` | `vehicle-changes` | `selection` or `gate` | 45 |
| `vehicle-agreement-plan` | `vehicle-changes` | `gate` | 2 |
| `vehicle-agreement` | `vehicle-agreement-plan`, `vehicle-engines` | `gate` and the plan has legs | 15 per matrix leg |
| `vehicle-winner-checks` | `vehicle-agreement-plan`, `vehicle-engines` | `gate` and the record names a winner | 15 |
| `vehicle-selection` | `vehicle-changes`, `vehicle-engines` | `selection` | 120 |

Every action is pinned by commit SHA with a version comment, as in 015 (`actions/checkout`,
`actions/setup-dotnet`, `actions/setup-node`, `actions/upload-artifact`, plus `actions/cache` and
`actions/download-artifact`). Every step has its own `timeout-minutes`. The job environment keeps 015's
`MMO_RUNNER_CLASS: gha-ubuntu-latest-x64`, `DOTNET_EnableAVX512F: "0"` and `DOTNET_TieredPGO: "0"`; the
selection run sets `DOTNET_PreferredVectorBitWidth=128` on Bepu's child processes only (research R8).

### `vehicle-changes`

```yaml
vehicle-changes:
  name: vehicle-changes
  runs-on: ubuntu-latest
  timeout-minutes: 2
  outputs:
    selection: ${{ steps.paths.outputs.selection }}
    gate: ${{ steps.paths.outputs.gate }}
  steps:
    - uses: actions/checkout@<sha> # with fetch-depth 0, persist-credentials false
    - id: paths
      timeout-minutes: 1
      run: tools/vehicles/ci/changed-paths.sh "${{ github.event_name }}" "${{ github.event.pull_request.base.sha }}" >> "$GITHUB_OUTPUT"
```

`tools/vehicles/ci/changed-paths.sh` prints `selection=true|false` and `gate=true|false` from
`git diff --name-only <base>...HEAD` and the lists above; its table lives in the script and the workflow
test checks it against this contract.

### `vehicle-engines`

1. Checkout; set up .NET; restore `tools/vehicles/emsdk/emsdk.proj` (`PackageDownload` of the three
   Emscripten 3.1.56 packs at 10.0.15) and check each `.nupkg.sha512` against `simcore/vehicles/engines/jolt.json`.
2. Compute each native engine's identity (`simcore vehicles identity --engine jolt`) and the cache key
   `vehicle-engine-jolt-<fingerprint>-<sha256 of the shim's sources>`; restore `actions/cache`.
3. On a miss, `src/MmoEngine.SimCore.Vehicles.Jolt/native/build.sh` builds, each bounded by
   `timeout --kill-after=30s 15m` (FR-021), and writes `outcomes.json` with one outcome per build
   (`Built`, `BuildFailed` or `BuildTimedOut`, the failing step and the last 50 lines) instead of failing
   the job:
   - `server`: fetch Jolt by commit and check the tree hash, CMake with the pinned options, the `.so`
     with the version script; `nm -D --undefined-only` must show no pthread lock function and no
     `pthread_create`;
   - `browser-module`: the same sources with the pinned Emscripten, `-sMALLOC=emmalloc -Wl,--trace`;
   - `evidence-threaded`: the server build without BLUE and with `JobSystemThreadPool`, for FR-027 only.
4. Build `tools/vehicles/lockcount/` (`liblockcount.so` and `liblocktest.so`).
5. Test the shim: `ctest` with clang source-based coverage, `llvm-cov export -format=lcov`; then
   `python3 tools/coverage_gate.py --base <base> --coverage-dir coverage --lcov coverage/jolt.lcov` for the
   changed files under `src/MmoEngine.SimCore.Vehicles.Jolt/native/` (95%, research R28).
6. `dotnet format tests/MmoEngine.SimCore.Vehicles.Native.Tests --verify-no-changes`, then `dotnet test
   tests/MmoEngine.SimCore.Vehicles.Native.Tests` with `MMO_VEHICLE_NATIVE_DIR` (real Jolt through
   P/Invoke, the lock counter's self-test against `liblocktest.so`, the emmalloc trace assertion, the
   resting-offset calibration).
7. Upload the `vehicle-engines` artifact: `jolt/server/`, `jolt/browser/`, `jolt/evidence-threaded/`,
   `jolt/outcomes.json`, `lockcount/`.

A failed build is a recorded outcome, not a failed job; a failed shim test, coverage gate or native
.NET test fails the job.

### `vehicle-agreement`

`vehicle-agreement-plan` (2 minutes) runs `simcore vehicles decision check-stale`, which fails the job on a
stale record (FR-028) and prints the provisional flag when it is set, and emits the matrix from
`simcore vehicles decision gate-plan`:

| Gate plan | Legs |
| --- | --- |
| `NoRecord`, or `None` in every mode | no legs: the plan job writes the outcome to the job summary, and `vehicle-agreement` and `vehicle-winner-checks` are skipped |
| `BitExact` | one leg per mode the record names a winner for; `vehicle-winner-checks` runs |
| `NonExact` | the same legs, running the NonExact gate and printing the provisional flag in the log and summary |

Each mode leg (`fail-fast: false`, `timeout-minutes: 15`):

1. Checkout, set up .NET and Node, download the `vehicle-engines` artifact.
2. Publish the winner's host in the leg's mode: `interp-prebuilt` with `-p:UsingBrowserRuntimeWorkload=false`
   and **no workload installed**; `interp-relinked` and `aot` after `dotnet workload install wasm-tools`.
3. `simcore agreement prepare --engine <winner> --seeds 1-50 --work "$WORK"`.
4. The mode's required builds: Node runs with `AGREEMENT_BUILD` and the Chromium Worker with
   `AGREEMENT_MODE`.
5. BitExact: `simcore agreement compare --work "$WORK" --require <the mode's builds> --summary
   "$GITHUB_STEP_SUMMARY"`, which fails on any divergent step (FR-029).
   NonExact: `simcore streams verify --engine bepu` (a missed committed hash fails),
   `simcore vehicles divergence --work "$WORK" --require <builds>` (a bound exceeded fails) and
   `simcore agreement compare --work "$WORK" --require <builds> --compare v1-part` (any divergent step of
   the version 1 part hash fails), with the provisional flag echoed to the log and summary.

`vehicle-winner-checks` (15 minutes) runs `simcore vehicles checks --engine <winner>` and `simcore
vehicles lockcount --engine <winner>` (under `LD_PRELOAD` for a native winner) and fails on any failed
check: the winner's checks are a real test on `main` (US1 scenario 14).

### `vehicle-selection`

1. Checkout, set up .NET and Node, download the `vehicle-engines` artifact.
2. Publish, in this order, each bounded by `timeout 15m` and writing `<host>/<mode>/outcome.json` through
   `tools/vehicles/ci/publish-host.sh` (a failure or timeout is a recorded outcome, FR-021):
   1. the prebuilt build of 015's `MmoEngine.SimCore.Wasm` (the download baseline) and of each host
      (`Fixture`, `Bepu`, `Jolt`), with `-p:UsingBrowserRuntimeWorkload=false`;
   2. `dotnet workload install wasm-tools` (continue on error; a failure makes both later modes
      `ModeNotProduced` with this step as the reason);
   3. the relinked builds;
   4. the AOT builds, last, each host with its own `-p:BaseIntermediateOutputPath` (research R15).
3. `npm ci` and `npx playwright install --with-deps chromium` in `tools/agreement`.
4. `simcore vehicles select run --work "$RUNNER_TEMP/selection" --engines <artifact> --publish
   "$RUNNER_TEMP/vehicle-publish" --out "$RUNNER_TEMP/vehicle-selection/report.json"`, bounded by
   `timeout 100m`.
5. `simcore vehicles select validate-report` and `simcore vehicles select check-fixtures` on the report;
   either failing fails the job.
6. `simcore vehicles decide --report … --out "$RUNNER_TEMP/vehicle-selection/decision.json"`, and both
   files shown in the job log and summary.
7. Upload the `vehicle-selection` artifact (the report, the decision and the run's `report/`
   directories), also after a failed step, when the report exists.

No measured number is compared with a threshold. The job fails only when the report is invalid, the
fixture self-check fails or a step cannot run; every candidate's own failure is a status in the report
(FR-037).

## After the selection run

The decision is committed by an agent, not by CI: it downloads the `vehicle-selection` artifact of the
pull request's run, copies `report.json` to `simcore/vehicles/selection/report.json`, runs `simcore
vehicles decide` to write `simcore/vehicles/selection/decision.json`, and commits both in the same pull
request. The commit touches only the record's folder, so it starts `vehicle-agreement` (and its
staleness check) but not another selection run.

## Workflow tests

`tests/workflows/test_simcore_workflow.py` gains, for the new jobs: the triggers (`workflow_dispatch`,
`tools/vehicles/**`); each job's `needs`, `if` and `timeout-minutes` (2, 45, 2, 15 per leg, 15, 120); every
step's own bound; the matrix's `fail-fast: false` and its source in `gate-plan`; no workload install in
the `interp-prebuilt` leg; the prebuilt publishes before the workload and the AOT ones last; the
SHA-pinned actions; and that `agreement` and `measure` equal their committed definitions
(`tests/workflows/fixtures/simcore-015-jobs.json`), so a change to them fails the test. A test runs
`changed-paths.sh` on fixture diffs for each output.
