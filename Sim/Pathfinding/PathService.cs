using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.Pathfinding;

public enum PathStatus { Pending, Found, NoPath }

public readonly record struct PathResult(PathStatus Status, List<TilePos>? Path, long MapVersion);

// Async-ready path request facade. Today: computes synchronously on the
// sim thread using a single AStar. Tomorrow: queue + worker pool, same
// API. Callers get a request id back, poll TryConsume next tick (or same
// tick under the sync impl), and Discard ids they no longer care about
// (e.g. pawn picked a new goal) so memory doesn't grow.
public sealed class PathService
{
    private readonly Func<MapView> _viewProvider;
    private readonly AStar _astar;
    private readonly Dictionary<long, PathResult> _ready = new();
    private long _nextId;

    public PathService(int width, int height, Func<MapView> viewProvider)
    {
        _astar = new AStar(width, height);
        _viewProvider = viewProvider;
    }

    public long Request(TilePos from, TilePos to)
    {
        long id = ++_nextId;
        var view = _viewProvider();
        var path = _astar.FindPath(view, from, to);
        _ready[id] = new PathResult(
            path is null ? PathStatus.NoPath : PathStatus.Found,
            path,
            view.Version);
        return id;
    }

    // Atomic take: caller is the sole consumer of a result.
    public bool TryConsume(long id, out PathResult result)
    {
        if (_ready.Remove(id, out var hit))
        {
            result = hit;
            return true;
        }
        result = default;
        return false;
    }

    // Caller no longer needs the result (e.g. pawn picked a new goal).
    public void Discard(long id) => _ready.Remove(id);

    public int PendingCount => _ready.Count;
}
