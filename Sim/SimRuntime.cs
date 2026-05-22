using System.Collections.Concurrent;
using Friflo.Engine.ECS;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Diagnostics;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.Stockpiles;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim;

public sealed class SimRuntime
{
    public EntityStore Store { get; } = new();
    public TileMap Map { get; }
    public JobBoard Jobs { get; } = new();
    public long Tick { get; private set; }
    public long MapVersion { get; private set; }
    public long RoomVersion { get; private set; }
    public int RoomCount { get; private set; }
    private int[] _roomTiles = Array.Empty<int>();

    private MapView _mapView = null!;
    public MapView MapView => Volatile.Read(ref _mapView);

    // Wall/door mutations within a single Step() coalesce into one
    // rebuild at end-of-tick instead of N immediate rebuilds. A 5x5 wall
    // ring all finishing in the same tick was previously cloning the
    // 256x256x4 map arrays and re-flood-filling rooms once per wall —
    // long enough to look like a hard freeze on the render thread.
    private bool _mapDirty;
    private bool _roomsDirty;

    public PathService PathService { get; }
    public SimWatcher Watcher { get; } = new();

    private readonly DummyController _dummies;
    private readonly BuildSystem _builds;
    private readonly ChopSystem _chops;
    private readonly DeconSystem _decons;
    private readonly FloorSystem _floors;
    private readonly DoorBuildSystem _doorBuilds;
    private readonly DoorSystem _doors;
    private readonly HaulSystem _hauls;
    private readonly SafetySystem _safety;
    // Stockpile tiles currently promised to an in-flight haul job. Posting
    // a new haul avoids these so two carriers can't target the same cell.
    private readonly HashSet<TilePos> _reservedHaulDests = new();
    private readonly ConcurrentQueue<ISimCommand> _commands = new();
    private readonly object _mapLock = new();
    private readonly List<TilePos> _playerWalls = new();
    private readonly Dictionary<TilePos, Entity> _trees = new();
    private readonly Dictionary<TilePos, Entity> _doorMap = new();
    // Subset of doors flagged Forbidden — passed to MapView so A* + the
    // mover treat them like walls. Kept in sync with the Door component.
    private readonly HashSet<TilePos> _forbiddenDoorTiles = new();
    // Door blueprints placed on top of a built wall: the wall must
    // deconstruct first. Map tile → the parked DoorBlueprint entity so
    // CompleteJob(Deconstruct) knows to chain a DoorBuild job.
    private readonly Dictionary<TilePos, Entity> _pendingDoorAfterDecon = new();

    // Stockpile zones. Ordered list (player-creation order = display
    // order). Tile-claim is exclusive: a tile may only belong to one
    // zone so haul routing has no ambiguity.
    private readonly List<Stockpile> _stockpiles = new();
    private readonly Dictionary<TilePos, Stockpile> _stockpileByTile = new();
    private int _nextStockpileId = 1;
    public IReadOnlyList<Stockpile> Stockpiles => _stockpiles;
    private readonly Random _spawnRng;
    private const int InitialTreeCount = 50;

    // Walls cost nothing yet, but deconstructing one still drops some
    // wood — half of a notional 2-wood cost — to give the player a
    // reason to reclaim their walls.
    public const int WallDeconWoodReturn = 1;

    // Per-tile stacking cap for wood piles. Carriers and the merge pass
    // never combine two stacks past this size; an in-progress stack with
    // remaining room is a valid haul destination.
    public const int WoodMaxStack = 75;

    public SimRuntime(int seed = 1337)
    {
        Map = TileMap.GenerateDefault(SimConstants.MapSize, SimConstants.MapSize, seed);
        _spawnRng = new Random(seed + 7);
        PathService = new PathService(Map.Width, Map.Height, () => MapView);
        _dummies = new DummyController(PathService, Jobs, () => MapView, CancelJob, seed + 1, TryGetDoor);
        _dummies.OnHaulPickup = (carriedEnt, cb) => OnHaulPickedUp(carriedEnt, cb);
        _dummies.OnHaulDeliver = (carrying, dropTile, cb) => DeliverCarrying(carrying, dropTile, cb);
        _builds = new BuildSystem(this, Jobs);
        _chops = new ChopSystem(this, Jobs);
        _decons = new DeconSystem(this, Jobs);
        _floors = new FloorSystem(this, Jobs);
        _doorBuilds = new DoorBuildSystem(this, Jobs);
        _doors = new DoorSystem();
        _hauls = new HaulSystem(this, Jobs);
        _safety = new SafetySystem(() => MapView, PathService, Watcher);

        // Trees go down before colonists so spawn can avoid landing on one.
        for (int i = 0; i < InitialTreeCount; i++) SpawnRandomTree();
        // Initial view + room layer must exist before pawns spawn; force
        // the rebuild now rather than waiting for the first Step().
        DoRebuildMapView();
        DoRecomputeRooms();

        int center = SimConstants.MapSize / 2;
        for (int i = 0; i < 3; i++) SpawnDummy(center, center);
    }

    public void Step(float dt)
    {
        while (_commands.TryDequeue(out var cmd)) cmd.Apply(this);
        _dummies.Step(Store, dt);
        _builds.Step(Store, dt);
        _chops.Step(Store, dt);
        _decons.Step(Store, dt);
        _floors.Step(Store, dt);
        _doorBuilds.Step(Store, dt);
        _doors.Step(Store, dt);
        _hauls.Step(Store, dt);
        MergeCoincidentWood();
        _safety.Step(Store, Tick);
        // Coalesced rebuild: one map clone + one room flood-fill per tick
        // even if N walls/doors mutated this tick.
        if (_mapDirty) { DoRebuildMapView(); _mapDirty = false; }
        if (_roomsDirty) { DoRecomputeRooms(); _roomsDirty = false; }
        Tick++;
        Watcher.Observe(Tick, Store, Jobs);
    }

    public IReadOnlyCollection<TilePos> TreeTiles => _trees.Keys;
    public bool TryGetTree(TilePos tile, out Entity entity) => _trees.TryGetValue(tile, out entity!);

    public IReadOnlyCollection<TilePos> DoorTiles => _doorMap.Keys;
    public bool TryGetDoor(TilePos tile, out Entity entity) => _doorMap.TryGetValue(tile, out entity!);

    public void QueueCommand(ISimCommand cmd) => _commands.Enqueue(cmd);

    // Flip a blueprint / job's Forbidden flag by tile. Workers stop
    // claiming it while forbidden; any active claim is released so the
    // current builder re-plans next tick.
    public bool SetJobForbidden(TilePos tile, bool forbidden)
        => Jobs.SetForbiddenByTile(tile, forbidden);

    // Cancel a blueprint / job by tile. Drops the backing entity for
    // build-style jobs so it doesn't leak. Mirrors the rect-cancel path
    // but single-tile — sourced from the blueprint info panel button.
    public bool CancelJobAtTile(TilePos tile)
    {
        var job = Jobs.GetByTile(tile);
        if (job is null) return false;
        CancelJob(job.Id);
        return true;
    }

    // Drain the pending command queue without running any systems.
    // Used by SimHost while paused so designations + assignments
    // (forbid, decon, build, draft, …) apply immediately instead of
    // sitting in the queue until the sim is resumed. Returns true if at
    // least one command was applied, so the host knows to republish the
    // render snapshot. Map / room rebuild flags coalesce the same way
    // they do in Step().
    public bool ApplyQueuedCommands()
    {
        bool any = false;
        while (_commands.TryDequeue(out var cmd)) { cmd.Apply(this); any = true; }
        if (!any) return false;
        if (_mapDirty) { DoRebuildMapView(); _mapDirty = false; }
        if (_roomsDirty) { DoRecomputeRooms(); _roomsDirty = false; }
        // Bump Tick so info panels (which dedupe re-renders on snap.Tick)
        // pick up the new decon marks / blueprints / forbid flags while
        // the sim is paused. No systems ran, so no gameplay timer advances.
        Tick++;
        return true;
    }

    public SimSnapshot BuildSnapshot(int? selectedDummyId = null, IReadOnlyCollection<int>? selectedTreeIds = null, IReadOnlyCollection<int>? selectedWoodIds = null)
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
            bool carrying = ent.HasComponent<Carrying>();
            if (carrying) label = "Haul";
            dummies[i++] = new DummyState(ent.Id, p.X, p.Y, label, drafted, carrying);

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
            bps[j++] = new BlueprintState(job.Tile, bp.ProgressSec / BuildSystem.BuildTimeSec, job.Forbidden);
        }
        if (j < bps.Length) Array.Resize(ref bps, j);

        var floorBps = new List<BlueprintState>();
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.FloorBuild) continue;
            var bp = job.Entity.GetComponent<FloorBlueprint>();
            floorBps.Add(new BlueprintState(job.Tile, bp.ProgressSec / FloorSystem.FloorTimeSec, job.Forbidden));
        }

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
        woodQuery.ForEachEntity((ref Wood w, Entity e) =>
        {
            woods[wi++] = new WoodState(e.Id, w.Tile, w.Count, ItemCatalog.Wood.FullPath, e.HasComponent<Forbidden>());
        });

        int[]? selTreeArr = null;
        if (selectedTreeIds is { Count: > 0 })
        {
            selTreeArr = new int[selectedTreeIds.Count];
            int si = 0;
            foreach (var id in selectedTreeIds) selTreeArr[si++] = id;
        }

        int[]? selWoodArr = null;
        if (selectedWoodIds is { Count: > 0 })
        {
            selWoodArr = new int[selectedWoodIds.Count];
            int si = 0;
            foreach (var id in selectedWoodIds) selWoodArr[si++] = id;
        }

        var decons = new List<DeconState>();
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.Deconstruct && job.Kind != JobKind.DoorDeconstruct) continue;
            var d = job.Entity.GetComponent<Decon>();
            decons.Add(new DeconState(job.Tile, d.ProgressSec / DeconSystem.DeconTimeSec, job.Forbidden));
        }

        // Include both active door-build blueprints and ones parked
        // waiting on a deconstruct (they have no DoorBuild job yet).
        // Iterating the component covers both — pending entries have
        // ProgressSec == 0.
        var doorBps = new List<BlueprintState>();
        Store.Query<DoorBlueprint>().ForEachEntity((ref DoorBlueprint bp, Entity _) =>
        {
            // Door blueprints can live without an active job (parked
            // waiting for a wall decon) — look up the job by tile to
            // surface the Forbidden flag when one exists.
            bool forbidden = Jobs.GetByTile(bp.Tile)?.Forbidden ?? false;
            doorBps.Add(new BlueprintState(bp.Tile, bp.ProgressSec / DoorBuildSystem.DoorTimeSec, forbidden));
        });

        var doorRender = new List<DoorRenderState>();
        var doorQuery = Store.Query<Door>();
        doorQuery.ForEachEntity((ref Door d, Entity _) =>
        {
            float open = Math.Clamp(d.ProgressSec / DoorSystem.OpenTimeSec, 0f, 1f);
            doorRender.Add(new DoorRenderState(d.Tile, d.Orientation, open, d.Forbidden, d.Locked, d.Priority));
        });

        var stockpiles = new StockpileState[_stockpiles.Count];
        for (int si = 0; si < _stockpiles.Count; si++)
        {
            var p = _stockpiles[si];
            var tiles = new TilePos[p.Tiles.Count];
            int ti = 0;
            foreach (var t in p.Tiles) tiles[ti++] = t;
            var allowed = new string[p.AllowedItemPaths.Count];
            int ai = 0;
            foreach (var path in p.AllowedItemPaths) allowed[ai++] = path;
            stockpiles[si] = new StockpileState(p.Id, p.Name, p.Priority, tiles, allowed);
        }

        return new SimSnapshot(
            Tick, MapVersion, RoomVersion, RoomCount,
            dummies, bps, floorBps.ToArray(), trees, woods, decons.ToArray(),
            doorBps.ToArray(), doorRender.ToArray(),
            stockpiles,
            selectedDummyId, selectedPath, selectedOrders, selTreeArr, selWoodArr);
    }

    // Render layer snapshot: assembled from the published MapView's
    // chunks (immutable, lock-free) rather than from TileMap directly,
    // so the renderer sees consistent data for the MapVersion it asks
    // about even if a sim tick is mid-mutation.
    public byte[] CopyLayerForRender(MapLayer layer) => MapView.AssembleFlat(layer);

    public bool TryPlaceWallBlueprint(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.GetWall(tile) != WallType.None) return false;
        if (_trees.ContainsKey(tile)) return false;
        if (_doorMap.ContainsKey(tile)) return false;
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
                Map.SetWall(tile, WallType.Stone);
                _playerWalls.Add(tile);
            }
            RefreshDoorOrientationsAround(tile);
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
            wood.AddComponent(new Wood { Tile = tile, Count = 1 });

            RebuildMapView();
        }
        else if (kind == JobKind.Deconstruct)
        {
            // Decon marker entity is single-purpose; throw it away.
            entity.DeleteEntity();
            lock (_mapLock)
            {
                // Wall layer only — terrain underneath stays put.
                Map.SetWall(tile, WallType.None);
                _playerWalls.Remove(tile);
            }
            RefreshDoorOrientationsAround(tile);
            if (WallDeconWoodReturn > 0)
            {
                var wood = Store.CreateEntity();
                wood.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
                wood.AddComponent(new Wood { Tile = tile, Count = WallDeconWoodReturn });
            }
            RebuildMapView();
            // Chain: a door blueprint was parked waiting on this decon.
            // Post its DoorBuild job now that the wall is gone.
            if (_pendingDoorAfterDecon.TryGetValue(tile, out var pendingBp))
            {
                _pendingDoorAfterDecon.Remove(tile);
                var doorId = Jobs.Post(JobKind.DoorBuild, tile, pendingBp);
                if (doorId.IsNone)
                {
                    pendingBp.DeleteEntity();
                }
            }
        }
        else if (kind == JobKind.FloorBuild)
        {
            entity.DeleteEntity();
            lock (_mapLock)
            {
                Map.SetFlooring(tile, FlooringType.Wood);
            }
            RebuildMapView();
        }
        else if (kind == JobKind.FloorDeconstruct)
        {
            entity.DeleteEntity();
            lock (_mapLock)
            {
                Map.SetFlooring(tile, FlooringType.None);
            }
            RebuildMapView();
        }
        else if (kind == JobKind.DoorBuild)
        {
            // Transmute the blueprint entity into the live door: drop
            // DoorBlueprint, add Door at Closed. Same tile, same id.
            // New doors default Locked=true (enemies stub); Forbidden=false.
            var bp = entity.GetComponent<DoorBlueprint>();
            entity.RemoveComponent<DoorBlueprint>();
            // Walls may have changed since the blueprint was placed —
            // recompute orientation from the live wall layer.
            var builtOrientation = ComputeDoorOrientation(tile, bp.Orientation);
            entity.AddComponent(new Door
            {
                Tile = tile,
                Orientation = builtOrientation,
                State = DoorState.Closed,
                ProgressSec = 0f,
                WantsOpen = false,
                IdleSec = 0f,
                Forbidden = false,
                Locked = true,
                Priority = DoorPriority.Medium,
            });
            _doorMap[tile] = entity;
            // Doors don't affect walkability, so no map rebuild — but
            // they DO bound rooms, so flag the room layer dirty.
            _roomsDirty = true;
        }
        else if (kind == JobKind.DoorDeconstruct)
        {
            // Delete the marker entity. The door entity itself lives in
            // _doorMap — drop it now that decon is complete.
            entity.DeleteEntity();
            if (_doorMap.TryGetValue(tile, out var doorEnt))
            {
                _doorMap.Remove(tile);
                _forbiddenDoorTiles.Remove(tile);
                doorEnt.DeleteEntity();
            }
            if (WallDeconWoodReturn > 0)
            {
                var wood = Store.CreateEntity();
                wood.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
                wood.AddComponent(new Wood { Tile = tile, Count = WallDeconWoodReturn });
            }
            RebuildMapView();
        }
    }

    public void CancelJob(JobId id)
    {
        var job = Jobs.Get(id);
        if (job is null) return;
        var tile = job.Tile;
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
        else if (kind == JobKind.Deconstruct)
        {
            // Wall stays; throw the marker away with the job. A future
            // re-designate spawns a fresh marker at 0 progress. If a
            // door blueprint was parked waiting on this decon, drop it
            // too — the player un-queued the whole "decon then door"
            // intent.
            entity.DeleteEntity();
            if (_pendingDoorAfterDecon.TryGetValue(tile, out var pendingBp))
            {
                _pendingDoorAfterDecon.Remove(tile);
                pendingBp.DeleteEntity();
            }
        }
        else if (kind == JobKind.FloorBuild)
        {
            entity.DeleteEntity();
        }
        else if (kind == JobKind.DoorBuild)
        {
            entity.DeleteEntity();
        }
        else if (kind == JobKind.FloorDeconstruct)
        {
            // Floor stays; throw the marker away. Re-designate spawns fresh.
            entity.DeleteEntity();
        }
        else if (kind == JobKind.DoorDeconstruct)
        {
            // Door stays; throw the marker away.
            entity.DeleteEntity();
        }
        else if (kind == JobKind.Haul)
        {
            // Wood entity survives the cancel — only the routing intent
            // is dropped. Release the dest cell so another haul can use it.
            if (entity.HasComponent<HaulPayload>())
            {
                var hp = entity.GetComponent<HaulPayload>();
                _reservedHaulDests.Remove(hp.DestTile);
                entity.RemoveComponent<HaulPayload>();
            }
            if (entity.HasComponent<HaulReserved>()) entity.RemoveComponent<HaulReserved>();
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

    // Player-built walls only. Procgen walls aren't in _playerWalls so
    // they can't be deconstructed — keeps the player from gnawing on
    // the map borders. Spawns a Decon marker entity that the system
    // ticks; the job owns that entity and deletes it on complete/cancel.
    public bool TryPostDeconstructJob(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.GetWall(tile) == WallType.None) return false;
        if (!_playerWalls.Contains(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.Deconstruct, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    public IReadOnlyList<TilePos> PlayerWalls => _playerWalls;

    // Post a door-deconstruct job. The door entity sticks around until
    // completion; the job carries a fresh Decon marker so DeconSystem
    // ticks progress without trampling Door state.
    public bool TryPostDoorDeconstructJob(TilePos tile)
    {
        if (!_doorMap.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.DoorDeconstruct, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    // Flip a built door's Forbidden flag. Forbidden = treated as a
    // wall by pathing; the map view rebuilds so A* picks up the new
    // walkability instantly.
    public void SetDoorForbidden(TilePos tile, bool forbidden)
    {
        if (!_doorMap.TryGetValue(tile, out var doorEnt)) return;
        ref var door = ref doorEnt.GetComponent<Door>();
        if (door.Forbidden == forbidden) return;
        door.Forbidden = forbidden;
        if (forbidden) _forbiddenDoorTiles.Add(tile);
        else _forbiddenDoorTiles.Remove(tile);
        RebuildMapView();
    }

    // Flip a built door's Locked flag. Stub — no enemy code consumes
    // it yet, so the bool just rides on the Door component.
    public void SetDoorLocked(TilePos tile, bool locked)
    {
        if (!_doorMap.TryGetValue(tile, out var doorEnt)) return;
        ref var door = ref doorEnt.GetComponent<Door>();
        door.Locked = locked;
    }

    // Set a built door's traversal priority. Rebuilds the map view so
    // A* picks up the new per-door cost on the next path request.
    public void SetDoorPriority(TilePos tile, DoorPriority priority)
    {
        if (!_doorMap.TryGetValue(tile, out var doorEnt)) return;
        ref var door = ref doorEnt.GetComponent<Door>();
        if (door.Priority == priority) return;
        door.Priority = priority;
        RebuildMapView();
    }

    // Post a floor-deconstruct job. Floors hidden under a wall are
    // skipped — the wall takes precedence; decon the wall first.
    public bool TryPostFloorDeconJob(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.GetFlooring(tile) != FlooringType.Wood) return false;
        if (Map.GetWall(tile) != WallType.None) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.FloorDeconstruct, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    // Post a wood-floor blueprint on this tile. Rejects if a wall
    // already exists (walls block floors), if the tile already has the
    // target flooring, or if any other job sits on the tile.
    public bool TryPlaceFloorBlueprint(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.GetWall(tile) != WallType.None) return false;
        if (Map.GetFlooring(tile) == FlooringType.Wood) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new FloorBlueprint { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.FloorBuild, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    // Post a door blueprint. Orientation derives from flanking walls
    // (east+west = horizontal; north+south = vertical; otherwise
    // Horizontal as a freestanding default). Placement rules:
    //   - Empty tile: post DoorBuild immediately.
    //   - Tile with an existing WallBuild job: cancel that job, then
    //     post DoorBuild fresh on top.
    //   - Tile with a built player wall: post a Deconstruct job and
    //     park the DoorBlueprint entity in _pendingDoorAfterDecon —
    //     CompleteJob(Deconstruct) chains the DoorBuild post.
    //   - Anything else (tree, existing door, procgen wall, conflicting
    //     non-WallBuild job, already-pending door): reject.
    public bool TryPlaceDoorBlueprint(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (_trees.ContainsKey(tile)) return false;
        if (_doorMap.ContainsKey(tile)) return false;
        if (_pendingDoorAfterDecon.ContainsKey(tile)) return false;

        var orientation = ComputeDoorOrientation(tile);

        // Replace a wall blueprint at the same tile (cancel it first).
        var existing = Jobs.GetByTile(tile);
        if (existing is not null)
        {
            if (existing.Kind != JobKind.WallBuild) return false;
            CancelJob(existing.Id);
        }

        if (Map.GetWall(tile) != WallType.None)
        {
            // Built wall under the door: must be a player wall (procgen
            // and border walls aren't deconstructable). Park the door
            // blueprint and post the decon job; chain on completion.
            if (!_playerWalls.Contains(tile)) return false;

            var bp = Store.CreateEntity();
            bp.AddComponent(new DoorBlueprint { Tile = tile, Orientation = orientation, ProgressSec = 0f });

            var decon = Store.CreateEntity();
            decon.AddComponent(new Decon { Tile = tile, ProgressSec = 0f });
            var deconId = Jobs.Post(JobKind.Deconstruct, tile, decon);
            if (deconId.IsNone)
            {
                decon.DeleteEntity();
                bp.DeleteEntity();
                return false;
            }
            _pendingDoorAfterDecon[tile] = bp;
            return true;
        }

        var e = Store.CreateEntity();
        e.AddComponent(new DoorBlueprint { Tile = tile, Orientation = orientation, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.DoorBuild, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    // Create a stockpile zone covering the inclusive rect [a..b].
    // Tiles already claimed by another stockpile are skipped — zones
    // don't overlap. Returns the new stockpile's id, or 0 if the rect
    // produced zero free tiles.
    public int CreateStockpileRect(TilePos a, TilePos b)
    {
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        var tiles = new List<TilePos>();
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                var t = new TilePos(x, y);
                if (!Map.InBounds(t)) continue;
                if (_stockpileByTile.ContainsKey(t)) continue;
                tiles.Add(t);
            }
        }
        if (tiles.Count == 0) return 0;

        int id = _nextStockpileId++;
        var name = $"Stockpile {id}";
        var pile = new Stockpile(id, name, StockpilePriority.Normal, tiles);
        _stockpiles.Add(pile);
        foreach (var t in tiles) _stockpileByTile[t] = pile;
        return id;
    }

    // Add the free tiles of [a..b] to an existing stockpile (compound
    // shapes — phase 5 UI calls this from the panel). Skips tiles
    // already claimed by *any* zone (including this one). Returns the
    // count of newly added tiles.
    public int ExpandStockpileRect(int stockpileId, TilePos a, TilePos b)
    {
        var pile = FindStockpile(stockpileId);
        if (pile is null) return 0;
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        int added = 0;
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                var t = new TilePos(x, y);
                if (!Map.InBounds(t)) continue;
                if (_stockpileByTile.ContainsKey(t)) continue;
                pile.Tiles.Add(t);
                _stockpileByTile[t] = pile;
                added++;
            }
        }
        return added;
    }

    // Remove the rect's tiles from a stockpile (subtract / shrink).
    // A zone that loses every tile remains — the panel still exists so
    // the player can re-expand. Returns the count of removed tiles.
    public int ShrinkStockpileRect(int stockpileId, TilePos a, TilePos b)
    {
        var pile = FindStockpile(stockpileId);
        if (pile is null) return 0;
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        int removed = 0;
        var removedTiles = new HashSet<TilePos>();
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                var t = new TilePos(x, y);
                if (!pile.Tiles.Remove(t)) continue;
                _stockpileByTile.Remove(t);
                removedTiles.Add(t);
                removed++;
            }
        }
        if (removedTiles.Count > 0) CancelHaulsTargetingTiles(removedTiles);
        return removed;
    }

    public bool DeleteStockpile(int stockpileId)
    {
        var pile = FindStockpile(stockpileId);
        if (pile is null) return false;
        foreach (var t in pile.Tiles) _stockpileByTile.Remove(t);
        _stockpiles.Remove(pile);
        CancelHaulsTargetingStockpile(stockpileId);
        return true;
    }

    // Cancel any in-flight haul whose dest belongs to a stockpile we
    // just deleted (or whose dest tile was shrunk out). Cancellation
    // releases the reservation and clears HaulReserved so the wood is
    // free to be re-posted next tick.
    private void CancelHaulsTargetingStockpile(int stockpileId)
    {
        var toCancel = new List<JobId>();
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.Haul) continue;
            if (!job.Entity.HasComponent<HaulPayload>()) continue;
            if (job.Entity.GetComponent<HaulPayload>().StockpileId != stockpileId) continue;
            toCancel.Add(job.Id);
        }
        foreach (var id in toCancel) CancelJob(id);
    }

    private void CancelHaulsTargetingTiles(HashSet<TilePos> removedTiles)
    {
        var toCancel = new List<JobId>();
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.Haul) continue;
            if (!job.Entity.HasComponent<HaulPayload>()) continue;
            var hp = job.Entity.GetComponent<HaulPayload>();
            if (!removedTiles.Contains(hp.DestTile)) continue;
            toCancel.Add(job.Id);
        }
        foreach (var id in toCancel) CancelJob(id);
    }

    public bool RenameStockpile(int stockpileId, string name)
    {
        var pile = FindStockpile(stockpileId);
        if (pile is null) return false;
        pile.Name = name;
        return true;
    }

    public bool SetStockpilePriority(int stockpileId, StockpilePriority priority)
    {
        var pile = FindStockpile(stockpileId);
        if (pile is null) return false;
        pile.Priority = priority;
        return true;
    }

    public bool SetStockpileItemAllowed(int stockpileId, string itemPath, bool allowed)
    {
        var pile = FindStockpile(stockpileId);
        if (pile is null) return false;
        if (allowed) pile.AllowedItemPaths.Add(itemPath);
        else pile.AllowedItemPaths.Remove(itemPath);
        return true;
    }

    public bool SetStockpileCategoryAllowed(int stockpileId, string categoryPath, bool allowed)
    {
        var pile = FindStockpile(stockpileId);
        if (pile is null) return false;
        if (!ItemCatalog.CategoriesByPath.TryGetValue(categoryPath, out var cat)) return false;
        pile.SetCategoryAllowed(cat, allowed);
        return true;
    }

    private Stockpile? FindStockpile(int id)
    {
        foreach (var p in _stockpiles) if (p.Id == id) return p;
        return null;
    }

    public bool TryGetStockpileAt(TilePos tile, out Stockpile pile)
    {
        if (_stockpileByTile.TryGetValue(tile, out var p)) { pile = p; return true; }
        pile = null!;
        return false;
    }

    public void ReserveHaulDest(TilePos tile) => _reservedHaulDests.Add(tile);
    public void ReleaseHaulDest(TilePos tile) => _reservedHaulDests.Remove(tile);
    public bool IsHaulDestReserved(TilePos tile) => _reservedHaulDests.Contains(tile);

    public bool TryFindBestHaulDest(TilePos source, ItemDef def, out TilePos dest, out int stockpileId)
        => TryFindBestHaulDest(source, def, 1, out dest, out stockpileId);

    // Walks the player's zones (highest priority first; create-order tiebreak)
    // and picks the best cell that accepts the item. A cell is valid if it's
    // empty OR holds the same item with room for countToMove. Merge bias:
    // within a zone, a partial-stack tile with the most existing items wins
    // over an empty tile; final tiebreak is distance.
    public bool TryFindBestHaulDest(TilePos source, ItemDef def, int countToMove, out TilePos dest, out int stockpileId)
    {
        dest = default;
        stockpileId = 0;

        // Map each tile to the wood entity sitting on it (if any) and its
        // count, so the picker can score partial-stack merges.
        var woodAt = new Dictionary<TilePos, int>();
        Store.Query<Wood>().ForEachEntity((ref Wood w, Entity ent) =>
        {
            if (w.Tile == source) return; // source tile doesn't block itself
            woodAt[w.Tile] = w.Count;
        });

        StockpilePriority bestPriority = StockpilePriority.Low - 1;
        int bestStack = -1;
        int bestDist = int.MaxValue;

        foreach (var pile in _stockpiles)
        {
            if (!pile.Allows(def)) continue;
            if (pile.Tiles.Count == 0) continue;
            if (pile.Priority < bestPriority) continue;

            TilePos pileBest = default;
            int pileBestStack = -1;
            int pileBestDist = int.MaxValue;
            foreach (var t in pile.Tiles)
            {
                if (_reservedHaulDests.Contains(t)) continue;
                int existing = woodAt.TryGetValue(t, out var c) ? c : 0;
                if (existing > 0 && existing + countToMove > WoodMaxStack) continue;
                int d = Math.Abs(t.X - source.X) + Math.Abs(t.Y - source.Y);
                // Bigger existing stack wins (merge bias); break ties by distance.
                if (existing > pileBestStack || (existing == pileBestStack && d < pileBestDist))
                {
                    pileBestStack = existing;
                    pileBestDist = d;
                    pileBest = t;
                }
            }
            if (pileBestStack < 0) continue;

            if (pile.Priority > bestPriority
                || (pile.Priority == bestPriority && pileBestStack > bestStack)
                || (pile.Priority == bestPriority && pileBestStack == bestStack && pileBestDist < bestDist))
            {
                bestPriority = pile.Priority;
                bestStack = pileBestStack;
                bestDist = pileBestDist;
                dest = pileBest;
                stockpileId = pile.Id;
            }
        }
        return stockpileId != 0;
    }

    // Pawn (re-)anchors every carried slot at dropTile, completes the
    // primary haul job, releases the dest reservation, and frees any
    // topoff reservations that the pawn never managed to physically pick
    // up so HaulSystem can re-post them. dropTile is usually the planned
    // DestTile, but the carrier passes its current tile instead when
    // delivery aborts (drafted mid-haul, dest blocked).
    public void DeliverCarrying(Carrying c, TilePos dropTile, CommandBuffer cb)
    {
        _reservedHaulDests.Remove(c.DestTile);
        if (!c.PrimaryJobId.IsNone)
        {
            var job = Jobs.Get(c.PrimaryJobId);
            if (job is not null) Jobs.Complete(c.PrimaryJobId);
        }
        if (c.Slots is not null)
        {
            foreach (var slot in c.Slots)
            {
                if (!Store.TryGetEntityById(slot.EntityId, out var e)) continue;
                if (e.HasComponent<HaulReserved>()) cb.RemoveComponent<HaulReserved>(e.Id);
                cb.AddComponent(e.Id, new Wood { Tile = dropTile, Count = slot.Count });
                cb.AddComponent(e.Id, new WorldPos { X = dropTile.X + 0.5f, Y = dropTile.Y + 0.5f });
            }
        }
        if (c.PendingPickupIds is not null)
        {
            foreach (var pid in c.PendingPickupIds)
            {
                if (!Store.TryGetEntityById(pid, out var pe)) continue;
                if (pe.HasComponent<HaulReserved>()) cb.RemoveComponent<HaulReserved>(pe.Id);
                if (pe.HasComponent<HaulPayload>()) cb.RemoveComponent<HaulPayload>(pe.Id);
            }
        }
    }

    // Called by DummyController when it actually picks up an item from
    // the world. Removes the world-side Wood component so the renderer
    // stops drawing the log; the entity itself stays alive on the
    // carrier until drop.
    public void OnHaulPickedUp(Entity carriedEntity, CommandBuffer cb)
    {
        if (carriedEntity.HasComponent<Wood>()) cb.RemoveComponent<Wood>(carriedEntity.Id);
        if (carriedEntity.HasComponent<HaulPayload>()) cb.RemoveComponent<HaulPayload>(carriedEntity.Id);
    }

    // Toggle the Forbidden marker on a world item entity. Cancels any
    // in-flight haul job that referenced it (the carrier's abort path
    // re-drops carried cargo on the spot via DeliverCarrying) and clears
    // any topoff reservation so the carrier drops it from its pending
    // pickup list on arrival.
    public void SetItemForbidden(int entityId, bool forbidden)
    {
        if (!Store.TryGetEntityById(entityId, out var ent)) return;
        if (forbidden)
        {
            if (ent.HasComponent<HaulReserved>())
            {
                var hr = ent.GetComponent<HaulReserved>();
                if (!hr.JobId.IsNone)
                {
                    CancelJob(hr.JobId);
                }
                else
                {
                    if (ent.HasComponent<HaulPayload>())
                    {
                        var hp = ent.GetComponent<HaulPayload>();
                        _reservedHaulDests.Remove(hp.DestTile);
                        ent.RemoveComponent<HaulPayload>();
                    }
                    ent.RemoveComponent<HaulReserved>();
                }
            }
            if (!ent.HasComponent<Forbidden>()) ent.AddComponent(new Forbidden());
        }
        else
        {
            if (ent.HasComponent<Forbidden>()) ent.RemoveComponent<Forbidden>();
        }
    }

    // Sum of all wood stacks at the given tile. Used by HaulSystem to bias
    // merge-haul destinations and by gameplay queries (e.g. a future "X
    // logs ready" tooltip).
    public int WoodCountAtTile(TilePos tile)
    {
        int total = 0;
        Store.Query<Wood>().ForEachEntity((ref Wood w, Entity ent) =>
        {
            if (w.Tile == tile) total += w.Count;
        });
        return total;
    }

    // End-of-tick consolidator: any two unreserved wood entities on the
    // same tile whose combined count fits in one stack collapse into one
    // entity. The result is at-most-one wood entity per tile, which is
    // what TryFindBestHaulDest assumes.
    private void MergeCoincidentWood()
    {
        var byTile = new Dictionary<TilePos, Entity>();
        var mergeOps = new List<(int destId, int amt)>();
        var deletes = new List<Entity>();
        Store.Query<Wood>().ForEachEntity((ref Wood w, Entity e) =>
        {
            if (e.HasComponent<HaulReserved>()) return;
            if (byTile.TryGetValue(w.Tile, out var existing))
            {
                int existingCount = existing.GetComponent<Wood>().Count;
                if (existingCount + w.Count <= WoodMaxStack)
                {
                    mergeOps.Add((existing.Id, w.Count));
                    deletes.Add(e);
                }
                return;
            }
            byTile[w.Tile] = e;
        });
        foreach (var (id, amt) in mergeOps)
        {
            if (Store.TryGetEntityById(id, out var dest))
            {
                ref var dw = ref dest.GetComponent<Wood>();
                dw.Count += amt;
            }
        }
        foreach (var e in deletes) e.DeleteEntity();
    }

    private bool HasWallAt(int x, int y)
    {
        if (!Map.InBounds(new TilePos(x, y))) return false;
        return Map.GetWall(new TilePos(x, y)) != WallType.None;
    }

    // Pick door orientation from the four cardinal neighbors of the tile.
    // E+W walls → horizontal (door swings into the N/S corridor); N+S
    // walls → vertical. Fallback when neither pair is present: keep the
    // current orientation if known, else Horizontal. Used at blueprint
    // time, at build completion (walls may have shifted while the bp
    // sat), and whenever an adjacent wall is added or removed.
    private DoorOrientation ComputeDoorOrientation(TilePos tile, DoorOrientation? current = null)
    {
        bool wallW = HasWallAt(tile.X - 1, tile.Y);
        bool wallE = HasWallAt(tile.X + 1, tile.Y);
        bool wallN = HasWallAt(tile.X, tile.Y - 1);
        bool wallS = HasWallAt(tile.X, tile.Y + 1);
        if (wallE && wallW) return DoorOrientation.Horizontal;
        if (wallN && wallS) return DoorOrientation.Vertical;
        return current ?? DoorOrientation.Horizontal;
    }

    // After a wall toggles at `changed`, any door sitting in one of the
    // four cardinal neighbors may now have a different best orientation.
    // Update the Door component in place; renderer + DoorRenderState will
    // pick the new value on the next snapshot.
    private void RefreshDoorOrientationsAround(TilePos changed)
    {
        Span<TilePos> neighbors = stackalloc TilePos[4]
        {
            new TilePos(changed.X - 1, changed.Y),
            new TilePos(changed.X + 1, changed.Y),
            new TilePos(changed.X, changed.Y - 1),
            new TilePos(changed.X, changed.Y + 1),
        };
        foreach (var n in neighbors)
        {
            if (!_doorMap.TryGetValue(n, out var ent)) continue;
            ref var door = ref ent.GetComponent<Door>();
            var want = ComputeDoorOrientation(n, door.Orientation);
            if (want != door.Orientation) door.Orientation = want;
        }
    }

    // Spawn a free wood pile at the given tile. Used by haul tests and
    // future debug tooling — gameplay drops wood via chop/decon.
    public Entity SpawnWoodPile(TilePos tile, int count = 1)
    {
        var w = Store.CreateEntity();
        w.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
        w.AddComponent(new Wood { Tile = tile, Count = count });
        return w;
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

    // Mark the map view as needing a rebuild this tick. Cheap; the actual
    // clone-and-publish runs once at end of Step().
    private void RebuildMapView()
    {
        _mapDirty = true;
        // Rooms also need to refresh whenever walls change.
        _roomsDirty = true;
    }

    private void DoRebuildMapView()
    {
        MapView newView;
        lock (_mapLock)
        {
            MapVersion++;
            var treeTiles = new TilePos[_trees.Count];
            int idx = 0;
            foreach (var t in _trees.Keys) treeTiles[idx++] = t;
            // Pass the previously published view so the new snapshot
            // can reuse chunk refs that weren't touched this tick.
            TilePos[]? forbidden = null;
            if (_forbiddenDoorTiles.Count > 0)
            {
                forbidden = new TilePos[_forbiddenDoorTiles.Count];
                int fi = 0;
                foreach (var t in _forbiddenDoorTiles) forbidden[fi++] = t;
            }
            TilePos[]? doorTiles = null;
            float[]? doorCosts = null;
            if (_doorMap.Count > 0)
            {
                doorTiles = new TilePos[_doorMap.Count];
                doorCosts = new float[_doorMap.Count];
                int di = 0;
                foreach (var (tile, ent) in _doorMap)
                {
                    doorTiles[di] = tile;
                    doorCosts[di] = DoorPathing.CostFor(ent.GetComponent<Door>().Priority);
                    di++;
                }
            }
            newView = Map.Snapshot(MapVersion, _mapView, _playerWalls.ToArray(), treeTiles, forbidden, doorTiles, doorCosts);
        }
        Volatile.Write(ref _mapView, newView);
    }

    // Rebuild the room layer from the current wall + door state.
    // 4-connected BFS over the whole map; runs at most once per tick via
    // the _roomsDirty coalesce flag.
    private void DoRecomputeRooms()
    {
        int w = Map.Width, h = Map.Height;
        int n = w * h;
        if (_roomTiles.Length != n) _roomTiles = new int[n];
        // Walls come from the published MapView so the room flood-fill
        // is consistent with whatever Walkable() returns this tick.
        var walls = _mapView.AssembleFlat(MapLayer.Wall);
        int count = RoomMap.Compute(w, h, walls, _doorMap.Keys, _roomTiles);
        RoomCount = count;
        RoomVersion++;
    }

    public int[] CopyRoomTilesForRender()
    {
        lock (_mapLock)
        {
            return (int[])_roomTiles.Clone();
        }
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
