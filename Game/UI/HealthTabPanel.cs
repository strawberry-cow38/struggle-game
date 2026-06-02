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
    private Label _bleedValue = null!;
    private Label _deathValue = null!;
    private readonly Dictionary<string, Label> _capValues = new();
    private VBoxContainer _conditionsCol = null!;
    private Label _conditionsEmpty = null!;
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
        if (!_open) return;
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
            _conditionsEmpty.Visible = hs.Injuries.Length == 0;
            foreach (var g in GroupInjuries(hs.Injuries)) _conditionsCol.AddChild(BuildConditionRow(g));
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
        float Bleed, bool Tended, bool Stabilized);

    private static List<InjuryGroup> GroupInjuries(InjuryState[] injuries)
    {
        var map = new Dictionary<(string, ConditionKind, string?, bool), (int n, float maxSev, float bleed, bool allTended, bool anyStab)>();
        var order = new List<(string, ConditionKind, string?, bool)>();
        foreach (var inj in injuries)
        {
            var key = (inj.PartId, inj.Kind, inj.Caliber, inj.Lodged);
            if (map.TryGetValue(key, out var cur))
                map[key] = (cur.n + 1, System.Math.Max(cur.maxSev, inj.Severity), cur.bleed + inj.Bleed, cur.allTended && inj.Tended, cur.anyStab || inj.Stabilized);
            else { map[key] = (1, inj.Severity, inj.Bleed, inj.Tended, inj.Stabilized); order.Add(key); }
        }
        var list = new List<InjuryGroup>();
        foreach (var key in order)
        {
            var v = map[key];
            list.Add(new InjuryGroup(key.Item1, key.Item2, v.n, v.maxSev, key.Item3, key.Item4, v.bleed, v.allTended, v.anyStab));
        }
        return list;
    }

    // Trim long caliber names for the conditions list (9x19mm Parabellum -> Para).
    private static string ShortCaliber(string caliber)
        => caliber.Contains("Parabellum") ? "Para" : caliber;

    private static Control BuildConditionRow(InjuryGroup g)
    {
        string part = BodyTree.TryGet(g.PartId, out var def) ? def.DisplayName : g.PartId;
        string detail = g.Kind switch
        {
            ConditionKind.Missing => "missing",
            ConditionKind.Scar => "scar",
            ConditionKind.Gunshot when g.Caliber is not null =>
                $"gunshot — {ShortCaliber(g.Caliber)}, {(g.Lodged ? "lodged" : "through & through")}",
            _ => g.Kind.ToString().ToLower(),
        };
        string countTag = g.Count > 1 ? $"  x{g.Count}" : "";

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var line = new Label { Text = $"{part}: {detail}{countTag}", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        line.AddThemeFontSizeOverride("font_size", 14);
        Color c = g.Kind == ConditionKind.Missing
            ? new Color(1f, 0.4f, 0.4f)
            : g.MaxSeverity >= 12f ? new Color(1f, 0.6f, 0.3f) : new Color(0.9f, 0.85f, 0.6f);
        line.AddThemeColorOverride("font_color", c);
        row.AddChild(line);

        var icon = new ConditionIcon { CustomMinimumSize = new Vector2(28, 18), MouseFilter = Control.MouseFilterEnum.Ignore };
        icon.Set(g.Tended ? 0f : g.Bleed, g.Tended, g.Stabilized);
        row.AddChild(icon);
        return row;
    }

    private static string InjurySig(InjuryState[] injuries)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in injuries)
            sb.Append(i.PartId).Append((int)i.Kind).Append((int)(i.Severity * 10)).Append(i.Caliber)
              .Append(i.Lodged ? 'L' : 'T').Append(i.Tended ? 'b' : '-').Append(i.Stabilized ? 's' : '-')
              .Append((int)(i.Bleed * 1000)).Append(';');
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
