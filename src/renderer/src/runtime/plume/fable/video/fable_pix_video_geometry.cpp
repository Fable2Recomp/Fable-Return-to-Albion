#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_geometry.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"

#include <algorithm>
#include <bit>
#include <cmath>

namespace refii::renderer {
namespace {

float LoadFablePixVideoGuestF32(uint8_t* base, uint32_t guest_address) {
  return std::bit_cast<float>(bink::LoadGuestU32(base, guest_address));
}

}  // namespace

bool FablePixVideoBuildFrameVertices(float width,
                                     float height,
                                     float left,
                                     float top,
                                     std::array<float, 6>& vertices) {
  if (!std::isfinite(width) || !std::isfinite(height) ||
      !std::isfinite(left) || !std::isfinite(top) || width <= 0.0f ||
      height <= 0.0f || width > 32768.0f || height > 32768.0f ||
      std::fabs(left) > 32768.0f || std::fabs(top) > 32768.0f) {
    return false;
  }

  const float x0 = left - 0.5f;
  const float y0 = top - 0.5f;
  const float x1 = left + width - 0.5f;
  const float y1 = top + height - 0.5f;
  vertices = {x0, y0, x1, y0, x1, y1};
  return std::all_of(vertices.begin(), vertices.end(), [](float value) {
    return std::isfinite(value) && std::fabs(value) <= 32768.0f;
  });
}

bool FablePixVideoReadFrameVerticesFromArgs(uint8_t* base,
                                            uint32_t extent_pointer,
                                            uint32_t position_pointer,
                                            std::array<float, 6>& vertices) {
  if (!bink::IsReadableGuestByteRange(base, extent_pointer,
                                      sizeof(uint32_t) * 2u) ||
      !bink::IsReadableGuestByteRange(base, position_pointer,
                                      sizeof(uint32_t) * 2u)) {
    return false;
  }

  return FablePixVideoBuildFrameVertices(
      LoadFablePixVideoGuestF32(base, extent_pointer + 0u),
      LoadFablePixVideoGuestF32(base, extent_pointer + 4u),
      LoadFablePixVideoGuestF32(base, position_pointer + 0u),
      LoadFablePixVideoGuestF32(base, position_pointer + 4u), vertices);
}

uint32_t FablePixVideoSyntheticVertexBufferAddress(uint32_t packet,
                                                   uint32_t salt) {
  return SyntheticDirectScratchBufferAddress(0xFD000000u, packet, salt);
}

FablePixVideoUvScale FablePixVideoComputeUvScale(
    float decoded_width,
    float decoded_height,
    const std::array<float, 6>& vertices) {
  FablePixVideoUvScale scale;
  scale.decoded_width = decoded_width;
  scale.decoded_height = decoded_height;
  scale.output_width = std::fabs(vertices[2] - vertices[0]);
  scale.output_height = std::fabs(vertices[5] - vertices[1]);
  if (!std::isfinite(decoded_width) || !std::isfinite(decoded_height) ||
      !std::isfinite(scale.output_width) ||
      !std::isfinite(scale.output_height) || decoded_width <= 0.0f ||
      decoded_height <= 0.0f || scale.output_width <= 0.0f ||
      scale.output_height <= 0.0f) {
    return scale;
  }

  scale.u1 = scale.output_width / (decoded_width * 4.0f);
  scale.v1 = scale.output_height / (decoded_height * 4.0f);
  scale.valid = std::isfinite(scale.u1) && std::isfinite(scale.v1) &&
                scale.u1 > 0.0f && scale.v1 > 0.0f &&
                scale.u1 <= 4.0f && scale.v1 <= 4.0f;
  return scale;
}

FablePixVideoUvScale FablePixVideoReadUvScaleFromDecodedExtent(
    uint8_t* base,
    uint32_t decoded_extent_pointer,
    const std::array<float, 6>& vertices) {
  if (!bink::IsReadableGuestByteRange(base, decoded_extent_pointer,
                                      sizeof(uint32_t) * 2u)) {
    return {};
  }

  return FablePixVideoComputeUvScale(
      LoadFablePixVideoGuestF32(base, decoded_extent_pointer + 0u),
      LoadFablePixVideoGuestF32(base, decoded_extent_pointer + 4u), vertices);
}

FablePixVideoUvScale FablePixVideoReadUvScaleFromStack(
    uint8_t* base,
    uint32_t stack_pointer,
    const std::array<float, 6>& vertices) {
  constexpr uint32_t kFablePixRenderDecodedExtentStackOffset = 84u;
  if (stack_pointer > UINT32_MAX - kFablePixRenderDecodedExtentStackOffset) {
    return {};
  }
  const uint32_t stack_extent_pointer =
      stack_pointer + kFablePixRenderDecodedExtentStackOffset;
  if (!bink::IsReadableGuestByteRange(base, stack_extent_pointer,
                                      sizeof(uint32_t))) {
    return {};
  }

  // PixRenderVideoFrame receives the decoded-frame extent in the later PPC
  // stack arguments. The caller stores the pointer at SP+84 before branching
  // to sub_82BA83C0; IDA shows the native path using it as a28.
  const uint32_t extent_pointer = bink::LoadGuestU32(base, stack_extent_pointer);
  return FablePixVideoReadUvScaleFromDecodedExtent(base, extent_pointer,
                                                   vertices);
}

uint32_t FablePixVideoOutputWidthFromVertices(
    const std::array<float, 6>& vertices) {
  return static_cast<uint32_t>(
      std::max(1.0f, std::fabs(vertices[2] - vertices[0])));
}

uint32_t FablePixVideoOutputHeightFromVertices(
    const std::array<float, 6>& vertices) {
  return static_cast<uint32_t>(
      std::max(1.0f, std::fabs(vertices[5] - vertices[1])));
}

void FablePixVideoConfigureOutputState(RendererDrawEventSnapshot& event,
                                       uint32_t width,
                                       uint32_t height) {
  width = std::max<uint32_t>(1u, width);
  height = std::max<uint32_t>(1u, height);

  const float min_depth =
      event.viewport.valid ? event.viewport.min_depth : 0.0f;
  const float max_depth =
      event.viewport.valid ? event.viewport.max_depth : 1.0f;
  event.viewport = RendererViewportSnapshot{
      .x = 0.0f,
      .y = 0.0f,
      .width = static_cast<float>(width),
      .height = static_cast<float>(height),
      .min_depth = min_depth,
      .max_depth = max_depth,
      .valid = true,
  };
  event.scissor = RendererScissorSnapshot{
      .left = 0,
      .top = 0,
      .right = static_cast<int32_t>(width),
      .bottom = static_cast<int32_t>(height),
      .valid = true,
  };

  event.device_registers_valid = false;
  event.pm4_registers_valid = false;
}

void FablePixVideoConfigurePixelConstants(RendererDrawEventSnapshot& event) {
  auto& constants = event.pixel_shader_constants;
  constants.fill(0);

  const auto store_constant = [&](uint32_t reg, float x, float y, float z,
                                  float w) {
    const uint32_t dword = reg * 4u;
    if (dword + 3u >= constants.size()) {
      return;
    }
    constants[dword + 0u] = std::bit_cast<uint32_t>(x);
    constants[dword + 1u] = std::bit_cast<uint32_t>(y);
    constants[dword + 2u] = std::bit_cast<uint32_t>(z);
    constants[dword + 3u] = std::bit_cast<uint32_t>(w);
  };

  store_constant(253, -0.391449f, 2.017822f, 1.1641235f, 1.5957947f);
  store_constant(254, 0.529705f, -1.081669f, 1.1641235f, -0.81347656f);
  store_constant(255, -0.8706551f, 0.0f, 0.0f, 0.0f);

  constexpr uint32_t kFixedAlphaRegister = 31u;
  constexpr uint32_t kFixedAlphaDword = kFixedAlphaRegister * 4u;
  constexpr uint32_t kFloatOneBits = 0x3F800000u;
  if (kFixedAlphaDword + 3u < constants.size()) {
    constants[kFixedAlphaDword + 0u] = kFloatOneBits;
    constants[kFixedAlphaDword + 1u] = kFloatOneBits;
    constants[kFixedAlphaDword + 2u] = kFloatOneBits;
    constants[kFixedAlphaDword + 3u] = kFloatOneBits;
  }
}

}  // namespace refii::renderer
