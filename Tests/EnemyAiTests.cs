using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;
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

    // Park every colonist in the far corner so a test raider never perceives
    // one (keeps the mission running uninterrupted by the Engage reflex).
    private static void StashColonistsFarAway(SimRuntime sim)
    {
        int far = SimConstants.MapSize - 2;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity e) =>
        {
            if (!e.HasComponent<Enemy>()) { p.X = far + 0.5f; p.Y = far + 0.5f; }
        });
    }

    [Fact]
    public void Enemy_MissionAdvancesThroughHoldToExfil()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        StashColonistsFarAway(sim);

        int c = SimConstants.MapSize / 2;
        var mission = new System.Collections.Generic.List<EnemyObjective>
        {
            new EnemyObjective(EnemyObjectiveKind.AdvanceTo, c, c, 0),
            new EnemyObjective(EnemyObjectiveKind.Hold, c, c, 20),
            new EnemyObjective(EnemyObjectiveKind.Exfil, 0, 0, 0),
        };
        var enemy = sim.SpawnEnemy(c, c, mission);
        SetPos(sim, enemy.Id, c + 0.5f, c + 0.5f);

        // Spawned on the AdvanceTo tile → it completes immediately and the brain
        // settles into the Hold step.
        for (int i = 0; i < 20; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(EnemyGoalKind.Hold, enemy.GetComponent<EnemyBrain>().Goal);

        // Once the hold duration elapses the queue advances to Exfil.
        for (int i = 0; i < 60; i++) sim.Step(SimConstants.TickSeconds);
        ref var brain = ref enemy.GetComponent<EnemyBrain>();
        Assert.Equal(EnemyGoalKind.Exfil, brain.Goal);
        Assert.Equal(2, brain.MissionIndex);
    }

    [Fact]
    public void Enemy_ExfilReachesEdgeAndDespawns()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        StashColonistsFarAway(sim);

        var mission = new System.Collections.Generic.List<EnemyObjective>
        {
            new EnemyObjective(EnemyObjectiveKind.Exfil, 0, 0, 0),
        };
        var enemy = sim.SpawnEnemy(4, 4, mission);
        int id = enemy.Id;
        SetPos(sim, id, 4.5f, 4.5f);

        // It heads for the nearest edge and despawns on the perimeter.
        bool gone = false;
        for (int i = 0; i < 400 && !gone; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            gone = !sim.Store.TryGetEntityById(id, out _);
        }
        Assert.True(gone, "exfil should have walked the raider off the edge + despawned it");
    }

    [Fact]
    public void Enemy_NoMission_FallsBackToAdvance()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        StashColonistsFarAway(sim);

        var enemy = sim.SpawnEnemy(30, 30); // null mission
        for (int i = 0; i < 20; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(EnemyGoalKind.Advance, enemy.GetComponent<EnemyBrain>().Goal);
    }

    [Fact]
    public void Raid_AssaultSteersTowardColonyCentroid()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        // Cluster every colonist at a known far point so the centroid is fixed.
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos p, ref Wanderer _, Entity e) =>
        {
            if (!e.HasComponent<Enemy>()) { p.X = 200.5f; p.Y = 200.5f; }
        });

        var raider = sim.SpawnEnemy(20, 20, SimRuntime.RaidMission());
        SetPos(sim, raider.Id, 20.5f, 20.5f);

        for (int i = 0; i < 20; i++) sim.Step(SimConstants.TickSeconds);

        ref var brain = ref raider.GetComponent<EnemyBrain>();
        // Far out of sight (centroid ~254 tiles away) → not Engage; the Assault
        // goal steers toward the colony centroid (~200,200), NOT map centre 128.
        Assert.Equal(EnemyGoalKind.Assault, brain.Goal);
        Assert.True(brain.HasGoalTile);
        Assert.True(System.Math.Abs(brain.GoalTileX - 200) <= 5 && System.Math.Abs(brain.GoalTileY - 200) <= 5,
            $"assault goal tile {brain.GoalTileX},{brain.GoalTileY} should aim at the colony centroid ~200,200");
    }

    [Fact]
    public void Raid_SpawnsGroupAndRaisesDismissableNotification()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        int before = sim.Store.Query<Enemy>().Count;
        sim.SpawnRaid(4);
        Assert.Equal(before + 4, sim.Store.Query<Enemy>().Count);

        // The raid raises exactly one notification, carried on the snapshot.
        var snap = sim.BuildSnapshot();
        int notes = 0, id = 0;
        foreach (var note in snap.Notifications) { notes++; id = note.Id; }
        Assert.Equal(1, notes);
        Assert.True(id != 0);

        // Dismissing it clears it from subsequent snapshots.
        sim.DismissNotification(id);
        var snap2 = sim.BuildSnapshot();
        int notes2 = 0;
        foreach (var _ in snap2.Notifications) notes2++;
        Assert.Equal(0, notes2);
    }

    [Fact]
    public void Enemy_DoesNotPerceiveColonistThroughWall()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        int colonist = FirstColonist(sim);
        sim.Store.GetEntityById(colonist).AddComponent(new Drafted());
        SetPos(sim, colonist, 20.5f, 20.5f);

        // Wall column between the (well-within-sight) colonist and the enemy.
        for (int y = 18; y <= 22; y++) sim.InstantPlaceWall(new TilePos(25, y));

        var enemy = sim.SpawnEnemy(30, 20);
        SetPos(sim, enemy.Id, 30.5f, 20.5f);

        for (int i = 0; i < 16; i++) sim.Step(SimConstants.TickSeconds);
        // No line of sight → no acquisition (no ESP through the wall).
        Assert.Equal(0, enemy.GetComponent<EnemyBrain>().TargetEntityId);
        Assert.NotEqual(EnemyGoalKind.Engage, enemy.GetComponent<EnemyBrain>().Goal);
    }

    [Fact]
    public void Enemy_HuntsLastSeenTileAfterLosingSight()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        int colonist = FirstColonist(sim);
        sim.Store.GetEntityById(colonist).AddComponent(new Drafted());
        SetPos(sim, colonist, 20.5f, 20.5f);

        var enemy = sim.SpawnEnemy(27, 20);
        SetPos(sim, enemy.Id, 27.5f, 20.5f);

        // Acquire in the open, recording the last-seen tile (~20,20).
        for (int i = 0; i < 16; i++) sim.Step(SimConstants.TickSeconds);
        Assert.Equal(EnemyGoalKind.Engage, enemy.GetComponent<EnemyBrain>().Goal);

        // Yank the target far out of sight — the enemy should push to where it
        // last saw them rather than instantly forgetting.
        SetPos(sim, colonist, 20.5f, 200.5f);
        for (int i = 0; i < 16; i++) sim.Step(SimConstants.TickSeconds);

        ref var brain = ref enemy.GetComponent<EnemyBrain>();
        Assert.Equal(EnemyGoalKind.Hunt, brain.Goal);
        Assert.True(System.Math.Abs(brain.GoalTileX - 20) <= 3 && System.Math.Abs(brain.GoalTileY - 20) <= 3,
            $"hunt goal tile {brain.GoalTileX},{brain.GoalTileY} should be the last-seen spot ~20,20");
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
