using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class RecreationSystemTests
{
    // Test pool provider — flat list, no SimRuntime hook needed.
    private static RecreationSystem.AvailableKindsProvider PoolOf(params RecreationKind[] kinds)
        => () => kinds;

    [Fact]
    public void PowerPerSec_UrIs85PercentPerHour()
    {
        Assert.Equal(0.85f / 3600f, RecreationSystem.PowerPerSec(RecreationKind.Ur), 6);
    }

    [Fact]
    public void PowerPerSec_SpectatingIsUrMinus10Percent()
    {
        Assert.Equal((0.85f - 0.10f) / 3600f, RecreationSystem.PowerPerSec(RecreationKind.Spectating), 6);
    }

    [Fact]
    public void Drain_TakesSixteenSimHoursFromFullToEmpty()
    {
        var store = new EntityStore();
        var pawn = store.CreateEntity();
        pawn.AddComponent(new RecreationNeed { Level = 1f });

        var sys = new RecreationSystem(0, PoolOf());

        // 16 sim-hours of drain at SimSecondsPerRealSecond. Step in big
        // chunks so we don't burn time on the per-tick loop.
        float realSecPerSimHour = 3600f / (float)SimRuntime.SimSecondsPerRealSecond;
        float realDt = 1f; // 1 real-second per Step
        int steps = (int)(16 * realSecPerSimHour / realDt);
        for (int i = 0; i < steps; i++) sys.Step(store, realDt);

        float lvl = pawn.GetComponent<RecreationNeed>().Level;
        Assert.True(lvl < 0.001f, $"expected near-zero, got {lvl}");
    }

    [Fact]
    public void Refill_TakesAboutOnePointOneEightHoursAtUrPower()
    {
        // Pawn engaged at Ur from 0 → 1.0. Time = 1 / 0.85 sim-hours ≈ 1.176h.
        var store = new EntityStore();
        var pawn = store.CreateEntity();
        pawn.AddComponent(new RecreationNeed { Level = 0f });
        pawn.AddComponent(new AtRecreation { BoardEntityId = 1, Kind = RecreationKind.Ur, Role = RecreationRole.Player });

        var sys = new RecreationSystem(0, PoolOf());

        float realSecPerSimHour = 3600f / (float)SimRuntime.SimSecondsPerRealSecond;
        float realDt = 1f;
        // Step enough to overshoot; clamp at 1.0 verifies.
        int steps = (int)(2.0f * realSecPerSimHour / realDt);
        for (int i = 0; i < steps; i++) sys.Step(store, realDt);

        Assert.Equal(1f, pawn.GetComponent<RecreationNeed>().Level, 4);
    }

    [Fact]
    public void Spectating_RefillsSlowerThanUr()
    {
        var store = new EntityStore();
        var ur = store.CreateEntity();
        ur.AddComponent(new RecreationNeed { Level = 0f });
        ur.AddComponent(new AtRecreation { BoardEntityId = 1, Kind = RecreationKind.Ur, Role = RecreationRole.Player });
        var spec = store.CreateEntity();
        spec.AddComponent(new RecreationNeed { Level = 0f });
        spec.AddComponent(new AtRecreation { BoardEntityId = 1, Kind = RecreationKind.Spectating, Role = RecreationRole.Spectator });

        var sys = new RecreationSystem(0, PoolOf());
        // 30 real-sec ≈ 0.5 sim-hour: Ur gains 0.425, spectating gains 0.375.
        for (int i = 0; i < 30; i++) sys.Step(store, 1f);

        float urLvl = ur.GetComponent<RecreationNeed>().Level;
        float specLvl = spec.GetComponent<RecreationNeed>().Level;
        Assert.True(urLvl > specLvl);
    }

    [Fact]
    public void Preference_NeverRolledSentinelGetsFirstRollOnStep()
    {
        var store = new EntityStore();
        var pawn = store.CreateEntity();
        pawn.AddComponent(new RecreationNeed { Level = 1f });
        pawn.AddComponent(new RecreationPreference { Kind = (RecreationKind)255, SecondsUntilRoll = 0f });

        var sys = new RecreationSystem(0, PoolOf(RecreationKind.Ur));
        sys.Step(store, 0.1f);

        Assert.Equal(RecreationKind.Ur, pawn.GetComponent<RecreationPreference>().Kind);
    }

    [Fact]
    public void Preference_EmptyPoolDefersOneSimHourRetry()
    {
        var store = new EntityStore();
        var pawn = store.CreateEntity();
        pawn.AddComponent(new RecreationNeed { Level = 1f });
        pawn.AddComponent(new RecreationPreference { Kind = (RecreationKind)255, SecondsUntilRoll = 0f });

        var sys = new RecreationSystem(0, PoolOf());
        sys.Step(store, 0.1f);

        var pref = pawn.GetComponent<RecreationPreference>();
        Assert.Equal((RecreationKind)255, pref.Kind);
        // Roughly 1 sim-hour minus the dt of this step.
        Assert.InRange(pref.SecondsUntilRoll, 3580f, 3600f);
    }

    [Fact]
    public void Preference_TwelveHourTimerCountsDown()
    {
        var store = new EntityStore();
        var pawn = store.CreateEntity();
        pawn.AddComponent(new RecreationNeed { Level = 1f });
        pawn.AddComponent(new RecreationPreference { Kind = RecreationKind.Ur, SecondsUntilRoll = RecreationSystem.PreferenceRollSec });

        var sys = new RecreationSystem(0, PoolOf(RecreationKind.Ur, RecreationKind.Spectating));
        // 60 real-sec = 1 sim-hour drain. SecondsUntilRoll should drop by 3600.
        for (int i = 0; i < 60; i++) sys.Step(store, 1f);

        var pref = pawn.GetComponent<RecreationPreference>();
        Assert.InRange(pref.SecondsUntilRoll, RecreationSystem.PreferenceRollSec - 3700f, RecreationSystem.PreferenceRollSec - 3500f);
    }
}
