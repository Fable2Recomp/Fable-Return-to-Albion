shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_never;

// A rounded translucent panel fitted behind HUD text — the remaster's stand-in for the shipped
// black-tinted slabs (`Material655`, diffuse 0/0/0, TintingAmount 1) the TopBox and the counters
// author. Size is in canvas units; the quad's UV runs 0..1 across it.
uniform vec2 size = vec2(100.0, 40.0);
uniform float radius = 12.0;
uniform vec4 fill : source_color = vec4(0.04, 0.03, 0.02, 0.62);
uniform vec4 border : source_color = vec4(0.0, 0.0, 0.0, 0.0);
uniform float border_width = 0.0;
uniform float opacity = 1.0;

void fragment() {
	vec2 p = (UV - vec2(0.5)) * size;
	vec2 half = size * 0.5 - vec2(radius);
	vec2 q = abs(p) - half;
	float d = length(max(q, vec2(0.0))) + min(max(q.x, q.y), 0.0) - radius;

	float coverage = 1.0 - smoothstep(-0.75, 0.75, d);
	// No ring by default: a plain translucent panel behind the text (the border uniform stays
	// for a themed variant, off at alpha 0).
	float ring = border_width > 0.0
		? 1.0 - smoothstep(border_width - 0.75, border_width + 0.75, abs(d + border_width * 0.5))
		: 0.0;
	vec4 colour = mix(fill, border, ring * border.a);
	ALBEDO = colour.rgb;
	ALPHA = colour.a * coverage * opacity;
}
