using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Stockpiles;
using Xunit;

namespace StruggleGame.Tests;

public class StockpileTests
{
    [Fact]
    public void CreateRect_ProducesZoneWithEveryTileInRect()
    {
        var sim = new SimRuntime();
        var a = new TilePos(10, 10);
        var b = new TilePos(12, 11);
        sim.QueueCommand(new CreateStockpileRectCommand(a, b));
        sim.Step(SimConstants.TickSeconds);

        Assert.Single(sim.Stockpiles);
        var pile = sim.Stockpiles[0];
        Assert.Equal(6, pile.Tiles.Count);
        for (int y = 10; y <= 11; y++)
            for (int x = 10; x <= 12; x++)
                Assert.Contains(new TilePos(x, y), pile.Tiles);
    }

    [Fact]
    public void Create_DefaultsToNormalPriority_AndAllowsAllCatalogItems()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(10, 10)));
        sim.Step(SimConstants.TickSeconds);

        var pile = sim.Stockpiles[0];
        Assert.Equal(StockpilePriority.Normal, pile.Priority);
        Assert.True(pile.Allows(ItemCatalog.Wood));
    }

    [Fact]
    public void CreateRect_SkipsTilesAlreadyClaimedByAnotherZone()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(11, 10)));
        sim.Step(SimConstants.TickSeconds);
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(11, 10), new TilePos(12, 10)));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal(2, sim.Stockpiles.Count);
        Assert.Equal(2, sim.Stockpiles[0].Tiles.Count);
        Assert.Single(sim.Stockpiles[1].Tiles);
        Assert.Contains(new TilePos(12, 10), sim.Stockpiles[1].Tiles);
    }

    [Fact]
    public void Expand_AddsFreeTiles_AndSkipsClaimed()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(10, 10)));
        sim.Step(SimConstants.TickSeconds);
        int id = sim.Stockpiles[0].Id;

        sim.QueueCommand(new ExpandStockpileRectCommand(id, new TilePos(10, 10), new TilePos(12, 10)));
        sim.Step(SimConstants.TickSeconds);

        var pile = sim.Stockpiles[0];
        Assert.Equal(3, pile.Tiles.Count);
        Assert.Contains(new TilePos(10, 10), pile.Tiles);
        Assert.Contains(new TilePos(11, 10), pile.Tiles);
        Assert.Contains(new TilePos(12, 10), pile.Tiles);
    }

    [Fact]
    public void Shrink_RemovesTiles_LeavesOthersIntact()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(12, 10)));
        sim.Step(SimConstants.TickSeconds);
        int id = sim.Stockpiles[0].Id;

        sim.QueueCommand(new ShrinkStockpileRectCommand(id, new TilePos(11, 10), new TilePos(11, 10)));
        sim.Step(SimConstants.TickSeconds);

        var pile = sim.Stockpiles[0];
        Assert.Equal(2, pile.Tiles.Count);
        Assert.DoesNotContain(new TilePos(11, 10), pile.Tiles);
        // Shrunk-out tile is now free to be claimed by another zone.
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(11, 10), new TilePos(11, 10)));
        sim.Step(SimConstants.TickSeconds);
        Assert.Equal(2, sim.Stockpiles.Count);
    }

    [Fact]
    public void Delete_RemovesZoneAndFreesTiles()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(11, 10)));
        sim.Step(SimConstants.TickSeconds);
        int id = sim.Stockpiles[0].Id;

        sim.QueueCommand(new DeleteStockpileCommand(id));
        sim.Step(SimConstants.TickSeconds);
        Assert.Empty(sim.Stockpiles);

        // Tiles are reusable after delete.
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(11, 10)));
        sim.Step(SimConstants.TickSeconds);
        Assert.Single(sim.Stockpiles);
    }

    [Fact]
    public void SetCategoryAllowed_FalseClearsEntireSubtree()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(10, 10)));
        sim.Step(SimConstants.TickSeconds);
        int id = sim.Stockpiles[0].Id;

        sim.QueueCommand(new SetStockpileCategoryAllowedCommand(id, "Resources", false));
        sim.Step(SimConstants.TickSeconds);
        Assert.False(sim.Stockpiles[0].Allows(ItemCatalog.Wood));

        sim.QueueCommand(new SetStockpileCategoryAllowedCommand(id, "Resources", true));
        sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.Stockpiles[0].Allows(ItemCatalog.Wood));
    }

    [Fact]
    public void RenameAndPriority_RoundTrip()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(10, 10)));
        sim.Step(SimConstants.TickSeconds);
        int id = sim.Stockpiles[0].Id;

        sim.QueueCommand(new RenameStockpileCommand(id, "Lumber"));
        sim.QueueCommand(new SetStockpilePriorityCommand(id, StockpilePriority.Critical));
        sim.Step(SimConstants.TickSeconds);

        Assert.Equal("Lumber", sim.Stockpiles[0].Name);
        Assert.Equal(StockpilePriority.Critical, sim.Stockpiles[0].Priority);
    }

    [Fact]
    public void SetItemAllowed_TogglesSingleItem()
    {
        var sim = new SimRuntime();
        sim.QueueCommand(new CreateStockpileRectCommand(new TilePos(10, 10), new TilePos(10, 10)));
        sim.Step(SimConstants.TickSeconds);
        int id = sim.Stockpiles[0].Id;

        sim.QueueCommand(new SetStockpileItemAllowedCommand(id, "Resources/Wood/Wood", false));
        sim.Step(SimConstants.TickSeconds);
        Assert.False(sim.Stockpiles[0].Allows(ItemCatalog.Wood));

        sim.QueueCommand(new SetStockpileItemAllowedCommand(id, "Resources/Wood/Wood", true));
        sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.Stockpiles[0].Allows(ItemCatalog.Wood));
    }
}
