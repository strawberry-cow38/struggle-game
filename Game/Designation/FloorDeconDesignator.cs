using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag a rect; on release posts DeconstructFloorsInRectCommand which
// queues a floor-deconstruct job per built floor in the rect. See
// RectDragDesignator.
public partial class FloorDeconDesignator : RectDragDesignator
{
    protected override ToolMode Mode => ToolMode.FloorDecon;
    protected override int ZOrder => 53;
    protected override Color FillColor => new(0.85f, 0.35f, 0.85f, 0.22f);
    protected override Color BorderColor => new(1.0f, 0.45f, 1.0f, 0.85f);

    protected override void Commit(TilePos start, TilePos current)
        => Host!.QueueCommand(new DeconstructFloorsInRectCommand(start, current));
}
