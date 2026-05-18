struct VSOutput {
  float4 Position : SV_Position;
  float2 UV : TEXCOORD;
};

VSOutput VSMain(uint vertex_id : SV_VertexID) {
  const float2 positions[3] = {
      float2(-1.0, -1.0),
      float2(3.0, -1.0),
      float2(-1.0, 3.0),
  };

  VSOutput output;
  const float2 position = positions[vertex_id];
  output.Position = float4(position, 0.0, 1.0);
  output.UV = position * float2(0.5, -0.5) + float2(0.5, 0.5);
  return output;
}
