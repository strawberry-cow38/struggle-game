using Godot;

namespace StruggleGame.Game.UI;

// Full-viewport overlay that draws the colonist-bar drag-select rectangle.
public partial class DragRectOverlay : Control
{
    public bool Active;
    public Vector2 Start;
    public Vector2 Cur;

    // Reorder drop indicator: a vertical insertion bar.
    public bool InsertActive;
    public float InsertX;
    public float InsertTop;
    public float InsertHeight;

    public override void _Draw()
    {
        if (Active)
        {
            var tl = new Vector2(Mathf.Min(Start.X, Cur.X), Mathf.Min(Start.Y, Cur.Y));
            var rect = new Rect2(tl, (Cur - Start).Abs());
            DrawRect(rect, new Color(UiTheme.Accent.R, UiTheme.Accent.G, UiTheme.Accent.B, 0.16f), filled: true);
            DrawRect(rect, new Color(UiTheme.Accent.R, UiTheme.Accent.G, UiTheme.Accent.B, 0.85f), filled: false, width: 1.5f);
        }
        if (InsertActive)
            DrawRect(new Rect2(InsertX - 2f, InsertTop, 4f, InsertHeight), UiTheme.Accent);
    }
}
