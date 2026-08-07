#include "runtime/refii_renderer/plume/fable/video/fable_bink_vertex_transform.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace refii::renderer {

bool FableVideoInferBinkWrapperVertexExtent(
    const FableDirectInlineBuffer& vertex_buffer,
    uint32_t stride,
    uint32_t offset,
    float& width,
    float& height) {
  width = 0.0f;
  height = 0.0f;
  if (!vertex_buffer.ready() || stride < 20u || offset == UINT32_MAX ||
      offset >= vertex_buffer.byte_size) {
    return false;
  }

  float max_x = 0.0f;
  float max_y = 0.0f;
  const uint32_t vertex_count =
      std::min<uint32_t>((vertex_buffer.byte_size - offset) / stride, 4u);
  for (uint32_t vertex = 0; vertex < vertex_count; ++vertex) {
    const uint32_t base = offset + vertex * stride;
    float x = 0.0f;
    float y = 0.0f;
    std::memcpy(&x, vertex_buffer.data.data() + base + 0u, sizeof(x));
    std::memcpy(&y, vertex_buffer.data.data() + base + 4u, sizeof(y));
    if (!std::isfinite(x) || !std::isfinite(y)) {
      return false;
    }
    max_x = std::max(max_x, x);
    max_y = std::max(max_y, y);
  }

  if (max_x <= 1.0f || max_y <= 1.0f) {
    return false;
  }
  width = max_x;
  height = max_y;
  return true;
}

void FableVideoApplyBinkWrapperVertexTransform(
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    float width,
    float height) {
  if (!std::isfinite(width) || !std::isfinite(height) || width <= 0.0f ||
      height <= 0.0f) {
    // CONTENT-keyed last resort, deliberately NOT the guest display dims:
    // Bink movies are authored at a fixed 1280x720 content resolution
    // regardless of the native-resolution override. The only caller
    // (ApplyExactBinkWrapperVertexTransformLocked) infers the real extent
    // from the draw's vertex data and falls back to the live guest display
    // dims itself, so this branch fires only for non-finite garbage.
    width = 1280.0f;
    height = 720.0f;
  }

  const auto store = [&](uint32_t constant_index, uint32_t component,
                         float value) {
    const uint32_t dword = constant_index * 4u + component;
    if (dword < constants.size()) {
      std::memcpy(&constants[dword], &value, sizeof(value));
    }
  };

  store(0, 0, 2.0f / width);
  store(0, 1, 0.0f);
  store(0, 2, 0.0f);
  store(0, 3, -1.0f);
  store(1, 0, 0.0f);
  store(1, 1, -2.0f / height);
  store(1, 2, 0.0f);
  store(1, 3, 1.0f);
  store(2, 0, 0.0f);
  store(2, 1, 0.0f);
  store(2, 2, 1.0f);
  store(2, 3, 0.0f);
  store(3, 0, 0.0f);
  store(3, 1, 0.0f);
  store(3, 2, 0.0f);
  store(3, 3, 1.0f);
}

}  // namespace refii::renderer
