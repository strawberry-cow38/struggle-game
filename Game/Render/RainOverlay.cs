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

    // Per-layer base fall speeds (layer-uv units/sec) — must match the
    // layer calls in rain.gdshader. The scroll PHASE integrates on the
    // CPU (fall*dt per frame): fall varies with wind + intensity every
    // frame, and a shader-side TIME*fall would teleport the whole field
    // on every change ("tweaking out" while the weather drifts).
    private static readonly float[] BaseFall = { 16f, 12f, 9f };
    // Streak cell heights per layer (shader `len`) — used to wrap the
    // accumulated phase by a whole number of cells so float precision
    // holds over long sessions (one pattern re-roll every ~30+ min).
    private static readonly float[] CellLen = { 2.2f, 1.5f, 1.0f };
    private const float WrapCells = 16384f;
    private readonly float[] _scroll = new float[3];
    // Displayed wind chases the sim value MUCH slower than intensity —
    // slant changes read as the whole sheet leaning, so they must creep.
    // Gusts are expressed through the density waves instead (wave_phase).
    private const float WindFadeRate = 0.25f;
    // Visual wind authority: scale + cap on the slant so even storm wind
    // tilts the streaks gently instead of shoving the plane around.
    private const float SlantScale = 0.45f;
    private const float SlantMax = 0.18f;
    private Vector2 _displayedWind;
    private float _wavePhase;

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
        float tw = 1f - Mathf.Exp(-(float)delta * WindFadeRate);
        _displayedIntensity = Mathf.Lerp(_displayedIntensity, target, t);
        _displayedWind = _displayedWind.Lerp(new Vector2(snap.RainWindX, snap.RainWindY), tw);
        if (_displayedIntensity < 0.002f && target <= 0f)
        {
            _displayedIntensity = 0f;
            _rect.Visible = false; // fully faded out — skip the fragment cost
            return;
        }
        _rect.Visible = true;

        // Integrate each layer's fall phase (see BaseFall comment) and
        // derive the matching slant. Mirrors the old in-shader formula:
        // fall = max(base * speed_k + windY, 2), slant = windX / fall.
        float speedK = 0.8f + 0.4f * _displayedIntensity;
        var scroll = new Vector3();
        var slant = new Vector3();
        for (int i = 0; i < 3; i++)
        {
            float fall = Mathf.Max(BaseFall[i] * speedK + _displayedWind.Y, 2f);
            float wrap = CellLen[i] * WrapCells;
            _scroll[i] = (_scroll[i] + fall * (float)delta) % wrap;
            float sl = Mathf.Clamp(_displayedWind.X * SlantScale / fall, -SlantMax, SlantMax);
            if (i == 0) { scroll.X = _scroll[i]; slant.X = sl; }
            else if (i == 1) { scroll.Y = _scroll[i]; slant.Y = sl; }
            else { scroll.Z = _scroll[i]; slant.Z = sl; }
        }

        // Gust waves roll faster (and feel choppier) in heavier weather.
        // Wrap at Tau*10: both shader wave frequencies (1.0 and 0.7) hit a
        // whole cycle there (10 / 7 turns), so the wrap is seamless.
        _wavePhase = (_wavePhase + (0.35f + 0.65f * _displayedIntensity) * (float)delta) % (Mathf.Tau * 10f);

        _material.SetShaderParameter("intensity", _displayedIntensity);
        _material.SetShaderParameter("scroll", scroll);
        _material.SetShaderParameter("slant", slant);
        _material.SetShaderParameter("wave_phase", _wavePhase);
        if (Camera is not null)
        {
            _material.SetShaderParameter("camera_center", Camera.GetScreenCenterPosition());
            _material.SetShaderParameter("camera_zoom", Camera.Zoom.X);
        }
        _material.SetShaderParameter("viewport_size", GetViewport().GetVisibleRect().Size);
        _material.SetShaderParameter("pixels_per_tile", (float)SimConstants.PixelsPerTile);
    }
}
