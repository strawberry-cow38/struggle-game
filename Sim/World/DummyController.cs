using Friflo.Engine.ECS;
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
    private static readonly (int dx, int dy)[] FourNeighbors = new (int, int)[]
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    public delegate bool DoorLookup(TilePos tile, out Entity entity);

    private readonly PathService _paths;
    private readonly JobBoard _jobs;
    private readonly Func<MapView> _viewProvider;
    private readonly Action<JobId> _cancelJob;
    private readonly DoorLookup _tryGetDoor;
    private readonly Random _rng;

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
        var query = store.Query<WorldPos, PathFollower, Wanderer>();
        query.ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer _, Entity entity) =>
        {
            Plan(ref pos, ref path, entity, cb, view);
            AdvanceAlongPath(ref pos, ref path, dt);
        });
        cb.Playback();
    }

    private void Plan(ref WorldPos pos, ref PathFollower path, Entity entity, CommandBuffer cb, MapView view)
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
                _jobs.Release(bt.JobId);
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
                cb.RemoveComponent<BuildTarget>(entity.Id);
                path.Waypoints = null;
                path.Index = 0;
            }
            else if (IsAdjacent4(here, job.Tile))
            {
                // Standing next to it — BuildSystem advances progress.
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

    private static bool TryPickNeighbor(MapView view, TilePos from, TilePos target, out TilePos neighbor)
    {
        TilePos best = default;
        int bestDist = int.MaxValue;
        foreach (var (dx, dy) in FourNeighbors)
        {
            int nx = target.X + dx;
            int ny = target.Y + dy;
            if (!view.Walkable(nx, ny)) continue;
            int d = Math.Abs(nx - from.X) + Math.Abs(ny - from.Y);
            if (d < bestDist)
            {
                bestDist = d;
                best = new TilePos(nx, ny);
            }
        }
        if (bestDist == int.MaxValue)
        {
            neighbor = default;
            return false;
        }
        neighbor = best;
        return true;
    }

    private bool TryClaimJob(MapView view, TilePos from, Entity entity, CommandBuffer cb, ref PathFollower path)
    {
        JobId bestId = JobId.None;
        TilePos bestNeighbor = default;
        int bestDist = int.MaxValue;
        foreach (var job in _jobs.All)
        {
            if (job.Kind != JobKind.WallBuild
                && job.Kind != JobKind.ChopTree
                && job.Kind != JobKind.Deconstruct
                && job.Kind != JobKind.FloorBuild
                && job.Kind != JobKind.DoorBuild) continue;
            if (job.State != JobState.Open) continue;
            int d = Math.Abs(job.Tile.X - from.X) + Math.Abs(job.Tile.Y - from.Y);
            if (d >= bestDist) continue;
            if (!TryPickNeighbor(view, from, job.Tile, out var neighbor)) continue;
            bestId = job.Id;
            bestNeighbor = neighbor;
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

    private static bool IsAdjacent4(TilePos a, TilePos b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        return dx + dy == 1;
    }

    private void AdvanceAlongPath(ref WorldPos pos, ref PathFollower path, float dt)
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
