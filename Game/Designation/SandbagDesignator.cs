using Godot;
using StruggleGame.Game.Tools;
using StruggleGame.Sim.Commands;
using StruggleGame.Sim.Map;

namespace StruggleGame.Game.Designation;

// Cursor previews a 1-tile sandbag footprint (red when blocked). LMB
// places via PlaceSandbagCommand. See HoverPlaceDesignator.
public partial class SandbagDesignator : HoverPlaceDesignator
{
    private static readonly Color OkFill   = new(0.55f, 0.50f, 0.30f, 0.45f);
    private static readonly Color OkBorder = new(0.80f, 0.72f, 0.45f, 0.95f);
    private static readonly Color BadFill   = new(0.85f, 0.20f, 0.15f, 0.45f);
    private static readonly Color BadBorder = new(1.00f, 0.45f, 0.35f, 0.95f);
    private static readonly Color InlayOk   = new(0.72f, 0.66f, 0.42f, 0.85f);
    private static readonly Color InlayBad  = new(1.00f, 0.85f, 0.80f, 0.85f);

    protected override ToolMode Mode => ToolMode.Sandbag;
    protected override int ZOrder => 55;

    protected override void Place(TilePos tile)
        => Host!.QueueCommand(new PlaceSandbagCommand(tile));

    public override void _Draw()
    {
        if (!Hovering || Host is null) return;
        bool ok = Host.CanPlaceSandbag(HoverTile);
        var fill = ok ? OkFill : BadFill;
        var border = ok ? OkBorder : BadBorder;
        var inlay = ok ? InlayOk : InlayBad;

        var rect = TileRect(HoverTile);
        DrawRect(rect, fill, filled: true);
        // Two stacked bag rows to read as a low barricade.
        float inset = PixelsPerTile * 0.14f;
        float rowH = (PixelsPerTile - 2 * inset) * 0.42f;
        var top = new Rect2(rect.Position.X + inset, rect.Position.Y + inset, PixelsPerTile - 2 * inset, rowH);
        var bot = new Rect2(rect.Position.X + inset, rect.Position.Y + PixelsPerTile - inset - rowH, PixelsPerTile - 2 * inset, rowH);
        DrawRect(top, inlay, filled: true);
        DrawRect(bot, inlay, filled: true);
        DrawRect(rect, border, filled: false, width: 2f);
    }
}
