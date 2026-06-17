using System;
using System.Collections.Generic;
using System.Numerics;

namespace StruggleGame.Sim.World;

// A hex-tiled sphere (Goldberg polyhedron). Built by subdividing an
// icosahedron to "frequency" N (Class-I geodesic), projecting onto the unit
// sphere, then taking the DUAL: every geodesic vertex becomes a tile whose
// corners are the centroids of the triangles around it. The 12 original
// icosahedron vertices have 5 surrounding triangles → pentagons; every other
// vertex has 6 → hexagons. You cannot tile a sphere with only hexagons; the
// 12 pentagons are mathematically unavoidable (Euler characteristic), so this
// is a soccer-ball topology: 10*N²+2 tiles total, exactly 12 of them pentagons.
//
// Pure logic (System.Numerics, no Godot) so it lives in Sim, is seed-
// deterministic, and is unit-testable. The Game layer turns Tiles into a mesh.
public enum Biome
{
    Ocean,
    Beach,
    Grassland,
    Forest,
    Desert,
    Savanna,
    Tundra,
    Taiga,
    Mountain,
    Snow,
}

public sealed class WorldTile
{
    public int Index;
    public Vector3 Center;       // unit-sphere position of the tile centre
    public Vector3[] Corners;    // ordered ring (5 for pentagons, 6 for hexes)
    public int[] Neighbors;      // indices of edge-adjacent tiles
    public Biome Biome;
    public float Elevation;      // -1..1 (negative = below sea level)
    public float Moisture;       // 0..1
    // Coverage system (RimWorld-style): the sphere is ALWAYS full size; a world
    // of X% coverage only runs biome generation on X% of the tiles. The rest are
    // "null" tiles — ungenerated open water, cheap, no biome data.
    public bool Generated;       // false = null/water tile (outside the coverage region)
    public bool IsPentagon => Corners.Length == 5;

    public WorldTile(int index, Vector3 center, Vector3[] corners, int[] neighbors)
    {
        Index = index;
        Center = center;
        Corners = corners;
        Neighbors = neighbors;
    }
}

public sealed class HexPlanet
{
    public readonly int Frequency;
    public readonly int Seed;
    public readonly float Coverage;     // 0..1 fraction of tiles with real biomes
    public readonly WorldTile[] Tiles;

    // ~RimWorld 100% world ≈ 600k tiles. 10*N²+2 = 600252 at N=245.
    public const int RimWorld100Frequency = 245;

    public int TileCount => Tiles.Length;
    public int GeneratedTileCount
    {
        get { int n = 0; foreach (var t in Tiles) if (t.Generated) n++; return n; }
    }
    public int PentagonCount
    {
        get { int n = 0; foreach (var t in Tiles) if (t.IsPentagon) n++; return n; }
    }

    // frequency = sphere subdivision (tile count, ALWAYS full for a given world
    // scale). coverage = fraction of those tiles that get real biome generation
    // (the rest are null/water). So a "30% world" has the SAME sphere as a 100%
    // world — it just generates biomes on 30% of the tiles.
    public HexPlanet(int frequency = 12, int seed = 1337, float coverage = 1f)
    {
        if (frequency < 1) frequency = 1;
        Frequency = frequency;
        Seed = seed;
        Coverage = Math.Clamp(coverage, 0f, 1f);

        var (verts, faces) = BuildGeodesic(frequency);
        Tiles = BuildDual(verts, faces);
        AssignBiomes(seed, Coverage);
    }

    // ---- icosahedron ---------------------------------------------------------

    private static (Vector3[] verts, int[][] faces) Icosahedron()
    {
        float t = (1f + MathF.Sqrt(5f)) / 2f;
        var v = new[]
        {
            N(-1,  t,  0), N( 1,  t,  0), N(-1, -t,  0), N( 1, -t,  0),
            N( 0, -1,  t), N( 0,  1,  t), N( 0, -1, -t), N( 0,  1, -t),
            N( t,  0, -1), N( t,  0,  1), N(-t,  0, -1), N(-t,  0,  1),
        };
        var f = new[]
        {
            new[]{0,11,5},  new[]{0,5,1},   new[]{0,1,7},   new[]{0,7,10},  new[]{0,10,11},
            new[]{1,5,9},   new[]{5,11,4},  new[]{11,10,2}, new[]{10,7,6},  new[]{7,1,8},
            new[]{3,9,4},   new[]{3,4,2},   new[]{3,2,6},   new[]{3,6,8},   new[]{3,8,9},
            new[]{4,9,5},   new[]{2,4,11},  new[]{6,2,10},  new[]{8,6,7},   new[]{9,8,1},
        };
        return (v, f);
    }

    private static Vector3 N(float x, float y, float z) => Vector3.Normalize(new Vector3(x, y, z));

    // ---- geodesic subdivision (Class I) -------------------------------------

    // Subdivide each icosa face into N² small triangles, project to the sphere,
    // dedup shared vertices, and return the merged vertex list + triangle list.
    private static (List<Vector3> verts, List<int[]> faces) BuildGeodesic(int n)
    {
        var verts = new List<Vector3>();
        var faces = new List<int[]>();
        // Quantized-position dedup: shared-edge vertices computed from two faces
        // are equal in exact arithmetic; quantize to merge the float drift.
        var lookup = new Dictionary<(long, long, long), int>();
        const float Q = 1_000_000f;

        int AddVert(Vector3 p)
        {
            p = Vector3.Normalize(p);
            var key = ((long)MathF.Round(p.X * Q), (long)MathF.Round(p.Y * Q), (long)MathF.Round(p.Z * Q));
            if (lookup.TryGetValue(key, out int idx)) return idx;
            idx = verts.Count;
            verts.Add(p);
            lookup[key] = idx;
            return idx;
        }

        var (ico, icoFaces) = Icosahedron();
        foreach (var face in icoFaces)
        {
            Vector3 a = ico[face[0]], b = ico[face[1]], c = ico[face[2]];
            // Grid of points P(i,j), i=0..n rows, j=0..i within the row.
            var grid = new int[n + 1][];
            for (int i = 0; i <= n; i++)
            {
                grid[i] = new int[i + 1];
                for (int j = 0; j <= i; j++)
                {
                    float wa = (n - i) / (float)n;
                    float wb = (i - j) / (float)n;
                    float wc = j / (float)n;
                    grid[i][j] = AddVert(a * wa + b * wb + c * wc);
                }
            }
            // Triangulate between consecutive rows.
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    faces.Add(new[] { grid[i][j], grid[i + 1][j], grid[i + 1][j + 1] }); // up
                    if (j < i)
                        faces.Add(new[] { grid[i][j], grid[i + 1][j + 1], grid[i][j + 1] }); // down
                }
            }
        }
        return (verts, faces);
    }

    // ---- dual (tiles) --------------------------------------------------------

    private static WorldTile[] BuildDual(List<Vector3> verts, List<int[]> faces)
    {
        int vn = verts.Count;
        var vertFaces = new List<int>[vn];
        var vertNeighbors = new HashSet<int>[vn];
        for (int i = 0; i < vn; i++) { vertFaces[i] = new List<int>(6); vertNeighbors[i] = new HashSet<int>(); }

        for (int fi = 0; fi < faces.Count; fi++)
        {
            var f = faces[fi];
            for (int k = 0; k < 3; k++)
            {
                int v = f[k];
                vertFaces[v].Add(fi);
                vertNeighbors[v].Add(f[(k + 1) % 3]);
                vertNeighbors[v].Add(f[(k + 2) % 3]);
            }
        }

        var tiles = new WorldTile[vn];
        for (int v = 0; v < vn; v++)
        {
            Vector3 center = verts[v];
            // Tile corners = projected centroids of the faces around this vertex.
            var fids = vertFaces[v];
            var corners = new Vector3[fids.Count];
            for (int k = 0; k < fids.Count; k++)
            {
                var f = faces[fids[k]];
                Vector3 centroid = (verts[f[0]] + verts[f[1]] + verts[f[2]]) / 3f;
                corners[k] = Vector3.Normalize(centroid);
            }
            OrderRing(center, corners);

            var neigh = new int[vertNeighbors[v].Count];
            vertNeighbors[v].CopyTo(neigh);
            tiles[v] = new WorldTile(v, center, corners, neigh);
        }
        return tiles;
    }

    // Sort a tile's corners CCW (viewed from outside) around its centre, so the
    // ring forms a simple polygon the renderer can fan-triangulate.
    private static void OrderRing(Vector3 center, Vector3[] corners)
    {
        Vector3 up = center;
        Vector3 seed = MathF.Abs(up.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, seed));
        Vector3 bitangent = Vector3.Cross(up, tangent);
        Array.Sort(corners, (p, q) =>
        {
            float ap = MathF.Atan2(Vector3.Dot(p, bitangent), Vector3.Dot(p, tangent));
            float aq = MathF.Atan2(Vector3.Dot(q, bitangent), Vector3.Dot(q, tangent));
            return ap.CompareTo(aq);
        });
    }

    // ---- biomes --------------------------------------------------------------

    // Smooth, coherent fields over the sphere from a seeded sum of sine waves
    // (cheap spherical "noise", fully deterministic). Elevation picks land vs
    // ocean + mountains; moisture + latitude pick the land biome.
    private const int SmoothPasses = 2;   // neighbour-majority passes to de-speckle biomes

    private void AssignBiomes(int seed, float coverage)
    {
        // Fewer octaves + lower frequency = big coherent continents/regions instead
        // of small scattered blobs. (Was 8/6 dirs @ 1.7/2.3 → patchy/messy.)
        var elevDirs = SeededDirs(seed * 2 + 1, 5);
        var moistDirs = SeededDirs(seed * 2 + 7, 4);
        const float seaLevel = 0.02f;

        // Coverage region = a spherical cap around a seeded "world centre" dir,
        // sized so it contains ~coverage of the sphere. cap fraction =
        // (1-cosθ)/2, so cosθ_max = 1 - 2*coverage. Tiles inside get real biome
        // generation; tiles outside are null/water (no field eval = cheaper, so
        // a smaller world genuinely generates faster while keeping the full sphere).
        bool partial = coverage < 0.999f;
        Vector3 worldCentre = SeededDirs(seed * 3 + 5, 1)[0];
        worldCentre = Vector3.Normalize(worldCentre);
        float cosCut = 1f - 2f * coverage; // coverage=1 → -1 (whole sphere)

        foreach (var t in Tiles)
        {
            if (partial && Vector3.Dot(t.Center, worldCentre) < cosCut)
            {
                // outside coverage → null/water tile, skip the noise eval
                t.Generated = false;
                t.Biome = Biome.Ocean;
                t.Elevation = -1f;
                t.Moisture = 1f;
                continue;
            }
            float elev = Field(t.Center, elevDirs, 0.85f);
            float moist = (Field(t.Center, moistDirs, 1.1f) + 1f) * 0.5f; // 0..1
            float lat = MathF.Abs(t.Center.Y); // 0 equator .. 1 pole

            t.Generated = true;
            t.Elevation = elev;
            t.Moisture = moist;
            t.Biome = Classify(elev, moist, lat, seaLevel);
        }

        SmoothBiomes();
    }

    // Neighbour-majority relaxation: each generated tile takes the most common
    // biome among itself + its generated neighbours. Kills single-tile speckle and
    // ragged boundaries → clean coherent regions. Deterministic (reads prior pass).
    private void SmoothBiomes()
    {
        var cur = new Biome[Tiles.Length];
        for (int i = 0; i < Tiles.Length; i++) cur[i] = Tiles[i].Biome;
        int kinds = Enum.GetValues<Biome>().Length;
        var tally = new int[kinds];

        for (int pass = 0; pass < SmoothPasses; pass++)
        {
            var next = (Biome[])cur.Clone();
            foreach (var t in Tiles)
            {
                if (!t.Generated) continue;
                Array.Clear(tally);
                tally[(int)cur[t.Index]]++;
                foreach (int nb in t.Neighbors)
                    if (Tiles[nb].Generated) tally[(int)cur[nb]]++;
                int bestKind = (int)cur[t.Index], bestN = -1;
                for (int k = 0; k < kinds; k++)
                    if (tally[k] > bestN) { bestN = tally[k]; bestKind = k; }
                next[t.Index] = (Biome)bestKind;
            }
            cur = next;
        }
        for (int i = 0; i < Tiles.Length; i++) Tiles[i].Biome = cur[i];
    }

    private static Biome Classify(float elev, float moist, float lat, float seaLevel)
    {
        if (elev < seaLevel) return Biome.Ocean;
        if (elev < seaLevel + 0.03f) return Biome.Beach;
        if (elev > 0.55f) return Biome.Mountain;

        if (lat > 0.82f) return Biome.Snow;
        if (lat > 0.68f) return moist > 0.45f ? Biome.Taiga : Biome.Tundra;
        if (lat < 0.28f) // tropics
            return moist > 0.6f ? Biome.Forest : moist > 0.32f ? Biome.Savanna : Biome.Desert;
        // temperate
        return moist > 0.55f ? Biome.Forest : moist > 0.28f ? Biome.Grassland : Biome.Desert;
    }

    // value in [-1,1]: average of sines of (dir · p * freq).
    private static float Field(Vector3 p, Vector3[] dirs, float freq)
    {
        float s = 0f;
        foreach (var d in dirs) s += MathF.Sin(Vector3.Dot(d, p) * freq + d.X * 3.17f);
        return s / dirs.Length;
    }

    private static Vector3[] SeededDirs(int seed, int count)
    {
        var rng = new Random(seed);
        var dirs = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            // Uniform-ish directions on the sphere.
            double z = rng.NextDouble() * 2.0 - 1.0;
            double a = rng.NextDouble() * Math.PI * 2.0;
            double r = Math.Sqrt(1.0 - z * z);
            dirs[i] = new Vector3((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)), (float)z)
                      * (1f + i * 0.35f); // vary frequency per octave
        }
        return dirs;
    }

    // Find the tile whose centre is nearest a given direction (e.g. to place
    // the "current location" marker). dir need not be normalized.
    public int NearestTile(Vector3 dir)
    {
        dir = Vector3.Normalize(dir);
        int best = 0; float bestDot = float.NegativeInfinity;
        for (int i = 0; i < Tiles.Length; i++)
        {
            float d = Vector3.Dot(Tiles[i].Center, dir);
            if (d > bestDot) { bestDot = d; best = i; }
        }
        return best;
    }
}
