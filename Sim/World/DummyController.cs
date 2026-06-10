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
    // Tick-start crowding state per tile, rebuilt each Step(). Count = pawns on
    // the tile; SlowedId = the ONE mover picked to slow (others move full so the
    // overlap clears decisively instead of looping overlap→separate→overlap).
    // Pick: the lone mover when one pawn is stationary, else a pseudo-random
    // mover (min-hash, deterministic for replays). Pure movement-step friction —
    // pathfinding never sees it, so the multithreaded pather stays untouched.
    private readonly Dictionary<TilePos, (int Count, int SlowedId, uint BestKey)> _tileCrowd = new();
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

    // Melee: punch cadence + flat miss chance (melee-skill stub).
    private const long MeleeAttackInterval = 60;
    private const double MeleeMissChance = 0.10;
    private const long MeleeEngagedTicks = 120;  // victim stays slowed ~2s after a swing
    private const long MeleeStunTicks = 30;       // ~0.5s frozen on a stun
    private const double MeleeStunChance = 0.25;
    private const float EngagedSlowFactor = 0.5f;
    private const long TendHoldTicks = 20; // patient holds this long after each tend tick
    // Walk speed multiplier while sharing a tile with another pawn (squeeze).
    private const float CrowdedSpeedFactor = 0.4f;
    private const float SouthFacing = MathF.PI / 2f; // +Y is down on screen
    // Wired by SimRuntime: land a melee hit (attacker, target). Attacker's
    // equipped weapon decides the damage.
    public Action<int, int>? MeleeHit;
    // Wired by SimRuntime: line-of-sight test for bullets (x0,y0,x1,y1).
    public Func<int, int, int, int, bool>? LosClear;
    // Wired by SimRuntime: is there a built sandbag at this tile? (cover)
    public Func<int, int, bool>? HasSandbag;
    // Bullet-spawn requests emitted this tick, drained by SimRuntime after
    // the query pass (entity creation can't happen mid-iteration).
    public readonly List<ProjectileSpawn> PendingProjectiles = new();

    // Reused scratch for haul top-off candidate scan (avoids a per-pickup List
    // + a per-call sort-comparer delegate).
    private readonly List<(Entity Ent, int Count, string Path, int Dist)> _topoffCandidates = new();
    private static readonly Comparison<(Entity Ent, int Count, string Path, int Dist)> _topoffByDist
        = (a, b) => a.Dist - b.Dist;

    private ArchetypeQuery<WorldPos, PathFollower, Wanderer>? _wandererQ;
    private ArchetypeQuery<ItemPile>? _itemPileQ;

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
        // Snapshot live colonist targets once per tick (id + pos of every
        // conscious non-enemy pawn) so the enemy brain can perceive without
        // nesting an ECS query inside the per-pawn loop below.
        _colonistTargets.Clear();
        _enemyTargets.Clear();
        (_colonistTargetsQ ??= store.Query<WorldPos, Wanderer, Health>()).ForEachEntity((ref WorldPos p, ref Wanderer _, ref Health h, Entity e) =>
        {
            if (h.Unconscious) return;
            // Use the EFFECTIVE position — the peek cell when leaning — so a
            // pawn peeking around cover is perceived (and aimed at) where its
            // hitbox actually is, not at its body tile behind the wall.
            EffectivePos(e, p.X, p.Y, out float ex, out float ey);
            if (e.HasComponent<Enemy>()) _enemyTargets.Add((e.Id, ex, ey)); // for colonist auto-engage
            else _colonistTargets.Add((e.Id, ex, ey));                      // for enemy perception
        });
        var query = _wandererQ ??= store.Query<WorldPos, PathFollower, Wanderer>();
        // Tick-start crowding: count pawns per tile and pick the ONE mover to
        // slow there (others stay full so the overlap clears, not oscillates).
        // Stationary pawns never get picked, so a parked/downed pawn holds while
        // the mover squeezes past it; with two movers it's a deterministic
        // pseudo-random (min-hash) pick. Pure local friction, no blocking.
        _tileCrowd.Clear();
        query.ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer _, Entity entity) =>
        {
            // Downed pawns don't crowd — you step over a body freely (matches
            // "downed pawns don't block"). Skip them entirely.
            if (entity.HasComponent<Health>() && entity.GetComponent<Health>().Unconscious) return;
            var t = new TilePos((int)pos.X, (int)pos.Y);
            bool moving = path.Waypoints is { Count: > 0 } && path.Index < path.Waypoints.Count;
            _tileCrowd.TryGetValue(t, out var agg);
            agg.Count++;
            if (moving)
            {
                uint key = CrowdHash(entity.Id, t.X, t.Y);
                if (agg.SlowedId == 0 || key < agg.BestKey) { agg.SlowedId = entity.Id; agg.BestKey = key; }
            }
            _tileCrowd[t] = agg;
        });
        query.ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer w, Entity entity) =>
        {
            Plan(ref pos, ref path, ref w, dt, entity, cb, view, store);
            // Injured legs slow the walk (Moving capacity), floored so a
            // hurt-but-conscious pawn can still crawl.
            float bx = pos.X, by = pos.Y;
            float speedMul = HealthMods.MoveSpeed(entity);
            if (entity.HasComponent<Combat>() && tick < entity.GetComponent<Combat>().EngagedUntil)
                speedMul *= EngagedSlowFactor;
            // Crowding: if this tile is shared and WE'RE the picked mover, slow
            // the squeeze-through. Exactly one pawn per shared tile slows; the
            // rest keep full speed so the clump resolves instead of oscillating.
            if (_tileCrowd.TryGetValue(new TilePos((int)pos.X, (int)pos.Y), out var crowd)
                && crowd.Count >= 2 && crowd.SlowedId == entity.Id)
                speedMul *= CrowdedSpeedFactor;
            AdvanceAlongPath(ref pos, ref path, dt, view, speedMul);
            float mdx = pos.X - bx, mdy = pos.Y - by;
            if (mdx * mdx + mdy * mdy > 1e-9f) w.Facing = MathF.Atan2(mdy, mdx);
        });
        cb.Playback();
    }

    private void Plan(ref WorldPos pos, ref PathFollower path, ref Wanderer w, float dt, Entity entity, CommandBuffer cb, MapView view, EntityStore store)
    {
        var here = new TilePos((int)pos.X, (int)pos.Y);

        // Hostiles run an entirely separate goal-oriented brain — no jobs,
        // wander, sleep, needs or player orders. AdvanceAlongPath (after this
        // returns) still moves them along any path the brain requested.
        if (entity.HasComponent<Enemy>())
        {
            PlanEnemy(ref pos, ref path, ref w, dt, entity, cb, view, store, here);
            return;
        }

        bool drafted = entity.HasComponent<Drafted>();

        // Drafted + next to a sandbag → stay crouched (head down) while
        // standing there or moving along the line. Persists until the pawn
        // leaves every sandbag's 8-neighbourhood. The firing block may
        // override with a Popped/Leaning stance when actually shooting.
        w.Crouched = drafted && IsAdjacentToSandbag(here);

        // Undraft-mid-walk edge: abandon the drafted move order and ease
        // onto the nearest tile before normal jobs/wander resume.
        if (w.WasDrafted && !drafted)
        {
            if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
            path.Waypoints = null;
            path.Index = 0;
            w.Snapping = true;
        }
        if (drafted) w.Snapping = false; // re-drafted: drafted logic owns movement
        w.WasDrafted = drafted;

        // Keep RangedCombat attached iff a ranged weapon is equipped. (The
        // structural add/remove lands at cb.Playback — next tick it's live.)
        bool hasRangedWeapon = TryGetRangedWeapon(entity, out var rangedWeaponDef);
        if (hasRangedWeapon && !entity.HasComponent<RangedCombat>())
            cb.AddComponent(entity.Id, new RangedCombat { Mode = DefaultFireMode(rangedWeaponDef.Ranged!) });
        else if (!hasRangedWeapon && entity.HasComponent<RangedCombat>())
            cb.RemoveComponent<RangedCombat>(entity.Id);
        if (entity.HasComponent<RangedCombat>())
        {
            ref var rc0 = ref entity.GetComponent<RangedCombat>();
            // Always finish a reload once its timer elapses, even if the pawn
            // stopped firing mid-reload (target lost) — this is the insert-mag
            // phase that actually fills the mag (else the reload would just keep
            // restarting and never load).
            if (rc0.Reloading && _tick >= rc0.NextActionTick && hasRangedWeapon)
                CompleteReload(entity, ref rc0, rangedWeaponDef.Ranged!);
            // Recoil settles back down over time (fast between taps/bursts).
            if (rc0.Recoil > 0f && hasRangedWeapon)
                rc0.Recoil = MathF.Max(0f, rc0.Recoil - rangedWeaponDef.Ranged!.RecoilRecoverPerSec * dt);
            // Undrafting stops any fire order (mirrors melee being cleared).
            if (!drafted) { rc0.TargetEntityId = 0; rc0.BurstRemaining = 0; }
            // Default to no cover stance each tick; the firing block below
            // re-asserts Tucked/Popped when actually engaging from cover.
            rc0.Stance = CoverStance.None;
            rc0.Leaning = false;
        }

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

        // Unconscious (passed out from blood loss / brain damage): the
        // colonist collapses where they are. Drop any job so others can
        // take it, kill EVERY queued order (drafted move queue, melee,
        // equip, pick-up), stop moving — but keep draft status. Nothing
        // resumes when they come to.
        if (entity.HasComponent<Health>() && entity.GetComponent<Health>().Unconscious)
        {
            if (entity.HasComponent<BuildTarget>())
            {
                var bt = entity.GetComponent<BuildTarget>();
                if (entity.HasComponent<Carrying>()) OnHaulDeliver?.Invoke(entity, here, cb);
                else _jobs.Release(bt.JobId);
                cb.RemoveComponent<BuildTarget>(entity.Id);
            }
            if (entity.HasComponent<OrderQueue>()) cb.RemoveComponent<OrderQueue>(entity.Id);
            if (entity.HasComponent<MeleeTarget>()) cb.RemoveComponent<MeleeTarget>(entity.Id);
            if (entity.HasComponent<TreatmentTarget>()) cb.RemoveComponent<TreatmentTarget>(entity.Id);
            if (entity.HasComponent<EquipOrder>()) cb.RemoveComponent<EquipOrder>(entity.Id);
            if (entity.HasComponent<PickupOrder>()) cb.RemoveComponent<PickupOrder>(entity.Id);
            if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
            path.Waypoints = null;
            path.Index = 0;
            // Slide onto the nearest tile as they collapse, rather than
            // freezing mid-step between tiles.
            SnapToNearestTile(ref pos, dt, out _, out _);
            return;
        }

        // Stunned (just took a melee hit): frozen for a moment — can't
        // move or act.
        if (entity.HasComponent<Combat>() && _tick < entity.GetComponent<Combat>().StunUntil)
        {
            if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
            path.Waypoints = null;
            path.Index = 0;
            return;
        }

        // Being tended: a medic is treating this pawn — hold still (don't wander
        // or wander off into a job) so the doctor stays adjacent. The medic
        // refreshes TendedUntilTick each working tick.
        if (_tick < w.TendedUntilTick && !entity.HasComponent<TreatmentTarget>())
        {
            ClearPath(ref path);
            SnapToNearestTile(ref pos, dt, out _, out _);
            return;
        }

        // Easing onto the grid after an undraft mid-walk. Hold here (no
        // jobs/wander) until centered, then fall through this same tick.
        if (w.Snapping)
        {
            if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
            path.Waypoints = null;
            path.Index = 0;
            if (SnapToNearestTile(ref pos, dt, out float udx, out float udy))
                w.Snapping = false;
            else
            {
                if (udx * udx + udy * udy > 1e-9f) w.Facing = MathF.Atan2(udy, udx);
                return;
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
                    var eqSlot = Items.ItemCatalog.ItemsByPath.TryGetValue(eo.ItemPath, out var edef) && edef.IsArmor
                        ? EquipSlot.Apparel : EquipSlot.Generic;
                    var equipSlot = new EquippedItemSlot { Slot = eqSlot, ItemPath = eo.ItemPath, Count = got };
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

        // Player pick-up order. Same shape as EquipOrder, but the unit(s)
        // go into general inventory and the amount is clamped to remaining
        // carry capacity on arrival.
        if (entity.HasComponent<PickupOrder>())
        {
            var po = entity.GetComponent<PickupOrder>();
            bool stillThere = store.TryGetEntityById(po.ItemEntityId, out var itemEnt)
                && itemEnt.HasComponent<ItemPile>()
                && itemEnt.GetComponent<ItemPile>().Tile == po.ItemTile
                && itemEnt.GetComponent<ItemPile>().Count > 0;
            if (!stillThere)
            {
                cb.RemoveComponent<PickupOrder>(entity.Id);
                path.Waypoints = null;
                path.Index = 0;
                return;
            }
            if (here == po.ItemTile)
            {
                path.Waypoints = null;
                path.Index = 0;
                int want = po.RequestedCount;
                if (ItemCatalog.ItemsByPath.TryGetValue(po.ItemPath, out var def))
                {
                    CurrentCarryLoad(entity, out float lw, out float lb);
                    int fit = (def.Weight <= 0f && def.Bulk <= 0f)
                        ? int.MaxValue
                        : (int)Math.Floor(Math.Min(
                            def.Weight > 0f ? (SimConstants.MaxCarryWeight - lw) / def.Weight : int.MaxValue,
                            def.Bulk > 0f ? (SimConstants.MaxCarryBulk - lb) / def.Bulk : int.MaxValue));
                    if (fit < 0) fit = 0;
                    want = Math.Min(want, fit);
                }
                int got = want > 0 ? (CookConsumePile?.Invoke(po.ItemTile, po.ItemPath, want) ?? 0) : 0;
                if (got > 0)
                {
                    if (entity.HasComponent<Inventory>())
                    {
                        ref var inv = ref entity.GetComponent<Inventory>();
                        inv.Items ??= new List<InventoryStack>();
                        int idx = -1; // plain loop, not FindIndex (no predicate closure alloc)
                        for (int k = 0; k < inv.Items.Count; k++)
                            if (inv.Items[k].ItemPath == po.ItemPath) { idx = k; break; }
                        if (idx >= 0) { var s = inv.Items[idx]; s.Count += got; inv.Items[idx] = s; }
                        else inv.Items.Add(new InventoryStack { ItemPath = po.ItemPath, Count = got });
                    }
                    else
                    {
                        cb.AddComponent(entity.Id, new Inventory
                        {
                            Items = new List<InventoryStack> { new InventoryStack { ItemPath = po.ItemPath, Count = got } },
                            Equipped = new List<EquippedItemSlot>(),
                        });
                    }
                }
                cb.RemoveComponent<PickupOrder>(entity.Id);
                return;
            }
            bool headingToItem = path.Waypoints is { Count: > 0 }
                && path.Waypoints[path.Waypoints.Count - 1] == po.ItemTile;
            if (!headingToItem && path.PendingPathId == 0)
            {
                if (view.Walkable(po.ItemTile))
                {
                    path.Waypoints = null;
                    path.Index = 0;
                    path.PendingPathId = _paths.Request(here, po.ItemTile);
                }
                else
                {
                    cb.RemoveComponent<PickupOrder>(entity.Id);
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

            // Medical order: walk to the patient and tend / stabilize over time.
            // Cancels when the patient has nothing left to treat or the doctor
            // is out of medicine.
            if (entity.HasComponent<TreatmentTarget>())
            {
                ref var tt = ref entity.GetComponent<TreatmentTarget>();
                // Tend needs no medicine (bare-hands = half quality); stabilize
                // requires it. Order persists + re-cycles until nothing's left
                // to treat (or it's cancelled by a move/undraft).
                bool valid = store.TryGetEntityById(tt.PatientEntityId, out var pat)
                    && pat.HasComponent<Health>() && pat.HasComponent<WorldPos>()
                    && (HasTreatableWounds?.Invoke(pat, tt.Stabilize) ?? false) && (!tt.Stabilize || HasMedicine(entity));
                if (!valid) { cb.RemoveComponent<TreatmentTarget>(entity.Id); ClearPath(ref path); }
                else
                {
                    var pp = pat.GetComponent<WorldPos>();
                    var ptile = new TilePos((int)pp.X, (int)pp.Y);
                    bool adjacent = ptile == here
                        || (Math.Abs(ptile.X - here.X) <= 1 && Math.Abs(ptile.Y - here.Y) <= 1);
                    if (adjacent)
                    {
                        ClearPath(ref path);
                        SnapToNearestTile(ref pos, dt, out _, out _);
                        w.Facing = MathF.Atan2(pp.Y - pos.Y, pp.X - pos.X);
                        // Keep the patient put while we work on them.
                        if (pat.HasComponent<Wanderer>())
                            pat.GetComponent<Wanderer>().TendedUntilTick = _tick + TendHoldTicks;
                        if (tt.WorkUntilTick == 0)
                        {
                            long dur = tt.Stabilize ? SimConstants.StabilizeWorkTicks : SimConstants.TendWorkTicks;
                            // Bare-hands tending (no medicine) is 30% slower.
                            if (!tt.Stabilize && !HasMedicine(entity))
                                dur = (long)(dur * SimConstants.BareHandTendWorkMultiplier);
                            tt.WorkStartTick = _tick;
                            tt.WorkUntilTick = _tick + dur;
                        }
                        else if (_tick >= tt.WorkUntilTick)
                        {
                            bool usedMed = ConsumeMedicine(entity); // tend works without; stabilize had it (valid)
                            float quality = usedMed ? SimConstants.TendQualityStub : SimConstants.TendQualityStub * 0.5f;
                            ApplyTreatment?.Invoke(pat, tt.Stabilize, quality);
                            // Keep going if there's more to treat; else done.
                            bool more = (HasTreatableWounds?.Invoke(pat, tt.Stabilize) ?? false)
                                && (!tt.Stabilize || HasMedicine(entity));
                            if (more) tt.WorkUntilTick = 0; // start the next cycle
                            else cb.RemoveComponent<TreatmentTarget>(entity.Id);
                        }
                        return;
                    }
                    bool headingAdj = path.Waypoints is { Count: > 0 }
                        && Math.Abs(path.Waypoints[path.Waypoints.Count - 1].X - ptile.X) <= 1
                        && Math.Abs(path.Waypoints[path.Waypoints.Count - 1].Y - ptile.Y) <= 1;
                    if (!headingAdj && path.PendingPathId == 0)
                    {
                        if (TryPickNeighbor(view, here, ptile, out var approach))
                        {
                            path.Waypoints = null; path.Index = 0;
                            path.PendingPathId = _paths.Request(here, approach);
                        }
                        else cb.RemoveComponent<TreatmentTarget>(entity.Id);
                    }
                    return;
                }
            }

            // Ranged fire order: hold the firing position (never chase). Ease
            // onto the nearest tile on engage; fire while range + LoS hold;
            // if the target slips out, wait in place for it to return (or a
            // new order — move orders clear the fire target).
            if (entity.HasComponent<RangedCombat>()
                && entity.GetComponent<RangedCombat>().TargetEntityId != 0
                && TryGetRangedWeapon(entity, out var rwDef))
            {
                var spec = rwDef.Ranged!;
                int targetId = entity.GetComponent<RangedCombat>().TargetEntityId;
                // A burst, once started, is committed: keep firing its remaining
                // rounds even if the target goes down mid-burst (the target is
                // still in place, so the shots land). Burst mode only.
                var rcNow = entity.GetComponent<RangedCombat>();
                bool burstCommit = rcNow.Mode == Items.FireMode.Burst && rcNow.BurstRemaining > 0;
                // Stop when a conscious target goes down — UNLESS this is a
                // finish-off (ordered on an already-downed pawn) or a committed
                // burst. Mirrors melee.
                bool valid = store.TryGetEntityById(targetId, out var tgt)
                    && tgt.HasComponent<Health>() && tgt.HasComponent<WorldPos>()
                    && (!tgt.GetComponent<Health>().Unconscious
                        || entity.GetComponent<RangedCombat>().FinishOff
                        || burstCommit);
                // Auto-acquired targets drop once they leave the engagement
                // envelope (out of range, or no LoS and no lean) so the pawn
                // re-acquires a fresh one; player-forced targets hold.
                if (valid && entity.GetComponent<RangedCombat>().AutoTarget)
                {
                    var tpw = tgt.GetComponent<WorldPos>();
                    var tt = new TilePos((int)tpw.X, (int)tpw.Y);
                    float ex = tpw.X - pos.X, ey = tpw.Y - pos.Y;
                    bool los = LosClear?.Invoke(here.X, here.Y, tt.X, tt.Y) ?? true;
                    if (MathF.Sqrt(ex * ex + ey * ey) > spec.Range
                        || (!los && !TryFindLeanCell(view, here, tt, out _)))
                        valid = false;
                }
                if (!valid)
                {
                    ref var rc = ref entity.GetComponent<RangedCombat>();
                    // Mid-burst/auto: if shots are still queued + another enemy
                    // is engageable, transfer the spray to it (no re-aim) rather
                    // than stopping. Gated by fire-at-will (off = hold fire).
                    int transfer = (FireAtWill && rc.BurstRemaining > 0)
                        ? PerceiveNearestEnemy(here, spec.Range, view) : 0;
                    if (transfer != 0 && store.TryGetEntityById(transfer, out var nt))
                    {
                        rc.AutoTarget = true; rc.FinishOff = false;
                        RedirectFire(ref rc, transfer, spec.AimTicks);
                        ExecuteRangedFire(entity, nt, spec, ref pos, ref path, ref w, dt, view, here);
                        return;
                    }
                    // Committed burst with the target lost (gone / out of range /
                    // no LoS) + nothing else to shoot: spray the rest toward the
                    // last spot it was seen, then release.
                    if (rc.Mode == Items.FireMode.Burst && rc.BurstRemaining > 0)
                    {
                        FinishBurstBlind(entity, spec, ref pos, ref path, ref w, dt, here);
                        return;
                    }
                    rc.TargetEntityId = 0; rc.BurstRemaining = 0;
                    // fall through to normal drafted hold/move
                }
                else
                {
                    ExecuteRangedFire(entity, tgt, spec, ref pos, ref path, ref w, dt, view, here);
                    return;
                }
            }

            // Melee attack order: close to the target and punch on cadence
            // until it's downed. A move order / new attack clears this
            // (see IssueMoveOrderCommand); un-drafting clears it too.
            if (entity.HasComponent<MeleeTarget>())
            {
                var mt = entity.GetComponent<MeleeTarget>();
                bool valid = store.TryGetEntityById(mt.TargetEntityId, out var tgt)
                    && tgt.HasComponent<Health>() && tgt.HasComponent<WorldPos>()
                    // Stop when the target's merely downed — UNLESS this is a
                    // finishing attack, which presses on until they're dead
                    // (target entity gone → TryGetEntityById fails above).
                    && (!tgt.GetComponent<Health>().Unconscious || mt.FinishOff);
                if (!valid)
                {
                    cb.RemoveComponent<MeleeTarget>(entity.Id);
                    path.Waypoints = null; path.Index = 0;
                    return;
                }
                var tp = tgt.GetComponent<WorldPos>();
                var ttile = new TilePos((int)tp.X, (int)tp.Y);
                bool adjacent = ttile != here
                    && Math.Abs(ttile.X - here.X) <= 1 && Math.Abs(ttile.Y - here.Y) <= 1;
                if (adjacent)
                {
                    path.Waypoints = null; path.Index = 0;
                    w.Facing = MathF.Atan2(tp.Y - pos.Y, tp.X - pos.X); // face the victim
                    if (_tick - mt.LastHitTick >= MeleeAttackInterval)
                    {
                        DoMeleeSwing(entity, tgt);
                        ref var live = ref entity.GetComponent<MeleeTarget>();
                        live.LastHitTick = _tick;
                    }
                    return;
                }
                bool headingAdj = path.Waypoints is { Count: > 0 }
                    && Math.Abs(path.Waypoints[path.Waypoints.Count - 1].X - ttile.X) <= 1
                    && Math.Abs(path.Waypoints[path.Waypoints.Count - 1].Y - ttile.Y) <= 1;
                if (!headingAdj && path.PendingPathId == 0)
                {
                    if (TryPickNeighbor(view, here, ttile, out var approach))
                    {
                        path.Waypoints = null; path.Index = 0;
                        path.PendingPathId = _paths.Request(here, approach);
                    }
                    else cb.RemoveComponent<MeleeTarget>(entity.Id);
                }
                return;
            }

            // Reloading pins the pawn: snap onto the tile + hold until the mag's
            // in, suppressing queued movement. A FRESH move order cancels the
            // reload (IssueMoveOrderCommand clears Reloading) so it repositions
            // instead — and since the rounds only load on completion, that
            // abort can't grant a free reload.
            if (entity.HasComponent<RangedCombat>())
            {
                ref var rcReload = ref entity.GetComponent<RangedCombat>();
                if (rcReload.Reloading)
                {
                    if (_tick >= rcReload.NextActionTick && TryGetRangedWeapon(entity, out var rwPin))
                        CompleteReload(entity, ref rcReload, rwPin.Ranged!); // done — fall through
                    else
                    {
                        if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
                        path.Waypoints = null; path.Index = 0;
                        if (SnapToNearestTile(ref pos, dt, out float rdx, out float rdy)) w.Facing = SouthFacing;
                        else if (rdx * rdx + rdy * rdy > 1e-9f) w.Facing = MathF.Atan2(rdy, rdx);
                        return; // hold while reloading
                    }
                }
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
            // Idle drafted + armed: auto-engage the nearest enemy it can see or
            // lean-peek (ExecuteRangedFire handles the crouch/lean + firing).
            // Marked AutoTarget so it re-acquires when that enemy slips away.
            // Suppressed when "fire at will" is off (only forced targets fire).
            if (FireAtWill && entity.HasComponent<RangedCombat>() && TryGetRangedWeapon(entity, out var autoWdef))
            {
                var autoSpec = autoWdef.Ranged!;
                int foe = PerceiveNearestEnemy(here, autoSpec.Range, view);
                if (foe != 0 && store.TryGetEntityById(foe, out var foeEnt))
                {
                    ref var rcAuto = ref entity.GetComponent<RangedCombat>();
                    rcAuto.TargetEntityId = foe;
                    rcAuto.AutoTarget = true;
                    rcAuto.FinishOff = false;
                    ExecuteRangedFire(entity, foeEnt, autoSpec, ref pos, ref path, ref w, dt, view, here);
                    return;
                }
            }
            // Idle drafted: ease onto the nearest tile, then stand at the
            // ready facing south. While still sliding into place, face the
            // direction of travel so the snap reads naturally.
            if (SnapToNearestTile(ref pos, dt, out float sdx, out float sdy))
                w.Facing = SouthFacing;
            else if (sdx * sdx + sdy * sdy > 1e-9f)
                w.Facing = MathF.Atan2(sdy, sdx);
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

        float sleepLvl = entity.HasComponent<SleepNeed>()
            ? entity.GetComponent<SleepNeed>().Level : 1f;
        bool tired = sleepLvl < SleepStartThreshold;
        bool passedOut = sleepLvl <= 0f;

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

        // 4b. Idle reload: top off an equipped ranged weapon's magazine,
        //     fetching ammo from a nearby pile if the pawn isn't already
        //     carrying some. Its own behavior, not a haul job. Runs only
        //     when otherwise idle (after work jobs, before wander).
        if (TryReloadBehavior(entity, ref path, view, here))
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

        // Spatial local-priority-window claim. Ring out over the job index's
        // chunks from the pawn, collecting a small window of nearby open jobs
        // it's allowed to do; once the window fills, scan one more ring for
        // margin and stop. Then pick the best by (priority, distance) and claim
        // the first reachable one. Priorities are honoured WITHIN the local
        // window (a near important job beats a near trivial one), but a far
        // higher-priority job won't drag the pawn across the map — flat O(local)
        // cost vs scanning every job. Player-pinned jobs win if in the window.
        Span<byte> pri = stackalloc byte[WorkTypes.Count];
        bool anyAllowed = false;
        for (int wt = 0; wt < WorkTypes.Count; wt++)
        {
            byte p = _getPriority(entity, (WorkType)wt);
            if (p > 8) p = 8;
            pri[wt] = p;
            if (p > 0) anyAllowed = true;
        }
        if (!anyAllowed) return false;

        bool carrying = entity.HasComponent<Carrying>();
        var cands = _claimCandidates;
        cands.Clear();

        int shift = JobBoard.ChunkShift;
        int pcx = from.X >> shift, pcy = from.Y >> shift;
        int maxRing = (view.Width >> shift) + 2; // covers the map → never stuck
        for (int r = 0; r <= maxRing; r++)
        {
            bool windowFilledBefore = cands.Count >= ClaimWindow;
            for (int cx = pcx - r; cx <= pcx + r; cx++)
            for (int cy = pcy - r; cy <= pcy + r; cy++)
            {
                if (r > 0 && Math.Abs(cx - pcx) != r && Math.Abs(cy - pcy) != r) continue; // shell only
                for (int wt = 0; wt < WorkTypes.Count; wt++)
                {
                    if (pri[wt] == 0) continue;
                    var list = _jobs.OpenJobsInChunk((WorkType)wt, cx, cy);
                    if (list is null) continue;
                    for (int k = 0; k < list.Count; k++)
                    {
                        var job = list[k];
                        int prio = pri[wt];
                        int bpId = _getJobBlueprintId(job);
                        if (bpId != 0)
                        {
                            int owner = _getBlueprintClaimant(bpId);
                            if (owner != 0)
                            {
                                if (owner != entity.Id) continue;  // pinned to someone else
                                prio = 0;                           // pinned to me → top
                            }
                        }
                        if (job.Kind == JobKind.Haul && carrying) continue;
                        int d = Math.Abs(job.Tile.X - from.X) + Math.Abs(job.Tile.Y - from.Y);
                        cands.Add((job, prio, d));
                    }
                }
            }
            if (windowFilledBefore) break; // filled last ring; this one was the margin
        }
        if (cands.Count == 0) return false;

        cands.Sort(_claimByPriorityThenDist);
        foreach (var c in cands)
        {
            if (!TryFinalizeJob(view, from, c.Job, out var approach)) continue;
            if (!_jobs.TryClaim(c.Job.Id, entity)) continue;
            cb.AddComponent(entity.Id, new BuildTarget { JobId = c.Job.Id });
            if (approach != from) path.PendingPathId = _paths.Request(from, approach);
            return true;
        }
        return false;
    }

    private const int ClaimWindow = 8;
    private readonly List<(Jobs.Job Job, int Priority, int Dist)> _claimCandidates = new();
    private static readonly Comparison<(Jobs.Job Job, int Priority, int Dist)> _claimByPriorityThenDist
        = (a, b) => a.Priority != b.Priority ? a.Priority - b.Priority : a.Dist - b.Dist;

    // Final per-candidate validity + standing tile. Funded/item-block gates +
    // the pathable approach. (Pinned-to-other + haul-while-carrying were already
    // filtered during the ring collect.)
    private bool TryFinalizeJob(MapView view, TilePos from, Jobs.Job job, out TilePos approach)
    {
        approach = default;
        if ((job.Kind == JobKind.WallBuild
             || job.Kind == JobKind.FloorBuild
             || job.Kind == JobKind.DoorBuild
             || job.Kind == JobKind.BedBuild)
            && !_isBlueprintFunded(job.Entity)) return false;
        if ((job.Kind == JobKind.WallBuild
             || job.Kind == JobKind.DoorBuild
             || job.Kind == JobKind.BedBuild)
            && _anyItemAt(job.Tile)) return false;
        bool isHaul = job.Kind == JobKind.Haul;
        bool isFloor = job.Kind == JobKind.FloorBuild || job.Kind == JobKind.FloorDeconstruct;
        bool isRoof = job.Kind == JobKind.RoofBuild || job.Kind == JobKind.RoofRemove;
        bool isLamp = job.Kind == JobKind.LampBuild || job.Kind == JobKind.LampDeconstruct;
        bool isCook = job.Kind == JobKind.Cook;
        if (isHaul || isFloor || isLamp || isCook)
        {
            if (!view.Walkable(job.Tile)) return false;
            approach = job.Tile;
        }
        else if (isRoof)
        {
            if (view.Walkable(job.Tile)) approach = job.Tile;
            else if (TryPickNeighbor(view, from, job.Tile, out var neighbor)) approach = neighbor;
            else return false;
        }
        else
        {
            if (!TryPickNeighbor(view, from, job.Tile, out var neighbor)) return false;
            approach = neighbor;
        }
        return true;
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
                // The item entity can vanish out from under the job (consumed,
                // merged, owner died). Bail safely instead of dereferencing a
                // dead handle.
                if (job.Entity.IsNull || !job.Entity.HasComponent<HaulPayload>())
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

    // Total current carry load = persistent inventory + in-transit haul
    // cargo. Used to clamp a pick-up to remaining capacity.
    private static void CurrentCarryLoad(Entity carrier, out float w, out float b)
    {
        InventoryLoad(carrier, out w, out b);
        if (carrier.HasComponent<Carrying>())
        {
            var c = carrier.GetComponent<Carrying>();
            if (c.Slots is not null)
                foreach (var s in c.Slots)
                    if (ItemCatalog.ItemsByPath.TryGetValue(s.ItemPath, out var d)) { w += d.Weight * s.Count; b += d.Bulk * s.Count; }
        }
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
        var candidates = _topoffCandidates;
        candidates.Clear();
        if (!string.IsNullOrEmpty(primaryPath))
        {
            (_itemPileQ ??= store.Query<ItemPile>()).ForEachEntity((ref ItemPile p, Entity e) =>
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
        candidates.Sort(_topoffByDist);

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

    // First equipped ranged weapon on the pawn, if any.
    private static bool TryGetRangedWeapon(Entity entity, out Items.ItemDef def)
    {
        def = null!;
        if (!entity.HasComponent<Inventory>()) return false;
        var inv = entity.GetComponent<Inventory>();
        if (inv.Equipped is null) return false;
        foreach (var eq in inv.Equipped)
            if (Items.ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var d) && d.IsRangedWeapon)
            {
                def = d;
                return true;
            }
        return false;
    }

    private static Items.FireMode DefaultFireMode(Items.RangedSpec spec)
    {
        if (spec.Modes.HasFlag(Items.FireModeFlags.Single)) return Items.FireMode.Single;
        if (spec.Modes.HasFlag(Items.FireModeFlags.Burst)) return Items.FireMode.Burst;
        return Items.FireMode.Auto;
    }

    private static int ShotsForMode(Items.FireMode mode, Items.RangedSpec spec) => mode switch
    {
        Items.FireMode.Single => 1,
        Items.FireMode.Burst => Math.Max(1, spec.BurstShots),
        Items.FireMode.Auto => int.MaxValue, // fire until the mag runs dry, then reload
        _ => 1,
    };

    // Run one tick of the firing state machine: reload when dry, gate on
    // cooldowns, run the warmup + burst cadence, emit a bullet per shot.
    // ─── Enemy AI (goal-oriented hostile brain) ──────────────────────────
    private const long EnemyThinkInterval = 15;     // ticks between re-perceive/re-plan
    private const float EnemySightRange = 28f;        // tiles — FLOOR; actual sight = max(this, weapon range)
    private const float EnemyRetreatBloodThreshold = 0.45f;
    private const float EnemyRetreatMovingThreshold = 0.40f;
    private const int EnemyFleeDist = 12;             // tiles toward the edge when retreating
    private const int EnemyCoverSearchRadius = 7;     // tiles around the standoff point to scan for firing cells
    private const float EnemyOpenExposurePenalty = 1000f; // open cells score far worse than covered ones
    private const float EngagePreferredFraction = 0.7f;   // preferred standoff = weapon range x this (short gun → push, long → hold)
    private const float EnemyRangePreferenceWeight = 2f;  // how hard to prefer cells near the standoff distance
    private const float EnemyLosAvoidPenalty = 6f;    // extra A* step cost for a tile in colonist LOS
    private const float EnemyCaughtInOpenDist = 12f;  // exposed + target within this → drop cover, shoot

    // Tiles colonists can see (threat field), supplied by SimRuntime. Enemies
    // weight their pathing to avoid these + open fire if caught in one near a
    // target. Immutable snapshot — safe to hand to the path workers.
    public System.Func<IReadOnlySet<TilePos>?>? ColonistLosProvider;
    // Per-tile light (0..1) for the darkness accuracy debuff. Null => fully lit.
    public System.Func<int, int, float>? LightProvider;
    // Global "fire at will": when false, idle drafted colonists do NOT
    // auto-acquire/peek enemies — they only fire at a player-forced target.
    public bool FireAtWill = true;
    // Medical: check a patient still has wounds this mode can treat / apply the
    // treatment. Wired to SimRuntime.
    public System.Func<Entity, bool, bool>? HasTreatableWounds;
    public System.Action<Entity, bool, float>? ApplyTreatment;

    // (id, x, y) of every conscious non-enemy pawn, rebuilt once per Step.
    private readonly List<(int Id, float X, float Y)> _colonistTargets = new();
    // Conscious enemies (id + pos), rebuilt each Step for drafted-colonist
    // auto-engagement (the mirror of _colonistTargets, which enemies perceive).
    private readonly List<(int Id, float X, float Y)> _enemyTargets = new();
    private ArchetypeQuery<WorldPos, Wanderer, Health>? _colonistTargetsQ;

    // The hostile brain. Selects a goal on a stagger (perception is the
    // expensive bit) then executes it every tick. Goals are dispatched by
    // kind so new intents (steal, destroy, …) slot in as a new kind + a
    // selection rule + a handler — no rewrite of the firing/movement core.
    private void PlanEnemy(ref WorldPos pos, ref PathFollower path, ref Wanderer w, float dt, Entity entity, CommandBuffer cb, MapView view, EntityStore store, TilePos here)
    {
        // Resolve any in-flight path request first (mirrors the colonist path).
        if (path.PendingPathId != 0)
        {
            if (!_paths.TryConsume(path.PendingPathId, out var result)) return; // still pending
            path.PendingPathId = 0;
            if (result.Status == PathStatus.Found && result.Path is { Count: > 0 })
            {
                path.Waypoints = result.Path;
                path.Index = result.Path[0] == here ? 1 : 0;
            }
            else { path.Waypoints = null; path.Index = 0; }
        }

        if (!entity.HasComponent<EnemyBrain>()) return;

        // Downed hostiles freeze (mirrors the colonist unconscious gate) —
        // including sliding onto the nearest tile as they collapse, so the body
        // lands on a tile centre instead of frozen mid-step.
        if (entity.HasComponent<Health>() && entity.GetComponent<Health>().Unconscious)
        {
            ClearPath(ref path);
            SnapToNearestTile(ref pos, dt, out _, out _);
            return;
        }

        ref var brain = ref entity.GetComponent<EnemyBrain>();

        // Crouch when hugging a sandbag (same as a drafted colonist) so the
        // lowered hitbox + crouch visual apply while firing over cover.
        w.Crouched = IsAdjacentToSandbag(here);

        // Per-tick ranged housekeeping (the colonist Plan does this for drafted
        // pawns; enemies skip that path, so do it here): finish reloads, settle
        // recoil, clear the stance until the fire step re-asserts it.
        if (entity.HasComponent<RangedCombat>())
        {
            ref var rc0 = ref entity.GetComponent<RangedCombat>();
            if (rc0.Reloading && _tick >= rc0.NextActionTick && TryGetRangedWeapon(entity, out var rwReload))
                CompleteReload(entity, ref rc0, rwReload.Ranged!); // insert-mag phase
            if (rc0.Recoil > 0f && TryGetRangedWeapon(entity, out var wd0))
                rc0.Recoil = MathF.Max(0f, rc0.Recoil - wd0.Ranged!.RecoilRecoverPerSec * dt);
            rc0.Stance = CoverStance.None;
            rc0.Leaning = false;
        }

        // ── Think (staggered): perceive + pick a goal ONLY. Movement is
        // issued in the per-tick execution below — re-requesting a path every
        // think (and discarding the one mid-walk) made the pawn stutter in
        // place instead of committing to a route. ──
        if (_tick >= brain.NextThinkTick)
        {
            brain.NextThinkTick = _tick + EnemyThinkInterval;
            bool hurt = entity.HasComponent<Health>()
                && (entity.GetComponent<Health>().BloodLevel < EnemyRetreatBloodThreshold
                    || entity.GetComponent<Health>().Moving < EnemyRetreatMovingThreshold);
            // See at least as far as the weapon can shoot — a 50-tile rifle
            // that only perceives at 28 would close to within a third of its
            // reach before engaging. Floor at the base sight for short/no arms.
            float sight = EnemySightRange;
            if (TryGetRangedWeapon(entity, out var wdefSight))
                sight = MathF.Max(EnemySightRange, wdefSight.Ranged!.Range);
            brain.TargetEntityId = PerceiveNearestColonist(here, sight);
            // Remember where a seen target is, so losing sight flips us to Hunt
            // (push to last-seen) rather than instantly forgetting.
            if (brain.TargetEntityId != 0
                && store.TryGetEntityById(brain.TargetEntityId, out var seen) && seen.HasComponent<WorldPos>())
            {
                var sp = seen.GetComponent<WorldPos>();
                brain.LastSeenX = (int)sp.X; brain.LastSeenY = (int)sp.Y; brain.HasLastSeen = true;
            }
            else if (brain.HasLastSeen && Arrived(here, brain.LastSeenX, brain.LastSeenY))
            {
                brain.HasLastSeen = false; // reached the last-known spot, nobody there → give up
            }

            // Locked in melee (a colonist in our face) trumps everything — can't
            // shoot point-blank, so swing back. Then the usual reflexes: Retreat
            // when hurt, Engage on sight, Hunt a last-known spot, else the mission.
            int meleeFoe = PerceiveAdjacentColonist(here);
            if (meleeFoe != 0) { brain.Goal = EnemyGoalKind.Melee; brain.TargetEntityId = meleeFoe; }
            else if (hurt) brain.Goal = EnemyGoalKind.Retreat;
            else if (brain.TargetEntityId != 0) brain.Goal = EnemyGoalKind.Engage;
            else if (brain.HasLastSeen)
            {
                brain.GoalTileX = brain.LastSeenX; brain.GoalTileY = brain.LastSeenY; brain.HasGoalTile = true;
                brain.Goal = EnemyGoalKind.Hunt;
            }
            else brain.Goal = StepMission(ref brain, here);

            // For Engage, hold a committed firing position chosen by exposure
            // (cover) scoring. Re-pick only when we lack one or it went stale.
            if (brain.Goal == EnemyGoalKind.Engage) UpdateFireCell(ref brain, here, entity, view, store);
            else brain.HasFireCell = false;
        }

        // ── Execute every tick. Path requests only fire when there's no route
        // already in flight or being followed (see EnsurePathTo), so the pawn
        // commits to its cover/route instead of re-pathing each think. ──
        switch (brain.Goal)
        {
            case EnemyGoalKind.Engage:
                TickEngage(ref pos, ref path, ref w, dt, entity, view, store, here, ref brain);
                break;
            case EnemyGoalKind.Retreat:
                TickRetreat(ref path, here, store, view, brain.TargetEntityId);
                break;
            case EnemyGoalKind.Advance:
            case EnemyGoalKind.Assault:
            case EnemyGoalKind.Hunt:
                TickAdvance(ref path, here, in brain); // all march toward GoalTile
                break;
            case EnemyGoalKind.Melee:
                TickMelee(ref pos, ref path, ref w, entity, store, here, ref brain);
                break;
            case EnemyGoalKind.Hold:
                ClearPath(ref path); // posted up — stand watch (Engage interrupts if a target appears)
                break;
            case EnemyGoalKind.Exfil:
                TickExfil(ref path, here, entity, cb);
                break;
            default:
                ClearPath(ref path);
                break;
        }
    }

    // Resolve the current mission objective into a concrete goal for this
    // think, advancing the queue as steps complete. Runs only when no combat
    // reflex is active. A null/empty mission falls back to the legacy "advance
    // toward map centre + hunt" behaviour. The loop (not recursion) drains any
    // already-satisfied steps in one think; capped so a Patrol of co-located
    // points can't spin forever.
    private EnemyGoalKind StepMission(ref EnemyBrain brain, TilePos here)
    {
        var mission = brain.Mission;
        if (mission is null || mission.Count == 0)
        {
            brain.HasGoalTile = false; // no objective tile → TickAdvance uses map centre
            return EnemyGoalKind.Advance;
        }

        int guard = mission.Count * 2 + 2;
        while (guard-- > 0)
        {
            if (brain.MissionIndex >= mission.Count)
                return EnemyGoalKind.None; // mission complete → idle

            var obj = mission[brain.MissionIndex];
            switch (obj.Kind)
            {
                case EnemyObjectiveKind.AdvanceTo:
                    brain.GoalTileX = obj.TileX; brain.GoalTileY = obj.TileY; brain.HasGoalTile = true;
                    if (Arrived(here, obj.TileX, obj.TileY)) { AdvanceMission(ref brain); continue; }
                    return EnemyGoalKind.Advance;

                case EnemyObjectiveKind.Hold:
                    brain.GoalTileX = obj.TileX; brain.GoalTileY = obj.TileY; brain.HasGoalTile = true;
                    if (!Arrived(here, obj.TileX, obj.TileY)) return EnemyGoalKind.Advance; // walk there first
                    if (brain.PhaseStartTick == 0) brain.PhaseStartTick = _tick;
                    if (obj.Param > 0 && _tick - brain.PhaseStartTick >= obj.Param)
                    { brain.PhaseStartTick = 0; AdvanceMission(ref brain); continue; }
                    return EnemyGoalKind.Hold;

                case EnemyObjectiveKind.Patrol:
                    brain.MissionIndex = 0; brain.PhaseStartTick = 0;
                    // If the first step is already satisfied the guard stops the spin.
                    continue;

                case EnemyObjectiveKind.Exfil:
                    return EnemyGoalKind.Exfil;

                case EnemyObjectiveKind.Assault:
                    // Hunt the colony: steer toward the living-colonist centroid
                    // (recomputed each think so we chase them as they move). The
                    // assault is done once no conscious colonists remain.
                    if (!TryColonyAnchor(out var anchor)) { AdvanceMission(ref brain); continue; }
                    brain.GoalTileX = anchor.X; brain.GoalTileY = anchor.Y; brain.HasGoalTile = true;
                    return EnemyGoalKind.Assault;

                default:
                    AdvanceMission(ref brain); continue;
            }
        }
        // Couldn't settle (e.g. degenerate patrol) — just hold where we are.
        return EnemyGoalKind.Hold;
    }

    private void AdvanceMission(ref EnemyBrain brain)
    {
        brain.MissionIndex++;
        brain.PhaseStartTick = 0;
        brain.HasFireCell = false;
    }

    private const int EnemyArriveRadius = 2;
    private static bool Arrived(TilePos here, int x, int y)
        => Math.Abs(here.X - x) <= EnemyArriveRadius && Math.Abs(here.Y - y) <= EnemyArriveRadius;

    // Exfil: head for the nearest map edge along the LOS-avoiding path; once on
    // the perimeter, despawn (deferred via the command buffer — a structural
    // change can't happen inside the controller's query loop).
    private void TickExfil(ref PathFollower path, TilePos here, Entity entity, CommandBuffer cb)
    {
        // The map border (tile 0 / MapSize-1) is non-walkable, so the outermost
        // reachable ring is tile 1 / MapSize-2 — that's "the edge" for exfil.
        const int lo = 1;
        int hi = SimConstants.MapSize - 2;
        if (here.X <= lo || here.Y <= lo || here.X >= hi || here.Y >= hi)
        {
            ClearPath(ref path);
            cb.DeleteEntity(entity.Id);
            return;
        }
        // Head for the nearest edge tile on the same row/column.
        int distLeft = here.X - lo, distRight = hi - here.X, distTop = here.Y - lo, distBottom = hi - here.Y;
        int min = Math.Min(Math.Min(distLeft, distRight), Math.Min(distTop, distBottom));
        TilePos dest = min == distLeft ? new TilePos(lo, here.Y)
            : min == distRight ? new TilePos(hi, here.Y)
            : min == distTop ? new TilePos(here.X, lo)
            : new TilePos(here.X, hi);
        EnsurePathTo(ref path, here, dest);
    }

    // Advance: no target in sight — march toward the goal destination (the
    // brain's assigned tile, else map centre), dodging colonist LOS via the
    // weighted path. Perception flips us to Engage the moment a colonist
    // comes into view. Holds once arrived.
    private void TickAdvance(ref PathFollower path, TilePos here, in EnemyBrain brain)
    {
        int center = SimConstants.MapSize / 2;
        var dest = brain.HasGoalTile ? new TilePos(brain.GoalTileX, brain.GoalTileY) : new TilePos(center, center);
        if (Math.Abs(here.X - dest.X) <= 2 && Math.Abs(here.Y - dest.Y) <= 2) { ClearPath(ref path); return; }
        EnsurePathTo(ref path, here, dest);
    }

    // Centroid of all living (conscious, non-enemy) colonists — the colony mass
    // a raid pushes toward. False when none remain (raid won).
    private bool TryColonyAnchor(out TilePos anchor)
    {
        anchor = default;
        if (_colonistTargets.Count == 0) return false;
        float sx = 0f, sy = 0f;
        foreach (var t in _colonistTargets) { sx += t.X; sy += t.Y; }
        anchor = new TilePos((int)(sx / _colonistTargets.Count), (int)(sy / _colonistTargets.Count));
        return true;
    }

    // Nearest conscious non-enemy pawn within `sight` tiles (squared compare), or 0.
    private int PerceiveNearestColonist(TilePos here, float sight)
    {
        int best = 0;
        float bestD2 = sight * sight;
        float hx = here.X + 0.5f, hy = here.Y + 0.5f;
        foreach (var t in _colonistTargets)
        {
            float dx = t.X - hx, dy = t.Y - hy;
            float d2 = dx * dx + dy * dy;
            if (d2 >= bestD2) continue;
            // LoS-gated: no shooting through walls, and no ESP either — the
            // enemy only "sees" a colonist it has a clear line to (reuses the
            // same LoS the firing code uses).
            if (LosClear is not null && !LosClear(here.X, here.Y, (int)t.X, (int)t.Y)) continue;
            bestD2 = d2; best = t.Id;
        }
        return best;
    }

    // Nearest conscious enemy within `sight` that an idle drafted colonist can
    // engage — direct LoS OR a lean-peek opens a shot. For auto-engagement.
    private int PerceiveNearestEnemy(TilePos here, float sight, MapView view)
    {
        int best = 0;
        float bestD2 = sight * sight;
        float hx = here.X + 0.5f, hy = here.Y + 0.5f;
        foreach (var t in _enemyTargets)
        {
            float dx = t.X - hx, dy = t.Y - hy;
            float d2 = dx * dx + dy * dy;
            if (d2 >= bestD2) continue;
            var et = new TilePos((int)t.X, (int)t.Y);
            bool los = LosClear?.Invoke(here.X, here.Y, et.X, et.Y) ?? true;
            if (!los && !TryFindLeanCell(view, here, et, out _)) continue; // can't see or peek it
            bestD2 = d2; best = t.Id;
        }
        return best;
    }

    // One melee swing: lunge anim on the attacker, engage-slow the victim, then
    // hit-or-miss (flinch + chance to stun on a hit). Shared by the drafted
    // colonist melee order and the enemy melee-back reflex.
    private void DoMeleeSwing(Entity attacker, Entity victim)
    {
        if (attacker.HasComponent<Combat>())
        { ref var ac = ref attacker.GetComponent<Combat>(); ac.SwingTick = _tick; }
        if (victim.HasComponent<Combat>())
        { ref var tc = ref victim.GetComponent<Combat>(); tc.EngagedUntil = _tick + MeleeEngagedTicks; }

        if (_rng.NextDouble() >= MeleeMissChance)
        {
            MeleeHit?.Invoke(attacker.Id, victim.Id);
            if (victim.HasComponent<Combat>())
            {
                ref var tc = ref victim.GetComponent<Combat>();
                tc.FlinchTick = _tick;
                if (_rng.NextDouble() < MeleeStunChance) tc.StunUntil = _tick + MeleeStunTicks;
            }
        }
        else if (attacker.HasComponent<Combat>())
        {
            ref var ac = ref attacker.GetComponent<Combat>(); ac.MissTick = _tick;
        }
    }

    private const float EnemyMeleeReach = 1.5f; // tiles — covers adjacent + same-tile

    // Nearest conscious colonist within melee reach of `here`, or 0. The enemy
    // is "locked in melee" when one is this close — it can't bring the gun to
    // bear, so it swings back.
    private int PerceiveAdjacentColonist(TilePos here)
    {
        int best = 0;
        float bestD2 = EnemyMeleeReach * EnemyMeleeReach;
        float hx = here.X + 0.5f, hy = here.Y + 0.5f;
        foreach (var t in _colonistTargets)
        {
            float dx = t.X - hx, dy = t.Y - hy;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; best = t.Id; }
        }
        return best;
    }

    // Melee: a colonist is in our face — face it and swing on the melee cadence
    // (gated off Combat.SwingTick). No shooting while locked in.
    private void TickMelee(ref WorldPos pos, ref PathFollower path, ref Wanderer w, Entity entity, EntityStore store, TilePos here, ref EnemyBrain brain)
    {
        ClearPath(ref path);
        if (!store.TryGetEntityById(brain.TargetEntityId, out var tgt) || !tgt.HasComponent<WorldPos>()) return;
        var tp = tgt.GetComponent<WorldPos>();
        float dx = tp.X - pos.X, dy = tp.Y - pos.Y;
        if (dx * dx + dy * dy > 1e-9f) w.Facing = MathF.Atan2(dy, dx);
        if (entity.HasComponent<Combat>())
        {
            ref var ac = ref entity.GetComponent<Combat>();
            if (_tick - ac.SwingTick >= MeleeAttackInterval) DoMeleeSwing(entity, tgt);
        }
    }

    // Engage: walk to the committed firing position (a low-exposure cover
    // cell chosen on the think) and fire from it. If none's been found yet,
    // close on the target so cover comes into reach. ExecuteRangedFire handles
    // the actual crouch/lean once posted up.
    private void TickEngage(ref WorldPos pos, ref PathFollower path, ref Wanderer w, float dt, Entity entity, MapView view, EntityStore store, TilePos here, ref EnemyBrain brain)
    {
        int targetId = brain.TargetEntityId;
        if (targetId == 0
            || !TryGetRangedWeapon(entity, out var wdef)
            || !entity.HasComponent<RangedCombat>()
            || !store.TryGetEntityById(targetId, out var tgt)
            || !tgt.HasComponent<Health>() || !tgt.HasComponent<WorldPos>())
        {
            // Target vanished mid-burst: spray the rest toward its last spot.
            if (entity.HasComponent<RangedCombat>() && TryGetRangedWeapon(entity, out var lostGun))
            {
                ref var rcL = ref entity.GetComponent<RangedCombat>();
                if (rcL.Mode == Items.FireMode.Burst && rcL.BurstRemaining > 0)
                {
                    FinishBurstBlind(entity, lostGun.Ranged!, ref pos, ref path, ref w, dt, here);
                    return;
                }
            }
            return; // nothing valid to fight
        }
        var spec = wdef.Ranged!;
        // Target down: transfer the spray to a fresh colonist if mid-burst (no
        // re-aim). With no fresh target, a committed burst finishes its rounds
        // into the downed target; otherwise stop (the next think re-plans).
        if (tgt.GetComponent<Health>().Unconscious)
        {
            ref var rcE = ref entity.GetComponent<RangedCombat>();
            bool burstCommit = rcE.Mode == Items.FireMode.Burst && rcE.BurstRemaining > 0;
            float sight = MathF.Max(EnemySightRange, spec.Range);
            int nt = rcE.BurstRemaining > 0 ? PerceiveNearestColonist(here, sight) : 0;
            if (nt != 0 && store.TryGetEntityById(nt, out var fresh))
            {
                tgt = fresh;
                brain.TargetEntityId = nt;
                RedirectFire(ref rcE, nt, spec.AimTicks);
            }
            else if (!burstCommit)
            {
                return; // no transfer + not a committed burst → stop
            }
            // committed burst, no transfer: keep firing into the downed target
        }

        // Caught in the open: if we're standing in a colonist sightline and a
        // target is close with a clear shot, frick the cover-seek and just
        // open fire from here.
        var los = ColonistLosProvider?.Invoke();
        if (los is not null && los.Contains(here))
        {
            var tpw0 = tgt.GetComponent<WorldPos>();
            EffectivePos(tgt, tpw0.X, tpw0.Y, out float epx, out float epy); // aim at the peek when leaning
            float ex = epx - pos.X, ey = epy - pos.Y;
            float edist = MathF.Sqrt(ex * ex + ey * ey);
            var et = new TilePos((int)epx, (int)epy);
            if (edist <= EnemyCaughtInOpenDist && edist >= SimConstants.RangedMinFireRange && edist <= spec.Range
                && (LosClear?.Invoke(here.X, here.Y, et.X, et.Y) ?? true))
            {
                brain.HasFireCell = false;
                entity.GetComponent<RangedCombat>().TargetEntityId = targetId;
                ExecuteRangedFire(entity, tgt, spec, ref pos, ref path, ref w, dt, view, here);
                return;
            }
        }

        if (brain.HasFireCell)
        {
            var fc = new TilePos(brain.FireCellX, brain.FireCellY);
            if (here == fc)
            {
                // Settle onto the cell CENTER before peeking. The pawn arrives
                // at the tile edge off the path; firing immediately would pop
                // the lean/peek (a ~tile shift) before it's actually in place —
                // that's the "snaps a distance" bug. Ease in first, facing the
                // target, and only fire once centered.
                if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
                path.Waypoints = null; path.Index = 0;
                var tpw = tgt.GetComponent<WorldPos>();
                float fdx = tpw.X - pos.X, fdy = tpw.Y - pos.Y;
                if (fdx * fdx + fdy * fdy > 1e-9f) w.Facing = MathF.Atan2(fdy, fdx);
                if (!SnapToNearestTile(ref pos, dt, out _, out _)) return; // still easing in
                entity.GetComponent<RangedCombat>().TargetEntityId = targetId;
                ExecuteRangedFire(entity, tgt, spec, ref pos, ref path, ref w, dt, view, here);
            }
            else
            {
                EnsurePathTo(ref path, here, fc);
            }
        }
        else
        {
            var tpw0 = tgt.GetComponent<WorldPos>();
            EffectivePos(tgt, tpw0.X, tpw0.Y, out float cex, out float cey);
            EnsurePathTo(ref path, here, new TilePos((int)cex, (int)cey)); // close in to bring cover into reach
        }
    }

    // Keep a committed firing position. Reuse the current one while it's still
    // valid (in range + has a shot); otherwise pick the lowest-exposure cell.
    private void UpdateFireCell(ref EnemyBrain brain, TilePos here, Entity entity, MapView view, EntityStore store)
    {
        if (!TryGetRangedWeapon(entity, out var wdef)
            || !store.TryGetEntityById(brain.TargetEntityId, out var tgt)
            || !tgt.HasComponent<WorldPos>())
        {
            brain.HasFireCell = false;
            return;
        }
        var spec = wdef.Ranged!;
        // Reason about the target's EFFECTIVE position (its peek cell while
        // leaning) — else a colonist peeking from behind a wall reads as having
        // no clear line and the enemy never engages, just keeps closing.
        var tw0 = tgt.GetComponent<WorldPos>();
        EffectivePos(tgt, tw0.X, tw0.Y, out float twx, out float twy);
        var tw = new WorldPos { X = twx, Y = twy };
        var ttile = new TilePos((int)tw.X, (int)tw.Y);
        float dx = tw.X - (here.X + 0.5f), dy = tw.Y - (here.Y + 0.5f);
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        // Already able to shoot from here → fire in place. Don't relocate to
        // hunt for cover (no sprinting across open gaps when you can already
        // engage — only reposition when you CAN'T shoot from here).
        bool losHere = LosClear?.Invoke(here.X, here.Y, ttile.X, ttile.Y) ?? true;
        if (dist <= spec.Range && dist >= SimConstants.RangedMinFireRange && losHere)
        {
            brain.FireCellX = here.X; brain.FireCellY = here.Y; brain.HasFireCell = true;
            return;
        }

        // Can't shoot from here → reposition. Aim for the weapon's preferred
        // standoff distance (short range → push in close, long range → hold
        // far), and look for cover AT that standoff point on our side of the
        // target rather than anywhere near it.
        if (brain.HasFireCell
            && FiringCellValid(new TilePos(brain.FireCellX, brain.FireCellY), ttile, spec, view))
        {
            return; // committed cell still valid
        }
        float preferred = spec.Range * EngagePreferredFraction;
        // Only ever CLOSE toward the preferred distance — never back away. If
        // the pawn is already nearer than preferred, hold that distance (it
        // can fight from closer); just floor it above point-blank.
        float desired = MathF.Max(SimConstants.RangedMinFireRange + 1f, MathF.Min(preferred, dist));
        float ux = (here.X + 0.5f) - tw.X, uy = (here.Y + 0.5f) - tw.Y;
        float ulen = MathF.Sqrt(ux * ux + uy * uy);
        if (ulen < 1e-3f) { ux = 1f; uy = 0f; ulen = 1f; }
        var standoff = new TilePos(
            (int)(tw.X + ux / ulen * desired),
            (int)(tw.Y + uy / ulen * desired));
        if (FindBestFiringCell(here, ttile, standoff, desired, spec, view, out var cell))
        {
            brain.FireCellX = cell.X; brain.FireCellY = cell.Y; brain.HasFireCell = true;
        }
        else
        {
            brain.HasFireCell = false; // nothing reachable yet — close in
        }
    }

    // A cell can fire on the target if it's in weapon range with either a
    // direct line or a corner-lean shot.
    private bool FiringCellValid(TilePos cell, TilePos target, Items.RangedSpec spec, MapView view)
    {
        float dx = target.X - cell.X, dy = target.Y - cell.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > spec.Range || dist < SimConstants.RangedMinFireRange) return false;
        bool directLos = LosClear?.Invoke(cell.X, cell.Y, target.X, target.Y) ?? true;
        return directLos || TryFindLeanCell(view, cell, target, out _);
    }

    // Lowest-exposure firing cell near the target: covered cells (a wall/
    // sandbag between the cell and the threat) beat open ones by a large
    // margin; travel distance from the pawn tie-breaks. Returns false when no
    // in-range cell with a shot exists in the search window (→ close in).
    // Lowest-exposure firing cell around `searchCenter` (the standoff point at
    // the weapon's preferred distance, on the pawn's side of the target).
    // Score: cover beats open by a big margin, then nearness to the preferred
    // engagement distance, then travel from the pawn. Cells must be in weapon
    // range with a direct or toward-target corner-lean shot.
    private bool FindBestFiringCell(TilePos here, TilePos target, TilePos searchCenter, float preferred, Items.RangedSpec spec, MapView view, out TilePos best)
    {
        best = here;
        float bestScore = float.MaxValue;
        bool found = false;
        int r = EnemyCoverSearchRadius;
        for (int dy = -r; dy <= r; dy++)
        for (int dx = -r; dx <= r; dx++)
        {
            int cx = searchCenter.X + dx, cy = searchCenter.Y + dy;
            if (!view.Walkable(cx, cy)) continue;
            var c = new TilePos(cx, cy);
            float tdx = target.X - cx, tdy = target.Y - cy;
            float dist = MathF.Sqrt(tdx * tdx + tdy * tdy);
            if (dist > spec.Range || dist < SimConstants.RangedMinFireRange) continue;
            bool directLos = LosClear?.Invoke(cx, cy, target.X, target.Y) ?? true;
            if (!directLos && !TryFindLeanCell(view, c, target, out _)) continue;
            float exposure = CoverToward(c, target, view) ? 0f : EnemyOpenExposurePenalty;
            float rangeMiss = MathF.Abs(dist - preferred) * EnemyRangePreferenceWeight;
            float travel = Math.Abs(cx - here.X) + Math.Abs(cy - here.Y);
            float score = exposure + rangeMiss + travel;
            if (score < bestScore) { bestScore = score; best = c; found = true; }
        }
        return found;
    }

    // True when a wall or sandbag sits in the cardinal step(s) from `cell`
    // toward `threat` — i.e. the cell is shielded from that direction.
    private bool CoverToward(TilePos cell, TilePos threat, MapView view)
    {
        int sx = Math.Sign(threat.X - cell.X), sy = Math.Sign(threat.Y - cell.Y);
        if (sx != 0 && (view.GetWall(cell.X + sx, cell.Y) != WallType.None
                        || (HasSandbag?.Invoke(cell.X + sx, cell.Y) ?? false))) return true;
        if (sy != 0 && (view.GetWall(cell.X, cell.Y + sy) != WallType.None
                        || (HasSandbag?.Invoke(cell.X, cell.Y + sy) ?? false))) return true;
        return false;
    }

    // Retreat: head EnemyFleeDist tiles away from the threat. Only pick a new
    // flee destination once the current route runs out (no per-tick re-route).
    private void TickRetreat(ref PathFollower path, TilePos here, EntityStore store, MapView view, int threatId)
    {
        bool hasActivePath = path.Waypoints is { Count: > 0 } && path.Index < path.Waypoints.Count;
        if (hasActivePath || path.PendingPathId != 0) return;
        float ax = 0f, ay = 0f;
        if (threatId != 0 && store.TryGetEntityById(threatId, out var th) && th.HasComponent<WorldPos>())
        {
            var tp = th.GetComponent<WorldPos>();
            ax = (here.X + 0.5f) - tp.X; ay = (here.Y + 0.5f) - tp.Y;
        }
        float len = MathF.Sqrt(ax * ax + ay * ay);
        if (len < 1e-3f) { ax = 0f; ay = -1f; } else { ax /= len; ay /= len; }
        int fx = here.X + (int)MathF.Round(ax * EnemyFleeDist);
        int fy = here.Y + (int)MathF.Round(ay * EnemyFleeDist);
        if (TryNearestWalkable(view, fx, fy, out var fleeTile) && fleeTile != here)
            path.PendingPathId = _paths.Request(here, fleeTile, ColonistLosProvider?.Invoke(), EnemyLosAvoidPenalty);
    }

    // Request a route to `dest` ONLY if the pawn isn't already following one
    // (or waiting on one). Committing to the path is what stops the stutter.
    private void EnsurePathTo(ref PathFollower path, TilePos here, TilePos dest)
    {
        bool hasActivePath = path.Waypoints is { Count: > 0 } && path.Index < path.Waypoints.Count;
        if (hasActivePath || path.PendingPathId != 0 || dest == here) return;
        // Weight the route to dodge colonist sightlines (still passes through
        // if there's no way around).
        path.PendingPathId = _paths.Request(here, dest, ColonistLosProvider?.Invoke(), EnemyLosAvoidPenalty);
    }

    // A pawn's effective hitbox/aim position: the peek cell while it's popped
    // out leaning around cover, otherwise its body position. Mirrors the
    // hitbox shift in SimRuntime.GatherProjPawns so perception + aim line up
    // with where rounds actually connect.
    private static void EffectivePos(Entity e, float bodyX, float bodyY, out float x, out float y)
    {
        x = bodyX; y = bodyY;
        if (e.HasComponent<RangedCombat>())
        {
            var rc = e.GetComponent<RangedCombat>();
            if (rc.Stance == CoverStance.Popped && rc.Leaning)
            {
                // Part-way to the peek cell — matches the rendered lean + the
                // hitbox shift in GatherProjPawns.
                x = bodyX + (rc.PeekX - bodyX) * SimConstants.LeanPeekFraction;
                y = bodyY + (rc.PeekY - bodyY) * SimConstants.LeanPeekFraction;
            }
        }
    }

    // Fraction of the full aim time it takes to swing a running burst onto a
    // fresh target — quicker than a cold acquire (already shouldered), but not
    // instant (instant snapping between targets is too strong).
    private const float TransferReaimFraction = 0.5f;

    // Hand a still-running burst to a new target with a short re-aim.
    private void RedirectFire(ref RangedCombat rc, int newTarget, long aimTicks)
    {
        rc.TargetEntityId = newTarget;
        rc.AimTargetId = newTarget;
        rc.AimReadyTick = _tick + (long)(aimTicks * TransferReaimFraction);
        rc.LastAimTick = _tick;
    }

    private static bool HasMedicine(Entity e)
    {
        if (!e.HasComponent<Inventory>()) return false;
        var inv = e.GetComponent<Inventory>();
        if (inv.Items is null) return false;
        foreach (var s in inv.Items)
            if (s.Count > 0 && s.ItemPath == Items.ItemCatalog.Medicine.FullPath) return true;
        return false;
    }

    // Pull one Medicine from the doctor's inventory. True if consumed.
    private static bool ConsumeMedicine(Entity e)
    {
        if (!e.HasComponent<Inventory>()) return false;
        ref var inv = ref e.GetComponent<Inventory>();
        if (inv.Items is null) return false;
        for (int k = 0; k < inv.Items.Count; k++)
        {
            var s = inv.Items[k];
            if (s.ItemPath != Items.ItemCatalog.Medicine.FullPath || s.Count <= 0) continue;
            s.Count--;
            if (s.Count <= 0) inv.Items.RemoveAt(k); else inv.Items[k] = s;
            return true;
        }
        return false;
    }

    private void ClearPath(ref PathFollower path)
    {
        if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
        path.Waypoints = null; path.Index = 0;
    }

    // Spiral out from (x,y) for the first walkable tile within radius 8.
    private static bool TryNearestWalkable(MapView view, int x, int y, out TilePos tile)
    {
        for (int r = 0; r <= 8; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (r > 0 && Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                var t = new TilePos(x + dx, y + dy);
                if (view.Walkable(t.X, t.Y)) { tile = t; return true; }
            }
        }
        tile = default;
        return false;
    }

    // Hold-and-fire at a validated target from the current tile: snap onto
    // the tile, assess crouch/lean cover, face + fire if in range with LoS.
    // Shared by the drafted-colonist combat path and the enemy brain
    // (PlanEnemy) so both get identical cover + firing behavior.
    private void ExecuteRangedFire(Entity entity, Entity tgt, Items.RangedSpec spec, ref WorldPos pos, ref PathFollower path, ref Wanderer w, float dt, MapView view, TilePos here)
    {
        // Hold ground: drop any path, snap smoothly to the tile.
        if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
        path.Waypoints = null; path.Index = 0;
        SnapToNearestTile(ref pos, dt, out _, out _);

        // Aim at the target's EFFECTIVE position (its peek cell while leaning),
        // so a target peeking around cover gets shot where its hitbox is.
        var tgtWp = tgt.GetComponent<WorldPos>();
        EffectivePos(tgt, tgtWp.X, tgtWp.Y, out float tpx, out float tpy);
        var tp = new WorldPos { X = tpx, Y = tpy };
        // Remember where we last engaged, so a lost burst can spray here.
        { ref var rcSeen = ref entity.GetComponent<RangedCombat>(); rcSeen.LastSeenX = tpx; rcSeen.LastSeenY = tpy; }
        float ddx = tp.X - pos.X, ddy = tp.Y - pos.Y;
        float distTiles = MathF.Sqrt(ddx * ddx + ddy * ddy);
        var ttile = new TilePos((int)tp.X, (int)tp.Y);
        // In range, but not point-blank — too close and the gun can't be
        // brought to bear (melee/back off instead).
        bool inRange = distTiles <= spec.Range && distTiles >= SimConstants.RangedMinFireRange;
        bool directLos = LosClear?.Invoke(here.X, here.Y, ttile.X, ttile.Y) ?? true;

        // ─── Cover assessment ─────────────────────────────────
        // Crouch cover: a sandbag in the step toward the target.
        bool crouchCover = directLos && HasSandbagToward(here, ttile);
        // Wall lean: when the direct shot is blocked (incl. grazing a wall
        // corner), peek one cell sideways to a spot that opens a clear lane.
        bool leaning = false;
        WorldPos muzzle = pos;
        var firePos = pos;
        bool losFinal = directLos;
        if (!directLos && TryFindLeanCell(view, here, ttile, out var leanCell))
        {
            leaning = true;
            losFinal = true;
            muzzle = new WorldPos { X = leanCell.X + 0.5f, Y = leanCell.Y + 0.5f };
            firePos = muzzle;
        }
        bool hasCover = crouchCover || leaning;

        ref var rcS = ref entity.GetComponent<RangedCombat>();
        if (hasCover)
        {
            bool reloadingOrEmpty = rcS.Reloading || rcS.MagCount <= 0;
            // Pop up only to fire; tuck while reloading or waiting.
            rcS.Stance = (losFinal && inRange && !reloadingOrEmpty)
                ? CoverStance.Popped : CoverStance.Tucked;
            rcS.Leaning = leaning;
            rcS.PeekX = firePos.X; rcS.PeekY = firePos.Y;
        }

        if (losFinal)
        {
            // Visible (directly or by leaning): aim from the firing position
            // toward the target, fire if in range.
            float adx = tp.X - muzzle.X, ady = tp.Y - muzzle.Y;
            if (adx * adx + ady * ady > 1e-9f) w.Facing = MathF.Atan2(ady, adx);
            if (inRange) HandleRangedFire(entity, tgt, spec, muzzle, tp, distTiles);
        }
        else
        {
            // Lost sight, no lean available: don't stare through the wall —
            // hunker down (tuck if behind cover) and wait.
            w.Facing = SouthFacing;
        }
    }

    private void HandleRangedFire(Entity entity, Entity tgt, Items.RangedSpec spec, WorldPos pos, WorldPos tp, float dist)
    {
        ref var rc = ref entity.GetComponent<RangedCombat>();

        if (rc.Reloading)
        {
            if (_tick < rc.NextActionTick) return;
            CompleteReload(entity, ref rc, spec); // insert-mag phase: now fill the mag
        }

        if (rc.MagCount <= 0)
        {
            if (!TryStartReload(entity, ref rc, spec)) rc.TargetEntityId = 0; // no ammo
            return;
        }

        // Snapshot fires with NO aim time (but a big accuracy penalty); Aimed
        // pays the per-target aim delay. Auto picks snapshot for very close
        // targets, aimed otherwise.
        bool snapshot = ResolveSnapshot(rc.AimMode, dist, spec.Range);
        long aimTicks = snapshot ? 0 : spec.AimTicks;

        // Aiming: a per-target spot-to-fire delay. (Re)start it when the target
        // changes or after the line was lost for longer than the aim time —
        // a brief LoS blip (<= AimTicks) keeps the existing aim. This is reached
        // only on ticks with a clear shot, so LastAimTick tracks continuous LoS.
        if (rc.AimTargetId != tgt.Id || _tick - rc.LastAimTick > spec.AimTicks)
            rc.AimReadyTick = _tick + aimTicks;
        rc.AimTargetId = tgt.Id;
        rc.LastAimTick = _tick;
        if (_tick < rc.AimReadyTick) return; // still aiming

        if (_tick < rc.NextActionTick) return; // shot / burst cooldown

        if (rc.BurstRemaining <= 0)
            rc.BurstRemaining = ShotsForMode(rc.Mode, spec); // start a burst (aim already paid)

        bool tgtDowned = tgt.HasComponent<Health>() && tgt.GetComponent<Health>().Unconscious;
        FireOneShot(entity, tgt.Id, tgtDowned, spec, ref rc, pos, tp, dist, snapshot);
        rc.MagCount--;
        rc.BurstRemaining--;
        rc.ShotTick = _tick;
        rc.NextActionTick = _tick + (rc.BurstRemaining > 0 ? spec.ShotCooldownTicks : spec.CycleCooldownTicks);
    }

    // Spray the rest of a committed burst toward the last position the target
    // was fired at — "into the unknown" when the target is gone / out of sight.
    // Holds position, fires on the burst cadence with the gun's normal aimed
    // spread, and releases once the rounds are spent.
    private void FinishBurstBlind(Entity entity, Items.RangedSpec spec, ref WorldPos pos, ref PathFollower path, ref Wanderer w, float dt, TilePos here)
    {
        if (path.PendingPathId != 0) { _paths.Discard(path.PendingPathId); path.PendingPathId = 0; }
        path.Waypoints = null; path.Index = 0;
        SnapToNearestTile(ref pos, dt, out _, out _);

        ref var rc = ref entity.GetComponent<RangedCombat>();
        var tp = new WorldPos { X = rc.LastSeenX, Y = rc.LastSeenY };
        float ddx = tp.X - pos.X, ddy = tp.Y - pos.Y;
        if (ddx * ddx + ddy * ddy > 1e-9f) w.Facing = MathF.Atan2(ddy, ddx);

        if (rc.MagCount <= 0) { rc.TargetEntityId = 0; rc.BurstRemaining = 0; return; } // dry → stop
        if (_tick < rc.NextActionTick) return;                                          // shot cooldown

        FireOneShot(entity, 0, false, spec, ref rc, pos, tp, MathF.Sqrt(ddx * ddx + ddy * ddy), snapshot: false);
        rc.MagCount--;
        rc.BurstRemaining--;
        rc.ShotTick = _tick;
        rc.NextActionTick = _tick + (rc.BurstRemaining > 0 ? spec.ShotCooldownTicks : spec.CycleCooldownTicks);
        if (rc.BurstRemaining <= 0) rc.TargetEntityId = 0; // burst spent → release
    }

    // Begin a reload: drop the spent mag now (MagCount -> 0) and start the
    // timer, but DON'T pull the new rounds yet — the ammo is only inserted when
    // the reload COMPLETES (CompleteReload), so interrupting a reload can't
    // grant a free instant reload. Returns false (no reload) if no matching
    // ammo is on hand.
    private bool TryStartReload(Entity entity, ref RangedCombat rc, Items.RangedSpec spec)
    {
        if (!TryFindReloadStack(entity, ref rc, spec, out _)) return false;
        rc.Reloading = true;
        rc.MagCount = 0;            // mag dropped
        rc.NextActionTick = _tick + spec.ReloadTicks;
        rc.BurstRemaining = 0;
        return true;
    }

    // Finish a reload: NOW pull the rounds from inventory into the mag (the
    // "insert mag" phase). Called only when the reload timer elapses.
    private void CompleteReload(Entity entity, ref RangedCombat rc, Items.RangedSpec spec)
    {
        rc.Reloading = false;
        if (!TryFindReloadStack(entity, ref rc, spec, out int k)) return; // ammo gone meanwhile
        ref var inv = ref entity.GetComponent<Inventory>();
        var stk = inv.Items![k];
        int load = Math.Min(spec.MagazineSize, stk.Count);
        stk.Count -= load;
        if (stk.Count <= 0) inv.Items.RemoveAt(k); else inv.Items[k] = stk;
        rc.MagCount = load;
        rc.LoadedAmmoPath = stk.ItemPath;
    }

    // Index of the first inventory stack of compatible (and, if set, preferred)
    // ammo with rounds in it. No mutation.
    private static bool TryFindReloadStack(Entity entity, ref RangedCombat rc, Items.RangedSpec spec, out int index)
    {
        index = -1;
        if (!entity.HasComponent<Inventory>()) return false;
        ref var inv = ref entity.GetComponent<Inventory>();
        if (inv.Items is null) return false;
        for (int k = 0; k < inv.Items.Count; k++)
        {
            var stk = inv.Items[k];
            if (stk.Count <= 0) continue;
            if (!Items.ItemCatalog.ItemsByPath.TryGetValue(stk.ItemPath, out var d) || d.Ammo is null) continue;
            if (d.Ammo.CategoryPath != spec.AmmoCategoryPath) continue;
            if (rc.PreferredAmmoPath is not null && stk.ItemPath != rc.PreferredAmmoPath) continue;
            index = k;
            return true;
        }
        return false;
    }

    private const float RangedMinShotDist = 1.5f; // min flight line so point-blank shots still sweep the target

    // True if this shot should be a snapshot (no aim time, wide cone): always
    // for Snapshot mode, and for Auto when the target is within the close band.
    public static bool ResolveSnapshot(Items.AimMode mode, float dist, float range)
        => mode == Items.AimMode.Snapshot
        || (mode == Items.AimMode.Auto && dist <= range * SimConstants.SnapshotRangeFraction);

    private void FireOneShot(Entity entity, int targetHintId, bool tgtDowned, Items.RangedSpec spec, ref RangedCombat rc, WorldPos pos, WorldPos tp, float dist, bool snapshot)
    {
        // Dispersion cone = steady spread + current recoil. The scatter radius
        // at the target is tan(cone) * distance; the round flies a FIXED line
        // to a random point in that disc and connects (or not) purely by
        // geometry in the collision pass.
        // Target in shadow widens the cone (harder to aim at what you can't see).
        float tgtLight = LightProvider?.Invoke((int)tp.X, (int)tp.Y) ?? 1f;
        float darkMult = 1f + SimConstants.DarknessSpreadBonus * (1f - Math.Clamp(tgtLight, 0f, 1f));
        // Snapshot trades accuracy for speed: a much wider cone.
        float snapMult = snapshot ? SimConstants.SnapshotSpreadMultiplier : 1f;
        float coneRad = (spec.SpreadDegrees + rc.Recoil) * darkMult * snapMult * (MathF.PI / 180f);
        // Forward direction to the target (fallback if standing on top of it).
        float dirx = tp.X - pos.X, diry = tp.Y - pos.Y;
        float d = MathF.Sqrt(dirx * dirx + diry * diry);
        if (d < 1e-3f) { dirx = 1f; diry = 0f; d = 1f; } else { dirx /= d; diry /= d; }
        // Always aim at least MinShotDist ahead so the flight line genuinely
        // sweeps the target (a zero-length point-blank line can't hit) and the
        // ballistic solve doesn't blow up on a near-zero flight time.
        float aimDist = MathF.Max(d, RangedMinShotDist);
        float radius = MathF.Tan(coneRad) * aimDist;
        double ang = _rng.NextDouble() * Math.PI * 2.0;
        float r = (float)Math.Sqrt(_rng.NextDouble()) * radius; // uniform in the disc
        float toX = pos.X + dirx * aimDist + (float)Math.Cos(ang) * r;
        float toY = pos.Y + diry * aimDist + (float)Math.Sin(ang) * r;
        // Aim at the chosen body region's height — or low for a downed/prone
        // target so finishing shots still connect.
        float aimH = tgtDowned ? SimConstants.DownedAimHeight : rc.TargetArea switch
        {
            Items.TargetArea.Head => SimConstants.AimHeadHeight,
            Items.TargetArea.Torso => SimConstants.BodyAimHeight,
            Items.TargetArea.Legs => SimConstants.AimLegsHeight,
            _ => SimConstants.AimAutoHeight, // Auto = center mass
        };
        // Vertical inaccuracy too: the same cone scatters the impact height, so
        // the aimed region is a BIAS, not a guarantee — a head-aimed round can
        // stray up high or down into the neck/torso.
        float vScatter = (float)(_rng.NextDouble() * 2.0 - 1.0) * radius;
        aimH = MathF.Max(0.05f, aimH + vScatter);
        PendingProjectiles.Add(new ProjectileSpawn(
            pos.X, pos.Y, toX, toY, aimH, spec.ProjectileSpeed,
            entity.Id, targetHintId, true, rc.LoadedAmmoPath ?? ""));
        // Muzzle climb: this shot kicks the cone wider for the next.
        rc.Recoil = MathF.Min(spec.MaxRecoilDegrees, rc.Recoil + spec.RecoilPerShot);
    }

    // True if any of the 8 tiles around `here` holds a sandbag.
    private bool IsAdjacentToSandbag(TilePos here)
    {
        if (HasSandbag is null) return false;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                if (HasSandbag(here.X + dx, here.Y + dy)) return true;
            }
        return false;
    }

    // True if a sandbag sits in the immediate step from `here` toward the
    // target — the pawn can crouch behind it (directional, low cover).
    private bool HasSandbagToward(TilePos here, TilePos ttile)
    {
        if (HasSandbag is null) return false;
        int sgx = Math.Sign(ttile.X - here.X);
        int sgy = Math.Sign(ttile.Y - here.Y);
        if (sgx == 0 && sgy == 0) return false;
        if (sgx != 0 && HasSandbag(here.X + sgx, here.Y)) return true;
        if (sgy != 0 && HasSandbag(here.X, here.Y + sgy)) return true;
        if (sgx != 0 && sgy != 0 && HasSandbag(here.X + sgx, here.Y + sgy)) return true;
        return false;
    }

    // RimWorld-style corner lean: when a wall directly in the target's
    // direction blocks the shot, the pawn peeks ONE step sideways (along the
    // wall face) to a single adjacent cell that can see the target. It doesn't
    // move there — it just exposes the body at that cell while popped and tucks
    // back behind the wall otherwise. Strictly a sideways peek against the wall
    // it's hugging: no diagonal hops, no leaning when there's no wall to peek
    // around.
    // Public probe for the hit-chance readout: does a lean-peek from `here`
    // open a clear shot at `target`? Same logic the firing code uses.
    public bool TryGetLeanCell(MapView view, TilePos here, TilePos target, out TilePos cell)
        => TryFindLeanCell(view, here, target, out cell);

    private bool TryFindLeanCell(MapView view, TilePos here, TilePos ttile, out TilePos leanCell)
    {
        leanCell = here;
        if (LosClear is null) return false;
        int dx = ttile.X - here.X, dy = ttile.Y - here.Y;
        if (dx == 0 && dy == 0) return false;

        // Only lean when actually hugging a wall (a genuine cover peek) — not
        // sidestepping in the open.
        bool hugging = view.GetWall(here.X + 1, here.Y) != WallType.None
            || view.GetWall(here.X - 1, here.Y) != WallType.None
            || view.GetWall(here.X, here.Y + 1) != WallType.None
            || view.GetWall(here.X, here.Y - 1) != WallType.None;
        if (!hugging) return false;

        // Consider EVERY adjacent cell (all 4 faces + 4 corners) as a peek
        // candidate, not just one axis — at a corner, peeking either way is
        // valid. A candidate counts if: it's open, the shot from it has clear
        // LoS to the target (the body lacks it — callers only lean when the
        // direct shot is blocked), and the peek step isn't AWAY from the target
        // (the one rule that kills the "lean backward" jank). Pick the peek that
        // ends up closest to the target (the most forward shoulder-out).
        float toLen = MathF.Sqrt((float)(dx * dx + dy * dy));
        float best = float.MaxValue; bool found = false;
        foreach (var (px, py) in _leanNeighbors)
        {
            int cx = here.X + px, cy = here.Y + py;
            if (!view.InBounds(cx, cy)) continue;
            if (view.GetWall(cx, cy) != WallType.None) continue; // can't peek into a wall
            // Reject a step pointing away from the target (dot clearly < 0);
            // a square-on perpendicular peek (dot ~0) is allowed.
            float stepLen = MathF.Sqrt((float)(px * px + py * py));
            if (toLen > 1e-4f && stepLen > 1e-4f
                && ((px / stepLen) * (dx / toLen) + (py / stepLen) * (dy / toLen)) < -0.05f)
                continue;
            // The peek must open a clear lane the body lacks.
            if (!LosClear(cx, cy, ttile.X, ttile.Y)) continue;
            float ex = ttile.X - cx, ey = ttile.Y - cy;
            float d2 = ex * ex + ey * ey;
            if (d2 < best) { best = d2; leanCell = new TilePos(cx, cy); found = true; }
        }
        return found;
    }

    // The 4 orthogonal faces — each peek is a single clean direction. (No
    // diagonals: a diagonal peek cell reads as leaning two ways at once. At a
    // corner both orthogonal options are still evaluated separately, and the
    // best single one is picked.)
    private static readonly (int dx, int dy)[] _leanNeighbors =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    // Undrafted idle behavior: keep the equipped ranged weapon's magazine
    // topped off, walking to fetch ammo from a pile when none is carried.
    // Returns true if it took control of the pawn this tick.
    private bool TryReloadBehavior(Entity entity, ref PathFollower path, MapView view, TilePos here)
    {
        if (!entity.HasComponent<RangedCombat>()) return false;
        if (!TryGetRangedWeapon(entity, out var def)) return false;
        var spec = def.Ranged!;
        ref var rc = ref entity.GetComponent<RangedCombat>();

        // Finish an in-progress reload (stand still until it completes).
        if (rc.Reloading)
        {
            if (_tick >= rc.NextActionTick) rc.Reloading = false;
            path.Waypoints = null; path.Index = 0;
            return true;
        }
        if (rc.MagCount >= spec.MagazineSize) return false; // already full

        // The ammo type we want: keep the loaded type when topping a partial
        // mag, else the player-locked type, else anything compatible.
        string? want = rc.MagCount > 0 ? rc.LoadedAmmoPath : rc.PreferredAmmoPath;

        // Carrying compatible ammo → reload where we stand.
        if (TopUpMagFromInventory(entity, ref rc, spec, want))
        {
            path.Waypoints = null; path.Index = 0;
            return true;
        }

        // Otherwise go fetch some from the nearest matching pile.
        if (!TryFindNearestAmmoPile(here, spec, want, out var pileTile, out var pilePath))
            return false; // no ammo anywhere → resume normal life

        bool adjacent = Math.Abs(pileTile.X - here.X) <= 1 && Math.Abs(pileTile.Y - here.Y) <= 1;
        if (adjacent)
        {
            int got = CookConsumePile?.Invoke(pileTile, pilePath, spec.MagazineSize) ?? 0;
            if (got > 0) AddInventoryAmmo(entity, pilePath, got);
            path.Waypoints = null; path.Index = 0;
            return true; // next tick: reload in place
        }

        if (path.PendingPathId == 0 && (path.Waypoints is null || path.Index >= path.Waypoints.Count))
        {
            if (view.Walkable(pileTile)) path.PendingPathId = _paths.Request(here, pileTile);
            else if (TryPickNeighbor(view, here, pileTile, out var approach)) path.PendingPathId = _paths.Request(here, approach);
            else return false;
        }
        return true;
    }

    // Pull rounds from inventory into the magazine (top-up, keeps the partial
    // mag). `want` constrains the ammo type when set. Starts a timed reload.
    private bool TopUpMagFromInventory(Entity entity, ref RangedCombat rc, Items.RangedSpec spec, string? want)
    {
        if (!entity.HasComponent<Inventory>()) return false;
        ref var inv = ref entity.GetComponent<Inventory>();
        if (inv.Items is null) return false;
        int need = spec.MagazineSize - rc.MagCount;
        if (need <= 0) return false;
        for (int k = 0; k < inv.Items.Count; k++)
        {
            var stk = inv.Items[k];
            if (!Items.ItemCatalog.ItemsByPath.TryGetValue(stk.ItemPath, out var d) || d.Ammo is null) continue;
            if (d.Ammo.CategoryPath != spec.AmmoCategoryPath) continue;
            if (want is not null && stk.ItemPath != want) continue;
            int load = Math.Min(need, stk.Count);
            if (load <= 0) continue;
            stk.Count -= load;
            if (stk.Count <= 0) inv.Items.RemoveAt(k); else inv.Items[k] = stk;
            rc.MagCount += load;
            rc.LoadedAmmoPath = stk.ItemPath;
            rc.Reloading = true;
            rc.NextActionTick = _tick + spec.ReloadTicks;
            rc.BurstRemaining = 0;
            return true;
        }
        return false;
    }

    private bool TryFindNearestAmmoPile(TilePos here, Items.RangedSpec spec, string? want, out TilePos tile, out string path)
    {
        tile = default; path = "";
        if (CookFindNearestPile is null) return false;
        int best = int.MaxValue;
        foreach (var kv in Items.ItemCatalog.ItemsByPath)
        {
            var d = kv.Value;
            if (d.Ammo is null || d.Ammo.CategoryPath != spec.AmmoCategoryPath) continue;
            if (want is not null && kv.Key != want) continue;
            if (CookFindNearestPile(here, kv.Key) is not TilePos t) continue;
            int dist = Math.Abs(t.X - here.X) + Math.Abs(t.Y - here.Y);
            if (dist < best) { best = dist; tile = t; path = kv.Key; }
        }
        return best != int.MaxValue;
    }

    private static void AddInventoryAmmo(Entity entity, string itemPath, int count)
    {
        if (count <= 0 || !entity.HasComponent<Inventory>()) return;
        ref var inv = ref entity.GetComponent<Inventory>();
        inv.Items ??= new List<InventoryStack>();
        for (int i = 0; i < inv.Items.Count; i++)
            if (inv.Items[i].ItemPath == itemPath)
            {
                var s = inv.Items[i]; s.Count += count; inv.Items[i] = s; return;
            }
        inv.Items.Add(new InventoryStack { ItemPath = itemPath, Count = count });
    }

    // Smoothly slide pos toward the center of the tile it's currently
    // standing on (nearest tile). Used when a colonist is drafted-idle or
    // gets downed: instead of freezing mid-tile, they ease onto the grid.
    // Returns true once centered. dir{X,Y} is the (un-normalized) movement
    // this tick so the caller can face the direction of travel.
    private static bool SnapToNearestTile(ref WorldPos pos, float dt, out float dirX, out float dirY)
    {
        float cx = MathF.Round(pos.X - 0.5f) + 0.5f;
        float cy = MathF.Round(pos.Y - 0.5f) + 0.5f;
        float dx = cx - pos.X, dy = cy - pos.Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist < 1e-4f) { pos.X = cx; pos.Y = cy; dirX = dirY = 0f; return true; }
        float step = SimConstants.WalkTilesPerSecond * dt;
        if (dist <= step) { dirX = dx; dirY = dy; pos.X = cx; pos.Y = cy; return true; }
        dirX = dx; dirY = dy;
        pos.X += dx / dist * step;
        pos.Y += dy / dist * step;
        return false;
    }

    // Deterministic per-(pawn,tile) hash so the "which mover slows" pick is
    // pseudo-random yet stable across a tick / replay (no Random()).
    private static uint CrowdHash(int id, int x, int y)
    {
        unchecked
        {
            uint h = (uint)id * 2654435761u;
            h ^= (uint)x * 40503u;
            h ^= (uint)y * 12289u;
            h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
            return h;
        }
    }

    private void AdvanceAlongPath(ref WorldPos pos, ref PathFollower path, float dt, MapView view, float speedMul)
    {
        if (path.Waypoints is null || path.Index >= path.Waypoints.Count) return;

        float remaining = SimConstants.WalkTilesPerSecond * dt * speedMul;
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
