using StruggleGame.Sim;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Pathfinding;
using Xunit;

namespace StruggleGame.Tests;

public class SimRuntimeTests
{
    [Fact]
    public void Step_IncrementsTick()
    {
        var sim = new SimRuntime();
        Assert.Equal(0, sim.Tick);

        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(3, sim.Tick);
    }

    [Fact]
    public void TileMeters_IsOnePointFive()
    {
        Assert.Equal(1.5f, SimConstants.TileMeters);
    }

    [Fact]
    public void DefaultMap_IsRequestedSize()
    {
        var sim = new SimRuntime();
        Assert.Equal(SimConstants.MapSize, sim.Map.Width);
        Assert.Equal(SimConstants.MapSize, sim.Map.Height);
    }
}

public class TileMapTests
{
    [Fact]
    public void Border_IsImpassableButNotAWall()
    {
        // Magic border: not a stone wall in the layer (so it doesn't
        // render / get treated as a deconstructable wall), but Walkable
        // still refuses to enter it so pawns can't path off the edge.
        var map = TileMap.GenerateDefault(64, 64);
        for (int x = 0; x < 64; x++)
        {
            Assert.True(map.IsBorder(x, 0));
            Assert.True(map.IsBorder(x, 63));
            Assert.Equal(WallType.None, map.GetWall(x, 0));
            Assert.Equal(WallType.None, map.GetWall(x, 63));
            Assert.False(map.Walkable(x, 0));
            Assert.False(map.Walkable(x, 63));
        }
        for (int y = 0; y < 64; y++)
        {
            Assert.True(map.IsBorder(0, y));
            Assert.True(map.IsBorder(63, y));
            Assert.False(map.Walkable(0, y));
            Assert.False(map.Walkable(63, y));
        }
    }

    [Fact]
    public void InBounds_RejectsNegative()
    {
        var map = new TileMap(8, 8);
        Assert.False(map.InBounds(-1, 0));
        Assert.False(map.InBounds(0, -1));
        Assert.False(map.InBounds(8, 0));
        Assert.True(map.InBounds(7, 7));
    }
}

public class AStarTests
{
    [Fact]
    public void StraightLine_Open()
    {
        // y=0 / y=9 / x=0 / x=9 are magic border on a 10x10 map, so the
        // path runs along an interior row.
        var map = new TileMap(10, 10);
        var astar = new AStar(10, 10);

        var path = astar.FindPath(map.Snapshot(0), new TilePos(1, 1), new TilePos(8, 1));

        Assert.NotNull(path);
        Assert.Equal(new TilePos(1, 1), path![0]);
        Assert.Equal(new TilePos(8, 1), path[^1]);
    }

    [Fact]
    public void Blocked_ReturnsNull()
    {
        var map = new TileMap(5, 5);
        for (int y = 0; y < 5; y++) map.SetWall(2, y, WallType.Stone);
        var astar = new AStar(5, 5);

        var path = astar.FindPath(map.Snapshot(0), new TilePos(0, 0), new TilePos(4, 4));

        Assert.Null(path);
    }

    [Fact]
    public void StartOnWall_ReturnsNull()
    {
        var map = new TileMap(5, 5);
        map.SetWall(0, 0, WallType.Stone);
        var astar = new AStar(5, 5);

        var path = astar.FindPath(map.Snapshot(0), new TilePos(0, 0), new TilePos(4, 4));

        Assert.Null(path);
    }
}
