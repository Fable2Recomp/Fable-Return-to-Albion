shader_type canvas_item;
render_mode unshaded;

uniform bool volumetric_clouds = false;
uniform sampler3D cloud_volume_shape : filter_linear, repeat_enable;
uniform sampler3D cloud_volume_detail : filter_linear, repeat_enable;
uniform sampler2D cloud_shape : filter_linear_mipmap, repeat_enable;
uniform sampler2D cloud_detail : filter_linear_mipmap, repeat_enable;
uniform float cloud_coverage = 0.45;
uniform float cloud_density = 0.70;
uniform float cloud_softness = 0.35;
uniform float cloud_scale = 1.0;
uniform float cloud_height = 0.12;
uniform float cloud_speed = 1.0;
uniform float cloud_time = 0.0;
uniform vec2 cloud_offset = vec2(0.0);
uniform float cloud_motion_multiplier = 1.0;
uniform vec2 wind_dir = vec2(1.0, 0.25);
uniform float overcast = 0.0;
uniform float storm = 0.0;
uniform vec3 sun_engine = vec3(1.0, 0.0, 0.0);
uniform vec2 shadow_map_center = vec2(0.0);
uniform float shadow_map_extent = 4000.0;
#include "res://src/Engine/level/environment/dynamic/dynamic_cloud_density.gdshaderinc"
#include "res://src/Engine/level/environment/dynamic/dynamic_cloud_layers.gdshaderinc"

float sunlight_opacity(vec3 position) {
    vec3 sun = normalize(vec3(sun_engine.x, sun_engine.z, sun_engine.y));
    if (sun.y <= 0.03) { return 0.0; }
    float opacity;
    if (volumetric_clouds) {
        float distance_in_layer = CLOUD_DEPTH / sun.y;
        float step_length = distance_in_layer / 8.0;
        float optical_depth = 0.0;
        for (int i = 0; i < 8; i++) {
            optical_depth += volume_density(position + sun * (float(i) + 0.5) * step_length) * step_length;
        }
        opacity = 1.0 - exp(-optical_depth * CLOUD_EXTINCTION);
    } else {
        opacity = cloud_layer_opacity(cloud_layer_noise(cloud_layer_uv(position)));
    }
    return opacity * 0.85 * smoothstep(0.03, 0.10, sun.y);
}

void fragment() {
    vec2 world = shadow_map_center + (UV - 0.5) * shadow_map_extent;
    float opacity = sunlight_opacity(vec3(world.x, CLOUD_BASE, world.y));
    COLOR = vec4(vec3(opacity), 1.0);
}
