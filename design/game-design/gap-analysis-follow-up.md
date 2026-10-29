---
title: "Draft Specs: Gap Analysis Follow-Up"
type: game-design
source: https://claude.ai/artifact/1WRrvaCJf3JDTs3fYaTFjS
source_updated: 2026-10-05
imported: 2026-10-29
---

# Draft Specs: Gap Analysis Follow-Up

Oct 5, 2026 · @Nicholas Bonilla

Draft specs for every gap in the gap analysis, with decisions made so each can be reviewed and promoted. Scope is not cut: the vertical slice only sets build order and fun gates. Every number is a starting target for playtesting, not a final value. Each spec is self-contained so it can become its own GitHub issue.

## 0. Cross-spec decisions

The gap analysis found ten contradictions between the newer specs and Game Design V1. Each is resolved here so the specs below can build on one consistent model.

| # | Conflict | Decision |
| --- | --- | --- |
| 1 | Bonus-only mana vs. V1 spice that powers respawns and turret upkeep | **Bonus-only mana wins.** V1's spice types are retired. Respawns and turrets never depend on supply, so a starved base is still a normal base. |
| 2 | Miner role vs. Spice Caravan harvest mode | **The Miner is the only harvester.** Caravans are transports only. |
| 3 | Zero-XP creeps vs. V1's XP-granting PvE mobs | **One combat bot family: creeps, zero XP.** Monsters that grant progress exist only in the practice range and the onboarding prologue, never in the live war. |
| 4 | Small aircraft claim bubble vs. dense fights on Galleons and the Gigas | **Vessel regions** (spec 4). |
| 5 | Cosmetics-only vs. capital looting and kiosk points raising election odds | **Capital loot is cosmetic trophies only. Election influence is earned through play only**, never through kiosks, currency or anything purchasable. |
| 6 | Frictionless Join Now vs. anti-cheat friction for new accounts | **Trust tiers** (spec 3). New accounts play immediately; restrictions apply to social and economic features, not to the fight. |
| 7 | Low-population scaling vs. conquest alert at 700 players | **Alerts trigger on a share of active continent population**, not a fixed count (spec 9). |
| 8 | V1's 16-seat Galaxy airship vs. undecided middle transport | **The middle class is the Frigate: 12 seats,** a Galaxy-style heavy transport with a spawn function. Roster: Skiff 3, Cutter 6, Frigate 12, Galleon class 32. |
| 9 | Phase 4 targets 100k to 1M players with no gameplay slice yet | **Phase 4 stays as the long-term target.** Launch capacity targets are set separately (spec 13). |
| 10 | Fog draft marked as needing refinement | **The Fog draft is non-normative** until it is validated against the Delivery spec. |

**Economy, unified:** Miners harvest Spice and deliver it to depots. Artisans withdraw Spice for crafting. Rear-base crystals refine depot Spice into mana, which convoys and skyships carry to the front. This also closes the open question of whether Spice and mana are one economy.

## 1. Vertical Slice and Fun Gates

Full scope stays. This spec only sets the order in which systems are built and adds a fun gate next to every technical gate, so the game proves it is fun as early as it proves it can scale.

### The first slice

The first playable build combines the riskiest systems in the smallest space:

- One medium base with a crystal, two factions, and creeps.
- Knight, Medic and Engineer, each with one weapon.
- One broom and one Skiff.
- One mana caravan route into the base.
- 30 to 60 humans plus bots.

Every other system is built afterward in the order set by the GitHub Projects ranking. Nothing is removed from the design.

### Riskiest assumptions, tested first

1. Browser FPS combat feels good enough (spec 2, spec 15).
2. Walkable ships network acceptably (spec 4).
3. A 100-player fight feels epic rather than chaotic.
4. Bots make low population feel alive (spec 17).

### Fun gates

Each engine phase gate gains a playtest gate alongside its technical measurement:

| Engine phase | Technical gate (existing) | Fun gate (new) |
| --- | --- | --- |
| Phase 1 | Tick time and frame-time targets | 8 of 10 playtesters want another session after a 30-minute base fight |
| Phase 2 | Stress ladder with bots | A 100-player fight is rated at least 4 of 5 for "felt epic"; no more than 1 in 5 testers report confusion about where to go |
| Phase 3 | Multi-region tests | Testers can name the fight they want to join from the map within 30 seconds |
| Phase 4 | Scaling past one host | First-session retention and fight quality hold at the larger scale |

### Playtest cadence

- A fixed weekly internal playtest from the first slice onward.
- A monthly open playtest once the slice passes its Phase 1 fun gate.
- Every playtest ends with the same short survey, so results can be compared over time.

### Re-plan trigger

If a fun gate fails twice in a row, development pauses on new systems and focuses on fixing the failing experience before continuing.

## 2. Core Combat Feel, TTK and Weapon Model

Combat uses visible magical projectiles with moderate time-to-kill, resolved on the server. Moderate TTK keeps large fights readable, reduces the impact of latency, and blunts the payoff from cheating.

### Time-to-kill targets

| Matchup | Target TTK (body shots, effective range) |
| --- | --- |
| Infantry vs. infantry, primary weapon | 0.6 to 0.9 s |
| Infantry vs. infantry, headshots | About 40% faster (2x headshot multiplier) |
| Infantry vs. Mech | 4 to 6 s with small arms; 2 to 3 s with dedicated anti-armor |
| Dedicated anti-air vs. broom | 2 to 4 s |
| Broom vs. infantry | Never one-shot; 1.5 s minimum |

### Weapon model

All weapons are data-defined archetypes:

- **Projectile speed:** 250 to 600 m/s for bolts, slower for heavy spells. No true hitscan; everything is a visible projectile.
- **Gravity drop** per archetype, **splash radius** and **falloff** for area spells.
- **Ammunition as mana charges** with a reload or recharge time.
- **Rate of fire, spread and recoil** per weapon.

Archetypes at launch: rune rifle (assault), wand (close range), staff (marksman), lance (anti-armor), shield-and-blade (Knight melee), bolt crossbow (Engineer).

### Area damage rules

- Splash damage per target is capped per second, so a chokepoint cannot be wiped instantly by stacked explosives.
- Grenade-equivalents are limited to two per life.

### Movement

- Walk 4 m/s, sprint 6.5 m/s, crouch-slide on sprint.
- Flyer flight: 3-second meter, 6-second recharge, hover allowed while the meter lasts.
- On ships: the same movement in ship-local space (spec 4).

### Hit model

This resolves the open hit-detection question from the design side:

- Clients send fire events with timestamp and aim. The **server resolves hits** by rewinding pose history, with lag compensation capped at 200 ms.
- Clients show predicted hit effects immediately for responsiveness; the server confirms damage.
- Cost control: the server only tests projectiles against hitboxes within a narrow cone along the shot path.

### Feedback

Hit markers, damage numbers (toggleable), distinct headshot audio, and kill confirmation, matching the Killcam and Damage Breakdown spec.

### Latency target

Input to on-screen response under 100 ms at 60 fps on recommended hardware.

## 3. Client Trust and Anti-Cheat

The browser client is untrusted for everything that affects the fight. The server owns all gameplay state, sends players only what they can plausibly perceive, and detects cheating from behavior. This complements the existing Replay and Anti-Cheat Evidence spec, which covers investigation and enforcement.

### Trust matrix

| Data | Authority | Server check |
| --- | --- | --- |
| Movement | Server simulates from client inputs | Speed, acceleration and collision validated; reconciliation on mismatch |
| Aim and firing | Client sends fire events | Rate of fire, ammo and cooldowns validated; aim snap analysis |
| Hits and damage | Server (spec 2) | Rewound pose history, 200 ms cap |
| Visibility | Server | Delivery and fog tiers withhold unseen entities |
| Cooldowns, mana, Spice, XP | Server | Never accepted from the client |
| Cosmetics and settings | Client | Server validates ownership |

### Information minimization

Entities a player cannot plausibly perceive are never sent. This is the main defense against wallhacks, which no server check can stop once data is on the client.

### Account trust tiers

New players can fight immediately; restrictions apply to features that cheaters and spammers exploit.

| Tier | How reached | Restrictions |
| --- | --- | --- |
| T0 New | First session | Squad chat and voice only; no gifting; no outfit creation; reports against them reviewed faster |
| T1 Established | 5 hours played and 3 days since creation | Proximity and faction chat; outfit membership |
| T2 Trusted | 20 hours or a verified purchase | Outfit creation and leadership; election candidacy |

### Detection

- **Behavioral analysis** on the ClickHouse logs: aim snap rates, reaction times, headshot ratios, impossible movement, and kill rates per account age.
- **New-account review:** high-performing T0 accounts are prioritized for automated and human review.
- **WebAssembly integrity checks** verified by the server, treated as a speed bump rather than a wall.

### Enforcement

- Clear messages when a player is removed by anti-cheat, and an appeals process.
- False positives tracked as a metric; any detection rule with a confirmed false-positive rate above 1% is disabled until fixed.
- Ban evasion signals (device, payment, network) retained only as long as necessary and disclosed in the privacy policy (spec 11).

### Fallback

If browser cheating becomes unmanageable, a native client distributed through Steam with a commercial anti-cheat is offered, and Outfit Wars (spec 22) requires it.

## 4. Moving Platforms and Regionizer Authority

Every crewed vessel and everyone aboard is simulated by exactly one region server at a time. A vessel is never split across servers. This keeps close-quarters fights on decks consistent and avoids the distributed-physics problem that took Star Citizen years to address.

### Vessel classes

| Class | Seats | Deck | Claim radius |
| --- | --- | --- | --- |
| Skiff | 3 | Seats only | Small aircraft bubble |
| Cutter | 6 | Seats only | Small aircraft bubble |
| Frigate | 12 | Seats only, with a spawn function | Medium bubble |
| Galleon class | 32 | Walkable decks | Large bubble covering the hull plus 50 m |
| Gigas | Base-scale | Walkable decks | Treated as a static base region while anchored |

Only the Galleon class and the Gigas have walkable decks. Smaller skyships use fixed seats, which avoids the hardest moving-platform cases for most ships.

### Authority rules

- A walkable vessel is a **vessel region**: a region whose frame moves with the hull rather than sitting on map sections.
- When a vessel region overlaps a ground region, both are owned by the same region server for the duration of the overlap (merge at tick end, as the regionizer already does).
- Ownership transfers between region servers only when no boarding or deck combat is in progress; otherwise the transfer waits, up to a hard limit.
- An anchored Gigas becomes a static base region and stays static until it moves again.

### Coordinates and rewind

- Players aboard are stored in vessel-local coordinates; the vessel transform is stored separately.
- Pose history is kept in vessel-local space, alongside vessel transform history, so lag-compensated hits rewind both.
- Boarding from a broom converts the rider into vessel-local space at a defined attach point.

### Budgets

- At most 4 Galleon-class vessels and 1 Gigas per region.
- At most 2 Galleon-class vessels per faction per continent.

### Load test

Added to the Phase 2 stress ladder: 4 Galleon-class vessels and a Gigas over one base, with 300 players including boarders, at target tick rate.

### Fallback

If the load test fails, Galleon-class decks become seat-based like smaller ships, and the Gigas stays walkable only while anchored.

## 5. Faction Population Balance

Imbalance between factions is handled with caps, a ladder of underdog incentives, and a Sellsword contract built in from day one, rather than a fourth faction added years later as PS2 did with NSO. Population Scaling handles total population; this spec handles the split between factions.

### Caps and queues

- When a continent has 300 or more players, no faction may exceed 40% of its population.
- Players over the cap wait in a queue with a live view of the war, or can deploy to another continent immediately.
- Squads and outfits deploying together are admitted as a group, never split.

### Underdog incentive ladder

Applied in steps as a faction's share falls below 30%, then 25%:

| Step | Under 30% | Under 25% |
| --- | --- | --- |
| XP bonus | +20% | +50% |
| Respawn time | -2 s | -4 s |
| Creeps | Creeps fight for the underdog | More creeps |
| Vehicle cost | -15% | -30% |
| Gigas charge rate | +25% | +50% |

### Sellsword contracts

- Any player can sign a Sellsword contract to fight for the underdog faction for the rest of a continent session, using their own unlocks with neutral Sellsword cosmetics.
- Contracts pay a completion bonus in XP and an exclusive Sellsword cosmetic track.
- Contracts are offered only when a faction is below 30%.

### Faction switching

- Each account can have characters in all three factions.
- Alert rewards require being on the same character from the start of the alert, discouraging switching to the winning side.
- After switching factions on a continent, a player cannot rejoin that continent on another faction for 1 hour.

### Metrics and alarms

- Faction split per continent, per hour.
- An alarm when any faction holds over 45% for more than 30 minutes, or under 20% for more than 15 minutes.

## 6. Classes, Loadouts and Progression

Six combat classes and two support roles, all swappable at terminals. Progression is sideways: unlocks add options, never power. This resolves the open class questions in Game Design V1.

### Roster

| Class | Role (PS2 analog) | Decisions |
| --- | --- | --- |
| Knight | Heavy assault | Shield-and-blade or lance; personal ward shield; anti-armor lance. Absorbs Heavy if V1 lists it separately. |
| Flyer | Light assault | Flight meter: 3 s burst, 6 s recharge, hover while meter lasts |
| Medic | Combat medic | Mid-range fighter with a healing staff; revives; self-defense wand. Not a pure healer. |
| Engineer | Engineer | Bolt crossbow; repair, mana packs, deployable turrets and AA; temporary breach barriers |
| Wizard | Infiltrator and spellcaster | Spells with dramatic effects near powered crystals (bonus only); scouting and area denial |
| Mech | MAX | A suited class, not a vehicle. Costs a resource to pull, 3-minute cooldown, slow and tough |
| Miner | Support role | Harvests Spice (cross-spec decision 2) |
| Artisan | Support role | Crafts consumables from depot Spice |

### XP and certs

- 250 XP earns 1 cert.
- XP sources: kills, assists, captures, revives, repairs, resupplies, deliveries, escorts, mining and crafting. Logistics actions earn XP on par with combat over an hour of play.
- Creep and bot convoy kills earn zero XP (cross-spec decision 3).

### Unlocks

- **Sidegrade rule:** a fully unlocked weapon may beat the starter weapon of the same role by at most 5% on any single performance metric, and must trade something away for it.
- **Starter loadouts are competitive:** every class's default loadout is a complete, viable kit.
- Unlock prices range from 50 to 1,000 certs; utility and cosmetic-adjacent unlocks sit at the low end.
- **Respec:** any cert purchase is refundable within 24 hours of purchase.

### New-player protection

The veteran advantage comes from skill, map knowledge and team play, not from gear, keeping the gap PS2 players often cited much smaller.

## 7. Bases, Capture and Lattice

Bases come in three archetypes, built for fights of 8 to 20 minutes, with spawn rooms and capture points separated so attackers can dig in and defenders can push back. Base spacing is tied to the regionizer's claim radii.

### Archetypes

| Archetype | Capture points | Players it suits | Target fight length |
| --- | --- | --- | --- |
| Outpost | 1 | 12 to 48 | 8 to 12 min |
| Facility | 2 to 3 | 48 to 150 | 12 to 18 min |
| Stronghold | 3 to 4, plus outer zones | 150 or more | 15 to 20 min |

### Layout rules

- **Spawn rooms** have one-way shields, at least two exits, and sit 60 to 120 m from the nearest point.
- **Attacker approach:** at least two covered routes to every point, and vehicle access to the outer area but not to points.
- **Crystal** sits near the center, visible from outside, never on a capture point.
- **Kiosk sockets** in safe courtyards near spawn rooms.
- **Floating structures** (on signature bases only) sink over 10 seconds with warning when unpowered.
- **Outer zones** for Population Scaling activate only between fights.

### Capture

- Base capture time is 3 minutes with one point held, faster with more points held.
- Capture speed scales with the attacker-to-defender ratio near the point, capped at 2x.
- Creeps count as partial presence at low population (Population Scaling spec).

### Spawn-camp prevention

- Multiple shielded exits and turrets that cover exits from inside.
- A base whose spawn room is camped for 2 minutes offers a fallback spawn at the nearest friendly base.

### Lattice and spacing

- Minimum distance between bases is at least 1.2 times the largest claim radius, so neighboring fights never force a regionizer merge.
- Each base has 2 to 4 lattice links. Links decide where attacks can happen and where supply lanes run.

## 8. Combined Arms and Anti-Air Balance

Every unit type has a hard counter it can't ignore, and anti-air kills rather than merely scares. This targets PS2's longest-running complaint: air farming infantry while ground anti-air felt like a deterrent at best.

### Counter matrix

| Attacker → target | Infantry | Mech | Ground vehicles and caravans | Brooms | Skiff / Cutter | Frigate / Galleon class |
| --- | --- | --- | --- | --- | --- | --- |
| Infantry | Even | Weak | Weak (Knight lance: strong) | Weak (AA: strong) | Weak (AA: strong) | Weak; boarding possible |
| Mech | Strong | Even | Moderate | Moderate with AA loadout | Moderate with AA loadout | Weak |
| Brooms | Moderate (never one-shot) | Weak | Moderate | Even | Moderate | Weak; boarding possible |
| Skiff | Moderate | Moderate | Strong | Weak (tail gunner: moderate) | Even | Moderate |
| Galleon class | Moderate | Moderate | Strong | Weak | Strong | Even |

### Anti-air

- **Options:** Engineer deployable AA turret, Knight lock-on lance, Mech AA loadout, base AA turrets, and crystal ground cannons during sky events.
- **Dedicated AA kills a broom in 2 to 4 seconds** at effective range and a Skiff in 8 to 12 seconds.
- **Lock-ons** have a lock time of 1.5 to 2 seconds and are countered by a broom's flare-equivalent, so they help new players without beating experts.

### Air vs. infantry

- Air weapons deal reduced splash to infantry, and no air weapon kills an infantry player in one hit.
- Skiff belly bombards fire arcing shots, rewarding skill over precision farming.

### Altitude and terrain

- A flight ceiling of 400 m above terrain keeps aircraft within reach of ground AA.
- Floating islands provide cover near important areas (Air vehicles spec).

### Ramming and boarding

- Ramming a skyship kills the broom rider and deals hull damage; it is a sacrifice play, not a free kill.
- Brooms can land riders on enemy decks of walkable vessels (spec 4).

### Costs

Vehicle costs scale with power: broom 450 (from V1), Skiff 700, Cutter 600, Frigate 900, Galleon class pulled by outfits with a long cooldown.

## 9. Win Conditions and Meta-Campaign

Continents are won by territory and locked by an alert; continent wins earn war points; war points across a season decide the season's victor. Every other endgame (capitals, the Gigas, the sky leviathan) feeds into this one structure.

### Hierarchy

1. **Fights** decide bases.
2. **Territory** decides alerts: holding 66% of a continent's territory starts V1's victory countdown.
3. **Alerts** decide continent locks.
4. **Continent locks** earn war points.
5. **War points** decide the season (70 to 100 days, matching the season pass).

### Alerts

- **Triggers scale with population:** an alert can start once a continent has at least 30% of the server's active players, or after the 66% territory threshold, whichever comes first. This replaces V1's fixed 700-player trigger (cross-spec decision 7).
- Alert types from V1 (conquest, trade war, challenge) stay; each lasts 45 to 90 minutes.
- The alert winner locks the continent for a set time and earns war points.

### Capitals

- A faction capital's shield drops only after its faction has lost a continent lock and the attackers hold a set share of adjacent territory.
- Looting a capital awards cosmetic trophies only (cross-spec decision 5). The losing faction loses nothing it owns.

### Gigas and events

- A Gigas event influences territory like any large fight; it does not award war points directly.
- Sky leviathan finales and community events never affect war points.

### Season end

- The faction with the most war points wins the season and gets a cosmetic celebration, a capital monument and a season title.
- Territory resets; characters, unlocks and cosmetics never reset.

### Low-population play

Off-peak, smaller continents with lower thresholds stay open (Population Scaling spec), so alerts still happen when few people are online.

## 10. Faction Leadership, Command and Squad Tools

Elected faction leaders stay, with limited, transparent powers and elections that can't be bought or stuffed with alts. Squad and platoon tools give everyone else the command structure big fights need.

### Elected leaders

- **Term:** one week, with an election at the end of each week.
- **Voting:** one vote per account per faction, from T1 accounts (spec 3) with at least 5 hours on that faction's characters in the past 30 days.
- **Candidacy:** T2 accounts only. Influence is earned through play (captures, deliveries, squad leading), never through kiosks, currency or anything purchasable (cross-spec decision 5).
- **Powers:** set faction-wide waypoints and priority targets, cast the tiebreak vote for Gigas destinations, and activate one location-based buff per alert.
- **Buff limits:** at most +5% to one stat, in one region, for 10 minutes; never stacks with another leader buff.
- **Recall:** if 60% of active voters sign a recall during a term, the runner-up takes over.
- **Visibility:** leader actions are logged publicly in the faction feed.

### Squads and platoons

- Squads of up to 12, platoons of up to 4 squads.
- Squad and platoon leaders can place waypoints, mark targets and assign squads to objectives.
- Command chat for platoon leaders and the faction leader.
- Squad leaders earn bonus XP when their squad captures, defends or delivers, rewarding leadership.

### Outfit permissions

- Ranks with configurable permissions: invite, promote, pull Galleon-class vessels, manage halls and liveries, schedule events.
- Works alongside home regiments (Always Belong spec).

## 11. Account, Identity, Age and Privacy

Accounts are created with as little friction as possible, ages are asked once with a neutral question, and players under 13 get a restricted, consent-based experience. Compliance with the amended FTC COPPA Rule has been required since April 22, 2026. All of this needs review by a lawyer before launch.

### Accounts

- **Sign-in options:** email, Steam, Discord, or a guest session that becomes a full account later.
- Guest sessions can play the Join Now flow (onboarding spec) and keep their progress when upgraded.
- Optional linking for Twitch (Drops) and Discord (roles, events).

### Age

- **Neutral age question** (date of birth, no hint of a "correct" answer) shown once during the live loading screen, so it adds no extra screen.
- **Under 13:** no voice chat, no free-text chat (pings and quick commands only), no public stats, no account linking, no purchases without verifiable parental consent, and no personalized advertising or profiling.
- **13 to 17:** voice off by default, filtered chat, spending limits, and teen-appropriate defaults.
- Age data is stored minimally and never shared.

### Privacy

- **Data inventory** listing every data type, its purpose, its retention period and who can access it.
- **Retention defaults:** gameplay telemetry pseudonymized after 90 days; chat logs kept 30 days unless part of a report; voice recorded only for active moderation reports.
- **Self-service rights:** export and delete account data from the settings page, covering GDPR requests.
- **Public stats opt-out:** players can hide their profile from the public stats API and web map.
- **Ban-evasion data** (spec 3) disclosed and kept only as long as necessary.

### Security

- Two-factor authentication offered to everyone and required for outfit leaders and accounts with purchases above a threshold.
- Session and token handling reviewed before launch.

## 12. Store, Currency and Consumer-Law Compliance

The store follows the EU CPC Network's Key Principles on in-game virtual currencies (March 2025) everywhere, not just in the EU: real-money prices are always visible, currency packs never force leftovers, and players can get refunds. Lawyer review is required before the store goes live.

### Prices

- Every item shows its real-money equivalent next to its premium currency price.
- **No leftover currency:** item prices are multiples of the smallest pack, and pack sizes match common item prices.
- Bundles state the total price and the saving honestly.

### Refunds and withdrawal

- **Unused premium currency** can be refunded within 14 days of purchase.
- **Cosmetics:** up to 3 self-service refunds per year for items bought in the last 14 days and not used in a match, similar to Fortnite's refund tickets.
- For digital content used immediately, the purchase flow asks for clear consent and explains that the withdrawal right ends once the content is used.

### Pressure tactics

- No countdown timers in the shop. Rotation dates are shown as plain dates.
- No upgrade or tier-purchase pop-ups (Monetization spec).
- Limited items are labeled clearly with when they leave.

### Minors

- Spending limits for 13 to 17, configurable by parents.
- No purchases for under-13 accounts without verifiable parental consent (spec 11).
- Marketing in the game never targets minors with limited-time pressure.

### Payments and tax

- Use a merchant of record (for example, Steam on Steam, or a payment provider that handles VAT and sales tax) so taxes, invoicing and regional rules are handled correctly.
- Regional pricing set per market.

### Records

Every purchase, refund and currency movement is logged for support, disputes and regulator requests.

## 13. Launch Capacity, Queues and Overflow

The game plans for surges aimed at a single fight, because Join Now links and viral clips send many people to the same place at once. New World's launch is the warning: large worlds and many servers still produced long queues, and Amazon said it wished character transfers had been ready on day one.

### Launch targets

- **Capacity:** launch infrastructure sized for 2x the expected peak, based on wishlists and playtest signups.
- **Regions:** North America and Europe at launch, more by demand.
- **Pre-launch load test** at 2x expected peak, with real players plus bots.

### Full fight from a link

1. Send the player to the nearest similar fight on the same continent and faction.
2. If none is available, open the recruit galleon (onboarding spec), which always has room.
3. Only then queue them, with a live view of the fight and their position in line.

### Continent overflow

- When every open continent is near capacity, another continent opens (Population Scaling spec), announced in advance.
- Faction caps (spec 5) apply during surges, with squads admitted as a group.

### Day-one tools

- Character transfers between regions.
- A public status page with queue times and incidents.
- In-game and Discord announcements for maintenance and incidents.

### Queue experience

- Queued players can watch the war, customize their character, or try the practice range.
- Estimated wait time shown, updated every 30 seconds.

## 14. Telemetry, KPIs and Analytics

Player telemetry runs on the same ClickHouse and OpenTelemetry stack as server telemetry, with pseudonymous IDs and the retention rules from spec 11. The existing harness measures servers; this spec measures players.

### Core KPIs and starting targets

| KPI | Starting target |
| --- | --- |
| Day-1 retention | 35% |
| Day-7 retention | 15% |
| Day-30 retention | 7% |
| Time from link click to first shot fired | Under 2 minutes after the game is installed |
| Time to first kill | Under 5 minutes |
| First-session length | Over 25 minutes median |
| Free-to-paying conversion | 2 to 5% of monthly active players |

### Gameplay health

- Fight-size distribution per continent and hour.
- Faction split per continent (alarms in spec 5).
- Bot-to-human ratio per region.
- Class and vehicle pick rates, and kill rates per weapon, to spot balance outliers.
- Base capture times against the targets in spec 7.

### Role health

Foxhole's logistics players organized a strike in 2021 and 2022 when hauling became a chore. These catch that before it happens:

- Hours per hauler, miner and artisan per week, and the share of players in each role.
- Deliveries completed vs. interrupted.
- Role abandonment: players who try a role and never return to it.

### Funnels

- Onboarding: link click, install, first login, first deploy, first death, second session.
- Store: shop visits, wishlist adds, purchases, refunds.

### Tools

- Dashboards for daily review, plus alarms for crashes, retention drops and balance outliers.
- Experiment flags for A/B testing onboarding and UI changes.

## 15. Client Performance and Hardware Targets

The browser client targets 60 fps in a 100-player fight on mid-range hardware and stays playable on integrated graphics, by capping what it renders in detail rather than limiting how many players are in the fight.

### Hardware targets

| Tier | Hardware class | Target |
| --- | --- | --- |
| Minimum | Recent integrated graphics, 8 GB RAM, 4 CPU cores | 30 fps in a 100-player fight at low settings |
| Recommended | Mid-range discrete GPU, 16 GB RAM, 6 CPU cores | 60 fps in a 100-player fight at medium settings |
| High | Recent mid-to-high discrete GPU | 60 fps in a 300-player fight at high settings |

### Browsers and APIs

- Chromium-based browsers with WebGPU as the primary target; WebGL2 as fallback with reduced effects.
- Firefox and Safari supported once they meet the WebGPU baseline; tested every release.

### Render budgets

- **Detailed characters:** up to 64 fully animated nearby; beyond that, simplified models and impostors.
- **Threat-based priority:** players shooting at you, near you or in your squad are always rendered in detail, similar to PS2's render prioritization.
- **Frame-time budget** per subsystem (rendering, animation, networking, UI), tracked in every build.

### Memory and download

- Memory ceiling of 3 GB in the browser tab.
- Initial download under 300 MB, with assets streamed by continent and on demand.
- A Steam desktop wrapper (spec 26) uses the same budgets.

### Measurement

- An automated benchmark scene with a 100-player bot fight runs on every build and fails the build if frame time regresses by more than 10%.

## 16. Hosting Cost and Operations

Production runs on rented bare-metal servers with included bandwidth and DDoS protection, orchestrated by Kubernetes and Agones. Egress, not compute, is the cost that grows fastest with big fights, so bandwidth pricing drives the hosting choice.

### Cost model

- The architecture's own estimate is about 0.46 Gbit/s of egress per 1,000-player fight, roughly 200 GB per hour.
- At typical metered cloud egress prices, that alone could cost on the order of $10 or more per hour per large fight, which is why bare metal with included or unmetered bandwidth is preferred.
- **Track three numbers continuously:** cost per concurrent player-hour, cost per continent-hour, and monthly fixed cost.
- **Break-even curve:** monthly hosting cost compared against supporter membership and store revenue at different concurrency levels, reviewed monthly.

### Infrastructure

- One dedicated 16-core node per active continent, as in the architecture spec, plus shared nodes for login, store, chat and APIs.
- Kubernetes with Agones for game server lifecycle; the same manifests run locally, keeping the "free to run locally" principle.
- DDoS protection at the network edge from the hosting provider.
- Regions: North America and Europe at launch (spec 13).

### Reliability

- **Backups:** daily full backups and point-in-time recovery for the account, progression and purchase databases; restore tested monthly.
- **Runbooks** for the most likely incidents: region server crash, database failover, DDoS, payment outage, login outage.
- **Solo on-call:** automated alerts to phone for critical issues only; scheduled maintenance windows announced in advance; a status page (spec 13).

### End-of-life pledge

If the game ever shuts down, server software and tools are released so the community can host it. Worlds Adrift could not be community-hosted because it depended on proprietary infrastructure; the permissive-license rule already avoids that, and this pledge makes it explicit.

## 17. Unified Bot and AI Behavior

All bots run on one AI framework with shared perception, pathing and budgets, configured per profile. There are no player-equivalent combat bots and no simulated war on empty continents; creeps are the only combat backfill. This resolves V1's "full bots or simulated war" question.

### Profiles

| Profile | Purpose | Combat | XP when killed |
| --- | --- | --- | --- |
| Creep | Population backfill, contests points | Weak, Titanfall-grunt level | Zero |
| Convoy | Carries mana along lanes | None (escorted by creeps or players) | Zero |
| Crew | Recruit galleon gunners and repair crew | Weak gunnery | Not targetable as kills |
| Officer | Home regiment, social and mission giver | None | Not targetable |
| Training monster | Practice range and onboarding prologue only | Tuned for learning | Small, prologue only |
| Leviathan | Scripted world event | Event-specific | Event rewards only |

### Framework

- **One decision system** (utility AI or behavior trees; pick one) with per-profile configuration.
- **Perception** respects the same visibility rules as players, so bots never see through walls.
- **Pathing:** navmeshes for each static base and continent, plus vessel-local navmeshes for walkable ships (spec 4).

### Budgets

- At most 300 AI agents per region.
- AI may use at most 15% of a region's tick time; past that, bots far from players drop to lower update rates, and convoys switch to abstract lane simulation.
- Bots with no players nearby are simulated abstractly or despawned.

### Phase-out

- Creeps despawn as real players join, starting with those farthest from fights.
- Recruit galleon crew are replaced station by station as players arrive.

### Readability

Every bot is visually recognizable as a bot: creeps are monsters, crew and officers have distinct uniforms. Bots are never presented as real players.

## 18. UI, HUD and Map Readability

A player should be able to answer three questions at a glance: where is the fight I can help with, how is my faction doing, and what is about to kill me. The HUD stays minimal in combat; the map carries the strategic picture.

### Map

- **Faction territory** in three colorblind-safe palettes (spec 20), with outlines as well as fills.
- **Fight heat markers** sized by player count, so players can pick a fight within 30 seconds (fun gate, spec 1).
- **Mana flow** along lattice lines, dimming or flashing when cut; crystal states shown as bright, dim or empty.
- **Events** (Gigas, sky leviathan crossings, recruit galleon) marked with routes and timers.
- One-click deploy from the map to any fight the player can join.

### HUD

- **Always shown:** health, shield, ammo or mana charges, objective marker, minimap.
- **Contextual:** capture progress near points, ship hull and lift state when aboard, flight meter for Flyers.
- **Damage direction indicators** and hit feedback (spec 2).
- A minimal mode for streamers and competitive players.

### Ships

- Hull damage and lift-leak locations shown on a ship diagram for the crew.
- Breached decks highlighted for defenders and attackers on a walkable vessel.

### Notifications

- One notification channel with priorities, so alerts, squad orders and social messages never stack on top of each other in a fight.
- Social, store and progress notifications are held until the player is out of combat.

## 19. Audio Design

Audio prioritizes information in fights: threats nearby are always audible, distant chaos becomes a bed of ambient battle sound, and voice chat is never drowned out.

### Mixing priorities (highest first)

1. Squad voice chat and critical callouts.
2. Nearby threats: footsteps, reloads, incoming fire, lock-on warnings.
3. Own weapon and hit feedback.
4. Nearby battle sounds.
5. Distant battle bed, music and ambience.
6. Bot chatter and social ambience (ducked under everything above, silent in combat except useful callouts).

### Scale

- A cap on simultaneous detailed sound sources (for example, 48); beyond that, distant sounds blend into a procedural battle bed sized by fight intensity.
- Directional audio for footsteps and gunfire within 50 m.

### Identity

- Distinct sound signatures per faction and per class weapon archetype, so players can tell who is shooting without looking.
- Signature sounds for crystals (hum rising with mana), skyship creaks and lift leaks, and the Gigas arrival.

### Music

- Music is stream-safe: either owned outright or licensed for streaming (Creators and Streamers spec).
- Shanties for skyship launches and celebrations; adaptive combat music kept low so it never masks gameplay audio.

### Settings

Separate volume sliders for voice, effects, music, ambience and bot chatter, plus a "competitive" mix preset that boosts footsteps and threats.

## 20. Accessibility

Accessibility is designed in from the start, especially color, because three faction colors on one battlefield are a readability risk for colorblind players.

### Vision

- **Faction palettes** chosen to be distinguishable under the common types of color blindness, plus shape and icon markers so color is never the only signal.
- Colorblind presets and a custom faction color option.
- UI scaling from 75% to 150%, and high-contrast HUD mode.

### Hearing

- Subtitles for all dialogue, bot chatter and shanties, with speaker names.
- Visual indicators for important sounds: footsteps, gunfire direction, lock-on warnings.
- Speech-to-text for voice chat (if feasible), and text-to-speech for squad chat.

### Motor

- Full key and button remapping, including mouse buttons.
- Toggle or hold options for sprint, aim, crouch and flight.
- Aim-assist only on controllers, never on mouse.
- Pings and quick commands as an alternative to voice.

### Comfort

- Motion-sickness options for ships and flight: reduced camera sway, horizon stabilization, field-of-view slider, and reduced camera shake.
- Reduced flashing effects for spells, fireworks and explosions.

### Cognitive

- Clear objective markers and a "what should I do next" prompt available at any time.
- Simple onboarding (onboarding spec) that teaches one thing at a time.

## 21. Localization

Every player-facing string goes through a localization pipeline from the first build, even though launch is English only. Retrofitting localization later is far more expensive than building it in.

### Pipeline

- No hard-coded player-facing text: every string has a key in a resource file.
- An ICU-style message format for plurals, genders and variables.
- UI layouts tolerate text 40% longer than English and right-to-left scripts.
- Fonts cover Latin, Cyrillic, CJK and Korean from the start.

### Languages

- **Launch:** English.
- **First wave after launch:** German, French, Spanish, Brazilian Portuguese, Russian and Korean, chosen by the regions with the most wishlists and players.
- Community translation contributions welcome, reviewed before release.

### Content

- Bot chatter and shanties written with localization in mind: short lines, no wordplay that can't translate, or marked as English-only flavor.
- Store pages, legal text and the privacy policy localized professionally.

### Regional formats

Dates, numbers and currencies follow the player's locale, including store prices (spec 12).

## 22. Outfit Wars and Competitive

Outfit Wars is a scheduled tournament where outfits fight each other on dedicated instanced maps, separate from the main war so it never splits its population. It runs a few times per year as an event, not as a permanent mode.

### Format

- **Teams:** outfits register a roster of 24 to 48 players.
- **Matches:** two outfits on an instanced continent section with a few bases, a set match length, and a clear win condition (most territory or the enemy's capital crystal).
- **Bracket:** qualifiers over two weekends, then a single-elimination finals weekend, streamed.

### Fairness

- Native client with commercial anti-cheat required (spec 3).
- Standardized loadouts available, so roster size and unlocks never decide outcomes.
- Observers and casters get the spectator tools from the Creators and Streamers spec.

### Rewards

- Cosmetics and titles only: outfit banners, hall trophies and Galleon-class liveries for winners and participants.
- Never power, never war points.

### Scheduling

- Run between main seasons or on quiet weekends, announced at least a month in advance.
- Only run once the main game has enough population that the event doesn't empty the war.

## 23. Content Pipeline and Art Budget

Cosmetic output is planned per season around what a solo developer plus contractors can realistically produce, with modular kits doing most of the work. Seasons, a featured shop slot, per-class hulls, mounts, pets and creator items add up to a treadmill sized for a team, so the budget below is a ceiling, not a minimum.

### Per-season budget (starting target)

| Item type | Per season | Source |
| --- | --- | --- |
| Character outfit sets | 4 | Contractors from modular kits |
| Weapon skin sets | 3 | Contractors, texture-based |
| Modular skyship pieces (sails, figureheads, lanterns, paint) | 8 | Contractors |
| Full skyship hulls | 1 (one class at a time) | Contractors |
| Mount or broom models | 2 | Contractors |
| Pets | 2 | Contractors |
| Fireworks, emotes, banners | 10 | In-house, template-based |
| Season pass signature vehicle item | 1 | Contractor |
| Creator or collaboration items | 1 to 2 | Partner-designed |

### Pipeline

- **Modular kits** for characters, ships and weapons, so new items are mostly recombination and texture work.
- **Asset validation tools** that check hitbox, silhouette and faction-readability rules automatically (Monetization spec).
- **A shared style guide** (spec 24) given to every contractor.
- **Item pipeline stages:** concept, approval, model, validation, in-game review, scheduled release.

### Scheduling

- Content for a season is finished at least one season ahead, so production delays never empty the shop.
- Shop rotation reuses returning items to stretch the catalog.

### Budget

Contractor spending is set per season as a share of the previous season's cosmetic revenue, with a fixed minimum during early access.

## 24. Lore and Worldbuilding

The world is a warm, adventurous sky-fantasy built around crystals: bright, colorful and hopeful rather than grim, in line with the Aesthetic draft's inspirations (FlyFF, MapleStory, Arcane, Skies of Arcadia, Xenoblade). Lore exists to give factions identity and events meaning, delivered in small pieces rather than walls of text.

### Core premise

Crystals hold up the world: they power floating cities, skyships and magic. Three factions fight over the crystal lattice that ties the continents together. Every major system (mana supply, the Gigas, floating structures, wizards) is an expression of crystal power.

### Faction pillars (proposals)

| Faction concept | Values | Visual identity |
| --- | --- | --- |
| An old order | Tradition, duty, stewardship of the crystals | Stone, gold, heraldry, cathedral-like ships |
| An industrious league | Invention, progress, harnessing crystals as engines | Brass, gears, glowing conduits |
| Sky-nomad clans | Freedom, kinship, living with the winds | Sails, banners, lightweight wood and cloth |

Faction names, colors and histories are finalized in this spec before character and ship art begins, since every cosmetic depends on them.

### Delivery

- **Small pieces:** item descriptions, crew banter, hall decorations, loading-screen vignettes.
- **Events:** the sky leviathan, Gigas launches and community milestones reveal lore chapters (Social Media and Community Events spec).
- **Seasons:** each season has a story theme that ties into its finale.

### Style guide

A shared style guide covers palettes, shapes, materials and tone per faction, the crystal visual language (bright, dim, empty), and rules for keeping cosmetics readable. It is given to every contractor (spec 23).

## 25. QA and Release Process

Automated tests and bot-driven load tests catch most regressions; a public test server catches the rest before players see them. Releases are small, frequent, behind feature flags, and always reversible.

### Automated testing

- **Unit and integration tests** for server logic, especially economy, capture and anti-cheat rules.
- **Deterministic simulation tests** replaying recorded inputs to catch desyncs.
- **Bot load tests** from the existing stress ladder on every release candidate, plus the vessel load test (spec 4).
- **Client performance benchmark** on every build (spec 15).

### Public test server

- A separate environment where players can try upcoming changes, with characters copied from live.
- Every gameplay or balance change spends at least one week there before release.
- Feedback collected through a Discord channel and an in-game survey.

### Release cadence

- **Weekly** small patches for fixes and tuning.
- **Monthly** content updates.
- **Seasonal** major updates aligned with the season pass.

### Safety

- **Feature flags** for every new system, so it can be switched off without a new build.
- **Rollback:** any release can be rolled back within 15 minutes; database migrations are written to be reversible.
- **Staged rollout:** new server builds go to one continent first, then everywhere.

### Patch notes

Every release has player-facing patch notes, including balance reasoning, published in-game, on the website and on Discord.

## 26. Platform Strategy

Browser first, for the zero-friction Join Now experience; a Steam desktop build at launch, for discovery, wishlists and a native client; consoles later, once the game is proven.

### Launch platforms

| Platform | Role | Notes |
| --- | --- | --- |
| Browser | Primary entry point; Join Now links | WebGPU with WebGL2 fallback (spec 15) |
| Steam (Windows, desktop wrapper) | Discovery, wishlists, store, Steam sign-in | Same client code and budgets as the browser |
| Native client with anti-cheat | Required for Outfit Wars; offered to anyone | Fallback if browser cheating becomes unmanageable (spec 3) |

### Later platforms

- **Linux and Steam Deck:** supported through the browser and Steam build once performance targets are met; tested before marketing it.
- **Consoles:** deferred until after launch. Requires controller balance (aim assist rules, spec 20), certification, platform store rules and crossplay decisions.
- **Mobile:** not planned; the screen size and controls don't suit the game.

### Cross-platform

- One account and one progression across browser, Steam and native clients.
- Cosmetics bought on one platform are available on all of them, subject to platform store rules.
- Input-based matchmaking is not needed in an open world, but controller aim assist stays limited to controllers.

## Sources

From the gap analysis research. Legal points need review by a lawyer; several PlanetSide 2 lessons come from community forums and reflect player perception rather than official data.

- [Game Design V1 (mmo-engine #19)](https://github.com/nbon12/mmo-engine/issues/19) and [Overall System (mmo-engine #3)](https://github.com/nbon12/mmo-engine/issues/3)
- [FTC COPPA Rule amendments overview](https://securiti.ai/ftc-coppa-final-rule-amendments/)
- [European Commission on the CPC Key Principles on in-game virtual currencies](https://commission.europa.eu/news-and-media/news/european-commission-hosts-stakeholders-talks-application-cpc-networks-key-principles-games-virtual-2025-06-03_en) and [Reed Smith Q&A on the guidance](https://www.reedsmith.com/articles/qas-on-the-eu-consumer-protection-authorities-joint-guidance-paper/)
- [Amazon on the New World launch (PC Gamer)](https://www.pcgamer.com/amazon-reflects-on-new-world-launch-the-numbers-grew-so-quickly-and-so-close-to-launch-it-caught-us-by-surprise/)
- [PlanetSide Arena (Wikipedia)](https://en.wikipedia.org/wiki/PlanetSide_Arena)
- [Daybreak: Dev Letter, Directives 2024](https://www.daybreakgames.com/press/ps2/article/dev-letter-directives-2024)
- [BattlEye coming to PlanetSide 2 (2016)](https://www.battleye.com/2016/05/17/battleye-coming-to-planetside-2-and-the-isle/)
- [PlanetSide 2: Nanite Systems Operatives launch](https://www.planetside2.com/news/ps2-nanite-systems-operatives-launch-2019)
- [PlanetSide 2 patch notes, September 17, 2026](https://www.planetside2.com/patch-notes/sep-17-patch-2026)
- [Worlds Adrift shutdown (PCGamesInsider)](https://www.pcgamesinsider.biz/news/69078/bossas-ambitious-spatialos-powered-worlds-adrift-is-shutting-down-in-july/)
- [Population balance in PlanetSide 2 (Loadout Optional)](http://loadoutoptional.com/2013/07/18/population-balance-ps2/)
