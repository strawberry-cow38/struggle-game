using Friflo.Engine.ECS;
using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// Sub-tile float position. Integer tile = (int)X, (int)Y; sub-tile is the
// fractional remainder. Centers are at .5.
public struct Position : IComponent
{
    public float X;
    public float Y;
}

// Active path being walked by the entity. Null Tiles = no path.
public struct PathFollower : IComponent
{
    public List<TilePos>? Waypoints;
    public int Index;
}

// Marker for the wandering dummy entity. DummyController scans for this.
public struct Wanderer : IComponent
{
}
