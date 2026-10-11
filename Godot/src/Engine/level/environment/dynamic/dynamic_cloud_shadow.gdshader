shader_type spatial;
render_mode unshaded, cull_disabled;

uniform sampler2D cloud_shadow_map : filter_linear, repeat_disable;
uniform vec2 shadow_map_center = vec2(0.0);
uniform float shadow_map_extent = 4000.0;
varying vec3 cloud_position;

void vertex() {
    cloud_position = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
    vec2 uv = (cloud_position.xz - shadow_map_center) / shadow_map_extent + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) { discard; }
    ALPHA = texture(cloud_shadow_map, uv).r;
    ALPHA_SCISSOR_THRESHOLD = 0.4;
}
