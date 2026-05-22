using Godot;
using StruggleGame.Game.Tools;

namespace StruggleGame.Game.UI;

// Bottom-right tool palette. Each button is a toggle-style ToolMode
// selector — clicking the active tool deselects it. Layout grows
// upward/leftward as more tools land.
public partial class Toolbar : CanvasLayer
{
    public ToolService? Tools { get; set; }

    private const int ButtonSize = 56;
    private const int ButtonGap = 6;
    private const int MarginRight = 16;
    private const int MarginBottom = 16;

    private readonly Dictionary<ToolMode, Button> _buttons = new();
    private HBoxContainer _hbox = null!;

    public override void _Ready()
    {
        Layer = 90;
        if (Tools is null) return;

        _hbox = new HBoxContainer
        {
            Name = "ToolRow",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        _hbox.AddThemeConstantOverride("separation", ButtonGap);
        AddChild(_hbox);

        AddButton(_hbox, ToolMode.BuildWall, "Wall");
        AddButton(_hbox, ToolMode.Door, "Door");
        AddButton(_hbox, ToolMode.Floor, "Floor");
        AddButton(_hbox, ToolMode.Chop, "Chop");
        AddButton(_hbox, ToolMode.CutPlants, "Cut");
        AddButton(_hbox, ToolMode.Harvest, "Harvest");
        AddButton(_hbox, ToolMode.Decon, "Decon");
        AddButton(_hbox, ToolMode.FloorDecon, "FloorDecon");
        AddButton(_hbox, ToolMode.Stockpile, "Stockpile");
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
        if (_hbox is null) return;
        var vp = GetViewport().GetVisibleRect().Size;
        _hbox.Position = new Vector2(
            vp.X - _hbox.Size.X - MarginRight,
            vp.Y - _hbox.Size.Y - MarginBottom);
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
