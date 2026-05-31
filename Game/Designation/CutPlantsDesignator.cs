using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag a rect; on release posts CutPlantsInRectCommand which queues a
// cut job per non-tree plant in the rect. See RectDragDesignator.
public partial class CutPlantsDesignator : RectDragDesignator
{
    protected override ToolMode Mode => ToolMode.CutPlants;
    protected override int ZOrder => 52;
    protected override Color FillColor => new(0.95f, 0.85f, 0.30f, 0.20f);
    protected override Color BorderColor => new(1.0f, 0.92f, 0.40f, 0.85f);

    protected override void Commit(TilePos start, TilePos current)
        => Host!.QueueCommand(new CutPlantsInRectCommand(start, current));
}
