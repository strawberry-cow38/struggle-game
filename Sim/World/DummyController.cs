using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;

namespace StruggleGame.Sim.World;

// Per-tick: for every Wanderer, drive the decision loop.
//   1. If a path request is in flight, poll PathService — when it resolves
//      either start walking or drop the goal.
//   2. If holding a BuildTarget, walk to a tile 4-adjacent to it.
//   3. If any blueprints are open, claim the nearest by Manhattan and ask
//      PathService to route to a walkable neighbor.
//   4. Otherwise pick a random wander goal and request a path.
//
// All pathfinding now goes through PathService so this controller is
// trivially compatible with a future async worker pool.
public sealed class DummyController
{
    private static readonly (int dx, int dy)[] FourNeighbors = new (int, int)[]
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    private readonly PathService _paths;
    private readonly BlueprintRegistry _registry;
    private readonly Func<MapView> _viewProvider;
    private readonly Random _rng;

    public DummyController(PathService paths, BlueprintRegistry registry, Func<MapView> viewProvider, int seed)
    {
        _paths = paths;
        _registry = registry;
        _viewProvider = viewProvider;
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
                // If the path starts at our current tile, skip it.
                path.Index = result.Path[0] == here ? 1 : 0;
            }
            else
            {
                // No path: drop any build target so we don't loop.
                if (entity.HasComponent<BuildTarget>())
                {
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                }
                path.Waypoints = null;
                path.Index = 0;
            }
        }

        // 2. Existing build target.
        if (entity.HasComponent<BuildTarget>())
        {
            var bt = entity.GetComponent<BuildTarget>();
            if (!_registry.Has(bt.Tile))
            {
                cb.RemoveComponent<BuildTarget>(entity.Id);
                path.Waypoints = null;
                path.Index = 0;
            }
            else if (IsAdjacent4(here, bt.Tile))
            {
                // Standing next to it — BuildSystem advances progress.
                path.Waypoints = null;
                path.Index = 0;
                return;
            }
            else if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
            {
                if (TryPickNeighbor(view, here, bt.Tile, out var neighbor))
                {
                    if (neighbor == here)
                    {
                        // Already there next tick the IsAdjacent4 branch fires.
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
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                }
                return;
            }
            else
            {
                return; // still walking
            }
        }

        // 3. Claim a new blueprint.
        if (_registry.Count > 0 && TryClaimBlueprint(view, here, entity, cb, ref path))
        {
            return;
        }

        // 4. Wander.
        if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
        {
            RequestWanderPath(view, here, ref path);
        }
    }

    // Find a walkable 4-neighbor of `target` closest to `from`. Result may
    // equal `from` if we're already adjacent.
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

    // Pick the closest blueprint (by Manhattan) that has at least one
    // walkable neighbor and assign it. Routing happens via PathService —
    // if no actual route exists, the result tick clears the target.
    private bool TryClaimBlueprint(MapView view, TilePos from, Entity entity, CommandBuffer cb, ref PathFollower path)
    {
        TilePos? bestTile = null;
        TilePos bestNeighbor = default;
        int bestDist = int.MaxValue;
        foreach (var t in _registry.Tiles)
        {
            int d = Math.Abs(t.X - from.X) + Math.Abs(t.Y - from.Y);
            if (d >= bestDist) continue;
            if (!TryPickNeighbor(view, from, t, out var neighbor)) continue;
            bestTile = t;
            bestNeighbor = neighbor;
            bestDist = d;
        }
        if (bestTile is null) return false;

        cb.AddComponent(entity.Id, new BuildTarget { Tile = bestTile.Value });
        if (bestNeighbor != from)
        {
            path.PendingPathId = _paths.Request(from, bestNeighbor);
        }
        return true;
    }

    private void RequestWanderPath(MapView view, TilePos from, ref PathFollower path)
    {
        for (int tries = 0; tries < 8; tries++)
        {
            var goal = new TilePos(_rng.Next(view.Width), _rng.Next(view.Height));
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

    private static void AdvanceAlongPath(ref WorldPos pos, ref PathFollower path, float dt)
    {
        if (path.Waypoints is null || path.Index >= path.Waypoints.Count) return;

        float remaining = SimConstants.WalkTilesPerSecond * dt;
        while (remaining > 0f && path.Index < path.Waypoints.Count)
        {
            var target = path.Waypoints[path.Index];
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
