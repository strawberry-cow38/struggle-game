using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Active only when ToolMode == Door. LMB-click places a single door
// blueprint on the hovered tile. Orientation is auto-picked from the
// flanking walls on the sim side. Preview is a small swing-arc hint.
public partial class DoorDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color PreviewFill = new(0.55f, 0.40f, 0.85f, 0.30f);
    private static readonly Color PreviewBorder = new(0.85f, 0.65f, 1.00f, 0.85f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private TilePos _hoverTile;
    private bool _hovering;

    public override void _Ready()
    {
        ZIndex = 55;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.Door, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.Door)
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
            Host.QueueCommand(new PlaceDoorBlueprintCommand(tile));
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Draw()
    {
        if (!_hovering) return;
        var rect = new Rect2(_hoverTile.X * PixelsPerTile, _hoverTile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, PreviewFill, filled: true);
        DrawRect(rect, PreviewBorder, filled: false, width: 2f);
    }

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
