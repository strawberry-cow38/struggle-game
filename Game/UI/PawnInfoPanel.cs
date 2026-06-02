using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected colonist. Today: stub bio + live
// inventory list. Each inventory row shows item name x count, the
// per-unit weight/bulk contribution, a Forbid toggle (sticky — the
// AI will never auto-drop or use a forbidden slot) and a Force Drop
// button (the player override that ejects a slot anyway).
//
// Multi-pawn selection is ignored for now — we render the first
// selected pawn. Per-pawn inventory management for a herd is a
// follow-up.
public partial class PawnInfoPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private const int PanelWidth = 560;
    private const int MarginLeft = 16;
    private const int MarginBottom = 16;

    private Panel _root = null!;

    // For other bottom-left HUD elements to dodge this panel when it's open.
    public bool PanelOpen => _root is not null && _root.Visible;
    public float PanelTop => _root is not null ? _root.Position.Y : float.MaxValue;
    private Label _nameLabel = null!;
    private ProgressBar _healthBar = null!;
    private Label _healthPct = null!;
    private ProgressBar _moodBar = null!;
    private Label _moodPct = null!;
    private Label _bioLabel = null!;
    private Label _foodLabel = null!;
    private ProgressBar _foodBar = null!;
    private Label _foodPct = null!;
    private Label _sleepLabel = null!;
    private ProgressBar _sleepBar = null!;
    private Label _sleepPct = null!;
    private Label _recLabel = null!;
    private ProgressBar _recBar = null!;
    private Label _recPct = null!;
    private Label _activityLabel = null!;
    private Label _weaponLabel = null!;

    private int _shownPawnId = -1;
    private long _lastSnapshotTick = -1;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 290),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        AddChild(_root);

        // Dark slate card (RimWorld-ish), flat-themed (no image assets).
        _root.AddThemeStyleboxOverride("panel", MakeBox(new Color(0.16f, 0.17f, 0.20f, 0.97f),
            border: new Color(0.34f, 0.36f, 0.42f), borderWidth: 2, corner: 6, margin: 10));

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 10, OffsetTop = 10, OffsetRight = -10, OffsetBottom = -10,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 6);
        _root.AddChild(vbox);

        var headerRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _nameLabel = new Label { Text = "Colonist", CustomMinimumSize = new Vector2(0, 28) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 22);
        // Fake bold via a matching outline (no bold font asset bundled).
        _nameLabel.AddThemeConstantOverride("outline_size", 3);
        _nameLabel.AddThemeColorOverride("font_outline_color", new Color(0.93f, 0.93f, 0.95f));
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        // Overall health bar (mean remaining-HP across present body parts).
        vbox.AddChild(BuildNeedRow("Health:", new Color(0.78f, 0.32f, 0.32f), out var _, out _healthBar, out _healthPct));
        // Mood bar (stub 100% for now).
        vbox.AddChild(BuildNeedRow("Mood:", new Color(0.42f, 0.58f, 0.78f), out var _, out _moodBar, out _moodPct));

        vbox.AddChild(new HSeparator());

        // Two columns: needs bars (left) | bio stub (right), vertical divider.
        var midRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        midRow.AddThemeConstantOverride("separation", 12);

        var needsCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.8f };
        needsCol.AddThemeConstantOverride("separation", 4);
        var needsHeader = new Label { Text = "Needs:" };
        needsHeader.AddThemeFontSizeOverride("font_size", 14);
        needsCol.AddChild(needsHeader);
        needsCol.AddChild(BuildNeedRow("Food:", new Color(0.62f, 0.45f, 0.28f), out _foodLabel, out _foodBar, out _foodPct));
        needsCol.AddChild(BuildNeedRow("Sleep:", new Color(0.45f, 0.78f, 0.38f), out _sleepLabel, out _sleepBar, out _sleepPct));
        needsCol.AddChild(BuildNeedRow("Recreation:", new Color(0.72f, 0.62f, 0.22f), out _recLabel, out _recBar, out _recPct));
        midRow.AddChild(needsCol);

        midRow.AddChild(new VSeparator());

        var bioCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1f };
        bioCol.AddThemeConstantOverride("separation", 4);
        var bioHeader = new Label { Text = "Bio:" };
        bioHeader.AddThemeFontSizeOverride("font_size", 14);
        bioCol.AddChild(bioHeader);
        _bioLabel = new Label
        {
            Text = "Stub bio. Name, traits, mood, skills go here later.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        _bioLabel.AddThemeFontSizeOverride("font_size", 11);
        _bioLabel.AddThemeColorOverride("font_color", new Color(0.62f, 0.65f, 0.72f));
        bioCol.AddChild(_bioLabel);
        midRow.AddChild(bioCol);

        vbox.AddChild(midRow);

        vbox.AddChild(new HSeparator());

        // Footer: current activity (left) · equipped weapon (right).
        var activityRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        activityRow.AddThemeConstantOverride("separation", 8);
        _activityLabel = new Label { Text = "Activity:", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _activityLabel.AddThemeFontSizeOverride("font_size", 12);
        _weaponLabel = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right };
        _weaponLabel.AddThemeFontSizeOverride("font_size", 12);
        _weaponLabel.AddThemeColorOverride("font_color", new Color(0.7f, 0.9f, 1.0f));
        activityRow.AddChild(_activityLabel);
        activityRow.AddChild(_weaponLabel);
        vbox.AddChild(activityRow);

        // Dumb tab strip at the bottom (inert — styling only for now),
        // buttons span the full panel width.
        var tabRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        tabRow.AddThemeConstantOverride("separation", 3);
        foreach (var tab in new[] { "Log", "Gear", "Social", "Bio", "Needs", "Health" })
        {
            var t = new Button { Text = tab, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 24) };
            t.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            var tan = MakeBox(new Color(0.45f, 0.36f, 0.22f), border: new Color(0.20f, 0.15f, 0.08f), borderWidth: 1, corner: 4, margin: 4);
            t.AddThemeStyleboxOverride("normal", tan);
            t.AddThemeStyleboxOverride("hover", tan);
            t.AddThemeStyleboxOverride("pressed", tan);
            t.AddThemeColorOverride("font_color", new Color(0.93f, 0.9f, 0.82f));
            tabRow.AddChild(t);
        }
        vbox.AddChild(tabRow);

        GetTree().Root.SizeChanged += Reposition;
        CallDeferred(nameof(Reposition));
    }

    public override void _ExitTree()
    {
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        var snap = Host.LatestSnapshot;
        int? sel = Host.SelectedDummyId;
        if (sel is null || snap is null)
        {
            if (_root.Visible) { _root.Visible = false; _shownPawnId = -1; }
            return;
        }
        if (!_root.Visible) _root.Visible = true;
        Reposition(); // re-anchor bottom-left as content height changes
        if (sel.Value != _shownPawnId || snap.Tick != _lastSnapshotTick)
        {
            Render(snap, sel.Value);
            _shownPawnId = sel.Value;
            _lastSnapshotTick = snap.Tick;
        }
    }

    // Bottom-left anchored card (tracks the panel's current height each frame).
    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
        _root.Position = new Vector2(MarginLeft, vp.Y - _root.Size.Y - MarginBottom);
    }

    // One need row: name on the left, bar filling the middle, % on the right.
    private static HBoxContainer BuildNeedRow(string title, Color fill, out Label name, out ProgressBar bar, out Label pct)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 8);
        name = new Label { Text = title, CustomMinimumSize = new Vector2(96, 0), VerticalAlignment = VerticalAlignment.Center };
        name.AddThemeFontSizeOverride("font_size", 13);
        bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Step = 0.0001,
            CustomMinimumSize = new Vector2(0, 16),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        StyleBar(bar, fill);
        pct = new Label
        {
            Text = "", CustomMinimumSize = new Vector2(48, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        pct.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(name); row.AddChild(bar); row.AddChild(pct);
        return row;
    }

    // Flat StyleBox helper (no image assets) — bg + optional border + rounded
    // corners + uniform content margin.
    private static StyleBoxFlat MakeBox(Color bg, Color border = default, int borderWidth = 0, int corner = 0, int margin = 0)
    {
        var box = new StyleBoxFlat { BgColor = bg };
        if (borderWidth > 0)
        {
            box.BorderColor = border;
            box.BorderWidthLeft = box.BorderWidthRight = box.BorderWidthTop = box.BorderWidthBottom = borderWidth;
        }
        box.CornerRadiusTopLeft = box.CornerRadiusTopRight = box.CornerRadiusBottomLeft = box.CornerRadiusBottomRight = corner;
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = margin;
        return box;
    }

    // Flat colored fill on a dark track; % text hidden (we label it ourselves).
    private static void StyleBar(ProgressBar bar, Color fill)
    {
        bar.ShowPercentage = false;
        bar.AddThemeStyleboxOverride("background", MakeBox(new Color(0.09f, 0.09f, 0.11f), corner: 3));
        bar.AddThemeStyleboxOverride("fill", MakeBox(fill, corner: 3));
    }

    private void Render(SimSnapshot snap, int pawnId)
    {
        DummyState? found = null;
        foreach (var d in snap.Dummies)
        {
            if (d.EntityId == pawnId) { found = d; break; }
        }
        if (found is null)
        {
            // Pawn was removed while selected.
            if (Host is not null) Host.SelectedDummyId = null;
            return;
        }
        var p = found.Value;
        _nameLabel.Text = $"Colonist #{p.EntityId}";

        float oh = p.Health.OverallHealth;
        _healthBar.Value = oh;
        _healthPct.Text = $"{oh * 100f:0}%";
        StyleBar(_healthBar, BarColor(oh));

        float mood = 1f; // stub
        _moodBar.Value = mood;
        _moodPct.Text = $"{mood * 100f:0}%";
        StyleBar(_moodBar, BarColor(mood));

        float food = 1f; // stub
        _foodBar.Value = food;
        _foodPct.Text = $"{food * 100f:0}%";
        StyleBar(_foodBar, BarColor(food));

        _sleepBar.Value = p.SleepLevel;
        _sleepLabel.Text = p.Sleeping ? "Sleep (zzz):" : "Sleep:";
        _sleepPct.Text = $"{p.SleepLevel * 100f:0}%";
        StyleBar(_sleepBar, BarColor((float)p.SleepLevel));

        _recBar.Value = p.RecreationLevel;
        _recLabel.Text = p.AtRecreationKind is RecreationKind k ? $"Rec ({k}):" : "Recreation:";
        _recPct.Text = $"{p.RecreationLevel * 100f:0}%";
        StyleBar(_recBar, BarColor((float)p.RecreationLevel));

        _activityLabel.Text = $"Activity: {p.Job}";
        string weapon = "Unarmed";
        foreach (var eq in p.Equipped)
            if (ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def) && (def.IsWeapon || def.IsRangedWeapon))
            {
                weapon = def.DisplayName;
                if (def.Ranged is { } r) weapon += $" ({CaliberFor(r.AmmoCategoryPath)})";
                break;
            }
        _weaponLabel.Text = $"Equipped: {weapon}";
    }

    // Caliber label for a weapon's ammo category — pulled from the first
    // matching ammo def's display name, with any "(AP/HP/FMJ)" suffix dropped.
    private static string CaliberFor(string ammoCategory)
    {
        foreach (var d in ItemCatalog.ItemsByPath.Values)
            if (d.IsAmmo && d.Ammo is { } a && a.CategoryPath == ammoCategory)
            {
                int paren = d.DisplayName.IndexOf(" (", System.StringComparison.Ordinal);
                return paren >= 0 ? d.DisplayName.Substring(0, paren) : d.DisplayName;
            }
        return ammoCategory;
    }

    // Bar fill: green when high, amber mid, red when low (low % = bad).
    private static Color BarColor(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        var low = new Color(0.78f, 0.22f, 0.22f);   // red
        var mid = new Color(0.82f, 0.62f, 0.18f);   // amber
        var high = new Color(0.40f, 0.74f, 0.34f);  // green
        return v < 0.5f ? low.Lerp(mid, v / 0.5f) : mid.Lerp(high, (v - 0.5f) / 0.5f);
    }
}
