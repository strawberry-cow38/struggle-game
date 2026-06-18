using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.WorldMap;

// PROTOTYPE of the "genius" planet renderer: instead of drawing 600k hex polygons,
// we bake a tile-ID LOOKUP texture (equirectangular: each texel = which tile covers
// that direction) once, then render ONE smooth sphere whose fragment shader samples
// the lookup → tile id → a tiny palette texture for the colour. Render cost is O(1)
// in tile count. Borders come from the screen-space change in id. (Equirect here for
// simplicity; a cubemap is the production upgrade to kill pole distortion.)
public partial class PlanetShaderView : Node3D
{
    public string? CaptureDir;
    public int Frequency = 64;     // 10*N^2+2 = 40,962 tiles (fast bake for the proto)
    public int Seed = 1337;
    public float Coverage = 1f;

    // All sized adaptively from tile count in _Ready (so it's correct from 41k to 600k+).
    private int TexW, TexH;   // equirect lookup resolution
    private int PalW;         // palette is PalW x PalW (must be ≥ tile count)
    private int CW, CH;       // coarse buckets for the nearest-tile bake

    private static int Pow2AtLeast(double v){ int p=1; while(p<v) p<<=1; return p; }
    private void SizeTextures(int tiles)
    {
        PalW = Pow2AtLeast(Math.Ceiling(Math.Sqrt(tiles)));            // ≥ tile count slots
        TexH = Math.Clamp(Pow2AtLeast(Math.Sqrt(tiles * 8.0)), 512, 2048);
        TexW = TexH * 2;                                               // ~8+ texels/tile
        CH = Math.Clamp(Pow2AtLeast(Math.Sqrt(tiles / 12.0)), 32, 256);
        CW = CH * 2;                                                  // ~few tiles/bucket
    }

    private HexPlanet _planet = null!;
    private ShaderMaterial _mat = null!;
    private Node3D _pivot = null!;
    private Camera3D _cam = null!;
    private float _yaw = 0.6f, _pitch = 0.4f, _dist = 5.0f;
    private bool _drag;
    private int _currentTile;

    public override void _Ready()
    {
        var swGen = System.Diagnostics.Stopwatch.StartNew();
        _planet = new HexPlanet(Frequency, Seed, Coverage);
        swGen.Stop();
        SizeTextures(_planet.TileCount);
        _currentTile = _planet.NearestTile(new System.Numerics.Vector3(0.25f, 0.6f, 0.4f));

        BuildEnvironment();
        var swBake = System.Diagnostics.Stopwatch.StartNew();
        var idTex = BakeIdTexture();
        swBake.Stop();
        GD.Print($"[planet-shader] tiles={_planet.TileCount} gen={swGen.ElapsedMilliseconds}ms bake={swBake.ElapsedMilliseconds}ms lookup={TexW}x{TexH} pal={PalW}");
        var palTex = BuildPalette();
        var cenTex = BuildCenters();
        BuildSphere(idTex, palTex, cenTex);
        BuildCamera();
        UpdateOrbit();

        if (!string.IsNullOrEmpty(CaptureDir)) _ = RunCapture();
    }

    private void BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.05f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.7f, 0.74f, 0.85f),
            AmbientLightEnergy = 1.0f,
        };
        AddChild(new WorldEnvironment { Environment = env });
        var sun = new DirectionalLight3D { RotationDegrees = new Vector3(-55, -40, 0), LightEnergy = 0.7f, ShadowEnabled = false };
        AddChild(sun);
    }

    // ---- direction <-> equirect uv (MUST match the shader) ----
    static System.Numerics.Vector3 DirFromUV(float u, float v)
    {
        float lon = u * MathF.Tau - MathF.PI;
        float lat = MathF.PI * 0.5f - v * MathF.PI;
        float cl = MathF.Cos(lat);
        return new System.Numerics.Vector3(cl * MathF.Sin(lon), MathF.Sin(lat), cl * MathF.Cos(lon));
    }
    static (float u, float v) UVFromDir(System.Numerics.Vector3 d)
    {
        float lon = MathF.Atan2(d.X, d.Z);
        float lat = MathF.Asin(Math.Clamp(d.Y, -1f, 1f));
        return ((lon + MathF.PI) / MathF.Tau, (MathF.PI * 0.5f - lat) / MathF.PI);
    }

    // Bake the equirect tile-id texture. For each texel, find the nearest tile centre
    // using a coarse (u,v) bucket grid so it's not O(texels*tiles).
    private ImageTexture BakeIdTexture()
    {
        var tiles = _planet.Tiles;
        // buckets
        var buckets = new System.Collections.Generic.List<int>[CW * CH];
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new System.Collections.Generic.List<int>();
        foreach (var t in tiles)
        {
            var (u, v) = UVFromDir(t.Center);
            int bx = Math.Clamp((int)(u * CW), 0, CW - 1);
            int by = Math.Clamp((int)(v * CH), 0, CH - 1);
            buckets[by * CW + bx].Add(t.Index);
        }

        // Bake the TOP-2 nearest tiles per texel (id1, id2) as floats. Storing the
        // 2nd-nearest is what makes crisp analytic edges robust: the shader draws
        // the boundary exactly where the two nearest tile centres tie — no tap
        // guessing, no neighbour table needed.
        var f = new float[TexW * TexH * 2];
        for (int y = 0; y < TexH; y++)
        {
            float v = (y + 0.5f) / TexH;
            int by = Math.Clamp((int)(v * CH), 0, CH - 1);
            bool pole = by <= 1 || by >= CH - 2;
            for (int x = 0; x < TexW; x++)
            {
                float u = (x + 0.5f) / TexW;
                int bx = Math.Clamp((int)(u * CW), 0, CW - 1);
                var dir = DirFromUV(u, v);

                int id1 = -1, id2 = -1; float d1 = -2f, d2 = -2f;
                int rxLo = pole ? 0 : bx - 2, rxHi = pole ? CW - 1 : bx + 2;
                for (int yy = by - 2; yy <= by + 2; yy++)
                {
                    if (yy < 0 || yy >= CH) continue;
                    for (int xx = rxLo; xx <= rxHi; xx++)
                    {
                        int wx = ((xx % CW) + CW) % CW;
                        foreach (int ti in buckets[yy * CW + wx])
                        {
                            float dot = System.Numerics.Vector3.Dot(dir, tiles[ti].Center);
                            if (dot > d1) { d2 = d1; id2 = id1; d1 = dot; id1 = ti; }
                            else if (dot > d2) { d2 = dot; id2 = ti; }
                        }
                    }
                }
                if (id1 < 0) id1 = 0;
                if (id2 < 0) id2 = id1;
                int o = (y * TexW + x) * 2;
                f[o] = id1; f[o + 1] = id2;
            }
        }
        var bytes = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(TexW, TexH, false, Image.Format.Rgf, bytes);
        return ImageTexture.CreateFromImage(img);
    }

    private ImageTexture BuildPalette()
    {
        var buf = new byte[PalW * PalW * 4];
        foreach (var t in _planet.Tiles)
        {
            var c = t.Generated ? BiomeColor(t.Biome) : new Color(0.08f, 0.22f, 0.46f); // null = deep water
            int id = t.Index, px = id % PalW, py = id / PalW;
            if (py >= PalW) continue;
            int o = (py * PalW + px) * 4;
            buf[o] = (byte)(c.R * 255); buf[o + 1] = (byte)(c.G * 255); buf[o + 2] = (byte)(c.B * 255); buf[o + 3] = 255;
        }
        var img = Image.CreateFromData(PalW, PalW, false, Image.Format.Rgba8, buf);
        return ImageTexture.CreateFromImage(img);
    }

    // id -> tile centre xyz, as a float texture (for the analytic Voronoi edges).
    private ImageTexture BuildCenters()
    {
        var f = new float[PalW * PalW * 4];
        foreach (var t in _planet.Tiles)
        {
            int id = t.Index, px = id % PalW, py = id / PalW;
            if (py >= PalW) continue;
            int o = (py * PalW + px) * 4;
            f[o] = t.Center.X; f[o + 1] = t.Center.Y; f[o + 2] = t.Center.Z; f[o + 3] = 0f;
        }
        var bytes = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(PalW, PalW, false, Image.Format.Rgbaf, bytes);
        return ImageTexture.CreateFromImage(img);
    }

    // Analytic-Voronoi crisp edges. The id_tex (cheap) only gives CANDIDATE tiles;
    // the true nearest tile + the boundary are computed per-pixel from the tile
    // CENTRE positions (centers tex) — so edges are math, razor-sharp at ANY zoom.
    // We tap the id texture in a small ring to discover the local neighbour tiles,
    // then pick nearest/2nd-nearest centre; the boundary is where they tie.
    private const string Shader = @"
shader_type spatial;
render_mode cull_back;
uniform sampler2D idpair : filter_nearest;     // r=nearest id, g=2nd-nearest id (floats)
uniform sampler2D palette : filter_nearest;
uniform sampler2D centers : filter_nearest;    // id -> tile centre xyz (Rgbaf)
uniform float pal_w = 256.0;
uniform float highlight_id = -1.0;
uniform float border = 0.55;
uniform float grain = 0.12;       // surface noise strength
uniform float tile_var = 0.10;    // per-tile brightness variation
varying vec3 v_dir;
void vertex(){ v_dir = normalize(VERTEX); }
vec2 palUV(float id){ return (vec2(mod(id,pal_w), floor(id/pal_w)) + 0.5) / pal_w; }
vec3 centreOf(float id){ return normalize(texture(centers, palUV(id)).xyz); }
// cheap hash + 3D value noise on the sphere direction (deterministic, no texture)
float hash31(vec3 p){ p = fract(p*0.3183099 + 0.1); p *= 17.0; return fract(p.x*p.y*p.z*(p.x+p.y+p.z)); }
float vnoise(vec3 x){
    vec3 i = floor(x), f = fract(x); f = f*f*(3.0-2.0*f);
    float n000=hash31(i+vec3(0,0,0)), n100=hash31(i+vec3(1,0,0));
    float n010=hash31(i+vec3(0,1,0)), n110=hash31(i+vec3(1,1,0));
    float n001=hash31(i+vec3(0,0,1)), n101=hash31(i+vec3(1,0,1));
    float n011=hash31(i+vec3(0,1,1)), n111=hash31(i+vec3(1,1,1));
    return mix(mix(mix(n000,n100,f.x),mix(n010,n110,f.x),f.y),
               mix(mix(n001,n101,f.x),mix(n011,n111,f.x),f.y), f.z);
}
float fbm(vec3 p){ return 0.5*vnoise(p) + 0.25*vnoise(p*2.03) + 0.125*vnoise(p*4.01); }
void fragment(){
    vec3 d = normalize(v_dir);
    float lon = atan(d.x, d.z);
    float lat = asin(clamp(d.y,-1.0,1.0));
    vec2 uv = vec2((lon+PI)/TAU, (PI*0.5 - lat)/PI);

    // the two nearest tiles are baked per texel → exact candidates, no guessing
    vec2 pr = texture(idpair, uv).rg;
    float id1 = pr.r, id2 = pr.g;
    vec3 col = texture(palette, palUV(id1)).rgb;

    // SURFACE TEXTURE so tiles read like terrain, not flat paint fills:
    // per-tile brightness variation (breaks up same-biome flatness) + a multi-
    // octave value-noise grain across the surface. Both deterministic from
    // position/id, no texture asset.
    float tv = hash31(vec3(id1*0.137, id1*0.0079, 3.1)) - 0.5;
    col *= 1.0 + tv * tile_var;
    float g = fbm(d * 90.0) - 0.5;          // fine grain
    g += (fbm(d * 22.0) - 0.5) * 0.6;       // coarser mottle
    col *= 1.0 + g * grain;

    // ANALYTIC edge: boundary is exactly where the two nearest centres tie. This
    // is computed from the real centre directions, so it stays razor-crisp at ANY
    // zoom regardless of the lookup texture resolution.
    float diff = dot(d, centreOf(id1)) - dot(d, centreOf(id2));
    float aa = fwidth(diff) * 1.5 + 1e-6;
    float edge = 1.0 - smoothstep(0.0, aa, diff);
    col = mix(col, col*0.4, edge*border);

    if (highlight_id >= 0.0 && abs(id1 - highlight_id) < 0.5) col = mix(col, vec3(1.0,0.93,0.3), 0.5);
    ALBEDO = clamp(col, 0.0, 1.0); ROUGHNESS = 1.0; SPECULAR = 0.0; METALLIC = 0.0;
}
";

    private void BuildSphere(Texture2D idTex, Texture2D palTex, Texture2D cenTex)
    {
        _mat = new ShaderMaterial { Shader = new Shader { Code = Shader } };
        _mat.SetShaderParameter("idpair", idTex);
        _mat.SetShaderParameter("palette", palTex);
        _mat.SetShaderParameter("centers", cenTex);
        _mat.SetShaderParameter("pal_w", (float)PalW);
        _mat.SetShaderParameter("highlight_id", (float)_currentTile);
        var mesh = new SphereMesh { Radius = 2f, Height = 4f, RadialSegments = 128, Rings = 96 };
        AddChild(new MeshInstance3D { Mesh = mesh, MaterialOverride = _mat });
    }

    private void BuildCamera()
    {
        _pivot = new Node3D(); AddChild(_pivot);
        _cam = new Camera3D { Current = true, Fov = 45f }; _pivot.AddChild(_cam);
    }
    private void UpdateOrbit()
    {
        _pitch = Mathf.Clamp(_pitch, -1.45f, 1.45f); _dist = Mathf.Clamp(_dist, 2.3f, 9f);
        _pivot.Rotation = new Vector3(_pitch, _yaw, 0);
        _cam.Position = new Vector3(0, 0, _dist); _cam.LookAt(Vector3.Zero, Vector3.Up);
    }
    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb){ if(mb.ButtonIndex==MouseButton.Left)_drag=mb.Pressed;
            else if(mb.ButtonIndex==MouseButton.WheelUp){_dist*=0.9f;UpdateOrbit();}
            else if(mb.ButtonIndex==MouseButton.WheelDown){_dist*=1.1f;UpdateOrbit();} }
        else if (e is InputEventMouseMotion mm && _drag){ _yaw-=mm.Relative.X*0.006f; _pitch-=mm.Relative.Y*0.006f; UpdateOrbit(); }
    }

    private static Color BiomeColor(Biome b) => b switch
    {
        Biome.Ocean=>new Color(0.10f,0.26f,0.52f), Biome.Beach=>new Color(0.85f,0.78f,0.55f),
        Biome.Grassland=>new Color(0.46f,0.62f,0.28f), Biome.Forest=>new Color(0.17f,0.40f,0.20f),
        Biome.Desert=>new Color(0.83f,0.71f,0.40f), Biome.Savanna=>new Color(0.69f,0.64f,0.30f),
        Biome.Tundra=>new Color(0.62f,0.62f,0.57f), Biome.Taiga=>new Color(0.24f,0.42f,0.34f),
        Biome.Mountain=>new Color(0.44f,0.40f,0.38f), Biome.Snow=>new Color(0.92f,0.94f,0.97f),
        _=>new Color(1,0,1),
    };

    private async Task RunCapture()
    {
        Directory.CreateDirectory(CaptureDir!);
        var frames = new (float yaw, float dist)[]{ (0.6f,5.0f),(2.2f,5.0f),(0.6f,3.0f),(0.6f,2.5f) };
        for (int i=0;i<frames.Length;i++)
        {
            _yaw=frames[i].yaw; _pitch=0.35f; _dist=frames[i].dist; UpdateOrbit();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(Path.Combine(CaptureDir!, $"planet_{i:D2}.png"));
            GD.Print($"[planet-shader] shot {i} dist={_dist:0.0} tiles={_planet.TileCount}");
        }
        GetTree().Quit();
    }
}
