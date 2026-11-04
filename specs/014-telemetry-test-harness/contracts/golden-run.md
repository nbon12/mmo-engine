# Contract: Golden runs (format version 1)

A golden run is a folder, `harness/golden/<name>/`:

```text
config.json    # the run config, as given to `regionizer-sim run`
golden.json    # identity and runner class
ticks.csv      # the recorded ticks.csv without its wall-clock columns (bookkeepingMicros, serializedMicros)
```

```json
{
  "formatVersion": 1,
  "name": "baseline-small",
  "runFormatVersion": 1,
  "sourceRunId": "baseline-s16-r16-seed11-1a2b3c4d",
  "configHash": "<16 hex>",
  "gitSha": "<40 hex>",
  "ticks": 40,
  "runnerClass": { "id": "<name->16 hex>", "fields": { "architecture": "X64", "cpuModel": "...", "dotnetMajor": "10", "intrinsics": {} } }
}
```

## Check

`harness golden check --golden <folder> --run <replayed run folder>`:

| Result | Exit | Report |
| --- | --- | --- |
| Every row's `tick` and `stateHash` match | 0 | "all N ticks match on runner class X" |
| A row differs, or one side has more rows | 7 | the first divergent tick, both hashes, and both rows' deterministic columns |
| Another golden format version, run format version, or runner class | 8 | "not comparable", naming both classes or versions |

The runner class ID is defined in research R5. Recording is `harness golden record`, and its output
is committed. CI never records into `harness/golden/` (FR-020). The `golden` job records a transient
reference in a temporary folder, so a second run on the same runner can be compared with it.
