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

    private const int PortraitSize = 62;  // +20% over the original 52
    private const int CardWidth = 80;
    private const int MarginTop = 8;
    private const float ClickSlopPx = 9f; // deadzone before a drag-select kicks in

    private HBoxContainer _bar = null!;
    private readonly List<(int id, PanelContainer card, PortraitView portrait)> _cards = new();
    private readonly Dictionary<int, Vector2> _worldTile = new();
    private readonly Dictionary<int, string> _loadoutSig = new();
    private string _lastSig = "";

    private bool _dragging;
    private bool _dragAdditive;
    private Vector2 _dragStart;
    private DragRectOverlay _overlay = null!;
    private int _followId = -1;

    public override void _Ready()
    {
        Layer = 94;
        _bar = new HBoxContainer { Name = "ColonistRow", MouseFilter = Control.MouseFilterEnum.Ignore };
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

        var colonists = new List<DummyState>();
        foreach (var d in snap.Dummies)
            if (!d.IsEnemy) colonists.Add(d);
        colonists.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));

        _bar.Visible = colonists.Count > 0;

        _worldTile.Clear();
        foreach (var c in colonists) _worldTile[c.EntityId] = new Vector2(c.X, c.Y);

        var sb = new System.Text.StringBuilder();
        foreach (var c in colonists) sb.Append(c.EntityId).Append(',');
        string sig = sb.ToString();
        if (sig != _lastSig)
        {
            _lastSig = sig;
            Rebuild(colonists);
        }

        // Selection border + loadout-throttled portrait refresh.
        // Camera follow: feed the live pawn pos while following; drop it once
        // the user pans (the camera clears Following on any non-zoom move).
        if (Camera is not null)
        {
            if (!Camera.Following) _followId = -1;
            else if (_followId >= 0 && _worldTile.TryGetValue(_followId, out var ft))
                Camera.FollowTarget = ft * SimConstants.PixelsPerTile;
        }

        var sel = new HashSet<int>(Host.SelectedDummyIds);
        var byId = new Dictionary<int, DummyState>();
        foreach (var c in colonists) byId[c.EntityId] = c;
        foreach (var (id, card, portrait) in _cards)
        {
            bool selected = sel.Contains(id);
            card.AddThemeStyleboxOverride("panel",
                UiTheme.Box(UiTheme.Panel, selected ? UiTheme.Accent : UiTheme.Border, 2, 8, 4, glow: false));
            if (byId.TryGetValue(id, out var d))
            {
                string lo = LoadoutSig(d);
                if (!_loadoutSig.TryGetValue(id, out var prev) || prev != lo)
                {
                    _loadoutSig[id] = lo;
                    ApplyLoadout(portrait, d);
                }
            }
        }

        Reposition();
    }

    public override void _Input(InputEvent @event)
    {
        if (Host is null || !_bar.Visible) return;

        if (@event is InputEventMouseMotion mm && _dragging)
        {
            _overlay.Cur = mm.Position;
            // Only show the rect once we've moved past the deadzone (play).
            if (!_overlay.Active && (mm.Position - _dragStart).Length() > ClickSlopPx)
                _overlay.Active = true;
            _overlay.QueueRedraw();
            return;
        }
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            var pos = mb.Position;
            if (mb.Pressed)
            {
                if (!_bar.GetGlobalRect().HasPoint(pos)) { _dragging = false; return; }
                if (mb.DoubleClick)
                {
                    FocusAt(pos);
                    _dragging = false;
                    GetViewport().SetInputAsHandled();
                    return;
                }
                _dragStart = pos;
                _dragging = true;
                _dragAdditive = mb.ShiftPressed || mb.CtrlPressed;
                _overlay.Active = false; // activates once past the deadzone
                _overlay.Start = pos;
                _overlay.Cur = pos;
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
        }
    }

    private void ClickAt(Vector2 pos)
    {
        int id = CardAt(pos);
        if (id < 0 || Host is null) return;
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
        foreach (var (id, card, _) in _cards)
            if (card.GetGlobalRect().Intersects(rect) && !picked.Contains(id)) picked.Add(id);
        Host.SelectedDummyIds = picked.ToArray();
    }

    private void FocusAt(Vector2 pos)
    {
        int id = CardAt(pos);
        if (id < 0) return;
        if (Host is not null) Host.SelectedDummyId = id;
        if (Camera is not null && _worldTile.TryGetValue(id, out var tile))
        {
            _followId = id;
            Camera.FollowTarget = tile * SimConstants.PixelsPerTile;
            Camera.Following = true; // tracks until the user pans (not zoom)
        }
    }

    private int CardAt(Vector2 pos)
    {
        foreach (var (id, card, _) in _cards)
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

        foreach (var c in colonists)
        {
            int id = c.EntityId;

            var card = new PanelContainer
            {
                CustomMinimumSize = new Vector2(CardWidth, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            card.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.Panel, UiTheme.Border, 2, 8, 4, glow: false));
            card.Theme = UiTheme.LabelTheme();

            var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            col.AddThemeConstantOverride("separation", 3);
            card.AddChild(col);

            // Top-down "clone" of the pawn + their equipment, on a dark inset.
            var frame = new PanelContainer { CustomMinimumSize = new Vector2(PortraitSize, PortraitSize), MouseFilter = Control.MouseFilterEnum.Ignore };
            frame.AddThemeStyleboxOverride("panel", UiTheme.InsetBox(UiTheme.Inset, corner: 4));
            var portrait = new PortraitView { MouseFilter = Control.MouseFilterEnum.Ignore };
            frame.AddChild(portrait);
            col.AddChild(frame);

            var name = new Label
            {
                Text = $"#{id}",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            name.AddThemeFontSizeOverride("font_size", 13);
            col.AddChild(name);

            _bar.AddChild(card);
            _cards.Add((id, card, portrait));
        }
    }

    // Loadout signature — equipped item paths + drafted state. Portrait only
    // redraws when this changes.
    private static string LoadoutSig(DummyState d)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(d.Drafted ? 'D' : '-');
        foreach (var eq in d.Equipped) sb.Append(eq.ItemPath).Append(';');
        return sb.ToString();
    }

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
        portrait.Set(d.Drafted, rangedLen, melee, armor);
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
