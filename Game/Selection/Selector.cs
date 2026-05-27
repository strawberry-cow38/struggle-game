using Godot;
using StruggleGame.Game.Designation;
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
    private const float DragThresholdPx = 6f;

    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private bool _dragging;
    private Vector2 _dragStartWorld;
    private Vector2 _dragEndWorld;
    private bool _dragShift;
    private bool _dragDoubleClick;

    private bool _rmbDragging;
    private Vector2 _rmbStartWorld;
    private Vector2 _rmbEndWorld;
    private bool _rmbShift;
    private int[] _rmbDraftedIds = Array.Empty<int>();

    public override void _Ready()
    {
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.None, ClearDrag);
    }

    private void ClearDrag()
    {
        bool any = _dragging || _rmbDragging;
        _dragging = false;
        _rmbDragging = false;
        _rmbDraftedIds = Array.Empty<int>();
        if (any) QueueRedraw();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null) return;
        if (Tools.Mode != ToolMode.None) return;

        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _dragging = true;
                    _dragStartWorld = _dragEndWorld = GetGlobalMousePosition();
                    _dragShift = mb.ShiftPressed;
                    _dragDoubleClick = mb.DoubleClick;
                    GetViewport().SetInputAsHandled();
                }
                else if (_dragging)
                {
                    _dragging = false;
                    float dx = _dragEndWorld.X - _dragStartWorld.X;
                    float dy = _dragEndWorld.Y - _dragStartWorld.Y;
                    if (dx * dx + dy * dy > DragThresholdPx * DragThresholdPx)
                        HandleRectSelectPawns(_dragStartWorld, _dragEndWorld, _dragShift);
                    else
                        HandleSelect(_dragStartWorld, _dragShift, _dragDoubleClick);
                    QueueRedraw();
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                if (mb.Pressed)
                {
                    var drafted = CollectDraftedSelected();
                    if (drafted.Length == 0) return;
                    _rmbDragging = true;
                    _rmbStartWorld = _rmbEndWorld = SnapToTileCenter(GetGlobalMousePosition());
                    _rmbShift = mb.ShiftPressed;
                    _rmbDraftedIds = drafted;
                    GetViewport().SetInputAsHandled();
                }
                else if (_rmbDragging)
                {
                    _rmbDragging = false;
                    IssueOrders(_rmbDraftedIds, _rmbStartWorld, _rmbEndWorld, _rmbShift);
                    _rmbDraftedIds = Array.Empty<int>();
                    QueueRedraw();
                    GetViewport().SetInputAsHandled();
                }
            }
        }
        else if (@event is InputEventMouseMotion)
        {
            if (_dragging)
            {
                _dragEndWorld = GetGlobalMousePosition();
                QueueRedraw();
            }
            if (_rmbDragging)
            {
                var snapped = SnapToTileCenter(GetGlobalMousePosition());
                if (snapped != _rmbEndWorld)
                {
                    _rmbEndWorld = snapped;
                    QueueRedraw();
                }
            }
        }
    }

    public override void _Draw()
    {
        if (_dragging)
        {
            float dx = _dragEndWorld.X - _dragStartWorld.X;
            float dy = _dragEndWorld.Y - _dragStartWorld.Y;
            if (dx * dx + dy * dy > DragThresholdPx * DragThresholdPx)
            {
                float minX = Mathf.Min(_dragStartWorld.X, _dragEndWorld.X);
                float maxX = Mathf.Max(_dragStartWorld.X, _dragEndWorld.X);
                float minY = Mathf.Min(_dragStartWorld.Y, _dragEndWorld.Y);
                float maxY = Mathf.Max(_dragStartWorld.Y, _dragEndWorld.Y);
                var rect = new Rect2(minX, minY, maxX - minX, maxY - minY);
                DrawRect(rect, new Color(0.4f, 1f, 0.4f, 0.18f), filled: true);
                DrawRect(rect, new Color(0.4f, 1f, 0.4f, 0.85f), filled: false, width: 1.5f);
            }
        }

        if (_rmbDragging && _rmbDraftedIds.Length > 0)
        {
            var slots = ComputeOrderSlots(_rmbDraftedIds.Length, _rmbStartWorld, _rmbEndWorld);
            var dotColor = new Color(1f, 0.85f, 0.3f, 0.95f);
            float r = PixelsPerTile * 0.18f;
            foreach (var t in slots)
            {
                var c = new Vector2((t.X + 0.5f) * PixelsPerTile, (t.Y + 0.5f) * PixelsPerTile);
                DrawCircle(c, r, dotColor);
            }
            float ddx = _rmbEndWorld.X - _rmbStartWorld.X;
            float ddy = _rmbEndWorld.Y - _rmbStartWorld.Y;
            if (ddx * ddx + ddy * ddy > DragThresholdPx * DragThresholdPx && _rmbDraftedIds.Length > 1)
            {
                DrawLine(_rmbStartWorld, _rmbEndWorld, new Color(1f, 0.85f, 0.3f, 0.6f), 1.5f, antialiased: true);
            }
        }
    }

    private int[] CollectDraftedSelected()
    {
        var snap = Host!.LatestSnapshot;
        if (snap is null) return Array.Empty<int>();
        var selected = Host.SelectedDummyIds;
        if (selected.Length == 0) return Array.Empty<int>();
        var draftedSet = new HashSet<int>();
        foreach (var d in snap.Dummies)
        {
            if (d.Drafted) draftedSet.Add(d.EntityId);
        }
        var result = new List<int>(selected.Length);
        foreach (var id in selected)
            if (draftedSet.Contains(id)) result.Add(id);
        return result.ToArray();
    }

    private TilePos[] ComputeOrderSlots(int n, Vector2 startWorld, Vector2 endWorld)
    {
        float dx = endWorld.X - startWorld.X;
        float dy = endWorld.Y - startWorld.Y;
        bool isLine = n > 1 && dx * dx + dy * dy > DragThresholdPx * DragThresholdPx;
        var used = new HashSet<TilePos>();
        var slots = new TilePos[n];
        if (isLine)
        {
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / (n - 1);
                float wx = Mathf.Lerp(startWorld.X, endWorld.X, t);
                float wy = Mathf.Lerp(startWorld.Y, endWorld.Y, t);
                var seed = new TilePos(Mathf.FloorToInt(wx / PixelsPerTile), Mathf.FloorToInt(wy / PixelsPerTile));
                var slot = FindFreeWalkable(seed, used);
                slots[i] = slot;
                used.Add(slot);
            }
        }
        else
        {
            var center = new TilePos(Mathf.FloorToInt(endWorld.X / PixelsPerTile), Mathf.FloorToInt(endWorld.Y / PixelsPerTile));
            for (int i = 0; i < n; i++)
            {
                var slot = FindFreeWalkable(center, used);
                slots[i] = slot;
                used.Add(slot);
            }
        }
        return slots;
    }

    private static Vector2 SnapToTileCenter(Vector2 w)
    {
        int tx = Mathf.FloorToInt(w.X / PixelsPerTile);
        int ty = Mathf.FloorToInt(w.Y / PixelsPerTile);
        return new Vector2((tx + 0.5f) * PixelsPerTile, (ty + 0.5f) * PixelsPerTile);
    }

    private TilePos FindFreeWalkable(TilePos start, HashSet<TilePos> used)
    {
        if (Host!.Map.Walkable(start) && !used.Contains(start)) return start;
        for (int r = 1; r <= 24; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                    var t = new TilePos(start.X + dx, start.Y + dy);
                    if (Host.Map.Walkable(t) && !used.Contains(t)) return t;
                }
            }
        }
        return start;
    }

    private void IssueOrders(int[] ids, Vector2 startWorld, Vector2 endWorld, bool append)
    {
        if (ids.Length == 0) return;
        var slots = ComputeOrderSlots(ids.Length, startWorld, endWorld);
        for (int i = 0; i < ids.Length; i++)
        {
            Host!.QueueCommand(new IssueMoveOrderCommand(ids[i], slots[i], append));
        }
    }

    private void HandleRectSelectPawns(Vector2 a, Vector2 b, bool shift)
    {
        var snap = Host!.LatestSnapshot;
        if (snap is null) return;
        float minX = Mathf.Min(a.X, b.X), maxX = Mathf.Max(a.X, b.X);
        float minY = Mathf.Min(a.Y, b.Y), maxY = Mathf.Max(a.Y, b.Y);

        var set = shift ? new HashSet<int>(Host.SelectedDummyIds) : new HashSet<int>();
        foreach (var d in snap.Dummies)
        {
            float px = d.X * PixelsPerTile;
            float py = d.Y * PixelsPerTile;
            if (px < minX || px > maxX) continue;
            if (py < minY || py > maxY) continue;
            set.Add(d.EntityId);
        }

        var arr = new int[set.Count];
        int i = 0;
        foreach (var id in set) arr[i++] = id;
        Host.SelectedDummyIds = arr;

        if (arr.Length > 0)
        {
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
        }
        else if (!shift)
        {
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
        }
    }

    private void HandleSelect(Vector2 world, bool shift, bool doubleClick)
    {
        var snap = Host!.LatestSnapshot;
        if (snap is null) return;

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
            Host.SelectedGrowZoneId = null;
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
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
            Host.SelectedGrowZoneId = null;
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
            Host.SelectedGrowZoneId = null;
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
            Host.SelectedGrowZoneId = null;
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedWallTile = null;
            Host.SelectedDoorTile = null;
            Host.SelectedBlueprintTile = null;
            return;
        }

        if (TryPickGrowZone(snap, clickTile, out int growId))
        {
            Host.SelectedGrowZoneId = growId;
            Host.SelectedStockpileId = null;
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
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            return;
        }

        // Lamp under cursor — picks before blueprints/walls so the
        // fixture is reachable even if a passthrough tile coincides.
        if (TryPickLamp(snap, clickTile))
        {
            Host.SelectedLampTiles = ToggleTile(Host.SelectedLampTiles, clickTile, shift);
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
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
            Host.SelectedGrowZoneId = null;
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
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
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
            Host.SelectedGrowZoneId = null;
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
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

    private bool TryPickLamp(SimSnapshot snap, TilePos tile)
    {
        foreach (var l in snap.Lamps)
        {
            if (l.Tile == tile) return true;
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

    private bool TryPickGrowZone(SimSnapshot snap, TilePos tile, out int id)
    {
        id = -1;
        foreach (var z in snap.GrowZones)
        {
            foreach (var t in z.Tiles)
            {
                if (t == tile) { id = z.Id; return true; }
            }
        }
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
            Host.SelectedGrowZoneId = null;
        }
    }

}
