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

    // Set when the game has a selected colonist; null otherwise.
    public int? SelectedDummyId { get; }
    public TilePos[]? SelectedPath { get; }
    public TilePos[]? SelectedOrders { get; }

    public SimSnapshot(
        long tick,
        long mapVersion,
        DummyState[] dummies,
        BlueprintState[] blueprints,
        int? selectedDummyId = null,
        TilePos[]? selectedPath = null,
        TilePos[]? selectedOrders = null)
    {
        Tick = tick;
        MapVersion = mapVersion;
        Dummies = dummies;
        Blueprints = blueprints;
        SelectedDummyId = selectedDummyId;
        SelectedPath = selectedPath;
        SelectedOrders = selectedOrders;
    }
}

public readonly record struct DummyState(int EntityId, float X, float Y, string Job, bool Drafted);

// Progress = 0..1 normalised by BuildSystem.BuildTimeSec.
public readonly record struct BlueprintState(TilePos Tile, float Progress);
