using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.Harness;

// Scripted scenario driver. Activated by `--harness[=<name>]` on the
// godot command line. Runs windowed (so screenshots are real), enqueues
// a scenario worth of sim commands, samples the snapshot + watcher each
// second to a JSONL log, dumps a viewport screenshot every 5s, then
// quits the engine and writes a final report.json.
//
// Output: ProjectSettings.GlobalizePath("user://harness/<stamp>/") by
// default, or whatever path follows --harness-out=. Path is printed at
// startup so you can scp it back.
public partial class HarnessController : Node2D
{
    public SimHost Host { get; set; } = null!;
    public string Scenario { get; set; } = "default";
    public string OutputDir { get; set; } = string.Empty;

    private double _elapsed;
    private double _nextSampleAt;
    private double _nextScreenshotAt;
    private int _shotIndex;
    private int _stepIndex;
    private bool _finished;
    private bool _headless;
    private StreamWriter? _log;
    private readonly List<(double At, System.Action<HarnessController> Run, string Desc)> _schedule = new();
    private readonly List<string> _events = new();

    public override void _Ready()
    {
        if (string.IsNullOrEmpty(OutputDir))
        {
            var stamp = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
            OutputDir = ProjectSettings.GlobalizePath($"user://harness/{stamp}/");
        }
        Directory.CreateDirectory(OutputDir);
        _log = new StreamWriter(Path.Combine(OutputDir, "log.jsonl")) { AutoFlush = true };
        _headless = DisplayServer.GetName() == "headless";

        GD.Print($"[harness] scenario={Scenario} headless={_headless} out={OutputDir}");
        Log($"{{\"event\":\"start\",\"scenario\":\"{Scenario}\",\"tickHz\":{Host.TickHz},\"headless\":{(_headless ? "true" : "false")}}}");

        BuildSchedule();
    }

    private void BuildSchedule()
    {
        int c = SimConstants.MapSize / 2;
        switch (Scenario)
        {
            case "quick":
                _schedule.Add((2.0, h => h.PlaceWall(c + 1, c), "place wall E"));
                _schedule.Add((4.0, h => h.PlaceRing(c, c, 3), "place ring r3"));
                _schedule.Add((10.0, h => h.DraftLowest(), "draft lowest pawn"));
                _schedule.Add((12.0, h => h.MoveLowest(c - 8, c - 8, false), "move drafted SW"));
                _schedule.Add((15.0, h => h.Finish("quick complete"), "finish"));
                break;
            case "chop":
                // Verify trees exist, chop rect drops a wood pile.
                _schedule.Add((1.0, h => h.RecordTreeCount("start"), "record trees"));
                _schedule.Add((2.0, h => h.ChopRect(0, 0, SimConstants.MapSize - 1, SimConstants.MapSize - 1), "chop all"));
                _schedule.Add((3.0, h => h.RecordTreeCount("after-chop-cmd"), "record"));
                _schedule.Add((20.0, h => h.RecordTreeCount("late"), "record late"));
                _schedule.Add((22.0, h => h.Finish("chop complete"), "finish"));
                break;
            case "debug":
                // Verify spawn/remove machinery. Start count is 3, spawn 5,
                // expect 8, remove the lowest-id pawn, expect 7.
                _schedule.Add((1.0, h => h.RecordCount("start"), "record count"));
                for (int i = 0; i < 5; i++)
                {
                    double at = 2.0 + i * 0.2;
                    _schedule.Add((at, h => h.SpawnPawn(), "spawn pawn"));
                }
                _schedule.Add((4.0, h => h.RecordCount("after-spawn"), "record after spawn"));
                _schedule.Add((5.0, h => h.RemoveLowest(), "remove lowest"));
                _schedule.Add((6.0, h => h.RecordCount("after-remove"), "record after remove"));
                _schedule.Add((8.0, h => h.Finish("debug complete"), "finish"));
                break;
            case "doors":
                _schedule.Add((1.0, h => h.PlaceWall(c - 1, c), "wall W"));
                _schedule.Add((1.5, h => h.PlaceWall(c + 1, c), "wall E"));
                _schedule.Add((10.0, h => h.PlaceDoor(c, c), "place door"));
                _schedule.Add((20.0, h => h.DraftLowest(), "draft lowest"));
                _schedule.Add((21.0, h => h.MoveLowest(c, c + 4, false), "march south through door"));
                _schedule.Add((30.0, h => h.MoveLowest(c, c - 4, false), "march north back"));
                _schedule.Add((40.0, h => h.Finish("doors complete"), "finish"));
                break;
            case "stress":
                for (int r = 2; r <= 6; r++)
                {
                    double at = 2.0 + (r - 2) * 1.5;
                    int radius = r;
                    _schedule.Add((at, h => h.PlaceRing(c, c, radius), $"ring r{radius}"));
                }
                _schedule.Add((20.0, h => h.DraftLowest(), "draft lowest"));
                _schedule.Add((30.0, h => h.Finish("stress complete"), "finish"));
                break;
            default:
                _schedule.Add((2.0, h => h.PlaceWall(c + 1, c), "place wall E"));
                _schedule.Add((4.0, h => h.PlaceRing(c, c, 3), "ring r3"));
                _schedule.Add((8.0, h => h.PlaceRing(c, c, 5), "ring r5"));
                _schedule.Add((12.0, h => h.DraftLowest(), "draft lowest"));
                _schedule.Add((14.0, h => h.MoveLowest(c - 10, c - 10, false), "move drafted SW"));
                _schedule.Add((18.0, h => h.MoveLowest(c + 10, c + 10, true), "queue move NE"));
                _schedule.Add((22.0, h => h.PlaceRing(c, c, 7), "ring r7"));
                _schedule.Add((30.0, h => h.Finish("default complete"), "finish"));
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_finished) return;
        _elapsed += delta;

        while (_stepIndex < _schedule.Count && _elapsed >= _schedule[_stepIndex].At)
        {
            var step = _schedule[_stepIndex++];
            step.Run(this);
            _events.Add($"t={_elapsed:0.00} {step.Desc}");
            Log($"{{\"event\":\"step\",\"t\":{_elapsed:0.000},\"desc\":\"{step.Desc}\"}}");
        }

        if (_elapsed >= _nextSampleAt)
        {
            _nextSampleAt = _elapsed + 1.0;
            WriteSample();
        }
        if (_elapsed >= _nextScreenshotAt)
        {
            _nextScreenshotAt = _elapsed + 5.0;
            CallDeferred(nameof(Screenshot));
        }
    }

    private void WriteSample()
    {
        var snap = Host.LatestSnapshot;
        if (snap is null) return;
        var w = Host.Watcher;
        int idle = 0, walking = 0, drafted = 0, building = 0;
        foreach (var d in snap.Dummies)
        {
            if (d.Drafted) drafted++;
            else if (d.Job == "WallBuild") building++;
            else if (d.Job == "Idle") idle++;
            else walking++;
        }
        var sb = new StringBuilder();
        sb.Append("{\"event\":\"sample\"");
        sb.Append(",\"t\":").Append(_elapsed.ToString("0.000"));
        sb.Append(",\"tick\":").Append(snap.Tick);
        sb.Append(",\"fps\":").Append((int)Engine.GetFramesPerSecond());
        sb.Append(",\"tps\":").Append(Host.ActualTps.ToString("0"));
        sb.Append(",\"dummies\":").Append(snap.Dummies.Length);
        sb.Append(",\"idle\":").Append(idle);
        sb.Append(",\"walking\":").Append(walking);
        sb.Append(",\"building\":").Append(building);
        sb.Append(",\"drafted\":").Append(drafted);
        sb.Append(",\"blueprints\":").Append(snap.Blueprints.Length);
        sb.Append(",\"trees\":").Append(snap.Trees.Length);
        sb.Append(",\"wood\":").Append(snap.Wood.Length);
        sb.Append(",\"stuck\":").Append(w.StuckTotal);
        sb.Append(",\"braindead\":").Append(w.BrainDeadTotal);
        sb.Append('}');
        Log(sb.ToString());
    }

    public void Screenshot()
    {
        if (_headless) return;
        var tex = GetViewport().GetTexture();
        if (tex is null) return;
        var img = tex.GetImage();
        if (img is null) return;
        var path = Path.Combine(OutputDir, $"shot_{_shotIndex:D3}_t{(int)_elapsed:D3}s.png");
        img.SavePng(path);
        _shotIndex++;
        Log($"{{\"event\":\"screenshot\",\"t\":{_elapsed:0.000},\"path\":\"{Json(path)}\"}}");
    }

    private void PlaceWall(int x, int y)
    {
        Host.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(x, y)));
    }

    private void PlaceDoor(int x, int y)
    {
        Host.QueueCommand(new PlaceDoorBlueprintCommand(new TilePos(x, y)));
    }

    private void PlaceRing(int cx, int cy, int r)
    {
        for (int dx = -r; dx <= r; dx++)
        {
            PlaceWall(cx + dx, cy - r);
            PlaceWall(cx + dx, cy + r);
        }
        for (int dy = -r + 1; dy <= r - 1; dy++)
        {
            PlaceWall(cx - r, cy + dy);
            PlaceWall(cx + r, cy + dy);
        }
    }

    private int? LowestPawnId()
    {
        var snap = Host.LatestSnapshot;
        if (snap is null || snap.Dummies.Length == 0) return null;
        int best = snap.Dummies[0].EntityId;
        foreach (var d in snap.Dummies) if (d.EntityId < best) best = d.EntityId;
        return best;
    }

    private void DraftLowest()
    {
        if (LowestPawnId() is int id) Host.QueueCommand(new ToggleDraftCommand(id));
    }

    private void SpawnPawn()
    {
        Host.QueueCommand(new SpawnDummyCommand());
    }

    private void RemoveLowest()
    {
        if (LowestPawnId() is int id) Host.QueueCommand(new RemoveDummyCommand(id));
    }

    private void ChopRect(int x0, int y0, int x1, int y1)
    {
        Host.QueueCommand(new ChopTreesInRectCommand(new TilePos(x0, y0), new TilePos(x1, y1)));
    }

    private void RecordTreeCount(string label)
    {
        var snap = Host.LatestSnapshot;
        int trees = snap?.Trees.Length ?? -1;
        int wood = snap?.Wood.Length ?? -1;
        _events.Add($"trees[{label}]={trees} wood={wood}");
        Log($"{{\"event\":\"trees\",\"label\":\"{Json(label)}\",\"trees\":{trees},\"wood\":{wood}}}");
    }

    private void RecordCount(string label)
    {
        var snap = Host.LatestSnapshot;
        int n = snap?.Dummies.Length ?? -1;
        _events.Add($"count[{label}]={n}");
        Log($"{{\"event\":\"count\",\"label\":\"{Json(label)}\",\"count\":{n}}}");
    }

    private void MoveLowest(int x, int y, bool append)
    {
        if (LowestPawnId() is int id)
        {
            Host.QueueCommand(new IssueMoveOrderCommand(id, new TilePos(x, y), append));
        }
    }

    private void Finish(string reason)
    {
        if (_finished) return;
        _finished = true;
        CallDeferred(nameof(Screenshot));
        var snap = Host.LatestSnapshot;
        var w = Host.Watcher;
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"scenario\":\"").Append(Json(Scenario)).Append("\",");
        sb.Append("\"reason\":\"").Append(Json(reason)).Append("\",");
        sb.Append("\"elapsed\":").Append(_elapsed.ToString("0.000")).Append(',');
        sb.Append("\"finalTick\":").Append(snap?.Tick ?? 0).Append(',');
        sb.Append("\"stuckTotal\":").Append(w.StuckTotal).Append(',');
        sb.Append("\"brainDeadTotal\":").Append(w.BrainDeadTotal).Append(',');
        sb.Append("\"anomalies\":[");
        var recent = w.Recent;
        for (int i = 0; i < recent.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var a = recent[i];
            sb.Append("{\"tick\":").Append(a.Tick)
              .Append(",\"id\":").Append(a.EntityId)
              .Append(",\"kind\":\"").Append(a.Kind).Append('"')
              .Append(",\"detail\":\"").Append(Json(a.Detail)).Append("\"}");
        }
        sb.Append("],\"events\":[");
        for (int i = 0; i < _events.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append(Json(_events[i])).Append('"');
        }
        sb.Append("]}");
        File.WriteAllText(Path.Combine(OutputDir, "report.json"), sb.ToString());
        Log($"{{\"event\":\"finish\",\"reason\":\"{Json(reason)}\"}}");
        _log?.Flush();
        _log?.Dispose();
        _log = null;
        GD.Print($"[harness] done in {_elapsed:0.0}s — report at {OutputDir}");

        // Give the deferred screenshot one frame to fire before quitting.
        GetTree().CreateTimer(0.5).Timeout += () => GetTree().Quit(0);
    }

    private void Log(string line)
    {
        _log?.WriteLine(line);
    }

    private static string Json(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
