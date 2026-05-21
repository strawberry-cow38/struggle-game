shader_type canvas_item;

// Trippy animated grayscale checkerboard for the empty space around the
// map. Two phase-offset brightness waves alternate per checkerboard cell;
// a slow scroll keeps the grid from feeling static.

uniform float cell_size = 48.0;
uniform float color_speed = 0.6;
uniform float scroll_speed = 8.0;
uniform float brightness = 0.45;

void fragment() {
    vec2 px = FRAGCOORD.xy + vec2(TIME * scroll_speed, TIME * scroll_speed * 0.7);
    vec2 cell = floor(px / cell_size);
    float check = mod(cell.x + cell.y, 2.0);

    float t = TIME * color_speed;
    float a = 0.5 + 0.5 * cos(t + (cell.x + cell.y) * 0.08);
    float b = 0.5 + 0.5 * cos(t * 1.3 + (cell.x - cell.y) * 0.11 + 1.5);

    float v = mix(a, b, check) * brightness;
    COLOR = vec4(v, v, v, 1.0);
}
