using Godot;

namespace StruggleGame.Game.Camera;

// Camera2D with middle-mouse-button pan and discrete zoom levels stepped
// by the scroll wheel. Zoom transitions tween smoothly toward the target;
// pan is direct (mouse-locked) so it feels 1:1.
public partial class GameCamera : Camera2D
{
    private static readonly float[] ZoomLevels = new[]
    {
        0.25f, 0.5f, 0.75f, 1.0f, 1.5f, 2.0f, 3.0f, 4.0f,
    };

    private const int DefaultZoomIndex = 3; // 1.0
    private const float ZoomTweenSeconds = 0.15f;

    private int _zoomIndex = DefaultZoomIndex;
    private float _targetZoom = ZoomLevels[DefaultZoomIndex];
    private bool _panning;

    public override void _Ready()
    {
        Zoom = new Vector2(_targetZoom, _targetZoom);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Middle)
            {
                _panning = mb.Pressed;
                GetViewport().SetInputAsHandled();
                return;
            }
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
            {
                SetZoomIndex(_zoomIndex + 1);
                GetViewport().SetInputAsHandled();
                return;
            }
            if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
            {
                SetZoomIndex(_zoomIndex - 1);
                GetViewport().SetInputAsHandled();
                return;
            }
        }
        else if (@event is InputEventMouseMotion mm && _panning)
        {
            // Drag in screen pixels — translate world by relative / zoom
            // so the world tracks the cursor 1:1.
            Position -= mm.Relative / Zoom;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (Mathf.IsEqualApprox(Zoom.X, _targetZoom)) return;

        // Exponential smoothing toward target. Frame-rate independent.
        float t = 1f - Mathf.Exp(-(float)delta / ZoomTweenSeconds);
        float next = Mathf.Lerp(Zoom.X, _targetZoom, t);
        if (Mathf.Abs(next - _targetZoom) < 0.001f) next = _targetZoom;
        Zoom = new Vector2(next, next);
    }

    private void SetZoomIndex(int idx)
    {
        idx = Mathf.Clamp(idx, 0, ZoomLevels.Length - 1);
        if (idx == _zoomIndex) return;
        _zoomIndex = idx;
        _targetZoom = ZoomLevels[idx];
    }
}
