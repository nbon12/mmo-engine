---
title: "Join Now Onboarding Spec"
type: game-design
source: https://claude.ai/artifact/GsH1snnsYJRSoYuhdCg1vZ
source_updated: 2026-10-05
imported: 2026-10-29
---

# Join Now Onboarding Spec

Oct 4, 2026 · @Nicholas Bonilla

## Overview

A new player who clicks a social media link lands in the fight it showed within seconds of the game loading, in a seat where they can contribute immediately and cannot fail instantly. Everything else waits until after their first taste of the battle.

Goals:

- Put new players into the action immediately; players want to be part of the fight right away.
- Avoid the classic first-minute failure: spawning, dying without knowing why, and quitting.
- Give new players teammates and a purpose from the first moment.
- Stay fair to everyone already fighting.

## Entry points

- **"Join this fight" links:** every shared clip or live map view links into the game, landing the player at the same fight on the same continent.
- **"Join this galleon" posts:** a post announcing the recruit galleon ("The recruit galleon launches in 10 minutes, join it") links straight onto that ship.
- **Live web map:** anyone can watch the war in a browser, including the recruit galleon's live position, and join from there.
- **Pick a side online:** choosing a faction on the website or through social media leaves a cosmetic waiting at first login.

**Faction assignment:** the faction defaults to the one from the link, unless it is heavily overpopulated. Then the game suggests the underdog faction with a small welcome bonus.

## First-session flow

Click the link, watch the fight while loading, and land in a seat shooting at the same battle from the clip.

1. **Click the link.** The game opens (or the download starts, with the live web map to watch meanwhile).
2. **Live loading screen.** Instead of a static screen, a live view of the fight they are joining builds anticipation.
3. **No setup screens.** A default character with an auto-generated name; faction from the link.
4. **Land in a seat.** A gunner seat on the recruit galleon or another friendly skyship, or an anti-air or base turret at that fight. No navigation or loadout decisions, real impact, and some protection from the vehicle around them.
5. **Squad and voice.** They are already in the crew's squad and can hear their crew (see Squad and voice).
6. **First death is a pause point.** Fast respawn, a death recap, and offers to switch roles, join a different squad, or customize their character.

Customization, loadouts, the full tutorial and the practice range are all available later, never required up front.

## The recruit galleon

Each faction has a dedicated 32-seat recruit galleon that new players join from "join this galleon" links. It flies into a real battle with a clear mission.

- **One per faction, not one per click.** New arrivals fill the same ship; a second launches only when the first is full. Spawning a warship per click would flood the war with free capital ships.
- **Bots crew the empty stations:** gunners and repair crew, clearly weaker than players. Real players replace bots as they arrive, so it works with 2 joiners or 30.
- **Mana shield on launch:** a visible shield absorbs damage for the first few minutes so the ship reliably reaches the fight. Once it drops, the galleon is a normal target.
- **A mission:** each launch is assigned to assault a nearby contested base, shown clearly to everyone aboard (for example, "Break the shield generator at Crystal Ridge").
- **Modest firepower:** a spectacle and learning space, not a decisive weapon. It cannot take a base on its own.
- **Its own identity:** a name and appearance per faction, and a live position on the web map so people outside the game can watch it fly in.

## Squad and voice

New players have teammates and can hear them from the first moment, the way Sea of Thieves crews and PS2 proximity chat bring fights to life.

### Defaults

- **Auto-squad:** everyone who joins the recruit galleon goes into its squad, or platoon once full. Squad markers, objectives and the crew list work immediately.
- **Listening on, microphone opt-in:** new players hear their crew right away. Talking is one prompt away ("Press T to talk to your crew"), with push-to-talk as the default.
- **Squad voice first:** proximity voice with strangers and enemies is a toggle players discover later.
- **Newcomer-friendly squads:** after the first session, offer open squads led by players earning the mentoring bonus.

### Safety

- One-click mute and report for any player, plus "mute all".
- Pings and quick commands ("Need repairs!", "Enemy above!") so players without a mic can still communicate.
- Voice moderation tools and clear reporting. Recording or analyzing voice for moderation has privacy requirements to check.
- Voice off by default for players who indicate they are under a certain age. Several regions require extra care with minors and voice.

## Teaching, first deaths and fallbacks

### Teaching in context

- One prompt at a time, only when it becomes relevant ("Press X to fire", "That marker is the objective", "Hold E to repair").
- No upfront tutorial. The full tutorial and a practice range with bots stay available any time.

### First deaths

- Fast respawns and a clear death recap (who killed you, with what, from where) for the first few lives.
- After the first death, offer role switching, squad options and character customization.

### Fallbacks

- **No free seats:** spawn as infantry at the nearest friendly spawn, with a marker pointing to the action and prompts as they move.
- **Recruit galleon destroyed:** respawn options point to the nearest friendly spawn or the next recruit galleon launch.
- **Download time:** the live web map lets people watch the war while the game downloads, so they arrive knowing where the action is.

## Fairness guardrails

- **Protect the ship, not the players.** New characters are never immune, since shooting someone who cannot die feels unfair. The recruit galleon's shield is visible and temporary, so enemies can see it and plan around it.
- **Normal spawn protection only,** the same short protection every player gets.
- **Limited firepower:** the recruit galleon cannot decide a fight on its own.
- **One recruit galleon per faction at a time,** with a second only when the first is full.
- **Bots stay weaker than players** and give way to real players as they join.

## Open questions

- [ ] How long the recruit galleon's launch shield lasts.
- [ ] How missions are chosen: nearest contested base, a hand-picked target, or player vote.
- [ ] Launch cadence: continuous, or scheduled launches tied to social posts.
- [ ] Should veterans be allowed to join the recruit galleon, and if so, how many?
- [ ] How "new player" is defined for welcome bonuses and gentle first deaths.
- [ ] Age signals and voice defaults per region.
- [ ] Whether to support play-while-downloading to cut first-session wait.
