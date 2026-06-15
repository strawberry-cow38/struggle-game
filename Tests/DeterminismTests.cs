using System.Globalization;
using System.Text;
using StruggleGame.Sim;
using StruggleGame.Sim.Snapshots;
using Xunit;

namespace StruggleGame.Tests;

// Determinism guard. The sim must be a pure function of (seed, inputs):
// same seed + same steps => byte-identical observable state. That property
// is what makes save/load reload to the same future, makes lockstep
// multiplayer possible (send inputs, not state), and makes bug reports
// reproduce from a seed. Every RNG in the sim is already seed-derived
// (SimRuntime _spawnRng = seed+7, DummyController seed+1, Weather seed+13,
// Recreation seed+11, TileMap seed). These tests LOCK that in: if anyone
// adds a stray un-seeded `new Random()` / DateTime.Now / Guid.NewGuid /
// GD.Rand to the sim path, the fingerprint diverges and SameSeed fails.
public class DeterminismTests
{
    private const int Seed = 4242;
    private const int Dummies = 6;
    private const int Ticks = 500;

    // Build + run a fresh sim and return a fingerprint of its observable
    // state. Uses the default (synchronous) pathfinding — async pathfinding
    // is intentionally NOT enabled here, since thread timing is not part of
    // the deterministic contract.
    private static string RunFingerprint(int seed, int ticks)
    {
        var sim = new SimRuntime(seed);
        for (int i = 0; i < Dummies; i++) sim.SpawnRandomDummy();
        for (int t = 0; t < ticks; t++) sim.Step(SimConstants.TickSeconds);
        return Fingerprint(sim.BuildSnapshot());
    }

    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);

    private static string Fingerprint(SimSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append("tick=").Append(s.Tick);
        // Weather is seed-driven (WeatherSystem, seed+13).
        sb.Append("|rain=").Append(F(s.RainIntensity))
          .Append(',').Append(F(s.RainWindX)).Append(',').Append(F(s.RainWindY));
        // Pawn positions + jobs are the richest RNG-sensitive signal — they
        // come out of the seeded DummyController AI (seed+1).
        sb.Append("|dummies=");
        foreach (var d in s.Dummies)
            sb.Append('(').Append(d.EntityId).Append(',')
              .Append(F(d.X)).Append(',').Append(F(d.Y)).Append(',')
              .Append(d.Job).Append(',').Append(F(d.Facing)).Append(',')
              .Append(F(d.SleepLevel)).Append(')');
        // Trees + carrots spawn from _spawnRng (seed+7).
        sb.Append("|trees=");
        foreach (var t in s.Trees)
            sb.Append('(').Append(t.EntityId).Append(',')
              .Append(t.Tile.X).Append(',').Append(t.Tile.Y).Append(',')
              .Append(F(t.GrowthStage)).Append(')');
        sb.Append("|crops=");
        foreach (var c in s.Crops)
            sb.Append('(').Append(c.EntityId).Append(',').Append(F(c.GrowthStage)).Append(')');
        return sb.ToString();
    }

    [Fact]
    public void SameSeed_ProducesIdenticalState()
    {
        var a = RunFingerprint(Seed, Ticks);
        var b = RunFingerprint(Seed, Ticks);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeed_DivergesState()
    {
        // Guards the guard: if two different seeds produced identical runs,
        // the RNG isn't actually feeding the sim and SameSeed would pass
        // vacuously. Distinct seeds must yield distinct playthroughs.
        var a = RunFingerprint(Seed, Ticks);
        var b = RunFingerprint(Seed + 1, Ticks);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Replaying_FromSameSeed_MatchesAtEveryCheckpoint()
    {
        // Stronger than end-state equality: the two runs must agree at every
        // checkpoint along the way, so a divergence can't cancel itself out
        // before the final snapshot is taken.
        var simA = new SimRuntime(Seed);
        var simB = new SimRuntime(Seed);
        for (int i = 0; i < Dummies; i++) { simA.SpawnRandomDummy(); simB.SpawnRandomDummy(); }
        for (int t = 0; t < Ticks; t++)
        {
            simA.Step(SimConstants.TickSeconds);
            simB.Step(SimConstants.TickSeconds);
            if (t % 50 == 0)
                Assert.Equal(Fingerprint(simA.BuildSnapshot()), Fingerprint(simB.BuildSnapshot()));
        }
    }
}
