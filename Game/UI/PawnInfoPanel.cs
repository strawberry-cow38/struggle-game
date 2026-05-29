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

    private const int PanelWidth = 320;
    private const int MarginRight = 16;
    private const int MarginTop = 16;

    private Panel _root = null!;
    private Label _nameLabel = null!;
    private Label _stateLabel = null!;
    private Label _bioLabel = null!;
    private Label _capLabel = null!;
    private Label _sleepLabel = null!;
    private ProgressBar _sleepBar = null!;
    private Label _recLabel = null!;
    private ProgressBar _recBar = null!;
    private VBoxContainer _invList = null!;
    private Label _invEmptyLabel = null!;

    private int _shownPawnId = -1;
    private long _lastSnapshotTick = -1;

    public override void _Ready()
    {
        Layer = 95;

        _root = new Panel
        {
            Name = "Root",
            CustomMinimumSize = new Vector2(PanelWidth, 240),
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        AddChild(_root);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 10, OffsetTop = 10, OffsetRight = -10, OffsetBottom = -10,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 6);
        _root.AddChild(vbox);

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

        _bioLabel = new Label
        {
            Text = "(no bio yet)",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _bioLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_bioLabel);

        vbox.AddChild(new HSeparator());

        var needsHeader = new Label { Text = "Needs" };
        needsHeader.AddThemeFontSizeOverride("font_size", 14);
        vbox.AddChild(needsHeader);

        _sleepLabel = new Label { Text = "Sleep" };
        _sleepLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_sleepLabel);
        _sleepBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.0001,
            CustomMinimumSize = new Vector2(0, 18),
        };
        vbox.AddChild(_sleepBar);

        _recLabel = new Label { Text = "Recreation" };
        _recLabel.AddThemeFontSizeOverride("font_size", 11);
        vbox.AddChild(_recLabel);
        _recBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.0001,
            CustomMinimumSize = new Vector2(0, 18),
        };
        vbox.AddChild(_recBar);

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
        if (sel.Value != _shownPawnId || snap.Tick != _lastSnapshotTick)
        {
            Render(snap, sel.Value);
            _shownPawnId = sel.Value;
            _lastSnapshotTick = snap.Tick;
        }
    }

    private void Reposition()
    {
        var vp = GetViewport().GetVisibleRect().Size;
        _root.Position = new Vector2(vp.X - PanelWidth - MarginRight, MarginTop);
        _root.Size = new Vector2(PanelWidth, _root.Size.Y);
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
        _bioLabel.Text = "Stub bio. Name, traits, mood, skills go here later.";

        _sleepBar.Value = p.SleepLevel;
        _sleepLabel.Text = $"Sleep: {p.SleepLevel * 100f:0}%" + (p.Sleeping ? "  (asleep)" : "");

        _recBar.Value = p.RecreationLevel;
        string recTag = p.AtRecreationKind is RecreationKind k ? $"  ({k})" : "";
        _recLabel.Text = $"Recreation: {p.RecreationLevel * 100f:0}%{recTag}";

        _capLabel.Text = $"Carry: {p.CarryWeight:0.#} / {p.MaxCarryWeight:0.#} wt    {p.CarryBulk:0.#} / {p.MaxCarryBulk:0.#} bulk";

        foreach (var child in _invList.GetChildren()) child.QueueFree();
        if (p.Inventory.Length == 0)
        {
            _invEmptyLabel.Visible = true;
            return;
        }
        _invEmptyLabel.Visible = false;

        foreach (var slot in p.Inventory)
        {
            BuildSlotRow(p.EntityId, slot);
        }
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
