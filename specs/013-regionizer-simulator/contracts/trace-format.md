# Contract: Position trace (format version 1)

A trace records every movement the simulator applied, so that a later run can replay positions in
place of the synthetic movement models (FR-022). Phase 1 bot runs and live telemetry can later be
converted to this format.

A CSV file with a two-line header:

```text
#trace v1 scenario=<name> seed=<n> scheduling=<mode> configHash=<16 hex>
step,entityId,op,class,layer,x,y,z,fightTag
```

The `op` column takes these values:

- `add`: the entity's initial placement at step 0. Carries its class and fight tag.
- `move`: a position applied in the entity's region tick at that step.
- `teleportBegin`: the destination of a two-step teleport.
- `occupied` / `vacated`: a capture point's state changed. Only `step`, `entityId` and `op` are filled.
- `portalOpen` / `portalClose`: `entityId` holds the portal ID.

Rows are in application order. Doubles are written round-trip. Replay applies each row at the same
step and point in the schedule. With the same scheduling mode and seed, a replayed run's per-tick
state hashes equal the recorded run's (US4, scenario 4).
