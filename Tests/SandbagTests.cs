using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class SandbagTests
{
    [Fact]
    public void PlaceSandbagBlueprint_PostsBuildJob_AndOccupiesTile()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        sim.QueueCommand(new PlaceSandbagCommand(tile));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(1, CountJobsOfKind(sim, JobKind.SandbagBuild));
        // Sandbag is low cover, not a hard wall — the tile stays walkable.
        Assert.True(sim.MapView.Walkable(tile));
    }

    [Fact]
    public void Sandbag_BuildsToCompletion()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        sim.QueueCommand(new PlaceSandbagCommand(tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.SandbagMap.ContainsKey(tile));
        Assert.Equal(0, CountJobsOfKind(sim, JobKind.SandbagBuild));
    }

    [Fact]
    public void Sandbag_IsWalkableButHighCost()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        Assert.True(sim.InstantPlaceSandbag(tile));
        Assert.True(sim.SandbagMap.ContainsKey(tile));
        // RebuildMapView is deferred to end-of-tick — step once to apply.
        sim.Step(SimConstants.TickSeconds);
        // Walkable, but routed-around: furniture cost weight, not 1.0.
        Assert.True(sim.MapView.Walkable(tile));
        Assert.Equal(MapView.FurnitureCost, sim.MapView.CostAt(tile.X, tile.Y), 3);
    }

    [Fact]
    public void Sandbag_CannotStackOnExisting()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        Assert.True(sim.InstantPlaceSandbag(tile));
        Assert.False(sim.CanPlaceSandbag(tile));
        Assert.False(sim.TryPlaceSandbagBlueprint(tile));
    }

    [Fact]
    public void Sandbag_Deconstructs()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        Assert.True(sim.InstantPlaceSandbag(tile));
        Assert.True(sim.TryPostSandbagDeconstructJob(tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.False(sim.SandbagMap.ContainsKey(tile));
        Assert.True(sim.MapView.Walkable(tile));
    }

    private static TilePos NearestBuildableTile(SimRuntime sim)
    {
        int c = SimConstants.MapSize / 2;
        for (int r = 1; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = c + dx, y = c + dy;
                    var t = new TilePos(x, y);
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    bool occ = false;
                    sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
                    {
                        if ((int)p.X == x && (int)p.Y == y) occ = true;
                    });
                    if (occ) continue;
                    return t;
                }
        }
        throw new Xunit.Sdk.XunitException("no buildable tile near center");
    }

    private static int CountJobsOfKind(SimRuntime sim, JobKind kind)
    {
        int n = 0;
        foreach (var j in sim.Jobs.All) if (j.Kind == kind) n++;
        return n;
    }
}
