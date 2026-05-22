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

    [Fact]
    public void TwoFreeWoodOnSameTile_MergeIntoOneStack()
    {
        var sim = new SimRuntime();
        var tile = NearbyWalkable(sim, new TilePos(50, 50));
        sim.SpawnWoodPile(tile, 5);
        sim.SpawnWoodPile(tile, 7);

        // The merge pass runs each Step; one tick is enough.
        sim.Step(SimConstants.TickSeconds);

        int entities = 0;
        int total = 0;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood w, Entity _) =>
        {
            if (w.Tile != tile) return;
            entities++;
            total += w.Count;
        });
        Assert.Equal(1, entities);
        Assert.Equal(12, total);
    }

    [Fact]
    public void FreeWood_PrefersPartialStackOnStockpile_OverEmptyTile()
    {
        var sim = new SimRuntime();
        var partial = NearbyWalkable(sim, new TilePos(50, 50));
        var empty = NearbyWalkableNotEqual(sim, new TilePos(52, 50), partial);
        // Single stockpile spanning both tiles, so priority + zone are equal.
        sim.QueueCommand(new CreateStockpileRectCommand(
            new TilePos(Math.Min(partial.X, empty.X), Math.Min(partial.Y, empty.Y)),
            new TilePos(Math.Max(partial.X, empty.X), Math.Max(partial.Y, empty.Y))));
        sim.Step(SimConstants.TickSeconds);

        sim.SpawnWoodPile(partial, 10);
        var src = NearbyWalkableNotEqual(sim, new TilePos(46, 46), partial);
        var src2 = NearbyWalkableNotEqual(sim, src, empty);
        sim.SpawnWoodPile(src2, 1);

        var dest = sim.TryFindBestHaulDest(src2, ItemCatalog.Wood, out var picked, out _);
        Assert.True(dest);
        Assert.Equal(partial, picked);
    }

    [Fact]
    public void PartialStackOnStockpile_GetsMergedIntoLargerStockpileStack()
    {
        var sim = new SimRuntime();
        var big = NearbyWalkable(sim, new TilePos(50, 50));
        var small = NearbyWalkableNotEqual(sim, new TilePos(54, 50), big);
        sim.QueueCommand(new CreateStockpileRectCommand(
            new TilePos(Math.Min(big.X, small.X), Math.Min(big.Y, small.Y)),
            new TilePos(Math.Max(big.X, small.X), Math.Max(big.Y, small.Y))));
        sim.Step(SimConstants.TickSeconds);

        sim.SpawnWoodPile(big, 20);
        sim.SpawnWoodPile(small, 3);

        bool merged = false;
        for (int i = 0; i < 8000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            int bigCount = WoodCountAtTile(sim, big);
            int smallCount = WoodCountAtTile(sim, small);
            if (bigCount == 23 && smallCount == 0) { merged = true; break; }
        }
        Assert.True(merged, "small partial stack should consolidate onto the bigger pile");
    }

    [Fact]
    public void Carrier_BatchesTwoNearbyPilesInOneDeliveryRound()
    {
        // Two small wood piles within HaulTopoffRadius should both be
        // picked up by a single carrier on one trip and end up at the
        // same stockpile dest (merged by the end-of-tick consolidator).
        var sim = new SimRuntime();
        var dest = NearbyWalkable(sim, new TilePos(80, 80));
        sim.QueueCommand(new CreateStockpileRectCommand(dest, dest));
        sim.Step(SimConstants.TickSeconds);

        var primary = NearbyWalkableNotEqual(sim, new TilePos(40, 40), dest);
        var secondary = NearbyWalkableNotEqual(sim, new TilePos(primary.X + 1, primary.Y), dest);
        int md = Math.Abs(primary.X - secondary.X) + Math.Abs(primary.Y - secondary.Y);
        Assert.InRange(md, 1, SimConstants.HaulTopoffRadius);

        sim.SpawnWoodPile(primary, 5);
        sim.SpawnWoodPile(secondary, 7);

        bool delivered = false;
        for (int i = 0; i < 8000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (WoodCountAtTile(sim, dest) == 12
                && !WoodAtTile(sim, primary)
                && !WoodAtTile(sim, secondary))
            {
                delivered = true;
                break;
            }
        }
        Assert.True(delivered, "both nearby piles should consolidate at the dest");
    }

    [Fact]
    public void Carrier_DoesNotBatchBeyondInventoryCap()
    {
        // Primary pile already saturates a colonist's carry capacity
        // (Weight * Count == MaxCarryWeight), so the topoff scan must
        // not reserve the neighbor. The neighbor stays where it was
        // until it gets its own primary haul.
        var sim = new SimRuntime();
        var dest = NearbyWalkable(sim, new TilePos(80, 80));
        sim.QueueCommand(new CreateStockpileRectCommand(dest, dest));
        sim.Step(SimConstants.TickSeconds);

        var primary = NearbyWalkableNotEqual(sim, new TilePos(40, 40), dest);
        var neighbor = NearbyWalkableNotEqual(sim, new TilePos(primary.X + 1, primary.Y), dest);
        int cap = (int)(SimConstants.MaxCarryWeight / ItemCatalog.Wood.Weight);
        sim.SpawnWoodPile(primary, cap);
        sim.SpawnWoodPile(neighbor, 4);

        // Run until something reaches the dest. The first arrival must
        // be just the primary (cap units) — not primary + neighbor.
        int firstAtDest = 0;
        for (int i = 0; i < 8000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            int dc = WoodCountAtTile(sim, dest);
            if (dc > 0) { firstAtDest = dc; break; }
        }
        Assert.Equal(cap, firstAtDest);
    }

    [Fact]
    public void Door_StaysOpenWhileWoodSitsOnTile()
    {
        // Build a door, drop wood on it, wait past AutoCloseSec, and the
        // door must still be Open (not Closed or Closing).
        var sim = new SimRuntime();
        // Pick a buildable tile away from the spawn cluster.
        TilePos tile = default;
        bool found = false;
        for (int r = 5; r < SimConstants.MapSize && !found; r++)
        {
            for (int dy = -r; dy <= r && !found; dy++)
                for (int dx = -r; dx <= r && !found; dx++)
                {
                    var t = new TilePos(SimConstants.MapSize / 2 + dx, SimConstants.MapSize / 2 + dy);
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    var l = new TilePos(t.X - 1, t.Y);
                    var rr = new TilePos(t.X + 1, t.Y);
                    if (!sim.MapView.Walkable(l) || !sim.MapView.Walkable(rr)) continue;
                    if (sim.TreeTiles.Contains(l) || sim.TreeTiles.Contains(rr)) continue;
                    tile = t; found = true;
                }
        }
        Assert.True(found, "could not find buildable door tile");

        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(tile.X - 1, tile.Y)));
        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(tile.X + 1, tile.Y)));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        sim.QueueCommand(new PlaceDoorBlueprintCommand(tile));
        for (int i = 0; i < 1500; i++) sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.TryGetDoor(tile, out var doorEnt));

        sim.SpawnWoodPile(tile, 1);

        // Run well past AutoCloseSec — door must NOT close.
        int ticks = (int)(((DoorSystem.AutoCloseSec + DoorSystem.OpenTimeSec) * 4f / SimConstants.TickSeconds) + 5);
        for (int i = 0; i < ticks; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(DoorState.Open, doorEnt.GetComponent<Door>().State);
    }

    private static int WoodCountAtTile(SimRuntime sim, TilePos t)
    {
        int total = 0;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood w, Entity _) =>
        {
            if (w.Tile == t) total += w.Count;
        });
        return total;
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
