using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag a rect; on release posts ChopTreesInRectCommand which queues a
// chop job per tree in the rect. See RectDragDesignator.
public partial class ChopDesignator : RectDragDesignator
{
    protected override ToolMode Mode => ToolMode.Chop;
    protected override int ZOrder => 52;
    protected override Color FillColor => new(0.55f, 0.95f, 0.35f, 0.20f);
    protected override Color BorderColor => new(0.65f, 1.0f, 0.45f, 0.85f);

    protected override void Commit(TilePos start, TilePos current)
        => Host!.QueueCommand(new ChopTreesInRectCommand(start, current));
}
