using Godot;

namespace StruggleGame.Game.UI;

// Shared "ethereal PS2 dreamcore" UI styling: translucent cool-indigo glass
// panels with a soft pastel glow, thin light edges, and outlined text so it
// stays readable over the see-through background. No art assets — everything
// is StyleBoxFlat + a generated label Theme.
public static class UiTheme
{
    public static readonly Color Panel = new(0.14f, 0.12f, 0.30f, 0.46f);   // glass indigo (lets frost show)
    public static readonly Color PanelDeep = new(0.07f, 0.06f, 0.15f, 0.82f);
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

    // A Theme that outlines every Label so text stays legible over the glass.
    // Assign to a panel root; children inherit it.
    public static Theme LabelTheme()
    {
        var t = new Theme();
        t.SetColor("font_color", "Label", Text);
        t.SetConstant("outline_size", "Label", 3);
        t.SetColor("font_outline_color", "Label", Outline);
        return t;
    }
}
