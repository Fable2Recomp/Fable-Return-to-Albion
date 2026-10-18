shader_type canvas_item;
render_mode unshaded, blend_premul_alpha;

uniform float erosion = 0.0;
varying vec2 footprint;
varying vec4 track;

void vertex() {
    footprint = VERTEX * 0.64;
    track = INSTANCE_CUSTOM;
}

void fragment() {
    float toe = length(vec2(footprint.x / 0.105, (footprint.y - 0.055) / 0.15));
    float heel = length(vec2(footprint.x / 0.075, (footprint.y + 0.12) / 0.085));
    float coverage = clamp((1.15 - min(toe, heel)) / 0.32, 0.0, 1.0) * clamp(track.x - erosion, 0.0, 1.0);
    if (coverage < 0.001) discard;
    float height = track.y + dot(track.zw, footprint);
    COLOR = vec4(coverage, height * coverage, 0.0, coverage);
}
