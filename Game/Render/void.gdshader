shader_type canvas_item;

// Trippy animated checkerboard for the empty space around the map.
// Two colour waves cycle out of phase; the checkerboard alternates
// between them and a subtle scroll keeps it from looking static.

uniform float cell_size = 48.0;
uniform float color_speed = 0.6;
uniform float scroll_speed = 8.0;
uniform float brightness = 0.45;

void fragment() {
    vec2 px = FRAGCOORD.xy + vec2(TIME * scroll_speed, TIME * scroll_speed * 0.7);
    vec2 cell = floor(px / cell_size);
    float check = mod(cell.x + cell.y, 2.0);

    float t = TIME * color_speed;
    vec3 a = 0.5 + 0.5 * cos(t + cell.xyx * 0.08 + vec3(0.0, 2.094, 4.188));
    vec3 b = 0.5 + 0.5 * cos(t * 1.3 + cell.yxy * 0.11 + vec3(1.5, 3.5, 5.5));

    vec3 col = mix(a, b, check) * brightness;
    COLOR = vec4(col, 1.0);
}
