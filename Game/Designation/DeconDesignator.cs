using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag a rect; on release posts DeconstructWallsInRectCommand which
// queues one Deconstruct job per player-built wall and one DoorDeconstruct
// per door in the rect. See RectDragDesignator.
public partial class DeconDesignator : RectDragDesignator
{
    protected override ToolMode Mode => ToolMode.Decon;
    protected override int ZOrder => 53;
    protected override Color FillColor => new(1.0f, 0.55f, 0.15f, 0.22f);
    protected override Color BorderColor => new(1.0f, 0.70f, 0.25f, 0.85f);

    protected override void Commit(TilePos start, TilePos current)
        => Host!.QueueCommand(new DeconstructWallsInRectCommand(start, current));
}
