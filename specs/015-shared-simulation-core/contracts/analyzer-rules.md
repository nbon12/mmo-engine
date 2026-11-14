# Contract: Determinism analyzer rules

`DeterminismAnalyzer` (`src/MmoEngine.SimCore.Analyzers/`) enforces FR-012 and the constitution's
no-blocking rule (Principle II) on the core assembly, `src/MmoEngine.SimCore/`, and the build fails on
any of its diagnostics (FR-014, SC-003). The rationale and the measurements behind each rule are in
[research.md](../research.md) R4 and R5.

The analyzer is attached to the core only. It does not run on the server helpers, the CLI, the
WebAssembly host, the benchmarks or the tests, which may use `double`, `Stopwatch` and locks freely.

## Rules

Every rule is severity Error, enabled by default, category `Determinism`, and carries
`WellKnownDiagnosticTags.NotConfigurable`.

| ID | Title | Reports | Allowed |
| --- | --- | --- | --- |
| MMO1001 | Vector or SIMD type outside the lane-wise allowlist | Any use or declaration of `System.Numerics` `Vector<T>`, `Vector`, `Vector2`, `Vector3`, `Vector4`, `Quaternion`, `Matrix3x2`, `Matrix4x4` or `Plane`, including as a type argument, array element, pointer target, return type, parameter, local or `typeof` operand. Every member of `System.Runtime.Intrinsics` and its sub-namespaces that is not on the [Vector128 allowlist](#vector128-allowlist-mmo1001): the X86, Arm and Wasm ISA classes, `Vector64`, `Vector256`, `Vector512`, `IsHardwareAccelerated`, `IsSupported`, the horizontal `Sum` and `Dot`, and every `*Estimate` and `*Native` member. A `Vector128<T>` whose element type is not `float`. | `Vector128<float>` with the allowlisted members. The core's own scalar types (`Vec3`). |
| MMO1002 | Platform math outside the allowlist | Static methods of `System.Math`, `System.MathF`, `System.Single`, `System.Double`, `System.Half` and `System.Runtime.InteropServices.NFloat`, and static members of the generic-math interfaces in `System.Numerics` (so `T.Cos(x)` is caught), that are not on the [math allowlist](#math-allowlist-mmo1002). Invocations, method groups and function pointers (`&MathF.Sin`) all count. The `%` operator (and `%=`) on `float` operands. | The math allowlist, the constants, and `nameof(MathF.Sin)`. |
| MMO1003 | Fused multiply-add | Any member whose name starts with `FusedMultiply`, or is `MultiplyAddEstimate`, on any type (`Math`, `MathF`, `float`, `Vector128`, `AdvSimd` and the rest), and every member of `System.Runtime.Intrinsics.X86.Fma` and `Fma.X64`. | `a * b + c` written as two float operations. |
| MMO1004 | Iteration over a hash-ordered collection | Enumerating a [hash-ordered collection](#hash-ordered-collections-mmo1004): `foreach`; `GetEnumerator`, `CopyTo` or `ToArray`; `Keys` or `Values` whose result is not itself hash-ordered; a `..` spread; and any conversion of the collection to another type (LINQ receivers, query syntax, `string.Join`, `new List<T>(set)`, `AddRange`, returning it as `IEnumerable<T>`). | Lookups: `TryGetValue`, `ContainsKey`, `Contains`, the indexer, `Count`, `Keys.Count`. Order-insensitive sinks on hash types (`UnionWith`, a hash type's copy constructor). `SortedDictionary`, `SortedSet`, arrays and `List<T>`. |
| MMO1005 | Ambient time, randomness or a per-process hash seed | The [ambient list](#ambient-time-randomness-and-hash-seeds-mmo1005): wall-clock and monotonic clocks, every timer, all of `System.Random` (seeded too), `Guid.NewGuid`, `RandomNumberGenerator`, and hashing seeded per process (`HashCode`, `string.GetHashCode`, a `GetHashCode` that is not overridden, `RuntimeHelpers.GetHashCode`). | The step number and step length passed in by the caller. The core's seeded spread hash (SplitMix64). `int.GetHashCode()` and other overrides that return the value's bits. |
| MMO1006 | Lock or blocking synchronization | Every `lock` statement, including on `System.Threading.Lock`. The [blocking list](#locks-and-blocking-mmo1006): `Monitor`, `Mutex`, the semaphores, reader-writer locks, spin primitives, wait handles, `Barrier`, `BlockingCollection`, `Parallel`, `Thread.Sleep`/`Join`/`SpinWait`/`Yield`, `Task.Wait`/`WaitAll`/`WaitAny`/`RunSynchronously`, `Task<T>.Result`, `ValueTask<T>.Result`, `GetResult` on every awaiter, and `[MethodImpl(MethodImplOptions.Synchronized)]`. | `await`, `Interlocked` and `Volatile`; none of them blocks. |
| MMO1007 | Non-float floating point or platform-sized number | Declarations of `double`, `Half`, `NFloat`, `nint` or `nuint` (fields, properties, events, parameters, return types, locals, type arguments, array elements), and any non-constant expression of those types. An untyped literal in a float expression (`x * 0.5`) is one. | `const double` fields and constant-folded expressions such as `(float)(Math.PI / 180.0)`. `int` and `long` for fixed point and counters. `decimal` is not checked. |
| MMO1008 | Platform query, conditional compilation or reflection | `#if` and `#elif` directives. `[Conditional]` declarations and calls to `System.Diagnostics.Debug` and `Trace`. `OperatingSystem`, `RuntimeInformation`, `RuntimeFeature`. Every member of `System.Environment` except the two clocks (MMO1005). `IntPtr.Size`, `UIntPtr.Size`, `BitConverter.IsLittleEndian`. Members of `System.Type`, every type in `System.Reflection`, `Activator`. Every `dynamic` operation. `[UnsafeAccessor]` declarations. | One code path for every build. `typeof(T)` and `nameof` without member access. |
| MMO1009 | Float-to-integer or ulong-to-float conversion outside the SimMath helpers | A non-constant conversion, explicit or implicit, `checked` or not, from `float` or `double` to any integer type (`sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `char`), outside the bodies of the three `MmoEngine.SimCore.Math.SimMath` helpers. Every member of `System.Convert`. A non-constant conversion from `ulong` to `float`, anywhere in the core, `SimMath` included. | `SimMath.FloorToInt`, `SimMath.RoundToInt` and `SimMath.TruncateToInt`, which saturate before the cast (NaN → 0, ≥ 2^31 → `int.MaxValue`, < −2^31 → `int.MinValue`). Constant casts such as `(int)2.5f`. Integer-to-float conversions from `int`, `uint`, `long`, `short` and `byte`, which round correctly in every build. |

US3 scenario 1 lists `Vector128.Sum` and `Vector128.Dot` as its horizontal samples. A fused
`Vector128.FusedMultiplyAdd` belongs to MMO1003 (US3 scenario 3), by the precedence below.

## Messages

`{0}` is the member as `ToDisplayString` shows it (for example `MathF.Sin(float)`), or the source
text for a conversion, `foreach`, spread, directive or declaration. Every message names the allowed
alternative (US3).

| ID | Message format | `{1}` |
| --- | --- | --- |
| MMO1001 | `'{0}' uses '{1}', whose results can differ between the server and WebAssembly builds; use the core's scalar float types, or lane-wise Vector128<float> arithmetic from the allowlist` | the vector or intrinsic type |
| MMO1002 | `'{0}' is platform math, which is not correctly rounded and can differ between builds; use {1}` | the [replacement](#replacement-map-mmo1002-messages) |
| MMO1003 | `'{0}' fuses (or may fuse) a multiply and an add, which rounds differently from 'a * b + c'; write the multiply and the add as separate float operations` | unused |
| MMO1004 | `'{0}' enumerates '{1}', whose order is not defined; iterate a sorted array or List<T> kept alongside it, or a SortedDictionary or SortedSet` | the collection type |
| MMO1005 | `'{0}' reads ambient time, randomness or a per-process hash seed; take the step number and step length from the caller, and draw randomness from the core's seeded spread hash` | unused |
| MMO1006 | `'{0}' takes a lock or blocks, which gameplay code must not do (constitution Principle II); the core is a pure step function: take state in and return state out` | unused |
| MMO1007 | `'{0}' uses '{1}'; the core computes in 'float' only, and in 'int' or 'long' for fixed point and counters, so every build rounds and overflows the same way` | the banned type |
| MMO1008 | `'{0}' lets the server and WebAssembly builds take different paths; the core has one code path for every build` | unused |
| MMO1009 | `'{0}' {1}` | the reason, below |

MMO1009's reason text:

| Conversion | `{1}` |
| --- | --- |
| `float` or `double` to an integer, or `System.Convert` | `converts a floating-point value to an integer, which gives different results between builds for NaN and out-of-range values; use 'SimMath.FloorToInt', 'SimMath.RoundToInt' or 'SimMath.TruncateToInt', which saturate first` |
| `ulong` to `float` | `converts a 'ulong' to 'float', which the WebAssembly build rounds through 'double' and so can differ by one ulp; narrow it to 'uint' or 'long' first` |

The exemption is per method, not per type: the three helpers are the only places a float-to-integer
cast is allowed ([research.md](../research.md) R3 and R4). The trigonometric range reduction
converts its in-range quadrant count with `SimMath.RoundToInt`, not with a raw cast. The spread hash
converts its 24-bit `ulong` value as `(float)(uint)(h >> 40)`.

The diagnostic's location is the operation's syntax: the invocation, member access or conversion
expression; the `lock` keyword for a `lock` statement; the collection expression for `foreach` and
spreads; the declared identifier for a declaration; the directive line for `#if`.

## Precedence and deduplication

- **One diagnostic per operation.** When an operation matches more than one rule, it reports the
  first in this order:

  `MMO1003` > `MMO1001` > `MMO1002` > `MMO1008` > `MMO1005` > `MMO1006` > `MMO1004` > `MMO1009` > `MMO1007`

  So `Vector128.FusedMultiplyAdd` and `Fma.MultiplyAdd` report MMO1003 and not MMO1001,
  `Vector128.IsHardwareAccelerated` reports MMO1001 and not MMO1008, and `Math.Sin` reports MMO1002
  for the call even though the call's type is `double`.
- **No repeats inside one expression.** An operation is skipped when an enclosing operation in the
  same statement already reports the same rule. `Vector3.Dot(new Vector3(x), Vector3.UnitX)` gives
  one MMO1001, not three. A field or property initializer counts as its declaration.
- **Different rules in one expression still report.** `(float)Math.Sin(x)` gives MMO1002 for the call
  and MMO1007 for the implicit conversion of `x` to `double`.
- **Declarations report separately** from the operations inside them. `double M(float x) => x` gives
  two MMO1007: the return type and the conversion.

## Allowlists and tables

The tables live in the analyzer's source as `ImmutableHashSet<string>` keyed by the original
definition's metadata name (``Namespace.Type`1+Nested`` plus the member name). Where an entry needs one
overload, the key is the documentation comment ID (`ISymbol.GetDocumentationCommentId()`, for example
`M:System.MathF.Round(System.Single)`). The core cannot widen its own rules: there is no
`AdditionalFiles` list, and the analyzer tests pin every table.

### Vector128 allowlist (MMO1001)

Allowed only with element type `float`. Any other `Vector128<T>` is MMO1001.

| Type | Allowed members |
| --- | --- |
| `System.Runtime.Intrinsics.Vector128` | `Create`, `CreateScalar`, `Add`, `Subtract`, `Multiply`, `Divide`, `Negate`, `Sqrt`, `Abs`, `BitwiseAnd`, `BitwiseOr`, `Xor`, `AndNot`, `OnesComplement`, `ConditionalSelect`, `Equals`, `LessThan`, `LessThanOrEqual`, `GreaterThan`, `GreaterThanOrEqual`, `GetElement`, `WithElement`, `ToScalar`, `CopyTo` |
| `System.Runtime.Intrinsics.Vector128<T>` | `Zero`, `One`, `AllBitsSet`, `Count`, the indexer (`Item`), and the operators `+`, `-`, `*`, `/`, unary `-`, unary `+`, `&`, `\|`, `^`, `~`, `==`, `!=` |

Everything else on these types is MMO1001. That includes `Sum`, `Dot`, `Min`, `Max`, `MinNative`,
`MaxNative`, every `*Estimate`, `CreateScalarUnsafe`, `LoadUnsafe`, `IsHardwareAccelerated`,
`Vector128<T>.IsSupported`, instance `Equals`, and `GetHashCode` (which uses the per-process
`HashCode` seed). `FusedMultiplyAdd` is MMO1003.

### Math allowlist (MMO1002)

Allowed on `Math`, `MathF` and `float` (`System.Single`) with `float` operands:

| Members | Note |
| --- | --- |
| `Sqrt`, `Abs`, `CopySign`, `Floor`, `Ceiling`, `Truncate` | IEEE 754 exact or correctly rounded |
| `Round(x)` and `Round(x, MidpointRounding)` | ties to even by default; matched by documentation ID, so `Round(x, digits)` is not allowed |
| `BitIncrement`, `BitDecrement`, `ScaleB`, `ILogB` | bit and exponent operations |
| `Clamp` | inside `MmoEngine.SimCore.Math.SimMath` only; elsewhere it reports MMO1002 naming `SimMath.Clamp` |
| every `float.Is*` predicate | `IsNaN`, `IsFinite`, `IsInfinity`, `IsNegative`, `IsNormal`, `IsSubnormal` and the rest |
| `PI`, `E`, `Tau`, and the `float` constants | constants are never reported |

Allowed on `Math` with integer operands, everywhere: `Min`, `Max`, `Abs`, `Clamp`, `BigMul`, `DivRem`.

`BitConverter` bit casts (`SingleToInt32Bits`, `Int32BitsToSingle`, `SingleToUInt32Bits`,
`UInt32BitsToSingle`) and `BinaryPrimitives` are not math host types and are allowed.
`BitConverter.IsLittleEndian` is MMO1008, and the `double` casts are MMO1007.

Everything else on the math host types is MMO1002. Besides the transcendental functions (`Sin`,
`Cos`, `Tan`, `SinCos`, `Asin`, `Acos`, `Atan`, `Atan2`, the hyperbolics, `Exp*`, `Log*`, `Pow`,
`Cbrt`, `RootN`, `Hypot`, the `*Pi` functions), that includes:

- `Min`, `Max`, `MinMagnitude`, `MaxMagnitude`, `MinNumber` and `MaxNumber` on floats, because which
  NaN they return differs between x64 and WebAssembly [measured];
- `ReciprocalEstimate` and `ReciprocalSqrtEstimate`, which gave three answers in three builds
  [measured];
- `Round(x, digits)` and `Round(x, digits, mode)`, which scale by powers of ten;
- `Lerp`, `DegreesToRadians` and `RadiansToDegrees`, whose implementation may change;
- `IEEERemainder`, `Sign`, `Parse` and `ToString`, which the core does not need;
- float `%`.

### Replacement map (MMO1002 messages)

| Banned member | `{1}` in the message |
| --- | --- |
| `Sin`, `Cos`, `Tan`, `SinCos`, `Asin`, `Acos`, `Atan`, `Atan2` | `'SimMath.Sin'`, `'SimMath.Cos'`, and so on, by the same name |
| `SinPi`, `CosPi`, `SinCosPi` | `'SimMath.SinCosBinaryAngle' for binary angles` |
| `Min`, `MinMagnitude`, `MinNumber` | `'SimMath.Min'` |
| `Max`, `MaxMagnitude`, `MaxNumber` | `'SimMath.Max'` |
| `Clamp` outside `SimMath` | `'SimMath.Clamp'` |
| `ReciprocalEstimate` | `'1f / x'` |
| `ReciprocalSqrtEstimate` | `'1f / SimMath.Sqrt(x)'` |
| `Round(x, digits)` | `'MathF.Round(x)' on a scaled value, or 'SimMath.RoundToInt'` |
| `Lerp` | `'a + ((b - a) * t)' written as separate float operations` |
| `DegreesToRadians`, `RadiansToDegrees` | `a multiplication by a float constant` |
| anything else, and float `%` | `scalar float arithmetic, or add a core function with a documented error bound` |

### Hash-ordered collections (MMO1004)

`Dictionary<,>` with its `KeyCollection` and `ValueCollection`, `HashSet<>`, `Hashtable`,
`ConcurrentDictionary<,>`, `ConcurrentBag<>`, `ImmutableDictionary<,>`, `ImmutableHashSet<>`,
`FrozenDictionary<,>`, `FrozenSet<>`, `ReadOnlyDictionary<,>` with its key and value collections,
`HybridDictionary`, `ConditionalWeakTable<,>`, and the interfaces `IDictionary<,>`,
`IReadOnlyDictionary<,>`, `ISet<>`, `IReadOnlySet<>` and `IDictionary`.

The enumerating members are `GetEnumerator`, `CopyTo`, `ToArray`, `Keys` and `Values`. `Keys` and
`Values` report only when their result is not itself on the list; otherwise the later enumeration of
that result reports.

### Ambient time, randomness and hash seeds (MMO1005)

- Whole types: `Stopwatch`, `TimeProvider`, `Random`, `RandomNumberGenerator`, `HashCode`,
  `System.Threading.Timer`, `PeriodicTimer`, `System.Timers.Timer`.
- Members: `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`,
  `DateTimeOffset.UtcNow`, `Environment.TickCount`, `Environment.TickCount64`, `Guid.NewGuid`,
  `Guid.CreateVersion7`, `string.GetHashCode`, `object.GetHashCode` and `ValueType.GetHashCode` when
  the receiver's type does not override them, `RuntimeHelpers.GetHashCode`, `Task.Delay`.

### Locks and blocking (MMO1006)

- Whole types: `Monitor`, `Lock`, `Mutex`, `Semaphore`, `SemaphoreSlim`, `ReaderWriterLock`,
  `ReaderWriterLockSlim`, `SpinLock`, `SpinWait`, `ManualResetEvent`, `ManualResetEventSlim`,
  `AutoResetEvent`, `EventWaitHandle`, `WaitHandle`, `CountdownEvent`, `Barrier`,
  `BlockingCollection<>`, `Parallel`.
- Members: `Thread.Sleep`, `Thread.Join`, `Thread.SpinWait`, `Thread.Yield`, `Task.Wait`,
  `Task.WaitAll`, `Task.WaitAny`, `Task.RunSynchronously`, `Task<T>.Result`, `ValueTask<T>.Result`,
  and `GetResult` on `TaskAwaiter`, `TaskAwaiter<T>`, `ValueTaskAwaiter`, `ValueTaskAwaiter<T>` and the
  four configured awaiters.
- Every `lock` statement, and every method whose implementation flags include `Synchronized`.

### Numeric types (MMO1007)

`double`, `System.Half`, `System.Runtime.InteropServices.NFloat`, `nint` (`IntPtr`) and `nuint`
(`UIntPtr`). `nint` and `NFloat` are 4 bytes on wasm32 and 8 bytes on x64 [measured]. `double` is
deterministic for `+ − × ÷ √`, but FR-012 keeps it out: untyped literals silently create doubles,
they bring in the `Math.*(double)` surface, and the state hash assumes float32 fields.

### Generated code

`ConfigureGeneratedCodeAnalysis(Analyze | ReportDiagnostics)`. Roslyn treats files named `*.g.cs`
and files with an `<auto-generated>` header as generated and skips them by default. Without this
flag, the committed `SimMathCoefficients.g.cs` and `TwoOverPi.g.cs` would escape the rules, and so
would any source generator's output.

### Not covered

The agreement suite remains the second line of defence for what the rules cannot see. Known gaps:
`EqualityComparer<string>.Default.GetHashCode` and other indirect routes to a per-process seed, and
library code outside the core that the core calls through the BCL. `async` and `await` are allowed,
because they do not block; the core has no reason to use them.

## Severity and suppression

All of these were tried against the prototype [measured].

| Mechanism | Hides an MMO error? |
| --- | --- |
| `#pragma warning disable MMO1002` | No |
| `.editorconfig` or `.globalconfig` `dotnet_diagnostic.MMO1002.severity = none` | No |
| `-p:NoWarn=MMO1002` or `<NoWarn>` | No |
| `TreatWarningsAsErrors=false` | No |
| `[SuppressMessage("Determinism", "MMO1002")]` on a member or the assembly | Yes, in the build |
| `-p:RunAnalyzers=false` or `RunAnalyzersDuringBuild=false` | Yes, in the build |
| A Roslyn reference newer than the SDK's compiler (CS9057) without `-warnaserror` | Yes: the analyzer silently does not load |

The backstop test closes the last three. `CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes`
(`tests/MmoEngine.SimCore.Analyzers.Tests/`) compiles every `src/MmoEngine.SimCore/**/*.cs` file
(excluding `bin/` and `obj/`) in the test process, against `Basic.Reference.Assemblies.Net100` and the
`System.IO.Hashing` assembly, with the core's options (nullable on, unsafe off, latest language
version). It runs a `DeterminismAnalyzer` instance through `CompilationWithAnalyzers` with
`reportSuppressedDiagnostics: true`, and asserts zero compiler errors and zero analyzer diagnostics,
suppressed ones included. The core sets `ImplicitUsings=disable`, so its sources compile in the test
exactly as they do in the build. Because the test runs the analyzer itself, it does not depend on the
build having loaded it. With an assembly-level `[SuppressMessage]`, the prototype's core built, and
the backstop failed and printed the diagnostic marked `[suppressed]` [measured].

## Wiring

The core attaches the analyzer as an analyzer only:

```xml
<!-- src/MmoEngine.SimCore/MmoEngine.SimCore.csproj -->
<PropertyGroup>
  <ImplicitUsings>disable</ImplicitUsings>
</PropertyGroup>
<ItemGroup>
  <ProjectReference Include="..\MmoEngine.SimCore.Analyzers\MmoEngine.SimCore.Analyzers.csproj"
                    OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />
</ItemGroup>
```

- **It does not flow to consumers.** NuGet drops `ReferenceOutputAssembly="false"` references from the
  restore graph, so the core's `project.assets.json` lists no project references [measured]. A
  consumer that used `lock`, `Math.Sin` and `DateTime.UtcNow` built clean, while the same calls inside
  the core failed `dotnet build` with exit code 1 and `error MMO1002` on the exact line [measured].
- **The analyzer tests** reference the analyzer project as a normal `ProjectReference`, so they can
  instantiate `DeterminismAnalyzer`. The analyzer does not run on the test code.
- **The WebAssembly host** keeps its `[JSExport]` methods outside the core, so its unsafe interop code
  is not analyzed ([wasm-api.md](wasm-api.md)).

The analyzer project:

```xml
<!-- src/MmoEngine.SimCore.Analyzers/MmoEngine.SimCore.Analyzers.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
    <IsRoslynComponent>true</IsRoslynComponent>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <AdditionalFiles Include="AnalyzerReleases.Shipped.md" />
    <AdditionalFiles Include="AnalyzerReleases.Unshipped.md" />
  </ItemGroup>
</Project>
```

`TargetFramework` overrides the repository's `net10.0` default, because analyzers load into the
compiler. `LangVersion=latest`, `Nullable` and `TreatWarningsAsErrors` come from
`Directory.Build.props`.

Registration:

- `EnableConcurrentExecution()`.
- `ConfigureGeneratedCodeAnalysis(Analyze | ReportDiagnostics)`.
- One `RegisterOperationAction` over Invocation, PropertyReference, FieldReference, MethodReference,
  EventReference, ObjectCreation, Lock, Loop, Conversion, Spread, VariableDeclarator, Binary and Unary
  (user-defined operators and float `%`), TypeOf, DefaultValue and the four Dynamic kinds.
- `RegisterSymbolAction` for Field, Property, Method and Event declarations (declared types,
  `MethodImplementationFlags`, `[Conditional]` and `[UnsafeAccessor]`).
- `RegisterSyntaxTreeAction` for `#if` and `#elif`.

Matching compares `OriginalDefinition` by metadata name. This avoids `GetTypeByMetadataName`
returning null for an ambiguous name, and catches generic math, because `T.Sin` resolves to an
interface member in `System.Numerics`.

## Roslyn pin

`Directory.Packages.props` pins `Microsoft.CodeAnalysis.CSharp` to **5.0.0** while `global.json` is on
10.0.1xx. SDK 10.0.115's compiler is 5.0.0 (assembly version 5.0.0.0, C# 14 by default) [measured].

- An analyzer built against a newer Roslyn (5.3.0 was tried) gets `CS9057`: it "references version
  '5.3.0.0' of the compiler, which is newer than the currently running version '5.0.0.0'".
- Under `-warnaserror` the build fails. **Without it the build passes with one warning, and the
  analyzer silently does not run** [measured].
- Any later SDK loads a 5.0.0 analyzer. Raise the pin only together with `global.json`'s band.

The test `RoslynPinMatchesTheSdkBand` reads both files and fails when `global.json` is on 10.0.1xx and
the pin is not 5.0.0. CI builds with `-warnaserror`, and the backstop does not depend on the build
loading the analyzer.

## Release tracking

`EnforceExtendedAnalyzerRules` turns on the release-tracking rules, and RS2008 is an error under the
repository's `TreatWarningsAsErrors` [measured]. The two files are `AdditionalFiles`:

```text
; src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Shipped.md
; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md
```

```text
; src/MmoEngine.SimCore.Analyzers/AnalyzerReleases.Unshipped.md
; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MMO1001 | Determinism | Error | DeterminismAnalyzer
MMO1002 | Determinism | Error | DeterminismAnalyzer
MMO1003 | Determinism | Error | DeterminismAnalyzer
MMO1004 | Determinism | Error | DeterminismAnalyzer
MMO1005 | Determinism | Error | DeterminismAnalyzer
MMO1006 | Determinism | Error | DeterminismAnalyzer
MMO1007 | Determinism | Error | DeterminismAnalyzer
MMO1008 | Determinism | Error | DeterminismAnalyzer
MMO1009 | Determinism | Error | DeterminismAnalyzer
```

A later change to a rule's severity or category must be recorded under `### Changed Rules`, so it is
always a deliberate edit that shows in the diff, and `DescriptorsMatchTheReleaseFile` fails until the
file and the descriptors agree.

## Tests

All in `tests/MmoEngine.SimCore.Analyzers.Tests/` (xUnit v3). The project name ends in `.Tests`, so
`Directory.Build.props` treats it as a test project. It references `Basic.Reference.Assemblies.Net100`
1.8.14 (MIT) and `Microsoft.CodeAnalysis.CSharp` 5.0.0.

**Harness.** `CSharpCompilation.Create` with `LanguageVersion.Latest`, nullable on and unsafe allowed
(for the function-pointer sample), then
`WithAnalyzers([new DeterminismAnalyzer()], new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true))`
and `GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken)`. Each sample is one member of
`public static class C` in `namespace Sample`, with the usings for `System`, `System.Collections.Generic`,
`System.Collections.Immutable`, `System.Diagnostics`, `System.Linq`, `System.Numerics`,
`System.Runtime.Intrinsics`, `System.Threading` and `System.Threading.Tasks`.

**`EachSampleProducesExactlyItsRule(ruleId, count, member)`**, a Theory with at least one violating
row and one allowed row per rule (an allowed row has an empty rule ID and count 0). Each row asserts,
in order:

1. the sample has no compiler errors, so a typo cannot make an allowed row pass vacuously;
2. the exact sequence of rule IDs, and their count;
3. every diagnostic's severity is Error;
4. every diagnostic's location lies inside the sample member's span.

The prototype had 75 rows; a mutation check (a wrong count, and a banned call in an allowed row) made
both fail [measured]. The rows below are the minimum; extra rows for the property, event, pointer and
initializer branches keep the analyzer above the 95% changed-file line coverage gate (the prototype's
rows reached 94.4%).

| Rule | Violating samples (count 1 unless noted) | Allowed samples |
| --- | --- | --- |
| MMO1001 | `Vector3.Dot(new Vector3(x), Vector3.UnitX)`; `Vector<float>.Count`; `Vector128.Sum(a)`; `Vector128.Dot(a, a)`; `Vector128.IsHardwareAccelerated`; `new List<Quaternion>()`; `var v = Matrix4x4.Identity;`; `System.Runtime.Intrinsics.X86.Sse.IsSupported` | `Vector128.Add(a, b) * b - Vector128.Create(1f)`; `x * y + 1f` |
| MMO1002 | `MathF.Sin(x)`; `float.Atan2(x, 1f)`; `T.Cos(x)` with `T : ITrigonometricFunctions<T>`; the method group `MathF.Tan`; the function pointer `&MathF.Sin`; `MathF.Pow(x, 2f)`; `MathF.ReciprocalSqrtEstimate(x)`; `MathF.Max(x, 0f)`; `MathF.Round(x, 2)`; `x % 2f`; `Math.Clamp(x, 0f, 1f)` outside `SimMath` | `MathF.Sqrt(MathF.Abs(x)) + MathF.Floor(x) + MathF.Round(x) + MathF.Round(x, MidpointRounding.ToZero) + MathF.CopySign(x, -1f) + MathF.PI`; `nameof(MathF.Sin)`; `Math.Max(i, 0) + Math.Clamp(i, 0, 9)`; `float.IsFinite(x) ? BitConverter.SingleToUInt32Bits(x) : 0u` |
| MMO1003 | `MathF.FusedMultiplyAdd(a, b, c)`; `float.MultiplyAddEstimate(a, a, a)`; `Vector128.FusedMultiplyAdd(a, a, a)`; `System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(a, a, a)` | `a * b + c` |
| MMO1004 | `foreach` over a `Dictionary<int, int>`; `s.First()` on a `HashSet<int>`; `d.Keys.ToArray()`; `new List<int>(s)`; `[.. s]`; `string.Join(",", s)`; returning an `ImmutableHashSet<int>` as `IEnumerable<int>`; `foreach` over `IReadOnlyDictionary<int, int>.Keys`; `d.Keys.CopyTo(dst, 0)`; `from kv in d select kv.Value` | `d.TryGetValue(k, out var v) ? v + d.Count : d.Keys.Count`; `foreach` over a `SortedDictionary<int, int>`; `a.UnionWith(b)` on two `HashSet<int>` |
| MMO1005 | `DateTime.Now.Ticks`; `DateTime.UtcNow.Ticks`; `Stopwatch.GetTimestamp()`; `Environment.TickCount`; `new Random(42).Next()`; `Random.Shared.Next()`; `Guid.NewGuid()`; `s.GetHashCode()` on a string; `HashCode.Combine(a, b)`; `TimeProvider.System.GetUtcNow()` | `a.GetHashCode() ^ 17` on an `int`; `step * stepTicks` |
| MMO1006 | `lock (Gate) { }`; a `static readonly Lock` field; `lock (l)` on a `Lock` parameter (count 2: the parameter and the statement); `t.Wait()`; `t.Result`; `t.GetAwaiter().GetResult()`; `Thread.Sleep(1)`; `Monitor.Enter(typeof(C))`; `[MethodImpl(MethodImplOptions.Synchronized)]`; `Parallel.For(0, a.Length, i => a[i]++)` | `await t`; `Interlocked.Increment(ref x)` |
| MMO1007 | `(float)(x * 0.5)`; `double M(float x) => x` (count 2); `(int)(nint)a`; `var h = (Half)1;` | `const double Scale = 1.0 / 3.0` used as `x * (float)Scale`; `x * (float)(Math.PI / 180.0)` |
| MMO1008 | `OperatingSystem.IsBrowser()`; `IntPtr.Size`; `Environment.ProcessorCount`; `typeof(MathF).GetMethod("Sin")`; `d.Foo()` on a `dynamic`; an `#if DEBUG` block; `Debug.Assert(b)`; an `[UnsafeAccessor]` extern method | `object M() => typeof(int);` (no member access on the `Type`) |
| MMO1009 | `(int)x`; `(byte)(x * 2f)`; `float M(ulong u) => u`; `Convert.ToInt32(x)` | `(int)2.5f`; `i * 0.5f`; `(float)(uint)(h >> 40)` |

**Other tests in the project:**

| Test | Asserts |
| --- | --- |
| `PlatformMathMessageNamesTheReplacement(member, replacement)` (Theory) | `MathF.Sin` names `'SimMath.Sin'`, `float.Atan2` names `'SimMath.Atan2'`, `MathF.Max` names `'SimMath.Max'`, `Math.Clamp` outside `SimMath` names `'SimMath.Clamp'`, and `MathF.Pow` gets the fallback text |
| `FusedCallsReportOnlyTheFmaRule(member)` (Theory) | for `MathF.FusedMultiplyAdd`, `Math.FusedMultiplyAdd`, `Vector128.FusedMultiplyAdd` and `Fma.MultiplyAdd`, the diagnostics whose span is the call are exactly one MMO1003; the `double` declarations of the `Math` row report MMO1007 at their own locations |
| `SimMathMayClampAndCastFloats` | `Clamp` anywhere in `MmoEngine.SimCore.Math.SimMath`, and a float-to-`int` cast inside `FloorToInt`, `RoundToInt` or `TruncateToInt`, report nothing; the same cast in another `SimMath` method reports MMO1009, and the same code in another type reports MMO1002 and MMO1009 |
| `SeverityCannotBeLowered(mechanism)` (Theory) | with `#pragma warning disable MMO1002`, with `SpecificDiagnosticOptions` set to Suppress (what `NoWarn` and `severity = none` map to), and with warnings not treated as errors, `MathF.Sin` still reports MMO1002 at Error and not suppressed |
| `SuppressMessageIsReportedAsSuppressed` | `[SuppressMessage("Determinism", "MMO1002")]` yields MMO1002 with `IsSuppressed` true, which the backstop counts |
| `DescriptorsMatchTheReleaseFile` | `SupportedDiagnostics` is exactly MMO1001–MMO1009; each is Error, enabled by default, category `Determinism`, tagged `NotConfigurable`; `AnalyzerReleases.Unshipped.md` lists the same nine as Error |
| `CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes` | the backstop above |
| `CoreProjectAttachesTheAnalyzerPrivately` | the core's project file has exactly one analyzer `ProjectReference` with `OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"` and `PrivateAssets="all"`; `ImplicitUsings` is `disable`; nothing sets `RunAnalyzers`, `RunAnalyzersDuringBuild`, `NoWarn` or `WarningsNotAsErrors` for MMO rules; there is no `<Compile Remove>` |
| `OnlyTheCoreAttachesTheAnalyzer` | no other project file under `src/`, `tests/` or `benchmarks/` attaches it with `OutputItemType="Analyzer"` |
| `RoslynPinMatchesTheSdkBand` | the pin above |
| `ABuildWithAViolationFails` | `dotnet build` of a throwaway class library in a unique temporary directory, attaching the built analyzer with `<Analyzer Include="…" />` and containing one `foreach` over a `Dictionary`, exits non-zero with `error MMO1004` in its output (about 1.1 s warm [measured]); the output is written to the test log |

**Mapping to the acceptance scenarios (US3):**

| Scenario | Tests |
| --- | --- |
| US3.1 vector, quaternion, matrix and horizontal `Vector128` | `EachSampleProducesExactlyItsRule`, MMO1001 rows |
| US3.2 platform math, naming the replacement | MMO1002 rows; `PlatformMathMessageNamesTheReplacement` |
| US3.3 fused multiply-add, and no other rule for the same call | MMO1003 rows; `FusedCallsReportOnlyTheFmaRule` |
| US3.4 hash-ordered iteration | MMO1004 rows |
| US3.5 ambient time and randomness | MMO1005 rows |
| US3.6 locks, task waits and blocking primitives | MMO1006 rows |
| US3.7 `double`, `Half`, `nint`, `NFloat`; float-to-integer; platform paths | MMO1007, MMO1009 and MMO1008 rows; `SimMathMayClampAndCastFloats` |
| Independent Test: "build the core itself with the analyzer as an error" | `CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes`, `ABuildWithAViolationFails`, and CI's `dotnet build MmoEngine.sln -warnaserror` |

US3 scenario 8 (the trigonometric bounds) is covered by the math tests in
`tests/MmoEngine.SimCore.Tests/Math/` and by the agreement suite, not by the analyzer.

**Timings** [measured, sandbox]: loading the reference assemblies takes about 67 ms, the first
compile-and-analyze about 970 ms (Roslyn's JIT), and each later one 5–7 ms. The prototype's 81 tests
ran in about 2 s.
