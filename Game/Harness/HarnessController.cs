using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

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
    public bool MovieMode { get; set; }

    private double _elapsed;
    private double _nextSampleAt;
    private double _nextScreenshotAt;
    private double _screenshotEverySec = 5.0;
    private float _screenshotScale = 1.0f;
    private double _warmupSec;
    private bool _warmedUp;
    private bool _manualSim;
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

        // Render captures at 1440p.
        if (!_headless)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
            DisplayServer.WindowSetPosition(new Vector2I(0, 0));
            DisplayServer.WindowSetSize(new Vector2I(2560, 1440));
        }
        GD.Print($"[harness] scenario={Scenario} headless={_headless} out={OutputDir}");
        Log($"{{\"event\":\"start\",\"scenario\":\"{Scenario}\",\"tickHz\":{Host.TickHz},\"headless\":{(_headless ? "true" : "false")}}}");

        BuildSchedule();

        if (_warmupSec > 0.0 || _manualSim)
        {
            // Pause the sim thread. In manual-sim mode we keep it paused
            // and drive ticks from _Process. In warmup-only mode we
            // unpause once warmup completes.
            Host.SetPaused(true);
        }
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
            case "pocketsand":
                // Pocket Sand demo: give the lowest pawn a rifle + spare
                // weapons, draft + select it, and screenshot the segmented card.
                _schedule.Add((0.3, h => h.SetCameraZoom(3.0f), "zoom"));
                _schedule.Add((0.5, h => h.GiveLowestSidearms(), "give weapons"));
                _schedule.Add((0.9, h => h.DraftLowest(), "draft + select"));
                _schedule.Add((2.0, h => h.Screenshot(), "shot"));
                _schedule.Add((3.0, h => h.Finish("pocketsand done"), "finish"));
                _screenshotEverySec = double.PositiveInfinity;
                break;
            case "fire":
                // Fire demo: light a cluster near center, let it grow, screenshot.
                _schedule.Add((0.3, h => h.SetCameraZoom(3.0f), "zoom"));
                _schedule.Add((0.5, h => h.IgniteCluster(), "ignite"));
                _schedule.Add((2.6, h => h.Screenshot(), "shot"));
                _schedule.Add((3.4, h => h.Finish("fire done"), "finish"));
                _screenshotEverySec = double.PositiveInfinity;
                break;
            case "fire-video":
                // Fire clip: zoom on a lit cluster, capture a PNG sequence.
                _screenshotEverySec = 1.0 / 30.0;
                _screenshotScale = 0.5f;
                _warmupSec = 1.0;
                _manualSim = true;
                _schedule.Add((0.1, h => h.SetCameraZoom(4.0f), "zoom"));
                _schedule.Add((0.3, h => h.IgniteCluster(), "ignite"));
                _schedule.Add((5.0, h => h.Finish("fire-video done"), "finish"));
                break;
            case "gear":
                // Gear pane demo: give the lowest pawn a loadout, select it,
                // open the Gear tab, and screenshot.
                _schedule.Add((0.3, h => h.SetCameraZoom(3.0f), "zoom"));
                _schedule.Add((0.5, h => h.GiveLowestSidearms(), "give gear"));
                _schedule.Add((1.0, h => h.OpenGearForLowest(), "select + open gear"));
                _schedule.Add((2.2, h => h.Screenshot(), "shot"));
                _schedule.Add((3.0, h => h.Finish("gear done"), "finish"));
                _screenshotEverySec = double.PositiveInfinity;
                break;
            case "tileinfo":
                // Selection-panel demo: build a wall, select it, screenshot the
                // (now colonist-pane-styled) info panel bottom-left.
                _schedule.Add((0.1, h => h.SetCameraZoom(3.0f), "zoom"));
                _schedule.Add((0.3, h => h.InstantWall(c, c), "wall"));
                _schedule.Add((0.6, h => h.SelectWallAt(c, c), "select wall"));
                _schedule.Add((2.0, h => h.Screenshot(), "shot: deconstruct"));
                _schedule.Add((2.3, h => h.QueueWallDeconAt(c, c), "queue decon"));
                _schedule.Add((3.5, h => h.Screenshot(), "shot: cancel"));
                _schedule.Add((4.5, h => h.Finish("tileinfo done"), "finish"));
                _screenshotEverySec = double.PositiveInfinity;
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
            case "doors-video":
                // Movie Maker mode (--write-movie) lets Godot capture every
                // rendered frame at a fixed delta; pair with manual-sim so
                // the sim ticks once per render frame and stays locked to
                // the recording. Falls back to PNG-sequence capture when
                // not in movie mode.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity; // disable own screenshots
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 0.5f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                _schedule.Add((0.5, h => h.PlaceWall(c - 1, c), "wall W"));
                _schedule.Add((0.7, h => h.PlaceWall(c + 1, c), "wall E"));
                _schedule.Add((5.0, h => h.PlaceDoor(c, c), "place door"));
                _schedule.Add((9.0, h => h.DraftLowest(), "draft lowest"));
                _schedule.Add((9.5, h => h.MoveLowest(c, c + 4, false), "march south through door"));
                _schedule.Add((14.0, h => h.MoveLowest(c, c - 4, false), "march back through"));
                _schedule.Add((19.0, h => h.Finish("doors-video complete"), "finish"));
                break;
            case "rooms-video":
                // Build a small enclosed box near center, then drop a door,
                // then a pawn walks through it. Renderer tints the interior
                // tiles once the wall ring closes; tint persists across the
                // door swing because doors count as room boundaries too.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 0.5f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                // 5x5 outer wall ring with a gap at south-center for the door.
                {
                    int x0 = c - 2, x1 = c + 2;
                    int y0 = c - 2, y1 = c + 2;
                    double at = 0.3;
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y0), $"wall top x={xc}"));
                        at += 0.1;
                    }
                    for (int x = x0; x <= x1; x++)
                    {
                        if (x == c) continue; // gap for the door
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y1), $"wall bot x={xc}"));
                        at += 0.1;
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((at, h => h.PlaceWall(x0, yc), $"wall L y={yc}"));
                        _schedule.Add((at + 0.05, h => h.PlaceWall(x1, yc), $"wall R y={yc}"));
                        at += 0.1;
                    }
                }
                _schedule.Add((16.0, h => h.PlaceDoor(c, c + 2), "place door in gap"));
                _schedule.Add((22.0, h => h.DraftLowest(), "draft lowest"));
                _schedule.Add((22.5, h => h.MoveLowest(c, c, false), "march into room"));
                _schedule.Add((28.0, h => h.MoveLowest(c, c + 6, false), "march back out"));
                _schedule.Add((34.0, h => h.Finish("rooms-video complete"), "finish"));
                break;
            case "lighting":
                // Builds a 7x7 room with a south door, lets auto-roof + the
                // build queue finish, then sits long enough for a couple of
                // post-build screenshots. Cranks the sim 8× so a real wall
                // ring + roof completes in seconds of wall-clock.
                _schedule.Add((0.5, h => h.Host.SetTickHz(SimConstants.TickHz * 8), "speed 8x"));
                _screenshotEverySec = 2.0;
                {
                    int x0 = c - 3, x1 = c + 3;
                    int y0 = c - 3, y1 = c + 3;
                    double at = 1.0;
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y0), $"wall top x={xc}"));
                        at += 0.05;
                    }
                    for (int x = x0; x <= x1; x++)
                    {
                        if (x == c) continue;
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y1), $"wall bot x={xc}"));
                        at += 0.05;
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((at, h => h.PlaceWall(x0, yc), $"wall L y={yc}"));
                        _schedule.Add((at + 0.025, h => h.PlaceWall(x1, yc), $"wall R y={yc}"));
                        at += 0.05;
                    }
                    _schedule.Add((at + 1.0, h => h.PlaceDoor(c, c + 3), "place door south"));
                }
                _schedule.Add((60.0, h => h.Finish("lighting complete"), "finish"));
                break;
            case "lamp":
                // 11x11 room. Spawn lots of builders so walls + roof finish
                // before the lamp drops, then center lamp shows inner ring
                // filling the interior with mid/outer bands spilling onto
                // grass outside the walls.
                _schedule.Add((0.1, h => h.Host.SetTickHz(SimConstants.TickHz * 8), "speed 8x"));
                _screenshotEverySec = 5.0;
                {
                    int x0 = c - 5, x1 = c + 5;
                    int y0 = c - 5, y1 = c + 5;
                    // Door first so the wall ring always has a walkable gap
                    // — pawns can never seal themselves inside.
                    _schedule.Add((0.5, h => h.PlaceDoor(c, c + 5), "place south door"));
                    double at = 2.0;
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y0), $"wall top x={xc}"));
                        at += 0.02;
                    }
                    for (int x = x0; x <= x1; x++)
                    {
                        if (x == c) continue;
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y1), $"wall bot x={xc}"));
                        at += 0.02;
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((at, h => h.PlaceWall(x0, yc), $"wall L y={yc}"));
                        _schedule.Add((at + 0.01, h => h.PlaceWall(x1, yc), $"wall R y={yc}"));
                        at += 0.02;
                    }
                    _schedule.Add((at + 15.0, h => h.PlaceLamp(c, c), "drop lamp center"));
                }
                _schedule.Add((45.0, h => h.Finish("lamp complete"), "finish"));
                break;
            case "lamp-bands":
                // 25x25 outer room = 23x23 interior. Lamp at center reaches
                // 19x19 (outer ring), leaving a ~2-tile fully-dark margin
                // around walls so all three falloff bands are visible:
                // bright core (15x15), mid ring (17x17), dim ring (19x19),
                // black margin out to walls. Door LAST (matches working
                // small-lamp pattern) — walls close ring immediately, auto-
                // roof posts blueprints on a real interior, roof builds
                // before lamp drops.
                _schedule.Add((0.1, h => h.Host.SetTickHz(SimConstants.TickHz * 16), "speed 16x"));
                for (int i = 0; i < 30; i++)
                    _schedule.Add((0.15 + i * 0.02, h => h.SpawnPawn(), "spawn pawn"));
                _screenshotEverySec = 5.0;
                {
                    int x0 = c - 12, x1 = c + 12;
                    int y0 = c - 12, y1 = c + 12;
                    double at = 2.0;
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y0), $"wall top x={xc}"));
                        at += 0.01;
                    }
                    for (int x = x0; x <= x1; x++)
                    {
                        if (x == c) continue;
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y1), $"wall bot x={xc}"));
                        at += 0.01;
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((at, h => h.PlaceWall(x0, yc), $"wall L y={yc}"));
                        _schedule.Add((at + 0.005, h => h.PlaceWall(x1, yc), $"wall R y={yc}"));
                        at += 0.01;
                    }
                    _schedule.Add((at + 15.0, h => h.PlaceDoor(c, c + 12), "place south door"));
                    _schedule.Add((at + 40.0, h => h.PlaceLamp(c, c), "drop lamp center"));
                }
                _schedule.Add((90.0, h => h.Finish("lamp-bands complete"), "finish"));
                break;
            case "rgb-wheel":
                // Three lamps (R/G/B) arranged in an equilateral triangle
                // so each lamp's outer ring overlaps the others' inner
                // discs. Per-channel max-blend produces secondaries at
                // the pairwise overlaps (R+G=yellow, G+B=cyan, R+B=
                // magenta) and a tertiary white core at the centroid.
                // Big 41x41 room so the bands have headroom; door last,
                // pawns spawn slowly so they don't crowd the demo.
                _schedule.Add((0.1, h => h.Host.SetTickHz(SimConstants.TickHz * 16), "speed 16x"));
                // 41x41 room = 2624 px; default zoom 1.0 only shows ~16
                // tiles. Drop to 0.32 so the whole demo + a margin fits
                // (≈50 tiles wide on a 1024 viewport).
                _schedule.Add((0.2, h => h.SetCameraZoom(0.32f), "zoom out"));
                for (int i = 0; i < 12; i++)
                    _schedule.Add((0.15 + i * 0.05, h => h.SpawnPawn(), "spawn pawn"));
                _screenshotEverySec = 5.0;
                {
                    int x0 = c - 20, x1 = c + 20;
                    int y0 = c - 20, y1 = c + 20;
                    double at = 2.0;
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y0), $"wall top x={xc}"));
                        at += 0.01;
                    }
                    for (int x = x0; x <= x1; x++)
                    {
                        if (x == c) continue;
                        int xc = x;
                        _schedule.Add((at, h => h.PlaceWall(xc, y1), $"wall bot x={xc}"));
                        at += 0.01;
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((at, h => h.PlaceWall(x0, yc), $"wall L y={yc}"));
                        _schedule.Add((at + 0.005, h => h.PlaceWall(x1, yc), $"wall R y={yc}"));
                        at += 0.01;
                    }
                    _schedule.Add((at + 15.0, h => h.PlaceDoor(c, c + 20), "south door"));
                    // Equilateral triangle around centroid (c, c). Side
                    // length 11 tiles puts each lamp's center ~6 tiles
                    // from the next, so the 7.5-tile inner discs overlap.
                    int rx = 5;  // half side
                    int ry = 3;  // ~side * sin(60) / 2 rounded
                    var red   = new LightColor(255,   0,   0);
                    var green = new LightColor(  0, 255,   0);
                    var blue  = new LightColor(  0,   0, 255);
                    _schedule.Add((at + 40.0, h => h.PlaceLamp(c,      c - ry, red),   "red lamp"));
                    _schedule.Add((at + 40.1, h => h.PlaceLamp(c - rx, c + ry, green), "green lamp"));
                    _schedule.Add((at + 40.2, h => h.PlaceLamp(c + rx, c + ry, blue),  "blue lamp"));
                }
                _schedule.Add((120.0, h => h.Finish("rgb-wheel complete"), "finish"));
                break;
            case "fonts":
                _schedule.Add((0.3, h => h.FontShowcase(), "fonts"));
                _schedule.Add((1.5, h => h.Screenshot(), "shot"));
                _schedule.Add((2.5, h => h.Finish("fonts done"), "finish"));
                _screenshotEverySec = double.PositiveInfinity;
                break;
            case "healthtab":
                // Wound the lowest colonist with a demo mix, select it, open
                // the health tab, then screenshot the status icons.
                _schedule.Add((0.1, h => h.SetCameraZoom(4.0f), "zoom"));
                _schedule.Add((0.5, h => h.HealthDemo(), "wound + open health"));
                _schedule.Add((2.5, h => h.Screenshot(), "shot"));
                _schedule.Add((3.5, h => h.Finish("healthtab done"), "finish"));
                _screenshotEverySec = double.PositiveInfinity;
                break;
            case "lit-room-night":
                // Visual harness: 5x5 outer-wall room with door on south,
                // roof over interior, lit lamp at center, world time set
                // to 22:00 so the sun is fully down and lamp halo dominates.
                {
                    int half = 2;
                    int x0 = c - half, x1 = c + half;
                    int y0 = c - half, y1 = c + half;
                    _schedule.Add((0.05, h => h.SetWorldTimeAt(22, 0), "night 22:00"));
                    _schedule.Add((0.1, h => h.SetCameraZoom(3.0f), "zoom"));
                    // Outer wall ring
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((0.2, h => h.InstantWall(xc, y0), $"wall N {xc}"));
                        if (xc != c) _schedule.Add((0.2, h => h.InstantWall(xc, y1), $"wall S {xc}"));
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((0.2, h => h.InstantWall(x0, yc), $"wall W {yc}"));
                        _schedule.Add((0.2, h => h.InstantWall(x1, yc), $"wall E {yc}"));
                    }
                    _schedule.Add((0.3, h => h.InstantDoor(c, y1), "south door"));
                    _schedule.Add((0.4, h => h.InstantRoofRect(x0 + 1, y0 + 1, x1 - 1, y1 - 1), "roof interior"));
                    _schedule.Add((0.5, h => h.InstantLamp(c, c), "lamp center"));
                    _schedule.Add((2.0, h => h.Screenshot(), "shot"));
                    _schedule.Add((3.0, h => h.Finish("lit-room-night done"), "finish"));
                    _screenshotEverySec = double.PositiveInfinity;
                }
                break;
            case "lit-room-day":
                // Same as lit-room-night but world time set to noon so the
                // sun dominates and the lamp halo is washed out.
                {
                    int half = 2;
                    int x0 = c - half, x1 = c + half;
                    int y0 = c - half, y1 = c + half;
                    _schedule.Add((0.05, h => h.SetWorldTimeAt(12, 0), "noon 12:00"));
                    _schedule.Add((0.1, h => h.SetCameraZoom(5.0f), "zoom"));
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((0.2, h => h.InstantWall(xc, y0), $"wall N {xc}"));
                        if (xc != c) _schedule.Add((0.2, h => h.InstantWall(xc, y1), $"wall S {xc}"));
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((0.2, h => h.InstantWall(x0, yc), $"wall W {yc}"));
                        _schedule.Add((0.2, h => h.InstantWall(x1, yc), $"wall E {yc}"));
                    }
                    _schedule.Add((0.3, h => h.InstantDoor(c, y1), "south door"));
                    _schedule.Add((0.4, h => h.InstantRoofRect(x0 + 1, y0 + 1, x1 - 1, y1 - 1), "roof interior"));
                    _schedule.Add((0.5, h => h.InstantLamp(c, c), "lamp center"));
                    _schedule.Add((2.0, h => h.Screenshot(), "shot"));
                    _schedule.Add((3.0, h => h.Finish("lit-room-day done"), "finish"));
                    _screenshotEverySec = double.PositiveInfinity;
                }
                break;
            case "ur-board":
                // 2 players + 8 spectators at an Ur board inside a lit
                // room. World time pinned to 18:00 (Recreation slot) so
                // pawns idle-seek the board even at full need.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 0.5f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                {
                    // 9x9 outer wall, 7x7 interior — fits radius-3 spectator
                    // halo around a center board. Door south, lamp NW
                    // interior corner.
                    int half = 4;
                    int x0 = c - half, x1 = c + half;
                    int y0 = c - half, y1 = c + half;
                    _schedule.Add((0.05, h => h.SetWorldTimeAt(18, 0), "recreation 18:00"));
                    _schedule.Add((0.1, h => h.SetCameraZoom(2.0f), "zoom"));
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((0.2, h => h.InstantWall(xc, y0), $"wall N {xc}"));
                        if (xc != c) _schedule.Add((0.2, h => h.InstantWall(xc, y1), $"wall S {xc}"));
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((0.2, h => h.InstantWall(x0, yc), $"wall W {yc}"));
                        _schedule.Add((0.2, h => h.InstantWall(x1, yc), $"wall E {yc}"));
                    }
                    _schedule.Add((0.3, h => h.InstantDoor(c, y1), "south door"));
                    _schedule.Add((0.4, h => h.InstantRoofRect(x0 + 1, y0 + 1, x1 - 1, y1 - 1), "roof interior"));
                    int lampX = x0 + 1, lampY = y0 + 1;
                    _schedule.Add((0.5, h => h.InstantLamp(lampX, lampY), "lamp NW corner"));
                    _schedule.Add((0.6, h => h.InstantUrBoard(c, c), "ur board center"));
                    // Spawn pawns INSIDE the room near the board so the
                    // walk to a seat is short — random map-wide spawn was
                    // dropping them too far to converge in scenario time.
                    int[] spawnDx = { -1, 1, 0, 0, -2, 2, 0, 0, -1, 1 };
                    int[] spawnDy = { 0, 0, -1, 1, 0, 0, -2, 2, -1, 1 };
                    for (int i = 0; i < 10; i++)
                    {
                        int sx = c + spawnDx[i];
                        int sy = c + spawnDy[i];
                        _schedule.Add((1.0 + i * 0.1, h => h.SpawnPawnAt(sx, sy), $"spawn pawn {i}"));
                    }
                    _schedule.Add((2.5, h => h.DrainAllRecreation(), "drain all rec to 0"));
                    for (int t = 8; t < 60; t += 5)
                    {
                        _schedule.Add(((double)t, h => h.DrainAllRecreation(), $"drain all rec @ t={t}"));
                    }
                    _schedule.Add((60.0, h => h.Finish("ur-board complete"), "finish"));
                }
                break;
            case "stove-cook":
                // Stove cook demo: lit room with a stove, a pile of carrots,
                // a single pawn. Pawn hauls 5 carrots → cooks meal → drops
                // meal on standing tile. Loops forever.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 0.5f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                {
                    int half = 5;
                    int x0 = c - half, x1 = c + half;
                    int y0 = c - half, y1 = c + half;
                    _schedule.Add((0.05, h => h.SetWorldTimeAt(10, 0), "work 10:00"));
                    _schedule.Add((0.1, h => h.SetCameraZoom(2.0f), "zoom"));
                    for (int x = x0; x <= x1; x++)
                    {
                        int xc = x;
                        _schedule.Add((0.2, h => h.InstantWall(xc, y0), $"wall N {xc}"));
                        if (xc != c) _schedule.Add((0.2, h => h.InstantWall(xc, y1), $"wall S {xc}"));
                    }
                    for (int y = y0 + 1; y <= y1 - 1; y++)
                    {
                        int yc = y;
                        _schedule.Add((0.2, h => h.InstantWall(x0, yc), $"wall W {yc}"));
                        _schedule.Add((0.2, h => h.InstantWall(x1, yc), $"wall E {yc}"));
                    }
                    _schedule.Add((0.3, h => h.InstantDoor(c, y1), "south door"));
                    _schedule.Add((0.4, h => h.InstantRoofRect(x0 + 1, y0 + 1, x1 - 1, y1 - 1), "roof interior"));
                    int lampX = x0 + 1, lampY = y0 + 1;
                    _schedule.Add((0.5, h => h.InstantLamp(lampX, lampY), "lamp NW"));
                    // Stove: origin = (c, c-1). Body runs c-1..c+1 east-west,
                    // standing tile north of center: (c, c-2). Use North orientation.
                    int sox = c, soy = c - 1;
                    _schedule.Add((0.6, h => h.InstantStove(sox, soy, StoveOrientation.North), "stove"));
                    // Carrot pile dropped at SE corner.
                    int carrotX = c + 2, carrotY = c + 2;
                    _schedule.Add((0.7, h => h.DropCarrotsAt(carrotX, carrotY, 25), "carrot pile"));
                    // Add a Forever bill so the pawn never stops cooking.
                    _schedule.Add((0.8, h => h.AddBillToFirstStove(RecipeId.CookSimpleMeal, BillRepeatMode.Forever, 0, 0), "bill"));
                    // Spawn the pawn near the door.
                    int spawnX = c, spawnY = c + 2;
                    _schedule.Add((1.0, h => h.SpawnPawnAt(spawnX, spawnY), "spawn cook"));
                    _schedule.Add((60.0, h => h.Finish("stove-cook complete"), "finish"));
                }
                break;
            case "wall-grid":
                // Visual harness: lay out all 16 neighbor-mask wall
                // variants in a 4x4 grid of clusters. Each cluster is
                // a 3x3 footprint: center wall + up to 4 neighbors
                // present per the cluster's mask bits (N=8 E=4 S=2 W=1).
                // Bits run 0000..1111 left-to-right top-to-bottom.
                {
                    int spacing = 4;
                    int originX = c - (4 * spacing) / 2;
                    int originY = c - (4 * spacing) / 2;
                    _schedule.Add((0.1, h => h.SetCameraZoom(1.5f), "zoom in"));
                    for (int mask = 0; mask < 16; mask++)
                    {
                        int col = mask % 4;
                        int row = mask / 4;
                        int cx = originX + col * spacing + 1;
                        int cy = originY + row * spacing + 1;
                        int m = mask;
                        _schedule.Add((0.5, h => h.InstantWall(cx, cy), $"center {m:x}"));
                        if ((m & 8) != 0) _schedule.Add((0.6, h => h.InstantWall(cx, cy - 1), $"N {m:x}"));
                        if ((m & 4) != 0) _schedule.Add((0.6, h => h.InstantWall(cx + 1, cy), $"E {m:x}"));
                        if ((m & 2) != 0) _schedule.Add((0.6, h => h.InstantWall(cx, cy + 1), $"S {m:x}"));
                        if ((m & 1) != 0) _schedule.Add((0.6, h => h.InstantWall(cx - 1, cy), $"W {m:x}"));
                    }
                    _schedule.Add((2.0, h => h.Screenshot(), "shot"));
                    _schedule.Add((3.0, h => h.Finish("wall-grid complete"), "finish"));
                    _screenshotEverySec = double.PositiveInfinity;
                }
                break;
            case "gunfight":
                // Ranged demo: an M16-armed, drafted shooter full-autos a
                // target ~20 tiles away. Manual-sim + 60fps PNG capture so it
                // encodes to a smooth clip. Camera zoomed out to frame both.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 1.0f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                // Clear the 3 default wanderers so only the duel is in frame.
                _schedule.Add((0.1, h => h.RemoveLowest(), "remove default 1"));
                _schedule.Add((0.15, h => h.RemoveLowest(), "remove default 2"));
                _schedule.Add((0.2, h => h.RemoveLowest(), "remove default 3"));
                _schedule.Add((0.3, h => h.SetCameraZoom(2.4f), "zoom in"));
                _schedule.Add((0.6, h => h.SetupGunfight(c - 3, c, c + 3, c), "spawn shooter + target, open fire"));
                _schedule.Add((5.0, h => h.Finish("gunfight complete"), "finish"));
                break;
            case "enemy":
                // Enemy AI demo: a drafted, rifle-armed defender (orange) holds
                // center; a hostile (red) spawns to the east, hunts the
                // defender, closes to range + opens fire, and falls back when
                // its blood drops. Manual-sim + 60fps capture for a smooth clip.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 1.0f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                _schedule.Add((0.1, h => h.RemoveLowest(), "remove default 1"));
                _schedule.Add((0.15, h => h.RemoveLowest(), "remove default 2"));
                _schedule.Add((0.2, h => h.RemoveLowest(), "remove default 3"));
                _schedule.Add((0.3, h => h.SetCameraZoom(0.9f), "zoom out to frame both"));
                _schedule.Add((0.6, h => h.SetupEnemyDemo(c - 6, c, c + 9, c), "spawn defender + hunting enemy"));
                _schedule.Add((14.0, h => h.Finish("enemy complete"), "finish"));
                break;
            case "enemy-wall":
                // Same as "enemy" but a tall wall sits between the two, so the
                // hostile must path AROUND it (long protected approach) and
                // only takes fire once it rounds the end — long enough to get
                // chewed up gradually and flee instead of getting deleted.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 1.0f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                _schedule.Add((0.1, h => h.RemoveLowest(), "remove default 1"));
                _schedule.Add((0.15, h => h.RemoveLowest(), "remove default 2"));
                _schedule.Add((0.2, h => h.RemoveLowest(), "remove default 3"));
                _schedule.Add((0.3, h => h.SetCameraZoom(0.8f), "zoom out"));
                // 13-tile vertical wall at x=c between defender (west) + enemy (east).
                for (int wy = c - 6; wy <= c + 6; wy++)
                {
                    int y = wy;
                    _schedule.Add((0.4, h => h.InstantWall(c, y), $"wall y={y}"));
                }
                _schedule.Add((0.6, h => h.SetupEnemyDemo(c - 9, c, c + 9, c), "spawn defender + hunting enemy"));
                _schedule.Add((24.0, h => h.Finish("enemy-wall complete"), "finish"));
                break;
            case "enemy-cover":
                // Cover-seeking showcase: a SANDBAG line between defender + the
                // hostile. The enemy scores a cell tucked behind a sandbag
                // (sandbag toward the threat) as low-exposure, posts up there
                // CROUCHED, and fires over the bags at the defender — clean
                // cover, no wall-peek lean.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 1.0f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                _schedule.Add((0.1, h => h.RemoveLowest(), "remove default 1"));
                _schedule.Add((0.15, h => h.RemoveLowest(), "remove default 2"));
                _schedule.Add((0.2, h => h.RemoveLowest(), "remove default 3"));
                _schedule.Add((0.3, h => h.SetCameraZoom(0.9f), "zoom out"));
                // 7-tile vertical sandbag line at x=c between defender + enemy.
                for (int sy = c - 3; sy <= c + 3; sy++)
                {
                    int y = sy;
                    _schedule.Add((0.4, h => h.InstantSandbag(c, y), $"sandbag y={y}"));
                }
                _schedule.Add((0.6, h => h.SetupEnemyDemo(c - 9, c, c + 9, c), "spawn defender + hunting enemy"));
                _schedule.Add((24.0, h => h.Finish("enemy-cover complete"), "finish"));
                break;
            case "enemy-corner":
                // Toward-target lean showcase: a vertical wall with the
                // defender out past its NORTH end (north-west). The enemy
                // tucks on the east face near the top + peeks NORTH around the
                // tip — which now points TOWARD the defender, not away. Reads
                // as "edge out toward the enemy" instead of the old jank.
                if (MovieMode)
                {
                    _screenshotEverySec = double.PositiveInfinity;
                }
                else
                {
                    _screenshotEverySec = 1.0 / 60.0;
                    _screenshotScale = 1.0f;
                }
                _warmupSec = 2.0;
                _manualSim = true;
                _schedule.Add((0.1, h => h.RemoveLowest(), "remove default 1"));
                _schedule.Add((0.15, h => h.RemoveLowest(), "remove default 2"));
                _schedule.Add((0.2, h => h.RemoveLowest(), "remove default 3"));
                _schedule.Add((0.3, h => h.SetCameraZoom(0.85f), "zoom out"));
                // Vertical wall, north end at (c, c-2).
                for (int wy = c - 2; wy <= c + 5; wy++)
                {
                    int y = wy;
                    _schedule.Add((0.4, h => h.InstantWall(c, y), $"wall y={y}"));
                }
                // Defender NW of the wall's north tip; enemy to the east.
                _schedule.Add((0.6, h => h.SetupEnemyDemo(c - 4, c - 4, c + 8, c), "spawn defender + hunting enemy"));
                _schedule.Add((22.0, h => h.Finish("enemy-corner complete"), "finish"));
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

        if (!_warmedUp)
        {
            if (_elapsed < _warmupSec) return;
            _warmedUp = true;
            if (!_manualSim) Host.SetPaused(false);
            _elapsed = 0.0; // restart clock so schedule entries are relative to t=0 after warmup
        }

        // Manual-sim mode: step the sim exactly once per render frame.
        // This locks sim-time to render-time so the video captures every
        // tick with no drift, regardless of how fast Godot is running.
        if (_manualSim)
        {
            Host.StepManual(SimConstants.TickSeconds);
        }

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
            _nextScreenshotAt = _elapsed + _screenshotEverySec;
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
            else if (d.Job == "Standing") idle++;
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
        sb.Append(",\"wood\":").Append(snap.ItemPiles.Length);
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
        if (_screenshotScale > 0f && _screenshotScale < 1.0f)
        {
            int w = Math.Max(1, (int)(img.GetWidth() * _screenshotScale));
            int h = Math.Max(1, (int)(img.GetHeight() * _screenshotScale));
            img.Resize(w, h, Image.Interpolation.Bilinear);
        }
        var path = Path.Combine(OutputDir, $"shot_{_shotIndex:D5}_t{(int)_elapsed:D3}s.png");
        img.SavePng(path);
        _shotIndex++;
        Log($"{{\"event\":\"screenshot\",\"t\":{_elapsed:0.000},\"path\":\"{Json(path)}\"}}");
    }

    private void PlaceWall(int x, int y)
    {
        Host.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(x, y)));
    }

    private void InstantWall(int x, int y)
    {
        Host.QueueCommand(new InstantPlaceWallCommand(new TilePos(x, y)));
    }

    private void InstantSandbag(int x, int y)
    {
        Host.QueueCommand(new InstantPlaceSandbagCommand(new TilePos(x, y)));
    }

    private void InstantDoor(int x, int y)
    {
        Host.QueueCommand(new InstantPlaceDoorCommand(new TilePos(x, y)));
    }

    private void InstantLamp(int x, int y)
    {
        Host.QueueCommand(new InstantPlaceLampCommand(new TilePos(x, y)));
    }

    private void InstantLamp(int x, int y, LightColor color)
    {
        Host.QueueCommand(new InstantPlaceLampCommand(new TilePos(x, y), color));
    }

    private void InstantRoofRect(int x0, int y0, int x1, int y1)
    {
        Host.QueueCommand(new InstantPaintRoofRectCommand(new TilePos(x0, y0), new TilePos(x1, y1)));
    }

    private void InstantUrBoard(int x, int y)
    {
        Host.QueueCommand(new InstantPlaceUrBoardCommand(new TilePos(x, y)));
    }

    private void SpawnPawnAt(int x, int y)
    {
        Host.QueueCommand(new SpawnDummyAtCommand(new TilePos(x, y)));
    }

    private void SetupGunfight(int sx, int sy, int tx, int ty)
    {
        Host.QueueCommand(new SetupGunfightCommand(new TilePos(sx, sy), new TilePos(tx, ty)));
    }

    private void SetupEnemyDemo(int dx, int dy, int ex, int ey)
    {
        Host.QueueCommand(new SetupEnemyDemoCommand(new TilePos(dx, dy), new TilePos(ex, ey)));
    }

    private void DrainAllRecreation()
    {
        Host.QueueCommand(new SetAllRecreationLevelCommand(0f));
    }

    private void InstantStove(int x, int y, StoveOrientation o)
    {
        Host.QueueCommand(new InstantPlaceStoveCommand(new TilePos(x, y), o));
    }

    private void DropCarrotsAt(int x, int y, int count)
    {
        Host.QueueCommand(new SpawnItemPileCommand(new TilePos(x, y), StruggleGame.Sim.Items.ItemCatalog.Carrot.FullPath, count));
    }

    private void AddBillToFirstStove(RecipeId recipe, BillRepeatMode mode, int target, int remaining)
    {
        Host.QueueCommand(new AddBillToFirstStoveCommand(recipe, mode, target, remaining));
    }

    private void SetWorldTimeAt(int hour, int minute)
    {
        Host.QueueCommand(new SetWorldTimeCommand(hour * 3600.0 + minute * 60.0));
    }

    private void PlaceDoor(int x, int y)
    {
        Host.QueueCommand(new PlaceDoorBlueprintCommand(new TilePos(x, y)));
    }

    private void PlaceLamp(int x, int y)
    {
        Host.QueueCommand(new PlaceLampBlueprintCommand(new TilePos(x, y)));
    }

    private void PlaceLamp(int x, int y, LightColor color)
    {
        Host.QueueCommand(new PlaceLampBlueprintCommand(new TilePos(x, y), color));
    }

    // Drop the camera zoom to fit a bigger demo on screen. Walks the
    // scene tree to find the GameCamera; no-op if it's not present
    // (e.g. headless boot variants).
    private void SetCameraZoom(float zoom)
    {
        var root = GetTree().Root;
        var cam = FindCamera(root);
        if (cam is null) return;
        if (cam is StruggleGame.Game.Camera.GameCamera gc) gc.ForceZoom(zoom);
        else cam.Zoom = new Vector2(zoom, zoom);
    }

    private static Camera2D? FindCamera(Node n)
    {
        if (n is Camera2D c) return c;
        foreach (var child in n.GetChildren())
        {
            var hit = FindCamera(child);
            if (hit is not null) return hit;
        }
        return null;
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
        if (LowestPawnId() is int id)
        {
            Host.QueueCommand(new ToggleDraftCommand(id));
            Host.SelectedDummyId = id; // select so the draft action bar shows
        }
    }

    private void GiveLowestSidearms()
    {
        if (LowestPawnId() is int id) Host.QueueCommand(new DebugGiveSidearmsCommand(id));
    }

    private void SelectWallAt(int x, int y) => Host.SelectedWallTiles = new[] { new TilePos(x, y) };
    private void QueueWallDeconAt(int x, int y) => Host.QueueCommand(new PostWallDeconCommand(new TilePos(x, y)));

    private void FontShowcase()
    {
        var layer = new CanvasLayer { Layer = 98 };
        AddChild(layer);

        var panel = new Panel { CustomMinimumSize = new Vector2(1480, 980) };
        panel.AddThemeStyleboxOverride("panel", StruggleGame.Game.UI.UiTheme.PanelBox(16, 0));
        panel.Size = new Vector2(1480, 980);
        var vp = GetViewport().GetVisibleRect().Size;
        panel.Position = new Vector2((vp.X - 1480) * 0.5f, (vp.Y - 980) * 0.5f);
        layer.AddChild(panel);

        var vb = new VBoxContainer { Position = new Vector2(48, 36) };
        vb.AddThemeConstantOverride("separation", 14);
        panel.AddChild(vb);

        string sample = "Colonist #7  Health 96%  Pain 12%  0123456789";
        var fonts = new[]
        {
            "Quicksand", "Comfortaa", "Jost", "Outfit", "Orbitron", "VT323", "ChakraPetch",
            "Rajdhani", "Exo2", "SpaceGrotesk", "Iceland", "Tomorrow", "Syne", "Sora",
        };
        foreach (var name in fonts)
        {
            var ff = new FontFile();
            var bytes = Godot.FileAccess.GetFileAsBytes($"res://assets/fonts/{name}.ttf");
            GD.Print($"[fonts] {name} bytes={bytes.Length}");
            ff.Data = bytes;
            var line = new Label { Text = $"{name}   {sample}" };
            line.AddThemeFontOverride("font", ff);
            line.AddThemeFontSizeOverride("font_size", 28);
            line.AddThemeColorOverride("font_color", new Color(0.93f, 0.95f, 1f));
            line.AddThemeConstantOverride("outline_size", 3);
            line.AddThemeColorOverride("font_outline_color", new Color(0.03f, 0.03f, 0.09f, 0.9f));
            vb.AddChild(line);
        }
    }

    private void HealthDemo()
    {
        if (LowestPawnId() is not int id) return;
        Host.QueueCommand(new DebugHealthDemoCommand(id));
        Host.SelectedDummyId = id; // opens the pawn card so the health tab anchors above it
        if (GetTree().Root.FindChild("HealthTabPanel", true, false) is StruggleGame.Game.UI.HealthTabPanel panel)
            panel.OpenFor(id);
    }

    private void IgniteCluster()
    {
        int c = SimConstants.MapSize / 2;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                Host.QueueCommand(new IgniteTileCommand(new TilePos(c + dx, c + dy), SimConstants.FireBaseFuelSec));
    }

    private void OpenGearForLowest()
    {
        if (LowestPawnId() is not int id) return;
        Host.SelectedDummyId = id; // opens the pawn card so the gear pane anchors above it
        if (GetTree().Root.FindChild("GearTabPanel", true, false) is StruggleGame.Game.UI.GearTabPanel panel)
            panel.OpenFor(id);
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
        int wood = snap?.ItemPiles.Length ?? -1;
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
