using Godot;
using StruggleGame.Sim;

namespace StruggleGame.Game.Designation;

// Tile-count readout for drag-to-place tools. Designators call
// Draw(this, xmin, ymin, xmax, ymax) from their _Draw() and the helper
// renders an "X" tile count centered above the top edge and a "Y" tile
// count centered against the left edge of the rect, drawn in world
// space so it scales with the camera like the rest of the preview.
public static class DragMeasureOverlay
{
    private const int PixelsPerTile = SimConstants.PixelsPerTile;
    private const int FontSize = 28;
    private static readonly Color TextColor = Colors.White;
    private static readonly Color ShadowColor = new(0f, 0f, 0f, 0.85f);

    public static void Draw(Node2D node, int xmin, int ymin, int xmax, int ymax)
    {
        var font = ThemeDB.FallbackFont;
        if (font is null) return;

        int w = xmax - xmin + 1;
        int h = ymax - ymin + 1;

        string wTxt = w.ToString();
        string hTxt = h.ToString();

        var wSize = font.GetStringSize(wTxt, HorizontalAlignment.Left, -1f, FontSize);
        var hSize = font.GetStringSize(hTxt, HorizontalAlignment.Left, -1f, FontSize);
        float ascent = font.GetAscent(FontSize);

        float topCenterX = (xmin + w * 0.5f) * PixelsPerTile;
        var wPos = new Vector2(topCenterX - wSize.X * 0.5f, ymin * PixelsPerTile - 4f);

        float leftMidY = (ymin + h * 0.5f) * PixelsPerTile;
        var hPos = new Vector2(xmin * PixelsPerTile - 6f - hSize.X, leftMidY + ascent * 0.5f - 2f);

        node.DrawString(font, wPos + new Vector2(1, 1), wTxt, HorizontalAlignment.Left, -1f, FontSize, ShadowColor);
        node.DrawString(font, wPos, wTxt, HorizontalAlignment.Left, -1f, FontSize, TextColor);
        node.DrawString(font, hPos + new Vector2(1, 1), hTxt, HorizontalAlignment.Left, -1f, FontSize, ShadowColor);
        node.DrawString(font, hPos, hTxt, HorizontalAlignment.Left, -1f, FontSize, TextColor);
    }
}
