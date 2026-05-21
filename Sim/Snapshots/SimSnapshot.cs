using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.Snapshots;

// Immutable per-tick render data. Game reads this; Sim builds it at end
// of tick and publishes via Volatile.Write. No locks on the hot path.
public sealed class SimSnapshot
{
    public long Tick { get; }
    public long MapVersion { get; }
    public DummyState[] Dummies { get; }
    public BlueprintState[] Blueprints { get; }

    public SimSnapshot(long tick, long mapVersion, DummyState[] dummies, BlueprintState[] blueprints)
    {
        Tick = tick;
        MapVersion = mapVersion;
        Dummies = dummies;
        Blueprints = blueprints;
    }
}

public readonly record struct DummyState(float X, float Y, string Job);

// Progress = 0..1 normalised by BuildSystem.BuildTimeSec.
public readonly record struct BlueprintState(TilePos Tile, float Progress);
