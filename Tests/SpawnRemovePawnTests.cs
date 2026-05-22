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
