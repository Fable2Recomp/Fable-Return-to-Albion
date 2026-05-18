Texture2D<float4> g_SourceTexture : register(t0, space0);
SamplerState g_SourceSampler : register(s0, space1);

struct PushConstants {
  float2 UVScale;
  float2 UVBias;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> g_PushConstants
    : register(b0, space2);

struct PSInput {
  float4 Position : SV_Position;
  float2 UV : TEXCOORD;
};

float4 PSMain(PSInput input) : SV_Target {
  const float2 uv = input.UV * g_PushConstants.UVScale +
                    g_PushConstants.UVBias;
  return g_SourceTexture.Sample(g_SourceSampler, uv);
}
