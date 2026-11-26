# Contract: `simcore` commands for vehicles

A delta on 015's [simcore-cli.md](../../015-shared-simulation-core/contracts/simcore-cli.md). The `simcore`
executable (`src/MmoEngine.SimCore.Cli/`) keeps every 015 command, option, output and exit code; the
changes below are additive. Run it from the repository root, as in 015:

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- <command> [options]
```

015's settings hold: `DOTNET_ENVIRONMENT` is validated; logs are Serilog compact JSON on stderr; results
are plain text on stdout; every file is written atomically; an empty path option is a usage error (exit
2). Exit codes keep 015's meaning: 0 success, 1 a divergence or a validation failure, 2 a usage error,
an unreadable or refused input, a NotComparable stream, or a check that could not run, with the
selection-specific codes FR-037 sets (below).

**Engines.** `--engine` takes `bepu`, `jolt`, `rapier` or `fixture`. Native engines load their library
from `--native-dir`, default `$MMO_VEHICLE_NATIVE_DIR`. A Bepu command checks the vector pin first
(research R8) and exits 2 with the guard's message when it is missing. `rapier` has no engine factory
while its fit check fails (research R12): a command that would step it exits 2 with
`rapier: not built; its fit check failed criterion (c), see the selection report`.

## Changed 015 commands

| Command | Change |
| --- | --- |
| `config check <file>` | also validates the optional `vehicles` section ([vehicle-config.md](vehicle-config.md)) |
| `golden write [--dir simcore/golden]` | also writes the version 2 stream cases; 015's `unsupported-version` now writes version 3 ([vehicle-formats.md](vehicle-formats.md#golden-fixtures)) |
| `streams record --engine <engine> (--scenario <name> \| --all) --reason "<why>" [--out simcore/vehicles/streams/<engine>] [--content simcore/vehicles/content] [--config simcore/vehicles/config/default.json]` | records the engine's vehicle streams (FR-017) from `VehicleScenarioCatalog`, with the engine's identity and `VehicleRulesRevision` in each header; appends to `<out>/REGENERATIONS.md`; without `--engine`, exactly 015's behavior |
| `streams verify --engine <engine> [--dir simcore/vehicles/streams/<engine>] [--content simcore/vehicles/content]` | runs the engine's committed streams and checks both expected hashes per step; NotComparable for another identity or vehicle rules revision (exit 2); without `--engine`, 015's |
| `agreement prepare --engine <engine> --work <dir> [--seeds 1-50] [--streams simcore/vehicles/streams/<engine>] [--content simcore/vehicles/content] [--config simcore/vehicles/config/default.json] [--selection-config simcore/vehicles/selection.json] [--fault <code>]` | writes a [vehicle work directory](#vehicle-work-directory) with manifest version 2: the committed streams, generated streams `vseed-<nnn>`, server records (layout 2), server windows, batch inputs, refusals; `--fault` (fixture only, an identity-preserving browser fault) writes `injectedFault`; without `--engine`, 015's |
| `agreement compare --work <dir> [--require <builds>] [--summary <file>] [--compare full\|v1-part]` | reads manifest version 1 or 2; `--compare v1-part` (version 2 only) compares only the version 1 part bytes and `v1PartHash` per step (the NonExact gate, FR-029); for version 2 it also compares each build's `.windows` file with the server's records (US2 scenario 4) |

`streams record` with `--engine` prints 015's line shapes. `turning-circle`, `brake-to-stop` and
`ramp-jump` build their commands while recording (the recording governor of US1 scenario 3 and the
speed-triggered phases), by stepping the engine, so their commands differ between engines; recording is
deterministic, so each stream verifies like any other.

### Build names

015's table gains three builds and a mode column:

| Build | Mode | Runs |
| --- | --- | --- |
| `node-interp` | `interp-prebuilt` | Node, prebuilt runtime, jiterpreter on |
| `node-interp-nojit` | `interp-prebuilt` | Node, prebuilt runtime, jiterpreter off |
| `chromium-worker` | `interp-prebuilt` | a dedicated Worker in headless Chromium |
| `firefox-worker`, `webkit-worker` | `interp-prebuilt` | 015's optional Workers; not run for vehicles |
| `node-interp-relinked` | `interp-relinked` | Node, relinked runtime, jiterpreter on |
| `node-interp-relinked-nojit` | `interp-relinked` | Node, relinked runtime, jiterpreter off (new) |
| `chromium-worker-relinked` | `interp-relinked` | a Chromium Worker on the relinked build (new) |
| `node-aot` | `aot` | Node, AOT |
| `chromium-worker-aot` | `aot` | a Chromium Worker on the AOT build (new) |

Required builds per mode (FR-018): `interp-prebuilt` `node-interp,node-interp-nojit,chromium-worker`;
`interp-relinked` `node-interp-relinked,node-interp-relinked-nojit,chromium-worker-relinked`; `aot`
`node-aot,chromium-worker-aot`. For a manifest version 1 work directory, `compare` reports exactly 015's
seven builds, so 015's output and job summary do not change; for version 2 it reports the builds of the
modes the manifest lists.

## Vehicle work directory

015's layout ([agreement work directory](../../015-shared-simulation-core/contracts/simcore-cli.md#agreement-work-directory)),
plus:

```text
<work>/
├── manifest.json                      formatVersion 2
├── streams/<stream>.mmrs              version 2 streams
├── server/<stream>.records            layout 2 records
├── server/<stream>.windows            the server's records cut into windows (the reference)
├── engine/identity.json               the engine identity the work directory was prepared for
├── <build>/<stream>.records           each build's records
├── <build>/<stream>.windows           each build's windowed runs
└── report/                            agreement.json, divergences/, divergence.json
```

### Manifest version 2

015's members, with these changes:

| Field | Rule |
| --- | --- |
| `formatVersion` | 2 |
| `formats` | `{ "inputCommand": 1, "collisionData": 1, "recordedStream": 2, "stateSerialization": 2, "vehicleConfig": 1 }` |
| `vehicleRulesRevision` | `SimCoreVersion.VehicleRulesRevision`; compare refuses a mismatch as NotComparable |
| `engine` | `{ "engine": "jolt", "release": "v5.7.0", "fingerprint": "…" }`; a build that reports another identity through `EngineIdentity()` writes `<stream>.error.txt` |
| `host` | `{ "assembly": "MmoEngine.SimCore.Vehicles.Jolt.Wasm.dll", "exportsPath": "MmoEngine.SimCore.Vehicles.Jolt.Wasm.VehicleExports", "engineGlue": "engine/jolt-glue.mjs" }` (null glue for managed engines) |
| `recordLayout` | 2 |
| `windows` | `{ "seconds": 0.5 }`; each stream entry gains `windowSteps` |
| `perturbation` | `{ "stream": "flat-accel-30hz", "step": 15, "field": "vehicles[0].position.x" }` |
| `streams[].vehicles` | the stream's vehicle count, beside `characters` |
| `injectedFault` | null, or `{ "stream", "step" }` for the `fixture-perturbed` candidate: the runners pass the step as `perturbStep` to that stream's `RunStream` |

NotComparable lines gain `vehicle rules revision <m> in the work directory; this core implements vehicle
rules revision <c>` and `engine <identity> in the work directory; this build is <identity>`.

## `vehicles` commands

| Command | Purpose | Exit codes |
| --- | --- | --- |
| `vehicles content generate [--out simcore/vehicles/content/vehicle-world.mmcd]` | writes the vehicle test world (FR-007); output `<path>: <bytes> bytes, checksum <hash>` | 0; 2 |
| `vehicles identity --engine <engine>` | prints `<engine> <release> <fingerprint>` from the pin file and its inputs | 0; 2 |
| `vehicles selection-config check [<file>] [--profiles harness/profiles]` | validates the selection configuration and runs the window check | 0 valid; 1 invalid or a profile not covered; 2 |
| `vehicles checks --engine <engine> [--streams simcore/vehicles/streams/<engine>] [--content simcore/vehicles/content] [--selection-config simcore/vehicles/selection.json] [--seeds 1-50] [--out <file>]` | runs US1 scenarios 1 to 11 and the coverage check on the engine's server build, each on a fresh engine instance; one `<scenario> <stream>: pass\|fail <measured> (bound <bound>)` line each, then a total | 0 every check passed; 1 any failed; 2 cannot run |
| `vehicles lockcount --engine <engine> [--steps 1000] [--warmup 300]` | steps `flat-accel-30hz` and `pile-up` and reports lock calls counted by the interposer (native) or the instrumented sites (Bepu); run by the selection under `LD_PRELOAD` | 0 none taken; 1 a lock taken; 2 the counter is missing or cannot run |
| `vehicles divergence --work <dir> [--require <builds>] [--selection-config simcore/vehicles/selection.json] [--summary <file>]` | measures the windowed errors of every build's `.windows` against the server's (US5); writes `report/divergence.json` | 0 within bounds; 1 beyond a bound; 2 cannot run |
| `vehicles license scan --engine <engine> --target server\|browser --inputs <dir> --out <inventory>` | runs the collectors on a build's outputs | 0; 2 |
| `vehicles license classify --inventory <file> [--boundary simcore/vehicles/license/boundary.json]` | classifies an inventory; prints the license result | 0 Pass; 1 Blocked; 2 |
| `vehicles fitcheck --engine rapier [--out <file>]` | evaluates FR-030's criteria (research R12) | 0 all pass; 1 a criterion failed; 2 cannot run |
| `vehicles select run --work <dir> --engines <dir> --publish <dir> --out <report>` | the selection run ([selection-report.md](selection-report.md#how-the-report-is-produced)) | 0 every candidate has a status in every mode; 1 the report fails validation; 2 cannot run (FR-037) |
| `vehicles select validate-report <file>` | validates a selection report | 0; 1; 2 |
| `vehicles select check-fixtures <report>` | the fixture self-check: `fixture` Qualified and `fixture-perturbed` Diverged (`RecordDiverged`, step 15, `vehicles[0].position.x`) in every mode, and `decide --fixtures` giving `fixture` with BitExact | 0; 1 a fixture ended otherwise; 2 |
| `vehicles decide --report <file> --out <file> [--fixtures]` | the decision ([decision-record.md](decision-record.md)) | 0 every outcome; 1 the report fails validation; 2 usage or unreadable |
| `vehicles decision check-stale [--record simcore/vehicles/selection/decision.json]` | the staleness check (FR-028) | 0 current; 1 stale; 2 |
| `vehicles decision gate-plan [--record simcore/vehicles/selection/decision.json]` | the vehicle agreement job's plan as one JSON line | 0; 2 |

### `vehicles select run`

- `--work` must not exist or be empty; each candidate gets `<work>/<candidate>/` for its agreement work
  directory and logs.
- `--engines` is the unpacked `vehicle-engines` artifact: `<engine>/server/`, `<engine>/browser/`, and
  `<engine>/outcomes.json` with one build outcome per native build.
- `--publish` holds `<host>/<mode>/` publish outputs and `<host>/<mode>/outcome.json` per host and mode,
  written by the job's publish steps ([ci-jobs.md](ci-jobs.md#vehicle-selection)).
- Each candidate's server-side phases run in child processes of this command with the candidate's
  environment and a timeout per phase; a crash or timeout is that phase's reason, never the run's.
- The browser runners are `node --test tools/agreement/node/agreement.test.mjs` and `npx playwright test
  --project chromium` in `tools/agreement`, with `AGREEMENT_APP`, `AGREEMENT_WORK`, `AGREEMENT_BUILD` and
  `AGREEMENT_MODE` set per build.
- Output: one line per candidate and mode, `<candidate> <mode>: <status> <first reason>`, then
  `selection: <n> candidates, report <path>`.
- `MMO_RUNNER_CLASS` is required (exit 2 without it).

## Tests

In `tests/MmoEngine.SimCore.Tests/Cli/` and `tests/MmoEngine.SimCore.Vehicles.Tests/Cli/`: each command's
options and exit codes; `streams record --engine fixture` on a temporary copy appending to its
`REGENERATIONS.md`; `streams verify --engine fixture` in a child process beside the in-process run
(US1 scenario 8); `agreement prepare --engine fixture` writing manifest version 2; `agreement compare`
on records from `StreamRunner.Run(…, perturbStep: 15)` exiting 1 and naming step 15 and
`vehicles[0].position.x` (US2 Independent Test, SC-004); `agreement compare` on a 015 work directory
printing exactly 015's seven build lines; `vehicles decide` and `check-stale` on the fixture reports.
