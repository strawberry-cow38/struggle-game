using Godot;
using StruggleGame.Game.Camera;
using StruggleGame.Game.Render;
using StruggleGame.Sim;

namespace StruggleGame.Game;

public partial class Bootstrap : Node2D
{
    private SimHost? _host;

    public override void _Ready()
    {
        _host = new SimHost();
        GD.Print(
            $"Struggle Game booted. Tile = {SimConstants.TileMeters}m, " +
            $"Tick = {SimConstants.TickHz}Hz, Map = {SimConstants.MapSize}x{SimConstants.MapSize}.");

        AddVoidBackground();

        var renderer = new WorldRenderer { Host = _host, Name = "WorldRenderer" };
        AddChild(renderer);

        float worldPx = SimConstants.MapSize * SimConstants.PixelsPerTile;
        var camera = new GameCamera
        {
            Name = "Camera",
            Position = new Vector2(worldPx * 0.5f, worldPx * 0.5f),
        };
        AddChild(camera);
        camera.MakeCurrent();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_host is null) return;
        if (@event is not InputEventKey k || !k.Pressed || k.Echo) return;

        switch (k.Keycode)
        {
            case Key.Key1: _host.SetTickHz(60); break;
            case Key.Key2: _host.SetTickHz(120); break;
            case Key.Key3: _host.SetTickHz(180); break;
            case Key.Key4: _host.SetTickHz(360); break;
            default: return;
        }
        GD.Print($"Sim tick rate → {_host.TickHz}Hz");
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        _host?.Dispose();
        _host = null;
    }

    private void AddVoidBackground()
    {
        var layer = new CanvasLayer { Name = "VoidLayer", Layer = -100 };
        AddChild(layer);

        var shader = GD.Load<Shader>("res://Game/Render/void.gdshader");
        var rect = new ColorRect
        {
            Name = "Void",
            Material = new ShaderMaterial { Shader = shader },
        };
        rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(rect);
    }
}
