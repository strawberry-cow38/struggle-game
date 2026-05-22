using System.Diagnostics;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Diagnostics;
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
    private volatile float _actualTps;
    // -1 means "no selection". Volatile int so the game thread can write
    // and the sim thread can read each tick without locking.
    private int _selectedDummyId = -1;

    // Tree selection set. Game thread writes via SelectedTreeIds setter
    // (replaces atomically); sim thread reads via Volatile each tick.
    private int[] _selectedTreeIds = Array.Empty<int>();

    public TileMap Map => _sim.Map;
    public SimWatcher Watcher => _sim.Watcher;

    public int TickHz => _tickHz;

    public float ActualTps => _actualTps;

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

    public int? SelectedDummyId
    {
        get { int v = Volatile.Read(ref _selectedDummyId); return v >= 0 ? v : null; }
        set { Volatile.Write(ref _selectedDummyId, value ?? -1); }
    }

    public int[] SelectedTreeIds
    {
        get => Volatile.Read(ref _selectedTreeIds);
        set => Volatile.Write(ref _selectedTreeIds, value ?? Array.Empty<int>());
    }

    // Game→Sim command submission. Drained at the start of every tick.
    public void QueueCommand(ISimCommand cmd) => _sim.QueueCommand(cmd);

    // Threadsafe map snapshot for rebuilding the wall overlay texture
    // when MapVersion changes.
    public byte[] CopyTilesForRender() => _sim.CopyTilesForRender();

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long nextTick = sw.ElapsedTicks;
        long windowStartMs = sw.ElapsedMilliseconds;
        int ticksThisWindow = 0;

        while (_running)
        {
            int hz = _tickHz;
            long tickStride = Stopwatch.Frequency / hz;
            float dt = 1f / hz;

            long now = sw.ElapsedTicks;
            if (now >= nextTick)
            {
                _sim.Step(dt);
                int sel = Volatile.Read(ref _selectedDummyId);
                var trees = Volatile.Read(ref _selectedTreeIds);
                Volatile.Write(ref _latest, _sim.BuildSnapshot(sel >= 0 ? sel : null, trees.Length > 0 ? trees : null));
                ticksThisWindow++;
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

            long elapsedMs = sw.ElapsedMilliseconds;
            long windowMs = elapsedMs - windowStartMs;
            if (windowMs >= 500)
            {
                _actualTps = ticksThisWindow * 1000f / windowMs;
                ticksThisWindow = 0;
                windowStartMs = elapsedMs;
            }
        }
    }
}
