using Godot;

namespace StruggleGame.Game.Audio;

// Plays a click whenever the player presses any UI button. One global hook —
// the control hovered at mouse-down — covers every Button without wiring each
// one. Never consumes the event, so gameplay input (selection, double-click,
// camera) is untouched. Stream loaded at runtime (no .import step).
public partial class UiSfx : Node
{
    private const string Dir = "res://Game/Assets/Audio/";

    private AudioStreamPlayer _button = null!;

    public override void _Ready()
    {
        _button = new AudioStreamPlayer { Name = "ButtonPlayer", Bus = "Master" };
        AddChild(_button);
        var s = AudioStreamOggVorbis.LoadFromFile(ProjectSettings.GlobalizePath(Dir + "Button.ogg"));
        if (s is not null) _button.Stream = s;
    }

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            if (GetViewport().GuiGetHoveredControl() is BaseButton { Disabled: false })
                _button.Play();
        }
    }
}
