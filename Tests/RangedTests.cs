using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class RangedTests
{
    private static (int a, int b) TwoPawns(SimRuntime sim)
    {
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (ids.Count < 2) ids.Add(e.Id); });
        Assert.True(ids.Count >= 2, "need two pawns");
        return (ids[0], ids[1]);
    }

    private static void SetPos(SimRuntime sim, int id, float x, float y)
    {
        ref var wp = ref sim.Store.GetEntityById(id).GetComponent<WorldPos>();
        wp.X = x; wp.Y = y;
    }

    private static void ArmWithRifle(SimRuntime sim, int id, ItemDef ammo, int ammoCount)
    {
        var e = sim.Store.GetEntityById(id);
        e.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>
            {
                new InventoryStack { ItemPath = ammo.FullPath, Count = ammoCount },
            },
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = ItemCatalog.AssaultRifle.FullPath, Count = 1 },
            },
        });
    }

    [Fact]
    public void DraftedRangedAttack_WoundsTargetWithGunshot()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);

        // Both drafted so neither wanders off; placed 4 tiles apart in the open.
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoHp, 60);

        // Let the RangedCombat component attach, then lock positions + fire.
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        SetPos(sim, shooter, 20.5f, 20.5f);
        SetPos(sim, target, 24.5f, 20.5f);

        Assert.True(sim.Store.GetEntityById(shooter).HasComponent<RangedCombat>(),
            "equipping a ranged weapon should attach RangedCombat");

        sim.SetFireTarget(shooter, target);

        bool gunshot = false;
        bool sawProjectile = false;
        for (int i = 0; i < 3000; i++)
        {
            // Keep them pinned (drafted idle would otherwise micro-adjust).
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, target, 24.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);

            int proj = 0;
            sim.Store.Query<Projectile>().ForEachEntity((ref Projectile _, Entity _) => proj++);
            if (proj > 0) sawProjectile = true;

            if (sim.Store.TryGetEntityById(target, out var t) && t.HasComponent<Health>())
            {
                var inj = t.GetComponent<Health>().Injuries;
                if (inj is not null)
                    foreach (var w in inj)
                        if (w.Kind == ConditionKind.Gunshot) { gunshot = true; break; }
            }
            if (gunshot) break;
        }

        Assert.True(sawProjectile, "firing should spawn at least one projectile");
        Assert.True(gunshot, "a hit should leave a Gunshot wound on the target");
    }

    [Fact]
    public void RunningDry_ReloadsFromInventoryAmmo()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);

        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        // Mag holds 30; give 45 so a reload must pull the spare 15.
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoHp, 45);

        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        sim.SetFireTarget(shooter, target);

        int minInvSeen = int.MaxValue;
        for (int i = 0; i < 6000; i++)
        {
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, target, 24.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);

            if (!sim.Store.TryGetEntityById(target, out var t) || !t.HasComponent<Wanderer>())
                break; // target killed — plenty of rounds were fired
            var inv = sim.Store.GetEntityById(shooter).GetComponent<Inventory>();
            int ammo = 0;
            if (inv.Items is not null)
                foreach (var s in inv.Items) ammo += s.Count;
            minInvSeen = System.Math.Min(minInvSeen, ammo);
        }

        // Inventory ammo must have dropped below the 30-round mag size,
        // proving at least one reload drew spare rounds from inventory.
        Assert.True(minInvSeen < 30, $"expected a reload to draw spare ammo (min inv seen={minInvSeen})");
    }
}
