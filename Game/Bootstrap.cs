using Godot;
using StruggleGame.Sim;

namespace StruggleGame.Game;

public partial class Bootstrap : Node2D
{
    private SimRuntime? _sim;

    public override void _Ready()
    {
        _sim = new SimRuntime();
        GD.Print($"Struggle Game booted. Tile = {SimConstants.TileMeters}m, TickHz = {SimConstants.TickHz}.");
    }

    public override void _Process(double delta)
    {
        _sim?.Step();
    }
}
