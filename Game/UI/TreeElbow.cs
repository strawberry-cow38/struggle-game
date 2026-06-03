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
        float x = 5f;
        float midY = Size.Y * 0.5f;
        DrawLine(new Vector2(x, 0f), new Vector2(x, Last ? midY : Size.Y), col, 2f);
        DrawLine(new Vector2(x, midY), new Vector2(Size.X - 2f, midY), col, 2f);
    }
}
