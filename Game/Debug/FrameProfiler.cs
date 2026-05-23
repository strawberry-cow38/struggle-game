using System.Diagnostics;

namespace StruggleGame.Game.Debug;

// Per-frame scoped timer registry. Callers wrap blocks with
//   using (FrameProfiler.Instance.Scope("Stockpiles")) { ... }
// and the profiler accumulates the elapsed ms into the named section
// for the current frame. EndFrame() rolls the accumulator into a
// 60-frame ring so the overlay can show rolling avg + max.
public sealed class FrameProfiler
{
    public static FrameProfiler Instance { get; } = new();

    private const int RingSize = 60;
    private readonly Dictionary<string, Section> _sections = new();
    private readonly List<Section> _ordered = new();

    public IReadOnlyList<Section> Sections => _ordered;

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

    public Scope BeginScope(string name) => new Scope(Get(name));

    public void EndFrame()
    {
        foreach (var s in _ordered) s.Commit();
    }

    public ref struct Scope
    {
        private readonly Section _section;
        private readonly long _startTicks;
        public Scope(Section section)
        {
            _section = section;
            _startTicks = Stopwatch.GetTimestamp();
        }
        public void Dispose()
        {
            long delta = Stopwatch.GetTimestamp() - _startTicks;
            double ms = delta * 1000.0 / Stopwatch.Frequency;
            _section.Add(ms);
        }
    }

    public sealed class Section
    {
        public string Name { get; }
        private readonly double[] _ring;
        private int _ringIdx;
        private double _curFrameMs;

        public Section(string name, int ringSize)
        {
            Name = name;
            _ring = new double[ringSize];
        }

        public void Add(double ms) => _curFrameMs += ms;

        public void Commit()
        {
            _ring[_ringIdx] = _curFrameMs;
            _ringIdx = (_ringIdx + 1) % _ring.Length;
            _curFrameMs = 0;
        }

        public double AvgMs()
        {
            double s = 0;
            for (int i = 0; i < _ring.Length; i++) s += _ring[i];
            return s / _ring.Length;
        }

        public double MaxMs()
        {
            double m = 0;
            for (int i = 0; i < _ring.Length; i++) if (_ring[i] > m) m = _ring[i];
            return m;
        }

        public double LastMs() => _ring[(_ringIdx - 1 + _ring.Length) % _ring.Length];
    }
}
