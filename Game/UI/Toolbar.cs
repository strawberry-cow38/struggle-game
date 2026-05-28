using Godot;
using StruggleGame.Game.Tools;

namespace StruggleGame.Game.UI;

// Bottom-right tool palette. Each button is a toggle-style ToolMode
// selector — clicking the active tool deselects it. Layout grows
// upward/leftward as more tools land.
public partial class Toolbar : CanvasLayer
{
    public ToolService? Tools { get; set; }
    public WorkTab? WorkTab { get; set; }
    public ScheduleTab? ScheduleTab { get; set; }

    private const int ButtonSize = 56;
    private const int ButtonGap = 6;
    private const int MarginRight = 16;
    private const int MarginBottom = 16;

    private readonly Dictionary<ToolMode, Button> _buttons = new();
    private HBoxContainer _hbox = null!;
    private Button _buildToggle = null!;
    private Button _workToggle = null!;
    private Button _scheduleToggle = null!;

    public override void _Ready()
    {
        Layer = 90;
        if (Tools is null) return;

        _hbox = new HBoxContainer
        {
            Name = "ToolRow",
            MouseFilter = Control.MouseFilterEnum.Pass,
            Visible = false,
        };
        _hbox.AddThemeConstantOverride("separation", ButtonGap);
        AddChild(_hbox);

        _buildToggle = new Button
        {
            Name = "BuildToggle",
            Text = "Build",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(ButtonSize * 1.4f, ButtonSize),
            FocusMode = Control.FocusModeEnum.None,
        };
        _buildToggle.Pressed += () =>
        {
            _hbox.Visible = _buildToggle.ButtonPressed;
            if (!_hbox.Visible && Tools is not null) Tools.Mode = ToolMode.None;
            CallDeferred(nameof(Reposition));
        };
        AddChild(_buildToggle);
        _buildToggle.Resized += Reposition;

        _workToggle = new Button
        {
            Name = "WorkToggle",
            Text = "Work",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(ButtonSize * 1.4f, ButtonSize),
            FocusMode = Control.FocusModeEnum.None,
        };
        _workToggle.Pressed += () =>
        {
            if (WorkTab is null) return;
            if (_workToggle.ButtonPressed) WorkTab.Open();
            else WorkTab.Close();
            CallDeferred(nameof(Reposition));
        };
        AddChild(_workToggle);
        _workToggle.Resized += Reposition;

        _scheduleToggle = new Button
        {
            Name = "ScheduleToggle",
            Text = "Sched",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(ButtonSize * 1.4f, ButtonSize),
            FocusMode = Control.FocusModeEnum.None,
        };
        _scheduleToggle.Pressed += () =>
        {
            if (ScheduleTab is null) return;
            if (_scheduleToggle.ButtonPressed) ScheduleTab.Open();
            else ScheduleTab.Close();
            CallDeferred(nameof(Reposition));
        };
        AddChild(_scheduleToggle);
        _scheduleToggle.Resized += Reposition;

        AddButton(_hbox, ToolMode.BuildWall, "Wall");
        AddButton(_hbox, ToolMode.Door, "Door");
        AddButton(_hbox, ToolMode.Floor, "Floor");
        AddButton(_hbox, ToolMode.Chop, "Chop");
        AddButton(_hbox, ToolMode.CutPlants, "Cut");
        AddButton(_hbox, ToolMode.Harvest, "Harvest");
        AddButton(_hbox, ToolMode.Decon, "Decon");
        AddButton(_hbox, ToolMode.FloorDecon, "FloorDecon");
        AddButton(_hbox, ToolMode.Stockpile, "Stockpile");
        AddButton(_hbox, ToolMode.GrowZone, "Grow");
        AddButton(_hbox, ToolMode.Lamp, "Lamp");
        AddButton(_hbox, ToolMode.Bed, "Bed");
        AddButton(_hbox, ToolMode.Roof, "Roof");
        AddButton(_hbox, ToolMode.RemoveRoof, "UnRoof");
        AddButton(_hbox, ToolMode.NoRoof, "NoRoof");
        AddButton(_hbox, ToolMode.ClearNoRoof, "ClrNoRoof");
        AddButton(_hbox, ToolMode.Cancel, "Cancel");

        // Absolute positioning against the viewport. CanvasLayer parents
        // and anchored HBox sizing had inconsistent first-frame behavior;
        // computing pixels from viewport size is reliable.
        _hbox.Resized += Reposition;
        GetTree().Root.SizeChanged += Reposition;
        CallDeferred(nameof(Reposition));

        Tools.ModeChanged += OnModeChanged;
        OnModeChanged(Tools.Mode);
    }

    private void Reposition()
    {
        if (_hbox is null || _buildToggle is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        _buildToggle.Position = new Vector2(
            vp.X - _buildToggle.Size.X - MarginRight,
            vp.Y - _buildToggle.Size.Y - MarginBottom);
        if (_workToggle is not null)
        {
            _workToggle.Position = new Vector2(
                _buildToggle.Position.X - _workToggle.Size.X - ButtonGap,
                vp.Y - _workToggle.Size.Y - MarginBottom);
        }
        if (_scheduleToggle is not null)
        {
            float anchorX = _workToggle is not null ? _workToggle.Position.X : _buildToggle.Position.X;
            _scheduleToggle.Position = new Vector2(
                anchorX - _scheduleToggle.Size.X - ButtonGap,
                vp.Y - _scheduleToggle.Size.Y - MarginBottom);
        }
        if (_hbox.Visible)
        {
            float rightEdge;
            if (_scheduleToggle is not null) rightEdge = _scheduleToggle.Position.X - ButtonGap;
            else if (_workToggle is not null) rightEdge = _workToggle.Position.X - ButtonGap;
            else rightEdge = _buildToggle.Position.X - ButtonGap;
            _hbox.Position = new Vector2(
                rightEdge - _hbox.Size.X,
                vp.Y - _hbox.Size.Y - MarginBottom);
        }
    }

    public override void _ExitTree()
    {
        if (Tools is not null) Tools.ModeChanged -= OnModeChanged;
        if (IsInsideTree()) GetTree().Root.SizeChanged -= Reposition;
    }

    private void AddButton(HBoxContainer parent, ToolMode mode, string label)
    {
        var btn = new Button
        {
            Text = label,
            ToggleMode = true,
            CustomMinimumSize = new Vector2(ButtonSize * 1.4f, ButtonSize),
            FocusMode = Control.FocusModeEnum.None,
        };
        btn.Pressed += () =>
        {
            if (Tools is null) return;
            // Re-pressing the active tool turns it off.
            Tools.Mode = btn.ButtonPressed ? mode : ToolMode.None;
        };
        parent.AddChild(btn);
        _buttons[mode] = btn;
    }

    private void OnModeChanged(ToolMode mode)
    {
        foreach (var (m, btn) in _buttons)
        {
            btn.SetPressedNoSignal(m == mode);
        }
    }
}
