using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.Pathfinding;

// 8-connected grid A* with octile heuristic. Returns waypoints from start
// to goal (inclusive of both). Returns null if no path. Reuses internal
// buffers across calls; not thread-safe — give each worker its own
// instance. Takes a MapView per call so a worker pool can compute against
// an immutable snapshot without touching the live TileMap.
public sealed class AStar
{
    private static readonly (int dx, int dy, float cost)[] Neighbors = new (int, int, float)[]
    {
        (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
        (1, 1, 1.4142136f), (1, -1, 1.4142136f), (-1, 1, 1.4142136f), (-1, -1, 1.4142136f),
    };

    private readonly int _width;
    private readonly int _height;
    private readonly float[] _gScore;
    private readonly int[] _cameFrom;
    private readonly bool[] _closed;
    private readonly int[] _generation;
    private int _runId;
    private readonly PriorityQueue<int, float> _open = new();

    public AStar(int width, int height)
    {
        _width = width;
        _height = height;
        int cells = width * height;
        _gScore = new float[cells];
        _cameFrom = new int[cells];
        _closed = new bool[cells];
        _generation = new int[cells];
    }

    public List<TilePos>? FindPath(MapView view, TilePos start, TilePos goal)
    {
        if (view.Width != _width || view.Height != _height)
        {
            throw new ArgumentException("MapView size doesn't match AStar buffer size.", nameof(view));
        }
        if (!view.Walkable(start) || !view.Walkable(goal)) return null;
        if (start == goal) return new List<TilePos> { start };

        _runId++;
        _open.Clear();

        int startIdx = Index(start.X, start.Y);
        int goalIdx = Index(goal.X, goal.Y);

        _gScore[startIdx] = 0;
        _generation[startIdx] = _runId;
        _cameFrom[startIdx] = -1;
        _closed[startIdx] = false;
        _open.Enqueue(startIdx, Heuristic(start, goal));

        while (_open.Count > 0)
        {
            int currentIdx = _open.Dequeue();
            // _closed leaks across runs; gate by current generation.
            if (_generation[currentIdx] == _runId && _closed[currentIdx]) continue;
            if (currentIdx == goalIdx) return Reconstruct(goalIdx);
            _closed[currentIdx] = true;

            int cx = currentIdx % _width;
            int cy = currentIdx / _width;

            foreach (var (dx, dy, cost) in Neighbors)
            {
                int nx = cx + dx;
                int ny = cy + dy;
                if (!view.Walkable(nx, ny)) continue;
                // Disallow diagonal cut through wall corner.
                if (dx != 0 && dy != 0 && (!view.Walkable(cx + dx, cy) || !view.Walkable(cx, cy + dy)))
                {
                    continue;
                }

                int nIdx = Index(nx, ny);
                if (_generation[nIdx] == _runId && _closed[nIdx]) continue;

                float tentativeG = _gScore[currentIdx] + cost;
                if (_generation[nIdx] != _runId || tentativeG < _gScore[nIdx])
                {
                    _generation[nIdx] = _runId;
                    _gScore[nIdx] = tentativeG;
                    _cameFrom[nIdx] = currentIdx;
                    _closed[nIdx] = false;
                    float f = tentativeG + Heuristic(new TilePos(nx, ny), goal);
                    _open.Enqueue(nIdx, f);
                }
            }
        }
        return null;
    }

    private List<TilePos> Reconstruct(int goalIdx)
    {
        var stack = new Stack<TilePos>();
        int cur = goalIdx;
        while (cur != -1)
        {
            stack.Push(new TilePos(cur % _width, cur / _width));
            cur = _cameFrom[cur];
        }
        return new List<TilePos>(stack);
    }

    private int Index(int x, int y) => y * _width + x;

    private static float Heuristic(TilePos a, TilePos b)
    {
        int dx = Math.Abs(a.X - b.X);
        int dy = Math.Abs(a.Y - b.Y);
        int min = Math.Min(dx, dy);
        int max = Math.Max(dx, dy);
        return (max - min) + 1.4142136f * min;
    }
}
