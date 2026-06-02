using Godot;

namespace StruggleGame.Game.UI;

// Flat-drawn ballistic marker for a gunshot condition, shown left of the
// bleed/treatment icon:
//   - lodged           -> a small brass bullet stuck in
//   - through & through -> a grey arrow (passed clean through)
// Draws nothing for non-gunshot conditions (slot kept for alignment).
public partial class BallisticIcon : Control
{
    private bool _gunshot;
    private bool _lodged;

    public void Set(bool gunshot, bool lodged)
    {
        _gunshot = gunshot; _lodged = lodged;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_gunshot) return;
        var c = Size * 0.5f;
        if (_lodged)
        {
            var brass = new Color(0.80f, 0.60f, 0.28f);
            DrawRect(new Rect2(c.X - 2f, c.Y - 3f, 4f, 7f), brass);   // bullet body
            DrawCircle(new Vector2(c.X, c.Y - 3f), 2f, brass);        // rounded tip
        }
        else
        {
            var grey = new Color(0.78f, 0.82f, 0.86f);                // pass-through arrow
            DrawLine(new Vector2(c.X - 7f, c.Y), new Vector2(c.X + 6f, c.Y), grey, 2f);
            DrawLine(new Vector2(c.X + 6f, c.Y), new Vector2(c.X + 1f, c.Y - 4f), grey, 2f);
            DrawLine(new Vector2(c.X + 6f, c.Y), new Vector2(c.X + 1f, c.Y + 4f), grey, 2f);
        }
    }
}
