using Godot;

namespace StruggleGame.Game.UI;

// A panel background that looks like the VFD clock face: the normal glass
// StyleBoxFlat (fill / border / rounded corners / glow shadow) with the same
// fine horizontal "scan line" control-grid wires drawn across it. A seeded
// scatter of rows is drawn slightly brighter (see UiTheme.DrawScanlines).
//
// Composes an inner StyleBoxFlat rather than reimplementing it, so the glass
// look + AnimateGlow shadow pulsing keep working untouched; this just overlays
// the grid in _Draw.
public partial class ScanlineStyleBox : StyleBox
{
    public StyleBoxFlat Flat = null!;  // the glass box (also what AnimateGlow pulses)
    public float Spacing = 4f;         // px between scan lines (matches the clock)
    public float Inset = 6f;           // keep lines off the rounded border

    public override void _Draw(Rid toCanvasItem, Rect2 rect)
    {
        Flat.Draw(toCanvasItem, rect);
        UiTheme.DrawScanlines(toCanvasItem, rect, Spacing, Inset);
    }

    // Grow the draw bounds by the inner box's glow shadow so the halo isn't
    // clipped to the panel rect (ShadowOffset is unused here, so size suffices).
    public override Rect2 _GetDrawRect(Rect2 rect) => rect.Grow(Flat.ShadowSize);
    public override bool _TestMask(Vector2 point, Rect2 rect) => Flat.TestMask(point, rect);
}
