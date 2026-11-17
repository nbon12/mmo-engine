using System.Globalization;

namespace MmoEngine.SimCore.Server;

/// <summary>
/// The divergence report (FR-026, specs/015-shared-simulation-core/contracts/state-serialization.md#divergence-report).
/// This phase has its expected-hash form, which <c>simcore streams verify</c> prints: a committed stream
/// stores hashes, not states, so the report names the stream, the first divergent step and both hashes
/// but no fields. Phase 4 adds the between-builds report with its field diff.
/// </summary>
public static class DivergenceReport
{
    /// <summary>
    /// The server build's run of <paramref name="stream"/> against its committed expected hashes, first
    /// differing at <paramref name="step"/>: two lines joined with <c>\n</c>, with no line break after the
    /// second,
    /// <c>DIVERGED stream &lt;name&gt;, build server, first divergent step &lt;n&gt;, against the expected hashes</c>
    /// and <c>  hash  expected &lt;x16&gt;  server &lt;x16&gt;</c>, each hash as 16 lowercase hex digits.
    /// </summary>
    /// <exception cref="ArgumentNullException">The stream name is null.</exception>
    public static string AgainstExpected(string stream, uint step, ulong expected, ulong actual)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"DIVERGED stream {stream}, build server, first divergent step {step}, against the expected hashes\n  hash  expected {expected:x16}  server {actual:x16}");
    }
}
