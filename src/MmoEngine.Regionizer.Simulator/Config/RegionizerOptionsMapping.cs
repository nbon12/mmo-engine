using MmoEngine.Regionizer.Simulator.Scheduling;

namespace MmoEngine.Regionizer.Simulator.Config;

/// <summary>Maps the <c>regionizer</c> section onto the core's <see cref="RegionizerOptions"/>.</summary>
public static class RegionizerOptionsMapping
{
    /// <summary>The core options of a run, on the run's own time base (<c>scheduling.baseStepHz</c>).</summary>
    public static RegionizerOptions ToRegionizerOptions(this RunConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Regionizer.ToRegionizerOptions(TimeBase.From(config.Scheduling));
    }

    /// <summary>The core options at the default 60 Hz base step rate.</summary>
    public static RegionizerOptions ToRegionizerOptions(this RegionizerConfig config) => config.ToRegionizerOptions(TimeBase.Default);

    /// <summary>The core options; <c>split.minAgeSeconds</c> becomes base steps of <paramref name="time"/>, rounded half away from zero.</summary>
    public static RegionizerOptions ToRegionizerOptions(this RegionizerConfig config, TimeBase time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        var radius = config.ClaimRadius;
        return new RegionizerOptions(
            config.SectionSize,
            config.Anchoring,
            config.MergeGap,
            new ClaimRadii(radius.Infantry, radius.GroundVehicle, radius.Aircraft, radius.Traveller, radius.CapturePointZone),
            config.Split.MinSections,
            config.Split.MinDeadPercent,
            time.ToSteps(config.Split.MinAgeSeconds),
            config.TimerMode);
    }
}
