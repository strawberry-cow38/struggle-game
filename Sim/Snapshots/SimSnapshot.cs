using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim.Snapshots;

// Immutable per-tick render data. Game reads this; Sim builds it at end
// of tick and publishes via Volatile.Write. No locks on the hot path.
public sealed class SimSnapshot
{
    public long Tick { get; }
    public long MapVersion { get; }
    public DummyState[] Dummies { get; }
    public BlueprintState[] Blueprints { get; }
    public BlueprintState[] FloorBlueprints { get; }
    public TreeState[] Trees { get; }
    public WoodState[] Wood { get; }
    public DeconState[] Decons { get; }
    public BlueprintState[] DoorBlueprints { get; }
    public DoorRenderState[] Doors { get; }

    // Set when the game has a selected colonist; null otherwise.
    public int? SelectedDummyId { get; }
    public TilePos[]? SelectedPath { get; }
    public TilePos[]? SelectedOrders { get; }

    // Set of tree entity ids the player has selected. May be empty.
    public int[] SelectedTreeIds { get; }

    public SimSnapshot(
        long tick,
        long mapVersion,
        DummyState[] dummies,
        BlueprintState[] blueprints,
        BlueprintState[] floorBlueprints,
        TreeState[] trees,
        WoodState[] wood,
        DeconState[] decons,
        BlueprintState[] doorBlueprints,
        DoorRenderState[] doors,
        int? selectedDummyId = null,
        TilePos[]? selectedPath = null,
        TilePos[]? selectedOrders = null,
        int[]? selectedTreeIds = null)
    {
        Tick = tick;
        MapVersion = mapVersion;
        Dummies = dummies;
        Blueprints = blueprints;
        FloorBlueprints = floorBlueprints;
        Trees = trees;
        Wood = wood;
        Decons = decons;
        DoorBlueprints = doorBlueprints;
        Doors = doors;
        SelectedDummyId = selectedDummyId;
        SelectedPath = selectedPath;
        SelectedOrders = selectedOrders;
        SelectedTreeIds = selectedTreeIds ?? Array.Empty<int>();
    }
}

public readonly record struct DummyState(int EntityId, float X, float Y, string Job, bool Drafted);

// Progress = 0..1 normalised by BuildSystem.BuildTimeSec.
public readonly record struct BlueprintState(TilePos Tile, float Progress);

// EntityId lets the game thread reference a tree (selection, hit-test).
// ChopProgress = 0..1 normalised by ChopSystem.ChopTimeSec; 0 if no
// active chop job on the tile.
public readonly record struct TreeState(int EntityId, TilePos Tile, float ChopProgress, bool HasJob);

public readonly record struct WoodState(TilePos Tile);

// Decon mark on a wall. Progress = 0..1 normalised by DeconSystem.DeconTimeSec.
public readonly record struct DeconState(TilePos Tile, float Progress);

// Built door's current render state. OpenAmount = 0 (closed) .. 1 (fully
// open). Orientation drives which axis the door swings on.
public readonly record struct DoorRenderState(TilePos Tile, DoorOrientation Orientation, float OpenAmount);
