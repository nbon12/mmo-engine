using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>Maps the <c>regionizer</c> section onto the core's <see cref="RegionizerOptions"/>.</summary>
public static class RegionizerOptionsMapping
{
    /// <summary>The core options; <c>split.minAgeSeconds</c> becomes base steps at 60 Hz, rounded half away from zero.</summary>
    public static RegionizerOptions ToRegionizerOptions(this RegionizerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var radius = config.ClaimRadius;
        return new RegionizerOptions(
            config.SectionSize,
            config.Anchoring,
            config.MergeGap,
            new ClaimRadii(radius.Infantry, radius.GroundVehicle, radius.Aircraft, radius.Traveller, radius.CapturePointZone),
            config.Split.MinSections,
            config.Split.MinDeadPercent,
            TimeBase.SecondsToSteps(config.Split.MinAgeSeconds),
            config.TimerMode);
    }
}
