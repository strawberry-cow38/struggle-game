using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class EnemyAiTests
{
    private static int FirstColonist(SimRuntime sim)
    {
        int id = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        {
            if (id == 0 && !e.HasComponent<Enemy>()) id = e.Id;
        });
        Assert.True(id != 0, "need a colonist");
        return id;
    }

    private static void SetPos(SimRuntime sim, int id, float x, float y)
    {
        ref var wp = ref sim.Store.GetEntityById(id).GetComponent<WorldPos>();
        wp.X = x; wp.Y = y;
    }

    [Fact]
    public void Enemy_AcquiresAndWoundsColonist()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds); // settle the starting colonists

        // Draft a colonist so it holds still (no wandering out of the fight),
        // and plant it on known-open ground (same region RangedTests uses).
        int colonist = FirstColonist(sim);
        var ce = sim.Store.GetEntityById(colonist);
        ce.AddComponent(new Drafted());
        SetPos(sim, colonist, 20.5f, 20.5f);

        // Hostile 7 tiles away in the open — within sight + rifle range, clear LoS.
        var enemy = sim.SpawnEnemy(27, 20);
        SetPos(sim, enemy.Id, 27.5f, 20.5f);

        // Acquisition: the brain locks onto the colonist on its first think
        // (before any rounds land + down it).
        for (int i = 0; i < 5; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(colonist, enemy.GetComponent<EnemyBrain>().TargetEntityId);
        Assert.Equal(EnemyGoalKind.Engage, enemy.GetComponent<EnemyBrain>().Goal);

        // Engagement: over a few seconds of auto fire it wounds (and likely
        // downs) the colonist.
        for (int i = 0; i < 360; i++) sim.Step(SimConstants.TickSeconds);

        bool killed = !sim.Store.TryGetEntityById(colonist, out var live);
        bool wounded = !killed && (live.GetComponent<Health>().Injuries?.Count ?? 0) > 0;
        Assert.True(killed || wounded, "enemy should have engaged + wounded the colonist");
    }

    [Fact]
    public void Enemy_BuildSnapshotDoesNotThrowAndFlagsEnemy()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var enemy = sim.SpawnEnemy(30, 30);

        // Enemies lack WorkPriorities/Schedule/needs; the snapshot must not
        // try to lazily add them inside its query loop (regression: that
        // threw StructuralChangeException, only reachable via BuildSnapshot
        // which the sim-only tests never call).
        var snap = sim.BuildSnapshot();

        bool found = false;
        foreach (var d in snap.Dummies)
            if (d.EntityId == enemy.Id) { found = true; Assert.True(d.IsEnemy); }
        Assert.True(found, "enemy should appear in the snapshot, flagged IsEnemy");
    }

    [Fact]
    public void Enemy_RetreatsWhenHurt()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        int colonist = FirstColonist(sim);
        SetPos(sim, colonist, 20.5f, 20.5f);

        var enemy = sim.SpawnEnemy(27, 20);
        SetPos(sim, enemy.Id, 27.5f, 20.5f);

        // Cripple it: blood just under the retreat threshold (but high enough
        // to stay conscious), so the next think flips the goal to Retreat.
        ref var h = ref enemy.GetComponent<Health>();
        h.BloodLevel = 0.42f;

        for (int i = 0; i < 60; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(EnemyGoalKind.Retreat, enemy.GetComponent<EnemyBrain>().Goal);
    }
}
