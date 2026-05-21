using Godot;
using StruggleGame.Sim;

namespace StruggleGame.Game.UI;

// Top-left perf readout: FPS / TPS / speed multiplier. Uses a CanvasLayer
// so it sits above the world and ignores camera transforms.
public partial class HudOverlay : CanvasLayer
{
    public SimHost? Host { get; set; }

    private Label _label = null!;

    public override void _Ready()
    {
        Layer = 100;

        var settings = new LabelSettings
        {
            FontSize = 20,
            FontColor = new Color(1.0f, 0.92f, 0.10f),
            OutlineSize = 4,
            OutlineColor = new Color(0f, 0f, 0f, 0.85f),
        };

        _label = new Label
        {
            Name = "Readout",
            LabelSettings = settings,
            Position = new Vector2(12, 8),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_label);
    }

    public override void _Process(double delta)
    {
        if (Host is null) return;
        float fps = (float)Engine.GetFramesPerSecond();
        float tps = Host.ActualTps;
        float speed = Host.TickHz / (float)SimConstants.TickHz;
        _label.Text = $"FPS  {fps:0}\nTPS  {tps:0} / {Host.TickHz}\nSPEED {speed:0.##}x";
    }
}
