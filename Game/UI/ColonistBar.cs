using System.Collections.Generic;
using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Game.Camera;

namespace StruggleGame.Game.UI;

// Top-center colonist portrait bar (RimWorld-style). One card per living
// colonist: a portrait placeholder (no art yet), the name below, and a
// weapon-icon placeholder (dud) below that.
//   - left-click selects a colonist
//   - shift/ctrl-click adds/removes from the multi-selection
//   - double-click focuses the camera on the colonist
//   - drag a box across the cards to rect-select them
// All routed through _Input so a drag can span multiple cards; mirrors
// Host.SelectedDummyIds so it stays in sync with world selection.
public partial class ColonistBar : CanvasLayer
{
    public SimHost? Host { get; set; }
    public GameCamera? Camera { get; set; }

    private const int PortraitSize = 72;  // +10% bump
    private const int CardWidth = 90;
    private const int MarginTop = 8;
    private const float ClickSlopPx = 9f; // deadzone before a drag-select kicks in

    private const int MaxPerRow = 10; // 11+ colonists wrap to a new row

    private VBoxContainer _bar = null!;
    private readonly List<(int id, PanelContainer card, PanelContainer frame, PortraitView portrait)> _cards = new();
    private readonly Dictionary<int, string> _loadoutSig = new();
    // Weapon-icon holder per card (below the name), refreshed when loadout changes.
    private readonly Dictionary<int, Control> _weaponSlots = new();
    // Stable display order — new colonists append to the right; the player can
    // drag to reorder. Removed colonists drop out, others keep their slot.
    private readonly List<int> _order = new();
    private readonly List<int> _newScratch = new();
    private string _lastSig = "";

    private double _glowClock;                          // advances each frame, drives the selection glow
    private readonly Dictionary<int, double> _selSince = new(); // when each id became selected (for the pulse)

    private bool _dragging;       // rect-select (started on bar background)
    private bool _reordering;     // dragging a card to a new slot
    private int _reorderId = -1;
    private bool _dragAdditive;
    private Vector2 _dragStart;
    private DragRectOverlay _overlay = null!;

    public override void _Ready()
    {
        Layer = 94;
        _bar = new VBoxContainer { Name = "ColonistRows", MouseFilter = Control.MouseFilterEnum.Ignore };
        _bar.AddThemeConstantOverride("separation", 6);
        AddChild(_bar);
        _overlay = new DragRectOverlay { MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_overlay);
        _bar.Resized += Reposition;
        GetTree().Root.SizeChanged += Reposition;
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    public override void _Process(double delta)
    {
        if (Host?.LatestSnapshot is not { } snap) { _bar.Visible = false; return; }
        _glowClock += delta;

        var byId = new Dictionary<int, DummyState>();
        foreach (var d in snap.Dummies)
            if (!d.IsEnemy) byId[d.EntityId] = d;

        _bar.Visible = byId.Count > 0;

        // Reconcile the persistent order: drop gone, append new (right).
        for (int i = _order.Count - 1; i >= 0; i--)
            if (!byId.ContainsKey(_order[i])) _order.RemoveAt(i);
        _newScratch.Clear();
        foreach (var id in byId.Keys) if (!_order.Contains(id)) _newScratch.Add(id);
        _newScratch.Sort();
        _order.AddRange(_newScratch);

        var sb = new System.Text.StringBuilder();
        foreach (var id in _order) sb.Append(id).Append(',');
        string sig = sb.ToString();
        if (sig != _lastSig)
        {
            _lastSig = sig;
            var ordered = new List<DummyState>(_order.Count);
            foreach (var id in _order) ordered.Add(byId[id]);
            Rebuild(ordered);
        }

        // Mood-coded card ring (border + glow), selection override; draft shows
        // as a portrait badge.
        var sel = new HashSet<int>(Host.SelectedDummyIds);
        foreach (var (id, card, frame, portrait) in _cards)
        {
            bool selected = sel.Contains(id);
            bool has = byId.TryGetValue(id, out var d);
            Color ring = has ? MoodColor(d.Mood) : UiTheme.Border;
            // Outer card: neutral edge, cyan selection outline around the whole
            // card when selected, with a pulse-on-select + flicker glow (matches
            // the info-panel tabs). Mood ring stays on the frame.
            Color cardEdge = selected ? UiTheme.Accent : UiTheme.Border;
            Color cardGlow = new Color(0, 0, 0, 0);
            int cardGlowSize = 0;
            if (selected)
            {
                if (!_selSince.ContainsKey(id)) _selSince[id] = _glowClock;
                float gb = UiTheme.PulseFlicker(_glowClock - _selSince[id]);
                cardGlow = new Color(UiTheme.Accent.R, UiTheme.Accent.G, UiTheme.Accent.B, UiTheme.GlowAlpha(gb));
                cardGlowSize = UiTheme.GlowSize(gb);
            }
            else _selSince.Remove(id);
            card.AddThemeStyleboxOverride("panel", CardBox(cardEdge, cardGlow, cardGlowSize));
            frame.AddThemeStyleboxOverride("panel", FrameBox(ring));
            if (has)
            {
                string lo = LoadoutSig(d);
                if (!_loadoutSig.TryGetValue(id, out var prev) || prev != lo)
                {
                    _loadoutSig[id] = lo;
                    ApplyLoadout(portrait, d);
                    RefreshWeaponIcon(id, d);
                }
            }
        }

        Reposition();
    }

    public override void _Input(InputEvent @event)
    {
        if (Host is null || !_bar.Visible) return;

        if (@event is InputEventMouseMotion mm)
        {
            if (_dragging)
            {
                _overlay.Cur = mm.Position;
                if (!_overlay.Active && (mm.Position - _dragStart).Length() > ClickSlopPx)
                    _overlay.Active = true;
                _overlay.QueueRedraw();
                return;
            }
            if (_reordering && (mm.Position - _dragStart).Length() > ClickSlopPx)
            {
                // Only dim once a real drag begins (not on a plain click).
                if (CardOf(_reorderId) is { } dc) dc.Modulate = new Color(1f, 1f, 1f, 0.5f);
                if (TryInsertSlot(mm.Position, out _, out float lx, out float top, out float h))
                {
                    _overlay.InsertActive = true;
                    _overlay.InsertX = lx; _overlay.InsertTop = top; _overlay.InsertHeight = h;
                    _overlay.QueueRedraw();
                }
                return;
            }
        }
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            var pos = mb.Position;
            if (mb.Pressed)
            {
                if (!BarZone().HasPoint(pos)) { _dragging = false; _reordering = false; return; }
                if (mb.DoubleClick)
                {
                    FocusAt(pos);
                    _dragging = false; _reordering = false;
                    GetViewport().SetInputAsHandled();
                    return;
                }
                _dragStart = pos;
                _dragAdditive = mb.ShiftPressed || mb.CtrlPressed;
                int hit = CardAt(pos);
                if (hit >= 0)
                {
                    // Press on a card: reorder (or click-select if it doesn't move).
                    _reordering = true; _reorderId = hit; _dragging = false;
                }
                else
                {
                    // Press on bar background: rect-select.
                    _dragging = true; _reordering = false;
                    _overlay.Active = false; _overlay.Start = pos; _overlay.Cur = pos;
                }
                GetViewport().SetInputAsHandled();
            }
            else if (_dragging)
            {
                _dragging = false;
                _overlay.Active = false;
                _overlay.QueueRedraw();
                var rect = RectOf(_dragStart, pos);
                if (rect.Size.X < ClickSlopPx && rect.Size.Y < ClickSlopPx) ClickAt(pos);
                else RectSelect(rect);
                GetViewport().SetInputAsHandled();
            }
            else if (_reordering)
            {
                _reordering = false;
                if (CardOf(_reorderId) is { } dc) dc.Modulate = Colors.White;
                _overlay.InsertActive = false;
                _overlay.QueueRedraw();
                if ((pos - _dragStart).Length() <= ClickSlopPx) ClickAt(_dragStart); // it was a click
                else Reorder(_reorderId, pos);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    // Drop the dragged colonist into the gap nearest the release point.
    private void Reorder(int draggedId, Vector2 releasePos)
    {
        if (!TryInsertSlot(releasePos, out int slot, out _, out _, out _)) return;
        int from = _order.IndexOf(draggedId);
        if (from < 0) return;
        int to = slot;
        if (from < to) to--; // removal shifts everything after it left
        to = Mathf.Clamp(to, 0, _order.Count - 1);
        if (to == from) return;
        _order.RemoveAt(from);
        _order.Insert(to, draggedId);
        _lastSig = ""; // force a rebuild in the new order next frame
    }

    // The bar plus a generous margin — pressing anywhere in here (off a card)
    // starts a rect-select, so it's easy to begin a drag near the portraits.
    private Rect2 BarZone() => _bar.GetGlobalRect().Grow(70f);

    private Control? CardOf(int id)
    {
        foreach (var (cid, card, _, _) in _cards) if (cid == id) return card;
        return null;
    }

    // Insertion slot (gap index in _order) + the drop-line geometry, chosen by
    // the card nearest the cursor and which half it's on (squeeze between two).
    private bool TryInsertSlot(Vector2 pos, out int slot, out float lineX, out float top, out float height)
    {
        slot = 0; lineX = top = height = 0f;
        if (_cards.Count == 0) return false;
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < _cards.Count; i++)
        {
            var r = _cards[i].card.GetGlobalRect();
            if (r.HasPoint(pos)) { best = i; break; }
            float d = (pos - (r.Position + r.Size * 0.5f)).LengthSquared();
            if (d < bestD) { bestD = d; best = i; }
        }
        var rect = _cards[best].card.GetGlobalRect();
        bool after = pos.X > rect.Position.X + rect.Size.X * 0.5f;
        slot = best + (after ? 1 : 0);
        lineX = after ? rect.Position.X + rect.Size.X + 3f : rect.Position.X - 3f;
        top = rect.Position.Y; height = rect.Size.Y;
        return true;
    }

    private void ClickAt(Vector2 pos)
    {
        if (Host is null) return;
        int id = CardAt(pos);
        if (id < 0)
        {
            // Plain click on empty play area deselects (shift-click leaves it).
            if (!_dragAdditive) Host.SelectedDummyId = null;
            return;
        }
        if (_dragAdditive)
        {
            var cur = new List<int>(Host.SelectedDummyIds);
            if (!cur.Remove(id)) cur.Add(id);
            Host.SelectedDummyIds = cur.ToArray();
        }
        else Host.SelectedDummyId = id;
    }

    private void RectSelect(Rect2 rect)
    {
        if (Host is null) return;
        var picked = new List<int>(_dragAdditive ? Host.SelectedDummyIds : System.Array.Empty<int>());
        int before = picked.Count;
        foreach (var (id, card, _, _) in _cards)
            if (card.GetGlobalRect().Intersects(rect) && !picked.Contains(id)) picked.Add(id);
        // An empty drag over the bar shouldn't clear the current selection.
        if (picked.Count == before && !_dragAdditive) return;
        Host.SelectedDummyIds = picked.ToArray();
    }

    private void FocusAt(Vector2 pos)
    {
        int id = CardAt(pos);
        if (id < 0) return;
        if (Host is not null) Host.SelectedDummyId = id;
        if (Camera is not null) Camera.FollowId = id; // follows until the user pans
    }

    private int CardAt(Vector2 pos)
    {
        foreach (var (id, card, _, _) in _cards)
            if (card.GetGlobalRect().HasPoint(pos)) return id;
        return -1;
    }

    private static Rect2 RectOf(Vector2 a, Vector2 b)
    {
        var tl = new Vector2(Mathf.Min(a.X, b.X), Mathf.Min(a.Y, b.Y));
        var br = new Vector2(Mathf.Max(a.X, b.X), Mathf.Max(a.Y, b.Y));
        return new Rect2(tl, br - tl);
    }

    private void Rebuild(List<DummyState> colonists)
    {
        // Detach immediately (not just QueueFree) so the bar's width reflects
        // only the new cards this frame — otherwise the stale cards linger one
        // frame and the bar flickers off-center.
        foreach (var child in _bar.GetChildren()) { _bar.RemoveChild(child); child.QueueFree(); }
        _cards.Clear();
        _loadoutSig.Clear();
        _weaponSlots.Clear();

        BuildCards(colonists);
    }

    // Purple colonist card: neutral border (the selection outline goes here,
    // around the whole card), the VFD scan-line grid, and a glow only when a
    // glow color is supplied (alpha > 0) — the mood ring lives on the inner
    // frame now, not the card edge.
    private static ScanlineStyleBox CardBox(Color border, Color glow, int glowSize = 0)
    {
        var b = new StyleBoxFlat { BgColor = UiTheme.Panel };
        b.BorderColor = border;
        b.BorderWidthLeft = b.BorderWidthRight = b.BorderWidthTop = b.BorderWidthBottom = 2;
        b.CornerRadiusTopLeft = b.CornerRadiusTopRight = b.CornerRadiusBottomLeft = b.CornerRadiusBottomRight = 8;
        b.ShadowColor = glow;
        b.ShadowSize = glowSize;
        var sb = UiTheme.Scan(b, inset: 5f);
        // Mirror the card's asymmetric padding onto the wrapper (it drives layout).
        sb.SetContentMargin(Side.Left, 11);
        sb.SetContentMargin(Side.Right, 11);
        sb.SetContentMargin(Side.Top, 11);
        sb.SetContentMargin(Side.Bottom, 4);
        return sb;
    }

    // Inner portrait frame: dark inset with the mood-ring border (no glow — the
    // ring reads as a colored edge on the portrait).
    private static StyleBoxFlat FrameBox(Color border)
    {
        var b = new StyleBoxFlat { BgColor = UiTheme.Inset };
        b.BorderColor = border;
        b.BorderWidthLeft = b.BorderWidthRight = b.BorderWidthTop = b.BorderWidthBottom = 4;
        b.CornerRadiusTopLeft = b.CornerRadiusTopRight = b.CornerRadiusBottomLeft = b.CornerRadiusBottomRight = 4;
        return b;
    }

    // Red (low) → amber (mid) → green (high) mood ramp.
    private static Color MoodColor(float mood)
    {
        // Vivid, well-separated hues so orange and green don't blur together.
        var red = new Color(0.98f, 0.10f, 0.10f);
        var amber = new Color(1.00f, 0.58f, 0.00f);
        var green = new Color(0.10f, 0.90f, 0.18f);
        mood = Mathf.Clamp(mood, 0f, 1f);
        return mood < 0.5f ? red.Lerp(amber, mood * 2f) : amber.Lerp(green, (mood - 0.5f) * 2f);
    }

    private void BuildCards(List<DummyState> colonists)
    {
        HBoxContainer? row = null;
        int inRow = 0;
        foreach (var c in colonists)
        {
            int id = c.EntityId;
            if (row is null || inRow >= MaxPerRow)
            {
                row = new HBoxContainer
                {
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Alignment = BoxContainer.AlignmentMode.Center,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                row.AddThemeConstantOverride("separation", 6);
                _bar.AddChild(row);
                inRow = 0;
            }

            var card = new PanelContainer
            {
                CustomMinimumSize = new Vector2(CardWidth, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            card.AddThemeStyleboxOverride("panel", CardBox(UiTheme.Border, UiTheme.Border)); // recolored per-mood each frame
            card.Theme = UiTheme.LabelTheme();

            var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            col.AddThemeConstantOverride("separation", 3);
            card.AddChild(col);

            // Top-down "clone" of the pawn + their equipment, on a dark inset.
            var frame = new PanelContainer { CustomMinimumSize = new Vector2(PortraitSize, PortraitSize), MouseFilter = Control.MouseFilterEnum.Ignore };
            frame.AddThemeStyleboxOverride("panel", FrameBox(UiTheme.Border)); // recolored per-mood each frame
            var portrait = new PortraitView { MouseFilter = Control.MouseFilterEnum.Ignore };
            frame.AddChild(portrait);
            col.AddChild(frame);

            var name = new Label
            {
                Text = c.Name.Split(' ')[0], // first name fits the narrow card
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            name.AddThemeFontSizeOverride("font_size", 13);
            col.AddChild(name);

            // Card + a free-floating weapon icon just BELOW it (outside the card
            // panel), wrapped together so they move/center as one.
            var wrap = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            wrap.AddThemeConstantOverride("separation", 4);
            wrap.AddChild(card);

            var weapon = new PanelContainer
            {
                CustomMinimumSize = new Vector2(0, 30),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            weapon.AddThemeStyleboxOverride("panel", UiTheme.PanelBox(corner: 6, margin: 4));
            wrap.AddChild(weapon);
            _weaponSlots[id] = weapon;

            row.AddChild(wrap);
            _cards.Add((id, card, frame, portrait));
            inRow++;
        }
    }

    // Loadout signature — equipped item paths + drafted state. Portrait only
    // redraws when this changes.
    private static string LoadoutSig(DummyState d)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(d.Drafted ? 'D' : '-');
        sb.Append(IsBleeding(d) ? 'B' : '-');
        sb.Append(IsDowned(d) ? 'X' : '-');
        foreach (var eq in d.Equipped) sb.Append(eq.ItemPath).Append(';');
        return sb.ToString();
    }

    private static bool IsBleeding(in DummyState d) => d.Health.BleedRate > 0f;
    // Downed = knocked out, or legs wrecked enough that they can't move.
    private static bool IsDowned(in DummyState d) => d.Health.Unconscious || d.Health.Moving < 0.10f;

    private static void ApplyLoadout(PortraitView portrait, DummyState d)
    {
        float rangedLen = 0f;
        bool melee = false, armor = false;
        foreach (var eq in d.Equipped)
        {
            if (!ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def)) continue;
            if (def.Ranged is { } r) rangedLen = Mathf.Clamp(r.Range / 60f, 0.35f, 1f);
            else if (def.IsWeapon) melee = true;
            if (def.IsArmor) armor = true;
        }
        portrait.Set(d.Drafted, rangedLen, melee, armor, IsBleeding(d), IsDowned(d));
    }

    // Rebuild the under-name weapon icon for a colonist's current loadout.
    private void RefreshWeaponIcon(int id, in DummyState d)
    {
        if (!_weaponSlots.TryGetValue(id, out var holder)) return;
        foreach (var ch in holder.GetChildren()) { holder.RemoveChild(ch); ch.QueueFree(); }
        var (path, kind) = WeaponIcons.PickEquipped(d);
        holder.AddChild(WeaponIcons.Make(path, kind, pad: 2, bloom: true));
    }

    private void Reposition()
    {
        if (_bar is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        // Use the content min width (settles immediately, unlike Size) so the
        // whole bar stays centered the same frame colonists are added/removed.
        float w = _bar.GetCombinedMinimumSize().X;
        _bar.Position = new Vector2((vp.X - w) * 0.5f, MarginTop);
    }

}
