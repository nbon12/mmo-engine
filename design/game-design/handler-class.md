---
title: "Handler Class Spec"
type: game-design
source: https://claude.ai/artifact/GF7C9ZQYysKcp1VcVZG9BV
source_updated: 2026-10-05
imported: 2026-10-29
---

# Handler Class Spec

Oct 5, 2026 · @Nicholas Bonilla

## Overview

The Handler (working name) is a support class whose kit includes one or two companion bots, so a support player can fight and contribute without a squad. The player focuses on healing and support while the bots apply pressure on command.

Goals:

- **Solo support is viable.** A medic-style player who hasn't found a squad, or prefers not to join one, still has a meaningful role.
- **Real teammates are always better.** Joining a squad should feel like an upgrade, never a loss. The bots bridge to squad play rather than replace it.
- **Fair for the other side.** Enemies should read the bots instantly, kill them with normal weapons, and have a clear counter.

Inspiration: Kingdom Hearts party commands, and pet classes such as WoW's Hunter and Guild Wars 2's Ranger.

## Design pillars

1. **Shared power budget.** The Handler plus their bots should equal roughly one player in a fight, not two or three.
2. **Survival over killing.** Solo, the class wins by outlasting enemies through healing and sustain, not by out-damaging them.
3. **Readable machines.** Companions are clearly drones or robots, never mistakable for real players.
4. **People first.** Healing, reviving, and supporting humans always pays better than supporting your own bots.

Non-goals:

- A commander class that fields a squad of AI soldiers.
- Bots that count toward faction population or base capture.
- Deep micro-management of bots mid-firefight.

## Class kit

The Handler trades personal firepower for healing tools and companions. Values below are proposed starting points for playtesting.

**Player loadout**

- Primary weapon: lighter than standard infantry (e.g. SMG or carbine class), to pay for the bots in the power budget.
- Heal tool: heals players and repairs the Handler's own bots.
- Revive: same as other medic-style support.
- One utility slot: e.g. a short shield bubble or a recall that pulls bots back to the Handler.

**Companions**

| Companion | Role | HP | Notes |
| --- | --- | --- | --- |
| Assault drone | Suppresses and pressures enemies the Handler points at | Low | Main damage source; weaker than a player's rifle |
| Guard drone | Stays near the Handler, intercepts attackers, marks threats | Low–medium | The solo survival tool |

A loadout picks one or two companions depending on squad size (next section). Both are leashed to the Handler by range and shut down if they stray past it.

## Squad-size scaling

Companions are strongest when the Handler is alone and shift toward supporting the team as humans join, so the toolkit changes shape instead of disappearing.

| Situation | Companions | Behavior |
| --- | --- | --- |
| Solo (no squad) | 2 | Assault + Guard; full personal kit |
| Squad with 1–3 other humans | 1 | Player picks Assault or Guard |
| Squad with 4+ other humans | 0 combat bots | Bot slot becomes a squad-support drone that follows and heals or resupplies teammates |

While solo, companions also defend and assist nearby friendly players outside the squad. That lets a lone Handler naturally support whoever is fighting nearby.

Scaling changes apply at the Handler's next respawn or resupply, not mid-fight, so joining a squad never strips bots during a firefight.

## Commanding the companions

Control is a few stances plus ping commands, simple enough to use while healing in a firefight.

- **Stances** (toggle on one key): Aggressive (engage anything in range), Defensive (protect the Handler, engage only attackers), Passive (follow, never engage).
- **Ping to attack:** the standard ping on an enemy or vehicle sends the Assault drone after it.
- **Ping to hold:** pinging a location sends companions to guard it, within leash range.
- **Recall:** pulls companions back to the Handler.

Companions reuse the game's existing ping and spotting systems so players don't learn a separate control scheme.

## Death, respawn, and costs

The Handler is the high-value target: kill them and the companions go down too.

- **Handler dies:** companions power down within a few seconds. Revived Handler gets them back at their current HP.
- **Companion destroyed:** rebuild after a cooldown (starting point: 30–45 s), or instantly at a friendly terminal or resupply point.
- **Cost:** rebuilding costs a small amount of a resource. Whether that's Spice or a separate infantry resource is an open question.
- **Healing bots:** the Handler can repair their own bots, but at a reduced rate compared with healing players, to discourage a self-sufficient two-unit loop.

## Progression and rewards

The reward system steers Handlers toward real players without punishing solo play.

- Healing, reviving, and resupplying players gives clearly more XP than repairing your own bots.
- Kills by companions give the Handler partial credit, less than a personal kill.
- Squad play earns the normal squad XP bonus, so joining is always the higher-earning choice.
- After a few minutes solo, a light prompt surfaces nearby squads looking for support (e.g. "3 squads nearby need a medic").

Companion unlocks and upgrades are earned in-game like any other class progression. Cosmetic skins for companions fit the cosmetics-only monetization approach; no companion power is sold.

## Balance and counterplay

The main risk is pets that tank too well, the long-running complaint about pet classes in WoW PvP. Every rule below keeps the bots a pressure tool, not a wall.

**Counterplay for enemies**

- Companions die quickly to normal infantry weapons; no special counter needed.
- Killing the Handler disables the companions.
- Explosives, EMP-style grenades, or a dedicated anti-drone tool (if the game has one) deal bonus damage to companions.
- Companions carry an obvious silhouette, sound, and nameplate showing their owner.

**Limits**

- Cap Handlers per squad (starting point: 2) so squads can't stack companions.
- Companions never count toward faction population, base capture, or point holding.
- Companions can't operate vehicles, terminals, or deployables.

**Balance test:** a solo Handler with two companions should lose most 1v1s against a solo assault-style class at equal skill, but survive noticeably longer. If the Handler wins those fights, damage is too high.

## Technical considerations

Two bots per Handler is far cheaper than squad-scale AI, but at continent scale it still needs a budget.

- **Simple AI:** companions use short-range local pathing and a small behavior tree (follow, engage target, hold point), never map-wide navigation. The leash keeps pathfinding cheap.
- **Tick budget:** companion AI can update at a lower rate than players, with higher rates only while in combat.
- **Server authority:** companion state is simulated server-side, like other deployables, to prevent cheating.
- **Global cap:** the per-squad Handler cap plus an optional per-region companion cap protects server performance in large fights.
- **Stats API:** expose companion kills, healing, and uptime as separate stats so Handler performance can be tracked and balanced.

## Open questions

- [ ] Final class name (Handler is a placeholder).
- [ ] Is the Handler a new class, or a companion loadout on the existing medic-style class?
- [ ] Rebuild cost: Spice, a separate infantry resource, or cooldown only?
- [ ] Should solo companions assist non-squad allies by default, or only on command?
- [ ] Exact squad-size thresholds for dropping from 2 to 1 to 0 combat companions.
- [ ] Does the game have an anti-drone tool or EMP grenade companions should be weak to?
- [ ] Should companions exist in Lane War, or only in the main game?
