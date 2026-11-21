using Microsoft.CodeAnalysis;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// The table pin (US3.1 to US3.7, FR-014), per
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md#allowlists-and-tables: one row for every
/// entry the contract's tables name, allowed or banned, and for every entry of the MMO1008 and MMO1009
/// rules. An allowlist member reports nothing; a banned member, type or list entry reports exactly its
/// rule. A table entry that is dropped, misspelled or wrongly allowed in the analyzer's source fails its
/// row here. <c>RuleTests.EachSampleProducesExactlyItsRule</c> is each rule's sample set; a row may
/// repeat one of its samples.
/// </summary>
public sealed class TableTests
{
    private const string Vector128Allowlist = "Vector128 allowlist";

    private const string Vector128Exclusions = "Vector128 exclusions";

    private const string MathAllowlist = "Math allowlist";

    private const string MathExclusions = "Math exclusions";

    private const string HashOrdered = "Hash-ordered collections";

    private const string OrderInsensitiveSinks = "Order-insensitive sinks";

    private const string Ambient = "Ambient time, randomness and hash seeds";

    private const string Blocking = "Locks and blocking";

    private const string NumericTypes = "Numeric types";

    private const string PlatformPaths = "Platform paths";

    private const string Conversions = "Conversions";

    /// <summary>The <c>MethodImpl</c> attribute that sets the <c>Synchronized</c> flag, as the samples write it.</summary>
    private const string Synchronized =
        "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]";

    /// <summary>
    /// contracts/analyzer-rules.md#vector128-allowlist-mmo1001: the 24 static <c>Vector128</c> members
    /// (<c>Create</c> through <c>CopyTo</c>), and the <c>Vector128&lt;T&gt;</c> members and operators,
    /// all on <c>Vector128&lt;float&gt;</c>, report nothing.
    /// </summary>
    public static TheoryData<string, string, int, string> Vector128AllowlistRows => new()
    {
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(float x) => Vector128.Create(x);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(float x) => Vector128.CreateScalar(x);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Add(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Subtract(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Multiply(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Divide(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a) => Vector128.Negate(a);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a) => Vector128.Sqrt(a);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a) => Vector128.Abs(a);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.BitwiseAnd(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.BitwiseOr(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Xor(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.AndNot(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a) => Vector128.OnesComplement(a);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.ConditionalSelect(a, a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Equals(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.LessThan(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.LessThanOrEqual(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.GreaterThan(a, b);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.GreaterThanOrEqual(a, b);" },
        { Vector128Allowlist, "", 0, "public static float M(Vector128<float> a) => Vector128.GetElement(a, 0);" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, float x) => Vector128.WithElement(a, 0, x);" },
        { Vector128Allowlist, "", 0, "public static float M(Vector128<float> a) => Vector128.ToScalar(a);" },
        { Vector128Allowlist, "", 0, "public static void M(Vector128<float> a, float[] d) => Vector128.CopyTo(a, d);" },

        { Vector128Allowlist, "", 0, "public static Vector128<float> M() => Vector128<float>.Zero;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M() => Vector128<float>.One;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M() => Vector128<float>.AllBitsSet;" },
        { Vector128Allowlist, "", 0, "public static int M() => Vector128<float>.Count;" },
        { Vector128Allowlist, "", 0, "public static float M(Vector128<float> a) => a[0];" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => a + b;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => a - b;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => a * b;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => a / b;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a) => -a;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a) => +a;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => a & b;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => a | b;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => a ^ b;" },
        { Vector128Allowlist, "", 0, "public static Vector128<float> M(Vector128<float> a) => ~a;" },
        { Vector128Allowlist, "", 0, "public static bool M(Vector128<float> a, Vector128<float> b) => a == b;" },
        { Vector128Allowlist, "", 0, "public static bool M(Vector128<float> a, Vector128<float> b) => a != b;" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#vector128-allowlist-mmo1001 and the MMO1001 rule: everything else on
    /// <c>Vector128</c> and <c>Vector128&lt;T&gt;</c>, a <c>Vector128&lt;T&gt;</c> whose element type is
    /// not <c>float</c>, <c>Vector64</c>, <c>Vector256</c> and <c>Vector512</c> (with the allowlisted
    /// <c>Create</c>, so the type alone is what reports), the X86, Arm and Wasm ISA classes, and each
    /// System.Numerics type of the rule report MMO1001. <c>FusedMultiplyAdd</c> and the one
    /// <c>*Estimate</c> member, <c>MultiplyAddEstimate</c>, report MMO1003, which comes first.
    /// </summary>
    public static TheoryData<string, string, int, string> Vector128ExclusionRows => new()
    {
        { Vector128Exclusions, "MMO1001", 1, "public static float M(Vector128<float> a) => Vector128.Sum(a);" },
        { Vector128Exclusions, "MMO1001", 1, "public static float M(Vector128<float> a) => Vector128.Dot(a, a);" },
        { Vector128Exclusions, "MMO1001", 1, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Min(a, b);" },
        { Vector128Exclusions, "MMO1001", 1, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Max(a, b);" },
        { Vector128Exclusions, "MMO1001", 1, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.MinNative(a, b);" },
        { Vector128Exclusions, "MMO1001", 1, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.MaxNative(a, b);" },
        { Vector128Exclusions, "MMO1001", 1, "public static Vector128<float> M(float x) => Vector128.CreateScalarUnsafe(x);" },
        { Vector128Exclusions, "MMO1001", 1, "public static Vector128<float> M(float[] d) => Vector128.LoadUnsafe(ref d[0]);" },
        { Vector128Exclusions, "MMO1001", 1, "public static bool M() => Vector128.IsHardwareAccelerated;" },
        { Vector128Exclusions, "MMO1001", 1, "public static bool M() => Vector128<float>.IsSupported;" },
        { Vector128Exclusions, "MMO1001", 1, "public static bool M(Vector128<float> a, Vector128<float> b) => a.Equals(b);" },
        { Vector128Exclusions, "MMO1001", 1, "public static int M(Vector128<float> a) => a.GetHashCode();" },
        { Vector128Exclusions, "MMO1003", 1, "public static Vector128<float> M(Vector128<float> a) => Vector128.FusedMultiplyAdd(a, a, a);" },
        { Vector128Exclusions, "MMO1003", 1, "public static Vector128<float> M(Vector128<float> a) => Vector128.MultiplyAddEstimate(a, a, a);" },

        { Vector128Exclusions, "MMO1001", 1, "public static void M() { var v = Vector128.Create(1); }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M(float x) { var v = Vector64.Create(x); }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M(float x) { var v = Vector256.Create(x); }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M(float x) { var v = Vector512.Create(x); }" },
        { Vector128Exclusions, "MMO1001", 1, "public static bool M() => System.Runtime.Intrinsics.X86.Sse.IsSupported;" },
        { Vector128Exclusions, "MMO1001", 1, "public static bool M() => System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported;" },
        { Vector128Exclusions, "MMO1001", 1, "public static bool M() => System.Runtime.Intrinsics.Wasm.PackedSimd.IsSupported;" },

        { Vector128Exclusions, "MMO1001", 1, "public static int M() => Vector<float>.Count;" },
        { Vector128Exclusions, "MMO1001", 1, "public static bool M() => Vector.IsHardwareAccelerated;" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M(float x) { var v = new Vector2(x); }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M(float x) { var v = new Vector3(x); }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M(float x) { var v = new Vector4(x); }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M() { var q = Quaternion.Identity; }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M() { var m = Matrix3x2.Identity; }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M() { var m = Matrix4x4.Identity; }" },
        { Vector128Exclusions, "MMO1001", 1, "public static void M(float x) { var p = new Plane(x, x, x, x); }" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#math-allowlist-mmo1002: the exact and correctly rounded members, the
    /// bit and exponent operations and both allowed <c>Round</c> overloads on <c>MathF</c> and on
    /// <c>float</c>, <c>Math.Abs(float)</c>, the <c>float.Is*</c> predicates, the constants, and the
    /// integer <c>Math</c> members report nothing.
    /// </summary>
    public static TheoryData<string, string, int, string> MathAllowlistRows => new()
    {
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.Sqrt(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.Sqrt(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.Abs(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.Abs(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.CopySign(x, -1f);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.CopySign(x, -1f);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.Floor(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.Floor(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.Ceiling(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.Ceiling(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.Truncate(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.Truncate(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.Round(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.Round(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.Round(x, MidpointRounding.AwayFromZero);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.Round(x, MidpointRounding.AwayFromZero);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.BitIncrement(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.BitIncrement(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.BitDecrement(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.BitDecrement(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => MathF.ScaleB(x, 2);" },
        { MathAllowlist, "", 0, "public static float M(float x) => float.ScaleB(x, 2);" },
        { MathAllowlist, "", 0, "public static int M(float x) => MathF.ILogB(x);" },
        { MathAllowlist, "", 0, "public static int M(float x) => float.ILogB(x);" },
        { MathAllowlist, "", 0, "public static float M(float x) => Math.Abs(x);" },

        { MathAllowlist, "", 0, "public static bool M(float x) => float.IsNaN(x);" },
        { MathAllowlist, "", 0, "public static bool M(float x) => float.IsFinite(x);" },
        { MathAllowlist, "", 0, "public static bool M(float x) => float.IsInfinity(x);" },
        { MathAllowlist, "", 0, "public static bool M(float x) => float.IsNegative(x);" },
        { MathAllowlist, "", 0, "public static bool M(float x) => float.IsNormal(x);" },
        { MathAllowlist, "", 0, "public static bool M(float x) => float.IsSubnormal(x);" },

        { MathAllowlist, "", 0, "public static float M() => MathF.PI;" },
        { MathAllowlist, "", 0, "public static float M() => MathF.E;" },
        { MathAllowlist, "", 0, "public static float M() => MathF.Tau;" },
        { MathAllowlist, "", 0, "public static float M() => float.Epsilon;" },

        { MathAllowlist, "", 0, "public static int M(int a, int b) => Math.Min(a, b);" },
        { MathAllowlist, "", 0, "public static int M(int a, int b) => Math.Max(a, b);" },
        { MathAllowlist, "", 0, "public static int M(int a) => Math.Abs(a);" },
        { MathAllowlist, "", 0, "public static int M(int a) => Math.Clamp(a, 0, 9);" },
        { MathAllowlist, "", 0, "public static long M(int a, int b) => Math.BigMul(a, b);" },
        { MathAllowlist, "", 0, "public static (int, int) M(int a, int b) => Math.DivRem(a, b);" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#math-allowlist-mmo1002: everything else on the math host types, on
    /// <c>MathF</c> where it declares the member and on <c>float</c> where it does not, float <c>%</c>
    /// and <c>%=</c> (also through a type parameter that may be a float, while an
    /// <c>IBinaryInteger&lt;T&gt;</c> <c>%</c> is allowed), and float <c>Math.Clamp</c> outside
    /// <c>SimMath</c> report MMO1002.
    /// </summary>
    public static TheoryData<string, string, int, string> MathExclusionRows => new()
    {
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Sin(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Cos(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Tan(x);" },
        { MathExclusions, "MMO1002", 1, "public static (float, float) M(float x) => MathF.SinCos(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Asin(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Acos(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Atan(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x, float y) => MathF.Atan2(x, y);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Sinh(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Exp(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Log(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Pow(x, 2f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Cbrt(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.RootN(x, 3);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.Hypot(x, 1f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.SinPi(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.CosPi(x);" },
        { MathExclusions, "MMO1002", 1, "public static (float, float) M(float x) => float.SinCosPi(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.TanPi(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Min(x, 0f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Max(x, 0f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.MinMagnitude(x, 0f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.MaxMagnitude(x, 0f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.MinNumber(x, 0f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.MaxNumber(x, 0f);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.ReciprocalEstimate(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.ReciprocalSqrtEstimate(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Round(x, 2);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => MathF.Round(x, 2, MidpointRounding.ToEven);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float a, float b, float t) => float.Lerp(a, b, t);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.DegreesToRadians(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => float.RadiansToDegrees(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x, float y) => MathF.IEEERemainder(x, y);" },
        { MathExclusions, "MMO1002", 1, "public static int M(float x) => MathF.Sign(x);" },
        { MathExclusions, "MMO1002", 1, "public static float M(string s) => float.Parse(s);" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => x % 2f;" },
        { MathExclusions, "MMO1002", 1, "public static void M(ref float x) { x %= 2f; }" },
        { MathExclusions, "MMO1002", 1, "public static float M(float x) => Math.Clamp(x, 0f, 1f);" },
        { MathExclusions, "MMO1002", 1, "public static T M<T>(T a, T b) where T : IModulusOperators<T, T, T> => a % b;" },
        { MathExclusions, "MMO1002", 1, "public static void M<T>(ref T a, T b) where T : IModulusOperators<T, T, T> => a %= b;" },
        { MathExclusions, "", 0, "public static T M<T>(T a, T b) where T : IBinaryInteger<T> => a % b;" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#hash-ordered-collections-mmo1004: one <c>foreach</c> over each type and
    /// interface of the list, written with its full namespace (the immutable builders and
    /// <c>StringDictionary</c> included), the enumerating members <c>GetEnumerator</c>, <c>CopyTo</c> and
    /// <c>ToArray</c> on a <c>HashSet&lt;int&gt;</c>, and <c>FrozenSet&lt;int&gt;.Items</c>, report MMO1004.
    /// </summary>
    public static TheoryData<string, string, int, string> HashOrderedRows => new()
    {
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.Dictionary<int, int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.Dictionary<int, int>.KeyCollection") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.Dictionary<int, int>.ValueCollection") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.HashSet<int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Hashtable") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Concurrent.ConcurrentDictionary<int, int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Concurrent.ConcurrentBag<int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Immutable.ImmutableDictionary<int, int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Immutable.ImmutableHashSet<int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Immutable.ImmutableDictionary<int, int>.Builder") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Immutable.ImmutableHashSet<int>.Builder") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Frozen.FrozenDictionary<int, int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Frozen.FrozenSet<int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.ObjectModel.ReadOnlyDictionary<int, int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.ObjectModel.ReadOnlyDictionary<int, int>.KeyCollection") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.ObjectModel.ReadOnlyDictionary<int, int>.ValueCollection") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Specialized.HybridDictionary") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Specialized.StringDictionary") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Runtime.CompilerServices.ConditionalWeakTable<object, object>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.IDictionary<int, int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.IReadOnlyDictionary<int, int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.ISet<int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.Generic.IReadOnlySet<int>") },
        { HashOrdered, "MMO1004", 1, Foreach("System.Collections.IDictionary") },

        { HashOrdered, "MMO1004", 1, "public static System.Collections.Generic.HashSet<int>.Enumerator M(System.Collections.Generic.HashSet<int> s) => s.GetEnumerator();" },
        { HashOrdered, "MMO1004", 1, "public static void M(System.Collections.Generic.HashSet<int> s, int[] a) => s.CopyTo(a);" },
        { HashOrdered, "MMO1004", 1, "public static int[] M(System.Collections.Generic.HashSet<int> s) => s.ToArray();" },
        { HashOrdered, "MMO1004", 1, "public static int M(System.Collections.Frozen.FrozenSet<int> s) => s.Items[0];" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#hash-ordered-collections-mmo1004, order-insensitive sinks: a
    /// <c>HashSet&lt;int&gt;</c> passed to each set operation of the list, declared on a listed type, or
    /// to a listed type's copy constructor, reports nothing. Passed to any other member it reports
    /// MMO1004 once: <c>Add</c>, <c>TryAdd</c> and a collection initializer of a dictionary,
    /// <c>Add</c> of a set of collections, a <c>Hashtable</c> initializer, a derived class's
    /// constructor, and a set operation of a type that is not listed (<c>SortedSet</c>).
    /// </summary>
    public static TheoryData<string, string, int, string> OrderInsensitiveSinkRows => new()
    {
        { OrderInsensitiveSinks, "", 0, "public static void M(HashSet<int> a, HashSet<int> b) => a.UnionWith(b);" },
        { OrderInsensitiveSinks, "", 0, "public static void M(HashSet<int> a, HashSet<int> b) => a.IntersectWith(b);" },
        { OrderInsensitiveSinks, "", 0, "public static void M(HashSet<int> a, HashSet<int> b) => a.ExceptWith(b);" },
        { OrderInsensitiveSinks, "", 0, "public static void M(HashSet<int> a, HashSet<int> b) => a.SymmetricExceptWith(b);" },
        { OrderInsensitiveSinks, "", 0, "public static bool M(HashSet<int> a, HashSet<int> b) => a.SetEquals(b);" },
        { OrderInsensitiveSinks, "", 0, "public static bool M(HashSet<int> a, HashSet<int> b) => a.Overlaps(b);" },
        { OrderInsensitiveSinks, "", 0, "public static bool M(HashSet<int> a, HashSet<int> b) => a.IsSubsetOf(b);" },
        { OrderInsensitiveSinks, "", 0, "public static bool M(HashSet<int> a, HashSet<int> b) => a.IsSupersetOf(b);" },
        { OrderInsensitiveSinks, "", 0, "public static bool M(HashSet<int> a, HashSet<int> b) => a.IsProperSubsetOf(b);" },
        { OrderInsensitiveSinks, "", 0, "public static bool M(HashSet<int> a, HashSet<int> b) => a.IsProperSupersetOf(b);" },
        { OrderInsensitiveSinks, "", 0, "public static ImmutableHashSet<int> M(ImmutableHashSet<int> a, HashSet<int> b) => a.Union(b);" },
        { OrderInsensitiveSinks, "", 0, "public static ImmutableHashSet<int> M(ImmutableHashSet<int> a, HashSet<int> b) => a.Intersect(b);" },
        { OrderInsensitiveSinks, "", 0, "public static ImmutableHashSet<int> M(ImmutableHashSet<int> a, HashSet<int> b) => a.Except(b);" },
        { OrderInsensitiveSinks, "", 0, "public static ImmutableHashSet<int> M(ImmutableHashSet<int> a, HashSet<int> b) => a.SymmetricExcept(b);" },
        { OrderInsensitiveSinks, "", 0, "public static HashSet<int> M(HashSet<int> s) => new HashSet<int>(s);" },
        { OrderInsensitiveSinks, "", 0, "public static Dictionary<int, int> M(HashSet<KeyValuePair<int, int>> s) => new Dictionary<int, int>(s);" },

        { OrderInsensitiveSinks, "MMO1004", 1, "public static int M(HashSet<int> s) { var d = new Dictionary<int, IEnumerable<int>>(); d.Add(0, s); return d.Count; }" },
        { OrderInsensitiveSinks, "MMO1004", 1, "public static bool M(Dictionary<int, IEnumerable<int>> d, HashSet<int> s) => d.TryAdd(0, s);" },
        { OrderInsensitiveSinks, "MMO1004", 1, "public static int M(HashSet<int> s) { var d = new Dictionary<int, IEnumerable<int>> { { 0, s } }; return d.Count; }" },
        { OrderInsensitiveSinks, "MMO1004", 1, "public static bool M(HashSet<IEnumerable<int>> h, HashSet<int> s) => h.Add(s);" },
        { OrderInsensitiveSinks, "MMO1004", 1, "public static int M(HashSet<int> s) { var t = new System.Collections.Hashtable { { 0, s } }; return t.Count; }" },
        { OrderInsensitiveSinks, "MMO1004", 1, "public sealed class Bag : HashSet<int> { public Bag(IEnumerable<int> e) : base(e) { } } public static Bag M(HashSet<int> s) => new Bag(s);" },
        { OrderInsensitiveSinks, "MMO1004", 1, "public static void M(SortedSet<int> a, HashSet<int> b) => a.UnionWith(b);" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#ambient-time-randomness-and-hash-seeds-mmo1005: one use of each whole
    /// type and of each member of the list reports MMO1005. <c>object.GetHashCode</c> and
    /// <c>ValueType.GetHashCode</c> are called on receivers whose types do not override them, the
    /// <c>ValueTuple</c> hashes on tuples of two to eight elements, and a record class's synthesized
    /// hash on a record class that declares none. The hashes the list leaves allowed report nothing: a
    /// record class's own override, a record struct's synthesized hash, a one-element
    /// <c>ValueTuple</c>'s and a <c>Tuple</c>'s.
    /// </summary>
    public static TheoryData<string, string, int, string> AmbientRows => new()
    {
        { Ambient, "MMO1005", 1, "public static bool M() => Stopwatch.IsHighResolution;" },
        { Ambient, "MMO1005", 1, "public static long M() => TimeProvider.System.GetTimestamp();" },
        { Ambient, "MMO1005", 1, "public static int M() => Random.Shared.Next(10);" },
        { Ambient, "MMO1005", 1, "public static int M() => System.Security.Cryptography.RandomNumberGenerator.GetInt32(10);" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => HashCode.Combine(a);" },
        { Ambient, "MMO1005", 1, "public static object M(TimerCallback c) => new System.Threading.Timer(c);" },
        { Ambient, "MMO1005", 1, "public static object M() => new PeriodicTimer(TimeSpan.FromTicks(10));" },
        { Ambient, "MMO1005", 1, "public static object M() => new System.Timers.Timer();" },

        { Ambient, "MMO1005", 1, "public static long M() => DateTime.Now.Ticks;" },
        { Ambient, "MMO1005", 1, "public static long M() => DateTime.UtcNow.Ticks;" },
        { Ambient, "MMO1005", 1, "public static long M() => DateTime.Today.Ticks;" },
        { Ambient, "MMO1005", 1, "public static long M() => DateTimeOffset.Now.Ticks;" },
        { Ambient, "MMO1005", 1, "public static long M() => DateTimeOffset.UtcNow.Ticks;" },
        { Ambient, "MMO1005", 1, "public static int M() => Environment.TickCount;" },
        { Ambient, "MMO1005", 1, "public static long M() => Environment.TickCount64;" },
        { Ambient, "MMO1005", 1, "public static Guid M() => Guid.NewGuid();" },
        { Ambient, "MMO1005", 1, "public static Guid M() => Guid.CreateVersion7();" },
        { Ambient, "MMO1005", 1, "public static int M(string s) => s.GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(object o) => o.GetHashCode();" },
        { Ambient, "MMO1005", 1, "public struct S { } public static int M(S s) => s.GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => (a, a).GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => (a, a, a).GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => (a, a, a, a).GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => (a, a, a, a, a).GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => (a, a, a, a, a, a).GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => (a, a, a, a, a, a, a).GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static int M(int a) => (a, a, a, a, a, a, a, a).GetHashCode();" },
        { Ambient, "MMO1005", 1, "public sealed record R(int A); public static int M(R r) => r.GetHashCode();" },
        { Ambient, "MMO1005", 1, "public static Task M() => Task.Delay(1);" },

        { Ambient, "", 0, "public sealed record R(int A) { public override int GetHashCode() => A; } public static int M(R r) => r.GetHashCode();" },
        { Ambient, "", 0, "public record struct S(int A); public static int M(S s) => s.GetHashCode();" },
        { Ambient, "", 0, "public static int M(int a) => new ValueTuple<int>(a).GetHashCode();" },
        { Ambient, "", 0, "public static int M(int a) => Tuple.Create(a, a).GetHashCode();" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#locks-and-blocking-mmo1006: one use of each whole type of the list,
    /// <c>Monitor</c> through <c>Parallel</c> and PLINQ's <c>ParallelEnumerable</c> and
    /// <c>ParallelQuery</c> (through a local of its derived <c>ParallelQuery&lt;T&gt;</c> and
    /// <c>OrderedParallelQuery&lt;T&gt;</c>), and one row per member, including <c>GetResult</c> on each
    /// of the four awaiters and the four configured awaiters, report MMO1006. A type is used through a
    /// member or a local's creation, never a parameter, so the use is the row's one diagnostic. The
    /// <c>Synchronized</c> flag reports once wherever it can be set: on a getter, a setter, an
    /// <c>init</c> accessor, an indexer getter, an event adder and remover, a field-like event's
    /// accessors (through <c>[method:]</c>), a local function and a lambda.
    /// </summary>
    public static TheoryData<string, string, int, string> BlockingRows => new()
    {
        { Blocking, "MMO1006", 1, "public static bool M(object o) => Monitor.IsEntered(o);" },
        { Blocking, "MMO1006", 1, "public static void M() { var l = new Lock(); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var m = new Mutex(); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var s = new Semaphore(0, 1); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var s = new SemaphoreSlim(0); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var r = new ReaderWriterLock(); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var r = new ReaderWriterLockSlim(); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var s = new SpinLock(false); }" },
        { Blocking, "MMO1006", 1, "public static void M() => SpinWait.SpinUntil(() => true);" },
        { Blocking, "MMO1006", 1, "public static void M() { var e = new ManualResetEvent(false); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var e = new ManualResetEventSlim(); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var e = new AutoResetEvent(false); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var e = new EventWaitHandle(false, EventResetMode.AutoReset); }" },
        { Blocking, "MMO1006", 1, "public static int M() => WaitHandle.WaitAny([]);" },
        { Blocking, "MMO1006", 1, "public static void M() { var c = new CountdownEvent(1); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var b = new Barrier(1); }" },
        { Blocking, "MMO1006", 1, "public static void M() { var b = new System.Collections.Concurrent.BlockingCollection<int>(); }" },
        { Blocking, "MMO1006", 1, "public static void M(Action a) => Parallel.Invoke(a);" },
        { Blocking, "MMO1006", 1, "public static float M(float[] a) => a.AsParallel().Sum();" },
        { Blocking, "MMO1006", 1, "public static void M() { ParallelQuery<int>? q = null; }" },
        { Blocking, "MMO1006", 1, "public static void M() { OrderedParallelQuery<int>? q = null; }" },

        { Blocking, "MMO1006", 1, "public static void M() => Thread.Sleep(1);" },
        { Blocking, "MMO1006", 1, "public static void M(Thread t) => t.Join();" },
        { Blocking, "MMO1006", 1, "public static void M() => Thread.SpinWait(1);" },
        { Blocking, "MMO1006", 1, "public static bool M() => Thread.Yield();" },
        { Blocking, "MMO1006", 1, "public static void M(Task t) => t.Wait();" },
        { Blocking, "MMO1006", 1, "public static void M(Task t) => Task.WaitAll(t);" },
        { Blocking, "MMO1006", 1, "public static int M(Task t) => Task.WaitAny(t);" },
        { Blocking, "MMO1006", 1, "public static void M(Task t) => t.RunSynchronously();" },
        { Blocking, "MMO1006", 1, "public static int M(Task<int> t) => t.Result;" },
        { Blocking, "MMO1006", 1, "public static int M(ValueTask<int> t) => t.Result;" },

        { Blocking, "MMO1006", 1, "public static void M(Task t) => t.GetAwaiter().GetResult();" },
        { Blocking, "MMO1006", 1, "public static int M(Task<int> t) => t.GetAwaiter().GetResult();" },
        { Blocking, "MMO1006", 1, "public static void M(ValueTask t) => t.GetAwaiter().GetResult();" },
        { Blocking, "MMO1006", 1, "public static int M(ValueTask<int> t) => t.GetAwaiter().GetResult();" },
        { Blocking, "MMO1006", 1, "public static void M(Task t) => t.ConfigureAwait(false).GetAwaiter().GetResult();" },
        { Blocking, "MMO1006", 1, "public static int M(Task<int> t) => t.ConfigureAwait(false).GetAwaiter().GetResult();" },
        { Blocking, "MMO1006", 1, "public static void M(ValueTask t) => t.ConfigureAwait(false).GetAwaiter().GetResult();" },
        { Blocking, "MMO1006", 1, "public static int M(ValueTask<int> t) => t.ConfigureAwait(false).GetAwaiter().GetResult();" },

        { Blocking, "MMO1006", 1, "public static int P { " + Synchronized + " get => 1; }" },
        { Blocking, "MMO1006", 1, "private static int f; public static int P { get => f; " + Synchronized + " set => f = value; }" },
        { Blocking, "MMO1006", 1, "public sealed class K { public int P { get; " + Synchronized + " init; } }" },
        { Blocking, "MMO1006", 1, "public sealed class K { public int this[int i] { " + Synchronized + " get => i; } }" },
        { Blocking, "MMO1006", 1, "public static event Action? E { " + Synchronized + " add { } remove { } }" },
        { Blocking, "MMO1006", 1, "public static event Action? E { add { } " + Synchronized + " remove { } }" },
        { Blocking, "MMO1006", 1, "[method: System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)] public static event Action? E; public static void M() => E?.Invoke();" },
        { Blocking, "MMO1006", 1, "public static void M() { Sync(); " + Synchronized + " static void Sync() { } }" },
        { Blocking, "MMO1006", 1, "public static Func<int> M() => " + Synchronized + " () => 1;" },
    };

    /// <summary>
    /// contracts/analyzer-rules.md#numeric-types-mmo1007: a local of each of <c>double</c>,
    /// <c>System.Half</c>, <c>System.Runtime.InteropServices.NFloat</c>, <c>nint</c> and <c>nuint</c>
    /// with a constant initializer (<c>default</c> for the two types that have no C# constants) reports
    /// MMO1007 once.
    /// </summary>
    public static TheoryData<string, string, int, string> NumericTypeRows => new()
    {
        { NumericTypes, "MMO1007", 1, "public static void M() { double d = 1.0; }" },
        { NumericTypes, "MMO1007", 1, "public static void M() { System.Half h = default; }" },
        { NumericTypes, "MMO1007", 1, "public static void M() { System.Runtime.InteropServices.NFloat n = default; }" },
        { NumericTypes, "MMO1007", 1, "public static void M() { nint n = 1; }" },
        { NumericTypes, "MMO1007", 1, "public static void M() { nuint n = 1; }" },
    };

    /// <summary>
    /// The MMO1008 rule (contracts/analyzer-rules.md#rules): one row per entry. An <c>#if</c> block, an
    /// <c>#if</c> with an <c>#elif</c> (one diagnostic per directive), a <c>[Conditional]</c>
    /// declaration, <c>Debug</c> and <c>Trace</c>, <c>OperatingSystem</c>, <c>RuntimeInformation</c>,
    /// <c>RuntimeFeature</c>, <c>Environment</c>, <c>IntPtr.Size</c>, <c>UIntPtr.Size</c>,
    /// <c>BitConverter.IsLittleEndian</c>, a <c>System.Type</c> member, a <c>System.Reflection</c> type,
    /// <c>Activator</c>, the four <c>dynamic</c> operation kinds and an <c>[UnsafeAccessor]</c>
    /// declaration; the two attributes on local functions; and native code: a <c>[DllImport]</c> method
    /// and local function, an <c>InternalCall</c> extern, an extern accessor, a <c>[LibraryImport]</c>
    /// partial method (whose implementation part is not extern), <c>NativeLibrary</c>, <c>Marshal</c>
    /// and <c>Unsafe</c>.
    /// </summary>
    public static TheoryData<string, string, int, string> PlatformPathRows => new()
    {
        { PlatformPaths, "MMO1008", 1, "public static int M()\n    {\n#if MMO_SAMPLE\n        return 1;\n#endif\n        return 0;\n    }" },
        { PlatformPaths, "MMO1008", 2, "public static int M()\n    {\n#if MMO_A\n        return 1;\n#elif MMO_B\n        return 2;\n#endif\n        return 0;\n    }" },
        { PlatformPaths, "MMO1008", 1, "[Conditional(\"MMO_SAMPLE\")] public static void M() { }" },
        { PlatformPaths, "MMO1008", 1, "public static void M(bool b) => Debug.Assert(b);" },
        { PlatformPaths, "MMO1008", 1, "public static void M(string s) => Trace.WriteLine(s);" },
        { PlatformPaths, "MMO1008", 1, "public static bool M() => OperatingSystem.IsBrowser();" },
        { PlatformPaths, "MMO1008", 1, "public static System.Runtime.InteropServices.Architecture M() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;" },
        { PlatformPaths, "MMO1008", 1, "public static bool M() => System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;" },
        { PlatformPaths, "MMO1008", 1, "public static int M() => Environment.ProcessorCount;" },
        { PlatformPaths, "MMO1008", 1, "public static int M() => IntPtr.Size;" },
        { PlatformPaths, "MMO1008", 1, "public static int M() => UIntPtr.Size;" },
        { PlatformPaths, "MMO1008", 1, "public static bool M() => BitConverter.IsLittleEndian;" },
        { PlatformPaths, "MMO1008", 1, "public static bool M() => typeof(int).IsValueType;" },
        { PlatformPaths, "MMO1008", 1, "public static object M() => System.Reflection.Assembly.GetExecutingAssembly();" },
        { PlatformPaths, "MMO1008", 1, "public static T M<T>() where T : new() => Activator.CreateInstance<T>();" },
        { PlatformPaths, "MMO1008", 1, "public static object M(dynamic d) => d.Foo();" },
        { PlatformPaths, "MMO1008", 1, "public static object M(dynamic d) => d.Foo;" },
        { PlatformPaths, "MMO1008", 1, "public static object M(dynamic d) => new List<int>(d);" },
        { PlatformPaths, "MMO1008", 1, "public static object M(dynamic d) => d[0];" },
        { PlatformPaths, "MMO1008", 1, "[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"M\")] public static extern void Call(object o);" },
        { PlatformPaths, "MMO1008", 1, "public static void M() { Log(); [Conditional(\"MMO_SAMPLE\")] static void Log() { } }" },
        { PlatformPaths, "MMO1008", 1, "public static void M(object o) { Call(o); [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"M\")] static extern void Call(object o); }" },
        { PlatformPaths, "MMO1008", 1, "[System.Runtime.InteropServices.DllImport(\"m\")] public static extern float Sinf(float x);" },
        { PlatformPaths, "MMO1008", 1, "public static float M(float x) { return Sinf(x); [System.Runtime.InteropServices.DllImport(\"m\")] static extern float Sinf(float x); }" },
        { PlatformPaths, "MMO1008", 1, "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.InternalCall)] public static extern float S(float x);" },
        { PlatformPaths, "MMO1008", 1, "public static extern int P { get; }" },
        { PlatformPaths, "MMO1008", 1, "public static partial class N { [System.Runtime.InteropServices.LibraryImport(\"m\")] public static partial float Sinf(float x); public static partial float Sinf(float x) => 0f; }" },
        { PlatformPaths, "MMO1008", 1, "public static bool M(string s) => System.Runtime.InteropServices.NativeLibrary.TryLoad(s, out _);" },
        { PlatformPaths, "MMO1008", 1, "public static int M() => System.Runtime.InteropServices.Marshal.SizeOf<int>();" },
        { PlatformPaths, "MMO1008", 1, "public static int M() => System.Runtime.CompilerServices.Unsafe.SizeOf<object>();" },
    };

    /// <summary>
    /// The MMO1009 rule (contracts/analyzer-rules.md#rules): a non-constant cast from a <c>float</c>
    /// parameter to each integer type and to an enum (here one with a <c>byte</c> underlying type), a
    /// <c>checked</c> cast, a <c>System.Convert</c> member, <c>int.CreateTruncating</c>,
    /// <c>int.CreateSaturating</c> and <c>int.CreateChecked</c> of a <c>float</c>, and a non-constant
    /// <c>ulong</c>-to-<c>float</c> conversion report MMO1009, and so do <c>int.CreateSaturating</c> and
    /// <c>long.CreateTruncating</c> of a type parameter that may be a float (one constrained to
    /// <c>IBinaryInteger&lt;T&gt;</c> is allowed). So does each element conversion that has no conversion
    /// operation of its own: a <c>foreach</c> iteration variable (also before a
    /// user-defined operator), a tuple conversion (explicit and implicit), a deconstruction (of a tuple,
    /// through <c>Deconstruct</c>, and in a <c>foreach</c>), a spread, and the left operand of <c>??</c>.
    /// </summary>
    public static TheoryData<string, string, int, string> ConversionRows => new()
    {
        { Conversions, "MMO1009", 1, "public static sbyte M(float x) => (sbyte)x;" },
        { Conversions, "MMO1009", 1, "public static byte M(float x) => (byte)x;" },
        { Conversions, "MMO1009", 1, "public static short M(float x) => (short)x;" },
        { Conversions, "MMO1009", 1, "public static ushort M(float x) => (ushort)x;" },
        { Conversions, "MMO1009", 1, "public static int M(float x) => (int)x;" },
        { Conversions, "MMO1009", 1, "public static uint M(float x) => (uint)x;" },
        { Conversions, "MMO1009", 1, "public static long M(float x) => (long)x;" },
        { Conversions, "MMO1009", 1, "public static ulong M(float x) => (ulong)x;" },
        { Conversions, "MMO1009", 1, "public static char M(float x) => (char)x;" },
        { Conversions, "MMO1009", 1, "public static Int128 M(float x) => (Int128)x;" },
        { Conversions, "MMO1009", 1, "public static UInt128 M(float x) => (UInt128)x;" },
        { Conversions, "MMO1009", 1, "public enum E : byte { A } public static E M(float x) => (E)x;" },
        { Conversions, "MMO1009", 1, "public static int M(float x) => checked((int)x);" },
        { Conversions, "MMO1009", 1, "public static long M(float x) => Convert.ToInt64(x);" },
        { Conversions, "MMO1009", 1, "public static int M(float x) => int.CreateTruncating(x);" },
        { Conversions, "MMO1009", 1, "public static int M(float x) => int.CreateSaturating(x);" },
        { Conversions, "MMO1009", 1, "public static int M(float x) => int.CreateChecked(x);" },
        { Conversions, "MMO1009", 1, "public static float M(ulong u) => (float)u;" },
        { Conversions, "MMO1009", 1, "public static int M<T>(T v) where T : INumberBase<T> => int.CreateSaturating(v);" },
        { Conversions, "MMO1009", 1, "public static long M<T>(T v) where T : INumberBase<T> => long.CreateTruncating(v);" },
        { Conversions, "", 0, "public static int M<T>(T v) where T : IBinaryInteger<T> => int.CreateSaturating(v);" },

        { Conversions, "MMO1009", 1, "public static int M(float[] a) { var n = 0; foreach (int i in a) n += i; return n; }" },
        { Conversions, "MMO1009", 1, "public static float M(ulong[] a) { var s = 0f; foreach (float f in a) s += f; return s; }" },
        { Conversions, "MMO1009", 1, "public readonly struct S { public static explicit operator S(int v) => default; } public static int M(float[] a) { var n = 0; foreach (S s in a) n++; return n; }" },
        { Conversions, "MMO1009", 1, "public static (int, int) M((float, float) t) => ((int, int))t;" },
        { Conversions, "MMO1009", 1, "public static (float, float) M((ulong, ulong) t) => t;" },
        { Conversions, "MMO1009", 1, "public static float M((ulong, ulong) t) { (float p, float q) = t; return p + q; }" },
        { Conversions, "MMO1009", 1, "public readonly struct V { public void Deconstruct(out ulong x, out ulong y) { x = 1; y = 2; } } public static float M(V v) { (float a, float b) = v; return a + b; }" },
        { Conversions, "MMO1009", 1, "public static float M((ulong, ulong)[] a) { var s = 0f; foreach ((float p, float q) in a) s += p + q; return s; }" },
        { Conversions, "MMO1009", 1, "public static float[] M(ulong[] a) => [.. a];" },
        { Conversions, "MMO1009", 1, "public static float M(ulong? u, float f) => u ?? f;" },
    };

    /// <summary>
    /// Every entry of the contract's tables, run through <see cref="AnalyzerHarness.AnalyzeMemberAsync"/>
    /// with the four assertions of <c>RuleTests.EachSampleProducesExactlyItsRule</c>: the sample compiles;
    /// its diagnostics' IDs are exactly <paramref name="count"/> times <paramref name="expectedRuleOrEmpty"/>
    /// (an allowed entry has <c>""</c>, 0 and no diagnostics); every diagnostic is an Error; and every one
    /// lies inside the sample member. <paramref name="table"/> names the contract table the row comes from.
    /// </summary>
    [Theory]
    [MemberData(nameof(Vector128AllowlistRows))]
    [MemberData(nameof(Vector128ExclusionRows))]
    [MemberData(nameof(MathAllowlistRows))]
    [MemberData(nameof(MathExclusionRows))]
    [MemberData(nameof(HashOrderedRows))]
    [MemberData(nameof(OrderInsensitiveSinkRows))]
    [MemberData(nameof(AmbientRows))]
    [MemberData(nameof(BlockingRows))]
    [MemberData(nameof(NumericTypeRows))]
    [MemberData(nameof(PlatformPathRows))]
    [MemberData(nameof(ConversionRows))]
    public async Task EveryContractTableEntryIsEnforced(string table, string expectedRuleOrEmpty, int count, string member)
    {
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"{table}: the sample must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            Enumerable.Repeat(expectedRuleOrEmpty, count).SequenceEqual(result.Diagnostics.Select(d => d.Id)),
            $"{table}: expected {count} x '{expectedRuleOrEmpty}', got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(
            result.Diagnostics,
            d => Assert.True(
                d.Location.SourceTree == result.Tree && result.MemberSpan.Contains(d.Location.SourceSpan),
                $"{table}: {AnalyzerHarness.Describe(d)} lies outside the sample member {result.MemberSpan}."));
    }

    /// <summary>A member that enumerates a parameter of <paramref name="type"/> with one <c>foreach</c> and nothing else.</summary>
    private static string Foreach(string type) =>
        $"public static int M({type} d) {{ var n = 0; foreach (var e in d) {{ n++; }} return n; }}";
}
