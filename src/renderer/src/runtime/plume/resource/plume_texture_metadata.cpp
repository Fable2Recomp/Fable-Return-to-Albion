#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/cvars.h"

#include <algorithm>
#include <array>
#include <cstdio>
#include <memory>

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME
bool SameAspectRatio(uint32_t lhs_width, uint32_t lhs_height,
                     uint32_t rhs_width, uint32_t rhs_height) {
  return lhs_width != 0 && lhs_height != 0 && rhs_width != 0 &&
         rhs_height != 0 &&
         uint64_t(lhs_width) * uint64_t(rhs_height) ==
             uint64_t(rhs_width) * uint64_t(lhs_height);
}

bool FablePromotedRenderSurfaceExtent(uint32_t guest_width,
                                      uint32_t guest_height,
                                      uint32_t& promoted_width,
                                      uint32_t& promoted_height) {
  const uint32_t output_width = static_cast<uint32_t>(
      std::max(1, RendererOutputWidth()) * std::max(1, RendererInternalScale()));
  const uint32_t output_height = static_cast<uint32_t>(
      std::max(1, RendererOutputHeight()) * std::max(1, RendererInternalScale()));
  if (output_width == guest_width && output_height == guest_height) {
    return false;
  }
  if (output_width < guest_width || output_height < guest_height) {
    return false;
  }

  auto set_scaled_output = [&](uint32_t divisor) {
    promoted_width = std::max(1u, output_width / std::max(1u, divisor));
    promoted_height = std::max(1u, output_height / std::max(1u, divisor));
    return promoted_width != guest_width || promoted_height != guest_height;
  };

  // Fable's D3D9 path still describes its main render/display surfaces with the
  // original guest extents. In replacement mode, Plume should own those as
  // output-sized render targets instead of paying a final scaler blit forever.
  const bool output_is_16x9 =
      uint64_t(output_width) * 9ull == uint64_t(output_height) * 16ull;
  const bool native_720p_surface =
      guest_width == 1280u && guest_height == 720u &&
      SameAspectRatio(guest_width, guest_height, output_width, output_height);
  const bool title_display_surface =
      guest_width == 1280u && guest_height == 512u && output_is_16x9;
  if (native_720p_surface || title_display_surface) {
    return set_scaled_output(1u);
  }

  // The title/menu compositor builds a private-rect chain at 280/560/1120
  // guest extents. Promote only render/depth surfaces here; uploaded textures
  // keep their authored dimensions.
  const bool title_full_surface =
      guest_width == 1120u && (guest_height == 720u || guest_height == 576u);
  if (title_full_surface) {
    return set_scaled_output(1u);
  }
  if (guest_width == 560u && guest_height == 360u) {
    return set_scaled_output(2u);
  }
  if (guest_width == 280u && guest_height == 180u) {
    return set_scaled_output(4u);
  }

  return false;
}

bool ShouldPromoteFableRenderSurfaceToOutputExtent(
    const PlumeTrackedResource& tracked) {
  if (!IsSurfaceResource(tracked.kind) ||
      (!tracked.texture_render_target && !tracked.texture_depth_target)) {
    return false;
  }

  uint32_t promoted_width = 0;
  uint32_t promoted_height = 0;
  return FablePromotedRenderSurfaceExtent(
      tracked.texture_width, tracked.texture_height, promoted_width,
      promoted_height);
}

void PromoteFableRenderSurfaceToOutputExtent(PlumeTrackedResource& tracked) {
  uint32_t promoted_width = 0;
  uint32_t promoted_height = 0;
  if (!ShouldPromoteFableRenderSurfaceToOutputExtent(tracked) ||
      !FablePromotedRenderSurfaceExtent(
          tracked.texture_width, tracked.texture_height, promoted_width,
          promoted_height)) {
    return;
  }
  tracked.texture_width = promoted_width;
  tracked.texture_height = promoted_height;
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
}

void PromoteFableParentBackedTextureProxyToOutputExtent(
    const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      tracked.parent_address == 0 || tracked.texture_guest_base != 0 ||
      tracked.texture_guest_mips != 0) {
    return;
  }

  uint32_t promoted_width = 0;
  uint32_t promoted_height = 0;
  if (!FablePromotedRenderSurfaceExtent(
          tracked.texture_width, tracked.texture_height, promoted_width,
          promoted_height)) {
    return;
  }

  tracked.texture_width = promoted_width;
  tracked.texture_height = promoted_height;
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
}

bool FableTextureUploadLooksLikeDecodedRgba(
    const RendererTrackedResourceSnapshot& snapshot,
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch,
    uint32_t width, uint32_t height, uint32_t depth) {
  namespace xenos = rex::graphics::xenos;
  if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
      snapshot.upload_data.empty() || width == 0 || height == 0 ||
      depth == 0) {
    return false;
  }

  switch (fetch.format) {
    case xenos::TextureFormat::k_DXT2_3:
    case xenos::TextureFormat::k_DXT4_5:
    case xenos::TextureFormat::k_DXT2_3_AS_16_16_16_16:
    case xenos::TextureFormat::k_DXT4_5_AS_16_16_16_16:
      break;
    default:
      return false;
  }

  const uint64_t rgba_bytes =
      uint64_t(width) * uint64_t(height) * uint64_t(depth) * 4ull;
  return rgba_bytes != 0 && snapshot.upload_data.size() >= rgba_bytes;
}

plume::RenderFormat DecodeFableDepthSurfaceFormat(
    const RendererTrackedSurfaceSnapshot& surface,
    uint32_t surface_d3d_format) {
  const uint32_t d3d_format =
      surface_d3d_format != 0 ? (surface_d3d_format & 0x3Fu)
                              : (surface.d3d_format & 0x3Fu);
  return ConvertFableD3DDepthFormat(d3d_format);
}

void DecodeFableTextureMetadata(const RendererTrackedResourceSnapshot& snapshot,
                                PlumeTrackedResource& tracked) {
  tracked.kind = snapshot.kind;
  tracked.guest_address = snapshot.guest_address;
  tracked.parent_address = snapshot.parent_address;
  tracked.size = IsSurfaceResource(snapshot.kind) ? snapshot.surface.byte_size
                                                  : snapshot.texture.byte_size;
  tracked.stride_or_format =
      IsSurfaceResource(snapshot.kind) ? snapshot.surface.pitch_msaa : 0;
  tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
  tracked.texture_width = 1;
  tracked.texture_height = 1;
  tracked.texture_depth = 1;
  tracked.texture_mip_levels = 1;
  tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  tracked.texture_view_dimension = plume::RenderTextureViewDimension::TEXTURE_2D;

  namespace xenos = rex::graphics::xenos;

  if (IsSurfaceResource(snapshot.kind)) {
    const RendererTrackedSurfaceSnapshot& surface = snapshot.surface;
    const uint32_t packed_extent = surface.packed_extent;
    const uint32_t byte_size = surface.byte_size;
    const uint32_t render_target_info = surface.render_target_info;
    const uint32_t depth_stencil_info = surface.depth_stencil_info;
    if (packed_extent != 0) {
      uint32_t decoded_width = 0;
      uint32_t decoded_height = 0;
      uint32_t decoded_depth = 1;
      if (DecodeFableSurfaceExtentFromPacked(
              packed_extent, byte_size, decoded_width,
              decoded_height, &decoded_depth)) {
        tracked.texture_width = ClampGuestTextureExtent(decoded_width);
        tracked.texture_height = ClampGuestTextureExtent(decoded_height);
        tracked.texture_depth = ClampGuestTextureExtent(decoded_depth);
      }
    } else {
      tracked.texture_width =
          ClampGuestTextureExtent(std::max<uint32_t>(byte_size, 1u));
      tracked.texture_height = ClampGuestTextureExtent(
          std::max<uint32_t>(surface.pitch_msaa, 1u));
    }
    if (tracked.texture_depth == 0) {
      tracked.texture_depth = 1;
    }
    tracked.texture_mip_levels = 1;
    const uint32_t surface_d3d_format =
        surface.d3d_format != 0
            ? (surface.d3d_format & 0x3Fu)
            : (depth_stencil_info != 0 ? (depth_stencil_info & 0x3Fu)
                                       : (render_target_info & 0x3Fu));
    tracked.texture_guest_format = surface_d3d_format;
    if (IsDepthSurfaceResource(snapshot.kind)) {
      tracked.texture_format =
          DecodeFableDepthSurfaceFormat(surface, surface_d3d_format);
    } else {
      const auto color_format =
          static_cast<xenos::ColorRenderTargetFormat>(
              (render_target_info >> 16u) & 0xFu);
      tracked.texture_format = ConvertXenosColorRenderTargetFormat(color_format);
    }
    if (!IsDepthSurfaceResource(snapshot.kind) &&
        !IsUsablePlumeColorTargetFormat(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    }
    tracked.texture_render_target =
        snapshot.kind == RendererTrackedResourceKind::kSurface;
    tracked.texture_depth_target =
        snapshot.kind == RendererTrackedResourceKind::kDepthSurface;
    PromoteFableRenderSurfaceToOutputExtent(tracked);
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    return;
  }

  const std::array<uint32_t, 6> fetch_dwords =
      PreferredTextureFetchDwords(snapshot.texture);
  tracked.texture_fetch_dwords = fetch_dwords;

  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = fetch_dwords[0];
  fetch.dword_1 = fetch_dwords[1];
  fetch.dword_2 = fetch_dwords[2];
  fetch.dword_3 = fetch_dwords[3];
  fetch.dword_4 = fetch_dwords[4];
  fetch.dword_5 = fetch_dwords[5];

  if (fetch.type != xenos::FetchConstantType::kTexture &&
      fetch.type != xenos::FetchConstantType::kInvalidTexture) {
    return;
  }

  tracked.texture_guest_base = fetch.base_address << 12;
  tracked.texture_guest_mips = fetch.mip_address << 12;
  tracked.texture_guest_pitch = fetch.pitch << 5;
  tracked.texture_guest_format = static_cast<uint32_t>(fetch.format);
  tracked.texture_guest_dimension = static_cast<uint32_t>(fetch.dimension);
  tracked.texture_guest_tiled = fetch.tiled != 0;
  tracked.texture_format = ConvertXenosTextureFormat(fetch.format);

  const bool cube = fetch.dimension == xenos::DataDimension::kCube;
  if (fetch.dimension == xenos::DataDimension::k1D) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_1D;
  } else if (fetch.dimension == xenos::DataDimension::k3D) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_3D;
  } else {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
  }
  tracked.texture_view_dimension =
      ViewDimensionForTextureDimension(tracked.texture_dimension, cube);

  if (fetch.dimension == xenos::DataDimension::k1D) {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_1d.width + 1u);
  } else if (fetch.dimension == xenos::DataDimension::k3D) {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_3d.width + 1u);
    tracked.texture_height =
        ClampGuestTextureExtent(fetch.size_3d.height + 1u);
    tracked.texture_depth =
        ClampGuestTextureExtent(fetch.size_3d.depth + 1u);
  } else {
    tracked.texture_width =
        ClampGuestTextureExtent(fetch.size_2d.width + 1u);
    tracked.texture_height =
        ClampGuestTextureExtent(fetch.size_2d.height + 1u);
    tracked.texture_depth = 1;
  }

  tracked.texture_mip_levels =
      fetch.mip_address != 0 && fetch.mip_max_level >= fetch.mip_min_level
          ? std::max<uint32_t>(
                uint32_t(fetch.mip_max_level - fetch.mip_min_level + 1), 1u)
          : 1u;
  if (FableTextureUploadLooksLikeDecodedRgba(
          snapshot, fetch, tracked.texture_width, tracked.texture_height,
          tracked.texture_depth)) {
    tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    tracked.texture_mip_levels = 1u;
  }
  PromoteFableParentBackedTextureProxyToOutputExtent(snapshot, tracked);
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
}

std::unique_ptr<plume::RenderTexture> CreateTextureForTrackedResource(
    plume::RenderDevice* device, const RendererTrackedResourceSnapshot& snapshot,
    PlumeTrackedResource& tracked) {
  if (!device || !IsImageResource(snapshot.kind)) {
    return nullptr;
  }

  const bool requested_color_target =
      snapshot.kind == RendererTrackedResourceKind::kTexture &&
      tracked.texture_render_target && !tracked.texture_depth_target;
  DecodeFableTextureMetadata(snapshot, tracked);
  tracked.texture_render_target =
      snapshot.kind == RendererTrackedResourceKind::kSurface ||
      requested_color_target;
  tracked.texture_depth_target =
      snapshot.kind == RendererTrackedResourceKind::kDepthSurface;

  if (snapshot.kind == RendererTrackedResourceKind::kSurface &&
      snapshot.surface.packed_extent != 0) {
    uint32_t surface_width = 0;
    uint32_t surface_height = 0;
    if (DecodeFableSurfaceExtentFromPacked(snapshot.surface.packed_extent,
                                           snapshot.surface.byte_size,
                                           surface_width, surface_height) &&
        !IsPlausibleFableColorRenderTargetExtent(surface_width,
                                                surface_height)) {
      return nullptr;
    }
  }

  plume::RenderTextureDesc desc;
  if (IsDepthSurfaceResource(snapshot.kind)) {
    tracked.texture_dimension = plume::RenderTextureDimension::TEXTURE_2D;
    tracked.texture_view_dimension =
        plume::RenderTextureViewDimension::TEXTURE_2D;
    tracked.texture_depth = 1;
    if (!plume::RenderFormatIsDepth(tracked.texture_format)) {
      tracked.texture_format =
          DecodeFableDepthSurfaceFormat(snapshot.surface,
                                        tracked.texture_guest_format);
    }
    if (!IsUsablePlumeTextureFormat(tracked.texture_format) ||
        !plume::RenderFormatIsDepth(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::D32_FLOAT_S8_UINT;
    }
    PromoteFableRenderSurfaceToOutputExtent(tracked);
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    desc = plume::RenderTextureDesc::DepthTarget(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureFormat(tracked));
  } else if (snapshot.kind == RendererTrackedResourceKind::kSurface) {
    if (!IsUsablePlumeColorTargetFormat(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    }
    PromoteFableRenderSurfaceToOutputExtent(tracked);
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    desc = plume::RenderTextureDesc::ColorTarget(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureFormat(tracked));
  } else if (requested_color_target &&
             tracked.texture_dimension ==
                 plume::RenderTextureDimension::TEXTURE_2D &&
             tracked.texture_view_dimension ==
                 plume::RenderTextureViewDimension::TEXTURE_2D) {
    if (!IsUsablePlumeColorTargetFormat(tracked.texture_format)) {
      tracked.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
    }
    tracked.texture_depth = 1;
    RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
    desc = plume::RenderTextureDesc::ColorTarget(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureFormat(tracked));
  } else if (tracked.texture_view_dimension ==
             plume::RenderTextureViewDimension::TEXTURE_CUBE) {
    desc = plume::RenderTextureDesc::Texture(
        plume::RenderTextureDimension::TEXTURE_2D,
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked), 1, 1,
        6, PlumeGuestTextureFormat(tracked),
        plume::RenderTextureFlag::CUBE);
  } else if (tracked.texture_dimension ==
             plume::RenderTextureDimension::TEXTURE_3D) {
    desc = plume::RenderTextureDesc::Texture3D(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
        PlumeGuestTextureDepth(tracked), 1, PlumeGuestTextureFormat(tracked));
  } else if (tracked.texture_dimension ==
             plume::RenderTextureDimension::TEXTURE_1D) {
    desc = plume::RenderTextureDesc::Texture1D(
        PlumeGuestTextureWidth(tracked), 1, PlumeGuestTextureFormat(tracked));
  } else {
    desc = plume::RenderTextureDesc::Texture2D(
        PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked), 1,
        PlumeGuestTextureFormat(tracked));
  }

  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
  return device->createTexture(desc);
}

void NameTrackedTexture(plume::RenderTexture* texture,
                        const RendererTrackedResourceSnapshot& snapshot,
                        const PlumeTrackedResource& tracked) {
  if (!texture) {
    return;
  }

  char name[160] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable %s guest=0x%08X base=0x%08X %ux%ux%u mips=%u fmt=%u gfmt=%u pitch=%u %s%s%s",
                TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
                PlumeGuestTextureBaseAddress(tracked),
                PlumeGuestTextureWidth(tracked),
                PlumeGuestTextureHeight(tracked),
                PlumeGuestTextureDepth(tracked),
                tracked.texture_mip_levels,
                static_cast<uint32_t>(PlumeGuestTextureFormat(tracked)),
                PlumeGuestTextureGuestFormat(tracked),
                PlumeGuestTexturePitch(tracked),
                tracked.texture_guest_tiled ? "tiled " : "linear ",
                tracked.texture_upload_decoded ? "decoded " : "",
                tracked.texture_upload_pending ? "pending" : "");
  texture->setName(name);
}
#endif

}  // namespace refii::renderer
