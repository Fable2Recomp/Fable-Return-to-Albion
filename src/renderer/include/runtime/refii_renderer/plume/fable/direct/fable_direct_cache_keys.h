#pragma once

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"

#include <cstddef>
#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace refii::renderer {

void DirectHashCombine(size_t& seed, size_t value);
uint64_t DirectHashBytes(const void* data, size_t size);

struct DirectInputElementKey {
  std::string semantic_name;
  uint32_t semantic_index = 0;
  uint32_t location = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  uint32_t slot_index = 0;
  uint32_t aligned_byte_offset = 0;

  bool operator==(const DirectInputElementKey& other) const;
};

struct DirectInputSlotKey {
  uint32_t index = 0;
  uint32_t stride = 0;
  plume::RenderInputSlotClassification classification =
      plume::RenderInputSlotClassification::UNKNOWN;

  bool operator==(const DirectInputSlotKey& other) const;
};

struct DirectPipelineKey {
  uint64_t vertex_shader_hash = 0;
  uint64_t pixel_shader_hash = 0;
  plume::RenderPrimitiveTopology topology =
      plume::RenderPrimitiveTopology::UNKNOWN;
  uint32_t color_count = 0;
  std::array<plume::RenderFormat, kFableDirectFramebufferColorCount>
      color_formats = {};
  plume::RenderFormat depth_format = plume::RenderFormat::UNKNOWN;
  bool pm4_valid = false;
  uint32_t spec_constants = 0;
  uint32_t depth_control = 0;
  uint32_t stencil_ref_mask = 0;
  uint32_t stencil_ref_mask_bf = 0;
  std::array<bool, kFableDirectFramebufferColorCount> blend_enabled = {};
  std::array<uint32_t, kFableDirectFramebufferColorCount> blend_controls = {};
  std::array<uint32_t, kFableDirectFramebufferColorCount> color_write_masks = {};
  uint32_t cull_control = 0;
  bool force_no_cull = false;
  // kGtlTessDraw pipelines: same guest VS identity but the TESS-VARIANT
  // artifact (`<stem>.tess`) is bound, so the key must not collide with the
  // base pipeline for that shader pair.
  bool tess_variant = false;
  std::vector<DirectInputSlotKey> input_slots;
  std::vector<DirectInputElementKey> input_elements;

  bool operator==(const DirectPipelineKey& other) const;
};

struct DirectPipelineKeyHash {
  size_t operator()(const DirectPipelineKey& key) const;
};

struct DirectUploadBufferKey {
  uint64_t hash = 0;
  uint32_t size = 0;
  uint32_t flags = 0;

  bool operator==(const DirectUploadBufferKey& other) const;
};

struct DirectUploadBufferKeyHash {
  size_t operator()(const DirectUploadBufferKey& key) const;
};

struct DirectCachedUploadBuffer {
  std::unique_ptr<plume::RenderBuffer> buffer;
  std::vector<uint8_t> bytes;
  uint64_t last_used_frame = 0;
};

// ---------------------------------------------------------------------------
// Per-frame constant arena
// ---------------------------------------------------------------------------
// Shader constants are transient per-draw data: the vertex bank carries the
// per-object world-view-projection, so its contents differ on essentially every
// draw by construction. They were previously served by a content-hash cache,
// which is the one access pattern that cache can never satisfy -- every draw
// missed, every miss did a vkCreateBuffer whose result was retained for the
// life of the process, and every miss then walked the whole cache map trying
// (and failing) to evict.
//
// The arena is the shape this data actually wants: bump-allocate out of a few
// large upload buffers, hand back a (buffer, offset) pair, and reset the whole
// thing when the frame's GPU work is known complete. No per-draw allocation, no
// hashing, no eviction scan, and bounded memory.
//
// Both consumers already take an offset natively -- the SPIRV path binds
// `getDeviceAddress()` (arena base + offset) and the root-descriptor path binds
// `buffer->at(offset)` -- so nothing downstream needs to change.
struct DirectConstantArenaChunk {
  std::unique_ptr<plume::RenderBuffer> buffer;
  uint8_t* mapped = nullptr;
  uint64_t device_address = 0;
  size_t capacity = 0;
  size_t used = 0;
};

// One slot per in-flight frame. plume waits on the frame context's fence from
// N frames ago before any recording begins (WaitPlumeFrameContextLocked, called
// from PresentPlumeFrame before ExecuteRefiiRenderGraphFrame), so a slot's
// buffers are provably GPU-free by the time we reset them. The run log reports
// 3 frame contexts; 4 slots keeps a margin without relying on that number.
struct DirectConstantArenaSlot {
  std::vector<DirectConstantArenaChunk> chunks;
  size_t current_chunk = 0;
};

// A bump allocation inside an arena chunk.
struct DirectConstantAllocation {
  plume::RenderBuffer* buffer = nullptr;
  uint64_t device_address = 0;
  uint32_t offset = 0;

  explicit operator bool() const { return buffer != nullptr; }
};

struct DirectSurfaceAliasCopyCacheEntry {
  uint64_t alias_serial = 0;
  uint64_t source_content_serial = 0;
  uint64_t destination_content_serial = 0;
  uint64_t frame_index = 0;
};

struct DirectRegisteredTextureIdentityCacheEntry {
  FableTextureIdentity identity;
};

}  // namespace refii::renderer
