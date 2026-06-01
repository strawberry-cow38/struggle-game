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

        // Identical straight 15-tile walks east.
        sim.QueueCommand(new IssueMoveOrderCommand(crowded, new TilePos(35, 20), false));
        sim.QueueCommand(new IssueMoveOrderCommand(solo, new TilePos(75, 20), false));

        for (int i = 0; i < 18; i++) sim.Step(SimConstants.TickSeconds);

        float crowdedTravel = sim.Store.GetEntityById(crowded).GetComponent<WorldPos>().X - 20.5f;
        float soloTravel = sim.Store.GetEntityById(solo).GetComponent<WorldPos>().X - 60.5f;

        Assert.True(soloTravel > 0f, "solo pawn should have moved");
        Assert.True(crowdedTravel < soloTravel - 0.3f,
            $"crowded pawn ({crowdedTravel:0.00}) should lag the solo pawn ({soloTravel:0.00}) leaving the shared tile");
    }
}
