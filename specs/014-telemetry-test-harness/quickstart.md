# Quickstart: Telemetry and Test Harness

Every check here is also an automated test. This page lists the commands those tests run.

## Prerequisites

- .NET SDK 10.0.100 or a later 10.0.1xx patch (`global.json`).
- Docker, for the store tests (Testcontainers starts `clickhouse/clickhouse-server:25.8`).

## Build and test

```bash
dotnet build MmoEngine.sln -warnaserror
dotnet test tests/MmoEngine.Harness.Tests
```

## A ClickHouse to point the tools at

Without the cluster, use a throwaway container:

```bash
docker run -d --name harness-clickhouse -p 18123:8123 \
  -e CLICKHOUSE_USER=harness -e CLICKHOUSE_PASSWORD=harness clickhouse/clickhouse-server:25.8
export MMO_CLICKHOUSE="Host=localhost;Port=18123;Username=harness;Password=harness"
```

## Load a run and gate it with the rules

```bash
dotnet run --project src/MmoEngine.Regionizer.Simulator.Cli -- run simulator/scenarios/Baseline.json --ticks 300 --out out/baseline
dotnet run --project src/MmoEngine.Harness.Cli -- schema apply
dotnet run --project src/MmoEngine.Harness.Cli -- load out/baseline
dotnet run --project src/MmoEngine.Harness.Cli -- rules run --run-id <runId from the load output> --artifacts out/artifacts
echo $?   # 0 passed, 5 failed, 6 inconclusive
```

## Load the decision memo's sweep results

```bash
for sweep in docs/simulator/results/first-pass/*/; do
  dotnet run --project src/MmoEngine.Harness.Cli -- load "$sweep"
done
```

## Golden runs

```bash
dotnet run --project src/MmoEngine.Regionizer.Simulator.Cli -- run harness/golden/<name>/config.json --out out/replay
dotnet run --project src/MmoEngine.Harness.Cli -- golden check --golden harness/golden/<name> --run out/replay
echo $?   # 0 match, 7 diverged, 8 not comparable
```

## The replay viewer

```bash
dotnet run --project src/MmoEngine.Regionizer.Simulator.Cli -- view out/baseline --tick 120 --out out/tick-120.svg [--layer 0]
```
