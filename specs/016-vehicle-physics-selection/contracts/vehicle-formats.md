# Contract: Vehicle formats (canonical state version 2, recorded stream version 2, step record version 2)

A delta on feature 015's [state-serialization.md](../../015-shared-simulation-core/contracts/state-serialization.md)
and [recorded-stream-format.md](../../015-shared-simulation-core/contracts/recorded-stream-format.md).
Everything those contracts say still holds for version 1, which is unchanged (FR-015): its layouts, its
golden fixtures, its 19 committed streams, `simcore/content/test-world.mmcd` and
`SimCoreVersion.RulesRevision` = 1. This document states only what version 2 adds or changes.

Shared rules, as in 015: little-endian through `BinaryPrimitives`; floats as exact binary32 bits; no
padding; nothing allocated before every count is bounded; no partial result on a refusal.

## Canonical state version 2

The canonical state of a world that has a vehicle configuration. It has no identifier of its own:
recorded stream version 2 pins state serialisation version 2, the vehicle config block version 1 and
input command version 1.

### Layout

| Offset | Size | Field | Rule |
| --- | --- | --- | --- |
| 0 | P | the **version 1 part** | exactly 015's layout: header, characters, projectiles and the six event lists, byte for byte what version 1 writes for the same characters, weapons and projectiles; `characterCount` may be 0 |
| P | 2 | `vehicleCount` (u16) | ≤ `vehicles.maxVehicles` (at most 256) |
| P + 2 + 112·v | 112 | `vehicles[v]` | ascending ID |
| P + 2 + 112·n | 2 + 8·c | `events.vehicleContacts` | u16 count, then the records |
| … | 2 + 5·r | `events.vehicleInputRejections` | u16 count, then the records |

```text
P = 015's stateLength for the version 1 part
stateLength(v2) = P + 6 + 112·n + 8·c + 5·r
```

### Vehicle record (112 bytes)

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `id` | [1, 65535], strictly ascending |
| 4 | 12 | 3 × f32 LE | `position.x`, `.y`, `.z` | the centre of mass; \|v\| ≤ 8192 m; inside the playable area |
| 16 | 16 | 4 × f32 LE | `orientation.x`, `.y`, `.z`, `.w` | the engine's own bits; on load, \|q\|² within [0.998, 1.002] |
| 32 | 12 | 3 × f32 LE | `linearVelocity.x`, `.y`, `.z` | m/s, world frame |
| 44 | 12 | 3 × f32 LE | `angularVelocity.x`, `.y`, `.z` | rad/s, world frame |
| 56 | 52 | 4 × wheel | `wheels[w]` | front left, front right, rear left, rear right |
| 108 | 4 | u32 LE | `lastSequence` | 0 before the first applied command |

Wheel (13 bytes):

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | f32 LE | `steeringAngle` | radians, \|v\| ≤ the configured maximum; 0 for `wheels[2]` and `wheels[3]` |
| 4 | 4 | f32 LE | `spin` | rad/s |
| 8 | 4 | f32 LE | `compression` | [0, `suspensionTravel`] |
| 12 | 1 | u8 | `contact` | 0 or 1 |

Total: 4 + 12 + 16 + 12 + 12 + 4·13 + 4 = 112 bytes.

### Vehicle events

| List | Record | Size | Order | Capacity |
| --- | --- | --- | --- | --- |
| `events.vehicleContacts` | `u32 lowerId`, `u32 higherId` | 8 | ascending (lowerId, higherId) | 8 × `vehicles.maxVehicles`; more begun contacts in one step fault the step with `QueryCapacityExceeded` at `events.vehicleContacts.count` |
| `events.vehicleInputRejections` | `u32 vehicleId`, `u8 reason` (015's `InputRejection`, 1 to 4) | 5 | ascending vehicle ID | `vehicles.maxVehicles` |

A contact begins in a step when the pair's oriented-box separation, from the canonical positions,
orientations and the configured half-extents and offset, is ≤ `vehicles.contactTolerance` at the end of
the step and was > it at the end of the step before (the loaded state's poses for the first step after
a load). A loaded state has both counts 0.

### Field paths

015's paths, plus `vehicleCount`, `vehicles[i].id`, `.position.x` … `.position.z`, `.orientation.x` …
`.orientation.w`, `.linearVelocity.x` … `.z`, `.angularVelocity.x` … `.z`, `.wheels[w].steeringAngle`,
`.wheels[w].spin`, `.wheels[w].compression`, `.wheels[w].contact`, `.lastSequence`,
`events.vehicleContacts.count`, `events.vehicleContacts[k].lowerId`, `.higherId`,
`events.vehicleInputRejections.count`, `events.vehicleInputRejections[k].vehicleId`, `.reason`. An index
is the element's position in its list. `StateSerializer.Layout` gains these rows after 015's 74; a wheel
field's element index is the vehicle index × 4 + the wheel.

### Canonicalisation, hashes and loading

- **Canonicalisation and the finite check** are 015's, applied to every `f32` of the whole state in
  layout order (FR-012): −0 is written and committed as +0; a NaN or infinity stops the step with
  `StepFault { NonFinite, <path> }`, for example `vehicles[0].angularVelocity.y`, and commits nothing.
- **Full state hash**: `XxHash64(stateBytes[0, stateLength), seed 0)`.
- **Version 1 part hash**: `XxHash64(stateBytes[0, P), seed 0)`. While characters and vehicles do not
  interact, it equals the full hash of the same stream with its vehicles removed and run as version 1
  (US7 scenario 7).
- **Loading** (`SimWorld.Reset` with a vehicle configuration): 015's byte-only checks on the version 1
  part, then, in layout order:

  | Check | Code | Field |
  | --- | --- | --- |
  | `vehicleCount` ≤ 256 | `Oversized` | `vehicleCount` |
  | a record or count runs past the end; bytes left after the last list | `Truncated`; `Oversized` | the field being read; `length` |
  | IDs in [1, 65535] and strictly ascending | `OutOfRange`; `Inconsistent` | `vehicles[i].id` |
  | every float finite; none is −0 | `NonFinite`; `OutOfRange` | that field |
  | positions within ±8192 m; \|q\|² in [0.998, 1.002]; `contact` ≤ 1; `compression` in [0, 8192] | `OutOfRange` | that field |
  | both vehicle event counts 0 | `Inconsistent` | `events.<list>.count` |

  Then the checks that need the configuration and the world: `vehicleCount` ≤ `vehicles.maxVehicles`
  (`Oversized`); every position inside the playable area (`OutOfRange`); `compression` ≤
  `suspensionTravel` and \|`steeringAngle`\| ≤ the maximum, rear angles 0 (`OutOfRange`). Messages keep
  015's shape, `state vehicles[1].id at offset 2468: id 3 is not above 7`.
- **Loading a record's state** (the reset and divergence runs, research R23): `StateSerializer.ClearEvents`
  writes every event count of a committed state as 0 and drops the records, giving a loadable state;
  events are outputs, so the next step is the same.

### Maximum length

```text
MaxStateLength(v2) = MaxStateLength(v1) + 6 + 112·V + 8·(8·V) + 5·V      (V = vehicles.maxVehicles)
```

With 015's placeholders and V = 64: 91,672 + 6 + 7,168 + 4,096 + 320 = 103,262 bytes. With V = 256:
91,672 + 6 + 28,672 + 16,384 + 1,280 = 138,014 bytes. `CompiledConfig.MaxStateLength` and
`MaxStepRecordLength` (+ 24 for a version 2 record) use these values.

## Step record version 2

The record of every step of a version 2 stream (FR-012):

| Offset | Size | Type | Field |
| --- | --- | --- | --- |
| 0 | 4 | u32 LE | `step` |
| 4 | 4 | u32 LE | `stateLength` |
| 8 | `stateLength` | bytes | `state` (canonical state version 2) |
| 8 + `stateLength` | 8 | u64 LE | `hash` (full state hash) |
| 16 + `stateLength` | 8 | u64 LE | `v1PartHash` |

Total 24 + `stateLength`. A `.records` file of a version 2 stream is these records back to back; a
version 1 stream's records keep 015's 16 + `stateLength` layout. A reader learns which from the stream's
format version (the agreement manifest's `recordLayout`, [vehicles-cli.md](vehicles-cli.md#manifest-version-2)).
`StepRecord` reads and writes both; `tools/agreement/lib/runtime.mjs`'s `readRecords(bytes, layout = 1)`
takes the layout.

**Perturbation (SC-004).** For a version 2 stream that has vehicles, `StreamRunner.Run(…, perturbStep)`
applies `MathF.BitIncrement` to `vehicles[0].position.x` after that step commits, then recanonicalises,
reserialises and rehashes both hashes. The perturbation stream is `flat-accel-30hz`, step 15; its vehicle
drives along +X at x ≥ 40 m, so the bits rise by exactly 1.

## Recorded stream version 2

`.mmrs`, magic `MMRS`, version 2. Committed vehicle streams live in
`simcore/vehicles/streams/<engine>/` and are written only by `simcore streams record --engine <engine>
--reason` (FR-017; the `StreamFiles` writer and its architecture test cover these folders too).

### Header (116 bytes)

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | ASCII | `magic` | `MMRS` |
| 4 | 2 | u16 LE | `version` | 2 |
| 6 | 2 | u16 LE | `headerLength` | 116 |
| 8 | 4 | u32 LE | `rulesRevision` | `SimCoreVersion.RulesRevision` at recording |
| 12 | 4 | u32 LE | `vehicleRulesRevision` | `SimCoreVersion.VehicleRulesRevision` at recording; ≥ 1 |
| 16 | 2 | u16 LE | `stepHz` | [20, 60]; equals the config block's `stepRateHz` |
| 18 | 2 | u16 LE | `characterCount` | [0, 64]; equals the initial state's |
| 20 | 2 | u16 LE | `vehicleCount` | [0, 256]; equals the initial state's; `characterCount` + `vehicleCount` ≥ 1 |
| 22 | 1 | u8 | `engine` | 1 Bepu, 2 Jolt, 3 Rapier, 255 Fixture |
| 23 | 1 | u8 | `releaseLength` | [1, 32] |
| 24 | 32 | ASCII | `release` | `releaseLength` printable ASCII bytes (0x21–0x7E), then zero bytes |
| 56 | 8 | u64 LE | `engineFingerprint` | the identity fingerprint ([engine-adapter.md](engine-adapter.md#engine-identity)) |
| 64 | 4 | u32 LE | `stepCount` | [1, 2²⁰] |
| 68 | 8 | u64 LE | `seed` | as version 1 |
| 76 | 8 | u64 LE | `matchSalt` | as version 1 |
| 84 | 8 | u64 LE | `collisionChecksum` | as version 1 |
| 92 | 4 | u32 LE | `configLength` | ≤ 65,536; 015's config block |
| 96 | 4 | u32 LE | `vehicleConfigLength` | ≤ 65,536; the vehicle config block ([vehicle-config.md](vehicle-config.md#vehicle-config-block)) |
| 100 | 4 | u32 LE | `initialStateLength` | ≤ 1 MiB |
| 104 | 4 | u32 LE | `stepsLength` | the steps block's length |
| 108 | 8 | u64 LE | `fileLength` | ≤ 64 MiB; equals the actual length |

Total: 116 bytes (4 + 2 + 2 + 4 + 4 + 2 + 2 + 2 + 1 + 1 + 32 + 8 + 4 + 8 + 8 + 8 + 4 + 4 + 4 + 4 + 8).

The selection configuration is never in a stream (FR-014, FR-036).

### Blocks

```text
header (116) | config block | vehicle config block | initial state (v2) | steps | u64 XxHash64 trailer
fileLength = 116 + configLength + vehicleConfigLength + initialStateLength + stepsLength + 8
```

### Steps block

`stepCount` entries back to back:

| Offset (in the entry) | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 4 | u32 LE | `steps[i].step` | S₀ + i + 1 |
| 4 | 1 | u8 | `steps[i].commandCount` | ≤ `characterCount` |
| 5 + 18c | 18 | u8 index + 17-byte command | `steps[i].commands[c]` | as version 1: index below `characterCount`, strictly ascending |
| 5 + 18·C | 2 | u16 LE | `steps[i].vehicleCommandCount` | ≤ `vehicleCount` |
| 7 + 18·C + 19v | 19 | u16 index + 17-byte command | `steps[i].vehicleCommands[v]` | index below `vehicleCount`, strictly ascending; the command decoded, not validated |
| 7 + 18·C + 19·V | 8 | u64 LE | `steps[i].expectedHash` | the full state hash after the step |
| 15 + 18·C + 19·V | 8 | u64 LE | `steps[i].expectedV1PartHash` | the version 1 part hash after the step |

Total per entry: 23 + 18·C + 19·V bytes. A vehicle index is the position in the world's vehicle list
(ascending ID); vehicles neither join nor leave during a stream. A vehicle with no command in a step
steps with the neutral input.

### Decoding

`RecordedStream.Decode` now reads versions 1 and 2. Check 2 accepts `version` 1 or 2 and dispatches;
any other version is `UnsupportedVersion` with the detail
`unsupported version 3; this reader supports versions 1 and 2`. A version 1 stream then runs 015's
checks unchanged, except that message. A version 2 stream runs, in order:

| # | Check | Code | Offset | Field |
| --- | --- | --- | --- | --- |
| 3 | `headerLength` is 116; at least 116 bytes | `Inconsistent`; `Truncated` | 6; the length | `headerLength`; `header` |
| 4 | the actual length equals `fileLength` | `Truncated`; `Oversized` | the actual length; `fileLength` | `fileLength` |
| 5 | limits: `fileLength` ≤ 64 MiB, `configLength` and `vehicleConfigLength` ≤ 65,536, `initialStateLength` ≤ 1 MiB, `characterCount` ≤ 64, `vehicleCount` ≤ 256, `stepCount` in [1, 2²⁰]; both counts 0 | `Oversized`; `OutOfRange` | 108, 92, 96, 100, 18, 20, 64; 18 | that field |
| 6 | the block lengths sum to `fileLength`, in checked `u64` | `Inconsistent` | 108 | `fileLength` |
| 7 | the trailer's XxHash64 | `ChecksumMismatch` | `fileLength` − 8 | `checksum` |
| 8 | `stepHz` in [20, 60]; `vehicleRulesRevision` ≥ 1; `engine` a known code; `releaseLength` in [1, 32], printable bytes, zero padding | `OutOfRange` (`engine`, `releaseLength`, `vehicleRulesRevision`, `stepHz`); `Inconsistent` (padding) | 16, 12, 22, 23, 24 + k | that field |
| 9 | the config block (015's check 9) | as 015 | | `config.*` |
| 10 | the vehicle config block: length 2 + 6·`count`, field IDs known and ascending, every field present | `Truncated`, `Oversized`; `Inconsistent` | | `vehicleConfig.count`; `vehicleConfig.entries[k].fieldId`; `vehicleConfig.<path>` |
| 11 | `stepHz` equals the config's `stepRateHz` | `Inconsistent` | 16 | `stepHz` |
| 12 | the initial state: the byte-only checks of [loading](#canonicalisation-hashes-and-loading), then its `characterCount` and `vehicleCount` equal the header's | as loading; `Inconsistent` | the field's offset | `initialState.<path>` |
| 13 | each step entry: `step`, `commandCount` and the character commands, `vehicleCommandCount` and the vehicle commands; the entries fill the block | `Inconsistent`; `OutOfRange` (index); the input codec's codes | the field's offset | `steps[i].<path>` |

### Opening a version 2 stream to run

`StreamRunner` opens it in this order; every refusal happens before the first step (US7 scenario 5):

| # | Check | Refusal |
| --- | --- | --- |
| 1 | `RecordedStream.Decode` | `CoreFormatException` |
| 2 | `rulesRevision` = `SimCoreVersion.RulesRevision` | `RulesRevisionMismatch` at offset 8 (NotComparable) |
| 3 | `vehicleRulesRevision` = `SimCoreVersion.VehicleRulesRevision` | **`VehicleRulesRevisionMismatch`** at offset 12, `vehicle rules revision 1 in the stream; this core implements vehicle rules revision 2` (NotComparable) |
| 4 | the engine identity equals the supplied engine factory's | **`EngineIdentityMismatch`** at offset 22, `engine jolt v5.7.0 c0ffee0123456789 in the stream; this build is jolt v5.7.0 0123456789abcdef` (NotComparable) |
| 5 | a supplied collision world has `collisionChecksum` | `CollisionDataMissing` at offset 84 |
| 6 | `CompiledConfig.Create(config)` | `ConfigValidationException` |
| 7 | `CompiledVehicleConfig.Create(vehicleConfig)` | `ConfigValidationException` with `vehicles.<path>: <message>` errors |
| 8 | `SimWorld.Reset(initialState, matchSalt)` with the engine | `CoreFormatException`, field `initialState.<path>` |

`FormatErrorCode` gains `VehicleRulesRevisionMismatch` = 11 and `EngineIdentityMismatch` = 12; both are
NotComparable, never Diverged (US2 scenario 7). A step fault stops the run with `StepFaultException`, as in
015, now also for `VehicleOutOfBounds` and `EngineFault`.

The suite's outcome names are 015's: Match, Diverged, NotComparable and Refused. In the selection, a
candidate whose own committed stream is NotComparable is Failed (`StreamNotComparable`).

### API

```csharp
// src/MmoEngine.SimCore/Formats/RecordedStream.cs (additions)
public sealed class RecordedStream
{
    public const ushort FormatVersion = 2;                       // the newest version this reader writes
    public static readonly ushort[] SupportedVersions = [1, 2];
    public const int HeaderLengthV2 = 116;

    public ushort Version { get; }                               // 1 or 2
    public uint VehicleRulesRevision { get; }                    // 0 for version 1
    public EngineIdentity Engine { get; }                        // default for version 1
    public int VehicleCount { get; }
    public VehicleConfig? VehicleConfig { get; }                 // null for version 1
    public IReadOnlyList<RecordedStep> Steps { get; }            // RecordedStep gains VehicleCommands and ExpectedV1PartHash
}

public sealed record RecordedStep(uint Step, RecordedCommand[] Commands, RecordedVehicleCommand[] VehicleCommands,
                                  ulong ExpectedHash, ulong ExpectedV1PartHash);
public readonly record struct RecordedVehicleCommand(ushort VehicleIndex, InputCommand Command);

// src/MmoEngine.SimCore/Batch/StreamRunner.cs (additions)
public static class StreamRunner
{
    public static byte[] Run(ReadOnlySpan<byte> stream, IReadOnlyList<CollisionWorld> worlds,
                             IVehicleEngineFactory? engines, int perturbStep = -1);
    // The reset and divergence runs (research R23): from a committed record's state at startStep, count steps.
    public static byte[] RunWindow(ReadOnlySpan<byte> stream, IReadOnlyList<CollisionWorld> worlds,
                                   IVehicleEngineFactory? engines, ReadOnlySpan<byte> startState, uint startStep, int count);
}

// src/MmoEngine.SimCore/SimCoreVersion.cs (addition)
public static class SimCoreVersion
{
    public const uint RulesRevision = 1;          // unchanged (FR-015)
    public const uint VehicleRulesRevision = 1;   // new
}
```

A version 2 stream opened without an engine factory is refused with `EngineIdentityMismatch`
(`no engine was supplied`).

## The version 1 baseline manifest (FR-015)

`simcore/baseline/015-65112ab.sha256` lists the SHA-256 of 015's 19 committed streams and of
`simcore/content/test-world.mmcd` at commit `65112ab`, one `<sha256>  <path>` line each in path order.
`Version1BaselineTests.TheVersion1FilesMatchTheBaseline` compares it with the files in the repository and
asserts `SimCoreVersion.RulesRevision == 1`; `Version1BaselineTests.EveryVersion1StreamStillMatches` runs
each of the 19 streams under state serialisation version 1 against the expected hashes it carries
(US7 scenario 2). The manifest is written once, by the implementing task, from `git show
65112ab:<path> | sha256sum`, and no command writes it afterwards.

## Golden fixtures

Under 015's [shared fixture rules](../../015-shared-simulation-core/contracts/input-command-format.md#shared-fixture-rules),
in `simcore/golden/stream/`, written by `simcore golden write`.

**Moved** (FR-016): 015's `unsupported-version` case keeps its name and base and now writes version 3
(`write 4 "0300"`), expecting `unsupported version 3; this reader supports versions 1 and 2`. The
agreement work directory's `unsupported-version` refusal is built the same way, with the same expected
text. 015's NotComparable test on a modified `flat-run-30hz` moves to version 3 too.

**New valid cases** (each decodes and re-encodes to identical bytes, US7 scenario 1):

| Case | What it pins |
| --- | --- |
| `v2-one-vehicle` | the base of the version 2 refusals: version 2, engine Fixture, release `fixture-1`, fingerprint `0123456789abcdef`, `vehicleRulesRevision` 1, 30 Hz, 0 characters, 1 vehicle (ID 1 at (64.0, 0.85, 28.0), identity orientation, at rest, spawn compression 0.109, contact 1 on every wheel), the placeholder vehicle config block, 2 steps (step 1 with `Forward` for vehicle index 0, step 2 with none), expected hashes `0000000000000001` and `0000000000000002`, v1 part hashes `0000000000000003` and `0000000000000004` |
| `v2-characters-and-vehicles` | 1 character (015's worked example character) and 2 vehicles (IDs 1 and 7), commands for both kinds in one step |
| `v2-rejected-vehicle-command` | a vehicle command with buttons `0xFE00`: it decodes; the world rejects it |
| `v2-seeded` | a generated stream's header: `seed` 7 and a `matchSalt` with the top bit set |

**New refusal cases**, base `v2-one-vehicle` unless stated (offsets follow from the base's layout and are
listed in each case's JSON):

| Case | Mutation | Code | Field |
| --- | --- | --- | --- |
| `v2-unsupported-version` | `write 4 "0300"` | `UnsupportedVersion` (NotComparable) | `version` |
| `v2-header-length` | `write 6 "4000"` (64) | `Inconsistent` | `headerLength` |
| `v2-truncated` | `truncate` to one byte short | `Truncated` | `fileLength` |
| `v2-trailing-bytes` | `append "00000000"` | `Oversized` | `fileLength` |
| `v2-no-entities` | `write 20 "0000"` (0 vehicles, 0 characters) | `OutOfRange` | `characterCount` |
| `v2-too-many-vehicles` | `write 20 "0101"` (257) | `Oversized` | `vehicleCount` |
| `v2-unknown-engine` | `write 22 "07"`, `fixChecksum` | `OutOfRange` | `engine` |
| `v2-release-padding` | a non-zero byte after the release, `fixChecksum` | `Inconsistent` | `release` |
| `v2-vehicle-config-unknown-field` | a field ID outside sections 8 and 9, `fixChecksum` | `Inconsistent` | `vehicleConfig.entries[k].fieldId` |
| `v2-vehicle-config-missing-field` | the last vehicle entry deleted, lengths adjusted, `fixChecksum` | `Inconsistent` | `vehicleConfig.<path>` |
| `v2-state-vehicle-count` | the header says 2 vehicles, `fixChecksum` | `Inconsistent` | `initialState.vehicleCount` |
| `v2-vehicle-ids-not-ascending` | base `v2-characters-and-vehicles`, the second vehicle's ID set to 1, `fixChecksum` | `Inconsistent` | `initialState.vehicles[1].id` |
| `v2-vehicle-id-zero` | the vehicle's ID set to 0, `fixChecksum` | `OutOfRange` | `initialState.vehicles[0].id` |
| `v2-vehicle-non-finite` | `linearVelocity.y` set to NaN, `fixChecksum` | `NonFinite` | `initialState.vehicles[0].linearVelocity.y` |
| `v2-vehicle-negative-zero` | `position.y` set to −0, `fixChecksum` | `OutOfRange` | `initialState.vehicles[0].position.y` |
| `v2-state-has-vehicle-events` | one vehicle contact in the initial state, lengths adjusted, `fixChecksum` | `Inconsistent` | `initialState.events.vehicleContacts.count` |
| `v2-vehicle-command-index` | the step's vehicle index set to 1, `fixChecksum` | `OutOfRange` | `steps[0].vehicleCommands[0].vehicleIndex` |
| `v2-too-many-vehicle-commands` | `vehicleCommandCount` 2, `fixChecksum` | `Inconsistent` | `steps[0].vehicleCommandCount` |
| `v2-vehicle-rules-revision` (open) | `vehicleRulesRevision` raised by one, `fixChecksum`; opened with the fixture engine and the vehicle world | `VehicleRulesRevisionMismatch` (NotComparable) | `vehicleRulesRevision` |
| `v2-engine-identity` (open) | `engineFingerprint` changed, `fixChecksum`; opened with the fixture engine | `EngineIdentityMismatch` (NotComparable) | `engine` |
| `v2-collision-missing` (open) | none; opened with no collision world | `CollisionDataMissing` | `collisionChecksum` |
| `v2-vehicle-config-invalid` (open) | `vehicles.massKg` set to NaN, `fixChecksum` | `ConfigValidationException`: `vehicles.massKg: must be finite` | |

Tests in `tests/MmoEngine.SimCore.Tests/Formats/`: the round trips (US7 scenario 1); one Theory row per
refusal, asserting code, offset and field (US7 scenarios 3 to 5); the version 1 fixtures still decoding
(US7 scenario 2); a Theory over every vehicle float field for the −0 and non-finite rules (US7
scenario 6); and the version 1 part hash of `characters-and-vehicles` against the same stream with its
vehicles removed (US7 scenario 7).
