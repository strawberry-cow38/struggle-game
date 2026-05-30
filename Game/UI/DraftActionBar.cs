using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Items;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Game.Tools;

namespace StruggleGame.Game.UI;

// Bottom-center bar shown when a single drafted colonist holding a ranged
// weapon is selected. Offers the weapon's available fire modes + a
// "Force Target" button (enters ToolMode.ForceFireTarget; the next click on
// a pawn issues a fire order). Polls the snapshot each frame like the other
// selection-driven panels.
public partial class DraftActionBar : CanvasLayer
{
    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    private const int ButtonHeight = 28;

    private Panel _root = null!;
    private Label _magLabel = null!;
    private Button _forceTargetBtn = null!;

    private readonly (FireMode mode, FireModeFlags flag)[] _modes =
    {
        (FireMode.Single, FireModeFlags.Single),
        (FireMode.Burst, FireModeFlags.Burst),
        (FireMode.Auto, FireModeFlags.Auto),
    };
    private readonly Button[] _modeButtons = new Button[3];

    private int _shownPawnId = -1;

    public override void _Ready()
    {
        Layer = 96;

        _root = new Panel { Visible = false };
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        _root.CustomMinimumSize = new Vector2(0, ButtonHeight + 16);
        AddChild(_root);

        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 6);
        hbox.Position = new Vector2(8, 8);
        _root.AddChild(hbox);

        var fireLabel = new Label { Text = "Fire:" };
        fireLabel.AddThemeConstantOverride("outline_size", 4);
        hbox.AddChild(fireLabel);

        for (int i = 0; i < _modes.Length; i++)
        {
            var m = _modes[i].mode;
            var btn = new Button
            {
                Text = m.ToString(),
                ToggleMode = true,
                CustomMinimumSize = new Vector2(0, ButtonHeight),
                FocusMode = Control.FocusModeEnum.None,
            };
            btn.Pressed += () =>
            {
                if (Host is null || _shownPawnId < 0) return;
                Host.QueueCommand(new SetFireModeCommand(_shownPawnId, m));
            };
            hbox.AddChild(btn);
            _modeButtons[i] = btn;
        }

        _magLabel = new Label { Text = "" };
        _magLabel.AddThemeConstantOverride("outline_size", 4);
        hbox.AddChild(_magLabel);

        _forceTargetBtn = new Button
        {
            Text = "Force Target",
            ToggleMode = true,
            CustomMinimumSize = new Vector2(0, ButtonHeight),
            FocusMode = Control.FocusModeEnum.None,
        };
        _forceTargetBtn.Pressed += () =>
        {
            if (Tools is null) return;
            Tools.Mode = _forceTargetBtn.ButtonPressed ? ToolMode.ForceFireTarget : ToolMode.None;
        };
        hbox.AddChild(_forceTargetBtn);
    }

    public override void _Process(double delta)
    {
        if (Host is null) { HideBar(); return; }
        var snap = Host.LatestSnapshot;
        int? sel = Host.SelectedDummyId;
        if (snap is null || sel is null) { HideBar(); return; }

        DummyState? found = null;
        foreach (var d in snap.Dummies)
            if (d.EntityId == sel.Value) { found = d; break; }

        if (found is not { } p || !p.Drafted || !p.HasRangedWeapon) { HideBar(); return; }

        _shownPawnId = p.EntityId;
        if (!_root.Visible) _root.Visible = true;

        for (int i = 0; i < _modes.Length; i++)
        {
            bool avail = (p.RangedModes & _modes[i].flag) != 0;
            _modeButtons[i].Visible = avail;
            _modeButtons[i].SetPressedNoSignal(avail && p.RangedMode == _modes[i].mode);
        }
        _magLabel.Text = $"Mag: {p.RangedMag}/{p.RangedMagSize}";

        if (Tools is not null)
            _forceTargetBtn.SetPressedNoSignal(Tools.Mode == ToolMode.ForceFireTarget);
    }

    private void HideBar()
    {
        if (_root.Visible) { _root.Visible = false; _shownPawnId = -1; }
    }
}
