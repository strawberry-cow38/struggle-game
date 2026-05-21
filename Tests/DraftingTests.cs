using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class DraftingTests
{
    [Fact]
    public void Drafting_DropsBuildTargetAndReleasesJob()
    {
        var sim = new SimRuntime();
        var center = SimConstants.MapSize / 2;

        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(center + 1, center)));
        for (int i = 0; i < 30; i++) sim.Step(SimConstants.TickSeconds);

        var pawn = FindClaimer(sim);
        Assert.True(pawn.HasComponent<BuildTarget>(), "Expected a pawn to have claimed the blueprint after 30 ticks.");
        var jobId = pawn.GetComponent<BuildTarget>().JobId;

        sim.QueueCommand(new ToggleDraftCommand(pawn.Id));
        sim.Step(SimConstants.TickSeconds);

        Assert.True(pawn.HasComponent<Drafted>(), "Drafted marker should be set after toggling.");
        Assert.False(pawn.HasComponent<BuildTarget>(), "Drafted pawn must drop its BuildTarget.");
        var job = sim.Jobs.Get(jobId);
        Assert.NotNull(job);
        // The released job may be re-claimed by another idle pawn on the
        // same tick. What matters is that *this* pawn is no longer the
        // claimant.
        Assert.NotEqual(pawn.Id, job!.Claimant.Id);
    }

    [Fact]
    public void Drafted_DoesNotReClaimJobs()
    {
        var sim = new SimRuntime();
        var center = SimConstants.MapSize / 2;

        var pawn = FindWanderer(sim);
        sim.QueueCommand(new ToggleDraftCommand(pawn.Id));
        sim.Step(SimConstants.TickSeconds);
        Assert.True(pawn.HasComponent<Drafted>());

        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(center + 1, center)));
        for (int i = 0; i < 200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.False(pawn.HasComponent<BuildTarget>(), "Drafted pawn must not claim any job.");
    }

    private static Entity FindWanderer(SimRuntime sim)
    {
        Entity found = default;
        sim.Store.Query<Wanderer>().ForEachEntity((ref Wanderer _, Entity e) =>
        {
            if (found.IsNull) found = e;
        });
        Assert.False(found.IsNull, "No wanderer entity in sim.");
        return found;
    }

    private static Entity FindClaimer(SimRuntime sim)
    {
        Entity found = default;
        sim.Store.Query<Wanderer, BuildTarget>().ForEachEntity((ref Wanderer _, ref BuildTarget _, Entity e) =>
        {
            if (found.IsNull) found = e;
        });
        Assert.False(found.IsNull, "No pawn claimed the blueprint.");
        return found;
    }
}
