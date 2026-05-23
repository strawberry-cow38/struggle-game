using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Four roof drag-rect verbs (single node so we share the drag scaffold):
//   Roof        → PaintRoofRectCommand          (build a roof patch)
//   RemoveRoof  → RemoveRoofRectCommand         (strip roof flag)
//   NoRoof      → SetNoRoofRectCommand(mark=t)  (forbid auto-roof)
//   ClearNoRoof → SetNoRoofRectCommand(mark=f)  (re-allow auto-roof)
// Each verb stays sticky so the player can paint several rects before
// switching tools.
public partial class RoofDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color RoofFill = new(0.35f, 0.65f, 1.00f, 0.18f);
    private static readonly Color RoofBorder = new(0.55f, 0.80f, 1.00f, 0.95f);
    private static readonly Color RemoveFill = new(1.00f, 0.40f, 0.30f, 0.20f);
    private static readonly Color RemoveBorder = new(1.00f, 0.55f, 0.45f, 0.95f);
    private static readonly Color NoRoofFill = new(1.00f, 0.85f, 0.20f, 0.20f);
    private static readonly Color NoRoofBorder = new(1.00f, 0.95f, 0.35f, 0.95f);
    private static readonly Color ClearFill = new(0.55f, 0.55f, 0.55f, 0.20f);
    private static readonly Color ClearBorder = new(0.85f, 0.85f, 0.85f, 0.95f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;

    public override void _Ready()
    {
        ZIndex = 53;
    }

    private static bool IsRoofMode(ToolMode m) =>
        m == ToolMode.Roof || m == ToolMode.RemoveRoof
        || m == ToolMode.NoRoof || m == ToolMode.ClearNoRoof;

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || !IsRoofMode(Tools.Mode))
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
        switch (mode)
        {
            case ToolMode.Roof:
                Host!.QueueCommand(new PaintRoofRectCommand(_startTile, _currentTile));
                break;
            case ToolMode.RemoveRoof:
                Host!.QueueCommand(new RemoveRoofRectCommand(_startTile, _currentTile));
                break;
            case ToolMode.NoRoof:
                Host!.QueueCommand(new SetNoRoofRectCommand(_startTile, _currentTile, mark: true));
                break;
            case ToolMode.ClearNoRoof:
                Host!.QueueCommand(new SetNoRoofRectCommand(_startTile, _currentTile, mark: false));
                break;
        }
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

        Color fill = RoofFill, border = RoofBorder;
        switch (Tools.Mode)
        {
            case ToolMode.RemoveRoof:  fill = RemoveFill; border = RemoveBorder; break;
            case ToolMode.NoRoof:      fill = NoRoofFill; border = NoRoofBorder; break;
            case ToolMode.ClearNoRoof: fill = ClearFill;  border = ClearBorder;  break;
        }
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
