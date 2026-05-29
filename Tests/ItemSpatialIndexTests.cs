using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

// The item spatial index must never drift from the ECS. These exercise
// every mutation path (spawn, consume-to-zero, merge, full haul cycle)
// and assert ValidateAgainst stays clean, plus the two queries.
public class ItemSpatialIndexTests
{
    [Fact]
    public void SpawnAndQuery_WoodAndPile()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var woodTile = NearbyWalkable(sim, new TilePos(50, 50));
        var pileTile = NearbyWalkableNotEqual(sim, new TilePos(55, 50), woodTile);

        sim.SpawnWoodPile(woodTile);
        sim.SpawnItemPile(pileTile, ItemCatalog.Carrot.FullPath, 3);

        // Occupancy is now item-kind agnostic — wood and carrots both count.
        Assert.True(sim.ItemIndex.AnyItemAt(woodTile));
        Assert.True(sim.ItemIndex.AnyItemAt(pileTile));
        Assert.False(sim.ItemIndex.AnyItemAt(NearbyWalkableNotEqual(sim, new TilePos(70, 70), woodTile)));
        // Nearest is still path-specific.
        Assert.True(sim.ItemIndex.TryGetNearest(woodTile, ItemCatalog.Carrot.FullPath, out var found));
        Assert.Equal(pileTile, found);
        sim.ItemIndex.ValidateAgainst(sim.Store);
    }

    [Fact]
    public void NearestPicksClosestOfMatchingPath()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var from = NearbyWalkable(sim, new TilePos(50, 50));
        var near = NearbyWalkableNotEqual(sim, new TilePos(52, 50), from);
        var far = NearbyWalkableNotEqual(sim, new TilePos(90, 90), from);

        sim.SpawnItemPile(far, ItemCatalog.Carrot.FullPath, 1);
        sim.SpawnItemPile(near, ItemCatalog.Carrot.FullPath, 1);
        // A different path nearby must not be picked.
        sim.SpawnItemPile(from, ItemCatalog.SimpleMeal.FullPath, 1);

        Assert.True(sim.ItemIndex.TryGetNearest(from, ItemCatalog.Carrot.FullPath, out var found));
        Assert.Equal(near, found);
        sim.ItemIndex.ValidateAgainst(sim.Store);
    }

    [Fact]
    public void ConsumeToZero_RemovesFromIndex()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var t = NearbyWalkable(sim, new TilePos(50, 50));
        sim.SpawnItemPile(t, ItemCatalog.Carrot.FullPath, 2);

        sim.TryConsumeFromPile(t, ItemCatalog.Carrot.FullPath, 2);

        Assert.False(sim.ItemIndex.TryGetNearest(t, ItemCatalog.Carrot.FullPath, out _));
        sim.ItemIndex.ValidateAgainst(sim.Store);
    }

    [Fact]
    public void CoincidentWoodPiles_MergeStaysConsistent()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var t = NearbyWalkable(sim, new TilePos(50, 50));
        sim.SpawnWoodPile(t);
        sim.SpawnWoodPile(t);
        // MergeCoincidentWood runs each Step; advance to let it fold them.
        for (int i = 0; i < 3; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.ItemIndex.AnyItemAt(t));
        sim.ItemIndex.ValidateAgainst(sim.Store);
    }

    [Fact]
    public void FullHaulCycle_KeepsIndexConsistent()
    {
        var sim = new SimRuntime();
        var dest = NearbyWalkable(sim, new TilePos(60, 60));
        sim.QueueCommand(new CreateStockpileRectCommand(dest, dest));
        sim.Step(SimConstants.TickSeconds);

        var src = NearbyWalkableNotEqual(sim, new TilePos(46, 46), dest);
        sim.SpawnWoodPile(src);

        // Run the full pickup→carry→deliver cycle; the index is fed only by
        // component add/remove events (incl. CommandBuffer playback), so any
        // gap would surface as drift here.
        for (int i = 0; i < 4000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (i % 50 == 0) sim.ItemIndex.ValidateAgainst(sim.Store);
        }
        sim.ItemIndex.ValidateAgainst(sim.Store);
    }

    [Fact]
    public void OverCapPiles_SpillToSeparateTiles()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var t = NearbyWalkable(sim, new TilePos(50, 50));
        // 70 + 50 = 120 > 75 cap, so they can't merge — the extra must spill.
        sim.SpawnItemPile(t, ItemCatalog.Carrot.FullPath, 70);
        sim.SpawnItemPile(t, ItemCatalog.Carrot.FullPath, 50);

        for (int i = 0; i < 5; i++) sim.Step(SimConstants.TickSeconds);

        var tiles = new List<TilePos>();
        int total = 0;
        sim.Store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity _) =>
        {
            if (p.ItemPath != ItemCatalog.Carrot.FullPath) return;
            tiles.Add(p.Tile);
            total += p.Count;
        });
        Assert.Equal(2, tiles.Count);                 // still two piles
        Assert.NotEqual(tiles[0], tiles[1]);          // but on different tiles now
        Assert.Equal(120, total);                     // nothing lost
        sim.ItemIndex.ValidateAgainst(sim.Store);
    }

    [Fact]
    public void ReservedWood_DoesNotCountAsUnreserved()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var t = NearbyWalkable(sim, new TilePos(50, 50));
        sim.SpawnWoodPile(t);

        // Fresh wood: wedges a door (unreserved).
        Assert.True(sim.ItemIndex.AnyUnreservedItemAt(t));

        // Find the wood entity and reserve it — should stop counting.
        int id = 0;
        sim.Store.Query<ItemPile>().ForEachEntity((ref ItemPile w, Entity e) => { if (w.ItemPath == ItemCatalog.Wood.FullPath && w.Tile == t) id = e.Id; });
        Assert.True(sim.Store.TryGetEntityById(id, out var wood));
        wood.AddComponent(new HaulReserved { JobId = StruggleGame.Sim.Jobs.JobId.None });
        Assert.True(sim.ItemIndex.AnyItemAt(t));            // still wood there
        Assert.False(sim.ItemIndex.AnyUnreservedItemAt(t)); // but reserved now
        sim.ItemIndex.ValidateAgainst(sim.Store);

        // Un-reserve: counts again.
        wood.RemoveComponent<HaulReserved>();
        Assert.True(sim.ItemIndex.AnyUnreservedItemAt(t));
        sim.ItemIndex.ValidateAgainst(sim.Store);
    }

    private static TilePos NearbyWalkable(SimRuntime sim, TilePos near)
        => NearbyWalkableNotEqual(sim, near, new TilePos(int.MinValue, int.MinValue));

    private static TilePos NearbyWalkableNotEqual(SimRuntime sim, TilePos near, TilePos avoid)
    {
        for (int r = 0; r < SimConstants.MapSize; r++)
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    var t = new TilePos(near.X + dx, near.Y + dy);
                    if (t == avoid) continue;
                    if (!sim.MapView.Walkable(t)) continue;
                    if (sim.TreeTiles.Contains(t)) continue;
                    return t;
                }
        throw new Xunit.Sdk.XunitException("no walkable tile near anchor");
    }
}
