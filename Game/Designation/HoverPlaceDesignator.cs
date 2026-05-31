using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Shared skeleton for single-tile placement designators: hover previews a
// 1-tile footprint, LMB places it. Active only while ToolMode == Mode.
// Subclasses supply the mode, draw order, the place command (Place) and
// their own _Draw (the footprint preview art varies per buildable).
public abstract partial class HoverPlaceDesignator : Node2D
{
    protected const int PixelsPerTile = SimConstants.PixelsPerTile;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    protected TilePos HoverTile { get; private set; }
    protected bool Hovering { get; private set; }

    protected abstract ToolMode Mode { get; }
    protected abstract int ZOrder { get; }
    protected abstract void Place(TilePos tile);

    public override void _Ready()
    {
        ZIndex = ZOrder;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == Mode, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != Mode)
        {
            if (Hovering) { Hovering = false; QueueRedraw(); }
            return;
        }

        if (@event is InputEventMouseMotion)
        {
            var t = MouseToTile();
            if (!Hovering || t != HoverTile)
            {
                HoverTile = t;
                Hovering = true;
                QueueRedraw();
            }
        }
        else if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && mb.Pressed)
        {
            Place(MouseToTile());
            GetViewport().SetInputAsHandled();
        }
    }

    protected void ClearPreview()
    {
        if (!Hovering) return;
        Hovering = false;
        QueueRedraw();
    }

    protected TilePos MouseToTile()
    {
        var world = GetGlobalMousePosition();
        return new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
    }

    protected static Rect2 TileRect(TilePos t)
        => new(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
}
