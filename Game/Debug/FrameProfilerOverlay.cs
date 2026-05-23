using Godot;
using StruggleGame.Sim;

namespace StruggleGame.Game.Debug;

// Press F3 to toggle. Shows Godot per-frame budget (process, physics,
// draw calls, render time) + every named FrameProfiler section's
// rolling avg + max ms. Top-right column so it doesn't fight HudOverlay
// (top-left) or the stuck-watcher (also top-right but lower band).
public partial class FrameProfilerOverlay : CanvasLayer
{
    public SimHost? Host { get; set; }

    private Label _label = null!;
    private bool _visible = true;

    public override void _Ready()
    {
        Layer = 110;

        var settings = new LabelSettings
        {
            FontSize = 14,
            FontColor = new Color(0.85f, 1.0f, 0.85f),
            OutlineSize = 4,
            OutlineColor = new Color(0f, 0f, 0f, 0.85f),
        };
        _label = new Label
        {
            Name = "Readout",
            LabelSettings = settings,
            AnchorLeft = 1, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 0,
            OffsetLeft = -360, OffsetTop = 240, OffsetRight = -12, OffsetBottom = 800,
            HorizontalAlignment = HorizontalAlignment.Left,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = _visible,
        };
        AddChild(_label);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey k && k.Pressed && !k.Echo && k.Keycode == Key.F3)
        {
            _visible = !_visible;
            _label.Visible = _visible;
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!_visible) return;

        double frameMs = delta * 1000.0;
        double processMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        double physMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double prims = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double objs = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double vidMem = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / (1024.0 * 1024.0);

        var sb = new System.Text.StringBuilder();
        sb.Append("== FRAME PROFILER (F3) ==\n");
        sb.AppendFormat("frame      {0,6:0.00} ms ({1,4:0} fps)\n", frameMs, frameMs > 0 ? 1000.0 / frameMs : 0);
        sb.AppendFormat("process    {0,6:0.00} ms\n", processMs);
        sb.AppendFormat("physics    {0,6:0.00} ms\n", physMs);
        sb.AppendFormat("draw calls {0,6:0}\n", drawCalls);
        sb.AppendFormat("prims      {0,6:0}\n", prims);
        sb.AppendFormat("objects    {0,6:0}\n", objs);
        sb.AppendFormat("vidmem     {0,6:0.0} MB\n", vidMem);
        if (Host is not null)
        {
            sb.AppendFormat("sim tps    {0,6:0} / {1}\n", Host.ActualTps, Host.TickHz);
        }
        sb.Append("\n-- render sections (avg | max) --\n");
        foreach (var s in FrameProfiler.Instance.Sections)
        {
            sb.AppendFormat("{0,-14} {1,6:0.00} | {2,6:0.00} ms\n", s.Name, s.AvgMs(), s.MaxMs());
        }
        _label.Text = sb.ToString();
    }
}
