shader_type canvas_item;

// Fullscreen procedural rain overlay — zero per-drop objects. Three
// parallax layers of hashed streaks, each anchored to WORLD pixels (the
// node passes the camera center + zoom every frame), so the rain field
// stays glued to the ground while the camera pans and zooms.
//
// Per layer: world space is sheared so streaks run along the wind
// vector, then scrolled along y by the fall speed. Each cell of a tall
// thin grid owns at most one streak (hash gates existence → density
// scales with intensity; hash also jitters x/phase so no tiling reads).
//
// Scroll phase is INTEGRATED on the CPU (RainOverlay accumulates
// fall*dt per layer) instead of TIME * speed: speed varies with wind +
// intensity every frame, and multiplying a varying speed by total TIME
// teleports the whole field on every change.

uniform float intensity : hint_range(0.0, 1.0) = 0.0; // 0 = invisible
uniform vec3 scroll = vec3(0.0);           // per-layer fall phase, layer-uv units
uniform vec3 slant = vec3(0.0);            // per-layer x-shear (windX / fall)
uniform vec2 camera_center = vec2(0.0);    // world px at the screen center
uniform float camera_zoom = 1.0;           // screen px per world px
uniform vec2 viewport_size = vec2(1920.0, 1080.0);
uniform float pixels_per_tile = 64.0;
uniform vec3 rain_color : source_color = vec3(0.72, 0.82, 0.96);

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 443.8975);
	p3 += dot(p3, p3.yzx + 19.19);
	return fract((p3.x + p3.y) * p3.z);
}

// One layer of streaks. uv in world tiles; scr = accumulated fall phase,
// shear = x slant; cols = streak columns per tile, len = streak cell
// height in tiles.
float rain_layer(vec2 uv, float scr, float shear, float cols, float len, float density) {
	// Shear so a streak's long axis runs along the velocity (wind, fall),
	// then scroll: features march down (and sideways via the shear).
	vec2 p = vec2(uv.x - uv.y * shear, uv.y - scr);
	vec2 cuv = vec2(p.x * cols, p.y / len);
	vec2 id = floor(cuv);
	float rnd = hash12(id);
	if (rnd > density) {
		return 0.0;
	}
	vec2 f = fract(cuv);
	// Random x inside the column + soft edges. fwidth keeps the core
	// from aliasing away when zoomed far out (streak thinner than 1px).
	float cx = 0.2 + 0.6 * fract(rnd * 13.7);
	float w = 0.09 + fwidth(cuv.x);
	float core = 1.0 - smoothstep(0.0, w, abs(f.x - cx));
	// Streak segment: random phase, ~70% of the cell tall, faded head/tail.
	float phase = fract(rnd * 7.31);
	float fy = fract(f.y - phase);
	float seg = smoothstep(0.0, 0.2, fy) * smoothstep(0.7, 0.45, fy);
	return core * seg;
}

void fragment() {
	if (intensity <= 0.001) {
		COLOR = vec4(0.0);
	} else {
		// Screen pixel → world tile (overlay lives on a CanvasLayer, so
		// FRAGCOORD is screen space and the camera transform is ours to apply).
		vec2 world_px = camera_center + (FRAGCOORD.xy - viewport_size * 0.5) / camera_zoom;
		vec2 uv = world_px / pixels_per_tile;

		// Near → far: bigger/faster/brighter to smaller/slower/dimmer.
		float a = 0.0;
		a += 0.60 * rain_layer(uv, scroll.x, slant.x, 6.0, 2.2, 0.30 * intensity + 0.05);
		a += 0.45 * rain_layer(uv * 1.7 + 31.4, scroll.y, slant.y, 6.0, 1.5, 0.45 * intensity + 0.05);
		a += 0.32 * rain_layer(uv * 2.6 + 77.7, scroll.z, slant.z, 6.0, 1.0, 0.60 * intensity + 0.05);

		// Faint atmospheric haze so storms read grey even between streaks.
		float haze = 0.06 * intensity;
		float alpha = clamp(a * (0.35 + 0.65 * intensity) + haze, 0.0, 0.6);
		COLOR = vec4(rain_color, alpha);
	}
}
