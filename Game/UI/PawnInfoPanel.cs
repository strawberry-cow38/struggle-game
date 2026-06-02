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

    private const int PanelWidth = 740;
    private const int MarginLeft = 16;
    private const int MarginBottom = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _stateLabel = null!;
    private Label _bioLabel = null!;
    private Label _capLabel = null!;
    private Label _sleepLabel = null!;
    private ProgressBar _sleepBar = null!;
    private Label _sleepPct = null!;
    private Label _recLabel = null!;
    private ProgressBar _recBar = null!;
    private Label _recPct = null!;
    private VBoxContainer _invList = null!;
    private Label _invEmptyLabel = null!;
    private VBoxContainer _equipList = null!;
    private Label _equipEmptyLabel = null!;
    private Label _bloodLabel = null!;
    private Label _capLabel2 = null!;
    private VBoxContainer _injuryList = null!;
    private Label _injuryEmptyLabel = null!;
    private string _lastInjurySig = "";

    private int _shownPawnId = -1;
    private long _lastSnapshotTick = -1;
    // Signature of the inventory/equipped/carrying rows last built. Rows
    // hold clickable buttons; rebuilding them every tick (Render runs per
    // snapshot tick) would QueueFree a button mid-click and swallow the
    // press. So rebuild rows only when this signature changes; the live
    // labels/bars still refresh every tick.
    private string _lastRowSig = "";

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 360),
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

        // Dumb tab strip (inert — styling only for now).
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

        var headerRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _nameLabel = new Label { Text = "Colonist", CustomMinimumSize = new Vector2(0, 24) };
        _nameLabel.AddThemeFontSizeOverride("font_size", 18);
        _nameLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        headerRow.AddChild(_nameLabel);
        var closeBtn = new Button { Text = "X", CustomMinimumSize = new Vector2(28, 24) };
        closeBtn.Pressed += () => { if (Host is not null) Host.SelectedDummyId = null; };
        headerRow.AddChild(closeBtn);
        vbox.AddChild(headerRow);

        vbox.AddChild(new HSeparator());

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

        vbox.AddChild(new HSeparator());

        // Two columns: needs bars (left) | bio stub (right), vertical divider.
        var midRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        midRow.AddThemeConstantOverride("separation", 12);

        var needsCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 2.2f };
        needsCol.AddThemeConstantOverride("separation", 4);
        var needsHeader = new Label { Text = "Needs" };
        needsHeader.AddThemeFontSizeOverride("font_size", 14);
        needsCol.AddChild(needsHeader);
        needsCol.AddChild(BuildNeedRow("Sleep", new Color(0.45f, 0.78f, 0.38f), out _sleepLabel, out _sleepBar, out _sleepPct));
        needsCol.AddChild(BuildNeedRow("Recreation", new Color(0.72f, 0.62f, 0.22f), out _recLabel, out _recBar, out _recPct));
        midRow.AddChild(needsCol);

        midRow.AddChild(new VSeparator());

        var bioCol = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1f };
        bioCol.AddThemeConstantOverride("separation", 4);
        var bioHeader = new Label { Text = "Bio" };
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

        var healthHeader = new Label { Text = "Health" };
        healthHeader.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(healthHeader);

        _bloodLabel = new Label { Text = "" };
        _bloodLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_bloodLabel);

        _capLabel2 = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _capLabel2.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_capLabel2);

        _injuryList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _injuryList.AddThemeConstantOverride("separation", 2);
        vbox.AddChild(_injuryList);

        _injuryEmptyLabel = new Label { Text = "(no injuries)" };
        _injuryEmptyLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_injuryEmptyLabel);

        vbox.AddChild(new HSeparator());

        var equipHeader = new Label { Text = "Equipped" };
        equipHeader.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(equipHeader);

        _equipList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _equipList.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(_equipList);

        _equipEmptyLabel = new Label { Text = "(nothing equipped)" };
        _equipEmptyLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_equipEmptyLabel);

        vbox.AddChild(new HSeparator());

        var invHeader = new Label { Text = "Inventory" };
        invHeader.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(invHeader);

        _capLabel = new Label { Text = "" };
        _capLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_capLabel);

        _invList = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        _invList.AddThemeConstantOverride("separation", 4);
        vbox.AddChild(_invList);

        _invEmptyLabel = new Label { Text = "(empty)" };
        _invEmptyLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_invEmptyLabel);

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
        string draftTag = p.Drafted ? "  [DRAFTED]" : "";
        string sleepTag = p.Sleeping ? "  [SLEEPING]" : "";
        _stateLabel.Text = $"State: {p.Job}{draftTag}{sleepTag}";

        _sleepBar.Value = p.SleepLevel;
        _sleepLabel.Text = p.Sleeping ? "Sleep (zzz)" : "Sleep";
        _sleepPct.Text = $"{p.SleepLevel * 100f:0}%";

        _recBar.Value = p.RecreationLevel;
        _recLabel.Text = p.AtRecreationKind is RecreationKind k ? $"Rec ({k})" : "Recreation";
        _recPct.Text = $"{p.RecreationLevel * 100f:0}%";

        _capLabel.Text = $"Carry: {p.CarryWeight:0.#} / {p.MaxCarryWeight:0.#} wt    {p.CarryBulk:0.#} / {p.MaxCarryBulk:0.#} bulk";

        // Health — live labels every tick; the injury list rebuilds only
        // when its content changes (gated separately from the row sig so
        // the equip/inventory early-return below doesn't skip it).
        var hs = p.Health;
        string downed = hs.Unconscious ? "  [UNCONSCIOUS]" : "";
        string bleeding = "";
        if (hs.BleedRate > 0f)
        {
            // Death ≈ blood reaching 0. Game-hours is fixed by the sim; real
            // time scales with the current speed (more ticks/sec = sooner).
            double gameHours = hs.BloodLevel * StruggleGame.Sim.SimRuntime.SimSecondsPerRealSecond
                / (hs.BleedRate * 3600.0);
            double realSec = hs.BloodLevel
                / (hs.BleedRate * StruggleGame.Sim.SimConstants.TickSeconds * Math.Max(1, Host!.TickHz));
            bleeding = $"    bleeding {hs.BleedRate * 100f:0.0}%/s — bleed-out ~{gameHours:0.0}h game / {FormatDuration(realSec)} real";
        }
        _bloodLabel.Text = $"Blood: {hs.BloodLevel * 100f:0}%{bleeding}    Consciousness: {hs.Consciousness * 100f:0}%{downed}";
        _capLabel2.Text = $"Move {hs.Moving * 100f:0}%  ·  Manip {hs.Manipulation * 100f:0}%  ·  Sight {hs.Sight * 100f:0}%  ·  Pain {hs.Pain * 100f:0}%";
        string injSig = BuildInjurySignature(hs.Injuries);
        if (injSig != _lastInjurySig || pawnId != _shownPawnId)
        {
            _lastInjurySig = injSig;
            foreach (var child in _injuryList.GetChildren()) child.QueueFree();
            _injuryEmptyLabel.Visible = hs.Injuries.Length == 0;
            foreach (var g in GroupInjuries(hs.Injuries)) BuildInjuryRow(g);
        }

        // Only rebuild the clickable rows when their contents actually
        // change — see _lastRowSig. Pawn switch forces a rebuild.
        string sig = BuildRowSignature(p);
        if (sig == _lastRowSig && pawnId == _shownPawnId) return;
        _lastRowSig = sig;

        // Equipped section.
        foreach (var child in _equipList.GetChildren()) child.QueueFree();
        _equipEmptyLabel.Visible = p.Equipped.Length == 0;
        foreach (var eq in p.Equipped)
        {
            BuildEquippedRow(p.EntityId, eq);
        }

        // Inventory section: general held stacks + in-transit haul cargo.
        foreach (var child in _invList.GetChildren()) child.QueueFree();
        _invEmptyLabel.Visible = p.Held.Length == 0 && p.Inventory.Length == 0;
        foreach (var stack in p.Held)
        {
            BuildHeldRow(p.EntityId, stack);
        }
        foreach (var slot in p.Inventory)
        {
            BuildSlotRow(p.EntityId, slot);
        }
    }

    // One panel row = all conditions of the same kind on the same part,
    // collapsed with a count + the worst severity.
    private readonly record struct InjuryGroup(
        string PartId, StruggleGame.Sim.Bodies.ConditionKind Kind, int Count, float MaxSeverity,
        string? Caliber, bool Lodged);

    // Reused scratch — Render calls these every tick while a pawn is
    // selected, so fresh Dictionary/List/StringBuilder allocations each tick
    // were steady garbage. GroupInjuries is called then fully consumed before
    // the next call, so a single reused result list is safe.
    private readonly Dictionary<(string, StruggleGame.Sim.Bodies.ConditionKind, string?, bool), (int n, float maxSev)> _injMap = new();
    private readonly List<(string, StruggleGame.Sim.Bodies.ConditionKind, string?, bool)> _injOrder = new();
    private readonly List<InjuryGroup> _injGroups = new();
    private readonly System.Text.StringBuilder _injSigSb = new();

    private List<InjuryGroup> GroupInjuries(InjuryState[] injuries)
    {
        var map = _injMap; map.Clear();
        var order = _injOrder; order.Clear();
        foreach (var inj in injuries)
        {
            var key = (inj.PartId, inj.Kind, inj.Caliber, inj.Lodged);
            if (map.TryGetValue(key, out var cur))
                map[key] = (cur.n + 1, System.Math.Max(cur.maxSev, inj.Severity));
            else { map[key] = (1, inj.Severity); order.Add(key); }
        }
        var list = _injGroups; list.Clear();
        foreach (var key in order) { var v = map[key]; list.Add(new InjuryGroup(key.Item1, key.Item2, v.n, v.maxSev, key.Item3, key.Item4)); }
        return list;
    }

    // Compact real-time: "1m05s" / "42s".
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

    private string BuildInjurySignature(InjuryState[] injuries)
    {
        var sb = _injSigSb; sb.Clear();
        foreach (var g in GroupInjuries(injuries))
            sb.Append(g.PartId).Append((int)g.Kind).Append('x').Append(g.Count).Append((int)(g.MaxSeverity * 100))
              .Append(g.Caliber).Append(g.Lodged ? 'L' : 'T').Append(';');
        return sb.ToString();
    }

    private void BuildInjuryRow(InjuryGroup g)
    {
        string part = StruggleGame.Sim.Bodies.BodyTree.TryGet(g.PartId, out var def)
            ? def.DisplayName : g.PartId;
        string kind = g.Kind.ToString();
        string detail = g.Kind switch
        {
            StruggleGame.Sim.Bodies.ConditionKind.Missing => "missing",
            StruggleGame.Sim.Bodies.ConditionKind.Scar => "scar",
            // Gunshots show caliber + whether the round lodged or passed through.
            StruggleGame.Sim.Bodies.ConditionKind.Gunshot when g.Caliber is not null =>
                $"gunshot {g.MaxSeverity:0} dmg — {g.Caliber}, {(g.Lodged ? "lodged" : "through & through")}",
            _ => $"{kind.ToLower()} {g.MaxSeverity:0} dmg",
        };
        string countTag = g.Count > 1 ? $" x{g.Count}" : "";
        var line = new Label { Text = $"{part}: {detail}{countTag}" };
        line.AddThemeFontSizeOverride("font_size", 11);
        // Tint by how nasty it is (damage in hit points).
        Color c = g.Kind == StruggleGame.Sim.Bodies.ConditionKind.Missing
            ? new Color(1f, 0.4f, 0.4f)
            : g.MaxSeverity >= 12f ? new Color(1f, 0.6f, 0.3f) : new Color(0.9f, 0.85f, 0.6f);
        line.AddThemeColorOverride("font_color", c);
        _injuryList.AddChild(line);
    }

    private static string BuildRowSignature(DummyState p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var eq in p.Equipped) sb.Append('E').Append(eq.Index).Append(eq.ItemPath).Append(eq.Count).Append((int)eq.Slot).Append(';');
        foreach (var h in p.Held) sb.Append('H').Append(h.Index).Append(h.ItemPath).Append(h.Count).Append(';');
        foreach (var s in p.Inventory) sb.Append('C').Append(s.SlotEntityId).Append(s.ItemPath).Append(s.Count).Append(s.Forbidden ? '1' : '0').Append(';');
        return sb.ToString();
    }

    private void BuildEquippedRow(int pawnId, EquippedSlotState eq)
    {
        string itemName = ItemCatalog.ItemsByPath.TryGetValue(eq.ItemPath, out var def)
            ? def.DisplayName : eq.ItemPath;
        float w = def?.Weight ?? 0f;
        float b = def?.Bulk ?? 0f;

        var row = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 2);

        var line = new Label
        {
            Text = $"[{eq.Slot}] {itemName} x{eq.Count}    {w * eq.Count:0.#} wt  {b * eq.Count:0.#} bulk",
        };
        line.AddThemeColorOverride("font_color", new Color(0.7f, 0.9f, 1.0f));
        row.AddChild(line);

        var btns = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btns.AddThemeConstantOverride("separation", 4);

        int index = eq.Index;
        var unequipBtn = new Button
        {
            Text = "Unequip",
            CustomMinimumSize = new Vector2(0, 24),
            FocusMode = Control.FocusModeEnum.None,
        };
        unequipBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new ForceUnequipCommand(pawnId, index));
        };
        btns.AddChild(unequipBtn);

        var dropBtn = new Button
        {
            Text = "Drop",
            CustomMinimumSize = new Vector2(0, 24),
            FocusMode = Control.FocusModeEnum.None,
        };
        dropBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new DropEquippedCommand(pawnId, index));
        };
        btns.AddChild(dropBtn);

        row.AddChild(btns);
        _equipList.AddChild(row);
    }

    private void BuildHeldRow(int pawnId, HeldStackState stack)
    {
        string itemName = ItemCatalog.ItemsByPath.TryGetValue(stack.ItemPath, out var def)
            ? def.DisplayName : stack.ItemPath;
        float w = def?.Weight ?? 0f;
        float b = def?.Bulk ?? 0f;

        var row = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 2);

        var line = new Label
        {
            Text = $"{itemName} x{stack.Count}    {w * stack.Count:0.#} wt  {b * stack.Count:0.#} bulk",
        };
        row.AddChild(line);

        int index = stack.Index;
        var btns = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btns.AddThemeConstantOverride("separation", 4);

        // Equippable items in the bag get a direct Equip button (no walking).
        if (def is not null && def.Equippable)
        {
            var equipBtn = new Button
            {
                Text = "Equip",
                CustomMinimumSize = new Vector2(0, 24),
                FocusMode = Control.FocusModeEnum.None,
            };
            equipBtn.Pressed += () =>
            {
                if (Host is null) return;
                Host.QueueCommand(new EquipFromInventoryCommand(pawnId, index));
            };
            btns.AddChild(equipBtn);
        }

        var dropBtn = new Button
        {
            Text = "Force Drop",
            CustomMinimumSize = new Vector2(0, 24),
            FocusMode = Control.FocusModeEnum.None,
        };
        dropBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new DropHeldItemCommand(pawnId, index));
        };
        btns.AddChild(dropBtn);
        row.AddChild(btns);
        _invList.AddChild(row);
    }

    private void BuildSlotRow(int pawnId, CarriedItemState slot)
    {
        string itemName = ItemCatalog.ItemsByPath.TryGetValue(slot.ItemPath, out var def)
            ? def.DisplayName : slot.ItemPath;
        float w = def?.Weight ?? 0f;
        float b = def?.Bulk ?? 0f;

        var row = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", 2);

        var line = new Label
        {
            Text = $"{itemName} x{slot.Count}    {w * slot.Count:0.#} wt  {b * slot.Count:0.#} bulk",
        };
        if (slot.Forbidden)
        {
            line.AddThemeColorOverride("font_color", new Color(1.0f, 0.55f, 0.55f));
            line.Text += "    [FORBIDDEN]";
        }
        row.AddChild(line);

        var btns = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        btns.AddThemeConstantOverride("separation", 4);

        var forbidBtn = new Button
        {
            Text = slot.Forbidden ? "Unforbid" : "Forbid",
            CustomMinimumSize = new Vector2(0, 24),
            FocusMode = Control.FocusModeEnum.None,
        };
        int slotId = slot.SlotEntityId;
        bool nowForbidden = slot.Forbidden;
        forbidBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new SetInventorySlotForbiddenCommand(pawnId, slotId, !nowForbidden));
        };
        btns.AddChild(forbidBtn);

        var dropBtn = new Button
        {
            Text = "Force Drop",
            CustomMinimumSize = new Vector2(0, 24),
            FocusMode = Control.FocusModeEnum.None,
        };
        dropBtn.Pressed += () =>
        {
            if (Host is null) return;
            Host.QueueCommand(new ForceDropInventorySlotCommand(pawnId, slotId));
        };
        btns.AddChild(dropBtn);

        row.AddChild(btns);
        _invList.AddChild(row);
    }
}
