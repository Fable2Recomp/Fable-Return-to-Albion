#ifndef FABLE_DITHER_INCLUDED
#define FABLE_DITHER_INCLUDED

// Branchless, array-free 4x4 ordered Bayer threshold in (0, 1) for screen-space fade dithering.
// Matches the standard 4x4 matrix {0,8,2,10; 12,4,14,6; 3,11,1,9; 15,7,13,5} without indexing
// a constant array inside divergent D3D12/DXIL pixel control flow.
float fable_bayer_threshold(vec2 frag_xy) {
	ivec2 cell = ivec2(frag_xy) & ivec2(3);
	int x_xor_y = cell.x ^ cell.y;
	int bayer = ((x_xor_y & 1) << 3) | ((cell.y & 1) << 2) | (x_xor_y & 2) | ((cell.y & 2) >> 1);
	return (float(bayer) + 0.5) * (1.0 / 16.0);
}

#endif
