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

    // Non-linear remap exponent applied to per-channel sim brightness
    // before lerping into [AmbientMin, 1]. Sim publishes lamps at their
    // literal spec (50% inner = byte 128) and this curve pushes mid
    // values toward full bright so 50%-lit reads close to 100%-lit,
    // while shadow→ambient contrast stays untouched (sim 0 still maps
    // to AmbientMin). Lower = brighter mids.
    private const float LightCurveExp = 0.4f;

    // Halo footprint in tiles (diameter). Bigger = softer wider bloom.
    private const float HaloDiameterTiles = 1.5f;

    private long _lastLightVersion = -1;
    private long _lastLampSnapTick = -2;
    private SimSnapshot? _lastLampSnap;

    private ImageTexture? _lightTex;
    // Exposed for the wall sprite's ShaderMaterial — wall shader bilinear-
    // samples this same per-tile RGB texture so wall lighting tracks ground
    // lighting exactly without a CPU bake per LightVersion bump.
    public Texture2D? LightTex => _lightTex;
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
        float floor = AmbientMin;
        float range = 1f - AmbientMin;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            data[o + 0] = Curve(rgb[i * 3], floor, range);
            data[o + 1] = Curve(rgb[i * 3 + 1], floor, range);
            data[o + 2] = Curve(rgb[i * 3 + 2], floor, range);
            data[o + 3] = 255;
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

    // Apply LightCurveExp to one channel: simByte/255 → pow(t, exp) →
    // lerp into [floor, 1] → 0..255 byte. Pulled out so the per-tile
    // RGB loop stays tight.
    private static byte Curve(byte simByte, float floor, float range)
    {
        float t = simByte / 255f;
        if (t <= 0f) return (byte)Mathf.RoundToInt(floor * 255f);
        float shaped = Mathf.Pow(t, LightCurveExp);
        float v = floor + range * shaped;
        if (v >= 1f) return 255;
        return (byte)Mathf.RoundToInt(v * 255f);
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
