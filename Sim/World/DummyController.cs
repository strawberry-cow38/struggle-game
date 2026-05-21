using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;

namespace StruggleGame.Sim.World;

// Per-tick: for every Wanderer, ensure they have a path. If they don't,
// pick a random walkable tile and A* to it. Otherwise advance along the
// current path at WalkTilesPerSecond.
public sealed class DummyController
{
    private readonly TileMap _map;
    private readonly AStar _astar;
    private readonly Random _rng;

    public DummyController(TileMap map, int seed)
    {
        _map = map;
        _astar = new AStar(map);
        _rng = new Random(seed);
    }

    public void Step(EntityStore store, float dt)
    {
        var query = store.Query<Position, PathFollower, Wanderer>();
        query.ForEachEntity((ref Position pos, ref PathFollower path, ref Wanderer _, Entity _) =>
        {
            EnsurePath(ref pos, ref path);
            AdvanceAlongPath(ref pos, ref path, dt);
        });
    }

    private void EnsurePath(ref Position pos, ref PathFollower path)
    {
        if (path.Waypoints is not null && path.Index < path.Waypoints.Count) return;

        var start = new TilePos((int)pos.X, (int)pos.Y);
        TilePos goal;
        for (int tries = 0; tries < 32; tries++)
        {
            goal = new TilePos(_rng.Next(_map.Width), _rng.Next(_map.Height));
            if (!_map.Walkable(goal) || goal == start) continue;

            var found = _astar.FindPath(start, goal);
            if (found is { Count: > 1 })
            {
                path.Waypoints = found;
                path.Index = 1; // skip start; we're already on it
                return;
            }
        }
        // Couldn't find anywhere reachable this tick — try again next tick.
        path.Waypoints = null;
        path.Index = 0;
    }

    private static void AdvanceAlongPath(ref Position pos, ref PathFollower path, float dt)
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
