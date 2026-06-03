using Godot;

namespace StruggleGame.Game.UI;

// A tiny vector weapon icon for the Pocket Sand segments: a rifle (ranged),
// a knife (melee), or a fist (unarmed). No art assets — drawn in _Draw.
public partial class WeaponGlyph : Control
{
    public enum Kind { Ranged, Melee, Unarmed }

    private static readonly Color Metal = new(0.78f, 0.80f, 0.86f);
    private static readonly Color Dark = new(0.18f, 0.16f, 0.20f);
    private static readonly Color Skin = new(0.90f, 0.74f, 0.58f);

    private Kind _kind = Kind.Unarmed;
    public Kind Glyph { get => _kind; set { _kind = value; QueueRedraw(); } }

    public override void _Draw()
    {
        float s = Mathf.Min(Size.X, Size.Y);
        var c = Size * 0.5f;
        switch (_kind)
        {
            case Kind.Ranged: DrawRifle(c, s); break;
            case Kind.Melee: DrawKnife(c, s); break;
            default: DrawFist(c, s); break;
        }
    }

    // Side-view rifle pointing right: stock, receiver, long barrel, magazine.
    private void DrawRifle(Vector2 c, float s)
    {
        float w = s * 0.72f, h = s * 0.16f;
        var bar = new Rect2(c.X - w * 0.5f, c.Y - h * 0.5f, w, h);
        DrawRect(bar, Metal);                                                   // barrel/receiver
        DrawRect(new Rect2(bar.Position.X - s * 0.06f, bar.Position.Y - s * 0.04f, s * 0.14f, h + s * 0.16f), Dark); // stock
        DrawRect(new Rect2(c.X - s * 0.04f, bar.Position.Y + h, s * 0.12f, s * 0.20f), Dark); // magazine
        DrawRect(new Rect2(c.X - s * 0.02f, bar.Position.Y + h, s * 0.05f, s * 0.10f), Metal); // trigger area
    }

    // Knife: angled blade from lower-left to upper-right with a dark handle.
    private void DrawKnife(Vector2 c, float s)
    {
        var tip = c + new Vector2(s * 0.32f, -s * 0.32f);
        var baseA = c + new Vector2(-s * 0.10f, -s * 0.02f);
        var baseB = c + new Vector2(-s * 0.02f, s * 0.10f);
        DrawColoredPolygon(new[] { tip, baseA, baseB }, Metal);                 // blade
        DrawLine(c + new Vector2(-s * 0.10f, s * 0.02f), c + new Vector2(-s * 0.30f, s * 0.22f), Dark, s * 0.12f); // handle
    }

    // Fist: rounded knuckle block with three knuckle grooves + a thumb.
    private void DrawFist(Vector2 c, float s)
    {
        float w = s * 0.46f, h = s * 0.40f;
        var r = new Rect2(c.X - w * 0.5f, c.Y - h * 0.4f, w, h);
        DrawRect(r, Skin);
        DrawRect(new Rect2(r.Position.X, r.Position.Y, w, s * 0.12f), Skin.Darkened(0.12f)); // knuckle row
        for (int i = 1; i < 4; i++)
            DrawLine(new Vector2(r.Position.X + w * i / 4f, r.Position.Y),
                     new Vector2(r.Position.X + w * i / 4f, r.Position.Y + s * 0.12f), Dark, 1.5f);
        DrawRect(new Rect2(r.Position.X - s * 0.08f, c.Y, s * 0.10f, s * 0.16f), Skin); // thumb
    }
}
