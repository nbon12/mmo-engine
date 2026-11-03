using MmoEngine.Regionizer.Simulator.Config;
using MmoEngine.Regionizer.Simulator.Metrics;
using MmoEngine.Regionizer.Simulator.Movement;
using MmoEngine.Regionizer.Simulator.Scenarios;
using MmoEngine.Regionizer.Simulator.Tests.Support;
using MmoEngine.Regionizer.Simulator.Trace;

namespace MmoEngine.Regionizer.Simulator.Tests.Trace;

/// <summary>The trace format (contracts/trace-format.md): writing, reading and checking, and how replay serves rows.</summary>
public sealed class TraceFormatTests
{
    private const string Header = "#trace v1 scenario=Baseline seed=7 scheduling=Lockstep configHash=0123456789abcdef";

    private static readonly EntitySpec Soldier = new(0, EntityClass.Infantry, 0, 10, 20, 0, 1);
    private static readonly EntitySpec Point = new(1, EntityClass.CapturePoint, 0, 30, 40, 0, -1);

    [Fact]
    public void RecorderWritesEveryOpInApplicationOrder()
    {
        var path = Path.Combine(TestRuns.TempDir(), "trace.csv");
        var header = new TraceHeader("Migration", 42, SchedulingMode.Interleaved, "00000000000000ff");
        using (var recorder = new TraceRecorder(path, header))
        {
            Assert.False(File.Exists(path), "the file is created with the first row");
            recorder.OnEntityAdded(0, new EntitySpec(0, EntityClass.Aircraft, 1, 0.1, -2.5, 300, -1));
            recorder.OnEntityAdded(0, Point with { Id = 1 });
            recorder.OnUpdateApplied(2, 0, MovementUpdate.MoveTo(1, 1.0 / 3, 2, 300));
            recorder.OnUpdateApplied(2, 0, MovementUpdate.None);
            recorder.OnUpdateApplied(4, 0, MovementUpdate.TeleportTo(0, 5, 6, 0));
            recorder.OnTeleportCompleted(6, 0);
            recorder.OnUpdateApplied(6, 1, MovementUpdate.Occupied);
            recorder.OnUpdateApplied(8, 1, MovementUpdate.Vacated);
            recorder.OnPortalChanged(8, 3, true);
            recorder.OnPortalChanged(10, 3, false);
            recorder.OnTick(default);
            recorder.OnEvent(new RunEvent(0, 0, RunEventType.Merge, 1));
            Assert.Equal(8, recorder.Rows);
            Assert.Equal(header, recorder.Header);
            Assert.Equal(Path.GetFullPath(path), recorder.Path);
        }

        Assert.Equal(
            [
                "#trace v1 scenario=Migration seed=42 scheduling=Interleaved configHash=00000000000000ff",
                TraceFormat.ColumnHeader,
                "0,0,add,Aircraft,1,0.1,-2.5,300,-1",
                "0,1,add,CapturePoint,0,30,40,0,-1",
                "2,0,move,,1,0.3333333333333333,2,300,",
                "4,0,teleportBegin,,0,5,6,0,",
                "6,1,occupied,,,,,,",
                "8,1,vacated,,,,,,",
                "8,3,portalOpen,,,,,,",
                "10,3,portalClose,,,,,,",
            ],
            File.ReadAllLines(path));
    }

    [Fact]
    public void RecorderWithNoRowsWritesNoFileAndRefusesRowsOnceDisposed()
    {
        var path = Path.Combine(TestRuns.TempDir(), "trace.csv");
        var recorder = new TraceRecorder(path, TraceHeader.Parse(Header));

        recorder.Dispose();
        recorder.Dispose();

        Assert.False(File.Exists(path));
        Assert.Throws<ObjectDisposedException>(() => recorder.OnPortalChanged(0, 1, true));
    }

    [Fact]
    public void ReaderReadsBackWhatTheRecorderWrote()
    {
        var path = Write(
            "0,0,add,Infantry,0,10,20,0,1",
            "0,1,add,CapturePoint,0,30,40,0,-1",
            string.Empty,
            "2,0,move,,0,11.25,20,0,",
            "2,1,occupied,,,,,,",
            "4,0,teleportBegin,,0,500,500,0,",
            "4,9,portalOpen,,,,,,",
            "6,1,vacated,,,,,,",
            "6,9,portalClose,,,,,,");

        using var reader = TraceReader.Open(path);
        var rows = new List<TraceRow>();
        while (reader.TryRead(out var row))
        {
            rows.Add(row);
        }

        Assert.Equal(TraceHeader.Parse(Header), reader.Header);
        Assert.Equal(path, reader.Path);
        Assert.Equal(2, reader.EntityCount);
        Assert.Equal(11, reader.LineNumber);
        Assert.Equal([TraceOp.Add, TraceOp.Add, TraceOp.Move, TraceOp.Occupied, TraceOp.TeleportBegin, TraceOp.PortalOpen, TraceOp.Vacated, TraceOp.PortalClose], rows.Select(r => r.Op));
        Assert.Equal(Soldier, rows[0].ToEntity());
        Assert.Equal(Point, rows[1].ToEntity());
        Assert.Equal(MovementUpdate.MoveTo(0, 11.25, 20, 0), rows[2].ToUpdate());
        Assert.Equal(MovementUpdate.Occupied, rows[3].ToUpdate());
        Assert.Equal(MovementUpdate.TeleportTo(0, 500, 500, 0), rows[4].ToUpdate());
        Assert.Equal(9, rows[5].EntityId);
        Assert.Equal(MovementUpdate.Vacated, rows[6].ToUpdate());
        Assert.Equal(new TraceSummary(TraceHeader.Parse(Header), 2, 4, 2, 6), TraceReader.Validate(path));
    }

    [Fact]
    public void HeaderRoundTripsAndIgnoresUnknownFields()
    {
        var header = new TraceHeader("Soak", ulong.MaxValue, SchedulingMode.Interleaved, "fedcba9876543210");

        Assert.Equal(header, TraceHeader.Parse(header.Format()));
        Assert.Equal(header, TraceHeader.Parse("#trace  v1 source=bots configHash=fedcba9876543210 scheduling=Interleaved seed=18446744073709551615 scenario=Soak"));
        Assert.Equal(
            "#trace v1 scenario=Baseline seed=1 scheduling=Lockstep configHash=" + TestRuns.Load(TestRuns.Small("Baseline", 1, 5)).ConfigHash,
            TraceHeader.For(TestRuns.Load(TestRuns.Small("Baseline", 1, 5))).Format());
    }

    public static TheoryData<string[], int, string> MalformedTraces => new()
    {
        { [], 1, "the file is empty" },
        { ["trace v1 scenario=Baseline seed=7 scheduling=Lockstep configHash=0123456789abcdef"], 1, "the first line must start with '#trace v1'" },
        { ["#trace"], 1, "the first line must start with '#trace v1'" },
        { ["#trace v2 scenario=Baseline seed=7 scheduling=Lockstep configHash=0123456789abcdef"], 1, "trace format 'v2' is not supported" },
        { ["#trace v1 scenario=Baseline scheduling=Lockstep configHash=0123456789abcdef"], 1, "the header has no seed=" },
        { ["#trace v1 scenario=Baseline seed= scheduling=Lockstep configHash=0123456789abcdef"], 1, "the header has no seed=" },
        { ["#trace v1 scenario=Baseline seed=-3 scheduling=Lockstep configHash=0123456789abcdef"], 1, "seed must be an unsigned 64-bit integer" },
        { ["#trace v1 scenario=Baseline seed=7 scheduling=1 configHash=0123456789abcdef"], 1, "scheduling must be one of Lockstep, Interleaved" },
        { ["#trace v1 scenario=Baseline seed=7 scheduling=Lockstep configHash=0123456789ABCDEF"], 1, "configHash must be 16 lowercase hex digits" },
        { ["#trace v1 scenario=Baseline seed=7 scheduling=Lockstep configHash=0123"], 1, "configHash must be 16 lowercase hex digits" },
        { [Header], 2, "the second line must be" },
        { [Header, "step,entityId,op"], 2, "the second line must be" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0"], 3, "expected 9 fields, found 8" },
        { [Header, TraceFormat.ColumnHeader, "x,0,add,Infantry,0,10,20,0,1"], 3, "step must be a whole number" },
        { [Header, TraceFormat.ColumnHeader, "-1,0,add,Infantry,0,10,20,0,1"], 3, "step must be a whole number" },
        { [Header, TraceFormat.ColumnHeader, "0,99999999999,add,Infantry,0,10,20,0,1"], 3, "entityId must be a whole number from 0 to 2147483647" },
        { [Header, TraceFormat.ColumnHeader, "0,0,jump,Infantry,0,10,20,0,1"], 3, "op must be one of add, move, teleportBegin, occupied, vacated, portalOpen, portalClose, was 'jump'" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Dragon,0,10,20,0,1"], 3, "class must be one of" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,0,0,10,20,0,1"], 3, "class must be one of" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,-1,10,20,0,1"], 3, "layer must be a whole number" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,NaN,20,0,1"], 3, "x must be a finite number" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,Infinity,0,1"], 3, "y must be a finite number" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,,1"], 3, "z must be a finite number" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0,-2"], 3, "fightTag must be a whole number of at least -1" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0,1", "0,0,move,,0,x,20,0,"], 4, "x must be a finite number" },
        { [Header, TraceFormat.ColumnHeader, "3,0,add,Infantry,0,10,20,0,1"], 3, "add rows must come first, at step 0" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0,1", "0,0,move,,0,1,1,0,", "0,1,add,Infantry,0,10,20,0,1"], 5, "add rows must come first, at step 0" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0,1", "0,2,add,Infantry,0,10,20,0,1"], 4, "expected 1, was 2" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0,1", "4,0,move,,0,1,1,0,", "2,0,move,,0,1,1,0,"], 5, "step 2 comes after step 4" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0,1", "2,5,move,,0,1,1,0,"], 4, "entity 5 was never added" },
        { [Header, TraceFormat.ColumnHeader, "0,0,add,Infantry,0,10,20,0,1", "2,0,occupied,,,,,,"], 4, "entity 0 is Infantry; only capture points are occupied or vacated" },
    };

    [Theory]
    [MemberData(nameof(MalformedTraces))]
    public void MalformedTraceIsRefusedNamingTheLine(string[] lines, int line, string message)
    {
        var path = WriteLines(lines);

        var ex = Assert.Throws<TraceFormatException>(() => TraceReader.Validate(path));

        Assert.Equal(line, ex.LineNumber);
        Assert.StartsWith($"line {line}: ", ex.Message, StringComparison.Ordinal);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingTraceIsRefused()
    {
        var path = Path.Combine(TestRuns.TempDir(), "none.csv");

        var ex = Assert.Throws<TraceFormatException>(() => TraceReader.ReadHeader(path));

        Assert.Equal(0, ex.LineNumber);
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rows are served at their own step. Late rows catch up: consecutive late moves on one layer
    /// collapse to the newest, while teleports, occupancy changes, layer changes and on-time rows are
    /// served one per call, in order. Portal rows are counted, not served.
    /// </summary>
    [Fact]
    public void ReplayServesRowsOnTimeAndCatchesUpLateMoves()
    {
        var path = Write(
            "0,0,add,Infantry,0,10,20,0,1",
            "0,1,add,CapturePoint,0,30,40,0,-1",
            "2,0,move,,0,11,20,0,",
            "4,0,move,,0,12,20,0,",
            "4,1,occupied,,,,,,",
            "4,3,portalOpen,,,,,,",
            "6,0,move,,0,13,20,0,",
            "6,0,teleportBegin,,0,500,500,0,",
            "8,0,move,,0,501,500,0,",
            "8,0,move,,0,502,500,0,",
            "10,0,move,,1,503,500,150,",
            "10,0,move,,1,504,500,150,",
            "14,0,move,,1,505,500,150,",
            "14,0,move,,0,506,500,0,");
        using var replay = ReplayMovementSource.Open(path, [Soldier, Point]);

        Assert.Equal(MovementUpdate.None, replay.Next(0, 0));
        Assert.Equal(MovementUpdate.None, replay.Next(1, 1));
        Assert.Equal(MovementUpdate.MoveTo(0, 11, 20, 0), replay.Next(0, 2));
        Assert.Equal(MovementUpdate.Occupied, replay.Next(1, 8));
        Assert.Equal(MovementUpdate.MoveTo(0, 13, 20, 0), replay.Next(0, 8));
        Assert.Equal(MovementUpdate.TeleportTo(0, 500, 500, 0), replay.Next(0, 8));
        replay.OnTeleportCompleted(0, 8);
        Assert.Equal(MovementUpdate.MoveTo(0, 501, 500, 0), replay.Next(0, 8));
        Assert.Equal(MovementUpdate.MoveTo(0, 502, 500, 0), replay.Next(0, 8));
        Assert.Equal(MovementUpdate.None, replay.Next(0, 8));
        Assert.Equal(MovementUpdate.MoveTo(1, 504, 500, 150), replay.Next(0, 12));
        Assert.Equal(MovementUpdate.None, replay.Next(0, 12));
        Assert.Equal(MovementUpdate.MoveTo(1, 505, 500, 150), replay.Next(0, 16));
        Assert.Equal(MovementUpdate.MoveTo(0, 506, 500, 0), replay.Next(0, 16));
        Assert.Equal(MovementUpdate.None, replay.Next(1, 100));

        Assert.Equal(11, replay.UpdatesRead);
        Assert.Equal(9, replay.UpdatesServed);
        Assert.Equal(2, replay.UpdatesCollapsed);
        Assert.Equal(1, replay.PortalRows);
        Assert.Equal(TraceHeader.Parse(Header), replay.Header);
    }

    public static TheoryData<string, string> MismatchedEntities => new()
    {
        { "fewer", "entity 1: the trace places CapturePoint (fight -1) at layer 0 (30, 40, 0), the configured scenario places only 1 entities" },
        { "more", "the trace places 2 entities, the configured scenario 3" },
        { "moved", "entity 0: the trace places Infantry (fight 1) at layer 0 (10, 20, 0), the configured scenario places Infantry (fight 1) at layer 0 (10, 20.5, 0)" },
        { "class", "entity 1: the trace places CapturePoint (fight -1) at layer 0 (30, 40, 0), the configured scenario places Traveller (fight -1) at layer 0 (30, 40, 0)" },
    };

    [Theory]
    [MemberData(nameof(MismatchedEntities))]
    public void ReplayRefusesATraceForOtherEntities(string variant, string message)
    {
        var path = Write("0,0,add,Infantry,0,10,20,0,1", "0,1,add,CapturePoint,0,30,40,0,-1", "2,0,move,,0,11,20,0,");
        EntitySpec[] entities = variant switch
        {
            "fewer" => [Soldier],
            "more" => [Soldier, Point, Point with { Id = 2 }],
            "moved" => [Soldier with { Y = 20.5 }, Point],
            _ => [Soldier, Point with { Class = EntityClass.Traveller }],
        };

        var ex = Assert.Throws<TraceMismatchException>(() => ReplayMovementSource.Open(path, entities));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void AnEmptyTraceReplaysNothing()
    {
        using var replay = ReplayMovementSource.Open(Write(), []);

        Assert.Equal(0, replay.UpdatesRead);
        Assert.Equal(new TraceSummary(TraceHeader.Parse(Header), 0, 0, 0, 0), TraceReader.Validate(Write()));
    }

    [Fact]
    public void AnInvalidRowStopsOpeningTheReplay()
    {
        var path = Write("0,0,add,Infantry,0,10,20,0,1", "0,1,add,Dragon,0,30,40,0,-1");

        Assert.Throws<TraceFormatException>(() => ReplayMovementSource.Open(path, [Soldier, Point]));
    }

    [Fact]
    public void RowsThatAreNotMovementHaveNoUpdate()
    {
        Assert.Throws<ArgumentException>(() => TraceRow.Update(0, 0, MovementUpdate.None));
        Assert.Throws<InvalidOperationException>(() => TraceRow.Portal(0, 1, true).ToUpdate());
        Assert.Throws<InvalidOperationException>(() => TraceRow.Add(0, Soldier).ToUpdate());
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceFormat.OpName((TraceOp)99));
        Assert.False(TraceRow.Portal(0, 1, false).IsUpdate);
        Assert.True(TraceRow.Update(0, 1, MovementUpdate.Vacated).IsUpdate);
    }

    /// <summary>A trace file with the standard header and the given data rows.</summary>
    private static string Write(params string[] rows) => WriteLines([Header, TraceFormat.ColumnHeader, .. rows]);

    private static string WriteLines(string[] lines)
    {
        var dir = TestRuns.TempDir();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "trace.csv");
        File.WriteAllText(path, lines.Length == 0 ? string.Empty : string.Join('\n', lines) + "\n");
        return path;
    }
}
