using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Active only when ToolMode == Sandbag. Cursor previews a 1-tile sandbag
// footprint at the hovered tile. Red when blocked. LMB places via
// PlaceSandbagCommand. Mirrors UrBoardDesignator.
public partial class SandbagDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color OkFill   = new(0.55f, 0.50f, 0.30f, 0.45f);
    private static readonly Color OkBorder = new(0.80f, 0.72f, 0.45f, 0.95f);
    private static readonly Color BadFill   = new(0.85f, 0.20f, 0.15f, 0.45f);
    private static readonly Color BadBorder = new(1.00f, 0.45f, 0.35f, 0.95f);
    private static readonly Color InlayOk   = new(0.72f, 0.66f, 0.42f, 0.85f);
    private static readonly Color InlayBad  = new(1.00f, 0.85f, 0.80f, 0.85f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private TilePos _hoverTile;
    private bool _hovering;

    public override void _Ready()
    {
        ZIndex = 55;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.Sandbag, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.Sandbag)
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
            Host.QueueCommand(new PlaceSandbagCommand(tile));
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Draw()
    {
        if (!_hovering || Host is null) return;
        bool ok = Host.CanPlaceSandbag(_hoverTile);
        var fill = ok ? OkFill : BadFill;
        var border = ok ? OkBorder : BadBorder;
        var inlay = ok ? InlayOk : InlayBad;

        var rect = TileRect(_hoverTile);
        DrawRect(rect, fill, filled: true);
        // Two stacked bag rows to read as a low barricade.
        float inset = PixelsPerTile * 0.14f;
        float rowH = (PixelsPerTile - 2 * inset) * 0.42f;
        var top = new Rect2(rect.Position.X + inset, rect.Position.Y + inset, PixelsPerTile - 2 * inset, rowH);
        var bot = new Rect2(rect.Position.X + inset, rect.Position.Y + PixelsPerTile - inset - rowH, PixelsPerTile - 2 * inset, rowH);
        DrawRect(top, inlay, filled: true);
        DrawRect(bot, inlay, filled: true);
        DrawRect(rect, border, filled: false, width: 2f);
    }

    private static Rect2 TileRect(TilePos t)
        => new(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);

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
