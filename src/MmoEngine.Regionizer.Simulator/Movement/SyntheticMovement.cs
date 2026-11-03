using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Random;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Movement;

/// <summary>What the movement models share: the world mirror, the map, the fights and the configuration.</summary>
internal sealed class MovementContext(WorldState state, MapLayout layout, FightTable fights, MovementConfig movement, double captureZoneRadius)
{
    public WorldState State { get; } = state;

    public MapLayout Layout { get; } = layout;

    public FightTable Fights { get; } = fights;

    public MovementConfig Movement { get; } = movement;

    public MovementModelConfig Models => Movement.Models;

    /// <summary>A capture point is occupied while infantry of its fight stand within this radius.</summary>
    public double CaptureZoneRadius { get; } = captureZoneRadius;

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
}

/// <summary>One entity's synthetic movement model, with its own random stream.</summary>
internal abstract class MotionModel(DeterministicRandom random)
{
    protected DeterministicRandom Random { get; } = random;

    /// <summary>The entity's position at step 0.</summary>
    public abstract (double X, double Y, double Z) Place();

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

/// <summary>Infantry in a fight: waypoints in the fight disc, half near an objective; redeploys by two-step teleport.</summary>
internal sealed class InfantryModel(DeterministicRandom random, MovementContext context, Fight fight) : MotionModel(random)
{
    private double _wx;
    private double _wy;
    private double _speed;
    private long _nextRepick;

    public override (double X, double Y, double Z) Place()
    {
        var (x, y) = PointInFight();
        return (x, y, 0);
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
            (_wx, _wy) = context.Layout.Clamp(px, py);
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
        return context.Layout.Clamp(x, y);
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

    public override (double X, double Y, double Z) Place()
    {
        var (x, y) = PointOnAnnulus();
        return (x, y, 0);
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
        return context.Layout.Clamp(x, y);
    }
}

/// <summary>
/// Aircraft in a fight: straight strafing passes through the fight's centre, entering and leaving
/// <c>strafeEntryMetres</c> out, with a straight repositioning leg to the next entry point. Altitude
/// is the fight altitude over the fight disc and the cruise altitude elsewhere.
/// </summary>
internal sealed class StrafingAircraftModel(DeterministicRandom random, MovementContext context, Fight fight) : MotionModel(random)
{
    private double _tx;
    private double _ty;
    private double _exitX;
    private double _exitY;
    private bool _onPass;
    private double _speed;

    public override (double X, double Y, double Z) Place()
    {
        var (ex, ey, xx, xy) = NewPass();
        var f = Random.NextDouble();
        var x = ex + (f * (xx - ex));
        var y = ey + (f * (xy - ey));
        _tx = xx;
        _ty = xy;
        _onPass = true;
        _speed = context.DrawSpeed(Random, EntityClass.Aircraft);
        return (x, y, Altitude(x, y));
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

        return MovementUpdate.MoveTo(state.Layer[entityId], x, y, Altitude(x, y));
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

/// <summary>Point-to-point aircraft: fly between random bases at the cruise altitude.</summary>
internal sealed class PointToPointAircraftModel(DeterministicRandom random, MovementContext context) : MotionModel(random)
{
    private int _target;
    private double _speed;

    public override (double X, double Y, double Z) Place()
    {
        var bases = context.Layout.Bases;
        var from = Random.NextInt(0, bases.Count - 1);
        _target = PickOther(from);
        var f = Random.NextDouble();
        var x = bases[from].X + (f * (bases[_target].X - bases[from].X));
        var y = bases[from].Y + (f * (bases[_target].Y - bases[from].Y));
        _speed = context.DrawSpeed(Random, EntityClass.Aircraft);
        return (x, y, context.Models.AircraftCruiseAltitudeMetres);
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

        return MovementUpdate.MoveTo(state.Layer[entityId], x, y, context.Models.AircraftCruiseAltitudeMetres);
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

/// <summary>Travellers: move along their lane at a fixed speed and turn around at its ends.</summary>
internal sealed class TravellerModel(DeterministicRandom random, MovementContext context, Lane lane, double speed) : MotionModel(random)
{
    private int _direction = 1;

    public override (double X, double Y, double Z) Place()
    {
        var x = lane.StartX + (Random.NextDouble() * lane.Length);
        _direction = Random.Chance(0.5) ? 1 : -1;
        return (x, lane.Y, 0);
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

/// <summary>Capture points: static; occupied while infantry of the fight at its base stand within the zone radius.</summary>
internal sealed class CapturePointModel(DeterministicRandom random, MovementContext context, Base atBase, int objective) : MotionModel(random)
{
    public override (double X, double Y, double Z) Place()
    {
        var (x, y) = atBase.Objectives[objective];
        return (x, y, 0);
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
