# Regionizer decision memo

> **Scope of this memo: full scale.** This memo was generated on 2026-11-06 from every sweep in
> `simulator/sweeps/` the memo's rules read, at the issue's plan: 18,000 ticks (10 simulated minutes) and 10 seeds per cell,
> 840 runs. They ran on GitHub-hosted `ubuntu-latest` runners (4 vCPU, .NET 10, Release) with the Sweeps
> workflow: run 42141270969 for 12 sweeps, and run 42178394391 for split-damping in 4 shards, merged with
> `infra/sweeps/merge-shards.py`. The index and table of each sweep are in `docs/simulator/results/full/`.
> The per-run manifests and summaries the memo also reads are on the `sweep-results/<run>-1/<sweep>`
> branches (split-damping: its four `-shard-<k>-of-4` branches). Every number below is a measurement, and nothing here settles an open decision. The
> first-pass memo (9,000 ticks, 5 seeds, one Apple M1 Max) is in git history; every value it recommended
> holds here except claim anchoring, below.
>
> What to read before using it:
>
> - **Claim radii come out at the top of each one-at-a-time sweep.** On the default map, swept one class
>   at a time, no radius up to 40 m (infantry), 48 m (ground vehicles) or 32 m (aircraft) fuses two fights
>   at the median. With all three at those values together (`baseline-final`), the p99 of the largest
>   region rises from about 483 players at the defaults to about 840. The grid below shows that this
>   rise comes from split damping, which `baseline-final` changes as well, not from the radii.
> - **Section size is still flagged.** No size keeps the regionizer's bookkeeping p99 within the proposed
>   1 ms budget at 5,000 entities in every seed of both scenarios. Baseline measures 1.09–3.14 ms (the
>   best, 64 m, has a worst seed of 1.21 ms). One base measures 0.26–4.21 ms, and is within budget at
>   32 m and 64 m. These readings are lockstep and single-threaded. The issue proposed the budget "revised
>   after the first measurement", so this is the measurement for that owner decision (and for optimising
>   the bookkeeping). The section size stays at its 16 m default meanwhile. Lockstep readings exclude
>   merge-later work (research D7).
> - **Claim anchoring now reads Position, by a hair.** Across 10 seeds, Position anchoring fused no fights
>   and Section anchoring fused 2 (0.0003 fusions per second against 0). The first pass, with 5 seeds,
>   measured 0 for both and read Section. A difference of two fusion events in 100 simulated minutes is
>   at the edge of what the sweep can tell apart, so treat the two modes as tied on fusions. Read the
>   bookkeeping cost before choosing. Anchoring was compared at the same section size, not at equal
>   bookkeeping cost (research D15).
> - **Lane bridging limit: 5 travellers on the 600 m lane.** 0, 2 and 5 travellers never fused the two
>   fights. 10 fused them on 0.8% of ticks on average (2.4% in the worst seed), and 20 on 7.5% (12.7%).
>   That is the steep jump the issue's estimate predicts, and why lane traffic needs a design limit rather
>   than a tuning knob.
> - **Split damping: p_dead = 50%.** That cell has the lowest merge-plus-split rate (30.9 per second
>   against 38.4 for 40%) among the cells whose split latency p99 stays under 5 s in every seed. One of
>   the 16 cells fails that latency bound. That rule reads churn in Migration only. The grid below shows
>   what p_dead = 50% costs in Baseline.
> - **The grid near the chosen values (`claim-radius-grid`).** This is a 14th sweep, run after the memo's
>   13, so the memo's rules don't read it. It ran on Baseline at 2,000 players: 16 cells, 10 seeds, 160
>   runs in 5 shards (Sweeps run 42199973215). It crossed each class's claim radius at its default and
>   its recommended value (infantry 16 and 40 m, ground vehicle 24 and 48 m, aircraft 16 and 32 m) with
>   split p_dead at 25% (the default) and 50% (recommended). Results:
>   - **Split damping sets the largest region, and the radii barely move it.** p_dead = 50% lifts the p99
>     of the largest region from 486.6 players (mean of the 8 cells at 25%, each seed 481 to 494) to
>     844.1 (each seed 827 to 866). It lifts fusion events from 0.5–1.8 per run to 35.9–63.1, and cuts
>     the median region count from 152–171 to 112–125. Between their two values, the three radii together
>     change the largest region by at most 1.6% (at 50%: 835.5 to 855.5).
>   - **No cell fused two fights at the median** (fusedFightPairs.p50 is 0 in every run). At 50%, the
>     extra fusions are fights that touched briefly and stayed one region because the region waited for
>     half its sections to die before splitting.
>   - **So the target fight size reading (839.6) is a reading at p_dead = 50%.** At the default 25% it
>     would be about 487. Split damping trades churn in moving fights (Migration) against how long
>     fights that touched stay fused (Baseline). The memo's split-damping rule reads only the first.
>     Both p_dead values and their readings are recorded here. The configuration still holds the default
>     (25%); the Phase 1 sweep, which measures tick cost against fight size, is where the two meet.
>   - Each cell's `sweep.json` and `cells.csv` are in `docs/simulator/results/full/claim-radius-grid/`.

Generated by `regionizer-sim memo` from the sweep results in `sweeps-full` (the full-scale results above), read with the rules of research D15. Every recommended value is a measurement read off the curve shown with it (x, and the mean, min and max over seeds), with its margin, seeds and run IDs, at the population its row names, which every run's manifest confirms. Values for open decision 1 are recommendations recorded under Principle VI and stay in configuration. The target fight size is a reading for the Phase 1 sweep; open decision 2 is set by load tests. Merge rates count real merges, not absorptions. The measured options at the end are reported side by side, with no recommendation.

All 10 values and 2 measured options were read.

| Value | Population | Recommended | Label | Status | Margin |
| --- | --- | --- | --- | --- | --- |
| Claim radius, infantry | 2000 | 40 m (measurement) | Recommendation, recorded under Principle VI (open decision 1) | Recommended | no collapse up to 40 m, the top of the sweep; 29.5333 m above the analytic floor 10.4667 m (10 m range + 14 m/s closing at 30 Hz) |
| Claim radius, ground vehicle | 2000 | 48 m (measurement) | Recommendation, recorded under Principle VI (open decision 1) | Recommended | no collapse up to 48 m, the top of the sweep; 31 m above the analytic floor 17 m (15 m range + 60 m/s closing at 30 Hz) |
| Claim radius, aircraft | 2000 | 32 m (measurement) | Recommendation, recorded under Principle VI (open decision 1) | Recommended | no collapse up to 32 m, the top of the sweep; 16 m above the analytic floor 16 m (8 m range + 240 m/s closing at 30 Hz) |
| Section size | 5000 | none | Recommendation, recorded under Principle VI (open decision 1) | **FLAGGED** | no swept value has budget.bookkeepingP99Micros ≤ 1000 in every seed; the best worst seed is 1214.2532 at 64 m |
| Claim anchoring | 2000 | Position (measurement) | Recommendation | Recommended | mean 0 against 0.0003 for Section (100% lower) |
| Minimum base spacing | 2000 | 1000 m (measurement) | Recommendation | Recommended | worst seed totals.oversizedFusionTicks / ticks 0 < 0.01 at 900 m; 850 m measures 0.0192 (fails); read 900 m, plus a margin of 100 m |
| Lane bridging limit | 2000 | 5 travellers (measurement) | Recommendation | Recommended | worst seed totals.oversizedFusionTicks / ticks 0 < 0.01 at 5 travellers; 10 travellers measures 0.0237 (fails) |
| Split damping | 2000 | regionizer.split.minDeadPercent=50 (measurement) | Recommendation | Recommended | mean 30.8652 against 38.4383 for regionizer.split.minDeadPercent=40 (19.7% lower); worst seed metrics.splitLatencyTicks.p99 115 < 150 (1 of 16 cells fail it) |
| Air layer | 2000 | do not adopt: airLayer.enabled = false (measurement) | Recommendation | Recommended | rates.mergesPerSecondByClass.Aircraft 25.7955 → 22.1005, a 14.3% drop (needs ≥ 25%); rates.fusionsPerSecondByClass 0.0003 → 0 |
| Target fight size | 2000 | 839.6 players (measurement) | Reading (open decision 2 is set by load tests) | Read | a reading, not a threshold: the mean over 10 seeds, which range 825 to 849 players |

## Claim radius, infantry

- **Recommended**: 40 m (measurement)
- **Label**: Recommendation, recorded under Principle VI (open decision 1)
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Baseline at 2,000 players with the infantry claim radius swept. Collapse is the smallest radius whose mean over seeds of the p50 fused-fight count is at least 1. The recommendation is the largest radius at least one sweep step below collapse and at or above the analytic floor; if the floor is above it, the row is flagged as a conflict. The region-count and largest-share curves are recorded next to it.
- **Read from**: sweep `infantry-radius` (Baseline); metric `fusedFightPairs.p50` from each run's summary.json; x `regionizer.claimRadius.infantry`
- **Margin**: no collapse up to 40 m, the top of the sweep; 29.5333 m above the analytic floor 10.4667 m (10 m range + 14 m/s closing at 30 Hz)
- **Principle VI**: A recommendation for open decision 1 (section size and claim radius per class), recorded under Principle VI with its curve, seeds and runs. The value stays in configuration.
- **Region count**: Region count does not collapse in the swept range: no value from 8 to 40 m has mean fusedFightPairs.p50 ≥ 1; at 8 m the mean metrics.regionCount.p50 is 172.4 and the mean metrics.largestRegionShare.p50 is 0.2344, and at 40 m the mean metrics.regionCount.p50 is 171.1 and the mean metrics.largestRegionShare.p50 is 0.2345.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `baseline-s16-r8-seed5843011988881251843-802b6517`, `baseline-s16-r8-seed7620229681788273117-97e06796`, `baseline-s16-r8-seed3728128964537905102-d9e4f7ce`, `baseline-s16-r8-seed1906953110644082044-3affca76`, `baseline-s16-r8-seed17502585706050438786-5e6a2fe3`, `baseline-s16-r8-seed4965958803928893316-16911bc4`, `baseline-s16-r8-seed14666344033508591874-fcf20fe4`, `baseline-s16-r8-seed8128550842455080494-b56d682b`, `baseline-s16-r8-seed17568711540103687888-68a28777`, `baseline-s16-r8-seed180498484486306318-259409b5`, `baseline-s16-r12-seed5843011988881251843-f5fd78c2`, `baseline-s16-r12-seed7620229681788273117-81b40508`, `baseline-s16-r12-seed3728128964537905102-441cee05`, `baseline-s16-r12-seed1906953110644082044-cc1c1b4f`, `baseline-s16-r12-seed17502585706050438786-e9bfb74f`, `baseline-s16-r12-seed4965958803928893316-14ee958c`, `baseline-s16-r12-seed14666344033508591874-cd614469`, `baseline-s16-r12-seed8128550842455080494-74e81bd5`, `baseline-s16-r12-seed17568711540103687888-b089aebb`, `baseline-s16-r12-seed180498484486306318-0553b823`, `baseline-s16-r16-seed5843011988881251843-1d867f75`, `baseline-s16-r16-seed7620229681788273117-b91679ab`, `baseline-s16-r16-seed3728128964537905102-771c30b8`, `baseline-s16-r16-seed1906953110644082044-26bb0ce8`, `baseline-s16-r16-seed17502585706050438786-83ae4bf0`, `baseline-s16-r16-seed4965958803928893316-51a3b43b`, `baseline-s16-r16-seed14666344033508591874-cd55c78d`, `baseline-s16-r16-seed8128550842455080494-58b945ee`, `baseline-s16-r16-seed17568711540103687888-55a93f45`, `baseline-s16-r16-seed180498484486306318-e12b02f0`, `baseline-s16-r20-seed5843011988881251843-d1bad893`, `baseline-s16-r20-seed7620229681788273117-398e921f`, `baseline-s16-r20-seed3728128964537905102-79e8273c`, `baseline-s16-r20-seed1906953110644082044-cd2868dc`, `baseline-s16-r20-seed17502585706050438786-c183216a`, `baseline-s16-r20-seed4965958803928893316-5f9fb352`, `baseline-s16-r20-seed14666344033508591874-1b762b87`, `baseline-s16-r20-seed8128550842455080494-456434e1`, `baseline-s16-r20-seed17568711540103687888-0cf8816a`, `baseline-s16-r20-seed180498484486306318-9a4065f2`, `baseline-s16-r24-seed5843011988881251843-a6d22b39`, `baseline-s16-r24-seed7620229681788273117-ff63c497`, `baseline-s16-r24-seed3728128964537905102-14d2fb9d`, `baseline-s16-r24-seed1906953110644082044-f7d29366`, `baseline-s16-r24-seed17502585706050438786-eed8fb42`, `baseline-s16-r24-seed4965958803928893316-56be1eb4`, `baseline-s16-r24-seed14666344033508591874-3a49885b`, `baseline-s16-r24-seed8128550842455080494-d15ec745`, `baseline-s16-r24-seed17568711540103687888-35b26382`, `baseline-s16-r24-seed180498484486306318-9e17342b`, `baseline-s16-r28-seed5843011988881251843-0277b1af`, `baseline-s16-r28-seed7620229681788273117-bd8ae76d`, `baseline-s16-r28-seed3728128964537905102-22beb6a6`, `baseline-s16-r28-seed1906953110644082044-de1bd33c`, `baseline-s16-r28-seed17502585706050438786-638a58aa`, `baseline-s16-r28-seed4965958803928893316-821beb5b`, `baseline-s16-r28-seed14666344033508591874-2fe1793c`, `baseline-s16-r28-seed8128550842455080494-a485e5d4`, `baseline-s16-r28-seed17568711540103687888-4e305442`, `baseline-s16-r28-seed180498484486306318-6ee44f92`, `baseline-s16-r32-seed5843011988881251843-cfd4041b`, `baseline-s16-r32-seed7620229681788273117-4801a1b2`, `baseline-s16-r32-seed3728128964537905102-27e9e132`, `baseline-s16-r32-seed1906953110644082044-de1365ae`, `baseline-s16-r32-seed17502585706050438786-e8d400e1`, `baseline-s16-r32-seed4965958803928893316-c4d419c7`, `baseline-s16-r32-seed14666344033508591874-17b4dd90`, `baseline-s16-r32-seed8128550842455080494-db3366f3`, `baseline-s16-r32-seed17568711540103687888-743227d8`, `baseline-s16-r32-seed180498484486306318-321acf71`, `baseline-s16-r36-seed5843011988881251843-0e268f20`, `baseline-s16-r36-seed7620229681788273117-e9a1ad28`, `baseline-s16-r36-seed3728128964537905102-cbbb7402`, `baseline-s16-r36-seed1906953110644082044-a5e586c4`, `baseline-s16-r36-seed17502585706050438786-7e780529`, `baseline-s16-r36-seed4965958803928893316-bf629fa6`, `baseline-s16-r36-seed14666344033508591874-e1e63852`, `baseline-s16-r36-seed8128550842455080494-af6158c2`, `baseline-s16-r36-seed17568711540103687888-638620a8`, `baseline-s16-r36-seed180498484486306318-31140f1c`, `baseline-s16-r40-seed5843011988881251843-52433f1a`, `baseline-s16-r40-seed7620229681788273117-472a1ade`, `baseline-s16-r40-seed3728128964537905102-0580b346`, `baseline-s16-r40-seed1906953110644082044-673acaa0`, `baseline-s16-r40-seed17502585706050438786-5ad8378a`, `baseline-s16-r40-seed4965958803928893316-6f8ea75f`, `baseline-s16-r40-seed14666344033508591874-676954a2`, `baseline-s16-r40-seed8128550842455080494-7e1b9eef`, `baseline-s16-r40-seed17568711540103687888-d5174b49`, `baseline-s16-r40-seed180498484486306318-dd7bb893`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| 8 | 0 | 0 | 0 | 10 |
| 12 | 0 | 0 | 0 | 10 |
| 16 | 0 | 0 | 0 | 10 |
| 20 | 0 | 0 | 0 | 10 |
| 24 | 0 | 0 | 0 | 10 |
| 28 | 0 | 0 | 0 | 10 |
| 32 | 0 | 0 | 0 | 10 |
| 36 | 0 | 0 | 0 | 10 |
| 40 | 0 | 0 | 0 | 10 |

Constitution reading: the fused-fight-pairs, region-count and largest-share curves the constitution's Validation program reads, each the mean over seeds with the min and max in brackets.

| x | Fused fight pairs | Region count | Largest share |
| --- | --- | --- | --- |
| 8 | 0 (0 to 0) | 172.4 (170 to 174) | 0.2344 (0.234 to 0.235) |
| 12 | 0 (0 to 0) | 172.4 (170 to 174) | 0.2344 (0.234 to 0.235) |
| 16 | 0 (0 to 0) | 172.2 (169 to 174) | 0.2345 (0.234 to 0.235) |
| 20 | 0 (0 to 0) | 172.2 (169 to 174) | 0.2345 (0.234 to 0.235) |
| 24 | 0 (0 to 0) | 172.1 (169 to 174) | 0.2345 (0.234 to 0.235) |
| 28 | 0 (0 to 0) | 172.1 (169 to 174) | 0.2345 (0.234 to 0.235) |
| 32 | 0 (0 to 0) | 171.4 (169 to 173) | 0.2345 (0.234 to 0.235) |
| 36 | 0 (0 to 0) | 171.1 (168 to 173) | 0.2345 (0.234 to 0.235) |
| 40 | 0 (0 to 0) | 171.1 (168 to 173) | 0.2345 (0.234 to 0.235) |

## Claim radius, ground vehicle

- **Recommended**: 48 m (measurement)
- **Label**: Recommendation, recorded under Principle VI (open decision 1)
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Baseline at 2,000 players with the ground-vehicle claim radius swept. Collapse is the smallest radius whose mean over seeds of the p50 fused-fight count is at least 1. The recommendation is the largest radius at least one sweep step below collapse and at or above the analytic floor; if the floor is above it, the row is flagged as a conflict. The region-count and largest-share curves are recorded next to it.
- **Read from**: sweep `vehicle-radius` (Baseline); metric `fusedFightPairs.p50` from each run's summary.json; x `regionizer.claimRadius.groundVehicle`
- **Margin**: no collapse up to 48 m, the top of the sweep; 31 m above the analytic floor 17 m (15 m range + 60 m/s closing at 30 Hz)
- **Principle VI**: A recommendation for open decision 1 (section size and claim radius per class), recorded under Principle VI with its curve, seeds and runs. The value stays in configuration.
- **Region count**: Region count does not collapse in the swept range: no value from 8 to 48 m has mean fusedFightPairs.p50 ≥ 1; at 8 m the mean metrics.regionCount.p50 is 198.5 and the mean metrics.largestRegionShare.p50 is 0.23, and at 48 m the mean metrics.regionCount.p50 is 161.4 and the mean metrics.largestRegionShare.p50 is 0.2359.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `baseline-s16-r16-seed5843011988881251843-d812f0e4`, `baseline-s16-r16-seed7620229681788273117-db360f91`, `baseline-s16-r16-seed3728128964537905102-f1d920fe`, `baseline-s16-r16-seed1906953110644082044-84360b89`, `baseline-s16-r16-seed17502585706050438786-b2bb950c`, `baseline-s16-r16-seed4965958803928893316-68945d86`, `baseline-s16-r16-seed14666344033508591874-7dacc816`, `baseline-s16-r16-seed8128550842455080494-b06cf2fb`, `baseline-s16-r16-seed17568711540103687888-badfafdd`, `baseline-s16-r16-seed180498484486306318-5488a49a`, `baseline-s16-r16-seed5843011988881251843-ef4488da`, `baseline-s16-r16-seed7620229681788273117-95d64c9e`, `baseline-s16-r16-seed3728128964537905102-66b8f3c6`, `baseline-s16-r16-seed1906953110644082044-4f047b3b`, `baseline-s16-r16-seed17502585706050438786-8e9c8a8f`, `baseline-s16-r16-seed4965958803928893316-80611dea`, `baseline-s16-r16-seed14666344033508591874-0ff45c7d`, `baseline-s16-r16-seed8128550842455080494-86753630`, `baseline-s16-r16-seed17568711540103687888-d39beca7`, `baseline-s16-r16-seed180498484486306318-8b98b4d8`, `baseline-s16-r16-seed5843011988881251843-1d867f75`, `baseline-s16-r16-seed7620229681788273117-b91679ab`, `baseline-s16-r16-seed3728128964537905102-771c30b8`, `baseline-s16-r16-seed1906953110644082044-26bb0ce8`, `baseline-s16-r16-seed17502585706050438786-83ae4bf0`, `baseline-s16-r16-seed4965958803928893316-51a3b43b`, `baseline-s16-r16-seed14666344033508591874-cd55c78d`, `baseline-s16-r16-seed8128550842455080494-58b945ee`, `baseline-s16-r16-seed17568711540103687888-55a93f45`, `baseline-s16-r16-seed180498484486306318-e12b02f0`, `baseline-s16-r16-seed5843011988881251843-dd332090`, `baseline-s16-r16-seed7620229681788273117-86c545af`, `baseline-s16-r16-seed3728128964537905102-4adddcf2`, `baseline-s16-r16-seed1906953110644082044-6dff691a`, `baseline-s16-r16-seed17502585706050438786-94496922`, `baseline-s16-r16-seed4965958803928893316-c828776c`, `baseline-s16-r16-seed14666344033508591874-5114060c`, `baseline-s16-r16-seed8128550842455080494-61c39c85`, `baseline-s16-r16-seed17568711540103687888-93394f86`, `baseline-s16-r16-seed180498484486306318-1802fb1e`, `baseline-s16-r16-seed5843011988881251843-f74caa15`, `baseline-s16-r16-seed7620229681788273117-3d0f58dc`, `baseline-s16-r16-seed3728128964537905102-d7de6f37`, `baseline-s16-r16-seed1906953110644082044-183260c2`, `baseline-s16-r16-seed17502585706050438786-2aeed996`, `baseline-s16-r16-seed4965958803928893316-025dddde`, `baseline-s16-r16-seed14666344033508591874-4be3c058`, `baseline-s16-r16-seed8128550842455080494-8a117015`, `baseline-s16-r16-seed17568711540103687888-1eddb757`, `baseline-s16-r16-seed180498484486306318-7bdc7056`, `baseline-s16-r16-seed5843011988881251843-4617b8d7`, `baseline-s16-r16-seed7620229681788273117-2b2bbd82`, `baseline-s16-r16-seed3728128964537905102-2bbeb234`, `baseline-s16-r16-seed1906953110644082044-e5debbc8`, `baseline-s16-r16-seed17502585706050438786-eb764f28`, `baseline-s16-r16-seed4965958803928893316-1d574687`, `baseline-s16-r16-seed14666344033508591874-203fbcf8`, `baseline-s16-r16-seed8128550842455080494-c782341f`, `baseline-s16-r16-seed17568711540103687888-c383cba0`, `baseline-s16-r16-seed180498484486306318-d6b4c025`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| 8 | 0 | 0 | 0 | 10 |
| 16 | 0 | 0 | 0 | 10 |
| 24 | 0 | 0 | 0 | 10 |
| 32 | 0 | 0 | 0 | 10 |
| 40 | 0 | 0 | 0 | 10 |
| 48 | 0 | 0 | 0 | 10 |

Constitution reading: the fused-fight-pairs, region-count and largest-share curves the constitution's Validation program reads, each the mean over seeds with the min and max in brackets.

| x | Fused fight pairs | Region count | Largest share |
| --- | --- | --- | --- |
| 8 | 0 (0 to 0) | 198.5 (195 to 200) | 0.23 (0.2291 to 0.2305) |
| 16 | 0 (0 to 0) | 175.1 (172 to 177) | 0.234 (0.2335 to 0.2345) |
| 24 | 0 (0 to 0) | 172.2 (169 to 174) | 0.2345 (0.234 to 0.235) |
| 32 | 0 (0 to 0) | 166.5 (164 to 169) | 0.2353 (0.2345 to 0.236) |
| 40 | 0 (0 to 0) | 164.4 (162 to 166) | 0.2354 (0.235 to 0.236) |
| 48 | 0 (0 to 0) | 161.4 (159 to 163) | 0.2359 (0.2351 to 0.2365) |

## Claim radius, aircraft

- **Recommended**: 32 m (measurement)
- **Label**: Recommendation, recorded under Principle VI (open decision 1)
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Baseline at 2,000 players with the aircraft claim radius swept. Collapse is the smallest radius whose mean over seeds of the p50 fused-fight count is at least 1. The recommendation is the largest radius at least one sweep step below collapse and at or above the analytic floor (two aircraft closing at 240 m/s at 30 Hz: the range plus 8 m); if the floor is above it, the row is flagged as a conflict. The region-count and largest-share curves are recorded next to it.
- **Read from**: sweep `aircraft-radius` (Baseline); metric `fusedFightPairs.p50` from each run's summary.json; x `regionizer.claimRadius.aircraft`
- **Margin**: no collapse up to 32 m, the top of the sweep; 16 m above the analytic floor 16 m (8 m range + 240 m/s closing at 30 Hz)
- **Principle VI**: A recommendation for open decision 1 (section size and claim radius per class), recorded under Principle VI with its curve, seeds and runs. The value stays in configuration.
- **Region count**: Region count does not collapse in the swept range: no value from 8 to 32 m has mean fusedFightPairs.p50 ≥ 1; at 8 m the mean metrics.regionCount.p50 is 179.7 and the mean metrics.largestRegionShare.p50 is 0.2341, and at 32 m the mean metrics.regionCount.p50 is 163.5 and the mean metrics.largestRegionShare.p50 is 0.2349.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `baseline-s16-r16-seed5843011988881251843-484d8b41`, `baseline-s16-r16-seed7620229681788273117-b53ac239`, `baseline-s16-r16-seed3728128964537905102-8e9faffe`, `baseline-s16-r16-seed1906953110644082044-e2dc7ab2`, `baseline-s16-r16-seed17502585706050438786-6e224c06`, `baseline-s16-r16-seed4965958803928893316-702a31ac`, `baseline-s16-r16-seed14666344033508591874-ae8294b4`, `baseline-s16-r16-seed8128550842455080494-5eea8fd0`, `baseline-s16-r16-seed17568711540103687888-40412c5d`, `baseline-s16-r16-seed180498484486306318-f9d16107`, `baseline-s16-r16-seed5843011988881251843-1d867f75`, `baseline-s16-r16-seed7620229681788273117-b91679ab`, `baseline-s16-r16-seed3728128964537905102-771c30b8`, `baseline-s16-r16-seed1906953110644082044-26bb0ce8`, `baseline-s16-r16-seed17502585706050438786-83ae4bf0`, `baseline-s16-r16-seed4965958803928893316-51a3b43b`, `baseline-s16-r16-seed14666344033508591874-cd55c78d`, `baseline-s16-r16-seed8128550842455080494-58b945ee`, `baseline-s16-r16-seed17568711540103687888-55a93f45`, `baseline-s16-r16-seed180498484486306318-e12b02f0`, `baseline-s16-r16-seed5843011988881251843-2ed8822f`, `baseline-s16-r16-seed7620229681788273117-11d42ae7`, `baseline-s16-r16-seed3728128964537905102-3816550d`, `baseline-s16-r16-seed1906953110644082044-169eddea`, `baseline-s16-r16-seed17502585706050438786-fa683ca7`, `baseline-s16-r16-seed4965958803928893316-4ff7c664`, `baseline-s16-r16-seed14666344033508591874-1a10f481`, `baseline-s16-r16-seed8128550842455080494-5e8cdc61`, `baseline-s16-r16-seed17568711540103687888-3e33d599`, `baseline-s16-r16-seed180498484486306318-77e29826`, `baseline-s16-r16-seed5843011988881251843-bd3ed6a5`, `baseline-s16-r16-seed7620229681788273117-f6a8c208`, `baseline-s16-r16-seed3728128964537905102-82c8cbe9`, `baseline-s16-r16-seed1906953110644082044-b5d57126`, `baseline-s16-r16-seed17502585706050438786-b989c15d`, `baseline-s16-r16-seed4965958803928893316-f9176421`, `baseline-s16-r16-seed14666344033508591874-f9e59b57`, `baseline-s16-r16-seed8128550842455080494-047aebe6`, `baseline-s16-r16-seed17568711540103687888-2e9d524d`, `baseline-s16-r16-seed180498484486306318-7ff40556`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| 8 | 0 | 0 | 0 | 10 |
| 16 | 0 | 0 | 0 | 10 |
| 24 | 0 | 0 | 0 | 10 |
| 32 | 0 | 0 | 0 | 10 |

Constitution reading: the fused-fight-pairs, region-count and largest-share curves the constitution's Validation program reads, each the mean over seeds with the min and max in brackets.

| x | Fused fight pairs | Region count | Largest share |
| --- | --- | --- | --- |
| 8 | 0 (0 to 0) | 179.7 (177 to 182) | 0.2341 (0.2335 to 0.2345) |
| 16 | 0 (0 to 0) | 172.2 (169 to 174) | 0.2345 (0.234 to 0.235) |
| 24 | 0 (0 to 0) | 169.3 (166 to 171) | 0.2346 (0.234 to 0.235) |
| 32 | 0 (0 to 0) | 163.5 (161 to 165) | 0.2349 (0.2345 to 0.2355) |

## Section size

- **Recommended**: none
- **Label**: Recommendation, recorded under Principle VI (open decision 1)
- **Status**: **FLAGGED**
- **Population**: 5000 (`population.total`, as every run's manifest records)
- **Why**: no swept value has budget.bookkeepingP99Micros ≤ 1000 in every seed; the best worst seed is 1214.2532 at 64 m
- **Rule**: Baseline (sweep section-size) and One base (sweep section-size-onebase) at 5,000 entities, the stress bound the bookkeeping budget is set at, with the section size swept; each sweep is a series. The smallest size whose bookkeeping p99 is within the 1 ms budget (an estimate until measured) in every seed of both scenarios.
- **Read from**: sweep `section-size` (Baseline) and `section-size-onebase` (OneBase); metric `budget.bookkeepingP99Micros` from each run's summary.json; x `regionizer.sectionSize`
- **Margin**: none
- **Principle VI**: A recommendation for open decision 1 (section size and claim radius per class), recorded under Principle VI with its curve, seeds and runs. The value stays in configuration.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `baseline-s8-r16-seed5843011988881251843-74cddf24`, `baseline-s8-r16-seed7620229681788273117-b7934fe9`, `baseline-s8-r16-seed3728128964537905102-058871a2`, `baseline-s8-r16-seed1906953110644082044-e18da9ae`, `baseline-s8-r16-seed17502585706050438786-7278929d`, `baseline-s8-r16-seed4965958803928893316-77581561`, `baseline-s8-r16-seed14666344033508591874-b332dcd4`, `baseline-s8-r16-seed8128550842455080494-38d191da`, `baseline-s8-r16-seed17568711540103687888-4fbdd4d3`, `baseline-s8-r16-seed180498484486306318-befc1cb8`, `baseline-s16-r16-seed5843011988881251843-c6398861`, `baseline-s16-r16-seed7620229681788273117-ca171114`, `baseline-s16-r16-seed3728128964537905102-692295ab`, `baseline-s16-r16-seed1906953110644082044-f2a60a16`, `baseline-s16-r16-seed17502585706050438786-a0b321cb`, `baseline-s16-r16-seed4965958803928893316-34f34416`, `baseline-s16-r16-seed14666344033508591874-629d2b3e`, `baseline-s16-r16-seed8128550842455080494-602a2fb0`, `baseline-s16-r16-seed17568711540103687888-6e52e5d9`, `baseline-s16-r16-seed180498484486306318-913e4a54`, `baseline-s32-r16-seed5843011988881251843-5dc9a6ac`, `baseline-s32-r16-seed7620229681788273117-3b1e4c28`, `baseline-s32-r16-seed3728128964537905102-39e23353`, `baseline-s32-r16-seed1906953110644082044-ad84a0ad`, `baseline-s32-r16-seed17502585706050438786-9dea836e`, `baseline-s32-r16-seed4965958803928893316-91187aaf`, `baseline-s32-r16-seed14666344033508591874-6ea6e667`, `baseline-s32-r16-seed8128550842455080494-4024e895`, `baseline-s32-r16-seed17568711540103687888-b65122a4`, `baseline-s32-r16-seed180498484486306318-cb556fb5`, `baseline-s64-r16-seed5843011988881251843-5481c02a`, `baseline-s64-r16-seed7620229681788273117-8a0c171f`, `baseline-s64-r16-seed3728128964537905102-ec46c363`, `baseline-s64-r16-seed1906953110644082044-1360fc75`, `baseline-s64-r16-seed17502585706050438786-d800e417`, `baseline-s64-r16-seed4965958803928893316-57e625ed`, `baseline-s64-r16-seed14666344033508591874-bd64ca28`, `baseline-s64-r16-seed8128550842455080494-87301943`, `baseline-s64-r16-seed17568711540103687888-313a104d`, `baseline-s64-r16-seed180498484486306318-1882dab3`, `onebase-s8-r16-seed5843011988881251843-d6fb90b6`, `onebase-s8-r16-seed7620229681788273117-d19b134f`, `onebase-s8-r16-seed3728128964537905102-8b3dbbfd`, `onebase-s8-r16-seed1906953110644082044-83dc82aa`, `onebase-s8-r16-seed17502585706050438786-b18a18bd`, `onebase-s8-r16-seed4965958803928893316-4d81aa6f`, `onebase-s8-r16-seed14666344033508591874-7350cf79`, `onebase-s8-r16-seed8128550842455080494-04df16d8`, `onebase-s8-r16-seed17568711540103687888-49fea3ce`, `onebase-s8-r16-seed180498484486306318-8bc3e3dc`, `onebase-s16-r16-seed5843011988881251843-f5e3ede1`, `onebase-s16-r16-seed7620229681788273117-ec74bbbc`, `onebase-s16-r16-seed3728128964537905102-501b932b`, `onebase-s16-r16-seed1906953110644082044-cf798756`, `onebase-s16-r16-seed17502585706050438786-fcba9bc6`, `onebase-s16-r16-seed4965958803928893316-b534fa4c`, `onebase-s16-r16-seed14666344033508591874-6bd65030`, `onebase-s16-r16-seed8128550842455080494-7e82c550`, `onebase-s16-r16-seed17568711540103687888-00b3dfb0`, `onebase-s16-r16-seed180498484486306318-5e603c29`, `onebase-s32-r16-seed5843011988881251843-b9268f10`, `onebase-s32-r16-seed7620229681788273117-4e2dc745`, `onebase-s32-r16-seed3728128964537905102-d2262cf2`, `onebase-s32-r16-seed1906953110644082044-27c5da1b`, `onebase-s32-r16-seed17502585706050438786-61be1a83`, `onebase-s32-r16-seed4965958803928893316-92d27506`, `onebase-s32-r16-seed14666344033508591874-ede5e588`, `onebase-s32-r16-seed8128550842455080494-13926e91`, `onebase-s32-r16-seed17568711540103687888-9c3d109c`, `onebase-s32-r16-seed180498484486306318-025b0cc5`, `onebase-s64-r16-seed5843011988881251843-cdeb9c77`, `onebase-s64-r16-seed7620229681788273117-f5e04d0e`, `onebase-s64-r16-seed3728128964537905102-8a4d7afb`, `onebase-s64-r16-seed1906953110644082044-b698d6a2`, `onebase-s64-r16-seed17502585706050438786-72e43200`, `onebase-s64-r16-seed4965958803928893316-4e23f111`, `onebase-s64-r16-seed14666344033508591874-a5fbe0bb`, `onebase-s64-r16-seed8128550842455080494-7a2ef8ad`, `onebase-s64-r16-seed17568711540103687888-d5049838`, `onebase-s64-r16-seed180498484486306318-9fef3989`

| Series | x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- | --- |
| section-size | 8 | 3072.7878 | 3011.5748 | 3138.9616 | 10 |
| section-size | 16 | 1944.8983 | 1885.6647 | 1994.2068 | 10 |
| section-size | 32 | 1277.6231 | 1121.1917 | 1578.5403 | 10 |
| section-size | 64 | 1142.0341 | 1092.2392 | 1214.2532 | 10 |
| section-size-onebase | 8 | 4163.0278 | 4144.5703 | 4205.2951 | 10 |
| section-size-onebase | 16 | 2110.8955 | 2097.1903 | 2124.5973 | 10 |
| section-size-onebase | 32 | 961.0879 | 916.842 | 998.648 | 10 |
| section-size-onebase | 64 | 264.5958 | 256.0751 | 278.5281 | 10 |

## Claim anchoring

- **Recommended**: Position (measurement)
- **Label**: Recommendation
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Baseline at 2,000 players in both anchoring modes at equal bookkeeping cost. The mode with the lower fusion-event rate (fusions per second, every causing class summed). Fusions are a fight reading, so this row reads the constitution's continent cap like the claim-radius rows.
- **Read from**: sweep `anchoring` (Baseline); metric `rates.fusionsPerSecondByClass` from each run's summary.json; x `regionizer.anchoring`
- **Margin**: mean 0 against 0.0003 for Section (100% lower)
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `baseline-s16-r16-seed5843011988881251843-1d867f75`, `baseline-s16-r16-seed7620229681788273117-b91679ab`, `baseline-s16-r16-seed3728128964537905102-771c30b8`, `baseline-s16-r16-seed1906953110644082044-26bb0ce8`, `baseline-s16-r16-seed17502585706050438786-83ae4bf0`, `baseline-s16-r16-seed4965958803928893316-51a3b43b`, `baseline-s16-r16-seed14666344033508591874-cd55c78d`, `baseline-s16-r16-seed8128550842455080494-58b945ee`, `baseline-s16-r16-seed17568711540103687888-55a93f45`, `baseline-s16-r16-seed180498484486306318-e12b02f0`, `baseline-s16-r16-seed5843011988881251843-57c912c1`, `baseline-s16-r16-seed7620229681788273117-28b64af5`, `baseline-s16-r16-seed3728128964537905102-6f6936a6`, `baseline-s16-r16-seed1906953110644082044-05726e67`, `baseline-s16-r16-seed17502585706050438786-e612cef6`, `baseline-s16-r16-seed4965958803928893316-fd7fbb36`, `baseline-s16-r16-seed14666344033508591874-90d0ad0e`, `baseline-s16-r16-seed8128550842455080494-fd0d72b2`, `baseline-s16-r16-seed17568711540103687888-4bf7d510`, `baseline-s16-r16-seed180498484486306318-3760bf21`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| Section | 0.0003 | 0 | 0.0033 | 10 |
| Position | 0 | 0 | 0 | 10 |

## Minimum base spacing

- **Recommended**: 1000 m (measurement)
- **Label**: Recommendation
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Spacing at 2,000 players with the base spacing swept. The smallest spacing whose oversized-fusion ticks are under 1% of ticks in every seed, plus a 100 m sprawl margin (a placeholder until fight sprawl is measured). The fused-fight-pairs, region-count and largest-share curves are recorded next to it.
- **Read from**: sweep `spacing` (Spacing); metric `totals.oversizedFusionTicks / ticks` from each run's summary.json; x the cells' swept parameters
- **Margin**: worst seed totals.oversizedFusionTicks / ticks 0 < 0.01 at 900 m; 850 m measures 0.0192 (fails); read 900 m, plus a margin of 100 m
- **Region count**: Region count collapses at 700 m and below (mean fusedFightPairs.p50 ≥ 1): at 700 m the mean metrics.regionCount.p50 is 1.3 and the mean metrics.largestRegionShare.p50 is 0.9998; one step above, at 750 m the mean metrics.regionCount.p50 is 2 and the mean metrics.largestRegionShare.p50 is 0.5.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `spacing-s16-r16-seed5843011988881251843-9efaab50`, `spacing-s16-r16-seed7620229681788273117-0090fe3f`, `spacing-s16-r16-seed3728128964537905102-cb7dfe92`, `spacing-s16-r16-seed1906953110644082044-c4fe4d9e`, `spacing-s16-r16-seed17502585706050438786-f4e3c395`, `spacing-s16-r16-seed4965958803928893316-2c0b8014`, `spacing-s16-r16-seed14666344033508591874-d0093ff9`, `spacing-s16-r16-seed8128550842455080494-23b398b7`, `spacing-s16-r16-seed17568711540103687888-2677437d`, `spacing-s16-r16-seed180498484486306318-74273617`, `spacing-s16-r16-seed5843011988881251843-4085765f`, `spacing-s16-r16-seed7620229681788273117-f1626954`, `spacing-s16-r16-seed3728128964537905102-e50b360e`, `spacing-s16-r16-seed1906953110644082044-fe956118`, `spacing-s16-r16-seed17502585706050438786-4a6f0ef3`, `spacing-s16-r16-seed4965958803928893316-2f6dafeb`, `spacing-s16-r16-seed14666344033508591874-d72bc8aa`, `spacing-s16-r16-seed8128550842455080494-a4096e7c`, `spacing-s16-r16-seed17568711540103687888-cabdc8b2`, `spacing-s16-r16-seed180498484486306318-9aa8a621`, `spacing-s16-r16-seed5843011988881251843-97e933fc`, `spacing-s16-r16-seed7620229681788273117-935d19cf`, `spacing-s16-r16-seed3728128964537905102-851d6e80`, `spacing-s16-r16-seed1906953110644082044-6d339a9c`, `spacing-s16-r16-seed17502585706050438786-6dd24b81`, `spacing-s16-r16-seed4965958803928893316-3d135cf4`, `spacing-s16-r16-seed14666344033508591874-01882663`, `spacing-s16-r16-seed8128550842455080494-8bc9e6ca`, `spacing-s16-r16-seed17568711540103687888-5becdb41`, `spacing-s16-r16-seed180498484486306318-586a1c77`, `spacing-s16-r16-seed5843011988881251843-1b3c5a57`, `spacing-s16-r16-seed7620229681788273117-f9e85341`, `spacing-s16-r16-seed3728128964537905102-ff5471fc`, `spacing-s16-r16-seed1906953110644082044-97fa6e41`, `spacing-s16-r16-seed17502585706050438786-4ea6afe8`, `spacing-s16-r16-seed4965958803928893316-2caef976`, `spacing-s16-r16-seed14666344033508591874-b3444218`, `spacing-s16-r16-seed8128550842455080494-b437491e`, `spacing-s16-r16-seed17568711540103687888-f3c3bf67`, `spacing-s16-r16-seed180498484486306318-9566c5f5`, `spacing-s16-r16-seed5843011988881251843-afda3e2b`, `spacing-s16-r16-seed7620229681788273117-0d3902c8`, `spacing-s16-r16-seed3728128964537905102-b02793d5`, `spacing-s16-r16-seed1906953110644082044-c31a3f16`, `spacing-s16-r16-seed17502585706050438786-5a716bee`, `spacing-s16-r16-seed4965958803928893316-29db7137`, `spacing-s16-r16-seed14666344033508591874-e1ec4661`, `spacing-s16-r16-seed8128550842455080494-5d21535b`, `spacing-s16-r16-seed17568711540103687888-74b429f8`, `spacing-s16-r16-seed180498484486306318-d27f708d`, `spacing-s16-r16-seed5843011988881251843-2819f510`, `spacing-s16-r16-seed7620229681788273117-1dd39121`, `spacing-s16-r16-seed3728128964537905102-3e1c5d4f`, `spacing-s16-r16-seed1906953110644082044-5b32d1f0`, `spacing-s16-r16-seed17502585706050438786-08070967`, `spacing-s16-r16-seed4965958803928893316-d272029d`, `spacing-s16-r16-seed14666344033508591874-e0daca36`, `spacing-s16-r16-seed8128550842455080494-ac0b978b`, `spacing-s16-r16-seed17568711540103687888-67d84f7b`, `spacing-s16-r16-seed180498484486306318-f9ce2173`, `spacing-s16-r16-seed5843011988881251843-e0d53a28`, `spacing-s16-r16-seed7620229681788273117-f85c5501`, `spacing-s16-r16-seed3728128964537905102-0ba09058`, `spacing-s16-r16-seed1906953110644082044-acbeca12`, `spacing-s16-r16-seed17502585706050438786-8bfa004c`, `spacing-s16-r16-seed4965958803928893316-30eac8eb`, `spacing-s16-r16-seed14666344033508591874-fc6e1063`, `spacing-s16-r16-seed8128550842455080494-627ccbaa`, `spacing-s16-r16-seed17568711540103687888-cd0eff9b`, `spacing-s16-r16-seed180498484486306318-c0ef1356`, `spacing-s16-r16-seed5843011988881251843-ea04a2ea`, `spacing-s16-r16-seed7620229681788273117-c5aade51`, `spacing-s16-r16-seed3728128964537905102-072a1e7f`, `spacing-s16-r16-seed1906953110644082044-fd647c9d`, `spacing-s16-r16-seed17502585706050438786-8e643130`, `spacing-s16-r16-seed4965958803928893316-7a3c0adf`, `spacing-s16-r16-seed14666344033508591874-a649e0dd`, `spacing-s16-r16-seed8128550842455080494-44037811`, `spacing-s16-r16-seed17568711540103687888-9ddb48af`, `spacing-s16-r16-seed180498484486306318-131dde7e`, `spacing-s16-r16-seed5843011988881251843-25a52940`, `spacing-s16-r16-seed7620229681788273117-63214b92`, `spacing-s16-r16-seed3728128964537905102-932fe154`, `spacing-s16-r16-seed1906953110644082044-c5e880ee`, `spacing-s16-r16-seed17502585706050438786-f8fa37e1`, `spacing-s16-r16-seed4965958803928893316-fd45fc0d`, `spacing-s16-r16-seed14666344033508591874-98bf4d64`, `spacing-s16-r16-seed8128550842455080494-551a5155`, `spacing-s16-r16-seed17568711540103687888-ed1f44f9`, `spacing-s16-r16-seed180498484486306318-69f6375f`, `spacing-s16-r16-seed5843011988881251843-c5547f65`, `spacing-s16-r16-seed7620229681788273117-52b931c6`, `spacing-s16-r16-seed3728128964537905102-886b656c`, `spacing-s16-r16-seed1906953110644082044-0fe7540b`, `spacing-s16-r16-seed17502585706050438786-f18b94c4`, `spacing-s16-r16-seed4965958803928893316-8464740e`, `spacing-s16-r16-seed14666344033508591874-7d5c913b`, `spacing-s16-r16-seed8128550842455080494-e1eecbea`, `spacing-s16-r16-seed17568711540103687888-c486bf2e`, `spacing-s16-r16-seed180498484486306318-3e1e2eed`, `spacing-s16-r16-seed5843011988881251843-53087b46`, `spacing-s16-r16-seed7620229681788273117-7ac76419`, `spacing-s16-r16-seed3728128964537905102-1a73e067`, `spacing-s16-r16-seed1906953110644082044-dc40749c`, `spacing-s16-r16-seed17502585706050438786-af1537ea`, `spacing-s16-r16-seed4965958803928893316-cb6cec23`, `spacing-s16-r16-seed14666344033508591874-cba49bfb`, `spacing-s16-r16-seed8128550842455080494-b95a6b03`, `spacing-s16-r16-seed17568711540103687888-1296b2fa`, `spacing-s16-r16-seed180498484486306318-61e0b19d`, `spacing-s16-r16-seed5843011988881251843-e7b29dd7`, `spacing-s16-r16-seed7620229681788273117-a4cd5271`, `spacing-s16-r16-seed3728128964537905102-cab9b508`, `spacing-s16-r16-seed1906953110644082044-cb807e01`, `spacing-s16-r16-seed17502585706050438786-87c19a13`, `spacing-s16-r16-seed4965958803928893316-757e321d`, `spacing-s16-r16-seed14666344033508591874-86d85a03`, `spacing-s16-r16-seed8128550842455080494-0a6e3034`, `spacing-s16-r16-seed17568711540103687888-ead3b86f`, `spacing-s16-r16-seed180498484486306318-823429bd`, `spacing-s16-r16-seed5843011988881251843-2e64fcc7`, `spacing-s16-r16-seed7620229681788273117-b4e39f93`, `spacing-s16-r16-seed3728128964537905102-5e8c07f2`, `spacing-s16-r16-seed1906953110644082044-d09f21aa`, `spacing-s16-r16-seed17502585706050438786-8ec478eb`, `spacing-s16-r16-seed4965958803928893316-70a436aa`, `spacing-s16-r16-seed14666344033508591874-75de85c0`, `spacing-s16-r16-seed8128550842455080494-b6386e46`, `spacing-s16-r16-seed17568711540103687888-6b4e34d7`, `spacing-s16-r16-seed180498484486306318-ed00338f`, `spacing-s16-r16-seed5843011988881251843-8e3c02c1`, `spacing-s16-r16-seed7620229681788273117-b90c3dc2`, `spacing-s16-r16-seed3728128964537905102-881d2672`, `spacing-s16-r16-seed1906953110644082044-f441da32`, `spacing-s16-r16-seed17502585706050438786-b1c7f164`, `spacing-s16-r16-seed4965958803928893316-f78a2b8c`, `spacing-s16-r16-seed14666344033508591874-46c01bee`, `spacing-s16-r16-seed8128550842455080494-654a05b4`, `spacing-s16-r16-seed17568711540103687888-5ed65d84`, `spacing-s16-r16-seed180498484486306318-ad4ad386`, `spacing-s16-r16-seed5843011988881251843-0d802950`, `spacing-s16-r16-seed7620229681788273117-a186bb97`, `spacing-s16-r16-seed3728128964537905102-7bb7b04f`, `spacing-s16-r16-seed1906953110644082044-f04c456b`, `spacing-s16-r16-seed17502585706050438786-a3574e1f`, `spacing-s16-r16-seed4965958803928893316-c4ed2ce2`, `spacing-s16-r16-seed14666344033508591874-5d8d1f72`, `spacing-s16-r16-seed8128550842455080494-6d03225e`, `spacing-s16-r16-seed17568711540103687888-9ffe35f1`, `spacing-s16-r16-seed180498484486306318-55a9b219`, `spacing-s16-r16-seed5843011988881251843-b198ef2f`, `spacing-s16-r16-seed7620229681788273117-72584e21`, `spacing-s16-r16-seed3728128964537905102-4f865a89`, `spacing-s16-r16-seed1906953110644082044-a0dd2867`, `spacing-s16-r16-seed17502585706050438786-8b972913`, `spacing-s16-r16-seed4965958803928893316-b52adb17`, `spacing-s16-r16-seed14666344033508591874-5343c721`, `spacing-s16-r16-seed8128550842455080494-7a66dd66`, `spacing-s16-r16-seed17568711540103687888-b6d4b961`, `spacing-s16-r16-seed180498484486306318-1fb296b3`, `spacing-s16-r16-seed5843011988881251843-9f8fe88b`, `spacing-s16-r16-seed7620229681788273117-049919de`, `spacing-s16-r16-seed3728128964537905102-e6b95ae2`, `spacing-s16-r16-seed1906953110644082044-b8be5b3c`, `spacing-s16-r16-seed17502585706050438786-dd7a2e4a`, `spacing-s16-r16-seed4965958803928893316-78e4a61a`, `spacing-s16-r16-seed14666344033508591874-3cb466d7`, `spacing-s16-r16-seed8128550842455080494-ab89ae7e`, `spacing-s16-r16-seed17568711540103687888-1c426e89`, `spacing-s16-r16-seed180498484486306318-61d6d08e`, `spacing-s16-r16-seed5843011988881251843-6aeb135a`, `spacing-s16-r16-seed7620229681788273117-15b4e03d`, `spacing-s16-r16-seed3728128964537905102-9eed764b`, `spacing-s16-r16-seed1906953110644082044-6a0421c8`, `spacing-s16-r16-seed17502585706050438786-99370838`, `spacing-s16-r16-seed4965958803928893316-e3c71f96`, `spacing-s16-r16-seed14666344033508591874-43ca5104`, `spacing-s16-r16-seed8128550842455080494-69b842cb`, `spacing-s16-r16-seed17568711540103687888-74c6ad36`, `spacing-s16-r16-seed180498484486306318-75893328`, `spacing-s16-r16-seed5843011988881251843-3b3bb00e`, `spacing-s16-r16-seed7620229681788273117-c5b0af9a`, `spacing-s16-r16-seed3728128964537905102-9ccff9a3`, `spacing-s16-r16-seed1906953110644082044-5833f8fb`, `spacing-s16-r16-seed17502585706050438786-44027d3c`, `spacing-s16-r16-seed4965958803928893316-22da1821`, `spacing-s16-r16-seed14666344033508591874-caf10b6a`, `spacing-s16-r16-seed8128550842455080494-b3ad0a89`, `spacing-s16-r16-seed17568711540103687888-b5d9673b`, `spacing-s16-r16-seed180498484486306318-e0d58359`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| 100 | 1 | 1 | 1 | 10 |
| 150 | 1 | 1 | 1 | 10 |
| 200 | 1 | 1 | 1 | 10 |
| 250 | 1 | 1 | 1 | 10 |
| 300 | 1 | 1 | 1 | 10 |
| 350 | 1 | 1 | 1 | 10 |
| 400 | 1 | 1 | 1 | 10 |
| 450 | 1 | 1 | 1 | 10 |
| 500 | 1 | 1 | 1 | 10 |
| 550 | 0.9994 | 0.9977 | 1 | 10 |
| 600 | 0.9898 | 0.9843 | 0.9958 | 10 |
| 650 | 0.912 | 0.8612 | 0.9532 | 10 |
| 700 | 0.636 | 0.5285 | 0.7262 | 10 |
| 750 | 0.2574 | 0.158 | 0.4712 | 10 |
| 800 | 0.041 | 0.0184 | 0.0734 | 10 |
| 850 | 0.0059 | 0 | 0.0192 | 10 |
| 900 | 0 | 0 | 0 | 10 |
| 950 | 0 | 0 | 0 | 10 |
| 1000 | 0 | 0 | 0 | 10 |

Constitution reading: the fused-fight-pairs, region-count and largest-share curves the constitution's Validation program reads, each the mean over seeds with the min and max in brackets.

| x | Fused fight pairs | Region count | Largest share |
| --- | --- | --- | --- |
| 100 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 150 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 200 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 250 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 300 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 350 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 400 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 450 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 500 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 550 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 600 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 650 | 1 (1 to 1) | 1 (1 to 1) | 1 (1 to 1) |
| 700 | 1 (1 to 1) | 1.3 (1 to 2) | 0.9998 (0.999 to 1) |
| 750 | 0.1 (0 to 1) | 2 (2 to 2) | 0.5 (0.5 to 0.5003) |
| 800 | 0 (0 to 0) | 2.1 (2 to 3) | 0.5 (0.5 to 0.5) |
| 850 | 0 (0 to 0) | 2.1 (2 to 3) | 0.5 (0.5 to 0.5) |
| 900 | 0 (0 to 0) | 2 (2 to 2) | 0.5 (0.5 to 0.5) |
| 950 | 0 (0 to 0) | 2 (2 to 2) | 0.5 (0.5 to 0.5) |
| 1000 | 0 (0 to 0) | 2 (2 to 2) | 0.5 (0.5 to 0.5) |

## Lane bridging limit

- **Recommended**: 5 travellers (measurement)
- **Label**: Recommendation
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Lane traffic at 2,000 players with the traveller count swept. The largest traffic level whose bridging (fight-fusion) ticks are under 1% of ticks in every seed. The lane-traffic sweep sets metrics.targetFightSize below the two fights' combined player count, so an oversized-fusion tick is a tick on which the fights are fused.
- **Read from**: sweep `lane-traffic` (LaneTraffic); metric `totals.oversizedFusionTicks / ticks` from each run's summary.json; x the cells' swept parameters
- **Margin**: worst seed totals.oversizedFusionTicks / ticks 0 < 0.01 at 5 travellers; 10 travellers measures 0.0237 (fails)
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `lanetraffic-s16-r16-seed5843011988881251843-d5680a82`, `lanetraffic-s16-r16-seed7620229681788273117-702c5a3b`, `lanetraffic-s16-r16-seed3728128964537905102-a0680cf5`, `lanetraffic-s16-r16-seed1906953110644082044-d21f6222`, `lanetraffic-s16-r16-seed17502585706050438786-d1825a32`, `lanetraffic-s16-r16-seed4965958803928893316-0a0e0b75`, `lanetraffic-s16-r16-seed14666344033508591874-70e770e0`, `lanetraffic-s16-r16-seed8128550842455080494-c6464ad4`, `lanetraffic-s16-r16-seed17568711540103687888-88f54c51`, `lanetraffic-s16-r16-seed180498484486306318-9bd19668`, `lanetraffic-s16-r16-seed5843011988881251843-12583ccf`, `lanetraffic-s16-r16-seed7620229681788273117-04f68bea`, `lanetraffic-s16-r16-seed3728128964537905102-a8bc5473`, `lanetraffic-s16-r16-seed1906953110644082044-22eb4128`, `lanetraffic-s16-r16-seed17502585706050438786-4a2bac23`, `lanetraffic-s16-r16-seed4965958803928893316-672815ef`, `lanetraffic-s16-r16-seed14666344033508591874-b2c9a57b`, `lanetraffic-s16-r16-seed8128550842455080494-20a0314e`, `lanetraffic-s16-r16-seed17568711540103687888-db931d75`, `lanetraffic-s16-r16-seed180498484486306318-468ae56c`, `lanetraffic-s16-r16-seed5843011988881251843-0c235826`, `lanetraffic-s16-r16-seed7620229681788273117-90586c29`, `lanetraffic-s16-r16-seed3728128964537905102-8a977e61`, `lanetraffic-s16-r16-seed1906953110644082044-efa36b29`, `lanetraffic-s16-r16-seed17502585706050438786-e9bedcc3`, `lanetraffic-s16-r16-seed4965958803928893316-0c9d4efe`, `lanetraffic-s16-r16-seed14666344033508591874-3c3c5946`, `lanetraffic-s16-r16-seed8128550842455080494-a074ef54`, `lanetraffic-s16-r16-seed17568711540103687888-9fc30fc7`, `lanetraffic-s16-r16-seed180498484486306318-a2b11adc`, `lanetraffic-s16-r16-seed5843011988881251843-9325b5b6`, `lanetraffic-s16-r16-seed7620229681788273117-294df5dc`, `lanetraffic-s16-r16-seed3728128964537905102-e7b8b79c`, `lanetraffic-s16-r16-seed1906953110644082044-8e533eb7`, `lanetraffic-s16-r16-seed17502585706050438786-3402421a`, `lanetraffic-s16-r16-seed4965958803928893316-6ab1343b`, `lanetraffic-s16-r16-seed14666344033508591874-359b1e06`, `lanetraffic-s16-r16-seed8128550842455080494-eb3dd56d`, `lanetraffic-s16-r16-seed17568711540103687888-8bf3ca03`, `lanetraffic-s16-r16-seed180498484486306318-dbb68858`, `lanetraffic-s16-r16-seed5843011988881251843-c0840569`, `lanetraffic-s16-r16-seed7620229681788273117-be6fd1db`, `lanetraffic-s16-r16-seed3728128964537905102-f483b0b6`, `lanetraffic-s16-r16-seed1906953110644082044-373e0ef6`, `lanetraffic-s16-r16-seed17502585706050438786-05b50199`, `lanetraffic-s16-r16-seed4965958803928893316-ea61149b`, `lanetraffic-s16-r16-seed14666344033508591874-5ae5baf2`, `lanetraffic-s16-r16-seed8128550842455080494-514fa7c1`, `lanetraffic-s16-r16-seed17568711540103687888-4f8466b8`, `lanetraffic-s16-r16-seed180498484486306318-94985282`, `lanetraffic-s16-r16-seed5843011988881251843-089d41f5`, `lanetraffic-s16-r16-seed7620229681788273117-c605201d`, `lanetraffic-s16-r16-seed3728128964537905102-bee648d4`, `lanetraffic-s16-r16-seed1906953110644082044-7bf859f4`, `lanetraffic-s16-r16-seed17502585706050438786-f5465719`, `lanetraffic-s16-r16-seed4965958803928893316-be734d00`, `lanetraffic-s16-r16-seed14666344033508591874-48530b23`, `lanetraffic-s16-r16-seed8128550842455080494-9414d278`, `lanetraffic-s16-r16-seed17568711540103687888-2bc4c272`, `lanetraffic-s16-r16-seed180498484486306318-792c58e0`, `lanetraffic-s16-r16-seed5843011988881251843-e71401c4`, `lanetraffic-s16-r16-seed7620229681788273117-ef982057`, `lanetraffic-s16-r16-seed3728128964537905102-d98f7306`, `lanetraffic-s16-r16-seed1906953110644082044-309a7571`, `lanetraffic-s16-r16-seed17502585706050438786-4b5c9632`, `lanetraffic-s16-r16-seed4965958803928893316-55effe06`, `lanetraffic-s16-r16-seed14666344033508591874-1a4ec9c3`, `lanetraffic-s16-r16-seed8128550842455080494-6b4e2f8e`, `lanetraffic-s16-r16-seed17568711540103687888-118a13c4`, `lanetraffic-s16-r16-seed180498484486306318-62fe6420`, `lanetraffic-s16-r16-seed5843011988881251843-2e6c236f`, `lanetraffic-s16-r16-seed7620229681788273117-7313a047`, `lanetraffic-s16-r16-seed3728128964537905102-3a547a3e`, `lanetraffic-s16-r16-seed1906953110644082044-f9f8c3ac`, `lanetraffic-s16-r16-seed17502585706050438786-b1bb0c9b`, `lanetraffic-s16-r16-seed4965958803928893316-ed9fe85d`, `lanetraffic-s16-r16-seed14666344033508591874-032ae35b`, `lanetraffic-s16-r16-seed8128550842455080494-3cd34050`, `lanetraffic-s16-r16-seed17568711540103687888-3b785877`, `lanetraffic-s16-r16-seed180498484486306318-52fe6db4`, `lanetraffic-s16-r16-seed5843011988881251843-edfe6679`, `lanetraffic-s16-r16-seed7620229681788273117-93cb077f`, `lanetraffic-s16-r16-seed3728128964537905102-e294e5c9`, `lanetraffic-s16-r16-seed1906953110644082044-dab7c61f`, `lanetraffic-s16-r16-seed17502585706050438786-6d0b9f2d`, `lanetraffic-s16-r16-seed4965958803928893316-7aacea82`, `lanetraffic-s16-r16-seed14666344033508591874-fe71f460`, `lanetraffic-s16-r16-seed8128550842455080494-257c2b97`, `lanetraffic-s16-r16-seed17568711540103687888-60ee4597`, `lanetraffic-s16-r16-seed180498484486306318-cbaa4e6d`, `lanetraffic-s16-r16-seed5843011988881251843-38e8c4b8`, `lanetraffic-s16-r16-seed7620229681788273117-693b4b7f`, `lanetraffic-s16-r16-seed3728128964537905102-069b58b2`, `lanetraffic-s16-r16-seed1906953110644082044-c70385e8`, `lanetraffic-s16-r16-seed17502585706050438786-45a3c2a2`, `lanetraffic-s16-r16-seed4965958803928893316-eac75988`, `lanetraffic-s16-r16-seed14666344033508591874-23e71052`, `lanetraffic-s16-r16-seed8128550842455080494-bb18b038`, `lanetraffic-s16-r16-seed17568711540103687888-a15e99b1`, `lanetraffic-s16-r16-seed180498484486306318-e78e0c95`, `lanetraffic-s16-r16-seed5843011988881251843-6b3f360e`, `lanetraffic-s16-r16-seed7620229681788273117-e9f59cc0`, `lanetraffic-s16-r16-seed3728128964537905102-96dc0487`, `lanetraffic-s16-r16-seed1906953110644082044-b187ed86`, `lanetraffic-s16-r16-seed17502585706050438786-ce25f532`, `lanetraffic-s16-r16-seed4965958803928893316-bd4fa88d`, `lanetraffic-s16-r16-seed14666344033508591874-9d06d03d`, `lanetraffic-s16-r16-seed8128550842455080494-27ffdaee`, `lanetraffic-s16-r16-seed17568711540103687888-8ed687fb`, `lanetraffic-s16-r16-seed180498484486306318-b79e5def`, `lanetraffic-s16-r16-seed5843011988881251843-76d3e04d`, `lanetraffic-s16-r16-seed7620229681788273117-9c2444aa`, `lanetraffic-s16-r16-seed3728128964537905102-627f9038`, `lanetraffic-s16-r16-seed1906953110644082044-1a816851`, `lanetraffic-s16-r16-seed17502585706050438786-7604f01e`, `lanetraffic-s16-r16-seed4965958803928893316-f8f79c72`, `lanetraffic-s16-r16-seed14666344033508591874-4b0f60c6`, `lanetraffic-s16-r16-seed8128550842455080494-75d6c800`, `lanetraffic-s16-r16-seed17568711540103687888-9a967c08`, `lanetraffic-s16-r16-seed180498484486306318-6ccd4b11`, `lanetraffic-s16-r16-seed5843011988881251843-230f873a`, `lanetraffic-s16-r16-seed7620229681788273117-539becdc`, `lanetraffic-s16-r16-seed3728128964537905102-e74d5548`, `lanetraffic-s16-r16-seed1906953110644082044-3a0db417`, `lanetraffic-s16-r16-seed17502585706050438786-2e3756b5`, `lanetraffic-s16-r16-seed4965958803928893316-a8a7feb3`, `lanetraffic-s16-r16-seed14666344033508591874-55aee0d4`, `lanetraffic-s16-r16-seed8128550842455080494-8b723867`, `lanetraffic-s16-r16-seed17568711540103687888-0ac6bd4d`, `lanetraffic-s16-r16-seed180498484486306318-b6555e98`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| 0 | 0 | 0 | 0 | 10 |
| 2 | 0 | 0 | 0 | 10 |
| 5 | 0 | 0 | 0 | 10 |
| 10 | 0.0084 | 0 | 0.0237 | 10 |
| 20 | 0.0751 | 0.0266 | 0.1269 | 10 |
| 30 | 0.0967 | 0.0402 | 0.1948 | 10 |
| 40 | 0.104 | 0.057 | 0.2212 | 10 |
| 60 | 0.1079 | 0.0618 | 0.1778 | 10 |
| 80 | 0.1068 | 0.0473 | 0.1682 | 10 |
| 100 | 0.1211 | 0.0664 | 0.2271 | 10 |
| 120 | 0.1293 | 0.0649 | 0.1922 | 10 |
| 150 | 0.1394 | 0.0746 | 0.2177 | 10 |
| 200 | 0.123 | 0.0701 | 0.1788 | 10 |

## Split damping

- **Recommended**: regionizer.split.minDeadPercent=50 (measurement)
- **Label**: Recommendation
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Migration at 2,000 players with N_min, p_dead and T_min swept. The lowest merge-plus-split rate (per second, every causing class summed) among cells whose split latency p99 is under 5 s (150 ticks at 30 Hz) in every seed. Merges are real merges: absorptions of entity-less regions are not in the rate.
- **Read from**: sweep `split-damping` (Migration); metric `rates.mergesPerSecondByClass + rates.splitsPerSecond` from each run's summary.json; x the cells' swept parameters
- **Margin**: mean 30.8652 against 38.4383 for regionizer.split.minDeadPercent=40 (19.7% lower); worst seed metrics.splitLatencyTicks.p99 115 < 150 (1 of 16 cells fail it)
- **Merge counting**: Merge rates count real merges only, where both sides held an entity. Absorptions of entity-less regions are reported separately (summary.json `totals.absorptions`: mean 545566.0875 per run, 452453 to 604105) and are not in the rate.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `migration-s16-r16-seed5843011988881251843-d25c0597`, `migration-s16-r16-seed7620229681788273117-0942cad9`, `migration-s16-r16-seed3728128964537905102-60aead0a`, `migration-s16-r16-seed1906953110644082044-49db5972`, `migration-s16-r16-seed17502585706050438786-551664e0`, `migration-s16-r16-seed4965958803928893316-2b540f47`, `migration-s16-r16-seed14666344033508591874-b8ff44ba`, `migration-s16-r16-seed8128550842455080494-72fa83a4`, `migration-s16-r16-seed17568711540103687888-0575052b`, `migration-s16-r16-seed180498484486306318-be9b51a0`, `migration-s16-r16-seed5843011988881251843-b8a4ecbd`, `migration-s16-r16-seed7620229681788273117-d56f67d1`, `migration-s16-r16-seed3728128964537905102-39ba633e`, `migration-s16-r16-seed1906953110644082044-ace18622`, `migration-s16-r16-seed17502585706050438786-74f67293`, `migration-s16-r16-seed4965958803928893316-8a0c9b83`, `migration-s16-r16-seed14666344033508591874-d669ceb9`, `migration-s16-r16-seed8128550842455080494-eb371c62`, `migration-s16-r16-seed17568711540103687888-a9ad0d52`, `migration-s16-r16-seed180498484486306318-84ac7f9f`, `migration-s16-r16-seed5843011988881251843-8cb8e8c1`, `migration-s16-r16-seed7620229681788273117-9ff314c2`, `migration-s16-r16-seed3728128964537905102-7960dd98`, `migration-s16-r16-seed1906953110644082044-f47bc70c`, `migration-s16-r16-seed17502585706050438786-a6023f74`, `migration-s16-r16-seed4965958803928893316-2f2327b4`, `migration-s16-r16-seed14666344033508591874-cb9668bd`, `migration-s16-r16-seed8128550842455080494-5bcfa8a2`, `migration-s16-r16-seed17568711540103687888-0362f6f9`, `migration-s16-r16-seed180498484486306318-ce7bed34`, `migration-s16-r16-seed5843011988881251843-3237520f`, `migration-s16-r16-seed7620229681788273117-e5fbdd87`, `migration-s16-r16-seed3728128964537905102-e4a6dc41`, `migration-s16-r16-seed1906953110644082044-84f2e0fd`, `migration-s16-r16-seed17502585706050438786-de284427`, `migration-s16-r16-seed4965958803928893316-67bf6f3e`, `migration-s16-r16-seed14666344033508591874-28197f76`, `migration-s16-r16-seed8128550842455080494-9239e352`, `migration-s16-r16-seed17568711540103687888-2e2a6500`, `migration-s16-r16-seed180498484486306318-e21d72fa`, `migration-s16-r16-seed5843011988881251843-b0accc65`, `migration-s16-r16-seed7620229681788273117-bd2d1fd3`, `migration-s16-r16-seed3728128964537905102-332a58e9`, `migration-s16-r16-seed1906953110644082044-0963abf2`, `migration-s16-r16-seed17502585706050438786-a5842099`, `migration-s16-r16-seed4965958803928893316-49e01db7`, `migration-s16-r16-seed14666344033508591874-2edc427a`, `migration-s16-r16-seed8128550842455080494-d299104a`, `migration-s16-r16-seed17568711540103687888-5c1b9d68`, `migration-s16-r16-seed180498484486306318-bff129d8`, `migration-s16-r16-seed5843011988881251843-9f0c21e7`, `migration-s16-r16-seed7620229681788273117-87e11244`, `migration-s16-r16-seed3728128964537905102-b969e689`, `migration-s16-r16-seed1906953110644082044-8923c769`, `migration-s16-r16-seed17502585706050438786-247ea431`, `migration-s16-r16-seed4965958803928893316-6b5e34c4`, `migration-s16-r16-seed14666344033508591874-c80f8e25`, `migration-s16-r16-seed8128550842455080494-02bd28d2`, `migration-s16-r16-seed17568711540103687888-c452709f`, `migration-s16-r16-seed180498484486306318-3733b642`, `migration-s16-r16-seed5843011988881251843-9860e58d`, `migration-s16-r16-seed7620229681788273117-1e6a0a73`, `migration-s16-r16-seed3728128964537905102-8f1a61aa`, `migration-s16-r16-seed1906953110644082044-4f87bb2a`, `migration-s16-r16-seed17502585706050438786-afc0fe00`, `migration-s16-r16-seed4965958803928893316-17d615be`, `migration-s16-r16-seed14666344033508591874-ec70c342`, `migration-s16-r16-seed8128550842455080494-d7c3204e`, `migration-s16-r16-seed17568711540103687888-e77f8cdc`, `migration-s16-r16-seed180498484486306318-c2a81be0`, `migration-s16-r16-seed5843011988881251843-d492a946`, `migration-s16-r16-seed7620229681788273117-a76d5a4c`, `migration-s16-r16-seed3728128964537905102-7045ec97`, `migration-s16-r16-seed1906953110644082044-bc21bfc1`, `migration-s16-r16-seed17502585706050438786-e11e061d`, `migration-s16-r16-seed4965958803928893316-b31fce6b`, `migration-s16-r16-seed14666344033508591874-e2c0ff24`, `migration-s16-r16-seed8128550842455080494-e4de00de`, `migration-s16-r16-seed17568711540103687888-62ea2141`, `migration-s16-r16-seed180498484486306318-3e20eda2`, `migration-s16-r16-seed5843011988881251843-9402566c`, `migration-s16-r16-seed7620229681788273117-5d880e9e`, `migration-s16-r16-seed3728128964537905102-b7474ac6`, `migration-s16-r16-seed1906953110644082044-b2a13270`, `migration-s16-r16-seed17502585706050438786-0db9b8cd`, `migration-s16-r16-seed4965958803928893316-ca630073`, `migration-s16-r16-seed14666344033508591874-5ca2098b`, `migration-s16-r16-seed8128550842455080494-e8fb3189`, `migration-s16-r16-seed17568711540103687888-7f859595`, `migration-s16-r16-seed180498484486306318-21c52b80`, `migration-s16-r16-seed5843011988881251843-0bbb3a34`, `migration-s16-r16-seed7620229681788273117-0cb367ba`, `migration-s16-r16-seed3728128964537905102-f20507d9`, `migration-s16-r16-seed1906953110644082044-c70f3e51`, `migration-s16-r16-seed17502585706050438786-05f3cc82`, `migration-s16-r16-seed4965958803928893316-49cd9069`, `migration-s16-r16-seed14666344033508591874-bf4be5bf`, `migration-s16-r16-seed8128550842455080494-4f26b0d5`, `migration-s16-r16-seed17568711540103687888-ff2bd490`, `migration-s16-r16-seed180498484486306318-3ae5534b`, `migration-s16-r16-seed5843011988881251843-6b812c75`, `migration-s16-r16-seed7620229681788273117-15e42eb7`, `migration-s16-r16-seed3728128964537905102-b760ed2b`, `migration-s16-r16-seed1906953110644082044-c9d9e18c`, `migration-s16-r16-seed17502585706050438786-31977848`, `migration-s16-r16-seed4965958803928893316-b7df6589`, `migration-s16-r16-seed14666344033508591874-22713b0a`, `migration-s16-r16-seed8128550842455080494-55e2f128`, `migration-s16-r16-seed17568711540103687888-35767cdb`, `migration-s16-r16-seed180498484486306318-945b7944`, `migration-s16-r16-seed5843011988881251843-75aebf4d`, `migration-s16-r16-seed7620229681788273117-3f456b86`, `migration-s16-r16-seed3728128964537905102-a415af05`, `migration-s16-r16-seed1906953110644082044-655d97f7`, `migration-s16-r16-seed17502585706050438786-f39afc07`, `migration-s16-r16-seed4965958803928893316-c0ad2bd5`, `migration-s16-r16-seed14666344033508591874-65684086`, `migration-s16-r16-seed8128550842455080494-fba3345e`, `migration-s16-r16-seed17568711540103687888-1fbda67b`, `migration-s16-r16-seed180498484486306318-ed985202`, `migration-s16-r16-seed5843011988881251843-7f170737`, `migration-s16-r16-seed7620229681788273117-eedb4dc1`, `migration-s16-r16-seed3728128964537905102-c7e573c8`, `migration-s16-r16-seed1906953110644082044-adf4f4bf`, `migration-s16-r16-seed17502585706050438786-2ee4132b`, `migration-s16-r16-seed4965958803928893316-381aedc2`, `migration-s16-r16-seed14666344033508591874-7e054261`, `migration-s16-r16-seed8128550842455080494-4eb32365`, `migration-s16-r16-seed17568711540103687888-7225410f`, `migration-s16-r16-seed180498484486306318-b97f0a96`, `migration-s16-r16-seed5843011988881251843-8cb8e8c1`, `migration-s16-r16-seed7620229681788273117-9ff314c2`, `migration-s16-r16-seed3728128964537905102-7960dd98`, `migration-s16-r16-seed1906953110644082044-f47bc70c`, `migration-s16-r16-seed17502585706050438786-a6023f74`, `migration-s16-r16-seed4965958803928893316-2f2327b4`, `migration-s16-r16-seed14666344033508591874-cb9668bd`, `migration-s16-r16-seed8128550842455080494-5bcfa8a2`, `migration-s16-r16-seed17568711540103687888-0362f6f9`, `migration-s16-r16-seed180498484486306318-ce7bed34`, `migration-s16-r16-seed5843011988881251843-71959516`, `migration-s16-r16-seed7620229681788273117-18f912ad`, `migration-s16-r16-seed3728128964537905102-9e5f5bdc`, `migration-s16-r16-seed1906953110644082044-c5fca87b`, `migration-s16-r16-seed17502585706050438786-e40ff07f`, `migration-s16-r16-seed4965958803928893316-c05304a9`, `migration-s16-r16-seed14666344033508591874-de15e4e4`, `migration-s16-r16-seed8128550842455080494-a8f40b31`, `migration-s16-r16-seed17568711540103687888-92cdb7ef`, `migration-s16-r16-seed180498484486306318-20ddeb74`, `migration-s16-r16-seed5843011988881251843-5f98adf2`, `migration-s16-r16-seed7620229681788273117-653c1698`, `migration-s16-r16-seed3728128964537905102-89dd53f3`, `migration-s16-r16-seed1906953110644082044-e42f1b15`, `migration-s16-r16-seed17502585706050438786-3f7f495c`, `migration-s16-r16-seed4965958803928893316-340e6cea`, `migration-s16-r16-seed14666344033508591874-ba2a659f`, `migration-s16-r16-seed8128550842455080494-a2ba3fb1`, `migration-s16-r16-seed17568711540103687888-8547edce`, `migration-s16-r16-seed180498484486306318-d875034c`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| regionizer.split.minSections=8 | 54.5613 | 53.965 | 55.6567 | 10 |
| regionizer.split.minSections=16 | 54.5613 | 53.965 | 55.6567 | 10 |
| regionizer.split.minSections=32 | 56.4865 | 55.6667 | 57.9367 | 10 |
| regionizer.split.minSections=64 | 62.6858 | 61.8517 | 64.1467 | 10 |
| regionizer.split.minSections=128 | 59.2485 | 58.0417 | 60.6683 | 10 |
| regionizer.split.minSections=256 | 54.5142 | 53.265 | 55.3817 | 10 |
| regionizer.split.minDeadPercent=10 | 83.0025 | 81.8117 | 84.03 | 10 |
| regionizer.split.minDeadPercent=20 | 63.9673 | 62.8317 | 65.4167 | 10 |
| regionizer.split.minDeadPercent=30 | 49.3888 | 48.8217 | 50.8017 | 10 |
| regionizer.split.minDeadPercent=40 | 38.4383 | 38.0633 | 39.265 | 10 |
| regionizer.split.minDeadPercent=50 | 30.8652 | 30.3017 | 31.5283 | 10 |
| regionizer.split.minAgeSeconds=0 | 53.4067 | 52.24 | 54.665 | 10 |
| regionizer.split.minAgeSeconds=1 | 55.0035 | 53.7917 | 56.1033 | 10 |
| regionizer.split.minAgeSeconds=2 | 56.4865 | 55.6667 | 57.9367 | 10 |
| regionizer.split.minAgeSeconds=5 | 57.7142 | 56.7967 | 59.69 | 10 |
| regionizer.split.minAgeSeconds=10 | 53.467 | 52.4967 | 54.6667 | 10 |

## Air layer

- **Recommended**: do not adopt: airLayer.enabled = false (measurement)
- **Label**: Recommendation
- **Status**: Recommended
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Air traffic at 2,000 players with and without the air layer. Adopt only if aircraft-caused merges (real merges, not absorptions) drop by at least 25% with no rise in fusion events.
- **Read from**: sweep `air-layer` (AirTraffic); metric `rates.mergesPerSecondByClass.Aircraft` from each run's summary.json; x `airLayer.enabled`
- **Margin**: rates.mergesPerSecondByClass.Aircraft 25.7955 → 22.1005, a 14.3% drop (needs ≥ 25%); rates.fusionsPerSecondByClass 0.0003 → 0
- **Merge counting**: Merge rates count real merges only, where both sides held an entity. Absorptions of entity-less regions are reported separately (summary.json `totals.absorptions`: mean 624344.25 per run, 571577 to 674933) and are not in the rate.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `airtraffic-s16-r16-seed5843011988881251843-fd712480`, `airtraffic-s16-r16-seed7620229681788273117-1c1545a0`, `airtraffic-s16-r16-seed3728128964537905102-414a011c`, `airtraffic-s16-r16-seed1906953110644082044-60e0edfd`, `airtraffic-s16-r16-seed17502585706050438786-bfc02089`, `airtraffic-s16-r16-seed4965958803928893316-26636417`, `airtraffic-s16-r16-seed14666344033508591874-4baf005f`, `airtraffic-s16-r16-seed8128550842455080494-c40d13b6`, `airtraffic-s16-r16-seed17568711540103687888-7b8c01c4`, `airtraffic-s16-r16-seed180498484486306318-06e35b21`, `airtraffic-s16-r16-seed5843011988881251843-d60c2d35`, `airtraffic-s16-r16-seed7620229681788273117-1e297289`, `airtraffic-s16-r16-seed3728128964537905102-345914cc`, `airtraffic-s16-r16-seed1906953110644082044-4ed78f9a`, `airtraffic-s16-r16-seed17502585706050438786-21997e25`, `airtraffic-s16-r16-seed4965958803928893316-4138f47d`, `airtraffic-s16-r16-seed14666344033508591874-f096486e`, `airtraffic-s16-r16-seed8128550842455080494-567dd35b`, `airtraffic-s16-r16-seed17568711540103687888-0eef8480`, `airtraffic-s16-r16-seed180498484486306318-10592392`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| false | 25.7955 | 25.005 | 26.4933 | 10 |
| true | 22.1005 | 21.51 | 22.7967 | 10 |

## Target fight size

- **Recommended**: 839.6 players (measurement)
- **Label**: Reading (open decision 2 is set by load tests)
- **Status**: Read
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Rule**: Baseline at 2,000 players at the chosen values. The p99 of the largest region's player count, as a reading for the Phase 1 sweep; open decision 2 is set by load tests.
- **Read from**: sweep `baseline-final` (Baseline); metric `metrics.largestRegionPlayers.p99` from each run's summary.json; x the cells' swept parameters
- **Margin**: a reading, not a threshold: the mean over 10 seeds, which range 825 to 849 players
- **Principle VI**: A reading for the Phase 1 sweep, recorded under Principle VI. Open decision 2 (target fight size) is set by load tests.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `baseline-s16-r40-seed5843011988881251843-61fcf353`, `baseline-s16-r40-seed7620229681788273117-fe70ef0e`, `baseline-s16-r40-seed3728128964537905102-cef9801c`, `baseline-s16-r40-seed1906953110644082044-9a96f198`, `baseline-s16-r40-seed17502585706050438786-b9fc3131`, `baseline-s16-r40-seed4965958803928893316-d16be019`, `baseline-s16-r40-seed14666344033508591874-1d9f1f93`, `baseline-s16-r40-seed8128550842455080494-ee821ec3`, `baseline-s16-r40-seed17568711540103687888-e1f7d3a6`, `baseline-s16-r40-seed180498484486306318-d603e12b`

| x | Mean | Min | Max | Seeds |
| --- | --- | --- | --- | --- |
| metrics.targetFightSize=1200 | 839.6 | 825 | 849 | 10 |

## Measured options

Reported side by side, with no recommendation (research D15). Each metric is the mean over the seeds that completed cleanly, with their min and max in brackets; runs that stopped at a timer fault or an invariant violation are counted separately.

### Merge gap 0 vs 1

- **Status**: Read
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Read from**: sweep `merge-gap` (Migration); one cell per value of `regionizer.mergeGap`; Interleaved scheduling in every run's manifest
- **About**: Migration at 2,000 players in interleaved mode with the merge gap at 0 and 1. The merge-gap rule is one of the issue's proposed changes to architecture v2, which needs a yes or no before sub-spec 10, so the memo reports it and does not recommend.
- **Merge counting**: Merge rates count real merges only, where both sides held an entity. Absorptions of entity-less regions are reported separately (summary.json `totals.absorptions`: mean 549391.75 per run, 531634 to 565679) and are not in the rate.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `migration-s16-r16-seed5843011988881251843-8cb8e8c1`, `migration-s16-r16-seed7620229681788273117-9ff314c2`, `migration-s16-r16-seed3728128964537905102-7960dd98`, `migration-s16-r16-seed1906953110644082044-f47bc70c`, `migration-s16-r16-seed17502585706050438786-a6023f74`, `migration-s16-r16-seed4965958803928893316-2f2327b4`, `migration-s16-r16-seed14666344033508591874-cb9668bd`, `migration-s16-r16-seed8128550842455080494-5bcfa8a2`, `migration-s16-r16-seed17568711540103687888-0362f6f9`, `migration-s16-r16-seed180498484486306318-ce7bed34`, `migration-s16-r16-seed5843011988881251843-2b4a7438`, `migration-s16-r16-seed7620229681788273117-22b34180`, `migration-s16-r16-seed3728128964537905102-dbf209ed`, `migration-s16-r16-seed1906953110644082044-bac9b3a1`, `migration-s16-r16-seed17502585706050438786-43d75a55`, `migration-s16-r16-seed4965958803928893316-e166c570`, `migration-s16-r16-seed14666344033508591874-c8e0a61d`, `migration-s16-r16-seed8128550842455080494-7147bf46`, `migration-s16-r16-seed17568711540103687888-55c0e4bb`, `migration-s16-r16-seed180498484486306318-1fb0f3ae`

| Metric | regionizer.mergeGap = 0 | regionizer.mergeGap = 1 |
| --- | --- | --- |
| Merges per second, every causing class | 37.372 (36.3883 to 38.42) | 35.3512 (34.7267 to 36.3583) |
| Splits per second | 19.1145 (18.8083 to 19.5167) | 19.947 (19.3517 to 20.43) |
| Fusions per second, every causing class | 0.3002 (0.2733 to 0.325) | 0.2868 (0.2383 to 0.3217) |
| Fusions | 180.1 (164 to 195) | 172.1 (143 to 193) |
| Runs stopped by a timer fault | 0 of 10 | 0 of 10 |
| Runs stopped by an invariant violation | 0 of 10 | 0 of 10 |

### Tick-counter vs absolute timers

- **Status**: Read
- **Population**: 2000 (`population.total`, as every run's manifest records)
- **Read from**: sweep `timer-mode` (Migration); one cell per value of `regionizer.timerMode`; Interleaved scheduling in every run's manifest
- **About**: Migration at 2,000 players in interleaved mode with tick-counter timers (the constitution's default) and absolute deadlines. Adopting absolute deadlines would need a constitution amendment, so the memo reports both and does not recommend.
- **Merge counting**: Merge rates count real merges only, where both sides held an entity. Absorptions of entity-less regions are reported separately (summary.json `totals.absorptions`: mean 559241.7 per run, 551683 to 565679) and are not in the rate.
- **Seeds**: 5843011988881251843, 7620229681788273117, 3728128964537905102, 1906953110644082044, 17502585706050438786, 4965958803928893316, 14666344033508591874, 8128550842455080494, 17568711540103687888, 180498484486306318
- **Runs**: `migration-s16-r16-seed5843011988881251843-8cb8e8c1`, `migration-s16-r16-seed7620229681788273117-9ff314c2`, `migration-s16-r16-seed3728128964537905102-7960dd98`, `migration-s16-r16-seed1906953110644082044-f47bc70c`, `migration-s16-r16-seed17502585706050438786-a6023f74`, `migration-s16-r16-seed4965958803928893316-2f2327b4`, `migration-s16-r16-seed14666344033508591874-cb9668bd`, `migration-s16-r16-seed8128550842455080494-5bcfa8a2`, `migration-s16-r16-seed17568711540103687888-0362f6f9`, `migration-s16-r16-seed180498484486306318-ce7bed34`, `migration-s16-r16-seed5843011988881251843-edb30be5`, `migration-s16-r16-seed7620229681788273117-d29d29be`, `migration-s16-r16-seed3728128964537905102-294176b0`, `migration-s16-r16-seed1906953110644082044-4dfd62c9`, `migration-s16-r16-seed17502585706050438786-f5374acf`, `migration-s16-r16-seed4965958803928893316-ed78d5e4`, `migration-s16-r16-seed14666344033508591874-672436d0`, `migration-s16-r16-seed8128550842455080494-f4c446c8`, `migration-s16-r16-seed17568711540103687888-9e4729f9`, `migration-s16-r16-seed180498484486306318-2509cc38`

| Metric | regionizer.timerMode = TickCounter | regionizer.timerMode = Absolute |
| --- | --- | --- |
| Merges per second, every causing class | 37.372 (36.3883 to 38.42) | 37.372 (36.3883 to 38.42) |
| Splits per second | 19.1145 (18.8083 to 19.5167) | 19.1145 (18.8083 to 19.5167) |
| Fusions per second, every causing class | 0.3002 (0.2733 to 0.325) | 0.3002 (0.2733 to 0.325) |
| Timers set | 12135.9 (11873 to 12272) | 12135.9 (11873 to 12272) |
| Timers fired | 11765.5 (11518 to 11906) | 11811.6 (11559 to 11942) |
| Runs stopped by a timer fault | 0 of 10 | 0 of 10 |
| Runs stopped by an invariant violation | 0 of 10 | 0 of 10 |
