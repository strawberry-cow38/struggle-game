using Friflo.Engine.ECS;
using StruggleGame.Sim.Diagnostics;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;

namespace StruggleGame.Sim.World;

// Belt-and-braces: if a pawn somehow ends up standing on a Wall tile
// (build race, future teleport command, save-load edge case), this
// system runs once per tick after movement + building and snaps the
// pawn to the nearest 4-connected walkable tile. The rescue is logged
// to the SimWatcher so we notice it.
public sealed class SafetySystem
{
    private const int MaxRescueRadius = 32;
    private static readonly (int dx, int dy)[] Four = new (int, int)[]
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    private readonly Func<MapView> _viewProvider;
    private readonly PathService _paths;
    private readonly SimWatcher _watcher;

    public SafetySystem(Func<MapView> viewProvider, PathService paths, SimWatcher watcher)
    {
        _viewProvider = viewProvider;
        _paths = paths;
        _watcher = watcher;
    }

    public void Step(EntityStore store, long tick)
    {
        var view = _viewProvider();
        store.Query<WorldPos, PathFollower, Wanderer>().ForEachEntity((ref WorldPos pos, ref PathFollower path, ref Wanderer _, Entity ent) =>
        {
            var from = new TilePos((int)pos.X, (int)pos.Y);
            if (view.Walkable(from)) return;

            var safe = FindNearestWalkable(view, from);
            if (safe is null) return;
            var to = safe.Value;

            pos.X = to.X + 0.5f;
            pos.Y = to.Y + 0.5f;
            path.Waypoints = null;
            path.Index = 0;
            if (path.PendingPathId != 0)
            {
                _paths.Discard(path.PendingPathId);
                path.PendingPathId = 0;
            }
            _watcher.RecordRescue(tick, ent.Id, from, to);
        });
    }

    private static TilePos? FindNearestWalkable(MapView view, TilePos from)
    {
        var visited = new HashSet<TilePos> { from };
        var queue = new Queue<TilePos>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            int d = Math.Max(Math.Abs(cur.X - from.X), Math.Abs(cur.Y - from.Y));
            if (d >= MaxRescueRadius) continue;
            foreach (var (dx, dy) in Four)
            {
                var n = new TilePos(cur.X + dx, cur.Y + dy);
                if (!visited.Add(n)) continue;
                if (!view.InBounds(n)) continue;
                if (view.Walkable(n)) return n;
                queue.Enqueue(n);
            }
        }
        return null;
    }
}
