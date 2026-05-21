using System.Diagnostics;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game;

// Owns the SimRuntime and runs it on a dedicated thread at the fixed
// tick rate. Publishes the latest immutable SimSnapshot via Volatile.Write
// so the Godot main thread can read without locking. Game code must NEVER
// touch SimRuntime.Store directly.
public sealed class SimHost : IDisposable
{
    private readonly SimRuntime _sim;
    private readonly Thread _thread;
    private volatile bool _running;
    private volatile int _tickHz = SimConstants.TickHz;
    private SimSnapshot? _latest;

    public TileMap Map => _sim.Map;

    public int TickHz => _tickHz;

    public SimHost()
    {
        _sim = new SimRuntime();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sim" };
        _thread.Start();
    }

    public SimSnapshot? LatestSnapshot => Volatile.Read(ref _latest);

    // Switch tick rate at runtime. Loop reads _tickHz each iteration so the
    // change picks up next tick boundary without a restart.
    public void SetTickHz(int hz)
    {
        if (hz < 1) hz = 1;
        _tickHz = hz;
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long nextTick = sw.ElapsedTicks;

        while (_running)
        {
            int hz = _tickHz;
            long tickStride = Stopwatch.Frequency / hz;
            float dt = 1f / hz;

            long now = sw.ElapsedTicks;
            if (now >= nextTick)
            {
                _sim.Step(dt);
                Volatile.Write(ref _latest, _sim.BuildSnapshot());
                nextTick += tickStride;
                // If we fell badly behind (paused breakpoint, hz bump, etc.),
                // don't try to catch up by spamming — resync to now.
                if (nextTick < now - tickStride * 4) nextTick = now + tickStride;
            }
            else
            {
                long sleepTicks = nextTick - now;
                long sleepMs = sleepTicks * 1000 / Stopwatch.Frequency;
                if (sleepMs > 1) Thread.Sleep(1); else Thread.SpinWait(64);
            }
        }
    }
}
