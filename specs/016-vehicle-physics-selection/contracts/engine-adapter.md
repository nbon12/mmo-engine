# Contract: The engine adapter interface, native ABI, browser hosts and engine identity

Every candidate is driven through one engine-neutral interface by one harness (FR-003). The core owns the
vehicle model (research R2): an engine integrates rigid chassis bodies under gravity and resolves
chassis-against-static and chassis-against-chassis contacts, nothing else (FR-033). This document defines:

- the managed interface the core calls ([Interface](#interface));
- each adapter's settings and its history-free mode ([Adapters](#adapters));
- the byte ABI every native engine exports ([Native ABI](#native-abi)) and its JavaScript glue;
- the browser hosts and the runner changes ([Browser hosts](#browser-hosts), [Runners](#runners));
- the engine identity and pin files ([Engine identity](#engine-identity));
- the analyzer wiring per adapter ([Analyzer](#analyzer)).

## Interface

```csharp
// src/MmoEngine.SimCore/Vehicles/Engine/ (namespace MmoEngine.SimCore.Vehicles), analyzed
public interface IVehicleEngineFactory
{
    EngineIdentity Identity { get; }
    IVehicleEngine Create();                       // one engine instance per world
}

public interface IVehicleEngine
{
    EngineIdentity Identity { get; }

    // Once per world: the core's kept static triangles and the compiled vehicle configuration.
    // May allocate. Discards any bodies.
    EngineResult Load(ReadOnlySpan<StaticTriangle> triangles, CompiledVehicleConfig config);

    // At Reset: the world's vehicle IDs, ascending. May allocate. Every later Step passes exactly these.
    EngineResult Begin(ReadOnlySpan<uint> vehicleIds);

    // One substep (FR-009): bodies in ascending vehicle ID, written into the engine, integrated with
    // gravity, contacts resolved, read back. Allocates nothing; takes no lock (FR-025, FR-034).
    EngineResult Step(ReadOnlySpan<VehicleBody> bodiesIn, Span<VehicleBody> bodiesOut, float substepLength);
}

public readonly record struct EngineIdentity(EngineCode Engine, string Release, ulong Fingerprint);
public enum EngineCode : byte { Bepu = 1, Jolt = 2, Rapier = 3, Fixture = 255 }

public struct VehicleBody                          // 56 bytes on the wire (below)
{
    public uint Id;
    public Vec3 Position;                          // centre of mass
    public Quat Orientation;                       // x, y, z, w
    public Vec3 LinearVelocity;
    public Vec3 AngularVelocity;
}

public readonly record struct StaticTriangle(Vec3 A, Vec3 B, Vec3 C, StaticTriangleKind Kind);
public enum StaticTriangleKind : byte { HeightfieldUp = 0, MeshDoubleSided = 1 }

public readonly record struct EngineResult(EngineResultCode Code, int EngineDetail);
public enum EngineResultCode : byte { Ok = 0, Faulted = 1, LoadRefused = 2 }
```

- **Triangles.** `CollisionWorld.CopyStaticTriangles(Span<StaticTriangle>)` (new, core) gives every kept
  triangle in triangle-ID order: heightfield triangles wound so that (B − A) × (C − A) points up, mesh
  triangles as stored, double-sided. Degenerate triangles are already dropped (FR-006).
- **Faults.** `Step` returning `Faulted` stops the world step with `StepFault { EngineFault }`, the
  detail as the engine's code; the core still applies its own non-finite check to the bodies read back.
- **The core's use** (data-model.md, World step order V2–V3): per substep, after the wheel model updates
  the velocities, one `Step` call for all vehicles. K = 1 is one call per world step.
- **Ownership.** An engine instance belongs to one world; its world is its only caller (Principle II).
  The adapter keeps every buffer it needs from `Load` and `Begin`.

## Adapters

| Adapter | Project | Engine settings (in its pin file) | History-free mode (research R5) |
| --- | --- | --- | --- |
| Fixture | `src/MmoEngine.SimCore.Vehicles.Fixture/` | internal substeps from the configured top speed and contact tolerance (bounded at 64); `fault` | stateless by construction; every fault but `state-survives-reload` and `hidden-accumulator` keeps it so |
| Bepu | `src/MmoEngine.SimCore.Vehicles.Bepu/` | `SolveDescription(velocityIterations 4, substeps 4)`; `ContinuousDetection.Continuous` on each chassis; contact `SpringSettings(30, 1)`, `MaximumRecoveryVelocity` 2 m/s; angular integration `Nonconserving`; no thread dispatcher; sleep threshold −1; no-op refinement schedulers | `Simulation.Clear()`, `PairCache.Mapping.Clear()`, re-add the statics (the cached `Mesh`) and the bodies in ascending ID, every substep |
| Jolt | `src/MmoEngine.SimCore.Vehicles.Jolt/` | `mNumVelocitySteps` 10, `mNumPositionSteps` 2, `mPenetrationSlop` 0.01, `mSpeculativeContactDistance` 0.02, `mConstraintWarmStart` false, `mUseBodyPairContactCache` false, `mDeterministicSimulation` true; 1 collision step per `Update`; `LinearCast`; sleeping off | persistent bodies created by `CreateBodyWithID(BodyID(id))` at `Begin`; every substep writes each body's pose and velocity through `GetBodyInterfaceNoLock()` before `Update` and reads them back |
| Rapier | `src/MmoEngine.SimCore.Vehicles.Rapier/` | `num_solver_iterations` 4, `max_ccd_substeps` 1, `ccd_enabled`, `can_sleep(false)`; friction combine `Average` | a fresh `PhysicsWorld` per substep (follow-up; research R12) |

Common to every engine: one chassis body per vehicle, a box with the configured half-extents and no
rounding (Jolt convex radius 0), offset vertically by `chassisOffsetY` with the centre of mass exactly at
the body origin; mass and diagonal inertia from `CompiledVehicleConfig`, never from the shape; friction
μ_c on every collider and restitution 0; gravity (0, −`gravity`, 0); bodies stepped on the calling thread.
Bepu's adapter passes angles only as vectors and quaternions and references only the Bepu members on its
committed allowlist (`BepuAllowlistTests`, research R7).

**Bepu's vector pin.** `BepuEngineFactory` is created only through `EngineCatalog` (server) or the Bepu
host (browser). On the server, `VectorWidthGuard.Require128()` runs first and throws
`InvalidOperationException("Bepu needs DOTNET_PreferredVectorBitWidth=128: Vector<float>.Count is 8 and Vector256 is hardware-accelerated")`
otherwise (research R8).

## Native ABI

Jolt's shim (`src/MmoEngine.SimCore.Vehicles.Jolt/native/`, C++) exports these, and Rapier's follow-up
shim the same. Every function is `extern "C"`, returns `int32`, and takes byte buffers; a negative return
is an error code, with a UTF-8 message at the start of `out` when `outCap` allows.

| Function | In | Out | Return |
| --- | --- | --- | --- |
| `veh_abi_version()` | | | 1 |
| `veh_identity(out, outCap)` | | the shim's identity string, for example `jolt v5.7.0 cpd=1 blue=1 fpcontract=off obj16 emmalloc` | its length |
| `veh_load(in, inLen, out, outCap)` | the load payload (below) | an error message | 0, or −1 malformed, −2 too large, −3 engine refused |
| `veh_begin(in, inLen, out, outCap)` | `u32 count`, then `count` × `u32 id` ascending | an error message | 0, or −1 not loaded, −2 malformed |
| `veh_step(in, inLen, out, outCap)` | `u32 substepLengthBits`, `u32 count`, then `count` bodies | `u32 count`, then `count` bodies | the bytes written, or −1 not begun, −2 count or IDs differ from `Begin`, −3 `outCap` too small, −4 engine fault (then `out` holds `i32 engineCode`) |

**Body record (56 bytes)**: `u32 id`, then 13 `f32` as raw bits: position x, y, z; orientation x, y, z,
w; linear velocity x, y, z; angular velocity x, y, z. Little-endian, no padding. The adapter writes and
reads them with `BinaryPrimitives`; no float is converted.

**Load payload**: `"VEHL"`, `u16` version 1, `u16` reserved 0; the vehicle block (`u32 maxVehicles`,
then `f32` gravity, mass, the three inertia diagonals, the three half-extents, `chassisOffsetY`,
`chassisFriction`); the engine settings as `u16 count` and `{u16 id, u32 bits}` entries from the pin file;
`u32 triangleCount`; then each triangle as 9 `f32` and a `u8` kind. With the vehicle world (about 98,000
triangles) the payload is about 3.6 MB.

**Server binding**: `JoltNative` (`[LibraryImport("libmmo_vehicles_jolt")]`, `SetLastError = false`,
pointers from `fixed` spans), resolved by `NativeLibrary.SetDllImportResolver` from the directory the
engine catalog is given (`MMO_VEHICLE_NATIVE_DIR` in CI). `JoltEngine.Create` checks `veh_abi_version()`
and `veh_identity` against the pin file's `nativeIdentity` and refuses a mismatch with `LoadRefused`.

**Browser binding**: `JoltJs`, `[SupportedOSPlatform("browser")]`:

```csharp
[JSImport("step", "mmo-vehicle-engine")]
internal static partial int Step([JSMarshalAs<JSType.MemoryView>] Span<byte> input,
                                 [JSMarshalAs<JSType.MemoryView>] Span<byte> output);
```

and the same for `load`, `begin`, `abiVersion` and `identity`. The glue
(`src/MmoEngine.SimCore.Vehicles.Jolt/native/glue/jolt-glue.mjs`, shipped as `wwwroot/engine/jolt-glue.mjs`)
exports `async function instantiate(baseUrl, expectedSha256)`, which fetches and checks the module files,
instantiates the module with fixed input and output areas (8 MiB and 1 MiB, no memory growth), and
returns `{ load, begin, step, abiVersion, identity }`. Each function copies `inView` into the input area
with `inView.copyTo(...)`, calls the export, and copies the result back with `outView.set(...)` before it
returns, because a MemoryView is valid only during the call (research R14).

**Binding choice**: `JoltCalls.Create()` returns the browser binding when `OperatingSystem.IsBrowser()`,
else the server one. The marshalling (`JoltEngine`) is the same code in both builds.

## Browser hosts

One host per engine, each a `Microsoft.NET.Sdk.WebAssembly` project with 015's settings (`Exe`,
`AllowUnsafeBlocks`, `InvariantGlobalization` from `Directory.Build.props`, `Program.Main` returning 0):

| Host | Engine files under `wwwroot/engine/` |
| --- | --- |
| `src/MmoEngine.SimCore.Vehicles.Fixture.Wasm/` | none |
| `src/MmoEngine.SimCore.Vehicles.Bepu.Wasm/` | none (Bepu's assemblies are in `_framework/`) |
| `src/MmoEngine.SimCore.Vehicles.Jolt.Wasm/` | `jolt-engine.mjs`, `jolt-engine.wasm`, `jolt-glue.mjs`, `jolt-engine.sha256` (copied from the `vehicle-engines` artifact by an MSBuild item; a missing file fails the publish) |

Each host's exports, `VehicleExports`, are one-line delegations to a `BatchStepper` built with the
host's engine factory (`new BatchStepper(32, factory)`) and to `StreamRunner`:

| Export | Delegates to |
| --- | --- |
| `LoadCollision(byte[])`, `LoadConfig(byte[])`, `Reset(byte[], long)`, `CommandBuffer()`, `ReportBuffer()`, `StepQueued(int)` | `BatchStepper`, as 015 ([wasm-api.md](../../015-shared-simulation-core/contracts/wasm-api.md)) |
| `LoadVehicleConfig(byte[])` | `BatchStepper.LoadVehicleConfig` |
| `RunStream(byte[] stream, byte[] collision, int perturbStep)` | `StreamRunner.Run` with the host's factory |
| `RunWindow(byte[] stream, byte[] collision, byte[] startState, int startStep, int count)` | `StreamRunner.RunWindow` |
| `EngineIdentity()` | the factory's identity as `u8 engine, u8 releaseLength, release, u64 fingerprint` |

`BatchStepper`'s changes: a vehicle configuration loaded with `LoadVehicleConfig` makes `Reset` read a
version 2 state; `StepQueued` then applies each queued command to the first vehicle (lowest ID) and the
neutral input to every other vehicle and character; its records use the version 2 record layout. With no
vehicle configuration it behaves exactly as in 015.

**The `fixture-perturbed` candidate** (fault `browser-perturb-step-15`, identity-preserving) is the
fixture engine whose browser builds perturb `vehicles[0].position.x` by one ulp at step 15 of
`flat-accel-30hz` (US2 scenario 9). `simcore agreement prepare --engine fixture --fault
browser-perturb-step-15` writes `"injectedFault": { "stream": "flat-accel-30hz", "step": 15 }` into its
manifest, and the runners pass that step as `perturbStep` to every `RunStream` of that stream, while the
server records stay unperturbed; so every required build of every mode is Diverged (`RecordDiverged`) at
step 15 and the windowed runs still match.

## Runners

Additive changes in `tools/agreement/` (research R15):

| File | Change |
| --- | --- |
| `lib/runtime.mjs` | `bootRuntime(url, { jiterpreter, assembly, exportsPath, engineGlue })`; the defaults are 015's assembly and exports path and no glue; with `engineGlue`, it imports the glue, awaits `instantiate`, and calls `runtime.setModuleImports('mmo-vehicle-engine', imports)` before returning; `readRecords(bytes, layout = 1)` reads layout 2 records too |
| `lib/server.mjs` | `GET /engine/*` from `<app>/engine/`; `WORKER_BUILDS` adds `chromium-worker-relinked` and `chromium-worker-aot`; the PUT rule also accepts `<stream>.windows` and `<stream>.windows.error.txt` |
| `node/agreement.test.mjs` | `NODE_BUILDS` adds `node-interp-relinked-nojit`; with a manifest version 2 it boots the manifest's host assembly and glue, runs `RunStream` per stream, the batch check, the perturbation check, the refusal checks and the windowed runs (`<build>/<stream>.windows`) |
| `specs/worker.spec.mjs` | the Worker build name is `chromium-worker`, `chromium-worker-relinked` or `chromium-worker-aot` from `AGREEMENT_MODE` (`interp-prebuilt` when unset); with a manifest version 2, the same checks as Node |
| `web/worker.mjs` | keeps `addEventListener('message', …)` and never sets `self.onmessage` before importing `dotnet.js` |
| `lib/measure-vehicles.mjs` (new) | the vehicle measurement: download, cold starts and step costs per candidate and mode ([selection-report.md](selection-report.md#measurements)) |

A `.windows` file is, per window in order, `u32 startStep | u32 recordCount | records (layout 2)`.

## Engine identity

`simcore/vehicles/engines/<engine>.json`, one per engine (`bepu`, `jolt`, `rapier`, `fixture`):

```json
{
  "formatVersion": 1,
  "engine": "jolt",
  "release": "v5.7.0",
  "adapterRevision": 1,
  "sources": {
    "repository": "https://github.com/jrouwe/JoltPhysics",
    "commit": "cf16769bb0ede9c45d57d06068ff0689e9702f5c",
    "tree": "47f0b9a2d9a830c64c3a729b372e0c951a4c1daa",
    "paths": ["Jolt/", "Build/CMakeLists.txt", "LICENSE"]
  },
  "toolchains": {
    "nativeCompiler": "clang version 18.1.3",
    "emscripten": {
      "version": "3.1.56",
      "packages": [
        { "id": "Microsoft.NET.Runtime.Emscripten.3.1.56.Sdk.linux-x64", "version": "10.0.15", "sha512": "<from the restore>" },
        { "id": "Microsoft.NET.Runtime.Emscripten.3.1.56.Cache.linux-x64", "version": "10.0.15", "sha512": "<from the restore>" },
        { "id": "Microsoft.NET.Runtime.Emscripten.3.1.56.Node.linux-x64", "version": "10.0.15", "sha512": "<from the restore>" }
      ]
    }
  },
  "buildSettings": {
    "cmake": ["-DCROSS_PLATFORM_DETERMINISTIC=ON", "-DCMAKE_BUILD_TYPE=Distribution", "-DOBJECT_LAYER_BITS=16"],
    "defines": ["JPH_PLATFORM_BLUE", "JPH_CROSS_PLATFORM_DETERMINISTIC", "JPH_OBJECT_LAYER_BITS=16"],
    "flags": ["-ffp-contract=off", "-fno-exceptions", "-fno-rtti"],
    "browserFlags": ["-O3", "-msimd128", "-sMALLOC=emmalloc", "-sALLOW_MEMORY_GROWTH=0"]
  },
  "engineSettings": { "numVelocitySteps": 10, "numPositionSteps": 2, "penetrationSlop": 0.01, "constraintWarmStart": false, "bodyPairContactCache": false },
  "historyFreeMode": "persistent-bodies-state-written-each-substep",
  "nativeIdentity": "jolt v5.7.0 cpd=1 blue=1 fpcontract=off obj16 emmalloc",
  "identityInputs": [
    "src/MmoEngine.SimCore.Vehicles.Jolt/native/build.sh",
    "src/MmoEngine.SimCore.Vehicles.Jolt/native/CMakeLists.txt",
    "src/MmoEngine.SimCore.Vehicles.Jolt/native/include/Jolt/Core/PlatformBlue.h"
  ]
}
```

The block above is illustrative: the `sha512` values are written by the implementing task from the
restore's `.nupkg.sha512` files. Bepu's pin file names `BepuPhysics` and `BepuUtilities` 2.5.0-beta.33
with their nupkg SHA-256s, its `packages.lock.json` as an identity input, its engine settings, its history-free
mode and `vectorPin: "DOTNET_PreferredVectorBitWidth=128"`. The fixture's names its fault and its model
settings.

**Fingerprint** (`EngineIdentityFingerprint.Compute`, `src/MmoEngine.SimCore.Vehicles.Server/`):
XxHash64 (seed 0) over the pin file's canonical JSON (members in file order, no whitespace, LF) followed,
for each path in `identityInputs` in order, by the path's UTF-8 bytes, a zero byte and the file's SHA-256.
`simcore vehicles identity --engine <engine>` prints `<engine> <release> <fingerprint>`; CI uses it as
the cache key and the selection checks every built artifact's recorded fingerprint against it.

## Analyzer

The determinism analyzer (015's MMO1001–MMO1009) is attached to every adapter project and to the fixture
engine with 015's wiring (`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`), every rule at
Error, with one declared exemption per adapter (FR-004). The exemption is an MSBuild property in the
adapter's project file, `<MmoDeterminismExemption>`, made visible to the analyzer with
`<CompilerVisibleProperty Include="MmoDeterminismExemption" />` and read from
`build_property.MmoDeterminismExemption`. Its value names one rule and the members it admits, for example
`MMO1008:interop`. 015's suppression rules still hold: an exemption is not a suppression, `.editorconfig`
severities and `[SuppressMessage]` still hide nothing from the backstop, and the core and the fixture
declare none (an architecture test reads every project file and allows the property only in the three
adapters, with exactly these values).

| Project | `MmoDeterminismExemption` | Members admitted |
| --- | --- | --- |
| `MmoEngine.SimCore.Vehicles.Fixture` | none | |
| `MmoEngine.SimCore.Vehicles.Bepu` | `MMO1001:system-numerics` | `System.Numerics.Vector3`, `Quaternion`, `Vector<T>` and the BepuUtilities wide types in Bepu's API and callbacks |
| `MmoEngine.SimCore.Vehicles.Jolt`, `.Rapier` | `MMO1008:interop` | `extern` and `[LibraryImport]` declarations, `NativeLibrary`, `Marshal`, `OperatingSystem.IsBrowser()` in the binding factory, and in the browser build `[JSImport]` declarations with the generator's `JSFunctionBinding` and `JSMarshalerArgument` code |

MMO1006 (locks) is never exempt. The analyzer's backstop test compiles each adapter's sources and
requires zero diagnostics outside its declared exemption, suppressed ones included
(`AdapterBackstopTests`, `tests/MmoEngine.SimCore.Analyzers.Tests/`). The wasm hosts stay outside the
analyzer, as 015's host does.
