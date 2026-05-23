using Godot;
using StruggleGame.Game.Debug;
using StruggleGame.Sim;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;
using TileMap = StruggleGame.Sim.Map.TileMap;

namespace StruggleGame.Game.Render;

// Renders the static tile map (per-tile flat green ground + a wall
// overlay rebuilt whenever SimSnapshot.MapVersion changes), the
// pending blueprints from the snapshot, and the dynamic dummies on
// top.
public partial class WorldRenderer : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    // One pixel per tile, drawn stretched to tile size with the Nearest
    // filter — each tile gets a slightly different green shade picked
    // from a deterministic per-tile hash.
    private ImageTexture? _groundTex;
    private ImageTexture? _wallOverlayTex;
    private ImageTexture? _roomOverlayTex;
    // Cached flooring layer (one byte per tile). Drawn per-frame as small
    // DrawRects rather than as a giant per-pixel texture overlay — the
    // old (mapSize*PixelsPerTile)^2 image was 1GB at 256x256 tiles and
    // froze the main thread for seconds every time a blueprint finished
    // (because the renderer eagerly rebuilt every overlay on MapVersion
    // bump, including floors).
    private byte[]? _floorBytes;
    private int _mapPixelWidth;
    private int _mapPixelHeight;
    private int _mapWidth;
    private int _mapHeight;
    private long _lastMapVersion = -1;
    private long _lastRoomVersion = -1;
    private long _lastRoofVersion = -1;
    private ImageTexture? _roofOverlayTex;
    private ImageTexture? _noRoofOverlayTex;

    // Snapshot pair used for render-side interpolation. _prevSnap is the
    // last snapshot we drew from, _currSnap is the next one. We render at
    // alpha across the wall-clock interval between them so visible motion
    // is smooth even when the render rate is much lower than the sim's
    // tick rate (which would otherwise show snapshot-to-snapshot jumps).
    private SimSnapshot? _prevSnap;
    private SimSnapshot? _currSnap;
    private ulong _currSnapStartMs;
    private Dictionary<int, DummyState>? _prevDummyByIdScratch;
    private Dictionary<TilePos, DoorRenderState>? _prevDoorByTileScratch;

    // Selection sets are cached by reference identity of the snapshot's
    // SelectedXxxIds array — when the underlying array doesn't change
    // between snapshots we skip rebuilding the HashSet (avoids per-frame
    // GC churn when nothing's been clicked).
    private int[]? _cachedSelectedTreeIdRef;
    private HashSet<int>? _cachedSelectedTreeSet;
    private int[]? _cachedSelectedWoodIdRef;
    private HashSet<int>? _cachedSelectedWoodSet;

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
    private static readonly Color DoorBlueprintFill = new(0.55f, 0.40f, 0.85f, 0.30f);
    private static readonly Color DoorBlueprintBorder = new(0.85f, 0.65f, 1.00f, 0.85f);
    private static readonly Color DoorPanelColor = new(0.55f, 0.36f, 0.20f);
    private static readonly Color DoorPanelEdge = new(0.30f, 0.18f, 0.08f);
    private static readonly Color DoorForbidMark = new(0.95f, 0.18f, 0.18f, 0.95f);
    private static readonly Color SelectionOutline = new(0.30f, 0.95f, 1.00f, 1.00f);
    private static readonly Color StockpileFill = new(0.95f, 0.85f, 0.25f, 0.12f);
    private static readonly Color StockpileBorder = new(1.00f, 0.90f, 0.35f, 0.85f);
    private static readonly Color StockpileSelectedBorder = new(1.00f, 1.00f, 0.20f, 1.00f);
    private static readonly Color GrowZoneFill = new(0.35f, 0.85f, 0.30f, 0.14f);
    private static readonly Color GrowZoneBorder = new(0.45f, 0.95f, 0.40f, 0.85f);
    private static readonly Color GrowZoneSelectedBorder = new(0.65f, 1.00f, 0.55f, 1.00f);

    public SimHost? Host { get; set; }

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        TextureRepeat = TextureRepeatEnum.Enabled;

        if (Host is null) return;
        _mapWidth = Host.Map.Width;
        _mapHeight = Host.Map.Height;
        _mapPixelWidth = _mapWidth * PixelsPerTile;
        _mapPixelHeight = _mapHeight * PixelsPerTile;
        _groundTex = BuildGroundTexture(_mapWidth, _mapHeight, seed: 1337);
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_groundTex is null || Host is null) return;

        var latest = Host.LatestSnapshot;
        // Reference compare, not Tick: paused republishes (selection
        // change, designation while paused) reuse the same Tick but are
        // a brand new snapshot object — Tick-only check would miss them
        // and the selection rings/outlines would lag until unpause.
        if (latest is not null && !ReferenceEquals(latest, _currSnap))
        {
            _prevSnap = _currSnap;
            _currSnap = latest;
            _currSnapStartMs = Time.GetTicksMsec();
        }
        var snap = _currSnap ?? latest;

        // Wall-clock alpha into the [_prevSnap, _currSnap] interval, clamped
        // to [0,1]. Used to lerp pawn positions and door open amounts so
        // motion looks smooth between sim ticks regardless of render fps.
        float interpAlpha = 1f;
        if (_prevSnap is not null && Host is not null)
        {
            float tickIntervalMs = 1000f / Math.Max(1, Host.TickHz);
            float elapsed = Time.GetTicksMsec() - _currSnapStartMs;
            interpAlpha = elapsed / tickIntervalMs;
            if (interpAlpha < 0f) interpAlpha = 0f;
            if (interpAlpha > 1f) interpAlpha = 1f;
        }

        // Rebuild overlays if the sim mutated the map since last frame.
        if (snap is not null && snap.MapVersion != _lastMapVersion)
        {
            var wallBytes = Host!.CopyLayerForRender(MapLayer.Wall);
            _wallOverlayTex = BuildWallOverlay(wallBytes, _mapWidth, _mapHeight);
            _floorBytes = Host!.CopyLayerForRender(MapLayer.Flooring);
            _lastMapVersion = snap.MapVersion;
        }
        if (snap is not null && snap.RoomVersion != _lastRoomVersion)
        {
            var roomTiles = Host!.CopyRoomTilesForRender();
            _roomOverlayTex = BuildRoomOverlay(roomTiles, _mapWidth, _mapHeight);
            _lastRoomVersion = snap.RoomVersion;
        }
        if (snap is not null && snap.RoofVersion != _lastRoofVersion)
        {
            var roofBytes = Host!.CopyRoofTilesForRender();
            var noRoofBytes = Host!.CopyNoRoofTilesForRender();
            _roofOverlayTex = BuildRoofOverlay(roofBytes, _mapWidth, _mapHeight);
            _noRoofOverlayTex = BuildNoRoofOverlay(noRoofBytes, _mapWidth, _mapHeight);
            _lastRoofVersion = snap.RoofVersion;
        }

        var mapRect = new Rect2(0, 0, _mapPixelWidth, _mapPixelHeight);
        using (FrameProfiler.Instance.BeginScope("Map"))
        {
            DrawTextureRect(_groundTex, mapRect, tile: false);
            DrawFlooringTiles();
            if (_roomOverlayTex is not null)
            {
                DrawTextureRect(_roomOverlayTex, mapRect, tile: false);
            }
            if (_wallOverlayTex is not null)
            {
                DrawTextureRect(_wallOverlayTex, mapRect, tile: false);
            }
            // Roof sits above walls so it visually covers them too. The
            // no-roof hatch goes on top of everything map-layer so the
            // player can see the forbidden cells through the room tint.
            if (_roofOverlayTex is not null)
            {
                DrawTextureRect(_roofOverlayTex, mapRect, tile: false);
            }
            if (_noRoofOverlayTex is not null)
            {
                DrawTextureRect(_noRoofOverlayTex, mapRect, tile: false);
            }
        }

        if (snap is null) { FrameProfiler.Instance.EndFrame(); return; }

        // Visible-world tile rect for cheap AABB culling of entity loops.
        // 1-tile pad so things straddling the edge don't pop in/out.
        var canvasXform = GetCanvasTransform();
        var canvasInv = canvasXform.AffineInverse();
        var vpSize = GetViewportRect().Size;
        // pxPerTile = how many screen pixels a single sim tile occupies
        // after the camera transform. Below ~14 px/tile, trees + crops
        // are tiny — drop to a flat-rect LOD that skips the per-tree
        // DrawCircles (~32-segment polygons) so the per-tree cost stops
        // dominating the frame when zoomed far out.
        float pxPerTile = canvasXform.X.Length() * PixelsPerTile;
        bool simpleLod = pxPerTile < 14f;
        var tl = canvasInv * Vector2.Zero;
        var br = canvasInv * vpSize;
        int viewMinTileX = (int)Math.Floor(Math.Min(tl.X, br.X) / PixelsPerTile) - 1;
        int viewMaxTileX = (int)Math.Floor(Math.Max(tl.X, br.X) / PixelsPerTile) + 1;
        int viewMinTileY = (int)Math.Floor(Math.Min(tl.Y, br.Y) / PixelsPerTile) - 1;
        int viewMaxTileY = (int)Math.Floor(Math.Max(tl.Y, br.Y) / PixelsPerTile) + 1;

        using (FrameProfiler.Instance.BeginScope("Stockpiles"))
        {
            int? selectedStockpileId = Host?.SelectedStockpileId;
            foreach (var sp in snap.Stockpiles)
            {
                DrawStockpile(sp, isSelected: selectedStockpileId == sp.Id);
            }
        }

        using (FrameProfiler.Instance.BeginScope("GrowZones"))
        {
            int? selectedGrowZoneId = Host?.SelectedGrowZoneId;
            foreach (var gz in snap.GrowZones)
            {
                DrawGrowZone(gz, isSelected: selectedGrowZoneId == gz.Id);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Blueprints"))
        {
            foreach (var bp in snap.Blueprints)
            {
                if (bp.Tile.X < viewMinTileX || bp.Tile.X > viewMaxTileX
                    || bp.Tile.Y < viewMinTileY || bp.Tile.Y > viewMaxTileY) continue;
                DrawBlueprint(bp.Tile, bp.Progress);
                if (bp.Forbidden) DrawForbidX(bp.Tile);
            }

            foreach (var fbp in snap.FloorBlueprints)
            {
                if (fbp.Tile.X < viewMinTileX || fbp.Tile.X > viewMaxTileX
                    || fbp.Tile.Y < viewMinTileY || fbp.Tile.Y > viewMaxTileY) continue;
                DrawFloorBlueprint(fbp.Tile, fbp.Progress);
                if (fbp.Forbidden) DrawForbidX(fbp.Tile);
            }

            foreach (var dbp in snap.DoorBlueprints)
            {
                if (dbp.Tile.X < viewMinTileX || dbp.Tile.X > viewMaxTileX
                    || dbp.Tile.Y < viewMinTileY || dbp.Tile.Y > viewMaxTileY) continue;
                DrawDoorBlueprint(dbp.Tile, dbp.Progress);
                if (dbp.Forbidden) DrawForbidX(dbp.Tile);
            }

            foreach (var rbp in snap.RoofBlueprints)
            {
                if (rbp.Tile.X < viewMinTileX || rbp.Tile.X > viewMaxTileX
                    || rbp.Tile.Y < viewMinTileY || rbp.Tile.Y > viewMaxTileY) continue;
                DrawRoofBlueprint(rbp.Tile, rbp.Progress, rbp.Build);
                if (rbp.Forbidden) DrawForbidX(rbp.Tile);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Doors"))
        {
            _prevDoorByTileScratch ??= new Dictionary<TilePos, DoorRenderState>();
            _prevDoorByTileScratch.Clear();
            if (_prevSnap is not null && interpAlpha < 1f)
            {
                foreach (var pd in _prevSnap.Doors) _prevDoorByTileScratch[pd.Tile] = pd;
            }
            foreach (var door in snap.Doors)
            {
                if (door.Tile.X < viewMinTileX || door.Tile.X > viewMaxTileX
                    || door.Tile.Y < viewMinTileY || door.Tile.Y > viewMaxTileY) continue;
                float openAmount = door.OpenAmount;
                if (_prevDoorByTileScratch.TryGetValue(door.Tile, out var pd))
                {
                    openAmount = Mathf.Lerp(pd.OpenAmount, door.OpenAmount, interpAlpha);
                }
                DrawDoor(new DoorRenderState(door.Tile, door.Orientation, openAmount, door.Forbidden, door.Locked, door.Priority));
            }
        }

        using (FrameProfiler.Instance.BeginScope("Decons"))
        {
            foreach (var d in snap.Decons)
            {
                if (d.Tile.X < viewMinTileX || d.Tile.X > viewMaxTileX
                    || d.Tile.Y < viewMinTileY || d.Tile.Y > viewMaxTileY) continue;
                DrawDeconMark(d.Tile, d.Progress);
                if (d.Forbidden) DrawForbidX(d.Tile);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Selection"))
        {
            if (Host is not null)
            {
                foreach (var t in Host.SelectedWallTiles) DrawSelectionOutline(t);
                foreach (var t in Host.SelectedDoorTiles) DrawSelectionOutline(t);
                foreach (var t in Host.SelectedBlueprintTiles) DrawSelectionOutline(t);
            }
        }

        var stackFont = ThemeDB.FallbackFont;
        var mouseLocal = GetLocalMousePosition();
        int cursorTileX = Mathf.FloorToInt(mouseLocal.X / PixelsPerTile);
        int cursorTileY = Mathf.FloorToInt(mouseLocal.Y / PixelsPerTile);
        using (FrameProfiler.Instance.BeginScope("Wood"))
        {
            var selectedWoodSet = GetCachedSelectedSet(
                snap.SelectedWoodIds, ref _cachedSelectedWoodIdRef, ref _cachedSelectedWoodSet);
            foreach (var w in snap.Wood)
            {
                if (w.Tile.X < viewMinTileX || w.Tile.X > viewMaxTileX
                    || w.Tile.Y < viewMinTileY || w.Tile.Y > viewMaxTileY) continue;
                DrawWood(w.Tile);
                if (w.Forbidden) DrawForbiddenMark(w.Tile);
                if (selectedWoodSet is not null && selectedWoodSet.Contains(w.EntityId)) DrawWoodSelectionRing(w.Tile);
                if (w.Tile.X == cursorTileX && w.Tile.Y == cursorTileY)
                {
                    DrawStackLabel(stackFont, w.Tile, w.ItemPath, w.Count);
                }
            }
        }

        using (FrameProfiler.Instance.BeginScope("Trees"))
        {
            var selectedTreeSet = GetCachedSelectedSet(
                snap.SelectedTreeIds, ref _cachedSelectedTreeIdRef, ref _cachedSelectedTreeSet);
            foreach (var t in snap.Trees)
            {
                if (t.Tile.X < viewMinTileX || t.Tile.X > viewMaxTileX
                    || t.Tile.Y < viewMinTileY || t.Tile.Y > viewMaxTileY) continue;
                DrawTree(t, selectedTreeSet, simpleLod);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Crops"))
        {
            foreach (var c in snap.Crops)
            {
                if (c.Tile.X < viewMinTileX || c.Tile.X > viewMaxTileX
                    || c.Tile.Y < viewMinTileY || c.Tile.Y > viewMaxTileY) continue;
                DrawCrop(c, simpleLod);
            }
        }

        using (FrameProfiler.Instance.BeginScope("ItemPiles"))
        {
            foreach (var p in snap.ItemPiles)
            {
                if (p.Tile.X < viewMinTileX || p.Tile.X > viewMaxTileX
                    || p.Tile.Y < viewMinTileY || p.Tile.Y > viewMaxTileY) continue;
                DrawItemPile(p);
                if (p.Tile.X == cursorTileX && p.Tile.Y == cursorTileY)
                {
                    DrawStackLabel(stackFont, p.Tile, p.ItemPath, p.Count);
                }
            }
        }

        float radius = PixelsPerTile * 0.35f;
        var labelFont = ThemeDB.FallbackFont;
        const int labelFontSize = 14;
        var labelOffset = new Vector2(0f, -PixelsPerTile * 0.6f);
        _prevDummyByIdScratch ??= new Dictionary<int, DummyState>();
        _prevDummyByIdScratch.Clear();
        if (_prevSnap is not null && interpAlpha < 1f)
        {
            foreach (var pd in _prevSnap.Dummies) _prevDummyByIdScratch[pd.EntityId] = pd;
        }
        using (FrameProfiler.Instance.BeginScope("Dummies"))
        {
            foreach (var d in snap.Dummies)
            {
                int tx = (int)d.X;
                int ty = (int)d.Y;
                if (tx < viewMinTileX || tx > viewMaxTileX
                    || ty < viewMinTileY || ty > viewMaxTileY) continue;
                float drawX = d.X;
                float drawY = d.Y;
                if (_prevDummyByIdScratch.TryGetValue(d.EntityId, out var prev))
                {
                    drawX = Mathf.Lerp(prev.X, d.X, interpAlpha);
                    drawY = Mathf.Lerp(prev.Y, d.Y, interpAlpha);
                }
                var center = new Vector2(drawX * PixelsPerTile, drawY * PixelsPerTile);
                DrawCircle(center, radius, DummyColor);
                if (d.Drafted)
                {
                    DrawArc(center, radius + 2f, 0f, Mathf.Tau, 32, DraftedRing, 2f, antialiased: true);
                }
                if (d.Carrying)
                {
                    float logW = PixelsPerTile * 0.45f;
                    float logH = PixelsPerTile * 0.18f;
                    var carry = new Rect2(center.X - logW * 0.5f, center.Y - radius - logH - 1f, logW, logH);
                    DrawRect(carry, WoodColor, filled: true);
                    DrawRect(new Rect2(carry.Position + new Vector2(0, 1f), new Vector2(carry.Size.X, 2f)), WoodHighlight, filled: true);
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
        }

        using (FrameProfiler.Instance.BeginScope("Path"))
        {
            DrawSelectedPath(snap);
        }

        FrameProfiler.Instance.EndFrame();
    }

    // Stack label rendered just below a dropped item: white "Name xCount"
    // text drawn in world-space (scales with camera zoom). Only the
    // stack on the exact hovered tile gets labelled so dense yards don't
    // drown in text.
    private const int StackLabelFontSize = 25;

    private void DrawStackLabel(Font? font, TilePos tile, string itemPath, int count)
    {
        if (font is null) return;
        string name = ItemCatalog.ItemsByPath.TryGetValue(itemPath, out var def)
            ? def.DisplayName
            : itemPath;
        string text = $"{name} x{count}";
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1f, StackLabelFontSize);
        float cx = (tile.X + 0.5f) * PixelsPerTile;
        float baseY = (tile.Y + 0.5f) * PixelsPerTile + PixelsPerTile * 0.22f + font.GetAscent(StackLabelFontSize);
        var pos = new Vector2(cx - size.X * 0.5f, baseY);
        // Cheap drop shadow so white text stays legible on light tiles.
        DrawString(font, pos + new Vector2(1f, 1f), text,
            HorizontalAlignment.Left, -1f, StackLabelFontSize, new Color(0f, 0f, 0f, 0.85f));
        DrawString(font, pos, text,
            HorizontalAlignment.Left, -1f, StackLabelFontSize, Colors.White);
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

    private void DrawTree(Sim.Snapshots.TreeState t, HashSet<int>? selectedTrees, bool simpleLod)
    {
        var center = new Vector2((t.Tile.X + 0.5f) * PixelsPerTile, (t.Tile.Y + 0.5f) * PixelsPerTile);
        float scale = 0.35f + 0.65f * Mathf.Clamp(t.GrowthStage, 0f, 1f);

        if (simpleLod)
        {
            // Zoomed out: 2 rects per tree (canopy block + trunk pip),
            // no DrawCircle (defaults to 32-segment polygon = ~64 tris
            // per canopy). Cuts ~95% of the per-tree vertex cost at low
            // zoom where the detail isn't visible anyway.
            float r = PixelsPerTile * 0.42f * scale;
            DrawRect(new Rect2(center.X - r, center.Y - r, r * 2f, r * 2f), CanopyColor, filled: true);
            if (t.HasJob)
            {
                float s = PixelsPerTile * 0.30f;
                DrawLine(center + new Vector2(-s, -s), center + new Vector2(s, s), TreeMarkColor, width: 2f);
                DrawLine(center + new Vector2(-s, s), center + new Vector2(s, -s), TreeMarkColor, width: 2f);
            }
            if (selectedTrees is not null && selectedTrees.Contains(t.EntityId))
            {
                var ring = new Rect2(center.X - r - 1f, center.Y - r - 1f, (r + 1f) * 2f, (r + 1f) * 2f);
                DrawRect(ring, TreeSelectColor, filled: false, width: 1.5f);
            }
            return;
        }

        // Visually grow with stage. Saplings start at ~35% scale so they
        // still read as a tree (not invisible) and mature at 1.0.
        float trunkW = PixelsPerTile * 0.18f * scale;
        float trunkH = PixelsPerTile * 0.40f * scale;
        var trunkRect = new Rect2(center.X - trunkW * 0.5f, center.Y, trunkW, trunkH);
        DrawRect(trunkRect, TrunkColor, filled: true);

        float canopyR = PixelsPerTile * 0.42f * scale;
        DrawCircle(center + new Vector2(0, -PixelsPerTile * 0.10f * scale), canopyR, CanopyDark);
        DrawCircle(center + new Vector2(0, -PixelsPerTile * 0.12f * scale), canopyR * 0.78f, CanopyColor);

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

    private static readonly Color CarrotBody = new(0.95f, 0.55f, 0.12f);
    private static readonly Color CarrotBodyDark = new(0.78f, 0.40f, 0.08f);
    private static readonly Color CarrotLeaf = new(0.30f, 0.70f, 0.20f);
    private static readonly Color CutMarkColor = new(0.95f, 0.85f, 0.20f, 0.95f);
    private static readonly Color HarvestMarkColor = new(1.0f, 0.55f, 0.20f, 0.95f);

    private void DrawCrop(Sim.Snapshots.CropState c, bool simpleLod)
    {
        var center = new Vector2((c.Tile.X + 0.5f) * PixelsPerTile, (c.Tile.Y + 0.5f) * PixelsPerTile);
        float scale = 0.30f + 0.70f * Mathf.Clamp(c.GrowthStage, 0f, 1f);

        if (simpleLod)
        {
            float r = PixelsPerTile * 0.20f * scale;
            DrawRect(new Rect2(center.X - r, center.Y - r, r * 2f, r * 2f), CarrotBody, filled: true);
            return;
        }

        // Orange wedge for the carrot body, point-down.
        float bodyH = PixelsPerTile * 0.32f * scale;
        float bodyW = PixelsPerTile * 0.28f * scale;
        var p0 = center + new Vector2(-bodyW * 0.5f, -bodyH * 0.10f);
        var p1 = center + new Vector2(bodyW * 0.5f, -bodyH * 0.10f);
        var p2 = center + new Vector2(0f, bodyH);
        DrawColoredPolygon(new[] { p0, p1, p2 }, CarrotBody);
        DrawLine(p0, p2, CarrotBodyDark, width: 1f);
        DrawLine(p1, p2, CarrotBodyDark, width: 1f);

        // Green leaves on top.
        float leafH = PixelsPerTile * 0.28f * scale;
        var top = center + new Vector2(0f, -bodyH * 0.10f);
        DrawLine(top, top + new Vector2(0f, -leafH), CarrotLeaf, width: 2f);
        DrawLine(top, top + new Vector2(-leafH * 0.5f, -leafH * 0.85f), CarrotLeaf, width: 2f);
        DrawLine(top, top + new Vector2(leafH * 0.5f, -leafH * 0.85f), CarrotLeaf, width: 2f);

        if (c.ActiveJob is StruggleGame.Sim.Jobs.JobKind kind)
        {
            var mark = kind == StruggleGame.Sim.Jobs.JobKind.Harvest ? HarvestMarkColor : CutMarkColor;
            float s = PixelsPerTile * 0.28f;
            DrawLine(center + new Vector2(-s, -s), center + new Vector2(s, s), mark, width: 2.5f);
            DrawLine(center + new Vector2(-s, s), center + new Vector2(s, -s), mark, width: 2.5f);

            if (c.WorkProgress > 0f)
            {
                float bw = PixelsPerTile * 0.6f;
                float bh = 3f;
                var barBg = new Rect2(center.X - bw * 0.5f, center.Y - PixelsPerTile * 0.55f, bw, bh);
                DrawRect(barBg, new Color(0f, 0f, 0f, 0.6f), filled: true);
                var barFg = new Rect2(barBg.Position, new Vector2(bw * Mathf.Clamp(c.WorkProgress, 0f, 1f), bh));
                DrawRect(barFg, new Color(1f, 0.9f, 0.2f, 1f), filled: true);
            }
        }
    }

    private void DrawItemPile(Sim.Snapshots.ItemPileState p)
    {
        // Generic non-wood pile. Carrots = small orange dot stack.
        var center = new Vector2((p.Tile.X + 0.5f) * PixelsPerTile, (p.Tile.Y + 0.5f) * PixelsPerTile);
        float r = PixelsPerTile * 0.16f;
        DrawCircle(center, r, CarrotBody);
        DrawArc(center, r, 0f, Mathf.Tau, 18, CarrotBodyDark, width: 1f, antialiased: true);
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

    private static readonly Color ForbiddenMarkColor = new(0.95f, 0.25f, 0.25f, 0.95f);

    private void DrawForbiddenMark(TilePos tile)
    {
        float cx = (tile.X + 0.5f) * PixelsPerTile;
        float cy = (tile.Y + 0.5f) * PixelsPerTile;
        float s = PixelsPerTile * 0.28f;
        DrawLine(new Vector2(cx - s, cy - s), new Vector2(cx + s, cy + s), ForbiddenMarkColor, width: 2.5f);
        DrawLine(new Vector2(cx - s, cy + s), new Vector2(cx + s, cy - s), ForbiddenMarkColor, width: 2.5f);
    }

    private void DrawWoodSelectionRing(TilePos tile)
    {
        float cx = (tile.X + 0.5f) * PixelsPerTile;
        float cy = (tile.Y + 0.5f) * PixelsPerTile;
        float r = PixelsPerTile * 0.45f;
        DrawArc(new Vector2(cx, cy), r, 0f, Mathf.Tau, 32, SelectionRing, width: 2f, antialiased: true);
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

    // One pixel per tile, each a small jitter around a base green. The
    // resulting Image is drawn stretched to (mapWidth*PixelsPerTile,
    // mapHeight*PixelsPerTile) so every tile shows its own flat shade
    // (Nearest filter — no blur between neighbors).
    private static ImageTexture BuildGroundTexture(int mapWidth, int mapHeight, int seed)
    {
        var rng = new Random(seed);
        var img = Image.CreateEmpty(mapWidth, mapHeight, false, Image.Format.Rgba8);
        for (int y = 0; y < mapHeight; y++)
        {
            for (int x = 0; x < mapWidth; x++)
            {
                float n = (float)rng.NextDouble();
                float r = 0.20f + n * 0.10f;
                float g = 0.40f + n * 0.18f;
                float b = 0.16f + n * 0.10f;
                img.SetPixel(x, y, new Color(r, g, b));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    private void DrawDoorBlueprint(TilePos tile, float progress)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, DoorBlueprintFill, filled: true);
        DrawRect(rect, DoorBlueprintBorder, filled: false, width: 2f);
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

    private void DrawDoor(StruggleGame.Sim.Snapshots.DoorRenderState door)
    {
        // Door panel fills the whole tile when closed and swings on a
        // hinge at one tile edge. No separate jamb decoration — the
        // entire tile is the door.
        float cx = (door.Tile.X + 0.5f) * PixelsPerTile;
        float cy = (door.Tile.Y + 0.5f) * PixelsPerTile;
        float panelLen = PixelsPerTile;
        float panelThick = PixelsPerTile * 0.30f;

        float angle = door.OpenAmount * (Mathf.Pi * 0.5f);
        Vector2 pivot;
        Vector2 closedDir;
        if (door.Orientation == DoorOrientation.Horizontal)
        {
            pivot = new Vector2(cx - PixelsPerTile * 0.5f, cy);
            closedDir = new Vector2(1f, 0f);
        }
        else
        {
            pivot = new Vector2(cx, cy - PixelsPerTile * 0.5f);
            closedDir = new Vector2(0f, 1f);
        }
        var perp = new Vector2(-closedDir.Y, closedDir.X);
        var dir = new Vector2(
            closedDir.X * Mathf.Cos(angle) - perp.X * Mathf.Sin(angle),
            closedDir.Y * Mathf.Cos(angle) - perp.Y * Mathf.Sin(angle));
        var perpDir = new Vector2(-dir.Y, dir.X);
        var p0 = pivot - perpDir * (panelThick * 0.5f);
        var p1 = pivot + perpDir * (panelThick * 0.5f);
        var p2 = pivot + dir * panelLen + perpDir * (panelThick * 0.5f);
        var p3 = pivot + dir * panelLen - perpDir * (panelThick * 0.5f);
        var pts = new Vector2[] { p0, p1, p2, p3 };
        DrawColoredPolygon(pts, DoorPanelColor);
        DrawLine(p0, p1, DoorPanelEdge, width: 2f);
        DrawLine(p1, p2, DoorPanelEdge, width: 2f);
        DrawLine(p2, p3, DoorPanelEdge, width: 2f);
        DrawLine(p3, p0, DoorPanelEdge, width: 2f);

        if (door.Forbidden)
        {
            // Red X over the tile so the player can see at a glance which
            // doors are walled off. Drawn on the tile rect (not the swung
            // panel) so it stays readable regardless of open amount.
            float left = door.Tile.X * PixelsPerTile;
            float top = door.Tile.Y * PixelsPerTile;
            float right = left + PixelsPerTile;
            float bottom = top + PixelsPerTile;
            float inset = PixelsPerTile * 0.18f;
            DrawLine(new Vector2(left + inset, top + inset), new Vector2(right - inset, bottom - inset), DoorForbidMark, width: 3f);
            DrawLine(new Vector2(right - inset, top + inset), new Vector2(left + inset, bottom - inset), DoorForbidMark, width: 3f);
        }
    }

    // Faint yellow fill over every tile in the zone, plus a 1px outline
    // on the perimeter edges (edges that face a tile outside the zone).
    // Iterating per-tile + per-edge keeps compound shapes correct without
    // an extra polygon pass.
    private void DrawStockpile(StruggleGame.Sim.Snapshots.StockpileState sp, bool isSelected)
    {
        var set = new HashSet<TilePos>(sp.Tiles);
        var border = isSelected ? StockpileSelectedBorder : StockpileBorder;
        float borderW = isSelected ? 3f : 2f;
        foreach (var t in sp.Tiles)
        {
            var rect = new Rect2(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
            DrawRect(rect, StockpileFill, filled: true);
        }
        foreach (var t in sp.Tiles)
        {
            float x0 = t.X * PixelsPerTile;
            float y0 = t.Y * PixelsPerTile;
            float x1 = x0 + PixelsPerTile;
            float y1 = y0 + PixelsPerTile;
            if (!set.Contains(new TilePos(t.X, t.Y - 1)))
                DrawLine(new Vector2(x0, y0), new Vector2(x1, y0), border, width: borderW);
            if (!set.Contains(new TilePos(t.X, t.Y + 1)))
                DrawLine(new Vector2(x0, y1), new Vector2(x1, y1), border, width: borderW);
            if (!set.Contains(new TilePos(t.X - 1, t.Y)))
                DrawLine(new Vector2(x0, y0), new Vector2(x0, y1), border, width: borderW);
            if (!set.Contains(new TilePos(t.X + 1, t.Y)))
                DrawLine(new Vector2(x1, y0), new Vector2(x1, y1), border, width: borderW);
        }
    }

    // Reuses the cached HashSet when the snapshot ships the same Selected
    // array reference as last frame. Snapshot publishing reuses the
    // Array.Empty<int>() singleton for the "nothing selected" case, so the
    // common path hits the cache + allocates nothing.
    private static HashSet<int>? GetCachedSelectedSet(int[] ids, ref int[]? cachedRef, ref HashSet<int>? cachedSet)
    {
        if (ids.Length == 0) { cachedRef = ids; cachedSet = null; return null; }
        if (ReferenceEquals(cachedRef, ids)) return cachedSet;
        cachedRef = ids;
        cachedSet = new HashSet<int>(ids);
        return cachedSet;
    }

    private void DrawGrowZone(StruggleGame.Sim.Snapshots.GrowZoneState gz, bool isSelected)
    {
        var set = new HashSet<TilePos>(gz.Tiles);
        var border = isSelected ? GrowZoneSelectedBorder : GrowZoneBorder;
        float borderW = isSelected ? 3f : 2f;
        foreach (var t in gz.Tiles)
        {
            var rect = new Rect2(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
            DrawRect(rect, GrowZoneFill, filled: true);
        }
        foreach (var t in gz.Tiles)
        {
            float x0 = t.X * PixelsPerTile;
            float y0 = t.Y * PixelsPerTile;
            float x1 = x0 + PixelsPerTile;
            float y1 = y0 + PixelsPerTile;
            if (!set.Contains(new TilePos(t.X, t.Y - 1)))
                DrawLine(new Vector2(x0, y0), new Vector2(x1, y0), border, width: borderW);
            if (!set.Contains(new TilePos(t.X, t.Y + 1)))
                DrawLine(new Vector2(x0, y1), new Vector2(x1, y1), border, width: borderW);
            if (!set.Contains(new TilePos(t.X - 1, t.Y)))
                DrawLine(new Vector2(x0, y0), new Vector2(x0, y1), border, width: borderW);
            if (!set.Contains(new TilePos(t.X + 1, t.Y)))
                DrawLine(new Vector2(x1, y0), new Vector2(x1, y1), border, width: borderW);
        }
    }

    // Cyan ring around a selected tile (wall / door / blueprint / job).
    // Two pixels inset so it doesn't overdraw the tile's own border art.
    private void DrawSelectionOutline(TilePos tile)
    {
        float inset = 1.5f;
        var rect = new Rect2(
            tile.X * PixelsPerTile + inset,
            tile.Y * PixelsPerTile + inset,
            PixelsPerTile - inset * 2f,
            PixelsPerTile - inset * 2f);
        DrawRect(rect, SelectionOutline, filled: false, width: 3f);
    }

    // Red X over a tile — same look as the door forbid mark, reused for
    // blueprints / jobs the player has flagged Forbidden.
    private void DrawForbidX(TilePos tile)
    {
        float left = tile.X * PixelsPerTile;
        float top = tile.Y * PixelsPerTile;
        float right = left + PixelsPerTile;
        float bottom = top + PixelsPerTile;
        float inset = PixelsPerTile * 0.18f;
        DrawLine(new Vector2(left + inset, top + inset), new Vector2(right - inset, bottom - inset), DoorForbidMark, width: 3f);
        DrawLine(new Vector2(right - inset, top + inset), new Vector2(left + inset, bottom - inset), DoorForbidMark, width: 3f);
    }

    private static readonly Color RoofBpFill = new(0.20f, 0.55f, 0.95f, 0.22f);
    private static readonly Color RoofBpBorder = new(0.55f, 0.85f, 1.00f, 0.95f);
    private static readonly Color RoofRemoveFill = new(0.95f, 0.30f, 0.20f, 0.22f);
    private static readonly Color RoofRemoveBorder = new(1.00f, 0.55f, 0.45f, 0.95f);

    private void DrawRoofBlueprint(TilePos tile, float progress, bool build)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, build ? RoofBpFill : RoofRemoveFill, filled: true);
        DrawRect(rect, build ? RoofBpBorder : RoofRemoveBorder, filled: false, width: 2f);
        // Remove jobs paint a small X so a teardown reads differently
        // from a build at a glance.
        if (!build)
        {
            float inset = PixelsPerTile * 0.22f;
            var a = new Vector2(rect.Position.X + inset, rect.Position.Y + inset);
            var b = new Vector2(rect.Position.X + PixelsPerTile - inset, rect.Position.Y + PixelsPerTile - inset);
            var c = new Vector2(rect.Position.X + PixelsPerTile - inset, rect.Position.Y + inset);
            var d = new Vector2(rect.Position.X + inset, rect.Position.Y + PixelsPerTile - inset);
            DrawLine(a, b, RoofRemoveBorder, width: 2f);
            DrawLine(c, d, RoofRemoveBorder, width: 2f);
        }
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

    // Draws floored tiles as per-tile wood rects with two darker plank
    // lines. Iterates the cached _floorBytes; skips empty tiles. Cheap
    // even for big maps because the cost scales with floored-tile count,
    // not map area.
    private void DrawFlooringTiles()
    {
        if (_floorBytes is null) return;
        int plankY1 = PixelsPerTile / 3;
        int plankY2 = (PixelsPerTile * 2) / 3;
        for (int ty = 0; ty < _mapHeight; ty++)
        {
            int row = ty * _mapWidth;
            float oy = ty * PixelsPerTile;
            for (int tx = 0; tx < _mapWidth; tx++)
            {
                if (_floorBytes[row + tx] == 0) continue;
                float ox = tx * PixelsPerTile;
                DrawRect(new Rect2(ox, oy, PixelsPerTile, PixelsPerTile), WoodFloorColor, filled: true);
                DrawRect(new Rect2(ox, oy + plankY1, PixelsPerTile, 1f), WoodFloorPlank, filled: true);
                DrawRect(new Rect2(ox, oy + plankY2, PixelsPerTile, 1f), WoodFloorPlank, filled: true);
            }
        }
    }

    // Per-tile translucent tint, one hue per room id. Skips id 0
    // (barrier + outdoor — RoomMap.Compute already remaps every
    // border-touching component to 0). Deterministic golden-angle hue
    // picker keeps adjacent rooms visually distinct.
    private static ImageTexture BuildRoomOverlay(int[] roomTiles, int width, int height)
    {
        var img = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var transparent = new Color(0f, 0f, 0f, 0f);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int id = roomTiles[y * width + x];
                if (id == 0)
                {
                    img.SetPixel(x, y, transparent);
                    continue;
                }
                // Golden-angle hue spread keeps colors well separated.
                float hue = (id * 0.6180339887f) % 1f;
                var c = Color.FromHsv(hue, 0.55f, 1.0f);
                c.A = 0.40f;
                img.SetPixel(x, y, c);
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

    // Roof overlay: dark translucent tile per roofed cell. Sits on top
    // of walls so a roofed wall looks fractionally darker than an
    // unroofed one — readable but not noisy.
    private static readonly Color RoofTint = new(0.05f, 0.05f, 0.08f, 0.35f);
    private static ImageTexture BuildRoofOverlay(byte[] tiles, int width, int height)
    {
        var img = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var transparent = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                img.SetPixel(x, y, tiles[row + x] != 0 ? RoofTint : transparent);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }

    // No-roof overlay: yellow tint with checker dithering so the
    // forbidden cells are obviously "tagged" rather than just colored.
    // Checker = on for (x+y) odd.
    private static readonly Color NoRoofTintA = new(1.00f, 0.85f, 0.20f, 0.40f);
    private static readonly Color NoRoofTintB = new(1.00f, 0.85f, 0.20f, 0.15f);
    private static ImageTexture BuildNoRoofOverlay(byte[] tiles, int width, int height)
    {
        var img = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        var transparent = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (tiles[row + x] == 0) { img.SetPixel(x, y, transparent); continue; }
                img.SetPixel(x, y, ((x + y) & 1) == 0 ? NoRoofTintA : NoRoofTintB);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
