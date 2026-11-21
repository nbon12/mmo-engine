using BenchmarkDotNet.Attributes;

namespace MmoEngine.SimCore.Benchmarks;

/// <summary>
/// One weapon step through <see cref="Weapons.WeaponStep.Step"/>, over every step of <c>rifle-burst</c>
/// (FR-030;
/// specs/015-shared-simulation-core/contracts/measurement-report.md#how-the-report-is-produced,
/// step 1). Each operation is <see cref="StepCase.StepCycling"/> of the case at 30 Hz, which restarts
/// the pass when it has ended. The class is public and not sealed, as BenchmarkDotNet's generated code
/// requires.
/// </summary>
[BenchmarkCategory("weapon")]
public class WeaponBenchmarks
{
    private const int StepRateHz = 30;

    private StepCase _case = null!;
    private uint _lastStep;

    /// <summary>The case, exported as the entry's <c>case</c>.</summary>
    [Params("rifle-hold")]
    public string Case { get; set; } = string.Empty;

    /// <summary>The step number the last operation computed, kept so the work is not eliminated.</summary>
    public uint LastStep => _lastStep;

    /// <summary>Builds the case and every input, state and buffer its pass needs.</summary>
    [GlobalSetup]
    public void Setup() => _case = StepCase.Create(StepCase.Weapon, Case, StepRateHz);

    /// <summary>The pass's next step.</summary>
    [Benchmark]
    public void Step()
    {
        _case.StepCycling();
        _lastStep = _case.LastStep;
    }
}
