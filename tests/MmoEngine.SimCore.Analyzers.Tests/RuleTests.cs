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

        { "", 0, "public static Vector128<float> M(Vector128<float> a, Vector128<float> b) => Vector128.Add(a, b) * b - Vector128.Create(1f);" },
        { "", 0, "public static float M(float x, float y) => x * y + 1f;" },
    };

    /// <summary>
    /// MMO1002 (US3.2): platform math outside the allowlist, as an invocation, a generic-math interface
    /// call, a method group or a function pointer, and float <c>%</c> and <c>%=</c>; the allowlist, the
    /// constants, <c>nameof</c>, integer <c>Math</c> and the bit casts are allowed.
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

        { "", 0, "public static float M(float x) => MathF.Sqrt(MathF.Abs(x)) + MathF.Floor(x) + MathF.Round(x) + MathF.Round(x, MidpointRounding.ToZero) + MathF.CopySign(x, -1f) + MathF.PI;" },
        { "", 0, "public static string M() => nameof(MathF.Sin);" },
        { "", 0, "public static int M(int i) => Math.Max(i, 0) + Math.Clamp(i, 0, 9);" },
        { "", 0, "public static uint M(float x) => float.IsFinite(x) ? BitConverter.SingleToUInt32Bits(x) : 0u;" },
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
    /// <c>IEnumerable&lt;T&gt;</c> or query syntax; lookups, <c>Count</c>, sorted collections and
    /// order-insensitive sinks are allowed.
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

        { "", 0, "public static int M(Dictionary<int, int> d, int k) => d.TryGetValue(k, out var v) ? v + d.Count : d.Keys.Count;" },
        { "", 0, "public static int M(SortedDictionary<int, int> d) { var s = 0; foreach (var kv in d) s += kv.Value; return s; }" },
        { "", 0, "public static void M(HashSet<int> a, HashSet<int> b) => a.UnionWith(b);" },
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
    /// The full MMO1004 message (contracts/analyzer-rules.md#messages) for the <c>foreach</c>, spread and
    /// LINQ-receiver forms: <c>{0}</c> is <c>foreach</c>, <c>..</c> or the converted expression's source
    /// text, and <c>{1}</c> is the enumerated expression's constructed type, namespace-qualified.
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
    /// US3.3: a fused call reports the fused-multiply-add rule and no other rule. The diagnostics at the
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
}
