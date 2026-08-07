#include "runtime/refii_renderer/plume/fable/direct/fable_direct_index_data.h"

#include <algorithm>
#include <cstring>

namespace refii::renderer {

uint32_t NormalizeDirectIndexFormat(uint32_t stride_or_format) {
  return stride_or_format == 4u ? 4u : 2u;
}

uint32_t DirectReadIndexValue(const uint8_t* bytes, uint32_t stride,
                              bool byte_swap) {
  if (stride == 4u) {
    uint32_t index = 0;
    std::memcpy(&index, bytes, sizeof(index));
    if (byte_swap) {
      index = ((index & 0x000000FFu) << 24) |
              ((index & 0x0000FF00u) << 8) |
              ((index & 0x00FF0000u) >> 8) |
              ((index & 0xFF000000u) >> 24);
    }
    return index;
  }

  uint16_t index16 = 0;
  std::memcpy(&index16, bytes, sizeof(index16));
  if (byte_swap) {
    index16 = static_cast<uint16_t>((index16 << 8) | (index16 >> 8));
  }
  return index16;
}

void DirectSwapIndexBytes(uint8_t* data, size_t byte_size, uint32_t stride) {
  if (!data || (stride != 2u && stride != 4u)) {
    return;
  }
  for (size_t offset = 0; offset + stride <= byte_size; offset += stride) {
    if (stride == 4u) {
      std::swap(data[offset + 0u], data[offset + 3u]);
      std::swap(data[offset + 1u], data[offset + 2u]);
    } else {
      std::swap(data[offset + 0u], data[offset + 1u]);
    }
  }
}

bool DirectIndexBytesMaxIndex(const uint8_t* data, size_t byte_size,
                              uint32_t stride, uint32_t first_index,
                              uint32_t index_count, uint32_t& max_index,
                              bool byte_swap) {
  if (!data || byte_size == 0 || index_count == 0) {
    return false;
  }
  if (stride != 2u && stride != 4u) {
    stride = 2u;
  }
  const uint64_t begin = uint64_t(first_index) * uint64_t(stride);
  const uint64_t required = begin + uint64_t(index_count) * uint64_t(stride);
  if (required > byte_size) {
    return false;
  }
  max_index = 0;
  const uint8_t* bytes = data + begin;
  for (uint32_t i = 0; i < index_count; ++i) {
    const uint32_t index =
        DirectReadIndexValue(bytes + size_t(i) * stride, stride, byte_swap);
    max_index = std::max(max_index, index);
  }
  return true;
}

bool DirectIndexBufferLooksValid(const uint8_t* data, size_t byte_size,
                                 uint32_t stride, uint32_t first_index,
                                 uint32_t index_count,
                                 uint32_t vertex_capacity,
                                 uint32_t& max_index, bool byte_swap) {
  if (index_count == 0 || vertex_capacity == 0) {
    return true;
  }
  if (!DirectIndexBytesMaxIndex(data, byte_size, stride, first_index,
                                index_count, max_index, byte_swap)) {
    return true;
  }
  return max_index < vertex_capacity;
}

}  // namespace refii::renderer
