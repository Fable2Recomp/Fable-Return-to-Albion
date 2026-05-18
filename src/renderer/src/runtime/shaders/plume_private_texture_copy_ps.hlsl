#define REFII_SHADER_STAGE_PIXEL 1

#ifdef __spirv__
struct PushConstants {
  uint64_t VertexShaderConstants;
  uint64_t PixelShaderConstants;
  uint64_t SharedConstants;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> g_PushConstants;
#else
cbuffer SharedConstants : register(b2, space4) {
  uint4 g_Texture2DDescriptorIndices[4] : packoffset(c0);
  uint4 g_Texture3DDescriptorIndices[4] : packoffset(c4);
  uint4 g_TextureCubeDescriptorIndices[4] : packoffset(c8);
  uint4 g_SamplerDescriptorIndices[4] : packoffset(c12);
  uint g_Booleans : packoffset(c16.x);
  uint g_SwappedTexcoords : packoffset(c16.y);
  float2 g_HalfPixelOffset : packoffset(c16.z);
  float g_AlphaThreshold : packoffset(c17.x);
  uint g_PrivateTextureSlot : packoffset(c17.y);
};
#endif

Texture2D<float4> g_Texture2DDescriptorHeap[] : register(t0, space0);
SamplerState g_SamplerDescriptorHeap[] : register(s0, space3);

struct PSInput {
  float4 Position : SV_Position;
  float4 TexCoord0 : TEXCOORD0;
};

uint LoadTexture2DDescriptor(uint slot) {
  slot &= 15u;
#ifdef __spirv__
  return vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + slot * 4u);
#else
  return g_Texture2DDescriptorIndices[slot >> 2u][slot & 3u];
#endif
}

uint LoadSamplerDescriptor(uint slot) {
  slot &= 15u;
#ifdef __spirv__
  return vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 192u +
                                 slot * 4u);
#else
  return g_SamplerDescriptorIndices[slot >> 2u][slot & 3u];
#endif
}

uint LoadPrivateTextureSlot() {
#ifdef __spirv__
  return vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 276u);
#else
  return g_PrivateTextureSlot;
#endif
}

float4 PSMain(PSInput input) : SV_Target0 {
  const uint slot = LoadPrivateTextureSlot();
  const uint texture_descriptor = LoadTexture2DDescriptor(slot);
  const uint sampler_descriptor = LoadSamplerDescriptor(slot);
  return g_Texture2DDescriptorHeap[texture_descriptor].Sample(
      g_SamplerDescriptorHeap[sampler_descriptor], input.TexCoord0.xy);
}
