#ifndef DYNAMIC_SURFACE_WEATHER
#define DYNAMIC_SURFACE_WEATHER
#include "res://src/Engine/level/environment/dynamic/dynamic_cloud_receiver.gdshaderinc"
global uniform vec4 dynamic_surface_weather;
global uniform float dynamic_surface_time;
global uniform float dynamic_surface_rain;
global uniform sampler2D dynamic_track_map : filter_linear, repeat_disable;
global uniform vec4 dynamic_track_projection;
global uniform float dynamic_track_height;
global uniform sampler2D dynamic_roof_map : filter_linear, repeat_disable;
global uniform vec4 dynamic_roof_projection;
global uniform vec3 dynamic_sun_radiance;
uniform float surface_weather_response : hint_range(0.0, 1.0) = 1.0;

float surface_hash(vec2 p) {
    uvec2 cell = uvec2(ivec2(floor(p)));
    uint h = (cell.x * 1597334677u) ^ (cell.y * 3812015801u);
    h = (h ^ (h >> 16u)) * 2246822519u;
    h = (h ^ (h >> 13u)) * 3266489917u;
    return float((h ^ (h >> 16u)) & 16777215u) / 16777216.0;
}
float surface_noise(vec2 p) {
    vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(surface_hash(i), surface_hash(i + vec2(1, 0)), f.x),
        mix(surface_hash(i + vec2(0, 1)), surface_hash(i + vec2(1)), f.x), f.y);
}

float surface_exposure(vec3 world) {
    if (dynamic_roof_projection.w < 0.5) return 1.0;
    vec2 uv = (world.xz - dynamic_roof_projection.xy) / dynamic_roof_projection.z + 0.5;
    if (min(uv.x, uv.y) < 0.0 || max(uv.x, uv.y) > 1.0) return 1.0;
    vec2 roof = textureLod(dynamic_roof_map, uv, 0.0).rg;
    float height = roof.x / max(roof.y, 0.0001);
    return 1.0 - roof.y * smoothstep(0.0, 0.16, height - world.y - 0.08);
}

float surface_track(vec3 world) {
    if (dynamic_track_projection.w < 0.5) return 0.0;
    vec2 uv = (world.xz - dynamic_track_projection.xy) / dynamic_track_projection.z + 0.5;
    if (min(uv.x, uv.y) < 0.01 || max(uv.x, uv.y) > 0.99) return 0.0;
    vec2 track = texture(dynamic_track_map, uv).rg;
    float height = track.y / max(track.x, 0.0001) + dynamic_track_height;
    return track.x * (1.0 - smoothstep(0.08, 0.3, abs(world.y - height)));
}

vec3 surface_weights(vec3 world, vec3 normal) {
    float gate = dynamic_surface_weather.w * surface_weather_response;
    if (gate < 0.001) { return vec3(0); }
    gate *= surface_exposure(world);
    if (gate < 0.001 || max(dynamic_surface_weather.x, dynamic_surface_weather.y) < 0.001) return vec3(0);
    float broad = surface_noise(world.xz * 0.27);
    float snow = 0.0;
    if (dynamic_surface_weather.y > 0.001) {
        float grain = surface_noise(world.xz * 1.7);
        float snow_line = (1.0 - dynamic_surface_weather.y) * 0.95;
        snow = smoothstep(snow_line - 0.08, snow_line + 0.26, 0.25 + 0.55 * broad + grain * 0.2);
        snow *= smoothstep(0.1, 0.65, normal.y) * smoothstep(0.0, 0.12, dynamic_surface_weather.y);
    }
    if (snow > 0.001) snow *= 1.0 - surface_track(world) * 0.8;
    float wet = dynamic_surface_weather.x * (0.3 + 0.7 * max(normal.y, 0.0));
    float puddle = smoothstep(1.0 - dynamic_surface_weather.z * 0.8, 1.08 - dynamic_surface_weather.z * 0.8, broad);
    puddle *= smoothstep(0.92, 0.995, normal.y) * (1.0 - snow);
    return vec3(wet, snow, puddle) * gate;
}

void surface_material(vec3 weights, vec3 world, inout vec3 albedo, inout float roughness, inout float metallic) {
    if (max(weights.x, weights.y) < 0.001) return;
    float wet = weights.x * (1.0 - weights.y);
    albedo *= mix(1.0, 0.58, wet);
    if (weights.y > 0.001) {
        float grain = surface_noise(world.xz * 38.0);
        albedo = mix(albedo, vec3(0.56, 0.61, 0.68) * mix(0.86, 1.0, grain), weights.y);
    }
    roughness = mix(roughness, 0.24, wet);
    roughness = mix(roughness, 0.075, weights.z);
    roughness = mix(roughness, 0.88, weights.y);
    metallic *= 1.0 - max(wet, weights.y);
}

vec2 surface_ripple(vec3 world) {
    if (dynamic_surface_rain < 0.001) return vec2(0.0);
    vec2 p = world.xz * 2.3;
    vec2 cell = floor(p);
    float seed = surface_hash(cell);
    vec2 centre = vec2(surface_hash(cell + 7.1), surface_hash(cell + 13.7)) * 0.5 + 0.25;
    vec2 f = fract(p) - centre;
    float age = fract(dynamic_surface_time * 0.7 + seed * 13.0);
    float radius = length(f);
    float ring = sin((radius - age * 0.65) * 65.0) * exp(-abs(radius - age * 0.65) * 18.0);
    return normalize(f + vec2(0.001)) * ring * sin(age * PI) * (1.0 - age) * step(0.35, seed) * 0.035 * dynamic_surface_rain;
}

vec3 surface_classic(vec3 colour, vec3 world, vec3 normal, vec3 view) {
    vec3 w = surface_weights(world, normal);
    vec3 coated = colour;
    float lum = dot(colour, vec3(0.299, 0.587, 0.114));
    coated *= mix(1.0, 0.64, w.x * (1.0 - w.y));
    coated = mix(coated, vec3(0.83, 0.89, 0.95) * lum * 1.65, w.y);
    float transmission = surface_cloud_transmission(world);
    coated *= mix(1.0, transmission, 0.65);
    float spec = pow(max(dot(reflect(-dynamic_sun_toward, normal), view), 0.0), mix(70.0, 350.0, w.z));
    return coated + dynamic_sun_radiance * spec * w.x * (1.0 - w.y) * transmission * 0.12;
}
#endif
