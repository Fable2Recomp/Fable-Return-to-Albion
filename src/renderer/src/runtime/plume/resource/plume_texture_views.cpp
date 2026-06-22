#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
namespace {

plume::RenderSwizzle MapXenosTextureSwizzle(uint32_t component) {
  switch (component) {
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_R:
      return plume::RenderSwizzle::R;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_G:
      return plume::RenderSwizzle::G;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_B:
      return plume::RenderSwizzle::B;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_A:
      return plume::RenderSwizzle::A;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_0:
      return plume::RenderSwizzle::ZERO;
    case rex::graphics::xenos::XE_GPU_TEXTURE_SWIZZLE_1:
      return plume::RenderSwizzle::ONE;
    case 6:
      return plume::RenderSwizzle::ZERO;
    case 7:
      return plume::RenderSwizzle::ONE;
    default:
      return plume::RenderSwizzle::IDENTITY;
  }
}

plume::RenderSwizzle ComposeTextureSwizzle(
    uint32_t component,
    const std::array<plume::RenderSwizzle, 4>& base_mapping) {
  if (component < base_mapping.size()) {
    return base_mapping[component];
  }
  return MapXenosTextureSwizzle(component);
}

std::array<plume::RenderSwizzle, 4> BaseXenosTextureComponentMapping(
    uint32_t guest_format) {
  using Swizzle = plume::RenderSwizzle;
  namespace xenos = rex::graphics::xenos;

  switch (static_cast<xenos::TextureFormat>(guest_format & 0x3Fu)) {
    case xenos::TextureFormat::k_8:
    case xenos::TextureFormat::k_8_A:
    case xenos::TextureFormat::k_8_B:
    case xenos::TextureFormat::k_24_8:
    case xenos::TextureFormat::k_24_8_FLOAT:
    case xenos::TextureFormat::k_16:
    case xenos::TextureFormat::k_16_EXPAND:
    case xenos::TextureFormat::k_16_FLOAT:
    case xenos::TextureFormat::k_32:
    case xenos::TextureFormat::k_32_FLOAT:
    case xenos::TextureFormat::k_32_AS_8:
    case xenos::TextureFormat::k_16_MPEG:
    case xenos::TextureFormat::k_8_INTERLACED:
    case xenos::TextureFormat::k_32_AS_8_INTERLACED:
    case xenos::TextureFormat::k_16_INTERLACED:
    case xenos::TextureFormat::k_16_MPEG_INTERLACED:
    case xenos::TextureFormat::k_DXT3A:
    case xenos::TextureFormat::k_DXT5A:
      return {Swizzle::R, Swizzle::R, Swizzle::R, Swizzle::R};

    case xenos::TextureFormat::k_8_8:
    case xenos::TextureFormat::k_16_16_EDRAM:
    case xenos::TextureFormat::k_16_16:
    case xenos::TextureFormat::k_16_16_EXPAND:
    case xenos::TextureFormat::k_16_16_FLOAT:
    case xenos::TextureFormat::k_32_32:
    case xenos::TextureFormat::k_32_32_FLOAT:
    case xenos::TextureFormat::k_32_AS_8_8:
    case xenos::TextureFormat::k_16_16_MPEG:
    case xenos::TextureFormat::k_32_AS_8_8_INTERLACED:
    case xenos::TextureFormat::k_16_16_MPEG_INTERLACED:
    case xenos::TextureFormat::k_DXN:
    case xenos::TextureFormat::k_CTX1:
      return {Swizzle::R, Swizzle::G, Swizzle::G, Swizzle::G};

    case xenos::TextureFormat::k_10_11_11:
    case xenos::TextureFormat::k_11_11_10:
    case xenos::TextureFormat::k_10_11_11_AS_16_16_16_16:
    case xenos::TextureFormat::k_11_11_10_AS_16_16_16_16:
    case xenos::TextureFormat::k_32_32_32_FLOAT:
      return {Swizzle::R, Swizzle::G, Swizzle::B, Swizzle::B};

    case xenos::TextureFormat::k_6_5_5:
      return {Swizzle::R, Swizzle::B, Swizzle::G, Swizzle::G};

    default:
      return {Swizzle::R, Swizzle::G, Swizzle::B, Swizzle::A};
  }
}

}  // namespace

plume::RenderTextureViewDimension ViewDimensionForTextureDimension(
    plume::RenderTextureDimension dimension, bool cube) {
  if (cube) {
    return plume::RenderTextureViewDimension::TEXTURE_CUBE;
  }
  switch (dimension) {
    case plume::RenderTextureDimension::TEXTURE_1D:
      return plume::RenderTextureViewDimension::TEXTURE_1D;
    case plume::RenderTextureDimension::TEXTURE_2D:
      return plume::RenderTextureViewDimension::TEXTURE_2D;
    case plume::RenderTextureDimension::TEXTURE_3D:
      return plume::RenderTextureViewDimension::TEXTURE_3D;
    case plume::RenderTextureDimension::UNKNOWN:
      break;
  }
  return plume::RenderTextureViewDimension::TEXTURE_2D;
}

plume::RenderTextureViewDesc TextureViewDescForDimension(
    plume::RenderTextureViewDimension dimension, plume::RenderFormat format) {
  switch (dimension) {
    case plume::RenderTextureViewDimension::TEXTURE_1D:
      return plume::RenderTextureViewDesc::Texture1D(format);
    case plume::RenderTextureViewDimension::TEXTURE_3D:
      return plume::RenderTextureViewDesc::Texture3D(format);
    case plume::RenderTextureViewDimension::TEXTURE_CUBE:
      return plume::RenderTextureViewDesc::TextureCube(format);
    case plume::RenderTextureViewDimension::TEXTURE_2D:
    case plume::RenderTextureViewDimension::UNKNOWN:
      break;
  }
  return plume::RenderTextureViewDesc::Texture2D(format);
}

plume::RenderComponentMapping XenosTextureComponentMapping(
    uint32_t guest_format, uint32_t fetch_swizzle) {
  const std::array<plume::RenderSwizzle, 4> base_mapping =
      BaseXenosTextureComponentMapping(guest_format);
  return plume::RenderComponentMapping(
      ComposeTextureSwizzle((fetch_swizzle >> 0u) & 0x7u, base_mapping),
      ComposeTextureSwizzle((fetch_swizzle >> 3u) & 0x7u, base_mapping),
      ComposeTextureSwizzle((fetch_swizzle >> 6u) & 0x7u, base_mapping),
      ComposeTextureSwizzle((fetch_swizzle >> 9u) & 0x7u, base_mapping));
}

plume::RenderComponentMapping IdentityTextureComponentMapping() {
  return plume::RenderComponentMapping(plume::RenderSwizzle::R,
                                       plume::RenderSwizzle::G,
                                       plume::RenderSwizzle::B,
                                       plume::RenderSwizzle::A);
}

bool FableSingleSourceVideoUsesDecodedChannels(uint32_t guest_format) {
  namespace xenos = rex::graphics::xenos;
  switch (static_cast<xenos::TextureFormat>(guest_format & 0x3Fu)) {
    case xenos::TextureFormat::k_8_8_8_8:
    case xenos::TextureFormat::k_8_8_8_8_A:
    case xenos::TextureFormat::k_8_8_8_8_AS_16_16_16_16:
    case xenos::TextureFormat::k_8_8_8_8_GAMMA_EDRAM:
    case xenos::TextureFormat::k_Cr_Y1_Cb_Y0_REP:
    case xenos::TextureFormat::k_Y1_Cr_Y0_Cb_REP:
      return true;
    default:
      return false;
  }
}

bool IsLikelyFableVideoNeutralChromaTexture(
    const RendererTrackedResourceSnapshot& snapshot,
    const PlumeTrackedResource& tracked) {
  namespace xenos = rex::graphics::xenos;
  return snapshot.kind == RendererTrackedResourceKind::kTexture &&
         PlumeGuestTextureWidth(tracked) == 1 && PlumeGuestTextureHeight(tracked) == 1 &&
         PlumeGuestTextureDepth(tracked) == 1 &&
         PlumeGuestTextureFormat(tracked) == plume::RenderFormat::R8G8B8A8_UNORM &&
         static_cast<xenos::TextureFormat>(PlumeGuestTextureGuestFormat(tracked) &
                                           0x3Fu) ==
             xenos::TextureFormat::k_8_8_8_8 &&
         snapshot.texture.raw_fetch_dwords[2] == 0 &&
         snapshot.texture.raw_fetch_dwords[4] == 3;
}

uint32_t PackTextureComponentMapping(
    const plume::RenderComponentMapping& mapping) {
  return static_cast<uint32_t>(mapping.r) |
         (static_cast<uint32_t>(mapping.g) << 4u) |
         (static_cast<uint32_t>(mapping.b) << 8u) |
         (static_cast<uint32_t>(mapping.a) << 12u);
}
#endif

}  // namespace refii::renderer
