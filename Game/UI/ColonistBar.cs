using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// Top-center colonist portrait bar (RimWorld-style). One card per living
// colonist: a portrait placeholder (no art yet), the name below, and a
// weapon-icon placeholder (dud) below that. Clicking a card selects that
// colonist; shift/ctrl-click adds/removes from the multi-selection. Mirrors
// Host.SelectedDummyIds so it stays in sync with world selection.
public partial class ColonistBar : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PortraitSize = 52;
    private const int CardWidth = 66;
    private const int MarginTop = 8;

    private static readonly Color CardBg = new(0.16f, 0.17f, 0.20f);
    private static readonly Color BorderIdle = new(0.32f, 0.34f, 0.40f);
    private static readonly Color BorderSel = new(0.95f, 0.86f, 0.30f);

    private HBoxContainer _bar = null!;
    private readonly List<(int id, PanelContainer card)> _cards = new();
    private string _lastSig = "";

    public override void _Ready()
    {
        Layer = 94;
        _bar = new HBoxContainer { Name = "ColonistRow", MouseFilter = Control.MouseFilterEnum.Pass };
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

        // Gather living colonists (not enemies) in stable id order.
        var colonists = new List<DummyState>();
        foreach (var d in snap.Dummies)
            if (!d.IsEnemy) colonists.Add(d);
        colonists.Sort((a, b) => a.EntityId.CompareTo(b.EntityId));

        _bar.Visible = colonists.Count > 0;

        // Rebuild cards only when the colonist set changes.
        var sb = new System.Text.StringBuilder();
        foreach (var c in colonists) sb.Append(c.EntityId).Append(',');
        string sig = sb.ToString();
        if (sig != _lastSig)
        {
            _lastSig = sig;
            Rebuild(colonists);
        }

        // Selection highlight every frame.
        var sel = new HashSet<int>(Host.SelectedDummyIds);
        foreach (var (id, card) in _cards)
            card.AddThemeStyleboxOverride("panel", MakeBox(CardBg, sel.Contains(id) ? BorderSel : BorderIdle, 2, 4, 4));

        Reposition();
    }

    private void Rebuild(List<DummyState> colonists)
    {
        foreach (var child in _bar.GetChildren()) child.QueueFree();
        _cards.Clear();

        foreach (var c in colonists)
        {
            int id = c.EntityId;

            var card = new PanelContainer { CustomMinimumSize = new Vector2(CardWidth, 0) };
            card.AddThemeStyleboxOverride("panel", MakeBox(CardBg, BorderIdle, 2, 4, 4));
            card.MouseFilter = Control.MouseFilterEnum.Stop;
            card.GuiInput += e => OnCardInput(e, id);

            var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            col.AddThemeConstantOverride("separation", 3);
            card.AddChild(col);

            // Portrait placeholder — flat box tinted from the id so colonists
            // are at least distinguishable until real art lands.
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
            name.AddThemeFontSizeOverride("font_size", 11);
            col.AddChild(name);

            // Weapon-icon placeholder (dud for now).
            var weapon = new Panel
            {
                CustomMinimumSize = new Vector2(PortraitSize, 12),
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            weapon.AddThemeStyleboxOverride("panel", MakeBox(new Color(0.22f, 0.23f, 0.27f), new Color(0.10f, 0.10f, 0.12f), 1, 2, 0));
            col.AddChild(weapon);

            _bar.AddChild(card);
            _cards.Add((id, card));
        }
    }

    private void OnCardInput(InputEvent @event, int id)
    {
        if (Host is null) return;
        if (@event is not InputEventMouseButton mb || !mb.Pressed || mb.ButtonIndex != MouseButton.Left) return;

        if (mb.ShiftPressed || mb.CtrlPressed)
        {
            var cur = new List<int>(Host.SelectedDummyIds);
            if (!cur.Remove(id)) cur.Add(id);
            Host.SelectedDummyIds = cur.ToArray();
        }
        else
        {
            Host.SelectedDummyId = id;
        }
        GetViewport().SetInputAsHandled();
    }

    private void Reposition()
    {
        if (_bar is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        _bar.Position = new Vector2((vp.X - _bar.Size.X) * 0.5f, MarginTop);
    }

    // Deterministic pastel tint per colonist id.
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
