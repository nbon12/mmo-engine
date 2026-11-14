# Contract: Simulation configuration and its rule table

Movement, weapon, projectile and hitbox parameters are configuration, validated before any step runs
(FR-029). The core holds **one rule table**: `ConfigFields` (one row per field) and `ConfigRelations`
(one row per cross-field rule), in `src/MmoEngine.SimCore/Config/`. That table is the single source
for:

- `ConfigGuard.Check`, the check every build runs;
- `SimConfigValidator`, the server's FluentValidation adapter, which reports the guard's errors
  unchanged;
- the binary config block in recorded streams, whose field IDs come from the table;
- this document, whose tables are the rows.

Every value in the **Placeholder** columns is a placeholder. This feature sets no tuning value; game
design replaces them, and replacing them changes no code. The step rate is configuration and defaults
to the constitution's 30 Hz starting target; open decision 3 (tick rate) stays open.

## Shape

The JSON form is `simcore/config/default.json` (example at the end). Property names are camelCase,
and error paths are the camelCase JSON path with zero-based array indices, such as
`weapons[0].roundsPerMinute`.

| Path | Holds |
| --- | --- |
| `stepRateHz` | the step rate |
| `world` | `maxCharacters`, `maxProjectiles` |
| `movement` | the character controller fields |
| `weapons[i]` | one weapon per slot; i is the slot |
| `projectileClasses[k]` | one ballistic class per index; 0 small arms and 1 heavy in the placeholders |
| `hitboxes` | `standing[3]` and `crouched[3]` capsules (0 head, 1 torso, 2 legs), `eyeHeightStanding`, `eyeHeightCrouched` |
| `input` | `weaponSlots` |

A hitbox capsule is `{ "a": { "fwd", "right", "up" }, "b": { "fwd", "right", "up" }, "radius" }`, in
metres relative to the feet: `fwd` along the aim heading, `right` to the character's right, `up` along
+Y.

Shape rules are checked first:

| Path | Rule | Message |
| --- | --- | --- |
| `world`, `movement`, `hitboxes`, `input`, any array element, any capsule's `a` or `b` | present | `is required` |
| `weapons` | 1 to 8 entries | `must have between 1 and 8 entries` |
| `projectileClasses` | 1 to 8 entries | `must have between 1 and 8 entries` |
| `hitboxes.standing`, `hitboxes.crouched` | exactly 3 entries | `must have exactly 3 entries` |

## Field kinds

| Kind | JSON | Binary value (u32) | Checks, in order | Message |
| --- | --- | --- | --- | --- |
| Int | an integer | the bits of an `int` (two's complement) | min ≤ v ≤ max | `must be between {min} and {max}` |
| Float | a number, read straight to binary32 | the `float`'s IEEE 754 bits | finite; then min ≤ v ≤ max | `must be finite`; `must be between {min} and {max}` |
| Enum | a member name | the member's value | a defined member | `must be Auto or Semi` |

`{min}` and `{max}` are printed in invariant culture with round-trip formatting, as in the tables
below (`must be between 0.1 and 20`). Messages carry no unit, matching the repo's
`must be between 1 and 64`.

The server reads JSON floats with `Utf8JsonReader.GetSingle`, which rounds the decimal once to the
nearest binary32 [documentation], so `9.81` gets the same bits as the C# literal `9.81f`. Those bits
travel in the stream's config block, so no build other than the server parses decimal text.

## Field IDs

`fieldId = section << 12 | instance << 8 | field` (u16). The instance is the slot, the class index or
the stance; it is 0 in sections without instances. Field numbers start at `0x01` in each section.

| Section | Name | Instance | Instances |
| --- | --- | --- | --- |
| 0 | general (`stepRateHz`, `world.*`) | 0 | 1 |
| 1 | movement | 0 | 1 |
| 2 | weapon | slot | 0 to 7 |
| 3 | projectile class | class index | 0 to 7 |
| 4 | hitbox | stance: 0 standing, 1 crouched | 2 |
| 5 | input | 0 | 1 |

The config block in a recorded stream lists `u16 count`, then `count` entries of
`{u16 fieldId, u32 valueBits}` in strictly ascending fieldId. Every field of every present instance
appears exactly once. The weapon and projectile class instances present are 0 to N − 1 with no gap.
The block's decoding and its refusals are in
[recorded-stream-format.md](recorded-stream-format.md).

## Rule table

Min and max are inclusive. Where the research states a strict bound (for example "> 0"), the table
uses the smallest value that still makes sense.

### General (section 0)

| fieldId | Path | Kind | Unit | Min | Max | Placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| `0x0001` | `stepRateHz` | Int | Hz | 20 | 60 | 30 |
| `0x0002` | `world.maxCharacters` | Int | characters | 1 | 1024 | 64 |
| `0x0003` | `world.maxProjectiles` | Int | projectiles | 1 | 16384 | 1024 |

### Movement (section 1)

| fieldId | Path | Kind | Unit | Min | Max | Placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| `0x1001` | `movement.walkSpeed` | Float | m/s | 0.1 | 20 | 3.5 |
| `0x1002` | `movement.runSpeed` | Float | m/s | 0.1 | 20 | 7 |
| `0x1003` | `movement.crouchSpeed` | Float | m/s | 0.1 | 20 | 2 |
| `0x1004` | `movement.groundAccel` | Float | m/s² | 0.1 | 1000 | 70 |
| `0x1005` | `movement.groundDecel` | Float | m/s² | 0.1 | 1000 | 50 |
| `0x1006` | `movement.airAccel` | Float | m/s² | 0 | 1000 | 8 |
| `0x1007` | `movement.gravity` | Float | m/s² | 0.1 | 100 | 9.81 |
| `0x1008` | `movement.jumpSpeed` | Float | m/s | 0.1 | 20 | 4.5 |
| `0x1009` | `movement.maxFallSpeed` | Float | m/s | 1 | 100 | 50 |
| `0x100A` | `movement.capsuleRadius` | Float | m | 0.05 | 1 | 0.4 |
| `0x100B` | `movement.standHeight` | Float | m | 0.5 | 3 | 1.8 |
| `0x100C` | `movement.crouchHeight` | Float | m | 0.5 | 3 | 1.2 |
| `0x100D` | `movement.stepHeight` | Float | m | 0.01 | 1 | 0.35 |
| `0x100E` | `movement.snapDistance` | Float | m | 0 | 2 | 0.35 |
| `0x100F` | `movement.slopeLimitDegrees` | Float | ° | 5 | 60 | 45 |
| `0x1010` | `movement.ledgeSupportMinNormalY` | Float | (cosine) | 0 | 1 | 0.1736 |
| `0x1011` | `movement.contactSkin` | Float | m | 0.001 | 0.05 | 0.01 |
| `0x1012` | `movement.contactTolerance` | Float | m | 0.0001 | 0.025 | 0.002 |
| `0x1013` | `movement.maxSlideIterations` | Int | iterations | 1 | 8 | 5 |
| `0x1014` | `movement.maxSubMoves` | Int | sub-moves | 1 | 16 | 8 |

`movement.gravity` is also the gravity projectiles fall under, scaled by their class's
`gravityFactor`.

### Weapons (section 2, instance i = slot)

Placeholders are given as slot 0 / slot 1: an automatic rifle at 600 rounds per minute and a
semi-automatic launcher at 30. One value means both slots.

| fieldId | Path | Kind | Unit | Min | Max | Placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| `0x2i01` | `weapons[i].roundsPerMinute` | Int | rounds/min | 1 | 6000 | 600 / 30 |
| `0x2i02` | `weapons[i].maxShotsPerStep` | Int | shots | 1 | 8 | 1 |
| `0x2i03` | `weapons[i].fireMode` | Enum | Auto = 0, Semi = 1 | | | Auto / Semi |
| `0x2i04` | `weapons[i].magazineSize` | Int | rounds | 1 | 1000 | 30 / 1 |
| `0x2i05` | `weapons[i].reserveRounds` | Int | rounds | 0 | 10000 | 210 / 5 |
| `0x2i06` | `weapons[i].reloadMs` | Int | ms | 0 | 60000 | 2000 / 3500 |
| `0x2i07` | `weapons[i].equipMs` | Int | ms | 0 | 10000 | 500 / 750 |
| `0x2i08` | `weapons[i].heatPerShot` | Int | heat | 0 | 1000000 | 0 |
| `0x2i09` | `weapons[i].overheatThreshold` | Int | heat | 1 | 10000000 | 100000 |
| `0x2i0A` | `weapons[i].heatRecoveryLevel` | Int | heat | 0 | 10000000 | 40000 |
| `0x2i0B` | `weapons[i].heatCooldownPerSecond` | Int | heat/s | 0 | 10000000 | 50000 |
| `0x2i0C` | `weapons[i].heatCooldownDelayMs` | Int | ms | 0 | 10000 | 250 |
| `0x2i0D` | `weapons[i].spreadHalfAngleDeg` | Float | ° | 0 | 45 | 1.5 / 0.5 |
| `0x2i0E` | `weapons[i].recoilKickPitchDeg` | Float | ° | 0 | 10 | 0.25 |
| `0x2i0F` | `weapons[i].recoilKickYawDeg` | Float | ° | 0 | 10 | 0.1 |
| `0x2i10` | `weapons[i].recoilMaxPitchDeg` | Float | ° | 0 | 45 | 6 |
| `0x2i11` | `weapons[i].recoilMaxYawDeg` | Float | ° | 0 | 45 | 3 |
| `0x2i12` | `weapons[i].recoilRecoveryDegPerSecond` | Float | °/s | 0 | 360 | 10 |
| `0x2i13` | `weapons[i].recoilRecoveryDelayMs` | Int | ms | 0 | 10000 | 100 |
| `0x2i14` | `weapons[i].muzzleSpeed` | Float | m/s | 1 | 2000 | 600 / 120 |
| `0x2i15` | `weapons[i].projectileClass` | Int | class index | 0 | 7 | 0 / 1 |

Heat is a dimensionless counter. Neither placeholder weapon heats; the `heat-overheat` stream uses a
weapon with `heatPerShot` 1000, and streams carry their whole configuration.

### Projectile classes (section 3, instance k = class index)

| fieldId | Path | Kind | Unit | Min | Max | Placeholder (small arms / heavy) |
| --- | --- | --- | --- | --- | --- | --- |
| `0x3k01` | `projectileClasses[k].gravityFactor` | Float | × gravity | 0 | 10 | 1 / 1 |
| `0x3k02` | `projectileClasses[k].maxRangeM` | Float | m | 1 | 10000 | 600 / 400 |
| `0x3k03` | `projectileClasses[k].lifetimeMs` | Int | ms | 1 | 60000 | 3000 / 8000 |

### Hitboxes (section 4, instance s = stance)

Each capsule c (0 head, 1 torso, 2 legs) of stance s has seven fields, with
`fieldId = 0x4000 | s << 8 | c << 4 | j`:

| j | Path (`{stance}` is `standing` or `crouched`) | Kind | Unit | Min | Max |
| --- | --- | --- | --- | --- | --- |
| 1 | `hitboxes.{stance}[c].a.fwd` | Float | m | −1 | 1 |
| 2 | `hitboxes.{stance}[c].a.right` | Float | m | −1 | 1 |
| 3 | `hitboxes.{stance}[c].a.up` | Float | m | 0 | 3.5 |
| 4 | `hitboxes.{stance}[c].b.fwd` | Float | m | −1 | 1 |
| 5 | `hitboxes.{stance}[c].b.right` | Float | m | −1 | 1 |
| 6 | `hitboxes.{stance}[c].b.up` | Float | m | 0 | 3.5 |
| 7 | `hitboxes.{stance}[c].radius` | Float | m | 0.01 | 1 |

So `hitboxes.standing[0].a.fwd` is `0x4001`, `hitboxes.standing[2].radius` is `0x4027` and
`hitboxes.crouched[1].b.up` is `0x4116`.

| fieldId | Path | Kind | Unit | Min | Max | Placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| `0x4031` | `hitboxes.eyeHeightStanding` | Float | m | 0.1 | 3 | 1.62 |
| `0x4131` | `hitboxes.eyeHeightCrouched` | Float | m | 0.1 | 3 | 1.05 |

Capsule placeholders, as a (fwd, right, up) – b (fwd, right, up), radius, in metres:

| Stance | Head | Torso | Legs |
| --- | --- | --- | --- |
| standing | (0.03, 0, 1.56) – (0.03, 0, 1.70), 0.12 | (0, 0, 1.02) – (0, 0, 1.40), 0.22 | (0, 0, 0.12) – (0, 0, 0.92), 0.17 |
| crouched | (0.15, 0, 1.00) – (0.15, 0, 1.10), 0.12 | (0.05, 0, 0.55) – (0.08, 0, 0.85), 0.23 | (0, 0, 0.12) – (0, 0, 0.45), 0.20 |

### Input (section 5)

| fieldId | Path | Kind | Unit | Min | Max | Placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| `0x5001` | `input.weaponSlots` | Int | slots | 1 | 8 | 2 |

## Relations

Checked only when the shape and every field passed. The formulas are evaluated by the guard in
`float` with this exact parenthesisation, or in `int`, so every build accepts and refuses the same
configurations. In the messages, `[i]`, `[c]` and `{stance}` are replaced by the instance being
reported, and `({n})` by the value named.

`CosSlopeLimit` = `SimMath.Cos((movement.slopeLimitDegrees * MathF.PI) / 180f)`, the same value
`CompiledConfig` stores.
`MaxSpeed` = `(movement.runSpeed / CosSlopeLimit) + SimMath.Max(movement.jumpSpeed, movement.maxFallSpeed)`,
a bound on the character's speed. Horizontal speed never exceeds `runSpeed`. On the ground the
projection adds at most `runSpeed` × tan(slope limit) vertically, so the speed is at most
`runSpeed / CosSlopeLimit`. In the air the vertical speed is at most that projected speed,
`jumpSpeed` or `maxFallSpeed`.

| ID | Path | Holds when | Message |
| --- | --- | --- | --- |
| R01 | `movement.walkSpeed` | `walkSpeed >= crouchSpeed` | `must be at least movement.crouchSpeed` |
| R02 | `movement.runSpeed` | `runSpeed >= walkSpeed` | `must be at least movement.walkSpeed` |
| R03 | `movement.crouchHeight` | `crouchHeight > ((2f * capsuleRadius) + 0.05f)` | `must be greater than twice movement.capsuleRadius plus 0.05` |
| R04 | `movement.crouchHeight` | `crouchHeight < standHeight` | `must be less than movement.standHeight` |
| R05 | `movement.stepHeight` | `stepHeight >= (capsuleRadius * (1f - CosSlopeLimit))` | `must be at least movement.capsuleRadius times (1 - cos movement.slopeLimitDegrees)` |
| R06 | `movement.snapDistance` | `snapDistance <= (2f * stepHeight)` | `must be at most twice movement.stepHeight` |
| R07 | `movement.contactTolerance` | `contactTolerance >= (contactSkin * 0.05f)` and `contactTolerance <= (contactSkin * 0.5f)` | `must be between 0.05 and 0.5 times movement.contactSkin` |
| R08 | `movement.ledgeSupportMinNormalY` | `ledgeSupportMinNormalY <= CosSlopeLimit` | `must be at most the cosine of movement.slopeLimitDegrees` |
| R09 | `movement.maxSubMoves` | `(MaxSpeed * 0.05f) <= ((float)maxSubMoves * capsuleRadius)` | `must be large enough that no sub-move of a 20 Hz step is longer than movement.capsuleRadius` |
| R10 | `weapons` | entry count `== input.weaponSlots` | `must have exactly input.weaponSlots ({n}) entries` |
| R11 | `weapons[i].roundsPerMinute` | `roundsPerMinute <= maxShotsPerStep * 60 * stepRateHz` | `must be at most weapons[i].maxShotsPerStep times 60 times stepRateHz ({n})` |
| R12 | `weapons[i].heatRecoveryLevel` | `heatRecoveryLevel < overheatThreshold` | `must be less than weapons[i].overheatThreshold` |
| R13 | `weapons[i].heatCooldownPerSecond` | `heatPerShot == 0` or `heatCooldownPerSecond > 0` | `must be greater than 0 when weapons[i].heatPerShot is greater than 0` |
| R14 | `weapons[i].projectileClass` | `projectileClass <` the number of `projectileClasses` | `must be less than the number of projectileClasses ({n})` |
| R15 | `hitboxes.{stance}[c].b.up` | `b.up >= a.up` | `must be at least hitboxes.{stance}[c].a.up` |
| R16 | `hitboxes.standing[c].b.up` | `b.up <= (movement.standHeight + 0.5f)` | `must be at most movement.standHeight plus 0.5` |
| R17 | `hitboxes.crouched[c].b.up` | `b.up <= (movement.crouchHeight + 0.5f)` | `must be at most movement.crouchHeight plus 0.5` |
| R18 | `hitboxes.eyeHeightStanding` | `eyeHeightStanding < movement.standHeight` | `must be less than movement.standHeight` |
| R19 | `hitboxes.eyeHeightCrouched` | `eyeHeightCrouched < movement.crouchHeight` | `must be less than movement.crouchHeight` |

Why each one matters:

- R01, R02: crouch ≤ walk ≤ run.
- R03, R04: the crouched capsule has a straight part and is shorter than the standing one, so the
  headroom test has a segment to test.
- R05: the rounded capsule bottom climbs at most r·(1 − cos limit) on a walkable edge, so a valid
  step height covers it.
- R06: the ground probe snaps down at most two step heights.
- R07: the upper bound is the research's; the lower bound keeps push-out, which acts below
  r − 0.05·`contactSkin`, inside the contact tolerance FR-003 promises.
- R09: a 20 Hz step is the longest supported step (a degraded region runs at 20 Hz), so a movement
  configuration valid at 20 Hz is valid at every supported rate. Sub-moves no longer than r bound the
  collision query and the candidate buffer.
- R11: makes `maxShotsPerStep` a loop bound that never truncates, so fire timing never drifts
  (FR-007). At 30 Hz with one shot per step the limit is 1800 rounds per minute.
- R12, R13: an overheated weapon always reaches its recovery level and fires again.
- R18, R19: the projectile origin is inside the character's own capsule, whose clearance from
  geometry the movement step keeps.

## Check order and the error list

1. Shape. Any shape error ends the check, because the missing parts can't be read.
2. Fields in ascending fieldId, which is also the order of the tables above (instances ascending).
   A float that is not finite reports only `must be finite`.
3. Relations, R01 to R19, each over its instances in ascending order, only when steps 1 and 2 found
   no error.

The order is fixed, so the guard and every adapter return the same list in the same order.

## Compile-time conversions

`CompiledConfig.Create(SimConfig)` runs the guard first, then computes every value the steps read.
Integer arithmetic is exact. Float expressions are evaluated in `float`, with the parenthesisation
shown, from the configuration's bits, so every build gets the same compiled bits. `Hz` is
`stepRateHz`. `ceilSteps(ms)` is `(ms * Hz + 999) / 1000` in `int` (at most 60000 · 60 + 999, far
below 2³¹). `angleUnits(deg)` is `SimMath.RoundToInt((deg * 65536f) / 360f)`, binary angle units
with 65,536 per turn, rounded to nearest with ties to even. `floor(x + 0.5f)` is not used, because
`0.49999997f + 0.5f` rounds up to 1 ([research R9](../research.md#r9-weapons)).

| Compiled value | Formula | Type |
| --- | --- | --- |
| `Dt` | `1f / (float)Hz` | float, s |
| `GroundAccelPerStep`, `GroundDecelPerStep`, `AirAccelPerStep` | `groundAccel * Dt`, `groundDecel * Dt`, `airAccel * Dt` | float, m/s |
| `GravityPerStep` | `gravity * Dt` | float, m/s |
| `CosSlopeLimit` | `SimMath.Cos((slopeLimitDegrees * MathF.PI) / 180f)` | float |
| `AxisTop[stance]` | `standHeight - capsuleRadius`, `crouchHeight - capsuleRadius` | float, m |
| `SweepTarget` | `capsuleRadius + contactSkin` | float, m |
| `SweepTolerance` | `0.1f * contactSkin` | float, m |
| `PushOutThreshold` | `capsuleRadius - (0.5f * SweepTolerance)` | float, m |
| `HeadroomClearance` | `capsuleRadius + (0.5f * contactSkin)` | float, m |
| `Rpm` | `roundsPerMinute` | int, units per step |
| `ShotCost` | `60 * Hz` | int, units per shot |
| `ReloadSteps`, `EquipSteps`, `HeatCooldownDelaySteps`, `RecoilRecoveryDelaySteps` | `ceilSteps(reloadMs)`, `ceilSteps(equipMs)`, `ceilSteps(heatCooldownDelayMs)`, `ceilSteps(recoilRecoveryDelayMs)` | int, steps |
| `HeatPerShotScaled`, `OverheatThresholdScaled`, `HeatRecoveryScaled` | `heatPerShot * Hz`, `overheatThreshold * Hz`, `heatRecoveryLevel * Hz` | uint (at most 6 × 10⁸) |
| `HeatCooldownPerStep` | `heatCooldownPerSecond`, unchanged: heat is stored × Hz, so one step's cooling of `heatCooldownPerSecond / Hz` is exactly `heatCooldownPerSecond` stored units | uint |
| `VersMax` | `s = SimMath.Sin((spreadHalfAngleDeg * MathF.PI) / 360f)`, then `(2f * s) * s`, which is 2·sin²(θmax/2) | float |
| `RecoilKickPitchUnits`, `RecoilKickYawUnits`, `RecoilMaxPitchUnits`, `RecoilMaxYawUnits` | `angleUnits(recoilKickPitchDeg)`, and so on | int |
| `RecoilRecoveryPerStep` | `u = SimMath.RoundToInt(((recoilRecoveryDegPerSecond * 65536f) / 360f) / (float)Hz)`, raised to 1 when the rate is above 0 and u is 0 | int, units per step |
| `HalfG[k]` | `(-0.5f * movement.gravity) * projectileClasses[k].gravityFactor` | float, m/s² |
| `MaxRangeSq[k]` | `maxRangeM * maxRangeM` | float, m² |
| `LifetimeSteps[k]` | `ceilSteps(lifetimeMs)` | int, steps (at least 1) |
| `BoundCenterUp[stance]` | `lo = min over capsules of (a.up - radius)`, `hi = max over capsules of (b.up + radius)`, then `(lo + hi) * 0.5f` | float, m |
| `BoundRadius[stance]` | the largest `SimMath.Sqrt(((e.fwd * e.fwd) + (e.right * e.right)) + ((e.up - BoundCenterUp) * (e.up - BoundCenterUp))) + radius` over every capsule endpoint e, plus `0.01f` | float, m |
| `EyeHeight[stance]` | `eyeHeightStanding`, `eyeHeightCrouched` | float, m |

The bounding sphere is centred on the vertical axis through the feet, so it does not depend on yaw.
Every other field is used as it is.

With the placeholders at 30 Hz: `ShotCost` 1800, rifle `Rpm` 600 (one shot every 3 steps),
`ReloadSteps` 60 and 105, `EquipSteps` 15 and 23, `HeatCooldownDelaySteps` 8,
`RecoilRecoveryDelaySteps` 3, `LifetimeSteps` 90 and 240.

## Error shape

The core's own types, in `MmoEngine.SimCore.Config`. They have the same shape as the simulator's
`ConfigError` and `ConfigValidationException`, but are separate types, because the core references
only the BCL and `System.IO.Hashing`.

```csharp
public sealed record ConfigError(string Path, string Message)
{
    public override string ToString() => $"{Path}: {Message}";
}

public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(IReadOnlyList<ConfigError> errors)
        : base("The configuration is invalid: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<ConfigError> Errors { get; }
}
```

Examples:

```text
stepRateHz: must be between 20 and 60
movement.gravity: must be finite
weapons[0].fireMode: must be Auto or Semi
weapons[1].roundsPerMinute: must be at most weapons[1].maxShotsPerStep times 60 times stepRateHz (1800)
hitboxes.crouched[0].b.up: must be at least hitboxes.crouched[0].a.up
```

## Where the guard runs

### Core

- `ConfigGuard.Check(SimConfig) → IReadOnlyList<ConfigError>`: the shape, field and relation checks
  above. It never throws for a bad value.
- `CompiledConfig.Create(SimConfig)` calls it and throws `ConfigValidationException(errors)` when the
  list is not empty. The step functions accept only a `CompiledConfig`.

### Server

`src/MmoEngine.SimCore.Server/` holds the server-side pieces the continent server (sub-spec 5) will
reuse:

- `SimConfigJson` binds a JSON file to `SimConfig`. JSON problems use the repo's wording, keyed by
  path: `is not valid JSON (…)`, `must be a JSON object`, `has an invalid value or is not a known
  field (…)` (unknown fields are refused) and `is required`.
- `SimConfigValidator : AbstractValidator<SimConfig>` (FluentValidation) applies the guard, so there is
  one rule table:

  ```csharp
  RuleFor(c => c).Custom((config, context) =>
  {
      foreach (var error in ConfigGuard.Check(config))
      {
          context.AddFailure(error.Path, error.Message);
      }
  });
  ```

  `SimConfigValidator.Errors(config)` returns the failures as `ConfigError`s. The list equals
  `ConfigGuard.Check`'s, path for path and message for message.
- The environment name (`DOTNET_ENVIRONMENT`) is checked with the repo's existing pattern.
- `simcore config check <file>` binds and validates a file, prints one `path: message` line per error
  and exits 0 when valid, 1 when invalid and 2 on a usage error.

A server loads its configuration at startup and refuses to start on any error (fail fast).

### Browser guard

The browser build parses no JSON. It receives the binary config block, as a recorded stream carries
it, and `BatchStepper.LoadConfig(ReadOnlySpan<byte> block)` runs:

1. Decode the block. A malformed block returns `LoadResult.FormatRefused` (1).
2. `ConfigGuard.Check` on the decoded `SimConfig`. Any error returns `LoadResult.ConfigInvalid` (2).
3. `CompiledConfig.Create`, then size the world's buffers. Returns `LoadResult.Ok` (0); the world then
   needs `Reset` before it steps.

On a refusal the report buffer holds a `u32` byte length followed by that many bytes of UTF-8 text:
one line per error, `path: message` for a guard error or the format error's message
(`config <field> at offset <n>: <detail>`), separated by `\n`. The report buffer is created with room
for the longest possible list: the table has at most 260 fields and 19 relations, and every message is
short, so the list stays far below the buffer's 64 KiB minimum. The guard is the same code as on the
server, run on the same bits, so a configuration valid on the server is valid in the browser.

### Recorded streams

`StreamRunner` decodes the stream's config block, runs the guard and compiles before step 0. A
configuration that fails the guard refuses the stream before any step runs, with the guard's errors.

## The configuration file

`simcore/config/default.json` holds the placeholders. Scenario streams that need other values, such as
20 Hz and 60 Hz step rates or a heat weapon, derive them from this file in `ScenarioCatalog`
(`src/MmoEngine.SimCore.Server/`) and carry their whole configuration in the stream's config block.

```json
{
  "stepRateHz": 30,
  "world": {
    "maxCharacters": 64,
    "maxProjectiles": 1024
  },
  "movement": {
    "walkSpeed": 3.5,
    "runSpeed": 7.0,
    "crouchSpeed": 2.0,
    "groundAccel": 70,
    "groundDecel": 50,
    "airAccel": 8,
    "gravity": 9.81,
    "jumpSpeed": 4.5,
    "maxFallSpeed": 50,
    "capsuleRadius": 0.4,
    "standHeight": 1.8,
    "crouchHeight": 1.2,
    "stepHeight": 0.35,
    "snapDistance": 0.35,
    "slopeLimitDegrees": 45,
    "ledgeSupportMinNormalY": 0.1736,
    "contactSkin": 0.01,
    "contactTolerance": 0.002,
    "maxSlideIterations": 5,
    "maxSubMoves": 8
  },
  "weapons": [
    {
      "roundsPerMinute": 600,
      "maxShotsPerStep": 1,
      "fireMode": "Auto",
      "magazineSize": 30,
      "reserveRounds": 210,
      "reloadMs": 2000,
      "equipMs": 500,
      "heatPerShot": 0,
      "overheatThreshold": 100000,
      "heatRecoveryLevel": 40000,
      "heatCooldownPerSecond": 50000,
      "heatCooldownDelayMs": 250,
      "spreadHalfAngleDeg": 1.5,
      "recoilKickPitchDeg": 0.25,
      "recoilKickYawDeg": 0.1,
      "recoilMaxPitchDeg": 6,
      "recoilMaxYawDeg": 3,
      "recoilRecoveryDegPerSecond": 10,
      "recoilRecoveryDelayMs": 100,
      "muzzleSpeed": 600,
      "projectileClass": 0
    },
    {
      "roundsPerMinute": 30,
      "maxShotsPerStep": 1,
      "fireMode": "Semi",
      "magazineSize": 1,
      "reserveRounds": 5,
      "reloadMs": 3500,
      "equipMs": 750,
      "heatPerShot": 0,
      "overheatThreshold": 100000,
      "heatRecoveryLevel": 40000,
      "heatCooldownPerSecond": 50000,
      "heatCooldownDelayMs": 250,
      "spreadHalfAngleDeg": 0.5,
      "recoilKickPitchDeg": 0.25,
      "recoilKickYawDeg": 0.1,
      "recoilMaxPitchDeg": 6,
      "recoilMaxYawDeg": 3,
      "recoilRecoveryDegPerSecond": 10,
      "recoilRecoveryDelayMs": 100,
      "muzzleSpeed": 120,
      "projectileClass": 1
    }
  ],
  "projectileClasses": [
    {
      "gravityFactor": 1.0,
      "maxRangeM": 600,
      "lifetimeMs": 3000
    },
    {
      "gravityFactor": 1.0,
      "maxRangeM": 400,
      "lifetimeMs": 8000
    }
  ],
  "hitboxes": {
    "standing": [
      { "a": { "fwd": 0.03, "right": 0, "up": 1.56 }, "b": { "fwd": 0.03, "right": 0, "up": 1.70 }, "radius": 0.12 },
      { "a": { "fwd": 0, "right": 0, "up": 1.02 }, "b": { "fwd": 0, "right": 0, "up": 1.40 }, "radius": 0.22 },
      { "a": { "fwd": 0, "right": 0, "up": 0.12 }, "b": { "fwd": 0, "right": 0, "up": 0.92 }, "radius": 0.17 }
    ],
    "crouched": [
      { "a": { "fwd": 0.15, "right": 0, "up": 1.00 }, "b": { "fwd": 0.15, "right": 0, "up": 1.10 }, "radius": 0.12 },
      { "a": { "fwd": 0.05, "right": 0, "up": 0.55 }, "b": { "fwd": 0.08, "right": 0, "up": 0.85 }, "radius": 0.23 },
      { "a": { "fwd": 0, "right": 0, "up": 0.12 }, "b": { "fwd": 0, "right": 0, "up": 0.45 }, "radius": 0.20 }
    ],
    "eyeHeightStanding": 1.62,
    "eyeHeightCrouched": 1.05
  },
  "input": {
    "weaponSlots": 2
  }
}
```

## Tests that pin this contract

All automated, in `tests/MmoEngine.SimCore.Tests/Config/`; `tasks.md` names each one.

- A Theory over every row: below the minimum, above the maximum, and for floats NaN, +∞ and −∞. Each
  gives exactly one error, with that row's path and message, from `ConfigGuard.Check` and from
  `SimConfigValidator`.
- A Theory over every relation: a violating value gives exactly that relation's error, and the
  boundary value gives none.
- A reflection test: every property of `SimConfig` and its sections has exactly one row, and every
  row's fieldId is unique.
- `simcore/config/default.json` binds and passes, and its compiled values match committed bits at 20,
  30 and 60 Hz.
- The config block encodes from `SimConfig` and decodes back to the same bits; the guard's result is
  the same for the decoded configuration.
- `BatchStepper.LoadConfig` returns `ConfigInvalid` with the same `path: message` lines as the guard,
  and `FormatRefused` for a malformed block.
