namespace StruggleGame.Sim.Map;

// Immutable, versioned read-only view of the tile grid. Built by SimRuntime
// whenever the map mutates (currently: blueprint completion). Readers from
// other threads (future A* worker pool) capture a reference and use it for
// the lifetime of a single computation; the snapshot won't change under
// them. Stale snapshots are fine — the requester gen check rejects results
// computed against an outdated map.
public sealed class MapView
{
    public long Version { get; }
    public int Width { get; }
    public int Height { get; }

    // Pre-scanned list of every wall tile. Built once at construction so
    // wander/anchor lookups don't rescan the grid each call. Treated as
    // immutable for the snapshot's lifetime.
    public IReadOnlyList<TilePos> Walls { get; }

    // Subset of Walls actually placed by the player (via blueprint
    // completion). Excludes the border + procgen clusters. Wander logic
    // uses this so colonists hover around real player structures.
    public IReadOnlyList<TilePos> PlayerWalls { get; }

    private readonly TileType[] _tiles;

    public MapView(long version, int width, int height, TileType[] tiles, IReadOnlyList<TilePos> playerWalls)
    {
        Version = version;
        Width = width;
        Height = height;
        _tiles = tiles;
        PlayerWalls = playerWalls;

        var walls = new List<TilePos>();
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (tiles[row + x] == TileType.Wall) walls.Add(new TilePos(x, y));
            }
        }
        Walls = walls;
    }

    public TileType Get(int x, int y) => _tiles[y * Width + x];
    public TileType Get(TilePos p) => _tiles[p.Y * Width + p.X];

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public bool InBounds(TilePos p) => InBounds(p.X, p.Y);

    public bool Walkable(int x, int y) => InBounds(x, y) && _tiles[y * Width + x] != TileType.Wall;
    public bool Walkable(TilePos p) => Walkable(p.X, p.Y);

    public ReadOnlySpan<TileType> RawTiles => _tiles;
}
