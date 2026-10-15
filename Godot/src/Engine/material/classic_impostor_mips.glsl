#[compute]
#version 450

// One mip of the tree impostor atlas from the one above it: the 2 x 2 box (one bilinear tap at
// the block's centre). The title's impostors carry four mips (`sub_82A85AE0`,
// Texture_InitHeaderWrapper mips = 4); a viewport's texture has none.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D above;
layout(set = 0, binding = 1, rgba16f) uniform restrict writeonly image2D below;

void main()
{
	ivec2 p = ivec2(gl_GlobalInvocationID.xy);
	ivec2 size = imageSize(below);
	if (p.x >= size.x || p.y >= size.y)
	{
		return;
	}
	vec2 uv = (vec2(p) + 0.5) / vec2(size);
	imageStore(below, p, textureLod(above, uv, 0.0));
}
