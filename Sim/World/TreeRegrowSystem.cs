using StruggleGame.Sim.Map;

namespace StruggleGame.Sim.World;

// World engine restock: tries once per CheckIntervalSec to spawn a fresh
// sapling on a random walkable, outdoor, structure-free tile until the
// live tree count matches SimRuntime.TargetTreeCount. New trees come in
// at Growth.Stage = 0 so the player sees them grow rather than popping
// in mature.
//
// "Structure-free" means a 5-tile Chebyshev buffer around the candidate
// is clear of walls, doors, floors, stockpiles and any in-progress
// blueprints. That keeps regrowth from blocking the player's base or
// crowding the front door.
public sealed class TreeRegrowSystem
{
    public const float CheckIntervalSec = 10f;
    public const int StructureBufferTiles = 5;

    private readonly SimRuntime _sim;
    private float _accumSec;

    public TreeRegrowSystem(SimRuntime sim)
    {
        _sim = sim;
    }

    public void Step(float dt)
    {
        _accumSec += dt;
        if (_accumSec < CheckIntervalSec) return;
        _accumSec = 0f;
        if (_sim.TreeCount >= SimRuntime.TargetTreeCount) return;
        _sim.TryRegrowTreeSomewhere(StructureBufferTiles);
    }
}
