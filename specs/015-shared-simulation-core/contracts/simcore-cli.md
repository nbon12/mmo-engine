# Contract: `simcore` CLI

`src/MmoEngine.SimCore.Cli/` builds the `simcore` executable (`AssemblyName` `simcore`). It is a thin
System.CommandLine shell over `MmoEngine.SimCore` and `MmoEngine.SimCore.Server`, like the
repository's other CLIs. It generates the test content, writes the golden fixtures, records and
verifies the committed streams, prepares and compares the agreement suite, validates the measurement
report and checks configuration files.

Run it from the repository root: every default path is relative to the current directory.

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- <command> [options]
```

## Settings, output and exit codes

- `DOTNET_ENVIRONMENT` must be Development, Dev, Test, Staging or Production, and is Development when
  unset. The known set is the simulator's, kept equal by a test. An invalid value exits 2 and names
  the variable.
- Logs are Serilog compact JSON on stderr, with the property `Tool` = `simcore`.
- Results are plain text lines on stdout, in the shapes given below. Report files are written where
  each command says.
- Every file the CLI creates or replaces is written to a temporary file in the same directory and then
  renamed, so an interrupted run never leaves a half-written file. The two appends (to
  `REGENERATIONS.md` and to the `--summary` file) are single writes at the end of a command.

| Exit code | Meaning |
| --- | --- |
| 0 | success: everything matched, or the input is valid |
| 1 | a divergence, or a validation failure |
| 2 | a usage error, an unreadable or refused input, a NotComparable stream (another format version or rules revision), or a check that could not run |

| Command | Purpose | Exit codes |
| --- | --- | --- |
| `content generate [--out simcore/content/test-world.mmcd]` | Writes the deterministic test world | 0; 2 |
| `golden write [--dir simcore/golden]` | Writes every golden fixture's `.bin` from its `.json` (FR-023) | 0; 2 |
| `streams record (--scenario <name> \| --all) --reason "<why>" [--out simcore/streams] [--content simcore/content] [--config simcore/config/default.json]` | Regenerates committed streams and their expected hashes (FR-028) | 0; 1 a server run faulted; 2 |
| `streams verify [--dir simcore/streams] [--content simcore/content]` | Runs every committed stream through the server build and checks every expected hash | 0 all match; 1 a divergence; 2 |
| `agreement prepare --work <dir> [--seeds 1-50] [--streams simcore/streams] [--content simcore/content] [--config simcore/config/default.json]` | Writes the agreement work directory and the server build's records | 0; 1 a committed stream diverged on the server; 2 |
| `agreement compare --work <dir> [--require <builds>] [--summary <file>]` | Compares every build's records with the server's | 0 all agree; 1 a divergence; 2 cannot run |
| `measure validate-report <file>` | Checks the measurement report ([measurement-report.md](measurement-report.md)) | 0 valid; 1 invalid; 2 unreadable |
| `config check <file>` | Validates a configuration file ([config-rules.md](config-rules.md)) | 0 valid; 1 invalid; 2 usage |

## `content generate`

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- content generate --out simcore/content/test-world.mmcd
```

`TestWorldGenerator` (`src/MmoEngine.SimCore.Server/`) builds the test world from a fixed seed: a
heightfield with slopes on both sides of the slope limit, a ridge and a cliff, and static meshes with
a wall, stairs of two rises, a low ledge, a concave corner and a thin wall (spec Assumptions, "Test
content"). It is encoded as `MMCD` v1 ([collision-data-format.md](collision-data-format.md)).

- Output: one line, `<path>: <bytes> bytes, checksum <hash>`, with the checksum as 16 lowercase hex
  digits.
- The bytes are the same on every run and every machine. The test
  `ContentGenerateReproducesTheCommittedWorld` asserts that the output equals the committed
  `simcore/content/test-world.mmcd` byte for byte.
- A changed world has a new checksum. Every committed stream names the old one, so `streams verify`
  refuses them (`CollisionDataMissing`, exit 2) until they are regenerated with `streams record`.

## `golden write`

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden
```

Writes the `.bin` of every case under `<dir>/input/`, `<dir>/collision/` and `<dir>/stream/` from its
`.json`, under the [shared fixture
rules](input-command-format.md#shared-fixture-rules). This is the only code that writes the golden
`.bin` files; the format tests open them read-only.

1. Read every `<case>.json`, in ordinal name order per format. A file that is not valid JSON, a case
   whose `format` does not match its folder, or a refusal case whose `base` is not a valid case of the
   same format exits 2, and nothing is written.
2. Write each valid case's `.bin` with the core's encoder for its format (`InputCodec.TryEncode`,
   `CollisionFile.Encode`, `RecordedStream.Encode`). An encoder that refuses the values (the
   collision and stream encoders decode their own output) exits 2, and nothing is written.
3. Write each refusal case's `.bin` by applying its `mutations` to its base's bytes.
4. An unchanged `.bin` is not rewritten.

The command does not check `expectedError`; the format tests do (US6 scenarios 2 to 5). Output, one
line per case, then a total:

```text
input/example: unchanged, 17 bytes
collision/tiny-world: written, 188 bytes
golden write: 92 cases, 1 written, 91 unchanged
```

The test `GoldenWriteReproducesTheCommittedFixtures` runs it on a copy of `simcore/golden/` in a
temporary directory and asserts that every `.bin` is unchanged.

## `streams record`

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --all --reason "Explain why the expected hashes change"
```

This is the only way the committed expected hashes change (FR-028). No test and no other command
writes under `simcore/streams/`; the tests open stream files read-only.

1. Refuse with exit 2 when `--reason` is missing, empty, whitespace only, longer than 500 characters
   or more than one line; when both or neither of `--scenario` and `--all` are given; or when
   `--scenario` names an unknown scenario (the message lists the known names).
2. For each selected scenario, in name order, build its stream with `ScenarioCatalog`: the
   configuration (from `--config` plus the scenario's overrides), the initial state, the match salt
   and the commands, against the collision file in `--content` that the scenario names.
3. Run it through the server build (`StreamRunner`) to compute every step's expected hash. A step
   fault stops the command with exit 1 and a refusal (an invalid configuration, a missing collision
   file) with exit 2; either way nothing is written.
4. Write `<out>/<name>.mmrs` with `rulesRevision` = `SimCoreVersion.RulesRevision`
   ([recorded-stream-format.md](recorded-stream-format.md)). An unchanged stream is not rewritten.
5. With `--all`, a `.mmrs` file in `<out>` whose scenario is no longer in the catalog is deleted and
   listed as removed.
6. When at least one stream was added, changed or removed, append an entry to
   `<out>/REGENERATIONS.md`. When nothing changed, the file is not touched and the command exits 0.

Output, one line per selected stream, then a total:

```text
flat-run-30hz: changed; file 3f9d0c1a72b4e865 -> 8a17e2c40d5b9f31; final state 0c6e1f93a2d7b548 -> 5b2a90e7c13d4f86; first changed step 12
wall-slide-45: unchanged; file 77d0a3c9e15b2f48; final state 1e94b07c5a3d28f6
stairs-climb: added; file c50e8b2f193a7d64; final state 9f31a6d0e2b47c85
streams record: 18 streams, 1 changed, 1 added, 0 removed, 16 unchanged
```

The file hash is the stream file's XxHash64 trailer and the final state hash is the expected hash of
the last step, both as 16 lowercase hex digits. "First changed step" is the first step whose expected
hash differs from the old file's.

### `REGENERATIONS.md`

`simcore/streams/REGENERATIONS.md` starts with a `# Stream regenerations` heading and one sentence
saying the file is appended by `simcore streams record`. Each regeneration appends one entry:

```markdown
## 2026-11-14T09:30:12Z

- Reason: Raised the slide loop's iteration limit from 4 to 5.
- Command: `simcore streams record --all`
- Rules revision: 2 (was 1)

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| flat-run-30hz | 3f9d0c1a72b4e865 -> 8a17e2c40d5b9f31 | 0c6e1f93a2d7b548 -> 5b2a90e7c13d4f86 | 12 |
| stairs-climb | added: c50e8b2f193a7d64 | added: 9f31a6d0e2b47c85 | |
| old-scenario | removed: 1b2c3d4e5f607182 | removed | |
```

- The heading is the UTC time of the run, to the second, in ISO 8601.
- "Rules revision" shows the old value when any changed stream carried another one.
- Unchanged streams are not listed.
- The reason is written exactly as given.

The entry is committed with the streams, so every change to an expected hash is a visible commit
with its reason (FR-028). The CLI test `RecordAppendsTheReasonAndOldAndNewHashes` runs the command
on a copy of the streams in a temporary directory and asserts the entry and the output lines.

## `streams verify`

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify
```

Runs every `*.mmrs` in `--dir`, in ordinal name order, through the server build, with the collision
files in `--content` keyed by checksum. Each step's hash is compared with the stream's expected hash.
Nothing is written.

Each stream gets one outcome ([recorded-stream-format.md](recorded-stream-format.md#outcomes)) and
prints it:

| Outcome | Output |
| --- | --- |
| Match | `<name>: match, <steps> steps, final <hash>` |
| Diverged | the divergence report against the expected hashes, which starts `DIVERGED stream <name>, build server, first divergent step <n>, against the expected hashes` and gives both hashes |
| NotComparable | `NOT COMPARABLE stream <name>: ` and both values, for example `rules revision 1 in the stream; this core implements rules revision 2`, or `unsupported version 2; this reader supports version 1` |
| Refused | `REFUSED stream <name>: ` and the error's message, for example `stream collisionChecksum at offset 36: collision data 9224e3fa3b3fca9c was not supplied` when the named collision file is not in `--content` |

The last line is `streams verify: <n> streams, <m> match, <d> diverged, <c> not comparable, <r> refused`.

Exit 1 when any stream diverged; otherwise 2 when any was not comparable or refused, or when the
directory holds no stream; otherwise 0. Only Match passes. A committed stream carries the expected
hashes, not the states, so a verify divergence names the step and both hashes but no fields
([state-serialization.md](state-serialization.md#divergence-report)). The field-by-field report comes
from `agreement compare`, where both sides have their states.

`EveryCommittedStreamMatchesInAFreshProcess` runs `simcore streams verify` as a child process while the
scenario tests run the same streams in the test process, so every stream is stepped in two separate
processes (US1 scenarios 7 and 8, the US1 Independent Test).

## `agreement prepare`

```bash
WORK="$(mktemp -d)"
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement prepare --seeds 1-50 --work "$WORK"
```

- `--work` must not exist or must be an empty directory; anything else exits 2. Each run uses a unique
  temporary directory, so runs are safe in parallel and against leftovers.
- `--seeds` is `<first>-<last>` or one number, with 1 ≤ first ≤ last ≤ 999. CI passes `1-50`
  (FR-026, SC-001).

It writes the [work directory](#agreement-work-directory):

1. Every committed stream from `--streams`, run through the server build. Its expected hashes are
   checked as `streams verify` does; a divergence exits 1 and a refusal exits 2.
2. One generated stream per seed ([generated streams](#generated-streams)), with its expected hashes
   from the server build.
3. Every collision file the streams name, under `collision/<checksum>.mmcd`.
4. The server build's records for every stream, under `server/<stream>.records`.
5. The batch inputs for every batchable stream, under `batch/<stream>/`.
6. The refusal fixtures, under `refusals/`.
7. `manifest.json`, last, so a work directory without a manifest is incomplete.

Output: one line, `prepared <work>: <n> streams (<named> named, <generated> generated), <steps> steps, rules revision <r>`.

## `agreement compare`

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work "$WORK" --summary "$GITHUB_STEP_SUMMARY"
```

- `--require` is a comma-separated list of [build names](#build-names). It defaults to
  `node-interp,node-interp-nojit,chromium-worker`. The `measure` job passes
  `node-interp-relinked`, plus `node-aot` when the AOT build was produced.
- Every known build with a folder under `<work>` is compared, required or not. A build with no folder
  is `NotRun`. Folders with other names are ignored.

For each compared build and each manifest stream, the comparison reads `<build>/<stream>.records` and
the server's `server/<stream>.records`, step by step. The first step where any of these holds is the
first divergent step:

- the build's record is missing, or the build wrote `<stream>.error.txt` instead;
- the step numbers differ;
- the hashes differ, or the build's hash differs from the stream's expected hash;
- the state bytes differ (so equal hashes over different states still diverge).

| Per-build status | When |
| --- | --- |
| `Match` | every stream matched at every step |
| `Diverged` | at least one stream diverged, failed or is missing |
| `NotRun` | no folder; an error only when the build is required |

Exit 2 when the manifest is missing or unreadable, a server records file is missing, a required build
is `NotRun`, or the manifest's rules revision or format versions differ from the core's
(NotComparable, naming both values). Otherwise exit 1 when any build is `Diverged`, else 0. A
Firefox or WebKit build that Playwright could not install is reported `NotRun`, never as a pass.

Output, one line per known build, the first divergence report of each diverged build, then the outcome:

```text
node-interp: match, 68 streams, 24300 steps
node-interp-nojit: match, 68 streams, 24300 steps
chromium-worker: diverged in 1 of 68 streams; first: seed-017 at step 212
firefox-worker: match, 68 streams, 24300 steps
webkit-worker: not run
node-interp-relinked: not run
node-aot: not run
agreement: diverged
```

Files, under `<work>/report/`:

- `agreement.json`, every build and stream (the example shows three of the seven builds):

  ```json
  {
    "formatVersion": 1,
    "outcome": "Diverged",
    "rulesRevision": 1,
    "streams": 68,
    "required": ["node-interp", "node-interp-nojit", "chromium-worker"],
    "builds": [
      { "build": "node-interp", "required": true, "status": "Match", "streams": 68, "steps": 24300, "diverged": [] },
      {
        "build": "chromium-worker", "required": true, "status": "Diverged", "streams": 68, "steps": 24300,
        "diverged": [
          {
            "stream": "seed-017",
            "step": 212,
            "serverHash": "0c6e1f93a2d7b548",
            "buildHash": "5b2a90e7c13d4f86",
            "differingFields": 1,
            "error": null,
            "report": "report/divergences/chromium-worker/seed-017.txt"
          }
        ]
      },
      { "build": "webkit-worker", "required": false, "status": "NotRun" }
    ]
  }
  ```

- `divergences/<build>/<stream>.txt`, the divergence report for each diverged stream.
- With `--summary <file>`, a Markdown section is appended to that file: a `## SimCore agreement`
  heading, a table with one row per build (build, required, status, streams, steps, first divergence),
  and each diverged build's first divergence report in a code block. CI passes
  `$GITHUB_STEP_SUMMARY`.

### Divergence report

Each report names the stream, the build, the first divergent step and both hashes (16 lowercase hex
digits). It then lists every differing state field by path, decoded through the state layout table,
with the server's and the build's value each as `0x%08X` bits and as a decimal (floats in round-trip
`"R"` form). When a list's counts differ, the count is listed and each element that exists on one
side only shows `absent` on the other. A build that wrote an error file is reported with its message
instead of fields. The exact layout and field paths are in
[state-serialization.md](state-serialization.md#divergence-report). `DivergenceReport` in
`src/MmoEngine.SimCore.Server/` produces it for `agreement compare` and for the tests.

This is what US2 scenario 4 and FR-026 require. `PerturbedRecordsReportTheFirstStepAndField` feeds
records from `StreamRunner.Run(…, perturbStep: k)` to the comparison and asserts the report
names the stream, step *k*, `characters[0].position.x` and both bit patterns (SC-002).

## `measure validate-report`

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- measure validate-report "$REPORT"
```

Checks the measurement report against [measurement-report.md](measurement-report.md#what-validate-report-checks).
Output: one `path: problem` line per problem, in document order; nothing when the report is valid.
Exit 0 valid, 1 invalid (including malformed JSON, reported as `$: is not valid JSON (…)`), 2 when the
file is missing or unreadable.

## `config check`

```bash
dotnet run --project src/MmoEngine.SimCore.Cli -- config check simcore/config/default.json
```

Binds the file with `SimConfigJson` and validates it with `SimConfigValidator`, which applies the core's
rule table ([config-rules.md](config-rules.md#server)). Output: one `path: message` line per error, in
rule-table order; nothing when valid. Exit 0 valid, 1 invalid, 2 when the file is missing.

## Agreement work directory

```text
<work>/
├── manifest.json
├── streams/<stream>.mmrs              committed and generated streams
├── collision/<checksum>.mmcd          every collision file a stream names
├── batch/<stream>/config.bin          the stream's config block
├── batch/<stream>/initial-state.bin   its initial state (canonical, zero events)
├── batch/<stream>/commands.bin        its commands, 17 bytes per step
├── refusals/<name>.mmrs               streams that every build must refuse
├── server/<stream>.records            the server build's step records
├── <build>/<stream>.records           each WebAssembly build's step records
├── <build>/<stream>.error.txt         instead of records, when the build threw
├── <build>/run.json                   the runner's description of the build
└── report/                            agreement.json, divergences/<build>/<stream>.txt
```

A `.records` file is every step's record, concatenated: `u32 step | u32 stateLength | state | u64 hash`,
little-endian ([state-serialization.md](state-serialization.md#step-record)). It is exactly what
`StreamRunner.Run` and the `RunStream` export return ([wasm-api.md](wasm-api.md#streamrunner)).

A stream is batchable when every step carries exactly one command, for character index 0. Its
`batch/` inputs let the runners drive `BatchStepper` through `StepQueued` and compare with
`RunStream` (US2 scenario 6). The measurement uses the same inputs.

`run.json` holds the build name, the runtime options, `runtimeBuildInfo`, the Node version or the
browser's user agent, `inWorker` and `crossOriginIsolated` for Workers, and the start and end times.
`compare` copies it into `agreement.json` when it is present.

### `manifest.json`

```json
{
  "formatVersion": 1,
  "rulesRevision": 1,
  "formats": { "inputCommand": 1, "collisionData": 1, "recordedStream": 1 },
  "seeds": { "first": 1, "last": 50 },
  "streams": [
    {
      "name": "flat-run-30hz",
      "origin": "committed",
      "seed": 0,
      "file": "streams/flat-run-30hz.mmrs",
      "collision": "collision/5be1c0d2a4f39e70.mmcd",
      "collisionChecksum": "5be1c0d2a4f39e70",
      "stepRateHz": 30,
      "steps": 30,
      "characters": 1,
      "serverRecords": "server/flat-run-30hz.records",
      "finalHash": "0c6e1f93a2d7b548",
      "batch": {
        "config": "batch/flat-run-30hz/config.bin",
        "initialState": "batch/flat-run-30hz/initial-state.bin",
        "commands": "batch/flat-run-30hz/commands.bin",
        "matchSalt": "9e3779b97f4a7c15"
      }
    },
    {
      "name": "seed-001",
      "origin": "generated",
      "seed": 1,
      "file": "streams/seed-001.mmrs",
      "collision": "collision/5be1c0d2a4f39e70.mmcd",
      "collisionChecksum": "5be1c0d2a4f39e70",
      "stepRateHz": 30,
      "steps": 300,
      "characters": 3,
      "serverRecords": "server/seed-001.records",
      "finalHash": "a41f6c0e93d27b58",
      "batch": null
    }
  ],
  "perturbation": { "stream": "flat-run-30hz", "step": 15, "field": "characters[0].position.x" },
  "refusals": [
    {
      "name": "unsupported-version",
      "stream": "refusals/unsupported-version.mmrs",
      "collision": "collision/5be1c0d2a4f39e70.mmcd",
      "expected": "unsupported version 2; this reader supports version 1"
    },
    {
      "name": "rules-revision",
      "stream": "refusals/rules-revision.mmrs",
      "collision": "collision/5be1c0d2a4f39e70.mmcd",
      "expected": "rulesRevision at offset 8"
    },
    {
      "name": "collision-missing",
      "stream": "streams/flat-run-30hz.mmrs",
      "collision": "collision/0c4d7a19e2b35f60.mmcd",
      "expected": "collisionChecksum at offset 36"
    }
  ]
}
```

| Field | Rule |
| --- | --- |
| `formatVersion` | 1, the manifest's own version |
| `rulesRevision`, `formats` | the core's `SimCoreVersion.RulesRevision` and format versions when the directory was prepared; `compare` refuses a mismatch as NotComparable |
| `streams[]` | committed streams in name order, then generated streams in seed order; `name` matches `^[a-z0-9-]+$` and is unique |
| `origin`, `seed` | `committed` (seed 0, or the generator seed it was recorded from) or `generated` |
| `file`, `collision`, `serverRecords` and the `batch` paths | relative to the work directory |
| `collisionChecksum`, `finalHash`, `matchSalt` | 16 lowercase hex digits; JavaScript converts with `BigInt('0x' + s)` |
| `steps`, `characters`, `stepRateHz` | from the stream's header |
| `batch` | the batch inputs, or null when the stream is not batchable |
| `perturbation` | the stream, the step number and the field the one-ulp check expects (SC-002); the stream is `flat-run-30hz`, the step its middle step |
| `refusals[]` | each must be refused before step 0 by every build, with an error message containing `expected` (US2 scenario 5, US6 scenarios 2 and 5). `unsupported-version` is `flat-run-30hz` with its version set to 2; `rules-revision` is `flat-run-30hz` with its rules revision raised by one and its trailer rewritten, so it decodes and is refused when opened; `collision-missing` pairs it with a small collision file whose checksum it does not name |

### Build names

| Build | Runs | Job | Default `--require` |
| --- | --- | --- | --- |
| `server` | the server build in the CLI (`agreement prepare`) | both | the reference |
| `node-interp` | Node, interpreted build, prebuilt runtime, jiterpreter on | `agreement` | yes |
| `node-interp-nojit` | Node, interpreted build, prebuilt runtime, jiterpreter off | `agreement` | yes |
| `chromium-worker` | dedicated Worker in headless Chromium | `agreement` | yes |
| `firefox-worker` | dedicated Worker in Firefox | `agreement` | no: `NotRun` when Playwright cannot install it |
| `webkit-worker` | dedicated Worker in WebKit | `agreement` | no: `NotRun` when Playwright cannot install it |
| `node-interp-relinked` | Node, interpreted build relinked with the `wasm-tools` workload | `measure` | passed by the job |
| `node-aot` | Node, ahead-of-time build | `measure` | passed by the job when AOT was produced |

The runners are in [wasm-api.md](wasm-api.md#agreement-runners).

## Generated streams

`StreamGenerator` (`src/MmoEngine.SimCore.Server/`) makes `seed-<nnn>` (three digits) from its seed
alone, using the core's SplitMix64 as a counter-based generator, so the streams are identical on
every run (US2 scenario 3). They are generated at test time and never committed.

- The step rate cycles 20, 30 and 60 Hz by seed. Each stream has 300 steps and two to four
  characters on the test world.
- Every command field is drawn uniformly over its valid range: every button bit from 0 to 8, every
  yaw, every pitch in [−16384, 16384] and every weapon slot. About one command in 64 is made invalid
  on purpose (a stale sequence number, an unknown button bit, a pitch out of range or an unknown slot),
  so the rejection path runs in every build too (US1 scenario 9).
- `GeneratedStreamsCoverEveryButtonAndTheAimRange` asserts that seeds 1–50 together set every button
  bit, reach both pitch limits, cover every 1/16 turn of yaw, and include every rejection reason.

## Named scenario streams

`ScenarioCatalog` (`src/MmoEngine.SimCore.Server/`) defines the committed streams under
`simcore/streams/`. Each runs on `simcore/content/test-world.mmcd` with `simcore/config/default.json`,
unless the row says otherwise. The scenario tests in `tests/MmoEngine.SimCore.Tests/Scenarios/` build
the same definitions and assert each scenario's stated outcome (end positions, shot counts, hit parts);
the agreement suite runs every one of them in every build (US1 scenario 7, US2 scenarios 1 and 2).

| Stream | Configuration and characters | Exercises | Backs |
| --- | --- | --- | --- |
| `flat-run-20hz` | 20 Hz; one character | a forward run on flat terrain for one simulated second | US1.1, US1.8 |
| `flat-run-30hz` | 30 Hz; one character | the same at 30 Hz; also the perturbation stream and the measurement's movement step | US1.1, US1.8, SC-002 |
| `flat-run-60hz` | 60 Hz; one character | the same at 60 Hz | US1.1, US1.8 |
| `wall-slide-45` | one character | walks into the wall at 45 degrees and slides along it | US1.2 |
| `stairs-climb` | one character | walks into the stair whose rise is below the step height and climbs it | US1.3 |
| `stairs-blocked` | one character | walks into the stair whose rise is above the step height and is blocked | US1.3 |
| `slope-limit` | one character | walks up the slope below the limit (climbs, stays grounded), then the one above it (refused) | US1.4 |
| `jump-land` | one character | jumps from flat terrain, follows gravity and lands grounded | US1.5 |
| `crouch-under-ledge` | one character | crouches under the low ledge, releases Crouch, stays crouched until there is room to stand | US1.6 |
| `corner-wedge` | one character | pushes into the concave corner: no tunnelling, no oscillation, the slide loop stops at its limit | Edge cases: concave corner |
| `thin-wall-fast` | one character | moves further than its capsule radius per step at the thin wall: the step is subdivided and never passes through | Edge cases: subdivided step |
| `rifle-burst` | one character, the rifle (600 rpm) | holds Fire for 3 s aimed above the horizon: 30 shots, recoil kick and recovery, shots expire at their range; also the measurement's weapon step | US4.5, US5.4 |
| `launcher-arc` | one character, the launcher | fires the heavy projectile horizontally over flat terrain; the arc meets the terrain at the closed-form step; also the measurement's projectile step | US5.1 |
| `reload-and-empty` | one character, the rifle with 2 rounds in the magazine | fires 2 shots, reports Empty, reloads from reserve after the reload time; switches slot and presses Fire while Switching and Reloading | US4.3, US4.6 |
| `heat-overheat` | one character, the heat test weapon (`heatPerShot` 1000) | fires continuously until it overheats, then resumes only at the recovery level; Fire while Overheated is blocked | US4.4, US4.6 |
| `projectile-hits-hitbox` | two characters: shooter and target | rifle shots at the standing and the crouched target striking head, torso and legs; a projectile that crosses a whole hitbox within one step; a point-blank shot that starts inside a hitbox | US5.2, US5.5, US5.6, Edge cases: point-blank |
| `mesh-occludes-shot` | two characters, the target behind the wall | the shot hits the mesh, not the target | US5.3 |
| `fuzz-all-buttons` | four characters; one seeded fuzz stream, committed | every button, the full aim range and the four rejection reasons, from a fixed seed | US1.9, US2.3 |

That is 18 streams, against SC-001's "at least 10 named movement, weapon and projectile scenarios".
`flat-run-30hz`, `rifle-burst` and `launcher-arc` are batchable, so the batch check and the measurement
can drive them through `StepQueued`.
