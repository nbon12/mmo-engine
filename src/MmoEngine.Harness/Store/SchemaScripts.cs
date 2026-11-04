using System.Reflection;

namespace MmoEngine.Harness.Store;

/// <summary>The store's DDL: the embedded <c>Store/Schema/*.sql</c> files, in file-name order (FR-001).</summary>
public static class SchemaScripts
{
    private const string Prefix = "Schema.";

    /// <summary>Every DDL statement, as (file name, SQL), in apply order.</summary>
    public static IReadOnlyList<(string Name, string Sql)> All()
    {
        var assembly = typeof(SchemaScripts).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n => (n[Prefix.Length..], Read(assembly, n)))];
    }

    /// <summary>The tables the schema defines, in apply order.</summary>
    public static IReadOnlyList<string> Tables() =>
        [.. All().Select(s => TableName(s.Sql))];

    internal static string TableName(string sql)
    {
        const string marker = "CREATE TABLE IF NOT EXISTS ";
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException("A schema file has no CREATE TABLE IF NOT EXISTS statement.");
        }

        start += marker.Length;
        var end = start;
        while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_'))
        {
            end++;
        }

        return sql[start..end];
    }

    private static string Read(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
