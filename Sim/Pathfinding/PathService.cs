using System;
using System.Collections.Concurrent;
using System.Threading;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.Pathfinding;

public enum PathStatus { Pending, Found, NoPath }

public readonly record struct PathResult(PathStatus Status, List<TilePos>? Path, long MapVersion);

// Async path request facade. Request() enqueues onto a small pool of dedicated
// background workers and returns an id immediately — the A* search runs OFF the
// sim thread, so a burst of move orders never blocks the tick. Callers poll
// TryConsume(id) on later ticks until the result lands (pawns already do this),
// and Discard ids they abandon.
//
// Dedicated (not ThreadPool) workers: they sit blocked on the queue and react
// instantly, which the ThreadPool can't guarantee when the sim thread is busy
// (its lazy scheduling starves path jobs). Each worker owns its own AStar
// (AStar isn't thread-safe). Each request captures the current immutable
// MapView, so workers read map data lock-free and results carry the MapVersion
// they were computed against for staleness checks. Workers are background, so
// they're reclaimed at process exit — there's exactly one PathService per game.
public sealed class PathService
{
    private readonly Func<MapView> _viewProvider;
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly ConcurrentDictionary<long, PathResult> _ready = new();
    // Ids still wanted (not consumed/discarded). A worker skips storing a
    // result for an id discarded before it finished.
    private readonly ConcurrentDictionary<long, byte> _pending = new();
    private long _nextId; // sim thread only
    // A standing soft-avoid applied to every request that doesn't pass its own
    // (e.g. tiles occupied by stationary colonists). Published once per tick by
    // the sim; MUST be swapped to a fresh immutable set, never mutated in place,
    // since worker threads read the reference captured into a WorkItem.
    private IReadOnlySet<TilePos>? _defaultAvoid;
    private float _defaultAvoidPenalty;
    // async=false runs A* inline (same tick, deterministic) — used by tests and
    // the harness so results don't depend on worker-thread timing. async=true
    // is the game path: off-thread workers.
    private readonly bool _async;
    private readonly AStar? _syncAstar;

    private readonly struct WorkItem
    {
        public readonly long Id;
        public readonly TilePos From;
        public readonly TilePos To;
        public readonly MapView View;
        public readonly IReadOnlySet<TilePos>? Avoid;
        public readonly float AvoidPenalty;
        public WorkItem(long id, TilePos from, TilePos to, MapView view, IReadOnlySet<TilePos>? avoid, float avoidPenalty)
        { Id = id; From = from; To = to; View = view; Avoid = avoid; AvoidPenalty = avoidPenalty; }
    }

    public PathService(int width, int height, Func<MapView> viewProvider, bool async = false)
    {
        _viewProvider = viewProvider;
        _async = async;
        if (!async)
        {
            _syncAstar = new AStar(width, height);
            return;
        }
        int workers = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);
        for (int i = 0; i < workers; i++)
        {
            var astar = new AStar(width, height); // per-worker (AStar isn't thread-safe)
            var t = new Thread(() => WorkerLoop(astar))
            {
                IsBackground = true,
                Name = $"PathWorker{i}",
                Priority = ThreadPriority.BelowNormal,
            };
            t.Start();
        }
    }

    // Sim-thread only. Async: queue for a worker. Sync: compute inline now.
    // avoid/avoidPenalty: tiles to route around (extra step cost) — must be an
    // IMMUTABLE set; a worker may read it on another thread.
    // Sim-thread only. Sets the standing soft-avoid for subsequent requests.
    // Pass a FRESH immutable set each time (don't mutate the previous one).
    public void SetDefaultAvoid(IReadOnlySet<TilePos>? avoid, float penalty)
    {
        _defaultAvoid = avoid;
        _defaultAvoidPenalty = penalty;
    }

    public long Request(TilePos from, TilePos to, IReadOnlySet<TilePos>? avoid = null, float avoidPenalty = 0f)
    {
        // Fall back to the standing per-tick avoid (stationary colonists) when
        // the caller doesn't specify one.
        if (avoid is null) { avoid = _defaultAvoid; avoidPenalty = _defaultAvoidPenalty; }
        long id = ++_nextId;
        _pending[id] = 0;
        var view = _viewProvider();
        if (_async)
        {
            _queue.Add(new WorkItem(id, from, to, view, avoid, avoidPenalty));
        }
        else
        {
            var path = _syncAstar!.FindPath(view, from, to, avoid, avoidPenalty);
            _ready[id] = new PathResult(
                path is null ? PathStatus.NoPath : PathStatus.Found, path, view.Version);
        }
        return id;
    }

    private void WorkerLoop(AStar astar)
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            if (!_pending.ContainsKey(item.Id)) continue; // discarded before we ran
            List<TilePos>? path;
            try { path = astar.FindPath(item.View, item.From, item.To, item.Avoid, item.AvoidPenalty); }
            catch { path = null; }
            var result = new PathResult(
                path is null ? PathStatus.NoPath : PathStatus.Found, path, item.View.Version);
            if (_pending.ContainsKey(item.Id)) _ready[item.Id] = result;
        }
    }

    // Sim-thread only. Returns false until a worker has finished this id —
    // the caller keeps the request id and polls again next tick.
    public bool TryConsume(long id, out PathResult result)
    {
        if (_ready.TryRemove(id, out var hit))
        {
            _pending.TryRemove(id, out _);
            result = hit;
            return true;
        }
        result = default;
        return false;
    }

    // Caller no longer needs the result (e.g. pawn picked a new goal).
    public void Discard(long id)
    {
        _pending.TryRemove(id, out _);
        _ready.TryRemove(id, out _);
    }

    public int PendingCount => _pending.Count + _ready.Count;
}
