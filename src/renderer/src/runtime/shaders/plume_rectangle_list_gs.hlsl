struct RectVertex {
    float4 pos : SV_Position;
    float4 tex0 : TEXCOORD0;
    float4 tex1 : TEXCOORD1;
    float4 tex2 : TEXCOORD2;
    float4 tex3 : TEXCOORD3;
    float4 tex4 : TEXCOORD4;
    float4 tex5 : TEXCOORD5;
    float4 tex6 : TEXCOORD6;
    float4 tex7 : TEXCOORD7;
    float4 tex8 : TEXCOORD8;
    float4 tex9 : TEXCOORD9;
    float4 tex10 : TEXCOORD10;
    float4 tex11 : TEXCOORD11;
    float4 tex12 : TEXCOORD12;
    float4 tex13 : TEXCOORD13;
    float4 tex14 : TEXCOORD14;
    float4 tex15 : TEXCOORD15;
    float4 color0 : COLOR0;
    float4 color1 : COLOR1;
};

float EdgeLengthSq(float4 a, float4 b) {
    float2 delta = b.xy - a.xy;
    return dot(delta, delta);
}

RectVertex MakeSyntheticCorner(RectVertex v0, RectVertex v1, RectVertex v2) {
    RectVertex out_v;
    out_v.pos = v1.pos - v0.pos + v2.pos;
    out_v.tex0 = v1.tex0 - v0.tex0 + v2.tex0;
    out_v.tex1 = v1.tex1 - v0.tex1 + v2.tex1;
    out_v.tex2 = v1.tex2 - v0.tex2 + v2.tex2;
    out_v.tex3 = v1.tex3 - v0.tex3 + v2.tex3;
    out_v.tex4 = v1.tex4 - v0.tex4 + v2.tex4;
    out_v.tex5 = v1.tex5 - v0.tex5 + v2.tex5;
    out_v.tex6 = v1.tex6 - v0.tex6 + v2.tex6;
    out_v.tex7 = v1.tex7 - v0.tex7 + v2.tex7;
    out_v.tex8 = v1.tex8 - v0.tex8 + v2.tex8;
    out_v.tex9 = v1.tex9 - v0.tex9 + v2.tex9;
    out_v.tex10 = v1.tex10 - v0.tex10 + v2.tex10;
    out_v.tex11 = v1.tex11 - v0.tex11 + v2.tex11;
    out_v.tex12 = v1.tex12 - v0.tex12 + v2.tex12;
    out_v.tex13 = v1.tex13 - v0.tex13 + v2.tex13;
    out_v.tex14 = v1.tex14 - v0.tex14 + v2.tex14;
    out_v.tex15 = v1.tex15 - v0.tex15 + v2.tex15;
    out_v.color0 = v1.color0 - v0.color0 + v2.color0;
    out_v.color1 = v1.color1 - v0.color1 + v2.color1;
    return out_v;
}

[maxvertexcount(4)]
void GSMain(triangle RectVertex input_vertices[3],
            inout TriangleStream<RectVertex> output_stream) {
    float edge0 = EdgeLengthSq(input_vertices[1].pos, input_vertices[2].pos);
    float edge1 = EdgeLengthSq(input_vertices[2].pos, input_vertices[0].pos);
    float edge2 = EdgeLengthSq(input_vertices[0].pos, input_vertices[1].pos);
    uint first = (edge0 > edge1 && edge0 > edge2) ? 0u : ((edge1 > edge2) ? 1u : 2u);

    RectVertex v0 = input_vertices[first];
    RectVertex v1 = input_vertices[(first + 1u) % 3u];
    RectVertex v2 = input_vertices[(first + 2u) % 3u];
    output_stream.Append(v0);
    output_stream.Append(v1);
    output_stream.Append(v2);
    output_stream.Append(MakeSyntheticCorner(v0, v1, v2));
    output_stream.RestartStrip();
}
