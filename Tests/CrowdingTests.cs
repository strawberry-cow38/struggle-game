using System.Collections.Generic;
using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

// A pawn squeezing through a tile another pawn occupies walks slower
// (CrowdedSpeedFactor) until it's clear — pure movement-step friction, no
// blocking, no pathfinding involvement.
public class CrowdingTests
{
    private static List<int> Colonists(SimRuntime sim)
    {
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        {
            if (!e.HasComponent<Enemy>()) ids.Add(e.Id);
        });
        return ids;
    }

    private static void SetPos(SimRuntime sim, int id, float x, float y)
    {
        ref var wp = ref sim.Store.GetEntityById(id).GetComponent<WorldPos>();
        wp.X = x; wp.Y = y;
    }

    // Park a pawn for real: drop any stale (e.g. wander) path AND its in-flight
    // path request so it doesn't re-acquire one and walk off.
    private static void Freeze(SimRuntime sim, int id)
    {
        ref var pf = ref sim.Store.GetEntityById(id).GetComponent<PathFollower>();
        pf.Waypoints = null; pf.Index = 0; pf.PendingPathId = 0;
    }

    [Fact]
    public void CrowdedPawn_MovesSlowerThanSoloPawn()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        var ids = Colonists(sim);
        Assert.True(ids.Count >= 3, "need three colonists");
        int blocker = ids[0], crowded = ids[1], solo = ids[2];

        // Draft everyone so nobody wanders; park any extras out of the way.
        foreach (int id in ids) sim.Store.GetEntityById(id).AddComponent(new Drafted());
        for (int i = 3; i < ids.Count; i++) SetPos(sim, ids[i], 250.5f, 250.5f);

        // crowded starts stacked on the (stationary) blocker; solo starts alone.
        SetPos(sim, blocker, 20.5f, 20.5f);
        SetPos(sim, crowded, 20.5f, 20.5f);
        SetPos(sim, solo, 60.5f, 20.5f);
        Freeze(sim, blocker); // truly stationary — no stale wander path

        // Identical long straight walks east (far enough neither arrives).
        sim.QueueCommand(new IssueMoveOrderCommand(crowded, new TilePos(120, 20), false));
        sim.QueueCommand(new IssueMoveOrderCommand(solo, new TilePos(160, 20), false));

        for (int i = 0; i < 60; i++) sim.Step(SimConstants.TickSeconds);

        float crowdedTravel = sim.Store.GetEntityById(crowded).GetComponent<WorldPos>().X - 20.5f;
        float soloTravel = sim.Store.GetEntityById(solo).GetComponent<WorldPos>().X - 60.5f;

        Assert.True(soloTravel > 0f, "solo pawn should have moved");
        Assert.True(crowdedTravel < soloTravel - 0.3f,
            $"crowded pawn ({crowdedTravel:0.00}) should lag the solo pawn ({soloTravel:0.00}) leaving the shared tile");
    }

    [Fact]
    public void TwoMoversSharingATile_OnlyOneSlows_SoTheySeparate()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        var ids = Colonists(sim);
        Assert.True(ids.Count >= 2, "need two colonists");
        int a = ids[0], b = ids[1];
        foreach (int id in ids) sim.Store.GetEntityById(id).AddComponent(new Drafted());
        for (int i = 2; i < ids.Count; i++) SetPos(sim, ids[i], 250.5f, 250.5f);

        // Both start stacked on the same tile, ordered the same way.
        SetPos(sim, a, 20.5f, 20.5f);
        SetPos(sim, b, 20.5f, 20.5f);
        sim.QueueCommand(new IssueMoveOrderCommand(a, new TilePos(120, 20), false));
        sim.QueueCommand(new IssueMoveOrderCommand(b, new TilePos(120, 20), false));

        for (int i = 0; i < 60; i++) sim.Step(SimConstants.TickSeconds);

        float ax = sim.Store.GetEntityById(a).GetComponent<WorldPos>().X;
        float bx = sim.Store.GetEntityById(b).GetComponent<WorldPos>().X;
        // Exactly one slowed → they pull apart instead of marching in lockstep
        // (which is what would re-trigger overlap→separate→overlap).
        Assert.True(System.Math.Abs(ax - bx) > 0.4f,
            $"two stacked movers should separate (one slows), but x's were {ax:0.00} and {bx:0.00}");
    }
}
