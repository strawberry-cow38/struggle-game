using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using Xunit;

namespace StruggleGame.Tests;

public class BlueprintCrashRepro
{
    [Fact]
    public void PlaceBlueprintAndTick_DoesNotCrash()
    {
        var sim = new SimRuntime();
        var center = SimConstants.MapSize / 2;
        sim.QueueCommand(new PlaceWallBlueprintCommand(new TilePos(center + 2, center)));
        for (int i = 0; i < 600; i++) sim.Step(SimConstants.TickSeconds);
    }
}
