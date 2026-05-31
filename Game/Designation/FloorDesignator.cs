using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag draws a filled rect (with a hover preview + size readout); on
// release posts FloorRectBlueprintCommand which queues one FloorBuild
// blueprint per eligible tile. See RectDragDesignator.
public partial class FloorDesignator : RectDragDesignator
{
    protected override ToolMode Mode => ToolMode.Floor;
    protected override int ZOrder => 54;
    protected override Color FillColor => new(0.85f, 0.55f, 0.25f, 0.22f);
    protected override Color BorderColor => new(0.95f, 0.70f, 0.35f, 0.85f);
    protected override bool ShowHoverPreview => true;
    protected override bool ShowMeasure => true;

    protected override void Commit(TilePos start, TilePos current)
        => Host!.QueueCommand(new FloorRectBlueprintCommand(start, current));
}
