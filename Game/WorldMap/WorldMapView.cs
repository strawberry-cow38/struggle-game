using System.IO;
using System.Threading.Tasks;
using Godot;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.WorldMap;

// 3D view of the HexPlanet: builds the Goldberg-sphere mesh (biome-colored
// tiles), an orbiting camera, lighting, and a pin marker on the "current"
// tile. Drag to orbit, wheel to zoom. When CaptureDir is set the view auto-
// orbits, saves a few PNGs, and quits (used by the --worldmap harness so it
// can be screenshotted headlessly-ish on the build box).
public partial class WorldMapView : Node3D
{
    // Set by Bootstrap from --harness-out=. Non-null → capture mode.
    public string? CaptureDir;
    public int Frequency = 51; // 10*N²+2 = 26012 tiles (~10x of N=16)
    public int Seed = 1337;

    private const float R = 2.0f;          // planet radius (Godot units)
    // 0 = corners stay at their true (shared) positions so adjacent tiles meet
    // edge-to-edge with no gaps. Per-tile flat shading still defines the hexes.
    private const float BorderInset = 0.0f;

    private HexPlanet _planet = null!;
    private Node3D _pivot = null!;
    private Camera3D _cam = null!;
    private float _yaw = 0.6f, _pitch = 0.45f, _dist = R * 3.2f;
    private bool _dragging;
    private int _currentTile;

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

    // ---- scene build ---------------------------------------------------------

    private void BuildEnvironment()
    {
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.05f),
            // Ambient must read from our Color, NOT the (near-black) background —
            // otherwise the night side of the globe goes pitch black.
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.66f, 0.70f, 0.82f),
            AmbientLightEnergy = 0.95f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
        };
        AddChild(new WorldEnvironment { Environment = env });

        // Gentle key light for form. Shadows OFF: the only shadow-caster is the
        // marker pin, and its cast shadow streaks an ugly black wedge across the
        // tiles. Even, readable tiles matter more than self-shadowing here.
        var sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52, -38, 0),
            LightEnergy = 0.85f,
            ShadowEnabled = false,
        };
        AddChild(sun);
    }

    private static Vector3 G(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    private void BuildPlanetMesh()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        foreach (var tile in _planet.Tiles)
        {
            Vector3 outward = G(tile.Center).Normalized();
            // Subtle relief: land rises a touch above sea level.
            float rTile = R * (1f + Mathf.Max(0f, tile.Elevation) * 0.04f);
            Vector3 center = outward * rTile;

            Color col = BiomeColor(tile.Biome);
            if (tile.Index == _currentTile) col = col.Lerp(new Color(1f, 1f, 1f), 0.35f);
            // Edge color = slightly darker. Center-bright / edge-dark makes each
            // hex read as a subtly raised "pillow" so the tiles stay visible on
            // the otherwise smooth, seamless sphere.
            Color edge = new Color(col.R * 0.78f, col.G * 0.78f, col.B * 0.78f);

            int n = tile.Corners.Length;
            var ring = new Vector3[n];
            for (int k = 0; k < n; k++)
            {
                Vector3 corner = G(tile.Corners[k]).Normalized();
                // inset toward the tile centre direction, then re-project to rTile
                Vector3 inset = corner.Lerp(outward, BorderInset).Normalized();
                ring[k] = inset * rTile;
            }

            for (int k = 0; k < n; k++)
            {
                Vector3 a = center, b = ring[k], c = ring[(k + 1) % n];
                // Smooth (spherical) normals: each vertex's normal is its own
                // outward sphere direction. Coincident corners shared by adjacent
                // tiles get the same normal, so lighting flows smoothly across
                // tile edges and the whole thing reads as one smooth sphere.
                // The center-bright / edge-dark vertex colors keep the hexes
                // visible. Cull is disabled, so triangle winding is irrelevant.
                AddVert(st, a, a.Normalized(), col);
                AddVert(st, b, b.Normalized(), edge);
                AddVert(st, c, c.Normalized(), edge);
            }
        }

        var mesh = st.Commit();
        var mi = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                Roughness = 0.9f,
                Metallic = 0.0f,
                // Render both faces. The per-tile winding doesn't match Godot's
                // front-face convention (the globe rendered inside-out), and a
                // convex sphere can't show its interior anyway (the near,
                // outward-normal faces always win the depth test). Foolproof.
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(mi);
    }

    private static void AddVert(SurfaceTool st, Vector3 v, Vector3 n, Color c)
    {
        st.SetColor(c);
        st.SetNormal(n);
        st.AddVertex(v);
    }

    private void BuildCurrentTilePin()
    {
        var t = _planet.Tiles[_currentTile];
        Vector3 outward = G(t.Center).Normalized();
        Vector3 basePos = outward * R * (1f + Mathf.Max(0f, t.Elevation) * 0.04f);

        // A cone (cylinder w/ zero top radius) hovering above the tile, tip down.
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
        // Orient local +Y (cone tip) to point DOWN toward the planet.
        Vector3 yAxis = -outward;
        Vector3 helper = Mathf.Abs(yAxis.Y) < 0.99f ? Vector3.Up : Vector3.Right;
        Vector3 xAxis = helper.Cross(yAxis).Normalized();
        Vector3 zAxis = xAxis.Cross(yAxis).Normalized();
        var basis = new Basis(xAxis, yAxis, zAxis);
        // Hover so the tip sits just above the tile surface.
        pin.Transform = new Transform3D(basis, basePos + outward * (0.30f * R));
        AddChild(pin);
    }

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
        _dist = Mathf.Clamp(_dist, R * 1.4f, R * 8f);
        _pivot.Rotation = new Vector3(_pitch, _yaw, 0f);
        _cam.Position = new Vector3(0f, 0f, _dist);
        _cam.LookAt(Vector3.Zero, Vector3.Up);
    }

    // ---- interactive orbit ---------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left) _dragging = mb.Pressed;
            else if (mb.ButtonIndex == MouseButton.WheelUp) { _dist *= 0.9f; UpdateOrbit(); }
            else if (mb.ButtonIndex == MouseButton.WheelDown) { _dist *= 1.1f; UpdateOrbit(); }
        }
        else if (e is InputEventMouseMotion mm && _dragging)
        {
            _yaw -= mm.Relative.X * 0.006f;
            _pitch -= mm.Relative.Y * 0.006f;
            UpdateOrbit();
        }
    }

    // ---- biome palette -------------------------------------------------------

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

    // ---- capture (for the --worldmap harness) --------------------------------

    private async Task RunCapture()
    {
        Directory.CreateDirectory(CaptureDir!);
        float[] yaws = { 0.4f, 1.7f, 3.0f, 4.3f, 5.6f };
        for (int i = 0; i < yaws.Length; i++)
        {
            _yaw = yaws[i];
            _pitch = 0.5f;
            UpdateOrbit();
            // Let a couple of frames render before grabbing the backbuffer.
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            var img = GetViewport().GetTexture().GetImage();
            string path = Path.Combine(CaptureDir!, $"worldmap_{i:D2}.png");
            img.SavePng(path);
            GD.Print($"[worldmap] shot {i} -> {path}  ({_planet.TileCount} tiles, {_planet.PentagonCount} pentagons)");
        }
        GetTree().Quit();
    }
}
