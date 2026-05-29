using Friflo.Engine.ECS;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;
using StruggleGame.Sim.Work;

namespace StruggleGame.Sim.World;

// Per-tick: for every Wanderer, drive the decision loop.
//   1. If a path request is in flight, poll PathService — when it resolves
//      either start walking or drop the goal.
//   2. If holding a BuildTarget that still points at an open/claimed job,
//      walk to a tile 4-adjacent to it.
//   3. If any WallBuild jobs are Open, claim the nearest reachable one
//      and request a route to its neighbor.
//   4. Otherwise pick a random wander goal and request a path.
//
// All pathfinding goes through PathService and all work goes through
// JobBoard so adding new kinds (haul, eat, sleep…) doesn't touch this
// shape.
public sealed class DummyController
{
    // 8-connected (cardinals + diagonals). Pawns must stand exactly one
    // tile from a blueprint center in any direction to work on it, so the
    // approach picker considers all 8 neighbors.
    private static readonly (int dx, int dy)[] EightNeighbors = new (int, int)[]
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    public delegate bool DoorLookup(TilePos tile, out Entity entity);

    private readonly PathService _paths;
    private readonly JobBoard _jobs;
    private readonly Func<MapView> _viewProvider;
    private readonly Action<JobId> _cancelJob;
    private readonly DoorLookup _tryGetDoor;
    private readonly Random _rng;
    // Per-pawn priority lookup. Wired from SimRuntime so the controller
    // doesn't have to know about the runtime's CheckmarkMode flag or the
    // parallel Allowed/Priorities arrays.
    private readonly Func<Entity, WorkType, byte> _getPriority;
    // Per-pawn schedule category for the current world hour. Wired from
    // SimRuntime so the controller doesn't need to know about the world
    // clock or per-pawn Schedule components.
    private readonly Func<Entity, ScheduleCategory> _getScheduleSlot;
    // Returns true if a blueprint entity has all its materials deposited
    // (or god-mode free-build is on). Build-kind jobs whose blueprint is
    // unfunded are filtered out of the claim list so pawns don't park
    // adjacent to a wall they can't construct yet.
    private readonly Func<Entity, bool> _isBlueprintFunded;
    // For a job, return the entity id of the blueprint it targets (build
    // jobs → blueprint entity, haul jobs → HaulPayload.BlueprintEntityId,
    // else 0). Used together with _getBlueprintClaimant to filter jobs
    // against player-pinned blueprint assignments.
    private readonly Func<Jobs.Job, int> _getJobBlueprintId;
    // For a blueprint entity id, return the pawn entity id pinned to it
    // (or 0 = unpinned). Pinned blueprints: the pinned pawn boosts to a
    // priority bucket above 1, everyone else skips the job entirely.
    private readonly Func<int, int> _getBlueprintClaimant;
    // (Entity pawn, out int bedEntityId, out TilePos bedOrigin, out TilePos bedFoot)
    // → true if a bed was reserved. Picks assigned bed if present, else
    // nearest unowned + unreserved bed. Sets BedReservedBy on the bed.
    public delegate bool TryReserveBedDelegate(Entity pawn, out int bedEntityId, out Map.TilePos bedOrigin, out Map.TilePos bedFoot);
    private readonly TryReserveBedDelegate _tryReserveBed;
    private readonly Action<int, int> _releaseBedReservation;
    // Recreation seat picker. Returns true on reservation; out fields name
    // the board, the board's tile (for proximity bookkeeping), the seat
    // tile the pawn walks to, and the role (player vs spectator).
    public delegate bool TryReserveRecreationDelegate(Entity pawn, RecreationKind preferred, out int boardEntityId, out TilePos boardTile, out TilePos seatTile, out RecreationRole role);
    private readonly TryReserveRecreationDelegate _tryReserveRecreation;
    private readonly Action<int, TilePos, RecreationRole> _releaseUrSeat;
    // Auto-claim list: pawn → bed pairs to assign after Step() so the
    // structural change happens outside the query loop. SimRuntime drains
    // it once cb.Playback returns.
    public readonly List<(int BedEntityId, int PawnEntityId)> PendingAutoBedClaims = new();

    // Sleep need triggers. Below SleepStartThreshold the pawn walks to a
    // bed (or floor-sleeps); they keep sleeping until Level >= 1.0.
    public const float SleepStartThreshold = 0.15f;
    // Optional callback for haul completion. Set by SimRuntime so we
    // don't need to plumb the runtime through the controller's surface.
    // Fires when a pawn physically picks up one item entity. Hooked by
    // SimRuntime to strip world-side Wood + HaulPayload from the entity.
    public Action<Friflo.Engine.ECS.Entity, CommandBuffer>? OnHaulPickup;
    // Fires when a pawn drops its entire inventory at a tile (either
    // planned DestTile or a fallback tile on abort). Hooked by SimRuntime
    // to re-anchor every slot, complete the primary job, and free any
    // never-picked-up topoff reservations.
    // Args: carrier entity, drop tile, command buffer. DeliverCarrying
    // owns the Carrying component lifecycle — callers must NOT remove
    // Carrying themselves (forbidden slots may be retained on the pawn).
    public Action<Entity, TilePos, CommandBuffer>? OnHaulDeliver;
    // Cook hooks wired by SimRuntime so DummyController can talk to the
    // item layer without a hard ref. Find nearest matching pile, consume
    // from a pile at a tile, spawn a drop at a tile.
    public Func<TilePos, string, TilePos?>? CookFindNearestPile;
    public Func<TilePos, string, int, int>? CookConsumePile;
    public Action<TilePos, string, int>? CookSpawnPile;
    // Scratch set populated each Step() so a single tick of topoff scans
    // doesn't reserve the same item for two different carriers.
    private readonly HashSet<int> _topoffReservedThisTick = new();
    // "Is there a Wood stack on this tile" — backed by the sim's item
    // spatial index (no per-tick full Wood scan). Build-kind jobs whose
    // target tile holds wood are filtered out of the claim list so a pawn
    // doesn't park next to a wall blueprint with wood sitting on it —
    // BlueprintClearanceSystem handles relocating the wood first.
    private readonly Func<TilePos, bool> _anyItemAt;
    // Idle-pawn job-seek throttle. Scanning every Open job for every idle
    // pawn every tick is pure waste when nothing changed. Skip the scan if
    // the JobBoard version hasn't moved since this pawn last looked AND it
    // looked within the last JobReseekInterval ticks. A version bump (job
    // posted / claimed / freed) forces an immediate re-scan, so newly
    // available work is still picked up next tick.
    private const long JobReseekInterval = 10;
    private readonly Dictionary<int, (long Version, long Tick)> _lastJobSeek = new();
    private long _tick;

    public DummyController(
        PathService paths,
        JobBoard jobs,
        Func<MapView> viewProvider,
        Action<JobId> cancelJob,
        int seed,
        DoorLookup tryGetDoor,
        Func<Entity, WorkType, byte> getPriority,
        Func<Entity, ScheduleCategory> getScheduleSlot,
        Func<Entity, bool> isBlueprintFunded,
        Func<Jobs.Job, int> getJobBlueprintId,
        Func<int, int> getBlueprintClaimant,
        TryReserveBedDelegate tryReserveBed,
        Action<int, int> releaseBedReservation,
        TryReserveRecreationDelegate tryReserveRecreation,
        Action<int, TilePos, RecreationRole> releaseUrSeat,
        Func<TilePos, bool> anyItemAt)
    {
        _anyItemAt = anyItemAt;
        _paths = paths;
        _jobs = jobs;
        _viewProvider = viewProvider;
        _cancelJob = cancelJob;
        _tryGetDoor = tryGetDoor;
        _rng = new Random(seed);
        _getPriority = getPriority;
        _getScheduleSlot = getScheduleSlot;
        _isBlueprintFunded = isBlueprintFunded;
        _getJobBlueprintId = getJobBlueprintId;
        _getBlueprintClaimant = getBlueprintClaimant;
        _tryReserveBed = tryReserveBed;
        _releaseBedReservation = releaseBedReservation;
        _tryReserveRecreation = tryReserveRecreation;
        _releaseUrSeat = releaseUrSeat;
    }

    public void Step(EntityStore store, float dt, long tick)
    {
        _tick = tick;
        var view = _viewProvider();
        var cb = store.GetCommandBuffer();
        _topoffReservedThisTick.Clear();
        var query = store.Query<WorldPos, PathFollower, Wanderer>();
        query.ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer w, Entity entity) =>
        {
            Plan(ref pos, ref path, ref w, dt, entity, cb, view, store);
            AdvanceAlongPath(ref pos, ref path, dt, view);
        });
        cb.Playback();
    }

    private void Plan(ref WorldPos pos, ref PathFollower path, ref Wanderer w, float dt, Entity entity, CommandBuffer cb, MapView view, EntityStore store)
    {
        var here = new TilePos((int)pos.X, (int)pos.Y);
        bool drafted = entity.HasComponent<Drafted>();

        // 1. Resolve in-flight request.
        if (path.PendingPathId != 0)
        {
            if (!_paths.TryConsume(path.PendingPathId, out var result))
            {
                return; // still pending
            }
            path.PendingPathId = 0;

            if (result.Status == PathStatus.Found && result.Path is { Count: > 0 })
            {
                path.Waypoints = result.Path;
                path.Index = result.Path[0] == here ? 1 : 0;
            }
            else if (!drafted)
            {
                // Unreachable. Kill the job so no one re-picks it.
                if (entity.HasComponent<BuildTarget>())
                {
                    var bt = entity.GetComponent<BuildTarget>();
                    _cancelJob(bt.JobId);
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                }
                path.Waypoints = null;
                path.Index = 0;
            }
            else
            {
                // Drafted: order was unreachable. Drop this order, fall
                // through to next order on the queue (if any).
                path.Waypoints = null;
                path.Index = 0;
            }
        }

        // Player equip order. Beats the job auction: the chosen colonist
        // walks to the dropped pile, pulls one unit into an equipped slot,
        // and clears the order. Bails if the pile vanished or is walled off.
        if (entity.HasComponent<EquipOrder>())
        {
            var eo = entity.GetComponent<EquipOrder>();
            bool stillThere = store.TryGetEntityById(eo.ItemEntityId, out var itemEnt)
                && itemEnt.HasComponent<ItemPile>()
                && itemEnt.GetComponent<ItemPile>().Tile == eo.ItemTile
                && itemEnt.GetComponent<ItemPile>().Count > 0;
            if (!stillThere)
            {
                cb.RemoveComponent<EquipOrder>(entity.Id);
                path.Waypoints = null;
                path.Index = 0;
                return;
            }
            if (here == eo.ItemTile)
            {
                path.Waypoints = null;
                path.Index = 0;
                int got = CookConsumePile?.Invoke(eo.ItemTile, eo.ItemPath, 1) ?? 0;
                if (got > 0)
                {
                    var equipSlot = new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = eo.ItemPath, Count = got };
                    if (entity.HasComponent<Inventory>())
                    {
                        ref var inv = ref entity.GetComponent<Inventory>();
                        inv.Equipped ??= new List<EquippedItemSlot>();
                        inv.Equipped.Add(equipSlot);
                    }
                    else
                    {
                        cb.AddComponent(entity.Id, new Inventory
                        {
                            Equipped = new List<EquippedItemSlot> { equipSlot },
                            Items = new List<InventoryStack>(),
                        });
                    }
                }
                cb.RemoveComponent<EquipOrder>(entity.Id);
                return;
            }
            // Head for the item now — don't finish the current wander leg
            // first. Re-path unless we're already walking toward the item
            // tile (or a request is already in flight from a prior tick).
            bool headingToItem = path.Waypoints is { Count: > 0 }
                && path.Waypoints[path.Waypoints.Count - 1] == eo.ItemTile;
            if (!headingToItem && path.PendingPathId == 0)
            {
                if (view.Walkable(eo.ItemTile))
                {
                    // Drop the stale wander path so the pawn stops walking
                    // away while the new route resolves.
                    path.Waypoints = null;
                    path.Index = 0;
                    path.PendingPathId = _paths.Request(here, eo.ItemTile);
                }
                else
                {
                    cb.RemoveComponent<EquipOrder>(entity.Id);
                }
            }
            return;
        }

        // Drafted colonists ignore jobs/wander. Walk the active player
        // order if there is one; otherwise dequeue the next move order;
        // otherwise hold position and watch.
        if (drafted)
        {
            // Drafted pawn drops any held rec seat — the player took
            // direct control, recreation is on hold.
            if (entity.HasComponent<RecreationReservation>())
            {
                var rr = entity.GetComponent<RecreationReservation>();
                _releaseUrSeat(rr.BoardEntityId, rr.SeatTile, rr.Role);
                cb.RemoveComponent<RecreationReservation>(entity.Id);
            }
            if (entity.HasComponent<AtRecreation>())
            {
                var ar = entity.GetComponent<AtRecreation>();
                _releaseUrSeat(ar.BoardEntityId, ar.SeatTile, ar.Role);
                cb.RemoveComponent<AtRecreation>(entity.Id);
            }
            // Belt-and-braces: if a BuildTarget survived the draft toggle
            // for any reason, drop it now and clear whatever path was
            // pointing at it. ToggleDraftCommand already does this on the
            // tick the draft started; this catches any race.
            if (entity.HasComponent<BuildTarget>())
            {
                var bt = entity.GetComponent<BuildTarget>();
                // Mid-haul: drop every carried + reserved item where the
                // carrier stands. OnHaulDeliver handles job completion +
                // dest-reservation release + freeing topoff reservations.
                if (entity.HasComponent<Carrying>())
                {
                    OnHaulDeliver?.Invoke(entity, here, cb);
                }
                else
                {
                    _jobs.Release(bt.JobId);
                }
                // Drafted mid-cook: drop staged carrots at our feet, kill
                // the Cooking binding, reset the stove's progress. Per
                // master spec, drafted is the only interrupt that drops
                // cook progress.
                if (entity.HasComponent<Cooking>())
                {
                    var cooking = entity.GetComponent<Cooking>();
                    if (store.TryGetEntityById(cooking.StoveEntityId, out var stoveEnt)
                        && stoveEnt.HasComponent<Stove>())
                    {
                        ref var stove = ref stoveEnt.GetComponent<Stove>();
                        stove.CookProgressTicks = 0f;
                        stove.CurrentBillIndex = -1;
                        stove.ActiveCookEntityId = 0;
                    }
                    cb.RemoveComponent<Cooking>(entity.Id);
                }
                if (entity.HasComponent<CookHaul>())
                {
                    var ch = entity.GetComponent<CookHaul>();
                    if (ch.CarrotsCarried > 0)
                    {
                        CookSpawnPile?.Invoke(here, Items.ItemCatalog.Carrot.FullPath, ch.CarrotsCarried);
                    }
                    cb.RemoveComponent<CookHaul>(entity.Id);
                }
                cb.RemoveComponent<BuildTarget>(entity.Id);
                if (path.PendingPathId != 0)
                {
                    _paths.Discard(path.PendingPathId);
                    path.PendingPathId = 0;
                }
                path.Waypoints = null;
                path.Index = 0;
            }

            if (path.Waypoints is not null && path.Index < path.Waypoints.Count) return;
            if (path.PendingPathId != 0) return;

            if (entity.HasComponent<OrderQueue>())
            {
                ref var oq = ref entity.GetComponent<OrderQueue>();
                while (oq.Tiles is { Count: > 0 })
                {
                    var next = oq.Tiles[0];
                    oq.Tiles.RemoveAt(0);
                    if (!view.Walkable(next) || next == here) continue;
                    path.PendingPathId = _paths.Request(here, next);
                    return;
                }
            }
            return; // standing watch
        }

        // 2. Sleep behavior. Pawn already Sleeping keeps sleeping until
        //    the SleepNeed hits 1.0; otherwise tired pawns (Level < 0.30)
        //    with no active build/haul walk to a bed (or floor-sleep on
        //    the spot if no bed is free).
        if (entity.HasComponent<Sleeping>())
        {
            float lvl = entity.HasComponent<SleepNeed>()
                ? entity.GetComponent<SleepNeed>().Level
                : 1f;
            if (lvl >= 1f)
            {
                var s = entity.GetComponent<Sleeping>();
                if (s.BedEntityId != 0) _releaseBedReservation(s.BedEntityId, entity.Id);
                cb.RemoveComponent<Sleeping>(entity.Id);
            }
            return;
        }

        bool tired = entity.HasComponent<SleepNeed>()
            && entity.GetComponent<SleepNeed>().Level < SleepStartThreshold;
        bool passedOut = entity.HasComponent<SleepNeed>()
            && entity.GetComponent<SleepNeed>().Level <= 0f;

        // Passed out: 0% sleep forces the pawn to drop in-flight work
        // so others (or themselves after waking) can pick it up. Voluntary
        // sleep (<30% but >0%) doesn't preempt — finish the current job
        // first.
        if (passedOut && entity.HasComponent<BuildTarget>())
        {
            var bt = entity.GetComponent<BuildTarget>();
            if (entity.HasComponent<Carrying>())
            {
                OnHaulDeliver?.Invoke(entity, here, cb);
            }
            else
            {
                _jobs.Release(bt.JobId);
            }
            cb.RemoveComponent<BuildTarget>(entity.Id);
            if (path.PendingPathId != 0)
            {
                _paths.Discard(path.PendingPathId);
                path.PendingPathId = 0;
            }
            path.Waypoints = null;
            path.Index = 0;
        }

        if (tired
            && (passedOut || !entity.HasComponent<BuildTarget>())
            && (passedOut || !entity.HasComponent<Carrying>()))
        {
            // Sleep overpowers recreation: a tired pawn parked at a board
            // releases the seat and heads for bed. Without this they'd
            // sit at the seat forever (the AtRecreation branch returns
            // early before this check).
            if (entity.HasComponent<AtRecreation>())
            {
                var ar = entity.GetComponent<AtRecreation>();
                _releaseUrSeat(ar.BoardEntityId, ar.SeatTile, ar.Role);
                cb.RemoveComponent<AtRecreation>(entity.Id);
            }
            if (entity.HasComponent<RecreationReservation>())
            {
                var rr = entity.GetComponent<RecreationReservation>();
                _releaseUrSeat(rr.BoardEntityId, rr.SeatTile, rr.Role);
                cb.RemoveComponent<RecreationReservation>(entity.Id);
            }
            // Pick / re-pick a bed each plan tick until arrival. The
            // runtime's TryReserveBed checks BedReservedBy atomically so
            // two tired pawns the same tick can't grab one bed.
            if (_tryReserveBed(entity, out int bedId, out var bedOrigin, out var bedFoot))
            {
                // Standing on the head tile → climb in. If the bed has no
                // owner yet, auto-assign it to this sleeper (queued for
                // after Step so the structural change is safe). Snap pos
                // to tile center so the sprite is exactly on the pillow,
                // not floating an inch off it.
                if (here == bedOrigin)
                {
                    path.Waypoints = null;
                    path.Index = 0;
                    pos.X = bedOrigin.X + 0.5f;
                    pos.Y = bedOrigin.Y + 0.5f;
                    cb.AddComponent(entity.Id, new Sleeping { BedEntityId = bedId });
                    if (!entity.HasComponent<AssignedBed>())
                    {
                        PendingAutoBedClaims.Add((bedId, entity.Id));
                    }
                    return;
                }
                // Walk toward the bed head. Beds are walkable, so the
                // origin tile is itself the path target.
                if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
                {
                    if (view.Walkable(bedOrigin))
                    {
                        path.PendingPathId = _paths.Request(here, bedOrigin);
                    }
                    else
                    {
                        // Bed unreachable — drop the reservation and
                        // floor-sleep on the spot rather than spin.
                        _releaseBedReservation(bedId, entity.Id);
                        cb.AddComponent(entity.Id, new Sleeping { BedEntityId = 0 });
                    }
                }
                return;
            }
            // No bed found → floor-sleep at current tile.
            path.Waypoints = null;
            path.Index = 0;
            cb.AddComponent(entity.Id, new Sleeping { BedEntityId = 0 });
            return;
        }

        // 2b. Recreation behavior. Pawn already AtRecreation stays put
        //     until the RecreationNeed hits 1.0; otherwise pawns below
        //     the seek threshold with no active build/haul walk to an
        //     Ur board seat picked from their RecreationPreference.
        if (entity.HasComponent<AtRecreation>())
        {
            float rLvl = entity.HasComponent<RecreationNeed>()
                ? entity.GetComponent<RecreationNeed>().Level
                : 1f;
            if (rLvl >= 1f)
            {
                var ar = entity.GetComponent<AtRecreation>();
                _releaseUrSeat(ar.BoardEntityId, ar.SeatTile, ar.Role);
                cb.RemoveComponent<AtRecreation>(entity.Id);
            }
            return;
        }

        // Schedule gating: pawn idle-seeks in Recreation/Any slot (any
        // level), or urgent-seeks (need below 15%) outside Sleep slot.
        // Sleep slot always wins over recreation — pawns about to sleep
        // shouldn't go play.
        var recSlot = _getScheduleSlot(entity);
        bool recSlotAllowsIdle = recSlot == ScheduleCategory.Recreation || recSlot == ScheduleCategory.Any;
        bool recBlocked = recSlot == ScheduleCategory.Sleep;
        bool hasRec = entity.HasComponent<RecreationNeed>() && entity.HasComponent<RecreationPreference>();
        bool urgentRec = hasRec && !recBlocked && entity.GetComponent<RecreationNeed>().Level < RecreationSystem.SeekThreshold;
        bool idleRec = hasRec && recSlotAllowsIdle && entity.GetComponent<RecreationNeed>().Level < 1f;
        bool seekingRec = (urgentRec || idleRec)
            && !entity.HasComponent<BuildTarget>()
            && !entity.HasComponent<Carrying>();
        // Existing reservation: pawn already holds a seat. Walk to it,
        // or sit if we're already on the tile. Skips a re-call into
        // TryReserveRecreation (which would leak seats by handing out
        // a fresh tile every tick with no per-pawn tracking).
        if (entity.HasComponent<RecreationReservation>())
        {
            var rr = entity.GetComponent<RecreationReservation>();
            if (recBlocked || !hasRec)
            {
                _releaseUrSeat(rr.BoardEntityId, rr.SeatTile, rr.Role);
                cb.RemoveComponent<RecreationReservation>(entity.Id);
            }
            else if (here == rr.SeatTile)
            {
                path.Waypoints = null;
                path.Index = 0;
                cb.AddComponent(entity.Id, new AtRecreation
                {
                    BoardEntityId = rr.BoardEntityId,
                    Kind = rr.Kind,
                    Role = rr.Role,
                    SeatTile = rr.SeatTile,
                });
                cb.RemoveComponent<RecreationReservation>(entity.Id);
                return;
            }
            else
            {
                // Re-path each plan tick the pawn has nothing in flight.
                // Wander paths get overridden immediately so the rec pull
                // beats the random stroll.
                if (path.Waypoints is null || path.Index >= path.Waypoints.Count
                    || (path.Waypoints.Count > 0 && path.Waypoints[path.Waypoints.Count - 1] != rr.SeatTile))
                {
                    if (view.Walkable(rr.SeatTile))
                    {
                        path.PendingPathId = _paths.Request(here, rr.SeatTile);
                    }
                    else
                    {
                        _releaseUrSeat(rr.BoardEntityId, rr.SeatTile, rr.Role);
                        cb.RemoveComponent<RecreationReservation>(entity.Id);
                    }
                }
                return;
            }
        }

        if (seekingRec)
        {
            var pref = entity.GetComponent<RecreationPreference>();
            // Kind sentinel 255 = "never rolled yet" — RecreationSystem
            // will pick one on its next tick; defer.
            if ((byte)pref.Kind <= 1
                && _tryReserveRecreation(entity, pref.Kind, out int boardId, out var bTile, out var seatTile, out var role))
            {
                cb.AddComponent(entity.Id, new RecreationReservation
                {
                    BoardEntityId = boardId,
                    Kind = pref.Kind,
                    Role = role,
                    SeatTile = seatTile,
                });
                if (here == seatTile)
                {
                    path.Waypoints = null;
                    path.Index = 0;
                    cb.AddComponent(entity.Id, new AtRecreation
                    {
                        BoardEntityId = boardId,
                        Kind = pref.Kind,
                        Role = role,
                        SeatTile = seatTile,
                    });
                    cb.RemoveComponent<RecreationReservation>(entity.Id);
                    return;
                }
                if (view.Walkable(seatTile))
                {
                    path.PendingPathId = _paths.Request(here, seatTile);
                }
                else
                {
                    // Seat unreachable — release reservation immediately.
                    _releaseUrSeat(boardId, seatTile, role);
                    cb.RemoveComponent<RecreationReservation>(entity.Id);
                }
                return;
            }
        }

        // 3. Existing build target.
        if (entity.HasComponent<BuildTarget>())
        {
            var bt = entity.GetComponent<BuildTarget>();
            var job = _jobs.Get(bt.JobId);
            if (job is null || job.State == JobState.Completed || job.State == JobState.Cancelled)
            {
                // If carrying for a now-dead haul, drop the cargo at the
                // current tile so it's not stuck in limbo.
                if (entity.HasComponent<Carrying>())
                {
                    OnHaulDeliver?.Invoke(entity, here, cb);
                }
                cb.RemoveComponent<BuildTarget>(entity.Id);
                path.Waypoints = null;
                path.Index = 0;
            }
            else if (job.Kind == JobKind.Haul)
            {
                HandleHaul(ref pos, ref path, entity, cb, view, job, here, store);
                return;
            }
            else if (job.Kind == JobKind.Cook)
            {
                HandleCook(ref pos, ref path, entity, cb, view, job, here, store);
                return;
            }
            else if (job.Kind == JobKind.FloorBuild
                || job.Kind == JobKind.FloorDeconstruct
                || job.Kind == JobKind.LampBuild
                || job.Kind == JobKind.LampDeconstruct)
            {
                // Floors don't block movement and the worker stands on
                // the tile itself — approach = job.Tile, adjacency
                // permissive.
                if (BuildAdjacency.InRangeOrOnTile(pos.X, pos.Y, job.Tile.X, job.Tile.Y))
                {
                    path.Waypoints = null;
                    path.Index = 0;
                    return;
                }
                if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
                {
                    if (!view.Walkable(job.Tile))
                    {
                        _cancelJob(bt.JobId);
                        cb.RemoveComponent<BuildTarget>(entity.Id);
                    }
                    else
                    {
                        path.PendingPathId = _paths.Request(here, job.Tile);
                    }
                }
                return;
            }
            else if (job.Kind == JobKind.RoofBuild || job.Kind == JobKind.RoofRemove)
            {
                // Roofs built from underneath: stand on the tile when
                // walkable, else stand on any adjacent walkable tile (so
                // roofs over walls / doors are reachable).
                if (BuildAdjacency.InRangeOrOnTile(pos.X, pos.Y, job.Tile.X, job.Tile.Y))
                {
                    path.Waypoints = null;
                    path.Index = 0;
                    return;
                }
                if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
                {
                    if (view.Walkable(job.Tile))
                    {
                        path.PendingPathId = _paths.Request(here, job.Tile);
                    }
                    else if (TryPickNeighbor(view, here, job.Tile, out var neighbor))
                    {
                        if (neighbor == here)
                        {
                            path.Waypoints = null;
                            path.Index = 0;
                        }
                        else
                        {
                            path.PendingPathId = _paths.Request(here, neighbor);
                        }
                    }
                    else
                    {
                        _cancelJob(bt.JobId);
                        cb.RemoveComponent<BuildTarget>(entity.Id);
                    }
                }
                return;
            }
            else if (BuildAdjacency.InRange(pos.X, pos.Y, job.Tile.X, job.Tile.Y))
            {
                // Standing in the build ring at exact tile center —
                // BuildSystem.Step will see the same InRange truth and
                // advance progress. Parking mid-tile is not safe: the
                // float adjacency rule is tighter than integer
                // Chebyshev-1, so a pawn at sub-tile pos like (5.0, 4.5)
                // would land outside InRange even though its integer
                // tile (5, 4) is "adjacent".
                path.Waypoints = null;
                path.Index = 0;
                return;
            }
            else if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
            {
                if (TryPickNeighbor(view, here, job.Tile, out var neighbor))
                {
                    if (neighbor == here)
                    {
                        path.Waypoints = null;
                        path.Index = 0;
                    }
                    else
                    {
                        path.PendingPathId = _paths.Request(here, neighbor);
                    }
                }
                else
                {
                    // No walkable neighbor anywhere — same as unreachable.
                    _cancelJob(bt.JobId);
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                }
                return;
            }
            else
            {
                return; // still walking
            }
        }

        // 4. Claim a new job — gated by the pawn's current schedule slot.
        // Sleep / Recreation pawns leave open jobs alone and fall through
        // to wander (placeholder for future tired/rec behavior). Work +
        // Any slots claim normally. Mid-job pawns reach this branch only
        // after their BuildTarget closes, so existing work always finishes
        // regardless of the slot — schedule is a guide, not a chokehold.
        var slot = _getScheduleSlot(entity);
        bool mayWork = slot == ScheduleCategory.Work || slot == ScheduleCategory.Any;
        if (mayWork && _jobs.Count > 0 && TryClaimJob(view, here, entity, cb, ref path))
        {
            return;
        }

        // 5. Wander.
        if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
        {
            // Rest timer only ticks while parked (no waypoints). On
            // first spawn IdleSec is 0 so the pawn requests its first
            // stroll immediately; on every subsequent arrival the
            // timer was armed back when the path was requested, so it
            // counts down from full here and the pawn actually pauses.
            w.IdleSec -= dt;
            if (w.IdleSec > 0f) return;
            RequestWanderPath(view, here, ref path);
            w.IdleSec = WanderRestMinSec + (float)_rng.NextDouble() * (WanderRestMaxSec - WanderRestMinSec);
        }
    }

    private const float WanderRestMinSec = 1.5f;
    private const float WanderRestMaxSec = 5.0f;

    // Pick a walkable 8-neighbor of `target` closest to `from`. Tiles that
    // are themselves pending wall/door blueprints are heavily deprioritized
    // (only chosen as a last resort) so two pawns building neighboring
    // blueprints don't park on each other's job tile and mutually block.
    private bool TryPickNeighbor(MapView view, TilePos from, TilePos target, out TilePos neighbor)
    {
        TilePos bestFree = default;
        int bestFreeDist = int.MaxValue;
        TilePos bestAny = default;
        int bestAnyDist = int.MaxValue;
        foreach (var (dx, dy) in EightNeighbors)
        {
            int nx = target.X + dx;
            int ny = target.Y + dy;
            if (!view.Walkable(nx, ny)) continue;
            int d = Math.Abs(nx - from.X) + Math.Abs(ny - from.Y);
            if (d < bestAnyDist)
            {
                bestAnyDist = d;
                bestAny = new TilePos(nx, ny);
            }
            if (IsPendingBlueprintTile(nx, ny)) continue;
            if (d < bestFreeDist)
            {
                bestFreeDist = d;
                bestFree = new TilePos(nx, ny);
            }
        }
        if (bestFreeDist != int.MaxValue)
        {
            neighbor = bestFree;
            return true;
        }
        if (bestAnyDist != int.MaxValue)
        {
            neighbor = bestAny;
            return true;
        }
        neighbor = default;
        return false;
    }

    private bool IsPendingBlueprintTile(int x, int y)
    {
        var job = _jobs.GetByTile(new TilePos(x, y));
        if (job is null) return false;
        if (job.State == JobState.Completed || job.State == JobState.Cancelled) return false;
        return job.Kind == JobKind.WallBuild || job.Kind == JobKind.DoorBuild;
    }

    private bool TryClaimJob(MapView view, TilePos from, Entity entity, CommandBuffer cb, ref PathFollower path)
    {
        // Throttle: skip the full job scan if nothing changed on the board
        // since this pawn last looked and it looked recently. (Version bump
        // = a job appeared/claimed/freed → re-scan immediately.)
        long jobVersion = _jobs.Version;
        if (_lastJobSeek.TryGetValue(entity.Id, out var last)
            && last.Version == jobVersion
            && _tick - last.Tick < JobReseekInterval)
        {
            return false;
        }
        _lastJobSeek[entity.Id] = (jobVersion, _tick);

        // Per-priority-bucket nearest job. Bucket 0 = player-pinned to
        // this pawn (RMB "Prioritize for X" beats every WorkType priority).
        // Buckets 1..8 are the work-tab priorities. Iterate 0..8 in order
        // — within a bucket we still pick the closest, but we never spill
        // into a lower-priority bucket while a higher one has work.
        Span<JobId> bestId = stackalloc JobId[9];
        Span<TilePos> bestApproach = stackalloc TilePos[9];
        Span<int> bestDist = stackalloc int[9];
        for (int i = 0; i < 9; i++) { bestId[i] = JobId.None; bestDist[i] = int.MaxValue; }

        foreach (var job in _jobs.All)
        {
            if (job.State != JobState.Open) continue;
            if (job.Forbidden) continue;
            if (!WorkTypes.TryGet(job.Kind, out var wt)) continue;
            byte pr = _getPriority(entity, wt);
            if (pr == 0 || pr > 8) continue;
            // Player-pinned blueprint filter: if this job targets a
            // blueprint somebody else owns, skip it; if the owner is us,
            // promote to bucket 0 so this beats every other priority.
            int pinnedBpId = _getJobBlueprintId(job);
            if (pinnedBpId != 0)
            {
                int owner = _getBlueprintClaimant(pinnedBpId);
                if (owner != 0)
                {
                    if (owner != entity.Id) continue;
                    pr = 0;
                }
            }
            // A pawn still hauling (forbidden cargo retained from a prior
            // delivery, mid-flight cargo, etc.) must not pick up another
            // haul — HandleHaul would treat the old Carrying as the active
            // job and walk to its stale DestTile.
            if (job.Kind == JobKind.Haul && entity.HasComponent<Carrying>()) continue;
            // Build-kind jobs gated on funded blueprints. Pawns must not
            // walk over and idle next to an unfunded wall — the haul
            // pipeline needs to land materials first.
            if ((job.Kind == JobKind.WallBuild
                 || job.Kind == JobKind.FloorBuild
                 || job.Kind == JobKind.DoorBuild
                 || job.Kind == JobKind.BedBuild)
                && !_isBlueprintFunded(job.Entity)) continue;
            // Don't try to build over a wood pile — clearance system
            // posts a haul to relocate it; pawn should pick the haul up
            // (or take a different job) rather than spin on the wall.
            if ((job.Kind == JobKind.WallBuild
                 || job.Kind == JobKind.DoorBuild
                 || job.Kind == JobKind.BedBuild)
                && _anyItemAt(job.Tile)) continue;
            int d = Math.Abs(job.Tile.X - from.X) + Math.Abs(job.Tile.Y - from.Y);
            if (d >= bestDist[pr]) continue;
            TilePos approach;
            bool isHaul = job.Kind == JobKind.Haul;
            bool isFloor = job.Kind == JobKind.FloorBuild || job.Kind == JobKind.FloorDeconstruct;
            bool isRoof = job.Kind == JobKind.RoofBuild || job.Kind == JobKind.RoofRemove;
            bool isLamp = job.Kind == JobKind.LampBuild || job.Kind == JobKind.LampDeconstruct;
            bool isCook = job.Kind == JobKind.Cook;
            if (isHaul || isFloor || isLamp || isCook)
            {
                if (!view.Walkable(job.Tile)) continue;
                approach = job.Tile;
            }
            else if (isRoof)
            {
                if (view.Walkable(job.Tile)) approach = job.Tile;
                else if (TryPickNeighbor(view, from, job.Tile, out var neighbor)) approach = neighbor;
                else continue;
            }
            else
            {
                if (!TryPickNeighbor(view, from, job.Tile, out var neighbor)) continue;
                approach = neighbor;
            }
            bestId[pr] = job.Id;
            bestApproach[pr] = approach;
            bestDist[pr] = d;
        }

        for (int level = 0; level <= 8; level++)
        {
            if (bestId[level].IsNone) continue;
            if (!_jobs.TryClaim(bestId[level], entity)) continue;
            cb.AddComponent(entity.Id, new BuildTarget { JobId = bestId[level] });
            if (bestApproach[level] != from)
            {
                path.PendingPathId = _paths.Request(from, bestApproach[level]);
            }
            return true;
        }
        return false;
    }

    // Cook flow. job.Entity = the stove. job.Tile = standing tile.
    // Phases:
    //   0 (no Cooking on pawn): gather carrots. If pawn already holds
    //      enough in CookHaul, walk to standing tile. Else find nearest
    //      reachable carrot pile, walk to it, consume what's needed.
    //   1 (Cooking on pawn): stand on the standing tile while CookSystem
    //      ticks progress. CookSystem clears the pawn's Cooking +
    //      BuildTarget on completion.
    private void HandleCook(
        ref WorldPos pos,
        ref PathFollower path,
        Entity entity,
        CommandBuffer cb,
        MapView view,
        Job job,
        TilePos here,
        EntityStore store)
    {
        var stoveEnt = job.Entity;
        if (!stoveEnt.HasComponent<Stove>() || !stoveEnt.HasComponent<BillsBoard>())
        {
            _cancelJob(job.Id);
            cb.RemoveComponent<BuildTarget>(entity.Id);
            return;
        }
        ref var stove = ref stoveEnt.GetComponent<Stove>();
        var board = stoveEnt.GetComponent<BillsBoard>();
        if (board.Bills is null || stove.CurrentBillIndex < 0
            || stove.CurrentBillIndex >= board.Bills.Count)
        {
            _cancelJob(job.Id);
            cb.RemoveComponent<BuildTarget>(entity.Id);
            return;
        }
        var bill = board.Bills[stove.CurrentBillIndex];
        var recipe = Recipes.Get(bill.Recipe);
        // V1: single input (carrots). Multi-input would loop here.
        var input = recipe.Inputs[0];
        int needed = input.Count;
        var standing = StoveOrientations.StandingTile(stove.Origin, stove.Orientation);

        // Phase 1: already cooking — stand still on the standing tile.
        if (entity.HasComponent<Cooking>())
        {
            var cooking = entity.GetComponent<Cooking>();
            if (cooking.Phase != 1) return;
            if (here != standing)
            {
                // Got bumped off. Walk back.
                if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
                {
                    path.PendingPathId = _paths.Request(here, standing);
                }
                return;
            }
            path.Waypoints = null;
            path.Index = 0;
            return;
        }

        // Phase 0: gather. Check pawn's current haul.
        int haveOnPawn = entity.HasComponent<CookHaul>()
            ? entity.GetComponent<CookHaul>().CarrotsCarried
            : 0;

        if (haveOnPawn >= needed)
        {
            // Walk to standing tile, deposit, and start cooking.
            if (here == standing)
            {
                path.Waypoints = null;
                path.Index = 0;
                cb.RemoveComponent<CookHaul>(entity.Id);
                cb.AddComponent(entity.Id, new Cooking
                {
                    StoveEntityId = stoveEnt.Id,
                    BillIndex = stove.CurrentBillIndex,
                    Phase = 1,
                });
                stove.ActiveCookEntityId = entity.Id;
                return;
            }
            if (path.Waypoints is null || path.Index >= path.Waypoints.Count
                || (path.Waypoints.Count > 0 && path.Waypoints[path.Waypoints.Count - 1] != standing))
            {
                if (!view.Walkable(standing))
                {
                    _cancelJob(job.Id);
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                    return;
                }
                path.PendingPathId = _paths.Request(here, standing);
            }
            return;
        }

        // Need more carrots: find nearest pile and walk to it.
        var pile = CookFindNearestPile?.Invoke(here, input.ItemPath);
        if (pile is null)
        {
            // Ingredient gone — drop what we have at our feet and bail.
            if (haveOnPawn > 0)
            {
                CookSpawnPile?.Invoke(here, input.ItemPath, haveOnPawn);
                cb.RemoveComponent<CookHaul>(entity.Id);
            }
            _cancelJob(job.Id);
            cb.RemoveComponent<BuildTarget>(entity.Id);
            return;
        }

        var pileTile = pile.Value;
        if (here == pileTile)
        {
            path.Waypoints = null;
            path.Index = 0;
            int wanted = needed - haveOnPawn;
            int taken = CookConsumePile?.Invoke(pileTile, input.ItemPath, wanted) ?? 0;
            if (taken > 0)
            {
                if (!entity.HasComponent<CookHaul>())
                {
                    cb.AddComponent(entity.Id, new CookHaul { CarrotsCarried = haveOnPawn + taken });
                }
                else
                {
                    ref var ch = ref entity.GetComponent<CookHaul>();
                    ch.CarrotsCarried += taken;
                }
            }
            return;
        }

        if (path.Waypoints is null || path.Index >= path.Waypoints.Count
            || (path.Waypoints.Count > 0 && path.Waypoints[path.Waypoints.Count - 1] != pileTile))
        {
            if (!view.Walkable(pileTile))
            {
                // Pile sitting on unwalkable tile (shouldn't happen for
                // ground items, but defensive): re-try next tick.
                return;
            }
            path.PendingPathId = _paths.Request(here, pileTile);
        }
    }

    // Multi-pickup haul: walk to primary pickup tile (job.Tile) → pickup
    // + topoff scan → walk each pending pickup tile in nearest order →
    // walk to dest tile → drop the whole inventory. Capacity is gated by
    // SimConstants.MaxCarryWeight + MaxCarryBulk using each ItemDef's
    // per-unit Weight/Bulk. Topoffs share the primary's DestTile rather
    // than picking their own — the merge pass + HaulSystem clean up any
    // overflow next tick.
    private void HandleHaul(
        ref WorldPos pos,
        ref PathFollower path,
        Entity entity,
        CommandBuffer cb,
        MapView view,
        Job job,
        TilePos here,
        EntityStore store)
    {
        if (!entity.HasComponent<Carrying>())
        {
            // Phase 1: walk to primary pickup tile.
            if (here == job.Tile)
            {
                path.Waypoints = null;
                path.Index = 0;
                if (!job.Entity.HasComponent<HaulPayload>())
                {
                    _cancelJob(job.Id);
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                    return;
                }
                var hp = job.Entity.GetComponent<HaulPayload>();
                var slots = new List<CarriedSlot>
                {
                    new CarriedSlot { EntityId = job.Entity.Id, ItemPath = hp.ItemPath, Count = hp.Count },
                };
                var pending = new List<int>();
                // Blueprint hauls skip topoffs — extra material at the
                // dropoff would dump as a Wood stack on top of the
                // blueprint tile rather than deposit. Stockpile hauls
                // keep the topoff sweep.
                if (hp.BlueprintEntityId == 0)
                {
                    ScanTopoffs(store, cb, entity, slots, pending, here, hp.DestTile);
                }
                cb.AddComponent(entity.Id, new Carrying
                {
                    Slots = slots,
                    PendingPickupIds = pending,
                    DestTile = hp.DestTile,
                    StockpileId = hp.StockpileId,
                    PrimaryJobId = job.Id,
                    BlueprintEntityId = hp.BlueprintEntityId,
                });
                OnHaulPickup?.Invoke(job.Entity, cb);
                return;
            }

            if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
            {
                if (!view.Walkable(job.Tile))
                {
                    _cancelJob(job.Id);
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                    return;
                }
                path.PendingPathId = _paths.Request(here, job.Tile);
            }
            return;
        }

        // Phase 2: carrying. Visit pending pickups (nearest first) before
        // heading to DestTile.
        var c = entity.GetComponent<Carrying>();
        TilePos? pickupTile = null;
        int pickupEntityId = 0;
        if (c.PendingPickupIds is { Count: > 0 })
        {
            int bestDist = int.MaxValue;
            foreach (var pid in c.PendingPickupIds)
            {
                if (!store.TryGetEntityById(pid, out var pe)) continue;
                if (!TryGetSourceTile(pe, out var ptile)) continue;
                int d = Math.Abs(ptile.X - here.X) + Math.Abs(ptile.Y - here.Y);
                if (d < bestDist) { bestDist = d; pickupTile = ptile; pickupEntityId = pid; }
            }
        }

        var target = pickupTile ?? c.DestTile;

        if (here == target)
        {
            path.Waypoints = null;
            path.Index = 0;

            if (pickupTile is not null)
            {
                if (store.TryGetEntityById(pickupEntityId, out var pe)
                    && pe.HasComponent<HaulPayload>()
                    && pe.HasComponent<ItemPile>())
                {
                    var hp = pe.GetComponent<HaulPayload>();
                    ref var live = ref entity.GetComponent<Carrying>();
                    live.Slots!.Add(new CarriedSlot { EntityId = pe.Id, ItemPath = hp.ItemPath, Count = hp.Count });
                    live.PendingPickupIds!.Remove(pickupEntityId);
                    OnHaulPickup?.Invoke(pe, cb);
                }
                else
                {
                    ref var live = ref entity.GetComponent<Carrying>();
                    live.PendingPickupIds!.Remove(pickupEntityId);
                }
                return;
            }

            // Dropoff at primary DestTile.
            OnHaulDeliver?.Invoke(entity, here, cb);
            cb.RemoveComponent<BuildTarget>(entity.Id);
            return;
        }

        if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
        {
            if (!view.Walkable(target))
            {
                if (pickupTile is not null)
                {
                    // Topoff blocked off — drop the reservation and try a
                    // different pending pickup (or the dest) next tick.
                    if (store.TryGetEntityById(pickupEntityId, out var pe))
                    {
                        if (pe.HasComponent<HaulReserved>()) cb.RemoveComponent<HaulReserved>(pe.Id);
                        if (pe.HasComponent<HaulPayload>()) cb.RemoveComponent<HaulPayload>(pe.Id);
                    }
                    ref var live = ref entity.GetComponent<Carrying>();
                    live.PendingPickupIds!.Remove(pickupEntityId);
                    return;
                }
                // Dest blocked: drop everything here, abort.
                OnHaulDeliver?.Invoke(entity, here, cb);
                cb.RemoveComponent<BuildTarget>(entity.Id);
                return;
            }
            path.PendingPathId = _paths.Request(here, target);
        }
    }

    // Looks for unreserved item entities of the same kind within
    // SimConstants.HaulTopoffRadius of the primary pickup tile and
    // reserves as many as fit in the pawn's remaining Weight + Bulk
    // capacity. Each reserved entity gets HaulReserved (JobId.None — it's
    // a piggyback pickup, not a posted job) + a HaulPayload pointing at
    // the primary's DestTile, plus its id appended to pendingIds.
    // Sum the weight/bulk of a pawn's persistent inventory (equipped +
    // general held). Shares the carry budget with haul cargo, so the
    // topoff scan must subtract it before deciding what else fits.
    private static void InventoryLoad(Entity carrier, out float w, out float b)
    {
        w = 0f; b = 0f;
        if (!carrier.HasComponent<Inventory>()) return;
        var inv = carrier.GetComponent<Inventory>();
        if (inv.Equipped is not null)
            foreach (var e in inv.Equipped)
                if (ItemCatalog.ItemsByPath.TryGetValue(e.ItemPath, out var d)) { w += d.Weight * e.Count; b += d.Bulk * e.Count; }
        if (inv.Items is not null)
            foreach (var it in inv.Items)
                if (ItemCatalog.ItemsByPath.TryGetValue(it.ItemPath, out var d)) { w += d.Weight * it.Count; b += d.Bulk * it.Count; }
    }

    private void ScanTopoffs(
        EntityStore store,
        CommandBuffer cb,
        Entity carrier,
        List<CarriedSlot> slots,
        List<int> pendingIds,
        TilePos primarySource,
        TilePos dest)
    {
        float wUsed = 0f, bUsed = 0f;
        foreach (var s in slots)
        {
            if (!ItemCatalog.ItemsByPath.TryGetValue(s.ItemPath, out var d)) continue;
            wUsed += d.Weight * s.Count;
            bUsed += d.Bulk * s.Count;
        }
        // Equipped + general inventory eat into the same budget.
        InventoryLoad(carrier, out float invW, out float invB);
        wUsed += invW;
        bUsed += invB;
        float wRem = SimConstants.MaxCarryWeight - wUsed;
        float bRem = SimConstants.MaxCarryBulk - bUsed;
        if (wRem <= 0f || bRem <= 0f) return;

        // Topoff with the same item path the primary slot carries — mixing
        // kinds would route a carrot pile to a wood-only stockpile dest.
        string primaryPath = slots.Count > 0 ? slots[0].ItemPath : string.Empty;

        // Snapshot candidates first so the nested query can't see any
        // mutations we'd queue mid-iteration. Wood is just an ItemPile now,
        // so one query covers every kind.
        var candidates = new List<(Entity Ent, int Count, string Path, int Dist)>();
        if (!string.IsNullOrEmpty(primaryPath))
        {
            store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity e) =>
            {
                if (p.ItemPath != primaryPath) return;
                if (e.HasComponent<HaulReserved>()) return;
                if (e.HasComponent<Forbidden>()) return;
                if (_topoffReservedThisTick.Contains(e.Id)) return;
                int md = Math.Abs(p.Tile.X - primarySource.X) + Math.Abs(p.Tile.Y - primarySource.Y);
                if (md == 0) return;
                if (md > SimConstants.HaulTopoffRadius) return;
                candidates.Add((e, p.Count, p.ItemPath, md));
            });
        }
        candidates.Sort((a, b) => a.Dist - b.Dist);

        foreach (var cand in candidates)
        {
            if (!ItemCatalog.ItemsByPath.TryGetValue(cand.Path, out var def)) continue;
            float w = def.Weight * cand.Count;
            float b = def.Bulk * cand.Count;
            if (w > wRem || b > bRem) continue;
            cb.AddComponent(cand.Ent.Id, new HaulPayload
            {
                DestTile = dest,
                StockpileId = 0,
                ItemPath = cand.Path,
                Count = cand.Count,
            });
            cb.AddComponent(cand.Ent.Id, new HaulReserved { JobId = JobId.None });
            pendingIds.Add(cand.Ent.Id);
            _topoffReservedThisTick.Add(cand.Ent.Id);
            wRem -= w;
            bRem -= b;
            if (wRem <= 0f || bRem <= 0f) break;
        }
    }

    // A pickup-pending item entity carries an ItemPile (wood, carrots, …).
    private static bool TryGetSourceTile(Entity e, out TilePos tile)
    {
        if (e.HasComponent<ItemPile>()) { tile = e.GetComponent<ItemPile>().Tile; return true; }
        tile = default;
        return false;
    }

    private const int WanderRadius = 10;

    private void RequestWanderPath(MapView view, TilePos from, ref PathFollower path)
    {
        // Anchor wander to a player-placed wall if any exist, else to
        // the map center. PlayerWalls excludes border + procgen so
        // colonists don't drift to map edges when nothing is built.
        TilePos anchor;
        if (view.PlayerWalls.Count > 0)
        {
            anchor = view.PlayerWalls[_rng.Next(view.PlayerWalls.Count)];
        }
        else
        {
            anchor = new TilePos(view.Width / 2, view.Height / 2);
        }
        for (int tries = 0; tries < 12; tries++)
        {
            int gx = anchor.X + _rng.Next(-WanderRadius, WanderRadius + 1);
            int gy = anchor.Y + _rng.Next(-WanderRadius, WanderRadius + 1);
            var goal = new TilePos(gx, gy);
            if (!view.Walkable(goal) || goal == from) continue;
            // Skip furniture footprints (beds) — they're walkable for
            // sleepers but a terrible wander destination since the
            // pawn would camp on the mattress.
            if (view.HasFurniture(goal)) continue;
            path.PendingPathId = _paths.Request(from, goal);
            return;
        }
    }

    private void AdvanceAlongPath(ref WorldPos pos, ref PathFollower path, float dt, MapView view)
    {
        if (path.Waypoints is null || path.Index >= path.Waypoints.Count) return;

        float remaining = SimConstants.WalkTilesPerSecond * dt;
        while (remaining > 0f && path.Index < path.Waypoints.Count)
        {
            var target = path.Waypoints[path.Index];

            // Door gate: if the next tile holds a door that isn't fully
            // open yet, flag it as wanting to open and freeze in place
            // until DoorSystem advances State to Open.
            if (_tryGetDoor(target, out var doorEnt))
            {
                ref var door = ref doorEnt.GetComponent<Door>();
                if (door.Forbidden)
                {
                    // Door was forbidden after path was planned. Treat as a
                    // wall: drop stale waypoints, let planner re-route.
                    path.Waypoints = null;
                    path.Index = 0;
                    return;
                }
                if (door.State != DoorState.Open)
                {
                    door.WantsOpen = true;
                    return;
                }
                door.IdleSec = 0f;
            }
            else if (!view.Walkable(target))
            {
                // A wall (or other blocker) appeared on the planned route
                // after this path was computed. Drop the stale waypoints
                // so the planner re-routes next tick instead of marching
                // the pawn onto unwalkable terrain.
                path.Waypoints = null;
                path.Index = 0;
                return;
            }

            float tx = target.X + 0.5f;
            float ty = target.Y + 0.5f;
            float dx = tx - pos.X;
            float dy = ty - pos.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            if (dist <= remaining)
            {
                pos.X = tx;
                pos.Y = ty;
                remaining -= dist;
                path.Index++;
            }
            else
            {
                pos.X += dx / dist * remaining;
                pos.Y += dy / dist * remaining;
                remaining = 0f;
            }
        }

        if (path.Index >= path.Waypoints!.Count)
        {
            path.Waypoints = null;
            path.Index = 0;
        }
    }
}
