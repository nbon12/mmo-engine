# Research: Regionizer Simulator

Phase 0 decisions for [plan.md](plan.md). Each entry gives the decision, why, and what else was
considered. Numbers are estimates until a run measures them (Principle VI).

## External design sources (Principle VII)

| Source | Used for | How |
| --- | --- | --- |
| [Folia: Region logic](https://docs.papermc.io/folia/reference/region-logic/), PaperMC docs, read 2026-11-03 | Region states, the four invariants, sections as power-of-two tiles, merge-later against ticking regions, tick-end order, recalculation count and max dead section percent | Published documentation only. No Folia source code was read, copied or translated. |
| Issue nbon12/mmo-engine#13, "Pre-Phase 1 Spec", sub-spec 1 | Claims per class, anchoring modes, merge gap, split damping, scenarios, sweeps, metrics, analytic formulas | The project's own spec |
| `.specify/memory/constitution.md` 1.1.0, Architecture Constraints | Invariants 1–4, tick counters with merge offsets, portals, open decisions | Binding rules |
| [.NET vectorization guidelines](https://github.com/dotnet/runtime/blob/8323e58d7b587deceb7ef39e61725bce7bfe86f8/docs/coding-guidelines/vectorization-guidelines.md) | Recording hardware-intrinsic settings in the manifest | Documentation |

What Folia's page does not cover, and so is our own design: claims per entity class in place of
chunk holders and the empty-section creation radius, the merge gap in place of the merge radius,
section and position anchoring, islands defined by shared claims, split callbacks, timers, layers and
portals.

## D1. Claim geometry

**Decision**: A claim is the set of sections whose square lies within distance *r* of an anchor.
In position-anchored mode the anchor is the entity's exact position. In section-anchored mode it is
the entity's whole section square, so the claim is the rounded square around that section. The
distance is the Euclidean distance between point (or square) and square, in the horizontal plane.

**Rationale**: This is the geometry that makes the issue's merge-distance formula exact. Two
entities within rA + rB always share a section, because some point lies within rA of one and rB of
the other, and both claims include its section. Sharing a section bounds their distance by
rA + rB + √2·s in position mode (k = 1: the shared section's diagonal) and rA + rB + 3√2·s in
section mode (k = 3: both entities' section diagonals plus the shared one). A merge gap of one
section adds at most √2·s. The analytic check (US6) tests both bounds directly.

**Alternatives**: A disc around the section centre in section mode is cheaper, but it breaks the
lower bound: two entities within rA + rB could miss each other. A square (Chebyshev) claim
over-claims at the corners and changes k.

## D2. Claim reachability, areas and portals

**Decision**: A claim spreads from the entity's own section in eight-neighbour steps between sections
of the same layer and the same area tag, and through open portals. It only visits sections inside the
claim geometry (D1). If a claim's bounding box holds a single area and no portal endpoint, a fast
rectangle scan is used. Otherwise a breadth-first walk is used. A claim never crosses an area or
layer boundary except through an open portal.

**Rationale**: This implements "reached from its own section without crossing a closed portal" and
the issue's proposed "claims cross area boundaries only through open portals". The open world (one
area, no portals) stays on the fast path.

**Alternatives**: Following the map's navigable adjacency for claims. That would let an undeclared
walkway leak claims silently. Instead, the portal validator rejects such maps (D11), and the
simulator refuses to run a map that fails validation.

## D3. Merge rule and merge gap

**Decision**: When an entity's claim adds a section S, the regions that must join the entity's region
are the owner of S (if any, live or dead), plus, with a merge gap of 1, the owners of the
eight-neighbours of S reachable under D2. A merge runs at once when neither region is ticking.
Otherwise the pair is recorded as a pending merge and runs at the first tick end where both regions
are not ticking. The lower region ID absorbs the higher, so the older region keeps its age.

**Rationale**: Claims are the only source of section ownership, so checking on each claim increment
is enough. Whichever of two adjacent sections became owned later finds the other. Folia's merge-later
covers both orders of tick ends: a region that still owes a merge to a ticking region becomes
transient, and is absorbed when that region's tick ends.

**Alternatives**: Merging only on live sections. Folia counts every owned position, and dead
sections disappear at the next split check anyway, so we follow Folia.

## D4. Tick end and split damping

**Decision**: At `MarkNotTicking`:
1. Merge with every pending partner that is not ticking.
2. If a partner is still ticking, become transient.
3. Otherwise become ready. If the region owns at least *N*min sections, at least *p*dead percent of
   them are dead, and its age is at least *T*min, drop the dead sections and compute islands. Islands
   are found by union-find over sections: sections claimed by the same entity are joined, and with
   gap 1 so are eight-neighbour live sections. Each island becomes a child region with the parent's
   tick counter. The largest island, ties broken by lowest section index, keeps the parent's ID.

Dead sections are dropped only when the split check runs, as in Folia.

**Rationale**: Folia's order and damping (recalculation count and max dead section percent), plus the
issue's minimum age. Keeping the parent's ID on the largest child keeps region IDs stable through
small splits, and the tie-break keeps it deterministic.

## D5. Region states

**Decision**: There is an enum with Transient, Ready, Ticking and Dead, and one transition function.
It allows Transient→Ready, Ready→Ticking, Ready→Transient, Ticking→Ready, Ticking→Transient, and
{Transient, Ready}→Dead. Anything else returns false and changes nothing. Observers receive every
transition, and the invariant monitor re-checks each one.

## D6. Timers

**Decision**: Timers belong to regions and reference an entity. A merge concatenates timer lists and
a split hands each timer to its entity's child region.
- **Tick-counter mode** (the default, as the constitution requires): a timer stores a deadline in its
  region's tick counter. The counter increments at tick start, and timers due at or below the counter
  fire during the tick. A merge adds the counter difference to the absorbed region's timers; a split
  copies the parent's counter to each child.
- **Absolute mode**: a timer stores a deadline on the continent time base and fires at the first tick
  of its owning region at or after that time.

The timer monitor keeps an independent shadow: per timer, it counts ticks of the timer's current owner
(tick mode) or tracks the owner's tick times (absolute mode), and checks every firing against it.

**Rationale**: The constitution mandates tick counters with merge offsets. The issue recommends
absolute deadlines. Building both measures the difference without settling it. Adopting absolute
deadlines would need a constitution amendment.

## D7. Scheduling and the time base

**Decision**: The continent time base is an integer count of base steps at 60 Hz. A region ticking at
30 Hz is due every 2 steps, and at 20 Hz every 3.
- **Lockstep**: every 2 steps, all ready regions start in ID order, each runs its tick body, then all
  end in ID order.
- **Interleaved**: at each base step, the due regions' start and end events are shuffled into a random
  valid interleaving by the scheduling stream. A region's body runs at its start event, so other
  regions are ticking while it moves. At each tick end, a region drops to 20 Hz with probability
  *p*drop for a random number of ticks, both drawn from the scheduling stream.

The tick body runs location-queue arrivals for the region, synthetic movement with *dt* = period/60 s,
claim updates and due timers. Portal toggles and arrivals into unowned locations apply at the step
boundary, when no region is ticking. A merge always happens at or after a tick end, so the
constitution's "merge from the next tick end" holds.

**Rationale**: Integer time keeps both modes exact and hashable. Running the body at the start event
is what makes merges against ticking regions happen in interleaved mode.

## D8. Determinism

**Decision**:
- A SplitMix64-seeded xoshiro256** generator, implemented in our own code. A stream's seed is
  SplitMix64(run seed XOR FNV-1a-64(stream name)). Each entity's movement stream is derived from
  (seed, "movement", entity ID), so a new draw for one entity never shifts another.
- Entities and regions are iterated in ID order, from lists kept sorted.
- Positions are doubles computed only with +, −, ×, ÷ and square root, which are correctly rounded
  under IEEE 754. There are no transcendental functions in the simulated path. Random directions use
  rejection sampling in the unit disc, and turning uses a rational rotation. A source-scan test bans
  `Math.Sin|Cos|Tan|Atan|Exp|Log|Pow` and `MathF.` in the core and simulator movement code.
- The state hash is XxHash64 over, for each region in ID order: ID, state, tick counter, sorted
  section keys, sorted entity IDs, and timers sorted by ID. It is hashed per tick and chained, so a
  row written every *n* ticks still covers all of them.

**Rationale**: FR-016, FR-017 and FR-019, and the clarification that hashes must match on one
machine with the design open to matching across machines. Cross-machine golden runs are sub-spec 2.

## D9. Bookkeeping cost

**Decision**: `Stopwatch.GetTimestamp` wraps every regionizer call in a step. Serialized cost covers
the operations that touch more than one region and would hold the regionizer's lock in the server:
merges, splits, transient-region creation and claims landing on another region's section. Total cost
is everything. Both are recorded per step and summarized at p50, p95, p99 and max against the
configured budget (1 ms p99, an estimate).

## D10. Metric sampling

**Decision**: Most metrics are recorded every tick. Two are sampled at a configurable interval:
island detection for split latency (default every tick) and cross-region pairs (default every 30
ticks, using a uniform grid of 1,000 m cells). This is because their per-tick cost grows with fight
size squared. The interval is in the manifest, and split latency is reported at that resolution.

## D11. Map file and portal validator

**Decision**: A versioned JSON map holds:
- the section size;
- per layer, a grid with area tags (rectangles over a default area) and blocked, non-navigable
  sections;
- explicit navigable adjacency (walkways, ramps, stairs), plus implicit adjacency between
  eight-neighbour navigable sections of the same area;
- interaction openings such as windows;
- portals as section pairs with an open flag and a closed-state policy (entity transfers, door and
  objective events, deferred projectiles);
- areas flagged sealed.

The validator reports these findings:
- **Leak**: an explicit adjacency or opening that crosses an area or layer boundary but matches no
  portal.
- **DeadPortal**: a portal endpoint that is outside its layer or blocked.
- **MissingPolicy**: a portal without a closed-state policy.
- **SealingFailure**: with all portals closed, a breadth-first walk over navigable adjacency and
  openings from a sealed area reaches another area.

Exit code 0 means no findings, 1 means findings, 2 means an unreadable map.

## D12. Configuration and validation

**Decision**: Runs are configured by JSON files read with System.Text.Json into option records. They
are validated with FluentValidation before anything runs, and any failure exits with code 2, naming
the field. The environment name (`DOTNET_ENVIRONMENT`) is checked against Development, Dev, Test,
Staging and Production, defaulting to Development. All tuning values live in configuration, and the
code has no tuning constants. The config hash is XxHash64 of the effective configuration serialized
canonically.

## D13. Dependencies (Principle VII)

| Package | Version | License | Use |
| --- | --- | --- | --- |
| FluentValidation | 12.2.0 | Apache-2.0 | Configuration validation |
| System.IO.Hashing | 10.0.14 | MIT | XxHash64 |
| System.CommandLine | 2.0.14 | MIT | CLIs |
| Serilog, Serilog.Sinks.Console, Serilog.Formatting.Compact | 4.4.0, 6.1.1, 3.0.0 | Apache-2.0 | Structured JSON logs |
| xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | 4.0.2, 4.0.1, 18.11.0 | Apache-2.0, Apache-2.0, MIT | Tests |
| CsCheck | 4.9.1 | Apache-2.0 | Property-based tests (5.x targets .NET 10 only) |
| coverlet.collector | 10.1.0 | MIT | Coverage |
| BenchmarkDotNet | 0.15.8 | MIT | Benchmarks |

FsCheck was rejected because it is BSD-3-Clause, which is not on the approved list.

## D14. SDK

**Decision**: Pin .NET 9 (`global.json` 9.0.300, `rollForward: latestPatch`). The constitution says
the SDK SHOULD be the current LTS (.NET 10), but it isn't installed on the machine running this
unattended work, and installing it needed an approval that didn't arrive. Moving to .NET 10 is
recorded as a follow-up. It's a `global.json` and `TargetFramework` change.

## D15. Reading the results (memo rules)

| Value | Scenario and sweep | Rule (margins configurable) |
| --- | --- | --- |
| Claim radius per class | Baseline, radius swept per class | The collapse radius is the smallest radius whose mean over seeds of p50 fused-fight count is at least 1. The recommendation is the largest radius at least one sweep step below collapse and at or above the analytic floor. If the floor is above that, the row is flagged as a conflict. |
| Section size | Baseline and One base, section size swept | The smallest size whose bookkeeping p99 is within budget in both |
| Claim anchoring | Baseline, both modes at equal bookkeeping cost | The mode with the lower fusion-event rate |
| Minimum base spacing | Spacing, spacing swept | The smallest spacing whose oversized-fusion ticks are under 1% in every seed, plus a sprawl margin |
| Lane bridging limit | Lane traffic, travellers swept | The largest traffic level whose bridging (fight-fusion) ticks are under 1% |
| Split damping | Migration, *N*min, *p*dead and *T*min swept | The lowest merge-plus-split rate with split latency p99 under 5 s |
| Air layer | Air traffic, with and without the layer | Adopt only if aircraft-caused merges drop by at least 25% with no rise in fusion events |
| Target fight size | Baseline at the chosen values | p99 of the largest region's player count. A reading for the Phase 1 sweep; open decision 2 is set by load tests. |

Every row records the curve (x, mean, min and max over seeds), its margin, the seeds and the run IDs,
and is labeled a measurement. Section size and claim radius rows are recommendations recorded under
Principle VI (open decision 1).
