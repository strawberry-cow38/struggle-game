using System.Numerics;
using StruggleGame.Sim.World;
using Xunit;

namespace StruggleGame.Tests;

// Topology guards for the Goldberg-polyhedron world sphere. These verify the
// geometry is correct WITHOUT needing Godot to render it: a hex sphere of
// frequency N must have 10*N²+2 tiles, exactly 12 pentagons (the rest
// hexagons), every point on the unit sphere, and a symmetric neighbour graph.
public class HexPlanetTests
{
    [Theory]
    [InlineData(1)]   // 12 tiles — a dodecahedron, all pentagons
    [InlineData(4)]   // 162
    [InlineData(12)]  // 1442
    public void TileCount_Is_10NsquaredPlus2(int n)
    {
        var planet = new HexPlanet(frequency: n, seed: 1337);
        Assert.Equal(10 * n * n + 2, planet.TileCount);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    public void Has_Exactly_12_Pentagons_Rest_Hexagons(int n)
    {
        var planet = new HexPlanet(frequency: n, seed: 7);
        int pent = 0, hex = 0, other = 0;
        foreach (var t in planet.Tiles)
        {
            if (t.Corners.Length == 5) pent++;
            else if (t.Corners.Length == 6) hex++;
            else other++;
        }
        Assert.Equal(12, pent);
        Assert.Equal(0, other);
        Assert.Equal(planet.TileCount - 12, hex);
    }

    [Fact]
    public void All_Centres_And_Corners_On_Unit_Sphere()
    {
        var planet = new HexPlanet(frequency: 10, seed: 99);
        foreach (var t in planet.Tiles)
        {
            Assert.True(System.MathF.Abs(t.Center.Length() - 1f) < 1e-3f, "center off-sphere");
            foreach (var c in t.Corners)
                Assert.True(System.MathF.Abs(c.Length() - 1f) < 1e-3f, "corner off-sphere");
        }
    }

    [Fact]
    public void Neighbour_Count_Matches_Corner_Count()
    {
        var planet = new HexPlanet(frequency: 8, seed: 3);
        foreach (var t in planet.Tiles)
            Assert.Equal(t.Corners.Length, t.Neighbors.Length);
    }

    [Fact]
    public void Neighbour_Graph_Is_Symmetric()
    {
        var planet = new HexPlanet(frequency: 6, seed: 5);
        foreach (var t in planet.Tiles)
            foreach (int nb in t.Neighbors)
                Assert.Contains(t.Index, planet.Tiles[nb].Neighbors);
    }

    [Fact]
    public void Generation_Is_Deterministic_Per_Seed()
    {
        var a = new HexPlanet(frequency: 8, seed: 4242);
        var b = new HexPlanet(frequency: 8, seed: 4242);
        Assert.Equal(a.TileCount, b.TileCount);
        for (int i = 0; i < a.TileCount; i++)
        {
            Assert.Equal(a.Tiles[i].Biome, b.Tiles[i].Biome);
            Assert.Equal(a.Tiles[i].Center, b.Tiles[i].Center);
        }
    }

    [Fact]
    public void Different_Seeds_Give_Different_Biome_Maps()
    {
        var a = new HexPlanet(frequency: 10, seed: 1);
        var b = new HexPlanet(frequency: 10, seed: 2);
        int diff = 0;
        for (int i = 0; i < a.TileCount; i++)
            if (a.Tiles[i].Biome != b.Tiles[i].Biome) diff++;
        Assert.True(diff > a.TileCount / 10, $"only {diff} tiles differ — biome field not seed-varying");
    }

    [Fact]
    public void NearestTile_Finds_The_Closest_Centre()
    {
        var planet = new HexPlanet(frequency: 8, seed: 11);
        // The nearest tile to a tile's own centre must be itself.
        for (int i = 0; i < planet.TileCount; i += 37)
            Assert.Equal(i, planet.NearestTile(planet.Tiles[i].Center));
    }
}
