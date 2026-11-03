using System.Globalization;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Running;
using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Trace;

/// <summary>
/// Replays a recorded position trace in place of synthetic movement (FR-022). Use it through
/// <see cref="RunOptions.MovementSourceFactory"/>. The trace's <c>add</c> rows must equal the
/// entities the configured scenario places. The trace is streamed: rows are read up to the current
/// step and queued per entity.
/// <para>
/// Each region tick asks for an entity's update; the source answers with the oldest queued row whose
/// step has come. With the recorded scheduling mode and seed every row is served at its own step and
/// point in the schedule, so the run's state hashes equal the recorded run's. When the schedule
/// differs (another mode, or a trace from elsewhere), rows that are late catch up: consecutive late
/// moves on one layer collapse to the newest, while teleports, occupancy changes and layer changes
/// are served one per tick, in order.
/// </para>
/// <para>
/// Portal rows are counted, not applied: portal toggles come from the scenario's step events, which
/// the configuration fixes, and the run records them again.
/// </para>
/// </summary>
public sealed class ReplayMovementSource : IMovementSource, IDisposable
{
    private readonly TraceReader _reader;
    private readonly Queue<(long Step, MovementUpdate Update)>?[] _queues;
    private TraceRow _next;
    private bool _hasNext;

    private ReplayMovementSource(TraceReader reader, int entities, TraceRow next, bool hasNext)
    {
        _reader = reader;
        _queues = new Queue<(long, MovementUpdate)>?[entities];
        _next = next;
        _hasNext = hasNext;
    }

    public TraceHeader Header => _reader.Header;

    /// <summary>Update rows read from the trace so far.</summary>
    public long UpdatesRead { get; private set; }

    /// <summary>Updates handed to the engine.</summary>
    public long UpdatesServed { get; private set; }

    /// <summary>Late moves dropped because a newer move on the same layer was also due.</summary>
    public long UpdatesCollapsed { get; private set; }

    /// <summary>Portal rows read (not applied; see the class remarks).</summary>
    public long PortalRows { get; private set; }

    /// <summary>
    /// Opens the trace at <paramref name="path"/> for a run whose scenario places
    /// <paramref name="entities"/>.
    /// </summary>
    /// <exception cref="TraceFormatException">The trace is invalid.</exception>
    /// <exception cref="TraceMismatchException">Its <c>add</c> rows differ from <paramref name="entities"/>.</exception>
    public static ReplayMovementSource Open(string path, IReadOnlyList<EntitySpec> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var reader = TraceReader.Open(path);
        try
        {
            var count = 0;
            bool hasNext;
            TraceRow row;
            while ((hasNext = reader.TryRead(out row)) && row.Op == TraceOp.Add)
            {
                var recorded = row.ToEntity();
                if (count >= entities.Count || recorded != entities[count])
                {
                    throw new TraceMismatchException(
                        $"entity {row.EntityId}: the trace places {Describe(recorded)}, the configured scenario {(count < entities.Count ? "places " + Describe(entities[count]) : $"places only {entities.Count} entities")}");
                }

                count++;
            }

            if (count != entities.Count)
            {
                throw new TraceMismatchException($"the trace places {count} entities, the configured scenario {entities.Count}");
            }

            return new ReplayMovementSource(reader, count, row, hasNext);
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    /// <summary>A factory for <see cref="RunOptions.MovementSourceFactory"/>; <paramref name="opened"/> receives the source it creates.</summary>
    public static Func<ScenarioWorld, Config.RunConfig, IMovementSource> Factory(string path, Action<ReplayMovementSource>? opened = null) =>
        (world, _) =>
        {
            ArgumentNullException.ThrowIfNull(world);
            var source = Open(path, world.Entities);
            opened?.Invoke(source);
            return source;
        };

    public MovementUpdate Next(int entityId, long now)
    {
        Fill(now);
        var queue = _queues[entityId];
        if (queue is null || !queue.TryDequeue(out var item))
        {
            return MovementUpdate.None;
        }

        while (item.Step < now
            && item.Update.Op == MovementOp.Move
            && queue.TryPeek(out var newer)
            && newer.Update.Op == MovementOp.Move
            && newer.Update.Layer == item.Update.Layer)
        {
            item = queue.Dequeue();
            UpdatesCollapsed++;
        }

        UpdatesServed++;
        return item.Update;
    }

    /// <summary>Nothing to do: the destination was the teleport row's position.</summary>
    public void OnTeleportCompleted(int entityId, long now)
    {
    }

    public void Dispose() => _reader.Dispose();

    private static string Describe(in EntitySpec e) => string.Create(
        CultureInfo.InvariantCulture,
        $"{e.Class} (fight {e.FightTag}) at layer {e.Layer} ({e.X:R}, {e.Y:R}, {e.Z:R})");

    /// <summary>Queues every row whose step is at most <paramref name="now"/>.</summary>
    private void Fill(long now)
    {
        while (_hasNext && _next.Step <= now)
        {
            if (_next.IsUpdate)
            {
                (_queues[_next.EntityId] ??= new Queue<(long, MovementUpdate)>()).Enqueue((_next.Step, _next.ToUpdate()));
                UpdatesRead++;
            }
            else
            {
                PortalRows++;
            }

            _hasNext = _reader.TryRead(out _next);
        }
    }
}
