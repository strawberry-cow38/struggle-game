using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Bodies;
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
