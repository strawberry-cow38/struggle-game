using System;
using Godot;

namespace StruggleGame.Game.UI;

// HUD watch readout with three looks:
//   Vfd   — cyan-green vacuum-fluorescent segments (old hi-fi / microwave
//           clock): teal phosphor glow on near-black, faint ghost segments.
//   Ember — Project-Zomboid-style chamfered 7-segment digits glowing like hot
//           coals (amber core + orange bloom).
//   Nixie — neon-orange numeral cathodes inside glass tubes with ghosted
//           digits behind and a wire anode mesh in front.
// All drawn in _Draw, no art assets. Blinking colon + small date line.
// Vfd/Ember share the 7-segment renderer; only the palette differs.
public partial class DigitalClock : Control
{
    public enum ClockStyle { Vfd, Ember, Nixie }

    private ClockStyle _style = ClockStyle.Vfd;
    public ClockStyle Style
    {
        get => _style;
        set { _style = value; UpdateMinSize(); QueueRedraw(); }
    }

    // ---- 7-segment geometry (Vfd + Ember) ----
    private const float DigitW = 22f;
    private const float DigitH = 42f;
    private const float Thick = 6f;
    private const float DigitGap = 8f;
    private const float ColonW = 16f;
    private const float PadX = 16f;
    private const float PadTop = 14f;
    private const float DateH = 22f;

    // ---- Nixie tube geometry ----
    private const float TubeW = 50f;
    private const float TubeH = 80f;
    private const float TubeGap = 6f;
    private const float NixColonW = 22f;
    private const int NixFont = 60;

    // Per-segment-style palette.
    private readonly struct SegPal
    {
        public readonly Color Core, Body, Bloom, Off, Bg, Edge, Date;
        public SegPal(Color core, Color body, Color bloom, Color off, Color bg, Color edge, Color date)
        { Core = core; Body = body; Bloom = bloom; Off = off; Bg = bg; Edge = edge; Date = date; }
    }

    // Purple VFD phosphor — matches the dreamcore colonist-panel UI.
    private static readonly SegPal VfdPal = new(
        core:  new Color(0.96f, 0.90f, 1.00f),         // lavender-white hot center
        body:  new Color(0.74f, 0.52f, 1.00f),         // violet phosphor
        bloom: new Color(0.55f, 0.28f, 0.96f, 0.24f),  // soft purple gas-discharge halo
        off:   new Color(0.30f, 0.20f, 0.44f, 0.45f),  // faint ghost segment
        bg:    new Color(0.11f, 0.04f, 0.19f, 0.94f),  // dreamcore deep purple (matches panels)
        edge:  new Color(0.42f, 0.26f, 0.58f, 0.60f),  // dreamcore border
        date:  new Color(0.82f, 0.64f, 1.00f, 0.92f));

    // VFD faceplate extras (purple).
    private static readonly Color VfdBacklight = new(0.45f, 0.20f, 0.78f, 0.10f); // even phosphor glow
    private static readonly Color VfdGrid = new(0.58f, 0.38f, 0.90f, 0.085f);     // control-grid wires

    // Warm amber ember.
    private static readonly SegPal EmberPal = new(
        core:  new Color(1.00f, 0.82f, 0.42f),
        body:  new Color(1.00f, 0.55f, 0.14f),
        bloom: new Color(1.00f, 0.38f, 0.08f, 0.30f),
        off:   new Color(0.28f, 0.07f, 0.02f, 0.55f),
        bg:    new Color(0.06f, 0.03f, 0.02f, 0.92f),
        edge:  new Color(0.55f, 0.22f, 0.06f, 0.55f),
        date:  new Color(1.00f, 0.50f, 0.16f, 0.92f));

    // Nixie palette.
    private static readonly Color NixCore = new(1.00f, 0.74f, 0.40f);
    private static readonly Color NixNeon = new(1.00f, 0.46f, 0.12f);
    private static readonly Color NixHalo = new(1.00f, 0.34f, 0.06f, 0.20f);
    private static readonly Color NixGhost = new(1.00f, 0.30f, 0.06f, 0.07f);
    private static readonly Color NixGlass = new(0.05f, 0.035f, 0.05f, 0.94f);
    private static readonly Color NixGlassTop = new(0.16f, 0.10f, 0.09f, 0.55f);
    private static readonly Color NixRim = new(0.55f, 0.40f, 0.30f, 0.55f);
    private static readonly Color NixMesh = new(1.00f, 0.55f, 0.20f, 0.10f);

    // Which of the 7 segments (a b c d e f g) light up per digit 0-9.
    private static readonly bool[][] Glyphs =
    {
        new[]{ true,  true,  true,  true,  true,  true,  false },
        new[]{ false, true,  true,  false, false, false, false },
        new[]{ true,  true,  false, true,  true,  false, true  },
        new[]{ true,  true,  true,  true,  false, false, true  },
        new[]{ false, true,  true,  false, false, true,  true  },
        new[]{ true,  false, true,  true,  false, true,  true  },
        new[]{ true,  false, true,  true,  true,  true,  true  },
        new[]{ true,  true,  true,  false, false, false, false },
        new[]{ true,  true,  true,  true,  true,  true,  true  },
        new[]{ true,  true,  true,  true,  false, true,  true  },
    };

    private int _h, _m, _s;
    private string _date = "";
    private double _time;
    private double _redrawAccum;
    private SegPal _p;     // active segment palette during a draw
    private bool _soft;    // VFD soft gas-discharge bloom + control grid
    private float _lw;     // logical (pre-scale) width during a draw

    // Overall display scale (everything drawn at design size then scaled).
    private const float Mag = 1.1f;

    public void SetTime(int hours, int minutes, int seconds, string date)
    {
        _h = hours; _m = minutes; _s = seconds; _date = date;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _time += delta;
        _redrawAccum += delta;
        if (_redrawAccum >= 1.0 / 20.0) { _redrawAccum = 0; QueueRedraw(); }
    }

    public override void _Ready() => UpdateMinSize();

    // Design-space (pre-scale) size for the active style.
    private Vector2 BaseSize()
        => _style == ClockStyle.Nixie
            ? new Vector2(PadX * 2 + TubeW * 4 + NixColonW + TubeGap * 4, PadTop * 2 + TubeH + DateH)
            : new Vector2(PadX * 2 + DigitW * 4 + ColonW + DigitGap * 4, PadTop * 2 + DigitH + DateH);

    private void UpdateMinSize() => CustomMinimumSize = BaseSize() * Mag;

    public override void _Draw()
    {
        // Draw at design size, scaled up uniformly.
        DrawSetTransform(Vector2.Zero, 0f, new Vector2(Mag, Mag));
        _lw = BaseSize().X;
        switch (_style)
        {
            case ClockStyle.Nixie: DrawNixie(); break;
            case ClockStyle.Ember: _soft = false; DrawSevenSeg(EmberPal); break;
            default:               _soft = true;  DrawSevenSeg(VfdPal); break;
        }
    }

    private float Flick() => 0.86f + 0.14f * Flicker((float)_time);

    // Date readout, themed to match the clock: a divider strip, then the
    // date glowing in the same phosphor as the digits (uppercased for a
    // display feel).
    private void DrawDateLine(float panelH, Color core)
    {
        var font = UiTheme.Font;
        if (font is null || _date.Length == 0) return;
        string s = _date.ToUpperInvariant();
        int fs = 14;
        float w = _lw;
        float tw = font.GetStringSize(s, HorizontalAlignment.Left, -1, fs).X;
        var pos = new Vector2((w - tw) * 0.5f, panelH - 6);

        // Divider between the time row and the date row.
        float dy = panelH - DateH;
        DrawLine(new Vector2(12, dy), new Vector2(w - 12, dy), new Color(core.R, core.G, core.B, 0.22f), 1f);

        // Plain white date text — no phosphor glow, for a clean readable line.
        DrawString(font, pos, s, HorizontalAlignment.Left, -1, fs, new Color(0.97f, 0.97f, 1f));
    }

    // ------------------------------------------------------- 7-segment (Vfd/Ember)
    private void DrawSevenSeg(SegPal pal)
    {
        _p = pal;
        float w = _lw, lcdH = PadTop * 2 + DigitH + DateH;
        var screen = new Rect2(0, 0, w, lcdH);
        DrawRect(screen, pal.Bg, true);

        // VFD: even phosphor backlight glow behind the digits (a few stacked
        // translucent bands brightest across the digit row).
        if (_soft)
        {
            float cy = PadTop + DigitH * 0.5f;
            for (int i = 3; i >= 1; i--)
                DrawRect(new Rect2(4, cy - i * 14, w - 8, i * 28), VfdBacklight, true);
        }

        float flick = Flick();
        float x = PadX, y = PadTop;
        DrawDigit(_h / 10, x, y, flick); x += DigitW + DigitGap;
        DrawDigit(_h % 10, x, y, flick); x += DigitW + DigitGap;
        DrawColon(x, y, flick);          x += ColonW + DigitGap;
        DrawDigit(_m / 10, x, y, flick); x += DigitW + DigitGap;
        DrawDigit(_m % 10, x, y, flick);

        // VFD: fine horizontal control-grid wires across the whole face.
        if (_soft)
            for (float gy = PadTop - 2; gy < lcdH - 4; gy += 4f)
                DrawLine(new Vector2(6, gy), new Vector2(w - 6, gy), VfdGrid, 1f);

        DrawRect(screen, pal.Edge, false, 2f);
        DrawDateLine(lcdH, pal.Date);
    }

    private void DrawColon(float x, float y, float flick)
    {
        bool on = (_s & 1) == 0;
        float cx = x + ColonW * 0.5f;
        float r = Thick * 0.55f;
        DrawDot(new Vector2(cx, y + DigitH * 0.30f), r, on, flick);
        DrawDot(new Vector2(cx, y + DigitH * 0.70f), r, on, flick);
    }

    private void DrawDot(Vector2 c, float r, bool on, float flick)
    {
        if (on)
        {
            DrawCircle(c, r * 2.1f, _p.Bloom);
            DrawCircle(c, r, _p.Body * flick);
            DrawCircle(c, r * 0.5f, _p.Core * flick);
        }
        else DrawCircle(c, r, _p.Off);
    }

    private void DrawDigit(int d, float ox, float oy, float flick)
    {
        if (d < 0 || d > 9) d = 0;
        var on = Glyphs[d];
        float midX = ox + DigitW * 0.5f;
        float qtr = DigitH * 0.25f;
        DrawSeg(HSeg(midX, oy, DigitW), on[0], flick);
        DrawSeg(HSeg(midX, oy + DigitH * 0.5f, DigitW), on[6], flick);
        DrawSeg(HSeg(midX, oy + DigitH, DigitW), on[3], flick);
        DrawSeg(VSeg(ox, oy + qtr, DigitH * 0.5f), on[5], flick);
        DrawSeg(VSeg(ox, oy + qtr * 3, DigitH * 0.5f), on[4], flick);
        DrawSeg(VSeg(ox + DigitW, oy + qtr, DigitH * 0.5f), on[1], flick);
        DrawSeg(VSeg(ox + DigitW, oy + qtr * 3, DigitH * 0.5f), on[2], flick);
    }

    private void DrawSeg(Vector2[] core, bool on, float flick)
    {
        if (on)
        {
            if (_soft)
            {
                // Soft gas-discharge haze: extra wide low-alpha layers so lit
                // segments bloom into each other like a real VFD.
                DrawColoredPolygon(Expand(core, 7f), _p.Bloom * 0.5f);
                DrawColoredPolygon(Expand(core, 4.5f), _p.Bloom);
            }
            DrawColoredPolygon(Expand(core, 2.6f), _p.Bloom);
            DrawColoredPolygon(core, _p.Body * flick);
            DrawColoredPolygon(Expand(core, -1.4f), _p.Core * flick);
        }
        else DrawColoredPolygon(core, _p.Off);
    }

    private static Vector2[] HSeg(float cx, float cy, float len)
    {
        float h = Thick * 0.5f, l = len * 0.5f;
        return new[]
        {
            new Vector2(cx - l, cy),
            new Vector2(cx - l + h, cy - h),
            new Vector2(cx + l - h, cy - h),
            new Vector2(cx + l, cy),
            new Vector2(cx + l - h, cy + h),
            new Vector2(cx - l + h, cy + h),
        };
    }

    private static Vector2[] VSeg(float cx, float cy, float len)
    {
        float h = Thick * 0.5f, l = len * 0.5f;
        return new[]
        {
            new Vector2(cx, cy - l),
            new Vector2(cx + h, cy - l + h),
            new Vector2(cx + h, cy + l - h),
            new Vector2(cx, cy + l),
            new Vector2(cx - h, cy + l - h),
            new Vector2(cx - h, cy - l + h),
        };
    }

    private static Vector2[] Expand(Vector2[] pts, float amt)
    {
        var c = Vector2.Zero;
        foreach (var p in pts) c += p;
        c /= pts.Length;
        var outp = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++)
            outp[i] = pts[i] + (pts[i] - c).Normalized() * amt;
        return outp;
    }

    private static float Flicker(float t)
    {
        float n = Mathf.Sin(t * 27.3f) * 0.5f + Mathf.Sin(t * 11.1f + 1.7f) * 0.3f + Mathf.Sin(t * 53.7f) * 0.2f;
        return Mathf.Clamp(0.5f + 0.5f * n, 0f, 1f);
    }

    // ----------------------------------------------------------------- Nixie
    private void DrawNixie()
    {
        float panelH = PadTop * 2 + TubeH + DateH;
        float flick = Flick();

        float x = PadX, y = PadTop;
        DrawTube(_h / 10, x, y, flick); x += TubeW + TubeGap;
        DrawTube(_h % 10, x, y, flick); x += TubeW + TubeGap;
        DrawNixColon(x, y, flick);      x += NixColonW + TubeGap;
        DrawTube(_m / 10, x, y, flick); x += TubeW + TubeGap;
        DrawTube(_m % 10, x, y, flick);

        DrawDateLine(panelH, NixNeon);
    }

    private void DrawTube(int d, float ox, float oy, float flick)
    {
        if (d < 0 || d > 9) d = 0;
        var rect = new Rect2(ox, oy, TubeW, TubeH);
        DrawRect(rect, NixGlass, true);
        DrawRect(new Rect2(ox, oy, TubeW, TubeH * 0.32f), NixGlassTop, true);
        DrawRect(rect, NixRim, false, 1.5f);

        var center = new Vector2(ox + TubeW * 0.5f, oy + TubeH * 0.5f);
        DrawGlyph((d + 4) % 10, center, NixGhost, NixFont, 1f);
        DrawGlyph((d + 7) % 10, center, NixGhost, NixFont, 1f);
        DrawGlyphGlow(d, center, flick);
        DrawMesh(rect);
    }

    private void DrawGlyphGlow(int d, Vector2 center, float flick)
    {
        var halo = NixHalo * flick;
        for (int ring = 0; ring < 2; ring++)
        {
            float spread = ring == 0 ? 5.5f : 2.8f;
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.Pi / 4f;
                var off = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * spread;
                DrawGlyph(d, center + off, halo, NixFont, 1f);
            }
        }
        DrawGlyph(d, center, NixNeon * flick, NixFont, 1f);
        DrawGlyph(d, center, NixCore * flick, NixFont, 0.78f);
    }

    private void DrawGlyph(int d, Vector2 center, Color col, int size, float scale)
    {
        var font = UiTheme.Font;
        if (font is null) return;
        int fs = (int)(size * scale);
        string s = d.ToString();
        var sz = font.GetStringSize(s, HorizontalAlignment.Left, -1, fs);
        float asc = font.GetAscent(fs), desc = font.GetDescent(fs);
        var pos = new Vector2(center.X - sz.X * 0.5f, center.Y + (asc - desc) * 0.5f);
        DrawString(font, pos, s, HorizontalAlignment.Left, -1, fs, col);
    }

    private void DrawMesh(Rect2 r)
    {
        const float step = 9f;
        for (float o = -r.Size.Y; o < r.Size.X; o += step)
        {
            DrawLine(ClampX(new Vector2(r.Position.X + o, r.Position.Y), r),
                     ClampX(new Vector2(r.Position.X + o + r.Size.Y, r.Position.Y + r.Size.Y), r), NixMesh, 1f);
            DrawLine(ClampX(new Vector2(r.Position.X + o + r.Size.Y, r.Position.Y), r),
                     ClampX(new Vector2(r.Position.X + o, r.Position.Y + r.Size.Y), r), NixMesh, 1f);
        }
    }

    private static Vector2 ClampX(Vector2 p, Rect2 r)
        => new(Mathf.Clamp(p.X, r.Position.X, r.Position.X + r.Size.X), p.Y);

    private void DrawNixColon(float x, float y, float flick)
    {
        float cx = x + NixColonW * 0.5f;
        bool on = (_s & 1) == 0;
        float r = 4.5f;
        DrawNeonDot(new Vector2(cx, y + TubeH * 0.34f), r, on, flick);
        DrawNeonDot(new Vector2(cx, y + TubeH * 0.66f), r, on, flick);
    }

    private void DrawNeonDot(Vector2 c, float r, bool on, float flick)
    {
        if (!on) { DrawCircle(c, r, NixGhost); return; }
        DrawCircle(c, r * 2.4f, NixHalo * flick);
        DrawCircle(c, r, NixNeon * flick);
        DrawCircle(c, r * 0.5f, NixCore * flick);
    }
}
