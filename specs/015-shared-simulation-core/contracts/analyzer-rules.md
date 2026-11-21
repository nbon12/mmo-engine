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
| MMO1002 | Platform math outside the allowlist | Static methods of `System.Math`, `System.MathF`, `System.Single`, `System.Double`, `System.Half` and `System.Runtime.InteropServices.NFloat`, and static methods of the generic-math interfaces in `System.Numerics` (so `T.Cos(x)` is caught), that are not on the [math allowlist](#math-allowlist-mmo1002). Invocations, method groups and function pointers (`&MathF.Sin`) all count. The `%` operator (and `%=`) on `float` operands, and on a type parameter that may be bound to `float` (the generic-math `IModulusOperators` operator, `a % b` with `T : INumber<T>`). | The math allowlist, the constants (static properties such as `T.Pi` included), and `nameof(MathF.Sin)`. The operators and conversions these types define, so `(Half)1` reports only MMO1007. `%` on a type parameter constrained to `IBinaryInteger<T>`. |
| MMO1003 | Fused multiply-add | Any member whose name starts with `FusedMultiply`, or is `MultiplyAddEstimate`, on any type (`Math`, `MathF`, `float`, `Vector128`, `AdvSimd` and the rest), and every member of `System.Runtime.Intrinsics.X86.Fma` and `Fma.X64`. | `a * b + c` written as two float operations. |
| MMO1004 | Iteration over a hash-ordered collection | Enumerating a [hash-ordered collection](#hash-ordered-collections-mmo1004), or a type parameter constrained to one: `foreach`; `GetEnumerator`, `CopyTo` or `ToArray`; `Keys`, `Values` or `Items` whose result is not itself hash-ordered; a `..` spread; and any conversion of the collection to a type that is not itself hash-ordered (LINQ receivers, query syntax, `string.Join`, `new List<T>(set)`, `AddRange`, returning it as `IEnumerable<T>`), including the two that have no conversion operation: a declaration pattern (`h is IEnumerable<int> e`, `case IEnumerable<int> e:`) and a type argument bound to a type parameter constrained to a type that is not hash-ordered (`Sum(set)` with `Sum<T>(T e) where T : IEnumerable<int>`). | Lookups: `TryGetValue`, `ContainsKey`, `Contains`, the indexer, `Count`, `Keys.Count`. [Order-insensitive sinks](#hash-ordered-collections-mmo1004): a listed type's set operations (`UnionWith`, `SetEquals` and the rest) and its own copy constructor. `SortedDictionary`, `SortedSet`, arrays and `List<T>`. An unconstrained type argument (`List<HashSet<int>>`, `First()` on one). |
| MMO1005 | Ambient time, randomness or a per-process hash seed | The [ambient list](#ambient-time-randomness-and-hash-seeds-mmo1005): wall-clock and monotonic clocks, every timer, all of `System.Random` (seeded too), `Guid.NewGuid`, `RandomNumberGenerator`, and hashing seeded per process (`HashCode`, `string.GetHashCode`, a `GetHashCode` that is not overridden, `RuntimeHelpers.GetHashCode`). | The step number and step length passed in by the caller. The core's seeded spread hash (SplitMix64). `int.GetHashCode()` and other overrides that return the value's bits. |
| MMO1006 | Lock or blocking synchronization | Every `lock` statement, including on `System.Threading.Lock`. The [blocking list](#locks-and-blocking-mmo1006): `Monitor`, `Mutex`, the semaphores, reader-writer locks, spin primitives, wait handles, `Barrier`, `BlockingCollection`, `Parallel`, `Thread.Sleep`/`Join`/`SpinWait`/`Yield`, `Task.Wait`/`WaitAll`/`WaitAny`/`RunSynchronously`, `Task<T>.Result`, `ValueTask<T>.Result`, `GetResult` on every awaiter, and `[MethodImpl(MethodImplOptions.Synchronized)]` on a method, an accessor, a field-like event, a local function or a lambda. | `await`, `Interlocked` and `Volatile`; none of them blocks. |
| MMO1007 | Non-float floating point or platform-sized number | Declarations of `double`, `Half`, `NFloat`, `nint` or `nuint` (fields, properties, events, parameters, return types, locals, type arguments, array elements), including the parameters and return types of local functions and delegates and the ones a lambda writes out, and any non-constant expression of those types. An untyped literal in a float expression (`x * 0.5`) is one. | `const double` fields and constant-folded expressions such as `(float)(Math.PI / 180.0)`. `int` and `long` for fixed point and counters. `decimal` is not checked. |
| MMO1008 | Platform query, conditional compilation or reflection | `#if` and `#elif` directives, active or not. `[Conditional]` declarations (methods and local functions) and every member of `System.Diagnostics.Debug` and `Trace`. `OperatingSystem`, `RuntimeInformation`, `RuntimeFeature`. Every member of `System.Environment` except the two clocks (MMO1005). `IntPtr.Size`, `UIntPtr.Size`, `BitConverter.IsLittleEndian`. Members of `System.Type`, operators included, every type in `System.Reflection` and its sub-namespaces, `Activator`. Every `dynamic` operation. `[UnsafeAccessor]` declarations (methods and local functions). Native code: every `extern` declaration (a `[DllImport]` or `InternalCall` method or local function, an extern accessor or event), a `[LibraryImport]` declaration, and every member of `NativeLibrary` and `Marshal`. | One code path for every build. `typeof(T)` and `nameof` without member access. |
| MMO1009 | Float-to-integer or ulong-to-float conversion outside the SimMath helpers | A non-constant conversion, explicit or implicit, `checked` or not, from `float` or `double` to any integer type (`sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `char`, `Int128`, `UInt128`) or to any enum type (matched by its underlying integer type, so `(Buttons)x` counts), outside the bodies of the three `MmoEngine.SimCore.Math.SimMath` helpers. Every member of `System.Convert`. An invocation of `CreateTruncating`, `CreateSaturating` or `CreateChecked` declared on one of those integer types (`int.CreateSaturating(x)`, `long.CreateTruncating(x)`) whose type argument `TOther` is `float`, `double`, `Half` or `NFloat`, or a type parameter not constrained to `IBinaryInteger<T>`. A non-constant conversion from `ulong` to `float`, anywhere in the core, `SimMath` included. These conversions count wherever Roslyn makes them without a conversion operation of their own: element by element in a tuple conversion (`((int, int))t`, nested tuples too), from a `foreach` element to its iteration variable (`foreach (int i in floats)`), in a deconstruction (from a tuple or through `Deconstruct`, also in a `foreach`), from a spread's elements to the collection expression's (`float[] f = [.. ulongs]`), from the left operand of `??` to its result, and in the standard conversion on either side of a user-defined operator in any of these. | `SimMath.FloorToInt`, `SimMath.RoundToInt` and `SimMath.TruncateToInt`, which saturate before the cast (NaN → 0, ≥ 2^31 → `int.MaxValue`, < −2^31 → `int.MinValue`). Constant casts such as `(int)2.5f`. `Create*` from a type parameter constrained to `IBinaryInteger<T>`. Integer-to-float conversions from `int`, `uint`, `long`, `short` and `byte`, which round correctly in every build. |

US3 scenario 1 lists `Vector128.Sum` and `Vector128.Dot` as its horizontal samples. A fused
`Vector128.FusedMultiplyAdd` belongs to MMO1003 (US3 scenario 3), by the precedence below.

## Messages

`{0}` names the reported operation, by kind:

- an invocation, object creation, member reference, method group or function pointer: the symbol as
  `ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)` shows it, for example
  `MathF.Sin(float)`;
- a `foreach`: the keyword `foreach`;
- a `..` spread: `..`;
- a `lock` statement: `lock`;
- a float `%` or `%=`, generic ones included: the operator, `%` or `%=`;
- a declaration pattern (MMO1004): its source text (`IEnumerable<int> e`);
- a conversion, including the implicit conversion of a LINQ receiver or an argument: the source text
  of the conversion's syntax, which is the cast expression for an explicit conversion (`(int)x`) and
  the converted expression for an implicit one (`s` in `s.First()`, `u` in `float M(ulong u) => u`);
- an element conversion with no syntax of its own (MMO1009): for a `foreach`, the iteration variable's
  declared type (`int` in `foreach (int i in a)`), or the deconstruction as written
  (`(float p, float q)` in `foreach ((float p, float q) in a)`); for a deconstruction assignment, the
  deconstructed value (`t` in `(float p, float q) = t`); for `??`, its left operand (`u` in `u ?? f`);
  for a spread, `..`, as for every spread;
- a property, indexer or event accessor's `Synchronized` flag (MMO1006), or its `extern` modifier or
  platform-path attribute (MMO1008): the accessor as `CSharpShortErrorMessageFormat` shows it, for
  example `C.P.get`;
- an `#if` or `#elif` directive: the directive keyword, `#if` or `#elif`;
- a declaration: the declared identifier, which for a field-like event's accessors is the event's;
- anything else (a non-constant `double` expression, a `dynamic` operation, an attribute, and a
  lambda's own `Synchronized` flag or written return type): the operation's source text.

Where `{1}` is a type (MMO1001, MMO1004 and MMO1007), it is the constructed type as
`ITypeSymbol.ToDisplayString()` shows it, which is the default
`SymbolDisplayFormat.CSharpErrorMessageFormat`, namespace-qualified, never the type's
`OriginalDefinition`. For MMO1004 it is the type of the hash-ordered expression that is enumerated or
converted, for example `System.Collections.Generic.Dictionary<int, int>`; for a generic call, the
hash-ordered type argument (`'C.Sum<HashSet<int>>(HashSet<int>)' enumerates
'System.Collections.Generic.HashSet<int>'`). So a `foreach` over a
`Dictionary<int, int>` reports this message, ending with MMO1004's alternative:

```text
'foreach' enumerates 'System.Collections.Generic.Dictionary<int, int>', whose order is not defined; iterate a sorted array or List<T> kept alongside it, or a SortedDictionary or SortedSet
```

Every message names the allowed alternative (US3).

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
| `float` or `double` to an integer or an enum, an integer type's `Create*` from a floating-point value, or `System.Convert` | `converts a floating-point value to an integer, which gives different results between builds for NaN and out-of-range values; use 'SimMath.FloorToInt', 'SimMath.RoundToInt' or 'SimMath.TruncateToInt', which saturate first` |
| `ulong` to `float` | `converts a 'ulong' to 'float', which the WebAssembly build rounds through 'double' and so can differ by one ulp; narrow it to 'uint' or 'long' first` |

The exemption is per method, not per type: the three helpers are the only places a float-to-integer
cast is allowed ([research.md](../research.md) R3 and R4). It is keyed by their documentation comment
IDs (`M:MmoEngine.SimCore.Math.SimMath.FloorToInt(System.Single)`, and the same for `RoundToInt` and
`TruncateToInt`), not their names. It covers the helpers' own bodies, not a lambda or local function
inside them, which can run without the helper's range checks, and not another overload or a local
function that shares a helper's name (`RoundToInt(float, float)`, or a `static int RoundToInt(float v)`
inside another method). The trigonometric range reduction
converts its in-range quadrant count with `SimMath.RoundToInt`, not with a raw cast. The spread hash
converts its 24-bit `ulong` value as `(float)(uint)(h >> 40)`.

The `Create*` methods are matched on the integer type itself because they are class members of
`System.Int32` and its siblings, not of a host type MMO1002 covers or of a generic-math interface.
CoreLib implements them with the raw conversion, so `int.CreateSaturating(float.NaN)` is 0 on x64 and
−2147483648 in the wasm interpreter [measured, design review]. The same call through a type
parameter (`T.CreateSaturating(x)`) resolves to `INumberBase<T>` and reports MMO1002, which comes
first in the precedence order. An integer type's `Create*` whose `TOther` is a type parameter
(`int.CreateSaturating(v)` with `T : INumberBase<T>`) reports MMO1009, because the caller can bind it to
`float`; the check is in the generic body, since the binding can be any number of calls away. A type
parameter constrained, directly or through another type parameter, to `IBinaryInteger<T>` (or an
interface that extends it) is never a floating-point type, so its `Create*` is allowed. An enum's
float conversion compiles to the same raw conversion as a cast to its underlying type.

The diagnostic's location is the operation's syntax: the invocation, member access or conversion
expression; the `lock` keyword for a `lock` statement; the collection expression for `foreach` and
spreads; the declared identifier for a declaration; the directive line for `#if`. The forms with no
syntax of their own report where their conversion or flag is written: a `foreach` element conversion at
the declared iteration type (or the deconstruction variables), a deconstruction assignment's and a
`??`'s at the converted operand, a spread's at its collection expression; an accessor's `Synchronized`
flag, `extern` modifier or attribute at the accessor keyword (`get`, `set`, `init`, `add`, `remove`), a
field-like event's at the event's identifier; and a lambda's own finding at the lambda, which has no
identifier. A declaration pattern reports at the pattern, and a hash-ordered type argument at the
invocation, member reference or object creation that binds it.

## Precedence and deduplication

- **One diagnostic per operation.** When an operation matches more than one rule, it reports the
  first in this order:

  `MMO1003` > `MMO1001` > `MMO1002` > `MMO1008` > `MMO1005` > `MMO1006` > `MMO1004` > `MMO1009` > `MMO1007`

  So `Vector128.FusedMultiplyAdd` and `Fma.MultiplyAdd` report MMO1003 and not MMO1001,
  `Vector128.IsHardwareAccelerated` reports MMO1001 and not MMO1008, and `Math.Sin` reports MMO1002
  for the call even though the call's type is `double`.
- **No repeats inside one expression.** An operation is skipped when an enclosing operation in the
  same statement already reports the same rule. `Vector3.Dot(new Vector3(x), Vector3.UnitX)` gives
  one MMO1001, not three. A field, property or parameter initializer counts as its declaration. The
  statement is the nearest enclosing one, so each statement of a `foreach` body reports on its own.
- **Different rules in one expression still report.** `(float)Math.Sin(x)` gives MMO1002 for the call
  and MMO1007 for the implicit conversion of `x` to `double`.
- **Declarations report separately** from the operations inside them. `double M(float x) => x` gives
  two MMO1007: the return type and the conversion.
- **A positional record parameter reports once.** The compiler also declares a property from each
  primary constructor parameter of a record, at the parameter's identifier. The parameter reports
  the declaration and that property does not, so `record R(double X)` gives one MMO1007, not two at
  the same place. A property the record declares itself reports on its own identifier:
  `record R(double X) { public double X { get; init; } = X; }` gives two.
- **A method's identifier reports one rule**, the first in the precedence order among its return
  type, its `extern` modifier or its `[Conditional]`, `[UnsafeAccessor]` or `[LibraryImport]`
  attribute (MMO1008) and its `Synchronized` flag (MMO1006). Its parameters report on their own
  identifiers. A local function is checked the same way at its identifier, and a lambda or anonymous
  method at the lambda, where a return type counts only when the lambda writes one and a parameter
  only when its type is written. An accessor reports one rule at its keyword: `extern` or an
  attribute (MMO1008), then `Synchronized` (MMO1006). A delegate's identifier reports its return type
  and its parameters report on their own identifiers. An event's identifier reports one rule among
  its type's MMO1001, a field-like event's implicit accessors being `extern` (MMO1008) or synchronized
  by `[method: MethodImpl(…)]` (MMO1006), and the rest of its type's rules.
- **A partial member reports once.** Roslyn runs the symbol action for both parts of a partial
  method, property or event. The defining declaration reports, with the attributes of both parts, and
  the implementation part reports nothing, so `partial double M(double x)` with its implementation
  gives two MMO1007 (the return type and `x`), not four, and a `[LibraryImport]` declaration with its
  implementation part gives one MMO1008.
- **A `foreach`, a spread and a tuple conversion are each one operation.** A `foreach` over a
  hash-ordered collection whose iteration variable also converts a float reports MMO1004 only, and the
  conversion shows once the collection is replaced. A tuple conversion or deconstruction reports one
  MMO1009, with the reason of its first converting element.

## Allowlists and tables

The tables live in the analyzer's source as `ImmutableHashSet<string>` keyed by the original
definition's metadata name (``Namespace.Type`1+Nested`` plus the member name). Where an entry needs one
overload, the key is the documentation comment ID (`ISymbol.GetDocumentationCommentId()`, for example
`M:System.MathF.Round(System.Single)`). The core cannot widen its own rules: there is no
`AdditionalFiles` list, and the analyzer tests pin every table: `TableTests.EveryContractTableEntryIsEnforced`
has a row for every entry the tables below name, allowed or banned, so dropping, misspelling or
wrongly allowing any entry they name fails a test.

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
- `IEEERemainder`, `Sign` and `Parse`, which the core does not need;
- float `%`, also through a type parameter that may be bound to `float` (`a % b` and `a %= b` with
  `T : IModulusOperators<T, T, T>` or `T : INumber<T>`, which call the interface's `op_Modulus`).
  Research R4 found `%` exact and identical in every build but unneeded, and the generic route is the
  same operation. A type parameter constrained to `IBinaryInteger<T>` is never `float`, so its `%` is
  integer `%` and allowed.

Instance members of these types, such as `ToString` and `TryFormat`, are not platform math and are
outside MMO1002, which covers static methods only. The core formats floats in invariant round-trip
form for error text (`Vec3.ToString`, `ConfigFields.FormatBound`), and that text is the same in every
build.

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
`ConcurrentDictionary<,>`, `ConcurrentBag<>`, `ImmutableDictionary<,>` with its `Builder`,
`ImmutableHashSet<>` with its `Builder`, `FrozenDictionary<,>`, `FrozenSet<>`, `ReadOnlyDictionary<,>`
with its key and value collections, `HybridDictionary`, `StringDictionary` (a `Hashtable` of strings,
whose hash is seeded per process), `ConditionalWeakTable<,>`, and the interfaces `IDictionary<,>`,
`IReadOnlyDictionary<,>`, `ISet<>`, `IReadOnlySet<>` and `IDictionary`. The builders derive from
`object`, not from their immutable type, so they are listed by name.

The enumerating members are `GetEnumerator`, `CopyTo`, `ToArray`, `Keys`, `Values` and `Items`
(`FrozenSet<T>.Items`, an array in hash order). `Keys`, `Values` and `Items` report only when their
result is not itself on the list; otherwise the later enumeration of that result reports. A conversion works the same way: it reports when its target type is not on the
list, and a conversion to one (a `Dictionary` passed as an `IReadOnlyDictionary`) leaves the report to
the later enumeration of the converted value.

A class derived from one of the listed classes enumerates in the same order, so it counts too. An
interface counts only as the expression's own type, so `SortedDictionary`, which implements
`IDictionary<,>`, and `SortedSet`, which implements `ISet<>`, do not. A type parameter constrained to
a listed type, directly or through another type parameter (`where T : Dictionary<int, int>`), is
hash-ordered too, so a `foreach` or a LINQ call on a `T` reports in the generic body.

Two routes reach a type that is not hash-ordered with no conversion operation, and both count as
conversions:

- **A declaration pattern** whose input is hash-ordered and whose declared type is not
  (`h is IEnumerable<int> e`, `case IEnumerable<int> e:`, a switch expression arm, `h is object o`),
  including a recursive pattern with a designation. `h is var v` and `h is HashSet<int> g` keep the
  type and do not report. A type test with no designation (`h is IEnumerable<int>`) does not
  enumerate and does not report.
- **A type argument** that is hash-ordered, bound to a type parameter that is constrained, directly or
  through another type parameter, to a class or interface and to no hash-ordered one, so the generic
  code sees it as that type: `Sum(set)` with `Sum<T>(T e) where T : IEnumerable<int>`, a method group
  of it, and `new Box<HashSet<int>>(set)` with `Box<T> where T : IEnumerable<int>`. It reports at the
  invocation, member reference or object creation, naming the member and the type argument. An
  unconstrained type parameter gives the generic code nothing to enumerate the argument through, and
  each read gets the argument's own type back, so `List<HashSet<int>>`, `Dictionary<int, HashSet<int>>`
  and `First()` on a `List<HashSet<int>>` do not report.

The order-insensitive sinks are the only arguments that do not report. A hash-ordered collection
passed to one of the set operations `UnionWith`, `IntersectWith`, `ExceptWith`, `SymmetricExceptWith`,
`SetEquals`, `Overlaps`, `IsSubsetOf`, `IsSupersetOf`, `IsProperSubsetOf`, `IsProperSupersetOf`,
`Union`, `Intersect`, `Except` or `SymmetricExcept`, declared on a listed type (`HashSet<T>`,
`ISet<T>`, `IReadOnlySet<T>`, `ImmutableHashSet<T>`, `FrozenSet<T>`), or to a listed type's own
constructor (its copy constructor), is not enumerated in an order the step can observe. Passed to any
other member it is a conversion like any other and reports: `Add`, `TryAdd` and a collection
initializer, which can store the set as a value and let a later read enumerate it
(`new Dictionary<int, IEnumerable<int>> { { 0, set } }`), a `Hashtable`, a derived class's constructor,
and a set operation of a type that is not listed (`SortedSet<T>.UnionWith`).

### Ambient time, randomness and hash seeds (MMO1005)

- Whole types: `Stopwatch`, `TimeProvider`, `Random`, `RandomNumberGenerator`, `HashCode`,
  `System.Threading.Timer`, `PeriodicTimer`, `System.Timers.Timer`.
- Members: `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`,
  `DateTimeOffset.UtcNow`, `Environment.TickCount`, `Environment.TickCount64`, `Guid.NewGuid`,
  `Guid.CreateVersion7`, `string.GetHashCode`, `object.GetHashCode` and `ValueType.GetHashCode` when
  the receiver's type does not override them, `RuntimeHelpers.GetHashCode`, `Task.Delay`.
- A whole type reports on every use: any member, constructor or method group of it, or of a class
  derived from it. Declaring a field, parameter or local of one of these types does not report by
  itself; its first use does.
- Roslyn binds a `GetHashCode()` call to the receiver type's own override when it has one, so a call
  that still targets `object.GetHashCode` or `ValueType.GetHashCode` is on a type that does not
  override it (a struct without an override, `object`, an interface or a type parameter).
  `int.GetHashCode()` binds to `Int32`'s override and is allowed.

### Locks and blocking (MMO1006)

- Whole types: `Monitor`, `Lock`, `Mutex`, `Semaphore`, `SemaphoreSlim`, `ReaderWriterLock`,
  `ReaderWriterLockSlim`, `SpinLock`, `SpinWait`, `ManualResetEvent`, `ManualResetEventSlim`,
  `AutoResetEvent`, `EventWaitHandle`, `WaitHandle`, `CountdownEvent`, `Barrier`,
  `BlockingCollection<>`, `Parallel`.
- Members: `Thread.Sleep`, `Thread.Join`, `Thread.SpinWait`, `Thread.Yield`, `Task.Wait`,
  `Task.WaitAll`, `Task.WaitAny`, `Task.RunSynchronously`, `Task<T>.Result`, `ValueTask<T>.Result`,
  and `GetResult` on `TaskAwaiter`, `TaskAwaiter<T>`, `ValueTaskAwaiter`, `ValueTaskAwaiter<T>` and the
  four configured awaiters.
- Every `lock` statement, and every method, property, indexer or event accessor, local function and
  lambda whose implementation flags include `Synchronized`: at the method's or local function's
  identifier, the accessor's keyword, the lambda, or, for a field-like event whose
  `[method: MethodImpl(MethodImplOptions.Synchronized)]` sets the flag on both implicit accessors, the
  event's identifier.
- A whole type reports on every use (any member, constructor or method group, of it or of a class
  derived from it) and on every declaration whose type is or contains it: a field, property, event,
  parameter, return type or local, so `static readonly Lock L = new();` reports once, at `L`.

### Numeric types (MMO1007)

`double`, `System.Half`, `System.Runtime.InteropServices.NFloat`, `nint` (`IntPtr`) and `nuint`
(`UIntPtr`). `nint` and `NFloat` are 4 bytes on wasm32 and 8 bytes on x64 [measured]. `double` is
deterministic for `+ − × ÷ √`, but FR-012 keeps it out: untyped literals silently create doubles,
they bring in the `Math.*(double)` surface, and the state hash assumes float32 fields.

A declaration or an expression reports when its type is one of these or contains one as a type
argument, array element or pointer target (`List<double>`, `nint*`). The expressions are the
operation kinds the analyzer registers (invocations, member references, method groups, object
creations, conversions, unary, binary and compound operators, `default`) with no constant value. A
parameter or local reference is not one of them; its declaration reports instead.

### Generated code

`ConfigureGeneratedCodeAnalysis(Analyze | ReportDiagnostics)`. Roslyn treats files named `*.g.cs`
and files with an `<auto-generated>` header as generated and skips them by default. Without this
flag, the committed `SimMathCoefficients.g.cs` and `TwoOverPi.g.cs` would escape the rules, and so
would any source generator's output.

### Attribute applications

An operation whose ancestor is an `IAttributeOperation` (an attribute applied to an assembly, type,
member or parameter) is not reported by any operation rule. Attribute arguments are constants and
`typeof` expressions stored as metadata; no step runs them, and running them would need reflection,
which MMO1008 bans. Roslyn models each attribute as an object creation of the attribute type, so
without this exemption the SDK-generated `obj/…/MmoEngine.SimCore.AssemblyInfo.cs`, which carries
`[assembly: System.Reflection.AssemblyVersion(…)]`, `AssemblyMetadata("IsTrimmable", "True")` and the
other `System.Reflection` assembly attributes under an `<auto-generated>` header, would report
MMO1008 on lines nobody can edit and fail the core's build. The symbol actions still check the
attributes that change behaviour: `[MethodImpl(MethodImplOptions.Synchronized)]` (MMO1006),
`[Conditional]`, `[UnsafeAccessor]` and `[LibraryImport]` (MMO1008), on members and accessors, and the
declaration action checks them on local functions and lambdas. `[DllImport]` and
`[MethodImpl(MethodImplOptions.InternalCall)]` need an `extern` declaration, which MMO1008 reports by
itself.

### Not covered

The agreement suite remains the second line of defence for what the rules cannot see. Known gaps:
`EqualityComparer<string>.Default.GetHashCode` and other indirect routes to a per-process seed, and
library code outside the core that the core calls through the BCL. An order-insensitive sink whose
comparer treats as equal two elements that the source keeps apart (a case-insensitive set built from a
case-sensitive one) keeps whichever comes first. A spread's element conversion is checked when the
collection expression's target is an array, a span or an `IEnumerable<T>`, which every collection type
the core uses is. A hash-ordered value passed as an unconstrained type argument, whose generic code
then type-tests it (`t is IEnumerable<int> e` on a `T`), is not seen, because the pattern's input is
`T`, not a hash-ordered type; the core has no such generic code. `async` and `await` are allowed,
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
  EventReference, ObjectCreation, Lock, Loop, Conversion, Spread, Coalesce and DeconstructionAssignment
  (MMO1009's element conversions), VariableDeclarator, DeclarationPattern and RecursivePattern
  (MMO1004's patterns), Binary and Unary (user-defined operators and float `%`), CompoundAssignment
  (float `%=`), TypeOf, DefaultValue and the four Dynamic kinds. It
  returns at once for an operation under an `IAttributeOperation` ([attribute applications](#attribute-applications)).
- A second `RegisterOperationAction` over LocalFunction and AnonymousFunction, for the declarations
  inside a body, which no symbol action sees: a local function's identifier and parameters, as a
  method's, and a lambda's `Synchronized` flag and written parameter and return types. Its findings
  are declarations, so they take no part in the per-statement deduplication.
- `RegisterSymbolAction` for Field, Property, Method, Event and NamedType declarations (declared types,
  `MethodImplementationFlags` and `extern` on methods and accessors, `[Conditional]`, `[UnsafeAccessor]`
  and `[LibraryImport]`, and a delegate's return and parameter types). It skips the implementation part
  of a partial member, whose defining declaration reports.
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
rows reached 94.4%). T167's coverage rows add a parameter's default value, an event reference, a
function pointer's parameter and return type, an indexer's parameter and argument, a tuple field, a
generic method's type argument, a static initializer, a `double` property read, a nullable
conversion, `Keys` on a `SortedDictionary` (allowed) and the positional record parameters above.

| Rule | Violating samples (count 1 unless noted) | Allowed samples |
| --- | --- | --- |
| MMO1001 | `Vector3.Dot(new Vector3(x), Vector3.UnitX)`; `Vector<float>.Count`; `Vector128.Sum(a)`; `Vector128.Dot(a, a)`; `Vector128.IsHardwareAccelerated`; `new List<Quaternion>()`; `var v = Matrix4x4.Identity;`; `System.Runtime.Intrinsics.X86.Sse.IsSupported` | `Vector128.Add(a, b) * b - Vector128.Create(1f)`; `x * y + 1f` |
| MMO1002 | `MathF.Sin(x)`; `float.Atan2(x, 1f)`; `T.Cos(x)` with `T : ITrigonometricFunctions<T>`; the method group `MathF.Tan`; the function pointer `&MathF.Sin`; `MathF.Pow(x, 2f)`; `MathF.ReciprocalSqrtEstimate(x)`; `MathF.Max(x, 0f)`; `MathF.Round(x, 2)`; `x % 2f`; `Math.Clamp(x, 0f, 1f)` outside `SimMath`; `a % b` and `a %= b` with `T : IModulusOperators<T, T, T>` | `MathF.Sqrt(MathF.Abs(x)) + MathF.Floor(x) + MathF.Round(x) + MathF.Round(x, MidpointRounding.ToZero) + MathF.CopySign(x, -1f) + MathF.PI`; `nameof(MathF.Sin)`; `Math.Max(i, 0) + Math.Clamp(i, 0, 9)`; `float.IsFinite(x) ? BitConverter.SingleToUInt32Bits(x) : 0u`; `a % b` with `T : IBinaryInteger<T>`, and with `T : U` where `U : IBinaryInteger<U>` |
| MMO1003 | `MathF.FusedMultiplyAdd(a, b, c)`; `float.MultiplyAddEstimate(a, a, a)`; `Vector128.FusedMultiplyAdd(a, a, a)`; `System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(a, a, a)` | `a * b + c` |
| MMO1004 | `foreach` over a `Dictionary<int, int>`; `s.First()` on a `HashSet<int>`; `d.Keys.ToArray()`; `new List<int>(s)`; `[.. s]`; `string.Join(",", s)`; returning an `ImmutableHashSet<int>` as `IEnumerable<int>`; `foreach` over `IReadOnlyDictionary<int, int>.Keys`; `d.Keys.CopyTo(dst, 0)`; `from kv in d select kv.Value`; a `HashSet<int>` stored as a `Dictionary<int, IEnumerable<int>>` value by a collection initializer, by `Add` and in a `Hashtable`; a `HashSet<int>` passed to a derived class's constructor; `Sum(h)` with `Sum<T>(T e) where T : IEnumerable<int>`; `new Box<HashSet<int>>(h)` with `Box<T> where T : IEnumerable<int>`; `h is IEnumerable<int> e`; `case IEnumerable<int> e:`; `foreach` over a `T : Dictionary<int, int>`, and over a `U : T` with that `T`; `s.First()` on a `T : HashSet<int>` | `d.TryGetValue(k, out var v) ? v + d.Count : d.Keys.Count`; `foreach` over a `SortedDictionary<int, int>` and over a `T : SortedDictionary<int, int>`; `a.UnionWith(b)` and `a.SetEquals(b)` on two `HashSet<int>`; `new HashSet<int>(s)`; `h is HashSet<int> g ? g.Count : 0`; `l.First()` on a `List<HashSet<int>>`; `new List<HashSet<int>>()` |
| MMO1005 | `DateTime.Now.Ticks`; `DateTime.UtcNow.Ticks`; `Stopwatch.GetTimestamp()`; `Environment.TickCount`; `new Random(42).Next()`; `Random.Shared.Next()`; `Guid.NewGuid()`; `s.GetHashCode()` on a string; `HashCode.Combine(a, b)`; `TimeProvider.System.GetUtcNow()` | `a.GetHashCode() ^ 17` on an `int`; `step * stepTicks` |
| MMO1006 | `lock (Gate) { }`; a `static readonly Lock` field; `lock (l)` on a `Lock` parameter (count 2: the parameter and the statement); `t.Wait()`; `t.Result`; `t.GetAwaiter().GetResult()`; `Thread.Sleep(1)`; `Monitor.Enter(typeof(C))`; `[MethodImpl(MethodImplOptions.Synchronized)]` on a method, a getter, a setter, an event adder, a field-like event (`[method:]`), a local function and a lambda, and only on a partial method's implementation part (reported once, at the defining declaration); a `Lock` parameter of a local function and of a delegate; `Parallel.For(0, a.Length, i => a[i]++)` | `await t`; `Interlocked.Increment(ref x)`; plain accessors and a plain local function |
| MMO1007 | `(float)(x * 0.5)`; `double M(float x) => x` (count 2); `(int)(nint)a`; `var h = (Half)1;`; a local function's `double` parameter; `delegate double D(double x)` (count 2); `(double d) => 1f` returned as `Func<double, float>` (count 2: the return type and `d`); `var f = double () => 0;` (count 2: `f` and the written return type); a partial method `partial double M(double x)` with its implementation (count 2, the defining declaration's return type and `x`), and a partial `double` property and partial `Action<double>` event (count 1 each) | `const double Scale = 1.0 / 3.0` used as `x * (float)Scale`; `x * (float)(Math.PI / 180.0)`; `x => x * 2f` as `Func<float, float>` |
| MMO1008 | `OperatingSystem.IsBrowser()`; `IntPtr.Size`; `Environment.ProcessorCount`; `typeof(MathF).GetMethod("Sin")`; `d.Foo()` on a `dynamic`; an `#if DEBUG` block; `Debug.Assert(b)`; an `[UnsafeAccessor]` extern method; a `[Conditional]` local function and an `[UnsafeAccessor]` extern local function; a `[DllImport("m")] static extern float Sinf(float x)` method and local function; an extern property accessor (`static extern int P { get; }`) | `object M() => typeof(int);` (no member access on the `Type`) |
| MMO1009 | `(int)x`; `(byte)(x * 2f)`; `float M(ulong u) => u`; `Convert.ToInt32(x)`; `int.CreateSaturating(x)`; `long.CreateTruncating(x)`; `(DayOfWeek)x`; `(UInt128)x`; `foreach (int i in floats)`, `foreach (float f in ulongs)`, `foreach (DayOfWeek d in floats)`, and a `foreach` into a struct whose operator takes `int` or from one whose operator returns `float`; `((int, int))t` with `t` a `(float, float)`, nested and nullable too; `(float, float) M((ulong, ulong) t) => t`; `(float p, float q) = t` with `t` a `(ulong, ulong)`, through `Deconstruct` and an extension `Deconstruct` of `ulong` out parameters, and in a `foreach`; `[.. ulongs]` into a `float[]`, `List<float>`, `IEnumerable<float>` and `Span<float>`; `u ?? f` with `u` a `ulong?`, and with tuples; `int.CreateSaturating(v)` and `long.CreateTruncating(v)` with `T : INumberBase<T>` | `(int)2.5f`; `i * 0.5f`; `(float)(uint)(h >> 40)`; `int.CreateSaturating(l)` with `l` a `long`; `int.CreateSaturating(v)` with `T : IBinaryInteger<T>`, and with `U : T` where `T : IBinaryInteger<T>`; `(DayOfWeek)i` with `i` an `int`; `foreach (var f in floats)`; `foreach (float f in ints)`; `((float, float))t` with `t` an `(int, int)`; a deconstruction, a spread and `??` from `uint` |

**Other tests in the project:**

| Test | Asserts |
| --- | --- |
| `PlatformMathMessageNamesTheReplacement(member, replacement)` (Theory) | every row of the replacement map: for example `MathF.Sin` names `'SimMath.Sin'`, `float.Atan2` names `'SimMath.Atan2'`, `MathF.Max` names `'SimMath.Max'`, `Math.Clamp` outside `SimMath` names `'SimMath.Clamp'`, and `MathF.Pow` gets the fallback text |
| `FormsWithoutTheirOwnSyntaxReportWhereTheContractSays(member, rule, reportedText, name, text)` (Theory, `RuleTests`) | the forms with no operation or identifier of their own report exactly one diagnostic, at the place [Messages](#messages) gives and with its `{0}`: a `foreach` element conversion at `int` and at `(float p, float q)`, a deconstruction at `t`, a tuple conversion at `((int, int))t`, a spread at `a` named `..`, `??` at `u`; a getter's and an event remover's `Synchronized` at `get` and `remove` named `C.P.get` and `C.E.remove`, a field-like event's at `E`, a local function's at its identifier, a lambda's at the lambda; a `[Conditional]` local function and a `[DllImport]` extern local function at their identifiers; an extern getter at `get`, named `C.P.get` |
| `HashOrderMessagesNameTheOperationAndTheCollection(member, message)` (Theory) | the full MMO1004 message, `'{0}' enumerates '{1}', …`, for the `foreach` over a `Dictionary<int, int>` (`'foreach'`, `'System.Collections.Generic.Dictionary<int, int>'`), `[.. s]` (`'..'`, `'System.Collections.Generic.HashSet<int>'`), `s.First()` (`'s'`, `'System.Collections.Generic.HashSet<int>'`), the pattern `h is IEnumerable<int> e` (`'IEnumerable<int> e'`) and the generic call `Sum(h)` (`'C.Sum<HashSet<int>>(HashSet<int>)'`, `'System.Collections.Generic.HashSet<int>'`) |
| `EveryContractTableEntryIsEnforced(table, rule, count, member)` (Theory, `TableTests`) | one row per entry of every table under [Allowlists and tables](#allowlists-and-tables) and of the MMO1008 and MMO1009 rules: each allowlist member reports nothing, and each named banned member, type or list entry reports exactly its rule, with the same four assertions as `EachSampleProducesExactlyItsRule` |
| `FusedCallsReportOnlyTheFmaRule(member)` (Theory) | for `MathF.FusedMultiplyAdd`, `Math.FusedMultiplyAdd`, `Vector128.FusedMultiplyAdd` and `Fma.MultiplyAdd`, the diagnostics whose span is the call are exactly one MMO1003; the `double` declarations of the `Math` row report MMO1007 at their own locations |
| `SimMathMayClampAndCastFloats` | `Clamp` anywhere in `MmoEngine.SimCore.Math.SimMath`, and a float-to-`int` cast inside `FloorToInt(float)`, `RoundToInt(float)` or `TruncateToInt(float)`, report nothing; the same cast in another `SimMath` method, in a local function named `RoundToInt` and in a `RoundToInt(float, float)` overload reports MMO1009, and the same code in another type reports MMO1002 and MMO1009 |
| `SeverityCannotBeLowered(mechanism)` (Theory) | with `#pragma warning disable MMO1002`, with `SpecificDiagnosticOptions` set to Suppress (what `NoWarn` and `severity = none` map to), and with warnings not treated as errors, `MathF.Sin` still reports MMO1002 at Error and not suppressed |
| `SuppressMessageIsReportedAsSuppressed` | `[SuppressMessage("Determinism", "MMO1002")]` yields MMO1002 with `IsSuppressed` true, which the backstop counts |
| `DescriptorsMatchTheReleaseFile` | `SupportedDiagnostics` is exactly MMO1001–MMO1009; each is Error, enabled by default, category `Determinism`, tagged `NotConfigurable`; `AnalyzerReleases.Unshipped.md` lists the same nine as Error |
| `CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes` | the backstop above |
| `AttributeApplicationsAreNotReported` | a compilation of the sample class plus a file with an `<auto-generated>` header holding the SDK's assembly attributes (`AssemblyCompany`, `AssemblyConfiguration`, `AssemblyFileVersion`, `AssemblyInformationalVersion`, `AssemblyProduct`, `AssemblyTitle`, `AssemblyVersion`, and `AssemblyMetadata` for `IsTrimmable` and `IsAotCompatible`, all spelled `System.Reflection.…`) and a member with `[Obsolete]` reports nothing; the same file with `typeof(MathF).GetMethod("Sin")` in a method body still reports one MMO1008 |
| `CoreProjectAttachesTheAnalyzerPrivately` | the core's project file has exactly one analyzer `ProjectReference` with `OutputItemType="Analyzer"`, `ReferenceOutputAssembly="false"` and `PrivateAssets="all"`; `ImplicitUsings` is `disable`; nothing in it or in `Directory.Build.props` sets `RunAnalyzers`, `RunAnalyzersDuringBuild`, `NoWarn` or `WarningsNotAsErrors` for MMO rules; there is no `<Compile Remove>`; and each of these defects, made in an in-memory copy, is reported as exactly one problem line |
| `OnlyTheCoreAttachesTheAnalyzer` | no other project file under `src/`, `tests/` or `benchmarks/` attaches it with `OutputItemType="Analyzer"` or an `<Analyzer Include="…MmoEngine.SimCore.Analyzers.dll" />` item; an in-memory copy with either attachment is reported |
| `RoslynPinMatchesTheSdkBand` | the pin above |
| `ABuildWithAViolationFails` | `dotnet build` of a throwaway class library in a unique temporary directory, with `IsTrimmable` and `IsAotCompatible` set as in the core (so the SDK generates the `System.Reflection` assembly attributes), attaching the built analyzer with `<Analyzer Include="…" />` and containing one `foreach` over a `Dictionary`, exits non-zero with exactly one MMO diagnostic in its output, which contains `error MMO1004: 'foreach' enumerates 'System.Collections.Generic.Dictionary<int, int>', whose order is not defined`, and none in the generated `AssemblyInfo.cs` (about 1.1 s warm [measured]); the output is written to the test log |

**Mapping to the acceptance scenarios (US3):**

| Scenario | Tests |
| --- | --- |
| US3.1 vector, quaternion, matrix and horizontal `Vector128` | `EachSampleProducesExactlyItsRule`, MMO1001 rows |
| US3.2 platform math, naming the replacement | MMO1002 rows; `PlatformMathMessageNamesTheReplacement` |
| US3.3 fused multiply-add, and no other rule for the same call | MMO1003 rows; `FusedCallsReportOnlyTheFmaRule` |
| US3.4 hash-ordered iteration | MMO1004 rows; `HashOrderMessagesNameTheOperationAndTheCollection` |
| US3.5 ambient time and randomness | MMO1005 rows |
| US3.6 locks, task waits and blocking primitives | MMO1006 rows; `FormsWithoutTheirOwnSyntaxReportWhereTheContractSays` |
| US3.7 `double`, `Half`, `nint`, `NFloat`; float-to-integer; `ulong`-to-`float`; platform paths | MMO1007, MMO1009 and MMO1008 rows; `SimMathMayClampAndCastFloats`; `FormsWithoutTheirOwnSyntaxReportWhereTheContractSays` |
| Independent Test: "build the core itself with the analyzer as an error" | `CoreSourcesHaveNoDeterminismDiagnosticsEvenSuppressedOnes`, `ABuildWithAViolationFails`, and CI's `dotnet build MmoEngine.sln -warnaserror` |

US3 scenario 8 (the trigonometric bounds) is not the analyzer's. Its bound half is
`tests/MmoEngine.SimCore.Tests/Math/FixedTableBoundsTests.EveryInDomainRowIsWithinItsBound` on the
server build, with `FixedTableBoundsTests.BoundsAreTheResearchR3Values` pinning each `SimMathBounds`
constant to research.md R3's documented bound. Its cross-build half ("identical bits for every input") is the tables check of the
agreement runners, `tools/agreement/node/agreement.test.mjs` and `tools/agreement/specs/worker.spec.mjs`,
with `agreement compare` diffing every row ([wasm-api.md](wasm-api.md#cross-build-tables)). The
recorded streams alone cannot cover it, because they never call most of `SimMath`.

**Timings** [measured, sandbox]: loading the reference assemblies takes about 67 ms, the first
compile-and-analyze about 970 ms (Roslyn's JIT), and each later one 5–7 ms. The prototype's 81 tests
ran in about 2 s.
