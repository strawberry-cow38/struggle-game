using System.Collections.Generic;
using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Items;

namespace StruggleGame.Game.UI;

// Top-left perf readout: FPS / TPS / speed multiplier. Uses a CanvasLayer
// so it sits above the world and ignores camera transforms.
public partial class HudOverlay : CanvasLayer
{
    public SimHost? Host { get; set; }
    // When open, these bottom-left panels push the tile readout above them.
    public PawnInfoPanel? PawnPanel { get; set; }
    public HealthTabPanel? HealthTab { get; set; }

    private DigitalClock _clock = null!;
    private Label _perfLabel = null!;
    private Label _tileLabel = null!;

    // Refresh the readout at most this often. Per-frame at 1500+ fps the
    // sim queries + string interpolation showed up under the mouse-move
    // perf hit; cap it to 30Hz so hover info still feels responsive
    // without spinning the CPU.
    private const double RefreshIntervalSec = 1.0 / 30.0;
    private double _refreshAccum;

    public override void _Ready()
    {
        Layer = 100;

        // Clock / date — Zomboid-style fire digital watch, top-left.
        _clock = new DigitalClock { Name = "Clock", MouseFilter = Control.MouseFilterEnum.Ignore };
        var clockPanel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        clockPanel.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, UiTheme.Border, 1, 8, 8, glow: false));
        clockPanel.AddChild(_clock);
        clockPanel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        clockPanel.OffsetLeft = 12; clockPanel.OffsetTop = 12;
        AddChild(clockPanel);

        // FPS / TPS — glass panel, top-right.
        _perfLabel = new Label { Name = "Perf", HorizontalAlignment = HorizontalAlignment.Right, MouseFilter = Control.MouseFilterEnum.Ignore };
        _perfLabel.AddThemeFontSizeOverride("font_size", 18);
        var perfPanel = MakeHudPanel(_perfLabel);
        perfPanel.SetAnchorsPreset(Control.LayoutPreset.TopRight);
        perfPanel.GrowHorizontal = Control.GrowDirection.Begin; // expand left from the right edge
        perfPanel.OffsetRight = -12; perfPanel.OffsetTop = 12;
        AddChild(perfPanel);

        // Hovered-tile contents + light, bottom-left.
        _tileLabel = new Label
        {
            Name = "TileContents",
            LabelSettings = new LabelSettings
            {
                FontSize = 16,
                FontColor = new Color(0.92f, 0.95f, 1.0f),
                OutlineSize = 4,
                OutlineColor = new Color(0f, 0f, 0f, 0.85f),
            },
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_tileLabel);
    }

    // Wrap a label in a small glass HUD panel (shared dreamcore theme).
    private static PanelContainer MakeHudPanel(Label label)
    {
        var p = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        p.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, UiTheme.Border, 1, 8, 8, glow: false));
        p.Theme = UiTheme.LabelTheme();
        p.AddChild(label);
        return p;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        _refreshAccum += delta;
        if (_refreshAccum < RefreshIntervalSec) return;
        _refreshAccum = 0;
        float fps = (float)Engine.GetFramesPerSecond();
        float tps = Host.ActualTps;
        string paused = Host.IsPaused ? "  [PAUSED]" : string.Empty;

        // Screen→world for the hover tile: convert viewport mouse
        // through the root canvas transform inverse (HudOverlay sits on
        // a CanvasLayer so it has no Node2D transform of its own).
        var viewport = GetViewport();
        var screenMouse = viewport.GetMousePosition();
        var canvasInv = GetTree().Root.GetCanvasTransform().AffineInverse();
        var worldMouse = canvasInv * screenMouse;
        int tx = (int)Math.Floor(worldMouse.X / SimConstants.PixelsPerTile);
        int ty = (int)Math.Floor(worldMouse.Y / SimConstants.PixelsPerTile);

        // World time + calendar from the latest snapshot (sim-thread
        // authoritative). Default to epoch if no snap yet so the format
        // string never blanks.
        var dt = Host.LatestSnapshot is { } snap
            ? SimRuntime.WorldEpoch.AddSeconds(snap.WorldTimeSec)
            : SimRuntime.WorldEpoch;
        _perfLabel.Text = $"FPS  {fps:0}\nTPS  {tps:0} / {Host.TickHz}{paused}";
        _clock.SetTime(dt.Hour, dt.Minute, dt.Second, dt.ToString("ddd MMM d, yyyy"));

        UpdateTileReadout(tx, ty);
    }

    // Bottom-left list of everything on the hovered tile, with a light row.
    private void UpdateTileReadout(int tx, int ty)
    {
        bool inBounds = tx >= 0 && tx < SimConstants.MapSize && ty >= 0 && ty < SimConstants.MapSize;
        if (Host is null || !inBounds || Host.LatestSnapshot is not { } snap)
        {
            _tileLabel.Text = string.Empty;
            return;
        }

        var lines = new List<string>();
        bool Here(TilePos t) => t.X == tx && t.Y == ty;

        foreach (var d in snap.Dummies)
            if ((int)Math.Floor(d.X) == tx && (int)Math.Floor(d.Y) == ty)
                lines.Add(d.IsEnemy ? $"Raider #{d.EntityId}" : d.Name);

        foreach (var ip in snap.ItemPiles)
            if (Here(ip.Tile))
            {
                string nm = ip.Label ?? (ItemCatalog.ItemsByPath.TryGetValue(ip.ItemPath, out var idf) ? idf.DisplayName : ip.ItemPath);
                lines.Add(ip.Count > 1 ? $"{nm} x{ip.Count}" : nm);
            }

        foreach (var t in snap.Trees) if (Here(t.Tile)) lines.Add("Tree");
        foreach (var c in snap.Crops) if (Here(c.Tile)) lines.Add($"{c.Kind} (crop)");
        foreach (var dr in snap.Doors) if (Here(dr.Tile)) lines.Add("Door");
        foreach (var b in snap.Beds) if (Here(b.Origin)) lines.Add("Bed");
        foreach (var l in snap.Lamps) if (Here(l.Tile)) lines.Add("Lamp");
        foreach (var s in snap.Stoves) if (Here(s.Origin)) lines.Add("Stove");
        foreach (var u in snap.UrBoards) if (Here(u.Tile)) lines.Add("Ur Board");
        foreach (var sb in snap.Sandbags) if (Here(sb.Tile)) lines.Add("Sandbag");
        foreach (var bp in snap.BloodPuddles) if (Here(bp.Tile)) lines.Add("Blood");

        foreach (var sp in snap.Stockpiles)
            foreach (var t in sp.Tiles) if (t.X == tx && t.Y == ty) { lines.Add($"Stockpile: {sp.Name}"); break; }
        foreach (var gz in snap.GrowZones)
            foreach (var t in gz.Tiles) if (t.X == tx && t.Y == ty) { lines.Add($"Grow zone: {gz.Name}"); break; }

        // Terrain / flooring / wall (the ground itself).
        var map = Host.Map;
        if ((int)map.GetWall(tx, ty) != 0) lines.Add($"{map.GetWall(tx, ty)} wall");
        if ((int)map.GetFlooring(tx, ty) != 0) lines.Add($"{map.GetFlooring(tx, ty)} floor");
        lines.Add($"{map.GetTerrain(tx, ty)}");

        float light = Host.LightAt(new TilePos(tx, ty));
        lines.Add($"Light: {light * 100f:0}%");

        _tileLabel.Text = string.Join("\n", lines);

        var vp = GetViewport().GetVisibleRect().Size;
        var min = _tileLabel.GetMinimumSize();
        // Sit above whichever bottom-left panel is open (highest wins).
        float bottom = vp.Y - 12f;
        if (PawnPanel is { PanelOpen: true } pp) bottom = Math.Min(bottom, pp.PanelTop - 8f);
        if (HealthTab is { PanelOpen: true } ht) bottom = Math.Min(bottom, ht.PanelTop - 8f);
        _tileLabel.Position = new Vector2(12, bottom - min.Y);
    }
}
