using System;
using System.IO;
using System.Numerics;
using System.Text;
using StruggleGame.Sim.World;
using Xunit;
using Xunit.Abstractions;

namespace StruggleGame.Tests;

// Question (master 2026-06-18): can the PLAYABLE region dodge all 12 pentagons so
// the hexes stay 100% uniform (no tiling rewrite) and the pentagons live only in
// the unplayable poles/water? This measures it: find the tile farthest from every
// pentagon (the centre of the biggest pentagon-free cap = an icosahedron face
// centroid), then report how much coverage stays pentagon-free around it.
public class PentagonAvoidBench
{
    private readonly ITestOutputHelper _out;
    public PentagonAvoidBench(ITestOutputHelper o){ _out = o; }

    [Fact]
    public void Measure()
    {
        var sb = new StringBuilder();
        void Line(string s){ _out.WriteLine(s); sb.AppendLine(s); }

        var planet = new HexPlanet(64, 1337, 1f); // pentagon DIRECTIONS are the 12
                                                   // icosa verts at any frequency
        var pents = new System.Collections.Generic.List<Vector3>();
        foreach (var t in planet.Tiles) if (t.IsPentagon) pents.Add(t.Center);
        Line($"pentagons: {pents.Count}");

        // best centre = tile maximizing the min angular distance to any pentagon
        WorldTile best = planet.Tiles[0]; float bestMinDot = 2f;
        foreach (var t in planet.Tiles)
        {
            float maxDot = -2f; // nearest pentagon = largest dot
            foreach (var p in pents) { float d = Vector3.Dot(t.Center, p); if (d > maxDot) maxDot = d; }
            if (maxDot < bestMinDot) { bestMinDot = maxDot; best = t; }
        }
        float capRadiusDeg = MathF.Acos(Math.Clamp(bestMinDot, -1f, 1f)) * 180f / MathF.PI;
        float capFrac = (1f - bestMinDot) / 2f;
        Line($"largest pentagon-free cap: radius {capRadiusDeg:F1} deg = {capFrac*100f:F1}% of sphere");
        Line($"  → at 600k tiles that's ~{(int)(capFrac*600252)} playable hexes, zero pentagons");
        Line("");

        // For caps centred on `best`, how many pentagons fall inside each coverage?
        Line("coverage% | pentagons inside the playable cap (centred to avoid them)");
        foreach (float cov in new[]{ 0.05f, 0.10f, 0.105f, 0.15f, 0.20f, 0.30f })
        {
            float cosCut = 1f - 2f * cov;
            int pentsInside = 0;
            foreach (var p in pents) if (Vector3.Dot(p, best.Center) >= cosCut) pentsInside++;
            Line($"   {cov*100,5:F1}% | {pentsInside}");
        }

        File.WriteAllText(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "pentagon-bench.txt"), sb.ToString());
    }
}
