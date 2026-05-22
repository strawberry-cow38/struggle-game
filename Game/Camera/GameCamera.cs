using Godot;
using StruggleGame.Sim;

namespace StruggleGame.Game.Camera;

// Camera2D with middle-mouse-button pan and discrete zoom levels stepped
// by the scroll wheel. Zoom tweens briefly toward the target; pan is
// direct (mouse-locked) so it feels 1:1.
public partial class GameCamera : Camera2D
{
    private static readonly float[] ZoomLevels = new[]
    {
        0.0625f, 0.08f, 0.1f, 0.125f, 0.16f, 0.2f, 0.25f, 0.32f, 0.4f, 0.5f,
        0.625f, 0.75f, 0.875f, 1.0f, 1.15f, 1.3f, 1.5f, 1.75f, 2.0f, 2.5f,
        3.0f, 3.5f, 4.0f,
    };

    private const int DefaultZoomIndex = 13; // 1.0
    // 25% of the original 0.15s tween — short snap with a hint of ease.
    private const float ZoomTweenSeconds = 0.0375f;

    // Screen-space pan speed at zoom 1.0 (pixels per second). Scaled by
    // 1/zoom each frame so the world appears to scroll at a consistent
    // rate regardless of how zoomed in/out you are.
    private const float KeyPanPxPerSec = 1200f;
    private const float ShiftBoostMultiplier = 3.0f;

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
        ApplyKeyPan((float)delta);

        if (!Mathf.IsEqualApprox(Zoom.X, _targetZoom))
        {
            // Frame-rate-independent exponential smoothing toward target.
            float t = 1f - Mathf.Exp(-(float)delta / ZoomTweenSeconds);
            float next = Mathf.Lerp(Zoom.X, _targetZoom, t);
            if (Mathf.Abs(next - _targetZoom) < 0.001f) next = _targetZoom;
            Zoom = new Vector2(next, next);
        }

        ClampToMap();
    }

    private static void GetMapBounds(out float worldPx)
    {
        worldPx = SimConstants.MapSize * SimConstants.PixelsPerTile;
    }

    private void ClampToMap()
    {
        GetMapBounds(out float worldPx);
        Position = new Vector2(
            Mathf.Clamp(Position.X, 0f, worldPx),
            Mathf.Clamp(Position.Y, 0f, worldPx));
    }

    private void ApplyKeyPan(float delta)
    {
        var input = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) input.Y -= 1f;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) input.Y += 1f;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) input.X -= 1f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) input.X += 1f;
        if (input == Vector2.Zero) return;

        input = input.Normalized();
        float speed = KeyPanPxPerSec;
        if (Input.IsKeyPressed(Key.Shift)) speed *= ShiftBoostMultiplier;
        Position += input * speed * delta / Zoom.X;
    }

    private void SetZoomIndex(int idx)
    {
        idx = Mathf.Clamp(idx, 0, ZoomLevels.Length - 1);
        if (idx == _zoomIndex) return;
        _zoomIndex = idx;
        _targetZoom = ZoomLevels[idx];
    }
}
