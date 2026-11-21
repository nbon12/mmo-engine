using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace MmoEngine.SimCore.Analyzers;

/// <summary>
/// The data behind the determinism rules, per
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md#allowlists-and-tables. Types are keyed
/// by their original definition's metadata name (<c>Namespace.Type`1+Nested</c>), members by that name
/// plus <c>.</c> and the member's metadata name, and single overloads by their documentation comment ID.
/// The tables live in the analyzer's source and every set is built once, so the core cannot widen its
/// own rules; <c>TableTests.EveryContractTableEntryIsEnforced</c> pins every entry.
/// </summary>
internal static class Tables
{
    // ---- MMO1001: vector and SIMD types -------------------------------------------------------

    /// <summary>The System.Numerics vector, quaternion, matrix and plane types, banned wherever they appear.</summary>
    public static readonly ImmutableHashSet<string> NumericsVectorTypes = Set(
        "System.Numerics.Vector`1",
        "System.Numerics.Vector",
        "System.Numerics.Vector2",
        "System.Numerics.Vector3",
        "System.Numerics.Vector4",
        "System.Numerics.Quaternion",
        "System.Numerics.Matrix3x2",
        "System.Numerics.Matrix4x4",
        "System.Numerics.Plane");

    /// <summary>The prefix of every type in System.Runtime.Intrinsics and its sub-namespaces (the ISA classes).</summary>
    public const string IntrinsicsPrefix = "System.Runtime.Intrinsics.";

    /// <summary>The static <c>Vector128</c> class, whose allowlisted members are lane-wise.</summary>
    public const string Vector128 = "System.Runtime.Intrinsics.Vector128";

    /// <summary><c>Vector128&lt;T&gt;</c>, allowed only with element type <c>float</c>.</summary>
    public const string Vector128OfT = "System.Runtime.Intrinsics.Vector128`1";

    /// <summary>
    /// The lane-wise <c>Vector128</c> and <c>Vector128&lt;T&gt;</c> members (contracts/analyzer-rules.md#vector128-allowlist-mmo1001).
    /// Everything else on these types, and every other type in System.Runtime.Intrinsics, is MMO1001.
    /// </summary>
    public static readonly ImmutableHashSet<string> Vector128Allowlist = Members(
            [Vector128],
            "Create",
            "CreateScalar",
            "Add",
            "Subtract",
            "Multiply",
            "Divide",
            "Negate",
            "Sqrt",
            "Abs",
            "BitwiseAnd",
            "BitwiseOr",
            "Xor",
            "AndNot",
            "OnesComplement",
            "ConditionalSelect",
            "Equals",
            "LessThan",
            "LessThanOrEqual",
            "GreaterThan",
            "GreaterThanOrEqual",
            "GetElement",
            "WithElement",
            "ToScalar",
            "CopyTo")
        .Union(Members(
            [Vector128OfT],
            "Zero",
            "One",
            "AllBitsSet",
            "Count",
            "Item",
            "op_Addition",
            "op_Subtraction",
            "op_Multiply",
            "op_Division",
            "op_UnaryNegation",
            "op_UnaryPlus",
            "op_BitwiseAnd",
            "op_BitwiseOr",
            "op_ExclusiveOr",
            "op_OnesComplement",
            "op_Equality",
            "op_Inequality"));

    // ---- MMO1003: fused multiply-add ----------------------------------------------------------

    /// <summary>The name prefix of a fused member on any type (<c>FusedMultiplyAdd</c> and its relatives).</summary>
    public const string FusedPrefix = "FusedMultiply";

    /// <summary>The other fused member names, on any type.</summary>
    public static readonly ImmutableHashSet<string> FusedMembers = Set("MultiplyAddEstimate");

    /// <summary>The x86 FMA intrinsics classes, every member of which is MMO1003.</summary>
    public static readonly ImmutableHashSet<string> FmaTypes = Set(
        "System.Runtime.Intrinsics.X86.Fma",
        "System.Runtime.Intrinsics.X86.Fma+X64");

    // ---- MMO1002: platform math ---------------------------------------------------------------

    /// <summary>The math host types whose static methods are MMO1002 unless allowlisted.</summary>
    public static readonly ImmutableHashSet<string> MathHostTypes = Set(
        "System.Math",
        "System.MathF",
        "System.Single",
        "System.Double",
        "System.Half",
        "System.Runtime.InteropServices.NFloat");

    /// <summary>The namespace of the generic-math interfaces, whose static members are math hosts too (<c>T.Cos(x)</c>).</summary>
    public const string GenericMathNamespace = "System.Numerics";

    /// <summary>The exact or correctly rounded members, and the bit and exponent operations, allowed with <c>float</c> operands.</summary>
    public static readonly ImmutableHashSet<string> FloatOperandMath = Members(
        ["System.Math", "System.MathF", "System.Single"],
        "Sqrt",
        "Abs",
        "CopySign",
        "Floor",
        "Ceiling",
        "Truncate",
        "BitIncrement",
        "BitDecrement",
        "ScaleB",
        "ILogB");

    /// <summary>
    /// The two allowed <c>Round</c> overloads (ties to even, or an explicit <see cref="MidpointRounding"/>),
    /// by documentation comment ID, so <c>Round(x, digits)</c> is not allowed. <c>System.Math</c> has no
    /// <c>float</c> <c>Round</c>.
    /// </summary>
    public static readonly ImmutableHashSet<string> RoundOverloads = Set(
        "M:System.MathF.Round(System.Single)",
        "M:System.MathF.Round(System.Single,System.MidpointRounding)",
        "M:System.Single.Round(System.Single)",
        "M:System.Single.Round(System.Single,System.MidpointRounding)");

    /// <summary>The host of the <c>float.Is*</c> predicates, every one of which is allowed.</summary>
    public const string PredicateHost = "System.Single";

    /// <summary>The name prefix of the <c>float.Is*</c> predicates (<c>IsNaN</c>, <c>IsFinite</c> and the rest).</summary>
    public const string PredicatePrefix = "Is";

    /// <summary>The <c>System.Math</c> members allowed with integer operands, everywhere.</summary>
    public static readonly ImmutableHashSet<string> IntegerOperandMath = Members(
        ["System.Math"],
        "Min",
        "Max",
        "Abs",
        "Clamp",
        "BigMul",
        "DivRem");

    /// <summary>The float members allowed only inside <see cref="SimMathType"/>.</summary>
    public static readonly ImmutableHashSet<string> SimMathOnlyMath = Members(
        ["System.Math", "System.Single"],
        "Clamp");

    /// <summary>The core's own math class, the one place a float <c>Clamp</c> is allowed.</summary>
    public const string SimMathType = "MmoEngine.SimCore.Math.SimMath";

    /// <summary>
    /// What MMO1002's message offers instead of a banned member, by member name
    /// (contracts/analyzer-rules.md#replacement-map-mmo1002-messages). A member that is not here gets
    /// <see cref="MathFallback"/>.
    /// </summary>
    public static readonly ImmutableDictionary<string, string> MathReplacements = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Sin"] = "'SimMath.Sin'",
        ["Cos"] = "'SimMath.Cos'",
        ["Tan"] = "'SimMath.Tan'",
        ["SinCos"] = "'SimMath.SinCos'",
        ["Asin"] = "'SimMath.Asin'",
        ["Acos"] = "'SimMath.Acos'",
        ["Atan"] = "'SimMath.Atan'",
        ["Atan2"] = "'SimMath.Atan2'",
        ["SinPi"] = "'SimMath.SinCosBinaryAngle' for binary angles",
        ["CosPi"] = "'SimMath.SinCosBinaryAngle' for binary angles",
        ["SinCosPi"] = "'SimMath.SinCosBinaryAngle' for binary angles",
        ["Min"] = "'SimMath.Min'",
        ["MinMagnitude"] = "'SimMath.Min'",
        ["MinNumber"] = "'SimMath.Min'",
        ["Max"] = "'SimMath.Max'",
        ["MaxMagnitude"] = "'SimMath.Max'",
        ["MaxNumber"] = "'SimMath.Max'",
        ["Clamp"] = "'SimMath.Clamp'",
        ["ReciprocalEstimate"] = "'1f / x'",
        ["ReciprocalSqrtEstimate"] = "'1f / SimMath.Sqrt(x)'",
        ["Round"] = "'MathF.Round(x)' on a scaled value, or 'SimMath.RoundToInt'",
        ["Lerp"] = "'a + ((b - a) * t)' written as separate float operations",
        ["DegreesToRadians"] = "a multiplication by a float constant",
        ["RadiansToDegrees"] = "a multiplication by a float constant",
    }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>MMO1002's replacement for every other banned member, and for float <c>%</c> and <c>%=</c>.</summary>
    public const string MathFallback = "scalar float arithmetic, or add a core function with a documented error bound";

    // ---- MMO1004: hash-ordered collections ----------------------------------------------------

    /// <summary>
    /// The hash-ordered collections (contracts/analyzer-rules.md#hash-ordered-collections-mmo1004). A
    /// class deriving from one of the classes is hash-ordered too; an interface counts only as the
    /// static type itself, so <c>SortedDictionary</c>, which implements <c>IDictionary</c>, is not.
    /// </summary>
    public static readonly ImmutableHashSet<string> HashOrderedTypes = Set(
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.Dictionary`2+KeyCollection",
        "System.Collections.Generic.Dictionary`2+ValueCollection",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Hashtable",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Immutable.ImmutableDictionary`2",
        "System.Collections.Immutable.ImmutableHashSet`1",
        "System.Collections.Frozen.FrozenDictionary`2",
        "System.Collections.Frozen.FrozenSet`1",
        "System.Collections.ObjectModel.ReadOnlyDictionary`2",
        "System.Collections.ObjectModel.ReadOnlyDictionary`2+KeyCollection",
        "System.Collections.ObjectModel.ReadOnlyDictionary`2+ValueCollection",
        "System.Collections.Specialized.HybridDictionary",
        "System.Runtime.CompilerServices.ConditionalWeakTable`2",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.IDictionary");

    /// <summary>The instance methods of a hash-ordered collection that enumerate it or copy it out in hash order.</summary>
    public static readonly ImmutableHashSet<string> EnumeratingMethods = Set("GetEnumerator", "CopyTo", "ToArray");

    /// <summary>The views of a hash-ordered collection, reported only when their own type is not hash-ordered.</summary>
    public static readonly ImmutableHashSet<string> EnumeratingProperties = Set("Keys", "Values");

    /// <summary>
    /// The set operations that take another collection and whose result does not depend on its order.
    /// A hash-ordered collection passed to one of them, declared on a listed type, is not enumerated in
    /// a way the step can observe (contracts/analyzer-rules.md#hash-ordered-collections-mmo1004).
    /// </summary>
    public static readonly ImmutableHashSet<string> OrderInsensitiveSinkMethods = Set(
        "UnionWith",
        "IntersectWith",
        "ExceptWith",
        "SymmetricExceptWith",
        "SetEquals",
        "Overlaps",
        "IsSubsetOf",
        "IsSupersetOf",
        "IsProperSubsetOf",
        "IsProperSupersetOf",
        "Union",
        "Intersect",
        "Except",
        "SymmetricExcept");

    // ---- MMO1005: ambient time, randomness and hash seeds -------------------------------------

    /// <summary>
    /// The clocks, timers, random sources and per-process hash seeds every member and constructor of
    /// which is MMO1005 (contracts/analyzer-rules.md#ambient-time-randomness-and-hash-seeds-mmo1005).
    /// A class derived from one counts too.
    /// </summary>
    public static readonly ImmutableHashSet<string> AmbientTypes = Set(
        "System.Diagnostics.Stopwatch",
        "System.TimeProvider",
        "System.Random",
        "System.Security.Cryptography.RandomNumberGenerator",
        "System.HashCode",
        "System.Threading.Timer",
        "System.Threading.PeriodicTimer",
        "System.Timers.Timer");

    /// <summary>
    /// The single members that read a clock, draw a random value or hash with a per-process seed.
    /// <c>object.GetHashCode</c> and <c>ValueType.GetHashCode</c> are here because Roslyn binds a
    /// <c>GetHashCode</c> call to the receiver type's own override when it has one, so a call that still
    /// targets one of these two is on a type that does not override it.
    /// </summary>
    public static readonly ImmutableHashSet<string> AmbientMembers = Set(
        "System.DateTime.Now",
        "System.DateTime.UtcNow",
        "System.DateTime.Today",
        "System.DateTimeOffset.Now",
        "System.DateTimeOffset.UtcNow",
        "System.Environment.TickCount",
        "System.Environment.TickCount64",
        "System.Guid.NewGuid",
        "System.Guid.CreateVersion7",
        "System.String.GetHashCode",
        "System.Object.GetHashCode",
        "System.ValueType.GetHashCode",
        "System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode",
        "System.Threading.Tasks.Task.Delay");

    // ---- MMO1006: locks and blocking ----------------------------------------------------------

    /// <summary>
    /// The locks, wait handles, spin primitives and blocking collections every use and declaration of
    /// which is MMO1006 (contracts/analyzer-rules.md#locks-and-blocking-mmo1006). A class derived from
    /// one counts too.
    /// </summary>
    public static readonly ImmutableHashSet<string> BlockingTypes = Set(
        "System.Threading.Monitor",
        "System.Threading.Lock",
        "System.Threading.Mutex",
        "System.Threading.Semaphore",
        "System.Threading.SemaphoreSlim",
        "System.Threading.ReaderWriterLock",
        "System.Threading.ReaderWriterLockSlim",
        "System.Threading.SpinLock",
        "System.Threading.SpinWait",
        "System.Threading.ManualResetEvent",
        "System.Threading.ManualResetEventSlim",
        "System.Threading.AutoResetEvent",
        "System.Threading.EventWaitHandle",
        "System.Threading.WaitHandle",
        "System.Threading.CountdownEvent",
        "System.Threading.Barrier",
        "System.Collections.Concurrent.BlockingCollection`1",
        "System.Threading.Tasks.Parallel");

    /// <summary>The single members that block: thread sleeps and joins, task waits and results, and every awaiter's <c>GetResult</c>.</summary>
    public static readonly ImmutableHashSet<string> BlockingMembers = Set(
        "System.Threading.Thread.Sleep",
        "System.Threading.Thread.Join",
        "System.Threading.Thread.SpinWait",
        "System.Threading.Thread.Yield",
        "System.Threading.Tasks.Task.Wait",
        "System.Threading.Tasks.Task.WaitAll",
        "System.Threading.Tasks.Task.WaitAny",
        "System.Threading.Tasks.Task.RunSynchronously",
        "System.Threading.Tasks.Task`1.Result",
        "System.Threading.Tasks.ValueTask`1.Result",
        "System.Runtime.CompilerServices.TaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.TaskAwaiter`1.GetResult",
        "System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ValueTaskAwaiter`1.GetResult",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter.GetResult",
        "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter.GetResult");

    // ---- MMO1007: numeric types ---------------------------------------------------------------

    /// <summary>
    /// The numeric types the core may not declare or compute in, besides <c>double</c>, <c>nint</c>
    /// and <c>nuint</c>, which are matched by their special type
    /// (contracts/analyzer-rules.md#numeric-types-mmo1007).
    /// </summary>
    public static readonly ImmutableHashSet<string> BannedNumericTypes = Set(
        "System.Half",
        "System.Runtime.InteropServices.NFloat");

    // ---- MMO1008: platform queries, conditional compilation and reflection --------------------

    /// <summary>
    /// The types every member of which is MMO1008: <c>Debug</c> and <c>Trace</c>, the platform and
    /// runtime queries, <c>Environment</c> (except its two clocks, which are MMO1005),
    /// <c>System.Type</c> and <c>Activator</c>.
    /// </summary>
    public static readonly ImmutableHashSet<string> PlatformPathTypes = Set(
        "System.Diagnostics.Debug",
        "System.Diagnostics.Trace",
        "System.OperatingSystem",
        "System.Runtime.InteropServices.RuntimeInformation",
        "System.Runtime.CompilerServices.RuntimeFeature",
        "System.Environment",
        "System.Type",
        "System.Activator");

    /// <summary>The prefix of every type in System.Reflection and its sub-namespaces, every member of which is MMO1008.</summary>
    public const string ReflectionPrefix = "System.Reflection.";

    /// <summary>The single members that ask which platform the build runs on.</summary>
    public static readonly ImmutableHashSet<string> PlatformPathMembers = Set(
        "System.IntPtr.Size",
        "System.UIntPtr.Size",
        "System.BitConverter.IsLittleEndian");

    /// <summary>The <see cref="PlatformPathTypes"/> members that are clocks, which MMO1005 reports instead.</summary>
    public static readonly ImmutableHashSet<string> PlatformPathExemptions = Set(
        "System.Environment.TickCount",
        "System.Environment.TickCount64");

    /// <summary>The attributes whose declarations are MMO1008: a call that some builds drop, and an accessor that reaches private members.</summary>
    public static readonly ImmutableHashSet<string> PlatformPathAttributes = Set(
        "System.Diagnostics.ConditionalAttribute",
        "System.Runtime.CompilerServices.UnsafeAccessorAttribute");

    // ---- MMO1009: float-to-integer and ulong-to-float conversions -----------------------------

    /// <summary>The <c>MmoEngine.SimCore.Math.SimMath</c> methods in whose bodies a float-to-integer conversion is allowed: they saturate first.</summary>
    public static readonly ImmutableHashSet<string> SimMathIntegerHelpers = Set("FloorToInt", "RoundToInt", "TruncateToInt");

    /// <summary><c>System.Convert</c>, every member of which is MMO1009.</summary>
    public const string ConvertType = "System.Convert";

    /// <summary>The span types a collection expression can target, whose element type is their one type argument.</summary>
    public static readonly ImmutableHashSet<string> SpanTypes = Set("System.Span`1", "System.ReadOnlySpan`1");

    /// <summary>The 128-bit integer types, which a float conversion reaches through a user-defined operator.</summary>
    public static readonly ImmutableHashSet<string> WideIntegerTypes = Set("System.Int128", "System.UInt128");

    /// <summary>
    /// The <c>Create*</c> methods of the integer types, MMO1009 when their <c>TOther</c> is one of
    /// <see cref="FloatingPointTypes"/>: CoreLib implements them with the raw conversion.
    /// </summary>
    public static readonly ImmutableHashSet<string> IntegerCreateMethods = Members(
        [
            "System.SByte",
            "System.Byte",
            "System.Int16",
            "System.UInt16",
            "System.Int32",
            "System.UInt32",
            "System.Int64",
            "System.UInt64",
            "System.Char",
            "System.Int128",
            "System.UInt128",
        ],
        "CreateTruncating",
        "CreateSaturating",
        "CreateChecked");

    /// <summary>The floating-point types an integer <c>Create*</c> may not convert from.</summary>
    public static readonly ImmutableHashSet<string> FloatingPointTypes = Set(
        "System.Single",
        "System.Double",
        "System.Half",
        "System.Runtime.InteropServices.NFloat");

    /// <summary>MMO1009's <c>{1}</c> for a float-to-integer or float-to-enum conversion, an integer <c>Create*</c> from a float, or <c>System.Convert</c>.</summary>
    public const string FloatToIntegerReason =
        "converts a floating-point value to an integer, which gives different results between builds for NaN and out-of-range values; use 'SimMath.FloorToInt', 'SimMath.RoundToInt' or 'SimMath.TruncateToInt', which saturate first";

    /// <summary>MMO1009's <c>{1}</c> for a <c>ulong</c>-to-<c>float</c> conversion.</summary>
    public const string UlongToFloatReason =
        "converts a 'ulong' to 'float', which the WebAssembly build rounds through 'double' and so can differ by one ulp; narrow it to 'uint' or 'long' first";

    // ---- Matching -----------------------------------------------------------------------------

    /// <summary>
    /// The metadata name of <paramref name="type"/>'s original definition: <c>Namespace.Type`1</c>, with
    /// <c>+</c> before each nested type's name (<c>System.Collections.Generic.Dictionary`2+KeyCollection</c>).
    /// </summary>
    public static string FullName(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        if (definition.ContainingType is { } outer)
        {
            return FullName(outer) + "+" + definition.MetadataName;
        }

        var ns = definition.ContainingNamespace;
        return ns is null || ns.IsGlobalNamespace ? definition.MetadataName : ns.ToDisplayString() + "." + definition.MetadataName;
    }

    /// <summary>The table key of a member: its containing type's <see cref="FullName"/>, <c>.</c> and the member's metadata name.</summary>
    public static string MemberKey(ISymbol member) =>
        member.ContainingType is { } type ? FullName(type) + "." + member.OriginalDefinition.MetadataName : member.MetadataName;

    private static ImmutableHashSet<string> Set(params string[] items) => ImmutableHashSet.Create(StringComparer.Ordinal, items);

    private static ImmutableHashSet<string> Members(string[] types, params string[] members) =>
        ImmutableHashSet.CreateRange(StringComparer.Ordinal, types.SelectMany(type => members.Select(member => type + "." + member)));
}
