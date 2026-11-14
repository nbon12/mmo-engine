# Contract: Input command (format version 1)

One step's input from one player (FR-018): its sequence number, the client step it was made for, the
buttons held, the aim as fixed-point angles, and the weapon slot. It is 17 bytes, little-endian.

- **Source of truth.** The C# definition in `src/MmoEngine.SimCore/Inputs/` (`InputCommand`,
  `InputCodec`, `InputValidator`, `InputRejection`, `Buttons`). The browser client runs the same
  code in WebAssembly. The golden fixtures in `simcore/golden/input/` are the reference that any
  other decoder (a TypeScript fallback, if one is ever needed) is checked against.
- **Two separate steps.** Decoding checks only the frame: length, format identifier and version. A
  decode failure is a `FormatError`. Validation then checks the values against the character's state
  and the configuration (FR-019). A validation failure is an `InputRejection` code, reported in the
  step's events, and leaves the state unchanged.
- **Not covered here.** Packet framing, protocol-version negotiation, and size and rate limits on
  the wire belong to the transport and edge (sub-specs 4 and 7).

## Layout

| Offset | Size | Type | Field | Rule |
| --- | --- | --- | --- | --- |
| 0 | 1 | u8 | `formatId` | `0x49` (ASCII `I`); anything else is `UnknownMagic` |
| 1 | 1 | u8 | `version` | 1; anything else is `UnsupportedVersion` |
| 2 | 4 | u32 LE | `sequence` | must be greater than the character's `lastSequence` (validation); gaps are allowed |
| 6 | 4 | u32 LE | `clientStep` | carried, not checked by the core; sub-spec 6 uses it |
| 10 | 2 | u16 LE | `buttons` | bits 0 to 8 as below; bits 9 to 15 must be 0 (validation) |
| 12 | 2 | u16 LE | `aimYaw` | binary angle, 65,536 per turn; every value is valid |
| 14 | 2 | i16 LE | `aimPitch` | binary angle; valid range [−16384, 16384], which is ±90° (validation) |
| 16 | 1 | u8 | `weaponSlot` | must be below `input.weaponSlots` (validation) |

Total: 17 bytes (1 + 1 + 4 + 4 + 2 + 2 + 2 + 1).

The frame is the one in [data-model.md](../data-model.md): Y up, yaw 0 faces +Z, positive yaw turns
toward +X, positive pitch looks up. The movement and weapon steps turn the angles into sines and
cosines with `SimMath.SinCosBinaryAngle`, so every build sees the same bits for the same aim.
Fixed-point aim is identical by construction; float aim would be identical only by luck ([research
R12](../research.md#r12-formats)).

A `u32` sequence lasts 2.27 years at 60 Hz, so it has no wrap-around rule. Once a character has
applied sequence `0xFFFFFFFF`, every later command is rejected with `SequenceNotIncreasing`.

## Buttons

| Bit | Mask | Name | Effect |
| --- | --- | --- | --- |
| 0 | `0x0001` | `Forward` | wish direction +forward |
| 1 | `0x0002` | `Back` | wish direction −forward |
| 2 | `0x0004` | `Left` | wish direction −right |
| 3 | `0x0008` | `Right` | wish direction +right |
| 4 | `0x0010` | `Jump` | level-triggered: jumps whenever held while grounded |
| 5 | `0x0020` | `Crouch` | hold to crouch |
| 6 | `0x0040` | `Walk` | hold to walk; run is the default; crouch overrides both |
| 7 | `0x0080` | `Fire` | hold the trigger |
| 8 | `0x0100` | `Reload` | start a reload |
| 9–15 | `0xFE00` | reserved | must be 0; a set bit is rejected with `UnknownButtons` |

`Buttons` is a `[Flags]` `ushort` enum with these names. `Buttons.Known` is `0x01FF`.

## Example

Sequence 1, client step 100, `Forward` and `Fire` held (`0x0081`), yaw 16384 (facing +X), pitch
−1024 (5.625° down), slot 0. In the annotation, `v` is the version and `btns` the buttons:

```text
offset  00 01 02 03 04 05 06 07 08 09 0a 0b 0c 0d 0e 0f 10
bytes   49 01 01 00 00 00 64 00 00 00 81 00 00 40 00 fc 00
        id v  sequence    clientStep  btns  yaw   pitch slot
```

## Decoding

`InputCodec.TryDecode` runs these checks in order and stops at the first failure. The format
identifier and the version come before the length, so a command from another version is refused as
`UnsupportedVersion` even when its length differs.

| # | Condition | Code | Offset | Field | Detail |
| --- | --- | --- | --- | --- | --- |
| 1 | length is 0 | `Truncated` | 0 | `formatId` | `17 bytes expected, got 0` |
| 2 | byte 0 is not `0x49` | `UnknownMagic` | 0 | `formatId` | `format identifier 0x4A is not 0x49` |
| 3 | length is 1 | `Truncated` | 1 | `version` | `17 bytes expected, got 1` |
| 4 | byte 1 is not 1 | `UnsupportedVersion` | 1 | `version` | `unsupported version 2; this reader supports version 1` |
| 5 | length is below 17 | `Truncated` | the length | `length` | `17 bytes expected, got 12` |
| 6 | length is above 17 | `Oversized` | 17 | `length` | `17 bytes expected, got 20` |

Decoding checks no value. Reserved button bits, an out-of-range pitch and an unknown weapon slot all
decode, so a decoded command re-encodes to the same 17 bytes, and validation reports them with their
own codes. Validation needs the character's `lastSequence` and the configuration, which a decoder
does not have.

The error message has the shape `"<format> <field> at offset <n>: <detail>"`, with format `input`:

```text
input version at offset 1: unsupported version 2; this reader supports version 1
input length at offset 16: 17 bytes expected, got 16
```

Inside a recorded stream the stream decoder reports the same error with format `stream`, the file
offset and the field prefixed by the command's place, for example
`steps[3].commands[0].input.version`
([recorded-stream-format.md](recorded-stream-format.md#steps-block)).

## Validation

`InputValidator.Validate` checks a decoded command in this order and returns the first failure
(FR-019, US1 scenario 9):

| Value | `InputRejection` | Rejected when |
| --- | --- | --- |
| 0 | `None` | accepted |
| 1 | `SequenceNotIncreasing` | `sequence ≤ lastSequence` (`lastSequence` starts at 0, so the first accepted sequence is at least 1) |
| 2 | `UnknownButtons` | `(buttons & 0xFE00) != 0` |
| 3 | `PitchOutOfRange` | `aimPitch < −16384` or `aimPitch > 16384` |
| 4 | `UnknownWeaponSlot` | `weaponSlot ≥ input.weaponSlots` |

- **A rejected command changes nothing.** The world records the event `{characterId, reason}`
  ([state-serialization.md](state-serialization.md#step-events)) and steps that character with the
  neutral input: no buttons, aim and active slot unchanged, `lastSequence` unchanged. Gravity,
  slides, cooling and recoil recovery still run. A repeated or replayed command is therefore
  rejected every time.
- **An accepted command** sets the character's `aimYaw`, `aimPitch` and `lastSequence`, and its
  `weaponSlot` drives the weapon step's slot change.
- **Codes are fixed.** The values are part of the hashed state. A new code is added at the end and
  bumps the rules revision ([data-model.md](../data-model.md)).

The validator runs inside `SimWorld.Step` (stage 1 of the world step order in
[data-model.md](../data-model.md)). Callers never apply an unvalidated command.

## API

```csharp
namespace MmoEngine.SimCore.Inputs;

public readonly record struct InputCommand(
    uint Sequence, uint ClientStep, Buttons Buttons, ushort AimYaw, short AimPitch, byte WeaponSlot);

public static class InputCodec
{
    public const byte FormatId = 0x49;
    public const byte FormatVersion = 1;
    public const int EncodedSize = 17;

    // Writes formatId, version and the fields as given, without validating them.
    // Returns false, writing nothing, when destination is shorter than 17 bytes.
    public static bool TryEncode(in InputCommand command, Span<byte> destination);

    // True with the command on success; false with the error and a default command on refusal.
    public static bool TryDecode(ReadOnlySpan<byte> source, out InputCommand command, out FormatError error);
}

public static class InputValidator
{
    public static InputRejection Validate(in InputCommand command, uint lastSequence, int weaponSlots);
}
```

- **No exceptions and no allocation**, on success and on refusal. The codec sits on the tick path.
  `FormatError` (in `MmoEngine.SimCore.Formats`) is a readonly struct. For the input codec its
  `Format` and `Field` are string literals, and its `Detail` text is formatted from stored numbers
  only when it is read, so recording a refusal allocates nothing. A zero-allocation test covers
  `TryDecode` on a valid command and on each refusal, and `Validate` on each code (FR-032).
- **`TryEncode` writes any value.** It encodes commands the validator would reject, so the fixtures
  and the fuzz streams can carry them.
- **Round trip.** For every 17-byte buffer with a valid identifier and version,
  `TryEncode(TryDecode(bytes))` gives the same bytes. A CsCheck property with a fixed seed checks
  this over random buffers.

## Golden fixtures

### Shared fixture rules

These rules hold for all three formats: `simcore/golden/input/`, `simcore/golden/collision/` and
`simcore/golden/stream/` (FR-023, US6).

- **A case is two files**, `<case>.bin` and `<case>.json`, with a kebab-case name. `.gitattributes`
  marks `simcore/golden/**/*.bin` as binary.
- **JSON values.**
  - Integers of up to 32 bits are JSON numbers.
  - 64-bit integers (seeds, salts, checksums, hashes) are strings of 16 lowercase hex digits, the
    same `x16` form the reports print, because JavaScript numbers cannot hold them.
  - Floats are objects `{ "bits": "0x3F800000", "value": 1.0 }`. The bits are authoritative. `value`
    is the round-trip ("R") decimal, and a test checks that it parses back to the same bits.
    Negative zero is `{ "bits": "0x80000000", "value": -0.0 }`.
  - Other bit patterns are uppercase `0x` hex strings too: a config block entry's `fieldId`
    (4 digits) and `valueBits` (8 digits)
    ([recorded-stream-format.md](recorded-stream-format.md#golden-fixtures)).
- **A valid case** (`"kind": "valid"`) holds the decoded values. Tests check that encoding the JSON
  gives the `.bin` byte for byte, and that decoding the `.bin` gives the JSON (US6 scenario 1).
- **A refusal case** (`"kind": "refusal"`) names a `base` valid case, the `mutations` that turn the
  base's `.bin` into this case's `.bin`, and the `expectedError` (`code`, `offset`, `field`). Tests
  check that the mutations reproduce the `.bin` byte for byte, and that the decoder refuses it with
  exactly that code, offset and field and returns no result (US6 scenarios 2 and 3).
- **Mutations** apply in order:

  | Op | Arguments | Effect |
  | --- | --- | --- |
  | `truncate` | `length` | keep the first `length` bytes |
  | `append` | `hex` | add bytes at the end |
  | `write` | `offset`, `hex` | overwrite bytes in place |
  | `insert` | `offset`, `hex` | insert bytes before `offset` |
  | `delete` | `offset`, `length` | remove bytes |
  | `fixChecksum` | none | collision data and streams only: rewrite the 8-byte trailer as the XxHash64 (seed 0) of every byte before it, so the case reaches the checks that come after the checksum |

- **Writing.** The `.bin` of a valid case is the encoder's output for its `.json`; the `.bin` of a
  refusal case is its mutations applied to its base. `simcore golden write` writes both from the
  `.json` files. Tests open the fixtures read-only and never rewrite them, so a format change shows
  up as a changed `.bin` in the commit.

### Input cases

Valid cases. Each also holds a `validate` object, `{ lastSequence, weaponSlots, expected }`, and a
test checks `InputValidator.Validate` against it. Unless the table says otherwise, a case has
sequence 1, client step 0, buttons 0, yaw 0, pitch 0, slot 0, and validates with `lastSequence` 0
and `weaponSlots` 2.

| Case | Values | Validates as |
| --- | --- | --- |
| `neutral` | the defaults | `None` |
| `example` | the example above | `None` |
| `button-forward`, `button-back`, `button-left`, `button-right`, `button-jump`, `button-crouch`, `button-walk`, `button-fire`, `button-reload` | that one button | `None` |
| `all-buttons` | buttons `0x01FF` | `None` |
| `aim-yaw-max` | yaw 65535 | `None` |
| `aim-pitch-up` | pitch 16384 | `None` |
| `aim-pitch-down` | pitch −16384 | `None` |
| `sequence-max` | sequence `0xFFFFFFFF`, client step `0xFFFFFFFF` | `None` |
| `weapon-slot-last` | slot 1 | `None` |
| `reject-sequence-zero` | sequence 0 | `SequenceNotIncreasing` |
| `reject-sequence-repeat` | sequence 5, validated with `lastSequence` 5 | `SequenceNotIncreasing` |
| `reject-sequence-first` | sequence 0, buttons `0x0200` | `SequenceNotIncreasing` (sequence is checked first) |
| `reject-button-9` | buttons `0x0200` | `UnknownButtons` |
| `reject-buttons-reserved` | buttons `0xFE00` | `UnknownButtons` |
| `reject-buttons-all` | buttons `0xFFFF` | `UnknownButtons` |
| `reject-pitch-above` | pitch 16385 | `PitchOutOfRange` |
| `reject-pitch-below` | pitch −16385 | `PitchOutOfRange` |
| `reject-pitch-min` | pitch −32768 | `PitchOutOfRange` |
| `reject-pitch-max` | pitch 32767 | `PitchOutOfRange` |
| `reject-weapon-slot` | slot 2 | `UnknownWeaponSlot` |
| `reject-weapon-slot-max` | slot 255 | `UnknownWeaponSlot` |

Refusal cases, all with base `neutral`:

| Case | Mutations | Code | Offset | Field |
| --- | --- | --- | --- | --- |
| `refuse-empty` | `truncate 0` | `Truncated` | 0 | `formatId` |
| `refuse-format-id` | `write 0 "4a"` | `UnknownMagic` | 0 | `formatId` |
| `refuse-only-format-id` | `truncate 1` | `Truncated` | 1 | `version` |
| `refuse-version-0` | `write 1 "00"` | `UnsupportedVersion` | 1 | `version` |
| `refuse-version-2` | `write 1 "02"` | `UnsupportedVersion` | 1 | `version` |
| `refuse-version-2-longer` | `write 1 "02"`, `append "000000"` | `UnsupportedVersion` | 1 | `version` |
| `refuse-truncated-2` | `truncate 2` | `Truncated` | 2 | `length` |
| `refuse-truncated-16` | `truncate 16` | `Truncated` | 16 | `length` |
| `refuse-oversized-18` | `append "00"` | `Oversized` | 17 | `length` |

A valid case and a refusal case as JSON:

```json
{
  "format": "input",
  "formatVersion": 1,
  "case": "example",
  "kind": "valid",
  "command": {
    "sequence": 1,
    "clientStep": 100,
    "buttons": 129,
    "aimYaw": 16384,
    "aimPitch": -1024,
    "weaponSlot": 0
  },
  "validate": { "lastSequence": 0, "weaponSlots": 2, "expected": "None" }
}
```

```json
{
  "format": "input",
  "formatVersion": 1,
  "case": "refuse-version-2",
  "kind": "refusal",
  "base": "neutral",
  "mutations": [ { "op": "write", "offset": 1, "hex": "02" } ],
  "expectedError": { "code": "UnsupportedVersion", "offset": 1, "field": "version" }
}
```
