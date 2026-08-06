#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/hooks/general/fable_hook_stack.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"

#include <algorithm>
#include <bit>
#include <cmath>

#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/xenos.h>

namespace refii::renderer {
namespace {

bool IsPlausiblePrivateTextureRectangleSurfaceExtent(uint32_t width,
                                                     uint32_t height) {
  constexpr uint32_t kMaxFableSurfaceExtent = 2048;
  constexpr uint64_t kMaxFableSurfacePixels =
      static_cast<uint64_t>(kMaxFableSurfaceExtent) *
      static_cast<uint64_t>(kMaxFableSurfaceExtent);
  return width != 0 && height != 0 && width <= kMaxFableSurfaceExtent &&
         height <= kMaxFableSurfaceExtent &&
         static_cast<uint64_t>(width) * height <= kMaxFableSurfacePixels;
}

bool FablePrivateTextureRectangleFetchShapeSupported(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;

  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
    case xenos::DataDimension::k3D:
      return !fetch.stacked;
    case xenos::DataDimension::k2DOrStacked:
      return !fetch.stacked;
    case xenos::DataDimension::kCube:
      return !fetch.stacked && fetch.size_2d.stack_depth == 5;
    default:
      return false;
  }
}

void NormalizePrivateTextureRectangleFetchForInfo(
    rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  if (fetch.mip_address == 0) {
    fetch.mip_min_level = 0;
    fetch.mip_max_level = 0;
    return;
  }

  if (fetch.mip_max_level < fetch.mip_min_level) {
    fetch.mip_max_level = fetch.mip_min_level;
  }
}

bool FablePrivateTextureRectangleFetchMipLayoutSupported(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  if (fetch.mip_min_level == 0) {
    return true;
  }

  return fetch.base_address == 0 || fetch.mip_address == 0 ||
         fetch.base_address == fetch.mip_address;
}

}  // namespace

uint32_t MixSyntheticDirectAddress(uint32_t value) {
  value ^= value >> 16u;
  value *= 0x7FEB352Du;
  value ^= value >> 15u;
  value *= 0x846CA68Bu;
  value ^= value >> 16u;
  return value;
}

uint32_t SyntheticDirectScratchBufferAddress(uint32_t namespace_base,
                                             uint32_t source,
                                             uint32_t salt) {
  const uint32_t mixed = MixSyntheticDirectAddress(
      source ^ (salt * 0x9E3779B9u) ^ (source >> 7u));
  return (namespace_base & 0xFF000000u) | (mixed & 0x00FFFFF0u);
}

uint32_t SyntheticPrivateTextureRectangleVertexBufferAddress(uint32_t source,
                                                            uint32_t salt) {
  return SyntheticDirectScratchBufferAddress(0xFE000000u, source, salt);
}

std::array<FablePrivateRectDirectVertex, 6>
BuildPrivateTextureRectangleDirectVertices(
    const std::array<float, 6>& corners,
    float u0,
    float v0,
    float u1,
    float v1) {
  const float x0 = corners[0];
  const float y0 = corners[1];
  const float x1 = corners[2];
  const float y1 = corners[5];

  const auto make_vertex = [](float x, float y, float u, float v) {
    FablePrivateRectDirectVertex vertex;
    vertex.position[0] = x - y;
    vertex.position[1] = 0.0f;
    vertex.position[2] = y;
    vertex.position[3] = 1.0f;
    vertex.texcoord0[0] = u;
    vertex.texcoord0[1] = v;
    vertex.texcoord0[2] = 0.0f;
    vertex.texcoord0[3] = 1.0f;
    vertex.texcoord1[0] = u;
    vertex.texcoord1[1] = v;
    vertex.texcoord1[2] = 0.0f;
    vertex.texcoord1[3] = 1.0f;
    return vertex;
  };

  return {
      make_vertex(x0, y0, u0, v0),
      make_vertex(x0, y1, u0, v1),
      make_vertex(x1, y0, u1, v0),
      make_vertex(x1, y0, u1, v0),
      make_vertex(x0, y1, u0, v1),
      make_vertex(x1, y1, u1, v1),
  };
}

std::array<FablePrivateRectDirectVertex, 3>
BuildPrivateTextureRectangleSingleTriangleDirectVertices(
    const std::array<float, 6>& corners,
    float u0,
    float v0,
    float u1,
    float v1) {
  const float x0 = corners[0];
  const float y0 = corners[1];
  const float x1 = corners[2];
  const float y1 = corners[5];
  const float width = x1 - x0;
  const float height = y1 - y0;
  const float du = u1 - u0;
  const float dv = v1 - v0;

  const auto make_vertex = [](float x, float y, float u, float v) {
    FablePrivateRectDirectVertex vertex;
    vertex.position[0] = x - y;
    vertex.position[1] = 0.0f;
    vertex.position[2] = y;
    vertex.position[3] = 1.0f;
    vertex.texcoord0[0] = u;
    vertex.texcoord0[1] = v;
    vertex.texcoord0[2] = 0.0f;
    vertex.texcoord0[3] = 1.0f;
    vertex.texcoord1[0] = u;
    vertex.texcoord1[1] = v;
    vertex.texcoord1[2] = 0.0f;
    vertex.texcoord1[3] = 1.0f;
    return vertex;
  };

  return {
      make_vertex(x0, y0, u0, v0),
      make_vertex(x0 + width * 2.0f, y0, u0 + du * 2.0f, v0),
      make_vertex(x0, y0 + height * 2.0f, u0, v0 + dv * 2.0f),
  };
}

FablePrivateRectUvBounds ComputePrivateTextureRectangleUvBounds(
    uint32_t source_width_value,
    uint32_t source_height_value,
    const std::array<float, 6>& corners,
    float source_x,
    float source_y) {
  FablePrivateRectUvBounds bounds;
  const float source_width =
      static_cast<float>(std::max(1u, source_width_value));
  const float source_height =
      static_cast<float>(std::max(1u, source_height_value));
  const float rect_width = std::fabs(corners[2] - corners[0]);
  const float rect_height = std::fabs(corners[5] - corners[1]);
  if (!std::isfinite(source_width) || !std::isfinite(source_height) ||
      !std::isfinite(rect_width) || !std::isfinite(rect_height) ||
      source_width <= 0.0f || source_height <= 0.0f || rect_width <= 0.0f ||
      rect_height <= 0.0f) {
    return bounds;
  }

  const float clamped_source_x = std::clamp(source_x, 0.0f, source_width);
  const float clamped_source_y = std::clamp(source_y, 0.0f, source_height);
  const float source_right =
      std::clamp(clamped_source_x + rect_width, 0.0f, source_width);
  const float source_bottom =
      std::clamp(clamped_source_y + rect_height, 0.0f, source_height);
  bounds.u0 = clamped_source_x / source_width;
  bounds.v0 = clamped_source_y / source_height;
  bounds.u1 = source_right / source_width;
  bounds.v1 = source_bottom / source_height;
  bounds.valid = bounds.u1 > bounds.u0 && bounds.v1 > bounds.v0 &&
                 std::isfinite(bounds.u0) && std::isfinite(bounds.v0) &&
                 std::isfinite(bounds.u1) && std::isfinite(bounds.v1);
  return bounds;
}

bool FablePrivateTextureRectangleScaleVerticesToTarget(
    FablePrivateRectTargetScaleInput input,
    std::array<float, 6>& vertices,
    FablePrivateRectTargetScaleResult* result) {
  if (result) {
    *result = {};
  }
  if (input.source_width == 0 || input.source_height == 0 ||
      input.target_width == 0 || input.target_height == 0 ||
      (input.target_width == input.source_width &&
       input.target_height == input.source_height)) {
    return false;
  }

  const float rect_width = std::fabs(vertices[2] - vertices[0]);
  const float rect_height = std::fabs(vertices[5] - vertices[1]);
  const float source_width = static_cast<float>(input.source_width);
  const float source_height = static_cast<float>(input.source_height);
  const float target_width = static_cast<float>(input.target_width);
  const float target_height = static_cast<float>(input.target_height);
  const float scale_x = target_width / source_width;
  const float scale_y = target_height / source_height;
  if (!std::isfinite(scale_x) || !std::isfinite(scale_y) ||
      scale_x <= 0.0f || scale_y <= 0.0f ||
      (std::fabs(scale_x - 1.0f) < 0.001f &&
       std::fabs(scale_y - 1.0f) < 0.001f)) {
    return false;
  }

  if (!std::isfinite(rect_width) || !std::isfinite(rect_height) ||
      rect_width <= 0.0f || rect_height <= 0.0f ||
      rect_width > source_width + 8.0f ||
      rect_height > source_height + 8.0f) {
    return false;
  }

  for (uint32_t i = 0; i < vertices.size(); i += 2u) {
    vertices[i + 0u] *= scale_x;
    vertices[i + 1u] *= scale_y;
  }
  const bool scaled =
      std::all_of(vertices.begin(), vertices.end(), [](float value) {
        return std::isfinite(value) && std::fabs(value) <= 32768.0f;
      });
  if (result) {
    result->scaled = scaled;
    result->scale_x = scale_x;
    result->scale_y = scale_y;
    result->source_width = input.source_width;
    result->source_height = input.source_height;
    result->target_width = input.target_width;
    result->target_height = input.target_height;
  }
  return scaled;
}

bool FablePrivateTextureRectangleExtentFromFetchDwords(
    const std::array<uint32_t, 6>& dwords,
    uint32_t& right,
    uint32_t& bottom) {
  right = 0;
  bottom = 0;
  if (!FableTextureFetchHasDwords(dwords)) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dwords[0];
  fetch.dword_1 = dwords[1];
  fetch.dword_2 = dwords[2];
  fetch.dword_3 = dwords[3];
  fetch.dword_4 = dwords[4];
  fetch.dword_5 = dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture) {
    return false;
  }

  if (fetch.dimension == xenos::DataDimension::k1D) {
    right = fetch.size_1d.width + 1u;
    bottom = 1u;
  } else if (fetch.dimension == xenos::DataDimension::k3D) {
    right = fetch.size_3d.width + 1u;
    bottom = fetch.size_3d.height + 1u;
  } else {
    right = fetch.size_2d.width + 1u;
    bottom = fetch.size_2d.height + 1u;
  }
  return right != 0 && bottom != 0;
}

bool FablePrivateTextureRectangleFetchLooksLikeTextureSource(
    const std::array<uint32_t, 6>& dwords) {
  if (!FableTextureFetchHasDwords(dwords)) {
    return false;
  }

  namespace xenos = rex::graphics::xenos;
  xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dwords[0];
  fetch.dword_1 = dwords[1];
  fetch.dword_2 = dwords[2];
  fetch.dword_3 = dwords[3];
  fetch.dword_4 = dwords[4];
  fetch.dword_5 = dwords[5];
  if (fetch.type != xenos::FetchConstantType::kTexture ||
      static_cast<uint32_t>(fetch.format) >= 64u) {
    return false;
  }

  const rex::graphics::FormatInfo* format_info =
      rex::graphics::FormatInfo::Get(fetch.format);
  if (!format_info || format_info->bytes_per_block() == 0 ||
      !FablePrivateTextureRectangleFetchShapeSupported(fetch)) {
    return false;
  }

  NormalizePrivateTextureRectangleFetchForInfo(fetch);
  if (!FablePrivateTextureRectangleFetchMipLayoutSupported(fetch)) {
    return false;
  }

  rex::graphics::TextureInfo texture;
  return rex::graphics::TextureInfo::Prepare(fetch, &texture);
}

bool FablePrivateTextureRectangleCornersFromRect(
    uint32_t left,
    uint32_t top,
    uint32_t right,
    uint32_t bottom,
    bool pixel_center_offset_enabled,
    std::array<float, 6>& corners) {
  right = (right + 7u) & ~7u;
  bottom = (bottom + 7u) & ~7u;
  if (right < left || bottom < top || right > 32768u || bottom > 32768u) {
    return false;
  }

  const float bias = pixel_center_offset_enabled ? 0.0f : 0.5f;
  const float x0 = static_cast<float>(left) - bias;
  const float y0 = static_cast<float>(top) - bias;
  const float x1 = static_cast<float>(right) - bias;
  const float y1 = static_cast<float>(bottom) - bias;

  // Fable primitive 8 feeds top-left, top-right, bottom-right. The translated
  // GUI path reconstructs the missing bottom-left corner later.
  corners = {x0, y0, x1, y0, x1, y1};
  return std::all_of(corners.begin(), corners.end(), [](float value) {
    return std::isfinite(value) && std::fabs(value) <= 32768.0f;
  });
}

float FablePrivateTextureSourceCoordinateFromRaw(uint32_t raw) {
  const float as_float = std::bit_cast<float>(raw);
  if (raw > 32768u && std::isfinite(as_float) && as_float >= 0.0f &&
      as_float <= 32768.0f) {
    return as_float;
  }
  if (raw <= 32768u) {
    return static_cast<float>(raw);
  }
  return 0.0f;
}

RendererScissorSnapshot FablePrivateTextureRectangleClearRectFromCorners(
    const RendererDrawEventSnapshot& event) {
  RendererScissorSnapshot rect;
  if (!event.private_texture_corners_valid) {
    return rect;
  }
  const auto& corners = event.private_texture_corners;
  const float min_x = std::min({corners[0], corners[2], corners[4]});
  const float max_x = std::max({corners[0], corners[2], corners[4]});
  const float min_y = std::min({corners[1], corners[3], corners[5]});
  const float max_y = std::max({corners[1], corners[3], corners[5]});
  if (!std::isfinite(min_x) || !std::isfinite(max_x) ||
      !std::isfinite(min_y) || !std::isfinite(max_y) ||
      max_x <= min_x || max_y <= min_y) {
    return rect;
  }
  rect.valid = true;
  rect.left = static_cast<int32_t>(std::floor(min_x));
  rect.top = static_cast<int32_t>(std::floor(min_y));
  rect.right = static_cast<int32_t>(std::ceil(max_x));
  rect.bottom = static_cast<int32_t>(std::ceil(max_y));
  return rect;
}

bool FablePrivateTextureRectangleTargetExtent(
    const RendererDrawEventSnapshot& event,
    uint32_t& width,
    uint32_t& height) {
  const auto accept_extent = [&](uint32_t candidate_width,
                                 uint32_t candidate_height) {
    if (!IsPlausiblePrivateTextureRectangleSurfaceExtent(candidate_width,
                                                        candidate_height)) {
      return false;
    }
    width = candidate_width;
    height = candidate_height;
    return true;
  };

  if (event.viewport.valid && event.viewport.width > 0.0f &&
      event.viewport.height > 0.0f) {
    const uint32_t viewport_width =
        static_cast<uint32_t>(std::lround(event.viewport.width));
    const uint32_t viewport_height =
        static_cast<uint32_t>(std::lround(event.viewport.height));
    if (accept_extent(viewport_width, viewport_height)) {
      return true;
    }
  }

  if (event.scissor.valid && event.scissor.right > event.scissor.left &&
      event.scissor.bottom > event.scissor.top) {
    const uint32_t scissor_width =
        static_cast<uint32_t>(event.scissor.right - event.scissor.left);
    const uint32_t scissor_height =
        static_cast<uint32_t>(event.scissor.bottom - event.scissor.top);
    if (accept_extent(scissor_width, scissor_height)) {
      return true;
    }
  }

  if (event.private_texture_rectangle &&
      event.private_texture_corners_valid) {
    const auto& corners = event.private_texture_corners;
    const uint32_t rect_width =
        static_cast<uint32_t>(std::lround(std::fabs(corners[2] - corners[0])));
    const uint32_t rect_height =
        static_cast<uint32_t>(std::lround(std::fabs(corners[5] - corners[1])));
    if (accept_extent(rect_width, rect_height)) {
      return true;
    }
  }

  return false;
}

uint32_t PackPrivateTextureRectangleSurfaceExtent(uint32_t width,
                                                  uint32_t height) {
  width = std::clamp(width, 1u, 0x4000u);
  height = std::clamp(height, 1u, 0x8000u);
  return ((width - 1u) << 18u) | ((height - 1u) << 3u);
}

uint32_t FablePrivateTextureRectangleSourceFetchAddress(
    const RendererHookArgs& args,
    uint8_t* base,
    uint32_t source,
    const FablePrivateTextureRectangleObserverGeometryCallbacks& callbacks) {
  if (!base || source == 0 || !callbacks.is_likely_guest_dword_address ||
      !callbacks.load_texture_fetch_dwords) {
    return 0;
  }

  constexpr uint32_t kTextureFetchOffset = 28u;
  const uint32_t object_fetch_address = source + kTextureFetchOffset;
  if (IsLikelyFableObjectAddress(source) &&
      callbacks.is_likely_guest_dword_address(object_fetch_address)) {
    const std::array<uint32_t, 6> dwords =
        callbacks.load_texture_fetch_dwords(base, object_fetch_address);
    if (FablePrivateTextureRectangleFetchLooksLikeTextureSource(dwords)) {
      return object_fetch_address;
    }
  }

  if (FableHookAddressNearStack(args, source) &&
      callbacks.is_likely_guest_dword_address(object_fetch_address) &&
      callbacks.is_likely_guest_dword_address(object_fetch_address + 20u)) {
    const std::array<uint32_t, 6> dwords =
        callbacks.load_texture_fetch_dwords(base, object_fetch_address);
    if (FablePrivateTextureRectangleFetchLooksLikeTextureSource(dwords)) {
      return object_fetch_address;
    }
  }

  return 0;
}

bool FablePrivateTextureRectangleReasonableVertexBuffer(
    uint8_t* base,
    uint32_t vertex_buffer) {
  if (!base || vertex_buffer == 0) {
    return false;
  }
  if (!IsLikelyFableGpuByteBufferRange(vertex_buffer, sizeof(float) * 6u) &&
      !IsLikelyFableObjectAddress(vertex_buffer)) {
    return false;
  }

  for (uint32_t i = 0; i < 6; ++i) {
    const uint32_t bits =
        bink::LoadGuestU32(base, vertex_buffer + i * sizeof(float));
    const float value = std::bit_cast<float>(bits);
    if (!std::isfinite(value) || std::fabs(value) > 32768.0f) {
      return false;
    }
  }
  return true;
}

uint32_t FablePrivateTextureRectangleFindVertexBuffer(
    uint8_t* base,
    uint32_t device_pointer) {
  if (!base || !IsLikelyFableObjectAddress(device_pointer)) {
    return 0;
  }

  const uint32_t command_end = bink::LoadGuestU32(base, device_pointer + 48u);
  if (!IsLikelyFableGpuByteBufferRange(command_end, sizeof(uint32_t))) {
    return 0;
  }

  constexpr uint32_t kPrivateRectangleVertexFetchPacket = 346112u;
  constexpr uint32_t kScanBackBytes = 1024u;
  const uint32_t scan_begin =
      command_end > kScanBackBytes ? command_end - kScanBackBytes : 0u;
  for (uint32_t command = command_end & ~3u; command >= scan_begin + 4u;
       command -= 4u) {
    if (!IsLikelyFableGpuByteBufferRange(command - 4u,
                                         sizeof(uint32_t) * 2u)) {
      break;
    }
    if (bink::LoadGuestU32(base, command - 4u) !=
        kPrivateRectangleVertexFetchPacket) {
      continue;
    }

    const uint32_t encoded_vertex_buffer = bink::LoadGuestU32(base, command);
    const uint32_t vertex_buffer =
        DecodeFableGpuBufferPointer(encoded_vertex_buffer);
    if (FablePrivateTextureRectangleReasonableVertexBuffer(base,
                                                           vertex_buffer)) {
      return vertex_buffer;
    }
  }
  return 0;
}

bool FablePrivateTextureRectangleBuildVerticesFromArgs(
    const RendererHookArgs& args,
    uint8_t* base,
    const FablePrivateTextureRectangleObserverGeometryCallbacks& callbacks,
    std::array<float, 6>& vertices) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) ||
      !callbacks.is_likely_guest_dword_address ||
      !callbacks.is_exact_stack_rect_return_address ||
      !callbacks.current_pixel_center_offset_render_state ||
      !callbacks.load_texture_fetch_dwords) {
    return false;
  }

  uint32_t left = 0;
  uint32_t top = 0;
  uint32_t right = 0;
  uint32_t bottom = 0;
  if (IsLikelyFableObjectAddress(args.r5) ||
      (callbacks.is_exact_stack_rect_return_address(args.lr) &&
       FableHookAddressNearStack(args, args.r5) &&
       callbacks.is_likely_guest_dword_address(args.r5 + 12u))) {
    left = bink::LoadGuestU32(base, args.r5 + 0u);
    top = bink::LoadGuestU32(base, args.r5 + 4u);
    right = bink::LoadGuestU32(base, args.r5 + 8u);
    bottom = bink::LoadGuestU32(base, args.r5 + 12u);
  } else {
    const uint32_t source_fetch_address =
        FablePrivateTextureRectangleSourceFetchAddress(args, base, args.r6,
                                                       callbacks);
    if (source_fetch_address == 0) {
      return false;
    }

    const std::array<uint32_t, 6> dwords =
        callbacks.load_texture_fetch_dwords(base, source_fetch_address);
    if (!FablePrivateTextureRectangleExtentFromFetchDwords(dwords, right,
                                                           bottom)) {
      return false;
    }
  }

  const bool pixel_center_offset_enabled =
      (callbacks.current_pixel_center_offset_render_state() & 1u) != 0;
  return FablePrivateTextureRectangleCornersFromRect(
      left, top, right, bottom, pixel_center_offset_enabled, vertices);
}

float FablePrivateTextureRectangleSourceCoordinate(
    uint8_t* base,
    uint32_t address,
    const FablePrivateTextureRectangleObserverGeometryCallbacks& callbacks) {
  if (!base || !callbacks.is_likely_guest_dword_address ||
      !callbacks.is_likely_guest_dword_address(address)) {
    return 0.0f;
  }
  return FablePrivateTextureSourceCoordinateFromRaw(
      bink::LoadGuestU32(base, address));
}

}  // namespace refii::renderer
