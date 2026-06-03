using Godot;

namespace StruggleGame.Game.UI;

// A little tree connector drawn for a condition child row: a vertical line +
// a horizontal stub forming an L (└) for the last child, or a T (├) for the
// rest so the verticals stack into a trunk under the part header.
public partial class TreeElbow : Control
{
    public bool Last;

    public override void _Draw()
    {
        // Integer coords + a fixed mid-Y (not row-height-relative) so the
        // horizontals line up across rows; the stub starts at the vertical's
        // right edge so they meet without overlapping.
        var col = new Color(UiTheme.Border.R, UiTheme.Border.G, UiTheme.Border.B, 0.55f);
        const float t = 2f;     // thickness
        const float x = 6f;     // vertical x
        const float midY = 9f;  // connector height
        float vBottom = Last ? midY + t : Size.Y;
        DrawRect(new Rect2(x, 0f, t, vBottom), col);                       // vertical
        DrawRect(new Rect2(x + t, midY, Size.X - 2f - (x + t), t), col);   // horizontal stub
    }
}
