using Godot;
using StruggleGame.Game.Designation;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Debug;

// Active when ToolMode == DebugSpawnItem. LMB-click drops a single
// pile of the currently selected debug item onto the hovered tile.
// The selection is owned by DebugBar (static slot below) — picking
// a new item from the popup updates Current then flips the mode on.
public partial class DebugSpawnItemDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color PreviewFill = new(0.20f, 0.80f, 1.00f, 0.30f);
    private static readonly Color PreviewBorder = new(0.55f, 0.95f, 1.00f, 0.85f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    // Item to spawn on click. Defaults to first registered item if null
    // at click time; cleared/changed via DebugBar's picker.
    public static ItemDef? Current { get; set; }
    public static int Count { get; set; } = 1;

    private TilePos _hoverTile;
    private bool _hovering;

    public override void _Ready()
    {
        ZIndex = 55;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.DebugSpawnItem, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.DebugSpawnItem)
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
            var item = Current;
            if (item is null) return;
            var tile = MouseToTile();
            Host.QueueCommand(new DebugSpawnItemCommand(tile, item.FullPath, Count <= 0 ? 1 : Count));
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
