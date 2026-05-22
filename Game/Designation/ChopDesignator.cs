using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Active only when ToolMode == Chop. LMB-drag draws a rect; on release
// posts ChopTreesInRectCommand which queues one ChopTree job per tree
// whose tile is inside.
public partial class ChopDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color FillColor = new(0.55f, 0.95f, 0.35f, 0.20f);
    private static readonly Color BorderColor = new(0.65f, 1.0f, 0.45f, 0.85f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;

    public override void _Ready()
    {
        ZIndex = 52;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.Chop)
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
                Host.QueueCommand(new ChopTreesInRectCommand(_startTile, _currentTile));
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

    public override void _Draw()
    {
        if (!_dragging) return;
        int xmin = Math.Min(_startTile.X, _currentTile.X);
        int ymin = Math.Min(_startTile.Y, _currentTile.Y);
        int xmax = Math.Max(_startTile.X, _currentTile.X);
        int ymax = Math.Max(_startTile.Y, _currentTile.Y);
        var rect = new Rect2(
            xmin * PixelsPerTile,
            ymin * PixelsPerTile,
            (xmax - xmin + 1) * PixelsPerTile,
            (ymax - ymin + 1) * PixelsPerTile);
        DrawRect(rect, FillColor, filled: true);
        DrawRect(rect, BorderColor, filled: false, width: 2f);
    }

    private TilePos MouseToTile()
    {
        var world = GetGlobalMousePosition();
        return new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
    }
}
