using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Movement;

/// <summary>What the movement models share: the world mirror, the map, the fights, the air layer and the configuration.</summary>
internal sealed class MovementContext(
    WorldState state,
    MapLayout layout,
    FightTable fights,
    MovementConfig movement,
    double captureZoneRadius,
    AirLayerPolicy? airLayer = null)
{
    public WorldState State { get; } = state;

    public MapLayout Layout { get; } = layout;

    public FightTable Fights { get; } = fights;

    public MovementConfig Movement { get; } = movement;

    public MovementModelConfig Models => Movement.Models;

    /// <summary>A capture point is occupied while infantry of its fight stand within this radius.</summary>
    public double CaptureZoneRadius { get; } = captureZoneRadius;

    /// <summary>Which layer aircraft use at each altitude.</summary>
    public AirLayerPolicy AirLayer { get; } = airLayer ?? AirLayerPolicy.None;

    public double Ceiling(EntityClass cls) => cls switch
    {
        EntityClass.Infantry => Movement.SpeedCeiling.Infantry,
        EntityClass.GroundVehicle => Movement.SpeedCeiling.GroundVehicle,
        EntityClass.Aircraft => Movement.SpeedCeiling.Aircraft,
        _ => 0,
    };

    /// <summary>U[speedFactorMin, speedFactorMax] times the class ceiling.</summary>
    public double DrawSpeed(DeterministicRandom random, EntityClass cls) =>
        random.NextDouble(Models.SpeedFactorMin, Models.SpeedFactorMax) * Ceiling(cls);

    /// <summary>When the next waypoint is picked: U[min, max] seconds after <paramref name="now"/>.</summary>
    public long NextRepick(DeterministicRandom random, long now) =>
        now + Math.Max(1, TimeBase.SecondsToSteps(random.NextDouble(Models.WaypointIntervalSecondsMin, Models.WaypointIntervalSecondsMax)));

    /// <summary>Clamps a point into a fight's area: its own bounds (a bunker layer) or else the map.</summary>
    public (double X, double Y) Clamp(Fight fight, double x, double y) =>
        fight.Bounds is { } bounds ? bounds.Clamp(x, y) : Layout.Clamp(x, y);
}

/// <summary>One entity's synthetic movement model, with its own random stream.</summary>
internal abstract class MotionModel(DeterministicRandom random)
{
    protected DeterministicRandom Random { get; } = random;

    /// <summary>The entity's layer and position at step 0.</summary>
    public abstract (int Layer, double X, double Y, double Z) Place();

    /// <summary>The update for one tick, <paramref name="dtSeconds"/> after the previous one.</summary>
    public abstract MovementUpdate Step(int entityId, long now, double dtSeconds);

    /// <summary>The entity arrived from a two-step teleport.</summary>
    public virtual void OnTeleported(int entityId)
    {
    }
}

/// <summary>
/// The synthetic movement models of data-model.md behind <see cref="IMovementSource"/>. Each entity's
/// draws come from its own stream (seed, "movement", entity ID), so one entity's draws never shift
/// another's. Elapsed time per entity is measured from its previous update, which covers 20 Hz ticks.
/// </summary>
public sealed class SyntheticMovement : IMovementSource
{
    private readonly MotionModel[] _models;
    private readonly long[] _lastUpdate;

    internal SyntheticMovement(MotionModel[] models)
    {
        _models = models;
        _lastUpdate = new long[models.Length];
    }

    public MovementUpdate Next(int entityId, long now)
    {
        var dt = TimeBase.StepsToSeconds(now - _lastUpdate[entityId]);
        _lastUpdate[entityId] = now;
        return _models[entityId].Step(entityId, now, dt);
    }

    public void OnTeleportCompleted(int entityId, long now)
    {
        _lastUpdate[entityId] = now;
        _models[entityId].OnTeleported(entityId);
    }
}

/// <summary>
/// Infantry in a fight: waypoints in the fight disc (clamped to the fight's area), half near an
/// objective; redeploys by two-step teleport. They stay on their fight's layer.
/// </summary>
internal sealed class InfantryModel(DeterministicRandom random, MovementContext context, Fight fight) : MotionModel(random)
{
    private double _wx;
    private double _wy;
    private double _speed;
    private long _nextRepick;

    public override (int Layer, double X, double Y, double Z) Place()
    {
        var (x, y) = PointInFight();
        return (fight.Layer, x, y, 0);
    }

    public override MovementUpdate Step(int entityId, long now, double dtSeconds)
    {
        var state = context.State;
        if (state.InstantActionPending[entityId])
        {
            state.InstantActionPending[entityId] = false;
            return Redeploy(entityId);
        }

        if (dtSeconds > 0 && Random.Chance(context.Movement.RedeployProbabilityPerSecond * dtSeconds))
        {
            return Redeploy(entityId);
        }

        if (now >= _nextRepick)
        {
            PickWaypoint(now);
        }

        var (x, y, _) = Planar.MoveToward(state.X[entityId], state.Y[entityId], _wx, _wy, _speed * dtSeconds);
        return MovementUpdate.MoveTo(state.Layer[entityId], x, y, 0);
    }

    public override void OnTeleported(int entityId) => _nextRepick = 0;

    private MovementUpdate Redeploy(int entityId)
    {
        var (x, y) = PointInFight();
        return MovementUpdate.TeleportTo(context.State.Layer[entityId], x, y, 0);
    }

    private void PickWaypoint(long now)
    {
        var models = context.Models;
        var objectives = fight.Base.Objectives;
        if (objectives.Count > 0 && Random.Chance(models.ObjectiveWaypointShare))
        {
            var (ox, oy) = objectives[Random.NextInt(0, objectives.Count - 1)];
            var (px, py) = Planar.InDisc(Random, ox, oy, models.ObjectiveRadiusMetres);
            (px, py) = Planar.IntoDisc(px, py, fight.CenterX, fight.CenterY, fight.Radius);
            (_wx, _wy) = context.Clamp(fight, px, py);
        }
        else
        {
            (_wx, _wy) = PointInFight();
        }

        _speed = context.DrawSpeed(Random, EntityClass.Infantry);
        _nextRepick = context.NextRepick(Random, now);
    }

    private (double X, double Y) PointInFight()
    {
        var (x, y) = Planar.InDisc(Random, fight.CenterX, fight.CenterY, fight.Radius);
        return context.Clamp(fight, x, y);
    }
}

/// <summary>Ground vehicles in a fight: waypoints on the annulus from the fight radius outwards, at U[vehicleMin, max] × ceiling.</summary>
internal sealed class GroundVehicleModel(DeterministicRandom random, MovementContext context, Fight fight) : MotionModel(random)
{
    private double _wx;
    private double _wy;
    private double _speed;
    private long _nextRepick;
    private bool _arrived = true;

    public override (int Layer, double X, double Y, double Z) Place()
    {
        var (x, y) = PointOnAnnulus();
        return (fight.Layer, x, y, 0);
    }

    public override MovementUpdate Step(int entityId, long now, double dtSeconds)
    {
        var state = context.State;
        if (_arrived || now >= _nextRepick)
        {
            (_wx, _wy) = PointOnAnnulus();
            _speed = Random.NextDouble(context.Models.VehicleSpeedFactorMin, context.Models.SpeedFactorMax) * context.Ceiling(EntityClass.GroundVehicle);
            _nextRepick = context.NextRepick(Random, now);
        }

        var (x, y, arrived) = Planar.MoveToward(state.X[entityId], state.Y[entityId], _wx, _wy, _speed * dtSeconds);
        _arrived = arrived;
        return MovementUpdate.MoveTo(state.Layer[entityId], x, y, 0);
    }

    private (double X, double Y) PointOnAnnulus()
    {
        var (x, y) = Planar.OnAnnulus(Random, fight.CenterX, fight.CenterY, fight.Radius, fight.Radius + context.Models.VehicleAnnulusMetres);
        return context.Clamp(fight, x, y);
    }
}

/// <summary>
/// Aircraft in a fight: straight strafing passes through the fight's centre, entering and leaving
/// <c>strafeEntryMetres</c> out, with a straight repositioning leg to the next entry point. Altitude
/// is the fight altitude over the fight disc and the cruise altitude elsewhere; with the air layer on,
/// the altitude also picks the layer (<see cref="AirLayerPolicy"/>).
/// </summary>
internal sealed class StrafingAircraftModel(DeterministicRandom random, MovementContext context, Fight fight) : MotionModel(random)
{
    private double _tx;
    private double _ty;
    private double _exitX;
    private double _exitY;
    private bool _onPass;
    private double _speed;

    public override (int Layer, double X, double Y, double Z) Place()
    {
        var (ex, ey, xx, xy) = NewPass();
        var f = Random.NextDouble();
        var x = ex + (f * (xx - ex));
        var y = ey + (f * (xy - ey));
        _tx = xx;
        _ty = xy;
        _onPass = true;
        _speed = context.DrawSpeed(Random, EntityClass.Aircraft);
        var z = Altitude(x, y);
        return (context.AirLayer.InitialLayer(fight.Layer, z), x, y, z);
    }

    public override MovementUpdate Step(int entityId, long now, double dtSeconds)
    {
        var state = context.State;
        var (x, y, arrived) = Planar.MoveToward(state.X[entityId], state.Y[entityId], _tx, _ty, _speed * dtSeconds);
        if (arrived)
        {
            if (_onPass)
            {
                var (ex, ey, xx, xy) = NewPass();
                (_tx, _ty, _exitX, _exitY) = (ex, ey, xx, xy);
                _onPass = false;
            }
            else
            {
                (_tx, _ty) = (_exitX, _exitY);
                _onPass = true;
            }

            _speed = context.DrawSpeed(Random, EntityClass.Aircraft);
        }

        var z = Altitude(x, y);
        return MovementUpdate.MoveTo(context.AirLayer.LayerFor(state.Layer[entityId], fight.Layer, z), x, y, z);
    }

    private (double EntryX, double EntryY, double ExitX, double ExitY) NewPass()
    {
        var (ux, uy) = Random.UnitVector();
        var l = context.Models.StrafeEntryMetres;
        var (ex, ey) = context.Layout.Clamp(fight.CenterX - (ux * l), fight.CenterY - (uy * l));
        var (xx, xy) = context.Layout.Clamp(fight.CenterX + (ux * l), fight.CenterY + (uy * l));
        return (ex, ey, xx, xy);
    }

    private double Altitude(double x, double y) =>
        Planar.DistanceSquared(x, y, fight.CenterX, fight.CenterY) <= fight.Radius * fight.Radius
            ? context.Models.AircraftFightAltitudeMetres
            : context.Models.AircraftCruiseAltitudeMetres;
}

/// <summary>Point-to-point aircraft: fly between random bases at the cruise altitude, on the air layer when it is on.</summary>
internal sealed class PointToPointAircraftModel(DeterministicRandom random, MovementContext context) : MotionModel(random)
{
    private int _target;
    private double _speed;

    public override (int Layer, double X, double Y, double Z) Place()
    {
        var bases = context.Layout.Bases;
        var from = Random.NextInt(0, bases.Count - 1);
        _target = PickOther(from);
        var f = Random.NextDouble();
        var x = bases[from].X + (f * (bases[_target].X - bases[from].X));
        var y = bases[from].Y + (f * (bases[_target].Y - bases[from].Y));
        _speed = context.DrawSpeed(Random, EntityClass.Aircraft);
        var z = context.Models.AircraftCruiseAltitudeMetres;
        return (context.AirLayer.InitialLayer(0, z), x, y, z);
    }

    public override MovementUpdate Step(int entityId, long now, double dtSeconds)
    {
        var state = context.State;
        var target = context.Layout.Bases[_target];
        var (x, y, arrived) = Planar.MoveToward(state.X[entityId], state.Y[entityId], target.X, target.Y, _speed * dtSeconds);
        if (arrived)
        {
            _target = PickOther(_target);
            _speed = context.DrawSpeed(Random, EntityClass.Aircraft);
        }

        var z = context.Models.AircraftCruiseAltitudeMetres;
        return MovementUpdate.MoveTo(context.AirLayer.LayerFor(state.Layer[entityId], 0, z), x, y, z);
    }

    private int PickOther(int current)
    {
        var count = context.Layout.Bases.Count;
        if (count == 1)
        {
            return current;
        }

        var next = Random.NextInt(0, count - 2);
        return next >= current ? next + 1 : next;
    }
}

/// <summary>Where a clump of travellers starts: its centre along the lane, how far its members spread, and its direction (+1 or −1).</summary>
internal readonly record struct TravellerClump(double CenterX, double SpreadMetres, int Direction);

/// <summary>
/// Travellers: move along their lane at a fixed speed and turn around at its ends. A traveller starts
/// at a uniform point with a random direction or, in a clump, within the clump's spread of its centre
/// heading the clump's way.
/// </summary>
internal sealed class TravellerModel(DeterministicRandom random, MovementContext context, Lane lane, double speed, TravellerClump? clump = null)
    : MotionModel(random)
{
    private int _direction = 1;

    public override (int Layer, double X, double Y, double Z) Place()
    {
        if (clump is { } c)
        {
            var offset = Random.NextDouble(-c.SpreadMetres, c.SpreadMetres);
            _direction = c.Direction;
            return (0, Math.Min(Math.Max(c.CenterX + offset, lane.StartX), lane.EndX), lane.Y, 0);
        }

        var x = lane.StartX + (Random.NextDouble() * lane.Length);
        _direction = Random.Chance(0.5) ? 1 : -1;
        return (0, x, lane.Y, 0);
    }

    public override MovementUpdate Step(int entityId, long now, double dtSeconds)
    {
        var state = context.State;
        var x = state.X[entityId] + (_direction * speed * dtSeconds);
        if (x > lane.EndX)
        {
            x = Math.Max(lane.StartX, lane.EndX - (x - lane.EndX));
            _direction = -1;
        }
        else if (x < lane.StartX)
        {
            x = Math.Min(lane.EndX, lane.StartX + (lane.StartX - x));
            _direction = 1;
        }

        return MovementUpdate.MoveTo(state.Layer[entityId], x, lane.Y, 0);
    }
}

/// <summary>Capture points: static on their base's layer; occupied while infantry of the fight at its base stand within the zone radius.</summary>
internal sealed class CapturePointModel(DeterministicRandom random, MovementContext context, Base atBase, int objective) : MotionModel(random)
{
    public override (int Layer, double X, double Y, double Z) Place()
    {
        var (x, y) = atBase.Objectives[objective];
        return (atBase.Layer, x, y, 0);
    }

    public override MovementUpdate Step(int entityId, long now, double dtSeconds)
    {
        var state = context.State;
        var occupied = IsOccupied(state, entityId);
        if (occupied == state.Occupied[entityId])
        {
            return MovementUpdate.None;
        }

        return occupied ? MovementUpdate.Occupied : MovementUpdate.Vacated;
    }

    private bool IsOccupied(WorldState state, int entityId)
    {
        var fight = context.Fights.AtBase(atBase.Index);
        if (fight is null)
        {
            return false;
        }

        var r2 = context.CaptureZoneRadius * context.CaptureZoneRadius;
        var x = state.X[entityId];
        var y = state.Y[entityId];
        var layer = state.Layer[entityId];
        foreach (var infantry in fight.Infantry)
        {
            if (!state.InTransit[infantry] && state.Layer[infantry] == layer && Planar.DistanceSquared(state.X[infantry], state.Y[infantry], x, y) <= r2)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A vehicle column's route: back and forth along the straight line between two bases at a fixed
/// speed. A position is a pure function of time, so the column keeps its spacing at any tick rate.
/// </summary>
internal sealed class ColumnRoute
{
    public ColumnRoute(double ax, double ay, double bx, double by, double startArcMetres, double speedMetresPerSecond)
    {
        AX = ax;
        AY = ay;
        BX = bx;
        BY = by;
        StartArcMetres = startArcMetres;
        Speed = speedMetresPerSecond;
        Length = Math.Sqrt(Planar.DistanceSquared(ax, ay, bx, by));
    }

    public double AX { get; }

    public double AY { get; }

    public double BX { get; }

    public double BY { get; }

    /// <summary>Where along the loop A → B → A the column's leader is at step 0, in metres.</summary>
    public double StartArcMetres { get; }

    public double Speed { get; }

    /// <summary>|AB| in metres; one loop is twice this.</summary>
    public double Length { get; }

    /// <summary>The point <paramref name="behindMetres"/> behind the leader along the loop at base step <paramref name="step"/>.</summary>
    public (double X, double Y) At(long step, double behindMetres)
    {
        if (Length <= 0)
        {
            return (AX, AY);
        }

        var loop = 2 * Length;
        var arc = (StartArcMetres + (Speed * TimeBase.StepsToSeconds(step)) - behindMetres) % loop;
        if (arc < 0)
        {
            arc += loop;
        }

        if (arc <= Length)
        {
            var f = arc / Length;
            return (AX + ((BX - AX) * f), AY + ((BY - AY) * f));
        }

        var g = (arc - Length) / Length;
        return (BX + ((AX - BX) * g), BY + ((AY - BY) * g));
    }
}

/// <summary>A ground vehicle in a column: it follows the column's route <paramref name="behindMetres"/> behind the leader.</summary>
internal sealed class ColumnVehicleModel(DeterministicRandom random, MovementContext context, ColumnRoute route, double behindMetres) : MotionModel(random)
{
    public override (int Layer, double X, double Y, double Z) Place()
    {
        var (x, y) = route.At(0, behindMetres);
        return (0, x, y, 0);
    }

    public override MovementUpdate Step(int entityId, long now, double dtSeconds)
    {
        var (x, y) = route.At(now, behindMetres);
        return MovementUpdate.MoveTo(context.State.Layer[entityId], x, y, 0);
    }
}
