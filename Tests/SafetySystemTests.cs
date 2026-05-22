using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class SafetySystemTests
{
    [Fact]
    public void PawnOnWall_GetsSnappedToNearestWalkable()
    {
        var sim = new SimRuntime();
        var pawn = FindWanderer(sim);

        // Top border row is all wall (TileMap.GenerateDefault).
        ref var pos = ref pawn.GetComponent<WorldPos>();
        pos.X = 5.5f;
        pos.Y = 0.5f;
        Assert.False(sim.Map.Walkable(5, 0));

        sim.Step(SimConstants.TickSeconds);

        var after = pawn.GetComponent<WorldPos>();
        int tx = (int)after.X;
        int ty = (int)after.Y;
        Assert.True(sim.Map.Walkable(tx, ty),
            $"Rescued pos must be walkable; got ({after.X:0.0},{after.Y:0.0}) -> tile ({tx},{ty})");
        Assert.True(sim.Watcher.RescuedTotal >= 1, "Watcher should have recorded a rescue.");
    }

    private static Entity FindWanderer(SimRuntime sim)
    {
        Entity found = default;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        {
            if (found.IsNull) found = e;
        });
        Assert.False(found.IsNull);
        return found;
    }
}
