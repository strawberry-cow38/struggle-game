using System.Collections.Generic;
using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class MedicalTests
{
    private static int FirstColonist(SimRuntime sim)
    {
        int id = 0;
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (id == 0 && !e.HasComponent<Enemy>()) id = e.Id; });
        return id;
    }

    private static void SetPos(SimRuntime sim, int id, float x, float y)
    { ref var wp = ref sim.Store.GetEntityById(id).GetComponent<WorldPos>(); wp.X = x; wp.Y = y; }

    private static List<int> Colonists(SimRuntime sim)
    {
        var ids = new List<int>();
        sim.Store.Query<WorldPos, Wanderer>().ForEachEntity((ref WorldPos _, ref Wanderer _, Entity e) =>
        { if (!e.HasComponent<Enemy>()) ids.Add(e.Id); });
        return ids;
    }

    [Fact]
    public void Tend_TreatsWorstWoundsWithinBudget()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var e = sim.Store.GetEntityById(FirstColonist(sim));
        // Budget is 10: worst (8) tended, next (6) tended (crosses 0), smallest
        // (2) left untreated.
        e.GetComponent<Health>().Injuries = new List<PartInjury>
        {
            new PartInjury { PartId = "Torso", Kind = ConditionKind.Gunshot, Severity = 8f },
            new PartInjury { PartId = "ArmL", Kind = ConditionKind.Gunshot, Severity = 6f },
            new PartInjury { PartId = "LegL", Kind = ConditionKind.Gunshot, Severity = 2f },
        };

        sim.ApplyTreatment(e, stabilize: false, SimConstants.TendQualityStub);

        var inj = e.GetComponent<Health>().Injuries!;
        var byPart = new Dictionary<string, PartInjury>();
        foreach (var w in inj) byPart[w.PartId] = w;
        Assert.True(byPart["Torso"].Tended);
        Assert.True(byPart["ArmL"].Tended);
        Assert.False(byPart["LegL"].Tended);
        Assert.Equal(SimConstants.TendQualityStub, byPart["Torso"].TendQuality);
        Assert.Equal(0f, HealthSystem.BleedOf(byPart["Torso"])); // tended → no bleed
    }

    [Fact]
    public void Stabilize_CutsBleedButDoesNotTend()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var e = sim.Store.GetEntityById(FirstColonist(sim));
        var wound = new PartInjury { PartId = "Torso", Kind = ConditionKind.Gunshot, Severity = 5f };
        e.GetComponent<Health>().Injuries = new List<PartInjury> { wound };
        float rawBleed = BodyTree.BleedRate(wound.Kind, wound.Severity);
        Assert.True(rawBleed > 0f, "test wound should bleed");

        sim.ApplyTreatment(e, stabilize: true, 0f);

        var w = e.GetComponent<Health>().Injuries![0];
        Assert.True(w.Stabilized);
        Assert.False(w.Tended);
        Assert.Equal(rawBleed * HealthSystem.StabilizeBleedFraction, HealthSystem.BleedOf(w), 4);
    }

    [Fact]
    public void BareHandsTend_NoMedicine_HalfQuality()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var ids = Colonists(sim);
        int doctor = ids[0], patient = ids[1];

        var de = sim.Store.GetEntityById(doctor);
        de.AddComponent(new Drafted()); // no inventory → no medicine
        SetPos(sim, doctor, 20.5f, 20.5f);
        ref var dpf = ref de.GetComponent<PathFollower>(); dpf.Waypoints = null; dpf.Index = 0; dpf.PendingPathId = 0;

        var pe = sim.Store.GetEntityById(patient);
        SetPos(sim, patient, 21.5f, 20.5f);
        pe.GetComponent<Health>().Injuries = new List<PartInjury>
        { new PartInjury { PartId = "Torso", Kind = ConditionKind.Gunshot, Severity = 4f } };

        sim.SetTreatmentTarget(doctor, patient, stabilize: false);

        // Bare-hands tend = 1.3x the work time; step well past it.
        for (int i = 0; i < 420; i++)
        {
            SetPos(sim, doctor, 20.5f, 20.5f); SetPos(sim, patient, 21.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
        }
        var w = pe.GetComponent<Health>().Injuries![0];
        Assert.True(w.Tended);
        Assert.Equal(SimConstants.TendQualityStub * 0.5f, w.TendQuality, 4); // half quality
    }

    [Fact]
    public void Tend_RepeatsUntilAllWoundsTreated()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var ids = Colonists(sim);
        int doctor = ids[0], patient = ids[1];

        var de = sim.Store.GetEntityById(doctor);
        de.AddComponent(new Drafted());
        de.AddComponent(new Inventory
        { Items = new List<InventoryStack> { new InventoryStack { ItemPath = ItemCatalog.Medicine.FullPath, Count = 5 } } });
        SetPos(sim, doctor, 20.5f, 20.5f);
        ref var dpf = ref de.GetComponent<PathFollower>(); dpf.Waypoints = null; dpf.Index = 0; dpf.PendingPathId = 0;

        var pe = sim.Store.GetEntityById(patient);
        SetPos(sim, patient, 21.5f, 20.5f);
        // 8+6+5 = 19 > budget 10 → needs two tend cycles.
        pe.GetComponent<Health>().Injuries = new List<PartInjury>
        {
            new PartInjury { PartId = "Torso", Kind = ConditionKind.Gunshot, Severity = 8f },
            new PartInjury { PartId = "ArmL", Kind = ConditionKind.Gunshot, Severity = 6f },
            new PartInjury { PartId = "LegL", Kind = ConditionKind.Gunshot, Severity = 5f },
        };

        sim.SetTreatmentTarget(doctor, patient, stabilize: false);
        for (int i = 0; i < 700; i++)
        {
            SetPos(sim, doctor, 20.5f, 20.5f); SetPos(sim, patient, 21.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
        }

        foreach (var w in pe.GetComponent<Health>().Injuries!)
            Assert.True(w.Tended, $"wound {w.PartId} should be tended");
        Assert.False(de.HasComponent<TreatmentTarget>(), "order clears once all wounds are tended");
    }

    [Fact]
    public void TendJob_DoctorWalksWorksAndConsumesMedicine()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var ids = Colonists(sim);
        Assert.True(ids.Count >= 2, "need two colonists");
        int doctor = ids[0], patient = ids[1];

        var de = sim.Store.GetEntityById(doctor);
        de.AddComponent(new Drafted());
        de.AddComponent(new Inventory
        {
            Items = new List<InventoryStack> { new InventoryStack { ItemPath = ItemCatalog.Medicine.FullPath, Count = 1 } },
        });
        SetPos(sim, doctor, 20.5f, 20.5f);
        ref var dpf = ref de.GetComponent<PathFollower>(); dpf.Waypoints = null; dpf.Index = 0; dpf.PendingPathId = 0;

        var pe = sim.Store.GetEntityById(patient);
        SetPos(sim, patient, 21.5f, 20.5f); // adjacent
        ref var ppf = ref pe.GetComponent<PathFollower>(); ppf.Waypoints = null; ppf.Index = 0; ppf.PendingPathId = 0;
        pe.GetComponent<Health>().Injuries = new List<PartInjury>
        { new PartInjury { PartId = "Torso", Kind = ConditionKind.Gunshot, Severity = 4f } };

        sim.SetTreatmentTarget(doctor, patient, stabilize: false);

        // Work time is 240 ticks; pin both in place so the doctor stays adjacent.
        for (int i = 0; i < 300; i++)
        {
            SetPos(sim, doctor, 20.5f, 20.5f); SetPos(sim, patient, 21.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
        }

        Assert.True(pe.GetComponent<Health>().Injuries![0].Tended, "patient's wound should be tended");
        // Medicine consumed (stack removed when it hits 0).
        int meds = 0;
        var inv = de.GetComponent<Inventory>();
        if (inv.Items is not null) foreach (var s in inv.Items) if (s.ItemPath == ItemCatalog.Medicine.FullPath) meds += s.Count;
        Assert.Equal(0, meds);
        Assert.False(de.HasComponent<TreatmentTarget>(), "treatment order should clear on completion");
    }
}
