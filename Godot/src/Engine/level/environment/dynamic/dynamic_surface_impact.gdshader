shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_never, unshaded, shadows_disabled;

uniform float impact_time = 0.0;
uniform float brightness = 1.0;
varying vec2 impact_uv;
varying float impact_alpha;
varying float impact_kind;
varying float impact_part;
varying float impact_seed;

void vertex() {
    float age = impact_time - INSTANCE_CUSTOM.r;
    float kind = INSTANCE_CUSTOM.g;
    float strength = INSTANCE_CUSTOM.b;
    float seed = INSTANCE_CUSTOM.a;
    float part = UV2.x;
    bool snow = kind > 0.5 && kind < 1.5;
    bool foot = kind > 1.5;
    float duration = snow ? 0.85 : (foot ? 0.8 : 0.4);
    float life = age / duration;
    impact_uv = UV * 2.0 - 1.0;
    impact_kind = kind;
    impact_part = part;
    impact_seed = fract(seed * 13.7 + part * 0.618);
    impact_alpha = 0.0;
    if (life < 0.0 || life >= 1.0 || strength <= 0.0) {
        POSITION = vec4(0.0, 0.0, 2.0, 1.0);
    } else {
        vec3 world;
        if (part < 0.5) {
            float radius = mix(0.025, foot ? 0.48 : 0.16, life);
            world = (MODEL_MATRIX * vec4(impact_uv.x * radius, 0.0, impact_uv.y * radius, 1.0)).xyz;
            impact_alpha = snow ? 0.0 : pow(1.0 - life, 1.8) * (foot ? 0.22 : 0.14);
        } else {
            float variation = fract(sin(part * 17.13 + seed * 73.71) * 4173.13);
            float angle = part * 2.399963 + seed * TAU + variation * 0.5;
            float speed = (snow ? 0.42 : (foot ? 0.75 : 0.25)) * mix(0.55, 1.35, variation);
            float lift = (snow ? 1.05 : (foot ? 1.6 : 1.05)) * mix(0.65, 1.2, impact_seed);
            float height = max(0.0, age * lift - age * age * (snow ? 1.3 : 3.4));
            vec3 local = vec3(cos(angle) * age * speed, height, sin(angle) * age * speed);
            world = (MODEL_MATRIX * vec4(local, 1.0)).xyz;
            float size = (snow ? mix(0.009, 0.035, life) : (foot ? 0.009 : 0.004)) * mix(0.6, 1.4, variation);
            float stretch = snow ? 0.8 : mix(2.4, 1.0, life);
            world += (INV_VIEW_MATRIX[0].xyz * impact_uv.x + INV_VIEW_MATRIX[1].xyz * impact_uv.y * stretch) * size;
            impact_alpha = (1.0 - life) * (snow ? 0.42 : 0.38) * smoothstep(0.0, 0.025, height);
        }
        impact_alpha *= strength;
        POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
    }
}

void fragment() {
    float radius = length(impact_uv);
    bool should_discard = (impact_alpha <= 0.005 || radius > 1.15);
    ALBEDO = vec3(0.0);
    ALPHA = 0.0;

    if (!should_discard) {
        float angle = atan(impact_uv.y, impact_uv.x);
        float ring_radius = 0.78 + 0.012 * sin(angle * 7.0 + impact_seed * TAU);
        float shape = impact_part < 0.5
            ? exp(-pow((radius - ring_radius) * 17.0, 2.0)) * (0.65 + 0.35 * sin(angle * 3.0 + impact_seed * TAU))
            : pow(max(0.0, 1.0 - radius * radius), 1.4);
        if (impact_kind > 0.5 && impact_kind < 1.5)
            shape *= smoothstep(0.0, 0.4, 0.7 + 0.3 * sin(impact_uv.x * 13.0 + impact_seed * 17.0) * cos(impact_uv.y * 11.0));
        float alpha = impact_alpha * shape;
        if (alpha >= 0.005) {
            ALPHA = alpha;
            ALBEDO = (impact_kind > 0.5 && impact_kind < 1.5 ? vec3(0.85, 0.90, 0.97) : vec3(0.52, 0.65, 0.76)) * brightness;
        }
    }
}
