using Godot;

namespace StruggleGame.Game.UI;

// Escape-menu overlay. Pauses the sim on open + eats all mouse/keyboard
// input behind it (full-screen Control with MouseFilter=Stop and an
// _Input handler that marks every event handled while visible) so the
// player can't designate / drag-rect / hit toolbar shortcuts through
// the menu. Resume button closes + restores the pre-open paused state.
public partial class MainMenuPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private Control _root = null!;
    private Control _options = null!;
    private bool _wasPausedBeforeOpen;

    public override void _Ready()
    {
        Layer = 200;

        _root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);

        var dim = new ColorRect
        {
            Name = "Dim",
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);

        var center = new CenterContainer
        {
            Name = "Center",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(center);

        var panel = new Panel
        {
            CustomMinimumSize = new Vector2(320, 280),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        center.AddChild(panel);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 24, OffsetTop = 24, OffsetRight = -24, OffsetBottom = -24,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        var title = new Label
        {
            Text = "Paused",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 32),
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        var resumeBtn = new Button { Text = "Resume", CustomMinimumSize = new Vector2(0, 36) };
        resumeBtn.Pressed += Close;
        vbox.AddChild(resumeBtn);

        var optionsBtn = new Button { Text = "Options", CustomMinimumSize = new Vector2(0, 36) };
        optionsBtn.Pressed += OpenOptions;
        vbox.AddChild(optionsBtn);

        var quitBtn = new Button { Text = "Quit", CustomMinimumSize = new Vector2(0, 36) };
        quitBtn.Pressed += () => GetTree().Quit();
        vbox.AddChild(quitBtn);

        BuildOptionsPage();
    }

    private void BuildOptionsPage()
    {
        _options = new Control
        {
            Name = "Options",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false,
        };
        _options.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_options);

        var dim = new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _options.AddChild(dim);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _options.AddChild(center);

        var panel = new Panel
        {
            CustomMinimumSize = new Vector2(480, 360),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        center.AddChild(panel);

        var vbox = new VBoxContainer
        {
            AnchorRight = 1, AnchorBottom = 1,
            OffsetLeft = 24, OffsetTop = 24, OffsetRight = -24, OffsetBottom = -24,
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        vbox.AddThemeConstantOverride("separation", 12);
        panel.AddChild(vbox);

        var title = new Label
        {
            Text = "Options",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 32),
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        vbox.AddChild(title);

        vbox.AddChild(new HSeparator());

        var todo = new Label
        {
            Text = "(nothing here yet)",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(todo);

        var backBtn = new Button { Text = "Back", CustomMinimumSize = new Vector2(0, 36) };
        backBtn.Pressed += () => { _options.Visible = false; _root.Visible = true; };
        vbox.AddChild(backBtn);
    }

    public bool IsOpen => _root.Visible || _options.Visible;

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (Host is not null)
        {
            _wasPausedBeforeOpen = Host.IsPaused;
            Host.SetPaused(true);
        }
        _root.Visible = true;
        _options.Visible = false;
    }

    public void Close()
    {
        _root.Visible = false;
        _options.Visible = false;
        if (Host is not null && !_wasPausedBeforeOpen) Host.SetPaused(false);
    }

    private void OpenOptions()
    {
        _root.Visible = false;
        _options.Visible = true;
    }

    public override void _Input(InputEvent @event)
    {
        if (!IsOpen) return;
        // ESC inside the menu = close (or pop options back to root).
        if (@event is InputEventKey ek && ek.Pressed && !ek.Echo && ek.Keycode == Key.Escape)
        {
            if (_options.Visible) { _options.Visible = false; _root.Visible = true; }
            else Close();
            GetViewport().SetInputAsHandled();
            return;
        }
        // While the menu is open, eat every other key + mouse event so
        // player hotkeys (R/F/B/C/X/space/etc.) and designator drags
        // can't fire behind the overlay.
        if (@event is InputEventKey or InputEventMouseButton or InputEventMouseMotion)
        {
            GetViewport().SetInputAsHandled();
        }
    }
}
