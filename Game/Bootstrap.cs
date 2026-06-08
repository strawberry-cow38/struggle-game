using Godot;
using StruggleGame.Game.Audio;
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
    private WorldRenderer? _renderer;

    public override void _Ready()
    {
        // Default is true in Godot 4 but set explicit so a high polling-
        // rate mouse stays collated to one motion event per frame.
        Input.UseAccumulatedInput = true;

        // Force VSync on at runtime. The project.godot vsync_mode wasn't
        // actually capping the framerate (seen running ~720fps), so set it
        // explicitly on the window — caps to refresh, frees the CPU core the
        // uncapped renderer was burning, and kills the move-order-spam jitter.
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Enabled);

        // Fresh world every launch. Harness still gets the deterministic
        // SimHost() default via --harness wiring elsewhere.
        _host = new SimHost(System.Environment.TickCount);
        GD.Print(
            $"Struggle Game booted. Tile = {SimConstants.TileMeters}m, " +
            $"Tick = {SimConstants.TickHz}Hz, Map = {SimConstants.MapSize}x{SimConstants.MapSize}.");

        AddVoidBackground();

        var renderer = new WorldRenderer { Host = _host, Name = "WorldRenderer" };
        _renderer = renderer;
        AddChild(renderer);

        var wallDesignator = new WallDesignator { Host = _host, Tools = _tools, Name = "WallDesignator" };
        AddChild(wallDesignator);

        var cancelDesignator = new CancelDesignator { Host = _host, Tools = _tools, Name = "CancelDesignator" };
        AddChild(cancelDesignator);

        var chopDesignator = new ChopDesignator { Host = _host, Tools = _tools, Name = "ChopDesignator" };
        AddChild(chopDesignator);

        var cutPlantsDesignator = new CutPlantsDesignator { Host = _host, Tools = _tools, Name = "CutPlantsDesignator" };
        AddChild(cutPlantsDesignator);

        var harvestDesignator = new HarvestDesignator { Host = _host, Tools = _tools, Name = "HarvestDesignator" };
        AddChild(harvestDesignator);

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

        var growZoneDesignator = new GrowZoneDesignator { Host = _host, Tools = _tools, Name = "GrowZoneDesignator" };
        AddChild(growZoneDesignator);

        var roofDesignator = new RoofDesignator { Host = _host, Tools = _tools, Name = "RoofDesignator" };
        AddChild(roofDesignator);

        var lampDesignator = new LampDesignator { Host = _host, Tools = _tools, Name = "LampDesignator" };
        AddChild(lampDesignator);

        var bedDesignator = new BedDesignator { Host = _host, Tools = _tools, Name = "BedDesignator" };
        AddChild(bedDesignator);

        var urBoardDesignator = new UrBoardDesignator { Host = _host, Tools = _tools, Name = "UrBoardDesignator" };
        AddChild(urBoardDesignator);

        var stoveDesignator = new StoveDesignator { Host = _host, Tools = _tools, Name = "StoveDesignator" };
        AddChild(stoveDesignator);

        var sandbagDesignator = new SandbagDesignator { Host = _host, Tools = _tools, Name = "SandbagDesignator" };
        AddChild(sandbagDesignator);

        var spawnDesignator = new SpawnPawnDesignator { Host = _host, Tools = _tools, Name = "SpawnPawnDesignator" };
        AddChild(spawnDesignator);

        var removeDesignator = new RemovePawnDesignator { Host = _host, Tools = _tools, Name = "RemovePawnDesignator" };
        AddChild(removeDesignator);

        var debugSpawnItemDesignator = new DebugSpawnItemDesignator { Host = _host, Tools = _tools, Name = "DebugSpawnItemDesignator" };
        AddChild(debugSpawnItemDesignator);

        var debugAddInjuryDesignator = new DebugAddInjuryDesignator { Host = _host, Tools = _tools, Name = "DebugAddInjuryDesignator" };
        AddChild(debugAddInjuryDesignator);

        var forceTargetDesignator = new ForceTargetDesignator { Host = _host, Tools = _tools, Name = "ForceTargetDesignator" };
        AddChild(forceTargetDesignator);

        var selector = new Selector { Host = _host, Tools = _tools, Name = "Selector" };
        AddChild(selector);

        var pickupDialog = new PickupQuantityDialog { Host = _host, Name = "PickupQuantityDialog" };
        AddChild(pickupDialog);
        selector.PickupDialog = pickupDialog;

        float worldPx = SimConstants.MapSize * SimConstants.PixelsPerTile;
        var camera = new GameCamera
        {
            Name = "Camera",
            Position = new Vector2(worldPx * 0.5f, worldPx * 0.5f),
        };
        AddChild(camera);
        camera.MakeCurrent();
        selector.Camera = camera;
        // Resolve a followed pawn's live world-pixel position from the snapshot.
        camera.ResolveWorldPx = id =>
        {
            var snap = _host?.LatestSnapshot;
            if (snap is null) return null;
            foreach (var d in snap.Dummies)
                if (d.EntityId == id) return new Vector2(d.X, d.Y) * SimConstants.PixelsPerTile;
            return null;
        };

        var hud = new HudOverlay { Host = _host, Name = "Hud" };
        AddChild(hud);

        var workTab = new WorkTab { Host = _host, Name = "WorkTab" };
        AddChild(workTab);

        var scheduleTab = new ScheduleTab { Host = _host, Name = "ScheduleTab" };
        AddChild(scheduleTab);

        var toolbar = new Toolbar { Tools = _tools, WorkTab = workTab, ScheduleTab = scheduleTab, Name = "Toolbar" };
        AddChild(toolbar);

        var stockpilePanel = new StockpilePanel { Host = _host, Tools = _tools, Name = "StockpilePanel" };
        AddChild(stockpilePanel);

        var growZonePanel = new GrowZonePanel { Host = _host, Tools = _tools, Name = "GrowZonePanel" };
        AddChild(growZonePanel);

        var itemInfoPanel = new ItemInfoPanel { Host = _host, Name = "ItemInfoPanel" };
        AddChild(itemInfoPanel);

        var treeInfoPanel = new TreeInfoPanel { Host = _host, Name = "TreeInfoPanel" };
        AddChild(treeInfoPanel);

        var cropInfoPanel = new CropInfoPanel { Host = _host, Name = "CropInfoPanel" };
        AddChild(cropInfoPanel);

        var wallInfoPanel = new WallInfoPanel { Host = _host, Name = "WallInfoPanel" };
        AddChild(wallInfoPanel);

        var doorInfoPanel = new DoorInfoPanel { Host = _host, Name = "DoorInfoPanel" };
        AddChild(doorInfoPanel);

        var lampInfoPanel = new LampInfoPanel { Host = _host, Name = "LampInfoPanel" };
        AddChild(lampInfoPanel);

        var bedInfoPanel = new BedInfoPanel { Host = _host, Name = "BedInfoPanel" };
        AddChild(bedInfoPanel);

        var urBoardInfoPanel = new UrBoardInfoPanel { Host = _host, Name = "UrBoardInfoPanel" };
        AddChild(urBoardInfoPanel);

        var billsPanel = new BillsPanel { Host = _host, Name = "BillsPanel" };
        AddChild(billsPanel);

        var stoveInfoPanel = new StoveInfoPanel { Host = _host, Bills = billsPanel, Name = "StoveInfoPanel" };
        AddChild(stoveInfoPanel);

        var blueprintInfoPanel = new BlueprintInfoPanel { Host = _host, Name = "BlueprintInfoPanel" };
        AddChild(blueprintInfoPanel);

        var healthTab = new HealthTabPanel { Host = _host, Name = "HealthTabPanel" };
        AddChild(healthTab);

        var needsTab = new NeedsPanel { Host = _host, Name = "NeedsPanel" };
        AddChild(needsTab);

        var pawnInfoPanel = new PawnInfoPanel { Host = _host, HealthTab = healthTab, NeedsTab = needsTab, Name = "PawnInfoPanel" };
        AddChild(pawnInfoPanel);
        hud.PawnPanel = pawnInfoPanel;
        hud.HealthTab = healthTab;
        hud.NeedsTab = needsTab;
        healthTab.PawnPanel = pawnInfoPanel;
        needsTab.PawnPanel = pawnInfoPanel;
        needsTab.HealthRef = healthTab;

        var debugBar = new DebugBar { Tools = _tools, Host = _host, Name = "DebugBar" };
        AddChild(debugBar);

        var draftActionBar = new DraftActionBar { Host = _host, Tools = _tools, Name = "DraftActionBar" };
        AddChild(draftActionBar);

        var colonistBar = new ColonistBar { Host = _host, Camera = camera, Name = "ColonistBar" };
        AddChild(colonistBar);

        var combatSfx = new CombatSfx { Host = _host, Name = "CombatSfx" };
        AddChild(combatSfx);

        var profiler = new FrameProfilerOverlay { Host = _host, Name = "FrameProfiler" };
        AddChild(profiler);

        _menu = new MainMenuPanel { Host = _host, Name = "MainMenu" };
        AddChild(_menu);

        var notifications = new UI.NotificationPanel { Host = _host, Name = "Notifications" };
        AddChild(notifications);

        // Decorative Labels and Separators don't need mouse interaction —
        // but Godot's GUI hover system hit-tests every Pass/Stop Control
        // on every mouse motion event. With ~50 visible Controls across
        // panels, that adds real per-event cost. Relax them to Ignore so
        // the hover system skips past them.
        CallDeferred(nameof(RelaxDecorativeMouseFilters));

        TryStartHarness();
    }

    private void RelaxDecorativeMouseFilters()
    {
        RelaxMouseFilters(this);
    }

    private static void RelaxMouseFilters(Node node)
    {
        // Decorative widgets — don't capture mouse, so don't participate
        // in Godot's per-motion-event hover hit-test scan.
        if (node is Label lbl) lbl.MouseFilter = Control.MouseFilterEnum.Ignore;
        else if (node is HSeparator hs) hs.MouseFilter = Control.MouseFilterEnum.Ignore;
        else if (node is VSeparator vs) vs.MouseFilter = Control.MouseFilterEnum.Ignore;
        // Layout-only containers — their children handle their own mouse
        // input; the box wrapping them never needs to be a hover target.
        // (Panel intentionally left at Stop so panel-background clicks
        // are still consumed and don't pass through to the world.)
        else if (node is BoxContainer bc) bc.MouseFilter = Control.MouseFilterEnum.Ignore;
        else if (node is MarginContainer mc) mc.MouseFilter = Control.MouseFilterEnum.Ignore;
        else if (node is CenterContainer cc) cc.MouseFilter = Control.MouseFilterEnum.Ignore;
        else if (node is GridContainer gc) gc.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var c in node.GetChildren()) RelaxMouseFilters(c);
    }

    private void TryStartHarness()
    {
        string? scenario = null;
        string? outDir = null;
        bool movieMode = false;
        var all = new System.Collections.Generic.List<string>();
        all.AddRange(OS.GetCmdlineArgs());
        all.AddRange(OS.GetCmdlineUserArgs());
        foreach (var arg in all)
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

        // Right-click anywhere in world space deselects the active tool.
        if (@event is InputEventMouseButton rmb
            && rmb.ButtonIndex == MouseButton.Right
            && rmb.Pressed
            && _tools.Mode != ToolMode.None)
        {
            _tools.Mode = ToolMode.None;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;

        switch (k.Keycode)
        {
            case Key.Key1: _host.SetTickHz(60); _host.SetPaused(false); break;
            case Key.Key2: _host.SetTickHz(180); _host.SetPaused(false); break;
            case Key.Key3: _host.SetTickHz(360); _host.SetPaused(false); break;
            case Key.Key4: _host.SetTickHz(720); _host.SetPaused(false); break;
            case Key.P:
                if (_renderer is not null)
                {
                    _renderer.ShowHitboxes = !_renderer.ShowHitboxes;
                    GD.Print(_renderer.ShowHitboxes ? "Hitboxes ON" : "Hitboxes OFF");
                }
                break;
            case Key.R:
            {
                var pawnIds = _host.SelectedDummyIds;
                if (pawnIds.Length > 0)
                {
                    // Group toggle: if any selected pawn isn't drafted yet,
                    // draft the missing ones (so a mixed selection flips
                    // toward all-drafted on the first press). Once every
                    // pawn is drafted, the next press undrafts the whole
                    // group. Same shape as the F-key door forbid handler.
                    var rsnap = _host.LatestSnapshot;
                    if (rsnap is null) return;
                    var sel = new HashSet<int>(pawnIds);
                    int draftedCount = 0, totalSeen = 0;
                    foreach (var d in rsnap.Dummies)
                    {
                        if (!sel.Contains(d.EntityId)) continue;
                        totalSeen++;
                        if (d.Drafted) draftedCount++;
                    }
                    bool draftAll = draftedCount < totalSeen;
                    foreach (var d in rsnap.Dummies)
                    {
                        if (!sel.Contains(d.EntityId)) continue;
                        if (d.Drafted == draftAll) continue;
                        _host.QueueCommand(new Sim.Commands.ToggleDraftCommand(d.EntityId));
                    }
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
                foreach (var p in snap.ItemPiles)
                {
                    if (!idSet.Contains(p.EntityId)) continue;
                    if (p.Forbidden) forbidden++; else haulable++;
                }
                bool target = !(forbidden > haulable);
                foreach (var p in snap.ItemPiles)
                {
                    if (!idSet.Contains(p.EntityId)) continue;
                    if (p.Forbidden == target) continue;
                    _host.QueueCommand(new Sim.Commands.ForbidStackCommand(p.EntityId, target));
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
                foreach (var t in _host.SelectedLampTiles)
                {
                    _host.QueueCommand(new Sim.Commands.PostLampDeconCommand(t));
                    any = true;
                }
                foreach (var t in _host.SelectedBedTiles)
                {
                    _host.QueueCommand(new Sim.Commands.PostBedDeconCommand(t));
                    any = true;
                }
                foreach (var t in _host.SelectedStoveTiles)
                {
                    _host.QueueCommand(new Sim.Commands.DeconstructStoveCommand(t));
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
