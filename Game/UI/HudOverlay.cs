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
    // When open, the bottom-left pawn panel pushes the tile readout above it.
    public PawnInfoPanel? PawnPanel { get; set; }

    private Label _label = null!;
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

        var settings = new LabelSettings
        {
            FontSize = 20,
            FontColor = new Color(1.0f, 1.0f, 1.0f),
            OutlineSize = 4,
            OutlineColor = new Color(0f, 0f, 0f, 0.85f),
        };

        _label = new Label
        {
            Name = "Readout",
            LabelSettings = settings,
            Position = new Vector2(12, 8),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_label);

        // FPS / TPS readout, top-right.
        _perfLabel = new Label
        {
            Name = "Perf",
            LabelSettings = settings,
            HorizontalAlignment = HorizontalAlignment.Right,
            AnchorLeft = 1, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 0,
            OffsetLeft = -260, OffsetTop = 8, OffsetRight = -12, OffsetBottom = 60,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_perfLabel);

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
        string clock = dt.ToString("HH:mm");
        string date = dt.ToString("ddd MMM d, yyyy");

        _perfLabel.Text = $"FPS  {fps:0}\nTPS  {tps:0} / {Host.TickHz}{paused}";
        _label.Text = $"{clock}  {date}";

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
                lines.Add(d.IsEnemy ? $"Raider #{d.EntityId}" : $"Colonist #{d.EntityId}");

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
        // Sit just above the pawn panel when it's open, else the screen bottom.
        float bottom = PawnPanel is { PanelOpen: true } pp ? pp.PanelTop - 8f : vp.Y - 12f;
        _tileLabel.Position = new Vector2(12, bottom - min.Y);
    }
}
