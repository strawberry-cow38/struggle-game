namespace StruggleGame.Sim.Map;

public sealed class TileMap
{
    private readonly TileType[] _tiles;

    public int Width { get; }
    public int Height { get; }

    public TileMap(int width, int height)
    {
        Width = width;
        Height = height;
        _tiles = new TileType[width * height];
    }

    public TileType Get(int x, int y) => _tiles[Index(x, y)];
    public TileType Get(TilePos p) => _tiles[Index(p.X, p.Y)];

    public void Set(int x, int y, TileType t) => _tiles[Index(x, y)] = t;
    public void Set(TilePos p, TileType t) => _tiles[Index(p.X, p.Y)] = t;

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public bool InBounds(TilePos p) => InBounds(p.X, p.Y);

    public bool Walkable(int x, int y) => InBounds(x, y) && _tiles[Index(x, y)] != TileType.Wall;
    public bool Walkable(TilePos p) => Walkable(p.X, p.Y);

    public ReadOnlySpan<TileType> RawTiles => _tiles;

    private int Index(int x, int y) => y * Width + x;

    // Deterministic procgen: mostly grass with a scattering of wall clusters
    // so the dummy has obstacles to path around. Seeded so the map is the
    // same every run (foundation phase).
    public static TileMap GenerateDefault(int width, int height, int seed = 1337)
    {
        var map = new TileMap(width, height);
        var rng = new Random(seed);

        // Drop ~40 random wall clusters of 4–12 tiles each.
        for (int cluster = 0; cluster < 40; cluster++)
        {
            int cx = rng.Next(width);
            int cy = rng.Next(height);
            int size = rng.Next(4, 13);
            for (int i = 0; i < size; i++)
            {
                int wx = cx + rng.Next(-2, 3);
                int wy = cy + rng.Next(-2, 3);
                if (map.InBounds(wx, wy))
                {
                    map.Set(wx, wy, TileType.Wall);
                }
            }
        }

        // Border of walls so the dummy can't path off the edge.
        for (int x = 0; x < width; x++)
        {
            map.Set(x, 0, TileType.Wall);
            map.Set(x, height - 1, TileType.Wall);
        }
        for (int y = 0; y < height; y++)
        {
            map.Set(0, y, TileType.Wall);
            map.Set(width - 1, y, TileType.Wall);
        }

        return map;
    }
}
