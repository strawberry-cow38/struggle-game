using Godot;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;
using StruggleGame.Sim.Snapshots;
using StruggleGame.Sim.World;

namespace StruggleGame.Game.UI;

// Right-side panel for a selected lamp. Shows tile + power state, exposes
// the Power cheat toggle (until a real power network ships), a color
// picker + preset swatches, and a Deconstruct button. Multi-select bulk-
// applies across every selected lamp. See TileInfoPanel.
public partial class LampInfoPanel : TileInfoPanel
{
    private Label _tileLabel = null!;
    private Label _stateLabel = null!;
    private CheckBox _poweredChk = null!;
    private ColorPickerButton _colorBtn = null!;
    private Button _deconBtn = null!;
    private bool _suppressToggle;

    protected override TilePos[] SelectedTiles
    {
        get => Host!.SelectedLampTiles;
        set => Host!.SelectedLampTiles = value;
    }
    protected override string Title => "Lamp";
    protected override int MinHeight => 180;

    protected override void BuildBody(VBoxContainer vbox)
    {
        _tileLabel = new Label { Text = "" };
        vbox.AddChild(_tileLabel);

        _stateLabel = new Label { Text = "" };
        vbox.AddChild(_stateLabel);

        _poweredChk = new CheckBox { Text = "Powered (cheat — no power network yet)" };
        _poweredChk.Toggled += OnPoweredToggled;
        vbox.AddChild(_poweredChk);

        var colorRow = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        colorRow.AddChild(new Label { Text = "Color:" });
        _colorBtn = new ColorPickerButton
        {
            CustomMinimumSize = new Vector2(120, 28),
            EditAlpha = false,
            Color = Colors.White,
        };
        _colorBtn.ColorChanged += OnColorChanged;
        colorRow.AddChild(_colorBtn);
        vbox.AddChild(colorRow);

        // Color wheel presets — 12-step hue ring at full saturation plus a
        // white reset. Quick-pick row above the freeform picker.
        var presetRow1 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        var presetRow2 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        presetRow1.AddThemeConstantOverride("separation", 2);
        presetRow2.AddThemeConstantOverride("separation", 2);
        AddPreset(presetRow1, "R",  new LightColor(255,   0,   0));
        AddPreset(presetRow1, "RO", new LightColor(255, 128,   0));
        AddPreset(presetRow1, "O",  new LightColor(255, 191,   0));
        AddPreset(presetRow1, "Y",  new LightColor(255, 255,   0));
        AddPreset(presetRow1, "YG", new LightColor(128, 255,   0));
        AddPreset(presetRow1, "G",  new LightColor(  0, 255,   0));
        AddPreset(presetRow1, "GC", new LightColor(  0, 255, 128));
        AddPreset(presetRow2, "C",  new LightColor(  0, 255, 255));
        AddPreset(presetRow2, "CB", new LightColor(  0, 128, 255));
        AddPreset(presetRow2, "B",  new LightColor(  0,   0, 255));
        AddPreset(presetRow2, "BM", new LightColor(128,   0, 255));
        AddPreset(presetRow2, "M",  new LightColor(255,   0, 255));
        AddPreset(presetRow2, "MR", new LightColor(255,   0, 128));
        AddPreset(presetRow2, "W",  new LightColor(255, 255, 255));
        vbox.AddChild(presetRow1);
        vbox.AddChild(presetRow2);

        _deconBtn = new Button { Text = "Deconstruct", CustomMinimumSize = new Vector2(0, 28) };
        _deconBtn.Pressed += OnDeconPressed;
        vbox.AddChild(_deconBtn);
    }

    protected override void Render(SimSnapshot snap, TilePos[] tiles)
    {
        var live = new List<LampState>(tiles.Length);
        var liveTiles = new List<TilePos>(tiles.Length);
        foreach (var t in tiles)
        {
            foreach (var l in snap.Lamps)
            {
                if (l.Tile == t) { live.Add(l); liveTiles.Add(t); break; }
            }
        }
        if (live.Count == 0)
        {
            SelectedTiles = Array.Empty<TilePos>();
            return;
        }
        if (live.Count != tiles.Length)
        {
            SelectedTiles = liveTiles.ToArray();
        }

        int onCount = 0;
        foreach (var l in live) if (l.PoweredOn) onCount++;

        if (live.Count == 1)
        {
            NameLabel.Text = "Lamp";
            _tileLabel.Text = $"Tile: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _stateLabel.Text = live[0].PoweredOn ? "Power: ON" : "Power: OFF";
        }
        else
        {
            NameLabel.Text = $"Lamps ({live.Count})";
            _tileLabel.Text = $"First: ({liveTiles[0].X}, {liveTiles[0].Y})";
            _stateLabel.Text = $"Power on: {onCount}/{live.Count}";
        }

        _suppressToggle = true;
        _poweredChk.ButtonPressed = onCount == live.Count;
        // Color reflects the first selected lamp. Multi-select bulk-sets
        // every selected lamp to whatever the picker emits on change.
        var c0 = live[0].Color;
        _colorBtn.Color = new Color(c0.R / 255f, c0.G / 255f, c0.B / 255f);
        _suppressToggle = false;
    }

    private void OnPoweredToggled(bool pressed)
    {
        if (_suppressToggle || Host is null) return;
        foreach (var t in Host.SelectedLampTiles)
            Host.QueueCommand(new SetLampPoweredCommand(t, pressed));
    }

    private void OnColorChanged(Color c)
    {
        if (_suppressToggle || Host is null) return;
        var lc = new LightColor((byte)Math.Clamp(Math.Round(c.R * 255f), 0, 255),
                                (byte)Math.Clamp(Math.Round(c.G * 255f), 0, 255),
                                (byte)Math.Clamp(Math.Round(c.B * 255f), 0, 255));
        foreach (var t in Host.SelectedLampTiles)
            Host.QueueCommand(new SetLampColorCommand(t, lc));
    }

    // Preset swatch — small square tinted to the color. Click bulk-sets
    // every selected lamp via the same SetLampColorCommand path the
    // freeform picker uses, and syncs the picker swatch too.
    private void AddPreset(HBoxContainer row, string tooltip, LightColor color)
    {
        var btn = new Button
        {
            CustomMinimumSize = new Vector2(22, 22),
            TooltipText = tooltip,
            Flat = false,
        };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(color.R / 255f, color.G / 255f, color.B / 255f),
            BorderColor = new Color(0, 0, 0, 0.6f),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        };
        btn.AddThemeStyleboxOverride("normal", sb);
        btn.AddThemeStyleboxOverride("hover", sb);
        btn.AddThemeStyleboxOverride("pressed", sb);
        btn.Pressed += () =>
        {
            if (Host is null) return;
            _suppressToggle = true;
            _colorBtn.Color = new Color(color.R / 255f, color.G / 255f, color.B / 255f);
            _suppressToggle = false;
            foreach (var t in Host.SelectedLampTiles)
                Host.QueueCommand(new SetLampColorCommand(t, color));
        };
        row.AddChild(btn);
    }

    private void OnDeconPressed()
    {
        if (Host is null) return;
        foreach (var t in Host.SelectedLampTiles)
            Host.QueueCommand(new PostLampDeconCommand(t));
    }
}
