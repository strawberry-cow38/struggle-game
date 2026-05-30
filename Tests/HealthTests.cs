using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class HealthTests
{
    private static Health Fresh()
    {
        var h = new Health { BloodLevel = 1f, Injuries = new List<PartInjury>() };
        HealthSystem.Recompute(ref h);
        return h;
    }

    private static void Injure(ref Health h, string part, ConditionKind kind, float sev)
    {
        h.Injuries!.Add(new PartInjury { PartId = part, Kind = kind, Severity = sev });
        HealthSystem.Recompute(ref h);
    }

    [Fact]
    public void FullHealth_AllCapacitiesOneAndConscious()
    {
        var h = Fresh();
        Assert.Equal(1f, h.Consciousness, 3);
        Assert.Equal(1f, h.Moving, 3);
        Assert.Equal(1f, h.Manipulation, 3);
        Assert.Equal(1f, h.Sight, 3);
        Assert.False(h.Unconscious);
    }

    [Fact]
    public void MissingHand_HalfHandManipulation_MissingArmMore()
    {
        var h = Fresh();
        Injure(ref h, "HandL", ConditionKind.Missing, 1f);
        Assert.Equal(0.75f, h.Manipulation, 3); // lost one of four 0.5 sources

        var h2 = Fresh();
        Injure(ref h2, "ArmL", ConditionKind.Missing, 1f); // takes the hand with it
        Assert.Equal(0.5f, h2.Manipulation, 3);
    }

    [Fact]
    public void MissingFoot_ReducesMoving()
    {
        var h = Fresh();
        Injure(ref h, "FootL", ConditionKind.Missing, 1f);
        Assert.Equal(0.75f, h.Moving, 3);
    }

    [Fact]
    public void LostEye_HalvesSight()
    {
        var h = Fresh();
        Injure(ref h, "EyeL", ConditionKind.Missing, 1f);
        Assert.Equal(0.5f, h.Sight, 3);
    }

    [Fact]
    public void BleedingOut_PassesOut_NoDeath()
    {
        var h = Fresh();
        Injure(ref h, "Torso", ConditionKind.Cut, 0.8f);
        Assert.False(h.Unconscious);

        // ~50 sim-sec of a 0.8 cut (bleed 0.016/s) drops blood below the
        // consciousness threshold.
        HealthSystem.Advance(ref h, 50f);
        HealthSystem.Recompute(ref h);
        Assert.True(h.BloodLevel < 0.30f);
        Assert.True(h.Unconscious);
        Assert.True(h.BloodLevel >= 0f); // floors, never dies
    }

    [Fact]
    public void PainShock_DownsColonist_EvenWithoutBleeding()
    {
        var h = Fresh();
        // Bruises don't bleed and barely touch capacities, but pile on
        // enough and the pain alone knocks the colonist out.
        Injure(ref h, "ArmL", ConditionKind.Bruise, 1f);
        Injure(ref h, "ArmR", ConditionKind.Bruise, 1f);
        Injure(ref h, "LegL", ConditionKind.Bruise, 1f);
        Injure(ref h, "LegR", ConditionKind.Bruise, 1f);
        Injure(ref h, "Torso", ConditionKind.Bruise, 1f);
        Injure(ref h, "Head", ConditionKind.Bruise, 1f);
        Assert.True(h.Pain >= HealthSystem.PainShockThreshold);
        Assert.True(h.Unconscious);
        Assert.True(h.BloodLevel >= 0.99f); // not from blood loss — pure pain
    }

    [Fact]
    public void SmallCut_HealsAwayUntended()
    {
        var h = Fresh();
        Injure(ref h, "ArmR", ConditionKind.Cut, 0.3f);
        Assert.Single(h.Injuries!);
        HealthSystem.Advance(ref h, 2000f); // heals 0.0002*2000 = 0.4 > 0.3
        Assert.Empty(h.Injuries!);
    }

    [Fact]
    public void LargeWound_WorsensUntended()
    {
        var h = Fresh();
        Injure(ref h, "ArmR", ConditionKind.Cut, 0.7f);
        HealthSystem.Advance(ref h, 1000f);
        Assert.True(h.Injuries![0].Severity > 0.7f);
    }

    [Fact]
    public void Scar_IsPermanent_SmallEfficiencyLoss()
    {
        var h = Fresh();
        Injure(ref h, "HandL", ConditionKind.Scar, 1f);
        HealthSystem.Advance(ref h, 5000f);
        Assert.Single(h.Injuries!); // never heals away
        Assert.True(h.Manipulation < 1f && h.Manipulation > 0.9f); // small hit
    }

    // Down a colonist without killing them: pile on bruises until pain
    // shock. (Brain/heart loss + bleed-out are lethal now.)
    private static void DownByPain(SimRuntime sim, int id)
    {
        foreach (var part in new[] { "ArmL", "ArmR", "LegL", "LegR", "Torso", "Head" })
            sim.ApplyInjury(id, part, ConditionKind.Bruise, 1f);
    }

    [Fact]
    public void UnconsciousPawn_CollapsesAndStopsMoving()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        int pawnId = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (pawnId == 0) pawnId = e.Id; });

        DownByPain(sim, pawnId); // pain shock — downed but alive
        // Let the health tick flip the flag + the controller react.
        for (int i = 0; i < 120; i++) sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.Store.TryGetEntityById(pawnId, out var pawn));
        Assert.True(pawn.GetComponent<Health>().Unconscious);
        var wp = pawn.GetComponent<WorldPos>();
        var before = (wp.X, wp.Y);
        for (int i = 0; i < 300; i++) sim.Step(SimConstants.TickSeconds);
        var wp2 = pawn.GetComponent<WorldPos>();
        Assert.Equal(before.X, wp2.X, 3);
        Assert.Equal(before.Y, wp2.Y, 3); // stayed put — didn't wander
        Assert.False(pawn.HasComponent<BuildTarget>());
    }

    [Fact]
    public void BleedingPawn_DripsBloodPuddles()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        int pawnId = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (pawnId == 0) pawnId = e.Id; });
        sim.ApplyInjury(pawnId, "Torso", ConditionKind.Cut, 0.8f);

        for (int i = 0; i < 400; i++) sim.Step(SimConstants.TickSeconds);

        int puddles = 0;
        sim.Store.Query<BloodPuddle>().ForEachEntity((ref BloodPuddle _, Entity _) => puddles++);
        Assert.True(puddles > 0, "a bleeding colonist should leave blood puddles");
    }

    [Fact]
    public void DownedColonist_CannotBeDrafted()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        int pawnId = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (pawnId == 0) pawnId = e.Id; });
        DownByPain(sim, pawnId); // unconscious now (alive)

        sim.QueueCommand(new StruggleGame.Sim.Commands.ToggleDraftCommand(pawnId));
        sim.Step(SimConstants.TickSeconds);

        Assert.True(sim.Store.TryGetEntityById(pawnId, out var pawn));
        Assert.False(pawn.HasComponent<Drafted>());
    }

    [Fact]
    public void ZeroConsciousness_KillsPawn_LeavesCorpse()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        int pawnId = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (pawnId == 0) pawnId = e.Id; });

        sim.ApplyInjury(pawnId, "Brain", ConditionKind.Missing, 1f); // consciousness 0
        for (int i = 0; i < 120; i++) sim.Step(SimConstants.TickSeconds); // health tick processes death

        // Pawn entity is gone...
        bool pawnGone = !sim.Store.TryGetEntityById(pawnId, out var p) || !p.HasComponent<Wanderer>();
        Assert.True(pawnGone, "dead pawn should be removed");
        // ...and a corpse exists holding the colonist's health data.
        int corpses = 0;
        bool keptData = false;
        sim.Store.Query<Corpse>().ForEachEntity((ref Corpse c, Entity _) =>
        {
            corpses++;
            if (c.Health.Injuries is { Count: > 0 }) keptData = true;
        });
        Assert.Equal(1, corpses);
        Assert.True(keptData, "corpse should retain the colonist's injuries");
    }

    [Fact]
    public void DownedColonist_DropsGearOnTheGround()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        int pawnId = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (pawnId == 0) pawnId = e.Id; });

        Assert.True(sim.Store.TryGetEntityById(pawnId, out var pawn));
        pawn.AddComponent(new Inventory
        {
            Items = new List<InventoryStack> { new InventoryStack { ItemPath = ItemCatalog.Carrot.FullPath, Count = 3 } },
            Equipped = new List<EquippedItemSlot> { new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = ItemCatalog.WoodenTrinket.FullPath, Count = 1 } },
        });

        DownByPain(sim, pawnId);
        for (int i = 0; i < 120; i++) sim.Step(SimConstants.TickSeconds); // health tick fires OnDowned

        var inv = sim.Store.GetEntityById(pawnId).GetComponent<Inventory>();
        Assert.True((inv.Items?.Count ?? 0) == 0 && (inv.Equipped?.Count ?? 0) == 0, "inventory should be emptied");
        // The trinket + carrots are now on the ground.
        int trinkets = 0, carrots = 0;
        sim.Store.Query<ItemPile>().ForEachEntity((ref ItemPile p, Entity _) =>
        {
            if (p.ItemPath == ItemCatalog.WoodenTrinket.FullPath) trinkets += p.Count;
            if (p.ItemPath == ItemCatalog.Carrot.FullPath) carrots += p.Count;
        });
        Assert.Equal(1, trinkets);
        Assert.Equal(3, carrots);
    }

    [Fact]
    public void ApplyInjury_OnSpawnedPawn_Recomputes()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        int pawnId = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (pawnId == 0) pawnId = e.Id; });

        sim.ApplyInjury(pawnId, "FootL", ConditionKind.Missing, 1f);
        Assert.True(sim.Store.TryGetEntityById(pawnId, out var pawn));
        Assert.Equal(0.75f, pawn.GetComponent<Health>().Moving, 3);
    }
}
