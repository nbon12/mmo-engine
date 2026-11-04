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
| `profile list [--profiles harness/profiles]` | Lists the catalogue, including the reordering variants | 0; 2 invalid profile file |
| `profile netem --profile <name> [--device lo] [--seed 1] [--tcp-ingress]` | Prints a `tc` netem script | 0; 2 |
| `profile chaos --profile <name> [--namespace harness] [--label k=v ...]` | Prints a Chaos Mesh NetworkChaos | 0; 2 |
| `probe serve [--bind 127.0.0.1:7400] [--seconds N]` | Runs the probe server | 0; 2 bad bind |
| `probe run --target host:port --profile <name> [--seconds 300] [--rate 30] [--run-id <id>]` | Probes, writes `net_samples` and a `probe` run row, and calibrates | 0 within tolerance; 9 outside; 1 store error; 2 |
| `calibrate --run-id <id> --profile <name> [--write-calibrated]` | Calibrates a stored probe run; when it passes, `--write-calibrated` stores the settings in `<profiles>/<name lowercased>.json` | 0; 9; 1; 2 no samples or bad profile |
| `stub-target [--bind 127.0.0.1:7500] [--seconds N]` | Runs the stub target | 0; 2 |
| `bots run --target host:port [--bots 4] [--seconds 10] [--run-id <id>] [--profile none]` | Runs a bot host, writing `bot_frames` and a `bot` run row; then run `rules run` | 0; 1; 2 |

Exit codes 5 to 9 don't collide with the simulator's 3 (violation) and 4 (outside tolerance).
