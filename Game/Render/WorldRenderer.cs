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

    // Tileable grassy texture: low-frequency value noise (wraps at edges)
    // shifted into a green palette, plus per-pixel grit.
    private static ImageTexture BuildGrassTexture(int seed)
    {
        const int gridSize = 16;
        var rng = new Random(seed);
        var noise = new float[gridSize + 1, gridSize + 1];
        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                noise[gx, gy] = (float)rng.NextDouble();
            }
        }
        // Make the noise grid wrap so the resulting texture tiles seamlessly.
        for (int i = 0; i <= gridSize; i++)
        {
            noise[gridSize, i] = noise[0, i % gridSize];
            noise[i, gridSize] = noise[i % gridSize, 0];
        }

        var img = Image.CreateEmpty(GrassTexSize, GrassTexSize, false, Image.Format.Rgba8);
        for (int py = 0; py < GrassTexSize; py++)
        {
            for (int px = 0; px < GrassTexSize; px++)
            {
                float fx = px / (float)GrassTexSize * gridSize;
                float fy = py / (float)GrassTexSize * gridSize;
                int ix = (int)fx;
                int iy = (int)fy;
                float u = fx - ix;
                float v = fy - iy;
                float n00 = noise[ix, iy];
                float n10 = noise[ix + 1, iy];
                float n01 = noise[ix, iy + 1];
                float n11 = noise[ix + 1, iy + 1];
                float n = Mathf.Lerp(Mathf.Lerp(n00, n10, u), Mathf.Lerp(n01, n11, u), v);

                // Per-pixel grit for blade-of-grass texture without a real noise tex.
                float grit = ((float)rng.NextDouble() - 0.5f) * 0.10f;

                float r = 0.16f + n * 0.18f + grit * 0.5f;
                float g = 0.32f + n * 0.30f + grit * 0.8f;
                float b = 0.10f + n * 0.12f + grit * 0.5f;
                img.SetPixel(px, py, new Color(
                    Mathf.Clamp(r, 0f, 1f),
                    Mathf.Clamp(g, 0f, 1f),
                    Mathf.Clamp(b, 0f, 1f)));
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
