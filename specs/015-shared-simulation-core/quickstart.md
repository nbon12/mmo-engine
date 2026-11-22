# Quickstart: Shared Simulation Core

Every check on this page is also an automated test or a CI job. The page lists the commands they run,
for use during development. Run each command from the repository root unless its block changes
directory. Every expected outcome below is what the command prints or its exit code.

## Prerequisites

- .NET SDK 10.0.100 or a later 10.0.1xx patch (`global.json`). The analyzer's Roslyn pin matches this
  band ([contracts/analyzer-rules.md](contracts/analyzer-rules.md#roslyn-pin)).
- Node 22 (slice 2, not on main yet), for the agreement and measurement runners that slice 2 adds in
  tools/agreement/.
- Python 3 with PyYAML, for the workflow tests.
- Optional (slice 2, not on main yet): the `wasm-tools` workload, only for the relinked interpreted and
  AOT builds in [the measurement report](#the-measurement-report). Without it, `dotnet publish` uses
  the prebuilt runtime, which is the build the gating CI job tests.

## Build and test

```bash
dotnet build MmoEngine.sln -warnaserror
dotnet format MmoEngine.sln --verify-no-changes
dotnet test MmoEngine.sln
```

Expected: each command exits 0. The build compiles `src/MmoEngine.SimCore/` with the determinism
analyzer attached, so a violation fails it with an `error MMO10xx` line on the offending source line.
The tests include the analyzer's backstop compile of the core's sources, the scenario streams at 20,
30 and 60 Hz, the golden fixtures, the configuration rule table, the fixed trig table and the
cross-build tables on the server build, a dry run of the server benchmarks' export, and the
zero-allocation tests.

## See the analyzer reject a violation

```bash
dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~ABuildWithAViolationFails" --logger "console;verbosity=detailed"
```

Expected: one passing test. Its log holds the throwaway project's build output, which contains
`error MMO1004: 'foreach' enumerates 'System.Collections.Generic.Dictionary<int, int>', whose order is not defined`.

```bash
dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~EachSampleProducesExactlyItsRule"
```

Expected: every row passes. Each violating sample produces exactly its rule's diagnostic and each
allowed sample none ([contracts/analyzer-rules.md](contracts/analyzer-rules.md#tests)).

## Check a configuration file

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- config check simcore/config/default.json
echo $?   # 0 valid, 1 invalid, 2 usage or a file it cannot check
```

Expected: no output and exit 0. An invalid file prints one `path: message` line per error and exits 1
([contracts/config-rules.md](contracts/config-rules.md)). A missing file, one larger than 1 MiB or one
that cannot be read is named on stderr and exits 2
([contracts/simcore-cli.md](contracts/simcore-cli.md#config-check)).

## Reproduce the test world

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- content generate --out simcore/content/test-world.mmcd
git diff --exit-code simcore/content/test-world.mmcd
```

Expected: one line, `simcore/content/test-world.mmcd: <bytes> bytes, checksum <hash>`, and `git diff`
exits 0 because the generator reproduces the committed file byte for byte.

## Rewrite the golden fixtures

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden
git diff --exit-code simcore/golden
```

Expected: one `<format>/<case>: unchanged, <bytes> bytes` line per case and a total, exit 0, and
`git diff` exits 0. After a deliberate format change, the changed `.bin` files show in the diff and are
committed with their `.json` ([contracts/simcore-cli.md](contracts/simcore-cli.md#golden-write)).

## Run the exhaustive trigonometric sweeps

```bash
MMO_EXHAUSTIVE_MATH=1 dotnet test tests/MmoEngine.SimCore.Tests -c Release --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Math" --logger "console;verbosity=detailed"
```

Expected: every math test passes, and the console output holds one
`SIMMATH <function> max <ulp> ulp at a 0x... b 0x... (bound <bound>)` line per swept function and
bound: Sin, Cos, SinCos and Tan also write a `<function>Large` line for |x| > 12867. The
detailed console logger is needed because the sweeps write those lines to the test output, which
`dotnet test` shows for a passing test only at that verbosity. The sweeps take about 2 minutes on 4
cores, so without the variable they skip; the fixed table in the normal test run is the gate
([research.md](research.md) R3).

## Verify the committed streams

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify
echo $?   # 0 all match, 1 divergence, 2 cannot run
```

Expected: one `<name>: match, <steps> steps, final <hash>` line per stream in `simcore/streams/`, then
`streams verify: 19 streams, 19 match, 0 diverged, 0 not comparable, 0 refused`, and exit 0
([contracts/simcore-cli.md](contracts/simcore-cli.md#streams-verify)).

## Run the agreement suite

Planned for slice 2, not on main yet: these commands need the WebAssembly host, the agreement runners and the SimCore workflow, which slice 2 adds.

This is what the `agreement` job in `.github/workflows/simcore.yml` runs. Publish the WebAssembly
build, prepare a work directory, run the build in Node and in browser Workers, then compare.

```bash
APP_OUT="$(mktemp -d)"
WORK="$(mktemp -d)"
dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$APP_OUT"
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement prepare --seeds 1-50 --work "$WORK"
```

Expected: both exit 0, and `prepare` prints
`prepared <work>: 69 streams (19 named, 50 generated), <steps> steps, rules revision <r>`. With the
`wasm-tools` workload installed, the publish relinks the runtime; the run below still works, but it
then tests the relinked build rather than the prebuilt one CI's `agreement` job tests.

```bash
(cd tools/agreement && npm ci && npx playwright install chromium firefox webkit)
```

Expected: exit 0. On Linux, `npx playwright install --with-deps chromium firefox webkit` also installs
the browsers' system libraries, which needs root. Firefox and WebKit are optional: without them,
`compare` reports those builds `not run`.

```bash
AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" AGREEMENT_BUILD=node-interp \
  node --test tools/agreement/node/agreement.test.mjs
AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" AGREEMENT_BUILD=node-interp-nojit \
  node --test tools/agreement/node/agreement.test.mjs
```

Expected: each run passes every test (one per stream, plus the batch, perturbation, refusal and
cross-build tables checks) and exits 0. The second run has the jiterpreter off.

```bash
(cd tools/agreement && AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" npx playwright test)
```

Expected: exit 0, with one passing Worker test per browser project. To run Chromium only, the browser
CI requires:

```bash
(cd tools/agreement && AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" npx playwright test --project chromium)
```

Expected: exit 0, with one passing Worker test.

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work "$WORK"
echo $?   # 0 all agree, 1 divergence, 2 cannot run
```

Expected: `match` with `tables match` for `node-interp`, `node-interp-nojit` and `chromium-worker`,
and for `firefox-worker` and `webkit-worker` when they ran; `not run` for the rest; then `agreement: match` and exit 0. The full
results are in `$WORK/report/agreement.json`. The work directory's layout and the divergence report
are in [contracts/simcore-cli.md](contracts/simcore-cli.md#agreement-work-directory); the runners and
the exports they call are in [contracts/wasm-api.md](contracts/wasm-api.md).

## Regenerate the committed streams

Expected hashes change only through this command, with a reason (FR-028).

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --all
echo $?   # 2: --reason is required
```

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --all --reason "Explain why the expected hashes change"
dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify
```

Expected: `record` prints one line per stream (`changed`, `unchanged`, `added` or `removed`, with the
old and new file and final-state hashes) and a total, exits 0, and appends an entry with the reason
to `simcore/streams/REGENERATIONS.md` when anything changed. `verify` then exits 0. Commit the streams
and the entry together. When the test world changed, run `content generate` first, because every
stream names the world's checksum.

## Benchmarks

```bash
MMO_RUNNER_CLASS=local-dev dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Benchmarks -- --filter '*'
```

Expected: BenchmarkDotNet tables with Mean, P99 and Allocated columns for the movement, weapon,
projectile, hitbox-build and state-hash benchmarks, and
`BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json` labelled `measurement` with the
runner class `local-dev`. The numbers are measurements on this machine only.

## The measurement report

Planned for slice 2, not on main yet: these commands need the WebAssembly host, the agreement runners and the SimCore workflow, which slice 2 adds.

This builds the report for the prebuilt interpreted build, on a machine without the `wasm-tools`
workload. It needs the benchmark output above and Playwright's Chromium from the agreement section.

```bash
export MMO_RUNNER_CLASS=local-dev
PUBLISH="$(mktemp -d)"
MWORK="$(mktemp -d)"
dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$PUBLISH/prebuilt"
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement prepare --seeds 1-50 --work "$MWORK"
node tools/agreement/lib/measure.mjs --work "$MWORK" \
  --server-benchmarks BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json \
  --mode interp-prebuilt="$PUBLISH/prebuilt" \
  --unproduced interp-relinked="not built on this machine" \
  --unproduced aot="not built on this machine" \
  --out "$MWORK/simcore-measurement.json"
dotnet run --project src/MmoEngine.SimCore.Cli -- measure validate-report "$MWORK/simcore-measurement.json"
echo $?   # 0 valid, 1 invalid, 2 unreadable
```

Expected: `measure.mjs` exits 0, and `validate-report` prints nothing and exits 0. Without
`MMO_RUNNER_CLASS`, `measure.mjs` exits 2.

With the workload installed (`dotnet workload install wasm-tools`), the relinked and AOT builds come
from the publishes below. The block then runs the Node agreement on each build against `$MWORK` and
compares them with the two builds required. After that, pass the builds to `measure.mjs` with
`--mode` instead of `--unproduced`:

```bash
dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$PUBLISH/relinked"
dotnet publish src/MmoEngine.SimCore.Wasm -c Release -p:RunAOTCompilation=true -o "$PUBLISH/aot"
AGREEMENT_APP="$PUBLISH/relinked/wwwroot" AGREEMENT_WORK="$MWORK" AGREEMENT_BUILD=node-interp-relinked \
  node --test tools/agreement/node/agreement.test.mjs
AGREEMENT_APP="$PUBLISH/aot/wwwroot" AGREEMENT_WORK="$MWORK" AGREEMENT_BUILD=node-aot \
  node --test tools/agreement/node/agreement.test.mjs
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work "$MWORK" --require node-interp-relinked,node-aot
```

Expected: every command exits 0, and `compare` prints `match` with `tables match` for
`node-interp-relinked` and `node-aot`, `not run` for the builds that did not run, then
`agreement: match`.

The report's schema and what `validate-report` checks are in
[contracts/measurement-report.md](contracts/measurement-report.md).

## Workflow tests

Planned for slice 2, not on main yet: these commands need the WebAssembly host, the agreement runners and the SimCore workflow, which slice 2 adds.

```bash
python3 -m unittest discover -s tests/workflows -v
```

Expected: every test passes and the command exits 0. `tests/workflows/test_simcore_workflow.py` pins the
`SimCore` workflow: its triggers and path filters (FR-027, US2 scenario 7), its SHA-pinned actions,
the `agreement` job's `timeout-minutes: 10` (SC-006), its three browsers, the `measure` job's steps,
and the `"SimCore"` entry in `auto-merge.yml`'s `workflow_run` list.
