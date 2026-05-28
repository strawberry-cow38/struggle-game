using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Jobs;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class BlueprintCostTests
{
    [Fact]
    public void GodModeOff_WallBlueprint_PullsWoodAndCompletes()
    {
        var sim = new SimRuntime();
        sim.SetGodModeFreeBuild(false);

        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 2, c);

        // Spawn enough wood adjacent to the blueprint to cover its cost.
        var pile = sim.Store.CreateEntity();
        var pileTile = new TilePos(c + 3, c);
        pile.AddComponent(new Wood { Tile = pileTile, Count = SimRuntime.WallWoodCost });
        pile.AddComponent(new WorldPos { X = pileTile.X + 0.5f, Y = pileTile.Y + 0.5f });

        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));

        // Run long enough for a pawn to claim haul → carry → drop → build.
        for (int i = 0; i < 1800; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(WallType.Stone, sim.Map.GetWall(bpTile));
        Assert.False(sim.Store.TryGetEntityById(pile.Id, out var still) && still.HasComponent<Wood>(),
            "wood stack should have been consumed by blueprint deposit");
    }

    [Fact]
    public void GodModeOff_NoWood_BlueprintStallsUnfunded()
    {
        var sim = new SimRuntime();
        sim.SetGodModeFreeBuild(false);

        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 2, c);
        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(WallType.None, sim.Map.GetWall(bpTile));
        var job = sim.Jobs.GetByTile(bpTile);
        Assert.NotNull(job);
        // No pawn should have claimed the build job — funding isn't there
        // yet, so the claim filter must skip it.
        Assert.Equal(JobState.Open, job!.State);
    }

    [Fact]
    public void GodModeOff_OversizedStack_OnlyConsumesNeededAmount()
    {
        var sim = new SimRuntime();
        sim.SetGodModeFreeBuild(false);

        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 2, c);
        var pileTile = new TilePos(c + 3, c);

        var pile = sim.Store.CreateEntity();
        pile.AddComponent(new Wood { Tile = pileTile, Count = SimRuntime.WallWoodCost + 15 });
        pile.AddComponent(new WorldPos { X = pileTile.X + 0.5f, Y = pileTile.Y + 0.5f });

        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));
        for (int i = 0; i < 1800; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(WallType.Stone, sim.Map.GetWall(bpTile));

        int totalWood = 0;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood w, Entity _) => totalWood += w.Count);
        Assert.Equal(15, totalWood);
    }

    [Fact]
    public void GodModeOff_CancelFundedBlueprint_RefundsDepositedWoodOnTile()
    {
        var sim = new SimRuntime();
        sim.SetGodModeFreeBuild(false);

        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 2, c);
        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));
        // Drain the queue so the blueprint entity exists.
        sim.Step(SimConstants.TickSeconds);

        // Deposit straight into the blueprint to skip the haul timeline.
        var job = sim.Jobs.GetByTile(bpTile);
        Assert.NotNull(job);
        string woodPath = StruggleGame.Sim.Items.ItemCatalog.Wood.FullPath;
        int leftover = BlueprintCostOps.Deposit(job!.Entity, woodPath, SimRuntime.WallWoodCost);
        Assert.Equal(0, leftover);

        sim.CancelJobAtTile(bpTile);
        sim.Step(SimConstants.TickSeconds);

        int onTile = 0;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood w, Entity _) =>
        {
            if (w.Tile == bpTile) onTile += w.Count;
        });
        Assert.Equal(SimRuntime.WallWoodCost, onTile);
        Assert.Equal(WallType.None, sim.Map.GetWall(bpTile));
    }

    [Fact]
    public void WoodOnWallBlueprintTile_GetsRelocatedBeforeBuild()
    {
        var sim = new SimRuntime();
        // God-mode on so the blueprint doesn't try to consume the
        // blocking wood for its own funding — we want to observe pure
        // relocation behavior.
        Assert.True(sim.GodModeFreeBuild);

        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 2, c);
        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));
        sim.Step(SimConstants.TickSeconds);

        sim.SpawnWoodPile(bpTile, 4);

        for (int i = 0; i < 3000; i++) sim.Step(SimConstants.TickSeconds);

        int onBlueprintTile = 0;
        int elsewhere = 0;
        sim.Store.Query<Wood>().ForEachEntity((ref Wood w, Entity _) =>
        {
            if (w.Tile == bpTile) onBlueprintTile += w.Count;
            else elsewhere += w.Count;
        });
        Assert.Equal(WallType.Stone, sim.Map.GetWall(bpTile));
        Assert.Equal(0, onBlueprintTile);
        Assert.Equal(4, elsewhere);
    }

    [Fact]
    public void GodModeOn_BlueprintBuildsWithoutWood()
    {
        var sim = new SimRuntime();
        Assert.True(sim.GodModeFreeBuild, "default should be god mode on");

        int c = SimConstants.MapSize / 2;
        var bpTile = new TilePos(c + 2, c);
        sim.QueueCommand(new PlaceWallBlueprintCommand(bpTile));
        for (int i = 0; i < 1200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(WallType.Stone, sim.Map.GetWall(bpTile));
    }
}
