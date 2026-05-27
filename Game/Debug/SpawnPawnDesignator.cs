using Godot;
using StruggleGame.Game.Designation;
using StruggleGame.Game.Tools;
using StruggleGame.Sim;
using StruggleGame.Sim.Commands;

namespace StruggleGame.Game.Debug;

// Active when ToolMode == SpawnPawn. Every LMB click queues a
// SpawnDummyCommand; click location is intentionally ignored — the sim
// picks a random walkable tile. This matches the master's spec
// ("spawns a new random pawn").
public partial class SpawnPawnDesignator : Node2D
{
    public SimHost? Host { get; set; }
    public ToolService? Tools { get; set; }

    public override void _Ready()
    {
        if (Tools is not null) this.BindInputToMode(Tools, m => m == ToolMode.SpawnPawn);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Host is null || Tools is null) return;
        if (Tools.Mode != ToolMode.SpawnPawn) return;
        if (@event is not InputEventMouseButton mb) return;
        if (mb.ButtonIndex != MouseButton.Left || !mb.Pressed) return;

        Host.QueueCommand(new SpawnDummyCommand());
        GetViewport().SetInputAsHandled();
    }
}
