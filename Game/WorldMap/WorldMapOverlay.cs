using Godot;

namespace StruggleGame.Game.WorldMap;

// In-game world-map overlay (toggled by M). Renders the 3D WorldMapView into a
// SubViewport shown on a high CanvasLayer, so the globe covers the 2D colony
// AND its UI. The SubViewport gives the map its own 3D world (camera, light,
// environment) isolated from the game. Drag to orbit, wheel to zoom inside it.
public partial class WorldMapOverlay : CanvasLayer
{
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
        };
        container.AddChild(_vp);
        _vp.AddChild(new WorldMapView { Name = "WorldMapView" });

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
