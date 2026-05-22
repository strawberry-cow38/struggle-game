using Friflo.Engine.ECS;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;

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
    // Optional callback for haul completion. Set by SimRuntime so we
    // don't need to plumb the runtime through the controller's surface.
    // Fires when a pawn physically picks up one item entity. Hooked by
    // SimRuntime to strip world-side Wood + HaulPayload from the entity.
    public Action<Friflo.Engine.ECS.Entity, CommandBuffer>? OnHaulPickup;
    // Fires when a pawn drops its entire inventory at a tile (either
    // planned DestTile or a fallback tile on abort). Hooked by SimRuntime
    // to re-anchor every slot, complete the primary job, and free any
    // never-picked-up topoff reservations.
    public Action<Carrying, TilePos, CommandBuffer>? OnHaulDeliver;
    // Scratch set populated each Step() so a single tick of topoff scans
    // doesn't reserve the same item for two different carriers.
    private readonly HashSet<int> _topoffReservedThisTick = new();

    public DummyController(
        PathService paths,
        JobBoard jobs,
        Func<MapView> viewProvider,
        Action<JobId> cancelJob,
        int seed,
        DoorLookup tryGetDoor)
    {
        _paths = paths;
        _jobs = jobs;
        _viewProvider = viewProvider;
        _cancelJob = cancelJob;
        _tryGetDoor = tryGetDoor;
        _rng = new Random(seed);
    }

    public void Step(EntityStore store, float dt)
    {
        var view = _viewProvider();
        var cb = store.GetCommandBuffer();
        _topoffReservedThisTick.Clear();
        var query = store.Query<WorldPos, PathFollower, Wanderer>();
        query.ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer _, Entity entity) =>
        {
            Plan(ref pos, ref path, entity, cb, view, store);
            AdvanceAlongPath(ref pos, ref path, dt, view);
        });
        cb.Playback();
    }

    private void Plan(ref WorldPos pos, ref PathFollower path, Entity entity, CommandBuffer cb, MapView view, EntityStore store)
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

        // Drafted colonists ignore jobs/wander. Walk the active player
        // order if there is one; otherwise dequeue the next move order;
        // otherwise hold position and watch.
        if (drafted)
        {
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
                    var c = entity.GetComponent<Carrying>();
                    OnHaulDeliver?.Invoke(c, here, cb);
                    cb.RemoveComponent<Carrying>(entity.Id);
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

        // 2. Existing build target.
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
                    var c = entity.GetComponent<Carrying>();
                    OnHaulDeliver?.Invoke(c, here, cb);
                    cb.RemoveComponent<Carrying>(entity.Id);
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
            else if (job.Kind == JobKind.FloorBuild)
            {
                // Floors don't block movement and the worker can stand on
                // the tile itself — approach = job.Tile, adjacency permissive.
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

        // 3. Claim a new job.
        if (_jobs.Count > 0 && TryClaimJob(view, here, entity, cb, ref path))
        {
            return;
        }

        // 4. Wander.
        if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
        {
            RequestWanderPath(view, here, ref path);
        }
    }

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
        JobId bestId = JobId.None;
        TilePos bestNeighbor = default;
        bool bestIsHaul = false;
        int bestDist = int.MaxValue;
        foreach (var job in _jobs.All)
        {
            if (job.Kind != JobKind.WallBuild
                && job.Kind != JobKind.ChopTree
                && job.Kind != JobKind.Deconstruct
                && job.Kind != JobKind.FloorBuild
                && job.Kind != JobKind.DoorBuild
                && job.Kind != JobKind.Haul) continue;
            if (job.State != JobState.Open) continue;
            int d = Math.Abs(job.Tile.X - from.X) + Math.Abs(job.Tile.Y - from.Y);
            if (d >= bestDist) continue;
            TilePos approach;
            bool isHaul = job.Kind == JobKind.Haul;
            bool isFloor = job.Kind == JobKind.FloorBuild;
            if (isHaul || isFloor)
            {
                // Haul pickup walks onto the source tile itself, not a
                // neighbor. Floors also walk onto the tile — they don't
                // block pathing and the worker can stand on them.
                if (!view.Walkable(job.Tile)) continue;
                approach = job.Tile;
            }
            else
            {
                if (!TryPickNeighbor(view, from, job.Tile, out var neighbor)) continue;
                approach = neighbor;
            }
            bestId = job.Id;
            bestNeighbor = approach;
            bestIsHaul = isHaul;
            bestDist = d;
        }
        if (bestId.IsNone) return false;
        if (!_jobs.TryClaim(bestId, entity)) return false;

        cb.AddComponent(entity.Id, new BuildTarget { JobId = bestId });
        if (bestNeighbor != from)
        {
            path.PendingPathId = _paths.Request(from, bestNeighbor);
        }
        return true;
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
                ScanTopoffs(store, cb, slots, pending, here, hp.DestTile);
                cb.AddComponent(entity.Id, new Carrying
                {
                    Slots = slots,
                    PendingPickupIds = pending,
                    DestTile = hp.DestTile,
                    StockpileId = hp.StockpileId,
                    PrimaryJobId = job.Id,
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
                if (!pe.HasComponent<Wood>()) continue;
                var ptile = pe.GetComponent<Wood>().Tile;
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
                    && pe.HasComponent<Wood>())
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
            OnHaulDeliver?.Invoke(c, here, cb);
            cb.RemoveComponent<Carrying>(entity.Id);
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
                OnHaulDeliver?.Invoke(c, here, cb);
                cb.RemoveComponent<Carrying>(entity.Id);
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
    private void ScanTopoffs(
        EntityStore store,
        CommandBuffer cb,
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
        float wRem = SimConstants.MaxCarryWeight - wUsed;
        float bRem = SimConstants.MaxCarryBulk - bUsed;
        if (wRem <= 0f || bRem <= 0f) return;

        // Snapshot candidates first so the nested query can't see any
        // mutations we'd queue mid-iteration.
        var candidates = new List<(Entity Ent, int Count, string Path, int Dist)>();
        store.Query<Wood>().ForEachEntity((ref Wood w, Entity e) =>
        {
            if (e.HasComponent<HaulReserved>()) return;
            if (e.HasComponent<Forbidden>()) return;
            if (_topoffReservedThisTick.Contains(e.Id)) return;
            int md = Math.Abs(w.Tile.X - primarySource.X) + Math.Abs(w.Tile.Y - primarySource.Y);
            if (md == 0) return; // primary already handled
            if (md > SimConstants.HaulTopoffRadius) return;
            candidates.Add((e, w.Count, ItemCatalog.Wood.FullPath, md));
        });
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
