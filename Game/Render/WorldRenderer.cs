using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Render;

// Renders the static tile map (baked once as an ImageTexture, drawn at
// integer pixels-per-tile) plus the dynamic dummies from the latest
// SimSnapshot. Tiles do not change yet, so the map texture is built
// once at _Ready.
public partial class WorldRenderer : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private ImageTexture? _mapTex;
    private int _mapPixelWidth;
    private int _mapPixelHeight;

    private static readonly Color GrassColor = new(0.32f, 0.50f, 0.22f);
    private static readonly Color WallColor = new(0.18f, 0.16f, 0.14f);
    private static readonly Color DummyColor = new(0.95f, 0.55f, 0.20f);

    public SimHost? Host { get; set; }

    public override void _Ready()
    {
        if (Host is null) return;
        BuildMapTexture(Host.Map);
    }

    public override void _Process(double delta)
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_mapTex is null || Host is null) return;

        DrawTextureRect(
            _mapTex,
            new Rect2(0, 0, _mapPixelWidth, _mapPixelHeight),
            tile: false);

        var snap = Host.LatestSnapshot;
        if (snap is null) return;

        float radius = PixelsPerTile * 0.35f;
        foreach (var d in snap.Dummies)
        {
            DrawCircle(new Vector2(d.X * PixelsPerTile, d.Y * PixelsPerTile), radius, DummyColor);
        }
    }

    private void BuildMapTexture(TileMap map)
    {
        var img = Image.CreateEmpty(map.Width, map.Height, false, Image.Format.Rgba8);
        for (int y = 0; y < map.Height; y++)
        {
            for (int x = 0; x < map.Width; x++)
            {
                var c = map.Get(x, y) == TileType.Wall ? WallColor : GrassColor;
                img.SetPixel(x, y, c);
            }
        }
        _mapTex = ImageTexture.CreateFromImage(img);
        _mapPixelWidth = map.Width * PixelsPerTile;
        _mapPixelHeight = map.Height * PixelsPerTile;
    }
}
