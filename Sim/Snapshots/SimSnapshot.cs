using StruggleGame.Sim.Map;
using StruggleGame.Sim.Stockpiles;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim.Snapshots;

// Per-tick render data. SimRuntime maintains two of these (a double
// buffer) and alternates which slot it fills each tick, so the renderer
// can keep reading the previously published instance while the next is
// being built. Section arrays are oversized + reused across ticks; the
// public SnapshotList<T> view exposes only the valid prefix.
public sealed class SimSnapshot
{
    public long Tick { get; internal set; }
    public long MapVersion { get; internal set; }
    public long RoomVersion { get; internal set; }
    public int RoomCount { get; internal set; }
    public long RoofVersion { get; internal set; }
    public long LightVersion { get; internal set; }
    public double WorldTimeSec { get; internal set; }

    internal DummyState[] DummiesBuf = System.Array.Empty<DummyState>();
    internal int DummiesCount;
    public SnapshotList<DummyState> Dummies => new(DummiesBuf, DummiesCount);

    internal BlueprintState[] BlueprintsBuf = System.Array.Empty<BlueprintState>();
    internal int BlueprintsCount;
    public SnapshotList<BlueprintState> Blueprints => new(BlueprintsBuf, BlueprintsCount);

    internal BlueprintState[] FloorBlueprintsBuf = System.Array.Empty<BlueprintState>();
    internal int FloorBlueprintsCount;
    public SnapshotList<BlueprintState> FloorBlueprints => new(FloorBlueprintsBuf, FloorBlueprintsCount);

    internal TreeState[] TreesBuf = System.Array.Empty<TreeState>();
    internal int TreesCount;
    public SnapshotList<TreeState> Trees => new(TreesBuf, TreesCount);

    internal CropState[] CropsBuf = System.Array.Empty<CropState>();
    internal int CropsCount;
    public SnapshotList<CropState> Crops => new(CropsBuf, CropsCount);

    internal WoodState[] WoodBuf = System.Array.Empty<WoodState>();
    internal int WoodCount;
    public SnapshotList<WoodState> Wood => new(WoodBuf, WoodCount);

    internal ItemPileState[] ItemPilesBuf = System.Array.Empty<ItemPileState>();
    internal int ItemPilesCount;
    public SnapshotList<ItemPileState> ItemPiles => new(ItemPilesBuf, ItemPilesCount);

    internal DeconState[] DeconsBuf = System.Array.Empty<DeconState>();
    internal int DeconsCount;
    public SnapshotList<DeconState> Decons => new(DeconsBuf, DeconsCount);

    internal BlueprintState[] DoorBlueprintsBuf = System.Array.Empty<BlueprintState>();
    internal int DoorBlueprintsCount;
    public SnapshotList<BlueprintState> DoorBlueprints => new(DoorBlueprintsBuf, DoorBlueprintsCount);

    internal DoorRenderState[] DoorsBuf = System.Array.Empty<DoorRenderState>();
    internal int DoorsCount;
    public SnapshotList<DoorRenderState> Doors => new(DoorsBuf, DoorsCount);

    internal StockpileState[] StockpilesBuf = System.Array.Empty<StockpileState>();
    internal int StockpilesCount;
    public SnapshotList<StockpileState> Stockpiles => new(StockpilesBuf, StockpilesCount);

    internal GrowZoneState[] GrowZonesBuf = System.Array.Empty<GrowZoneState>();
    internal int GrowZonesCount;
    public SnapshotList<GrowZoneState> GrowZones => new(GrowZonesBuf, GrowZonesCount);

    internal RoofBlueprintState[] RoofBlueprintsBuf = System.Array.Empty<RoofBlueprintState>();
    internal int RoofBlueprintsCount;
    public SnapshotList<RoofBlueprintState> RoofBlueprints => new(RoofBlueprintsBuf, RoofBlueprintsCount);

    internal LampState[] LampsBuf = System.Array.Empty<LampState>();
    internal int LampsCount;
    public SnapshotList<LampState> Lamps => new(LampsBuf, LampsCount);

    internal BedState[] BedsBuf = System.Array.Empty<BedState>();
    internal int BedsCount;
    public SnapshotList<BedState> Beds => new(BedsBuf, BedsCount);

    internal BlueprintState[] LampBlueprintsBuf = System.Array.Empty<BlueprintState>();
    internal int LampBlueprintsCount;
    public SnapshotList<BlueprintState> LampBlueprints => new(LampBlueprintsBuf, LampBlueprintsCount);

    internal BedBlueprintState[] BedBlueprintsBuf = System.Array.Empty<BedBlueprintState>();
    internal int BedBlueprintsCount;
    public SnapshotList<BedBlueprintState> BedBlueprints => new(BedBlueprintsBuf, BedBlueprintsCount);

    internal RoofFlashState[] RoofFlashesBuf = System.Array.Empty<RoofFlashState>();
    internal int RoofFlashesCount;
    public SnapshotList<RoofFlashState> RoofFlashes => new(RoofFlashesBuf, RoofFlashesCount);

    // Sim-global work-tab mode flag. true = checkmark, false = priority 1..8.
    public bool CheckmarkMode { get; internal set; } = true;

    internal PawnWorkState[] PawnWorkBuf = System.Array.Empty<PawnWorkState>();
    internal int PawnWorkCount;
    public SnapshotList<PawnWorkState> PawnWork => new(PawnWorkBuf, PawnWorkCount);

    public int? SelectedDummyId { get; internal set; }
    public int[] SelectedDummyIds { get; internal set; } = System.Array.Empty<int>();
    public TilePos[]? SelectedPath { get; internal set; }
    public TilePos[]? SelectedOrders { get; internal set; }
    public int[] SelectedTreeIds { get; internal set; } = System.Array.Empty<int>();
    public int[] SelectedWoodIds { get; internal set; } = System.Array.Empty<int>();
    public int[] SelectedCropIds { get; internal set; } = System.Array.Empty<int>();
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
    float MaxCarryBulk,
    float SleepLevel,
    bool Sleeping,
    int AssignedBedEntityId);

public readonly record struct CarriedItemState(int SlotEntityId, string ItemPath, int Count, bool Forbidden);

// Per-pawn work-tab row data. Priorities[i] is 0..8 (0 = disabled);
// Allowed[i] is the parallel checkmark-mode state. Both arrays are
// length WorkTypes.Count and indexed by (int)WorkType. Snapshots are
// shallow copies so the UI can read across ticks without locks.
public readonly record struct PawnWorkState(int EntityId, string Name, byte[] Priorities, bool[] Allowed, byte[] Schedule);

// Funding is Deposited / Needed across all ResourceReq entries (0..1).
// Roof/lamp are always free → snapshot reports 1f. Renderer dims the
// fill tint when Funding < 1 so the player can see which blueprints
// still need wood deliveries. Costs is the per-resource breakdown used
// by the info panel; empty array when there's no cost ledger.
public readonly record struct BlueprintState(TilePos Tile, float Progress, bool Forbidden, float Funding, ResourceCostState[] Costs);

public readonly record struct ResourceCostState(string ItemPath, int Needed, int Deposited);

public readonly record struct TreeState(int EntityId, TilePos Tile, float ChopProgress, bool HasJob, float GrowthStage);

public readonly record struct WoodState(int EntityId, TilePos Tile, int Count, string ItemPath, bool Forbidden);

public readonly record struct CropState(
    int EntityId,
    TilePos Tile,
    CropKind Kind,
    float GrowthStage,
    float WorkProgress,
    Jobs.JobKind? ActiveJob);

public readonly record struct ItemPileState(int EntityId, TilePos Tile, int Count, string ItemPath);

public readonly record struct DeconState(TilePos Tile, float Progress, bool Forbidden);

public readonly record struct DoorRenderState(
    TilePos Tile,
    DoorOrientation Orientation,
    float OpenAmount,
    bool Forbidden,
    bool Locked,
    StruggleGame.Sim.World.DoorPriority Priority);

public readonly record struct StockpileState(
    int Id,
    string Name,
    StockpilePriority Priority,
    TilePos[] Tiles,
    string[] AllowedItemPaths);

public readonly record struct RoofBlueprintState(TilePos Tile, float Progress, bool Build, bool Forbidden);
public readonly record struct RoofFlashState(TilePos Tile, float Alpha);

public readonly record struct LampState(TilePos Tile, bool PoweredOn, LightColor Color);

public readonly record struct BedState(TilePos Origin, BedOrientation Orientation, int AssignedPawnEntityId);

public readonly record struct BedBlueprintState(TilePos Origin, BedOrientation Orientation, float Progress, bool Forbidden, float Funding, ResourceCostState[] Costs);

public readonly record struct GrowZoneState(
    int Id,
    string Name,
    World.CropKind CropKind,
    bool AllowCutting,
    bool AllowSowing,
    TilePos[] Tiles);
