# Data Model: Regionizer Simulator

Entities from [spec.md](spec.md), with fields, rules and state. The public API is in
[contracts/regionizer-api.md](contracts/regionizer-api.md).

## Core library

### Section

| Field | Type | Rule |
| --- | --- | --- |
| Key | (layer, x, y) | x and y in section units, inside the layer's rectangle |
| Index | int | dense over all layers, fixed for the topology's lifetime |
| Area | ushort | from the map. Layer 0 with no map has one area. |
| ClaimCount | int | ≥ 0. The number of entity claims covering the section. |
| Owner | region ID or 0 | Live = ClaimCount > 0. Dead = owned with ClaimCount = 0. |

### Region

| Field | Rule |
| --- | --- |
| Id | long, ascending by creation, never reused |
| State | Transient, Ready, Ticking or Dead. Legal moves in research D5. |
| CreatedAt | time base at creation. Age = Now − CreatedAt. A merge keeps the absorber's value. |
| TickCounter | incremented at each `TryMarkTicking`. Split children copy the parent's. |
| Sections | the indices it owns. It never grows while Ticking. |
| Entities | ascending entity IDs |
| Timers | timer records (see Timer) |
| PendingMergePartners | regions it must merge with once neither is ticking |

```text
          create(NewClaim/Split)              create(Transient)
                 │                                   │
                 ▼                                   ▼
    ┌────────► Ready ──TryMarkTicking──► Ticking   Transient
    │            │  ▲                      │ │        │
    │            │  └──MarkNotTicking──────┘ │        │ partner ends tick / merge done
    │  owes merge│     (no pending)          │        ▼
    │            ▼                           │      Ready
    └──────── Transient ◄──MarkNotTicking────┘
                 │        (still owes a ticking partner)
                 ▼
      Dead (absorbed, or no sections left; any non-ticking state)
```

### Entity

| Field | Rule |
| --- | --- |
| Id | int, unique in the run, assigned by the scenario |
| Class | Infantry, GroundVehicle, Aircraft, Traveller, CapturePoint or HeavyProjectile |
| Layer, X, Y, Z | metres. Z is used only for aircraft altitude (air layer). |
| FightTag | ≥ 0 for players in a fight, −1 otherwise |
| RegionId | the owner, or 0 while in transit (two-step teleport) |
| ClaimedSections | the current claim (research D1 and D2), in a buffer pooled per entity |
| CaptureOccupied | capture points only. Unoccupied means the claim is its own section only. |

A player is any infantry, ground vehicle, aircraft or traveller. Capture points and heavy projectiles
are not players. A heavy projectile claims nothing.

### Portal

`PortalId`, endpoints `A` and `B` (section keys), `Open`. The closed-state policy is a map-file
concern (validator).

### Timer

| Field | Rule |
| --- | --- |
| TimerId | int, ascending |
| EntityId | the entity it belongs to. It moves with the entity on a split. |
| Delay | > 0. In owner ticks (TickCounter mode) or base steps (Absolute mode). The simulator's synthetic timers draw delays in ticks and convert them to base steps in Absolute mode: delay × baseStepHz / tickHz. |
| SetAt | the owner's tick counter (TickCounter mode) or the time base (Absolute mode) |
| Deadline | SetAt + Delay. A merge adds absorber.TickCounter − absorbed.TickCounter in TickCounter mode. |

## Simulator

### Run configuration

The fields and their validation are in [contracts/config-and-cli.md](contracts/config-and-cli.md).
The key rules:

- the section size is a power of two from 1 to 1024;
- radii are ≥ 0, and speed ceilings are > 0;
- the merge gap is 0 or 1;
- 0 ≤ *p*dead ≤ 100, *N*min ≥ 1, *T*min ≥ 0;
- map sides are 500–20,000 m;
- the population is 0 to `population.limit`;
- the sampling intervals are ≥ 1;
- the 20 Hz drop probability is in [0, 1], and drop tick bounds satisfy 1 ≤ min ≤ max;
- the scenario is one of the nine names;
- a map file, when given, must load and pass the portal validator.

### Scenario layout (defaults)

| Scenario | Layout | Key parameters (defaults) |
| --- | --- | --- |
| Baseline | Map with 3 lanes and 4 bases per lane. Fights at 7 bases. | fights `[1200,800,800,500,400,300,300]`; 500 travellers; 200 point-to-point aircraft; composition 80% infantry, 15% ground vehicle, 5% aircraft; fight radius 8 m × √players |
| Spacing | Two bases on one lane | spacing 500 m (swept 100–1,000); fights `[800,800]` |
| Lane traffic | Two bases joined by one lane | lane 600 m; travellers 60 (swept 10–200); placement `Clumped` or `Uniform`; fights `[500,500]` |
| Air traffic | Baseline | point-to-point aircraft 200 (swept); air layer on or off |
| Vehicle columns | Baseline fights | 4 columns of 12 vehicles, 20 m apart, at 20 m/s between fights |
| Stacked areas | `simulator/maps/stacked-base.json` | surface fight 600 above underground fight 300; door cycle 60 s, open 20 s |
| Migration | Baseline | each fight moves to the next base on its lane every 120 s; 30% of its infantry use Instant Action teleports |
| One base | One base | one fight of 5,000 |
| Soak | Migration + Stacked areas + Air traffic | interleaved; 2,592,000 ticks (24 h); metrics every 30 ticks |

`population.total` scales every count in proportion: each fight is rounded to the nearest integer,
and the remainder goes to the first fight. There is one exception. Lane traffic keeps its travellers
and aircraft as exact counts, because the traveller count is the swept traffic level, and scales
only its two fights, to `population.total` minus the travellers and aircraft. `population.total`
must hold them. A total of 2,000 gives the constitution-cap reading of
Baseline.

### Movement models

All directions come from rejection sampling in the unit disc. Speeds are U[0.5, 1] × the class
ceiling unless stated otherwise.

| Class | Model |
| --- | --- |
| Infantry (fight) | Every U[2, 10] s, picks a waypoint inside the fight radius: half the time within 40 m of an objective, otherwise uniform. Walks to it. Redeploys (two-step teleport) with a per-second probability. |
| Ground vehicle (fight) | Waypoints on the annulus from the fight radius to fight radius + 150 m, at U[0.3, 1] × the ceiling |
| Aircraft (fight) | Straight strafing passes through the fight's centre, entering 800 m out and leaving on the far side. Altitude 50 m over the fight and 300 m otherwise. |
| Aircraft (point-to-point) | Flies between random bases at 300 m altitude |
| Traveller | Moves along its lane between bases and turns around at the end. Half move at the infantry ceiling and half at the ground-vehicle ceiling. |
| Capture point | Static. Occupied while any infantry of its fight is within the zone radius. |

### Run, sweep and memo

The run folder is in [contracts/run-output.md](contracts/run-output.md). A sweep is a set of
(cell, seed) runs with a cell table. The memo has one row per value, as in research D15.
