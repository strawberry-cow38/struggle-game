using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
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
