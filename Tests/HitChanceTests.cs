using System;
using StruggleGame.Sim;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Gunnery;
using Xunit;

namespace StruggleGame.Tests;

// Locks the analytic single-shot hit-chance model used by the hover readout
// (HitChanceEstimator) against the firing model it mirrors.
public class HitChanceTests
{
    private static RangedSpec Rifle() => new()
    {
        Range = 30f,
        SpreadDegrees = 1.2f,
    };

    private static bool NoWall(int x, int y) => false;
    private static bool NoSandbag(int x, int y) => false;

    [Fact]
    public void PointBlankClearShot_IsVeryLikely()
    {
        var r = HitChanceEstimator.Estimate(
            Rifle(), recoilDeg: 0f,
            fromX: 5f, fromY: 5f, toX: 8f, toY: 5f,
            targetBodyH: SimConstants.PawnBodyHeight, aimHeight: SimConstants.AimAutoHeight,
            NoWall, NoSandbag);
        Assert.True(r.InRange);
        Assert.Equal(HitCover.None, r.Cover);
        Assert.True(r.Chance > 0.85f, $"close clear shot should be near-certain, was {r.Chance:0.00}");
    }

    [Fact]
    public void OutOfRange_IsZeroAndFlagged()
    {
        var r = HitChanceEstimator.Estimate(
            Rifle(), recoilDeg: 0f,
            fromX: 0f, fromY: 0f, toX: 50f, toY: 0f,
            targetBodyH: SimConstants.PawnBodyHeight, aimHeight: SimConstants.AimAutoHeight,
            NoWall, NoSandbag);
        Assert.False(r.InRange);
        Assert.Equal(0f, r.Chance);
    }

    [Fact]
    public void WallOnLine_BlocksTheShot()
    {
        // Wall sits at tile x=8 between shooter (x=5) and target (x=12).
        var r = HitChanceEstimator.Estimate(
            Rifle(), recoilDeg: 0f,
            fromX: 5.5f, fromY: 5.5f, toX: 12.5f, toY: 5.5f,
            targetBodyH: SimConstants.PawnBodyHeight, aimHeight: SimConstants.AimAutoHeight,
            isWall: (x, y) => x == 8 && y == 5, isSandbag: NoSandbag);
        Assert.Equal(HitCover.WallBlocked, r.Cover);
        Assert.Equal(0f, r.Chance);
    }

    [Fact]
    public void DistanceWidensTheCone_LowersChance()
    {
        var near = HitChanceEstimator.Estimate(Rifle(), 0f, 0f, 0f, 6f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag);
        var far = HitChanceEstimator.Estimate(Rifle(), 0f, 0f, 0f, 28f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag);
        Assert.True(far.Chance < near.Chance, $"far ({far.Chance:0.00}) should be worse than near ({near.Chance:0.00})");
        Assert.True(far.ScatterRadius > near.ScatterRadius);
    }

    [Fact]
    public void Recoil_WidensTheCone_LowersChance()
    {
        var steady = HitChanceEstimator.Estimate(Rifle(), 0f, 0f, 0f, 20f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag);
        var kicked = HitChanceEstimator.Estimate(Rifle(), recoilDeg: 6f, 0f, 0f, 20f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag);
        Assert.True(kicked.ConeDeg > steady.ConeDeg);
        Assert.True(kicked.Chance < steady.Chance);
    }

    [Fact]
    public void Darkness_WidensCone_LowersChance()
    {
        var lit = HitChanceEstimator.Estimate(Rifle(), 0f, 0f, 0f, 16f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag,
            spreadMultiplier: 1f);
        var dark = HitChanceEstimator.Estimate(Rifle(), 0f, 0f, 0f, 16f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag,
            spreadMultiplier: 2.5f);
        Assert.True(dark.ConeDeg > lit.ConeDeg);
        Assert.True(dark.Chance < lit.Chance, $"dark ({dark.Chance:0.00}) should be worse than lit ({lit.Chance:0.00})");
    }

    [Fact]
    public void SmallerHitRadius_LowersChance()
    {
        var full = HitChanceEstimator.Estimate(Rifle(), 0f, 0f, 0f, 16f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag,
            hitRadius: HitChanceEstimator.HitRadius);
        var sliver = HitChanceEstimator.Estimate(Rifle(), 0f, 0f, 0f, 16f, 0f,
            SimConstants.PawnBodyHeight, SimConstants.AimAutoHeight, NoWall, NoSandbag,
            hitRadius: HitChanceEstimator.HitRadius * 0.5f);
        Assert.True(sliver.Chance < full.Chance, $"sliver ({sliver.Chance:0.00}) should be worse than full ({full.Chance:0.00})");
    }

    [Fact]
    public void SandbagOnLine_RaisesFloor_HurtsLowAim()
    {
        // Aiming at the legs (low) into a sandbag should be eaten harder than a
        // torso shot, since the sandbag crest cuts off the low band.
        var legs = HitChanceEstimator.Estimate(Rifle(), 0f, 5.5f, 5.5f, 12.5f, 5.5f,
            SimConstants.PawnBodyHeight, SimConstants.AimLegsHeight,
            NoWall, isSandbag: (x, y) => x == 8 && y == 5);
        Assert.Equal(HitCover.Sandbag, legs.Cover);
        Assert.True(legs.Chance < 0.5f, $"low shot into a sandbag should be largely eaten, was {legs.Chance:0.00}");
    }
}
