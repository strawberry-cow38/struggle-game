using Godot;
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

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;

    public override void _Ready()
    {
        ZIndex = 50;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null) return;

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
        foreach (var t in Bresenham(_startTile, _currentTile))
        {
            DrawTilePreview(t);
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
        foreach (var t in Bresenham(_startTile, _currentTile))
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

    private static IEnumerable<TilePos> Bresenham(TilePos a, TilePos b)
    {
        int x0 = a.X, y0 = a.Y, x1 = b.X, y1 = b.Y;
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            yield return new TilePos(x0, y0);
            if (x0 == x1 && y0 == y1) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
