namespace StruggleGame.Sim.Map;

// Four parallel byte arrays, one per layer. Walls block walkability;
// terrain/flooring/roof are presentation today (terrain may add water
// later). Keeping arrays parallel + byte-sized makes Snapshot a flat
// memcpy quartet and lets the renderer pull each layer cheaply.
public sealed class TileMap
{
    private readonly byte[] _terrain;
    private readonly byte[] _flooring;
    private readonly byte[] _walls;
    private readonly byte[] _roofs;

    public int Width { get; }
    public int Height { get; }

    public TileMap(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        _terrain = new byte[n];
        _flooring = new byte[n];
        _walls = new byte[n];
        _roofs = new byte[n];
    }

    public TerrainType GetTerrain(int x, int y) => (TerrainType)_terrain[Index(x, y)];
    public TerrainType GetTerrain(TilePos p) => GetTerrain(p.X, p.Y);
    public void SetTerrain(int x, int y, TerrainType t) => _terrain[Index(x, y)] = (byte)t;
    public void SetTerrain(TilePos p, TerrainType t) => SetTerrain(p.X, p.Y, t);

    public FlooringType GetFlooring(int x, int y) => (FlooringType)_flooring[Index(x, y)];
    public FlooringType GetFlooring(TilePos p) => GetFlooring(p.X, p.Y);
    public void SetFlooring(int x, int y, FlooringType t) => _flooring[Index(x, y)] = (byte)t;
    public void SetFlooring(TilePos p, FlooringType t) => SetFlooring(p.X, p.Y, t);

    public WallType GetWall(int x, int y) => (WallType)_walls[Index(x, y)];
    public WallType GetWall(TilePos p) => GetWall(p.X, p.Y);
    public void SetWall(int x, int y, WallType t) => _walls[Index(x, y)] = (byte)t;
    public void SetWall(TilePos p, WallType t) => SetWall(p.X, p.Y, t);

    public RoofType GetRoof(int x, int y) => (RoofType)_roofs[Index(x, y)];
    public RoofType GetRoof(TilePos p) => GetRoof(p.X, p.Y);
    public void SetRoof(int x, int y, RoofType t) => _roofs[Index(x, y)] = (byte)t;
    public void SetRoof(TilePos p, RoofType t) => SetRoof(p.X, p.Y, t);

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public bool InBounds(TilePos p) => InBounds(p.X, p.Y);

    public bool Walkable(int x, int y) => InBounds(x, y) && _walls[Index(x, y)] == 0;
    public bool Walkable(TilePos p) => Walkable(p.X, p.Y);

    public ReadOnlySpan<byte> RawTerrain => _terrain;
    public ReadOnlySpan<byte> RawFlooring => _flooring;
    public ReadOnlySpan<byte> RawWalls => _walls;
    public ReadOnlySpan<byte> RawRoofs => _roofs;

    // Build an immutable read-only snapshot at the given version. Caller is
    // responsible for serialising writes vs this read (SimRuntime holds the
    // map lock for both).
    public MapView Snapshot(
        long version,
        IReadOnlyList<TilePos>? playerWalls = null,
        IReadOnlyList<TilePos>? trees = null)
    {
        return new MapView(
            version, Width, Height,
            (byte[])_terrain.Clone(),
            (byte[])_flooring.Clone(),
            (byte[])_walls.Clone(),
            (byte[])_roofs.Clone(),
            playerWalls ?? Array.Empty<TilePos>(),
            trees);
    }

    private int Index(int x, int y) => y * Width + x;

    // Deterministic procgen: grass terrain everywhere with a scattering of
    // stone wall clusters so the dummy has obstacles to path around.
    // Seeded so the map is the same every run (foundation phase).
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
                    map.SetWall(wx, wy, WallType.Stone);
                }
            }
        }

        // Border of walls so the dummy can't path off the edge.
        for (int x = 0; x < width; x++)
        {
            map.SetWall(x, 0, WallType.Stone);
            map.SetWall(x, height - 1, WallType.Stone);
        }
        for (int y = 0; y < height; y++)
        {
            map.SetWall(0, y, WallType.Stone);
            map.SetWall(width - 1, y, WallType.Stone);
        }

        return map;
    }
}
