shader_type canvas_item;
render_mode unshaded;

uniform sampler2D screen_texture : hint_screen_texture, repeat_disable, filter_linear;
uniform int preset = 0;
uniform float exposure = 0.0;
uniform float contrast = 1.0;
uniform float saturation = 1.0;
uniform float vignette = 0.0;

void fragment() {
    vec3 c = textureLod(screen_texture, SCREEN_UV, 0.0).rgb * exp2(exposure);
    float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
    if (preset == 1) {
        c = mix(vec3(luma), c, 1.3) * 1.05;
    } else if (preset == 2) {
        c *= vec3(1.12, 1.02, 0.86);
    } else if (preset == 3) {
        c *= vec3(0.87, 1.02, 1.15);
    } else if (preset == 4) {
        c = vec3(luma) * vec3(1.15, 0.96, 0.72);
    } else if (preset == 5) {
        c = vec3(luma);
    } else if (preset == 6) {
        c = vec3((luma - 0.18) * 1.5 + 0.18);
    }
    c = mix(vec3(dot(c, vec3(0.2126, 0.7152, 0.0722))), c, saturation);
    c = (c - 0.18) * contrast + 0.18;
    vec2 edge = (SCREEN_UV - 0.5) * 1.414214;
    c *= 1.0 - vignette * smoothstep(0.15, 0.95, dot(edge, edge));
    COLOR = vec4(max(c, vec3(0.0)), 1.0);
}
