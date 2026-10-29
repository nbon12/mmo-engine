# PS2-Style MMOFPS — Server Architecture Spec

**Sep 30, 2026 · @Nicholas Bonilla**

> **About this copy.** Imported on 2026-10-28 as the design reference for the project
> constitution (`.specify/memory/constitution.md`). The constitution governs: it carries the
> binding rules derived from this spec. This document keeps the rationale, estimates, alternatives
> and sources. Two embedded diagrams in the original (the architecture overview,
> `node/333188a6-0ab8`, and the roadmap, `node/3f4a1d4f-000b`) were not included in the export
> and appear below as placeholders. Links were normalized from the export; the text is otherwise
> unchanged.

## Overview

Each continent runs as one server process that splits itself into regions around active fights and ticks those regions in parallel. Progression and social data live in an Orleans tier over PostgreSQL, and Agones runs the continent servers as a fleet.

The target is PlanetSide 2-scale play, with continents of up to 2,000 concurrent players and several base fights at once. Combat mixes infantry, ground vehicles and aircraft, and weapons fire projectiles with travel time.

### Goals

* Use every core of a continent's host, with no locks in gameplay code.
* Keep fights free of fixed server seams; region boundaries move with the players.
* Isolate load, so an overloaded fight slows only itself.
* Keep progression, outfits, friends and chat global across continents.
* Depend only on permissively licensed code; GPL and AGPL projects are reference reading.

### Non-goals

* Simulating one continent across several machines.
* Splitting one fight across machines. Parallel phases raise a single fight's ceiling on one host, and load tests measure it.
* Console clients, which need a native client and are deferred.

## Assumptions and constraints

The server is C# on .NET, deployed as Linux containers, and the first client is a TypeScript browser app.

| Area               | Assumption                                                                                                |
| ------------------ | --------------------------------------------------------------------------------------------------------- |
| **Server**         | C# on .NET, one process per continent, Linux containers                                                   |
| **Client**         | TypeScript in the browser; a native client for consoles comes later                                       |
| **Continent size** | Up to 2,000 concurrent players                                                                            |
| **Host**           | One continent per host, at least 16 physical cores, as [Folia's FAQ](https://docs.papermc.io/folia/faq/) recommends for its model |
| **Tick rate**      | 30 Hz per region as the starting target                                                                   |
| **Weapons**        | Projectiles with travel time; instant resolution only at close range                                      |
| **Database**       | PostgreSQL                                                                                                |

Dependencies must be MIT or Apache 2.0 licensed: [Orleans](https://www.microsoft.com/en-us/research/project/orleans-virtual-actors/) is MIT and [Agones](https://github.com/agones-dev/agones) is Apache 2.0. [Folia](https://github.com/PaperMC/Folia) (GPL-3.0) and [AzerothCore](https://github.com/chromiecraft/azerothcore-wotlk) (GPL-2.0) are design references only. The regionizer is written from Folia's published docs, never from its source.

## Architecture overview

Six components make up the system, and only the continent servers run the real-time simulation.

[embed: node/333188a6-0ab8 — architecture diagram, not included in this copy]

Players hold two connections: game traffic goes to their continent server, and everything else goes through the gateway. Only Orleans writes to PostgreSQL.

## Continent server: regionized simulation

A continent server cuts its map into square sections and groups them into regions that tick in parallel. The design follows Folia's [overview](https://docs.papermc.io/folia/reference/overview/) and [region logic](https://docs.papermc.io/folia/reference/region-logic/) docs, reimplemented in C#.

### Sections and regions

The map is a grid of fixed square sections, sized as a power of two. Regions own sections, not entities, so regionizer work happens only when a section gains or loses activity. Folia groups chunks the same way to cut regionizer operations.

Each region owns a private data object: its entities, spatial index, projectiles, base capture state, timers and pose history. Only work scheduled by that region's tick may touch it, so gameplay code needs no locks.

### Invariants

The regionizer keeps four rules, adapted from Folia's:

1. Every live section belongs to exactly one region.
2. Every section within an entity's claim radius, without crossing a closed portal, belongs to that entity's region.
3. A ticking region never grows. New activity beside it forms a transient region that merges in when the tick ends.
4. A region is always transient, ready, ticking or dead.

Rule 2 replaces Folia's single merge radius with a per-class claim radius, covered under Shooter adaptations, and adds portals, covered under Designed boundaries.

### Merge and split

* **Merge:** regions whose claims touch must merge. If one is ticking, the merge waits until its tick ends.
* **Split:** at tick end, after pending merges, a region holding separate islands splits into independent regions.
* **Damping:** a split is only considered once a region has enough sections and enough dead ones, like Folia's recalculation thresholds. This limits merge/split churn.
* **Callbacks:** region data merges by concatenating entities and timers, and splits by partitioning them by position.

### Designed boundaries

Level designers place portals, such as doors, elevators, tunnel mouths and airlocks, between groups of sections. Claims don't pass through a closed portal, so regions on either side never merge however close they are. Opening a portal connects the two sides, which can trigger a merge when the current tick ends.

Each boundary declares what may cross while it is closed: entity transfers, door and objective events, and deferred projectiles. Visibility doesn't cross a closed portal either, so a sealed underground fight stays independent of the surface above it.

### Scheduling

A fixed pool of tick threads runs the regions. Each region schedules its own next tick, and the pool starts the earliest one first, as Folia's scheduler does. A region that overruns its budget delays only itself, and it can drop to 20 Hz rather than stall.

Tick counters are per region. On a merge, the absorbed region's timers get an offset so relative deadlines survive; on a split, each child inherits the parent's counter. Respawn, capture and cooldown timers stay correct through both.

### Parallel phases inside a region

A region too busy for one thread spreads its tick across the pool in phases, so one fight can use many cores. Each phase gives every entity exactly one writer, and workers read the previous tick's state.

1. **Movement:** each worker integrates its own slice of players and vehicles.
2. **Shots:** each worker sweeps its own slice of projectiles, or validates its slice of reported hits.
3. **Effects:** damage, heals and kill credit go into per-target queues.
4. **Apply:** the worker that owns each target applies that target's queue.
5. **Gameplay:** captures, timers and objectives run once, on one thread.

Phases switch on only above a region-size threshold set by load tests, because coordination costs more than it saves in small regions.

### Global region

One global region per continent ticks on its own schedule and never merges or splits. It owns state tied to no place: lattice links, base ownership, continent lock and alerts, population caps and faction balance. Regions read a copy of it taken at the start of each tick and change it only through the global queue.

### Area state

State tied to a designed area, such as its doors, objectives, hazards and timers, has a single owner: an area actor scoped like the global region. Regions read a copy taken at the start of each tick and change it only through the area's queue.

### Cross-region work

Three queues carry work between regions, mirroring Folia's utilities:

* **Entity queue:** runs a task on whichever region owns an entity, following it through merges and moves.
* **Location queue:** runs a task on the next tick of the region that owns a location, creating that region if needed.
* **Global queue:** changes global-region state.

### Deploys and teleports

Every spawn, login, redeploy and Instant Action jump uses Folia's two-step teleport. The current region removes the entity and sets its new position, then the location queue adds it on the destination region's next tick. Instant Action picks its destination from population stats that regions publish each tick.

### Kept open for later: multi-server continents

Running one continent across several servers is deferred until profiling shows one host can't carry a continent. It would spread total load, but it can't split one fight. These invariants keep the option open:

* One authority per entity at any instant
* Stable entity IDs
* Player sessions independent of the worker simulating them
* Writes across regions only through messages
* One continent-wide time base

The Simulation Domains sub-spec is the starting point for that later work. Its designed boundaries and area state are adopted above; its dynamic domains are dropped, because region splitting already covers them.

## Shooter adaptations

Folia's model assumes short-range interactions, so every interaction here is classed by how fast it must land. Only instant interactions set region boundaries.

| Class         | Examples                                                                     | Resolved by                                                   | Added delay                         |
| ------------- | ---------------------------------------------------------------------------- | ------------------------------------------------------------- | ----------------------------------- |
| **Instant**   | Melee, close-range hits, heals, repairs, revives, collisions, capture points | The owning region, inside its tick                            | None                                |
| **Deferred**  | Projectiles that leave the firing region, artillery                          | The region that owns the impact point, via the location queue | Up to 1 tick, hidden in flight time |
| **Read-only** | Far visibility, targeting, spotting, minimap                                 | Any region, from the last published snapshots                 | 1 tick stale                        |

### Claim radius per entity class

Each entity claims the sections within its own instant-interaction radius. Infantry, ground vehicles and aircraft get separate radii, set from the simulator results. An aircraft whose guns fire projectiles claims only a small bubble, so it cannot bridge the fights it flies between.

Map design backs this up. Base spacing and terrain keep neighboring fights farther apart than the largest claim radius.

### Snapshots

At the end of every tick, each region publishes an immutable snapshot of its entities and the sections it owns. Each entry holds ID, class, faction, position, velocity and visible state. Readers use the latest complete snapshot, so reads never block the writer.

Snapshots are the only way a region sees another region's entities. This departs from Folia, which forbids cross-region reads.

### Cross-region messages

Anything that changes another region's entity, such as damage, travels as a message on the entity queue. The owner applies it at the start of its next tick. Messages from one sender arrive in order; there is no ordering across senders.

### Projectiles

* **Close range:** the firing region resolves hits instantly against its own entities.
* **In flight:** projectiles claim no sections. Otherwise one tank shell arcing between two fights would fuse their regions.
* **Over empty space:** the firing region keeps simulating the projectile across unowned sections, where only terrain can be hit.
* **Entering another region:** the firing region hands it over on the location queue. The new owner tests it against pose history at the projectile's true timestamp, so the hop adds delay but no positional error.

At 30 Hz a hop costs at most 33 ms, about 17 m of flight for a 500 m/s round.

### Bridging

The main threat to parallelism is an entity within claim reach of two fights, which fuses them into one region. Per-class claim radii and base spacing are the first defense. The largest region's share of players is tracked as a production metric.

## Interest management and network LOD

Delivery workers build each player's view from published snapshots, off the simulation threads, so view work never lengthens a region's tick. Each snapshot includes its region's cell index for nearby lookups.

### Cell grid

Each region indexes its entities in a grid of cells smaller than a section. As in [AzerothCore's map grid](https://www.azerothcore.org/doxygen/da/d7e/classMapGrid.html), each cell keeps a separate list of far-visible objects. Aircraft, vehicles and base structures go on that list, so long-range queries never scan infantry.

A player's view set is recomputed when the player crosses a cell boundary, not every tick. Each cell also keeps a list of the views watching it, so views update when other entities enter or leave.

### Visibility tiers

| Tier     | Contents                                 | Source                   | Update rate    | Cap in dense fights |
| -------- | ---------------------------------------- | ------------------------ | -------------- | ------------------: |
| **Near** | Everything inside the class's near range | Snapshot cell index      | Every tick     |        100 entities |
| **Mid**  | Infantry and vehicles past near range    | Snapshot cell index      | 10 Hz to start |        300 entities |
| **Far**  | Far-visible objects only                 | Snapshots of all regions | 2 Hz to start  |        600 entities |

View data comes from snapshots, so it is at most one tick old. Ranges, rates and caps per class are tuned in testing.

### Bandwidth budget

Each client gets a per-tick byte budget, filled near tier first, then mid, then far. When the budget runs out, far updates are dropped first. Updates are delta-encoded against the client's last acknowledged state.

### Dense fights

When a fight packs players inside each other's near range, distance stops limiting the view. Each tier then keeps only its most relevant entities, ranked by distance, threat, squad and what the player is aiming at.

| Policy at 1,000 players                                 | Per client | Server egress |
| ------------------------------------------------------- | ---------: | ------------: |
| Every entity at 30 Hz                                   |  ~240 KB/s |   ~1.9 Gbit/s |
| Tier caps: 100, 300 and 600 entities at 30, 10 and 2 Hz |   ~58 KB/s |  ~0.46 Gbit/s |

Both rows assume about 8 bytes per entity update; real figures come from load tests.

## Combat netcode

The server is authoritative for all combat. Clients predict only their own movement and weapon effects.

* **Prediction:** the client applies its own inputs at once and reconciles when server state arrives.
* **Lag compensation:** each region keeps a pose history per entity, long enough to cover the highest supported latency. Close-range hits are tested against targets rewound to the shooter's view time.
* **History through merges:** pose history is region data, so it moves with its entities through merges and splits.
* **Projectiles:** simulated on the server with travel time. A projectile handed to another region keeps its true timestamp.
* **Hit confirmation:** the region that applies damage sends the result back to the shooter's region on the entity queue.
* **Tick rate:** 30 Hz per region to start. An overloaded region falls behind alone.
* **Transport:** the browser client needs WebSocket or WebTransport; the choice is open.

### Hit detection model

How hits are decided is an open decision, and it largely sets the cost of dense fights.

* **Server-simulated shots:** the server sweeps every projectile against rewound hitboxes. This is the most secure option and the most expensive at 1,000 players.
* **Client-reported hits with server validation:** the client claims a hit, and the server checks range, fire rate and line of fire, rewinding hitboxes only for claimed hits. Hits are a small fraction of shots, so this costs far less, but it gives cheaters more room.

## Persistence and social tier

Orleans grains hold live player and social state in memory, and PostgreSQL is the source of truth. Grains decide who may change a record and when; the database keeps it.

### Grain catalog

| Grain            | Key                   | Owns                                                    | Stored                    |
| ---------------- | --------------------- | ------------------------------------------------------- | ------------------------- |
| **Player**       | Character ID          | Profile, loadouts, progression counters, presence       | Own schema; stats batched |
| **Outfit**       | Outfit ID             | Roster, ranks, outfit stats                             | Own schema                |
| **Squad**        | Squad ID              | Members, leader, waypoints                              | Memory only               |
| **Chat channel** | Channel ID            | Members, message fan-out                                | Memory only               |
| **Continent**    | Continent instance ID | Population, faction split, server endpoint, heartbeat   | Memory only               |
| **World**        | World ID              | Continent directory, open and locked continents, alerts | Own schema                |

### Storage

* **Queryable data** (characters, outfits, rosters, stats and the currency ledger) lives in our own PostgreSQL schema. Grains read and write it through EF Core or Dapper.
* **Opaque state** can use Orleans' [ADO.NET provider](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence/relational-storage), which supports PostgreSQL. It stores each grain's state as one serialized payload, binary by default with JSON recommended, so it is not used for anything we query.
* **Orleans system tables** for cluster membership and reminders live in the same PostgreSQL instance.

### Write policy

| Data                       | Policy                                                                      |
| -------------------------- | --------------------------------------------------------------------------- |
| **Currency and purchases** | Written immediately, as append-only ledger rows                             |
| **Loadouts and settings**  | Written on change                                                           |
| **XP and stats**           | Batched per player on a fixed interval, at logout and on continent transfer |
| **Squads, chat, presence** | Never persisted                                                             |

A crash loses at most one batch interval of stats. Currency is never held only in memory.

### Concurrency

Grains are the only writers of live data, and admin tools change it through grain calls, not direct SQL. Rows carry a concurrency token (`xmin` in PostgreSQL), so a stale save fails instead of overwriting. Leaderboards and analytics read PostgreSQL directly and lag live state by up to one batch interval.

### Cross-player transfers

[Orleans transactions](https://learn.microsoft.com/en-us/dotnet/orleans/grains/transactions) span grains, but they need a transactional state store. The documented store is Azure Table, and falling back to regular grain storage is meant for development only. So trades run as one PostgreSQL transaction, driven by a short-lived trade grain.

The trade grain locks the items in both player grains, commits the SQL transaction, then tells both grains to apply it.

### Day-one alternative

A single ASP.NET Core service over the same schema can serve early development. Keeping one owner per player and per outfit in its code makes a later move to grains mechanical. Orleans comes in once write volume or cross-continent traffic outgrows one service.

### [Nakama](https://www.gamedeveloper.com/game-platforms/embracing-the-future-of-open-source-game-servers-with-nakama) option

[Nakama](https://www.gamedeveloper.com/game-platforms/embracing-the-future-of-open-source-game-servers-with-nakama) is Apache 2.0, written in Go, and runs on PostgreSQL or another Postgres-compatible database. It brings friends, groups, chat and leaderboards ready-made, at the cost of a second runtime to operate. Whether it replaces custom social grains is an open question.

## Orchestration

Each continent server runs as an [Agones](https://github.com/agones-dev/agones) GameServer in a Fleet on Kubernetes. [Agones](https://github.com/agones-dev/agones) (Apache 2.0) handles server lifecycle, health checks, connection details and fleet autoscaling tied to cluster autoscaling.

* **Placement:** one continent per dedicated node with at least 16 physical cores. No other workloads share the node, so tick budgets stay predictable.
* **Opening a continent:** the World grain requests an allocation from the fleet. The allocated server loads the map and registers its endpoint with its Continent grain.
* **Locking a continent:** the server stops accepting deploys, moves players out through the normal teleport path, flushes progression batches, then shuts down.
* **Failure:** the global region checkpoints base ownership to the World grain. A replacement server restores the map from it, and lost stats are bounded by the batch interval.
* **Everything else:** Orleans silos and the gateway run as ordinary Kubernetes Deployments, outside Agones.

## Key data flows

Five flows cross component boundaries; everything else stays inside one component.

### Login and first deploy

1. The client signs in through the gateway, which activates the Player grain.
2. The gateway asks the World grain for open continents.
3. The player picks a spawn. The gateway returns that continent's endpoint and a one-time join token.
4. The client connects to the continent server, which checks the token and fetches the loadout from the Player grain.
5. The server adds the player at the spawn point through the location queue, and the owning region starts sending updates.

### Instant Action

1. The client asks its current region for Instant Action.
2. The region picks a destination fight from the population stats in the latest snapshots.
3. The current region removes the player, and the location queue adds them to the destination region on its next tick.

### Progression events

1. Regions record XP and stat changes in region-local buffers during each tick.
2. A per-continent batcher aggregates them per player.
3. On each batch interval, at logout and on transfer, the batcher sends each Player grain one update.
4. The Player grain applies it in memory and persists it under the write policy.

### Chat and presence

1. The continent server sends its Continent grain a heartbeat with population and the player list.
2. The Continent grain updates each Player grain's presence.
3. Chat travels from the client to the gateway to a Chat channel grain, which fans it out to members' gateway connections.

### Continent lock

1. The global region detects the lock condition and tells the World grain.
2. The World grain opens the next continent through an Agones allocation.
3. The locking server drains its players to open continents and shuts down.

## Capacity and performance model

The binding limit is the largest single fight, because one fight is one region and can't be split across regions. Total continent population matters less.

| Reference point                                 | Value                                                                                         |
| ----------------------------------------------- | --------------------------------------------------------------------------------------------- |
| **PlanetSide 2 continent**                      | 64 km², up to 2,000 concurrent players ([Wikipedia](https://en.wikipedia.org/wiki/PlanetSide_2)) |
| **PlanetSide 2 highest stable continent count** | 1,241 players, 2022 ([Wikipedia](https://en.wikipedia.org/wiki/PlanetSide_2))                 |
| **Folia's pre-release test server**             | About 330 players at peak ([Folia FAQ](https://docs.papermc.io/folia/faq/))                   |
| **Region tick budget at 30 Hz**                 | 33 ms                                                                                         |

PlanetSide 2 has also put upwards of 1,200 players in a single location during special events. Whether one region can carry a fight that size is borderline, not ruled out. Parallel phases add margin, and the design still sets a target fight size and uses game design to spread players: base capacity limits, several live objectives per lane, and Instant Action steering.

### One base, 1,000 players

By estimate, a single 1,000-player fight spends between a third of a 30 Hz tick and all of it, before parallel phases. Per-player movement cost is the biggest unknown, so the Phase 1 sweep measures it first.

| Work per tick                                                              |      Lean |     Heavy |
| -------------------------------------------------------------------------- | --------: | --------: |
| Movement and collision: 1,000 players at 5–20 µs                           |      5 ms |     20 ms |
| Projectiles: about 400 in flight at 3–5 µs per sweep, plus spawns and hits |      2 ms |      3 ms |
| Vehicles: 100 at 20–50 µs                                                  |      2 ms |      5 ms |
| Captures, heals, revives, damage                                           |      2 ms |      5 ms |
| **Total, against a 33 ms budget**                                          | **11 ms** | **33 ms** |

These are estimates, not measurements.

### Threads

Networking, serialization and GC get reserved cores first. Following Folia's rule of thumb, total allocated threads stay under 80% of the host's cores, and the remainder become tick threads.

### Per-region efficiency

* Entities are stored as structs of arrays per region.
* Gameplay code makes no per-tick allocations; buffers and messages are pooled.
* Visibility and hit tests run as tight loops over contiguous data.
* GC pause time is tracked against the tick budget.

### Degradation order

When a region still overruns, it degrades in this order, and correctness always comes before tick rate:

1. Tighten that region's Delivery caps.
2. Drop that region from 30 Hz to 20 Hz.
3. Inside sealed designed areas only, slow game time with a time scale over the continent-wide time base.

Time dilation stays inside sealed areas. Across an open boundary, players would see the other side in slow motion, and crossing projectiles would need two clocks.

### Metrics

Every continent server reports per-region tick time, region count, the largest region's share of players, merge and split rate, cross-region message volume, snapshot size and bytes per client per tick.

## Risks and mitigations

The largest risk is density: one big fight is one region, and only parallel phases spread it across cores.

| Risk                                                 | Impact                                                                | Mitigation                                                                                         |
| ---------------------------------------------------- | --------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| **One fight outgrows a region's tick budget**        | That fight's tick rate drops                                          | Parallel phases, density caps, a cheaper hit model, target fight size, designs that spread players |
| **Bridging entities fuse separate fights**           | Parallelism collapses into one region                                 | Per-class claim radii, base spacing, terrain; alert on the largest region's share                  |
| **Merge and split churn**                            | Wasted work, timer jitter                                             | Split thresholds and a minimum region lifetime before a split                                      |
| **Snapshots are one tick old**                       | An entity that appeared this tick can be missed by cross-region reads | Keep cross-region interactions deferred; resolve hits at true timestamps                           |
| **GPL code leaking into the server**                 | License obligations on our code                                       | Implement from Folia's docs only; record design sources; never copy source                         |
| **.NET GC pauses**                                   | Tick overruns                                                         | Allocation-free gameplay code; GC pause time as a tracked metric                                   |
| **Browser transport over TCP**                       | Head-of-line blocking under packet loss                               | Evaluate WebTransport datagrams; WebSocket as fallback                                             |
| **Continent server crash**                           | Players dropped; up to one batch of stats lost                        | Checkpointed base ownership, fast replacement through Agones, currency written immediately         |
| **Orleans overhead too early**                       | Slower iteration                                                      | Start with ASP.NET Core over PostgreSQL; move to grains when load requires                         |
| **Browser client can't render a 1,000-player fight** | Frame rate collapses in the biggest fights                            | GPU instancing, animation LOD, simplified distant models                                           |
| **Client-reported hits, if chosen**                  | More room for cheating                                                | Server checks on range, fire rate and line of fire; review of statistical outliers                 |
| **A map's portal graph misses an opening**           | Areas meant to stay separate merge                                    | Validate each map's portal graph in the simulator                                                  |

## Validation plan and roadmap

The first deliverable is an offline regionizer simulator. Its results set the claim radii, base spacing and target fight size that the rest of the design depends on.

### Simulator

1. Lay out bases along lanes on a continent-sized map.
2. Place simulated players in several fights, plus aircraft and stragglers moving between them.
3. Run the regionizer every tick across a range of claim radii.
4. Plot region count and the largest region's share of players against claim radius.
5. Read the claim-radius budget and minimum base spacing off the point where region count collapses.

### Load tests

* Bots on one continent server measure per-region tick time against players in the region. That curve sets the target fight size.
* A named stress scenario puts 1,000 bots on one base. It passes if the region holds 30 Hz or degrades cleanly to 20 Hz.
* Door tests open and close portals mid-fight to check that merges and splits stay correct.
* A soak test drives repeated merges and splits to check timer correctness and churn.

### Roadmap

[embed: node/3f4a1d4f-000b — roadmap, not included in this copy]

Phase 1 deliberately runs one region per server, so netcode and interest management are proven before the regionizer adds parallelism. Parallel phases join Phase 2 if the Phase 1 sweep shows one thread can't hold the target fight size.

## Alternatives considered

Every option that promised spatial sharding out of the box is discontinued, experimental, unverified or tied to another engine.

| Option | What it offered | Why not chosen |
| ------ | --------------- | -------------- |
| [SpatialOS](https://en.wikipedia.org/wiki/Improbable_%28company%29) | Managed interest management and load balancing | Improbable moved to metaverse and blockchain work (MSquared, Somnia) |
| [AWS SimSpace Weaver](https://docs.aws.amazon.com/simspaceweaver/latest/catalog/intro.html) | Spatial simulations split across EC2 instances | Support ended May 20, 2026 |
| [SpacetimeDB](https://runtimewire.com/article/spacetimedb-october-31-scaling-plan-single-thread) | Built-in query subscriptions, C# modules, TypeScript client | Each database runs single-threaded and sharding is manual; cross-database calls are only planned for October 31, 2026 |
| [Unreal MultiServer Replication](https://dev.epicgames.com/community/learning/knowledge-base/xBvk/unreal-engine-experimental-an-introduction-to-the-multiserver-replication-plugin) | A proxy fanning clients out to several servers | Experimental plumbing; requires an Unreal client and server |
| [coherence](https://docs.coherence.io/overview/features) | Multiple simulators with authority handover | Unity-only SDK |
| [OpenSimulator](https://github.com/nebadon2025/opensimulator) | C#, BSD license, regions on separate servers | Fixed square regions put server borders through fights and vehicle routes |
| [MultiPaper](https://github.com/MultiPaper/ShreddedPaper) | One world shared across servers by chunk ownership | Its team's newer project, ShreddedPaper, multithreads a single server instead |
| [KBEngine](https://github.com/kbengine/kbengine/releases) | BigWorld-style cells with dynamic load balancing | Cross-process space sharing unconfirmed; docs mostly in Chinese |

## Open questions, glossary and sources

Nine decisions remain open; the simulator and load tests settle the first four.

### Open questions

* Section size and claim radius per entity class, from the simulator.
* Target maximum fight size, from load tests.
* Tick rate: stay at 30 Hz, or go higher?
* Browser transport: WebTransport datagrams or WebSocket?
* Hit detection: server-simulated shots, or client-reported hits with server validation?
* Which bases get designed boundaries, and which portals can players open?
* Orleans from day one, or ASP.NET Core over PostgreSQL first?
* Nakama or custom grains for friends, groups and chat?
* Anti-cheat approach for a browser client.

### Glossary

| Term                   | Meaning                                                                                               |
| ---------------------- | ----------------------------------------------------------------------------------------------------- |
| **Section**            | Fixed square map tile; the unit regions own                                                           |
| **Region**             | A set of sections with private data, ticked with one writer per entity                                |
| **Claim radius**       | Distance around an entity whose sections must share its region                                        |
| **Global region**      | Continent-wide state tied to no place; never merges or splits                                         |
| **Snapshot**           | Immutable end-of-tick copy of a region's entities and sections                                        |
| **Entity queue**       | Delivers a task to whichever region owns an entity                                                    |
| **Location queue**     | Delivers a task to the region owning a location, on its next tick                                     |
| **Far-visible object** | An entity kept on a separate list for long-range visibility                                           |
| **Grain**              | An Orleans virtual actor, addressed by ID                                                             |
| **Fleet**              | An Agones set of identical game servers                                                               |
| **Portal**             | A designed opening between groups of sections; claims and visibility don't cross it while closed      |
| **Area actor**         | Single owner of a designed area's state, scoped like the global region                                |
| **Parallel phase**     | One step of a busy region's tick, run across threads with one writer per entity                       |
| **Delivery**           | Workers that build each player's view from snapshots, off the simulation threads; never authoritative |

### Sources

* Folia: [overview](https://docs.papermc.io/folia/reference/overview/), [region logic](https://docs.papermc.io/folia/reference/region-logic/), [FAQ](https://docs.papermc.io/folia/faq/), [license](https://github.com/PaperMC)
* AzerothCore: [README and license](https://github.com/chromiecraft/azerothcore-wotlk), [MapGrid reference](https://www.azerothcore.org/doxygen/da/d7e/classMapGrid.html)
* Orleans: [project page](https://www.microsoft.com/en-us/research/project/orleans-virtual-actors/), [ADO.NET persistence](https://learn.microsoft.com/en-us/dotnet/orleans/grains/grain-persistence/relational-storage), [transactions](https://learn.microsoft.com/en-us/dotnet/orleans/grains/transactions)
* [Agones on GitHub](https://github.com/agones-dev/agones)
* [Nakama overview, Game Developer](https://www.gamedeveloper.com/game-platforms/embracing-the-future-of-open-source-game-servers-with-nakama)
* [PlanetSide 2, Wikipedia](https://en.wikipedia.org/wiki/PlanetSide_2)
* [SpacetimeDB scaling plan, RuntimeWire](https://runtimewire.com/article/spacetimedb-october-31-scaling-plan-single-thread)
* [Improbable, Wikipedia](https://en.wikipedia.org/wiki/Improbable_%28company%29)
* [AWS SimSpace Weaver documentation](https://docs.aws.amazon.com/simspaceweaver/latest/catalog/intro.html)
* [Unreal MultiServer Replication, Epic knowledge base](https://dev.epicgames.com/community/learning/knowledge-base/xBvk/unreal-engine-experimental-an-introduction-to-the-multiserver-replication-plugin)
* [coherence features](https://docs.coherence.io/overview/features)
* [OpenSimulator README](https://github.com/nebadon2025/opensimulator)
* [ShreddedPaper README](https://github.com/MultiPaper/ShreddedPaper)
* [KBEngine releases](https://github.com/kbengine/kbengine/releases)
