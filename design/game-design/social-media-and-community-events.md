---
title: "Social Media and Community Events Spec"
type: game-design
source: https://claude.ai/artifact/DRRvJYeKSW7UnTzEZztVLT
source_updated: 2026-10-05
imported: 2026-10-29
---

# Social Media and Community Events Spec

Oct 4, 2026 · @Nicholas Bonilla

## Overview

Before launch, social media exists to build a community big enough to fill the first playtests and launch-day servers. Population is the game's biggest risk, and this is where it gets solved early.

Goals:

- Grow Steam wishlists and a Discord community ahead of launch.
- Turn social interest into players with as little friction as possible.
- Give followers who don't play yet a real, visible role, without ever letting social engagement decide fights.
- Reach PlanetSide players, the game's natural first audience.

## Platforms and content

A few platforms done consistently, all pointing back to the Steam page and Discord.

### Hubs

- **Steam page,** live as soon as there is something to show. Wishlists are the key pre-launch number, since they drive launch-day visibility.
- **Discord** as the community home: playtest signups, feedback, announcements, and where future outfits form.

### Platforms

- **Short-form video** (TikTok, YouTube Shorts, Instagram Reels): where indie games most often break out. Gigas crashes and skyship drops are the clips.
- **Reddit:** gamedev, indie game and especially PlanetSide communities. Follow each subreddit's self-promotion rules and participate genuinely.
- **X or Bluesky** for the gamedev community, with traditions like #ScreenshotSaturday.
- **YouTube devlogs,** roughly monthly.

### Content pillars

- **Spectacle:** big fights, Gigas battles, skyship crews in action.
- **Design decisions:** why there is no pay-to-win, why creeps exist, why breaches are permanent.
- **Engineering:** moving-platform netcode and self-hosted infrastructure.
- **Community:** playtest highlights, player clips, outfit stories.

### Approach

- **PlanetSide players first:** reach them respectfully, without bashing PS2.
- **Sustainable cadence:** two or three short clips a week and a monthly devlog. Record gameplay during development and clip it later.
- **Playtests as content:** schedule large playtests as promoted events and invite streamers. They test the netcode and produce the best footage.
- **Show what exists,** not what is planned. Avoid overpromising.

## Low-friction entry

Every path from a social post should lead into the game in as few steps as possible.

- **"Join this fight" links:** every shared clip or live map view links straight into the game, landing the player on the same continent and faction where possible.
- **Live web map:** built on the stats API, anyone can watch the war in a browser (front lines, active fights, Gigas events, sky leviathan crossings) with nothing to install.
- **Pick a side online:** choose a faction on the website or through social media and find a cosmetic waiting at first login.

## Follower milestones

Shared community milestones unlock things for everyone, in the style of creators who animate "at 1 million followers the city grows". Faking followers only makes a milestone arrive early, which hurts nobody.

### Core rewards

- **Free cosmetic for everyone** at each milestone (a commemorative banner, firework, sail or pet).
- **Planned buildings open:** non-gameplay features already on the roadmap, such as a social lounge, launch as "the community built this". Scaffolding rises in the capitals as the count climbs, shown in-game and on social media.
- **Fallback date:** each building unlocks at its milestone or on a set date, whichever comes first, so finished features never sit locked away.
- **Reachable targets** set from the actual growth rate.

### Pre-launch ideas

- **Bigger playtests:** for example, "at 10,000 Discord members, a 48-hour playtest opens for everyone". The top priority, since the game needs crowds to test properly.
- **Feature reveals:** each milestone unlocks a devlog showing a new system.
- **Founder wall:** a capital monument engraved with early community members' names, unveiled at launch.

### Other ideas

- **Naming rights:** the community names the next Gigas, a region, a creep species or a landmark.
- **Community design contest:** the winning cosmetic design is produced, credited to its designer.
- **Lore chapters** and **soundtrack releases.**
- **Sky events:** an aurora or meteor shower over every continent for a weekend, as long as it never reduces visibility in fights.
- **Celebration weekends:** fireworks festivals or double XP for everyone.
- **Special event modes,** such as a Lane War preview weekend.
- **Charity milestones:** a donation to a cause the community picks.

Growing the capitals themselves with milestones needs more design work before committing to it.

## Sky leviathan world event

A colossal sky creature crosses the continent several times over a multi-day event. Each crossing is a short fight; damage from every crossing adds to one global health bar, and the final crossing ends with a finale and season reveal.

### References

- **Breath of the Wild's dragons:** fly their own routes across Hyrule and appear periodically; players intercept them and shoot parts for materials.
- **Fortnite's Pandora's Box and Doctor Doom:** community-wide health bars, with a hidden damage multiplier Epic raised over time (up to 30x) to land the finale on schedule. Lesson: PvP players will grief people trying to shoot it.
- **PlanetSide 2's Haunted Bastion:** a Halloween hostile Bastion that appears and dies quickly once players pile on. Lesson: short, satisfying fights.

### Crossings

- The creature crosses on a route lasting a few hours, passing over front lines and active fights so it comes to the war instead of pulling players away from it.
- Each crossing is announced on the map with its route so players can plan an intercept. Between crossings it is gone, which prevents camping.
- Each crossing ends when players deal enough damage to drive it off, wounded. The threshold scales with population so quiet crossings don't drag and busy ones aren't over instantly.

### Damage

- Several weak points spread across its body (like the Bastion's eight), so players around and below it all have targets.
- Ground forces can reach it with crystal-powered ground cannons, so it is not only an air event.
- All factions can shoot it while still fighting each other: three-way chaos is part of the fun.

### Global health and finale

- One global health bar across all servers and continents, shown in-game, on the live web map and on social media.
- A hidden damage multiplier guarantees the final crossing lands on the planned date regardless of population or social engagement.
- The finale is a story beat, for example the creature's fall revealing a new region or launching the next season.

### Rewards

- Every player who hits it during a crossing earns a small reward (a fragment or cosmetic material) toward a collection, Zelda-style.
- Everyone gets a commemorative cosmetic when it falls.
- A faction contribution leaderboard for bragging rights; the top faction may get a cosmetic celebration but nothing that affects the war.

### Technical

- Each server reports damage to a central aggregation service that updates the global health bar every few seconds and feeds the public API for the web map and stream overlays.

## The crystal cannon

Social engagement charges a giant crystal cannon in the capitals that fires at the sky leviathan, giving followers who don't play yet a visible role in the event.

1. **Charge:** the crossing announcement post ("The leviathan crosses tonight at 8pm") doubles as the cannon's charge. Every like on it charges the cannon.
2. **Fire:** when the creature appears, the cannon fires a volley sized to the charge, visible to everyone in-game and in the clip posted afterward.
3. **Live boost:** if the crossing is streamed, live likes, comments or Twitch chat fire extra shots in real time.
4. **Recap:** the highlight clip goes out with "your likes dealt X damage", closing the loop and setting up the next crossing.

### Limits

- **Capped share:** social damage is a bounded portion of each crossing, so players in the game always do most of the work and inflated likes cannot decide anything.
- **Platform rules:** platforms discourage posts that explicitly ask for likes, so frame it as an event ("every like on tonight's post charges the cannon") and check current rules.
- **APIs:** platforms typically expose like counts on your own posts but not individual likes in real time, so poll the count periodically. Twitch chat is much easier to hook into live.

## Guardrails

- **Social engagement never decides fights between factions.** Likes can be botted or bought, and losing a battle to TikTok engagement would feel unfair.
- **Shared, not competitive:** milestones and the leviathan are goals everyone works toward together. Faction rivalry stays limited to bragging rights.
- **No gameplay content held hostage:** new continents, vehicles and gameplay features ship when ready, never gated behind follower counts.
- **Developer controls timing:** hidden multipliers and fallback dates mean neither low engagement nor faked engagement can derail the schedule.

## Open questions

- [ ] The creature's name and lore ("sky leviathan" is a placeholder).
- [ ] Crossing frequency and event length.
- [ ] Share of each crossing's damage that social engagement can provide.
- [ ] Should areas where players shoot the leviathan offer any protection from griefers, or is being hunted while shooting part of the event?
- [ ] Which platforms charge the cannon at launch (Instagram, TikTok, Twitch chat).
- [ ] Milestone thresholds and which roadmap buildings to attach to them.
- [ ] Whether and how the capitals themselves grow with milestones.
