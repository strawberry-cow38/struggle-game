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

    // Door tiles flagged Forbidden by the player. Pathing treats them
    // as walls — A* skips, the mover refuses to step. Built doors that
    // aren't forbidden still pass through Walkable normally (gating
    // happens at the mover via DoorState).
    public IReadOnlyList<TilePos> ForbiddenDoors { get; }

    // All built (non-blueprint) door tiles. Pathing uses this set to
    // bias A* — each door's per-priority cost lives in _doorCostByTile
    // and is what CostAt actually reads. The list is kept too because
    // some consumers want to enumerate door tiles directly.
    public IReadOnlyList<TilePos> DoorTiles { get; }

    // Tiles occupied by furniture footprints (currently just beds).
    // Pathing keeps them WALKABLE but heavily weighted so A* routes
    // around them unless they're the actual destination. Sleepers can
    // still occupy the head tile and players can shove a colonist
    // across one if they really need to.
    public IReadOnlyList<TilePos> FurnitureTiles { get; }
    public const float FurnitureCost = 8.0f;

    // Tiles occupied by blocking furniture (currently Ur boards) — same
    // FurnitureTiles semantics for cost weighting but pathing treats
    // them like trees: Walkable returns false. Beds remain walkable so
    // sleepers can climb in; boards don't, since you stand adjacent.
    public IReadOnlyList<TilePos> BlockingFurniture { get; }

    // Exposed for TileMap.Snapshot to share unchanged chunk byte[]s into
    // the next MapView. Outside callers should go through GetWall/etc.
    internal byte[][] TerrainChunks { get; }
    internal byte[][] FlooringChunks { get; }
    internal byte[][] WallChunks { get; }
    internal byte[][] RoofChunks { get; }

    private readonly HashSet<TilePos> _treeSet;
    private readonly HashSet<TilePos> _forbiddenDoorSet;
    private readonly HashSet<TilePos> _furnitureSet;
    private readonly HashSet<TilePos> _blockingFurnitureSet;
    private readonly Dictionary<TilePos, float> _doorCostByTile;

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
        IReadOnlyList<TilePos>? trees = null,
        IReadOnlyList<TilePos>? forbiddenDoors = null,
        IReadOnlyList<TilePos>? doorTiles = null,
        IReadOnlyList<float>? doorCosts = null,
        IReadOnlyList<TilePos>? furnitureTiles = null,
        IReadOnlyList<TilePos>? blockingFurniture = null)
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
        ForbiddenDoors = forbiddenDoors ?? Array.Empty<TilePos>();
        _forbiddenDoorSet = new HashSet<TilePos>(ForbiddenDoors);
        DoorTiles = doorTiles ?? Array.Empty<TilePos>();
        FurnitureTiles = furnitureTiles ?? Array.Empty<TilePos>();
        _furnitureSet = new HashSet<TilePos>(FurnitureTiles);
        BlockingFurniture = blockingFurniture ?? Array.Empty<TilePos>();
        _blockingFurnitureSet = new HashSet<TilePos>(BlockingFurniture);
        _doorCostByTile = new Dictionary<TilePos, float>(DoorTiles.Count);
        if (doorCosts is not null && doorCosts.Count == DoorTiles.Count)
        {
            for (int i = 0; i < DoorTiles.Count; i++)
            {
                _doorCostByTile[DoorTiles[i]] = doorCosts[i];
            }
        }
        else
        {
            // Caller didn't supply per-door costs (legacy callers / tests) —
            // fall back to the original Medium cost so paths behave the
            // same as before per-door priority shipped.
            foreach (var t in DoorTiles) _doorCostByTile[t] = 1.30f;
        }

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

    // Outermost ring = magic border (matches TileMap.IsBorder).
    public bool IsBorder(int x, int y) => x == 0 || y == 0 || x == Width - 1 || y == Height - 1;
    public bool IsBorder(TilePos p) => IsBorder(p.X, p.Y);

    public bool HasTree(int x, int y) => _treeSet.Contains(new TilePos(x, y));
    public bool HasTree(TilePos p) => _treeSet.Contains(p);

    // Per-tile pathing cost multiplier. A* multiplies the base edge
    // cost (1.0 ortho / 1.41 diag) by the destination tile's cost.
    // Door cost is the per-door value the player set via priority;
    // wood floor is a small bonus so pawns prefer indoor hallways.
    //   Door:     (per-priority — see DoorPathing.CostFor)
    //   Wood:     0.80  (prefer indoor hallways)
    //   default:  1.00
    public float CostAt(int x, int y)
    {
        var p = new TilePos(x, y);
        if (_doorCostByTile.TryGetValue(p, out var dc)) return dc;
        if (_furnitureSet.Contains(p)) return FurnitureCost;
        if ((FlooringType)RawFlooringByte(x, y) == FlooringType.Wood) return 0.80f;
        return 1.00f;
    }
    public float CostAt(TilePos p) => CostAt(p.X, p.Y);

    public bool Walkable(int x, int y)
    {
        if (!InBounds(x, y)) return false;
        if (IsBorder(x, y)) return false;
        if (RawWallByte(x, y) != 0) return false;
        var p = new TilePos(x, y);
        if (_treeSet.Contains(p)) return false;
        if (_forbiddenDoorSet.Contains(p)) return false;
        if (_blockingFurnitureSet.Contains(p)) return false;
        // Furniture (beds) intentionally walkable — see CostAt.
        return true;
    }

    public bool HasFurniture(TilePos p) => _furnitureSet.Contains(p) || _blockingFurnitureSet.Contains(p);
    public bool HasFurniture(int x, int y) { var p = new TilePos(x, y); return _furnitureSet.Contains(p) || _blockingFurnitureSet.Contains(p); }
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
