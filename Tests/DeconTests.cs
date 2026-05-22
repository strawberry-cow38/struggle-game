using System.Linq;
using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class DeconTests
{
    [Fact]
    public void DeconRect_RemovesWallAndDropsHalfWood()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        // Place + build the wall first.
        sim.QueueCommand(new PlaceWallBlueprintCommand(tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(TileType.Wall, sim.Map.Get(tile));
        Assert.Contains(tile, sim.PlayerWalls);

        int woodBefore = CountWood(sim);

        // Now decon it.
        sim.QueueCommand(new DeconstructWallsInRectCommand(tile, tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(TileType.Grass, sim.Map.Get(tile));
        Assert.DoesNotContain(tile, sim.PlayerWalls);
        Assert.True(sim.MapView.Walkable(tile));
        Assert.Equal(woodBefore + SimRuntime.WallDeconWoodReturn, CountWood(sim));
    }

    [Fact]
    public void DeconRect_OnNonPlayerWall_PostsNoJob()
    {
        var sim = new SimRuntime();
        int before = sim.Jobs.Count;

        // Procgen / map-border walls are not in PlayerWalls. Pick a wall tile.
        TilePos? wall = null;
        for (int y = 0; y < sim.Map.Height && wall is null; y++)
            for (int x = 0; x < sim.Map.Width; x++)
                if (sim.Map.Get(new TilePos(x, y)) == TileType.Wall) { wall = new TilePos(x, y); break; }
        Assert.NotNull(wall);

        sim.QueueCommand(new DeconstructWallsInRectCommand(wall!.Value, wall.Value));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(before, sim.Jobs.Count);
    }

    [Fact]
    public void CancelTool_KillsInFlightDeconJob_LeavesWall()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        sim.QueueCommand(new PlaceWallBlueprintCommand(tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(TileType.Wall, sim.Map.Get(tile));

        sim.QueueCommand(new DeconstructWallsInRectCommand(tile, tile));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(1, CountDeconJobs(sim));

        sim.QueueCommand(new CancelJobsInRectCommand(tile, tile));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(0, CountDeconJobs(sim));
        Assert.Equal(TileType.Wall, sim.Map.Get(tile));
        Assert.Contains(tile, sim.PlayerWalls);
    }

    [Fact]
    public void DoubleDeconOnSameTile_OnlyOneJob()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        sim.QueueCommand(new PlaceWallBlueprintCommand(tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        sim.QueueCommand(new DeconstructWallsInRectCommand(tile, tile));
        sim.QueueCommand(new DeconstructWallsInRectCommand(tile, tile));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(1, CountDeconJobs(sim));
    }

    private static TilePos NearestBuildableTile(SimRuntime sim)
    {
        int c = SimConstants.MapSize / 2;
        // Pick a walkable tile near center with no tree.
        for (int r = 1; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = c + dx, y = c + dy;
                    var t = new TilePos(x, y);
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    // Avoid colonist-occupied tile.
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

    private static int CountDeconJobs(SimRuntime sim)
    {
        int n = 0;
        foreach (var j in sim.Jobs.All) if (j.Kind == JobKind.Deconstruct) n++;
        return n;
    }

    private static int CountWood(SimRuntime sim)
    {
        int n = 0;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood _, Entity _) => n++);
        return n;
    }
}
