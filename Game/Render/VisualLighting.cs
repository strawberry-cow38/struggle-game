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

    private Texture2D? _lampTex;
    private OccluderPolygon2D? _wallPolyPrototype;

    private readonly Dictionary<TilePos, PointLight2D> _lampNodes = new();
    private readonly Dictionary<TilePos, LightOccluder2D> _doorNodes = new();

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
            Color = new Color(0.72f, 0.72f, 0.78f),
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
            var roofs = Host.CopyRoofTilesForRender();
            RebuildSunMask(roofs);
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

    private void RebuildWallOccluders(byte[] wallBytes)
    {
        if (_wallsRoot is null || _wallPolyPrototype is null) return;
        foreach (var child in _wallsRoot.GetChildren())
        {
            child.QueueFree();
        }
        for (int y = 0; y < MapHeight; y++)
        {
            int row = y * MapWidth;
            for (int x = 0; x < MapWidth; x++)
            {
                if (wallBytes[row + x] == 0) continue;
                var occ = new LightOccluder2D
                {
                    Occluder = _wallPolyPrototype,
                    Position = new Vector2(x * PixelsPerTile, y * PixelsPerTile),
                };
                _wallsRoot.AddChild(occ);
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

    private void UpdateDoors(SimSnapshot snap)
    {
        if (_doorsRoot is null) return;
        var seen = new HashSet<TilePos>();
        foreach (var d in snap.Doors)
        {
            seen.Add(d.Tile);
            if (!_doorNodes.TryGetValue(d.Tile, out var occ))
            {
                occ = new LightOccluder2D
                {
                    Position = new Vector2(d.Tile.X * PixelsPerTile, d.Tile.Y * PixelsPerTile),
                };
                _doorsRoot.AddChild(occ);
                _doorNodes[d.Tile] = occ;
            }
            // Per-door occluder polygon that mirrors the visual door
            // panel: panelLen × panelThick rect pivoted at one tile edge,
            // rotated by OpenAmount × 90°. Even when closed the polygon
            // is panel-thick (not full tile), so the door tile receives
            // some light. As the door swings open the panel rotates away
            // and the blocked region shrinks naturally.
            occ.Occluder = BuildDoorPanelOccluder(d.Orientation, d.OpenAmount);
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

    private static OccluderPolygon2D BuildDoorPanelOccluder(DoorOrientation orient, float openAmount)
    {
        // Local-space (tile-local; LightOccluder2D Position = tile origin)
        // panel polygon. Pivot is at the left/top edge midpoint depending
        // on orientation, matching DrawDoor's swing. PanelThick is kept
        // narrow so even a fully closed door doesn't fully cover the
        // tile, letting some sun/lamp light reach the door surface.
        float panelLen = PixelsPerTile;
        float panelThick = PixelsPerTile * 0.22f;
        float angle = openAmount * (Mathf.Pi * 0.5f);
        Vector2 pivot;
        Vector2 closedDir;
        if (orient == DoorOrientation.Horizontal)
        {
            pivot = new Vector2(0f, PixelsPerTile * 0.5f);
            closedDir = new Vector2(1f, 0f);
        }
        else
        {
            pivot = new Vector2(PixelsPerTile * 0.5f, 0f);
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
        return new OccluderPolygon2D
        {
            Polygon = new Vector2[] { p0, p1, p2, p3 },
            Closed = true,
            CullMode = OccluderPolygon2D.CullModeEnum.Disabled,
        };
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
                    BlendMode = Light2D.BlendModeEnum.Add,
                    ShadowEnabled = true,
                    ShadowColor = new Color(0f, 0f, 0f, 0.21f),
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
            node.Energy = lamp.PoweredOn ? 0.7f : 0f;
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
                // Linear falloff capped at ~0.55 alpha so the source
                // tile stays bright but never reads as a nuclear spike;
                // gentle taper extends the visible glow far out.
                float a = Mathf.Pow(t, 1.2f) * 0.55f;
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
