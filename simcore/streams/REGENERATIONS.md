# Stream regenerations

This file is appended by `simcore streams record`; each entry gives the reason and the old and new hashes.

## 2026-11-17T00:15:17Z

- Reason: Add the corner-wedge movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario corner-wedge`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| corner-wedge | added: ea91aa0217dc3585 | added: 575bf753e22a6515 | |

## 2026-11-17T00:15:18Z

- Reason: Add the crouch-under-ledge movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario crouch-under-ledge`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| crouch-under-ledge | added: 4eb67f30f909e011 | added: 3f47944714850ce5 | |

## 2026-11-17T00:15:19Z

- Reason: Add the flat-run-20hz movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario flat-run-20hz`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| flat-run-20hz | added: e41a48ca141d1201 | added: deb5b150bc688e0c | |

## 2026-11-17T00:15:20Z

- Reason: Add the flat-run-30hz movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario flat-run-30hz`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| flat-run-30hz | added: 77b6400089110974 | added: d75efdbed95bfedd | |

## 2026-11-17T00:15:22Z

- Reason: Add the flat-run-60hz movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario flat-run-60hz`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| flat-run-60hz | added: 4bba354705caa150 | added: fc4f64b017700f82 | |

## 2026-11-17T00:15:23Z

- Reason: Add the jump-land movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario jump-land`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| jump-land | added: 54fe216dc016a73b | added: 0b74b75315c56dc1 | |

## 2026-11-17T00:15:24Z

- Reason: Add the slope-limit movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario slope-limit`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| slope-limit | added: cdb5b29fb3f3e5f8 | added: c295a0957a79ae2d | |

## 2026-11-17T00:15:25Z

- Reason: Add the stairs-blocked movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario stairs-blocked`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| stairs-blocked | added: b928ab1024f75710 | added: a62e763d26285f4b | |

## 2026-11-17T00:15:26Z

- Reason: Add the stairs-climb movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario stairs-climb`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| stairs-climb | added: 49ad1a630ac0f907 | added: e6ff177d0e8792bc | |

## 2026-11-17T00:15:26Z

- Reason: Add the thin-wall-fast movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario thin-wall-fast`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| thin-wall-fast | added: 366c0d19d28ea4ea | added: f7b37bf293c996c1 | |

## 2026-11-17T00:15:27Z

- Reason: Add the wall-slide-45 movement scenario (spec 015 US1).
- Command: `simcore streams record --scenario wall-slide-45`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| wall-slide-45 | added: c9cdeeb976a4c0b7 | added: b9be152f8d0d1450 | |

## 2026-11-17T01:16:12Z

- Reason: The ground probe and the step-up's down-sweeps now take the nearest support contact touching the capsule, so the 50-degree face no longer leaves the character airborne on the plateau (spec 015 Phase 3 review).
- Command: `simcore streams record --scenario slope-limit`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| slope-limit | cdb5b29fb3f3e5f8 -> ef6eb3374da84868 | c295a0957a79ae2d -> cf23db61b372bf66 | 86 |

## 2026-11-21T08:18:53Z

- Reason: First recording: projectile flight (stage 5) is in the world step
- Command: `simcore streams record --scenario fuzz-all-buttons`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| fuzz-all-buttons | added: 89ae4d9707d3dc24 | added: 63092789b15a64a8 | |

## 2026-11-21T08:19:17Z

- Reason: First recording of the heat-overheat scenario (spec 015 US5): its shots fly through stage 5
- Command: `simcore streams record --scenario heat-overheat`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| heat-overheat | added: 48527a5a43ea9195 | added: b735e3b1e7c61c2f | |

## 2026-11-21T08:19:23Z

- Reason: First recording of the launcher-arc scenario (spec 015 US5): its shots fly through stage 5
- Command: `simcore streams record --scenario launcher-arc`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| launcher-arc | added: df3040b1a6d2f2b8 | added: 3d950c34ebbce620 | |

## 2026-11-21T08:19:36Z

- Reason: First recording of the mesh-occludes-shot scenario (spec 015 US5): its shots fly through stage 5
- Command: `simcore streams record --scenario mesh-occludes-shot`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| mesh-occludes-shot | added: 832b5f0ef92bacdc | added: f37fe2bcbbe1e100 | |

## 2026-11-21T08:19:41Z

- Reason: First recording of the projectile-hits-hitbox scenario (spec 015 US5): its shots fly through stage 5
- Command: `simcore streams record --scenario projectile-hits-hitbox`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| projectile-hits-hitbox | added: 824803c9feb674e9 | added: bebd1bfc4e86a66e | |

## 2026-11-21T08:19:53Z

- Reason: First recording of the reload-and-empty scenario (spec 015 US5): its shots fly through stage 5
- Command: `simcore streams record --scenario reload-and-empty`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| reload-and-empty | added: c00adbb3a9f33669 | added: fb8c055cac37c513 | |

## 2026-11-21T08:19:58Z

- Reason: First recording of the rifle-burst scenario (spec 015 US5): its shots fly through stage 5
- Command: `simcore streams record --scenario rifle-burst`
- Rules revision: 1

| Stream | File hash | Final state hash | First changed step |
| --- | --- | --- | --- |
| rifle-burst | added: 211b40e5a6aecaf0 | added: 4c0e147fd1431fc0 | |
