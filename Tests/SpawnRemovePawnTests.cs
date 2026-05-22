using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class SpawnRemovePawnTests
{
    [Fact]
    public void DefaultPawnCount_IsThree()
    {
        var sim = new SimRuntime();
        Assert.Equal(3, CountWanderers(sim));
    }

    [Fact]
    public void SpawnDummyCommand_AddsOnePawn()
    {
        var sim = new SimRuntime();
        int before = CountWanderers(sim);

        sim.QueueCommand(new SpawnDummyCommand());
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(before + 1, CountWanderers(sim));
    }

    [Fact]
    public void RemoveDummyCommand_DeletesTargetPawn()
    {
        var sim = new SimRuntime();
        int target = LowestPawnId(sim);

        sim.QueueCommand(new RemoveDummyCommand(target));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(2, CountWanderers(sim));
        Assert.False(sim.Store.TryGetEntityById(target, out _));
    }

    [Fact]
    public void RemoveDummyCommand_OnUnknownId_IsNoop()
    {
        var sim = new SimRuntime();
        int before = CountWanderers(sim);

        sim.QueueCommand(new RemoveDummyCommand(999999));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(before, CountWanderers(sim));
    }

    [Fact]
    public void RemoveAllPawns_SimKeepsTickingCleanly()
    {
        var sim = new SimRuntime();
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) => ids.Add(e.Id));

        foreach (var id in ids) sim.QueueCommand(new RemoveDummyCommand(id));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(0, CountWanderers(sim));

        // Tick for a couple of seconds with zero colonists — no exceptions,
        // no anomalies, snapshot stays well-formed.
        for (int i = 0; i < 180; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(0, sim.Watcher.StuckTotal);
        Assert.Equal(0, sim.Watcher.BrainDeadTotal);

        // Player can still queue blueprints; they just sit (no claimants).
        int c = SimConstants.MapSize / 2;
        sim.QueueCommand(new PlaceWallBlueprintCommand(new StruggleGame.Sim.Map.TilePos(c, c)));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(1, sim.Jobs.Count);

        // Spawning recovers — new pawn shows up at random walkable tile.
        sim.QueueCommand(new SpawnDummyCommand());
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(1, CountWanderers(sim));
    }

    private static int CountWanderers(SimRuntime sim)
    {
        int n = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity _) => n++);
        return n;
    }

    private static int LowestPawnId(SimRuntime sim)
    {
        int best = int.MaxValue;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        {
            if (e.Id < best) best = e.Id;
        });
        Assert.NotEqual(int.MaxValue, best);
        return best;
    }
}
