using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
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

    private static void DownByPain(SimRuntime sim, int id)
    {
        foreach (var part in new[] { "ArmL", "ArmR", "LegL", "LegR", "Torso", "Head" })
            sim.ApplyInjury(id, part, ConditionKind.Bruise, 14f);
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
    public void ReloadCancelledByMoveOrder_GrantsNoAmmo()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 60);
        sim.Step(SimConstants.TickSeconds); sim.Step(SimConstants.TickSeconds);
        SetPos(sim, shooter, 20.5f, 20.5f); SetPos(sim, target, 24.5f, 20.5f);
        sim.SetFireTarget(shooter, target);

        // Mag starts empty, so the shooter reloads before it can fire — catch it
        // mid-reload (the rifle's reload is 120 ticks).
        var e = sim.Store.GetEntityById(shooter);
        for (int i = 0; i < 60 && !e.GetComponent<RangedCombat>().Reloading; i++)
        {
            SetPos(sim, shooter, 20.5f, 20.5f); SetPos(sim, target, 24.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
        }
        Assert.True(e.GetComponent<RangedCombat>().Reloading, "shooter should be mid-reload");
        Assert.Equal(0, e.GetComponent<RangedCombat>().MagCount);
        int ammoBefore = InvCount(e, ItemCatalog.RifleAmmoFmj.FullPath);

        // Interrupt with a fresh move order — reload aborts, and because the
        // rounds only load on completion, the mag stays empty + ammo untouched
        // (no free instant reload from interrupting).
        sim.QueueCommand(new IssueMoveOrderCommand(shooter, new TilePos(30, 20), false));
        sim.Step(SimConstants.TickSeconds);

        Assert.False(e.GetComponent<RangedCombat>().Reloading, "move order should cancel the reload");
        Assert.Equal(0, e.GetComponent<RangedCombat>().MagCount);
        Assert.Equal(ammoBefore, InvCount(e, ItemCatalog.RifleAmmoFmj.FullPath));
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
                        // A gunshot wound, or a part the round shot clean off.
                        if (w.Kind == ConditionKind.Gunshot || w.Kind == ConditionKind.Missing) { gunshot = true; break; }
            }
            if (gunshot) break;
        }

        Assert.True(sawProjectile, "firing should spawn at least one projectile");
        Assert.True(gunshot, "a hit should leave a Gunshot wound on the target");
    }

    [Fact]
    public void AntiFriendlyFire_PawnInShooter3x3IsNotHit()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (ids.Count < 3) ids.Add(e.Id); });
        Assert.True(ids.Count >= 3, "need three pawns");
        int shooter = ids[0], bystander = ids[1], target = ids[2];
        foreach (var id in ids) sim.Store.GetEntityById(id).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 200);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        sim.SetFireTarget(shooter, target);

        bool targetHit = false;
        for (int i = 0; i < 1500; i++)
        {
            // Bystander hugs the shooter (in its 3x3) directly on the line to
            // the target — must never eat a round; target sits well outside.
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, bystander, 21.5f, 20.5f);
            SetPos(sim, target, 27.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
            if (sim.Store.GetEntityById(target).GetComponent<Health>().Injuries is { Count: > 0 }) targetHit = true;
        }

        int bystanderInjuries = sim.Store.GetEntityById(bystander).GetComponent<Health>().Injuries?.Count ?? 0;
        Assert.Equal(0, bystanderInjuries); // in the shooter's 3x3 → immune to its fire
        Assert.True(targetHit, "the round should clear the cluster + still hit the target beyond it");
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

    [Fact]
    public void PreferredAmmo_LoadsChosenType_AndUnloadReturnsRounds()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, _) = TwoPawns(sim);
        var e = sim.Store.GetEntityById(shooter);
        e.AddComponent(new Drafted());
        e.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>
            {
                new InventoryStack { ItemPath = ItemCatalog.RifleAmmoFmj.FullPath, Count = 30 },
                new InventoryStack { ItemPath = ItemCatalog.RifleAmmoHp.FullPath, Count = 30 },
            },
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = ItemCatalog.AssaultRifle.FullPath, Count = 1 },
            },
        });
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        Assert.True(e.HasComponent<RangedCombat>());

        // Lock to HP + force-reload. Two-phase: the rounds only load when the
        // reload COMPLETES (~120 ticks), so step it out, then mag fills with HP,
        // HP inventory empties, FMJ is untouched.
        sim.SetPreferredAmmoAndReload(shooter, ItemCatalog.RifleAmmoHp.FullPath);
        for (int i = 0; i < 160; i++) sim.Step(SimConstants.TickSeconds);
        var rc = e.GetComponent<RangedCombat>();
        Assert.Equal(ItemCatalog.RifleAmmoHp.FullPath, rc.LoadedAmmoPath);
        Assert.Equal(30, rc.MagCount);
        Assert.Equal(30, InvCount(e, ItemCatalog.RifleAmmoFmj.FullPath));
        Assert.Equal(0, InvCount(e, ItemCatalog.RifleAmmoHp.FullPath));

        // Unload returns the 30 HP rounds to inventory and empties the mag.
        sim.UnloadMagazine(shooter);
        Assert.Equal(0, e.GetComponent<RangedCombat>().MagCount);
        Assert.Equal(30, InvCount(e, ItemCatalog.RifleAmmoHp.FullPath));
    }

    [Fact]
    public void RangedTargeting_FinishesOff_UnconsciousTarget()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoHp, 90);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);

        // Knock the target out non-lethally (pain shock from bruises).
        foreach (var part in new[] { "ArmL", "ArmR", "LegL", "LegR", "Torso", "Head" })
            sim.ApplyInjury(target, part, ConditionKind.Bruise, 14f);
        for (int i = 0; i < 120; i++) sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.Store.GetEntityById(target).GetComponent<Health>().Unconscious,
            "target should be downed before the fire order");

        // Ordering fire on a downed pawn is allowed and runs until it dies.
        sim.SetFireTarget(shooter, target);
        Assert.True(sim.Store.GetEntityById(shooter).GetComponent<RangedCombat>().TargetEntityId == target,
            "fire order should stick on an unconscious target");

        bool dead = false;
        for (int i = 0; i < 8000; i++)
        {
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, target, 24.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
            if (!sim.Store.TryGetEntityById(target, out var t) || !t.HasComponent<Wanderer>()) { dead = true; break; }
        }
        Assert.True(dead, "ranged fire should finish off the downed target");
    }

    [Fact]
    public void RangedStops_WhenConsciousTargetGoesDown()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoHp, 90);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);

        // Ordered on a CONSCIOUS target (not a finish-off), then it drops.
        sim.SetFireTarget(shooter, target);
        Assert.False(sim.Store.GetEntityById(shooter).GetComponent<RangedCombat>().FinishOff);
        DownByPain(sim, target);
        for (int i = 0; i < 200; i++) sim.Step(SimConstants.TickSeconds);

        // Fire stopped when it went down; target is still alive (downed).
        Assert.Equal(0, sim.Store.GetEntityById(shooter).GetComponent<RangedCombat>().TargetEntityId);
        Assert.True(sim.Store.TryGetEntityById(target, out var t) && t.HasComponent<Wanderer>(),
            "downed-but-alive target should not have been finished off");
    }

    [Fact]
    public void UndraftedPawn_TopsOffMagFromInventory()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, _) = TwoPawns(sim);
        // Equipped rifle + spare ammo, but NOT drafted and mag starts empty.
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 60);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        var e = sim.Store.GetEntityById(shooter);
        Assert.True(e.HasComponent<RangedCombat>());

        // Left to its own devices, an undrafted pawn reloads from inventory.
        for (int i = 0; i < 800; i++)
        {
            sim.Step(SimConstants.TickSeconds);
            if (e.GetComponent<RangedCombat>().MagCount >= 30) break;
        }
        Assert.Equal(30, e.GetComponent<RangedCombat>().MagCount);
        Assert.Equal(30, InvCount(e, ItemCatalog.RifleAmmoFmj.FullPath)); // 60 - 30 loaded
    }

    [Fact]
    public void UndraftedPawn_FetchesAmmoFromPile_WhenInventoryEmpty()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, _) = TwoPawns(sim);
        // Rifle equipped, NO ammo carried, not drafted.
        var e = sim.Store.GetEntityById(shooter);
        e.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>(),
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = ItemCatalog.AssaultRifle.FullPath, Count = 1 },
            },
        });
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);

        // Drop an ammo pile on the pawn's tile so the fetch needs no pathing.
        SetPos(sim, shooter, 40.5f, 40.5f);
        sim.SpawnItemPile(new StruggleGame.Sim.Map.TilePos(40, 40), ItemCatalog.RifleAmmoFmj.FullPath, 100);

        bool reloaded = false;
        for (int i = 0; i < 1200; i++)
        {
            SetPos(sim, shooter, 40.5f, 40.5f);
            sim.Step(SimConstants.TickSeconds);
            if (e.GetComponent<RangedCombat>().MagCount > 0) { reloaded = true; break; }
        }
        Assert.True(reloaded, "undrafted pawn should fetch ammo from the pile and reload");
    }

    [Fact]
    public void BulletHitsBystanderInTheLine_FriendlyFire()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) => ids.Add(e.Id));
        Assert.True(ids.Count >= 3, "need three pawns");
        int shooter = ids[0], bystander = ids[1], target = ids[2];
        foreach (var id in new[] { shooter, bystander, target })
            sim.Store.GetEntityById(id).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 90);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);

        // Line them up: shooter — bystander — target, all on one row.
        void Place()
        {
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, bystander, 23.5f, 20.5f);
            SetPos(sim, target, 28.5f, 20.5f);
        }
        Place();
        sim.SetFireTarget(shooter, target);

        bool bystanderHit = false;
        for (int i = 0; i < 3000 && !bystanderHit; i++)
        {
            Place();
            sim.Step(SimConstants.TickSeconds);
            if (sim.Store.TryGetEntityById(bystander, out var b) && b.HasComponent<Health>())
            {
                var inj = b.GetComponent<Health>().Injuries;
                if (inj is not null)
                    foreach (var w in inj)
                        if (w.Kind == ConditionKind.Gunshot || w.Kind == ConditionKind.Missing) { bystanderHit = true; break; }
            }
        }
        Assert.True(bystanderHit, "a round fired at the far target should hit the bystander standing in the line");
    }

    [Fact]
    public void StandingShot_FliesOverDownedBystander()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) => ids.Add(e.Id));
        Assert.True(ids.Count >= 3, "need three pawns");
        int shooter = ids[0], bystander = ids[1], target = ids[2];
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 200);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        DownByPain(sim, bystander); // prone now

        void Place()
        {
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, bystander, 23.5f, 20.5f); // lying in the line
            SetPos(sim, target, 28.5f, 20.5f);    // standing
        }
        Place();
        sim.SetFireTarget(shooter, target);

        bool targetHit = false, bystanderGunshot = false;
        for (int i = 0; i < 2500; i++)
        {
            Place();
            sim.Step(SimConstants.TickSeconds);
            if (sim.Store.TryGetEntityById(bystander, out var b) && b.HasComponent<Health>())
                foreach (var w in b.GetComponent<Health>().Injuries!)
                    if (w.Kind == ConditionKind.Gunshot) bystanderGunshot = true;
            if (sim.Store.TryGetEntityById(target, out var t) && t.HasComponent<Health>())
                foreach (var w in t.GetComponent<Health>().Injuries!)
                    if (w.Kind == ConditionKind.Gunshot) targetHit = true;
            if (targetHit) break;
        }
        Assert.True(targetHit, "the standing target should be hit");
        Assert.False(bystanderGunshot, "a torso-height round should fly over the prone bystander");
    }

    [Fact]
    public void Recoil_ClimbsUnderAutoFire_ThenSettles()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 300);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        // Down the target first so the order is a finish-off — the shooter
        // keeps mag-dumping the prone target instead of stopping after a few
        // hits, which is what lets the recoil cone actually build.
        DownByPain(sim, target);
        sim.SetFireMode(shooter, FireMode.Auto);
        sim.SetFireTarget(shooter, target);

        float peak = 0f;
        for (int i = 0; i < 220; i++)
        {
            SetPos(sim, shooter, 20.5f, 20.5f);
            if (sim.Store.TryGetEntityById(target, out _)) SetPos(sim, target, 24.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
            peak = System.Math.Max(peak, sim.Store.GetEntityById(shooter).GetComponent<RangedCombat>().Recoil);
        }
        Assert.True(peak > 2f, $"sustained auto fire should build the recoil cone (peak={peak})");

        // Stop firing (undraft clears the order); recoil should settle back down.
        sim.QueueCommand(new StruggleGame.Sim.Commands.ToggleDraftCommand(shooter));
        for (int i = 0; i < 180; i++) sim.Step(SimConstants.TickSeconds);
        Assert.True(sim.Store.GetEntityById(shooter).GetComponent<RangedCombat>().Recoil < 0.5f,
            "recoil should recover once the pawn stops firing");
    }

    [Fact]
    public void TargetArea_Head_LandsUpperBodyWounds()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 90);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        sim.SetTargetArea(shooter, TargetArea.Head);
        sim.SetFireMode(shooter, FireMode.Single);
        sim.SetFireTarget(shooter, target);

        var upper = new HashSet<string> { "Head", "Neck", "EyeL", "EyeR", "EarL", "EarR" };
        bool anyUpper = false;
        for (int i = 0; i < 2500 && !anyUpper; i++)
        {
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, target, 24.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
            if (sim.Store.TryGetEntityById(target, out var t) && t.HasComponent<Health>())
                foreach (var w in t.GetComponent<Health>().Injuries!)
                    if (w.Kind == ConditionKind.Gunshot && upper.Contains(w.PartId)) { anyUpper = true; break; }
        }
        // Aim is a bias (vertical inaccuracy can stray to torso), so we only
        // require that head-aim does reach the upper body.
        Assert.True(anyUpper, "head-aimed fire should land at least one upper-body wound");
    }

    private static void EquipVest(SimRuntime sim, int id)
    {
        var e = sim.Store.GetEntityById(id);
        e.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>(),
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Apparel, ItemPath = ItemCatalog.KevlarVest.FullPath, Count = 1 },
            },
        });
    }

    // shooter fires `ammo` at a kevlar-wearing target aiming torso; returns
    // (sawTorsoGunshot, sawTorsoBruise) observed over the fight.
    private static (bool gun, bool bruise) ShootArmoredTorso(ItemDef ammo)
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        EquipVest(sim, target);
        ArmWithRifle(sim, shooter, ammo, 200);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        sim.SetTargetArea(shooter, TargetArea.Torso);
        // Single fire: one round resolves at a time. Auto would batch several
        // arrivals into one tick (damage now lands on arrival), spiking the
        // target unconscious before the per-tick heal below — which drops the
        // vest and lets later rounds hit a bare torso. We're testing armor, not
        // downing, so keep the rounds spaced out.
        sim.SetFireMode(shooter, FireMode.Single);
        sim.SetFireTarget(shooter, target);

        bool gun = false, bruise = false;
        for (int i = 0; i < 1500; i++)
        {
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, target, 24.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
            ref var th = ref sim.Store.GetEntityById(target).GetComponent<Health>();
            foreach (var w in th.Injuries!)
                if (w.PartId == "Torso")
                {
                    if (w.Kind == ConditionKind.Gunshot) gun = true;
                    if (w.Kind == ConditionKind.Bruise) bruise = true;
                }
            // Keep the target alive + standing so torso hits keep coming.
            th.Injuries!.Clear();
            th.BloodLevel = 1f;
            HealthSystem.Recompute(ref th);
        }
        return (gun, bruise);
    }

    [Fact]
    public void Kevlar_DeflectsFmj_AsBruise_NotGunshot()
    {
        var (gun, bruise) = ShootArmoredTorso(ItemCatalog.RifleAmmoFmj);
        Assert.False(gun, "FMJ should not penetrate the kevlar torso (deflected)");
        Assert.True(bruise, "deflected FMJ should leave a blunt bruise on the armored torso");
    }

    [Fact]
    public void Kevlar_PenetratedByAp_AsGunshot()
    {
        var (gun, _) = ShootArmoredTorso(ItemCatalog.RifleAmmoAp);
        Assert.True(gun, "AP should punch through the kevlar torso (gunshot wound)");
    }

    [Fact]
    public void AdjacentTarget_TooClose_DoesNotFire()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter, ItemCatalog.RifleAmmoFmj, 90);
        sim.Step(SimConstants.TickSeconds);
        sim.Step(SimConstants.TickSeconds);
        sim.SetFireTarget(shooter, target);

        bool hit = false;
        for (int i = 0; i < 800; i++)
        {
            // One tile apart — too close to bring the rifle to bear.
            SetPos(sim, shooter, 20.5f, 20.5f);
            SetPos(sim, target, 21.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
            if (sim.Store.TryGetEntityById(target, out var t) && t.HasComponent<Health>())
                foreach (var w in t.GetComponent<Health>().Injuries!)
                    if (w.Kind == ConditionKind.Gunshot) { hit = true; break; }
        }
        Assert.False(hit, "a ranged colonist shouldn't fire at a target on an adjacent tile");
    }

    private static int InvCount(Entity e, string path)
    {
        int n = 0;
        var inv = e.GetComponent<Inventory>();
        if (inv.Items is not null)
            foreach (var s in inv.Items) if (s.ItemPath == path) n += s.Count;
        return n;
    }
}
