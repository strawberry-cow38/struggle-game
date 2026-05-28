using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.Designation;

// Active only when ToolMode == Bed. Cursor previews a 2x1 bed footprint
// at the hovered tile, oriented by `_orientation`. Q / `,` rotate left,
// E / `.` rotate right. Preview turns red when either tile is blocked.
// LMB places via PlaceBedCommand.
public partial class BedDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color OkFill   = new(0.55f, 0.35f, 0.20f, 0.45f);
    private static readonly Color OkBorder = new(0.85f, 0.65f, 0.40f, 0.95f);
    private static readonly Color BadFill   = new(0.85f, 0.20f, 0.15f, 0.45f);
    private static readonly Color BadBorder = new(1.00f, 0.45f, 0.35f, 0.95f);
    private static readonly Color PillowOk  = new(0.95f, 0.95f, 0.92f, 0.85f);
    private static readonly Color PillowBad = new(1.00f, 0.85f, 0.80f, 0.85f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private TilePos _hoverTile;
    private bool _hovering;
    private BedOrientation _orientation = BedOrientation.East;

    public override void _Ready()
    {
        ZIndex = 55;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.Bed, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.Bed)
        {
            if (_hovering) { _hovering = false; QueueRedraw(); }
            return;
        }

        if (@event is InputEventMouseMotion)
        {
            var t = MouseToTile();
            if (!_hovering || t != _hoverTile)
            {
                _hoverTile = t;
                _hovering = true;
                QueueRedraw();
            }
        }
        else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            var tile = MouseToTile();
            Host.QueueCommand(new PlaceBedCommand(tile, _orientation));
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey k && k.Pressed && !k.Echo)
        {
            // Q / , rotate counter-clockwise; E / . rotate clockwise.
            // Cycle N → E → S → W → N (CW).
            if (k.Keycode == Key.Q || k.Keycode == Key.Comma)
            {
                _orientation = RotateCCW(_orientation);
                QueueRedraw();
                GetViewport().SetInputAsHandled();
            }
            else if (k.Keycode == Key.E || k.Keycode == Key.Period)
            {
                _orientation = RotateCW(_orientation);
                QueueRedraw();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public override void _Draw()
    {
        if (!_hovering || Host is null) return;
        var origin = _hoverTile;
        var foot = BedOrientations.Foot(origin, _orientation);
        bool ok = Host.CanPlaceBed(origin, _orientation);
        var fill = ok ? OkFill : BadFill;
        var border = ok ? OkBorder : BadBorder;
        var pillow = ok ? PillowOk : PillowBad;

        var headRect = TileRect(origin);
        var footRect = TileRect(foot);
        DrawRect(headRect, fill, filled: true);
        DrawRect(footRect, fill, filled: true);

        // Pillow square inside the head tile so rotation is legible.
        float inset = PixelsPerTile * 0.18f;
        var pillowRect = new Rect2(
            headRect.Position.X + inset,
            headRect.Position.Y + inset,
            PixelsPerTile - 2 * inset,
            PixelsPerTile - 2 * inset);
        DrawRect(pillowRect, pillow, filled: true);

        DrawRect(headRect, border, filled: false, width: 2f);
        DrawRect(footRect, border, filled: false, width: 2f);
    }

    private static Rect2 TileRect(TilePos t)
        => new(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);

    private static BedOrientation RotateCW(BedOrientation o) => (BedOrientation)(((int)o + 1) & 0b11);
    private static BedOrientation RotateCCW(BedOrientation o) => (BedOrientation)(((int)o + 3) & 0b11);

    private void ClearPreview()
    {
        if (!_hovering) return;
        _hovering = false;
        QueueRedraw();
    }

    private TilePos MouseToTile()
    {
        var world = GetGlobalMousePosition();
        return new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
    }
}
