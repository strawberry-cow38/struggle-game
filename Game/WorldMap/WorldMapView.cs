using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.WorldMap;

// 3D view of the HexPlanet. Far out it's a smooth matte hex globe you orbit;
// zoom in past a threshold and a vertex shader MORPHS the whole planet into a
// flat equirectangular hex map, centered on whatever you were looking at and
// facing the camera (master's "squish the hexes into a flat 2D map on zoom").
//
// The morph is per-vertex in a shader: each vertex carries its sphere position
// (VERTEX) and its lon/lat (UV2); the shader lerps between the sphere point and
// a flat plane position by a `morph` uniform. Centering + the facing plane are
// frozen when the unwrap begins so the viewed region stays put as it flattens.
public partial class WorldMapView : Node3D
{
    public string? CaptureDir;            // non-null → auto-orbit + screenshot then quit
    public int Frequency = 51;            // 10*N²+2 = 26012 tiles (~10x of N=16)
    public int Seed = 1337;

    private const float R = 2.0f;
    private const float FlattenStart = R * 1.95f; // begin unwrap at this distance
    private const float FlattenEnd = R * 1.25f;   // fully flat by here

    private HexPlanet _planet = null!;
    private Node3D _pivot = null!;
    private Camera3D _cam = null!;
    private ShaderMaterial _mat = null!;
    private float _yaw = 0.6f, _pitch = 0.45f, _dist = R * 3.2f;
    private bool _dragging;
    private int _currentTile;

    private float _morph;
    private bool _flatFrozen;             // have we captured the unwrap framing?
    private Vector3 _flatNorth = Vector3.Up;

    public override void _Ready()
    {
        _planet = new HexPlanet(Frequency, Seed);
        _currentTile = _planet.NearestTile(new System.Numerics.Vector3(0.25f, 0.65f, 0.35f));

        BuildEnvironment();
        BuildPlanetMesh();
        BuildCurrentTilePin();
        BuildCameraRig();
        UpdateOrbit();

        if (!string.IsNullOrEmpty(CaptureDir))
            _ = RunCapture();
    }

    private void BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.05f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.66f, 0.70f, 0.82f),
            AmbientLightEnergy = 0.95f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52, -38, 0),
            LightEnergy = 0.85f,
            ShadowEnabled = false,
        };
        AddChild(sun);
    }

    private static Vector3 G(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    // Per-vertex morph shader: lerp sphere position → flat-map position by `morph`.
    // Local-area flatten: azimuthal-equidistant projection centered on the view
    // direction. Each vertex's angular distance theta from the centre maps to a
    // flat radius (scale*theta) in the camera-facing east/north plane, so the
    // region you're looking at flattens 1:1 (crisp, undistorted) with no poles
    // or date-line seam (only the antipode behind you wraps, off-screen).
    private const string ShaderCode = @"
shader_type spatial;
render_mode cull_disabled;
uniform float morph : hint_range(0.0, 1.0) = 0.0;
uniform vec3 view_dir = vec3(0.0, 0.0, 1.0);
uniform vec3 flat_right = vec3(1.0, 0.0, 0.0);
uniform vec3 flat_up = vec3(0.0, 1.0, 0.0);
uniform vec3 flat_center = vec3(0.0);
uniform float flat_scale = 2.0;
void vertex() {
    vec3 d = normalize(VERTEX);
    float c = clamp(dot(d, view_dir), -1.0, 1.0);
    float theta = acos(c);
    vec3 t = d - view_dir * c;            // tangential component
    float tl = length(t);
    vec2 dir2 = tl > 1e-5 ? vec2(dot(t, flat_right), dot(t, flat_up)) / tl : vec2(0.0);
    vec2 f = (flat_scale * theta) * dir2;
    vec3 flat_pos = flat_center + f.x * flat_right + f.y * flat_up;
    VERTEX = mix(VERTEX, flat_pos, morph);
    NORMAL = normalize(mix(NORMAL, view_dir, morph));
}
void fragment() {
    ALBEDO = COLOR.rgb;
    ROUGHNESS = 1.0;
    METALLIC = 0.0;
    SPECULAR = 0.0;
}
";

    private void BuildPlanetMesh()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        foreach (var tile in _planet.Tiles)
        {
            Vector3 outward = G(tile.Center).Normalized();
            Vector3 center = outward * R;

            Color col = BiomeColor(tile.Biome);
            if (tile.Index == _currentTile) col = col.Lerp(new Color(1f, 1f, 1f), 0.35f);

            int n = tile.Corners.Length;
            var ring = new Vector3[n];
            for (int k = 0; k < n; k++)
                ring[k] = G(tile.Corners[k]).Normalized() * R;

            for (int k = 0; k < n; k++)
            {
                Vector3 a = center, b = ring[k], c = ring[(k + 1) % n];
                AddVert(st, a, col);
                AddVert(st, b, col);
                AddVert(st, c, col);
            }
        }

        var mesh = st.Commit();
        _mat = new ShaderMaterial { Shader = new Shader { Code = ShaderCode } };
        var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _mat };
        AddChild(mi);
    }

    // Vertex normal = its own sphere direction (smooth shading). UV2 = the
    // vertex's lon/lat (radians), longitude unwrapped to the tile centre so a
    // tile's verts stay contiguous across the date-line.
    // Vertex normal = its own sphere direction (smooth shading). The flat-map
    // position is derived in the shader from the sphere position, so no extra
    // per-vertex data is needed.
    private static void AddVert(SurfaceTool st, Vector3 v, Color c)
    {
        st.SetColor(c);
        st.SetNormal(v.Normalized());
        st.AddVertex(v);
    }

    private void BuildCurrentTilePin()
    {
        var t = _planet.Tiles[_currentTile];
        Vector3 outward = G(t.Center).Normalized();
        var pin = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0f, BottomRadius = 0.09f * R, Height = 0.26f * R, RadialSegments = 12 },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(1f, 0.85f, 0.15f),
                EmissionEnabled = true,
                Emission = new Color(1f, 0.8f, 0.1f),
                EmissionEnergyMultiplier = 2.2f,
            },
        };
        Vector3 yAxis = -outward;
        Vector3 helper = Mathf.Abs(yAxis.Y) < 0.99f ? Vector3.Up : Vector3.Right;
        Vector3 xAxis = helper.Cross(yAxis).Normalized();
        Vector3 zAxis = xAxis.Cross(yAxis).Normalized();
        pin.Transform = new Transform3D(new Basis(xAxis, yAxis, zAxis), outward * R + outward * (0.30f * R));
        // The pin only makes sense on the globe; hide it once flattened.
        _pin = pin;
        AddChild(pin);
    }

    private MeshInstance3D _pin = null!;

    private void BuildCameraRig()
    {
        _pivot = new Node3D();
        AddChild(_pivot);
        _cam = new Camera3D { Current = true, Fov = 45f };
        _pivot.AddChild(_cam);
    }

    private void UpdateOrbit()
    {
        _pitch = Mathf.Clamp(_pitch, -1.45f, 1.45f);
        _dist = Mathf.Clamp(_dist, R * 1.05f, R * 8f);
        _pivot.Rotation = new Vector3(_pitch, _yaw, 0f);
        _cam.Position = new Vector3(0f, 0f, _dist);
        _cam.LookAt(Vector3.Zero, Vector3.Up);   // provisional → gives correct GlobalPosition

        UpdateMorph();                            // may freeze the flat framing (reads cam pos)

        // On the flat map, roll the camera so its up is the map's north → the
        // map reads upright instead of tilted to the orbit angle.
        if (_morph > 0.001f)
            _cam.LookAt(Vector3.Zero, _flatNorth);
    }

    // Map zoom distance → morph amount, and freeze the unwrap framing (centre +
    // camera-facing plane) the moment the unwrap begins so the viewed region
    // stays put as it flattens.
    private void UpdateMorph()
    {
        float m = Mathf.Clamp((FlattenStart - _dist) / (FlattenStart - FlattenEnd), 0f, 1f);

        if (m > 0f && !_flatFrozen)
        {
            FreezeFlatFraming();
            _flatFrozen = true;
        }
        else if (m <= 0f)
        {
            _flatFrozen = false;
        }

        _morph = m;
        if (_mat is not null) _mat.SetShaderParameter("morph", m);
        if (_pin is not null) _pin.Visible = m < 0.5f;
    }

    private void FreezeFlatFraming()
    {
        Vector3 viewDir = _cam.GlobalPosition.Normalized();   // projection centre (point facing camera)

        // North-up / east-right framing in the camera-facing plane.
        Vector3 north = Vector3.Up - viewDir * Vector3.Up.Dot(viewDir);
        north = north.LengthSquared() < 1e-5f ? Vector3.Forward : north.Normalized();
        Vector3 east = north.Cross(viewDir).Normalized();
        _flatNorth = north;

        // Map sits at the origin (where the camera is already looking) so the
        // zoomed-in camera frames a good region.
        _mat.SetShaderParameter("view_dir", viewDir);
        _mat.SetShaderParameter("flat_right", east);
        _mat.SetShaderParameter("flat_up", north);
        _mat.SetShaderParameter("flat_center", Vector3.Zero);
        _mat.SetShaderParameter("flat_scale", R);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left) _dragging = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.WheelUp) { _dist *= 0.9f; UpdateOrbit(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown) { _dist *= 1.1f; UpdateOrbit(); }
        }
        else if (e is InputEventMouseMotion mm && _dragging && _morph <= 0.001f)
        {
            // Orbit only while we're still a globe; once unwrapping, the framing
            // is frozen (pan across the flat map is a later addition).
            _yaw -= mm.Relative.X * 0.006f;
            _pitch -= mm.Relative.Y * 0.006f;
            UpdateOrbit();
        }
    }

    private static Color BiomeColor(Biome b) => b switch
    {
        Biome.Ocean     => new Color(0.10f, 0.26f, 0.52f),
        Biome.Beach     => new Color(0.85f, 0.78f, 0.55f),
        Biome.Grassland => new Color(0.46f, 0.62f, 0.28f),
        Biome.Forest    => new Color(0.17f, 0.40f, 0.20f),
        Biome.Desert    => new Color(0.83f, 0.71f, 0.40f),
        Biome.Savanna   => new Color(0.69f, 0.64f, 0.30f),
        Biome.Tundra    => new Color(0.62f, 0.62f, 0.57f),
        Biome.Taiga     => new Color(0.24f, 0.42f, 0.34f),
        Biome.Mountain  => new Color(0.44f, 0.40f, 0.38f),
        Biome.Snow      => new Color(0.92f, 0.94f, 0.97f),
        _               => new Color(1f, 0f, 1f),
    };

    private async Task RunCapture()
    {
        Directory.CreateDirectory(CaptureDir!);
        // First a few globe angles, then zoom all the way in to capture the
        // flattened map so a screenshot can verify both ends of the morph.
        var frames = new (float yaw, float dist)[]
        {
            (0.4f, R * 3.2f), (2.0f, R * 3.2f), (4.0f, R * 3.2f),
            (0.4f, R * 1.7f),                       // mid-morph
            (0.4f, R * 1.1f), (3.2f, R * 1.1f),     // fully flat
        };
        for (int i = 0; i < frames.Length; i++)
        {
            _yaw = frames[i].yaw;
            _pitch = 0.4f;
            _dist = frames[i].dist;
            UpdateOrbit();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = GetViewport().GetTexture().GetImage();
            string path = Path.Combine(CaptureDir!, $"worldmap_{i:D2}.png");
            img.SavePng(path);
            GD.Print($"[worldmap] shot {i} (dist={_dist:0.0} morph={_morph:0.00}) -> {path}");
        }
        GetTree().Quit();
    }
}
