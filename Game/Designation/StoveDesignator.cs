using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.Designation;

// Active only when ToolMode == Stove. Cursor previews a 3-tile body row
// + 1 perpendicular standing tile (T shape) at the hovered tile, oriented
// by `_orientation`. Q / `,` rotate left, E / `.` rotate right. Preview
// turns red when any tile is blocked. LMB places via PlaceStoveBlueprintCommand.
public partial class StoveDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color OkFill     = new(0.45f, 0.45f, 0.50f, 0.45f);
    private static readonly Color OkBorder   = new(0.85f, 0.85f, 0.90f, 0.95f);
    private static readonly Color BadFill    = new(0.85f, 0.20f, 0.15f, 0.45f);
    private static readonly Color BadBorder  = new(1.00f, 0.45f, 0.35f, 0.95f);
    private static readonly Color StandOk    = new(0.95f, 0.80f, 0.30f, 0.55f);
    private static readonly Color StandBad   = new(1.00f, 0.65f, 0.55f, 0.55f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private TilePos _hoverTile;
    private bool _hovering;
    private StoveOrientation _orientation = StoveOrientation.South;

    public override void _Ready()
    {
        ZIndex = 55;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.Stove, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.Stove)
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
            Host.QueueCommand(new PlaceStoveBlueprintCommand(tile, _orientation));
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey k && k.Pressed && !k.Echo)
        {
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
        bool ok = Host.CanPlaceStove(origin, _orientation);
        var fill = ok ? OkFill : BadFill;
        var border = ok ? OkBorder : BadBorder;
        var stand = ok ? StandOk : StandBad;

        foreach (var t in StoveOrientations.BodyTiles(origin, _orientation))
        {
            var r = TileRect(t);
            DrawRect(r, fill, filled: true);
            DrawRect(r, border, filled: false, width: 2f);
        }

        var standTile = StoveOrientations.StandingTile(origin, _orientation);
        var sr = TileRect(standTile);
        DrawRect(sr, stand, filled: true);
        DrawRect(sr, border, filled: false, width: 2f);

        // Mark center body tile (interaction spot) with a dot.
        var centerR = TileRect(origin);
        var dotPos = new Vector2(centerR.Position.X + PixelsPerTile * 0.5f, centerR.Position.Y + PixelsPerTile * 0.5f);
        DrawCircle(dotPos, PixelsPerTile * 0.12f, border);
    }

    private static Rect2 TileRect(TilePos t)
        => new(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);

    private static StoveOrientation RotateCW(StoveOrientation o) => (StoveOrientation)(((int)o + 1) & 0b11);
    private static StoveOrientation RotateCCW(StoveOrientation o) => (StoveOrientation)(((int)o + 3) & 0b11);

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
