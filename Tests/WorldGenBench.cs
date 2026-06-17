using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using StruggleGame.Sim.World;
using Xunit;
using Xunit.Abstractions;

namespace StruggleGame.Tests;

// Not a pass/fail test — a benchmark. Times HexPlanet generation as the sphere
// scales toward RimWorld's 100% world (~600k tiles), and shows the coverage
// system's speedup (a smaller world = same sphere, fewer tiles get biomes).
// Run: dotnet test --filter WorldGenBench  (results also written to
// %USERPROFILE%/worldgen-bench.txt for easy retrieval).
public class WorldGenBench
{
    private readonly ITestOutputHelper _out;
    public WorldGenBench(ITestOutputHelper o){ _out = o; }

    [Fact]
    public void Bench()
    {
        var sb = new StringBuilder();
        void Line(string s){ _out.WriteLine(s); sb.AppendLine(s); }

        Line("=== HexPlanet generation benchmark ===");
        Line($"machine ts (UTC build): see file");
        Line("");
        Line("freq | tiles    | full-gen ms | approx MB");
        Line("-----+----------+-------------+----------");
        // scaling curve up to RimWorld-100% (N=245 ≈ 600k tiles)
        foreach (var f in new[]{ 51, 120, 180, 245 })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long m0 = GC.GetTotalMemory(true);
            var t = Stopwatch.StartNew();
            var p = new HexPlanet(f, 1337, 1f);
            t.Stop();
            long m1 = GC.GetTotalMemory(false);
            Line($"{f,4} | {p.TileCount,8} | {t.ElapsedMilliseconds,11} | {(m1 - m0) / 1048576,8}");
            GC.KeepAlive(p);
        }

        Line("");
        Line("=== coverage % @ RimWorld-100% sphere (N=245, ~600k tiles) ===");
        Line("the sphere is ALWAYS full; coverage only changes how many tiles get biomes:");
        foreach (var cov in new[]{ 1f, 0.5f, 0.3f })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var t = Stopwatch.StartNew();
            var p = new HexPlanet(HexPlanet.RimWorld100Frequency, 1337, cov);
            t.Stop();
            Line($"coverage {cov,4:P0}: {t.ElapsedMilliseconds,6} ms | generated {p.GeneratedTileCount,8} / {p.TileCount} tiles");
            GC.KeepAlive(p);
        }

        var outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "worldgen-bench.txt");
        File.WriteAllText(outPath, sb.ToString());
        _out.WriteLine($"\n(results written to {outPath})");
    }
}
