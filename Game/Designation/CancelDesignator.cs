using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag a rect; on release posts CancelJobsInRectCommand which cancels
// every designated job/blueprint in the rect. See RectDragDesignator.
public partial class CancelDesignator : RectDragDesignator
{
    protected override ToolMode Mode => ToolMode.Cancel;
    protected override int ZOrder => 51;
    protected override Color FillColor => new(0.95f, 0.20f, 0.20f, 0.25f);
    protected override Color BorderColor => new(1.0f, 0.30f, 0.30f, 0.85f);

    protected override void Commit(TilePos start, TilePos current)
        => Host!.QueueCommand(new CancelJobsInRectCommand(start, current));
}
