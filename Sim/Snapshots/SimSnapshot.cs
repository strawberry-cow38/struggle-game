using StruggleGame.Sim.Map;
using StruggleGame.Sim.Stockpiles;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim.Snapshots;

// Immutable per-tick render data. Game reads this; Sim builds it at end
// of tick and publishes via Volatile.Write. No locks on the hot path.
public sealed class SimSnapshot
{
    public long Tick { get; }
    public long MapVersion { get; }
    public long RoomVersion { get; }
    public int RoomCount { get; }
    // Bumped whenever the roof or no-roof layer changes (auto-roof,
    // paint, remove, no-roof toggle). Renderer keys overlay rebuilds
    // off this exactly like RoomVersion drives the room overlay.
    public long RoofVersion { get; }
    public DummyState[] Dummies { get; }
    public BlueprintState[] Blueprints { get; }
    public BlueprintState[] FloorBlueprints { get; }
    public TreeState[] Trees { get; }
    public CropState[] Crops { get; }
    public WoodState[] Wood { get; }
    public ItemPileState[] ItemPiles { get; }
    public DeconState[] Decons { get; }
    public BlueprintState[] DoorBlueprints { get; }
    public DoorRenderState[] Doors { get; }
    public StockpileState[] Stockpiles { get; }
    public GrowZoneState[] GrowZones { get; }
    // Pending RoofBuild / RoofRemove jobs. Build=true draws as a roof
    // blueprint outline; Build=false draws as a remove-X over the
    // already-roofed tile.
    public RoofBlueprintState[] RoofBlueprints { get; }

    // Set when the game has a selected colonist; null otherwise.
    public int? SelectedDummyId { get; }
    public TilePos[]? SelectedPath { get; }
    public TilePos[]? SelectedOrders { get; }

    // Set of tree entity ids the player has selected. May be empty.
    public int[] SelectedTreeIds { get; }

    // Set of wood/item stack entity ids the player has selected. May be empty.
    public int[] SelectedWoodIds { get; }

    public SimSnapshot(
        long tick,
        long mapVersion,
        long roomVersion,
        int roomCount,
        long roofVersion,
        DummyState[] dummies,
        BlueprintState[] blueprints,
        BlueprintState[] floorBlueprints,
        TreeState[] trees,
        CropState[] crops,
        WoodState[] wood,
        ItemPileState[] itemPiles,
        DeconState[] decons,
        BlueprintState[] doorBlueprints,
        DoorRenderState[] doors,
        StockpileState[] stockpiles,
        GrowZoneState[] growZones,
        RoofBlueprintState[] roofBlueprints,
        int? selectedDummyId = null,
        TilePos[]? selectedPath = null,
        TilePos[]? selectedOrders = null,
        int[]? selectedTreeIds = null,
        int[]? selectedWoodIds = null)
    {
        Tick = tick;
        MapVersion = mapVersion;
        RoomVersion = roomVersion;
        RoomCount = roomCount;
        RoofVersion = roofVersion;
        Dummies = dummies;
        Blueprints = blueprints;
        FloorBlueprints = floorBlueprints;
        Trees = trees;
        Crops = crops;
        Wood = wood;
        ItemPiles = itemPiles;
        Decons = decons;
        DoorBlueprints = doorBlueprints;
        Doors = doors;
        Stockpiles = stockpiles;
        GrowZones = growZones;
        RoofBlueprints = roofBlueprints;
        SelectedDummyId = selectedDummyId;
        SelectedPath = selectedPath;
        SelectedOrders = selectedOrders;
        SelectedTreeIds = selectedTreeIds ?? Array.Empty<int>();
        SelectedWoodIds = selectedWoodIds ?? Array.Empty<int>();
    }
}

public readonly record struct DummyState(
    int EntityId,
    float X,
    float Y,
    string Job,
    bool Drafted,
    bool Carrying,
    CarriedItemState[] Inventory,
    float CarryWeight,
    float CarryBulk,
    float MaxCarryWeight,
    float MaxCarryBulk);

// One inventory slot surfaced to the UI. SlotEntityId is the underlying
// item entity (the same id the carry/drop commands reference).
public readonly record struct CarriedItemState(int SlotEntityId, string ItemPath, int Count, bool Forbidden);

// Progress = 0..1 normalised by BuildSystem.BuildTimeSec. Forbidden
// blueprints are skipped by builders and rendered with a red X overlay.
public readonly record struct BlueprintState(TilePos Tile, float Progress, bool Forbidden);

// EntityId lets the game thread reference a tree (selection, hit-test).
// ChopProgress = 0..1 normalised by ChopSystem.ChopTimeSec; 0 if no
// active chop job on the tile.
public readonly record struct TreeState(int EntityId, TilePos Tile, float ChopProgress, bool HasJob, float GrowthStage);

public readonly record struct WoodState(int EntityId, TilePos Tile, int Count, string ItemPath, bool Forbidden);

// A planted crop. WorkProgress = 0..1 normalised by the active job's
// duration (CutPlantSystem.CutTimeSec for CutPlants, HarvestSystem.HarvestTimeSec
// for Harvest); 0 if no job. JobKind distinguishes the two for rendering.
public readonly record struct CropState(
    int EntityId,
    TilePos Tile,
    CropKind Kind,
    float GrowthStage,
    float WorkProgress,
    Jobs.JobKind? ActiveJob);

// Dropped non-wood item pile (carrots etc). Not haulable yet; lives on
// the ground for visualization only.
public readonly record struct ItemPileState(int EntityId, TilePos Tile, int Count, string ItemPath);

// Decon mark on a wall. Progress = 0..1 normalised by DeconSystem.DeconTimeSec.
public readonly record struct DeconState(TilePos Tile, float Progress, bool Forbidden);

// Built door's current render state. OpenAmount = 0 (closed) .. 1 (fully
// open). Orientation drives which axis the door swings on. Forbidden +
// Locked are player toggles surfaced to the info panel.
public readonly record struct DoorRenderState(
    TilePos Tile,
    DoorOrientation Orientation,
    float OpenAmount,
    bool Forbidden,
    bool Locked,
    StruggleGame.Sim.World.DoorPriority Priority);

// Render-friendly stockpile zone. Tiles is a frozen snapshot of the
// zone's tile set as of build time; AllowedItemPaths captures the
// filter so the panel UI doesn't need to ask the sim back. Both
// arrays may be empty (zero-tile zone is legal during expand/shrink
// mid-edit).
public readonly record struct StockpileState(
    int Id,
    string Name,
    StockpilePriority Priority,
    TilePos[] Tiles,
    string[] AllowedItemPaths);

// Pending roof job. Progress = 0..1 normalised by RoofSystem build /
// remove time. Build=true paints as a roof blueprint outline; false
// paints as an X over the already-roofed tile.
public readonly record struct RoofBlueprintState(TilePos Tile, float Progress, bool Build, bool Forbidden);

// Render-friendly grow zone. Mirror of StockpileState. AllowCutting +
// AllowSowing drive the manager's auto-job posting; CropKind decides
// what counts as "matching" (kept vs. cut) and what gets sown.
public readonly record struct GrowZoneState(
    int Id,
    string Name,
    World.CropKind CropKind,
    bool AllowCutting,
    bool AllowSowing,
    TilePos[] Tiles);
