# Research: Shared Simulation Core

Phase 0 output for `015-shared-simulation-core`. Each decision records what was chosen, why, and what
else was considered. External design sources are listed at the end (Principle VII).

**Evidence labels.** Five throwaway prototypes were built and measured before this plan: math,
collision and movement, the analyzer, the WebAssembly build, and formats with weapons and projectiles.

- **[measured]**: measured by a prototype. Every measurement ran in the same sandbox: a 4-vCPU Xeon at
  2.3 GHz with AVX-512 and FMA, .NET SDK 10.0.115 (runtime 10.0.15), and Node 22. Sandbox timings are
  not runner-class numbers; the measurement job (R17) records those.
- **[documentation]**: taken from documentation, a specification or the literature.
- **[estimate]**: reasoned, not measured.

**Frame.** Every formula uses the Y-up frame of R2.

**Open decisions.** None is settled here:

- **3 (tick rate):** stays open. The step length is validated configuration (default 30 Hz), tested
  at 20, 30 and 60 Hz.
- **5 (hit detection):** stays open. Projectile flight and hitboxes serve both options, and no claim
  or claim validation is built.
- **4 and 9:** not touched.

## R1. Projects and layout

**Decision**:

| Project | Role |
|---|---|
| `src/MmoEngine.SimCore/` | The core (net10.0; `IsTrimmable`, `IsAotCompatible`, `AllowUnsafeBlocks=false`, `ImplicitUsings=disable`). It references only the BCL and `System.IO.Hashing`. It holds every step, the formats, the configuration guard, `BatchStepper` and `StreamRunner`. |
| `src/MmoEngine.SimCore.Analyzers/` | The analyzer (R5). It is attached to the core with `OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all"`. |
| `src/MmoEngine.SimCore.Wasm/` | The WebAssembly host (R15). |
| `src/MmoEngine.SimCore.Server/` | What the continent server (sub-spec 5) will reuse: `SimConfigJson`, the `SimConfigValidator` FluentValidation adapter (R14), the content generators `TestWorldGenerator`, `ScenarioCatalog` and `StreamGenerator`, and `DivergenceReport`. |
| `src/MmoEngine.SimCore.Cli/` | The `simcore` CLI, with System.CommandLine and Serilog compact JSON on stderr. |
| `tests/MmoEngine.SimCore.Tests/` | Tests for the core, Server and CLI. |
| `tests/MmoEngine.SimCore.Analyzers.Tests/` | Analyzer tests. |
| `tests/MmoEngine.SimCore.Wasm.Tests/` | Tests for the WebAssembly host. |
| `benchmarks/MmoEngine.SimCore.Benchmarks/` | Benchmarks. |
| `tools/agreement/` | The agreement runners (R16). |
| `tools/simcore/` | The coefficient generators (R3). |
| `simcore/` | Committed content: config, test world, streams and golden fixtures. |

A new `.gitattributes` marks `*.mmcd`, `*.mmrs` and `simcore/golden/**/*.bin` as binary. An
architecture test, in the style of `CoreDependencyTests`, reads the core's project file. It asserts
the BCL-plus-`System.IO.Hashing` reference set and the analyzer wiring.

**Rationale**:

- **The analyzer reaches only the core** [measured].
  - NuGet drops `ReferenceOutputAssembly=false` references from the restore graph.
  - A consumer project that used `lock`, `Math.Sin` and `DateTime.UtcNow` built clean.
  - The same calls inside the core failed the build with `error MMO1002` on the exact line.
  - So the server, the bots, the host and the tests can use `Stopwatch` and `double` freely.
- **`[JSExport]` needs special build settings.** It requires unsafe code (CS0227) and a partial class
  (SYSLIB1071) [measured], so it stays out of the analyzed core.
- **`BatchStepper` and `StreamRunner` live in the core**, so the server tests cover all the logic the
  browser runs.
- **The generators live outside the core.** The browser only runs streams; it never generates
  content.

**Alternatives considered**:

- **`[JSExport]` in the core:** it would bring unsafe and browser-only code into the analyzed
  assembly.
- **A separate agreement CLI** (the WebAssembly prototype's `Agreement.Cli`): merged into one CLI,
  per the repo's one-CLI-per-component convention.
- **Generators in the core:** they would ship in the browser bundle for nothing.

## R2. Coordinate frame and units

**Decision**:

- **Frame.** Right-handed, **Y up**, metres, `float`.
- **Coordinates** are centred on the continent, with |v| ≤ 8192 m. This is validated when collision
  data loads and by the state guard. Recommended content extent: |x|, |z| ≤ 4096 m.
- **Yaw** is a `u16` binary angle (65,536 per turn).
  - Yaw 0 faces +Z, and positive yaw turns toward +X.
  - `forward = (sinψ, 0, cosψ)` and `right = forward × up = (−cosψ, 0, sinψ)`.
- **Pitch** is an `i16` binary angle in [−16384, 16384]; positive looks up.

**Rationale**:

- **Collision and the renderer.** The collision prototype was Y up. glTF 2.0, the web renderer's
  convention, is right-handed with +Y up, +Z forward and −X right [documentation]. So the browser
  client (sub-spec 8) needs no axis swap.
- **Converted prototypes.**
  - The formats prototype used Z up. Its hitbox, spread, ballistic and heightfield formulas were
    converted. The conversion changes which component each expression writes, not which operations
    run or in what order, so its cross-build results carry over.
  - The collision prototype's right vector had the opposite sign; it was replaced.
- **The 8192 m limit** comes from the measured minimum clearance with a 10 mm skin, over 24 fuzz runs
  [measured]:

  | Offset | Without local origin | With local origin (R6) |
  |---|---|---|
  | 4 km | 5.8 mm | 8.8 mm |
  | 8 km | 3.5 mm | 4.3 mm |
  | 16 km | −1.7 mm (overlap) | +1.8 mm |

  Most of the residual is the rounding of stored positions: the ulp just below 8192 m is 0.49 mm.

**Alternatives considered**:

- **Z up:** neither the renderer nor the collision code uses it.
- **The formats prototype's 16,384 m limit:** only 1.8 mm of clearance at 16 km.
- **Double positions:** FR-012 keeps the core in `float`.

## R3. Deterministic math

**Decision**: `SimMath` provides:

- `Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan`, `Atan2`, `SinCos` and `SinCosBinaryAngle`;
- `Sqrt`, which wraps the correctly rounded `MathF.Sqrt`;
- `Min`, `Max` and `Clamp`;
- `FloorToInt`, `RoundToInt` and `TruncateToInt`, which saturate.

Each function has one fixed algorithm:

- **Evaluation.** Scalar `float`, no FMA, Horner evaluation, and every operation parenthesised.
- **Reduction** for sin, cos, sincos and tan:
  - none for |x| ≤ π/4;
  - 4-part Cody–Waite for |x| ≤ 12867. The split constants have 11, 11, 11 and 24 bits, `k` comes
    from `RoundToInt`, and a Fast2Sum tail goes into the kernels;
  - exact integer Payne–Hanek above that, multiplying by a 96-bit window of a 320-bit 2/π table in
    `UInt128`, with one hand-written rounding.
- **Kernels.**
  - Sin: degree 7.
  - Cos: degree 10, with fdlibm-style compensation.
  - Tan: degree 15, with an accurate reciprocal.
  - Asin and acos: share a degree-5 polynomial, with a square-root correction above ½.
  - Atan: uses fdlibm's breakpoints.
  - Atan2: follows C99 Annex F.
- **`SinCosBinaryAngle`** reduces in integers. The quadrant is `(a + 0x2000) >> 14`, and an 8-bit
  constant makes `i·C_hi` exact. All aim maths uses this function.
- **Non-finite input** returns the `float.NaN` constant.

The documented bounds (FR-013, SC-004, US3.8), with the measured maximum in parentheses:

| Function | Domain | Bound (measured) |
|---|---|---|
| Sin, Cos, SinCos | \|x\| ≤ 12867, exhaustive | ≤ 1 ulp (0.793 / 0.800) |
| Tan | \|x\| ≤ 12867, exhaustive | ≤ 1.5 ulp (1.102) |
| Sin, Cos / Tan | finite \|x\| > 12867 (9.6e8 floats) | ≤ 2 / 2.5 ulp (1.550 / 2.072) |
| Asin, Acos | [−1, 1], exhaustive | ≤ 1 ulp (0.713 / 0.781) |
| Atan | all floats, exhaustive | ≤ 1 ulp (0.849) |
| Atan2 | 2^22 directions × 5 radii, plus 64 M random pairs | ≤ 2 ulp (1.488) |
| SinCosBinaryAngle | all 65,536 angles | ≤ 1 ulp (0.737) |

**Coefficients are generated clean-room.**

- `tools/simcore/gen_coeffs.py` runs a weighted Remez fit at 60 digits, then rounds the coefficients
  to binary32 one at a time, refitting the rest after each.
- `gen_twoopi.py` and `emit_cs.py` write `SimMathCoefficients.g.cs` and `TwoOverPi.g.cs` as `f`
  literals. A unit test pins every constant's bits.
- mpmath 1.4.1 and numpy 2.5.3 (BSD) run in a throwaway venv, pinned by `requirements.txt`. They are
  never linked.

**Tests.**

- **Server side:** the bounds are checked against `Math.*(double)`, and against `double.SinPi`/`CosPi`
  for binary angles. NaN mismatches are counted explicitly, and the domain endpoints are included.
  The first prototype harness missed asin(±1) = NaN, because a comparison with NaN is false.
- **WebAssembly side:** bits are compared with the server's only, through the cross-build tables.
  `CrossBuildTables.BuildInputs()` holds the fixed table (the same rows the server-side bound test
  checks); `agreement prepare` writes the server's results, every WebAssembly runner evaluates the
  same rows through the `EvaluateTables` export, and `agreement compare` diffs the result bits row by
  row. A NaN result matches any NaN, because NaN bits are not portable (R4); every in-domain row is
  finite and compares bit for bit ([contracts/wasm-api.md](contracts/wasm-api.md#cross-build-tables)).
  The streams cannot do this: they reach only `SinCosBinaryAngle`, and `Sin` and `Cos` at a few
  configuration inputs.
- **Gating:** a fixed table gates every pull request (SC-004). The exhaustive sweeps (about 2 minutes
  on 4 cores) are xUnit tests in `tests/MmoEngine.SimCore.Tests/Math/` that run only when
  `MMO_EXHAUSTIVE_MATH=1` is set and skip otherwise, so the pull-request run in `dotnet.yml` stays
  fast and unchanged. The `measure` job sets the variable and runs them; they write each function's
  maximum error in ulp to the job summary (R17). They are not part of the measurement report.

**Rationale**:

- **Bit identity [measured].** Hashes of the raw output bits for about 8.1 million inputs were
  identical across:
  - x64 under the default settings, `TieredCompilation=0`, `JitMinOpts=1`, `EnableHWIntrinsic=0`,
    `EnableAVX512F=0`, `EnableFMA=0` and `TieredPGO=0`, and in Debug;
  - wasm interpreted, with the jiterpreter on and off;
  - wasm AOT.

  The inputs included every 4099th bit pattern, so NaNs, infinities and subnormals were covered.
- **Exhaustive properties [measured].** Oddness, evenness, |sin|, |cos| ≤ 1, and monotonicity had no
  violation for |x| ≤ 12867.
- **Horner over Estrin.** Horner needs fewer operations, its rounding analysis is simpler, and
  Estrin's parallelism does not help the interpreter.
- **Cost [measured].**
  - x64: Sin costs 13.3 ns, against 3.8 ns for `MathF`.
  - wasm AOT: Sin costs 13.9 ns against 10.5 ns, and Atan2 costs 11.7 ns against 17.6 ns.
  - Interpreted wasm runs every function at 75–155 ns, mostly call overhead: `MathF.Sqrt` alone costs
    91 ns.
- **Generator pitfall [measured].** Remez with the rounded coefficients held fixed diverged for tan
  degree 15: the error went from 1.4e-9 to 1.7e-7. The script checks each stage against naive
  rounding and falls back.

**Alternatives considered** (all [measured]):

- **Platform `MathF`:** it differs between builds (R4).
- **3-part Cody–Waite with no tail:** up to 974 ulp near zeros.
- **Cody–Waite beyond 12867:** an absolute error of 3.1e-2 by 1e6.
- **Returning NaN or clamping out of range:** it silently poisons state.
- **Simpler kernels:** uncompensated cos (1.14 ulp), asin without the correction (2.37 ulp), a
  3-interval atan (1.92 ulp), and tan with a plain −1/t (2.40 ulp).
- **Converting binary angles to radians first:** up to 28,090 ulp near zeros.

## R4. Platform math and conversions

**Decision**:

- **Allowed** `Math`, `MathF` and `float.*` members:
  - `Sqrt`, `Abs`, `CopySign`, `Floor`, `Ceiling` and `Truncate`;
  - `Round(x)` and `Round(x, MidpointRounding)`;
  - `BitIncrement`, `BitDecrement`, `ScaleB` and `ILogB`;
  - `Clamp`, inside `SimMath` only;
  - the `float.Is*` predicates, `BitConverter` bit casts, and the constants;
  - the integer `Min`, `Max`, `Abs`, `Clamp`, `BigMul` and `DivRem`.
- **Banned:** everything else. That includes the transcendentals, the float
  `Min`/`Max`/`*Magnitude`/`*Number` family, the `*Estimate` functions, `Round(x, digits)`, `Lerp`,
  `DegreesToRadians`, `RadiansToDegrees`, `IEEERemainder` and float `%`. `Math.X(float)` overloads,
  `float.X` statics and generic-math members (`T.Cos`) count as `MathF.X`.
- **Conversions:**
  - Float-to-integer conversion happens only inside the three `SimMath` helpers, which saturate
    first: NaN → 0, ≥ 2^31 → `int.MaxValue`, < −2^31 → `int.MinValue`.
  - `ulong → float` is banned.
  - `SimMath.Min` and `Max` are the compare-selects `a < b ? a : b` and `a > b ? a : b`.
  - `Sign` is deterministic, but nothing needs it, so it stays off the list.

**Rationale** (all [measured]):

- **The allowed set is safe.** It gave identical bits in every build, including ±0, halfway cases and
  subnormals, with hardware intrinsics on and off.
- **Platform transcendentals differ.** They call glibc on x64 and musl on wasm. IEEE 754 only
  recommends correct rounding for them [documentation]. Mismatch rates between x64 and wasm:

  | Sin | Cos | Tan | Asin | Acos | Atan | Atan2 | Log10 | Cbrt |
  |---|---|---|---|---|---|---|---|---|
  | 0.79% | 0.65% | 8.4% | 0.71% | 9.5% | 12.6% | 1.3% | 52.7% | 10.4% |

- **`ReciprocalEstimate` and `ReciprocalSqrtEstimate`** gave three different answers, one per build
  kind, on 97–99% of inputs.
- **FMA was bit-identical in every build.** It is banned by FR-012 and for its cost on wasm, not
  because it diverged.
- **Min and Max agree on values, not NaNs.** `Min(0x7FC00001, NaN)` returns `FFC00000` on x64 and
  `7FC00001` on wasm.
- **No hidden precision or reordering.**
  - `a*b+c` with a = b = 1+2^-12 and c = −(1+2^-11) gives 0 everywhere; contraction would give
    2^-24.
  - `1e30f*1e30f/1e30f` gives +∞.
  - RyuJIT never contracted to FMA.
- **Out-of-range float-to-integer conversions differ, and AOT was not even stable between probes**
  (consistent with LLVM folding an undefined conversion [estimate]):

  | Conversion | x64 (saturating since .NET 9) | wasm interpreter | wasm AOT (two probes) |
  |---|---|---|---|
  | `(int)NaN` | 0 | −2147483648 | −2147483648 / 0 |
  | `(int)3e9f` | 2147483647 | −2147483648 | −2147483648 / 7581376 |
  | `(uint)5e9f` | 4294967295 | 0 | 0 |
  | `(long)1e20f` | 9223372036854775807 | −9223372036854775808 | 0 |
  | `(sbyte)300f` | 44 | 44 | −64 |
  | `(ushort)-1f` | 65535 | 0 | 0 |

- **`ulong → float` double-rounds on wasm.** (float)(2^63+2^39+1) is `0x5F000000` on wasm and
  `0x5F000001` on x64. The smaller integer types and `double → float` round correctly everywhere.
- **NaN bits are not portable.** AOT folds `0f/0f` to `7FC00000`, while x64 gives `FFC00000`.
  Signalling NaNs are quieted by some tiers only, and WebAssembly leaves NaN payloads
  nondeterministic [documentation]. Hence the state refuses NaN (R13).
- **Subnormals are preserved in every build** (no FTZ or DAZ), and the trig table includes them. A
  tick-thread check against native code that sets flush-to-zero belongs to the thread's owner
  (sub-spec 5).

**Alternatives considered**:

- **Allowing `Math.Min`/`Max`:** their NaN result depends on the ISA.
- **Allowing `IEEERemainder` and `%`:** exact and identical, but unneeded.
- **`checked` conversions:** they agree, but throw, and an exception is not a refusal path.

## R5. The determinism analyzer

**Decision**: `DeterminismAnalyzer`.

- **Project.** netstandard2.0, `Microsoft.CodeAnalysis.CSharp` **5.0.0** (MIT),
  `EnforceExtendedAnalyzerRules` and `IsRoslynComponent`.
- **Release tracking.** `AnalyzerReleases.Shipped.md` and `AnalyzerReleases.Unshipped.md` are
  `AdditionalFiles`.
- **Rules.** All nine are Error and `NotConfigurable`. The full table is in
  [contracts/analyzer-rules.md](contracts/analyzer-rules.md).

| ID | Rule |
|---|---|
| MMO1001 | Vector or SIMD type outside the lane-wise `Vector128<float>` allowlist, including horizontal `Sum`/`Dot` |
| MMO1002 | Platform math outside the allowlist; the message names the `SimMath` replacement |
| MMO1003 | Fused multiply-add (`FusedMultiply*`, `MultiplyAddEstimate`, `X86.Fma`) |
| MMO1004 | Iteration over a hash-ordered collection, including LINQ, spreads and conversions to `IEnumerable<T>` |
| MMO1005 | Ambient time, randomness (even seeded `System.Random`) or a per-process hash seed |
| MMO1006 | Locks and blocking, including `[MethodImpl(Synchronized)]` and `Task.Result` |
| MMO1007 | `double`, `Half`, `NFloat`, `nint`, `nuint` (constants excepted) |
| MMO1008 | `#if`, platform and runtime queries, reflection, `dynamic`, `UnsafeAccessor` |
| MMO1009 | Float-to-integer or float-to-enum conversion outside the `SimMath` helpers, including the integer types' generic-math creators (`int.CreateSaturating(x)`), and `ulong → float` |

- **One diagnostic per operation**, by precedence FMA > vector > platform math. So
  `Vector128.FusedMultiplyAdd` reports MMO1003 only (US3.1, US3.3).
- **Lists.** The allowlists and the replacement map are tables in the analyzer's source, so the core
  cannot widen them.
- **Matching** uses `OriginalDefinition` metadata names.
- **Generated code.** `ConfigureGeneratedCodeAnalysis(Analyze | ReportDiagnostics)` stops generators
  from smuggling calls in.
- **Attribute applications are skipped** by the operation rules: they are metadata, never run by a
  step. Without this, the SDK-generated `AssemblyInfo.cs` (`System.Reflection.AssemblyVersion`,
  `AssemblyMetadata("IsTrimmable", …)` and the rest) would report MMO1008, because Roslyn models each
  attribute as an object creation of a `System.Reflection` type.

**Rationale**:

- **Each rule answers a measured divergence:**
  - `Vector3.Lerp` is fused on x64 and unfused on wasm, so 48,529 of 200k results differ;
  - `Vector<float>.Count` is 8 on x64 and 4 on wasm;
  - `nint` and `NFloat` change width;
  - a `HashSet<int>` enumerates as `1,7,3,9,5` after removals;
  - `string.GetHashCode` changes per process;
  - float-to-integer conversions differ (R4). `int.CreateSaturating(float.NaN)` and
    `int.CreateTruncating(float.NaN)` give 0 on x64 and −2147483648 in the wasm interpreter, and so
    does a cast of NaN to an `int`-backed enum, because CoreLib and the cast both reach the raw
    conversion [measured, design review].
- **Horizontal `Vector128` ops are banned as policy.** Lane-wise ops, `Sum` and `Dot` were identical
  over 200k vectors, but the reduction order is an implementation detail.
- **`double` is banned for FR-012**, not for determinism. Untyped literals silently create doubles,
  and the hash assumes float32 fields.
- **The Roslyn pin [measured].** SDK 10.0.115's compiler is 5.0.0. An analyzer built against 5.3.0
  gets CS9057. Under `-warnaserror` the build fails; without it, **the analyzer silently does not
  run**. Keep 5.0.0 while `global.json` is on 10.0.1xx.
- **RS2008** is an error under `TreatWarningsAsErrors`, which is why the release files exist.
- **Suppression [measured].**
  - `NotConfigurable` defeats `#pragma`, `.editorconfig` and `.globalconfig` severity, `NoWarn` and
    `TreatWarningsAsErrors=false`.
  - `[SuppressMessage]` and `RunAnalyzers=false` still bypass the build.
  - The backstop `CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes` closes the bypasses. It
    compiles the core's real sources in the test process with `reportSuppressedDiagnostics: true` and
    asserts zero diagnostics. The core disables implicit usings, so the sources compile as they are.
    With an assembly-level `[SuppressMessage]`, the core built but the backstop failed. It also
    catches `RunAnalyzers=false` and CS9057.
- **Testing.**
  - The harness is `CompilationWithAnalyzers` with `Basic.Reference.Assemblies.Net100` 1.8.14 (MIT).
  - The Theory `EachSampleProducesExactlyItsRule` has at least one violating and one allowed row per
    rule. Each row asserts that the sample compiles cleanly, and the exact IDs, count, severity and
    location.
  - The prototype's 81 tests ran in about 2 s. Its coverage was 94.4% line, so rows are added to
    clear the 95% gate.
  - One end-to-end Fact builds a temp project with a violation (1.1 s).
- **The regionizer keeps its regex `BannedApiTests`**, because it computes in doubles (013 research
  D8).

**Alternatives considered**:

- **`Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` 1.1.4:** it downloads reference packs at test
  time and pulls in a large dependency tree.
- **`TRUSTED_PLATFORM_ASSEMBLIES`:** it works, but is tied to the test host's runtime. Kept as the
  fallback.
- **BannedApiAnalyzers:** one ID, no allowlists, and blind to `foreach`, conversions, `lock` and
  `double`.
- **A regex scan:** misses generic math, method groups and conversions.
- **Banning all intrinsics:** stricter than FR-012 needs.

## R6. Collision world

**Decision**: An immutable world, built once by core code from an `MMCD` file and shared read-only,
including by parallel movement workers.

- **Heightfield.** Sample (i, j) is at
  `(originX + (float)i·cellSize, h[j·columns + i], originZ + (float)j·cellSize)`.
  - The diagonal runs v00 → v11. Triangle k=0 is (v00, v11, v10); k=1 is (v00, v01, v11).
  - ID = 2·(j·(columns−1) + i) + k.
  - Triangles are generated on demand and are never degenerate.
- **Meshes.**
  - Each triangle's normal and AABB are computed at load.
  - Degenerate triangles (an edge under 1 mm, or a tiny cross product) are dropped and counted.
  - Mesh IDs follow every heightfield ID, in file order.
- **Grid.** A uniform X–Z grid, with its cell size stored in the file ([1, 64] m, placeholder 4 m).
  - It is held as CSR arrays, each cell's list ascending.
  - At most 64 triangles per cell, validated at load.
  - Derived data is never stored in the file.
- **Query.** `Query(box, Span<int>)` returns ascending IDs with no allocation.
  - It visits the heightfield cells overlapping the box, skipping any whose corner height range
    misses it.
  - It then visits the grid triangles, deduplicated by the **reference-point test**: each triangle is
    kept only in the cell holding `(max(tri.minX, box.minX), max(tri.minZ, box.minZ))`.
  - The world allocates the span once, when it is created, like every other buffer it owns.
  - Its capacity is `MaxQueryCandidates` for the widest box the configured capsule can sweep,
    computed from the world's cell sizes and the per-cell limit ([data-model.md](data-model.md)). A
    query that would overflow it refuses the step (`QueryCapacityExceeded`) and never truncates; a
    valid configuration makes that impossible.
- **Local origin.** Coordinates are snapped to a 64 m lattice. The origin is subtracted before the
  closest-point maths and added back after; the subtraction is exact by Sterbenz's lemma.

**Rationale**:

- **Shared vertices are bit-identical.** One vertex expression is used everywhere, so the surface is
  watertight.
- **The reference-point method keeps queries concurrent.** It has no mutable state, and matched a
  stamp array exactly [measured].
- **Capacity sizing [measured].** A sub-move is at most r long, so a query needs about 274 slots for
  1 m cells. The fuzz world's worst query returned 58 candidates.

**Alternatives considered**:

- **A stamp array:** it needs per-thread mutable scratch.
- **A stored grid:** the checksum would cover derived data.
- **Absolute coordinates:** they erode the skin (R2).

## R7. Capsule sweep by conservative advancement

**Decision**: Newton steps on f(t) = dist(capsule axis at t, triangle) − (r + skin).

- **Parameters.** At most 8 iterations, with a stop tolerance of 0.1·skin and a minimum approach of
  1e-4·|d|.
- **Each iteration** recomputes the axis from the start, so no error accumulates.
- **Sweeps.**
  - Candidates are culled by AABB and by plane.
  - The minimum t wins, with ties going to the lower ID.
  - The four boundary planes use IDs −4 to −1, so they win ties.
  - Triangles are double-sided.
- **Closest points** follow Ericson §5.1.5, §5.1.9 and §5.1.10, in a fixed candidate order with a
  strict `<`.
- **Face contacts** use the exact face normal, so walls stay flat. Edge and vertex contacts use the
  direction between the closest points.

**Rationale**:

- **It never penetrates.** f is the distance to a convex set composed with an affine map, so it is
  convex [documentation: Boyd and Vandenberghe §3.2]. Each Newton step lands at or before the true
  time of impact, so the cap can only stop the capsule short.
- **Accuracy [measured]**, over 5,737 random sweeps against a double-precision reference:
  - no stop was closer than r;
  - the stop gap was within [−4.5e-7, +9.65e-4] m;
  - 83% of sweeps needed one closest-point evaluation, and the most was six;
  - the cap was never reached, there or in 72,000 fuzz steps.
- **Closest points [measured]** were within 6.2e-7 m of double references over 25,000 cases.

**Alternatives considered**:

- **Exact analytic time of impact:** many degenerate branches, and falling (motion parallel to the
  axis and to vertical edges) is the degenerate case.
- **Sub-steps with push-out:** no time of impact for landing, cost that grows with speed, and corners
  that fight. Push-out stays only as a bounded safety net.

## R8. Movement step

**Decision**: A kinematic collide-and-slide controller that moves velocity toward a target velocity.
In order:

1. **Validate the input.** Take the yaw's sine and cosine from `SinCosBinaryAngle`.
2. **Stance.** Crouching always succeeds. Standing tests only the extra part of the standing
   capsule, and reports `HeadroomBlocked` when it fails (US1.6).
3. **Wish speed.** Crouched gives crouch speed, then the `Walk` bit gives walk speed, and otherwise
   run speed. Diagonals are scaled by the literal `0.70710677f`.
4. **Horizontal velocity** moves toward the target by at most rate·dt.
5. **Vertical velocity.** Grounded, vy = 0, or JumpSpeed if Jump is held. Airborne, vy −= g·dt,
   clamped at the maximum fall speed.
6. **Slopes.** When grounded, project onto the ground: `v.y = −(n.x·v.x + n.z·v.z)/n.y`.
7. **Sub-moves.** `n = clamp(⌈|v·dt|/r⌉, 1, MaxSubMoves)`. Each sub-move runs:
   - **(a) push-out**, at most 4 iterations, reporting `Stuck`;
   - **(b) slide**, at most `maxSlideIterations` sweeps (placeholder 5):
     - walkable hits re-project onto the support plane when grounded, and clip against the
       contact normal when airborne (a landing is then found by the ground probe);
     - steep hits clip against the horizontalised normal when grounded, in walls-only mode, or
       airborne with v.y > 0, and then zero v.y if the velocity still points into the face, so a
       character never climbs a steep face;
     - a steep hit while airborne with v.y ≤ 0 clips against the true contact normal, so a falling
       character slides down the face instead of hanging on it;
     - the crease rule applies only when the velocity clipped against the new plane would move back
       into an earlier plane of the same slide, the three-plane stop only when the crease velocity
       still moves into a third plane, and the no-reversal rule as before; a cap with motion left
       reports `SlideCapReached`;
   - **(c) step-up**: up, forward against walls only, then down. It is accepted only if the landing
     **contact point** rises at most StepHeight above the starting contact.
8. **Ground probe** with snap. Support is a walkable normal, **or** an edge or vertex contact on a
   walkable face whose contact normal's y is at least `ledgeSupportMinNormalY`.
9. **Bounds** are four vertical planes inside the sweep, reporting `ClampedToBounds`.
10. **Commit**: the finite check and −0 canonicalisation (R13).

The placeholders are:

- speeds 3.5, 7.0 and 2.0 m/s (walk, run, crouch);
- capsule radius 0.4 m, heights 1.8 and 1.2 m;
- step height 0.35 m, slope limit 45°;
- skin 0.01 m.

The full table and its relations are in [contracts/config-rules.md](contracts/config-rules.md). For
example, StepHeight must be at least r·(1 − cos limit).

**Rationale**: Each rule fixes a failure the prototype measured.

| Pitfall | Measured effect | Fix |
|---|---|---|
| Ghost hits on internal heightfield edges | The flat run covered 6.822 m of 6.825 m, and the wall slide ran 0.35% slow | Re-project walkable hits: 6.82500 of 6.82500 |
| Bounds clamped after the move | Up to 6.6 cm into sloped ground | Bound planes in the sweep: minimum clearance 9.7 mm |
| Step rise measured by feet height | Rises of 0.36 m and 0.50 m climbed (StepHeight 0.35 m) | Measure the contact point |
| No ledge rule | A 0.3 m stair not climbed at walk speed (30 and 60 Hz) or crouch speed | The ledge rule: climbs at 20, 30 and 60 Hz |
| A steep re-hit loop while grounded | 0.26% of slides used every iteration | Zero v.y against steep faces |

- **Two slide rules were found in design review, after the prototype**:
  - With every steep hit clipped against the horizontalised normal, a character falling onto a
    50° face keeps v = (0, v.y, 0), because the horizontal clip cannot remove a vertical component;
    zeroing v.y then stops it, and it hangs on the face, airborne, step after step. Clipping a
    falling character against the true normal lets it slide down.
  - An unconditional crease stops a character dead at every obtuse concave join. On a wall faceted
    at 10°, n1 = (0, 0, 1) and n2 = (−sin 10°, 0, cos 10°) give a vertical crease, so a horizontal
    velocity projects to 0. Clipping against n2 alone gives (6.79, 0, 1.20) from (7, 0, 0), which
    does not re-enter facet 1, so no crease is needed.

  Tests, in `tests/MmoEngine.SimCore.Tests/Movement/` at 20, 30 and 60 Hz, on small worlds built in
  the test: `SlideTests.FallingOntoASteepFaceSlidesToItsFoot` (Theory over 50° and 70° faces: a
  character released at rest 0.5 m above a face of height h and angle θ, with no input, is grounded on
  the floor at its foot within twice the frictionless time, 2·(√(1/g) + √(2h/g)/sin θ) seconds rounded
  up to whole steps; its Position.y never rises; and it is never grounded on the face) and
  `SlideTests.FacetedConcaveWallKeepsItsParallelSpeed` (Theory: a concave wall faceted at 10°, walked
  along with Forward held parallel to the first facet, and walked into at 45°: in the step whose
  slide reaches each join, the horizontal speed after the step is at least cos 10° times the speed
  at its start, less 1e-4 m/s, the character never stops, and no step reports SlideCapReached).
  `corner-wedge` still stops at the 90° corner, where the crease applies.
- **The ledge rule makes the resting point depend on the rate.** Dropped onto a lip, the axis settles
  at x = 11.645, 11.75 and 11.655 at 20, 30 and 60 Hz. Each rate is deterministic, so tests assert
  per-rate positions.
- **The slide cap** [measured], across 77,000 fuzz slides:

  | Cap | Capped slides |
  |---|---|
  | 4 | 55 |
  | 5 | 7 |
  | 6 | 1 |
  | 8 | 0 |

  At 5, a capped slide stops short, safely. 8 would add 60% more worst-case sweeps [estimate].
- **Scenario results [measured]**, at 20, 30 and 60 Hz, all with clearance ≥ 9.9 mm:
  - the flat run is within 1e-4 m of Σₖ min(k·a·dt, v)·dt (US1.1);
  - the 45° wall (US1.2);
  - rises of 0.10–0.34 m climb, and 0.36 and 0.50 m block (US1.3);
  - 30° and 44° slopes climb, and 46°, 50° and 70° are refused (US1.4);
  - a jump and landing (US1.5) and crouching under a ledge (US1.6);
  - corners, with no oscillation;
  - 50 m/s into a zero-thickness wall, with no tunnelling.
- **Fuzz [measured].** 8 seeds × 3 rates × 3,000 steps. Clearance never fell below 9.72 mm, and
  nothing stuck or went NaN.
- **Cross-build [measured].** About 38,000 hashed steps, including worlds at +4 km and −7 km, were
  identical across x64, interpreted wasm and AOT wasm.
- **Cost [measured].**
  - x64: 2.6 µs for the flat run, 3.7 µs for the wall slide, 10.1 µs wedged in a corner, and 2.9 µs
    in the fuzz world, with 0 bytes allocated.
  - wasm: about 95 µs interpreted and 14 µs AOT.
  - About 3 ms per tick at 1,000 players is an [estimate] from this prototype only.

**Alternatives considered**:

- **Pure position projection:** loses the velocity that US1.2 tests.
- **Setting velocity directly.**
- **Quake-style friction and accelerate:** allows strafe-jump exploits. Only the behaviour was
  considered; no Quake code was consulted.
- **The rejected fixes in the table.**

## R9. Weapons

**Decision**: An integer fire accumulator.

- **Units.** A step advances `rpm` units, and a shot costs `60·stepHz` units.
- **The fire loop** fires while the wait is below `rpm`, the shot count is below `maxShotsPerStep`,
  and the weapon can fire. Each shot adds the cost.
- **The wait** then drops by `rpm`. A released trigger counts down to ready and never banks shots.
- **The bound.** `maxShotsPerStep` is the fixed loop bound (FR-005). `rpm ≤ maxShotsPerStep·60·stepHz`
  is validated.
- **Conversions**, done once by `CompiledConfig`:
  - durations → steps, as `ceil(ms·Hz/1000)` in integers;
  - heat is scaled by Hz, so it stays exact;
  - recoil is `i32` binary-angle units, converted with `SimMath.RoundToInt` (ties to even;
    [contracts/config-rules.md](contracts/config-rules.md#compile-time-conversions));
  - the result is that **weapon state holds no floats**.
- **Phases:** `Ready`, `Firing`, `Reloading`, `Switching` and `Overheated`. Within a step: prepare →
  slot change → countdowns → reload start → cooling and recoil recovery → the fire loop → the wait
  update ([data-model.md](data-model.md)).
- **`FireBlock`** codes report why a held trigger did not fire (US4.6). There is no automatic
  reload.

**Rationale** (all [measured]):

- **No drift.** Over a 1-hour hold, every rpm from 30 to 3600 in steps of 7 matches the exact schedule
  `ceil((k+1)·rpm/cost)` at every step, at 20, 30 and 60 Hz (FR-007).
- **US4.1.** 700 rpm for 3 s at 30 Hz gives exactly 35 shots; 600 rpm for 3 s gives 30 shots at
  each rate.
- **Uneven intervals.** 700 rpm at 30 Hz, an interval of 2.57 steps, fires at steps 0, 2, 5, 7 and 10.
- **Fast weapons.** 2400 rpm at 30 Hz (valid with `maxShotsPerStep` 2) gives two shots in some
  steps.
- **Integer state** makes heat, reload and recoil identical by construction.

**Alternatives considered**:

- **A float interval accumulator:** it drifts.
- **Millisecond timers without a carried remainder:** they drift when the interval is not a whole
  number of steps.
- **`floor(x + 0.5)` rounding:** 0.49999997f + 0.5f rounds up to 1 [documentation].

## R10. Spread

**Decision**: A stateless hash (FR-008).

- **Hash.** With `Mix` as the SplitMix64 finaliser:
  `h = Mix(Mix(salt ^ playerId) ^ (((ulong)slot << 32) | shotCounter))`.
- **Uniforms.**
  - `u1 = (float)(uint)(h >> 40)·2^-24` is exact, and goes through `uint` because `ulong → float` is
    banned.
  - `φ = (ushort)h` is a binary angle.
  - Bit 16 of h signs the recoil yaw kick.
- **Cone.**
  - `versMax = 2·sin²(θmax/2)`, compiled with `SimMath.Sin`.
  - `s = u1·versMax`, `cosθ = 1 − s` and `sinθ = sqrt(s·(2 − s))`.
- **Basis**, from `SinCosBinaryAngle` of the aim plus recoil:
  - `f = (cp·sy, sp, cp·cy)`;
  - `r = (−cy, 0, sy)`;
  - `u = (−sp·sy, cp, −sp·cy)`.
- **Direction:** `dir = ((cosθ·f) + (sinθcosφ·r)) + (sinθsinφ·u)`.

**Rationale** [measured], over 200k shots:

- **The bits match** in every build.
- **The cone holds.** The maximum angle is 2.00003° for a 2° cone.
- **The distribution is uniform.** χ²(versine, 19 df) = 22.9 and χ²(φ, 15 df) = 12.1, both below the
  5% critical values of 30.1 and 25.0 [documentation].
- **The versine form** avoids cancellation near θ = 0.
- **Replay in any order.** With no shared stream, shots can be replayed in any order.

**Alternatives considered**:

- **Uniform-in-disc:** needs the sine of a variable angle.
- **A seeded generator stream:** order-dependent and stateful.
- **`System.Random`:** banned (MMO1005), and its algorithm is not stable [documentation].

## R11. Projectiles and hitboxes

**Decision**:

- **Closed form** at k steps after launch (FR-009):
  - `t = (float)k / (float)stepHz`;
  - `x = x0 + (vx·t)`, `y = y0 + ((vy·t) + ((halfG·t)·t))`, `z = z0 + (vz·t)`;
  - `halfG` is compiled; gravity only, no drag.
- **Sweeping.** Each step sweeps p(k) → p(k+1), so a point-blank hit reports at the first step.
- **Launch.** Projectiles start at the eye (feet + 1.62 or 1.05 m), with velocity `dir·muzzleSpeed`.
  The shooter's own velocity is not inherited, and **the owner's hitboxes are excluded**.
- **Hit order.** The earliest hit wins by the key `(t, world before character, target ID, hitbox
  index)`, compared as exact floats (FR-010).
- **Hitbox input.** `ProjectileStep` takes hitboxes as an input span, so sub-spec 6 can pass rewound
  poses.
- **Segment–capsule.** Clip the segment to the target's bounding sphere first, using the stable form,
  then solve the cylinder and its end spheres on the short local segment.
- **Expiry.** A projectile expires on range or lifetime (US5.4), or when its end point leaves the
  ±8192 m bound (`OutOfBounds`).
- **Hitboxes.** Three capsules per stance (head, torso, legs) in local (fwd, right, up) coordinates,
  rotated by yaw only:
  - `wx = fx + ((sy·lf) − (cy·lr))`, `wy = fy + lu`, `wz = fz + ((cy·lf) + (sy·lr))`.

  The placeholder layout is in [contracts/config-rules.md](contracts/config-rules.md).

**Rationale** [measured]:

- **Ballistic bits matched across builds** (50 projectiles × 600 steps at 20, 30 and 60 Hz).
  Consecutive segments join exactly, and a closed form serves both options of open decision 5.
- **Clipping matters**, over 300k segments up to 90 m, against a double-precision version:

  | Variant | Hit/miss disagreements | Hit-point error p99 | Max |
  |---|---|---|---|
  | Unclipped | 83 | 3.2 mm | 24 mm |
  | Clipped to the bounding sphere | 0 | 0.004 mm | 0.11 mm |

- **Hitbox bits matched** across 9,363 yaws × 3 capsules. Hitboxes are not hashed, so in CI the
  pose rows of the cross-build tables compare them in every build (US5.5,
  [contracts/wasm-api.md](contracts/wasm-api.md#cross-build-tables)).
- **Owner exclusion is necessary.** The eye sits inside the owner's head capsule, so without the
  exclusion every shot would hit at t = 0.

**Alternatives considered**:

- **Per-step integration:** accumulates rounding.
- **The unclipped quadratic.**
- **Hitscan only:** cannot model heavy projectiles.

## R12. Formats

**Decision**: Three binary formats, each with an identifier and a version (FR-020). Their layouts are
in [contracts/](contracts/).

- **Input command:** 17 bytes. ID `0x49`, version 1, then a `u32` sequence, a `u32` client step,
  `u16` buttons (bit 6 is `Walk`), the aim and the weapon slot.
  - `TryDecode` throws nothing and allocates nothing.
  - Validation is separate and returns `InputRejection` codes (US1.9).
- **Collision data `MMCD` v1:** a 48-byte header, then the heights, the meshes, and an XxHash64
  trailer (FR-021).
- **Recorded stream `MMRS` v1:** a 64-byte header carrying `rulesRevision`, then the config block,
  the initial state, the steps with their expected hashes, and a trailer (FR-022).

Shared rules:

- **Encoding.** Little-endian through `BinaryPrimitives`, and floats as exact bits. NaN and ±∞ are
  refused.
- **Check order.** Magic, version, header length, declared length, count limits, checked `u64` total,
  checksum, then values. Nothing is allocated before the counts are bounded, and no partial result
  escapes (US6.3).
- **Errors.**
  - `FormatError { Format, Code, Offset, Field, Detail }`.
  - The version message is `"unsupported version {v}; this reader supports version {s}"` (US6.2).
  - A missing collision file refuses the stream before step 0 (US6.5), and a rules-revision mismatch
    is NotComparable (US2.5).
- **Golden fixtures** (FR-023, US6.1) are `<case>.bin` plus `<case>.json` under `simcore/golden/`, and
  round-trip both ways.
- **Regeneration** (FR-028) happens only through `simcore streams record --reason`. It logs old and
  new hashes, and appends the reason to `simcore/streams/REGENERATIONS.md`.

**Rationale**:

- **Repo conventions.** These follow the repo: version first, `path: problem` messages, and golden
  runs with an explicit record command.
- **XxHash64 is the repo's hash.** 64 bits is a usable content identity, whereas CRC32 reaches a 50%
  collision chance at about 77,000 files [estimate].
- **Throughput [measured]:** 5,664 MiB/s on x64, 257 MiB/s interpreted and 644 MiB/s AOT.
  `System.IO.Hashing` adds 3,460 B brotli to the bundle.
- **Binary over JSON.** Exact float bits, a future wire message, and millions of collision floats.

**Alternatives considered**:

- **A u16 sequence with wrap-around:** complexity for nothing; a u32 lasts 2.27 years at 60 Hz.
- **Float aim:** identical only by luck.
- **CRC32 and XxHash3.**
- **JSON.**
- **A stored spatial index.**

## R13. Canonical state and the step hash

**Decision**:

- **Serialisation.** The canonical state is serialised little-endian:
  - a header;
  - characters in ascending ID, each followed by its weapon records;
  - projectiles in ascending ID, including the current position;
  - the step's events.
- **Hash.** XxHash64 (seed 0), one-shot, over a world-owned buffer. It is **not chained**, and is
  printed as 16 hex digits (FR-016).
- **Commit.**
  - −0 becomes +0 **at commit**, by the integer bit test `bits == 0x80000000u ? 0u : bits`.
  - A NaN or ±∞ refuses the step with a `StepFault` naming the field path, and the previous state is
    kept (FR-017).
- **The step record** is `u32 step | u32 stateLength | state | u64 hash`.

**Rationale**:

- **Why the bit test [measured].** `x + 0f` also clears −0, but the bit test cannot be optimised
  away.
- **Why at commit.** Canonicalising at commit means equal hashes mean equal states. Otherwise
  `Atan2(±0, −1)` could diverge later.
- **Why hash the current projectile position.** A one-ulp ballistic error then shows at its own step
  (SC-002).
- **Why not chained.** Reconciliation (sub-spec 6) compares single steps.
- **Allocation and cost [measured].** Zero allocation over 10k hashes. Each 512 B costs 129 ns on
  x64, 2.28 µs interpreted and 0.76 µs AOT.
- **Not stackalloc.** About 90 KB (91,672 bytes with the placeholders) is too large for the wasm
  stack, and a stackalloc in a loop trips CA2014.

**Alternatives considered**:

- **A chained hash:** couples each step to its history.
- **Canonicalising only in the hash:** equal hashes could hide unequal states.
- **Hashing NaN:** the result would be undefined.

## R14. Configuration

**Decision**: One rule table in the core, `ConfigFields` plus `ConfigRelations`, is the single source
for validation, the binary config block and the documentation (FR-029).

- **Guard.** `ConfigGuard.Check` returns `ConfigError(Path, Message)` entries.
- **Compilation.** `CompiledConfig.Create` throws `ConfigValidationException` and performs every
  conversion. The steps accept only `CompiledConfig`.
- **Defaults.** Placeholders are in `simcore/config/default.json`. `stepRateHz` defaults to 30, with
  a valid range of [20, 60].
- **Server.** `SimConfigValidator : AbstractValidator<SimConfig>` adds each guard error with the same
  path and message, following the repo's FluentValidation pattern (`RunConfigValidator`). The repo
  has no `AddValidatedOptions`.
- **Browser.** `BatchStepper.LoadConfig` runs the guard and returns the errors.

**Rationale**: FluentValidation 12.2.0 works in interpreted wasm [measured], but it is heavy and slow
there:

- it adds **+992 KB raw (+33%) and +290 KB brotli (+32%)**;
- its first validation takes **84 ms, against 12 ms** for the table guard.

The adapter produced the same paths and messages as the guard.

**Alternatives considered**:

- **FluentValidation in the core:** the cost above.
- **Two rule sets:** they drift.
- **Validating inside each step:** costs tick time and returns partial results.

## R15. WebAssembly host and the batched API

**Decision**: A `[SupportedOSPlatform("browser")] static partial class SimCoreExports` whose one-line
`[JSExport]` methods delegate to a `BatchStepper`:

- `LoadCollision`, `LoadConfig` and `Reset`;
- `CommandBuffer` and `ReportBuffer`;
- `StepQueued` and `RunStream`;
- `EvaluateTables`, which runs the cross-build math and hitbox tables (R3).

Records are packed, and the report buffer's header gives their total length, so no record-size export
is needed ([contracts/wasm-api.md](contracts/wasm-api.md)).

Data crosses the boundary as **bytes only**:

- **Buffers.** The command and report buffers are handed to JavaScript as `IMemoryView`: the command
  buffer once, and the report buffer again after a `LoadConfig` that sizes it for larger maxima.
- **Per frame**, JavaScript writes the commands, calls `StepQueued(k)` once, and slices `n` records
  out. A batch must equal stepping one at a time (FR-025, US2.6).
- **Publish output.** The trimmed publish output keeps every export, and is the only output used.

**Rationale**: Per-call cost [measured], in µs:

| Variant | interp Node | interp Chromium Worker | AOT Node |
|---|---|---|---|
| no-op | 1.1–1.5 | 1.8 | 0.55–0.92 |
| `byte[]` in and out | 4.3–5.1 | 7.5 | 1.8–2.4 |
| shared `IMemoryView` | 1.9–2.3 | 2.3 | 1.2–1.5 |
| raw pointer | 1.7–1.9 | 2.0 | 1.1–1.4 |

- **The boundary is cheap.** It costs under 0.02% of a 16.7 ms frame, so batching is about the API's
  shape.
- **Marshalling limits.** A `Span<byte>` parameter fails at run time, and `ulong` and `float[]` are
  unsupported.
- **Build output.** `dotnet build` output is untrimmed (33 MB).

**Alternatives considered**:

- **`Span` parameters, and JavaScript numbers.**
- **Raw pointers:** unsafe code for 0.2 µs.
- **Per-call arrays.**
- **A TypeScript port:** it stays the fallback, checked against the golden fixtures.

## R16. Agreement runners

**Decision**: `tools/agreement/` is a Node package whose only dependency is `@playwright/test`
**1.63.0** (Apache-2.0, a dev dependency, pinned).

- **Node.** `node --test` runs the agreement with the jiterpreter on, then again with it off
  (`withRuntimeOptions`).
- **Browsers.** Playwright projects `chromium`, `firefox` and `webkit` run a dedicated module Worker
  that calls `RunStream`.
- **Test server.** `node:http` on `127.0.0.1` port 0, with:
  - a path-traversal guard;
  - correct MIME types;
  - `no-store` caching;
  - COOP and COEP headers;
  - a unique work directory per run.
- **Comparison.** `simcore agreement prepare` writes the manifest, the streams, the server records
  and the server's cross-build table results. `simcore agreement compare` writes the field-level
  divergence report, and the first mismatching row when a build's table results differ.
- **Build names.**
  - `node-interp` and `node-interp-nojit`.
  - `chromium-worker`, `firefox-worker` and `webkit-worker`.
  - In the measurement job, `node-interp-relinked` and `node-aot`.
- **Required builds.** Node and Chromium are required. A failed Firefox or WebKit install is
  reported as not run, never as passed.
- **Generated streams.** `StreamGenerator` uses the core's SplitMix64 as a counter-based generator,
  seeded 1–50, so the streams are the same on every run (US2.3).
- **The one-ulp check** (SC-002, US2.4). `RunStream(..., perturbStep: k)` applies `BitIncrement` to
  `characters[0].position.x` after step k commits, then serialises and hashes that step again. The
  suite must report step k first, naming that field.

**Rationale** [measured]:

- **Both runners worked on the first attempt.**
- **Firefox and WebKit could not be downloaded in the sandbox** (proxy 403). They run first in CI, and
  all three engines support what the runtime needs [documentation].
- **The boot manifest lives in `dotnet.js`,** and every asset carries a SHA-256 integrity hash. So the
  probe is a runtime parameter, never a patched output.
- **The wrong MIME type** falls back to slower instantiation, so the measurement treats it as an
  error.
- **Timer resolution.** Without COOP and COEP, Worker timers are coarsened to 100 µs; with them, they
  resolve to 5 µs.
- **Playwright's Node-side loader breaks `dotnet.js`**, but `node --test` works.
- **The jiterpreter flags take effect.** The trig table took 12.1–12.5 s with the jiterpreter off,
  against 6.1–6.5 s with it on, and it is a separate code generator.
- **A second engine catches runtime bugs.** WebAssembly f32 arithmetic is fully specified, so another
  engine catches runtime and marshalling bugs, not arithmetic ones.
- **Time budget [estimate].** About 3.5–7.5 minutes, against SC-006's 10, which `timeout-minutes: 10`
  enforces.

**Alternatives considered**:

- **A Node-side Playwright project:** its loader breaks `dotnet.js`.
- **Microsoft.Playwright for .NET:** heavier.
- **Patching a build for the probe:** breaks the integrity check.
- **`System.Random` for the generators:** its algorithm is not stable.

## R17. Interpreter against AOT, and the measurement job

**Decision**: This feature records the numbers, and gate 9 chooses the build mode later. The
`measure` job (30 minutes), in the order of
[contracts/measurement-report.md](contracts/measurement-report.md#how-the-report-is-produced):

1. Runs BenchmarkDotNet on the server steps: mean and p99, labelled `measurement` with the runner
   class (FR-030, US7.1).
2. Publishes the prebuilt interpreted build, before the workload is installed, because the workload
   makes every later publish relink.
3. Installs `wasm-tools`, continuing on error.
4. Publishes the relinked interpreted and AOT builds.
5. Runs the Node agreement on both. Each one's agreement gates whenever that build was produced: a
   relinked or AOT divergence is a real divergence between builds. The numbers never gate.
6. Measures each wasm build in Node and the Chromium Worker: download size raw, brotli and gzip;
   10 cold starts; mean and p99 per step (US7.2).
7. Validates the report and uploads it.

The job also runs the exhaustive trig sweeps (R3), which write to the job summary, not to the report.

A mode that cannot be produced is recorded as `{produced: false, reason}` (FR-031). Zero-allocation
tests run on the server build (FR-032). It was not verified that Mono tracks
`GC.GetAllocatedBytesForCurrentThread`.

**Rationale** [measured, toy kernels]:

| Build | Raw | Brotli | Cold start, Node | Cold start, Chromium Worker |
|---|---|---|---|---|
| interp, prebuilt runtime | 4,364 KiB | 1,377 KiB | 111–123 ms | 127–148 ms |
| interp, relinked | 2,946 KiB | 888 KiB | 87–165 ms | 126–160 ms |
| AOT | 6,882–6,891 KiB | 1,588–1,663 KiB | 103–113 ms | 131–165 ms |

Step cost, in µs:

| Kernel | x64 | interp Node | AOT Node |
|---|---|---|---|
| 64-iteration multiply-add chain | 0.90 | 3.3–3.5 | 0.86–0.92 |
| closest point on 256 triangles | 7.3 | 161 | 6.4 |

- **Interpreter against AOT.** The interpreter runs 3–27× slower than x64. AOT runs at about x64
  speed, but its brotli download is about 1.8× the relinked interpreter's.
- **AOT build cost.** An AOT publish takes 51–56 s, and the workload install 25 s and 1.1 GB.

**Alternatives considered**:

- **AOT in the gating job:** about 1.5–2 extra minutes, for a mode gate 9 hasn't chosen.
- **Timing on the browser's main thread.**
- **Measuring debug output.**

## R18. Coverage and CI

**Decision**:

- **Wasm coverage.** `tests/MmoEngine.SimCore.Wasm.Tests/` holds a direct `ProjectReference` to the
  host, with `[SupportedOSPlatform("browser")]` on its test classes. An entry-point test covers
  `Program.cs`.
- **The new workflow.** `.github/workflows/simcore.yml` has an `agreement` job (gating, 10 minutes)
  and a `measure` job (30 minutes).
  - It is path-filtered on `src/MmoEngine.SimCore*/**`, `tests/MmoEngine.SimCore*/**`,
    `benchmarks/MmoEngine.SimCore*/**`, `tools/agreement/**`, `tools/simcore/**`, `simcore/**`, and
    the build files (FR-027, US2.7).
  - Its actions are pinned by SHA, and `tests/workflows/test_simcore_workflow.py` pins it.
- **Auto-merge.** `auto-merge.yml` gains `"SimCore"`.

**Rationale** [measured]:

- **Linking the exports file into the test project fails the gate.** coverlet skips test assemblies.
- **`[ExcludeFromCodeCoverage]` also fails the gate.**
- **The direct reference passes.** It builds with `-warnaserror`, and coverlet reports both files.
- **The new project slows the existing workflow slightly.** Adding the host to the solution adds
  about 29 MB of packs and 7 s to `dotnet.yml`.
- **The wasm prototype's path list missed `simcore/**`,** where the recorded streams live.

**Alternatives considered**:

- **A shim library:** works, but it is a project that exists only for coverage.
- **Running the agreement in `dotnet.yml`:** browsers would install on every .NET change.

## Design sources

All of these were used as design references only. No code was copied from any of them, and no GPL,
LGPL or AGPL code was read. Quake's slide move is GPL, so it was deliberately not consulted. The
fdlibm documentation was read under its freely-distributable notice.

**Floating point and elementary functions**

- IEEE 754-2019.
- The WebAssembly Core Specification 2.0 (numerics).
- ECMA-335 Partition I §12.1.3.
- .NET 9's breaking change "FP-to-integer conversions are saturating".
- The .NET vectorization guidelines:
  https://github.com/dotnet/runtime/blob/8323e58d7b587deceb7ef39e61725bce7bfe86f8/docs/coding-guidelines/vectorization-guidelines.md
- Rapier's determinism documentation.
- Goldberg (1991), Sterbenz (1974) and Figueroa (1995).
- Cody and Waite (1980).
- Payne and Hanek (1983).
- Muller, *Elementary Functions*, 3rd ed. (2016), and Muller et al., *Handbook of Floating-Point
  Arithmetic*, 2nd ed. (2018).
- Dekker (1971).
- Brisebarre and Chevillard (ARITH 2007).
- The fdlibm documentation.
- The mpmath and numpy documentation.

**Collision, movement and projectiles**

- Ericson, *Real-Time Collision Detection* (2005).
- Mirtich (1996), and Tang, Kim and Manocha's C2A (2009).
- Boyd and Vandenberghe (2004).
- Fauerby (2003).
- Dittrich and Seeger (ICDE 2000).
- The documentation for Jolt `CharacterVirtual`, PhysX CCT, Godot `CharacterBody3D` and Unity
  `CharacterController`.
- Haines, Günther and Akenine-Möller, *Ray Tracing Gems*, ch. 7 (2019).
- Quílez's ray–capsule article.
- glTF 2.0.

**Weapons, spread and hashing**

- Steele, Lea and Flood (OOPSLA 2014), and Vigna's splitmix64 constants.
- Pharr, Jakob and Humphreys, *Physically Based Rendering*, 3rd ed., §13.6.4.
- Bresenham (1965).
- Fiedler, "Fix Your Timestep!".
- The xxHash XXH64 specification.
- RFC 1982.

**.NET and the browser**

- The Roslyn analyzer and IOperation documentation, and `ReleaseTrackingAnalyzers.Help.md`.
- The BannedApiAnalyzers and Basic.Reference.Assemblies READMEs.
- The .NET API documentation.
- Microsoft Learn on JSExport, running .NET from JavaScript, and .NET on Web Workers.
- The runtime packs' `dotnet.d.ts` and targets.
- The Playwright and `node:test` documentation.
- MDN on module workers and cross-origin isolation.
- The GitHub Actions documentation.
- The coverlet documentation.
- The FluentValidation and xUnit v3 documentation.

**New dependencies:**

- `Microsoft.CodeAnalysis.CSharp` 5.0.0 (MIT).
- `Basic.Reference.Assemblies.Net100` 1.8.14 (MIT).
- `@playwright/test` 1.63.0 (Apache-2.0, CI only).
- mpmath and numpy (BSD) are tools only, never linked.

## Prototype locations

The five prototypes were throwaway work outside the repository and were not committed. Everything the
implementation needs from them is recorded here, in the data model and in the contracts.
