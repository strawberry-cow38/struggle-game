using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.Selection;

// LMB click in the world (when no other tool is active) picks the
// nearest pawn within PickRadiusPx; if none, the nearest wood stack;
// if none, the nearest tree; if neither, clears all selection.
// Shift+LMB on a tree or wood stack toggles it in/out of the
// multi-selection. Double-click LMB selects every tree (or, if the
// cursor is on a wood stack, every wood stack) in the camera viewport.
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

        var world = GetGlobalMousePosition();

        if (doubleClick)
        {
            // Double-click only expands to "all of this in view" when the
            // cursor is actually on a wood stack or tree — otherwise fall
            // through to normal single-click selection. Without the guard
            // a double-click on empty ground (or a wall) was silently
            // selecting every tree in view.
            if (TryPickWood(snap, world, out _))
            {
                SelectAllWoodInView(snap);
                return;
            }
            if (TryPickTree(snap, world, out _))
            {
                SelectAllTreesInView(snap);
                return;
            }
            // No tree / wood under cursor — let the single-click path
            // below run so wall + door + pawn picks still work on a
            // fast double-click.
        }

        // Pawn beats wood/tree if both are within radius.
        if (TryPickPawn(snap, world, out int pawnId))
        {
            Host.SelectedDummyIds = ToggleInt(Host.SelectedDummyIds, pawnId, shift);
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            return;
        }

        if (TryPickWood(snap, world, out int woodId))
        {
            var set = shift ? new HashSet<int>(Host.SelectedWoodIds) : new HashSet<int>();
            if (shift && !set.Add(woodId)) set.Remove(woodId);
            else set.Add(woodId);
            WriteWoodSelection(set);
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedWallTile = null;
            Host.SelectedDoorTile = null;
            Host.SelectedBlueprintTile = null;
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
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedWallTile = null;
            Host.SelectedDoorTile = null;
            Host.SelectedBlueprintTile = null;
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
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedWallTile = null;
            Host.SelectedDoorTile = null;
            Host.SelectedBlueprintTile = null;
            return;
        }

        // Door under cursor wins over wall (door sits "on top" UX-wise).
        if (TryPickDoor(snap, clickTile))
        {
            Host.SelectedDoorTiles = ToggleTile(Host.SelectedDoorTiles, clickTile, shift);
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            return;
        }

        // Blueprint / decon mark / chop / haul job on the clicked tile.
        // Beats walls so a decon mark over a wall is reachable; gets
        // beaten by built doors. Wall blueprint tiles have no real wall
        // so there's no conflict to worry about.
        if (TryPickBlueprint(snap, clickTile))
        {
            Host.SelectedBlueprintTiles = ToggleTile(Host.SelectedBlueprintTiles, clickTile, shift);
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            return;
        }

        // Wall on the clicked tile — works for player-built + procgen,
        // but the panel only enables the decon button for player walls.
        if (Host.Map.InBounds(clickTile) && Host.Map.GetWall(clickTile) != WallType.None)
        {
            Host.SelectedWallTiles = ToggleTile(Host.SelectedWallTiles, clickTile, shift);
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            return;
        }

        // Empty click — clear all selection (unless shift, which preserves).
        if (!shift)
        {
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
        }
    }

    // shift=false: replace selection with [v]. shift=true: toggle v in
    // the current selection (add if absent, remove if present).
    private static int[] ToggleInt(int[] current, int v, bool shift)
    {
        if (!shift) return new[] { v };
        var set = new HashSet<int>(current);
        if (!set.Add(v)) set.Remove(v);
        var arr = new int[set.Count];
        int i = 0;
        foreach (var x in set) arr[i++] = x;
        return arr;
    }

    private static TilePos[] ToggleTile(TilePos[] current, TilePos v, bool shift)
    {
        if (!shift) return new[] { v };
        var list = new List<TilePos>(current.Length + 1);
        bool removed = false;
        foreach (var t in current)
        {
            if (t == v) { removed = true; continue; }
            list.Add(t);
        }
        if (!removed) list.Add(v);
        return list.ToArray();
    }

    private bool TryPickDoor(SimSnapshot snap, TilePos tile)
    {
        foreach (var d in snap.Doors)
        {
            if (d.Tile == tile) return true;
        }
        return false;
    }

    private bool TryPickBlueprint(SimSnapshot snap, TilePos tile)
    {
        foreach (var b in snap.Blueprints)      { if (b.Tile == tile) return true; }
        foreach (var b in snap.FloorBlueprints) { if (b.Tile == tile) return true; }
        foreach (var b in snap.DoorBlueprints)  { if (b.Tile == tile) return true; }
        foreach (var d in snap.Decons)          { if (d.Tile == tile) return true; }
        return false;
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

    private bool TryPickWood(SimSnapshot snap, Vector2 world, out int id)
    {
        id = -1;
        float bestSq = (PixelsPerTile * 0.5f) * (PixelsPerTile * 0.5f);
        foreach (var w in snap.Wood)
        {
            float px = (w.Tile.X + 0.5f) * PixelsPerTile;
            float py = (w.Tile.Y + 0.5f) * PixelsPerTile;
            float dx = px - world.X;
            float dy = py - world.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestSq)
            {
                bestSq = d2;
                id = w.EntityId;
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

    private void WriteWoodSelection(HashSet<int> set)
    {
        var arr = new int[set.Count];
        int i = 0;
        foreach (var id in set) arr[i++] = id;
        Host!.SelectedWoodIds = arr;
    }

    private void SelectAllWoodInView(SimSnapshot snap)
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
        foreach (var w in snap.Wood)
        {
            float px = (w.Tile.X + 0.5f) * PixelsPerTile;
            float py = (w.Tile.Y + 0.5f) * PixelsPerTile;
            if (px < minX || px > maxX) continue;
            if (py < minY || py > maxY) continue;
            set.Add(w.EntityId);
        }
        WriteWoodSelection(set);
        if (set.Count > 0)
        {
            Host!.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
        }
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
