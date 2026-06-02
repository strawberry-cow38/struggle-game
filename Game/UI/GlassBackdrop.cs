using Godot;

namespace StruggleGame.Game.UI;

// Frosted-glass backing for a panel: a rounded rect that samples the screen
// behind it and blurs it, so the world reads as soft frosted glass through the
// translucent panel drawn on top. Follows a target Control's rect each frame.
// Place it just before the panel in the same CanvasLayer so it draws underneath.
public partial class GlassBackdrop : ColorRect
{
    public Control? Target;
    public float Corner = 12f;

    private const string BlurShader = @"
shader_type canvas_item;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
uniform vec2 size_px = vec2(100.0, 100.0);
uniform float corner = 12.0;
uniform float blur = 6.0;
void fragment() {
    vec2 p = UV * size_px;
    vec2 h = size_px * 0.5;
    vec2 q = abs(p - h) - (h - vec2(corner));
    float d = length(max(q, vec2(0.0))) - corner;
    if (d > 0.0) { discard; }
    vec2 ps = SCREEN_PIXEL_SIZE * blur;
    vec3 c = vec3(0.0);
    c += texture(screen_tex, SCREEN_UV + ps * vec2(-1.0,-1.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 0.0,-1.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 1.0,-1.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2(-1.0, 0.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 0.0, 0.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 1.0, 0.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2(-1.0, 1.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 0.0, 1.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 1.0, 1.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2(-2.0, 0.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 2.0, 0.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 0.0,-2.0)).rgb;
    c += texture(screen_tex, SCREEN_UV + ps * vec2( 0.0, 2.0)).rgb;
    c /= 13.0;
    COLOR = vec4(c, 1.0);
}";

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Color = new Color(1, 1, 1, 1);
        Material = new ShaderMaterial { Shader = new Shader { Code = BlurShader } };
        Visible = false;
    }

    public override void _Process(double delta)
    {
        if (Target is null) { Visible = false; return; }
        Visible = Target.Visible;
        if (!Visible) return;
        Position = Target.Position;
        Size = Target.Size;
        if (Material is ShaderMaterial m)
        {
            m.SetShaderParameter("size_px", Size);
            m.SetShaderParameter("corner", Corner);
        }
    }
}
