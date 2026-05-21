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

    public override void _Ready()
    {
        Layer = 90;
        if (Tools is null) return;

        var hbox = new HBoxContainer
        {
            Name = "ToolRow",
            AnchorLeft = 1, AnchorTop = 1, AnchorRight = 1, AnchorBottom = 1,
            GrowHorizontal = Control.GrowDirection.Begin,
            GrowVertical = Control.GrowDirection.Begin,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        hbox.AddThemeConstantOverride("separation", ButtonGap);
        AddChild(hbox);

        AddButton(hbox, ToolMode.BuildWall, "Wall");
        AddButton(hbox, ToolMode.Cancel, "Cancel");

        // Children aren't sized until the first layout pass — reposition
        // every time the row resorts (initial sort + future button adds).
        hbox.Resized += () =>
        {
            hbox.Position = new Vector2(
                -hbox.Size.X - MarginRight,
                -hbox.Size.Y - MarginBottom);
        };

        Tools.ModeChanged += OnModeChanged;
        OnModeChanged(Tools.Mode);
    }

    public override void _ExitTree()
    {
        if (Tools is not null) Tools.ModeChanged -= OnModeChanged;
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
