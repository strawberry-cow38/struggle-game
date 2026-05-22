using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.Selection;

// LMB click in the world (when no other tool is active) picks the
// nearest pawn within PickRadiusPx; if none, the nearest tree; if
// neither, clears all selection. Shift+LMB on a tree toggles it in/out
// of the multi-tree selection. Double-click LMB selects every tree in
// the camera viewport (Cities-Skylines style).
//
// RMB on a drafted colonist's selection issues a move order to the
// clicked tile. Shift+RMB appends to the order queue instead of
// replacing it.
public partial class Selector : Node2D
{
    private const float PickRadiusPx = SimConstants.PixelsPerTile * 0.6f;
    private const int PixelsPerTile = SimConstants.PixelsPerTile;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null) return;
        if (Tools.Mode != ToolMode.None) return;
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;

        if (mb.ButtonIndex == MouseButton.Left)
        {
            HandleSelect(mb.ShiftPressed, mb.DoubleClick);
            GetViewport().SetInputAsHandled();
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            if (HandleOrder(mb.ShiftPressed))
            {
                GetViewport().SetInputAsHandled();
            }
        }
    }

    private void HandleSelect(bool shift, bool doubleClick)
    {
        var snap = Host!.LatestSnapshot;
        if (snap is null) return;

        if (doubleClick)
        {
            SelectAllTreesInView(snap);
            return;
        }

        var world = GetGlobalMousePosition();

        // Pawn beats tree if both are within radius.
        if (TryPickPawn(snap, world, out int pawnId))
        {
            Host.SelectedDummyId = pawnId;
            Host.SelectedTreeIds = Array.Empty<int>();
            return;
        }

        if (TryPickTree(snap, world, out int treeId))
        {
            var set = shift ? new HashSet<int>(Host.SelectedTreeIds) : new HashSet<int>();
            if (shift && !set.Add(treeId)) set.Remove(treeId);
            else set.Add(treeId);
            WriteTreeSelection(set);
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            return;
        }

        // Stockpile zone tile under cursor (any zone tile counts).
        var clickTile = new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
        if (TryPickStockpile(snap, clickTile, out int stockId))
        {
            Host.SelectedStockpileId = stockId;
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            return;
        }

        // Empty click — clear all selection (unless shift, which preserves).
        if (!shift)
        {
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
        }
    }

    private bool TryPickStockpile(SimSnapshot snap, TilePos tile, out int id)
    {
        id = -1;
        foreach (var sp in snap.Stockpiles)
        {
            foreach (var t in sp.Tiles)
            {
                if (t == tile) { id = sp.Id; return true; }
            }
        }
        return false;
    }

    private bool TryPickPawn(SimSnapshot snap, Vector2 world, out int id)
    {
        id = -1;
        float bestDistSq = PickRadiusPx * PickRadiusPx;
        foreach (var d in snap.Dummies)
        {
            float px = d.X * PixelsPerTile;
            float py = d.Y * PixelsPerTile;
            float dx = px - world.X;
            float dy = py - world.Y;
            float distSq = dx * dx + dy * dy;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                id = d.EntityId;
            }
        }
        return id >= 0;
    }

    private bool TryPickTree(SimSnapshot snap, Vector2 world, out int id)
    {
        id = -1;
        float bestSq = (PixelsPerTile * 0.5f) * (PixelsPerTile * 0.5f);
        foreach (var t in snap.Trees)
        {
            float px = (t.Tile.X + 0.5f) * PixelsPerTile;
            float py = (t.Tile.Y + 0.5f) * PixelsPerTile;
            float dx = px - world.X;
            float dy = py - world.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestSq)
            {
                bestSq = d2;
                id = t.EntityId;
            }
        }
        return id >= 0;
    }

    private void SelectAllTreesInView(SimSnapshot snap)
    {
        var vp = GetViewport().GetVisibleRect();
        var canvasXform = GetCanvasTransform().AffineInverse();
        var topLeft = canvasXform * vp.Position;
        var bottomRight = canvasXform * (vp.Position + vp.Size);
        float minX = Mathf.Min(topLeft.X, bottomRight.X);
        float maxX = Mathf.Max(topLeft.X, bottomRight.X);
        float minY = Mathf.Min(topLeft.Y, bottomRight.Y);
        float maxY = Mathf.Max(topLeft.Y, bottomRight.Y);

        var set = new HashSet<int>();
        foreach (var t in snap.Trees)
        {
            float px = (t.Tile.X + 0.5f) * PixelsPerTile;
            float py = (t.Tile.Y + 0.5f) * PixelsPerTile;
            if (px < minX || px > maxX) continue;
            if (py < minY || py > maxY) continue;
            set.Add(t.EntityId);
        }
        WriteTreeSelection(set);
        if (set.Count > 0) Host!.SelectedDummyId = null;
    }

    private void WriteTreeSelection(HashSet<int> set)
    {
        var arr = new int[set.Count];
        int i = 0;
        foreach (var id in set) arr[i++] = id;
        Host!.SelectedTreeIds = arr;
    }

    private bool HandleOrder(bool append)
    {
        if (Host!.SelectedDummyId is not int sel) return false;
        var snap = Host.LatestSnapshot;
        if (snap is null) return false;

        bool drafted = false;
        foreach (var d in snap.Dummies)
        {
            if (d.EntityId == sel) { drafted = d.Drafted; break; }
        }
        if (!drafted) return false;

        var world = GetGlobalMousePosition();
        var tile = new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));
        Host.QueueCommand(new IssueMoveOrderCommand(sel, tile, append));
        return true;
    }
}
