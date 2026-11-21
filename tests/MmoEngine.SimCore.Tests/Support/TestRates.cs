namespace MmoEngine.SimCore.Tests.Support;

/// <summary>
/// The three step rates every rate-dependent test runs at: 20, 30 and 60 Hz. Data variations are
/// Theories, never loops inside a Fact. A test that takes only the rate uses
/// <c>[Theory] [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]</c> with a
/// parameter named <c>hz</c>. A test with more data declares a test-class property, such as
/// <c>public static TheoryData&lt;InputRejection, int&gt; Cases =&gt; TestRates.WithRateLast(...)</c>,
/// and passes it to <c>[MemberData(nameof(Cases))]</c>. Rows come in the order of the values given,
/// each crossed with 20, 30 and 60 in that order, and every call returns a new instance.
/// </summary>
public static class TestRates
{
    /// <summary>The step rates, in ascending order.</summary>
    public static IReadOnlyList<int> Hz { get; } = [20, 30, 60];

    /// <summary>One row per step rate.</summary>
    public static TheoryData<int> All => [20, 30, 60];

    /// <summary>Rows <c>(hz, value)</c>: each value crossed with every step rate.</summary>
    public static TheoryData<int, T> With<T>(params T[] values)
    {
        RequireRows(values);
        var data = new TheoryData<int, T>();
        foreach (var value in values)
        {
            foreach (var hz in Hz)
            {
                data.Add(hz, value);
            }
        }

        return data;
    }

    /// <summary>Rows <c>(value, hz)</c>: each value crossed with every step rate.</summary>
    public static TheoryData<T, int> WithRateLast<T>(params T[] values)
    {
        RequireRows(values);
        var data = new TheoryData<T, int>();
        foreach (var value in values)
        {
            foreach (var hz in Hz)
            {
                data.Add(value, hz);
            }
        }

        return data;
    }

    /// <summary>Rows <c>(a, b, hz)</c>: each pair crossed with every step rate.</summary>
    public static TheoryData<T1, T2, int> WithRateLast<T1, T2>(params (T1, T2)[] rows)
    {
        RequireRows(rows);
        var data = new TheoryData<T1, T2, int>();
        foreach (var (a, b) in rows)
        {
            foreach (var hz in Hz)
            {
                data.Add(a, b, hz);
            }
        }

        return data;
    }

    private static void RequireRows<T>(T[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }
    }
}
