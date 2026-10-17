#ifndef DYNAMIC_CLOUD_RECEIVER
#define DYNAMIC_CLOUD_RECEIVER
global uniform vec3 dynamic_sun_toward;
global uniform sampler2D dynamic_cloud_mask : filter_linear, repeat_disable;
global uniform vec4 dynamic_cloud_projection;

float surface_cloud_transmission(vec3 world) {
    if (dynamic_cloud_projection.w < 0.5 || dynamic_sun_toward.y < 0.03) { return 1.0; }
    vec2 at_cloud = world.xz + dynamic_sun_toward.xz * ((1400.0 - world.y) / dynamic_sun_toward.y);
    vec2 uv = (at_cloud - dynamic_cloud_projection.xy) / dynamic_cloud_projection.z + 0.5;
    float border = min(min(uv.x, uv.y), min(1.0 - uv.x, 1.0 - uv.y));
    return 1.0 - texture(dynamic_cloud_mask, clamp(uv, vec2(0), vec2(1))).r * smoothstep(0.0, 0.06, border);
}
#endif
