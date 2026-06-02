using Godot;

namespace StruggleGame.Game.UI;

// Tiny flat-drawn status icon for a health condition row:
//   - bleeding  -> red droplet, sized by how much it's bleeding
//   - stabilized -> the droplet with a white cross over it (bleed cut, patched)
//   - tended    -> a beige bandage (bleeding stopped)
// No art assets; everything is drawn from primitives.
public partial class ConditionIcon : Control
{
    private float _bleed;
    private bool _tended;
    private bool _stabilized;

    public void Set(float bleed, bool tended, bool stabilized)
    {
        _bleed = bleed; _tended = tended; _stabilized = stabilized;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var c = Size * 0.5f;

        if (_tended)
        {
            // Bandage: beige pill with a darker pad seam down the middle.
            var beige = new Color(0.90f, 0.82f, 0.62f);
            var seam = new Color(0.74f, 0.66f, 0.47f);
            var r = new Rect2(Size.X * 0.12f, Size.Y * 0.30f, Size.X * 0.76f, Size.Y * 0.40f);
            DrawRect(r, beige);
            DrawRect(new Rect2(c.X - 3f, r.Position.Y, 6f, r.Size.Y), seam);
            return;
        }

        if (_bleed > 0f)
        {
            float t = Mathf.Clamp(_bleed * 12f, 0.18f, 1f);
            float radius = Mathf.Lerp(3.5f, 8.5f, t);
            DrawCircle(c, radius, new Color(0.80f, 0.15f, 0.12f));
            if (_stabilized)
            {
                // White medical cross over the droplet.
                var w = new Color(1f, 1f, 1f, 0.92f);
                DrawLine(new Vector2(c.X - radius, c.Y), new Vector2(c.X + radius, c.Y), w, 2f);
                DrawLine(new Vector2(c.X, c.Y - radius), new Vector2(c.X, c.Y + radius), w, 2f);
            }
        }
    }
}
