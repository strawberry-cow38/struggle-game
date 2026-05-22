using System.Collections.Concurrent;
using Friflo.Engine.ECS;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Diagnostics;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim;

public sealed class SimRuntime
{
    public EntityStore Store { get; } = new();
    public TileMap Map { get; }
    public JobBoard Jobs { get; } = new();
    public long Tick { get; private set; }
    public long MapVersion { get; private set; }

    private MapView _mapView = null!;
    public MapView MapView => Volatile.Read(ref _mapView);

    public PathService PathService { get; }
    public SimWatcher Watcher { get; } = new();

    private readonly DummyController _dummies;
    private readonly BuildSystem _builds;
    private readonly ChopSystem _chops;
    private readonly SafetySystem _safety;
    private readonly ConcurrentQueue<ISimCommand> _commands = new();
    private readonly object _mapLock = new();
    private readonly List<TilePos> _playerWalls = new();
    private readonly Dictionary<TilePos, Entity> _trees = new();
    private readonly Random _spawnRng;
    private const int InitialTreeCount = 50;

    public SimRuntime(int seed = 1337)
    {
        Map = TileMap.GenerateDefault(SimConstants.MapSize, SimConstants.MapSize, seed);
        _spawnRng = new Random(seed + 7);
        PathService = new PathService(Map.Width, Map.Height, () => MapView);
        _dummies = new DummyController(PathService, Jobs, () => MapView, CancelJob, seed + 1);
        _builds = new BuildSystem(this, Jobs);
        _chops = new ChopSystem(this, Jobs);
        _safety = new SafetySystem(() => MapView, PathService, Watcher);

        // Trees go down before colonists so spawn can avoid landing on one.
        for (int i = 0; i < InitialTreeCount; i++) SpawnRandomTree();
        RebuildMapView();

        int center = SimConstants.MapSize / 2;
        for (int i = 0; i < 3; i++) SpawnDummy(center, center);
    }

    public void Step(float dt)
    {
        while (_commands.TryDequeue(out var cmd)) cmd.Apply(this);
        _dummies.Step(Store, dt);
        _builds.Step(Store, dt);
        _chops.Step(Store, dt);
        _safety.Step(Store, Tick);
        Tick++;
        Watcher.Observe(Tick, Store, Jobs);
    }

    public IReadOnlyCollection<TilePos> TreeTiles => _trees.Keys;
    public bool TryGetTree(TilePos tile, out Entity entity) => _trees.TryGetValue(tile, out entity!);

    public void QueueCommand(ISimCommand cmd) => _commands.Enqueue(cmd);

    public SimSnapshot BuildSnapshot(int? selectedDummyId = null, IReadOnlyCollection<int>? selectedTreeIds = null)
    {
        var dq = Store.Query<WorldPos, Wanderer>();
        var dummies = new DummyState[dq.Count];
        TilePos[]? selectedPath = null;
        TilePos[]? selectedOrders = null;
        int i = 0;
        dq.ForEachEntity((ref WorldPos p, ref Wanderer _, Entity ent) =>
        {
            bool drafted = ent.HasComponent<Drafted>();
            string label = drafted ? "Drafted" : "Idle";
            if (!drafted && ent.HasComponent<BuildTarget>())
            {
                var bt = ent.GetComponent<BuildTarget>();
                var j = Jobs.Get(bt.JobId);
                if (j is not null) label = j.Kind.ToString();
            }
            dummies[i++] = new DummyState(ent.Id, p.X, p.Y, label, drafted);

            if (selectedDummyId is int sel && ent.Id == sel)
            {
                if (ent.HasComponent<PathFollower>())
                {
                    var pf = ent.GetComponent<PathFollower>();
                    if (pf.Waypoints is { Count: > 0 })
                    {
                        int remaining = pf.Waypoints.Count - pf.Index;
                        if (remaining > 0)
                        {
                            selectedPath = new TilePos[remaining];
                            for (int k = 0; k < remaining; k++)
                                selectedPath[k] = pf.Waypoints[pf.Index + k];
                        }
                    }
                }
                if (ent.HasComponent<OrderQueue>())
                {
                    var oq = ent.GetComponent<OrderQueue>();
                    if (oq.Tiles is { Count: > 0 })
                    {
                        selectedOrders = oq.Tiles.ToArray();
                    }
                }
            }
        });

        var bps = new BlueprintState[Jobs.Count];
        int j = 0;
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.WallBuild) continue;
            var bp = job.Entity.GetComponent<Blueprint>();
            bps[j++] = new BlueprintState(job.Tile, bp.ProgressSec / BuildSystem.BuildTimeSec);
        }
        if (j < bps.Length) Array.Resize(ref bps, j);

        var trees = new TreeState[_trees.Count];
        int k = 0;
        foreach (var (tile, ent) in _trees)
        {
            var tc = ent.GetComponent<Tree>();
            bool hasJob = Jobs.GetByTile(tile)?.Kind == JobKind.ChopTree;
            trees[k++] = new TreeState(ent.Id, tile, tc.ChopProgressSec / ChopSystem.ChopTimeSec, hasJob);
        }

        var woodQuery = Store.Query<Wood>();
        var woods = new WoodState[woodQuery.Count];
        int wi = 0;
        woodQuery.ForEachEntity((ref Wood w, Entity _) => { woods[wi++] = new WoodState(w.Tile); });

        int[]? selTreeArr = null;
        if (selectedTreeIds is { Count: > 0 })
        {
            selTreeArr = new int[selectedTreeIds.Count];
            int si = 0;
            foreach (var id in selectedTreeIds) selTreeArr[si++] = id;
        }

        return new SimSnapshot(
            Tick, MapVersion, dummies, bps, trees, woods,
            selectedDummyId, selectedPath, selectedOrders, selTreeArr);
    }

    // Snapshot of the tile array taken under a lock so a parallel write
    // can't tear it. Game uses this to rebuild the wall overlay texture
    // when MapVersion changes.
    public byte[] CopyTilesForRender()
    {
        lock (_mapLock)
        {
            var src = Map.RawTiles;
            var copy = new byte[src.Length];
            for (int k = 0; k < src.Length; k++) copy[k] = (byte)src[k];
            return copy;
        }
    }

    public bool TryPlaceWallBlueprint(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.Get(tile) == TileType.Wall) return false;
        if (_trees.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Blueprint { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.WallBuild, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    public void CompleteJob(JobId id)
    {
        var job = Jobs.Get(id);
        if (job is null) return;
        var tile = job.Tile;
        var entity = job.Entity;
        var kind = job.Kind;
        Jobs.Complete(id);

        if (kind == JobKind.WallBuild)
        {
            entity.DeleteEntity();
            lock (_mapLock)
            {
                Map.Set(tile, TileType.Wall);
                _playerWalls.Add(tile);
            }
            RebuildMapView();
        }
        else if (kind == JobKind.ChopTree)
        {
            // The job entity IS the tree. Delete it, drop a wood pile,
            // and rebuild the map view so the tile becomes walkable.
            _trees.Remove(tile);
            entity.DeleteEntity();

            var wood = Store.CreateEntity();
            wood.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
            wood.AddComponent(new Wood { Tile = tile });

            RebuildMapView();
        }
    }

    public void CancelJob(JobId id)
    {
        var job = Jobs.Get(id);
        if (job is null) return;
        var entity = job.Entity;
        var kind = job.Kind;
        Jobs.Cancel(id);

        if (kind == JobKind.WallBuild)
        {
            // Blueprint entity is single-purpose; throw it away with the job.
            entity.DeleteEntity();
        }
        else if (kind == JobKind.ChopTree)
        {
            // The tree survives a cancelled chop — reset its progress so
            // a future chop starts fresh.
            if (entity.HasComponent<Tree>())
            {
                ref var tree = ref entity.GetComponent<Tree>();
                tree.ChopProgressSec = 0f;
            }
        }
    }

    // Try to post a chop job on the tree at this tile. Returns false if
    // the tile has no tree or already has a job.
    public bool TryPostChopJob(TilePos tile)
    {
        if (!_trees.TryGetValue(tile, out var treeEnt)) return false;
        if (Jobs.HasTile(tile)) return false;
        var id = Jobs.Post(JobKind.ChopTree, tile, treeEnt);
        return !id.IsNone;
    }

    // Drop a tree at a random walkable, unoccupied, tree-free tile.
    public bool SpawnRandomTree()
    {
        for (int attempts = 0; attempts < 512; attempts++)
        {
            int x = _spawnRng.Next(Map.Width);
            int y = _spawnRng.Next(Map.Height);
            if (!Map.Walkable(x, y)) continue;
            var tile = new TilePos(x, y);
            if (_trees.ContainsKey(tile)) continue;
            if (IsOccupied(x, y)) continue;

            var e = Store.CreateEntity();
            e.AddComponent(new Tree { Tile = tile, ChopProgressSec = 0f });
            _trees[tile] = e;
            return true;
        }
        return false;
    }

    private void RebuildMapView()
    {
        MapView newView;
        lock (_mapLock)
        {
            MapVersion++;
            var treeTiles = new TilePos[_trees.Count];
            int idx = 0;
            foreach (var t in _trees.Keys) treeTiles[idx++] = t;
            newView = Map.Snapshot(MapVersion, _playerWalls.ToArray(), treeTiles);
        }
        Volatile.Write(ref _mapView, newView);
    }

    // Pick a random walkable, unoccupied tile and drop a fresh wanderer
    // there. Returns false if no usable tile was found (extremely small or
    // densely packed maps).
    public bool SpawnRandomDummy()
    {
        var view = MapView;
        for (int attempts = 0; attempts < 512; attempts++)
        {
            int x = _spawnRng.Next(Map.Width);
            int y = _spawnRng.Next(Map.Height);
            if (!view.Walkable(x, y)) continue;
            if (IsOccupied(x, y)) continue;
            var e = Store.CreateEntity();
            e.AddComponent(new WorldPos { X = x + 0.5f, Y = y + 0.5f });
            e.AddComponent(new PathFollower());
            e.AddComponent(new Wanderer());
            return true;
        }
        return false;
    }

    // Delete a colonist by entity id. Releases any claimed job and
    // discards any in-flight path request so we don't leak handles.
    public bool RemoveDummy(int entityId)
    {
        if (!Store.TryGetEntityById(entityId, out var ent)) return false;
        if (!ent.HasComponent<Wanderer>()) return false;
        if (ent.HasComponent<BuildTarget>())
        {
            var bt = ent.GetComponent<BuildTarget>();
            Jobs.Release(bt.JobId);
            ent.RemoveComponent<BuildTarget>();
        }
        if (ent.HasComponent<PathFollower>())
        {
            ref var pf = ref ent.GetComponent<PathFollower>();
            if (pf.PendingPathId != 0) PathService.Discard(pf.PendingPathId);
        }
        ent.DeleteEntity();
        return true;
    }

    private void SpawnDummy(int tileX, int tileY)
    {
        var view = MapView;
        for (int r = 0; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = tileX + dx;
                    int y = tileY + dy;
                    if (!view.Walkable(x, y)) continue;
                    if (IsOccupied(x, y)) continue;

                    var e = Store.CreateEntity();
                    e.AddComponent(new WorldPos { X = x + 0.5f, Y = y + 0.5f });
                    e.AddComponent(new PathFollower());
                    e.AddComponent(new Wanderer());
                    return;
                }
            }
        }
        throw new InvalidOperationException("No walkable tile found for dummy spawn.");
    }

    private bool IsOccupied(int tileX, int tileY)
    {
        bool occupied = false;
        Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            if ((int)p.X == tileX && (int)p.Y == tileY) occupied = true;
        });
        return occupied;
    }
}
