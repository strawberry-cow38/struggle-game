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

    private static readonly Color DraftBadge = new(0.82f, 0.20f, 0.18f); // combat red
    private static readonly Color BleedBadge = new(0.74f, 0.09f, 0.10f); // blood red
    private static readonly Color DownedBadge = new(0.95f, 0.70f, 0.15f); // warning amber
    private static readonly Color BadgeBack = new(0.05f, 0.04f, 0.08f, 0.92f);
    private static readonly Color BadgeMark = new(0.96f, 0.96f, 1.00f);

    private bool _drafted;
    private float _rangedLen;  // 0 = no ranged weapon
    private bool _melee;
    private bool _armor;
    private bool _bleeding;
    private bool _downed;

    public void Set(bool drafted, float rangedLen, bool melee, bool armor, bool bleeding, bool downed)
    {
        _drafted = drafted; _rangedLen = rangedLen; _melee = melee; _armor = armor;
        _bleeding = bleeding; _downed = downed;
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

        float br = Mathf.Min(Size.X, Size.Y) * 0.16f;

        // Draft indicator, bottom-right corner: a red badge with a white sword
        // when this colonist is drafted (combat-ready); hidden otherwise.
        if (_drafted)
        {
            var pc = new Vector2(Size.X - br - 3f, Size.Y - br - 3f);
            DrawBadge(pc, br, DraftBadge);
            // Tiny sword glyph: blade with the crossguard up near the hilt so
            // it reads as a sword, not a plus. Pommel dot at the base.
            DrawLine(pc + new Vector2(0f, -br * 0.60f), pc + new Vector2(0f, br * 0.62f), BadgeMark, 1.6f);
            DrawLine(pc + new Vector2(-br * 0.42f, -br * 0.30f), pc + new Vector2(br * 0.42f, -br * 0.30f), BadgeMark, 1.5f);
            DrawCircle(pc + new Vector2(0f, br * 0.62f), 1.4f, BadgeMark);
        }

        // Bleeding indicator, top-right corner: a red badge with a white blood
        // droplet (round bottom + pointed top).
        if (_bleeding)
        {
            var pc = new Vector2(Size.X - br - 3f, br + 3f);
            DrawBadge(pc, br, BleedBadge);
            float dr = br * 0.42f;
            DrawCircle(pc + new Vector2(0f, br * 0.22f), dr, BadgeMark);
            var drop = new[]
            {
                pc + new Vector2(0f, -br * 0.58f),
                pc + new Vector2(-dr, br * 0.10f),
                pc + new Vector2(dr, br * 0.10f),
            };
            DrawColoredPolygon(drop, BadgeMark);
        }

        // Downed indicator, top-left corner: an amber badge with a white
        // down-chevron (incapacitated / unconscious).
        if (_downed)
        {
            var pc = new Vector2(br + 3f, br + 3f);
            DrawBadge(pc, br, DownedBadge);
            DrawLine(pc + new Vector2(-br * 0.45f, -br * 0.18f), pc + new Vector2(0f, br * 0.45f), BadgeMark, 1.9f);
            DrawLine(pc + new Vector2(br * 0.45f, -br * 0.18f), pc + new Vector2(0f, br * 0.45f), BadgeMark, 1.9f);
        }
    }

    // Corner badge backing: a dark outline ring under a colored face.
    private void DrawBadge(Vector2 center, float r, Color face)
    {
        DrawCircle(center, r + 2f, BadgeBack);
        DrawCircle(center, r, face);
    }
}
