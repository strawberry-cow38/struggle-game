using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Shared skeleton for line-drag designators: LMB-press starts a drag,
// motion snaps a cardinal (N/S or E/W) line of tiles to the cursor, and
// LMB-release commits one item per tile. A single-tile hover preview shows
// when not dragging. Active only while ToolMode == Mode.
//
// Subclasses supply the mode, draw order, the per-tile commit, and the
// per-tile preview art (Wall = plain square, Sandbag = bag rows tinted by
// placement validity).
public abstract partial class LineDragDesignator : Node2D
{
    protected const int PixelsPerTile = SimConstants.PixelsPerTile;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private TilePos _startTile;
    private TilePos _currentTile;
    private bool _hovering;
    private TilePos _hoverTile;

    protected abstract ToolMode Mode { get; }
    protected abstract int ZOrder { get; }
    protected abstract void CommitTile(TilePos tile);
    protected abstract void DrawTilePreview(TilePos tile);

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
                foreach (var t in CardinalLine(_startTile, _currentTile)) CommitTile(t);
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
                if (t != _currentTile) { _currentTile = t; QueueRedraw(); }
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
            int xmin = int.MaxValue, ymin = int.MaxValue;
            int xmax = int.MinValue, ymax = int.MinValue;
            foreach (var t in CardinalLine(_startTile, _currentTile))
            {
                DrawTilePreview(t);
                if (t.X < xmin) xmin = t.X;
                if (t.Y < ymin) ymin = t.Y;
                if (t.X > xmax) xmax = t.X;
                if (t.Y > ymax) ymax = t.Y;
            }
            if (xmin <= xmax) DragMeasureOverlay.Draw(this, xmin, ymin, xmax, ymax);
        }
        else if (_hovering)
        {
            DrawTilePreview(_hoverTile);
        }
    }

    protected void ClearPreview()
    {
        if (!_dragging && !_hovering) return;
        _dragging = false;
        _hovering = false;
        QueueRedraw();
    }

    protected TilePos MouseToTile()
    {
        var world = GetGlobalMousePosition();
        return new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
    }

    // Snap to the dominant cardinal axis — lines only run N/S or E/W.
    protected static IEnumerable<TilePos> CardinalLine(TilePos a, TilePos b)
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
