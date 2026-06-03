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
        var col = new Color(UiTheme.Border.R, UiTheme.Border.G, UiTheme.Border.B, 0.55f);
        const float t = 2f; // thickness — filled rects keep it pixel-consistent
        float x = 5f;
        float midY = Mathf.Round(Size.Y * 0.5f);
        float vBottom = Last ? midY : Size.Y;
        DrawRect(new Rect2(x, 0f, t, vBottom), col);                 // vertical
        DrawRect(new Rect2(x, midY, Size.X - 2f - x, t), col);       // horizontal stub
    }
}
