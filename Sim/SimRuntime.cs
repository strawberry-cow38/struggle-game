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
using StruggleGame.Sim.GrowZones;
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
    // Roof layer. Both arrays are y*Width + x indexed.
    //   _roofTiles    : 1 = roofed (auto from room recompute OR painted by
    //                   the player's roof designator). Persists across
    //                   recomputes — auto-roof never removes a tile, the
    //                   player does that explicitly via the remove-roof
    //                   designator.
    //   _noRoofTiles  : 1 = player marked "do not auto-roof". Auto-roof
    //                   skips these; setting the flag also clears any
    //                   existing roof on the tile.
    private byte[] _roofTiles = Array.Empty<byte>();
    private byte[] _noRoofTiles = Array.Empty<byte>();
    public long RoofVersion { get; private set; }
    // Per-tile LAMP contribution only (max-blend of every powered lamp,
    // walls block via Bresenham LOS). Sun is NOT in here — it's a global
    // triple composed against this buffer at read time (LightAt /
    // CopyLightRgbForRender). Sun changing every tick during sunrise /
    // sunset would otherwise force a full-map rewrite + lamp re-pass; the
    // split makes a sun tick free on the sim side.
    private byte[] _lampR = Array.Empty<byte>();
    private byte[] _lampG = Array.Empty<byte>();
    private byte[] _lampB = Array.Empty<byte>();
    public long LightVersion { get; private set; }

    // World time, in in-sim seconds since the Jan 1 2000 00:00:00 epoch.
    // Advances every tick by SimSecondsPerRealSecond * dt — at default tps
    // this is 1 in-sim sec per tick, 60 in-sim sec per real sec, so a
    // full 24h day = 24 real minutes at 1x speed. Speed multipliers (2x,
    // 3x, 6x) scale ticks-per-real-second up, which advances time the
    // same amount per tick but at a higher per-real-second rate.
    public const double SimSecondsPerRealSecond = 60.0;
    public static readonly DateTime WorldEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private double _worldTimeSec;
    public double WorldTimeSec => _worldTimeSec;
    public DateTime WorldDateTime => WorldEpoch.AddSeconds(_worldTimeSec);
    // Current sun RGB (global, applies to every non-roofed tile in the
    // composition pass). Updated when Step detects the sun bytes have
    // moved (sunrise/sunset hour ramps). Sun changes do NOT touch the
    // lamp buffer — they just bump LightVersion so the renderer
    // recomposites.
    private byte _lastSunR, _lastSunG, _lastSunB;
    // Set when sun bytes change since last publish. Coalesces N sub-tick
    // sun changes into one LightVersion bump per Step.
    private bool _sunDirty;
    // Index = room id (0 = outdoor faux room, 1..RoomCount = enclosed
    // interiors). Resized + repopulated by DoRecomputeRooms whenever
    // walls/doors change. Fixed-figure for now: outdoor = OutdoorTempC,
    // interior = IndoorTempC. Per-room insulation / heat loss ships later.
    private float[] _roomTemps = Array.Empty<float>();

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
    private readonly GrowthSystem _growth;
    private readonly TreeRegrowSystem _regrow;
    private readonly CutPlantSystem _cuts;
    private readonly HarvestSystem _harvests;
    private readonly SowSystem _sows;
    private readonly GrowZoneManager _zoneManager;
    private readonly DeconSystem _decons;
    private readonly FloorSystem _floors;
    private readonly RoofSystem _roofs;
    private readonly LampSystem _lamps;
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
    private readonly Dictionary<TilePos, Entity> _crops = new();
    private readonly Dictionary<TilePos, Entity> _doorMap = new();
    // Built lamps. Tile → Lamp entity. Lamps don't block walkability;
    // the map keeps them addressable for selection / power toggle /
    // decon and lets the light recompute iterate every powered lamp.
    private readonly Dictionary<TilePos, Entity> _lampMap = new();
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

    // Player-painted grow zones (mirror of stockpiles for farming).
    private readonly List<GrowZone> _growZones = new();
    private readonly Dictionary<TilePos, GrowZone> _growZoneByTile = new();
    private int _nextGrowZoneId = 1;
    public IReadOnlyList<GrowZone> GrowZones => _growZones;
    private readonly Random _spawnRng;
    private const int InitialTreeCount = 50;
    // World engine restocks toward this count over time, biased away from
    // player structures. Same as InitialTreeCount for now — could diverge
    // later if biomes or weather thin trees out.
    public const int TargetTreeCount = 50;
    // Scattered demo crops at world gen so the cut/harvest designators
    // have something to chew on before a grow-zone UI exists.
    private const int InitialCarrotCount = 25;

    // Walls cost nothing yet, but deconstructing one still drops some
    // wood — half of a notional 2-wood cost — to give the player a
    // reason to reclaim their walls.
    public const int WallDeconWoodReturn = 1;

    // Per-tile stacking cap for wood piles. Carriers and the merge pass
    // never combine two stacks past this size; an in-progress stack with
    // remaining room is a valid haul destination.
    public const int WoodMaxStack = 75;

    // Yield from a fully-grown tree. Trees below 50% growth ramp linearly
    // from 0 → WoodPerTreeFull; trees at 50% or above yield the full amount.
    public const int WoodPerTreeFull = 25;

    // Growth threshold below which the chop designator refuses to post a
    // ChopTree job — the "cut plants" designator handles immature trees.
    public const float ChopMinGrowthStage = 0.5f;

    // Crop harvest band. Below HarvestMinGrowthStage = no yield (cut
    // only). Linear ramp from CarrotMinYield → CarrotMaxYield between
    // HarvestMinGrowthStage and 1.0.
    public const float HarvestMinGrowthStage = 0.75f;
    public const int CarrotMinYield = 1;
    public const int CarrotMaxYield = 4;

    public SimRuntime(int seed = 1337)
    {
        // Start at 08:00 on Jan 1 2000 — first daylight tick of the
        // epoch day so the world spawns under full sun, not midnight.
        _worldTimeSec = 8 * 3600;
        Map = TileMap.GenerateDefault(SimConstants.MapSize, SimConstants.MapSize, seed);
        _spawnRng = new Random(seed + 7);
        PathService = new PathService(Map.Width, Map.Height, () => MapView);
        _dummies = new DummyController(PathService, Jobs, () => MapView, CancelJob, seed + 1, TryGetDoor);
        _dummies.OnHaulPickup = (carriedEnt, cb) => OnHaulPickedUp(carriedEnt, cb);
        _dummies.OnHaulDeliver = (carrierEntity, dropTile, cb) => DeliverCarrying(carrierEntity, dropTile, cb);
        _builds = new BuildSystem(this, Jobs);
        _chops = new ChopSystem(this, Jobs);
        _growth = new GrowthSystem(this);
        _regrow = new TreeRegrowSystem(this);
        _cuts = new CutPlantSystem(this, Jobs);
        _harvests = new HarvestSystem(this, Jobs);
        _sows = new SowSystem(this, Jobs);
        _zoneManager = new GrowZoneManager(this);
        _decons = new DeconSystem(this, Jobs);
        _floors = new FloorSystem(this, Jobs);
        _roofs = new RoofSystem(this, Jobs);
        _lamps = new LampSystem(this, Jobs);
        _doorBuilds = new DoorBuildSystem(this, Jobs);
        _doors = new DoorSystem();
        _hauls = new HaulSystem(this, Jobs);
        _safety = new SafetySystem(() => MapView, PathService, Watcher);

        // Trees go down before colonists so spawn can avoid landing on one.
        for (int i = 0; i < InitialTreeCount; i++) SpawnRandomTree();
        for (int i = 0; i < InitialCarrotCount; i++) SpawnRandomCarrot();
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
        // Advance world time. Sun bytes derived once per tick; any change
        // marks the light grid dirty so the end-of-tick coalesce picks it
        // up. ComputeSun is cheap (a few mults + a smoothstep).
        _worldTimeSec += SimSecondsPerRealSecond * dt;
        ComputeSun(_worldTimeSec, out var sR, out var sG, out var sB);
        if (sR != _lastSunR || sG != _lastSunG || sB != _lastSunB) _sunDirty = true;
        _dummies.Step(Store, dt);
        _builds.Step(Store, dt);
        _chops.Step(Store, dt);
        _growth.Step(Store, dt);
        _regrow.Step(dt);
        _cuts.Step(Store, dt);
        _harvests.Step(Store, dt);
        _sows.Step(Store, dt);
        _zoneManager.Step(dt);
        _decons.Step(Store, dt);
        _floors.Step(Store, dt);
        _roofs.Step(Store, dt);
        _lamps.Step(Store, dt);
        _doorBuilds.Step(Store, dt);
        _doors.Step(Store, dt);
        _hauls.Step(Store, dt);
        MergeCoincidentWood();
        _safety.Step(Store, Tick);
        // Coalesced rebuild: one map clone + one room flood-fill per tick
        // even if N walls/doors mutated this tick.
        if (_mapDirty) { DoRebuildMapView(); _mapDirty = false; }
        if (_roomsDirty) { DoRecomputeRooms(); _roomsDirty = false; }
        if (_sunDirty) { _lastSunR = sR; _lastSunG = sG; _lastSunB = sB; LightVersion++; _sunDirty = false; }
        Tick++;
        Watcher.Observe(Tick, Store, Jobs);
    }

    public IReadOnlyCollection<TilePos> TreeTiles => _trees.Keys;
    public bool TryGetTree(TilePos tile, out Entity entity) => _trees.TryGetValue(tile, out entity!);

    public IReadOnlyCollection<TilePos> DoorTiles => _doorMap.Keys;
    public bool TryGetDoor(TilePos tile, out Entity entity) => _doorMap.TryGetValue(tile, out entity!);

    public IReadOnlyCollection<TilePos> LampTiles => _lampMap.Keys;
    public bool TryGetLamp(TilePos tile, out Entity entity) => _lampMap.TryGetValue(tile, out entity!);

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

            CarriedItemState[] inventory = Array.Empty<CarriedItemState>();
            float carryW = 0f, carryB = 0f;
            if (carrying)
            {
                var c = ent.GetComponent<Carrying>();
                if (c.Slots is { Count: > 0 })
                {
                    inventory = new CarriedItemState[c.Slots.Count];
                    for (int si = 0; si < c.Slots.Count; si++)
                    {
                        var s = c.Slots[si];
                        inventory[si] = new CarriedItemState(s.EntityId, s.ItemPath, s.Count, s.Forbidden);
                        if (ItemCatalog.ItemsByPath.TryGetValue(s.ItemPath, out var def))
                        {
                            carryW += def.Weight * s.Count;
                            carryB += def.Bulk * s.Count;
                        }
                    }
                }
            }
            dummies[i++] = new DummyState(
                ent.Id, p.X, p.Y, label, drafted, carrying,
                inventory, carryW, carryB,
                SimConstants.MaxCarryWeight, SimConstants.MaxCarryBulk);

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

        // Roof jobs are invisible: pawns still walk + claim them, but
        // the player never sees a blueprint outline or progress bar.
        // Completion happens on the first tick the pawn is in range
        // (RoofBuild/RemoveTimeSec are 0), so any blueprint state would
        // flicker for a single tick anyway.
        var roofBps = new List<RoofBlueprintState>();

        var trees = new TreeState[_trees.Count];
        int k = 0;
        foreach (var (tile, ent) in _trees)
        {
            var tc = ent.GetComponent<Tree>();
            bool hasJob = Jobs.GetByTile(tile)?.Kind == JobKind.ChopTree;
            float stage = ent.HasComponent<Growth>() ? ent.GetComponent<Growth>().Stage : 1f;
            trees[k++] = new TreeState(ent.Id, tile, tc.ChopProgressSec / ChopSystem.ChopTimeSec, hasJob, stage);
        }

        var woodQuery = Store.Query<Wood>();
        var woods = new WoodState[woodQuery.Count];
        int wi = 0;
        woodQuery.ForEachEntity((ref Wood w, Entity e) =>
        {
            woods[wi++] = new WoodState(e.Id, w.Tile, w.Count, ItemCatalog.Wood.FullPath, e.HasComponent<Forbidden>());
        });

        var crops = new CropState[_crops.Count];
        int ci = 0;
        foreach (var (cTile, cEnt) in _crops)
        {
            var cc = cEnt.GetComponent<Crop>();
            float cStage = cEnt.HasComponent<Growth>() ? cEnt.GetComponent<Growth>().Stage : 0f;
            var job = Jobs.GetByTile(cTile);
            JobKind? activeKind = null;
            float work = 0f;
            if (job is not null && (job.Kind == JobKind.CutPlants || job.Kind == JobKind.Harvest))
            {
                activeKind = job.Kind;
                float denom = job.Kind == JobKind.Harvest
                    ? HarvestSystem.HarvestTimeSec
                    : CutPlantSystem.CutTimeSec;
                work = cc.WorkProgressSec / denom;
            }
            crops[ci++] = new CropState(cEnt.Id, cTile, cc.Kind, cStage, work, activeKind);
        }

        var pileQuery = Store.Query<ItemPile>();
        var piles = new ItemPileState[pileQuery.Count];
        int pi = 0;
        pileQuery.ForEachEntity((ref ItemPile p, Entity e) =>
        {
            piles[pi++] = new ItemPileState(e.Id, p.Tile, p.Count, p.ItemPath);
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
            if (job.Kind != JobKind.Deconstruct
                && job.Kind != JobKind.DoorDeconstruct
                && job.Kind != JobKind.LampDeconstruct) continue;
            var d = job.Entity.GetComponent<Decon>();
            float denom = job.Kind == JobKind.LampDeconstruct
                ? LampSystem.LampDeconTimeSec
                : DeconSystem.DeconTimeSec;
            decons.Add(new DeconState(job.Tile, d.ProgressSec / denom, job.Forbidden));
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

        var growZones = new GrowZoneState[_growZones.Count];
        for (int zi = 0; zi < _growZones.Count; zi++)
        {
            var z = _growZones[zi];
            var tiles = new TilePos[z.Tiles.Count];
            int ti = 0;
            foreach (var t in z.Tiles) tiles[ti++] = t;
            growZones[zi] = new GrowZoneState(
                z.Id, z.Name, z.CropKind, z.AllowCutting, z.AllowSowing, tiles);
        }

        var lamps = new LampState[_lampMap.Count];
        int li = 0;
        foreach (var (lTile, lEnt) in _lampMap)
        {
            var lc = lEnt.GetComponent<Lamp>();
            lamps[li++] = new LampState(lTile, lc.PoweredOn, lc.Color);
        }

        var lampBps = new List<BlueprintState>();
        Store.Query<LampBlueprint>().ForEachEntity((ref LampBlueprint bp, Entity _) =>
        {
            bool forbidden = Jobs.GetByTile(bp.Tile)?.Forbidden ?? false;
            lampBps.Add(new BlueprintState(bp.Tile, bp.ProgressSec / LampSystem.LampBuildTimeSec, forbidden));
        });

        return new SimSnapshot(
            Tick, MapVersion, RoomVersion, RoomCount, RoofVersion, LightVersion,
            _worldTimeSec,
            dummies, bps, floorBps.ToArray(), trees, crops, woods, piles, decons.ToArray(),
            doorBps.ToArray(), doorRender.ToArray(),
            stockpiles, growZones, roofBps.ToArray(),
            lamps, lampBps.ToArray(),
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
            // The job entity IS the tree. Yield ramps with growth — full
            // wood at ≥50% growth (chop never lands on younger trees, but
            // guard anyway), linear ramp below.
            float stage = entity.HasComponent<Growth>() ? entity.GetComponent<Growth>().Stage : 1f;
            int yield = stage >= ChopMinGrowthStage
                ? WoodPerTreeFull
                : Math.Max(1, (int)Math.Round(WoodPerTreeFull * stage / ChopMinGrowthStage));
            _trees.Remove(tile);
            entity.DeleteEntity();

            var wood = Store.CreateEntity();
            wood.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
            wood.AddComponent(new Wood { Tile = tile, Count = yield });

            RebuildMapView();
        }
        else if (kind == JobKind.CutPlants)
        {
            if (entity.HasComponent<Tree>())
            {
                // Immature tree: linear-ramp wood from 0 → WoodPerTreeFull
                // between stage 0 and ChopMinGrowthStage. Always at least 1
                // so a player-issued cut still returns a token amount.
                float stage = entity.HasComponent<Growth>() ? entity.GetComponent<Growth>().Stage : 0f;
                int yield = Math.Max(1, (int)Math.Round(WoodPerTreeFull * stage / ChopMinGrowthStage));
                _trees.Remove(tile);
                entity.DeleteEntity();
                var wood = Store.CreateEntity();
                wood.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
                wood.AddComponent(new Wood { Tile = tile, Count = yield });
                RebuildMapView();
            }
            else if (entity.HasComponent<Crop>())
            {
                // Crops cut at any stage yield nothing — the verb exists
                // to clear ground (e.g. reclaim a grow zone) without
                // waiting for harvestability.
                _crops.Remove(tile);
                entity.DeleteEntity();
            }
        }
        else if (kind == JobKind.Harvest)
        {
            if (!entity.HasComponent<Crop>()) return;
            var crop = entity.GetComponent<Crop>();
            float stage = entity.HasComponent<Growth>() ? entity.GetComponent<Growth>().Stage : 0f;
            int yield = CarrotMinYield;
            if (stage >= 1f) yield = CarrotMaxYield;
            else if (stage >= HarvestMinGrowthStage)
            {
                float t = (stage - HarvestMinGrowthStage) / (1f - HarvestMinGrowthStage);
                yield = (int)Math.Round(CarrotMinYield + (CarrotMaxYield - CarrotMinYield) * t);
            }
            _crops.Remove(tile);
            entity.DeleteEntity();
            // Generic ItemPile drop. Carrots aren't haulable yet (haul +
            // stockpile machinery still hardcodes the Wood component);
            // the pile just sits on the ground for now and reads via the
            // ItemPile snapshot.
            string itemPath = crop.Kind switch
            {
                CropKind.Carrot => Items.ItemCatalog.Carrot.FullPath,
                _ => Items.ItemCatalog.Carrot.FullPath,
            };
            var drop = Store.CreateEntity();
            drop.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
            drop.AddComponent(new ItemPile { Tile = tile, Count = yield, ItemPath = itemPath });
        }
        else if (kind == JobKind.Sow)
        {
            // The job entity is just the SowSite marker; throw it away
            // and plant a fresh stage-0 crop of the configured kind on
            // the tile. If something else already grew there (race with
            // another command), bail without spawning.
            CropKind sowKind = CropKind.Carrot;
            if (entity.HasComponent<SowSite>()) sowKind = entity.GetComponent<SowSite>().Kind;
            entity.DeleteEntity();
            if (_trees.ContainsKey(tile) || _crops.ContainsKey(tile)) return;
            if (!Map.Walkable(tile)) return;
            SpawnCropAt(tile, sowKind, 0f);
        }
        else if (kind == JobKind.RoofBuild)
        {
            var bp = entity.GetComponent<RoofBlueprint>();
            var tiles = bp.Tiles;
            entity.DeleteEntity();
            EnsureRoofArrays(Map.Width, Map.Height);
            bool any = false;
            if (tiles is null || tiles.Length == 0)
            {
                int idx = tile.Y * Map.Width + tile.X;
                if (_roofTiles[idx] == 0) { _roofTiles[idx] = 1; any = true; }
            }
            else
            {
                foreach (var t in tiles)
                {
                    if (!Map.InBounds(t)) continue;
                    int idx = t.Y * Map.Width + t.X;
                    if (_roofTiles[idx] == 0) { _roofTiles[idx] = 1; any = true; }
                }
            }
            // Roof toggle is composition-only — sun gating happens at
            // read time, lamp buffer is independent. Just bump versions.
            if (any) { RoofVersion++; LightVersion++; }
        }
        else if (kind == JobKind.RoofRemove)
        {
            var bp = entity.GetComponent<RoofBlueprint>();
            var tiles = bp.Tiles;
            entity.DeleteEntity();
            EnsureRoofArrays(Map.Width, Map.Height);
            bool any = false;
            if (tiles is null || tiles.Length == 0)
            {
                int idx = tile.Y * Map.Width + tile.X;
                if (_roofTiles[idx] != 0) { _roofTiles[idx] = 0; any = true; }
            }
            else
            {
                foreach (var t in tiles)
                {
                    if (!Map.InBounds(t)) continue;
                    int idx = t.Y * Map.Width + t.X;
                    if (_roofTiles[idx] != 0) { _roofTiles[idx] = 0; any = true; }
                }
            }
            if (any) { RoofVersion++; LightVersion++; }
        }
        else if (kind == JobKind.LampBuild)
        {
            // Transmute the blueprint entity into the live lamp: drop
            // LampBlueprint, add Lamp at PoweredOn=true (stub until a
            // power network ships). Color carries over from blueprint so
            // pre-tinted designations build into pre-tinted lamps.
            var bpColor = entity.GetComponent<LampBlueprint>().Color;
            entity.RemoveComponent<LampBlueprint>();
            entity.AddComponent(new Lamp { Tile = tile, PoweredOn = true, Color = bpColor });
            _lampMap[tile] = entity;
            RecomputeLampLight();
            // Auto-roof pass on the lamp's own tile: while the lamp
            // build job was active Jobs.HasTile blocked the original
            // auto-roof from posting here, leaving an uncovered hole.
            // Now that the job is gone we can post the roof if the
            // tile is interior + not no-roof + still unroofed.
            int lidx = tile.Y * Map.Width + tile.X;
            if (_roomTiles[lidx] != 0 && _noRoofTiles[lidx] == 0 && _roofTiles[lidx] == 0)
            {
                TryPostRoofBuildJob(tile);
            }
        }
        else if (kind == JobKind.LampDeconstruct)
        {
            entity.DeleteEntity();
            if (_lampMap.TryGetValue(tile, out var lampEnt))
            {
                _lampMap.Remove(tile);
                lampEnt.DeleteEntity();
                RecomputeLampLight();
            }
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
            // Doors are LOS-opaque for lamp light; relight so neighbors
            // stop bleeding through the new door tile immediately.
            RecomputeLampLight();
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
            // Door gone = light can flow through that tile again. Relight.
            RecomputeLampLight();
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
        else if (kind == JobKind.RoofBuild || kind == JobKind.RoofRemove)
        {
            // Roof state unchanged; throw the marker away.
            entity.DeleteEntity();
        }
        else if (kind == JobKind.LampBuild || kind == JobKind.LampDeconstruct)
        {
            // Lamp state unchanged; throw the marker away. For decon the
            // Lamp entity stayed in _lampMap so it's still functional.
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
    // the tile has no tree, already has a job, or holds an immature tree
    // (the "cut plants" designator targets those instead).
    public bool TryPostChopJob(TilePos tile)
    {
        if (!_trees.TryGetValue(tile, out var treeEnt)) return false;
        if (Jobs.HasTile(tile)) return false;
        float stage = treeEnt.HasComponent<Growth>() ? treeEnt.GetComponent<Growth>().Stage : 1f;
        if (stage < ChopMinGrowthStage) return false;
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

    // Single-click lamp placement. Rejects if a wall, door, tree, lamp,
    // or any other job already sits on the tile. Lamps don't block
    // walking so we don't touch the map view; an in-progress build is
    // an entity carrying LampBlueprint + a LampBuild job that swaps to
    // Lamp on completion.
    public bool TryPlaceLampBlueprint(TilePos tile) => TryPlaceLampBlueprint(tile, LightColor.White);

    public bool TryPlaceLampBlueprint(TilePos tile, LightColor color)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.GetWall(tile) != WallType.None) return false;
        if (_trees.ContainsKey(tile)) return false;
        if (_doorMap.ContainsKey(tile)) return false;
        if (_lampMap.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new LampBlueprint { Tile = tile, ProgressSec = 0f, Color = color });
        var id = Jobs.Post(JobKind.LampBuild, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    // Post a single LampDeconstruct job. The Lamp entity stays in
    // _lampMap until completion; the job carries a fresh Decon marker
    // that LampSystem ticks against.
    public bool TryPostLampDeconstructJob(TilePos tile)
    {
        if (!_lampMap.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.LampDeconstruct, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    // Cheat toggle: flip the lamp's PoweredOn flag and recompute light
    // so the falloff disappears / reappears immediately.
    public void SetLampPowered(TilePos tile, bool on)
    {
        if (!_lampMap.TryGetValue(tile, out var lampEnt)) return;
        ref var lamp = ref lampEnt.GetComponent<Lamp>();
        if (lamp.PoweredOn == on) return;
        lamp.PoweredOn = on;
        RecomputeLampLight();
    }

    // Recolor a built lamp + re-stamp the light layer so the new tint
    // shows up immediately. No-op when the new color matches.
    public void SetLampColor(TilePos tile, LightColor color)
    {
        if (!_lampMap.TryGetValue(tile, out var lampEnt)) return;
        ref var lamp = ref lampEnt.GetComponent<Lamp>();
        if (lamp.Color.Equals(color)) return;
        lamp.Color = color;
        if (lamp.PoweredOn) RecomputeLampLight();
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

    // === Grow zones ===
    // Mirror of the stockpile API. Zones own a tile set; per-tile claim
    // is exclusive so the GrowZoneManager scan never double-posts.

    public int CreateGrowZoneRect(TilePos a, TilePos b, CropKind kind)
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
                if (_growZoneByTile.ContainsKey(t)) continue;
                tiles.Add(t);
            }
        }
        if (tiles.Count == 0) return 0;
        int id = _nextGrowZoneId++;
        var zone = new GrowZone(id, $"Grow Zone {id}", kind, tiles);
        _growZones.Add(zone);
        foreach (var t in tiles) _growZoneByTile[t] = zone;
        return id;
    }

    public int ExpandGrowZoneRect(int zoneId, TilePos a, TilePos b)
    {
        var zone = FindGrowZone(zoneId);
        if (zone is null) return 0;
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        int added = 0;
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                var t = new TilePos(x, y);
                if (!Map.InBounds(t)) continue;
                if (_growZoneByTile.ContainsKey(t)) continue;
                zone.Tiles.Add(t);
                _growZoneByTile[t] = zone;
                added++;
            }
        }
        return added;
    }

    public int ShrinkGrowZoneRect(int zoneId, TilePos a, TilePos b)
    {
        var zone = FindGrowZone(zoneId);
        if (zone is null) return 0;
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        int removed = 0;
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                var t = new TilePos(x, y);
                if (!zone.Tiles.Remove(t)) continue;
                _growZoneByTile.Remove(t);
                removed++;
            }
        }
        return removed;
    }

    public bool DeleteGrowZone(int zoneId)
    {
        var zone = FindGrowZone(zoneId);
        if (zone is null) return false;
        foreach (var t in zone.Tiles) _growZoneByTile.Remove(t);
        _growZones.Remove(zone);
        return true;
    }

    public bool RenameGrowZone(int zoneId, string name)
    {
        var zone = FindGrowZone(zoneId);
        if (zone is null) return false;
        zone.Name = name;
        return true;
    }

    public bool SetGrowZoneCropKind(int zoneId, CropKind kind)
    {
        var zone = FindGrowZone(zoneId);
        if (zone is null) return false;
        zone.CropKind = kind;
        return true;
    }

    public bool SetGrowZoneAllowCutting(int zoneId, bool allowed)
    {
        var zone = FindGrowZone(zoneId);
        if (zone is null) return false;
        zone.AllowCutting = allowed;
        return true;
    }

    public bool SetGrowZoneAllowSowing(int zoneId, bool allowed)
    {
        var zone = FindGrowZone(zoneId);
        if (zone is null) return false;
        zone.AllowSowing = allowed;
        return true;
    }

    private GrowZone? FindGrowZone(int id)
    {
        foreach (var z in _growZones) if (z.Id == id) return z;
        return null;
    }

    public bool TryGetGrowZoneAt(TilePos tile, out GrowZone zone)
    {
        if (_growZoneByTile.TryGetValue(tile, out var z)) { zone = z; return true; }
        zone = null!;
        return false;
    }

    // A tile is "sowable" if it's walkable, has no tree/crop/blueprint
    // sitting on it. The grow-zone manager calls this before posting a
    // Sow job so seeds don't land on top of in-progress walls / floors.
    public bool IsSowable(TilePos tile)
    {
        if (!Map.Walkable(tile)) return false;
        if (_trees.ContainsKey(tile)) return false;
        if (_crops.ContainsKey(tile)) return false;
        if (_doorMap.ContainsKey(tile)) return false;
        if (Map.GetFlooring(tile) != FlooringType.None) return false;
        // A blueprint / decon on the tile means in-progress build work —
        // don't fight it with sowing. Cheap O(N) scan over jobs is fine
        // at the 2s manager cadence.
        if (Jobs.HasTile(tile)) return false;
        return true;
    }

    // Post a Sow job at the tile. Job entity carries the SowSite marker
    // (kind + per-job progress). Returns false if a job already sits
    // there or the tile isn't sowable.
    public bool TryPostSowJob(TilePos tile, CropKind kind)
    {
        if (Jobs.HasTile(tile)) return false;
        if (!IsSowable(tile)) return false;
        var e = Store.CreateEntity();
        e.AddComponent(new SowSite { Tile = tile, Kind = kind, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.Sow, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    public void ReserveHaulDest(TilePos tile) => _reservedHaulDests.Add(tile);
    public void ReleaseHaulDest(TilePos tile) => _reservedHaulDests.Remove(tile);
    public bool IsHaulDestReserved(TilePos tile) => _reservedHaulDests.Contains(tile);

    public bool TryFindBestHaulDest(TilePos source, ItemDef def, out TilePos dest, out int stockpileId)
        => TryFindBestHaulDest(source, def, 1, out dest, out stockpileId);

    // Walks the player's zones and picks the best cell that accepts the item.
    // A cell is valid if it's empty OR holds the same item with room for
    // countToMove. Two-pass merge bias: pass 1 considers only partial-stack
    // tiles across ALL piles (so a colonist hauling from outside tops off
    // an existing pile instead of starting a fresh one — even if the
    // partial sits in a lower-priority zone). Pass 2 falls back to empty
    // tiles only if no merge target exists anywhere. Within each pass:
    // priority > existing count > distance.
    public bool TryFindBestHaulDest(TilePos source, ItemDef def, int countToMove, out TilePos dest, out int stockpileId)
    {
        dest = default;
        stockpileId = 0;

        var woodAt = new Dictionary<TilePos, int>();
        Store.Query<Wood>().ForEachEntity((ref Wood w, Entity ent) =>
        {
            if (w.Tile == source) return; // source tile doesn't block itself
            woodAt[w.Tile] = w.Count;
        });

        for (int pass = 0; pass < 2; pass++)
        {
            bool mergePass = pass == 0;
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
                    if (mergePass && existing <= 0) continue;
                    if (!mergePass && existing > 0) continue;
                    if (existing > 0 && existing + countToMove > WoodMaxStack) continue;
                    int d = Math.Abs(t.X - source.X) + Math.Abs(t.Y - source.Y);
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
            if (stockpileId != 0) return true;
        }
        return false;
    }

    // Pawn (re-)anchors every carried slot at dropTile, completes the
    // primary haul job, releases the dest reservation, and frees any
    // topoff reservations that the pawn never managed to physically pick
    // up so HaulSystem can re-post them. dropTile is usually the planned
    // DestTile, but the carrier passes its current tile instead when
    // delivery aborts (drafted mid-haul, dest blocked).
    public void DeliverCarrying(Entity carrier, TilePos dropTile, CommandBuffer cb)
    {
        if (!carrier.HasComponent<Carrying>()) return;
        var c = carrier.GetComponent<Carrying>();
        _reservedHaulDests.Remove(c.DestTile);
        if (!c.PrimaryJobId.IsNone)
        {
            var job = Jobs.Get(c.PrimaryJobId);
            if (job is not null) Jobs.Complete(c.PrimaryJobId);
        }
        List<CarriedSlot>? retained = null;
        if (c.Slots is not null)
        {
            foreach (var slot in c.Slots)
            {
                if (slot.Forbidden)
                {
                    retained ??= new List<CarriedSlot>();
                    retained.Add(slot);
                    continue;
                }
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
        cb.RemoveComponent<Carrying>(carrier.Id);
        if (retained is not null)
        {
            // Forbidden slots stay on the pawn. Reset haul-related fields
            // so HaulSystem / HandleHaul don't treat the pawn as mid-job.
            cb.AddComponent(carrier.Id, new Carrying
            {
                Slots = retained,
                PendingPickupIds = null,
                DestTile = default,
                StockpileId = 0,
                PrimaryJobId = default,
            });
        }
    }

    // Player-issued via the pawn info panel: drop a single inventory
    // slot at the pawn's current tile. Bypasses the "forbidden stays in
    // inventory" rule — this is the explicit player override that
    // forbidden cargo can be ejected with.
    public void ForceDropInventorySlot(int carrierId, int slotEntityId)
    {
        if (!Store.TryGetEntityById(carrierId, out var ent)) return;
        if (!ent.HasComponent<Carrying>()) return;
        if (!ent.HasComponent<WorldPos>()) return;
        var wp = ent.GetComponent<WorldPos>();
        var here = new TilePos((int)wp.X, (int)wp.Y);
        ref var c = ref ent.GetComponent<Carrying>();
        if (c.Slots is null) return;
        int idx = c.Slots.FindIndex(s => s.EntityId == slotEntityId);
        if (idx < 0) return;
        var slot = c.Slots[idx];
        c.Slots.RemoveAt(idx);
        if (Store.TryGetEntityById(slot.EntityId, out var slotEnt))
        {
            if (slotEnt.HasComponent<HaulReserved>()) slotEnt.RemoveComponent<HaulReserved>();
            if (slotEnt.HasComponent<HaulPayload>()) slotEnt.RemoveComponent<HaulPayload>();
            if (!slotEnt.HasComponent<Wood>()) slotEnt.AddComponent(new Wood { Tile = here, Count = slot.Count });
            if (!slotEnt.HasComponent<WorldPos>()) slotEnt.AddComponent(new WorldPos { X = here.X + 0.5f, Y = here.Y + 0.5f });
        }
        bool empty = (c.Slots.Count == 0) && (c.PendingPickupIds is null || c.PendingPickupIds.Count == 0);
        if (empty)
        {
            ent.RemoveComponent<Carrying>();
        }
    }

    public void SetInventorySlotForbidden(int carrierId, int slotEntityId, bool forbidden)
    {
        if (!Store.TryGetEntityById(carrierId, out var ent)) return;
        if (!ent.HasComponent<Carrying>()) return;
        ref var c = ref ent.GetComponent<Carrying>();
        if (c.Slots is null) return;
        int idx = c.Slots.FindIndex(s => s.EntityId == slotEntityId);
        if (idx < 0) return;
        var slot = c.Slots[idx];
        if (slot.Forbidden == forbidden) return;
        slot.Forbidden = forbidden;
        c.Slots[idx] = slot;
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

    // Drop a tree at a random walkable, unoccupied, tree-free tile. Initial
    // growth stage is randomized so a fresh map has a mix of mature trees
    // and saplings; later regrowth (world-engine target restock) should
    // call SpawnTreeAt with stage=0 so trees grow in visibly.
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

            float stage = 0.3f + (float)_spawnRng.NextDouble() * 0.7f;
            SpawnTreeAt(tile, stage);
            return true;
        }
        return false;
    }

    public Entity SpawnTreeAt(TilePos tile, float growthStage)
    {
        var e = Store.CreateEntity();
        e.AddComponent(new Tree { Tile = tile, ChopProgressSec = 0f });
        e.AddComponent(new Growth { Stage = Math.Clamp(growthStage, 0f, 1f) });
        _trees[tile] = e;
        return e;
    }

    public int TreeCount => _trees.Count;

    // World-engine restock pick. Caller (TreeRegrowSystem) gates the call
    // rate; this one method does the "find a candidate, drop a sapling"
    // step. Candidate tile must be walkable, outdoor, unoccupied, and have
    // a clear bufferTiles-Chebyshev ring around it (no player walls /
    // doors / floors / stockpiles / blueprints). New trees spawn at
    // stage 0 so growth is visible.
    public bool TryRegrowTreeSomewhere(int bufferTiles)
    {
        for (int attempts = 0; attempts < 64; attempts++)
        {
            int x = _spawnRng.Next(Map.Width);
            int y = _spawnRng.Next(Map.Height);
            if (!Map.Walkable(x, y)) continue;
            var tile = new TilePos(x, y);
            if (_trees.ContainsKey(tile)) continue;
            if (_crops.ContainsKey(tile)) continue;
            if (IsOccupied(x, y)) continue;
            if (!IsTileOutdoor(tile)) continue;
            if (IsTileNearPlayerStructure(tile, bufferTiles)) continue;
            SpawnTreeAt(tile, 0f);
            return true;
        }
        return false;
    }

    // Chebyshev ring scan + blueprint sweep. Returns true if any cell
    // within `radius` of `t` hosts a built wall / built floor / door /
    // stockpile tile, or if a wall/floor/door blueprint sits in the same
    // ring. Used to keep regrowth from crowding the player's base.
    public bool IsTileNearPlayerStructure(TilePos t, int radius)
    {
        int xmin = Math.Max(0, t.X - radius);
        int xmax = Math.Min(Map.Width - 1, t.X + radius);
        int ymin = Math.Max(0, t.Y - radius);
        int ymax = Math.Min(Map.Height - 1, t.Y + radius);
        for (int y = ymin; y <= ymax; y++)
        {
            for (int x = xmin; x <= xmax; x++)
            {
                if (Map.GetWall(x, y) != WallType.None) return true;
                if (Map.GetFlooring(x, y) != FlooringType.None) return true;
                var p = new TilePos(x, y);
                if (_doorMap.ContainsKey(p)) return true;
                if (_stockpileByTile.ContainsKey(p)) return true;
            }
        }
        bool near = false;
        Store.Query<Blueprint>().ForEachEntity((ref Blueprint b, Entity _) =>
        {
            if (Math.Abs(b.Tile.X - t.X) <= radius && Math.Abs(b.Tile.Y - t.Y) <= radius) near = true;
        });
        if (near) return true;
        Store.Query<FloorBlueprint>().ForEachEntity((ref FloorBlueprint b, Entity _) =>
        {
            if (Math.Abs(b.Tile.X - t.X) <= radius && Math.Abs(b.Tile.Y - t.Y) <= radius) near = true;
        });
        if (near) return true;
        Store.Query<DoorBlueprint>().ForEachEntity((ref DoorBlueprint b, Entity _) =>
        {
            if (Math.Abs(b.Tile.X - t.X) <= radius && Math.Abs(b.Tile.Y - t.Y) <= radius) near = true;
        });
        return near;
    }

    public IReadOnlyCollection<TilePos> CropTiles => _crops.Keys;
    public bool TryGetCrop(TilePos tile, out Entity entity) => _crops.TryGetValue(tile, out entity!);

    public bool SpawnRandomCarrot()
    {
        for (int attempts = 0; attempts < 512; attempts++)
        {
            int x = _spawnRng.Next(Map.Width);
            int y = _spawnRng.Next(Map.Height);
            if (!Map.Walkable(x, y)) continue;
            var tile = new TilePos(x, y);
            if (_trees.ContainsKey(tile)) continue;
            if (_crops.ContainsKey(tile)) continue;
            if (IsOccupied(x, y)) continue;
            float stage = (float)_spawnRng.NextDouble();
            SpawnCropAt(tile, CropKind.Carrot, stage);
            return true;
        }
        return false;
    }

    public Entity SpawnCropAt(TilePos tile, CropKind kind, float growthStage)
    {
        var e = Store.CreateEntity();
        e.AddComponent(new Crop { Tile = tile, Kind = kind, WorkProgressSec = 0f });
        e.AddComponent(new Growth { Stage = Math.Clamp(growthStage, 0f, 1f) });
        _crops[tile] = e;
        return e;
    }

    // CutPlants targets immature trees AND crops at any stage. Returns
    // false if nothing cuttable is on the tile or a job already sits on it.
    public bool TryPostCutPlantJob(TilePos tile)
    {
        if (Jobs.HasTile(tile)) return false;
        if (_trees.TryGetValue(tile, out var treeEnt))
        {
            float stage = treeEnt.HasComponent<Growth>() ? treeEnt.GetComponent<Growth>().Stage : 1f;
            if (stage >= ChopMinGrowthStage) return false; // mature trees use chop
            var id = Jobs.Post(JobKind.CutPlants, tile, treeEnt);
            return !id.IsNone;
        }
        if (_crops.TryGetValue(tile, out var cropEnt))
        {
            var id = Jobs.Post(JobKind.CutPlants, tile, cropEnt);
            return !id.IsNone;
        }
        return false;
    }

    // Harvest only targets crops at ≥75% growth.
    public bool TryPostHarvestJob(TilePos tile)
    {
        if (Jobs.HasTile(tile)) return false;
        if (!_crops.TryGetValue(tile, out var cropEnt)) return false;
        float stage = cropEnt.HasComponent<Growth>() ? cropEnt.GetComponent<Growth>().Stage : 0f;
        if (stage < HarvestMinGrowthStage) return false;
        var id = Jobs.Post(JobKind.Harvest, tile, cropEnt);
        return !id.IsNone;
    }

    // Tile is "outdoors" (light = 100%) when it's not enclosed by any
    // player-built room. RoomMap leaves outdoor / barrier tiles at room
    // id 0; indoor rooms get ids 1..N. Light stub: outdoor = grow,
    // indoor = no grow. Real per-tile light comes later.
    public bool IsTileOutdoor(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        int w = Map.Width;
        int idx = tile.Y * w + tile.X;
        if (idx < 0 || idx >= _roomTiles.Length) return false;
        return _roomTiles[idx] == 0;
    }

    // Per-tile room id lookup. 0 = outdoor faux room (including barrier
    // tiles); 1..RoomCount = enclosed interior. Returns -1 for out-of-
    // bounds. Reads _roomTiles directly — caller is the sim thread or
    // the render thread under _mapLock semantics (snapshot publishing
    // guarantees stability between recompute and snapshot read).
    public int RoomIdAt(TilePos tile)
    {
        if (!Map.InBounds(tile)) return -1;
        int idx = tile.Y * Map.Width + tile.X;
        if (idx < 0 || idx >= _roomTiles.Length) return -1;
        return _roomTiles[idx];
    }

    // Fixed-figure room temperature. Outdoor = OutdoorTempC; every
    // enclosed room clamps to IndoorTempC. Returns OutdoorTempC for
    // unknown ids so out-of-range reads degrade gracefully.
    public float RoomTempC(int roomId)
    {
        if (roomId < 0 || roomId >= _roomTemps.Length) return SimConstants.OutdoorTempC;
        return _roomTemps[roomId];
    }

    // Tile temperature = the temperature of the room the tile is in.
    public float TileTempC(TilePos tile) => RoomTempC(RoomIdAt(tile));

    // Grow check now reads against the tile's actual room temperature
    // instead of a blanket stub. 18..25°C is "comfortable" — both
    // fixed-figure temps land inside it so behavior is unchanged today,
    // but plants in a frozen / overheated room will refuse to grow once
    // per-room heat loss/gain ships.
    public bool IsTileGrowTemperature(TilePos tile)
    {
        float t = TileTempC(tile);
        return t >= 18f && t <= 25f;
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
        // Only player walls + doors enclose rooms. Procgen walls are
        // terrain, not room boundaries — empty maps should report 0
        // rooms. Outdoor (border-touching) components also collapse to 0.
        int count = RoomMap.Compute(w, h, _playerWalls, _doorMap.Keys, _roomTiles);
        RoomCount = count;
        // Resize + repopulate the per-room temperature table. Index 0 is
        // always the outdoor faux room; ids 1..count are interiors.
        if (_roomTemps.Length != count + 1) _roomTemps = new float[count + 1];
        _roomTemps[0] = SimConstants.OutdoorTempC;
        for (int i = 1; i <= count; i++) _roomTemps[i] = SimConstants.IndoorTempC;
        RoomVersion++;

        AutoRoofAfterRecompute();
    }

    // After a room recompute, ensure every interior tile + its bordering
    // wall/door tiles have either an existing roof or a queued RoofBuild
    // job. Auto-roof only POSTS jobs — it never adds tiles directly and
    // it never tears anything down. Skips tiles flagged no-roof and tiles
    // that already have any job (so a wall blueprint isn't shadowed by a
    // competing roof job; the next recompute after the wall finishes
    // catches it).
    //
    // Eligible tiles are bucketed by 3x3 grid cell (snapped to absolute
    // world coords via floor-divide). Each cell's eligible set becomes
    // one chunked RoofBuild job — pawn approaches one anchor, ticks
    // progress, flips every tile in the chunk on completion.
    private void AutoRoofAfterRecompute()
    {
        int w = Map.Width, h = Map.Height;
        EnsureRoofArrays(w, h);

        var chunks = new Dictionary<(int cx, int cy), List<TilePos>>();
        void Add(int x, int y)
        {
            int idx = y * w + x;
            if (_noRoofTiles[idx] != 0) return;
            if (_roofTiles[idx] != 0) return;
            if (Jobs.HasTile(new TilePos(x, y))) return;
            var key = (FloorDiv(x, 3), FloorDiv(y, 3));
            if (!chunks.TryGetValue(key, out var list))
            {
                list = new List<TilePos>(9);
                chunks[key] = list;
            }
            list.Add(new TilePos(x, y));
        }

        // Pass 1: interior tiles.
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (_roomTiles[row + x] == 0) continue;
                Add(x, y);
            }
        }
        // Pass 2: barrier tiles (walls/doors) with at least one interior
        // 8-neighbour — that covers the walls along each side AND the
        // corner walls (whose 4-neighbours are also walls; only diagonal
        // neighbours land inside the room).
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                if (_roomTiles[row + x] != 0) continue;
                bool nearInterior = false;
                for (int dy = -1; dy <= 1 && !nearInterior; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= h) continue;
                    int nrow = ny * w;
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        if (nx < 0 || nx >= w) continue;
                        if (_roomTiles[nrow + nx] != 0) { nearInterior = true; break; }
                    }
                }
                if (!nearInterior) continue;
                Add(x, y);
            }
        }

        foreach (var (_, list) in chunks) PostRoofBuildChunk(list);
    }

    // C#'s '/' truncates toward zero, which buckets negatives wrong (e.g.
    // -1/3 == 0, putting -1 in the same chunk as 0..2). Maps are 0-based
    // today, but use floor-divide so the chunking math stays correct if
    // negative tile coords ever land.
    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    // Post one chunked RoofBuild job for an already-filtered list of
    // eligible tiles (caller guarantees: unroofed, not no-roof, no job).
    // Anchor preference: 3x3-cell center if walkable + in list; else
    // first walkable in list; else first tile. Walkable anchor lets the
    // pawn park there and reach every chunk tile via Chebyshev≤1.
    private bool PostRoofBuildChunk(List<TilePos> tiles)
    {
        if (tiles.Count == 0) return false;
        var anchor = PickRoofChunkAnchor(tiles);
        var e = Store.CreateEntity();
        e.AddComponent(new RoofBlueprint { Tiles = tiles.ToArray(), Build = true });
        var extras = tiles.Count > 1 ? tiles.ToArray() : null;
        var id = Jobs.Post(JobKind.RoofBuild, anchor, e, extras);
        if (id.IsNone) { e.DeleteEntity(); return false; }
        return true;
    }

    private TilePos PickRoofChunkAnchor(List<TilePos> tiles)
    {
        int cx = FloorDiv(tiles[0].X, 3) * 3 + 1;
        int cy = FloorDiv(tiles[0].Y, 3) * 3 + 1;
        var center = new TilePos(cx, cy);
        TilePos firstWalkable = default;
        bool foundWalkable = false;
        foreach (var t in tiles)
        {
            if (t == center && Map.Walkable(t)) return center;
            if (!foundWalkable && Map.Walkable(t))
            {
                firstWalkable = t;
                foundWalkable = true;
            }
        }
        return foundWalkable ? firstWalkable : tiles[0];
    }

    // Post a RoofBuild job at the tile if none of the gates trip:
    //   - out of bounds
    //   - tile is already roofed
    //   - tile is flagged no-roof
    //   - the tile already carries any other job (wall blueprint, decon,
    //     pre-existing roof job, etc — first-come-first-served)
    public bool TryPostRoofBuildJob(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        int idx = tile.Y * Map.Width + tile.X;
        EnsureRoofArrays(Map.Width, Map.Height);
        if (_roofTiles[idx] != 0) return false;
        if (_noRoofTiles[idx] != 0) return false;
        if (Jobs.HasTile(tile)) return false;
        var e = Store.CreateEntity();
        e.AddComponent(new RoofBlueprint { Tiles = new[] { tile }, Build = true });
        var id = Jobs.Post(JobKind.RoofBuild, tile, e);
        if (id.IsNone) { e.DeleteEntity(); return false; }
        return true;
    }

    // Post a RoofRemove job if the tile is currently roofed and isn't
    // already queued for anything. Returns true if a job was posted.
    public bool TryPostRoofRemoveJob(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        int idx = tile.Y * Map.Width + tile.X;
        EnsureRoofArrays(Map.Width, Map.Height);
        if (_roofTiles[idx] == 0) return false;
        if (Jobs.HasTile(tile)) return false;
        var e = Store.CreateEntity();
        e.AddComponent(new RoofBlueprint { Tiles = new[] { tile }, Build = false });
        var id = Jobs.Post(JobKind.RoofRemove, tile, e);
        if (id.IsNone) { e.DeleteEntity(); return false; }
        return true;
    }

    // === Roof designator entry points ===
    // Drag-rect "build roof". Eligible tiles are bucketed by 3x3 grid
    // cell; each bucket becomes one chunked RoofBuild job that flips
    // every covered tile on completion. Tiles already roofed / flagged
    // no-roof / occupied by another job are skipped.
    public void PaintRoofRect(TilePos a, TilePos b)
    {
        int w = Map.Width, h = Map.Height;
        EnsureRoofArrays(w, h);
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        var chunks = new Dictionary<(int cx, int cy), List<TilePos>>();
        for (int y = Math.Max(0, ymin); y <= Math.Min(h - 1, ymax); y++)
        {
            int row = y * w;
            for (int x = Math.Max(0, xmin); x <= Math.Min(w - 1, xmax); x++)
            {
                int idx = row + x;
                if (_roofTiles[idx] != 0) continue;
                if (_noRoofTiles[idx] != 0) continue;
                var tile = new TilePos(x, y);
                if (Jobs.HasTile(tile)) continue;
                var key = (FloorDiv(x, 3), FloorDiv(y, 3));
                if (!chunks.TryGetValue(key, out var list))
                {
                    list = new List<TilePos>(9);
                    chunks[key] = list;
                }
                list.Add(tile);
            }
        }
        foreach (var (_, list) in chunks) PostRoofBuildChunk(list);
    }

    // Drag-rect "remove roof". For each tile in the rect:
    //   - if there's a pending RoofBuild chunk covering it, cancel the
    //     whole chunk (any chunk tiles outside the remove rect re-queue
    //     on the next auto-roof pass triggered below)
    //   - else if the tile is currently roofed, gather into 3x3 chunks
    //     and post a RoofRemove job per chunk
    // Does NOT touch the no-roof mark — that's a separate verb.
    public void RemoveRoofRect(TilePos a, TilePos b)
    {
        int w = Map.Width, h = Map.Height;
        EnsureRoofArrays(w, h);
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        var cancelled = new HashSet<JobId>();
        for (int y = Math.Max(0, ymin); y <= Math.Min(h - 1, ymax); y++)
        {
            for (int x = Math.Max(0, xmin); x <= Math.Min(w - 1, xmax); x++)
            {
                var tile = new TilePos(x, y);
                var existing = Jobs.GetByTile(tile);
                if (existing is null) continue;
                if (existing.Kind != JobKind.RoofBuild) continue;
                if (cancelled.Add(existing.Id)) CancelJob(existing.Id);
            }
        }
        var chunks = new Dictionary<(int cx, int cy), List<TilePos>>();
        for (int y = Math.Max(0, ymin); y <= Math.Min(h - 1, ymax); y++)
        {
            int row = y * w;
            for (int x = Math.Max(0, xmin); x <= Math.Min(w - 1, xmax); x++)
            {
                int idx = row + x;
                if (_roofTiles[idx] == 0) continue;
                var tile = new TilePos(x, y);
                if (Jobs.HasTile(tile)) continue;
                var key = (FloorDiv(x, 3), FloorDiv(y, 3));
                if (!chunks.TryGetValue(key, out var list))
                {
                    list = new List<TilePos>(9);
                    chunks[key] = list;
                }
                list.Add(tile);
            }
        }
        foreach (var (_, list) in chunks) PostRoofRemoveChunk(list);
        if (cancelled.Count > 0) AutoRoofAfterRecompute();
    }

    private bool PostRoofRemoveChunk(List<TilePos> tiles)
    {
        if (tiles.Count == 0) return false;
        var anchor = PickRoofChunkAnchor(tiles);
        var e = Store.CreateEntity();
        e.AddComponent(new RoofBlueprint { Tiles = tiles.ToArray(), Build = false });
        var extras = tiles.Count > 1 ? tiles.ToArray() : null;
        var id = Jobs.Post(JobKind.RoofRemove, anchor, e, extras);
        if (id.IsNone) { e.DeleteEntity(); return false; }
        return true;
    }

    // Drag-rect "no-roof zone". mark=true sets the no-roof flag AND
    // cancels any pending RoofBuild AND posts a RoofRemove for any
    // already-built roof in the rect. mark=false clears the flag and
    // re-runs auto-roof on the next tick (so freshly-eligible tiles
    // get build jobs queued again).
    public void SetNoRoofRect(TilePos a, TilePos b, bool mark)
    {
        int w = Map.Width, h = Map.Height;
        EnsureRoofArrays(w, h);
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        bool flagChanged = false;
        byte want = mark ? (byte)1 : (byte)0;
        var cancelled = new HashSet<JobId>();
        for (int y = Math.Max(0, ymin); y <= Math.Min(h - 1, ymax); y++)
        {
            int row = y * w;
            for (int x = Math.Max(0, xmin); x <= Math.Min(w - 1, xmax); x++)
            {
                int idx = row + x;
                if (_noRoofTiles[idx] != want)
                {
                    _noRoofTiles[idx] = want;
                    flagChanged = true;
                }
                if (!mark) continue;
                var tile = new TilePos(x, y);
                var existing = Jobs.GetByTile(tile);
                if (existing is not null && existing.Kind == JobKind.RoofBuild)
                {
                    if (cancelled.Add(existing.Id)) CancelJob(existing.Id);
                }
                if (_roofTiles[idx] != 0) TryPostRoofRemoveJob(tile);
            }
        }
        if (flagChanged)
        {
            RoofVersion++;
            if (!mark) _roomsDirty = true;
        }
        // Cancelling a chunk drops every tile in it, including tiles
        // outside the no-roof rect that should still be queued. Re-run
        // auto-roof to repost chunks for the leftovers.
        if (mark && cancelled.Count > 0) AutoRoofAfterRecompute();
    }

    private void EnsureRoofArrays(int w, int h)
    {
        int n = w * h;
        if (_roofTiles.Length != n) _roofTiles = new byte[n];
        if (_noRoofTiles.Length != n) _noRoofTiles = new byte[n];
        if (_lampR.Length != n)
        {
            _lampR = new byte[n];
            _lampG = new byte[n];
            _lampB = new byte[n];
            // Lamp buffer starts at zero — no lamps placed yet. Sun is
            // composed in at read time so we only need to seed the
            // global sun triple here.
            ComputeSun(_worldTimeSec, out byte sR, out byte sG, out byte sB);
            _lastSunR = sR; _lastSunG = sG; _lastSunB = sB;
            LightVersion++;
        }
    }

    // Single-tile roof toggle. Roof state is composition-only (it gates
    // whether sun reaches this tile at read time); the lamp buffer is
    // unaffected by roof flips. Just bump LightVersion so the renderer
    // recomposites.
    private void RecomputeLightAt(int idx)
    {
        if (idx < 0 || idx >= _lampR.Length) return;
        LightVersion++;
    }

    // Day/night sun. Hour-of-day drives intensity (smoothstep ramps over
    // 1h at dawn/dusk, full daylight 8-20, fully dark 21-7) and color
    // (orange near horizon at the bookends of each ramp, white at full
    // daylight). The renderer + every wall/floor lighting consumer reads
    // composed lamp + sun RGB grid; sun lives behind the same channel
    // model so colored lamps composite the same way day or night.
    // Noon = warm dim sun (was pure 1,1,1 white). Slight orange cast
    // and a small dim so the world doesn't look bleached at peak day.
    private const float SunMidR = 0.90f, SunMidG = 0.45f, SunMidB = 0.10f;          // noon — saturated orange (rev 3)
    // Sunrise/sunset = very saturated orange-red. Was 1.0/0.55/0.25;
    // dropped green + blue hard so the horizon ramp reads as a deep
    // golden-hour glow instead of a beige tint.
    private const float SunHorizonR = 1.00f, SunHorizonG = 0.22f, SunHorizonB = 0.00f; // sunrise/sunset deep red-orange
    public static void ComputeSun(double worldTimeSec, out byte r, out byte g, out byte b)
    {
        // hourOfDay: floating 0..24. Modulo on double so it survives any
        // accumulated drift past day-rollover without loss of precision.
        double hours = worldTimeSec / 3600.0;
        double hod = hours - Math.Floor(hours / 24.0) * 24.0;
        float intensity;
        float t; // 0 at horizon end of ramp, 1 at full-day end
        if (hod >= 8.0 && hod < 19.0)
        {
            intensity = 1f;
            t = 1f;
        }
        else if (hod >= 6.0 && hod < 8.0)
        {
            // Sunrise ramp: 6→8am (2hr). t=0 at 6am (horizon), t=1 at 8am (full day).
            t = (float)((hod - 6.0) / 2.0);
            intensity = Smoothstep(t);
        }
        else if (hod >= 19.0 && hod < 21.0)
        {
            // Sunset ramp: 7→9pm (2hr). t=1 at 7pm (full day), t=0 at 9pm (horizon).
            t = (float)((21.0 - hod) / 2.0);
            intensity = Smoothstep(t);
        }
        else
        {
            intensity = 0f;
            t = 0f;
        }
        // Color lerp between horizon orange (t=0) and midday white (t=1).
        // Night intensity=0 so color doesn't matter; keep the orange tint
        // so any debug visualization at intensity=0 reads as warm-dark.
        float cR = SunHorizonR + (SunMidR - SunHorizonR) * t;
        float cG = SunHorizonG + (SunMidG - SunHorizonG) * t;
        float cB = SunHorizonB + (SunMidB - SunHorizonB) * t;
        r = (byte)Math.Round(255f * cR * intensity);
        g = (byte)Math.Round(255f * cG * intensity);
        b = (byte)Math.Round(255f * cB * intensity);
    }
    private static float Smoothstep(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }

    // Debug / cheat hook: shift world time by a delta (in in-sim seconds).
    // Wired up by the +1hr / -1hr buttons in the debug bar. Forces a
    // sun recompute so the lighting reflects the new time immediately.
    public void AdvanceWorldTime(double deltaSec)
    {
        _worldTimeSec += deltaSec;
        if (_worldTimeSec < 0) _worldTimeSec = 0;
        _sunDirty = true;
    }

    // Per-lamp falloff. Master spec: 50% inside a 15x15 diameter disc,
    // 49→25% ramp in the 17x17 ring, 24→0% ramp in the 19x19 ring.
    // Squared-distance thresholds (tile units, lamp at center .5, .5):
    //   d² ≤ 56.25  (r ≤ 7.5)  → 128
    //   d² ≤ 72.25  (r ≤ 8.5)  → lerp 125 → 64 (49% → 25%)
    //   d² ≤ 90.25  (r ≤ 9.5)  → lerp 61  → 0  (24% → 0%)
    // Contributions max-blend against base sun/roof light, so a lamp
    // never darkens a tile.
    private const float LampInnerSq = 56.25f;
    private const float LampMidSq   = 72.25f;
    private const float LampOuterSq = 90.25f;
    // Lamp brightness ramp — literal spec values (50/49→25/24→0).
    // Sim publishes raw 0..255 bytes; the visual side applies a
    // non-linear curve so 50% reads close to 100% bright while shadows
    // keep the full ambient-vs-zero contrast.
    private const byte LampInner    = 128;  // 50%
    private const byte LampMidStart = 125;  // 49% — edge of inner disc
    private const byte LampMidEnd   = 64;   // 25% — ring fade
    private const byte LampOuterEnd = 0;    // 0%  — beyond visible

    // Render-side lamp visual brightness boost. Applied per byte in
    // CopyLightRgbForRender — bytes at or above LampVisualBoostFloor
    // (the inner disc) get scaled by LampVisualBoostScale clamped at
    // 255. Bytes below stay raw so the bilinear-extended lit area
    // does NOT grow visually — only the bright core pops harder.
    private const byte LampVisualBoostFloor = 125;
    private const float LampVisualBoostScale = 1.8f;
    private static byte BoostLampCore(byte raw)
    {
        if (raw < LampVisualBoostFloor) return raw;
        int v = (int)(raw * LampVisualBoostScale);
        if (v > 255) v = 255;
        return (byte)v;
    }


    // Full lamp-buffer recompute. Walks every powered lamp, max-blends
    // its falloff disc into _lampR/G/B with wall-LOS gating. Sun is NOT
    // touched here — it's composed in at read time (LightAt /
    // CopyLightRgbForRender) so this method only needs to fire when
    // lamps / walls / lamp power / lamp color change. Sun ticks during
    // sunrise/sunset are free.
    private void RecomputeLampLight()
    {
        int w = Map.Width, h = Map.Height;
        int n = _lampR.Length;
        Array.Clear(_lampR, 0, n);
        Array.Clear(_lampG, 0, n);
        Array.Clear(_lampB, 0, n);
        // Lamp pass. Walls block light: for each (lamp, target) pair, walk
        // a Bresenham line between them and skip the contribution if any
        // intermediate tile is a wall. Endpoints excluded so the lamp's
        // own tile + the wall tile adjacent to the lamp still light up.
        foreach (var (tile, lampEnt) in _lampMap)
        {
            if (!lampEnt.HasComponent<Lamp>()) continue;
            var lamp = lampEnt.GetComponent<Lamp>();
            if (!lamp.PoweredOn) continue;
            var col = lamp.Color;
            int cx = tile.X, cy = tile.Y;
            int x0 = Math.Max(0, cx - 9);
            int x1 = Math.Min(w - 1, cx + 9);
            int y0 = Math.Max(0, cy - 9);
            int y1 = Math.Min(h - 1, cy + 9);
            for (int y = y0; y <= y1; y++)
            {
                int dy = y - cy;
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    int dx = x - cx;
                    float d2 = dx * dx + dy * dy;
                    if (d2 > LampOuterSq) continue;
                    // Wall tiles read as 0% lit — the wall itself is
                    // opaque, no surface for light to land on. Skip
                    // before the LOS walk so adjacent lamps don't
                    // bleed into the wall cell.
                    if (Map.GetWall(x, y) != WallType.None) continue;
                    if (!LampLosClear(cx, cy, x, y)) continue;
                    byte contrib;
                    if (d2 <= LampInnerSq)
                    {
                        contrib = LampInner;
                    }
                    else if (d2 <= LampMidSq)
                    {
                        // sqrt(56.25)=7.5 .. sqrt(72.25)=8.5
                        float r = MathF.Sqrt(d2);
                        float t = (r - 7.5f) / 1.0f;
                        contrib = (byte)Math.Round(LampMidStart - (LampMidStart - LampMidEnd) * t);
                    }
                    else
                    {
                        // 8.5 .. 9.5
                        float r = MathF.Sqrt(d2);
                        float t = (r - 8.5f) / 1.0f;
                        contrib = (byte)Math.Round(LampMidEnd - (LampMidEnd - LampOuterEnd) * t);
                    }
                    int idx = row + x;
                    byte cr = (byte)(contrib * col.R / 255);
                    byte cg = (byte)(contrib * col.G / 255);
                    byte cb = (byte)(contrib * col.B / 255);
                    if (_lampR[idx] < cr) _lampR[idx] = cr;
                    if (_lampG[idx] < cg) _lampG[idx] = cg;
                    if (_lampB[idx] < cb) _lampB[idx] = cb;
                }
            }
        }
        LightVersion++;
    }

    // Bresenham line from (cx,cy) to (tx,ty). Returns false if any
    // intermediate tile (exclusive of both endpoints) carries a wall
    // or a door. Doors are LOS-opaque regardless of open/closed state —
    // the door tile itself still receives light because it's an endpoint
    // for lamps that target it, but light stops cold at the door so it
    // can't bleed into the next room.
    private bool LampLosClear(int cx, int cy, int tx, int ty)
    {
        if (cx == tx && cy == ty) return true;
        int dx = Math.Abs(tx - cx), dy = Math.Abs(ty - cy);
        int sx = cx < tx ? 1 : -1, sy = cy < ty ? 1 : -1;
        int err = dx - dy;
        int x = cx, y = cy;
        while (true)
        {
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x += sx; }
            if (e2 <  dx) { err += dx; y += sy; }
            if (x == tx && y == ty) return true;
            if (Map.GetWall(x, y) != WallType.None) return false;
            if (_doorMap.ContainsKey(new TilePos(x, y))) return false;
        }
    }

    // 0..1 brightness at a tile. Composes lamp + (sun if unroofed) on
    // demand. Out-of-bounds reads as dark (0).
    public float LightAt(TilePos tile)
    {
        if (!Map.InBounds(tile)) return 0f;
        int idx = tile.Y * Map.Width + tile.X;
        if (idx < 0 || idx >= _lampR.Length) return 0f;
        byte r = _lampR[idx], g = _lampG[idx], b = _lampB[idx];
        if (_roofTiles[idx] == 0)
        {
            if (_lastSunR > r) r = _lastSunR;
            if (_lastSunG > g) g = _lastSunG;
            if (_lastSunB > b) b = _lastSunB;
        }
        byte m = r; if (g > m) m = g; if (b > m) m = b;
        return m / 255f;
    }

    public byte[] CopyRoofTilesForRender()
    {
        lock (_mapLock) { return (byte[])_roofTiles.Clone(); }
    }

    public byte[] CopyNoRoofTilesForRender()
    {
        lock (_mapLock) { return (byte[])_noRoofTiles.Clone(); }
    }

    // Packed RGB per-tile light, composed at copy time. Output length =
    // 3 * width * height, interleaved as R,G,B,R,G,B,... For each tile:
    // roofed → lamp bytes only; outdoor → max(sun, lamp) per channel.
    // Wall tiles get the average of their non-wall neighbors instead of
    // their own (forced-zero) value — the multiply overlay sits on top
    // of the wall texture, and sampling the wall tile's own zero would
    // render walls in lit rooms as black. Neighbor-fill keeps lit-room
    // walls glowing without per-vertex bake work on the renderer side.
    public byte[] CopyLightRgbForRender()
    {
        lock (_mapLock)
        {
            int w = Map.Width;
            int h = Map.Height;
            int n = _lampR.Length;
            var rgb = new byte[n * 3];
            byte sR = _lastSunR, sG = _lastSunG, sB = _lastSunB;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    int j = i * 3;
                    bool isWall = Map.GetWall(x, y) != WallType.None;
                    byte r, g, b;
                    if (isWall)
                    {
                        // Walls store 0 in the light grid. Bilinear
                        // sampling then falls off from lit-floor → 0 at
                        // the wall center, which (a) naturally shades
                        // the wall (the half facing lit space reads
                        // brighter, the half facing dark reads dim) and
                        // (b) stops light bleeding through: dark-outside
                        // → 0-wall = 0 across the whole wall span. The
                        // old neighbor-avg fill let inside-lit rooms
                        // leak across walls to outside-adjacent pixels.
                        r = g = b = 0;
                    }
                    else
                    {
                        // Lamp visual boost — only scales bytes inside
                        // the lamp's core (>= LampVisualBoostFloor).
                        // Outer-ring falloff bytes stay at their raw
                        // values so bilinear sampling does NOT visually
                        // extend the lit area; only the bright core
                        // pops brighter. HUD's LightAt() reads raw
                        // _lampR so per-tile percentages stay truthful.
                        r = BoostLampCore(_lampR[i]);
                        g = BoostLampCore(_lampG[i]);
                        b = BoostLampCore(_lampB[i]);
                        if (_roofTiles[i] == 0)
                        {
                            if (sR > r) r = sR;
                            if (sG > g) g = sG;
                            if (sB > b) b = sB;
                        }
                    }
                    rgb[j]     = r;
                    rgb[j + 1] = g;
                    rgb[j + 2] = b;
                }
            }
            return rgb;
        }
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
