using Godot;

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
    // Equatorial playable band as a fraction of the sphere. 0.8 → |lat|≲53°, so
    // the polar no-go ice caps are modest (~20% of the planet) instead of huge.
    // Tradeoff: the 10 ring-pentagons (±26.57°) are now INSIDE the playable band
    // (shown bright red for now) — only the 2 pole pentagons stay buried. Dialling
    // this down toward 0.45 grows the caps until all 12 are buried again.
    public float Coverage = 0.8f;

    private SubViewport _vp = null!;

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
        _vp.AddChild(new PlanetShaderView { Frequency = Frequency, Coverage = Coverage, Name = "PlanetShaderView" });

        // Close hint, top-left.
        var hint = new Label
        {
            Text = "World Map  ·  press M to close",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = new Vector2(22, 16),
        };
        hint.AddThemeFontSizeOverride("font_size", 18);
        hint.AddThemeColorOverride("font_color", new Color(0.92f, 0.94f, 1f));
        AddChild(hint);
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
