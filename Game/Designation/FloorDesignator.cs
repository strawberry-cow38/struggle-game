using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Active only when ToolMode == Floor. LMB-drag draws a filled rect;
// on release posts FloorRectBlueprintCommand which queues one
// FloorBuild blueprint per eligible tile.
public partial class FloorDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color FillColor = new(0.85f, 0.55f, 0.25f, 0.22f);
    private static readonly Color BorderColor = new(0.95f, 0.70f, 0.35f, 0.85f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;
    private bool _hovering;
    private TilePos _hoverTile;

    public override void _Ready()
    {
        ZIndex = 54;
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.Floor, ClearPreview);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.Floor)
        {
            if (_dragging || _hovering) { _dragging = false; _hovering = false; QueueRedraw(); }
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
                Host.QueueCommand(new FloorRectBlueprintCommand(_startTile, _currentTile));
                _dragging = false;
                QueueRedraw();
                GetViewport().SetInputAsHandled();
            }
        }
        else if (@event is InputEventMouseMotion)
        {
            var t = MouseToTile();
            if (_dragging)
            {
                if (t != _currentTile)
                {
                    _currentTile = t;
                    QueueRedraw();
                }
            }
            else if (!_hovering || t != _hoverTile)
            {
                _hoverTile = t;
                _hovering = true;
                QueueRedraw();
            }
        }
    }

    public override void _Draw()
    {
        if (_dragging)
        {
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
            DragMeasureOverlay.Draw(this, xmin, ymin, xmax, ymax);
        }
        else if (_hovering)
        {
            var rect = new Rect2(_hoverTile.X * PixelsPerTile, _hoverTile.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
            DrawRect(rect, FillColor, filled: true);
            DrawRect(rect, BorderColor, filled: false, width: 2f);
        }
    }

    private void ClearPreview()
    {
        if (!_dragging && !_hovering) return;
        _dragging = false;
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
