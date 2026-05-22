using Godot;
using StruggleGame.Game.Camera;
using StruggleGame.Game.Debug;
using StruggleGame.Game.Designation;
using StruggleGame.Game.Harness;
using StruggleGame.Game.Render;
using StruggleGame.Game.Selection;
using StruggleGame.Game.Tools;
using StruggleGame.Game.UI;
using StruggleGame.Sim;

namespace StruggleGame.Game;

public partial class Bootstrap : Node2D
{
    private SimHost? _host;
    private readonly ToolService _tools = new();
    private UI.MainMenuPanel? _menu;

    public override void _Ready()
    {
        _host = new SimHost();
        GD.Print(
            $"Struggle Game booted. Tile = {SimConstants.TileMeters}m, " +
            $"Tick = {SimConstants.TickHz}Hz, Map = {SimConstants.MapSize}x{SimConstants.MapSize}.");

        AddVoidBackground();

        var renderer = new WorldRenderer { Host = _host, Name = "WorldRenderer" };
        AddChild(renderer);

        var wallDesignator = new WallDesignator { Host = _host, Tools = _tools, Name = "WallDesignator" };
        AddChild(wallDesignator);

        var cancelDesignator = new CancelDesignator { Host = _host, Tools = _tools, Name = "CancelDesignator" };
        AddChild(cancelDesignator);

        var chopDesignator = new ChopDesignator { Host = _host, Tools = _tools, Name = "ChopDesignator" };
        AddChild(chopDesignator);

        var deconDesignator = new DeconDesignator { Host = _host, Tools = _tools, Name = "DeconDesignator" };
        AddChild(deconDesignator);

        var floorDeconDesignator = new FloorDeconDesignator { Host = _host, Tools = _tools, Name = "FloorDeconDesignator" };
        AddChild(floorDeconDesignator);

        var floorDesignator = new FloorDesignator { Host = _host, Tools = _tools, Name = "FloorDesignator" };
        AddChild(floorDesignator);

        var doorDesignator = new DoorDesignator { Host = _host, Tools = _tools, Name = "DoorDesignator" };
        AddChild(doorDesignator);

        var stockpileDesignator = new StockpileDesignator { Host = _host, Tools = _tools, Name = "StockpileDesignator" };
        AddChild(stockpileDesignator);

        var spawnDesignator = new SpawnPawnDesignator { Host = _host, Tools = _tools, Name = "SpawnPawnDesignator" };
        AddChild(spawnDesignator);

        var removeDesignator = new RemovePawnDesignator { Host = _host, Tools = _tools, Name = "RemovePawnDesignator" };
        AddChild(removeDesignator);

        var selector = new Selector { Host = _host, Tools = _tools, Name = "Selector" };
        AddChild(selector);

        float worldPx = SimConstants.MapSize * SimConstants.PixelsPerTile;
        var camera = new GameCamera
        {
            Name = "Camera",
            Position = new Vector2(worldPx * 0.5f, worldPx * 0.5f),
        };
        AddChild(camera);
        camera.MakeCurrent();

        var hud = new HudOverlay { Host = _host, Name = "Hud" };
        AddChild(hud);

        var toolbar = new Toolbar { Tools = _tools, Name = "Toolbar" };
        AddChild(toolbar);

        var stockpilePanel = new StockpilePanel { Host = _host, Tools = _tools, Name = "StockpilePanel" };
        AddChild(stockpilePanel);

        var itemInfoPanel = new ItemInfoPanel { Host = _host, Name = "ItemInfoPanel" };
        AddChild(itemInfoPanel);

        var treeInfoPanel = new TreeInfoPanel { Host = _host, Name = "TreeInfoPanel" };
        AddChild(treeInfoPanel);

        var wallInfoPanel = new WallInfoPanel { Host = _host, Name = "WallInfoPanel" };
        AddChild(wallInfoPanel);

        var doorInfoPanel = new DoorInfoPanel { Host = _host, Name = "DoorInfoPanel" };
        AddChild(doorInfoPanel);

        var blueprintInfoPanel = new BlueprintInfoPanel { Host = _host, Name = "BlueprintInfoPanel" };
        AddChild(blueprintInfoPanel);

        var debugBar = new DebugBar { Tools = _tools, Name = "DebugBar" };
        AddChild(debugBar);

        _menu = new MainMenuPanel { Host = _host, Name = "MainMenu" };
        AddChild(_menu);

        TryStartHarness();
    }

    private void TryStartHarness()
    {
        string? scenario = null;
        string? outDir = null;
        bool movieMode = false;
        foreach (var arg in OS.GetCmdlineArgs())
        {
            if (arg == "--harness") scenario ??= "default";
            else if (arg.StartsWith("--harness=")) scenario = arg.Substring("--harness=".Length);
            else if (arg.StartsWith("--harness-out=")) outDir = arg.Substring("--harness-out=".Length);
            else if (arg.StartsWith("--write-movie")) movieMode = true;
        }
        if (scenario is null || _host is null) return;

        var harness = new HarnessController
        {
            Host = _host,
            Scenario = scenario,
            OutputDir = outDir ?? string.Empty,
            MovieMode = movieMode,
            Name = "Harness",
        };
        AddChild(harness);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_host is null) return;
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;

        switch (k.Keycode)
        {
            case Key.Key1: _host.SetTickHz(60); _host.SetPaused(false); break;
            case Key.Key2: _host.SetTickHz(120); _host.SetPaused(false); break;
            case Key.Key3: _host.SetTickHz(180); _host.SetPaused(false); break;
            case Key.Key4: _host.SetTickHz(360); _host.SetPaused(false); break;
            case Key.R:
            {
                var pawnIds = _host.SelectedDummyIds;
                if (pawnIds.Length > 0)
                {
                    foreach (var pid in pawnIds)
                        _host.QueueCommand(new Sim.Commands.ToggleDraftCommand(pid));
                    GetViewport().SetInputAsHandled();
                }
                return;
            }
            case Key.F:
            {
                // Doors selected: toggle Forbidden across the whole
                // selection. Target = !(majority currently forbidden), so
                // a mixed selection flips toward "all forbid".
                var doorTiles = _host.SelectedDoorTiles;
                if (doorTiles.Length > 0)
                {
                    var dsnap = _host.LatestSnapshot;
                    if (dsnap is null) return;
                    var tileSet = new HashSet<Sim.Map.TilePos>(doorTiles);
                    int forbidCount = 0, totalCount = 0;
                    foreach (var d in dsnap.Doors)
                    {
                        if (!tileSet.Contains(d.Tile)) continue;
                        totalCount++;
                        if (d.Forbidden) forbidCount++;
                    }
                    bool doorTarget = !(forbidCount > totalCount - forbidCount);
                    foreach (var dt in doorTiles)
                        _host.QueueCommand(new Sim.Commands.SetDoorForbiddenCommand(dt, doorTarget));
                    GetViewport().SetInputAsHandled();
                    return;
                }
                var woodIds = _host.SelectedWoodIds;
                if (woodIds.Length == 0) return;
                var snap = _host.LatestSnapshot;
                if (snap is null) return;
                var idSet = new HashSet<int>(woodIds);
                int forbidden = 0, haulable = 0;
                foreach (var w in snap.Wood)
                {
                    if (!idSet.Contains(w.EntityId)) continue;
                    if (w.Forbidden) forbidden++; else haulable++;
                }
                bool target = !(forbidden > haulable);
                foreach (var w in snap.Wood)
                {
                    if (!idSet.Contains(w.EntityId)) continue;
                    if (w.Forbidden == target) continue;
                    _host.QueueCommand(new Sim.Commands.ForbidStackCommand(w.EntityId, target));
                }
                GetViewport().SetInputAsHandled();
                return;
            }
            case Key.B:
            {
                // Post chop jobs for every selected tree.
                var treeIds = _host.SelectedTreeIds;
                if (treeIds.Length == 0) return;
                var bsnap = _host.LatestSnapshot;
                if (bsnap is null) return;
                var idSet = new HashSet<int>(treeIds);
                foreach (var tr in bsnap.Trees)
                {
                    if (!idSet.Contains(tr.EntityId)) continue;
                    if (tr.HasJob) continue;
                    _host.QueueCommand(new Sim.Commands.ChopTreesInRectCommand(tr.Tile, tr.Tile));
                }
                GetViewport().SetInputAsHandled();
                return;
            }
            case Key.C:
            {
                // Cancel every selected blueprint / queued job.
                var bpTiles = _host.SelectedBlueprintTiles;
                if (bpTiles.Length == 0) return;
                foreach (var t in bpTiles)
                    _host.QueueCommand(new Sim.Commands.CancelJobAtTileCommand(t));
                _host.SelectedBlueprintTiles = Array.Empty<Sim.Map.TilePos>();
                GetViewport().SetInputAsHandled();
                return;
            }
            case Key.X:
            {
                // Decon selected walls (player-built only) + doors.
                bool any = false;
                foreach (var t in _host.SelectedWallTiles)
                {
                    if (!_host.IsPlayerWall(t)) continue;
                    _host.QueueCommand(new Sim.Commands.PostWallDeconCommand(t));
                    any = true;
                }
                foreach (var t in _host.SelectedDoorTiles)
                {
                    _host.QueueCommand(new Sim.Commands.PostDoorDeconCommand(t));
                    any = true;
                }
                if (any) GetViewport().SetInputAsHandled();
                return;
            }
            case Key.Space:
                _host.SetPaused(!_host.IsPaused);
                GD.Print(_host.IsPaused ? "Sim PAUSED" : "Sim RESUMED");
                GetViewport().SetInputAsHandled();
                return;
            case Key.Escape:
                _menu?.Open();
                GetViewport().SetInputAsHandled();
                return;
            default: return;
        }
        GD.Print($"Sim tick rate → {_host.TickHz}Hz");
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        _host?.Dispose();
        _host = null;
    }

    private void AddVoidBackground()
    {
        var layer = new CanvasLayer { Name = "VoidLayer", Layer = -100 };
        AddChild(layer);

        var shader = GD.Load<Shader>("res://Game/Render/void.gdshader");
        var rect = new ColorRect
        {
            Name = "Void",
            Material = new ShaderMaterial { Shader = shader },
            // Don't eat mouse input — the camera needs MMB pan + wheel zoom.
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(rect);
    }
}
