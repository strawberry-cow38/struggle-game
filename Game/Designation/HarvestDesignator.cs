using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag a rect; on release posts HarvestInRectCommand which queues a
// harvest job per mature crop in the rect. See RectDragDesignator.
public partial class HarvestDesignator : RectDragDesignator
{
    protected override ToolMode Mode => ToolMode.Harvest;
    protected override int ZOrder => 52;
    protected override Color FillColor => new(1.0f, 0.55f, 0.20f, 0.20f);
    protected override Color BorderColor => new(1.0f, 0.70f, 0.30f, 0.85f);

    protected override void Commit(TilePos start, TilePos current)
        => Host!.QueueCommand(new HarvestInRectCommand(start, current));
}
