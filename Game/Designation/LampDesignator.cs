using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-click places a single lamp blueprint on the hovered tile. Preview is
// a yellow tinted square. See HoverPlaceDesignator.
public partial class LampDesignator : HoverPlaceDesignator
{
    private static readonly Color PreviewFill = new(1.00f, 0.85f, 0.30f, 0.30f);
    private static readonly Color PreviewBorder = new(1.00f, 0.95f, 0.55f, 0.85f);

    protected override ToolMode Mode => ToolMode.Lamp;
    protected override int ZOrder => 55;

    protected override void Place(TilePos tile)
        => Host!.QueueCommand(new PlaceLampBlueprintCommand(tile));

    public override void _Draw()
    {
        if (!Hovering) return;
        var rect = TileRect(HoverTile);
        DrawRect(rect, PreviewFill, filled: true);
        DrawRect(rect, PreviewBorder, filled: false, width: 2f);
    }
}
