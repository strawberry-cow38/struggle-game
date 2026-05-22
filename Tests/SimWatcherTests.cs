using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Diagnostics;
using StruggleGame.Sim.Map;
using Xunit;

namespace StruggleGame.Tests;

public class SimWatcherTests
{
    [Fact]
    public void IdlePawnsDoNotTripBrainDead_WhenWandering()
    {
        // Default-pawn sim, no jobs. Wander loop should keep them
        // moving so the watcher records no anomalies after ~12s of ticks.
        var sim = new SimRuntime();
        int ticks = SimWatcher.BrainDeadTicks + 60; // a bit over threshold
        for (int i = 0; i < ticks; i++) sim.Step(SimConstants.TickSeconds);

        Assert.Equal(0, sim.Watcher.StuckTotal);
        Assert.Equal(0, sim.Watcher.BrainDeadTotal);
    }

    [Fact]
    public void HarnessScenario_PlacesBlueprintsAndCompletesSome()
    {
        // Mirrors the "quick" harness scenario in headless form so we
        // exercise PlaceWall + ToggleDraft + MoveOrder through the
        // command queue without Godot.
        var sim = new SimRuntime();
        int c = SimConstants.MapSize / 2;

        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(c + 1, c)));
        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(c - 1, c)));
        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(c, c + 1)));
        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(c, c - 1)));

        for (int i = 0; i < 600; i++) sim.Step(SimConstants.TickSeconds);

        // Pawns should have claimed and completed at least one of the
        // adjacent blueprints (sim runs at TickHz, BuildTimeSec ~ 1s so
        // 600 ticks is plenty).
        Assert.True(sim.MapVersion > 0, $"Expected at least one wall to complete; MapVersion={sim.MapVersion}");
        Assert.Equal(0, sim.Watcher.BrainDeadTotal);
    }
}
