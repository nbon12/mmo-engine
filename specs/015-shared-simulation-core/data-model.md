# Data Model: Shared Simulation Core

Entities from [spec.md](spec.md), with their fields, units, rules, owners and state changes. Byte
layouts are in the contracts, not here:

- the canonical state, its step events, the state hash and the step record:
  [contracts/state-serialization.md](contracts/state-serialization.md);
- the input command: [contracts/input-command-format.md](contracts/input-command-format.md);
- collision data (`MMCD`): [contracts/collision-data-format.md](contracts/collision-data-format.md);
- recorded streams (`MMRS`) and their config block:
  [contracts/recorded-stream-format.md](contracts/recorded-stream-format.md);
- every configuration field, its range, its binary field ID and the cross-field rules:
  [contracts/config-rules.md](contracts/config-rules.md).

Type names are the core's (`src/MmoEngine.SimCore/`, namespaces under `MmoEngine.SimCore`). Every value
here that comes from configuration is a placeholder. This feature sets no tuning value; game design
replaces them, and replacing them changes no code.

## Conventions

- **Frame.** Right-handed, Y up, metres, `float` (binary32). The horizontal plane is X–Z. Coordinates
  are centred on the continent. Every coordinate in collision data and in state has |v| ≤ 8192 m; the
  recommended content extent is |x|, |z| ≤ 4096 m.
- **Angles.** Yaw is a `u16` binary angle (65,536 per turn). Yaw 0 faces +Z, and positive yaw turns
  from +Z toward +X (counter-clockwise seen from above). `forward = (sinψ, 0, cosψ)` and
  `right = forward × up = (−cosψ, 0, sinψ)`. Pitch is an `i16` binary angle in [−16384, 16384]
  (±90°), positive looking up. Every sine and cosine comes from `SimMath` (FR-013), and aim always
  goes through `SimMath.SinCosBinaryAngle`.
- **Time.** Time is a step number (`u32`). The step length is `1 / stepRateHz` from validated
  configuration, 20 to 60 Hz (FR-015). The core never reads a clock or a measured frame time
  (FR-001). Open decision 3 (tick rate) stays open: 30 Hz is only the default.
- **Order.** Characters are kept in ascending character ID and projectiles in ascending projectile ID.
  Every loop over them runs in that order. The core has no hash-ordered collection (FR-012).
- **Floats in state.** Negative zero is rewritten to positive zero at commit by the integer bit test
  (`bits == 0x80000000u ? 0u : bits`), so equal hashes mean equal states. NaN and ±infinity never
  reach a committed state: the step refuses instead (FR-016, FR-017).
- **Loops.** Every loop has a fixed bound taken from configuration or from a core constant (FR-005).
  The bounds are listed with each step below.
- **Rules revision.** `SimCoreVersion.RulesRevision` (`u32`, starts at 1) names the step rules. A
  change to any rule or algorithmic constant below bumps it, and the committed streams are then
  regenerated with a stated reason (FR-028).

## Ownership

The core owns no state. It has no static mutable field. Each step function takes state in and writes
state out, and every instance below belongs to the caller that created it.

| Entity | Type | Created and written by | Shared |
| --- | --- | --- | --- |
| Raw configuration | `SimConfig` | The caller: `SimConfigJson` on the server, the config block decoder in the browser and in `StreamRunner` | No |
| Compiled configuration | `CompiledConfig` | `CompiledConfig.Create` | Yes, immutable |
| Collision world | `CollisionWorld` | `CollisionFile` decoder, once per file | Yes, immutable, read by any thread without locks |
| World | `SimWorld` | Its caller: the owning region on the server (sub-spec 5), the Worker on the client (`BatchStepper`), `StreamRunner` in the agreement suite | No: one writer |
| Character state, weapon state, projectiles | inside `SimWorld` | `SimWorld.Step` only | No |
| Input command | `InputCommand` | Decoded by the caller (`InputCodec.TryDecode`), validated by the world | No |
| Hitboxes | `Hitbox` | `HitboxBuilder`, into the world's scratch, each step | No |
| Step report | `StepReport` | `SimWorld.Step`, over world-owned buffers, valid until the next step | No |
| Recorded stream | `.mmrs` file | `simcore streams record` only; read-only everywhere else | Committed file |

On the server the region that owns an entity is its single writer and calls the core inside its tick.
On the client the Worker owns the predicted copy. The core takes no locks (constitution Principle II).

## Configuration

### SimConfig

The raw, unvalidated configuration as JSON or a stream's config block decodes it. Sections:
`stepRateHz`, `world`, `movement`, `weapons[]`, `projectileClasses[]`, `hitboxes`, `input`. Every
field, its kind (Int, Float or Enum), unit, range, binary field ID and placeholder is one row of the
rule table in [contracts/config-rules.md](contracts/config-rules.md). `ConfigGuard.Check(SimConfig)`
returns every invalid field as `ConfigError(Path, Message)`, keyed by the camelCase JSON path
(FR-029).

### CompiledConfig

What every step reads. `CompiledConfig.Create(SimConfig)` runs the guard, throws
`ConfigValidationException(errors)` when the list is not empty, and otherwise performs every
conversion once. The step functions accept only a `CompiledConfig`, so no step can run on an
unvalidated value. The conversions (durations to steps, degrees to binary angle units, heat scaled by
the step rate, the spread's versine bound, `halfG`, `CosSlopeLimit`, the hitbox bounding spheres) are
listed in [contracts/config-rules.md](contracts/config-rules.md#compile-time-conversions). Both builds
compute them with the same float operations from the same bits, so they produce the same compiled
values.

`CompiledConfig` also keeps a copy of its validated `SimConfig`, so a recorder can write the stream's
config block, and the maximum canonical state length for the configured maxima, which sizes the
world's buffers.

### Movement configuration

The movement section plus `stepRateHz`: walk, run and crouch speeds; ground acceleration, ground
deceleration and air acceleration; gravity, jump speed and maximum fall speed; capsule radius and the
standing and crouched heights; step height and snap distance; slope limit and the ledge support
normal; contact skin and contact tolerance; the slide and sub-move iteration limits. Ranges and
relations are in the rule table.

Algorithmic constants of the movement step. They are not configuration; changing one bumps the rules
revision.

| Constant | Value | Used by |
| --- | --- | --- |
| Time-of-impact iteration cap | 8 per triangle sweep | sweep |
| Push-out iteration cap | 4 per sub-move | push-out |
| Sweep tolerance | 0.1 · `contactSkin` | sweep, push-out threshold |
| Minimum approach | 1e-4 · \|d\| | sweep |
| Local origin snap | 64 m lattice in X and Z | closest-point maths |
| Diagonal factor | `0.70710677f` (literal) | wish direction |
| Parallel threshold | sin²θ < 1e-10 | segment–segment closest points |
| Degenerate triangle | edge < 1 mm, or \|AB×AC\|² ≤ 1e-8 · maxEdge⁴, or \|AB×AC\|² ≤ 4e-12 | mesh load |

## Collision world

Built only from one collision data file in the `MMCD` format (FR-004,
[contracts/collision-data-format.md](contracts/collision-data-format.md)). The file holds primary
data only. Normals, bounding boxes and the grid are derived at load by core code, in file order, so
the same file gives the same world in every build. A world is immutable once built.

### Heightfield

| Field | Type | Rule |
| --- | --- | --- |
| OriginX, OriginZ | `float`, m | finite, \|v\| ≤ 8192 |
| CellSize | `float`, m | [0.25, 64] |
| Columns, Rows | `u32` | samples along X and along Z, each [2, 4097] |
| Heights | `float[Columns · Rows]` | row-major, j outer; finite, \|h\| ≤ 8192 |

- Sample (i, j) is at `(OriginX + (float)i·CellSize, Heights[j·Columns + i], OriginZ + (float)j·CellSize)`.
  This one expression is used everywhere, so vertices shared by neighbouring cells are bit-identical
  and the surface is watertight.
- Each cell splits along the diagonal v00 → v11. Triangle k = 0 is (v00, v11, v10), used where u ≥ w;
  k = 1 is (v00, v01, v11). Triangle ID = 2·(j·(Columns − 1) + i) + k. There are
  2·(Columns − 1)·(Rows − 1) heightfield triangles.
- Triangles are generated on demand and never stored. Their normals have +Y and they cannot be
  degenerate.
- The playable area is the heightfield's X–Z extent, from OriginX to OriginX + (Columns − 1)·CellSize
  and from OriginZ to OriginZ + (Rows − 1)·CellSize. Its four sides are vertical boundary planes with
  IDs −4 to −1, which the capsule sweep tests like walls.
- `HeightAt(x, z)` follows the same diagonal rule. It is used for spawn placement and tests.

### Mesh set

| Field | Type | Rule |
| --- | --- | --- |
| Meshes | per mesh: MeshId `u32`, vertices `float` x, y, z, triangle index triples `u32` | MeshId strictly ascending; every index below the mesh's vertex count; every coordinate finite with \|v\| ≤ 8192 |
| Triangles (derived) | per triangle: A, B, C, unit normal N, axis-aligned bounding box | computed at load |
| DegenerateTriangleCount | `int` | triangles dropped at load (edge case: they load and are ignored) |

Mesh triangle ID = heightfield triangle count + the triangle's global index in file order. A dropped
degenerate triangle keeps its ID unused, so IDs never shift. Triangles are double-sided, so
zero-thickness walls work.

### Uniform grid

The spatial index over the mesh triangles.

| Field | Type | Rule |
| --- | --- | --- |
| GridCellSize | `float`, m | [1, 64], stored in the file |
| CellsX, CellsZ | `int` | the heightfield extent divided by GridCellSize, rounded up; each in [1, 4096] |
| CellStart | `int[CellsX · CellsZ + 1]` | offsets into CellTriangles |
| CellTriangles | `int[]` | mesh triangle IDs of each cell, ascending |

A triangle is registered in every cell its bounding box overlaps, by one clamped `floor` cell
function. Cells are filled by visiting triangles in ID order, so each list is ascending. At most 64
triangles per cell, and at most 2²⁴ registrations in total; more is refused at load with `OutOfRange`
at field `grid.cells[cx,cz]` or `grid.registrations`
([contracts/collision-data-format.md](contracts/collision-data-format.md#limits)).

### CollisionWorld

| Field | Type | Rule |
| --- | --- | --- |
| Checksum | `u64` | the file's XxHash64 trailer. It identifies the world; a recorded stream names its world by it (FR-021). |
| Heightfield, Meshes, Grid | as above | immutable |
| MaxQueryCandidates(box width) | `int` | the largest candidate count a capsule query box of that width can return, from the cell sizes and the per-cell limit. The world calls it with the widest box its configured capsule can sweep (a sub-move is at most r long) and sizes its candidate buffer from the result. |

Queries, all allocation-free, writing into a caller-supplied span:

- **Box query** (capsule sweeps, push-out, headroom): heightfield cells overlapping the box first
  (skipping a cell whose four corner heights miss the box's Y range), then grid cells. A triangle
  found in several grid cells is kept only in the cell that holds
  (max(tri.minX, box.minX), max(tri.minZ, box.minZ)) (the reference-point method). The output is
  ascending and has no duplicates. Overflowing the span is impossible under a valid configuration; if
  it happens anyway the step refuses (`StepFault.QueryCapacityExceeded`) and never truncates.
- **Segment query** (projectiles): visits the cells the segment crosses and tests their triangles,
  keeping the earliest hit with ties to the lower triangle ID. Its work is bounded by the segment's
  length divided by the cell size.

## Input command

One step's input from one player (FR-018). The 17-byte wire form (format ID `0x49`, version 1) is in
[contracts/input-command-format.md](contracts/input-command-format.md). Decoding (`InputCodec`) and
validation (`InputValidator`) are separate: a decode failure is a `FormatError`, a validation failure
is an `InputRejection`.

| Field | Type | Rule |
| --- | --- | --- |
| Sequence | `u32` | must be greater than the character's LastSequence (starts at 0, so the first is ≥ 1); gaps allowed |
| ClientStep | `u32` | carried, not checked by the core (sub-spec 6 uses it) |
| Buttons | `u16` | bits 0 to 8 known, bits 9 to 15 must be 0 |
| AimYaw | `u16` | binary angle; every value is valid |
| AimPitch | `i16` | binary angle in [−16384, 16384] |
| WeaponSlot | `u8` | below `input.weaponSlots` |

| Bit | Button | Effect |
| --- | --- | --- |
| 0 | Forward | wish direction +forward |
| 1 | Back | wish direction −forward |
| 2 | Left | wish direction −right |
| 3 | Right | wish direction +right |
| 4 | Jump | level-triggered: jumps whenever held while grounded |
| 5 | Crouch | hold to crouch |
| 6 | Walk | hold to walk; run is the default; crouch overrides both |
| 7 | Fire | hold the trigger |
| 8 | Reload | start a reload; level-triggered, and ignored while the magazine is full or the reserve is empty |

**Validation** (FR-019) checks in this order and reports the first failure:
`SequenceNotIncreasing`, `UnknownButtons`, `PitchOutOfRange`, `UnknownWeaponSlot`. A rejected command
does not touch the state: the step records `{character ID, reason}` and steps that character with the
neutral input.

**Neutral input.** A character with no command this step, or whose command was rejected, is stepped
with no buttons held, its aim and active slot unchanged and LastSequence unchanged. Gravity, slides,
cooling and recoil recovery still run. A world step takes at most one command per character, in
ascending character order; the stream decoder and the batch stepper guarantee this.

## Character state

The movement step's input and output (FR-001). The world holds characters in a struct array in
ascending ID and their weapons in a flat array indexed by character index × `input.weaponSlots` +
slot.

| Field | Type | Unit | Rule |
| --- | --- | --- | --- |
| Id | `u32` | | unique in the world, ascending; also the spread's player ID (FR-008) |
| Position | `Vec3` (state paths `position.x`, `.y`, `.z`) | m | the feet, which is the capsule bottom; finite; \|v\| ≤ 8192; inside the playable area |
| Velocity | `Vec3` | m/s | finite |
| GroundNormal | `Vec3` | unit vector | the support normal while grounded (y ≥ `CosSlopeLimit`); (0, 1, 0) while airborne |
| AimYaw | `u16` | binary angle | from the last applied input |
| AimPitch | `i16` | binary angle | [−16384, 16384] |
| Stance | `u8` | | 0 Standing, 1 Crouched |
| Flags | `u8` | | bit 0 Grounded, bit 1 ClampedToBounds (in the last step); bits 2 to 7 are 0 |
| LastSequence | `u32` | | sequence of the last applied input; 0 before the first |
| ActiveSlot | `u8` | | below WeaponCount |
| WeaponCount | `u8` | | equals `input.weaponSlots` |
| Weapons | `WeaponState[WeaponCount]` | | in slot order |

The spec's "contact state" is GroundNormal plus the two flags. The capsule follows from the stance:
radius r = `movement.capsuleRadius`, height h = `standHeight` or `crouchHeight`, and its axis runs from
Position + (0, r, 0) to Position + (0, h − r, 0).

The state guard (run when a state is loaded with `SimWorld.Reset`) refuses a state that breaks any
rule in this table, a duplicate or unsorted ID, more characters than `world.maxCharacters`, or a
non-empty event list. Refusals are `FormatError`s with the field path
([contracts/state-serialization.md](contracts/state-serialization.md)).

### Stance

```text
              Crouch held (always succeeds: the feet stay, the top lowers)
   Standing ─────────────────────────────────────────────────────────────► Crouched
      ▲                                                                       │
      └──────────────── Crouch released and the headroom test is clear ───────┘
                        (released but blocked: stays Crouched, outcome HeadroomBlocked)
```

The headroom test checks only the part the standing capsule adds, the axis segment from
Position + (0, crouchHeight − r, 0) to Position + (0, standHeight − r, 0): the character stands only
if no triangle is within r + `contactSkin`/2 of it (US1 scenario 6).

### Grounded and airborne

```text
               Jump held (sub-step 5), or the ground probe finds no support (sub-step 8)
   Grounded ───────────────────────────────────────────────────────────────────────► Airborne
      ▲                                                                                 │
      └───────────────────── the ground probe finds support (sub-step 8) ───────────────┘
```

A grounded character whose probe finds support stays grounded: it snaps to the support and
GroundNormal becomes the support normal.

**Support** means a contact normal with y ≥ `CosSlopeLimit`, or an edge or vertex contact on a face
whose normal has y ≥ `CosSlopeLimit`, with the contact normal's y ≥ `ledgeSupportMinNormalY`. The
support normal is the contact normal when it is walkable, otherwise the face normal.

### Movement step

`MovementStep` is a pure function of the character's state, one validated input (or the neutral
input), the compiled configuration and the collision world (FR-001). Its only other argument is the
world's candidate buffer, whose contents never carry from one call to the next. Sub-steps, in order:

| # | Sub-step | Reads | Writes |
| --- | --- | --- | --- |
| 1 | Apply input | the input | AimYaw, AimPitch, LastSequence; sinψ and cosψ from `SinCosBinaryAngle(AimYaw)` |
| 2 | Stance | Crouch, Stance, Position, collision world | Stance; outcome HeadroomBlocked |
| 3 | Wish direction | Forward, Back, Left, Right, Walk, Stance, sinψ, cosψ | target horizontal velocity: (F − B)·forward + (R − L)·right, times `0.70710677f` on a diagonal, times the speed (crouched: `crouchSpeed`; Walk held: `walkSpeed`; otherwise `runSpeed`) |
| 4 | Horizontal velocity | Velocity x and z, Grounded, target | Velocity x and z: move toward the target by at most rate·dt, taking the target exactly when within it. Rate: grounded with a wish `groundAccel`, grounded without `groundDecel`, airborne with a wish `airAccel`, airborne without 0. |
| 5 | Vertical velocity | Grounded, Jump, Velocity.y | Velocity.y, Grounded. Grounded: Velocity.y = 0, and Jump held sets it to `jumpSpeed` and makes the character airborne. Airborne, including on the jump step: Velocity.y −= gravity·dt, clamped at −`maxFallSpeed`. |
| 6 | Ground projection | Grounded, GroundNormal, Velocity | if grounded, Velocity.y = −(n.x·v.x + n.z·v.z)/n.y, which keeps horizontal speed on slopes; n.y ≥ `CosSlopeLimit` > 0, so the division is safe |
| 7 | Sub-moves | Position, Velocity, collision world | Position, Velocity; outcomes Stuck, SlideCapReached, ClampedToBounds |
| 8 | Ground probe | Grounded at step start, whether the character jumped, Velocity.y | Position (snap), Grounded, GroundNormal, Velocity.y |

Sub-step 7 splits the step into n = clamp(⌈\|v·dt\|/r⌉, 1, `maxSubMoves`) sub-moves of dt/n each. Relation
R09 in the rule table keeps n inside the cap, so no sub-move is longer than r and no wall thinner than
the movement per step is passed through (edge cases). Each sub-move runs, in order:

1. **Push-out.** If any triangle is closer to the axis than r − 0.5·(sweep tolerance), the deepest is
   pushed out to r + `contactSkin`, ties to the lowest ID. At most 4 iterations; still overlapping
   reports Stuck.
2. **Slide.** At most `maxSlideIterations` sweeps. Each sweeps d = v·(dt/n)·τ, moves to the time of
   impact and keeps the rest of the motion (τ *= 1 − t). On a walkable hit while grounded the velocity
   is re-projected onto the support plane; on a steep hit, or in walls-only mode, the normal is
   flattened to horizontal and the velocity clipped against it. Two planes clip along their crease, a
   third stops the velocity, and a velocity that would reverse against the intended motion becomes 0,
   so corners never oscillate. Running out of iterations with motion left reports SlideCapReached; a
   boundary plane hit reports ClampedToBounds.
3. **Step-up**, only when grounded and the slide hit a wall: find the starting ground contact with a
   2·`contactSkin` down-sweep, sweep up by `stepHeight` (rejected if it rises no more than the skin),
   slide horizontally in walls-only mode, sweep down. Accepted only if it lands on support, the landing
   contact is at most `stepHeight` above the starting ground contact, and it moves further along the
   horizontal velocity than the plain slide (US1 scenario 3).

After the sub-moves a safety clamp keeps Position inside the playable area and reports
ClampedToBounds if it had to move it.

Sub-step 8 sweeps down `snapDistance` when the character was grounded at the start of the step and
did not jump, otherwise 2·`contactSkin` when Velocity.y ≤ 0. Support found: move to it, set Grounded,
GroundNormal and Velocity.y = 0. Otherwise the character is airborne and GroundNormal = (0, 1, 0).

Each capsule sweep uses conservative advancement against each candidate triangle (at most 8
iterations, never past the true time of impact), keeps the minimum time of impact, and breaks ties to
the lower ID, so boundary planes win ties. The capsule never ends a step overlapping terrain or a
mesh by more than `contactTolerance` (FR-003).

Loop bounds per step (FR-005): `maxSubMoves` sub-moves; per sub-move 4 push-outs, `maxSlideIterations`
slide sweeps and one step-up of 4 sweeps plus `maxSlideIterations` slide sweeps; one probe; 8
iterations per triangle sweep; the candidate buffer's capacity per query.

## Weapon configuration and state

The compiled weapon configuration (per slot) is in
[contracts/config-rules.md](contracts/config-rules.md#compile-time-conversions): `Rpm` (fire-rate units
per step, equal to `roundsPerMinute`), `ShotCost` (60·`stepRateHz` units per shot), `MaxShotsPerStep`,
`FireMode` (Auto or Semi), magazine and reserve sizes, `ReloadSteps`, `EquipSteps`,
`HeatCooldownDelaySteps`, `RecoilRecoveryDelaySteps`, heat values scaled by `stepRateHz`, `VersMax`,
recoil in binary angle units, `MuzzleSpeed` and the projectile class index. No duration or rate in
the weapon step is a float, so fire timing is exact (FR-007).

### WeaponState

One per slot per character. The weapon step writes it; it holds no floats.

| Field | Type | Rule |
| --- | --- | --- |
| Phase | `u8` `WeaponPhase` | Ready, Firing, Reloading, Switching or Overheated |
| Flags | `u8` | bit 0 TriggerLatched (a semi-automatic weapon has fired and waits for release); bits 1 to 7 are 0 |
| PhaseSteps | `u16` | steps left in Switching or Reloading; while Overheated, the equip time still owed (0 if none); otherwise 0 |
| FireWait | `i32` | fire-rate units still to wait; 0 ≤ FireWait < ShotCost after every step |
| ShotCounter | `u32` | shots this weapon has fired; wraps at 2³²; an input to the spread hash |
| Magazine | `u16` | ≤ `magazineSize` |
| Reserve | `u16` | rounds outside the magazine |
| Heat | `u32` | heat × `stepRateHz`; below OverheatThresholdScaled + HeatPerShotScaled |
| StepsSinceShot | `u16` | steps since this weapon last fired, saturating at 65535 |
| RecoilPitch | `i32` | binary angle units, [0, RecoilMaxPitchUnits] |
| RecoilYaw | `i32` | binary angle units, [−RecoilMaxYawUnits, RecoilMaxYawUnits] |

A new character's weapon starts Ready, with flags 0, PhaseSteps 0, FireWait 0, ShotCounter 0, a full
magazine, `reserveRounds` in reserve, Heat 0, StepsSinceShot 65535 and no recoil.

### Weapon phases

| From | Condition (sub-step) | To | Effect |
| --- | --- | --- | --- |
| Ready | Fire held and no fire block (W5) | Firing | the fire loop runs |
| Firing | Fire not held, or a fire block (W5) | Ready | |
| Ready, Firing | Reload held, Magazine < `magazineSize`, Reserve > 0 (W3) | Reloading | PhaseSteps = ReloadSteps |
| Reloading | PhaseSteps reaches 0 (W2), or ReloadSteps is 0 at entry (W3) | Ready | moves min(`magazineSize` − Magazine, Reserve) rounds from Reserve |
| Ready (holstered) | the slot changes to this weapon (W1) | Switching | PhaseSteps = EquipSteps; EquipSteps 0 means Ready at once |
| Overheated (holstered) | the slot changes to this weapon (W1) | Overheated | PhaseSteps = EquipSteps |
| Firing, Reloading, Switching | the slot changes away from this weapon (W1) | Ready | a reload is cancelled; PhaseSteps = 0 |
| Overheated | the slot changes away from this weapon (W1) | Overheated | PhaseSteps = 0 |
| Switching | PhaseSteps reaches 0 (W2) | Ready | |
| Ready, Firing | a shot brings Heat ≥ OverheatThresholdScaled (W5) | Overheated | the fire loop ends |
| Overheated | Heat ≤ HeatRecoveryScaled after cooling (W4) | Switching if PhaseSteps > 0, else Ready | |

A weapon that enters Switching or Reloading at step n with a duration of D steps is Ready at step
n + D and can fire in that step. Entry in W1 skips that step's count-down and entry in W3 comes after
it, so both count from the next step. A holstered weapon is always Ready or Overheated.

### Weapon step

`WeaponStep` runs once per character, in ascending character ID, after movement (FR-006). It reads
the post-movement pose, the validated input, the compiled configuration, the match salt, the
projectile count and NextProjectileId. Sub-steps, in order:

| # | Sub-step | Applies to | Rule |
| --- | --- | --- | --- |
| W0 | Prepare | every weapon | StepsSinceShot += 1, saturating; if Fire is not held, clear TriggerLatched |
| W1 | Slot change | the old and new active weapon | when WeaponSlot ≠ ActiveSlot: apply the slot-change rows above; ActiveSlot = WeaponSlot |
| W2 | Count down | the active weapon | in Switching, Reloading or Overheated with PhaseSteps > 0: PhaseSteps −= 1; Switching or Reloading reaching 0 completes. PhaseSteps set by W1 in this step is not counted down until the next step. |
| W3 | Reload start | the active weapon | Reload held in Ready or Firing, Magazine < `magazineSize`, Reserve > 0. There is no automatic reload. |
| W4 | Cooling and recoil recovery | every weapon | if StepsSinceShot ≥ HeatCooldownDelaySteps: Heat −= HeatCooldownPerStep, not below 0, then Overheated exits as above. If StepsSinceShot ≥ RecoilRecoveryDelaySteps: RecoilPitch and RecoilYaw move toward 0 by RecoilRecoveryPerStep, not past it. |
| W5 | Fire loop | the active weapon | with Fire held, the fire block and fire loop below; with Fire not held, Firing becomes Ready |
| W6 | Fire wait | every weapon | FireWait = max(FireWait − Rpm, 0) |

**Fire block.** With Fire held, W5 first takes the first reason that applies: Switching, Reloading,
Overheated (from the phase), Empty (Magazine = 0), AwaitingRelease (Semi with TriggerLatched). With a
reason, no shot fires, the phase is Ready if it was Firing, and the step reports
`{character ID, FireBlock}` (US4 scenario 6). A block is reported only while Fire is held.

**Fire loop.** With no block:

```text
shots = 0
while FireWait < Rpm and shots < MaxShotsPerStep and Magazine > 0
      and Phase != Overheated and not (Semi and TriggerLatched):
    if the world holds maxProjectiles projectiles or NextProjectileId = 0xFFFFFFFF:
        if shots = 0: report FireBlock.ProjectileLimit
        stop
    fire one shot; shots += 1
Phase = Overheated if it overheated, else Firing (Ready if ProjectileLimit was reported)
```

The relation `roundsPerMinute ≤ maxShotsPerStep · 60 · stepRateHz` makes the cap a loop bound that a
valid configuration never reaches, so W6's clamp at 0 never discards owed time. A released trigger
counts FireWait down to 0 and never banks shots, so re-pressing the trigger never fires faster than
the rate. Over any period the shot count never drifts from the configured rate by more than one
(FR-007, US4 scenario 1).

**One shot**, in this order:

1. Aim with recoil: yaw = (u16)(AimYaw + RecoilYaw), pitch = clamp(AimPitch + RecoilPitch, −16384,
   16384). The first shot of a burst uses the recoil from before its own kick.
2. Spread hash (FR-008): h = Mix(Mix(matchSalt ^ characterId) ^ (((u64)slot << 32) | ShotCounter)),
   where Mix is the SplitMix64 finaliser (`z ^= z >> 30; z *= 0xBF58476D1CE4E5B9; z ^= z >> 27;
   z *= 0x94D049BB133111EB; z ^= z >> 31`).
3. Direction: u1 = (float)(uint)(h >> 40)·2⁻²⁴ (exact, and through `uint` because `ulong → float` is
   banned), φ = (u16)h, s = u1·VersMax, cosθ = 1 − s,
   sinθ = Sqrt(s·(2 − s)). With sy, cy from `SinCosBinaryAngle(yaw)` and sp, cp from
   `SinCosBinaryAngle((u16)pitch)`: f = (cp·sy, sp, cp·cy), r = (−cy, 0, sy),
   u = (−sp·sy, cp, −sp·cy), and per component dir = ((cosθ·f) + ((sinθ·cosφ)·r)) + ((sinθ·sinφ)·u).
   Directions are uniform in solid angle inside the cone (US4 scenario 2).
4. Spawn a projectile: Id = NextProjectileId, then NextProjectileId += 1; Origin = Position +
   (0, eye height of the post-movement stance, 0); LaunchVelocity = dir·MuzzleSpeed; LaunchStep = the
   step being computed; Position = Origin. The projectile does not inherit the shooter's velocity.
5. Report the shot `{character ID, slot, ShotCounter, projectile ID, dir}`.
6. Magazine −= 1; ShotCounter += 1; Heat += HeatPerShotScaled; Heat ≥ OverheatThresholdScaled sets
   Overheated (US4 scenario 4).
7. Recoil kick (US4 scenario 5): RecoilPitch = min(RecoilPitch + RecoilKickPitchUnits,
   RecoilMaxPitchUnits). RecoilYaw += RecoilKickYawUnits when bit 16 of h is 0, −RecoilKickYawUnits
   when it is 1, clamped to ±RecoilMaxYawUnits.
8. StepsSinceShot = 0; FireWait += ShotCost; a Semi weapon sets TriggerLatched.

## Projectile

### Projectile class

Per class index (0 small arms, 1 heavy in the placeholders): `HalfG` = (−0.5·`movement.gravity`)·
`gravityFactor`, `MaxRangeSq` = `maxRangeM`², `LifetimeSteps` = ⌈`lifetimeMs`·Hz/1000⌉. Phase 1 has no
drag and a projectile radius of 0 (spec Assumptions).

### Projectile

| Field | Type | Unit | Rule |
| --- | --- | --- | --- |
| Id | `u32` | | unique, ascending, taken from NextProjectileId; never 0 |
| OwnerId | `u32` | | the shooter's character ID; the owner's hitboxes are never tested |
| Class | `u8` | | below the number of projectile classes |
| LaunchStep | `u32` | step | L, the step it was fired in; ≤ the world's step |
| Origin | `Vec3` | m | the shooter's eye at launch; \|v\| ≤ 8192 |
| LaunchVelocity | `Vec3` | m/s | dir·MuzzleSpeed |
| Position | `Vec3` | m | p(n − L + 1) at the end of step n; derived, but stored and hashed so a one-ulp error shows at its own step (SC-002) |

The ballistic position after k steps is closed form (FR-009, US5 scenario 1), with
t = (float)k / (float)stepRateHz:

```text
x = x0 + (vx·t)
y = y0 + ((vy·t) + ((HalfG·t)·t))
z = z0 + (vz·t)
```

Both ends of every swept segment come from this one function of k, so consecutive segments join
exactly, and client and server can evaluate any step directly.

### Lifecycle

```text
  spawned (weapon step W5, step L, Position = Origin)
     │
     ▼
  flying ── each step n: sweep p(k) → p(k+1), k = n − L ──┬──► hit                (removed; hit event)
     ▲                                                     ├──► expired, Range      (removed; expiry event)
     └──────────── no hit and no limit passed ─────────────┼──► expired, Lifetime   (removed; expiry event)
                   (Position = p(k+1))                     └──► expired, OutOfBounds (removed; expiry event)
```

A projectile is swept in the step it is fired, so a projectile that starts inside a hitbox reports
that hit in its first step (edge case). It leaves the state in the step it hits or expires.

### Projectile step

`ProjectileStep` runs after the hitboxes are built, once per projectile in ascending ID, the step's
new projectiles included. It takes the hitboxes as an input span, so sub-spec 6 can pass hitboxes
built from rewound poses. For a projectile with k = n − L:

1. a = p(k), b = p(k + 1).
2. World candidates: the collision world's segment query against terrain and meshes.
3. Character candidates: every character except the owner. The segment is first clipped to the
   character's bounding sphere, then tested against its three capsules (head, torso, legs). A start
   inside a capsule hits at t = 0. A segment that crosses a whole hitbox within one step still hits,
   because the full segment is swept (US5 scenario 6).
4. The earliest hit wins by the key (t, world before character, target ID, hitbox index), compared as
   exact floats (FR-010). A mesh in front of a character is hit first (US5 scenario 3).
5. A hit's point is a + ((b − a)·t) per component. If |point − Origin|² ≤ MaxRangeSq the projectile
   reports the hit `{projectile ID, kind, target ID, hitbox, point, t}` (US5 scenario 2) and is
   removed. Otherwise it expires with reason Range and is removed.
6. With no hit, the first of these that applies expires it: |b − Origin|² > MaxRangeSq (Range),
   k + 1 ≥ LifetimeSteps (Lifetime), any coordinate of b beyond ±8192 m (OutOfBounds) (US5
   scenario 4). Otherwise Position = b.

For a world hit the target ID is the triangle ID and the hitbox is None. Projectile flight and
hitboxes serve both server-simulated shots and client-reported hits; open decision 5 (hit detection)
stays open, and this feature adds no hit claim or claim validation.

## Hitbox layout and hitboxes

**HitboxLayout** (in `CompiledConfig`, one per stance; FR-011):

| Field | Type | Rule |
| --- | --- | --- |
| Capsules[3] | head, torso, legs: A (fwd, right, up), B (fwd, right, up), Radius | relative to the feet, rotated by yaw only; A.up ≤ B.up ≤ stance height + 0.5 m |
| BoundCenterUp | `float`, m | height of the bounding sphere's centre above the feet, on the vertical axis |
| BoundRadius | `float`, m | encloses all three capsules, plus a 0.01 m margin |
| EyeHeight | `float`, m | the projectile origin above the feet; below the stance height |

Because the bounding sphere's centre is on the vertical axis through the feet, it does not depend on
yaw.

**Hitbox** (built each step into world scratch by `HitboxBuilder`, from the post-movement Position,
AimYaw and Stance): part (Head, Torso, Legs), world endpoints A and B, radius; per character also the
bounding sphere centre (Position + (0, BoundCenterUp, 0)) and radius. With sy, cy from
`SinCosBinaryAngle(AimYaw)`, a local point (lf, lr, lu) becomes

```text
wx = fx + ((sy·lf) − (cy·lr))
wy = fy + lu
wz = fz + ((cy·lf) + (sy·lr))
```

where (fx, fy, fz) is Position. The same pose gives the same hitboxes to the bit in every build
(US5 scenario 5). Hitboxes are derived, not part of the hashed state.

## World (SimWorld)

One simulated world: one caller's characters and projectiles over one collision world and one
compiled configuration.

| Field | Type | Rule |
| --- | --- | --- |
| Config | `CompiledConfig` | fixed for the world's life |
| Collision | `CollisionWorld` | fixed for the world's life |
| MatchSalt | `u64` | set with the state at Reset; an input to every shot's spread |
| Step | `u32` | step number of the committed state |
| Characters | `CharacterState[maxCharacters]` and a count | ascending ID |
| Weapons | `WeaponState[maxCharacters · weaponSlots]` | by character index, then slot |
| Projectiles | `Projectile[maxProjectiles]` and a count | ascending ID |
| NextProjectileId | `u32` | ≥ 1 and greater than every live projectile ID |

The match salt is not in the canonical state. It is fixed per match and travels beside the state: in
a recorded stream's header, and in the call that resets a world.

**Scratch the world owns.** All of it is allocated when the world is created, sized from the
configured maxima and the collision world, so a step allocates nothing (FR-032, SC-005).

| Buffer | Size | Holds |
| --- | --- | --- |
| Next state | one copy of the characters, weapons and projectiles | the state the step computes; swapped in at commit |
| Inputs | one entry per character | the accepted input or the neutral input, and the rejection reason |
| Candidates | `int[MaxQueryCandidates(widest box)]` | collision query output |
| Hitboxes | maxCharacters × 3 capsules and maxCharacters bounding spheres | this step's hitboxes |
| Events | shots: maxCharacters × the largest `maxShotsPerStep`; hits and expiries: maxProjectiles; input rejections, fire blocks and movement outcomes: maxCharacters | this step's events |
| State buffer | `byte[]` of the maximum canonical state length | the canonical serialisation that is hashed and reported |

### World step order

`SimWorld.Step(commands)` computes step n = Step + 1:

| # | Stage | Reads | Writes |
| --- | --- | --- | --- |
| 1 | Validate inputs | commands, each character's LastSequence, `input.weaponSlots` | per character: the accepted input or the neutral input; input rejection events |
| 2 | Movement, ascending character ID | current characters, inputs, movement config, collision world | next Position, Velocity, GroundNormal, AimYaw, AimPitch, Stance, Flags, LastSequence; movement outcome events |
| 3 | Weapons, ascending character ID | next characters (post-movement pose and stance), current weapons, inputs, weapon config, MatchSalt, NextProjectileId, projectile count | next weapons and ActiveSlot; new projectiles appended in ID order; NextProjectileId; shot and fire block events |
| 4 | Hitboxes | next characters' Position, AimYaw and Stance; hitbox layouts | hitbox scratch |
| 5 | Projectiles, ascending projectile ID | current and new projectiles, class config, collision world, hitbox scratch | next projectiles (positions, removals); hit and expiry events |
| 6 | Canonicalise, check, serialise, hash | the next state and its events | −0 rewritten to +0 in place; the first non-finite float stops the step with `StepFault.NonFinite` naming it; the canonical bytes in the state buffer; XxHash64 (seed 0, not chained) of those bytes |
| 7 | Commit | | Step = n; the next state becomes the current state |

A fault at any stage commits nothing: the previous state, its step number and its hash stay as they
were (FR-017).

Characters join and leave only between steps. In this feature that happens through `Reset`; the calls
that add and remove characters belong to the continent server (sub-spec 5).

## Step report and step fault

### StepReport

What `SimWorld.Step` returns (spec Key Entity "Step report"). It is a `ref struct` over the world's
buffers, so returning it allocates nothing, and it stays valid until the next call on the same world.

| Field | Type | Rule |
| --- | --- | --- |
| Step | `u32` | the new step number; the old one on a fault |
| Fault | `StepFault` | Code None when the step committed |
| Hash | `u64` | XxHash64 (seed 0, one-shot) of State; 0 on a fault. Printed as 16 lowercase hex digits in reports and fixtures. |
| State | `ReadOnlySpan<byte>` | the canonical state with this step's events |
| Events | `StepEvents` | typed read-only views of the six event lists |

`StepEvents`, each list in the order the state serialises it:

| List | Element | Order |
| --- | --- | --- |
| Shots | character ID, slot, ShotCounter, projectile ID, direction | ascending character ID, then firing order |
| Hits | projectile ID, kind, target ID, hitbox, point, t | ascending projectile ID |
| Expiries | projectile ID, reason | ascending projectile ID |
| Input rejections | character ID, reason | ascending character ID |
| Fire blocks | character ID, reason | ascending character ID |
| Movement outcomes | character ID, flags | ascending character ID; only characters with non-zero flags |

The events are part of the hashed state, so a build that fires one more shot, or reports a different
hitbox, diverges at that step.

**Step record.** What `BatchStepper` and `StreamRunner` return for each step: step number, state
length, the canonical state bytes and the hash
([contracts/state-serialization.md](contracts/state-serialization.md)). Buffers have a fixed capacity
of the maximum record length for the configured maxima.

### StepFault

| Field | Type | Rule |
| --- | --- | --- |
| Code | `u8` `StepFaultCode` | None, NonFinite or QueryCapacityExceeded |
| Path | layout field number and element index | the field, as the state layout table names it; formatted on demand, for example `characters[3].velocity.y` or `projectiles[0].position.x`, where the index is the element's position in ID order |

Recording a fault allocates nothing; only formatting the path does.

## Recorded stream

A committed file that drives the agreement suite (FR-022,
[contracts/recorded-stream-format.md](contracts/recorded-stream-format.md)).

| Part | Rule |
| --- | --- |
| Format version | 1; another version is refused naming both versions (US2 scenario 5, US6 scenario 2) |
| Rules revision | must equal `SimCoreVersion.RulesRevision`; a mismatch is NotComparable, naming both values, never a divergence |
| Step rate | equals the config block's `stepRateHz` |
| Character count | [1, 64]; equals the initial state's |
| Step count | [1, 2²⁰] |
| Seed | the seed its commands were generated from (a generated stream or a seeded scenario such as `fuzz-all-buttons`); 0 for a scenario stream with no seed |
| Match salt | given to the world with the initial state |
| Collision checksum | names the collision world; when it is not supplied the stream is refused before step 0 (US6 scenario 5) |
| Config block | every field of the rule table once, by field ID; decoded to `SimConfig`, then guarded and compiled |
| Initial state | the canonical state with zero events; loaded with Reset |
| Steps | per step: its number (initial step + i + 1), its commands (character index ascending, one input command each), the expected hash |

**Relationships.** A stream names one collision world (by checksum) and carries one configuration and
one initial state. `StreamRunner` builds a world from them, runs every step and returns the step
records. The agreement suite compares each build's records with the server build's and with the
expected hashes: Match, Diverged (first divergent step and every differing field, by path, as bits
and decimals), NotComparable (another format version or rules revision) or Refused (any other
refusal before step 0)
([contracts/recorded-stream-format.md](contracts/recorded-stream-format.md#outcomes)).

**Lifecycle.** Expected hashes are produced by the server build and change only through
`simcore streams record --reason "<why>"`, which prints each stream's old and new hash and appends
the reason to `simcore/streams/REGENERATIONS.md` (FR-028). Tests open stream files read-only.

## Batch stepper (browser)

`BatchStepper` (`src/MmoEngine.SimCore/Batch/`) is the browser's entry point (FR-025). It owns one
`SimWorld`, a command buffer (capacity × 17 bytes, created once) and a report buffer that
`LoadConfig` sizes for the configured maxima: at least capacity × the maximum step record length
([contracts/wasm-api.md](contracts/wasm-api.md#report-buffer-layouts)). `LoadCollision`,
`LoadConfig` and `Reset` load the world; `StepQueued(count)` steps one world step per queued command,
in order, applying each command to the state's first character (the one the Worker predicts) and the
neutral input to every other character, and writes one step record per step. It stops at the first
command that fails to decode or the first fault: the steps before it keep their records, and a stop
entry gives the code and the format error or fault path. Queued commands give the same records as the
same commands stepped one call at a time (US2 scenario 6).

`LoadResult`, returned by the load calls:

| Value | Name | Meaning |
| --- | --- | --- |
| 0 | Ok | loaded |
| 1 | FormatRefused | the bytes failed their decoder; the report buffer holds the format error |
| 2 | ConfigInvalid | the config block decoded but failed the guard; the report buffer holds every `ConfigError` ([contracts/config-rules.md](contracts/config-rules.md#browser-guard)) |
| 3 | NotReady | Reset before both the collision world and the configuration loaded |

## Reason and outcome codes

All are `u8` unless stated. A value never changes meaning. A new value goes at the end; one that can
appear in the hashed state (an input rejection, fire block, expiry reason, hit kind, hitbox part or
movement outcome) also bumps the rules revision.

**InputRejection** (`Inputs/`)

| Value | Name | When |
| --- | --- | --- |
| 0 | None | accepted |
| 1 | SequenceNotIncreasing | Sequence ≤ LastSequence |
| 2 | UnknownButtons | a bit from 9 to 15 is set |
| 3 | PitchOutOfRange | AimPitch outside [−16384, 16384] |
| 4 | UnknownWeaponSlot | WeaponSlot ≥ `input.weaponSlots` |

**WeaponPhase** (`Weapons/`): 0 Ready, 1 Firing, 2 Reloading, 3 Switching, 4 Overheated.

**FireMode** (configuration): 0 Auto, 1 Semi.

**FireBlock** (`Weapons/`), reported only while Fire is held and no shot fired

| Value | Name | When |
| --- | --- | --- |
| 0 | None | not blocked |
| 1 | Switching | the active weapon is Switching |
| 2 | Reloading | the active weapon is Reloading |
| 3 | Empty | Magazine is 0 |
| 4 | Overheated | the active weapon is Overheated |
| 5 | AwaitingRelease | a Semi weapon has fired and the trigger has not been released |
| 6 | ProjectileLimit | the world already holds `world.maxProjectiles` projectiles, or projectile IDs are used up |

**ExpiryReason** (`Projectiles/`): 1 Range, 2 Lifetime, 3 OutOfBounds. 0 is never written.

**HitKind** (`Projectiles/`): 0 Terrain, 1 Mesh, 2 Character.

**HitboxPart** (`Hitboxes/`): 0 Head, 1 Torso, 2 Legs, 255 None (a terrain or mesh hit).

**Stance**: 0 Standing, 1 Crouched. **Character flags**: bit 0 Grounded, bit 1 ClampedToBounds.
**Weapon flags**: bit 0 TriggerLatched.

**MovementOutcome** (`Movement/`, flags)

| Bit value | Name | When |
| --- | --- | --- |
| 0 | None | |
| 1 | HeadroomBlocked | Crouch released but there is no room to stand |
| 2 | Stuck | still overlapping after 4 push-out iterations |
| 4 | SlideCapReached | a slide used `maxSlideIterations` sweeps with motion left |
| 8 | ClampedToBounds | a boundary plane or the safety clamp stopped the character |

**FormatErrorCode** (`Formats/`). The error is `FormatError { Format, Code, Offset, Field, Detail }`,
with the message `"<format> <field> at offset <n>: <detail>"`.

| Value | Name | When |
| --- | --- | --- |
| 0 | None | |
| 1 | UnknownMagic | the format identifier is wrong |
| 2 | UnsupportedVersion | `unsupported version {v}; this reader supports version {s}` |
| 3 | Truncated | fewer bytes than declared or needed |
| 4 | Oversized | more bytes than declared, or a count or length above its limit |
| 5 | Inconsistent | fields that disagree: lengths, ordering, or an unknown, missing or repeated config field ID |
| 6 | OutOfRange | a value outside its documented range |
| 7 | NonFinite | a NaN or infinite float |
| 8 | ChecksumMismatch | the XxHash64 trailer does not match |
| 9 | CollisionDataMissing | a stream names a collision checksum that was not supplied |
| 10 | RulesRevisionMismatch | a stream's rules revision differs from the core's; reported as NotComparable |

**StepFaultCode** (`World/`)

| Value | Name | When |
| --- | --- | --- |
| 0 | None | the step committed |
| 1 | NonFinite | a float in the next state or its events is NaN or infinite |
| 2 | QueryCapacityExceeded | a collision query had more candidates than its buffer; a valid configuration makes this impossible |

**LoadResult** (`Batch/`): 0 Ok, 1 FormatRefused, 2 ConfigInvalid, 3 NotReady (above).
