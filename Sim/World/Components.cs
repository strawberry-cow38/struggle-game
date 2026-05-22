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
public struct Wanderer : IComponent
{
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

// Dropped log pile on the ground. Doesn't block walking. No interaction
// yet — placeholder for future hauling.
public struct Wood : IComponent
{
    public TilePos Tile;
    public int Count;
}

// Pending deconstruct order on a wall tile. Job entity carries this;
// ProgressSec ticks while a colonist is adjacent. On completion the wall
// reverts to Floor and half its build cost drops as Wood on the tile.
public struct Decon : IComponent
{
    public TilePos Tile;
    public float ProgressSec;
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

// One item in a pawn's carry inventory. Each slot references an
// existing item entity (Wood today, more later) kept alive across
// the haul so completion is a re-anchor rather than delete/recreate.
public struct CarriedSlot
{
    public int EntityId;
    public string ItemPath;
    public int Count;
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
