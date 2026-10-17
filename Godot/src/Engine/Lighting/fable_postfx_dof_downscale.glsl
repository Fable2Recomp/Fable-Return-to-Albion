#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(rgba16f, set = 0, binding = 0) uniform restrict readonly image2D color_image;
layout(rgba16f, set = 0, binding = 1) uniform restrict writeonly image2D dof_image;
layout(set = 0, binding = 2, std430) restrict readonly buffer StateBuffer {
    float v[];
} state;
layout(set = 0, binding = 3) uniform sampler2D depth_tex;

layout(push_constant, std430) uniform Params {
    ivec2 full_size;
    ivec2 quarter_size;
    vec4 depth;
} params;

void main() {
    ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(coord, params.quarter_size))) { return; }
    vec3 total = vec3(0.0);
    float weight = 0.0;
    for (int y = 0; y < 4; y++) {
        for (int x = 0; x < 4; x++) {
            ivec2 at = min(coord * 4 + ivec2(x, y), params.full_size - 1);
            float z = texelFetch(depth_tex, at, 0).r;
            float view_w = params.depth.z * z + params.depth.w;
            if (z <= 0.0 || abs(view_w) < 1e-6) { continue; }
            float d = -(params.depth.x * z + params.depth.y) / view_w;
            float valid = 1.0 - clamp(d - 2550.0, 0.0, 1.0);
            total += imageLoad(color_image, at).rgb * valid;
            weight += valid;
        }
    }
    imageStore(dof_image, coord, vec4(total * state.v[16], weight) / 16.0);
}
