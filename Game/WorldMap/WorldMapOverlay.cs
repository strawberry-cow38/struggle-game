using Godot;
using StruggleGame.Game.UI;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.WorldMap;

// In-game world-map overlay (toggled by M). Renders the 3D PlanetShaderView into
// a SubViewport shown on a high CanvasLayer, so the globe covers the 2D colony
// AND its UI. The SubViewport gives the map its own 3D world (camera, light,
// environment) isolated from the game. Drag to orbit, wheel to zoom inside it.
public partial class WorldMapOverlay : CanvasLayer
{
    // In-game planet scale = full RimWorld-100% (freq 245 = 600,252 tiles). The
    // one-time lookup bake (~13s) freezes on first open; master accepted that —
    // a future "generate world" screen will own that step. Built once then cached
    // (SetActive hides/shows, never rebuilds), so it's a one-time first-open cost.
    public int Frequency = StruggleGame.Sim.World.HexPlanet.RimWorld100Frequency; // 245
    // Goldberg = beautiful REGULAR hexes (the lat-long PolarCap tiling makes the
    // tiles come out square-ish, not hex). The 12 pentagons render as normal
    // terrain (invisible specks among 600k tiles), so we keep the pretty hexes.
    public StruggleGame.Sim.World.HexPlanet.WorldGen Mode =
        StruggleGame.Sim.World.HexPlanet.WorldGen.Goldberg;
    // Equatorial playable band fraction. 0.85 → small polar no-go ice caps (~15%).
    public float Coverage = 0.85f;
    public bool AutoSelectDemo = false;   // harness: auto-select a tile for the screenshot

    private SubViewport _vp = null!;
    private PlanetShaderView _planet = null!;

    // Tile info panel (matches the game's dreamcore UiTheme glass panels).
    private Panel _info = null!;
    private ScanlineStyleBox _infoBox = null!;
    private Label _infoTitle = null!, _infoBody = null!;
    private double _glowT;

    public override void _Ready()
    {
        Layer = 200; // above all game UI

        var container = new SubViewportContainer
        {
            Stretch = true,
            MouseFilter = Control.MouseFilterEnum.Stop, // capture + forward to the viewport
        };
        container.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(container);

        _vp = new SubViewport
        {
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = true,
            UseTaa = true,                 // temporal AA — smooths hex edges + grain
        };
        container.AddChild(_vp);
        _planet = new PlanetShaderView { Frequency = Frequency, Coverage = Coverage, Mode = Mode, Name = "PlanetShaderView" };
        _planet.SelectionChanged += OnTileSelected;
        _vp.AddChild(_planet);

        BuildInfoPanel();

        if (AutoSelectDemo)
            Callable.From(() => _planet.DebugSelect(
                _planet.Planet.NearestTile(new System.Numerics.Vector3(0.25f, 0.45f, 0.5f)))).CallDeferred();

        // Close hint, top-left.
        var hint = new Label
        {
            Text = "World Map  ·  press M to close  ·  click a tile",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(22, 16),
        };
        hint.AddThemeFontSizeOverride("font_size", 18);
        hint.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
        AddChild(hint);
    }

    // Bottom-right glass info panel, same dreamcore styling as the in-game panes.
    private void BuildInfoPanel()
    {
        _info = new Panel { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _info.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        _info.Position = new Vector2(22, 56);
        _info.CustomMinimumSize = new Vector2(320, 168);
        _infoBox = UiTheme.PanelBox(corner: 12, margin: 10);
        _info.AddThemeStyleboxOverride("panel", _infoBox);
        AddChild(_info);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 14, OffsetTop = 12, OffsetRight = -14, OffsetBottom = -12,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        vbox.AddThemeConstantOverride("separation", 6);
        _info.AddChild(vbox);

        _infoTitle = new Label();
        _infoTitle.AddThemeFontSizeOverride("font_size", 22);
        _infoTitle.AddThemeColorOverride("font_color", UiTheme.Accent);
        vbox.AddChild(_infoTitle);
        vbox.AddChild(new HSeparator());
        _infoBody = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _infoBody.AddThemeFontSizeOverride("font_size", 16);
        _infoBody.AddThemeColorOverride("font_color", UiTheme.Text);
        vbox.AddChild(_infoBody);
    }

    private void OnTileSelected(int idx)
    {
        if (idx < 0) { _info.Visible = false; return; }
        var t = _planet.Planet.Tiles[idx];
        _infoTitle.Text = $"{t.Biome}";
        string hemi = t.LatitudeDeg >= 0 ? "N" : "S";
        int elevM = (int)Mathf.Round(t.Elevation * 4000f); // -1..1 → ~±4000 m
        _infoBody.Text =
            $"Tile #{t.Index}\n" +
            $"Latitude:  {Mathf.Abs(t.LatitudeDeg):0.0}° {hemi}\n" +
            $"Avg temp:  {t.TemperatureC:0.0} °C\n" +
            $"Rainfall:  {t.RainfallMm:0} mm/yr\n" +
            $"Elevation: {elevM} m\n" +
            $"Moisture:  {Mathf.Round(t.Moisture * 100f)}%";
        _info.Visible = true;
    }

    public override void _Process(double delta)
    {
        if (_info.Visible) { _glowT += delta; UiTheme.AnimateGlow(_infoBox, _glowT); }
    }

    // Show/hide without rebuilding the (large) mesh. Pauses the SubViewport's
    // rendering while hidden so it doesn't keep drawing the globe off-screen.
    public void SetActive(bool on)
    {
        Visible = on;
        _vp.RenderTargetUpdateMode = on
            ? SubViewport.UpdateMode.Always
            : SubViewport.UpdateMode.Disabled;
    }
}
