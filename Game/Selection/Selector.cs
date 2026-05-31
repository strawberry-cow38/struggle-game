using Godot;
using StruggleGame.Game.Designation;
using StruggleGame.Game.Tools;
using StruggleGame.Game.UI;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
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

    private PopupMenu? _bpMenu;
    private TilePos _bpMenuTile;
    private int _bpMenuPawnId;
    // Set when the context menu carries an "Equip" entry (id 1); names the
    // dropped pile entity the selected colonist would go fetch.
    private int _equipItemEntityId;
    // Pick-up entry (ids 3 = all, 4 = X) state: the pile, how many the
    // selected colonist can carry, and its display name for the dialog.
    private int _pickupItemId;
    private int _pickupMax;
    private string _pickupName = "";
    // Quantity dialog for "Pick up X..." — wired by Bootstrap.
    public PickupQuantityDialog? PickupDialog { get; set; }
    // Camera ref so an open context menu can forward wheel-zoom. Wired by Bootstrap.
    public StruggleGame.Game.Camera.GameCamera? Camera { get; set; }
    // Melee menu (id 5): the victim + the drafted attackers ordered on it.
    private int _meleeTargetId;
    private int[] _meleeAttackers = Array.Empty<int>();
    // Fire menu (id 6): drafted attackers that hold a ranged weapon.
    private int[] _fireAttackers = Array.Empty<int>();
    // Move-here entry (id 7): drafted single pawn ordered to stand on the
    // clicked tile (lets it move atop dropped piles etc).
    private TilePos _moveHereTile;

    public override void _Ready()
    {
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.None, ClearDrag);
        _bpMenu = new PopupMenu();
        // Parent the menu to a CanvasLayer, NOT this Node2D — an embedded
        // popup under a world-space CanvasItem inherits the camera's canvas
        // transform and scales with zoom (tiny zoomed out, huge zoomed in).
        // A CanvasLayer renders in screen space, so the menu stays put.
        var menuLayer = new CanvasLayer { Name = "MenuLayer", Layer = 110 };
        AddChild(menuLayer);
        menuLayer.AddChild(_bpMenu);
        _bpMenu.IdPressed += OnBlueprintMenuPressed;
        // PopupMenu only activates items on LMB by default. The menu is
        // opened with RMB, so let RMB activate the hovered item too — the
        // player shouldn't have to switch buttons mid-gesture.
        _bpMenu.WindowInput += OnMenuWindowInput;
    }

    private void OnMenuWindowInput(InputEvent @event)
    {
        if (_bpMenu is null) return;
        if (@event is not InputEventMouseButton mb) return;
        // Forward scroll-wheel zoom to the camera so the player can zoom with
        // the context menu still open, instead of the popup swallowing it.
        if (mb.Pressed && Camera is not null
            && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
        {
            Camera.ZoomStep(mb.ButtonIndex == MouseButton.WheelUp ? 1 : -1);
            _bpMenu.SetInputAsHandled();
            return;
        }
        if (!mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left && mb.ButtonIndex != MouseButton.Right) return;

        // A click outside the menu's rect (left OR right) closes it. We own
        // WindowInput, so Godot's default outside-click close doesn't fire —
        // do it ourselves, otherwise the menu would stay stuck open.
        var rect = new Rect2(Vector2.Zero, _bpMenu.Size);
        if (!rect.HasPoint(mb.Position))
        {
            _bpMenu.Hide();
            return;
        }

        // Inside + RMB → activate the hovered item (so the player doesn't have
        // to switch to LMB mid-gesture). LMB inside is handled by the popup.
        if (mb.ButtonIndex == MouseButton.Right)
        {
            int focused = _bpMenu.GetFocusedItem();
            if (focused < 0) return;
            long id = _bpMenu.GetItemId(focused);
            _bpMenu.Hide();
            OnBlueprintMenuPressed(id);
        }
    }

    private void OnBlueprintMenuPressed(long id)
    {
        if (Host is null) return;
        if (id == 0)
        {
            Host.QueueCommand(new PrioritizeBlueprintForPawnCommand(_bpMenuTile, _bpMenuPawnId));
        }
        else if (id == 1)
        {
            Host.QueueCommand(new EquipItemCommand(_bpMenuPawnId, _equipItemEntityId));
        }
        else if (id == 2)
        {
            Host.QueueCommand(new PrioritizeHaulForPawnCommand(_bpMenuPawnId, _pickupItemId));
        }
        else if (id == 3)
        {
            // Pick up all (capacity-clamped sim-side).
            Host.QueueCommand(new PickUpItemCommand(_bpMenuPawnId, _pickupItemId, int.MaxValue));
        }
        else if (id == 4)
        {
            // Pick up X — open the quantity dialog.
            PickupDialog?.Open(_bpMenuPawnId, _pickupItemId, _pickupMax, _pickupName);
        }
        else if (id == 5)
        {
            foreach (var attacker in _meleeAttackers)
                Host.QueueCommand(new MeleeAttackCommand(attacker, _meleeTargetId));
        }
        else if (id == 6)
        {
            foreach (var shooter in _fireAttackers)
                Host.QueueCommand(new SetFireTargetCommand(shooter, _meleeTargetId));
        }
        else if (id == 7)
        {
            // Move here — stand on the clicked tile (e.g. atop dropped items).
            Host.QueueCommand(new IssueMoveOrderCommand(_bpMenuPawnId, _moveHereTile, append: false));
        }
    }

    // Drafted pawn(s) RMB on another pawn → "Melee attack X". Returns
    // false (→ move-order drag) if the cursor isn't on a different pawn.
    private bool TryShowMeleeMenu(Vector2 world, int[] attackers)
    {
        if (Host is null || _bpMenu is null) return false;
        var snap = Host.LatestSnapshot;
        if (snap is null) return false;
        if (!TryPickPawn(snap, world, out int targetId)) return false;
        if (System.Array.IndexOf(attackers, targetId) >= 0) return false; // don't punch self

        string name = $"Colonist {targetId}";
        foreach (var pw in snap.PawnWork)
            if (pw.EntityId == targetId) { name = pw.Name; break; }

        _meleeTargetId = targetId;
        _meleeAttackers = attackers;
        _bpMenu.Clear();
        // Fire at X is the top option (grayed out when no shooter is in range);
        // melee attack sits below it.
        _fireAttackers = FilterRangedHolders(snap, attackers);
        if (_fireAttackers.Length > 0)
        {
            _bpMenu.AddItem($"Fire at {name}", 6);
            _bpMenu.SetItemDisabled(_bpMenu.ItemCount - 1, !AnyAttackerCanFire(snap, _fireAttackers, targetId));
        }
        _bpMenu.AddItem($"Melee attack {name}", 5);
        var screenPos = GetCanvasTransform() * world;
        _bpMenu.Position = new Vector2I((int)screenPos.X, (int)screenPos.Y);
        _bpMenu.Popup();
        return true;
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
            // Any mouse-button press that reaches here is OUTSIDE the context
            // menu (clicks on the menu go to the popup window). So a click or
            // right-click off an open menu just closes it, consuming the click.
            if (mb.Pressed && _bpMenu is { Visible: true }
                && (mb.ButtonIndex == MouseButton.Left || mb.ButtonIndex == MouseButton.Right))
            {
                _bpMenu.Hide();
                GetViewport().SetInputAsHandled();
                return;
            }
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
                    if (drafted.Length == 0)
                    {
                        // Non-drafted pawn(s) selected: RMB on a blueprint
                        // opens a "Prioritize for X" menu so the player can
                        // pin that blueprint to a specific colonist. Falls
                        // through silently if no single non-drafted pawn is
                        // selected or the cursor isn't on a blueprint.
                        if (TryShowBlueprintMenu(GetGlobalMousePosition()))
                        {
                            GetViewport().SetInputAsHandled();
                        }
                        return;
                    }
                    // Drafted: RMB on another pawn → "Melee attack X" menu.
                    if (TryShowMeleeMenu(GetGlobalMousePosition(), drafted))
                    {
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    // Drafted: RMB on an item pile → equip / pick-up menu
                    // (no haul / prioritize-work). Single drafted pawn only.
                    if (drafted.Length == 1
                        && TryShowBlueprintMenu(GetGlobalMousePosition(), draftedMode: true))
                    {
                        GetViewport().SetInputAsHandled();
                        return;
                    }
                    // Otherwise fall through to the move-order drag.
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

    // Resolve (selected non-drafted pawn id, tile, pawn display name) from
    // a screen click and pop the prioritize menu. Returns false if any
    // precondition fails (no single non-drafted pawn, no blueprint at
    // tile) so the caller can fall through to drafted-RMB logic.
    // draftedMode: the selected pawn is drafted — equipping and picking up
    // are still allowed, but "Prioritize work" (id 0) and "Prioritize Haul"
    // (id 2) are hidden so draft never queues a work job.
    private bool TryShowBlueprintMenu(Vector2 world, bool draftedMode = false)
    {
        if (Host is null || _bpMenu is null) return false;
        var snap = Host.LatestSnapshot;
        if (snap is null) return false;
        var selected = Host.SelectedDummyIds;
        if (selected.Length != 1) return false;

        int pawnId = selected[0];
        string pawnName = $"Colonist {pawnId}";
        foreach (var pw in snap.PawnWork)
        {
            if (pw.EntityId == pawnId) { pawnName = pw.Name; break; }
        }

        var clickTile = new TilePos(
            Mathf.FloorToInt(world.X / PixelsPerTile),
            Mathf.FloorToInt(world.Y / PixelsPerTile));

        _bpMenuPawnId = pawnId;
        _bpMenu.Clear();
        // Prioritize-blueprint entry (id 0) when a blueprint is under the
        // cursor — counts as queuing work, so suppressed while drafted.
        if (!draftedMode && TryPickBlueprint(snap, clickTile, out var bpTile))
        {
            _bpMenuTile = bpTile;
            _bpMenu.AddItem($"Prioritize for {pawnName}", 0);
        }
        // Equip entry (id 1) when an equippable dropped pile is under the cursor.
        if (TryPickEquippablePile(snap, clickTile, out int itemId, out string itemName))
        {
            _equipItemEntityId = itemId;
            _bpMenu.AddItem($"Equip {itemName}", 1);
        }
        // Pick-up / haul entries for any dropped pile under the cursor.
        if (TryPickAnyPile(snap, clickTile, out var pile)
            && ItemCatalog.ItemsByPath.TryGetValue(pile.ItemPath, out var pileDef))
        {
            _pickupItemId = pile.EntityId;
            _pickupName = pileDef.DisplayName;
            // Prioritize Haul (id 2) — only for piles not already in a
            // stockpile, and never while drafted (hauling is work). Grayed out
            // when no stockpile would actually accept this item.
            if (!draftedMode && !IsInStockpile(snap, pile.Tile))
            {
                _bpMenu.AddItem($"Prioritize Haul for {pawnName}", 2);
                _bpMenu.SetItemDisabled(_bpMenu.ItemCount - 1, !AnyStockpileAccepts(snap, pile.ItemPath));
            }
            _pickupMax = PawnPickupMax(snap, pawnId, pileDef, pile.Count);
            if (_pickupMax > 0)
            {
                _bpMenu.AddItem($"Pick up all ({Math.Min(_pickupMax, pile.Count)})", 3);
                _bpMenu.AddItem("Pick up X...", 4);
            }
        }
        // Drafted single pawn RMB'd something rmb-able on a tile → offer to
        // just move there (stand atop the dropped pile etc). Only when the menu
        // already has an entry, so RMB on empty ground still falls through to a
        // direct move order instead of popping a menu.
        if (draftedMode && _bpMenu.ItemCount > 0)
        {
            _moveHereTile = clickTile;
            _bpMenu.AddItem("Move here", 7);
        }

        if (_bpMenu.ItemCount == 0) return false;

        var canvasXform = GetCanvasTransform();
        var screenPos = canvasXform * world;
        _bpMenu.Position = new Vector2I((int)screenPos.X, (int)screenPos.Y);
        _bpMenu.Popup();
        return true;
    }

    // True if any built stockpile accepts this item (so a haul has somewhere
    // to go). Stockpiles allow by exact item path.
    private static bool AnyStockpileAccepts(SimSnapshot snap, string itemPath)
    {
        foreach (var sp in snap.Stockpiles)
        {
            if (sp.Tiles.Length == 0) continue;
            foreach (var p in sp.AllowedItemPaths)
                if (p == itemPath) return true;
        }
        return false;
    }

    private static bool IsInStockpile(SimSnapshot snap, TilePos tile)
    {
        foreach (var sp in snap.Stockpiles)
            foreach (var t in sp.Tiles)
                if (t == tile) return true;
        return false;
    }

    // Any dropped pile on the clicked tile (wood, food, anything).
    private static bool TryPickAnyPile(SimSnapshot snap, TilePos tile, out ItemPileState pile)
    {
        foreach (var p in snap.ItemPiles)
        {
            if (p.Tile == tile) { pile = p; return true; }
        }
        pile = default;
        return false;
    }

    // How many units of `def` the selected colonist could still carry,
    // capped at what's in the pile. Reads the already-summed carry load
    // from the pawn's snapshot row.
    private static int PawnPickupMax(SimSnapshot snap, int pawnId, ItemDef def, int pileCount)
    {
        foreach (var d in snap.Dummies)
        {
            if (d.EntityId != pawnId) continue;
            float remW = d.MaxCarryWeight - d.CarryWeight;
            float remB = d.MaxCarryBulk - d.CarryBulk;
            int fit = (def.Weight <= 0f && def.Bulk <= 0f)
                ? int.MaxValue
                : (int)Math.Floor(Math.Min(
                    def.Weight > 0f ? remW / def.Weight : int.MaxValue,
                    def.Bulk > 0f ? remB / def.Bulk : int.MaxValue));
            if (fit < 0) fit = 0;
            return Math.Min(fit, pileCount);
        }
        return 0;
    }

    // Find an equippable ItemPile on the clicked tile. Returns its entity
    // id + display name so the context menu can offer an "Equip" entry.
    private static bool TryPickEquippablePile(SimSnapshot snap, TilePos tile, out int entityId, out string name)
    {
        entityId = 0;
        name = "";
        foreach (var pile in snap.ItemPiles)
        {
            if (pile.Tile != tile) continue;
            if (!ItemCatalog.ItemsByPath.TryGetValue(pile.ItemPath, out var def)) continue;
            if (!def.Equippable) continue;
            entityId = pile.EntityId;
            name = def.DisplayName;
            return true;
        }
        return false;
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
            Host.SelectedCropIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
        }
        else if (!shift)
        {
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
        }
    }

    private void HandleSelect(Vector2 world, bool shift, bool doubleClick)
    {
        var snap = Host!.LatestSnapshot;
        if (snap is null) return;

        if (doubleClick)
        {
            // Double-click only expands to "all of this in view" when the
            // cursor is actually on an item stack or tree — otherwise fall
            // through to normal single-click selection. Without the guard
            // a double-click on empty ground (or a wall) was silently
            // selecting every tree in view.
            if (TryPickItemPile(snap, world, out _))
            {
                SelectAllItemsInView(snap);
                return;
            }
            if (TryPickTree(snap, world, out _))
            {
                SelectAllTreesInView(snap);
                return;
            }
            if (TryPickCrop(snap, world, out _))
            {
                SelectAllCropsInView(snap);
                return;
            }
            // No tree / wood / crop under cursor — let the single-click
            // path below run so wall + door + pawn picks still work on
            // a fast double-click.
        }

        // Pawn beats wood/tree if both are within radius.
        if (TryPickPawn(snap, world, out int pawnId))
        {
            Host.SelectedDummyIds = ToggleInt(Host.SelectedDummyIds, pawnId, shift);
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
            return;
        }

        if (TryPickItemPile(snap, world, out int pileId))
        {
            var set = shift ? new HashSet<int>(Host.SelectedWoodIds) : new HashSet<int>();
            if (shift && !set.Add(pileId)) set.Remove(pileId);
            else set.Add(pileId);
            WriteWoodSelection(set);
            Host.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
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
            Host.SelectedCropIds = Array.Empty<int>();
            Host.SelectedWallTile = null;
            Host.SelectedDoorTile = null;
            Host.SelectedBlueprintTile = null;
            return;
        }

        if (TryPickCrop(snap, world, out int cropId))
        {
            var set = shift ? new HashSet<int>(Host.SelectedCropIds) : new HashSet<int>();
            if (shift && !set.Add(cropId)) set.Remove(cropId);
            else set.Add(cropId);
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
            Host.SelectedWallTile = null;
            Host.SelectedDoorTile = null;
            Host.SelectedBlueprintTile = null;
            WriteCropSelection(set);
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
            Host.SelectedCropIds = Array.Empty<int>();
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
            Host.SelectedCropIds = Array.Empty<int>();
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
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
            Host.SelectedStoveTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
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
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
            Host.SelectedStoveTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
            return;
        }

        // Bed under cursor — either tile of the 2-tile footprint resolves
        // back to the origin tile so the selection keys off a stable id.
        if (TryPickBed(snap, clickTile, out var bedOrigin))
        {
            Host.SelectedBedTiles = ToggleTile(Host.SelectedBedTiles, bedOrigin, shift);
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
            return;
        }

        if (TryPickUrBoard(snap, clickTile))
        {
            Host.SelectedUrBoardTiles = ToggleTile(Host.SelectedUrBoardTiles, clickTile, shift);
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
            Host.SelectedStoveTiles = Array.Empty<TilePos>();
            return;
        }

        if (TryPickStove(snap, clickTile, out var stoveOrigin))
        {
            Host.SelectedStoveTiles = ToggleTile(Host.SelectedStoveTiles, stoveOrigin, shift);
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
            return;
        }

        // Blueprint / decon mark / chop / haul job on the clicked tile.
        // Beats walls so a decon mark over a wall is reachable; gets
        // beaten by built doors. Wall blueprint tiles have no real wall
        // so there's no conflict to worry about.
        if (TryPickBlueprint(snap, clickTile, out var bpTile))
        {
            Host.SelectedBlueprintTiles = ToggleTile(Host.SelectedBlueprintTiles, bpTile, shift);
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
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
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
            Host.SelectedStoveTiles = Array.Empty<TilePos>();
            Host.SelectedDummyId = null;
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            Host.SelectedCropIds = Array.Empty<int>();
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
            Host.SelectedCropIds = Array.Empty<int>();
            Host.SelectedWallTiles = Array.Empty<TilePos>();
            Host.SelectedDoorTiles = Array.Empty<TilePos>();
            Host.SelectedBlueprintTiles = Array.Empty<TilePos>();
            Host.SelectedLampTiles = Array.Empty<TilePos>();
            Host.SelectedBedTiles = Array.Empty<TilePos>();
            Host.SelectedUrBoardTiles = Array.Empty<TilePos>();
            Host.SelectedStoveTiles = Array.Empty<TilePos>();
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

    private bool TryPickBed(SimSnapshot snap, TilePos tile, out TilePos origin)
    {
        foreach (var b in snap.Beds)
        {
            if (b.Origin == tile) { origin = b.Origin; return true; }
            var foot = StruggleGame.Sim.World.BedOrientations.Foot(b.Origin, b.Orientation);
            if (foot == tile) { origin = b.Origin; return true; }
        }
        origin = default;
        return false;
    }

    private bool TryPickBlueprint(SimSnapshot snap, TilePos tile, out TilePos resolved)
    {
        resolved = tile;
        foreach (var b in snap.Blueprints)      { if (b.Tile == tile) return true; }
        foreach (var b in snap.FloorBlueprints) { if (b.Tile == tile) return true; }
        foreach (var b in snap.DoorBlueprints)  { if (b.Tile == tile) return true; }
        foreach (var b in snap.LampBlueprints)  { if (b.Tile == tile) return true; }
        foreach (var b in snap.BedBlueprints)
        {
            if (b.Origin == tile) { resolved = b.Origin; return true; }
            var foot = StruggleGame.Sim.World.BedOrientations.Foot(b.Origin, b.Orientation);
            if (foot == tile) { resolved = b.Origin; return true; }
        }
        foreach (var b in snap.UrBoardBlueprints) { if (b.Tile == tile) return true; }
        foreach (var b in snap.StoveBlueprints)
        {
            foreach (var t in StruggleGame.Sim.World.StoveOrientations.BodyTiles(b.Origin, b.Orientation))
            {
                if (t == tile) { resolved = b.Origin; return true; }
            }
            if (StruggleGame.Sim.World.StoveOrientations.StandingTile(b.Origin, b.Orientation) == tile)
            { resolved = b.Origin; return true; }
        }
        foreach (var d in snap.Decons)          { if (d.Tile == tile) return true; }
        return false;
    }

    private bool TryPickUrBoard(SimSnapshot snap, TilePos tile)
    {
        foreach (var ub in snap.UrBoards)
        {
            if (ub.Tile == tile) return true;
        }
        return false;
    }

    private bool TryPickStove(SimSnapshot snap, TilePos tile, out TilePos origin)
    {
        foreach (var s in snap.Stoves)
        {
            foreach (var t in StruggleGame.Sim.World.StoveOrientations.BodyTiles(s.Origin, s.Orientation))
            {
                if (t == tile) { origin = s.Origin; return true; }
            }
            if (StruggleGame.Sim.World.StoveOrientations.StandingTile(s.Origin, s.Orientation) == tile)
            { origin = s.Origin; return true; }
        }
        origin = default;
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

    // True if any of the attackers is both in range of the target AND has
    // ammo (loaded or reloadable) — i.e. can actually open fire.
    private static bool AnyAttackerCanFire(SimSnapshot snap, int[] attackers, int targetId)
    {
        float tx = 0, ty = 0; bool foundTarget = false;
        foreach (var d in snap.Dummies)
            if (d.EntityId == targetId) { tx = d.X; ty = d.Y; foundTarget = true; break; }
        if (!foundTarget) return false;
        var set = new HashSet<int>(attackers);
        foreach (var d in snap.Dummies)
        {
            if (!set.Contains(d.EntityId)) continue;
            if (!d.RangedHasAmmo) continue;
            float dx = d.X - tx, dy = d.Y - ty;
            if (Mathf.Sqrt(dx * dx + dy * dy) <= d.RangedRange) return true;
        }
        return false;
    }

    // Of the given pawn ids, those currently holding a ranged weapon.
    private static int[] FilterRangedHolders(SimSnapshot snap, int[] ids)
    {
        var set = new HashSet<int>();
        foreach (var d in snap.Dummies)
            if (d.HasRangedWeapon) set.Add(d.EntityId);
        var list = new List<int>();
        foreach (var id in ids)
            if (set.Contains(id)) list.Add(id);
        return list.ToArray();
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

    // Dropped item stacks (wood, carrots, meals — all ItemPiles). Feeds
    // the SelectedWoodIds selection set; the info panel resolves the id
    // against snap.ItemPiles.
    private bool TryPickItemPile(SimSnapshot snap, Vector2 world, out int id)
    {
        id = -1;
        float bestSq = (PixelsPerTile * 0.5f) * (PixelsPerTile * 0.5f);
        foreach (var p in snap.ItemPiles)
        {
            float px = (p.Tile.X + 0.5f) * PixelsPerTile;
            float py = (p.Tile.Y + 0.5f) * PixelsPerTile;
            float dx = px - world.X;
            float dy = py - world.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestSq)
            {
                bestSq = d2;
                id = p.EntityId;
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

    private bool TryPickCrop(SimSnapshot snap, Vector2 world, out int id)
    {
        id = -1;
        float bestSq = (PixelsPerTile * 0.5f) * (PixelsPerTile * 0.5f);
        foreach (var c in snap.Crops)
        {
            float px = (c.Tile.X + 0.5f) * PixelsPerTile;
            float py = (c.Tile.Y + 0.5f) * PixelsPerTile;
            float dx = px - world.X;
            float dy = py - world.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestSq)
            {
                bestSq = d2;
                id = c.EntityId;
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
        if (set.Count > 0)
        {
            Host!.SelectedDummyId = null;
            Host.SelectedCropIds = Array.Empty<int>();
        }
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

    private void WriteCropSelection(HashSet<int> set)
    {
        var arr = new int[set.Count];
        int i = 0;
        foreach (var id in set) arr[i++] = id;
        Host!.SelectedCropIds = arr;
    }

    private void SelectAllCropsInView(SimSnapshot snap)
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
        foreach (var c in snap.Crops)
        {
            float px = (c.Tile.X + 0.5f) * PixelsPerTile;
            float py = (c.Tile.Y + 0.5f) * PixelsPerTile;
            if (px < minX || px > maxX) continue;
            if (py < minY || py > maxY) continue;
            set.Add(c.EntityId);
        }
        WriteCropSelection(set);
        if (set.Count > 0)
        {
            Host!.SelectedDummyId = null;
            Host.SelectedTreeIds = Array.Empty<int>();
            Host.SelectedWoodIds = Array.Empty<int>();
            // NOT SelectedCropIds — WriteCropSelection just set it.
            Host.SelectedStockpileId = null;
            Host.SelectedGrowZoneId = null;
        }
    }

    private void SelectAllItemsInView(SimSnapshot snap)
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
        foreach (var p in snap.ItemPiles)
        {
            float px = (p.Tile.X + 0.5f) * PixelsPerTile;
            float py = (p.Tile.Y + 0.5f) * PixelsPerTile;
            if (px < minX || px > maxX) continue;
            if (py < minY || py > maxY) continue;
            set.Add(p.EntityId);
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
