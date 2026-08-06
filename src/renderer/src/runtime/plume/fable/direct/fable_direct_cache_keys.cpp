#include "runtime/refii_renderer/plume/fable/direct/fable_direct_cache_keys.h"

#include <cstring>
#include <functional>

namespace refii::renderer {

void DirectHashCombine(size_t& seed, size_t value) {
  seed ^= value + 0x9E3779B97F4A7C15ull + (seed << 6u) + (seed >> 2u);
}

// Perf: FNV-1a serializes one 64-bit multiply per BYTE, and its sole consumer
// (the direct upload-buffer cache) hashes two 4 KiB shader-constant banks plus
// the shared block on every recorded draw — ~8 KiB of dependent multiplies per
// draw. Absorbing eight bytes per round cuts that chain ~8x.
//
// This changes the hash VALUE, which is safe by construction: the result is
// only ever a bucket key for an in-memory cache whose hit test is a full
// std::memcmp of the candidate bytes, so bucketing cannot affect which buffer
// is selected. Nothing persists this value across runs.
uint64_t DirectHashBytes(const void* data, size_t size) {
  constexpr uint64_t kFnvOffset = 1469598103934665603ull;
  constexpr uint64_t kFnvPrime = 1099511628211ull;
  const auto* bytes = static_cast<const uint8_t*>(data);
  uint64_t hash = kFnvOffset;
  size_t i = 0;
  for (; i + sizeof(uint64_t) <= size; i += sizeof(uint64_t)) {
    uint64_t word = 0;
    std::memcpy(&word, bytes + i, sizeof(word));
    hash ^= word;
    hash *= kFnvPrime;
  }
  for (; i < size; ++i) {
    hash ^= bytes[i];
    hash *= kFnvPrime;
  }
  return hash;
}

bool DirectInputElementKey::operator==(
    const DirectInputElementKey& other) const {
  return semantic_name == other.semantic_name &&
         semantic_index == other.semantic_index &&
         location == other.location && format == other.format &&
         slot_index == other.slot_index &&
         aligned_byte_offset == other.aligned_byte_offset;
}

bool DirectInputSlotKey::operator==(const DirectInputSlotKey& other) const {
  return index == other.index && stride == other.stride &&
         classification == other.classification;
}

bool DirectPipelineKey::operator==(const DirectPipelineKey& other) const {
  return vertex_shader_hash == other.vertex_shader_hash &&
         pixel_shader_hash == other.pixel_shader_hash &&
         topology == other.topology && color_count == other.color_count &&
         color_formats == other.color_formats &&
         depth_format == other.depth_format && pm4_valid == other.pm4_valid &&
         spec_constants == other.spec_constants &&
         depth_control == other.depth_control &&
         stencil_ref_mask == other.stencil_ref_mask &&
         stencil_ref_mask_bf == other.stencil_ref_mask_bf &&
         blend_enabled == other.blend_enabled &&
         blend_controls == other.blend_controls &&
         color_write_masks == other.color_write_masks &&
         cull_control == other.cull_control &&
         force_no_cull == other.force_no_cull &&
         tess_variant == other.tess_variant &&
         input_slots == other.input_slots &&
         input_elements == other.input_elements;
}

size_t DirectPipelineKeyHash::operator()(const DirectPipelineKey& key) const {
  size_t seed = 0;
  DirectHashCombine(seed, std::hash<uint64_t>{}(key.vertex_shader_hash));
  DirectHashCombine(seed, std::hash<uint64_t>{}(key.pixel_shader_hash));
  DirectHashCombine(seed, std::hash<uint32_t>{}(
                              static_cast<uint32_t>(key.topology)));
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.color_count));
  for (plume::RenderFormat format : key.color_formats) {
    DirectHashCombine(
        seed, std::hash<uint32_t>{}(static_cast<uint32_t>(format)));
  }
  DirectHashCombine(seed, std::hash<uint32_t>{}(
                              static_cast<uint32_t>(key.depth_format)));
  DirectHashCombine(seed, std::hash<bool>{}(key.pm4_valid));
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.spec_constants));
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.depth_control));
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.stencil_ref_mask));
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.stencil_ref_mask_bf));
  for (bool blend_enabled : key.blend_enabled) {
    DirectHashCombine(seed, std::hash<bool>{}(blend_enabled));
  }
  for (uint32_t blend_control : key.blend_controls) {
    DirectHashCombine(seed, std::hash<uint32_t>{}(blend_control));
  }
  for (uint32_t color_write_mask : key.color_write_masks) {
    DirectHashCombine(seed, std::hash<uint32_t>{}(color_write_mask));
  }
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.cull_control));
  DirectHashCombine(seed, std::hash<bool>{}(key.force_no_cull));
  DirectHashCombine(seed, std::hash<bool>{}(key.tess_variant));
  for (const DirectInputSlotKey& slot : key.input_slots) {
    DirectHashCombine(seed, std::hash<uint32_t>{}(slot.index));
    DirectHashCombine(seed, std::hash<uint32_t>{}(slot.stride));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                                static_cast<uint32_t>(slot.classification)));
  }
  for (const DirectInputElementKey& element : key.input_elements) {
    DirectHashCombine(seed, std::hash<std::string>{}(element.semantic_name));
    DirectHashCombine(seed, std::hash<uint32_t>{}(element.semantic_index));
    DirectHashCombine(seed, std::hash<uint32_t>{}(element.location));
    DirectHashCombine(seed, std::hash<uint32_t>{}(
                                static_cast<uint32_t>(element.format)));
    DirectHashCombine(seed, std::hash<uint32_t>{}(element.slot_index));
    DirectHashCombine(seed,
                      std::hash<uint32_t>{}(element.aligned_byte_offset));
  }
  return seed;
}

bool DirectUploadBufferKey::operator==(
    const DirectUploadBufferKey& other) const {
  return hash == other.hash && size == other.size && flags == other.flags;
}

size_t DirectUploadBufferKeyHash::operator()(
    const DirectUploadBufferKey& key) const {
  size_t seed = 0;
  DirectHashCombine(seed, std::hash<uint64_t>{}(key.hash));
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.size));
  DirectHashCombine(seed, std::hash<uint32_t>{}(key.flags));
  return seed;
}

}  // namespace refii::renderer
