#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <algorithm>
#include <cstdint>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
uint32_t ClampGuestTextureExtent(uint32_t extent) {
  return std::clamp<uint32_t>(extent, 1, 4096);
}

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
    case xenos::TextureFormat::k_16:
    case xenos::TextureFormat::k_16_EXPAND:
      return plume::RenderFormat::R16_UNORM;
    case xenos::TextureFormat::k_16_16:
    case xenos::TextureFormat::k_16_16_EXPAND:
      return plume::RenderFormat::R16G16_UNORM;
    case xenos::TextureFormat::k_16_16_16_16:
    case xenos::TextureFormat::k_16_16_16_16_EXPAND:
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
#endif

}  // namespace refii::renderer
