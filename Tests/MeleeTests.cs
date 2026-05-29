using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class MeleeTests
{
    private static (int a, int b) TwoPawns(SimRuntime sim)
    {
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (ids.Count < 2) ids.Add(e.Id); });
        Assert.True(ids.Count >= 2, "need two pawns");
        return (ids[0], ids[1]);
    }

    [Fact]
    public void DraftedMeleeAttack_BruisesAndDownsTarget()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (attacker, target) = TwoPawns(sim);

        Assert.True(sim.Store.TryGetEntityById(attacker, out var atk));
        atk.AddComponent(new Drafted());
        sim.SetMeleeTarget(attacker, target);

        bool downed = false;
        for (int i = 0; i < 8000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (sim.Store.TryGetEntityById(target, out var t)
                && t.HasComponent<Health>() && t.GetComponent<Health>().Unconscious)
            { downed = true; break; }
        }
        Assert.True(downed, "melee should eventually down the target");

        // Every wound dealt was a bruise on an outer part (no organs hit).
        var th = sim.Store.GetEntityById(target).GetComponent<Health>();
        Assert.NotEmpty(th.Injuries!);
        foreach (var inj in th.Injuries!)
        {
            Assert.Equal(ConditionKind.Bruise, inj.Kind);
            Assert.True(BodyTree.TryGet(inj.PartId, out var def) && !def.Internal,
                $"hit an internal part: {inj.PartId}");
        }
    }

    [Fact]
    public void MeleeStops_WhenTargetDowned()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (attacker, target) = TwoPawns(sim);
        Assert.True(sim.Store.TryGetEntityById(attacker, out var atk));
        atk.AddComponent(new Drafted());

        // Pre-down the target; the attack order should clear itself.
        sim.ApplyInjury(target, "Brain", ConditionKind.Missing, 1f);
        sim.SetMeleeTarget(attacker, target);
        for (int i = 0; i < 200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.Store.TryGetEntityById(attacker, out var a2));
        Assert.False(a2.HasComponent<MeleeTarget>());
    }
}
