# Anti-cheat strategy for the browser client

**2026-11-21 · analysis and proposal, no decision**

This note proposes a defense strategy for a browser client. It is input to open decision 9
("Anti-cheat approach for a browser client") and touches open decision 5 (hit detection). It
settles neither, and doesn't change the architecture spec (`docs/architecture/server-architecture.md`).
Settling decision 9 is a constitution amendment, which stays owner-merged.

It works backwards from what a capable cheat developer would do, then orders the defenses by how
well they hold against that person. It also keeps the product goal of one-tap joining from a
social link.

## Short answer

1. **The client can't be trusted, and in a browser it can't even be watched.** A cheater can run
   a modified Chromium, hook browser APIs below our code, read the GPU's draw calls, drive our real
   client headless, or aim through screen capture and a hardware mouse. No client-side check can
   see those. So everything that must hold is enforced on the server.
2. **Four server-side layers do the real work:** the authoritative server (nothing to write),
   minimal disclosure (nothing to read), detection from server telemetry (behavior can't be faked
   perfectly), and account economics (a ban has to cost something).
3. **Client hardening is a cost multiplier, not a wall.** Per-build obfuscation and rotating
   protocol encoding turn free copy-paste cheats into maintained products that break on every
   update. Its biggest payoff is detection: a cheat that missed a rotation gives itself away.
4. **Instant joining stays.** Friction scales with privileges, not with joining. Guests are
   cheap to make and have little to cheat with, and detection can be far stricter for them.
5. **Synthetic cheaters are the test suite.** Each layer is verified by scripted cheating clients
   that must be rejected or caught. No layer is accepted on review alone.

## Who we're defending against

| Tier | Who | Typical tools | What stops them |
| --- | --- | --- | --- |
| **T0, casual** | Copies a script from a forum | Userscripts, extensions, devtools edits, memory editors | Server authority; per-build obfuscation breaks their scripts |
| **T1, maintained cheat** | Sells or maintains a cheat | Browser-API hooks, automated updaters that match code by behavior, black-box calls to our encoder | Minimal disclosure, server detection, rotation tripwires, delayed bans |
| **T2, top end** | Determined, technical | Modified Chromium, read-only hooks, headless real client, screen-capture aimbots with hardware input | Only the server-side layers. Client measures are blind here |

T0 is most of the volume. T2 is a few people. The strategy is built so that T2 is contained by the
server and T0 and T1 are priced out.

## Layer 1: the server decides everything

Principle IV already requires this. What it means against the moves above:

- **Inputs only.** The client sends inputs and intents, never results. Position, damage, ammo,
  cooldowns and inventory change only through server rules.
- **Validate every message** for size, rate, value ranges, sequencing and permission (Principle
  IV), and check movement against the physics the server runs: speed, acceleration, collision.
- **Hit detection.** In a browser there's no client-side anti-cheat to fall back on, so the
  margin decision 5 gives cheaters weighs more than it would natively. Whichever option decision 5
  picks, design the server so that hit claims, if used, are checked against rewound hitboxes
  (Combat netcode, lag compensation) and against range, fire rate and line of fire, as the
  constitution already requires for that option.

## Layer 2: send nothing a cheater could use

Against ESP, this is the only defense that works. A wallhack can only show what reached the client.

- **Occlusion-aware disclosure.** On top of the distance tiers and caps, don't send an enemy's
  position until it could become visible to the player soon. "Soon" covers the player's latency
  and the time to round a corner, so honest players don't see enemies pop in.
- **Coarse audio and minimap.** Send sound events with rough positions, and show a minimap only
  what the player's side has spotted.
- **No hidden state in snapshots.** Cloaked players, other players' loadouts and cooldowns,
  objective timers the player shouldn't see: none of it leaves the server.
- **Cost.** This runs in the delivery workers, off the tick path. Its cost has to come from load
  tests, alongside decision 2.

## Layer 3: detect from server telemetry

Detection uses only what the server saw, so a modified browser can't hide from it.

- **Aimbots:** reaction time to newly visible targets, how fast the aim snaps and how it settles,
  accuracy and headshot rate compared with the player's peers, accuracy when the shooter is moving.
- **ESP:** the crosshair tracking enemies through walls, and pre-aiming at corners an enemy is
  about to come round. The server knows exactly what each client was sent and when, so "reacted to
  something it couldn't see" is measurable.
- **Bots:** input timing that is too regular, perfect paths, sessions no human keeps up.
- **Tripwires from Layer 4:** old-format messages, failed or late integrity answers, calls to decoy
  functions. An honest client never produces any of them.
- **Response:** each signal adds to a per-account score. Sanctions are automatic and scale with the
  score and the account's trust tier. Bans go out in delayed waves, so a cheat maker can't tell
  which change caught them. Clear-cut tripwires can act at once for guests, who lose nothing by a
  kick.
- **Where it runs:** the telemetry pipeline from spec 014 (`specs/014-telemetry-test-harness/`)
  is the natural home for these signals. The game servers only record events. Scoring runs
  elsewhere, off the tick path.

## Layer 4: harden the client to raise the cost

None of this is relied on for enforcement. It shortens the life of each cheat and feeds Layer 3.

- **Every build:** randomized identifier mangling, encoded values (health and ammo stored masked,
  with a new key per build), restructured modules, encrypted constants, and a shuffled protocol
  (opcodes, field order and a light payload encoding). The server and client get matching tables
  from the same seeded build step.
- **Every session:** the protocol codec and the integrity checks are picked from a large pool of
  pre-built variants.
- **During a session:** only the small codec and integrity modules rotate, every few minutes.
  Integrity challenges are tied to live session state (recent inputs, current frame data), so a
  clean second client can't answer them for a modified one.
- **Not done:** swapping the whole client mid-session (frame hitches in an FPS cost honest players
  more than it costs cheaters), control-flow flattening in hot loops (render, prediction,
  networking), and debugger detection that breaks honest players' browsers.
- **Design rule to follow now:** keep the protocol codec and the integrity checks as small,
  isolated modules the client can load and swap at runtime. That keeps rotation possible later
  without a rewrite, whatever decisions 4 and 9 settle.
- **Operations:** source maps for each build stay private and go to the error tracker, so crash
  reports can be decoded.

## Layer 5: make accounts worth losing, without a sign-up wall

In a browser, ban evasion is a new account and a new browser profile, so the cost has to come from
the account. Friction scales with privileges, not with joining:

| Tier | How you get it | What you can do |
| --- | --- | --- |
| **Guest** | Tap the link | Fight, move, default loadout, quick-chat only |
| **Claimed** | One tap with an existing identity provider (Google, Discord, Apple) | Keep progress, free chat, outfits, unlocks |
| **Trusted** | Hours played with clean scores, account age | Ranked play, leaderboards, trading, outfit leadership |
| **Verified** | Phone number or a purchase | Economy, tournaments, roles near administration |

- **Guests have little at stake.** Their kills count in the fight, but nothing they do lasts:
  no leaderboards, trading or ranked play, and limited effect on persistent outcomes.
- **Guests get strict detection.** A false positive costs an honest guest one re-tap, so the
  thresholds can be much stricter than for long-standing accounts.
- **Friction hides in the load.** While assets download: an invisible bot check, a small
  proof-of-work, and rate limits on guest creation per IP range, tighter for datacenter and VPN
  ranges.
- **Invites carry trust.** A trusted player's link gives invitees a slightly better start. It
  stops doing so if the people they invite keep getting caught.
- **Automated guards** (Principle XIII's "Automation, not approval"): a kill switch for guest creation per
  source or IP range, an overflow instance for guests when cheating spikes on a continent, and a
  cap on the share of guests per continent.

Every tier is a permission on the one shared account identity the constitution requires, so the
tiers don't conflict with it.

## Attacker moves and what answers them

| Move | Client-side answer | What actually holds |
| --- | --- | --- |
| Hook browser APIs, or run a modified Chromium | None | Layers 1–3 |
| ESP from WebGL draw calls (read-only) | None | Layer 2 |
| Call our own encoder as a black box | Rotation of the codec slows it down | Layer 3 behavior, Layer 1 limits |
| Find values by what changes, read the HUD | Value encoding slows it down | Layer 1: changing them does nothing |
| Behavior-matching updater per build | Structural changes per build | Tripwires feed Layer 3 |
| Hide from integrity checks, or answer them from a clean client | Challenges tied to session state | Layer 3 |
| Safe mode, throwaway test accounts, watching ban waves | Tripwires that change per build | Delayed waves, Layer 5 |
| Headless real client with stealth patches | Weak | Layer 3 bot signals, Layer 5 creation limits |
| Screen-capture aimbot with a hardware mouse | None | Layer 3 aim statistics |

## What we accept

- A determined cheater can make a fresh guest and spoil a few fights before being caught. The
  strict guest thresholds, creation limits and guest cap keep that window short and the volume low.
  That is the price of one-tap joining.
- A careful T2 aimbot that stays within human statistics won't be caught quickly. It is also
  limited to what a very good human could do.
- ESP limited to what Layer 2 sends (players about to come into view) still gives a small edge.

## How each layer is tested

Principle IX requires automated tests, so each layer is verified by synthetic cheaters run against
the server in CI and in the harness:

- **Layer 1:** scripted clients that send speed and teleport inputs, out-of-range values, too many
  messages and fake hit claims. Every one must be rejected.
- **Layer 2:** given a map, a set of positions and a latency, no enemy that can't become visible
  in time appears in a client's stream.
- **Layer 3:** replays of scripted aimbots, ESP-driven aiming and bot input against recorded
  honest-player baselines. Each must be flagged above a set rate, with false positives on the
  baselines below a set rate.
- **Layer 4:** a client that is one rotation behind trips the tripwire. A build's codec tables match
  the server's.
- **Layer 5:** guest creation limits and the kill switch, overflow and cap guards trigger at their
  thresholds.

## Order of work

What doesn't depend on decision 9 can start whenever the relevant feature is specified:

1. Layer 1 and the baseline of Layer 2 (interest-management tiers and caps, closed portals, and
   no hidden state in snapshots) are already required by Principle IV and land with the combat and
   delivery features. Occlusion-aware disclosure and coarse audio and minimap go further; their
   cost comes from load tests (Layer 2).
2. Record the server-side events Layer 3 needs (shots, aim samples, what each client was sent and
   when) from the start. That data is cheap to record and impossible to recover later.
3. Keep the codec and integrity checks as swappable modules in the client from its first version.
4. Account tiers and their guards come with the account and onboarding features.
5. Detection scoring, the obfuscation pipeline, rotation and integrity challenges are the parts
   decision 9 would settle. Build them when that decision is amended, or sooner if cheating shows
   up in testing.

## Open decisions this touches

- **9, anti-cheat approach.** This note is a proposal for it. Settling it needs an amendment.
- **5, hit detection.** In a browser, the room client-reported hits leave cheaters weighs more,
  because there's no client-side anti-cheat to make up for it. This note doesn't choose.
- **4, transport.** Rotating the protocol encoding works over WebSocket or WebTransport. This note
  doesn't choose.
