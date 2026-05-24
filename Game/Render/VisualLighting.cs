using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.Render;

// Visual-only lighting layer. Sim still computes the per-tile RGB
// light grid for gameplay (mood, future colonist vision, etc.) — this
// node ignores that grid and uses Godot's stock 2D lighting (Light2D
// + LightOccluder2D + CanvasModulate) to draw the pretty visual.
//
// Topology:
//   - CanvasModulate child sets ambient darkness on the world canvas.
//   - Sun PointLight2D covers the whole map with a roof-mask texture
//     (white where no-roof, black where roofed) so sunlight only hits
//     outdoor tiles. Rebuilt on RoofVersion bumps.
//   - One PointLight2D per powered lamp, colored to the lamp.
//   - One LightOccluder2D per wall tile so lamp light casts real
//     shadows. Rebuilt on MapVersion bumps.
public partial class VisualLighting : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    public SimHost? Host;
    public int MapWidth;
    public int MapHeight;

    private CanvasModulate? _modulate;
    private PointLight2D? _sun;
    private Node2D? _wallsRoot;
    private Node2D? _doorsRoot;
    private Node2D? _lampsRoot;

    private long _lastMapVersion = -1;
    private long _lastRoofVersion = -1;
    private long _lastLampSnapshotTick = -2;
    private SimSnapshot? _lastLampSnap;
    private byte[]? _lastRoofs;

    private Texture2D? _lampTex;
    private OccluderPolygon2D? _wallPolyPrototype;

    private readonly Dictionary<TilePos, PointLight2D> _lampNodes = new();
    private readonly Dictionary<TilePos, LightOccluder2D> _doorNodes = new();
    private readonly Dictionary<TilePos, LightOccluder2D> _wallNodes = new();

    // Visual lamp throw radius (purely cosmetic - sim's gameplay light
    // grid still uses LampOuterSq = 9.5 tiles). Larger spread reads as
    // a real ambient glow rather than a tight disc.
    private const float LampRangeTiles = 16f;

    public override void _Ready()
    {
        // Ambient: how dim an unlit roofed tile reads. Higher = roofed
        // interiors stay visible without a lamp; lower = stronger
        // "needs a lamp" feel. Cool tint so warm lamps pop.
        _modulate = new CanvasModulate
        {
            Color = new Color(0.60f, 0.60f, 0.66f),
        };
        AddChild(_modulate);

        _lampTex = BuildRadialGradient(128);

        _sun = new PointLight2D
        {
            Color = new Color(1f, 0.98f, 0.92f),
            Energy = 0.45f,
            BlendMode = Light2D.BlendModeEnum.Add,
            ShadowEnabled = false,
            TextureScale = 1f,
        };
        AddChild(_sun);

        _wallsRoot = new Node2D { Name = "Walls" };
        AddChild(_wallsRoot);

        _doorsRoot = new Node2D { Name = "Doors" };
        AddChild(_doorsRoot);

        _lampsRoot = new Node2D { Name = "Lamps" };
        AddChild(_lampsRoot);

        // Shared 1-tile square occluder polygon. Each wall tile gets its
        // own LightOccluder2D positioned at the tile, but they all reuse
        // this polygon resource.
        _wallPolyPrototype = new OccluderPolygon2D
        {
            Polygon = new Vector2[]
            {
                new(0, 0),
                new(PixelsPerTile, 0),
                new(PixelsPerTile, PixelsPerTile),
                new(0, PixelsPerTile),
            },
            Closed = true,
            CullMode = OccluderPolygon2D.CullModeEnum.Disabled,
        };
    }

    public void Tick()
    {
        if (Host is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        if (snap.MapVersion != _lastMapVersion)
        {
            var wallBytes = Host.CopyLayerForRender(MapLayer.Wall);
            RebuildWallOccluders(wallBytes);
            _lastMapVersion = snap.MapVersion;
        }

        if (snap.RoofVersion != _lastRoofVersion)
        {
            _lastRoofs = Host.CopyRoofTilesForRender();
            RebuildSunMask(_lastRoofs);
            _lastRoofVersion = snap.RoofVersion;
        }

        // Lamp + door state refresh when snapshot changes. Lamps may move
        // power state / color, doors open and close - both need replays.
        if (!ReferenceEquals(snap, _lastLampSnap) || snap.Tick != _lastLampSnapshotTick)
        {
            UpdateLamps(snap);
            UpdateDoors(snap);
            _lastLampSnap = snap;
            _lastLampSnapshotTick = snap.Tick;
        }
    }

    // Incremental: diff against the previously seen wall set so a single
    // wall placement only adds one occluder, not N. Freeing + recreating
    // every occluder per MapVersion bump was contributing to the wall-
    // build freeze.
    private void RebuildWallOccluders(byte[] wallBytes)
    {
        if (_wallsRoot is null || _wallPolyPrototype is null) return;
        var seen = new HashSet<TilePos>();
        for (int y = 0; y < MapHeight; y++)
        {
            int row = y * MapWidth;
            for (int x = 0; x < MapWidth; x++)
            {
                if (wallBytes[row + x] == 0) continue;
                var t = new TilePos(x, y);
                seen.Add(t);
                if (_wallNodes.ContainsKey(t)) continue;
                var occ = new LightOccluder2D
                {
                    Occluder = _wallPolyPrototype,
                    Position = new Vector2(x * PixelsPerTile, y * PixelsPerTile),
                };
                _wallsRoot.AddChild(occ);
                _wallNodes[t] = occ;
            }
        }
        if (_wallNodes.Count != seen.Count)
        {
            var toRemove = new List<TilePos>();
            foreach (var kvp in _wallNodes)
                if (!seen.Contains(kvp.Key)) toRemove.Add(kvp.Key);
            foreach (var t in toRemove)
            {
                _wallNodes[t].QueueFree();
                _wallNodes.Remove(t);
            }
        }
    }

    private void RebuildSunMask(byte[] roofs)
    {
        if (_sun is null) return;
        // White where there's no roof (outdoor), black where roofed.
        // Light2D texture brightness modulates how much light reaches
        // each pixel, so roofed tiles receive zero sun contribution.
        var img = Image.CreateEmpty(MapWidth, MapHeight, false, Image.Format.Rgba8);
        var lit = new Color(1f, 1f, 1f, 1f);
        var dark = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < MapHeight; y++)
        {
            int row = y * MapWidth;
            for (int x = 0; x < MapWidth; x++)
            {
                img.SetPixel(x, y, roofs[row + x] == 0 ? lit : dark);
            }
        }
        var tex = ImageTexture.CreateFromImage(img);
        _sun.Texture = tex;
        // PointLight2D draws its texture centered at Position. Stretch
        // tile-space mask to pixel-space and center over map.
        _sun.TextureScale = PixelsPerTile;
        _sun.Offset = Vector2.Zero;
        _sun.Position = new Vector2(MapWidth * PixelsPerTile * 0.5f, MapHeight * PixelsPerTile * 0.5f);
    }

    // Closed doors occlude light (full tile). Open doors don't, so light
    // spills through the threshold while a pawn is crossing. Reuses the
    // shared wall polygon prototype; Visible toggles on door state.
    private void UpdateDoors(SimSnapshot snap)
    {
        if (_doorsRoot is null || _wallPolyPrototype is null) return;
        var seen = new HashSet<TilePos>();
        foreach (var d in snap.Doors)
        {
            seen.Add(d.Tile);
            if (!_doorNodes.TryGetValue(d.Tile, out var occ))
            {
                occ = new LightOccluder2D
                {
                    Occluder = _wallPolyPrototype,
                    Position = new Vector2(d.Tile.X * PixelsPerTile, d.Tile.Y * PixelsPerTile),
                };
                _doorsRoot.AddChild(occ);
                _doorNodes[d.Tile] = occ;
            }
            // OpenAmount: 0 = closed, 1 = open. Stop occluding once the
            // door is mostly open so a pawn walking through doesn't see
            // light cut off; keep occluding closed/opening/closing.
            occ.Visible = d.OpenAmount < 0.8f;
        }
        if (_doorNodes.Count != seen.Count)
        {
            var toRemove = new List<TilePos>();
            foreach (var kvp in _doorNodes)
                if (!seen.Contains(kvp.Key)) toRemove.Add(kvp.Key);
            foreach (var t in toRemove)
            {
                _doorNodes[t].QueueFree();
                _doorNodes.Remove(t);
            }
        }
    }

    private void UpdateLamps(SimSnapshot snap)
    {
        if (_lampsRoot is null || _lampTex is null) return;
        var seen = new HashSet<TilePos>();
        foreach (var lamp in snap.Lamps)
        {
            seen.Add(lamp.Tile);
            if (!_lampNodes.TryGetValue(lamp.Tile, out var node))
            {
                node = new PointLight2D
                {
                    Texture = _lampTex,
                    // Mix (not Add): each lamp alpha-blends toward its
                    // own color rather than summing intensities. Two
                    // overlapping lamps cap at the lamp color instead
                    // of doubling into a nuclear hotspot.
                    BlendMode = Light2D.BlendModeEnum.Mix,
                    // Shadow casting is later toggled to "one lamp per
                    // proximity cluster" — see AssignClusterShadows.
                    ShadowEnabled = false,
                    // Transparent shadow color: walls still occlude
                    // the lamp (lit region stops at the wall), but each
                    // lamp's shadow doesn't tint pixels darker. Two
                    // overlapping lamps would otherwise stack their
                    // shadow alphas behind a shared wall = darker shadow
                    // per extra lamp. With alpha 0 the unlit area just
                    // reads as ambient, no stacking.
                    ShadowColor = new Color(0f, 0f, 0f, 0f),
                    ShadowFilter = Light2D.ShadowFilterEnum.Pcf5,
                    ShadowFilterSmooth = 1.0f,
                };
                _lampsRoot.AddChild(node);
                _lampNodes[lamp.Tile] = node;
            }
            node.Position = new Vector2((lamp.Tile.X + 0.5f) * PixelsPerTile, (lamp.Tile.Y + 0.5f) * PixelsPerTile);
            // Texture is 128px square. Scale so falloff covers
            // LampRangeTiles (= sim's LampOuterSq radius) on each side.
            node.TextureScale = (LampRangeTiles * 2f * PixelsPerTile) / 128f;
            node.Color = new Color(lamp.Color.R / 255f, lamp.Color.G / 255f, lamp.Color.B / 255f);
            // Mix mode lerps toward (color * energy) using the texture
            // alpha. Energy must be ≥ 1.0 so the lamp target meets or
            // exceeds bright backgrounds (sun-lit tiles, other lamps);
            // otherwise Mix darkens lit pixels instead of brightening
            // dark ones, which reads as "subtracting light".
            node.Energy = lamp.PoweredOn ? 1.0f : 0f;
            node.Enabled = lamp.PoweredOn;
        }
        // Drop nodes for lamps that no longer exist (deconstructed).
        if (_lampNodes.Count != seen.Count)
        {
            var toRemove = new List<TilePos>();
            foreach (var kvp in _lampNodes)
                if (!seen.Contains(kvp.Key)) toRemove.Add(kvp.Key);
            foreach (var t in toRemove)
            {
                _lampNodes[t].QueueFree();
                _lampNodes.Remove(t);
            }
        }
        AssignClusterShadows(snap);
    }

    // Group powered lamps into proximity clusters (centers within
    // 2×LampRangeTiles share a cluster, since that's the threshold for
    // their lit areas to overlap) via union-find. Per cluster, pick one
    // lamp as the shadow caster; everything else in the cluster has
    // ShadowEnabled=false. This collapses the "fan of shadows" effect
    // where N overlapping lamps each cast their own shadow ray from a
    // shared wall vertex — now the cluster has one shadow source, so
    // walls cast one consistent shadow regardless of lamp count.
    private void AssignClusterShadows(SimSnapshot snap)
    {
        var lit = new List<LampState>();
        foreach (var l in snap.Lamps) if (l.PoweredOn) lit.Add(l);
        int n = lit.Count;
        if (n == 0) return;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

        // Two lamps share a cluster if their lit-radius circles overlap.
        // Distance threshold in tile units = 2 × LampRangeTiles.
        float threshTiles = 2f * LampRangeTiles;
        float threshSq = threshTiles * threshTiles;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                float dx = lit[i].Tile.X - lit[j].Tile.X;
                float dy = lit[i].Tile.Y - lit[j].Tile.Y;
                if (dx * dx + dy * dy <= threshSq) Union(i, j);
            }
        }

        // Per cluster: pick the lamp with the smallest tile coord (X
        // then Y) as the shadow caster. Stable across ticks so we don't
        // flicker which lamp owns the shadow. Outdoor (unroofed) lamps
        // are skipped — bright ambient + sun already pushes those tiles
        // past ~50% light, where cast shadows just read as noise. If a
        // cluster has no indoor lamp, no caster is assigned and the
        // cluster casts zero shadows.
        var clusterCaster = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            if (!IsRoofed(lit[i].Tile.X, lit[i].Tile.Y)) continue;
            int root = Find(i);
            if (!clusterCaster.TryGetValue(root, out var existing))
            {
                clusterCaster[root] = i;
                continue;
            }
            var a = lit[i].Tile;
            var b = lit[existing].Tile;
            if (a.X < b.X || (a.X == b.X && a.Y < b.Y)) clusterCaster[root] = i;
        }

        for (int i = 0; i < n; i++)
        {
            if (!_lampNodes.TryGetValue(lit[i].Tile, out var node)) continue;
            int root = Find(i);
            node.ShadowEnabled = clusterCaster.TryGetValue(root, out var caster) && caster == i;
        }
    }

    private bool IsRoofed(int x, int y)
    {
        if (_lastRoofs is null) return false;
        if (x < 0 || y < 0 || x >= MapWidth || y >= MapHeight) return false;
        return _lastRoofs[y * MapWidth + x] != 0;
    }

    // Soft radial gradient: white at center, fading to transparent at
    // edges. Used as the lamp + sun light texture so the falloff feels
    // smooth instead of a hard disc.
    private static ImageTexture BuildRadialGradient(int size)
    {
        var img = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        float center = (size - 1) * 0.5f;
        float maxR = center;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float t = Mathf.Clamp(1f - d / maxR, 0f, 1f);
                // Mix-mode alpha controls the lerp weight between
                // background and lamp color. Full 1.0 at center so the
                // lamp tile saturates at the lamp color (no "nuclear"
                // risk under Mix — final caps at the color itself).
                float a = Mathf.Pow(t, 1.2f);
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
