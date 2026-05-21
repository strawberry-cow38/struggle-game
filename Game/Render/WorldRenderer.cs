using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using TileMap = StruggleGame.Sim.Map.TileMap;

namespace StruggleGame.Game.Render;

// Renders the static tile map (tiled grass texture + a baked wall
// overlay) plus the dynamic dummies from the latest SimSnapshot. Tiles do
// not change yet, so both textures are built once at _Ready.
public partial class WorldRenderer : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;
    private const int GrassTexSize = 256;

    private ImageTexture? _grassTex;
    private ImageTexture? _wallOverlayTex;
    private int _mapPixelWidth;
    private int _mapPixelHeight;

    private static readonly Color WallColor = new(0.18f, 0.16f, 0.14f);
    private static readonly Color DummyColor = new(0.95f, 0.55f, 0.20f);

    public SimHost? Host { get; set; }

    public override void _Ready()
    {
        // Keep tiles crisp at high zoom and let the grass tile across the map.
        TextureFilter = TextureFilterEnum.Nearest;
        TextureRepeat = TextureRepeatEnum.Enabled;

        if (Host is null) return;
        _grassTex = BuildGrassTexture(seed: 1337);
        _wallOverlayTex = BuildWallOverlay(Host.Map);
        _mapPixelWidth = Host.Map.Width * PixelsPerTile;
        _mapPixelHeight = Host.Map.Height * PixelsPerTile;
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_grassTex is null || _wallOverlayTex is null || Host is null) return;

        var mapRect = new Rect2(0, 0, _mapPixelWidth, _mapPixelHeight);
        DrawTextureRect(_grassTex, mapRect, tile: true);
        DrawTextureRect(_wallOverlayTex, mapRect, tile: false);

        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        float radius = PixelsPerTile * 0.35f;
        foreach (var d in snap.Dummies)
        {
            DrawCircle(new Vector2(d.X * PixelsPerTile, d.Y * PixelsPerTile), radius, DummyColor);
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

        // Mottled soil/dirt base so bare patches show through between blades.
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

        // Scatter ~2200 short upright blade strokes across the texture.
        // Each blade = 3–6 px tall × 1 px wide, dark at base → bright at tip,
        // hue chosen from a small palette so the field has visible variation.
        var bladePalette = new Color[]
        {
            new(0.48f, 0.62f, 0.18f),
            new(0.36f, 0.55f, 0.16f),
            new(0.42f, 0.66f, 0.22f),
            new(0.55f, 0.58f, 0.18f),
            new(0.62f, 0.55f, 0.20f), // dry/yellow blade
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
                // Top of stroke = bright tip, bottom = darker base.
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

    // Walls = opaque dark, grass = transparent. Drawn over the tiled grass.
    private static ImageTexture BuildWallOverlay(TileMap map)
    {
        var img = Image.CreateEmpty(map.Width, map.Height, false, Image.Format.Rgba8);
        var transparent = new Color(0f, 0f, 0f, 0f);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                img.SetPixel(x, y, map.Get(x, y) == TileType.Wall ? WallColor : transparent);
            }
        }
        return ImageTexture.CreateFromImage(img);
    }
}
