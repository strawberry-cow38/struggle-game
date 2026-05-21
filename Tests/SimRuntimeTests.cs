using StruggleGame.Sim;
using Xunit;

namespace StruggleGame.Tests;

public class SimRuntimeTests
{
    [Fact]
    public void Step_IncrementsTick()
    {
        var sim = new SimRuntime();
        Assert.Equal(0, sim.Tick);

        sim.Step();
        sim.Step();
        sim.Step();

        Assert.Equal(3, sim.Tick);
    }

    [Fact]
    public void TileMeters_IsOnePointFive()
    {
        Assert.Equal(1.5f, SimConstants.TileMeters);
    }
}
