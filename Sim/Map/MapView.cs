namespace StruggleGame.Sim.Map;

// Immutable, versioned read-only view of the tile grid. Built by SimRuntime
// whenever the map mutates. Readers from other threads (future A* worker
// pool) capture a reference and use it for the lifetime of a single
// computation; the snapshot won't change under them. Stale snapshots are
// fine — the requester gen check rejects results computed against an
// outdated map.
//
// Tiles are layered: terrain → flooring → wall → roof. Each layer holds
// a byte per tile so a snapshot is four flat arrays.
public sealed class MapView
{
    public long Version { get; }
    public int Width { get; }
    public int Height { get; }

    // Pre-scanned list of every wall tile. Built once at construction so
    // wander/anchor lookups don't rescan the grid each call.
    public IReadOnlyList<TilePos> Walls { get; }

    // Subset of Walls actually placed by the player (via blueprint
    // completion). Excludes the border + procgen clusters.
    public IReadOnlyList<TilePos> PlayerWalls { get; }

    // Tiles occupied by standing trees. Blocks walkability.
    public IReadOnlyList<TilePos> Trees { get; }

    private readonly byte[] _terrain;
    private readonly byte[] _flooring;
    private readonly byte[] _walls;
    private readonly byte[] _roofs;
    private readonly HashSet<TilePos> _treeSet;

    public MapView(
        long version,
        int width,
        int height,
        byte[] terrain,
        byte[] flooring,
        byte[] walls,
        byte[] roofs,
        IReadOnlyList<TilePos> playerWalls,
        IReadOnlyList<TilePos>? trees = null)
    {
        Version = version;
        Width = width;
        Height = height;
        _terrain = terrain;
        _flooring = flooring;
        _walls = walls;
        _roofs = roofs;
        PlayerWalls = playerWalls;
        Trees = trees ?? Array.Empty<TilePos>();
        _treeSet = new HashSet<TilePos>(Trees);

        var wallList = new List<TilePos>();
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (walls[row + x] != 0) wallList.Add(new TilePos(x, y));
            }
        }
        Walls = wallList;
    }

    public TerrainType GetTerrain(int x, int y) => (TerrainType)_terrain[y * Width + x];
    public TerrainType GetTerrain(TilePos p) => GetTerrain(p.X, p.Y);
    public FlooringType GetFlooring(int x, int y) => (FlooringType)_flooring[y * Width + x];
    public FlooringType GetFlooring(TilePos p) => GetFlooring(p.X, p.Y);
    public WallType GetWall(int x, int y) => (WallType)_walls[y * Width + x];
    public WallType GetWall(TilePos p) => GetWall(p.X, p.Y);
    public RoofType GetRoof(int x, int y) => (RoofType)_roofs[y * Width + x];
    public RoofType GetRoof(TilePos p) => GetRoof(p.X, p.Y);

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public bool InBounds(TilePos p) => InBounds(p.X, p.Y);

    public bool HasTree(int x, int y) => _treeSet.Contains(new TilePos(x, y));
    public bool HasTree(TilePos p) => _treeSet.Contains(p);

    public bool Walkable(int x, int y) =>
        InBounds(x, y) && _walls[y * Width + x] == 0 && !_treeSet.Contains(new TilePos(x, y));
    public bool Walkable(TilePos p) => Walkable(p.X, p.Y);

    public ReadOnlySpan<byte> RawTerrain => _terrain;
    public ReadOnlySpan<byte> RawFlooring => _flooring;
    public ReadOnlySpan<byte> RawWalls => _walls;
    public ReadOnlySpan<byte> RawRoofs => _roofs;
}
