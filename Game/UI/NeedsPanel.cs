using System;
using System.Collections.Generic;
using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// RimWorld-style needs/mood pane. Opened from the pawn card's "Needs" tab for
// the selected colonist. A mood bar on top, then a scrolling list of the good
// (green) and bad (red) thoughts feeding it. The thought list is derived from
// the pawn's current state for now (sleep / recreation / pain / health) until
// the full point-based thought system lands; the layout is the real thing.
public partial class NeedsPanel : CanvasLayer
{
    public SimHost? Host { get; set; }
    public PawnInfoPanel? PawnPanel { get; set; }

    private const int PanelWidth = 700;   // fixed, matches the health pane
    private const int PanelHeight = 460;
    private const float GapAbovePanel = 8f;

    private static readonly Color Good = new(0.55f, 0.86f, 0.48f);
    private static readonly Color Bad = new(0.93f, 0.42f, 0.40f);

    private Panel _root = null!;
    private VBoxContainer _vbox = null!;
    private ScanlineStyleBox _panelBox = null!;
    private double _glowT;

    private ProgressBar _moodBar = null!;
    private Label _moodPct = null!;
    private VBoxContainer _thoughtsCol = null!;
    private ScrollContainer _scroll = null!;
    private string _lastSig = "";

    private bool _open;
    private int _shownPawn = -1;

    public void OpenFor(int pawnId)
    {
        _open = true;
        _lastSig = "";
        _root.Visible = true;
    }

    public void Close() { _open = false; _root.Visible = false; }

    public bool PanelOpen => _root is not null && _root.Visible;
    public float PanelTop => _root is not null ? _root.Position.Y : float.MaxValue;

    public override void _Ready()
    {
        Layer = 97;

        _root = new Panel { Visible = false, CustomMinimumSize = new Vector2(PanelWidth, 0), Size = new Vector2(PanelWidth, PanelHeight) };
        _panelBox = UiTheme.PanelBox(corner: 12, margin: 0);
        _root.AddThemeStyleboxOverride("panel", _panelBox);
        _root.Theme = UiTheme.LabelTheme();
        AddChild(new GlassBackdrop { Target = _root, Corner = 12f });
        AddChild(_root);

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 12; vbox.OffsetTop = 12; vbox.OffsetRight = -12; vbox.OffsetBottom = -12;
        vbox.AddThemeConstantOverride("separation", 8);
        _root.AddChild(vbox);
        _vbox = vbox;

        // Header: title + red X close.
        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var title = new Label { Text = "Needs", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 18);
        header.AddChild(title);
        var closeBtn = UiTheme.CloseButton();
        closeBtn.Pressed += Close;
        header.AddChild(closeBtn);
        vbox.AddChild(header);

        // Mood bar.
        _moodBar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.0001, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 22) };
        _moodBar.AddThemeStyleboxOverride("background", UiTheme.InsetBox(UiTheme.Inset, corner: 4));
        var moodRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var moodLabel = new Label { Text = "Mood:", CustomMinimumSize = new Vector2(72, 0), VerticalAlignment = VerticalAlignment.Center };
        moodLabel.AddThemeFontSizeOverride("font_size", 14);
        _moodBar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _moodPct = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(48, 0), VerticalAlignment = VerticalAlignment.Center };
        _moodPct.AddThemeFontSizeOverride("font_size", 14);
        moodRow.AddThemeConstantOverride("separation", 8);
        moodRow.AddChild(moodLabel); moodRow.AddChild(_moodBar); moodRow.AddChild(_moodPct);
        vbox.AddChild(moodRow);

        vbox.AddChild(new HSeparator());

        var thoughtsHeader = new Label { Text = "Thoughts" };
        thoughtsHeader.AddThemeFontSizeOverride("font_size", 14);
        thoughtsHeader.AddThemeColorOverride("font_color", UiTheme.TextDim);
        vbox.AddChild(thoughtsHeader);

        _scroll = new ScrollContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        vbox.AddChild(_scroll);
        _thoughtsCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _thoughtsCol.AddThemeConstantOverride("separation", 3);
        _scroll.AddChild(_thoughtsCol);
    }

    // Route the scroll wheel into the list when hovering it (no map zoom).
    public override void _Input(InputEvent @event)
    {
        if (!_root.Visible || _scroll is null) return;
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown)) return;
        if (!_scroll.GetGlobalRect().HasPoint(mb.Position)) return;
        _scroll.GetVScrollBar().Value += mb.ButtonIndex == MouseButton.WheelDown ? 40 : -40;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!_open) return;
        _glowT += delta;
        UiTheme.AnimateGlow(_panelBox, _glowT);
        if (Host?.LatestSnapshot is not { } snap) return;

        int? sel = Host.SelectedDummyId;
        if (sel is null) { Close(); return; }
        DummyState? found = null;
        foreach (var d in snap.Dummies)
            if (d.EntityId == sel.Value) { found = d; break; }
        if (found is not { } p) { Close(); return; }

        bool pawnChanged = sel.Value != _shownPawn;
        _shownPawn = sel.Value;

        float mood = p.Mood;
        _moodBar.Value = mood;
        _moodPct.Text = $"{mood * 100f:0}%";
        StyleFill(_moodBar, BarColor(mood));

        var thoughts = DeriveThoughts(p);
        string sig = ThoughtsSig(thoughts);
        if (sig != _lastSig || pawnChanged)
        {
            _lastSig = sig;
            foreach (var c in _thoughtsCol.GetChildren()) c.QueueFree();
            foreach (var (label, pts, good) in thoughts)
                _thoughtsCol.AddChild(ThoughtRow(label, pts, good));
        }

        Recenter();
    }

    // Placeholder thought derivation from current pawn state. Replaced by the
    // real point-based thought system later; the row layout stays the same.
    private static List<(string label, int pts, bool good)> DeriveThoughts(in DummyState p)
    {
        var list = new List<(string, int, bool)>();
        double sleep = p.SleepLevel, rec = p.RecreationLevel;
        float pain = p.Health.Pain, bleed = p.Health.BleedRate, hp = p.Health.OverallHealth;

        if (sleep < 0.30) list.Add(("Exhausted", -(int)Math.Round((0.30 - sleep) * 30) - 3, false));
        else if (sleep > 0.85) list.Add(("Well rested", 4, true));

        if (rec < 0.30) list.Add(("Bored", -(int)Math.Round((0.30 - rec) * 25) - 2, false));
        else if (rec > 0.80) list.Add(("Had some fun", 5, true));

        if (bleed > 0f) list.Add(("Bleeding", -10, false));
        if (pain > 0.01f) list.Add(("In pain", -(int)Math.Round(pain * 40f) - 1, false));
        if (hp < 0.90f) list.Add(("Injured", -(int)Math.Round((0.90f - hp) * 30f) - 1, false));

        if (list.Count == 0) list.Add(("Comfortable", 3, true));

        // Good group on top (biggest gain first), bad group below (biggest hit
        // first) — so the worst bad sits right under the smallest good.
        var goods = list.FindAll(t => t.Item3);
        var bads = list.FindAll(t => !t.Item3);
        goods.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        bads.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        var ordered = new List<(string label, int pts, bool good)>(goods.Count + bads.Count);
        ordered.AddRange(goods);
        ordered.AddRange(bads);
        return ordered;
    }

    private static string ThoughtsSig(List<(string label, int pts, bool good)> ts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (l, p, _) in ts) sb.Append(l).Append(p).Append(';');
        return sb.ToString();
    }

    private static HBoxContainer ThoughtRow(string label, int pts, bool good)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var name = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        name.AddThemeFontSizeOverride("font_size", 14);
        var val = new Label { Text = pts >= 0 ? $"+{pts}" : pts.ToString(), HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(44, 0) };
        val.AddThemeFontSizeOverride("font_size", 14);
        val.AddThemeColorOverride("font_color", good ? Good : Bad);
        row.AddChild(name); row.AddChild(val);
        return row;
    }

    private static void StyleFill(ProgressBar bar, Color c)
    {
        var fill = new StyleBoxFlat { BgColor = c };
        fill.CornerRadiusTopLeft = fill.CornerRadiusTopRight = fill.CornerRadiusBottomLeft = fill.CornerRadiusBottomRight = 4;
        bar.AddThemeStyleboxOverride("fill", fill);
    }

    // Red (low) → amber (mid) → green (high), matching the colonist-bar ramp.
    private static Color BarColor(float v)
    {
        var red = new Color(0.90f, 0.30f, 0.26f);
        var amber = new Color(0.95f, 0.78f, 0.30f);
        var green = new Color(0.45f, 0.82f, 0.45f);
        v = Mathf.Clamp(v, 0f, 1f);
        return v < 0.5f ? red.Lerp(amber, v * 2f) : amber.Lerp(green, (v - 0.5f) * 2f);
    }

    private void Recenter()
    {
        if (_root is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        float x = PawnPanel?.PanelLeft ?? 12f;
        float panelTop = PawnPanel is { PanelOpen: true } pp ? pp.PanelTop : vp.Y - (PawnPanel?.PanelMarginBottom ?? 16f);
        float h = PanelHeight; // fixed, matches the health pane
        float y = panelTop - GapAbovePanel - h;
        _root.Size = new Vector2(PanelWidth, h);
        _root.Position = new Vector2(x, y);
    }
}
