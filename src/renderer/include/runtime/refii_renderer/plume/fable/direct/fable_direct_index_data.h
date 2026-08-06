#pragma once

#include <cstddef>
#include <cstdint>

namespace refii::renderer {

uint32_t NormalizeDirectIndexFormat(uint32_t stride_or_format);

uint32_t DirectReadIndexValue(const uint8_t* bytes, uint32_t stride,
                              bool byte_swap);

void DirectSwapIndexBytes(uint8_t* data, size_t byte_size, uint32_t stride);

bool DirectIndexBytesMaxIndex(const uint8_t* data, size_t byte_size,
                              uint32_t stride, uint32_t first_index,
                              uint32_t index_count, uint32_t& max_index,
                              bool byte_swap = false);

bool DirectIndexBufferLooksValid(const uint8_t* data, size_t byte_size,
                                 uint32_t stride, uint32_t first_index,
                                 uint32_t index_count,
                                 uint32_t vertex_capacity,
                                 uint32_t& max_index,
                                 bool byte_swap = false);

}  // namespace refii::renderer
