#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <algorithm>
#include <cstdint>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
uint32_t ClampGuestTextureExtent(uint32_t extent) {
  return std::clamp<uint32_t>(extent, 1, 4096);
}

// *** A PER-AXIS CLAMP CANNOT CATCH A VOLUME EXPLOSION. ***
//
// The clamp above bounds each dimension independently, so it is structurally
// blind to a bad VOLUME. Capture refii_plume_frame9173 contained a single
// 1540x1811x972 R8G8B8A8 image — 10.1 GB, 92% of all texture memory in the
// frame, and with ZERO usage events: never bound, never sampled, never
// rendered to. Every one of those three axes is inside its own Xenos
// bitfield, so no per-dimension clamp could ever have fired: the guest packs
// a 3D texture extent as 11/11/10 bits (D3D_GetTextureDimensions 0x821FAB38,
// `clrlwi r10,r10,21` / `extrwi r10,r10,11,10` / `srwi r10,r10,22`), giving
// maxima of 2048/2048/1024 — and 1540/1811/972 all fit.
//
// The bound below is therefore a MEMORY-BUDGET invariant, not a shape
// heuristic. The console has 512 MB of unified memory in TOTAL (CPU + GPU),
// so no single guest resource can legitimately approach it. The largest real
// Fable 2 image is a 1920x1080 R16G16B16A16 scene target after 1080p
// promotion: 15.8 MB. 64 MB is four times that.
//
// kMaxGuestImageBytes is declared in plume_resource_manager_internal.h so the
// rejection log at the allocation funnel can quote the ceiling it enforced.

plume::RenderFormat ConvertXenosColorRenderTargetFormat(
    rex::graphics::xenos::ColorRenderTargetFormat format) {
  namespace xenos = rex::graphics::xenos;
  switch (xenos::GetStorageColorFormat(format)) {
    case xenos::ColorRenderTargetFormat::k_8_8_8_8:
    case xenos::ColorRenderTargetFormat::k_8_8_8_8_GAMMA:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case xenos::ColorRenderTargetFormat::k_2_10_10_10:
      return plume::RenderFormat::R10G10B10A2_UNORM;
    case xenos::ColorRenderTargetFormat::k_2_10_10_10_FLOAT:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    case xenos::ColorRenderTargetFormat::k_16_16:
      return plume::RenderFormat::R16G16_SNORM;
    case xenos::ColorRenderTargetFormat::k_16_16_16_16:
      return plume::RenderFormat::R16G16B16A16_SNORM;
    case xenos::ColorRenderTargetFormat::k_16_16_FLOAT:
      return plume::RenderFormat::R16G16_FLOAT;
    case xenos::ColorRenderTargetFormat::k_16_16_16_16_FLOAT:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    case xenos::ColorRenderTargetFormat::k_32_FLOAT:
      return plume::RenderFormat::R32_FLOAT;
    case xenos::ColorRenderTargetFormat::k_32_32_FLOAT:
      return plume::RenderFormat::R32G32_FLOAT;
    default:
      return plume::RenderFormat::R8G8B8A8_UNORM;
  }
}

plume::RenderFormat ConvertXenosTextureFormat(
    rex::graphics::xenos::TextureFormat format) {
  namespace xenos = rex::graphics::xenos;
  switch (format) {
    case xenos::TextureFormat::k_8:
    case xenos::TextureFormat::k_8_A:
    case xenos::TextureFormat::k_8_B:
      return plume::RenderFormat::R8_UNORM;
    case xenos::TextureFormat::k_8_8:
    case xenos::TextureFormat::k_CTX1:
      return plume::RenderFormat::R8G8_UNORM;
    case xenos::TextureFormat::k_8_8_8_8:
    case xenos::TextureFormat::k_8_8_8_8_A:
    case xenos::TextureFormat::k_8_8_8_8_AS_16_16_16_16:
    case xenos::TextureFormat::k_8_8_8_8_GAMMA_EDRAM:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case xenos::TextureFormat::k_1_5_5_5:
    case xenos::TextureFormat::k_5_6_5:
    case xenos::TextureFormat::k_4_4_4_4:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case xenos::TextureFormat::k_2_10_10_10:
    case xenos::TextureFormat::k_2_10_10_10_AS_16_16_16_16:
      return plume::RenderFormat::R10G10B10A2_UNORM;
    case xenos::TextureFormat::k_10_11_11:
    case xenos::TextureFormat::k_11_11_10:
    case xenos::TextureFormat::k_10_11_11_AS_16_16_16_16:
    case xenos::TextureFormat::k_11_11_10_AS_16_16_16_16:
      return plume::RenderFormat::R16G16B16A16_UNORM;
    case xenos::TextureFormat::k_DXT1:
    case xenos::TextureFormat::k_DXT1_AS_16_16_16_16:
      return plume::RenderFormat::BC1_UNORM;
    case xenos::TextureFormat::k_DXT2_3:
    case xenos::TextureFormat::k_DXT2_3_AS_16_16_16_16:
      return plume::RenderFormat::BC2_UNORM;
    case xenos::TextureFormat::k_DXT4_5:
    case xenos::TextureFormat::k_DXT4_5_AS_16_16_16_16:
      return plume::RenderFormat::BC3_UNORM;
    case xenos::TextureFormat::k_DXN:
      return plume::RenderFormat::BC5_UNORM;
    // *** THE _EXPAND FORMATS ARE FLOAT, NOT UNORM (fixed 2026-08-04). ***
    //
    // They used to share the UNORM case with their fixed-point namesakes, which
    // CLAMPED every value to [0,1]. The SDK states the mapping plainly —
    // rexglue-sdk/include/rex/graphics/pipeline/texture/info.h, `GetBaseFormat`:
    //     k_16_EXPAND          -> k_16_FLOAT
    //     k_16_16_EXPAND       -> k_16_16_FLOAT
    //     k_16_16_16_16_EXPAND -> k_16_16_16_16_FLOAT
    // "EXPAND" is the expanded-RANGE (float) form; it is not a fixed-point
    // variant. This is a stated contract, not an inference.
    //
    // MEASURED COST OF THE OLD MAPPING (capture frame9173, run 578). The scene
    // resolve destination `0x404A4F10` carries guest format **29 =
    // k_16_16_16_16_EXPAND** and we created it as R16G16B16A16_UNORM. Its dump
    // is pure white sky over pure black ground with NO GRADIENT ANYWHERE —
    // the exact signature of HDR clamped to [0,1]: everything above 1.0 pinned
    // to white, everything below pinned to black. The tonemap then samples that
    // saturated image and the composite comes out black.
    //
    // Note the two tables in this file disagreed: the COLOR RENDER TARGET path
    // maps k_16_16_16_16 to R16G16B16A16_SNORM while this TEXTURE path mapped
    // the same guest format to UNORM. Only the _EXPAND half is corrected here,
    // because that is the half the SDK states outright and the half the capture
    // proves is in play. The plain fixed-point k_16_16_16_16 texture case is
    // left alone deliberately — changing it needs its own evidence.
    case xenos::TextureFormat::k_16_EXPAND:
      return plume::RenderFormat::R16_FLOAT;
    case xenos::TextureFormat::k_16_16_EXPAND:
      return plume::RenderFormat::R16G16_FLOAT;
    case xenos::TextureFormat::k_16_16_16_16_EXPAND:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    case xenos::TextureFormat::k_16:
      return plume::RenderFormat::R16_UNORM;
    case xenos::TextureFormat::k_16_16:
      return plume::RenderFormat::R16G16_UNORM;
    case xenos::TextureFormat::k_16_16_16_16:
      return plume::RenderFormat::R16G16B16A16_UNORM;
    case xenos::TextureFormat::k_DXT3A:
      return plume::RenderFormat::R8_UNORM;
    case xenos::TextureFormat::k_DXT3A_AS_1_1_1_1:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    case xenos::TextureFormat::k_DXT5A:
      return plume::RenderFormat::BC4_UNORM;
    case xenos::TextureFormat::k_16_FLOAT:
      return plume::RenderFormat::R16_FLOAT;
    case xenos::TextureFormat::k_16_16_FLOAT:
      return plume::RenderFormat::R16G16_FLOAT;
    case xenos::TextureFormat::k_16_16_16_16_FLOAT:
      return plume::RenderFormat::R16G16B16A16_FLOAT;
    case xenos::TextureFormat::k_32:
      return plume::RenderFormat::R32_UINT;
    case xenos::TextureFormat::k_32_32:
      return plume::RenderFormat::R32G32_UINT;
    case xenos::TextureFormat::k_32_32_32_32:
      return plume::RenderFormat::R32G32B32A32_UINT;
    case xenos::TextureFormat::k_32_FLOAT:
      return plume::RenderFormat::R32_FLOAT;
    case xenos::TextureFormat::k_32_32_FLOAT:
      return plume::RenderFormat::R32G32_FLOAT;
    case xenos::TextureFormat::k_32_32_32_32_FLOAT:
      return plume::RenderFormat::R32G32B32A32_FLOAT;
    case xenos::TextureFormat::k_24_8:
    case xenos::TextureFormat::k_24_8_FLOAT:
      return plume::RenderFormat::R32_FLOAT;
    case xenos::TextureFormat::k_Cr_Y1_Cb_Y0_REP:
    case xenos::TextureFormat::k_Y1_Cr_Y0_Cb_REP:
      return plume::RenderFormat::R8G8B8A8_UNORM;
    default:
      return plume::RenderFormat::R8G8B8A8_UNORM;
  }
}

bool IsBlockCompressedFormat(plume::RenderFormat format) {
  switch (format) {
    case plume::RenderFormat::BC1_TYPELESS:
    case plume::RenderFormat::BC1_UNORM:
    case plume::RenderFormat::BC1_UNORM_SRGB:
    case plume::RenderFormat::BC2_TYPELESS:
    case plume::RenderFormat::BC2_UNORM:
    case plume::RenderFormat::BC2_UNORM_SRGB:
    case plume::RenderFormat::BC3_TYPELESS:
    case plume::RenderFormat::BC3_UNORM:
    case plume::RenderFormat::BC3_UNORM_SRGB:
    case plume::RenderFormat::BC4_TYPELESS:
    case plume::RenderFormat::BC4_UNORM:
    case plume::RenderFormat::BC4_SNORM:
    case plume::RenderFormat::BC5_TYPELESS:
    case plume::RenderFormat::BC5_UNORM:
    case plume::RenderFormat::BC5_SNORM:
    case plume::RenderFormat::BC6H_TYPELESS:
    case plume::RenderFormat::BC6H_UF16:
    case plume::RenderFormat::BC6H_SF16:
    case plume::RenderFormat::BC7_TYPELESS:
    case plume::RenderFormat::BC7_UNORM:
    case plume::RenderFormat::BC7_UNORM_SRGB:
      return true;
    default:
      return false;
  }
}

bool IsUsablePlumeTextureFormat(plume::RenderFormat format) {
  return format != plume::RenderFormat::UNKNOWN &&
         format != plume::RenderFormat::MAX;
}

bool IsUsablePlumeColorTargetFormat(plume::RenderFormat format) {
  return IsUsablePlumeTextureFormat(format) &&
         !plume::RenderFormatIsDepth(format) && !IsBlockCompressedFormat(format);
}

uint32_t TextureBlockWidth(plume::RenderFormat format) {
  return std::max<uint32_t>(1, plume::RenderFormatBlockWidth(format));
}

uint32_t TextureBlockHeight(plume::RenderFormat format) {
  return IsBlockCompressedFormat(format) ? 4u : 1u;
}

uint32_t TextureRowCount(plume::RenderFormat format, uint32_t height) {
  const uint32_t block_height = TextureBlockHeight(format);
  return std::max<uint32_t>(1, (height + block_height - 1u) / block_height);
}

uint32_t TextureTightRowBytes(plume::RenderFormat format, uint32_t width) {
  const uint32_t block_width = TextureBlockWidth(format);
  const uint32_t block_count =
      std::max<uint32_t>(1, (width + block_width - 1u) / block_width);
  return block_count * plume::RenderFormatSize(format);
}

uint32_t TextureRowWidthForPitch(plume::RenderFormat format,
                                 uint32_t row_pitch_bytes) {
  const uint32_t format_size =
      std::max<uint32_t>(1, plume::RenderFormatSize(format));
  const uint32_t block_width = TextureBlockWidth(format);
  return std::max<uint32_t>(block_width,
                            (row_pitch_bytes / format_size) * block_width);
}

uint64_t GuestImageAllocationBytes(plume::RenderFormat format, uint32_t width,
                                   uint32_t height, uint32_t depth,
                                   uint32_t array_size) {
  const uint64_t row_bytes = TextureTightRowBytes(format, std::max(width, 1u));
  const uint64_t rows = TextureRowCount(format, std::max(height, 1u));
  return row_bytes * rows * std::max(depth, 1u) * std::max(array_size, 1u);
}

bool ValidGuestImageAllocation(plume::RenderFormat format, uint32_t width,
                               uint32_t height, uint32_t depth,
                               uint32_t array_size, uint64_t* out_bytes) {
  const uint64_t bytes =
      GuestImageAllocationBytes(format, width, height, depth, array_size);
  if (out_bytes != nullptr) {
    *out_bytes = bytes;
  }
  return width != 0 && height != 0 && bytes != 0 &&
         bytes <= kMaxGuestImageBytes;
}

uint8_t ExpandUnormBits(uint32_t value, uint32_t bits) {
  const uint32_t max_value = (1u << bits) - 1u;
  return static_cast<uint8_t>((value * 255u + max_value / 2u) / max_value);
}

bool DecodePacked16XenosTexel(rex::graphics::xenos::TextureFormat format,
                              uint16_t texel, uint8_t* rgba) {
  namespace xenos = rex::graphics::xenos;
  switch (format) {
    case xenos::TextureFormat::k_1_5_5_5:
      rgba[0] = ExpandUnormBits(texel & 0x1Fu, 5);
      rgba[1] = ExpandUnormBits((texel >> 5u) & 0x1Fu, 5);
      rgba[2] = ExpandUnormBits((texel >> 10u) & 0x1Fu, 5);
      rgba[3] = (texel & 0x8000u) != 0 ? 0xFFu : 0x00u;
      return true;
    case xenos::TextureFormat::k_5_6_5:
      rgba[0] = ExpandUnormBits(texel & 0x1Fu, 5);
      rgba[1] = ExpandUnormBits((texel >> 5u) & 0x3Fu, 6);
      rgba[2] = ExpandUnormBits((texel >> 11u) & 0x1Fu, 5);
      rgba[3] = 0xFFu;
      return true;
    case xenos::TextureFormat::k_4_4_4_4:
      rgba[0] = ExpandUnormBits(texel & 0xFu, 4);
      rgba[1] = ExpandUnormBits((texel >> 4u) & 0xFu, 4);
      rgba[2] = ExpandUnormBits((texel >> 8u) & 0xFu, 4);
      rgba[3] = ExpandUnormBits((texel >> 12u) & 0xFu, 4);
      return true;
    default:
      return false;
  }
}

bool IsPacked16XenosTextureFormat(
    rex::graphics::xenos::TextureFormat format) {
  namespace xenos = rex::graphics::xenos;
  return format == xenos::TextureFormat::k_1_5_5_5 ||
         format == xenos::TextureFormat::k_5_6_5 ||
         format == xenos::TextureFormat::k_4_4_4_4;
}

bool DecodePackedDepthXenosTexel(rex::graphics::xenos::TextureFormat format,
                                 uint32_t texel, float* depth) {
  if (!depth) {
    return false;
  }
  namespace xenos = rex::graphics::xenos;
  const uint32_t depth24 = (texel >> 8u) & 0xFFFFFFu;
  switch (format) {
    case xenos::TextureFormat::k_24_8:
      *depth = xenos::UNorm24To32(depth24);
      return true;
    case xenos::TextureFormat::k_24_8_FLOAT:
      *depth = xenos::Float20e4To32(depth24) * 0.5f;
      return true;
    default:
      return false;
  }
}

bool IsPackedDepthXenosTextureFormat(
    rex::graphics::xenos::TextureFormat format) {
  namespace xenos = rex::graphics::xenos;
  return format == xenos::TextureFormat::k_24_8 ||
         format == xenos::TextureFormat::k_24_8_FLOAT;
}

bool IsCtx1DecodeConversion(rex::graphics::xenos::TextureFormat guest_format,
                            plume::RenderFormat host_format) {
  // CTX1 is the Xbox 360 two-channel normal-map block format (8-byte 4x4
  // blocks, DXT1-style endpoints/selectors). Vulkan has no CTX1 block format,
  // so the upload path decodes it to R8G8_UNORM texels. This predicate marks
  // that guest/host pairing as an intentional decode conversion so the
  // block-geometry mismatch check can tell it apart from an accidental
  // guest/host format disagreement.
  return guest_format == rex::graphics::xenos::TextureFormat::k_CTX1 &&
         host_format == plume::RenderFormat::R8G8_UNORM;
}
#endif

}  // namespace refii::renderer
