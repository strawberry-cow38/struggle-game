using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class MeleeTests
{
    private static void DownByPain(SimRuntime sim, int id)
    {
        foreach (var part in new[] { "ArmL", "ArmR", "LegL", "LegR", "Torso", "Head" })
            sim.ApplyInjury(id, part, ConditionKind.Bruise, 1f);
    }

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
    public void DownedColonist_LosesQueuedOrders_KeepsDraft()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (attacker, target) = TwoPawns(sim);
        Assert.True(sim.Store.TryGetEntityById(attacker, out var atk));
        atk.AddComponent(new Drafted());
        sim.SetMeleeTarget(attacker, target);

        // Knock the attacker out (non-lethally).
        DownByPain(sim, attacker);
        for (int i = 0; i < 120; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.Store.TryGetEntityById(attacker, out var a2));
        Assert.True(a2.GetComponent<Health>().Unconscious);
        Assert.False(a2.HasComponent<MeleeTarget>()); // order killed
        Assert.True(a2.HasComponent<Drafted>());       // draft kept
    }

    [Fact]
    public void ArmedMelee_DealsWeaponAttacks_NotBruises()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (attacker, target) = TwoPawns(sim);
        Assert.True(sim.Store.TryGetEntityById(attacker, out var atk));
        atk.AddComponent(new Drafted());
        // Equip the trinket weapon on the attacker.
        atk.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>(),
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = StruggleGame.Sim.Items.ItemCatalog.WoodenTrinket.FullPath, Count = 1 },
            },
        });
        sim.SetMeleeTarget(attacker, target);

        for (int i = 0; i < 6000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (sim.Store.GetEntityById(target).GetComponent<Health>().Unconscious) break;
        }

        var th = sim.Store.GetEntityById(target).GetComponent<Health>();
        Assert.NotEmpty(th.Injuries!);
        // Every wound is a weapon attack (Cut or Stab), never a fist bruise.
        foreach (var inj in th.Injuries!)
            Assert.True(inj.Kind == ConditionKind.Cut || inj.Kind == ConditionKind.Stab,
                $"unexpected armed-hit kind: {inj.Kind}");
    }

    [Fact]
    public void MeleeStops_WhenConsciousTargetGoesDown()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (attacker, target) = TwoPawns(sim);
        Assert.True(sim.Store.TryGetEntityById(attacker, out var atk));
        atk.AddComponent(new Drafted());

        // Ordered on a CONSCIOUS target — not a finish-off — so it stops
        // when the target drops (downed by pain here, still alive).
        sim.SetMeleeTarget(attacker, target);
        DownByPain(sim, target);
        for (int i = 0; i < 200; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.Store.TryGetEntityById(attacker, out var a2));
        Assert.False(a2.HasComponent<MeleeTarget>()); // attack stopped
        Assert.True(sim.Store.TryGetEntityById(target, out var t) && t.HasComponent<Wanderer>(),
            "target should still be alive (downed, not killed)"); // not a finish-off → not dead
    }

    [Fact]
    public void FinishOff_KillsDownedTarget()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (attacker, target) = TwoPawns(sim);
        Assert.True(sim.Store.TryGetEntityById(attacker, out var atk));
        atk.AddComponent(new Drafted());
        atk.AddComponent(new Inventory // armed so the strikes bleed the target out
        {
            Items = new List<InventoryStack>(),
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = StruggleGame.Sim.Items.ItemCatalog.WoodenTrinket.FullPath, Count = 1 },
            },
        });

        DownByPain(sim, target);            // target downed, alive
        sim.SetMeleeTarget(attacker, target); // ordered on a downed target → finish-off

        bool dead = false;
        for (int i = 0; i < 12000; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (!sim.Store.TryGetEntityById(target, out var t) || !t.HasComponent<Wanderer>()) { dead = true; break; }
        }
        Assert.True(dead, "finishing attack should kill the downed target");
        int corpses = 0;
        sim.Store.Query<Corpse>().ForEachEntity((ref Corpse _, Entity _) => corpses++);
        Assert.Equal(1, corpses);
    }
}
