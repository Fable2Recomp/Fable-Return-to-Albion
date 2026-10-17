const float CLOUD_LAYER_ALTITUDE = 1400.0;

vec2 cloud_layer_uv(vec3 position) {
    vec2 drift = cloud_offset * 0.004;
    return position.xz * (cloud_height * cloud_scale / CLOUD_LAYER_ALTITUDE) + drift;
}

float cloud_layer_noise(vec2 uv) {
    vec2 drift = cloud_offset * 0.004;
    float shape = texture(cloud_shape, uv).r;
    float detail = texture(cloud_detail, uv * 2.7 + drift * 1.6).r;
    return shape - (1.0 - detail) * 0.22 * (1.0 - overcast * 0.5);
}

float cloud_layer_opacity(float noise) {
    float cover = clamp(cloud_coverage + overcast * 0.6 + storm * 0.3, 0.0, 1.0);
    float density = clamp(cloud_density + overcast * 0.35 + storm * 0.3, 0.0, 1.0);
    return smoothstep(1.0 - cover, 1.0 - cover + mix(cloud_softness, 0.12, overcast), noise) * density;
}
