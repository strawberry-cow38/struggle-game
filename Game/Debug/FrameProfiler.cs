using System.Diagnostics;

namespace StruggleGame.Game.Debug;

// Per-frame scoped timer registry with GC-aware accounting.
//
// The naive profiler attributed *all* time spent inside a scope to that
// scope — including GC pauses, OS context switches, and other jitter
// that happens to land while the scope is open. Result: random 4ms
// spikes on whichever section happened to be running when the GC ran.
//
// This rewrite tracks GC collection counts at frame start + scope
// boundaries. If any GC occurred during the frame, every per-section
// sample for that frame is marked "tainted" and excluded from the
// clean-max calculation. A separate GC row exposes the actual pause
// cost. The overlay shows clean avg + clean max + tainted-max so the
// real render cost is visible alongside the GC jitter.
//
// Usage stays the same:
//   FrameProfiler.Instance.BeginFrame();
//   using (FrameProfiler.Instance.BeginScope("Trees")) { ... }
//   FrameProfiler.Instance.EndFrame();
public sealed class FrameProfiler
{
    public static FrameProfiler Instance { get; } = new();

    public bool Enabled = false;

    private const int RingSize = 120;
    private readonly Dictionary<string, Section> _sections = new();
    private readonly List<Section> _ordered = new();

    public IReadOnlyList<Section> Sections => _ordered;

    // Frame-wide GC + total tracking.
    private long _frameStartTicks;
    private TimeSpan _frameStartGcPause;
    private int _frameStartGen0, _frameStartGen1, _frameStartGen2;
    private bool _frameStarted;

    private readonly double[] _frameRingMs = new double[RingSize];
    private readonly double[] _frameRingGcMs = new double[RingSize];
    private readonly bool[] _frameRingHadGc = new bool[RingSize];
    private int _frameRingIdx;
    private int _frameRingCount;

    // Per-frame Gen 0/1/2 delta rings — overlay sums for window-level
    // collection rate.
    private readonly byte[] _gen0Ring = new byte[RingSize];
    private readonly byte[] _gen1Ring = new byte[RingSize];
    private readonly byte[] _gen2Ring = new byte[RingSize];

    public Section Get(string name)
    {
        if (!_sections.TryGetValue(name, out var s))
        {
            s = new Section(name, RingSize);
            _sections[name] = s;
            _ordered.Add(s);
        }
        return s;
    }

    public void BeginFrame()
    {
        if (!Enabled) return;
        _frameStartTicks = Stopwatch.GetTimestamp();
        _frameStartGcPause = System.GC.GetTotalPauseDuration();
        _frameStartGen0 = System.GC.CollectionCount(0);
        _frameStartGen1 = System.GC.CollectionCount(1);
        _frameStartGen2 = System.GC.CollectionCount(2);
        _frameStarted = true;
    }

    public Scope BeginScope(string name) => Enabled ? new Scope(Get(name)) : default;

    public void EndFrame()
    {
        if (!Enabled || !_frameStarted)
        {
            // Still commit per-section accumulators if any timing
            // happened without a BeginFrame call — keeps the section
            // ring aligned with frame count.
            foreach (var s in _ordered) s.Commit(tainted: false);
            return;
        }
        _frameStarted = false;

        double frameMs = (Stopwatch.GetTimestamp() - _frameStartTicks) * 1000.0 / Stopwatch.Frequency;
        var gcNow = System.GC.GetTotalPauseDuration();
        double gcMs = (gcNow - _frameStartGcPause).TotalMilliseconds;
        int g0 = System.GC.CollectionCount(0) - _frameStartGen0;
        int g1 = System.GC.CollectionCount(1) - _frameStartGen1;
        int g2 = System.GC.CollectionCount(2) - _frameStartGen2;
        bool hadGc = (g0 + g1 + g2) > 0 || gcMs > 0.01;

        _frameRingMs[_frameRingIdx] = frameMs;
        _frameRingGcMs[_frameRingIdx] = gcMs;
        _frameRingHadGc[_frameRingIdx] = hadGc;
        _gen0Ring[_frameRingIdx] = (byte)Math.Min(g0, 255);
        _gen1Ring[_frameRingIdx] = (byte)Math.Min(g1, 255);
        _gen2Ring[_frameRingIdx] = (byte)Math.Min(g2, 255);
        _frameRingIdx = (_frameRingIdx + 1) % RingSize;
        if (_frameRingCount < RingSize) _frameRingCount++;

        foreach (var s in _ordered) s.Commit(hadGc);
    }

    public FrameStats FrameRingStats()
    {
        if (_frameRingCount == 0) return default;
        double sum = 0, max = 0, gcSum = 0, gcMax = 0;
        double windowMs = 0;
        int gcFrames = 0;
        int gen0 = 0, gen1 = 0, gen2 = 0;
        for (int i = 0; i < _frameRingCount; i++)
        {
            sum += _frameRingMs[i];
            windowMs += _frameRingMs[i];
            if (_frameRingMs[i] > max) max = _frameRingMs[i];
            gcSum += _frameRingGcMs[i];
            if (_frameRingGcMs[i] > gcMax) gcMax = _frameRingGcMs[i];
            if (_frameRingHadGc[i]) gcFrames++;
            gen0 += _gen0Ring[i];
            gen1 += _gen1Ring[i];
            gen2 += _gen2Ring[i];
        }
        return new FrameStats
        {
            FrameCount = _frameRingCount,
            FrameAvgMs = sum / _frameRingCount,
            FrameMaxMs = max,
            GcAvgMs = gcSum / _frameRingCount,
            GcMaxMs = gcMax,
            GcFramePct = 100.0 * gcFrames / _frameRingCount,
            Gen0PerSec = windowMs > 0 ? gen0 * 1000.0 / windowMs : 0,
            Gen1PerSec = windowMs > 0 ? gen1 * 1000.0 / windowMs : 0,
            Gen2PerSec = windowMs > 0 ? gen2 * 1000.0 / windowMs : 0,
        };
    }

    public ref struct Scope
    {
        private readonly Section? _section;
        private readonly long _startTicks;
        public Scope(Section section)
        {
            _section = section;
            _startTicks = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            if (_section is null) return;
            long delta = Stopwatch.GetTimestamp() - _startTicks;
            double ms = delta * 1000.0 / Stopwatch.Frequency;
            _section.Add(ms);
        }
    }

    public struct FrameStats
    {
        public int FrameCount;
        public double FrameAvgMs;
        public double FrameMaxMs;
        public double GcAvgMs;
        public double GcMaxMs;
        public double GcFramePct;
        public double Gen0PerSec;
        public double Gen1PerSec;
        public double Gen2PerSec;
    }

    public sealed class Section
    {
        public string Name { get; }
        private readonly double[] _ring;
        private readonly bool[] _tainted;
        private int _ringIdx;
        private int _ringCount;
        private double _curFrameMs;

        public Section(string name, int ringSize)
        {
            Name = name;
            _ring = new double[ringSize];
            _tainted = new bool[ringSize];
        }

        public void Add(double ms) => _curFrameMs += ms;

        public void Commit(bool tainted)
        {
            _ring[_ringIdx] = _curFrameMs;
            _tainted[_ringIdx] = tainted;
            _ringIdx = (_ringIdx + 1) % _ring.Length;
            if (_ringCount < _ring.Length) _ringCount++;
            _curFrameMs = 0;
        }

        // Clean stats: only frames where no GC ran. The clean max is the
        // honest per-section render cost; tainted max is what the old
        // profiler reported (includes GC pause that happened to land in
        // this scope) so you can still see the worst-case wall time.
        public SectionStats Stats()
        {
            if (_ringCount == 0) return default;
            double sum = 0; int cleanCount = 0; double cleanMax = 0;
            double maxAny = 0;
            for (int i = 0; i < _ringCount; i++)
            {
                double v = _ring[i];
                if (v > maxAny) maxAny = v;
                if (_tainted[i]) continue;
                sum += v;
                cleanCount++;
                if (v > cleanMax) cleanMax = v;
            }
            return new SectionStats
            {
                CleanAvgMs = cleanCount > 0 ? sum / cleanCount : 0,
                CleanMaxMs = cleanMax,
                TaintedMaxMs = maxAny,
                CleanFrameCount = cleanCount,
            };
        }
    }

    public struct SectionStats
    {
        public double CleanAvgMs;
        public double CleanMaxMs;
        public double TaintedMaxMs;
        public int CleanFrameCount;
    }
}
