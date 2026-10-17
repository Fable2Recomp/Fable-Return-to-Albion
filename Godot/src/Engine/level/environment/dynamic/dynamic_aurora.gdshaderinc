float aurora_hash(float x) {
    uint bits = uint(int(x));
    bits = (bits ^ (bits >> 16u)) * 0x7feb352du;
    bits = (bits ^ (bits >> 15u)) * 0x846ca68bu;
    bits = bits ^ (bits >> 16u);
    return float(bits & 0x00ffffffu) / 16777216.0;
}

float aurora_noise(float x) {
    float cell = floor(x), f = fract(x);
    f = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    return mix(aurora_hash(cell), aurora_hash(cell + 1.0), f);
}

float aurora_fold(float x) {
    return aurora_noise(x) * 0.57 + aurora_noise(x * 2.07 + 13.0) * 0.28
        + aurora_noise(x * 4.31 + 47.0) * 0.15;
}

vec3 aurora_curtain(vec2 p, float time, float seed, float altitude, float strength) {
    float drift = time * 0.012;
    float x = p.x + drift;
    float warp = x + (aurora_fold(x * 1.25 + seed + time * 0.008) - 0.5) * 0.6;
    float fold = aurora_fold(warp * 2.3 + seed);
    float bottom = altitude + (fold - 0.5) * 0.19 + p.x * 0.08
        + 0.19 * sin(warp * 2.4 + seed + time * 0.012);
    float h = p.y - bottom;
    float u = warp + h * (0.10 + 0.09 * aurora_noise(x * 2.0 + seed))
        + 0.016 * sin(h * 5.0 + x * 2.0 + time * 0.025);
    float fine = aurora_noise(u * 135.0 + seed * 19.0 - time * 0.08);
    float medium = aurora_noise(u * 48.0 + seed * 7.0 + time * 0.025);
    float rays = 0.62 + 0.20 * medium + 0.23 * fine * fine;
    float activity = aurora_fold(warp * 0.85 + seed + time * 0.007);
    float length = 0.32 + 0.30 * aurora_fold(u * 1.8 + seed + 19.0);
    float edge_distance = h / 0.025;
    float edge = exp(-edge_distance * edge_distance) * 0.18;
    float curtain = smoothstep(-0.022, 0.035, h) * exp(-max(h, 0.0) / length * 2.4);
    float glow = exp(-abs(h) * 8.0) * 0.10;
    float extent = exp(-pow(abs((x + 0.3) / 2.9), 4.0));
    float brightness = smoothstep(0.28, 0.72, activity) * extent * strength;
    float palette = aurora_noise(warp * 0.75 + seed * 2.0 + time * 0.003);
    float warmth = aurora_noise(warp * 0.45 + seed * 5.3 - time * 0.002);
    vec3 lower = mix(vec3(0.16, 1.0, 0.32), vec3(0.10, 0.72, 1.0), smoothstep(0.25, 0.70, palette));
    lower = mix(lower, vec3(0.38, 0.20, 1.0), smoothstep(0.70, 0.95, palette) * 0.8);
    vec3 upper = mix(vec3(0.55, 0.14, 1.0), vec3(1.0, 0.18, 0.48), smoothstep(0.25, 0.75, warmth));
    vec3 tint = mix(lower, upper, smoothstep(0.025, length * 0.65, h));
    return (tint * curtain * rays + lower * (edge + glow)) * brightness;
}

vec3 aurora_terms(vec3 dir, float day) {
    if (aurora_strength < 0.001 || dir.y < 0.02 || day > 0.99 || dir.z > -0.05) {
        return vec3(0.0);
    }
    vec2 p = dir.xy / max(-dir.z, 0.05);
    float time = cloud_time;
    vec3 light = aurora_curtain(p, time, 3.7, 0.43, 0.31);
    light += aurora_curtain(p * vec2(0.73, 1.0) + vec2(0.8, 0.0),
        time * 0.7, 31.6, 0.17, 0.04);
    return light * aurora_strength * pow(1.0 - day, 3.0)
        * smoothstep(0.02, 0.16, dir.y) * smoothstep(0.05, 0.25, -dir.z);
}
