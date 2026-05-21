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

    public override void _ExitTree()
    {
        _host?.Dispose();
        _host = null;
    }
}
