# Regionizer bookkeeping benchmarks (SC-004, T072)

BenchmarkDotNet 0.15.8, ShortRun job (3 warm-up and 3 measured iterations). Apple M1 Max (10 cores),
macOS 26.5, .NET SDK 9.0.303, runtime 9.0.7 Arm64, Release. Run on 2026-11-03 at commit `7040518`,
with no other simulation running. All numbers are measurements.

```bash
dotnet run -c Release --project benchmarks/MmoEngine.Regionizer.Benchmarks -- --filter '*' --job short
```

The benchmarks (`benchmarks/MmoEngine.Regionizer.Benchmarks/BookkeepingBenchmarks.cs`) drive the
core library directly:

| Benchmark | What it measures |
| --- | --- |
| `MoveWithinSection` | moving one entity without leaving its section |
| `MoveAcrossSectionEdge` | moving one entity across a section edge |
| `TickStartAndEndWithNoMerge` | starting and ending one region's tick with no merge |
| `FullLockstepStep` | one lockstep step of 5,000 entities in seven fights on an 8 km open-world map |

## Results

| Method                     | SectionSize | Anchoring | Mean            | Error          | StdDev        | Allocated |
|--------------------------- |------------ |---------- |----------------:|---------------:|--------------:|----------:|
| MoveWithinSection          | 8           | Section   |        14.35 ns |       2.059 ns |      0.113 ns |         - |
| MoveAcrossSectionEdge      | 8           | Section   |       355.64 ns |     260.635 ns |     14.286 ns |         - |
| TickStartAndEndWithNoMerge | 8           | Section   |        24.93 ns |       1.464 ns |      0.080 ns |         - |
| FullLockstepStep           | 8           | Section   |   235,387.19 ns |  18,885.182 ns |  1,035.161 ns |     306 B |
| MoveWithinSection          | 8           | Position  |       333.66 ns |      58.299 ns |      3.196 ns |         - |
| MoveAcrossSectionEdge      | 8           | Position  |       350.53 ns |      30.777 ns |      1.687 ns |         - |
| TickStartAndEndWithNoMerge | 8           | Position  |        24.89 ns |       1.439 ns |      0.079 ns |         - |
| FullLockstepStep           | 8           | Position  | 1,745,426.03 ns | 316,083.847 ns | 17,325.622 ns |     352 B |
| MoveWithinSection          | 16          | Section   |        14.36 ns |       0.273 ns |      0.015 ns |         - |
| MoveAcrossSectionEdge      | 16          | Section   |       212.55 ns |      28.459 ns |      1.560 ns |         - |
| TickStartAndEndWithNoMerge | 16          | Section   |        25.46 ns |       1.059 ns |      0.058 ns |         - |
| FullLockstepStep           | 16          | Section   |   125,713.39 ns |  16,762.123 ns |    918.789 ns |     332 B |
| MoveWithinSection          | 16          | Position  |       136.46 ns |       6.503 ns |      0.356 ns |         - |
| MoveAcrossSectionEdge      | 16          | Position  |       207.94 ns |      24.019 ns |      1.317 ns |         - |
| TickStartAndEndWithNoMerge | 16          | Position  |        24.96 ns |       3.437 ns |      0.188 ns |         - |
| FullLockstepStep           | 16          | Position  |   899,787.08 ns | 692,156.169 ns | 37,939.415 ns |     221 B |
| MoveWithinSection          | 32          | Section   |        14.47 ns |       2.709 ns |      0.148 ns |         - |
| MoveAcrossSectionEdge      | 32          | Section   |       105.97 ns |       3.056 ns |      0.167 ns |         - |
| TickStartAndEndWithNoMerge | 32          | Section   |        25.47 ns |       0.577 ns |      0.032 ns |         - |
| FullLockstepStep           | 32          | Section   |    93,027.64 ns |  13,130.624 ns |    719.734 ns |     155 B |
| MoveWithinSection          | 32          | Position  |       126.44 ns |       7.570 ns |      0.415 ns |         - |
| MoveAcrossSectionEdge      | 32          | Position  |       132.14 ns |       2.703 ns |      0.148 ns |         - |
| TickStartAndEndWithNoMerge | 32          | Position  |        27.79 ns |       0.362 ns |      0.020 ns |         - |
| FullLockstepStep           | 32          | Position  |   625,720.55 ns |  38,694.478 ns |  2,120.975 ns |     311 B |
| MoveWithinSection          | 64          | Section   |        14.31 ns |       2.029 ns |      0.111 ns |         - |
| MoveAcrossSectionEdge      | 64          | Section   |       102.97 ns |       5.079 ns |      0.278 ns |         - |
| TickStartAndEndWithNoMerge | 64          | Section   |        32.16 ns |       1.276 ns |      0.070 ns |         - |
| FullLockstepStep           | 64          | Section   |    87,223.76 ns |  16,004.231 ns |    877.246 ns |      77 B |
| MoveWithinSection          | 64          | Position  |        92.47 ns |      12.271 ns |      0.673 ns |         - |
| MoveAcrossSectionEdge      | 64          | Position  |        73.96 ns |       1.349 ns |      0.074 ns |         - |
| TickStartAndEndWithNoMerge | 64          | Position  |        25.23 ns |       0.809 ns |      0.044 ns |         - |
| FullLockstepStep           | 64          | Position  |   514,869.25 ns | 101,746.737 ns |  5,577.082 ns |     113 B |

## Reading them against the 1 ms budget

- The steady state is within budget. A full 5,000-entity step with section-anchored claims
  averages 0.09–0.24 ms, depending on section size. Position-anchored claims recompute on every move
  and cost 0.5–1.75 ms per step; at 8 m sections that is over budget on average.
- The tail is not. The simulator's per-tick bookkeeping p99 at 5,000 entities
  (`docs/simulator/decision-memo.md`, section-size row) is 1.1–3.2 ms. That tail comes from ticks
  with merges, splits, transient regions and teleports, plus the per-call measurement and the
  observers' O(1) queueing. The memo's section-size row is flagged for this reason. The next
  decision is either to revise the budget, as the issue planned after the first measurement, or to
  optimise the merge and split paths.
- Allocation. Single moves and tick start or end allocate nothing (FR-028; `ZeroAllocationTests`
  covers the steady state). `FullLockstepStep` averages 77–352 B per step. Those bytes come from the
  steps in which a region is created, merged or split, which FR-028 does not cover.
