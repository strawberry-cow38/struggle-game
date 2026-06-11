namespace StruggleGame.Sim.Map;

// Four parallel byte arrays, one per layer. Walls block walkability;
// terrain/flooring/roof are presentation today (terrain may add water
// later). Per-chunk dirty bits drive incremental Snapshot: only chunks
// that mutated since the previous snapshot need to be cloned into the
// new MapView; the rest are reused from the previous snapshot by ref.
public sealed class TileMap
{
    private readonly byte[] _terrain;
    private readonly byte[] _flooring;
    private readonly byte[] _walls;
    private readonly byte[] _roofs;

    private readonly bool[] _terrainChunkDirty;
    private readonly bool[] _flooringChunkDirty;
    private readonly bool[] _wallChunkDirty;
    private readonly bool[] _roofChunkDirty;

    public int Width { get; }
    public int Height { get; }
    public int ChunksAcross { get; }
    public int ChunksDown { get; }
    public int ChunkCount => ChunksAcross * ChunksDown;

    public TileMap(int width, int height)
    {
        Width = width;
        Height = height;
        int n = width * height;
        _terrain = new byte[n];
        _flooring = new byte[n];
        _walls = new byte[n];
        _roofs = new byte[n];

        ChunksAcross = MapChunks.ChunksAcross(width);
        ChunksDown = MapChunks.ChunksDown(height);
        int c = ChunksAcross * ChunksDown;
        _terrainChunkDirty = new bool[c];
        _flooringChunkDirty = new bool[c];
        _wallChunkDirty = new bool[c];
        _roofChunkDirty = new bool[c];
        // First snapshot must populate every chunk.
        for (int i = 0; i < c; i++)
        {
            _terrainChunkDirty[i] = true;
            _flooringChunkDirty[i] = true;
            _wallChunkDirty[i] = true;
            _roofChunkDirty[i] = true;
        }
    }

    public TerrainType GetTerrain(int x, int y) => (TerrainType)_terrain[Index(x, y)];
    public TerrainType GetTerrain(TilePos p) => GetTerrain(p.X, p.Y);
    public void SetTerrain(int x, int y, TerrainType t)
    {
        _terrain[Index(x, y)] = (byte)t;
        _terrainChunkDirty[MapChunks.ChunkIndex(x, y, ChunksAcross)] = true;
    }
    public void SetTerrain(TilePos p, TerrainType t) => SetTerrain(p.X, p.Y, t);

    public FlooringType GetFlooring(int x, int y) => (FlooringType)_flooring[Index(x, y)];
    public FlooringType GetFlooring(TilePos p) => GetFlooring(p.X, p.Y);
    public void SetFlooring(int x, int y, FlooringType t)
    {
        _flooring[Index(x, y)] = (byte)t;
        _flooringChunkDirty[MapChunks.ChunkIndex(x, y, ChunksAcross)] = true;
    }
    public void SetFlooring(TilePos p, FlooringType t) => SetFlooring(p.X, p.Y, t);

    // Wall reads use Volatile.Read: the Godot main thread calls GetWall /
    // Walkable directly (WallInfoPanel, Selector) while the sim thread
    // writes via SetWall. _walls is allocated once and never replaced, so
    // a single byte element read can't tear; the volatile read just keeps
    // the JIT from caching a stale value across the racing write. Worst
    // case is a one-frame-stale wall byte, which is fine for UI reads —
    // an explicit lock here would cost more on the hot sim-side callers
    // of these same accessors than the race ever could.
    public WallType GetWall(int x, int y) => (WallType)Volatile.Read(ref _walls[Index(x, y)]);
    public WallType GetWall(TilePos p) => GetWall(p.X, p.Y);
    public void SetWall(int x, int y, WallType t)
    {
        _walls[Index(x, y)] = (byte)t;
        _wallChunkDirty[MapChunks.ChunkIndex(x, y, ChunksAcross)] = true;
    }
    public void SetWall(TilePos p, WallType t) => SetWall(p.X, p.Y, t);

    public RoofType GetRoof(int x, int y) => (RoofType)_roofs[Index(x, y)];
    public RoofType GetRoof(TilePos p) => GetRoof(p.X, p.Y);
    public void SetRoof(int x, int y, RoofType t)
    {
        _roofs[Index(x, y)] = (byte)t;
        _roofChunkDirty[MapChunks.ChunkIndex(x, y, ChunksAcross)] = true;
    }
    public void SetRoof(TilePos p, RoofType t) => SetRoof(p.X, p.Y, t);

    public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;
    public bool InBounds(TilePos p) => InBounds(p.X, p.Y);

    // Outermost ring is a magic border — impassable but not a wall. Keeps
    // pawns inside the map without showing as a stone wall they could
    // try to deconstruct or that would enclose the map as a "room".
    public bool IsBorder(int x, int y) => x == 0 || y == 0 || x == Width - 1 || y == Height - 1;
    public bool IsBorder(TilePos p) => IsBorder(p.X, p.Y);

    public bool Walkable(int x, int y) => InBounds(x, y) && !IsBorder(x, y) && Volatile.Read(ref _walls[Index(x, y)]) == 0;
    public bool Walkable(TilePos p) => Walkable(p.X, p.Y);

    public ReadOnlySpan<byte> RawTerrain => _terrain;
    public ReadOnlySpan<byte> RawFlooring => _flooring;
    public ReadOnlySpan<byte> RawWalls => _walls;
    public ReadOnlySpan<byte> RawRoofs => _roofs;

    // Incremental snapshot. For each chunk: if dirty since the previous
    // snapshot, allocate a fresh chunk byte[] and copy the chunk's tile
    // bytes out of the flat backing array; otherwise reuse the previous
    // snapshot's chunk ref by reference. Clears dirty bits after.
    //
    // Reused chunk refs are safe because TileMap never writes back into
    // a MapView's chunk byte[] — mutations land in the flat _walls etc.
    // array and the NEXT snapshot allocates a new chunk byte[] for any
    // chunk that was touched.
    public MapView Snapshot(
        long version,
        MapView? previous = null,
        IReadOnlyList<TilePos>? playerWalls = null,
        IReadOnlyList<TilePos>? trees = null,
        IReadOnlyList<TilePos>? forbiddenDoors = null,
        IReadOnlyList<TilePos>? doorTiles = null,
        IReadOnlyList<float>? doorCosts = null,
        IReadOnlyList<TilePos>? furnitureTiles = null,
        IReadOnlyList<TilePos>? blockingFurniture = null)
    {
        var terrainChunks = BuildChunks(_terrain, _terrainChunkDirty, previous?.TerrainChunks);
        var flooringChunks = BuildChunks(_flooring, _flooringChunkDirty, previous?.FlooringChunks);
        var wallChunks = BuildChunks(_walls, _wallChunkDirty, previous?.WallChunks);
        var roofChunks = BuildChunks(_roofs, _roofChunkDirty, previous?.RoofChunks);

        Array.Clear(_terrainChunkDirty);
        Array.Clear(_flooringChunkDirty);
        Array.Clear(_wallChunkDirty);
        Array.Clear(_roofChunkDirty);

        return new MapView(
            version, Width, Height, ChunksAcross, ChunksDown,
            terrainChunks, flooringChunks, wallChunks, roofChunks,
            playerWalls ?? Array.Empty<TilePos>(),
            trees,
            forbiddenDoors,
            doorTiles,
            doorCosts,
            furnitureTiles,
            blockingFurniture);
    }

    private byte[][] BuildChunks(byte[] flat, bool[] dirty, byte[][]? prev)
    {
        var chunks = new byte[ChunkCount][];
        for (int cy = 0; cy < ChunksDown; cy++)
        {
            for (int cx = 0; cx < ChunksAcross; cx++)
            {
                int ci = cy * ChunksAcross + cx;
                if (dirty[ci] || prev is null || prev[ci] is null)
                {
                    chunks[ci] = CopyChunkFromFlat(flat, cx, cy);
                }
                else
                {
                    chunks[ci] = prev[ci];
                }
            }
        }
        return chunks;
    }

    private byte[] CopyChunkFromFlat(byte[] flat, int cx, int cy)
    {
        var chunk = new byte[MapChunks.ChunkArea];
        int baseX = cx * MapChunks.ChunkSize;
        int baseY = cy * MapChunks.ChunkSize;
        int rowSpan = Math.Min(MapChunks.ChunkSize, Width - baseX);
        int colSpan = Math.Min(MapChunks.ChunkSize, Height - baseY);
        for (int ly = 0; ly < colSpan; ly++)
        {
            int srcRow = (baseY + ly) * Width + baseX;
            int dstRow = ly * MapChunks.ChunkSize;
            Buffer.BlockCopy(flat, srcRow, chunk, dstRow, rowSpan);
        }
        return chunk;
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
                if (map.InBounds(wx, wy) && !map.IsBorder(wx, wy))
                {
                    map.SetWall(wx, wy, WallType.Stone);
                }
            }
        }

        // No border walls — the outer ring is a magic border (IsBorder)
        // that Walkable refuses to enter, so pawns still can't path off
        // the edge and the renderer doesn't draw stone walls around the
        // map perimeter.
        return map;
    }
}
