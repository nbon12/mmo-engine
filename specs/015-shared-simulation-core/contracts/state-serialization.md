# Contract: Canonical state (serialisation version 1), state hash and step record

The canonical state is the byte form of one world's state after a step: its characters with their
weapons, its projectiles, and the events of the step that produced it. It is what the state hash
covers (FR-016), what a recorded stream stores as its initial state, what the step records carry
between builds in the agreement suite, and what the divergence report decodes field by field
(FR-026).

- **No identifier of its own.** The state is always inside a versioned container. Recorded stream
  format version 1 pins state serialisation version 1 and input command version 1
  ([recorded-stream-format.md](recorded-stream-format.md)). A change to anything here is a new
  stream format version, and changes every hash, so it also bumps the rules revision.
- **Writer.** `SimWorld.Step` writes it at stage 6 of the world step order
  ([data-model.md](../data-model.md)): canonicalise, check, serialise, hash, then commit. The world
  owns the buffer, sized from the configured maxima, so the step allocates nothing (FR-032).
- **One layout table.** `StateSerializer` (`src/MmoEngine.SimCore/Formats/`) holds the table below
  as data. The writer's fault paths, the reader and the divergence report all use it, so they cannot
  disagree.

## Conventions

- Little-endian everywhere, through `BinaryPrimitives`. Types are written `u8`, `u16 LE`, `i16 LE`,
  `u32 LE`, `i32 LE` and `f32 LE`.
- Floats are IEEE 754 binary32 bits. Every float in a canonical state is finite and is never
  negative zero (see [Canonicalisation](#canonicalisation-and-the-finite-check)).
- No padding and no alignment. Records follow each other directly.
- Characters are in ascending character ID, each followed by its weapons in slot order. Projectiles
  are in ascending projectile ID. Every event list has its own fixed order, below.
- Reserved bits are written as 0.
- The frame is Y up, metres ([data-model.md](../data-model.md)). Positions are the feet.

## Layout

With c = `characterCount`, p = `projectileCount` and W = `input.weaponSlots` (every character has W
weapons), a character record is R = 52 + 30·W bytes:

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 12 | header | `step`, `characterCount`, `projectileCount`, `nextProjectileId` | below |
| 12 + R·i | R | character | `characters[i]` | i from 0 to c − 1, ascending ID |
| 12 + R·c + 49·j | 49 | projectile | `projectiles[j]` | j from 0 to p − 1, ascending ID |
| 12 + R·c + 49·p | 12 + event bytes | step events | `events` | six lists, each with a `u16` count |

```text
stateLength = 24 + c·(52 + 30·W) + 49·p + 25·shots + 26·hits + 5·(expiries + inputRejections + fireBlocks + movementOutcomes)
```

The 24 constant bytes are the 12-byte header and the six 2-byte event counts.

### Header (12 bytes)

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `step` | the step this state is the result of; the initial state's own step number for a loaded state |
| 4 | 2 | u16 LE | `characterCount` | ≤ `world.maxCharacters` (at most 1024) |
| 6 | 2 | u16 LE | `projectileCount` | ≤ `world.maxProjectiles` (at most 16384) |
| 8 | 4 | u32 LE | `nextProjectileId` | ≥ 1 and greater than every projectile ID in the state |

Total: 12 bytes.

### Character record (52 bytes + 30 per weapon)

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `id` | unique, strictly ascending; also the spread hash's player ID (FR-008) |
| 4 | 4 | f32 LE | `position.x` | the feet (the capsule bottom); \|v\| ≤ 8192 m; inside the playable area |
| 8 | 4 | f32 LE | `position.y` | up |
| 12 | 4 | f32 LE | `position.z` | |
| 16 | 4 | f32 LE | `velocity.x` | m/s |
| 20 | 4 | f32 LE | `velocity.y` | m/s, up |
| 24 | 4 | f32 LE | `velocity.z` | m/s |
| 28 | 4 | f32 LE | `groundNormal.x` | the support normal while grounded; (0, 1, 0) while airborne |
| 32 | 4 | f32 LE | `groundNormal.y` | while grounded, ≥ `CosSlopeLimit` |
| 36 | 4 | f32 LE | `groundNormal.z` | |
| 40 | 2 | u16 LE | `aimYaw` | binary angle, 65,536 per turn; yaw 0 faces +Z |
| 42 | 2 | i16 LE | `aimPitch` | [−16384, 16384]; positive looks up |
| 44 | 1 | u8 | `stance` | 0 Standing, 1 Crouched |
| 45 | 1 | u8 | `flags` | bit 0 Grounded, bit 1 ClampedToBounds (in the last step); bits 2 to 7 are 0 |
| 46 | 4 | u32 LE | `lastSequence` | sequence of the last applied input command; 0 before the first |
| 50 | 1 | u8 | `activeSlot` | below `weaponCount` |
| 51 | 1 | u8 | `weaponCount` | equals `input.weaponSlots` (1 to 8) |
| 52 | 30·W | weapon × W | `weapons[s]` | slot order, s from 0 to W − 1 |

Total: 52 bytes before the weapons (4 + 9·4 + 2 + 2 + 1 + 1 + 4 + 1 + 1), 112 bytes with the
placeholder two slots.

### Weapon record (30 bytes)

The weapon state holds no floats ([research R9](../research.md#r9-weapons)).

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 1 | u8 | `phase` | `WeaponPhase`: 0 Ready, 1 Firing, 2 Reloading, 3 Switching, 4 Overheated |
| 1 | 1 | u8 | `flags` | bit 0 TriggerLatched; bits 1 to 7 are 0 |
| 2 | 2 | u16 LE | `phaseSteps` | steps left in Switching or Reloading; the equip time owed while Overheated; otherwise 0 |
| 4 | 4 | i32 LE | `fireWait` | fire-rate units still to wait; 0 ≤ `fireWait` < `ShotCost` |
| 8 | 4 | u32 LE | `shotCounter` | shots fired by this weapon; wraps at 2³² |
| 12 | 2 | u16 LE | `magazine` | ≤ `magazineSize` |
| 14 | 2 | u16 LE | `reserve` | rounds outside the magazine |
| 16 | 4 | u32 LE | `heat` | heat × `stepRateHz` |
| 20 | 2 | u16 LE | `stepsSinceShot` | saturates at 65535 |
| 22 | 4 | i32 LE | `recoilPitch` | binary angle units, [0, `RecoilMaxPitchUnits`] |
| 26 | 4 | i32 LE | `recoilYaw` | binary angle units, [−`RecoilMaxYawUnits`, `RecoilMaxYawUnits`] |

Total: 30 bytes (1 + 1 + 2 + 4 + 4 + 2 + 2 + 4 + 2 + 4 + 4).

### Projectile record (49 bytes)

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `id` | ≥ 1, strictly ascending; taken from `nextProjectileId` |
| 4 | 4 | u32 LE | `ownerId` | the shooter's character ID; the owner's hitboxes are never tested |
| 8 | 1 | u8 | `class` | below the number of `projectileClasses` |
| 9 | 4 | u32 LE | `launchStep` | the step it was fired in; ≤ `step` |
| 13 | 4 | f32 LE | `origin.x` | the shooter's eye at launch; \|v\| ≤ 8192 m |
| 17 | 4 | f32 LE | `origin.y` | |
| 21 | 4 | f32 LE | `origin.z` | |
| 25 | 4 | f32 LE | `launchVelocity.x` | m/s, `direction · muzzleSpeed` |
| 29 | 4 | f32 LE | `launchVelocity.y` | |
| 33 | 4 | f32 LE | `launchVelocity.z` | |
| 37 | 4 | f32 LE | `position.x` | p(k + 1) with k = `step` − `launchStep`: derived, but stored and hashed, so a one-ulp error in the ballistic evaluation shows at its own step (SC-002) |
| 41 | 4 | f32 LE | `position.y` | |
| 45 | 4 | f32 LE | `position.z` | |

Total: 49 bytes (4 + 4 + 1 + 4 + 9·4).

### Step events

The events of the step that produced the state. A loaded state (an initial state, or a `Reset`) has
all six counts 0. The events are hashed, so a build that fires one more shot or reports another
hitbox diverges at that step. Each list is its `u16 LE` count, then its records, in this order:

| # | List | Count field | Record size | Order |
| --- | --- | --- | --- | --- |
| 1 | `events.shots` | `events.shots.count` | 25 | ascending character ID, then firing order |
| 2 | `events.hits` | `events.hits.count` | 26 | ascending projectile ID |
| 3 | `events.expiries` | `events.expiries.count` | 5 | ascending projectile ID |
| 4 | `events.inputRejections` | `events.inputRejections.count` | 5 | ascending character ID |
| 5 | `events.fireBlocks` | `events.fireBlocks.count` | 5 | ascending character ID |
| 6 | `events.movementOutcomes` | `events.movementOutcomes.count` | 5 | ascending character ID; only characters with non-zero flags |

Shot (25 bytes; US4):

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `characterId` | the shooter |
| 4 | 1 | u8 | `slot` | the weapon slot |
| 5 | 4 | u32 LE | `shotCounter` | the counter value the spread hash used, before the shot increments it |
| 9 | 4 | u32 LE | `projectileId` | the projectile it spawned |
| 13 | 4 | f32 LE | `direction.x` | unit vector inside the spread cone |
| 17 | 4 | f32 LE | `direction.y` | |
| 21 | 4 | f32 LE | `direction.z` | |

Hit (26 bytes; FR-010, US5):

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `projectileId` | |
| 4 | 1 | u8 | `kind` | `HitKind`: 0 Terrain, 1 Mesh, 2 Character |
| 5 | 4 | u32 LE | `targetId` | the character ID, or the triangle ID for terrain and meshes ([collision-data-format.md](collision-data-format.md#triangle-ids)) |
| 9 | 1 | u8 | `hitbox` | `HitboxPart`: 0 Head, 1 Torso, 2 Legs, 255 None for terrain and meshes |
| 10 | 4 | f32 LE | `point.x` | the impact point |
| 14 | 4 | f32 LE | `point.y` | |
| 18 | 4 | f32 LE | `point.z` | |
| 22 | 4 | f32 LE | `t` | fraction of the step's segment, [0, 1]; 0 for a start inside a hitbox |

The four 5-byte records:

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `projectileId` (expiries) or `characterId` (the other three) | |
| 4 | 1 | u8 | `reason` (expiries: `ExpiryReason` 1 Range, 2 Lifetime, 3 OutOfBounds; input rejections: `InputRejection` 1 to 4; fire blocks: `FireBlock` 1 to 6) or `flags` (movement outcomes: `MovementOutcome` bits 1 HeadroomBlocked, 2 Stuck, 4 SlideCapReached, 8 ClampedToBounds) | never 0 |

Total: 5 bytes (4 + 1). The code values are those of [data-model.md](../data-model.md) and
[input-command-format.md](input-command-format.md#validation).

## Maximum length

Each list's maximum is its event buffer's capacity in the world ([data-model.md](../data-model.md)):
shots at most `world.maxCharacters` × S, where S is the largest `maxShotsPerStep` over the weapons;
hits and expiries at most `world.maxProjectiles` each; the other three at most `world.maxCharacters`
each. With C = `world.maxCharacters`, P = `world.maxProjectiles` and W = `input.weaponSlots`:

```text
MaxStateLength = 24 + C·(67 + 30·W + 25·S) + 80·P
```

Per character: 52 + 30·W for the record, 25·S for shots and 15 for the three 5-byte lists. Per
projectile: 49 for the record, 26 for a hit and 5 for an expiry.

| Configuration | C | P | W | S | MaxStateLength | MaxStepRecordLength |
| --- | --- | --- | --- | --- | --- | --- |
| Placeholders (`simcore/config/default.json`) | 64 | 1024 | 2 | 1 | 91,672 bytes | 91,688 bytes |
| Every limit at its maximum | 1024 | 16384 | 8 | 8 | 1,829,912 bytes | 1,829,928 bytes |

`CompiledConfig.MaxStateLength` and `CompiledConfig.MaxStepRecordLength` (MaxStateLength + 16) hold
these values. They size the world's state buffer and every fixed-capacity record buffer. The state
buffer is a `byte[]`, not a stackalloc: about 90 KB is too large for the WebAssembly stack, and a
stackalloc inside a loop trips CA2014 ([research
R13](../research.md#r13-canonical-state-and-the-step-hash)).

## Field paths

`StepFault` and the divergence report name a field by its path through this layout:

| Part | Paths |
| --- | --- |
| Header | `step`, `characterCount`, `projectileCount`, `nextProjectileId` |
| Character i | `characters[i].id`, `.position.x`, `.position.y`, `.position.z`, `.velocity.x` … `.groundNormal.z`, `.aimYaw`, `.aimPitch`, `.stance`, `.flags`, `.lastSequence`, `.activeSlot`, `.weaponCount` |
| Weapon s of character i | `characters[i].weapons[s].phase`, `.flags`, `.phaseSteps`, `.fireWait`, `.shotCounter`, `.magazine`, `.reserve`, `.heat`, `.stepsSinceShot`, `.recoilPitch`, `.recoilYaw` |
| Projectile j | `projectiles[j].id`, `.ownerId`, `.class`, `.launchStep`, `.origin.x` … `.launchVelocity.z`, `.position.x` … `.position.z` |
| Events | `events.shots.count`, `events.shots[k].direction.x`, `events.hits[k].t`, `events.expiries[k].reason`, and so on |

An index is the element's position in its list in serialisation order (ascending ID), not its ID.
For example `characters[3].velocity.y` is the vertical velocity of the fourth character by ID.

## Canonicalisation and the finite check

At stage 6 of the world step, the serialiser walks the next state's fields in the layout's order
and, for every `f32`:

1. Reads its bits, `b = BitConverter.SingleToUInt32Bits(v)`.
2. If `(b & 0x7F800000) == 0x7F800000` (NaN or ±infinity), the step stops with
   `StepFault { Code = NonFinite, Path = <that field> }`. Nothing is committed: the previous state,
   its step number and its hash stay as they were (FR-017). The state buffer's contents are not a
   state after a fault, and the report's hash is 0.
3. If `b == 0x80000000` (negative zero), it writes +0 to the buffer and also writes +0 back into the
   next state's field, so the committed state holds +0 (FR-016).
4. Otherwise it writes `b`.

Integers need no canonicalisation. The first non-finite field in layout order is the one reported.

Why it is done this way:

- **The integer bit test cannot be optimised away.** [measured] `x + 0f` also turns −0 into +0 in
  all three builds, and `x == 0f ? 0f : x` works too, but `x - 0f` keeps −0. Ordinary code produces
  −0 from `0·−1`, `−(0)`, `(−1)·0` and the underflow `−1e-45f / 2` ([research
  R13](../research.md#r13-canonical-state-and-the-step-hash)).
- **At commit, not only in the hash.** Equal hashes then mean equal states. Otherwise two builds
  could hold +0 and −0 under one hash, and `Atan2(±0, −1)` would make them diverge several steps
  later.
- **NaN is never hashed.** Its payload bits are not specified to be the same across WebAssembly
  engines and x64 [documentation], so a hash over a NaN would be undefined. Validation makes NaN
  impossible; this check makes a bug visible at the step that caused it.

## State hash

```text
hash = XxHash64.HashToUInt64(stateBytes, seed: 0)
```

- **Over exactly the state bytes**: the `stateLength` bytes from the header through the last event.
  The step record's own fields are not hashed.
- **Not chained.** A step's hash depends only on that step's state, unlike the simulator's
  `StateHasher`. Reconciliation (sub-spec 6) compares a predicted and an authoritative state at one
  step, independent of history, and the agreement suite compares every step anyway. The state's
  `step` field is hashed, so equal states at different steps have different hashes.
- **Printed** as 16 lowercase hex digits (`x16`) in every report and JSON file. In binary it is a
  `u64 LE`.
- **Cost** [measured, sandbox]: 129 ns per 512 bytes on x64, 2.28 µs interpreted WebAssembly and
  0.76 µs AOT WebAssembly; 0 bytes allocated over 10,000 hashes ([research
  R13](../research.md#r13-canonical-state-and-the-step-hash)).
- **Use.** Every step of `SimWorld.Step` hashes. Whether production ticks hash is sub-spec 6's
  decision.

`StateHash.Compute(ReadOnlySpan<byte>)` (`src/MmoEngine.SimCore/Hashing/`) is the one function every
caller uses.

## Worked example

A test vector, pinned by a test in `tests/MmoEngine.SimCore.Tests/Formats/`. Step 0, one character
(ID 1) standing at (0.5, 0, −0.5), grounded, facing +X (yaw 16384), with the placeholder rifle (30
in the magazine, 210 in reserve) and launcher (1 and 5), no projectiles, `nextProjectileId` 1, no
events. The position is inside the `tiny-world` collision fixture's playable area, so this state is
also the initial state of the stream fixtures
([recorded-stream-format.md](recorded-stream-format.md#golden-fixtures)).
Length 136 = 24 + 1·(52 + 30·2):

```text
0000  00 00 00 00 01 00 00 00 01 00 00 00 01 00 00 00   step 0; 1 character; 0 projectiles; nextProjectileId 1; id 1
0010  00 00 00 3f 00 00 00 00 00 00 00 bf 00 00 00 00   position (0.5, 0, -0.5); velocity.x 0
0020  00 00 00 00 00 00 00 00 00 00 00 00 00 00 80 3f   velocity.y, .z 0; groundNormal.x 0, .y 1
0030  00 00 00 00 00 40 00 00 00 01 00 00 00 00 00 02   groundNormal.z 0; yaw 16384; pitch 0; Standing; Grounded; lastSequence 0; slot 0; 2 weapons
0040  00 00 00 00 00 00 00 00 00 00 00 00 1e 00 d2 00   weapons[0] at 0x40: Ready, fireWait 0, shotCounter 0, magazine 30, reserve 210
0050  00 00 00 00 ff ff 00 00 00 00 00 00 00 00 00 00   heat 0; stepsSinceShot 65535; recoil 0, 0; weapons[1] at 0x5e
0060  00 00 00 00 00 00 00 00 00 00 01 00 05 00 00 00   weapons[1]: Ready, magazine 1, reserve 5, heat 0
0070  00 00 ff ff 00 00 00 00 00 00 00 00 00 00 00 00   stepsSinceShot 65535; recoil 0, 0; events at 0x7c
0080  00 00 00 00 00 00 00 00                           all six event counts 0

hash 35038da2f2d9459c
```

## Loading a state

`SimWorld.Reset(ReadOnlySpan<byte> state, ulong matchSalt)` loads a canonical state, and the stream
decoder reads a stream's initial state with the same reader. A refused state throws
`CoreFormatException`, and the world keeps what it had. The error's format is `state`, with offsets
from the state's first byte; inside a stream it is `stream`, with file offsets and the field
prefixed by `initialState.` ([recorded-stream-format.md](recorded-stream-format.md#initial-state)).

Checks that need only the bytes, in layout order (the stream decoder runs these):

| Check | Code | Field |
| --- | --- | --- |
| at least 12 bytes | `Truncated` | `step` |
| `characterCount` ≤ 1024, `projectileCount` ≤ 16384, every `weaponCount` ≤ 8 | `Oversized` | that count |
| a record or count runs past the end | `Truncated` | the field being read |
| bytes left after `events.movementOutcomes` | `Oversized` | `length` |
| character IDs strictly ascending; projectile IDs ≥ 1 and strictly ascending | `Inconsistent` | `.id` |
| `nextProjectileId` ≥ 1 and greater than every projectile ID | `Inconsistent` | `nextProjectileId` |
| every float finite | `NonFinite` | that field |
| no float is negative zero (a canonical state never holds one, so decode then encode is the identity) | `OutOfRange` | that field |
| `position` and `origin` coordinates within ±8192 m | `OutOfRange` | that field |
| `aimPitch` in range; `stance` ≤ 1; reserved flag bits 0; `activeSlot` < `weaponCount`; `phase` ≤ 4; `launchStep` ≤ `step` | `OutOfRange` | that field |
| all six event counts 0 | `Inconsistent` | `events.<list>.count` |

Checks that need the compiled configuration and the collision world (`Reset` and the stream runner
run these):

| Check | Code | Field |
| --- | --- | --- |
| `characterCount` ≤ `world.maxCharacters`; `projectileCount` ≤ `world.maxProjectiles` | `Oversized` | that count |
| `weaponCount` = `input.weaponSlots` | `Inconsistent` | `characters[i].weaponCount` |
| `position` inside the playable area (the heightfield's X–Z extent) | `OutOfRange` | `characters[i].position.x` or `.z` |
| grounded: `groundNormal.y` ≥ `CosSlopeLimit`; airborne: `groundNormal` is exactly (0, 1, 0) | `OutOfRange`; `Inconsistent` | `characters[i].groundNormal.y`; `.groundNormal.x` |
| every rule of the weapon record: `magazine` ≤ `magazineSize`, 0 ≤ `fireWait` < `ShotCost`, the recoil bounds, and a holstered weapon is Ready or Overheated | `OutOfRange`; `Inconsistent` (holstered phase) | that field |
| `class` below the number of projectile classes | `OutOfRange` | `projectiles[j].class` |

The message shape is the formats' `"<format> <field> at offset <n>: <detail>"`, for example:

```text
state characters[0].position.x at offset 16: 9000 is outside [-8192, 8192]
stream initialState.events.expiries.count at offset 890: a loaded state has no events, found 1
```

## Step record

What `BatchStepper` and `StreamRunner` return for each step (FR-025, US2 scenario 6):

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `step` | equals the state's `step` |
| 4 | 4 | u32 LE | `stateLength` | ≤ `MaxStateLength` |
| 8 | `stateLength` | bytes | `state` | the canonical state, with that step's events |
| 8 + `stateLength` | 8 | u64 LE | `hash` | `StateHash.Compute(state)` |

Total: 16 + `stateLength` bytes. With the worked example's state that is 152 bytes.

- **Packed sequences.** A `.records` file (`<work>/<build>/<stream>.records` in the agreement suite)
  and the output of `StreamRunner.Run` are step records back to back, in step order, with no header
  and no padding. A reader walks them by `stateLength`.
- **Fixed-capacity buffers.** `BatchStepper`'s report buffer holds the records packed in the same
  way, after a small header, and is sized from `MaxStepRecordLength` times its command capacity
  ([wasm-api.md](wasm-api.md#report-buffer-layouts)).
- **Faults.** A step that faults writes no record. `BatchStepper` stops at the first fault: the
  steps before it keep their records, and a stop entry gives the fault's code and path.
  `StreamRunner` throws `StepFaultException` instead ([wasm-api.md](wasm-api.md)).

`StepRecord` (`src/MmoEngine.SimCore/Batch/`) writes and reads this layout.

## Divergence report

The agreement suite and `simcore streams verify` compare step records and report the first divergent
step of each stream, field by field (FR-026, US2 scenario 4, SC-002). `DivergenceReport` in
`MmoEngine.SimCore.Server` builds it; [simcore-cli.md](simcore-cli.md) has the commands and exit
codes.

**Comparison.**

- The reference is the server build's record for the same stream and step. Each other build is
  compared with it.
- Step by step, a step diverges when the two records' hashes differ, their state bytes differ, or
  one side has no record for it (a build that stopped early). Only the first divergent step of each
  (stream, build) pair is reported: later steps follow from it.
- Every build's hashes, the server's included, are also compared with the stream's committed
  expected hashes. A stream stores hashes, not states, so that report names the step and both hashes
  but no fields.

**Field diff.** Both states are decoded through the layout table, with no value checks, so a broken
state from a buggy build can still be read.

- Paths are walked in layout order. Every path whose bits differ is listed.
- List elements are matched by index. When two counts differ, the count is listed, and each element
  that exists on one side only is listed with `absent` on the other.
- A state that cannot be read to its end is listed up to that point, followed by
  `unreadable from offset <n>`.

**Values.** Every value is shown as its bits and as a decimal:

- bits as `0x%08X`: the field's own bits, zero-extended to 32 bits (an `i16` of −1 is `0x0000FFFF`);
- decimals: unsigned or signed by the field's type, and floats with round-trip (`"R"`) formatting in
  the invariant culture, so the decimal parses back to the same bits;
- hashes as `x16`.

**Example.** The values are illustrative; the hashes are the real hashes of the two states
described.

```text
DIVERGED stream wall-slide-45, build chromium-worker, first divergent step 37
  hash  server d15d8dbcd3e66f5a  chromium-worker fdebaca498955582
  2 fields differ
  characters[0].position.x  server 0x40A3D70A 5.12   chromium-worker 0x40A3D70B 5.1200004
  characters[0].position.z  server 0xBFAF5C29 -1.37  chromium-worker 0xBFAF5C2A -1.3700001
```

The remaining examples show the shape only; their values and hashes are placeholders. A build that
fired one shot fewer at its first divergent step lists the count and the missing shot:

```text
  events.shots.count               server 0x00000001 1     node-interp 0x00000000 0
  events.shots[0].characterId      server 0x00000001 1     node-interp absent
```

Against the committed expected hashes:

```text
DIVERGED stream jump-land, build server, first divergent step 12, against the expected hashes
  hash  expected 5c01e7a4f0d29b13  server 9a4b2c6d1e0f3857
```

**Not comparable.** A stream in another format version or rules revision is not a divergence
(US2 scenario 5). The report names both values and lists no steps:

```text
NOT COMPARABLE stream jump-land: unsupported version 2; this reader supports version 1
NOT COMPARABLE stream jump-land: rules revision 1 in the stream; this core implements rules revision 2
```

## API

```csharp
// src/MmoEngine.SimCore/Formats/StateSerializer.cs (namespace MmoEngine.SimCore.Formats)
public static class StateSerializer
{
    public const int HeaderLength = 12;
    public const int CharacterFixedLength = 52;
    public const int WeaponLength = 30;
    public const int ProjectileLength = 49;
    public const int ShotLength = 25;
    public const int HitLength = 26;
    public const int SmallEventLength = 5;

    public static int MaxLength(int maxCharacters, int maxProjectiles, int weaponSlots, int maxShotsPerStep);
}

// src/MmoEngine.SimCore/Hashing/StateHash.cs (namespace MmoEngine.SimCore.Hashing)
public static class StateHash
{
    public static ulong Compute(ReadOnlySpan<byte> canonicalState); // XxHash64, seed 0
}
```

`SimWorld.Step` returns a `StepReport` whose `State` is the canonical state in the world's buffer
and whose `Hash` is its hash, valid until the next step ([data-model.md](../data-model.md)).
`SimWorld.Reset` loads a state as above.

Tests that pin this contract, in `tests/MmoEngine.SimCore.Tests/`:

- the worked example's bytes and hash;
- a Theory over every float field: −0 written there is committed and hashed as +0, and the hash
  equals that of the same state with +0 (edge case);
- a Theory over every float field: NaN, +∞ or −∞ there refuses the step with that field's path, and
  the previous state and hash are kept (FR-017);
- `MaxLength` against a state filled to every maximum;
- a property (CsCheck, fixed seed): reading a written state and writing it again gives the same
  bytes;
- every load refusal in the two tables above, by code and field;
- the divergence report for a one-ulp change at step k of a stream names step k and the field, with
  both bit patterns (US2 scenario 4, SC-002).
