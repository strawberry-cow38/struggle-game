using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Active only when ToolMode == CutPlants. LMB-drag draws a rect; on
// release posts CutPlantsInRectCommand which queues one job per
// immature tree / crop (any growth) in the rect.
public partial class CutPlantsDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color FillColor = new(0.95f, 0.85f, 0.30f, 0.20f);
    private static readonly Color BorderColor = new(1.0f, 0.92f, 0.40f, 0.85f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;

    public override void _Ready()
    {
        ZIndex = 52;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.CutPlants, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.CutPlants)
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
                Host.QueueCommand(new CutPlantsInRectCommand(_startTile, _currentTile));
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
