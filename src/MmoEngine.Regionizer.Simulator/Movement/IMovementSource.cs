using MmoEngine.Regionizer.Simulator.Scenarios;

namespace MmoEngine.Regionizer.Simulator.Movement;

/// <summary>What happens to one entity in its region's tick.</summary>
public enum MovementOp : byte
{
    None,

    /// <summary>Move to (Layer, X, Y, Z).</summary>
    Move,

    /// <summary>Begin a two-step teleport to (Layer, X, Y, Z); it completes through the location queue.</summary>
    TeleportBegin,

    /// <summary>A capture point became occupied.</summary>
    Occupy,

    /// <summary>A capture point became unoccupied.</summary>
    Vacate,
}

/// <summary>One entity's update for one tick.</summary>
public readonly record struct MovementUpdate(MovementOp Op, int Layer, double X, double Y, double Z)
{
    public static MovementUpdate None => default;

    public static MovementUpdate MoveTo(int layer, double x, double y, double z) => new(MovementOp.Move, layer, x, y, z);

    public static MovementUpdate TeleportTo(int layer, double x, double y, double z) => new(MovementOp.TeleportBegin, layer, x, y, z);

    public static MovementUpdate Occupied => new(MovementOp.Occupy, 0, 0, 0, 0);

    public static MovementUpdate Vacated => new(MovementOp.Vacate, 0, 0, 0, 0);
}

/// <summary>
/// The source of every per-entity position update (FR-021, FR-022). The engine asks it once per entity
/// per region tick, in ascending entity ID within the region, and applies the answer to the
/// regionizer and to <see cref="WorldState"/>. Synthetic movement implements it now; trace replay can
/// replace it.
/// </summary>
public interface IMovementSource
{
    /// <summary>The update for <paramref name="entityId"/> in its region's tick at base step <paramref name="now"/>.</summary>
    MovementUpdate Next(int entityId, long now);

    /// <summary>The entity's two-step teleport completed at <paramref name="now"/>; it now stands at its destination.</summary>
    void OnTeleportCompleted(int entityId, long now);
}

/// <summary>
/// The simulator's mirror of every entity's class, tag and last applied position, indexed by entity
/// ID. The engine writes it after each applied update; movement models and monitors read it.
/// </summary>
public sealed class WorldState
{
    public WorldState(IReadOnlyList<EntitySpec> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        Count = entities.Count;
        Class = new EntityClass[Count];
        FightTag = new int[Count];
        Layer = new int[Count];
        X = new double[Count];
        Y = new double[Count];
        Z = new double[Count];
        InTransit = new bool[Count];
        Occupied = new bool[Count];
        InstantActionPending = new bool[Count];
        for (var i = 0; i < Count; i++)
        {
            var e = entities[i];
            if (e.Id != i)
            {
                throw new ArgumentException("Entity IDs must be compact and ascending from 0.", nameof(entities));
            }

            Class[i] = e.Class;
            FightTag[i] = e.FightTag;
            Layer[i] = e.Layer;
            X[i] = e.X;
            Y[i] = e.Y;
            Z[i] = e.Z;
        }
    }

    public int Count { get; }

    public EntityClass[] Class { get; }

    public int[] FightTag { get; }

    public int[] Layer { get; }

    public double[] X { get; }

    public double[] Y { get; }

    public double[] Z { get; }

    /// <summary>Between the two steps of a teleport.</summary>
    public bool[] InTransit { get; }

    /// <summary>Capture points only.</summary>
    public bool[] Occupied { get; }

    /// <summary>Set by scenario events (Migration's Instant Action); the entity's model teleports and clears it.</summary>
    public bool[] InstantActionPending { get; }

    /// <summary>Records an update the engine applied to the regionizer.</summary>
    public void Apply(int entityId, in MovementUpdate update)
    {
        switch (update.Op)
        {
            case MovementOp.Move:
                Layer[entityId] = update.Layer;
                X[entityId] = update.X;
                Y[entityId] = update.Y;
                Z[entityId] = update.Z;
                break;
            case MovementOp.TeleportBegin:
                InTransit[entityId] = true;
                Layer[entityId] = update.Layer;
                X[entityId] = update.X;
                Y[entityId] = update.Y;
                Z[entityId] = update.Z;
                break;
            case MovementOp.Occupy:
                Occupied[entityId] = true;
                break;
            case MovementOp.Vacate:
                Occupied[entityId] = false;
                break;
            default:
                break;
        }
    }

    /// <summary>Records a completed teleport; the destination was stored when it began.</summary>
    public void CompleteTeleport(int entityId) => InTransit[entityId] = false;
}
