# Quickstart: Regionizer Simulator

A guide to running and validating the feature. Every check here is also an automated test. This page
shows how to run them by hand during development.

## Prerequisites

- .NET SDK 9.0.300 or a later 9.0 patch (`global.json`)

## Build and test

```bash
dotnet build MmoEngine.sln -warnaserror
```

```bash
dotnet format MmoEngine.sln --verify-no-changes
```

```bash
dotnet test MmoEngine.sln --collect:"XPlat Code Coverage"
```

Expected: every test passes. The coverage report lands under each test project's `TestResults/`.

## One run

```bash
dotnet run --project src/MmoEngine.Regionizer.Simulator.Cli -- run simulator/scenarios/Baseline.json --out .runs/baseline --ticks 900
```

Expected: exit code 0, and `.runs/baseline/` holds `manifest.json`, `ticks.csv`, `events.csv` and
`summary.json` with no `_incomplete` marker (see [contracts/run-output.md](contracts/run-output.md)).
Running the same command again gives identical `stateHash` columns.

## A sweep

```bash
dotnet run -c Release --project src/MmoEngine.Regionizer.Simulator.Cli -- sweep simulator/sweeps/infantry-radius.json --out .runs/sweeps
```

Expected: one run folder per cell and seed, plus `cells.csv`.

## Portal validator

```bash
dotnet run --project src/MmoEngine.PortalValidator.Cli -- simulator/maps/test/seeded-leaks.json
```

Expected: exit code 1, with exactly the leaks listed in `simulator/maps/test/seeded-leaks.expected.json`.
On `simulator/maps/test/clean.json` the exit code is 0.

## Analytic checks and the memo

```bash
dotnet run -c Release --project src/MmoEngine.Regionizer.Simulator.Cli -- check --out .runs/checks
```

The memo reads every sweep that `simulator/sweeps/memo-rules.json` names. Run each spec in
`simulator/sweeps/` (full scale) or `simulator/sweeps/first-pass/` (9,000 ticks and 5 seeds) into
the same folder first, then generate the memo:

```bash
dotnet run -c Release --project src/MmoEngine.Regionizer.Simulator.Cli -- memo .runs/sweeps --rules simulator/sweeps/memo-rules.json --out .runs/decision-memo.md
```

Expected: `checks.json` shows all three checks within tolerance (exit code 0). The memo has one row
per value, each with its evidence. The committed memo is `docs/simulator/decision-memo.md`.

`tests/MmoEngine.Regionizer.Simulator.Tests/Cli/QuickstartTests.cs` runs these commands at tiny
sizes as an automated check.

## Benchmarks

```bash
dotnet run -c Release --project benchmarks/MmoEngine.Regionizer.Benchmarks -- --filter '*'
```
