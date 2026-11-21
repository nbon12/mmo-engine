using System.Globalization;
using MmoEngine.SimCore.Batch;
using MmoEngine.SimCore.Benchmarks;
using MmoEngine.SimCore.Collision;
using MmoEngine.SimCore.Config;
using MmoEngine.SimCore.Formats;
using MmoEngine.SimCore.Geometry;
using MmoEngine.SimCore.Hitboxes;
using MmoEngine.SimCore.Movement;
using MmoEngine.SimCore.Projectiles;
using MmoEngine.SimCore.Server;
using MmoEngine.SimCore.Tests.Support;
using MmoEngine.SimCore.World;

namespace MmoEngine.SimCore.Tests.Benchmarks;

/// <summary>
/// The benchmark and allocation drivers step the real core, the same way the world does (FR-030,
/// FR-032; plan.md, Tick budget and performance). A <see cref="StepCase"/> is one named case of one step
/// function: <c>StepCase.Create(kind, caseName, hz)</c> builds its scenario definition over
/// <see cref="SimCoreContent"/> at the rate and precomputes every input, state and buffer, and each
/// <see cref="StepCase.Step"/> runs one step of its pass, calling the core's own entry point. Each check
/// here builds its reference independently, from <see cref="TestConfigs"/> and
/// <see cref="TestWorlds.TestWorld"/>, and runs it on a <see cref="SimWorld"/> (<see cref="ScenarioRuns"/>)
/// or through <see cref="StreamRunner"/>.
/// <para>
/// Besides <c>Create</c>, <c>PassLength</c>, <c>StepsLeftInPass</c>, <c>Restart()</c>, <c>Step()</c>,
/// <c>StepCycling()</c> and <c>LastResultHash</c>, the tests read what the last <c>Step()</c> did:
/// <c>LastStep</c> (the step number it computed), for <c>movement</c> <c>Character</c> (character 0 as it
/// left it), for <c>weapon</c> <c>LastShotCount</c> (the shots it fired, <c>WeaponStep.Step</c>'s return),
/// for <c>projectile</c> <c>Projectile</c> (the projectile as it left it), <c>LastOutcome</c>,
/// <c>LastHit</c> and <c>LastExpiry</c> (<c>ProjectileStep.Step</c>'s return and its two out values), and
/// for <c>world</c> <c>LastFault</c> and <c>LastStepHash</c> (its <c>StepReport</c>'s <c>Fault</c> and
/// <c>Hash</c>).
/// </para>
/// </summary>
public sealed class StepCaseTests
{
    /// <summary>
    /// Rows <c>(kind, caseName, hz)</c>: every benchmarked and allocation-checked case, each at 20, 30 and
    /// 60 Hz, with the scenario definition it is built from: <c>movement</c> <c>flat-run</c>
    /// (<c>flat-run-&lt;hz&gt;hz</c>), <c>wall-slide</c> (<c>wall-slide-45</c>) and <c>corner-wedge</c>
    /// (<c>corner-wedge</c>); <c>weapon</c> <c>rifle-hold</c> (<c>rifle-burst</c>); <c>projectile</c>
    /// <c>launcher-flight</c> (<c>launcher-arc</c>); <c>hitboxBuild</c> <c>standing</c> and
    /// <c>stateHash</c> <c>one-character</c> (both <c>flat-run-&lt;hz&gt;hz</c>); and <c>world</c>
    /// <c>fuzz-all-buttons</c> (<c>fuzz-all-buttons</c>).
    /// </summary>
    public static TheoryData<string, string, int> Cases => TestRates.WithRateLast<string, string>(
        ("movement", "flat-run"),
        ("movement", "wall-slide"),
        ("movement", "corner-wedge"),
        ("weapon", "rifle-hold"),
        ("projectile", "launcher-flight"),
        ("hitboxBuild", "standing"),
        ("stateHash", "one-character"),
        ("world", "fuzz-all-buttons"));

    /// <summary>Rows <c>(caseName, hz)</c>: the three movement cases of <see cref="Cases"/>, each at 20, 30 and 60 Hz.</summary>
    public static TheoryData<string, int> MovementCases => TestRates.WithRateLast("flat-run", "wall-slide", "corner-wedge");

    /// <summary>
    /// The content the benchmarks build their cases from is the committed content: the world's checksum is
    /// that of <c>simcore/content/test-world.mmcd</c>, opened read-only and decoded with
    /// <see cref="CollisionFile.Decode"/>, and the embedded base configuration, compiled at 30 Hz, has the
    /// same bits as <c>TestConfigs.At(30)</c> compiled: the same configuration block and the same derived
    /// values, compared bit for bit.
    /// </summary>
    [Fact]
    public void ContentMatchesTheCommittedConfigAndWorld()
    {
        var committed = CollisionFile.Decode(RepoPaths.ReadAllBytes("simcore", "content", "test-world.mmcd"));
        Assert.True(
            SimCoreContent.World.Checksum == committed.Checksum,
            $"SimCoreContent.World has checksum {SimCoreContent.World.Checksum:x16}; simcore/content/test-world.mmcd has {committed.Checksum:x16}");

        var baseAt30 = SimCoreContent.BaseConfig.Clone();
        baseAt30.StepRateHz = 30;
        var expected = TestConfigs.Compiled(30);
        AssertSameBits(expected, CompiledConfig.Create(baseAt30), "SimCoreContent.BaseConfig compiled at 30 Hz");
        AssertSameBits(expected, SimCoreContent.Compiled(30), "SimCoreContent.Compiled(30)");
    }

    /// <summary>
    /// Given <c>StepCase.Create(kind, caseName, hz)</c>, its pass has at least one step and starts full.
    /// When <c>PassLength</c> <c>Step()</c> calls run, each takes one step off <c>StepsLeftInPass</c>, which
    /// ends at 0, and one more <c>Step()</c> throws <see cref="InvalidOperationException"/>. Then
    /// <c>Restart()</c> sets <c>StepsLeftInPass</c> back to <c>PassLength</c>, and a second pass ends with the
    /// same <c>LastResultHash</c> (the XxHash64 of the case's output state bytes) as the first.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void EachPassRunsItsStepsAndRestartRestoresIt(string kind, string caseName, int hz)
    {
        var what = $"{kind}/{caseName} at {hz} Hz";
        var stepCase = StepCase.Create(kind, caseName, hz);
        Assert.True(stepCase.PassLength >= 1, $"{what}: PassLength is {stepCase.PassLength}; a pass must have at least one step");
        Assert.True(
            stepCase.StepsLeftInPass == stepCase.PassLength,
            $"{what}: a new case has {stepCase.StepsLeftInPass} steps left in its pass; it must have PassLength {stepCase.PassLength}");

        RunPass(stepCase, what);
        var firstPass = stepCase.LastResultHash;
        Assert.Throws<InvalidOperationException>(() =>
        {
            stepCase.Step();
        });

        stepCase.Restart();
        Assert.True(
            stepCase.StepsLeftInPass == stepCase.PassLength,
            $"{what}: Restart() left {stepCase.StepsLeftInPass} steps in the pass; it must restore PassLength {stepCase.PassLength}");
        RunPass(stepCase, what);
        Assert.True(
            stepCase.LastResultHash == firstPass,
            $"{what}: the second pass ended with LastResultHash {stepCase.LastResultHash:x16}; the first ended with {firstPass:x16}");
    }

    /// <summary>
    /// Given a movement case and the <see cref="SimWorld"/> run of the same definition
    /// (<c>ScenarioCatalog.Get(name).Build(TestConfigs.At(hz), TestWorlds.TestWorld, hz)</c>), When one pass
    /// runs, Then character 0 ends with the Position, Velocity, Stance and Flags bits that the world
    /// committed after the same number of steps.
    /// </summary>
    [Theory]
    [MemberData(nameof(MovementCases))]
    public void MovementCasesEndWhereTheWorldEnds(string caseName, int hz)
    {
        var definition = Definition(caseName, hz);
        var stepCase = StepCase.Create("movement", caseName, hz);
        var what = $"movement/{caseName} at {hz} Hz";
        RunPass(stepCase, what);

        var run = WorldRun(definition, hz);
        Assert.True(
            stepCase.PassLength <= run.Steps.Count,
            $"{what}: the pass has {stepCase.PassLength} steps; {definition} has only {run.Steps.Count}");
        var worldStep = run.Steps[stepCase.PassLength - 1];
        Assert.True(
            worldStep.Fault.Code == StepFaultCode.None,
            $"{definition} at {hz} Hz: world step {worldStep.Step} faulted with {worldStep.Fault.Code} at {worldStep.Fault.Path}");
        Assert.Equal(Motion(worldStep.Characters[0]), Motion(stepCase.Character));
    }

    /// <summary>
    /// Given the <c>rifle-hold</c> case and the <see cref="SimWorld"/> run of <c>rifle-burst</c> at the rate,
    /// When one pass runs, Then it fires as many shots as the world's shot events hold, each on the step of
    /// the world's shot.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void WeaponCaseFiresTheWorldsShots(int hz)
    {
        var stepCase = StepCase.Create("weapon", "rifle-hold", hz);
        var shotSteps = new List<uint>();
        for (var i = 0; i < stepCase.PassLength; i++)
        {
            stepCase.Step();
            for (var shot = 0; shot < stepCase.LastShotCount; shot++)
            {
                shotSteps.Add(stepCase.LastStep);
            }
        }

        var worldShotSteps = new List<uint>();
        foreach (var step in WorldRun(Definition("rifle-hold", hz), hz).Steps)
        {
            worldShotSteps.AddRange(Enumerable.Repeat(step.Step, step.Shots.Count));
        }

        Assert.NotEmpty(worldShotSteps);
        Assert.Equal(worldShotSteps, shotSteps);
    }

    /// <summary>
    /// Given the <c>launcher-flight</c> case and the <see cref="SimWorld"/> run of <c>launcher-arc</c> at the
    /// rate, whose one launcher shot fires a projectile that later hits or expires, When one pass runs, Then
    /// every step but the last leaves the projectile flying, and the pass, which starts after the firing
    /// step, ends on the step on which the world's projectile hit or expired, with the same projectile ID,
    /// and the same <see cref="HitKind"/> and point bits for a hit or the same <see cref="ExpiryReason"/> for
    /// an expiry.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void ProjectileCaseEndsAtTheWorldsHitOrExpiry(int hz)
    {
        var definition = Definition("launcher-flight", hz);
        var run = WorldRun(definition, hz);
        var launches = new List<(uint Step, ShotEvent Shot)>();
        foreach (var step in run.Steps)
        {
            foreach (var shot in step.Shots)
            {
                launches.Add((step.Step, shot));
            }
        }

        var (firingStep, launch) = Assert.Single(launches);
        var projectileId = launch.ProjectileId;
        var end = Assert.Single(run.Steps, worldStep => EndsFlight(worldStep, projectileId));

        var stepCase = StepCase.Create("projectile", "launcher-flight", hz);
        var what = $"projectile/launcher-flight at {hz} Hz";
        for (var i = 0; i < stepCase.PassLength; i++)
        {
            stepCase.Step();
            if (i < stepCase.PassLength - 1)
            {
                Assert.True(
                    stepCase.LastOutcome == ProjectileOutcome.Flying,
                    $"{what}: step {i + 1} of {stepCase.PassLength} ended {stepCase.LastOutcome}; only the last step of the pass may end the flight");
            }
        }

        Assert.True(
            stepCase.PassLength == end.Step - firingStep,
            $"{what}: the pass has {stepCase.PassLength} steps; the world fired at step {firingStep} and its projectile ended at step {end.Step}, {end.Step - firingStep} steps later");
        Assert.Equal(end.Step, stepCase.LastStep);
        Assert.Equal(projectileId, stepCase.Projectile.Id);
        var hits = end.Hits.Where(worldHit => worldHit.ProjectileId == projectileId).ToList();
        if (hits.Count != 0)
        {
            var hit = Assert.Single(hits);
            Assert.Equal(ProjectileOutcome.Hit, stepCase.LastOutcome);
            Assert.Equal(hit.Kind, stepCase.LastHit.Kind);
            Assert.Equal(projectileId, stepCase.LastHit.ProjectileId);
            Assert.Equal(Bits(hit.Point), Bits(stepCase.LastHit.Point));
        }
        else
        {
            var expiry = Assert.Single(end.Expiries, worldExpiry => worldExpiry.ProjectileId == projectileId);
            Assert.Equal(ProjectileOutcome.Expired, stepCase.LastOutcome);
            Assert.Equal(expiry.Reason, stepCase.LastExpiry);
        }
    }

    /// <summary>
    /// Given the <c>fuzz-all-buttons</c> world case and <c>StreamRunner.Run</c> of the same built stream over
    /// the test world, When one pass runs, Then every step's <c>StepReport.Fault.Code</c> is
    /// <see cref="StepFaultCode.None"/>, and the pass has one step per record, each with the record's step
    /// number and hash.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestRates.All), MemberType = typeof(TestRates))]
    public void WorldCaseHasNoFault(int hz)
    {
        var stream = ScenarioCatalog.Get(Definition("fuzz-all-buttons", hz)).Build(TestConfigs.At(hz), TestWorlds.TestWorld, hz);
        CollisionWorld[] worlds = [TestWorlds.TestWorld];
        var records = Records(StreamRunner.Run(stream.Encode(), worlds));

        var stepCase = StepCase.Create("world", "fuzz-all-buttons", hz);
        var what = $"world/fuzz-all-buttons at {hz} Hz";
        Assert.True(
            stepCase.PassLength == records.Count,
            $"{what}: the pass has {stepCase.PassLength} steps; StreamRunner.Run of the stream gives {records.Count} records");
        for (var i = 0; i < stepCase.PassLength; i++)
        {
            stepCase.Step();
            var (step, hash) = records[i];
            Assert.True(
                stepCase.LastFault.Code == StepFaultCode.None,
                $"{what}: pass step {i + 1} (world step {stepCase.LastStep}) faulted with {stepCase.LastFault.Code} at {stepCase.LastFault.Path}");
            Assert.True(
                stepCase.LastStep == step && stepCase.LastStepHash == hash,
                $"{what}: pass step {i + 1} is world step {stepCase.LastStep} with hash {stepCase.LastStepHash:x16}; StreamRunner.Run's record {i} is step {step} with hash {hash:x16}");
        }
    }

    /// <summary>
    /// Runs one whole pass: <c>PassLength</c> <c>Step()</c> calls, each of which must take one step off
    /// <c>StepsLeftInPass</c>, ending at 0.
    /// </summary>
    private static void RunPass(StepCase stepCase, string what)
    {
        for (var i = 0; i < stepCase.PassLength; i++)
        {
            stepCase.Step();
            var left = stepCase.PassLength - (i + 1);
            Assert.True(
                stepCase.StepsLeftInPass == left,
                $"{what}: after {i + 1} of {stepCase.PassLength} steps, StepsLeftInPass is {stepCase.StepsLeftInPass}; it must be {left}");
        }

        Assert.Equal(0, stepCase.StepsLeftInPass);
    }

    /// <summary>Whether <paramref name="step"/> holds a hit or an expiry of projectile <paramref name="projectileId"/>.</summary>
    private static bool EndsFlight(ScenarioStep step, uint projectileId)
    {
        foreach (var hit in step.Hits)
        {
            if (hit.ProjectileId == projectileId)
            {
                return true;
            }
        }

        foreach (var expiry in step.Expiries)
        {
            if (expiry.ProjectileId == projectileId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The scenario definition a case is built from, at <paramref name="hz"/>.</summary>
    private static string Definition(string caseName, int hz) => caseName switch
    {
        "flat-run" or "standing" or "one-character" => string.Create(CultureInfo.InvariantCulture, $"flat-run-{hz}hz"),
        "wall-slide" => "wall-slide-45",
        "corner-wedge" => "corner-wedge",
        "rifle-hold" => "rifle-burst",
        "launcher-flight" => "launcher-arc",
        "fuzz-all-buttons" => "fuzz-all-buttons",
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "Unknown case."),
    };

    /// <summary>
    /// <c>ScenarioCatalog.Get(definition).Build(TestConfigs.At(hz), TestWorlds.TestWorld, hz)</c>, run on a new
    /// <see cref="SimWorld"/> over the test world with the stream's configuration compiled.
    /// </summary>
    private static ScenarioRun WorldRun(string definition, int hz)
    {
        var stream = ScenarioCatalog.Get(definition).Build(TestConfigs.At(hz), TestWorlds.TestWorld, hz);
        var world = new SimWorld(CompiledConfig.Create(stream.Config), TestWorlds.TestWorld);
        return ScenarioRuns.Run(world, stream);
    }

    /// <summary>The step number and hash of every packed step record in <paramref name="records"/>, in order; the records fill it exactly.</summary>
    private static List<(uint Step, ulong Hash)> Records(byte[] records)
    {
        var read = new List<(uint Step, ulong Hash)>();
        var offset = 0;
        while (offset < records.Length)
        {
            Assert.True(
                StepRecord.TryRead(records.AsSpan(offset), out var step, out _, out var hash, out var length),
                $"StreamRunner.Run's records do not parse at byte {offset} of {records.Length}");
            read.Add((step, hash));
            offset += length;
        }

        return read;
    }

    /// <summary>Character 0's movement result, bit for bit: Position, Velocity, Stance and Flags.</summary>
    private static string Motion(CharacterState character) => FormattableString.Invariant(
        $"position {Bits(character.Position)}, velocity {Bits(character.Velocity)}, stance {(byte)character.Stance}, flags 0x{(byte)character.Flags:x2}");

    private static string Bits(Vec3 vector) => FormattableString.Invariant(
        $"{BitConverter.SingleToUInt32Bits(vector.X):x8} {BitConverter.SingleToUInt32Bits(vector.Y):x8} {BitConverter.SingleToUInt32Bits(vector.Z):x8}");

    private static string Bits(float value) => BitConverter.SingleToUInt32Bits(value).ToString("x8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Asserts that <paramref name="actual"/> has <paramref name="expected"/>'s bits: the same configuration
    /// block (<see cref="ConfigBlock.Encode"/> of <see cref="CompiledConfig.Config"/>, every input field) and
    /// the same derived values, floats compared by their bits.
    /// </summary>
    private static void AssertSameBits(CompiledConfig expected, CompiledConfig actual, string what)
    {
        Assert.True(
            ConfigBlock.Encode(expected.Config).AsSpan().SequenceEqual(ConfigBlock.Encode(actual.Config)),
            $"{what}: its configuration block differs from TestConfigs.Compiled(30)'s");
        Assert.Equal(Derived(expected), Derived(actual));
    }

    /// <summary>
    /// The compiled values derived from the configuration, one line each, floats as their bits: the general,
    /// movement and projectile-class values, every compiled weapon and both hitbox layouts.
    /// </summary>
    private static List<string> Derived(CompiledConfig config)
    {
        var lines = new List<string>
        {
            Line("StepRateHz", config.StepRateHz),
            Line("MaxCharacters", config.MaxCharacters),
            Line("MaxProjectiles", config.MaxProjectiles),
            Line("WeaponSlots", config.WeaponSlots),
            Line("Dt", config.Dt),
            Line("WalkSpeed", config.WalkSpeed),
            Line("RunSpeed", config.RunSpeed),
            Line("CrouchSpeed", config.CrouchSpeed),
            Line("GroundAccelPerStep", config.GroundAccelPerStep),
            Line("GroundDecelPerStep", config.GroundDecelPerStep),
            Line("AirAccelPerStep", config.AirAccelPerStep),
            Line("Gravity", config.Gravity),
            Line("GravityPerStep", config.GravityPerStep),
            Line("JumpSpeed", config.JumpSpeed),
            Line("MaxFallSpeed", config.MaxFallSpeed),
            Line("CapsuleRadius", config.CapsuleRadius),
            Line("StandHeight", config.StandHeight),
            Line("CrouchHeight", config.CrouchHeight),
            Line("StepHeight", config.StepHeight),
            Line("SnapDistance", config.SnapDistance),
            Line("CosSlopeLimit", config.CosSlopeLimit),
            Line("LedgeSupportMinNormalY", config.LedgeSupportMinNormalY),
            Line("ContactSkin", config.ContactSkin),
            Line("ContactTolerance", config.ContactTolerance),
            Line("MaxSlideIterations", config.MaxSlideIterations),
            Line("MaxSubMoves", config.MaxSubMoves),
            Line("SweepTarget", config.SweepTarget),
            Line("SweepTolerance", config.SweepTolerance),
            Line("PushOutThreshold", config.PushOutThreshold),
            Line("HeadroomClearance", config.HeadroomClearance),
            Line("ShotCost", config.ShotCost),
            Line("MaxShotsPerStepMax", config.MaxShotsPerStepMax),
            Line("ProjectileClassCount", config.ProjectileClassCount),
            Line("MaxStateLength", config.MaxStateLength),
            Line("MaxStepRecordLength", config.MaxStepRecordLength),
        };
        for (var i = 0; i < config.AxisTop.Length; i++)
        {
            lines.Add(Line($"AxisTop[{i}]", config.AxisTop[i]));
        }

        for (var i = 0; i < config.ProjectileClassCount; i++)
        {
            lines.Add(Line($"HalfG[{i}]", config.HalfG[i]));
            lines.Add(Line($"MaxRangeSq[{i}]", config.MaxRangeSq[i]));
            lines.Add(Line($"LifetimeSteps[{i}]", config.LifetimeSteps[i]));
        }

        for (var i = 0; i < config.Weapons.Length; i++)
        {
            var weapon = config.Weapons[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"Weapons[{i}].");
            lines.Add(Line(prefix + "Rpm", weapon.Rpm));
            lines.Add(Line(prefix + "MaxShotsPerStep", weapon.MaxShotsPerStep));
            lines.Add(Line(prefix + "FireMode", (int)weapon.FireMode));
            lines.Add(Line(prefix + "MagazineSize", weapon.MagazineSize));
            lines.Add(Line(prefix + "ReserveRounds", weapon.ReserveRounds));
            lines.Add(Line(prefix + "ReloadSteps", weapon.ReloadSteps));
            lines.Add(Line(prefix + "EquipSteps", weapon.EquipSteps));
            lines.Add(Line(prefix + "HeatPerShotScaled", weapon.HeatPerShotScaled));
            lines.Add(Line(prefix + "OverheatThresholdScaled", weapon.OverheatThresholdScaled));
            lines.Add(Line(prefix + "HeatRecoveryScaled", weapon.HeatRecoveryScaled));
            lines.Add(Line(prefix + "HeatCooldownPerStep", weapon.HeatCooldownPerStep));
            lines.Add(Line(prefix + "HeatCooldownDelaySteps", weapon.HeatCooldownDelaySteps));
            lines.Add(Line(prefix + "SpreadHalfAngleDeg", weapon.SpreadHalfAngleDeg));
            lines.Add(Line(prefix + "VersMax", weapon.VersMax));
            lines.Add(Line(prefix + "RecoilKickPitchUnits", weapon.RecoilKickPitchUnits));
            lines.Add(Line(prefix + "RecoilKickYawUnits", weapon.RecoilKickYawUnits));
            lines.Add(Line(prefix + "RecoilMaxPitchUnits", weapon.RecoilMaxPitchUnits));
            lines.Add(Line(prefix + "RecoilMaxYawUnits", weapon.RecoilMaxYawUnits));
            lines.Add(Line(prefix + "RecoilRecoveryPerStep", weapon.RecoilRecoveryPerStep));
            lines.Add(Line(prefix + "RecoilRecoveryDelaySteps", weapon.RecoilRecoveryDelaySteps));
            lines.Add(Line(prefix + "MuzzleSpeed", weapon.MuzzleSpeed));
            lines.Add(Line(prefix + "ProjectileClass", weapon.ProjectileClass));
        }

        for (var i = 0; i < config.HitboxLayouts.Length; i++)
        {
            var layout = config.HitboxLayouts[i];
            var prefix = string.Create(CultureInfo.InvariantCulture, $"HitboxLayouts[{i}].");
            for (var c = 0; c < layout.Capsules.Length; c++)
            {
                var capsule = layout.Capsules[c];
                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{prefix}Capsules[{c}] {Bits(capsule.A)} {Bits(capsule.B)} {Bits(capsule.Radius)}"));
            }

            lines.Add(Line(prefix + "BoundCenterUp", layout.BoundCenterUp));
            lines.Add(Line(prefix + "BoundRadius", layout.BoundRadius));
            lines.Add(Line(prefix + "EyeHeight", layout.EyeHeight));
        }

        return lines;
    }

    private static string Bits(HitboxLocalPoint point) => $"{Bits(point.Fwd)} {Bits(point.Right)} {Bits(point.Up)}";

    private static string Line(string name, float value) => $"{name} {Bits(value)}";

    private static string Line(string name, int value) => string.Create(CultureInfo.InvariantCulture, $"{name} {value}");

    private static string Line(string name, uint value) => string.Create(CultureInfo.InvariantCulture, $"{name} {value}");
}
