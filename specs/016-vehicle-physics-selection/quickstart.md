# Quickstart: Vehicle Physics Engine Selection

Every check on this page is also an automated test or a CI job; the page lists the commands they run,
for use during development. Run each command from the repository root unless its block changes
directory. Every expected outcome is what the command prints or its exit code.
`tests/MmoEngine.SimCore.Vehicles.Tests/Cli/QuickstartTests.cs` parses every `simcore` command below
against the CLI's command tree, checks that every path and project exists, every build name and mode is
known and every script is executable, as 015's quickstart test does.

## Prerequisites

- Everything 015's quickstart needs ([../015-shared-simulation-core/quickstart.md](../015-shared-simulation-core/quickstart.md)):
  the .NET SDK per `global.json`, Node 22, Python 3 with PyYAML.
- For Jolt's native builds: clang 18, CMake 3.28 or later, `git` with access to github.com, and
  nuget.org (the build script downloads the pinned Emscripten 3.1.56 packs).
- For the Rapier fit check: `cargo` (the manifest's `rust-toolchain.toml` pins Rust 1.97.0) and access
  to crates.io for the first `cargo fetch`.
- Every process that steps Bepu on the server needs `DOTNET_PreferredVectorBitWidth=128`; the vehicles
  test project sets it through its run settings, and a `simcore` command without it exits 2 naming the
  variable.

## Build and test

```bash
dotnet build MmoEngine.sln -warnaserror
dotnet format MmoEngine.sln --verify-no-changes
dotnet test MmoEngine.sln
```

Expected: each exits 0. The tests include the version 2 golden fixtures, the version 1 baseline manifest,
the vehicle configuration rules, the fixture engine's checks and faults, the status table, the decision
fixtures, the license classifier, the fit-check orchestration, the window check, Bepu's server-side
tests and the zero-allocation tests. The adapters build with the analyzer attached; a rule broken
outside an adapter's declared exemption fails the build.

## Check the configurations

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- config check simcore/vehicles/config/default.json
echo $?   # 0 valid, 1 invalid, 2 a file it cannot check
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles selection-config check simcore/vehicles/selection.json --profiles harness/profiles
echo $?   # 0 valid and every profile covered, 1 invalid or a profile not covered, 2 cannot check
```

Expected: no output and exit 0 for both. The second also checks the divergence window against every
network profile at 20, 30 and 60 Hz ([contracts/selection-config.md](contracts/selection-config.md#window-check-us5-scenario-6-fr-022)).

## Reproduce the vehicle test world

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles content generate --out simcore/vehicles/content/vehicle-world.mmcd
git diff --exit-code simcore/vehicles/content/vehicle-world.mmcd
```

Expected: one line, `simcore/vehicles/content/vehicle-world.mmcd: <bytes> bytes, checksum <hash>`, and
`git diff` exits 0.

## Rewrite the golden fixtures

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden
git diff --exit-code simcore/golden
```

Expected: one `unchanged` line per case, 015's and the version 2 cases, and a total; `git diff` exits 0.

## Verify the committed streams

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify
dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify --engine fixture
DOTNET_PreferredVectorBitWidth=128 dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify --engine bepu
```

Expected: the first prints 015's 19 `match` lines and `streams verify: 19 streams, 19 match, …`, exactly
as before this feature. The other two print one `match` line per vehicle stream of that engine and exit
0 ([contracts/vehicles-cli.md](contracts/vehicles-cli.md)).

## Run the behavior checks

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- vehicles checks --engine fixture
echo $?   # 0 every check passed, 1 a check failed, 2 cannot run
```

Expected: one `pass` line per US1 scenario 1 to 11 and the coverage check, each with its measured value
and bound, and exit 0. The fixture engine with no fault passes every check (SC-009).

## Build Jolt

```bash
NATIVE="$(mktemp -d)"
src/MmoEngine.SimCore.Vehicles.Jolt/native/build.sh --out "$NATIVE"
cat "$NATIVE/jolt/outcomes.json"
tools/vehicles/lockcount/build.sh --out "$NATIVE/lockcount"
```

Expected: `build.sh` exits 0 and `outcomes.json` lists `server`, `browser-module` and
`evidence-threaded`, each `Built` with its seconds. `$NATIVE/jolt/server/libmmo_vehicles_jolt.so` and
`$NATIVE/jolt/browser/` (`jolt-engine.mjs`, `jolt-engine.wasm`, `jolt-glue.mjs`, `jolt-engine.sha256`)
exist. A build that fails or passes 15 minutes is recorded as `BuildFailed` or `BuildTimedOut` with its
last 50 lines, and the script still exits 0.

```bash
MMO_VEHICLE_NATIVE_DIR="$NATIVE" dotnet test tests/MmoEngine.SimCore.Vehicles.Native.Tests
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles identity --engine jolt
```

Expected: the native tests pass (real Jolt through P/Invoke, the lock counter's self-test, the emmalloc
trace, the resting-offset calibration), and `identity` prints `jolt v5.7.0 <fingerprint>`.

## Run the lock test

```bash
dotnet build src/MmoEngine.SimCore.Cli -c Release
LD_PRELOAD="$NATIVE/lockcount/liblockcount.so" MMO_VEHICLE_NATIVE_DIR="$NATIVE" \
  dotnet src/MmoEngine.SimCore.Cli/bin/Release/net10.0/simcore.dll vehicles lockcount --engine jolt
echo $?   # 0 no lock taken, 1 a lock taken, 2 the counter is missing
DOTNET_PreferredVectorBitWidth=128 dotnet src/MmoEngine.SimCore.Cli/bin/Release/net10.0/simcore.dll vehicles lockcount --engine bepu
```

Expected: each prints the steps measured, 0 lock calls and the lock symbols the engine links (none), and
exits 0. Without `LD_PRELOAD`, the Jolt run exits 2, because it cannot find the counter.

## Run the agreement suite for one candidate and mode

This is one leg of the `vehicle-agreement` job: Jolt in `interp-prebuilt`.

```bash
APP_OUT="$(mktemp -d)"
WORK="$(mktemp -d)"
MMO_VEHICLE_NATIVE_DIR="$NATIVE" dotnet publish src/MmoEngine.SimCore.Vehicles.Jolt.Wasm -c Release -p:UsingBrowserRuntimeWorkload=false -o "$APP_OUT"
MMO_VEHICLE_NATIVE_DIR="$NATIVE" dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement prepare --engine jolt --seeds 1-50 --work "$WORK"
(cd tools/agreement && npm ci && npx playwright install chromium)
AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" AGREEMENT_BUILD=node-interp \
  node --test tools/agreement/node/agreement.test.mjs
AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" AGREEMENT_BUILD=node-interp-nojit \
  node --test tools/agreement/node/agreement.test.mjs
(cd tools/agreement && AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" AGREEMENT_MODE=interp-prebuilt npx playwright test --project chromium)
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work "$WORK" --require node-interp,node-interp-nojit,chromium-worker
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- vehicles divergence --work "$WORK" --require node-interp,node-interp-nojit,chromium-worker
```

Expected: `prepare` prints `prepared <work>: 64 streams (14 named, 50 generated), <steps> steps, rules
revision 1`; each runner exits 0; `compare` prints `match` for the three builds and `agreement: match`;
`divergence` reports every error as 0 and exits 0. For the relinked and AOT modes, install the
`wasm-tools` workload, publish without `-p:UsingBrowserRuntimeWorkload=false` (and with
`-p:RunAOTCompilation=true` for AOT, last), and use that mode's builds and `AGREEMENT_MODE`
([contracts/vehicles-cli.md](contracts/vehicles-cli.md#build-names)).

## Run the selection locally

The `vehicle-selection` job runs every candidate in every mode. A local run with only the prebuilt
publishes records the other two modes as NotBuilt (`ModeNotProduced`).

```bash
PUBLISH="$(mktemp -d)"
SELECT="$(mktemp -d)"
OUT="$(mktemp -d)"
tools/vehicles/ci/publish-host.sh Core interp-prebuilt "$PUBLISH"
tools/vehicles/ci/publish-host.sh Fixture interp-prebuilt "$PUBLISH"
tools/vehicles/ci/publish-host.sh Bepu interp-prebuilt "$PUBLISH"
MMO_VEHICLE_NATIVE_DIR="$NATIVE" tools/vehicles/ci/publish-host.sh Jolt interp-prebuilt "$PUBLISH"
MMO_RUNNER_CLASS=local-dev dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- \
  vehicles select run --work "$SELECT" --engines "$NATIVE" --publish "$PUBLISH" --out "$OUT/report.json"
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- vehicles select validate-report "$OUT/report.json"
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- vehicles select check-fixtures "$OUT/report.json"
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- vehicles decide --report "$OUT/report.json" --out "$OUT/decision.json"
```

Expected: `select run` prints one status line per candidate and mode and exits 0; Rapier is NotTested
(`FitCheckFailed`) in every mode; `validate-report` and `check-fixtures` exit 0; `decide` prints one
outcome per mode and exits 0. These numbers are measurements on this machine only, and the record's
`provenance.runnerClass` says `local-dev`.

## Decide from the committed report

```bash
OUT="$(mktemp -d)"
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles decide --report simcore/vehicles/selection/report.json --out "$OUT/decision.json"
cmp "$OUT/decision.json" simcore/vehicles/selection/decision.json
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles decision check-stale
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles decision gate-plan
```

Expected: `decide` exits 0 and `cmp` finds the files identical (SC-003); `check-stale` prints
`decision record: current` and exits 0, after a `provisional:` line when the record carries the flag;
`gate-plan` prints the vehicle agreement job's matrix as one JSON line.

The decision fixtures decide the same way:

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles decide --report simcore/vehicles/fixtures/decision/cost-100-110.report.json --out "$OUT/cost-100-110.json"
cmp "$OUT/cost-100-110.json" simcore/vehicles/fixtures/decision/cost-100-110.expected.json
```

Expected: both exit 0.

## Check Rapier's fit and a license input

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles fitcheck --engine rapier
echo $?   # 0 all criteria pass, 1 a criterion failed, 2 cannot run
dotnet run --project src/MmoEngine.SimCore.Cli -- vehicles license classify --inventory simcore/vehicles/fixtures/license/gpl-transitive.inventory.json
echo $?   # 0 Pass, 1 Blocked, 2 cannot run
```

Expected: `fitcheck` prints a verdict and its evidence for criteria c, b, d, f, a and e, with (c) failing
on `foldhash 0.2.0 (Zlib)`, and exits 1; `classify` prints `Blocked` naming the transitive
`GPL-3.0-only` component and exits 1 ([contracts/license-scan.md](contracts/license-scan.md)).

## Regenerate an engine's streams

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --engine fixture --all
echo $?   # 2: --reason is required
```

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --engine fixture --all --reason "Explain why the expected hashes change"
dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify --engine fixture
```

Expected: `record` prints one line per stream and a total, exits 0, and appends the reason to
`simcore/vehicles/streams/fixture/REGENERATIONS.md` when anything changed; `verify` then exits 0. A
recorded change to any engine's streams stales the decision record until a new selection run replaces it.

## Benchmarks

```bash
MMO_RUNNER_CLASS=local-dev dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Vehicles.Benchmarks -- --engine fixture --filter '*'
```

Expected: BenchmarkDotNet tables with Mean, P99 and Allocated for world steps of 1, 16, 64 and 256
vehicles, and an export under `BenchmarkDotNet.Artifacts/results/` labelled `measurement` with the
runner class `local-dev`. Bepu's run needs `DOTNET_PreferredVectorBitWidth=128` and records it.

## Workflow tests

```bash
python3 -m unittest discover -s tests/workflows -v
```

Expected: every test passes. `tests/workflows/test_simcore_workflow.py` pins the vehicle jobs' triggers,
needs, bounds, matrix and publish order, and that 015's `agreement` and `measure` jobs are unchanged
([contracts/ci-jobs.md](contracts/ci-jobs.md)).
