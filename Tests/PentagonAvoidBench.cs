using System;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

// Verifies the RimWorld-style fix (master 2026-06-18): pole-align the planet +
// polar no-go caps → the playable equatorial band is 100% pentagon-free, all 12
// pentagons buried in the impassable poles, hexes otherwise untouched.
public class PentagonAvoidTests
{
    [Fact]
    public void PoleAligned_TwoPentagonsAtPoles_TenInRings()
    {
        var p = new HexPlanet(64, 1337, 1f);
        int atPole = 0, inRing = 0, other = 0;
        foreach (var t in p.Tiles)
        {
            if (!t.IsPentagon) continue;
            float absLat = MathF.Asin(Math.Clamp(MathF.Abs(t.Center.Y), 0f, 1f)) * 180f / MathF.PI;
            if (absLat > 80f) atPole++;
            else if (absLat is > 20f and < 33f) inRing++;   // ±26.57° rings
            else other++;
        }
        Assert.Equal(2, atPole);     // one pentagon on each pole
        Assert.Equal(10, inRing);    // ten in the ±26.57° rings
        Assert.Equal(0, other);      // none stranded in the playable band
    }

    [Theory]
    [InlineData(0.40f)]   // |lat| ≲ 23.6°
    [InlineData(0.44f)]   // just under the 26.57° ring
    public void PlayableBand_IsPentagonFree(float coverage)
    {
        var p = new HexPlanet(96, 7, coverage);
        int playablePentagons = 0, playable = 0;
        foreach (var t in p.Tiles)
        {
            if (!t.Generated) continue;
            playable++;
            if (t.IsPentagon) playablePentagons++;
        }
        Assert.Equal(0, playablePentagons);   // band is pure hexes
        Assert.True(playable > p.TileCount / 4, $"band too small: {playable}/{p.TileCount}");
    }

    [Fact]
    public void BigPolarNoGo_AndFullCoverageHasNoCaps()
    {
        var band = new HexPlanet(64, 1, 0.4f);
        int nogo = 0; foreach (var t in band.Tiles) if (!t.Generated) nogo++;
        Assert.True(nogo > band.TileCount / 2, "expected big polar no-go (>50%)");

        var full = new HexPlanet(64, 1, 1f);
        foreach (var t in full.Tiles) Assert.True(t.Generated); // coverage 1 = no caps
    }
}
