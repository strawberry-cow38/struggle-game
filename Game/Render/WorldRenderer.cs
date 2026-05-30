using Godot;
using StruggleGame.Game.Debug;
using StruggleGame.Sim;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;
using TileMap = StruggleGame.Sim.Map.TileMap;

namespace StruggleGame.Game.Render;

// Renders the static tile map (tileable noisy dirt ground + a wall
// overlay rebuilt whenever SimSnapshot.MapVersion changes), the
// pending blueprints from the snapshot, and the dynamic dummies on
// top.
public partial class WorldRenderer : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    // Tileable noisy dirt PNG (assets/ground/dirt.png), tiled across the
    // whole map with Nearest filter so the texel grit stays crisp.
    private ImageTexture? _groundTex;
    private ImageTexture? _wallOverlayTex;
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
    private long _lastRoofVersion = -1;
    private byte[]? _lastWallBytes;
    private ImageTexture? _noRoofOverlayTex;
    // Visual-only lighting layer (CanvasModulate + per-lamp Light2D +
    // per-wall LightOccluder2D). Sim's per-tile RGB grid stays for
    // gameplay; this node mirrors lamp/wall state from the snapshot to
    // drive Godot's stock 2D lighting for the pretty visual.
    private VisualLighting? _visualLighting;

    // Pre-rendered 64×64 wall sprites for each of the 256 neighbor
    // masks (low nibble = NESW cardinals, high nibble = NE/SE/SW/NW
    // diagonals). Pinch-corner geometry is baked per-variant in Blender.
    // When all 256 load, BuildWallOverlay skips every wall
    // tile and we stamp the correct sprite per tile based on the
    // tile's neighbor mask. Missing textures fall back to the
    // procedural brick overlay for that tile.
    private readonly Texture2D?[] _wallTextures = new Texture2D?[256];
    private Node2D? _wallSpritesRoot;
    private readonly Dictionary<TilePos, Sprite2D> _wallSprites = new();

    // Snapshot pair used for render-side interpolation. _prevSnap is the
    // last snapshot we drew from, _currSnap is the next one. We render at
    // alpha across the wall-clock interval between them so visible motion
    // is smooth even when the render rate is much lower than the sim's
    // tick rate (which would otherwise show snapshot-to-snapshot jumps).
    private SimSnapshot? _prevSnap;
    private SimSnapshot? _currSnap;
    private ulong _currSnapStartMs;
    private Dictionary<int, DummyState>? _prevDummyByIdScratch;
    private HashSet<int>? _selectedDummyIdsScratch;
    private Dictionary<TilePos, DoorRenderState>? _prevDoorByTileScratch;

    // Selection sets are cached by reference identity of the snapshot's
    // SelectedXxxIds array — when the underlying array doesn't change
    // between snapshots we skip rebuilding the HashSet (avoids per-frame
    // GC churn when nothing's been clicked).
    private int[]? _cachedSelectedTreeIdRef;
    private HashSet<int>? _cachedSelectedTreeSet;
    private int[]? _cachedSelectedWoodIdRef;
    private HashSet<int>? _cachedSelectedWoodSet;
    private int[]? _cachedSelectedCropIdRef;
    private HashSet<int>? _cachedSelectedCropSet;

    // Wall base color — brown brick mid-tone. Old (0.18, 0.16, 0.14)
    // was too dark for the mul overlay swing to read; the bumped 0.60
    // looked washed out / white. Settled on a clearer brown that holds
    // brick identity at lit (0.42, 0.32, 0.22) and still has visible
    // shadow swing (mul ambient 0.55 → ~0.23, 0.18, 0.12).
    private static readonly Color WallColor = new(0.42f, 0.32f, 0.22f);
    private static readonly Color DummyColor = new(0.95f, 0.55f, 0.20f);
    private static readonly Color BlueprintFill = new(0.20f, 0.55f, 0.95f, 0.30f);
    private static readonly Color BlueprintBorder = new(0.45f, 0.75f, 1.00f, 0.85f);
    private static readonly Color BlueprintProgress = new(0.95f, 0.85f, 0.20f, 0.85f);
    private static readonly Color SelectionRing = new(1.0f, 1.0f, 0.20f, 1.0f);
    private static readonly Color PathLineColor = new(1.0f, 0.92f, 0.10f, 0.85f);
    private static readonly Color PathTargetColor = new(1.0f, 0.92f, 0.10f, 1.0f);
    private static readonly Color DraftedRing = new(1.0f, 0.25f, 0.20f, 1.0f);
    private static readonly Color EquippedMarkerColor = new(0.30f, 0.85f, 1.0f, 1.0f);
    private static readonly Color FacingArrowColor = new(0.15f, 0.10f, 0.05f, 0.9f);
    private static readonly Color CorpseColor = new(0.45f, 0.42f, 0.40f, 1f);
    private static readonly Color CorpseXColor = new(0.95f, 0.10f, 0.10f, 1f);

    // A dead colonist: a greyed-out body disc with a big red X over it.
    private void DrawCorpse(TilePos tile)
    {
        var center = new Vector2((tile.X + 0.5f) * PixelsPerTile, (tile.Y + 0.5f) * PixelsPerTile);
        float radius = PixelsPerTile * 0.32f;
        DrawCircle(center, radius, CorpseColor);
        float s = radius * 1.1f;
        DrawLine(new Vector2(center.X - s, center.Y - s), new Vector2(center.X + s, center.Y + s), CorpseXColor, 3f, antialiased: true);
        DrawLine(new Vector2(center.X - s, center.Y + s), new Vector2(center.X + s, center.Y - s), CorpseXColor, 3f, antialiased: true);
    }
    private readonly Vector2[] _facingTri = new Vector2[3];
    // Reused polygon scratch so the per-pawn / per-crop draw loops don't
    // heap-allocate a fresh Vector2[] every frame. DrawColoredPolygon
    // copies the points immediately, so a shared buffer is safe.
    private readonly Vector2[] _equipDiamondPts = new Vector2[4];
    private readonly Vector2[] _cropTriPts = new Vector2[3];
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
    private static readonly Color ProgressBarBg = new(0f, 0f, 0f, 0.6f);
    private static readonly Color ProgressBarFg = new(1f, 0.9f, 0.2f, 1f);
    private static readonly Color JobLabelColor = new(1f, 1f, 1f, 0.95f);

    // Render hot-path scratch — reused across _Draw calls so the per-frame
    // path doesn't allocate. _zoneScratch is shared between DrawStockpile
    // and DrawGrowZone since the two never run nested.
    private Font? _fallbackFont;
    private readonly Dictionary<string, Vector2> _jobLabelSizes = new();
    private readonly HashSet<TilePos> _zoneScratch = new();
    private readonly Vector2[] _doorPts = new Vector2[4];

    public SimHost? Host { get; set; }

    public override void _Ready()
    {
        TextureFilter = TextureFilterEnum.Nearest;
        TextureRepeat = TextureRepeatEnum.Enabled;
        _fallbackFont = ThemeDB.FallbackFont;

        if (Host is null) return;
        _mapWidth = Host.Map.Width;
        _mapHeight = Host.Map.Height;
        _mapPixelWidth = _mapWidth * PixelsPerTile;
        _mapPixelHeight = _mapHeight * PixelsPerTile;
        _groundTex = LoadGroundTexture("res://assets/ground/dirt.png");

        _wallSpritesRoot = new Node2D { Name = "WallSprites", TextureFilter = TextureFilterEnum.Nearest };
        AddChild(_wallSpritesRoot);
        for (int m = 0; m < 256; m++)
        {
            string bits = Convert.ToString(m, 2).PadLeft(8, '0');
            _wallTextures[m] = LoadWallTexture($"res://assets/walls/wall_{bits}.png");
        }

        _visualLighting = new VisualLighting { Host = Host, MapWidth = _mapWidth, MapHeight = _mapHeight };
        AddChild(_visualLighting);
    }

    private static Texture2D? LoadWallTexture(string path)
    {
        var img = new Image();
        var err = img.Load(ProjectSettings.GlobalizePath(path));
        if (err != Error.Ok) return null;
        return ImageTexture.CreateFromImage(img);
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
        _visualLighting?.Tick();
    }

    public override void _Draw()
    {
        if (_groundTex is null || Host is null) return;

        FrameProfiler.Instance.BeginFrame();

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

        // Wall overlay = unlit brick stamps per wall tile. All lighting
        // (sun, lamps, ambient floor) is applied by the VisualLighting
        // multiply overlay sitting on top, so wall bake only rebuilds
        // when the wall geometry itself changes (MapVersion). Sun ticks
        // during dawn/dusk previously thrashed FPS by re-baking every
        // texel — now they're free on the renderer side too.
        if (snap is not null && snap.MapVersion != _lastMapVersion)
        {
            _lastWallBytes = Host!.CopyLayerForRender(MapLayer.Wall);
            _floorBytes = Host!.CopyLayerForRender(MapLayer.Flooring);
            _lastMapVersion = snap.MapVersion;
            if (_lastWallBytes is not null)
            {
                _wallOverlayTex = BuildWallOverlay(_lastWallBytes, _mapWidth, _mapHeight);
                UpdateWallSprites(_lastWallBytes, _mapWidth, _mapHeight);
            }
        }
        if (snap is not null && snap.RoofVersion != _lastRoofVersion)
        {
            var noRoofBytes = Host!.CopyNoRoofTilesForRender();
            _noRoofOverlayTex = BuildNoRoofOverlay(noRoofBytes, _mapWidth, _mapHeight);
            _lastRoofVersion = snap.RoofVersion;
        }

        var mapRect = new Rect2(0, 0, _mapPixelWidth, _mapPixelHeight);
        using (FrameProfiler.Instance.BeginScope("Map"))
        {
            DrawTextureRect(_groundTex, mapRect, tile: true);
            DrawFlooringTiles();
            if (_wallOverlayTex is not null)
            {
                DrawTextureRect(_wallOverlayTex, mapRect, tile: false);
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
                DrawBlueprint(bp.Tile, bp.Progress, bp.Funding);
                if (bp.Forbidden) DrawForbidX(bp.Tile);
            }

            foreach (var fbp in snap.FloorBlueprints)
            {
                if (fbp.Tile.X < viewMinTileX || fbp.Tile.X > viewMaxTileX
                    || fbp.Tile.Y < viewMinTileY || fbp.Tile.Y > viewMaxTileY) continue;
                DrawFloorBlueprint(fbp.Tile, fbp.Progress, fbp.Funding);
                if (fbp.Forbidden) DrawForbidX(fbp.Tile);
            }

            foreach (var dbp in snap.DoorBlueprints)
            {
                if (dbp.Tile.X < viewMinTileX || dbp.Tile.X > viewMaxTileX
                    || dbp.Tile.Y < viewMinTileY || dbp.Tile.Y > viewMaxTileY) continue;
                DrawDoorBlueprint(dbp.Tile, dbp.Progress, dbp.Funding);
                if (dbp.Forbidden) DrawForbidX(dbp.Tile);
            }

            foreach (var rf in snap.RoofFlashes)
            {
                if (rf.Tile.X < viewMinTileX || rf.Tile.X > viewMaxTileX
                    || rf.Tile.Y < viewMinTileY || rf.Tile.Y > viewMaxTileY) continue;
                DrawRoofFlash(rf.Tile, rf.Alpha);
            }

            foreach (var lbp in snap.LampBlueprints)
            {
                if (lbp.Tile.X < viewMinTileX || lbp.Tile.X > viewMaxTileX
                    || lbp.Tile.Y < viewMinTileY || lbp.Tile.Y > viewMaxTileY) continue;
                DrawLampBlueprint(lbp.Tile, lbp.Progress, lbp.Funding);
                if (lbp.Forbidden) DrawForbidX(lbp.Tile);
            }

            foreach (var bbp in snap.BedBlueprints)
            {
                var foot = BedOrientations.Foot(bbp.Origin, bbp.Orientation);
                int minX = Math.Min(bbp.Origin.X, foot.X), maxX = Math.Max(bbp.Origin.X, foot.X);
                int minY = Math.Min(bbp.Origin.Y, foot.Y), maxY = Math.Max(bbp.Origin.Y, foot.Y);
                if (maxX < viewMinTileX || minX > viewMaxTileX
                    || maxY < viewMinTileY || minY > viewMaxTileY) continue;
                DrawBedBlueprint(bbp.Origin, bbp.Orientation, bbp.Progress, bbp.Funding);
                if (bbp.Forbidden) { DrawForbidX(bbp.Origin); DrawForbidX(foot); }
            }

            foreach (var ubp in snap.UrBoardBlueprints)
            {
                if (ubp.Tile.X < viewMinTileX || ubp.Tile.X > viewMaxTileX
                    || ubp.Tile.Y < viewMinTileY || ubp.Tile.Y > viewMaxTileY) continue;
                DrawUrBoardBlueprint(ubp.Tile, ubp.Progress, ubp.Funding);
                if (ubp.Forbidden) DrawForbidX(ubp.Tile);
            }

            foreach (var sbp in snap.StoveBlueprints)
            {
                DrawStoveBlueprint(sbp.Origin, sbp.Orientation, sbp.Progress, sbp.Funding);
                if (sbp.Forbidden) DrawForbidX(sbp.Origin);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Stoves"))
        {
            foreach (var s in snap.Stoves)
            {
                DrawStove(s.Origin, s.Orientation, s.CookProgress, s.CurrentBillIndex >= 0);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Beds"))
        {
            foreach (var bed in snap.Beds)
            {
                var origin = bed.Origin;
                var foot = BedOrientations.Foot(origin, bed.Orientation);
                int minX = Math.Min(origin.X, foot.X), maxX = Math.Max(origin.X, foot.X);
                int minY = Math.Min(origin.Y, foot.Y), maxY = Math.Max(origin.Y, foot.Y);
                if (maxX < viewMinTileX || minX > viewMaxTileX
                    || maxY < viewMinTileY || minY > viewMaxTileY) continue;
                DrawBed(origin, bed.Orientation);
            }
        }

        using (FrameProfiler.Instance.BeginScope("UrBoards"))
        {
            foreach (var ub in snap.UrBoards)
            {
                if (ub.Tile.X < viewMinTileX || ub.Tile.X > viewMaxTileX
                    || ub.Tile.Y < viewMinTileY || ub.Tile.Y > viewMaxTileY) continue;
                DrawUrBoard(ub.Tile);
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
                foreach (var t in Host.SelectedBlueprintTiles)
                {
                    DrawSelectionOutline(t);
                    foreach (var bbp in snap.BedBlueprints)
                    {
                        if (bbp.Origin == t)
                        {
                            DrawSelectionOutline(BedOrientations.Foot(bbp.Origin, bbp.Orientation));
                            break;
                        }
                    }
                }
                foreach (var t in Host.SelectedLampTiles) DrawSelectionOutline(t);
                foreach (var t in Host.SelectedUrBoardTiles) DrawSelectionOutline(t);
                foreach (var t in Host.SelectedBedTiles)
                {
                    DrawSelectionOutline(t);
                    foreach (var b in snap.Beds)
                    {
                        if (b.Origin == t)
                        {
                            DrawSelectionOutline(BedOrientations.Foot(b.Origin, b.Orientation));
                            break;
                        }
                    }
                }
                foreach (var t in Host.SelectedStoveTiles)
                {
                    foreach (var s in snap.Stoves)
                    {
                        if (s.Origin != t) continue;
                        foreach (var bt in StoveOrientations.BodyTiles(s.Origin, s.Orientation))
                            DrawSelectionOutline(bt);
                        DrawSelectionOutline(StoveOrientations.StandingTile(s.Origin, s.Orientation));
                        break;
                    }
                }
            }
        }

        var stackFont = _fallbackFont;
        var mouseLocal = GetLocalMousePosition();
        int cursorTileX = Mathf.FloorToInt(mouseLocal.X / PixelsPerTile);
        int cursorTileY = Mathf.FloorToInt(mouseLocal.Y / PixelsPerTile);

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
            var selectedCropSet = GetCachedSelectedSet(
                snap.SelectedCropIds, ref _cachedSelectedCropIdRef, ref _cachedSelectedCropSet);
            foreach (var c in snap.Crops)
            {
                if (c.Tile.X < viewMinTileX || c.Tile.X > viewMaxTileX
                    || c.Tile.Y < viewMinTileY || c.Tile.Y > viewMaxTileY) continue;
                DrawCrop(c, simpleLod);
                if (selectedCropSet is not null && selectedCropSet.Contains(c.EntityId)) DrawWoodSelectionRing(c.Tile);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Blood"))
        {
            foreach (var bp in snap.BloodPuddles)
            {
                if (bp.Tile.X < viewMinTileX || bp.Tile.X > viewMaxTileX
                    || bp.Tile.Y < viewMinTileY || bp.Tile.Y > viewMaxTileY) continue;
                DrawBloodPuddle(bp);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Projectiles"))
        {
            foreach (var pr in snap.Projectiles)
            {
                int ptx = (int)pr.X, pty = (int)pr.Y;
                if (ptx < viewMinTileX || ptx > viewMaxTileX
                    || pty < viewMinTileY || pty > viewMaxTileY) continue;
                DrawProjectile(pr);
            }
        }

        using (FrameProfiler.Instance.BeginScope("ItemPiles"))
        {
            // Item piles share the SelectedWoodIds selection set (cached, so
            // this re-fetch is free within the frame).
            var selectedPileSet = GetCachedSelectedSet(
                snap.SelectedWoodIds, ref _cachedSelectedWoodIdRef, ref _cachedSelectedWoodSet);
            foreach (var p in snap.ItemPiles)
            {
                if (p.Tile.X < viewMinTileX || p.Tile.X > viewMaxTileX
                    || p.Tile.Y < viewMinTileY || p.Tile.Y > viewMaxTileY) continue;
                DrawItemPile(p);
                if (p.Forbidden) DrawForbiddenMark(p.Tile);
                if (selectedPileSet is not null && selectedPileSet.Contains(p.EntityId)) DrawWoodSelectionRing(p.Tile);
                if (p.Tile.X == cursorTileX && p.Tile.Y == cursorTileY)
                {
                    DrawStackLabel(stackFont, p.Tile, p.ItemPath, p.Count, p.Label);
                }
            }
        }

        // Darkness sits above every world entity (doors, walls, trees,
        // crops, wood, item piles, blueprints, selection rings) so they
        // all dim under a roof. Pawns + path debug stay above darkness so
        // they remain readable in shadow. No-roof hatch goes on top of
        // darkness so the designator mark survives.
        // Per-tile RGB light is composited by the child _lightOverlaySprite
        // (mul blend, ZIndex=100) above this _Draw — see _Ready. We only
        // draw the no-roof designator hatch here so it survives the tint.
        var mapRectForDark = new Rect2(0, 0, _mapPixelWidth, _mapPixelHeight);
        using (FrameProfiler.Instance.BeginScope("NoRoofHatch"))
        {
            if (_noRoofOverlayTex is not null)
            {
                DrawTextureRect(_noRoofOverlayTex, mapRectForDark, tile: false);
            }
        }

        using (FrameProfiler.Instance.BeginScope("Lamps"))
        {
            // Drawn above darkness so a powered lamp visibly glows even
            // in an otherwise dim tile. Unpowered lamps still read as a
            // dim fixture for the same reason — readable in the dark.
            foreach (var lamp in snap.Lamps)
            {
                if (lamp.Tile.X < viewMinTileX || lamp.Tile.X > viewMaxTileX
                    || lamp.Tile.Y < viewMinTileY || lamp.Tile.Y > viewMaxTileY) continue;
                DrawLamp(lamp.Tile, lamp.PoweredOn, lamp.Color);
            }
        }

        float radius = PixelsPerTile * 0.35f;
        var labelFont = _fallbackFont;
        const int labelFontSize = 14;
        var labelOffset = new Vector2(0f, -PixelsPerTile * 0.6f);
        _prevDummyByIdScratch ??= new Dictionary<int, DummyState>();
        _prevDummyByIdScratch.Clear();
        if (_prevSnap is not null && interpAlpha < 1f)
        {
            foreach (var pd in _prevSnap.Dummies) _prevDummyByIdScratch[pd.EntityId] = pd;
        }
        _selectedDummyIdsScratch ??= new HashSet<int>();
        _selectedDummyIdsScratch.Clear();
        foreach (var sid in snap.SelectedDummyIds) _selectedDummyIdsScratch.Add(sid);
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
                // Combat juice: lunge forward on a swing. (No victim flinch.)
                {
                    var fdir = new Vector2(Mathf.Cos(d.Facing), Mathf.Sin(d.Facing));
                    long sinceSwing = snap.Tick - d.SwingTick;
                    if (d.SwingTick > 0 && sinceSwing >= 0 && sinceSwing < LungeTicks)
                        center += fdir * (Mathf.Sin(sinceSwing / (float)LungeTicks * Mathf.Pi) * PixelsPerTile * 0.30f);
                }
                DrawCircle(center, radius, DummyColor);
                // Facing arrow: a small triangle on the rim pointing the
                // way the colonist last moved.
                {
                    var dir = new Vector2(Mathf.Cos(d.Facing), Mathf.Sin(d.Facing));
                    var perp = new Vector2(-dir.Y, dir.X);
                    _facingTri[0] = center + dir * (radius * 1.15f);
                    _facingTri[1] = center + dir * (radius * 0.35f) + perp * (radius * 0.5f);
                    _facingTri[2] = center + dir * (radius * 0.35f) - perp * (radius * 0.5f);
                    DrawColoredPolygon(_facingTri, FacingArrowColor);
                }
                if (d.Drafted)
                {
                    DrawArc(center, radius + 2f, 0f, Mathf.Tau, 32, DraftedRing, 2f, antialiased: true);
                }
                long sinceShot = snap.Tick - d.ShotTick;
                if (d.HasRangedWeapon && d.ShotTick > 0 && sinceShot >= 0 && sinceShot < MuzzleFlashTicks)
                {
                    var fdir2 = new Vector2(Mathf.Cos(d.Facing), Mathf.Sin(d.Facing));
                    var muzzle = center + fdir2 * (radius + PixelsPerTile * 0.18f);
                    float fade = 1f - sinceShot / (float)MuzzleFlashTicks;
                    var fc = new Color(1f, 0.85f, 0.3f, fade);
                    DrawCircle(muzzle, PixelsPerTile * 0.12f * fade + 2f, fc);
                }
                if (d.Carrying)
                {
                    float logW = PixelsPerTile * 0.45f;
                    float logH = PixelsPerTile * 0.18f;
                    var carry = new Rect2(center.X - logW * 0.5f, center.Y - radius - logH - 1f, logW, logH);
                    DrawRect(carry, WoodColor, filled: true);
                    DrawRect(new Rect2(carry.Position + new Vector2(0, 1f), new Vector2(carry.Size.X, 2f)), WoodHighlight, filled: true);
                }
                if (d.Equipped.Length > 0)
                {
                    // Equipped-gear marker: a small cyan diamond pinned to
                    // the pawn's lower-right. Dummy stand-in until real
                    // per-item apparel sprites exist.
                    float s = PixelsPerTile * 0.16f;
                    var ec = new Vector2(center.X + radius * 0.6f, center.Y + radius * 0.6f);
                    _equipDiamondPts[0] = new Vector2(ec.X, ec.Y - s);
                    _equipDiamondPts[1] = new Vector2(ec.X + s, ec.Y);
                    _equipDiamondPts[2] = new Vector2(ec.X, ec.Y + s);
                    _equipDiamondPts[3] = new Vector2(ec.X - s, ec.Y);
                    DrawColoredPolygon(_equipDiamondPts, EquippedMarkerColor);
                }
                if (_selectedDummyIdsScratch.Contains(d.EntityId))
                {
                    DrawArc(center, radius + 5f, 0f, Mathf.Tau, 32, SelectionRing, 2f, antialiased: true);
                }
                string? labelText = d.Health.Unconscious ? "Unconscious"
                    : d.MeleeTargetId != 0 ? $"Melee Attacking Colonist {d.MeleeTargetId}"
                    : d.Sleeping ? "Sleeping"
                    : (string.IsNullOrEmpty(d.Job) ? null : d.Job);
                if (labelFont is not null && labelText is not null)
                {
                    // Cache label width by string — set of distinct labels
                    // is tiny (Idle/Drafted/Haul/WallBuild/Sleeping/etc.)
                    // and GetStringSize is a non-trivial Godot text-shape
                    // call. Without the cache it's invoked per visible
                    // pawn per frame.
                    if (!_jobLabelSizes.TryGetValue(labelText, out var textSize))
                    {
                        textSize = labelFont.GetStringSize(labelText, HorizontalAlignment.Center, -1f, labelFontSize);
                        _jobLabelSizes[labelText] = textSize;
                    }
                    var anchor = center + labelOffset - new Vector2(textSize.X * 0.5f, 0f);
                    DrawString(labelFont, anchor, labelText, HorizontalAlignment.Left, -1f, labelFontSize,
                        JobLabelColor);
                }
                if (d.Sleeping && !d.Health.Unconscious && labelFont is not null)
                {
                    DrawSleepZs(labelFont, center, radius);
                }
                if (d.Health.Unconscious && labelFont is not null)
                {
                    DrawDownedExclaims(labelFont, center, radius);
                }
                long sinceMiss = snap.Tick - d.MissTick;
                if (d.MissTick > 0 && sinceMiss >= 0 && sinceMiss < MissTextTicks && labelFont is not null)
                {
                    DrawMissedText(labelFont, center, radius, 1f - sinceMiss / (float)MissTextTicks);
                }
            }
        }

        using (FrameProfiler.Instance.BeginScope("Path"))
        {
            DrawSelectedPath(snap);
        }

        FrameProfiler.Instance.EndFrame();
    }

    // Three "Z" glyphs streaming up + slightly right from the sleeper's
    // head. Cycle = SleepZCycleMs; the three glyphs are 1/3 of a cycle
    // apart so a fresh Z spawns about every half-second. Alpha fades
    // linearly so the older Zs dissolve as they drift.
    private const int SleepZFontSize = 26;
    private const float SleepZCycleMs = 1500f;
    private static readonly Color SleepZColor = new(0.85f, 0.95f, 1f, 1f);

    private void DrawSleepZs(Font font, Vector2 center, float radius)
    {
        float now = Time.GetTicksMsec();
        var head = new Vector2(center.X, center.Y - radius);
        for (int i = 0; i < 3; i++)
        {
            float phase = ((now + i * (SleepZCycleMs / 3f)) % SleepZCycleMs) / SleepZCycleMs;
            float drift = PixelsPerTile * 0.55f;
            var p = head + new Vector2(phase * PixelsPerTile * 0.25f, -phase * drift - 2f);
            var col = SleepZColor;
            col.A = 1f - phase;
            DrawString(font, p, "Z", HorizontalAlignment.Left, -1f, SleepZFontSize, col);
        }
    }

    // Combat anim windows (sim ticks).
    private const long MuzzleFlashTicks = 5;
    private const long LungeTicks = 8;
    private const long FlinchTicks = 8;
    private const long MissTextTicks = 45;
    private const int MissedFontSize = 24;
    private static readonly Color MissedColor = new(0.85f, 0.85f, 0.9f, 1f);

    // "Missed!" floating up + fading from the attacker on a whiff.
    private void DrawMissedText(Font font, Vector2 center, float radius, float fade)
    {
        var col = MissedColor;
        col.A = Mathf.Clamp(fade, 0f, 1f);
        float rise = (1f - fade) * PixelsPerTile * 0.5f;
        var p = new Vector2(center.X - PixelsPerTile * 0.35f, center.Y - radius - 4f - rise);
        DrawString(font, p, "Missed!", HorizontalAlignment.Left, -1f, MissedFontSize, col);
    }

    // Alarm "!" glyphs streaming up from a downed colonist's head — the
    // unconscious counterpart to the sleeper's Zs.
    private const int DownedFontSize = 28;
    private const float DownedCycleMs = 1100f;
    private static readonly Color DownedColor = new(1f, 0.35f, 0.30f, 1f);

    private void DrawDownedExclaims(Font font, Vector2 center, float radius)
    {
        float now = Time.GetTicksMsec();
        var head = new Vector2(center.X, center.Y - radius);
        for (int i = 0; i < 3; i++)
        {
            float phase = ((now + i * (DownedCycleMs / 3f)) % DownedCycleMs) / DownedCycleMs;
            float drift = PixelsPerTile * 0.55f;
            var p = head + new Vector2(-phase * PixelsPerTile * 0.25f, -phase * drift - 2f);
            var col = DownedColor;
            col.A = 1f - phase;
            DrawString(font, p, "!", HorizontalAlignment.Left, -1f, DownedFontSize, col);
        }
    }

    // Stack label rendered just below a dropped item: white "Name xCount"
    // text drawn in world-space (scales with camera zoom). Only the
    // stack on the exact hovered tile gets labelled so dense yards don't
    // drown in text.
    private const int StackLabelFontSize = 25;

    private void DrawStackLabel(Font? font, TilePos tile, string itemPath, int count, string? labelOverride = null)
    {
        if (font is null) return;
        string name = labelOverride ?? (ItemCatalog.ItemsByPath.TryGetValue(itemPath, out var def)
            ? def.DisplayName
            : itemPath);
        // Corpse labels are already the colonist name; don't append a count.
        string text = labelOverride is not null ? name : $"{name} x{count}";
        var size = font.GetStringSize(text, HorizontalAlignment.Left, -1f, StackLabelFontSize);
        float cx = (tile.X + 0.5f) * PixelsPerTile;
        float baseY = (tile.Y + 0.5f) * PixelsPerTile + PixelsPerTile * 0.22f + font.GetAscent(StackLabelFontSize);
        var pos = new Vector2(cx - size.X * 0.5f, baseY);
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
                DrawRect(barBg, ProgressBarBg, filled: true);
                var barFg = new Rect2(barBg.Position, new Vector2(bw * Mathf.Clamp(t.ChopProgress, 0f, 1f), bh));
                DrawRect(barFg, ProgressBarFg, filled: true);
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
        _cropTriPts[0] = p0; _cropTriPts[1] = p1; _cropTriPts[2] = p2;
        DrawColoredPolygon(_cropTriPts, CarrotBody);
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
                DrawRect(barBg, ProgressBarBg, filled: true);
                var barFg = new Rect2(barBg.Position, new Vector2(bw * Mathf.Clamp(c.WorkProgress, 0f, 1f), bh));
                DrawRect(barFg, ProgressBarFg, filled: true);
            }
        }
    }

    private static readonly Color BloodColor = new(0.45f, 0.03f, 0.03f);

    private static readonly Color BulletColor = new(1.0f, 0.92f, 0.45f);
    private static readonly Color BulletApColor = new(0.75f, 0.85f, 1.0f);

    private void DrawProjectile(Sim.Snapshots.ProjectileState pr)
    {
        var head = new Vector2(pr.X * PixelsPerTile, pr.Y * PixelsPerTile);
        var dir = new Vector2(Mathf.Cos(pr.Angle), Mathf.Sin(pr.Angle));
        var tail = head - dir * (PixelsPerTile * 0.45f);
        var col = pr.IsAp ? BulletApColor : BulletColor;
        DrawLine(tail, head, col, 2.0f, antialiased: true);
        DrawCircle(head, 2.5f, col);
    }

    private void DrawBloodPuddle(Sim.Snapshots.BloodPuddleState bp)
    {
        var center = new Vector2((bp.Tile.X + 0.5f) * PixelsPerTile, (bp.Tile.Y + 0.5f) * PixelsPerTile);
        float r = PixelsPerTile * (0.18f + 0.27f * Mathf.Clamp(bp.Amount, 0f, 1f));
        var col = BloodColor;
        col.A = 0.45f + 0.4f * Mathf.Clamp(bp.Amount, 0f, 1f);
        DrawCircle(center, r, col);
    }

    private void DrawItemPile(Sim.Snapshots.ItemPileState p)
    {
        // Path-dispatch the look: wood = log, corpse = body + red X,
        // everything else = a small stacked dot.
        if (p.ItemPath == ItemCatalog.Corpse.FullPath) { DrawCorpse(p.Tile); return; }
        if (p.ItemPath == ItemCatalog.Wood.FullPath) { DrawWood(p.Tile); return; }
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
            DrawRect(bg, ProgressBarBg, filled: true);
            var fg = new Rect2(bg.Position, new Vector2(bw * Mathf.Clamp(progress, 0f, 1f), bh));
            DrawRect(fg, DeconProgress, filled: true);
        }
    }

    // Dims the blueprint fill toward transparent when funding is low so
    // the player can spot unfunded blueprints at a glance. At funding=1
    // we return the base color unchanged; at funding=0 the alpha drops
    // to a quarter of its original value (still visible, but clearly
    // pending). RGB is left alone — the hue still identifies the kind.
    private static Color FundedFill(Color baseFill, float funding)
    {
        float f = Mathf.Clamp(funding, 0f, 1f);
        return new Color(baseFill.R, baseFill.G, baseFill.B, baseFill.A * (0.25f + 0.75f * f));
    }

    private void DrawBlueprint(TilePos tile, float progress, float funding)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, FundedFill(BlueprintFill, funding), filled: true);
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

    // Tileable noisy dirt PNG, repeated across the whole map. Nearest
    // filter on the renderer keeps the texel grit crisp.
    private static ImageTexture? LoadGroundTexture(string path)
    {
        var img = new Image();
        var err = img.Load(ProjectSettings.GlobalizePath(path));
        if (err != Error.Ok) return null;
        return ImageTexture.CreateFromImage(img);
    }

    private void DrawDoorBlueprint(TilePos tile, float progress, float funding)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, FundedFill(DoorBlueprintFill, funding), filled: true);
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
        _doorPts[0] = p0;
        _doorPts[1] = p1;
        _doorPts[2] = p2;
        _doorPts[3] = p3;
        DrawColoredPolygon(_doorPts, DoorPanelColor);
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
        var set = _zoneScratch;
        set.Clear();
        foreach (var t in sp.Tiles) set.Add(t);
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
        var set = _zoneScratch;
        set.Clear();
        foreach (var t in gz.Tiles) set.Add(t);
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

    private static readonly Color RoofBpBorder = new(0.78f, 0.82f, 0.88f, 0.95f);
    private static readonly Color RoofMetalBase = new(0.58f, 0.60f, 0.64f, 1.00f);
    private static readonly Color RoofMetalRibDark = new(0.34f, 0.36f, 0.40f, 1.00f);
    private static readonly Color RoofMetalRibLight = new(0.82f, 0.86f, 0.92f, 1.00f);
    private static readonly Color RoofRemoveFill = new(0.95f, 0.30f, 0.20f, 0.22f);
    private static readonly Color RoofRemoveBorder = new(1.00f, 0.55f, 0.45f, 0.95f);
    private const int RoofMetalRibs = 5;

    private void DrawRoofFlash(TilePos tile, float alpha)
    {
        // Brief corrugated sheet-metal flash on RoofBuild completion. Alpha
        // ticks down from 1 to 0 over RoofFlashSec; the roof bit is already
        // set so darkness layers under the flash.
        if (alpha <= 0f) return;
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        var baseCol = RoofMetalBase; baseCol.A *= alpha;
        DrawRect(rect, baseCol, filled: true);
        float ribW = PixelsPerTile / (float)RoofMetalRibs;
        for (int i = 0; i < RoofMetalRibs; i++)
        {
            float rx = rect.Position.X + i * ribW;
            float highlightW = ribW * 0.18f;
            float shadowW = ribW * 0.22f;
            var hi = RoofMetalRibLight; hi.A *= alpha;
            var lo = RoofMetalRibDark; lo.A *= alpha;
            DrawRect(new Rect2(rx, rect.Position.Y, highlightW, PixelsPerTile), hi, filled: true);
            DrawRect(new Rect2(rx + ribW - shadowW, rect.Position.Y, shadowW, PixelsPerTile), lo, filled: true);
        }
    }

    private static readonly Color LampBpFill = new(1.00f, 0.85f, 0.30f, 0.22f);
    private static readonly Color LampBpBorder = new(1.00f, 0.95f, 0.55f, 0.85f);
    private static readonly Color LampBaseColor = new(0.35f, 0.30f, 0.22f, 1f);
    private static readonly Color LampLitColor = new(1.00f, 0.95f, 0.55f, 1f);
    private static readonly Color LampLitHalo = new(1.00f, 0.90f, 0.45f, 0.45f);
    private static readonly Color LampOffColor = new(0.45f, 0.42f, 0.32f, 1f);

    private static readonly Color BedFrame   = new(0.42f, 0.28f, 0.18f, 1f);
    private static readonly Color BedSheet   = new(0.75f, 0.55f, 0.45f, 1f);
    private static readonly Color BedPillow  = new(0.96f, 0.94f, 0.90f, 1f);
    private static readonly Color BedShadow  = new(0.0f, 0.0f, 0.0f, 0.20f);

    private void DrawBed(TilePos origin, BedOrientation orientation)
    {
        var foot = BedOrientations.Foot(origin, orientation);
        int minX = Math.Min(origin.X, foot.X);
        int minY = Math.Min(origin.Y, foot.Y);
        int maxX = Math.Max(origin.X, foot.X);
        int maxY = Math.Max(origin.Y, foot.Y);
        float x0 = minX * PixelsPerTile;
        float y0 = minY * PixelsPerTile;
        float w = (maxX - minX + 1) * PixelsPerTile;
        float h = (maxY - minY + 1) * PixelsPerTile;

        float inset = PixelsPerTile * 0.08f;
        var frame = new Rect2(x0 + inset, y0 + inset, w - 2 * inset, h - 2 * inset);
        // Soft drop shadow under the frame.
        var shadow = new Rect2(frame.Position.X + 2, frame.Position.Y + 3, frame.Size.X, frame.Size.Y);
        DrawRect(shadow, BedShadow, filled: true);

        DrawRect(frame, BedFrame, filled: true);

        // Sheet/mattress is the inner panel — slightly inset from frame.
        float sheetInset = PixelsPerTile * 0.06f;
        var sheet = new Rect2(
            frame.Position.X + sheetInset,
            frame.Position.Y + sheetInset,
            frame.Size.X - 2 * sheetInset,
            frame.Size.Y - 2 * sheetInset);
        DrawRect(sheet, BedSheet, filled: true);

        // Pillow on the head/origin tile end. Direction is given by
        // orientation: foot points away from head, so pillow sits on the
        // *opposite* side of the sheet from the foot offset.
        var (dx, dy) = BedOrientations.Offset(orientation);
        float pillowSpan = 0.45f;     // fraction of the sheet length to cover
        float pillowThick = 0.85f;    // fraction of the cross-axis to cover
        Rect2 pillowRect;
        if (dx != 0)
        {
            // Horizontal bed: pillow is a thin vertical band at the head end.
            float thickPx = sheet.Size.Y * pillowThick;
            float spanPx = sheet.Size.X * pillowSpan;
            float py = sheet.Position.Y + (sheet.Size.Y - thickPx) * 0.5f;
            float px = dx > 0
                ? sheet.Position.X
                : sheet.Position.X + sheet.Size.X - spanPx;
            pillowRect = new Rect2(px, py, spanPx, thickPx);
        }
        else
        {
            // Vertical bed: pillow is a thin horizontal band at the head end.
            float thickPx = sheet.Size.X * pillowThick;
            float spanPx = sheet.Size.Y * pillowSpan;
            float px = sheet.Position.X + (sheet.Size.X - thickPx) * 0.5f;
            float py = dy > 0
                ? sheet.Position.Y
                : sheet.Position.Y + sheet.Size.Y - spanPx;
            pillowRect = new Rect2(px, py, thickPx, spanPx);
        }
        DrawRect(pillowRect, BedPillow, filled: true);

        // Frame outline on top so it reads as crafted, not flat-painted.
        DrawRect(frame, BedFrame, filled: false, width: 2f);
    }

    private static readonly Color BedBpFill   = new(0.55f, 0.40f, 0.30f, 0.40f);
    private static readonly Color BedBpBorder = new(0.85f, 0.65f, 0.45f, 0.85f);

    private void DrawBedBlueprint(TilePos origin, BedOrientation orientation, float progress, float funding)
    {
        var foot = BedOrientations.Foot(origin, orientation);
        var oRect = new Rect2(origin.X * PixelsPerTile, origin.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        var fRect = new Rect2(foot.X * PixelsPerTile, foot.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        var bedFill = FundedFill(BedBpFill, funding);
        DrawRect(oRect, bedFill, filled: true);
        DrawRect(fRect, bedFill, filled: true);
        DrawRect(oRect, BedBpBorder, filled: false, width: 2f);
        DrawRect(fRect, BedBpBorder, filled: false, width: 2f);
        if (progress > 0f)
        {
            float p = Mathf.Clamp(progress, 0f, 1f);
            int minX = Math.Min(origin.X, foot.X);
            int minY = Math.Min(origin.Y, foot.Y);
            int maxX = Math.Max(origin.X, foot.X);
            int maxY = Math.Max(origin.Y, foot.Y);
            float x0 = minX * PixelsPerTile;
            float y0 = minY * PixelsPerTile;
            float w = (maxX - minX + 1) * PixelsPerTile;
            float h = (maxY - minY + 1) * PixelsPerTile;
            // Vertical fill from bottom of footprint bbox.
            float fh = h * p;
            var bar = new Rect2(x0, y0 + (h - fh), w, fh);
            DrawRect(bar, BlueprintProgress, filled: true);
        }
    }

    private static readonly Color StoveBody    = new(0.32f, 0.32f, 0.36f, 1f);
    private static readonly Color StoveBorder  = new(0.10f, 0.10f, 0.12f, 1f);
    private static readonly Color StoveBurner  = new(0.18f, 0.18f, 0.20f, 1f);
    private static readonly Color StoveBurnerLit = new(1.00f, 0.55f, 0.15f, 1f);
    private static readonly Color StoveStandFill = new(0.95f, 0.80f, 0.30f, 0.18f);
    private static readonly Color StoveBpFill   = new(0.40f, 0.40f, 0.44f, 0.40f);
    private static readonly Color StoveBpBorder = new(0.85f, 0.85f, 0.92f, 0.85f);

    private void DrawStove(TilePos origin, StoveOrientation orientation, float cookProgress, bool active)
    {
        // Standing tile: subtle yellow tint so it reads as the cook spot.
        var stand = StoveOrientations.StandingTile(origin, orientation);
        var standR = new Rect2(stand.X * PixelsPerTile, stand.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(standR, StoveStandFill, filled: true);

        // Body: 3 tile row. Bake a rounded-ish look with insets.
        int idx = 0;
        foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
        {
            float x0 = t.X * PixelsPerTile;
            float y0 = t.Y * PixelsPerTile;
            float inset = PixelsPerTile * 0.08f;
            var body = new Rect2(x0 + inset, y0 + inset, PixelsPerTile - 2 * inset, PixelsPerTile - 2 * inset);
            DrawRect(body, StoveBody, filled: true);
            DrawRect(body, StoveBorder, filled: false, width: 2f);

            // Burner circle on each body tile.
            var burnerCenter = new Vector2(x0 + PixelsPerTile * 0.5f, y0 + PixelsPerTile * 0.5f);
            float r = PixelsPerTile * 0.22f;
            DrawCircle(burnerCenter, r, StoveBurner);
            if (active)
            {
                var litR = r * (0.55f + 0.45f * cookProgress);
                DrawCircle(burnerCenter, litR, StoveBurnerLit);
            }
            idx++;
        }
    }

    private void DrawStoveBlueprint(TilePos origin, StoveOrientation orientation, float progress, float funding)
    {
        var fill = FundedFill(StoveBpFill, funding);
        // Body footprint outline.
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
        {
            var r = new Rect2(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
            DrawRect(r, fill, filled: true);
            DrawRect(r, StoveBpBorder, filled: false, width: 2f);
            if (t.X < minX) minX = t.X; if (t.Y < minY) minY = t.Y;
            if (t.X > maxX) maxX = t.X; if (t.Y > maxY) maxY = t.Y;
        }
        // Standing tile tint.
        var stand = StoveOrientations.StandingTile(origin, orientation);
        var sr = new Rect2(stand.X * PixelsPerTile, stand.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(sr, StoveStandFill, filled: true);
        DrawRect(sr, StoveBpBorder, filled: false, width: 2f);

        if (progress > 0f)
        {
            float p = Mathf.Clamp(progress, 0f, 1f);
            float x0 = minX * PixelsPerTile;
            float y0 = minY * PixelsPerTile;
            float w = (maxX - minX + 1) * PixelsPerTile;
            float h = (maxY - minY + 1) * PixelsPerTile;
            float fh = h * p;
            var bar = new Rect2(x0, y0 + (h - fh), w, fh);
            DrawRect(bar, BlueprintProgress, filled: true);
        }
    }

    private static readonly Color UrBoardWood   = new(0.55f, 0.35f, 0.18f, 1f);
    private static readonly Color UrBoardInlay  = new(0.95f, 0.82f, 0.42f, 1f);
    private static readonly Color UrBoardShadow = new(0f, 0f, 0f, 0.22f);
    private static readonly Color UrBoardBpFill   = new(0.65f, 0.45f, 0.20f, 0.40f);
    private static readonly Color UrBoardBpBorder = new(0.95f, 0.75f, 0.40f, 0.85f);

    private void DrawUrBoard(TilePos tile)
    {
        float x0 = tile.X * PixelsPerTile;
        float y0 = tile.Y * PixelsPerTile;
        var shadow = new Rect2(x0 + 2, y0 + 3, PixelsPerTile, PixelsPerTile);
        DrawRect(shadow, UrBoardShadow, filled: true);
        float inset = PixelsPerTile * 0.10f;
        var board = new Rect2(x0 + inset, y0 + inset, PixelsPerTile - 2 * inset, PixelsPerTile - 2 * inset);
        DrawRect(board, UrBoardWood, filled: true);

        // 3x2 rosette grid inlays (small game of ur motif).
        float gridInset = PixelsPerTile * 0.20f;
        float gx = x0 + gridInset;
        float gy = y0 + gridInset;
        float gw = PixelsPerTile - 2 * gridInset;
        float gh = PixelsPerTile - 2 * gridInset;
        float cellW = gw / 3f;
        float cellH = gh / 2f;
        float dotR = Mathf.Min(cellW, cellH) * 0.22f;
        for (int cy = 0; cy < 2; cy++)
        {
            for (int cx = 0; cx < 3; cx++)
            {
                float px = gx + (cx + 0.5f) * cellW;
                float py = gy + (cy + 0.5f) * cellH;
                DrawCircle(new Vector2(px, py), dotR, UrBoardInlay);
            }
        }
        DrawRect(board, UrBoardInlay, filled: false, width: 2f);
    }

    private void DrawUrBoardBlueprint(TilePos tile, float progress, float funding)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, FundedFill(UrBoardBpFill, funding), filled: true);
        DrawRect(rect, UrBoardBpBorder, filled: false, width: 2f);
        if (progress > 0f)
        {
            float h = PixelsPerTile * Mathf.Clamp(progress, 0f, 1f);
            var bar = new Rect2(rect.Position.X, rect.Position.Y + (PixelsPerTile - h), PixelsPerTile, h);
            DrawRect(bar, BlueprintProgress, filled: true);
        }
    }

    private void DrawLamp(TilePos tile, bool poweredOn, LightColor color)
    {
        float cx = (tile.X + 0.5f) * PixelsPerTile;
        float cy = (tile.Y + 0.5f) * PixelsPerTile;
        float baseR = PixelsPerTile * 0.30f;
        float bulbR = PixelsPerTile * 0.18f;
        // Dark base plate so the lamp reads as a fixture, not just a
        // glow.
        DrawCircle(new Vector2(cx, cy + PixelsPerTile * 0.06f), baseR, LampBaseColor);
        if (poweredOn)
        {
            // Halo + bulb tint to the lamp's color so the fixture itself
            // reads as red/green/blue/etc at a glance — the per-tile light
            // layer already paints the surrounding tiles in the same hue.
            float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
            var halo = new Color(r, g, b, LampLitHalo.A);
            var bulb = new Color(
                Mathf.Lerp(LampLitColor.R, r, 0.65f),
                Mathf.Lerp(LampLitColor.G, g, 0.65f),
                Mathf.Lerp(LampLitColor.B, b, 0.65f),
                1f);
            DrawCircle(new Vector2(cx, cy), bulbR * 2.0f, halo);
            DrawCircle(new Vector2(cx, cy), bulbR, bulb);
        }
        else
        {
            DrawCircle(new Vector2(cx, cy), bulbR, LampOffColor);
        }
    }

    private void DrawLampBlueprint(TilePos tile, float progress, float funding)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, FundedFill(LampBpFill, funding), filled: true);
        DrawRect(rect, LampBpBorder, filled: false, width: 2f);
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

    private void DrawFloorBlueprint(TilePos tile, float progress, float funding)
    {
        var rect = new Rect2(tile.X * PixelsPerTile, tile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, FundedFill(FloorBlueprintFill, funding), filled: true);
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

    // Per-vertex wall lighting. Each tile *corner* (width+1 x height+1
    // grid) gets a color = average of its up-to-4 surrounding open-floor
    // tile lights. Walls don't emit (sim writes 0) so they're excluded
    // from the average — a wall corner surrounded by 3 walls + 1 lit
    // floor tile inherits that floor's color at full strength. For each
    // wall texel, bilinear-blend the 4 corners of its tile; the texel
    // color is base wall + WallLightLift * sampledLight, clamped. This
    // replaces per-face bevels: inner corners light naturally because
    // the lit diagonal floor is one of the corner samples; straight wall
    // runs stay clean because corners between two wall-flanked tiles
    // average to whichever floor side dominates.
    private const int WallSubpx = 16;
    // Tileable stone-brick texture sampled per wall texel. Generated once,
    // power-of-two so the modulo collapses to a bitmask. Brick rows
    // staggered (offset by half-width every other row) with darker mortar
    // lines + per-brick tint variation + per-pixel noise.
    private const int WallTexSize = 64;
    private static byte[]? _wallBaseTex;
    private static byte[] EnsureWallBaseTex()
    {
        if (_wallBaseTex != null) return _wallBaseTex;
        var tex = new byte[WallTexSize * WallTexSize * 3];
        var rng = new Random(8675309);
        const int BrickW = 16;
        const int BrickH = 8;
        const int MortarPx = 1;
        float baseR = WallColor.R, baseG = WallColor.G, baseB = WallColor.B;
        // Per-brick tint table — deterministic so adjacent bricks differ.
        int bricksX = WallTexSize / BrickW;
        int bricksY = WallTexSize / BrickH;
        var brickTint = new float[bricksX * bricksY * 2 * 3];
        for (int by = 0; by < bricksY; by++)
        {
            for (int rowOff = 0; rowOff < 2; rowOff++)
            {
                for (int bx = 0; bx < bricksX; bx++)
                {
                    int bi = ((by * 2 + rowOff) * bricksX + bx) * 3;
                    float t = (float)(rng.NextDouble() - 0.5) * 0.12f;
                    brickTint[bi]     = t;
                    brickTint[bi + 1] = t * 0.9f;
                    brickTint[bi + 2] = t * 0.7f;
                }
            }
        }
        for (int y = 0; y < WallTexSize; y++)
        {
            int rowInBrick = y % BrickH;
            int brickRow = y / BrickH;
            int rowOff = brickRow & 1;
            int xShift = rowOff * (BrickW / 2);
            bool mortarY = rowInBrick < MortarPx;
            for (int x = 0; x < WallTexSize; x++)
            {
                int colInBrick = (x + xShift) % BrickW;
                bool mortarX = colInBrick < MortarPx;
                float r, g, b;
                if (mortarX || mortarY)
                {
                    // Mortar: noticeably darker than base + slight noise.
                    float n = (float)(rng.NextDouble() - 0.5) * 0.04f;
                    r = baseR * 0.45f + n;
                    g = baseG * 0.45f + n;
                    b = baseB * 0.45f + n;
                }
                else
                {
                    int bx = ((x + xShift) % WallTexSize) / BrickW;
                    int bi = ((brickRow * 2 + rowOff) * bricksX + bx) * 3;
                    float n = (float)(rng.NextDouble() - 0.5) * 0.08f;
                    r = baseR + brickTint[bi]     + n;
                    g = baseG + brickTint[bi + 1] + n * 0.95f;
                    b = baseB + brickTint[bi + 2] + n * 0.90f;
                }
                if (r < 0f) r = 0f; if (r > 1f) r = 1f;
                if (g < 0f) g = 0f; if (g > 1f) g = 1f;
                if (b < 0f) b = 0f; if (b > 1f) b = 1f;
                int oi = (y * WallTexSize + x) * 3;
                tex[oi]     = (byte)(255f * r);
                tex[oi + 1] = (byte)(255f * g);
                tex[oi + 2] = (byte)(255f * b);
            }
        }
        _wallBaseTex = tex;
        return tex;
    }

    private ImageTexture BuildWallOverlay(byte[] tiles, int width, int height)
    {
        int w = width * WallSubpx;
        int h = height * WallSubpx;
        // Raw RGBA8 byte buffer + Image.CreateFromData. SetPixel-per-pixel
        // at this resolution (e.g. 100x100 tiles = 1600x1600 = 2.56M
        // calls) freezes the main thread for ~1s per wall placement —
        // unacceptable when building. Default-zero bytes are already
        // transparent, so non-wall tiles cost nothing.
        var data = new byte[w * h * 4];

        var wtex = EnsureWallBaseTex();
        const int TexMask = WallTexSize - 1;

        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                if (tiles[ty * width + tx] == 0) continue;
                int mask = WallNeighborMask(tiles, tx, ty, width, height);
                if (_wallTextures[mask] is not null) continue;
                int baseX = tx * WallSubpx;
                int baseY = ty * WallSubpx;
                for (int sy = 0; sy < WallSubpx; sy++)
                {
                    int rowStart = ((baseY + sy) * w + baseX) * 4;
                    int tv = (baseY + sy) & TexMask;
                    for (int sx = 0; sx < WallSubpx; sx++)
                    {
                        int tu = (baseX + sx) & TexMask;
                        int ti = (tv * WallTexSize + tu) * 3;
                        int idx = rowStart + sx * 4;
                        data[idx + 0] = wtex[ti];
                        data[idx + 1] = wtex[ti + 1];
                        data[idx + 2] = wtex[ti + 2];
                        data[idx + 3] = 255;
                    }
                }
            }
        }
        var img = Image.CreateFromData(w, h, false, Image.Format.Rgba8, data);
        return ImageTexture.CreateFromImage(img);
    }

    // Neighbor mask (8-bit): low nibble = NESW cardinals (bit3=N, bit2=E,
    // bit1=S, bit0=W). High nibble = diagonals (bit7=NW, bit6=SW, bit5=SE,
    // bit4=NE). Matches Blender wall_NNNNNNNN.png 8-char binary file
    // naming. Pinch-corner geometry is pre-baked per variant.
    private static int WallNeighborMask(byte[] tiles, int tx, int ty, int width, int height)
    {
        int m = 0;
        bool n = ty > 0 && tiles[(ty - 1) * width + tx] != 0;
        bool e = tx < width - 1 && tiles[ty * width + (tx + 1)] != 0;
        bool s = ty < height - 1 && tiles[(ty + 1) * width + tx] != 0;
        bool w = tx > 0 && tiles[ty * width + (tx - 1)] != 0;
        if (n) m |= 8;
        if (e) m |= 4;
        if (s) m |= 2;
        if (w) m |= 1;
        bool ne = ty > 0 && tx < width - 1 && tiles[(ty - 1) * width + (tx + 1)] != 0;
        bool se = ty < height - 1 && tx < width - 1 && tiles[(ty + 1) * width + (tx + 1)] != 0;
        bool sw = ty < height - 1 && tx > 0 && tiles[(ty + 1) * width + (tx - 1)] != 0;
        bool nw = ty > 0 && tx > 0 && tiles[(ty - 1) * width + (tx - 1)] != 0;
        if (ne) m |= 16;
        if (se) m |= 32;
        if (sw) m |= 64;
        if (nw) m |= 128;
        return m;
    }

    private void UpdateWallSprites(byte[] tiles, int width, int height)
    {
        if (_wallSpritesRoot is null) return;
        var seen = new HashSet<TilePos>();
        for (int ty = 0; ty < height; ty++)
        {
            for (int tx = 0; tx < width; tx++)
            {
                if (tiles[ty * width + tx] == 0) continue;
                int mask = WallNeighborMask(tiles, tx, ty, width, height);
                var tex = _wallTextures[mask];
                if (tex is null) continue;
                var tile = new TilePos(tx, ty);
                seen.Add(tile);
                if (!_wallSprites.TryGetValue(tile, out var spr))
                {
                    spr = new Sprite2D
                    {
                        Centered = false,
                        TextureFilter = TextureFilterEnum.Nearest,
                    };
                    _wallSpritesRoot.AddChild(spr);
                    _wallSprites[tile] = spr;
                }
                spr.Texture = tex;
                spr.Position = new Vector2(tx * PixelsPerTile, ty * PixelsPerTile);
                int srcW = tex.GetWidth();
                if (srcW > 0)
                {
                    float s = (float)PixelsPerTile / srcW;
                    spr.Scale = new Vector2(s, s);
                }
            }
        }
        if (_wallSprites.Count != seen.Count)
        {
            var rm = new List<TilePos>();
            foreach (var kv in _wallSprites)
                if (!seen.Contains(kv.Key)) rm.Add(kv.Key);
            foreach (var t in rm)
            {
                _wallSprites[t].QueueFree();
                _wallSprites.Remove(t);
            }
        }
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
