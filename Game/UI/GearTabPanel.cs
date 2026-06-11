using System;
using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.Commands;

namespace StruggleGame.Game.UI;

// Gear pane: top = equipped gear (Unequip / Drop), bottom = general inventory
// (Equip if equippable / Drop X / Drop), with Weight + Bulk carry bars. Opened
// from the pawn card's "Gear" tab. Same width/styling as the Needs/Health panes.
public partial class GearTabPanel : CanvasLayer
{
    public SimHost? Host { get; set; }
    public PawnInfoPanel? PawnPanel { get; set; }
    public HudOverlay? Hud { get; set; }
    public DropQuantityDialog? DropDialog { get; set; }

    private const int PanelWidth = 700;
    private const int PanelHeight = 460;
    private const float GapAbovePanel = 8f;

    private Panel _root = null!;
    private ScanlineStyleBox _panelBox = null!;
    private double _glowT;

    private ProgressBar _weightBar = null!, _bulkBar = null!;
    private Label _weightVal = null!, _bulkVal = null!;
    private VBoxContainer _equippedCol = null!, _invCol = null!;
    private readonly System.Collections.Generic.List<ScrollContainer> _scrolls = new();
    private string _lastSig = "";
    private int _shownPawn = -1;
    private bool _open;

    public void OpenFor(int pawnId) { _open = true; _lastSig = ""; _root.Visible = true; }
    public void Close() { _open = false; _root.Visible = false; }
    public bool PanelOpen => _root is not null && _root.Visible;

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

        // Header: title + red X close.
        var header = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var title = new Label { Text = "Gear", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 18);
        header.AddChild(title);
        var closeBtn = UiTheme.CloseButton();
        closeBtn.Pressed += Close;
        header.AddChild(closeBtn);
        vbox.AddChild(header);

        // Carry bars.
        vbox.AddChild(BarRow("Weight:", out _weightBar, out _weightVal));
        vbox.AddChild(BarRow("Bulk:", out _bulkBar, out _bulkVal));
        vbox.AddChild(new HSeparator());

        vbox.AddChild(SectionHeader("Equipped"));
        _equippedCol = SectionList(vbox);
        vbox.AddChild(new HSeparator());
        vbox.AddChild(SectionHeader("Inventory"));
        _invCol = SectionList(vbox);
    }

    private static HBoxContainer BarRow(string label, out ProgressBar bar, out Label val)
    {
        bar = new ProgressBar { MinValue = 0, MaxValue = 1, Step = 0.0001, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 22) };
        bar.AddThemeStyleboxOverride("background", UiTheme.InsetBox(UiTheme.Inset, corner: 4));
        bar.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 8);
        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(72, 0), VerticalAlignment = VerticalAlignment.Center };
        lbl.AddThemeFontSizeOverride("font_size", 14);
        val = new Label { Text = "", HorizontalAlignment = HorizontalAlignment.Right, CustomMinimumSize = new Vector2(110, 0), VerticalAlignment = VerticalAlignment.Center };
        val.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(lbl); row.AddChild(bar); row.AddChild(val);
        return row;
    }

    private static Label SectionHeader(string text)
    {
        var h = new Label { Text = text };
        h.AddThemeFontSizeOverride("font_size", 14);
        h.AddThemeColorOverride("font_color", UiTheme.TextDim);
        return h;
    }

    private VBoxContainer SectionList(VBoxContainer parent)
    {
        var scroll = new ScrollContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.ShowAlways,
        };
        var vbar = scroll.GetVScrollBar();
        vbar.CustomMinimumSize = new Vector2(9, 0);
        vbar.AddThemeStyleboxOverride("scroll", UiTheme.InsetBox(UiTheme.Inset, corner: 4));
        vbar.AddThemeStyleboxOverride("grabber", UiTheme.Box(UiTheme.PanelDeep, UiTheme.Border, 1, 4, 0, glow: false));
        vbar.AddThemeStyleboxOverride("grabber_highlight", UiTheme.Box(UiTheme.PanelDeep.Lightened(0.08f), UiTheme.Border, 1, 4, 0, glow: false));
        parent.AddChild(scroll);
        _scrolls.Add(scroll);

        var col = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 3);
        var margin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_right", 22);
        margin.AddChild(col);
        scroll.AddChild(margin);
        return col;
    }

    // Route the scroll wheel into whichever list is hovered (no map zoom).
    public override void _Input(InputEvent @event)
    {
        if (!_root.Visible) return;
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex is not (MouseButton.WheelUp or MouseButton.WheelDown)) return;
        foreach (var scroll in _scrolls)
            if (scroll.GetGlobalRect().HasPoint(mb.Position))
            {
                scroll.GetVScrollBar().Value += mb.ButtonIndex == MouseButton.WheelDown ? 40 : -40;
                GetViewport().SetInputAsHandled();
                return;
            }
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

        UpdateBar(_weightBar, _weightVal, p.CarryWeight, p.MaxCarryWeight);
        UpdateBar(_bulkBar, _bulkVal, p.CarryBulk, p.MaxCarryBulk);

        string sig = GearSig(p);
        if (sig != _lastSig || pawnChanged)
        {
            _lastSig = sig;
            foreach (var c in _equippedCol.GetChildren()) { _equippedCol.RemoveChild(c); c.QueueFree(); }
            foreach (var c in _invCol.GetChildren()) { _invCol.RemoveChild(c); c.QueueFree(); }
            if (p.Equipped.Length == 0) _equippedCol.AddChild(EmptyRow("(nothing equipped)"));
            else foreach (var eq in p.Equipped) _equippedCol.AddChild(EquippedRow(eq));
            if (p.Held.Length == 0) _invCol.AddChild(EmptyRow("(inventory empty)"));
            else foreach (var h in p.Held) _invCol.AddChild(InventoryRow(h));
        }

        Recenter();
    }

    private static string GearSig(in DummyState p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var eq in p.Equipped) sb.Append('E').Append(eq.ItemPath).Append(eq.Count).Append(';');
        foreach (var h in p.Held) sb.Append('I').Append(h.ItemPath).Append(h.Count).Append(';');
        return sb.ToString();
    }

    private void UpdateBar(ProgressBar bar, Label val, float cur, float max)
    {
        float fill = max > 0f ? Mathf.Clamp(cur / max, 0f, 1f) : 0f;
        bar.Value = fill;
        val.Text = $"{cur:0.#} / {max:0}";
        // Light when empty, red when near the cap (heavy = bad).
        StyleFill(bar, BarColor(1f - fill));
    }

    private static Label EmptyRow(string text)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", UiTheme.TextDim);
        return l;
    }

    private static Button ActionBtn(string text)
    {
        var b = new Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(64, 24) };
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }

    private const int IconSize = 16;

    // Fixed-size icon for the left of a gear row: the weapon's art when it has
    // any, else a small neutral chip so names still line up.
    private static Control ItemIcon(string itemPath, bool empty, string? ammo)
    {
        var holder = new CenterContainer { CustomMinimumSize = new Vector2(IconSize, IconSize), MouseFilter = Control.MouseFilterEnum.Ignore };
        if (WeaponIcons.Texture(itemPath, empty, ammo) is { } tex)
        {
            holder.AddChild(new TextureRect
            {
                Texture = tex,
                CustomMinimumSize = new Vector2(IconSize, IconSize),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            });
        }
        else
        {
            var chip = new Panel { CustomMinimumSize = new Vector2(IconSize - 8, IconSize - 8), MouseFilter = Control.MouseFilterEnum.Ignore };
            chip.AddThemeStyleboxOverride("panel", UiTheme.Box(UiTheme.PanelDeep, UiTheme.Border, 1, 4, 0, glow: false));
            holder.AddChild(chip);
        }
        return holder;
    }

    private Control EquippedRow(EquippedSlotState eq)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 6);
        bool ranged = ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var d) && d.IsRangedWeapon;
        row.AddChild(ItemIcon(eq.ItemPath, empty: ranged && eq.MagCount <= 0, ammo: eq.LoadedAmmoPath));
        string name = d is not null ? d.DisplayName : eq.ItemPath;
        var nameLbl = new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        nameLbl.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(nameLbl);
        int idx = eq.Index;
        var unequip = ActionBtn("Unequip");
        unequip.Pressed += () => { if (Host is not null && _shownPawn >= 0) Host.QueueCommand(new ForceUnequipCommand(_shownPawn, idx)); };
        row.AddChild(unequip);
        var drop = ActionBtn("Drop");
        drop.Pressed += () => { if (Host is not null && _shownPawn >= 0) Host.QueueCommand(new DropEquippedCommand(_shownPawn, idx)); };
        row.AddChild(drop);
        return row;
    }

    private Control InventoryRow(HeldStackState h)
    {
        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 6);
        bool equippable = ItemCatalog.ItemsByPath.TryGetValue(h.ItemPath, out var d) && d.Equippable;
        bool ranged = d is not null && d.IsRangedWeapon;
        row.AddChild(ItemIcon(h.ItemPath, empty: ranged && h.MagCount <= 0, ammo: h.LoadedAmmoPath));
        string name = d is not null ? d.DisplayName : h.ItemPath;
        var nameLbl = new Label { Text = h.Count > 1 ? $"{name} x{h.Count}" : name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, VerticalAlignment = VerticalAlignment.Center };
        nameLbl.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(nameLbl);
        int idx = h.Index, count = h.Count;
        if (equippable)
        {
            var equip = ActionBtn("Equip");
            equip.Pressed += () => { if (Host is not null && _shownPawn >= 0) Host.QueueCommand(new EquipFromInventoryCommand(_shownPawn, idx)); };
            row.AddChild(equip);
        }
        if (count > 1)
        {
            var dropX = ActionBtn("Drop X");
            dropX.Pressed += () => DropDialog?.Open(_shownPawn, idx, count, name);
            row.AddChild(dropX);
        }
        var drop = ActionBtn("Drop");
        drop.Pressed += () => { if (Host is not null && _shownPawn >= 0) Host.QueueCommand(new DropHeldItemCommand(_shownPawn, idx)); };
        row.AddChild(drop);
        return row;
    }

    private static void StyleFill(ProgressBar bar, Color c)
    {
        var fill = new StyleBoxFlat { BgColor = c };
        fill.CornerRadiusTopLeft = fill.CornerRadiusTopRight = fill.CornerRadiusBottomLeft = fill.CornerRadiusBottomRight = 4;
        bar.AddThemeStyleboxOverride("fill", fill);
    }

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
        float topLimit = Hud is not null ? Hud.ClockBottom + 10f : 12f;
        float maxToTop = panelTop - GapAbovePanel - topLimit;
        float h = Mathf.Min(PanelHeight, Mathf.Max(220f, maxToTop));
        float y = panelTop - GapAbovePanel - h;
        _root.Size = new Vector2(PanelWidth, h);
        _root.Position = new Vector2(x, y);
    }
}
