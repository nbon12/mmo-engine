# Self-hosted, multi-node SpacetimeDB compared with the chosen architecture

**2026-11-06 · analysis only, no decision**

The question: could we self-host SpacetimeDB on several nodes instead of using the chosen design,
and write our own layer to make the instances work together, so that we don't need the paid
multi-node product?

This note settles no open decision and doesn't change the architecture spec
(`docs/architecture/server-architecture.md`). It does update one fact in that spec's Alternatives
table: async cross-database calls shipped on 2026-10-30, in SpacetimeDB v2.14.0.

Sources read on 2026-11-06: SpacetimeDB's `LICENSE.txt` on `master` (Licensed Work 2.15.0), the
docs folder in its repository, and its v2.14.0 release notes. spacetimedb.com (the pricing page and
the scaling blog) couldn't be reached from the session that wrote this note, so the claims about
pricing come from search-result summaries and are marked as such. This is a reading of the license
text, not legal advice.

## Short answer

1. **Writing our own clustering layer doesn't get around the license.** The free production
   grant counts instances. It allows use "with no more than one SpacetimeDB instance in
   production". N self-hosted instances joined by our code are still N instances. A fork with
   clustering patched in is still the Licensed Work. More than one instance needs a commercial
   agreement with Clockwork Labs, no matter who wrote the clustering.
2. **The constitution rules it out first.** The SpacetimeDB server and its C# module library are
   under the Business Source License 1.1 (BSL), which later converts to AGPL-3.0 with a linking
   exception. Principle VII allows only MIT and Apache-2.0 code, plus named exceptions, and keeps
   copyleft out of hosted environments. Adopting SpacetimeDB at any node count needs an amendment,
   and amendments are owner-merged.
3. **Licensing aside, it fits the continent simulation worse.**
   - Each database runs its transactions one at a time. One fight would run on one thread, with no
     parallel phases.
   - Regions that move with the fight would mean moving rows between databases.
   - Clients connect over WebSocket only.
   - Every write goes through the commit log, even an event that's broadcast and then deleted.
   - C# modules can't make cross-database calls yet, C# procedures are marked unstable, and the
     unit-test harness supports only Rust and TypeScript.
4. **The custom layer amounts to building the deferred multi-server continent first.** It needs
   exactly-once messaging between servers, a distributed regionizer, session routing, snapshots
   shared across databases, a shared time base, and probably failover. The chosen design keeps
   those in one process's memory. The constitution lists running one continent across several
   machines as a non-goal.

**Recommendation:** keep the chosen architecture. Revisit it only if one of the triggers under
[What would change this](#what-would-change-this) happens.

## The option being compared

- **Nodes:** N hosts, each running a standalone SpacetimeDB server (`spacetime start`).
- **Databases on each host:** one per region shard or fixed zone, one global database per
  continent, and persistence databases for players, outfits and worlds. SpacetimeDB's docs say
  "Many databases can run on a single host".
- **Our federation layer:** relays that carry calls between instances, a map of which database owns
  each shard, and a client router.
- **Clients:** the TypeScript browser client, using SpacetimeDB's client SDK and connected to one or
  more databases.

## Licensing

### What each part is licensed under

These licenses were checked in the SpacetimeDB repository on 2026-11-06.

| Part | License | Under Principle VII |
| --- | --- | --- |
| Server (host, standalone server, datastore) | BSL 1.1, converting to AGPL-3.0 with a linking exception | Not allowed |
| C# module library (`crates/bindings-csharp`) | BSL 1.1 | Not allowed |
| Rust module library (`crates/bindings`) | Apache-2.0 | Allowed |
| C# client SDK (`sdks/csharp`) | Apache-2.0 | Allowed |
| TypeScript package `spacetimedb` (`crates/bindings-typescript`) | Unclear: `package.json` says ISC, but the directory's `LICENSE.txt` points to the BSL | Unclear until Clockwork Labs answers |

### What the free grant allows

The Additional Use Grant, quoted from the license:

> You may make use of the Licensed Work provided your application or service uses the Licensed
> Work with no more than one SpacetimeDB instance in production and provided that you do not use
> the Licensed Work for a Database Service.

- **What counts as an instance.** The docs call a server a host, and one host runs many databases.
  The natural reading is that one host process with many databases is one instance, and several
  hosts are several instances.
- **One instance covers the whole game.** The limit isn't per continent. The constitution puts
  each continent alone on a dedicated host with at least 16 cores. Even one continent plus the
  persistence tier goes over the limit unless both share one machine.
- **Standby servers are a gray area.** A second instance kept warm for failover is arguably "in
  production". The grant doesn't say, so a written answer would be needed.
- **The Database Service clause doesn't apply.** It covers letting third parties create their own
  table schemas, which we don't do.
- **Paid tiers.** According to search summaries of the pricing page (not fetched here), replication
  and custom scaling are Enterprise features, priced per node.

### Why a custom layer doesn't help

| Workaround | Why it fails |
| --- | --- |
| "Our code does the clustering, not theirs" | The grant counts instances, not features. |
| "Each instance is a separate application" | A game whose continents and persistence tier are joined by our relay is one service. Don't rely on that split without a written answer. |
| "Fork it and add clustering" | A fork is a derivative work. It is still the Licensed Work, under the same grant. |
| "Wait for it to turn into AGPL" | Each version converts on its Change Date, or four years after its first public release if that comes sooner. For 2.15.0 the Change Date is 2031-11-05. After that, the AGPL with SpacetimeDB's exception asks little of us legally. Our own rule is the blocker: Principle VII bars copyleft from hosted environments. |

## Technical comparison

| Concern | Chosen architecture | Self-hosted SpacetimeDB plus our layer |
| --- | --- | --- |
| **Parallelism** | Regions in one process tick in parallel on a fixed thread pool. A busy region spreads its tick across parallel phases. | The unit is the database, and a database "currently executes transactions serially". Many databases on one host run side by side, but one fight is one database on one thread. Parallel phases are impossible. The first simulator sweep already measures largest regions of about 482 to 842 players (measurement, `docs/simulator/decision-memo.md`). Each would be one serial database. |
| **Regions follow the fight** | Merges and splits happen in memory at the end of a tick. Regionizer bookkeeping measures 1.1 to 3.2 ms p99 at 5,000 entities on one thread (measurement, same memo). | A merge or split moves entities, pose history, timers and projectiles between databases by message. The choices are fixed zones, which put server seams through fights (the reason OpenSimulator was rejected), or a distributed regionizer that we write. |
| **Writes across regions** | Entity, location and global queues, applied in memory on the owner's next tick. | Async inter-database calls (IDC) send the call only if the sending transaction commits. Each runs exactly once, calls through one outbox table run in order, and nothing is sent until the sending commit is durable. That matches the entity queue well. But it works only between databases on the same server or cluster, only Rust and TypeScript modules can send, a pending call can't be cancelled, and an undeliverable call holds up later calls on that outbox. Latency per hop isn't published, and it includes waiting for the sender's commit to be durable. Between self-hosted instances we would build our own. |
| **Reads across regions** | Immutable snapshots, read without blocking the writer. | A database can't read or subscribe to another database. A relay or the clients have to subscribe to several databases. |
| **Interest management** | Delivery workers run off the simulation threads, with cells, three tiers capped at 100, 300 and 600 entities at 30, 10 and 2 Hz, a per-client byte budget, relevance ranking, and deltas against the last acknowledged state. | Subscriptions are queries: the client gets every change to the rows that match. They can't cap, budget or rank, and a moving player's query keeps changing. No benchmark of subscription fan-out is published. We would probably write our own Delivery layer, which gives up SpacetimeDB's main attraction. |
| **Transport** | Open decision 4: WebTransport datagrams or WebSocket. | WebSocket only, which would settle open decision 4 by default. |
| **Transient state** | Positions, pose history, projectiles, squads and chat are never persisted. | Every committed write goes to the commit log, event tables included ("The inserts are still recorded in the commitlog"). Movement alone for a 1,000-player fight at 30 Hz is about 30,000 logged row updates per second (estimate, by arithmetic). |
| **Runtime and language** | Native .NET 10 code with structs of arrays, no allocations per tick, a banned-API analyzer and BenchmarkDotNet. | C# runs as WebAssembly inside the host, so the existing benchmarks don't carry over. C# can't send cross-database calls, its procedures are marked unstable, and it has no unit-test harness. Rust and TypeScript are the first-class languages, but the constitution requires C# on .NET. |
| **Persistence** | Orleans grains over PostgreSQL, the system of record. Web surfaces and leaderboards read PostgreSQL. | SpacetimeDB is the store. A trade inside one database becomes a single transaction, which is simpler. Across databases only sagas work, because synchronous cross-database transactions are planned but not shipped. PostgreSQL would need a data feed, and Principle V would need an amendment. |
| **Failure and availability** | Agones starts a replacement server, which restores from a checkpoint. Stats lost are bounded by the batch interval. | A standalone server recovers from its own commit log, on its own disk. Replication exists in Maincloud: the v2.10 release notes mention Omnipaxos. It isn't part of a free self-hosted setup, and writing consensus replication ourselves is the hardest piece of the layer. |
| **Testing** | Deterministic simulation on a fixed time base and seed, parallel-equivalence tests, and the harness. | The Rust and TypeScript harness controls the caller, the time and the random seed for one module. Cross-database delivery is asynchronous and runs only on a live server, so scenarios spanning several regions can't be stepped tick by tick, as the rules for natural-language tests require. |
| **Updates** | A server is replaced through the fleet. | Module code is swapped without disconnecting clients. This is a real advantage. |

## The custom layer, piece by piece

| Piece | What it does | Counterpart in the chosen design | Difficulty (estimate) |
| --- | --- | --- | --- |
| **Relay between servers** | Outbox tables in our own schema, since built-in IDC works only on one server. A sidecar subscribes to them and calls the target's reducer over HTTP. An inbox table keyed by sender and message ID drops duplicates, and an acknowledgement clears the outbox. | In-memory queues | Moderate. It's the well-known outbox and inbox pattern. Each hop adds a subscription push, an HTTP call and a transaction on the target. |
| **Shard map and distributed regionizer** | Tracks which database owns which sections. During a merge or split it freezes, copies and releases the region's rows while keeping one authority per entity. | The in-process regionizer | Hard. This is the deferred multi-server continent. |
| **Session routing** | Connects the client to the right database and moves it on a handoff. Handles join tokens and reconnects. | One connection per continent, with the regions behind it | Moderate |
| **Snapshots across databases** | Far visibility and read-only interactions that reach across databases. | Snapshot publication | Moderate. Pushing data into the target databases adds writes to databases that each run on one thread. |
| **Shared time base** | Each database's scheduled tick runs on its own. The layer has to align ticks and hand projectiles over at their true timestamps. | One continent-wide time base | Hard to make exact |
| **Delivery and network LOD** | Our own caps, budgets and ranking, because subscriptions can't do them. | Delivery workers | The same work as in the chosen design, plus reading from SpacetimeDB |
| **PostgreSQL feed** | Keeps PostgreSQL current for web surfaces, leaderboards and analytics. | Not needed, because PostgreSQL is the store | Moderate |
| **Replication and failover** | Needed if "multi-node" also means high availability. | Agones replacement plus the checkpoint | Very hard, because it needs consensus. Skipping it means each instance is a single point of failure. |

Together these pieces are the deferred multi-server continent plus a database integration layer.
They would be built before Phase 1, not after profiling shows one host can't carry a continent.
The chosen design does the first five pieces inside one process, where they are data structures
rather than network protocols.

## Where SpacetimeDB is stronger

- Persistence, transactions and crash recovery come built in, with no ORM or grain storage code.
- Generated client bindings and live subscriptions suit UI-style state: inventory, outfit rosters,
  squad lists, chat and presence. With Orleans and the gateway, all of that is custom work.
- Inside one server, async IDC's guarantees match the entity queue: atomic with the sender, exactly
  once, and in order per outbox.
- Module code is swapped without disconnecting clients.
- A trade inside one database is a single transaction.
- Clockwork Labs' own MMORPG, BitCraft, runs on it. BitCraft isn't a 30 Hz shooter.

## The only setup the free grant allows, and why it's still not recommended

The free grant allows one instance for the whole game. That instance could hold the persistence
and social tier (players, outfits, worlds, chat and squads), with the continent servers left as
designed. It would stay within the instance limit, but it still:

- breaks Principle VII, because the server is under the BSL;
- breaks Principle V, because PostgreSQL is the system of record;
- settles open decisions 7, 8 and 10 (Orleans, Nakama or custom grains, and game-database hosting);
- keeps the currency ledger on a single machine with no replica; and
- needs a PostgreSQL feed for the web surfaces.

If it comes back, it comes back as a constitution amendment with Clockwork Labs' licensing answer in
writing, not as a feature.

## What would change this

- **Licensing.** Clockwork Labs releases the server and the C# module library under MIT or
  Apache-2.0, or grants multi-instance use in writing, and the owner amends Principle VII.
- **Parallelism.** SpacetimeDB can run one database's work in parallel. According to search
  summaries, "intra-database partitioning" is on its roadmap. It hasn't shipped.
- **Cross-database calls.** IDC works between self-hosted servers, and C# modules can declare
  outbox tables.
- **Transport.** SpacetimeDB adds a datagram transport, such as WebTransport.
- **Measurements.** Two would have to come out well:
  - IDC hops on one loaded host are well under one 33 ms tick.
  - Subscriptions can serve 1,000 clients at 30 Hz.

## Sources

- SpacetimeDB [`LICENSE.txt`](https://github.com/clockworklabs/SpacetimeDB/blob/master/LICENSE.txt)
  (master, Licensed Work 2.15.0): the Additional Use Grant, the Change Date and the linking
  exception.
- SpacetimeDB docs in its repository:
  - [Inter-database communication](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00200-core-concepts/00100-databases/00650-inter-database-communication.md):
    its guarantees, and "Current limitations".
  - [Auto-increment](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00200-core-concepts/00300-tables/00230-auto-increment.md):
    "SpacetimeDB currently executes transactions serially".
  - [Key architecture](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00100-intro/00100-getting-started/00400-key-architecture.md):
    "Many databases can run on a single host".
  - [Event tables](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00200-core-concepts/00300-tables/00550-event-tables.md):
    their inserts are recorded in the commit log.
  - [Schedule tables](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00200-core-concepts/00300-tables/00500-schedule-tables.md):
    interval ticks.
  - [Procedures](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00200-core-concepts/00200-functions/00400-procedures.md):
    C# procedures are unstable.
  - [Unit testing](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00250-how-to/00900-unit-testing.md):
    Rust and TypeScript only.
  - [FAQ](https://github.com/clockworklabs/SpacetimeDB/blob/master/docs/docs/00100-intro/00100-getting-started/00500-faq.md):
    licensing and Maincloud.
- [SpacetimeDB v2.14.0 release notes](https://github.com/clockworklabs/SpacetimeDB/releases/tag/v2.14.0)
  (2026-10-30): async IDC, with C# and C++ support "coming in a future release".
- SpacetimeDB releases list: Omnipaxos replication on Maincloud in v2.10.x.
- Not fetched, read through search summaries only:
  - [pricing](https://spacetimedb.com/pricing)
  - [scaling blog](https://spacetimedb.com/blog/how-does-spacetime-scale)
- In this repository: the architecture spec, the constitution (`.specify/memory/constitution.md`)
  and the regionizer decision memo (`docs/simulator/decision-memo.md`).
