using MmoEngine.Regionizer.Simulator.Config;

namespace MmoEngine.Regionizer.Simulator.Movement;

/// <summary>
/// Which section layer an aircraft uses (spec, Assumptions: Air layer). With the air layer on, an
/// aircraft moves onto it as soon as it flies above <c>airLayer.altitudeMetres</c>, and back to its
/// ground layer only once it drops below that altitude minus <c>airLayer.hysteresisMetres</c>, so an
/// aircraft hovering at the threshold does not flip layers every tick. The air layer allows direct
/// entry, so neither change needs a portal. With the layer off, aircraft stay on their ground layer.
/// </summary>
internal sealed class AirLayerPolicy
{
    private readonly double _altitude;
    private readonly double _hysteresis;

    public AirLayerPolicy(int layerId, double altitudeMetres, double hysteresisMetres)
    {
        LayerId = layerId;
        _altitude = altitudeMetres;
        _hysteresis = hysteresisMetres;
    }

    /// <summary>No air layer: every aircraft stays on its ground layer.</summary>
    public static AirLayerPolicy None { get; } = new(-1, 0, 0);

    /// <summary>The air layer's ID, or −1 when there is none.</summary>
    public int LayerId { get; }

    public bool Enabled => LayerId >= 0;

    /// <summary>The policy for a run: <paramref name="airLayerId"/> when <c>airLayer.enabled</c>, otherwise none.</summary>
    public static AirLayerPolicy From(AirLayerConfig config, int airLayerId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Enabled ? new AirLayerPolicy(airLayerId, config.AltitudeMetres, config.HysteresisMetres) : None;
    }

    /// <summary>The layer of an aircraft placed at <paramref name="altitude"/>: the air layer when above the threshold.</summary>
    public int InitialLayer(int groundLayer, double altitude) =>
        Enabled && altitude > _altitude ? LayerId : groundLayer;

    /// <summary>The layer after a move to <paramref name="altitude"/> from <paramref name="currentLayer"/>, with hysteresis.</summary>
    public int LayerFor(int currentLayer, int groundLayer, double altitude)
    {
        if (!Enabled)
        {
            return groundLayer;
        }

        if (currentLayer == LayerId)
        {
            return altitude < _altitude - _hysteresis ? groundLayer : LayerId;
        }

        return altitude > _altitude ? LayerId : groundLayer;
    }
}
