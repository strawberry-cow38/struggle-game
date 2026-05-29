using Godot;
using StruggleGame.Game.Debug;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;

namespace StruggleGame.Game.UI;

// Top-center debug bar. Currently hosts the Actions menu: Spawn Pawn
// and Remove Pawn buttons that toggle the corresponding ToolMode. Same
// click-to-activate / click-again-to-deactivate pattern as Toolbar.
public partial class DebugBar : CanvasLayer
{
    public ToolService? Tools { get; set; }
    public SimHost? Host { get; set; }

    private const int ButtonHeight = 36;
    private const int ButtonGap = 6;
    private const int MarginTop = 8;

    private readonly Dictionary<ToolMode, Button> _buttons = new();
    private HBoxContainer _hbox = null!;

    public override void _Ready()
    {
        Layer = 95;
        if (Tools is null) return;

        _hbox = new HBoxContainer
        {
            Name = "DebugRow",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _hbox.AddThemeConstantOverride("separation", ButtonGap);
        AddChild(_hbox);

        AddLabel(_hbox, "Actions");
        AddButton(_hbox, ToolMode.SpawnPawn, "Spawn Pawn");
        AddButton(_hbox, ToolMode.RemovePawn, "Remove Pawn");
        AddSpawnItemControls(_hbox);
        AddOneShotButton(_hbox, "Reroll Map", () => Host?.Reroll(System.Environment.TickCount));
        AddOneShotButton(_hbox, "-1 hr", () => Host?.QueueCommand(new AdvanceWorldTimeCommand(-3600)));
        AddOneShotButton(_hbox, "+1 hr", () => Host?.QueueCommand(new AdvanceWorldTimeCommand(3600)));
        AddGodModeButton(_hbox);

        _hbox.Resized += Reposition;
        GetTree().Root.SizeChanged += Reposition;
        CallDeferred(nameof(Reposition));

        Tools.ModeChanged += OnModeChanged;
        OnModeChanged(Tools.Mode);
    }

    private void Reposition()
    {
        if (_hbox is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        _hbox.Position = new Vector2((vp.X - _hbox.Size.X) * 0.5f, MarginTop);
    }

    public override void _ExitTree()
    {
        if (Tools is not null) Tools.ModeChanged -= OnModeChanged;
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    private static void AddLabel(HBoxContainer parent, string text)
    {
        var lbl = new Label
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        lbl.AddThemeConstantOverride("outline_size", 4);
        parent.AddChild(lbl);
    }

    private void AddButton(HBoxContainer parent, ToolMode mode, string label)
    {
        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        btn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = btn.ButtonPressed ? mode : ToolMode.None;
        };
        parent.AddChild(btn);
        _buttons[mode] = btn;
    }

    // Toggle for SimRuntime.GodModeFreeBuild. Local _godMode mirrors the
    // sim flag — we initialise it to true (matches SimRuntime default) and
    // flip on every press. Label reflects current state.
    private bool _godMode = true;
    private void AddGodModeButton(HBoxContainer parent)
    {
        var btn = new Button
        {
            Text = GodModeLabel(_godMode),
            ToggleMode = true,
            ButtonPressed = _godMode,
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        btn.Pressed += () =>
        {
            _godMode = btn.ButtonPressed;
            btn.Text = GodModeLabel(_godMode);
            Host?.QueueCommand(new SetGodModeFreeBuildCommand(_godMode));
        };
        parent.AddChild(btn);
    }

    private static string GodModeLabel(bool on) => on ? "God Mode: ON" : "God Mode: OFF";

    // Non-toggle button — fires its action once on click and doesn't sit
    // in the _buttons map (no ToolMode to track).
    private static void AddOneShotButton(HBoxContainer parent, string label, Action onPress)
    {
        var btn = new Button
        {
            Text = label,
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        btn.Pressed += () => onPress();
        parent.AddChild(btn);
    }

    private void OnModeChanged(ToolMode mode)
    {
        foreach (var (m, btn) in _buttons)
        {
            btn.SetPressedNoSignal(m == mode);
        }
        if (_spawnItemBtn is not null) _spawnItemBtn.SetPressedNoSignal(mode == ToolMode.DebugSpawnItem);
    }

    // Picker for the DebugSpawnItem tool. A toggle button shows the
    // current item label ("Spawn: Carrot x1"); pressing it pops up a
    // PopupMenu enumerating every registered ItemDef. Selecting an
    // item sets DebugSpawnItemDesignator.Current + flips the tool on.
    // Closing the popup without a choice toggles the tool off.
    private Button? _spawnItemBtn;
    private PopupMenu? _spawnItemMenu;
    private SpinBox? _spawnItemCount;
    private readonly List<ItemDef> _spawnItemItems = new();

    private void AddSpawnItemControls(HBoxContainer parent)
    {
        _spawnItemBtn = new Button
        {
            Text = SpawnItemLabel(),
            ToggleMode = true,
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _spawnItemBtn.Pressed += OnSpawnItemPressed;
        parent.AddChild(_spawnItemBtn);

        _spawnItemCount = new SpinBox
        {
            MinValue = 1,
            MaxValue = 999,
            Value = DebugSpawnItemDesignator.Count <= 0 ? 1 : DebugSpawnItemDesignator.Count,
            Step = 1,
            CustomMinimumSize = new Vector2(72, ButtonHeight),
        };
        _spawnItemCount.ValueChanged += v =>
        {
            DebugSpawnItemDesignator.Count = (int)v;
            if (_spawnItemBtn is not null) _spawnItemBtn.Text = SpawnItemLabel();
        };
        parent.AddChild(_spawnItemCount);

        _spawnItemMenu = new PopupMenu();
        _spawnItemMenu.IdPressed += OnSpawnItemMenuPressed;
        _spawnItemBtn.AddChild(_spawnItemMenu);
        RebuildSpawnItemMenu();
    }

    private void RebuildSpawnItemMenu()
    {
        if (_spawnItemMenu is null) return;
        _spawnItemMenu.Clear();
        _spawnItemItems.Clear();
        int id = 0;
        foreach (var (path, item) in ItemCatalog.ItemsByPath)
        {
            _spawnItemMenu.AddItem(item.DisplayName + "  (" + path + ")", id);
            _spawnItemItems.Add(item);
            id++;
        }
    }

    private void OnSpawnItemPressed()
    {
        if (Tools is null) return;
        if (_spawnItemBtn is null || _spawnItemMenu is null) return;
        if (Tools.Mode == ToolMode.DebugSpawnItem)
        {
            Tools.Mode = ToolMode.None;
            return;
        }
        // Pop up the menu under the button. Selection in
        // OnSpawnItemMenuPressed flips the tool on; closing without a
        // pick leaves us in ToolMode.None.
        var btnRect = _spawnItemBtn.GetGlobalRect();
        _spawnItemMenu.Position = new Vector2I(
            (int)btnRect.Position.X,
            (int)(btnRect.Position.Y + btnRect.Size.Y));
        _spawnItemMenu.Popup();
        // The button is in toggle mode and just got "pressed" on. Without
        // a selection we want it back off; the menu's IdPressed (success)
        // or close (cancel) handlers settle the final state.
        _spawnItemBtn.SetPressedNoSignal(false);
    }

    private void OnSpawnItemMenuPressed(long id)
    {
        int idx = (int)id;
        if (idx < 0 || idx >= _spawnItemItems.Count) return;
        DebugSpawnItemDesignator.Current = _spawnItemItems[idx];
        if (_spawnItemBtn is not null) _spawnItemBtn.Text = SpawnItemLabel();
        if (Tools is not null) Tools.Mode = ToolMode.DebugSpawnItem;
    }

    private static string SpawnItemLabel()
    {
        var item = DebugSpawnItemDesignator.Current;
        int count = DebugSpawnItemDesignator.Count <= 0 ? 1 : DebugSpawnItemDesignator.Count;
        return item is null ? "Spawn Item..." : $"Spawn: {item.DisplayName} x{count}";
    }
}
