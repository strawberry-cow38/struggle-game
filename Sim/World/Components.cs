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

// Dropped log pile on the ground. Doesn't block walking. No interaction
// yet — placeholder for future hauling.
public struct Wood : IComponent
{
    public TilePos Tile;
    public int Count;
}

// Generic dropped item pile (carrots today, other yields later). Doesn't
// block walking. Not yet haulable — haul + stockpile filters still key
// off the Wood component. ItemPath identifies the kind so the renderer +
// (eventually) haul system can dispatch.
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
}

// Player-applied "do not haul" mark on a world item stack. HaulSystem
// skips reserving forbidden entities; the topoff scan skips them too.
// Toggled per-stack from the item info panel or the F key.
public struct Forbidden : IComponent
{
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
}
