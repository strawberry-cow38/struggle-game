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
        BuildSphere(idTex, palTex);
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

        var buf = new byte[TexW * TexH * 4];
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

                int best = -1; float bestDot = -2f;
                // search neighbourhood (wrap in u, clamp in v); full u-row near poles
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
                            if (dot > bestDot) { bestDot = dot; best = ti; }
                        }
                    }
                }
                if (best < 0) best = 0;
                int o = (y * TexW + x) * 4;
                buf[o] = (byte)(best & 255);
                buf[o + 1] = (byte)((best >> 8) & 255);
                buf[o + 2] = (byte)((best >> 16) & 255);
                buf[o + 3] = 255;
            }
        }
        var img = Image.CreateFromData(TexW, TexH, false, Image.Format.Rgba8, buf);
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

    private const string Shader = @"
shader_type spatial;
render_mode cull_back;
uniform sampler2D id_tex : filter_nearest;
uniform sampler2D palette : filter_nearest;
uniform float pal_w = 256.0;
uniform float highlight_id = -1.0;
uniform float border = 0.35;
varying vec3 v_dir;
void vertex(){ v_dir = normalize(VERTEX); }
float decode(vec2 uv){ vec4 c = texture(id_tex, uv); return floor(c.r*255.0+0.5) + floor(c.g*255.0+0.5)*256.0 + floor(c.b*255.0+0.5)*65536.0; }
void fragment(){
    vec3 d = normalize(v_dir);
    float lon = atan(d.x, d.z);
    float lat = asin(clamp(d.y,-1.0,1.0));
    vec2 uv = vec2((lon+PI)/TAU, (PI*0.5 - lat)/PI);
    float id = decode(uv);
    float px = mod(id, pal_w);
    float py = floor(id / pal_w);
    vec3 col = texture(palette, (vec2(px,py)+0.5)/pal_w).rgb;
    // border: id changes across the pixel (only meaningful when zoomed in)
    float e = clamp(fwidth(id), 0.0, 1.0);
    col = mix(col, col*0.5, e*border);
    // highlight the current tile
    if (highlight_id >= 0.0 && abs(id - highlight_id) < 0.5) col = mix(col, vec3(1.0,0.93,0.3), 0.55);
    ALBEDO = col; ROUGHNESS = 1.0; SPECULAR = 0.0; METALLIC = 0.0;
}
";

    private void BuildSphere(Texture2D idTex, Texture2D palTex)
    {
        _mat = new ShaderMaterial { Shader = new Shader { Code = Shader } };
        _mat.SetShaderParameter("id_tex", idTex);
        _mat.SetShaderParameter("palette", palTex);
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
