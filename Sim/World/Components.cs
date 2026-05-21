using Friflo.Engine.ECS;
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

// Optional assignment from a builder to a specific blueprint, so they
// don't recompute the nearest target every tick.
public struct BuildTarget : IComponent
{
    public TilePos Tile;
}
