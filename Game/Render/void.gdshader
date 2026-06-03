shader_type canvas_item;

// Purple checkerboard for the empty space around the map (matches the UI).
// Each cell pulses in brightness on its own phase + a slow diagonal scroll
// keeps the grid alive.

uniform vec3 color_a = vec3(0.22, 0.10, 0.34); // deep purple
uniform vec3 color_b = vec3(0.42, 0.21, 0.58); // lighter purple
uniform float cell_size = 48.0;
uniform float pulse_speed = 1.2;
uniform float scroll_speed = 8.0;
uniform float min_brightness = 0.55;
uniform float pulse_amount = 0.45;

void fragment() {
    vec2 px = FRAGCOORD.xy + vec2(TIME * scroll_speed, TIME * scroll_speed * 0.7);
    vec2 cell = floor(px / cell_size);
    float check = mod(cell.x + cell.y, 2.0);

    float phase = (cell.x * 0.13 + cell.y * 0.17);
    float pulse = min_brightness + pulse_amount * (0.5 + 0.5 * sin(TIME * pulse_speed + phase));

    vec3 col = mix(color_a, color_b, check) * pulse;
    COLOR = vec4(col, 1.0);
}
