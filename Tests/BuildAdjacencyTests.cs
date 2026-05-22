using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

public class BuildAdjacencyTests
{
    // Blueprint at tile (4, 4) — center is (4.5, 4.5).

    [Fact]
    public void Cardinal_AdjacentTileCenter_InRange()
    {
        // Pawn standing on tile (4, 3) center = (4.5, 3.5). |dx|=0, |dy|=1.
        Assert.True(BuildAdjacency.InRange(4.5f, 3.5f, 4, 4));
    }

    [Fact]
    public void Diagonal_AdjacentTileCenter_InRange()
    {
        // Pawn on tile (5, 5) center = (5.5, 5.5). |dx|=|dy|=1.0.
        Assert.True(BuildAdjacency.InRange(5.5f, 5.5f, 4, 4));
    }

    [Fact]
    public void OnBlueprintTile_OutOfRange()
    {
        // Pawn on tile (4, 4) center = (4.5, 4.5). Same-tile excluded.
        Assert.False(BuildAdjacency.InRange(4.5f, 4.5f, 4, 4));
    }

    [Fact]
    public void TwoTilesAway_OutOfRange()
    {
        // Pawn on tile (6, 4) center = (6.5, 4.5). |dx|=2.0 > 1.0.
        Assert.False(BuildAdjacency.InRange(6.5f, 4.5f, 4, 4));
    }

    [Fact]
    public void SubTileDrift_OutOfRange()
    {
        // The bug master reported: pawn at world (5.9, 5.9). Old integer
        // check passed because (int)5.9 = 5, dx=dy=1. Float Chebyshev
        // from blueprint center (4.5, 4.5) is 1.4 — rejected.
        Assert.False(BuildAdjacency.InRange(5.9f, 5.9f, 4, 4));
    }

    [Fact]
    public void ExactlyOnRingEdge_InRange()
    {
        // Pawn just inside the cardinal edge: pos.X = 4.5, pos.Y = 5.5.
        // |dy| = 1.0 exact — passes the <= 1.0 check.
        Assert.True(BuildAdjacency.InRange(4.5f, 5.5f, 4, 4));
    }

    [Fact]
    public void JustInsideBlueprintTile_OutOfRange()
    {
        // Pawn at (4.6, 4.6) — inside blueprint tile but not at center.
        // |dx|=|dy|=0.1, both <= 0.5, on-tile exclusion fires.
        Assert.False(BuildAdjacency.InRange(4.6f, 4.6f, 4, 4));
    }
}
