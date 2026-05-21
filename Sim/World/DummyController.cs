using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;

namespace StruggleGame.Sim.World;

// Per-tick: for every Wanderer, decide what to do.
//   - If they have a BuildTarget that still exists in the registry, walk
//     to a tile 4-adjacent to it (BuildSystem ticks progress while
//     adjacent).
//   - Else if any blueprints are queued, claim the nearest reachable one.
//   - Else pick a random walkable goal and wander to it.
public sealed class DummyController
{
    private static readonly (int dx, int dy)[] FourNeighbors = new (int, int)[]
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    private readonly SimRuntime _sim;
    private readonly TileMap _map;
    private readonly BlueprintRegistry _registry;
    private readonly AStar _astar;
    private readonly Random _rng;

    public DummyController(SimRuntime sim, TileMap map, BlueprintRegistry registry, int seed)
    {
        _sim = sim;
        _map = map;
        _registry = registry;
        _astar = new AStar(map);
        _rng = new Random(seed);
    }

    public void Step(EntityStore store, float dt)
    {
        var cb = store.GetCommandBuffer();
        var query = store.Query<WorldPos, PathFollower, Wanderer>();
        query.ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer _, Entity entity) =>
        {
            Plan(ref pos, ref path, entity, cb);
            AdvanceAlongPath(ref pos, ref path, dt);
        });
        cb.Playback();
    }

    private void Plan(ref WorldPos pos, ref PathFollower path, Entity entity, CommandBuffer cb)
    {
        var here = new TilePos((int)pos.X, (int)pos.Y);

        // 1. Existing build target.
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
                // Standing next to it. Stop walking — BuildSystem ticks now.
                path.Waypoints = null;
                path.Index = 0;
                return;
            }
            else if (path.Waypoints is null || path.Index >= path.Waypoints.Count)
            {
                if (TryRouteToNeighbor(here, bt.Tile, out var route))
                {
                    path.Waypoints = route;
                    path.Index = 1;
                }
                else
                {
                    // Can't reach this target; drop it.
                    cb.RemoveComponent<BuildTarget>(entity.Id);
                }
                return;
            }
            else
            {
                return; // still walking toward target
            }
        }

        // 2. Look for a new blueprint to claim.
        if (_registry.Count > 0 && TryClaimNearestBlueprint(here, entity, cb, out var route2))
        {
            path.Waypoints = route2;
            path.Index = 1;
            return;
        }

        // 3. Wander.
        EnsureWanderPath(ref pos, ref path);
    }

    private bool TryClaimNearestBlueprint(TilePos from, Entity entity, CommandBuffer cb, out List<TilePos> route)
    {
        var ranked = new List<(int dist, TilePos tile)>(_registry.Count);
        foreach (var t in _registry.Tiles)
        {
            int dist = Math.Abs(t.X - from.X) + Math.Abs(t.Y - from.Y);
            ranked.Add((dist, t));
        }
        ranked.Sort(static (a, b) => a.dist.CompareTo(b.dist));

        int attempts = Math.Min(ranked.Count, 8);
        for (int i = 0; i < attempts; i++)
        {
            var target = ranked[i].tile;
            if (TryRouteToNeighbor(from, target, out var found))
            {
                cb.AddComponent(entity.Id, new BuildTarget { Tile = target });
                route = found;
                return true;
            }
        }
        route = null!;
        return false;
    }

    // Find the walkable 4-neighbor of `target` closest to `from`, then A*
    // to it. Returns the full waypoint list (start..neighbor inclusive).
    private bool TryRouteToNeighbor(TilePos from, TilePos target, out List<TilePos> route)
    {
        TilePos best = default;
        int bestDist = int.MaxValue;
        foreach (var (dx, dy) in FourNeighbors)
        {
            int nx = target.X + dx;
            int ny = target.Y + dy;
            if (!_map.Walkable(nx, ny)) continue;
            int d = Math.Abs(nx - from.X) + Math.Abs(ny - from.Y);
            if (d < bestDist)
            {
                bestDist = d;
                best = new TilePos(nx, ny);
            }
        }
        if (bestDist == int.MaxValue)
        {
            route = null!;
            return false;
        }
        if (best == from)
        {
            route = new List<TilePos> { from };
            return true;
        }
        var path = _astar.FindPath(from, best);
        if (path is { Count: > 0 })
        {
            route = path;
            return true;
        }
        route = null!;
        return false;
    }

    private void EnsureWanderPath(ref WorldPos pos, ref PathFollower path)
    {
        if (path.Waypoints is not null && path.Index < path.Waypoints.Count) return;

        var start = new TilePos((int)pos.X, (int)pos.Y);
        for (int tries = 0; tries < 32; tries++)
        {
            var goal = new TilePos(_rng.Next(_map.Width), _rng.Next(_map.Height));
            if (!_map.Walkable(goal) || goal == start) continue;

            var found = _astar.FindPath(start, goal);
            if (found is { Count: > 1 })
            {
                path.Waypoints = found;
                path.Index = 1;
                return;
            }
        }
        path.Waypoints = null;
        path.Index = 0;
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
