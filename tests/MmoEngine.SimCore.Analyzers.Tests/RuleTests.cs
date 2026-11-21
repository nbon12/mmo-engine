using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace MmoEngine.SimCore.Analyzers.Tests;

/// <summary>
/// Every determinism rule on its violating and allowed samples (US3.1 to US3.7, SC-003), per
/// specs/015-shared-simulation-core/contracts/analyzer-rules.md#tests. Each sample is one member of
/// <c>public static class C</c>, compiled and analyzed by <see cref="AnalyzerHarness"/>.
/// </summary>
public sealed class RuleTests
{
    /// <summary>MMO1004's message after its type, naming the allowed alternative.</summary>
    private const string HashOrderAlternative =
        ", whose order is not defined; iterate a sorted array or List<T> kept alongside it, or a SortedDictionary or SortedSet";

    /// <summary>MMO1009's reason for a float-to-integer or float-to-enum conversion (contracts/analyzer-rules.md#messages).</summary>
    private const string FloatToIntegerReason =
        "converts a floating-point value to an integer, which gives different results between builds for NaN and out-of-range values; use 'SimMath.FloorToInt', 'SimMath.RoundToInt' or 'SimMath.TruncateToInt', which saturate first";

    /// <summary>MMO1009's reason for a <c>ulong</c>-to-<c>float</c> conversion (contracts/analyzer-rules.md#messages).</summary>
    private const string UlongToFloatReason =
        "converts a 'ulong' to 'float', which the WebAssembly build rounds through 'double' and so can differ by one ulp; narrow it to 'uint' or 'long' first";

    /// <summary>MMO1006's message after <c>'{0}' </c> (contracts/analyzer-rules.md#messages).</summary>
    private const string BlockingText =
        "takes a lock or blocks, which gameplay code must not do (constitution Principle II); the core is a pure step function: take state in and return state out";

    /// <summary>MMO1007's message after <c>'{0}' uses '{1}'; </c> (contracts/analyzer-rules.md#messages).</summary>
    private const string NumericTypeText =
        "the core computes in 'float' only, and in 'int' or 'long' for fixed point and counters, so every build rounds and overflows the same way";

    /// <summary>MMO1008's message after <c>'{0}' </c> (contracts/analyzer-rules.md#messages).</summary>
    private const string PlatformPathText =
        "lets the server and WebAssembly builds take different paths; the core has one code path for every build";

    /// <summary>An <c>#if</c> block with an <c>#else</c> (US3.7, T146): one MMO1008, on the <c>#if</c> line.</summary>
    private const string IfElseMember = "public static int M()\n    {\n#if DEBUG\n        return 1;\n#else\n        return 0;\n#endif\n    }";

    /// <summary>An <c>#if</c> with an <c>#elif</c>, as <c>TableTests</c> writes it: one MMO1008 per directive.</summary>
    private const string IfElifMember = "public static int M()\n    {\n#if MMO_A\n        return 1;\n#elif MMO_B\n        return 2;\n#endif\n        return 0;\n    }";

    /// <summary>The <c>MethodImpl</c> attribute that sets the <c>Synchronized</c> flag, as the samples write it.</summary>
    private const string Synchronized =
        "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]";

    /// <summary>The path <see cref="SimMathMayClampAndCastFloats"/> compiles its stand-in SimMath under.</summary>
    private const string SimMathPath = "Math/SimMath.cs";

    /// <summary>The path <see cref="SimMathMayClampAndCastFloats"/> compiles its other type under.</summary>
    private const string OtherTypePath = "Sample/D.cs";

    /// <summary>
    /// A stand-in <c>MmoEngine.SimCore.Math.SimMath</c>: <c>Clamp</c> through <c>System.Math.Clamp</c>,
    /// the three saturating helpers casting behind a NaN and range check, another method with the same
    /// raw cast, a local function and an overload that share a helper's name and cast without the
    /// checks, and a <c>ulong</c>-to-<c>float</c> cast, which no SimMath method may make.
    /// </summary>
    private const string SimMathSource = """
        namespace MmoEngine.SimCore.Math
        {
            public static class SimMath
            {
                public static float Clamp(float x, float lo, float hi) => System.Math.Clamp(x, lo, hi);

                public static int FloorToInt(float x) =>
                    float.IsNaN(x) ? 0 : x >= 2147483648f ? int.MaxValue : x < -2147483648f ? int.MinValue : (int)System.MathF.Floor(x);

                public static int RoundToInt(float x) =>
                    float.IsNaN(x) ? 0 : x >= 2147483648f ? int.MaxValue : x < -2147483648f ? int.MinValue : (int)System.MathF.Round(x);

                public static int TruncateToInt(float x) =>
                    float.IsNaN(x) ? 0 : x >= 2147483648f ? int.MaxValue : x < -2147483648f ? int.MinValue : (int)x;

                public static int Other(float x) => (int)x;

                public static int Local(float x)
                {
                    return RoundToInt(x);

                    static int RoundToInt(float v) => (int)v;
                }

                public static int RoundToInt(float x, float s) => (int)(x * s);

                public static float FromBits(ulong u) => (float)u;
            }
        }
        """;

    /// <summary>The same clamp and cast as SimMath's helpers, in a type that is not SimMath.</summary>
    private const string OtherTypeSource = """
        namespace Sample
        {
            public static class D
            {
                public static int M(float x) => (int)System.Math.Clamp(x, 0f, 1f);
            }
        }
        """;

    /// <summary>The header Roslyn reads as "generated code" (contracts/analyzer-rules.md#generated-code).</summary>
    private const string GeneratedHeader = "// <auto-generated/>";

    /// <summary>The path <see cref="AttributeApplicationsAreNotReported"/> compiles the SDK's assembly attributes under.</summary>
    private const string AssemblyInfoPath = "obj/Sample.AssemblyInfo.cs";

    /// <summary>The path <see cref="AttributeApplicationsAreNotReported"/> compiles its generated probe under.</summary>
    private const string ProbePath = "obj/Sample.Probe.cs";

    /// <summary>
    /// A stand-in for the SDK-generated <c>obj/.../MmoEngine.SimCore.AssemblyInfo.cs</c>: the
    /// <c>System.Reflection</c> assembly attributes the SDK writes, with <c>IsTrimmable</c> and
    /// <c>IsAotCompatible</c> set as in the core, under a generated-code header
    /// (contracts/analyzer-rules.md#attribute-applications).
    /// </summary>
    private const string AssemblyInfoSource = """
        // <auto-generated/>
        [assembly: System.Reflection.AssemblyCompany("x")]
        [assembly: System.Reflection.AssemblyConfiguration("Release")]
        [assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
        [assembly: System.Reflection.AssemblyInformationalVersion("1.0.0")]
        [assembly: System.Reflection.AssemblyProduct("x")]
        [assembly: System.Reflection.AssemblyTitle("x")]
        [assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
        [assembly: System.Reflection.AssemblyMetadata("IsTrimmable", "True")]
        [assembly: System.Reflection.AssemblyMetadata("IsAotCompatible", "True")]

        """;

    /// <summary>The sample member of <see cref="AttributeApplicationsAreNotReported"/>: an attribute on a member.</summary>
    private const string ObsoleteMember = "[Obsolete(\"x\")] public static int M() => 1;";

    /// <summary>The class <see cref="GeneratedCodeIsAnalyzed"/> compiles in a generated file: one banned platform-math call.</summary>
    private const string GeneratedSource = "public static class G { public static float M(float x) => System.MathF.Sin(x); }\n";

    /// <summary>
    /// MMO1001 (US3.1): System.Numerics vector, quaternion, matrix and plane types, <c>Vector&lt;T&gt;</c>,
    /// the horizontal and hardware-query <c>Vector128</c> members, ISA classes and non-float
    /// <c>Vector128&lt;T&gt;</c>, wherever they appear; lane-wise <c>Vector128&lt;float&gt;</c> and scalar
    /// float arithmetic are allowed.
    /// </summary>
    public static TheoryData<string, int, string> VectorRows => new()
    {
        { "MMO1001", 1, "public static float M(float x) => Vector3.Dot(new Vector3(x), Vector3.UnitX);" },
        { "MMO1001", 1, "public static int M() => Vector<float>.Count;" },
        { "MMO1001", 1, "public static float M(Vector128<float> a) => Vector128.Sum(a);" },
        { "MMO1001", 1, "public static float M(Vector128<float> a) => Vector128.Dot(a, a);" },
        { "MMO1001", 1, "public static bool M() => Vector128.IsHardwareAccelerated;" },
        { "MMO1001", 1, "public static object M() => new List<Quaternion>();" },
        { "MMO1001", 1, "public static void M() { var v = Matrix4x4.Identity; }" },
        { "MMO1001", 1, "public static bool M() => System.Runtime.Intrinsics.X86.Sse.IsSupported;" },

        // Branch rows: a property, an event, a pointer target, a field initializer counted as its
        // declaration, and a non-float Vector128 as both return type and parameter.
        { "MMO1001", 1, "public static Vector3 P { get; }" },
        { "MMO1001", 1, "public static event Action<Vector4>? E;" },
        { "MMO1001", 1, "public static unsafe void M(Vector2* p) { }" },
        { "MMO1001", 1, "public static readonly Plane F = default;" },
        { "MMO1001", 2, "public static Vector128<int> M(Vector128<int> a) => a;" },

        // Coverage rows (T167): a property initializer and a parameter's default value counted as their
        // declarations; an event reference (the declaration, the handler parameter and the `+=`); a
        // function pointer's parameter; an indexer's parameter, at its declaration and its use; a field
        // of a tuple that holds a vector; a generic method's type argument; and a positional record
        // parameter, which reports once although the compiler also declares a property from it.
        { "MMO1001", 1, "public static Vector3 P { get; } = default;" },
        { "MMO1001", 1, "public static void M(Vector3 v = default) { }" },
        { "MMO1001", 3, "public static event Action<Vector2>? E; public static void M(Action<Vector2> h) => E += h;" },
        { "MMO1001", 1, "public static unsafe void M(delegate*<Vector2, float> p) { }" },
        { "MMO1001", 2, "public sealed class K { public float this[Vector2 v] => 0f; } public static float M(K k) => k[default];" },
        { "MMO1001", 2, "public static int M((Vector2, int) t) => t.Item2;" },
        { "MMO1001", 1, "public static int M() => Array.Empty<Vector3>().Length;" },
        { "MMO1001", 1, "public record struct S(Vector2 V);" },

        // Review rows (Phase 5): a local function's parameter (and the call, whose signature holds the
        // vector) and a delegate's return type are declarations like a method's.
        { "MMO1001", 2, "public static void M() { L(default); static void L(Vector3 v) { } }" },
        { "MMO1001", 1, "public delegate Vector3 D();" },

        // Review row (Phase 5, fourth pass): a pattern local is a declaration, reported at its
        // identifier, besides the member read in a later operation.
        { "MMO1001", 2, "public static float M(object o) => o is Vector2 v ? v.X : 0f;" },

        { "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Add(a, b) * b - Vector128.Create(1f);" },
        { "", 0, "public static float M(float x, float y) => x * y + 1f;" },
    };

    /// <summary>
    /// MMO1002 (US3.2): platform math outside the allowlist, as an invocation, a generic-math interface
    /// call, a method group or a function pointer, and float <c>%</c> and <c>%=</c>, also through a
    /// type parameter that may be <c>float</c>; the allowlist, the constants, <c>nameof</c>, integer
    /// <c>Math</c>, the bit casts and an <c>IBinaryInteger&lt;T&gt;</c> <c>%</c> are allowed.
    /// </summary>
    public static TheoryData<string, int, string> PlatformMathRows => new()
    {
        { "MMO1002", 1, "public static float M(float x) => MathF.Sin(x);" },
        { "MMO1002", 1, "public static float M(float x) => float.Atan2(x, 1f);" },
        { "MMO1002", 1, "public static T M<T>(T x) where T : ITrigonometricFunctions<T> => T.Cos(x);" },
        { "MMO1002", 1, "public static Func<float, float> M() => MathF.Tan;" },
        { "MMO1002", 1, "public static unsafe delegate*<float, float> M() => &MathF.Sin;" },
        { "MMO1002", 1, "public static float M(float x) => MathF.Pow(x, 2f);" },
        { "MMO1002", 1, "public static float M(float x) => MathF.ReciprocalSqrtEstimate(x);" },
        { "MMO1002", 1, "public static float M(float x) => MathF.Max(x, 0f);" },
        { "MMO1002", 1, "public static float M(float x) => MathF.Round(x, 2);" },
        { "MMO1002", 1, "public static float M(float x) => x % 2f;" },
        { "MMO1002", 1, "public static void M(ref float x) { x %= 2f; }" },
        { "MMO1002", 1, "public static float M(float x) => Math.Clamp(x, 0f, 1f);" },

        // Review rows (Phase 5, second pass): `%` and `%=` through a type parameter that may be bound to
        // float, by the generic-math operator interface; a type parameter constrained to
        // IBinaryInteger<T> is never a float, so its `%` is allowed (below).
        { "MMO1002", 1, "public static T M<T>(T a, T b) where T : IModulusOperators<T, T, T> => a % b;" },
        { "MMO1002", 1, "public static void M<T>(ref T a, T b) where T : IModulusOperators<T, T, T> => a %= b;" },

        // Coverage row (T167): a static initializer runs too, and a float field's declaration does not
        // hide the call it holds.
        { "MMO1002", 1, "public static readonly float F = MathF.Sin(1f);" },

        { "", 0, "public static float M(float x) => MathF.Sqrt(MathF.Abs(x)) + MathF.Floor(x) + MathF.Round(x) + MathF.Round(x, MidpointRounding.ToZero) + MathF.CopySign(x, -1f) + MathF.PI;" },
        { "", 0, "public static string M() => nameof(MathF.Sin);" },
        { "", 0, "public static int M(int i) => Math.Max(i, 0) + Math.Clamp(i, 0, 9);" },
        { "", 0, "public static uint M(float x) => float.IsFinite(x) ? BitConverter.SingleToUInt32Bits(x) : 0u;" },
        { "", 0, "public static T M<T>(T a, T b) where T : IBinaryInteger<T> => a % b;" },
        { "", 0, "public static T M<T, U>(T a, T b) where U : IBinaryInteger<U> where T : U, IModulusOperators<T, T, T> => a % b;" },
    };

    /// <summary>
    /// MMO1003 (US3.3): fused multiply-add on any type, including <c>Vector128</c> and the <c>Fma</c>
    /// intrinsics, which report MMO1003 and not MMO1001; <c>a * b + c</c> is allowed.
    /// </summary>
    public static TheoryData<string, int, string> FusedRows => new()
    {
        { "MMO1003", 1, "public static float M(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);" },
        { "MMO1003", 1, "public static float M(float a) => float.MultiplyAddEstimate(a, a, a);" },
        { "MMO1003", 1, "public static Vector128<float> M(Vector128<float> a) => Vector128.FusedMultiplyAdd(a, a, a);" },
        { "MMO1003", 1, "public static Vector128<float> M(Vector128<float> a) => System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(a, a, a);" },

        { "", 0, "public static float M(float a, float b, float c) => a * b + c;" },
    };

    /// <summary>
    /// MMO1004 (US3.4): enumerating a hash-ordered collection by <c>foreach</c>, LINQ, <c>Keys</c>,
    /// <c>CopyTo</c>, a spread, <c>string.Join</c>, a copy into a list, a return as
    /// <c>IEnumerable&lt;T&gt;</c>, query syntax, a declaration pattern or a type argument whose
    /// constraint is not hash-ordered, also through a type parameter constrained to a hash-ordered type;
    /// lookups, <c>Count</c>, sorted collections, order-insensitive sinks and unconstrained type
    /// arguments (<c>List&lt;HashSet&lt;int&gt;&gt;</c>) are allowed.
    /// </summary>
    public static TheoryData<string, int, string> HashOrderRows => new()
    {
        { "MMO1004", 1, "public static int M(Dictionary<int, int> d) { var s = 0; foreach (var kv in d) s += kv.Value; return s; }" },
        { "MMO1004", 1, "public static int M(HashSet<int> s) => s.First();" },
        { "MMO1004", 1, "public static int[] M(Dictionary<int, int> d) => d.Keys.ToArray();" },
        { "MMO1004", 1, "public static List<int> M(HashSet<int> s) => new List<int>(s);" },
        { "MMO1004", 1, "public static int[] M(HashSet<int> s) => [.. s];" },
        { "MMO1004", 1, "public static string M(HashSet<int> s) => string.Join(\",\", s);" },
        { "MMO1004", 1, "public static IEnumerable<int> M(ImmutableHashSet<int> s) => s;" },
        { "MMO1004", 1, "public static int M(IReadOnlyDictionary<int, int> d) { var n = 0; foreach (var k in d.Keys) n += k; return n; }" },
        { "MMO1004", 1, "public static void M(Dictionary<int, int> d, int[] dst) => d.Keys.CopyTo(dst, 0);" },
        { "MMO1004", 1, "public static IEnumerable<int> M(Dictionary<int, int> d) => from kv in d select kv.Value;" },

        // Coverage row (T167): an indexer argument is not an order-insensitive sink.
        { "MMO1004", 1, "public sealed class K { public int this[IEnumerable<int> e] => e.Count(); } public static int M(K k, HashSet<int> s) => k[s];" },

        // Review rows (Phase 5): only a listed type's set operations and copy constructors are
        // order-insensitive sinks. A set stored as a dictionary value, through a collection initializer,
        // Add or a Hashtable, or passed to a derived class's constructor, reports.
        { "MMO1004", 1, "public static int M(HashSet<int> s) { var d = new Dictionary<int, IEnumerable<int>> { { 0, s } }; return d[0].First(); }" },
        { "MMO1004", 1, "public static int M(HashSet<int> s) { var d = new Dictionary<int, IEnumerable<int>>(); d.Add(0, s); return d[0].First(); }" },
        { "MMO1004", 1, "public static object? M(HashSet<int> s) { var t = new System.Collections.Hashtable { { 0, s } }; return t[0]; }" },
        { "MMO1004", 1, "public sealed class Bag : HashSet<int> { public Bag(IEnumerable<int> e) : base(e) { } } public static Bag M(HashSet<int> s) => new Bag(s);" },

        // Review rows (Phase 5, second pass): a hash-ordered value that reaches a type that is not
        // hash-ordered with no conversion operation. A type argument bound to a type parameter whose
        // constraint is not hash-ordered (a generic method's, inferred, and a generic type's); a
        // declaration pattern, in `is` and in a switch case; and a type parameter that is hash-ordered
        // through its constraint, enumerated by foreach and by LINQ.
        { "MMO1004", 1, "public static int Sum<T>(T e) where T : IEnumerable<int> { var s = 0; foreach (var x in e) s += x; return s; } public static int M(HashSet<int> h) => Sum(h);" },
        { "MMO1004", 1, "public sealed class Box<T> where T : IEnumerable<int> { public Box(T items) { } } public static object M(HashSet<int> h) => new Box<HashSet<int>>(h);" },
        { "MMO1004", 1, "public static int M(HashSet<int> h) { var s = 0; if (h is IEnumerable<int> e) foreach (var x in e) s += x; return s; }" },
        { "MMO1004", 1, "public static int M(HashSet<int> h) { switch (h) { case IEnumerable<int> e: return e.First(); default: return 0; } }" },
        { "MMO1004", 1, "public static int M<T>(T d) where T : Dictionary<int, int> { var s = 0; foreach (var kv in d) s += kv.Value; return s; }" },
        { "MMO1004", 1, "public static int M<T>(T s) where T : HashSet<int> => s.First();" },
        { "MMO1004", 1, "public static int M<T, U>(U d) where T : Dictionary<int, int> where U : T { var s = 0; foreach (var kv in d) s += kv.Value; return s; }" },

        { "", 0, "public static int M(Dictionary<int, int> d, int k) => d.TryGetValue(k, out var v) ? v + d.Count : d.Keys.Count;" },
        { "", 0, "public static int M(SortedDictionary<int, int> d) { var s = 0; foreach (var kv in d) s += kv.Value; return s; }" },
        { "", 0, "public static void M(HashSet<int> a, HashSet<int> b) => a.UnionWith(b);" },
        { "", 0, "public static bool M(HashSet<int> a, HashSet<int> b) => a.SetEquals(b);" },
        { "", 0, "public static HashSet<int> M(HashSet<int> s) => new HashSet<int>(s);" },
        { "", 0, "public static int M(SortedDictionary<int, int> d) => d.Keys.First();" },
        { "", 0, "public static int M<T>(T d) where T : SortedDictionary<int, int> { var s = 0; foreach (var kv in d) s += kv.Value; return s; }" },
        { "", 0, "public static int M(HashSet<int> h) => h is HashSet<int> g ? g.Count : 0;" },
        { "", 0, "public static HashSet<int> M(List<HashSet<int>> l) => l.First();" },
        { "", 0, "public static object M() => new List<HashSet<int>>();" },
    };

    /// <summary>
    /// MMO1005 (US3.5): wall-clock and monotonic clocks, <c>Environment.TickCount</c> (MMO1005, not
    /// MMO1008), all of <c>System.Random</c> (seeded too), <c>Guid.NewGuid</c>, per-process hash seeds
    /// (a <c>ValueTuple</c>'s hash, which combines through <c>HashCode</c>, and a record class's
    /// synthesized hash among them) and <c>TimeProvider</c>; an <c>int</c>'s value-bits
    /// <c>GetHashCode</c>, a <c>Tuple</c>'s hash of <c>int</c>s and the caller's step arithmetic are
    /// allowed.
    /// </summary>
    public static TheoryData<string, int, string> AmbientRows => new()
    {
        { "MMO1005", 1, "public static long M() => DateTime.Now.Ticks;" },
        { "MMO1005", 1, "public static long M() => DateTime.UtcNow.Ticks;" },
        { "MMO1005", 1, "public static long M() => Stopwatch.GetTimestamp();" },
        { "MMO1005", 1, "public static int M() => Environment.TickCount;" },
        { "MMO1005", 1, "public static int M() => new Random(42).Next();" },
        { "MMO1005", 1, "public static int M() => Random.Shared.Next();" },
        { "MMO1005", 1, "public static Guid M() => Guid.NewGuid();" },
        { "MMO1005", 1, "public static int M(string s) => s.GetHashCode();" },
        { "MMO1005", 1, "public static int M(int a, int b) => HashCode.Combine(a, b);" },
        { "MMO1005", 1, "public static DateTimeOffset M() => TimeProvider.System.GetUtcNow();" },
        { "MMO1005", 1, "public static int M(int a, int b) => (a, b).GetHashCode();" },
        { "MMO1005", 1, "public sealed record R(int A); public static int M(R r) => r.GetHashCode();" },

        { "", 0, "public static int M(int a) => a.GetHashCode() ^ 17;" },
        { "", 0, "public static int M(int a, int b) => Tuple.Create(a, b).GetHashCode();" },
        { "", 0, "public static long M(long step, long stepTicks) => step * stepTicks;" },
    };

    /// <summary>
    /// MMO1006 (US3.6): every <c>lock</c> statement, a <c>System.Threading.Lock</c> declaration, task
    /// waits and results, an awaiter's <c>GetResult</c>, <c>Thread.Sleep</c>, <c>Monitor</c>, a
    /// synchronized method, <c>Parallel</c> and PLINQ; <c>await</c>, <c>Interlocked</c> and sequential
    /// LINQ are allowed.
    /// </summary>
    public static TheoryData<string, int, string> BlockingRows => new()
    {
        { "MMO1006", 1, "private static readonly object Gate = new(); public static void M() { lock (Gate) { } }" },
        { "MMO1006", 1, "private static readonly Lock L = new();" },
        { "MMO1006", 2, "public static void M(Lock l) { lock (l) { } }" },
        { "MMO1006", 1, "public static void M(Task t) => t.Wait();" },
        { "MMO1006", 1, "public static int M(Task<int> t) => t.Result;" },
        { "MMO1006", 1, "public static int M(Task<int> t) => t.GetAwaiter().GetResult();" },
        { "MMO1006", 1, "public static void M() => Thread.Sleep(1);" },
        { "MMO1006", 1, "public static void M() => Monitor.Enter(typeof(C));" },
        { "MMO1006", 1, "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)] public static void M() { }" },
        { "MMO1006", 1, "public static void M(int[] a) => Parallel.For(0, a.Length, i => a[i]++);" },

        // Review rows (Phase 5): the Synchronized flag on a getter, a setter, an event adder, a field-like
        // event's accessors (through [method:]), a local function and a lambda; and a Lock parameter of
        // a local function and of a delegate.
        { "MMO1006", 1, "public static int P { " + Synchronized + " get => 1; }" },
        { "MMO1006", 1, "private static int f; public static int Q { get => f; " + Synchronized + " set => f = value; }" },
        { "MMO1006", 1, "public static event Action? E { " + Synchronized + " add { } remove { } }" },
        { "MMO1006", 1, "[method: System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)] public static event Action? E; public static void M() => E?.Invoke();" },
        { "MMO1006", 1, "public static void M() { Sync(); " + Synchronized + " static void Sync() { } }" },
        { "MMO1006", 1, "public static Func<int> M() => " + Synchronized + " () => 1;" },
        { "MMO1006", 1, "public static void M() { L(null!); static void L(Lock l) { } }" },
        { "MMO1006", 1, "public delegate void D(Lock l);" },

        // Review row (Phase 5, second pass): a partial method's implementation part reports nothing
        // itself, but the flag it sets reaches the defining declaration, which reports it once.
        { "MMO1006", 1, "public static partial class N { public static partial void M(); " + Synchronized + " public static partial void M() { } }" },

        // Review rows (Phase 5, third pass): PLINQ blocks the caller on worker tasks and reduces in a
        // partition order that varies between runs. The outer Sum reports and covers AsParallel; a
        // ParallelQuery<T> parameter reports as a declaration.
        { "MMO1006", 1, "public static float M(float[] a) => a.AsParallel().Sum();" },
        { "MMO1006", 1, "public static void M(ParallelQuery<int> q) { }" },

        // Review row (Phase 5, fourth pass): a pattern local of a lock type is a declaration.
        { "MMO1006", 1, "public static bool M(object o) => o is Mutex m;" },

        { "", 0, "public static async Task<int> M(Task<int> t) => await t;" },
        { "", 0, "public static int M(ref int x) => Interlocked.Increment(ref x);" },
        { "", 0, "private static int f; public static int Q { get => f; set => f = value; } public static event Action? E { add { } remove { } }" },
        { "", 0, "public static int M() { return L(); static int L() => 1; }" },
        { "", 0, "public static float M(float[] a) => a.Sum();" },
    };

    /// <summary>
    /// MMO1007 (US3.7): <c>double</c>, <c>Half</c>, <c>nint</c>, <c>nuint</c> and <c>NFloat</c> as an
    /// untyped literal, a conversion, a return type, a field, a property, an event's type argument, a
    /// pointer target, a parameter, a member's type argument and a local that a pattern, a deconstruction
    /// or an <c>out</c> argument declares; a <c>const double</c>, a constant-folded <c>double</c>
    /// expression, an <c>int</c> type argument and pattern and deconstruction locals of allowed types are
    /// allowed.
    /// </summary>
    public static TheoryData<string, int, string> NumericTypeRows => new()
    {
        { "MMO1007", 1, "public static float M(float x) => (float)(x * 0.5);" },
        { "MMO1007", 2, "public static double M(float x) => x;" },
        { "MMO1007", 1, "public static int M(int a) => (int)(nint)a;" },
        { "MMO1007", 1, "public static void M() { var h = (Half)1; }" },

        // Branch rows: a field with its initializer counted as its declaration, a property, an event's
        // type argument, a pointer target and a parameter.
        { "MMO1007", 1, "public static double F = 1.0;" },
        { "MMO1007", 1, "public static Half P { get; }" },
        { "MMO1007", 1, "public static event Func<nint>? E;" },
        { "MMO1007", 1, "public static unsafe void M(nuint* p) { }" },
        { "MMO1007", 1, "public static void M(System.Runtime.InteropServices.NFloat n) { }" },

        // Coverage rows (T167): a double property read, even when narrowed to float at once; a function
        // pointer's return type; a positional record parameter, once; and a record property declared
        // explicitly, which reports on its own identifier.
        { "MMO1007", 1, "public static float M(TimeSpan t) => (float)t.TotalSeconds;" },
        { "MMO1007", 1, "public static unsafe void M(delegate*<double> p) { }" },
        { "MMO1007", 1, "public record R(double X);" },
        { "MMO1007", 2, "public record R(double X) { public double X { get; init; } = X; }" },

        // Review rows (Phase 5): a local function's parameter, a delegate's return type and parameter,
        // and the parameter and return types a lambda writes out, besides the declarations around them.
        { "MMO1007", 1, "public static void M() { L(1f); static void L(double v) { } }" },
        { "MMO1007", 2, "public delegate double D(double x);" },
        { "MMO1007", 2, "public static Func<double, float> M() => (double d) => 1f;" },
        { "MMO1007", 2, "public static void M() { var f = double () => 0; }" },

        // Review rows (Phase 5, second pass): a partial member reports at its defining declaration only,
        // so a partial method's return type and parameter report once each, not once per part, and so do
        // a partial property and a partial event.
        { "MMO1007", 2, "public static partial class N { public static partial double M(double x); public static partial double M(double x) => x; }" },
        { "MMO1007", 1, "public static partial class N { public static partial double P { get; } public static partial double P { get => 0; } }" },
        { "MMO1007", 1, "public static partial class N { public static partial event Action<double>? E; public static partial event Action<double>? E { add { } remove { } } }" },

        // Review rows (Phase 5, third pass): a banned type reached only through a type argument, of a
        // generic method (a call and a method group) or of the member's containing type, reports at the
        // reference even though its result is an int. A constant argument converts to nint with no
        // diagnostic, and a non-constant one is part of the same call and does not report again.
        { "MMO1007", 1, "public static int Count<T>(T v) => 1; public static int M() => Count<nint>(40);" },
        { "MMO1007", 1, "public static int Count<T>(T v) => 1; public static int M(int n) => Count<nint>(n);" },
        { "MMO1007", 1, "public static int Size<T>() => 0; public static Func<int> M() => Size<double>;" },
        { "MMO1007", 1, "public static class Box<T> { public static int Count; } public static int M() => Box<double>.Count;" },

        // Review rows (Phase 5, fourth pass): a local that no declarator declares reports at its
        // identifier as a declarator's local does: a declaration pattern (in an `is`, a negated `is not`
        // and a switch arm), a recursive pattern's and a list pattern's designation, a positional
        // pattern's subpatterns, both forms of deconstruction, a deconstructing `foreach` (with the
        // array parameter) and an `out var` (with the method's out parameter). A pattern local inside a
        // declaration that already reports the rule, in the same statement, does not report again.
        { "MMO1007", 1, "public static float M(object o) => o is double d ? (float)d : 0f;" },
        { "MMO1007", 1, "public static float M(object o) { if (o is not nint n) return 0f; return n; }" },
        { "MMO1007", 1, "public static float M(object o) => o switch { double d => (float)d, _ => 0f };" },
        { "MMO1007", 1, "public static float M(object o) => o is double { } d ? (float)d : 0f;" },
        { "MMO1007", 1, "public static int M(object o) => o is nint[] and [_, ..] t ? t.Length : 0;" },
        { "MMO1007", 2, "public static float M(object o) => o is (double a, double b) ? (float)a : 0f;" },
        { "MMO1007", 2, "public static float M() { var (a, b) = (1.0, 2.0); return (float)a; }" },
        { "MMO1007", 1, "public static float M() { (double a, var b) = (1.0, 2); return (float)a + b; }" },
        { "MMO1007", 2, "public static float M((double, int)[] arr) { var s = 0f; foreach (var (a, _) in arr) s += (float)a; return s; }" },
        { "MMO1007", 2, "public static void Get(out nint v) => v = 0; public static int M() { Get(out var v); return (int)v; }" },
        { "MMO1007", 1, "public static float M(object o) { double x = o is double d ? d : 0; return (float)x; }" },

        { "", 0, "private const double Scale = 1.0 / 3.0; public static float M(float x) => x * (float)Scale;" },
        { "", 0, "public static float M(float x) => x * (float)(Math.PI / 180.0);" },
        { "", 0, "public static Func<float, float> M() => x => x * 2f;" },
        { "", 0, "public static int Count<T>(T v) => 1; public static int M(int n) => Count<int>(n);" },
        { "", 0, "public static float M(object o) => o is float f ? f : (o is double _ ? 1f : 0f);" },
        { "", 0, "public static float M() { var (p, q) = (1, 2f); return p + q; }" },
    };

    /// <summary>
    /// MMO1008 (US3.7): <c>OperatingSystem</c>, <c>IntPtr.Size</c> (MMO1008, not MMO1007),
    /// <c>Environment</c>, reflection, a <c>dynamic</c> operation, an <c>#if</c> block (one
    /// diagnostic, on the <c>#if</c> line, none for <c>#else</c>), <c>Debug</c>, an
    /// <c>[UnsafeAccessor]</c> declaration and <c>extern</c> declarations (P/Invoke methods and local
    /// functions, extern accessors), and the pointer size through <c>Unsafe</c> and <c>Marshal</c>, even
    /// with a banned type argument (MMO1008, not MMO1007); <c>typeof</c> without member access is allowed.
    /// </summary>
    public static TheoryData<string, int, string> PlatformPathRows => new()
    {
        { "MMO1008", 1, "public static bool M() => OperatingSystem.IsBrowser();" },
        { "MMO1008", 1, "public static int M() => IntPtr.Size;" },
        { "MMO1008", 1, "public static int M() => Environment.ProcessorCount;" },
        { "MMO1008", 1, "public static object? M() => typeof(MathF).GetMethod(\"Sin\");" },
        { "MMO1008", 1, "public static object M(dynamic d) => d.Foo();" },
        { "MMO1008", 1, IfElseMember },
        { "MMO1008", 1, "public static void M(bool b) => Debug.Assert(b);" },
        { "MMO1008", 1, "[System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"M\")] public static extern void Call(object o);" },

        // Review rows (Phase 5): the same attributes on local functions.
        { "MMO1008", 1, "public static void M() { Log(); [Conditional(\"DEBUG\")] static void Log() { } }" },
        { "MMO1008", 1, "public static void M(object o) { Call(o); [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = \"M\")] static extern void Call(object o); }" },

        // Review rows (Phase 5, second pass): native code. A P/Invoke method and local function, and an
        // extern property accessor, which no attribute need mark.
        { "MMO1008", 1, "[System.Runtime.InteropServices.DllImport(\"m\")] private static extern float Sinf(float x);" },
        { "MMO1008", 1, "public static float M(float x) { return Sinf(x); [System.Runtime.InteropServices.DllImport(\"m\")] static extern float Sinf(float x); }" },
        { "MMO1008", 1, "public static extern int P { get; }" },

        // Review rows (Phase 5, third pass): the pointer size through generic size queries, which
        // return 4 on wasm32 and 8 on x64 for a reference type or nint.
        { "MMO1008", 1, "public static int M() => System.Runtime.CompilerServices.Unsafe.SizeOf<object>();" },
        { "MMO1008", 1, "public static int M() => System.Runtime.InteropServices.Marshal.SizeOf<nint>();" },

        { "", 0, "public static object M() => typeof(int);" },
    };

    /// <summary>
    /// MMO1009 (US3.7): non-constant float-to-integer and float-to-enum conversions, <c>System.Convert</c>,
    /// an integer type's <c>Create*</c> from a float or from a type parameter that may be one, and
    /// <c>ulong</c> to <c>float</c>; a constant cast, integer-to-float, a <c>ulong</c> narrowed to
    /// <c>uint</c> first, <c>Create*</c> from a <c>long</c> or from an <c>IBinaryInteger&lt;T&gt;</c>
    /// type parameter, and an <c>int</c>-to-enum cast are allowed.
    /// </summary>
    public static TheoryData<string, int, string> ConversionRows => new()
    {
        { "MMO1009", 1, "public static int M(float x) => (int)x;" },
        { "MMO1009", 1, "public static byte M(float x) => (byte)(x * 2f);" },
        { "MMO1009", 1, "public static float M(ulong u) => u;" },
        { "MMO1009", 1, "public static int M(float x) => Convert.ToInt32(x);" },
        { "MMO1009", 1, "public static int M(float x) => int.CreateSaturating(x);" },
        { "MMO1009", 1, "public static long M(float x) => long.CreateTruncating(x);" },
        { "MMO1009", 1, "public static DayOfWeek M(float x) => (DayOfWeek)x;" },
        { "MMO1009", 1, "public static UInt128 M(float x) => (UInt128)x;" },

        // Coverage row (T167): a nullable float to a nullable integer.
        { "MMO1009", 1, "public static int? M(float? x) => (int?)x;" },

        // Review rows (Phase 5): the element conversions Roslyn makes without a conversion operation of
        // their own. A foreach iteration variable (to int, from ulong, to an enum, and around a
        // user-defined operator on either side); a tuple conversion, explicit, implicit, nested and
        // nullable; a deconstruction from a tuple, from Deconstruct and from an extension Deconstruct, and
        // in a foreach; a spread into an array, a List, an IEnumerable and a Span; and the left operand
        // of ??, alone and in a tuple.
        { "MMO1009", 1, "public static int M(float[] a) { var s = 0; foreach (int i in a) s += i; return s; }" },
        { "MMO1009", 1, "public static float M(ulong[] a) { var s = 0f; foreach (float f in a) s += f; return s; }" },
        { "MMO1009", 1, "public static int M(float[] a) { var s = 0; foreach (DayOfWeek i in a) s++; return s; }" },
        { "MMO1009", 1, "public readonly struct S { public static explicit operator S(int v) => default; } public static int M(float[] a) { var n = 0; foreach (S s in a) n++; return n; }" },
        { "MMO1009", 1, "public readonly struct W { public static implicit operator float(W w) => 1f; } public static int M(W[] a) { var n = 0; foreach (int i in a) n += i; return n; }" },
        { "MMO1009", 1, "public static (int, int) M((float, float) t) => ((int, int))t;" },
        { "MMO1009", 1, "public static (float, float) M((ulong, ulong) t) => t;" },
        { "MMO1009", 1, "public static (int, (int, int)) M((float, (float, float)) t) => ((int, (int, int)))t;" },
        { "MMO1009", 1, "public static (int, int)? M((float, float)? t) => ((int, int)?)t;" },
        { "MMO1009", 1, "public static float M((ulong, ulong) t) { (float p, float q) = t; return p + q; }" },
        { "MMO1009", 1, "public readonly struct V { public void Deconstruct(out ulong x, out ulong y) { x = 1; y = 2; } } public static float M(V v) { (float a, float b) = v; return a + b; }" },
        { "MMO1009", 1, "public static void Deconstruct(this Version v, out ulong a, out ulong b) { a = 1; b = 2; } public static float M(Version v) { (float x, float y) = v; return x + y; }" },
        { "MMO1009", 1, "public static float M((ulong, ulong)[] a) { var s = 0f; foreach ((float p, float q) in a) s += p + q; return s; }" },
        { "MMO1009", 1, "public static float M((ulong, (ulong, ulong))[] a) { var s = 0f; foreach ((float p, (float q, var r)) in a) s += p + q; return s; }" },
        { "MMO1009", 1, "public static float[] M(ulong[] a) => [.. a];" },
        { "MMO1009", 1, "public static List<float> M(ulong[] a) => [.. a];" },
        { "MMO1009", 1, "public static IEnumerable<float> M(ulong[] a) => [.. a];" },
        { "MMO1009", 1, "public static float M(ulong[] a) { Span<float> s = [.. a]; return s[0]; }" },
        { "MMO1009", 1, "public static float M(ulong? u, float f) => u ?? f;" },
        { "MMO1009", 1, "public static (float, float) M((ulong, ulong)? u, (float, float) f) => u ?? f;" },

        // Review rows (Phase 5, second pass): an integer type's Create* whose TOther is a type parameter
        // that may be bound to float; one constrained to IBinaryInteger<T> is allowed (below).
        { "MMO1009", 1, "public static int M<T>(T v) where T : INumberBase<T> => int.CreateSaturating(v);" },
        { "MMO1009", 1, "public static long M<T>(T v) where T : INumberBase<T> => long.CreateTruncating(v);" },

        { "", 0, "public static int M() => (int)2.5f;" },
        { "", 0, "public static float M(int i) => i * 0.5f;" },
        { "", 0, "public static float M(ulong h) => (float)(uint)(h >> 40);" },
        { "", 0, "public static int M(long l) => int.CreateSaturating(l);" },
        { "", 0, "public static DayOfWeek M(int i) => (DayOfWeek)i;" },
        { "", 0, "public static float M(float[] a) { var s = 0f; foreach (var f in a) s += f; return s; }" },
        { "", 0, "public static float M(int[] a) { var s = 0f; foreach (float f in a) s += f; return s; }" },
        { "", 0, "public static (float, float) M((int, int) t) => ((float, float))t;" },
        { "", 0, "public static float M((uint, uint) t) { (float p, float q) = t; return p + q; }" },
        { "", 0, "public static float[] M(uint[] a) => [.. a];" },
        { "", 0, "public static float M(uint? u, float f) => u ?? f;" },
        { "", 0, "public static int M<T>(T v) where T : IBinaryInteger<T> => int.CreateSaturating(v);" },
        { "", 0, "public static int M<T, U>(U v) where T : IBinaryInteger<T> where U : T, INumberBase<U> => int.CreateSaturating(v);" },
    };

    /// <summary>
    /// Every row of contracts/analyzer-rules.md#replacement-map-mmo1002-messages: the banned call (or
    /// float <c>%</c>) and the replacement its MMO1002 message must end with.
    /// </summary>
    public static TheoryData<string, string> ReplacementRows => new()
    {
        { "public static float M(float x) => MathF.Sin(x);", "'SimMath.Sin'" },
        { "public static float M(float x) => MathF.Cos(x);", "'SimMath.Cos'" },
        { "public static float M(float x) => MathF.Tan(x);", "'SimMath.Tan'" },
        { "public static (float, float) M(float x) => MathF.SinCos(x);", "'SimMath.SinCos'" },
        { "public static float M(float x) => MathF.Asin(x);", "'SimMath.Asin'" },
        { "public static float M(float x) => MathF.Acos(x);", "'SimMath.Acos'" },
        { "public static float M(float x) => MathF.Atan(x);", "'SimMath.Atan'" },
        { "public static float M(float x) => float.Atan2(x, 1f);", "'SimMath.Atan2'" },
        { "public static float M(float x) => MathF.Min(x, 0f);", "'SimMath.Min'" },
        { "public static float M(float x) => MathF.MinMagnitude(x, 0f);", "'SimMath.Min'" },
        { "public static float M(float x) => float.MinNumber(x, 0f);", "'SimMath.Min'" },
        { "public static float M(float x) => MathF.Max(x, 0f);", "'SimMath.Max'" },
        { "public static float M(float x) => MathF.MaxMagnitude(x, 0f);", "'SimMath.Max'" },
        { "public static float M(float x) => float.MaxNumber(x, 0f);", "'SimMath.Max'" },
        { "public static float M(float x) => Math.Clamp(x, 0f, 1f);", "'SimMath.Clamp'" },
        { "public static float M(float x) => float.SinPi(x);", "'SimMath.SinCosBinaryAngle' for binary angles" },
        { "public static float M(float x) => float.CosPi(x);", "'SimMath.SinCosBinaryAngle' for binary angles" },
        { "public static (float, float) M(float x) => float.SinCosPi(x);", "'SimMath.SinCosBinaryAngle' for binary angles" },
        { "public static float M(float x) => MathF.ReciprocalEstimate(x);", "'1f / x'" },
        { "public static float M(float x) => MathF.ReciprocalSqrtEstimate(x);", "'1f / SimMath.Sqrt(x)'" },
        { "public static float M(float x) => MathF.Round(x, 2);", "'MathF.Round(x)' on a scaled value, or 'SimMath.RoundToInt'" },
        { "public static float M(float a, float b, float t) => float.Lerp(a, b, t);", "'a + ((b - a) * t)' written as separate float operations" },
        { "public static float M(float x) => float.DegreesToRadians(x);", "a multiplication by a float constant" },
        { "public static float M(float x) => float.RadiansToDegrees(x);", "a multiplication by a float constant" },
        { "public static float M(float x) => MathF.Pow(x, 2f);", "scalar float arithmetic, or add a core function with a documented error bound" },
        { "public static float M(float x) => x % 2f;", "scalar float arithmetic, or add a core function with a documented error bound" },
    };

    /// <summary>
    /// US3.3's fused calls: the four float and <c>Vector128&lt;float&gt;</c> forms, and
    /// <c>Math.FusedMultiplyAdd</c>, whose <c>double</c> declarations also report MMO1007 at their own
    /// locations.
    /// </summary>
    public static TheoryData<string> FusedCalls => new()
    {
        "public static float M(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);",
        "public static float M(float a) => float.MultiplyAddEstimate(a, a, a);",
        "public static Vector128<float> M(Vector128<float> a) => Vector128.FusedMultiplyAdd(a, a, a);",
        "public static Vector128<float> M(Vector128<float> a) => System.Runtime.Intrinsics.X86.Fma.MultiplyAdd(a, a, a);",
        "public static double M(double d) => Math.FusedMultiplyAdd(d, d, d);",
    };

    /// <summary>
    /// The full MMO1004 message (contracts/analyzer-rules.md#messages) for the <c>foreach</c>, spread,
    /// LINQ-receiver, declaration-pattern and generic-call forms: <c>{0}</c> is <c>foreach</c>,
    /// <c>..</c>, the converted expression's or the pattern's source text, or the called method, and
    /// <c>{1}</c> is the enumerated expression's constructed type (for a generic call, the hash-ordered
    /// type argument), namespace-qualified.
    /// </summary>
    public static TheoryData<string, string> HashOrderMessageRows => new()
    {
        {
            "public static int M(Dictionary<int, int> d) { var s = 0; foreach (var kv in d) s += kv.Value; return s; }",
            "'foreach' enumerates 'System.Collections.Generic.Dictionary<int, int>'" + HashOrderAlternative
        },
        {
            "public static int[] M(HashSet<int> s) => [.. s];",
            "'..' enumerates 'System.Collections.Generic.HashSet<int>'" + HashOrderAlternative
        },
        {
            "public static int M(HashSet<int> s) => s.First();",
            "'s' enumerates 'System.Collections.Generic.HashSet<int>'" + HashOrderAlternative
        },
        {
            "public static int M(HashSet<int> h) { var s = 0; if (h is IEnumerable<int> e) foreach (var x in e) s += x; return s; }",
            "'IEnumerable<int> e' enumerates 'System.Collections.Generic.HashSet<int>'" + HashOrderAlternative
        },
        {
            "public static int Sum<T>(T e) where T : IEnumerable<int> { var s = 0; foreach (var x in e) s += x; return s; } public static int M(HashSet<int> h) => Sum(h);",
            "'C.Sum<HashSet<int>>(HashSet<int>)' enumerates 'System.Collections.Generic.HashSet<int>'" + HashOrderAlternative
        },
    };

    /// <summary>
    /// MMO1009's two reasons (contracts/analyzer-rules.md#messages): the float-to-integer reason for a
    /// cast, <c>System.Convert</c>, an integer type's <c>CreateSaturating</c> and a cast to an enum, and
    /// the <c>ulong</c>-to-<c>float</c> reason for an implicit widening.
    /// </summary>
    public static TheoryData<string, string> ConversionMessageRows => new()
    {
        { "public static int M(float x) => (int)x;", FloatToIntegerReason },
        { "public static int M(float x) => Convert.ToInt32(x);", FloatToIntegerReason },
        { "public static int M(float x) => int.CreateSaturating(x);", FloatToIntegerReason },
        { "public static DayOfWeek M(float x) => (DayOfWeek)x;", FloatToIntegerReason },
        { "public static float M(ulong u) => u;", UlongToFloatReason },
    };

    /// <summary>
    /// The forms that have no operation, or no identifier, of their own (contracts/analyzer-rules.md#messages):
    /// the sample, its one rule, the source text its diagnostic covers, and <c>{0}</c> and the text after
    /// it. An element conversion reports at the declared iteration type, the deconstruction variables,
    /// the deconstructed value, the spread's collection (named <c>..</c>), the left operand of <c>??</c>
    /// or the tuple conversion; an accessor's <c>Synchronized</c> flag or <c>extern</c> at its keyword,
    /// named as the accessor; a field-like event's flag at the event; a local function's flag, attribute
    /// or <c>extern</c> at its identifier; a lambda's at the lambda; and a local that a pattern or a
    /// deconstruction declares, which has no declarator, at its identifier.
    /// </summary>
    public static TheoryData<string, string, string, string, string> PlacementRows => new()
    {
        { "public static int M(float[] a) { var s = 0; foreach (int i in a) s += i; return s; }", "MMO1009", "int", "int", FloatToIntegerReason },
        { "public static float M((ulong, ulong)[] a) { var s = 0f; foreach ((float p, float q) in a) s += p + q; return s; }", "MMO1009", "(float p, float q)", "(float p, float q)", UlongToFloatReason },
        { "public static float M((ulong, ulong) t) { (float p, float q) = t; return p + q; }", "MMO1009", "t", "t", UlongToFloatReason },
        { "public static (int, int) M((float, float) t) => ((int, int))t;", "MMO1009", "((int, int))t", "((int, int))t", FloatToIntegerReason },
        { "public static float[] M(ulong[] a) => [.. a];", "MMO1009", "a", "..", UlongToFloatReason },
        { "public static float M(ulong? u, float f) => u ?? f;", "MMO1009", "u", "u", UlongToFloatReason },
        { "public static int P { " + Synchronized + " get => 1; }", "MMO1006", "get", "C.P.get", BlockingText },
        { "public static event Action? E { add { } " + Synchronized + " remove { } }", "MMO1006", "remove", "C.E.remove", BlockingText },
        { "[method: System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)] public static event Action? E; public static void M() => E?.Invoke();", "MMO1006", "E", "E", BlockingText },
        { "public static void M() { Sync(); " + Synchronized + " static void Sync() { } }", "MMO1006", "Sync", "Sync", BlockingText },
        { "public static Func<int> M() => " + Synchronized + " () => 1;", "MMO1006", Synchronized + " () => 1", Synchronized + " () => 1", BlockingText },
        { "public static void M() { Log(); [Conditional(\"DEBUG\")] static void Log() { } }", "MMO1008", "Log", "Log", PlatformPathText },
        { "public static extern int P { get; }", "MMO1008", "get", "C.P.get", PlatformPathText },
        { "public static float M(float x) { return Sinf(x); [System.Runtime.InteropServices.DllImport(\"m\")] static extern float Sinf(float x); }", "MMO1008", "Sinf", "Sinf", PlatformPathText },
        { "public static float M(object o) => o is double d ? (float)d : 0f;", "MMO1007", "d", "d", "uses 'double'; " + NumericTypeText },
        { "public static float M() { (double a, var b) = (1.0, 2); return (float)a + b; }", "MMO1007", "a", "a", "uses 'double'; " + NumericTypeText },
    };

    /// <summary>
    /// The conditional-compilation members of <see cref="DirectivesReportOnTheirOwnLine"/> and the
    /// directive keyword each MMO1008 must name, in source order: the <c>#if</c>/<c>#else</c> block of
    /// <see cref="PlatformPathRows"/> (one, for <c>#if</c>) and the <c>#if</c>/<c>#elif</c> block of
    /// <c>TableTests</c> (one each).
    /// </summary>
    public static TheoryData<string, string[]> DirectiveRows => new()
    {
        { IfElseMember, ["#if"] },
        { IfElifMember, ["#if", "#elif"] },
    };

    /// <summary>
    /// The generated probe file of <see cref="AttributeApplicationsAreNotReported"/> (after its
    /// generated-code header) and the MMO1008 count it must give: an empty class gives none, so the
    /// attributes report nothing; a method body that reflects over <c>MathF</c> gives one, so the
    /// generated files are analyzed and the exemption covers only attribute applications.
    /// </summary>
    public static TheoryData<string, int> ProbeRows => new()
    {
        { "public static class Probe\n{\n}\n", 0 },
        { "public static class Probe\n{\n    public static object? P() => typeof(System.MathF).GetMethod(\"Sin\");\n}\n", 1 },
    };

    /// <summary>
    /// A violating sample reports exactly its rule, <paramref name="count"/> times, at Error, inside the
    /// sample member; an allowed sample (rule <c>""</c>, count 0) reports nothing. The sample must
    /// compile, so a typo cannot make an allowed row pass vacuously.
    /// </summary>
    [Theory]
    [MemberData(nameof(VectorRows))]
    [MemberData(nameof(PlatformMathRows))]
    [MemberData(nameof(FusedRows))]
    [MemberData(nameof(HashOrderRows))]
    [MemberData(nameof(AmbientRows))]
    [MemberData(nameof(BlockingRows))]
    [MemberData(nameof(NumericTypeRows))]
    [MemberData(nameof(PlatformPathRows))]
    [MemberData(nameof(ConversionRows))]
    public async Task EachSampleProducesExactlyItsRule(string ruleId, int count, string member)
    {
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sample must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            Enumerable.Repeat(ruleId, count).SequenceEqual(result.Diagnostics.Select(d => d.Id)),
            $"Expected {count} x '{ruleId}', got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(
            result.Diagnostics,
            d => Assert.True(
                d.Location.SourceTree == result.Tree && result.MemberSpan.Contains(d.Location.SourceSpan),
                $"{AnalyzerHarness.Describe(d)} lies outside the sample member {result.MemberSpan}."));
    }

    /// <summary>
    /// US3.2: the MMO1002 message names the reported operation and the core's replacement. The one
    /// diagnostic's message is <c>'{0}' is platform math, which is not correctly rounded and can differ
    /// between builds; use {1}</c>, where <c>{0}</c> is the called method as
    /// <see cref="SymbolDisplayFormat.CSharpShortErrorMessageFormat"/> shows it (for example
    /// <c>MathF.Sin(float)</c>), or the operator <c>%</c>, and <c>{1}</c> is the replacement.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReplacementRows))]
    public async Task PlatformMathMessageNamesTheReplacement(string member, string replacement)
    {
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);
        var diagnostic = SingleDiagnostic(result, "MMO1002");
        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);
        var operation = OperationDisplay(result);

        Assert.StartsWith($"'{operation}'", message, StringComparison.Ordinal);
        Assert.Contains(" is platform math, which is not correctly rounded and can differ between builds; use ", message, StringComparison.Ordinal);
        Assert.EndsWith(replacement, message, StringComparison.Ordinal);
        Assert.Equal($"'{operation}' is platform math, which is not correctly rounded and can differ between builds; use {replacement}", message);
    }

    /// <summary>
    /// US3.3: a fused call fails the build with the fused-multiply-add rule and no other rule. Every
    /// diagnostic is an Error that is not suppressed, so it fails the build. The diagnostics at the
    /// invocation's span are exactly <c>[MMO1003]</c>; every other diagnostic is MMO1007 on a
    /// <c>double</c> declaration's identifier (the parameter <c>d</c> and the method, for its return
    /// type), one per such declaration, and none lies on the call.
    /// </summary>
    [Theory]
    [MemberData(nameof(FusedCalls))]
    public async Task FusedCallsReportOnlyTheFmaRule(string member)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);
        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sample must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.All(result.Diagnostics, d =>
        {
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.False(d.IsSuppressed, $"{AnalyzerHarness.Describe(d)} must not be suppressed.");
        });

        var root = result.Tree.GetRoot(cancellationToken);
        var call = Assert.Single(root.DescendantNodes(result.MemberSpan).OfType<InvocationExpressionSyntax>());
        var report = AnalyzerHarness.Describe(result.Diagnostics);

        var atCall = result.Diagnostics.Where(d => d.Location.SourceSpan == call.Span).Select(d => d.Id).ToArray();
        Assert.True(atCall.SequenceEqual(["MMO1003"]), $"The call {call} must report exactly [MMO1003], got:{Environment.NewLine}{report}");

        var others = result.Diagnostics.Where(d => d.Location.SourceSpan != call.Span).ToArray();
        Assert.All(others, d =>
        {
            Assert.True(d.Id == "MMO1007", $"Only MMO1007 may be reported besides the call's MMO1003:{Environment.NewLine}{report}");
            Assert.False(d.Location.SourceSpan.OverlapsWith(call.Span), $"{AnalyzerHarness.Describe(d)} lies on the call {call}.");
        });

        var model = result.Compilation.GetSemanticModel(result.Tree);
        var doubleDeclarations = root.DescendantNodes(result.MemberSpan)
            .Select(node => node switch
            {
                MethodDeclarationSyntax method when model.GetDeclaredSymbol(method, cancellationToken)?.ReturnType.SpecialType == SpecialType.System_Double => method.Identifier.Span,
                ParameterSyntax parameter when model.GetDeclaredSymbol(parameter, cancellationToken)?.Type.SpecialType == SpecialType.System_Double => parameter.Identifier.Span,
                _ => (TextSpan?)null,
            })
            .OfType<TextSpan>()
            .OrderBy(span => span.Start)
            .ToArray();
        Assert.True(
            others.Select(d => d.Location.SourceSpan).OrderBy(span => span.Start).SequenceEqual(doubleDeclarations),
            $"Expected one MMO1007 on each double declaration [{string.Join(", ", doubleDeclarations)}], got:{Environment.NewLine}{report}");
    }

    /// <summary>
    /// US3.4: the one MMO1004 names the enumerating operation and the hash-ordered collection's type,
    /// and ends with the allowed alternative; the whole message equals the contract's format filled in.
    /// </summary>
    [Theory]
    [MemberData(nameof(HashOrderMessageRows))]
    public async Task HashOrderMessagesNameTheOperationAndTheCollection(string member, string message)
    {
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);
        var diagnostic = SingleDiagnostic(result, "MMO1004");

        Assert.Equal(message, diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// US3.7: the per-method exemption. In <c>MmoEngine.SimCore.Math.SimMath</c>, <c>Clamp</c> may call
    /// <c>System.Math.Clamp</c> and the three saturating helpers may cast a float to <c>int</c>, so
    /// nothing is reported in them; the same raw cast in another SimMath method, in a local function
    /// named <c>RoundToInt</c> and in a <c>RoundToInt(float, float)</c> overload reports MMO1009 (the
    /// exemption is keyed by the helpers' documentation IDs, not their names), a
    /// <c>ulong</c>-to-<c>float</c> cast reports MMO1009 even in SimMath, and the clamp and cast in
    /// another type report MMO1002 on the call and MMO1009 on the cast. The diagnostics are exactly
    /// those six, at those spans.
    /// </summary>
    [Fact]
    public async Task SimMathMayClampAndCastFloats()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await AnalyzerHarness.AnalyzeSourcesAsync([(SimMathPath, SimMathSource), (OtherTypePath, OtherTypeSource)]);
        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sources must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");

        var simMath = result.Compilation.SyntaxTrees.Single(t => t.FilePath == SimMathPath).GetRoot(cancellationToken);
        var other = result.Compilation.SyntaxTrees.Single(t => t.FilePath == OtherTypePath).GetRoot(cancellationToken);
        var otherCast = Method(simMath, "Other").DescendantNodes().OfType<CastExpressionSyntax>().Single();
        var localCast = Method(simMath, "Local").DescendantNodes().OfType<CastExpressionSyntax>().Single();
        var overloadCast = Method(simMath, "RoundToInt", parameterCount: 2).DescendantNodes().OfType<CastExpressionSyntax>().Single();
        var fromBitsCast = Method(simMath, "FromBits").DescendantNodes().OfType<CastExpressionSyntax>().Single();
        var typeMethod = Method(other, "M");
        var typeCast = typeMethod.DescendantNodes().OfType<CastExpressionSyntax>().Single();
        var typeCall = typeMethod.DescendantNodes().OfType<InvocationExpressionSyntax>().Single();

        var expected = new (string Path, string Id, TextSpan Span)[]
            {
                (SimMathPath, "MMO1009", otherCast.Span),
                (SimMathPath, "MMO1009", localCast.Span),
                (SimMathPath, "MMO1009", overloadCast.Span),
                (SimMathPath, "MMO1009", fromBitsCast.Span),
                (OtherTypePath, "MMO1009", typeCast.Span),
                (OtherTypePath, "MMO1002", typeCall.Span),
            }
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Span.Start)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToArray();
        var actual = result.Diagnostics
            .Select(d => (Path: d.Location.SourceTree?.FilePath ?? string.Empty, d.Id, Span: d.Location.SourceSpan))
            .ToArray();
        var report = AnalyzerHarness.Describe(result.Diagnostics);
        Assert.True(
            expected.SequenceEqual(actual),
            $"Expected [{string.Join(", ", expected)}], got:{Environment.NewLine}{report}");
        Assert.All(result.Diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));

        foreach (var (helper, parameterCount) in new[] { ("Clamp", 3), ("FloorToInt", 1), ("RoundToInt", 1), ("TruncateToInt", 1) })
        {
            var span = Method(simMath, helper, parameterCount).Span;
            Assert.DoesNotContain(
                result.Diagnostics,
                d => d.Location.SourceTree?.FilePath == SimMathPath && span.OverlapsWith(d.Location.SourceSpan));
        }
    }

    /// <summary>
    /// US3.7: the one MMO1009 is <c>'{0}' {1}</c> (contracts/analyzer-rules.md#messages), where
    /// <c>{0}</c> is the conversion's syntax (the cast expression, or the implicitly converted
    /// expression) or the called method, and <c>{1}</c> is the conversion's reason.
    /// </summary>
    [Theory]
    [MemberData(nameof(ConversionMessageRows))]
    public async Task ConversionMessagesGiveTheirReason(string member, string reason)
    {
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);
        var diagnostic = SingleDiagnostic(result, "MMO1009");
        var message = diagnostic.GetMessage(CultureInfo.InvariantCulture);

        Assert.EndsWith(reason, message, StringComparison.Ordinal);
        Assert.Equal($"'{ConversionDisplay(result)}' {reason}", message);
    }

    /// <summary>
    /// US3.6 and US3.7: a conversion, flag, attribute or local with no operation or identifier of its own
    /// reports exactly one diagnostic of its rule, covering exactly <paramref name="reportedText"/>, with
    /// the message <c>'{0}' …</c> where <c>{0}</c> is <paramref name="name"/> and the rest is
    /// <paramref name="text"/> (contracts/analyzer-rules.md#messages).
    /// </summary>
    [Theory]
    [MemberData(nameof(PlacementRows))]
    public async Task FormsWithoutTheirOwnSyntaxReportWhereTheContractSays(string member, string ruleId, string reportedText, string name, string text)
    {
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);
        var diagnostic = SingleDiagnostic(result, ruleId);

        Assert.Equal(reportedText, result.Tree.GetText(TestContext.Current.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.Equal($"'{name}' {text}", diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// US3.7, conditional compilation (T146): each <c>#if</c> and <c>#elif</c> reports one MMO1008 on its
    /// own line, and <c>#else</c> and <c>#endif</c> report nothing. In source order, the diagnostics are
    /// exactly one MMO1008 per entry of <paramref name="keywords"/>; each lies on a line whose text, less
    /// its indentation, starts with that keyword and a space, and its message is
    /// <c>'{keyword}' lets the server and WebAssembly builds take different paths; …</c>
    /// (contracts/analyzer-rules.md#messages: <c>{0}</c> is the directive keyword).
    /// </summary>
    [Theory]
    [MemberData(nameof(DirectiveRows))]
    public async Task DirectivesReportOnTheirOwnLine(string member, string[] keywords)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var result = await AnalyzerHarness.AnalyzeMemberAsync(member);
        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sample must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");

        var diagnostics = result.Diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
        var report = AnalyzerHarness.Describe(diagnostics);
        Assert.True(
            Enumerable.Repeat("MMO1008", keywords.Length).SequenceEqual(diagnostics.Select(d => d.Id)),
            $"Expected {keywords.Length} x 'MMO1008', got:{Environment.NewLine}{report}");

        var lines = result.Tree.GetText(cancellationToken).Lines;
        for (var i = 0; i < keywords.Length; i++)
        {
            var line = lines[diagnostics[i].Location.GetLineSpan().StartLinePosition.Line].ToString().TrimStart();
            Assert.True(
                line.StartsWith(keywords[i] + " ", StringComparison.Ordinal),
                $"{AnalyzerHarness.Describe(diagnostics[i])} must lie on the '{keywords[i]}' line, but its line is '{line}'.");
            Assert.Equal($"'{keywords[i]}' {PlatformPathText}", diagnostics[i].GetMessage(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// FR-014: an attribute application is not reported by any operation rule
    /// (contracts/analyzer-rules.md#attribute-applications). Three files compile together: the sample
    /// class with an <c>[Obsolete]</c> member, a generated <c>obj/Sample.AssemblyInfo.cs</c> holding the
    /// SDK's <c>System.Reflection</c> assembly attributes, and a generated <c>obj/Sample.Probe.cs</c>.
    /// The diagnostics are exactly <paramref name="reflectionCount"/> MMO1008, at Error, every one in
    /// the probe file: with an empty probe class nothing at all is reported, and reflection in a probe
    /// method body still reports its one MMO1008.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProbeRows))]
    public async Task AttributeApplicationsAreNotReported(string probe, int reflectionCount)
    {
        var result = await AnalyzerHarness.AnalyzeSourcesAsync(
        [
            (AnalyzerHarness.SamplePath, AnalyzerHarness.SampleText(ObsoleteMember)),
            (AssemblyInfoPath, AssemblyInfoSource),
            (ProbePath, GeneratedHeader + "\n" + probe),
        ]);
        var report = AnalyzerHarness.Describe(result.Diagnostics);

        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sources must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            Enumerable.Repeat("MMO1008", reflectionCount).SequenceEqual(result.Diagnostics.Select(d => d.Id)),
            $"Expected {reflectionCount} x 'MMO1008', got:{Environment.NewLine}{report}");
        Assert.All(result.Diagnostics, d =>
        {
            Assert.Equal(DiagnosticSeverity.Error, d.Severity);
            Assert.True(
                d.Location.SourceTree?.FilePath == ProbePath,
                $"{AnalyzerHarness.Describe(d)} must lie in {ProbePath}, not on an attribute application.");
        });
    }

    /// <summary>
    /// FR-014: generated code is analyzed and reported (contracts/analyzer-rules.md#generated-code). A
    /// file Roslyn treats as generated, by its <c>.g.cs</c> name or by its <c>&lt;auto-generated/&gt;</c>
    /// header, holding one <c>System.MathF.Sin</c> call reports exactly one MMO1002, at Error, on that
    /// call in that file.
    /// </summary>
    [Theory]
    [InlineData("X.g.cs", "")]
    [InlineData("X.cs", GeneratedHeader)]
    public async Task GeneratedCodeIsAnalyzed(string fileName, string header)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = header.Length == 0 ? GeneratedSource : header + "\n" + GeneratedSource;
        var result = await AnalyzerHarness.AnalyzeSourcesAsync([(fileName, source)]);
        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The source must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");

        var tree = Assert.Single(result.Compilation.SyntaxTrees);
        var call = Assert.Single(tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>());
        Assert.True(
            result.Diagnostics.Count == 1 && result.Diagnostics[0].Id == "MMO1002",
            $"Expected exactly one MMO1002 in the generated file {fileName}, got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        var diagnostic = result.Diagnostics[0];
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(fileName, diagnostic.Location.SourceTree?.FilePath);
        Assert.Equal(call.Span, diagnostic.Location.SourceSpan);
    }

    /// <summary>Asserts the sample compiles and reports exactly one diagnostic, with <paramref name="ruleId"/>, and returns it.</summary>
    private static Diagnostic SingleDiagnostic(SampleResult result, string ruleId)
    {
        Assert.True(
            result.CompilerErrors.Count == 0,
            $"The sample must compile:{Environment.NewLine}{AnalyzerHarness.Describe(result.CompilerErrors)}");
        Assert.True(
            result.Diagnostics.Count == 1 && result.Diagnostics[0].Id == ruleId,
            $"Expected exactly one {ruleId}, got:{Environment.NewLine}{AnalyzerHarness.Describe(result.Diagnostics)}");
        return result.Diagnostics[0];
    }

    /// <summary>
    /// The sample's reported operation as the message's <c>{0}</c> names it
    /// (contracts/analyzer-rules.md#messages): the one invocation's method in
    /// <see cref="SymbolDisplayFormat.CSharpShortErrorMessageFormat"/>, or, with no invocation, the
    /// one float <c>%</c> operator.
    /// </summary>
    private static string OperationDisplay(SampleResult result)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var nodes = result.Tree.GetRoot(cancellationToken).DescendantNodes(result.MemberSpan).ToArray();
        var invocations = nodes.OfType<InvocationExpressionSyntax>().ToArray();
        if (invocations.Length > 0)
        {
            var invocation = Assert.Single(invocations);
            var symbol = result.Compilation.GetSemanticModel(result.Tree).GetSymbolInfo(invocation, cancellationToken).Symbol;
            Assert.NotNull(symbol);
            return symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
        }

        var modulo = Assert.Single(nodes.OfType<BinaryExpressionSyntax>(), b => b.IsKind(SyntaxKind.ModuloExpression));
        return modulo.OperatorToken.Text;
    }

    /// <summary>
    /// The sample's conversion as MMO1009's <c>{0}</c> names it (contracts/analyzer-rules.md#messages):
    /// the one invocation's method in <see cref="SymbolDisplayFormat.CSharpShortErrorMessageFormat"/>;
    /// with no invocation, the one cast expression's source text; with neither, the expression body,
    /// which is the implicitly converted expression.
    /// </summary>
    private static string ConversionDisplay(SampleResult result)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var nodes = result.Tree.GetRoot(cancellationToken).DescendantNodes(result.MemberSpan).ToArray();
        var invocations = nodes.OfType<InvocationExpressionSyntax>().ToArray();
        if (invocations.Length > 0)
        {
            var invocation = Assert.Single(invocations);
            var symbol = result.Compilation.GetSemanticModel(result.Tree).GetSymbolInfo(invocation, cancellationToken).Symbol;
            Assert.NotNull(symbol);
            return symbol.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);
        }

        var casts = nodes.OfType<CastExpressionSyntax>().ToArray();
        if (casts.Length > 0)
        {
            return Assert.Single(casts).ToString();
        }

        return Assert.Single(nodes.OfType<ArrowExpressionClauseSyntax>()).Expression.ToString();
    }

    /// <summary>The one method named <paramref name="name"/> under <paramref name="root"/>, with <paramref name="parameterCount"/> parameters when given.</summary>
    private static MethodDeclarationSyntax Method(SyntaxNode root, string name, int? parameterCount = null) =>
        root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.Text == name && (parameterCount is null || m.ParameterList.Parameters.Count == parameterCount));
}
