using Godot;
using StruggleGame.Sim;

namespace StruggleGame.Game.UI;

// Top-left perf readout: FPS / TPS / speed multiplier. Uses a CanvasLayer
// so it sits above the world and ignores camera transforms.
public partial class HudOverlay : CanvasLayer
{
    public SimHost? Host { get; set; }

    // Bump on every build so the running game shows whether it is current.
    private const string BuildTag = "build 8d86eb5+1";

    private Label _label = null!;
    private Label _versionLabel = null!;

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

        _versionLabel = new Label
        {
            Name = "Version",
            Text = BuildTag,
            LabelSettings = settings,
            HorizontalAlignment = HorizontalAlignment.Right,
            AnchorLeft = 1, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 0,
            OffsetLeft = -260, OffsetTop = 8, OffsetRight = -12, OffsetBottom = 36,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        AddChild(_versionLabel);
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
