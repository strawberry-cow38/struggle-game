using System.Collections.Generic;
using Godot;
using StruggleGame.Sim;
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
    private const float ClickSlopPx = 6f;

    private static readonly Color CardBg = new(0.16f, 0.17f, 0.20f);
    private static readonly Color BorderIdle = new(0.32f, 0.34f, 0.40f);
    private static readonly Color BorderSel = new(0.95f, 0.86f, 0.30f);

    private HBoxContainer _bar = null!;
    private readonly List<(int id, PanelContainer card)> _cards = new();
    private readonly Dictionary<int, Vector2> _worldTile = new();
    private string _lastSig = "";

    private bool _dragging;
    private bool _dragAdditive;
    private Vector2 _dragStart;

    public override void _Ready()
    {
        Layer = 94;
        _bar = new HBoxContainer { Name = "ColonistRow", MouseFilter = Control.MouseFilterEnum.Ignore };
        _bar.AddThemeConstantOverride("separation", 6);
        AddChild(_bar);
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

        var sel = new HashSet<int>(Host.SelectedDummyIds);
        foreach (var (id, card) in _cards)
            card.AddThemeStyleboxOverride("panel", MakeBox(CardBg, sel.Contains(id) ? BorderSel : BorderIdle, 2, 4, 4));

        Reposition();
    }

    public override void _Input(InputEvent @event)
    {
        if (Host is null || !_bar.Visible) return;

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
                GetViewport().SetInputAsHandled();
            }
            else if (_dragging)
            {
                _dragging = false;
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
        foreach (var (id, card) in _cards)
            if (card.GetGlobalRect().Intersects(rect) && !picked.Contains(id)) picked.Add(id);
        Host.SelectedDummyIds = picked.ToArray();
    }

    private void FocusAt(Vector2 pos)
    {
        int id = CardAt(pos);
        if (id >= 0 && Camera is not null && _worldTile.TryGetValue(id, out var tile))
            Camera.FocusOn(tile * SimConstants.PixelsPerTile);
        if (id >= 0 && Host is not null) Host.SelectedDummyId = id;
    }

    private int CardAt(Vector2 pos)
    {
        foreach (var (id, card) in _cards)
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
        foreach (var child in _bar.GetChildren()) child.QueueFree();
        _cards.Clear();

        foreach (var c in colonists)
        {
            int id = c.EntityId;

            var card = new PanelContainer
            {
                CustomMinimumSize = new Vector2(CardWidth, 0),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            card.AddThemeStyleboxOverride("panel", MakeBox(CardBg, BorderIdle, 2, 4, 4));

            var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            col.AddThemeConstantOverride("separation", 3);
            card.AddChild(col);

            var portrait = new Panel
            {
                CustomMinimumSize = new Vector2(PortraitSize, PortraitSize),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            portrait.AddThemeStyleboxOverride("panel", MakeBox(PortraitTint(id), new Color(0.10f, 0.10f, 0.12f), 1, 3, 0));
            col.AddChild(portrait);

            var name = new Label
            {
                Text = $"#{id}",
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            name.AddThemeFontSizeOverride("font_size", 13);
            col.AddChild(name);

            var weapon = new Panel
            {
                CustomMinimumSize = new Vector2(PortraitSize, 14),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            weapon.AddThemeStyleboxOverride("panel", MakeBox(new Color(0.22f, 0.23f, 0.27f), new Color(0.10f, 0.10f, 0.12f), 1, 2, 0));
            col.AddChild(weapon);

            _bar.AddChild(card);
            _cards.Add((id, card));
        }
    }

    private void Reposition()
    {
        if (_bar is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        _bar.Position = new Vector2((vp.X - _bar.Size.X) * 0.5f, MarginTop);
    }

    private static Color PortraitTint(int id)
    {
        float h = (id * 0.61803398875f) % 1f;
        return Color.FromHsv(h, 0.35f, 0.62f);
    }

    private static StyleBoxFlat MakeBox(Color bg, Color border, int borderWidth, int corner, int margin)
    {
        var box = new StyleBoxFlat { BgColor = bg };
        box.BorderColor = border;
        box.BorderWidthLeft = box.BorderWidthRight = box.BorderWidthTop = box.BorderWidthBottom = borderWidth;
        box.CornerRadiusTopLeft = box.CornerRadiusTopRight = box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = corner;
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = margin;
        return box;
    }
}
