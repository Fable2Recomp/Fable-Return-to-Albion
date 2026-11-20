#[compute]
#version 450

// The phone's tree-card atlas (AndroidTreeCardAtlas): copies one region of a source into the atlas
// at an offset, or writes one region of a mip as the 2 x 2 box of the mip above. Regions are whole
// cells, so a cell's mips never read a neighbour.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0) uniform sampler2D source;
layout(set = 0, binding = 1, rgba16f) uniform restrict writeonly image2D target;

layout(push_constant, std430) uniform Region {
	ivec2 from;   // the source texel at the region's corner
	ivec2 to;     // the target texel at the region's corner
	ivec2 size;   // the region, in target texels
	int box;      // 0: copy; 1: each target texel is the mean of the source's 2 x 2 block
	int pad;
} region;

void main()
{
	ivec2 at = ivec2(gl_GlobalInvocationID.xy);
	if (at.x >= region.size.x || at.y >= region.size.y)
	{
		return;
	}

	vec4 colour;
	if (region.box == 0)
	{
		colour = texelFetch(source, region.from + at, 0);
	}
	else
	{
		ivec2 s = region.from + (at * 2);
		colour = 0.25 * (texelFetch(source, s, 0) + texelFetch(source, s + ivec2(1, 0), 0) +
			texelFetch(source, s + ivec2(0, 1), 0) + texelFetch(source, s + ivec2(1, 1), 0));
	}

	imageStore(target, region.to + at, colour);
}
