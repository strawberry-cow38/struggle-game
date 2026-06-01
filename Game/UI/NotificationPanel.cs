using System.Collections.Generic;
using Godot;
using StruggleGame.Sim.Commands;

namespace StruggleGame.Game.UI;

// Shows player notifications (raid alerts, etc) one at a time as a centered
// modal that PAUSES the sim while up. The notification list rides the sim
// snapshot; this panel polls it, displays the first un-acknowledged one, and
// on Dismiss unpauses + tells the sim to clear it.
//
// Pause ordering matters: the sim is frozen while the modal is up, so on
// dismiss we unpause FIRST (so the loop resumes + can apply the dismiss
// command) then queue the clear. Acknowledged ids are tracked locally so a
// still-present notification in the next snapshot can't re-pause us.
public partial class NotificationPanel : CanvasLayer
{
    public SimHost? Host { get; set; }

    private Control _root = null!;
    private Label _title = null!;
    private Label _message = null!;
    private readonly HashSet<int> _acked = new();
    private int _currentId;          // 0 = nothing showing
    private bool _wasPausedBeforeShow;

    public override void _Ready()
    {
        Layer = 190; // below the escape menu (200), above the game

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
            Color = new Color(0f, 0f, 0f, 0.5f),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(dim);

        var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(center);

        var panel = new Panel
        {
            CustomMinimumSize = new Vector2(420, 200),
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

        _title = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            CustomMinimumSize = new Vector2(0, 32),
        };
        _title.AddThemeFontSizeOverride("font_size", 26);
        _title.AddThemeColorOverride("font_color", new Color(1f, 0.5f, 0.4f));
        vbox.AddChild(_title);

        vbox.AddChild(new HSeparator());

        _message = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        vbox.AddChild(_message);

        var dismissBtn = new Button { Text = "Dismiss", CustomMinimumSize = new Vector2(0, 38) };
        dismissBtn.Pressed += Dismiss;
        vbox.AddChild(dismissBtn);
    }

    public override void _Process(double delta)
    {
        if (_currentId != 0) return; // already showing one; wait for dismiss
        var snap = Host?.LatestSnapshot;
        if (snap is null) return;

        foreach (var note in snap.Notifications)
        {
            if (_acked.Contains(note.Id)) continue;
            Show(note.Id, note.Title, note.Message);
            return;
        }
    }

    private void Show(int id, string title, string message)
    {
        _currentId = id;
        _title.Text = title;
        _message.Text = message;
        _root.Visible = true;
        if (Host is not null)
        {
            _wasPausedBeforeShow = Host.IsPaused;
            Host.SetPaused(true);
        }
    }

    private void Dismiss()
    {
        if (_currentId == 0) return;
        int id = _currentId;
        _acked.Add(id);
        _root.Visible = false;
        // Unpause first so the resumed loop can apply the clear command (the
        // sim is frozen while paused and won't drain commands otherwise).
        if (Host is not null && !_wasPausedBeforeShow) Host.SetPaused(false);
        Host?.QueueCommand(new DismissNotificationCommand(id));
        _currentId = 0;
    }

    public override void _Input(InputEvent @event)
    {
        if (_currentId == 0) return;
        // Enter / Esc / Space dismiss; eat keys so game hotkeys don't fire.
        if (@event is InputEventKey ek && ek.Pressed && !ek.Echo)
        {
            if (ek.Keycode is Key.Enter or Key.KpEnter or Key.Escape or Key.Space)
                Dismiss();
            GetViewport().SetInputAsHandled();
        }
    }
}
