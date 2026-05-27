using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.Designation;

// Three grow-zone drag-rect verbs (mirror of StockpileDesignator):
//   GrowZone        → CreateGrowZoneRectCommand (new zone, default Carrot)
//   GrowZoneExpand  → ExpandGrowZoneRectCommand on the selected zone
//   GrowZoneShrink  → ShrinkGrowZoneRectCommand on the selected zone
// Expand/Shrink are panel-driven one-shots; Create stays active so the
// player can paint several zones in a row.
public partial class GrowZoneDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color CreateFill = new(0.35f, 0.85f, 0.30f, 0.18f);
    private static readonly Color CreateBorder = new(0.45f, 1.00f, 0.40f, 0.95f);
    private static readonly Color ExpandFill = new(0.35f, 0.95f, 0.45f, 0.20f);
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
        if (Tools is not null) this.BindInputToMode(Tools, IsZoneMode, ClearPreview);
    }

    private static bool IsZoneMode(ToolMode m) =>
        m == ToolMode.GrowZone || m == ToolMode.GrowZoneExpand || m == ToolMode.GrowZoneShrink;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || !IsZoneMode(Tools.Mode))
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
                if (Tools.Mode != ToolMode.GrowZone) Tools.Mode = ToolMode.None;
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
        if (mode == ToolMode.GrowZone)
        {
            // Defaults: Carrot + AllowSowing=true + AllowCutting=false
            // (set in GrowZone ctor). Player tweaks from the panel.
            Host!.QueueCommand(new CreateGrowZoneRectCommand(_startTile, _currentTile, CropKind.Carrot));
            return;
        }
        if (Host!.SelectedGrowZoneId is not int id) return;
        if (mode == ToolMode.GrowZoneExpand)
            Host.QueueCommand(new ExpandGrowZoneRectCommand(id, _startTile, _currentTile));
        else if (mode == ToolMode.GrowZoneShrink)
            Host.QueueCommand(new ShrinkGrowZoneRectCommand(id, _startTile, _currentTile));
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
        if (Tools.Mode == ToolMode.GrowZoneExpand) { fill = ExpandFill; border = ExpandBorder; }
        else if (Tools.Mode == ToolMode.GrowZoneShrink) { fill = ShrinkFill; border = ShrinkBorder; }
        DrawRect(rect, fill, filled: true);
        DrawRect(rect, border, filled: false, width: 2f);
        DragMeasureOverlay.Draw(this, xmin, ymin, xmax, ymax);
    }

    private void ClearPreview()
    {
        if (!_dragging) return;
        _dragging = false;
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
