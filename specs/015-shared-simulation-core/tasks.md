---
description: "Task list for the shared simulation core (feature 015)"
---

# Tasks: Shared Simulation Core

**Input**: Design documents from `specs/015-shared-simulation-core/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, quickstart.md, contracts/ (analyzer-rules, collision-data-format, config-rules, input-command-format, measurement-report, recorded-stream-format, simcore-cli, state-serialization, wasm-api)

**Tests**: Tests are REQUIRED. Per the constitution (Principles VIII and IX), every acceptance
scenario maps to an automated test, tests are written first and must fail before implementation,
and the completion gate requires every applicable suite to pass. Every acceptance scenario and
Independent Test of spec.md is mapped to its task and test in [Scenario coverage](#scenario-coverage).

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story. User Stories 2 and 7 are split across the two delivery slices (plan.md, Delivery).

**Autonomous delivery (constitution Principle XIII)**: Every task is executed by the agent. No
task, checkpoint or checklist item assigns work to a person or waits on manual verification,
review, approval or sign-off. A checkpoint is passed when its automated checks are green; the
guarded-path review in Phase 13 is the automated subagent review of `.routines/merge-policy.md`.

**Delivery** (plan.md): slice 1 is Phases 1-10 on `015-shared-simulation-core`, one PR touching no
guarded path; slice 2 is Phases 11-13 on `015-shared-simulation-core-slice-2`, cut from `main`
after slice 1 merges. See [Delivery](#delivery) for the task ranges.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an unfinished task in the phase). Green tasks that edit `src/MmoEngine.SimCore/World/SimWorld.cs`, `src/MmoEngine.SimCore.Cli/Program.cs` or `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs` are never [P].
- **[Story]**: Which user story this task belongs to (US1 to US7). Setup, Foundational and Polish tasks carry no story label.
- Every task names its exact repo-relative file paths. Red tasks name the test (`Class.Method`), its file, the scenario it covers and the command that shows it failing; green tasks name the red tasks they turn green.

## Path Conventions

- Core: `src/MmoEngine.SimCore/` (namespaces equal folders: `Math`, `Geometry`, `Collision`, `Movement`, `Weapons`, `Projectiles`, `Hitboxes`, `Inputs`, `Formats`, `Config`, `World`, `Batch`, `Hashing`); BCL and `System.IO.Hashing` only.
- Analyzer: `src/MmoEngine.SimCore.Analyzers/` (netstandard2.0), attached to the core only.
- Server helpers: `src/MmoEngine.SimCore.Server/`; CLI `simcore`: `src/MmoEngine.SimCore.Cli/`.
- Tests: `tests/MmoEngine.SimCore.Tests/<Folder>/<Subject>Tests.cs`, `tests/MmoEngine.SimCore.Analyzers.Tests/`; benchmarks: `benchmarks/MmoEngine.SimCore.Benchmarks/`.
- Slice 2 only: `src/MmoEngine.SimCore.Wasm/`, `tests/MmoEngine.SimCore.Wasm.Tests/`, `tools/agreement/`, `.github/workflows/simcore.yml`, `tests/workflows/test_simcore_workflow.py`.
- Committed content: `simcore/config/`, `simcore/content/`, `simcore/streams/`, `simcore/golden/`. Tests open it read-only; only the `simcore` writing commands change it.

---

## Phase 1: Setup (Shared Infrastructure)

**Goal**: Add the slice-1 projects, the central package versions, the analyzer project skeleton attached to the core, the binary attributes and the test support every later phase uses, so the solution builds and formats clean with the six new projects in it.

**Independent Test**: `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes` exit 0 with the six new projects in the solution, and `git check-attr binary simcore/content/x.mmcd simcore/streams/x.mmrs simcore/golden/input/x.bin` reports `binary: set` for each path.

### Tests (write first, confirm red)

This phase owns no acceptance scenario and adds no xUnit test. Its checks are the build, format and attribute commands above. The wiring it creates is tested later: `Architecture/CoreDependencyTests` in Phase 2, and `WiringTests.CoreProjectAttachesTheAnalyzerPrivately`, `WiringTests.OnlyTheCoreAttachesTheAnalyzer` and `WiringTests.RoslynPinMatchesTheSdkBand` in Phase 5.

- [ ] T001 Before any change, confirm the phase's checks are red. Run `git check-attr binary simcore/content/x.mmcd simcore/streams/x.mmrs simcore/golden/input/x.bin` and confirm that each of the three lines ends in `binary: unspecified`. Run `dotnet build tests/MmoEngine.SimCore.Tests/MmoEngine.SimCore.Tests.csproj` and `dotnet build tests/MmoEngine.SimCore.Analyzers.Tests/MmoEngine.SimCore.Analyzers.Tests.csproj` and confirm that both exit non-zero because the project files do not exist. Run `dotnet sln MmoEngine.sln list` and confirm that none of the six new project paths appears.

### Implementation

- [ ] T002 [P] Add the two new package versions to `Directory.Packages.props`, inside the existing `<ItemGroup>` and after the Spec 014 block. Add a comment `<!-- Spec 015 (specs/015-shared-simulation-core/research.md R5): the determinism analyzer's Roslyn, pinned to the SDK 10.0.1xx compiler (contracts/analyzer-rules.md#roslyn-pin), and reference assemblies for the analyzer tests. Both MIT. -->`, then add `<PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="5.0.0" />` and `<PackageVersion Include="Basic.Reference.Assemblies.Net100" Version="1.8.14" />`. Write each element on one line with `Include` before `Version`, because Phase 5's `RoslynPinMatchesTheSdkBand` reads that line. Extend the file's top license comment so it also cites `specs/015-shared-simulation-core/research.md (R5)`. Change no existing version. Add nothing else: System.IO.Hashing, FluentValidation, System.CommandLine, Serilog, Serilog.Formatting.Compact, xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk, CsCheck, coverlet.collector and BenchmarkDotNet are already pinned.
- [ ] T003 [P] Create `.gitattributes` at the repository root. The first line is a comment: `# Binary content of the shared simulation core (specs/015-shared-simulation-core): no line-ending conversion, no text diff or merge.` Then add exactly three rules, `*.mmcd binary`, `*.mmrs binary` and `simcore/golden/**/*.bin binary`, and end the file with a newline (per `.editorconfig`). Golden `.json` files and `simcore/streams/REGENERATIONS.md` stay text, so add no rule for them.
- [ ] T004 Create the analyzer project skeleton in `src/MmoEngine.SimCore.Analyzers/` (depends on T002, because it uses the Roslyn pin).
  - `src/MmoEngine.SimCore.Analyzers/MmoEngine.SimCore.Analyzers.csproj`: write it exactly as the project XML in contracts/analyzer-rules.md#wiring. That is `Sdk="Microsoft.NET.Sdk"`, with `TargetFramework` netstandard2.0 (overriding `Directory.Build.props`'s net10.0), `ImplicitUsings` disable, `EnforceExtendedAnalyzerRules` true, `IsRoslynComponent` true and `IsPackable` false. Add `<PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />` with no version (central management), and `<AdditionalFiles Include="AnalyzerReleases.Shipped.md" />` and `<AdditionalFiles Include="AnalyzerReleases.Unshipped.md" />`. Set nothing that disables analyzers or warnings. `LangVersion`, `Nullable` and `TreatWarningsAsErrors` come from `Directory.Build.props`.
  - `src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Shipped.md`: only the header lines of contracts/analyzer-rules.md#release-tracking: `; Shipped analyzer releases` and the `; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md` line.
  - `src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Unshipped.md`: only `; Unshipped analyzer release` and the same help-link line. Phase 5 adds the `### New Rules` table for MMO1001–MMO1009.
  - `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs`: explicit usings `System.Collections.Immutable`, `Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.Diagnostics`, and a file-scoped `namespace MmoEngine.SimCore.Analyzers;`. Declare `[DiagnosticAnalyzer(LanguageNames.CSharp)] public sealed class DeterminismAnalyzer : DiagnosticAnalyzer`. `SupportedDiagnostics` returns `ImmutableArray<DiagnosticDescriptor>.Empty`. `Initialize(AnalysisContext context)` calls only `context.EnableConcurrentExecution()` and `context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics)`. Add no descriptor, no registration and no null check. Phase 5 adds the rules, `Descriptors.cs`, `Tables.cs` and every analyzer test. Add an XML summary citing `specs/015-shared-simulation-core/contracts/analyzer-rules.md`.
- [ ] T005 Create `src/MmoEngine.SimCore/MmoEngine.SimCore.csproj` with no source files (Phase 2 adds them), after T004.
  - Use `Sdk="Microsoft.NET.Sdk"` and a comment saying the core references only the BCL and System.IO.Hashing, and that Phase 2's `Architecture/CoreDependencyTests` enforces this.
  - `PropertyGroup`: `ImplicitUsings` disable, `AllowUnsafeBlocks` false, `IsTrimmable` true, `IsAotCompatible` true.
  - `<PackageReference Include="System.IO.Hashing" />` is its only package reference.
  - Attach the analyzer exactly as in contracts/analyzer-rules.md#wiring: `<ProjectReference Include="..\MmoEngine.SimCore.Analyzers\MmoEngine.SimCore.Analyzers.csproj" OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />`.
  - `<InternalsVisibleTo Include="MmoEngine.SimCore.Tests" />`.
  - The file must not contain `RunAnalyzers`, `RunAnalyzersDuringBuild`, `NoWarn`, `WarningsNotAsErrors` or any `<Compile Remove>`, because Phase 5's `WiringTests.CoreProjectAttachesTheAnalyzerPrivately` asserts their absence.
- [ ] T006 Create `src/MmoEngine.SimCore.Server/MmoEngine.SimCore.Server.csproj` with no source files, after T005. Use `Sdk="Microsoft.NET.Sdk"`, add `<ProjectReference Include="..\MmoEngine.SimCore\MmoEngine.SimCore.csproj" />` and `<PackageReference Include="FluentValidation" />`, and add `<InternalsVisibleTo Include="MmoEngine.SimCore.Tests" />`. It keeps the repository defaults (`ImplicitUsings` enable) and does not attach the analyzer. Add a comment saying this project holds the server-only helpers the continent server will reuse: JSON binding, the FluentValidation adapter, the environment check, the generators and the divergence report (plan.md, Structure Decision).
- [ ] T007 Create the CLI shell, after T005 and T006.
  - `src/MmoEngine.SimCore.Cli/MmoEngine.SimCore.Cli.csproj`, following `src/MmoEngine.Harness.Cli/MmoEngine.Harness.Cli.csproj`: `OutputType` Exe, `AssemblyName` simcore, `RootNamespace` MmoEngine.SimCore.Cli. Add ProjectReferences to `..\MmoEngine.SimCore\MmoEngine.SimCore.csproj` and `..\MmoEngine.SimCore.Server\MmoEngine.SimCore.Server.csproj`, PackageReferences `System.CommandLine`, `Serilog` and `Serilog.Formatting.Compact`, and `<InternalsVisibleTo Include="MmoEngine.SimCore.Tests" />`. It does not attach the analyzer.
  - `src/MmoEngine.SimCore.Cli/Program.cs`, with `using System.CommandLine;` and file-scoped `namespace MmoEngine.SimCore.Cli;`. Declare `public static class Program` with `public static int Main(string[] args) => Run(args, Console.Out, Console.Error);` as a one-line delegation, and `public static int Run(string[] args, TextWriter stdout, TextWriter stderr)`.
  - `Run` calls `ArgumentNullException.ThrowIfNull` on each argument, builds an empty `new RootCommand("simcore: the shared simulation core's tools (specs/015-shared-simulation-core/contracts/simcore-cli.md).")` with no subcommands, and returns `root.Parse(args).Invoke(new InvocationConfiguration { Output = stdout, Error = stderr })`.
  - Phase 2 adds Serilog compact logging with `Tool=simcore`, the `DOTNET_ENVIRONMENT` check, `CliExitCodes` and `config check`. Later phases register their commands here.
- [ ] T008 Create `tests/MmoEngine.SimCore.Tests/MmoEngine.SimCore.Tests.csproj`, after T007. `Directory.Build.props` makes it a VSTest test project because its name ends in `.Tests`.
  - ProjectReferences: `..\..\src\MmoEngine.SimCore\MmoEngine.SimCore.csproj`, `..\..\src\MmoEngine.SimCore.Server\MmoEngine.SimCore.Server.csproj`, `..\..\src\MmoEngine.SimCore.Cli\MmoEngine.SimCore.Cli.csproj`, and `..\..\src\MmoEngine.Regionizer.Simulator\MmoEngine.Regionizer.Simulator.csproj`. Comment the last one as test-only, for Phase 2's `Cli/EnvironmentTests.KnownEnvironmentsEqualTheSimulators`, which compares `SimEnvironment`'s allowed names with `MmoEngine.Regionizer.Simulator.Config.EnvironmentNames.Allowed`.
  - PackageReferences: `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `CsCheck`, `coverlet.collector`.
  - It does not attach the analyzer. BenchmarkDotNet, the benchmark project reference and the `Cli/Fixtures` content item are Phase 9's.
- [ ] T009 Add the shared test support in `tests/MmoEngine.SimCore.Tests/Support/`, after T008. Every file uses a file-scoped `namespace MmoEngine.SimCore.Tests.Support;` with usings outside the namespace, System first, to satisfy `.editorconfig` and `dotnet format`.
  - `GlobalUsings.cs`: `global using Xunit;`.
  - `RepoPaths.cs`: `public static class RepoPaths`.
    - `Root` walks up from `AppContext.BaseDirectory` to the first directory that holds `MmoEngine.sln`, resolved once through a `Lazy<string>`. If none is found, it throws `DirectoryNotFoundException` with the message `No directory holding MmoEngine.sln above <start>.`.
    - `Combine(params string[] parts)` returns an absolute path under `Root`, and `SimCore` returns `Combine("simcore")`.
    - `OpenRead(params string[] parts)` returns `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)`. `ReadAllBytes(params string[] parts)` and `ReadAllText(params string[] parts)` (UTF-8) read through `OpenRead`, so a test never opens a repository file for writing.
    - An XML summary states that tests open `simcore/` read-only, never write under it, and never rely on the current directory.
  - `TestRates.cs`: `public static class TestRates`.
    - `public static IReadOnlyList<int> Hz { get; } = [20, 30, 60];` and `public static TheoryData<int> All => [20, 30, 60];`, used as `[Theory] [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]` with a parameter named `hz`.
    - `public static TheoryData<int, T> With<T>(params T[] values)` gives rows `(hz, value)`.
    - `public static TheoryData<T, int> WithRateLast<T>(params T[] values)` gives rows `(value, hz)`.
    - `public static TheoryData<T1, T2, int> WithRateLast<T1, T2>(params (T1, T2)[] rows)` gives rows `(a, b, hz)`.
    - In all three, rows come in the order of the values given, each crossed with 20, 30 and 60 in that order. Each call returns a new instance. Each throws `ArgumentNullException` for null and `ArgumentException` for an empty list.
    - An XML summary shows the intended use, a test-class property such as `public static TheoryData<InputRejection, int> Cases => TestRates.WithRateLast(...)` passed to `[MemberData(nameof(Cases))]`, and says that data variations are Theories, never loops inside a Fact.
  - `TempDirectories.cs`: `public static class TempDirectories`.
    - `Create(string prefix)` returns a new empty directory `Path.Combine(Path.GetTempPath(), "mmo-simcore-tests", $"{prefix}-{Guid.NewGuid():N}")`.
    - `CopyOf(string sourceDirectory, string prefix)` copies files and subdirectories recursively in ordinal order. It reads each source file with `FileAccess.Read`, so tests of writing commands work on copies of `simcore/` folders.
    - Both return a `public sealed class TempDirectory : IDisposable`, declared in the same file, with `Path` and `Combine(params string[] parts)`. `Dispose` calls `Directory.Delete(Path, recursive: true)` as a best-effort cleanup that catches only `IOException` and `UnauthorizedAccessException`.
    - The directories are unique per call, so tests stay parallel-safe and rerun-safe.
- [ ] T010 Create `tests/MmoEngine.SimCore.Analyzers.Tests/MmoEngine.SimCore.Analyzers.Tests.csproj`, after T002 and T004.
  - Add `<ProjectReference Include="..\..\src\MmoEngine.SimCore.Analyzers\MmoEngine.SimCore.Analyzers.csproj" />` as a normal reference with no `OutputItemType`. Tests instantiate `DeterminismAnalyzer` through it, and `MmoEngine.SimCore.Analyzers.dll` is copied to the test output for Phase 5's `BuildTests.ABuildWithAViolationFails`. The analyzer does not run on the test code (contracts/analyzer-rules.md#wiring).
  - PackageReferences: `Microsoft.CodeAnalysis.CSharp`, `Basic.Reference.Assemblies.Net100`, `System.IO.Hashing` (comment: the backstop compile's metadata reference, Phase 5), `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`, `coverlet.collector`.
  - Add no source files. Phase 5 adds `Support/AnalyzerHarness.cs` and the test classes.
- [ ] T011 Add the six new projects to `MmoEngine.sln` under the existing solution folders, after every project-file task above.
  - Run `dotnet sln MmoEngine.sln add --solution-folder src src/MmoEngine.SimCore/MmoEngine.SimCore.csproj src/MmoEngine.SimCore.Analyzers/MmoEngine.SimCore.Analyzers.csproj src/MmoEngine.SimCore.Server/MmoEngine.SimCore.Server.csproj src/MmoEngine.SimCore.Cli/MmoEngine.SimCore.Cli.csproj`.
  - Run `dotnet sln MmoEngine.sln add --solution-folder tests tests/MmoEngine.SimCore.Tests/MmoEngine.SimCore.Tests.csproj tests/MmoEngine.SimCore.Analyzers.Tests/MmoEngine.SimCore.Analyzers.Tests.csproj`.
  - Confirm that `dotnet sln MmoEngine.sln list` prints all six paths. In `GlobalSection(NestedProjects)`, the four src GUIDs map to the existing `src` folder `{827E0CD3-B72D-47B6-A68D-7590B98EB39B}` and the two test GUIDs to the existing `tests` folder `{0AB3BF05-4346-4AA6-1389-037BE0695223}`, and no second `src` or `tests` folder entry exists.
  - Do NOT add `benchmarks/MmoEngine.SimCore.Benchmarks` (Phase 9) or any WebAssembly project (Phase 11).
- [ ] T012 Turn the red checks green. Run `dotnet restore MmoEngine.sln`, `dotnet build MmoEngine.sln -warnaserror --no-restore` and `dotnet format MmoEngine.sln --verify-no-changes --no-restore`, and confirm that each exits 0. The build output must contain no `CS9057` or `CS8032` line, which would show that the core did not load the analyzer (contracts/analyzer-rules.md#roslyn-pin). Rerun `git check-attr binary simcore/content/x.mmcd simcore/streams/x.mmrs simcore/golden/input/x.bin` and confirm that all three lines end in `binary: set`.
  - Then run `git diff --name-only` and `git status --porcelain`. Every changed or added path must be under `src/MmoEngine.SimCore*`, `tests/MmoEngine.SimCore*`, `Directory.Packages.props`, `.gitattributes` or `MmoEngine.sln`, and none under `.github/`, `.routines/`, `.claude/`, `infra/`, `CLAUDE.md`, `docs/architecture/` or `simcore/`.
  - Fix any formatting or build failure in the file that causes it. Never add a `NoWarn`, suppression or `<Compile Remove>`.

**Checkpoint**: These checks exit 0: `dotnet build MmoEngine.sln -warnaserror` (no `CS9057` or `CS8032`), `dotnet format MmoEngine.sln --verify-no-changes`, `git check-attr binary` (`set` for the `.mmcd`, `.mmrs` and `simcore/golden` `.bin` paths) and `dotnet sln MmoEngine.sln list` (all six new projects). `src/MmoEngine.SimCore.Cli/Program.cs` and `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs` gain their coverage in Phase 2 (`Cli/EnvironmentTests`, `Cli/ConfigCommandTests`) and Phase 5 (analyzer tests). `tools/coverage_gate.py` is checked for the whole slice in Phase 10.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Goal**: Build what every story needs and nothing a single story owns: SimMath and its coefficient generators, Vec3, the reason and outcome codes, FormatError, the configuration rule table with its compiler and config block, the server config adapter, the CLI shell with `config check`, the input codec, the canonical state serializer, StateHash and StepRecord, the collision file frame and world build, the recorded-stream frame, the hitbox builder, and the SimWorld step skeleton (stages 1, 4, 6 and 7 real; stages 2, 3 and 5 copy state forward unchanged). User story phases start once this checkpoint is green.

**Independent Test**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Architecture|FullyQualifiedName~MmoEngine.SimCore.Tests.Math.SimMathTests|FullyQualifiedName~MmoEngine.SimCore.Tests.Config|FullyQualifiedName~MmoEngine.SimCore.Tests.Formats|FullyQualifiedName~MmoEngine.SimCore.Tests.World|FullyQualifiedName~MmoEngine.SimCore.Tests.Collision.CollisionWorldBuildTests|FullyQualifiedName~MmoEngine.SimCore.Tests.Hitboxes|FullyQualifiedName~MmoEngine.SimCore.Tests.Inputs.InputCodecTests"` passes: the worked-example state hashes to `35038da2f2d9459c`, `tiny-world` encodes to 188 bytes with checksum `9224e3fa3b3fca9c`, `simcore/config/default.json` compiles to its pinned bits at 20, 30 and 60 Hz, and a SimWorld steps, canonicalises −0 and refuses non-finite fields.

### Tests (write first, confirm red)

Every test below is in `tests/MmoEngine.SimCore.Tests/` (namespace `MmoEngine.SimCore.Tests.<Folder>`), one class per file. A compile failure because the type under test does not exist yet counts as red. Tests open everything under `simcore/` read-only through `Support/RepoPaths` and write only to `Support/TempDirectories`. Inside `MmoEngine.SimCore.Tests.Math`, write `System.Math` and `System.MathF` fully qualified.

- [ ] T013 Add the shared test support in `tests/MmoEngine.SimCore.Tests/Support/`. Every later test task in this phase uses it.
  - `TestConfigs.cs`: `Default()` binds `simcore/config/default.json` through `SimConfigJson` (read-only via `RepoPaths`) and returns a fresh `SimConfig` each call. `At(int hz)` is a copy with `stepRateHz = hz`. `With(Action<SimConfig> edit)` applies an override to a copy. `Compiled(int hz)` returns `CompiledConfig.Create(At(hz))`.
  - `TestWorlds.cs` builds `CollisionData` values. `TinyWorld()` is exactly the `tiny-world` table of contracts/collision-data-format.md#tiny-world, with `heights[8]` as negative zero (`BitConverter.UInt32BitsToSingle(0x80000000)`). `HeightfieldOnly()` is the same heightfield with no meshes. `Flat(int samples = 65, float cellSize = 1f, float gridCellSize = 4f)` is all heights 0 from origin (−32, −32), so the playable area is [−32, 32] in X and Z. `Load(CollisionData)` returns `CollisionFile.Decode(CollisionFile.Encode(data))`.
  - `TestStates.cs` holds canonical states. `WorkedExample` is the 136 bytes of contracts/state-serialization.md#worked-example, as a hex literal. `Build(...)` writes a state with `BinaryPrimitives` at the contract's offsets, independent of `StateSerializer`, from character, weapon, projectile and event descriptions. The named states are:
    - `StandingOnFlat`: character 1 at (0.5, 0, 0.5), grounded, normal (0, 1, 0), the placeholder weapons;
    - `TwoCharacters`: IDs 1 and 4;
    - `WithFlyingProjectile`: one character plus projectile 1 of class 0. Its origin and position are (0, 50, 0), its launch velocity is (0, 1, 0), and its `launchStep` equals the state's step, so later phases' projectile step keeps it in flight for one step;
    - a `Mutate(byte[] state, int offset, ReadOnlySpan<byte> bytes)` helper.
  - `SimcoreCli.cs`: `Run(params string[] args)` returns `(int Exit, string Stdout, string Stderr)`. It calls `Program.Run(args, stdout, stderr, environmentName: "Development")` in process with `StringWriter`s. Callers pass paths rooted at `RepoPaths.Root`.
- [ ] T014 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Architecture/CoreDependencyTests.cs`, in the style of `tests/MmoEngine.Regionizer.Tests/Architecture/CoreDependencyTests.cs`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Architecture.CoreDependencyTests"` and confirm they fail.
  - `CoreDependencyTests.CoreReferencesOnlyTheBclAndSystemIOHashing`:
    - Given `typeof(SimCoreVersion).Assembly`, assert that every referenced assembly loads from the shared-framework directory, except exactly one, `System.IO.Hashing`.
    - Assert that no referenced name starts with `MmoEngine`.
    - Assert that `src/MmoEngine.SimCore/MmoEngine.SimCore.csproj` has exactly one `PackageReference`, `System.IO.Hashing`.
  - `CoreDependencyTests.CoreProjectKeepsTheDeterminismSettings`: parse the csproj with `XDocument` and assert:
    - `IsTrimmable` `true`, `IsAotCompatible` `true`, `AllowUnsafeBlocks` `false` and `ImplicitUsings` `disable`;
    - exactly one `ProjectReference` to `..\MmoEngine.SimCore.Analyzers\MmoEngine.SimCore.Analyzers.csproj`, with `OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"` and `PrivateAssets="all"` (contracts/analyzer-rules.md#wiring);
    - `InternalsVisibleTo` `MmoEngine.SimCore.Tests`.
- [ ] T015 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Architecture/CoreStateTests.cs` (FR-001, FR-015, data-model.md#ownership). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Architecture.CoreStateTests"` and confirm they fail.
  - `CoreStateTests.CoreHasNoStaticMutableFields`: reflect over every type of the core assembly, skipping compiler-generated types and fields. Assert that every static field is a literal (`const`) or `initonly`, and that the type of each `initonly` one is one of:
    - a primitive, an enum, `string` or a readonly struct;
    - `System.Collections.Immutable.ImmutableArray<T>`;
    - a sealed type whose instance fields are all `initonly`.

    Arrays are refused. The failure message lists every offending `Type.Field`.
  - `CoreStateTests.NoCoreMethodTakesATimeSpanOrAFrameTime`: no public or internal method or constructor of the core has a parameter of type `TimeSpan`, `DateTime`, `DateTimeOffset` or `Stopwatch`. No `float` or `double` parameter is named `dt`, `deltaTime`, `frameTime`, `elapsed` or `elapsedSeconds`, ignoring case. The step length comes only from `CompiledConfig` (FR-015).
- [ ] T016 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Math/SimMathTests.cs` (FR-013, research R3, R4). Assert exact bits through `BitConverter.SingleToUInt32Bits`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Math.SimMathTests"` and confirm they fail.
  - `NonFiniteInputsReturnNaN(string function, uint inputBits)`, Theory. Each row asserts `float.IsNaN` on the result, and on both outputs for `SinCos`:
    - NaN (`0x7FC00000`) for Sin, Cos, Tan, SinCos, Asin, Acos, Atan, Sqrt, and Atan2 in either argument;
    - +∞ and −∞ for Sin, Cos, Tan and SinCos;
    - Asin and Acos at `0x3F800001`, `0xBF800001`, 2 and −2;
    - Sqrt(−1).

    Two extra rows check that `Atan(+∞)` has bits `0x3FC90FDB` and `Atan(−∞)` has bits `0xBFC90FDB` (finite ±π/2, contracts/wasm-api.md#cross-build-tables).
  - `SignedZerosAndSubnormalsFollowTheContract`, Theory:
    - Sin, Tan, Asin, Atan and Sqrt of +0 return `0x00000000`, and of −0 return `0x80000000`.
    - Cos(±0) is `0x3F800000`, and Acos(0) is `0x3FC90FDB`.
    - For the subnormals `0x00000001` and `0x007FFFFF`, Sin, Tan, Asin and Atan return the input's bits.
    - Atan2 follows C99 Annex F: (+0, +0) gives `0x00000000`; (−0, +0) gives `0x80000000`; (+0, −0) and (+0, −1) give `0x40490FDB`; (−0, −0) and (−0, −1) give `0xC0490FDB`.
  - `SaturatingConversionsClampNaNAndOutOfRange(string conversion, uint inputBits, int expected)`, Theory over FloorToInt, RoundToInt and TruncateToInt (research R4):
    - NaN gives 0;
    - `3e9f`, `0x4F000000` (2³¹) and +∞ give `int.MaxValue`;
    - `-3e9f`, `0xCF000000` and −∞ give `int.MinValue`;
    - `0x4EFFFFFF` gives 2147483520.

    Further rows:
    - RoundToInt ties to even: 0.5f gives 0, 1.5f gives 2, 2.5f gives 2, 3.5f gives 4, −2.5f gives −2, and `0.49999997f` (`0x3EFFFFFF`) gives 0;
    - FloorToInt(−1.5f) gives −2, and FloorToInt(−0f) gives 0;
    - TruncateToInt(−1.5f) gives −1, and TruncateToInt(`0x3FFFFFFF`) gives 1.
  - `MinMaxAreCompareSelects`. Min is `a < b ? a : b` and Max is `a > b ? a : b`:
    - Min(+0, −0) returns `0x80000000`, and Min(−0, +0) returns `0x00000000`;
    - Min(NaN `0x7FC00001`, 1) returns 1, and Min(1, NaN `0x7FC00001`) returns `0x7FC00001`;
    - Max mirrors these;
    - Clamp(5, 0, 1) is 1, Clamp(−5, 0, 1) is 0, and Clamp(0.5f, 0, 1) is 0.5f.
  - `SinCosBinaryAngleIsExactAtTheQuadrants`:
    - Angles 0, 16384, 32768 and 49152 give (0, 1), (1, 0), (0, −1) and (−1, 0). The ±1 values are exactly `0x3F800000` and `0xBF800000`, and the zeros compare equal to `0f`.
    - Angle 8192 gives sine bits equal to cosine bits, within 1 ulp of √½.
    - For every one of the 65,536 angles a, `SinCosBinaryAngle((ushort)(a + 16384))` is bit for bit (cos a, −sin a), so the integer quadrant reduction is exact.
  - `CoefficientsHaveTheirPinnedBits(string name, uint bits)`, Theory over every `const float` of `SimMathCoefficients` and every word of `TwoOverPi.Words`.
    - The coefficient rows are the `name 0xXXXXXXXX` lines that `tools/simcore/emit_cs.py --list` prints; the generator task T041 adds them.
    - The 2/π rows are the ten words `A2F9836E 4E441529 FC2757D1 F534DDC0 DB629599 3C439041 FE5163AB DEBBC561 B7246E3A 424DD2E0`, most significant first.
    - Assert by reflection that the constant's bits equal the row's.
  - `EachReductionPathMeetsItsBoundAtSampleInputs(string function, uint xBits, uint yBits)`, Theory over these rows:
    - Sin, Cos, Tan and SinCos at 0.5f, π/4 (`0x3F490FDB`) and both its neighbours, and 100f (Cody–Waite reduction);
    - the same functions at 12867f and both its neighbours, 12868f, 1e6f, 1e20f and `float.MaxValue` (Payne–Hanek reduction);
    - Asin and Acos at 0.25f, 0.5f and both its neighbours, 0.9f, 1f and −1f;
    - Atan at 7/16, 11/16, 19/16 and 39/16 and their neighbours, and at 1e10f;
    - Atan2 at (1, 1), (−1, −1), (1e-30f, −1) and (3, −4).

    Each row asserts an error against the `System.Math` double reference within research R3's bound, measured in ulps of the reference rounded to float: Sin and Cos 1 ulp for |x| ≤ 12867 and 2 above; Tan 1.5 and 2.5; Asin, Acos and Atan 1; Atan2 2.
- [ ] T017 [P] Write failing test `Vec3Tests.OperationsUseTheContractsParenthesisation` in `tests/MmoEngine.SimCore.Tests/Math/Vec3Tests.cs`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~Vec3Tests"` and confirm it fails.
  - `Vec3.Dot((1f, 1e8f, -1e8f), (1f, 1f, 1f))` has bits `0x00000000`. The contract's `((x·x) + (y·y)) + (z·z)` gives 0, where the other association would give 1.
  - `Cross((1, 0, 0), (0, 1, 0))` is (0, 0, 1) and `Cross((0, 1, 0), (1, 0, 0))` is (0, 0, −1), each component computed as `(a.Y*b.Z) - (a.Z*b.Y)` and so on.
  - `Length((3, 4, 0))` is exactly 5f, through `SimMath.Sqrt(Dot(a, a))`.
  - `+`, `-` and scalar `*` are exact per component.
- [ ] T018 [P] Write failing test `ReasonCodeTests.EveryCodeHasItsContractValue(Type enumType, string member, long value)` in `tests/MmoEngine.SimCore.Tests/World/ReasonCodeTests.cs` (data-model.md#reason-and-outcome-codes). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ReasonCodeTests"` and confirm it fails.
  - It is a Theory with one row per member:
    - `InputRejection`: None 0, SequenceNotIncreasing 1, UnknownButtons 2, PitchOutOfRange 3, UnknownWeaponSlot 4;
    - `WeaponPhase`: Ready 0, Firing 1, Reloading 2, Switching 3, Overheated 4;
    - `FireMode`: Auto 0, Semi 1;
    - `FireBlock`: None 0, Switching 1, Reloading 2, Empty 3, Overheated 4, AwaitingRelease 5, ProjectileLimit 6;
    - `ExpiryReason`: Range 1, Lifetime 2, OutOfBounds 3;
    - `HitKind`: Terrain 0, Mesh 1, Character 2;
    - `HitboxPart`: Head 0, Torso 1, Legs 2, None 255;
    - `MovementOutcome`: None 0, HeadroomBlocked 1, Stuck 2, SlideCapReached 4, ClampedToBounds 8;
    - `Stance`: Standing 0, Crouched 1;
    - `CharacterFlags`: Grounded 1, ClampedToBounds 2;
    - `WeaponFlags`: TriggerLatched 1;
    - `StepFaultCode`: None 0, NonFinite 1, QueryCapacityExceeded 2;
    - `FormatErrorCode`: None 0, UnknownMagic 1, UnsupportedVersion 2, Truncated 3, Oversized 4, Inconsistent 5, OutOfRange 6, NonFinite 7, ChecksumMismatch 8, CollisionDataMissing 9, RulesRevisionMismatch 10;
    - `Buttons`: Forward `0x0001` through Reload `0x0100`, and Known `0x01FF`.
  - A companion Fact `EveryEnumHasExactlyTheContractMembersAndUnderlyingType` asserts each enum's member count. The underlying type is `byte` for all of them, except `Buttons`, which is `ushort` and `[Flags]`. `MovementOutcome`, `CharacterFlags` and `WeaponFlags` are also `[Flags]`.
- [ ] T019 [P] Write failing test `FormatErrorTests.MessageHasTheContractShape` in `tests/MmoEngine.SimCore.Tests/Formats/FormatErrorTests.cs` (contracts/input-command-format.md#decoding). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~FormatErrorTests"` and confirm it fails.
  - A `FormatError` with format `input`, code `Truncated`, offset 16, field `length`, and the expected-length detail of 17 and 16 has `ToString()` `input length at offset 16: 17 bytes expected, got 16`.
  - A version error at offset 1 reads `input version at offset 1: unsupported version 2; this reader supports version 1`.
  - `new CoreFormatException(error).Message` equals `error.ToString()`, and `.Error` equals the error.
  - `default(FormatError).Code` is `None`.
- [ ] T020 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Config/ConfigRuleTableTests.cs` (FR-015, FR-029, contracts/config-rules.md). The test holds its own copy of the contract's rule table, `ContractRows`: path, kind, min, max and fieldId for every one of the 116 fields of `default.json`'s instances, transcribed from config-rules.md. Every Theory row reports through both `ConfigGuard.Check` and `SimConfigValidator.Errors`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ConfigRuleTableTests"` and confirm they fail.
  - `EveryRowRefusesValuesOutsideItsRange(string path, string valueCase)`, Theory over each `ContractRows` row.
    - Cases: Int at min − 1 and max + 1; Float at `BitDecrement(min)` and `BitIncrement(max)`, plus NaN, +∞ and −∞; and the Enum row `weapons[i].fireMode` set to value 2.
    - Applied to `TestConfigs.Default()`, each case gives exactly one error with that path: `must be between {min} and {max}` with min and max in invariant round-trip form, `must be finite`, or `must be Auto or Semi`.
    - The rows include `stepRateHz` at 19 and at 61, which give exactly `stepRateHz: must be between 20 and 60` (FR-015), and `movement.gravity` at NaN, which gives `movement.gravity: must be finite`.
  - `EveryRelationRefusesItsViolationAndAcceptsItsBoundary(string relation)`, Theory over R01–R19. Each row edits the default to violate the relation and asserts exactly that relation's error, with the contract's path and message. It then edits to the boundary value and asserts no error. For example:
    - R11 at 30 Hz: `roundsPerMinute` 1801 gives `weapons[0].roundsPerMinute: must be at most weapons[0].maxShotsPerStep times 60 times stepRateHz (1800)`, and 1800 passes.
    - R10 with `input.weaponSlots` 3 gives `weapons: must have exactly input.weaponSlots (3) entries`.
    - R14 with `weapons[1].projectileClass` 2 gives `weapons[1].projectileClass: must be less than the number of projectileClasses (2)`.
    - R01 passes at `walkSpeed == crouchSpeed`.
  - `EveryConfigPropertyHasExactlyOneRowAndFieldIdsAreUnique`: reflect over `SimConfig` and its section types.
    - Every leaf property maps to exactly one `ConfigFields` row.
    - The rows are in strictly ascending fieldId, with no duplicates.
    - The rows equal `ContractRows`. With all instances counted (8 weapons, 8 projectile classes, 2 stances), there are 260 field rows.
  - `ShapeErrorsEndTheCheck`, Theory. Each row expects exactly the listed error:
    - `movement` null together with `stepRateHz` 0 gives `movement: is required`;
    - zero weapons, and nine weapons, give `weapons: must have between 1 and 8 entries`;
    - zero projectile classes gives `projectileClasses: must have between 1 and 8 entries`;
    - two standing capsules give `hitboxes.standing: must have exactly 3 entries`;
    - a null `hitboxes.standing[0].a` gives `hitboxes.standing[0].a: is required`;
    - a null `weapons[1]` gives `weapons[1]: is required`.
  - `ErrorsComeInRuleTableOrder`:
    - Field errors come in this order: `stepRateHz` 10, `movement.gravity` NaN, `weapons[0].reloadMs` −1, `weapons[1].magazineSize` 0, `input.weaponSlots` 9. They are reported in exactly that order, and no relation error appears.
    - A config that breaks R02 and R18 lists R02's error, then R18's.
- [ ] T021 [P] Write failing test `DefaultConfigTests.DefaultJsonBindsValidatesAndCompilesToPinnedBits(int hz)` in `tests/MmoEngine.SimCore.Tests/Config/DefaultConfigTests.cs`, with `[MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]` (FR-029, contracts/config-rules.md#compile-time-conversions). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~DefaultConfigTests"` and confirm it fails.
  - `simcore/config/default.json`, opened read-only and bound by `SimConfigJson`, has no binding error. With `stepRateHz = hz`, `ConfigGuard.Check` and `SimConfigValidator.Errors` are both empty.
  - `CompiledConfig.Create` gives these integers:
    - `ShotCost` 60·hz;
    - `ReloadSteps` 40/60/120 (rifle) and 70/105/210 (launcher);
    - `EquipSteps` 10/15/30 and 15/23/45;
    - `HeatCooldownDelaySteps` 5/8/15;
    - `RecoilRecoveryDelaySteps` 2/3/6;
    - `LifetimeSteps` 60/90/180 and 160/240/480;
    - `RecoilKickPitchUnits` 46, `RecoilKickYawUnits` 18, `RecoilMaxPitchUnits` 1092 and `RecoilMaxYawUnits` 546;
    - `RecoilRecoveryPerStep` 91/61/30;
    - `OverheatThresholdScaled` 100000·hz, `HeatRecoveryScaled` 40000·hz and `HeatCooldownPerStep` 50000.
  - Every float compiled value is pinned as a hex literal per hz: `Dt`, the three `*PerStep` accelerations, `GravityPerStep`, `CosSlopeLimit`, `AxisTop[2]`, `SweepTarget`, `SweepTolerance`, `PushOutThreshold`, `HeadroomClearance`, `VersMax[2]`, `HalfG[2]`, `MaxRangeSq[2]`, and per stance `BoundCenterUp`, `BoundRadius` and `EyeHeight`. Each pinned value is also asserted within 1 ulp of the contract formula evaluated in double, so a pin cannot record a wrong value. The pins are recorded from the first green run.
- [ ] T022 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Config/CompiledConfigTests.cs` (contracts/config-rules.md#compile-time-conversions, contracts/state-serialization.md#maximum-length). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CompiledConfigTests"` and confirm they fail.
  - `PlaceholderConversionsAtThirtyHzMatchTheContract`: `CompiledConfig.Create(TestConfigs.At(30))` gives:
    - `ShotCost` 1800 and `Rpm` 600 and 30;
    - `ReloadSteps` 60 and 105, and `EquipSteps` 15 and 23;
    - `HeatCooldownDelaySteps` 8 and `RecoilRecoveryDelaySteps` 3;
    - `LifetimeSteps` 90 and 240;
    - `MaxStateLength` 91,672 and `MaxStepRecordLength` 91,688;
    - `Config` equal field for field to the input, but not the same instance.
  - `AngleUnitsRoundTiesToEven`. The test finds a `recoilKickPitchDeg` value d, searching up from 0.00274658f with `BitIncrement`, where `(d * 65536f) / 360f` is `0x3EFFFFFF`. For that d, `RecoilKickPitchUnits` is 0. A d giving exactly 0.5 units also gives 0, and one giving 1.5 gives 2. `recoilRecoveryDegPerSecond` 0.001 at 60 Hz gives `RecoilRecoveryPerStep` 1, raised from 0, and a rate of 0 gives 0.
  - `CreateThrowsTheGuardsErrors`: with `movement.gravity` NaN and `stepRateHz` 61, `Create` throws `ConfigValidationException`. Its `Errors` equal `ConfigGuard.Check`'s list, `[stepRateHz: must be between 20 and 60, movement.gravity: must be finite]`, and its `Message` starts `The configuration is invalid: `.
- [ ] T023 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Config/ConfigBlockTests.cs` (contracts/recorded-stream-format.md#config-block). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ConfigBlockTests"` and confirm they fail.
  - `BlockRoundTripsToTheSameBitsAndGuardResult`:
    - `ConfigBlock.Encode(TestConfigs.Default())` is 698 bytes. `count` is 116, and the entries are strictly ascending.
    - Entry 0 is `0x0001` with value bits 30. Entry 9's value bits at offset 58 are `0x411CF5C3` (`movement.gravity` 9.81f). The last entry is `0x5001` with value 2.
    - `ConfigBlock.Decode` of those bytes re-encodes to identical bytes.
    - A config with `movement.gravity` NaN encodes and decodes, keeping its NaN bits. The guard then gives `movement.gravity: must be finite` for both the original and the decoded config.
  - `MalformedBlockIsRefusedNamingTheField(string caseName)`, Theory in the standalone form (format `config`, offsets from the block's first byte). Each row asserts the `CoreFormatException`'s code, offset and field:
    - empty: `Truncated`, 0, `count`;
    - count 117 in the 698-byte block: `Truncated`, 698, `count`;
    - count 115: `Oversized`, 692, `count`;
    - the first two entries swapped: `Inconsistent`, 8, `entries[1].fieldId`, with message `config entries[1].fieldId at offset 8: field 0x0001 is not above 0x0002`;
    - entry 1's fieldId repeated as `0x0001`: `Inconsistent`, 8, `entries[1].fieldId`;
    - the last fieldId as `0x6001`: `Inconsistent`, 692, `entries[115].fieldId`, with a message naming `0x6001`;
    - the last entry deleted and count 115: `Inconsistent`, 692, `input.weaponSlots`, with message `config input.weaponSlots at offset 692: field 0x5001 is missing`;
    - an instance gap, weapon slot 0's 21 entries deleted and count 95: `Inconsistent`, 140, `weapons[0].roundsPerMinute`, with a message naming `0x2001`.
- [ ] T024 [P] Write failing test `SimConfigJsonTests.JsonProblemsUseTheRepoWording(string caseName)` in `tests/MmoEngine.SimCore.Tests/Config/SimConfigJsonTests.cs` (contracts/config-rules.md#server, the wording of `src/MmoEngine.Regionizer.Simulator/Config/RunConfigLoader.cs`). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SimConfigJsonTests"` and confirm it fails.
  - It is a Theory over JSON text, each row asserting exactly one error:
    - truncated `{`: a message starting `is not valid JSON (`;
    - `[]`: `config: must be a JSON object`;
    - an unknown property `bogus`: `bogus: has an invalid value or is not a known field (` and the first line of the parser message;
    - `"stepRateHz": "fast"`: `stepRateHz: has an invalid value or is not a known field (...)`;
    - `"fireMode": "Burst"`: path `weapons[0].fireMode`.
  - A Fact `NineDotEightOneBindsToTheLiteralsBits` asserts that `default.json` binds `movement.gravity` to bits `0x411CF5C3`, the bits of `9.81f`, and that `movement` absent binds as null.
- [ ] T025 [P] Write failing test `SimConfigValidatorTests.ValidatorReportsTheGuardsErrorsUnchanged(string caseName)` in `tests/MmoEngine.SimCore.Tests/Config/SimConfigValidatorTests.cs` (FR-029). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SimConfigValidatorTests"` and confirm it fails.
  - It is a Theory with rows default, `stepRateHz` 61, gravity NaN, an R11 violation, and zero weapons.
  - `SimConfigValidator.Errors(config)` sequence-equals `ConfigGuard.Check(config)`, path for path and message for message.
  - `new SimConfigValidator().Validate(config).Errors` has the same `PropertyName` and `ErrorMessage` pairs in the same order.
  - The default row has no errors.
- [ ] T026 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Inputs/InputCodecTests.cs` (FR-018, contracts/input-command-format.md). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~InputCodecTests"` and confirm they fail.
  - `ExampleEncodesToItsContractBytes`:
    - `TryEncode(new InputCommand(1, 100, Buttons.Forward | Buttons.Fire, 16384, -1024, 0), span17)` returns true and writes `49 01 01 00 00 00 64 00 00 00 81 00 00 40 00 fc 00`.
    - `TryDecode` of those bytes returns true with that exact command.
    - `TryEncode` into a 16-byte span returns false and leaves it all zero.
    - Buttons `0xFE00`, pitch 32767 and slot 255 encode, and decode back unchanged, because decoding checks no value.
  - `DecodeRefusesInContractOrder(string caseName)`, Theory over the six checks of #decoding. Each row asserts false, a `default` command, and the exact code, offset, field and detail:
    - empty: `Truncated` 0 `formatId`, `17 bytes expected, got 0`;
    - first byte `0x4A`: `UnknownMagic` 0 `formatId`, `format identifier 0x4A is not 0x49`;
    - one byte: `Truncated` 1 `version`, `17 bytes expected, got 1`;
    - version 2 in 17 bytes, in 20 bytes, and version 0: `UnsupportedVersion` 1 `version`, message `input version at offset 1: unsupported version 2; this reader supports version 1` (and version 0 for that row);
    - 12 bytes: `Truncated` 12 `length`, `17 bytes expected, got 12`;
    - 16 bytes: message `input length at offset 16: 17 bytes expected, got 16`;
    - 20 bytes: `Oversized` 17 `length`, `17 bytes expected, got 20`.
  - `EncodeThenDecodeIsTheIdentity`: a CsCheck property with a fixed seed constant declared in the test and printed on failure. Over random 17-byte buffers with byte 0 `0x49` and byte 1 `0x01`, `TryDecode` succeeds, and `TryEncode` of the result gives the same 17 bytes.
- [ ] T027 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Formats/StateSerializerTests.cs` (FR-016, contracts/state-serialization.md). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StateSerializerTests"` and confirm they fail.
  - `WorkedExampleHasItsBytesAndHash`:
    - `StateSerializer.Write` of the worked example's values gives exactly `TestStates.WorkedExample`, 136 bytes, with events at `0x7c`.
    - `StateHash.Compute` of it is `0x35038da2f2d9459c`, and `ToString("x16")` is `35038da2f2d9459c`.
  - `MaxLengthMatchesAStateFilledToEveryMaximum(int c, int p, int w, int s, int expected)`, rows (64, 1024, 2, 1, 91672) and (1024, 16384, 8, 8, 1829912). `StateSerializer.MaxLength` equals expected, and a written state with C characters, P projectiles and every event list at its capacity is exactly that long.
  - `WriteThenReadThenWriteIsTheIdentity`: a CsCheck property with a fixed seed. It generates loadable states of 0 to 4 characters, W from 1 to 3, 0 to 8 projectiles, finite non-negative-zero floats and no events. Write, read with the byte-only reader, and write again gives the same bytes.
  - `WriterRefusesTheFirstNonFiniteFieldInLayoutOrder`: a state whose shot event has `direction.y` NaN and whose hit has `t` +∞ returns `StepFault { Code = NonFinite }` with `Path` `events.shots[0].direction.y`.
  - `WriterRewritesNegativeZeroInEventFloats`: a hit `point.x` of −0 is written as `00 00 00 00`, and the event element afterwards holds +0.
  - `LoadRefusesEachByteOnlyRule(string caseName)`, Theory over every row of #loading-a-state's byte-only table. Each row starts from `TestStates.WorkedExample` or a built state, applies a mutation, and asserts `CoreFormatException` with format `state`, that code and that field:
    - 11 bytes: `Truncated` `step`;
    - `characterCount` 1025: `Oversized` `characterCount`;
    - `projectileCount` 16385: `Oversized` `projectileCount`;
    - `weaponCount` 9: `Oversized` `characters[0].weaponCount`;
    - truncated to 100 bytes: `Truncated` `characters[0].weapons[1].fireWait`;
    - one trailing byte: `Oversized` `length`;
    - IDs 2 then 1: `Inconsistent` `characters[1].id`;
    - projectile ID 0: `Inconsistent` `projectiles[0].id`;
    - `nextProjectileId` 0: `Inconsistent` `nextProjectileId`;
    - `position.x` `0x7FC00000`: `NonFinite` `characters[0].position.x`;
    - `position.y` `0x80000000`: `OutOfRange` `characters[0].position.y`;
    - `position.x` 9000: message exactly `state characters[0].position.x at offset 16: 9000 is outside [-8192, 8192]`;
    - `aimPitch` 16385, `stance` 2, `flags` `0x04`, `activeSlot` 2, `weapons[0].phase` 5, `weapons[0].flags` `0x02`, and `launchStep` greater than `step`: `OutOfRange` at that field;
    - `events.expiries.count` 1 with its record: `Inconsistent` `events.expiries.count`.
- [ ] T028 [P] Write failing test `StateHashTests.HashIsXxHash64WithSeedZero` in `tests/MmoEngine.SimCore.Tests/Hashing/StateHashTests.cs`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StateHashTests"` and confirm it fails.
  - `StateHash.Compute` of the empty span is `0xEF46DB3751D8E999`.
  - Of `TestStates.WorkedExample` it is `0x35038da2f2d9459c`.
  - Of 1 MiB of bytes `(byte)(i * 31)` it equals `XxHash64.HashToUInt64(bytes, 0)`.
- [ ] T029 [P] Write failing test `StepRecordTests.RecordIsStepLengthStateAndHash` in `tests/MmoEngine.SimCore.Tests/Batch/StepRecordTests.cs` (contracts/state-serialization.md#step-record). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StepRecordTests"` and confirm it fails.
  - `StepRecord.Write` of the worked example is 152 bytes: `00 00 00 00`, then `88 00 00 00`, then the 136 state bytes, then `9c 45 d9 f2 a2 8d 03 35`.
  - Writing into 151 bytes returns false.
  - `StepRecord.TryRead` walks two packed records back to back, giving each one's step, state and hash, and refuses a record truncated mid-state.
- [ ] T030 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Formats/CollisionFileTests.cs` (FR-021, contracts/collision-data-format.md). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CollisionFileTests"` and confirm they fail.
  - `TinyWorldEncodesToItsContractBytesAndChecksum`:
    - `CollisionFile.Encode(TestWorlds.TinyWorld())` is 188 bytes, equal to the contract's hex dump. The trailer is `9c ca 3f 3b fa e3 24 92`, and the decoded `Checksum` is `0x9224e3fa3b3fca9c`.
    - `HeightfieldOnly()` encodes to 92 bytes.
  - `DecodeThenEncodeIsTheIdentity(string world)`, rows tiny-world, heightfield-only and flat: `Encode(Decode(bytes).Data)` equals the bytes, and `Data.Heights[8]` of tiny-world keeps bits `0x80000000`.
  - `FrameChecksRefuseInContractOrder(string caseName)`, Theory over checks 1–4 and 7 of #check-order. Each row asserts `CoreFormatException` with format `collision`, the code, the offset and the field:
    - 3 bytes: `Truncated` 3 `magic`;
    - `MMCX`: `UnknownMagic` 0 `magic`;
    - 5 bytes: `Truncated` 5 `version`;
    - version 2: `UnsupportedVersion` 4 `version`, message `collision version at offset 4: unsupported version 2; this reader supports version 1`;
    - version 2 truncated to 6 bytes: `UnsupportedVersion` 4, because the version comes before the length;
    - 7 bytes: `Truncated` 7 `headerLength`;
    - `headerLength` 40: `Inconsistent` 6 `headerLength`;
    - truncated to 20: `Truncated` 20 `header`;
    - truncated to 100: `Truncated` 100 `fileLength`, message `collision fileLength at offset 100: the file declares 188 bytes, 100 are present`;
    - 4 bytes appended: `Oversized` 188 `fileLength`;
    - `write 64 "0000803f"`: `ChecksumMismatch` 180 `checksum`, message `collision checksum at offset 180: the trailer is 9224e3fa3b3fca9c, the content hashes to <x16>`, where `<x16>` is the XxHash64 of the mutated bytes [0, 180).
- [ ] T031 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Collision/CollisionWorldBuildTests.cs` (FR-004, edge case: degenerate triangles). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CollisionWorldBuildTests"` and confirm they fail.
  - `DegenerateTrianglesLoadAndAreIgnored`: tiny-world loads.
    - `DegenerateTriangleCount` is 1.
    - Triangle ID 10 (indices 0, 0, 1) is unused: `TryGetMeshTriangle(10, ...)` is false.
    - ID 10 is in no grid cell, and cell (0, 0) holds exactly [8, 9].
  - `TinyWorldDerivesItsTrianglesAndGrid`:
    - `HeightfieldTriangleCount` is 8 and `MeshTriangleCount` is 3, with IDs 8 to 10.
    - Triangles 8 and 9 have normal (0, 0, 1) and AABB (−0.5, 0, 0.5) to (0.5, 1, 0.5).
    - Heightfield triangle 3 is cell (1, 0)'s k = 1: (0, 0, −1), (0, 0.25, 0), (1, 0, 0), bit for bit.
    - `GridCellsX` and `GridCellsZ` are 1; `CellStart` is [0, 2] and `CellTriangles` is [8, 9].
    - The playable area is [−1, 1] in X and Z. The boundary planes are −4 (+X normal at x = −1), −3 (−X at x = 1), −2 (+Z at z = −1) and −1 (−Z at z = 1).
    - Two decodes of the same bytes give identical bits for every normal, AABB and grid array.
  - `HeightAtFollowsTheFixedDiagonal`: on tiny-world, `HeightAt(0.75f, -0.75f)` is 0 (triangle k = 0), `HeightAt(0.25f, -0.25f)` is 0.125f within 1e-6 (k = 1), `HeightAt(0.5f, -0.5f)` is exactly 0 (on the diagonal), and `HeightAt(0f, 0f)` is 0.25f.
  - `SharedVerticesAreBitIdentical`: on a 9 × 9 heightfield with origin (−1000.3f, 2000.7f) and `cellSize` 0.37f, every vertex of every heightfield triangle equals the bits of the one sample expression `(originX + (float)i*cellSize, h, originZ + (float)j*cellSize)`. A vertex shared by neighbouring cells is identical in each.
  - `MaxQueryCandidatesCoversTheWidestCapsuleBox(float width, int expected)`, Theory on `TestWorlds.Flat()` (cellSize 1, gridCellSize 4). (1.22f, 274) is the placeholder capsule's box, research R6. Further rows are (0.5f, 264), which is 2·(1+1)² + 64·(1+1)²; (4f, 306), which is 2·(4+1)² + 64·(1+1)²; (4.5f, 648), which is 2·(5+1)² + 64·(2+1)² and takes the grid term past one grid cell; and (8.5f, 1224), which is 2·(9+1)² + 64·(3+1)². Each asserts `2·(⌈w/cellSize⌉+1)² + 64·(⌈w/gridCellSize⌉+1)²`.
- [ ] T032 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Formats/RecordedStreamTests.cs` (FR-022, contracts/recorded-stream-format.md). Each test builds the two-steps stream in the test:
  - stepHz 30, the default config, `matchSalt` `0x0123456789abcdef`, `collisionChecksum` `0x9224e3fa3b3fca9c`, seed 0;
  - the initial state `TestStates.WorkedExample`;
  - step 1 with the example command for index 0 and expected hash 1, and step 2 with no command and expected hash 2.

  Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~RecordedStreamTests"` and confirm they fail.
  - `EncodeThenDecodeIsTheIdentity`:
    - `Encode()` is 950 bytes: config at [64, 762), initial state at [762, 898), step 1 at 898 with the command at 904 and the hash at 921, step 2 at 929, and the trailer at 942.
    - `Decode` returns equal `RulesRevision` (1), `StepHz`, `CharacterCount` (1), `Seed`, `MatchSalt`, `CollisionChecksum`, config bits, initial state bytes, steps, commands and expected hashes, and re-encodes to the same bytes.
    - A seeded copy with seed 7 and salt `0x8000000000000001` round-trips too.
  - `FrameChecksRefuseInContractOrder(string caseName)`, Theory over checks 1–4 and 7. Each row asserts format `stream`, the code, the offset and the field:
    - 3 bytes: `Truncated` 3 `magic`;
    - `MMRX`: `UnknownMagic` 0;
    - version 2: `UnsupportedVersion` 4 `version`, message `stream version at offset 4: unsupported version 2; this reader supports version 1`;
    - version 2 truncated to 6 bytes: `UnsupportedVersion` 4;
    - `headerLength` 72: `Inconsistent` 6;
    - truncated to 40: `Truncated` 40 `header`;
    - truncated to 900: `Truncated` 900 `fileLength`;
    - 4 bytes appended: `Oversized` 950 `fileLength`;
    - `write 921 "ff"`: `ChecksumMismatch` 942 `checksum`.
  - `EmbeddedConfigAndStateErrorsCarryTheirPrefix(string caseName)`, with every mutation followed by `fixChecksum`:
    - config-count-short: `Oversized` 756 `config.count`;
    - config-not-ascending: `Inconsistent` 72 `config.entries[1].fieldId`, message `stream config.entries[1].fieldId at offset 72: field 0x0001 is not above 0x0002`;
    - config-unknown-field: `Inconsistent` 756 `config.entries[115].fieldId`;
    - config-missing-field: message `stream config.input.weaponSlots at offset 756: field 0x5001 is missing`;
    - state-has-events: message `stream initialState.events.expiries.count at offset 890: a loaded state has no events, found 1`;
    - initial `position.x` 9000: `OutOfRange` 778 `initialState.characters[0].position.x`.
- [ ] T033 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Hitboxes/HitboxBuilderTests.cs` (FR-011, data-model.md#hitbox-layout-and-hitboxes). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~HitboxBuilderTests"` and confirm they fail.
  - `WorldPointsFollowTheYawRotation(Stance stance, ushort yaw)`, Theory over {Standing, Crouched} × {0, 16384, 32768, 49152, 12345}. `HitboxBuilder.Build` uses the layout from `TestConfigs.Compiled(30)` and position (10.5, 2, −3.25).
    - It writes Head, Torso and Legs into a 3-element span, in that order.
    - Each endpoint's bits equal `wx = fx + ((sy*lf) - (cy*lr))`, `wy = fy + lu` and `wz = fz + ((cy*lf) + (sy*lr))`, with `(sy, cy)` from `SimMath.SinCosBinaryAngle(yaw)`.
    - Each radius equals the layout's.
    - At yaw 16384, the standing head A is (10.53, 3.56, −3.25).
  - `BoundingSphereEnclosesEveryCapsule(Stance stance)`:
    - The returned centre is (fx, fy + BoundCenterUp, fz) for every yaw. `BoundCenterUp` is 0.885 for standing and 0.57 for crouched, within 1e-6.
    - For every capsule endpoint e, |e − centre| + radius ≤ BoundRadius − 0.01, within 1e-5.
- [ ] T034 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/World/SimWorldTests.cs` (data-model.md#world-simworld). Use `TestWorlds.Flat()` and the `TestStates` states. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SimWorldTests"` and confirm they fail.
  - `StepAdvancesTheStepNumberAndHashesTheCanonicalState(int hz)`, over `TestRates.All`. A world from `TestConfigs.Compiled(hz)`, Reset with `StandingOnFlat` and salt 7, steps once with no commands.
    - `Fault.Code` is `None`, and `report.Step` and `CurrentStep` are 1.
    - The state's `step` field is 1, `State.Length` is 136, and `Hash` equals `StateHash.Compute(report.State)`.
    - A second step gives step 2 and a different hash.
  - `ResetRefusesStatesThatDoNotFitTheConfigurationOrWorld(string caseName)`, Theory over contracts/state-serialization.md#loading-a-state's configuration and world table. Each row asserts `CoreFormatException` with format `state`, that code and that field:
    - 65 characters: `Oversized` `characterCount`;
    - `maxProjectiles` 1 and 2 projectiles: `Oversized` `projectileCount`;
    - `weaponCount` 3: `Inconsistent` `characters[0].weaponCount`;
    - x = 40 or z = −40: `OutOfRange` `characters[0].position.x` or `.position.z`;
    - grounded with `groundNormal.y` 0.5: `OutOfRange` `characters[0].groundNormal.y`;
    - airborne with normal (0.1, 0.99, 0): `Inconsistent` `characters[0].groundNormal.x`;
    - `magazine` 31: `OutOfRange` `characters[0].weapons[0].magazine`;
    - `fireWait` 1800 and −1: `OutOfRange` `.fireWait`;
    - `recoilPitch` 1093: `OutOfRange` `.recoilPitch`;
    - `recoilYaw` −547: `OutOfRange` `.recoilYaw`;
    - weapon 1 Firing while `activeSlot` is 0: `Inconsistent` `characters[0].weapons[1].phase`;
    - projectile `class` 2: `OutOfRange` `projectiles[0].class`;
    - one byte-only row, `position.x` NaN: `NonFinite`.
  - `ResetKeepsThePreviousWorldOnRefusal`: Reset `StandingOnFlat` and Step once. Then a refused Reset throws. `CurrentStep` is still 1, and the next Step returns step 2 with the hash a fresh world gives for the same two steps.
  - `CharactersWithoutACommandGetTheNeutralInput`: with `TwoCharacters`, Step with only `new CharacterCommand(1, cmd)`. The internal `LastStepInputs` span holds:
    - for index 0, the neutral input: Buttons 0; AimYaw, AimPitch and WeaponSlot equal to its state; Sequence equal to its `lastSequence`;
    - for index 1, `cmd`.
  - `ConstructorSizesEveryBufferFromTheMaxima`: through internal accessors, with the placeholders on `Flat()`:
    - the candidate buffer is `collision.MaxQueryCandidates((3f * 0.4f) + (2f * 0.01f))`, which is 274;
    - the state buffer is 91,672 bytes;
    - the shot buffer holds 64, the hit and expiry buffers hold 1,024 each, and the other three event buffers hold 64 each.
- [ ] T035 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/World/CanonicalisationTests.cs`. Each uses a world on `Flat()` Reset with `WithFlyingProjectile`. Each Theory runs over the 18 float paths of the character and projectile records: `characters[0].position.x` … `characters[0].groundNormal.z` and `projectiles[0].origin.x` … `projectiles[0].position.z`. The paths map to `(fieldOrdinal, elementIndex)` through `StateSerializer.TryFindField`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CanonicalisationTests"` and confirm they fail.
  - `NegativeZeroIsCommittedAndHashedAsPositiveZero(string fieldPath)` (edge case: negative zero; FR-016):
    - World A steps with `StepWithInjectedBits(empty, ordinal, index, 0x80000000)`, and an identical world B with bits `0x00000000`.
    - Both commit, and the field's 4 bytes in A's `State` are `00 00 00 00`.
    - A's `Hash` equals B's, and A's `State` equals B's byte for byte.
    - A following plain Step gives equal hashes in A and B.
  - `NonFiniteFieldRefusesTheStepAndKeepsThePreviousState(string fieldPath, uint bits)`, × {`0x7FC00000`, `0x7F800000`, `0xFF800000`} (edge case: NaN and infinity; FR-017):
    - `Fault.Code` is `StepFaultCode.NonFinite`, `Fault.Path` equals `fieldPath` (for example `characters[0].velocity.y`), `Hash` is 0, and `report.Step` is the previous step.
    - Afterwards `CurrentStep` is unchanged, and a plain Step gives the step number, state bytes and hash an untouched world gives for that step.
- [ ] T036 [P] Write failing test `ConfigCommandTests.ConfigCheckExitsZeroForValidOneForInvalidTwoForMissing(string caseName)` in `tests/MmoEngine.SimCore.Tests/Cli/ConfigCommandTests.cs` (FR-029, contracts/simcore-cli.md#config-check). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ConfigCommandTests"` and confirm it fails.
  - It is a Theory through `SimcoreCli.Run`, with these rows:
    - `config check <RepoPaths.Root>/simcore/config/default.json`: exit 0 and empty stdout;
    - a temp copy with `stepRateHz` 61 and `walkSpeed` 0: exit 1 and stdout exactly `stepRateHz: must be between 20 and 60\nmovement.walkSpeed: must be between 0.1 and 20\n`;
    - a missing file: exit 2, with stderr naming the path;
    - no file argument: exit 2.
- [ ] T037 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Cli/EnvironmentTests.cs`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Cli.EnvironmentTests"` and confirm they fail.
  - `UnknownEnvironmentExitsTwoNamingTheVariable`: `Program.Run(["config", "check", <default.json>], out, err, environmentName: "Prod")` exits 2, and stderr contains `DOTNET_ENVIRONMENT: must be one of Development, Dev, Test, Staging, Production, was 'Prod'`. With `environmentName: null` it exits 0, defaulting to Development.
  - `KnownEnvironmentsEqualTheSimulators`: `SimEnvironment.Allowed` sequence-equals `MmoEngine.Regionizer.Simulator.Config.EnvironmentNames.Allowed`, and `SimEnvironment.Default` is `Development`.
- [ ] T038 [P] Write failing test `CliLoggingTests.EveryLogLineIsCompactJsonWithToolSimcore` in `tests/MmoEngine.SimCore.Tests/Cli/CliLoggingTests.cs`. `SimcoreCli.Run("config", "check", <default.json>)` writes at least one stderr line. Every non-empty line parses as a JSON object with `@t`, `@mt` and `"Tool": "simcore"`, and nothing is logged to stdout. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CliLoggingTests"` and confirm it fails.
- [ ] T039 [P] Write failing tests in `tests/MmoEngine.SimCore.Tests/Cli/AtomicFileTests.cs` (contracts/simcore-cli.md#settings-output-and-exit-codes). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~AtomicFileTests"` and confirm they fail.
  - `WriteReplacesTheFileThroughATempFileInTheSameDirectory`: `AtomicFile.WriteAllBytes(path, bytes)` in a `TempDirectories` folder creates the file with exactly those bytes. A second write replaces it, and the folder then holds only that file.
  - `FailedWriteLeavesTheOriginalAndNoTempFile`: `AtomicFile.Write(path, stream => throw new IOException())` rethrows. The original file keeps its bytes, and no other file remains in the folder.

### Implementation

Core code obeys the analyzer rules from this phase on, before Phase 5 turns them on:

- float32 only, and no System.Numerics;
- no `MathF` or `Math` outside the allowlist: use `SimMath`, and do no FMA;
- no hash-ordered iteration, no clock, no `System.Random`, no locks, no `#if` and no reflection;
- float-to-int only through `SimMath.FloorToInt`, `RoundToInt` and `TruncateToInt`, and `ulong` to float only as `(float)(uint)`;
- the contracts' exact parenthesisation;
- explicit usings and file-scoped namespaces equal to the folders.

- [ ] T040 [P] Add `src/MmoEngine.SimCore/SimCoreVersion.cs` (`public const uint RulesRevision = 1;`) and the code enums with data-model.md#reason-and-outcome-codes's values and `byte` underlying types. No later phase adds or renumbers one. Makes T018 pass and lets T014 and T015 compile; later tasks keep them green. The enums:
  - `Inputs/Buttons.cs` (`[Flags]` `ushort`, `Known = 0x01FF`) and `Inputs/InputRejection.cs`;
  - `Weapons/WeaponPhase.cs`, `Weapons/FireBlock.cs` and `Weapons/WeaponFlags.cs`;
  - `Config/FireMode.cs`;
  - `Projectiles/ExpiryReason.cs` and `Projectiles/HitKind.cs`;
  - `Hitboxes/HitboxPart.cs`;
  - `Movement/MovementOutcome.cs` (`[Flags]`), `Movement/Stance.cs` and `Movement/CharacterFlags.cs`;
  - `World/StepFaultCode.cs`;
  - `Formats/FormatErrorCode.cs`.
- [ ] T041 [P] Write the offline coefficient generators in `tools/simcore/` (research R3; clean-room, run in a throwaway venv, never in CI):
  - `requirements.txt` pins `mpmath==1.4.1` and `numpy==2.5.3`.
  - `gen_coeffs.py` runs a weighted Remez fit at 60 digits for the Sin (degree 7), Cos (degree 10, fdlibm-style compensation), Tan (degree 15), shared Asin/Acos (degree 5) and Atan kernels. It rounds the coefficients to binary32 one at a time, refitting the rest after each, checks each stage against naive rounding and falls back where Remez with fixed coefficients diverges (the Tan degree-15 pitfall). It also emits the 4-part Cody–Waite split of π/2 (11, 11, 11 and 24 bits) and the `SinCosBinaryAngle` constants (8-bit `C_hi`).
  - `gen_twoopi.py` emits 320 bits of 2/π at 120 digits.
  - `emit_cs.py` writes two files with an `// <auto-generated>` header naming `tools/simcore/emit_cs.py`:
    - `src/MmoEngine.SimCore/Math/SimMathCoefficients.g.cs`: `internal static class SimMathCoefficients` with `const float` `f` literals only, so the file has no executable code;
    - `src/MmoEngine.SimCore/Math/TwoOverPi.g.cs`: `internal static class TwoOverPi` with `ReadOnlySpan<uint> Words => [ ... ]`, RVA data with no allocation and no static field.

    `emit_cs.py --list` prints `name 0xXXXXXXXX` per constant.

  Run them with `python3 -m venv "$TMPDIR/simcore-venv" && "$TMPDIR/simcore-venv/bin/pip" install -r tools/simcore/requirements.txt && "$TMPDIR/simcore-venv/bin/python" tools/simcore/emit_cs.py --out src/MmoEngine.SimCore/Math`. Commit both `.g.cs` files, and paste the `--list` lines into the `SimMathTests.CoefficientsHaveTheirPinnedBits` rows of T016. Makes `SimMathTests.CoefficientsHaveTheirPinnedBits` pass.
- [ ] T042 [P] Add `simcore/config/default.json` as the example of contracts/config-rules.md#the-configuration-file, verbatim (two weapons, two projectile classes, 30 Hz). Together with the config and server tasks below, it makes T021 pass.
- [ ] T043 Implement `src/MmoEngine.SimCore/Formats/FormatError.cs` and `src/MmoEngine.SimCore/Formats/CoreFormatException.cs` (contracts/collision-data-format.md#api). Makes T019 pass.
  - `FormatError` is a `readonly struct` with `Format`, `Code`, `Offset` (`long`), `Field` and `Detail`. `ToString()` gives `"<format> <field> at offset <n>: <detail>"`.
  - `Detail` is formatted when read, from a stored `FormatDetail` template (a small enum in the same file: ExpectedLength, UnknownFormatId, UnsupportedVersion, DeclaredLength, CountsNeed, ChecksumMismatch, NotFinite, OutOfRangeFloat, NotAbove, Missing, …) and up to three `ulong` arguments, so creating one on the tick path allocates nothing. Decoders off the tick path may pass a preformatted detail string instead.
  - `CoreFormatException(FormatError)` has `Message == error.ToString()`.
- [ ] T044 Implement `src/MmoEngine.SimCore/Math/SimMath.cs` (`public static class SimMath`, research R3 and R4). Inside it write `System.MathF` fully qualified, and evaluate every polynomial by Horner in `float` with every operation parenthesised and no FMA. Makes the remaining tests of T016 pass (NonFiniteInputsReturnNaN, SignedZerosAndSubnormalsFollowTheContract, SaturatingConversionsClampNaNAndOutOfRange, MinMaxAreCompareSelects, SinCosBinaryAngleIsExactAtTheQuadrants, EachReductionPathMeetsItsBoundAtSampleInputs).
  - `Sin`, `Cos`, `SinCos(float, out float s, out float c)` and `Tan` reduce as follows:
    - none for |x| ≤ π/4;
    - 4-part Cody–Waite for |x| ≤ 12867, with k from `RoundToInt` and a Fast2Sum tail fed into the kernels;
    - exact integer Payne–Hanek above that, multiplying by a 96-bit window of `TwoOverPi.Words` in `UInt128` with one hand-written rounding.
  - Tan uses an accurate reciprocal.
  - `Asin` and `Acos` share the degree-5 polynomial, with a square-root correction above ½.
  - `Atan` uses the fdlibm breakpoints 7/16, 11/16, 19/16 and 39/16, and `Atan(±∞)` is ±π/2.
  - `Atan2` follows C99 Annex F.
  - `SinCosBinaryAngle(ushort a, out float s, out float c)` takes the quadrant `(a + 0x2000) >> 14` and reduces in integers.
  - `Sqrt` is `System.MathF.Sqrt`.
  - `Min` is `a < b ? a : b`, `Max` is `a > b ? a : b`, and `Clamp` uses `System.Math.Clamp`, allowed inside SimMath only.
  - `FloorToInt`, `RoundToInt` (ties to even) and `TruncateToInt` saturate first: NaN gives 0, ≥ 2³¹ gives `int.MaxValue`, and < −2³¹ gives `int.MinValue`. Only then do they cast.
  - Non-finite input to a radian function returns the `float.NaN` constant.
- [ ] T045 Implement `src/MmoEngine.SimCore/Geometry/Vec3.cs`, a `readonly struct Vec3(float X, float Y, float Z)`:
  - `+`, `-` and scalar `*` per component;
  - `Dot` as `((a.X*b.X) + (a.Y*b.Y)) + (a.Z*b.Z)`;
  - `Cross` as `((a.Y*b.Z) - (a.Z*b.Y), (a.Z*b.X) - (a.X*b.Z), (a.X*b.Y) - (a.Y*b.X))`;
  - `LengthSquared` as `Dot(a, a)`, and `Length` as `SimMath.Sqrt(LengthSquared)`.

  `ClosestPoints` is Phase 3's and `SegmentCapsule` Phase 7's. Makes T017 pass.
- [ ] T046 Implement `src/MmoEngine.SimCore/Inputs/InputCommand.cs` (`readonly record struct InputCommand(uint Sequence, uint ClientStep, Buttons Buttons, ushort AimYaw, short AimPitch, byte WeaponSlot)`) and `src/MmoEngine.SimCore/Inputs/InputCodec.cs`. Makes T026 pass.
  - The constants are `FormatId 0x49`, `FormatVersion 1` and `EncodedSize 17`.
  - `TryEncode` writes the 17 little-endian bytes of contracts/input-command-format.md#layout without validating them. It returns false and writes nothing when the destination is short.
  - `TryDecode` runs the six checks of #decoding in order, with format `input` and literal field names. It throws nothing and allocates nothing, and on refusal it returns `default` with the error.

  `InputValidator` is Phase 3's.
- [ ] T047 Implement the configuration model in `src/MmoEngine.SimCore/Config/` (data-model.md#configuration, contracts/config-rules.md#shape and #error-shape). Supports T020.
  - `SimConfig.cs`: sealed classes `SimConfig`, `WorldConfig`, `MovementConfig`, `WeaponConfig`, `ProjectileClassConfig`, `HitboxesConfig`, `HitboxCapsuleConfig` (`A`, `B`, `Radius`), `HitboxPointConfig` (`Fwd`, `Right`, `Up`) and `InputConfig`.
    - They have `get; set;` properties: `int` for Int rows, `float` for Float rows and `FireMode` for the Enum row.
    - Sections, array elements and capsule points are nullable, so a missing one is a shape error.
    - `SimConfig.Clone()` makes a deep copy.
  - `ConfigError.cs`: `sealed record ConfigError(string Path, string Message)`, whose `ToString()` is `"{Path}: {Message}"`.
  - `ConfigValidationException.cs`: the contract's exact shape.
- [ ] T048 Implement the rule table in `src/MmoEngine.SimCore/Config/` (contracts/config-rules.md#rule-table, #relations, #check-order-and-the-error-list). Makes the `ConfigGuard` assertions of T020 pass; its `SimConfigValidator` half passes with the server task below.
  - `ConfigFields.cs`: `ImmutableArray<ConfigField>`, one row per field definition of the six sections, in fieldId order. Each row holds:
    - section, field number, the path template (for example `weapons[{i}].roundsPerMinute`), kind, unit, min and max;
    - a getter and a setter over a `SimConfig` instance as `uint` value bits (Int as two's complement, Float as its bits, Enum as its value).

    `FieldId(section, instance, field)` is `section << 12 | instance << 8 | field`, and hitbox capsule fields are `0x4000 | s << 8 | c << 4 | j`.
  - `ConfigRelations.cs`: R01–R19 with the contract's exact float parenthesisation, `CosSlopeLimit` from `SimMath.Cos((slopeLimitDegrees * System.MathF.PI) / 180f)`, and `MaxSpeed`. The messages substitute `[i]`, `[c]`, `{stance}` and `({n})`.
  - `ConfigGuard.cs`: `Check(SimConfig) → IReadOnlyList<ConfigError>`, never throwing for a value.
    - Shape errors first, and any one of them ends the check.
    - Then every field in ascending fieldId over the present instances. A non-finite float reports only `must be finite`; Int and Float rows report `must be between {min} and {max}` with invariant round-trip numbers; an Enum row reports `must be Auto or Semi`.
    - Then the relations in order, only when there was no earlier error.
- [ ] T049 Implement `src/MmoEngine.SimCore/Hitboxes/HitboxLayout.cs` and `src/MmoEngine.SimCore/Hitboxes/Hitbox.cs` (data-model.md#hitbox-layout-and-hitboxes):
  - `HitboxLayout` is a sealed class with `Capsules` (an `ImmutableArray` of 3 `HitboxCapsuleLayout` in Head, Torso, Legs order, each with local A and B as fwd/right/up floats and a radius), `BoundCenterUp`, `BoundRadius` and `EyeHeight`;
  - `readonly struct Hitbox(HitboxPart Part, Vec3 A, Vec3 B, float Radius)`;
  - `readonly struct HitboxBounds(Vec3 Centre, float Radius)`.

  Supports the compiled-config and hitbox-builder tasks.
- [ ] T050 Implement the state and event types:
  - `src/MmoEngine.SimCore/Movement/CharacterState.cs`: the struct of data-model.md#character-state, without the weapons, which the world holds flat by index × slots;
  - `src/MmoEngine.SimCore/Weapons/WeaponState.cs`: the struct of #weaponstate, with `WeaponState.New(int magazineSize, int reserveRounds)` giving the new-weapon values (Ready, full magazine, reserve, `StepsSinceShot` 65535, everything else 0);
  - `src/MmoEngine.SimCore/Projectiles/Projectile.cs`;
  - `src/MmoEngine.SimCore/Projectiles/ProjectileHit.cs`: the hit event element;
  - `src/MmoEngine.SimCore/World/StepEvents.cs`: the five other event element types (`ShotEvent`, `ExpiryEvent`, `InputRejectionEvent`, `FireBlockEvent` and `MovementOutcomeEvent`), and a `ref struct StepEvents` with six `ReadOnlySpan` views in serialisation order;
  - `src/MmoEngine.SimCore/World/StepFault.cs`: `readonly struct StepFault(StepFaultCode Code, int FieldOrdinal, int ElementIndex)`, whose `Path` is formatted on demand through the layout table;
  - `src/MmoEngine.SimCore/World/StepReport.cs`: a `ref struct` with `Step`, `Fault`, `Hash`, `State`, `Events` and `Counters` (`StepCounters` with Sweeps, SlideCapHits and FireBlocks);
  - `src/MmoEngine.SimCore/World/CharacterCommand.cs`: `readonly record struct CharacterCommand(int CharacterIndex, InputCommand Command)`.

  Supports the serializer and world tasks.
- [ ] T051 Implement `src/MmoEngine.SimCore/Formats/StateSerializer.cs`, `src/MmoEngine.SimCore/Hashing/StateHash.cs` and `src/MmoEngine.SimCore/Batch/StepRecord.cs` (contracts/state-serialization.md). Makes T027, T028 and T029 pass.
  - `StateSerializer` holds the layout as data: `ImmutableArray<StateField>` (ordinal, record kind, offset in record, size, type, name). It provides:
    - the constants of #api and `MaxLength(c, p, w, s)`, which is `24 + c·(67 + 30·w + 25·s) + 80·p`;
    - `TryFindField(string path, out int fieldOrdinal, out int elementIndex)` and `FieldPath(int fieldOrdinal, int elementIndex)`;
    - `Write(...)` over the world's spans (header values, characters, weapons, projectiles and the six event spans) into a caller buffer, in layout order. It applies the finite check `(b & 0x7F800000) == 0x7F800000`, returning a `StepFault` naming the first such field, and rewrites `0x80000000` to 0 in both the buffer and the source field. It allocates nothing.
    - `ReadLoaded(ReadOnlySpan<byte>, in StateErrorContext context)`, which runs the byte-only checks of #loading-a-state in layout order and returns a `StateSnapshot`. The `StateErrorContext` carries the format (`state` or `stream`), the base offset and the field prefix (`initialState.`). The reader allocates; it is not tick path. A refusal throws `CoreFormatException` with the exact detail text, for example `9000 is outside [-8192, 8192]` and `a loaded state has no events, found 1`.
  - `StateHash.Compute` is `XxHash64.HashToUInt64(bytes, 0)`.
  - `StepRecord` writes and reads `u32 step | u32 stateLength | state | u64 hash`, little-endian.
- [ ] T052 Implement `src/MmoEngine.SimCore/Config/CompiledConfig.cs`: `CompiledConfig.Create(SimConfig)` runs `ConfigGuard.Check` and throws `ConfigValidationException` on any error. It then computes every value of contracts/config-rules.md#compile-time-conversions in `float` with the shown parenthesisation, or in `int`. Makes T022 pass.
  - `ceilSteps(ms)` is `(ms * Hz + 999) / 1000`, and `angleUnits` is `SimMath.RoundToInt((deg * 65536f) / 360f)`.
  - `RecoilRecoveryPerStep` is raised to 1 when the rate is above 0.
  - It builds a `HitboxLayout` per stance, with BoundCenterUp, BoundRadius plus 0.01f, and EyeHeight.
  - It stores per-slot compiled weapons and per-class projectile values in `ImmutableArray`s, `MaxShotsPerStepMax` (S), `MaxStateLength = StateSerializer.MaxLength(maxCharacters, maxProjectiles, weaponSlots, S)`, `MaxStepRecordLength = MaxStateLength + 16`, and `Config` as a deep copy.
- [ ] T053 Implement `src/MmoEngine.SimCore/Config/ConfigBlock.cs` (contracts/recorded-stream-format.md#config-block). Makes T023 pass.
  - `Encode(SimConfig)` takes a shape-valid config and writes `u16 count`, then `{u16 fieldId, u32 valueBits}` per present field in ascending fieldId, through `ConfigFields`.
  - `Decode(ReadOnlySpan<byte>)` is the standalone form, with format `config` and offsets from the block. It checks the structure completely, in contract order:
    - the length is `2 + 6·count`: fewer bytes is `Truncated` at `count`, more is `Oversized`;
    - each fieldId is known and above the one before (`Inconsistent` at `entries[k].fieldId`, `field 0x0001 is not above 0x0002`);
    - every field of every present instance is present, with weapon and class instances 0 to N − 1 with no gap. A missing one is `Inconsistent` at the offset where it would sort, as `<path>` with `field 0x5001 is missing`.
  - It checks no value: NaN and out-of-range values decode, and the guard refuses them.
  - An internal overload takes the format, base offset and prefix, for the stream-embedded form (`stream`, +64, `config.`).
- [ ] T054 Implement the server adapter in `src/MmoEngine.SimCore.Server/` (contracts/config-rules.md#server). Makes T024, T025 and T021 pass, and the `SimConfigValidator` half of T020.
  - `SimConfigJson.cs`: `Load(string path)` and `Parse(string json)` return `SimConfigLoad(SimConfig? Config, IReadOnlyList<ConfigError> Errors)`.
    - It uses System.Text.Json with camelCase names, `UnmappedMemberHandling.Disallow`, a `JsonStringEnumConverter<FireMode>` and floats read as binary32.
    - Its wording matches `RunConfigLoader`: `is not valid JSON (…)`, `config: must be a JSON object`, and `has an invalid value or is not a known field (…)` keyed by the camelCase path.
    - It refuses a file larger than 1 MiB before reading.
  - `SimConfigValidator.cs`: `AbstractValidator<SimConfig>` with the contract's `RuleFor(c => c).Custom(...)` over `ConfigGuard.Check`, and `static IReadOnlyList<ConfigError> Errors(SimConfig)`.
  - `SimEnvironment.cs`: `VariableName` `DOTNET_ENVIRONMENT`, `Default` `Development`, `Allowed` [Development, Dev, Test, Staging, Production], and `Resolve(string?)` with the simulator's message `must be one of …, was '<value>'`.
- [ ] T055 Implement `src/MmoEngine.SimCore/Hitboxes/HitboxBuilder.cs`. `HitboxBuilder.Build(HitboxLayout layout, Vec3 position, ushort aimYaw, Span<Hitbox> destination)` takes `(sy, cy)` from `SimMath.SinCosBinaryAngle(aimYaw)` and writes Head, Torso and Legs, with `wx = fx + ((sy*lf) - (cy*lr))`, `wy = fy + lu` and `wz = fz + ((cy*lf) + (sy*lr))`. It returns `HitboxBounds((fx, fy + BoundCenterUp, fz), BoundRadius)`, and it is allocation-free. Makes T033 pass.
- [ ] T056 Implement the collision world build in `src/MmoEngine.SimCore/Collision/` (data-model.md#collision-world, contracts/collision-data-format.md#derived-data-built-at-load), with the records `CollisionData` and `MeshData` of contracts/collision-data-format.md#api in `src/MmoEngine.SimCore/Formats/CollisionFile.cs`. Supports T031.
  - `Heightfield.cs`:
    - the one sample expression;
    - the fixed v00 → v11 diagonal, with triangle IDs `2·(j·(columns − 1) + i) + k`, generated on demand by `GetTriangle(int id, out Vec3 a, out Vec3 b, out Vec3 c)`;
    - `HeightAt` by the same diagonal rule;
    - the playable area, and the boundary planes −4 to −1.
  - `MeshSet.cs`:
    - triangles in ID order (H + global index) with normal `(AB × AC) / SimMath.Sqrt(|AB × AC|²)` and AABB from `SimMath.Min` and `SimMath.Max`;
    - a triangle is dropped as degenerate when |e|² < 1e-6 for any edge, or |AB × AC|² ≤ 1e-8·(maxEdge²)², or ≤ 4e-12. Its ID stays unused and `DegenerateTriangleCount` counts it;
    - `TryGetMeshTriangle(int id, out MeshTriangle)`.
  - `UniformGrid.cs`:
    - `CellsX = max(1, SimMath.TruncateToInt(System.MathF.Ceiling(extentX / gridCellSize)))`, and CellsZ likewise;
    - one clamped cell function `clamp(SimMath.FloorToInt((x - originX) / gridCellSize), 0, CellsX - 1)`;
    - a counting pass in ID order, building CSR `CellStart[CellsX·CellsZ + 1]` and ascending `CellTriangles`.

    The per-cell and registration refusals (check 9) are Phase 8's.
  - `CollisionWorld.cs`: an immutable `sealed partial class` with:
    - `Checksum`, `Data`, `HeightfieldTriangleCount`, `MeshTriangleCount`, `DegenerateTriangleCount`, `GridCellsX` and `GridCellsZ`;
    - `HeightAt`, and the `Heightfield`, `Meshes` and `Grid` accessors;
    - `MaxQueryCandidates(float boxWidth)`, which is `2·(⌈w/cellSize⌉+1)² + 64·(⌈w/gridCellSize⌉+1)²`.

    It is built in file order by an internal factory from `CollisionData` and the checksum. `BoxQuery` (Phase 3) and `SegmentQuery` (Phase 7) are later partial files.
- [ ] T057 Implement `CollisionFile` in `src/MmoEngine.SimCore/Formats/CollisionFile.cs` (contracts/collision-data-format.md#layout, #check-order, #api), after T056. Together they make T030 and T031 pass.
  - It has `FormatVersion`, `HeaderLength`, `MaxFileLength` and `Magic`.
  - `Decode` runs checks 1 to 4 and 7 in order, with format `collision` and the contract's messages. It then parses the header, heights, directory, vertices and indices through bounds-checked slices and builds the world. It never returns a partial world.
  - `Encode(CollisionData)` writes the header, body and the XxHash64 trailer, computing `fileLength`. It then decodes its own output, so it throws exactly what `Decode` would.

  Checks 5, 6, 8 and 9 are Phase 8's, and their fixtures are its red tests.
- [ ] T058 Implement `src/MmoEngine.SimCore/Formats/RecordedStream.cs` with `RecordedStep` and `RecordedCommand` (contracts/recorded-stream-format.md#api). Makes T032 pass.
  - It has a public constructor over `rulesRevision`, `stepHz`, `seed`, `matchSalt`, `collisionChecksum`, `SimConfig`, the initial state bytes and the steps. `CharacterCount` comes from the state header.
  - `Decode` runs checks 1–4 and 7, then:
    - check 9 through the internal `ConfigBlock` overload (`stream`, base 64, `config.`);
    - the byte-only half of check 11 through `StateSerializer.ReadLoaded` (`stream`, base `64 + configLength`, `initialState.`);
    - parsing of the step entries, with `InputCodec.TryDecode` for each command.
  - `Encode()` computes the block lengths, `fileLength` and the trailer, then decodes its output.

  Checks 5, 6, 8, 10, the character-count half of 11, 12 and the embedded input errors are Phase 8's.
- [ ] T059 Implement `src/MmoEngine.SimCore/World/SimWorld.cs` (data-model.md#world-simworld and #world-step-order). Makes T034 and T035 pass.
  - The constructor `SimWorld(CompiledConfig config, CollisionWorld collision)` allocates every buffer once from the maxima:
    - current and next characters, weapons (maxCharacters × weaponSlots) and projectiles;
    - inputs and rejection reasons per character;
    - the candidate buffer `int[collision.MaxQueryCandidates((3f * capsuleRadius) + (2f * contactSkin))]`, whose box Phase 3's queries never exceed;
    - hitboxes (maxCharacters × 3) and bounds;
    - the six event buffers sized as in data-model.md;
    - the `byte[MaxStateLength]` state buffer.

    It starts with an empty state: step 0, no characters, `nextProjectileId` 1.
  - `Reset(ReadOnlySpan<byte> canonicalState, ulong matchSalt)` runs `StateSerializer.ReadLoaded` (format `state`), then the configuration and world checks of contracts/state-serialization.md#loading-a-state, and only then copies the state in. On refusal it throws `CoreFormatException` and keeps the previous world.
  - `uint CurrentStep`.
  - `StepReport Step(ReadOnlySpan<CharacterCommand> commands)` runs the stages in order:
    - stage 1 merges the ascending commands with the characters, taking at most one per character, and gives the others the neutral input. Validation is Phase 3's.
    - `StepMovement`, `StepWeapons` and `StepProjectiles` are private methods that copy current to next unchanged; Phases 3, 6 and 7 replace their bodies.
    - stage 4 builds hitboxes with `HitboxBuilder` into scratch;
    - stage 6 calls `StateSerializer.Write` and `StateHash.Compute`;
    - stage 7 swaps next into current and sets the step. A fault commits nothing and returns `Hash` 0 with the previous step.
  - `internal ReadOnlySpan<InputCommand> LastStepInputs`, and internal buffer-size accessors for the tests.
  - `internal StepReport StepWithInjectedBits(ReadOnlySpan<CharacterCommand> commands, int fieldOrdinal, int elementIndex, uint bits)` runs stages 1–5, writes `bits` into that field of the next state, then runs stages 6 and 7. It is visible to `MmoEngine.SimCore.Tests` only, and is never a delegate field.
  - There is no LINQ, lambda, boxing or exception on the step path, and every loop is bounded by a count from the configuration.
- [ ] T060 [P] Implement the CLI infrastructure in `src/MmoEngine.SimCore.Cli/`. Makes T039 pass.
  - `CliExitCodes.cs`: `Success 0`, `Failed 1` (divergence or invalid) and `Usage 2` (usage, refused, not comparable, cannot run).
  - `CliLogging.cs`: `Create(TextWriter stderr)` returns a Serilog logger enriched with `Tool` = `simcore`, written through a `TextWriterSink` with `CompactJsonFormatter`, as `src/MmoEngine.Harness.Cli/Program.cs` does.
  - `AtomicFile.cs`: `WriteAllBytes`, `WriteAllText` and `Write(string path, Action<Stream> write)`. Each writes `<name>.<guid>.tmp` in the target's directory, flushes it, then calls `File.Move(temp, path, overwrite: true)`. On any exception it deletes the temp file and rethrows.
- [ ] T061 Implement the CLI shell's environment check and `config check`. Makes T036, T037 and T038 pass.
  - `src/MmoEngine.SimCore.Cli/Program.cs`: `public static int Run(string[] args, TextWriter stdout, TextWriter stderr)` reads `DOTNET_ENVIRONMENT` and delegates to `internal static int Run(string[] args, TextWriter stdout, TextWriter stderr, string? environmentName)`. That overload:
    - resolves the name with `SimEnvironment.Resolve`. An unknown name writes `DOTNET_ENVIRONMENT: <message>` to stderr and exits 2;
    - creates the logger with `CliLogging`;
    - builds the System.CommandLine root, where a parse error exits 2.
  - `src/MmoEngine.SimCore.Cli/Commands/ConfigCommand.cs`: `config check <file>`.
    - A missing file writes `<path>: the file does not exist` to stderr and exits 2.
    - Otherwise it binds with `SimConfigJson.Load`, then validates with `SimConfigValidator.Errors`, and prints one `path: message` line per binding or validation error to stdout, in rule-table order.
    - It exits 1 when there are errors, and 0 with no output when valid.
    - It logs one Information event with the path and the error count.

**Checkpoint**: These all exit 0:

- `dotnet build MmoEngine.sln -warnaserror`;
- `dotnet format MmoEngine.sln --verify-no-changes`;
- the Independent Test's filter, extended with `|FullyQualifiedName~MmoEngine.SimCore.Tests.Math.Vec3Tests|FullyQualifiedName~MmoEngine.SimCore.Tests.Hashing|FullyQualifiedName~MmoEngine.SimCore.Tests.Batch.StepRecordTests|FullyQualifiedName~MmoEngine.SimCore.Tests.Cli`;
- `dotnet run --project src/MmoEngine.SimCore.Cli -- config check simcore/config/default.json`, with no output.

---

## Phase 3: User Story 1 - A character moves the same way every time (Priority: P1) 🎯 MVP

**Goal**: Validate input commands, step character movement and capsule collision against the heightfield and meshes, run recorded streams, generate the deterministic test world, define and record the 11 movement scenario streams, and give `streams record` and `streams verify`, so a character moves identically in every process at 20, 30 and 60 Hz.

**Independent Test**: `Cli/StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` steps every committed stream through the server build in a child process and in the test process, and every hash equals the committed expected hashes; `Scenarios/MovementScenarioTests` asserts the stated end positions of the wall slide, step-up, slope limit, landing and crouch-under-ledge scenarios at 20, 30 and 60 Hz.

Conventions for this phase (they hold for every task below):

- Every movement Theory runs at `hz` 20, 30 and 60 through `[MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]`, or `TestRates.With(...)` / `TestRates.WithRateLast(...)` when it crosses another parameter.
- "Rest height" means a feet height h above a surface s with `h - s` in `[-contactTolerance, contactSkin + contactTolerance]` (the sweep stops the capsule a skin, 0.01 m, above what it touches; `contactTolerance` is 0.002 m).
- Scenario tests build definitions in process from `ScenarioCatalog` over the world `TestWorldGenerator.Generate()` encodes to (never the committed `.mmcd`), and step them through `SimWorld`. Only the US1.7 and US1.8 tests and `MovementDefinitionTests` read committed `simcore/streams/*.mmrs`, always through `RepoPaths` with `FileAccess.Read`.
- Placeholders from `simcore/config/default.json`: runSpeed 7, walkSpeed 3.5, crouchSpeed 2, groundAccel 70, groundDecel 50, airAccel 8, gravity 9.81, jumpSpeed 4.5, maxFallSpeed 50, capsuleRadius 0.4, standHeight 1.8, crouchHeight 1.2, stepHeight 0.35, snapDistance 0.35, slopeLimitDegrees 45, contactSkin 0.01, contactTolerance 0.002, maxSlideIterations 5, maxSubMoves 8 (contracts/config-rules.md#movement-section-1).

### Tests (write first, confirm red)

- [ ] T062 [US1] Add the Phase 3 test support in `tests/MmoEngine.SimCore.Tests/Support/` (it references types this phase creates, so it compiles once their green tasks land; until then the project's compile failure is the red state):
  - `MovementWorlds.cs`: static builders, each a `CollisionData` passed through `CollisionFile.Encode` then `CollisionFile.Decode`, returning `MovementWorld(CollisionWorld World, Vec3 Start, ushort Yaw, ...)` with the geometry tests assert against (plane normal and offset, top height, front edge, foot point): `Flat()` (64 m square, heights 0), `Wall(float angleDegrees)` (a zero-thickness vertical mesh quad 3 m ahead of the start, at that angle to the start heading), `Stair(float rise)` (a 2 m deep box step of that rise whose front face is 2 m ahead), `Slope(float degrees)` (a heightfield flat for 4 m, then rising at that angle along +Z for 6 m, then flat), `LowLedge(float underside)` (a horizontal mesh slab 4 m deep over a flat floor, underside at that height), `FacetedConcaveWall(float facetDegrees)` (three 4 m vertical facets, each turned `facetDegrees` from the last, concave toward the start), `SteepFace(float height, float degrees)` (a mesh face of that height and angle meeting a flat floor at its foot), `Corner()` (two vertical walls meeting at 90 degrees, concave toward the start), `ThinWall()` (one zero-thickness quad).
  - `ScenarioRuns.cs`: `ScenarioRun Run(string scenario, int hz)` builds `ScenarioCatalog.Get(scenario).Build(TestConfigs.Default, TestWorlds.TestWorld, hz)` (add `TestWorlds.TestWorld`, the lazily decoded `CollisionFile.Encode(TestWorldGenerator.Generate())`), compiles its config, resets a `SimWorld` with its initial state and match salt, steps every entry's commands, and keeps per step the decoded character records (Phase 2's `StateSerializer` reader over `StepReport.State`), the six event lists, `StepReport.Counters` and `Fault`; also `Run(SimWorld world, RecordedStream stream)`.
  - `StreamBytes.cs`: `ulong Trailer(ReadOnlySpan<byte> file)` (the last 8 bytes, LE) and `byte[] WithExpectedHash(byte[] stream, int stepIndex, ulong hash)`, which walks the steps block per contracts/recorded-stream-format.md#steps-block, rewrites that entry's `expectedHash` and recomputes the XxHash64 (seed 0) trailer.
  - `SimcoreCli.cs`: add `RunInFreshProcess(params string[] args) -> (int Exit, string Stdout, string Stderr)`, starting `dotnet <AppContext.BaseDirectory>/simcore.dll <args>` with `WorkingDirectory = RepoPaths.Root` and `DOTNET_ENVIRONMENT=Test`, a 5-minute timeout that kills the process and fails naming the arguments.
- [ ] T063 [P] [US1] Write failing test `InputValidatorTests.ValidateReportsTheFirstFailureInContractOrder(uint sequence, uint lastSequence, ushort buttons, short pitch, byte slot, InputRejection expected)` in `tests/MmoEngine.SimCore.Tests/Inputs/InputValidatorTests.cs` (FR-019, contracts/input-command-format.md#validation): Given `weaponSlots` 2, When `InputValidator.Validate(new InputCommand(sequence, 0, (Buttons)buttons, 0, pitch, slot), lastSequence, 2)` is called, assert exactly `expected` for the rows (5, 5, 0x0200, 20000, 9) → `SequenceNotIncreasing` (sequence checked first); (4, 5, 0, 0, 0) → `SequenceNotIncreasing`; (6, 5, 0x0200, 20000, 9) → `UnknownButtons`; (6, 5, 0x8000, 0, 0) → `UnknownButtons`; (6, 5, 0x01FF, 16385, 9) → `PitchOutOfRange`; (6, 5, 0, -16385, 0) → `PitchOutOfRange`; (6, 5, 0, 16384, 2) → `UnknownWeaponSlot`; (6, 5, 0x01FF, 16384, 1) → `None`; (6, 5, 0, -16384, 0) → `None`; (1, 0, 0, 0, 0) → `None`; (0, 0, 0, 0, 0) → `SequenceNotIncreasing`; and (n, 0xFFFFFFFF, 0, 0, 0) → `SequenceNotIncreasing` for n in {0, 1, 0x7FFFFFFF, 0xFFFFFFFF}. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~InputValidatorTests"` and confirm it fails.
- [ ] T064 [P] [US1] Write failing test `InputRejectionTests.InvalidCommandIsRejectedWithItsReasonAndLeavesTheStateUnchanged(InputRejection reason, int hz)` in `tests/MmoEngine.SimCore.Tests/Inputs/InputRejectionTests.cs` (US1.9): Given two `SimWorld`s A and B over `MovementWorlds.Flat()` at `TestConfigs.At(hz)`, each reset to the same one-character state (ID 1, grounded at rest height, `aimYaw` 1000, `aimPitch` 0, `lastSequence` 100, `activeSlot` 0), When A steps with one command for index 0 that carries Forward and Jump plus the reason's fault (rows `SequenceNotIncreasing`: sequence 100; `UnknownButtons`: buttons 0x0211; `PitchOutOfRange`: pitch 16385; `UnknownWeaponSlot`: slot 2; every other field valid, aimYaw 12345) and B steps with no command, assert A's `Events.InputRejections` is exactly `[{CharacterId 1, Reason reason}]` with `(byte)reason` 1, 2, 3 or 4; A's state bytes before the events block equal B's byte for byte; A's events block equals B's except `events.inputRejections.count` 1 and that one 5-byte record (`01 00 00 00 <reason>`); and A's character has `aimYaw` 1000, `lastSequence` 100, `activeSlot` 0, `Position` unchanged in X and Z and Grounded set (Jump not applied). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~InputRejectionTests"` and confirm it fails.
- [ ] T065 [P] [US1] Write failing test `ClosestPointsTests.MatchDoubleReferences(string query)` in `tests/MmoEngine.SimCore.Tests/Collision/ClosestPointsTests.cs` (FR-003, research R7), rows `point-triangle`, `segment-segment`, `segment-triangle`: Given 25,000 cases per row from CsCheck with a declared fixed seed (printed on failure), including near-parallel segments (sin²θ just above and below 1e-10), degenerate-direction segments, and coordinates offset by up to ±7,000 m in X and Z, When `ClosestPoints.PointTriangle`, `ClosestPoints.SegmentSegment` and `ClosestPoints.SegmentTriangle` (Ericson 5.1.5, 5.1.9, 5.1.10, local origin snapped to the 64 m lattice) are evaluated, assert each returned distance is within 1e-6 m of a double-precision reference written in the test, and the returned points lie on their primitives within 1e-6 m. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ClosestPointsTests"` and confirm it fails.
- [ ] T066 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Tests/Collision/BoxQueryTests.cs` (data-model.md#collisionworld, FR-004): `BoxQueryTests.CandidatesAreAscendingWithoutDuplicates(string box)`, rows over `TestWorlds.TinyWorld` (whole world; the quad's cell only; a box that misses every corner height) and a world built in the test whose single mesh triangle spans four 4 m grid cells: Given the world, When `CollisionWorld.QueryBox(minX, minY, minZ, maxX, maxY, maxZ, Span<int> output)` runs with a 64-slot span, assert the returned count c ≥ 0, `output[..c]` is strictly ascending, contains the spanning triangle's ID exactly once, contains every mesh triangle whose AABB overlaps the box and every heightfield triangle of an overlapping cell whose corner-height range overlaps the box's Y range, and nothing else (the dropped degenerate ID 10 never appears); and `BoxQueryTests.OverflowRefusesTheStepWithQueryCapacityExceeded`: `QueryBox` over all of tiny-world into a 1-slot span returns -1, and a `SimWorld` built with the internal constructor `SimWorld(CompiledConfig, CollisionWorld, int candidateCapacity)` with capacity 1 on tiny-world, stepped once, reports `Fault.Code == StepFaultCode.QueryCapacityExceeded` with element index 0, `Hash == 0`, `Step` equal to the previous step, and leaves `CurrentStep`, the state bytes and the hash as before. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~BoxQueryTests"` and confirm it fails.
- [ ] T067 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Tests/Collision/CapsuleSweepTests.cs` (FR-003, research R7): `CapsuleSweepTests.NeverPassesTheTrueTimeOfImpact`: Given 5,000 CsCheck sweeps with a declared fixed seed (printed on failure) of a radius-0.4 capsule against single triangles, When `CapsuleSweep.Sweep` returns a `SweepHit`, assert its `T` ≤ the double-precision reference time at which the axis comes within r + skin (bisection in the test) plus 1e-6, and the axis-to-triangle distance at `T` (double reference) ≥ r; `CapsuleSweepTests.TiesGoToTheLowerIdAndBoundaryPlanesWin`: two coincident mesh triangles (IDs 8 and 9) give `TriangleId` 8, and a mesh wall lying exactly on the playable area's +X edge gives the boundary plane's negative ID (-4 to -1) instead of the mesh ID; `CapsuleSweepTests.TrianglesAreDoubleSided`: sweeping at a vertical quad from its front and from its back both hit, with `T` bit-identical for mirrored starts and the contact normals opposite and each facing the capsule. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CapsuleSweepTests"` and confirm it fails.
- [ ] T068 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Tests/Movement/MovementStepTests.cs` (FR-002, data-model.md#movement-step): `MovementStepTests.WishDirectionFollowsTheButtons(Buttons buttons, Stance stance)`, one row per combination of Forward, Back, Left, Right and Walk (32) × Standing, Crouched: `MovementStep.WishVelocity(buttons, stance, sinYaw 0f, cosYaw 1f, config)` equals bit for bit `((F - B) * (0, 0, 1) + (R - L) * (-1, 0, 0))`, times `0.70710677f` when both net axes are non-zero, times crouchSpeed 2 when crouched, else walkSpeed 3.5 when Walk is held, else runSpeed 7 (opposite buttons cancel to (0, 0, 0)); `MovementStepTests.HorizontalVelocityMovesTowardTheTargetByAtMostRateTimesDt(string mode, int hz)`, rows grounded-wish (groundAccel), grounded-none (groundDecel), airborne-wish (airAccel), airborne-none (0), on `MovementWorlds.Flat()` from velocity (1, 0, 0) toward a run target along +Z: after one `MovementStep.Step` the horizontal velocity change has length ≤ rate · Dt + 1e-6 and equals the target exactly when the target was within rate · Dt; `MovementStepTests.SubMoveCountIsClampedCeilOfDistanceOverRadius(float distance, int expected)`, rows (0, 1), (0.4, 1), (0.41, 2), (1.2, 3), (100, 8): `MovementStep.SubMoveCount(distance, 0.4f, 8)`; `MovementStepTests.GroundProjectionKeepsHorizontalSpeed(int hz)`: grounded mid-slope on `MovementWorlds.Slope(30)` with Forward held up-slope and the velocity already at the run target, one step moves the feet horizontally by runSpeed · Dt within 1e-5 m, raises them by that distance · tan 30° within contactTolerance, keeps `Velocity.x` and `Velocity.z` bit-equal to the target, and ends Grounded. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementStepTests"` and confirm it fails.
- [ ] T069 [P] [US1] Write failing test `StepUpTests.RisesUpToTheStepHeightClimbAndHigherRisesBlock(int hz, float rise, bool climbs)` in `tests/MmoEngine.SimCore.Tests/Movement/StepUpTests.cs` (US1.3 supporting, FR-002), rows rise 0.10 and 0.34 climb, 0.36 and 0.50 block, each at 20, 30 and 60 Hz: Given `MovementStepTests`' grounded character on `MovementWorlds.Stair(rise)` with Forward held for 2 simulated seconds, assert for climbing rows the last step is Grounded with `Position.y` at rest height on the stair top and `Position` past the front edge by more than capsuleRadius, and no single step raises `Position.y` by more than stepHeight + contactSkin; for blocking rows `Position.y` stays at rest height on the floor at every step and the axis never comes nearer the stair face than capsuleRadius - contactTolerance. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StepUpTests"` and confirm it fails.
- [ ] T070 [P] [US1] Write failing test `SlopeTests.SlopesBelowTheLimitClimbAndSteeperAreRefused(int hz, float degrees, bool climbs)` in `tests/MmoEngine.SimCore.Tests/Movement/SlopeTests.cs` (US1.4 supporting, FR-002), rows 30 and 44 climb, 46, 50 and 70 refused, at 20, 30 and 60 Hz: Given a grounded character at the foot of `MovementWorlds.Slope(degrees)` with Forward held up-slope for 3 simulated seconds, assert for climbing rows it is Grounded at every step and ends on the top flat at rest height; for refused rows, whenever Grounded its `GroundNormal.y` ≥ `CosSlopeLimit`, and `Position.y` never exceeds the foot height + stepHeight. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SlopeTests"` and confirm it fails.
- [ ] T071 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Tests/Movement/SlideTests.cs` (research R8 slide rules, FR-002): `SlideTests.FallingOntoASteepFaceSlidesToItsFoot(int hz, float degrees)`, rows 50 and 70 at 20, 30 and 60 Hz: Given a character released at rest, airborne (`groundNormal` exactly (0, 1, 0)), 0.5 m above `MovementWorlds.SteepFace(2f, degrees)`, with no input, When stepped, assert it is Grounded on the floor at the face's foot, at rest height, within ⌈2 · (√(1/g) + √(2h/g) / sin θ) · hz⌉ steps (g 9.81, h 2), `Position.y` never rises from one step to the next, and it is never Grounded with `GroundNormal.y` < `CosSlopeLimit`; `SlideTests.FacetedConcaveWallKeepsItsParallelSpeed(int hz, string approach)`, rows parallel and 45-degree, on `MovementWorlds.FacetedConcaveWall(10)` at run speed: in the step whose slide reaches each join, the horizontal speed after the step ≥ cos 10° × the speed at its start - 1e-4 m/s, the horizontal speed is never 0 after contact, and no step reports `MovementOutcome.SlideCapReached`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SlideTests"` and confirm it fails.
- [ ] T072 [P] [US1] Write failing test `CornerTests.StartsOnATriangleEdgeOrVertexNeverTunnelOrOscillate(string start, int hz)` in `tests/MmoEngine.SimCore.Tests/Movement/CornerTests.cs` (edge case: edge, vertex, corner), rows heightfield-diagonal-edge, heightfield-shared-vertex, mesh-edge, mesh-vertex: Given a character whose capsule bottom starts exactly on that feature of `MovementWorlds.Corner()` or of a heightfield with a 0.25 m bump, When each of the eight movement directions is held for 60 steps (one sub-case per direction), assert no step ends with the axis nearer any triangle than capsuleRadius - contactTolerance (double reference distance), no step faults, and the per-step positions never contain an A, B, A, B alternation of bit-identical positions with A ≠ B. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CornerTests"` and confirm it fails.
- [ ] T073 [P] [US1] Write failing test `BoundsTests.CharacterPushedPastTheEdgeIsClampedAndReported(string approach, int hz)` in `tests/MmoEngine.SimCore.Tests/Movement/BoundsTests.cs` (edge case: out-of-bounds clamp), rows run-at-plus-x-edge, run-at-minus-z-edge, run-into-corner: Given a `SimWorld` on `MovementWorlds.Flat()` with the character 1 m inside the playable area running outward, When stepped 30 steps, assert `Position.x` and `Position.z` stay within [OriginX, OriginX + (Columns - 1) · CellSize] and [OriginZ, OriginZ + (Rows - 1) · CellSize] at every step, and on the first step that reaches the edge the state's `flags` has bit 1 (ClampedToBounds, 0x02) set and `Events.MovementOutcomes` holds `{CharacterId 1, Flags}` with bit value 8 (`MovementOutcome.ClampedToBounds`) set. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~BoundsTests"` and confirm it fails.
- [ ] T074 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Tests/Properties/MovementPropertyTests.cs` (FR-001, FR-003, FR-005), each a CsCheck property with a declared fixed seed printed on failure, crossed with hz 20, 30 and 60, over fuzz worlds generated in the test (heightfields with slopes up to 60°, random wall, box and ramp meshes) with one to four characters and random valid input commands for 500 steps per sample: `MovementPropertyTests.NeverOverlapsByMoreThanTheContactTolerance` (after every step every capsule axis is at least capsuleRadius - contactTolerance from every triangle, double reference); `MovementPropertyTests.NeverGetsStuckOnAFuzzWorld` (no step reports `MovementOutcome.Stuck` and no step faults); `MovementPropertyTests.StepUpNeverRisesMoreThanTheStepHeight` (in any step that starts and ends Grounded, the rise of `Position.y` ≤ stepHeight + horizontal displacement · tan(slopeLimit) + contactSkin); `MovementPropertyTests.EveryLoopStaysWithinItsBound` (`StepReport.Counters` is the sum over the step's characters, T097, so with characterCount the number of characters in the sample, every step has `Counters.SubMoves` ≤ characterCount · maxSubMoves and `Counters.Sweeps` ≤ `Counters.SubMoves` · (2 · maxSlideIterations + 4) + characterCount; the per-character bound is also asserted directly by calling `MovementStep.Step` on a copy of each character's pre-step state with the step's command, the sample's `CollisionWorld` and a candidate buffer `int[collision.MaxQueryCandidates((3f * capsuleRadius) + (2f * contactSkin))]`, and checking `MovementResult.SubMoves` ≤ maxSubMoves and `MovementResult.Sweeps` ≤ `MovementResult.SubMoves` · (2 · maxSlideIterations + 4) + 1). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementPropertyTests"` and confirm it fails.
- [ ] T075 [P] [US1] Write failing test `TestWorldGeneratorTests.TheTestWorldHasEveryFeatureTheScenariosNeed` in `tests/MmoEngine.SimCore.Tests/Scenarios/TestWorldGeneratorTests.cs` (spec Assumptions "Test content"): Given `TestWorldGenerator.Generate()` encoded and decoded, assert `DegenerateTriangleCount` 0; the heightfield holds a walkable 30° slope and a steep 50° slope rising from the plateau at its top (face normal y within 1e-4 of cos 30° and cos 50°), a ridge and a cliff (a cell whose heights drop by at least 3 m); the meshes hold a vertical wall (normal y == 0), a stair of rise 0.30 m and one of 0.50 m (one below and one above stepHeight 0.35), a ledge whose underside is 1.5 m above the floor (between crouchHeight 1.2 and standHeight 1.8), two walls meeting at 90 degrees, and a single zero-thickness quad; every location in `TestWorldGenerator.Features` lies inside the playable area at rest height; and two calls give byte-identical `CollisionFile.Encode` output. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~TestWorldGeneratorTests"` and confirm it fails.
- [ ] T076 [US1] Write failing test `MovementScenarioTests.FlatRunFromRestCoversTheDiscreteSum(int hz, double expectedMetres)` in `tests/MmoEngine.SimCore.Tests/Scenarios/MovementScenarioTests.cs` (US1.1), rows (20, 6.825), (30, 6.766667), (60, 6.708333): Given `ScenarioRuns.Run("flat-run-<hz>hz", hz)` (one character at rest, Grounded, on flat terrain, aim yaw 0, runSpeed 7, groundAccel 70), When stepped hz steps with Forward held, assert the displacement along the aim heading equals `expectedMetres` within 1e-4 m and equals Σₖ₌₁ᴺ min(k · a · dt, v) · dt computed in double within 1e-4 m, the lateral displacement is within 1e-4 m of 0, every step is Grounded, and the final state's `flags` has bit 0 (Grounded) set. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.FlatRunFromRestCoversTheDiscreteSum"` and confirm it fails.
- [ ] T077 [US1] Write failing test `MovementScenarioTests.WallSlideNeverEntersTheWallAndKeepsTheParallelSpeed(int hz)` in the same file (US1.2): Given `ScenarioRuns.Run("wall-slide-45", hz)` (the character reaches run speed 3 m before the test world's wall, heading 45° into it), assert at every step the capsule axis is at least capsuleRadius - contactTolerance from the wall plane; from the step after first contact the horizontal velocity's component along the wall normal is within 1e-4 m/s of 0, its magnitude is 7 · cos 45° = 4.9497476 within 1e-4 m/s, and the position along the wall increases at every step. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.WallSlide"` and confirm it fails.
- [ ] T078 [US1] Write failing test `MovementScenarioTests.StairBelowTheStepHeightIsClimbedAndAboveItIsBlocked(string scenario, int hz)` in the same file (US1.3), rows `stairs-climb` and `stairs-blocked` × 20, 30, 60: for `stairs-climb` (the 0.30 m stair) the last step is Grounded with `Position.y` at rest height on the stair top and the feet past its front edge by more than capsuleRadius; for `stairs-blocked` (the 0.50 m stair) whenever Grounded `Position.y` is at rest height on the floor, and the axis never comes nearer the stair face than capsuleRadius - contactTolerance. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.StairBelow"` and confirm it fails.
- [ ] T079 [US1] Write failing test `MovementScenarioTests.SlopeBelowTheLimitIsClimbedAndAboveItIsRefused(int hz)` in the same file (US1.4): Given `ScenarioRuns.Run("slope-limit", hz)` (Forward held up the 30° slope onto the plateau and on into the 50° slope), assert every step on the walkable slope is Grounded and the character reaches the plateau at rest height; afterwards it is never Grounded with `GroundNormal.y` < `CosSlopeLimit`, and `Position.y` never exceeds the plateau height + stepHeight (0.35 m). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.SlopeBelow"` and confirm it fails.
- [ ] T080 [US1] Write failing test `MovementScenarioTests.JumpLeavesWithJumpSpeedFollowsGravityAndLandsGrounded(int hz)` in the same file (US1.5): Given `ScenarioRuns.Run("jump-land", hz)` (Jump pressed once while Grounded on flat terrain), assert the jump step clears Grounded and its `velocity.y` bits equal `jumpSpeed - GravityPerStep` evaluated in float (4.5f - 9.81f / hz as `CompiledConfig` computes it); every later airborne step's `velocity.y` equals the previous one minus `GravityPerStep` exactly, clamped at -maxFallSpeed; `Position.y` is never below `HeightAt(x, z) - contactTolerance`; and the landing step sets Grounded with `Position.y` at rest height and `velocity.y == 0f`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.JumpLeaves"` and confirm it fails.
- [ ] T081 [US1] Write failing test `MovementScenarioTests.CrouchedUnderALedgeStaysCrouchedUntilThereIsRoomToStand(int hz)` in the same file (US1.6): Given `ScenarioRuns.Run("crouch-under-ledge", hz)` (Crouch held while walking under the 1.5 m ledge, released under it, Forward held until clear), assert every step from the release until the headroom clears has `stance` 1 (Crouched) and a movement outcome record with bit value 1 (`HeadroomBlocked`); the first step whose standing segment (crouchHeight - r to standHeight - r above the feet) is more than r + contactSkin / 2 from the ledge has `stance` 0 (Standing); and the capsule never comes nearer the ledge than capsuleRadius - contactTolerance. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.CrouchedUnder"` and confirm it fails.
- [ ] T082 [US1] Write failing test `MovementScenarioTests.CornerWedgeNeverTunnelsOrOscillates(int hz)` in the same file (edge case: concave corner): Given `ScenarioRuns.Run("corner-wedge", hz)` (Forward held diagonally into the 90° corner), assert no step ends with the axis nearer either wall than capsuleRadius - contactTolerance, the per-step positions contain no A, B, A, B alternation with A ≠ B, the last 10 steps' positions are bit-identical, and every step's `Counters.Sweeps` ≤ `Counters.SubMoves` · (2 · maxSlideIterations + 4) + 1. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.CornerWedge"` and confirm it fails.
- [ ] T083 [US1] Write failing test `MovementScenarioTests.FastMoveIntoAThinWallIsSubdividedAndNeverPassesThrough(int hz)` in the same file (edge case: subdivided step): Given `ScenarioRuns.Run("thin-wall-fast", hz)` (overrides runSpeed 20, groundAccel 1000, capsuleRadius 0.25, maxSubMoves 16, so 20 / hz > 0.25 m per step at every rate), assert every step whose start horizontal speed · Dt exceeds capsuleRadius has `Counters.SubMoves` ≥ 2, at least one such step occurs at each rate, and the signed distance of the feet to the thin wall's plane never changes sign. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementScenarioTests.FastMove"` and confirm it fails.
- [ ] T084 [P] [US1] Write failing test `MovementDefinitionTests.EachMovementDefinitionBuildsAtEveryRateAndMatchesItsCommittedStream(string name, int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/MovementDefinitionTests.cs` (FR-028, FR-022), rows the 11 movement names × 20, 30, 60: Given `ScenarioCatalog.Get(name)`, When built at hz, assert `StepHz == hz`, the config's `stepRateHz == hz`, every expected hash is 0, and the step count equals the definition's duration in seconds × hz; and When built with no rate and run through `StreamRunner.Run`, assert its header fields (rate, character count, seed, match salt, collision checksum), config entries, initial state bytes and every step's commands equal the committed `simcore/streams/<name>.mmrs`'s, and the run's per-step hashes equal that file's expected hashes. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MovementDefinitionTests"` and confirm it fails.
- [ ] T085 [P] [US1] Write failing test `StreamDeterminismTests.StreamForEachRateGivesIdenticalHashesTwice(int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/StreamDeterminismTests.cs` (US1.8): Given the committed `simcore/streams/flat-run-<hz>hz.mmrs` (header `stepHz == hz`) and `simcore/content/test-world.mmcd`, When `StreamRunner.Run` runs it twice, assert both outputs are byte-identical, hold hz records, and each record's hash equals that step's expected hash; and When `ScenarioCatalog.Get("flat-run-<hz>hz").Build(..., hz)` is built twice, assert identical `Encode()` bytes and byte-identical `StreamRunner.Run` records. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamDeterminismTests"` and confirm it fails.
- [ ] T086 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Tests/Batch/StreamRunnerTests.cs` (contracts/wasm-api.md#streamrunner, contracts/state-serialization.md#step-record): `StreamRunnerTests.RecordsCarryTheExpectedHashes(int hz)`: Given a 10-step one-character stream built in the test on `TestWorlds.Flat` (commands with increasing sequences and Forward on odd steps) whose expected hashes come from stepping a `SimWorld` directly with the same commands, When `StreamRunner.Run(stream.Encode(), CollisionFile.Encode(flatData))` runs, assert 10 records back to back, record i has `step == initialStep + i + 1`, `stateLength` equal to the state's length, state bytes equal to the direct `StepReport.State`, hash == `StateHash.Compute(state)` == the expected hash, and the total length is Σ(16 + stateLength); `StreamRunnerTests.BothOverloadsGiveTheSameRecords`: the byte overload and `Run(stream, new[] { world })` return identical bytes; `StreamRunnerTests.PerturbStepOutsideTheStreamThrows`: `perturbStep: 999999` throws `ArgumentOutOfRangeException` and returns no records; `StreamRunnerTests.StepFaultExceptionNamesTheStepCodeAndPath`: a `StepFault` taken from `SimWorld.StepWithInjectedBits` with NaN bits (0x7FC00000) at `characters[0].velocity.y`, wrapped as `new StepFaultException(7, fault)`, has `Step` 7, that `Fault`, and `Message` exactly `step 7 faulted: NonFinite at characters[0].velocity.y`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamRunnerTests"` and confirm it fails.
- [ ] T087 [P] [US1] Write failing tests in `tests/MmoEngine.SimCore.Tests/Cli/ContentCommandTests.cs` (contracts/simcore-cli.md#content-generate): `ContentCommandTests.ContentGenerateReproducesTheCommittedWorld`: Given a unique temp directory, When `SimcoreCli.Run("content", "generate", "--out", <tmp>/test-world.mmcd)` runs, assert exit 0, stdout exactly `<tmp>/test-world.mmcd: <n> bytes, checksum <x16>` where n is the committed `simcore/content/test-world.mmcd`'s length and x16 its trailer, and the written bytes equal the committed file's; `ContentCommandTests.ContentGenerateExitsTwoWhenTheOutputCannotBeWritten`: `--out` under a path whose parent is a regular file exits 2 and writes nothing. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ContentCommandTests"` and confirm it fails.
- [ ] T088 [US1] Write failing test `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` in `tests/MmoEngine.SimCore.Tests/Cli/StreamsCommandTests.cs` (US1.7, and with `MovementScenarioTests`' US1.2 to US1.6 end positions the US1 Independent Test): Given the committed `simcore/streams/*.mmrs` (n files) and `simcore/content/`, When `SimcoreCli.RunInFreshProcess("streams", "verify", "--dir", <root>/simcore/streams, "--content", <root>/simcore/content)` runs in a child process, assert exit 0, one line `<name>: match, <steps> steps, final <x16>` per file in ordinal name order, and the last line `streams verify: <n> streams, <n> match, 0 diverged, 0 not comparable, 0 refused`; and in the test process `StreamRunner.Run` on each file gives per-step hashes equal to that file's expected hashes, whose last equals the child's printed final hash. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamsCommandTests.EveryCommittedStream"` and confirm it fails.
- [ ] T089 [US1] Write failing `streams record` tests in the same file (FR-028, contracts/simcore-cli.md#streams-record), each on copies of `simcore/streams`, `simcore/content` and `simcore/config` in a unique temp directory, passing `--out`, `--content` and `--config` explicitly:
  - `StreamsCommandTests.RecordAppendsTheReasonAndOldAndNewHashes(string kind)`, rows `changed` (the temp config's `movement.runSpeed` set to 6.5, then `--scenario flat-run-30hz --reason "Test regeneration"`) and `added` (the temp `stairs-climb.mmrs` deleted, then `--scenario stairs-climb`): exit 0; stdout `flat-run-30hz: changed; file <old trailer> -> <new trailer>; final state <old last hash> -> <new last hash>; first changed step 3` or `stairs-climb: added; file <x16>; final state <x16>`, then `streams record: 1 streams, 1 changed, 0 added, 0 removed, 0 unchanged` or `... 0 changed, 1 added, ...`; the temp `REGENERATIONS.md` keeps its old text as a prefix and appends `## <UTC>` matching `^## \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$`, `- Reason: Test regeneration`, ``- Command: `simcore streams record --scenario <name>` ``, `- Rules revision: 1`, the table header and the row `| flat-run-30hz | <old> -> <new> | <old> -> <new> | 3 |` or `| stairs-climb | added: <x16> | added: <x16> | |`.
  - `StreamsCommandTests.RecordAllRemovesStreamsNoLongerInTheCatalog`: a copy of `flat-run-30hz.mmrs` named `old-scenario.mmrs` in the temp dir, `--all --reason "Remove old"`: exit 0, line `old-scenario: removed; file <x16>`, the file is gone, the total line matches `, 0 changed, \d+ added, 1 removed, ` and the entry has the row `| old-scenario | removed: <x16> | removed | |`.
  - `StreamsCommandTests.RecordRefusesAMissingEmptyLongOrMultiLineReason(string? reason)`, rows missing, `""`, `"   "`, 501 × `a`, `"a\nb"`: exit 2, stderr names `--reason`, every file in the temp dir byte-identical to before.
  - `StreamsCommandTests.RecordRefusesBothOrNeitherSelectorAndUnknownNames(string caseName)`, rows both (`--scenario flat-run-30hz --all`), neither, unknown (`--scenario no-such-scenario`): exit 2, nothing written, and for unknown the stderr lists every name of `ScenarioCatalog.Names`.
  - `StreamsCommandTests.RecordRefusesAnInvalidConfigurationOrMissingContent(string caseName)`, rows the temp config's `movement.gravity` set to 0 (stderr `movement.gravity: must be between 0.1 and 100`) and an empty `--content` directory: exit 2, nothing written.
  - `StreamsCommandTests.RecordLeavesUnchangedStreamsAndRegenerationsUntouched`: `--scenario wall-slide-45 --reason "No change"`: exit 0, stdout `wall-slide-45: unchanged; file <x16>; final state <x16>` and `streams record: 1 streams, 0 changed, 0 added, 0 removed, 1 unchanged`, and the `.mmrs` and `REGENERATIONS.md` bytes and last-write times unchanged.
  Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamsCommandTests.Record"` and confirm they fail.
- [ ] T090 [US1] Write failing `streams verify` tests in the same file (contracts/simcore-cli.md#streams-verify), each on a unique temp directory:
  - `StreamsCommandTests.VerifyReportsADivergenceAgainstTheExpectedHashes`: copies of the committed streams with `jump-land.mmrs` rewritten by `StreamBytes.WithExpectedHash(bytes, 11, 0x0123456789abcdef)`: exit 1, stdout holds `DIVERGED stream jump-land, build server, first divergent step 12, against the expected hashes` followed by `  hash  expected 0123456789abcdef  server <x16 of the real hash>`, and the last line is `streams verify: <n> streams, <n - 1> match, 1 diverged, 0 not comparable, 0 refused`.
  - `StreamsCommandTests.VerifyRefusesAStreamWhoseCollisionFileIsMissing`: only `flat-run-30hz.mmrs` and an empty content directory: exit 2, line `REFUSED stream flat-run-30hz: stream collisionChecksum at offset 36: collision data <x16 of the test world's checksum> was not supplied`, last line `streams verify: 1 streams, 0 match, 0 diverged, 0 not comparable, 1 refused`.
  - `StreamsCommandTests.VerifyExitsOneWhenADivergenceAndARefusalBothOccur`: the diverged `jump-land.mmrs` plus a stream whose collision file is absent: exit 1, both lines printed.
  - `StreamsCommandTests.VerifyOfAnEmptyDirectoryExitsTwo(string caseName)`, rows empty directory (stdout `streams verify: 0 streams, 0 match, 0 diverged, 0 not comparable, 0 refused`) and missing directory: exit 2.
  Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamsCommandTests.Verify"` and confirm they fail.
- [ ] T091 [P] [US1] Write failing test `StreamWriterTests.OnlyTheRecordCommandWritesStreamFiles` in `tests/MmoEngine.SimCore.Tests/Architecture/StreamWriterTests.cs` (FR-028, contracts/recorded-stream-format.md#regeneration): Given every `src/**/*.cs` and `tests/**/*.cs` outside `bin` and `obj`, read through `RepoPaths` with `FileAccess.Read`, assert the text `StreamFiles.WriteCommitted(` and `StreamFiles.AppendRegenerations(` each occurs exactly once in `src/`, both in `src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs`, the literal `REGENERATIONS.md` occurs in no `src/` file other than `src/MmoEngine.SimCore.Cli/Commands/StreamFiles.cs`, and no test file other than this one contains `StreamFiles.`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamWriterTests"` and confirm it fails.

### Implementation

- [ ] T092 [P] [US1] Implement `InputValidator.Validate(in InputCommand command, uint lastSequence, int weaponSlots)` in `src/MmoEngine.SimCore/Inputs/InputValidator.cs`: checks in contract order `SequenceNotIncreasing` (`sequence <= lastSequence`), `UnknownButtons` (`(buttons & 0xFE00) != 0`), `PitchOutOfRange` (outside [-16384, 16384]), `UnknownWeaponSlot` (`slot >= weaponSlots`), else `None`; no allocation, no exceptions (contracts/input-command-format.md#validation). Makes T063 pass.
- [ ] T093 [P] [US1] Implement `ClosestPoints` in `src/MmoEngine.SimCore/Geometry/ClosestPoints.cs`: `PointTriangle`, `SegmentSegment` and `SegmentTriangle` per Ericson 5.1.5, 5.1.9 and 5.1.10 with a fixed candidate order and strict `<`, the parallel threshold sin²θ < 1e-10, and `LocalOrigin(float x, float z)` snapping to the 64 m lattice (subtracted before, added after); float only, `SimMath` for every square root, `Vec3`'s parenthesisation (research R6, R7). Makes T065 pass.
- [ ] T094 [P] [US1] Implement the box query as a partial of `CollisionWorld` in `src/MmoEngine.SimCore/Collision/CollisionWorld.BoxQuery.cs`: `public int QueryBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ, Span<int> output)` visits the heightfield cells overlapping the box (j outer, i inner, both triangle IDs, skipping a cell whose four corner heights miss [minY, maxY]), then the grid cells through the one clamped cell function, keeping a mesh triangle only in the cell holding `(max(tri.minX, minX), max(tri.minZ, minZ))` and only when its AABB overlaps the box; output ascending without duplicates; returns -1 and writes no count when the span would overflow, never truncating (data-model.md#collisionworld). Makes the box-query half of T066 pass.
- [ ] T095 [US1] Implement `SweepHit` (readonly struct: `Hit`, `T`, `TriangleId` with -4 to -1 for the boundary planes, `Normal` (contact normal), `FaceNormal`, `IsFaceContact`, `ContactPoint`) in `src/MmoEngine.SimCore/Collision/SweepHit.cs` and `CapsuleSweep.Sweep(CollisionWorld world, Vec3 axisBottom, Vec3 axisTop, float radius, float skin, Vec3 displacement, Span<int> candidates, out int queryCount)` in `src/MmoEngine.SimCore/Collision/CapsuleSweep.cs`: candidates from `QueryBox` over the swept box, culled by AABB and plane; conservative advancement on f(t) = dist(axis at t, triangle) - (r + skin) with at most 8 iterations, stop tolerance 0.1 · skin, minimum approach 1e-4 · |d|, the axis recomputed from the start each iteration; the four boundary planes tested as walls; minimum t kept with ties to the lower ID; triangles double-sided; face contacts use the exact face normal, edge and vertex contacts the direction between the closest points; a -1 query count is passed up as overflow (research R7). Makes T067 pass.
- [ ] T096 [US1] Implement `MovementResult` (readonly struct: `Outcome` (`MovementOutcome`), `SubMoves`, `Sweeps`, `SlideCapHits`, `Fault` (`StepFaultCode`)) in `src/MmoEngine.SimCore/Movement/MovementResult.cs` and `MovementStep` in `src/MmoEngine.SimCore/Movement/MovementStep.cs`: `public static MovementResult Step(ref CharacterState character, in InputCommand input, CompiledConfig config, CollisionWorld world, Span<int> candidates)` running sub-steps 1 to 8 of data-model.md#movement-step (apply input with `SimMath.SinCosBinaryAngle`; stance with the headroom segment test at r + contactSkin / 2 and `HeadroomBlocked`; wish velocity; horizontal approach by rate · Dt; vertical velocity with jump and `GravityPerStep` clamped at -maxFallSpeed; ground projection; n = `SubMoveCount` sub-moves each with push-out (4 iterations, threshold `PushOutThreshold`, to r + contactSkin, `Stuck`), slide (at most `maxSlideIterations` sweeps; walkable, steep and falling-steep clips; crease only when re-entering an earlier plane; three-plane stop; no-reversal; `SlideCapReached`; boundary hit `ClampedToBounds`), step-up (2 · contactSkin down-contact, up by stepHeight, walls-only slide, down; accepted only on support with the landing contact at most stepHeight above the start contact and further along the velocity); the safety clamp to the playable area setting `CharacterFlags.ClampedToBounds`; the ground probe (snapDistance when grounded at start and not jumped, else 2 · contactSkin when `Velocity.y <= 0`) with the support rule and `ledgeSupportMinNormalY`), every loop bounded as listed there; plus `public static Vec3 WishVelocity(Buttons, Stance, float sinYaw, float cosYaw, CompiledConfig)` (literal `0.70710677f`) and `public static int SubMoveCount(float distance, float radius, int maxSubMoves)`; float only, no allocation, `QueryCapacityExceeded` returned as `Fault` without touching `character`. Makes T068, T069, T070, T071 and T072 pass.
- [ ] T097 [US1] Wire stage 1 validation and stage 2 movement into `src/MmoEngine.SimCore/World/SimWorld.cs` and add `SubMoves` to the counters in `src/MmoEngine.SimCore/World/StepReport.cs` (if Phase 2 did not): stage 1 runs `InputValidator.Validate(command, character.LastSequence, config weaponSlots)` per supplied command, records `{CharacterId, Reason}` in the input-rejection events in ascending character ID, and uses the neutral input (no buttons, aim, slot and `LastSequence` unchanged) for rejected and absent commands; `StepMovement` calls `MovementStep.Step` on each next-state character in ascending ID with the world's candidate buffer, appends `{CharacterId, (byte)Outcome}` to the movement-outcome events only when non-zero, sums `SubMoves`, `Sweeps` and `SlideCapHits` into `StepReport.Counters`, and on `QueryCapacityExceeded` faults the step with that code and the character's element index, committing nothing; add `internal SimWorld(CompiledConfig config, CollisionWorld collision, int candidateCapacity)` used only by tests. Makes T064, T073, T074 and the world half of T066 pass.
- [ ] T098 [US1] Implement `StepFaultException` (`Step`, `Fault`, message `step <n> faulted: <code> at <path>`) in `src/MmoEngine.SimCore/Batch/StepFaultException.cs` and `StreamRunner` in `src/MmoEngine.SimCore/Batch/StreamRunner.cs`: both `Run` overloads, opening in the order of contracts/recorded-stream-format.md#opening-a-stream-to-run steps 1, 3, 4 and 5 (decode; `CollisionDataMissing` at offset 36, field `collisionChecksum`, `collision data <x16> was not supplied`, with an empty list refusing every stream; `CompiledConfig.Create`; `SimWorld.Reset`), then one `SimWorld.Step` per entry with its commands (neutral input for the others), appending each record through `StepRecord`; a faulted step throws `StepFaultException`; `perturbStep` other than -1 throws `ArgumentOutOfRangeException` (Phase 4 adds the perturbation and the rules-revision check). Makes T086 pass.
- [ ] T099 [P] [US1] Implement `TestWorldGenerator` in `src/MmoEngine.SimCore.Server/TestWorldGenerator.cs`: `public static CollisionData Generate()` from a fixed seed through a private counter-based SplitMix64 step (no `System.Random`; the core's public `SplitMix64` arrives in Phase 4) for small height noise on the open ground only: a heightfield (origin and extent inside ±4096 m, cellSize 1, gridCellSize 4) with a flat run area, a 30° ramp up to a plateau and a 50° ramp rising from it, a ridge and a cliff dropping at least 3 m; meshes with a vertical wall, stairs of rise 0.30 m and 0.50 m, a ledge slab with its underside 1.5 m above the floor, two walls meeting at 90 degrees, and one zero-thickness thin wall; no degenerate triangle; and `public static TestWorldFeatures Features` (a record in the same file) naming each scenario's start position, facing yaw and the geometry the scenario tests assert (wall plane, stair top heights and front edges, plateau height, ledge underside and extent, corner, thin-wall plane). Makes T075 pass.
- [ ] T100 [P] [US1] Implement the expected-hash form of `DivergenceReport` in `src/MmoEngine.SimCore.Server/DivergenceReport.cs`: `public static string AgainstExpected(string stream, uint step, ulong expected, ulong actual)` returning `DIVERGED stream <name>, build server, first divergent step <n>, against the expected hashes` and `  hash  expected <x16>  server <x16>` (contracts/state-serialization.md#divergence-report; Phase 4 adds the between-builds report). Used by the `streams verify` task below.
- [ ] T101 [US1] Create `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs` with `ScenarioCatalog.Names` (ordinal order), `ScenarioCatalog.Get(name)` (throws `KeyNotFoundException` naming the known names) and `ScenarioDefinition` (name, collision file name `test-world.mmcd`, own step rate, config overrides applied to a copy of the base `SimConfig`, characters from `TestWorldGenerator.Features`, durations in seconds, commands with sequence numbers from 1 and `ClientStep` equal to the step; `Build(SimConfig baseConfig, CollisionWorld world, int? stepRateHz = null)` returning an unrecorded `RecordedStream` with expected hashes 0, seed 0 and a fixed match salt; `Record(...)` running it through `StreamRunner` and returning the stream with the run's hashes), and the definitions `flat-run-20hz`, `flat-run-30hz`, `flat-run-60hz` (one character, ID 1, at rest and Grounded on the flat area, yaw 0, Forward held for one simulated second, rate 20, 30, 60) (contracts/simcore-cli.md#named-scenario-streams). Makes T076 and the definition half of T085 pass.
- [ ] T102 [US1] Add to `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs` the definitions `wall-slide-45` (3 m run-up, 45° into the wall, 3 s), `stairs-climb` (the 0.30 m stair, 3 s), `stairs-blocked` (the 0.50 m stair, 3 s) and `slope-limit` (up the 30° ramp onto the plateau and on into the 50° ramp, 6 s), all at the default 30 Hz. Makes T077, T078 and T079 pass.
- [ ] T103 [US1] Add to `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs` the definitions `jump-land` (Jump held for one step on the flat area, then 2 s with no buttons), `crouch-under-ledge` (Crouch and Forward held toward the ledge, Crouch released under it, Forward held until clear), `corner-wedge` (Forward held diagonally into the corner, 4 s) and `thin-wall-fast` (overrides `movement.runSpeed` 20, `movement.groundAccel` 1000, `movement.capsuleRadius` 0.25, `movement.maxSubMoves` 16, which R09 accepts: ((20 / cos 45°) + 50) · 0.05 = 3.914 ≤ 16 · 0.25; Forward held into the thin wall, 2 s). Makes T080, T081, T082 and T083 pass.
- [ ] T104 [US1] Implement `content generate [--out simcore/content/test-world.mmcd]` in `src/MmoEngine.SimCore.Cli/Commands/ContentCommand.cs` (encode `TestWorldGenerator.Generate()`, write through `AtomicFile`, print `<path>: <bytes> bytes, checksum <x16>`, exit 2 with the reason on stderr when it cannot write), register it in `src/MmoEngine.SimCore.Cli/Program.cs`, then run `dotnet run --project src/MmoEngine.SimCore.Cli -- content generate --out simcore/content/test-world.mmcd` and commit `simcore/content/test-world.mmcd`. Makes T087 pass.
- [ ] T105 [US1] Implement `streams record (--scenario <name> | --all) --reason "<why>" [--out simcore/streams] [--content simcore/content] [--config simcore/config/default.json]` in `src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs` and the writer `StreamFiles` (`WriteCommitted(string outDir, string name, byte[] bytes)` through `AtomicFile`; `AppendRegenerations(string outDir, string entry)`, one write that creates the file with `# Stream regenerations` and the sentence "This file is appended by `simcore streams record`; each entry gives the reason and the old and new hashes." when absent) in `src/MmoEngine.SimCore.Cli/Commands/StreamFiles.cs`: steps 1 to 6 of contracts/simcore-cli.md#streams-record (reason rules: missing, empty, whitespace, over 500 characters or multi-line exit 2; both or neither selector exit 2; unknown name exit 2 listing `ScenarioCatalog.Names`; config bound with `SimConfigJson` and refused with `path: message` lines, exit 2; collision file `<content>/test-world.mmcd` missing exit 2; a step fault exit 1; nothing written on any refusal; name order; unchanged streams not rewritten; `--all` deletes catalog-less `.mmrs` and prints `<name>: removed; file <x16>`; per-stream and total lines; the entry with UTC heading, reason, ``- Command: `simcore streams record --scenario <name>` `` or `--all`, `Rules revision: 1` (with `(was <r>)` when a changed stream carried another) and the table of changed, added and removed streams); register `streams` in `src/MmoEngine.SimCore.Cli/Program.cs`. Makes T089 and T091 pass.
- [ ] T106 [US1] Implement `streams verify [--dir simcore/streams] [--content simcore/content]` in `src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs`: decode every `<content>/*.mmcd` once, run every `<dir>/*.mmrs` in ordinal order through `StreamRunner.Run(stream, worlds)`, compare each record's hash with the expected hash, and print `<name>: match, <steps> steps, final <x16>`, the `DivergenceReport.AgainstExpected` lines (T100) at the first divergent step, or `REFUSED stream <name>: <message>` for a `CoreFormatException`, `ConfigValidationException` (its `path: message` lines) or `StepFaultException`; then `streams verify: <n> streams, <m> match, <d> diverged, 0 not comparable, <r> refused`; exit 1 when any diverged, else 2 when any was refused or the directory is missing or holds no stream, else 0; nothing written (NotComparable is Phase 4's). Makes T090 pass.
- [ ] T107 [US1] Record the 11 movement streams with the server build, one command each, in name order: `dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --scenario <name> --reason "Add the <name> movement scenario (spec 015 US1)."` for `corner-wedge`, `crouch-under-ledge`, `flat-run-20hz`, `flat-run-30hz`, `flat-run-60hz`, `jump-land`, `slope-limit`, `stairs-blocked`, `stairs-climb`, `thin-wall-fast`, `wall-slide-45` (never `--all`: the fire streams are recorded in Phase 7); commit `simcore/streams/<name>.mmrs` for all 11 together with `simcore/streams/REGENERATIONS.md` and its 11 entries. Makes T088 (US1.7), T085 (US1.8) and T084 pass.

**Checkpoint**: `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes` exit 0; `dotnet test tests/MmoEngine.SimCore.Tests` passes, including every test named in this phase at 20, 30 and 60 Hz; `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify` prints `streams verify: 11 streams, 11 match, 0 diverged, 0 not comparable, 0 refused` and exits 0; `dotnet run --project src/MmoEngine.SimCore.Cli -- content generate --out simcore/content/test-world.mmcd` followed by `git diff --exit-code simcore/` exits 0.

---

## Phase 4: User Story 2 - The browser build agrees with the server bit for bit (Priority: P1), slice 1: server-side agreement

**Goal**: Build everything the agreement suite needs on the server side before any WebAssembly build exists: the batched stepper the browser will call (`BatchStepper`, with the browser configuration guard), the rules-revision refusal and the one-ulp perturbation in `StreamRunner`, the core's `SplitMix64`, the generated streams and the `fuzz-all-buttons` definition, the field-by-field divergence report, the cross-build tables and their comparison, `simcore agreement prepare` and `simcore agreement compare`, and NotComparable outcomes in `streams verify`. A divergence is then named by stream, step and field.

**Independent Test**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Agreement|FullyQualifiedName~MmoEngine.SimCore.Tests.Batch|FullyQualifiedName~AgreementCommandTests"` passes. It shows that queued batches equal one-at-a-time steps, that a one-ulp perturbation at step k is reported at step k naming `characters[0].position.x` with both bit patterns, that another format version or rules revision is NotComparable, and that `agreement compare` exits 0 for a build equal to the server and 1 for a perturbed one.

### Tests (write first, confirm red)

- [ ] T108 [P] [US2] Write failing tests `SplitMix64Tests.NextFromSeedZeroGivesTheReferenceSequence`, `SplitMix64Tests.MixIsTheFinaliserOfTheDataModel` and `SplitMix64Tests.AtIsTheCounterthValueOfNext(ulong seed, ulong counter)` in `tests/MmoEngine.SimCore.Tests/Hashing/SplitMix64Tests.cs`. These pin the core generator that `CrossBuildTables`, `StreamGenerator` and Phase 6's `Spread` draw from (wasm-api.md#cross-build-tables, simcore-cli.md#generated-streams, data-model.md#weapon-step).
  - Given state 0, When `SplitMix64.Next(ref state)` is called three times, assert `0xE220A8397B1DCDAF`, `0x6E789E6AA1B965F4` and `0x06C45D188009454F`.
  - Assert `SplitMix64.Mix(0) == 0`, `SplitMix64.Mix(0x9E3779B97F4A7C15) == 0xE220A8397B1DCDAF`, and that `Mix(0x0123456789ABCDEF)` equals the data-model.md finaliser (`z ^= z >> 30; z *= 0xBF58476D1CE4E5B9; z ^= z >> 27; z *= 0x94D049BB133111EB; z ^= z >> 31`) computed in the test.
  - Theory rows (seed, counter) in {0, 0x5EED0001, 1000} x {0, 1, 63}: assert `SplitMix64.At(seed, counter)` equals the value of the (counter + 1)-th `Next` call from that seed.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SplitMix64Tests"` and confirm it fails (the type does not exist yet).
- [ ] T109 [US2] Write failing test `StreamRunnerTests.PerturbationDivergesAtExactlyThatStep(int hz)` in `tests/MmoEngine.SimCore.Tests/Batch/StreamRunnerTests.cs` (SC-002, wasm-api.md#streamrunner, Theory over `TestRates.All`).
  - Given the committed `simcore/streams/flat-run-<hz>hz.mmrs` and `simcore/content/test-world.mmcd`, opened read-only through `RepoPaths`, with k the stream's step number at index `(stepCount - 1) / 2` (15 for flat-run-30hz).
  - When `StreamRunner.Run(stream, collision, perturbStep: k)` and `StreamRunner.Run(stream, collision)` are compared record by record, assert:
    - the two outputs have the same record count;
    - every record before step k is byte-identical;
    - record k has the same step number and state length;
    - its state bytes differ only in the 4 bytes of `characters[0].position.x` (state offset 16), where the perturbed bits equal the unperturbed bits + 1;
    - its hash equals `StateHash.Compute` of the perturbed state and differs from the unperturbed hash;
    - every later record differs from the unperturbed one, and the steps keep their numbers k + 1, k + 2 and onward.
  - Also assert `Run(..., perturbStep: -1)` is byte-identical to `Run(...)`. A `perturbStep` of 0, -2 or the last step number + 1 throws `ArgumentOutOfRangeException` with `ParamName == "perturbStep"`.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamRunnerTests.PerturbationDivergesAtExactlyThatStep"` and confirm it fails.
- [ ] T110 [US2] Write failing test `StreamRunnerTests.RefusesBeforeStepZero(string caseName)` in `tests/MmoEngine.SimCore.Tests/Batch/StreamRunnerTests.cs` (supports US2.5 and US6.5, recorded-stream-format.md#opening-a-stream-to-run). Each row starts from a copy of the flat-run-30hz bytes in memory. Assert that `Assert.Throws` gives exactly the stated exception type with these values, and that no records are returned:
  - `unsupported-version`: the u16 at offset 4 set to 2. `CoreFormatException` with code `UnsupportedVersion`, offset 4, field `version`, and message `stream version at offset 4: unsupported version 2; this reader supports version 1`.
  - `rules-revision`: the u32 at offset 8 set to `SimCoreVersion.RulesRevision + 1`, with the XxHash64 trailer rewritten. `CoreFormatException` with `RulesRevisionMismatch`, offset 8, field `rulesRevision`, and message `stream rulesRevision at offset 8: rules revision 2 in the stream; this core implements rules revision 1`.
  - `collision-missing-list`: the list overload with an empty list. `CollisionDataMissing`, offset 36, field `collisionChecksum`, and detail `collision data <test-world checksum x16> was not supplied`.
  - `collision-missing-bytes`: the byte overload given the tiny-world file from `TestWorlds.TinyWorld`. The same code, offset, field and detail.
  - `config-invalid`: the stream re-encoded with `movement.gravity` NaN. `ConfigValidationException` whose errors are exactly [`movement.gravity: must be finite`].
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamRunnerTests.RefusesBeforeStepZero"` and confirm the `rules-revision` row fails.
- [ ] T111 [US2] Write failing test `BatchStepperTests.QueuedCommandsEqualOneAtATime(int batchSize, int hz)` in `tests/MmoEngine.SimCore.Tests/Batch/BatchStepperTests.cs` (US2.6, wasm-api.md#batchstepper). Use `MemberData` crossing {1, 2, 7, 32} with `TestRates.All`.
  - Given: a batchable stream built in the test by a private helper. It has one character on `simcore/content/test-world.mmcd` with `TestConfigs.At(hz)`, and 96 steps, each with one command for character index 0. Command k has:
    - sequence k + 1;
    - buttons `(Buttons)((k * 37) % 512)`;
    - yaw `(ushort)(k * 2731)`;
    - pitch `((k * 1543) % 32769) - 16384`;
    - slot `k % 2`.
  - Every 16th command is made invalid, cycling a repeated sequence, button `0x0200`, pitch 16385 and slot 2.
  - Encode the stream with `RecordedStream` as Phase 2's RecordedStreamTests do.
  - When stepper A (`new BatchStepper(32)`) is loaded with `LoadCollision`, `LoadConfig` (the stream's config block) and `Reset(initialState, matchSalt)`, and is given the 17-byte commands `batchSize` at a time through `CommandSegment` with one `StepQueued(batchSize)` call per batch (the last batch shorter), and stepper B is driven the same way with one command per call:
    - every call returns the number of commands queued;
    - the concatenated records of A (each read from `ReportSegment`'s header: count at 0, length L at 4, records at 8) equal B's byte for byte;
    - both equal `StreamRunner.Run(stream, collision)` byte for byte.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~BatchStepperTests.QueuedCommandsEqualOneAtATime"` and confirm it fails (compile failure: `BatchStepper` does not exist).
- [ ] T112 [US2] Write failing browser-guard and report tests in `tests/MmoEngine.SimCore.Tests/Batch/BatchStepperTests.cs` (FR-025, FR-029 browser guard, config-rules.md#browser-guard, wasm-api.md#report-buffer-layouts and #return-codes):
  - `LoadCallsReturnTheirCodes(string caseName)` Theory. After each call, read the report buffer's u32 text length at offset 0 and the UTF-8 text at 4.
    - `collision-ok`: the test world gives `Ok` and length 0.
    - `collision-refused`: the test world with byte 0 changed gives `FormatRefused`, and the text equals the `CoreFormatException.Message` that `CollisionFile.Decode` throws for the same bytes.
    - `config-ok`: `Ok`, length 0.
    - `config-malformed`: a block whose entry count exceeds its length gives `FormatRefused`, and the text equals the `ConfigBlock` decode message (`config <field> at offset <n>: <detail>`).
    - `config-invalid`: a block with `movement.gravity` NaN and `movement.runSpeed` -1 gives `ConfigInvalid`. The text is exactly the `ConfigGuard.Check` errors formatted `path: message`, in rule-table order, joined by `\n`.
    - `reset-not-ready`: Reset with neither loaded, with only the collision loaded, with only the config loaded, and after a failed `LoadConfig` that followed an `Ok` one. Each gives `NotReady` with length 0.
    - `reset-refused`: a state whose `characters[0].weaponCount` is 3 gives `FormatRefused`, and the text equals `SimWorld.Reset`'s exception message.
    - `load-discards-world`: after an `Ok` Reset, `LoadCollision` (Ok) makes `StepQueued(1)` return -1.
  - `StepQueuedRefusesBadCalls(string caseName)` Theory:
    - no Reset gives -1 (`StepError.NotReady`);
    - count -1 gives -2 (`CountOutOfRange`);
    - count `CommandCapacity + 1` gives -2;
    - in each of these, report bytes 0-7 are all zero;
    - count 0 returns 0, with record count 0 and length 0.
  - `ConstructorRefusesACapacityOutsideOneTo256(int capacity)` with rows 0 and 257: `ArgumentOutOfRangeException`.
  - `StopsAtTheFirstFaultAndKeepsEarlierRecords`. Through the internal `StepQueuedWithInjectedBits(count: 5, commandIndex: 3, fieldOrdinal: <layout ordinal of characters[].velocity.y>, elementIndex: 0, bits: 0x7FC00000)`:
    - it returns 3, and the header has count 3 and the length of the 3 records;
    - the records equal the first 3 of a one-at-a-time run;
    - the stop entry at 8 + L holds i32 -4, the u32 step number of the 4th step, and the text `NonFinite at characters[0].velocity.y`;
    - a following `StepQueued(1)` with the 4th command returns 1, and its record equals the one-at-a-time run's 4th record.
  - `StopsAtAnUndecodableCommand`. Command 2 of 4 has byte 0 set to `0x4A`:
    - it returns 2, and the stop entry holds code -3, the 3rd step's number, and `input formatId at offset 0: format identifier 0x4A is not 0x49`;
    - the world stays at the 2nd step's state.
  - `ReportHeaderMatchesTheRecords`. After `StepQueued(32)`, walk the records by `stateLength`:
    - count == 32;
    - the walk ends exactly at 8 + L;
    - step numbers are consecutive;
    - each hash equals `StateHash.Compute(state)`.
  - `ReportBufferGrowsOnlyForLargerMaxima`:
    - a new stepper's `ReportSegment.Count` is 65,536;
    - for capacity 32, `LoadConfig(default.json)` makes it 2,935,060 (8 + 32 x 91,688 + 12 + 1,024);
    - a later `LoadConfig` with smaller `world.maxCharacters` keeps the same array and count;
    - a larger one allocates a bigger buffer;
    - for capacity 1, the default gives 92,732.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~BatchStepperTests"` and confirm it fails.
- [ ] T113 [P] [US2] Write failing tests for the server half of the cross-build tables in `tests/MmoEngine.SimCore.Tests/Batch/CrossBuildTablesTests.cs` (they support US3.8, US5.5 and SC-004; the cross-build halves are Phase 11's; wasm-api.md#cross-build-tables):
  - `InputsArePinned`:
    - two `BuildInputs()` calls return identical bytes;
    - the header is magic `MMXT`, version 1, reserved 0, math row count M and pose row count P == 6,192;
    - M equals the sum of the per-function counts of wasm-api.md's list. The test tallies them in its own table keyed by function ID 1-10, and it asserts each function's row count;
    - rows are ordered by function ID, then as listed;
    - pose rows are ordered by stance, then yaw ascending (1,032 yaws: every 64th angle plus each quadrant boundary's two neighbours), then the three positions as listed;
    - the bytes' `XxHash64` equals a constant pinned in the test. The constant is set when the green task lands; changing the table thereafter is a visible edit.
  - `EachRowEqualsTheDirectCall`. Given `Evaluate(BuildInputs(), config block of default.json)`, assert:
    - every math row's r0 and r1 bits equal the `SimMath` call the function ID names on the row's a and b bits (SinCos and SinCosBinaryAngle give sine then cosine; the others give the result and 0);
    - every pose row's 25 floats equal `HitboxBuilder` for that stance's `HitboxLayout`, yaw and position (head, torso and legs A, B and radius, then the sphere centre and radius), bit for bit;
    - the results header is `MMXR` v1 with the same counts.
  - `NonFiniteAndOutOfDomainRowsReturnNaN`: each NaN, +-inf, Asin/Acos outside +-1 or at +-2, and negative Sqrt row gives `float.IsNaN(r0)`, counted explicitly against the contract's row count.
  - `RefusesMalformedTables(string caseName)` Theory. Assert the `CoreFormatException` code, offset, field and exact message `tables <field> at offset <n>: <detail>`:
    - magic: `UnknownMagic` at 0, field `magic`;
    - version 2: `UnsupportedVersion` at 4, `unsupported version 2; this reader supports version 1`;
    - one byte short: `Inconsistent` at 8, field `length`, `<n> bytes, the counts need <m>`;
    - function ID 11 in math row 0: `OutOfRange` at 16, field `mathRows[0].function`, `unknown function ID 11`;
    - stance 2 in pose row 0: `OutOfRange` at 16 + 12M, field `poseRows[0].stance`, `unknown stance 2`.
  - Also `EvaluateRefusesAnInvalidConfigBlock`: a `movement.gravity` NaN block gives `ConfigValidationException` with [`movement.gravity: must be finite`].
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~CrossBuildTablesTests"` and confirm it fails.
- [ ] T114 [P] [US2] Write failing tests `TablesComparisonTests.ReportsTheFirstDifferingRow(string caseName, string expectedLine)` and `TablesComparisonTests.AnyNaNMatchesANaNRow` in `tests/MmoEngine.SimCore.Tests/Agreement/TablesComparisonTests.cs` (wasm-api.md#cross-build-tables, comparing).
  - Given `inputs = CrossBuildTables.BuildInputs()` and `server = Evaluate(inputs, default block)`, copy the server results as the build's results and mutate them. Assert `TablesComparison.Compare("node-interp", inputs, server, build)` returns `TablesStatus.Diverged` with `FirstMismatch` exactly equal to the row's line:
    - the low bit of r0 flipped in the first Atan2 row with a = 0x3F000000, b = 0xBF800000: `tables: build node-interp, first mismatch at math row <i>: Atan2(a 0x3F000000, b 0xBF800000) r0 server 0x<s> build 0x<s+1>`;
    - r1 flipped in a SinCos row: `... SinCos(a 0x<bits>) r1 server ... build ...`;
    - torso A.x flipped in the Crouched, yaw 0x4000, (4096.25, 37.5, -7000.125) row: `tables: build node-interp, first mismatch at pose row <j>: Crouched yaw 0x4000 at (4096.25, 37.5, -7000.125) torso A.x server 0x<s> build 0x<s+1>`;
    - the sphere radius flipped in a pose row: `... sphere radius server ... build ...`;
    - a finite server value against build NaN 0x7FC00000: `... r0 server 0x<s> build 0x7FC00000`;
    - the build's header with M - 1: `tables: build node-interp, results header differs from the server's`;
    - when two rows differ, only the first is reported.
  - Identical results give `Match` and a null `FirstMismatch`.
  - `AnyNaNMatchesANaNRow`: server NaN `0x7FC00000` against build `0xFFC00001` and `0x7F800001` gives `Match`.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~TablesComparisonTests"` and confirm it fails.
- [ ] T115 [US2] Write failing test `DivergenceReportTests.PerturbedRecordsReportTheFirstStepAndField` in `tests/MmoEngine.SimCore.Tests/Agreement/DivergenceReportTests.cs` (US2.4, state-serialization.md#divergence-report, simcore-cli.md#divergence-report).
  - Given the server records `StreamRunner.Run(flat-run-30hz, test-world)` and the build records `StreamRunner.Run(flat-run-30hz, test-world, perturbStep: 15)`, with the stream's expected hashes.
  - When `DivergenceReport.CompareBuild("flat-run-30hz", "node-interp", server, build, expectedHashes)` runs, assert:
    - `Outcome == StreamOutcome.Diverged` and `FirstDivergentStep == 15`;
    - `ServerHash` and `BuildHash` are record 15's two hashes, and `DifferingFields == 1`;
    - the report's first line is exactly `DIVERGED stream flat-run-30hz, build node-interp, first divergent step 15`;
    - the second line is `  hash  server <x16>  node-interp <x16>`, with both hashes as 16 lowercase hex digits;
    - the third line is `  1 field differs`;
    - the fourth matches `^  characters\[0\]\.position\.x +server 0x([0-9A-F]{8}) (\S+) +node-interp 0x([0-9A-F]{8}) (\S+)$`, where the build bits equal the server bits + 1 and each decimal is that float's `ToString("R", CultureInfo.InvariantCulture)`, which parses back to its bits;
    - no line names a step before 15.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~DivergenceReportTests.PerturbedRecordsReportTheFirstStepAndField"` and confirm it fails.
- [ ] T116 [US2] Write failing report-detail tests in `tests/MmoEngine.SimCore.Tests/Agreement/DivergenceReportTests.cs` (FR-026, state-serialization.md#divergence-report):
  - `DifferentCountsListAbsentElements`. The server state comes from `TestStates` with one projectile (ID 1, `nextProjectileId` 2); the build state is the same without it.
    - The report lists `projectileCount  server 0x00000001 1  node-interp 0x00000000 0`.
    - Then it lists each of the 13 projectile paths in layout order (`projectiles[0].id`, `.ownerId`, `.class`, `.launchStep`, `.origin.x` to `.z`, `.launchVelocity.x` to `.z`, `.position.x` to `.z`) as `server 0x<bits> <decimal>  node-interp absent`.
    - The summary line is `  14 fields differ`.
  - `UnreadableStateIsListedToItsOffset`. The build record's state is the server state cut to 30 bytes, with `stateLength` 30.
    - The report lists the differing readable fields up to offset 28, then `  unreadable from offset 28`.
  - `SignedAndSmallFieldsShowZeroExtendedBits`. `characters[0].aimPitch` is -1 against 0, which gives `server 0x0000FFFF -1` against `0x00000000 0`.
  - `MissingBuildRecordIsTheFirstDivergentStep`. The build records are the server's first 10 of 30.
    - The first divergent step is the 11th step's number.
    - The report has the line `  hash  server <x16>  node-interp absent`.
  - `BuildErrorIsReportedWithItsMessage`. `DivergenceReport.BuildError("flat-run-30hz", "chromium-worker", firstStep, "boom")`:
    - the outcome is Diverged at the stream's first step, with `Error == "boom"`;
    - the report has the line `  error  boom`.
  - `ExpectedHashDivergenceNamesTheStepAndBothHashes`. The build records equal the server's, but expected hash 12 is altered.
    - The first line is `DIVERGED stream jump-land, build node-interp, first divergent step 12, against the expected hashes`.
    - The next is `  hash  expected <x16>  node-interp <x16>`.
  - `NotComparableLineNamesBothValues`. `DivergenceReport.NotComparable("jump-land", error)` gives `NOT COMPARABLE stream jump-land: unsupported version 2; this reader supports version 1` for a version error, and `NOT COMPARABLE stream jump-land: rules revision 2 in the stream; this core implements rules revision 1` for a revision error.
  - Equal records give `StreamOutcome.Match` and an empty report.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~DivergenceReportTests"` and confirm the new tests fail.
- [ ] T117 [P] [US2] Write failing test `NotComparableTests.AnotherFormatVersionOrRulesRevisionIsNotComparableNotDiverged(string caseName, string expectedLine)` in `tests/MmoEngine.SimCore.Tests/Agreement/NotComparableTests.cs` (US2.5, simcore-cli.md#streams-verify).
  - Given a unique temp directory from `Support/TempDirectories` holding one modified copy of `simcore/streams/flat-run-30hz.mmrs`. The rows are:
    - `unsupported-version`: the u16 at offset 4 = 2;
    - `rules-revision`: the u32 at offset 8 = 2, with the trailer rewritten.
  - When `SimcoreCli.Run("streams", "verify", "--dir", <temp>, "--content", <RepoPaths.Root>/simcore/content)` runs, assert:
    - exit 2;
    - stdout contains exactly the line `NOT COMPARABLE stream flat-run-30hz: unsupported version 2; this reader supports version 1` or `NOT COMPARABLE stream flat-run-30hz: rules revision 2 in the stream; this core implements rules revision 1`;
    - the last line is `streams verify: 1 streams, 0 match, 0 diverged, 1 not comparable, 0 refused`;
    - no stdout line starts with `DIVERGED` or `REFUSED`.
  - Add `NotComparableTests.EmbeddedCommandVersionIsRefusedNotNotComparable`. A stream whose `steps[0].commands[0].input.version` is 2 prints `REFUSED stream ...`, exits 2, and counts 1 refused.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~NotComparableTests"` and confirm it fails.
- [ ] T118 [P] [US2] Write failing tests in `tests/MmoEngine.SimCore.Tests/Agreement/StreamGeneratorTests.cs` (FR-026 server half, simcore-cli.md#generated-streams):
  - `GeneratedStreamsCoverEveryButtonAndTheAimRange`. Given `StreamGenerator.Build(seed, default.json SimConfig, test world)` for seeds 1-50, replay each stream's commands through `InputValidator.Validate` while tracking each character's `lastSequence` as the world does. Assert that together the seeds include:
    - accepted commands setting each button bit 0-8;
    - accepted commands with pitch -16384 and with pitch 16384;
    - accepted commands whose yaw falls in each of the 16 ranges [k x 4096, (k + 1) x 4096);
    - at least one command rejected with each of `SequenceNotIncreasing`, `UnknownButtons`, `PitchOutOfRange` and `UnknownWeaponSlot`;
    - streams at 20, 30 and 60 Hz;
    - between 1/128 and 1/32 of all commands invalid.
  - `EachGeneratedStreamHasTheContractShape(int seed)`, rows 1, 2, 3 and 50. Assert:
    - `StreamGenerator.NameFor(seed)` is `seed-001`, `seed-002`, `seed-003` and `seed-050`;
    - `StepHz == new[] {20, 30, 60}[seed % 3]`;
    - 300 steps, and `CharacterCount` in [2, 4];
    - header `Seed == (ulong)seed`, and `CollisionChecksum` equals the test world's;
    - every step carries one command per character, in ascending index order;
    - `StreamRunner.Run` runs it with no refusal and no fault.
  - `SameSeedGivesTheSameStreamBytes`: two builds of seed 17, encoded, are byte-identical, and seed 18 differs.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamGeneratorTests"` and confirm it fails.
- [ ] T119 [P] [US2] Write failing test `FuzzAllButtonsDefinitionTests.DefinitionCoversEveryButtonTheAimRangeAndEveryRejection(int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/FuzzAllButtonsDefinitionTests.cs` (the fuzz-all-buttons row of simcore-cli.md#named-scenario-streams; Theory over `TestRates.All`).
  - Given `ScenarioCatalog.Get("fuzz-all-buttons").Build(TestConfigs default, test world, hz)`, assert:
    - `ScenarioCatalog.Names` contains `fuzz-all-buttons` in ordinal order;
    - 4 characters, 300 steps and header `Seed == 1000`;
    - its commands, replayed through `InputValidator.Validate`, set every button bit 0-8 in an accepted command and a reserved bit (`0xFE00`) in a rejected one;
    - they reach pitch -16384 and 16384 and every 1/16 turn of yaw;
    - they include all four rejection reasons;
    - two builds encode to identical bytes;
    - `StreamRunner.Run` completes with no fault.
  - The `.mmrs` file is not written here; Phase 7 records it.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~FuzzAllButtonsDefinitionTests"` and confirm it fails.
- [ ] T120 [US2] Write test support `tests/MmoEngine.SimCore.Tests/Support/WorkDirectories.cs`, then the failing `agreement prepare` tests in `tests/MmoEngine.SimCore.Tests/Cli/AgreementCommandTests.cs` (FR-026 server half, simcore-cli.md#agreement-prepare and #agreement-work-directory).
  - Support:
    - `WorkDirectories.Prepare(string seeds = "1-2")` runs `SimcoreCli.Run("agreement", "prepare", "--work", <new path under a unique temp dir>, "--seeds", seeds, "--streams", <root>/simcore/streams, "--content", <root>/simcore/content, "--config", <root>/simcore/config/default.json)`, asserts exit 0 and returns the path;
    - `WorkDirectories.WriteBuild(string work, string build, string? perturbStream = null, uint perturbStep = 0)` copies every `server/<stream>.records` to `<work>/<build>/` (or writes `StreamRunner.Run(stream, collision, perturbStep)` for `perturbStream`) and copies `server/tables.bin` to `<build>/tables.bin`.
  - `PrepareWritesEveryArtifactAndTheManifestLast`, with `--seeds 1-50`:
    - exit 0, and stdout is exactly `prepared <work>: <n> streams (<c> named, 50 generated), <steps> steps, rules revision 1`. Here c is the number of `*.mmrs` in `simcore/streams` (68 streams once Phase 7 has recorded all 18), n = c + 50, and steps is the sum of the streams' step counts;
    - `manifest.json` has `formatVersion` 1, `rulesRevision` 1 and `formats` {inputCommand 1, collisionData 1, recordedStream 1}, and `seeds` {1, 50};
    - its streams are the committed ones in ordinal name order, then `seed-001` to `seed-050` with origin `generated` and seeds 1-50;
    - every `file`, `collision`, `serverRecords` and batch path exists;
    - `streams/seed-001.mmrs` equals `StreamGenerator.Build(1, ...)` with its expected hashes filled, byte for byte;
    - each `server/<stream>.records` equals `StreamRunner.Run` of that stream;
    - `collision/<x16>.mmcd` equals `simcore/content/test-world.mmcd`;
    - `batch` is non-null exactly for the streams whose every step has one command for character index 0. For flat-run-30hz, `batch/flat-run-30hz/commands.bin` is 30 x 17 bytes, and `matchSalt` is the header salt in x16;
    - `perturbation` is {`flat-run-30hz`, 15, `characters[0].position.x`};
    - `refusals` are `unsupported-version`, `rules-revision` and `collision-missing`, with the expected texts `unsupported version 2; this reader supports version 1`, `rulesRevision at offset 8` and `collisionChecksum at offset 36`. The collision-missing refusal names a `collision/<x16>.mmcd` whose checksum is not flat-run-30hz's;
    - `tables/inputs.bin` equals `CrossBuildTables.BuildInputs()`, `tables/config.bin` equals the config block of default.json, and `server/tables.bin` equals `Evaluate` of the two;
    - `manifest.json`'s `LastWriteTimeUtc` is not earlier than any other file's.
  - `PrepareRefusesANonEmptyWorkDirectoryOrBadSeeds(string caseName)` Theory. Each row exits 2 and writes nothing new:
    - a non-empty directory (its one file unchanged);
    - `--work` naming a file;
    - `--seeds` values `0-5`, `5-3`, `1-1000` and `abc`;
    - a missing `--streams` directory.
  - `PrepareExitsOneWhenACommittedStreamDivergesAndWritesNoManifest`. A temp copy of the streams has one expected hash altered (trailer rewritten).
    - Exit 1, and stdout has a `DIVERGED stream <name>, build server, first divergent step <n>, against the expected hashes` line.
    - No `manifest.json` exists.
  - `PrepareNeverWritesUnderSimcore`. The XxHash64 of every file under `simcore/` is the same before and after a prepare.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~AgreementCommandTests"` and confirm it fails.
- [ ] T121 [US2] Write failing `agreement compare` tests in `tests/MmoEngine.SimCore.Tests/Cli/AgreementCommandTests.cs` (US2.4, US2.5 and FR-026 server half, simcore-cli.md#agreement-compare). Each runs on `WorkDirectories.Prepare()` plus `WriteBuild` folders.
  - `CompareExitsOneOnADivergedBuildAndWritesItsReport` (supports US2.4). node-interp is written with `perturbStream: "flat-run-30hz", perturbStep: 15`; node-interp-nojit and chromium-worker are unperturbed.
    - Exit 1, and stdout has `node-interp: diverged in 1 of <n> streams; first: flat-run-30hz at step 15; tables match`, then the first divergence report.
    - The last line is `agreement: diverged`.
    - `report/divergences/node-interp/flat-run-30hz.txt` names `characters[0].position.x` with both bit patterns.
    - `report/agreement.json` has `outcome` `Diverged`, and node-interp's `diverged[0]` is {stream `flat-run-30hz`, step 15, serverHash, buildHash, differingFields 1, error null, report `report/divergences/node-interp/flat-run-30hz.txt`}.
  - `CompareMatchesABuildEqualToTheServer`. The three required builds are written unperturbed.
    - Exit 0, with the lines `node-interp: match, <n> streams, <steps> steps, tables match`, the same for node-interp-nojit and chromium-worker, and `firefox-worker: not run`, `webkit-worker: not run`, `node-interp-relinked: not run`, `node-aot: not run` in that order.
    - The last line is `agreement: match`, and `agreement.json` has outcome `Match`.
  - `RequiredBuildThatDidNotRunExitsTwo`. Only node-interp and chromium-worker are written: exit 2, with `node-interp-nojit: not run` and `agreement: cannot run: required build node-interp-nojit did not run`.
  - `OptionalBuildThatDidNotRunIsNotRun`. firefox-worker is missing: exit 0, and `agreement.json` has {build `firefox-worker`, required false, status `NotRun`}.
  - `BuildErrorFileIsADivergence`. chromium-worker has `flat-run-30hz.error.txt` with `boom` instead of records: exit 1, the report has `  error  boom`, and `diverged[0].error == "boom"` at the stream's first step.
  - `SummaryAppendsTheSimCoreAgreementSection`. The `--summary` file already holds `existing\n`.
    - After the diverged run, it starts with `existing\n`, then `## SimCore agreement`.
    - The table header is `| Build | Required | Status | Streams | Steps | First divergence | Tables |`, with 7 build rows.
    - A fenced code block holds node-interp's first report.
  - `CompareReportsATablesMismatchAsDiverged(string caseName)`. The rows are a flipped bit in node-interp's `tables.bin` math row 0 r0, and a `tables.error.txt` holding `boom` instead of `tables.bin`:
    - exit 1;
    - the line `node-interp: streams match, <n> streams, <steps> steps; tables diverged` or `...; tables missing`;
    - `report/divergences/node-interp/tables.txt` holds the `TablesComparison` line or `boom`;
    - `agreement.json` tables {status `Diverged` or `Missing`, firstMismatch the line or `boom`}.
  - `WorkDirectoryFromAnotherRulesRevisionIsNotComparable(string caseName, string expectedLine)` (US2.5). The manifest's `rulesRevision` is set to 2, or `formats.recordedStream` to 2:
    - exit 2;
    - stdout has `NOT COMPARABLE work <work>: rules revision 2 in the work directory; this core implements rules revision 1`, or `NOT COMPARABLE work <work>: recorded stream format version 2 in the work directory; this reader supports version 1`;
    - no `DIVERGED` line.
  - `CompareCannotRunWithoutItsInputs(string caseName)` Theory. Exit 2 with `agreement: cannot run: <reason>` for each row: no manifest, a manifest that is not valid JSON, a missing `server/<stream>.records`, a missing `server/tables.bin`, and `--require node-interp,unknown-build` (which names `unknown-build`).
  - `UnknownFoldersAreIgnored`. A `<work>/other-build/` folder changes no output line.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~AgreementCommandTests"` and confirm the compare tests fail.
- [ ] T122 [P] [US2] Extend `tests/MmoEngine.SimCore.Tests/Architecture/StreamWriterTests.cs` with failing test `OnlyRecordAndAgreementPrepareEncodeStreamFiles` (recorded-stream-format.md#regeneration). Using the source scan Phase 3's `OnlyTheRecordCommandWritesStreamFiles` uses, assert:
  - the only `src/` files that call `RecordedStream.Encode` and write a `.mmrs` file are `src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs` (the record handler, under `--out`) and `src/MmoEngine.SimCore.Cli/Agreement/WorkDirectory.cs` (under `--work` only);
  - `WorkDirectory.cs` writes no path built from `simcore/`.
  - Keep Phase 3's test passing.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StreamWriterTests"` and confirm the new test fails (`WorkDirectory.cs` does not exist).

### Implementation

- [ ] T123 [P] [US2] Implement `public static class SplitMix64` in `src/MmoEngine.SimCore/Hashing/SplitMix64.cs`:
  - `public const ulong Gamma = 0x9E3779B97F4A7C15`;
  - `Mix(ulong z)`, the data-model.md finaliser;
  - `Next(ref ulong state)`, which does `state += Gamma` (unchecked) and returns `Mix(state)`;
  - `At(ulong seed, ulong counter) => Mix(unchecked(seed + (counter + 1) * Gamma))`.
  - Use explicit usings and no allocation. Makes T108 pass.
- [ ] T124 [P] [US2] Implement the batch stepper per wasm-api.md#core-api, #batchstepper, #report-buffer-layouts and #return-codes, and config-rules.md#browser-guard. Write `LoadResult` in `src/MmoEngine.SimCore/Batch/LoadResult.cs` (byte: Ok 0, FormatRefused 1, ConfigInvalid 2, NotReady 3), `StepError` in `src/MmoEngine.SimCore/Batch/StepError.cs` (int: NotReady -1, CountOutOfRange -2, CommandRefused -3, StepFault -4) and `BatchStepper` in `src/MmoEngine.SimCore/Batch/BatchStepper.cs`. Makes T111 and T112 pass (US2.6, FR-025, FR-029 browser guard).
  - **Constructor**: 1 <= capacity <= 256, else `ArgumentOutOfRangeException`. It allocates:
    - a `CommandCapacity x InputCommand.EncodedSize` (17) command buffer, never reallocated;
    - a 65,536-byte report buffer;
    - a one-element `CharacterCommand[]` scratch.
  - **`LoadCollision`**: decodes with `CollisionFile.Decode`.
  - **`LoadConfig`**: `ConfigBlock` decode (standalone `config` form), then `ConfigGuard.Check` (each error as a `path: message` line, joined by `\n`), then `CompiledConfig.Create`. The report buffer grows to `max(65,536, 8 + CommandCapacity x MaxStepRecordLength + 12 + 1,024)` only when that is larger than the current buffer.
  - **Load-call rules**: every load call discards the world. A failed load unloads what it was loading, and writes the u32 text length and the UTF-8 text (the format error's message, or the guard's lines). An `Ok` load writes text length 0.
  - **`Reset`**: `NotReady` unless both are loaded. Otherwise it constructs a `SimWorld(config, collision)` and calls `Reset(state, salt)`. A `CoreFormatException` gives `FormatRefused` with its message, and no world.
  - **`StepQueued(count)`**:
    - It checks the count range first (-2), then the world (-1). A negative return writes a zero header.
    - It decodes each command with `InputCodec.TryDecode`; a decode error stops with -3 and the format error's message. It steps `SimWorld.Step` with the command for character index 0, and every other character gets the neutral input.
    - A `StepReport.Fault` stops with -4 and `<code> at <path>`.
    - Each committed step's record is written through `StepRecord` after the 8-byte header. The stop entry is i32 code, u32 the uncommitted step number, u32 text length (<= 1,024), then the text.
    - It returns n. The committed path throws nothing and allocates nothing; only the stop path formats text.
  - **Fault seam**: `internal int StepQueuedWithInjectedBits(int count, int commandIndex, int fieldOrdinal, int elementIndex, uint bits)` shares the same private loop and calls `SimWorld.StepWithInjectedBits` for command `commandIndex`. It is not a delegate field.
- [ ] T125 [US2] Add the rules-revision refusal and the one-ulp perturbation per wasm-api.md#streamrunner and recorded-stream-format.md#opening-a-stream-to-run. Makes T110 and T109 pass (SC-002, US2.5 support).
  - In `src/MmoEngine.SimCore/Batch/StreamRunner.cs`:
    - add open step 2, between decode and the collision lookup: a `rulesRevision` other than `SimCoreVersion.RulesRevision` throws `CoreFormatException` with `RulesRevisionMismatch`, offset 8, field `rulesRevision`, and detail `rules revision <s> in the stream; this core implements rules revision <c>`;
    - after open step 5 and before step 0, a `perturbStep` other than -1 that is not one of the stream's step numbers throws `ArgumentOutOfRangeException(nameof(perturbStep))`;
    - after the step numbered `perturbStep` commits, append the record of the report from `world.PerturbFirstCharacterPositionX()` in place of that step's record.
  - In `src/MmoEngine.SimCore/World/SimWorld.cs`, add `internal StepReport PerturbFirstCharacterPositionX()`. It applies `MathF.BitIncrement` to character index 0's committed `Position.X`, re-runs stage 6 (canonicalise, finite check, serialise with the committed step's events, hash) and recommits. Later steps continue from the perturbed state.
  - Not [P]: edits `SimWorld.cs`.
- [ ] T126 [US2] Implement `public static class CrossBuildTables` in `src/MmoEngine.SimCore/Batch/CrossBuildTables.cs` per wasm-api.md#cross-build-tables, after T123. Makes T113 pass (server half of US3.8, US5.5 and SC-004). Then pin the `XxHash64` constant in `InputsArePinned`.
  - **`BuildInputs()`** writes `MMXT` v1: the math rows (function IDs 1-10, 12 bytes each), then the 6,192 pose rows (16 bytes each), in the contract's order.
    - Each function's listed special values come first, then its drawn bit patterns from `SplitMix64.At(0x5EED0000 + functionId, i)`. Patterns are mapped uniformly into the stated range, rejecting out-of-range draws in draw order with a fixed bound on attempts per row, so the sequence is the same in every build.
    - The non-finite and out-of-domain rows come last.
  - **`Evaluate(inputs, configBlock)`**: decodes the config block, runs `ConfigGuard` (`ConfigValidationException`), compiles it, then refuses malformed tables with `CoreFormatException` (format `tables`, messages `tables <field> at offset <n>: <detail>`) for:
    - magic, version 1 and reserved 0;
    - `length` against 16 + 12M + 16P;
    - an unknown `mathRows[i].function`;
    - a `poseRows[j].stance` above 1.
  - It evaluates each math row with the `SimMath` function it names. Each pose row goes through `HitboxBuilder` with that stance's `HitboxLayout`. It writes `MMXR` v1: 8 bytes per math row (r0, r1) and 25 floats per pose row (head, torso, legs, each A, B and radius; then the sphere centre and radius).
  - The results are allocated (not tick path); the function uses no LINQ, lambdas or hash-ordered collections.
- [ ] T127 [P] [US2] Implement the between-builds divergence report per state-serialization.md#divergence-report and simcore-cli.md#divergence-report. Makes T115 and T116 pass (US2.4, FR-026).
  - Add `src/MmoEngine.SimCore.Server/StateFieldReader.cs`:
    - it walks a state's bytes in layout order (header; characters, with each record's `weaponCount` weapons; projectiles; the six event lists) with no value checks;
    - it returns each field's path, kind and bits zero-extended to 32, and the offset of the first field that cannot be read whole;
    - it uses the layout table in `src/MmoEngine.SimCore/Formats/StateSerializer.cs`, and makes that table and its row type public read-only if Phase 2 left them internal.
  - Add `src/MmoEngine.SimCore.Server/StreamComparison.cs`: `StreamOutcome` {Match, Diverged}, and the record `StreamComparison(StreamOutcome Outcome, uint? FirstDivergentStep, ulong? ServerHash, ulong? BuildHash, int DifferingFields, string? Error, string Report)`.
  - In `src/MmoEngine.SimCore.Server/DivergenceReport.cs`, add `CompareBuild(string stream, string build, ReadOnlySpan<byte> serverRecords, ReadOnlySpan<byte> buildRecords, IReadOnlyList<ulong> expectedHashes)`. Per step, in this order, the first divergent step is where:
    - the build's record is missing or unreadable;
    - the step numbers differ;
    - the hashes or state bytes differ (this gives the field diff);
    - the build's hash differs from the expected hash (this gives the `against the expected hashes` form naming the build).
  - Also add `BuildError(string stream, string build, uint firstStep, string message)` and `NotComparable(string stream, FormatError error)`.
  - Report lines:
    - header `DIVERGED stream <s>, build <b>, first divergent step <n>`;
    - `  hash  server <x16>  <b> <x16|absent>`;
    - `  1 field differs` or `  <k> fields differ`;
    - each field as `  <path>  server 0x%08X <decimal>  <b> 0x%08X <decimal>`, where an element present on one side only shows `absent` on the other, and an unreadable state ends with `  unreadable from offset <n>`;
    - `  error  <message>` for a build error.
  - Decimals: unsigned or signed by type; floats as `ToString("R", CultureInfo.InvariantCulture)`.
- [ ] T128 [US2] Implement `TablesComparison` in `src/MmoEngine.SimCore.Server/TablesComparison.cs` per wasm-api.md#cross-build-tables (comparing), after T126 (it reads the `MMXT`/`MMXR` constants). It has `TablesStatus` {Match, Diverged, Missing}, `TablesResult(TablesStatus Status, string? FirstMismatch)` with `TablesResult.Missing(string message)`, and `Compare(string build, ReadOnlySpan<byte> inputs, ReadOnlySpan<byte> serverResults, ReadOnlySpan<byte> buildResults)`. Makes T114 pass.
  - The headers must be equal, else `tables: build <b>, results header differs from the server's`.
  - A math row matches when r0 and r1 are bit-identical, or the server value is NaN and the build value is any NaN. Pose rows compare bit for bit.
  - The first mismatch is formatted exactly as the contract's two lines:
    - functions are named by ID; Atan2 shows `(a 0x%08X, b 0x%08X)` and the others `(a 0x%08X)`;
    - then `r0` or `r1`, `server 0x%08X build 0x%08X`;
    - pose rows show `<Standing|Crouched> yaw 0x%04X at (<x>, <y>, <z>)` in "R" decimals, then `<head|torso|legs> <A|B>.<x|y|z>`, `<part> radius`, `sphere centre.<x|y|z>` or `sphere radius`.
- [ ] T129 [US2] Implement `StreamGenerator` in `src/MmoEngine.SimCore.Server/StreamGenerator.cs` per simcore-cli.md#generated-streams, after T123. Makes T118 pass (FR-026 server half).
  - **API**:
    - `NameFor(int seed)` gives `seed-<nnn>`;
    - `Build(int seed, SimConfig baseConfig, CollisionWorld world)` uses rate `{20, 30, 60}[seed % 3]`, 300 steps and 2 + (draw % 3) characters;
    - `Build(ulong seed, SimConfig baseConfig, CollisionWorld world, int stepRateHz, int characterCount, int stepCount)`.
  - Both return an unrecorded `RecordedStream` (expected hashes 0) with header seed = seed, a match salt from the first draw, and the world's checksum.
  - All draws are `SplitMix64.At(seed, counter)` with the counter advancing in one fixed order: the header, then the initial state, then per step, per character ascending.
  - **Initial state**: step 0; character IDs 1..n placed 4 m apart in the flat spawn area that `TestWorldGenerator` names for the scenarios (Phase 3), airborne 0.5 m above `HeightAt`, with velocity 0, `groundNormal` (0, 1, 0), aim 0, Standing, `lastSequence` 0, `activeSlot` 0 and new-weapon state; no projectiles; `nextProjectileId` 1.
  - **Commands**: every step carries one command per character, in ascending index. Valid commands use sequence `lastSequence + 1`, and buttons, yaw, pitch and slot uniform over their valid ranges. At step 0 and step 1, character 0 takes pitch -16384 and 16384.
  - **Invalid commands**: a draw with `draw % 64 == 0` makes the command invalid, cycling `SequenceNotIncreasing` (sequence = `lastSequence`), `UnknownButtons` (a bit of `0xFE00`), `PitchOutOfRange` (+-(16385 + draw % 16383)) and `UnknownWeaponSlot` (>= `input.weaponSlots`).
  - **Rules**: no `System.Random`; every loop is bounded.
- [ ] T130 [US2] Add the `fuzz-all-buttons` definition to `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs` per simcore-cli.md#named-scenario-streams, after T129. Makes T119 pass.
  - It has four characters and builds `StreamGenerator.Build(1000UL, baseConfig, world, stepRateHz ?? 30, 4, 300)`; header seed 1000.
  - Its `.mmrs` file is recorded in Phase 7, because it presses Fire. Do not run `streams record` for it, and never run `--all` here.
  - Not [P]: edits `ScenarioCatalog.cs`.
- [ ] T131 [US2] Report NotComparable outcomes in `simcore streams verify` per simcore-cli.md#streams-verify and recorded-stream-format.md#outcomes, after T125. Makes T117 pass (US2.5).
  - In `src/MmoEngine.SimCore.Cli/Commands/StreamsCommand.cs`, the verify handler maps two refusals to `DivergenceReport.NotComparable(name, error)` (`NOT COMPARABLE stream <name>: <detail>`) and counts them as not comparable:
    - a `CoreFormatException` with `UnsupportedVersion` on the stream's own `version` field (offset 4);
    - `RulesRevisionMismatch`.
  - Every other refusal, including an embedded `steps[i].commands[c].input.version`, stays `REFUSED`.
  - Exit 1 if any stream diverged; else 2 if any is not comparable or refused; else 0.
- [ ] T132 [US2] Implement `simcore agreement prepare` per simcore-cli.md#agreement-prepare, #agreement-work-directory and #manifestjson. Makes the prepare tests of T120 and T122 pass (FR-026 server half).
  - Files:
    - `src/MmoEngine.SimCore.Cli/Agreement/BuildNames.cs`: the seven known builds in contract order, and the default `--require` of `node-interp,node-interp-nojit,chromium-worker`;
    - `src/MmoEngine.SimCore.Cli/Agreement/AgreementManifest.cs`: the manifest model, written with `Utf8JsonWriter` in the contract example's field order and read back with `System.Text.Json`; 64-bit values in x16;
    - `src/MmoEngine.SimCore.Cli/Agreement/RefusalFixtures.cs`: the three refusal streams, plus a small heightfield-only 2x2-cell flat collision file encoded with `CollisionFile.Encode`, whose checksum flat-run-30hz does not name;
    - `src/MmoEngine.SimCore.Cli/Agreement/WorkDirectory.cs`: the writer, with every file written through `AtomicFile` and only under `--work`;
    - `src/MmoEngine.SimCore.Cli/Commands/AgreementCommand.cs`: the `agreement` command with the `prepare` subcommand.
  - Register `agreement` in `src/MmoEngine.SimCore.Cli/Program.cs`.
  - **Refusals before anything is written** (exit 2, with an `agreement prepare: <problem>` line on stderr):
    - `--work` exists and is not an empty directory;
    - `--seeds` is not `<first>-<last>` or one number with 1 <= first <= last <= 999;
    - `--streams` or `--content` is missing;
    - `--config` is invalid (its `path: message` lines).
  - **Writes, in steps 1-8 of the contract**:
    1. Each committed stream (ordinal order) runs through `StreamRunner`'s list overload over the `--content` files keyed by checksum, and its expected hashes are checked. A divergence prints the expected-hash `DIVERGED` report and exits 1; a refusal or NotComparable prints its line and exits 2. Neither case writes a manifest.
    2. The generated `seed-<nnn>` streams (`StreamGenerator.Build` over `--config` and the test world), run through the server, with their expected hashes filled as `streams record` fills them, encoded to `streams/`.
    3. The collision files, as `collision/<x16>.mmcd`.
    4. `server/<stream>.records`.
    5. `batch/<stream>/config.bin`, `initial-state.bin` and `commands.bin` (17 bytes per step via `InputCodec.TryEncode`), for streams whose every step has exactly one command for character index 0.
    6. `refusals/`: `unsupported-version` (flat-run-30hz with the u16 at 4 = 2) and `rules-revision` (the u32 at 8 = revision + 1, trailer rewritten), plus `collision-missing` paired with the small collision file.
    7. `tables/inputs.bin`, `tables/config.bin` and `server/tables.bin`.
    8. `manifest.json`, last. The `perturbation` entry is flat-run-30hz at the step at index (stepCount - 1) / 2; a missing flat-run-30hz exits 2.
  - Output: `prepared <work>: <n> streams (<named> named, <generated> generated), <steps> steps, rules revision <r>`.
  - Not [P]: edits `Program.cs`, and depends on the `BatchStepper`, `StreamRunner`, `CrossBuildTables` and `StreamGenerator` tasks.
- [ ] T133 [US2] Implement `simcore agreement compare` per simcore-cli.md#agreement-compare, after T132, T127 and T128. Makes the compare tests of T121 pass (US2.4, US2.5, FR-026 server half).
  - Files:
    - `src/MmoEngine.SimCore.Cli/Agreement/AgreementComparison.cs`: per known build with a folder, per manifest stream, reads `<build>/<stream>.records` or `<stream>.error.txt` against `server/<stream>.records` and the expected hashes from `streams/<stream>.mmrs`, through `DivergenceReport.CompareBuild` and `BuildError`. Then `tables.bin` or `tables.error.txt` through `TablesComparison` and `TablesResult.Missing`. Statuses are `Match`, `Diverged` and `NotRun`;
    - `src/MmoEngine.SimCore.Cli/Agreement/AgreementReport.cs`: writes `report/agreement.json` in the contract's shape and `report/divergences/<build>/<stream>.txt` and `tables.txt` via `AtomicFile`, and appends the `--summary` Markdown section in one write;
    - the `compare` subcommand in `src/MmoEngine.SimCore.Cli/Commands/AgreementCommand.cs`.
  - **Output lines**, one per known build in contract order:
    - `<b>: match, <n> streams, <steps> steps, tables match`;
    - `<b>: diverged in <d> of <n> streams; first: <stream> at step <k>; tables <match|diverged|missing>`;
    - `<b>: streams match, <n> streams, <steps> steps; tables <diverged|missing>`;
    - `<b>: not run`.
  - Then each diverged build's first report (or tables line), then `agreement: match`, `agreement: diverged`, `agreement: not comparable` or `agreement: cannot run: <reason>`.
  - **Exit 2**:
    - the manifest is missing or unreadable;
    - a server records file or `server/tables.bin` is missing;
    - a required build is `NotRun`;
    - `--require` names an unknown build;
    - the manifest's `rulesRevision` or a `formats` version differs from the core's. That case prints `NOT COMPARABLE work <work>: rules revision <m> in the work directory; this core implements rules revision <c>` or `<format name> format version <m> in the work directory; this reader supports version <c>`.
  - Otherwise exit 1 if any build is `Diverged`, else 0. Unknown folders are ignored.
- [ ] T134 [US2] Run the phase's coverage check:
  - `dotnet test tests/MmoEngine.SimCore.Tests --collect:"XPlat Code Coverage" --results-directory <unique temp>/coverage`;
  - then `python3 tools/coverage_gate.py --base origin/main --coverage-dir <unique temp>/coverage`.
  - Every file this phase adds or changes must be at or above 95% line coverage and appear in the report: `src/MmoEngine.SimCore/Hashing/SplitMix64.cs`, `Batch/BatchStepper.cs`, `Batch/LoadResult.cs`, `Batch/StepError.cs`, `Batch/StreamRunner.cs`, `Batch/CrossBuildTables.cs` and `World/SimWorld.cs`; `src/MmoEngine.SimCore.Server/StateFieldReader.cs`, `StreamComparison.cs`, `DivergenceReport.cs`, `TablesComparison.cs`, `StreamGenerator.cs` and `ScenarioCatalog.cs`; and `src/MmoEngine.SimCore.Cli/Agreement/*.cs`, `Commands/AgreementCommand.cs`, `Commands/StreamsCommand.cs` and `Program.cs`.
  - Add a Theory row or a Fact to the owning test class above for any uncovered branch, never a test that only proves "does not throw".

**Checkpoint**: These checks are green:
- `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes` exit 0.
- The Independent Test filter passes.
- `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify` exits 0 with every committed stream `match`.
- `git diff --exit-code simcore/` is clean: no stream was re-recorded, and `SimCoreVersion.RulesRevision` is still 1.
- The coverage gate from the last task exits 0.

---

## Phase 5: User Story 3 - The determinism rules are enforced by the build, not by review (Priority: P1)

**Goal**: Turn the analyzer skeleton into the nine determinism rules (MMO1001-MMO1009). Each gets its message, allowlists, precedence and release tracking. Prove every rule on violating and allowed samples, run the analyzer over the core's own sources as a backstop, and document and test the trigonometric error bounds on the server build.

**Independent Test**: `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests` passes: every rule row, the backstop over `src/MmoEngine.SimCore`, and the end-to-end failing build. `dotnet build MmoEngine.sln -warnaserror` exits 0 with the analyzer active on the core, and `Math/FixedTableBoundsTests.EveryInDomainRowIsWithinItsBound` passes.

### Tests (write first, confirm red)

- [ ] T135 [US3] Create the analyzer test harness `tests/MmoEngine.SimCore.Analyzers.Tests/Support/AnalyzerHarness.cs` (namespace `MmoEngine.SimCore.Analyzers.Tests`, `internal static class AnalyzerHarness`), per contracts/analyzer-rules.md#tests "Harness". Every later test task in this phase except DescriptorTests, WiringTests, BuildTests and the Math tests uses it. It provides:
  - `RepoRoot`: the directory holding `MmoEngine.sln`, found by walking up from `AppContext.BaseDirectory`. `ReadRepoText(string relativePath)` opens files with `FileAccess.Read` only.
  - `References`: `Basic.Reference.Assemblies.Net100.References.All`. `CoreReferences`: those plus `MetadataReference.CreateFromFile(typeof(System.IO.Hashing.XxHash64).Assembly.Location)`.
  - `SampleOptions`: `CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true)`. `CoreOptions`: the same with `allowUnsafe: false`, which is the core's setting. Both are parsed with `CSharpParseOptions(LanguageVersion.Latest)`.
  - `Task<SampleResult> AnalyzeMemberAsync(string member, CSharpCompilationOptions? options = null)`. It wraps `member` as the body of `public static class C` in `namespace Sample;` under the usings `System`, `System.Collections.Generic`, `System.Collections.Immutable`, `System.Diagnostics`, `System.Linq`, `System.Numerics`, `System.Runtime.Intrinsics`, `System.Threading` and `System.Threading.Tasks`, and records the member's `TextSpan` in the file.
  - `Task<AnalysisResult> AnalyzeSourcesAsync(IReadOnlyList<(string Path, string Text)> files, CSharpCompilationOptions? options = null, IEnumerable<MetadataReference>? references = null)` and `AnalyzeTreesAsync(IReadOnlyList<SyntaxTree> trees, ...)`.
  - Both run `compilation.WithAnalyzers([new DeterminismAnalyzer()], new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true))` and `GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken)`.
  - Results: compiler errors (`Severity == Error` from `compilation.GetDiagnostics()`) and analyzer diagnostics ordered by (file path, span start, ID). `Describe(Diagnostic)` formats `path(line,col): ID message [suppressed]` for failure messages.
  - `CoreSourceTrees()`: every `src/MmoEngine.SimCore/**/*.cs` under `RepoRoot` that has no `bin` or `obj` path segment, read with `FileAccess.Read`, ordinal path order, parsed with its repo-relative path.
- [ ] T136 [US3] Write failing test `RuleTests.EachSampleProducesExactlyItsRule(string ruleId, int count, string member)` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` (US3.1), per contracts/analyzer-rules.md#tests.
  - The Theory takes one `[MemberData]` per rule. This task creates the body and `VectorRows` (`TheoryData<string, int, string>`). Each row asserts, in order: (1) `CompilerErrors` is empty; (2) the diagnostics' IDs equal `Enumerable.Repeat(ruleId, count)` exactly (an allowed row has `""`, 0 and no diagnostics); (3) every diagnostic's `Severity == DiagnosticSeverity.Error`; (4) every diagnostic's `Location.SourceSpan` lies inside the member span.
  - MMO1001 rows, count 1 unless stated:
    - `public static float M(float x) => Vector3.Dot(new Vector3(x), Vector3.UnitX);` (one, not three)
    - `public static int M() => Vector<float>.Count;`
    - `public static float M(Vector128<float> a) => Vector128.Sum(a);`
    - `public static float M(Vector128<float> a) => Vector128.Dot(a, a);`
    - `public static bool M() => Vector128.IsHardwareAccelerated;` (MMO1001, not MMO1008)
    - `public static object M() => new List<Quaternion>();`
    - `public static void M() { var v = Matrix4x4.Identity; }`
    - `public static bool M() => System.Runtime.Intrinsics.X86.Sse.IsSupported;`
  - Branch rows, to clear the coverage gate:
    - `public static Vector3 P { get; }` (1)
    - `public static event Action<Vector4>? E;` (1)
    - `public static unsafe void M(Vector2* p) { }` (1)
    - `public static readonly Plane F = default;` (1: the initializer counts as its declaration)
    - `public static Vector128<int> M(Vector128<int> a) => a;` (2: the return type and the parameter)
  - Allowed rows:
    - `public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Add(a, b) * b - Vector128.Create(1f);`
    - `public static float M(float x, float y) => x * y + 1f;`
  - Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~RuleTests.EachSampleProducesExactlyItsRule"` and confirm the violating rows fail, because the skeleton reports nothing.
- [ ] T137 [US3] Add `PlatformMathRows` (MMO1002) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs`, and write failing test `RuleTests.PlatformMathMessageNamesTheReplacement(string member, string replacement)` (US3.2).
  - Rows, count 1 each:
    - `public static float M(float x) => MathF.Sin(x);`
    - `public static float M(float x) => float.Atan2(x, 1f);`
    - `public static T M<T>(T x) where T : ITrigonometricFunctions<T> => T.Cos(x);`
    - `public static Func<float, float> M() => MathF.Tan;`
    - `public static unsafe delegate*<float, float> M() => &MathF.Sin;`
    - `public static float M(float x) => MathF.Pow(x, 2f);`
    - `public static float M(float x) => MathF.ReciprocalSqrtEstimate(x);`
    - `public static float M(float x) => MathF.Max(x, 0f);`
    - `public static float M(float x) => MathF.Round(x, 2);`
    - `public static float M(float x) => x % 2f;`
    - `public static void M(ref float x) { x %= 2f; }`
    - `public static float M(float x) => Math.Clamp(x, 0f, 1f);` (outside SimMath)
  - Allowed rows:
    - `public static float M(float x) => MathF.Sqrt(MathF.Abs(x)) + MathF.Floor(x) + MathF.Round(x) + MathF.Round(x, MidpointRounding.ToZero) + MathF.CopySign(x, -1f) + MathF.PI;`
    - `public static string M() => nameof(MathF.Sin);`
    - `public static int M(int i) => Math.Max(i, 0) + Math.Clamp(i, 0, 9);`
    - `public static uint M(float x) => float.IsFinite(x) ? BitConverter.SingleToUInt32Bits(x) : 0u;`
  - The message Theory asserts exactly one MMO1002 per row. Its `GetMessage(CultureInfo.InvariantCulture)` starts with `'` plus the member's display (for example `'MathF.Sin(float)'`), contains ` is platform math, which is not correctly rounded and can differ between builds; use `, and ends with the replacement from contracts/analyzer-rules.md#replacement-map-mmo1002-messages:
    - `MathF.Sin` gives `'SimMath.Sin'`.
    - `float.Atan2` gives `'SimMath.Atan2'`.
    - `MathF.Max` gives `'SimMath.Max'`.
    - `Math.Clamp(x, 0f, 1f)` gives `'SimMath.Clamp'`.
    - `float.SinPi` gives `'SimMath.SinCosBinaryAngle' for binary angles`.
    - `MathF.ReciprocalEstimate` gives `'1f / x'`.
    - `MathF.ReciprocalSqrtEstimate` gives `'1f / SimMath.Sqrt(x)'`.
    - `MathF.Round(x, 2)` gives `'MathF.Round(x)' on a scaled value, or 'SimMath.RoundToInt'`.
    - `float.Lerp` gives `'a + ((b - a) * t)' written as separate float operations`.
    - `float.DegreesToRadians` gives `a multiplication by a float constant`.
    - `MathF.Pow` and `x % 2f` give the fallback `scalar float arithmetic, or add a core function with a documented error bound`.
  - Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~RuleTests"` and confirm both fail.
- [ ] T138 [US3] Add `FusedRows` (MMO1003) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs`, and write failing test `RuleTests.FusedCallsReportOnlyTheFmaRule(string member)` (US3.3).
  - Rows, count 1:
    - `public static float M(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);`
    - `public static float M(float a) => float.MultiplyAddEstimate(a, a, a);`
    - `public static Vector128<float> M(Vector128<float> a) => Vector128.FusedMultiplyAdd(a, a, a);` (MMO1003, not MMO1001)
    - `public static Vector128<float> M(Vector128<float> a) => System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(a, a, a);` (MMO1003, not MMO1001)
  - Allowed row: `public static float M(float a, float b, float c) => a * b + c;`.
  - The Theory has the same four calls plus `public static double M(double d) => Math.FusedMultiplyAdd(d, d, d);`. Assert that the diagnostics whose `SourceSpan` equals the invocation's span are exactly `[MMO1003]`, and that every other diagnostic is MMO1007 located on a `double` declaration (the parameter `d` and the return type), never on the call. Run with `--filter "FullyQualifiedName~RuleTests"` and confirm it fails.
- [ ] T139 [US3] Add `HashOrderRows` (MMO1004) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` (US3.4).
  - Rows, count 1:
    - `public static int M(Dictionary<int, int> d) { var s = 0; foreach (var kv in d) s += kv.Value; return s; }`
    - `public static int M(HashSet<int> s) => s.First();`
    - `public static int[] M(Dictionary<int, int> d) => d.Keys.ToArray();`
    - `public static List<int> M(HashSet<int> s) => new List<int>(s);`
    - `public static int[] M(HashSet<int> s) => [.. s];`
    - `public static string M(HashSet<int> s) => string.Join(",", s);`
    - `public static IEnumerable<int> M(ImmutableHashSet<int> s) => s;`
    - `public static int M(IReadOnlyDictionary<int, int> d) { var n = 0; foreach (var k in d.Keys) n += k; return n; }`
    - `public static void M(Dictionary<int, int> d, int[] dst) => d.Keys.CopyTo(dst, 0);`
    - `public static IEnumerable<int> M(Dictionary<int, int> d) => from kv in d select kv.Value;`
  - Allowed rows:
    - `public static int M(Dictionary<int, int> d, int k) => d.TryGetValue(k, out var v) ? v + d.Count : d.Keys.Count;`
    - `foreach` over a `SortedDictionary<int, int>` in the same shape as the first row
    - `public static void M(HashSet<int> a, HashSet<int> b) => a.UnionWith(b);`
  - Run with `--filter "FullyQualifiedName~RuleTests.EachSampleProducesExactlyItsRule"` and confirm the MMO1004 rows fail.
- [ ] T140 [US3] Add `AmbientRows` (MMO1005) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` (US3.5).
  - Rows, count 1:
    - `public static long M() => DateTime.Now.Ticks;`
    - `public static long M() => DateTime.UtcNow.Ticks;`
    - `public static long M() => Stopwatch.GetTimestamp();`
    - `public static int M() => Environment.TickCount;` (MMO1005, not MMO1008)
    - `public static int M() => new Random(42).Next();` (seeded still counts; one diagnostic)
    - `public static int M() => Random.Shared.Next();`
    - `public static Guid M() => Guid.NewGuid();`
    - `public static int M(string s) => s.GetHashCode();`
    - `public static int M(int a, int b) => HashCode.Combine(a, b);`
    - `public static DateTimeOffset M() => TimeProvider.System.GetUtcNow();`
  - Allowed rows:
    - `public static int M(int a) => a.GetHashCode() ^ 17;`
    - `public static long M(long step, long stepTicks) => step * stepTicks;`
  - Confirm the MMO1005 rows fail.
- [ ] T141 [US3] Add `BlockingRows` (MMO1006) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` (US3.6).
  - Rows, count 1 unless stated:
    - `private static readonly object Gate = new(); public static void M() { lock (Gate) { } }`
    - `private static readonly Lock L = new();`
    - `public static void M(Lock l) { lock (l) { } }` (count 2: the parameter and the statement)
    - `public static void M(Task t) => t.Wait();`
    - `public static int M(Task<int> t) => t.Result;`
    - `public static int M(Task<int> t) => t.GetAwaiter().GetResult();`
    - `public static void M() => Thread.Sleep(1);`
    - `public static void M() => Monitor.Enter(typeof(C));`
    - `[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)] public static void M() { }`
    - `public static void M(int[] a) => Parallel.For(0, a.Length, i => a[i]++);`
  - Allowed rows:
    - `public static async Task<int> M(Task<int> t) => await t;`
    - `public static int M(ref int x) => Interlocked.Increment(ref x);`
  - Confirm the MMO1006 rows fail.
- [ ] T142 [US3] Add `NumericTypeRows` (MMO1007) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` (US3.7).
  - Rows:
    - `public static float M(float x) => (float)(x * 0.5);` (1)
    - `public static double M(float x) => x;` (2: the return type and the conversion)
    - `public static int M(int a) => (int)(nint)a;` (1)
    - `public static void M() { var h = (Half)1; }` (1)
  - Branch rows:
    - `public static double F = 1.0;` (1)
    - `public static Half P { get; }` (1)
    - `public static event Func<nint>? E;` (1)
    - `public static unsafe void M(nuint* p) { }` (1)
    - `public static void M(System.Runtime.InteropServices.NFloat n) { }` (1)
  - Allowed rows:
    - `private const double Scale = 1.0 / 3.0; public static float M(float x) => x * (float)Scale;`
    - `public static float M(float x) => x * (float)(Math.PI / 180.0);`
  - Confirm the MMO1007 rows fail.
- [ ] T143 [US3] Add `PlatformPathRows` (MMO1008) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` (US3.7).
  - Rows, count 1:
    - `public static bool M() => OperatingSystem.IsBrowser();`
    - `public static int M() => IntPtr.Size;` (MMO1008, not MMO1007)
    - `public static int M() => Environment.ProcessorCount;`
    - `public static object? M() => typeof(MathF).GetMethod("Sin");`
    - `public static object M(dynamic d) => d.Foo();`
    - a member whose body is `#if DEBUG` / `return 1;` / `#else` / `return 0;` / `#endif` on separate lines (one diagnostic, on the `#if` line)
    - `public static void M(bool b) => Debug.Assert(b);`
    - `[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "M")] public static extern void Call(object o);`
  - Allowed row: `public static object M() => typeof(int);`.
  - Confirm the MMO1008 rows fail.
- [ ] T144 [US3] Add `ConversionRows` (MMO1009) to `RuleTests.EachSampleProducesExactlyItsRule` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs`, and write failing tests `RuleTests.SimMathMayClampAndCastFloats` and `RuleTests.ConversionMessagesGiveTheirReason(string member, string reason)` (US3.7).
  - Rows, count 1:
    - `public static int M(float x) => (int)x;`
    - `public static byte M(float x) => (byte)(x * 2f);`
    - `public static float M(ulong u) => u;`
    - `public static int M(float x) => Convert.ToInt32(x);`
    - `public static int M(float x) => int.CreateSaturating(x);`
    - `public static long M(float x) => long.CreateTruncating(x);`
    - `public static DayOfWeek M(float x) => (DayOfWeek)x;`
    - `public static UInt128 M(float x) => (UInt128)x;`
  - Allowed rows:
    - `public static int M() => (int)2.5f;`
    - `public static float M(int i) => i * 0.5f;`
    - `public static float M(ulong h) => (float)(uint)(h >> 40);`
    - `public static int M(long l) => int.CreateSaturating(l);`
    - `public static DayOfWeek M(int i) => (DayOfWeek)i;`
  - `SimMathMayClampAndCastFloats` uses `AnalyzeSourcesAsync`. File 1 declares `namespace MmoEngine.SimCore.Math { public static class SimMath { ... } }` with:
    - `Clamp(float x, float lo, float hi) => System.Math.Clamp(x, lo, hi)`;
    - `FloorToInt`, `RoundToInt` and `TruncateToInt`, each `(int)System.MathF.Floor(x)` / `(int)System.MathF.Round(x)` / `(int)x` behind a NaN and range check;
    - `Other(float x) => (int)x`;
    - `FromBits(ulong u) => (float)u`.
  - File 2 declares `namespace Sample { public static class D { public static int M(float x) => (int)System.Math.Clamp(x, 0f, 1f); } }`.
  - Assert the diagnostics are exactly:
    - one MMO1009 on `(int)x` in `Other`;
    - one MMO1009 on `(float)u` in `FromBits` (ulong to float is not exempt, even in SimMath);
    - in `D.M`, MMO1002 on the `Clamp` call and MMO1009 on the cast.
  - Nothing is reported in `Clamp`, `FloorToInt`, `RoundToInt` or `TruncateToInt`.
  - `ConversionMessagesGiveTheirReason` takes the rows `(int)x`, `Convert.ToInt32(x)`, `int.CreateSaturating(x)` and `(DayOfWeek)x`. Each message ends with `converts a floating-point value to an integer, which gives different results between builds for NaN and out-of-range values; use 'SimMath.FloorToInt', 'SimMath.RoundToInt' or 'SimMath.TruncateToInt', which saturate first`. The row `float M(ulong u) => u` ends with `converts a 'ulong' to 'float', which the WebAssembly build rounds through 'double' and so can differ by one ulp; narrow it to 'uint' or 'long' first` (contracts/analyzer-rules.md#messages).
  - Run with `--filter "FullyQualifiedName~RuleTests"` and confirm they fail.
- [ ] T145 [US3] Write failing tests `RuleTests.AttributeApplicationsAreNotReported` and `RuleTests.GeneratedCodeIsAnalyzed(string fileName, string header)` in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` (FR-014), per contracts/analyzer-rules.md#attribute-applications and #generated-code.
  - `AttributeApplicationsAreNotReported` analyzes three files together:
    - the sample class with a member `[Obsolete("x")] public static int M() => 1;`;
    - an `obj/Sample.AssemblyInfo.cs` with a `// <auto-generated/>` header holding `[assembly: System.Reflection.AssemblyCompany("x")]`, `AssemblyConfiguration`, `AssemblyFileVersion`, `AssemblyInformationalVersion`, `AssemblyProduct`, `AssemblyTitle`, `AssemblyVersion("1.0.0.0")`, `AssemblyMetadata("IsTrimmable", "True")` and `AssemblyMetadata("IsAotCompatible", "True")`;
    - an `obj/Sample.Probe.cs` with the same generated header.
  - With `Sample.Probe.cs` holding an empty class, the result is zero diagnostics. With it holding `public static object? P() => typeof(System.MathF).GetMethod("Sin");`, the result is exactly one MMO1008, located in `Sample.Probe.cs`.
  - `GeneratedCodeIsAnalyzed` has two rows: `("X.g.cs", "")` and `("X.cs", "// <auto-generated/>")`. A file holding `public static class G { public static float M(float x) => System.MathF.Sin(x); }` reports exactly one MMO1002.
  - Run with `--filter "FullyQualifiedName~RuleTests"` and confirm the probe row and both generated rows fail.
- [ ] T146 [US3] Write failing tests in `tests/MmoEngine.SimCore.Analyzers.Tests/SuppressionTests.cs` (SC-003, FR-014), per contracts/analyzer-rules.md#severity-and-suppression. The sample is `public static float M(float x) => MathF.Sin(x);`.
  - `SuppressionTests.SeverityCannotBeLowered(string mechanism)` has three rows:
    - `pragma` wraps the member in `#pragma warning disable MMO1002` / `#pragma warning restore MMO1002`;
    - `specific-suppress` sets `SpecificDiagnosticOptions` to `{ ["MMO1002"] = ReportDiagnostic.Suppress }` (what `NoWarn` and `severity = none` map to);
    - `warnings-not-errors` sets `GeneralDiagnosticOption = ReportDiagnostic.Default`.

    Each asserts exactly one diagnostic, with `Id == "MMO1002"`, `Severity == Error` and `IsSuppressed == false`.
  - `SuppressionTests.SuppressMessageIsReportedAsSuppressed(string scope)` has two rows:
    - `member` puts `[System.Diagnostics.CodeAnalysis.SuppressMessage("Determinism", "MMO1002")]` on the member;
    - `assembly` adds a file with `[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Determinism", "MMO1002")]`.

    Each asserts exactly one MMO1002 with `IsSuppressed == true`.
  - Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~SuppressionTests"` and confirm they fail.
- [ ] T147 [P] [US3] Write failing tests in `tests/MmoEngine.SimCore.Analyzers.Tests/DescriptorTests.cs` (FR-014, SC-003), per contracts/analyzer-rules.md#release-tracking.
  - `DescriptorTests.DescriptorsMatchTheReleaseFile` checks `new DeterminismAnalyzer().SupportedDiagnostics`:
    - the IDs are exactly `MMO1001` through `MMO1009`, in that order;
    - each has `DefaultSeverity == DiagnosticSeverity.Error`, `IsEnabledByDefault == true`, `Category == "Determinism"`, and `CustomTags` containing `WellKnownDiagnosticTags.NotConfigurable`.

    It reads `src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Unshipped.md`, located from `MmoEngine.sln` upward from `AppContext.BaseDirectory` and opened with `FileAccess.Read`, and parses its `### New Rules` table. The table lists the same nine IDs, each `Determinism | Error | DeterminismAnalyzer`. `AnalyzerReleases.Shipped.md` has no rule rows.
  - `DescriptorTests.TitlesAndMessageFormatsAreTheContracts(string id, string title, string messageFormat)` has nine `InlineData` rows. Each holds the Title from contracts/analyzer-rules.md#rules and the message format from #messages verbatim, and asserts both strings are equal.
  - Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~DescriptorTests"` and confirm they fail, because the skeleton has empty `SupportedDiagnostics`.
- [ ] T148 [P] [US3] Write the wiring guard tests in `tests/MmoEngine.SimCore.Analyzers.Tests/WiringTests.cs` (FR-014), per contracts/analyzer-rules.md#wiring and #roslyn-pin. They read project files with `FileAccess.Read` from the repo root, found by `MmoEngine.sln`. Each test checks the real files through a private static check function that returns problem lines, and also runs that function on in-memory mutated XML, so the check cannot pass vacuously.
  - `WiringTests.CoreProjectAttachesTheAnalyzerPrivately(string caseName)`:
    - The `real` row reads `src/MmoEngine.SimCore/MmoEngine.SimCore.csproj` and `Directory.Build.props`. It expects no problems: exactly one `ProjectReference` to `MmoEngine.SimCore.Analyzers.csproj` with `OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"` and `PrivateAssets="all"`; `ImplicitUsings` set to `disable`; no `RunAnalyzers`, `RunAnalyzersDuringBuild`, `NoWarn` or `WarningsNotAsErrors` naming MMO; no `<Compile Remove`.
    - Mutated rows: `PrivateAssets` missing, `ReferenceOutputAssembly="true"`, `<NoWarn>MMO1002</NoWarn>`, `<RunAnalyzers>false</RunAnalyzers>`, `<Compile Remove="Math/**" />` and `ImplicitUsings` `enable`. Each yields exactly its one problem line.
  - `WiringTests.OnlyTheCoreAttachesTheAnalyzer`: no `*.csproj` under `src/`, `tests/` or `benchmarks/` (outside bin/obj) other than the core's references the analyzer project with `OutputItemType="Analyzer"`. The analyzer test project's own reference is a normal `ProjectReference` without that attribute. This test enumerates projects, so slice 2's Wasm projects are covered automatically.
  - `WiringTests.RoslynPinMatchesTheSdkBand(string sdkVersion, string pin, string expectedProblem)`. The check maps band `10.0.1xx` to compiler `5.0.0`. Rows:
    - the real `global.json` `sdk.version` and the real `Directory.Packages.props` `Microsoft.CodeAnalysis.CSharp` version, expecting no problem;
    - `("10.0.115", "5.0.0", "")`;
    - `("10.0.115", "5.3.0", "Directory.Packages.props: Microsoft.CodeAnalysis.CSharp is 5.3.0; SDK band 10.0.1xx in global.json needs 5.0.0")`;
    - `("10.0.200", "5.0.0", "global.json: SDK band 10.0.2xx has no known compiler version; update the band table with the pin")`.
  - Phase 1 already established the real-file wiring, so the real rows pass when written. The mutated rows are written before the check function, so they fail first. Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~WiringTests"`.
- [ ] T149 [US3] Write failing tests in `tests/MmoEngine.SimCore.Analyzers.Tests/BackstopTests.cs` (US3-IT, SC-003), per contracts/analyzer-rules.md#severity-and-suppression.
  - `BackstopTests.CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes` runs `AnalyzerHarness.AnalyzeTreesAsync(AnalyzerHarness.CoreSourceTrees(), AnalyzerHarness.CoreOptions, AnalyzerHarness.CoreReferences)`. It asserts:
    - the tree paths include `src/MmoEngine.SimCore/Math/SimMath.cs` and `src/MmoEngine.SimCore/Math/SimMathCoefficients.g.cs` (generated files are analyzed);
    - no path contains a `bin` or `obj` segment;
    - zero compiler errors;
    - zero analyzer diagnostics, suppressed ones included.

    On failure it prints every diagnostic through `AnalyzerHarness.Describe`, which marks suppressed ones `[suppressed]`.
  - `BackstopTests.BackstopReportsAnInjectedViolation` uses the same trees plus one tree `Injected.cs` holding `namespace MmoEngine.SimCore; internal static class Injected { internal static int M(System.Collections.Generic.Dictionary<int, int> d) { var s = 0; foreach (var kv in d) s += kv.Value; return s; } }`. It asserts exactly one diagnostic, MMO1004, in `Injected.cs`.
  - `BackstopTests.BackstopCountsAnAssemblySuppressedDiagnostic` uses the same trees plus `Injected.cs` holding `[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Determinism", "MMO1002")]` and a `System.MathF.Sin` call. It asserts exactly one MMO1002 with `IsSuppressed == true`, which the main test's zero-diagnostics predicate would count.
  - Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~BackstopTests"`. The two injected tests fail, because the skeleton reports nothing. The main test can pass at this point only because no rule exists yet; T157 (the MMO1004 green task) and T160 (the backstop fix) are what make it meaningful.
- [ ] T150 [P] [US3] Write failing test `BuildTests.ABuildWithAViolationFails` in `tests/MmoEngine.SimCore.Analyzers.Tests/BuildTests.cs` (US3-IT, FR-014), per contracts/analyzer-rules.md#tests.
  - Setup in a unique directory under `Path.GetTempPath()` (best-effort delete at the end):
    - `Sample.csproj`: `net10.0`, `IsTrimmable` and `IsAotCompatible` true, `ImplicitUsings` disable, `<Analyzer Include="<abs path>" />`. The path points at the analyzer project's own build output `src/MmoEngine.SimCore.Analyzers/bin/<configuration>/netstandard2.0/MmoEngine.SimCore.Analyzers.dll`, with the configuration taken from the test's base directory. It is not the test-bin copy, which coverlet instruments. Assert `File.Exists` first, with the path in the message.
    - Empty `Directory.Build.props` and `Directory.Build.targets`, so nothing above the temp directory is imported.
    - A copy of the repo's `global.json`.
    - `C.cs`: `public static class C { public static int Sum(System.Collections.Generic.Dictionary<int, int> d) { int s = 0; foreach (var kv in d) s += kv.Value; return s; } }`.
  - It runs `dotnet build Sample.csproj -nologo -tl:off -clp:NoSummary -nodeReuse:false -p:UseSharedCompilation=false`. `dotnet` comes from `DOTNET_HOST_PATH` when set; inherited `MSBuild*` and `MSBUILD*` variables are removed from the child's environment; there is a 5-minute timeout. The output goes to `TestContext.Current.TestOutputHelper`.
  - It asserts:
    - exit code ≠ 0;
    - the distinct output lines matching `error MMO\d{4}` are exactly one, and it contains `C.cs(` and `error MMO1004`;
    - no output line contains both `AssemblyInfo.cs` and `MMO`.
  - Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~BuildTests"` and confirm it fails, because the skeleton analyzer reports nothing and the build exits 0.
- [ ] T151 [P] [US3] Write failing test `MathReferenceTests` in `tests/MmoEngine.SimCore.Tests/Math/MathReferenceTests.cs`, then the test-only helper `tests/MmoEngine.SimCore.Tests/Support/MathReference.cs` (namespace `MmoEngine.SimCore.Tests.Support`; write `System.Math` fully qualified, because `MmoEngine.SimCore.Math` shadows it). This is one task: the helper is test code that guards the bound tests against vacuity, so the tests come first and are confirmed red by the compile failure, then the helper is written.
  - `MathReference.Ulp(double reference)` is `System.Math.ScaleB(1.0, System.Math.Max(System.Math.ILogB(System.Math.Abs(reference)), -126) - 23)`.
  - `MathReference.UlpError(float result, double reference)` returns:
    - for a zero `reference`: 0 when `result` is ±0, else `double.PositiveInfinity`;
    - for a NaN `result` with a finite `reference`: `double.PositiveInfinity`;
    - otherwise `|(double)result - reference| / Ulp(reference)`.
  - `MathReference.Reference(int functionId, float a, float b)` returns `(double R0, double R1)` for the function IDs of contracts/wasm-api.md#cross-build-tables: 1 Sin, 2 Cos, 3 Tan, 4 SinCos (sine, cosine), 5 Asin, 6 Acos, 7 Atan, 8 Atan2(y = a, x = b), and 10 Sqrt, all through `System.Math` in double. Function 9 SinCosBinaryAngle uses `double.SinPi(angle / 32768.0)` and `double.CosPi(angle / 32768.0)`, with `angle` the low 16 bits of a's bits as `ushort`.
  - `MathReferenceTests.UlpErrorMeasuresInTheReferencesBinade(uint resultBits, double reference, double expected)` has these rows:
    - `1f` vs `1.0` gives 0;
    - `BitIncrement(1f)` vs `1.0` gives 1;
    - `BitDecrement(1f)` vs `1.0` gives 0.5;
    - `1f` vs `1.0 + 2^-24` gives 0.5;
    - bits `0x00000001` vs `2^-148` gives 1 (subnormal spacing 2^-149);
    - `+0` and `-0` vs `0.0` give 0;
    - `float.Epsilon` vs `0.0` gives +∞;
    - NaN vs `1.0` gives +∞.
  - `MathReferenceTests.ReferenceUsesTheDoubleFunctions` checks Sin, Atan2 and the binary-angle rows at angles 0, 0x4000, 0x8000 and 0xC000. It expects sine 0, 1, 0 and -1 and cosine 1, 0, -1 and 0, compared exactly as doubles.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MathReferenceTests"`: red (compile) before the helper, green after.
- [ ] T152 [US3] Write failing test `FixedTableBoundsTests.EveryInDomainRowIsWithinItsBound(int functionId)` in `tests/MmoEngine.SimCore.Tests/Math/FixedTableBoundsTests.cs` (US3.8a, SC-004 bound half, FR-013), per research.md R3 and contracts/wasm-api.md#cross-build-tables. The class is in namespace `MmoEngine.SimCore.Tests.Math`, with `System.Math` written fully qualified.
  - Given: `CrossBuildTables.BuildInputs()` (Phase 4) and its results from `CrossBuildTables.Evaluate(inputs, configBlock)`, where the config block is default.json's block as `TestConfigs` encodes it. The test parses `MMXT` math rows (u8 function ID, u32 a bits, u32 b bits at 16 + 12·i) and `MMXR` results (u32 r0 and r1 bits at 16 + 8·i) with `BinaryPrimitives`.
  - When the rows for `functionId` (`InlineData` 1 to 10) are compared with `MathReference.Reference`:
    - A row is out of domain when, judged from the input alone, a or b is NaN; a is ±∞ for functions 1 to 4; |a| > 1 for functions 5 and 6; or a < 0 (and not -0) for function 10.
    - Each such row asserts that `float.IsNaN(r0)` (and `float.IsNaN(r1)` for function 4) and that the double reference is NaN.
    - Each in-domain row asserts that the reference is not NaN, which ties the domain predicate and the reference together row by row.
    - The test counts the out-of-domain rows explicitly, never by NaN comparison, and asserts that the count is > 0 for functions 1 to 8 and 10, and 0 for function 9.
  - Then every in-domain result is within its bound by `MathReference.UlpError`, using the `SimMathBounds` constants:
    - Sin, Cos and SinCos (r0 and r1): `SinCosMaxUlp` (1) for |a| ≤ `SimMathBounds.ReductionLimit` (12867f), `SinCosLargeMaxUlp` (2) above;
    - Tan: `TanMaxUlp` (1.5) for |a| ≤ 12867, `TanLargeMaxUlp` (2.5) above;
    - Asin and Acos: `AsinAcosMaxUlp` (1);
    - Atan: `AtanMaxUlp` (1);
    - Atan2: `Atan2MaxUlp` (2);
    - SinCosBinaryAngle (sine and cosine): `SinCosBinaryAngleMaxUlp` (1);
    - Sqrt: r0 bits equal `(float)System.Math.Sqrt(a)` exactly (`SqrtMaxUlp` 0.5, correctly rounded).

    For the single-result functions, r1 bits are 0. The endpoints (±0, the subnormals, π/4, 12867 and their neighbours, ±1, ±½, the breakpoints) are included.
  - Every function ID 1 to 10 appears in the table, with at least the contract's row counts:
    - functions 1 to 4: ≥ 4,096 in-domain rows;
    - functions 5 to 8: ≥ 4,096;
    - function 9: exactly 65,536, each angle once;
    - function 10: ≥ 1,024.
  - Failures list up to 20 rows as `<Function> row <index> a 0x%08X b 0x%08X: r0 0x%08X is <err:F3> ulp from <reference:R>, bound <bound>`.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~FixedTableBoundsTests"` and confirm it fails to compile, because `SimMathBounds` does not exist.
- [ ] T153 [US3] Write the exhaustive sweep `ExhaustiveSweepTests.EachFunctionStaysWithinItsBoundOverItsWholeDomain(string function)` in `tests/MmoEngine.SimCore.Tests/Math/ExhaustiveSweepTests.cs` (research R3; not a gate, owns no scenario). It is the feature's one deliberate skip: `[Theory(Skip = "Set MMO_EXHAUSTIVE_MATH=1 to run the exhaustive sweeps (research.md R3)", SkipUnless = nameof(Enabled))]`, with `public static bool Enabled => Environment.GetEnvironmentVariable("MMO_EXHAUSTIVE_MATH") == "1";`.
  - Rows and domains:
    - `Sin`, `Cos`, `SinCos` and `Tan`: every float with |x| ≤ 12867 against the small-argument bound, and every finite float above against the large-argument bound;
    - `Asin` and `Acos`: every float in [-1, 1];
    - `Atan`: every float, with NaN giving NaN and ±∞ giving ±π/2 within 1 ulp;
    - `Atan2`: 2^22 directions k·2π/2^22 at radii 2^-100, 2^-20, 1, 2^20 and 2^100 (points rounded to float), plus 67,108,864 pairs of finite floats drawn with the core's SplitMix64 from a seed constant declared in the test and printed on failure;
    - `SinCosBinaryAngle`: all 65,536 angles.
  - It uses `MathReference.UlpError` and the `SimMathBounds` constants, sweeping with `Parallel.For` and a per-partition maximum (tests are not analyzed).
  - It writes one line per function to `TestContext.Current.TestOutputHelper`: `SIMMATH <function> max <ulp:F3> ulp at a 0x%08X b 0x%08X (bound <bound>)`. Phase 12's measure job copies these lines to its job summary.
  - It asserts the maximum ≤ the bound, and that every NaN or out-of-domain input gives NaN.
  - Run `MMO_EXHAUSTIVE_MATH=1 dotnet test tests/MmoEngine.SimCore.Tests -c Release --filter "FullyQualifiedName~ExhaustiveSweepTests"` and confirm it fails to compile, because `SimMathBounds` does not exist. Without the variable, every row reports skipped.

### Implementation

- [ ] T154 [US3] Implement the nine descriptors in `src/MmoEngine.SimCore.Analyzers/Descriptors.cs` (`internal static class Descriptors`, `public static readonly DiagnosticDescriptor Mmo1001` through `Mmo1009`, plus an `All` array in ID order).
  - Each descriptor: `DiagnosticSeverity.Error`, `isEnabledByDefault: true`, category `"Determinism"`, `customTags: [WellKnownDiagnosticTags.NotConfigurable]`, and the Title and message format verbatim from contracts/analyzer-rules.md#rules and #messages.
  - Return `Descriptors.All` from `SupportedDiagnostics` in `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs`.
  - Replace the header-only `src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Unshipped.md` with the contract's `### New Rules` table for MMO1001-MMO1009 (#release-tracking), so RS2008 and the release-tracking checks pass under `TreatWarningsAsErrors`.
  - The analyzer stays free of RS1035 banned APIs (no `File`, `Environment` or `Console`).
  - Makes T147 pass.
- [ ] T155 [US3] Implement the analysis framework and MMO1001 in `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs` and the new `src/MmoEngine.SimCore.Analyzers/Tables.cs`, per contracts/analyzer-rules.md#wiring, #precedence-and-deduplication and #vector128-allowlist-mmo1001.
  - Full `Initialize`:
    - `EnableConcurrentExecution()` and `ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics)`;
    - one `RegisterOperationAction` over Invocation, PropertyReference, FieldReference, MethodReference, EventReference, ObjectCreation, Lock, Loop, Conversion, Spread, VariableDeclarator, Binary, CompoundAssignment (for float `%=`, which the MMO1002 row requires), Unary, TypeOf, DefaultValue, DynamicInvocation, DynamicMemberReference, DynamicIndexerAccess and DynamicObjectCreation;
    - `RegisterSymbolAction` for Field, Property, Method and Event, skipping `IsImplicitlyDeclared` symbols (backing fields, record members);
    - `RegisterSyntaxTreeAction` (wired here; its rule lands with MMO1008).
  - The operation action returns at once when any ancestor is an `IAttributeOperation`.
  - Classification:
    - A pure `static string? Classify(IOperation op)` returns the first matching rule in the order `MMO1003 > MMO1001 > MMO1002 > MMO1008 > MMO1005 > MMO1006 > MMO1004 > MMO1009 > MMO1007`.
    - An operation is skipped when an ancestor inside the same statement (or the same field or property initializer) classifies to the same rule. The check walks `IOperation.Parent` with no shared mutable state.
    - Matching compares `OriginalDefinition` by metadata name (``Namespace.Type`1+Nested`` plus the member name, or the documentation comment ID where one overload is meant).
  - Locations: the operation's syntax; the declared identifier for declarations.
  - `Tables.cs` (`internal static class Tables`, every set an `ImmutableHashSet<string>` built once) gets the System.Numerics vector, quaternion, matrix and plane type names, the `System.Runtime.Intrinsics` namespace prefix, and the `Vector128` and `Vector128<T>` allowlists of the contract (element type `float` only).
  - MMO1001 reports a banned vector or intrinsic type wherever it appears: type argument, array element, pointer target, return type, parameter, local or `typeof` operand. `{1}` is the type.
  - Makes the MMO1001 rows of T136 (US3.1) pass.
- [ ] T156 [US3] Implement MMO1003 and MMO1002 in `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs` and `src/MmoEngine.SimCore.Analyzers/Tables.cs`, per contracts/analyzer-rules.md#math-allowlist-mmo1002 and #replacement-map-mmo1002-messages.
  - MMO1003: any member named `FusedMultiply*` or `MultiplyAddEstimate` on any type, and every member of `System.Runtime.Intrinsics.X86.Fma` and `Fma+X64`.
  - MMO1002 covers static members of `System.Math`, `System.MathF`, `System.Single`, `System.Double`, `System.Half` and `System.Runtime.InteropServices.NFloat`, and static members of the `System.Numerics` generic-math interfaces (so `T.Cos` counts), when they are not on the allowlist. It catches invocations, method groups and `&` function pointers, plus float `%` (Binary) and `%=` (CompoundAssignment).
  - Allowlist:
    - with float operands: `Sqrt`, `Abs`, `CopySign`, `Floor`, `Ceiling` and `Truncate`;
    - `Round` matched by the documentation IDs `M:System.MathF.Round(System.Single)` and `M:System.MathF.Round(System.Single,System.MidpointRounding)`, plus the `Math` and `float` equivalents;
    - `BitIncrement`, `BitDecrement`, `ScaleB` and `ILogB`;
    - every `float.Is*` predicate, and the constants;
    - with integer operands: `Min`, `Max`, `Abs`, `Clamp`, `BigMul` and `DivRem`;
    - float `Clamp` only when the containing type is `MmoEngine.SimCore.Math.SimMath`;
    - `nameof` operands are never operations.
  - `{1}` comes from the replacement map table; members not on the map get the fallback text.
  - Makes T137 (US3.2), T138 (US3.3), T146, `RuleTests.GeneratedCodeIsAnalyzed` and `BackstopTests.BackstopCountsAnAssemblySuppressedDiagnostic` pass.
- [ ] T157 [US3] Implement MMO1004 in `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs` and `src/MmoEngine.SimCore.Analyzers/Tables.cs`, per contracts/analyzer-rules.md#hash-ordered-collections-mmo1004.
  - The hash-ordered set: ``Dictionary`2`` with its `KeyCollection` and `ValueCollection`, ``HashSet`1``, `Hashtable`, ``ConcurrentDictionary`2``, ``ConcurrentBag`1``, ``ImmutableDictionary`2``, ``ImmutableHashSet`1``, ``FrozenDictionary`2``, ``FrozenSet`1``, ``ReadOnlyDictionary`2`` with its collections, `HybridDictionary`, ``ConditionalWeakTable`2``, and the interfaces ``IDictionary`2``, ``IReadOnlyDictionary`2``, ``ISet`1``, ``IReadOnlySet`1`` and `IDictionary`.
  - It reports:
    - `foreach` (Loop), at the collection expression;
    - `GetEnumerator`, `CopyTo` and `ToArray`;
    - `Keys` and `Values` only when their result type is not itself hash-ordered;
    - `..` spreads;
    - every conversion of a hash-ordered value to another type: LINQ receivers, query syntax, `string.Join`, `new List<T>(set)`, `AddRange`, and returning it as `IEnumerable<T>`. Order-insensitive sinks on hash types (`UnionWith`, a hash type's copy constructor) are excluded.
  - `{1}` is the collection type.
  - Makes T139 (US3.4), T150 and `BackstopTests.BackstopReportsAnInjectedViolation` pass.
- [ ] T158 [US3] Implement MMO1005 and MMO1006 in `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs` and `src/MmoEngine.SimCore.Analyzers/Tables.cs`, per contracts/analyzer-rules.md#ambient-time-randomness-and-hash-seeds-mmo1005 and #locks-and-blocking-mmo1006.
  - MMO1005, whole types: `Stopwatch`, `TimeProvider`, `Random`, `RandomNumberGenerator`, `HashCode`, `System.Threading.Timer`, `PeriodicTimer` and `System.Timers.Timer`.
  - MMO1005, members: `DateTime.Now`, `UtcNow` and `Today`; `DateTimeOffset.Now` and `UtcNow`; `Environment.TickCount` and `TickCount64`; `Guid.NewGuid` and `CreateVersion7`; `string.GetHashCode`; `RuntimeHelpers.GetHashCode`; `Task.Delay`; and `object.GetHashCode` or `ValueType.GetHashCode` when the receiver's type does not override them.
  - MMO1006, whole types: `Monitor`, `Lock`, `Mutex`, `Semaphore`, `SemaphoreSlim`, `ReaderWriterLock`, `ReaderWriterLockSlim`, `SpinLock`, `SpinWait`, `ManualResetEvent`, `ManualResetEventSlim`, `AutoResetEvent`, `EventWaitHandle`, `WaitHandle`, `CountdownEvent`, `Barrier`, ``BlockingCollection`1`` and `Parallel`, including declarations of those types.
  - MMO1006, members: `Thread.Sleep`, `Join`, `SpinWait` and `Yield`; `Task.Wait`, `WaitAll`, `WaitAny` and `RunSynchronously`; `Task<T>.Result` and `ValueTask<T>.Result`; and `GetResult` on the four awaiters and the four configured awaiters.
  - MMO1006 also reports every `lock` statement (location: the `lock` keyword) and, through the Method symbol action, `MethodImplementationFlags` containing `Synchronized`.
  - Makes T140 (US3.5) and T141 (US3.6) pass.
- [ ] T159 [US3] Implement MMO1007, MMO1008 and MMO1009 in `src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs` and `src/MmoEngine.SimCore.Analyzers/Tables.cs`, per contracts/analyzer-rules.md#numeric-types-mmo1007, #rules and #messages.
  - MMO1007 reports declarations of `double`, `Half`, `NFloat`, `nint` and `nuint` (fields, properties, events, parameters, return types, locals, type arguments, array elements), and every non-constant expression of those types, including the implicit conversion an untyped literal creates. `const` fields and constant-folded expressions are excluded.
  - MMO1008 reports:
    - `#if` and `#elif` from the syntax-tree action, at the directive line;
    - `[Conditional]` declarations, and calls to `System.Diagnostics.Debug` and `Trace`;
    - `OperatingSystem`, `RuntimeInformation` and `RuntimeFeature`;
    - every `System.Environment` member except the two clocks;
    - `IntPtr.Size`, `UIntPtr.Size` and `BitConverter.IsLittleEndian`;
    - members of `System.Type`, every type in `System.Reflection`, and `Activator`;
    - all four Dynamic kinds;
    - `[UnsafeAccessor]` declarations.

    `typeof(T)` without member access is allowed.
  - MMO1009 reports:
    - non-constant conversions, explicit or implicit, checked or not, from `float` or `double` to `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `char`, `Int128` or `UInt128`, or to an enum (matched by its underlying type), outside the bodies of `MmoEngine.SimCore.Math.SimMath.FloorToInt`, `RoundToInt` and `TruncateToInt`;
    - every `System.Convert` member;
    - `CreateTruncating`, `CreateSaturating` and `CreateChecked` declared on those integer types with a `TOther` of `float`, `double`, `Half` or `NFloat`;
    - every non-constant `ulong` to `float` conversion, `SimMath` included.

    `{1}` is the reason text for the conversion kind.
  - Makes T142, T143 and T144 (US3.7), and T145's `RuleTests.AttributeApplicationsAreNotReported`, pass.
- [ ] T160 [US3] Run `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~BackstopTests.CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes"` and `dotnet build MmoEngine.sln -warnaserror` (US3-IT, SC-003).
  - Fix each reported diagnostic in the `src/MmoEngine.SimCore/**` file that holds it, by rewriting to the allowed form: `SimMath.*` for platform math, `SimMath.FloorToInt`/`RoundToInt`/`TruncateToInt` for casts, `(float)(uint)` for ulong values, sorted arrays for hash-ordered iteration, and float literals for doubles.
  - Never suppress: no `[SuppressMessage]`, `#pragma`, `NoWarn` or editorconfig change.
  - The fixes must be bit-identical in behaviour. `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` and `dotnet test tests/MmoEngine.SimCore.Tests` stay green with every committed stream's expected hashes unchanged. A changed hash means the rewrite is wrong; it is not a reason to re-record.
  - Makes T149's `CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes` meaningful and green with every rule active, and the solution build exit 0.
- [ ] T161 [US3] Implement `src/MmoEngine.SimCore/Math/SimMathBounds.cs` (namespace `MmoEngine.SimCore.Math`, `public static class SimMathBounds`, `const float` fields only: `ReductionLimit = 12867f`, `SinCosMaxUlp = 1f`, `SinCosLargeMaxUlp = 2f`, `TanMaxUlp = 1.5f`, `TanLargeMaxUlp = 2.5f`, `AsinAcosMaxUlp = 1f`, `AtanMaxUlp = 1f`, `Atan2MaxUlp = 2f`, `SinCosBinaryAngleMaxUlp = 1f`, `SqrtMaxUlp = 0.5f`), after T160.
  - Each field's XML doc states the function, the domain and the measured maximum from research.md R3: Sin/Cos 0.793/0.800, Tan 1.102, large-argument 1.550/2.072, Asin/Acos 0.713/0.781, Atan 0.849, Atan2 1.488, SinCosBinaryAngle 0.737.
  - Add `<remarks>Maximum error: <see cref="SimMathBounds..."/></remarks>` to each trigonometric method and to `Sqrt` in `src/MmoEngine.SimCore/Math/SimMath.cs`. These are documentation only; no code changes.
  - Should a row exceed its bound, fix the algorithm or the coefficients in `SimMath.cs` or `SimMathCoefficients.g.cs`, regenerating with `tools/simcore/gen_coeffs.py` and `emit_cs.py` per R3. Never widen a bound past the R3 table.
  - Makes T152 (US3.8a) pass, and makes T153 compile and pass under `MMO_EXHAUSTIVE_MATH=1 dotnet test tests/MmoEngine.SimCore.Tests -c Release --filter "FullyQualifiedName~ExhaustiveSweepTests"`.
- [ ] T162 [US3] Run the coverage check on this phase's changed sources:
  - Run `dotnet test MmoEngine.sln --collect:"XPlat Code Coverage" --results-directory coverage` and `python3 tools/coverage_gate.py --base origin/main --coverage-dir coverage`.
  - Every file under `src/MmoEngine.SimCore.Analyzers/` must be ≥ 95% line coverage, reported by `tests/MmoEngine.SimCore.Analyzers.Tests`. `SimMathBounds.cs` has no executable lines.
  - For each uncovered analyzer branch, add an `EachSampleProducesExactlyItsRule` row in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` that states the exact rule ID and count: a remaining whole-type entry, an awaiter `GetResult`, a `ReadOnlyDictionary` collection, `DateTimeOffset.UtcNow`, `RuntimeInformation`, `Activator`, `Trace`, `[Conditional]`, `checked` casts, `CreateChecked`, and so on. Never add a test that only proves the analyzer does not throw.
  - Re-run until the gate exits 0.

**Checkpoint**: These all pass or exit 0:
- `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests` (every RuleTests row, SuppressionTests, DescriptorTests, WiringTests, BackstopTests and BuildTests);
- `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Math"` (FixedTableBoundsTests and MathReferenceTests pass; ExhaustiveSweepTests skipped);
- `dotnet build MmoEngine.sln -warnaserror` with the analyzer active on the core;
- `dotnet format MmoEngine.sln --verify-no-changes`;
- `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess`, with expected hashes unchanged;
- `python3 tools/coverage_gate.py --base origin/main --coverage-dir coverage`.

---

## Phase 6: User Story 4 - Weapons keep the same time and spread everywhere (Priority: P2)

**Goal**: Add the weapon step (the integer fire accumulator, the five phases, reload, slot switching, heat, recoil and fire blocks) and the stateless seeded spread. Spawn each shot's projectile at the shooter's eye, wire stage 3 of the world step, and define the `rifle-burst`, `reload-and-empty` and `heat-overheat` scenarios. Their `.mmrs` files are recorded by Phase 7 because they press Fire.

**Independent Test**: `Scenarios/WeaponScenarioTests.EveryWeaponFiresOnTheStepsItsConfigurationImplies` holds the trigger for slot 0 (rifle) and slot 1 (launcher) at 20, 30 and 60 Hz. Every shot's step, direction bits, recoil and counters must equal the values the compiled configuration implies. The weapon streams are compared across builds once Phase 7 records them (`streams verify`) and Phase 11 runs them in WebAssembly.

### Tests (write first, confirm red)

- [ ] T163 [P] [US4] Add the test support `tests/MmoEngine.SimCore.Tests/Support/WeaponRuns.cs` (namespace `MmoEngine.SimCore.Tests.Support`, no assertions of its own):
  - `WeaponRuns.Create(SimConfig config, ...)` builds a `SimWorld` over `TestWorlds.Flat`, encoded and decoded through `CollisionFile.Encode`/`CollisionFile.Decode`, with `CompiledConfig.Create(config)`. It then calls `Reset` with a one-character canonical state from `TestStates`: character ID 1, standing and grounded at the origin of the flat world. The caller may override that character's `ActiveSlot`, any `WeaponState` field (Phase, Flags, PhaseSteps, FireWait, ShotCounter, Magazine, Reserve, Heat, StepsSinceShot, RecoilPitch, RecoilYaw), the projectile list and `NextProjectileId` before the state is serialised with `StateSerializer`. The match salt is the constant `WeaponRuns.Salt = 0x0015_0000_5EED_0004UL`.
  - `Step(Buttons buttons, byte slot, ushort yaw = 0, short pitch = 0)` sends one `CharacterCommand(0, command)` with the next Sequence (starting at 1).
  - `Hold(Buttons buttons, int steps, byte slot, ushort yaw = 0, short pitch = 0)` calls `Step` `steps` times.
  - `Run(RecordedStream stream, CollisionWorld world)` builds the world from the stream's configuration, initial state and salt, then steps each `RecordedStep`'s commands as `CharacterCommand`s.
  - Every step returns a `WeaponSnapshot` copied out of the `StepReport` before the next step. It holds: `Step`; `Shots` (CharacterId, Slot, ShotCounter, ProjectileId, and the direction as three `BitConverter.SingleToUInt32Bits` values) from `report.Events.Shots`; `FireBlocks` (CharacterId, Reason) from `report.Events.FireBlocks`; every `WeaponState`, `ActiveSlot`, `NextProjectileId` and the projectile count, read from `report.State` with the Phase 2 `StateSerializer` reader.
  - A step whose `report.Fault.Code != StepFaultCode.None` throws `InvalidOperationException` with the fault's code and `Fault.Path`, so a fault fails the calling test loudly.
  - No `System.Random`. Nothing is written under `simcore/`.
- [ ] T164 [P] [US4] Write failing tests in `tests/MmoEngine.SimCore.Tests/Weapons/SpreadTests.cs` (US4.2, FR-008). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Weapons.SpreadTests"` and confirm they fail (a compile failure because `Spread` does not exist yet counts as red). Expected values are computed independently in the test with `System.Math` in double, never by calling a second core method. The SplitMix64 finaliser itself is pinned by Phase 4's `SplitMix64Tests`.
  - `SpreadTests.SameSaltPlayerAndCounterGiveTheSameDirectionBits(ulong salt, uint characterId, int slot, uint shotCounter, ushort yaw, short pitch, float spreadHalfAngleDeg)` (US4.2), InlineData rows:
    - (0, 1, 0, 0, 0, 0, 1.5)
    - (0x0123456789ABCDEF, 7, 1, 12345, 16384, 2048, 0.5)
    - (0xFFFFFFFFFFFFFFFF, 0xFFFFFFFF, 7, 0xFFFFFFFF, 65535, -16384, 45)
    - (42, 3, 0, 1, 32768, 16384, 1.5)
    - (0x5EED, 2, 1, 99, 12345, -100, 0)

    VersMax is read from `CompiledConfig.Create` of `TestConfigs.At(30)` with `weapons[0].spreadHalfAngleDeg` set to the row's value. Assert:
    - `Spread.Hash(salt, characterId, slot, shotCounter)` called twice gives the same value, and that value equals `Mix(Mix(salt ^ characterId) ^ (((ulong)slot << 32) | shotCounter))` evaluated with a SplitMix64 finaliser written in the test (data-model.md#weapon-step, One shot step 2).
    - `Spread.Direction(hash, yaw, pitch, versMax)` called twice gives identical bits in all three components.
    - Each component is within 1e-6 of the double-precision evaluation of data-model.md's formula from the same inputs: u1 = (h >> 40) / 2^24, s = u1 · VersMax, cosθ = 1 − s, sinθ = sqrt(s(2 − s)), φ = (ushort)h · π/32768, and the aim basis from `double.SinPi`/`double.CosPi` of yaw/32768 and pitch/32768.
    - For the spread-0 row the direction's bits equal those of f = (cp·sy, sp, cp·cy) computed from `SimMath.SinCosBinaryAngle` of the aim.
  - `SpreadTests.ADifferentShotCounterChangesTheDirection(string changed)` (US4.2), rows `shotCounter`, `salt`, `characterId`, `slot`. For base inputs (salt 0x0123456789ABCDEF, character 7, slot 0, yaw 12345, pitch 2048, rifle VersMax at 1.5°) and every shot counter 0..999, changing that one input by +1 gives a different hash, and a direction that differs from the base in at least one component's bits.
  - `SpreadTests.DirectionsFallInsideTheConeWithAUniformDistribution` (US4.2, FR-008). Inputs: a constant `const ulong SpreadSalt = 0x0015_5EED_0000_0004UL` (named in every assertion message), character 1, slot 0, shot counters 0..19,999, yaw 12345, pitch 2048, spread 1.5°. For all 20,000 directions, assert:
    - |dir| is within 1e-6 of 1;
    - the angle to the aim axis f (acos of the double dot product) is at most 1.5° · π/180 · (1 + 1e-4);
    - the versine (1 − dir·f)/VersMax, binned into 20 equal bins over [0, 1), gives χ² < 30.1;
    - φ = atan2(dir·u, dir·r), binned into 16 equal bins over [0, 2π), gives χ² < 25.0, with r = (−cy, 0, sy) and u = (−sp·sy, cp, −sp·cy) in double.
- [ ] T165 [P] [US4] Write failing test `WeaponPhaseTests.TransitionsFollowTheContractTable(string row)` in `tests/MmoEngine.SimCore.Tests/Weapons/WeaponPhaseTests.cs` (FR-006). Theory with MemberData over the row names of data-model.md#weapon-phases plus the reload-ignored rows.
  - Each row calls `WeaponStep.Step` directly (signature in the WeaponStep implementation task below) once, at 30 Hz from `TestConfigs.At(30)`, through a private helper in the test file. The helper passes an arranged post-movement `CharacterState` (ID 1, ActiveSlot 0 unless stated), a two-element `WeaponState` span, an `InputCommand`, projectile count 0, `nextProjectileId` 1 and spans sized for `MaxShotsPerStep`.
  - Every row asserts the exact `Phase`, `PhaseSteps`, `Magazine` and `Reserve` after the call, and the fire-block reason. Rows, active weapon slot 0 unless stated (placeholders: rifle `ReloadSteps` 60, launcher `EquipSteps` 23):
    - `ReadyFireHeldToFiring`: Ready, Fire held → Firing, one shot, Magazine 29.
    - `FiringReleasedToReady`: Firing, no button held → Ready.
    - `FiringBlockedToReady`: Firing, Magazine 0, Fire held → Ready, block Empty.
    - `ReadyReloadToReloading`: Ready, Magazine 29, Reserve 210, Reload held → Reloading, PhaseSteps 60, Magazine 29.
    - `FiringReloadToReloading`: Firing, Magazine 10, Reload held → Reloading, PhaseSteps 60.
    - `ReloadFullMagazineIgnored`: Magazine 30, Reload held → Ready, PhaseSteps 0.
    - `ReloadEmptyReserveIgnored`: Magazine 0, Reserve 0, Reload held → Ready, PhaseSteps 0.
    - `ReloadingCompletesToReady`: Reloading, PhaseSteps 1, Magazine 3, Reserve 20 → Ready, Magazine 23, Reserve 0.
    - `ReloadZeroStepsCompletesAtEntry`: `weapons[0].reloadMs` 0, Magazine 0, Reserve 210, Reload held → Ready, Magazine 30, Reserve 180.
    - `HolsteredReadySwitchesIn`: input WeaponSlot 1 → weapons[1] Switching, PhaseSteps 23, ActiveSlot 1.
    - `ZeroEquipStepsIsReadyAtOnce`: `weapons[1].equipMs` 0, input WeaponSlot 1 → weapons[1] Ready.
    - `HolsteredOverheatedSwitchesInOverheated`: both slots heatPerShot 10000; weapons[1] Overheated, Heat 3,000,000, StepsSinceShot 0; input WeaponSlot 1 → weapons[1] Overheated, PhaseSteps 23.
    - `ActiveFiringSwitchedAwayToReady`: Firing → weapons[0] Ready.
    - `ActiveReloadingSwitchedAwayCancels`: Reloading, PhaseSteps 30, Magazine 5, Reserve 100, input WeaponSlot 1 → weapons[0] Ready, PhaseSteps 0, Magazine 5, Reserve 100.
    - `ActiveSwitchingSwitchedAwayToReady`: Switching, PhaseSteps 10 → weapons[0] Ready, PhaseSteps 0.
    - `ActiveOverheatedSwitchedAwayStaysOverheated`: heat weapon, Overheated, PhaseSteps 4, Heat 3,000,000, StepsSinceShot 0 → weapons[0] Overheated, PhaseSteps 0.
    - `SwitchingCompletesToReady`: Switching, PhaseSteps 1 → Ready.
    - `ShotReachingThresholdOverheats`: heat weapon, Ready, Heat 2,700,000, Fire held → one shot, Heat 3,000,000, Overheated.
    - `OverheatedCoolsToReady`: heat weapon, Overheated, PhaseSteps 0, Heat 1,250,000, StepsSinceShot 7 → Heat 1,200,000, Ready.
    - `OverheatedCoolsToSwitching`: as the previous row with PhaseSteps 5 → Switching, PhaseSteps 4.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Weapons.WeaponPhaseTests"` and confirm it fails.
- [ ] T166 [P] [US4] Write failing tests in `tests/MmoEngine.SimCore.Tests/Weapons/WeaponStepTests.cs` (FR-006). They call `WeaponStep.Step` directly through a private helper in this file. Where the step length matters they are Theories `[MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]` with parameter `hz`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Weapons.WeaponStepTests"` and confirm they fail.
  - `SwitchAndReloadCountFromTheNextStep(int hz)`:
    - Switch: at step n, input WeaponSlot 1 with Fire held. Weapons[1] is Switching with PhaseSteps == EquipSteps (15/23/45 at 20/30/60 Hz), and Switching is reported at steps n..n + EquipSteps − 1. At step n + EquipSteps it is Ready and fires one shot in that step.
    - Reload: Reload and Fire held at step n with Magazine 0. Reloading with PhaseSteps == ReloadSteps (40/60/120), and Ready at step n + ReloadSteps with Magazine 30, Reserve 180. It fires in that step, leaving Magazine 29.
  - `ZeroDurationsCompleteInTheEntryStep(int hz)`: with `equipMs` 0 a slot change with Fire held fires in the entry step. With `reloadMs` 0, Reload and Fire held with Magazine 0 moves 30 rounds and fires in the entry step (Magazine 29, Reserve 180).
  - `ReleasedTriggerNeverBanksShots(int hz)`:
    - Rifle (600 rpm): fire one shot, then release for 10 · hz/10 steps. After every step 0 ≤ FireWait < ShotCost, and FireWait reaches exactly 0.
    - Re-press at step p: a shot at p, the next at exactly p + hz/10, none between, and no step with two shots.
  - `SemiAutomaticWaitsForRelease(int hz)`: slot 0 with `fireMode` Semi.
    - Fire held 10 steps: exactly one shot (held step 0), Flags TriggerLatched set, and the block AwaitingRelease at held steps 1..9.
    - One step released: no block, TriggerLatched cleared.
    - Press again: one shot in that step.
  - `ShotSpawnsAProjectileAtTheEyeWithTheMuzzleVelocity(int slot, Stance stance)`: InlineData (0, Standing), (0, Crouched), (1, Standing), (1, Crouched), at 30 Hz. The shooter is at Position (10.25, 3.5, −7.75) with Velocity (3, −2, 1), ActiveSlot = slot, `nextProjectileId` 41, step 9. The spawned projectile has:
    - Id 41, with `nextProjectileId` now 42;
    - OwnerId 1, Class == `weapons[slot].projectileClass` (0 / 1), LaunchStep 9;
    - Origin bits == (10.25f, 3.5f + EyeHeight[stance], −7.75f), with EyeHeight 1.62 / 1.05;
    - LaunchVelocity component bits == the shot event's direction component × MuzzleSpeed (600 / 120): the shooter's velocity is not inherited;
    - Position == Origin.

    The shot event also has CharacterId 1, Slot == slot, ShotCounter 0 and ProjectileId 41.
  - `ProjectileLimitStopsTheFireLoop(string caseName)`, rows:
    - `world-full`: projectile count == `world.maxProjectiles`, Firing, Fire held → no shot, block ProjectileLimit, Phase Ready.
    - `ids-used-up`: `nextProjectileId` 0xFFFFFFFF → no shot, block ProjectileLimit, `nextProjectileId` unchanged.
    - `limit-after-one-shot`: 2400 rpm, `maxShotsPerStep` 2, count == maxProjectiles − 1 → exactly one shot, block None, Phase Firing.
  - `RecoilAccumulatesToItsMaximumWithoutRecovery(int hz)`: `recoilRecoveryDegPerSecond` 0, `recoilKickPitchDeg` 1 and `recoilKickYawDeg` 1 (182 units), `magazineSize` 100, Fire held for 12 shots.
    - After the n-th shot RecoilPitch == min(182·n, 1092).
    - RecoilYaw == clamp(previous + (bit 16 of that shot's `Spread.Hash` == 0 ? 182 : −182), −546, 546).
  - `AimWithRecoilWrapsYawAndClampsPitch`, two arrangements as InlineData rows:
    - (AimYaw 65530, RecoilYaw 18, AimPitch 16384, RecoilPitch 100) → the shot's direction bits == `Spread.Direction(hash, 12, 16384, VersMax)`.
    - (AimYaw 10, RecoilYaw −30, AimPitch 0, RecoilPitch 0) → `Spread.Direction(hash, 65516, 0, VersMax)`.
  - `HolsteredWeaponsCoolRecoverAndCountDownTheirWait`: at 30 Hz, heat weapon in both slots, ActiveSlot 0. Holstered weapons[1] starts with Heat 500,000, StepsSinceShot 100, RecoilPitch 100, RecoilYaw −100, FireWait 1000. After one neutral step: Heat 450,000, RecoilPitch 39, RecoilYaw −39, FireWait 970, StepsSinceShot 101. A weapon at StepsSinceShot 65535 stays at 65535.
  - `RecoilMovesTowardZeroWithoutCrossing`: at 30 Hz, RecoilYaw −20 and RecoilPitch 20 with StepsSinceShot ≥ the delay → both exactly 0 after one step (never +41 or −41).
- [ ] T167 [US4] Write failing tests in `tests/MmoEngine.SimCore.Tests/Weapons/FireTimingTests.cs` (US4.1). Both drive the world through `WeaponRuns`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Weapons.FireTimingTests"` and confirm they fail.
  - `FireTimingTests.SevenHundredRpmAtThirtyHzFiresExactly35ShotsInThreeSeconds` (US4.1): `TestConfigs.At(30)` with `weapons[0].roundsPerMinute` 700 and `magazineSize` 100 (heat 0, `maxShotsPerStep` 1, R11 holds since 700 ≤ 1800), slot 0 Ready, Fire held for 90 steps.
    - Exactly 35 shot events in total.
    - After held step k (k = 0 for the first held step), the cumulative shot count == ceil((k + 1) · 700 / 1800) for every k in 0..89.
    - The j-th shot fires on held step floor((j − 1) · 1800 / 700) (0, 2, 5, 7, 10, … for j = 1..5), which is within one step of (j − 1) · 1800/700.
    - No step fires more than one shot.
  - Supporting `FireTimingTests.SixHundredRpmFiresThirtyShotsInThreeSecondsAtEveryRate(int hz)`: the placeholder rifle (magazine 30) held for 3 · hz steps fires exactly 30 shots, on held steps 0, hz/10, 2·hz/10, …, 29·hz/10.
- [ ] T168 [US4] Write failing test `FireTimingTests.ShotCountNeverDriftsFromTheRateByMoreThanOne(int hz, int rpm)` in `tests/MmoEngine.SimCore.Tests/Weapons/FireTimingTests.cs` (FR-007). Theory over `TestRates.With(...)` crossing hz 20, 30, 60 with every rpm from 30 to 3600 in steps of 7 (511 values).
  - Configuration: `TestConfigs.At(hz)` with `weapons[0].roundsPerMinute` = rpm and `maxShotsPerStep` = ceil(rpm / (60 · hz)), the smallest R11 allows.
  - `WeaponStep.Step` is called directly for 60 · hz steps with Fire held. Before each call Magazine is set to `magazineSize`, projectile count to 0 and `nextProjectileId` to 1, so only the accumulator limits shots.
  - Assert at every step k: the cumulative count == ceil((k + 1) · rpm / (60 · hz)), and 0 ≤ FireWait < ShotCost.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~FireTimingTests.ShotCountNeverDriftsFromTheRateByMoreThanOne"` and confirm it fails.
- [ ] T169 [US4] Write failing test `FireTimingTests.WeaponFasterThanOneStepFiresAtMostMaxShotsPerStepWithoutDrift` in `tests/MmoEngine.SimCore.Tests/Weapons/FireTimingTests.cs` (edge case: fire interval shorter than one step).
  - Arrange through `WeaponRuns` at 30 Hz with `weapons[0].roundsPerMinute` 2400, `maxShotsPerStep` 2 and `magazineSize` 1000, Fire held for 90 steps.
  - Assert:
    - no step has more than 2 shot events;
    - held step 0 has exactly 2;
    - the cumulative count after held step k == ceil((k + 1) · 2400 / 1800) for every k;
    - the total is 120;
    - the two shots of one step carry consecutive ShotCounter and ProjectileId values and different direction bits.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~FireTimingTests.WeaponFasterThanOneStepFiresAtMostMaxShotsPerStepWithoutDrift"` and confirm it fails.
- [ ] T170 [US4] Write failing test `WeaponScenarioTests.TwoRoundMagazineFiresTwiceReportsEmptyThenReloadsFromReserve(int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/WeaponScenarioTests.cs` (US4.3).
  - Given `ScenarioCatalog.Get("reload-and-empty").Build(TestConfigs.Default, testWorld, hz)`, where `testWorld` is `simcore/content/test-world.mmcd` opened read-only through `RepoPaths` and decoded with `CollisionFile.Decode`.
  - When it is stepped with `WeaponRuns.Run`, assert:
    - exactly 2 shot events in the whole run, at steps 1 and 1 + hz/10;
    - every step from 2 + hz/10 to hz (Fire held, Reload not yet pressed) has no shot and `FireBlocks == [{1, FireBlock.Empty}]`;
    - at step hz + 1 (Reload pressed) weapons[0] is Reloading with PhaseSteps == ReloadSteps (40/60/120), with block Reloading;
    - at step hz + 1 + ReloadSteps weapons[0] is Ready with Magazine 30 (min(30 − 0, 210)) and Reserve 180.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~WeaponScenarioTests.TwoRoundMagazineFiresTwiceReportsEmptyThenReloadsFromReserve"` and confirm it fails.
- [ ] T171 [US4] Write failing test `WeaponScenarioTests.HeatWeaponOverheatsAndResumesAtTheRecoveryLevel` in `tests/MmoEngine.SimCore.Tests/Scenarios/WeaponScenarioTests.cs` (US4.4, a Fact at 30 Hz).
  - Given the `heat-overheat` definition built at its own 30 Hz rate and stepped with `WeaponRuns.Run`, assert:
    - shots at exactly steps 1, 4, …, 28 (10 shots), with Heat after the k-th shot == 300,000 · k;
    - after step 28, Heat == 3,000,000 and Phase Overheated;
    - every step 29–70 has no shot and `FireBlocks == [{1, FireBlock.Overheated}]`;
    - Heat == 3,000,000 after steps 29–35, and == 3,000,000 − 50,000 · (s − 35) after each step s in 36–70;
    - step 71 fires exactly one shot (the 11th), cooling having brought Heat to 1,200,000 first, so the committed Heat is 1,500,000 and Phase is Firing;
    - step 72 has no shot and no fire block.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~WeaponScenarioTests.HeatWeaponOverheatsAndResumesAtTheRecoveryLevel"` and confirm it fails.
- [ ] T172 [US4] Write failing test `WeaponScenarioTests.HeatWeaponStopsAtTheThresholdAndResumesAtTheRecoveryLevel(int hz, int tenthShotStep, int resumeStep)` in `tests/MmoEngine.SimCore.Tests/Scenarios/WeaponScenarioTests.cs` (US4.4 at the other rates). InlineData rows (20, 19, 47) and (60, 55, 141).
  - Given `heat-overheat` built at hz, assert:
    - the first 10 shots fall on steps 1 + j · hz/10 (j = 0..9), the last at tenthShotStep, with Heat 100,000 · hz and Phase Overheated after it;
    - Overheated is reported at every step from tenthShotStep + 1 to resumeStep − 1;
    - the 11th shot fires at resumeStep with a committed Heat of 50,000 · hz;
    - the step after it has no shot and no block;
    - resumeStep == tenthShotStep + HeatCooldownDelaySteps + ceil(60,000 · hz / 50,000) − 1, computed from the compiled configuration.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~WeaponScenarioTests.HeatWeaponStopsAtTheThresholdAndResumesAtTheRecoveryLevel"` and confirm it fails.
- [ ] T173 [US4] Write failing test `WeaponScenarioTests.EachShotKicksTheAimAndTheOffsetRecoversAfterFiring(int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/WeaponScenarioTests.cs` (US4.5).
  - Given `rifle-burst` built at hz (Fire held 3 s at yaw 0 and pitch 2048, then released for 1 s), with the placeholders compiled to kick 46 pitch and 18 yaw units, maximums 1092 and 546, recovery 91/61/30 units per step and a recovery delay of 2/3/6 steps (equal to the shot interval hz/10, so W4 runs in each shot's step before its kick). Assert:
    - exactly 30 shots;
    - after the n-th shot, RecoilPitch == min(max(P(n−1) − (StepsSinceShot reached the delay ? RecoveryPerStep : 0), 0) + 46, 1092), where P(n−1) is the committed RecoilPitch before that step. Concretely, 46 after every shot at 20 and 30 Hz, and 46 + 16 · (n − 1) at 60 Hz (510 after the 30th);
    - RecoilYaw moved by +18 when bit 16 of that shot's `Spread.Hash(WeaponRuns-derived salt, 1, 0, ShotCounter)` is 0 and by −18 when it is 1, after recovery, clamped to ±546. |RecoilYaw| is 18 after every shot at every rate;
    - every shot's direction bits == `Spread.Direction(hash, (ushort)(0 + yawBeforeKick), clamp(2048 + pitchBeforeKick), VersMax)`, and the first shot uses yaw 0 and pitch 2048 unchanged. At 20 and 30 Hz every shot uses the bare aim; at 60 Hz shot n uses pitch 2048 + 16 · (n − 1);
    - after the last shot at step L = 1 + 29 · hz/10, both offsets are unchanged at steps L + 1 .. L + delay − 1. From step L + delay, each step moves both toward 0 by exactly RecoveryPerStep, never past 0, until both are 0: at step L + delay at 20 and 30 Hz, and at step L + 22 at 60 Hz.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~WeaponScenarioTests.EachShotKicksTheAimAndTheOffsetRecoversAfterFiring"` and confirm it fails.
- [ ] T174 [US4] Write failing test `WeaponScenarioTests.FireWhenTheWeaponCannotFireReportsTheReason(FireBlock reason, int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/WeaponScenarioTests.cs` (US4.6). Theory over `TestRates.WithRateLast(...)` crossing the six reasons with 20, 30, 60.
  - Each row is arranged with `WeaponRuns.Create` on `TestConfigs.At(hz)`:
    - `Switching`: input WeaponSlot 1 with Fire held.
    - `Reloading`: Magazine 29, Reload and Fire held.
    - `Empty`: Magazine 0, Reserve 210, Fire held.
    - `Overheated`: heat weapon (`heatPerShot` 10000), weapons[0] Overheated, Heat = 100,000 · hz, StepsSinceShot 0, Fire held.
    - `AwaitingRelease`: `weapons[0].fireMode` Semi, Flags TriggerLatched, Fire held.
    - `ProjectileLimit`: `world.maxProjectiles` 1, with one projectile already in the state: Id 1, Owner 1, class 0, LaunchStep 0, Origin and Position (0, 100, 0), LaunchVelocity (0, 50, 0), and NextProjectileId 2. Fire held.
  - In that step: no shot event, `FireBlocks == [{1, reason}]`, the fire-block counter of the report is 1, and Magazine and ShotCounter are unchanged.
  - The next step, with the same slot and Fire released: `FireBlocks` is empty and no shot fires.
  - Also add `WeaponScenarioTests.ReloadAndEmptyStreamReportsReloadingAndSwitchingBlocks(int hz)`. In the `reload-and-empty` definition at hz:
    - every step from hz + 2 to 1.5 · hz + 1 reports `[{1, Reloading}]`;
    - every step from 3.5 · hz + 1 to 4 · hz (slot 1 with Fire held, inside EquipSteps) reports `[{1, Switching}]`;
    - no shot fires in either range.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~WeaponScenarioTests.FireWhenTheWeaponCannotFireReportsTheReason|FullyQualifiedName~WeaponScenarioTests.ReloadAndEmptyStreamReportsReloadingAndSwitchingBlocks"` and confirm both fail.
- [ ] T175 [US4] Write failing test `WeaponScenarioTests.EveryWeaponFiresOnTheStepsItsConfigurationImplies(int slot, int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/WeaponScenarioTests.cs` (US4-IT). Theory crossing slot 0 (rifle, Auto) and slot 1 (launcher, Semi) with 20, 30, 60.
  - Arrange through `WeaponRuns.Create` on `TestConfigs.At(hz)` with ActiveSlot = slot, for 20 · hz steps. Fire is held every step for slot 0 and on even steps only for slot 1. Reload is held on every step whose previous committed Magazine is 0.
  - A private reference model in the test class, `ExpectedWeapon`, is advanced alongside the world. It is plain integer C# written from data-model.md#weapon-step: W0–W6, the fire block order and the fire loop. It calls `Spread.Hash` and `Spread.Direction` for directions and never `WeaponStep`.
  - After every step, assert that these equal the model's: the shot count; each shot's ShotCounter, ProjectileId and direction bits; the fire-block reason; and Phase, PhaseSteps, FireWait, ShotCounter, Magazine, Reserve, Heat, StepsSinceShot, RecoilPitch and RecoilYaw.
  - Independently of the model, assert:
    - slot 0's first 30 shots fall on held steps 0, hz/10, …, 29 · hz/10;
    - slot 1 fires exactly 6 shots in 20 s (magazine 1 + reserve 5): the first on step 1, each later one on the first even step at or after its reload completed, which is ReloadSteps (70/105/210) after its Reload press. After the 6th, every Fire-held step reports `[{1, Empty}]` with Reserve 0.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~WeaponScenarioTests.EveryWeaponFiresOnTheStepsItsConfigurationImplies"` and confirm it fails.

### Implementation

- [ ] T176 [P] [US4] Implement `public static class Spread` in `src/MmoEngine.SimCore/Weapons/Spread.cs` (namespace `MmoEngine.SimCore.Weapons`, explicit usings, no static mutable field), per data-model.md#weapon-step (One shot, steps 2 and 3) and research R10. It reuses Phase 4's `MmoEngine.SimCore.Hashing.SplitMix64.Mix` as the finaliser and defines no second one:
  - `public static ulong Hash(ulong matchSalt, uint characterId, int slot, uint shotCounter)` = `SplitMix64.Mix(SplitMix64.Mix(matchSalt ^ characterId) ^ (((ulong)slot << 32) | shotCounter))`.
  - `public static bool KicksYawPositive(ulong hash)` = bit 16 of `hash` is 0.
  - `public static Vec3 Direction(ulong hash, ushort yaw, short pitch, float versMax)`:
    - u1 = `(float)(uint)(hash >> 40) * 5.9604645E-08f`. That is exactly 2⁻²⁴, and the conversion goes through `uint` because `ulong → float` is banned (MMO1009).
    - φ = `(ushort)hash`.
    - `s = u1 * versMax`, `cosT = 1f - s`, `sinT = SimMath.Sqrt(s * (2f - s))`.
    - (sy, cy) from `SimMath.SinCosBinaryAngle(yaw)`, (sp, cp) from `SimMath.SinCosBinaryAngle((ushort)pitch)`, (sφ, cφ) from `SimMath.SinCosBinaryAngle(φ)`.
    - `f = (cp * sy, sp, cp * cy)`, `r = (-cy, 0f, sy)`, `u = (-sp * sy, cp, -sp * cy)`.
    - Each component is `((cosT * f) + ((sinT * cφ) * r)) + ((sinT * sφ) * u)`, with exactly this parenthesisation.
  - No MathF, no FMA, no allocation.
  - Makes T164 pass.
- [ ] T177 [US4] Implement `public static class WeaponStep` in `src/MmoEngine.SimCore/Weapons/WeaponStep.cs` with the static, allocation-free `public static int Step(CompiledConfig config, ulong matchSalt, uint step, ref CharacterState character, Span<WeaponState> weapons, in InputCommand input, int projectileCount, ref uint nextProjectileId, Span<Projectile> spawned, Span<ShotEvent> shots, out FireBlock fireBlock)`. Here `ShotEvent` is the Phase 2 shot element of `src/MmoEngine.SimCore/World/StepEvents.cs`.
  - Parameters:
    - `character` is the post-movement next state; only `ActiveSlot` is written.
    - `weapons` holds this character's `input.weaponSlots` entries, updated in place.
    - `input` is the accepted command or the neutral input.
    - `projectileCount` counts the world's projectiles, including those spawned earlier in this step.
    - The method returns the number of shots, which equals the number of projectiles written to `spawned` and of events written to `shots`.
  - Sub-steps W0–W6 in order, per data-model.md#weapon-step:
    - W0: StepsSinceShot += 1, saturating at 65535, for every weapon; clear TriggerLatched when Fire is not held.
    - W1: the slot-change rows of data-model.md#weapon-phases for the old and new weapon, then `ActiveSlot = WeaponSlot`.
    - W2: the active weapon's count-down. PhaseSteps set by W1 in this step is not counted.
    - W3: reload start when Reload is held in Ready or Firing with Magazine < magazineSize and Reserve > 0. `ReloadSteps == 0` completes at entry.
    - W4: cooling and recoil recovery for every weapon. Recovery moves toward 0 and never past it. Overheated exits to Switching when PhaseSteps > 0, else to Ready.
    - W5: the fire block in the order Switching, Reloading, Overheated, Empty, AwaitingRelease, reported only while Fire is held. Then the fire loop, bounded by `MaxShotsPerStep`. It stops on `projectileCount + shots == world.maxProjectiles` or `nextProjectileId == 0xFFFFFFFF`, reporting ProjectileLimit only when no shot fired. Afterwards Phase is Overheated, Firing, or Ready (when ProjectileLimit was reported). With Fire not held, Firing becomes Ready.
    - W6: FireWait = max(FireWait − Rpm, 0) for every weapon.
  - One shot, steps 1–8:
    1. yaw = `(ushort)(AimYaw + RecoilYaw)`, pitch = `SimMath.Clamp(AimPitch + RecoilPitch, -16384, 16384)` in int.
    2. h = `Spread.Hash(matchSalt, character.Id, slot, ShotCounter)`.
    3. dir = `Spread.Direction(h, yaw, (short)pitch, VersMax)`.
    4. Spawn into `spawned`: Id = nextProjectileId++, OwnerId = character.Id, Class = projectileClass, LaunchStep = step, Origin = `(Position.x, Position.y + EyeHeight[Stance], Position.z)`, LaunchVelocity = dir · MuzzleSpeed per component, Position = Origin.
    5. The shot event: character ID, slot, the pre-increment ShotCounter, projectile ID, dir.
    6. Magazine −= 1; ShotCounter += 1 (wrapping); Heat += HeatPerShotScaled; Overheated when Heat ≥ OverheatThresholdScaled.
    7. The recoil kick: pitch is min'd at RecoilMaxPitchUnits; yaw is ±RecoilKickYawUnits by `Spread.KicksYawPositive(h)`, clamped to ±RecoilMaxYawUnits.
    8. StepsSinceShot = 0; FireWait += ShotCost; Semi sets TriggerLatched.
  - Integers only, apart from the spread and the spawn. No LINQ, lambdas, boxing or exceptions. Every loop has a fixed bound.
  - Makes T165, T166 and T168 pass.
- [ ] T178 [US4] Replace the pass-through `StepWeapons` in `src/MmoEngine.SimCore/World/SimWorld.cs` with stage 3 of data-model.md#world-step-order, after T177.
  - Constructor: allocate a spawn scratch `Projectile[]` of `Min(world.maxProjectiles, world.maxCharacters × largest maxShotsPerStep)`, with nothing else new on the tick path.
  - For each character index in ascending ID:
    - copy its current `WeaponState`s into the next-state weapons slice;
    - call `WeaponStep.Step` with the world's `CompiledConfig`, `MatchSalt`, the step being computed, the next (post-movement) character, its weapons slice and its stage-1 input (accepted or neutral). The projectile count is the current count plus the projectiles spawned so far this step; also pass the next state's `NextProjectileId`, the spawn scratch from the next free index and the shot event buffer from the next free index;
    - advance the spawn and shot counts by the result, and append `{character ID, block}` to the fire-block events when the block is not `FireBlock.None`, counting it in the report's fire-block counter.
  - Change the Phase 2 pass-through body of `StepProjectiles` to copy the current projectiles and then append the spawn scratch in order. IDs stay ascending because every new ID exceeds every live ID, and positions stay at Origin. Phase 7 replaces this body.
  - Then run `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify --dir simcore/streams --content simcore/content`. It must print `0 diverged, 0 not comparable, 0 refused` with all 11 committed movement streams matching: their commands never press Fire, Reload or change slot, so idle weapons leave every weapon field unchanged. If a movement stream's hash nevertheless changes because this stage legitimately changes it, re-record exactly that stream in this task with `dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --scenario <name> --reason "<why>"`, committing the `.mmrs` and the `simcore/streams/REGENERATIONS.md` entry together.
  - `SimCoreVersion.RulesRevision` stays 1.
  - Makes T167 (`SevenHundredRpmAtThirtyHzFiresExactly35ShotsInThreeSeconds`, `SixHundredRpmFiresThirtyShotsInThreeSecondsAtEveryRate`), T169, T174's `FireWhenTheWeaponCannotFireReportsTheReason` and T175 pass.
- [ ] T179 [US4] Add the definitions `rifle-burst`, `reload-and-empty` and `heat-overheat` to `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs`, keeping `ScenarioCatalog.Names` in ordinal order, per contracts/simcore-cli.md#named-scenario-streams.
  - Common to all three: one character (ID 1), standing and grounded at the flat-terrain start location the flat-run definitions use (TestWorldGenerator's named feature location), with seed 0 and a fixed non-zero match-salt constant per definition. Exactly one command per step for character index 0, with Sequence = step index + 1, so `rifle-burst` is batchable. Durations are in seconds, so each builds at 20, 30 and 60 Hz.
  - `rifle-burst`: the placeholder rifle in slot 0, aim yaw 0 and pitch 2048 (11.25° above the horizon). Fire held for 3 s, then released for 1 s.
  - `reload-and-empty`: the initial state's weapons[0] has Magazine 2 and Reserve 210. Fire is held for [0, 1 s); one step holds Fire and Reload at 1 s; Fire is held for (1 s, 1.5 s]; nothing is held until 3.5 s; WeaponSlot 1 with Fire is held for [3.5 s, 4 s); nothing is held until 4.5 s.
  - `heat-overheat`: its own rate is 30 Hz. Slot 0 is the heat test weapon, `heatPerShot` 10000 with every other value the rifle's. Fire is held from step 1 for 2.4 s (72 steps at 30 Hz).
  - Each `Build(baseConfig, world, stepRateHz)` returns an unrecorded `RecordedStream` with expected hashes 0, derived from `baseConfig` (default.json).
  - Do not create any `.mmrs` file and do not run `streams record` for these names: Phase 7 records them because they press Fire. The Phase 3 test that lists known names reads `ScenarioCatalog.Names`, so it stays green.
  - Makes T170, T171, T172, T173 and T174's `ReloadAndEmptyStreamReportsReloadingAndSwitchingBlocks` pass.

**Checkpoint**: These automated checks must pass:
- `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Weapons|FullyQualifiedName~WeaponScenarioTests"` is green at 20, 30 and 60 Hz.
- The whole `dotnet test tests/MmoEngine.SimCore.Tests` stays green, including `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` over the unchanged movement streams, and `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --filter "FullyQualifiedName~BackstopTests"` is green over the new weapon code.
- `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes` exit 0.
- `dotnet test MmoEngine.sln --collect:"XPlat Code Coverage" --results-directory coverage` followed by `python3 tools/coverage_gate.py --base origin/main --coverage-dir coverage` reports `src/MmoEngine.SimCore/Weapons/WeaponStep.cs`, `src/MmoEngine.SimCore/Weapons/Spread.cs`, `src/MmoEngine.SimCore/World/SimWorld.cs` and `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs` at or above 95%.

---

## Phase 7: User Story 5 - Projectiles fly the same path everywhere (Priority: P2)

**Goal**: Fly projectiles on the closed-form ballistic path, sweep each step's segment against terrain, meshes and the hitboxes given to it, report the earliest hit or the expiry, wire stage 5 of the world step, define the projectile scenarios (`launcher-arc`, `projectile-hits-hitbox`, `mesh-occludes-shot`), and record the seven fire streams so all 18 named streams are committed.

**Independent Test**: `Scenarios/ProjectileScenarioTests.ShotsAtKnownDistancesHitWhereTheClosedFormPathSays` fires each projectile class at targets at known distances on flat terrain, behind the wall and over the ridge, and the hit, its point and its step equal the closed-form path's at 20, 30 and 60 Hz; `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify --dir simcore/streams --content simcore/content` prints `streams verify: 18 streams, 18 match, 0 diverged, 0 not comparable, 0 refused` and exits 0. (The same shots run in WebAssembly in Phase 11.)

### Tests (write first, confirm red)

Every Theory below whose result depends on the step length takes `int hz` from `[MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]` (crossed with its other data through `TestRates.With` / `TestRates.WithRateLast`, or a MemberData built from `TestRates.All` for three-parameter rows), so it runs at 20, 30 and 60 Hz. Configurations come from `TestConfigs.At(hz)` plus the overrides each task names. No `System.Random`: generated segments use the core's SplitMix64 or CsCheck with a seed constant declared in the test and printed on failure.

- [ ] T180 [P] [US5] Add the projectile test worlds and states in `tests/MmoEngine.SimCore.Tests/Support/ProjectileWorlds.cs`:
  - `CollisionData` builders (encoded with `CollisionFile.Encode` and decoded with `CollisionFile.Decode`, as `TestWorlds` does): `Flat()` (a 65 x 65 heightfield, cell 4 m, origin (-128, -128), every height 0, no mesh), `Wall()` (Flat plus one mesh: a vertical two-triangle quad at z = 12.5 m, x from -4 to 4 m, y from 0 to 4 m, both triangle IDs recorded as `WallTriangleIds`), `Ridge()` (Flat with the heightfield row band at z in [20, 28] m raised to a 3 m crest along z = 24 m, crest location recorded as `RidgeCrestZ`).
  - Canonical states built with `TestStates` and loaded through `SimWorld.Reset(state, matchSalt)`: `Shooter(id, position, yaw, stance)` and `Target(id, position, yaw, stance)` characters grounded at rest, and `InFlight(id, ownerId, classIndex, origin, launchVelocity, launchStep, step)` projectiles whose stored `Position` is the closed form p(step - launchStep + 1) evaluated with the contract expression of data-model.md#projectile, so Reset accepts them and the next `SimWorld.Step` sweeps p(k) to p(k + 1) with k = step + 1 - launchStep. No weapon step is needed to put a projectile in flight.
  - `AimAt(Vec3 eye, Vec3 point) -> Vec3` unit direction and `LaunchVelocity(direction, muzzleSpeed)` for building launch states.
- [ ] T181 [P] [US5] Add the double-precision reference in `tests/MmoEngine.SimCore.Tests/Support/ProjectileReference.cs` (test-only, independent of the code under test): `ClosedForm(origin, velocity, halfG, hz, k)` in double (x0 + vx*t, y0 + vy*t + halfG*t*t, z0 + vz*t with t = k / hz), `SegmentCapsule(a, b, capA, capB, radius) -> double? t` (exact quadratic on the unclipped segment in double; start inside gives 0), `SegmentTriangle(a, b, v0, v1, v2) -> double? t` (double-sided), `SegmentHeightfield(CollisionData, a, b) -> (double t, int triangleId)?` over every heightfield triangle the segment's X-Z box touches, `LayoutCapsules(stance, Vec3 feet, ushort yaw, SimConfig)` (config-rules.md hitbox rows rotated by data-model.md#hitbox-layout-and-hitboxes's formula in double), `DistanceToSegment(point, a, b)`, and `PredictFirstHit(...)` returning the step, kind, target ID, hitbox part and point of the first segment whose double evaluation meets terrain, a mesh or a non-owner capsule, earliest t first.
- [ ] T182 [P] [US5] Write failing tests in `tests/MmoEngine.SimCore.Tests/Projectiles/BallisticsTests.cs` (FR-009): `BallisticsTests.PositionAtHasTheContractBits(int hz)`: Given a projectile with Origin (1.5f, 1.62f, -3.25f), LaunchVelocity (412.7f, 35.1f, -431.9f) and `HalfG` = `CompiledConfig.HalfG[0]` at hz, When `Ballistics.PositionAt(in projectile, k, halfG, hz)` is called for every k in 0..`LifetimeSteps[0]`, assert each component's bits (`BitConverter.SingleToUInt32Bits`) equal the contract expression written inline in the test (`t = (float)k / (float)hz`; `x0 + (vx * t)`; `y0 + ((vy * t) + ((halfG * t) * t))`; `z0 + (vz * t)`) and that k = 0 returns Origin's bits; `BallisticsTests.ConsecutiveSegmentsJoinExactly(int hz)`: stepping the same projectile with `ProjectileStep.Step` over an empty flat world and no targets for `LifetimeSteps[0] - 1` steps, the stored Position after step n has exactly the bits of `PositionAt(n - LaunchStep + 1)`, so the end of each step's segment is the start of the next. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~BallisticsTests"` and confirm it fails.
- [ ] T183 [P] [US5] Write failing tests in `tests/MmoEngine.SimCore.Tests/Projectiles/SegmentCapsuleTests.cs` (FR-010, FR-011, research R11): `SegmentCapsuleTests.ClippedTestMatchesADoubleReference`: Given 100,000 segments up to 90 m long and capsules (radius 0.12 to 0.23 m, axis 0.1 to 0.8 m) drawn from SplitMix64 with seed constant `0x5E6C0A75` (printed on failure), When each is tested with `SegmentCapsule.ClipToSphere` then `SegmentCapsule.Intersect` and with `ProjectileReference.SegmentCapsule`, assert zero hit/miss disagreements and every hit point (a + ((b - a) * t)) within 1.1e-4 m (0.11 mm) of the reference point; `SegmentCapsuleTests.KnownSegmentsHitWhereTheGeometrySays(string caseName)`, Theory rows `cylinder-side` (t = 0.5 within 1e-6), `end-cap-a`, `end-cap-b`, `along-the-axis-into-cap-a` (enters at the cap sphere), `misses-by-1mm` (false), `starts-inside` (true with t bits 0x00000000), `zero-length-inside` (true, t 0) and `zero-length-outside` (false); `SegmentCapsuleTests.SegmentMissingTheBoundingSphereNeverReachesTheCapsuleTest` (`ClipToSphere` returns false for a segment passing 1e-3 m outside the sphere, and t0 <= t1 within [0, 1] for every clipped row). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SegmentCapsuleTests"` and confirm it fails.
- [ ] T184 [P] [US5] Write failing tests in `tests/MmoEngine.SimCore.Tests/Collision/SegmentQueryTests.cs` (FR-010 world half, collision-data-format.md#triangle-ids): `SegmentQueryTests.EarliestTriangleWinsAndTiesGoToTheLowerId(string caseName)`, Theory rows `terrain-before-mesh` (a descending segment over `ProjectileWorlds.Wall()` meets the ground before the wall: Kind `HitKind.Terrain`, the heightfield triangle ID `ProjectileReference.SegmentHeightfield` names), `mesh-before-terrain` (Kind `HitKind.Mesh`, TriangleId = the lower-t wall triangle), `two-coincident-mesh-triangles` (both at the same t: the lower ID), `shared-heightfield-edge` (a segment through the v00 -> v11 diagonal: hit, with the lower of the two triangle IDs) and `shared-cell-boundary` (through the edge between two cells: hit, lower ID); each asserts `CollisionWorld.SegmentQuery(a, b, out SegmentHit hit)` returns true and the hit's `T` equals the reference t within 1e-5, `TriangleId` and `Kind` exactly; `SegmentQueryTests.TrianglesAreDoubleSided` (the wall hit from either side gives the same TriangleId); `SegmentQueryTests.BoundaryPlanesAreNeverHit` (a segment leaving the playable area across x = maxX with no geometry returns false; no TriangleId in -4..-1 is ever reported); `SegmentQueryTests.SegmentCrossingManyCellsFindsTheFarTriangle` (a 90 m segment whose only geometry is a mesh triangle in the last grid cell it crosses: that triangle's ID); `SegmentQueryTests.SegmentOutsideTheWorldFindsNothing` (both ends beyond the heightfield and grid: false). Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~SegmentQueryTests"` and confirm it fails.
- [ ] T185 [P] [US5] Write failing test `ProjectileExpiryTests.ProjectilePastItsLimitExpiresAtThatStepWithoutAHit(ExpiryReason reason, int classIndex, int hz)` in `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileExpiryTests.cs` (US5.4): rows (Range, 0), (Range, 1), (Lifetime, 0), (Lifetime, 1), (OutOfBounds, 0), each at every rate. Given `ProjectileWorlds.Flat()` and one `InFlight` projectile with no target in the world other than its owner: Range rows aimed 10 degrees above the horizon with the class's default `maxRangeM` (600 / 400); Lifetime rows aimed 45 degrees up with `projectileClasses[k].maxRangeM` overridden to 10000; the OutOfBounds row with Origin (8100, 50, 0), velocity (+600, 0, 0) and `maxRangeM` 10000; When stepped with `SimWorld.Step` until it leaves the state, assert: `report.Events.Expiries` is exactly `[{projectileId, reason}]` at the step the limit is first passed (computed in the test with the contract expressions in float: Range the first k with |p(k + 1) - Origin|^2 > `MaxRangeSq[k]`; Lifetime k + 1 >= `LifetimeSteps[k]`; OutOfBounds the first k with any |p(k + 1) coordinate| > 8192f), no expiry at any earlier step, `report.Events.Hits` empty at every step, and the committed state's projectile count is 0 from that step on (`ProjectileReference.PredictFirstHit` is asserted to predict no hit, so the row is not vacuous). Add `ProjectileExpiryTests.RifleBurstShotsAboveTheHorizonExpireWithRange(int hz)` (US5.4): building Phase 6's `rifle-burst` definition with `ScenarioCatalog.Get("rifle-burst").Build(TestConfigs.At(hz), testWorld, hz)` and running it with `SimWorld` steps, every shot's projectile ID appears in exactly one expiry event, with reason `ExpiryReason.Range`, at its LaunchStep + the first k whose |p(k + 1) - Origin|^2 > `MaxRangeSq[0]`, and never in a hit event. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ProjectileExpiryTests"` and confirm it fails.
- [ ] T186 [US5] Write failing tests in `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileExpiryTests.cs` (data-model.md#projectile-step steps 5 and 6): `ProjectileExpiryTests.LimitsApplyInContractOrder(string caseName)`, Theory rows `range-and-lifetime-same-step` (expiry reason Range), `lifetime-and-out-of-bounds-same-step` (Lifetime), `range-and-out-of-bounds-same-step` (Range), each asserting the single expiry event's reason; `ProjectileExpiryTests.HitBeyondTheRangeExpiresWithRange(int hz)`: a projectile whose segment meets a target's torso at a point with |point - Origin|^2 > `MaxRangeSq[0]` (Origin 599.9 m from the target, `maxRangeM` 600) reports `events.expiries == [{projectileId, Range}]`, no hit event, and is removed. Run the same filter and confirm they fail.
- [ ] T187 [US5] Write failing test `ProjectileHitTests.TargetInThePathReportsTheStruckHitboxPointAndStep(Stance stance, HitboxPart part, int hz)` in `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileHitTests.cs` (US5.2), rows every (Standing, Crouched) x (Head, Torso, Legs) at every rate: Given `ProjectileWorlds.Flat()`, a shooter (ID 1) at (0, 0, 0) facing +Z and a target (ID 2) at (0, 0, 25) with that stance and yaw 32768 (facing the shooter), and one class-0 `InFlight` projectile owned by 1 launched from the shooter's eye toward that part's centre (the midpoint of the layout's A and B for that stance from config-rules.md, rotated by the target's yaw), When stepped with `SimWorld.Step`, assert that at exactly the step `ProjectileReference.PredictFirstHit` names, `report.Events.Hits` is exactly one element with that projectile ID, `Kind == HitKind.Character`, `TargetId == 2`, `Hitbox == part`, `0f <= T <= 1f`, and a point whose double distance to that capsule's axis segment (from `HitboxBuilder`'s output) equals the capsule's radius within 1e-4 m; no hit or expiry event at any earlier step; and the projectile ID is absent from the committed state after that step. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ProjectileHitTests"` and confirm it fails.
- [ ] T188 [US5] Write failing test `ProjectileHitTests.MeshBetweenMuzzleAndTargetIsHitInstead(int hz)` in `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileHitTests.cs` (US5.3): Given `ProjectileWorlds.Wall()`, the shooter at (0, 0, 0), the target standing at (0, 0, 25) behind the wall at z = 12.5 m, and a class-0 projectile aimed at the target's torso centre, When stepped until the projectile leaves the state, assert the single hit event has `Kind == HitKind.Mesh`, `TargetId` equal to the wall triangle `ProjectileReference.SegmentTriangle` reports first along the line (one of `WallTriangleIds`), `Hitbox == HitboxPart.None` (255), point.z within 1e-4 m of 12.5, at the predicted step; and no step's hit list holds `Kind == HitKind.Character` or `TargetId == 2`. Run the `ProjectileHitTests` filter and confirm it fails.
- [ ] T189 [US5] Write failing test `ProjectileHitTests.ProjectileCrossingAWholeHitboxInOneStepStillHits(int hz)` in `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileHitTests.cs` (US5.6): Given `weapons[0].muzzleSpeed` 2000 (so one step's segment, 2000 / hz >= 33.3 m, is longer than ten torso diameters, 4.4 m, asserted in the arrange), a standing target placed so that its torso lies in the middle of one step's segment and both of that segment's end points are outside the target's bounding sphere (asserted in the arrange with the double distance to `BoundCenterUp` against `BoundRadius`), and a class-0 projectile aimed at the torso centre, When stepped, assert that step's hit list is exactly one hit with `Kind == HitKind.Character`, `Hitbox == HitboxPart.Torso`, `0f < T && T < 1f`, at that step, and the projectile is removed. Run the `ProjectileHitTests` filter and confirm it fails.
- [ ] T190 [US5] Write failing test `ProjectileHitTests.ProjectileStartingInsideAHitboxHitsAtItsFirstStep(int hz)` in `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileHitTests.cs` (Edge case: point-blank): Given a target standing at (0, 0, 0) yaw 0 and a projectile owned by another character with LaunchStep = n (k = 0) and Origin (0, 1.21, 0), inside the target's torso capsule, built with `HitboxBuilder` into a span, When `ProjectileStep.Step(ref projectile, n, config, collision, targets, out hit, out expiry)` is called once, assert it returns `ProjectileOutcome.Hit`, `hit.T` has bits 0x00000000, `hit.Point` has exactly Origin's bits, `hit.Hitbox == HitboxPart.Torso`, `hit.TargetId` is the target's ID; and through `SimWorld.Step` with the same projectile spawned in that step the hit is reported in step n itself with T == 0. Run the `ProjectileHitTests` filter and confirm it fails.
- [ ] T191 [US5] Write failing test `ProjectileHitTests.EarliestHitAlongTheSegmentWinsAndTiesGoToTheLowerId(string caseName, int hz)` in `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileHitTests.cs` (FR-010, data-model.md#projectile-step step 4), rows: `nearer-target-first` (targets 9 at 20 m and 5 at 24 m on the line: TargetId 9), `same-position-lower-id` (targets 5 and 9 at the same position and pose, so their capsules are bit-identical: TargetId 5), `overlapping-parts-lower-index` (a crouched target hit at a point where the segment enters head and torso at the same t: `Hitbox == HitboxPart.Head`), `world-before-character` (a mesh triangle placed so its t equals the target's entry t exactly: `Kind == HitKind.Mesh`); each asserts the one hit event's kind, target ID and hitbox exactly. Add `ProjectileHitTests.OwnersHitboxesAreNeverTested(int hz)`: a projectile launched from the shooter's own eye (inside the shooter's head capsule) with OwnerId = the shooter reports no hit at k = 0 and flies on (`ProjectileOutcome.Flying`), while the same projectile with OwnerId = another character hits the shooter's Head with T bits 0x00000000. Run the `ProjectileHitTests` filter and confirm they fail.
- [ ] T192 [P] [US5] Write failing tests in `tests/MmoEngine.SimCore.Tests/World/ProjectileStageTests.cs` (data-model.md#world-step-order stage 5): `ProjectileStageTests.HitAndExpiredProjectilesAreRemovedAndTheRestKeepTheirOrder`: Given a Reset state with projectiles 4 (flying on), 7 (hits a target this step) and 9 (passes its lifetime this step) and NextProjectileId 10, When `SimWorld.Step` runs, assert the committed projectiles are exactly [4] with Position = PositionAt(k + 1) bits, `Events.Hits` is [7], `Events.Expiries` is [{9, Lifetime}], NextProjectileId is still 10, and the state bytes `StateSerializer` writes match those projectile and event lists; `ProjectileStageTests.NewProjectilesAreSweptInTheStepTheyAreFired`: a character pressing Fire with slot 0 Ready in a state that already holds projectile 3 produces, in the same step, the new projectile at Position = PositionAt(1) after projectile 3 in ascending ID; `ProjectileStageTests.HitboxesComeFromThePostMovementPose`: a target running across the line of a projectile is hit at the point the post-movement `HitboxBuilder` pose predicts, not the pre-movement one. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ProjectileStageTests"` and confirm it fails.
- [ ] T193 [US5] Write failing test `ProjectileScenarioTests.HorizontalShotFollowsTheClosedFormAndHitsTheTerrainWhereItFirstMeetsIt(int classIndex, int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/ProjectileScenarioTests.cs` (US5.1), rows class 0 and 1 at every rate: Given `ProjectileWorlds.Flat()` and one `InFlight` projectile of that class launched horizontally from (0, 1.62, 0) along +X at the class's default muzzle speed (600 / 120 m/s) with no target in range, When stepped with `SimWorld.Step` until it leaves the state, assert at every step before the hit its stored Position has exactly the bits of `Ballistics.PositionAt(k + 1)` and lies within 1e-3 m of `ProjectileReference.ClosedForm(k + 1)` in double; and the step whose hit list holds it is exactly the first step whose segment p(k) -> p(k + 1), evaluated in double, crosses height 0 (`ProjectileReference.SegmentHeightfield`), with `Kind == HitKind.Terrain`, `Hitbox == HitboxPart.None`, point.y within 1e-3 m of 0, and no hit at any earlier step. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ProjectileScenarioTests"` and confirm it fails.
- [ ] T194 [US5] Write failing test `ProjectileScenarioTests.LauncherArcMeetsTheTerrainAtTheClosedFormStep(int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/ProjectileScenarioTests.cs` (US5.1, the `launcher-arc` definition): Given `ScenarioCatalog.Get("launcher-arc").Build(TestConfigs.At(hz), testWorld, hz)` on `simcore/content/test-world.mmcd` (opened read-only through `RepoPaths`), When its steps run on a `SimWorld`, assert exactly one shot event (slot 1, class 1), the spawned projectile's Position at every later step has the bits of `Ballistics.PositionAt(k + 1)` from its stored Origin and LaunchVelocity, and its single hit has `Kind == HitKind.Terrain` at exactly the step `ProjectileReference.SegmentHeightfield` predicts along the double closed form, with the triangle ID it names; no expiry event. Run the `ProjectileScenarioTests` filter and confirm it fails.
- [ ] T195 [US5] Write failing test `ProjectileScenarioTests.MeshOccludesShotHitsTheWallAndNeverTheTarget(int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/ProjectileScenarioTests.cs` (US5.3, the `mesh-occludes-shot` definition): building and running the definition at hz on the test world, every hit event has `Kind == HitKind.Mesh`, a TargetId among the test world's wall mesh triangle IDs and `Hitbox == HitboxPart.None`; there is at least one shot and exactly one hit per shot; no hit event names the target's character ID. Run the `ProjectileScenarioTests` filter and confirm it fails.
- [ ] T196 [US5] Write failing test `ProjectileScenarioTests.StruckPartsMatchTheStancesLayout(Stance stance, int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/ProjectileScenarioTests.cs` (US5.5a, the layout half; the bit-for-bit half across builds is Phase 11's): rows Standing and Crouched at every rate; for target yaws {0, 12345, 16384, 32768, 49152} and each part, Given a target 25 m from the shooter with that stance and yaw and a class-0 projectile aimed at the part's configured centre (layout A/B midpoint from config-rules.md rotated by that yaw), When stepped, assert the hit's `Hitbox` equals the part `ProjectileReference.LayoutCapsules` (the configured layout in double, independent of `HitboxBuilder`) puts first along the line, its point's double distance to that reference capsule's axis equals the configured radius within 1e-4 m, and across the yaw rows of each stance every one of Head, Torso and Legs is struck at least once. Run the `ProjectileScenarioTests` filter and confirm it fails.
- [ ] T197 [US5] Write failing test `ProjectileScenarioTests.ProjectileHitsHitboxStrikesEveryPlannedPart(int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/ProjectileScenarioTests.cs` (US5.2, US5.6 and point-blank through the `projectile-hits-hitbox` definition): building and running the definition at hz, the hit events in step order are exactly seven Character hits on the target's ID with parts [Head, Torso, Legs] while it stands, [Head, Torso, Legs] while it crouches, then the point-blank hit; every aimed hit has `0f < T && T < 1f` and both end points of its striking segment outside the target's bounding sphere (a whole hitbox crossed within one step); the point-blank hit is reported in the step its shot fires with T bits 0x00000000; no hit names the shooter. Run the `ProjectileScenarioTests` filter and confirm it fails.
- [ ] T198 [US5] Write failing test `ProjectileScenarioTests.ShotsAtKnownDistancesHitWhereTheClosedFormPathSays(int classIndex, string placement, int hz)` in `tests/MmoEngine.SimCore.Tests/Scenarios/ProjectileScenarioTests.cs` (US5-IT), rows class 0 and 1 x placements `flat` (`ProjectileWorlds.Flat()`, target standing 25 m away, aimed at the torso centre with the class's gravity drop compensated by the launch pitch the double closed form gives), `behind-wall` (`Wall()`, target at 25 m behind the wall: hits the wall), `over-ridge-clear` (`Ridge()`, aimed high enough that the double path clears the crest and lands beyond it) and `over-ridge-blocked` (aimed low: hits the ridge face), at every rate: assert the hit's kind, target ID (character or triangle) and hitbox equal `ProjectileReference.PredictFirstHit`'s, its point within 1e-3 m of the predicted point, and its step equal to the predicted step. Run the `ProjectileScenarioTests` filter and confirm it fails.
- [ ] T199 [P] [US5] Write failing test `ScenarioCatalogTests.CatalogAndCommittedStreamsAreTheSameEighteen` in `tests/MmoEngine.SimCore.Tests/Scenarios/ScenarioCatalogTests.cs`: `ScenarioCatalog.Names` equals, in ordinal order, the 18 names of contracts/recorded-stream-format.md (`corner-wedge`, `crouch-under-ledge`, `flat-run-20hz`, `flat-run-30hz`, `flat-run-60hz`, `fuzz-all-buttons`, `heat-overheat`, `jump-land`, `launcher-arc`, `mesh-occludes-shot`, `projectile-hits-hitbox`, `reload-and-empty`, `rifle-burst`, `slope-limit`, `stairs-blocked`, `stairs-climb`, `thin-wall-fast`, `wall-slide-45`); `simcore/streams/` (listed read-only through `RepoPaths`) holds exactly one `<name>.mmrs` for each and no other `.mmrs`. Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ScenarioCatalogTests"` and confirm it fails (the three projectile definitions and seven fire streams are missing).

### Implementation

- [ ] T200 [P] [US5] Implement `Ballistics` in `src/MmoEngine.SimCore/Projectiles/Ballistics.cs`: `public static Vec3 PositionAt(in Projectile projectile, uint k, float halfG, int stepRateHz)` with `t = (float)k / (float)stepRateHz` and exactly data-model.md#projectile's parenthesisation (`x0 + (vx * t)`, `y0 + ((vy * t) + ((halfG * t) * t))`, `z0 + (vz * t)`); no FMA, no `Math`/`MathF`, explicit usings, allocation-free. Makes T182's `PositionAtHasTheContractBits` pass (with T203 for `ConsecutiveSegmentsJoinExactly`).
- [ ] T201 [P] [US5] Implement `SegmentCapsule` in `src/MmoEngine.SimCore/Geometry/SegmentCapsule.cs` per research R11 and data-model.md#projectile-step step 3: `public static bool ClipToSphere(Vec3 a, Vec3 b, Vec3 centre, float radius, out float t0, out float t1)` in Ray Tracing Gems ch. 7's stable form (d = b - a, f = a - centre, Δ = r² - |f - (((f·d) / (d·d)) · d)|², miss when Δ < 0, q = -(f·d) + sign(-(f·d)) · SimMath.Sqrt((d·d) · Δ), roots (f·f - r²) / q and q / (d·d) sorted and clamped to [0, 1], a zero-length segment tested as a point), and `public static bool Intersect(Vec3 p, Vec3 q, Vec3 capsuleA, Vec3 capsuleB, float radius, out float t)` solving the cylinder (Ericson §5.3.7) and the two end spheres on the short clipped segment and keeping the smallest t, with a start inside the capsule returning t = 0; the combined parameter on the original segment is `t0 + ((t1 - t0) * tc)` so a start inside gives exactly 0. Float32 only, fixed parenthesisation, `SimMath` for square roots and comparisons, no branch on platform, allocation-free. Makes T183 pass.
- [ ] T202 [P] [US5] Implement the segment query in `src/MmoEngine.SimCore/Collision/SegmentHit.cs` (`readonly struct SegmentHit(float T, int TriangleId, HitKind Kind)`) and `src/MmoEngine.SimCore/Collision/CollisionWorld.SegmentQuery.cs` (partial `CollisionWorld`) per data-model.md#collision-world and collision-data-format.md#triangle-ids: `public bool SegmentQuery(Vec3 a, Vec3 b, out SegmentHit hit)` walks the heightfield cells the segment's X-Z projection crosses (2D grid walk, loop bound `Columns + Rows`; a cell whose four corner heights lie wholly above or below the segment's Y range in that cell is skipped) testing the on-demand triangles, then the uniform-grid cells it crosses (bound `GridCellsX + GridCellsZ`) testing each cell's CSR mesh triangles; each test is a double-sided segment–triangle intersection (Ericson §5.3.6 accepting either sign of the determinant, inclusive barycentric edges so shared edges are watertight, the same 64 m local-origin snap as `Geometry/ClosestPoints.cs`); keep the smallest t with ties to the lower triangle ID; `Kind` is `HitKind.Terrain` for IDs below the heightfield triangle count and `HitKind.Mesh` otherwise; boundary planes (-4 to -1) are never tested. No allocation, no LINQ, no exceptions. Makes T184 pass.
- [ ] T203 [US5] Implement the projectile step in `src/MmoEngine.SimCore/Projectiles/ProjectileOutcome.cs` (`enum ProjectileOutcome : byte { Flying, Hit, Expired }`), `src/MmoEngine.SimCore/Projectiles/ProjectileTargets.cs` (`readonly ref struct ProjectileTargets` over the world's hitbox scratch exactly as Phase 2's `HitboxBuilder` writes it: the character IDs in index order, three `Hitbox` per character in Head, Torso, Legs order, and each character's bounding sphere) and `src/MmoEngine.SimCore/Projectiles/ProjectileStep.cs`: `public static ProjectileOutcome Step(ref Projectile projectile, uint step, CompiledConfig config, CollisionWorld collision, ProjectileTargets targets, out ProjectileHit hit, out ExpiryReason expiry)` following data-model.md#projectile-step steps 1-6 for k = step - LaunchStep: a = `Ballistics.PositionAt(k)`, b = `PositionAt(k + 1)` with `config.HalfG[Class]`; world candidate from `collision.SegmentQuery`; character candidates for every character whose ID is not OwnerId, `SegmentCapsule.ClipToSphere` on its bounding sphere then `Intersect` on its three capsules; the earliest hit by the key (t, world before character, target ID, hitbox index) compared as exact floats; point = a + ((b - a) * t) per component; a hit with |point - Origin|² <= `MaxRangeSq[Class]` returns Hit (`{Id, kind, targetId, hitbox, point, t}`, hitbox `HitboxPart.None` and the triangle ID for world hits), otherwise Expired with Range; with no hit, Expired with the first of Range (|b - Origin|² > MaxRangeSq), Lifetime (k + 1 >= `LifetimeSteps[Class]`), OutOfBounds (any |b component| > 8192f); otherwise Position = b and Flying. Every loop bounded by the targets' length (at most `world.maxCharacters` x 3); no allocation, no LINQ, no exceptions on the tick path. Makes T182's `ConsecutiveSegmentsJoinExactly` and the direct-call halves of T190 and T191's `OwnersHitboxesAreNeverTested` pass.
- [ ] T204 [US5] Replace the pass-through `StepProjectiles` in `src/MmoEngine.SimCore/World/SimWorld.cs` (stage 5 of data-model.md#world-step-order): build a `ProjectileTargets` over the stage-4 hitbox scratch, iterate the next projectile list (the current projectiles carried forward, then the step's new projectiles that `StepWeapons` appended) in ascending ID calling `ProjectileStep.Step(ref p, CurrentStep + 1, ...)`, compact survivors in place keeping ascending ID, append hit events to the world's hit buffer and expiry events to its expiry buffer in ascending projectile ID; NextProjectileId unchanged; stage 6 then canonicalises and hashes as before. Nothing allocated; the 11 movement streams' hashes stay unchanged because they hold no projectile (checked by `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess`). Makes T192, T187, T188, T189, T190, T191, T185, T186, T193, T196 and T198 pass.
- [ ] T205 [US5] Add the three projectile definitions to `src/MmoEngine.SimCore.Server/ScenarioCatalog.cs` per contracts/simcore-cli.md#named-scenario-streams, each building at 20, 30 and 60 Hz from seconds, with the definition's own rate 30 Hz for the committed stream:
  - `launcher-arc`: one character on the test world's flat area, initial state with ActiveSlot 1 and the launcher Ready, a command every step for character index 0 (batchable): Fire held for the first step at pitch 0 over the flat area, then no buttons for 1.5 s so the projectile reaches the terrain.
  - `projectile-hits-hitbox`: two characters, shooter and target 25 m apart on flat terrain, the target facing the shooter; the stream's configuration sets the rifle's `spreadHalfAngleDeg`, `recoilKickPitchDeg` and `recoilKickYawDeg` to 0 so each shot flies where it is aimed; one-step Fire presses separated by more than the fire interval, aimed (yaw and pitch from `SimMath.Atan2` through `SimMath.RoundToInt` to binary angle units) at the target's head, torso and legs centres while it stands, then while it holds Crouch, then the target runs toward the shooter and the shooter fires at the first step at which its eye lies inside one of the target's post-movement hitboxes (found by the definition stepping a `SimWorld` over the target's commands at Build), giving the point-blank shot.
  - `mesh-occludes-shot`: two characters on opposite sides of the test world's wall mesh, the same zero-spread rifle override, the shooter firing at the target's torso through the wall; `Build` throws `InvalidOperationException` naming the scenario if `CollisionWorld.SegmentQuery` finds no wall triangle on the aimed line.
  Makes T194, T195 and T197 pass.
- [ ] T206 [US5] Record the seven fire streams with the CLI only, one command each, in this order, and commit each `.mmrs` with its `simcore/streams/REGENERATIONS.md` entry. Before the first command, save the starting commit with `START="$(git rev-parse HEAD)"`:
  - `dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --scenario fuzz-all-buttons --reason "First recording: projectile flight (stage 5) is in the world step"`
  - the same command for `heat-overheat`, `launcher-arc`, `mesh-occludes-shot`, `projectile-hits-hitbox`, `reload-and-empty` and `rifle-burst`, each with a one-line reason naming the scenario's first recording;
  each prints `<name>: added; file <x16>; final state <x16>` and exits 0, writing `simcore/streams/<name>.mmrs` with rules revision 1 (`SimCoreVersion.RulesRevision` unchanged). Then `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify --dir simcore/streams --content simcore/content` prints `streams verify: 18 streams, 18 match, 0 diverged, 0 not comparable, 0 refused` and exits 0, `git diff --exit-code HEAD -- simcore/content` exits 0 (the working tree still holds the committed test world), and `git diff --exit-code "$START" HEAD -- simcore/content simcore/streams/{corner-wedge,crouch-under-ledge,flat-run-20hz,flat-run-30hz,flat-run-60hz,jump-land,slope-limit,stairs-blocked,stairs-climb,thin-wall-fast,wall-slide-45}.mmrs` exits 0 (the seven recordings changed neither the content nor any of the 11 movement streams). Makes T199 pass and keeps `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` green over all 18.

**Checkpoint**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Projectiles|FullyQualifiedName~SegmentQueryTests|FullyQualifiedName~ProjectileStageTests|FullyQualifiedName~ProjectileScenarioTests|FullyQualifiedName~ScenarioCatalogTests|FullyQualifiedName~StreamsCommandTests"` passes at 20, 30 and 60 Hz; the full `dotnet test MmoEngine.sln` passes with the Phase 3, 4 and 6 tests unchanged; `streams verify` exits 0 with `18 streams, 18 match`; `dotnet build MmoEngine.sln -warnaserror` (with the determinism analyzer on the core) and `dotnet format MmoEngine.sln --verify-no-changes` exit 0; every new `src/` file in this phase reaches 95% line coverage in `tools/coverage_gate.py`.

---

## Phase 8: User Story 6 - The core's formats are versioned and fixed by golden fixtures (Priority: P3)

**Goal**: Pin every format's bytes with committed golden fixtures written by `simcore golden write`. Finish the collision and stream decoders' remaining refusal checks, so that an unknown version, truncation, oversize, inconsistency, a corrupted checksum or missing collision data is refused with the exact code, offset and field, and never gives a partial result.

**Independent Test**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~GoldenFixtureTests|FullyQualifiedName~GoldenRefusalTests|FullyQualifiedName~GoldenCommandTests"` passes. Every valid fixture round-trips to identical bytes, every refusal fixture is refused with its `expectedError`, and `golden write` on a copy of `simcore/golden` rewrites nothing.

### Tests (write first, confirm red)

- [ ] T207 [US6] Write the read-only fixture support `tests/MmoEngine.SimCore.Tests/Support/GoldenFixtures.cs` (static class `GoldenFixtures`, namespace `MmoEngine.SimCore.Tests.Support`) first, because every test below uses it:
  - `Root` is `RepoPaths.Root` + `simcore/golden`. `Formats` is `["input", "collision", "stream"]`.
  - `ReadBin(string format, string caseName)` returns `byte[]`. `ReadJson(string format, string caseName)` returns a `JsonDocument`. Both open `<format>/<case>.bin` or `.json` through `RepoPaths` with `FileAccess.Read` and never write.
  - `TheoryData<string, string>` members, rows `(format, case)`. They are enumerated from the `.json` files in ordinal order and classified by reading `kind`, `refusedAt` and `expectedError.code` with `JsonDocument`, not with the code under test:
    - `ValidCases`;
    - `RefusalCases`;
    - `DecodeRefusalCases` (refusals with no `refusedAt`);
    - `OtherDecodeRefusals` (decode refusals whose code is neither `UnsupportedVersion` nor `ChecksumMismatch`);
    - `OpenRefusals`.
  - `ExpectedError(format, caseName)` returns `(FormatErrorCode Code, long Offset, string Field)`, parsed from `expectedError` with `Enum.Parse<FormatErrorCode>`.
  - `CollisionFixtureWorlds(caseName)` returns an `IReadOnlyList<CollisionWorld>`: `CollisionFile.Decode` of `collision/<name>.bin` for each name in a stream case's `collisionFixtures`.
  - `ContractCaseNames(format)` holds the case names of the contract tables as literals:
    - input, 38 cases: the 29 valid and 9 refusal cases of contracts/input-command-format.md#input-cases;
    - collision, 22 cases: 4 valid and 18 refusal (contracts/collision-data-format.md#golden-fixtures);
    - stream, 32 cases: 4 valid and 28 refusal (contracts/recorded-stream-format.md#golden-fixtures).
- [ ] T208 [US6] Write failing test `GoldenFixtureTests.EachValidFixtureRoundTripsToItsBytes(string format, string caseName)` in `tests/MmoEngine.SimCore.Tests/Formats/GoldenFixtureTests.cs` (US6.1). It is a `[Theory]` with `[MemberData(nameof(GoldenFixtures.ValidCases), MemberType = typeof(GoldenFixtures))]`.
  - **Given** each committed valid case. **When** its `.json` is parsed with `GoldenJson.Parse` and encoded (`InputCodec.TryEncode` into a 17-byte buffer, `CollisionFile.Encode(data)` or `RecordedStream.Encode()`), **then** the bytes equal the `.bin` byte for byte.
  - **When** the `.bin` is decoded, **then** every decoded value equals the JSON's:
    - input: `TryDecode` returns true, and the six fields are equal;
    - collision: `CollisionFile.Decode(bin).Data` matches the header fields, every height and every vertex coordinate compared as `BitConverter.SingleToUInt32Bits` (so `tiny-world`'s `heights[8]` stays `0x80000000`), each `meshId` and each `indices` array;
    - stream: `RecordedStream.Decode(bin)` matches `rulesRevision`, `stepHz`, `characterCount`, and `seed`, `matchSalt`, `collisionChecksum` and every `expectedHash` as x16 strings. It also matches the config entries (`fieldId` and `valueBits`, re-read from the decoded `Config` through `ConfigBlock`), `initialStateHex`, and every step's `step` and commands.
  - Re-encoding the decoded value gives the `.bin` again: `TryEncode`, `CollisionFile.Encode(world.Data)` or `stream.Encode()`.
  - For collision and stream cases, the `fileLength` field of the encoded bytes (u64 at offset 8 or 56) and the trailer as x16 equal the JSON's `fileLength` and `checksum`. Stream cases also check `configLength`, `initialStateLength` and `stepsLength` (`two-steps`: 698, 136, 44, `fileLength` 950).
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~GoldenFixtureTests.EachValidFixtureRoundTripsToItsBytes"` and confirm it fails. There are no fixtures and no `GoldenJson` yet.
- [ ] T209 [US6] In the same file `tests/MmoEngine.SimCore.Tests/Formats/GoldenFixtureTests.cs`, write failing tests (FR-020, FR-023):
  - `EveryContractCaseIsCommitted(string format)`, `[InlineData("input")]`, `[InlineData("collision")]`, `[InlineData("stream")]`:
    - the case names that have both a `.json` and a `.bin` in `simcore/golden/<format>/` equal `GoldenFixtures.ContractCaseNames(format)` exactly (38, 22 and 32 names);
    - the folder holds no other file;
    - every JSON has `format` equal to its folder, `formatVersion` 1, and `case` equal to its file name.
  - `FloatValuesParseBackToTheirBits(string format, string caseName)` over `ValidCases`:
    - every float object `{bits, value}` has `bits` matching `^0x[0-9A-F]{8}$`;
    - `float.Parse(<value's raw JSON text>, CultureInfo.InvariantCulture)` has exactly those bits (`-0.0` gives `0x80000000`);
    - every 64-bit string matches `^[0-9a-f]{16}$`;
    - every `fieldId` matches `^0x[0-9A-F]{4}$` and every `valueBits` matches `^0x[0-9A-F]{8}$`.
  - `InputCasesValidateAsRecorded(string caseName)` over the input valid cases: `InputValidator.Validate(command, validate.lastSequence, validate.weaponSlots)` equals `Enum.Parse<InputRejection>(validate.expected)`. For example, `reject-sequence-first` gives `SequenceNotIncreasing`, `reject-buttons-all` gives `UnknownButtons`, `reject-pitch-min` gives `PitchOutOfRange`, and `weapon-slot-last` gives `None`.
  - `CollisionCasesHaveTheirDerivedData(string caseName)` over the collision valid cases. The decoded world's values equal the JSON's `derived` object: `HeightfieldTriangleCount`, the mesh triangle IDs, `DegenerateTriangleCount` and the dropped IDs, `GridCellsX`, `GridCellsZ`, and each cell's triangle list (read through the grid accessor that Phase 2's `CollisionWorldBuildTests` uses). Pinned values:

    | Case | Heightfield triangles | Mesh triangle IDs | Degenerate IDs | Grid | Cells |
    | --- | --- | --- | --- | --- | --- |
    | `tiny-world` | 8 | [8, 9, 10] | [10] | 1 × 1 | [[8, 9]] |
    | `extent-at-limit` | 8 | [8, 9, 10] | [10] | 1 × 1 | [[8, 9]] |
    | `heightfield-only` | 8 | [] | [] | 1 × 1 | [[]] |
    | `full-cell` | 8 | 8 to 71 | none | 1 × 1 | [[8, …, 71]] (64 IDs) |

  - `StreamConfigEntriesNameTheirRuleTablePaths(string caseName)` over the stream valid cases: the entries' `fieldId`s are strictly ascending, each entry's `path` equals the `ConfigFields` row for that `fieldId`, and `two-steps` holds exactly 116 entries.
  - Confirm red with `--filter "FullyQualifiedName~GoldenFixtureTests"`.
- [ ] T210 [US6] Write failing test `GoldenRefusalTests.UnknownVersionIsRefusedNamingTheVersionFoundAndSupported(string format, string caseName)` in `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs` (US6.2).
  - **Given** the case's committed `.bin`. **When** it is decoded, **then** the error's `Code` is `FormatErrorCode.UnsupportedVersion`, its `Offset` and `Field` equal the case's `expectedError`, and `ToString()` is exactly the message in the table:

    | Format, case | Offset | Field | Message |
    | --- | --- | --- | --- |
    | input, `refuse-version-0` | 1 | `version` | `input version at offset 1: unsupported version 0; this reader supports version 1` |
    | input, `refuse-version-2` | 1 | `version` | `input version at offset 1: unsupported version 2; this reader supports version 1` |
    | input, `refuse-version-2-longer` | 1 | `version` | the same message; this 20-byte case shows that the version is checked before the length |
    | collision, `unsupported-version` | 4 | `version` | `collision version at offset 4: unsupported version 2; this reader supports version 1` |
    | stream, `unsupported-version` | 4 | `version` | `stream version at offset 4: unsupported version 2; this reader supports version 1` |
    | stream, `command-bad-version` | 905 | `steps[0].commands[0].input.version` | `stream steps[0].commands[0].input.version at offset 905: unsupported version 2; this reader supports version 1` |

  - For input rows, `InputCodec.TryDecode` returns false and `command == default(InputCommand)`. For collision and stream rows, `Assert.Throws<CoreFormatException>` on `CollisionFile.Decode` or `RecordedStream.Decode`, so no value is returned.
  - Confirm it fails.
- [ ] T211 [US6] In `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs`, write failing test `GoldenRefusalTests.MalformedBytesAreRefusedAtTheirOffsetAndFieldWithNoResult(string format, string caseName)` (US6.3), with `[MemberData(nameof(GoldenFixtures.OtherDecodeRefusals), MemberType = typeof(GoldenFixtures))]`. Its 43 rows:
  - input: `refuse-empty`, `refuse-format-id`, `refuse-only-format-id`, `refuse-truncated-2`, `refuse-truncated-16`, `refuse-oversized-18`;
  - collision: `unknown-magic`, `header-length`, `short-header`, `truncated`, `trailing-bytes`, `columns-too-few`, `too-many-meshes`, `counts-disagree`, `reserved-nonzero`, `grid-cell-too-small`, `extent-out-of-range`, `non-finite-height`, `height-out-of-range`, `vertex-out-of-range`, `mesh-index-out-of-range`, `crowded-cell`;
  - stream: `unknown-magic`, `header-length`, `short-header`, `truncated`, `trailing-bytes`, `too-many-characters`, `zero-steps`, `config-too-long`, `state-too-long`, `lengths-disagree`, `step-hz-out-of-range`, `step-hz-mismatch`, `config-count-short`, `config-not-ascending`, `config-unknown-field`, `config-missing-field`, `state-character-count`, `state-has-events`, `step-number-gap`, `too-many-commands`, `command-index-out-of-range`.

  What each row asserts:
  - The error's `Code`, `Offset` and `Field` equal the JSON's `expectedError` exactly. For example:
    - collision `crowded-cell`: `OutOfRange`, 3204, `grid.cells[0,0]`;
    - collision `extent-out-of-range`: `OutOfRange`, 32, `columns`;
    - stream `state-character-count`: `Inconsistent`, 766, `initialState.characterCount`;
    - stream `config-count-short`: `Oversized`, 756, `config.count`.
  - `Format` is `input`, `collision` or `stream`.
  - Input rows: `TryDecode` returns false and `command == default(InputCommand)`. Collision and stream rows: `Assert.Throws<CoreFormatException>`, whose `Error` carries those values, and no value is returned.

  Confirm it fails.
- [ ] T212 [US6] In `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs`, write failing test `GoldenRefusalTests.ContractMessagesAreReproducedExactly(string format, string caseName, string message)` (US6.3). It is an `InlineData` Theory: decoding the `.bin` gives a refusal whose `ToString()` equals the contract's message exactly.

  | Format, case | Message |
  | --- | --- |
  | collision, `truncated` | `collision fileLength at offset 100: the file declares 188 bytes, 100 are present` |
  | collision, `counts-disagree` | `collision fileLength at offset 8: the counts need 200 bytes; fileLength is 188` |
  | collision, `non-finite-height` | `collision heights[4] at offset 64: NaN is not finite` |
  | collision, `mesh-index-out-of-range` | `collision meshes[0].indices[1] at offset 148: vertex index 4 is not below vertexCount 4` |
  | collision, `crowded-cell` | `collision grid.cells[0,0] at offset 3204: 65 triangles; the limit is 64` |
  | stream, `lengths-disagree` | `stream fileLength at offset 56: the blocks need 951 bytes; fileLength is 950` |
  | stream, `config-not-ascending` | `stream config.entries[1].fieldId at offset 72: field 0x0001 is not above 0x0002` |
  | stream, `config-missing-field` | `stream config.input.weaponSlots at offset 756: field 0x5001 is missing` |
  | stream, `state-has-events` | `stream initialState.events.expiries.count at offset 890: a loaded state has no events, found 1` |

  Confirm it fails.
- [ ] T213 [US6] In `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs`, write failing test `GoldenRefusalTests.ChecksumMismatchIsRefused(string format, string caseName)` (US6.4), rows `("collision", "checksum-mismatch")` and `("stream", "checksum-mismatch")`.
  - **Given** a fixture whose content no longer matches its trailer. **When** it is decoded, **then** `Assert.Throws<CoreFormatException>`, with `Code` `ChecksumMismatch` and field `checksum`:
    - collision: offset 180, and the message is exactly `collision checksum at offset 180: the trailer is 9224e3fa3b3fca9c, the content hashes to <h>`;
    - stream: offset 942, and the message is exactly `stream checksum at offset 942: the trailer is <t>, the content hashes to <h>`.
  - `<h>` is `System.IO.Hashing.XxHash64.HashToUInt64(bin.AsSpan(0, 180 or 942))` as x16, computed in the test. The contract's example prints `20e8d90416fe5a4a` for the collision case. `<t>` is the u64 trailer of `stream/two-steps.bin` as x16.
  - Confirm it fails.
- [ ] T214 [US6] In `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs`, write failing test `GoldenRefusalTests.StreamWithoutItsCollisionDataIsRefusedBeforeStepZero(string caseName)` (US6.5).
  - Rows: `collision-missing`, where `collisionFixtures` is `[]`, and `collision-other`, where it is `["heightfield-only"]`, whose checksum differs from `9224e3fa3b3fca9c`.
  - **Given** the stream `.bin`, which `RecordedStream.Decode` accepts (asserted first). **When** `StreamRunner.Run(bin, GoldenFixtures.CollisionFixtureWorlds(caseName))` is called (the `IReadOnlyList<CollisionWorld>` overload), **then** it throws `CoreFormatException` with `Code` `CollisionDataMissing`, `Offset` 36 and `Field` `collisionChecksum`, and the message is exactly `stream collisionChecksum at offset 36: collision data 9224e3fa3b3fca9c was not supplied`.
  - The exception is the call's only outcome: no record array is returned, and the exception is not a `StepFaultException`, so no step ran.
  - Confirm it fails.
- [ ] T215 [US6] In `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs`, write failing test `GoldenRefusalTests.OtherOpenRefusalsHappenBeforeStepZero(string caseName)` (US6.5).
  - `rules-revision`, opened with `["tiny-world"]`: `CoreFormatException` with `RulesRevisionMismatch`, offset 8, field `rulesRevision`, and `Detail` exactly `rules revision 0 in the stream; this core implements rules revision 1`. Open step 2 of `StreamRunner` is Phase 4's.
  - `config-invalid`, opened with `["tiny-world"]`: `ConfigValidationException` whose errors are exactly one, `movement.gravity: must be finite`, equal to the JSON's `expectedConfigErrors`.
  - Both `.bin` files first decode with `RecordedStream.Decode`, because these are open-time refusals.
  - Confirm it fails.
- [ ] T216 [US6] In `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs`, write failing test `GoldenRefusalTests.MutationsReproduceEachRefusalFixture(string format, string caseName)` (FR-023), over `RefusalCases`:
  - the case's `base` names a valid case of the same format;
  - `GoldenMutations.Apply(format, GoldenFixtures.ReadBin(format, base), parsed.Mutations)` equals the case's `.bin` byte for byte;
  - cases with no mutations (`collision-missing`, `collision-other`) equal their base's bytes.

  Confirm red with `--filter "FullyQualifiedName~GoldenRefusalTests"`.
- [ ] T217 [US6] Write failing test `GoldenCommandTests.GoldenWriteReproducesTheCommittedFixtures` in `tests/MmoEngine.SimCore.Tests/Cli/GoldenCommandTests.cs` (US6-IT).
  - **Given** a copy of `simcore/golden/` in a unique `TempDirectories` directory. The committed files are read with `FileAccess.Read`.
  - **When** `SimcoreCli.Run("golden", "write", "--dir", <copy>)` runs, **then**:
    - exit 0, and stderr holds no Error-level event;
    - stdout is exactly 93 lines: one `<format>/<case>: unchanged, <length> bytes` line per case, formats in the order input, collision, stream, and cases in ordinal order within each format;
    - the first line is `input/aim-pitch-down: unchanged, 17 bytes`, and the lines include `collision/full-cell: unchanged, 3176 bytes`, `collision/tiny-world: unchanged, 188 bytes` and `stream/two-steps: unchanged, 950 bytes`;
    - the last line is `golden write: 92 cases, 0 written, 92 unchanged`;
    - every `.bin` in the copy is byte-identical to the committed one, and its last-write time is unchanged (it was not rewritten);
    - no other file appeared in the copy.
  - Confirm it fails.
- [ ] T218 [US6] In `tests/MmoEngine.SimCore.Tests/Cli/GoldenCommandTests.cs`, write failing tests:
  - `GoldenWriteRewritesOnlyChangedCases`. In a temp copy, delete `input/example.bin` and flip byte 20 of `collision/tiny-world.bin`. Then:
    - exit 0;
    - the lines `input/example: written, 17 bytes` and `collision/tiny-world: written, 188 bytes` appear in their ordinal places;
    - the total is `golden write: 92 cases, 2 written, 90 unchanged`;
    - both files equal the committed ones again;
    - no temporary file is left in either folder (`AtomicFile`).
  - `GoldenWriteRefusesAnInvalidCaseAndWritesNothing(string mutationName)`, an `InlineData` Theory over a fresh temp copy per row. Each row first deletes `input/example.bin` from its copy.

    | Row | Change to the copy |
    | --- | --- |
    | `invalid-json` | `input/neutral.json` replaced by `{` |
    | `format-mismatch` | `collision/tiny-world.json` with `"format": "input"` |
    | `base-not-valid` | `stream/truncated.json` with `"base": "checksum-mismatch"` |
    | `missing-base` | `stream/truncated.json` with `"base": "no-such-case"` |
    | `encoder-refuses` | `collision/heightfield-only.json` with `cellSize` `{ "bits": "0x3DCCCCCD", "value": 0.1 }`, so `CollisionFile.Encode` refuses `OutOfRange` at 24, `cellSize` |

    For every row:
    - exit 2;
    - stdout's last line starts with `golden write: refused, <format>/<case>.json: ` and names the changed file;
    - `input/example.bin` is not recreated;
    - every other `.bin` is byte-identical with an unchanged write time.
  - `GoldenWriteOfAMissingDirectoryExitsTwo`: `--dir` names a folder that does not exist under a temp root. Exit 2, the last stdout line starts with `golden write: refused, `, and nothing is created.

  Confirm red with `--filter "FullyQualifiedName~GoldenCommandTests"`.
- [ ] T219 [P] [US6] Write failing test `DecoderPropertyTests.TruncatedOrCorruptedFixturesNeverYieldAPartialResult(string format, string caseName)` in `tests/MmoEngine.SimCore.Tests/Properties/DecoderPropertyTests.cs` (FR-020, US6.3), over `GoldenFixtures.ValidCases`.
  - It uses CsCheck with a fixed seed constant `Seed` declared in the class, passed to `Sample`, and printed in the failure message, with 1,000 iterations per case.
  - Each iteration generates one of three changes to the valid `.bin`:
    - a truncation to a length in [0, length);
    - one to four byte flips (offset, non-zero XOR);
    - the same flips followed by a rewrite of the trailer as XxHash64 (collision and stream only), so the value checks are reached.
  - The decoder either:
    - returns a value whose re-encoding (`TryEncode`, `CollisionFile.Encode(world.Data)` or `stream.Encode()`) equals the mutated bytes; or
    - refuses: `TryDecode` false with `command == default`, or `CoreFormatException` from `CollisionFile.Decode` or `RecordedStream.Decode`.
  - Any other exception type (`IndexOutOfRangeException`, `ArgumentOutOfRangeException`, `OverflowException`, `OutOfMemoryException`, `NullReferenceException`) fails the property and prints the seed and the mutated bytes' length.
  - Confirm it fails.
- [ ] T220 [P] [US6] Write failing test `CollisionFileLimitTests.EachLimitIsRefusedAtItsFieldAndOffset(string caseName)` in `tests/MmoEngine.SimCore.Tests/Formats/CollisionFileLimitTests.cs` (FR-020, checks 5, 6, 8 and 9 of contracts/collision-data-format.md#check-order).
  - Rows not covered by a fixture are built in the test from `CollisionFile.Encode(TestWorlds.TinyWorld())`, or written byte by byte where `Encode` would refuse them. The trailer is rewritten with `XxHash64` in the test wherever a value check must be reached.
  - Each row asserts `Assert.Throws<CoreFormatException>` with exactly this `Code`, `Offset` and `Field`:

    | Row | Change | Code | Offset | Field |
    | --- | --- | --- | --- | --- |
    | `rows-too-few` | write 36 = 1 | `OutOfRange` | 36 | `rows` |
    | `columns-too-many` | write 32 = 4098 | `Oversized` | 32 | `columns` |
    | `rows-too-many` | write 36 = 4098 | `Oversized` | 36 | `rows` |
    | `directory-does-not-fit` | header 3 × 3 with 1 mesh, file and `fileLength` 60 bytes | `Inconsistent` | 8 | `fileLength` |
    | `vertex-count-too-few` | write 88 = 2 | `OutOfRange` | 88 | `meshes[0].vertexCount` |
    | `vertex-count-too-many` | write 88 = 1,048,577 | `Oversized` | 88 | `meshes[0].vertexCount` |
    | `triangle-count-zero` | write 92 = 0 | `OutOfRange` | 92 | `meshes[0].triangleCount` |
    | `triangle-count-too-many` | write 92 = 2,097,153 | `Oversized` | 92 | `meshes[0].triangleCount` |
    | `vertex-sum` | 2 × 2 heightfield, five directory entries each with `vertexCount` 2²⁰ and `triangleCount` 1, `fileLength` 132 | `Oversized` | 116 | `meshes[4].vertexCount` |
    | `triangle-sum` | 2 × 2 heightfield, three entries each with `vertexCount` 3 and `triangleCount` 2²¹, `fileLength` 108 | `Oversized` | 96 | `meshes[2].triangleCount` |
    | `origin-x-non-finite` | write 16 = `0x7F800000` | `NonFinite` | 16 | `originX` |
    | `origin-z-out-of-range` | write 20 = 9000 | `OutOfRange` | 20 | `originZ` |
    | `cell-size-too-small` | write 24 = 0.125 | `OutOfRange` | 24 | `cellSize` |
    | `cell-size-too-large` | write 24 = 65 | `OutOfRange` | 24 | `cellSize` |
    | `grid-cell-too-large` | write 28 = 65 | `OutOfRange` | 28 | `gridCellSize` |
    | `extent-z` | write 20 = 8191.5 | `OutOfRange` | 36 | `rows` |
    | `grid-cells-per-axis` | origin (−8192, −1), `cellSize` 4, `gridCellSize` 1, `columns` 4097, `rows` 2, no meshes (16,384 cells along X) | `OutOfRange` | 28 | `gridCellSize` |
    | `vertex-non-finite` | write 100 = NaN | `NonFinite` | 100 | `meshes[0].vertices[0].y` |
    | `mesh-id-not-ascending` | a two-mesh world from `TestWorlds`, with mesh 1's `meshId` set to mesh 0's | `Inconsistent` | mesh 1's `meshId` offset | `meshes[1].meshId` |
    | `registrations-over-limit` | origin (−2048, −2048), `cellSize` 4, `gridCellSize` 1, 1025 × 1025 heights, one mesh with vertices (−2048, 0, −2048), (2048, 0, −2048), (−2048, 0, 2048), (2048, 0, 2048) and triangles (0, 1, 2), (1, 3, 2). Each triangle overlaps all 4096 × 4096 = 2²⁴ cells, so the second one passes the total. | `OutOfRange` | the second triangle's first index (computed from the layout) | `grid.registrations` |
    | `file-too-long` | a 268,435,457-byte buffer holding `tiny-world`'s header with `fileLength` 268,435,457 | `Oversized` | 8 | `fileLength` |

  - Confirm it fails.
- [ ] T221 [P] [US6] Write failing test `RecordedStreamCheckTests.EachStructuralCheckIsRefusedAtItsFieldAndOffset(string caseName)` in `tests/MmoEngine.SimCore.Tests/Formats/RecordedStreamCheckTests.cs` (FR-020, checks 5, 8 and 12 of contracts/recorded-stream-format.md#decoding).
  - Rows are built from the committed `stream/two-steps.bin` or `stream/two-characters.bin`, with the trailer rewritten by `XxHash64` in the test. Offsets for `two-characters` are computed from its header (steps block at 64 + `configLength` + `initialStateLength`; command c's index at entry start + 5 + 18c).
  - Each row asserts `Assert.Throws<CoreFormatException>` with exactly this `Code`, `Offset` and `Field`:

    | Row | Change | Code | Offset | Field |
    | --- | --- | --- | --- | --- |
    | `file-too-long` | a 67,108,865-byte buffer holding `two-steps`' header with `fileLength` 67,108,865 | `Oversized` | 56 | `fileLength` |
    | `step-count-too-many` | write 16 = 1,048,577 | `Oversized` | 16 | `stepCount` |
    | `zero-characters` | write 14 = 0 | `OutOfRange` | 14 | `characterCount` |
    | `step-hz-above` | write 12 = 61 | `OutOfRange` | 12 | `stepHz` |
    | `step-hz-below` | write 12 = 19 | `OutOfRange` | 12 | `stepHz` |
    | `first-step-number` | write 898 = 2 | `Inconsistent` | 898 | `steps[0].step` |
    | `command-unknown-magic` | write 904 = `0x4A` | `UnknownMagic` | 904 | `steps[0].commands[0].input.formatId` |
    | `commands-not-ascending` | `two-characters` step 1 indices written as 1 then 0 | `Inconsistent` | the second index's offset | `steps[0].commands[1].characterIndex` |
    | `entry-past-block` | delete 4 bytes at 934; `stepsLength` 40; `fileLength` 946 | `Inconsistent` | 934 | `steps[1].expectedHash` |
    | `bytes-after-entries` | insert 4 zero bytes at 942; `stepsLength` 48; `fileLength` 954 | `Inconsistent` | 942 | `steps` |
    | `step-count-beyond-block` | write 16 = 1,048,576 | `Inconsistent` | 942 | `steps[2].step` |

  - Confirm it fails.
- [ ] T222 [P] [US6] Write failing test `HostileHeaderTests.HostileCountsAreRefusedWithoutACountSizedAllocation(string format, string caseName)` in `tests/MmoEngine.SimCore.Tests/Formats/HostileHeaderTests.cs` (FR-020: nothing is allocated before every count is bounded).
  - Rows:
    - `collision`, `huge-heightfield`: a 56-byte file with `columns` 4097, `rows` 4097, `meshCount` 4096 and `fileLength` 56. Expect `Inconsistent`, 8, `fileLength`.
    - `collision`, `huge-mesh`: a 2 × 2 heightfield with one directory entry (`vertexCount` 2²⁰, `triangleCount` 2²¹) and `fileLength` 84. Expect `Inconsistent`, 8, `fileLength`.
    - `stream`, `huge-blocks`: a 72-byte file with `configLength` 65,536, `initialStateLength` 1,048,576, `stepsLength` `0xFFFFFFFF`, `characterCount` 64, `stepCount` 2²⁰ and `fileLength` 72. Expect `Inconsistent`, 56, `fileLength`, from the checked u64 sum.
    - `stream`, `step-count-beyond-block`: `two-steps` with `stepCount` 1,048,576 and a fixed trailer. Expect `Inconsistent`, 942, `steps[2].step`.
  - Each row decodes once to warm up, then measures `GC.GetAllocatedBytesForCurrentThread()` around a second decode inside `try`/`catch (CoreFormatException)`.
  - It asserts that exact code, offset and field, and fewer than 65,536 bytes allocated. A count-sized buffer would be at least 8 MiB.
  - The method is synchronous, so the per-thread counter sees only its own work. The class needs no serial collection, and its XML comment says why.
  - Confirm it fails.
- [ ] T223 [P] [US6] Write failing tests `GoldenMutationsTests` in `tests/MmoEngine.SimCore.Tests/Formats/GoldenMutationsTests.cs` (contracts/input-command-format.md#shared-fixture-rules, Mutations table):
  - `EachOpEditsTheBytesAsTheContractSays(string op)`, a Theory over a base `00 01 02 03 04 05` (a 16-byte base for `fixChecksum`). Expected results:
    - `truncate 2` gives `00 01`;
    - `append "ff"` gives `… 05 ff`;
    - `write 1 "aabb"` gives `00 aa bb 03 04 05`;
    - `insert 2 "cc"` gives `00 01 cc 02 03 04 05`;
    - `delete 1 2` gives `00 03 04 05`;
    - `fixChecksum` on collision or stream bytes sets the last 8 bytes to `XxHash64.HashToUInt64(first 8)`, written little-endian.
  - `OpsApplyInOrder`: `write 0 "ff"` then `truncate 1` gives `ff`.
  - `BadArgumentsAreRefusedNamingTheMutation(string row)`: truncate past the length, write past the end, insert past the end, delete past the end, odd-length or non-hex `hex`, an unknown `op`, and `fixChecksum` with format `input` or on fewer than 8 bytes. Each throws `GoldenCaseException` whose message starts with `mutations[<i>]: ` for the failing mutation's index.

  Confirm it fails.
- [ ] T224 [P] [US6] Write failing tests `GoldenJsonTests` in `tests/MmoEngine.SimCore.Tests/Formats/GoldenJsonTests.cs`:
  - `ParsesTheContractExamples`:
    - the input `example` JSON of contracts/input-command-format.md#input-cases parses to an `InputValidCase` whose `Command` is `new InputCommand(1, 100, Buttons.Forward | Buttons.Fire, 16384, -1024, 0)`, validating as `None` with `lastSequence` 0 and `weaponSlots` 2;
    - the `refuse-version-2` JSON parses to a refusal case with base `neutral`, one `write` mutation at offset 1 with hex `02`, and `expectedError` (`UnsupportedVersion`, 1, `version`);
    - a stream refusal JSON with `"refusedAt": "open"`, `"collisionFixtures": ["tiny-world"]` and `expectedConfigErrors` `[{ "path": "movement.gravity", "message": "must be finite" }]` keeps all three.
  - `ParsesX16FloatObjectsAndBitStrings`:
    - `"fedcba9876543210"` gives 0xFEDCBA9876543210UL;
    - `{ "bits": "0x80000000", "value": -0.0 }` gives a float with bits 0x80000000;
    - `"0x1007"` gives `fieldId` 0x1007, and `"0x411CF5C3"` gives `valueBits` 0x411CF5C3.
  - `RefusesMalformedValuesNamingTheirPath(string json, string expectedMessage)`. Each row throws `GoldenCaseException` with exactly `<JSON path>: <problem>`:
    - an x16 with uppercase letters, 15 digits, or a `0x` prefix gives `$.matchSalt: must be 16 lowercase hex digits`;
    - float `bits` in lowercase or with 7 digits gives `$.heights[0].bits: must be 0x and 8 uppercase hex digits`;
    - a float `value` that does not parse to its bits (`{ "bits": "0x3F800000", "value": 2.0 }`) gives `$.heights[0].value: 2 does not have bits 0x3F800000`;
    - a `fieldId` with 3 digits;
    - an unknown `kind`;
    - a refusal with neither `expectedError` nor `expectedConfigErrors`;
    - `aimYaw` 65536.

  Confirm it fails.

### Implementation

- [ ] T225 [P] [US6] Create the fixture model `src/MmoEngine.SimCore.Server/Golden/GoldenCase.cs` (namespace `MmoEngine.SimCore.Server.Golden`), following contracts/input-command-format.md#shared-fixture-rules:
  - `GoldenKind { Valid, Refusal }`;
  - `GoldenMutation(string Op, long Offset, long Length, string? Hex)`;
  - `GoldenExpectedError(FormatErrorCode Code, long Offset, string Field)`;
  - `GoldenConfigEntry(ushort FieldId, string Path, uint ValueBits)`;
  - `GoldenValidate(uint LastSequence, int WeaponSlots, InputRejection Expected)`;
  - `GoldenCollisionDerived(int HeightfieldTriangleCount, int[] MeshTriangleIds, int[] DegenerateTriangleIds, int GridCellsX, int GridCellsZ, int[][] Cells)`;
  - abstract record `GoldenCase(string Format, int FormatVersion, string Case, GoldenKind Kind)`;
  - `InputValidCase(..., InputCommand Command, GoldenValidate Validate)`;
  - `CollisionValidCase(..., CollisionData Data, long FileLength, ulong Checksum, GoldenCollisionDerived Derived)`;
  - `StreamValidCase(..., RecordedStream Stream, GoldenConfigEntry[] ConfigEntries, long ConfigLength, long InitialStateLength, long StepsLength, long FileLength, ulong Checksum)`;
  - `GoldenRefusalCase(..., string Base, GoldenMutation[] Mutations, GoldenExpectedError? ExpectedError, string? RefusedAt, string[] CollisionFixtures, (string Path, string Message)[] ExpectedConfigErrors)`;
  - an `Encode()` on each valid case: `InputCodec.TryEncode` into 17 bytes, `CollisionFile.Encode(Data)` or `Stream.Encode()`. It lets `CoreFormatException` through;
  - `GoldenCaseException(string message)`.
- [ ] T226 [US6] Implement `GoldenMutations.Apply(string format, ReadOnlySpan<byte> baseBytes, IReadOnlyList<GoldenMutation> mutations)` returning `byte[]`, in `src/MmoEngine.SimCore.Server/Golden/GoldenMutations.cs`, after T225.
  - The ops are `truncate`, `append`, `write`, `insert`, `delete` and `fixChecksum`, applied in order. `fixChecksum` rewrites the 8-byte trailer as `XxHash64.HashToUInt64` (seed 0) of every byte before it, for `collision` and `stream` only.
  - Hex is parsed case-insensitively.
  - Bad arguments throw `GoldenCaseException` with a message starting `mutations[<i>]: `.
  - Makes T223 pass.
- [ ] T227 [US6] Implement `GoldenJson` in `src/MmoEngine.SimCore.Server/Golden/GoldenJson.cs`, with `public static GoldenCase Parse(string folderFormat, string fileName, ReadOnlySpan<byte> utf8Json)` over `JsonDocument`, after T225.
  - **Values.** An x16 is exactly `^[0-9a-f]{16}$`. A float object is `{bits, value}`: `bits` is authoritative and must match `^0x[0-9A-F]{8}$`, and `value`'s raw text must parse with `float.Parse(..., CultureInfo.InvariantCulture)` to exactly those bits. `fieldId` is 4 uppercase hex digits and `valueBits` 8, after `0x`. Integers must fit their field's width.
  - **Shapes.**
    - Input valid cases: `command` and `validate`.
    - Collision valid cases: top-level `originX`, `originZ`, `cellSize`, `gridCellSize`, `columns`, `rows`, `meshCount` (must equal the length of `meshes`), `heights`, and `meshes` (`meshId`, `vertices` as `[x, y, z]` float objects, flat `indices`), with `fileLength`, `checksum` and `derived`.
    - Stream valid cases: `rulesRevision`, `stepHz`, `characterCount`, `stepCount`, `seed`, `matchSalt`, `collisionChecksum`, `config` entries, `initialStateHex` and `steps` (`step`, `commands` with `characterIndex` plus the six command fields, `expectedHash`), with `configLength`, `initialStateLength`, `stepsLength`, `fileLength` and `checksum`. Its `RecordedStream` is built from the config entries written as a config block and read with `ConfigBlock`'s standalone decode, the state bytes and the steps, through the `RecordedStream` constructor from Phase 2.
    - Refusal cases: `base`, `mutations`, then `expectedError` or `expectedConfigErrors`, plus optional `refusedAt` (`"open"`) and `collisionFixtures`.
  - **Errors.** Every problem throws `GoldenCaseException("<JSON path>: <problem>")`.
  - Makes T224 pass.
- [ ] T228 [P] [US6] Complete `CollisionFile.Decode` in `src/MmoEngine.SimCore/Formats/CollisionFile.cs` and the counting pass in `src/MmoEngine.SimCore/Collision/UniformGrid.cs` with checks 5, 6, 8 and 9 of contracts/collision-data-format.md#check-order and #limits, using the contract's messages. Leave the order of checks 1–4 and 7 from Phase 2 unchanged.
  - **Check 5.** `fileLength` ≤ 268,435,456 (`Oversized`, 8). `columns` and `rows` in [2, 4097]: below gives `OutOfRange`, above gives `Oversized`, at 32 and 36. `meshCount` ≤ 4096 (`Oversized`, 40).
  - **Check 6**, all in checked `ulong` arithmetic:
    - D + 12M + 8 ≤ `fileLength`, else `Inconsistent`, 8, `fileLength`;
    - then each directory entry's `vertexCount` in [3, 2²⁰] and `triangleCount` in [1, 2²¹], and the running sums ≤ 2²²;
    - then the recomputed length equals `fileLength`, else `Inconsistent`, 8, `the counts need <n> bytes; fileLength is <m>`.
  - **Check 8**, values in file order:
    - `reserved` is 0;
    - `originX` and `originZ`;
    - `cellSize` in [0.25, 64] and `gridCellSize` in [1, 64];
    - the extent `originX + (float)(columns − 1)·cellSize` ≤ 8192, reported at 32 `columns` (Z at 36 `rows`);
    - grid cells per axis ⌈extent / `gridCellSize`⌉ ≤ 4096, reported at 28 `gridCellSize`;
    - heights finite and |h| ≤ 8192 (`heights[n] at offset <o>: NaN is not finite`);
    - `meshId` strictly ascending (`Inconsistent`);
    - vertices finite and within ±8192;
    - indices below `vertexCount` (`vertex index 4 is not below vertexCount 4`).
  - **Check 9.** The counting pass visits kept triangles in ID order and stops at the first triangle that brings a cell to 65 (`grid.cells[cx,cz]`: `65 triangles; the limit is 64`) or the total past 2²⁴ (`grid.registrations`). The decoder reports the file offset of that triangle's first index.
  - **No allocation, no partial result.** No array sized from a count is allocated before check 6 passes, and no `CollisionWorld` is constructed before every check passes.
  - Makes T220, the collision rows of T222, and (once the fixtures exist) the collision rows of T211 and T212 pass.
- [ ] T229 [P] [US6] Complete `RecordedStream.Decode` in `src/MmoEngine.SimCore/Formats/RecordedStream.cs` with checks 5, 6, 8, 10, the character-count half of 11, and 12 of contracts/recorded-stream-format.md#decoding. Leave the order of checks 1–4, 7, 9 and the byte-only half of 11 from Phase 2 unchanged.
  - **Check 5**, from the header only: `fileLength` ≤ 64 MiB (56), `configLength` ≤ 65,536 (44) and `initialStateLength` ≤ 1,048,576 (48) are `Oversized` above. `characterCount` in [1, 64] (14) and `stepCount` in [1, 2²⁰] (16) are `Oversized` above and `OutOfRange` at 0.
  - **Check 6.** 64 + `configLength` + `initialStateLength` + `stepsLength` + 8 equals `fileLength`, in checked `ulong`, else `Inconsistent`, 56, `the blocks need <n> bytes; fileLength is <m>`.
  - **Check 8.** `stepHz` in [20, 60] (`OutOfRange`, 12).
  - **Check 10.** `stepHz` equals the config's `stepRateHz` (`Inconsistent`, 12).
  - **Check 11, second half.** The initial state's `characterCount` equals the header's (`Inconsistent`, at the state's `characterCount` offset, field `initialState.characterCount`).
  - **Check 12**, each step entry in order:
    - `step` = S₀ + i + 1 (`Inconsistent`, `steps[i].step`);
    - `commandCount` ≤ `characterCount` (`Inconsistent`);
    - each `characterIndex` below `characterCount` (`OutOfRange`) and strictly ascending (`Inconsistent`);
    - each 17-byte input through `InputCodec.TryDecode`, whose error is re-reported with format `stream`, the file offset and the field `steps[i].commands[c].input.<field>`;
    - an entry that runs past the block is `Inconsistent` at the field being read;
    - bytes left after `stepCount` entries are `Inconsistent` at the first such byte with field `steps`.
  - **No allocation, no partial result.**
    - Checks 1 to 6 read only the header with `BinaryPrimitives`.
    - The step list grows with the entries actually present and is never pre-sized from `stepCount`.
    - No `RecordedStream` is constructed before every check passes.
  - Makes T221, the stream rows of T222, and (once the fixtures exist) the stream rows of the `GoldenRefusalTests` tasks pass. The committed `simcore/streams/*.mmrs` must still decode, because `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` stays green.
- [ ] T230 [US6] Implement `golden write [--dir simcore/golden]` in `src/MmoEngine.SimCore.Cli/Commands/GoldenCommand.cs` and register it in `src/MmoEngine.SimCore.Cli/Program.cs`, following steps 1–4 of contracts/simcore-cli.md#golden-write, after T226 and T227:
  - Read every `<case>.json` under `input/`, `collision/` and `stream/` in that format order, and in ordinal name order within each, with `GoldenJson.Parse`.
  - Refuse with exit 2 and write nothing when any of these happens: a missing `--dir`; invalid JSON; a `format` that differs from the folder; a refusal whose `base` is not a valid case of the same format; or an encoder that refuses a valid case (`CoreFormatException` from `CollisionFile.Encode` or `RecordedStream.Encode`). The stdout line is `golden write: refused, <format>/<case>.json: <problem>`, and an Error event is logged on stderr through `CliLogging`.
  - Compute every `.bin` in memory before writing anything. A valid case's `.bin` is its `Encode()`. A refusal case's `.bin` is `GoldenMutations.Apply` on its base's computed bytes.
  - Write only the files whose bytes differ from what is on disk, through `AtomicFile`.
  - Print one `<format>/<case>: written|unchanged, <n> bytes` line per case, then `golden write: <cases> cases, <w> written, <u> unchanged`, and exit 0.
  - Makes T218's `GoldenWriteRefusesAnInvalidCaseAndWritesNothing` (except the `encoder-refuses` row, which needs the collision fixtures) and `GoldenWriteOfAMissingDirectoryExitsTwo` pass.
- [ ] T231 [US6] Author the 38 input cases `simcore/golden/input/<case>.json` exactly as the tables of contracts/input-command-format.md#input-cases give them, after T230.
  - There are 29 valid cases, each with its `command` and its `validate` object `{ lastSequence, weaponSlots, expected }`. The defaults are sequence 1, client step 0, buttons 0, yaw 0, pitch 0, slot 0, `lastSequence` 0 and `weaponSlots` 2. `reject-sequence-repeat` has sequence 5 and `lastSequence` 5.
  - There are 9 refusal cases on base `neutral`, with their `mutations` and `expectedError`.
  - Write the `.bin` files with `dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden` and commit each `.json` together with its `.bin`. `example.bin` must equal `49 01 01 00 00 00 64 00 00 00 81 00 00 40 00 fc 00`.
  - Makes the input rows of T208, T209, the `GoldenRefusalTests` tasks and T219 pass.
- [ ] T232 [US6] Author the 22 collision cases `simcore/golden/collision/<case>.json` from contracts/collision-data-format.md#golden-fixtures.
  - `tiny-world` has the table's values. Its heights row 2 ends with `{ "bits": "0x80000000", "value": -0.0 }`, its mesh 0 has `meshId` 1 and the three index triples, `fileLength` 188 and `checksum` `9224e3fa3b3fca9c`. Its encoded bytes must equal the contract's hex dump.
  - `heightfield-only` is `tiny-world`'s heightfield with `meshCount` 0 and `fileLength` 92.
  - `extent-at-limit` is `tiny-world` with origin (8190, 8190).
  - `full-cell` is `tiny-world`'s heightfield plus one mesh (`meshId` 1) of 64 thin vertical triangles, side by side along X inside the single grid cell. Triangle t uses vertices 3t, 3t + 1 and 3t + 2: (x₀ₜ, 0, 0), (x₀ₜ + 0.01, 0, 0) and (x₀ₜ, 0.5, 0), with x₀ₜ = −0.5 + 0.015·t computed in `float`. That makes 192 vertices, vertices [96, 2400), indices [2400, 3168) and `fileLength` 3176. None of the triangles is degenerate under the data-model rule, and the JSON's bits are authoritative.
  - Each case carries its `derived` object, as `CollisionCasesHaveTheirDerivedData` lists it.
  - The 18 refusal cases carry their bases, the mutations exactly as the contract's table gives them (`crowded-cell`: the 36-byte vertex insert at 2400, the index triple (192, 193, 194) inserted at 3204, writes at 88, 92 and 8, then `fixChecksum`) and their `expectedError`.
  - A one-off script outside the repository may generate the long arrays. The committed `.json` is the source.
  - Run `golden write` and commit the `.json` and `.bin` files together.
  - Makes the collision rows of T208, T209, the `GoldenRefusalTests` tasks, T219 and the `encoder-refuses` row of T218 pass.
- [ ] T233 [US6] Author the 32 stream cases `simcore/golden/stream/<case>.json` from contracts/recorded-stream-format.md#golden-fixtures.
  - **`two-steps`.**
    - Header values: `rulesRevision` 1, `stepHz` 30, `characterCount` 1, `stepCount` 2, `seed` `0000000000000000`, `matchSalt` `0123456789abcdef`, `collisionChecksum` `9224e3fa3b3fca9c`.
    - `config`: the 116 entries of `simcore/config/default.json` as `{ fieldId, path, valueBits }` in ascending `fieldId`, with entry k at 66 + 6k and `movement.gravity` (`0x1007`) as entry 9.
    - `initialStateHex`: the 136-byte worked example of contracts/state-serialization.md#worked-example.
    - Step 1 has the `example` command for index 0 and `expectedHash` `0000000000000001`. Step 2 has no command and `0000000000000002`.
    - Lengths 698, 136, 44 and 950.
  - **`two-characters`.** Character IDs 1 and 4, built from the worked example with a second character. Step 1 has commands for indices 0 and 1, step 2 only for index 1.
  - **`rejected-command`.** Step 1's command has buttons `0xFE00`.
  - **`seeded`.** `seed` `0000000000000007` and `matchSalt` `fedcba9876543210`.
  - **The 28 refusal cases.** Base `two-steps`, with mutations exactly as the contract's table gives them.
    - The four open cases carry `"refusedAt": "open"` and `collisionFixtures`: `rules-revision` `["tiny-world"]`, `collision-missing` `[]`, `collision-other` `["heightfield-only"]`, `config-invalid` `["tiny-world"]`.
    - `config-invalid` has `expectedConfigErrors` `[{ "path": "movement.gravity", "message": "must be finite" }]` and no `expectedError`.
  - Run `golden write`, commit the `.json` and `.bin` files together, and do not touch `simcore/streams/`.
  - Makes the stream rows of T208, T209, the `GoldenRefusalTests` tasks (including T214 and T215), T219 and T221 pass.
- [ ] T234 [US6] Run the phase's checks.
  - `dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden` prints `golden write: 92 cases, 0 written, 92 unchanged`, and `git diff --exit-code simcore/golden` then exits 0.
  - `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Formats|FullyQualifiedName~GoldenCommandTests|FullyQualifiedName~DecoderPropertyTests|FullyQualifiedName~StreamsCommandTests"` passes. This also makes T217 and T218's `GoldenWriteRewritesOnlyChangedCases` green.
  - `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes` exit 0.
  - The coverage of `src/MmoEngine.SimCore.Server/Golden/*.cs`, `src/MmoEngine.SimCore.Cli/Commands/GoldenCommand.cs`, `src/MmoEngine.SimCore/Formats/CollisionFile.cs`, `src/MmoEngine.SimCore/Formats/RecordedStream.cs` and `src/MmoEngine.SimCore/Collision/UniformGrid.cs` in `dotnet test tests/MmoEngine.SimCore.Tests --collect:"XPlat Code Coverage"` is at least 95% lines. If it is not, add Theory rows to `CollisionFileLimitTests`, `RecordedStreamCheckTests`, `GoldenJsonTests` or `GoldenMutationsTests`.

**Checkpoint**: These checks are automated, and all must pass:
- the Independent Test filter;
- `FullyQualifiedName~MmoEngine.SimCore.Tests.Formats` and `FullyQualifiedName~DecoderPropertyTests`;
- `golden write --dir simcore/golden` reports `92 cases, 0 written, 92 unchanged`, and `git diff --exit-code simcore/golden` exits 0;
- `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` still passes over every committed stream, because the new decoder checks refuse none of them, and `git diff --name-only` lists nothing under `simcore/streams/`;
- `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes` exit 0.

---

## Phase 9: User Story 7 - Each build's cost is measured and recorded (Priority: P3), slice 1: server benchmarks and zero allocation

**Goal**: Add BenchmarkDotNet benchmarks for the server build's movement, weapon, projectile, hitbox-build and state-hash steps, with the measurement exporter and a P99 column. Prove the export with an in-process dry run, prove that warmed-up steps allocate nothing, and add `simcore measure validate-report`. The full benchmark run on the runner class and the WebAssembly measurements are Phase 12's.

**Independent Test**: `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Benchmarks|FullyQualifiedName~MmoEngine.SimCore.Tests.Allocation|FullyQualifiedName~ValidateReportTests|FullyQualifiedName~MeasurementReportValidatorTests"` passes. The dry run exports a valid `server` object labelled `measurement` with its runner class. Every step function allocates 0 bytes over 1,000 warmed-up steps at 20, 30 and 60 Hz. The contract's example report validates, and every mutation of it prints exactly its `path: problem` line.

### Tests (write first, confirm red)

> Red means the test fails or does not compile because its type is missing. Write each test, run its filter and see it fail before starting the matching Implementation task.

- [ ] T235 [US7] Add the report fixture and write failing tests `ValidateReportTests` in `tests/MmoEngine.SimCore.Tests/Cli/ValidateReportTests.cs`. These are US7.2's validator half; the scenario itself belongs to Phase 12's `measure` job.
  - The fixture `tests/MmoEngine.SimCore.Tests/Cli/Fixtures/measurement-report.json` is the Example of `specs/015-shared-simulation-core/contracts/measurement-report.md#example`, byte for byte. Add a `<None Update="Cli/Fixtures/measurement-report.json" CopyToOutputDirectory="PreserveNewest" />` item for it in `tests/MmoEngine.SimCore.Tests/MmoEngine.SimCore.Tests.csproj`.
  - Add `tests/MmoEngine.SimCore.Tests/Support/ReportMutations.cs`: `ReportMutations.Apply(string json, string mutation) -> string` over `System.Text.Json.Nodes.JsonNode`. A mutation is one or more operations joined by ` ; `. `set <path> <json>` replaces or adds the value; `remove <path>` deletes an object member or an array element. Paths use the validator's syntax: dot-separated member names, which may contain `-`, and `[i]` indexes. An unknown path throws `ArgumentException` naming it.
  - Every test writes its report into a unique directory from `Support/TempDirectories`. It runs the CLI in process with `SimcoreCli.Run("measure", "validate-report", <absolute path>)` and never writes under `simcore/`.
  - `TheContractExampleIsValid`: the unmutated fixture gives exit 0 and empty stdout.
  - `EachProblemIsReported(string mutation, string expectedLine)`: a Theory with InlineData, one row per mutation that contracts/measurement-report.md#tests lists. Each row gives exit 1, and stdout is exactly one line, `expectedLine`:
    - `remove machine.cpuModel` → `machine.cpuModel: is required`
    - `set extra 1` → `extra: is not a known field`
    - `set runnerClass "" ; set server.runnerClass ""` → `runnerClass: must not be empty`
    - `set server.runnerClass "other-runner"` → `server.runnerClass: is "other-runner"; must equal runnerClass "gha-ubuntu-latest-x64"`
    - `remove server.benchmarks[3]` → `server.benchmarks: has no weapon benchmark`
    - `remove wasm.modes.aot` → `wasm.modes: has no aot mode`
    - `remove wasm.modes.aot.reason` → `wasm.modes.aot.reason: is required when produced is false`
    - `set wasm.modes.interp-prebuilt.download.total.brotliBytes 1417661` → `wasm.modes.interp-prebuilt.download.total.brotliBytes: is 1417661; the files sum to 1417660`
    - `set wasm.modes.interp-prebuilt.startup.coldStarts 9` → `wasm.modes.interp-prebuilt.startup.coldStarts: is 9; must be 10`
    - `set wasm.modes.interp-prebuilt.startup.chromiumWorker.crossOriginIsolated false` → `wasm.modes.interp-prebuilt.startup.chromiumWorker.crossOriginIsolated: is false; must be true`
    - `remove wasm.modes.interp-prebuilt.steps.node[1]` → `wasm.modes.interp-prebuilt.steps.node: has no weapon entry`
    - `set wasm.modes.interp-relinked.startup.node.totalMs.p90 90.0` → `wasm.modes.interp-relinked.startup.node.totalMs.p90: is 90.0; must be at least the median 93.6`
  - `AnUnproducedModeNeedsOnlyItsReason(string mode)`: a Theory over rows `aot` and `interp-relinked`. Setting that mode to `{"produced": false, "reason": "not built on this machine"}` gives exit 0 and empty stdout.
  - `MalformedJsonIsInvalidAndAMissingFileCannotRun`:
    - A file holding `{"formatVersion": 1,` gives exit 1. Stdout is exactly one line that starts with `$: is not valid JSON (` and ends with `)`.
    - A path that does not exist, inside a fresh temp directory, gives exit 2. Stdout is empty and stderr contains that path.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ValidateReportTests"` and confirm it fails: there is no `measure` command yet.
- [ ] T236 [US7] Write failing tests `MeasurementReportValidatorTests` in `tests/MmoEngine.SimCore.Tests/Cli/MeasurementReportValidatorTests.cs`. They cover every other check of contracts/measurement-report.md#what-validate-report-checks, so `MeasurementReportValidator.cs` reaches 95% line coverage. They call `MmoEngine.SimCore.Cli.Measure.MeasurementReportValidator.Validate(string json)` directly on `ReportMutations.Apply(<fixture text>, mutation)`. This task depends on the fixture and `ReportMutations` from T235.
  - `EveryOtherCheckReportsItsLine(string mutation, string expectedLine)`: a Theory with InlineData. Each row returns exactly `[expectedLine]`:
    - `set formatVersion 2` → `formatVersion: is 2; must be 1`
    - `set formatVersion "1"` → `formatVersion: must be an integer`
    - `set kind "x"` → `kind: is "x"; must be "simcore-measurement"`
    - `set label "estimate"` → `label: is "estimate"; must be "measurement"`
    - `set recordedAt "2026-11-14 10:00:00"` → `recordedAt: is "2026-11-14 10:00:00"; must match YYYY-MM-DDTHH:MM:SSZ`
    - `set commit "ABC"` → `commit: is "ABC"; must match 40 lowercase hex digits or "unknown"`
    - `set machine.logicalCores 0` → `machine.logicalCores: is 0; must be at least 1`
    - `set machine.architecture "x86"` → `machine.architecture: is "x86"; must be "x64" or "arm64"`
    - `set dotnet.sdkVersion "10"` → `dotnet.sdkVersion: is "10"; must match <major>.<minor>.<patch>`
    - `remove dotnet.environment.DOTNET_TieredPGO` → `dotnet.environment.DOTNET_TieredPGO: is required`
    - `set node.version ""` → `node.version: must not be empty`
    - `set server.tool "Benchmark"` → `server.tool: is "Benchmark"; must match BenchmarkDotNet <version>`
    - `set server.label "estimate"` → `server.label: is "estimate"; must be "measurement"`
    - `set server.benchmarks[0].step "physics"` → `server.benchmarks[0].step: is "physics"; must be movement, weapon, projectile, hitboxBuild or stateHash`
    - `set server.benchmarks[1].case "flat-run"` → `server.benchmarks[1].case: "flat-run" is not unique within movement`
    - `set server.benchmarks[0].meanNs 0` → `server.benchmarks[0].meanNs: is 0; must be greater than 0`
    - `set server.benchmarks[0].iterations 0` → `server.benchmarks[0].iterations: is 0; must be at least 1`
    - `set server.benchmarks[0].allocatedBytesPerOp -1` → `server.benchmarks[0].allocatedBytesPerOp: is -1; must be at least 0`
    - `set wasm.modes.jit {"produced": false, "reason": "x"}` → `wasm.modes.jit: is not a known mode`
    - `set wasm.modes.aot.reason ""` → `wasm.modes.aot.reason: must not be empty`
    - `set wasm.modes.aot.build {"aot": true, "relinked": true, "workload": true}` → `wasm.modes.aot.build: is not a known field`
    - `set wasm.modes.interp-prebuilt.reason "x"` → `wasm.modes.interp-prebuilt.reason: must be null when produced is true`
    - `set wasm.modes.interp-prebuilt.build.relinked true` → `wasm.modes.interp-prebuilt.build.relinked: is true; must be false`
    - `set wasm.modes.interp-prebuilt.runtime.debugLevel 1` → `wasm.modes.interp-prebuilt.runtime.debugLevel: is 1; must be 0`
    - `set wasm.modes.interp-prebuilt.download.files[1].group "loader"` → `wasm.modes.interp-prebuilt.download.files: has 2 loader entries; must have exactly one, named dotnet.js`
    - `set wasm.modes.interp-prebuilt.download.files[8].name "MmoEngine.SimCore.a0eeztrxtm.wasm"` → `wasm.modes.interp-prebuilt.download.files[8].name: "MmoEngine.SimCore.a0eeztrxtm.wasm" is not unique`
    - `set wasm.modes.interp-prebuilt.download.files[6].group "fonts"` → `wasm.modes.interp-prebuilt.download.files[6].group: is "fonts"; must be loader, jsModuleNative, jsModuleRuntime, wasmNative, coreAssembly, assembly or icu`
    - `set wasm.modes.interp-prebuilt.download.total.files 8` → `wasm.modes.interp-prebuilt.download.total.files: is 8; there are 9 files`
    - `set wasm.modes.interp-prebuilt.startup.node.importMs.median 0` → `wasm.modes.interp-prebuilt.startup.node.importMs.median: is 0; must be greater than 0`
    - `set wasm.modes.interp-prebuilt.steps.node[0].batch 2` → `wasm.modes.interp-prebuilt.steps.node[0].batch: is 2; must be 1`
    - `set wasm.modes.interp-prebuilt.steps.chromiumWorker[0].steps 2999` → `wasm.modes.interp-prebuilt.steps.chromiumWorker[0].steps: is 2999; must be at least 3000`
    - `set wasm.modes.interp-prebuilt.steps.node[0].scenario "rifle-burst"` → `wasm.modes.interp-prebuilt.steps.node[0].scenario: is "rifle-burst"; must be "flat-run-30hz" for movement`
    - `set wasm.modes.interp-prebuilt.agreement {"build": "node-interp", "status": "Match"}` → `wasm.modes.interp-prebuilt.agreement: must be null for interp-prebuilt`
    - `set wasm.modes.interp-relinked.agreement null` → `wasm.modes.interp-relinked.agreement: is required when the mode was produced`
    - `set wasm.modes.interp-relinked.agreement.build "node-interp"` → `wasm.modes.interp-relinked.agreement.build: is "node-interp"; must be "node-interp-relinked"`
    - `set wasm.modes.interp-relinked.agreement.status "Ok"` → `wasm.modes.interp-relinked.agreement.status: is "Ok"; must be Match, Diverged or NotRun`
    - `set wasm.modes.interp-prebuilt {"produced": false, "reason": "x"} ; set wasm.modes.interp-relinked {"produced": false, "reason": "y"}` → `wasm.modes: no interpreted mode was produced`
  - `ARootThatIsNotAnObjectIsReported`: `[]` returns exactly `["$: must be an object"]`.
  - `UnknownCommitAndUnsetEnvironmentAreValid`: `set commit "unknown" ; set dotnet.environment.DOTNET_TieredPGO "unset"` returns no problem.
  - `ProblemsComeInDocumentOrder`: `set wasm.modes.interp-prebuilt.startup.coldStarts 9 ; set machine.logicalCores 0` returns exactly `["machine.logicalCores: is 0; must be at least 1", "wasm.modes.interp-prebuilt.startup.coldStarts: is 9; must be 10"]`, in that order.
  - `ServerObjectAloneIsCheckedAgainstTheGivenRunnerClass`: `ValidateServerObject(<fixture's server element>, "gha-ubuntu-latest-x64")` returns no problem. With `"other-runner"` it returns exactly `["server.runnerClass: is \"gha-ubuntu-latest-x64\"; must equal runnerClass \"other-runner\""]`.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MeasurementReportValidatorTests"` and confirm it fails: `MeasurementReportValidator` does not exist yet.
- [ ] T237 [P] [US7] Write failing tests `ServerBenchmarkExportTests` in `tests/MmoEngine.SimCore.Tests/Benchmarks/ServerBenchmarkExportTests.cs` (US7.1, FR-030). Also add `tests/MmoEngine.SimCore.Tests/Benchmarks/BenchmarkDryRunCollection.cs`: `[CollectionDefinition(Name, DisableParallelization = true)]`, with an XML doc comment giving the reason, written the way `tests/MmoEngine.Regionizer.Tests/Allocation/ZeroAllocationTests.cs` documents its collection. The reason: BenchmarkDotNet's in-process toolchain runs the benchmarks on the test process's own thread, times them, and raises the process and thread priority while it runs, so it must not share the process with parallel tests. The class is `[Collection(BenchmarkDryRunCollection.Name)]`.
  - `DryRunExportsAValidServerObject` (US7.1):
    - Given a unique artifacts directory from `TempDirectories`, it calls `BenchmarkRunner.Run([typeof(MovementBenchmarks), typeof(WeaponBenchmarks), typeof(ProjectileBenchmarks)], SimCoreBenchmarkConfig.Create("test-runner", Job.Dry.WithToolchain(InProcessEmitToolchain.Instance)).WithArtifactsPath(<dir>).WithOptions(ConfigOptions.DisableOptimizationsValidator))`.
    - The run produces exactly one summary, and `HasCriticalValidationErrors` is false. On failure, the test prints the validation errors.
    - `<dir>/results/simcore-server-benchmarks.json` exists, and its root object is the `server` object:
      - `tool` == `BenchmarkDotNet 0.15.8`, `label` == `measurement` and `runnerClass` == `test-runner`.
      - `benchmarks` holds the entries `movement`/`flat-run`, `movement`/`wall-slide`, `movement`/`corner-wedge`, `weapon`/`rifle-hold` and `projectile`/`launcher-flight`, in that order, and no others.
      - Every entry has `meanNs` > 0, `p99Ns` > 0, `iterations` >= 1 and `allocatedBytesPerOp` >= 0, an integer.
      - `MeasurementReportValidator.ValidateServerObject(root, "test-runner")` returns an empty list, which covers check 5 and every `server` field rule.
  - `ConfigCarriesTheExporterP99ColumnMemoryDiagnoserAndJoinedSummary`: `SimCoreBenchmarkConfig.Create("x", Job.Dry)` has:
    - exactly one exporter of type `SimCoreMeasurementExporter`;
    - a column whose `Id` is `P99` and `ColumnName` is `P99`;
    - `MemoryDiagnoser.Default` among its diagnosers;
    - `ConfigOptions.JoinSummary` set in `Options`;
    - exactly the one job passed.
  - `MainExitsTwoWithoutARunnerClass(string? runnerClass)`: a Theory with rows `null` and `""`. `MmoEngine.SimCore.Benchmarks.Program.Run([], runnerClass, stderr)` returns 2 without running a benchmark. `stderr` holds exactly the line `MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable`. The test never sets a process environment variable.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~ServerBenchmarkExportTests"` and confirm it fails: the benchmark project is not referenced yet.
- [ ] T238 [P] [US7] Write failing tests `StepCaseTests` in `tests/MmoEngine.SimCore.Tests/Benchmarks/StepCaseTests.cs`. They prove that the benchmark and allocation drivers step the real core, the same way the world does. Use the Theory `[MemberData(nameof(StepCaseTests.Cases))]`, with `Cases` built from `TestRates.WithRateLast(...)` over the eight `(kind, caseName)` pairs below, crossed with hz 20, 30 and 60:
  - `movement`/`flat-run` (the `flat-run-<hz>hz` definition)
  - `movement`/`wall-slide` (`wall-slide-45`)
  - `movement`/`corner-wedge` (`corner-wedge`)
  - `weapon`/`rifle-hold` (`rifle-burst`)
  - `projectile`/`launcher-flight` (`launcher-arc`)
  - `hitboxBuild`/`standing` (`flat-run-<hz>hz`)
  - `stateHash`/`one-character` (`flat-run-<hz>hz`)
  - `world`/`fuzz-all-buttons` (`fuzz-all-buttons`)

  The tests:
  - `ContentMatchesTheCommittedConfigAndWorld`:
    - `SimCoreContent.World.Checksum` equals the checksum of `simcore/content/test-world.mmcd`, read through `RepoPaths` with `FileAccess.Read` and decoded with `CollisionFile.Decode`.
    - `SimCoreContent.BaseConfig` bound and compiled at 30 Hz gives the same `CompiledConfig` bits as `TestConfigs.At(30)`.
  - `EachPassRunsItsStepsAndRestartRestoresIt(string kind, string caseName, int hz)`:
    - `StepCase.Create(kind, caseName, hz)` has `PassLength` >= 1.
    - After `PassLength` calls to `Step()`, `StepsLeftInPass` == 0, and one more `Step()` throws `InvalidOperationException`.
    - After `Restart()`, `StepsLeftInPass` == `PassLength`.
    - A second pass gives the same `LastResultHash` as the first. `LastResultHash` is the XxHash64 of the case's output state bytes after the pass.
  - `MovementCasesEndWhereTheWorldEnds(string caseName, int hz)`: the movement rows. After one pass, character 0's `Position`, `Velocity`, `Stance` and `Flags` bits equal those in the committed state of a `SimWorld` built from the same definition (`ScenarioCatalog.Get(name).Build(TestConfigs.At(hz) base, world, hz)`) and stepped for the same number of steps.
  - `WeaponCaseFiresTheWorldsShots(int hz)`: over one `rifle-hold` pass, the shot count and each shot's step equal the `SimWorld` shot events of `rifle-burst` at hz.
  - `ProjectileCaseEndsAtTheWorldsHitOrExpiry(int hz)`: the `launcher-flight` pass ends on the same step as the `SimWorld` run of `launcher-arc` at hz, with the same `HitKind` or `ExpiryReason`, the same projectile ID and the same point bits.
  - `WorldCaseHasNoFault(int hz)`: every step of a `fuzz-all-buttons` pass has a `StepReport.Fault.Code` of `None`, and its per-step hashes equal `StreamRunner.Run` of the same built stream.

  Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StepCaseTests"` and confirm it fails: `StepCase` and `SimCoreContent` do not exist yet.
- [ ] T239 [P] [US7] Write failing test `StepAllocationTests.WarmedUpStepsAllocateNothing(string step, int hz)` in `tests/MmoEngine.SimCore.Tests/Allocation/StepAllocationTests.cs` (US7.3, FR-032, SC-005). Also add `tests/MmoEngine.SimCore.Tests/Allocation/ZeroAllocationCollection.cs`: `[CollectionDefinition(Name, DisableParallelization = true)]` with `Name = "Zero allocation (per-thread allocation counters)"`. Its doc comment gives the reason as `tests/MmoEngine.Regionizer.Tests/Allocation/ZeroAllocationTests.cs` does: `GC.GetAllocatedBytesForCurrentThread` is a per-thread counter, so another test's JIT, type loading or continuations landing on this thread would be counted. The class is `[Collection(ZeroAllocationCollection.Name)]`.
  - Rows (MemberData through `TestRates.WithRateLast`): `step` is one of `movement/flat-run`, `movement/wall-slide`, `movement/corner-wedge` (all through `MovementStep.Step`), `weapon/rifle-hold` (through `WeaponStep.Step`), `projectile/launcher-flight` (through `ProjectileStep.Step`), `hitboxBuild/standing` (through `HitboxBuilder`) and `world/fuzz-all-buttons` (through `SimWorld.Step`). Each is crossed with hz 20, 30 and 60.
  - Given `StepCase.Create(kind, caseName, hz)` warmed up by two full passes, with `Restart()` between them:
    - When a further 1,000 `Step()` calls run, a pass that ends inside the window is restarted with `Restart()`, and the restart is excluded from the count. The count is taken with `GC.GetAllocatedBytesForCurrentThread()` before and after each run of `Step()` calls, and the differences are summed.
    - Then the sum is exactly 0. The failure message names the step, the hz and the byte count.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~StepAllocationTests"` and confirm it fails: `StepCase` does not exist yet.
- [ ] T240 [US7] Write failing zero-allocation tests for the remaining tick-path entry points (FR-032, SC-005). Each class is `[Collection(ZeroAllocationCollection.Name)]`, so this task depends on the collection from T239. Each call is warmed up once before counting, and every assertion is an exact 0 from `GC.GetAllocatedBytesForCurrentThread()`.
  - `tests/MmoEngine.SimCore.Tests/Allocation/InputAllocationTests.cs`, `InputAllocationTests.DecodeAndValidateAllocateNothing(string inputCase)`. Theory rows:
    - `valid`: the 17-byte example `49 01 01 00 00 00 64 00 00 00 81 00 00 40 00 fc 00` from contracts/input-command-format.md;
    - each of the six refusal inputs of that contract, in contract order;
    - each `InputRejection` code reached through `InputValidator.Validate(in command, lastSequence, 2)`, plus an accepted command.

    1,000 calls of `InputCodec.TryDecode` and `InputValidator.Validate` allocate 0 bytes.
  - `tests/MmoEngine.SimCore.Tests/Allocation/BatchAllocationTests.cs`, `BatchAllocationTests.StepQueuedAllocatesNothingAfterWarmUp(int batchSize, int hz)`. Rows: batch sizes {1, 16} crossed with {20, 30, 60}.
    - Setup: a `BatchStepper` loaded with the encoded test world, the config block of `TestConfigs.At(hz)` and the initial state of the `flat-run-<hz>hz` definition. Commands are encoded into `CommandBuffer` with `InputCodec.TryEncode`.
    - Warm-up: two batches.
    - Measured: `StepQueued(batchSize)` calls until 1,000 steps have run. When the definition's commands run out, `Reset` is called with the counter paused.
    - Assert: 0 bytes allocated.
  - `tests/MmoEngine.SimCore.Tests/Allocation/HashAllocationTests.cs`, `HashAllocationTests.StateHashAllocatesNothing(string stateCase)`. Rows: `worked-example` (`TestStates`' 136-byte state) and `max-length` (a buffer of `CompiledConfig.MaxStateLength` bytes). 1,000 `StateHash.Compute` calls allocate 0 bytes.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Allocation"`. Confirm the new tests fail to compile until `ZeroAllocationCollection` exists, and that any row that already runs is recorded with its result. A row that passes against Phase 2–7 code is allowed: these entry points were written allocation-free.

### Implementation

- [ ] T241 [US7] Create the benchmark project and its content loader, and reference it from the tests. This makes `StepCaseTests.ContentMatchesTheCommittedConfigAndWorld` compile.
  - `benchmarks/MmoEngine.SimCore.Benchmarks/MmoEngine.SimCore.Benchmarks.csproj`, shaped like `benchmarks/MmoEngine.Regionizer.Benchmarks/MmoEngine.Regionizer.Benchmarks.csproj`:
    - `OutputType` Exe and `IsPackable` false;
    - a `ProjectReference` to `src/MmoEngine.SimCore` and to `src/MmoEngine.SimCore.Server`;
    - `PackageReference Include="BenchmarkDotNet"`, with the version central (0.15.8, already in `Directory.Packages.props`);
    - `<EmbeddedResource Include="..\..\simcore\config\default.json" LogicalName="MmoEngine.SimCore.Benchmarks.default.json" />`.
  - Add the project to `MmoEngine.sln`, nested under the existing `benchmarks` solution folder. Add it with `dotnet sln MmoEngine.sln add --solution-folder benchmarks benchmarks/MmoEngine.SimCore.Benchmarks/MmoEngine.SimCore.Benchmarks.csproj`.
  - Add `<PackageReference Include="BenchmarkDotNet" />` and a `ProjectReference` to the benchmark project in `tests/MmoEngine.SimCore.Tests/MmoEngine.SimCore.Tests.csproj`.
  - `benchmarks/MmoEngine.SimCore.Benchmarks/SimCoreContent.cs`:
    - `public static class SimCoreContent`, with `BaseConfig` (the embedded `default.json` bound with `SimConfigJson`) and `World` (the CollisionData that `TestWorldGenerator` produces, put through `CollisionFile.Encode` then `CollisionFile.Decode`). Both are created once per process.
    - `Compiled(int hz)`, which returns `CompiledConfig.Create` of `BaseConfig` with `stepRateHz` = hz.
    - It reads no file from disk, so BenchmarkDotNet's out-of-process toolchain finds its content from any working directory.
- [ ] T242 [US7] Implement `benchmarks/MmoEngine.SimCore.Benchmarks/StepCases.cs`, after T241. Makes T238 and T239 pass, together with the allocation-fix task below.
  - Shape: `public sealed class StepCase` with `static Create(string kind, string caseName, int hz)`, `PassLength`, `StepsLeftInPass`, `Restart()`, `Step()` (throws `InvalidOperationException` when the pass has ended), `StepCycling()` (restarts first when the pass has ended; the benchmarks' operation) and `LastResultHash`. An unknown kind or case throws `ArgumentException` naming it.
  - Create builds `ScenarioCatalog.Get(<definition>).Build(SimCoreContent.BaseConfig, SimCoreContent.World, hz)`, using the definition mapping of `StepCaseTests`. It precomputes every input, state and buffer the pass needs at creation, so `Step()` and `Restart()` only copy structs and call the core:
    - movement: character 0's initial `CharacterState` decoded with `StateSerializer`'s reader, and the per-step `InputCommand` (or the neutral input). `Step()` calls `MovementStep.Step` with the arguments Phase 3 defines, against a candidate buffer of `World.MaxQueryCandidates`.
    - weapon: `rifle-burst`'s initial character and weapons. `Step()` calls `WeaponStep.Step` (Phase 6) with the step's input and a projectile scratch whose count is reset to 0 each step.
    - projectile: the canonical state of `launcher-arc` after its firing step, read from a `SimWorld` run at creation, and the hitboxes built once with `HitboxBuilder`. `Step()` calls `ProjectileStep.Step` (Phase 7). The pass ends when the projectile is removed.
    - hitboxBuild: one `flat-run-<hz>hz` pose per step, built into a preallocated span.
    - stateHash: `flat-run-<hz>hz`'s canonical state per step, hashed with `StateHash.Compute`.
    - world: a `SimWorld` with a `CharacterCommand[]` per step. `Restart()` calls `SimWorld.Reset(initialState, matchSalt)`.
- [ ] T243 [P] [US7] Implement `src/MmoEngine.SimCore.Cli/Measure/MeasurementReportValidator.cs`, following every check of contracts/measurement-report.md#what-validate-report-checks over the closed schema of #fields. Makes T236 pass.
  - API: `public static IReadOnlyList<string> Validate(string json)` and `public static IReadOnlyList<string> ValidateServerObject(JsonElement server, string runnerClass)`, both using `System.Text.Json.JsonDocument`.
  - Walk order: each object is walked in the file's member order. After its present members, each missing required member is reported in schema order. Cross-field checks are reported at the end of the object they belong to: runner-class equality, the step coverage of `server.benchmarks`, the mode set, at least one interpreted mode, the download totals and the step kinds.
  - Paths use dot-separated member names and `[i]` indexes. A value in a message is printed as its raw JSON text (`GetRawText`).
  - The message vocabulary is exactly that of the test rows:
    - `is required`, `is required when produced is false`, `is required when the mode was produced`
    - `is not a known field`, `is not a known mode`
    - `must be an integer`, `must be a number`, `must be a string`, `must be a boolean`, `must be an object`, `must be an array`, `must be a string or null`, `must be an object or null`
    - `must not be empty`
    - `is <v>; must be <alternatives>`, `is <v>; must match <format>`, `is <v>; must be at least <n>`, `is <v>; must be greater than 0`, `is <v>; must equal runnerClass <v>`, `is <v>; must be at least the median <v>`, `is <v>; the files sum to <n>`, `is <v>; there are <n> files`
    - `has no <kind> benchmark`, `has no <kind> entry`, `has no <mode> mode`, `has <n> loader entries; must have exactly one, named dotnet.js`
    - `<v> is not unique`, `<v> is not unique within <step>`
    - `must be null when produced is true`, `must be null for interp-prebuilt`, `no interpreted mode was produced`
    - `$: must be an object`, `$: is not valid JSON (<JsonException message>)`
  - Rules:
    - Runner-class emptiness is checked only at the top level. `server.runnerClass` is checked only for equality.
    - A missing mode is reported only by the mode-set check.
    - Integers use checked `long` sums.
    - There is no threshold on any number (Principle VI).
- [ ] T244 [US7] Implement `src/MmoEngine.SimCore.Cli/Commands/MeasureCommand.cs` and register it in `src/MmoEngine.SimCore.Cli/Program.cs`, following contracts/simcore-cli.md#measure-validate-report, after T243. Makes T235 pass.
  - The command is `measure validate-report <file>`.
  - Read the file with `FileAccess.Read`. If it is missing or unreadable, exit `CliExitCodes` 2 with nothing on stdout, and log on stderr through `CliLogging` an event naming the path.
  - Otherwise print `MeasurementReportValidator.Validate`'s lines on stdout, one per line, in order. Exit 0 when there are none, else 1. Malformed JSON is exit 1 with its `$: is not valid JSON (...)` line.
  - The command writes no file.
- [ ] T245 [US7] Implement the exporter, the column and the configuration in `benchmarks/MmoEngine.SimCore.Benchmarks/`, following contracts/measurement-report.md#how-the-report-is-produced, step 1, and the `server` table of #fields, after T241. Together with the next task, makes T237's `ConfigCarriesTheExporterP99ColumnMemoryDiagnoserAndJoinedSummary` and `DryRunExportsAValidServerObject` pass.
  - `P99Column.cs`: `IColumn`, with `Id` and `ColumnName` `P99`. Its value is `report.ResultStatistics.Percentiles.Percentile(99)`, shown in the summary's time unit.
  - `SimCoreMeasurementExporter.cs`: `IExporter`, constructed with the runner class. It writes `<summary.ResultsDirectoryPath>/simcore-server-benchmarks.json` with `Utf8JsonWriter`, through a temp file in the same directory and then a rename. The fields, in contract order:
    - `tool`: `BenchmarkDotNet <major.minor.patch>`, from the BenchmarkDotNet assembly's informational version with any `+<commit>` suffix removed;
    - `label`: `measurement`;
    - `runnerClass`;
    - `benchmarks[]` in step order movement, weapon, projectile, hitboxBuild, stateHash, then in each class's `Case` `[Params]` order. Each entry has:
      - `step`: the benchmark's single `[BenchmarkCategory]`;
      - `case`: its `Case` parameter value;
      - `meanNs`: `Statistics.Mean`;
      - `p99Ns`: `Statistics.Percentiles.Percentile(99)`;
      - `iterations`: `Statistics.N`;
      - `allocatedBytesPerOp`: `GcStats.GetBytesAllocatedPerOperation`.

    A benchmark without exactly one category or a `Case` parameter throws `InvalidOperationException` naming it. A report without statistics, meaning a failed benchmark, is left out, so `validate-report` names the missing step.
  - `SimCoreBenchmarkConfig.cs`: `public static IConfig Create(string runnerClass, Job job)`. It returns `ManualConfig.Create(DefaultConfig.Instance)` with `AddJob(job)`, `AddExporter(new SimCoreMeasurementExporter(runnerClass))`, `AddColumn(P99Column.Instance)`, `AddDiagnoser(MemoryDiagnoser.Default)` and `WithOptions(ConfigOptions.JoinSummary)`. Joining the summaries puts every benchmark class in one export file.
- [ ] T246 [US7] Implement the benchmarks and the entry point in `benchmarks/MmoEngine.SimCore.Benchmarks/`, after T242 and T245. Makes T237 pass, together with the tasks above.
  - Five benchmark classes. Each is public and not sealed, as BenchmarkDotNet's generated code requires. Each runs at 30 Hz, with a `[Params] public string Case` and a `[GlobalSetup]` that calls `StepCase.Create`. The `[Benchmark] public void Step()` calls `StepCycling()`. Each keeps a result field so the work is not eliminated.
    - `MovementBenchmarks.cs`: `[BenchmarkCategory("movement")]`, Case `flat-run`, `wall-slide`, `corner-wedge`.
    - `WeaponBenchmarks.cs`: `[BenchmarkCategory("weapon")]`, `rifle-hold`.
    - `ProjectileBenchmarks.cs`: `[BenchmarkCategory("projectile")]`, `launcher-flight`.
    - `HitboxBenchmarks.cs`: `[BenchmarkCategory("hitboxBuild")]`, `standing`.
    - `StateHashBenchmarks.cs`: `[BenchmarkCategory("stateHash")]`, `one-character`.
  - `Program.cs`: `Main(args)` returns `Run(args, Environment.GetEnvironmentVariable("MMO_RUNNER_CLASS"), Console.Error)`. `public static int Run(string[] args, string? runnerClass, TextWriter stderr)`:
    - returns 2 with the exact line `MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable` when `runnerClass` is null or empty;
    - otherwise runs `BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, SimCoreBenchmarkConfig.Create(runnerClass, Job.Default))`;
    - returns 1 when any summary has a critical validation error or a report without statistics, else 0.
  - Must not create `tools/agreement/lib/measure.mjs`, the `measure` job or any WebAssembly measurement.
- [ ] T247 [US7] Make every zero-allocation row green. Makes T239 and T240 pass.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Allocation"`.
  - Fix each row that reports bytes in the core file that allocates, keeping its phase's rules: no LINQ, lambdas, boxing, `params` arrays, string formatting or per-step `new` on the tick path, and every buffer owned by the world and sized from the `CompiledConfig` maxima. The candidate files are `src/MmoEngine.SimCore/Movement/MovementStep.cs`, `src/MmoEngine.SimCore/Weapons/WeaponStep.cs`, `src/MmoEngine.SimCore/Projectiles/ProjectileStep.cs`, `src/MmoEngine.SimCore/Hitboxes/HitboxBuilder.cs`, `src/MmoEngine.SimCore/World/SimWorld.cs`, `src/MmoEngine.SimCore/Batch/BatchStepper.cs`, `src/MmoEngine.SimCore/Inputs/InputCodec.cs`, `src/MmoEngine.SimCore/Inputs/InputValidator.cs` and `src/MmoEngine.SimCore/Hashing/StateHash.cs`. If an allocation is in `StepCases.cs` itself, fix it there.
  - Never lower the warm-up or step count, and never exclude a step from the count.
  - A fix must not change any committed stream's hashes: `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify` still prints `18 streams, 18 match`, with `0 diverged, 0 not comparable, 0 refused`.
  - Not `[P]`: it may edit `SimWorld.cs`.
- [ ] T248 [US7] Run the slice-1 gates on this phase's changes, and add tests where a gate fails:
  - `dotnet build MmoEngine.sln -warnaserror`, with the new benchmark project in the solution;
  - `dotnet format MmoEngine.sln --verify-no-changes`;
  - `dotnet test MmoEngine.sln --collect:"XPlat Code Coverage" --results-directory coverage`;
  - `python3 tools/coverage_gate.py --base origin/main --coverage-dir coverage`.

  `src/MmoEngine.SimCore.Cli/Commands/MeasureCommand.cs`, `src/MmoEngine.SimCore.Cli/Measure/MeasurementReportValidator.cs` and the changed `src/MmoEngine.SimCore.Cli/Program.cs` must each reach at least 95% line coverage and appear in the coverage report. Add rows to `tests/MmoEngine.SimCore.Tests/Cli/MeasurementReportValidatorTests.cs` for any uncovered validator line. `benchmarks/` is outside the gate.

**Checkpoint**: These commands exit 0, and coverage_gate.py lists every changed `src/*.cs` at 95% or above:

- `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Benchmarks|FullyQualifiedName~MmoEngine.SimCore.Tests.Allocation|FullyQualifiedName~ValidateReportTests|FullyQualifiedName~MeasurementReportValidatorTests"`
- `dotnet build MmoEngine.sln -warnaserror`
- `dotnet format MmoEngine.sln --verify-no-changes`
- `dotnet run --project src/MmoEngine.SimCore.Cli -- measure validate-report tests/MmoEngine.SimCore.Tests/Cli/Fixtures/measurement-report.json`
- `python3 tools/coverage_gate.py --base origin/main --coverage-dir coverage`

---

## Phase 10: Polish and PR, slice 1

**Goal**: Close slice 1. Every automated gate of `.github/workflows/dotnet.yml` passes locally, the committed content, fixtures and streams reproduce, `simcore/README.md` and the `docs/patch-notes.md` entry are written, `spec.md` and `tasks.md` match the work performed, the spec-test review has run, and the PR from `015-shared-simulation-core` is opened as a draft and marked ready once `.routines/merge-policy.md` rules 1-4 and 6 hold. Rule 5 does not apply because slice 1 touches no guarded path, so `.github/workflows/auto-merge.yml` squash-merges it.

**Independent Test**: Run these locally with `COV="$(mktemp -d)"`. Each must exit 0: `dotnet build MmoEngine.sln -warnaserror`, `dotnet format MmoEngine.sln --verify-no-changes`, `dotnet test MmoEngine.sln --collect:"XPlat Code Coverage" --results-directory "$COV"` and `python3 tools/coverage_gate.py --base origin/main --coverage-dir "$COV"`. After `simcore content generate`, `simcore golden write` and `simcore streams verify`, `git diff --exit-code simcore/` is clean; `streams verify` must print `streams verify: 18 streams, 18 match, 0 diverged, 0 not comparable, 0 refused`. `git diff --name-only origin/main...HEAD` matches none of the `GUARDED_PATHS` patterns in `.github/workflows/auto-merge.yml`.

### Tests (write first, confirm red)

- [ ] T249 [P] Write failing test `QuickstartTests.EverySliceOneQuickstartCommandParses()` in `tests/MmoEngine.SimCore.Tests/Cli/QuickstartTests.cs` (namespace `MmoEngine.SimCore.Tests.Cli`, one class per file). The test checks the quickstart's slice-1 commands against the CLI.
  - **Given**: `specs/015-shared-simulation-core/quickstart.md`, opened read-only through `RepoPaths` (`FileAccess.Read`).
  - **When**: The test collects every line that starts with `dotnet run --project src/MmoEngine.SimCore.Cli` inside five `##` sections: `Check a configuration file`, `Reproduce the test world`, `Rewrite the golden fixtures`, `Verify the committed streams` and `Regenerate the committed streams`. It splits each line after ` -- ` into arguments. A double-quoted string stays one argument with its quotes removed, and a trailing `# ...` comment is dropped.
  - **Then, line count**: Exactly 7 lines are found, in this order: `config check simcore/config/default.json`, `content generate --out simcore/content/test-world.mmcd`, `golden write --dir simcore/golden`, `streams verify`, `streams record --all`, `streams record --all --reason "Explain why the expected hashes change"`, `streams verify`. A count other than 7 fails, so an extractor that finds nothing cannot pass.
  - **Then, parsing**: Every line except the documented refusal parses against `Program.CreateRootCommand()` with `ParseResult.Errors` empty. Each resolves to the command its first two words name: `ParseResult.CommandResult.Command.Name` is `check`, `generate`, `write`, `verify` or `record`, and its parent command's name is `config`, `content`, `golden` or `streams`.
  - **Then, refusal**: The documented refusal is `streams record --all`, the line whose comment reads `# 2: --reason is required`. Run it through `SimcoreCli.Run` with `--out <an empty directory from TempDirectories>` appended. It returns exit 2, and the temp directory is still empty afterwards (contracts/simcore-cli.md#streams-record step 1).
  - **Excluded**: The `Run the agreement suite` and `The measurement report` sections. They need slice 2's WebAssembly publish and `tools/agreement/`; Phase 13 covers them.
  - **Red**: Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~QuickstartTests.EverySliceOneQuickstartCommandParses"`. It fails: it does not compile until `Program.CreateRootCommand()` exists.

### Implementation

- [ ] T250 Extract `internal static RootCommand CreateRootCommand()` in `src/MmoEngine.SimCore.Cli/Program.cs`. It builds the System.CommandLine tree that `Program.Run(string[] args, TextWriter stdout, TextWriter stderr)` builds today, and `Run` calls it. Exit codes, the environment check and logging stay unchanged.
  - If a quickstart line still fails to parse, compare it with contracts/simcore-cli.md#settings-output-and-exit-codes. Correct whichever side differs from the contract: the command definition in `src/MmoEngine.SimCore.Cli/Commands/<Name>Command.cs`, or the line in `specs/015-shared-simulation-core/quickstart.md`.
  - Makes T249 pass, and keeps every `tests/MmoEngine.SimCore.Tests/Cli/*Tests.cs` test green.
- [ ] T251 [P] Write `simcore/README.md`. One section per folder says what it holds and which command writes it:
  - `config/`: `default.json`, the configuration example of contracts/config-rules.md, hand-edited and checked by `simcore config check`.
  - `content/`: `test-world.mmcd`, written only by `simcore content generate` (contracts/collision-data-format.md).
  - `streams/`: the 18 named `.mmrs` streams and `REGENERATIONS.md`. Both are written only by `simcore streams record --reason`, and the regeneration rule says that expected hashes change only through that command, committed together with its entry (FR-028). `simcore streams verify` checks them.
  - `golden/`: the `input/`, `collision/` and `stream/` cases. Each `.json` is hand-written; each `.bin` is written only by `simcore golden write` (FR-023).
  - Tests open everything here read-only. Tests of the writing commands run on copies in a temporary directory. `.gitattributes` marks `*.mmcd`, `*.mmrs` and `simcore/golden/**/*.bin` as binary.
  - Every command is given as `dotnet run --project src/MmoEngine.SimCore.Cli -- <command>`, exactly as quickstart.md writes it, so the command forms stay identical.
- [ ] T252 Check that the committed content reproduces. Run these from the repository root, in order:
  - `dotnet run --project src/MmoEngine.SimCore.Cli -- content generate --out simcore/content/test-world.mmcd`. It prints one `simcore/content/test-world.mmcd: <bytes> bytes, checksum <x16>` line.
  - `dotnet run --project src/MmoEngine.SimCore.Cli -- golden write --dir simcore/golden`. Every case line ends `unchanged`, and the total line ends `0 written, <n> unchanged`.
  - `dotnet run --project src/MmoEngine.SimCore.Cli -- streams record --all --reason "Slice 1 polish: confirm no stream changes"`. It prints 18 `unchanged` lines and `streams record: 18 streams, 0 changed, 0 added, 0 removed, 18 unchanged`, and it does not touch `REGENERATIONS.md`.
  - `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify`. It prints 18 `<name>: match, <steps> steps, final <x16>` lines and `streams verify: 18 streams, 18 match, 0 diverged, 0 not comparable, 0 refused`, and exits 0.
  - `dotnet run --project src/MmoEngine.SimCore.Cli -- config check simcore/config/default.json`. It prints nothing and exits 0.
  - Then `git status --porcelain simcore/` must be empty and `git diff --exit-code simcore/` must exit 0. If any file changed, find the nondeterminism or stale file in the phase file that owns it (Phase 3 world, Phase 7 fire streams, Phase 8 fixtures) and fix it there. Never commit a regenerated file to make this pass.
- [ ] T253 Run every dotnet.yml gate locally, with the same commands and flags as `.github/workflows/dotnet.yml`:
  - `dotnet restore MmoEngine.sln`
  - `dotnet build MmoEngine.sln -warnaserror --no-restore`
  - `dotnet format MmoEngine.sln --verify-no-changes --no-restore`
  - `COV="$(mktemp -d)"; dotnet test MmoEngine.sln --no-build --collect:"XPlat Code Coverage" --results-directory "$COV"`
  - `python3 -m unittest discover -s tests/workflows -p "test_coverage_gate.py" -v`
  - `python3 tools/coverage_gate.py --base origin/main --coverage-dir "$COV"`

  Also run `python3 -m unittest discover -s tests/workflows -v`; slice 1 must not break the existing workflow tests. Use a temporary results directory, so no `coverage/` folder is left in the working tree. Record each command and its exit code for the PR description.
- [ ] T254 Top up coverage from T253's gate table. For every `src/MmoEngine.SimCore*/**/*.cs` row below 95.00% or marked `not in any coverage report`:
  - Add the missing tests in the folder that owns the file. Core, Server and Cli files get tests in `tests/MmoEngine.SimCore.Tests/<Folder>/<Subject>Tests.cs`; analyzer files get tests in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs`, `DescriptorTests.cs` or `WiringTests.cs`.
  - The new tests are Theories over the uncovered branches, with exact asserted values: codes, offsets, field paths, messages and exit codes.
  - Never add `[ExcludeFromCodeCoverage]`, a skip or a `.runsettings` exclusion.
  - Rerun `dotnet test` with coverage and `python3 tools/coverage_gate.py --base origin/main --coverage-dir "$COV"` until it exits 0 with every changed `src/*.cs` file listed and at or above 95%.
- [ ] T255 Audit the slice-1 tests with commands, fixing every hit in the file it names:
  - `grep -rnE "Skip *=|Assert\.Skip|SkipUnless|SkipWhen" tests/MmoEngine.SimCore.Tests tests/MmoEngine.SimCore.Analyzers.Tests --include=*.cs` lists only `tests/MmoEngine.SimCore.Tests/Math/ExhaustiveSweepTests.cs`.
  - `grep -rnE "new Random\(|Random\.Shared|System\.Random" src/MmoEngine.SimCore* tests/MmoEngine.SimCore* benchmarks/MmoEngine.SimCore* --include=*.cs` lists only the analyzer's violating samples in `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs`.
  - Every file under `tests/MmoEngine.SimCore.Tests/Properties/` that calls CsCheck's `Sample` declares a `const` seed and passes it, so a failure prints its seed.
  - `grep -rln "Path.GetTempPath" tests/MmoEngine.SimCore.Tests` lists only `Support/TempDirectories.cs`.
  - The only `[Collection]` definitions with `DisableParallelization = true` are `ZeroAllocationCollection` and the benchmark dry-run collection (slice 2 adds the `SimCoreExportsTests` one). Each has a comment giving its reason.
  - Run `dotnet test tests/MmoEngine.SimCore.Tests` and `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests` twice in a row. Both runs pass, and `git status --porcelain simcore/ specs/` is empty after each, so the suites are rerun-safe and never write under `simcore/`.
- [ ] T256 Check scope and licenses with commands:
  - **Guarded paths**: `git diff --name-only --no-renames origin/main...HEAD | grep -E '^\.github/|^\.routines/|^\.claude/|^\.specify/memory/constitution\.md$|(^|/)CLAUDE\.md$|^docs/architecture/server-architecture\.md$|^infra/'` prints nothing and exits 1. These are the `GUARDED_PATHS` of `.github/workflows/auto-merge.yml`.
  - **Packages**: `git diff origin/main...HEAD -- Directory.Packages.props` adds exactly two `PackageVersion` lines, `Microsoft.CodeAnalysis.CSharp` 5.0.0 and `Basic.Reference.Assemblies.Net100` 1.8.14, under a comment citing `specs/015-shared-simulation-core/research.md` R5 and their MIT licenses. `research.md`'s "New dependencies" list names the same two for slice 1.
  - **No npm package**: `git diff --name-only origin/main...HEAD -- '*package.json' '*package-lock.json'` is empty.
  - **File count**: Record `git diff --name-only origin/main...HEAD | wc -l` for the merge task below.
- [ ] T257 Run the server benchmarks on this machine: `MMO_RUNNER_CLASS=<runner class naming this machine, e.g. claude-cloud-x64-<vCPU count>c> dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Benchmarks -- --filter '*'`.
  - It must exit 0. `BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json` must exist, with `label` `measurement` and that `runnerClass`. `BenchmarkDotNet.Artifacts/` is git-ignored and stays uncommitted.
  - Turn the JSON's `benchmarks[]` into a Markdown table: step, case, meanNs, p99Ns, iterations, allocatedBytesPerOp. Put the table in the scratchpad PR body file, headed with the runner class, CPU model (`lscpu` model name), core count and SDK version (`dotnet --version`). Label the numbers as measurements on that machine only, not targets (constitution, Performance tests). Every `allocatedBytesPerOp` must be 0. A non-zero value is a defect: fix it in the step function's owning file and add the case to `tests/MmoEngine.SimCore.Tests/Allocation/StepAllocationTests.cs`.
- [ ] T258 Bring the spec artifacts in line with the work performed:
  - **spec.md**: In `specs/015-shared-simulation-core/spec.md`, record each behaviour, message, value or name the implementation settled differently from the text. Set `**Status**` to `Slice 1 implemented; slice 2 (WebAssembly host, agreement runners, SimCore workflow, measure job) pending`.
  - **quickstart.md and contracts**: Apply the same corrections to `specs/015-shared-simulation-core/quickstart.md` and `specs/015-shared-simulation-core/contracts/*.md` wherever a built command, message or byte layout differs. The tests cite those documents as the contract. In particular, record in contracts/analyzer-rules.md#wiring that the operation registration includes `CompoundAssignment` (for float `%=`).
  - **tasks.md**: In `specs/015-shared-simulation-core/tasks.md`, check off every Phase 1-10 task that is done. Leave this phase's PR, review and merge tasks unchecked until they finish, and leave Phases 11-13 unchecked. Correct any test name in the Scenario coverage table that was renamed.
  - **Index check**: For each slice-1 scenario in the Scenario coverage table, the named `Class.Method` must appear in `dotnet test tests/MmoEngine.SimCore.Tests --list-tests` or `dotnet test tests/MmoEngine.SimCore.Analyzers.Tests --list-tests`. Check with a `grep -F` per name; every grep exits 0.
  - **Older specs**: Check `specs/013-regionizer-simulator/spec.md` and `specs/014-telemetry-test-harness/spec.md` for statements this feature contradicts. Examples are the known environment names, which `SimEnvironment` keeps equal to the simulator's, and the regionizer's regex banned-API scan, which stays. Update any superseded statement, and record which spec prevails and why in this feature's spec.md Assumptions.
- [ ] T259 Commit and push, then open the draft PR:
  - **Commit**: Commit the slice on `015-shared-simulation-core`. Messages end with the session's `Co-Authored-By:` and `Claude-Session:` lines. Push with `git push origin 015-shared-simulation-core`.
  - **PR body**: Write the body to a file in the session's scratchpad directory, never in the repository. It contains:
    - the commands run in this phase and their exit codes and summary lines (build, format, test, coverage gate with its table, workflow tests, the content, golden and streams reproductions, the guarded-path and package checks);
    - the benchmark table from T257;
    - the judgment calls made during implementation;
    - "No open decision is settled": open decision 3 (tick rate) and open decision 5 (hit detection) stay open, as spec.md says;
    - slice 2 as the follow-up (the WebAssembly host and its tests, `tools/agreement/`, `.github/workflows/simcore.yml` with its workflow test and the `auto-merge.yml` listener, the WebAssembly halves of US3.8 and US5.5, and the measure job for US7.2), with the slice-2 scenarios listed as deferred by plan.md Delivery;
    - the attribution lines as its last lines.

    The body contains no at-sign mention of the Claude workflow.
  - **Open**: Run `gh pr create --draft --base main --head 015-shared-simulation-core --title "015 shared simulation core, slice 1: core, analyzer, formats, simcore CLI and server-side agreement" --body-file <that file>`. Record the PR number.
- [ ] T260 Add the `docs/patch-notes.md` entry at the top, below the `<!-- Add the newest entry below this line. -->` marker, in the shape the file's header gives, after T259 (it needs the PR number):
  - **Heading**: `## <today's date> — Shared simulation core, slice 1: deterministic core, analyzer, formats and the simcore CLI`.
  - **Links**: `Spec: [specs/015-shared-simulation-core/spec.md](../specs/015-shared-simulation-core/spec.md) · PR: #<n>`.
  - **Behavior and contract changes**:
    - the new projects `src/MmoEngine.SimCore`, `src/MmoEngine.SimCore.Analyzers`, `src/MmoEngine.SimCore.Server` and `src/MmoEngine.SimCore.Cli` (`simcore`), with their tests and `benchmarks/MmoEngine.SimCore.Benchmarks`;
    - the three versioned formats (input command, collision data `.mmcd`, recorded stream `.mmrs`), the canonical state and its XxHash64;
    - analyzer rules MMO1001-MMO1009, attached to the core only, as errors that cannot be lowered;
    - the `simcore` commands and their exit codes;
    - the committed `simcore/` content: `default.json`, the test world, 18 streams, `REGENERATIONS.md` and the golden fixtures.
  - **Shared pieces to reuse**:
    - `SimWorld`;
    - `BatchStepper` (the API slice 2's WebAssembly host delegates to);
    - `StreamRunner`, with its one-ulp perturbation;
    - `ConfigGuard` and `SimConfigValidator`;
    - `CompiledConfig`;
    - the regeneration rule (`streams record --reason` is the only writer of `simcore/streams/`);
    - `golden write`;
    - the analyzer's attach pattern.
  - **Open follow-ups**:
    - slice 2: the WebAssembly host, the agreement runners, the SimCore workflow and its gating agreement job, and the measure job with the full benchmark run on the runner class;
    - moving the regionizer's regex banned-API scan onto the analyzer (plan.md).

  Commit and push it to the PR branch.
- [ ] T261 Run the `review-spec-tests` skill against PR #<n> in a fresh subagent, giving it only the PR link. It checks that every acceptance scenario and Independent Test in spec.md maps to a faithful test.
  - Treat the slice-2-owned scenarios as deferred by plan.md Delivery and list them in the PR description, not as blocking: US2.1, US2.2, US2.3, US2.7, US2-IT, US3.8b, US5.5b, US7.2, US7-IT, SC-001, SC-006, SC-007 and SC-004's bit half.
  - For every other finding, fix the test it names in its file under `tests/MmoEngine.SimCore.Tests/` or `tests/MmoEngine.SimCore.Analyzers.Tests/`, so that it asserts the scenario's exact Then. Never weaken an assertion or skip a test.
  - Rerun that test project and T253's coverage gate, push, and answer each finding on the PR (fixed, with the commit, or why it is not a problem).
- [ ] T262 Audit for human steps (Principle XIII) with commands:
  - `grep -n -i -E "manual|by hand|sign-off|sign off|approv|QA|stop and validate|reviewer must|ask the (owner|maintainer)"` over `specs/015-shared-simulation-core/tasks.md`, `specs/015-shared-simulation-core/spec.md`, `specs/015-shared-simulation-core/quickstart.md` and the PR body file. Every hit must describe system behaviour or an automated check, not a step a person performs. Reword any that does not.
  - Confirm with `gh pr view <n> --json body` that the final description contains no such step.
- [ ] T263 Mark the PR ready and see it merged under `.routines/merge-policy.md`. This depends on every task above.
  - **Wait for checks**: First wait for every check on the head commit to complete green with `gh pr checks <n> --watch`. That is the `.NET` workflow's `dotnet` job, plus `Workflow tests` if it ran. `auto-merge.yml` listens only to `Workflow tests` completions, so the PR is marked ready after `.NET` has passed and the `ready_for_review` event sees every check green.
  - **Confirm the rules**: No review thread is unresolved and no review requests changes (`gh pr view <n> --json reviewDecision,reviews`), and the description lists what was verified. Then run `gh pr ready <n>`.
  - **Check the outcome**: Read the `auto-merge` run's summary (`gh run list --workflow auto-merge.yml --branch 015-shared-simulation-core`, then `gh run view <id>`). If it squash-merged, the task is done.
  - **Over 300 files**: If the summary says the PR is manual only because `it changes 300 or more files, more than the compare API lists`, re-check rules 1-4 and 6 on the head commit. Rule 5 does not apply, since there is no guarded path. Then squash-merge it with `gh pr merge <n> --squash`, and say on the PR why the session merged it.
  - **Any other manual reason** (a failed check, a review requesting changes): Fix the cause on the branch and repeat this task.
  - Never add the `hold` label, skip a test or merge with a failing check.

**Checkpoint**: The checks in the Independent Test all exit 0, along with `QuickstartTests.EverySliceOneQuickstartCommandParses`, both workflow-test commands, the reproduction commands with `git diff --exit-code simcore/` clean, the guarded-path grep exiting 1, the `.NET` check on the PR's head commit, and the `review-spec-tests` subagent run with every non-deferred finding fixed. The PR is merged into `main` by `auto-merge.yml`, or by the session in the over-300-files case.

---

## Phase 11: User Story 2 - The browser build agrees with the server bit for bit (Priority: P1), slice 2: WebAssembly host, agreement runners and the SimCore workflow

**Goal**: On a new branch `015-shared-simulation-core-slice-2` cut from `main` after slice 1 merges, add the `[JSExport]` WebAssembly host (`src/MmoEngine.SimCore.Wasm`) and its x64 tests, the Node (`node:test`) and Playwright Worker agreement runners in `tools/agreement/` with the cross-build tables check, the `SimCore` workflow's gating `agreement` job with its workflow test, and the `auto-merge.yml` listener. Every committed and generated stream, and the cross-build tables, then agree bit for bit across the server, Node (jiterpreter on and off) and browser Workers on every core change. This phase owns US2.1, US2.2, US2.3, US2.7, US2-IT, the cross-build halves US3.8b and US5.5b, SC-001, the bit half of SC-004, SC-006, FR-024, FR-025 (the exports), the WebAssembly half of FR-026, and FR-027.

**Independent Test**: The `agreement` job of `.github/workflows/simcore.yml` on the PR publishes the prebuilt WebAssembly build, prepares 68 streams (18 named, 50 generated), runs `tools/agreement/node/agreement.test.mjs` as `node-interp` and `node-interp-nojit` and `tools/agreement/specs/worker.spec.mjs` in Chromium (and in Firefox and WebKit when they install), and `simcore agreement compare` exits 0 with `tables match` for every required build, all within `timeout-minutes: 10`. The perturbation test diverges at exactly its step. `python3 -m unittest discover -s tests/workflows -v` passes.

### Tests (write first, confirm red)

- [ ] T264 [US2] Cut the slice-2 branch and confirm the slice-1 baseline it builds on: `git fetch origin main && git switch -c 015-shared-simulation-core-slice-2 origin/main`. Skim the `docs/patch-notes.md` entries newer than slice 1's. Then check, as automated exit codes, that slice 1 is on `main`: `dotnet build MmoEngine.sln -warnaserror` exits 0, and `dotnet run --project src/MmoEngine.SimCore.Cli -- streams verify` prints `streams verify: 18 streams, 18 match, 0 diverged, 0 not comparable, 0 refused` and exits 0. Every later task in this phase depends on this one. Of the rest, only tasks marked [P] are independent of one another.
- [ ] T265 [P] [US2] Create the host's test project without the host. Add `tests/MmoEngine.SimCore.Wasm.Tests/MmoEngine.SimCore.Wasm.Tests.csproj`, with a direct `ProjectReference` to `..\..\src\MmoEngine.SimCore.Wasm\MmoEngine.SimCore.Wasm.csproj` (research R18: a direct reference, never a linked source file or `[ExcludeFromCodeCoverage]`) and the PackageReferences `xunit.v3`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` and `coverlet.collector`. Versions are central; `Directory.Build.props` already makes `*.Tests` an Exe on VSTest. Link `..\MmoEngine.SimCore.Tests\Support\RepoPaths.cs` as `Support\RepoPaths.cs` (`<Compile Include=… Link=…>`). Add the test project to `MmoEngine.sln` under the `tests` solution folder. Add these support files:
  - `Support/GlobalUsings.cs`: `global using Xunit;`.
  - `Support/SharedStepperCollection.cs`: `[CollectionDefinition(Name, DisableParallelization = true)]`. Its XML doc gives the reason, in the style of `tests/MmoEngine.Regionizer.Tests/Allocation/ZeroAllocationTests.cs`: every export shares the one static `BatchStepper` in `SimCoreExports`, so tests that load it must not interleave.
  - `Support/CommittedStreamInputs.cs`: reads `simcore/streams/<name>.mmrs` and `simcore/content/test-world.mmcd` read-only through `RepoPaths`. With core APIs only, it decodes the stream (`RecordedStream.Decode`) and exposes:
    - the stream bytes and the collision bytes;
    - the embedded config re-encoded as a config block (the bytes `agreement prepare` writes to `batch/<stream>/config.bin`);
    - the initial state;
    - the match salt;
    - for a batchable stream, its commands encoded 17 bytes each with `InputCodec.TryEncode`.

  Run `dotnet build tests/MmoEngine.SimCore.Wasm.Tests` and confirm it fails because the referenced host project does not exist.
- [ ] T266 [US2] Write failing tests in `tests/MmoEngine.SimCore.Wasm.Tests/SimCoreExportsTests.cs` (FR-024, FR-025, the host's coverage gate), after T265. The class is `[SupportedOSPlatform("browser")]` (CA1416, per contracts/wasm-api.md#marshalling-rules) and `[Collection(SharedStepperCollection.Name)]`. Every test loads the static stepper from scratch and calls the exports as plain methods on x64:
  - `LoadCallsReturnTheSteppersCodes`:
    - `LoadCollision(test-world bytes)` returns 0 and the report buffer's `u32` text length at offset 0 is 0.
    - `LoadCollision(the first 10 bytes)` returns 1, and the report text equals exactly the `Message` of the `CoreFormatException` that `CollisionFile.Decode` throws for the same bytes.
    - `LoadConfig(a 3-byte block)` returns 1.
    - `LoadConfig(flat-run-30hz's config block)` returns 0.
    - `LoadConfig` of the same block re-encoded with `movement.gravity` = NaN returns 2, with the text exactly `movement.gravity: must be finite`. The block is built as `BatchStepperTests.LoadCallsReturnTheirCodes` builds its ConfigInvalid row.
    - After that failed `LoadConfig`, `Reset(initial state, salt)` returns 3 (fail closed).
    - After both loads return 0, `Reset` returns 0, and `Reset(the initial state truncated by one byte)` returns 1.
  - `BuffersAreTheSteppersSegments`:
    - `CommandBuffer().Count` is 544 (32 × 17), and two calls return the same underlying array.
    - After `LoadConfig(default block)` returns 0, `ReportBuffer().Count` is 2,935,060 (max(65,536, 8 + 32 × 91,688 + 12 + 1,024), per contracts/wasm-api.md#report-buffer-layouts).
    - A second `LoadConfig` of the same block keeps the same array.
  - `StepQueuedDelegatesToTheStepper`:
    - `StepQueued(-1)` and `StepQueued(33)` return -2.
    - After a `LoadCollision` that returns 0, with no `Reset`, `StepQueued(1)` returns -1.
    - After loading and resetting with flat-run-30hz's inputs, `StepQueued(1)`, `StepQueued(16)` and `StepQueued(13)` return 1, 16 and 13. Each report header's `u32` count equals the returned number and its `u32` length equals the records' byte length. The concatenated records equal `StreamRunner.Run(flat-run-30hz, test-world)` byte for byte.
  - `ResetReinterpretsTheSignedSaltAsUnsigned`:
    - `Reset(rifle-burst's initial state, unchecked((long)0x9E3779B97F4A7C15UL))`, then rifle-burst's commands queued 32 at a time, gives records byte-equal to a fresh `new BatchStepper(32)` reset with the `ulong` `0x9E3779B97F4A7C15`.
    - With the stream's own salt, the records equal `StreamRunner.Run(rifle-burst)`.
    - With that salt + 1, they first differ at rifle-burst's first shot step.
  - `RunStreamReturnsTheServerRecords`:
    - For flat-run-30hz and fuzz-all-buttons, `RunStream(stream, collision, -1)` equals `StreamRunner.Run(stream, collision)`, and `RunStream(flat-run-30hz, collision, 15)` equals `StreamRunner.Run(…, perturbStep: 15)`.
    - A copy of flat-run-30hz with its version byte set to 2 throws a `CoreFormatException` whose `Message` contains `unsupported version 2; this reader supports version 1`.
  - `EvaluateTablesReturnsTheServerResults`:
    - `EvaluateTables(CrossBuildTables.BuildInputs(), default block)` equals `CrossBuildTables.Evaluate` of the same arguments byte for byte. Its length is 16 + 8·M + 100·P, with M and P read from the result header and P = 6,192.
    - Inputs with the magic changed to `MMXX` throw a `CoreFormatException` whose message starts `tables magic at offset 0:`.
  - `EntryPointReturnsZero`: `Program.Main()` returns 0.

  Run `dotnet test tests/MmoEngine.SimCore.Wasm.Tests --filter "FullyQualifiedName~SimCoreExportsTests"` and confirm it fails: the project does not compile without the host.
- [ ] T267 [P] [US2] Write failing `node:test` tests in `tools/agreement/node/tables.test.mjs` for `compareTables(inputs, serverResults, buildResults, buildName)` from `tools/agreement/lib/tables.mjs`. It returns `{ match, firstMismatch }` under the comparison rule of contracts/wasm-api.md#cross-build-tables. The fixtures come from `tools/agreement/node/fixtures/tables-fixture.mjs`, a deterministic in-memory builder of MMXT inputs and MMXR results with chosen rows set, so no binary is committed:
  - An identical pair gives `{ match: true, firstMismatch: null }`.
  - A math table of 41,208 rows, where row 41207 is Atan2 (function ID 8, a `0x3F000000`, b `0xBF800000`) with server r0 `0x402B6374` and build r0 `0x402B6375`, gives `match` false and `firstMismatch` exactly `tables: build chromium-worker, first mismatch at math row 41207: Atan2(a 0x3F000000, b 0xBF800000) r0 server 0x402B6374 build 0x402B6375`.
  - Identical math rows and 3,872 pose rows, where pose row 3871 is Crouched, yaw `0x4000`, at (4096.25, 37.5, −7000.125), with server torso A.x `0x45800266` and build `0x45800267` (float 7 of the row, byte offset 16 + 8·M + 100·3871 + 28), give exactly `tables: build node-aot, first mismatch at pose row 3871: Crouched yaw 0x4000 at (4096.25, 37.5, -7000.125) torso A.x server 0x45800266 build 0x45800267`.
  - A SinCos row differing only in r1 is reported with `math row <i>` and ` r1 server 0x… build 0x…`.
  - A math row with server r0 `0x7FC00000` and build r0 `0xFFC00001` matches.
  - Server NaN against build `0x00000000` does not match and names that row. So does server `0x3F800000` against build NaN.
  - Results whose header counts differ do not match, with a `firstMismatch` that starts `tables: build <name>, ` in `TablesComparison`'s header wording (src/MmoEngine.SimCore.Server/TablesComparison.cs).

  Run `node --test tools/agreement/node/tables.test.mjs` and confirm it fails, because `lib/tables.mjs` does not exist.
- [ ] T268 [P] [US2] Write failing `node:test` tests in `tools/agreement/node/server.test.mjs` for `startServer({ app, work })` from `tools/agreement/lib/server.mjs`, per contracts/wasm-api.md#served-files. Each test uses a fresh `fs.mkdtemp` app root holding `_framework/dotnet.js`, `x.wasm`, `x.json` and `x.dat`, and a fresh work root holding `manifest.json`. Each closes the server afterwards:
  - The server listens on `127.0.0.1` with a dynamic port.
  - `Content-Type` is `application/wasm` for `.wasm`, `text/javascript` for `/_framework/dotnet.js`, `/web/worker.mjs`, `/lib/runtime.mjs` and `/lib/tables.mjs`, `application/json` for `/work/manifest.json`, `text/html; charset=utf-8` for `/web/page.html`, and `application/octet-stream` for `.dat`.
  - Every response, a 404 and a 403 included, carries `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Embedder-Policy: require-corp` and `Cache-Control: no-store`, and no `Content-Encoding`. A served file's body equals the file's bytes.
  - `GET /_framework/%2e%2e/%2e%2e/etc/passwd` and `GET /work/..%2fmanifest.json` return 403.
  - `PUT /work/chromium-worker/flat-run-30hz.records`, `PUT /work/firefox-worker/x.error.txt`, `PUT /work/webkit-worker/tables.bin` and `PUT /work/chromium-worker/tables.error.txt` each return 204. The file holds exactly the body, and no temporary file is left in the folder.
  - These PUTs return 403 and write nothing: build `node-interp`, stream name `Bad_Name`, stream `tables` with `.records`, an extension other than the four, and a body of 64 MiB + 1 byte.
  - `GET /nothing` returns 404.

  Run `node --test tools/agreement/node/server.test.mjs` and confirm it fails, because `lib/server.mjs` does not exist.
- [ ] T269 [US2] Write the failing Node agreement runner `tools/agreement/node/agreement.test.mjs` with its per-stream tests (US2.1), per contracts/wasm-api.md#node:
  - At module top level, before any test, read `AGREEMENT_APP`, `AGREEMENT_WORK` and `AGREEMENT_BUILD`. Throw with a message naming the variable when one is missing, when a directory does not exist, or when the build is not one of `node-interp`, `node-interp-nojit`, `node-interp-relinked` or `node-aot`.
  - In `before`, boot one runtime with `bootRuntime(pathToFileURL(join(AGREEMENT_APP, '_framework', 'dotnet.js')).href, { jiterpreter: !build.endsWith('-nojit') })` from `../lib/runtime.mjs`, and create `AGREEMENT_WORK/<build>/`.
  - One `test('<stream name>')` per `manifest.streams` entry, in manifest order. It calls `RunStream(stream bytes, collision bytes, -1)`, writes `<build>/<name>.records`, and asserts the bytes equal `server/<name>.records`. A failure message names the first record step that differs, found with `firstDifferingStep` from `lib/runtime.mjs`. A thrown error is written to `<build>/<name>.error.txt` and fails the test.
  - In `after`, write `<build>/run.json` with `build`, `runtimeOptions`, `runtimeBuildInfo`, `node: process.version`, `startedAt` and `endedAt`.

  The agreement job then requires `simcore agreement compare` to print `node-interp: match, 68 streams, <steps> steps, tables match`, and the same for `node-interp-nojit`. Confirm red: `AGREEMENT_APP="$(mktemp -d)" AGREEMENT_WORK="$(mktemp -d)" AGREEMENT_BUILD=node-interp node --test tools/agreement/node/agreement.test.mjs` exits non-zero (no `dotnet.js`, no `lib/runtime.mjs`).
- [ ] T270 [US2] Add the test `'manifest holds the 50 generated seeds'` to `tools/agreement/node/agreement.test.mjs` (US2.3). It asserts:
  - `manifest.seeds` deep-equals `{ first: 1, last: 50 }`;
  - the `origin: 'generated'` entries are exactly `seed-001` to `seed-050` in order, with `seed` 1 to 50;
  - the `origin: 'committed'` entries number 18.

  Their per-stream tests (T269) then run every seed in both Node builds and the Worker, and compare reports zero divergent streams. Confirm it fails with T269's red command.
- [ ] T271 [US2] Add the test `'perturbation diverges at exactly its step'` to `tools/agreement/node/agreement.test.mjs` (US2-IT, SC-002 in WebAssembly). Take `p = manifest.perturbation` and assert:
  - `p.stream === 'flat-run-30hz'` and `p.field === 'characters[0].position.x'`;
  - `RunStream(stream, collision, p.step)` gives records byte-equal to the server's for every step below `p.step`;
  - `firstDifferingStep(server, perturbed) === p.step`;
  - in that record, the `u32` at state offset 16 (record offset 24, `characters[0].position.x` per contracts/state-serialization.md#character-record) equals the server's bits + 1, and the record's hash differs from the server's.

  Confirm it fails with T269's red command.
- [ ] T272 [US2] Add the test `'cross-build tables match the server'` to `tools/agreement/node/agreement.test.mjs` (US3.8b and US5.5b, the bit half of SC-004):
  - Call `EvaluateTables(tables/inputs.bin, tables/config.bin)` from `manifest.tables` and write the result to `<build>/tables.bin`. A thrown error is written to `<build>/tables.error.txt` and fails the test.
  - Assert `compareTables(inputs, server/tables.bin, result, build)` is `{ match: true, firstMismatch: null }`, so every in-domain math row matches bit for bit, NaN rows match any NaN, and every pose row matches bit for bit.
  - Assert the result header's pose row count is 6,192 and its length is 16 + 8·M + 100·6,192.

  Confirm it fails with T269's red command.
- [ ] T273 [US2] Add the supporting tests `'batch inputs through StepQueued equal RunStream'` (US2.6 in WebAssembly) and `'refusals throw their expected message'` (US2.5 and US6.5 in WebAssembly) to `tools/agreement/node/agreement.test.mjs`:
  - **Batch.** For each manifest stream whose `batch` is not null (flat-run-30hz, rifle-burst and launcher-arc):
    - `LoadCollision`, `LoadConfig` and `Reset(initialState, BigInt.asIntN(64, BigInt('0x' + matchSalt)))` each return 0.
    - Fetch `ReportBuffer()` again after `LoadConfig`, and dispose the old view.
    - Queue the commands in batches of 1, 3 and 16, cycling and clamped to what remains. Each `StepQueued(k)` returns k.
    - The concatenated `slice`d records equal `RunStream(stream, collision, -1)` byte for byte, and the batch count equals the number of commands.
  - **Refusals.** `manifest.refusals` has exactly `unsupported-version`, `rules-revision` and `collision-missing`, and each `RunStream(stream, collision, -1)` throws an `Error` whose message contains its `expected`: `unsupported version 2; this reader supports version 1`, `rulesRevision at offset 8` and `collisionChecksum at offset 36`.

  Confirm they fail with T269's red command.
- [ ] T274 [P] [US2] Create the runner package and write the failing Worker spec (US2.2, plus the Worker half of US3.8b, US5.5b and US2.6):
  - **Package.**
    - `tools/agreement/package.json`: `"private": true`, `"type": "module"`, and only `"devDependencies": { "@playwright/test": "1.63.0" }`, exact.
    - `tools/agreement/package-lock.json` from `npm install --save-exact` in `tools/agreement/` (lockfileVersion 3).
    - In the root `.gitignore`, `tools/agreement/test-results/` and `tools/agreement/playwright-report/`.
  - **Spec.** `tools/agreement/specs/worker.spec.mjs`:
    - At module top level, require `AGREEMENT_APP` and `AGREEMENT_WORK`. `beforeAll` starts `startServer` from `../lib/server.mjs`, and `afterAll` closes it.
    - The test `'every stream, batch and table matches the server in a dedicated Worker'` uses the build `${testInfo.project.name}-worker`. It collects console messages from the page and from every Worker (`page.on('worker', w => w.on('console', …))`), opens `/web/page.html?build=<build>`, and waits for `window.__agreement.done`.
  - **Assertions.**
    - Exactly one `booted` event, with `inWorker === true` and `crossOriginIsolated === true`.
    - The `stream` events' names deep-equal the manifest's stream names in order, each with `match === true` and `firstMismatchStep === null`.
    - No `error` and no `fatal` event.
    - The `batch` events' names deep-equal the manifest's batchable streams, each with `match === true`.
    - Exactly one `tables` event, with `match === true` and `firstMismatch === null`.
    - `done` has `streams === manifest.streams.length`, `mismatches === 0` and `errors === 0`.
    - `<work>/<build>/<stream>.records` exists for every stream, and `<work>/<build>/tables.bin` exists, so `agreement compare` can read them.
    - No console message contains `expected content type`.

  Run `cd tools/agreement && npm ci && npx playwright install chromium && AGREEMENT_APP="$(mktemp -d)" AGREEMENT_WORK="$(mktemp -d)" npx playwright test --project chromium` and confirm it fails (no `playwright.config.mjs`, no `lib/server.mjs`, no page).
- [ ] T275 [US2] Write the failing workflow test `tests/workflows/test_simcore_workflow.py`, class `TriggersAndPaths` (US2.7, FR-027).
  - Set-up, in the style of `tests/workflows/test_harness_workflow.py`:
    - a module docstring;
    - `ROOT`, with `WORKFLOW = yaml.safe_load(.github/workflows/simcore.yml)` and `TRIGGERS = WORKFLOW.get("on", WORKFLOW.get(True))`;
    - `AUTO_MERGE` from `.github/workflows/auto-merge.yml`;
    - `steps(job)` and `runs(job)` helpers.
  - `test_runs_on_pull_requests_and_main_for_simcore_paths`: for `pull_request` and `push`, `paths` contains every one of `src/MmoEngine.SimCore*/**`, `tests/MmoEngine.SimCore*/**`, `benchmarks/MmoEngine.SimCore*/**`, `tools/agreement/**`, `tools/simcore/**`, `simcore/**`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `MmoEngine.sln` and `.github/workflows/simcore.yml`, and `push.branches == ["main"]`.
  - `test_workflow_is_named_simcore`: `WORKFLOW["name"] == "SimCore"`, the name `auto-merge.yml` listens for.
  - `test_token_is_read_only`: `WORKFLOW["permissions"] == {"contents": "read"}`.

  Run `python3 -m unittest tests/workflows/test_simcore_workflow.py -v` and confirm it fails (`simcore.yml` is missing).
- [ ] T276 [US2] Add class `Pins` to `tests/workflows/test_simcore_workflow.py`:
  - `test_every_action_is_pinned_to_a_commit`: every `uses` in every job matches `^[\w.-]+/[\w.-]+@[0-9a-f]{40}$`.
  - `test_checkouts_do_not_persist_credentials`: every `actions/checkout@` step has `persist-credentials: false`.
  - `test_playwright_is_pinned_exactly`:
    - `tools/agreement/package.json` has `devDependencies == {"@playwright/test": "1.63.0"}` and no non-empty `dependencies`;
    - `package-lock.json` has `lockfileVersion == 3`, `packages[""]["devDependencies"]["@playwright/test"] == "1.63.0"` and `packages["node_modules/@playwright/test"]["version"] == "1.63.0"`.
  - `test_npm_ci_installs_from_the_lockfile`: a step runs `npm ci` with `working-directory: tools/agreement`, and no step runs `npm install`.
  - `test_toolchains_come_from_global_json_and_node_22`: `actions/setup-dotnet` has `global-json-file: global.json`, and `actions/setup-node` has `node-version: "22"`.

  Confirm it fails with the command above.
- [ ] T277 [US2] Add class `AgreementJob` to `tests/workflows/test_simcore_workflow.py` (US2.7, SC-001, SC-006):
  - `test_compare_gates_the_job` (US2.7):
    - exactly one step runs `agreement compare --work`, with `--summary "$GITHUB_STEP_SUMMARY"`;
    - it has no `continue-on-error`, and its `if` is `${{ !cancelled() }}`, so it reports even after a runner step failed;
    - it passes no `--require`, or exactly `--require node-interp,node-interp-nojit,chromium-worker`;
    - it comes after every runner step.
  - `test_agreement_job_times_out_after_ten_minutes` (SC-006): `jobs.agreement["timeout-minutes"] == 10`.
  - `test_prebuilt_wasm_is_published_before_any_workload`: a step runs `dotnet publish src/MmoEngine.SimCore.Wasm -c Release`, and no step of the job mentions `dotnet workload`.
  - `test_prepare_writes_fifty_seeds_to_a_fresh_work_directory` (SC-001): `agreement prepare --seeds 1-50 --work "$AGREEMENT_WORK"` runs after a step that appends `AGREEMENT_APP=$RUNNER_TEMP/…/wwwroot` and `AGREEMENT_WORK=$RUNNER_TEMP/…` to `$GITHUB_ENV`.
  - `test_runner_unit_tests_run_before_the_agreement`: a step runs `node --test tools/agreement/node/tables.test.mjs tools/agreement/node/server.test.mjs` before the first agreement run, without `continue-on-error`.
  - `test_node_runs_with_and_without_the_jiterpreter`: two steps run `node --test tools/agreement/node/agreement.test.mjs`, with `env.AGREEMENT_BUILD` `node-interp` and `node-interp-nojit`, and neither has `continue-on-error`.
  - `test_three_browser_projects_run_and_optional_ones_may_not_install`:
    - the `npx playwright install --with-deps chromium` step has no `continue-on-error`;
    - the firefox and webkit install steps each have an `id` and `continue-on-error: true`;
    - `npx playwright test --project chromium` runs unconditionally;
    - `--project firefox` and `--project webkit` run with `if` on their install step's `outcome == 'success'`, and none of the three test steps has `continue-on-error`;
    - `tools/agreement/playwright.config.mjs` names the projects `chromium`, `firefox` and `webkit`.
  - `test_runner_class_and_pinned_intrinsics_are_set`: workflow-level `env` has `MMO_RUNNER_CLASS == "gha-ubuntu-latest-x64"`, `DOTNET_EnableAVX512F == "0"` and `DOTNET_TieredPGO == "0"`.
  - `test_report_is_uploaded_when_the_job_fails`: an `actions/upload-artifact` step with `if: failure()` uploads a path ending `/report/`.
  - `test_steps_run_with_pipefail`: `WORKFLOW["defaults"]["run"]["shell"] == "bash"`.

  Confirm it fails with the command above.
- [ ] T278 [US2] Add class `AutoMerge` to `tests/workflows/test_simcore_workflow.py` (US2.7). `test_auto_merge_waits_for_simcore`: the `auto-merge.yml` `on.workflow_run.workflows` list (`AUTO_MERGE.get("on", AUTO_MERGE.get(True))`) contains both `"SimCore"` and `"Workflow tests"`, and `types == ["completed"]`. Confirm it fails with the command above (`"SimCore"` is absent).

### Implementation

- [ ] T279 [P] [US2] Implement the WebAssembly host (contracts/wasm-api.md#host-exports, research R15):
  - `src/MmoEngine.SimCore.Wasm/MmoEngine.SimCore.Wasm.csproj`: `Sdk="Microsoft.NET.Sdk.WebAssembly"`, `OutputType` `Exe`, `AllowUnsafeBlocks` `true` (CS0227 for the generated interop), and a `ProjectReference` to `..\MmoEngine.SimCore\MmoEngine.SimCore.csproj`. It has no analyzer reference: only the core attaches the analyzer, so Phase 5's `WiringTests.OnlyTheCoreAttachesTheAnalyzer` stays green. `InvariantGlobalization` comes from `Directory.Build.props`.
  - `src/MmoEngine.SimCore.Wasm/SimCoreExports.cs`, exactly the contract's code: a `[SupportedOSPlatform("browser")] public static partial class`, `CommandCapacity = 32`, one static `BatchStepper`, and the eight one-line `[JSExport]` delegations `LoadCollision`, `LoadConfig`, `Reset` (a `[JSMarshalAs<JSType.BigInt>] long` salt, `unchecked((ulong)…)`), `CommandBuffer` and `ReportBuffer` (`[return: JSMarshalAs<JSType.MemoryView>] ArraySegment<byte>`), `StepQueued`, `RunStream` and `EvaluateTables`, with `byte[]` parameters only.
  - `src/MmoEngine.SimCore.Wasm/Program.cs`: `public static class Program { public static int Main() => 0; }`.
  - Add the project to `MmoEngine.sln` under the `src` solution folder.

  Makes T266 (SimCoreExportsTests, all seven methods) pass. Also confirm with exit codes:
  - `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes` exit 0;
  - `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$(mktemp -d)"` exits 0 and writes `wwwroot/_framework/dotnet.js`.
- [ ] T280 [P] [US2] Implement `compareTables(inputs, serverResults, buildResults, buildName)` in `tools/agreement/lib/tables.mjs` (contracts/wasm-api.md#cross-build-tables):
  - The MMXT and MMXR headers must agree, using `TablesComparison`'s header wording.
  - Math rows match on bit-identical r0 and r1, or, where the server value is NaN (exponent all ones, mantissa non-zero), on any build NaN.
  - Pose rows compare all 25 floats bit for bit.
  - The first mismatch line follows the contract's two examples:
    - function names Sin, Cos, Tan, SinCos, Asin, Acos, Atan, Atan2, SinCosBinaryAngle and Sqrt by ID;
    - stance names Standing and Crouched;
    - yaw as `0x%04X`, and positions in JavaScript's shortest round-trip form;
    - the part fields `head`, `torso` and `legs` with `A.x` to `B.z` and `radius`, then the bounding sphere, named exactly as `TablesComparison` names them;
    - bits as uppercase `0x%08X`.

  It must produce the same lines as `src/MmoEngine.SimCore.Server/TablesComparison.cs`. Makes T267 (tables.test.mjs) pass.
- [ ] T281 [P] [US2] Implement `startServer({ app, work })` in `tools/agreement/lib/server.mjs` (contracts/wasm-api.md#served-files). It returns `{ url, port, close }`.
  - `node:http` on `127.0.0.1`, port 0.
  - Explicit GET routes:
    - `/web/page.html` and `/web/worker.mjs`, from `tools/agreement/web/`;
    - `/lib/runtime.mjs` and `/lib/tables.mjs`, from `tools/agreement/lib/` (`worker.mjs` imports `compareTables`);
    - `/_framework/*`, from `<app>/_framework/`;
    - `/work/*`, from `<work>/`.
  - The MIME table, plus COOP `same-origin`, COEP `require-corp` and `Cache-Control: no-store` on every response.
  - Files are streamed unmodified, without `Content-Encoding`.
  - A decoded path that resolves outside its root returns 403.
  - The PUT rule: the build is one of `chromium-worker`, `firefox-worker` or `webkit-worker`; the name matches `^[a-z0-9-]+$` and is never `tables` for `.records` or `.error.txt`; the body is at most 64 MiB. The body is written to a temporary file in the same folder, then renamed. The server answers 204, or 403 for anything else, and 404 for unknown paths.

  Add the `/lib/tables.mjs` row to the route table in `specs/015-shared-simulation-core/contracts/wasm-api.md#served-files`, which omits it. Makes T268 (server.test.mjs) pass.
- [ ] T282 [US2] Implement `tools/agreement/lib/runtime.mjs` (contracts/wasm-api.md#boot), after T279:
  - `JITERPRETER_OFF`, the three `--no-jiterpreter-*` flags;
  - `bootRuntime(dotnetJsUrl, { jiterpreter = true })`, which uses `dotnet.withRuntimeOptions(...)`, gets `getAssemblyExports('MmoEngine.SimCore.Wasm.dll')` and returns `{ runtime, exports, buildInfo, timings }`;
  - the record helpers `readRecords(bytes)` (u32 step, u32 stateLength, state, u64 hash, little-endian, next at `o + 16 + s`) and `firstDifferingStep(server, build)`. The latter returns the first step whose record is missing, has another step number, or differs in state bytes or hash, or `null` when both are equal.

  Then run locally against the T279 host:
  - `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$APP_OUT"`;
  - `agreement prepare --seeds 1-50 --work "$WORK"`;
  - `node --test tools/agreement/node/agreement.test.mjs`, with `AGREEMENT_BUILD=node-interp` and again with `node-interp-nojit`. Both pass every test.

  Makes T269 (per-stream tests, US2.1), T270 (US2.3), T271 (perturbation, US2-IT), T272 (tables, US3.8b and US5.5b) and T273 (batch and refusals) pass. A divergence found here is fixed in the core, never by special-casing a build. It is re-recorded with `streams record --scenario <name> --reason "…"` only when the server's rule itself was wrong, and `SimCoreVersion.RulesRevision` stays 1.
- [ ] T283 [US2] Implement the Worker runner (contracts/wasm-api.md#worker-playwright), after T282:
  - `tools/agreement/playwright.config.mjs`: `testDir: 'specs'`, projects `chromium`, `firefox` and `webkit` (headless), `retries: 0`, `workers: 1`, a 5-minute test timeout and the `list` reporter.
  - `tools/agreement/web/page.html`: a module script creates `new Worker('./worker.mjs', { type: 'module' })` and posts `start` with `build` from the query and `runtimeOptions: []`. It appends every message to `window.__agreement.events` and sets `window.__agreement.done` on `done` or `fatal`.
  - `tools/agreement/web/worker.mjs`, which posts the contract's message types (`booted`, `stream`, `error`, `batch`, `tables`, `done` and `fatal`) with their fields:
    - It boots with `bootRuntime(new URL('/_framework/dotnet.js', self.location.origin).href)` and posts `booted` (`inWorker: self instanceof WorkerGlobalScope`, `crossOriginIsolated`, `buildInfo` and `userAgent`).
    - It fetches `/work/manifest.json`, then runs each stream through `RunStream(…, -1)`. Records go by `PUT` to `/work/<build>/<stream>.records` and are compared with `/work/server/<stream>.records` using `firstDifferingStep`; a thrown error goes by `PUT` to `<stream>.error.txt`, as an `error` message.
    - It runs the batch check exactly as the Node runner does (batches of 1, 3 and 16, cycling).
    - It runs the tables check with `EvaluateTables` and `compareTables`, sending the result by `PUT` to `/work/<build>/tables.bin`, or the error to `tables.error.txt`.
    - It ends with `done`, or `fatal` on a boot or manifest failure.

  Then run `cd tools/agreement && AGREEMENT_APP="$APP_OUT/wwwroot" AGREEMENT_WORK="$WORK" npx playwright test --project chromium`. It passes, and `dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work "$WORK"` prints `chromium-worker: match, 68 streams, <steps> steps, tables match`. Makes T274 (worker.spec.mjs, US2.2) pass.
- [ ] T284 [P] [US2] Implement `.github/workflows/simcore.yml` with the `agreement` job only (the `measure` job is Phase 12's).
  - **Workflow level.**
    - `name: SimCore`.
    - `on.pull_request.paths` and `on.push` (`branches: [main]`) with the eleven paths of T275 (TriggersAndPaths).
    - `permissions: contents: read`.
    - `defaults.run.shell: bash`.
    - `env` `MMO_RUNNER_CLASS: gha-ubuntu-latest-x64`, `DOTNET_EnableAVX512F: "0"`, `DOTNET_TieredPGO: "0"`, `DOTNET_NOLOGO: "true"` and `DOTNET_CLI_TELEMETRY_OPTOUT: "true"`.
  - **Job.** `agreement`, `name: agreement`, on `ubuntu-latest`, with `timeout-minutes: 10`.
  - **Steps, in this order.**
    1. `actions/checkout` and `actions/setup-dotnet` (`global-json-file: global.json`), at the SHAs `.github/workflows/dotnet.yml` pins. Checkout has `persist-credentials: false`.
    2. `actions/setup-node` with `node-version: "22"`, pinned to the 40-hex commit of its latest release tag, resolved with `gh api repos/actions/setup-node/git/ref/tags/<tag>` (dereferenced when the tag is annotated) and commented `# <tag>`.
    3. A step appending `AGREEMENT_APP=$RUNNER_TEMP/simcore-app/wwwroot` and `AGREEMENT_WORK=$RUNNER_TEMP/agreement-work` to `$GITHUB_ENV`.
    4. `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$RUNNER_TEMP/simcore-app"`. This is the prebuilt runtime; no workload is ever installed here.
    5. `dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement prepare --seeds 1-50 --work "$AGREEMENT_WORK"`.
    6. `npm ci` in `tools/agreement`.
    7. `npx playwright install --with-deps chromium`.
    8. `npx playwright install --with-deps firefox` (`id: firefox`, `continue-on-error: true`), then the same for webkit (`id: webkit`).
    9. `node --test tools/agreement/node/tables.test.mjs tools/agreement/node/server.test.mjs`.
    10. `node --test tools/agreement/node/agreement.test.mjs` with `AGREEMENT_BUILD: node-interp`, then again with `node-interp-nojit`.
    11. `npx playwright test --project chromium`, then `--project firefox` and `--project webkit`, each `if: steps.<id>.outcome == 'success'`. All three run in `tools/agreement`.
    12. `dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement compare --work "$AGREEMENT_WORK" --summary "$GITHUB_STEP_SUMMARY"` with `if: ${{ !cancelled() }}`. This is the gate: no `continue-on-error`, and the default `--require`.
    13. `actions/upload-artifact` (the SHA `.github/workflows/harness.yml` pins) with `if: failure()`, `name: simcore-agreement-report` and `path: ${{ runner.temp }}/agreement-work/report/`.

  Makes T275 (TriggersAndPaths), T276 (Pins) and T277 (AgreementJob) pass.
- [ ] T285 [P] [US2] Add `"SimCore"` to `on.workflow_run.workflows` in `.github/workflows/auto-merge.yml`, so the list reads `["Workflow tests", "SimCore"]`. Extend the file's header comment, which says the list must name every pull request workflow, to name SimCore. Change nothing else, `GUARDED_PATHS` and `REQUIRE_CHECKS` included. Makes T278 (AutoMerge) pass.
- [ ] T286 [US2] Run the `agreement` job's commands end to end locally, in the workflow's order, on fresh `mktemp -d` app and work directories (US2-IT, SC-001):
  - publish;
  - `agreement prepare --seeds 1-50`, which prints `prepared <work>: 68 streams (18 named, 50 generated), <steps> steps, rules revision 1`;
  - the two runner unit-test files;
  - both Node runs;
  - `npx playwright test --project chromium`, plus firefox and webkit when `npx playwright install` succeeds here;
  - `agreement compare --work "$WORK"`, which exits 0 and prints `node-interp: match, 68 streams, <steps> steps, tables match`, the same for `node-interp-nojit` and `chromium-worker`, `match` or `not run` for `firefox-worker` and `webkit-worker`, `not run` for `node-interp-relinked` and `node-aot`, and `agreement: match`.

  Then confirm that `python3 -m unittest discover -s tests/workflows -v`, `dotnet build MmoEngine.sln -warnaserror`, `dotnet format MmoEngine.sln --verify-no-changes` and `dotnet test tests/MmoEngine.SimCore.Wasm.Tests` exit 0. Record the commands, their outputs and the elapsed time of the agreement steps for the PR description, which Phase 13 writes.

**Checkpoint**: These automated checks are green:
- `dotnet test tests/MmoEngine.SimCore.Wasm.Tests`, all seven `SimCoreExportsTests`;
- `node --test tools/agreement/node/tables.test.mjs tools/agreement/node/server.test.mjs`;
- the publish, `agreement prepare --seeds 1-50`, `agreement.test.mjs` as `node-interp` and as `node-interp-nojit`, `npx playwright test --project chromium` and `agreement compare`, which exits 0 with `tables match` for every required build;
- `python3 -m unittest discover -s tests/workflows -v`;
- `dotnet build MmoEngine.sln -warnaserror` and `dotnet format MmoEngine.sln --verify-no-changes`.

On the slice-2 PR, the SimCore `agreement` job passes within its 10-minute timeout.

---

## Phase 12: User Story 7 - Each build's cost is measured and recorded (Priority: P3), slice 2: the measurement job

**Goal**: Add the `measure` job of `.github/workflows/simcore.yml` and its runner. The job runs the server benchmarks on the runner class. It builds the prebuilt, relinked and AOT WebAssembly builds, or records why it could not produce each one. It gates on the relinked and AOT builds' agreement with the server. It measures download sizes, cold starts and step costs in Node and in a Chromium Worker, runs the exhaustive trig sweeps, and validates and uploads one measurement report (contracts/measurement-report.md). The numbers never gate (Principle VI).

**Independent Test**: The `measure` job on `gha-ubuntu-latest-x64` writes `$RUNNER_TEMP/simcore-measurement.json`, and `simcore measure validate-report` exits 0 on it. That proves every listed measurement is present with its unit, `runnerClass` matches in the report, `server` and each produced mode's build, and every mode is recorded as produced or with its reason. The job then uploads the report as `simcore-measurement`. Phase 9's `Allocation/StepAllocationTests.WarmedUpStepsAllocateNothing` passes in dotnet.yml. These commands pass locally: `node --test tools/agreement/node/measure.test.mjs tools/agreement/node/server.test.mjs` and `python3 -m unittest tests/workflows/test_simcore_workflow.py -v`.

### Tests (write first, confirm red)

- [ ] T287 [US7] Write failing test `MeasureJob.test_measure_job_measures_every_mode_and_validates_the_report` in `tests/workflows/test_simcore_workflow.py` (US7.2). Load `.github/workflows/simcore.yml` with `yaml.safe_load`, as `tests/workflows/test_harness_workflow.py` does, and reuse the module's `steps(job)` and `runs(job)` helpers. Add a `step_named(job, name)` helper that fails with the missing name. Given `jobs.measure`, assert:
  - The named steps appear at strictly increasing indexes in the order of contracts/measurement-report.md#how-the-report-is-produced: "Run the server benchmarks", "Publish the prebuilt interpreted build", "Install the wasm-tools workload", "Publish the relinked interpreted build", "Publish the AOT build", "Prepare the agreement work directory", "Agreement on the relinked build", "Agreement on the AOT build", "Compare the new builds with the server", "Measure the WebAssembly builds", "Validate the measurement report", "Upload the measurement report".
  - The "Measure the WebAssembly builds" run contains `node tools/agreement/lib/measure.mjs`, `--work "$WORK"`, `--server-benchmarks BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json` and `--out "$RUNNER_TEMP/simcore-measurement.json"`. It contains `--mode interp-prebuilt=` exactly once and no `--unproduced interp-prebuilt=`. For each of `interp-relinked` and `aot`, it contains exactly one `--mode <m>=` and exactly one `--unproduced <m>=`, in the two branches of one `if`/`else`.
  - The "Validate the measurement report" run contains `measure validate-report "$RUNNER_TEMP/simcore-measurement.json"`. It has no `continue-on-error`, no `|| true` and no `if:`.
  - The "Upload the measurement report" step `uses` `actions/upload-artifact@<40 hex>` with `with.name == "simcore-measurement"`, `with.path == "${{ runner.temp }}/simcore-measurement.json"` and `with.if-no-files-found == "error"`. It has no `if:`, so the upload runs only after validation passes.

  Run `python3 -m unittest tests/workflows/test_simcore_workflow.py -v` and confirm it fails: there is no `measure` job (KeyError).
- [ ] T288 [US7] Write failing test `MeasureJob.test_runner_class_and_pinned_intrinsics_are_set` in `tests/workflows/test_simcore_workflow.py` (US7-IT). Given the workflow-level `env` merged with `jobs.measure.env`, assert:
  - the merged env has `MMO_RUNNER_CLASS == "gha-ubuntu-latest-x64"`, `DOTNET_EnableAVX512F == "0"` and `DOTNET_TieredPGO == "0"` (strings);
  - `jobs.measure["runs-on"] == "ubuntu-latest"`;
  - no step's `env` sets `MMO_RUNNER_CLASS`, so one runner class reaches the benchmarks, `measure.mjs` and every report field.

  Run `python3 -m unittest tests/workflows/test_simcore_workflow.py -v` and confirm it fails.
- [ ] T289 [US7] Write failing test `MeasureJob.test_unproduced_modes_are_recorded_with_their_reason` in `tests/workflows/test_simcore_workflow.py` (SC-007, FR-031). Assert:
  - Step "Install the wasm-tools workload" has `id: install-workload`, run `dotnet workload install wasm-tools` and `continue-on-error: true`.
  - Steps "Publish the relinked interpreted build" (`id: publish-relinked`) and "Publish the AOT build" (`id: publish-aot`) each have `if: steps.install-workload.outcome == 'success'` and no `continue-on-error`. Each run writes `produced=true` to `"$GITHUB_OUTPUT"` on success. On failure each writes `produced=false` and a `reason=` line: relinked `reason=dotnet publish exited ${code}; see the publish-relinked step log`, AOT `reason=dotnet publish -p:RunAOTCompilation=true exited ${code}; see the publish-aot step log`. Each then exits 0. The AOT run contains `-p:RunAOTCompilation=true` and the relinked run does not.
  - The env of "Measure the WebAssembly builds" maps `RELINKED_PRODUCED: ${{ steps.publish-relinked.outputs.produced }}`, `RELINKED_REASON: ${{ steps.publish-relinked.outputs.reason }}`, `AOT_PRODUCED: ${{ steps.publish-aot.outputs.produced }}` and `AOT_REASON: ${{ steps.publish-aot.outputs.reason }}`.
  - The measure run falls back to the reason `dotnet workload install wasm-tools failed; see the install-workload step log` when the publish was skipped.

  Run `python3 -m unittest tests/workflows/test_simcore_workflow.py -v` and confirm it fails.
- [ ] T290 [US7] Write failing test `MeasureJob.test_server_benchmarks_run_with_the_runner_class` in `tests/workflows/test_simcore_workflow.py` (FR-030, full run). Assert:
  - Step "Run the server benchmarks" run contains `dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Benchmarks -- --filter '*'`.
  - It has no `continue-on-error` and no step-level `env` that sets or unsets `MMO_RUNNER_CLASS`. The benchmark `Main` exits 2 without the variable (Phase 9), so a missing class fails the job.
  - Its index is lower than "Measure the WebAssembly builds", which reads its output file.

  Run `python3 -m unittest tests/workflows/test_simcore_workflow.py -v` and confirm it fails.
- [ ] T291 [US7] Write the failing supporting tests of class `MeasureJob` in `tests/workflows/test_simcore_workflow.py`. Run `python3 -m unittest tests/workflows/test_simcore_workflow.py -v` and confirm each fails.
  - `test_relinked_and_aot_agreement_gates_the_job`:
    - "Agreement on the relinked build" has `if: steps.publish-relinked.outputs.produced == 'true'`, env `AGREEMENT_BUILD: node-interp-relinked` and `AGREEMENT_APP: ${{ runner.temp }}/publish/relinked/wwwroot`, and run `node --test tools/agreement/node/agreement.test.mjs`.
    - "Agreement on the AOT build" has the same with `publish-aot`, `node-aot` and `publish/aot/wwwroot`.
    - "Compare the new builds with the server" has `if: steps.publish-relinked.outputs.produced == 'true' || steps.publish-aot.outputs.produced == 'true'`. Its run contains `agreement compare --work "$WORK" --require "$REQUIRE" --summary "$GITHUB_STEP_SUMMARY"` and builds `REQUIRE` from `node-interp-relinked` and `node-aot`.
    - None of the three has `continue-on-error`.
    - The `agreement` job's compare step still contains neither `node-interp-relinked` nor `node-aot`, so its gating builds are unchanged.
  - `test_prebuilt_build_is_published_before_the_workload`: the run of "Publish the prebuilt interpreted build" contains `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$RUNNER_TEMP/publish/prebuilt"` and not `RunAOTCompilation`. Its index is lower than "Install the wasm-tools workload". No step before it runs `dotnet workload`. The step has no `continue-on-error`.
  - `test_exhaustive_math_sweeps_run_here_only`:
    - Scanning the raw text of every `.github/workflows/*.yml` finds `MMO_EXHAUSTIVE_MATH` only in `simcore.yml`, and inside it only in `jobs.measure` step "Run the exhaustive math sweeps".
    - That step's run contains `MMO_EXHAUSTIVE_MATH=1 dotnet test tests/MmoEngine.SimCore.Tests -c Release --filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Math"` and `>> "$GITHUB_STEP_SUMMARY"`.
    - The step has `continue-on-error: true`, because the sweeps are a measurement, not a gate (Phase 5).
    - It comes after "Compare the new builds with the server" and before "Measure the WebAssembly builds".
  - `test_job_times_out_after_thirty_minutes`: `jobs.measure["timeout-minutes"] == 30`.
  - `test_runner_tests_run_before_the_measurement`:
    - "Install the agreement tools" runs `npm ci` with `working-directory: tools/agreement`.
    - "Install Chromium" runs `npx playwright install --with-deps chromium` with the same working directory.
    - "Test the measurement runner" runs `node --test tools/agreement/node/measure.test.mjs tools/agreement/node/server.test.mjs`.
    - All three come before "Measure the WebAssembly builds", and none has `continue-on-error`.
- [ ] T292 [US7] Write failing test `'parses the boot manifest from dotnet.js'` in `tools/agreement/node/measure.test.mjs` (`node:test`, `node:assert/strict`), with the fixture `tools/agreement/node/fixtures/dotnet.js` (US7.2).
  - The fixture is a few lines of JavaScript, then `/*json-start*/`, a boot manifest, then `/*json-end*/`, then more JavaScript.
  - The manifest copies the shape of a real `dotnet publish src/MmoEngine.SimCore.Wasm -c Release` manifest: `debugLevel` 0, `globalizationMode` `"invariant"`, and `resources` groups `jsModuleNative`, `jsModuleRuntime`, `wasmNative`, `coreAssembly` and `assembly`, holding the eight file names of contracts/measurement-report.md#example (no `icu`).
  - Given that file, `parseBootManifest(text)` returns `debugLevel === 0` and `globalizationMode === 'invariant'`.
  - `downloadList(manifest)` returns, in order, `{name: 'dotnet.js', group: 'loader'}` and then each group's names with group `jsModuleNative`, `jsModuleRuntime`, `wasmNative`, `coreAssembly` and `assembly`, nine entries in all.
  - Text without the markers throws an Error with message `boot manifest not found in dotnet.js: no /*json-start*/ marker`.

  Run `node --test tools/agreement/node/measure.test.mjs` and confirm it fails: `lib/measure.mjs` does not exist.
- [ ] T293 [US7] Write failing test `'nearest-rank percentile picks the ceil(p/100*n)-th smallest'` in `tools/agreement/node/measure.test.mjs` (US7.2, contracts/measurement-report.md#how-each-number-is-measured). Import `nearestRank` and `summarize` from `../lib/passes.mjs`. Assert:
  - over the ten samples `[7, 3, 10, 1, 9, 2, 8, 4, 6, 5]`, `nearestRank(s, 50) === 5` and `nearestRank(s, 90) === 9`;
  - over 1..3008 in reverse order, `nearestRank(s, 99) === 2978`;
  - `nearestRank([42], 99) === 42` and `nearestRank(s, 100) === 10`;
  - `nearestRank([], 50)` throws `no samples`;
  - `summarize(s)` deep-equals `{median: 5, p90: 9}`.

  Run `node --test tools/agreement/node/measure.test.mjs` and confirm it fails.
- [ ] T294 [US7] Write failing tests `'download totals are the exact sums'` and `'a missing compressed sibling fails the measurement'` in `tools/agreement/node/measure.test.mjs` (US7.2: "download size (raw and compressed)").
  - Setup: create a unique temp publish directory with `fs.mkdtempSync(path.join(os.tmpdir(), 'measure-'))`, removed in `t.after`. Write `wwwroot/_framework/dotnet.js` (the fixture). Write each of the nine names with a distinct deterministic length (`1000 * (i + 1)` bytes of `i & 0xff`). Write each file's `.br` (`zlib.brotliCompressSync`) and `.gz` (`zlib.gzipSync`) sibling.
  - When `measureDownload(publishDir)` runs, assert:
    - `files` lists the nine names in `downloadList` order with their groups;
    - each `rawBytes` equals the written length, and `brotliBytes` and `gzipBytes` equal the sibling sizes from `fs.statSync`;
    - `total` deep-equals `{files: 9, rawBytes: Σ raw, brotliBytes: Σ br, gzipBytes: Σ gz}`, exact integers.
  - With `_framework/System.IO.Hashing.x8z6mup4sz.wasm.gz` deleted, `measureDownload` throws a `MeasurementError` with message `missing compressed file _framework/System.IO.Hashing.x8z6mup4sz.wasm.gz`.

  Run `node --test tools/agreement/node/measure.test.mjs` and confirm both fail.
- [ ] T295 [US7] Write failing test `'an unproduced mode is only its reason'` in `tools/agreement/node/measure.test.mjs` (US7.2: "names any mode it could not produce and why"; FR-031). Assert:
  - `unproducedMode('dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log')` deep-equals `{produced: false, reason: '<that text>'}`, and `Object.keys` of the result is exactly `['produced', 'reason']`;
  - `unproducedMode('')` throws a `UsageError` with message `--unproduced aot needs a non-empty reason`, using the mode name passed as the second argument;
  - an `assembleReport` call whose `aot` input is unproduced gives `report.wasm.modes.aot` equal to that two-key object.

  Run `node --test tools/agreement/node/measure.test.mjs` and confirm it fails.
- [ ] T296 [US7] Write the failing supporting tests in `tools/agreement/node/measure.test.mjs`. Run `node --test tools/agreement/node/measure.test.mjs` and confirm each fails.
  - `'arguments need each mode exactly once and the runner class'` (Theory-style table of `parseArgs(argv, env)` cases):
    - With `MMO_RUNNER_CLASS` unset or `''`, the result is `{exitCode: 2, message: 'MMO_RUNNER_CLASS is not set; a measurement without its runner class is not usable'}`.
    - With `aot` absent, the message is `mode aot is not given; pass --mode aot=<dir> or --unproduced aot="<reason>"`.
    - With `interp-prebuilt` twice, it is `mode interp-prebuilt is given more than once`.
    - With `--mode jit=x`, it is `unknown mode jit; modes are interp-prebuilt, interp-relinked, aot`.
    - With `--work` missing, it is `--work is required`, and the same for `--server-benchmarks` and `--out`.
    - With both interpreted modes unproduced, it is `at least one interpreted mode must be produced`.
    - A valid argv returns the work, benchmarks and out paths, the `RUNNER_CLASS`, and per mode `{produced: true, dir}` or `{produced: false, reason}`.
  - `'build flags match the mode'`:
    - `buildFlags('interp-prebuilt')` deep-equals `{aot: false, relinked: false, workload: false}`;
    - `buildFlags('interp-relinked')` deep-equals `{aot: false, relinked: true, workload: true}`;
    - `buildFlags('aot')` deep-equals `{aot: true, relinked: true, workload: true}`.
  - `'assembled report has exactly the contract fields'`:
    - Build the report with `assembleReport(inputs)` from fixed inputs: a server object, two produced modes, one unproduced mode, machine and versions, a fixture `report/agreement.json` with `node-interp-relinked` `Match` and no `node-aot`, and an env without `GITHUB_SHA`, `DOTNET_EnableAVX512F` or `DOTNET_TieredPGO`.
    - Its recursive key structure (arrays compared by their first element) equals that of `tests/MmoEngine.SimCore.Tests/Cli/Fixtures/measurement-report.json`, the contract example that Phase 9 committed, read read-only.
    - `recordedAt` matches `/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$/`, `commit === 'unknown'`, and `dotnet.environment` deep-equals `{DOTNET_EnableAVX512F: 'unset', DOTNET_TieredPGO: 'unset'}`.
    - `runnerClass` and `server.runnerClass` are the input class.
    - `wasm.modes['interp-prebuilt'].agreement === null` and `wasm.modes['interp-relinked'].agreement` deep-equals `{build: 'node-interp-relinked', status: 'Match'}`. A produced `aot` without an entry gets `{build: 'node-aot', status: 'NotRun'}`.
  - `'step passes warm up 512 steps, time 3008 and reset before each pass'`: call `runPasses` from `../lib/passes.mjs` with fake exports, which count `reset` and `stepQueued(n)` calls and return `n`, and a fake clock.
    - With `batch: 1`, `stepsPerPass: 30` and a clock that advances 1000 ns per `stepQueued`: `reset` is called 118 times (`ceil(3520 / 30)`), `stepQueued(1)` 3520 times, and `samples.length === 3008`; `steps === 3008`, `meanNs === 1000` and `p99Ns === 1000`.
    - With `batch: 16`, a clock that advances 16000 ns per `stepQueued(16)`, and 30 steps per pass: every timed call is `stepQueued(16)` and starts after at least 512 steps have run. Each pass's trailing `stepQueued(14)` is untimed. 188 batches are timed, and `steps === 3008` and `meanNs === 1000`.
    - A fake `stepQueued` that returns 15 for a count of 16 makes `runPasses` throw a `MeasurementError` with message `StepQueued(16) committed 15 steps: <stop text>`.
- [ ] T297 [P] [US7] Write failing test `'serves the measurement page, its worker and the shared passes module'` in `tools/agreement/node/server.test.mjs`, extending Phase 11's file and its start-the-server helper. Assert:
  - `GET /web/measure.html` returns 200 with `Content-Type: text/html; charset=utf-8`, and the body equals `tools/agreement/web/measure.html`;
  - `GET /web/measure-worker.mjs` and `GET /lib/passes.mjs` return 200 with `Content-Type: text/javascript`;
  - every one of the three responses carries `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Embedder-Policy: require-corp` and `Cache-Control: no-store`;
  - `PUT /work/node-aot/flat-run-30hz.records` still returns 403, because the PUT builds are unchanged.

  Run `node --test tools/agreement/node/server.test.mjs` and confirm it fails with 404s.

### Implementation

- [ ] T298 [P] [US7] Implement `tools/agreement/lib/passes.mjs`, an ES module with no `node:` imports so that Node and the Worker share it. It exports:
  - `nearestRank(samples, p)`: sort a copy ascending and return element `Math.max(1, Math.ceil(p / 100 * n)) - 1`; throw `no samples` when the list is empty.
  - `summarize(samples)`: return `{median: nearestRank(s, 50), p90: nearestRank(s, 90)}`.
  - `MeasurementError`.
  - `runPasses({ reset, stepQueued, writeCommands, commands, stepsPerPass, batch, warmupSteps = 512, timedSteps = 3008, now, unitNs })`.

  `runPasses` plays the stream's commands in passes, with an untimed `reset()` before each pass. It counts every step run, and the first `warmupSteps` are untimed. It times each call with `now()`: a single step when `batch` is 1, otherwise only full batches inside a pass, whose per-step time is the batch time divided by `batch`. A pass's partial batch runs untimed. It stops once `timedSteps` steps have been timed and returns `{steps, meanNs: totalNs / steps, p99Ns: nearestRank(perStepNs, 99), samples}`. A call that commits fewer steps than asked throws `MeasurementError('StepQueued(<n>) committed <m> steps: <stop text>')`, with the stop text read from the report buffer per contracts/wasm-api.md#one-frame-from-javascript.

  Makes T293 and T296's `'step passes warm up 512 steps, time 3008 and reset before each pass'` pass.
- [ ] T299 [US7] Implement the pure part of `tools/agreement/lib/measure.mjs` (ES module; it imports `node:fs`, `node:path`, `node:os`, `node:child_process` and `./passes.mjs`; `@playwright/test` is imported dynamically only inside the Worker measurement, so the tests need no browser), after T298. It exports:
  - `UsageError` and a re-export of `MeasurementError`.
  - `parseArgs(argv, env)`, with the rules and messages tested above (each of `interp-prebuilt`, `interp-relinked` and `aot` exactly once via `--mode <m>=<dir>` or `--unproduced <m>="<reason>"`).
  - `parseBootManifest(text)`: JSON between `/*json-start*/` and `/*json-end*/`.
  - `downloadList(manifest)`: `dotnet.js` as `loader`, then every `name` in `resources.jsModuleNative`, `jsModuleRuntime`, `wasmNative`, `coreAssembly`, `assembly` and `icu`, in that group order.
  - `measureDownload(publishDir)`: read `<dir>/wwwroot/_framework/dotnet.js`; take raw, `.br` and `.gz` sizes; a missing sibling throws `MeasurementError('missing compressed file _framework/<name>.<br|gz>')`; compute `total` as exact integer sums.
  - `unproducedMode(reason, mode)` and `buildFlags(mode)`.
  - `assembleReport(inputs)`, which builds exactly the top-level, `server`, `wasm.modes` and per-mode fields of contracts/measurement-report.md#fields:
    - `formatVersion` 1, `kind` `simcore-measurement`, `label` `measurement`;
    - `recordedAt` as `new Date().toISOString()` without milliseconds;
    - `commit` from `GITHUB_SHA`, or `unknown`;
    - `machine`: `os.cpus()[0].model`, `os.cpus().length`, `os.arch()` and `` `${os.type()} ${os.release()}` ``;
    - `dotnet.sdkVersion` from `dotnet --version`, and `runtimeVersion` from the highest `Microsoft.NETCore.App 10.` line of `dotnet --list-runtimes`, both read in the job that ran the benchmarks;
    - `dotnet.environment`, holding each variable's value or `unset`;
    - `node.version` from `process.version`, and `chromium.version` from Playwright's `browser.version()`;
    - `server` copied as is from `--server-benchmarks`;
    - each produced mode's `build`, `runtime` (the first Node cold start's `runtimeBuildInfo` fields, the manifest's `debugLevel` and `globalizationMode`, `runtimeOptions: []`, `jiterpreter: true`), `download`, `startup` (`coldStarts: 10`), `steps` and `agreement`. `agreement` is null for `interp-prebuilt`. Otherwise it comes from `<work>/report/agreement.json`'s `builds[]` entry for `node-interp-relinked` or `node-aot`, as `{build, status}`, or `status: 'NotRun'` when the entry or the file is absent.

  Makes T292, T294, T295, and T296's `'arguments need each mode exactly once and the runner class'`, `'build flags match the mode'` and `'assembled report has exactly the contract fields'` pass.
- [ ] T300 [US7] Implement `tools/agreement/lib/measure-node.mjs`, the Node half of contracts/measurement-report.md#how-each-number-is-measured, after T298. It exports `coldStart({app, work})` and `stepCosts({app, work})`, and a script entry `node measure-node.mjs <cold-start|steps> --app <wwwroot> --work <dir>` that prints one JSON line on stdout and exits 0, or prints the error on stderr and exits 1.
  - Both read the inputs before the clock starts: `manifest.json`, the batch entries of `flat-run-30hz`, `rifle-burst` and `launcher-arc` (`config.bin`, `initial-state.bin`, `commands.bin`, `matchSalt` as `BigInt.asIntN(64, BigInt('0x' + s))`) and each stream's collision file.
  - `coldStart` calls `bootRuntime(pathToFileURL(join(app, '_framework', 'dotnet.js')).href)` from `./runtime.mjs` (Phase 11), which gives `importMs` and `createMs`.
  - It then times with `performance.now()` the calls `LoadCollision`, `LoadConfig`, `Reset` and `StepQueued(1)` on the first command, which must return 0, 0, 0 and 1. Any other value throws naming the call and its code. That interval is `firstStepMs`.
  - `coldStart` returns `{importMs, createMs, firstStepMs, totalMs: importMs + createMs + firstStepMs, buildInfo}`.
  - `stepCosts` boots once and, for each kind (`movement`/`flat-run-30hz`, `weapon`/`rifle-burst`, `projectile`/`launcher-arc`), loads the collision file and config, then calls `runPasses` with `batch: 1` and a `now` from `process.hrtime.bigint()`. It returns `{kind, scenario, batch: 1, steps, meanNs, p99Ns}`.

  Checked end to end by the `measure` job's "Validate the measurement report" step (pinned by T287).
- [ ] T301 [US7] Implement `tools/agreement/web/measure.html` and `tools/agreement/web/measure-worker.mjs`, the Chromium Worker half of contracts/measurement-report.md#how-each-number-is-measured, after T298.
  - `measure.html` starts `new Worker('./measure-worker.mjs', { type: 'module' })` and posts `start` with `run` (`cold-start` or `steps`) from its `?run=` query. It appends every message to `window.__measure.events` and sets `window.__measure.done` on `done` or `fatal`.
  - `measure-worker.mjs` records `performance.now()` on its module's first line, so the Worker takes every time itself. It fetches `/work/manifest.json` and the three batch inputs and collision files before the clock starts.
  - It boots with `bootRuntime(new URL('/_framework/dotnet.js', self.location.origin).href)` from `/lib/runtime.mjs` and posts `booted` `{inWorker, crossOriginIsolated, buildInfo}`.
  - For `cold-start`, it times the first step as in Node and posts `coldStart` `{importMs, createMs, firstStepMs, totalMs}`.
  - For `steps`, it calls `runPasses` from `/lib/passes.mjs` with `batch: 16` and `now` from `performance.now()` scaled to ns, then posts one `steps` message `{kind, scenario, batch: 16, steps, meanNs, p99Ns}` per kind.
  - It then posts `done`. Any thrown error becomes `fatal` `{message}`.

  Checked end to end by the `measure` job's validate step (`startup.chromiumWorker`, `steps.chromiumWorker`, `crossOriginIsolated` true).
- [ ] T302 [P] [US7] Add the routes `GET /web/measure.html`, `GET /web/measure-worker.mjs` (from `tools/agreement/web/`) and `GET /lib/passes.mjs` (from `tools/agreement/lib/`) to `tools/agreement/lib/server.mjs`. They get the same MIME, COOP, COEP, `no-store` and traversal rules, and the PUT rules do not change. Add the three paths to the first row of the Routes table in `specs/015-shared-simulation-core/contracts/wasm-api.md#served-files`. Makes T297 pass.
- [ ] T303 [US7] Implement the entry point of `tools/agreement/lib/measure.mjs`, after T299. It runs only when `import.meta.url === pathToFileURL(process.argv[1]).href`.
  1. `parseArgs`; on a `UsageError`, print `measure: <message>` on stderr and exit 2.
  2. For each produced mode:
     - `measureDownload(dir)`.
     - Ten cold starts, each a fresh `node measure-node.mjs cold-start` process (`spawnSync(process.execPath, …)`). A non-zero exit is `MeasurementError('<mode>: node cold start <i> exited <code>: <stderr>')`.
     - One `measure-node.mjs steps` process.
     - Start `lib/server.mjs` with `AGREEMENT_APP=<dir>/wwwroot` and the work directory, and `chromium.launch()` from `@playwright/test`. Run ten cold starts, each in a fresh `browser.newContext()` and page at `/web/measure.html?run=cold-start`, then one context at `?run=steps`. Waiting on `window.__measure.done`: a `fatal` event, or a console message containing `does not have the expected content type "application/wasm"`, is a `MeasurementError`. `crossOriginIsolated` is the AND of every `booted` message.
     - Summarize each interval with `summarize`.
  3. Call `assembleReport` and write `--out` atomically (temp file in the same directory, then rename). Print `measure: wrote <out>, <p> modes produced, <u> unproduced` and exit 0. A `MeasurementError` prints `measure: <message>` and exits 1.

  Check: run the quickstart section "The measurement report" (specs/015-shared-simulation-core/quickstart.md#the-measurement-report) locally with `MMO_RUNNER_CLASS=local-dev`. `measure.mjs` exits 0, `dotnet run --project src/MmoEngine.SimCore.Cli -- measure validate-report "$MWORK/simcore-measurement.json"` prints nothing and exits 0, and without `MMO_RUNNER_CLASS`, `measure.mjs` exits 2.
- [ ] T304 [US7] Add the `measure` job to `.github/workflows/simcore.yml`, alongside Phase 11's `agreement` job and leaving it unchanged, after T303. The job has `runs-on: ubuntu-latest`, `timeout-minutes: 30` and the workflow's triggers, permissions and env (`MMO_RUNNER_CLASS`, `DOTNET_EnableAVX512F`, `DOTNET_TieredPGO`). Use the same SHA-pinned checkout (`persist-credentials: false`), `actions/setup-dotnet` (`global-json-file: global.json`) and `actions/setup-node` (Node 22) as the agreement job, and `actions/upload-artifact` pinned to a commit SHA. The steps run in this order, under the exact names, ids, conditions and runs that the `MeasureJob` tests pin:
  1. "Run the server benchmarks".
  2. "Publish the prebuilt interpreted build" (`-o "$RUNNER_TEMP/publish/prebuilt"`).
  3. "Install the wasm-tools workload" (`id: install-workload`, `continue-on-error: true`).
  4. "Publish the relinked interpreted build" (`id: publish-relinked`) and "Publish the AOT build" (`id: publish-aot`). Each runs `set +e`, publishes to `$RUNNER_TEMP/publish/<relinked|aot>`, captures `code=$?`, writes `produced` and `reason` to `$GITHUB_OUTPUT` and exits 0.
  5. "Prepare the agreement work directory": `WORK="$(mktemp -d "$RUNNER_TEMP/simcore-work.XXXXXX")"`, then `dotnet run --project src/MmoEngine.SimCore.Cli -c Release -- agreement prepare --seeds 1-50 --work "$WORK"`, then `echo "WORK=$WORK" >> "$GITHUB_ENV"`.
  6. "Agreement on the relinked build" and "Agreement on the AOT build".
  7. "Compare the new builds with the server". It builds `REQUIRE` with a comma join of `node-interp-relinked` and `node-aot` for the modes produced, and gates the job.
  8. "Run the exhaustive math sweeps" (`continue-on-error: true`). It runs `set -o pipefail; MMO_EXHAUSTIVE_MATH=1 dotnet test … --logger "console;verbosity=detailed" | tee "$RUNNER_TEMP/sweeps.log"`, then appends a `## SimCore exhaustive math sweeps` heading and the `SIMMATH ` lines that Phase 5's `ExhaustiveSweepTests` prints, grepped from the log, to `"$GITHUB_STEP_SUMMARY"`.
  9. "Install the agreement tools", "Install Chromium" and "Test the measurement runner".
  10. "Measure the WebAssembly builds". It builds a bash `args` array: `--mode interp-prebuilt="$RUNNER_TEMP/publish/prebuilt"`; for relinked and AOT, `--mode` when `*_PRODUCED` is `true`, else `--unproduced` with `*_REASON` or the workload reason.
  11. "Validate the measurement report".
  12. "Upload the measurement report" (`name: simcore-measurement`).
  13. "Upload the agreement report" (`if: failure()`, `name: simcore-measure-agreement-report`, path `${{ env.WORK }}/report/`).

  No step compares a measured number with a threshold. Makes T287, T288, T289, T290 and T291 (`test_relinked_and_aot_agreement_gates_the_job`, `test_prebuilt_build_is_published_before_the_workload`, `test_exhaustive_math_sweeps_run_here_only`, `test_job_times_out_after_thirty_minutes`, `test_runner_tests_run_before_the_measurement`) pass. Phase 11's `Pins.test_every_action_is_pinned_to_a_commit` stays green over the new job.

**Checkpoint**: These commands exit 0: `node --test tools/agreement/node/measure.test.mjs tools/agreement/node/server.test.mjs`, `python3 -m unittest tests/workflows/test_simcore_workflow.py -v` and `python3 -m unittest discover -s tests/workflows -v`. The local quickstart measurement run gives a report that `simcore measure validate-report` accepts with exit 0. On the slice-2 PR, the SimCore `measure` job passes: the relinked and AOT agreement is green whenever those builds were produced, `validate-report` exits 0 on the runner-class report, and the `simcore-measurement` artifact is uploaded. Phase 9's `StepAllocationTests.WarmedUpStepsAllocateNothing` passes in dotnet.yml (US7-IT).

---

## Phase 13: Polish and PR, slice 2

**Goal**: Close slice 2 on branch `015-shared-simulation-core-slice-2`. That means local runs of the slice-2 quickstart sections and the workflow and Node tests, the Wasm host at 95% or more changed-file coverage, the `docs/patch-notes.md` entry, `spec.md` and `tasks.md` brought up to date, and the spec-test review. Because the PR touches `.github/` (a guarded path), it also needs the independent automated review of `.routines/merge-policy.md`, then the session's own squash-merge and one push notification afterwards.

**Independent Test**: These all exit 0 locally:
- `dotnet build MmoEngine.sln -warnaserror`
- `dotnet format MmoEngine.sln --verify-no-changes`
- `dotnet test MmoEngine.sln --collect:"XPlat Code Coverage" --results-directory coverage`
- `python3 tools/coverage_gate.py --base origin/main --coverage-dir coverage`, with every `src/MmoEngine.SimCore.Wasm/*.cs` at or above 95% and present in a report
- `python3 -m unittest discover -s tests/workflows -v`
- `node --test tools/agreement/node/server.test.mjs tools/agreement/node/tables.test.mjs tools/agreement/node/measure.test.mjs`
- the quickstart's agreement steps: the publish, the prepare, both Node runs, `npx playwright test --project chromium` and `agreement compare`

On the PR, these checks pass on the head commit: the `SimCore` workflow's `agreement` and `measure` jobs, `.NET` and `Workflow tests`; the `review-spec-tests` run reports no blocking finding. `QuickstartTests.EverySliceTwoQuickstartCommandAndPathIsReal` also passes.

### Tests (write first, confirm red)

- [ ] T305 Write failing test `QuickstartTests.EverySliceTwoQuickstartCommandAndPathIsReal()` in `tests/MmoEngine.SimCore.Tests/Cli/QuickstartTests.cs`, next to Phase 10's `EverySliceOneQuickstartCommandParses`. The test reads `specs/015-shared-simulation-core/quickstart.md` read-only through `RepoPaths`, and its section list comes from `QuickstartSections.SliceTwo` in `tests/MmoEngine.SimCore.Tests/Support/QuickstartSections.cs`. That type does not exist yet, so the compile failure is the red. The sections are the ones headed `## Run the agreement suite`, `## The measurement report` and `## Workflow tests`. The test takes every line inside a ```` ```bash ```` block of those sections, with backslash continuations joined, and asserts:
  - Every `dotnet run --project src/MmoEngine.SimCore.Cli [-c Release] -- <args>` line, with `"$WORK"`, `"$MWORK"` and similar shell words replaced by a temp path, parses against the command tree of `MmoEngine.SimCore.Cli.Program` with an empty parse-error list. This covers `agreement prepare --seeds 1-50 --work`, `agreement compare --work`, `agreement compare --work ... --require node-interp-relinked,node-aot` and `measure validate-report`.
  - Every `dotnet publish <dir>` names a directory that holds a `.csproj`, which is `src/MmoEngine.SimCore.Wasm/MmoEngine.SimCore.Wasm.csproj`.
  - Every `node --test <path>` and `node <path>` names an existing file: `tools/agreement/node/agreement.test.mjs` and `tools/agreement/lib/measure.mjs`.
  - Every `AGREEMENT_BUILD=<name>` value and every name in a `--require` list is one of the builds in contracts/simcore-cli.md's build table, and is not `server`. The names are `node-interp`, `node-interp-nojit`, `chromium-worker`, `firefox-worker`, `webkit-worker`, `node-interp-relinked` and `node-aot`. The test asserts each name against the CLI's own known-build list in `src/MmoEngine.SimCore.Cli/Agreement/BuildNames.cs`, the one `agreement compare` uses.
  - Every `--mode <key>=` and `--unproduced <key>=` key is one of `interp-prebuilt`, `interp-relinked` and `aot` (contracts/measurement-report.md `wasm.modes`), and the measurement-report block names each of the three exactly once.
  - Every `npx playwright test --project <p>` names a project that `tools/agreement/playwright.config.mjs` declares (`chromium`, `firefox` or `webkit`, matched in the file's text).
  - `tests/workflows/test_simcore_workflow.py` exists.
  - On failure, the test reports the quickstart line and which check failed.

  Run `dotnet test tests/MmoEngine.SimCore.Tests --filter "FullyQualifiedName~QuickstartTests.EverySliceTwoQuickstartCommandAndPathIsReal"` and confirm it fails.

### Implementation

- [ ] T306 Add `tests/MmoEngine.SimCore.Tests/Support/QuickstartSections.cs`:
  - a static class with `SliceOne`, the headings Phase 10's test covers, moved out of `QuickstartTests` unchanged;
  - `SliceTwo`, the three headings above;
  - a shared `BashLines(string heading)` reader.

  Point `EverySliceOneQuickstartCommandParses` at `QuickstartSections.SliceOne`, with its assertions unchanged. Wherever the new test reports a mismatch, correct the line in `specs/015-shared-simulation-core/quickstart.md` to the built command, path or name. `quickstart.md` is held to the present, and the CLI and runners are never changed to fit a stale document. Makes T305 pass, and `EverySliceOneQuickstartCommandParses` stays green.
- [ ] T307 Bring the branch up to date and run the local gates. Run `git fetch origin` and `git merge origin/main` into `015-shared-simulation-core-slice-2`, never a force-push of `main`. Then run, from the repo root, and record each exit code and summary line for the PR description:
  - `dotnet restore MmoEngine.sln`
  - `dotnet build MmoEngine.sln -warnaserror --no-restore`
  - `dotnet format MmoEngine.sln --verify-no-changes --no-restore`
  - `dotnet test MmoEngine.sln --no-build --collect:"XPlat Code Coverage" --results-directory coverage`
  - `python3 -m unittest discover -s tests/workflows -p "test_coverage_gate.py" -v`
  - `python3 tools/coverage_gate.py --base origin/main --coverage-dir coverage`
  - `python3 -m unittest discover -s tests/workflows -v`, which needs PyYAML and covers `test_simcore_workflow.py`'s `TriggersAndPaths`, `Pins`, `AgreementJob`, `MeasureJob` and `AutoMerge`
  - `(cd tools/agreement && npm ci) && node --test tools/agreement/node/server.test.mjs tools/agreement/node/tables.test.mjs tools/agreement/node/measure.test.mjs`

  These are the same steps as `.github/workflows/dotnet.yml` and `.github/workflows/workflow-tests.yml`. Each must exit 0. A failure is fixed in the file that owns it, in that file's phase. A test is never skipped, disabled or quarantined to get green (`.routines/merge-policy.md`, Never).
- [ ] T308 Top up coverage: the gate must report no file under 95% and no file missing from every report. This applies to every `.cs` file under `src/` that slice 2 adds or changes: `src/MmoEngine.SimCore.Wasm/SimCoreExports.cs` and `src/MmoEngine.SimCore.Wasm/Program.cs`, plus any core, Server or Cli file that Phases 11 and 12 touched.
  - For each shortfall, add a Fact or Theory that asserts the uncovered branch's exact result: the code returned, the buffer length, the record bytes, or the `path: problem` line.
  - Wasm host tests go in `tests/MmoEngine.SimCore.Wasm.Tests/SimCoreExportsTests.cs`, in its serial collection. Core, Server and Cli tests go in the matching folder of `tests/MmoEngine.SimCore.Tests/`.
  - Never add a test whose only assertion is "does not throw".
  - Rerun the coverage commands of T307 until the gate exits 0.
- [ ] T309 Run the slice-2 quickstart sections exactly as `specs/015-shared-simulation-core/quickstart.md` writes them, in unique `mktemp -d` directories, and record each command's exit code and key output line for the PR description.
  - **Run the agreement suite.** Steps, each expected to exit 0:
    1. `dotnet publish src/MmoEngine.SimCore.Wasm -c Release -o "$APP_OUT"`.
    2. `agreement prepare --seeds 1-50 --work "$WORK"`, which prints `prepared <work>: 68 streams (18 named, 50 generated), <steps> steps, rules revision 1`.
    3. `(cd tools/agreement && npm ci && npx playwright install chromium)`.
    4. Both `node --test tools/agreement/node/agreement.test.mjs` runs, with `AGREEMENT_BUILD=node-interp` and `node-interp-nojit`.
    5. `npx playwright test --project chromium`.
    6. `agreement compare --work "$WORK"`, which prints `node-interp: match, 68 streams, <steps> steps, tables match`, the same line for `node-interp-nojit` and `chromium-worker`, `not run` for the builds that did not run, then `agreement: match`.
  - **The measurement report.** With `MMO_RUNNER_CLASS` set to a name for this container, run:
    1. the server benchmarks of the `## Benchmarks` section;
    2. the prebuilt publish;
    3. `agreement prepare`;
    4. `node tools/agreement/lib/measure.mjs` with `--mode interp-prebuilt=...` and `--unproduced interp-relinked="not built on this machine"` and `--unproduced aot="not built on this machine"`;
    5. `measure validate-report "$MWORK/simcore-measurement.json"`.

    `measure.mjs` must exit 0, and `validate-report` must print nothing and exit 0.
  - The numbers are recorded as measurements on this container's runner class only, never as targets or thresholds.
  - A failing step is fixed in the file and phase that own it, never by editing the expected output.
- [ ] T310 [P] Update `specs/015-shared-simulation-core/spec.md` to the work performed in slice 2. Wherever the built WebAssembly host, runners or workflow differ from a requirement, acceptance scenario or Independent Test of US2 or US7, or from SC-001, SC-004, SC-006 or SC-007, rewrite the text to state what was built, and record each change under **Assumptions** with its reason. Then reconcile other specs. The places to check are `specs/001-claude-mention-agent/spec.md`'s statements about `auto-merge.yml` and the workflows it waits for, and any other `specs/*/spec.md` that `.github/workflows/simcore.yml` or the `"SimCore"` `workflow_run` entry contradicts. Make each such spec state the current behavior, and record in this spec's Assumptions which spec prevails and why. Leave open decisions 3, 4, 5 and 9 unsettled (spec Constitution Requirements, merge-policy rule 6).
- [ ] T311 Open the PR as a draft from `015-shared-simulation-core-slice-2`, never from a `claude/*` branch: `git push -u origin 015-shared-simulation-core-slice-2`, then `gh pr create --draft --base main`. The title is "015 shared simulation core, slice 2: WebAssembly host, agreement suite and SimCore workflow". The description contains:
  - each command of the local-gates and quickstart tasks (above), with its exit code and result line;
  - the judgment calls made in Phases 11 to 13;
  - the new dependency `@playwright/test` 1.63.0 (Apache-2.0, a dev dependency of `tools/agreement/` only, as research.md R16 records), and that nothing else is new;
  - the guarded paths touched: `.github/workflows/simcore.yml` and `.github/workflows/auto-merge.yml`;
  - that open decisions 3, 4, 5 and 9 stay open;
  - gate 9's build-mode choice as the follow-up, made from the recorded numbers.

  The description never contains the at-sign mention that starts `.github/workflows/claude.yml`. It ends with the attribution lines:

  ```markdown
  🤖 Generated with [Claude Code](https://claude.com/claude-code)

  https://claude.ai/code/session_01VH4GDrwnfzK7HWUA8FYiJt
  ```
- [ ] T312 Add the entry at the top of `docs/patch-notes.md`, below the `<!-- Add the newest entry below this line. -->` marker, after T311. Its heading is `## <merge date> — Shared simulation core, slice 2: WebAssembly host and agreement suite`, followed by `Spec: [specs/015-shared-simulation-core/spec.md](../specs/015-shared-simulation-core/spec.md) · PR: #<n>`, using the number from T311. The sections:
  - **Behavior and contract changes**:
    - `src/MmoEngine.SimCore.Wasm` and its `[JSExport]` exports, including `EvaluateTables` (contracts/wasm-api.md);
    - `tools/agreement/`, with the Node runner (jiterpreter on and off) and the Playwright Worker runner;
    - the `SimCore` workflow's gating `agreement` job: 68 streams plus the cross-build tables, with `timeout-minutes: 10`;
    - the `measure` job and its `simcore-measurement` artifact, whose numbers never gate;
    - `auto-merge.yml` now waits for `SimCore`.
  - **Shared pieces to reuse**: the exports and the one-frame JavaScript pattern for sub-spec 8; `tools/agreement/lib/runtime.mjs`, `lib/server.mjs`, `lib/tables.mjs` and `lib/passes.mjs`; `simcore agreement prepare` and `compare`.
  - **Open follow-ups**: gate 9's WebAssembly build-mode choice from the recorded measurements; Firefox and WebKit stay optional while Playwright cannot always install them; `.routines/merge-policy.md` still says Workflow tests is the only CI.

  Commit and push the entry.
- [ ] T313 Watch the PR's checks on the pushed head commit, since this slice changes application and workflow code. Run `mcp__claude-code-remote__subscribe_pr_activity` for the PR, or `gh pr checks <n> --watch`. Every check run must complete with none failed or cancelled: `SimCore / agreement`, `SimCore / measure`, `.NET / dotnet` and `Workflow tests`.
  - On a failure, read the job log (`gh run view <run-id> --log-failed`), fix the owning file, and push.
  - Then download the measurement artifact (`gh run download <run-id> -n simcore-measurement -D <temp dir>`) and run `dotnet run --project src/MmoEngine.SimCore.Cli -- measure validate-report <temp dir>/simcore-measurement.json`, which must exit 0.
  - Add to the PR description the report's numbers, labelled as measurements on runner class `gha-ubuntu-latest-x64`:
    - server mean and p99 per benchmark;
    - per mode, either the raw, brotli and gzip download sizes, cold start and per-step cost, or the mode's unproduced reason.
- [ ] T314 Run `/review-spec-tests <PR#>` (the `review-spec-tests` skill, in a fresh subagent given only the PR link) against the PR, and fix every blocking finding. The check covers each slice-2 scenario: US2.1 to US2.7, US2-IT, US3.8b, US5.5b, US7.2, US7-IT, and SC-001, SC-004 (bit half), SC-006 and SC-007. Each must map to the test the Phase 11 and 12 tasks name (`tools/agreement/node/agreement.test.mjs`, `tools/agreement/specs/worker.spec.mjs`, `tests/workflows/test_simcore_workflow.py`, `tests/MmoEngine.SimCore.Wasm.Tests/SimCoreExportsTests.cs` and `tools/agreement/node/measure.test.mjs`), and that test must assert the scenario's stated values. Push the fixes, then rerun the local gates and wait for the PR's checks to pass again.
- [ ] T315 Audit `specs/015-shared-simulation-core/tasks.md`, `spec.md`, `quickstart.md`, the patch-notes entry and the PR description for any step assigned to a person, any manual check, approval or sign-off, or any stop-and-validate gate (constitution Principle XIII), with `grep -n -i -E "manual|by hand|sign-off|sign off|approv|QA|stop and validate|reviewer must|ask the (owner|maintainer)"`. None is allowed, so rewrite any hit that is not system behaviour or an automated check. Then check off every completed Phase 11, 12 and 13 task in `specs/015-shared-simulation-core/tasks.md`, so `tasks.md` matches the work performed, and push.
- [ ] T316 Run the independent automated review of `.routines/merge-policy.md`, which applies because the PR touches `.github/`.
  1. Spawn a fresh `general-purpose` subagent with the `Agent` tool, passing this session's `model`. Give it only the PR link: no summary and no conclusions. Its prompt: run the `code-review` skill at `high` with `--comment` against that PR, then end its turn.
  2. If it fails to start or errors, retry once. If the retry also fails, start a separate session titled "Review nbon12/mmo-engine#<n>" with `mcp__claude-code-remote__create_session` and the same prompt.
  3. Fix every confirmed blocking finding, push, and answer each finding on the PR as fixed or with why it is not a problem. Resolve each thread.
  4. If any fix is more than small and targeted, spawn a new reviewer the same way to check it.
- [ ] T317 Mark the PR ready and merge it.
  1. Once merge-policy rules 1 to 6 hold on the current head commit, run `gh pr ready <n>`. The rules: every check complete and passed; no unresolved thread and no review requesting changes; the description lists what was verified; the patch-notes entry is in; the guarded-path review has passed; no open decision is settled.
  2. `auto-merge.yml` does not merge a guarded-path PR, so squash-merge it with `gh pr merge <n> --squash`.
  3. Send one push notification with `PushNotification`, for example "merged mmo-engine#<n> touching guarded paths: .github/workflows/simcore.yml, .github/workflows/auto-merge.yml".
  4. Unsubscribe from the PR's activity.
  5. Treat `015-shared-simulation-core-slice-2` as closed from then on.

**Checkpoint**: These pass on the merged head commit: `QuickstartTests.EverySliceTwoQuickstartCommandAndPathIsReal` and the rest of `dotnet test MmoEngine.sln`, `tools/coverage_gate.py` (Wasm host at 95% or more), `python3 -m unittest discover -s tests/workflows -v`, the `node --test` runs, the `SimCore` `agreement` and `measure` jobs, `.NET`, `Workflow tests`, the `review-spec-tests` run and the automated guarded-path review. The PR is squash-merged and the push notification is sent.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies; starts immediately.
- **Foundational (Phase 2)**: Depends on Phase 1. BLOCKS every user story phase.
- **US1 (Phase 3, MVP)**: Depends on Phase 2.
- **US2 slice 1 (Phase 4)**: Depends on Phases 2 and 3 (committed movement streams, `StreamRunner`, `ScenarioCatalog`, `TestWorldGenerator`).
- **US3 (Phase 5)**: Depends on Phase 2 and Phase 4 (`CrossBuildTables` feeds `FixedTableBoundsTests`). Its backstop runs over every core file present when it lands, so later phases keep it green.
- **US4 (Phase 6)**: Depends on Phases 2 and 3, and on Phase 4 for `SplitMix64` (`Spread` reuses `SplitMix64.Mix`).
- **US5 (Phase 7)**: Depends on Phases 2, 3, 4 and 6 (projectiles are spawned by stage 3; `fuzz-all-buttons` is defined in Phase 4). It records the seven fire streams, so it is the last phase that adds a world-step stage.
- **US6 (Phase 8)**: Depends on Phases 2 and 3, and on Phase 4 (the `rules-revision` row of `GoldenRefusalTests.OtherOpenRefusalsHappenBeforeStepZero` needs `StreamRunner` open step 2).
- **US7 slice 1 (Phase 9)**: Depends on Phases 2, 3, 4, 6 and 7 (the step functions, `BatchStepper` and all 18 definitions it benchmarks and checks for allocation).
- **Polish slice 1 (Phase 10)**: Depends on Phases 1-9. Its merge closes slice 1.
- **US2 slice 2 (Phase 11)**: Depends on Phase 10 being merged to `main`; runs on the new branch `015-shared-simulation-core-slice-2`.
- **US7 slice 2 (Phase 12)**: Depends on Phase 11 (`lib/runtime.mjs`, `lib/server.mjs`, the workflow file and its test module).
- **Polish slice 2 (Phase 13)**: Depends on Phases 11 and 12.

### User Story Dependencies

- **US1 (P1)**: Only Foundational. It is the MVP: deterministic movement, recorded streams and `streams verify`.
- **US2 (P1)**: Slice 1 needs US1's streams and runner; slice 2 needs all of slice 1 on `main`.
- **US3 (P1)**: Foundational plus US2 slice 1's `CrossBuildTables` for the bound test; the rules themselves need nothing from other stories. The US3.8b half is owned by US2 slice 2.
- **US4 (P2)**: Foundational and US1 (world step, scenario catalog); `SplitMix64` from US2 slice 1.
- **US5 (P2)**: US4 (shots spawn projectiles) and US2 slice 1. The US5.5b half is owned by US2 slice 2.
- **US6 (P3)**: Foundational, US1 and US2 slice 1. Independent of US3, US4, US5 and US7.
- **US7 (P3)**: Slice 1 needs US1, US2 slice 1, US4 and US5; slice 2 needs US2 slice 2.
- Region merges, splits, portals and tick overruns are N/A by design (spec Edge Cases): the core is a pure step function with no region data.

### Within Each User Story

- The phase's test-support task comes first; then every red test task, each run and confirmed failing (a compile failure because the type under test does not exist counts as red); then the green tasks that name the red tasks they turn green.
- Codes and data types before codecs; codecs before `SimWorld`; step functions before the `SimWorld` stage that calls them; scenario definitions before the `streams record` task that commits their `.mmrs` files.
- Within Phase 5 the rule tasks come before the backstop fix (T160), and `SimMathBounds` comes after it.
- A stream is recorded only by the phase that owns it (Phase 3 the 11 movement streams, Phase 7 the 7 fire streams), always with `streams record --scenario <name> --reason "<why>"`, never `--all` before Phase 7. A later phase leaves earlier hashes unchanged or re-records with a reason in the same green task. `SimCoreVersion.RulesRevision` stays 1.
- Each phase ends at its checkpoint: build with `-warnaserror`, `dotnet format --verify-no-changes`, its test filter and, where it changes `src/`, the 95% coverage gate.

---

## Parallel Opportunities

- In Phase 1, T002 and the `.gitattributes` task run together; the project-file tasks follow in their stated order.
- In Phase 2, once T013 exists, every red test task is [P] (one file each), and the codes, generator, `default.json` and CLI-infrastructure green tasks are [P].
- Once Phase 4 is done, Phases 5, 6 and 8 can proceed in parallel; Phase 7 waits for Phase 6, and Phase 9 for Phase 7.
- In slice 2, Phase 11's test files (`tables.test.mjs`, `server.test.mjs`, the Wasm test project, the Worker spec) and its `lib/tables.mjs`, `lib/server.mjs`, host, workflow and `auto-merge.yml` tasks are [P] among themselves.

### Parallel example: User Story 1

```text
Task: "T063 InputValidatorTests in tests/MmoEngine.SimCore.Tests/Inputs/InputValidatorTests.cs"
Task: "T065 ClosestPointsTests in tests/MmoEngine.SimCore.Tests/Collision/ClosestPointsTests.cs"
Task: "T067 CapsuleSweepTests in tests/MmoEngine.SimCore.Tests/Collision/CapsuleSweepTests.cs"
Task: "T069 StepUpTests in tests/MmoEngine.SimCore.Tests/Movement/StepUpTests.cs"
Task: "T075 TestWorldGeneratorTests in tests/MmoEngine.SimCore.Tests/Scenarios/TestWorldGeneratorTests.cs"
```

### Parallel example: User Story 2

```text
# Slice 1
Task: "T108 SplitMix64Tests in tests/MmoEngine.SimCore.Tests/Hashing/SplitMix64Tests.cs"
Task: "T113 CrossBuildTablesTests in tests/MmoEngine.SimCore.Tests/Batch/CrossBuildTablesTests.cs"
Task: "T114 TablesComparisonTests in tests/MmoEngine.SimCore.Tests/Agreement/TablesComparisonTests.cs"
Task: "T118 StreamGeneratorTests in tests/MmoEngine.SimCore.Tests/Agreement/StreamGeneratorTests.cs"
# Slice 2
Task: "T267 tools/agreement/node/tables.test.mjs"
Task: "T268 tools/agreement/node/server.test.mjs"
Task: "T274 tools/agreement/specs/worker.spec.mjs and package.json"
```

### Parallel example: User Story 3

```text
Task: "T147 DescriptorTests in tests/MmoEngine.SimCore.Analyzers.Tests/DescriptorTests.cs"
Task: "T148 WiringTests in tests/MmoEngine.SimCore.Analyzers.Tests/WiringTests.cs"
Task: "T150 BuildTests in tests/MmoEngine.SimCore.Analyzers.Tests/BuildTests.cs"
Task: "T151 MathReferenceTests in tests/MmoEngine.SimCore.Tests/Math/MathReferenceTests.cs"
```

### Parallel example: User Story 4

```text
Task: "T163 WeaponRuns in tests/MmoEngine.SimCore.Tests/Support/WeaponRuns.cs"
Task: "T164 SpreadTests in tests/MmoEngine.SimCore.Tests/Weapons/SpreadTests.cs"
Task: "T165 WeaponPhaseTests in tests/MmoEngine.SimCore.Tests/Weapons/WeaponPhaseTests.cs"
Task: "T166 WeaponStepTests in tests/MmoEngine.SimCore.Tests/Weapons/WeaponStepTests.cs"
```

### Parallel example: User Story 5

```text
Task: "T182 BallisticsTests in tests/MmoEngine.SimCore.Tests/Projectiles/BallisticsTests.cs"
Task: "T183 SegmentCapsuleTests in tests/MmoEngine.SimCore.Tests/Projectiles/SegmentCapsuleTests.cs"
Task: "T184 SegmentQueryTests in tests/MmoEngine.SimCore.Tests/Collision/SegmentQueryTests.cs"
Task: "T192 ProjectileStageTests in tests/MmoEngine.SimCore.Tests/World/ProjectileStageTests.cs"
```

### Parallel example: User Story 6

```text
Task: "T219 DecoderPropertyTests in tests/MmoEngine.SimCore.Tests/Properties/DecoderPropertyTests.cs"
Task: "T220 CollisionFileLimitTests in tests/MmoEngine.SimCore.Tests/Formats/CollisionFileLimitTests.cs"
Task: "T221 RecordedStreamCheckTests in tests/MmoEngine.SimCore.Tests/Formats/RecordedStreamCheckTests.cs"
Task: "T222 HostileHeaderTests in tests/MmoEngine.SimCore.Tests/Formats/HostileHeaderTests.cs"
```

### Parallel example: User Story 7

```text
# Slice 1
Task: "T237 ServerBenchmarkExportTests in tests/MmoEngine.SimCore.Tests/Benchmarks/ServerBenchmarkExportTests.cs"
Task: "T238 StepCaseTests in tests/MmoEngine.SimCore.Tests/Benchmarks/StepCaseTests.cs"
Task: "T239 StepAllocationTests in tests/MmoEngine.SimCore.Tests/Allocation/StepAllocationTests.cs"
# Slice 2
Task: "T297 measurement routes in tools/agreement/node/server.test.mjs"
Task: "T298 tools/agreement/lib/passes.mjs"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Complete Phase 1 (Setup) and Phase 2 (Foundational); the Foundational checkpoint blocks every story.
2. Complete Phase 3 (US1): input validation, movement, collision, the test world, the 11 movement streams, `streams record` and `streams verify`.
3. The MVP is reached when `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` and `MovementScenarioTests` are green at 20, 30 and 60 Hz and `streams verify` prints `11 streams, 11 match`.

### Incremental Delivery

1. Setup + Foundational: the formats, config rules, SimMath and the world skeleton.
2. US1 (Phase 3): deterministic movement and the stream tooling (MVP).
3. US2 slice 1 (Phase 4): the batched stepper, perturbation, generated streams, divergence report, cross-build tables and `agreement prepare` / `compare`.
4. US3 (Phase 5): the analyzer enforces the determinism rules on every later phase's code.
5. US4 (Phase 6) then US5 (Phase 7): weapons, then projectiles and the 7 fire streams (18 committed in all).
6. US6 (Phase 8): golden fixtures and the remaining decoder checks.
7. US7 slice 1 (Phase 9): benchmarks, the dry-run export, zero allocation and `measure validate-report`.
8. Polish slice 1 (Phase 10): the first PR, which touches no guarded path and auto-merges.
9. US2 slice 2 (Phase 11), US7 slice 2 (Phase 12) and Polish slice 2 (Phase 13): the WebAssembly host, agreement runners, SimCore workflow and measure job, in the second PR, merged by the session after the automated guarded-path review.

Each step leaves `main` (or the slice branch) green under `.github/workflows/dotnet.yml`.

---

## Delivery

| Slice | Phases | Task IDs | Branch | Guarded paths | PR and merge |
| --- | --- | --- | --- | --- | --- |
| 1 | 1-10 (Setup, Foundational, US1, US2 slice 1, US3, US4, US5, US6, US7 slice 1, Polish) | T001-T263 | `015-shared-simulation-core` | None (no `.github/`, `.routines/`, `.claude/`, `CLAUDE.md`, architecture spec or `infra/`) | One draft PR; marked ready once merge-policy rules 1-4 and 6 hold; `auto-merge.yml` squash-merges it (the session merges only in the 300-file case) |
| 2 | 11-13 (US2 slice 2, US7 slice 2, Polish) | T264-T317 | `015-shared-simulation-core-slice-2`, cut from `main` after slice 1 merges | Yes: `.github/workflows/simcore.yml`, `.github/workflows/auto-merge.yml` | One draft PR; independent automated review (code-review subagent), then the session squash-merges and sends one push notification |

Per phase: Phase 1 (Setup) T001-T012; Phase 2 (Foundational) T013-T061; Phase 3 (US1) T062-T107; Phase 4 (US2 slice 1) T108-T134; Phase 5 (US3) T135-T162; Phase 6 (US4) T163-T179; Phase 7 (US5) T180-T206; Phase 8 (US6) T207-T234; Phase 9 (US7 slice 1) T235-T248; Phase 10 (Polish slice 1) T249-T263; Phase 11 (US2 slice 2) T264-T286; Phase 12 (US7 slice 2) T287-T304; Phase 13 (Polish slice 2) T305-T317.

---

## Scenario coverage

Every acceptance scenario and Independent Test of spec.md, with the task that writes its test and the test (file and `Class.Method`) that covers it. Supporting tests are listed after the owning one.

| Scenario | Task | Test |
| --- | --- | --- |
| US1.1 flat run from rest covers the discrete sum | T076 | `tests/MmoEngine.SimCore.Tests/Scenarios/MovementScenarioTests.cs` `MovementScenarioTests.FlatRunFromRestCoversTheDiscreteSum` |
| US1.2 wall at 45° never entered, parallel speed kept | T077 | `tests/MmoEngine.SimCore.Tests/Scenarios/MovementScenarioTests.cs` `MovementScenarioTests.WallSlideNeverEntersTheWallAndKeepsTheParallelSpeed` |
| US1.3 stair below the step height climbed, above blocked | T078, T069 | `tests/MmoEngine.SimCore.Tests/Scenarios/MovementScenarioTests.cs` `MovementScenarioTests.StairBelowTheStepHeightIsClimbedAndAboveItIsBlocked`; `tests/MmoEngine.SimCore.Tests/Movement/StepUpTests.cs` `StepUpTests.RisesUpToTheStepHeightClimbAndHigherRisesBlock` |
| US1.4 slope above the limit refused | T079, T070 | `tests/MmoEngine.SimCore.Tests/Scenarios/MovementScenarioTests.cs` `MovementScenarioTests.SlopeBelowTheLimitIsClimbedAndAboveItIsRefused`; `tests/MmoEngine.SimCore.Tests/Movement/SlopeTests.cs` `SlopeTests.SlopesBelowTheLimitClimbAndSteeperAreRefused` |
| US1.5 jump leaves with jump speed, lands grounded | T080 | `tests/MmoEngine.SimCore.Tests/Scenarios/MovementScenarioTests.cs` `MovementScenarioTests.JumpLeavesWithJumpSpeedFollowsGravityAndLandsGrounded` |
| US1.6 crouched under a ledge stays crouched | T081 | `tests/MmoEngine.SimCore.Tests/Scenarios/MovementScenarioTests.cs` `MovementScenarioTests.CrouchedUnderALedgeStaysCrouchedUntilThereIsRoomToStand` |
| US1.7 every committed stream identical in two processes | T088 | `tests/MmoEngine.SimCore.Tests/Cli/StreamsCommandTests.cs` `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess` |
| US1.8 20, 30 and 60 Hz streams identical twice | T085 | `tests/MmoEngine.SimCore.Tests/Scenarios/StreamDeterminismTests.cs` `StreamDeterminismTests.StreamForEachRateGivesIdenticalHashesTwice` |
| US1.9 invalid input rejected with its reason, state unchanged | T064, T063 | `tests/MmoEngine.SimCore.Tests/Inputs/InputRejectionTests.cs` `InputRejectionTests.InvalidCommandIsRejectedWithItsReasonAndLeavesTheStateUnchanged`; `tests/MmoEngine.SimCore.Tests/Inputs/InputValidatorTests.cs` `InputValidatorTests.ValidateReportsTheFirstFailureInContractOrder` |
| US1-IT committed streams through the server build twice; stated end positions | T088, T077-T081 | `StreamsCommandTests.EveryCommittedStreamMatchesInAFreshProcess`; `MovementScenarioTests` (US1.2-US1.6 methods above) |
| US2.1 server and Node WebAssembly bit-identical on every committed stream | T269 | `tools/agreement/node/agreement.test.mjs` `test('<stream name>')` per manifest stream, run as `node-interp` and `node-interp-nojit`, gated by `simcore agreement compare` |
| US2.2 same streams bit-identical in a dedicated Web Worker | T274 | `tools/agreement/specs/worker.spec.mjs` `'every stream, batch and table matches the server in a dedicated Worker'` |
| US2.3 50 generated seeds agree across builds | T270, T118 | `tools/agreement/node/agreement.test.mjs` `'manifest holds the 50 generated seeds'`; `tests/MmoEngine.SimCore.Tests/Agreement/StreamGeneratorTests.cs` `StreamGeneratorTests.GeneratedStreamsCoverEveryButtonAndTheAimRange` |
| US2.4 a one-step difference reported at its first step and field | T115, T121 | `tests/MmoEngine.SimCore.Tests/Agreement/DivergenceReportTests.cs` `DivergenceReportTests.PerturbedRecordsReportTheFirstStepAndField`; `tests/MmoEngine.SimCore.Tests/Cli/AgreementCommandTests.cs` `AgreementCommandTests.CompareExitsOneOnADivergedBuildAndWritesItsReport` |
| US2.5 another format version is not comparable | T117, T121 | `tests/MmoEngine.SimCore.Tests/Agreement/NotComparableTests.cs` `NotComparableTests.AnotherFormatVersionOrRulesRevisionIsNotComparableNotDiverged`; `AgreementCommandTests.WorkDirectoryFromAnotherRulesRevisionIsNotComparable` |
| US2.6 batched step equals one at a time | T111, T273 | `tests/MmoEngine.SimCore.Tests/Batch/BatchStepperTests.cs` `BatchStepperTests.QueuedCommandsEqualOneAtATime`; `tools/agreement/node/agreement.test.mjs` `'batch inputs through StepQueued equal RunStream'` |
| US2.7 CI runs the agreement suite and fails on divergence | T275, T277, T278 | `tests/workflows/test_simcore_workflow.py` `TriggersAndPaths.test_runs_on_pull_requests_and_main_for_simcore_paths`, `AgreementJob.test_compare_gates_the_job`, `AutoMerge.test_auto_merge_waits_for_simcore` |
| US2-IT agreement suite over committed and generated streams across builds | T271, T277 | the `agreement` job of `.github/workflows/simcore.yml`; `tools/agreement/node/agreement.test.mjs` `'perturbation diverges at exactly its step'`; `AgreementJob.test_prepare_writes_fifty_seeds_to_a_fresh_work_directory` |
| US3.1 System.Numerics vector types fail the build | T136 | `tests/MmoEngine.SimCore.Analyzers.Tests/RuleTests.cs` `RuleTests.EachSampleProducesExactlyItsRule` (MMO1001 rows) |
| US3.2 platform Math/MathF outside the allowlist fails | T137 | `RuleTests.EachSampleProducesExactlyItsRule` (MMO1002 rows); `RuleTests.PlatformMathMessageNamesTheReplacement` |
| US3.3 fused multiply-add fails | T138 | `RuleTests.EachSampleProducesExactlyItsRule` (MMO1003 rows); `RuleTests.FusedCallsReportOnlyTheFmaRule` |
| US3.4 hash-ordered enumeration fails | T139 | `RuleTests.EachSampleProducesExactlyItsRule` (MMO1004 rows) |
| US3.5 clocks, randomness and hash seeds fail | T140 | `RuleTests.EachSampleProducesExactlyItsRule` (MMO1005 rows) |
| US3.6 locks and blocking fail | T141 | `RuleTests.EachSampleProducesExactlyItsRule` (MMO1006 rows) |
| US3.7 double/Half/nint, float-to-int, ulong-to-float and platform paths fail | T142, T143, T144 | `RuleTests.EachSampleProducesExactlyItsRule` (MMO1007, MMO1008, MMO1009 rows); `RuleTests.SimMathMayClampAndCastFloats`; `RuleTests.ConversionMessagesGiveTheirReason` |
| US3.8 (a) trig within documented bounds on the server build | T152 | `tests/MmoEngine.SimCore.Tests/Math/FixedTableBoundsTests.cs` `FixedTableBoundsTests.EveryInDomainRowIsWithinItsBound` |
| US3.8 (b) server and WebAssembly bit-identical on the fixed table | T272, T274 | `tools/agreement/node/agreement.test.mjs` `'cross-build tables match the server'`; `tools/agreement/specs/worker.spec.mjs` `tables` assertion |
| US3-IT violating and allowed samples; a violation fails the build | T149, T150 | `tests/MmoEngine.SimCore.Analyzers.Tests/BackstopTests.cs` `BackstopTests.CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes`; `tests/MmoEngine.SimCore.Analyzers.Tests/BuildTests.cs` `BuildTests.ABuildWithAViolationFails` |
| US4.1 700 rpm at 30 Hz fires on the right steps | T167 | `tests/MmoEngine.SimCore.Tests/Weapons/FireTimingTests.cs` `FireTimingTests.SevenHundredRpmAtThirtyHzFiresExactly35ShotsInThreeSeconds` |
| US4.2 same salt, player and counter give the same spread | T164 | `tests/MmoEngine.SimCore.Tests/Weapons/SpreadTests.cs` `SpreadTests.SameSaltPlayerAndCounterGiveTheSameDirectionBits`, `ADifferentShotCounterChangesTheDirection`, `DirectionsFallInsideTheConeWithAUniformDistribution` |
| US4.3 two-round magazine fires twice, reports empty, reloads | T170 | `tests/MmoEngine.SimCore.Tests/Scenarios/WeaponScenarioTests.cs` `WeaponScenarioTests.TwoRoundMagazineFiresTwiceReportsEmptyThenReloadsFromReserve` |
| US4.4 heat weapon overheats and resumes at the recovery level | T171, T172 | `WeaponScenarioTests.HeatWeaponOverheatsAndResumesAtTheRecoveryLevel`; `WeaponScenarioTests.HeatWeaponStopsAtTheThresholdAndResumesAtTheRecoveryLevel` |
| US4.5 each shot kicks recoil and it recovers | T173 | `WeaponScenarioTests.EachShotKicksTheAimAndTheOffsetRecoversAfterFiring` |
| US4.6 fire when the weapon can't fire reports the reason | T174 | `WeaponScenarioTests.FireWhenTheWeaponCannotFireReportsTheReason`; `WeaponScenarioTests.ReloadAndEmptyStreamReportsReloadingAndSwitchingBlocks` |
| US4-IT every weapon fires on the configured steps | T175 | `WeaponScenarioTests.EveryWeaponFiresOnTheStepsItsConfigurationImplies` |
| US5.1 horizontal shot follows the closed form, hits terrain first | T193, T194 | `tests/MmoEngine.SimCore.Tests/Scenarios/ProjectileScenarioTests.cs` `ProjectileScenarioTests.HorizontalShotFollowsTheClosedFormAndHitsTheTerrainWhereItFirstMeetsIt`; `ProjectileScenarioTests.LauncherArcMeetsTheTerrainAtTheClosedFormStep` |
| US5.2 target in the path: struck hitbox, point and step | T187, T197 | `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileHitTests.cs` `ProjectileHitTests.TargetInThePathReportsTheStruckHitboxPointAndStep`; `ProjectileScenarioTests.ProjectileHitsHitboxStrikesEveryPlannedPart` |
| US5.3 mesh between muzzle and target is hit instead | T188, T195 | `ProjectileHitTests.MeshBetweenMuzzleAndTargetIsHitInstead`; `ProjectileScenarioTests.MeshOccludesShotHitsTheWallAndNeverTheTarget` |
| US5.4 past range or lifetime expires without a hit | T185 | `tests/MmoEngine.SimCore.Tests/Projectiles/ProjectileExpiryTests.cs` `ProjectileExpiryTests.ProjectilePastItsLimitExpiresAtThatStepWithoutAHit`; `ProjectileExpiryTests.RifleBurstShotsAboveTheHorizonExpireWithRange` |
| US5.5 (a) hitboxes follow the stance's layout | T196, T033 | `ProjectileScenarioTests.StruckPartsMatchTheStancesLayout`; `tests/MmoEngine.SimCore.Tests/Hitboxes/HitboxBuilderTests.cs` `HitboxBuilderTests.WorldPointsFollowTheYawRotation` |
| US5.5 (b) hitboxes bit-identical in every build | T272, T113 | `tools/agreement/node/agreement.test.mjs` `'cross-build tables match the server'` (6,192 pose rows); `tests/MmoEngine.SimCore.Tests/Batch/CrossBuildTablesTests.cs` `CrossBuildTablesTests.EachRowEqualsTheDirectCall` |
| US5.6 crossing a whole hitbox in one step still hits | T189 | `ProjectileHitTests.ProjectileCrossingAWholeHitboxInOneStepStillHits` |
| US5-IT shots at known distances hit where the closed form says | T198 | `ProjectileScenarioTests.ShotsAtKnownDistancesHitWhereTheClosedFormPathSays` |
| US6.1 every golden fixture round-trips to identical bytes | T208 | `tests/MmoEngine.SimCore.Tests/Formats/GoldenFixtureTests.cs` `GoldenFixtureTests.EachValidFixtureRoundTripsToItsBytes` |
| US6.2 unknown version refused naming both versions | T210 | `tests/MmoEngine.SimCore.Tests/Formats/GoldenRefusalTests.cs` `GoldenRefusalTests.UnknownVersionIsRefusedNamingTheVersionFoundAndSupported` |
| US6.3 truncated, oversized or inconsistent refused at offset and field | T211, T212, T219 | `GoldenRefusalTests.MalformedBytesAreRefusedAtTheirOffsetAndFieldWithNoResult`; `GoldenRefusalTests.ContractMessagesAreReproducedExactly`; `tests/MmoEngine.SimCore.Tests/Properties/DecoderPropertyTests.cs` `DecoderPropertyTests.TruncatedOrCorruptedFixturesNeverYieldAPartialResult` |
| US6.4 checksum mismatch refused | T213 | `GoldenRefusalTests.ChecksumMismatchIsRefused` |
| US6.5 missing collision data refused before step 0 | T214, T215 | `GoldenRefusalTests.StreamWithoutItsCollisionDataIsRefusedBeforeStepZero`; `GoldenRefusalTests.OtherOpenRefusalsHappenBeforeStepZero` |
| US6-IT encode/decode every fixture; malformed refused | T208, T217 | `GoldenFixtureTests.EachValidFixtureRoundTripsToItsBytes`, the `GoldenRefusalTests` above; `tests/MmoEngine.SimCore.Tests/Cli/GoldenCommandTests.cs` `GoldenCommandTests.GoldenWriteReproducesTheCommittedFixtures` |
| US7.1 server step benchmarks report mean and P99 | T237 | `tests/MmoEngine.SimCore.Tests/Benchmarks/ServerBenchmarkExportTests.cs` `ServerBenchmarkExportTests.DryRunExportsAValidServerObject` |
| US7.2 WebAssembly report records sizes, cold start and step cost per mode | T287, T292, T294, T295, T235 | the `measure` job's "Validate the measurement report" step, pinned by `tests/workflows/test_simcore_workflow.py` `MeasureJob.test_measure_job_measures_every_mode_and_validates_the_report`; `tools/agreement/node/measure.test.mjs` `'parses the boot manifest from dotnet.js'`, `'download totals are the exact sums'`, `'an unproduced mode is only its reason'`; validator half `tests/MmoEngine.SimCore.Tests/Cli/ValidateReportTests.cs` `ValidateReportTests.EachProblemIsReported` |
| US7.3 warmed-up steps allocate zero bytes | T239 | `tests/MmoEngine.SimCore.Tests/Allocation/StepAllocationTests.cs` `StepAllocationTests.WarmedUpStepsAllocateNothing` |
| US7-IT measurement job report on the runner class; zero-allocation tests pass | T288, T239 | the `measure` job on `gha-ubuntu-latest-x64`; `MeasureJob.test_runner_class_and_pinned_intrinsics_are_set`; `StepAllocationTests.WarmedUpStepsAllocateNothing` in dotnet.yml |
| SC-001 committed and 50 generated streams agree in every build | T269, T277 | `agreement compare` in the `agreement` job; `AgreementJob.test_prepare_writes_fifty_seeds_to_a_fresh_work_directory` |
| SC-002 a one-ulp change is reported at its first divergent step | T109, T271 | `tests/MmoEngine.SimCore.Tests/Batch/StreamRunnerTests.cs` `StreamRunnerTests.PerturbationDivergesAtExactlyThatStep`; `agreement.test.mjs` `'perturbation diverges at exactly its step'` |
| SC-003 every rule fails its sample, silent on allowed code, not suppressible | T136, T146, T149 | `RuleTests.EachSampleProducesExactlyItsRule`; `tests/MmoEngine.SimCore.Analyzers.Tests/SuppressionTests.cs` `SuppressionTests.SeverityCannotBeLowered`; `BackstopTests.CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes` |
| SC-004 trig within bounds (bound half) and bit-identical (bit half) | T152, T272 | `FixedTableBoundsTests.EveryInDomainRowIsWithinItsBound`; `agreement.test.mjs` `'cross-build tables match the server'` |
| SC-005 steady-state steps allocate zero bytes | T239, T240 | `StepAllocationTests.WarmedUpStepsAllocateNothing`; `BatchAllocationTests.StepQueuedAllocatesNothingAfterWarmUp`; `InputAllocationTests.DecodeAndValidateAllocateNothing`; `HashAllocationTests.StateHashAllocatesNothing` |
| SC-006 agreement suite within 10 minutes | T277 | `AgreementJob.test_agreement_job_times_out_after_ten_minutes` |
| SC-007 report records every measurement or the reason | T289, T287 | `MeasureJob.test_unproduced_modes_are_recorded_with_their_reason`; the `measure` job's validate step |
| Edge: edge, vertex or concave corner never tunnels or oscillates | T072, T082 | `tests/MmoEngine.SimCore.Tests/Movement/CornerTests.cs` `CornerTests.StartsOnATriangleEdgeOrVertexNeverTunnelOrOscillate`; `MovementScenarioTests.CornerWedgeNeverTunnelsOrOscillates` |
| Edge: a step longer than the capsule radius is subdivided | T083 | `MovementScenarioTests.FastMoveIntoAThinWallIsSubdividedAndNeverPassesThrough` |
| Edge: pushed out of bounds is clamped and reported | T073 | `tests/MmoEngine.SimCore.Tests/Movement/BoundsTests.cs` `BoundsTests.CharacterPushedPastTheEdgeIsClampedAndReported` |
| Edge: degenerate triangles load and are ignored | T031 | `tests/MmoEngine.SimCore.Tests/Collision/CollisionWorldBuildTests.cs` `CollisionWorldBuildTests.DegenerateTrianglesLoadAndAreIgnored` |
| Edge: NaN or infinity refuses the step naming the field | T035 | `tests/MmoEngine.SimCore.Tests/World/CanonicalisationTests.cs` `CanonicalisationTests.NonFiniteFieldRefusesTheStepAndKeepsThePreviousState` |
| Edge: negative zero canonicalised before hashing | T035 | `CanonicalisationTests.NegativeZeroIsCommittedAndHashedAsPositiveZero` |
| Edge: fire interval shorter than one step | T169 | `FireTimingTests.WeaponFasterThanOneStepFiresAtMostMaxShotsPerStepWithoutDrift` |
| Edge: point-blank hit at the first step | T190 | `ProjectileHitTests.ProjectileStartingInsideAHitboxHitsAtItsFirstStep` |
| Edge: region merges, splits, portals, tick overruns | n/a | N/A by design (spec Edge Cases); no test |

---

## Notes

- [P] tasks touch different files and depend on no unfinished task in their phase.
- [Story] labels map each task to its user story for traceability; Phase 11 tasks are [US2] and Phase 12 tasks [US7], including the cross-build halves of US3.8 and US5.5 they own.
- Write each test first and see it fail (the command is in the task); a compile failure because the type does not exist counts as red.
- Tests are parallel-safe and rerun-safe: unique temp directories, `simcore/` opened read-only, fixed seeds printed on failure, Theories for data variations. Serial collections only where the task states the reason (zero-allocation counters, the benchmark dry run, the shared Wasm stepper). The one deliberate skip is `Math/ExhaustiveSweepTests`, which runs only with `MMO_EXHAUSTIVE_MATH=1` and owns no scenario.
- Commit after each task or logical group; commit `.mmrs` files with their `REGENERATIONS.md` entry, and golden `.json` with its `.bin`.
- At any checkpoint, its automated checks decide whether the phase is done; continue when they are green.
- Avoid vague tasks, two unfinished tasks on one file marked [P], and assertions that only prove "does not throw".
