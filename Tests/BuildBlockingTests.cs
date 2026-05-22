using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class BuildBlockingTests
{
    [Fact]
    public void OccupiedBlueprintTile_BlocksCompletion()
    {
        var sim = new SimRuntime();
        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 1, c);

        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));

        // Draft a pawn and order it onto the blueprint tile so it physically
        // occupies the destination while other pawns try to build it.
        var blocker = FindWandererOffTile(sim, bpTile);
        sim.QueueCommand(new ToggleDraftCommand(blocker.Id));
        sim.Step(SimConstants.TickSeconds);
        sim.QueueCommand(new IssueMoveOrderCommand(blocker.Id, bpTile, false));

        // 600 ticks @ 60Hz = 10s — far longer than BuildTimeSec.
        for (int i = 0; i < 600; i++) sim.Step(SimConstants.TickSeconds);

        // Blocker must actually be on the tile for the test to mean anything.
        var pos = blocker.GetComponent<WorldPos>();
        Assert.True((int)pos.X == bpTile.X && (int)pos.Y == bpTile.Y,
            $"Blocker drafted-walk failed: pos=({pos.X:0.0},{pos.Y:0.0}), bp=({bpTile.X},{bpTile.Y})");

        Assert.Equal(WallType.None, sim.Map.GetWall(bpTile));
        // Job should still be live (not cancelled), waiting for tile to clear.
        var job = sim.Jobs.GetByTile(bpTile);
        Assert.NotNull(job);
    }

    [Fact]
    public void OccupiedBlueprintTile_CompletesAfterBlockerLeaves()
    {
        var sim = new SimRuntime();
        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 1, c);

        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));
        var blocker = FindWandererOffTile(sim, bpTile);
        sim.QueueCommand(new ToggleDraftCommand(blocker.Id));
        sim.Step(SimConstants.TickSeconds);
        sim.QueueCommand(new IssueMoveOrderCommand(blocker.Id, bpTile, false));
        for (int i = 0; i < 600; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(WallType.None, sim.Map.GetWall(bpTile));

        // Order the blocker to walk well away from the tile.
        sim.QueueCommand(new IssueMoveOrderCommand(blocker.Id, new TilePos(c - 8, c - 8), false));
        for (int i = 0; i < 600; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(WallType.Stone, sim.Map.GetWall(bpTile));
    }

    private static Entity FindWandererOffTile(SimRuntime sim, TilePos tile)
    {
        Entity found = default;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity e) =>
        {
            if (!found.IsNull) return;
            if ((int)p.X == tile.X && (int)p.Y == tile.Y) return;
            found = e;
        });
        Assert.False(found.IsNull, "No wanderer found off the target tile.");
        return found;
    }
}
