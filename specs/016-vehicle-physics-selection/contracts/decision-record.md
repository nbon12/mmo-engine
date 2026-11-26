# Contract: Decision record, the decision rule and the staleness check

`simcore vehicles decide` reads a validated selection report ([selection-report.md](selection-report.md)),
applies the decision rule (FR-026) per build mode, and writes the decision record (FR-027). The committed
record, `simcore/vehicles/selection/decision.json`, is gate 9's decision for "Vehicle physics engine".
`simcore vehicles decision check-stale` keeps it true afterwards (FR-028), and
`simcore vehicles decision gate-plan` tells CI what the vehicle agreement job gates (FR-029).

## The rule, per mode

Inputs, for mode m: each real candidate's status in m (the fixtures never take part), its deciding cost
c = `browser[m].stepCost[chromium-worker, vehicles 1].meanMicros`, its added Brotli bytes
d = `browser[m].download.brotliBytes`, and, for Bepu, its divergence verdicts in m. Values are
`System.Decimal`; band = `decision.tieBand` from the selection configuration. x **ties** with L when
x ≤ (1 + band) × L.

1. **Qualified candidates exist.** Q = the Qualified candidates.
   1. L_c = min c over Q. T_c = { q ∈ Q : c_q ties with L_c }.
   2. If |T_c| = 1, its member wins.
   3. Else L_d = min d over T_c, and T_d = { q ∈ T_c : d_q ties with L_d }. If |T_d| = 1, its member wins.
   4. Else the member of T_d with the lowest c wins; among equal c, the fewest d; among equal c and d, the
      first in the order Bepu, Jolt, Rapier.
   5. Outcome **BitExact**.
2. **Otherwise, NonExact Bepu**, when both hold: Bepu's status in m is Diverged (so every reason is
   `RecordDiverged` or `ResetDiverged`, and every server-side check passed, server reset equivalence
   included), and every divergence result of Bepu in m, on every stream and required build, is
   `WithinBounds`. Bepu wins with outcome **NonExact**, and the record is provisional.
3. **Otherwise** the outcome is **None**, with no winner.

`conditional` is true when the outcome or the winner differs between any two modes.

## Record

Canonical JSON: members in the order below, arrays in the orders stated, numbers as the report gave them,
UTF-8, LF, two-space indentation, a final newline, no clock time. The same report gives the same bytes
(US3 scenario 7).

```json
{
  "formatVersion": 1,
  "decision": "Vehicle physics engine",
  "gate": 9,
  "rule": "specs/016-vehicle-physics-selection/spec.md#FR-026",
  "outcomes": [
    { "mode": "interp-prebuilt", "outcome": "BitExact", "winner": "jolt" },
    { "mode": "interp-relinked", "outcome": "BitExact", "winner": "jolt" },
    { "mode": "aot", "outcome": "BitExact", "winner": "jolt" }
  ],
  "conditional": false,
  "conditionalOn": null,
  "reasoning": [
    {
      "mode": "interp-prebuilt",
      "branch": "qualified",
      "qualified": ["bepu", "jolt"],
      "lowestCost": { "candidate": "jolt", "meanMicros": 41.250 },
      "costTied": ["jolt"],
      "lowestBrotli": null,
      "brotliTied": null,
      "tieBreak": null,
      "steps": ["Qualified: bepu, jolt", "lowest cost jolt 41.250 µs; ties up to 45.375 µs: jolt", "jolt wins"]
    }
  ],
  "candidates": [
    { "candidate": "bepu", "statuses": [ { "mode": "aot", "status": "NotBuilt", "reasons": [ { "code": "BuildFailed", "details": { "build": "browser-host aot", "step": "dotnet publish", "lastLines": ["…"] } } ] } ] },
    { "candidate": "rapier", "statuses": [ { "mode": "interp-prebuilt", "status": "NotTested", "reasons": [ { "code": "FitCheckFailed", "details": { "criterion": "c" } }, { "code": "LicenseNotAllowed", "details": { "component": "foldhash", "version": "0.2.0", "expression": "Zlib" } } ] } ] }
  ],
  "decidingNumbers": [
    { "mode": "interp-prebuilt", "candidate": "jolt", "meanMicros": 41.250, "brotliBytes": 402113, "divergence": null }
  ],
  "provenance": {
    "runnerClass": "gha-ubuntu-latest-x64",
    "commit": "0123456789abcdef0123456789abcdef01234567",
    "ciRun": "45066104551",
    "engines": [ { "engine": "jolt", "release": "v5.7.0", "fingerprint": "c0ffee0123456789" } ]
  },
  "fingerprint": {
    "engines": [ { "engine": "bepu", "fingerprint": "…" }, { "engine": "jolt", "fingerprint": "…" }, { "engine": "rapier", "fingerprint": "…" } ],
    "vehicleRulesRevision": 1,
    "vehicleConfig": "9a0b1c2d3e4f5061",
    "selectionConfig": "3c1f0e9a7b2d4856",
    "streams": [ { "path": "simcore/vehicles/streams/jolt/flat-accel-30hz.mmrs", "checksum": "…" } ]
  },
  "evidence": {
    "winnerLoadsExactlyOnServer": [ { "candidate": "jolt", "serverResetEquivalent": true } ],
    "parallelPhases": [ { "candidate": "bepu", "documented": false, "made": true, "threads": [1, 4], "sameHashes": true, "note": "…" } ],
    "bepuVectorPin": "DOTNET_PreferredVectorBitWidth=128: …"
  },
  "provisional": null,
  "gate9": { "vehicleExitCriterionMet": true, "statement": "…" },
  "amendmentCandidates": [ { "component": "foldhash", "version": "0.2.0", "expression": "Zlib", "neededBy": "rapier" } ]
}
```

The values are placeholders that show the shape. Rules:

| Field | Rule |
| --- | --- |
| `outcomes[]` | one per mode, in the report's mode order |
| `conditional`, `conditionalOn` | when true, `conditionalOn` is `"gate 9's WebAssembly build-mode decision"` |
| `reasoning[]` | one per mode: `branch` (`qualified`, `nonExactBepu` or `none`), every set the rule formed and the threshold it used, and `steps`, one sentence per comparison, in the rule's order |
| `candidates[]` | every real candidate, every mode, every reason |
| `decidingNumbers[]` | per mode, every candidate that had a cost, Brotli bytes or divergence maxima |
| `provenance` | from the report |
| `fingerprint` | the inputs below, as they were in the repository the report was made from |
| `evidence.winnerLoadsExactlyOnServer` | from US1 scenario 10 for each winner; Phase 2's merge and split callbacks need it |
| `evidence.parallelPhases` | per candidate, FR-027's evidence; a run not made states why |
| `evidence.bepuVectorPin` | the pin's server-wide effect (research R8) |
| `provisional` | null, or for any NonExact mode `{ "flag": true, "condition": "vehicle correction distances under the 014 network profiles (harness/profiles/*.json) are not measured against stated bounds", "givesUp": "bit-exact agreement for every world that contains vehicles, including after vehicle–character interaction lands" }` |
| `gate9` | `vehicleExitCriterionMet` is true only when every mode's outcome is BitExact, because gate 9's build mode is open; otherwise the statement names the modes whose outcome is BitExact, in which the criterion would be met once gate 9 picks one of them. A NonExact mode never meets it by itself, and the statement says so (FR-027); when every mode is None, the statement says gate 9's vehicle exit criterion is not met (US3 scenario 5) |
| `amendmentCandidates[]` | every `LicenseNotAllowed` component of any candidate, by component |

## Exit codes and output

`simcore vehicles decide --report <file> --out <file>` exits 0 for every outcome, None included; 1 when the
report fails validation (its `path: problem` lines are printed and nothing is written); 2 on a usage
error or an unreadable input (FR-037). Output: one line per mode, `<mode>: <outcome> <winner|->`, then
`decision: <conditional|unconditional>; provisional: <yes|no>`. With `--fixtures`, it decides over the
report's fixture pair instead of the real candidates, which the selection's fixture self-check uses.

## Staleness check

`simcore vehicles decision check-stale [--record simcore/vehicles/selection/decision.json]` recomputes
the fingerprint from the repository and compares it with the record's:

| Input | Recomputed from |
| --- | --- |
| each engine | `EngineIdentityFingerprint.Compute` over `simcore/vehicles/engines/<engine>.json` and its inputs |
| `vehicleRulesRevision` | `SimCoreVersion.VehicleRulesRevision` |
| `vehicleConfig` | XxHash64 of the vehicle config block of `simcore/vehicles/config/default.json` |
| `selectionConfig` | `SelectionConfigFingerprint` of `simcore/vehicles/selection.json` |
| `streams` | every `simcore/vehicles/streams/*/*.mmrs`, by path, its trailer checksum; a stream added or removed counts |

Output: `stale: <input>: record <old>, repository <new>` per changed input, then `decision record: stale`
(exit 1), or `decision record: current` (exit 0); `provisional: <condition>` first when the record
carries the flag (FR-028). A missing or invalid record exits 2.

## Gate plan

`simcore vehicles decision gate-plan [--record …]` prints one JSON line for CI's matrix:
`{"gate":"BitExact"|"NonExact"|"None","legs":[{"mode":"interp-prebuilt","engine":"jolt","require":"node-interp,node-interp-nojit,chromium-worker"}],"provisional":false}`.
With no committed record it prints `{"gate":"NoRecord","legs":[],"provisional":false}`. A conditional
record yields each mode's own winner in its leg (FR-029).

## Fixture reports

`simcore/vehicles/fixtures/decision/`, each `<case>.report.json` with its `<case>.expected.json`
record. `DecisionRuleTests.EachFixtureReportGivesItsExpectedRecord` decides each and compares bytes
(US3 scenarios 1 to 6, 10 and 11):

| Case | Covers |
| --- | --- |
| `one-qualified` | US3.1 |
| `cheapest-wins` | US3.2 |
| `cost-tie-brotli-decides`, `both-tie-lowest-cost-wins`, `exact-equal-fixed-order` | US3.3, including Jolt 100 µs / 500 KB against Bepu 109 µs / 540 KB |
| `nonexact-bepu` | US3.4, with the provisional flag |
| `none-bepu-beyond-bound`, `none-bepu-failed`, `none-every-mode` | US3.5 |
| `mixed-mode` | US3.6: no qualifier; Bepu within bounds in `aot` only |
| `cost-100-110`, `cost-100-110.01`, `cost-100-109-115`, `brotli-100-110`, `brotli-100-110.01`, `brotli-100-109-115` | US3.10, the band boundaries |
| `cheapest-diverged` | US3.11 |

`DecisionRuleTests.TheCommittedRecordComesFromTheCommittedReport` decides
`simcore/vehicles/selection/report.json` and compares with `decision.json` byte for byte, and
`StalenessTests` change each fingerprint input in a temporary copy and require the check to name it
(US3 scenarios 7 and 8, SC-003, SC-008).
