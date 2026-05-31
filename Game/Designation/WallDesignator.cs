using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// LMB-drag places a cardinal line of wall blueprints; the preview line
// follows the cursor. On release each tile is queued as a
// PlaceWallBlueprintCommand. See LineDragDesignator.
public partial class WallDesignator : LineDragDesignator
{
    private static readonly Color PreviewColor = new(1.0f, 0.85f, 0.20f, 0.45f);
    private static readonly Color PreviewBorder = new(1.0f, 0.85f, 0.20f, 0.90f);

    protected override ToolMode Mode => ToolMode.BuildWall;
    protected override int ZOrder => 50;

    protected override void CommitTile(TilePos tile)
        => Host!.QueueCommand(new PlaceWallBlueprintCommand(tile));

    protected override void DrawTilePreview(TilePos t)
    {
        var rect = new Rect2(t.X * PixelsPerTile, t.Y * PixelsPerTile, PixelsPerTile, PixelsPerTile);
        DrawRect(rect, PreviewColor, filled: true);
        DrawRect(rect, PreviewBorder, filled: false, width: 2f);
    }
}
