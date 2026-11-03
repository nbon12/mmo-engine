using System.Text;
using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Trace;

/// <summary>
/// Records every movement a run applies as a position trace (FR-022, contracts/trace-format.md):
/// the setup placements, then each move, teleport, occupancy change and portal toggle in application
/// order. Add it to <see cref="RunOptions.Listeners"/>. The file is created when the first row is
/// recorded, after the run folder is set up, so a trace may live inside the run folder.
/// </summary>
public sealed class TraceRecorder : RunListener, IDisposable
{
    private readonly StringBuilder _line = new();
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>A recorder that writes to <paramref name="path"/> under <paramref name="header"/>.</summary>
    public TraceRecorder(string path, TraceHeader header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(header);
        Path = System.IO.Path.GetFullPath(path);
        Header = header;
    }

    /// <summary>The trace file.</summary>
    public string Path { get; }

    public TraceHeader Header { get; }

    /// <summary>Rows recorded so far.</summary>
    public long Rows { get; private set; }

    /// <summary>A recorder for a run of <paramref name="loaded"/>.</summary>
    public static TraceRecorder For(LoadedRunConfig loaded, string path) => new(path, TraceHeader.For(loaded));

    public override void OnEntityAdded(long step, in EntitySpec entity) => Write(TraceRow.Add(step, entity));

    public override void OnUpdateApplied(long step, int entityId, in MovementUpdate update)
    {
        if (update.Op != MovementOp.None)
        {
            Write(TraceRow.Update(step, entityId, update));
        }
    }

    public override void OnPortalChanged(long step, int portalId, bool open) => Write(TraceRow.Portal(step, portalId, open));

    /// <summary>Flushes and closes the file.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer?.Dispose();
    }

    private void Write(in TraceRow row)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var writer = _writer ??= Open();
        _line.Clear();
        TraceFormat.Append(_line, row);
        writer.WriteLine(_line);
        Rows++;
    }

    private StreamWriter Open()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var writer = new StreamWriter(Path, append: false, new UTF8Encoding(false), bufferSize: 1 << 16) { NewLine = "\n" };
        writer.WriteLine(Header.Format());
        writer.WriteLine(TraceFormat.ColumnHeader);
        return writer;
    }
}
