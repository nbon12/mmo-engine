# Contract: Vehicle configuration and its rule table

The vehicle model's parameters (FR-005), validated before any step on the server and in the browser
build, as 015's configuration is ([config-rules.md](../../015-shared-simulation-core/contracts/config-rules.md)).
It follows 015's pattern exactly: one rule table in the core (`VehicleConfigFields` and
`VehicleConfigRelations` in `src/MmoEngine.SimCore/Vehicles/Config/`) is the single source for the
guard every build runs (`VehicleConfigGuard.Check`), the server's FluentValidation adapter (015's
`SimConfigValidator`, which now also reports the vehicle guard's errors unchanged), the vehicle config
block in version 2 streams, and this document.

Every **Placeholder** value is a placeholder (spec Assumptions, "The vehicle model"); this feature sets no
tuning value, and replacing one changes no code. The selection's thresholds are not here: they are the
separate selection configuration ([selection-config.md](selection-config.md)).

## Shape

The `vehicles` section of the simulation configuration JSON. It is optional: a configuration without it
is valid and means no vehicles (FR-005), and 015's `simcore/config/default.json` stays as it is.
`simcore/vehicles/config/default.json` is 015's placeholder configuration plus this section.

```json
{
  "vehicles": {
    "maxVehicles": 64,
    "gravity": 9.81,
    "massKg": 2000,
    "chassisHalfExtents": { "x": 1.0, "y": 0.75, "z": 2.25 },
    "chassisOffsetY": 0.25,
    "chassisFriction": 0.5,
    "wheels": [
      { "attach": { "x": -0.85, "y": -0.26, "z": 1.4 } },
      { "attach": { "x": 0.85, "y": -0.26, "z": 1.4 } },
      { "attach": { "x": -0.85, "y": -0.26, "z": -1.4 } },
      { "attach": { "x": 0.85, "y": -0.26, "z": -1.4 } }
    ],
    "wheelRadius": 0.35,
    "suspensionRestLength": 0.35,
    "suspensionTravel": 0.25,
    "suspensionStiffness": 45000,
    "suspensionDamping": 3800,
    "tireGrip": 1.2,
    "tireSolverIterations": 4,
    "driveForce": 8000,
    "topSpeed": 20,
    "driveCutbackFraction": 0.1,
    "reverseDriveForce": 4000,
    "reverseTopSpeed": 5,
    "reverseEngageSpeed": 0.1,
    "brakeDeceleration": 8,
    "maxSteerAngleDeg": 35,
    "steerRateDegPerSecond": 90,
    "substeps": 1,
    "contactTolerance": 0.03
  }
}
```

The block above is a fragment of the whole file; the file also holds 015's sections. Attachment points
are metres relative to the centre of mass in the chassis frame (+X right, +Y up, +Z forward); the wheels
are, in order, front left, front right, rear left and rear right.

Shape rules, checked first, with 015's messages: `vehicles.chassisHalfExtents`, each `wheels[i]` and each
`attach` present (`is required`); `vehicles.wheels` exactly 4 entries (`must have exactly 4 entries`);
every leaf present (the server's binder refuses an omitted leaf with `<path>: is required`, as 015 does
since PR #73).

## Field IDs

`fieldId = section << 12 | instance << 8 | field`, as in 015, in two new sections that 015's table does
not use. 015's sections 0 to 5 and its config block are unchanged; in particular 015's
`config-unknown-field` refusal fixture, which uses field `0x6001`, still refuses.

| Section | Name | Instance | Instances |
| --- | --- | --- | --- |
| 8 | vehicle | 0 | 1 |
| 9 | wheel | the wheel, 0 to 3 | 4 |

## Rule table

Min and max are inclusive. Kinds and messages are 015's (`must be between {min} and {max}`, `must be
finite`).

### Vehicle (section 8)

| fieldId | Path | Kind | Unit | Min | Max | Placeholder |
| --- | --- | --- | --- | --- | --- | --- |
| `0x8001` | `vehicles.maxVehicles` | Int | vehicles | 1 | 256 | 64 |
| `0x8002` | `vehicles.gravity` | Float | m/s² | 0.1 | 50 | 9.81 |
| `0x8003` | `vehicles.massKg` | Float | kg | 1 | 100000 | 2000 |
| `0x8004` | `vehicles.chassisHalfExtents.x` | Float | m | 0.1 | 10 | 1.0 |
| `0x8005` | `vehicles.chassisHalfExtents.y` | Float | m | 0.1 | 10 | 0.75 |
| `0x8006` | `vehicles.chassisHalfExtents.z` | Float | m | 0.1 | 20 | 2.25 |
| `0x8007` | `vehicles.chassisOffsetY` | Float | m | −5 | 5 | 0.25 |
| `0x8008` | `vehicles.chassisFriction` | Float | | 0 | 2 | 0.5 |
| `0x8009` | `vehicles.wheelRadius` | Float | m | 0.05 | 3 | 0.35 |
| `0x800A` | `vehicles.suspensionRestLength` | Float | m | 0.01 | 3 | 0.35 |
| `0x800B` | `vehicles.suspensionTravel` | Float | m | 0.01 | 3 | 0.25 |
| `0x800C` | `vehicles.suspensionStiffness` | Float | N/m | 1 | 10000000 | 45000 |
| `0x800D` | `vehicles.suspensionDamping` | Float | N·s/m | 0 | 1000000 | 3800 |
| `0x800E` | `vehicles.tireGrip` | Float | | 0.05 | 5 | 1.2 |
| `0x800F` | `vehicles.tireSolverIterations` | Int | passes | 1 | 16 | 4 |
| `0x8010` | `vehicles.driveForce` | Float | N | 0 | 10000000 | 8000 |
| `0x8011` | `vehicles.topSpeed` | Float | m/s | 0.5 | 150 | 20 |
| `0x8012` | `vehicles.driveCutbackFraction` | Float | | 0.001 | 1 | 0.1 |
| `0x8013` | `vehicles.reverseDriveForce` | Float | N | 0 | 10000000 | 4000 |
| `0x8014` | `vehicles.reverseTopSpeed` | Float | m/s | 0.1 | 150 | 5 |
| `0x8015` | `vehicles.reverseEngageSpeed` | Float | m/s | 0 | 5 | 0.1 |
| `0x8016` | `vehicles.brakeDeceleration` | Float | m/s² | 0.1 | 100 | 8 |
| `0x8017` | `vehicles.maxSteerAngleDeg` | Float | ° | 0.1 | 80 | 35 |
| `0x8018` | `vehicles.steerRateDegPerSecond` | Float | °/s | 1 | 3600 | 90 |
| `0x8019` | `vehicles.substeps` | Int | substeps | 1 | 8 | 1 |
| `0x801A` | `vehicles.contactTolerance` | Float | m | 0.001 | 0.5 | 0.03 |

### Wheel (section 9, instance i = wheel)

| fieldId | Path | Kind | Unit | Min | Max | Placeholder (i = 0, 1, 2, 3) |
| --- | --- | --- | --- | --- | --- | --- |
| `0x9i01` | `vehicles.wheels[i].attach.x` | Float | m | −20 | 20 | −0.85, 0.85, −0.85, 0.85 |
| `0x9i02` | `vehicles.wheels[i].attach.y` | Float | m | −20 | 20 | −0.26 each |
| `0x9i03` | `vehicles.wheels[i].attach.z` | Float | m | −20 | 20 | 1.4, 1.4, −1.4, −1.4 |

That is 26 + 12 = 38 fields; the block is 2 + 6 · 38 = 230 bytes.

## Relations

Checked after every field passes its own rule, in this order. The error is reported on the first path
listed.

| Rule | Paths | Condition | Message |
| --- | --- | --- | --- |
| VR01 | `vehicles.wheels[1].attach.z`, `vehicles.wheels[3].attach.z` | each axle's two wheels share z | `must equal vehicles.wheels[0].attach.z` (and `[2]` for the rear) |
| VR02 | `vehicles.wheels[2].attach.z` | the front axle's z is greater than the rear's | `must be below vehicles.wheels[0].attach.z` |
| VR03 | `vehicles.wheels[1].attach.x`, `vehicles.wheels[3].attach.x` | each axle's left wheel has the lower x | `must be above vehicles.wheels[0].attach.x` (and `[2]`) |
| VR04 | `vehicles.suspensionTravel` | travel ≤ rest length | `must be at most vehicles.suspensionRestLength` |
| VR05 | `vehicles.reverseTopSpeed` | ≤ `vehicles.topSpeed` | `must be at most vehicles.topSpeed` |
| VR06 | `vehicles.chassisOffsetY` | \|offset\| < `chassisHalfExtents.y` (the centre of mass is inside the box) | `must be inside vehicles.chassisHalfExtents.y` |
| VR07 | `vehicles.suspensionStiffness` | √(4k / m) · (1 / (`stepRateHz` · `substeps`)) < 2, the explicit spring's stability bound per corner | `with stepRateHz {r} and substeps {K} the suspension's ω·dt is {x}, which must be below 2` |

VR07 reads `stepRateHz` from 015's general section, so the guard takes both blocks.

## Vehicle config block

In a version 2 stream ([vehicle-formats.md](vehicle-formats.md#recorded-stream-version-2)) and in the
browser's `LoadVehicleConfig` export: `u16 count`, then `count` entries `{u16 fieldId, u32 valueBits}` in
strictly ascending field ID, every field exactly once. As in 015, the block decoder checks structure only,
and the guard checks values when the stream is opened or the block is loaded. Inside a stream, format
errors have format `stream`, file offsets and the field prefixed by `vehicleConfig.`; standalone they have
format `vehicleconfig` and block offsets.

## Compile-time conversions

`CompiledVehicleConfig.Create` (data-model.md) converts degrees with `SimMath` and 015's angle rules,
computes the substep length as `1f / stepRateHz / substeps`, the diagonal inertia from the box and its
offset, and `SpawnCompression = massKg · gravity / (4 · suspensionStiffness)`.

## Where the guard runs

- **Core**: `VehicleConfigGuard.Check(VehicleConfig, int stepRateHz)` returns every `ConfigError`, in
  rule-table order then relation order; `CompiledVehicleConfig.Create` throws
  `ConfigValidationException` with them.
- **Server**: `SimConfigJson` binds the optional `vehicles` section; `SimConfigValidator` reports 015's
  errors, then the vehicle guard's. `simcore config check` therefore checks a file with or without
  vehicles.
- **Browser**: `BatchStepper.LoadVehicleConfig(block)` returns `ConfigInvalid` with one `path: message`
  line per error, as `LoadConfig` does.

## Tests that pin this contract

In `tests/MmoEngine.SimCore.Tests/Vehicles/Config/`: a Theory over every field at its minimum, maximum,
one step outside each, NaN and ±infinity; a Theory over every relation's boundary; an omitted leaf for
every field (server binder); the placeholder file passing; the block round-trip; and the guard returning
the same errors on the server and through `LoadVehicleConfig`.
