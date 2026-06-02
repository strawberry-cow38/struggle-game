using Godot;
using StruggleGame.Sim;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.UI;

// Top-left perf readout: FPS / TPS / speed multiplier. Uses a CanvasLayer
// so it sits above the world and ignores camera transforms.
public partial class HudOverlay : CanvasLayer
{
    public SimHost? Host { get; set; }

    private Label _label = null!;

    // Refresh the readout at most this often. Per-frame at 1500+ fps the
    // sim queries + string interpolation showed up under the mouse-move
    // perf hit; cap it to 30Hz so hover info still feels responsive
    // without spinning the CPU.
    private const double RefreshIntervalSec = 1.0 / 30.0;
    private double _refreshAccum;

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
        _refreshAccum += delta;
        if (_refreshAccum < RefreshIntervalSec) return;
        _refreshAccum = 0;
        float fps = (float)Engine.GetFramesPerSecond();
        float tps = Host.ActualTps;
        float speed = Host.TickHz / (float)SimConstants.TickHz;
        int rooms = Host.LatestSnapshot?.RoomCount ?? 0;
        string paused = Host.IsPaused ? "  [PAUSED]" : string.Empty;

        // Screen→world for the hover tile: convert viewport mouse
        // through the root canvas transform inverse (HudOverlay sits on
        // a CanvasLayer so it has no Node2D transform of its own).
        var viewport = GetViewport();
        var screenMouse = viewport.GetMousePosition();
        var canvasInv = GetTree().Root.GetCanvasTransform().AffineInverse();
        var worldMouse = canvasInv * screenMouse;
        int tx = (int)Math.Floor(worldMouse.X / SimConstants.PixelsPerTile);
        int ty = (int)Math.Floor(worldMouse.Y / SimConstants.PixelsPerTile);
        string hoverLine;
        if (tx >= 0 && tx < SimConstants.MapSize && ty >= 0 && ty < SimConstants.MapSize)
        {
            var tile = new TilePos(tx, ty);
            int rid = Host.RoomIdAt(tile);
            float temp = Host.TileTempC(tile);
            float light = Host.LightAt(tile);
            string roomLabel = rid == 0 ? "outdoor" : ($"room {rid}");
            hoverLine = $"\nHOVER {tx},{ty}  {roomLabel}  {temp:0.#}°C  light {light * 100f:0}%";
        }
        else
        {
            hoverLine = "\nHOVER -";
        }

        // World time + calendar from the latest snapshot (sim-thread
        // authoritative). Default to epoch if no snap yet so the format
        // string never blanks.
        var dt = Host.LatestSnapshot is { } snap
            ? SimRuntime.WorldEpoch.AddSeconds(snap.WorldTimeSec)
            : SimRuntime.WorldEpoch;
        string clock = dt.ToString("HH:mm");
        string date = dt.ToString("ddd MMM d, yyyy");

        _label.Text = $"FPS  {fps:0}\nTPS  {tps:0} / {Host.TickHz}{paused}\nSPEED {speed:0.##}x\nROOMS {rooms}\n{clock}  {date}{hoverLine}";
    }
}
