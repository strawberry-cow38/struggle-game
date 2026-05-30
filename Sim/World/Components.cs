using Friflo.Engine.ECS;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Sub-tile float position. Integer tile = (int)X, (int)Y; sub-tile is the
// fractional remainder. Centers are at .5.
public struct WorldPos : IComponent
{
    public float X;
    public float Y;
}

// Active path being walked by the entity. Null Tiles = no path.
// PendingPathId tracks an in-flight async PathService request; 0 = none.
// While non-zero, the entity is waiting on a route and should not submit
// further requests until TryConsume returns.
public struct PathFollower : IComponent
{
    public List<TilePos>? Waypoints;
    public int Index;
    public long PendingPathId;
}

// Marker for the wandering dummy entity. DummyController scans for this.
// IdleSec counts down while the pawn is parked between wander strolls
// — RequestWanderPath is gated on it hitting zero so colonists actually
// pause at their target instead of marching tile-to-tile non-stop.
public struct Wanderer : IComponent
{
    public float IdleSec;
    // Heading in radians (atan2 of last movement). Updated while walking;
    // holds its last value when idle so the facing arrow doesn't snap back.
    public float Facing;
    // Drafted state on the previous Plan tick, used to detect the
    // drafted->undrafted edge so we can snap a mid-walk pawn onto the grid.
    public bool WasDrafted;
    // While true, the pawn is easing onto its nearest tile center before
    // resuming normal jobs/wander (set when undrafted mid-walk).
    public bool Snapping;
}

// Pending construction job on a tile. ProgressSec advances while a
// builder is adjacent; when it reaches BuildSystem.BuildTimeSec the tile
// becomes Wall and the entity is deleted.
public struct Blueprint : IComponent
{
    public TilePos Tile;
    public float ProgressSec;
}

// Optional assignment from a builder to a specific job (any kind), so
// they don't recompute the nearest target every tick. The board is the
// source of truth for tile + state.
public struct BuildTarget : IComponent
{
    public JobId JobId;
}

// Marker — colonist is drafted. Drafted pawns skip job claim and
// wander; they hold position and follow player-issued orders.
public struct Drafted : IComponent
{
}

// Per-colonist priority table for each WorkType. Priorities[i] = 0
// means "disabled" — pawn refuses jobs of that work type. 1 is highest,
// 8 is lowest. Allowed[i] is the parallel checkmark-mode state: true
// means "the colonist may take jobs of this work type when the work tab
// is in checkmark mode" (priority then defaults to DefaultPriority).
// The two arrays are kept in sync so flipping between modes never wipes
// the player's tuning.
public struct WorkPriorities : IComponent
{
    public byte[]? Priorities;
    public bool[]? Allowed;

    public const byte DefaultPriority = 3;
}

// Per-hour schedule slots. Length 24, indexed by local hour 0..23.
// A "general guide" — DummyController consults it before claiming a
// new job; mid-job behavior is unaffected. Without tired/recreation
// stats yet, Sleep and Recreation both translate to "don't pick up
// fresh work, just idle/wander" — the distinction is preserved in
// the data so future stat-driven behavior can split them.
public enum ScheduleCategory : byte
{
    Any = 0,
    Work = 1,
    Sleep = 2,
    Recreation = 3,
}

public struct Schedule : IComponent
{
    public byte[]? Slots;
    public const int Hours = 24;
}

// FIFO queue of move orders for a drafted colonist. The next order is
// popped when the current path runs out; appended on shift+RMB.
public struct OrderQueue : IComponent
{
    public List<TilePos>? Tiles;
}

// A tree planted on a tile. Blocks walkability. Felled by a ChopTree
// job; on completion the tree entity is deleted and a Wood entity drops
// on the same tile.
public struct Tree : IComponent
{
    public TilePos Tile;
    public float ChopProgressSec;
}

// Growth state for plants (trees, crops, anything that progresses over
// time toward maturity). Stage = 0..1. GrowthSystem advances Stage when
// the host tile's light is >= GrowLightThreshold (today: unroofed) and
// the room temperature is comfortable. Spawn-time stage seeds variety so
// a fresh map shows mature + sapling trees mixed.
public struct Growth : IComponent
{
    public float Stage;
}

public enum CropKind : byte
{
    Carrot = 0,
}

// A planted crop on a tile. Doesn't block walking. Lifecycle: planted
// (Growth.Stage = 0) → grows → harvestable at ≥75% → harvested (yields
// carrots scaling linearly 1→4 between 75%–100%). Cut by the CutPlants
// designator at any stage (no yield). WorkProgressSec drives both
// cut-plant and harvest jobs.
public struct Crop : IComponent
{
    public TilePos Tile;
    public CropKind Kind;
    public float WorkProgressSec;
}

// A pending Sow job's progress + payload. Job entity carries this;
// ProgressSec advances while a sower is adjacent. On completion the
// tile gets a fresh Crop with Growth.Stage = 0 of the configured kind
// and the entity is deleted.
public struct SowSite : IComponent
{
    public TilePos Tile;
    public CropKind Kind;
    public float ProgressSec;
}

// The one and only dropped-item-on-ground component. Wood, carrots,
// meals — everything is an ItemPile identified by ItemPath. Doesn't block
// walking. (Wood used to be its own component; it was the first item type
// before this generic one existed. Now wood is just ItemPath ==
// ItemCatalog.Wood.FullPath, so selection / merging / spilling / hauling
// all treat it like any other stack.)
public struct ItemPile : IComponent
{
    public TilePos Tile;
    public int Count;
    public string ItemPath;
}

// Pending deconstruct order on a wall tile. Job entity carries this;
// ProgressSec ticks while a colonist is adjacent. On completion the wall
// reverts to Floor and half its build cost drops as Wood on the tile.
public struct Decon : IComponent
{
    public TilePos Tile;
    public float ProgressSec;
}

// Pending roof job covering one or more tiles (drag-rect + auto-roof
// chunk eligible tiles into 3x3 grid cells; each chunk is one job).
// Build=true raises roofs on every Tiles entry; Build=false tears them
// down. ProgressSec scales linearly with Tiles.Length, so chunk time
// = RoofBuildTimeSec * Tiles.Length. On completion SimRuntime flips
// every tile's roof bit, bumps RoofVersion once, and deletes the entity.
public struct RoofBlueprint : IComponent
{
    public TilePos[] Tiles;
    public float ProgressSec;
    public bool Build;
}

// Pending wood-flooring blueprint on a tile. Floor sits atop terrain;
// walls can be raised on top of it. ProgressSec advances while a
// builder is adjacent; on completion the flooring layer becomes Wood
// and the entity is deleted.
public struct FloorBlueprint : IComponent
{
    public TilePos Tile;
    public float ProgressSec;
}

// Orientation of a door, determined at placement from the flanking
// walls. Horizontal = walls to its east + west (door swings on a
// north-south axis); Vertical = walls to its north + south.
public enum DoorOrientation : byte
{
    Horizontal = 0,
    Vertical = 1,
}

public enum DoorState : byte
{
    Closed = 0,
    Opening = 1,
    Open = 2,
    Closing = 3,
}

// Player-set traversal priority for a door. Lower priority = higher
// path cost = pawns avoid; higher priority = lower cost = pawns
// prefer. ExitOnly is a near-block (huge cost) so pawns only use the
// door when no other route exists — useful for sealing a base behind
// a "fire exit" without making the door actually forbidden.
public enum DoorPriority : byte
{
    ExitOnly = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public static class DoorPathing
{
    // Edge-cost multiplier the A* layer reads via MapView.CostAt. Higher
    // = pawns route around the door; lower = pawns funnel through.
    //   ExitOnly: 50.0 — only used when no other walkable route exists
    //   Low:       2.5
    //   Medium:    1.3 — matches the pre-priority default
    //   High:      0.6 — cheaper than wood floor, so a designated main
    //                    entrance pulls traffic toward itself
    public static float CostFor(DoorPriority p) => p switch
    {
        DoorPriority.ExitOnly => 50.00f,
        DoorPriority.Low      => 2.50f,
        DoorPriority.Medium   => 1.30f,
        DoorPriority.High     => 0.60f,
        _                     => 1.30f,
    };
}

// A built door at a tile. Doesn't block pathing (the mover gates on
// State instead). When a pawn wants to cross, the mover flips
// WantsOpen so DoorSystem will start the opening animation; the pawn
// holds at the prev tile until State == Open. Auto-closes after a
// brief idle.
public struct Door : IComponent
{
    public TilePos Tile;
    public DoorOrientation Orientation;
    public DoorState State;
    public float ProgressSec;
    public bool WantsOpen;
    public float IdleSec;
    // Player-toggled. Forbidden doors refuse to open — they path as a
    // wall (MapView.Walkable returns false). Locked doors still open
    // for friendlies; the bool is a stub for an enemies pass that
    // hasn't shipped yet. Defaults: Locked=true, Forbidden=false.
    public bool Forbidden;
    public bool Locked;
    // Player-set traversal weight; cycles via the door info panel.
    // Defaults to Medium (cost matches the legacy 1.3 door multiplier).
    public DoorPriority Priority;
}

// Pending door blueprint on a tile. Built by an adjacent colonist like
// a wall; on completion the entity gains a Door component and the
// blueprint marker is replaced.
public struct DoorBlueprint : IComponent
{
    public TilePos Tile;
    public DoorOrientation Orientation;
    public float ProgressSec;
}

// 0-255 RGB triple shared by lamp emission + snapshot. Pure data; the
// renderer reinterprets bytes as a Godot Color. Default White = (255,255,255).
public readonly record struct LightColor(byte R, byte G, byte B)
{
    public static LightColor White => new(255, 255, 255);
}

// A built lamp. Doesn't block walkability (pawns walk over it; placement
// gates against trees/walls/doors/existing lamps but allows building on
// open floor). PoweredOn cheat-toggles emission until a power network
// ships — when on, the lamp writes a circular falloff into the per-tile
// light layer (50% inner, 25-49% mid ring, 0-24% outer ring). Color tints
// the emission per channel; recompute max-blends per channel so red+green
// lamps overlap to yellow.
public struct Lamp : IComponent
{
    public TilePos Tile;
    public bool PoweredOn;
    public LightColor Color;
}

// Pending lamp construction job. ProgressSec advances while a builder
// stands on or adjacent to the tile. On completion the entity gains a
// Lamp component (PoweredOn=true, Color copied over) and the blueprint
// marker is replaced.
public struct LampBlueprint : IComponent
{
    public TilePos Tile;
    public float ProgressSec;
    public LightColor Color;
}

// Bed orientation = direction the foot of the bed points from the head
// (anchor) tile. North = foot is one tile north of head, etc. The 2-tile
// footprint is Origin + (Origin shifted by orientation vector).
public enum BedOrientation : byte
{
    North = 0,
    East  = 1,
    South = 2,
    West  = 3,
}

public static class BedOrientations
{
    // (dx, dy) for the foot-tile offset from the head/origin tile.
    public static (int Dx, int Dy) Offset(BedOrientation o) => o switch
    {
        BedOrientation.North => (0, -1),
        BedOrientation.East  => (1, 0),
        BedOrientation.South => (0, 1),
        BedOrientation.West  => (-1, 0),
        _                    => (1, 0),
    };

    public static TilePos Foot(TilePos origin, BedOrientation o)
    {
        var (dx, dy) = Offset(o);
        return new TilePos(origin.X + dx, origin.Y + dy);
    }
}

// A placed bed. Origin = "head" tile; Foot tile is derived from
// Orientation. Pure decoration for now — no sleep behavior, but the
// 2-tile footprint blocks pathing the same way trees do.
public struct Bed : IComponent
{
    public TilePos Origin;
    public BedOrientation Orientation;
}

// Tracks which colonist owns this bed (0 = unassigned). Set by the
// bed info panel's Assign dropdown, or auto-claimed by an unassigned
// pawn that sleeps in it. AssignedBed on the pawn is the inverse view
// kept in sync by SimRuntime.AssignBedToPawn.
public struct BedAssignee : IComponent
{
    public int PawnEntityId;
}

// Sleep need: 0 = passed out tired, 1 = fully rested. Decays linearly
// over 16 sim-hours while awake; refills linearly over 8 sim-hours
// while the Sleeping component is present. Threshold + behavior live
// in DummyController + SleepSystem.
public struct SleepNeed : IComponent
{
    public float Level;
}

// Per-pawn "this bed is mine" pointer. BedEntityId references the bed
// entity (NOT the blueprint). Cleared when the bed is destroyed or
// reassigned to someone else.
public struct AssignedBed : IComponent
{
    public int BedEntityId;
}

// On a pawn currently sleeping. BedEntityId = the bed they're in (or 0
// for floor sleep). SleepSystem advances SleepNeed; DummyController
// wakes them when need is full or their schedule slot leaves Sleep.
public struct Sleeping : IComponent
{
    public int BedEntityId;
}

// Pending bed construction. ProgressSec advances while a builder is
// adjacent to either tile of the 2-tile footprint. On completion the
// BedBlueprint component is replaced with Bed at the same orientation.
// Footprint tiles are blocked (via _bedOccupied) from the moment the
// blueprint is queued so a second designation can't overlap.
public struct BedBlueprint : IComponent
{
    public TilePos Origin;
    public BedOrientation Orientation;
    public float ProgressSec;
}

// Recreation kinds the colonist can roll as a preferred activity. The
// roll pool is filtered to "currently possible on this map" — Ur is
// only in the pool if at least one Ur board exists. Spectating is in
// the pool only when at least one active game is happening (i.e. a
// board with a player). Future entries: Walk, Cloudwatch, etc.
public enum RecreationKind : byte
{
    Ur = 0,
    Spectating = 1,
}

// 0 = burnt out, 1 = freshly rested. Decays linearly over 16 sim-hours
// of "no recreation"; refills while AtRecreation is present at a per-
// kind power rate. Threshold + roll cadence live in RecreationSystem
// + DummyController.
public struct RecreationNeed : IComponent
{
    public float Level;
}

// Per-colonist preferred recreation type. Re-rolled every
// RecreationSystem.PreferenceRollSec from the live pool of types
// currently available on the map. Kind = 255 means "not yet rolled"
// — RecreationSystem fills it in on the first eligible tick.
public struct RecreationPreference : IComponent
{
    public RecreationKind Kind;
    public float SecondsUntilRoll;
}

// A built game-of-Ur board. 1x1, blocks pathing. Two player seats (the
// 4-cardinal-adjacent walkable tiles, picked at reservation time); up
// to 8 spectator slots clumping around within RecreationSystem.SpectatorRadius.
public struct UrBoard : IComponent
{
    public TilePos Tile;
}

// Pending Ur-board blueprint. ProgressSec advances while a builder is
// adjacent. Cost = 25 wood (UrBoardBuildWoodCost in SimRuntime).
public struct UrBoardBlueprint : IComponent
{
    public TilePos Tile;
    public float ProgressSec;
}

// Active recreation session on the pawn. BoardEntityId = the board
// they're attached to (Ur today; future boards reuse the same field).
// Role = Player (occupies a player seat) or Spectator. RecreationSystem
// reads this each tick to refill RecreationNeed; DummyController spawns
// + tears it down. SeatTile = the tile the pawn is standing on for the
// session, so reservation release can free the right slot.
public enum RecreationRole : byte
{
    Player = 0,
    Spectator = 1,
}

public struct AtRecreation : IComponent
{
    public int BoardEntityId;
    public RecreationKind Kind;
    public RecreationRole Role;
    public TilePos SeatTile;
}

// Sticky reservation held by a pawn while walking to a seat. Without
// this, DummyController.Plan would re-call TryReserveUrSeat every tick
// and silently leak seats / player counters. The reservation transfers
// to AtRecreation on arrival; on interruption (tired, drafted, seat
// unreachable) DummyController releases the held seat back to the
// board's seat pool.
public struct RecreationReservation : IComponent
{
    public int BoardEntityId;
    public RecreationKind Kind;
    public RecreationRole Role;
    public TilePos SeatTile;
}

// One item in a pawn's carry inventory. Each slot references an
// existing item entity (Wood today, more later) kept alive across
// the haul so completion is a re-anchor rather than delete/recreate.
// Forbidden = player marked this slot "keep on the pawn"; the haul
// delivery path leaves it in inventory instead of dropping it, the
// draft-toggle drop leaves it too, and the pawn won't take new haul
// jobs while carrying anything (forbidden or not).
public struct CarriedSlot
{
    public int EntityId;
    public string ItemPath;
    public int Count;
    public bool Forbidden;
}

// A pawn carrying one or more items to a single stockpile destination.
// Slots holds items already physically picked up (their world Wood +
// WorldPos components have been removed). PendingPickupIds holds
// additional item entities the pawn has reserved via HaulReserved +
// HaulPayload during the topoff scan; the pawn walks to each before
// heading to DestTile. PrimaryJobId references the originating Haul
// job (the one that drove the claim) so completion / cancellation
// can find it; topoff pickups are reservation-only with no Job.
public struct Carrying : IComponent
{
    public List<CarriedSlot>? Slots;
    public List<int>? PendingPickupIds;
    public TilePos DestTile;
    public int StockpileId;
    public Jobs.JobId PrimaryJobId;
    // Non-zero when the dropoff is a blueprint deposit. DeliverCarrying
    // routes per-slot Counts into BlueprintCostOps.Deposit instead of
    // spawning Wood at DestTile. Leftover (if any) still spills as Wood.
    public int BlueprintEntityId;
}

// Player-applied "do not haul" mark on a world item stack. HaulSystem
// skips reserving forbidden entities; the topoff scan skips them too.
// Toggled per-stack from the item info panel or the F key.
public struct Forbidden : IComponent
{
}

// Equipment slot kind. Today everything goes into Generic (one blanket
// slot); the enum exists so head/body/weapon/etc. can be added later
// without reshaping the Inventory component or its consumers.
public enum EquipSlot : byte
{
    Generic = 0,
}

// One equipped item. Count is almost always 1 today (equip pulls a
// single unit off a pile) but stays an int so stackable equipment is
// possible later.
public struct EquippedItemSlot
{
    public EquipSlot Slot;
    public string ItemPath;
    public int Count;
}

// One general-inventory stack. Unequipping an item drops it here; the
// haul system never touches these, so a colonist won't auto-cart its
// own pocketed items off to a stockpile.
public struct InventoryStack
{
    public string ItemPath;
    public int Count;
}

// Persistent per-pawn inventory: general carried stacks plus equipped
// items. Distinct from Carrying (which is transient haul cargo bound to
// a haul job). Both Items and Equipped count against the SAME carry
// weight/bulk budget as Carrying — capacity is shared — but neither is
// auto-dropped or auto-hauled; they only move on explicit player order
// (equip / unequip / force-drop).
public struct Inventory : IComponent
{
    public List<InventoryStack>? Items;
    public List<EquippedItemSlot>? Equipped;
}

// Player order: walk to ItemTile, pick up one unit of ItemPath from the
// pile sitting there, and move it into an equipped slot. ItemEntityId is
// the targeted pile entity (used to detect the pile vanishing mid-walk).
// Handled in DummyController.Plan ahead of the job auction so the chosen
// colonist actually performs it; removed on completion or if the pile
// is gone / unreachable.
public struct EquipOrder : IComponent
{
    public TilePos ItemTile;
    public string ItemPath;
    public int ItemEntityId;
}

// Player order: walk to ItemTile and pick up to RequestedCount units of
// ItemPath into GENERAL inventory (not equipped). The actual amount is
// clamped to the pawn's remaining carry capacity on arrival.
// RequestedCount == int.MaxValue means "as much as fits" (pick up all).
// Handled in DummyController.Plan ahead of the job auction, same as
// EquipOrder.
public struct PickupOrder : IComponent
{
    public TilePos ItemTile;
    public string ItemPath;
    public int ItemEntityId;
    public int RequestedCount;
}

// Transient combat state for juice + gameplay. All values are sim-tick
// stamps/deadlines (0 = none). The renderer reads SwingTick/MissTick/
// FlinchTick for animations; StunUntil/EngagedUntil drive behavior.
public struct Combat : IComponent
{
    public long SwingTick;     // last tick this pawn threw a punch (lunge anim)
    public long MissTick;      // last tick this pawn missed (Missed! text)
    public long FlinchTick;    // last tick this pawn was hit (flinch anim)
    public long StunUntil;     // stunned (no move/attack) until this tick
    public long EngagedUntil;  // slowed by melee until this tick
}

// Drafted-pawn order: close on TargetEntityId and punch it on a cadence
// until it's downed. Cleared by a new move order, a new attack, the
// target going down, or un-drafting. Handled in DummyController.
public struct MeleeTarget : IComponent
{
    public int TargetEntityId;
    public long LastHitTick;
    // True if the order was issued on an already-downed target — a manual
    // "finish him". Such an attack keeps going until the target dies
    // instead of stopping when it's merely unconscious.
    public bool FinishOff;
}

// Ranged-weapon state for a pawn carrying an equipped ranged weapon.
// Attached/removed by DummyController to mirror the equipped weapon. Holds
// the magazine (loaded count + which ammo is chambered), the selected fire
// mode, the forced target, and the cooldown/burst bookkeeping.
public struct RangedCombat : IComponent
{
    public int TargetEntityId;     // forced fire target, 0 = none
    public int MagCount;           // rounds currently in the magazine
    public string? LoadedAmmoPath; // which ammo is chambered (decides the wound)
    public string? PreferredAmmoPath; // player-chosen reload ammo; null = any matching
    public StruggleGame.Sim.Items.FireMode Mode;
    public StruggleGame.Sim.Items.TargetArea TargetArea; // body region the shot aims for
    public long NextActionTick;    // earliest tick the next shot / reload-finish may happen
    public int BurstRemaining;     // shots left to fire in the current burst
    public bool Reloading;         // mag refill in progress until NextActionTick
    public long ShotTick;          // last tick a shot left the muzzle (flash anim)
    public float Recoil;           // accumulated recoil cone (degrees); decays over time
    // True when the order was issued on an already-downed target (manual
    // retarget) — keeps firing until death. Otherwise fire stops when a
    // conscious target goes down, mirroring melee.
    public bool FinishOff;
}

// A bullet in flight. Created from a DummyController fire request, advanced
// each tick toward (ToX,ToY). On arrival it resolves: a hit applies the
// ammo's wound to TargetEntityId; a miss simply vanishes.
public struct Projectile : IComponent
{
    public float X, Y;             // live ground position (tiles)
    public float Height;           // live height above the ground (tiles)
    public float VertVel;          // vertical velocity (tiles/sec); falls under gravity
    public float ToX, ToY;         // flight destination (aim point; scattered on a miss)
    public float Speed;            // tiles per second (horizontal)
    public int ShooterEntityId;
    public int TargetEntityId;     // intended victim (for hit resolution)
    public bool WillHit;           // accuracy already rolled at fire time
    public string AmmoPath;        // wound source on a hit
    public float Angle;            // travel heading, for the streak render
    // Set the tick the round reaches its destination — it's drawn AT the
    // target that tick (so the tracer visibly connects) and removed the next.
    public bool Arrived;
}

// Queued bullet spawn emitted by DummyController during its query pass and
// drained by SimRuntime after the pass (entity creation is a structural
// change, so it can't happen mid-iteration).
public readonly record struct ProjectileSpawn(
    float FromX, float FromY, float ToX, float ToY, float ToHeight,
    float Speed, int ShooterEntityId, int TargetEntityId, bool WillHit, string AmmoPath);

// A dead colonist's body, dropped on the ground where they fell. Stashes
// the colonist's data (health/injuries) so future resurrection magic can
// rebuild the pawn. Rendered with a big red X.
public struct Corpse : IComponent
{
    public TilePos Tile;
    public Health Health;
    public string Name; // the dead colonist's name, for the item label
}

// One condition on one body part (a cut on the left hand, etc).
public struct PartInjury
{
    public string PartId;
    public StruggleGame.Sim.Bodies.ConditionKind Kind;
    public float Severity; // 0..1
    // Gunshot-only detail (null/false for other kinds): the round's caliber
    // and whether the bullet lodged in the body vs passed clean through.
    public string? Caliber;
    public bool Lodged;
}

// Per-colonist health. Injuries is the flat list of conditions across the
// body tree (most parts have none). The capacity fields + Unconscious are
// recomputed by HealthSystem each rare tick from the injuries + blood, and
// read by the effect wiring (move/work speed, the unconscious gate).
public struct Health : IComponent
{
    public float BloodLevel; // 0..1
    public List<PartInjury>? Injuries;
    public float Consciousness;
    public float Moving;
    public float Manipulation;
    public float Sight;
    public float BloodPumping;
    public float Breathing;
    public float Pain; // 0..1 summed across injuries; high pain -> shock
    public bool Unconscious;
    public bool WasDowned; // last tick's Unconscious, for the down -> drop-items transition
    // Blood spilled but not yet dropped as a puddle. HealthSystem drips a
    // puddle each time this crosses a threshold.
    public float BleedAccum;
}

// A pool of spilled blood on a tile. Amount 0..1 = how dark/large. Purely
// cosmetic for now; persists (no cleaning yet).
public struct BloodPuddle : IComponent
{
    public TilePos Tile;
    public float Amount;
}

// Marks an item entity as already promised to a haul job. Posted by
// HaulSystem when a Job is created; the same component is removed when
// the job completes/cancels. Prevents the poster from re-posting a haul
// for an item that's mid-flight.
public struct HaulReserved : IComponent
{
    public StruggleGame.Sim.Jobs.JobId JobId;
}

// Lives on the to-be-hauled item entity from job-post until the carrier
// picks it up. Captures the chosen destination so the carrier knows
// where to drop after pickup. Removed at pickup time.
public struct HaulPayload : IComponent
{
    public TilePos DestTile;
    public int StockpileId;
    public string ItemPath;
    public int Count;
    // Non-zero = haul targets a blueprint at DestTile. Set by
    // BlueprintHaulSystem; copied onto Carrying at pickup so the
    // deliver path can find the blueprint by entity id even if its
    // tile has moved or its entity slot was reused.
    public int BlueprintEntityId;
}

// Material requirement list attached to a blueprint entity. When present,
// build-style systems must check IsBlueprintFunded before advancing
// progress — unfunded blueprints idle until DepositToBlueprint fills
// every entry. Absent component = legacy "free" blueprint (current
// behavior for all built-in designators until they opt in).
public struct BlueprintCost : IComponent
{
    public ResourceReq[] Entries;
}

public struct ResourceReq
{
    public string ItemPath;
    public int Needed;
    public int Deposited;
    // In-flight haul reservation. BlueprintHaulSystem bumps this when it
    // dispatches a hauler; DeliverCarrying converts Reserved → Deposited
    // on success, and the abort paths decrement Reserved without raising
    // Deposited. (Needed - Deposited - Reserved) drives "still wanted".
    public int Reserved;
}

// ─── Stove / workbench / bills ────────────────────────────────────────
// 3x1 body + 1 standing tile making a T shape. Orientation = direction
// the standing tile extends from the center body tile.
public enum StoveOrientation : byte
{
    North = 0,
    East  = 1,
    South = 2,
    West  = 3,
}

public static class StoveOrientations
{
    // Body offsets relative to Origin (center body tile).
    public static (int Dx, int Dy)[] BodyOffsets(StoveOrientation o) => o switch
    {
        StoveOrientation.North => new (int, int)[] { (-1, 0), (0, 0), (1, 0) },
        StoveOrientation.South => new (int, int)[] { (-1, 0), (0, 0), (1, 0) },
        StoveOrientation.East  => new (int, int)[] { (0, -1), (0, 0), (0, 1) },
        StoveOrientation.West  => new (int, int)[] { (0, -1), (0, 0), (0, 1) },
        _                      => new (int, int)[] { (0, 0) },
    };

    // Offset from Origin to the cook's standing tile.
    public static (int Dx, int Dy) StandingOffset(StoveOrientation o) => o switch
    {
        StoveOrientation.North => (0, -1),
        StoveOrientation.East  => (1, 0),
        StoveOrientation.South => (0, 1),
        StoveOrientation.West  => (-1, 0),
        _                      => (0, 1),
    };

    public static IEnumerable<TilePos> BodyTiles(TilePos origin, StoveOrientation o)
    {
        foreach (var (dx, dy) in BodyOffsets(o))
            yield return new TilePos(origin.X + dx, origin.Y + dy);
    }

    public static TilePos StandingTile(TilePos origin, StoveOrientation o)
    {
        var (dx, dy) = StandingOffset(o);
        return new TilePos(origin.X + dx, origin.Y + dy);
    }
}

// Built stove. 3-tile body (high-cost walkable) + standing tile (blocked
// to other pathing except the cook). Active cook progress and current
// bill live here so interrupt-recovery can resume without touching the
// pawn (today: master spec is reset-on-interrupt, but the data shape
// supports both).
public struct Stove : IComponent
{
    public TilePos Origin;
    public StoveOrientation Orientation;
    public float CookProgressTicks;
    public int CurrentBillIndex; // -1 = idle
    public int ActiveCookEntityId; // 0 = no pawn currently bound
}

// Pending stove construction. Builder must be adjacent to any body tile.
public struct StoveBlueprint : IComponent
{
    public TilePos Origin;
    public StoveOrientation Orientation;
    public float ProgressSec;
}

// Bill repeat modes (rimworld-style).
//   Forever     — never stop.
//   DoUntilCount — keep cooking until world has TargetCount of the output.
//   DoXTimes    — decrement RemainingCount per completion; stop at 0.
public enum BillRepeatMode : byte
{
    Forever = 0,
    DoUntilCount = 1,
    DoXTimes = 2,
}

// Where finished output lands.
//   DropAtWorkbench   — drop on the standing tile (or nearest free body tile).
//   SpecificStockpile — haul to StockpileEntityId.
//   AnyStockpile      — pick the best haul destination at completion.
public enum BillOutputDest : byte
{
    DropAtWorkbench = 0,
    SpecificStockpile = 1,
    AnyStockpile = 2,
}

// Static recipe registry. Add entries when adding new recipes.
public enum RecipeId : byte
{
    CookSimpleMeal = 0,
    CookSimpleMealBatch = 1,
}

public sealed class Recipe
{
    public RecipeId Id { get; }
    public string DisplayName { get; }
    public (string ItemPath, int Count)[] Inputs { get; }
    public (string ItemPath, int Count) Output { get; }
    public int WorkTicks { get; }
    public Recipe(RecipeId id, string name, (string, int)[] inputs, (string, int) output, int workTicks)
    {
        Id = id; DisplayName = name; Inputs = inputs; Output = output; WorkTicks = workTicks;
    }
}

public static class Recipes
{
    public static readonly Recipe SimpleMeal = new(
        RecipeId.CookSimpleMeal, "Simple meal",
        new[] { (Items.ItemCatalog.Carrot.FullPath, 5) },
        (Items.ItemCatalog.SimpleMeal.FullPath, 1),
        workTicks: 200);

    public static readonly Recipe SimpleMealBatch = new(
        RecipeId.CookSimpleMealBatch, "Simple meal x4",
        new[] { (Items.ItemCatalog.Carrot.FullPath, 20) },
        (Items.ItemCatalog.SimpleMeal.FullPath, 4),
        workTicks: 800);

    public static Recipe Get(RecipeId id) => id switch
    {
        RecipeId.CookSimpleMeal      => SimpleMeal,
        RecipeId.CookSimpleMealBatch => SimpleMealBatch,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static IReadOnlyList<Recipe> All => new[] { SimpleMeal, SimpleMealBatch };
}

// One row in a workbench's bills list.
public struct Bill
{
    public RecipeId Recipe;
    public BillRepeatMode RepeatMode;
    public int TargetCount;    // DoUntilCount: stop when world has ≥ this many output items.
    public int RemainingCount; // DoXTimes: decrements per completion; 0 = done.
    public BillOutputDest OutputDest;
    public int StockpileEntityId; // SpecificStockpile target; 0 otherwise.
}

// Per-stove bills configuration. Bills run top-to-bottom; the first
// eligible bill wins. Removed when the stove is deconstructed.
public struct BillsBoard : IComponent
{
    public List<Bill>? Bills;
}

// Active cook session on a pawn. StoveEntityId = the bench they bound to.
// BillIndex = which bill they're working. Phase: 0 = hauling ingredients,
// 1 = working ticks (standing at the stove standing tile). Reset to 0 on
// drafted interrupt (only valid interrupt per master spec).
public struct Cooking : IComponent
{
    public int StoveEntityId;
    public int BillIndex;
    public byte Phase; // 0=haul, 1=work
}

// Carrots the cook has picked up but not yet deposited at the stove.
// Kept off the generic Carrying flow because Carrying assumes a haul
// dest tile + stockpile routing — cook ingredients route to the bound
// stove instead. Cleared on deposit + on drafted interrupt (carrots drop
// at the pawn's current tile as a fresh ItemPile so they aren't lost).
public struct CookHaul : IComponent
{
    public int CarrotsCarried;
}
