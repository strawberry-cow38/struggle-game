using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Stockpiles;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class HaulTests
{
    [Fact]
    public void TryFindBestHaulDest_PrefersHigherPriority()
    {
        var sim = new SimRuntime();
        var low = NearbyWalkable(sim, new TilePos(40, 50));
        var high = NearbyWalkable(sim, new TilePos(60, 50));
        sim.QueueCommand(new CreateStockpileRectCommand(low, low));
        sim.QueueCommand(new CreateStockpileRectCommand(high, high));
        sim.Step(SimConstants.TickSeconds);

        int lowId = sim.Stockpiles[0].Id;
        int highId = sim.Stockpiles[1].Id;
        sim.QueueCommand(new SetStockpilePriorityCommand(lowId, StockpilePriority.Low));
        sim.QueueCommand(new SetStockpilePriorityCommand(highId, StockpilePriority.Critical));
        sim.Step(SimConstants.TickSeconds);

        var src = NearbyWalkable(sim, new TilePos(50, 50));
        Assert.True(sim.TryFindBestHaulDest(src, ItemCatalog.Wood, out var dest, out int picked));
        Assert.Equal(highId, picked);
        Assert.Equal(high, dest);
    }

    [Fact]
    public void TryFindBestHaulDest_BreaksTieByDistance()
    {
        var sim = new SimRuntime();
        var near = NearbyWalkable(sim, new TilePos(48, 50));
        var far = NearbyWalkable(sim, new TilePos(80, 50));
        sim.QueueCommand(new CreateStockpileRectCommand(near, near));
        sim.QueueCommand(new CreateStockpileRectCommand(far, far));
        sim.Step(SimConstants.TickSeconds);

        var src = NearbyWalkable(sim, new TilePos(50, 50));
        Assert.True(sim.TryFindBestHaulDest(src, ItemCatalog.Wood, out var dest, out _));
        Assert.Equal(near, dest);
    }

    [Fact]
    public void WoodAlreadyOnAllowedStockpileTile_GetsNoHaulJob()
    {
        var sim = new SimRuntime();
        var tile = NearbyWalkable(sim, new TilePos(50, 50));
        sim.QueueCommand(new CreateStockpileRectCommand(tile, tile));
        sim.Step(SimConstants.TickSeconds);

        sim.SpawnWoodPile(tile);

        // Advance enough for HaulSystem to have posted (which it shouldn't).
        for (int i = 0; i < 5; i++) sim.Step(SimConstants.TickSeconds);

        int hauls = 0;
        foreach (var j in sim.Jobs.All) if (j.Kind == StruggleGame.Sim.Jobs.JobKind.Haul) hauls++;
        Assert.Equal(0, hauls);
    }

    [Fact]
    public void WoodInForbiddenZone_GetsHauledToAllowingZone()
    {
        var sim = new SimRuntime();
        var forbidden = NearbyWalkable(sim, new TilePos(48, 50));
        var allowed = NearbyWalkable(sim, new TilePos(60, 50));
        sim.QueueCommand(new CreateStockpileRectCommand(forbidden, forbidden));
        sim.QueueCommand(new CreateStockpileRectCommand(allowed, allowed));
        sim.Step(SimConstants.TickSeconds);

        int forbiddenId = sim.Stockpiles[0].Id;
        sim.QueueCommand(new SetStockpileCategoryAllowedCommand(forbiddenId, "Resources", false));
        sim.Step(SimConstants.TickSeconds);

        sim.SpawnWoodPile(forbidden);

        bool delivered = false;
        for (int i = 0; i < 8000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (WoodAtTile(sim, allowed)) { delivered = true; break; }
        }
        Assert.True(delivered, "wood should have been hauled to the allowing zone");
        Assert.False(WoodAtTile(sim, forbidden));
    }

    [Fact]
    public void WoodOutsideAnyZone_GetsHauledIn()
    {
        var sim = new SimRuntime();
        var dest = NearbyWalkable(sim, new TilePos(50, 50));
        sim.QueueCommand(new CreateStockpileRectCommand(dest, dest));
        sim.Step(SimConstants.TickSeconds);

        var src = NearbyWalkableNotEqual(sim, new TilePos(46, 46), dest);
        sim.SpawnWoodPile(src);

        bool delivered = false;
        for (int i = 0; i < 8000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (WoodAtTile(sim, dest)) { delivered = true; break; }
        }
        Assert.True(delivered, "wood should reach the stockpile");
    }

    [Fact]
    public void DeletingStockpileMidHaul_CancelsTheHaulJob()
    {
        var sim = new SimRuntime();
        var dest = NearbyWalkable(sim, new TilePos(60, 60));
        sim.QueueCommand(new CreateStockpileRectCommand(dest, dest));
        sim.Step(SimConstants.TickSeconds);
        int id = sim.Stockpiles[0].Id;

        var src = NearbyWalkableNotEqual(sim, new TilePos(46, 46), dest);
        sim.SpawnWoodPile(src);

        // Advance long enough for HaulSystem to post.
        for (int i = 0; i < 10; i++) sim.Step(SimConstants.TickSeconds);
        int haulsBefore = CountHaulJobs(sim);
        Assert.True(haulsBefore > 0, "expected a haul job to have been posted");

        sim.QueueCommand(new DeleteStockpileCommand(id));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(0, CountHaulJobs(sim));
    }

    // ---- helpers ----

    private static int CountHaulJobs(SimRuntime sim)
    {
        int n = 0;
        foreach (var j in sim.Jobs.All) if (j.Kind == StruggleGame.Sim.Jobs.JobKind.Haul) n++;
        return n;
    }

    private static bool WoodAtTile(SimRuntime sim, TilePos t)
    {
        bool found = false;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood w, Entity _) =>
        {
            if (w.Tile == t) found = true;
        });
        return found;
    }

    private static TilePos NearbyWalkable(SimRuntime sim, TilePos near)
    {
        for (int r = 0; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = near.X + dx;
                    int y = near.Y + dy;
                    var t = new TilePos(x, y);
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    return t;
                }
        }
        throw new Xunit.Sdk.XunitException("no walkable tile near anchor");
    }

    private static TilePos NearbyWalkableNotEqual(SimRuntime sim, TilePos near, TilePos avoid)
    {
        for (int r = 0; r < SimConstants.MapSize; r++)
        {
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = near.X + dx;
                    int y = near.Y + dy;
                    var t = new TilePos(x, y);
                    if (t == avoid) continue;
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    return t;
                }
        }
        throw new Xunit.Sdk.XunitException("no walkable tile near anchor");
    }
}
