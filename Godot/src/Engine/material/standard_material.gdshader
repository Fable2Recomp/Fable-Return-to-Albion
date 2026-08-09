shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled,
    ambient_light_disabled, specular_disabled;

uniform sampler2D albedo_tex : source_color, filter_linear_mipmap_anisotropic;
uniform sampler2D normal_tex : hint_normal, filter_linear_mipmap_anisotropic;
uniform sampler2D emission_tex : source_color, filter_linear_mipmap_anisotropic;
uniform bool has_normal = false;
uniform bool has_emission = false;
uniform bool alpha_scissor_on = false;
uniform float alpha_scissor_threshold = 0.25;

instance uniform vec4 probe_r = vec4(0.0);
instance uniform vec4 probe_g = vec4(0.0);
instance uniform vec4 probe_b = vec4(0.0);
instance uniform vec4 lightmap_colour = vec4(1.0, 1.0, 1.0, 0.0);
instance uniform vec4 ambient_colour = vec4(0.30, 0.30, 0.30, 0.0);

void fragment() {
    vec4 alb = texture(albedo_tex, UV);
    if (alpha_scissor_on && alb.a < alpha_scissor_threshold) {
        discard;
    }
    ALBEDO = alb.rgb;

    if (has_normal) {
        NORMAL_MAP = texture(normal_tex, UV).rgb;
    }

    vec3 nw = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
    vec3 gn = vec3(-nw.x, nw.z, nw.y);
    vec3 bake = vec3(0.0);
    if (lightmap_colour.w > 0.5) {
        bake = max(vec3(probe_r.x, probe_g.x, probe_b.x)
             + gn.x * vec3(probe_r.y, probe_g.y, probe_b.y)
             + gn.y * vec3(probe_r.z, probe_g.z, probe_b.z)
             + gn.z * vec3(probe_r.w, probe_g.w, probe_b.w),
               vec3(0.0));
    }
    vec3 ambient = bake * lightmap_colour.rgb + ambient_colour.rgb;

    EMISSION = alb.rgb * ambient;
    if (has_emission) {
        EMISSION += texture(emission_tex, UV).rgb;
    }
}

void light() {
    DIFFUSE_LIGHT += clamp(dot(NORMAL, LIGHT), 0.0, 1.0) *
        ATTENUATION * (LIGHT_COLOR / PI) * ALBEDO;
}
