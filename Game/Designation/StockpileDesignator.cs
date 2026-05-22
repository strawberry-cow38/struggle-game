using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Handles the three stockpile drag-rect verbs:
//   Stockpile        → CreateStockpileRectCommand (new zone)
//   StockpileExpand  → ExpandStockpileRectCommand on the selected zone
//   StockpileShrink  → ShrinkStockpileRectCommand on the selected zone
// Expand/Shrink modes are entered through the StockpilePanel buttons —
// they target Host.SelectedStockpileId. After the drag completes the
// designator auto-flips Tools.Mode back to None so the panel-driven
// edit is one-shot (matches Rimworld muscle memory).
public partial class StockpileDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color CreateFill = new(0.95f, 0.85f, 0.25f, 0.18f);
    private static readonly Color CreateBorder = new(1.00f, 0.90f, 0.35f, 0.95f);
    private static readonly Color ExpandFill = new(0.35f, 0.85f, 0.45f, 0.20f);
    private static readonly Color ExpandBorder = new(0.55f, 1.00f, 0.65f, 0.95f);
    private static readonly Color ShrinkFill = new(0.95f, 0.35f, 0.35f, 0.20f);
    private static readonly Color ShrinkBorder = new(1.00f, 0.55f, 0.55f, 0.95f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;

    public override void _Ready()
    {
        ZIndex = 53;
    }

    private bool IsStockpileMode(ToolMode m) =>
        m == ToolMode.Stockpile || m == ToolMode.StockpileExpand || m == ToolMode.StockpileShrink;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || !IsStockpileMode(Tools.Mode))
        {
            if (_dragging) { _dragging = false; QueueRedraw(); }
            return;
        }

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _startTile = MouseToTile();
                _currentTile = _startTile;
                _dragging = true;
                QueueRedraw();
                GetViewport().SetInputAsHandled();
            }
            else if (_dragging)
            {
                _currentTile = MouseToTile();
                IssueCommand(Tools.Mode);
                _dragging = false;
                QueueRedraw();
                GetViewport().SetInputAsHandled();

                // Expand/Shrink return to None so the panel UX feels
                // one-shot; Create stays active so the player can drop
                // several zones in a row.
                if (Tools.Mode != ToolMode.Stockpile) Tools.Mode = ToolMode.None;
            }
        }
        else if (@event is InputEventMouseMotion && _dragging)
        {
            var t = MouseToTile();
            if (t != _currentTile)
            {
                _currentTile = t;
                QueueRedraw();
            }
        }
    }

    private void IssueCommand(ToolMode mode)
    {
        if (mode == ToolMode.Stockpile)
        {
            Host!.QueueCommand(new CreateStockpileRectCommand(_startTile, _currentTile));
            return;
        }
        if (Host!.SelectedStockpileId is not int id) return;
        if (mode == ToolMode.StockpileExpand)
            Host.QueueCommand(new ExpandStockpileRectCommand(id, _startTile, _currentTile));
        else if (mode == ToolMode.StockpileShrink)
            Host.QueueCommand(new ShrinkStockpileRectCommand(id, _startTile, _currentTile));
    }

    public override void _Draw()
    {
        if (!_dragging || Tools is null) return;
        int xmin = Math.Min(_startTile.X, _currentTile.X);
        int ymin = Math.Min(_startTile.Y, _currentTile.Y);
        int xmax = Math.Max(_startTile.X, _currentTile.X);
        int ymax = Math.Max(_startTile.Y, _currentTile.Y);
        var rect = new Rect2(
            xmin * PixelsPerTile,
            ymin * PixelsPerTile,
            (xmax - xmin + 1) * PixelsPerTile,
            (ymax - ymin + 1) * PixelsPerTile);

        Color fill = CreateFill, border = CreateBorder;
        if (Tools.Mode == ToolMode.StockpileExpand) { fill = ExpandFill; border = ExpandBorder; }
        else if (Tools.Mode == ToolMode.StockpileShrink) { fill = ShrinkFill; border = ShrinkBorder; }
        DrawRect(rect, fill, filled: true);
        DrawRect(rect, border, filled: false, width: 2f);
    }

    private TilePos MouseToTile()
    {
        var world = GetGlobalMousePosition();
        return new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
    }
}
