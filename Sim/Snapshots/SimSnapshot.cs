using StruggleGame.Sim.Map;
using StruggleGame.Sim.Stockpiles;
using StruggleGame.Sim.World;

namespace StruggleGame.Sim.Snapshots;

// Per-tick render data. SimRuntime maintains a small pool of these and
// fills the oldest slot the renderer can no longer be holding (see the
// SeqId watermark protocol in SimRuntime.BuildSnapshot), so the renderer
// can keep reading published instances while the next is being built.
// Section arrays are oversized + reused across ticks; the public
// SnapshotList<T> view exposes only the valid prefix.
public sealed class SimSnapshot
{
    // Monotonic build sequence id. The renderer reports the smallest
    // SeqId it may still touch so the sim never recycles that slot.
    public long SeqId { get; internal set; }
    public long Tick { get; internal set; }
    public long MapVersion { get; internal set; }
    public long WallVersion { get; internal set; }
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

    internal ItemPileState[] ItemPilesBuf = System.Array.Empty<ItemPileState>();
    internal int ItemPilesCount;
    public SnapshotList<ItemPileState> ItemPiles => new(ItemPilesBuf, ItemPilesCount);

    internal BloodPuddleState[] BloodPuddlesBuf = System.Array.Empty<BloodPuddleState>();
    internal int BloodPuddlesCount;
    public SnapshotList<BloodPuddleState> BloodPuddles => new(BloodPuddlesBuf, BloodPuddlesCount);

    internal FireState[] FiresBuf = System.Array.Empty<FireState>();
    internal int FiresCount;
    public SnapshotList<FireState> Fires => new(FiresBuf, FiresCount);

    internal ProjectileState[] ProjectilesBuf = System.Array.Empty<ProjectileState>();
    internal int ProjectilesCount;
    public SnapshotList<ProjectileState> Projectiles => new(ProjectilesBuf, ProjectilesCount);

    internal BloodImpactState[] BloodImpactsBuf = System.Array.Empty<BloodImpactState>();
    internal int BloodImpactsCount;
    public SnapshotList<BloodImpactState> BloodImpacts => new(BloodImpactsBuf, BloodImpactsCount);

    internal SmokePuffState[] SmokePuffsBuf = System.Array.Empty<SmokePuffState>();
    internal int SmokePuffsCount;
    public SnapshotList<SmokePuffState> SmokePuffs => new(SmokePuffsBuf, SmokePuffsCount);

    internal ExplosionState[] ExplosionsBuf = System.Array.Empty<ExplosionState>();
    internal int ExplosionsCount;
    public SnapshotList<ExplosionState> Explosions => new(ExplosionsBuf, ExplosionsCount);


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

    internal UrBoardState[] UrBoardsBuf = System.Array.Empty<UrBoardState>();
    internal int UrBoardsCount;
    public SnapshotList<UrBoardState> UrBoards => new(UrBoardsBuf, UrBoardsCount);

    internal BlueprintState[] UrBoardBlueprintsBuf = System.Array.Empty<BlueprintState>();
    internal int UrBoardBlueprintsCount;
    public SnapshotList<BlueprintState> UrBoardBlueprints => new(UrBoardBlueprintsBuf, UrBoardBlueprintsCount);

    internal SandbagState[] SandbagsBuf = System.Array.Empty<SandbagState>();
    internal int SandbagsCount;
    public SnapshotList<SandbagState> Sandbags => new(SandbagsBuf, SandbagsCount);

    internal BlueprintState[] SandbagBlueprintsBuf = System.Array.Empty<BlueprintState>();
    internal int SandbagBlueprintsCount;
    public SnapshotList<BlueprintState> SandbagBlueprints => new(SandbagBlueprintsBuf, SandbagBlueprintsCount);

    internal StoveState[] StovesBuf = System.Array.Empty<StoveState>();
    internal int StovesCount;
    public SnapshotList<StoveState> Stoves => new(StovesBuf, StovesCount);

    internal StoveBlueprintState[] StoveBlueprintsBuf = System.Array.Empty<StoveBlueprintState>();
    internal int StoveBlueprintsCount;
    public SnapshotList<StoveBlueprintState> StoveBlueprints => new(StoveBlueprintsBuf, StoveBlueprintsCount);

    internal RoofFlashState[] RoofFlashesBuf = System.Array.Empty<RoofFlashState>();
    internal int RoofFlashesCount;
    public SnapshotList<RoofFlashState> RoofFlashes => new(RoofFlashesBuf, RoofFlashesCount);

    // Active player-facing notifications (e.g. an incoming raid). Persist in
    // every snapshot until the UI dismisses them via DismissNotificationCommand.
    internal GameNotificationState[] NotificationsBuf = System.Array.Empty<GameNotificationState>();
    internal int NotificationsCount;
    public SnapshotList<GameNotificationState> Notifications => new(NotificationsBuf, NotificationsCount);

    // Sim-global work-tab mode flag. true = checkmark, false = priority 1..8.
    public bool CheckmarkMode { get; internal set; } = true;

    // Global "fire at will": false = drafted colonists only fire at forced
    // (RMB) targets, no auto-acquire/peek.
    public bool FireAtWill { get; internal set; } = true;

    internal PawnWorkState[] PawnWorkBuf = System.Array.Empty<PawnWorkState>();
    internal int PawnWorkCount;
    public SnapshotList<PawnWorkState> PawnWork => new(PawnWorkBuf, PawnWorkCount);
    // Per-slot reusable per-pawn work/schedule arrays (this slot's snapshot owns
    // them; overwritten only when this slot is rebuilt, by which point the
    // renderer has moved on). Avoids 3 fresh arrays per pawn per tick.
    internal byte[][] PawnWorkPriPool = System.Array.Empty<byte[]>();
    internal bool[][] PawnWorkAllowedPool = System.Array.Empty<bool[]>();
    internal byte[][] PawnWorkSchedPool = System.Array.Empty<byte[]>();

    // The single selected drafted ranged pawn whose hit chances are published
    // on each DummyState.AimHit (0 = none / not exactly one ranged shooter).
    public int AimShooterId { get; internal set; }

    public int? SelectedDummyId { get; internal set; }
    public int[] SelectedDummyIds { get; internal set; } = System.Array.Empty<int>();
    public TilePos[]? SelectedPath { get; internal set; }
    public TilePos[]? SelectedOrders { get; internal set; }
    // Path + queued move/action tiles for EVERY selected pawn, so a whole
    // drafted squad shows its lines and waypoints at once.
    public PawnPathState[] SelectedPaths { get; internal set; } = System.Array.Empty<PawnPathState>();
    public int[] SelectedTreeIds { get; internal set; } = System.Array.Empty<int>();
    public int[] SelectedWoodIds { get; internal set; } = System.Array.Empty<int>();
    public int[] SelectedCropIds { get; internal set; } = System.Array.Empty<int>();
    // Mirrors the other selections so a paused republish fires when the
    // selected blueprint changes (its per-resource Costs are only snapshotted
    // for the selection — see SimRuntime.CostsIfSelected).
    public TilePos[] SelectedBlueprintTiles { get; internal set; } = System.Array.Empty<TilePos>();
}

// A selected pawn's remaining path + queued order tiles, for drawing move
// lines + waypoint markers for the whole selection.
public readonly record struct PawnPathState(int EntityId, TilePos[] Path, TilePos[] Orders);

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
    int AssignedBedEntityId,
    float RecreationLevel,
    RecreationKind? AtRecreationKind,
    EquippedSlotState[] Equipped,
    HeldStackState[] Held,
    HealthState Health,
    float Facing,
    long SwingTick,
    long MissTick,
    long FlinchTick,
    int MeleeTargetId,
    // Ranged-weapon state (HasRangedWeapon false → the rest is unused).
    bool HasRangedWeapon,
    // True when the equipped ranged weapon is a rocket launcher — the action
    // bar swaps the "Fire" (pawn-target) tile for a "Rocket" ground-strike tile.
    bool HasRocketLauncher,
    int RangedMag,
    int RangedMagSize,
    string? LoadedAmmoPath,
    StruggleGame.Sim.Items.FireMode RangedMode,
    StruggleGame.Sim.Items.FireModeFlags RangedModes,
    int FireTargetId,
    long ShotTick,
    // Last tick a reload failed for lack of ammo → "Out of ammo!" overhead float.
    long OutOfAmmoTick,
    float RangedRange,
    RangedStatus RangedStatus,
    StruggleGame.Sim.Items.TargetArea RangedTargetArea,
    StruggleGame.Sim.Items.AimMode RangedAimMode,
    // Cover stance for the crouch/lean visual. 0 = none, 1 = tucked, 2 = popped.
    byte CoverStance,
    bool Leaning,
    float PeekX,
    float PeekY,
    // Has rounds in the mag OR compatible ammo in inventory to reload with.
    bool RangedHasAmmo,
    // ─── Secondary weapon (underbarrel launcher, e.g. the M203) ────────
    // HasSecondary false → the rest is unused. Drives the second target
    // button + the second mag panel on the draft action bar.
    bool HasSecondary,
    int SecMag,
    int SecMagSize,
    string? SecAmmoPath,
    // Has a grenade chambered OR a compatible one in inventory to reload with.
    bool SecHasAmmo,
    bool SecReloading,
    // Firing pie meter: 0 = none, 1 = aiming, 2 = shot/burst cooldown; Progress
    // 0..1 fills the wedge.
    byte FireMeterPhase,
    float FireMeterProgress,
    // Tend/stabilize work progress 0..1 (0 = not treating); drives the bar.
    float TreatProgress,
    // Hostile — drawn with a red tint so it reads as an enemy, not a colonist.
    bool IsEnemy,
    // Current enemy goal (EnemyGoalKind value) for the overhead debug label.
    byte EnemyGoal,
    // Single-shot hit chance FROM the currently selected drafted shooter to
    // this pawn (null unless exactly one ranged shooter is selected). Drives
    // the hover hit-chance readout.
    StruggleGame.Sim.Gunnery.HitChanceResult? AimHit,
    // Mood 0..1 (stubbed for now). Drives the colonist-bar portrait border.
    float Mood,
    // Display name (placeholder, derived from the entity id for now).
    string Name,
    // True for a dead colonist's corpse: it stays in the dummy list (greyed,
    // not body-rendered) so it keeps its colonist-bar portrait + info panel
    // until the corpse is buried or lost. Defaulted so live pawns are unchanged.
    bool IsDead = false);

// What a ranged colonist is doing right now, for the overhead label.
public enum RangedStatus : byte { None = 0, Firing = 1, Watching = 2, Reloading = 3, TooClose = 4 }

// Accent/category of a player letter. Drives the stack tile color so threats
// read red at a glance, good news green, etc.
public enum LetterKind : byte { Neutral = 0, Threat = 1, Positive = 2, Negative = 3 }

// A player-facing notification ("letter"). Id is monotonic so the UI can track
// which it has already shown. Title is the short label on the right-side stack;
// Message is the expanded hover-tooltip summary; Detail is the full body shown
// in the click-through pane. Kind tints the tile. Dismissal is handled UI-side.
public readonly record struct GameNotificationState(
    int Id, string Title, string Message, string Detail, LetterKind Kind);

public readonly record struct CarriedItemState(int SlotEntityId, string ItemPath, int Count, bool Forbidden);

// Health snapshot for the colonist panel: blood + the derived capacities
// + the live injury list (part id + condition + severity).
public readonly record struct HealthState(
    float BloodLevel,
    float BleedRate,
    float Pain,
    float Consciousness,
    float Moving,
    float Manipulation,
    float Sight,
    float OverallHealth,
    bool Unconscious,
    InjuryState[] Injuries);

public readonly record struct InjuryState(string PartId, StruggleGame.Sim.Bodies.ConditionKind Kind, float Severity, string? Caliber = null, float Bleed = 0f, bool Tended = false, bool Stabilized = false, float TendQuality = 0f);

// Persistent inventory rows for the pawn info panel. Equipped slots and
// general (held) stacks are indexed by position in their respective
// component lists; the unequip / force-drop commands take that index.
public readonly record struct EquippedSlotState(int Index, string ItemPath, int Count, EquipSlot Slot, int MagCount = 0, string? LoadedAmmoPath = null);
public readonly record struct HeldStackState(int Index, string ItemPath, int Count, int MagCount = 0, string? LoadedAmmoPath = null);

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


public readonly record struct CropState(
    int EntityId,
    TilePos Tile,
    CropKind Kind,
    float GrowthStage,
    float WorkProgress,
    Jobs.JobKind? ActiveJob);

// Label overrides the catalog display name when set (corpses use the dead
// colonist's name instead of "Corpse").
public readonly record struct ItemPileState(int EntityId, TilePos Tile, int Count, string ItemPath, bool Forbidden, string? Label, int MagCount = 0, string? LoadedAmmoPath = null, int SecMagCount = 0, string? SecLoadedAmmoPath = null);

public readonly record struct BloodPuddleState(TilePos Tile, float Amount);

public readonly record struct FireState(TilePos Tile, float Intensity);

// A bullet in flight, in tile coordinates. Angle is the travel heading for
// drawing the streak; Speed (tiles/sec) sets the tracer length so it spans
// one tick of travel (no gaps); IsAp tints AP rounds differently from HP.
public readonly record struct ProjectileState(float X, float Y, float Height, float Angle, float Speed, bool IsAp, float OriginX, float OriginY, bool IsRocket = false, RocketWarhead Warhead = RocketWarhead.None);

// Which warhead a flying rocket carries — colours its nose cone in flight to
// match the launcher's loaded round. He40mm is the M203's 40mm grenade.
public enum RocketWarhead : byte { None = 0, Frag = 1, Hedp = 2, Incend = 3, He40mm = 4 }

// A drifting smoke puff dropped by a flying rocket. Alpha 1 = just spawned →
// 0 = gone. Seed varies the per-puff drift/size so the trail isn't uniform.
public readonly record struct SmokePuffState(float X, float Y, float Height, float Alpha, float Seed);

// An explosion flash. Radius = blast radius in tiles; Alpha 1 = detonation
// instant → 0 = faded. Incend tints the fireball oranger.
public readonly record struct ExplosionState(float X, float Y, float Radius, float Alpha, bool Incend);

// A transient blood spray at a bullet-hit point. Angle = the bullet's travel
// heading so droplets fan out the exit side. Scale shrinks entry pops vs the
// bigger exit burst. Alpha 1→0 over its life.
public readonly record struct BloodImpactState(float X, float Y, float Height, float Angle, float Scale, bool Dirt, float Alpha);

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

public readonly record struct UrBoardState(int EntityId, TilePos Tile, int PlayerCount, int SpectatorCount);

public readonly record struct SandbagState(TilePos Tile);

public readonly record struct StoveState(
    int EntityId,
    TilePos Origin,
    StoveOrientation Orientation,
    int CurrentBillIndex,
    float CookProgress,
    int ActiveCookEntityId,
    BillState[] Bills);

public readonly record struct StoveBlueprintState(
    TilePos Origin,
    StoveOrientation Orientation,
    float Progress,
    bool Forbidden,
    float Funding,
    ResourceCostState[] Costs);

public readonly record struct BillState(
    RecipeId Recipe,
    BillRepeatMode RepeatMode,
    int TargetCount,
    int RemainingCount,
    BillOutputDest OutputDest,
    int StockpileEntityId);

public readonly record struct GrowZoneState(
    int Id,
    string Name,
    World.CropKind CropKind,
    bool AllowCutting,
    bool AllowSowing,
    TilePos[] Tiles);
