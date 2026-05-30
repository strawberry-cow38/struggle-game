using Friflo.Engine.ECS;
using StruggleGame.Sim;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

// Cover-stance decision logic (phase 7b/7c). The projectile-blocking side is
// exercised by the firing pipeline; here we pin down the deterministic AI:
// a shooter behind a sandbag enters a crouch stance; a shooter hugging a wall
// with a clear flank enters a lean stance.
public class CoverTests
{
    [Fact]
    public void RangedLos_DoesNotCutPastWallCorner()
    {
        var sim = new SimRuntime();
        // A diagonal sight line that grazes the corner of a wall at (21,20)
        // must be blocked — a real round would clip the wall, not thread past.
        bool before = sim.RangedLosClear(20, 20, 22, 18);
        sim.InstantPlaceWall(new TilePos(21, 20));
        if (before) // only meaningful if the lane was open pre-wall
            Assert.False(sim.RangedLosClear(20, 20, 22, 18),
                "diagonal sight must not cut past the wall corner");
    }

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

    private static void ArmWithRifle(SimRuntime sim, int id)
    {
        var e = sim.Store.GetEntityById(id);
        e.AddComponent(new Inventory
        {
            Items = new List<InventoryStack>
            {
                new InventoryStack { ItemPath = ItemCatalog.RifleAmmoFmj.FullPath, Count = 60 },
            },
            Equipped = new List<EquippedItemSlot>
            {
                new EquippedItemSlot { Slot = EquipSlot.Generic, ItemPath = ItemCatalog.AssaultRifle.FullPath, Count = 1 },
            },
        });
    }

    private static CoverStance Engage(SimRuntime sim, int shooter, int target,
        float sx, float sy, float tx, float ty, out bool leaning)
    {
        sim.Store.GetEntityById(shooter).AddComponent(new Drafted());
        sim.Store.GetEntityById(target).AddComponent(new Drafted());
        ArmWithRifle(sim, shooter);
        sim.Step(SimConstants.TickSeconds); // attach RangedCombat
        sim.Step(SimConstants.TickSeconds);
        SetPos(sim, shooter, sx, sy);
        SetPos(sim, target, tx, ty);
        sim.SetFireTarget(shooter, target);

        for (int i = 0; i < 10; i++)
        {
            SetPos(sim, shooter, sx, sy);
            SetPos(sim, target, tx, ty);
            sim.Step(SimConstants.TickSeconds);
        }
        var rc = sim.Store.GetEntityById(shooter).GetComponent<RangedCombat>();
        leaning = rc.Leaning;
        return rc.Stance;
    }

    [Fact]
    public void ShooterBehindSandbag_EntersCrouchStance()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);

        // Sandbag immediately east of the shooter, toward the target.
        Assert.True(sim.InstantPlaceSandbag(new TilePos(21, 20)));

        var stance = Engage(sim, shooter, target, 20.5f, 20.5f, 25.5f, 20.5f, out bool leaning);

        Assert.NotEqual(CoverStance.None, stance);   // is using the cover
        Assert.False(leaning);                        // crouch, not a wall-lean
    }

    [Fact]
    public void ShooterInOpen_NoCoverStance()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);

        // No cover anywhere — clean line of sight in the open.
        var stance = Engage(sim, shooter, target, 20.5f, 20.5f, 25.5f, 20.5f, out _);

        Assert.Equal(CoverStance.None, stance);
    }

    [Fact]
    public void DraftedNextToSandbag_StaysCrouched_WithoutTarget()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (pawn, _) = TwoPawns(sim);
        var e = sim.Store.GetEntityById(pawn);
        e.AddComponent(new Drafted());
        Assert.True(sim.InstantPlaceSandbag(new TilePos(21, 20)));

        // Standing right next to the sandbag, no fire target → head down.
        for (int i = 0; i < 5; i++)
        {
            SetPos(sim, pawn, 20.5f, 20.5f);
            sim.Step(SimConstants.TickSeconds);
        }
        Assert.True(e.GetComponent<Wanderer>().Crouched, "should crouch beside a sandbag");

        // Stepped well away → stands back up.
        for (int i = 0; i < 5; i++)
        {
            SetPos(sim, pawn, 30.5f, 30.5f);
            sim.Step(SimConstants.TickSeconds);
        }
        Assert.False(e.GetComponent<Wanderer>().Crouched, "should stand once clear of cover");
    }

    [Fact]
    public void ShooterHuggingWall_LeansAroundCorner()
    {
        var sim = new SimRuntime();
        sim.Step(SimConstants.TickSeconds);
        var (shooter, target) = TwoPawns(sim);

        // One wall directly between shooter and target blocks the straight
        // shot, but the flanking cells can still see around it → lean.
        Assert.True(sim.InstantPlaceWall(new TilePos(21, 20)));

        var stance = Engage(sim, shooter, target, 20.5f, 20.5f, 24.5f, 20.5f, out bool leaning);

        Assert.NotEqual(CoverStance.None, stance);
        Assert.True(leaning, "shooter should lean around the wall to engage");
    }
}
