#include "runtime/refii_renderer/plume/fable/direct/fable_direct_primitive8.h"

#include "runtime/plume/resource/plume_resource_manager_internal.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>

namespace refii::renderer {
namespace {

float ReadFloat2ComponentEndian(const uint8_t* data, size_t size,
                                uint32_t stride, uint32_t vertex,
                                uint32_t component, bool swap_dword) {
  uint32_t bits = 0;
  const size_t offset =
      static_cast<size_t>(vertex) * stride +
      static_cast<size_t>(component) * sizeof(float);
  if (!data || offset + sizeof(bits) > size) {
    return 0.0f;
  }
  std::memcpy(&bits, data + offset, sizeof(bits));
  if (swap_dword) {
    bits = ((bits & 0x000000FFu) << 24u) |
           ((bits & 0x0000FF00u) << 8u) |
           ((bits & 0x00FF0000u) >> 8u) |
           ((bits & 0xFF000000u) >> 24u);
  }
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

}  // namespace

bool BuildExpandedPrimitive8RectangleFromBytes(
    const uint8_t* data, size_t size,
    std::array<float, 12>& expanded_vertices, uint32_t stride) {
  if (!data || stride < sizeof(float) * 2u ||
      size < size_t(stride) * 2u + sizeof(float) * 2u) {
    return false;
  }

  struct Point {
    float x = 0.0f;
    float y = 0.0f;
  };
  for (uint32_t pass = 0; pass < 2u; ++pass) {
    const bool swap_dword = pass != 0;
    const std::array<Point, 3> points = {{
        {ReadFloat2ComponentEndian(data, size, stride, 0, 0, swap_dword),
         ReadFloat2ComponentEndian(data, size, stride, 0, 1, swap_dword)},
        {ReadFloat2ComponentEndian(data, size, stride, 1, 0, swap_dword),
         ReadFloat2ComponentEndian(data, size, stride, 1, 1, swap_dword)},
        {ReadFloat2ComponentEndian(data, size, stride, 2, 0, swap_dword),
         ReadFloat2ComponentEndian(data, size, stride, 2, 1, swap_dword)},
    }};
    if (!std::all_of(points.begin(), points.end(), [](const Point& point) {
          return std::isfinite(point.x) && std::isfinite(point.y) &&
                 std::fabs(point.x) <= 32768.0f &&
                 std::fabs(point.y) <= 32768.0f;
        })) {
      continue;
    }

    std::array<float, 3> opposite_edge_lengths = {};
    for (uint32_t vertex = 0; vertex < 3u; ++vertex) {
      const Point& edge_a = points[(vertex + 1u) % 3u];
      const Point& edge_b = points[(vertex + 2u) % 3u];
      const float dx = edge_b.x - edge_a.x;
      const float dy = edge_b.y - edge_a.y;
      opposite_edge_lengths[vertex] = dx * dx + dy * dy;
    }

    uint32_t elbow = 0;
    if (opposite_edge_lengths[1] > opposite_edge_lengths[elbow]) {
      elbow = 1;
    }
    if (opposite_edge_lengths[2] > opposite_edge_lengths[elbow]) {
      elbow = 2;
    }
    const Point& vertex0 = points[elbow];
    const Point& vertex1 = points[(elbow + 1u) % 3u];
    const Point& vertex2 = points[(elbow + 2u) % 3u];
    const Point missing = {
        vertex1.x - vertex0.x + vertex2.x,
        vertex1.y - vertex0.y + vertex2.y,
    };
    if (!std::isfinite(missing.x) || !std::isfinite(missing.y) ||
        std::fabs(missing.x) > 32768.0f ||
        std::fabs(missing.y) > 32768.0f) {
      continue;
    }

    // Match Xenia's rectangle-list ordering: treat the longest edge as the
    // diagonal, synthesize the fourth corner, then emit two list triangles.
    expanded_vertices = {vertex0.x, vertex0.y, vertex1.x, vertex1.y,
                         vertex2.x, vertex2.y, vertex2.x, vertex2.y,
                         vertex1.x, vertex1.y, missing.x, missing.y};
    return true;
  }
  return false;
}

bool BuildExpandedInlinePrimitive8Rectangle(
    const FableDirectInlineBuffer& inline_buffer,
    std::array<float, 12>& expanded_vertices) {
  const uint32_t stride = inline_buffer.stride_or_format;
  if (!inline_buffer.ready() || (stride != 8u && stride != 20u) ||
      inline_buffer.byte_size < uint64_t(stride) * 2u + sizeof(float) * 2u ||
      inline_buffer.data.size() < uint64_t(stride) * 2u + sizeof(float) * 2u) {
    return false;
  }
  return BuildExpandedPrimitive8RectangleFromBytes(
      inline_buffer.data.data(), inline_buffer.data.size(), expanded_vertices,
      stride);
}

bool BuildExpandedTrackedPrimitive8Rectangle(
    const PlumeTrackedResource& buffer, uint32_t offset,
    std::array<float, 12>& expanded_vertices) {
  const uint32_t stride = PlumeGuestBufferStrideOrFormat(buffer);
  if (!PlumeGuestBufferReady(buffer) || (stride != 8u && stride != 20u) ||
      PlumeGuestBufferByteSize(buffer) <
          uint64_t(offset) + uint64_t(stride) * 2u + sizeof(float) * 2u ||
      buffer.debug_upload_sample.size() <
          uint64_t(offset) + uint64_t(stride) * 2u + sizeof(float) * 2u) {
    return false;
  }
  return BuildExpandedPrimitive8RectangleFromBytes(
      buffer.debug_upload_sample.data() + offset,
      buffer.debug_upload_sample.size() - offset, expanded_vertices, stride);
}

bool InlinePrimitive8RectangleIsCollapsed(
    const FableDirectInlineBuffer& inline_buffer) {
  const uint32_t stride = inline_buffer.stride_or_format;
  if (!inline_buffer.ready() || (stride != 8u && stride != 20u) ||
      inline_buffer.byte_size < uint64_t(stride) * 2u + sizeof(float) * 2u ||
      inline_buffer.data.size() < uint64_t(stride) * 2u + sizeof(float) * 2u) {
    return true;
  }
  float min_x = std::numeric_limits<float>::infinity();
  float min_y = std::numeric_limits<float>::infinity();
  float max_x = -std::numeric_limits<float>::infinity();
  float max_y = -std::numeric_limits<float>::infinity();
  for (uint32_t vertex = 0; vertex < 3u; ++vertex) {
    const float x = ReadFloat2ComponentEndian(
        inline_buffer.data.data(), inline_buffer.data.size(), stride, vertex,
        0u, false);
    const float y = ReadFloat2ComponentEndian(
        inline_buffer.data.data(), inline_buffer.data.size(), stride, vertex,
        1u, false);
    if (!std::isfinite(x) || !std::isfinite(y)) {
      return true;
    }
    min_x = std::min(min_x, x);
    min_y = std::min(min_y, y);
    max_x = std::max(max_x, x);
    max_y = std::max(max_y, y);
  }
  return (max_x - min_x) < 1.0e-3f || (max_y - min_y) < 1.0e-3f;
}

bool ShouldExpandInlinePrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot,
    bool synthetic_private_rect) {
  if (slot != 0 || synthetic_private_rect || command.draw.draw.indexed ||
      command.draw.draw.primitive_type != 8u ||
      command.draw.draw.primitive_count != 3u ||
      !command.inline_vertex_buffers[slot].ready()) {
    return false;
  }

  const FableDirectInlineBuffer& inline_buffer =
      command.inline_vertex_buffers[slot];
  return !InlinePrimitive8RectangleIsCollapsed(inline_buffer);
}

bool ShouldExpandPrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot,
    bool synthetic_private_rect, uint32_t stride) {
  if (slot != 0 || synthetic_private_rect || command.draw.draw.indexed ||
      command.draw.draw.primitive_type != 8u ||
      command.draw.draw.primitive_count != 3u) {
    return false;
  }
  return stride == 8u || stride == 20u;
}

}  // namespace refii::renderer
