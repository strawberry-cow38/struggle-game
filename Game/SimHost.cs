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
    private SimSnapshot? _latest;

    public TileMap Map => _sim.Map;

    public SimHost()
    {
        _sim = new SimRuntime();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sim" };
        _thread.Start();
    }

    public SimSnapshot? LatestSnapshot => Volatile.Read(ref _latest);

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long tickStride = Stopwatch.Frequency / SimConstants.TickHz;
        long nextTick = sw.ElapsedTicks;

        while (_running)
        {
            long now = sw.ElapsedTicks;
            if (now >= nextTick)
            {
                _sim.Step();
                Volatile.Write(ref _latest, _sim.BuildSnapshot());
                nextTick += tickStride;
                // If we fell badly behind (paused breakpoint etc.), don't
                // try to catch up by spamming — resync to now.
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
