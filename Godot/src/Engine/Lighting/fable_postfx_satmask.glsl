#[vertex]
#version 450

// One triangle over the screen; the stencil test (bit 0x80 set by entity_saturation_mask) picks
// the pixels.
void main() {
	vec2 p = vec2(float((gl_VertexIndex << 1) & 2), float(gl_VertexIndex & 2));
	gl_Position = vec4((p * 2.0) - 1.0, 0.0, 1.0);
}

#[fragment]
#version 450

layout(location = 0) out vec4 mask;

void main() {
	mask = vec4(1.0);
}
