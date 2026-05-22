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
}
