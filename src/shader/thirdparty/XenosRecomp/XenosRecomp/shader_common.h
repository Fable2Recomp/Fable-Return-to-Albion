#ifndef SHADER_COMMON_H_INCLUDED
#define SHADER_COMMON_H_INCLUDED

#define SPEC_CONSTANT_R11G11B10_NORMAL  (1 << 0)
#define SPEC_CONSTANT_ALPHA_TEST        (1 << 1)

#ifdef UNLEASHED_RECOMP
    #define SPEC_CONSTANT_BICUBIC_GI_FILTER (1 << 2)
    #define SPEC_CONSTANT_ALPHA_TO_COVERAGE (1 << 3)
    #define SPEC_CONSTANT_REVERSE_Z         (1 << 4)
#endif

#if !defined(__cplusplus) || defined(__INTELLISENSE__)

#define FLT_MIN asfloat(0xff7fffff)
#define FLT_MAX asfloat(0x7f7fffff)

#ifndef REFII_SHADER_STAGE_PIXEL
#define REFII_SHADER_STAGE_PIXEL 1
#endif

// Xenos memory export (memexport) state for this shader, emitted by the
// recompiler ahead of the include.
//
// REFII_MEMEXPORT_ENABLED is the compile gate: 1 when the shader writes any
// eM# export data register, which is exactly the condition the SDK uses
// (Shader::memexport_eM_written()). REFII_MEMEXPORT_STREAMS is the number of
// distinct stream constants recovered from the `mad eA, r#, const0100, c#`
// idiom - it is documentation, and the loud, greppable artifact that says
// "this shader writes to memory, not just to the render target".
#ifndef REFII_MEMEXPORT_ENABLED
#define REFII_MEMEXPORT_ENABLED 0
#endif
#ifndef REFII_MEMEXPORT_STREAMS
#define REFII_MEMEXPORT_STREAMS 0
#endif

// Xenos hardware-tessellation variant gate. Every VERTEX shader is compiled
// twice from the same generated HLSL: once normally, and once with
// REFII_TESSELLATION_VARIANT=1 (a dxc -D, see
// src/shader/src/refii_shader_xenos_translator_main.cpp) into the
// `<blob>.tess.<target>.bin` artifact. The tess variant seeds r0/r1 with the
// Xenos auto-generated tessellation input contract (patch index + domain UV)
// instead of the vfetch-driven vertex index; the shader body is byte-identical.
#ifndef REFII_TESSELLATION_VARIANT
#define REFII_TESSELLATION_VARIANT 0
#endif

#ifdef __spirv__

struct PushConstants
{
    uint64_t VertexShaderConstants;
    uint64_t PixelShaderConstants;
    uint64_t SharedConstants;
};

[[vk::push_constant]] ConstantBuffer<PushConstants> g_PushConstants;

#define g_Booleans                 vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 512)
#define g_SwappedTexcoords         vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 516)
#define g_HalfPixelOffset          vk::RawBufferLoad<float2>(g_PushConstants.SharedConstants + 520)
#define g_AlphaThreshold           vk::RawBufferLoad<float>(g_PushConstants.SharedConstants + 528)
#define g_VertexIntegerConstantValidMask vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 536)
#define g_PixelIntegerConstantValidMask vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 540)
#define REFII_VERTEX_INTEGER_CONSTANT_BYTE_BASE 576
#define REFII_PIXEL_INTEGER_CONSTANT_BYTE_BASE 1088
#define REFII_LOAD_INTEGER_CONSTANT(BASE, INDEX) int4( \
    asint(vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + (BASE) + (INDEX) * 16 + 0)), \
    asint(vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + (BASE) + (INDEX) * 16 + 4)), \
    asint(vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + (BASE) + (INDEX) * 16 + 8)), \
    asint(vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + (BASE) + (INDEX) * 16 + 12)))
#define g_VertexIntegerConstant(INDEX) REFII_LOAD_INTEGER_CONSTANT(REFII_VERTEX_INTEGER_CONSTANT_BYTE_BASE, INDEX)
#define g_PixelIntegerConstant(INDEX) REFII_LOAD_INTEGER_CONSTANT(REFII_PIXEL_INTEGER_CONSTANT_BYTE_BASE, INDEX)

[[vk::constant_id(0)]] const uint g_SpecConstants = 0;

#define g_SpecConstants() g_SpecConstants

// Memexport destination, delivered through the shared-constant block so that no
// new descriptor set / pipeline-layout entry is required on the Vulkan path.
// Bytes 544..560 of SharedConstants are otherwise unused (the block runs
// 0..384 texture rows, 384..512 sampler rows, 512..544 misc scalars,
// 576..1600 integer constants), so an un-wired runtime reads zeroes here and
// every export below is dropped instead of scribbling on memory.
//
// RUNTIME CONTRACT (Vulkan):
//   +544 uint64 device address of the memexport buffer (bufferDeviceAddress).
//   +552 uint   guest byte address that maps to buffer offset 0.
//   +556 uint   size of the buffer in bytes; 0 disables memexport entirely.
// Data lands in guest byte order (the endian swizzle below is applied), so the
// buffer holds guest memory, not host-native values. Writing from a vertex
// shader also needs the vertexPipelineStoresAndAtomics device feature.
#define g_MemoryExportBufferAddress vk::RawBufferLoad<uint64_t>(g_PushConstants.SharedConstants + 544, 0x8)
#define g_MemoryExportWindowBase    vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 552)
#define g_MemoryExportWindowBytes   vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 556)

// Xenos tessellation state, delivered through the shared-constant block exactly
// like the memexport slots directly above (no new descriptor set / pipeline
// layout entry). Bytes 560..576 are the last spare row before the integer
// constants at 576 (REFII_VERTEX_INTEGER_CONSTANT_BYTE_BASE), so an un-wired
// runtime reads zeroes here and the clamp below degrades to a 1x1 grid instead
// of dividing by zero.
//
// RUNTIME CONTRACT (both backends, filled per tess draw by the GTL):
//   +560 uint N               tessellation factor: subdivisions per patch edge.
//                             The host draws (N+1)*(N+1) grid vertices per
//                             patch instance (6*N*N indices); terrain uses the
//                             uniform N=2, water N=16. 0 = not wired; the
//                             shader clamps to 1.
//   +564 uint indexOffset     VGT_INDX_OFFSET for this draw, added to
//                             SV_InstanceID to recover the guest patch index
//                             in r0.x. Written raw (unsigned).
//   +568 uint reserved0       must be written 0.
//   +572 uint reserved1       must be written 0 (576 starts the vertex integer
//                             constants - do NOT grow past this).
#define g_TessellationFactor      vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 560)
#define g_TessellationIndexOffset vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 564)

#define DEFINE_MEMEXPORT_SHARED_CONSTANTS()
#define DEFINE_TESSELLATION_SHARED_CONSTANTS()

#if REFII_MEMEXPORT_ENABLED
#define REFII_MEMEXPORT_LOAD(BYTE_OFFSET) \
    vk::RawBufferLoad<uint>(g_MemoryExportBufferAddress + (BYTE_OFFSET))
#define REFII_MEMEXPORT_STORE(BYTE_OFFSET, VALUE) \
    vk::RawBufferStore<uint>(g_MemoryExportBufferAddress + (BYTE_OFFSET), (VALUE))
#endif

#else

#define DEFINE_SHARED_CONSTANTS() \
    uint g_Booleans : packoffset(c32.x); \
    uint g_SwappedTexcoords : packoffset(c32.y); \
    float2 g_HalfPixelOffset : packoffset(c32.z); \
    float g_AlphaThreshold : packoffset(c33.x); \
    uint g_VertexIntegerConstantValidMask : packoffset(c33.y); \
    uint g_PixelIntegerConstantValidMask : packoffset(c33.z); \
    int4 g_VertexIntegerConstants[32] : packoffset(c36); \
    int4 g_PixelIntegerConstants[32] : packoffset(c68);

#define g_VertexIntegerConstant(INDEX) g_VertexIntegerConstants[INDEX]
#define g_PixelIntegerConstant(INDEX) g_PixelIntegerConstants[INDEX]

static const uint g_SpecConstants_Value = 0;
#define g_SpecConstants() g_SpecConstants_Value

// Mirror of the Vulkan memexport slots at bytes 544..560 of SharedConstants.
// Only emitted into the cbuffer for shaders that actually export, so the
// generated HLSL of the other ~1170 blobs is untouched.
#define DEFINE_MEMEXPORT_SHARED_CONSTANTS() \
    uint2 g_MemoryExportBufferAddressUnusedOnD3D12 : packoffset(c34.x); \
    uint g_MemoryExportWindowBase : packoffset(c34.z); \
    uint g_MemoryExportWindowBytes : packoffset(c34.w);

// Mirror of the Vulkan tessellation slots at bytes 560..576 of SharedConstants
// (c35 - the row after the memexport slots at c34, directly before the integer
// constants at c36). Only emitted into the cbuffer when compiling the
// REFII_TESSELLATION_VARIANT artifact, so the normal variant's generated HLSL
// is untouched. Layout documented at the Vulkan definitions above.
#define DEFINE_TESSELLATION_SHARED_CONSTANTS() \
    uint g_TessellationFactor : packoffset(c35.x); \
    uint g_TessellationIndexOffset : packoffset(c35.y);

#if REFII_MEMEXPORT_ENABLED
// D3D12/DXIL has no raw pointer store, so the destination has to be a real UAV.
// RUNTIME CONTRACT: the D3D12 root signature must expose a UAV range at
// u0/space5 visible to the exporting stage before these pipelines can be
// created. Until it does, only the Vulkan path can run the five memexport
// shaders (d3d12 blobs are Windows-only - see GetShaderCompileTargets).
RWByteAddressBuffer g_MemoryExportBuffer : register(u0, space5);

#define REFII_MEMEXPORT_LOAD(BYTE_OFFSET) g_MemoryExportBuffer.Load(BYTE_OFFSET)
#define REFII_MEMEXPORT_STORE(BYTE_OFFSET, VALUE) g_MemoryExportBuffer.Store((BYTE_OFFSET), (VALUE))
#endif

#endif

#if REFII_MEMEXPORT_ENABLED

// ---------------------------------------------------------------------------
// Xenos memory export (memexport / GPU stream-out).
//
// A stream is described by a float4 ALU constant that the guest materialises
// into the eA export register with the idiom `mad eA, r#, const0100, c#`
// (const0100 = (0,1,0,0), so eA.y ends up holding 0x4B000000 + element index
// and the other three components pass the stream constant through untouched):
//
//   dw0: base_address:30 (physical address >> 2) | 0b01 << 30
//   dw1: 0x4B000000                (2^23 - the index rides on the mantissa)
//   dw2: endianness:3 | format:6 (bit 8) | num_format:3 (bit 16) |
//        red_blue_swap:1 (bit 19) | 0x4B0 << 20
//   dw3: index_count:23 | 0x96 << 23
//
// The struct is rexglue-sdk/include/rex/graphics/xenos.h
// (xe_gpu_memexport_stream_t, line ~1482); the packing / endian / bounds rules
// below are transcribed from
// rexglue-sdk/src/graphics/pipeline/shader/dxbc_translator_memexport.cpp
// (DxbcShaderTranslator::ExportToMemory). Nothing here is invented: where the
// SDK uses a DXBC opcode this uses the equivalent HLSL expression.
// ---------------------------------------------------------------------------

// Packs value.xyzw into consecutive bit fields of one dword, following the
// Direct3D fixed-point conversion rules (SDK: pack_8_16_32). Zero widths are
// skipped. Only used for widths <= 16.
uint memexportPackFixed(float4 value, uint4 widths, bool numSigned, bool numInteger)
{
    uint result = 0u;
    uint offset = 0u;

    [unroll] for (uint i = 0u; i < 4u; ++i)
    {
        uint width = widths[i];
        if (width == 0u)
            continue;

        // Denormal/NaN flush - the SDK does this before any conversion.
        float v = value[i];
        v = isnan(v) ? 0.0 : v;

        uint field;
        if (numSigned)
        {
            float maxValue = float((1u << (width - 1u)) - 1u);
            if (numInteger)
            {
                v = clamp(v, -1.0 - maxValue, maxValue);
            }
            else
            {
                v = clamp(v, -1.0, 1.0);
                if (width > 2u)
                    v *= maxValue;
            }
            // Plus/minus 0.5 then truncate towards zero.
            v += (v < 0.0) ? -0.5 : 0.5;
            field = uint(int(v));
        }
        else
        {
            float maxValue = float((1u << width) - 1u);
            if (numInteger)
            {
                v = clamp(v, 0.0, maxValue);
            }
            else
            {
                v = saturate(v);
                if (width > 1u)
                    v *= maxValue;
            }
            v += 0.5;
            field = uint(v);
        }

        result |= (field & ((1u << width) - 1u)) << offset;
        offset += width;
    }

    return result;
}

// Packs one eM# value for 'format'. Returns log2 of the element size in bytes
// (0..4), or 0xFFFFFFFF for a color format the hardware does not define - the
// SDK writes UINT32_MAX there and then enters no store case at all.
uint memexportPack(uint format, bool numSigned, bool numInteger, float4 value, out uint4 packed)
{
    packed = uint4(0u, 0u, 0u, 0u);

    switch (format)
    {
    case 2u:   // k_8
    case 8u:   // k_8_A
    case 9u:   // k_8_B
        packed.x = memexportPackFixed(value, uint4(8u, 0u, 0u, 0u), numSigned, numInteger);
        return 0u;

    case 3u:   // k_1_5_5_5
        packed.x = memexportPackFixed(value, uint4(5u, 5u, 5u, 1u), numSigned, numInteger);
        return 1u;

    case 4u:   // k_5_6_5
        packed.x = memexportPackFixed(value, uint4(5u, 6u, 5u, 0u), numSigned, numInteger);
        return 1u;

    case 5u:   // k_6_5_5
        packed.x = memexportPackFixed(value, uint4(5u, 5u, 6u, 0u), numSigned, numInteger);
        return 1u;

    case 6u:   // k_8_8_8_8
    case 14u:  // k_8_8_8_8_A
    case 50u:  // k_8_8_8_8_AS_16_16_16_16
        packed.x = memexportPackFixed(value, uint4(8u, 8u, 8u, 8u), numSigned, numInteger);
        return 2u;

    case 7u:   // k_2_10_10_10
    case 54u:  // k_2_10_10_10_AS_16_16_16_16
        packed.x = memexportPackFixed(value, uint4(10u, 10u, 10u, 2u), numSigned, numInteger);
        return 2u;

    case 10u:  // k_8_8
        packed.x = memexportPackFixed(value, uint4(8u, 8u, 0u, 0u), numSigned, numInteger);
        return 1u;

    case 15u:  // k_4_4_4_4
        packed.x = memexportPackFixed(value, uint4(4u, 4u, 4u, 4u), numSigned, numInteger);
        return 1u;

    case 16u:  // k_10_11_11
    case 55u:  // k_10_11_11_AS_16_16_16_16
        packed.x = memexportPackFixed(value, uint4(11u, 11u, 10u, 0u), numSigned, numInteger);
        return 2u;

    case 17u:  // k_11_11_10
    case 56u:  // k_11_11_10_AS_16_16_16_16
        packed.x = memexportPackFixed(value, uint4(10u, 11u, 11u, 0u), numSigned, numInteger);
        return 2u;

    case 24u:  // k_16
        packed.x = memexportPackFixed(value, uint4(16u, 0u, 0u, 0u), numSigned, numInteger);
        return 1u;

    case 25u:  // k_16_16
        packed.x = memexportPackFixed(value, uint4(16u, 16u, 0u, 0u), numSigned, numInteger);
        return 2u;

    case 26u:  // k_16_16_16_16
        packed.x = memexportPackFixed(value, uint4(16u, 16u, 0u, 0u), numSigned, numInteger);
        packed.y = memexportPackFixed(value.zwxy, uint4(16u, 16u, 0u, 0u), numSigned, numInteger);
        return 3u;

    case 30u:  // k_16_FLOAT
        packed.x = f32tof16(value.x);
        return 1u;

    case 31u:  // k_16_16_FLOAT
        packed.x = f32tof16(value.x) | (f32tof16(value.y) << 16u);
        return 2u;

    case 32u:  // k_16_16_16_16_FLOAT
        packed.x = f32tof16(value.x) | (f32tof16(value.y) << 16u);
        packed.y = f32tof16(value.z) | (f32tof16(value.w) << 16u);
        return 3u;

    case 36u:  // k_32_FLOAT
        packed.x = asuint(value.x);
        return 2u;

    case 37u:  // k_32_32_FLOAT
        packed.xy = asuint(value.xy);
        return 3u;

    case 38u:  // k_32_32_32_32_FLOAT
        packed = asuint(value);
        return 4u;

    default:
        return 0xFFFFFFFFu;
    }
}

// Endian128 swizzle, applied to the packed element exactly as the resolve path
// does (SDK: the endianness block of ExportToMemory). 8-in-64 and 8-in-128
// first reorder whole dwords and then fall through to 8-in-32.
uint4 memexportEndianSwap(uint4 v, uint endian)
{
    if (endian == 4u)          // k8in64
    {
        v = v.yxwz;
        endian = 2u;           // k8in32
    }
    else if (endian == 5u)     // k8in128
    {
        v = v.wzyx;
        endian = 2u;           // k8in32
    }

    if (endian == 1u || endian == 2u)   // k8in16, or the first half of k8in32
    {
        uint4 evenBytes = v & 0x00FF00FFu;
        uint4 oddBytes = (v >> 8u) & 0x00FF00FFu;
        v = evenBytes * 256u + oddBytes;
    }

    if (endian == 2u || endian == 3u)   // k16in32, or the second half of k8in32
    {
        v = (v >> 16u) | (v << 16u);
    }

    return v;
}

// Writes the eM# registers accumulated since the last `alloc export` (or since
// the start of the shader) into the stream described by eA. Called at every
// alloc boundary and at every shader exit; a zero written-mask makes it a nop.
//
// windowBase / windowBytes come in as parameters rather than being read here:
// on the D3D12 path they are cbuffer fields that the recompiler declares
// *after* this header is pasted in, so they are only nameable at the call site.
void memexportFlush(uint windowBase, uint windowBytes, float4 eA, uint eMWritten,
                    float4 eM0, float4 eM1, float4 eM2, float4 eM3, float4 eM4)
{
    if (eMWritten == 0u)
        return;

    // A zero window means the host has not bound a memexport buffer for this
    // draw. Drop instead of writing to an address we do not own.
    if (windowBytes == 0u)
        return;

    uint4 address = asuint(eA);

    // The stream constant's magic exponents must survive into eA, otherwise the
    // shader never wrote a real stream constant (the SDK performs the same
    // 0x1 / 0x96 / 0x96 / 0x96 check before exporting).
    if ((address.x >> 30u) != 0x1u ||
        (address.y >> 23u) != 0x96u ||
        (address.z >> 23u) != 0x96u ||
        (address.w >> 23u) != 0x96u)
    {
        return;
    }

    uint endian = address.z & 0x7u;
    uint format = (address.z >> 8u) & 0x3Fu;
    uint numFormat = (address.z >> 16u) & 0x7u;
    bool redBlueSwap = ((address.z >> 19u) & 0x1u) != 0u;
    uint baseIndex = address.y & 0x7FFFFFu;
    uint indexCount = address.w & 0x7FFFFFu;
    uint baseBytes = address.x << 2u;   // 30-bit dword address -> bytes

    // SurfaceNumberFormat: bit 0 = signed, bit 1 = integer (7 == float, which
    // only reaches the explicit *_FLOAT color formats).
    bool numSigned = (numFormat & 0x1u) != 0u;
    bool numInteger = (numFormat & 0x2u) != 0u;

    float4 eM[5] = { eM0, eM1, eM2, eM3, eM4 };

    [unroll] for (uint i = 0u; i < 5u; ++i)
    {
        if ((eMWritten & (1u << i)) == 0u)
            continue;

        // index_count is in elements of this stream's own format; the hardware
        // bounds-checks against it, so out-of-range elements are dropped.
        uint elementIndex = baseIndex + i;
        if (elementIndex >= indexCount)
            continue;

        float4 value = eM[i];
        if (redBlueSwap)
            value = value.zyxw;

        uint4 packed;
        uint elementSizeLog2 = memexportPack(format, numSigned, numInteger, value, packed);
        if (elementSizeLog2 > 4u)
            continue;   // undefined color format - the hardware stores nothing

        packed = memexportEndianSwap(packed, endian);

        uint elementBytes = 1u << elementSizeLog2;
        uint guestByte = baseBytes + (elementIndex << elementSizeLog2);
        if (guestByte < windowBase)
            continue;

        uint hostOffset = guestByte - windowBase;
        if (hostOffset + elementBytes > windowBytes)
            continue;

        if (elementSizeLog2 < 2u)
        {
            // Sub-dword element: read-modify-write of the containing dword.
            // NOTE: not atomic, unlike the SDK's AtomicAnd/AtomicOr pair -
            // vk::RawBufferStore has no atomic form. Fable II never exports a
            // sub-dword format (all five memexport shaders use >= 32bpp
            // streams), so no two invocations share a dword here.
            uint shift = (hostOffset & 3u) * 8u;
            uint width = elementBytes * 8u;
            uint mask = ((1u << width) - 1u) << shift;
            uint dwordOffset = hostOffset & ~3u;
            uint dword = REFII_MEMEXPORT_LOAD(dwordOffset);
            REFII_MEMEXPORT_STORE(dwordOffset, (dword & ~mask) | ((packed.x << shift) & mask));
        }
        else
        {
            uint dwordCount = 1u << (elementSizeLog2 - 2u);
            [unroll] for (uint d = 0u; d < 4u; ++d)
            {
                if (d < dwordCount)
                    REFII_MEMEXPORT_STORE(hostOffset + d * 4u, packed[d]);
            }
        }
    }
}

#endif  // REFII_MEMEXPORT_ENABLED

Texture2D<float4> g_Texture2DDescriptorHeap[] : register(t0, space0);
Texture3D<float4> g_Texture3DDescriptorHeap[] : register(t0, space1);
TextureCube<float4> g_TextureCubeDescriptorHeap[] : register(t0, space2);
SamplerState g_SamplerDescriptorHeap[] : register(s0, space3);

float4 sampleTexture2D(Texture2D<float4> texture, SamplerState samplerState, float2 texCoord)
{
#if REFII_SHADER_STAGE_PIXEL
    return texture.Sample(samplerState, texCoord);
#else
    return texture.SampleLevel(samplerState, texCoord, 0.0);
#endif
}

float4 sampleTexture3D(Texture3D<float4> texture, SamplerState samplerState, float3 texCoord)
{
#if REFII_SHADER_STAGE_PIXEL
    return texture.Sample(samplerState, texCoord);
#else
    return texture.SampleLevel(samplerState, texCoord, 0.0);
#endif
}

float4 sampleTextureCube(TextureCube<float4> texture, SamplerState samplerState, float3 texCoord)
{
#if REFII_SHADER_STAGE_PIXEL
    return texture.Sample(samplerState, texCoord);
#else
    return texture.SampleLevel(samplerState, texCoord, 0.0);
#endif
}

float4 tfetch1D(uint resourceDescriptorIndex, uint samplerDescriptorIndex, float texCoord)
{
    // Fallback through the 2D descriptor heap; many translated 1D fetch paths
    // originate from data that is bound as a 2D texture in practice.
    Texture2D<float4> texture = g_Texture2DDescriptorHeap[resourceDescriptorIndex];
    return sampleTexture2D(texture, g_SamplerDescriptorHeap[samplerDescriptorIndex], float2(texCoord, 0.5));
}

uint2 getTexture2DDimensions(Texture2D<float4> texture)
{
    uint2 dimensions;
    texture.GetDimensions(dimensions.x, dimensions.y);
    return dimensions;
}

float4 tfetch2D(uint resourceDescriptorIndex, uint samplerDescriptorIndex, float2 texCoord, float2 offset)
{
    Texture2D<float4> texture = g_Texture2DDescriptorHeap[resourceDescriptorIndex];
    return sampleTexture2D(texture, g_SamplerDescriptorHeap[samplerDescriptorIndex], texCoord + offset / getTexture2DDimensions(texture));
}

float2 getWeights2D(uint resourceDescriptorIndex, uint samplerDescriptorIndex, float2 texCoord, float2 offset)
{
    Texture2D<float4> texture = g_Texture2DDescriptorHeap[resourceDescriptorIndex];
    return select(isnan(texCoord), 0.0, frac(texCoord * getTexture2DDimensions(texture) + offset - 0.5));
}

float w0(float a)
{
    return (1.0f / 6.0f) * (a * (a * (-a + 3.0f) - 3.0f) + 1.0f);
}

float w1(float a)
{
    return (1.0f / 6.0f) * (a * a * (3.0f * a - 6.0f) + 4.0f);
}

float w2(float a)
{
    return (1.0f / 6.0f) * (a * (a * (-3.0f * a + 3.0f) + 3.0f) + 1.0f);
}

float w3(float a)
{
    return (1.0f / 6.0f) * (a * a * a);
}

float g0(float a)
{
    return w0(a) + w1(a);
}

float g1(float a)
{
    return w2(a) + w3(a);
}

float h0(float a)
{
    return -1.0f + w1(a) / (w0(a) + w1(a)) + 0.5f;
}

float h1(float a)
{
    return 1.0f + w3(a) / (w2(a) + w3(a)) + 0.5f;
}

float4 tfetch2DBicubic(uint resourceDescriptorIndex, uint samplerDescriptorIndex, float2 texCoord, float2 offset)
{
    Texture2D<float4> texture = g_Texture2DDescriptorHeap[resourceDescriptorIndex];
    SamplerState samplerState = g_SamplerDescriptorHeap[samplerDescriptorIndex];
    uint2 dimensions = getTexture2DDimensions(texture);
    
    float x = texCoord.x * dimensions.x + offset.x;
    float y = texCoord.y * dimensions.y + offset.y;

    x -= 0.5f;
    y -= 0.5f;
    float px = floor(x);
    float py = floor(y);
    float fx = x - px;
    float fy = y - py;

    float g0x = g0(fx);
    float g1x = g1(fx);
    float h0x = h0(fx);
    float h1x = h1(fx);
    float h0y = h0(fy);
    float h1y = h1(fy);

    float4 r =
        g0(fy) * (g0x * sampleTexture2D(texture, samplerState, float2(px + h0x, py + h0y) / float2(dimensions)) +
            g1x * sampleTexture2D(texture, samplerState, float2(px + h1x, py + h0y) / float2(dimensions))) +
        g1(fy) * (g0x * sampleTexture2D(texture, samplerState, float2(px + h0x, py + h1y) / float2(dimensions)) +
            g1x * sampleTexture2D(texture, samplerState, float2(px + h1x, py + h1y) / float2(dimensions)));

    return r;
}

float4 tfetch3D(uint resourceDescriptorIndex, uint samplerDescriptorIndex, float3 texCoord)
{
    return sampleTexture3D(g_Texture3DDescriptorHeap[resourceDescriptorIndex], g_SamplerDescriptorHeap[samplerDescriptorIndex], texCoord);
}

struct CubeMapData
{
    float3 cubeMapDirections[2];
    uint cubeMapIndex;
};

float4 tfetchCube(uint resourceDescriptorIndex, uint samplerDescriptorIndex, float3 texCoord, inout CubeMapData cubeMapData)
{
    return sampleTextureCube(
        g_TextureCubeDescriptorHeap[resourceDescriptorIndex],
        g_SamplerDescriptorHeap[samplerDescriptorIndex],
        cubeMapData.cubeMapDirections[texCoord.z]);
}

float4 tfetchR11G11B10(uint4 value)
{
    if (g_SpecConstants() & SPEC_CONSTANT_R11G11B10_NORMAL)
    {
        return float4(
            (value.x & 0x00000400 ? -1.0 : 0.0) + ((value.x & 0x3FF) / 1024.0),
            (value.x & 0x00200000 ? -1.0 : 0.0) + (((value.x >> 11) & 0x3FF) / 1024.0),
            (value.x & 0x80000000 ? -1.0 : 0.0) + (((value.x >> 22) & 0x1FF) / 512.0),
            0.0);
    }
    else
    {
        return asfloat(value);
    }
}

float4 tfetchTexcoord(uint swappedTexcoords, float4 value, uint semanticIndex)
{
    return (swappedTexcoords & (1ull << semanticIndex)) != 0 ? value.yxwz : value;
}

float4 cube(float4 value, inout CubeMapData cubeMapData)
{
    uint index = cubeMapData.cubeMapIndex;
    cubeMapData.cubeMapDirections[index] = value.xyz;
    ++cubeMapData.cubeMapIndex;
    
    return float4(0.0, 0.0, 0.0, index);
}

float4 dst(float4 src0, float4 src1)
{
    float4 dest;
    dest.x = 1.0;
    dest.y = src0.y * src1.y;
    dest.z = src0.z;
    dest.w = src1.w;
    return dest;
}

float4 max4(float4 src0)
{
    return max(max(src0.x, src0.y), max(src0.z, src0.w));
}

float2 getPixelCoord(uint resourceDescriptorIndex, float2 texCoord)
{
    return getTexture2DDimensions(g_Texture2DDescriptorHeap[resourceDescriptorIndex]) * texCoord;
}

float computeMipLevel(float2 pixelCoord)
{
    float2 dx = ddx(pixelCoord);
    float2 dy = ddy(pixelCoord);
    float deltaMaxSqr = max(dot(dx, dx), dot(dy, dy));
    return max(0.0, 0.5 * log2(deltaMaxSqr));
}

#endif

#endif
