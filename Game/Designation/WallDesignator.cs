using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Left-mouse drag in the world places a line of wall blueprints. The
// preview line follows the cursor in real time; on release the line is
// queued as PlaceWallBlueprintCommand for each tile.
public partial class WallDesignator : Node2D
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    private static readonly Color PreviewColor = new(1.0f, 0.85f, 0.20f, 0.45f);
    private static readonly Color PreviewBorder = new(1.0f, 0.85f, 0.20f, 0.90f);

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;
    private bool _hovering;
    private TilePos _hoverTile;

    public override void _Ready()
    {
        ZIndex = 50;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;
        if (Tools is null || Tools.Mode != ToolMode.BuildWall)
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
                CommitLine();
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
            foreach (var t in CardinalLine(_startTile, _currentTile))
            {
                DrawTilePreview(t);
            }
        }
        else if (_hovering)
        {
            DrawTilePreview(_hoverTile);
        }
    }

    private void DrawTilePreview(TilePos t)
    {
        var rect = new Rect2(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, PreviewColor, filled: true);
        DrawRect(rect, PreviewBorder, filled: false, width: 2f);
    }

    private void CommitLine()
    {
        foreach (var t in CardinalLine(_startTile, _currentTile))
        {
            Host!.QueueCommand(new PlaceWallBlueprintCommand(t));
        }
    }

    private TilePos MouseToTile()
    {
        var world = GetGlobalMousePosition();
        return new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
    }

    // Snap to the dominant cardinal axis — walls only run N/S or E/W.
    private static IEnumerable<TilePos> CardinalLine(TilePos a, TilePos b)
    {
        int dx = b.X - a.X;
        int dy = b.Y - a.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            int sx = dx >= 0 ? 1 : -1;
            int steps = Math.Abs(dx);
            for (int i = 0; i <= steps; i++) yield return new TilePos(a.X + i * sx, a.Y);
        }
        else
        {
            int sy = dy >= 0 ? 1 : -1;
            int steps = Math.Abs(dy);
            for (int i = 0; i <= steps; i++) yield return new TilePos(a.X, a.Y + i * sy);
        }
    }
}
