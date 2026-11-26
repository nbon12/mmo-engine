# Contract: Selection configuration

The selection's own thresholds (FR-036): the behavior tolerances of US1 scenarios 1 to 7, the divergence
window and bounds, and the decision rule's tie band. It is kept apart from the vehicle configuration that
streams carry and from the vehicle rules, so changing it changes no stream and no rules revision; the
decision record fingerprints it as an input of its own (FR-028). The contact tolerance is a simulation
input and stays in the vehicle configuration ([vehicle-config.md](vehicle-config.md)).

Every value is a placeholder the agent chose (spec Assumptions, "Behavior tolerances and time bounds"),
an estimate, not a measurement.

## File

`simcore/vehicles/selection.json`:

```json
{
  "formatVersion": 1,
  "acceleration": {
    "durationSeconds": 10,
    "minFractionOfTopSpeed": 0.95,
    "maxOvershootFraction": 0.02,
    "maxTiltDeg": 10,
    "wheelContactAfterSeconds": 1
  },
  "braking": { "restSpeed": 0.1, "maxDistanceFraction": 1.2 },
  "turning": { "targetSpeed": 5, "speedBand": 0.5, "maxSeconds": 15, "maxRadiusError": 0.25 },
  "landing": { "settleSeconds": 2 },
  "collision": { "withinSteps": 3, "maxClosingSpeedFraction": 0.1 },
  "divergence": { "windowSeconds": 0.5, "maxPositionErrorMetres": 0.05, "maxOrientationErrorDeg": 1 },
  "decision": { "tieBand": 0.1 }
}
```

## Rules

`SelectionConfigValidator` (FluentValidation, `src/MmoEngine.SimCore.Vehicles.Selection/Config/`)
refuses an invalid file with one `<path>: <message>` per invalid field (FR-036), in the table's order.
Every member is required, and unknown members are refused (`is not a known field`).

| Path | Rule |
| --- | --- |
| `formatVersion` | 1 |
| `acceleration.durationSeconds` | 1 to 120 |
| `acceleration.minFractionOfTopSpeed` | greater than 0, at most 1 |
| `acceleration.maxOvershootFraction` | 0 to 1 |
| `acceleration.maxTiltDeg` | greater than 0, at most 90 |
| `acceleration.wheelContactAfterSeconds` | 0 to `acceleration.durationSeconds` |
| `braking.restSpeed` | greater than 0, at most 5 |
| `braking.maxDistanceFraction` | at least 1, at most 10 |
| `turning.targetSpeed` | greater than 0, at most 50 |
| `turning.speedBand` | greater than 0, below `turning.targetSpeed` |
| `turning.maxSeconds` | 1 to 120 |
| `turning.maxRadiusError` | greater than 0, at most 1 |
| `landing.settleSeconds` | greater than 0, at most 30 |
| `collision.withinSteps` | an integer, 1 to 60 |
| `collision.maxClosingSpeedFraction` | 0 to 1 |
| `divergence.windowSeconds` | greater than 0, at most 10 |
| `divergence.maxPositionErrorMetres` | greater than 0, at most 10 |
| `divergence.maxOrientationErrorDeg` | greater than 0, at most 180 |
| `decision.tieBand` | 0 to 1 |

Numbers are read as `System.Decimal` (`Utf8JsonReader.GetDecimal`), so `tieBand` 0.1 is exactly one
tenth and the decision's comparisons are exact (research R25). The checks convert to `double` only where
they compare with a measured `double`.

## Window check (US5 scenario 6, FR-022)

`WindowCheck.Run(selectionConfig, profilesDirectory)` reads every `harness/profiles/*.json` (014's
network profiles: `name`, `roundTripMs`, `jitterMs`, `lossPercent`) and, for each rate 20, 30 and 60 Hz:

```text
window = ceil(windowSeconds × rate)
span   = ceil((roundTripMs + 2 × jitterMs) / 1000 × rate) + 2 + (lossPercent > 0 ? 1 : 0)
```

Every pair needs span ≤ window. A failing pair is reported as
`profile Poor at 20 Hz: reconciliation span 11 steps, window 10 steps`. With the committed profiles the
tightest pair is Poor at 20 Hz: 7 + 2 + 1 = 10, exactly the window. The check runs in
`WindowCheckTests.TheWindowCoversEveryCommittedProfile` and in `simcore vehicles selection-config check`
([vehicles-cli.md](vehicles-cli.md)).

## Fingerprint

`SelectionConfigFingerprint`: XxHash64 (seed 0) of the file's canonical JSON (members in the order
above, decimal values in invariant culture with no trailing zeros, no whitespace, LF), printed as 16
lowercase hex digits. The decision record stores it (FR-028).

## Tests

In `tests/MmoEngine.SimCore.Vehicles.Tests/Selection/`: a Theory over each rule's boundaries and one
step past them; a missing and an unknown member; the committed file passing; the window check on the
committed profiles and on fixture profiles with a span one step over the window at each rate; the
fingerprint's stability under whitespace and member-order changes in the file.
