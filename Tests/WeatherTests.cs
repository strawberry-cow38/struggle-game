using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class WeatherTests
{
    // One world-hour of weather at the canonical 1-world-second step the
    // sim feeds it (TickSeconds * SimSecondsPerRealSecond = 1.0).
    private static void StepHours(WeatherSystem w, double hours)
    {
        int steps = (int)(hours * 3600);
        for (int i = 0; i < steps; i++) w.Step(1f);
    }

    [Fact]
    public void SameSeed_SameTrajectory()
    {
        var a = new WeatherSystem(42);
        var b = new WeatherSystem(42);
        for (int i = 0; i < 6 * 3600; i++)
        {
            a.Step(1f);
            b.Step(1f);
            Assert.Equal(a.RainIntensity, b.RainIntensity);
            Assert.Equal(a.WindX, b.WindX);
            Assert.Equal(a.WindY, b.WindY);
        }
    }

    [Fact]
    public void Intensity_StaysInRange()
    {
        var w = new WeatherSystem(7);
        for (int i = 0; i < 48 * 3600; i++)
        {
            w.Step(1f);
            Assert.InRange(w.RainIntensity, 0f, 1f);
        }
    }

    [Fact]
    public void Walk_EventuallyRains_AndClearsAgain()
    {
        var w = new WeatherSystem(1337);
        bool rained = false, clearedAfterRain = false;
        // Worst-case clear stretch is 2 world-hours, fronts hold under an
        // hour — 48 world-hours is plenty to see at least one full cycle.
        for (int i = 0; i < 48 * 3600; i++)
        {
            w.Step(1f);
            if (w.RainIntensity > 0.15f) rained = true;
            else if (rained && w.RainIntensity <= 0f) clearedAfterRain = true;
        }
        Assert.True(rained);
        Assert.True(clearedAfterRain);
    }

    [Fact]
    public void Override_PinsIntensity_AndWind()
    {
        var w = new WeatherSystem(5);
        w.SetOverride(1f, windX: 4.5f, windY: 0f);
        StepHours(w, 3);
        Assert.Equal(1f, w.RainIntensity);
        Assert.Equal(4.5f, w.WindX);
        Assert.Equal(0f, w.WindY);
    }

    [Fact]
    public void ClearOverride_ReturnsToWalk()
    {
        var w = new WeatherSystem(5);
        w.SetOverride(1f);
        StepHours(w, 1);
        w.ClearOverride();
        // The walk ramps from the pinned value toward its own target;
        // a forced storm released during a clear phase must drain off.
        StepHours(w, 6);
        Assert.NotEqual(1f, w.RainIntensity);
    }

    [Fact]
    public void Storm_BlowsHarderThanClear()
    {
        var clear = new WeatherSystem(9);
        var storm = new WeatherSystem(9);
        clear.SetOverride(0f);
        storm.SetOverride(1f);
        StepHours(clear, 1);
        StepHours(storm, 1);
        float clearMag = MathF.Sqrt(clear.WindX * clear.WindX + clear.WindY * clear.WindY);
        float stormMag = MathF.Sqrt(storm.WindX * storm.WindX + storm.WindY * storm.WindY);
        Assert.True(stormMag > clearMag);
    }
}
