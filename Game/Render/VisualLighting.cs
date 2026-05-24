using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.Render;

// Visual lighting layer driven entirely by the sim's per-tile RGB
// light grid. Sim does the hard work (lamp falloff, wall LOS, roof
// mask, outdoor sun, color tinting) — this node just turns the grid
// into a smooth multiplicative overlay and drops a bloom halo on every
// powered lamp for the "look at the bulb" pop.
//
// Topology:
//   - Multiply Sprite2D: 1px-per-tile RGBA texture from sim grid,
//     bilinear-filtered, scaled to map pixel size, BlendMode = Mul.
//     Where grid = 255 the world stays untouched, where grid = 0 the
//     world goes pitch black. Clamped to AmbientMin so unlit roofed
//     rooms read as dim instead of invisible.
//   - Halo sprites: one additive radial gradient per powered lamp,
//     tinted to the lamp color. Stacks naturally where lamps overlap.
public partial class VisualLighting : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    public SimHost? Host;
    public int MapWidth;
    public int MapHeight;

    // Floor brightness: where the sim grid says a tile is fully dark
    // (roofed + unlit), we still let this fraction of the underlying
    // world bleed through. 0 = pitch black indoors, 1 = no darkening.
    private const float AmbientMin = 0.55f;

    // Halo footprint in tiles (diameter). Bigger = softer wider bloom.
    private const float HaloDiameterTiles = 1.5f;

    private long _lastLightVersion = -1;
    private long _lastMapVersion = -1;
    private long _lastLampSnapTick = -2;
    private SimSnapshot? _lastLampSnap;
    private byte[]? _wallBytes;

    private ImageTexture? _lightTex;
    private Sprite2D? _lightOverlay;
    private Node2D? _halosRoot;
    private Texture2D? _haloTex;

    private readonly Dictionary<TilePos, Sprite2D> _halos = new();

    public override void _Ready()
    {
        _lightOverlay = new Sprite2D
        {
            Centered = false,
            // Linear filter blurs the per-tile RGB grid into a smooth
            // gradient between tile centers. Without it the multiply
            // looks blocky.
            TextureFilter = CanvasItem.TextureFilterEnum.Linear,
            Material = new CanvasItemMaterial
            {
                BlendMode = CanvasItemMaterial.BlendModeEnum.Mul,
            },
        };
        AddChild(_lightOverlay);

        _halosRoot = new Node2D { Name = "Halos" };
        AddChild(_halosRoot);

        _haloTex = BuildRadialGradient(128);
    }

    public void Tick()
    {
        if (Host is null) return;
        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        if (snap.MapVersion != _lastMapVersion)
        {
            _wallBytes = Host.CopyLayerForRender(MapLayer.Wall);
            _lastMapVersion = snap.MapVersion;
            // Wall set changed → force rebuild so wall-fill picks up
            // the new occupancy even if LightVersion didn't bump.
            _lastLightVersion = -1;
        }

        if (snap.LightVersion != _lastLightVersion)
        {
            RebuildLightTexture();
            _lastLightVersion = snap.LightVersion;
        }

        if (!ReferenceEquals(snap, _lastLampSnap) || snap.Tick != _lastLampSnapTick)
        {
            UpdateHalos(snap);
            _lastLampSnap = snap;
            _lastLampSnapTick = snap.Tick;
        }
    }

    private void RebuildLightTexture()
    {
        if (_lightOverlay is null) return;
        var rgb = Host!.CopyLightRgbForRender();
        int n = MapWidth * MapHeight;
        var data = new byte[n * 4];
        int minA = (int)(AmbientMin * 255);
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            int sr = rgb[i * 3];
            int sg = rgb[i * 3 + 1];
            int sb = rgb[i * 3 + 2];
            if (sr < minA) sr = minA;
            if (sg < minA) sg = minA;
            if (sb < minA) sb = minA;
            data[o + 0] = (byte)sr;
            data[o + 1] = (byte)sg;
            data[o + 2] = (byte)sb;
            data[o + 3] = 255;
        }
        // Second pass: replace each wall texel with the max RGB across
        // its 4 orthogonal floor neighbors. Without this, bilinear
        // sampling drags brightness down at inner room corners where a
        // lit floor texel sits diagonally next to two wall texels at
        // the ambient floor — the corner reads visibly dimmer than the
        // rest of the lit floor. Filling walls with the brightest
        // neighbor floor value eliminates the corner dimming.
        var walls = _wallBytes;
        if (walls is not null)
        {
            for (int y = 0; y < MapHeight; y++)
            {
                int row = y * MapWidth;
                for (int x = 0; x < MapWidth; x++)
                {
                    int idx = row + x;
                    if (walls[idx] == 0) continue;
                    int o0 = idx * 4;
                    // Seed with current wall value (AmbientMin floor)
                    // so isolated walls — no lit floor neighbor — keep
                    // their ambient instead of getting blacked out.
                    int br = data[o0 + 0], bg = data[o0 + 1], bb = data[o0 + 2];
                    void Sample(int sx, int sy)
                    {
                        if ((uint)sx >= (uint)MapWidth || (uint)sy >= (uint)MapHeight) return;
                        int si = sy * MapWidth + sx;
                        if (walls[si] != 0) return;
                        int so = si * 4;
                        if (data[so + 0] > br) br = data[so + 0];
                        if (data[so + 1] > bg) bg = data[so + 1];
                        if (data[so + 2] > bb) bb = data[so + 2];
                    }
                    Sample(x - 1, y);
                    Sample(x + 1, y);
                    Sample(x, y - 1);
                    Sample(x, y + 1);
                    int o = idx * 4;
                    data[o + 0] = (byte)br;
                    data[o + 1] = (byte)bg;
                    data[o + 2] = (byte)bb;
                }
            }
        }
        var img = Image.CreateFromData(MapWidth, MapHeight, false, Image.Format.Rgba8, data);
        if (_lightTex is null)
        {
            _lightTex = ImageTexture.CreateFromImage(img);
            _lightOverlay.Texture = _lightTex;
            _lightOverlay.Scale = new Vector2(PixelsPerTile, PixelsPerTile);
            _lightOverlay.Position = Vector2.Zero;
        }
        else
        {
            _lightTex.Update(img);
        }
    }

    private void UpdateHalos(SimSnapshot snap)
    {
        if (_halosRoot is null || _haloTex is null) return;
        var seen = new HashSet<TilePos>();
        foreach (var l in snap.Lamps)
        {
            if (!l.PoweredOn) continue;
            seen.Add(l.Tile);
            if (!_halos.TryGetValue(l.Tile, out var spr))
            {
                spr = new Sprite2D
                {
                    Texture = _haloTex,
                    Centered = true,
                    Material = new CanvasItemMaterial
                    {
                        BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
                    },
                };
                _halosRoot.AddChild(spr);
                _halos[l.Tile] = spr;
            }
            spr.Position = new Vector2(
                (l.Tile.X + 0.5f) * PixelsPerTile,
                (l.Tile.Y + 0.5f) * PixelsPerTile);
            float pxSize = HaloDiameterTiles * PixelsPerTile;
            spr.Scale = new Vector2(pxSize / 128f, pxSize / 128f);
            spr.Modulate = new Color(l.Color.R / 255f, l.Color.G / 255f, l.Color.B / 255f, 1f);
        }
        if (_halos.Count != seen.Count)
        {
            var rm = new List<TilePos>();
            foreach (var kv in _halos)
                if (!seen.Contains(kv.Key)) rm.Add(kv.Key);
            foreach (var t in rm)
            {
                _halos[t].QueueFree();
                _halos.Remove(t);
            }
        }
    }

    // Radial gradient, white center → transparent edge. Used as the
    // additive halo texture. Sharper falloff (pow 2) so the bloom
    // disc reads as a "bulb" not a flat circle.
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
                float a = Mathf.Pow(t, 2.0f);
                img.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
