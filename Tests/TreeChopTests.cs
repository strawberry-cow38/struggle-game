using System.Linq;
using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class TreeChopTests
{
    [Fact]
    public void DefaultSim_HasFiftyTrees()
    {
        var sim = new SimRuntime();
        Assert.Equal(50, CountTrees(sim));
    }

    [Fact]
    public void TreeTiles_BlockWalkability()
    {
        var sim = new SimRuntime();
        var view = sim.MapView;
        foreach (var t in sim.TreeTiles)
        {
            Assert.False(view.Walkable(t), $"tree tile {t.X},{t.Y} must block walkability");
        }
    }

    [Fact]
    public void ChopJobInRect_ChopsTreeAndDropsWood()
    {
        var sim = new SimRuntime();

        // Pick a tree near map center so colonists can actually reach it.
        TilePos? targetTile = null;
        int c = SimConstants.MapSize / 2;
        int bestDist = int.MaxValue;
        foreach (var t in sim.TreeTiles)
        {
            int d = Math.Abs(t.X - c) + Math.Abs(t.Y - c);
            if (d < bestDist) { bestDist = d; targetTile = t; }
        }
        Assert.NotNull(targetTile);
        var tile = targetTile!.Value;
        // Force mature so the chop designator accepts the tile.
        Assert.True(sim.TryGetTree(tile, out var treeEnt));
        treeEnt.GetComponent<Growth>().Stage = 1f;

        sim.QueueCommand(new ChopTreesInRectCommand(tile, tile));
        // Run enough ticks for a colonist to walk + chop (2s) + complete.
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.False(sim.TreeTiles.Contains(tile), "Tree should be removed after chop.");
        Assert.True(sim.MapView.Walkable(tile), "Tile should be walkable post-chop.");
        Assert.Equal(1, CountWood(sim));
    }

    [Fact]
    public void ChopRectWithNoTrees_PostsNoJobs()
    {
        var sim = new SimRuntime();
        int before = sim.Jobs.Count;

        // Outside the map, no trees can match.
        sim.QueueCommand(new ChopTreesInRectCommand(new TilePos(-100, -100), new TilePos(-50, -50)));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(before, sim.Jobs.Count);
    }

    [Fact]
    public void DoubleChopOnSameTile_OnlyOneJob()
    {
        var sim = new SimRuntime();
        var tile = sim.TreeTiles.First();
        Assert.True(sim.TryGetTree(tile, out var treeEnt));
        treeEnt.GetComponent<Growth>().Stage = 1f;
        sim.QueueCommand(new ChopTreesInRectCommand(tile, tile));
        sim.QueueCommand(new ChopTreesInRectCommand(tile, tile));
        sim.Step(SimConstants.TickSeconds);
        int chopJobs = 0;
        foreach (var j in sim.Jobs.All) if (j.Kind == JobKind.ChopTree) chopJobs++;
        Assert.Equal(1, chopJobs);
    }

    private static int CountTrees(SimRuntime sim)
    {
        int n = 0;
        sim.Store.Query<Tree>().ForEachEntity((ref Tree _, Entity _) => n++);
        return n;
    }

    private static int CountWood(SimRuntime sim)
    {
        int n = 0;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood _, Entity _) => n++);
        return n;
    }
}
