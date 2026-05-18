Texture2D<float4> g_FontTexture : register(t0, space0);
SamplerState g_FontSampler : register(s0, space1);

struct PSInput {
  float4 Position : SV_Position;
  float2 UV : TEXCOORD;
  float4 Color : COLOR;
};

float4 PSMain(PSInput input) : SV_Target {
  return input.Color * g_FontTexture.Sample(g_FontSampler, input.UV);
}
