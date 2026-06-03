using Godot;

namespace StruggleGame.Game.UI;

// Full-viewport overlay that draws the colonist-bar drag-select rectangle.
public partial class DragRectOverlay : Control
{
    public bool Active;
    public Vector2 Start;
    public Vector2 Cur;

    public override void _Draw()
    {
        if (!Active) return;
        var tl = new Vector2(Mathf.Min(Start.X, Cur.X), Mathf.Min(Start.Y, Cur.Y));
        var rect = new Rect2(tl, (Cur - Start).Abs());
        DrawRect(rect, new Color(UiTheme.Accent.R, UiTheme.Accent.G, UiTheme.Accent.B, 0.16f), filled: true);
        DrawRect(rect, new Color(UiTheme.Accent.R, UiTheme.Accent.G, UiTheme.Accent.B, 0.85f), filled: false, width: 1.5f);
    }
}
