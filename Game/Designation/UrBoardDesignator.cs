using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Cursor previews a 1-tile board footprint (red when blocked). LMB places
// via PlaceUrBoardCommand. See HoverPlaceDesignator.
public partial class UrBoardDesignator : HoverPlaceDesignator
{
    private static readonly Color OkFill   = new(0.65f, 0.45f, 0.20f, 0.45f);
    private static readonly Color OkBorder = new(0.95f, 0.75f, 0.40f, 0.95f);
    private static readonly Color BadFill   = new(0.85f, 0.20f, 0.15f, 0.45f);
    private static readonly Color BadBorder = new(1.00f, 0.45f, 0.35f, 0.95f);
    private static readonly Color InlayOk   = new(0.95f, 0.85f, 0.45f, 0.85f);
    private static readonly Color InlayBad  = new(1.00f, 0.85f, 0.80f, 0.85f);

    protected override ToolMode Mode => ToolMode.UrBoard;
    protected override int ZOrder => 55;

    protected override void Place(TilePos tile)
        => Host!.QueueCommand(new PlaceUrBoardCommand(tile));

    public override void _Draw()
    {
        if (!Hovering || Host is null) return;
        bool ok = Host.CanPlaceUrBoard(HoverTile);
        var fill = ok ? OkFill : BadFill;
        var border = ok ? OkBorder : BadBorder;
        var inlay = ok ? InlayOk : InlayBad;

        var rect = TileRect(HoverTile);
        DrawRect(rect, fill, filled: true);
        float inset = PixelsPerTile * 0.22f;
        var inlayRect = new Rect2(rect.Position.X + inset, rect.Position.Y + inset, PixelsPerTile - 2 * inset, PixelsPerTile - 2 * inset);
        DrawRect(inlayRect, inlay, filled: true);
        DrawRect(rect, border, filled: false, width: 2f);
    }
}
