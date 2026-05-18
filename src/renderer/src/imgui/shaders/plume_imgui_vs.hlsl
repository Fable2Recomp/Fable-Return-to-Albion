struct PushConstants {
  float2 DisplayPos;
  float2 DisplaySize;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> g_PushConstants
    : register(b0, space2);

struct VSInput {
  float2 Position : POSITION;
  float2 UV : TEXCOORD;
  float4 Color : COLOR;
};

struct VSOutput {
  float4 Position : SV_Position;
  float2 UV : TEXCOORD;
  float4 Color : COLOR;
};

VSOutput VSMain(VSInput input) {
  VSOutput output;
  const float2 normalized =
      (input.Position - g_PushConstants.DisplayPos) /
      g_PushConstants.DisplaySize;
  output.Position =
      float4(normalized * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
  output.UV = input.UV;
  output.Color = input.Color;
  return output;
}
