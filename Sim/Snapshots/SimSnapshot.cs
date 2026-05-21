namespace StruggleGame.Sim.Snapshots;

// Immutable per-tick render data. Game reads this; Sim builds it at end
// of tick and publishes via Volatile.Write. No locks on the hot path.
public sealed class SimSnapshot
{
    public long Tick { get; }
    public DummyState[] Dummies { get; }

    public SimSnapshot(long tick, DummyState[] dummies)
    {
        Tick = tick;
        Dummies = dummies;
    }
}

public readonly record struct DummyState(float X, float Y);
