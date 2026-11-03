using System.Globalization;
using System.Text;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Trace;

/// <summary>The <c>op</c> column of a position trace (contracts/trace-format.md).</summary>
public enum TraceOp : byte
{
    /// <summary>The entity's initial placement at step 0, with its class and fight tag.</summary>
    Add,

    /// <summary>A position applied in the entity's region tick.</summary>
    Move,

    /// <summary>The destination of a two-step teleport.</summary>
    TeleportBegin,

    /// <summary>A capture point became occupied.</summary>
    Occupied,

    /// <summary>A capture point became unoccupied.</summary>
    Vacated,

    /// <summary>A portal opened; the entity ID column holds the portal ID.</summary>
    PortalOpen,

    /// <summary>A portal closed; the entity ID column holds the portal ID.</summary>
    PortalClose,
}

/// <summary>The first line of a trace: <c>#trace v1 scenario=&lt;name&gt; seed=&lt;n&gt; scheduling=&lt;mode&gt; configHash=&lt;16 hex&gt;</c>.</summary>
/// <param name="Scenario">The recorded run's scenario.</param>
/// <param name="Seed">The recorded run's seed.</param>
/// <param name="Scheduling">The recorded run's scheduling mode.</param>
/// <param name="ConfigHash">The recorded run's config hash, as in its manifest.</param>
public sealed record TraceHeader(string Scenario, ulong Seed, SchedulingMode Scheduling, string ConfigHash)
{
    /// <summary>The header of a trace recorded from <paramref name="loaded"/>.</summary>
    public static TraceHeader For(LoadedRunConfig loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        return new TraceHeader(loaded.Config.Scenario, loaded.Config.Seed, loaded.Config.Scheduling.Mode, loaded.ConfigHash);
    }

    /// <summary>The header line, without a line break.</summary>
    public string Format() => string.Create(
        CultureInfo.InvariantCulture,
        $"{TraceFormat.Magic} v{TraceFormat.Version} scenario={Scenario} seed={Seed} scheduling={Scheduling} configHash={ConfigHash}");

    /// <summary>Parses a header line. Unknown <c>key=value</c> fields are ignored.</summary>
    /// <exception cref="TraceFormatException">The line is not a version 1 trace header.</exception>
    public static TraceHeader Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2 || tokens[0] != TraceFormat.Magic)
        {
            throw new TraceFormatException(1, $"the first line must start with '{TraceFormat.Magic} v{TraceFormat.Version}'");
        }

        if (tokens[1] != "v" + TraceFormat.Version.ToString(CultureInfo.InvariantCulture))
        {
            throw new TraceFormatException(1, $"trace format '{tokens[1]}' is not supported (expected v{TraceFormat.Version})");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens.Skip(2))
        {
            var equals = token.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                fields[token[..equals]] = token[(equals + 1)..];
            }
        }

        var scenario = Required(fields, "scenario");
        if (!ulong.TryParse(Required(fields, "seed"), NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
        {
            throw new TraceFormatException(1, "seed must be an unsigned 64-bit integer");
        }

        if (!TraceFormat.TryParseName<SchedulingMode>(Required(fields, "scheduling"), out var scheduling))
        {
            throw new TraceFormatException(1, $"scheduling must be one of {string.Join(", ", Enum.GetNames<SchedulingMode>())}");
        }

        var configHash = Required(fields, "configHash");
        if (configHash.Length != 16 || !configHash.All(char.IsAsciiHexDigitLower))
        {
            throw new TraceFormatException(1, "configHash must be 16 lowercase hex digits");
        }

        return new TraceHeader(scenario, seed, scheduling, configHash);
    }

    private static string Required(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new TraceFormatException(1, $"the header has no {key}=");
}

/// <summary>
/// One trace row. <see cref="Class"/> and <see cref="FightTag"/> are meaningful only for
/// <see cref="TraceOp.Add"/>; the position only for adds, moves and teleports. For portal rows
/// <see cref="EntityId"/> is the portal ID.
/// </summary>
public readonly record struct TraceRow(long Step, int EntityId, TraceOp Op, EntityClass Class, int Layer, double X, double Y, double Z, int FightTag)
{
    /// <summary>The <c>add</c> row of an entity placed at setup.</summary>
    public static TraceRow Add(long step, in EntitySpec entity) =>
        new(step, entity.Id, TraceOp.Add, entity.Class, entity.Layer, entity.X, entity.Y, entity.Z, entity.FightTag);

    /// <summary>The row of an applied movement update; <see cref="MovementOp.None"/> has no row.</summary>
    public static TraceRow Update(long step, int entityId, in MovementUpdate update) => update.Op switch
    {
        MovementOp.Move => new(step, entityId, TraceOp.Move, default, update.Layer, update.X, update.Y, update.Z, 0),
        MovementOp.TeleportBegin => new(step, entityId, TraceOp.TeleportBegin, default, update.Layer, update.X, update.Y, update.Z, 0),
        MovementOp.Occupy => new(step, entityId, TraceOp.Occupied, default, 0, 0, 0, 0, 0),
        MovementOp.Vacate => new(step, entityId, TraceOp.Vacated, default, 0, 0, 0, 0, 0),
        _ => throw new ArgumentException($"Movement op {update.Op} has no trace row.", nameof(update)),
    };

    /// <summary>The row of a portal toggle.</summary>
    public static TraceRow Portal(long step, int portalId, bool open) =>
        new(step, portalId, open ? TraceOp.PortalOpen : TraceOp.PortalClose, default, 0, 0, 0, 0, 0);

    /// <summary>True for the ops a movement source replays (move, teleport, occupancy).</summary>
    public bool IsUpdate => Op is TraceOp.Move or TraceOp.TeleportBegin or TraceOp.Occupied or TraceOp.Vacated;

    /// <summary>The entity an <see cref="TraceOp.Add"/> row places.</summary>
    public EntitySpec ToEntity() => new(EntityId, Class, Layer, X, Y, Z, FightTag);

    /// <summary>The movement update a move, teleport or occupancy row replays.</summary>
    public MovementUpdate ToUpdate() => Op switch
    {
        TraceOp.Move => MovementUpdate.MoveTo(Layer, X, Y, Z),
        TraceOp.TeleportBegin => MovementUpdate.TeleportTo(Layer, X, Y, Z),
        TraceOp.Occupied => MovementUpdate.Occupied,
        TraceOp.Vacated => MovementUpdate.Vacated,
        _ => throw new InvalidOperationException($"A {TraceFormat.OpName(Op)} row is not a movement update."),
    };
}

/// <summary>
/// The position trace format, version 1 (contracts/trace-format.md): a CSV file with a two-line
/// header, one row per applied movement in application order, doubles written round-trip.
/// </summary>
public static class TraceFormat
{
    public const int Version = 1;

    public const string Magic = "#trace";

    public const string ColumnHeader = "step,entityId,op,class,layer,x,y,z,fightTag";

    private const int Columns = 9;

    /// <summary>The op as written in the <c>op</c> column.</summary>
    public static string OpName(TraceOp op) => op switch
    {
        TraceOp.Add => "add",
        TraceOp.Move => "move",
        TraceOp.TeleportBegin => "teleportBegin",
        TraceOp.Occupied => "occupied",
        TraceOp.Vacated => "vacated",
        TraceOp.PortalOpen => "portalOpen",
        TraceOp.PortalClose => "portalClose",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unknown trace op."),
    };

    /// <summary>Writes <paramref name="row"/> as one CSV line, without a line break.</summary>
    public static void Append(StringBuilder line, in TraceRow row)
    {
        ArgumentNullException.ThrowIfNull(line);
        line.Append(Inv(row.Step)).Append(',').Append(Inv(row.EntityId)).Append(',').Append(OpName(row.Op)).Append(',');
        switch (row.Op)
        {
            case TraceOp.Add:
                line.Append(row.Class).Append(',').Append(Inv(row.Layer)).Append(',')
                    .Append(Inv(row.X)).Append(',').Append(Inv(row.Y)).Append(',').Append(Inv(row.Z)).Append(',')
                    .Append(Inv(row.FightTag));
                break;
            case TraceOp.Move or TraceOp.TeleportBegin:
                line.Append(',').Append(Inv(row.Layer)).Append(',')
                    .Append(Inv(row.X)).Append(',').Append(Inv(row.Y)).Append(',').Append(Inv(row.Z)).Append(',');
                break;
            default:
                line.Append(",,,,,");
                break;
        }
    }

    /// <summary>Parses one data line. Fields an op does not use are ignored.</summary>
    /// <exception cref="TraceFormatException">The line is malformed.</exception>
    public static TraceRow Parse(string line, int lineNumber)
    {
        ArgumentNullException.ThrowIfNull(line);
        var fields = line.Split(',');
        if (fields.Length != Columns)
        {
            throw new TraceFormatException(lineNumber, $"expected {Columns} fields, found {fields.Length}");
        }

        var step = Integer(fields[0], "step", lineNumber);
        var id = (int)Integer(fields[1], "entityId", lineNumber, int.MaxValue);
        var op = ParseOp(fields[2], lineNumber);
        switch (op)
        {
            case TraceOp.Add:
                if (!TryParseName<EntityClass>(fields[3], out var cls))
                {
                    throw new TraceFormatException(lineNumber, $"class must be one of {string.Join(", ", Enum.GetNames<EntityClass>())}, was '{fields[3]}'");
                }

                return new TraceRow(
                    step,
                    id,
                    op,
                    cls,
                    (int)Integer(fields[4], "layer", lineNumber, int.MaxValue),
                    Number(fields[5], "x", lineNumber),
                    Number(fields[6], "y", lineNumber),
                    Number(fields[7], "z", lineNumber),
                    FightTag(fields[8], lineNumber));
            case TraceOp.Move or TraceOp.TeleportBegin:
                return new TraceRow(
                    step,
                    id,
                    op,
                    default,
                    (int)Integer(fields[4], "layer", lineNumber, int.MaxValue),
                    Number(fields[5], "x", lineNumber),
                    Number(fields[6], "y", lineNumber),
                    Number(fields[7], "z", lineNumber),
                    0);
            default:
                return new TraceRow(step, id, op, default, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>An enum member by its exact name (no numbers, no case folding, no flag lists).</summary>
    internal static bool TryParseName<T>(string text, out T value)
        where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static TraceOp ParseOp(string text, int lineNumber)
    {
        foreach (var op in Enum.GetValues<TraceOp>())
        {
            if (string.Equals(OpName(op), text, StringComparison.Ordinal))
            {
                return op;
            }
        }

        throw new TraceFormatException(lineNumber, $"op must be one of {string.Join(", ", Enum.GetValues<TraceOp>().Select(OpName))}, was '{text}'");
    }

    private static long Integer(string text, string name, int lineNumber, long max = long.MaxValue) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value <= max
            ? value
            : throw new TraceFormatException(lineNumber, $"{name} must be a whole number from 0 to {max}, was '{text}'");

    private static int FightTag(string text, int lineNumber) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= -1
            ? value
            : throw new TraceFormatException(lineNumber, $"fightTag must be a whole number of at least -1, was '{text}'");

    private static double Number(string text, string name, int lineNumber) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new TraceFormatException(lineNumber, $"{name} must be a finite number, was '{text}'");

    private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Inv(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>A trace file that is not valid version 1 trace format; the message names the line.</summary>
public sealed class TraceFormatException : Exception
{
    public TraceFormatException(int lineNumber, string message)
        : base($"line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }

    public TraceFormatException(string message)
        : base(message)
    {
    }

    /// <summary>The 1-based line, or 0 when the error is about the file as a whole.</summary>
    public int LineNumber { get; }
}

/// <summary>A trace whose entities differ from those the configured scenario places, so it cannot be replayed.</summary>
public sealed class TraceMismatchException(string message) : Exception(message);
