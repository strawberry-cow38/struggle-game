using Godot;

namespace StruggleGame.Game.UI;

// A little top-down "clone" of the colonist as they read in the world: body
// disc + facing arrow (+ drafted ring), plus a glyph for their loadout (ranged
// barrel / melee blade / armor ring). Redrawn only when the loadout changes.
public partial class PortraitView : Control
{
    private static readonly Color BodyColor = new(0.95f, 0.55f, 0.20f);   // matches DummyColor
    private static readonly Color FacingColor = new(0.15f, 0.10f, 0.05f, 0.9f);
    private static readonly Color DraftedRing = new(1.0f, 0.25f, 0.20f);
    private static readonly Color BarrelColor = new(0.16f, 0.14f, 0.18f);
    private static readonly Color BladeColor = new(0.72f, 0.74f, 0.80f);
    private static readonly Color ArmorColor = new(0.55f, 0.60f, 0.72f, 0.85f);

    private bool _drafted;
    private float _rangedLen;  // 0 = no ranged weapon
    private bool _melee;
    private bool _armor;
    private Color _mood = new(0, 0, 0, 0); // mood pip color (A=0 → no pip)

    public void Set(bool drafted, float rangedLen, bool melee, bool armor)
    {
        _drafted = drafted; _rangedLen = rangedLen; _melee = melee; _armor = armor;
        QueueRedraw();
    }

    // Mood pip color, shown in the bottom-right corner. Redraws only on change.
    public void SetMood(Color mood)
    {
        if (_mood == mood) return;
        _mood = mood;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var c = Size * 0.5f;
        float r = Mathf.Min(Size.X, Size.Y) * 0.27f;

        // Held weapon, drawn first so the body overlaps the grip end.
        if (_rangedLen > 0f)
        {
            float bl = Mathf.Lerp(r * 1.3f, r * 2.7f, Mathf.Clamp(_rangedLen, 0f, 1f));
            DrawRect(new Rect2(c.X + r * 0.55f - 2f, c.Y - bl * 0.35f, 4f, bl), BarrelColor);
        }
        else if (_melee)
        {
            DrawRect(new Rect2(c.X + r * 0.6f - 1.5f, c.Y - r * 0.7f, 3f, r * 1.5f), BladeColor);
        }

        if (_armor)
            DrawArc(c, r + 3f, 0f, Mathf.Tau, 24, ArmorColor, 2f, antialiased: true);

        DrawCircle(c, r, BodyColor);

        // Facing arrow pointing down (toward the viewer).
        var dir = new Vector2(0f, 1f);
        var perp = new Vector2(1f, 0f);
        var tri = new[]
        {
            c + dir * (r * 1.15f),
            c + dir * (r * 0.35f) + perp * (r * 0.5f),
            c + dir * (r * 0.35f) - perp * (r * 0.5f),
        };
        DrawColoredPolygon(tri, FacingColor);

        if (_drafted)
            DrawArc(c, r + 2f, 0f, Mathf.Tau, 28, DraftedRing, 2f, antialiased: true);

        // Mood pip, bottom-right corner, with a dark backing for contrast.
        if (_mood.A > 0f)
        {
            float pr = Mathf.Min(Size.X, Size.Y) * 0.15f;
            var pc = new Vector2(Size.X - pr - 3f, Size.Y - pr - 3f);
            DrawCircle(pc, pr + 2f, new Color(0.05f, 0.04f, 0.08f, 0.92f));
            DrawCircle(pc, pr, _mood);
        }
    }
}
