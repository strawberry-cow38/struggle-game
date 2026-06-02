using Godot;

namespace StruggleGame.Game.UI;

// Shared "ethereal PS2 dreamcore" UI styling: translucent cool-indigo glass
// panels with a soft pastel glow, thin light edges, and outlined text so it
// stays readable over the see-through background. No art assets — everything
// is StyleBoxFlat + a generated label Theme.
public static class UiTheme
{
    public static readonly Color Panel = new(0.17f, 0.07f, 0.30f, 0.52f);   // deep glass purple
    public static readonly Color PanelDeep = new(0.11f, 0.04f, 0.19f, 0.85f);
    public static readonly Color Inset = new(0.05f, 0.05f, 0.12f, 0.55f);
    public static readonly Color Border = new(0.74f, 0.82f, 1.0f, 0.45f);   // soft pastel edge
    public static readonly Color Glow = new(0.55f, 0.62f, 1.0f, 0.22f);
    public static readonly Color Accent = new(0.62f, 0.86f, 0.98f);         // pastel cyan
    public static readonly Color AccentPink = new(0.96f, 0.76f, 0.93f);
    public static readonly Color Text = new(0.93f, 0.95f, 1.0f);
    public static readonly Color TextDim = new(0.72f, 0.76f, 0.92f);
    public static readonly Color Outline = new(0.03f, 0.03f, 0.09f, 0.88f);

    // Glassy panel: translucent fill, soft glow halo, thin pastel border,
    // rounded corners, uniform content margin.
    public static StyleBoxFlat PanelBox(int corner = 12, int margin = 12)
        => Box(Panel, Border, 1, corner, margin, glow: true);

    public static StyleBoxFlat InsetBox(Color bg, int corner = 6, int margin = 0)
        => Box(bg, new Color(Border.R, Border.G, Border.B, 0.25f), 1, corner, margin, glow: false);

    public static StyleBoxFlat Box(Color bg, Color border, int borderWidth, int corner, int margin, bool glow)
    {
        var b = new StyleBoxFlat { BgColor = bg };
        b.BorderColor = border;
        b.BorderWidthLeft = b.BorderWidthRight = b.BorderWidthTop = b.BorderWidthBottom = borderWidth;
        b.CornerRadiusTopLeft = b.CornerRadiusTopRight = b.CornerRadiusBottomLeft = b.CornerRadiusBottomRight = corner;
        b.ContentMarginLeft = b.ContentMarginRight = b.ContentMarginTop = b.ContentMarginBottom = margin;
        if (glow)
        {
            b.ShadowColor = Glow;
            b.ShadowSize = 10;
        }
        return b;
    }

    // The UI font (Sora), lazily loaded + bumped to a slightly heavier weight
    // via the variable-font axis — "fatter" than regular without going bold.
    private static Font? _font;
    private static bool _fontTried;
    public static Font? Font
    {
        get
        {
            if (_fontTried) return _font;
            _fontTried = true;
            var bytes = Godot.FileAccess.GetFileAsBytes("res://assets/fonts/Sora.ttf");
            if (bytes.Length == 0) return _font;
            var ff = new FontFile { Data = bytes };
            var fv = new FontVariation { BaseFont = ff };
            // 0x77676874 = the OpenType "wght" axis tag.
            fv.SetVariationOpentype(new Godot.Collections.Dictionary
            {
                { 0x77676874, 560 },
            });
            _font = fv;
            return _font;
        }
    }

    // A Theme that sets the UI font + outlines every Label so text stays
    // legible over the glass. Assign to a panel root; children inherit it.
    public static Theme LabelTheme()
    {
        var t = new Theme();
        if (Font is not null) t.DefaultFont = Font;
        t.SetColor("font_color", "Label", Text);
        t.SetConstant("outline_size", "Label", 3);
        t.SetColor("font_outline_color", "Label", Outline);
        return t;
    }
}
