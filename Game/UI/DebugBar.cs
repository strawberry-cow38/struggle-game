using Godot;
using StruggleGame.Game.Tools;

namespace StruggleGame.Game.UI;

// Top-center debug bar. Currently hosts the Actions menu: Spawn Pawn
// and Remove Pawn buttons that toggle the corresponding ToolMode. Same
// click-to-activate / click-again-to-deactivate pattern as Toolbar.
public partial class DebugBar : CanvasLayer
{
    public ToolService? Tools { get; set; }

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

    private void OnModeChanged(ToolMode mode)
    {
        foreach (var (m, btn) in _buttons)
        {
            btn.SetPressedNoSignal(m == mode);
        }
    }
}
