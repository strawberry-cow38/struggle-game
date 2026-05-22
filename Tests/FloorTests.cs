using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class FloorTests
{
    [Fact]
    public void DragRectFloor_PlacesWoodFloorOnAllTiles()
    {
        var sim = new SimRuntime();
        var (a, b) = TwoByOneBuildableRect(sim);

        sim.QueueCommand(new FloorRectBlueprintCommand(a, b));
        for (int i = 0; i < 2400; i++) sim.Step(SimConstants.TickSeconds);

        int xmin = System.Math.Min(a.X, b.X);
        int xmax = System.Math.Max(a.X, b.X);
        int ymin = System.Math.Min(a.Y, b.Y);
        int ymax = System.Math.Max(a.Y, b.Y);
        for (int y = ymin; y <= ymax; y++)
            for (int x = xmin; x <= xmax; x++)
                Assert.Equal(FlooringType.Wood, sim.Map.GetFlooring(new TilePos(x, y)));
    }

    [Fact]
    public void FloorRect_OnWallTile_PostsNoJobForThatTile()
    {
        var sim = new SimRuntime();

        // Find a procgen wall tile.
        TilePos? wall = null;
        for (int y = 0; y < sim.Map.Height && wall is null; y++)
            for (int x = 0; x < sim.Map.Width; x++)
                if (sim.Map.GetWall(new TilePos(x, y)) != WallType.None) { wall = new TilePos(x, y); break; }
        Assert.NotNull(wall);

        int before = CountFloorJobs(sim);
        sim.QueueCommand(new FloorRectBlueprintCommand(wall!.Value, wall.Value));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(before, CountFloorJobs(sim));
        Assert.Equal(FlooringType.None, sim.Map.GetFlooring(wall.Value));
    }

    [Fact]
    public void WallBlueprintOverWoodFloor_BuildsSuccessfully_FloorSurvives()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        sim.QueueCommand(new FloorRectBlueprintCommand(tile, tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(FlooringType.Wood, sim.Map.GetFlooring(tile));

        sim.QueueCommand(new PlaceWallBlueprintCommand(tile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(WallType.Stone, sim.Map.GetWall(tile));
        Assert.Equal(FlooringType.Wood, sim.Map.GetFlooring(tile));
    }

    [Fact]
    public void DoubleFloorOnSameTile_OnlyOneJob()
    {
        var sim = new SimRuntime();
        var tile = NearestBuildableTile(sim);

        sim.QueueCommand(new FloorRectBlueprintCommand(tile, tile));
        sim.QueueCommand(new FloorRectBlueprintCommand(tile, tile));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(1, CountFloorJobs(sim));
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

    private static (TilePos a, TilePos b) TwoByOneBuildableRect(SimRuntime sim)
    {
        int c = SimConstants.MapSize / 2;
        for (int r = 1; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = c + dx, y = c + dy;
                    var a = new TilePos(x, y);
                    var b = new TilePos(x + 1, y);
                    if (!IsBuildable(sim, a) || !IsBuildable(sim, b)) continue;
                    return (a, b);
                }
        }
        throw new Xunit.Sdk.XunitException("no 2x1 buildable rect");
    }

    private static bool IsBuildable(SimRuntime sim, TilePos t)
    {
        if (!sim.MapView.Walkable(t)) return false;
        if (sim.TreeTiles.Contains(t)) return false;
        bool occ = false;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity _) =>
        {
            if ((int)p.X == t.X && (int)p.Y == t.Y) occ = true;
        });
        return !occ;
    }

    private static int CountFloorJobs(SimRuntime sim)
    {
        int n = 0;
        foreach (var j in sim.Jobs.All) if (j.Kind == JobKind.FloorBuild) n++;
        return n;
    }
}
