using Godot;

namespace StruggleGame.Game.UI;

// A panel background that looks like the VFD clock face: the normal glass
// StyleBoxFlat (fill / border / rounded corners / glow shadow) with the same
// fine horizontal "scan line" control-grid wires drawn across it.
//
// Composes an inner StyleBoxFlat rather than reimplementing it, so the glass
// look + AnimateGlow shadow pulsing keep working untouched; this just overlays
// the grid in _Draw. Matches DigitalClock's VFD grid (1px lines every 4px).
public partial class ScanlineStyleBox : StyleBox
{
    public StyleBoxFlat Flat = null!;          // the glass box (also what AnimateGlow pulses)
    public Color LineColor = UiTheme.ScanLine; // matches the clock's VfdGrid
    public float Spacing = 4f;                  // px between scan lines (matches the clock)
    public float Inset = 6f;                    // keep lines off the rounded border

    public override void _Draw(Rid toCanvasItem, Rect2 rect)
    {
        Flat.Draw(toCanvasItem, rect);

        float x0 = rect.Position.X + Inset;
        float x1 = rect.Position.X + rect.Size.X - Inset;
        float yTop = rect.Position.Y + Inset;
        float yBot = rect.Position.Y + rect.Size.Y - Inset;
        if (x1 <= x0) return;
        for (float y = yTop; y < yBot; y += Spacing)
            RenderingServer.CanvasItemAddLine(toCanvasItem, new Vector2(x0, y), new Vector2(x1, y), LineColor, 1f);
    }

    // Delegate hit-testing + the draw bounds (incl. the glow shadow) to the
    // inner box so clicks and the halo behave exactly like a plain panel.
    public override Rect2 _GetDrawRect(Rect2 rect) => Flat.GetDrawRect(rect);
    public override bool _TestMask(Vector2 point, Rect2 rect) => Flat.TestMask(point, rect);
}
