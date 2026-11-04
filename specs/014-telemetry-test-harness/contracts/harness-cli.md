# Contract: `harness` CLI

Settings (FR-034):

- `DOTNET_ENVIRONMENT` must be Development, Dev, Test, Staging or Production. Unset means Development.
- The ClickHouse connection string comes from `--clickhouse` or `MMO_CLICKHOUSE`, for example
  `Host=localhost;Port=18123;Username=harness;Password=...`.

An invalid setting exits 2 and names the setting. Logs are Serilog compact JSON on stderr. Results are
one JSON line on stdout.

| Command | Purpose | Exit codes |
| --- | --- | --- |
| `schema apply` | Applies the DDL (idempotent) | 0; 1 store error; 2 invalid settings |
| `load <folder>` | Loads a run folder, or a sweep folder (one with `sweep.json`); idempotent | 0; 1 store error; 2 refused folder or settings |
| `rules run --run-id <id> [--rules harness/rules] [--baselines harness/baselines] [--artifacts <dir>]` | Runs the applicable rules, writes `rule_results`, sets the verdict, and keeps a run that doesn't pass | 0 passed or none; 5 failed; 6 inconclusive; 2 invalid rule, query or run not loaded; 1 store error |
| `rules baselines sync [--baselines harness/baselines]` | Mirrors the committed baselines into the `baselines` table | 0; 1; 2 |
| `golden record --run <folder> --config <file> --out <folder> [--name <name>]` | Records a golden run | 0; 1 I/O error; 2 refused run folder |
| `golden check --golden <folder> --run <folder>` | Compares a replay with a golden run | 0 match; 7 diverged; 8 not comparable; 2 refused input |

Exit codes 5 to 9 don't collide with the simulator's 3 (violation) and 4 (outside tolerance). Exit
code 9 (calibration outside tolerance) is reserved for the network commands in a later slice.
