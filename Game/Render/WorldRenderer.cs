using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using TileMap = StruggleGame.Sim.Map.TileMap;

namespace StruggleGame.Game.Render;

// Renders the static tile map (tiled grass texture + a wall overlay
// rebuilt whenever SimSnapshot.MapVersion changes), the pending
// blueprints from the snapshot, and the dynamic dummies on top.
public partial class WorldRenderer : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;
    private const int GrassTexSize = 256;

    private ImageTexture? _grassTex;
    private ImageTexture? _wallOverlayTex;
    private ImageTexture? _floorOverlayTex;
    private int _mapPixelWidth;
    private int _mapPixelHeight;
    private int _mapWidth;
    private int _mapHeight;
    private long _lastMapVersion = -1;

    private static readonly Color WallColor = new(0.18f, 0.16f, 0.14f);
    private static readonly Color DummyColor = new(0.95f, 0.55f, 0.20f);
    private static readonly Color BlueprintFill = new(0.20f, 0.55f, 0.95f, 0.30f);
    private static readonly Color BlueprintBorder = new(0.45f, 0.75f, 1.00f, 0.85f);
    private static readonly Color BlueprintProgress = new(0.95f, 0.85f, 0.20f, 0.85f);
    private static readonly Color SelectionRing = new(1.0f, 1.0f, 0.20f, 1.0f);
    private static readonly Color PathLineColor = new(1.0f, 0.92f, 0.10f, 0.85f);
    private static readonly Color PathTargetColor = new(1.0f, 0.92f, 0.10f, 1.0f);
    private static readonly Color DraftedRing = new(1.0f, 0.25f, 0.20f, 1.0f);
    private static readonly Color OrderMarker = new(1.0f, 0.40f, 0.20f, 0.95f);
    private static readonly Color TrunkColor = new(0.32f, 0.20f, 0.10f);
    private static readonly Color CanopyColor = new(0.20f, 0.50f, 0.18f);
    private static readonly Color CanopyDark = new(0.12f, 0.34f, 0.10f);
    private static readonly Color TreeMarkColor = new(1.0f, 0.40f, 0.20f, 0.85f);
    private static readonly Color TreeSelectColor = new(0.95f, 0.95f, 0.20f, 1.0f);
    private static readonly Color WoodColor = new(0.55f, 0.35f, 0.18f);
    private static readonly Color WoodHighlight = new(0.78f, 0.55f, 0.28f);
    private static readonly Color DeconMarkColor = new(1.0f, 0.55f, 0.15f, 0.9f);
    private static readonly Color DeconProgress = new(1.0f, 0.70f, 0.25f, 1.0f);
    private static readonly Color WoodFloorColor = new(0.50f, 0.34f, 0.18f, 1.0f);
    private static readonly Color WoodFloorPlank = new(0.36f, 0.24f, 0.12f, 1.0f);
    private static readonly Color FloorBlueprintFill = new(0.85f, 0.55f, 0.25f, 0.30f);
    private static readonly Color FloorBlueprintBorder = new(0.95f, 0.70f, 0.35f, 0.85f);

    public SimHost? Host { get; set; }

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        TextureRepeat = TextureRepeatEnum.Enabled;

        if (Host is null) return;
        _grassTex = BuildGrassTexture(seed: 1337);
        _mapWidth = Host.Map.Width;
        _mapHeight = Host.Map.Height;
        _mapPixelWidth = _mapWidth * PixelsPerTile;
        _mapPixelHeight = _mapHeight * PixelsPerTile;
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_grassTex is null || Host is null) return;

        var snap = Host.LatestSnapshot;

        // Rebuild overlays if the sim mutated the map since last frame.
        if (snap is not null && snap.MapVersion != _lastMapVersion)
        {
            var wallBytes = Host.CopyLayerForRender(MapLayer.Wall);
            _wallOverlayTex = BuildWallOverlay(wallBytes, _mapWidth, _mapHeight);
            var floorBytes = Host.CopyLayerForRender(MapLayer.Flooring);
            _floorOverlayTex = BuildFloorOverlay(floorBytes, _mapWidth, _mapHeight);
            _lastMapVersion = snap.MapVersion;
        }

        var mapRect = new Rect2(0, 0, _mapPixelWidth, _mapPixelHeight);
        DrawTextureRect(_grassTex, mapRect, tile: true);
        if (_floorOverlayTex is not null)
        {
            DrawTextureRect(_floorOverlayTex, mapRect, tile: false);
        }
        if (_wallOverlayTex is not null)
        {
            DrawTextureRect(_wallOverlayTex, mapRect, tile: false);
        }

        if (snap is null) return;

        foreach (var bp in snap.Blueprints)
        {
            DrawBlueprint(bp.Tile, bp.Progress);
        }

        foreach (var fbp in snap.FloorBlueprints)
        {
            DrawFloorBlueprint(fbp.Tile, fbp.Progress);
        }

        foreach (var d in snap.Decons)
        {
            DrawDeconMark(d.Tile, d.Progress);
        }

        foreach (var w in snap.Wood)
        {
            DrawWood(w.Tile);
        }

        var selectedTreeSet = snap.SelectedTreeIds.Length > 0
            ? new HashSet<int>(snap.SelectedTreeIds)
            : null;
        foreach (var t in snap.Trees)
        {
            DrawTree(t, selectedTreeSet);
        }

        float radius = PixelsPerTile * 0.35f;
        var labelFont = ThemeDB.FallbackFont;
        const int labelFontSize = 14;
        var labelOffset = new Vector2(0f, -PixelsPerTile * 0.6f);
        foreach (var d in snap.Dummies)
        {
            var center = new Vector2(d.X * PixelsPerTile, d.Y * PixelsPerTile);
            DrawCircle(center, radius, DummyColor);
            if (d.Drafted)
            {
                DrawArc(center, radius + 2f, 0f, Mathf.Tau, 32, DraftedRing, 2f, antialiased: true);
            }
            if (snap.SelectedDummyId is int sel && d.EntityId == sel)
            {
                DrawArc(center, radius + 5f, 0f, Mathf.Tau, 32, SelectionRing, 2f, antialiased: true);
            }
            if (labelFont is not null && !string.IsNullOrEmpty(d.Job))
            {
                var textSize = labelFont.GetStringSize(d.Job, HorizontalAlignment.Center, -1f, labelFontSize);
                var anchor = center + labelOffset - new Vector2(textSize.X * 0.5f, 0f);
                DrawString(labelFont, anchor, d.Job, HorizontalAlignment.Left, -1f, labelFontSize,
                    new Color(1f, 1f, 1f, 0.95f));
            }
        }

        DrawSelectedPath(snap);
    }

    private void DrawSelectedPath(Sim.Snapshots.SimSnapshot snap)
    {
        if (snap.SelectedPath is not { Length: > 0 } path) return;
        if (snap.SelectedDummyId is not int sel) return;

        // Find live dummy world pos so the line starts at the colonist,
        // not at the tile they've already left.
        Vector2? start = null;
        foreach (var d in snap.Dummies)
        {
            if (d.EntityId != sel) continue;
            start = new Vector2(d.X * PixelsPerTile, d.Y * PixelsPerTile);
            break;
        }

        var points = new Vector2[path.Length + (start.HasValue ? 1 : 0)];
        int idx = 0;
        if (start is Vector2 s) points[idx++] = s;
        for (int k = 0; k < path.Length; k++)
        {
            points[idx++] = new Vector2(
                (path[k].X + 0.5f) * PixelsPerTile,
                (path[k].Y + 0.5f) * PixelsPerTile);
        }
        if (points.Length >= 2) DrawPolyline(points, PathLineColor, width: 2f, antialiased: true);

        var target = path[^1];
        float t = PixelsPerTile * 0.35f;
        var tc = new Vector2((target.X + 0.5f) * PixelsPerTile, (target.Y + 0.5f) * PixelsPerTile);
        DrawLine(tc + new Vector2(-t, -t), tc + new Vector2(t, t), PathTargetColor, width: 3f);
        DrawLine(tc + new Vector2(-t, t), tc + new Vector2(t, -t), PathTargetColor, width: 3f);

        // Queued draft orders past the live path.
        if (snap.SelectedOrders is { Length: > 0 } orders)
        {
            float r = PixelsPerTile * 0.18f;
            foreach (var o in orders)
            {
                var oc = new Vector2((o.X + 0.5f) * PixelsPerTile, (o.Y + 0.5f) * PixelsPerTile);
                DrawCircle(oc, r, OrderMarker);
            }
        }
    }

    private void DrawTree(Sim.Snapshots.TreeState t, HashSet<int>? selectedTrees)
    {
        var center = new Vector2((t.Tile.X + 0.5f) * PixelsPerTile, (t.Tile.Y + 0.5f) * PixelsPerTile);
        float trunkW = PixelsPerTile * 0.18f;
        float trunkH = PixelsPerTile * 0.40f;
        var trunkRect = new Rect2(center.X - trunkW * 0.5f, center.Y, trunkW, trunkH);
        DrawRect(trunkRect, TrunkColor, filled: true);

        float canopyR = PixelsPerTile * 0.42f;
        DrawCircle(center + new Vector2(0, -PixelsPerTile * 0.10f), canopyR, CanopyDark);
        DrawCircle(center + new Vector2(0, -PixelsPerTile * 0.12f), canopyR * 0.78f, CanopyColor);

        if (t.HasJob)
        {
            // Orange diagonal slash marking a pending chop.
            float s = PixelsPerTile * 0.30f;
            DrawLine(center + new Vector2(-s, -s), center + new Vector2(s, s), TreeMarkColor, width: 3f);
            DrawLine(center + new Vector2(-s, s), center + new Vector2(s, -s), TreeMarkColor, width: 3f);

            if (t.ChopProgress > 0f)
            {
                float bw = PixelsPerTile * 0.6f;
                float bh = 3f;
                var barBg = new Rect2(center.X - bw * 0.5f, center.Y - PixelsPerTile * 0.60f, bw, bh);
                DrawRect(barBg, new Color(0f, 0f, 0f, 0.6f), filled: true);
                var barFg = new Rect2(barBg.Position, new Vector2(bw * Mathf.Clamp(t.ChopProgress, 0f, 1f), bh));
                DrawRect(barFg, new Color(1f, 0.9f, 0.2f, 1f), filled: true);
            }
        }

        if (selectedTrees is not null && selectedTrees.Contains(t.EntityId))
        {
            DrawArc(center, canopyR + 3f, 0f, Mathf.Tau, 36, TreeSelectColor, width: 2f, antialiased: true);
        }
    }

    private void DrawWood(TilePos tile)
    {
        var center = new Vector2((tile.X + 0.5f) * PixelsPerTile, (tile.Y + 0.5f) * PixelsPerTile);
        float logW = PixelsPerTile * 0.55f;
        float logH = PixelsPerTile * 0.20f;
        var rect = new Rect2(center.X - logW * 0.5f, center.Y - logH * 0.5f, logW, logH);
        DrawRect(rect, WoodColor, filled: true);
        var hi = new Rect2(rect.Position + new Vector2(0, 1f), new Vector2(rect.Size.X, 2f));
        DrawRect(hi, WoodHighlight, filled: true);
    }

    private void DrawDeconMark(TilePos tile, float progress)
    {
        float cx = (tile.X + 0.5f) * PixelsPerTile;
        float cy = (tile.Y + 0.5f) * PixelsPerTile;
        float s = PixelsPerTile * 0.32f;
        DrawLine(new Vector2(cx - s, cy - s), new Vector2(cx + s, cy + s), DeconMarkColor, width: 3f);
        DrawLine(new Vector2(cx - s, cy + s), new Vector2(cx + s, cy - s), DeconMarkColor, width: 3f);
        if (progress > 0f)
        {
            float bw = PixelsPerTile * 0.7f;
            float bh = 3f;
            var bg = new Rect2(cx - bw * 0.5f, cy - PixelsPerTile * 0.45f, bw, bh);
            DrawRect(bg, new Color(0f, 0f, 0f, 0.6f), filled: true);
            var fg = new Rect2(bg.Position, new Vector2(bw * Mathf.Clamp(progress, 0f, 1f), bh));
            DrawRect(fg, DeconProgress, filled: true);
        }
    }

    private void DrawBlueprint(TilePos tile, float progress)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, BlueprintFill, filled: true);
        DrawRect(rect, BlueprintBorder, filled: false, width: 2f);
        if (progress > 0f)
        {
            float h = PixelsPerTile * Mathf.Clamp(progress, 0f, 1f);
            var bar = new Rect2(
                rect.Position.X,
                rect.Position.Y + (PixelsPerTile - h),
                PixelsPerTile,
                h);
            DrawRect(bar, BlueprintProgress, filled: true);
        }
    }

    // Grass texture tuned for the oblique top-down camera: short vertical
    // blade strokes (camera looks down + forward, so blades visible as tiny
    // upright streaks), each a base→tip gradient, scattered across a
    // mottled soil base. Wraps seamlessly because strokes that cross an
    // edge are also drawn on the opposite side.
    private static ImageTexture BuildGrassTexture(int seed)
    {
        var rng = new Random(seed);
        var img = Image.CreateEmpty(GrassTexSize, GrassTexSize, false, Image.Format.Rgba8);

        const int baseGrid = 8;
        var baseNoise = new float[baseGrid + 1, baseGrid + 1];
        for (int y = 0; y < baseGrid; y++)
        {
            for (int x = 0; x < baseGrid; x++)
            {
                baseNoise[x, y] = (float)rng.NextDouble();
            }
        }
        for (int i = 0; i <= baseGrid; i++)
        {
            baseNoise[baseGrid, i] = baseNoise[0, i % baseGrid];
            baseNoise[i, baseGrid] = baseNoise[i % baseGrid, 0];
        }
        for (int py = 0; py < GrassTexSize; py++)
        {
            for (int px = 0; px < GrassTexSize; px++)
            {
                float fx = px / (float)GrassTexSize * baseGrid;
                float fy = py / (float)GrassTexSize * baseGrid;
                int ix = (int)fx;
                int iy = (int)fy;
                float u = fx - ix;
                float v = fy - iy;
                float n = Mathf.Lerp(
                    Mathf.Lerp(baseNoise[ix, iy], baseNoise[ix + 1, iy], u),
                    Mathf.Lerp(baseNoise[ix, iy + 1], baseNoise[ix + 1, iy + 1], u),
                    v);
                float r = 0.18f + n * 0.12f;
                float g = 0.26f + n * 0.15f;
                float b = 0.10f + n * 0.07f;
                img.SetPixel(px, py, new Color(r, g, b));
            }
        }

        var bladePalette = new Color[]
        {
            new(0.48f, 0.62f, 0.18f),
            new(0.36f, 0.55f, 0.16f),
            new(0.42f, 0.66f, 0.22f),
            new(0.55f, 0.58f, 0.18f),
            new(0.62f, 0.55f, 0.20f),
        };
        const int bladeCount = 2200;
        for (int i = 0; i < bladeCount; i++)
        {
            int bx = rng.Next(GrassTexSize);
            int by = rng.Next(GrassTexSize);
            int height = rng.Next(3, 7);
            int width = rng.NextDouble() < 0.15 ? 2 : 1;
            var tip = bladePalette[rng.Next(bladePalette.Length)];
            for (int yy = 0; yy < height; yy++)
            {
                float t = yy / (float)(height - 1);
                float shade = Mathf.Lerp(0.55f, 1.0f, t);
                var c = new Color(tip.R * shade, tip.G * shade, tip.B * shade);
                for (int xx = 0; xx < width; xx++)
                {
                    int wx = (bx + xx) & (GrassTexSize - 1);
                    int wy = (by + yy) & (GrassTexSize - 1);
                    img.SetPixel(wx, wy, c);
                }
            }
        }

        return ImageTexture.CreateFromImage(img);
    }

    private void DrawFloorBlueprint(TilePos tile, float progress)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, FloorBlueprintFill, filled: true);
        DrawRect(rect, FloorBlueprintBorder, filled: false, width: 2f);
        if (progress > 0f)
        {
            float h = PixelsPerTile * Mathf.Clamp(progress, 0f, 1f);
            var bar = new Rect2(
                rect.Position.X,
                rect.Position.Y + (PixelsPerTile - h),
                PixelsPerTile,
                h);
            DrawRect(bar, BlueprintProgress, filled: true);
        }
    }

    private static ImageTexture BuildFloorOverlay(byte[] tiles, int width, int height)
    {
        // Per-tile pattern: solid wood with a darker horizontal plank line.
        int texW = width * PixelsPerTile;
        int texH = height * PixelsPerTile;
        var img = Image.CreateEmpty(texW, texH, false, Image.Format.Rgba8);
        var transparent = new Color(0f, 0f, 0f, 0f);
        // Fill transparent first.
        for (int py = 0; py < texH; py++)
        {
            for (int px = 0; px < texW; px++)
            {
                img.SetPixel(px, py, transparent);
            }
        }
        int plankY1 = PixelsPerTile / 3;
        int plankY2 = (PixelsPerTile * 2) / 3;
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                if (tiles[ty * width + tx] == 0) continue;
                int ox = tx * PixelsPerTile;
                int oy = ty * PixelsPerTile;
                for (int yy = 0; yy < PixelsPerTile; yy++)
                {
                    var c = (yy == plankY1 || yy == plankY2) ? WoodFloorPlank : WoodFloorColor;
                    for (int xx = 0; xx < PixelsPerTile; xx++)
                    {
                        img.SetPixel(ox + xx, oy + yy, c);
                    }
                }
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    private static ImageTexture BuildWallOverlay(byte[] tiles, int width, int height)
    {
        var img = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var transparent = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool wall = tiles[y * width + x] != 0;
                img.SetPixel(x, y, wall ? WallColor : transparent);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
