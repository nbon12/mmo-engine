using System.Text;

namespace MmoEngine.Regionizer.Simulator.Trace;

/// <summary>What a full pass over a valid trace found.</summary>
/// <param name="Header">The header line.</param>
/// <param name="Entities">Entities placed by <c>add</c> rows.</param>
/// <param name="Updates">Move, teleport and occupancy rows.</param>
/// <param name="PortalChanges">Portal rows.</param>
/// <param name="LastStep">The step of the last row, or 0 when there are no rows.</param>
public sealed record TraceSummary(TraceHeader Header, int Entities, long Updates, long PortalChanges, long LastStep);

/// <summary>
/// Reads a position trace row by row (contracts/trace-format.md), checking it as it goes: the header,
/// the column line, every field, steps that never decrease, <c>add</c> rows first at step 0 with IDs
/// compact from 0, updates only for added entities, and occupancy changes only for capture points.
/// Blank lines are skipped. Any problem throws <see cref="TraceFormatException"/> naming the line.
/// </summary>
public sealed class TraceReader : IDisposable
{
    private readonly StreamReader _reader;
    private readonly List<EntityClass> _classes = [];
    private long _lastStep;
    private bool _pastAdds;

    private TraceReader(string path, StreamReader reader)
    {
        Path = path;
        _reader = reader;
        Header = TraceHeader.Parse(NextLine() ?? throw new TraceFormatException(1, "the file is empty"));
        var columns = NextLine();
        if (columns != TraceFormat.ColumnHeader)
        {
            throw new TraceFormatException(2, $"the second line must be '{TraceFormat.ColumnHeader}'");
        }
    }

    /// <summary>The trace file.</summary>
    public string Path { get; }

    public TraceHeader Header { get; }

    /// <summary>The 1-based number of the last line read.</summary>
    public int LineNumber { get; private set; }

    /// <summary>Entities added so far.</summary>
    public int EntityCount => _classes.Count;

    /// <summary>Opens a trace and reads its two header lines.</summary>
    /// <exception cref="TraceFormatException">The file does not exist or its header is invalid.</exception>
    public static TraceReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = System.IO.Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new TraceFormatException($"file '{full}' does not exist");
        }

        var reader = new StreamReader(full, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
        try
        {
            return new TraceReader(full, reader);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>The header of the trace at <paramref name="path"/>.</summary>
    /// <exception cref="TraceFormatException">The file does not exist or its header is invalid.</exception>
    public static TraceHeader ReadHeader(string path)
    {
        using var reader = Open(path);
        return reader.Header;
    }

    /// <summary>Reads the whole trace, checking every row.</summary>
    /// <exception cref="TraceFormatException">The trace is invalid.</exception>
    public static TraceSummary Validate(string path)
    {
        using var reader = Open(path);
        long updates = 0;
        long portals = 0;
        while (reader.TryRead(out var row))
        {
            if (row.IsUpdate)
            {
                updates++;
            }
            else if (row.Op != TraceOp.Add)
            {
                portals++;
            }
        }

        return new TraceSummary(reader.Header, reader.EntityCount, updates, portals, reader._lastStep);
    }

    /// <summary>The next row, or false at the end of the file.</summary>
    /// <exception cref="TraceFormatException">The row is invalid.</exception>
    public bool TryRead(out TraceRow row)
    {
        string? line;
        do
        {
            line = NextLine();
            if (line is null)
            {
                row = default;
                return false;
            }
        }
        while (line.Length == 0);

        row = TraceFormat.Parse(line, LineNumber);
        Check(row);
        _lastStep = row.Step;
        return true;
    }

    public void Dispose() => _reader.Dispose();

    private void Check(in TraceRow row)
    {
        if (row.Step < _lastStep)
        {
            throw new TraceFormatException(LineNumber, $"step {row.Step} comes after step {_lastStep}; rows must be in application order");
        }

        if (row.Op == TraceOp.Add)
        {
            if (_pastAdds || row.Step != 0)
            {
                throw new TraceFormatException(LineNumber, "add rows must come first, at step 0");
            }

            if (row.EntityId != _classes.Count)
            {
                throw new TraceFormatException(LineNumber, $"entity IDs must be compact and ascending from 0; expected {_classes.Count}, was {row.EntityId}");
            }

            _classes.Add(row.Class);
            return;
        }

        _pastAdds = true;
        if (!row.IsUpdate)
        {
            return;
        }

        if (row.EntityId >= _classes.Count)
        {
            throw new TraceFormatException(LineNumber, $"entity {row.EntityId} was never added");
        }

        if (row.Op is TraceOp.Occupied or TraceOp.Vacated && _classes[row.EntityId] != EntityClass.CapturePoint)
        {
            throw new TraceFormatException(LineNumber, $"entity {row.EntityId} is {_classes[row.EntityId]}; only capture points are occupied or vacated");
        }
    }

    private string? NextLine()
    {
        var line = _reader.ReadLine();
        if (line is not null)
        {
            LineNumber++;
        }

        return line;
    }
}
