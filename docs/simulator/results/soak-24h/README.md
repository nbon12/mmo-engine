# 24-hour simulated soak (SC-002, T071)

The recorded run behind SC-002: "a 24-hour simulated Soak run in interleaved mode ends with zero
invariant violations, and every synthetic timer fires exactly once, when FR-012 says."

| | |
| --- | --- |
| Run | `soak-s16-r16-seed1-2dbfb8ef` ([manifest](manifest.json), [summary](summary.json)) |
| Commit | `9bdb673` |
| Scenario | Soak: Migration, Stacked areas and Air traffic, air layer on |
| Scheduling | Interleaved, 60 Hz base step, 30 Hz ticks, 20 Hz degraded ticks |
| Population | 1,000 players (`population.total`), seed 1 |
| Length | 2,592,000 ticks = 24 simulated hours |
| Machine | the one named in the manifest (Apple M1 Max, macOS, .NET 9, Release build) |
| Wall clock | 17,456 s (4.85 h), measured, with other simulations running alongside for part of it |

## Result

| Check | Result |
| --- | --- |
| Invariant violations (rules 1–6, checked at every base step) | **0** |
| Timer faults (shadow monitor) | **0** |
| Timers set / fired | 899,489 / 899,336. The 153 that did not fire were not yet due when the run ended. |
| Merges / absorptions / splits | 1,224,953 / 50,689,731 / 799,688 |
| Fusions | 28,399 |
| Final state hash | `d6f011575810ee56` |

The full `ticks.csv` (8.7 MB) and `events.csv` (156 MB) stay local. Rerunning the manifest's
configuration on the same build and machine reproduces them, with the same per-tick hashes (FR-019).

## Why 1,000 players

The scenario's default population is 5,900. At the measured 6.5 ms per tick at 1,000 players, a
24-hour run already takes about 4.9 hours, and the default population would take several times
longer. The soak tests churn and timer correctness under long runs (migration, doors, aircraft),
not scale, so 1,000 players keeps it practical. Rerunning it at the default population is listed
as a follow-up.
