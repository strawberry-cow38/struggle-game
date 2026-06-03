using Godot;

namespace StruggleGame.Game.UI;

// A reusable "HP  ▆▆▆▆░░  240 / 300" row for the selection info panels.
// Fill color goes red → amber → green with the remaining fraction.
public partial class HpBar : HBoxContainer
{
    private ProgressBar _bar = null!;
    private Label _val = null!;

    public override void _Ready()
    {
        MouseFilter = Control.MouseFilterEnum.Pass;
        AddThemeConstantOverride("separation", 8);

        var name = new Label { Text = "HP", CustomMinimumSize = new Vector2(40, 0), VerticalAlignment = VerticalAlignment.Center };
        name.AddThemeFontSizeOverride("font_size", 13);
        AddChild(name);

        _bar = new ProgressBar
        {
            MinValue = 0, MaxValue = 1, Step = 0.0001, ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 16),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        _bar.AddThemeStyleboxOverride("background", UiTheme.InsetBox(UiTheme.Inset, corner: 4));
        AddChild(_bar);

        _val = new Label { Text = "", CustomMinimumSize = new Vector2(86, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _val.AddThemeFontSizeOverride("font_size", 13);
        AddChild(_val);
    }

    public void Set(float cur, float max)
    {
        float r = max > 0f ? Mathf.Clamp(cur / max, 0f, 1f) : 1f;
        _bar.Value = r;
        _bar.AddThemeStyleboxOverride("fill", Fill(BarColor(r)));
        _val.Text = $"{cur:0} / {max:0}";
    }

    private static Color BarColor(float r)
        => r > 0.66f ? new Color(0.38f, 0.80f, 0.40f)
         : r > 0.33f ? new Color(0.90f, 0.76f, 0.24f)
         : new Color(0.86f, 0.30f, 0.26f);

    private static StyleBoxFlat Fill(Color c)
    {
        var b = new StyleBoxFlat { BgColor = c };
        b.CornerRadiusTopLeft = b.CornerRadiusTopRight = b.CornerRadiusBottomLeft = b.CornerRadiusBottomRight = 4;
        return b;
    }
}
