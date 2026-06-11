using Godot;
using StruggleGame.Sim;

namespace StruggleGame.Game.Render;

// Fullscreen procedural rain overlay (rain.gdshader on a ColorRect).
// Pure visual: reads RainIntensity + wind from the latest snapshot each
// frame, smooths the displayed intensity so fronts fade in/out instead
// of popping, and feeds the shader the camera center/zoom so the streak
// field stays anchored to world coordinates while the camera moves.
//
// Layer 10: above the world (WorldRenderer draws in the default canvas)
// and the visual lighting, below every UI layer (Toolbar starts at 90).
public partial class RainOverlay : CanvasLayer
{
    public SimHost? Host;
    public Camera2D? Camera;

    // Displayed intensity chases the sim value with this rate (1/sec) —
    // ~2s to close most of the gap, so a front rolling in reads as a
    // fade, and the debug presets still respond promptly.
    private const float FadeRate = 1.5f;

    private ColorRect? _rect;
    private ShaderMaterial? _material;
    private float _displayedIntensity;

    public override void _Ready()
    {
        Layer = 10;

        var shader = GD.Load<Shader>("res://Game/Render/rain.gdshader");
        _material = new ShaderMaterial { Shader = shader };
        _rect = new ColorRect
        {
            Name = "Rain",
            Material = _material,
            // Never eat mouse input — pan/zoom/designators live underneath.
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
        };
        _rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
    }

    public override void _Process(double delta)
    {
        if (_rect is null || _material is null) return;
        var snap = Host?.LatestSnapshot;
        if (snap is null) return;

        float target = snap.RainIntensity;
        float t = 1f - Mathf.Exp(-(float)delta * FadeRate);
        _displayedIntensity = Mathf.Lerp(_displayedIntensity, target, t);
        if (_displayedIntensity < 0.002f && target <= 0f)
        {
            _displayedIntensity = 0f;
            _rect.Visible = false; // fully faded out — skip the fragment cost
            return;
        }
        _rect.Visible = true;

        _material.SetShaderParameter("intensity", _displayedIntensity);
        _material.SetShaderParameter("wind", new Vector2(snap.RainWindX, snap.RainWindY));
        if (Camera is not null)
        {
            _material.SetShaderParameter("camera_center", Camera.GetScreenCenterPosition());
            _material.SetShaderParameter("camera_zoom", Camera.Zoom.X);
        }
        _material.SetShaderParameter("viewport_size", GetViewport().GetVisibleRect().Size);
        _material.SetShaderParameter("pixels_per_tile", (float)SimConstants.PixelsPerTile);
    }
}
