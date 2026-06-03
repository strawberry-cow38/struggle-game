using System.Collections.Generic;
using Godot;
using StruggleGame.Sim;
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

    private const int PanelWidth = 700;  // wider than the pawn card to fit conditions
    private const int PanelHeight = 460;
    private const float GapAbovePanel = 8f;

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
    private VBoxContainer _vbox = null!;
    private StyleBoxFlat _panelBox = null!;
    private double _glowT;
    private Button _overviewTab = null!;
    private Button _opsTab = null!;
    private Control _body = null!;
    private Control _opsStub = null!;
    private Label _painValue = null!;
    private Label _bleedValue = null!;
    private Label _deathValue = null!;
    private readonly Dictionary<string, Label> _capValues = new();
    private VBoxContainer _conditionsCol = null!;
    private ScrollContainer _scroll = null!;
    private PopupMenu _rowMenu = null!;
    private string _menuPartId = "";
    private string _lastInjurySig = "";

    private bool _open;
    private int _shownPawn = -1;

    // Tab follows the live selection rather than locking to one pawn, so it
    // updates every frame and switches colonists when the selection does.
    public void OpenFor(int pawnId)
    {
        _open = true;
        _lastInjurySig = "";
        _root.Visible = true;
    }

    public void Close() { _open = false; _root.Visible = false; }

    public bool PanelOpen => _root is not null && _root.Visible;
    public float PanelTop => _root is not null ? _root.Position.Y : float.MaxValue;

    public override void _Ready()
    {
        Layer = 97;

        _root = new Panel
        {
            Visible = false,
            CustomMinimumSize = new Vector2(PanelWidth, 0),
            Size = new Vector2(PanelWidth, PanelHeight),
        };
        _panelBox = UiTheme.PanelBox(corner: 12, margin: 0);
        _root.AddThemeStyleboxOverride("panel", _panelBox);
        _root.Theme = UiTheme.LabelTheme(); // outlined, readable text over the glass
        AddChild(new GlassBackdrop { Target = _root, Corner = 12f }); // frosted blur behind
        AddChild(_root);

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        vbox.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vbox.OffsetLeft = 12; vbox.OffsetTop = 12; vbox.OffsetRight = -12; vbox.OffsetBottom = -12;
        vbox.AddThemeConstantOverride("separation", 8);
        _root.AddChild(vbox);
        _vbox = vbox;

        // Tab row: Overview / Operations ... X close. The open tab is highlighted.
        var tabRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        tabRow.AddThemeConstantOverride("separation", 4);
        _overviewTab = MakeTab("Overview");
        _overviewTab.Pressed += () => SetActiveTab(true);
        tabRow.AddChild(_overviewTab);
        _opsTab = MakeTab("Operations");
        _opsTab.Pressed += () => SetActiveTab(false);
        tabRow.AddChild(_opsTab);
        tabRow.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });
        var closeBtn = MakeTab("X");
        closeBtn.CustomMinimumSize = new Vector2(34, 30);
        closeBtn.AddThemeColorOverride("font_color", new Color(0.92f, 0.34f, 0.34f)); // red X
        closeBtn.Pressed += Close;
        tabRow.AddChild(closeBtn);
        vbox.AddChild(tabRow);

        vbox.AddChild(new HSeparator());

        var body = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);
        vbox.AddChild(body);
        _body = body;

        // Left: pain + capacities.
        var leftCol = new VBoxContainer { CustomMinimumSize = new Vector2(215, 0) };
        leftCol.AddThemeConstantOverride("separation", 6);
        body.AddChild(leftCol);

        leftCol.AddChild(BuildRow("Pain", out _painValue));
        leftCol.AddChild(BuildRow("Bleeding", out _bleedValue));
        leftCol.AddChild(BuildRow("Death in", out _deathValue));
        leftCol.AddChild(new HSeparator());
        foreach (var (name, _) in Caps)
        {
            leftCol.AddChild(BuildRow(name, out var val));
            _capValues[name] = val;
        }

        // Visible divider between stats (left) and conditions (right) — the
        // flat-themed VSeparator is near-invisible, so draw an explicit line.
        var divider = new Panel { CustomMinimumSize = new Vector2(2, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Ignore };
        divider.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = UiTheme.Border });
        body.AddChild(divider);

        // Right: conditions, in a scroll view with an always-reserved scrollbar.
        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
        };
        body.AddChild(_scroll);

        // Style the scrollbar to match — pastel grabber on a dim track.
        var vbar = _scroll.GetVScrollBar();
        vbar.CustomMinimumSize = new Vector2(9, 0); // slim visible width
        vbar.AddThemeStyleboxOverride("scroll", UiTheme.InsetBox(UiTheme.Inset, corner: 4));
        vbar.AddThemeStyleboxOverride("grabber", UiTheme.Box(UiTheme.PanelDeep, UiTheme.Border, 1, 4, 0, glow: false));
        vbar.AddThemeStyleboxOverride("grabber_highlight", UiTheme.Box(UiTheme.PanelDeep.Lightened(0.08f), UiTheme.Border, 1, 4, 0, glow: false));
        vbar.AddThemeStyleboxOverride("grabber_pressed", UiTheme.Box(UiTheme.PanelDeep.Lightened(0.14f), UiTheme.Border, 1, 4, 0, glow: false));

        _conditionsCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _conditionsCol.AddThemeConstantOverride("separation", 3);
        _scroll.AddChild(_conditionsCol);

        // Operations tab content (placeholder for now).
        _opsStub = new Label
        {
            Text = "(no operations available)",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 160),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _opsStub.AddThemeColorOverride("font_color", new Color(0.55f, 0.58f, 0.64f));
        vbox.AddChild(_opsStub);

        _rowMenu = new PopupMenu();
        _rowMenu.IdPressed += OnRowMenuPressed;
        AddChild(_rowMenu);

        SetActiveTab(true);
        GetTree().Root.SizeChanged += Recenter;
        CallDeferred(nameof(Recenter));
    }

    private void OnRowMenuPressed(long id)
    {
        if (id == 1 && Host?.SelectedDummyId is int pid && _menuPartId.Length > 0)
            Host.QueueCommand(new StruggleGame.Sim.Commands.RequestBulletRemovalCommand(pid, _menuPartId));
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Recenter;
    }

    // While hovering the conditions scroll area, route the wheel into it and
    // swallow the event so it doesn't reach the camera (no map zoom). Runs in
    // _Input, ahead of the camera's _UnhandledInput.
    public override void _Input(InputEvent @event)
    {
        if (!_root.Visible || _scroll is null) return;
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown)) return;
        if (!_scroll.GetGlobalRect().HasPoint(mb.Position)) return;

        var bar = _scroll.GetVScrollBar();
        bar.Value += mb.ButtonIndex == MouseButton.WheelDown ? 40 : -40;
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!_open) return;
        _glowT += delta;
        UiTheme.AnimateGlow(_panelBox, _glowT);
        if (Host?.LatestSnapshot is not { } snap) return;

        // Follow the live selection; close when nothing's selected.
        int? sel = Host.SelectedDummyId;
        if (sel is null) { Close(); return; }
        DummyState? found = null;
        foreach (var d in snap.Dummies)
            if (d.EntityId == sel.Value) { found = d; break; }
        if (found is not { } p) { Close(); return; }

        bool pawnChanged = sel.Value != _shownPawn;
        _shownPawn = sel.Value;

        var hs = p.Health;
        _painValue.Text = $"{hs.Pain * 100f:0}%";
        _painValue.AddThemeColorOverride("font_color", CapColor(1f - hs.Pain)); // high pain = bad
        _bleedValue.Text = $"{hs.BleedRate * 100f:0.0}%";
        _bleedValue.AddThemeColorOverride("font_color", CapColor(1f - Mathf.Min(1f, hs.BleedRate * 10f)));

        // Bleed-out estimate (only while actively bleeding). Game-hours is
        // fixed by the sim; real time scales with the current game speed.
        if (hs.BleedRate > 0f && Host is not null)
        {
            double gameHours = hs.BloodLevel * SimRuntime.SimSecondsPerRealSecond / (hs.BleedRate * 3600.0);
            double realSec = hs.BloodLevel / (hs.BleedRate * SimConstants.TickSeconds * System.Math.Max(1, Host.TickHz));
            _deathValue.Text = $"{gameHours:0.0}h ({FormatDuration(realSec)})";
            _deathValue.AddThemeColorOverride("font_color", new Color(0.95f, 0.45f, 0.40f));
        }
        else
        {
            _deathValue.Text = "N/A";
            _deathValue.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        }

        foreach (var (name, real) in Caps)
        {
            float v = real ? CapValue(hs, name) : 0f;
            var lbl = _capValues[name];
            lbl.Text = $"{v * 100f:0}%";
            lbl.AddThemeColorOverride("font_color", CapColor(v));
        }

        // Conditions (rebuild only on change), grouped by part+kind.
        string sig = InjurySig(hs.Injuries);
        if (sig != _lastInjurySig || pawnChanged)
        {
            _lastInjurySig = sig;
            foreach (var c in _conditionsCol.GetChildren()) c.QueueFree();
            if (hs.Injuries.Length == 0)
            {
                var empty = new Label { Text = "(no health conditions)" };
                empty.AddThemeColorOverride("font_color", new Color(0.55f, 0.58f, 0.64f));
                _conditionsCol.AddChild(empty);
            }
            else
            {
                var groups = GroupInjuries(hs.Injuries);
                groups.Sort((a, b) => PartOrder(a.PartId).CompareTo(PartOrder(b.PartId)));
                // Tree: a header per body part, its conditions indented beneath.
                string? curPart = null;
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var g = groups[gi];
                    if (g.PartId != curPart)
                    {
                        curPart = g.PartId;
                        _conditionsCol.AddChild(BuildPartHeader(g.PartId));
                    }
                    bool last = gi + 1 >= groups.Count || groups[gi + 1].PartId != g.PartId;
                    _conditionsCol.AddChild(BuildConditionRow(g, last));
                }
            }
        }

        Recenter();
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 0) seconds = 0;
        if (seconds >= 60)
        {
            int m = (int)(seconds / 60);
            int s = (int)(seconds % 60);
            return $"{m}m{s:00}s";
        }
        return $"{seconds:0}s";
    }

    // Red (low) → amber → green (high).
    private static Color CapColor(float v)
    {
        v = Mathf.Clamp(v, 0f, 1f);
        var low = new Color(0.85f, 0.32f, 0.30f);
        var mid = new Color(0.85f, 0.70f, 0.25f);
        var high = new Color(0.55f, 0.88f, 0.55f);
        return v < 0.5f ? low.Lerp(mid, v / 0.5f) : mid.Lerp(high, (v - 0.5f) / 0.5f);
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
        float w = PanelWidth; // wider than the card so conditions fit
        float x = PawnPanel?.PanelLeft ?? 12f;
        float panelTop = PawnPanel is { PanelOpen: true } pp ? pp.PanelTop : vp.Y - (PawnPanel?.PanelMarginBottom ?? 16f);
        // Fit the panel to its content (the capacity column) so there's no
        // dead space below Digestion; clamp so the conditions area keeps a floor.
        float h = _vbox is null ? PanelHeight : Mathf.Max(220f, _vbox.GetCombinedMinimumSize().Y + 24f);
        float y = panelTop - GapAbovePanel - h;
        _root.Size = new Vector2(w, h);
        _root.Position = new Vector2(x, y);
    }

    private static HBoxContainer BuildRow(string label, out Label value)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var name = new Label { Text = $"{label}:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        name.AddThemeFontSizeOverride("font_size", 14);
        value = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(58, 0) };
        value.AddThemeFontSizeOverride("font_size", 14);
        value.AddThemeColorOverride("font_color", CapGood);
        row.AddChild(name);
        row.AddChild(value);
        return row;
    }

    private readonly record struct InjuryGroup(
        string PartId, ConditionKind Kind, int Count, float MaxSeverity, string? Caliber, bool Lodged,
        float Bleed, bool Tended, bool Stabilized, float TendQuality, bool RemovalRequested);

    private static List<InjuryGroup> GroupInjuries(InjuryState[] injuries)
    {
        // Key includes treatment + bullet state, so only wounds that are
        // identical in every respect merge; differing states split out.
        var map = new Dictionary<(string, ConditionKind, string?, bool, bool, bool, bool), (int n, float maxSev, float bleed, float tendQ)>();
        var order = new List<(string, ConditionKind, string?, bool, bool, bool, bool)>();
        foreach (var inj in injuries)
        {
            var key = (inj.PartId, inj.Kind, inj.Caliber, inj.Lodged, inj.Tended, inj.Stabilized, inj.RemovalRequested);
            if (map.TryGetValue(key, out var cur))
                map[key] = (cur.n + 1, System.Math.Max(cur.maxSev, inj.Severity), cur.bleed + inj.Bleed, System.Math.Max(cur.tendQ, inj.TendQuality));
            else { map[key] = (1, inj.Severity, inj.Bleed, inj.TendQuality); order.Add(key); }
        }
        var list = new List<InjuryGroup>();
        foreach (var key in order)
        {
            var v = map[key];
            list.Add(new InjuryGroup(key.Item1, key.Item2, v.n, v.maxSev, key.Item3, key.Item4, v.bleed, key.Item5, key.Item6, v.tendQ, key.Item7));
        }
        return list;
    }

    // Head-to-toe ordering; WholeBody pinned to the very top, unknowns last.
    private static readonly string[] _partOrder =
    {
        "WholeBody", "Head", "Brain", "EyeL", "EyeR", "EarL", "EarR", "Neck",
        "Torso", "Heart", "LungL", "LungR", "ArmL", "HandL", "ArmR", "HandR",
        "Body", "LegL", "FootL", "LegR", "FootR",
    };
    private static int PartOrder(string partId)
    {
        int i = System.Array.IndexOf(_partOrder, partId);
        return i < 0 ? _partOrder.Length : i;
    }

    // Trim long caliber names for the conditions list (Parabellum -> Para,
    // keeping the 9x19mm prefix).
    private static string ShortCaliber(string caliber)
        => caliber.Replace("Parabellum", "Para");

    // A body-part header (tree title).
    private static Label BuildPartHeader(string partId)
    {
        string name = partId == "WholeBody" ? "Whole Body"
            : BodyTree.TryGet(partId, out var def) ? def.DisplayName : partId;
        var h = new Label { Text = name };
        h.AddThemeFontSizeOverride("font_size", 14);
        h.AddThemeColorOverride("font_color", UiTheme.Accent);
        return h;
    }

    private Control BuildConditionRow(InjuryGroup g, bool last)
    {
        string detail = g.Kind switch
        {
            ConditionKind.Missing => "Missing",
            ConditionKind.Sickness => "Sickness",
            ConditionKind.Scar => "Scar",
            ConditionKind.Gunshot when g.Caliber is not null =>
                $"Gunshot — {ShortCaliber(g.Caliber)}",
            _ => g.Kind.ToString(),
        };
        string countTag = g.Count > 1 ? $"  x{g.Count}" : "";
        string queued = g.RemovalRequested ? "[Q] " : "";

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        row.AddThemeConstantOverride("separation", 4);
        // L/T tree connector to show the child belongs to the part above.
        row.AddChild(new Control { CustomMinimumSize = new Vector2(10, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        row.AddChild(new TreeElbow { Last = last, CustomMinimumSize = new Vector2(16, 18), MouseFilter = Control.MouseFilterEnum.Ignore });
        // Right-click a lodged gunshot to queue/cancel bullet-removal surgery.
        if (g.Lodged && g.Kind == ConditionKind.Gunshot)
        {
            string partId = g.PartId;
            bool requested = g.RemovalRequested;
            row.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Right)
                {
                    _menuPartId = partId;
                    _rowMenu.Clear();
                    _rowMenu.AddItem(requested ? "Cancel bullet removal" : "Remove bullet", 1);
                    _rowMenu.Position = (Vector2I)GetViewport().GetMousePosition();
                    _rowMenu.Popup();
                    GetViewport().SetInputAsHandled();
                }
            };
        }
        var line = new Label { Text = $"{queued}{detail}{countTag}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        line.AddThemeFontSizeOverride("font_size", 14);
        Color c = g.Kind == ConditionKind.Missing
            ? new Color(1f, 0.4f, 0.4f)
            : g.MaxSeverity >= 12f ? new Color(1f, 0.6f, 0.3f) : new Color(0.9f, 0.85f, 0.6f);
        line.AddThemeColorOverride("font_color", c);
        row.AddChild(line);

        // Ballistic marker (lodged / through) left of the status icon — shown
        // independently of treatment, so a lodged round still reads as lodged
        // even once tended or stabilized.
        var ballistic = new BallisticIcon { CustomMinimumSize = new Vector2(20, 18), MouseFilter = Control.MouseFilterEnum.Stop };
        ballistic.Set(g.Kind == ConditionKind.Gunshot, g.Lodged, g.Tended);
        if (g.Kind == ConditionKind.Gunshot)
            ballistic.TooltipText = g.Lodged
                ? "Lodged — the round is stuck in the body"
                : "Through & through — the round passed clean through";
        row.AddChild(ballistic);

        var icon = new ConditionIcon { CustomMinimumSize = new Vector2(28, 18), MouseFilter = Control.MouseFilterEnum.Stop };
        icon.Set(g.Tended ? 0f : g.Bleed, g.Tended, g.Stabilized);
        icon.TooltipText = g.Tended ? $"Tended — quality {g.TendQuality * 100f:0}%"
            : g.Stabilized ? $"Stabilized — bleeding {g.Bleed * 100f:0.0}%"
            : g.Bleed > 0f ? $"Bleeding {g.Bleed * 100f:0.0}%"
            : "";
        row.AddChild(icon);
        // Padding so the icon doesn't butt against the scrollbar.
        row.AddChild(new Control { CustomMinimumSize = new Vector2(8, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        return row;
    }

    private static string InjurySig(InjuryState[] injuries)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in injuries)
            sb.Append(i.PartId).Append((int)i.Kind).Append((int)(i.Severity * 10)).Append(i.Caliber)
              .Append(i.Lodged ? 'L' : 'T').Append(i.Tended ? 'b' : '-').Append(i.Stabilized ? 's' : '-')
              .Append(i.RemovalRequested ? 'Q' : '-').Append((int)(i.Bleed * 1000)).Append(';');
        return sb.ToString();
    }

    private static Button MakeTab(string text)
    {
        var t = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(120, 30) };
        StyleTab(t, active: false);
        return t;
    }

    private static void StyleTab(Button t, bool active)
    {
        var box = UiTheme.ButtonBox(active ? UiTheme.ButtonActive : UiTheme.Button, active);
        t.AddThemeStyleboxOverride("normal", box);
        t.AddThemeStyleboxOverride("pressed", box);
        t.AddThemeStyleboxOverride("hover", UiTheme.ButtonBox(active ? UiTheme.ButtonActive : UiTheme.ButtonHover, active));
        t.AddThemeColorOverride("font_color", UiTheme.Text);
    }

    // Highlight the open tab + swap its content (Operations is a stub for now).
    private void SetActiveTab(bool overview)
    {
        StyleTab(_overviewTab, overview);
        StyleTab(_opsTab, !overview);
        _body.Visible = overview;
        _opsStub.Visible = !overview;
    }
}
