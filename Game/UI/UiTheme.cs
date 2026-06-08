using Godot;

namespace StruggleGame.Game.UI;

// Shared "ethereal PS2 dreamcore" UI styling: translucent cool-indigo glass
// panels with a soft pastel glow, thin light edges, and outlined text so it
// stays readable over the see-through background. No art assets — everything
// is StyleBoxFlat + a generated label Theme.
public static class UiTheme
{
    public static readonly Color PanelDeep = new(0.11f, 0.04f, 0.19f, 0.97f); // draft tiles
    public static readonly Color Panel = new(0.11f, 0.04f, 0.19f, 0.95f);     // panes — near-opaque purple to match the clock screen
    public static readonly Color Inset = new(0.05f, 0.05f, 0.12f, 0.55f);
    public static readonly Color Border = new(0.42f, 0.26f, 0.58f, 0.65f);  // dark purple edge
    public static readonly Color Glow = new(0.36f, 0.16f, 0.52f, 0.22f);    // dark purple glow
    public static readonly Color Accent = new(0.62f, 0.86f, 0.98f);         // pastel cyan
    public static readonly Color AccentPink = new(0.96f, 0.76f, 0.93f);
    public static readonly Color Text = new(0.93f, 0.95f, 1.0f);
    public static readonly Color TextDim = new(0.72f, 0.76f, 0.92f);
    public static readonly Color Outline = new(0.03f, 0.03f, 0.09f, 0.88f);
    public static readonly Color ScanLine = new(0.58f, 0.38f, 0.90f, 0.085f);        // VFD control-grid wires (matches the clock)
    public static readonly Color ScanLineBright = new(0.61f, 0.40f, 0.945f, 0.089f); // scattered rows, only ~5% brighter

    // World seed — set by SimHost so the bright scan-line scatter is stable per
    // world (same seed → same rows light up). Defaults to the sim's default seed.
    public static int WorldSeed = 1337;
    private const uint ScanlineBrightPct = 22; // ~1 in 5 rows gets the bright color

    // Deterministic per-row brightness: hash the world seed with the row index
    // so the scatter is fixed for a given world but differs between worlds.
    private static bool ScanlineIsBright(int row)
    {
        uint h = (uint)(WorldSeed * 73856093) ^ (uint)((row + 1) * 19349663);
        h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
        return h % 100u < ScanlineBrightPct;
    }

    // Draw the VFD scan-line grid across a rect onto a canvas item: 1px lines
    // every `spacing` px, inset off the border, with a seeded scatter of
    // brighter rows. Shared by ScanlineStyleBox and any custom-drawn control.
    public static void DrawScanlines(Rid canvasItem, Rect2 rect, float spacing = 4f, float inset = 6f)
    {
        float x0 = rect.Position.X + inset, x1 = rect.Position.X + rect.Size.X - inset;
        if (x1 <= x0) return;
        float yTop = rect.Position.Y + inset, yBot = rect.Position.Y + rect.Size.Y - inset;
        int row = 0;
        for (float y = yTop; y < yBot; y += spacing, row++)
        {
            var c = ScanlineIsBright(row) ? ScanLineBright : ScanLine;
            RenderingServer.CanvasItemAddLine(canvasItem, new Vector2(x0, y), new Vector2(x1, y), c, 1f);
        }
    }

    // Wrap a flat glass box so it draws the scan-line grid on top. Use for any
    // pane/tile/card/button background that should match the clock face.
    public static ScanlineStyleBox Scan(StyleBoxFlat flat, float inset = 5f, float spacing = 4f)
        => new() { Flat = flat, Inset = inset, Spacing = spacing };

    // Buttons / tabs — a raised lighter indigo with a cyan edge so they pop
    // off the near-opaque panels instead of blending in.
    public static readonly Color Button = new(0.27f, 0.18f, 0.45f, 0.98f);
    public static readonly Color ButtonHover = new(0.36f, 0.25f, 0.58f, 0.98f);
    public static readonly Color ButtonActive = new(0.34f, 0.55f, 0.74f, 0.95f); // cyan-lit active tab
    public static readonly Color ButtonEdge = new(0.60f, 0.82f, 0.99f, 0.60f);   // bright cyan edge

    // A button/tab stylebox. Unselected uses the purple pane border; the
    // active tab gets the bright cyan edge so only it stands out. Carries the
    // scan-line grid so buttons match the panes.
    public static ScanlineStyleBox ButtonBox(Color bg, bool active = false, int corner = 6, int margin = 4)
    {
        var flat = Box(bg, active ? ButtonEdge : Border, 1, corner, margin, glow: false);
        var sb = Scan(flat, inset: 3f);
        sb.SetContentMarginAll(margin);
        return sb;
    }

    // An action button styled like the colonist-pane tabs (raised indigo with
    // the purple edge, lighter on hover, cyan when held). Caller wires Pressed.
    public static Button ActionButton(string text)
    {
        var b = new Godot.Button { Text = text, FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 30) };
        b.AddThemeStyleboxOverride("normal", ButtonBox(Button));
        b.AddThemeStyleboxOverride("hover", ButtonBox(ButtonHover));
        b.AddThemeStyleboxOverride("pressed", ButtonBox(ButtonActive, active: true));
        b.AddThemeStyleboxOverride("disabled", ButtonBox(new Color(Button.R, Button.G, Button.B, 0.40f)));
        b.AddThemeColorOverride("font_color", Text);
        b.AddThemeColorOverride("font_disabled_color", TextDim);
        return b;
    }

    // The shared red-box white-X close button (header corner of info panes).
    // Caller wires the Pressed handler.
    public static Button CloseButton()
    {
        var b = new Button { Text = "X", CustomMinimumSize = new Vector2(30, 28), FocusMode = Control.FocusModeEnum.None };
        var red = Box(new Color(0.80f, 0.20f, 0.20f, 0.98f), new Color(0.98f, 0.55f, 0.55f, 0.70f), 1, 6, 4, glow: false);
        var hov = Box(new Color(0.92f, 0.28f, 0.28f, 0.98f), new Color(1f, 0.70f, 0.70f, 0.85f), 1, 6, 4, glow: false);
        b.AddThemeStyleboxOverride("normal", red);
        b.AddThemeStyleboxOverride("pressed", red);
        b.AddThemeStyleboxOverride("hover", hov);
        b.AddThemeColorOverride("font_color", new Color(1f, 1f, 1f));
        return b;
    }

    // Glassy panel: translucent fill, soft glow halo, thin pastel border,
    // rounded corners, uniform content margin — overlaid with the clock's VFD
    // scan-line grid so every pane reads like the digital watch face.
    public static ScanlineStyleBox PanelBox(int corner = 12, int margin = 12)
    {
        var sb = Scan(Box(Panel, Border, 1, corner, margin, glow: true), inset: 6f);
        sb.SetContentMarginAll(margin);   // wrapper drives child layout, so mirror the inset
        return sb;
    }

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

    // Pulse a scan-line panel's glow (the pulse lives on its inner glass box).
    public static void AnimateGlow(ScanlineStyleBox box, double t) => AnimateGlow(box.Flat, t);

    // Pulse a panel box's glow (call each frame with accumulated time).
    public static void AnimateGlow(StyleBoxFlat box, double t)
    {
        float pulse = 0.5f + 0.5f * Mathf.Sin((float)t * 1.5f);
        box.ShadowSize = (int)Mathf.Lerp(7f, 18f, pulse);
        box.ShadowColor = new Color(Glow.R, Glow.G, Glow.B, Mathf.Lerp(0.12f, 0.32f, pulse));
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
