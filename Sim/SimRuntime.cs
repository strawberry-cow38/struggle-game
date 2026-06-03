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
using StruggleGame.Sim.Work;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim;

public sealed class SimRuntime
{
    public EntityStore Store { get; } = new();
    public TileMap Map { get; }
    public JobBoard Jobs { get; } = new();
    // Incremental spatial index of ground items, fed by the component
    // add/remove events wired in the constructor (see ItemSpatialIndex).
    private readonly ItemSpatialIndex _itemIndex = new();
    public ItemSpatialIndex ItemIndex => _itemIndex;
    // Needs (sleep / recreation) move over many sim-HOURS, so they don't
    // need 60 Hz updates. Run their decay every NeedTickInterval ticks with
    // the dt accumulated since the last run — the systems integrate by dt,
    // so a coarse step is mathematically identical to many fine ones.
    private const long NeedTickInterval = 250;
    private float _needAccumDt;
    public long Tick { get; private set; }
    public long MapVersion { get; private set; }
    // Bumps only when the WALL layer mutates (not floors/etc). The renderer
    // rebuilds the wall overlay + wall sprites off this, so placing floors
    // doesn't trigger a full wall-sprite rescan every tick.
    public long WallVersion { get; private set; }
    private bool _wallLayerDirty;
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
    // Lamp lighting recompute is expensive (rescans lamps + re-bakes LOS
    // discs). Coalesce it like the map/room rebuilds: mutation paths set
    // this flag (after InvalidateLampBakesNear marks which bakes are
    // stale) and RecomputeLampLight runs once per tick, not once per
    // wall/door completed.
    private bool _lightDirty;
    // Reused output buffer for CopyLightRgbForRender (render thread only).
    private byte[]? _lightRgbScratch;

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
    private readonly BedSystem _beds;
    private readonly StoveSystem _stoves;
    private readonly CookSystem _cooks;
    private readonly UrBoardSystem _urBoards;
    private readonly SandbagSystem _sandbags;
    private readonly RecreationSystem _recreation;
    private readonly DoorBuildSystem _doorBuilds;
    private readonly DoorSystem _doors;
    private readonly HaulSystem _hauls;
    private readonly BlueprintHaulSystem _bpHauls;
    private readonly BlueprintClearanceSystem _bpClearance;
    private readonly SafetySystem _safety;
    private readonly SleepSystem _sleep;
    private readonly HealthSystem _health;
    // Pawn tiles, rebuilt once per tick before the build phase. Buildable
    // systems read this to gate construction (don't spawn under a pawn)
    // instead of each rescanning every Wanderer.
    public readonly HashSet<TilePos> OccupiedPawnTiles = new();
    private ArchetypeQuery<WorldPos, Wanderer>? _occupiedPawnsQ;
    // Tiles a conscious colonist can see (LOS). Enemies weight their A* to
    // avoid these (don't run through colonist sightlines) and abandon cover to
    // open fire when caught standing in one near a target. Rebuilt on a
    // throttle and published as a fresh immutable set so path worker threads
    // read a captured reference safely (same discipline as MapView). Null
    // until first built.
    private volatile IReadOnlySet<TilePos>? _colonistLosTiles;
    private long _colonistLosNextTick;
    private ArchetypeQuery<WorldPos, Wanderer, Health>? _colonistLosQ;
    private const long ColonistLosRebuildInterval = 60; // throttle (~1s @ 60Hz)
    private const int ColonistLosRadius = 18;            // tiles a colonist's LOS reaches
    public IReadOnlySet<TilePos>? ColonistLosTiles => _colonistLosTiles;
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
    // Placed beds. Key = head/origin tile. Both head + foot tile are
    // entered into _bedOccupied so MapView treats the whole footprint
    // as blocked.
    private readonly Dictionary<TilePos, Entity> _bedMap = new();
    private readonly HashSet<TilePos> _bedOccupied = new();
    // Placed Ur boards. Tile → board entity. The board itself blocks
    // pathing (entered into _bedOccupied via MapView.HasFurniture path).
    private readonly Dictionary<TilePos, Entity> _urBoardMap = new();
    private readonly HashSet<TilePos> _urBoardOccupied = new();
    // Placed sandbags. Tile → sandbag entity. Walkable-but-slow (entered
    // into the furniture cost set, NOT the blocking set) and queryable as
    // low directional cover by the cover/peeking systems.
    private readonly Dictionary<TilePos, Entity> _sandbagMap = new();
    private readonly HashSet<TilePos> _sandbagOccupied = new();
    // Placed stoves. Key = origin (center body) tile. All 4 occupied
    // tiles (3 body + 1 standing) enter _stoveOccupied so MapView marks
    // them as high-cost furniture (pathable but A* avoids).
    private readonly Dictionary<TilePos, Entity> _stoveMap = new();
    private readonly HashSet<TilePos> _stoveOccupied = new();
    // Per-board seat reservations: board entity id → seated pawn ids.
    // Players + spectators share the same slot list; the role is stored
    // on the pawn's AtRecreation. Cleared by ReleaseUrSeat or board decon.
    private readonly Dictionary<int, HashSet<int>> _urBoardSeats = new();
    // Per-board active player count — distinct from total seats since a
    // spectator-only board has zero players. Bumped by ReserveUrSeat with
    // role=Player, decremented by ReleaseUrSeat.
    private readonly Dictionary<int, int> _urBoardPlayers = new();
    // In-flight sleep reservation: bed entity id → pawn entity id. Kept
    // out of ECS so Plan() can reserve from inside a query loop without
    // triggering Friflo's StructuralChangeException.
    private readonly Dictionary<int, int> _bedReservations = new();
    // Per-tile seconds remaining on a "roof just completed" flash. Render
    // side reads this via snapshot to fade the corrugated texture briefly
    // after RoofBuild completes; ticks down each Step.
    private const float RoofFlashSec = 0.6f;
    private readonly Dictionary<TilePos, float> _roofFlashes = new();
    // Transient blood-impact sprays at bullet-hit points (world tile coords +
    // remaining seconds). Cosmetic; aged out each tick.
    private readonly List<(float X, float Y, float Height, float Angle, float Scale, bool Dirt, float Sec)> _bloodImpacts = new();
    // Tile → blood-puddle entity (puddles persist, one per tile) so a bleed
    // drip is an O(1) lookup instead of a full BloodPuddle scan.
    private readonly Dictionary<TilePos, Entity> _bloodPuddleMap = new();
    private const float BloodImpactSec = 0.45f;
    // Cached per-lamp disc bake. Each entry is the lamp's static
    // contribution pattern (relative to its tile) baked against the
    // current wall/door layout. Color and power state are NOT baked —
    // those compose at RecomputeLampLight() time so changing them
    // never invalidates a bake. Wall/door change near a lamp marks
    // that bake Dirty so the next composite rebakes it.
    private readonly Dictionary<TilePos, LampBake> _lampBakes = new();

    private sealed class LampBake
    {
        // tile indices in the lamp buffer touched by this lamp's disc
        public int[] Indices = Array.Empty<int>();
        // raw 0..255 uncolored contribution per cell, parallel to Indices
        public byte[] Contribs = Array.Empty<byte>();
        public bool Dirty = true;
        // unique chunk ids this disc occupies — drives partial composite
        // and the per-chunk reverse-index subscription
        public int[] ChunkIds = Array.Empty<int>();
    }

    // Lightmap chunk grid. Chunked composite only re-clears + re-walks
    // lamps for chunks whose lamp set or wall layout has changed since
    // the last RecomputeLampLight pass. Distant chunks keep their bytes.
    private const int LightChunkSize = 16;
    private int _lightChunksW;
    private int _lightChunksH;
    private bool[] _lightChunkDirty = Array.Empty<bool>();
    // Reverse index: lamps whose disc touches each chunk. Kept in sync
    // with each bake's ChunkIds via SubscribeLampToChunks /
    // UnsubscribeLampFromChunks.
    private List<TilePos>[] _lampsByChunk = Array.Empty<List<TilePos>>();
    // Per-chunk roof / wall counts. Recomputed lazily when RoofVersion /
    // MapVersion bumps; consulted by the renderer composite to classify
    // each chunk as pure-open (no walls, no roofs, no lamps → just sun)
    // vs mixed (existing per-tile composite path).
    private int[] _chunkRoofCount = Array.Empty<int>();
    private int[] _chunkWallCount = Array.Empty<int>();
    private long _chunkRoofCountVersion = -1;
    private long _chunkWallCountVersion = -1;
    // Subset of doors flagged Forbidden — passed to MapView so A* + the
    // mover treat them like walls. Kept in sync with the Door component.
    private readonly HashSet<TilePos> _forbiddenDoorTiles = new();
    // Door blueprints placed on top of a built wall: the wall must
    // deconstruct first. Map tile → the parked DoorBlueprint entity so
    // CompleteJob(Deconstruct) knows to chain a DoorBuild job.
    private readonly Dictionary<TilePos, Entity> _pendingDoorAfterDecon = new();

    // Player-pinned blueprint claims. Blueprint entity id → pawn entity id
    // (the only colonist allowed to claim build / haul / decon jobs that
    // target that blueprint). Set via PrioritizeBlueprintForPawn from the
    // RMB-on-blueprint menu. Cleared by CompleteJob + CancelJob.
    private readonly Dictionary<int, int> _blueprintPriority = new();

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
    private const int InitialTreeCount = 100;
    // World engine restocks toward this count over time, biased away from
    // player structures. Same as InitialTreeCount for now — could diverge
    // later if biomes or weather thin trees out.
    public const int TargetTreeCount = 100;
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

    // asyncPathfinding: true in the live game (A* off the sim thread); default
    // false for tests/harness so path results land same-tick + deterministically.
    public SimRuntime(int seed = 1337, bool asyncPathfinding = false)
    {
        // Start at 08:00 on Jan 1 2000 — first daylight tick of the
        // epoch day so the world spawns under full sun, not midnight.
        _worldTimeSec = 8 * 3600;
        Map = TileMap.GenerateDefault(SimConstants.MapSize, SimConstants.MapSize, seed);
        _spawnRng = new Random(seed + 7);
        PathService = new PathService(Map.Width, Map.Height, () => MapView, asyncPathfinding);
        // Keep the item index in lockstep with the ECS. These fire at the
        // real structural-change moment (incl. CommandBuffer playback), so
        // deferred haul pickup/deliver are covered without per-site hooks.
        Store.OnComponentAdded += OnItemComponentAdded;
        Store.OnComponentRemoved += OnItemComponentRemoved;
        _dummies = new DummyController(PathService, Jobs, () => MapView, CancelJob, seed + 1, TryGetDoor, EffectivePriority, CurrentScheduleSlot, IsBlueprintFunded, GetJobBlueprintId, GetBlueprintClaimant, TryReserveBedAdapter, ReleaseBedReservation, TryReserveRecreation, ReleaseUrSeat, _itemIndex.AnyItemAt);
        _dummies.OnHaulPickup = (carriedEnt, cb) => OnHaulPickedUp(carriedEnt, cb);
        _dummies.OnHaulDeliver = (carrierEntity, dropTile, cb) => DeliverCarrying(carrierEntity, dropTile, cb);
        _dummies.CookFindNearestPile = (from, path) => FindNearestItemPile(from, path);
        _dummies.CookConsumePile = (tile, path, wanted) => TryConsumeFromPile(tile, path, wanted);
        _dummies.CookSpawnPile = (tile, path, count) => SpawnItemPile(tile, path, count);
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
        _beds = new BedSystem(this, Jobs);
        _stoves = new StoveSystem(this, Jobs);
        _cooks = new CookSystem(this, Jobs);
        _urBoards = new UrBoardSystem(this, Jobs);
        _sandbags = new SandbagSystem(this, Jobs);
        _recreation = new RecreationSystem(seed + 11, GetAvailableRecreationKinds);
        _doorBuilds = new DoorBuildSystem(this, Jobs);
        _doors = new DoorSystem(_itemIndex.AnyUnreservedItemAt);
        _hauls = new HaulSystem(this, Jobs);
        _bpHauls = new BlueprintHaulSystem(this, Jobs);
        _bpClearance = new BlueprintClearanceSystem(this, Jobs);
        _safety = new SafetySystem(() => MapView, PathService, Watcher);
        _sleep = new SleepSystem();
        _health = new HealthSystem(this);
        _health.SpawnBloodPuddle = SpawnBloodPuddle;
        _health.OnDowned = DropDownedItems;
        _health.OnDied = KillColonist;
        _dummies.MeleeHit = MeleeStrike;
        _dummies.LosClear = RangedLosClear;
        _dummies.HasSandbag = (x, y) => _sandbagMap.ContainsKey(new TilePos(x, y));
        _dummies.ColonistLosProvider = () => _colonistLosTiles;
        _dummies.LightProvider = (x, y) => LightAt(new TilePos(x, y));
        _dummies.HasTreatableWounds = HasTreatableWounds;
        _dummies.ApplyTreatment = ApplyTreatment;
        _dummies.HasRemovableBullet = HasRemovableBullet;
        _dummies.ApplyBulletRemoval = ApplyBulletRemoval;

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
        // Need decay runs on a rare tick (see NeedTickInterval). Accumulate
        // dt every tick; the gated systems below consume it when it fires.
        _needAccumDt += dt;
        bool needTick = Tick % NeedTickInterval == 0;
        float needDt = _needAccumDt;
        // Advance world time. Sun bytes derived once per tick; any change
        // marks the light grid dirty so the end-of-tick coalesce picks it
        // up. ComputeSun is cheap (a few mults + a smoothstep).
        _worldTimeSec += SimSecondsPerRealSecond * dt;
        ComputeSun(_worldTimeSec, out var sR, out var sG, out var sB);
        if (sR != _lastSunR || sG != _lastSunG || sB != _lastSunB) _sunDirty = true;
        // Refresh the colonist-LOS threat field on a throttle, before the
        // pawn brains run (enemies read it for path weighting + the caught-in-
        // the-open override).
        if (Tick >= _colonistLosNextTick)
        {
            _colonistLosNextTick = Tick + ColonistLosRebuildInterval;
            RebuildColonistLosTiles();
        }
        _dummies.Step(Store, dt, Tick);
        // Drain auto-bed-claim requests posted by Plan(). Safe to do
        // structural changes here — outside the controller's query loop.
        if (_dummies.PendingAutoBedClaims.Count > 0)
        {
            foreach (var (bedId, pawnId) in _dummies.PendingAutoBedClaims)
            {
                AssignBedToPawn(bedId, pawnId);
            }
            _dummies.PendingAutoBedClaims.Clear();
        }
        SpawnPendingProjectiles();
        StepProjectiles(dt);
        RebuildOccupiedPawnTiles();
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
        _beds.Step(Store, dt);
        _stoves.Step(Store, dt);
        _cooks.Step(Store, dt);
        _urBoards.Step(Store, dt);
        _sandbags.Step(Store, dt);
        if (needTick) _recreation.Step(Store, needDt);
        _doorBuilds.Step(Store, dt);
        _doors.Step(Store, dt);
        _bpClearance.Step(Store, dt);
        _bpHauls.Step(Store, dt);
        _hauls.Step(Store, dt);
        if (needTick) { _sleep.Step(Store, needDt); _needAccumDt = 0f; }
        _health.Step(Store, dt);
        AgeRoofFlashes(dt);
        AgeBloodImpacts(dt);
        // Merge/spill only ever act on a tile holding >=2 stacks. The item
        // index tracks that count (maintained via component add/remove events,
        // so it's correct across deferred haul pickup/deliver AND covers the
        // reservation-clear case the old "skip unless added" flag missed). When
        // no tile is coincident, both passes are guaranteed no-ops → skip the
        // full ItemPile scans entirely.
        if (_itemIndex.HasCoincidentTiles)
        {
            MergeCoincidentItemPiles();
            SpillCoincidentPiles();
        }
        _safety.Step(Store, Tick);
        // Coalesced rebuild: one map clone + one room flood-fill per tick
        // even if N walls/doors mutated this tick.
        if (_mapDirty) { DoRebuildMapView(); _mapDirty = false; }
        if (_roomsDirty) { DoRecomputeRooms(); _roomsDirty = false; }
        if (_lightDirty) { RecomputeLampLight(); _lightDirty = false; }
        if (_sunDirty) { _lastSunR = sR; _lastSunG = sG; _lastSunB = sB; LightVersion++; _sunDirty = false; }
        Tick++;
        Watcher.Observe(Tick, Store, Jobs);
#if DEBUG
        // Safety net: assert the item index never drifts from the ECS. If
        // a new item mutation path ever forgets to route through the
        // events / delete-site notify, this throws loudly in dev. Compiled
        // out of release builds entirely.
        if (Tick % 256 == 0) _itemIndex.ValidateAgainst(Store);
#endif
    }

    public IReadOnlyCollection<TilePos> TreeTiles => _trees.Keys;
    public bool TryGetTree(TilePos tile, out Entity entity) => _trees.TryGetValue(tile, out entity!);

    // Work-tab global mode. true = checkmark mode (DummyController reads
    // WorkPriorities.Allowed and treats each WorkType as on/off with the
    // default priority); false = priority mode (reads Priorities[] 1..8,
    // 0 = disabled). Defaults to checkmark — fresh worlds behave like the
    // pre-work-tab build until the player customises.
    public bool CheckmarkMode { get; private set; } = true;

    // Global "fire at will": when false, idle drafted colonists won't
    // auto-acquire enemies — they only fire at a player-forced (RMB) target.
    public bool FireAtWill { get; private set; } = true;
    public void SetFireAtWill(bool on) { FireAtWill = on; _dummies.FireAtWill = on; }

    // Debug bar toggle. When true, build systems skip BlueprintCost gating
    // entirely — useful while the haul-to-blueprint pipeline is still being
    // wired up. Defaults to true so existing playtests keep building.
    public bool GodModeFreeBuild { get; private set; } = true;

    public void SetGodModeFreeBuild(bool enabled) => GodModeFreeBuild = enabled;

    // Per-blueprint wood costs. Roof + lamp blueprints get no cost — we
    // don't yet have raw resources for steel/wire, so they ship free.
    public const int WallWoodCost = 5;
    public const int FloorWoodCost = 3;
    public const int DoorWoodCost = 20;
    public const int BedWoodCost = 45;
    public const int UrBoardWoodCost = 25;
    public const int SandbagWoodCost = 15;

    // Build-system funding check. Free pass when GodModeFreeBuild is on;
    // otherwise defer to per-blueprint BlueprintCost deposits.
    public bool IsBlueprintFunded(Entity blueprintEntity)
        => GodModeFreeBuild || World.BlueprintCostOps.IsFunded(blueprintEntity);

    // Apply a work-priority change from the work tab. Cancels any active
    // job of that work type if the new priority is "disabled" (0 in
    // priority mode, false in checkmark mode) so the pawn doesn't keep
    // working a job the player just forbade.
    public void SetWorkPriority(int entityId, WorkType type, byte priority)
    {
        if (!Store.TryGetEntityById(entityId, out var ent)) return;
        if (!ent.HasComponent<Wanderer>()) return;
        EnsureWorkPriorities(ent);
        ref var wp = ref ent.GetComponent<WorkPriorities>();
        if (priority > 8) priority = 8;
        wp.Priorities![(int)type] = priority;
        if (!CheckmarkMode && priority == 0) AbortJobIfWorkType(ent, type);
    }

    public void SetWorkCheckmark(int entityId, WorkType type, bool allowed)
    {
        if (!Store.TryGetEntityById(entityId, out var ent)) return;
        if (!ent.HasComponent<Wanderer>()) return;
        EnsureWorkPriorities(ent);
        ref var wp = ref ent.GetComponent<WorkPriorities>();
        wp.Allowed![(int)type] = allowed;
        if (CheckmarkMode && !allowed) AbortJobIfWorkType(ent, type);
    }

    public void SetCheckmarkMode(bool checkmarkMode)
    {
        if (CheckmarkMode == checkmarkMode) return;
        CheckmarkMode = checkmarkMode;
        // Switching mode can make in-flight jobs newly forbidden. Scan
        // every pawn and abort jobs whose work type is disallowed under
        // the new mode.
        (_wandererQ ??= Store.Query<WorldPos, Wanderer>()).ForEachEntity((ref WorldPos _, ref Wanderer _, Entity ent) =>
        {
            if (!ent.HasComponent<BuildTarget>()) return;
            var bt = ent.GetComponent<BuildTarget>();
            var job = Jobs.Get(bt.JobId);
            if (job is null) return;
            if (!WorkTypes.TryGet(job.Kind, out var wt)) return;
            if (!IsWorkTypeAllowed(ent, wt)) AbortPawnJob(ent);
        });
    }

    public static void EnsureWorkPriorities(Entity ent)
    {
        if (ent.HasComponent<WorkPriorities>())
        {
            ref var existing = ref ent.GetComponent<WorkPriorities>();
            if (existing.Priorities is null || existing.Priorities.Length != WorkTypes.Count)
            {
                existing.Priorities = new byte[WorkTypes.Count];
                for (int i = 0; i < WorkTypes.Count; i++) existing.Priorities[i] = WorkPriorities.DefaultPriority;
            }
            if (existing.Allowed is null || existing.Allowed.Length != WorkTypes.Count)
            {
                existing.Allowed = new bool[WorkTypes.Count];
                for (int i = 0; i < WorkTypes.Count; i++) existing.Allowed[i] = true;
            }
            return;
        }
        var wp = new WorkPriorities
        {
            Priorities = new byte[WorkTypes.Count],
            Allowed = new bool[WorkTypes.Count],
        };
        for (int i = 0; i < WorkTypes.Count; i++)
        {
            wp.Priorities[i] = WorkPriorities.DefaultPriority;
            wp.Allowed[i] = true;
        }
        ent.AddComponent(wp);
    }

    // Effective per-work-type priority for a pawn. Reads either Allowed
    // (checkmark mode) or Priorities (priority mode). Returns 0 = pawn
    // refuses jobs of that work type; 1..8 = take with that priority bucket
    // (1 highest, 8 lowest).
    public byte EffectivePriority(Entity ent, WorkType type)
    {
        if (!ent.HasComponent<WorkPriorities>()) return WorkPriorities.DefaultPriority;
        var wp = ent.GetComponent<WorkPriorities>();
        int idx = (int)type;
        if (CheckmarkMode)
        {
            bool allowed = wp.Allowed is not null && idx < wp.Allowed.Length && wp.Allowed[idx];
            return allowed ? WorkPriorities.DefaultPriority : (byte)0;
        }
        if (wp.Priorities is null || idx >= wp.Priorities.Length) return WorkPriorities.DefaultPriority;
        byte p = wp.Priorities[idx];
        return p > 8 ? (byte)8 : p;
    }

    public bool IsWorkTypeAllowed(Entity ent, WorkType type) => EffectivePriority(ent, type) > 0;

    private void AbortJobIfWorkType(Entity ent, WorkType type)
    {
        if (!ent.HasComponent<BuildTarget>()) return;
        var bt = ent.GetComponent<BuildTarget>();
        var job = Jobs.Get(bt.JobId);
        if (job is null) return;
        if (!WorkTypes.TryGet(job.Kind, out var jt)) return;
        if (jt != type) return;
        AbortPawnJob(ent);
    }

    private void AbortPawnJob(Entity ent)
    {
        var bt = ent.GetComponent<BuildTarget>();
        // Mid-haul: drop carried items at the pawn's current tile (or
        // dest as a fallback) so cargo doesn't vanish.
        if (ent.HasComponent<Carrying>())
        {
            TilePos here;
            if (ent.HasComponent<WorldPos>())
            {
                var wp = ent.GetComponent<WorldPos>();
                here = new TilePos((int)wp.X, (int)wp.Y);
            }
            else
            {
                here = ent.GetComponent<Carrying>().DestTile;
            }
            var cb = Store.GetCommandBuffer();
            DeliverCarrying(ent, here, cb);
            cb.Playback();
        }
        else
        {
            Jobs.Release(bt.JobId);
        }
        ent.RemoveComponent<BuildTarget>();
        if (ent.HasComponent<PathFollower>())
        {
            ref var pf = ref ent.GetComponent<PathFollower>();
            if (pf.PendingPathId != 0) PathService.Discard(pf.PendingPathId);
            pf.PendingPathId = 0;
            pf.Waypoints = null;
            pf.Index = 0;
        }
    }

    // Default schedule applied to every fresh pawn. RimWorld-ish split:
    // night sleep, light morning, productive day, evening recreation.
    private static readonly ScheduleCategory[] DefaultSchedule = new ScheduleCategory[24]
    {
        ScheduleCategory.Sleep, ScheduleCategory.Sleep, ScheduleCategory.Sleep, ScheduleCategory.Sleep,
        ScheduleCategory.Sleep, ScheduleCategory.Sleep,                                      // 0-5
        ScheduleCategory.Any, ScheduleCategory.Any,                                          // 6-7
        ScheduleCategory.Work, ScheduleCategory.Work, ScheduleCategory.Work, ScheduleCategory.Work, // 8-11
        ScheduleCategory.Recreation, ScheduleCategory.Recreation,                            // 12-13
        ScheduleCategory.Work, ScheduleCategory.Work, ScheduleCategory.Work, ScheduleCategory.Work, // 14-17
        ScheduleCategory.Recreation, ScheduleCategory.Recreation, ScheduleCategory.Recreation, ScheduleCategory.Recreation, // 18-21
        ScheduleCategory.Sleep, ScheduleCategory.Sleep,                                      // 22-23
    };

    // Every colonist needs a SleepNeed. Idempotent — sets the initial
    // level to 1.0 (well-rested) so freshly-spawned pawns aren't already
    // tired and the test/visual harness doesn't bias toward sleeping.
    public static void EnsureSleepNeed(Entity ent)
    {
        if (ent.HasComponent<SleepNeed>()) return;
        ent.AddComponent(new SleepNeed { Level = 1f });
    }

    public static void EnsureCombat(Entity ent)
    {
        if (!ent.HasComponent<Combat>()) ent.AddComponent(new Combat());
    }

    // Full-health body: full blood, no injuries, every capacity at 1.0.
    public static void EnsureHealth(Entity ent)
    {
        if (ent.HasComponent<Health>()) return;
        ent.AddComponent(new Health
        {
            BloodLevel = 1f,
            Injuries = new List<PartInjury>(),
            Consciousness = 1f, Moving = 1f, Manipulation = 1f,
            Sight = 1f, BloodPumping = 1f, Breathing = 1f,
            Unconscious = false,
        });
    }

    // RecreationNeed + RecreationPreference. Initial Kind sentinel (255)
    // tells RecreationSystem to roll on the first eligible tick. SecondsUntilRoll
    // starts at 0 so the first tick after spawn picks immediately from the
    // live pool.
    public static void EnsureRecreationNeed(Entity ent)
    {
        if (!ent.HasComponent<RecreationNeed>())
        {
            ent.AddComponent(new RecreationNeed { Level = 1f });
        }
        if (!ent.HasComponent<RecreationPreference>())
        {
            ent.AddComponent(new RecreationPreference { Kind = (RecreationKind)255, SecondsUntilRoll = 0f });
        }
    }

    public static void EnsureSchedule(Entity ent)
    {
        if (ent.HasComponent<Schedule>())
        {
            ref var existing = ref ent.GetComponent<Schedule>();
            if (existing.Slots is null || existing.Slots.Length != Schedule.Hours)
            {
                existing.Slots = new byte[Schedule.Hours];
                for (int i = 0; i < Schedule.Hours; i++) existing.Slots[i] = (byte)DefaultSchedule[i];
            }
            return;
        }
        var s = new Schedule { Slots = new byte[Schedule.Hours] };
        for (int i = 0; i < Schedule.Hours; i++) s.Slots[i] = (byte)DefaultSchedule[i];
        ent.AddComponent(s);
    }

    public int CurrentHour
    {
        get
        {
            int h = ((int)Math.Floor(_worldTimeSec / 3600.0)) % 24;
            if (h < 0) h += 24;
            return h;
        }
    }

    // Per-pawn schedule slot for the current world hour. Falls back to
    // Any when the pawn has no Schedule component, so legacy spawn paths
    // and tests behave like the pre-schedule build.
    public ScheduleCategory CurrentScheduleSlot(Entity ent)
    {
        if (!ent.HasComponent<Schedule>()) return ScheduleCategory.Any;
        var s = ent.GetComponent<Schedule>();
        if (s.Slots is null || s.Slots.Length != Schedule.Hours) return ScheduleCategory.Any;
        return (ScheduleCategory)s.Slots[CurrentHour];
    }

    // Paint inclusive range [hourStart..hourEnd] with the given category.
    // Range is allowed to wrap (e.g. 22..2 covers 22,23,0,1,2) so the UI
    // can drag across the midnight seam.
    public void PaintSchedule(int entityId, int hourStart, int hourEnd, ScheduleCategory cat)
    {
        if (!Store.TryGetEntityById(entityId, out var ent)) return;
        if (!ent.HasComponent<Wanderer>()) return;
        EnsureSchedule(ent);
        EnsureSleepNeed(ent);
        ref var sched = ref ent.GetComponent<Schedule>();
        if (hourStart < 0 || hourStart >= 24 || hourEnd < 0 || hourEnd >= 24) return;
        int h = hourStart;
        while (true)
        {
            sched.Slots![h] = (byte)cat;
            if (h == hourEnd) break;
            h = (h + 1) % 24;
        }
    }

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

    private readonly List<TilePos> _roofFlashKeys = new();

    private void AgeRoofFlashes(float dt)
    {
        if (_roofFlashes.Count == 0) return;
        // Reused scratch — runs every tick while a flash is live (~36 ticks
        // per roof build). Snapshot the keys so we can mutate the dict.
        _roofFlashKeys.Clear();
        foreach (var k in _roofFlashes.Keys) _roofFlashKeys.Add(k);
        List<TilePos>? dead = null;
        foreach (var t in _roofFlashKeys)
        {
            float v = _roofFlashes[t] - dt;
            if (v <= 0f) { (dead ??= new()).Add(t); }
            else _roofFlashes[t] = v;
        }
        if (dead is not null) foreach (var t in dead) _roofFlashes.Remove(t);
    }

    internal IReadOnlyDictionary<TilePos, float> RoofFlashes => _roofFlashes;
    internal const float RoofFlashDurationSec = RoofFlashSec;

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

    // === Bed assignment ===
    //
    // Each colonist may own at most one bed (AssignedBed on the pawn,
    // BedAssignee on the bed — kept as parallel mirrors). Assigning a
    // colonist to a new bed clears their old assignment first.
    // BedReservedBy is a separate in-flight marker used while walking
    // to or sleeping in a bed; it's NOT a long-term ownership pointer.
    public void AssignBedToPawn(int bedEntityId, int pawnEntityId)
    {
        if (pawnEntityId == 0) return;
        if (!Store.TryGetEntityById(pawnEntityId, out var pawn)) return;
        if (!Store.TryGetEntityById(bedEntityId, out var bed)) return;
        if (!bed.HasComponent<Bed>()) return;

        // Wipe pawn's old assignment first (clears mirror on the old bed
        // too). Then wipe whatever pawn currently owns this bed.
        UnassignPawnBed(pawn);
        if (bed.HasComponent<BedAssignee>())
        {
            var old = bed.GetComponent<BedAssignee>();
            if (old.PawnEntityId != 0
                && old.PawnEntityId != pawnEntityId
                && Store.TryGetEntityById(old.PawnEntityId, out var oldPawn))
            {
                UnassignPawnBed(oldPawn);
            }
        }

        pawn.AddComponent(new AssignedBed { BedEntityId = bedEntityId });
        bed.AddComponent(new BedAssignee { PawnEntityId = pawnEntityId });
    }

    public void UnassignPawnBed(Entity pawn)
    {
        if (!pawn.HasComponent<AssignedBed>()) return;
        var ab = pawn.GetComponent<AssignedBed>();
        if (Store.TryGetEntityById(ab.BedEntityId, out var bedEnt)
            && bedEnt.HasComponent<BedAssignee>())
        {
            bedEnt.RemoveComponent<BedAssignee>();
        }
        pawn.RemoveComponent<AssignedBed>();
    }

    // Called when a bed disappears (decon, future destruction).
    // Forgets the assignment from whichever pawn owned it AND clears
    // any in-flight reservation so the reserving pawn falls back to
    // floor sleep on the next plan tick.
    private void ForgetBed(Entity bedEnt)
    {
        int pawnId = 0;
        if (bedEnt.HasComponent<BedAssignee>())
        {
            pawnId = bedEnt.GetComponent<BedAssignee>().PawnEntityId;
        }
        if (pawnId != 0
            && Store.TryGetEntityById(pawnId, out var pawn)
            && pawn.HasComponent<AssignedBed>())
        {
            pawn.RemoveComponent<AssignedBed>();
        }
        // Any sleeper / walker on this bed has to be evicted: strip their
        // Sleeping component (SleepSystem stops gaining), and clear the
        // bed-side reservation. Walks abort on the next plan tick.
        int bedId = bedEnt.Id;
        _bedReservations.Remove(bedId);
        var sleepers = new List<Entity>();
        (_sleepingQ ??= Store.Query<Sleeping>()).ForEachEntity((ref Sleeping s, Entity ent) =>
        {
            if (s.BedEntityId == bedId) sleepers.Add(ent);
        });
        foreach (var s in sleepers) s.RemoveComponent<Sleeping>();
    }

    // Called when an Ur board is removed (decon). Evicts every pawn
    // currently seated at it: drop AtRecreation + release the seat
    // reservation so the seat bookkeeping stays sane.
    private void ForgetUrBoard(Entity boardEnt)
    {
        int bid = boardEnt.Id;
        var evictees = new List<Entity>();
        (_atRecreationQ ??= Store.Query<AtRecreation>()).ForEachEntity((ref AtRecreation ar, Entity ent) =>
        {
            if (ar.BoardEntityId == bid) evictees.Add(ent);
        });
        foreach (var ent in evictees)
        {
            ent.RemoveComponent<AtRecreation>();
        }
        _urBoardSeats.Remove(bid);
        _urBoardPlayers.Remove(bid);
    }

    // Atomic "pick a bed for this pawn to sleep in" call. Returns the
    // bed entity reserved to the pawn, or default if none is available.
    // Rules:
    //   1. If pawn has AssignedBed → that one (no need to check
    //      BedReservedBy; assignment beats reservation).
    //   2. Else nearest bed with no BedAssignee AND no BedReservedBy.
    //      "Don't steal" = beds owned by someone else are skipped.
    // Sets BedReservedBy = pawn on the returned bed.
    public bool TryReserveBedForSleep(Entity pawn, out Entity bed)
    {
        bed = default;
        if (pawn.HasComponent<AssignedBed>())
        {
            var ab = pawn.GetComponent<AssignedBed>();
            if (Store.TryGetEntityById(ab.BedEntityId, out var ownBed)
                && ownBed.HasComponent<Bed>())
            {
                ReserveBed(ownBed, pawn.Id);
                bed = ownBed;
                return true;
            }
            // Stale pointer: bed was destroyed without firing ForgetBed
            // (shouldn't happen — ForgetBed runs on decon). Fall through
            // to nearest-bed search; the dangling AssignedBed will be
            // overwritten or cleaned up on next ForgetBed.
        }

        // Look for the nearest unowned, unreserved bed.
        TilePos here = default;
        if (pawn.HasComponent<WorldPos>())
        {
            var wp = pawn.GetComponent<WorldPos>();
            here = new TilePos((int)wp.X, (int)wp.Y);
        }
        Entity bestBed = default;
        int bestDist = int.MaxValue;
        foreach (var kv in _bedMap)
        {
            var bedEnt = kv.Value;
            if (bedEnt.HasComponent<BedAssignee>()) continue;
            // Skip beds reserved by *other* pawns. The current pawn may
            // already own this reservation from a prior tick — that's
            // fine, re-use it instead of falling through to floor sleep.
            if (_bedReservations.TryGetValue(bedEnt.Id, out var resOwner)
                && resOwner != pawn.Id) continue;
            var origin = kv.Key;
            int d = Math.Abs(origin.X - here.X) + Math.Abs(origin.Y - here.Y);
            if (d < bestDist) { bestDist = d; bestBed = bedEnt; }
        }
        if (bestDist == int.MaxValue) return false;
        ReserveBed(bestBed, pawn.Id);
        bed = bestBed;
        return true;
    }

    private void ReserveBed(Entity bed, int pawnId)
    {
        _bedReservations[bed.Id] = pawnId;
    }

    public void ReleaseBedReservation(int bedEntityId, int pawnEntityId)
    {
        if (bedEntityId == 0) return;
        if (!_bedReservations.TryGetValue(bedEntityId, out var owner)) return;
        if (owner != pawnEntityId) return;
        _bedReservations.Remove(bedEntityId);
    }

    // Expose for DummyController. Returns the head tile a sleeper should
    // path to (= bed origin). Foot tile is also occupied by the bed but
    // pathing into the bed is allowed because the sleeper IS the body.
    // For simplicity walk to origin.
    // Adapter so DummyController.TryReserveBedDelegate can wrap
    // TryReserveBedForSleep + return both footprint tiles.
    private bool TryReserveBedAdapter(Entity pawn, out int bedEntityId, out TilePos bedOrigin, out TilePos bedFoot)
    {
        bedEntityId = 0;
        bedOrigin = default;
        bedFoot = default;
        if (!TryReserveBedForSleep(pawn, out var bed)) return false;
        if (!bed.HasComponent<Bed>()) return false;
        var b = bed.GetComponent<Bed>();
        bedEntityId = bed.Id;
        bedOrigin = b.Origin;
        bedFoot = BedOrientations.Foot(b.Origin, b.Orientation);
        return true;
    }

    public bool TryGetBedOriginTile(int bedEntityId, out TilePos origin)
    {
        origin = default;
        if (!Store.TryGetEntityById(bedEntityId, out var bed)) return false;
        if (!bed.HasComponent<Bed>()) return false;
        origin = bed.GetComponent<Bed>().Origin;
        return true;
    }

    // For a given job, return the entity id of the blueprint it targets:
    //   build-kind (Wall/Floor/Door/Bed) → job.Entity.Id (= blueprint)
    //   Haul with HaulPayload.BlueprintEntityId set → that id
    //   else 0
    // Used by DummyController to filter jobs against _blueprintPriority.
    public int GetJobBlueprintId(Job job)
    {
        switch (job.Kind)
        {
            case JobKind.WallBuild:
            case JobKind.FloorBuild:
            case JobKind.DoorBuild:
            case JobKind.BedBuild:
            case JobKind.UrBoardBuild:
            case JobKind.SandbagBuild:
                return job.Entity.Id;
            case JobKind.Haul:
                if (job.Entity.HasComponent<HaulPayload>())
                {
                    var hp = job.Entity.GetComponent<HaulPayload>();
                    if (hp.BlueprintEntityId != 0) return hp.BlueprintEntityId;
                }
                // Stockpile hauls have no blueprint — use the item entity's
                // own id as the pin key so "Prioritize Haul" can bind a
                // specific stockpile haul to a specific pawn. Unpinned
                // hauls return claimant 0 and behave normally.
                return job.Entity.Id;
            default:
                return 0;
        }
    }

    // Returns the pawn entity id pinned to this blueprint, or 0 if none.
    // DummyController calls this for every job during TryClaimJob.
    public int GetBlueprintClaimant(int blueprintEntityId)
    {
        if (blueprintEntityId == 0) return 0;
        return _blueprintPriority.TryGetValue(blueprintEntityId, out var pawnId) ? pawnId : 0;
    }

    // Pin a blueprint to a specific pawn (RMB → "Prioritize for X" menu).
    // Walks every BuildTarget-carrying pawn: if they're working on a job
    // that points at this blueprint AND they're not the new claimant,
    // evict them. Carriers get DeliverCarrying at their current tile so
    // any in-flight wood drops on the ground instead of vanishing; idle
    // claimants just release the job. _blueprintPriority is set first so
    // re-claim attempts during eviction respect the new owner.
    public void PrioritizeBlueprintForPawn(int blueprintEntityId, int pawnEntityId)
    {
        if (blueprintEntityId == 0 || pawnEntityId == 0) return;
        if (!Store.TryGetEntityById(blueprintEntityId, out _)) return;
        if (!Store.TryGetEntityById(pawnEntityId, out _)) return;
        _blueprintPriority[blueprintEntityId] = pawnEntityId;

        var evictees = new List<Entity>();
        var q = _buildTargetQ ??= Store.Query<BuildTarget>();
        q.ForEachEntity((ref BuildTarget bt, Entity ent) =>
        {
            if (ent.Id == pawnEntityId) return;
            var job = Jobs.Get(bt.JobId);
            if (job is null) return;
            if (GetJobBlueprintId(job) != blueprintEntityId) return;
            evictees.Add(ent);
        });

        foreach (var ent in evictees)
        {
            var bt = ent.GetComponent<BuildTarget>();
            if (ent.HasComponent<Carrying>())
            {
                TilePos here;
                if (ent.HasComponent<WorldPos>())
                {
                    var wp = ent.GetComponent<WorldPos>();
                    here = new TilePos((int)wp.X, (int)wp.Y);
                }
                else
                {
                    here = ent.GetComponent<Carrying>().DestTile;
                }
                var cb = Store.GetCommandBuffer();
                DeliverCarrying(ent, here, cb);
                cb.Playback();
            }
            else
            {
                Jobs.Release(bt.JobId);
            }
            ent.RemoveComponent<BuildTarget>();
            if (ent.HasComponent<PathFollower>())
            {
                ref var pf = ref ent.GetComponent<PathFollower>();
                if (pf.PendingPathId != 0) PathService.Discard(pf.PendingPathId);
                pf.PendingPathId = 0;
                pf.Waypoints = null;
                pf.Index = 0;
            }
        }
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
        if (_lightDirty) { RecomputeLampLight(); _lightDirty = false; }
        // Bump Tick so info panels (which dedupe re-renders on snap.Tick)
        // pick up the new decon marks / blueprints / forbid flags while
        // the sim is paused. No systems ran, so no gameplay timer advances.
        Tick++;
        return true;
    }

    // Double-buffered snapshot. We alternate which slot BuildSnapshot
    // writes into so the renderer can keep reading the previously
    // published instance while the next one is being assembled. Section
    // arrays inside each slot are pooled + reused across ticks.
    private readonly SimSnapshot _snapSlotA = new();
    private readonly SimSnapshot _snapSlotB = new();
    private bool _useSlotA;

    // Cached query objects — Friflo queries are live reusable views; caching
    // avoids the ~640-byte allocation per Store.Query<>() call on every tick.
    private ArchetypeQuery<WorldPos, Wanderer>?  _wandererQ;
    private ArchetypeQuery<Sleeping>?            _sleepingQ;
    private ArchetypeQuery<AtRecreation>?        _atRecreationQ;
    private ArchetypeQuery<BuildTarget>?         _buildTargetQ;
    private ArchetypeQuery<ItemPile>?            _itemPileQ;
    private ArchetypeQuery<BloodPuddle>?         _bloodPuddleQ;
    private ArchetypeQuery<Projectile>?          _projectileQ;
    private ArchetypeQuery<DoorBlueprint>?       _doorBpQ;
    private ArchetypeQuery<Door>?                _doorQ;
    private ArchetypeQuery<LampBlueprint>?       _lampBpQ;
    private ArchetypeQuery<BedBlueprint>?        _bedBpQ;
    private ArchetypeQuery<UrBoardBlueprint>?    _urBoardBpQ;
    private ArchetypeQuery<SandbagBlueprint>?    _sandbagBpQ;
    private ArchetypeQuery<StoveBlueprint>?      _stoveBpQ;
    private ArchetypeQuery<WorldPos, Health>?    _worldPosHealthQ;
    private ArchetypeQuery<Blueprint>?           _blueprintQ;
    private ArchetypeQuery<FloorBlueprint>?      _floorBpQ;
    private ArchetypeQuery<RecreationNeed>?      _recreationNeedQ;

    private static void EnsureCap<T>(ref T[] arr, int needed)
    {
        if (arr.Length >= needed) return;
        int next = Math.Max(4, arr.Length == 0 ? needed : arr.Length * 2);
        while (next < needed) next *= 2;
        Array.Resize(ref arr, next);
    }

    // Enum→name without per-call allocation (Enum.ToString allocates each time).
    private static readonly string[] _jobKindNames = BuildJobKindNames();
    private static string[] BuildJobKindNames()
    {
        var arr = new string[256];
        foreach (Jobs.JobKind k in Enum.GetValues<Jobs.JobKind>()) arr[(byte)k] = k.ToString();
        return arr;
    }
    private static string JobKindName(Jobs.JobKind k) => _jobKindNames[(byte)k] ?? string.Empty;

    // Per-pawn display name, cached so the snapshot doesn't allocate a fresh
    // "Colonist N" string for every pawn every tick.
    private readonly Dictionary<int, string> _pawnNameCache = new();
    private string PawnName(int id)
    {
        if (!_pawnNameCache.TryGetValue(id, out var n)) { n = $"Colonist {id}"; _pawnNameCache[id] = n; }
        return n;
    }

    // Per-blueprint resource-cost arrays are only read by the info panel for
    // the SELECTED blueprint — the world renderer never touches Costs. So
    // only snapshot the cost array for selected tiles; everyone else gets the
    // shared empty singleton (zero alloc). Avoids N little array allocs/tick.
    private readonly HashSet<TilePos> _selBpTiles = new();
    private ResourceCostState[] CostsIfSelected(Entity ent, TilePos tile)
        => _selBpTiles.Count > 0 && _selBpTiles.Contains(tile)
            ? BlueprintCostOps.SnapshotEntries(ent)
            : System.Array.Empty<ResourceCostState>();

    public SimSnapshot BuildSnapshot(int? selectedDummyId = null, int[]? selectedDummyIds = null, IReadOnlyCollection<int>? selectedTreeIds = null, IReadOnlyCollection<int>? selectedWoodIds = null, IReadOnlyCollection<int>? selectedCropIds = null, IReadOnlyCollection<TilePos>? selectedBlueprintTiles = null)
    {
        _useSlotA = !_useSlotA;
        var snap = _useSlotA ? _snapSlotA : _snapSlotB;

        _selBpTiles.Clear();
        if (selectedBlueprintTiles != null) foreach (var t in selectedBlueprintTiles) _selBpTiles.Add(t);
        snap.SelectedBlueprintTiles = selectedBlueprintTiles as TilePos[] ?? System.Array.Empty<TilePos>();

        snap.Tick = Tick;
        snap.MapVersion = MapVersion;
        snap.WallVersion = WallVersion;
        snap.RoomVersion = RoomVersion;
        snap.RoomCount = RoomCount;
        snap.RoofVersion = RoofVersion;
        snap.LightVersion = LightVersion;
        snap.WorldTimeSec = _worldTimeSec;
        snap.SelectedDummyId = selectedDummyId;
        snap.SelectedDummyIds = selectedDummyIds ?? Array.Empty<int>();
        snap.SelectedPath = null;
        snap.SelectedOrders = null;
        snap.CheckmarkMode = CheckmarkMode;
        snap.FireAtWill = FireAtWill;

        var selSet = (selectedDummyIds is { Length: > 0 }) ? new HashSet<int>(selectedDummyIds) : null;
        List<PawnPathState>? selPaths = null;

        // Hover hit-chance: resolve the single selected drafted ranged pawn (if
        // exactly one is selected and armed). Its per-target single-shot odds
        // are published on each DummyState.AimHit below for the hover readout.
        float aimFromX = 0f, aimFromY = 0f, aimRecoil = 0f;
        float aimHeightSel = SimConstants.AimAutoHeight;
        Items.RangedSpec? aimSpec = null;
        var aimMode = Items.AimMode.Aimed;
        int aimShooterId = 0;
        if (selectedDummyIds is { Length: 1 }
            && Store.TryGetEntityById(selectedDummyIds[0], out var shEnt)
            && shEnt.HasComponent<Drafted>() && shEnt.HasComponent<WorldPos>()
            && shEnt.HasComponent<RangedCombat>() && TryGetEquippedRangedSpec(shEnt, out var shSpec))
        {
            var shPos = shEnt.GetComponent<WorldPos>();
            var shRc = shEnt.GetComponent<RangedCombat>();
            aimFromX = shPos.X; aimFromY = shPos.Y;
            aimRecoil = shRc.Recoil;
            aimSpec = shSpec;
            aimMode = shRc.AimMode;
            aimShooterId = shEnt.Id;
            aimHeightSel = shRc.TargetArea switch
            {
                Items.TargetArea.Head => SimConstants.AimHeadHeight,
                Items.TargetArea.Torso => SimConstants.BodyAimHeight,
                Items.TargetArea.Legs => SimConstants.AimLegsHeight,
                _ => SimConstants.AimAutoHeight,
            };
        }
        snap.AimShooterId = aimShooterId;
        Func<int, int, bool> aimIsWall = (x, y) => Map.GetWall(x, y) != WallType.None;
        Func<int, int, bool> aimIsSandbag = _sandbagMap.Count > 0
            ? (x, y) => _sandbagMap.ContainsKey(new TilePos(x, y))
            : (x, y) => false;

        var dq = _wandererQ ??= Store.Query<WorldPos, Wanderer>();
        EnsureCap(ref snap.DummiesBuf, dq.Count);
        var dummiesBuf = snap.DummiesBuf;
        int i = 0;
        dq.ForEachEntity((ref WorldPos p, ref Wanderer wr, Entity ent) =>
        {
            bool drafted = ent.HasComponent<Drafted>();
            string label;
            if (drafted)
            {
                label = "Drafted";
            }
            else
            {
                bool moving = false;
                if (ent.HasComponent<PathFollower>())
                {
                    var pf = ent.GetComponent<PathFollower>();
                    moving = pf.Waypoints is { Count: > 0 } && pf.Index < pf.Waypoints.Count;
                }
                label = moving ? "Wandering" : "Standing";
            }
            if (!drafted && ent.HasComponent<BuildTarget>())
            {
                var bt = ent.GetComponent<BuildTarget>();
                var j = Jobs.Get(bt.JobId);
                if (j is not null) label = JobKindName(j.Kind);
            }
            bool carrying = ent.HasComponent<Carrying>();
            if (carrying) label = "Haul";
            if (!drafted && ent.HasComponent<AtRecreation>())
            {
                var ar = ent.GetComponent<AtRecreation>();
                label = ar.Role == RecreationRole.Player ? "Playing Ur" : "Watching Ur";
            }
            else if (!drafted && ent.HasComponent<RecreationReservation>())
            {
                var rr = ent.GetComponent<RecreationReservation>();
                label = rr.Role == RecreationRole.Player ? "→ Play Ur" : "→ Watch Ur";
            }

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
            // Persistent inventory (equipped + general held) rides the same
            // carry budget as haul cargo, so its weight/bulk adds into the
            // same carryW/carryB totals shown by the pawn panel.
            EquippedSlotState[] equipped = Array.Empty<EquippedSlotState>();
            HeldStackState[] held = Array.Empty<HeldStackState>();
            if (ent.HasComponent<Inventory>())
            {
                var inv = ent.GetComponent<Inventory>();
                if (inv.Equipped is { Count: > 0 })
                {
                    equipped = new EquippedSlotState[inv.Equipped.Count];
                    for (int ei = 0; ei < inv.Equipped.Count; ei++)
                    {
                        var es = inv.Equipped[ei];
                        equipped[ei] = new EquippedSlotState(ei, es.ItemPath, es.Count, es.Slot);
                        if (ItemCatalog.ItemsByPath.TryGetValue(es.ItemPath, out var def))
                        {
                            carryW += def.Weight * es.Count;
                            carryB += def.Bulk * es.Count;
                        }
                    }
                }
                if (inv.Items is { Count: > 0 })
                {
                    held = new HeldStackState[inv.Items.Count];
                    for (int hi = 0; hi < inv.Items.Count; hi++)
                    {
                        var hs = inv.Items[hi];
                        held[hi] = new HeldStackState(hi, hs.ItemPath, hs.Count);
                        if (ItemCatalog.ItemsByPath.TryGetValue(hs.ItemPath, out var def))
                        {
                            carryW += def.Weight * hs.Count;
                            carryB += def.Bulk * hs.Count;
                        }
                    }
                }
            }
            float sleepLevel = ent.HasComponent<SleepNeed>() ? ent.GetComponent<SleepNeed>().Level : 1f;
            bool isSleeping = ent.HasComponent<Sleeping>();
            int assignedBedId = ent.HasComponent<AssignedBed>() ? ent.GetComponent<AssignedBed>().BedEntityId : 0;
            float recLevel = ent.HasComponent<RecreationNeed>() ? ent.GetComponent<RecreationNeed>().Level : 1f;
            RecreationKind? atRecKind = ent.HasComponent<AtRecreation>() ? ent.GetComponent<AtRecreation>().Kind : null;

            HealthState healthState = default;
            if (ent.HasComponent<Health>())
            {
                var hc = ent.GetComponent<Health>();
                InjuryState[] injuries = Array.Empty<InjuryState>();
                float bleedRate = 0f;
                if (hc.Injuries is { Count: > 0 })
                {
                    injuries = new InjuryState[hc.Injuries.Count];
                    for (int ii = 0; ii < hc.Injuries.Count; ii++)
                    {
                        var inj = hc.Injuries[ii];
                        injuries[ii] = new InjuryState(inj.PartId, inj.Kind, inj.Severity, inj.Caliber, inj.Lodged,
                            HealthSystem.BleedOf(inj), inj.Tended, inj.Stabilized, inj.TendQuality, inj.RemovalRequested);
                        bleedRate += World.HealthSystem.BleedOf(inj);
                    }
                }
                healthState = new HealthState(
                    hc.BloodLevel, bleedRate, hc.Pain, hc.Consciousness, hc.Moving, hc.Manipulation,
                    hc.Sight, hc.OverallHealth, hc.Unconscious, injuries);
            }

            long swingT = 0, missT = 0, flinchT = 0;
            if (ent.HasComponent<Combat>())
            {
                var cm = ent.GetComponent<Combat>();
                swingT = cm.SwingTick; missT = cm.MissTick; flinchT = cm.FlinchTick;
            }
            int meleeTargetId = ent.HasComponent<MeleeTarget>() ? ent.GetComponent<MeleeTarget>().TargetEntityId : 0;

            bool hasRanged = false;
            string? loadedAmmo = null;
            int rangedMag = 0, rangedMagSize = 0, fireTargetId = 0;
            long shotTick = 0;
            float rangedRange = 0f;
            Items.FireMode rangedMode = Items.FireMode.Single;
            Items.FireModeFlags rangedModes = Items.FireModeFlags.None;
            var rangedStatus = Snapshots.RangedStatus.None;
            var rangedArea = Items.TargetArea.Auto;
            var rangedAimMode = Items.AimMode.Aimed;
            byte coverStance = 0;
            bool leaning = false;
            float peekX = p.X, peekY = p.Y;
            bool rangedHasAmmo = false;
            byte fireMeterPhase = 0;
            float fireMeterProgress = 0f;
            float treatProgress = 0f;
            if (ent.HasComponent<TreatmentTarget>())
            {
                var tt = ent.GetComponent<TreatmentTarget>();
                long span = tt.WorkUntilTick - tt.WorkStartTick;
                if (tt.WorkUntilTick > 0 && span > 0)
                    treatProgress = Math.Clamp((Tick - tt.WorkStartTick) / (float)span, 0f, 1f);
            }
            if (ent.HasComponent<RangedCombat>() && TryGetEquippedRangedSpec(ent, out var rspec))
            {
                var rc = ent.GetComponent<RangedCombat>();
                hasRanged = true;
                coverStance = (byte)rc.Stance;
                leaning = rc.Leaning;
                peekX = rc.PeekX; peekY = rc.PeekY;
                // Can it fire at all — loaded rounds, or spare ammo to reload?
                rangedHasAmmo = rc.MagCount > 0;
                if (!rangedHasAmmo && ent.HasComponent<Inventory>())
                {
                    var rinv = ent.GetComponent<Inventory>();
                    if (rinv.Items is not null)
                        foreach (var s in rinv.Items)
                            if (s.Count > 0
                                && Items.ItemCatalog.ItemsByPath.TryGetValue(s.ItemPath, out var ad)
                                && ad.Ammo is not null && ad.Ammo.CategoryPath == rspec.AmmoCategoryPath)
                            { rangedHasAmmo = true; break; }
                }
                rangedMag = rc.MagCount;
                rangedMagSize = rspec.MagazineSize;
                loadedAmmo = rc.LoadedAmmoPath;
                fireTargetId = rc.TargetEntityId;
                shotTick = rc.ShotTick;
                rangedMode = rc.Mode;
                rangedModes = rspec.Modes;
                rangedRange = rspec.Range;
                rangedArea = rc.TargetArea;
                rangedAimMode = rc.AimMode;
                // Overhead-label state: reloading > firing (in range + LoS) >
                // watching (target there but blocked/out of range).
                if (rc.Reloading) rangedStatus = Snapshots.RangedStatus.Reloading;
                else if (rc.TargetEntityId != 0
                    && Store.TryGetEntityById(rc.TargetEntityId, out var ftgt) && ftgt.HasComponent<WorldPos>())
                {
                    var ftp = ftgt.GetComponent<WorldPos>();
                    float fdx = ftp.X - p.X, fdy = ftp.Y - p.Y;
                    float fdist = MathF.Sqrt(fdx * fdx + fdy * fdy);
                    bool los = RangedLosClear((int)p.X, (int)p.Y, (int)ftp.X, (int)ftp.Y);
                    rangedStatus = fdist < SimConstants.RangedMinFireRange ? Snapshots.RangedStatus.TooClose
                        : (fdist <= rspec.Range && los) ? Snapshots.RangedStatus.Firing
                        : Snapshots.RangedStatus.Watching;
                }
                // Firing pie meter: aiming (spot-to-fire) shows first, then the
                // shot/burst cooldown — same fields the sim gates firing on.
                if (rc.TargetEntityId != 0 && !rc.Reloading)
                {
                    if (Tick < rc.AimReadyTick && rspec.AimTicks > 0)
                    {
                        fireMeterPhase = 1;
                        fireMeterProgress = 1f - (rc.AimReadyTick - Tick) / (float)rspec.AimTicks;
                    }
                    else if (Tick < rc.NextActionTick)
                    {
                        long cd = rc.BurstRemaining > 0 ? rspec.ShotCooldownTicks : rspec.CycleCooldownTicks;
                        if (cd > 0)
                        {
                            fireMeterPhase = 2;
                            fireMeterProgress = 1f - (rc.NextActionTick - Tick) / (float)cd;
                        }
                    }
                    fireMeterProgress = Math.Clamp(fireMeterProgress, 0f, 1f);
                }
            }
            // No active firing stance but crouched by a sandbag → show the
            // head-down crouch (covers melee/unarmed drafted pawns too).
            if (coverStance == 0 && wr.Crouched) coverStance = 1;

            // Hit chance FROM the selected shooter TO this pawn (skip the
            // shooter itself; needs a body to aim at).
            StruggleGame.Sim.Gunnery.HitChanceResult? aimHit = null;
            if (aimSpec is not null && ent.Id != aimShooterId && ent.HasComponent<Health>())
            {
                bool tDowned = ent.GetComponent<Health>().Unconscious;
                float tBodyH = tDowned ? SimConstants.DownedBodyHeight : SimConstants.PawnBodyHeight;
                float tAimH = tDowned ? SimConstants.DownedAimHeight : aimHeightSel;
                // Darkness: a target in shadow widens the cone (same model as
                // live fire). Lean: a popped-out leaning target is a smaller hit.
                float tLight = LightAt(new TilePos((int)p.X, (int)p.Y));
                float tSpreadMul = 1f + SimConstants.DarknessSpreadBonus * (1f - Math.Clamp(tLight, 0f, 1f));
                // Snapshot (or Auto resolving to snapshot at this range) widens the cone.
                float tDist = MathF.Sqrt((p.X - aimFromX) * (p.X - aimFromX) + (p.Y - aimFromY) * (p.Y - aimFromY));
                if (World.DummyController.ResolveSnapshot(aimMode, tDist, aimSpec.Range))
                    tSpreadMul *= SimConstants.SnapshotSpreadMultiplier;
                // Aim at the target's EFFECTIVE position — its peek cell while
                // leaning — so a peeking (visible) target isn't reported BLOCKED
                // just because its body tile sits behind the wall. Mirrors the
                // live-fire aim + the hitbox shift.
                bool tLean = ent.HasComponent<RangedCombat>()
                    && ent.GetComponent<RangedCombat>().Stance == CoverStance.Popped
                    && ent.GetComponent<RangedCombat>().Leaning;
                float tgtX = p.X, tgtY = p.Y;
                if (tLean)
                {
                    var trc = ent.GetComponent<RangedCombat>();
                    tgtX = p.X + (trc.PeekX - p.X) * SimConstants.LeanPeekFraction;
                    tgtY = p.Y + (trc.PeekY - p.Y) * SimConstants.LeanPeekFraction;
                }
                float tHitR = tLean ? ProjectileHitRadius * LeanHitFraction : ProjectileHitRadius;
                aimHit = StruggleGame.Sim.Gunnery.HitChanceEstimator.Estimate(
                    aimSpec, aimRecoil, aimFromX, aimFromY, tgtX, tgtY,
                    tBodyH, tAimH, aimIsWall, aimIsSandbag, tSpreadMul, tHitR);
                // A wall on the direct line isn't really "blocked" if the
                // shooter could lean-peek the target — recompute from the peek
                // cell so the readout shows the real odds, not BLOCKED.
                if (aimHit.Value.Cover == StruggleGame.Sim.Gunnery.HitCover.WallBlocked)
                {
                    var shooterTile = new TilePos((int)aimFromX, (int)aimFromY);
                    var tgtTile = new TilePos((int)tgtX, (int)tgtY);
                    if (_dummies.TryGetLeanCell(MapView, shooterTile, tgtTile, out var lean))
                        aimHit = StruggleGame.Sim.Gunnery.HitChanceEstimator.Estimate(
                            aimSpec, aimRecoil, lean.X + 0.5f, lean.Y + 0.5f, tgtX, tgtY,
                            tBodyH, tAimH, aimIsWall, aimIsSandbag, tSpreadMul, tHitR);
                }
            }

            dummiesBuf[i++] = new DummyState(
                ent.Id, p.X, p.Y, label, drafted, carrying,
                inventory, carryW, carryB,
                SimConstants.MaxCarryWeight, SimConstants.MaxCarryBulk,
                sleepLevel, isSleeping, assignedBedId,
                recLevel, atRecKind, equipped, held, healthState, wr.Facing,
                swingT, missT, flinchT, meleeTargetId,
                hasRanged, rangedMag, rangedMagSize, loadedAmmo, rangedMode, rangedModes,
                fireTargetId, shotTick, rangedRange, rangedStatus, rangedArea, rangedAimMode,
                coverStance, leaning, peekX, peekY, rangedHasAmmo,
                fireMeterPhase, fireMeterProgress, treatProgress,
                ent.HasComponent<Enemy>(),
                (byte)(ent.HasComponent<EnemyBrain>() ? ent.GetComponent<EnemyBrain>().Goal : EnemyGoalKind.None),
                aimHit,
                // Mood stub: deterministic per-entity 0.25..1.0 so the portrait
                // border color-coding is visible until a real mood system lands.
                0.25f + 0.75f * (((ent.Id * 2654435761u) % 1000u) / 1000f));

            // Capture path + queued tiles for every selected pawn, so the whole
            // squad shows its move lines + waypoints (not just the first pawn).
            if (selSet != null && selSet.Contains(ent.Id))
            {
                TilePos[]? path = null, orders = null;
                if (ent.HasComponent<PathFollower>())
                {
                    var pf = ent.GetComponent<PathFollower>();
                    if (pf.Waypoints is { Count: > 0 })
                    {
                        int remaining = pf.Waypoints.Count - pf.Index;
                        if (remaining > 0)
                        {
                            path = new TilePos[remaining];
                            for (int k = 0; k < remaining; k++)
                                path[k] = pf.Waypoints[pf.Index + k];
                        }
                    }
                }
                if (ent.HasComponent<OrderQueue>())
                {
                    var oq = ent.GetComponent<OrderQueue>();
                    if (oq.Tiles is { Count: > 0 }) orders = oq.Tiles.ToArray();
                }
                if (path != null || orders != null)
                {
                    if (ent.Id == selectedDummyId) { snap.SelectedPath = path; snap.SelectedOrders = orders; }
                    (selPaths ??= new List<PawnPathState>()).Add(
                        new PawnPathState(ent.Id, path ?? Array.Empty<TilePos>(), orders ?? Array.Empty<TilePos>()));
                }
            }
        });
        snap.DummiesCount = i;
        snap.SelectedPaths = selPaths?.ToArray() ?? Array.Empty<PawnPathState>();

        EnsureCap(ref snap.PawnWorkBuf, dq.Count);
        var pwBuf = snap.PawnWorkBuf;
        // Grow the per-pawn pools to the pawn count; inner arrays allocate only
        // when the pool grows (steady-state pawn count → zero allocation).
        int pawnCount = dq.Count;
        EnsureCap(ref snap.PawnWorkPriPool, pawnCount);
        EnsureCap(ref snap.PawnWorkAllowedPool, pawnCount);
        EnsureCap(ref snap.PawnWorkSchedPool, pawnCount);
        for (int k = 0; k < pawnCount; k++)
        {
            snap.PawnWorkPriPool[k] ??= new byte[WorkTypes.Count];
            snap.PawnWorkAllowedPool[k] ??= new bool[WorkTypes.Count];
            snap.PawnWorkSchedPool[k] ??= new byte[Schedule.Hours];
        }
        var priPool = snap.PawnWorkPriPool;
        var allowPool = snap.PawnWorkAllowedPool;
        var schedPool = snap.PawnWorkSchedPool;
        int pwi = 0;
        (_wandererQ ??= Store.Query<WorldPos, Wanderer>()).ForEachEntity((ref WorldPos _, ref Wanderer _, Entity ent) =>
        {
            // Enemies aren't colonists — they have no work tab / schedule and
            // lack those components, so EnsureXxx here would do a structural
            // AddComponent inside the query loop (StructuralChangeException).
            if (ent.HasComponent<Enemy>()) return;
            EnsureWorkPriorities(ent);
            EnsureSchedule(ent);
            EnsureSleepNeed(ent);
            var wp = ent.GetComponent<WorkPriorities>();
            var sched = ent.GetComponent<Schedule>();
            if (pwi >= priPool.Length) return; // pawn count grew mid-iteration; skip extra
            var pr = priPool[pwi];
            var al = allowPool[pwi];
            var sl = schedPool[pwi];
            Array.Clear(pr, 0, pr.Length);
            Array.Clear(al, 0, al.Length);
            Array.Clear(sl, 0, sl.Length);
            if (wp.Priorities is not null) Array.Copy(wp.Priorities, pr, Math.Min(wp.Priorities.Length, WorkTypes.Count));
            if (wp.Allowed is not null) Array.Copy(wp.Allowed, al, Math.Min(wp.Allowed.Length, WorkTypes.Count));
            if (sched.Slots is not null) Array.Copy(sched.Slots, sl, Math.Min(sched.Slots.Length, Schedule.Hours));
            pwBuf[pwi++] = new PawnWorkState(ent.Id, PawnName(ent.Id), pr, al, sl);
        });
        snap.PawnWorkCount = pwi;

        EnsureCap(ref snap.BlueprintsBuf, Jobs.Count);
        var bpsBuf = snap.BlueprintsBuf;
        int j = 0;
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.WallBuild) continue;
            var bp = job.Entity.GetComponent<Blueprint>();
            bpsBuf[j++] = new BlueprintState(job.Tile, bp.ProgressSec / BuildSystem.BuildTimeSec, job.Forbidden, BlueprintCostOps.FundingFraction(job.Entity), CostsIfSelected(job.Entity, job.Tile));
        }
        snap.BlueprintsCount = j;

        EnsureCap(ref snap.FloorBlueprintsBuf, Jobs.Count);
        var floorBuf = snap.FloorBlueprintsBuf;
        int fj = 0;
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.FloorBuild) continue;
            var bp = job.Entity.GetComponent<FloorBlueprint>();
            floorBuf[fj++] = new BlueprintState(job.Tile, bp.ProgressSec / FloorSystem.FloorTimeSec, job.Forbidden, BlueprintCostOps.FundingFraction(job.Entity), CostsIfSelected(job.Entity, job.Tile));
        }
        snap.FloorBlueprintsCount = fj;

        int roofCap = 0;
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.RoofBuild && job.Kind != JobKind.RoofRemove) continue;
            var bp = job.Entity.GetComponent<RoofBlueprint>();
            roofCap += bp.Tiles?.Length ?? 0;
        }
        EnsureCap(ref snap.RoofBlueprintsBuf, roofCap);
        var roofBuf = snap.RoofBlueprintsBuf;
        int rj = 0;
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.RoofBuild && job.Kind != JobKind.RoofRemove) continue;
            var bp = job.Entity.GetComponent<RoofBlueprint>();
            var tiles = bp.Tiles;
            if (tiles is null || tiles.Length == 0) continue;
            float perTile = job.Kind == JobKind.RoofBuild
                ? RoofSystem.RoofBuildTimeSec
                : RoofSystem.RoofRemoveTimeSec;
            float total = perTile * tiles.Length;
            float progress = total > 0f ? bp.ProgressSec / total : 0f;
            bool isBuild = job.Kind == JobKind.RoofBuild;
            foreach (var t in tiles)
            {
                roofBuf[rj++] = new RoofBlueprintState(t, progress, isBuild, job.Forbidden);
            }
        }
        snap.RoofBlueprintsCount = rj;

        EnsureCap(ref snap.RoofFlashesBuf, _roofFlashes.Count);
        var flashBuf = snap.RoofFlashesBuf;
        int fi = 0;
        foreach (var (t, sec) in _roofFlashes)
        {
            flashBuf[fi++] = new RoofFlashState(t, sec / RoofFlashSec);
        }
        snap.RoofFlashesCount = fi;

        EnsureCap(ref snap.NotificationsBuf, _notifications.Count);
        for (int ni = 0; ni < _notifications.Count; ni++) snap.NotificationsBuf[ni] = _notifications[ni];
        snap.NotificationsCount = _notifications.Count;

        EnsureCap(ref snap.TreesBuf, _trees.Count);
        var treesBuf = snap.TreesBuf;
        int k2 = 0;
        foreach (var (tile, ent) in _trees)
        {
            var tc = ent.GetComponent<Tree>();
            bool hasJob = Jobs.GetByTile(tile)?.Kind == JobKind.ChopTree;
            float stage = ent.HasComponent<Growth>() ? ent.GetComponent<Growth>().Stage : 1f;
            treesBuf[k2++] = new TreeState(ent.Id, tile, tc.ChopProgressSec / ChopSystem.ChopTimeSec, hasJob, stage);
        }
        snap.TreesCount = k2;

        EnsureCap(ref snap.CropsBuf, _crops.Count);
        var cropsBuf = snap.CropsBuf;
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
            cropsBuf[ci++] = new CropState(cEnt.Id, cTile, cc.Kind, cStage, work, activeKind);
        }
        snap.CropsCount = ci;

        var pileQuery = _itemPileQ ??= Store.Query<ItemPile>();
        EnsureCap(ref snap.ItemPilesBuf, pileQuery.Count);
        var pilesBuf = snap.ItemPilesBuf;
        int pi = 0;
        pileQuery.ForEachEntity((ref ItemPile p, Entity e) =>
        {
            string? label = e.HasComponent<Corpse>() ? e.GetComponent<Corpse>().Name : null;
            pilesBuf[pi++] = new ItemPileState(e.Id, p.Tile, p.Count, p.ItemPath, e.HasComponent<Forbidden>(), label);
        });
        snap.ItemPilesCount = pi;

        var puddleQuery = _bloodPuddleQ ??= Store.Query<BloodPuddle>();
        EnsureCap(ref snap.BloodPuddlesBuf, puddleQuery.Count);
        var puddlesBuf = snap.BloodPuddlesBuf;
        int bpi = 0;
        puddleQuery.ForEachEntity((ref BloodPuddle bp, Entity _) =>
        {
            puddlesBuf[bpi++] = new BloodPuddleState(bp.Tile, bp.Amount);
        });
        snap.BloodPuddlesCount = bpi;

        var projQuery = _projectileQ ??= Store.Query<Projectile>();
        EnsureCap(ref snap.ProjectilesBuf, projQuery.Count);
        var projBuf = snap.ProjectilesBuf;
        int pri = 0;
        projQuery.ForEachEntity((ref Projectile pr, Entity _) =>
        {
            bool isAp = pr.AmmoPath == Items.ItemCatalog.RifleAmmoAp.FullPath;
            projBuf[pri++] = new ProjectileState(pr.X, pr.Y, pr.Height, pr.Angle, pr.Speed, isAp, pr.OriginX, pr.OriginY);
        });
        snap.ProjectilesCount = pri;

        EnsureCap(ref snap.BloodImpactsBuf, _bloodImpacts.Count);
        var biBuf = snap.BloodImpactsBuf;
        for (int bi = 0; bi < _bloodImpacts.Count; bi++)
        {
            var b = _bloodImpacts[bi];
            biBuf[bi] = new BloodImpactState(b.X, b.Y, b.Height, b.Angle, b.Scale, b.Dirt, b.Sec / BloodImpactSec);
        }
        snap.BloodImpactsCount = _bloodImpacts.Count;


        int[] selTreeArr = Array.Empty<int>();
        if (selectedTreeIds is { Count: > 0 })
        {
            selTreeArr = new int[selectedTreeIds.Count];
            int si = 0;
            foreach (var id in selectedTreeIds) selTreeArr[si++] = id;
        }
        snap.SelectedTreeIds = selTreeArr;

        int[] selWoodArr = Array.Empty<int>();
        if (selectedWoodIds is { Count: > 0 })
        {
            selWoodArr = new int[selectedWoodIds.Count];
            int si = 0;
            foreach (var id in selectedWoodIds) selWoodArr[si++] = id;
        }
        snap.SelectedWoodIds = selWoodArr;

        int[] selCropArr = Array.Empty<int>();
        if (selectedCropIds is { Count: > 0 })
        {
            selCropArr = new int[selectedCropIds.Count];
            int si = 0;
            foreach (var id in selectedCropIds) selCropArr[si++] = id;
        }
        snap.SelectedCropIds = selCropArr;

        EnsureCap(ref snap.DeconsBuf, Jobs.Count);
        var deconsBuf = snap.DeconsBuf;
        int dj = 0;
        foreach (var job in Jobs.All)
        {
            if (job.Kind != JobKind.Deconstruct
                && job.Kind != JobKind.DoorDeconstruct
                && job.Kind != JobKind.LampDeconstruct
                && job.Kind != JobKind.BedDeconstruct) continue;
            var d = job.Entity.GetComponent<Decon>();
            float denom = job.Kind switch
            {
                JobKind.LampDeconstruct => LampSystem.LampDeconTimeSec,
                JobKind.BedDeconstruct => BedSystem.BedDeconTimeSec,
                _ => DeconSystem.DeconTimeSec,
            };
            deconsBuf[dj++] = new DeconState(job.Tile, d.ProgressSec / denom, job.Forbidden);
        }
        snap.DeconsCount = dj;

        // Include both active door-build blueprints and ones parked
        // waiting on a deconstruct (they have no DoorBuild job yet).
        var doorBpQuery = _doorBpQ ??= Store.Query<DoorBlueprint>();
        EnsureCap(ref snap.DoorBlueprintsBuf, doorBpQuery.Count);
        var doorBpBuf = snap.DoorBlueprintsBuf;
        int dbi = 0;
        doorBpQuery.ForEachEntity((ref DoorBlueprint bp, Entity ent) =>
        {
            bool forbidden = Jobs.GetByTile(bp.Tile)?.Forbidden ?? false;
            doorBpBuf[dbi++] = new BlueprintState(bp.Tile, bp.ProgressSec / DoorBuildSystem.DoorTimeSec, forbidden, BlueprintCostOps.FundingFraction(ent), CostsIfSelected(ent, bp.Tile));
        });
        snap.DoorBlueprintsCount = dbi;

        var doorQuery = _doorQ ??= Store.Query<Door>();
        EnsureCap(ref snap.DoorsBuf, doorQuery.Count);
        var doorBuf = snap.DoorsBuf;
        int dri = 0;
        doorQuery.ForEachEntity((ref Door d, Entity _) =>
        {
            float open = Math.Clamp(d.ProgressSec / DoorSystem.OpenTimeSec, 0f, 1f);
            doorBuf[dri++] = new DoorRenderState(d.Tile, d.Orientation, open, d.Forbidden, d.Locked, d.Priority);
        });
        snap.DoorsCount = dri;

        EnsureCap(ref snap.StockpilesBuf, _stockpiles.Count);
        var spBuf = snap.StockpilesBuf;
        for (int si = 0; si < _stockpiles.Count; si++)
        {
            var p = _stockpiles[si];
            var tiles = new TilePos[p.Tiles.Count];
            int ti = 0;
            foreach (var t in p.Tiles) tiles[ti++] = t;
            var allowed = new string[p.AllowedItemPaths.Count];
            int ai = 0;
            foreach (var path in p.AllowedItemPaths) allowed[ai++] = path;
            spBuf[si] = new StockpileState(p.Id, p.Name, p.Priority, tiles, allowed);
        }
        snap.StockpilesCount = _stockpiles.Count;

        EnsureCap(ref snap.GrowZonesBuf, _growZones.Count);
        var gzBuf = snap.GrowZonesBuf;
        for (int zi = 0; zi < _growZones.Count; zi++)
        {
            var z = _growZones[zi];
            var tiles = new TilePos[z.Tiles.Count];
            int ti = 0;
            foreach (var t in z.Tiles) tiles[ti++] = t;
            gzBuf[zi] = new GrowZoneState(z.Id, z.Name, z.CropKind, z.AllowCutting, z.AllowSowing, tiles);
        }
        snap.GrowZonesCount = _growZones.Count;

        EnsureCap(ref snap.LampsBuf, _lampMap.Count);
        var lampBuf = snap.LampsBuf;
        int li = 0;
        foreach (var (lTile, lEnt) in _lampMap)
        {
            var lc = lEnt.GetComponent<Lamp>();
            lampBuf[li++] = new LampState(lTile, lc.PoweredOn, lc.Color);
        }
        snap.LampsCount = li;

        EnsureCap(ref snap.BedsBuf, _bedMap.Count);
        var bedBuf = snap.BedsBuf;
        int bi2 = 0;
        foreach (var (origin, bEnt) in _bedMap)
        {
            var bc = bEnt.GetComponent<Bed>();
            int assignedPawnId = bEnt.HasComponent<BedAssignee>() ? bEnt.GetComponent<BedAssignee>().PawnEntityId : 0;
            bedBuf[bi2++] = new BedState(origin, bc.Orientation, assignedPawnId);
        }
        snap.BedsCount = bi2;

        var lampBpQuery = _lampBpQ ??= Store.Query<LampBlueprint>();
        EnsureCap(ref snap.LampBlueprintsBuf, lampBpQuery.Count);
        var lampBpBuf = snap.LampBlueprintsBuf;
        int lbi = 0;
        lampBpQuery.ForEachEntity((ref LampBlueprint bp, Entity ent) =>
        {
            bool forbidden = Jobs.GetByTile(bp.Tile)?.Forbidden ?? false;
            lampBpBuf[lbi++] = new BlueprintState(bp.Tile, bp.ProgressSec / LampSystem.LampBuildTimeSec, forbidden, BlueprintCostOps.FundingFraction(ent), CostsIfSelected(ent, bp.Tile));
        });
        snap.LampBlueprintsCount = lbi;

        var bedBpQuery = _bedBpQ ??= Store.Query<BedBlueprint>();
        EnsureCap(ref snap.BedBlueprintsBuf, bedBpQuery.Count);
        var bedBpBuf = snap.BedBlueprintsBuf;
        int bbi = 0;
        bedBpQuery.ForEachEntity((ref BedBlueprint bp, Entity ent) =>
        {
            bool forbidden = Jobs.GetByTile(bp.Origin)?.Forbidden ?? false;
            bedBpBuf[bbi++] = new BedBlueprintState(bp.Origin, bp.Orientation, bp.ProgressSec / BedSystem.BedBuildTimeSec, forbidden, BlueprintCostOps.FundingFraction(ent), CostsIfSelected(ent, bp.Origin));
        });
        snap.BedBlueprintsCount = bbi;

        EnsureCap(ref snap.UrBoardsBuf, _urBoardMap.Count);
        var urBuf = snap.UrBoardsBuf;
        int ubi = 0;
        foreach (var (tile, ubEnt) in _urBoardMap)
        {
            int boardId = ubEnt.Id;
            urBuf[ubi++] = new UrBoardState(boardId, tile, UrBoardPlayerCount(boardId), UrBoardSpectatorCount(boardId));
        }
        snap.UrBoardsCount = ubi;

        var urBpQuery = _urBoardBpQ ??= Store.Query<UrBoardBlueprint>();
        EnsureCap(ref snap.UrBoardBlueprintsBuf, urBpQuery.Count);
        var urBpBuf = snap.UrBoardBlueprintsBuf;
        int ubbi = 0;
        urBpQuery.ForEachEntity((ref UrBoardBlueprint bp, Entity ent) =>
        {
            bool forbidden = Jobs.GetByTile(bp.Tile)?.Forbidden ?? false;
            urBpBuf[ubbi++] = new BlueprintState(bp.Tile, bp.ProgressSec / UrBoardSystem.BuildTimeSec, forbidden, BlueprintCostOps.FundingFraction(ent), CostsIfSelected(ent, bp.Tile));
        });
        snap.UrBoardBlueprintsCount = ubbi;

        EnsureCap(ref snap.SandbagsBuf, _sandbagMap.Count);
        var sbBuf = snap.SandbagsBuf;
        int sbi = 0;
        foreach (var (tile, _) in _sandbagMap)
            sbBuf[sbi++] = new SandbagState(tile);
        snap.SandbagsCount = sbi;

        var sbBpQuery = _sandbagBpQ ??= Store.Query<SandbagBlueprint>();
        EnsureCap(ref snap.SandbagBlueprintsBuf, sbBpQuery.Count);
        var sbBpBuf = snap.SandbagBlueprintsBuf;
        int sbbi = 0;
        sbBpQuery.ForEachEntity((ref SandbagBlueprint bp, Entity ent) =>
        {
            bool forbidden = Jobs.GetByTile(bp.Tile)?.Forbidden ?? false;
            sbBpBuf[sbbi++] = new BlueprintState(bp.Tile, bp.ProgressSec / SandbagSystem.BuildTimeSec, forbidden, BlueprintCostOps.FundingFraction(ent), CostsIfSelected(ent, bp.Tile));
        });
        snap.SandbagBlueprintsCount = sbbi;

        EnsureCap(ref snap.StovesBuf, _stoveMap.Count);
        var stoveBuf = snap.StovesBuf;
        int stoveIdx = 0;
        foreach (var (origin, sEnt) in _stoveMap)
        {
            var sc = sEnt.GetComponent<Stove>();
            var board = sEnt.HasComponent<BillsBoard>() ? sEnt.GetComponent<BillsBoard>() : default;
            var bills = board.Bills;
            BillState[] billArr;
            if (bills is null || bills.Count == 0)
            {
                billArr = System.Array.Empty<BillState>();
            }
            else
            {
                billArr = new BillState[bills.Count];
                for (int bi3 = 0; bi3 < bills.Count; bi3++)
                {
                    var b = bills[bi3];
                    billArr[bi3] = new BillState(b.Recipe, b.RepeatMode, b.TargetCount, b.RemainingCount, b.OutputDest, b.StockpileEntityId);
                }
            }
            var recipeForProgress = Recipes.Get(sc.CurrentBillIndex >= 0 && sc.CurrentBillIndex < (bills?.Count ?? 0)
                ? bills![sc.CurrentBillIndex].Recipe
                : RecipeId.CookSimpleMeal);
            float progress01 = sc.CurrentBillIndex >= 0 && recipeForProgress.WorkTicks > 0
                ? Math.Clamp(sc.CookProgressTicks / recipeForProgress.WorkTicks, 0f, 1f)
                : 0f;
            stoveBuf[stoveIdx++] = new StoveState(sEnt.Id, origin, sc.Orientation, sc.CurrentBillIndex, progress01, sc.ActiveCookEntityId, billArr);
        }
        snap.StovesCount = stoveIdx;

        var stoveBpQuery = _stoveBpQ ??= Store.Query<StoveBlueprint>();
        EnsureCap(ref snap.StoveBlueprintsBuf, stoveBpQuery.Count);
        var stoveBpBuf = snap.StoveBlueprintsBuf;
        int stoveBpIdx = 0;
        stoveBpQuery.ForEachEntity((ref StoveBlueprint bp, Entity ent) =>
        {
            bool forbidden = Jobs.GetByTile(bp.Origin)?.Forbidden ?? false;
            stoveBpBuf[stoveBpIdx++] = new StoveBlueprintState(
                bp.Origin,
                bp.Orientation,
                bp.ProgressSec / StoveSystem.StoveBuildTimeSec,
                forbidden,
                BlueprintCostOps.FundingFraction(ent),
                CostsIfSelected(ent, bp.Origin));
        });
        snap.StoveBlueprintsCount = stoveBpIdx;

        return snap;
    }

    // Render layer snapshot: assembled from the published MapView's
    // chunks (immutable, lock-free) rather than from TileMap directly,
    // so the renderer sees consistent data for the MapVersion it asks
    // about even if a sim tick is mid-mutation.
    public byte[] CopyLayerForRender(MapLayer layer) => MapView.AssembleFlat(layer);

    // Harness shortcut: drop a finished door on the tile, no blueprint
    // or build job. Removes any wall already there; orientation is
    // resolved from the live wall layer.
    public bool InstantPlaceDoor(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (_doorMap.ContainsKey(tile)) return false;
        if (_trees.TryGetValue(tile, out var tree)) { _trees.Remove(tile); tree.DeleteEntity(); }
        lock (_mapLock)
        {
            if (Map.GetWall(tile) != WallType.None)
            {
                Map.SetWall(tile, WallType.None);
                _wallLayerDirty = true;
                _playerWalls.Remove(tile);
            }
        }
        var orientation = ComputeDoorOrientation(tile);
        var e = Store.CreateEntity();
        e.AddComponent(new Door
        {
            Tile = tile,
            Orientation = orientation,
            State = DoorState.Closed,
            ProgressSec = 0f,
            WantsOpen = false,
            IdleSec = 0f,
            Forbidden = false,
            Locked = true,
            Priority = DoorPriority.Medium,
        });
        _doorMap[tile] = e;
        _roomsDirty = true;
        RebuildMapView();
        // Door blocks LOS for lamp light; relight (instant-place
        // bypasses the job pipeline that normally handles this).
        InvalidateLampBakesNear(tile);
        _lightDirty = true;
        return true;
    }

    // Harness shortcut: drop a powered, color-tinted lamp on the tile.
    public bool InstantPlaceLamp(TilePos tile, LightColor color)
    {
        if (!Map.InBounds(tile)) return false;
        if (_lampMap.ContainsKey(tile)) return false;
        if (Map.GetWall(tile) != WallType.None) return false;
        if (_doorMap.ContainsKey(tile)) return false;
        if (_trees.TryGetValue(tile, out var tree)) { _trees.Remove(tile); tree.DeleteEntity(); }
        var e = Store.CreateEntity();
        e.AddComponent(new Lamp { Tile = tile, PoweredOn = true, Color = color });
        _lampMap[tile] = e;
        EnsureLampBaked(tile);
        _lightDirty = true;
        return true;
    }

    // Post a 2-tile bed blueprint at Origin oriented in the given
    // direction. Both footprint tiles (Origin + Foot) must be free of
    // walls, doors, trees, lamps, other beds, and other jobs. Occupancy
    // is reserved immediately so a second designation can't overlap.
    // BedSystem advances ProgressSec while a builder is adjacent; on
    // completion CompleteJob swaps BedBlueprint for Bed.
    public bool TryPlaceBedBlueprint(TilePos origin, BedOrientation orientation)
    {
        var foot = BedOrientations.Foot(origin, orientation);
        if (origin == foot) return false;
        if (!IsBedTileFree(origin) || !IsBedTileFree(foot)) return false;
        if (Jobs.HasTile(origin) || Jobs.HasTile(foot)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new BedBlueprint { Origin = origin, Orientation = orientation, ProgressSec = 0f });
        World.BlueprintCostOps.AttachCost(e, (Items.ItemCatalog.Wood.FullPath, BedWoodCost));
        var id = Jobs.Post(JobKind.BedBuild, origin, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        _bedOccupied.Add(origin);
        _bedOccupied.Add(foot);
        RebuildMapView();
        return true;
    }

    // Post a BedDeconstruct job against a built bed. Job carries a fresh
    // Decon marker that BedSystem ticks; the Bed entity stays in _bedMap
    // until completion, so cancel leaves the bed standing.
    public bool TryPostBedDeconstructJob(TilePos origin)
    {
        if (!_bedMap.ContainsKey(origin)) return false;
        if (Jobs.HasTile(origin)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = origin, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.BedDeconstruct, origin, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    private bool IsBedTileFree(TilePos t)
    {
        if (!Map.InBounds(t)) return false;
        if (Map.IsBorder(t.X, t.Y)) return false;
        if (Map.GetWall(t) != WallType.None) return false;
        if (_doorMap.ContainsKey(t)) return false;
        if (_lampMap.ContainsKey(t)) return false;
        if (_trees.ContainsKey(t)) return false;
        if (_bedOccupied.Contains(t)) return false;
        if (_urBoardOccupied.Contains(t)) return false;
        if (_stoveOccupied.Contains(t)) return false;
        if (_sandbagOccupied.Contains(t)) return false;
        return true;
    }

    // ─── Stove placement / decon ─────────────────────────────────────
    public bool TryPlaceStoveBlueprint(TilePos origin, StoveOrientation orientation)
    {
        foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
        {
            if (!IsBedTileFree(t)) return false;
            if (Jobs.HasTile(t)) return false;
        }
        var standing = StoveOrientations.StandingTile(origin, orientation);
        if (!IsBedTileFree(standing)) return false;
        if (Jobs.HasTile(standing)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new StoveBlueprint { Origin = origin, Orientation = orientation, ProgressSec = 0f });
        World.BlueprintCostOps.AttachCost(e, (Items.ItemCatalog.Wood.FullPath, StoveWoodCost));
        var id = Jobs.Post(JobKind.StoveBuild, origin, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
            _stoveOccupied.Add(t);
        _stoveOccupied.Add(standing);
        RebuildMapView();
        return true;
    }

    public bool TryPostStoveDeconstructJob(TilePos origin)
    {
        if (!_stoveMap.ContainsKey(origin)) return false;
        if (Jobs.HasTile(origin)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = origin, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.StoveDeconstruct, origin, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    public bool CanPlaceStove(TilePos origin, StoveOrientation orientation)
    {
        foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
            if (!IsBedTileFree(t)) return false;
        var standing = StoveOrientations.StandingTile(origin, orientation);
        return IsBedTileFree(standing);
    }

    public IReadOnlyDictionary<TilePos, Entity> StoveMap => _stoveMap;
    public const int StoveWoodCost = 40;

    // Drop an ItemPile of (path, count) on the given tile. Used by
    // CookSystem when a meal finishes, by harness scenarios for stocking
    // ingredients, and anything else that needs a runtime drop.
    public void SpawnItemPile(TilePos tile, string itemPath, int count)
    {
        if (count <= 0) return;
        var e = Store.CreateEntity();
        e.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
        e.AddComponent(new ItemPile { Tile = tile, Count = count, ItemPath = itemPath });
    }

    // Cook callback: consume up to `wanted` items of `itemPath` from any
    // ItemPile at the given tile. Returns how many were actually consumed.
    public int TryConsumeFromPile(TilePos tile, string itemPath, int wanted)
    {
        if (wanted <= 0) return 0;
        int taken = 0;
        // Index lookup of the tile's stacks instead of scanning every item.
        _itemIndex.GetEntitiesAt(tile, _tileEntScratch);
        Entity? toDelete = null;
        foreach (var id in _tileEntScratch)
        {
            if (taken >= wanted) break;
            if (!Store.TryGetEntityById(id, out var ent) || !ent.HasComponent<ItemPile>()) continue;
            ref var p = ref ent.GetComponent<ItemPile>();
            if (p.ItemPath != itemPath) continue;
            // Don't consume a stack a hauler has already claimed — deleting
            // it would leave that haul job pointing at a dead entity.
            if (ent.HasComponent<HaulReserved>()) continue;
            int can = Math.Min(p.Count, wanted - taken);
            p.Count -= can;
            taken += can;
            if (p.Count <= 0) toDelete = ent;
        }
        if (toDelete is Entity te) { _itemIndex.OnEntityGone(te.Id); te.DeleteEntity(); }
        return taken;
    }

    // Reused scratch for per-tile item lookups (sim thread, non-nested).
    private readonly List<int> _tileEntScratch = new();

    // Find the nearest tile holding a matching ItemPile reachable via
    // simple Manhattan distance. Returns null if nothing matches.
    public TilePos? FindNearestItemPile(TilePos from, string itemPath)
    {
        // Index-backed: spirals out by chunk instead of scanning every
        // pile. Indexed piles always have Count > 0 (drained piles are
        // deleted), so no count filter is needed here.
        return _itemIndex.TryGetNearest(from, itemPath, out var tile) ? tile : null;
    }

    // Harness shortcut: skip blueprint+build and drop a finished stove.
    public void InstantPlaceStove(TilePos origin, StoveOrientation orientation)
    {
        if (!CanPlaceStove(origin, orientation)) return;
        var e = Store.CreateEntity();
        e.AddComponent(new Stove
        {
            Origin = origin,
            Orientation = orientation,
            CookProgressTicks = 0f,
            CurrentBillIndex = -1,
            ActiveCookEntityId = 0,
        });
        e.AddComponent(new BillsBoard { Bills = new List<Bill>() });
        foreach (var t in StoveOrientations.BodyTiles(origin, orientation))
            _stoveOccupied.Add(t);
        _stoveOccupied.Add(StoveOrientations.StandingTile(origin, orientation));
        _stoveMap[origin] = e;
        RebuildMapView();
    }

    // Exposed so the BedDesignator preview can ask the sim whether a
    // candidate (origin, orientation) is legal without round-tripping a
    // command. Cheap — just dictionary/set lookups on the sim thread's
    // current state; the renderer reads it from the snapshot path which
    // already takes a stable view.
    public bool CanPlaceBed(TilePos origin, BedOrientation orientation)
    {
        var foot = BedOrientations.Foot(origin, orientation);
        if (origin == foot) return false;
        return IsBedTileFree(origin) && IsBedTileFree(foot);
    }

    public IReadOnlyDictionary<TilePos, Entity> BedMap => _bedMap;
    public IReadOnlyDictionary<TilePos, Entity> UrBoardMap => _urBoardMap;

    // Reserve the same tile as the bed pipeline — IsBedTileFree already
    // covers the common rejections (border, wall, door, lamp, tree, other
    // furniture). The board is 1x1 so no foot offset.
    public bool TryPlaceUrBoardBlueprint(TilePos tile)
    {
        if (!IsBedTileFree(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new UrBoardBlueprint { Tile = tile, ProgressSec = 0f });
        World.BlueprintCostOps.AttachCost(e, (Items.ItemCatalog.Wood.FullPath, UrBoardWoodCost));
        var id = Jobs.Post(JobKind.UrBoardBuild, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        _urBoardOccupied.Add(tile);
        RebuildMapView();
        return true;
    }

    // Harness shortcut: drop a finished Ur board directly, no blueprint
    // or job. Mirrors InstantPlaceLamp.
    public bool InstantPlaceUrBoard(TilePos tile)
    {
        if (!IsBedTileFree(tile)) return false;
        var e = Store.CreateEntity();
        e.AddComponent(new UrBoard { Tile = tile });
        _urBoardMap[tile] = e;
        _urBoardOccupied.Add(tile);
        RebuildMapView();
        return true;
    }

    public bool TryPostUrBoardDeconstructJob(TilePos tile)
    {
        if (!_urBoardMap.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.UrBoardDeconstruct, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    public bool CanPlaceUrBoard(TilePos tile) => IsBedTileFree(tile);

    // ─── Sandbag placement / decon ───────────────────────────────────
    // 1x1, walkable-but-slow low cover. Same tile-free rejection as the
    // bed/board pipeline (border, wall, door, lamp, tree, other furniture).
    public IReadOnlyDictionary<TilePos, Entity> SandbagMap => _sandbagMap;

    public bool TryPlaceSandbagBlueprint(TilePos tile)
    {
        if (!IsBedTileFree(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new SandbagBlueprint { Tile = tile, ProgressSec = 0f });
        World.BlueprintCostOps.AttachCost(e, (Items.ItemCatalog.Wood.FullPath, SandbagWoodCost));
        var id = Jobs.Post(JobKind.SandbagBuild, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        _sandbagOccupied.Add(tile);
        RebuildMapView();
        return true;
    }

    // Harness shortcut: drop a finished sandbag directly, no blueprint.
    public bool InstantPlaceSandbag(TilePos tile)
    {
        if (!IsBedTileFree(tile)) return false;
        var e = Store.CreateEntity();
        e.AddComponent(new Sandbag { Tile = tile });
        _sandbagMap[tile] = e;
        _sandbagOccupied.Add(tile);
        RebuildMapView();
        return true;
    }

    public bool TryPostSandbagDeconstructJob(TilePos tile)
    {
        if (!_sandbagMap.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Decon { Tile = tile, ProgressSec = 0f });
        var id = Jobs.Post(JobKind.SandbagDeconstruct, tile, e);
        if (id.IsNone)
        {
            e.DeleteEntity();
            return false;
        }
        return true;
    }

    public bool CanPlaceSandbag(TilePos tile) => IsBedTileFree(tile);

    // === Ur board seat reservation ===
    // DummyController calls this when a tired-for-fun pawn wants to sit
    // at a board. Picks the nearest board with an open slot for `role`
    // and returns the seat tile to walk to. Player seats are the 4
    // cardinal-adjacent walkable tiles; spectator slots clump in the
    // SpectatorRadius Chebyshev ring beyond.
    private static readonly (int dx, int dy)[] PlayerSeatOffsets = new (int, int)[]
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    public bool TryReserveUrSeat(Entity pawn, RecreationRole role, out int boardEntityId, out TilePos boardTile, out TilePos seatTile)
    {
        boardEntityId = 0;
        boardTile = default;
        seatTile = default;

        if (!pawn.HasComponent<WorldPos>()) return false;
        var pp = pawn.GetComponent<WorldPos>();
        var here = new TilePos((int)pp.X, (int)pp.Y);

        // Snapshot occupied seats this tick so two pawns in the same
        // plan loop don't grab the same tile.
        Entity bestBoard = default;
        TilePos bestSeat = default;
        int bestDist = int.MaxValue;

        foreach (var kv in _urBoardMap)
        {
            var board = kv.Value;
            int bid = board.Id;
            var btile = kv.Key;

            int players = _urBoardPlayers.TryGetValue(bid, out var p) ? p : 0;
            var seats = _urBoardSeats.TryGetValue(bid, out var s) ? s : null;
            int totalSeated = seats?.Count ?? 0;

            if (role == RecreationRole.Player)
            {
                if (players >= RecreationSystem.PlayerSeats) continue;
                if (!TryPickFreePlayerSeat(btile, seats, out var ps)) continue;
                int d = Math.Abs(ps.X - here.X) + Math.Abs(ps.Y - here.Y);
                if (d < bestDist) { bestDist = d; bestBoard = board; bestSeat = ps; }
            }
            else
            {
                // Spectating requires at least 1 player at the board.
                if (players < 1) continue;
                int specSlots = totalSeated - players;
                if (specSlots >= RecreationSystem.MaxSpectators) continue;
                if (!TryPickFreeSpectatorSeat(btile, seats, here, out var ss)) continue;
                int d = Math.Abs(ss.X - here.X) + Math.Abs(ss.Y - here.Y);
                if (d < bestDist) { bestDist = d; bestBoard = board; bestSeat = ss; }
            }
        }
        if (bestDist == int.MaxValue) return false;

        boardEntityId = bestBoard.Id;
        boardTile = bestBoard.GetComponent<UrBoard>().Tile;
        seatTile = bestSeat;
        if (!_urBoardSeats.TryGetValue(boardEntityId, out var live))
        {
            live = new HashSet<int>();
            _urBoardSeats[boardEntityId] = live;
        }
        live.Add(MakeSeatKey(seatTile));
        if (role == RecreationRole.Player)
        {
            _urBoardPlayers.TryGetValue(boardEntityId, out var cur);
            _urBoardPlayers[boardEntityId] = cur + 1;
        }
        return true;
    }

    // Preference-aware seat picker for the recreation phase. Ur preference
    // tries Player first, then falls back to Spectator at the same/any
    // board that already has a player. Spectating goes straight to a
    // spectator slot.
    public bool TryReserveRecreation(Entity pawn, RecreationKind preferred, out int boardEntityId, out TilePos boardTile, out TilePos seatTile, out RecreationRole role)
    {
        boardEntityId = 0; boardTile = default; seatTile = default; role = RecreationRole.Player;
        if (preferred == RecreationKind.Ur)
        {
            if (TryReserveUrSeat(pawn, RecreationRole.Player, out boardEntityId, out boardTile, out seatTile))
            {
                role = RecreationRole.Player;
                return true;
            }
            if (TryReserveUrSeat(pawn, RecreationRole.Spectator, out boardEntityId, out boardTile, out seatTile))
            {
                role = RecreationRole.Spectator;
                return true;
            }
            return false;
        }
        // Spectating preference.
        if (TryReserveUrSeat(pawn, RecreationRole.Spectator, out boardEntityId, out boardTile, out seatTile))
        {
            role = RecreationRole.Spectator;
            return true;
        }
        return false;
    }

    public void ReleaseUrSeat(int boardEntityId, TilePos seatTile, RecreationRole role)
    {
        if (boardEntityId == 0) return;
        if (_urBoardSeats.TryGetValue(boardEntityId, out var seats))
        {
            seats.Remove(MakeSeatKey(seatTile));
            if (seats.Count == 0) _urBoardSeats.Remove(boardEntityId);
        }
        if (role == RecreationRole.Player)
        {
            if (_urBoardPlayers.TryGetValue(boardEntityId, out var cur))
            {
                int next = cur - 1;
                if (next <= 0) _urBoardPlayers.Remove(boardEntityId);
                else _urBoardPlayers[boardEntityId] = next;
            }
        }
    }

    // Per-board player count for the renderer / info panel.
    public int UrBoardPlayerCount(int boardEntityId)
        => _urBoardPlayers.TryGetValue(boardEntityId, out var p) ? p : 0;

    public int UrBoardSpectatorCount(int boardEntityId)
    {
        int total = _urBoardSeats.TryGetValue(boardEntityId, out var s) ? s.Count : 0;
        return total - UrBoardPlayerCount(boardEntityId);
    }

    // Encode (tile.X, tile.Y) into a single int hash for the seat set.
    // Map size is bounded so X * MapSize + Y fits.
    private int MakeSeatKey(TilePos t) => t.X * SimConstants.MapSize + t.Y;

    private bool TryPickFreePlayerSeat(TilePos board, HashSet<int>? seats, out TilePos tile)
    {
        tile = default;
        var view = MapView;
        foreach (var (dx, dy) in PlayerSeatOffsets)
        {
            var t = new TilePos(board.X + dx, board.Y + dy);
            if (!view.Walkable(t)) continue;
            if (view.HasFurniture(t)) continue;
            if (seats is not null && seats.Contains(MakeSeatKey(t))) continue;
            tile = t;
            return true;
        }
        return false;
    }

    private bool TryPickFreeSpectatorSeat(TilePos board, HashSet<int>? seats, TilePos from, out TilePos tile)
    {
        tile = default;
        var view = MapView;
        TilePos best = default;
        int bestDist = int.MaxValue;
        for (int dy = -RecreationSystem.SpectatorRadius; dy <= RecreationSystem.SpectatorRadius; dy++)
        {
            for (int dx = -RecreationSystem.SpectatorRadius; dx <= RecreationSystem.SpectatorRadius; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                // Player seats live in the 4 cardinal slots; spectators
                // get everything else inside the radius.
                if ((dx == 1 || dx == -1 || dx == 0) && (dy == 1 || dy == -1 || dy == 0)
                    && Math.Abs(dx) + Math.Abs(dy) == 1) continue;
                var t = new TilePos(board.X + dx, board.Y + dy);
                if (!view.Walkable(t)) continue;
                if (view.HasFurniture(t)) continue;
                if (seats is not null && seats.Contains(MakeSeatKey(t))) continue;
                int d = Math.Abs(t.X - from.X) + Math.Abs(t.Y - from.Y);
                if (d < bestDist) { bestDist = d; best = t; }
            }
        }
        if (bestDist == int.MaxValue) return false;
        tile = best;
        return true;
    }

    // Live pool of recreation kinds the 12h roll can pick. Ur is in iff
    // there's at least one board on the map. Spectating is in iff there's
    // an active game (>=1 player seated at any board) — without that the
    // spectate role has nowhere to go.
    private readonly List<RecreationKind> _availableKindsScratch = new();
    public IReadOnlyList<RecreationKind> GetAvailableRecreationKinds()
    {
        _availableKindsScratch.Clear();
        if (_urBoardMap.Count == 0) return _availableKindsScratch;
        _availableKindsScratch.Add(RecreationKind.Ur);
        bool anyActive = false;
        foreach (var kv in _urBoardPlayers)
        {
            if (kv.Value > 0) { anyActive = true; break; }
        }
        if (anyActive) _availableKindsScratch.Add(RecreationKind.Spectating);
        return _availableKindsScratch;
    }

    // Harness shortcut: stamp roof bytes directly across a rect. Skips
    // the chunked build-job pipeline used by PaintRoofRect.
    public void InstantPaintRoofRect(TilePos a, TilePos b)
    {
        int w = Map.Width, h = Map.Height;
        EnsureRoofArrays(w, h);
        int xmin = Math.Min(a.X, b.X), xmax = Math.Max(a.X, b.X);
        int ymin = Math.Min(a.Y, b.Y), ymax = Math.Max(a.Y, b.Y);
        bool any = false;
        for (int y = Math.Max(0, ymin); y <= Math.Min(h - 1, ymax); y++)
        {
            int row = y * w;
            for (int x = Math.Max(0, xmin); x <= Math.Min(w - 1, xmax); x++)
            {
                int idx = row + x;
                if (_roofTiles[idx] == 0) { _roofTiles[idx] = 1; any = true; }
            }
        }
        if (any) { RoofVersion++; LightVersion++; }
    }

    // Harness/debug shortcut: jump world time to an absolute second on
    // the world clock. Forces sun recompute next tick.
    public void SetWorldTime(double seconds)
    {
        _worldTimeSec = seconds < 0 ? 0 : seconds;
        _sunDirty = true;
    }

    // Harness shortcut: stamp a finished stone wall directly. Skips the
    // blueprint + build job path. Wipes a tree on the tile first so the
    // visual harness can drop walls onto procgen tiles without staging.
    // Returns false if the tile is still occupied by something un-clearable.
    public bool InstantPlaceWall(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.GetWall(tile) != WallType.None) return false;
        if (_doorMap.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;
        if (_trees.TryGetValue(tile, out var tree))
        {
            _trees.Remove(tile);
            tree.DeleteEntity();
        }
        lock (_mapLock)
        {
            Map.SetWall(tile, WallType.Stone);
            _wallLayerDirty = true;
            _playerWalls.Add(tile);
        }
        RefreshDoorOrientationsAround(tile);
        RebuildMapView();
        // Wall blocks LOS for lamp light; relight (instant-place
        // bypasses the job pipeline that normally handles this).
        InvalidateLampBakesNear(tile);
        _lightDirty = true;
        return true;
    }

    public bool TryPlaceWallBlueprint(TilePos tile)
    {
        if (!Map.InBounds(tile)) return false;
        if (Map.GetWall(tile) != WallType.None) return false;
        if (_trees.ContainsKey(tile)) return false;
        if (_doorMap.ContainsKey(tile)) return false;
        if (Jobs.HasTile(tile)) return false;

        var e = Store.CreateEntity();
        e.AddComponent(new Blueprint { Tile = tile, ProgressSec = 0f });
        World.BlueprintCostOps.AttachCost(e, (Items.ItemCatalog.Wood.FullPath, WallWoodCost));
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
        int pinnedBpId = GetJobBlueprintId(job);
        Jobs.Complete(id);
        if (pinnedBpId != 0) _blueprintPriority.Remove(pinnedBpId);

        if (kind == JobKind.WallBuild)
        {
            entity.DeleteEntity();
            lock (_mapLock)
            {
                Map.SetWall(tile, WallType.Stone);
                _wallLayerDirty = true;
                _playerWalls.Add(tile);
            }
            RefreshDoorOrientationsAround(tile);
            RebuildMapView();
            // Wall now blocks LOS for nearby lamps; rebake those discs.
            InvalidateLampBakesNear(tile);
            _lightDirty = true;
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
            wood.AddComponent(new ItemPile { Tile = tile, Count = yield, ItemPath = ItemCatalog.Wood.FullPath });

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
                wood.AddComponent(new ItemPile { Tile = tile, Count = yield, ItemPath = ItemCatalog.Wood.FullPath });
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
            // Generic ItemPile drop — haul + stockpiles handle any ItemPile
            // by ItemDef, so the yield gets hauled/stored like anything else.
            // Only one crop kind today; harvest always yields carrots.
            string itemPath = Items.ItemCatalog.Carrot.FullPath;
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
                if (_roofTiles[idx] == 0) { _roofTiles[idx] = 1; any = true; _roofFlashes[tile] = RoofFlashSec; }
            }
            else
            {
                foreach (var t in tiles)
                {
                    if (!Map.InBounds(t)) continue;
                    int idx = t.Y * Map.Width + t.X;
                    if (_roofTiles[idx] == 0) { _roofTiles[idx] = 1; any = true; _roofFlashes[t] = RoofFlashSec; }
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
            EnsureLampBaked(tile);
            _lightDirty = true;
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
                DropLampBake(tile);
                _lightDirty = true;
            }
        }
        else if (kind == JobKind.BedBuild)
        {
            // Transmute the blueprint entity into the live bed at the
            // same orientation. Occupancy was already reserved when the
            // blueprint was posted, so the map view doesn't need a flip
            // here — just promote the entity.
            var bp = entity.GetComponent<BedBlueprint>();
            entity.RemoveComponent<BedBlueprint>();
            entity.AddComponent(new Bed { Origin = bp.Origin, Orientation = bp.Orientation });
            _bedMap[bp.Origin] = entity;
        }
        else if (kind == JobKind.BedDeconstruct)
        {
            // Decon marker is single-purpose; throw it away with the job.
            // The actual Bed entity lives in _bedMap; clear its footprint
            // from _bedOccupied so pathing reclaims both tiles.
            entity.DeleteEntity();
            if (_bedMap.TryGetValue(tile, out var bedEnt))
            {
                var bed = bedEnt.GetComponent<Bed>();
                var foot = BedOrientations.Foot(bed.Origin, bed.Orientation);
                ForgetBed(bedEnt);
                _bedMap.Remove(tile);
                _bedOccupied.Remove(tile);
                _bedOccupied.Remove(foot);
                bedEnt.DeleteEntity();
                RebuildMapView();
            }
        }
        else if (kind == JobKind.UrBoardBuild)
        {
            var bp = entity.GetComponent<UrBoardBlueprint>();
            entity.RemoveComponent<UrBoardBlueprint>();
            entity.AddComponent(new UrBoard { Tile = bp.Tile });
            _urBoardMap[bp.Tile] = entity;
        }
        else if (kind == JobKind.UrBoardDeconstruct)
        {
            entity.DeleteEntity();
            if (_urBoardMap.TryGetValue(tile, out var boardEnt))
            {
                ForgetUrBoard(boardEnt);
                _urBoardMap.Remove(tile);
                _urBoardOccupied.Remove(tile);
                boardEnt.DeleteEntity();
                RebuildMapView();
            }
        }
        else if (kind == JobKind.SandbagBuild)
        {
            var bp = entity.GetComponent<SandbagBlueprint>();
            entity.RemoveComponent<SandbagBlueprint>();
            entity.AddComponent(new Sandbag { Tile = bp.Tile });
            _sandbagMap[bp.Tile] = entity;
        }
        else if (kind == JobKind.SandbagDeconstruct)
        {
            entity.DeleteEntity();
            if (_sandbagMap.TryGetValue(tile, out var sbEnt))
            {
                _sandbagMap.Remove(tile);
                _sandbagOccupied.Remove(tile);
                sbEnt.DeleteEntity();
                RebuildMapView();
            }
        }
        else if (kind == JobKind.StoveBuild)
        {
            var bp = entity.GetComponent<StoveBlueprint>();
            entity.RemoveComponent<StoveBlueprint>();
            entity.AddComponent(new Stove
            {
                Origin = bp.Origin,
                Orientation = bp.Orientation,
                CookProgressTicks = 0f,
                CurrentBillIndex = -1,
                ActiveCookEntityId = 0,
            });
            entity.AddComponent(new BillsBoard { Bills = new List<Bill>() });
            _stoveMap[bp.Origin] = entity;
        }
        else if (kind == JobKind.StoveDeconstruct)
        {
            entity.DeleteEntity();
            if (_stoveMap.TryGetValue(tile, out var stoveEnt))
            {
                var stove = stoveEnt.GetComponent<Stove>();
                foreach (var t in StoveOrientations.BodyTiles(stove.Origin, stove.Orientation))
                    _stoveOccupied.Remove(t);
                _stoveOccupied.Remove(StoveOrientations.StandingTile(stove.Origin, stove.Orientation));
                _stoveMap.Remove(tile);
                stoveEnt.DeleteEntity();
                RebuildMapView();
            }
        }
        else if (kind == JobKind.Cook)
        {
            // Cook completion is handled by CookSystem (it consumes
            // ingredients + spawns the meal item). Here we just close
            // the job. Job entity is the stove — don't delete it.
        }
        else if (kind == JobKind.Deconstruct)
        {
            // Decon marker entity is single-purpose; throw it away.
            entity.DeleteEntity();
            lock (_mapLock)
            {
                // Wall layer only — terrain underneath stays put.
                Map.SetWall(tile, WallType.None);
                _wallLayerDirty = true;
                _playerWalls.Remove(tile);
            }
            RefreshDoorOrientationsAround(tile);
            if (WallDeconWoodReturn > 0)
            {
                var wood = Store.CreateEntity();
                wood.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
                wood.AddComponent(new ItemPile { Tile = tile, Count = WallDeconWoodReturn, ItemPath = ItemCatalog.Wood.FullPath });
            }
            RebuildMapView();
            // Wall gone = light can flow through that tile again; rebake
            // nearby lamps that were previously LOS-blocked by it.
            InvalidateLampBakesNear(tile);
            _lightDirty = true;
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
            InvalidateLampBakesNear(tile);
            _lightDirty = true;
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
                wood.AddComponent(new ItemPile { Tile = tile, Count = WallDeconWoodReturn, ItemPath = ItemCatalog.Wood.FullPath });
            }
            RebuildMapView();
            // Door removed = a room boundary changed; recompute enclosure.
            _roomsDirty = true;
            // Door gone = light can flow through that tile again. Relight.
            InvalidateLampBakesNear(tile);
            _lightDirty = true;
        }
    }

    public void CancelJob(JobId id)
    {
        var job = Jobs.Get(id);
        if (job is null) return;
        var tile = job.Tile;
        var entity = job.Entity;
        var kind = job.Kind;
        int pinnedBpId = GetJobBlueprintId(job);
        Jobs.Cancel(id);
        // Build-kind cancellation throws the blueprint entity away; clear
        // the pin so a future blueprint reusing the same id (Friflo recycles)
        // doesn't inherit an old pawn assignment. Haul cancellation leaves
        // the blueprint alone, so don't touch the pin in that case.
        if (pinnedBpId != 0
            && (kind == JobKind.WallBuild
                || kind == JobKind.FloorBuild
                || kind == JobKind.DoorBuild
                || kind == JobKind.BedBuild
                || kind == JobKind.UrBoardBuild
                || kind == JobKind.StoveBuild))
        {
            _blueprintPriority.Remove(pinnedBpId);
        }

        if (kind == JobKind.WallBuild)
        {
            // Refund whatever wood already sits in the blueprint cost,
            // then throw the marker away.
            RefundDeposits(entity, tile);
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
            RefundDeposits(entity, tile);
            entity.DeleteEntity();
        }
        else if (kind == JobKind.DoorBuild)
        {
            RefundDeposits(entity, tile);
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
        else if (kind == JobKind.BedBuild)
        {
            // Bed blueprint cancelled — refund deposited wood at the head
            // tile, drop the blueprint entity, free both footprint tiles
            // (no Bed exists yet, so _bedMap untouched).
            var bp = entity.GetComponent<BedBlueprint>();
            var foot = BedOrientations.Foot(bp.Origin, bp.Orientation);
            RefundDeposits(entity, bp.Origin);
            _bedOccupied.Remove(bp.Origin);
            _bedOccupied.Remove(foot);
            entity.DeleteEntity();
            RebuildMapView();
        }
        else if (kind == JobKind.BedDeconstruct)
        {
            // Decon cancelled — bed stays; throw the marker away.
            entity.DeleteEntity();
        }
        else if (kind == JobKind.UrBoardBuild)
        {
            var bp = entity.GetComponent<UrBoardBlueprint>();
            RefundDeposits(entity, bp.Tile);
            _urBoardOccupied.Remove(bp.Tile);
            entity.DeleteEntity();
            RebuildMapView();
        }
        else if (kind == JobKind.UrBoardDeconstruct)
        {
            entity.DeleteEntity();
        }
        else if (kind == JobKind.SandbagBuild)
        {
            var bp = entity.GetComponent<SandbagBlueprint>();
            RefundDeposits(entity, bp.Tile);
            _sandbagOccupied.Remove(bp.Tile);
            entity.DeleteEntity();
            RebuildMapView();
        }
        else if (kind == JobKind.SandbagDeconstruct)
        {
            entity.DeleteEntity();
        }
        else if (kind == JobKind.StoveBuild)
        {
            var bp = entity.GetComponent<StoveBlueprint>();
            RefundDeposits(entity, bp.Origin);
            foreach (var t in StoveOrientations.BodyTiles(bp.Origin, bp.Orientation))
                _stoveOccupied.Remove(t);
            _stoveOccupied.Remove(StoveOrientations.StandingTile(bp.Origin, bp.Orientation));
            entity.DeleteEntity();
            RebuildMapView();
        }
        else if (kind == JobKind.StoveDeconstruct)
        {
            entity.DeleteEntity();
        }
        else if (kind == JobKind.Cook)
        {
            // Cook cancelled mid-flight (master spec: only drafted
            // interrupt cancels). Reset stove progress + active cook.
            if (entity.HasComponent<Stove>())
            {
                ref var stove = ref entity.GetComponent<Stove>();
                stove.CookProgressTicks = 0f;
                stove.CurrentBillIndex = -1;
                stove.ActiveCookEntityId = 0;
            }
        }
        else if (kind == JobKind.DoorDeconstruct)
        {
            // Door stays; throw the marker away.
            entity.DeleteEntity();
        }
        else if (kind == JobKind.Haul)
        {
            // Wood entity survives the cancel — only the routing intent
            // is dropped. Release the dest cell so another haul can use it,
            // and undo any blueprint reservation this haul was holding so
            // the blueprint can attract a fresh hauler next tick.
            if (entity.HasComponent<HaulPayload>())
            {
                var hp = entity.GetComponent<HaulPayload>();
                _reservedHaulDests.Remove(hp.DestTile);
                if (hp.BlueprintEntityId != 0
                    && Store.TryGetEntityById(hp.BlueprintEntityId, out var bpEnt)
                    && bpEnt.HasComponent<BlueprintCost>())
                {
                    int amt = entity.HasComponent<ItemPile>() ? entity.GetComponent<ItemPile>().Count : hp.Count;
                    int release = amt < hp.Count ? amt : hp.Count;
                    BlueprintCostOps.ReleaseReservation(bpEnt, hp.ItemPath, release);
                }
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
        MarkLampChunksDirty(tile);
        _lightDirty = true;
    }

    // Recolor a built lamp + re-stamp the light layer so the new tint
    // shows up immediately. No-op when the new color matches.
    public void SetLampColor(TilePos tile, LightColor color)
    {
        if (!_lampMap.TryGetValue(tile, out var lampEnt)) return;
        ref var lamp = ref lampEnt.GetComponent<Lamp>();
        if (lamp.Color.Equals(color)) return;
        lamp.Color = color;
        if (lamp.PoweredOn)
        {
            MarkLampChunksDirty(tile);
            _lightDirty = true;
        }
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
        World.BlueprintCostOps.AttachCost(e, (Items.ItemCatalog.Wood.FullPath, FloorWoodCost));
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
            World.BlueprintCostOps.AttachCost(bp, (Items.ItemCatalog.Wood.FullPath, DoorWoodCost));

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
        World.BlueprintCostOps.AttachCost(e, (Items.ItemCatalog.Wood.FullPath, DoorWoodCost));
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

    public bool TryFindBestHaulDest(TilePos source, ItemDef def, int countToMove, out TilePos dest, out int stockpileId)
    {
        // Per-tile counts of the same item kind, for the merge-bias.
        var countAt = new Dictionary<TilePos, int>();
        string path = def.FullPath;
        (_itemPileQ ??= Store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity ent) =>
        {
            if (p.ItemPath == path) countAt[p.Tile] = p.Count;
        });
        return TryFindBestHaulDest(source, def, countToMove, countAt, out dest, out stockpileId);
    }

    // Walks the player's zones and picks the best cell that accepts the item.
    // A cell is valid if it's empty OR holds the same item with room for
    // countToMove. Two-pass merge bias: pass 1 considers only partial-stack
    // tiles across ALL piles (so a colonist hauling from outside tops off
    // an existing pile instead of starting a fresh one — even if the
    // partial sits in a lower-priority zone). Pass 2 falls back to empty
    // tiles only if no merge target exists anywhere. Within each pass:
    // priority > existing count > distance.
    //
    // woodAt is a tile→count index built by the caller (HaulSystem reuses
    // one allocation per tick across all candidates). Source tile is
    // forced to existing=0 so a pile sitting on a stockpile cell doesn't
    // pick itself as the best merge target.
    public bool TryFindBestHaulDest(TilePos source, ItemDef def, int countToMove,
        Dictionary<TilePos, int> woodAt, out TilePos dest, out int stockpileId)
    {
        dest = default;
        stockpileId = 0;

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
                    int existing = (t != source && woodAt.TryGetValue(t, out var c)) ? c : 0;
                    if (mergePass && existing <= 0) continue;
                    if (!mergePass && existing > 0) continue;
                    if (existing > 0 && existing + countToMove > def.MaxStack) continue;
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
            if (job is not null)
            {
                // Clear any "Prioritize Haul" pin (keyed by the item entity)
                // before completing — this path bypasses CompleteJob.
                int pinId = GetJobBlueprintId(job);
                if (pinId != 0) _blueprintPriority.Remove(pinId);
                Jobs.Complete(c.PrimaryJobId);
            }
        }
        // Resolve blueprint dropoff (Carrying may name a blueprint that
        // got cancelled / completed between pickup and delivery; in that
        // case fall through to the normal Wood-spawn path).
        Entity bpEnt = default;
        bool depositingToBlueprint = false;
        if (c.BlueprintEntityId != 0
            && Store.TryGetEntityById(c.BlueprintEntityId, out bpEnt)
            && bpEnt.HasComponent<BlueprintCost>())
        {
            depositingToBlueprint = true;
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

                int leftover = slot.Count;
                if (depositingToBlueprint && !string.IsNullOrEmpty(slot.ItemPath))
                {
                    leftover = BlueprintCostOps.Deposit(bpEnt, slot.ItemPath, slot.Count);
                }
                if (leftover <= 0)
                {
                    // Fully consumed by the deposit — drop the entity and
                    // skip the HaulReserved removal so playback doesn't
                    // touch a deleted entity.
                    cb.DeleteEntity(e.Id);
                    continue;
                }
                if (e.HasComponent<HaulReserved>()) cb.RemoveComponent<HaulReserved>(e.Id);
                cb.AddComponent(e.Id, new ItemPile { Tile = dropTile, Count = leftover, ItemPath = slot.ItemPath });
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
            if (!slotEnt.HasComponent<ItemPile>()) slotEnt.AddComponent(new ItemPile { Tile = here, Count = slot.Count, ItemPath = slot.ItemPath });
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

    // ─── Equipment / persistent inventory ────────────────────────────────

    // Order a specific colonist to walk to a dropped equippable pile and
    // equip one unit of it. Validates the target is a real, equippable
    // ItemPile; the actual pickup + slot insertion happens in
    // DummyController when the pawn arrives.
    public void SetEquipOrder(int pawnId, int itemEntityId)
    {
        if (!Store.TryGetEntityById(pawnId, out var pawn)) return;
        if (!pawn.HasComponent<Wanderer>()) return;
        if (!Store.TryGetEntityById(itemEntityId, out var item)) return;
        if (!item.HasComponent<ItemPile>()) return;
        var pile = item.GetComponent<ItemPile>();
        if (pile.Count <= 0) return;
        if (!ItemCatalog.ItemsByPath.TryGetValue(pile.ItemPath, out var def) || !def.Equippable) return;
        pawn.AddComponent(new EquipOrder
        {
            ItemTile = pile.Tile,
            ItemPath = pile.ItemPath,
            ItemEntityId = itemEntityId,
        });
    }

    // RMB "Prioritize Haul": ensure a stockpile haul exists for this item
    // and pin it to the chosen colonist (same pin path as blueprints,
    // keyed by the item entity id). No-op if there's no valid stockpile.
    public void PrioritizeHaulForPawn(int itemEntityId, int pawnEntityId)
    {
        if (!Store.TryGetEntityById(itemEntityId, out var item)) return;
        if (!item.HasComponent<ItemPile>() || item.HasComponent<Forbidden>()) return;
        var pile = item.GetComponent<ItemPile>();
        if (pile.Count <= 0) return;
        if (!ItemCatalog.ItemsByPath.TryGetValue(pile.ItemPath, out var def)) return;

        if (!item.HasComponent<HaulReserved>())
        {
            if (!TryFindBestHaulDest(pile.Tile, def, pile.Count, out var dest, out var stockpileId)) return;
            item.AddComponent(new HaulPayload
            {
                DestTile = dest,
                StockpileId = stockpileId,
                ItemPath = def.FullPath,
                Count = pile.Count,
            });
            var id = Jobs.Post(JobKind.Haul, pile.Tile, item);
            if (id.IsNone) { item.RemoveComponent<HaulPayload>(); return; }
            item.AddComponent(new HaulReserved { JobId = id });
            ReserveHaulDest(dest);
        }
        PrioritizeBlueprintForPawn(itemEntityId, pawnEntityId);
    }

    // Order a colonist to fetch up to `requestedCount` units of a dropped
    // pile into general inventory. int.MaxValue = pick up all (capacity
    // permitting). Actual amount is clamped to carry capacity on arrival.
    public void SetPickupOrder(int pawnId, int itemEntityId, int requestedCount)
    {
        if (requestedCount <= 0) return;
        if (!Store.TryGetEntityById(pawnId, out var pawn)) return;
        if (!pawn.HasComponent<Wanderer>()) return;
        if (!Store.TryGetEntityById(itemEntityId, out var item)) return;
        if (!item.HasComponent<ItemPile>()) return;
        var pile = item.GetComponent<ItemPile>();
        if (pile.Count <= 0) return;
        pawn.AddComponent(new PickupOrder
        {
            ItemTile = pile.Tile,
            ItemPath = pile.ItemPath,
            ItemEntityId = itemEntityId,
            RequestedCount = requestedCount,
        });
    }

    public const float MeleeBruiseSeverity = 3f; // bare-fist bruise damage in HP

    // Order a drafted colonist to melee-attack another pawn.
    public void SetMeleeTarget(int attackerId, int targetId)
    {
        if (attackerId == targetId) return;
        if (!Store.TryGetEntityById(attackerId, out var a) || !a.HasComponent<Drafted>()) return;
        if (!Store.TryGetEntityById(targetId, out var t) || !t.HasComponent<Health>()) return;
        // Ordering an attack on an already-downed target = a deliberate
        // finishing move that runs until they're dead.
        bool finishOff = t.GetComponent<Health>().Unconscious;
        a.AddComponent(new MeleeTarget { TargetEntityId = targetId, LastHitTick = 0, FinishOff = finishOff });
    }

    // Land one melee hit on a random outer (punchable) part. If the
    // attacker has a weapon equipped, the strike uses one of that weapon's
    // attacks (e.g. the trinket's Cut/Stab); otherwise it's a bare-fist
    // bruise.
    public void MeleeStrike(int attackerId, int targetId)
    {
        var parts = StruggleGame.Sim.Bodies.BodyTree.PunchableParts;
        if (parts.Count == 0) return;
        var part = parts[_spawnRng.Next(parts.Count)];

        var kind = StruggleGame.Sim.Bodies.ConditionKind.Bruise;
        float sev = MeleeBruiseSeverity;
        if (Store.TryGetEntityById(attackerId, out var a) && a.HasComponent<Inventory>())
        {
            var inv = a.GetComponent<Inventory>();
            if (inv.Equipped is not null)
                foreach (var eq in inv.Equipped)
                    if (Items.ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def) && def.IsWeapon)
                    {
                        var atk = def.MeleeAttacks[_spawnRng.Next(def.MeleeAttacks.Length)];
                        kind = atk.Kind; sev = atk.Severity;
                        break;
                    }
        }
        ApplyInjury(targetId, part, kind, sev);
    }

    // === Ranged weapons ===

    private readonly List<Entity> _projScratch = new();

    // The RangedSpec of the first ranged weapon in a pawn's equipped slots.
    public static bool TryGetEquippedRangedSpec(Entity ent, out Items.RangedSpec spec)
    {
        spec = null!;
        if (!ent.HasComponent<Inventory>()) return false;
        var inv = ent.GetComponent<Inventory>();
        if (inv.Equipped is null) return false;
        foreach (var eq in inv.Equipped)
            if (Items.ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def) && def.Ranged is not null)
            {
                spec = def.Ranged;
                return true;
            }
        return false;
    }

    // Order a drafted colonist with a ranged weapon to fire on a target.
    public void SetFireTarget(int shooterId, int targetId)
    {
        if (shooterId == targetId) return;
        if (!Store.TryGetEntityById(shooterId, out var s) || !s.HasComponent<Drafted>()) return;
        if (!s.HasComponent<RangedCombat>()) return;
        if (!Store.TryGetEntityById(targetId, out var t) || !t.HasComponent<Health>()) return;
        ref var rc = ref s.GetComponent<RangedCombat>();
        rc.TargetEntityId = targetId;
        rc.AutoTarget = false; // player-forced — hold this target, don't auto-drop
        rc.BurstRemaining = 0;
        // Ordering fire on an already-downed pawn = a deliberate finish-off
        // that keeps shooting until death (mirrors melee).
        rc.FinishOff = t.GetComponent<Health>().Unconscious;
    }

    // Draft action bar: manually reload. Returns any partial mag to
    // inventory first (no rounds lost), then refills from a matching ammo
    // stack — honoring a locked ammo type if one is set.
    public void ManualReload(int pawnId)
    {
        if (!Store.TryGetEntityById(pawnId, out var p)) return;
        if (!p.HasComponent<RangedCombat>()) return;
        if (!TryGetEquippedRangedSpec(p, out var spec)) return;
        UnloadMagazine(pawnId); // bank the partial mag (rounds returned, MagCount -> 0)
        // Two-phase: only START the timed reload here — the rounds are inserted
        // when it COMPLETES (the controller's per-tick CompleteReload), so the
        // mag shows empty during the reload and interrupting it grants nothing.
        if (!p.HasComponent<Inventory>()) return;
        var inv = p.GetComponent<Inventory>();
        if (inv.Items is null) return;
        string? preferred = p.GetComponent<RangedCombat>().PreferredAmmoPath;
        bool hasAmmo = false;
        foreach (var stk in inv.Items)
            if (stk.Count > 0
                && Items.ItemCatalog.ItemsByPath.TryGetValue(stk.ItemPath, out var d) && d.Ammo is not null
                && d.Ammo.CategoryPath == spec.AmmoCategoryPath
                && (preferred is null || stk.ItemPath == preferred))
            { hasAmmo = true; break; }
        if (!hasAmmo) return;
        ref var rc = ref p.GetComponent<RangedCombat>();
        rc.Reloading = true;
        rc.NextActionTick = Tick + spec.ReloadTicks;
        rc.BurstRemaining = 0;
    }

    // Empty the magazine, returning its rounds to inventory.
    public void UnloadMagazine(int pawnId)
    {
        if (!Store.TryGetEntityById(pawnId, out var p) || !p.HasComponent<RangedCombat>()) return;
        ref var rc = ref p.GetComponent<RangedCombat>();
        if (rc.MagCount > 0 && rc.LoadedAmmoPath is not null)
            AddToInventory(p, rc.LoadedAmmoPath, rc.MagCount);
        rc.MagCount = 0;
        rc.LoadedAmmoPath = null;
        rc.Reloading = false;
        rc.BurstRemaining = 0;
    }

    // Reload-button RMB menu: lock the auto-reload ammo type and force an
    // immediate swap to it.
    public void SetPreferredAmmoAndReload(int pawnId, string ammoPath)
    {
        if (!Store.TryGetEntityById(pawnId, out var p) || !p.HasComponent<RangedCombat>()) return;
        { ref var rc = ref p.GetComponent<RangedCombat>(); rc.PreferredAmmoPath = ammoPath; }
        ManualReload(pawnId);
    }

    private static void AddToInventory(Entity p, string itemPath, int count)
    {
        if (count <= 0) return;
        if (!p.HasComponent<Inventory>())
            p.AddComponent(new Inventory { Items = new List<InventoryStack>(), Equipped = new List<EquippedItemSlot>() });
        ref var inv = ref p.GetComponent<Inventory>();
        inv.Items ??= new List<InventoryStack>();
        for (int i = 0; i < inv.Items.Count; i++)
            if (inv.Items[i].ItemPath == itemPath)
            {
                var s = inv.Items[i]; s.Count += count; inv.Items[i] = s; return;
            }
        inv.Items.Add(new InventoryStack { ItemPath = itemPath, Count = count });
    }

    // Draft action bar: change a pawn's selected fire mode.
    public void SetFireMode(int pawnId, Items.FireMode mode)
    {
        if (!Store.TryGetEntityById(pawnId, out var p) || !p.HasComponent<RangedCombat>()) return;
        ref var rc = ref p.GetComponent<RangedCombat>();
        rc.Mode = mode;
        rc.BurstRemaining = 0; // re-arm cleanly under the new mode
    }

    // Draft action bar: set which body region the pawn aims for.
    public void SetTargetArea(int pawnId, Items.TargetArea area)
    {
        if (!Store.TryGetEntityById(pawnId, out var p) || !p.HasComponent<RangedCombat>()) return;
        p.GetComponent<RangedCombat>().TargetArea = area;
    }

    // Order a drafted doctor (with medicine) to tend / stabilize a patient.
    public void SetTreatmentTarget(int doctorId, int patientId, bool stabilize, bool removeBullet = false)
    {
        if (doctorId == patientId) return;
        if (!Store.TryGetEntityById(doctorId, out var d) || !d.HasComponent<Drafted>()) return;
        if (!Store.TryGetEntityById(patientId, out var pt) || !pt.HasComponent<Health>()) return;
        // A treatment order supersedes any combat order.
        if (d.HasComponent<RangedCombat>()) { ref var rc = ref d.GetComponent<RangedCombat>(); rc.TargetEntityId = 0; rc.BurstRemaining = 0; }
        if (d.HasComponent<MeleeTarget>()) d.RemoveComponent<MeleeTarget>();
        if (d.HasComponent<TreatmentTarget>())
        {
            ref var tt = ref d.GetComponent<TreatmentTarget>();
            tt.PatientEntityId = patientId; tt.Stabilize = stabilize; tt.RemoveBullet = removeBullet; tt.WorkUntilTick = 0;
        }
        else
        {
            d.AddComponent(new TreatmentTarget { PatientEntityId = patientId, Stabilize = stabilize, RemoveBullet = removeBullet });
        }
    }

    // Player queues (or un-queues) surgery on a specific lodged wound via the
    // health panel; a drafted surgeon is assigned later by RMB on the patient.
    public void RequestBulletRemoval(int patientId, string partId)
    {
        if (!Store.TryGetEntityById(patientId, out var pt) || !pt.HasComponent<Health>()) return;
        var injuries = pt.GetComponent<Health>().Injuries;
        if (injuries is null) return;
        for (int i = 0; i < injuries.Count; i++)
        {
            var w = injuries[i];
            if (w.PartId == partId && w.Lodged && w.Kind == StruggleGame.Sim.Bodies.ConditionKind.Gunshot)
            {
                w.RemovalRequested = !w.RemovalRequested;
                injuries[i] = w;
            }
        }
    }

    // Queue every lodged round for removal at once (the "Remove bullets" shortcut).
    public void RequestAllBulletRemovals(int patientId)
    {
        if (!Store.TryGetEntityById(patientId, out var pt) || !pt.HasComponent<Health>()) return;
        var injuries = pt.GetComponent<Health>().Injuries;
        if (injuries is null) return;
        for (int i = 0; i < injuries.Count; i++)
        {
            var w = injuries[i];
            if (w.Lodged && w.Kind == StruggleGame.Sim.Bodies.ConditionKind.Gunshot && !w.RemovalRequested)
            {
                w.RemovalRequested = true;
                injuries[i] = w;
            }
        }
    }

    // Any lodged round queued for removal?
    public bool HasRemovableBullet(Entity p)
    {
        if (!p.HasComponent<Health>()) return false;
        var inj = p.GetComponent<Health>().Injuries;
        if (inj is null) return false;
        foreach (var w in inj)
            if (w.Lodged && w.RemovalRequested && w.Kind == StruggleGame.Sim.Bodies.ConditionKind.Gunshot) return true;
        return false;
    }

    // Surgery: pull every queued lodged round. A tended wound comes out clean
    // (heals fully from here); an untended one doubles its severity + bleeding.
    public void ApplyBulletRemoval(Entity patient)
    {
        if (!patient.HasComponent<Health>()) return;
        var injuries = patient.GetComponent<Health>().Injuries;
        if (injuries is null) return;
        // One round per surgery (each takes a tend's worth of work).
        for (int i = 0; i < injuries.Count; i++)
        {
            var w = injuries[i];
            if (!(w.Lodged && w.RemovalRequested && w.Kind == StruggleGame.Sim.Bodies.ConditionKind.Gunshot)) continue;
            if (!w.Tended)
            {
                w.Severity *= 2f;
                w.BleedMult = (w.BleedMult > 0f ? w.BleedMult : 1f) * 2f;
            }
            w.Lodged = false;
            w.HealFloor = 0f;          // can now heal fully
            w.RemovalRequested = false;
            injuries[i] = w;
            break;
        }
        ref var h = ref patient.GetComponent<Health>();
        HealthSystem.Recompute(ref h);
    }

    public void SetAimMode(int pawnId, Items.AimMode mode)
    {
        if (!Store.TryGetEntityById(pawnId, out var p) || !p.HasComponent<RangedCombat>()) return;
        p.GetComponent<RangedCombat>().AimMode = mode;
    }

    // Does this pawn have a wound the given mode could still help? Tend wants an
    // untended non-permanent wound; stabilize wants an actively-bleeding wound
    // that isn't tended/stabilized yet.
    public bool HasTreatableWounds(Entity p, bool stabilize)
    {
        if (!p.HasComponent<Health>()) return false;
        var inj = p.GetComponent<Health>().Injuries;
        if (inj is null) return false;
        foreach (var w in inj)
        {
            if (StruggleGame.Sim.Bodies.BodyTree.IsPermanent(w.Kind)) continue;
            if (stabilize)
            {
                if (!w.Tended && !w.Stabilized && StruggleGame.Sim.Bodies.BodyTree.BleedRate(w.Kind, w.Severity) > 0f) return true;
            }
            else if (!w.Tended) return true;
        }
        return false;
    }

    private readonly List<int> _treatScratch = new();

    // Apply a tend (or stabilize) over the pawn's wounds, worst-first, until the
    // mode's severity budget runs out — the wound that exhausts it is still
    // treated fully (whole wounds only).
    public void ApplyTreatment(Entity patient, bool stabilize, float quality)
    {
        if (!patient.HasComponent<Health>()) return;
        var injuries = patient.GetComponent<Health>().Injuries; // mutating its elements persists (ref type)
        if (injuries is null) return;

        _treatScratch.Clear();
        for (int i = 0; i < injuries.Count; i++)
        {
            var w = injuries[i];
            if (StruggleGame.Sim.Bodies.BodyTree.IsPermanent(w.Kind)) continue;
            if (stabilize)
            {
                if (w.Tended || w.Stabilized) continue;
                if (StruggleGame.Sim.Bodies.BodyTree.BleedRate(w.Kind, w.Severity) <= 0f) continue;
            }
            else if (w.Tended) continue;
            _treatScratch.Add(i);
        }
        // Worst wounds first.
        _treatScratch.Sort((a, b) => injuries[b].Severity.CompareTo(injuries[a].Severity));

        float budget = stabilize ? SimConstants.StabilizeSeverityBudget : SimConstants.TendSeverityBudget;
        foreach (int idx in _treatScratch)
        {
            if (budget <= 0f) break;
            var w = injuries[idx];
            if (stabilize) { w.Stabilized = true; }
            else { w.Tended = true; w.TendQuality = quality; w.Stabilized = false; }
            injuries[idx] = w;
            budget -= w.Severity; // the wound that crosses 0 is still treated fully
        }
    }

    // Line of sight for bullets: walls block, doorways don't (door/cover
    // occlusion is future work). Bresenham, endpoints excluded.
    public bool RangedLosClear(int x0, int y0, int x1, int y1)
    {
        if (x0 == x1 && y0 == y1) return true;
        int dx = Math.Abs(x1 - x0), dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;
        int x = x0, y = y0;
        while (true)
        {
            int e2 = 2 * err;
            bool stepX = e2 > -dy;
            bool stepY = e2 < dx;
            if (stepX && stepY)
            {
                // Diagonal step: don't let the sight line squeeze past a wall
                // CORNER. If either cell flanking the diagonal is a wall, a real
                // round would clip it — so it's blocked (the pawn must lean to
                // open the lane instead of grazing the corner).
                if (Map.GetWall(x + sx, y) != WallType.None || Map.GetWall(x, y + sy) != WallType.None)
                    return false;
                err -= dy; err += dx; x += sx; y += sy;
            }
            else if (stepX) { err -= dy; x += sx; }
            else { err += dx; y += sy; }
            if (x == x1 && y == y1) return true;
            if (Map.GetWall(x, y) != WallType.None) return false;
        }
    }

    // Drain bullet-spawn requests posted by DummyController this tick. The hit
    // is resolved NOW (hitscan along the ballistic arc, height-aware so cover
    // still works); the spawned Projectile is a cosmetic tracer flying that
    // same arc to the locked impact point, applying the wound on arrival.
    // Rebuild the shared pawn-occupancy set once per tick (after movement,
    // before build systems). BuildableSystem reads it to gate construction.
    private void RebuildOccupiedPawnTiles()
    {
        OccupiedPawnTiles.Clear();
        (_occupiedPawnsQ ??= Store.Query<WorldPos, Wanderer>()).ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            OccupiedPawnTiles.Add(new TilePos((int)p.X, (int)p.Y));
        });
    }

    // Rebuild the colonist-LOS threat field (throttled). For each conscious
    // colonist, mark every tile within ColonistLosRadius it has a clear line
    // to. Publishes a FRESH set (atomic ref swap) so any path worker holding
    // the previous reference keeps reading valid immutable data.
    private void RebuildColonistLosTiles()
    {
        var set = new HashSet<TilePos>();
        int r = ColonistLosRadius;
        int w = Map.Width, h = Map.Height;
        (_colonistLosQ ??= Store.Query<WorldPos, Wanderer, Health>()).ForEachEntity((ref WorldPos p, ref Wanderer _, ref Health hp, Entity e) =>
        {
            if (e.HasComponent<Enemy>()) return;     // enemies aren't the threat
            if (hp.Unconscious) return;              // downed colonists can't see
            int cx = (int)p.X, cy = (int)p.Y;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx * dx + dy * dy > r * r) continue;
                int tx = cx + dx, ty = cy + dy;
                if ((uint)tx >= (uint)w || (uint)ty >= (uint)h) continue;
                var t = new TilePos(tx, ty);
                if (set.Contains(t)) continue;       // another colonist already sees it
                if (RangedLosClear(cx, cy, tx, ty)) set.Add(t);
            }
        });
        _colonistLosTiles = set;
    }

    private void SpawnPendingProjectiles()
    {
        if (_dummies.PendingProjectiles.Count == 0) return;
        GatherProjPawns();
        foreach (var ps in _dummies.PendingProjectiles)
        {
            float ddx = ps.ToX - ps.FromX, ddy = ps.ToY - ps.FromY;
            float ang = MathF.Atan2(ddy, ddx);
            // Ballistic launch: fire from muzzle height, pick a vertical
            // velocity that lands the round at torso-aim height by the time it
            // covers the horizontal distance (gravity arcs it). For fast rifle
            // rounds the flight is brief, so the arc is nearly flat.
            float dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
            float flight = MathF.Max(dist / MathF.Max(ps.Speed, 0.01f), 1e-3f);
            float vVel = (ps.ToHeight - SimConstants.MuzzleHeight) / flight
                       + 0.5f * SimConstants.ProjectileGravity * flight;
            // Trace the arc instantly: first wall/sandbag/pawn it crosses is the
            // locked impact. Nothing in the way → it lands at the aim point.
            ResolveArcImpact(ps.FromX, ps.FromY, ps.ToX, ps.ToY, vVel, ps.Speed,
                ps.ShooterEntityId, out float hitX, out float hitY, out float hitH,
                out int hitId, out bool hitWall);
            var e = Store.CreateEntity();
            e.AddComponent(new Projectile
            {
                X = ps.FromX, Y = ps.FromY, OriginX = ps.FromX, OriginY = ps.FromY,
                ToX = hitX, ToY = hitY, HitHeight = hitH,
                Height = SimConstants.MuzzleHeight, VertVel = vVel,
                Speed = ps.Speed, ShooterEntityId = ps.ShooterEntityId,
                ResolvedHitId = hitId, HitWall = hitWall, AmmoPath = ps.AmmoPath, Angle = ang,
            });
        }
        _dummies.PendingProjectiles.Clear();
    }

    // Snapshot every live pawn's hitbox (position + stance-adjusted height) for
    // this tick's hit resolution. Downed pawns lie prone (short box); pawns in
    // cover crouch below the sandbag or relocate to their lean peek cell.
    private void GatherProjPawns()
    {
        _projPawns.Clear();
        (_worldPosHealthQ ??= Store.Query<WorldPos, Health>()).ForEachEntity((ref WorldPos wp, ref Health h, Entity pe) =>
        {
            float px = wp.X, py = wp.Y, bh, hitR = ProjectileHitRadius;
            if (h.Unconscious)
            {
                bh = SimConstants.DownedBodyHeight;
            }
            else
            {
                var stance = CoverStance.None;
                bool leaning = false;
                float pkx = px, pky = py;
                if (pe.HasComponent<RangedCombat>())
                {
                    var rc = pe.GetComponent<RangedCombat>();
                    stance = rc.Stance; leaning = rc.Leaning; pkx = rc.PeekX; pky = rc.PeekY;
                }
                if (stance == CoverStance.None && pe.HasComponent<Wanderer>()
                    && pe.GetComponent<Wanderer>().Crouched)
                    stance = CoverStance.Tucked;

                switch (stance)
                {
                    case CoverStance.Tucked:
                        bh = leaning ? SimConstants.PawnBodyHeight : SimConstants.CrouchBodyHeight;
                        break;
                    case CoverStance.Popped:
                        bh = SimConstants.PawnBodyHeight;
                        if (leaning)
                        {
                            // Body leans only part-way to the peek cell (matches
                            // the rendered lean), so the hitbox sits where the
                            // sprite is — not fully out on the next tile.
                            px += (pkx - px) * SimConstants.LeanPeekFraction;
                            py += (pky - py) * SimConstants.LeanPeekFraction;
                            hitR = ProjectileHitRadius * LeanHitFraction;
                        }
                        break;
                    default:
                        bh = SimConstants.PawnBodyHeight;
                        break;
                }
            }
            _projPawns.Add((pe.Id, px, py, bh, hitR));
        });
    }

    // Hitscan along the ballistic arc from the muzzle to the aim point. Finds
    // the nearest blocker — first wall/low-sandbag (one sampled scan) vs nearest
    // pawn within hit radius (one projection pass over pawns). O(samples+pawns)
    // per shot, not O(samples×pawns). Height-aware so cover behaves the same.
    private void ResolveArcImpact(float fromX, float fromY, float aimX, float aimY,
        float vVel, float speed, int shooterId,
        out float hitX, out float hitY, out float hitH, out int hitId, out bool hitWall)
    {
        float ddx = aimX - fromX, ddy = aimY - fromY;
        float dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
        float g = SimConstants.ProjectileGravity, muzzle = SimConstants.MuzzleHeight;
        float invSpeed = 1f / MathF.Max(speed, 0.01f);
        // Arc height at a fraction f along the line (f in [0,1]).
        float HeightAt(float f) { float t = dist * f * invSpeed; return muzzle + vVel * t - 0.5f * g * t * t; }

        if (dist < 1e-4f)
        {
            hitX = aimX; hitY = aimY; hitH = MathF.Max(0f, muzzle); hitId = 0; hitWall = false; return;
        }

        // 1) First wall / low sandbag along the line (single sampled scan).
        float blockFrac = 1f; bool blocked = false;
        bool haveSandbags = _sandbagMap.Count > 0;
        int samples = Math.Max(2, (int)(dist / 0.2f));
        for (int k = 1; k <= samples; k++)
        {
            float f = (float)k / samples;
            int cx = (int)(fromX + ddx * f), cy = (int)(fromY + ddy * f);
            if (Map.GetWall(cx, cy) != WallType.None) { blockFrac = f; blocked = true; break; }
            if (haveSandbags && HeightAt(f) <= SimConstants.SandbagCoverHeight
                && _sandbagMap.ContainsKey(new TilePos(cx, cy)))
            { blockFrac = f; blocked = true; break; }
        }

        // 2) Nearest pawn whose body the line crosses at a strikeable height,
        //    closer than the wall block. Hit radius is per-pawn (leaning pawns
        //    present a smaller sliver).
        // Anti-friendly-fire: a round clears the 3x3 around the muzzle, so any
        // pawn (ally OR enemy) hugging the shooter isn't hit by its own fire —
        // the bullet only starts connecting past the immediate cluster.
        int muzzleTX = (int)fromX, muzzleTY = (int)fromY;
        float bestFrac = float.MaxValue; int pawn = 0; float pawnX = 0, pawnY = 0;
        foreach (var (id, ppx, ppy, bodyH, hitR) in _projPawns)
        {
            if (id == shooterId) continue;
            if (Math.Abs((int)ppx - muzzleTX) <= 1 && Math.Abs((int)ppy - muzzleTY) <= 1) continue;
            float proj = ((ppx - fromX) * ddx + (ppy - fromY) * ddy) / (dist * dist);
            float u = Math.Clamp(proj, 0f, 1f);
            if (u >= bestFrac || u > blockFrac) continue;
            float qx = fromX + ddx * u, qy = fromY + ddy * u;
            float gx = qx - ppx, gy = qy - ppy;
            if (gx * gx + gy * gy > hitR * hitR) continue;
            float h = HeightAt(u);
            if (h < 0f || h > bodyH) continue; // underground / flew over
            bestFrac = u; pawn = id; pawnX = ppx; pawnY = ppy;
        }

        if (pawn != 0)
        {
            // Snap impact to the pawn's center so entry/exit straddle the body.
            hitX = pawnX; hitY = pawnY;
            hitH = MathF.Max(0f, HeightAt(bestFrac));
            hitId = pawn; hitWall = false;
            return;
        }
        if (blocked)
        {
            hitX = fromX + ddx * blockFrac; hitY = fromY + ddy * blockFrac;
            hitH = MathF.Max(0f, HeightAt(blockFrac));
            hitId = 0; hitWall = true;
            return;
        }
        // Clean miss — the round zips past at the aim point.
        hitX = aimX; hitY = aimY;
        hitH = MathF.Max(0f, HeightAt(1f));
        hitId = 0; hitWall = false;
    }

    // Per-bullet hit radius around a pawn (tiles).
    private const float ProjectileHitRadius = SimConstants.ProjectileHitRadius;
    private const float LeanHitFraction = SimConstants.LeanHitFraction;
    private readonly List<(int Id, float X, float Y, float BodyH, float HitR)> _projPawns = new();

    // Animate the cosmetic tracers. The hit was already resolved at fire time
    // (ResolveArcImpact); each bullet just flies its locked arc to the impact
    // point and applies the wound the tick it ARRIVES — no live collision.
    private void StepProjectiles(float dt)
    {
        _projScratch.Clear();
        (_projectileQ ??= Store.Query<Projectile>()).ForEachEntity((ref Projectile _, Entity e) => _projScratch.Add(e));
        if (_projScratch.Count == 0) return;

        foreach (var e in _projScratch)
        {
            ref var pr = ref e.GetComponent<Projectile>();
            // Impacted last tick (drawn there for one frame) — retire it now.
            if (pr.Arrived) { e.DeleteEntity(); continue; }

            float ddx = pr.ToX - pr.X, ddy = pr.ToY - pr.Y;
            float dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (dist > 1e-4f) pr.Angle = MathF.Atan2(ddy, ddx);
            float step = pr.Speed * dt;
            bool reaching = dist <= step || dist < 1e-4f;

            if (!reaching)
            {
                pr.X += ddx / dist * step;
                pr.Y += ddy / dist * step;
                pr.Height += pr.VertVel * dt;
                pr.VertVel -= SimConstants.ProjectileGravity * dt; // gravity arc
                continue;
            }

            // Arrived at the locked impact — snap on, apply the outcome.
            pr.X = pr.ToX; pr.Y = pr.ToY; pr.Height = pr.HitHeight; pr.Arrived = true;
            float ih = pr.HitHeight;
            bool hitPawn = pr.ResolvedHitId != 0
                && Store.TryGetEntityById(pr.ResolvedHitId, out var vt) && vt.HasComponent<Health>();
            if (hitPawn)
            {
                bool passThrough = ResolveProjectileHit(pr.ResolvedHitId, pr.AmmoPath, ih);
                float cx = MathF.Cos(pr.Angle), cy = MathF.Sin(pr.Angle);
                // Entry + exit sprays sit at the wound height so they appear on
                // the body (torso/head), not down at the pawn's feet.
                _bloodImpacts.Add((pr.ToX - cx * 0.45f, pr.ToY - cy * 0.45f, ih, pr.Angle + MathF.PI, 0.55f, false, BloodImpactSec));
                if (passThrough)
                    _bloodImpacts.Add((pr.ToX + cx * 0.45f, pr.ToY + cy * 0.45f, ih, pr.Angle, 1.0f, false, BloodImpactSec));
            }
            else if (pr.HitWall)
            {
                // Struck a wall/sandbag — dust at the impact height (wall face).
                _bloodImpacts.Add((pr.ToX, pr.ToY, ih, pr.Angle, 0.6f, true, BloodImpactSec));
            }
            else
            {
                // Clean miss (or victim died mid-flight) — kick dust off the
                // ground where the round skips past, not floating at chest height.
                _bloodImpacts.Add((pr.ToX, pr.ToY, 0f, pr.Angle, 0.6f, true, BloodImpactSec));
            }
        }
    }

    private static readonly string[] _lowerParts = { "LegL", "FootL", "LegR", "FootR" };
    private static readonly string[] _midParts = { "Torso", "ArmL", "HandL", "ArmR", "HandR" };
    private static readonly string[] _upperParts = { "Head", "Neck", "EyeL", "EyeR", "EarL", "EarR" };

    // Resolve a bullet striking a pawn. Body part is chosen from the round's
    // height at impact (low→legs, mid→torso/arms, high→head). Returns true if
    // the round passed clean through (→ exit-wound spray); false if it lodged.
    private bool ResolveProjectileHit(int targetId, string ammoPath, float impactHeight)
    {
        if (!Store.TryGetEntityById(targetId, out var t) || !t.HasComponent<Health>()) return false;
        string part;
        if (t.GetComponent<Health>().Unconscious)
        {
            // Prone: the whole body is laid flat at the impact height, so a hit
            // can land on any part — height doesn't map to a region.
            var all = StruggleGame.Sim.Bodies.BodyTree.PunchableParts;
            part = all[_spawnRng.Next(all.Count)];
        }
        else
        {
            // Standing: pick the body region from the round's height at impact.
            float bodyH = SimConstants.PawnBodyHeight;
            var pool = impactHeight < bodyH * 0.45f ? _lowerParts
                     : impactHeight < bodyH * 0.85f ? _midParts
                     : _upperParts;
            part = pool[_spawnRng.Next(pool.Length)];
        }
        var kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot;
        float dmg = 12f, pen = 6f, penBlunt = 0f;
        string? caliber = null;
        if (Items.ItemCatalog.ItemsByPath.TryGetValue(ammoPath, out var def) && def.Ammo is not null)
        {
            kind = def.Ammo.InjuryKind;
            dmg = def.Ammo.Damage;
            pen = def.Ammo.PenSharp;
            penBlunt = def.Ammo.PenBlunt;
            caliber = def.DisplayName;
        }
        // High-penetration rounds (AP, ~12 mmRHA) punch through; expanding
        // ones (HP, ~3) lodge; FMJ (~6) is in between.
        float passChance = Math.Clamp(pen / 20f, 0.05f, 0.9f);
        bool passThrough = _spawnRng.NextDouble() < passChance;

        // Armor: if the struck part is covered, the round either deflects
        // (sharp pen ≤ armor → becomes a blunt bruise) or penetrates with the
        // damage bled down by the armor it chewed through.
        if (TryGetArmorCovering(t, part, out var armor))
        {
            if (pen <= armor.ArmorSharp)
            {
                kind = StruggleGame.Sim.Bodies.ConditionKind.Bruise;
                caliber = null;
                dmg *= Math.Clamp((penBlunt - armor.ArmorBlunt) / MathF.Max(penBlunt, 0.01f), 0.1f, 1f);
                passThrough = false; // bounced — no exit wound
            }
            else
            {
                dmg *= Math.Clamp((pen - armor.ArmorSharp) / pen, 0.05f, 1f);
            }
        }
        ApplyInjury(targetId, part, kind, dmg, caliber, lodged: !passThrough);
        if (t.HasComponent<Combat>())
        { ref var tc = ref t.GetComponent<Combat>(); tc.FlinchTick = Tick; }
        return passThrough;
    }

    // First worn armor on the pawn that covers the given body part.
    private static bool TryGetArmorCovering(Entity pawn, string part, out Items.ArmorSpec armor)
    {
        armor = null!;
        if (!pawn.HasComponent<Inventory>()) return false;
        var inv = pawn.GetComponent<Inventory>();
        if (inv.Equipped is null) return false;
        foreach (var eq in inv.Equipped)
            if (Items.ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var d) && d.Armor is not null)
                foreach (var c in d.Armor.Covers)
                    if (c == part) { armor = d.Armor; return true; }
        return false;
    }

    private void AgeBloodImpacts(float dt)
    {
        for (int i = _bloodImpacts.Count - 1; i >= 0; i--)
        {
            var b = _bloodImpacts[i];
            b.Sec -= dt;
            if (b.Sec <= 0f) _bloodImpacts.RemoveAt(i);
            else _bloodImpacts[i] = b;
        }
    }

    // Dump a downed colonist's equipped weapon + carried inventory onto
    // free tiles around them. (Clothes would be excluded — none exist yet.)
    public void DropDownedItems(int pawnId)
    {
        if (!Store.TryGetEntityById(pawnId, out var p)) return;
        if (!p.HasComponent<Inventory>() || !p.HasComponent<WorldPos>()) return;
        var wp = p.GetComponent<WorldPos>();
        var tile = new TilePos((int)wp.X, (int)wp.Y);
        ref var inv = ref p.GetComponent<Inventory>();
        if (inv.Equipped is not null)
            foreach (var eq in inv.Equipped) DropAtFreeAdjacent(tile, eq.ItemPath, eq.Count);
        if (inv.Items is not null)
            foreach (var it in inv.Items) DropAtFreeAdjacent(tile, it.ItemPath, it.Count);
        inv.Equipped?.Clear();
        inv.Items?.Clear();
    }

    private static readonly (int dx, int dy)[] _adjacent8 =
        { (1,0),(-1,0),(0,1),(0,-1),(1,1),(1,-1),(-1,1),(-1,-1) };

    private void DropAtFreeAdjacent(TilePos from, string path, int count)
    {
        var view = MapView;
        TilePos target = from;
        bool foundWalkable = false;
        foreach (var (dx, dy) in _adjacent8)
        {
            var t = new TilePos(from.X + dx, from.Y + dy);
            if (!view.Walkable(t)) continue;
            if (!_itemIndex.AnyItemAt(t)) { target = t; foundWalkable = true; break; } // prefer empty
            if (!foundWalkable) { target = t; foundWalkable = true; }
        }
        SpawnItemPile(target, path, count);
    }

    // Death: the colonist's consciousness hit zero. Drop any remaining
    // gear, leave a corpse holding their data, and remove the pawn.
    public void KillColonist(int pawnId)
    {
        if (!Store.TryGetEntityById(pawnId, out var p)) return;
        DropDownedItems(pawnId); // covers instant death with no down phase
        var tile = p.HasComponent<WorldPos>()
            ? new TilePos((int)p.GetComponent<WorldPos>().X, (int)p.GetComponent<WorldPos>().Y)
            : default;
        Health corpseHealth = default;
        if (p.HasComponent<Health>())
        {
            var h = p.GetComponent<Health>();
            corpseHealth = h;
            corpseHealth.Injuries = h.Injuries is not null ? new List<PartInjury>(h.Injuries) : new List<PartInjury>();
        }
        var c = Store.CreateEntity();
        c.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
        // The corpse is a real dropped item (selectable / haulable) that
        // also carries the colonist's data for resurrection.
        c.AddComponent(new ItemPile { Tile = tile, Count = 1, ItemPath = Items.ItemCatalog.Corpse.FullPath });
        c.AddComponent(new Corpse { Tile = tile, Health = corpseHealth, Name = $"Colonist {pawnId}" });
        RemoveDummy(pawnId);
    }

    // Drip blood on a tile — grows an existing puddle there or starts a
    // new one. Cosmetic; persists (no cleaning yet).
    public void SpawnBloodPuddle(TilePos tile)
    {
        if (_bloodPuddleMap.TryGetValue(tile, out var fe))
        {
            ref var bp = ref fe.GetComponent<BloodPuddle>();
            bp.Amount = Math.Min(1f, bp.Amount + 0.25f);
            return;
        }
        var ne = Store.CreateEntity();
        ne.AddComponent(new BloodPuddle { Tile = tile, Amount = 0.4f });
        ne.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
        _bloodPuddleMap[tile] = ne;
    }

    // Debug/gameplay: add a condition to one of a colonist's body parts
    // and recompute capacities immediately.
    // Debug: overwrite a pawn's injuries with a fixed demo set covering each
    // status-icon case (heavy bleed, light bleed, tended, stabilized).
    public void DebugHealthDemo(int pawnId)
    {
        if (!Store.TryGetEntityById(pawnId, out var pawn) || !pawn.HasComponent<Health>()) return;
        ref var h = ref pawn.GetComponent<Health>();
        h.Injuries = new List<PartInjury>
        {
            new PartInjury { PartId = "WholeBody", Kind = StruggleGame.Sim.Bodies.ConditionKind.Sickness, Severity = 30f },
            new PartInjury { PartId = "Torso", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 11f, Caliber = "7.62x51mm NATO", Lodged = true, HealFloor = 5.5f },
            new PartInjury { PartId = "ArmR", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 3f, Caliber = "9x19mm Parabellum" },
            new PartInjury { PartId = "ArmL", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 5f, Caliber = "9x19mm Parabellum", Tended = true, TendQuality = 0.75f },
            new PartInjury { PartId = "LegL", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 7f, Caliber = "5.56x45mm NATO", Stabilized = true },
            new PartInjury { PartId = "Head", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 4f, Caliber = "9x19mm Parabellum" },
            new PartInjury { PartId = "LegR", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 6f, Caliber = "5.56x45mm NATO", Lodged = true, HealFloor = 3f },
            new PartInjury { PartId = "ArmR", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 5f, Caliber = "7.62x51mm NATO", Tended = true, TendQuality = 0.75f },
            new PartInjury { PartId = "LegR", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 3f, Caliber = "9x19mm Parabellum" },
            new PartInjury { PartId = "Torso", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 8f, Caliber = "9x19mm Parabellum", Stabilized = true },
            new PartInjury { PartId = "ArmL", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 2f, Caliber = "9x19mm Parabellum" },
            new PartInjury { PartId = "Head", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 5f, Caliber = "7.62x51mm NATO", Tended = true, TendQuality = 0.75f },
            new PartInjury { PartId = "Torso", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 4f, Caliber = "5.56x45mm NATO" },
            new PartInjury { PartId = "ArmR", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 6f, Caliber = "5.56x45mm NATO", Lodged = true, HealFloor = 3f },
            new PartInjury { PartId = "LegL", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 3f, Caliber = "9x19mm Parabellum", Tended = true, TendQuality = 0.75f },
            new PartInjury { PartId = "LegR", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 7f, Caliber = "7.62x51mm NATO", Stabilized = true },
            new PartInjury { PartId = "Head", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 2f, Caliber = "5.56x45mm NATO" },
            new PartInjury { PartId = "Torso", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 9f, Caliber = "7.62x51mm NATO", Lodged = true, HealFloor = 4.5f },
            new PartInjury { PartId = "ArmL", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 4f, Caliber = "5.56x45mm NATO", Stabilized = true },
            new PartInjury { PartId = "LegR", Kind = StruggleGame.Sim.Bodies.ConditionKind.Gunshot, Severity = 5f, Caliber = "9x19mm Parabellum", Tended = true, TendQuality = 0.75f },
        };
    }

    public void ApplyInjury(int pawnId, string partId, StruggleGame.Sim.Bodies.ConditionKind kind, float severity, string? caliber = null, bool lodged = false)
    {
        if (!Store.TryGetEntityById(pawnId, out var pawn)) return;
        if (!pawn.HasComponent<Health>()) return;
        // "WholeBody" is a virtual part for body-wide conditions; everything
        // else must be a real body part.
        if (partId != "WholeBody" && !StruggleGame.Sim.Bodies.BodyTree.TryGet(partId, out _)) return;
        ref var h = ref pawn.GetComponent<Health>();
        h.Injuries ??= new List<PartInjury>();
        float sev = severity <= 0f ? 1f : severity;
        h.Injuries.Add(new PartInjury
        {
            PartId = partId,
            Kind = kind,
            // Severity is now damage in hit points — no upper clamp.
            Severity = sev,
            Caliber = caliber,
            Lodged = lodged,
            // Lodged gunshots stall at 50% until the round's removed.
            HealFloor = (lodged && kind == StruggleGame.Sim.Bodies.ConditionKind.Gunshot) ? sev * 0.5f : 0f,
        });
        HealthSystem.Recompute(ref h);
    }

    // Move an equipped item into the pawn's general inventory. Both share
    // the carry budget, so this is a pure reclassification — no weight
    // changes, the item just stops being "worn".
    public void ForceUnequip(int pawnId, int equipIndex)
    {
        if (!Store.TryGetEntityById(pawnId, out var pawn)) return;
        if (!pawn.HasComponent<Inventory>()) return;
        ref var inv = ref pawn.GetComponent<Inventory>();
        if (inv.Equipped is null || equipIndex < 0 || equipIndex >= inv.Equipped.Count) return;
        var slot = inv.Equipped[equipIndex];
        inv.Equipped.RemoveAt(equipIndex);
        inv.Items ??= new List<InventoryStack>();
        int existing = inv.Items.FindIndex(s => s.ItemPath == slot.ItemPath);
        if (existing >= 0)
        {
            var s = inv.Items[existing];
            s.Count += slot.Count;
            inv.Items[existing] = s;
        }
        else
        {
            inv.Items.Add(new InventoryStack { ItemPath = slot.ItemPath, Count = slot.Count });
        }
    }

    // Drop an equipped item on the ground at the pawn's feet.
    public void DropEquipped(int pawnId, int equipIndex)
    {
        if (!Store.TryGetEntityById(pawnId, out var pawn)) return;
        if (!pawn.HasComponent<Inventory>() || !pawn.HasComponent<WorldPos>()) return;
        ref var inv = ref pawn.GetComponent<Inventory>();
        if (inv.Equipped is null || equipIndex < 0 || equipIndex >= inv.Equipped.Count) return;
        var slot = inv.Equipped[equipIndex];
        inv.Equipped.RemoveAt(equipIndex);
        var wp = pawn.GetComponent<WorldPos>();
        SpawnItemPile(new TilePos((int)wp.X, (int)wp.Y), slot.ItemPath, slot.Count);
    }

    // Equip one unit of a general-inventory stack that's already on the
    // pawn (no walking — it's in their bag). No-op if not equippable.
    // Shares the carry budget, so total weight is unchanged.
    public void EquipFromInventory(int pawnId, int heldIndex)
    {
        if (!Store.TryGetEntityById(pawnId, out var pawn)) return;
        if (!pawn.HasComponent<Inventory>()) return;
        ref var inv = ref pawn.GetComponent<Inventory>();
        if (inv.Items is null || heldIndex < 0 || heldIndex >= inv.Items.Count) return;
        var stack = inv.Items[heldIndex];
        if (!ItemCatalog.ItemsByPath.TryGetValue(stack.ItemPath, out var def) || !def.Equippable) return;
        stack.Count -= 1;
        if (stack.Count <= 0) inv.Items.RemoveAt(heldIndex);
        else inv.Items[heldIndex] = stack;
        inv.Equipped ??= new List<EquippedItemSlot>();
        var slot = def.IsArmor ? EquipSlot.Apparel : EquipSlot.Generic;
        inv.Equipped.Add(new EquippedItemSlot { Slot = slot, ItemPath = stack.ItemPath, Count = 1 });
    }

    // Drop a general-inventory stack on the ground at the pawn's feet.
    public void DropHeldItem(int pawnId, int heldIndex)
    {
        if (!Store.TryGetEntityById(pawnId, out var pawn)) return;
        if (!pawn.HasComponent<Inventory>() || !pawn.HasComponent<WorldPos>()) return;
        ref var inv = ref pawn.GetComponent<Inventory>();
        if (inv.Items is null || heldIndex < 0 || heldIndex >= inv.Items.Count) return;
        var stack = inv.Items[heldIndex];
        inv.Items.RemoveAt(heldIndex);
        var wp = pawn.GetComponent<WorldPos>();
        SpawnItemPile(new TilePos((int)wp.X, (int)wp.Y), stack.ItemPath, stack.Count);
    }

    // ItemSpatialIndex feeders. Fire for every component add/remove in the
    // store; we filter to ItemPile (the one ground-item kind) and
    // HaulReserved. On add the component is present so we read its tile/
    // path; on remove the item has left the ground (picked up, consumed-
    // but-entity-kept, etc.) so we just drop it.
    private void OnItemComponentAdded(ComponentChanged c)
    {
        if (c.Type == typeof(HaulReserved)) { _itemIndex.OnReservedAdded(c.EntityId); return; }
        if (c.Type != typeof(ItemPile)) return;
        if (!Store.TryGetEntityById(c.EntityId, out var e)) return;
        if (e.HasComponent<ItemPile>())
        {
            var p = e.GetComponent<ItemPile>();
            _itemIndex.OnItemAdded(c.EntityId, p.Tile, p.ItemPath);
        }
    }

    private void OnItemComponentRemoved(ComponentChanged c)
    {
        if (c.Type == typeof(HaulReserved)) { _itemIndex.OnReservedRemoved(c.EntityId); return; }
        if (c.Type == typeof(ItemPile))
            _itemIndex.OnEntityGone(c.EntityId);
    }

    // Called by DummyController when it actually picks up an item from
    // the world. Removes the world-side ItemPile component so the renderer
    // stops drawing the stack; the entity itself stays alive on the
    // carrier until drop.
    public void OnHaulPickedUp(Entity carriedEntity, CommandBuffer cb)
    {
        if (carriedEntity.HasComponent<ItemPile>()) cb.RemoveComponent<ItemPile>(carriedEntity.Id);
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
                        if (hp.BlueprintEntityId != 0
                            && Store.TryGetEntityById(hp.BlueprintEntityId, out var bpEnt)
                            && bpEnt.HasComponent<BlueprintCost>())
                        {
                            int amt = ent.HasComponent<ItemPile>() ? ent.GetComponent<ItemPile>().Count : hp.Count;
                            int release = amt < hp.Count ? amt : hp.Count;
                            BlueprintCostOps.ReleaseReservation(bpEnt, hp.ItemPath, release);
                        }
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
    // logs ready" tooltip). Wood is now just an ItemPile of the wood path.
    public int WoodCountAtTile(TilePos tile)
    {
        int total = 0;
        string woodPath = ItemCatalog.Wood.FullPath;
        _itemIndex.GetEntitiesAt(tile, _tileEntScratch);
        foreach (var id in _tileEntScratch)
        {
            if (!Store.TryGetEntityById(id, out var ent) || !ent.HasComponent<ItemPile>()) continue;
            ref var p = ref ent.GetComponent<ItemPile>();
            if (p.ItemPath == woodPath) total += p.Count;
        }
        return total;
    }

    // Reused scratch for the merge pass.
    private readonly Dictionary<(TilePos Tile, string Path), Entity> _mergePileByKey = new();
    private readonly List<(int destId, int amt)> _mergeOpsScratch = new();
    private readonly List<Entity> _mergeDeletesScratch = new();

    // End-of-tick consolidator: any two unreserved piles of the same kind
    // on the same tile whose combined count fits in one stack collapse into
    // one entity. Keys on (Tile, ItemPath) so a carrot pile won't swallow a
    // wood pile on the same tile. Uses WoodMaxStack as a generic cap until
    // per-item stack sizes get pulled into ItemCatalog.
    private void MergeCoincidentItemPiles()
    {
        var byKey = _mergePileByKey; byKey.Clear();
        var mergeOps = _mergeOpsScratch; mergeOps.Clear();
        var deletes = _mergeDeletesScratch; deletes.Clear();
        (_itemPileQ ??= Store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity e) =>
        {
            if (e.HasComponent<HaulReserved>()) return;
            if (e.HasComponent<Corpse>()) return; // unique — never merge corpses
            var key = (p.Tile, p.ItemPath);
            if (byKey.TryGetValue(key, out var existing))
            {
                int existingCount = existing.GetComponent<ItemPile>().Count;
                int cap = Items.ItemCatalog.ItemsByPath.TryGetValue(p.ItemPath, out var mdef) ? mdef.MaxStack : WoodMaxStack;
                if (existingCount + p.Count <= cap)
                {
                    mergeOps.Add((existing.Id, p.Count));
                    deletes.Add(e);
                }
                return;
            }
            byKey[key] = e;
        });
        foreach (var (id, amt) in mergeOps)
        {
            if (Store.TryGetEntityById(id, out var dest))
            {
                ref var dp = ref dest.GetComponent<ItemPile>();
                dp.Count += amt;
            }
        }
        foreach (var e in deletes) { _itemIndex.OnEntityGone(e.Id); e.DeleteEntity(); }
    }

    // Reused scratch for the spill pass.
    private readonly Dictionary<TilePos, int> _spillPileCount = new();
    private readonly List<Entity> _spillExtras = new();
    private readonly HashSet<TilePos> _spillOccupied = new();

    // One stack per tile: when two piles can't fuse (combined > the stack
    // cap, so MergeCoincidentItemPiles left them), relocate the extra to
    // the nearest free walkable tile. Reserved piles (mid-haul) are left
    // alone. A relocate is delete + respawn so the item index's component
    // events keep it in sync.
    private void SpillCoincidentPiles()
    {
        _spillPileCount.Clear();
        _spillExtras.Clear();
        (_itemPileQ ??= Store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity e) =>
        {
            if (e.HasComponent<HaulReserved>()) return; // in flight, don't move
            if (e.HasComponent<Corpse>()) return;       // unique — never relocate/delete a corpse
            int c = _spillPileCount.GetValueOrDefault(p.Tile);
            _spillPileCount[p.Tile] = c + 1;
            if (c >= 1) _spillExtras.Add(e); // 2nd+ unreserved pile on this tile
        });
        if (_spillExtras.Count == 0) return;

        _spillOccupied.Clear();
        foreach (var t in _spillPileCount.Keys) _spillOccupied.Add(t);

        foreach (var e in _spillExtras)
        {
            if (!e.HasComponent<ItemPile>()) continue;
            var p = e.GetComponent<ItemPile>();
            if (!TryFindSpillTile(p.Tile, _spillOccupied, out var target)) continue; // nowhere safe; leave it
            _spillOccupied.Add(target);
            int count = p.Count;
            string path = p.ItemPath;
            _itemIndex.OnEntityGone(e.Id);
            e.DeleteEntity();
            SpawnItemPile(target, path, count);
        }
    }

    // Nearest walkable tile (spiral out) that isn't already holding a pile.
    private bool TryFindSpillTile(TilePos from, HashSet<TilePos> occupied, out TilePos result)
    {
        result = default;
        var view = MapView;
        for (int r = 1; r <= SpillSearchRadius; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue; // ring shell only
                    var t = new TilePos(from.X + dx, from.Y + dy);
                    if (occupied.Contains(t)) continue;
                    if (!view.Walkable(t)) continue;
                    result = t;
                    return true;
                }
        }
        return false;
    }

    private const int SpillSearchRadius = 8;

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
    // Drops every deposited unit on a blueprint back onto the world at
    // `tile` as the matching item entity, then zeroes Deposited so the
    // caller can safely delete the blueprint without double-refunding.
    // Reserved is left intact — cancelled in-flight hauls are handled by
    // their own cancel path which calls ReleaseReservation.
    private void RefundDeposits(Entity blueprint, TilePos tile)
    {
        if (!blueprint.HasComponent<BlueprintCost>()) return;
        ref var cost = ref blueprint.GetComponent<BlueprintCost>();
        var entries = cost.Entries;
        if (entries is null) return;
        string woodPath = Items.ItemCatalog.Wood.FullPath;
        for (int i = 0; i < entries.Length; i++)
        {
            int dep = entries[i].Deposited;
            if (dep <= 0) continue;
            if (entries[i].ItemPath == woodPath)
            {
                SpawnWoodPile(tile, dep);
            }
            entries[i].Deposited = 0;
        }
    }

    // Thin wrapper: wood is just an ItemPile of the wood path now. Kept so
    // existing callers / tests read clearly.
    public Entity SpawnWoodPile(TilePos tile, int count = 1)
    {
        var w = Store.CreateEntity();
        w.AddComponent(new WorldPos { X = tile.X + 0.5f, Y = tile.Y + 0.5f });
        w.AddComponent(new ItemPile { Tile = tile, Count = count, ItemPath = ItemCatalog.Wood.FullPath });
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
        (_blueprintQ ??= Store.Query<Blueprint>()).ForEachEntity((ref Blueprint b, Entity _) =>
        {
            if (Math.Abs(b.Tile.X - t.X) <= radius && Math.Abs(b.Tile.Y - t.Y) <= radius) near = true;
        });
        if (near) return true;
        (_floorBpQ ??= Store.Query<FloorBlueprint>()).ForEachEntity((ref FloorBlueprint b, Entity _) =>
        {
            if (Math.Abs(b.Tile.X - t.X) <= radius && Math.Abs(b.Tile.Y - t.Y) <= radius) near = true;
        });
        if (near) return true;
        (_doorBpQ ??= Store.Query<DoorBlueprint>()).ForEachEntity((ref DoorBlueprint b, Entity _) =>
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

    // Tile is "outdoors" when it's not enclosed by any player-built room.
    // RoomMap leaves outdoor / barrier tiles at room id 0; indoor rooms get
    // ids 1..N. (Crop growth gates on the real per-tile LightAt, not this.)
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
        // Rooms are NOT dirtied here. RoomMap is bounded only by player walls
        // + doors + the border, so room recompute is driven off wall changes
        // (DoRebuildMapView, via _wallLayerDirty) and door events (explicit
        // _roomsDirty at door build/decon). Everything else that rebuilds the
        // map view — floors, furniture, lamps, trees — leaves rooms alone and
        // skips the O(map) flood-fill + auto-roof pass.
    }

    private void DoRebuildMapView()
    {
        MapView newView;
        lock (_mapLock)
        {
            MapVersion++;
            // A wall change is the only map mutation (besides doors, handled
            // explicitly) that alters room enclosure — so bump WallVersion
            // AND dirty the room layer here.
            if (_wallLayerDirty) { WallVersion++; _roomsDirty = true; _wallLayerDirty = false; }
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
            // Beds remain walkable (sleepers stand on the head tile) but
            // are marked as furniture so A* applies a steep cost weight
            // and routes around them when there's another way through.
            // Beds, stoves and sandbags stay walkable but carry a steep A*
            // cost so pawns route around them when another way exists.
            // Sandbags are deliberately NOT in blockingFurniture — pawns
            // can clamber over them (slowly), like RimWorld.
            TilePos[]? furnitureTiles = null;
            int furnCount = _bedOccupied.Count + _stoveOccupied.Count + _sandbagOccupied.Count;
            if (furnCount > 0)
            {
                furnitureTiles = new TilePos[furnCount];
                int bi = 0;
                foreach (var t in _bedOccupied) furnitureTiles[bi++] = t;
                foreach (var t in _stoveOccupied) furnitureTiles[bi++] = t;
                foreach (var t in _sandbagOccupied) furnitureTiles[bi++] = t;
            }
            TilePos[]? blockingFurniture = null;
            if (_urBoardOccupied.Count > 0)
            {
                blockingFurniture = new TilePos[_urBoardOccupied.Count];
                int bi = 0;
                foreach (var t in _urBoardOccupied) blockingFurniture[bi++] = t;
            }
            newView = Map.Snapshot(MapVersion, _mapView, _playerWalls.ToArray(), treeTiles, forbidden, doorTiles, doorCosts, furnitureTiles, blockingFurniture);
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
        var arr = tiles.ToArray(); // one array, shared as both Tiles + extras (read-only)
        e.AddComponent(new RoofBlueprint { Tiles = arr, Build = true });
        var extras = tiles.Count > 1 ? arr : null;
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
        var arr = tiles.ToArray(); // one array, shared as both Tiles + extras (read-only)
        e.AddComponent(new RoofBlueprint { Tiles = arr, Build = false });
        var extras = tiles.Count > 1 ? arr : null;
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
        EnsureLightChunkArrays(w, h);
    }

    // Day/night sun. Hour-of-day drives intensity (smoothstep ramps over
    // 1h at dawn/dusk, full daylight 8-20, fully dark 21-7) and color
    // (orange near horizon at the bookends of each ramp, white at full
    // daylight). The renderer + every wall/floor lighting consumer reads
    // composed lamp + sun RGB grid; sun lives behind the same channel
    // model so colored lamps composite the same way day or night.
    // Noon = warm dim sun (was pure 1,1,1 white). Slight orange cast
    // and a small dim so the world doesn't look bleached at peak day.
    private const float SunMidR = 1.00f, SunMidG = 0.50f, SunMidB = 0.11f;          // noon — full R, orange tint
    // Sunrise/sunset = very saturated orange-red. Was 1.0/0.55/0.25;
    // dropped green + blue hard so the horizon ramp reads as a deep
    // golden-hour glow instead of a beige tint.
    private const float SunHorizonR = 1.00f, SunHorizonG = 0.22f, SunHorizonB = 0.00f; // sunrise/sunset deep red-orange (R already 1.0)
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


    // d² → uncolored contribution byte LUT. d² is always a non-negative
    // integer here (dx, dy ∈ ℤ inside a 19×19 disc), so a flat byte[91]
    // covers every value inside the LampOuterSq=90.25 disc. Built once.
    // Outside that range the loop culls before indexing.
    private const int LampContribLutLen = 91;
    private static readonly byte[] LampContribLut = BuildLampContribLut();
    private static byte[] BuildLampContribLut()
    {
        var lut = new byte[LampContribLutLen];
        for (int d2 = 0; d2 < LampContribLutLen; d2++)
        {
            if (d2 <= LampInnerSq) { lut[d2] = LampInner; continue; }
            float r = MathF.Sqrt(d2);
            if (d2 <= LampMidSq)
            {
                float t = r - 7.5f;
                lut[d2] = (byte)Math.Round(LampMidStart - (LampMidStart - LampMidEnd) * t);
            }
            else
            {
                float t = r - 8.5f;
                lut[d2] = (byte)Math.Round(LampMidEnd - (LampMidEnd - LampOuterEnd) * t);
            }
        }
        return lut;
    }

    // (Re)size + reset the per-chunk dirty + reverse-index arrays for a
    // map of width w / height h. When chunk grid dimensions change every
    // existing bake's ChunkIds (relative to the old grid) is no longer
    // valid, so reset them, mark all chunks dirty, and force a rebake.
    private void EnsureLightChunkArrays(int w, int h)
    {
        int cw = (w + LightChunkSize - 1) / LightChunkSize;
        int ch = (h + LightChunkSize - 1) / LightChunkSize;
        int cn = cw * ch;
        if (_lightChunksW == cw && _lightChunksH == ch && _lightChunkDirty.Length == cn) return;
        _lightChunksW = cw;
        _lightChunksH = ch;
        _lightChunkDirty = new bool[cn];
        _lampsByChunk = new List<TilePos>[cn];
        for (int i = 0; i < cn; i++) { _lampsByChunk[i] = new List<TilePos>(); _lightChunkDirty[i] = true; }
        foreach (var bake in _lampBakes.Values)
        {
            bake.ChunkIds = Array.Empty<int>();
            bake.Dirty = true;
        }
    }

    private int ChunkIdForTile(int tx, int ty) =>
        (ty / LightChunkSize) * _lightChunksW + (tx / LightChunkSize);

    private void SubscribeLampToChunks(TilePos lamp, int[] chunkIds)
    {
        for (int i = 0; i < chunkIds.Length; i++)
        {
            int c = chunkIds[i];
            var list = _lampsByChunk[c];
            if (!list.Contains(lamp)) list.Add(lamp);
            _lightChunkDirty[c] = true;
        }
    }

    private void UnsubscribeLampFromChunks(TilePos lamp, int[] chunkIds)
    {
        for (int i = 0; i < chunkIds.Length; i++)
        {
            int c = chunkIds[i];
            _lampsByChunk[c].Remove(lamp);
            _lightChunkDirty[c] = true;
        }
    }

    // Bake one lamp's disc against the current wall/door layout. The
    // result is two parallel arrays (tile index, uncolored contribution)
    // that the composite loop scans without any sqrt / LOS work. Color
    // and power state are applied later, so a bake stays valid until
    // walls/doors near the lamp change.
    private void BakeLampDisc(TilePos centerTile, LampBake bake)
    {
        int w = Map.Width, h = Map.Height;
        int cx = centerTile.X, cy = centerTile.Y;
        int x0 = Math.Max(0, cx - 9);
        int x1 = Math.Min(w - 1, cx + 9);
        int y0 = Math.Max(0, cy - 9);
        int y1 = Math.Min(h - 1, cy + 9);
        // Worst-case 19×19 = 361 cells; in practice many are culled.
        var idxBuf = new int[361];
        var conBuf = new byte[361];
        int count = 0;
        for (int y = y0; y <= y1; y++)
        {
            int dy = y - cy;
            int dy2 = dy * dy;
            int row = y * w;
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int d2 = dx * dx + dy2;
                if (d2 >= LampContribLutLen) continue;
                // Wall tiles read as 0% lit — the wall itself is opaque,
                // no surface for light to land on.
                if (Map.GetWall(x, y) != WallType.None) continue;
                if (!LampLosClear(cx, cy, x, y)) continue;
                idxBuf[count] = row + x;
                conBuf[count] = LampContribLut[d2];
                count++;
            }
        }
        if (bake.Indices.Length != count) bake.Indices = new int[count];
        if (bake.Contribs.Length != count) bake.Contribs = new byte[count];
        Array.Copy(idxBuf, bake.Indices, count);
        Array.Copy(conBuf, bake.Contribs, count);
        // Derive unique chunk ids from indices. Small lists per disc so
        // a linear de-dup beats a HashSet alloc.
        Span<int> chunkScratch = stackalloc int[32];
        int chunkCount = 0;
        for (int i = 0; i < count; i++)
        {
            int idx = bake.Indices[i];
            int tx = idx % w;
            int ty = idx / w;
            int cid = ChunkIdForTile(tx, ty);
            bool seen = false;
            for (int k = 0; k < chunkCount; k++) if (chunkScratch[k] == cid) { seen = true; break; }
            if (!seen && chunkCount < chunkScratch.Length) chunkScratch[chunkCount++] = cid;
        }
        bake.ChunkIds = new int[chunkCount];
        for (int i = 0; i < chunkCount; i++) bake.ChunkIds[i] = chunkScratch[i];
        bake.Dirty = false;
    }

    // Rebake a lamp: unsub old chunks (marks them dirty), recompute the
    // disc against the current wall layout, sub new chunks (marks them
    // dirty). Old and new chunk sets typically overlap; the union ends
    // up dirty so the next composite picks up the change.
    private void RebakeLamp(TilePos centerTile, LampBake bake)
    {
        UnsubscribeLampFromChunks(centerTile, bake.ChunkIds);
        BakeLampDisc(centerTile, bake);
        SubscribeLampToChunks(centerTile, bake.ChunkIds);
    }

    // Ensure a lamp has a fresh bake. Call this after lamp lifecycle
    // events (place / rebuild / move) so the chunk grid knows about it
    // before RecomputeLampLight runs.
    private void EnsureLampBaked(TilePos tile)
    {
        if (!_lampMap.TryGetValue(tile, out var ent)) return;
        if (!ent.HasComponent<Lamp>()) return;
        if (!_lampBakes.TryGetValue(tile, out var bake))
        {
            bake = new LampBake();
            _lampBakes[tile] = bake;
        }
        if (bake.Dirty) RebakeLamp(tile, bake);
    }

    private void EnsureChunkRoofCounts()
    {
        if (_chunkRoofCountVersion == RoofVersion && _chunkRoofCount.Length == _lightChunkDirty.Length) return;
        int cn = _lightChunkDirty.Length;
        if (_chunkRoofCount.Length != cn) _chunkRoofCount = new int[cn];
        else Array.Clear(_chunkRoofCount, 0, cn);
        int w = Map.Width, h = Map.Height;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int cyOff = (y / LightChunkSize) * _lightChunksW;
            for (int x = 0; x < w; x++)
            {
                if (_roofTiles[row + x] != 0)
                    _chunkRoofCount[cyOff + (x / LightChunkSize)]++;
            }
        }
        _chunkRoofCountVersion = RoofVersion;
    }

    private void EnsureChunkWallCounts()
    {
        if (_chunkWallCountVersion == MapVersion && _chunkWallCount.Length == _lightChunkDirty.Length) return;
        int cn = _lightChunkDirty.Length;
        if (_chunkWallCount.Length != cn) _chunkWallCount = new int[cn];
        else Array.Clear(_chunkWallCount, 0, cn);
        int w = Map.Width, h = Map.Height;
        for (int y = 0; y < h; y++)
        {
            int cyOff = (y / LightChunkSize) * _lightChunksW;
            for (int x = 0; x < w; x++)
            {
                if (Map.GetWall(x, y) != WallType.None)
                    _chunkWallCount[cyOff + (x / LightChunkSize)]++;
            }
        }
        _chunkWallCountVersion = MapVersion;
    }

    // Composite the lamp buffer from cached per-lamp bakes. Only chunks
    // marked dirty (lamp lifecycle, color/power toggle, or wall change
    // nearby) get cleared and recomposited; the rest keep their bytes.
    // Sun is NOT composed here — it composes in at read time
    // (LightAt / CopyLightRgbForRender) so sunrise/sunset ticks don't
    // touch lamps.
    private void RecomputeLampLight()
    {
        // Phase 1: bring all dirty bakes up to date (this marks their
        // old + new chunks dirty via un/subscribe).
        foreach (var (tile, lampEnt) in _lampMap)
        {
            if (!lampEnt.HasComponent<Lamp>()) continue;
            if (!_lampBakes.TryGetValue(tile, out var bake))
            {
                bake = new LampBake();
                _lampBakes[tile] = bake;
            }
            if (bake.Dirty) RebakeLamp(tile, bake);
        }

        // Phase 2: clear dirty chunk bytes + collect unique lamps to replay.
        var replay = new HashSet<TilePos>();
        bool anyDirty = false;
        int cn = _lightChunkDirty.Length;
        for (int c = 0; c < cn; c++)
        {
            if (!_lightChunkDirty[c]) continue;
            anyDirty = true;
            ClearLightChunk(c);
            var list = _lampsByChunk[c];
            for (int i = 0; i < list.Count; i++) replay.Add(list[i]);
            _lightChunkDirty[c] = false;
        }
        if (!anyDirty) return;

        // Phase 3: replay each unique lamp's bake once. Max-blend is
        // idempotent, so writes to chunks that were not cleared simply
        // re-affirm the existing bytes — no aliasing risk.
        foreach (var tile in replay)
        {
            if (!_lampMap.TryGetValue(tile, out var ent)) continue;
            if (!ent.HasComponent<Lamp>()) continue;
            var lamp = ent.GetComponent<Lamp>();
            if (!lamp.PoweredOn) continue;
            if (!_lampBakes.TryGetValue(tile, out var bake)) continue;
            var col = lamp.Color;
            byte colR = col.R, colG = col.G, colB = col.B;
            int len = bake.Indices.Length;
            var indices = bake.Indices;
            var contribs = bake.Contribs;
            for (int i = 0; i < len; i++)
            {
                int idx = indices[i];
                byte contrib = contribs[i];
                byte cr = (byte)(contrib * colR / 255);
                byte cg = (byte)(contrib * colG / 255);
                byte cb = (byte)(contrib * colB / 255);
                if (_lampR[idx] < cr) _lampR[idx] = cr;
                if (_lampG[idx] < cg) _lampG[idx] = cg;
                if (_lampB[idx] < cb) _lampB[idx] = cb;
            }
        }

        LightVersion++;
    }

    // Zero one chunk's slice of the lamp R/G/B buffers. Edge chunks at
    // the map's right/bottom can be partial; clamp to the map bounds.
    private void ClearLightChunk(int chunkId)
    {
        int cx = chunkId % _lightChunksW;
        int cy = chunkId / _lightChunksW;
        int w = Map.Width;
        int x0 = cx * LightChunkSize;
        int y0 = cy * LightChunkSize;
        int x1 = Math.Min(w, x0 + LightChunkSize);
        int y1 = Math.Min(Map.Height, y0 + LightChunkSize);
        int rowLen = x1 - x0;
        if (rowLen <= 0) return;
        for (int y = y0; y < y1; y++)
        {
            int s = y * w + x0;
            Array.Clear(_lampR, s, rowLen);
            Array.Clear(_lampG, s, rowLen);
            Array.Clear(_lampB, s, rowLen);
        }
    }

    // Drop a lamp's cached bake. Call when the lamp is deconstructed —
    // unsubscribes from its chunks (marking them dirty so the next
    // composite re-fills them without this lamp's contribution).
    private void DropLampBake(TilePos tile)
    {
        if (_lampBakes.TryGetValue(tile, out var bake))
        {
            UnsubscribeLampFromChunks(tile, bake.ChunkIds);
            _lampBakes.Remove(tile);
        }
    }

    // Mark every lamp bake whose disc could contain `tile` as dirty.
    // Used when a wall or door is placed/removed at `tile`: only lamps
    // within R=9 of the change can have shadowed-through-the-new-wall
    // tiles inside their disc. Their currently-subscribed chunks are
    // marked dirty too, so we don't have to wait for the rebake step
    // to clear them.
    private void InvalidateLampBakesNear(TilePos tile)
    {
        if (_lampBakes.Count == 0) return;
        foreach (var (lampTile, bake) in _lampBakes)
        {
            int dx = lampTile.X - tile.X;
            int dy = lampTile.Y - tile.Y;
            if (dx * dx + dy * dy < LampContribLutLen)
            {
                bake.Dirty = true;
                for (int i = 0; i < bake.ChunkIds.Length; i++)
                    _lightChunkDirty[bake.ChunkIds[i]] = true;
            }
        }
    }

    // Power toggle / color change don't invalidate the bake but the
    // bake's contribution to the buffer needs re-composition. Mark the
    // lamp's currently-subscribed chunks dirty so they get re-cleared
    // and replayed (without this lamp if powered off, with its new
    // color if recolored).
    private void MarkLampChunksDirty(TilePos tile)
    {
        if (!_lampBakes.TryGetValue(tile, out var bake)) return;
        for (int i = 0; i < bake.ChunkIds.Length; i++)
            _lightChunkDirty[bake.ChunkIds[i]] = true;
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
    // Walls are forced to 0 so bilinear sampling shades them naturally
    // and stops light bleeding through to outside-adjacent pixels.
    //
    // Per-chunk dispatch: chunks with zero walls + zero roofs + zero
    // lamp influence are "pure-sky" — every pixel reads as the sun
    // triple, so we broadcast the sun bytes across the chunk instead of
    // running the per-tile composite. Sunrise/sunset becomes O(chunks)
    // for open areas instead of O(map) per tick.
    public byte[] CopyLightRgbForRender()
    {
        lock (_mapLock)
        {
            EnsureChunkRoofCounts();
            EnsureChunkWallCounts();
            int w = Map.Width;
            int h = Map.Height;
            int n = _lampR.Length;
            // Reused scratch — render thread is the only caller and consumes
            // the buffer immediately, so a persistent array avoids a fresh
            // n*3 byte alloc (192 KB at 256x256) on every light rebuild.
            if (_lightRgbScratch is null || _lightRgbScratch.Length != n * 3) _lightRgbScratch = new byte[n * 3];
            var rgb = _lightRgbScratch;
            byte sR = _lastSunR, sG = _lastSunG, sB = _lastSunB;
            int cn = _lightChunkDirty.Length;
            for (int chunkId = 0; chunkId < cn; chunkId++)
            {
                int cxg = chunkId % _lightChunksW;
                int cyg = chunkId / _lightChunksW;
                int x0 = cxg * LightChunkSize;
                int y0 = cyg * LightChunkSize;
                int x1 = Math.Min(w, x0 + LightChunkSize);
                int y1 = Math.Min(h, y0 + LightChunkSize);
                bool pureSky = _chunkRoofCount[chunkId] == 0
                              && _chunkWallCount[chunkId] == 0
                              && _lampsByChunk[chunkId].Count == 0;
                if (pureSky)
                {
                    for (int y = y0; y < y1; y++)
                    {
                        int rowI = y * w;
                        for (int x = x0; x < x1; x++)
                        {
                            int j = (rowI + x) * 3;
                            rgb[j]     = sR;
                            rgb[j + 1] = sG;
                            rgb[j + 2] = sB;
                        }
                    }
                    continue;
                }
                for (int y = y0; y < y1; y++)
                {
                    int rowI = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = rowI + x;
                        int j = i * 3;
                        if (Map.GetWall(x, y) != WallType.None)
                        {
                            rgb[j] = 0; rgb[j + 1] = 0; rgb[j + 2] = 0;
                            continue;
                        }
                        byte r = BoostLampCore(_lampR[i]);
                        byte g = BoostLampCore(_lampG[i]);
                        byte b = BoostLampCore(_lampB[i]);
                        if (_roofTiles[i] == 0)
                        {
                            if (sR > r) r = sR;
                            if (sG > g) g = sG;
                            if (sB > b) b = sB;
                        }
                        rgb[j] = r; rgb[j + 1] = g; rgb[j + 2] = b;
                    }
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
            EnsureWorkPriorities(e);
            EnsureSchedule(e);
            EnsureSleepNeed(e);
            EnsureHealth(e);
            EnsureCombat(e);
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

    public bool SpawnDummyAt(int tileX, int tileY)
    {
        try { SpawnDummy(tileX, tileY); return true; }
        catch { return false; }
    }

    // Debug/harness: force every pawn's RecreationNeed to a level.
    // Used by the ur-board scenario to drop all colonists below the
    // SeekThreshold so they urgent-seek the board immediately.
    public void SetAllRecreationLevel(float level)
    {
        (_recreationNeedQ ??= Store.Query<RecreationNeed>()).ForEachEntity((ref RecreationNeed r, Entity _) =>
        {
            r.Level = level;
        });
    }

    private Entity SpawnDummy(int tileX, int tileY)
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
                    EnsureWorkPriorities(e);
                    EnsureSchedule(e);
                    EnsureSleepNeed(e);
                    EnsureRecreationNeed(e);
                    EnsureHealth(e);
                    EnsureCombat(e);
                    return e;
                }
            }
        }
        throw new InvalidOperationException("No walkable tile found for dummy spawn.");
    }

    // Harness: spawn an M16-armed, drafted shooter and a target, and order
    // the shooter to open fire (full-auto). For the "gunfight" demo video.
    public void SetupGunfight(TilePos shooterTile, TilePos targetTile)
    {
        var shooter = SpawnDummy(shooterTile.X, shooterTile.Y);
        var target = SpawnDummy(targetTile.X, targetTile.Y);
        shooter.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>
            {
                new InventoryStack { ItemPath = Items.ItemCatalog.RifleAmmoFmj.FullPath, Count = 120 },
            },
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = Items.ItemCatalog.AssaultRifle.FullPath, Count = 1 },
            },
        });
        shooter.AddComponent(new Drafted());
        // Full-auto + finish-off so it keeps hammering the target through the
        // downed state until death. Pre-load a full mag so it opens fire
        // immediately instead of burning the first 2s on a reload.
        var rifle = Items.ItemCatalog.AssaultRifle;
        shooter.AddComponent(new RangedCombat
        {
            Mode = Items.FireMode.Auto,
            TargetEntityId = target.Id,
            FinishOff = true,
            MagCount = rifle.Ranged!.MagazineSize,
            LoadedAmmoPath = Items.ItemCatalog.RifleAmmoFmj.FullPath,
        });
    }

    // Harness: an armed, drafted colonist (defender) holds position and fires
    // at a hostile that hunts it — shows the full enemy loop on camera
    // (acquire, close to range, fire, retreat when hurt). The defender is the
    // only armed colonist; the enemy auto-acquires it via perception.
    public void SetupEnemyDemo(TilePos defenderTile, TilePos enemyTile)
    {
        var rifle = Items.ItemCatalog.AssaultRifle;
        var defender = SpawnDummy(defenderTile.X, defenderTile.Y);
        defender.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>
            {
                new InventoryStack { ItemPath = Items.ItemCatalog.RifleAmmoFmj.FullPath, Count = 240 },
            },
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = rifle.FullPath, Count = 1 },
            },
        });
        defender.AddComponent(new Drafted());
        var enemy = SpawnEnemy(enemyTile.X, enemyTile.Y);
        defender.AddComponent(new RangedCombat
        {
            Mode = Items.FireMode.Auto,
            TargetEntityId = enemy.Id,
            MagCount = rifle.Ranged!.MagazineSize,
            LoadedAmmoPath = Items.ItemCatalog.RifleAmmoFmj.FullPath,
        });
    }

    private bool IsOccupied(int tileX, int tileY)
    {
        bool occupied = false;
        (_wandererQ ??= Store.Query<WorldPos, Wanderer>()).ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            if ((int)p.X == tileX && (int)p.Y == tileY) occupied = true;
        });
        return occupied;
    }

    // Spawn a hostile: a rifle-armed pawn driven by the goal-oriented brain
    // in DummyController.PlanEnemy. Shares the mover + projectile/cover
    // pipeline with colonists but carries the Enemy marker so it skips all
    // colonist behavior. Pre-loaded mag so it can open fire immediately.
    // ── Player-facing notifications (raids, etc). Persist until the UI
    // dismisses one by id; published every snapshot. ──
    private readonly List<GameNotificationState> _notifications = new();
    private int _nextNotificationId = 1;

    public int RaiseNotification(string title, string message)
    {
        int id = _nextNotificationId++;
        _notifications.Add(new GameNotificationState(id, title, message));
        return id;
    }

    public void DismissNotification(int id) => _notifications.RemoveAll(n => n.Id == id);

    // Spawn a raid: a cluster of `count` raiders arriving together at one
    // random map edge, each running its own brain (no squad coordination yet),
    // and raise a notification so the UI can alert + pause. The raiders push
    // into the colony and fight on sight (default hunting mission).
    public void SpawnRaid(int count)
    {
        if (count < 1) count = 1;
        int n = SimConstants.MapSize;
        int side = _spawnRng.Next(4);
        int span = _spawnRng.Next(8, n - 8);
        string from;
        for (int k = 0; k < count; k++)
        {
            // Spread the entry span a little so they don't all stack on one tile
            // (SpawnEnemy still spirals each to a free tile).
            int s = Math.Clamp(span + (k - count / 2) * 2, 1, n - 2);
            var (sx, sy, label) = side switch
            {
                0 => (1, s, "west"),
                1 => (n - 2, s, "east"),
                2 => (s, 1, "north"),
                _ => (s, n - 2, "south"),
            };
            from = label;
            SpawnEnemy(sx, sy, RaidMission()); // assault the colony, then exfil when it's cleared
            if (k == count - 1)
                RaiseNotification("Raid!", $"{count} raiders are attacking from the {from}.");
        }
    }

    // Ticks an enemy holds an objective tile in the demo raid mission.
    private const int RaiderHoldTicks = 300; // ~5s at 60 TPS

    // A demonstrable raid arc: march to map centre, hold the ground a few
    // seconds, then exfil off the nearest edge (despawning). Combat reflexes
    // still interrupt each step. Shows the full mission lifecycle.
    // A raid member's mission: hunt the colony down, then leave the map once
    // it's cleared. The Assault step steers toward the living-colonist centroid
    // each think (Engage interrupts on sight); it completes when none remain.
    public static List<EnemyObjective> RaidMission() => new()
    {
        new EnemyObjective(EnemyObjectiveKind.Assault, 0, 0, 0),
        new EnemyObjective(EnemyObjectiveKind.Exfil, 0, 0, 0),
    };

    public static List<EnemyObjective> RaiderMission()
    {
        int c = SimConstants.MapSize / 2;
        return new List<EnemyObjective>
        {
            new EnemyObjective(EnemyObjectiveKind.AdvanceTo, c, c, 0),
            new EnemyObjective(EnemyObjectiveKind.Hold, c, c, RaiderHoldTicks),
            new EnemyObjective(EnemyObjectiveKind.Exfil, 0, 0, 0),
        };
    }

    public Entity SpawnEnemy(int tileX, int tileY, List<EnemyObjective>? mission = null)
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

                    var rifle = Items.ItemCatalog.AssaultRifle;
                    var e = Store.CreateEntity();
                    e.AddComponent(new WorldPos { X = x + 0.5f, Y = y + 0.5f });
                    e.AddComponent(new PathFollower());
                    e.AddComponent(new Wanderer());
                    EnsureHealth(e);
                    EnsureCombat(e);
                    e.AddComponent(new Inventory
                    {
                        Items = new List<InventoryStack>
                        {
                            new InventoryStack { ItemPath = Items.ItemCatalog.RifleAmmoFmj.FullPath, Count = 120 },
                        },
                        Equipped = new List<EquippedItemSlot>
                        {
                            new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = rifle.FullPath, Count = 1 },
                        },
                    });
                    e.AddComponent(new RangedCombat
                    {
                        Mode = Items.FireMode.Auto,
                        AimMode = Items.AimMode.Auto, // raiders pick aimed/snapshot by range
                        MagCount = rifle.Ranged!.MagazineSize,
                        LoadedAmmoPath = Items.ItemCatalog.RifleAmmoFmj.FullPath,
                    });
                    e.AddComponent(new Enemy());
                    e.AddComponent(new EnemyBrain { Mission = mission });
                    return e;
                }
            }
        }
        throw new InvalidOperationException("No walkable tile found for enemy spawn.");
    }

    // Spawn a hostile at the first walkable tile just inside a random map
    // edge — the entry point for a future raid system.
    public Entity SpawnEnemyAtEdge(List<EnemyObjective>? mission = null)
    {
        int n = SimConstants.MapSize;
        // pick the inner ring (x==1 / y==1 / x==n-2 / y==n-2) deterministically
        // off the sim rng so tests/replays stay stable.
        int side = _spawnRng.Next(4);
        int span = _spawnRng.Next(1, n - 1);
        var (sx, sy) = side switch
        {
            0 => (1, span),
            1 => (n - 2, span),
            2 => (span, 1),
            _ => (span, n - 2),
        };
        return SpawnEnemy(sx, sy, mission);
    }
}
