using System;
using Godot;

namespace StruggleGame.Game.UI;

// A Project-Zomboid-style digital watch readout: chamfered 7-segment digits
// glowing like hot embers (amber core + orange bloom + a faint always-on
// "off" segment), a blinking colon, and a small date line. Drawn entirely in
// _Draw — no art assets. Sits on its own dark "LCD" screen so it reads like a
// wrist watch instead of plain HUD text.
public partial class DigitalClock : Control
{
    // Segment geometry (per digit cell).
    private const float DigitW = 22f;
    private const float DigitH = 42f;
    private const float Thick = 6f;
    private const float DigitGap = 8f;
    private const float ColonW = 16f;
    private const float PadX = 16f;
    private const float PadTop = 14f;
    private const float DateH = 22f;

    // Fire palette.
    private static readonly Color CoreHot = new(1.00f, 0.82f, 0.42f);   // bright amber-white
    private static readonly Color CoreAmber = new(1.00f, 0.55f, 0.14f); // ember orange
    private static readonly Color Bloom = new(1.00f, 0.38f, 0.08f, 0.30f);
    private static readonly Color Off = new(0.28f, 0.07f, 0.02f, 0.55f); // faint unlit segment
    private static readonly Color Lcd = new(0.06f, 0.03f, 0.02f, 0.92f);  // screen background
    private static readonly Color LcdEdge = new(0.55f, 0.22f, 0.06f, 0.55f);
    private static readonly Color DateCol = new(1.00f, 0.50f, 0.16f, 0.92f);

    // Which of the 7 segments (a b c d e f g) light up per digit 0-9.
    // a=top, b=top-right, c=bottom-right, d=bottom, e=bottom-left, f=top-left, g=middle
    private static readonly bool[][] Glyphs =
    {
        new[]{ true,  true,  true,  true,  true,  true,  false }, // 0
        new[]{ false, true,  true,  false, false, false, false }, // 1
        new[]{ true,  true,  false, true,  true,  false, true  }, // 2
        new[]{ true,  true,  true,  true,  false, false, true  }, // 3
        new[]{ false, true,  true,  false, false, true,  true  }, // 4
        new[]{ true,  false, true,  true,  false, true,  true  }, // 5
        new[]{ true,  false, true,  true,  true,  true,  true  }, // 6
        new[]{ true,  true,  true,  false, false, false, false }, // 7
        new[]{ true,  true,  true,  true,  true,  true,  true  }, // 8
        new[]{ true,  true,  true,  true,  false, true,  true  }, // 9
    };

    private int _h, _m, _s;
    private string _date = "";
    private double _time; // drives flicker + colon blink

    public void SetTime(int hours, int minutes, int seconds, string date)
    {
        _h = hours; _m = minutes; _s = seconds; _date = date;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _time += delta;
        QueueRedraw(); // cheap: only the small watch face redraws, for flicker
    }

    public override void _Ready()
    {
        // Layout: d d : d d  -> 5 elements, 4 gaps.
        float w = PadX * 2 + DigitW * 4 + ColonW + DigitGap * 4;
        float h = PadTop * 2 + DigitH + DateH;
        CustomMinimumSize = new Vector2(w, h);
    }

    public override void _Draw()
    {
        float w = Size.X, lcdH = PadTop * 2 + DigitH + DateH;

        // The LCD screen panel.
        var screen = new Rect2(0, 0, w, lcdH);
        DrawRect(screen, Lcd, true);
        DrawRect(screen, LcdEdge, false, 2f); // ember edge

        // Subtle global ember flicker — deterministic, watch-like jitter.
        float flick = 0.86f + 0.14f * Flicker((float)_time);

        float x = PadX;
        float y = PadTop;
        DrawDigit(_h / 10, x, y, flick); x += DigitW + DigitGap;
        DrawDigit(_h % 10, x, y, flick); x += DigitW + DigitGap;
        DrawColon(x, y, flick);          x += ColonW + DigitGap;
        DrawDigit(_m / 10, x, y, flick); x += DigitW + DigitGap;
        DrawDigit(_m % 10, x, y, flick);

        // Date line, centered under the digits.
        var font = UiTheme.Font;
        if (font is not null && _date.Length > 0)
        {
            int fs = 14;
            float tw = font.GetStringSize(_date, HorizontalAlignment.Left, -1, fs).X;
            var pos = new Vector2((w - tw) * 0.5f, PadTop + DigitH + DateH - 4);
            DrawString(font, pos, _date, HorizontalAlignment.Left, -1, fs, DateCol);
        }
    }

    // Pseudo-noise flicker in [0,1] from layered sines (no RNG, frame-stable).
    private static float Flicker(float t)
    {
        float n = Mathf.Sin(t * 27.3f) * 0.5f + Mathf.Sin(t * 11.1f + 1.7f) * 0.3f + Mathf.Sin(t * 53.7f) * 0.2f;
        return Mathf.Clamp(0.5f + 0.5f * n, 0f, 1f);
    }

    private void DrawColon(float x, float y, float flick)
    {
        // Blink off on even seconds for that ticking-watch feel.
        bool on = (_s & 1) == 0;
        float cx = x + ColonW * 0.5f;
        float r = Thick * 0.55f;
        float y1 = y + DigitH * 0.30f;
        float y2 = y + DigitH * 0.70f;
        DrawDot(new Vector2(cx, y1), r, on, flick);
        DrawDot(new Vector2(cx, y2), r, on, flick);
    }

    private void DrawDot(Vector2 c, float r, bool on, float flick)
    {
        if (on)
        {
            DrawCircle(c, r * 2.1f, Bloom);
            DrawCircle(c, r, CoreAmber * flick);
            DrawCircle(c, r * 0.5f, CoreHot * flick);
        }
        else
        {
            DrawCircle(c, r, Off);
        }
    }

    private void DrawDigit(int d, float ox, float oy, float flick)
    {
        if (d < 0 || d > 9) d = 0;
        var on = Glyphs[d];
        float midX = ox + DigitW * 0.5f;
        float qtr = DigitH * 0.25f;

        // a top, d bottom, g middle (horizontal)
        DrawSeg(HSeg(midX, oy, DigitW), on[0], flick);
        DrawSeg(HSeg(midX, oy + DigitH * 0.5f, DigitW), on[6], flick);
        DrawSeg(HSeg(midX, oy + DigitH, DigitW), on[3], flick);
        // f top-left, e bottom-left, b top-right, c bottom-right (vertical)
        DrawSeg(VSeg(ox, oy + qtr, DigitH * 0.5f), on[5], flick);
        DrawSeg(VSeg(ox, oy + qtr * 3, DigitH * 0.5f), on[4], flick);
        DrawSeg(VSeg(ox + DigitW, oy + qtr, DigitH * 0.5f), on[1], flick);
        DrawSeg(VSeg(ox + DigitW, oy + qtr * 3, DigitH * 0.5f), on[2], flick);
    }

    private void DrawSeg(Vector2[] core, bool on, float flick)
    {
        if (on)
        {
            // Bloom halo: same shape expanded around its centroid.
            DrawColoredPolygon(Expand(core, 2.6f), Bloom);
            DrawColoredPolygon(core, CoreAmber * flick);
            // Hot inner streak.
            DrawColoredPolygon(Expand(core, -1.4f), CoreHot * flick);
        }
        else
        {
            DrawColoredPolygon(core, Off);
        }
    }

    // Chamfered horizontal segment (flat hexagon) centered at (cx,cy).
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

    // Chamfered vertical segment centered at (cx,cy).
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

    // Expand/shrink a polygon by `amt` px along the centroid->vertex direction.
    private static Vector2[] Expand(Vector2[] pts, float amt)
    {
        var c = Vector2.Zero;
        foreach (var p in pts) c += p;
        c /= pts.Length;
        var outp = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++)
        {
            var dir = (pts[i] - c).Normalized();
            outp[i] = pts[i] + dir * amt;
        }
        return outp;
    }
}
