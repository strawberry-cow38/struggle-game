namespace StruggleGame.Sim.Map;

// Immutable, versioned read-only view of the tile grid. Built by SimRuntime
// whenever the map mutates. Readers from other threads (future A* worker
// pool) capture a reference and use it for the lifetime of a single
// computation; the snapshot won't change under them. Stale snapshots are
// fine — the requester gen check rejects results computed against an
// outdated map.
//
// Storage is chunked: each layer is byte[ChunkCount][] where each chunk
// holds MapChunks.ChunkArea bytes. TileMap.Snapshot rebuilds only the
// chunk byte[]s that mutated since the previous snapshot; unchanged
// chunks share the previous snapshot's byte[] by reference. For a small
// edit (e.g. one wall) that's one fresh ChunkArea allocation instead of
// cloning the whole map array.
public sealed class MapView
{
    public long Version { get; }
    public int Width { get; }
    public int Height { get; }
    public int ChunksAcross { get; }
    public int ChunksDown { get; }
    public int ChunkCount => ChunksAcross * ChunksDown;

    // Pre-scanned list of every wall tile. Built once at construction so
    // wander/anchor lookups don't rescan the grid each call.
    public IReadOnlyList<TilePos> Walls { get; }

    // Subset of Walls actually placed by the player (via blueprint
    // completion). Excludes the border + procgen clusters.
    public IReadOnlyList<TilePos> PlayerWalls { get; }

    // Tiles occupied by standing trees. Blocks walkability.
    public IReadOnlyList<TilePos> Trees { get; }

    // Exposed for TileMap.Snapshot to share unchanged chunk byte[]s into
    // the next MapView. Outside callers should go through GetWall/etc.
    internal byte[][] TerrainChunks { get; }
    internal byte[][] FlooringChunks { get; }
    internal byte[][] WallChunks { get; }
    internal byte[][] RoofChunks { get; }

    private readonly HashSet<TilePos> _treeSet;

    public MapView(
        long version,
        int width,
        int height,
        int chunksAcross,
        int chunksDown,
        byte[][] terrainChunks,
        byte[][] flooringChunks,
        byte[][] wallChunks,
        byte[][] roofChunks,
        IReadOnlyList<TilePos> playerWalls,
        IReadOnlyList<TilePos>? trees = null)
    {
        Version = version;
        Width = width;
        Height = height;
        ChunksAcross = chunksAcross;
        ChunksDown = chunksDown;
        TerrainChunks = terrainChunks;
        FlooringChunks = flooringChunks;
        WallChunks = wallChunks;
        RoofChunks = roofChunks;
        PlayerWalls = playerWalls;
        Trees = trees ?? Array.Empty<TilePos>();
        _treeSet = new HashSet<TilePos>(Trees);

        var wallList = new List<TilePos>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (RawWallByte(x, y) != 0) wallList.Add(new TilePos(x, y));
            }
        }
        Walls = wallList;
    }

    public TerrainType GetTerrain(int x, int y) => (TerrainType)RawTerrainByte(x, y);
    public TerrainType GetTerrain(TilePos p) => GetTerrain(p.X, p.Y);
    public FlooringType GetFlooring(int x, int y) => (FlooringType)RawFlooringByte(x, y);
    public FlooringType GetFlooring(TilePos p) => GetFlooring(p.X, p.Y);
    public WallType GetWall(int x, int y) => (WallType)RawWallByte(x, y);
    public WallType GetWall(TilePos p) => GetWall(p.X, p.Y);
    public RoofType GetRoof(int x, int y) => (RoofType)RawRoofByte(x, y);
    public RoofType GetRoof(TilePos p) => GetRoof(p.X, p.Y);

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public bool InBounds(TilePos p) => InBounds(p.X, p.Y);

    public bool HasTree(int x, int y) => _treeSet.Contains(new TilePos(x, y));
    public bool HasTree(TilePos p) => _treeSet.Contains(p);

    public bool Walkable(int x, int y) =>
        InBounds(x, y) && RawWallByte(x, y) == 0 && !_treeSet.Contains(new TilePos(x, y));
    public bool Walkable(TilePos p) => Walkable(p.X, p.Y);

    private byte RawTerrainByte(int x, int y) => TerrainChunks[MapChunks.ChunkIndex(x, y, ChunksAcross)][MapChunks.LocalIndex(x, y)];
    private byte RawFlooringByte(int x, int y) => FlooringChunks[MapChunks.ChunkIndex(x, y, ChunksAcross)][MapChunks.LocalIndex(x, y)];
    private byte RawWallByte(int x, int y) => WallChunks[MapChunks.ChunkIndex(x, y, ChunksAcross)][MapChunks.LocalIndex(x, y)];
    private byte RawRoofByte(int x, int y) => RoofChunks[MapChunks.ChunkIndex(x, y, ChunksAcross)][MapChunks.LocalIndex(x, y)];

    // Flatten a single layer into a fresh contiguous byte[] for callers
    // that need flat row-major data (RoomMap flood fill, renderer
    // overlay textures). O(W*H) — same cost the old MapView paid for
    // every snapshot. With chunking that work only happens when a
    // consumer asks for it, not on every snapshot.
    public byte[] AssembleFlat(MapLayer layer)
    {
        var chunks = layer switch
        {
            MapLayer.Terrain => TerrainChunks,
            MapLayer.Flooring => FlooringChunks,
            MapLayer.Wall => WallChunks,
            MapLayer.Roof => RoofChunks,
            _ => throw new ArgumentOutOfRangeException(nameof(layer)),
        };
        var flat = new byte[Width * Height];
        for (int cy = 0; cy < ChunksDown; cy++)
        {
            int baseY = cy * MapChunks.ChunkSize;
            int colSpan = Math.Min(MapChunks.ChunkSize, Height - baseY);
            for (int cx = 0; cx < ChunksAcross; cx++)
            {
                int baseX = cx * MapChunks.ChunkSize;
                int rowSpan = Math.Min(MapChunks.ChunkSize, Width - baseX);
                var chunk = chunks[cy * ChunksAcross + cx];
                for (int ly = 0; ly < colSpan; ly++)
                {
                    int srcRow = ly * MapChunks.ChunkSize;
                    int dstRow = (baseY + ly) * Width + baseX;
                    Buffer.BlockCopy(chunk, srcRow, flat, dstRow, rowSpan);
                }
            }
        }
        return flat;
    }
}
