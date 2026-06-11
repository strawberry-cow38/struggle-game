using Godot;
using StruggleGame.Sim.Commands;

namespace StruggleGame.Game.UI;

// "Drop X" dialog: a slider + number box (synced) capped at the stack size.
// Confirm posts a DropHeldAmountCommand for the chosen held-inventory stack.
public partial class DropQuantityDialog : CanvasLayer
{
    public SimHost? Host { get; set; }

    private PopupPanel _popup = null!;
    private Label _title = null!;
    private HSlider _slider = null!;
    private SpinBox _spin = null!;

    private int _pawnId;
    private int _heldIndex;
    private bool _sync; // guard against slider<->spin feedback loop

    public override void _Ready()
    {
        Layer = 96;
        _popup = new PopupPanel { Name = "DropPopup" };
        AddChild(_popup);

        var vbox = new VBoxContainer { CustomMinimumSize = new Vector2(260, 0) };
        vbox.AddThemeConstantOverride("separation", 8);
        _popup.AddChild(vbox);

        _title = new Label { Text = "Drop", HorizontalAlignment = HorizontalAlignment.Center };
        _title.AddThemeFontSizeOverride("font_size", 15);
        vbox.AddChild(_title);

        _slider = new HSlider { MinValue = 1, MaxValue = 1, Step = 1, CustomMinimumSize = new Vector2(0, 20) };
        _slider.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        vbox.AddChild(_slider);

        _spin = new SpinBox { MinValue = 1, MaxValue = 1, Step = 1 };
        vbox.AddChild(_spin);

        _slider.ValueChanged += v =>
        {
            if (_sync) return;
            _sync = true; _spin.Value = v; _sync = false;
        };
        _spin.ValueChanged += v =>
        {
            if (_sync) return;
            _sync = true; _slider.Value = v; _sync = false;
        };

        var btns = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        btns.AddThemeConstantOverride("separation", 8);
        var cancel = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(80, 28) };
        cancel.Pressed += () => _popup.Hide();
        btns.AddChild(cancel);
        var ok = new Button { Text = "Drop", CustomMinimumSize = new Vector2(80, 28) };
        ok.Pressed += OnConfirm;
        btns.AddChild(ok);
        vbox.AddChild(btns);
    }

    public void Open(int pawnId, int heldIndex, int maxUnits, string itemName)
    {
        if (maxUnits < 1) return;
        _pawnId = pawnId;
        _heldIndex = heldIndex;
        _title.Text = $"Drop {itemName} (max {maxUnits})";
        _sync = true;
        _slider.MaxValue = maxUnits;
        _spin.MaxValue = maxUnits;
        _slider.Value = maxUnits;
        _spin.Value = maxUnits;
        _sync = false;
        _popup.PopupCentered();
    }

    private void OnConfirm()
    {
        int count = (int)_spin.Value;
        if (Host is not null && count > 0)
            Host.QueueCommand(new DropHeldAmountCommand(_pawnId, _heldIndex, count));
        _popup.Hide();
    }
}
