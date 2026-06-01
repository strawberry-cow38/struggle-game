using System.Collections.Generic;
using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

// The local "excuse me" de-stack yield only applies to pawns IN MOTION. A
// stationary pawn (or a downed body) must never block movement, or a move
// order onto its tile would stall forever. This also guards against the yield
// deadlocking ordinary movement.
public class MovementYieldTests
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
    public void Yield_DoesNotBlockMovingOntoAStationaryPawn()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);

        var ids = Colonists(sim);
        Assert.True(ids.Count >= 2, "need two colonists");
        int mover = ids[0], blocker = ids[1];

        // Draft both so neither wanders; park the blocker on the destination
        // tile (stationary, no path) and the mover a few tiles away.
        sim.Store.GetEntityById(mover).AddComponent(new Drafted());
        sim.Store.GetEntityById(blocker).AddComponent(new Drafted());
        SetPos(sim, blocker, 20.5f, 20.5f);
        SetPos(sim, mover, 25.5f, 20.5f);

        sim.QueueCommand(new IssueMoveOrderCommand(mover, new TilePos(20, 20), false));

        for (int i = 0; i < 240; i++) sim.Step(SimConstants.TickSeconds);

        ref var mp = ref sim.Store.GetEntityById(mover).GetComponent<WorldPos>();
        float dx = mp.X - 20.5f, dy = mp.Y - 20.5f;
        Assert.True(dx * dx + dy * dy <= 1.5f * 1.5f,
            $"mover stalled at {mp.X:0.0},{mp.Y:0.0} — stationary pawn should not block movement onto its tile");
    }
}
