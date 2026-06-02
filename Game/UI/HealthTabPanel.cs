using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Bodies;
using StruggleGame.Sim.Snapshots;

namespace StruggleGame.Game.UI;

// RimWorld-style health tab. Opened from the pawn card's "Health" tab
// button for the selected colonist. Left column = pain + capacities (real
// values where the sim computes them, 0% stubs otherwise); right column =
// the pawn's health conditions (real injuries) or "(no health conditions)".
// Overview tab only for now; Operations is an inert placeholder.
public partial class HealthTabPanel : CanvasLayer
{
    public SimHost? Host { get; set; }
    // Aligned to sit directly above the pawn card and share its width.
    public PawnInfoPanel? PawnPanel { get; set; }

    private const int PanelWidth = 560;  // fallback if the pawn panel is absent
    private const int PanelHeight = 460;
    private const float GapAbovePanel = 8f;

    private static readonly Color PanelBg = new(0.16f, 0.17f, 0.20f, 0.98f);
    private static readonly Color Border = new(0.30f, 0.32f, 0.38f);
    private static readonly Color CapGood = new(0.55f, 0.88f, 0.55f);

    // (label, isReal) — real ones pull from HealthState; the rest are 0% stubs.
    private static readonly (string name, bool real)[] Caps =
    {
        ("Consciousness", true),
        ("Moving", true),
        ("Manipulation", true),
        ("Talking", false),
        ("Eating", false),
        ("Sight", true),
        ("Hearing", false),
        ("Breathing", false),
        ("Blood filtration", false),
        ("Blood pumping", false),
        ("Digestion", false),
    };

    private Panel _root = null!;
    private Label _painValue = null!;
    private readonly Dictionary<string, Label> _capValues = new();
    private VBoxContainer _conditionsCol = null!;
    private Label _conditionsEmpty = null!;
    private string _lastInjurySig = "";

    private int _pawnId = -1;

    public void OpenFor(int pawnId)
    {
        _pawnId = pawnId;
        _lastInjurySig = "";
        _root.Visible = true;
    }

    public void Close() { _root.Visible = false; _pawnId = -1; }

    public bool PanelOpen => _root is not null && _root.Visible;
    public float PanelTop => _root is not null ? _root.Position.Y : float.MaxValue;

    public override void _Ready()
    {
        Layer = 97;

        _root = new Panel
        {
            Visible = false,
            CustomMinimumSize = new Vector2(PanelWidth, PanelHeight),
            Size = new Vector2(PanelWidth, PanelHeight),
        };
        _root.AddThemeStyleboxOverride("panel", MakeBox(PanelBg, Border, 2, 6, 12));
        AddChild(_root);

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 12; vbox.OffsetTop = 12; vbox.OffsetRight = -12; vbox.OffsetBottom = -12;
        vbox.AddThemeConstantOverride("separation", 8);
        _root.AddChild(vbox);

        // Tab row: Overview (active) / Operations (inert) ... X close.
        var tabRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        tabRow.AddThemeConstantOverride("separation", 4);
        tabRow.AddChild(MakeTab("Overview", active: true));
        tabRow.AddChild(MakeTab("Operations", active: false));
        tabRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 28), FocusMode = Control.FocusModeEnum.None };
        closeBtn.Pressed += Close;
        tabRow.AddChild(closeBtn);
        vbox.AddChild(tabRow);

        vbox.AddChild(new HSeparator());

        var body = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(body);

        // Left: pain + capacities.
        var leftCol = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        leftCol.AddThemeConstantOverride("separation", 6);
        body.AddChild(leftCol);

        leftCol.AddChild(BuildRow("Pain", out _painValue));
        leftCol.AddChild(new HSeparator());
        foreach (var (name, _) in Caps)
        {
            leftCol.AddChild(BuildRow(name, out var val));
            _capValues[name] = val;
        }

        body.AddChild(new VSeparator());

        // Right: conditions.
        var rightCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        rightCol.AddThemeConstantOverride("separation", 4);
        body.AddChild(rightCol);

        _conditionsCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _conditionsCol.AddThemeConstantOverride("separation", 3);
        rightCol.AddChild(_conditionsCol);

        _conditionsEmpty = new Label
        {
            Text = "(no health conditions)",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _conditionsEmpty.AddThemeColorOverride("font_color", new Color(0.55f, 0.58f, 0.64f));
        rightCol.AddChild(_conditionsEmpty);

        GetTree().Root.SizeChanged += Recenter;
        CallDeferred(nameof(Recenter));
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Recenter;
    }

    public override void _Process(double delta)
    {
        if (!_root.Visible) return;
        if (Host?.LatestSnapshot is not { } snap) return;

        // Close if the pawn vanished / deselected.
        DummyState? found = null;
        foreach (var d in snap.Dummies)
            if (d.EntityId == _pawnId) { found = d; break; }
        if (found is not { } p) { Close(); return; }

        var hs = p.Health;
        _painValue.Text = hs.Pain <= 0.001f ? "None" : $"{hs.Pain * 100f:0}%";

        foreach (var (name, real) in Caps)
            _capValues[name].Text = $"{(real ? CapValue(hs, name) : 0f) * 100f:0}%";

        // Conditions (rebuild only on change).
        string sig = InjurySig(hs.Injuries);
        if (sig != _lastInjurySig)
        {
            _lastInjurySig = sig;
            foreach (var c in _conditionsCol.GetChildren()) c.QueueFree();
            _conditionsEmpty.Visible = hs.Injuries.Length == 0;
            foreach (var inj in hs.Injuries) _conditionsCol.AddChild(BuildConditionRow(inj));
        }

        Recenter();
    }

    private static float CapValue(HealthState hs, string name) => name switch
    {
        "Consciousness" => hs.Consciousness,
        "Moving" => hs.Moving,
        "Manipulation" => hs.Manipulation,
        "Sight" => hs.Sight,
        _ => 0f,
    };

    private void Recenter()
    {
        if (_root is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        float w = PawnPanel?.PanelWidthPx ?? PanelWidth;
        float x = PawnPanel?.PanelLeft ?? 12f;
        float panelTop = PawnPanel is { PanelOpen: true } pp ? pp.PanelTop : vp.Y - (PawnPanel?.PanelMarginBottom ?? 16f);
        float y = panelTop - GapAbovePanel - PanelHeight;
        _root.Size = new Vector2(w, PanelHeight);
        _root.Position = new Vector2(x, y);
    }

    private static HBoxContainer BuildRow(string label, out Label value)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var name = new Label { Text = label, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        name.AddThemeFontSizeOverride("font_size", 15);
        value = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(80, 0) };
        value.AddThemeFontSizeOverride("font_size", 15);
        value.AddThemeColorOverride("font_color", CapGood);
        row.AddChild(name);
        row.AddChild(value);
        return row;
    }

    private static Label BuildConditionRow(InjuryState inj)
    {
        string part = BodyTree.TryGet(inj.PartId, out var def) ? def.DisplayName : inj.PartId;
        string detail = inj.Kind switch
        {
            ConditionKind.Missing => "missing",
            ConditionKind.Scar => "scar",
            ConditionKind.Gunshot when inj.Caliber is not null =>
                $"gunshot {inj.Severity:0} — {inj.Caliber}, {(inj.Lodged ? "lodged" : "through")}",
            _ => $"{inj.Kind.ToString().ToLower()} {inj.Severity:0}",
        };
        var line = new Label { Text = $"{part}: {detail}" };
        line.AddThemeFontSizeOverride("font_size", 13);
        Color c = inj.Kind == ConditionKind.Missing
            ? new Color(1f, 0.4f, 0.4f)
            : inj.Severity >= 12f ? new Color(1f, 0.6f, 0.3f) : new Color(0.9f, 0.85f, 0.6f);
        line.AddThemeColorOverride("font_color", c);
        return line;
    }

    private static string InjurySig(InjuryState[] injuries)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in injuries) sb.Append(i.PartId).Append((int)i.Kind).Append((int)(i.Severity * 10)).Append(';');
        return sb.ToString();
    }

    private static Button MakeTab(string text, bool active)
    {
        var t = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(120, 30) };
        var bg = active ? new Color(0.24f, 0.26f, 0.30f) : new Color(0.13f, 0.14f, 0.17f);
        var box = MakeBox(bg, Border, 1, 4, 4);
        t.AddThemeStyleboxOverride("normal", box);
        t.AddThemeStyleboxOverride("hover", box);
        t.AddThemeStyleboxOverride("pressed", box);
        return t;
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
