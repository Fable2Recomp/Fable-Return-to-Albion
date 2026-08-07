#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/cvars.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/plume/draw/plume_draw_state.h"

#include <rex/logging.h>

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_render_state.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <mutex>
#include <type_traits>
#include <unordered_map>
#include <utility>
#include <vector>

namespace refii::renderer {

bool StoreInlineScratchBuffer(FableDirectRenderCommand& command,
                              bool index_buffer, uint32_t address,
                              uint32_t stride_or_format, const void* data,
                              size_t size, bool pinned_per_draw = false);
std::recursive_mutex& FableDirectGuestResourceRegistryMutex();
// fable_direct_cache_keys.cpp — the content-hash stamp
// (FableDirectInlineBuffer::content_hash) must use the SAME function the
// upload-buffer cache keys on.
uint64_t DirectHashBytes(const void* data, size_t size);

namespace {

constexpr size_t kMaxBufferedFableDirectRenderCommands = 16384;
constexpr uint32_t kMaxFableDirectScratchUploadSnapshotSize = 16u << 20;
constexpr uint32_t kPackedSamplerStateValid = 0x80000000u;
constexpr uint32_t kPackedSamplerStateMagLinear = 0x00000001u;
constexpr uint32_t kPackedSamplerStateMinLinear = 0x00000002u;
constexpr uint32_t kPackedSamplerStateSeparateZ = 0x00000004u;
constexpr uint32_t kPackedSamplerStateMipLinear = 0x00000008u;
constexpr uint32_t kPackedSamplerStateAddressUShift = 8u;
constexpr uint32_t kPackedSamplerStateAddressVShift = 11u;
constexpr uint32_t kPackedSamplerStateAddressWShift = 14u;
constexpr uint32_t kPackedSamplerStateAddressMask = 0x7u;
// One definition, shared with the consumer in fable_direct_renderer.cpp.
constexpr uint32_t kPackedSamplerStateLodBiasShift =
    kDirectPackedSamplerStateLodBiasShift;
constexpr uint32_t kPackedSamplerStateLodBiasMask =
    kDirectPackedSamplerStateLodBiasMask;
constexpr float kFableSamplerLodBiasScale = kDirectSamplerLodBiasScale;
// FilterQuality MOVED from bit 24 to bit 4 (2026-08-04) to free a CONTIGUOUS
// 10-bit run at [26:17] for the mip LOD bias, which the guest itself stores as
// 10 bits. Nothing outside this file ever read FilterQuality — it is stored and
// never consumed — so the move is self-contained. The old field also OVERLAPPED
// the LOD-bias mask below, which is a second reason it could not stay.
constexpr uint32_t kPackedSamplerStateFilterQualityShift = 4u;
constexpr uint32_t kPackedSamplerStateFilterQualityMask = 0x3u;
constexpr uint32_t kFableRenderStateSlotZFunc = 0;
constexpr uint32_t kFableRenderStateSlotColorWriteEnable = 1;
constexpr uint32_t kFableRenderStateSlotAlphaBlendEnable = 2;
constexpr uint32_t kFableRenderStateSlotSrcBlend = 3;
constexpr uint32_t kFableRenderStateSlotZWriteEnable = 4;
constexpr uint32_t kFableRenderStateSlotDestBlend = 5;
constexpr uint32_t kFableRenderStateSlotAlphaTestEnable = 6;
constexpr uint32_t kFableRenderStateSlotZEnable = 7;
constexpr uint32_t kFableRenderStateSlotHiZWriteEnable = 8;
constexpr uint32_t kFableRenderStateSlotBlendOp = 9;
constexpr uint32_t kFableRenderStateSlotBlendOpAlpha = 10;
constexpr uint32_t kFableRenderStateSlotSrcBlendAlpha = 11;
constexpr uint32_t kFableRenderStateSlotDestBlendAlpha = 12;
constexpr uint32_t kFableRenderStateSlotSeparateAlphaBlendEnable = 13;
constexpr uint32_t kFableRenderStateSlotStencilFail = 16;
constexpr uint32_t kFableRenderStateSlotStencilZFail = 17;
constexpr uint32_t kFableRenderStateSlotStencilEnable = 18;
constexpr uint32_t kFableRenderStateSlotCullMode = 19;
constexpr uint32_t kFableRenderStateSlotStencilPass = 20;
constexpr uint32_t kFableRenderStateSlotStencilFunc = 21;
constexpr uint32_t kFableRenderStateSlotStencilReadMask = 22;
constexpr uint32_t kFableRenderStateSlotStencilRef = 23;
constexpr uint32_t kFableRenderStateSlotAlphaFunc = 24;
constexpr uint32_t kFableRenderStateSlotAlphaRef = 25;
constexpr uint32_t kFableRenderStateSlotStencilWriteMask = 27;
constexpr uint32_t kFableRenderStateSlotPackedDepthModeBit2 = 28;
constexpr uint32_t kFableRenderStateSlotPackedDepthModeBit5 = 29;
constexpr float kFableAlphaRefScale = 1.0f / 255.0f;
constexpr uint32_t kFableTitleBillboardVertexDeclaration = 0x40021910u;
constexpr size_t kMaxBufferedFableDirectStateOnlyCommands = 512;
constexpr size_t kKeepBufferedFableDirectStateOnlyCommands = 128;
constexpr uint32_t kFableDirectGpuScratchMin = 0x1000u;
constexpr uint32_t kFableDirectObjectAddressMin = 0x40000000u;
constexpr size_t kMaxBufferedFableDirectDrawScratchCaches = 2048;

struct FableDirectState {
  std::mutex mutex;
  uint64_t serial = 0;
  uint64_t dropped_render_commands = 0;
  // Draw funnel census: draw-event commands entering staging vs reaching the
  // submitted stream (draw_count already counts recorded draw events).
  uint64_t funnel_draw_commands_pushed = 0;
  uint64_t funnel_draw_commands_published = 0;
  uint64_t draw_count = 0;
  uint64_t indexed_draw_count = 0;
  uint64_t state_flush_count = 0;
  uint64_t render_target_switch_count = 0;
  uint64_t depth_target_switch_count = 0;
  uint64_t next_draw_event_serial = 0;
  uint64_t next_render_command_serial = 0;
  uint64_t submitted_render_command_serial = 0;
  uint64_t consumed_submitted_render_command_serial = 0;
  uint32_t last_device = 0;
  FableDirectRenderTargets targets;
  FableDirectRenderTargetRoots target_roots;
  FableDirectShaderState shaders;
  FableDirectShaderRoots shader_roots;
  FableDirectInputState input;
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};
  FableDirectTextureState textures;
  FableDirectConstantState constants;
  FableDirectPm4State pm4;
  RendererViewportSnapshot viewport;
  RendererScissorSnapshot scissor;
  RendererLastDrawSnapshot last_draw;
  RendererScalerSnapshot scaler;
  uint32_t clear_flags = 0;
  FableDirectDirtyState dirty;
  std::vector<FableDirectRenderCommand> render_commands;
  std::vector<FableDirectRenderCommand> staged_submitted_render_commands;
  std::vector<FableDirectRenderCommand> submitted_render_commands;
  // Atomic so guest-thread hot paths (fetch-constant writes) can skip the
  // registry+state locks when nothing is staged; all writes still happen
  // under g_fable_direct.mutex.
  std::atomic<size_t> render_command_draw_event_count{0};
  size_t render_command_direct_event_count = 0;
  std::unordered_map<uint32_t, FableDirectInlineBuffer> vertex_scratch_cache;
  std::unordered_map<uint64_t, FableDirectInlineBuffer>
      vertex_scratch_cache_by_stride;
  std::unordered_map<uint32_t, FableDirectInlineBuffer> index_scratch_cache;
  std::unordered_map<uint64_t, std::vector<FableDirectInlineBuffer>>
      draw_vertex_scratch_cache;
  std::unordered_map<uint64_t, std::vector<FableDirectInlineBuffer>>
      draw_index_scratch_cache;
  uint64_t command_boundary_capture_count = 0;
  uint64_t empty_command_boundary_capture_count = 0;
  uint64_t last_command_boundary_submit = 0;
  uint64_t last_command_boundary_staged_commands = 0;
  uint64_t last_command_boundary_staged_draws = 0;
  uint64_t last_submitted_draw_event_count = 0;
  uint64_t consumed_exact_state_commands = 0;
  // Bumped once per SEALED guest-frame publish
  // (PublishStagedSubmittedRenderCommandsLocked) and stamped on the commands
  // it publishes — the guest-frame grouping the backlog supersession in
  // CaptureFableDirectFrameCommandBatch keys on. Immediate producer appends
  // (PIX/Bink, synthetic recovery) deliberately stay at generation 0.
  //
  // REDESIGNED 2026-08-01 (run 483): generations are now stamped at STAGE
  // time as guest_frame_seal_ordinal + 1 — the ordinal of the guest frame the
  // command was produced in — and the boundary seal advances the ordinal.
  // Minting a fresh generation per PUBLISH fragmented one visual frame across
  // generations whenever the record thread's mid-frame capture published the
  // bulk before the boundary seal published the tail: the filter then dropped
  // the tail fragments (the title logo/text overlay quads, 1-2 commands per
  // seal) as "superseded frames" while keeping the bulk — missing UI on
  // screen and no backlog relief. Stage-time stamping makes every piece of
  // frame N carry generation N+1 regardless of when it publishes.
  uint32_t guest_frame_seal_ordinal = 0;
};

FableDirectState g_fable_direct;
thread_local uint32_t g_fable_direct_patch_depth = 0;

struct FableDirectPatchScope {
  FableDirectPatchScope() { ++g_fable_direct_patch_depth; }
  ~FableDirectPatchScope() { --g_fable_direct_patch_depth; }
};

bool ScratchUploadSnapshotValid(const void* data, size_t size) {
  if (!data || size == 0 ||
      size > kMaxFableDirectScratchUploadSnapshotSize ||
      size > std::numeric_limits<uint32_t>::max()) {
    return false;
  }
  const uint8_t* bytes = static_cast<const uint8_t*>(data);
  return std::any_of(bytes, bytes + size, [](uint8_t value) {
    return value != 0;
  });
}

bool InlineBufferRangeHasPayload(const FableDirectInlineBuffer& inline_buffer,
                                 uint32_t offset, uint64_t byte_size) {
  if (!inline_buffer.ready() || offset >= inline_buffer.byte_size ||
      byte_size == 0) {
    return false;
  }
  const uint64_t available = uint64_t(inline_buffer.byte_size) - offset;
  const uint64_t inspect_size = std::min<uint64_t>(available, byte_size);
  if (inspect_size == 0 ||
      offset + inspect_size > inline_buffer.data.size()) {
    return false;
  }
  const uint8_t* begin = inline_buffer.data.data() + offset;
  return std::any_of(begin, begin + inspect_size, [](uint8_t value) {
    return value != 0;
  });
}

bool IsHighScratchByteBufferRange(uint32_t guest_address, uint32_t size);

bool IsCacheableScratchUploadAddress(uint32_t address, size_t size) {
  return size != 0 && size <= std::numeric_limits<uint32_t>::max() &&
         IsHighScratchByteBufferRange(address, static_cast<uint32_t>(size));
}

void StoreScratchCacheLocked(bool index_buffer, uint32_t address,
                             uint32_t stride_or_format, const void* data,
                             size_t size) {
  if (address == 0 || !ScratchUploadSnapshotValid(data, size) ||
      !IsCacheableScratchUploadAddress(address, size)) {
    return;
  }
  FableDirectInlineBuffer& cached =
      index_buffer ? g_fable_direct.index_scratch_cache[address]
                   : g_fable_direct.vertex_scratch_cache[address];
  cached.address = address;
  cached.byte_size = static_cast<uint32_t>(size);
  cached.stride_or_format = stride_or_format;
  cached.data.resize(size);
  std::memcpy(cached.data.data(), data, size);
  // In-place overwrite of a cache slot that may carry a stamped hash from
  // StoreVertexScratchCacheByInferredStrideLocked — the bytes changed, so the
  // stamp is void (StoreInlineScratchBuffer restamps on attach).
  cached.content_hash = 0;
  if (!index_buffer && stride_or_format != 0) {
    const uint64_t stride_key =
        (uint64_t(address) << 32u) | uint64_t(stride_or_format);
    FableDirectInlineBuffer& cached_by_stride =
        g_fable_direct.vertex_scratch_cache_by_stride[stride_key];
    cached_by_stride.address = address;
    cached_by_stride.byte_size = static_cast<uint32_t>(size);
    cached_by_stride.stride_or_format = stride_or_format;
    cached_by_stride.data.resize(size);
    std::memcpy(cached_by_stride.data.data(), data, size);
    cached_by_stride.content_hash = 0;
  }
}

void StoreDrawScratchCacheLocked(uint64_t draw_serial, bool index_buffer,
                                 uint32_t address, uint32_t stride_or_format,
                                 const void* data, size_t size) {
  if (draw_serial == 0 || address == 0 ||
      !ScratchUploadSnapshotValid(data, size)) {
    return;
  }
  FableDirectInlineBuffer cached;
  cached.address = address;
  cached.byte_size = static_cast<uint32_t>(size);
  cached.stride_or_format = stride_or_format;
  cached.data.resize(size);
  std::memcpy(cached.data.data(), data, size);
  auto& draw_cache = index_buffer ? g_fable_direct.draw_index_scratch_cache
                                  : g_fable_direct.draw_vertex_scratch_cache;
  auto& cache = draw_cache[draw_serial];
  const auto existing = std::find_if(
      cache.begin(), cache.end(), [&](const FableDirectInlineBuffer& upload) {
        return upload.address == address &&
               upload.stride_or_format == stride_or_format;
      });
  if (existing != cache.end()) {
    *existing = std::move(cached);
  } else {
    cache.push_back(std::move(cached));
  }
  while (draw_cache.size() > kMaxBufferedFableDirectDrawScratchCaches) {
    draw_cache.erase(draw_cache.begin());
  }
}

void StoreVertexScratchCacheByInferredStrideLocked(
    const FableDirectInlineBuffer& inline_buffer) {
  if (!inline_buffer.ready() || inline_buffer.stride_or_format == 0 ||
      !IsCacheableScratchUploadAddress(inline_buffer.address,
                                       inline_buffer.byte_size)) {
    return;
  }

  FableDirectInlineBuffer cached = inline_buffer;
  if (cached.data.size() > cached.byte_size) {
    cached.data.resize(cached.byte_size);
  }
  g_fable_direct.vertex_scratch_cache[cached.address] = cached;
  const uint64_t stride_key =
      (uint64_t(cached.address) << 32u) | uint64_t(cached.stride_or_format);
  g_fable_direct.vertex_scratch_cache_by_stride[stride_key] =
      std::move(cached);
}

void StoreCommandVertexScratchCachesByInferredStrideLocked(
    const FableDirectRenderCommand& command) {
  for (const FableDirectInlineBuffer& inline_buffer :
       command.inline_vertex_buffers) {
    StoreVertexScratchCacheByInferredStrideLocked(inline_buffer);
  }
}

const FableDirectInlineBuffer* FindScratchCacheForAddressLocked(
    const std::unordered_map<uint32_t, FableDirectInlineBuffer>& cache,
    uint32_t address) {
  const auto exact_it = cache.find(address);
  if (exact_it != cache.end()) {
    return &exact_it->second;
  }

  const FableDirectInlineBuffer* best = nullptr;
  uint32_t best_offset = std::numeric_limits<uint32_t>::max();
  for (const auto& [cached_address, cached] : cache) {
    if (!cached.ready() || address <= cached_address) {
      continue;
    }
    const uint64_t offset = uint64_t(address - cached_address);
    if (offset >= cached.byte_size || offset >= best_offset) {
      continue;
    }
    best = &cached;
    best_offset = static_cast<uint32_t>(offset);
  }
  return best;
}

const FableDirectInlineBuffer* FindVertexScratchCacheForStreamLocked(
    uint32_t address, uint32_t stride) {
  if (stride != 0) {
    const uint64_t stride_key = (uint64_t(address) << 32u) | uint64_t(stride);
    const auto exact_by_stride =
        g_fable_direct.vertex_scratch_cache_by_stride.find(stride_key);
    if (exact_by_stride != g_fable_direct.vertex_scratch_cache_by_stride.end()) {
      return &exact_by_stride->second;
    }

    const FableDirectInlineBuffer* best = nullptr;
    uint32_t best_offset = std::numeric_limits<uint32_t>::max();
    for (const auto& [cached_key, cached] :
         g_fable_direct.vertex_scratch_cache_by_stride) {
      (void)cached_key;
      if (!cached.ready() || cached.stride_or_format != stride ||
          address <= cached.address) {
        continue;
      }
      const uint64_t offset = uint64_t(address - cached.address);
      if (offset >= cached.byte_size || offset >= best_offset) {
        continue;
      }
      best = &cached;
      best_offset = static_cast<uint32_t>(offset);
    }
    if (best != nullptr) {
      return best;
    }
  }

  return FindScratchCacheForAddressLocked(g_fable_direct.vertex_scratch_cache,
                                          address);
}
uint32_t ReadInlineIndexValue(const uint8_t* bytes, uint32_t stride,
                              bool byte_swap) {
  if (stride == 4u) {
    uint32_t value = 0;
    std::memcpy(&value, bytes, sizeof(value));
    if (byte_swap) {
      value = ((value & 0x000000FFu) << 24) |
              ((value & 0x0000FF00u) << 8) |
              ((value & 0x00FF0000u) >> 8) |
              ((value & 0xFF000000u) >> 24);
    }
    return value;
  }

  uint16_t value = 0;
  std::memcpy(&value, bytes, sizeof(value));
  if (byte_swap) {
    value = static_cast<uint16_t>((value << 8) | (value >> 8));
  }
  return value;
}

bool InlineIndexBytesMaxIndex(const uint8_t* data, size_t byte_size,
                              uint32_t stride, uint32_t first_index,
                              uint32_t index_count, bool byte_swap,
                              uint32_t& max_index) {
  if (!data || byte_size == 0 || index_count == 0) {
    return false;
  }
  if (stride != 4u) {
    stride = 2u;
  }
  const uint64_t begin = uint64_t(first_index) * uint64_t(stride);
  const uint64_t required =
      begin + uint64_t(index_count) * uint64_t(stride);
  if (required > byte_size) {
    return false;
  }

  max_index = 0;
  const uint8_t* indices = data + begin;
  for (uint32_t i = 0; i < index_count; ++i) {
    max_index =
        std::max(max_index, ReadInlineIndexValue(indices + size_t(i) * stride,
                                                 stride, byte_swap));
  }
  return true;
}

uint32_t PrimaryInlineVertexCapacityForCommand(
    const FableDirectRenderCommand& command) {
  const RendererStreamBindingSnapshot& stream = command.resources.streams[0];
  const FableDirectInlineBuffer& inline_buffer =
      command.inline_vertex_buffers[0];
  const uint32_t stride =
      stream.stride != 0 ? stream.stride : inline_buffer.stride_or_format;
  if (stream.buffer == 0 || stride == 0 || !inline_buffer.ready()) {
    return 0;
  }

  uint64_t inline_offset = std::numeric_limits<uint64_t>::max();
  if (stream.buffer == inline_buffer.address) {
    inline_offset = 0;
  } else if (stream.buffer > inline_buffer.address) {
    inline_offset = uint64_t(stream.buffer - inline_buffer.address);
  }
  if (inline_offset == std::numeric_limits<uint64_t>::max() ||
      inline_offset >= inline_buffer.byte_size) {
    return 0;
  }

  const uint64_t combined_offset = inline_offset + uint64_t(stream.offset);
  if (combined_offset >= inline_buffer.byte_size) {
    return 0;
  }
  return static_cast<uint32_t>(
      (uint64_t(inline_buffer.byte_size) - combined_offset) / stride);
}

bool InlineIndexBufferLooksValidForCommand(
    const FableDirectRenderCommand& command, const uint8_t* data,
    size_t byte_size, uint32_t stride, uint32_t vertex_capacity) {
  const uint32_t index_count =
      DrawVertexOrIndexCount(command.draw.draw.primitive_type,
                             command.draw.draw.primitive_count);
  if (index_count == 0 || vertex_capacity == 0) {
    return true;
  }

  uint32_t max_index = 0;
  if (!InlineIndexBytesMaxIndex(data, byte_size, stride,
                                command.draw.draw.start, index_count, false,
                                max_index)) {
    return true;
  }
  if (max_index < vertex_capacity) {
    return true;
  }

  if (InlineIndexBytesMaxIndex(data, byte_size, stride,
                               command.draw.draw.start, index_count, true,
                               max_index) &&
      max_index < vertex_capacity) {
    return true;
  }
  return false;
}

void AttachCachedScratchBuffersToCommandLocked(
    FableDirectRenderCommand& command) {
  if (command.draw_serial == 0) {
    return;
  }
  const auto inline_buffer_covers_address =
      [](const FableDirectInlineBuffer& inline_buffer, uint32_t address,
         uint32_t stride_or_format) {
        if (!inline_buffer.ready() || address == 0) {
          return false;
        }
        const bool stride_matches =
            stride_or_format == 0 || inline_buffer.stride_or_format == 0 ||
            inline_buffer.stride_or_format == stride_or_format;
        if (!stride_matches) {
          return false;
        }
        const uint64_t offset =
            address >= inline_buffer.address
                ? uint64_t(address - inline_buffer.address)
                : std::numeric_limits<uint64_t>::max();
        return offset < inline_buffer.byte_size &&
               InlineBufferRangeHasPayload(
                   inline_buffer, static_cast<uint32_t>(offset),
                   uint64_t(inline_buffer.byte_size) - offset);
      };
  const auto attach_draw_serial_scratch = [&]() {
    const auto vertex_cache_it =
        g_fable_direct.draw_vertex_scratch_cache.find(command.draw_serial);
    if (vertex_cache_it != g_fable_direct.draw_vertex_scratch_cache.end()) {
      bool stored_any = false;
      for (const FableDirectInlineBuffer& upload : vertex_cache_it->second) {
        stored_any =
            StoreInlineScratchBuffer(command, false, upload.address,
                                     upload.stride_or_format, upload.data.data(),
                                     upload.data.size(),
                                     /*pinned_per_draw=*/true) ||
            stored_any;
      }
      if (stored_any) {
        StoreCommandVertexScratchCachesByInferredStrideLocked(command);
      }
      // Consume only when the command holds a payload (stored now, or pinned
      // earlier — the pinning guard also returns false): erasing after a
      // failed match (command bindings not yet in the payload's address
      // space) destroyed the geometry permanently. The cache is capped, so
      // keeping the entry for a later attach pass is bounded.
      if (stored_any ||
          std::any_of(command.inline_vertex_buffers.begin(),
                      command.inline_vertex_buffers.end(),
                      [](const FableDirectInlineBuffer& inline_buffer) {
                        return inline_buffer.ready();
                      })) {
        g_fable_direct.draw_vertex_scratch_cache.erase(vertex_cache_it);
      }
    }
    const auto index_cache_it =
        g_fable_direct.draw_index_scratch_cache.find(command.draw_serial);
    if (index_cache_it != g_fable_direct.draw_index_scratch_cache.end()) {
      bool stored_any_index = false;
      for (const FableDirectInlineBuffer& upload : index_cache_it->second) {
        stored_any_index =
            StoreInlineScratchBuffer(command, true, upload.address,
                                     upload.stride_or_format, upload.data.data(),
                                     upload.data.size(),
                                     /*pinned_per_draw=*/true) ||
            stored_any_index;
      }
      if (stored_any_index || command.inline_index_buffer.ready()) {
        g_fable_direct.draw_index_scratch_cache.erase(index_cache_it);
      }
    }
  };
  for (const RendererStreamBindingSnapshot& stream :
       command.resources.streams) {
    if (!IsHighScratchByteBufferRange(stream.buffer, 1u)) {
      continue;
    }
    const uint32_t slot =
        static_cast<uint32_t>(&stream - command.resources.streams.data());
    if (slot < command.inline_vertex_buffers.size() &&
        inline_buffer_covers_address(command.inline_vertex_buffers[slot],
                                     stream.buffer, stream.stride)) {
      continue;
    }
    const FableDirectInlineBuffer* cached =
        FindVertexScratchCacheForStreamLocked(stream.buffer, stream.stride);
    if (!cached) {
      continue;
    }
    StoreInlineScratchBuffer(command, false, cached->address,
                             cached->stride_or_format, cached->data.data(),
                             cached->data.size());
  }
  if (!command.draw.draw.indexed || command.resources.index_buffer == 0) {
    attach_draw_serial_scratch();
    return;
  }
  if (!IsHighScratchByteBufferRange(command.resources.index_buffer, 1u)) {
    attach_draw_serial_scratch();
    return;
  }
  if (inline_buffer_covers_address(command.inline_index_buffer,
                                   command.resources.index_buffer, 0)) {
    attach_draw_serial_scratch();
    return;
  }
  const FableDirectInlineBuffer* cached = FindScratchCacheForAddressLocked(
      g_fable_direct.index_scratch_cache, command.resources.index_buffer);
  if (!cached) {
    attach_draw_serial_scratch();
    return;
  }
  const uint32_t attach_address =
      cached->address <= command.resources.index_buffer &&
              uint64_t(command.resources.index_buffer - cached->address) <
                  uint64_t(cached->data.size())
          ? cached->address
          : command.resources.index_buffer;
  StoreInlineScratchBuffer(command, true, attach_address,
                           cached->stride_or_format, cached->data.data(),
                           cached->data.size());
  attach_draw_serial_scratch();
}

template <typename T>
void SetDirtyValue(bool& dirty, T& destination, const T& source) {
  if (destination != source) {
    destination = source;
    dirty = true;
  }
}

void RememberDeviceLocked(uint32_t device) {
  if (device != 0) {
    g_fable_direct.last_device = device;
  }
}

void TouchStateLocked(uint32_t device = 0) {
  ++g_fable_direct.serial;
  RememberDeviceLocked(device);
}

uint32_t DefaultPackedSamplerState() {
  return kPackedSamplerStateValid | kPackedSamplerStateMagLinear |
         kPackedSamplerStateMinLinear | kPackedSamplerStateMipLinear;
}

void StoreSamplerAddressMode(uint32_t& packed, uint32_t shift,
                             uint32_t value) {
  packed &= ~(kPackedSamplerStateAddressMask << shift);
  packed |= ((value & kPackedSamplerStateAddressMask) << shift);
}

bool IsLinearSamplerFilter(uint32_t value) {
  return value == 1;
}

uint32_t StorePackedSamplerStateValue(uint32_t packed, uint32_t state_slot,
                                      uint32_t value) {
  if ((packed & kPackedSamplerStateValid) == 0) {
    packed = DefaultPackedSamplerState();
  }

  switch (state_slot) {
    case 0:  // MinFilterZ / MinFilter
      if (IsLinearSamplerFilter(value)) {
        packed |= kPackedSamplerStateMinLinear;
      } else {
        packed &= ~kPackedSamplerStateMinLinear;
      }
      break;
    case 1:  // MagFilterZ / MagFilter
      if (IsLinearSamplerFilter(value)) {
        packed |= kPackedSamplerStateMagLinear;
      } else {
        packed &= ~kPackedSamplerStateMagLinear;
      }
      break;
    case 2:  // SeparateZFilterEnable
      if (value != 0) {
        packed |= kPackedSamplerStateSeparateZ;
      } else {
        packed &= ~kPackedSamplerStateSeparateZ;
      }
      break;
    case 3:  // MipMapLodBias
      // WAS DEAD, AND WRONG IN KIND (fixed 2026-08-04):
      //   packed = (packed & 0x803FFFFF) | ((value >> 10) & 0x7FC00000);
      // For any 32-bit `value`, `value >> 10` cannot set a bit above 21, while
      // the mask needs bits 22-30 — so it stored ZERO every time, and cleared
      // bits 22-30 on the way past. Nothing read the field, so the whole of
      // D3DSAMP_MIPMAPLODBIAS was silently discarded end to end.
      //
      // It also mis-typed the argument. IDA, D3DDevice_SetSamplerState_MipMap
      // LodBias 0x821EC798: `lfs f13, arg_24(r1)` loads the DWORD as a FLOAT,
      // multiplies by 32.0 (flt_8209ABC8) and inserts 10 bits at fetch dword 4
      // [21:12] — `821ec7dc insrwi r8, r9, 10, 10`. So `value` is an IEEE-754
      // bit pattern, and shifting it right by 10 is meaningless arithmetic on
      // an exponent field.
      //
      // Store the guest's OWN encoding verbatim: bias * 32, truncated, as a
      // signed 10-bit field. That keeps 1/32 LOD precision over +-16, which is
      // the exact range and step the console has.
      {
        float bias = 0.0f;
        std::memcpy(&bias, &value, sizeof(bias));
        if (!std::isfinite(bias)) {
          bias = 0.0f;
        }
        const int32_t fixed = std::clamp(
            static_cast<int32_t>(bias * kFableSamplerLodBiasScale), -512, 511);
        packed = (packed & ~(kPackedSamplerStateLodBiasMask
                             << kPackedSamplerStateLodBiasShift)) |
                 ((static_cast<uint32_t>(fixed) &
                   kPackedSamplerStateLodBiasMask)
                  << kPackedSamplerStateLodBiasShift);
      }
      break;
    case 4:  // AddressU
      StoreSamplerAddressMode(packed, kPackedSamplerStateAddressUShift, value);
      break;
    case 5:  // AddressV
      StoreSamplerAddressMode(packed, kPackedSamplerStateAddressVShift, value);
      break;
    case 6:  // AddressW
      StoreSamplerAddressMode(packed, kPackedSamplerStateAddressWShift, value);
      break;
    case 7:  // MipFilter
      if (IsLinearSamplerFilter(value)) {
        packed |= kPackedSamplerStateMipLinear;
      } else {
        packed &= ~kPackedSamplerStateMipLinear;
      }
      break;
    case 8:  // FilterQuality
      packed &= ~(kPackedSamplerStateFilterQualityMask
                  << kPackedSamplerStateFilterQualityShift);
      packed |= ((value & kPackedSamplerStateFilterQualityMask)
                 << kPackedSamplerStateFilterQualityShift);
      break;
    default:
      break;
  }
  return packed;
}

std::array<uint32_t, kRendererD3DRenderStateSlots>
DefaultFableRenderStates() {
  std::array<uint32_t, kRendererD3DRenderStateSlots> states = {};
  states[kFableRenderStateSlotZFunc] = 3;  // LESSEQUAL
  states[kFableRenderStateSlotColorWriteEnable] = 0xFu;
  states[kFableRenderStateSlotAlphaBlendEnable] = 0;
  states[kFableRenderStateSlotSrcBlend] = 1;   // ONE
  states[kFableRenderStateSlotZWriteEnable] = 1;
  states[kFableRenderStateSlotDestBlend] = 0;  // ZERO
  states[kFableRenderStateSlotAlphaTestEnable] = 0;
  states[kFableRenderStateSlotZEnable] = 1;
  states[kFableRenderStateSlotHiZWriteEnable] = 0;
  states[kFableRenderStateSlotBlendOp] = 0;       // ADD
  states[kFableRenderStateSlotBlendOpAlpha] = 0;  // ADD
  states[kFableRenderStateSlotSrcBlendAlpha] = 1; // ONE
  states[kFableRenderStateSlotDestBlendAlpha] = 0; // ZERO
  states[kFableRenderStateSlotSeparateAlphaBlendEnable] = 0;
  states[kFableRenderStateSlotStencilFail] = 0;   // KEEP
  states[kFableRenderStateSlotStencilZFail] = 0;  // KEEP
  states[kFableRenderStateSlotStencilEnable] = 0;
  states[kFableRenderStateSlotCullMode] = 6;   // CCW cull
  states[kFableRenderStateSlotStencilPass] = 0;   // KEEP
  states[kFableRenderStateSlotStencilFunc] = 7;   // ALWAYS
  states[kFableRenderStateSlotStencilReadMask] = 0xFFu;
  states[kFableRenderStateSlotStencilRef] = 0;
  states[kFableRenderStateSlotAlphaFunc] = 6;
  states[kFableRenderStateSlotAlphaRef] = 0;
  states[kFableRenderStateSlotStencilWriteMask] = 0xFFu;
  states[kFableRenderStateSlotPackedDepthModeBit2] = 0;
  states[kFableRenderStateSlotPackedDepthModeBit5] = 0;
  return states;
}

uint32_t PackFableFloatBits(float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

uint32_t FableDirectPackedBlendControl(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  return (states[kFableRenderStateSlotSrcBlend] & 0x1Fu) |
         ((states[kFableRenderStateSlotBlendOp] & 0x7u) << 5u) |
         ((states[kFableRenderStateSlotDestBlend] & 0x1Fu) << 8u) |
         ((states[kFableRenderStateSlotSrcBlendAlpha] & 0x1Fu) << 16u) |
         ((states[kFableRenderStateSlotBlendOpAlpha] & 0x7u) << 21u) |
         ((states[kFableRenderStateSlotDestBlendAlpha] & 0x1Fu) << 24u);
}

uint32_t FableDirectEffectiveBlendControl(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  if (states[kFableRenderStateSlotAlphaBlendEnable] == 0) {
    return 0x00010001u;
  }

  const uint32_t packed = FableDirectPackedBlendControl(states);
  if (states[kFableRenderStateSlotSeparateAlphaBlendEnable] != 0) {
    return packed;
  }

  return (((packed & 0x1010u) | ((packed << 4u) & 0xFFFF0u)) << 12u) &
             0xEFEF0000u |
         (packed & 0xFFFFu);
}

uint32_t FableDirectDepthControl(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states,
    bool has_depth_stencil, uint32_t current_depth_control) {
  const uint32_t depth_enabled =
      has_depth_stencil ? states[kFableRenderStateSlotZEnable] : 0u;
  const uint32_t stencil_enabled =
      has_depth_stencil ? states[kFableRenderStateSlotStencilEnable] : 0u;
  constexpr uint32_t kRenderStateDepthControlMask = 0x000FFFF7u;
  const uint32_t render_state_depth_control =
      (stencil_enabled & 0x1u) |
      ((depth_enabled & 0x1u) << 1u) |
      ((states[kFableRenderStateSlotZWriteEnable] & 0x1u) << 2u) |
      ((states[kFableRenderStateSlotZFunc] & 0x7u) << 4u) |
      ((states[kFableRenderStateSlotStencilFunc] & 0x7u) << 8u) |
      ((states[kFableRenderStateSlotStencilFail] & 0x7u) << 11u) |
      ((states[kFableRenderStateSlotStencilPass] & 0x7u) << 14u) |
      ((states[kFableRenderStateSlotStencilZFail] & 0x7u) << 17u);
  return (current_depth_control & ~kRenderStateDepthControlMask) |
         (render_state_depth_control & kRenderStateDepthControlMask);
}

uint32_t FableDirectStencilRefMask(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  return (states[kFableRenderStateSlotStencilRef] & 0xFFu) |
         ((states[kFableRenderStateSlotStencilReadMask] & 0xFFu) << 8u) |
         ((states[kFableRenderStateSlotStencilWriteMask] & 0xFFu) << 16u);
}

uint32_t FableDirectColorControl(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  return (states[kFableRenderStateSlotAlphaFunc] & 0x7u) |
         ((states[kFableRenderStateSlotAlphaTestEnable] & 0x1u) << 3u);
}

void SetPm4ValueLocked(uint32_t& destination, uint32_t value,
                       bool& changed) {
  if (destination != value) {
    destination = value;
    changed = true;
  }
}

void ApplyRenderStatePm4Locked() {
  FableDirectPm4State& pm4 = g_fable_direct.pm4;
  const auto& states = g_fable_direct.render_states;
  bool changed = !pm4.valid;
  pm4.valid = true;

  SetPm4ValueLocked(pm4.rb_depthcontrol,
                    FableDirectDepthControl(
                        states, g_fable_direct.targets.depth != 0,
                        pm4.rb_depthcontrol),
                    changed);

  const uint32_t color_mask =
      g_fable_direct.targets.color[0] != 0
          ? (pm4.rb_color_mask & 0xFFFFFFF0u) |
                (states[kFableRenderStateSlotColorWriteEnable] & 0xFu)
          : (pm4.rb_color_mask & 0xFFFFFFF0u);
  SetPm4ValueLocked(pm4.rb_color_mask, color_mask, changed);

  const uint32_t blend_control = FableDirectEffectiveBlendControl(states);
  for (uint32_t& rt_blend_control : pm4.rb_blendcontrol) {
    SetPm4ValueLocked(rt_blend_control, blend_control, changed);
  }

  SetPm4ValueLocked(pm4.rb_colorcontrol, FableDirectColorControl(states),
                    changed);
  SetPm4ValueLocked(
      pm4.rb_alpha_ref,
      PackFableFloatBits(static_cast<float>(
                            states[kFableRenderStateSlotAlphaRef] & 0xFFu) *
                        kFableAlphaRefScale),
      changed);

  const uint32_t stencil_ref_mask = FableDirectStencilRefMask(states);
  SetPm4ValueLocked(pm4.rb_stencilrefmask, stencil_ref_mask, changed);
  SetPm4ValueLocked(pm4.rb_stencilrefmask_bf, stencil_ref_mask, changed);

  SetPm4ValueLocked(
      pm4.pa_su_sc_mode_cntl,
      (pm4.pa_su_sc_mode_cntl & 0xFFFFFFF8u) |
          (states[kFableRenderStateSlotCullMode] & 0x7u),
      changed);

  uint32_t depth_info = pm4.rb_depth_info;
  depth_info = (depth_info & 0xFFFFFFFBu) |
               ((states[kFableRenderStateSlotPackedDepthModeBit2] & 0x1u)
                << 2u);
  depth_info = (depth_info & 0xFFFFFFDFu) |
               ((states[kFableRenderStateSlotPackedDepthModeBit5] & 0x1u)
                << 5u);
  SetPm4ValueLocked(pm4.rb_depth_info, depth_info, changed);

  if (changed) {
    g_fable_direct.dirty.pm4 = true;
    g_fable_direct.dirty.pipeline = true;
  }
}

bool IsHighScratchByteBufferRange(uint32_t guest_address, uint32_t size) {
  if ((guest_address & (alignof(uint32_t) - 1u)) != 0 || size == 0 ||
      size > kMaxFableDirectScratchUploadSnapshotSize) {
    return false;
  }
  return (guest_address >= kFableDirectGpuScratchMin &&
          guest_address < kFableDirectObjectAddressMin) ||
         guest_address >= 0x80000000u;
}

void ResetStateLocked() {
  g_fable_direct.targets = {};
  g_fable_direct.target_roots = {};
  g_fable_direct.shaders = {};
  g_fable_direct.shader_roots = {};
  g_fable_direct.input = {};
  g_fable_direct.render_states = DefaultFableRenderStates();
  g_fable_direct.textures = {};
  g_fable_direct.constants = {};
  g_fable_direct.pm4 = FableDirectPm4State{};
  ApplyRenderStatePm4Locked();
  g_fable_direct.viewport = {};
  g_fable_direct.scissor = {};
  g_fable_direct.last_draw = {};
  g_fable_direct.scaler = {};
  g_fable_direct.clear_flags = 0;
  g_fable_direct.dirty = FableDirectDirtyState{};
  g_fable_direct.render_commands.clear();
  g_fable_direct.staged_submitted_render_commands.clear();
  g_fable_direct.submitted_render_commands.clear();
  g_fable_direct.render_command_draw_event_count = 0;
  g_fable_direct.render_command_direct_event_count = 0;
  g_fable_direct.vertex_scratch_cache.clear();
  g_fable_direct.vertex_scratch_cache_by_stride.clear();
  g_fable_direct.index_scratch_cache.clear();
  g_fable_direct.draw_vertex_scratch_cache.clear();
  g_fable_direct.draw_index_scratch_cache.clear();
  g_fable_direct.next_draw_event_serial = 0;
  g_fable_direct.next_render_command_serial = 0;
  g_fable_direct.submitted_render_command_serial = 0;
  g_fable_direct.consumed_submitted_render_command_serial = 0;
  g_fable_direct.command_boundary_capture_count = 0;
  g_fable_direct.empty_command_boundary_capture_count = 0;
  g_fable_direct.last_command_boundary_submit = 0;
  g_fable_direct.last_command_boundary_staged_commands = 0;
  g_fable_direct.last_command_boundary_staged_draws = 0;
  g_fable_direct.last_submitted_draw_event_count = 0;
  g_fable_direct.consumed_exact_state_commands = 0;
  g_fable_direct.guest_frame_seal_ordinal = 0;
  g_fable_direct.render_target_switch_count = 0;
  g_fable_direct.depth_target_switch_count = 0;
}

bool RenderCommandCarriesDrawEvent(const FableDirectRenderCommand& command) {
  return command.draw_serial != 0;
}

bool RenderCommandCarriesDirectEvent(const FableDirectRenderCommand& command) {
  if (command.serial == 0 && command.draw_serial == 0) {
    return false;
  }

  switch (command.type) {
    case FableDirectRenderCommandType::kClear:
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP:
    case FableDirectRenderCommandType::kResolveCopy:
      return true;
    case FableDirectRenderCommandType::kSetRenderTarget:
    case FableDirectRenderCommandType::kSetDepthStencil:
    case FableDirectRenderCommandType::kSetTexture:
    case FableDirectRenderCommandType::kSetStreamSource:
    case FableDirectRenderCommandType::kSetIndices:
    case FableDirectRenderCommandType::kSetVertexDeclaration:
    case FableDirectRenderCommandType::kSetVertexShader:
    case FableDirectRenderCommandType::kSetPixelShader:
    case FableDirectRenderCommandType::kSetViewport:
    case FableDirectRenderCommandType::kSetScissor:
    case FableDirectRenderCommandType::kSetScaler:
    case FableDirectRenderCommandType::kSetRenderState:
    case FableDirectRenderCommandType::kSetSamplerState:
    case FableDirectRenderCommandType::kSetPm4Field:
    case FableDirectRenderCommandType::kStateFlush:
    // Synthesized at merge time from a GTL ResolvePacket / tess packet;
    // carries no guest event serials.
    case FableDirectRenderCommandType::kGtlResolve:
    case FableDirectRenderCommandType::kGtlTessDraw:
      return false;
  }
  return false;
}

bool DirectCommandIsDrawActionType(const FableDirectRenderCommand& command) {
  return command.type == FableDirectRenderCommandType::kDrawPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawIndexedPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawPrimitiveUP;
}

// The exact-consumed predicates read ONLY the command type and the three
// state-root scalars. That is what lets the setters evaluate them BEFORE
// default-constructing the ~12.6 KiB FableDirectRenderCommand (see
// ConsumeExactStateCommandLocked below) -- keep them that way.
bool DirectCommandIsExactFramebufferStateAction(
    FableDirectRenderCommandType type, const FableDirectStateRoot& state_root) {
  const auto exact_framebuffer_root = [](const FableDirectStateRoot& root) {
    if (root.call_serial == 0 || root.context_key == 0) {
      return false;
    }
    return root.symbol_address == 0x822869A0u ||
           root.symbol_address == 0x82264590u;
  };
  const auto exact_root = [](const FableDirectStateRoot& root,
                             uint32_t symbol_address) {
    return root.call_serial != 0 && root.context_key != 0 &&
           root.symbol_address == symbol_address;
  };

  if (type == FableDirectRenderCommandType::kSetRenderTarget) {
    return exact_root(state_root, 0x822869A0u);
  }
  if (type == FableDirectRenderCommandType::kSetDepthStencil) {
    return exact_root(state_root, 0x82264590u);
  }
  if (type == FableDirectRenderCommandType::kSetViewport ||
      type == FableDirectRenderCommandType::kSetScissor ||
      type == FableDirectRenderCommandType::kSetPm4Field) {
    return exact_framebuffer_root(state_root);
  }
  return false;
}

bool DirectCommandIsExactFramebufferStateAction(
    const FableDirectRenderCommand& command) {
  return DirectCommandIsExactFramebufferStateAction(command.type,
                                                    command.state_root);
}

bool DirectCommandHasExactStateRoot(const FableDirectStateRoot& state_root) {
  return state_root.call_serial != 0 && state_root.context_key != 0 &&
         state_root.symbol_address != 0;
}

bool DirectCommandIsExactRootedStateAction(
    FableDirectRenderCommandType type, const FableDirectStateRoot& state_root) {
  if (!DirectCommandHasExactStateRoot(state_root)) {
    return false;
  }

  switch (type) {
    case FableDirectRenderCommandType::kSetTexture:
    case FableDirectRenderCommandType::kSetStreamSource:
    case FableDirectRenderCommandType::kSetIndices:
    case FableDirectRenderCommandType::kSetVertexDeclaration:
    case FableDirectRenderCommandType::kSetVertexShader:
    case FableDirectRenderCommandType::kSetPixelShader:
    case FableDirectRenderCommandType::kSetViewport:
    case FableDirectRenderCommandType::kSetScissor:
    case FableDirectRenderCommandType::kSetRenderState:
    case FableDirectRenderCommandType::kSetSamplerState:
    case FableDirectRenderCommandType::kStateFlush:
      return true;
    case FableDirectRenderCommandType::kSetRenderTarget:
    case FableDirectRenderCommandType::kSetDepthStencil:
    case FableDirectRenderCommandType::kSetScaler:
    case FableDirectRenderCommandType::kSetPm4Field:
    case FableDirectRenderCommandType::kClear:
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP:
    case FableDirectRenderCommandType::kResolveCopy:
    case FableDirectRenderCommandType::kGtlResolve:
    case FableDirectRenderCommandType::kGtlTessDraw:
      return false;
  }
  return false;
}

bool DirectCommandIsExactRootedStateAction(
    const FableDirectRenderCommand& command) {
  return DirectCommandIsExactRootedStateAction(command.type,
                                               command.state_root);
}

bool DirectCommandIsExactConsumedStateAction(
    FableDirectRenderCommandType type, const FableDirectStateRoot& state_root) {
  return DirectCommandIsExactRootedStateAction(type, state_root) ||
         DirectCommandIsExactFramebufferStateAction(type, state_root);
}

bool DirectCommandIsExactConsumedStateAction(
    const FableDirectRenderCommand& command) {
  return DirectCommandIsExactConsumedStateAction(command.type,
                                                 command.state_root);
}

// HOISTED DISCARD -- call this BEFORE constructing the command.
//
// PushRenderCommandLocked's first act is to discard exact-consumed state
// actions, but by then every setter had already default-constructed a
// ~12.6 KiB FableDirectRenderCommand (inline std::array scratch buffers)
// under g_fable_direct.mutex, only for it to be thrown away: run 480 measured
// ~17,300 such discards per compose on the guest thread. The predicate needs
// only the command type and the state root, both known at the setter site
// before construction, so setters that can produce exact-consumed types call
// this first and skip building the command entirely on a hit.
//
// Must be called with g_fable_direct.mutex held (every setter already holds it
// for the state-mirror update, which happens regardless of the discard). The
// increment is the SAME census counter PushRenderCommandLocked bumps
// (consumed_exact_state= in the compose log), still written and read under the
// mutex, so the census stream is unchanged.
bool ConsumeExactStateCommandLocked(FableDirectRenderCommandType type,
                                    const FableDirectStateRoot& state_root) {
  if (!DirectCommandIsExactConsumedStateAction(type, state_root)) {
    return false;
  }
  ++g_fable_direct.consumed_exact_state_commands;
  return true;
}

size_t CountCommandDrawEvents(
    const std::vector<FableDirectRenderCommand>& commands);
size_t CountCommandDirectEvents(
    const std::vector<FableDirectRenderCommand>& commands);

void TrimBufferedStateOnlyRenderCommandsLocked(size_t keep_state_commands) {
  std::vector<FableDirectRenderCommand>& commands =
      g_fable_direct.render_commands;
  // GATE ON THE STATE-ONLY COUNT, NOT THE TOTAL SIZE.
  //
  // This early-out used to read `commands.size() < kMaxBufferedFableDirect
  // StateOnlyCommands` -- comparing the TOTAL buffer size against a STATE-ONLY
  // threshold of 512. This function is called from EVERY
  // PushRenderCommandLocked, so once a frame had pushed 512 commands of ANY
  // kind, every subsequent push ran the whole body below: a full reverse walk,
  // a MOVE of every retained element (sizeof(FableDirectRenderCommand) ==
  // 12,664 bytes, and moving a struct of std::arrays copies them), a
  // std::reverse over the result, then TWO more full walks to recount.
  // That is O(n) per push => O(n^2) per frame, on the guest render thread --
  // the thread the present pump blocks on, and it degrades exactly as observed
  // because n grows as more content loads.
  //
  // There is nothing to trim unless the STATE-ONLY population exceeds the
  // threshold, and that count is already tracked as total - direct_event_count.
  // Trimming semantics are unchanged: keep every direct-event command plus the
  // newest `keep_state_commands` state-only ones.
  const size_t direct_events = g_fable_direct.render_command_direct_event_count;
  const size_t state_only =
      commands.size() > direct_events ? commands.size() - direct_events : 0;
  if (state_only < kMaxBufferedFableDirectStateOnlyCommands) {
    return;
  }

  // COUNT FIRST, MOVE SECOND. The old code moved every element out of
  // `commands` and only then discovered it had retained all of them, taking the
  // `retained_reversed.size() == commands.size()` early return below -- which
  // discarded the moved-into vector and left the LIVE buffer full of moved-from
  // commands. Deciding up front makes the no-op case an actual no-op.
  {
    size_t would_retain = 0;
    size_t state_kept = 0;
    for (auto it = commands.rbegin(); it != commands.rend(); ++it) {
      const bool carries_direct_event = RenderCommandCarriesDirectEvent(*it);
      if (!carries_direct_event && state_kept >= keep_state_commands) {
        continue;
      }
      if (!carries_direct_event) {
        ++state_kept;
      }
      ++would_retain;
    }
    if (would_retain == commands.size()) {
      return;
    }
  }

  std::vector<FableDirectRenderCommand> retained_reversed;
  retained_reversed.reserve(std::min(commands.size(), keep_state_commands +
                                                      g_fable_direct
                                                          .render_command_direct_event_count));
  size_t retained_state_commands = 0;
  for (auto it = commands.rbegin(); it != commands.rend(); ++it) {
    const bool carries_direct_event = RenderCommandCarriesDirectEvent(*it);
    if (!carries_direct_event &&
        retained_state_commands >= keep_state_commands) {
      continue;
    }
    if (!carries_direct_event) {
      ++retained_state_commands;
    }
    retained_reversed.push_back(std::move(*it));
  }

  if (retained_reversed.size() == commands.size()) {
    return;
  }

  std::reverse(retained_reversed.begin(), retained_reversed.end());
  g_fable_direct.dropped_render_commands +=
      commands.size() - retained_reversed.size();
  commands = std::move(retained_reversed);
  g_fable_direct.render_command_draw_event_count =
      CountCommandDrawEvents(commands);
  g_fable_direct.render_command_direct_event_count =
      CountCommandDirectEvents(commands);
}

// TAKES A CONST REFERENCE ON PURPOSE -- DO NOT "SIMPLIFY" BACK TO BY-VALUE.
//
// FableDirectRenderCommand is ~14 KiB (it embeds std::array inline-vertex
// buffers, pix video planes and the sampler-state block), and the FIRST thing
// this function does is DISCARD most of what it is handed: run 318 measured
// consumed_exact_state=185281 discarded pushes in ONE frame against
// attempted=164 draws. By value, every one of those paid a full ~14 KiB copy at
// the call site before being dropped -- order of GB/frame of pure memcpy on the
// guest render thread, which is exactly the thread the present pump blocks on
// (measured pump wait avg_ms=245 while the pump body itself was 0.05 ms, and
// the GPU only 0.277 ms).
//
// The discard predicate reads only `type` and the small state-root scalars, so
// it runs fine against a reference. The copy now happens ONLY for commands we
// keep, where it costs exactly what the old code cost anyway.
//
// The exact-consumed discard is additionally HOISTED to the setter sites via
// ConsumeExactStateCommandLocked, so on the hot path the ~12.6 KiB command is
// never constructed at all. The check below is kept as a fail-open backstop
// for any caller that pushes without pre-filtering (draw/scaler pushes never
// match it today); it still bumps the same census counter, so a command that
// slips through is counted identically, never double-counted (a pre-filtered
// command never reaches this function).
void PushRenderCommandLocked(const FableDirectRenderCommand& incoming) {
  if (DirectCommandIsExactConsumedStateAction(incoming)) {
    ++g_fable_direct.consumed_exact_state_commands;
    return;
  }

  FableDirectRenderCommand command = incoming;
  command.serial = ++g_fable_direct.next_render_command_serial;
  // Guest-frame identity, stamped at PRODUCTION: the ordinal of the frame
  // being assembled when the guest emitted this command. Survives however the
  // command is later batched/staged/published. The immediate video route
  // (FableDirectCaptureSubmittedRenderCommands publish=true) zeroes it at its
  // append — route-based gen-0, run-475 contract.
  command.publish_generation = g_fable_direct.guest_frame_seal_ordinal + 1u;
  if (command.resource_serial == 0) {
    command.resource_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
  }
  const bool carries_draw_event = RenderCommandCarriesDrawEvent(command);
  const bool carries_direct_event = RenderCommandCarriesDirectEvent(command);
  TrimBufferedStateOnlyRenderCommandsLocked(
      kKeepBufferedFableDirectStateOnlyCommands);
  if (g_fable_direct.render_commands.size() >=
      kMaxBufferedFableDirectRenderCommands) {
    // DROP IN A BATCH. This used to erase ONE element from the FRONT of a
    // std::vector, which is O(n): it moves every remaining element down.
    //
    // The numbers make it the worst hot-path defect in the renderer.
    // sizeof(FableDirectRenderCommand) == 12,664 bytes (measured), and the cap
    // is 16,384, so the buffer is ~207 MB and a single-element front-erase
    // memmoves ~207 MB. Once the buffer SATURATES, every subsequent push paid
    // that -- on the guest render thread, which is precisely the thread the
    // present pump blocks on. Run 321 reported dropped=5571, i.e. of the order
    // of a TERABYTE of memmove, and the pump wait degraded 32 ms -> 117 ms
    // within one session with the frame-time curve flat until saturation and
    // then falling off a cliff. That shape is the signature of this bug.
    //
    // Erasing kDropBatch at a time amortises one move over kDropBatch pushes.
    // Semantics are otherwise unchanged: still oldest-first, still counted per
    // dropped command. The batch is small against the cap so the retained
    // history barely moves (16384 -> 16128 at the moment of a drop).
    constexpr size_t kDropBatch = 256;
    const size_t drop = std::min(kDropBatch,
                                 g_fable_direct.render_commands.size());
    for (size_t i = 0; i < drop; ++i) {
      const FableDirectRenderCommand& victim = g_fable_direct.render_commands[i];
      if (RenderCommandCarriesDrawEvent(victim) &&
          g_fable_direct.render_command_draw_event_count != 0) {
        --g_fable_direct.render_command_draw_event_count;
      }
      if (RenderCommandCarriesDirectEvent(victim) &&
          g_fable_direct.render_command_direct_event_count != 0) {
        --g_fable_direct.render_command_direct_event_count;
      }
    }
    g_fable_direct.render_commands.erase(
        g_fable_direct.render_commands.begin(),
        g_fable_direct.render_commands.begin() + drop);
    g_fable_direct.dropped_render_commands += drop;
  }

  AttachCachedScratchBuffersToCommandLocked(command);
  g_fable_direct.render_commands.push_back(std::move(command));
  if (carries_draw_event) {
    ++g_fable_direct.render_command_draw_event_count;
    ++g_fable_direct.funnel_draw_commands_pushed;
  }
  if (carries_direct_event) {
    ++g_fable_direct.render_command_direct_event_count;
  }
}


bool DirectResourceMatchesAddress(const FableDirectResourceReference& resource,
                                  uint32_t address);

FableDirectPm4State Pm4StateFromDrawEvent(
    const RendererDrawEventSnapshot& event);

void NormalizePrivateTextureRectangleDrawEvent(
    RendererDrawEventSnapshot& event) {
  if (event.source != RendererDrawEventSource::kPrivateTextureRectangle &&
      !event.private_texture_rectangle) {
    return;
  }

  event.private_texture_rectangle = true;
  // PrivateTextureRectangleDraw injects replacement rectangle vertices in the
  // 0152 gui-quad layout. If an inner submit donates Fable's pass-through GUI
  // VS, those vertices are interpreted as clip-space and cover the title with a
  // giant diagonal wedge.
  event.vertex_shader = kFableSyntheticPrivateRectVertexShader;
  if (event.pixel_shader == 0) {
    event.pixel_shader = kFableSyntheticPrivateRectCopyPixelShader;
  }
}


FableDirectRenderCommandType RenderCommandTypeForEvent(
    const RendererDrawEventSnapshot& event) {
  if (event.clear) {
    return FableDirectRenderCommandType::kClear;
  }
  if (event.source == RendererDrawEventSource::kPm4Resolve) {
    return FableDirectRenderCommandType::kResolveCopy;
  }
  if (event.draw_vertices_up) {
    return FableDirectRenderCommandType::kDrawPrimitiveUP;
  }
  return event.draw.indexed ? FableDirectRenderCommandType::kDrawIndexedPrimitive
                            : FableDirectRenderCommandType::kDrawPrimitive;
}




size_t CountCommandDrawEvents(
    const std::vector<FableDirectRenderCommand>& commands) {
  return static_cast<size_t>(std::count_if(
      commands.begin(), commands.end(), RenderCommandCarriesDrawEvent));
}

size_t CountCommandDirectEvents(
    const std::vector<FableDirectRenderCommand>& commands) {
  return static_cast<size_t>(std::count_if(
      commands.begin(), commands.end(), RenderCommandCarriesDirectEvent));
}

size_t SubmittedDirectEventCountLocked() {
  return CountCommandDirectEvents(g_fable_direct.submitted_render_commands);
}

size_t StagedAndSubmittedDirectEventCountLocked() {
  return SubmittedDirectEventCountLocked() +
         CountCommandDirectEvents(
             g_fable_direct.staged_submitted_render_commands);
}

void TrimSubmittedStateOnlyRenderCommandsLocked(
    std::vector<FableDirectRenderCommand>& commands) {
  const size_t original_size = commands.size();
  commands.erase(std::remove_if(commands.begin(), commands.end(),
                                [](const FableDirectRenderCommand& command) {
                                  return !RenderCommandCarriesDirectEvent(
                                      command);
                                }),
                 commands.end());
  g_fable_direct.dropped_render_commands += original_size - commands.size();
}

void TrimSubmittedRenderCommandsLocked() {
  TrimSubmittedStateOnlyRenderCommandsLocked(
      g_fable_direct.submitted_render_commands);
  if (g_fable_direct.submitted_render_commands.size() <=
      kMaxBufferedFableDirectRenderCommands) {
    return;
  }

  const size_t excess = g_fable_direct.submitted_render_commands.size() -
                        kMaxBufferedFableDirectRenderCommands;
  g_fable_direct.submitted_render_commands.erase(
      g_fable_direct.submitted_render_commands.begin(),
      g_fable_direct.submitted_render_commands.begin() + excess);
  g_fable_direct.dropped_render_commands += excess;
}

void AppendSubmittedRenderCommandsLocked(
    std::vector<FableDirectRenderCommand> commands) {
  if (commands.empty()) {
    return;
  }

  g_fable_direct.submitted_render_commands.reserve(
      std::min(kMaxBufferedFableDirectRenderCommands,
               g_fable_direct.submitted_render_commands.size() +
                   commands.size()));
  for (FableDirectRenderCommand& command : commands) {
    AttachCachedScratchBuffersToCommandLocked(command);
    // Render-target identity is not draw identity. A scene normally submits
    // many ordered scratch-wrapper draws to the same target; removing earlier
    // commands here collapsed an entire batch to its final (often off-screen)
    // quad. Explicit serial/identity supersession is handled by
    // FableDirectRemoveSupersededScratchDrawEvent instead.
    g_fable_direct.submitted_render_commands.push_back(std::move(command));
  }
  TrimSubmittedRenderCommandsLocked();
  g_fable_direct.last_submitted_draw_event_count =
      SubmittedDirectEventCountLocked();
  ++g_fable_direct.submitted_render_command_serial;
}

void TrimStagedSubmittedRenderCommandsLocked() {
  TrimSubmittedStateOnlyRenderCommandsLocked(
      g_fable_direct.staged_submitted_render_commands);
  if (g_fable_direct.staged_submitted_render_commands.size() <=
      kMaxBufferedFableDirectRenderCommands) {
    return;
  }

  const size_t excess = g_fable_direct.staged_submitted_render_commands.size() -
                        kMaxBufferedFableDirectRenderCommands;
  g_fable_direct.staged_submitted_render_commands.erase(
      g_fable_direct.staged_submitted_render_commands.begin(),
      g_fable_direct.staged_submitted_render_commands.begin() + excess);
  g_fable_direct.dropped_render_commands += excess;
}

void StageSubmittedRenderCommandsLocked(
    std::vector<FableDirectRenderCommand> commands) {
  if (commands.empty()) {
    return;
  }

  g_fable_direct.staged_submitted_render_commands.reserve(
      std::min(kMaxBufferedFableDirectRenderCommands,
               g_fable_direct.staged_submitted_render_commands.size() +
                   commands.size()));
  for (FableDirectRenderCommand& command : commands) {
    AttachCachedScratchBuffersToCommandLocked(command);
    // Guest-frame identity (publish_generation) is stamped at PRODUCTION time
    // in PushRenderCommandLocked, not here: the pump's batch drain stages the
    // whole accumulated backlog in one call, so a stage-time stamp collapsed
    // many visual frames into the single in-progress generation and the
    // supersession filter could never drop anything (run 484: 4,348-command
    // batch, zero drops).
    g_fable_direct.staged_submitted_render_commands.push_back(
        std::move(command));
  }
  TrimStagedSubmittedRenderCommandsLocked();
  g_fable_direct.last_submitted_draw_event_count =
      StagedAndSubmittedDirectEventCountLocked();
}

void PublishStagedSubmittedRenderCommandsLocked() {
  if (g_fable_direct.staged_submitted_render_commands.empty()) {
    return;
  }

  // Generations are stage-time stamps now (see guest_frame_seal_ordinal) —
  // publish no longer mints or overwrites them, so a frame published in
  // pieces keeps one identity. Immediate producers (PIX/Bink, synthetic
  // recovery) still bypass staging via AppendSubmittedRenderCommandsLocked
  // and stay at generation 0: never dropped, never claiming newest (run 475).
  for (FableDirectRenderCommand& command :
       g_fable_direct.staged_submitted_render_commands) {
    if (RenderCommandCarriesDrawEvent(command)) {
      ++g_fable_direct.funnel_draw_commands_published;
    }
  }
  AppendSubmittedRenderCommandsLocked(
      std::move(g_fable_direct.staged_submitted_render_commands));
  g_fable_direct.staged_submitted_render_commands.clear();
}

uint32_t OrderStagedNonVideoGuestFrameLocked() {
  std::vector<FableDirectRenderCommand>& commands =
      g_fable_direct.staged_submitted_render_commands;
  if (commands.size() < 2) {
    return 0;
  }

  uint32_t adjacent_inversions = 0;
  for (size_t index = 1; index < commands.size(); ++index) {
    if (commands[index].serial < commands[index - 1].serial) {
      ++adjacent_inversions;
    }
  }
  if (adjacent_inversions == 0) {
    return 0;
  }

  // Exact wrapper and scene boundaries can be observed on different hook
  // exits. They therefore prove that a command is complete, but their callback
  // order is not the guest draw order. Every command receives |serial| while
  // holding this same state mutex, so it is the canonical production order for
  // the completed guest frame. Keep equal serials stable for defensive
  // compatibility with synthetic commands.
  std::stable_sort(commands.begin(), commands.end(),
                   [](const FableDirectRenderCommand& left,
                      const FableDirectRenderCommand& right) {
                     return left.serial < right.serial;
                   });
  return adjacent_inversions;
}

bool CommandDrawEventsReferenceResource(
    const std::vector<FableDirectRenderCommand>& commands,
    const FableDirectResourceReference& resource) {
  return std::any_of(
      commands.begin(), commands.end(),
	      [&](const FableDirectRenderCommand& command) {
	        if (!RenderCommandCarriesDrawEvent(command)) {
	          return false;
	        }
	        const auto matches = [&](uint32_t address) {
	          return DirectResourceMatchesAddress(resource, address);
	        };
	        const FableDirectResourceBindings& bindings = command.resources;
	        if (matches(bindings.vertex_declaration) ||
	            matches(bindings.vertex_shader) ||
	            matches(bindings.pixel_shader) ||
	            matches(bindings.index_buffer) ||
	            matches(bindings.depth_stencil)) {
	          return true;
	        }
	        for (uint32_t render_target : bindings.render_targets) {
	          if (matches(render_target)) {
	            return true;
	          }
	        }
	        for (const RendererStreamBindingSnapshot& stream :
	             bindings.streams) {
	          if (matches(stream.buffer)) {
	            return true;
	          }
	        }
	        for (uint32_t texture : bindings.textures) {
	          if (matches(texture)) {
	            return true;
	          }
	        }
	        return false;
	      });
}

bool CommandMatchesSupersededScratchDraw(const FableDirectRenderCommand& command,
                                         uint64_t serial,
                                         uint32_t render_target) {
  if (!RenderCommandCarriesDrawEvent(command) ||
      command.draw_serial != serial ||
      command.draw.source != RendererDrawEventSource::kScratchWrapper) {
    return false;
  }
  if (render_target == 0) {
    return true;
  }
  for (const uint32_t target : command.resources.render_targets) {
    if (target == render_target) {
      return true;
    }
  }
  return false;
}

bool RemoveSupersededScratchDrawBySerial(
    std::vector<FableDirectRenderCommand>& commands, uint64_t serial,
    uint32_t render_target) {
  if (serial == 0) {
    return false;
  }
  const auto it = std::find_if(
      commands.begin(), commands.end(),
      [&](const FableDirectRenderCommand& command) {
        return CommandMatchesSupersededScratchDraw(command, serial,
                                                   render_target);
      });
  if (it == commands.end()) {
    return false;
  }
  commands.erase(it);
  return true;
}









void FillStagedCommandSummaryLocked(FableDirectStateSnapshot& snapshot) {
  for (const FableDirectRenderCommand& command :
       g_fable_direct.render_commands) {
    const size_t type_index = static_cast<size_t>(command.type);
    if (type_index < snapshot.staged_command_type_counts.size()) {
      ++snapshot.staged_command_type_counts[type_index];
      if (command.state_root.call_serial != 0 &&
          command.state_root.context_key != 0) {
        ++snapshot.staged_rooted_command_type_counts[type_index];
        if (command.state_root.symbol_address == 0x822869A0u ||
            command.state_root.symbol_address == 0x82264590u) {
          ++snapshot.staged_framebuffer_rooted_command_type_counts[type_index];
        }
      }
    }
    switch (command.type) {
      case FableDirectRenderCommandType::kClear:
        ++snapshot.staged_clear_command_count;
        break;
      case FableDirectRenderCommandType::kResolveCopy:
        ++snapshot.staged_resolve_command_count;
        break;
      case FableDirectRenderCommandType::kDrawPrimitive:
      case FableDirectRenderCommandType::kDrawIndexedPrimitive:
      case FableDirectRenderCommandType::kDrawPrimitiveUP: {
        const size_t source = static_cast<size_t>(command.draw.source);
        if (source < snapshot.staged_draw_source_counts.size()) {
          ++snapshot.staged_draw_source_counts[source];
        }
        break;
      }
      case FableDirectRenderCommandType::kSetRenderTarget:
      case FableDirectRenderCommandType::kSetDepthStencil:
      case FableDirectRenderCommandType::kSetTexture:
      case FableDirectRenderCommandType::kSetStreamSource:
      case FableDirectRenderCommandType::kSetIndices:
      case FableDirectRenderCommandType::kSetVertexDeclaration:
      case FableDirectRenderCommandType::kSetVertexShader:
      case FableDirectRenderCommandType::kSetPixelShader:
      case FableDirectRenderCommandType::kSetViewport:
      case FableDirectRenderCommandType::kSetScissor:
      case FableDirectRenderCommandType::kSetScaler:
      case FableDirectRenderCommandType::kSetRenderState:
      case FableDirectRenderCommandType::kSetSamplerState:
      case FableDirectRenderCommandType::kSetPm4Field:
      case FableDirectRenderCommandType::kStateFlush:
        ++snapshot.staged_state_command_count;
        break;
      case FableDirectRenderCommandType::kGtlResolve:
      case FableDirectRenderCommandType::kGtlTessDraw:
        // Never staged in the observer buffer; synthesized at merge time.
        break;
    }
  }
}

bool DirectResourceMatchesAddress(const FableDirectResourceReference& resource,
                                  uint32_t address) {
  if (address == 0) {
    return false;
  }
  if (resource.guest_address == address ||
      resource.data_address == address ||
      resource.auxiliary_address == address ||
      resource.parent_address == address) {
    return true;
  }
  const uint32_t aligned_address = address & ~3u;
  if (resource.buffer_resource && aligned_address != address &&
      (resource.data_address == aligned_address ||
       resource.auxiliary_address == aligned_address)) {
    return true;
  }
  if (!resource.buffer_resource || resource.data_address == 0 ||
      resource.byte_size == 0 || address < resource.data_address) {
    if (aligned_address == address ||
        aligned_address < resource.data_address) {
      return false;
    }
    return static_cast<uint64_t>(aligned_address - resource.data_address) <
           resource.byte_size;
  }
  return static_cast<uint64_t>(address - resource.data_address) <
         resource.byte_size;
}


void CopyPm4StateToDrawEvent(const FableDirectPm4State& pm4,
                             RendererDrawEventSnapshot& event) {
  event.pm4_registers_valid = pm4.valid;
  event.rb_surface_info = pm4.rb_surface_info;
  std::copy(pm4.rb_color_info.begin(), pm4.rb_color_info.end(),
            event.rb_color_info);
  event.rb_depth_info = pm4.rb_depth_info;
  event.rb_copy_control = pm4.rb_copy_control;
  event.rb_copy_dest_base = pm4.rb_copy_dest_base;
  event.rb_copy_dest_pitch = pm4.rb_copy_dest_pitch;
  event.rb_copy_dest_info = pm4.rb_copy_dest_info;
  event.rb_copy_func = pm4.rb_copy_func;
  event.rb_copy_ref = pm4.rb_copy_ref;
  event.rb_copy_mask = pm4.rb_copy_mask;
  event.rb_copy_surface_slice = pm4.rb_copy_surface_slice;
  event.rb_modecontrol = pm4.rb_modecontrol;
  event.rb_depthcontrol = pm4.rb_depthcontrol;
  event.rb_color_mask = pm4.rb_color_mask;
  std::copy(pm4.rb_blendcontrol.begin(), pm4.rb_blendcontrol.end(),
            event.rb_blendcontrol);
  event.rb_colorcontrol = pm4.rb_colorcontrol;
  event.rb_alpha_ref = pm4.rb_alpha_ref;
  event.rb_stencilrefmask = pm4.rb_stencilrefmask;
  event.rb_stencilrefmask_bf = pm4.rb_stencilrefmask_bf;
  event.pa_su_sc_mode_cntl = pm4.pa_su_sc_mode_cntl;
}

FableDirectPm4State Pm4StateFromDrawEvent(
    const RendererDrawEventSnapshot& event) {
  FableDirectPm4State pm4;
  pm4.valid = event.pm4_registers_valid;
  pm4.rb_surface_info = event.rb_surface_info;
  std::copy(std::begin(event.rb_color_info), std::end(event.rb_color_info),
            pm4.rb_color_info.begin());
  pm4.rb_depth_info = event.rb_depth_info;
  pm4.rb_copy_control = event.rb_copy_control;
  pm4.rb_copy_dest_base = event.rb_copy_dest_base;
  pm4.rb_copy_dest_pitch = event.rb_copy_dest_pitch;
  pm4.rb_copy_dest_info = event.rb_copy_dest_info;
  pm4.rb_copy_func = event.rb_copy_func;
  pm4.rb_copy_ref = event.rb_copy_ref;
  pm4.rb_copy_mask = event.rb_copy_mask;
  pm4.rb_copy_surface_slice = event.rb_copy_surface_slice;
  pm4.rb_modecontrol = event.rb_modecontrol;
  pm4.rb_depthcontrol = event.rb_depthcontrol;
  pm4.rb_color_mask = event.rb_color_mask;
  std::copy(std::begin(event.rb_blendcontrol),
            std::end(event.rb_blendcontrol), pm4.rb_blendcontrol.begin());
  pm4.rb_colorcontrol = event.rb_colorcontrol;
  pm4.rb_alpha_ref = event.rb_alpha_ref;
  pm4.rb_stencilrefmask = event.rb_stencilrefmask;
  pm4.rb_stencilrefmask_bf = event.rb_stencilrefmask_bf;
  pm4.pa_su_sc_mode_cntl = event.pa_su_sc_mode_cntl;
  return pm4;
}

void PopulateDrawEventStateLocked(RendererDrawEventSnapshot& event) {
  ApplyRenderStatePm4Locked();
  std::copy(g_fable_direct.targets.color.begin(),
            g_fable_direct.targets.color.end(), event.render_targets);
  event.depth_stencil = g_fable_direct.targets.depth;
  for (uint32_t slot = 0; slot < g_fable_direct.target_roots.color.size();
       ++slot) {
    event.clear_target_root_call_serial[slot] =
        g_fable_direct.target_roots.color[slot].call_serial;
    event.clear_target_root_context_key[slot] =
        g_fable_direct.target_roots.color[slot].context_key;
    event.clear_target_root_symbol_address[slot] =
        g_fable_direct.target_roots.color[slot].symbol_address;
    event.clear_target_root_lr[slot] =
        g_fable_direct.target_roots.color[slot].lr;
  }
  event.clear_depth_root_call_serial =
      g_fable_direct.target_roots.depth.call_serial;
  event.clear_depth_root_context_key =
      g_fable_direct.target_roots.depth.context_key;
  event.clear_depth_root_symbol_address =
      g_fable_direct.target_roots.depth.symbol_address;
  event.clear_depth_root_lr = g_fable_direct.target_roots.depth.lr;
  event.vertex_declaration = g_fable_direct.shaders.vertex_declaration;
  event.vertex_shader = g_fable_direct.shaders.vertex_shader;
  event.vertex_shader_root_call_serial =
      g_fable_direct.shader_roots.vertex_shader.call_serial;
  event.vertex_shader_root_context_key =
      g_fable_direct.shader_roots.vertex_shader.context_key;
  event.vertex_shader_root_symbol_address =
      g_fable_direct.shader_roots.vertex_shader.symbol_address;
  event.vertex_shader_root_lr = g_fable_direct.shader_roots.vertex_shader.lr;
  event.pixel_shader = g_fable_direct.shaders.pixel_shader;
  event.pixel_shader_root_call_serial =
      g_fable_direct.shader_roots.pixel_shader.call_serial;
  event.pixel_shader_root_context_key =
      g_fable_direct.shader_roots.pixel_shader.context_key;
  event.pixel_shader_root_symbol_address =
      g_fable_direct.shader_roots.pixel_shader.symbol_address;
  event.pixel_shader_root_lr = g_fable_direct.shader_roots.pixel_shader.lr;
  event.index_buffer = g_fable_direct.input.index_buffer;
  event.render_states = g_fable_direct.render_states;
  std::copy(g_fable_direct.input.streams.begin(),
            g_fable_direct.input.streams.end(), event.streams);
  std::copy(g_fable_direct.textures.textures.begin(),
            g_fable_direct.textures.textures.end(), event.textures);
  event.sampler_states = g_fable_direct.textures.samplers;
  event.fetch_constants = g_fable_direct.constants.fetch;
  event.vertex_shader_constants = g_fable_direct.constants.vertex_float;
  event.pixel_shader_constants = g_fable_direct.constants.pixel_float;
  event.vertex_shader_int_constants = g_fable_direct.constants.vertex_int;
  event.pixel_shader_int_constants = g_fable_direct.constants.pixel_int;
  event.vertex_shader_int_constant_valid_mask =
      g_fable_direct.constants.vertex_int_valid_mask;
  event.pixel_shader_int_constant_valid_mask =
      g_fable_direct.constants.pixel_int_valid_mask;
  event.vertex_shader_bool_constants = g_fable_direct.constants.vertex_bool;
  event.pixel_shader_bool_constants = g_fable_direct.constants.pixel_bool;
  event.clear_flags = g_fable_direct.clear_flags;
  CopyPm4StateToDrawEvent(g_fable_direct.pm4, event);
  event.viewport = g_fable_direct.viewport;
  event.scissor = g_fable_direct.scissor;
  event.scaler = g_fable_direct.scaler;
}



}  // namespace

bool FableDirectAttachCachedScratchBuffers(
    FableDirectRenderCommand& command) {
  const auto ready_signature = [](const FableDirectRenderCommand& command) {
    uint64_t signature = command.inline_index_buffer.ready() ? 1ull : 0ull;
    for (size_t slot = 0; slot < command.inline_vertex_buffers.size(); ++slot) {
      const FableDirectInlineBuffer& buffer =
          command.inline_vertex_buffers[slot];
      if (!buffer.ready()) {
        continue;
      }
      signature ^= (uint64_t(slot + 1u) << 56u) ^
                   (uint64_t(buffer.address) << 24u) ^
                   (uint64_t(buffer.byte_size) << 1u) ^
                   uint64_t(buffer.stride_or_format);
      signature *= 1099511628211ull;
    }
    if (command.inline_index_buffer.ready()) {
      signature ^= (uint64_t(command.inline_index_buffer.address) << 24u) ^
                   (uint64_t(command.inline_index_buffer.byte_size) << 1u) ^
                   uint64_t(command.inline_index_buffer.stride_or_format);
      signature *= 1099511628211ull;
    }
    return signature;
  };

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint64_t before = ready_signature(command);
  AttachCachedScratchBuffersToCommandLocked(command);
  return ready_signature(command) != before;
}

bool FableDirectIsScratchByteBufferRange(uint32_t guest_address,
                                         uint32_t size) {
  return IsHighScratchByteBufferRange(guest_address, size);
}

void ResetFableDirectState() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  ResetStateLocked();
  TouchStateLocked();
}

void FableDirectSetRenderTarget(uint32_t device, uint32_t slot,
                                uint32_t surface,
                                FableDirectStateRoot root) {
  if (slot >= g_fable_direct.targets.color.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_surface = g_fable_direct.targets.color[slot];
  const FableDirectStateRoot previous_root =
      g_fable_direct.target_roots.color[slot];
  SetDirtyValue(g_fable_direct.dirty.targets,
                g_fable_direct.targets.color[slot], surface);
  if (previous_surface != surface) {
    ++g_fable_direct.render_target_switch_count;
  }
  if (surface == 0) {
    g_fable_direct.target_roots.color[slot] = {};
  } else if (root.call_serial != 0 && root.context_key != 0) {
    g_fable_direct.target_roots.color[slot] = root;
  } else if (previous_surface == surface) {
    g_fable_direct.target_roots.color[slot] = previous_root;
  } else {
    g_fable_direct.target_roots.color[slot] = {};
  }
  g_fable_direct.dirty.pipeline = true;
  ApplyRenderStatePm4Locked();
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetRenderTarget,
          g_fable_direct.target_roots.color[slot])) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetRenderTarget;
    command.device = device;
    command.slot = slot;
    command.value = surface;
    command.state_root = g_fable_direct.target_roots.color[slot];
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorRenderTargetState(uint32_t device, uint32_t slot,
                                        uint32_t surface,
                                        FableDirectStateRoot root) {
  if (slot >= g_fable_direct.targets.color.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_surface = g_fable_direct.targets.color[slot];
  const FableDirectStateRoot previous_root =
      g_fable_direct.target_roots.color[slot];
  SetDirtyValue(g_fable_direct.dirty.targets,
                g_fable_direct.targets.color[slot], surface);
  if (previous_surface != surface) {
    ++g_fable_direct.render_target_switch_count;
  }
  if (surface == 0) {
    g_fable_direct.target_roots.color[slot] = {};
  } else if (root.call_serial != 0 && root.context_key != 0) {
    g_fable_direct.target_roots.color[slot] = root;
  } else if (previous_surface == surface) {
    g_fable_direct.target_roots.color[slot] = previous_root;
  } else {
    g_fable_direct.target_roots.color[slot] = {};
  }
  g_fable_direct.dirty.pipeline = true;
  ApplyRenderStatePm4Locked();
  TouchStateLocked(device);
}

void FableDirectSetDepthStencil(uint32_t device, uint32_t surface,
                                FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_surface = g_fable_direct.targets.depth;
  const FableDirectStateRoot previous_root = g_fable_direct.target_roots.depth;
  SetDirtyValue(g_fable_direct.dirty.targets, g_fable_direct.targets.depth,
                surface);
  if (previous_surface != surface) {
    ++g_fable_direct.depth_target_switch_count;
  }
  if (surface == 0) {
    g_fable_direct.target_roots.depth = {};
  } else if (root.call_serial != 0 && root.context_key != 0) {
    g_fable_direct.target_roots.depth = root;
  } else if (previous_surface == surface) {
    g_fable_direct.target_roots.depth = previous_root;
  } else {
    g_fable_direct.target_roots.depth = {};
  }
  g_fable_direct.dirty.pipeline = true;
  ApplyRenderStatePm4Locked();
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetDepthStencil,
          g_fable_direct.target_roots.depth)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetDepthStencil;
    command.device = device;
    command.value = surface;
    command.state_root = g_fable_direct.target_roots.depth;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorDepthStencilState(uint32_t device, uint32_t surface,
                                        FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_surface = g_fable_direct.targets.depth;
  const FableDirectStateRoot previous_root = g_fable_direct.target_roots.depth;
  SetDirtyValue(g_fable_direct.dirty.targets, g_fable_direct.targets.depth,
                surface);
  if (previous_surface != surface) {
    ++g_fable_direct.depth_target_switch_count;
  }
  if (surface == 0) {
    g_fable_direct.target_roots.depth = {};
  } else if (root.call_serial != 0 && root.context_key != 0) {
    g_fable_direct.target_roots.depth = root;
  } else if (previous_surface == surface) {
    g_fable_direct.target_roots.depth = previous_root;
  } else {
    g_fable_direct.target_roots.depth = {};
  }
  g_fable_direct.dirty.pipeline = true;
  ApplyRenderStatePm4Locked();
  TouchStateLocked(device);
}

void FableDirectSetTexture(uint32_t device, uint32_t slot, uint32_t texture,
                           FableDirectStateRoot root) {
  if (slot >= g_fable_direct.textures.textures.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.textures,
                g_fable_direct.textures.textures[slot], texture);
  if (!ConsumeExactStateCommandLocked(FableDirectRenderCommandType::kSetTexture,
                                      root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetTexture;
    command.device = device;
    command.slot = slot;
    command.value = texture;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorTextureState(uint32_t device, uint32_t slot,
                                   uint32_t texture) {
  if (slot >= g_fable_direct.textures.textures.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.textures,
                g_fable_direct.textures.textures[slot], texture);
  TouchStateLocked(device);
}

void FableDirectSetStream(uint32_t device, uint32_t slot,
                          const RendererStreamBindingSnapshot& stream,
                          FableDirectStateRoot root) {
  if (slot >= g_fable_direct.input.streams.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  RendererStreamBindingSnapshot& destination =
      g_fable_direct.input.streams[slot];
  if (destination.buffer != stream.buffer ||
      destination.offset != stream.offset ||
      destination.stride != stream.stride) {
    destination = stream;
    g_fable_direct.dirty.input = true;
  }
  g_fable_direct.dirty.pipeline = true;
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetStreamSource, root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetStreamSource;
    command.device = device;
    command.slot = slot;
    command.stream = stream;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorStreamState(
    uint32_t device, uint32_t slot,
    const RendererStreamBindingSnapshot& stream) {
  if (slot >= g_fable_direct.input.streams.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  RendererStreamBindingSnapshot& destination =
      g_fable_direct.input.streams[slot];
  if (destination.buffer != stream.buffer ||
      destination.offset != stream.offset ||
      destination.stride != stream.stride) {
    destination = stream;
    g_fable_direct.dirty.input = true;
  }
  g_fable_direct.dirty.pipeline = true;
  TouchStateLocked(device);
}

void FableDirectSetIndexBuffer(uint32_t device, uint32_t index_buffer,
                               FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.input, g_fable_direct.input.index_buffer,
                index_buffer);
  if (!ConsumeExactStateCommandLocked(FableDirectRenderCommandType::kSetIndices,
                                      root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetIndices;
    command.device = device;
    command.value = index_buffer;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorIndexBufferState(uint32_t device,
                                       uint32_t index_buffer) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.input, g_fable_direct.input.index_buffer,
                index_buffer);
  TouchStateLocked(device);
}

void FableDirectSetVertexDeclaration(uint32_t device,
                                     uint32_t vertex_declaration,
                                     FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.shaders,
                g_fable_direct.shaders.vertex_declaration,
                vertex_declaration);
  g_fable_direct.dirty.pipeline = true;
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetVertexDeclaration, root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetVertexDeclaration;
    command.device = device;
    command.value = vertex_declaration;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorVertexDeclarationState(uint32_t device,
                                             uint32_t vertex_declaration) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.shaders,
                g_fable_direct.shaders.vertex_declaration,
                vertex_declaration);
  g_fable_direct.dirty.pipeline = true;
  TouchStateLocked(device);
}

void FableDirectSetVertexShader(uint32_t device, uint32_t vertex_shader,
                                FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_shader = g_fable_direct.shaders.vertex_shader;
  const FableDirectStateRoot previous_root =
      g_fable_direct.shader_roots.vertex_shader;
  SetDirtyValue(g_fable_direct.dirty.shaders,
                g_fable_direct.shaders.vertex_shader, vertex_shader);
  if (vertex_shader == 0) {
    g_fable_direct.shader_roots.vertex_shader = {};
  } else if (root.call_serial != 0 && root.context_key != 0) {
    g_fable_direct.shader_roots.vertex_shader = root;
  } else if (previous_shader == vertex_shader) {
    g_fable_direct.shader_roots.vertex_shader = previous_root;
  } else {
    g_fable_direct.shader_roots.vertex_shader = {};
  }
  g_fable_direct.dirty.pipeline = true;
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetVertexShader,
          g_fable_direct.shader_roots.vertex_shader)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetVertexShader;
    command.device = device;
    command.value = vertex_shader;
    command.state_root = g_fable_direct.shader_roots.vertex_shader;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorVertexShaderState(uint32_t device,
                                        uint32_t vertex_shader) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_shader = g_fable_direct.shaders.vertex_shader;
  const FableDirectStateRoot previous_root =
      g_fable_direct.shader_roots.vertex_shader;
  SetDirtyValue(g_fable_direct.dirty.shaders,
                g_fable_direct.shaders.vertex_shader, vertex_shader);
  if (vertex_shader == 0) {
    g_fable_direct.shader_roots.vertex_shader = {};
  } else if (previous_shader == vertex_shader) {
    g_fable_direct.shader_roots.vertex_shader = previous_root;
  } else {
    g_fable_direct.shader_roots.vertex_shader = {};
  }
  g_fable_direct.dirty.pipeline = true;
  TouchStateLocked(device);
}

void FableDirectSetPixelShader(uint32_t device, uint32_t pixel_shader,
                               FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_shader = g_fable_direct.shaders.pixel_shader;
  const FableDirectStateRoot previous_root =
      g_fable_direct.shader_roots.pixel_shader;
  SetDirtyValue(g_fable_direct.dirty.shaders,
                g_fable_direct.shaders.pixel_shader, pixel_shader);
  if (pixel_shader == 0) {
    g_fable_direct.shader_roots.pixel_shader = {};
  } else if (root.call_serial != 0 && root.context_key != 0) {
    g_fable_direct.shader_roots.pixel_shader = root;
  } else if (previous_shader == pixel_shader) {
    g_fable_direct.shader_roots.pixel_shader = previous_root;
  } else {
    g_fable_direct.shader_roots.pixel_shader = {};
  }
  g_fable_direct.dirty.pipeline = true;
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetPixelShader,
          g_fable_direct.shader_roots.pixel_shader)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetPixelShader;
    command.device = device;
    command.value = pixel_shader;
    command.state_root = g_fable_direct.shader_roots.pixel_shader;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorPixelShaderState(uint32_t device, uint32_t pixel_shader) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_shader = g_fable_direct.shaders.pixel_shader;
  const FableDirectStateRoot previous_root =
      g_fable_direct.shader_roots.pixel_shader;
  SetDirtyValue(g_fable_direct.dirty.shaders,
                g_fable_direct.shaders.pixel_shader, pixel_shader);
  if (pixel_shader == 0) {
    g_fable_direct.shader_roots.pixel_shader = {};
  } else if (previous_shader == pixel_shader) {
    g_fable_direct.shader_roots.pixel_shader = previous_root;
  } else {
    g_fable_direct.shader_roots.pixel_shader = {};
  }
  g_fable_direct.dirty.pipeline = true;
  TouchStateLocked(device);
}

void FableDirectSetViewport(uint32_t device,
                            const RendererViewportSnapshot& viewport,
                            FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  g_fable_direct.viewport = viewport;
  g_fable_direct.dirty.viewport = true;
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetViewport, root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetViewport;
    command.device = device;
    command.viewport = viewport;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorViewportState(uint32_t device,
                                    const RendererViewportSnapshot& viewport) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  g_fable_direct.viewport = viewport;
  g_fable_direct.dirty.viewport = true;
  TouchStateLocked(device);
}

void FableDirectSetScissor(uint32_t device,
                           const RendererScissorSnapshot& scissor,
                           FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  g_fable_direct.scissor = scissor;
  g_fable_direct.dirty.scissor = true;
  if (!ConsumeExactStateCommandLocked(FableDirectRenderCommandType::kSetScissor,
                                      root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetScissor;
    command.device = device;
    command.scissor = scissor;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectMirrorScissorState(uint32_t device,
                                   const RendererScissorSnapshot& scissor) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  g_fable_direct.scissor = scissor;
  g_fable_direct.dirty.scissor = true;
  TouchStateLocked(device);
}

void FableDirectSetScaler(uint32_t device,
                          const RendererScalerSnapshot& scaler) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  g_fable_direct.scaler = scaler;
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetScaler;
  command.device = device;
  command.scaler = scaler;
  PushRenderCommandLocked(std::move(command));
  TouchStateLocked(device);
}

void FableDirectSetRenderState(uint32_t device, uint32_t slot, uint32_t value,
                               FableDirectStateRoot root) {
  if (slot >= g_fable_direct.render_states.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.pipeline,
                g_fable_direct.render_states[slot], value);
  ApplyRenderStatePm4Locked();
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetRenderState, root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetRenderState;
    command.device = device;
    command.slot = slot;
    command.value = value;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}

void FableDirectSetRenderStateSnapshot(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  g_fable_direct.render_states = states;
  g_fable_direct.dirty.pipeline = true;
  ApplyRenderStatePm4Locked();
  TouchStateLocked();
}

uint32_t FableDirectGetRenderState(uint32_t slot) {
  if (slot >= g_fable_direct.render_states.size()) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  return g_fable_direct.render_states[slot];
}


void FableDirectSetPackedSamplerStateSnapshot(
    const std::array<uint32_t, kRendererD3DSamplerStateSlots>& states) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  g_fable_direct.textures.samplers = states;
  g_fable_direct.dirty.textures = true;
  TouchStateLocked();
}

uint32_t FableDirectGetPackedSamplerState(uint32_t slot) {
  if (slot >= g_fable_direct.textures.samplers.size()) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  return g_fable_direct.textures.samplers[slot];
}

// Public pure-packing entry point. The implementation lives in this file's
// anonymous namespace, so callers outside this TU (the GTL, which keeps its own
// sampler mirror) reach it through here rather than through
// FableDirectStorePackedSamplerState, which also mutates direct-pipeline state.
uint32_t FableDirectPackSamplerStateValue(uint32_t packed, uint32_t state_slot,
                                          uint32_t value) {
  return StorePackedSamplerStateValue(packed, state_slot, value);
}

uint32_t FableDirectStorePackedSamplerState(uint32_t device, uint32_t slot,
                                            uint32_t state_slot,
                                            uint32_t value,
                                            FableDirectStateRoot root) {
  if (slot >= g_fable_direct.textures.samplers.size()) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t packed_state = StorePackedSamplerStateValue(
      g_fable_direct.textures.samplers[slot], state_slot, value);
  SetDirtyValue(g_fable_direct.dirty.textures,
                g_fable_direct.textures.samplers[slot], packed_state);
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetSamplerState, root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetSamplerState;
    command.device = device;
    command.slot = slot;
    command.value = packed_state;
    command.value2 = state_slot;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
  return packed_state;
}

void FableDirectSetFetchConstant(uint32_t dword, uint32_t value) {
  if (dword >= g_fable_direct.constants.fetch.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.constants,
                g_fable_direct.constants.fetch[dword], value);
  TouchStateLocked();
}

void FableDirectSetFetchConstantRange(uint32_t first_dword,
                                      const uint32_t* values,
                                      uint32_t dword_count) {
  if (values == nullptr || first_dword >= g_fable_direct.constants.fetch.size()) {
    return;
  }

  const uint32_t writable_dwords = std::min<uint32_t>(
      dword_count, g_fable_direct.constants.fetch.size() - first_dword);
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  std::copy_n(values, writable_dwords,
              g_fable_direct.constants.fetch.begin() + first_dword);
  g_fable_direct.dirty.constants = true;
  TouchStateLocked();
}

void FableDirectSetShaderFloatConstant(uint32_t pixel_bank, uint32_t dword,
                                       uint32_t value) {
  auto& constants = pixel_bank != 0 ? g_fable_direct.constants.pixel_float
                                    : g_fable_direct.constants.vertex_float;
  if (dword >= constants.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.constants, constants[dword], value);
  TouchStateLocked();
}

void FableDirectSetShaderFloatConstantRange(uint32_t pixel_bank,
                                            uint32_t first_dword,
                                            const uint32_t* values,
                                            uint32_t dword_count) {
  auto& constants = pixel_bank != 0 ? g_fable_direct.constants.pixel_float
                                    : g_fable_direct.constants.vertex_float;
  if (values == nullptr || first_dword >= constants.size()) {
    return;
  }

  const uint32_t writable_dwords =
      std::min<uint32_t>(dword_count, constants.size() - first_dword);
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  std::copy_n(values, writable_dwords, constants.begin() + first_dword);
  g_fable_direct.dirty.constants = true;
  TouchStateLocked();
}

void FableDirectSetShaderFloatConstantsSnapshot(
    uint32_t pixel_bank,
    const std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  if (pixel_bank != 0) {
    g_fable_direct.constants.pixel_float = constants;
  } else {
    g_fable_direct.constants.vertex_float = constants;
  }
  g_fable_direct.dirty.constants = true;
  TouchStateLocked();
}

void FableDirectSetShaderIntConstantRange(uint32_t pixel_bank,
                                          uint32_t first_dword,
                                          const uint32_t* values,
                                          uint32_t dword_count) {
  auto& constants = pixel_bank != 0 ? g_fable_direct.constants.pixel_int
                                    : g_fable_direct.constants.vertex_int;
  if (values == nullptr || first_dword >= constants.size()) {
    return;
  }

  const uint32_t writable_dwords =
      std::min<uint32_t>(dword_count, constants.size() - first_dword);
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  std::copy_n(values, writable_dwords, constants.begin() + first_dword);
  g_fable_direct.dirty.constants = true;
  TouchStateLocked();
}

void FableDirectSetShaderIntValidMask(uint32_t pixel_bank,
                                      uint32_t valid_mask) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  uint32_t& destination = pixel_bank != 0
                              ? g_fable_direct.constants.pixel_int_valid_mask
                              : g_fable_direct.constants.vertex_int_valid_mask;
  SetDirtyValue(g_fable_direct.dirty.constants, destination, valid_mask);
  TouchStateLocked();
}

void FableDirectSetShaderBoolConstantsSnapshot(
    uint32_t pixel_bank,
    const std::array<uint32_t, kRendererShaderBoolConstantDwords>& constants) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  if (pixel_bank != 0) {
    g_fable_direct.constants.pixel_bool = constants;
  } else {
    g_fable_direct.constants.vertex_bool = constants;
  }
  g_fable_direct.dirty.constants = true;
  TouchStateLocked();
}

void FableDirectCopyShaderFlowConstants(
    std::array<uint32_t, kRendererShaderBoolConstantDwords>& vertex_bool,
    std::array<uint32_t, kRendererShaderBoolConstantDwords>& pixel_bool,
    std::array<uint32_t, kRendererShaderIntConstantDwords>& vertex_int,
    std::array<uint32_t, kRendererShaderIntConstantDwords>& pixel_int,
    uint32_t& vertex_int_valid_mask, uint32_t& pixel_int_valid_mask) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  vertex_bool = g_fable_direct.constants.vertex_bool;
  pixel_bool = g_fable_direct.constants.pixel_bool;
  vertex_int = g_fable_direct.constants.vertex_int;
  pixel_int = g_fable_direct.constants.pixel_int;
  vertex_int_valid_mask = g_fable_direct.constants.vertex_int_valid_mask;
  pixel_int_valid_mask = g_fable_direct.constants.pixel_int_valid_mask;
}

void ApplyPm4FieldValueLocked(FableDirectPm4Field field, uint32_t value) {
  FableDirectPm4State& pm4 = g_fable_direct.pm4;
  pm4.valid = true;
  switch (field) {
    case FableDirectPm4Field::kSurfaceInfo:
      pm4.rb_surface_info = value;
      break;
    case FableDirectPm4Field::kColorInfo0:
      pm4.rb_color_info[0] = value;
      break;
    case FableDirectPm4Field::kColorInfo1:
      pm4.rb_color_info[1] = value;
      break;
    case FableDirectPm4Field::kColorInfo2:
      pm4.rb_color_info[2] = value;
      break;
    case FableDirectPm4Field::kColorInfo3:
      pm4.rb_color_info[3] = value;
      break;
    case FableDirectPm4Field::kDepthInfo:
      pm4.rb_depth_info = value;
      break;
    case FableDirectPm4Field::kCopyControl:
      pm4.rb_copy_control = value;
      break;
    case FableDirectPm4Field::kCopyDestBase:
      pm4.rb_copy_dest_base = value;
      break;
    case FableDirectPm4Field::kCopyDestPitch:
      pm4.rb_copy_dest_pitch = value;
      break;
    case FableDirectPm4Field::kCopyDestInfo:
      pm4.rb_copy_dest_info = value;
      break;
    case FableDirectPm4Field::kCopyFunc:
      pm4.rb_copy_func = value;
      break;
    case FableDirectPm4Field::kCopyRef:
      pm4.rb_copy_ref = value;
      break;
    case FableDirectPm4Field::kCopyMask:
      pm4.rb_copy_mask = value;
      break;
    case FableDirectPm4Field::kCopySurfaceSlice:
      pm4.rb_copy_surface_slice = value;
      break;
    case FableDirectPm4Field::kModeControl:
      pm4.rb_modecontrol = value;
      break;
    case FableDirectPm4Field::kDepthControl:
      pm4.rb_depthcontrol = value;
      break;
    case FableDirectPm4Field::kColorMask:
      pm4.rb_color_mask = value;
      break;
    case FableDirectPm4Field::kBlendControl0:
      pm4.rb_blendcontrol[0] = value;
      break;
    case FableDirectPm4Field::kBlendControl1:
      pm4.rb_blendcontrol[1] = value;
      break;
    case FableDirectPm4Field::kBlendControl2:
      pm4.rb_blendcontrol[2] = value;
      break;
    case FableDirectPm4Field::kBlendControl3:
      pm4.rb_blendcontrol[3] = value;
      break;
    case FableDirectPm4Field::kColorControl:
      pm4.rb_colorcontrol = value;
      break;
    case FableDirectPm4Field::kAlphaRef:
      pm4.rb_alpha_ref = value;
      break;
    case FableDirectPm4Field::kStencilRefMask:
      pm4.rb_stencilrefmask = value;
      break;
    case FableDirectPm4Field::kStencilRefMaskBackFace:
      pm4.rb_stencilrefmask_bf = value;
      break;
    case FableDirectPm4Field::kPaSuScModeCntl:
      pm4.pa_su_sc_mode_cntl = value;
      break;
  }
}

void FableDirectSetPm4Field(FableDirectPm4Field field, uint32_t value,
                            FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  ApplyPm4FieldValueLocked(field, value);
  g_fable_direct.dirty.pm4 = true;
  g_fable_direct.dirty.pipeline = true;
  if (!ConsumeExactStateCommandLocked(
          FableDirectRenderCommandType::kSetPm4Field, root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kSetPm4Field;
    command.slot = static_cast<uint32_t>(field);
    command.value = value;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked();
}

void FableDirectMirrorPm4Field(FableDirectPm4Field field, uint32_t value) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  ApplyPm4FieldValueLocked(field, value);
  g_fable_direct.dirty.pm4 = true;
  g_fable_direct.dirty.pipeline = true;
  TouchStateLocked();
}

void FableDirectRecordStateFlush(uint32_t device, uint32_t lr,
                                 FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  ++g_fable_direct.state_flush_count;
  if (!ConsumeExactStateCommandLocked(FableDirectRenderCommandType::kStateFlush,
                                      root)) {
    FableDirectRenderCommand command;
    command.type = FableDirectRenderCommandType::kStateFlush;
    command.device = device;
    command.value = lr;
    command.state_root = root;
    PushRenderCommandLocked(std::move(command));
  }
  TouchStateLocked(device);
}



bool TryCaptureExactNonVideoRenderCommandsLocked(uint64_t submit,
                                                 bool log_miss,
                                                 bool publish);





bool FableDirectRemoveSupersededScratchDrawEvent(uint64_t serial,
                                                 uint32_t render_target) {
  if (serial == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const bool removed_pending =
      RemoveSupersededScratchDrawBySerial(g_fable_direct.render_commands,
                                          serial, render_target);
  const bool removed =
      removed_pending |
      RemoveSupersededScratchDrawBySerial(
          g_fable_direct.staged_submitted_render_commands, serial,
          render_target) |
      RemoveSupersededScratchDrawBySerial(
          g_fable_direct.submitted_render_commands, serial, render_target);
  if (removed) {
    if (removed_pending) {
      g_fable_direct.render_command_draw_event_count =
          CountCommandDrawEvents(g_fable_direct.render_commands);
      g_fable_direct.render_command_direct_event_count =
          CountCommandDirectEvents(g_fable_direct.render_commands);
    }
    TouchStateLocked();
  }
  return removed;
}


bool StoreInlineScratchBuffer(FableDirectRenderCommand& command,
                              bool index_buffer, uint32_t address,
                              uint32_t stride_or_format, const void* data,
                              size_t size, bool pinned_per_draw) {
  if (address == 0 || !ScratchUploadSnapshotValid(data, size)) {
    return false;
  }
  const uint8_t* bytes = static_cast<const uint8_t*>(data);

  FableDirectInlineBuffer* inline_buffer = nullptr;
  uint32_t inline_address = address;
  uint32_t inline_stride_or_format = stride_or_format;
  uint32_t matched_stream_slot = UINT32_MAX;
  size_t upload_offset = 0;
  // The scratch payload arrives keyed by the CPU-view ring pointer (0xFFxx),
  // but commands whose bindings came from decoded device state hold the GPU
  // data pointer (0x10xx) for the same allocation — match either view, or
  // the attach silently misses and the draw fails missing_vertex_input.
  const uint32_t decoded_address = DecodeFableGpuBufferPointer(address);
  const auto match_index = [&](uint32_t base_address) {
    const uint32_t command_address = command.resources.index_buffer;
    if (command_address == base_address) {
      inline_buffer = &command.inline_index_buffer;
      inline_address = command_address;
      upload_offset = 0;
      return true;
    }
    if (command_address > base_address &&
        uint64_t(command_address - base_address) < uint64_t(size)) {
      inline_buffer = &command.inline_index_buffer;
      inline_address = command_address;
      upload_offset = command_address - base_address;
      return true;
    }
    return false;
  };
  const auto match_vertex = [&](uint32_t base_address) {
    for (uint32_t slot = 0; slot < command.resources.streams.size(); ++slot) {
      const uint32_t command_stride = command.resources.streams[slot].stride;
      const uint32_t effective_stride =
          stride_or_format != 0 ? stride_or_format : command_stride;
      if (effective_stride == 0 ||
          (command_stride != 0 && command_stride != effective_stride)) {
        continue;
      }
      const uint32_t command_address = command.resources.streams[slot].buffer;
      if (command_address == base_address) {
        inline_buffer = &command.inline_vertex_buffers[slot];
        inline_address = command_address;
        inline_stride_or_format = effective_stride;
        matched_stream_slot = slot;
        upload_offset = 0;
        return true;
      }
      if (command_address > base_address &&
          uint64_t(command_address - base_address) < uint64_t(size)) {
        inline_buffer = &command.inline_vertex_buffers[slot];
        inline_address =
            command.draw.draw.indexed ? base_address : command_address;
        inline_stride_or_format = effective_stride;
        matched_stream_slot = slot;
        upload_offset =
            command.draw.draw.indexed ? 0u : command_address - base_address;
        return true;
      }
    }
    return false;
  };
  const bool alias_differs = decoded_address != 0 && decoded_address != address;
  if (index_buffer) {
    if (!match_index(address) &&
        (!alias_differs || !match_index(decoded_address))) {
      return false;
    }
  } else {
    if (!match_vertex(address) &&
        (!alias_differs || !match_vertex(decoded_address))) {
      return false;
    }
  }

  // Per-draw geometry pinning: a command that already carries a NONZERO
  // payload for this binding keeps it. The scratch ring reuses addresses
  // within a compose burst, so a later write keyed by the same address is a
  // DIFFERENT draw's geometry — overwriting here bakes wrong-rect tiles into
  // the title composite. All-zero payloads stay replaceable (the Begin-exit
  // capture attaches before the guest writes the ring; the delayed finalize
  // must be able to fill in the real bytes).
  if (inline_buffer->ready() &&
      std::any_of(inline_buffer->data.begin(), inline_buffer->data.end(),
                  [](uint8_t byte) { return byte != 0; })) {
    return false;
  }

  size_t inline_size = size - upload_offset;
  if (inline_size == 0 ||
      inline_size > kMaxFableDirectScratchUploadSnapshotSize ||
      inline_size > std::numeric_limits<uint32_t>::max()) {
    return false;
  }
  if (index_buffer) {
    const uint32_t index_count =
        DrawVertexOrIndexCount(command.draw.draw.primitive_type,
                               command.draw.draw.primitive_count);
    const uint32_t index_stride =
        stride_or_format == 4u ? 4u : 2u;
    const uint64_t required_size =
        (uint64_t(command.draw.draw.start) + uint64_t(index_count)) *
        uint64_t(index_stride);
    if (required_size != 0 && required_size < inline_size &&
        required_size <= std::numeric_limits<uint32_t>::max()) {
      inline_size = static_cast<size_t>(required_size);
    }
    inline_stride_or_format = index_stride;
  }
  if (index_buffer && inline_buffer->ready()) {
    const uint32_t vertex_capacity =
        PrimaryInlineVertexCapacityForCommand(command);
    if (vertex_capacity != 0 &&
        InlineIndexBufferLooksValidForCommand(
            command, inline_buffer->data.data(), inline_buffer->byte_size,
            inline_buffer->stride_or_format, vertex_capacity) &&
        !InlineIndexBufferLooksValidForCommand(
            command, bytes + upload_offset, inline_size,
            inline_stride_or_format, vertex_capacity)) {
      return false;
    }
  }
  if (!index_buffer && matched_stream_slot < command.resources.streams.size() &&
      inline_stride_or_format != 0) {
    const RendererStreamBindingSnapshot& stream =
        command.resources.streams[matched_stream_slot];
    // Fable's BeginIndexedVertices wrappers often allocate one shared scratch
    // arena with a stable VB base and several IB chunks placed after it. The
    // command's current IB address can therefore point into the same arena while
    // a later wrapper is still attaching the authoritative VB payload. Do not
    // clamp the VB snapshot to "bytes before IB"; oversized VB views are benign,
    // but trimming here leaves indexed title/menu draws with too few vertices.
    const uint32_t vertex_count =
        DrawVertexOrIndexCount(command.draw.draw.primitive_type,
                               command.draw.draw.primitive_count);
    const uint64_t first_vertex =
        command.draw.draw.indexed
            ? 0u
            : uint64_t(command.draw.draw.start);
    const uint64_t required_size =
        uint64_t(stream.offset) +
        (first_vertex + uint64_t(vertex_count)) *
            uint64_t(inline_stride_or_format);
    if (required_size != 0 && required_size < inline_size &&
        required_size <= std::numeric_limits<uint32_t>::max()) {
      inline_size = static_cast<size_t>(required_size);
    }
  }
  const bool likely_title_billboard_vertex_slice =
      !index_buffer && matched_stream_slot == 0u &&
      command.resources.vertex_declaration ==
          kFableTitleBillboardVertexDeclaration &&
      inline_stride_or_format == 72u && command.draw.draw.indexed &&
      command.draw.draw.primitive_type == 4u &&
      command.draw.draw.primitive_count == 6u;
  if (likely_title_billboard_vertex_slice &&
      !ScratchUploadSnapshotValid(bytes + upload_offset, inline_size)) {
    return false;
  }
  inline_buffer->address = inline_address;
  inline_buffer->pinned_per_draw =
      inline_buffer->pinned_per_draw || pinned_per_draw;
  inline_buffer->byte_size = static_cast<uint32_t>(inline_size);
  inline_buffer->stride_or_format = inline_stride_or_format;
  inline_buffer->data.resize(inline_size);
  std::memcpy(inline_buffer->data.data(), bytes + upload_offset, inline_size);
  // Stamp the content hash ONCE at the payload's write site. The pin guard
  // above freezes any nonzero payload, so this hash stays valid for the
  // buffer's lifetime and travels with every copy (command re-records, the
  // packet round-trip) — bind-time upload-cache keying reuses it instead of
  // re-hashing the full payload per bind.
  inline_buffer->content_hash =
      DirectHashBytes(inline_buffer->data.data(), inline_size);
  return true;
}

bool CommandHasReadyInlineScratchBufferForUpload(
    const FableDirectRenderCommand& command, bool index_buffer,
    uint32_t address, uint32_t stride_or_format, size_t size) {
  if (address == 0 || size == 0) {
    return false;
  }

  if (index_buffer) {
    const uint32_t command_address = command.resources.index_buffer;
    if (command_address == 0) {
      return false;
    }
    const bool upload_covers_command =
        command_address == address ||
        (command_address > address &&
         uint64_t(command_address - address) < uint64_t(size));
    if (!upload_covers_command || !command.inline_index_buffer.ready() ||
        command.inline_index_buffer.address != command_address) {
      return false;
    }
    const uint64_t upload_offset =
        command_address >= address ? uint64_t(command_address - address) : 0u;
    const uint64_t required_size =
        uint64_t(size) > upload_offset ? uint64_t(size) - upload_offset : 0u;
    return required_size != 0 &&
           uint64_t(command.inline_index_buffer.byte_size) >= required_size;
  }

  for (uint32_t slot = 0; slot < command.resources.streams.size(); ++slot) {
    const RendererStreamBindingSnapshot& stream = command.resources.streams[slot];
    const uint32_t effective_stride =
        stride_or_format != 0 ? stride_or_format : stream.stride;
    if (effective_stride == 0 ||
        (stream.stride != 0 && stream.stride != effective_stride) ||
        stream.buffer == 0) {
      continue;
    }
    const bool upload_covers_stream =
        stream.buffer == address ||
        (stream.buffer > address &&
         uint64_t(stream.buffer - address) < uint64_t(size));
    if (!upload_covers_stream) {
      continue;
    }
    const FableDirectInlineBuffer& inline_buffer =
        command.inline_vertex_buffers[slot];
    if (!inline_buffer.ready() || inline_buffer.address != stream.buffer ||
        inline_buffer.stride_or_format != effective_stride) {
      return false;
    }
    const uint64_t upload_offset =
        stream.buffer >= address ? uint64_t(stream.buffer - address) : 0u;
    const uint64_t required_size =
        uint64_t(size) > upload_offset ? uint64_t(size) - upload_offset : 0u;
    return required_size != 0 &&
           uint64_t(inline_buffer.byte_size) >= required_size;
  }

  return false;
}

uint32_t FableDirectDrawScratchCacheEntryCount(uint64_t draw_serial,
                                               bool index_buffer) {
  if (draw_serial == 0) {
    return 0;
  }
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const auto& cache = index_buffer ? g_fable_direct.draw_index_scratch_cache
                                   : g_fable_direct.draw_vertex_scratch_cache;
  const auto it = cache.find(draw_serial);
  return it == cache.end() ? 0u
                           : static_cast<uint32_t>(it->second.size());
}

bool FableDirectAttachDrawScratchBuffer(uint64_t draw_serial,
                                        bool index_buffer, uint32_t address,
                                        uint32_t stride_or_format,
                                        const void* data, size_t size) {
  if (draw_serial == 0 || address == 0 || !data || size == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  bool attached = false;
  const auto attach_to_commands = [&](std::vector<FableDirectRenderCommand>&
                                          commands) {
    for (FableDirectRenderCommand& command : commands) {
      if (!RenderCommandCarriesDrawEvent(command) ||
          command.draw_serial != draw_serial) {
        continue;
      }
      const bool stored = StoreInlineScratchBuffer(
          command, index_buffer, address, stride_or_format, data, size,
          /*pinned_per_draw=*/true);
      if (stored && !index_buffer) {
        StoreCommandVertexScratchCachesByInferredStrideLocked(command);
      }
      attached = stored || attached;
    }
  };
  if (g_fable_direct.render_command_draw_event_count != 0) {
    attach_to_commands(g_fable_direct.render_commands);
  }
  attach_to_commands(g_fable_direct.staged_submitted_render_commands);
  attach_to_commands(g_fable_direct.submitted_render_commands);
  if (attached) {
    TouchStateLocked();
    TryCaptureExactNonVideoRenderCommandsLocked(draw_serial, false, false);
  } else {
    StoreScratchCacheLocked(index_buffer, address, stride_or_format, data, size);
    StoreDrawScratchCacheLocked(draw_serial, index_buffer, address,
                                stride_or_format, data, size);
  }
  return attached;
}

void FableDirectAttachScratchBufferByAddressLocked(bool index_buffer,
                                                   uint32_t address,
                                                   uint32_t stride_or_format,
                                                   const void* data,
                                                   size_t size) {
  StoreScratchCacheLocked(index_buffer, address, stride_or_format, data, size);
  if (!IsCacheableScratchUploadAddress(address, size)) {
    return;
  }
  bool attached = false;
  const auto attach_to_commands =
      [&](std::vector<FableDirectRenderCommand>& commands) {
    for (FableDirectRenderCommand& command : commands) {
      if (!RenderCommandCarriesDrawEvent(command)) {
        continue;
      }
      if (CommandHasReadyInlineScratchBufferForUpload(
              command, index_buffer, address, stride_or_format, size)) {
        continue;
      }
      const bool stored = StoreInlineScratchBuffer(
          command, index_buffer, address, stride_or_format, data, size);
      if (stored && !index_buffer) {
        StoreCommandVertexScratchCachesByInferredStrideLocked(command);
      }
      attached = stored || attached;
    }
  };
  if (g_fable_direct.render_command_draw_event_count != 0) {
    attach_to_commands(g_fable_direct.render_commands);
  }
  attach_to_commands(g_fable_direct.staged_submitted_render_commands);
  attach_to_commands(g_fable_direct.submitted_render_commands);
  if (attached) {
    TouchStateLocked();
    TryCaptureExactNonVideoRenderCommandsLocked(0, false, false);
  }
}

void FableDirectAttachScratchBufferByAddress(bool index_buffer,
                                             uint32_t address,
                                             uint32_t stride_or_format,
                                             const void* data, size_t size) {
  if (address == 0 || !data || size == 0) {
    return;
  }

  if (g_fable_direct_patch_depth != 0) {
    FableDirectAttachScratchBufferByAddressLocked(
        index_buffer, address, stride_or_format, data, size);
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  FableDirectAttachScratchBufferByAddressLocked(
      index_buffer, address, stride_or_format, data, size);
}


size_t FableDirectStagedDrawEventCount() {
  return g_fable_direct.render_command_draw_event_count.load();
}

void FableDirectCaptureSubmittedRenderCommands(uint64_t submit, bool publish) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  ++g_fable_direct.command_boundary_capture_count;
  g_fable_direct.last_command_boundary_submit = submit;
  g_fable_direct.last_command_boundary_staged_commands =
      g_fable_direct.render_commands.size();
  g_fable_direct.last_command_boundary_staged_draws =
      g_fable_direct.render_command_direct_event_count;
  if (g_fable_direct.render_commands.empty()) {
    ++g_fable_direct.empty_command_boundary_capture_count;
    g_fable_direct.last_submitted_draw_event_count =
        StagedAndSubmittedDirectEventCountLocked();
    return;
  }

  std::vector<FableDirectRenderCommand> staged_commands =
      std::move(g_fable_direct.render_commands);
  const size_t staged_direct_event_count =
      g_fable_direct.render_command_direct_event_count;
  g_fable_direct.render_commands.clear();
  g_fable_direct.render_command_draw_event_count = 0;
  g_fable_direct.render_command_direct_event_count = 0;
  if (staged_direct_event_count == 0) {
    g_fable_direct.last_submitted_draw_event_count =
        StagedAndSubmittedDirectEventCountLocked();
    return;
  }

  if (publish) {
    // Immediate producers (PIX/Bink and explicit synthetic recovery) publish
    // only the action they just captured. Flushing the shared staged queue
    // here would expose the non-video guest frame still under construction.
    // Route-based generation 0 (run 475): immediate segments are never
    // dropped by backlog supersession and never claim newest — zero the
    // production stamp on this route only.
    for (FableDirectRenderCommand& command : staged_commands) {
      command.publish_generation = 0;
    }
    AppendSubmittedRenderCommandsLocked(std::move(staged_commands));
  } else {
    StageSubmittedRenderCommandsLocked(std::move(staged_commands));
  }
}

bool DirectCommandIsExactNonVideoWrapperDraw(
    const FableDirectRenderCommand& command) {
  if (!DirectCommandIsDrawActionType(command) ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }

  if (command.draw.source == RendererDrawEventSource::kSceneObject &&
      command.draw.draw.indexed &&
      command.inline_vertex_buffers[0].ready() &&
      command.inline_index_buffer.ready()) {
    return true;
  }

  if (command.draw.source == RendererDrawEventSource::kSceneObject &&
      !command.draw.draw.indexed && command.inline_vertex_buffers[0].ready() &&
      command.resources.render_targets[0] != 0 &&
      command.resources.vertex_declaration != 0 &&
      command.resources.vertex_shader != 0 &&
      command.resources.pixel_shader != 0 &&
      command.draw.draw.primitive_count != 0) {
    return true;
  }

  const bool has_wrapper_identity =
      command.draw.private_texture_wrapper_call_serial != 0 &&
      command.draw.private_texture_wrapper_context_key != 0 &&
      command.draw.private_texture_wrapper_symbol_address != 0;
  if (!has_wrapper_identity) {
    return false;
  }

  if (command.draw.source == RendererDrawEventSource::kPrivateTextureRectangle &&
      command.draw.private_texture_rectangle) {
    return true;
  }

  constexpr std::array<uint32_t, 5> kBeginIndexedScratchWrapperSymbols = {
      0x82217DB8u, 0x821D7550u, 0x8220A3F0u, 0x8222E120u,
      0x82AA59B8u};
  const bool indexed_scratch_wrapper_symbol =
      std::find(kBeginIndexedScratchWrapperSymbols.begin(),
                kBeginIndexedScratchWrapperSymbols.end(),
                command.draw.private_texture_wrapper_symbol_address) !=
      kBeginIndexedScratchWrapperSymbols.end();
  if (command.draw.source == RendererDrawEventSource::kScratchWrapper &&
      indexed_scratch_wrapper_symbol &&
      command.draw.draw.indexed && command.inline_vertex_buffers[0].ready() &&
      command.inline_index_buffer.ready() &&
      command.resources.render_targets[0] != 0 &&
      command.resources.vertex_declaration != 0) {
    return true;
  }

  constexpr uint32_t kD3DDeviceDrawPrimitive = 0x8221C3E8u;
  if (command.draw.source == RendererDrawEventSource::kD3DWrapper &&
      command.draw.private_texture_wrapper_symbol_address ==
          kD3DDeviceDrawPrimitive &&
      !command.draw.draw.indexed && command.inline_vertex_buffers[0].ready() &&
      command.resources.render_targets[0] != 0 &&
      command.resources.vertex_declaration != 0 &&
      command.resources.vertex_shader != 0 &&
      command.resources.pixel_shader != 0 &&
      std::any_of(command.resources.textures.begin(),
                  command.resources.textures.end(),
                  [](uint32_t texture) { return texture != 0; })) {
    return true;
  }

  constexpr uint32_t kBeginVerticesTitleWrapper = 0x821EBF30u;
  return command.draw.source == RendererDrawEventSource::kSceneObject &&
         command.draw.private_texture_wrapper_symbol_address ==
             kBeginVerticesTitleWrapper &&
         !command.draw.draw.indexed && command.draw.draw.primitive_type == 8u &&
         command.draw.draw.primitive_count == 3u &&
         command.inline_vertex_buffers[0].ready();
}

bool DirectClearRootIsExact(uint32_t symbol_address) {
  return symbol_address == 0x821E2FE0u || symbol_address == 0x821EF830u ||
         symbol_address == 0x8227F510u || symbol_address == 0x8227F540u;
}

bool DirectCommandIsExactClearAction(
    const FableDirectRenderCommand& command) {
  if (command.type != FableDirectRenderCommandType::kClear ||
      command.clear.flags == 0 || command.clear.root_call_serial == 0 ||
      command.clear.root_context_key == 0 ||
      !DirectClearRootIsExact(command.clear.root_symbol_address)) {
    return false;
  }

  constexpr uint32_t kFableClearDepth = 0x10u;
  constexpr uint32_t kFableClearStencil = 0x20u;
  for (uint32_t slot = 0; slot < command.resources.render_targets.size();
       ++slot) {
    if ((command.clear.flags & (1u << slot)) != 0 &&
        command.resources.render_targets[slot] != 0 &&
        command.clear.target_roots.color[slot].call_serial != 0 &&
        command.clear.target_roots.color[slot].context_key != 0 &&
        command.clear.target_roots.color[slot].symbol_address == 0x822869A0u) {
      return true;
    }
  }

  return (command.clear.flags & (kFableClearDepth | kFableClearStencil)) != 0 &&
         command.resources.depth_stencil != 0 &&
         command.clear.target_roots.depth.call_serial != 0 &&
         command.clear.target_roots.depth.context_key != 0 &&
         command.clear.target_roots.depth.symbol_address == 0x82264590u;
}

bool DirectCommandIsExactConsumedTargetlessClear(
    const FableDirectRenderCommand& command) {
  if (command.type != FableDirectRenderCommandType::kClear ||
      command.clear.flags == 0 || command.clear.root_call_serial == 0 ||
      command.clear.root_context_key == 0 ||
      !DirectClearRootIsExact(command.clear.root_symbol_address)) {
    return false;
  }
  const bool clears_color =
      (command.clear.flags & 0xFu) != 0 &&
      std::all_of(command.resources.render_targets.begin(),
                  command.resources.render_targets.end(),
                  [](uint32_t target) { return target == 0; });
  const bool clears_depth_or_stencil =
      (command.clear.flags & (0x10u | 0x20u)) != 0 &&
      command.resources.depth_stencil == 0;
  if (!clears_color && !clears_depth_or_stencil) {
    return false;
  }
  const bool has_color_root = std::any_of(
      command.clear.target_roots.color.begin(),
      command.clear.target_roots.color.end(),
      [](const FableDirectStateRoot& root) {
        return root.call_serial != 0 || root.context_key != 0 ||
               root.symbol_address != 0;
      });
  const FableDirectStateRoot& depth_root = command.clear.target_roots.depth;
  return !has_color_root && depth_root.call_serial == 0 &&
         depth_root.context_key == 0 && depth_root.symbol_address == 0;
}

bool StagedCommandsAreExactNonVideoWrapperBoundaryLocked() {
  bool has_exact_direct_action = false;
  for (const FableDirectRenderCommand& command :
       g_fable_direct.render_commands) {
    if (!RenderCommandCarriesDirectEvent(command)) {
      continue;
    }
    const bool exact_action =
        DirectCommandIsDrawActionType(command)
            ? DirectCommandIsExactNonVideoWrapperDraw(command)
            : DirectCommandIsExactClearAction(command);
    if (!exact_action) {
      return false;
    }
    has_exact_direct_action = true;
  }
  return has_exact_direct_action;
}

bool DirectCommandHasPendingScratchPayload(
    const FableDirectRenderCommand& command) {
  if (!DirectCommandIsDrawActionType(command) ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }
  if (command.draw.source != RendererDrawEventSource::kSceneObject &&
      command.draw.source != RendererDrawEventSource::kScratchWrapper) {
    return false;
  }

  if (command.draw.draw.indexed && command.resources.index_buffer != 0 &&
      FableDirectIsScratchByteBufferRange(command.resources.index_buffer, 1u) &&
      !command.inline_index_buffer.ready()) {
    return true;
  }

  for (uint32_t slot = 0; slot < command.resources.streams.size(); ++slot) {
    const RendererStreamBindingSnapshot& stream =
        command.resources.streams[slot];
    if (stream.buffer == 0 ||
        !FableDirectIsScratchByteBufferRange(stream.buffer, 1u) ||
        command.inline_vertex_buffers[slot].ready()) {
      continue;
    }
    return true;
  }

  return false;
}

bool NormalizeExactQuadScratchIndexBufferLocked(
    FableDirectRenderCommand& command) {
  constexpr uint32_t kBeginIndexedVerticesWrapper = 0x82217DB8u;
  if (!DirectCommandIsDrawActionType(command) ||
      (command.draw.source != RendererDrawEventSource::kScratchWrapper &&
       command.draw.source != RendererDrawEventSource::kSceneObject) ||
      command.draw.private_texture_wrapper_symbol_address !=
          kBeginIndexedVerticesWrapper ||
      !command.draw.draw.indexed || command.draw.draw.primitive_type != 4u ||
      command.draw.draw.base_vertex != 0u || command.draw.draw.start != 0u ||
      command.draw.draw.primitive_count != 6u ||
      command.resources.render_targets[0] == 0 ||
      command.resources.vertex_declaration == 0 ||
      command.resources.vertex_shader == 0 ||
      command.resources.pixel_shader == 0 ||
      command.resources.index_buffer == 0 ||
      !FableDirectIsScratchByteBufferRange(command.resources.index_buffer, 1u) ||
      !command.inline_vertex_buffers[0].ready()) {
    return false;
  }

  const RendererStreamBindingSnapshot& stream0 = command.resources.streams[0];
  const FableDirectInlineBuffer& vb0 = command.inline_vertex_buffers[0];
  const uint32_t stride =
      stream0.stride != 0 ? stream0.stride : vb0.stride_or_format;
  if (stream0.buffer == 0 || stride == 0 ||
      !FableDirectIsScratchByteBufferRange(stream0.buffer, 1u)) {
    return false;
  }
  // BeginIndexedVertices places the quad IB right after the 4-vertex VB
  // allocation (16-byte aligned). The stream binding can be patched from
  // device scratch state one allocation late, so the captured inline VB
  // window is the authoritative arena base; accept either anchor.
  const auto expected_index_for_base = [&](uint32_t base) -> uint64_t {
    const uint64_t vertex_bytes = (uint64_t(stride) * 4u + 3u) & ~3ull;
    return (uint64_t(base) + vertex_bytes + 15u) & ~15ull;
  };
  const uint64_t stream_expected_index_address =
      uint64_t(stream0.buffer) + uint64_t(stride) * 4u;
  const uint64_t inline_expected_index_address =
      FableDirectIsScratchByteBufferRange(vb0.address, 1u)
          ? expected_index_for_base(vb0.address)
          : 0u;
  const auto matches_index_address = [&](uint64_t expected) {
    return expected != 0 &&
           expected <= std::numeric_limits<uint32_t>::max() &&
           command.resources.index_buffer == static_cast<uint32_t>(expected);
  };
  if (!matches_index_address(stream_expected_index_address) &&
      !matches_index_address(inline_expected_index_address)) {
    return false;
  }

  const uint32_t vertex_capacity = PrimaryInlineVertexCapacityForCommand(command);
  const bool had_index_payload = command.inline_index_buffer.ready();
  if (had_index_payload &&
      InlineIndexBufferLooksValidForCommand(
          command, command.inline_index_buffer.data.data(),
          command.inline_index_buffer.data.size(),
          command.inline_index_buffer.stride_or_format, vertex_capacity)) {
    return false;
  }

  static constexpr std::array<uint16_t, 6> kQuadIndices = {
      0u, 1u, 2u, 0u, 2u, 3u};
  if (!StoreInlineScratchBuffer(command, true, command.resources.index_buffer,
                                2u, kQuadIndices.data(),
                                kQuadIndices.size() * sizeof(uint16_t))) {
    return false;
  }

  static uint32_t logged_synthesized_quad_indices = 0;
  if (logged_synthesized_quad_indices < 32u) {
    REXLOG_INFO(
        "Refii direct normalized exact quad scratch IB #{} "
        "draw_serial={} wrapper=0x{:08X} stream0=0x{:08X}/{} "
        "ib=0x{:08X}/2 bytes={} had_payload={} vertex_capacity={} "
        "rt=0x{:08X} ds=0x{:08X} decl=0x{:08X} vs=0x{:08X} "
        "ps=0x{:08X} tex0=0x{:08X} tex1=0x{:08X} tex2=0x{:08X} "
        "tex13=0x{:08X}",
        ++logged_synthesized_quad_indices,
        static_cast<unsigned long long>(command.draw_serial),
        command.draw.private_texture_wrapper_symbol_address, stream0.buffer,
        stride, command.resources.index_buffer,
        static_cast<uint32_t>(kQuadIndices.size() * sizeof(uint16_t)),
        had_index_payload ? 1 : 0, vertex_capacity,
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_declaration, command.resources.vertex_shader,
        command.resources.pixel_shader, command.resources.textures[0],
        command.resources.textures[1], command.resources.textures[2],
        command.resources.textures[13]);
  }
  return true;
}

bool DirectCommandIsNonVideoSceneDraw(const FableDirectRenderCommand& command) {
  if (!DirectCommandIsDrawActionType(command) ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }
  return command.draw.source == RendererDrawEventSource::kSceneObject ||
         command.draw.source == RendererDrawEventSource::kScratchWrapper;
}

bool StagedCommandsAreNonVideoSceneBoundaryLocked() {
  bool has_scene_work = false;
  for (const FableDirectRenderCommand& command :
       g_fable_direct.render_commands) {
    if (!RenderCommandCarriesDirectEvent(command)) {
      continue;
    }
    if (DirectCommandIsDrawActionType(command)) {
      if (!DirectCommandIsNonVideoSceneDraw(command) ||
          DirectCommandHasPendingScratchPayload(command)) {
        return false;
      }
      has_scene_work = true;
      continue;
    }
    if (command.type == FableDirectRenderCommandType::kClear) {
      continue;
    }
    if (command.type == FableDirectRenderCommandType::kResolveCopy) {
      has_scene_work = true;
      continue;
    }
    return false;
  }
  return has_scene_work;
}

bool HydratePendingScratchPayloadsFromCacheLocked() {
  bool hydrated = false;
  for (FableDirectRenderCommand& command : g_fable_direct.render_commands) {
    if (!RenderCommandCarriesDrawEvent(command)) {
      continue;
    }
    const bool pending_scratch_payload =
        DirectCommandHasPendingScratchPayload(command);
    const bool normalized_existing_index_payload =
        NormalizeExactQuadScratchIndexBufferLocked(command);
    if (!pending_scratch_payload && !normalized_existing_index_payload) {
      continue;
    }
    const bool had_vertex_payload =
        command.inline_vertex_buffers[0].ready();
    const bool had_index_payload = command.inline_index_buffer.ready();
    AttachCachedScratchBuffersToCommandLocked(command);
    const bool normalized_index_payload =
        NormalizeExactQuadScratchIndexBufferLocked(command);
    const bool has_vertex_payload =
        command.inline_vertex_buffers[0].ready();
    const bool has_index_payload = command.inline_index_buffer.ready();
    hydrated = hydrated ||
               (!had_vertex_payload && has_vertex_payload) ||
               (!had_index_payload && has_index_payload) ||
               normalized_existing_index_payload ||
               normalized_index_payload ||
               !DirectCommandHasPendingScratchPayload(command);
  }
  if (hydrated) {
    TouchStateLocked();
  }
  return hydrated;
}

void RetireStaleNonExactStagedDrawsLocked() {
  // Draws wait in staging for asynchronous inline-payload/identity patches
  // before the exact-identity boundary accepts the batch. A draw that never
  // becomes exact used to block every exact sibling draw behind it until the
  // ring cap silently trimmed them all (the title GUI pyramid starved this
  // way: one unmatched draw per batch held back the ~10 exact GUI draws each
  // frame). Give each non-exact direct action a bounded number of boundary
  // attempts to become exact, then retire it with a diagnostic so the exact
  // remainder of the batch can flow.
  constexpr uint32_t kMaxExactCaptureAttempts = 12;
  size_t retired = 0;
  size_t consumed_targetless_clears = 0;
  static uint32_t logged_retired_nonexact_draws = 0;
  static uint32_t logged_exact_consumed_targetless_clears = 0;
  auto& commands = g_fable_direct.render_commands;
  for (auto it = commands.begin(); it != commands.end();) {
    FableDirectRenderCommand& command = *it;
    if (!RenderCommandCarriesDirectEvent(command)) {
      ++it;
      continue;
    }
    if (DirectCommandIsExactConsumedTargetlessClear(command)) {
      if (logged_exact_consumed_targetless_clears < 32u) {
        REXLOG_INFO(
            "Refii direct exact consumed targetless clear #{} "
            "draw_serial={} flags=0x{:08X} clear_root=0x{:08X}/{} "
            "clear_context=0x{:016X} lr=0x{:08X} rt0=0x{:08X} "
            "rt1=0x{:08X} rt2=0x{:08X} rt3=0x{:08X} ds=0x{:08X} "
            "target_root=0x{:08X}/{} depth_root=0x{:08X}/{}",
            ++logged_exact_consumed_targetless_clears,
            static_cast<unsigned long long>(command.draw_serial),
            command.clear.flags, command.clear.root_symbol_address,
            static_cast<unsigned long long>(command.clear.root_call_serial),
            static_cast<unsigned long long>(command.clear.root_context_key),
            command.clear.root_lr, command.resources.render_targets[0],
            command.resources.render_targets[1],
            command.resources.render_targets[2],
            command.resources.render_targets[3],
            command.resources.depth_stencil,
            command.clear.target_roots.color[0].symbol_address,
            static_cast<unsigned long long>(
                command.clear.target_roots.color[0].call_serial),
            command.clear.target_roots.depth.symbol_address,
            static_cast<unsigned long long>(
                command.clear.target_roots.depth.call_serial));
      }
      it = commands.erase(it);
      ++consumed_targetless_clears;
      continue;
    }
    const bool exact_action =
        DirectCommandIsDrawActionType(command)
            ? DirectCommandIsExactNonVideoWrapperDraw(command)
            : (command.type == FableDirectRenderCommandType::kClear
                   ? DirectCommandIsExactClearAction(command)
                   : true);
    if (exact_action) {
      command.exact_capture_attempts = 0;
      ++it;
      continue;
    }
    if (++command.exact_capture_attempts <= kMaxExactCaptureAttempts) {
      ++it;
      continue;
    }
    const bool pending_scratch_payload =
        DirectCommandHasPendingScratchPayload(command);
    if (RendererDeepObserverHooksEnabled() &&
        logged_retired_nonexact_draws < 64u) {
      REXLOG_INFO(
          "Refii direct consumed expired staged action #{} "
          "reason={} type={} source={} draw_serial={} wrapper=0x{:08X} "
          "indexed={} prim={}/{} rt=0x{:08X} ds=0x{:08X} "
          "decl=0x{:08X} vs=0x{:08X} ps=0x{:08X} tex13=0x{:08X} "
          "stream0=0x{:08X}/{} ib=0x{:08X}/{} inline_vb={} "
          "inline_ib={} pending_scratch={} clear_root=0x{:08X}",
          ++logged_retired_nonexact_draws,
          pending_scratch_payload ? "pending-scratch-payload-expired"
                                  : "not-exact-after-boundary-attempts",
          static_cast<uint32_t>(command.type),
          static_cast<uint32_t>(command.draw.source),
          static_cast<unsigned long long>(command.draw_serial),
          command.draw.private_texture_wrapper_symbol_address,
          command.draw.draw.indexed ? 1 : 0, command.draw.draw.primitive_type,
          command.draw.draw.primitive_count,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_declaration,
          command.resources.vertex_shader, command.resources.pixel_shader,
          command.resources.textures[13],
          command.resources.streams[0].buffer,
          command.resources.streams[0].stride,
          command.resources.index_buffer, command.draw.index_format,
          command.inline_vertex_buffers[0].ready() ? 1 : 0,
          command.inline_index_buffer.ready() ? 1 : 0,
          pending_scratch_payload ? 1 : 0,
          command.clear.root_symbol_address);
    }
    it = commands.erase(it);
    ++retired;
  }
  if (retired != 0 || consumed_targetless_clears != 0) {
    g_fable_direct.dropped_render_commands += retired;
    g_fable_direct.render_command_draw_event_count =
        CountCommandDrawEvents(commands);
    g_fable_direct.render_command_direct_event_count =
        CountCommandDirectEvents(commands);
  }
}

bool StageCurrentRenderCommandsAtBoundaryLocked(uint64_t submit,
                                                bool publish) {
  ++g_fable_direct.command_boundary_capture_count;
  g_fable_direct.last_command_boundary_submit = submit;
  g_fable_direct.last_command_boundary_staged_commands =
      g_fable_direct.render_commands.size();
  g_fable_direct.last_command_boundary_staged_draws =
      g_fable_direct.render_command_direct_event_count;

  std::vector<FableDirectRenderCommand> staged_commands =
      std::move(g_fable_direct.render_commands);
  const size_t staged_direct_event_count =
      g_fable_direct.render_command_direct_event_count;
  g_fable_direct.render_commands.clear();
  g_fable_direct.render_command_draw_event_count = 0;
  g_fable_direct.render_command_direct_event_count = 0;
  if (staged_direct_event_count == 0) {
    g_fable_direct.last_submitted_draw_event_count =
        StagedAndSubmittedDirectEventCountLocked();
    return false;
  }

  StageSubmittedRenderCommandsLocked(std::move(staged_commands));
  if (publish) {
    PublishStagedSubmittedRenderCommandsLocked();
  }
  return true;
}

bool TryCaptureExactNonVideoRenderCommandsLocked(uint64_t submit,
                                                 bool log_miss,
                                                 bool publish) {
  // Title scroll campaign census (remove when done).
  static std::atomic<uint64_t> try_capture_total_us{0};
  static std::atomic<uint32_t> try_capture_calls{0};
  struct TryCaptureCensus {
    std::chrono::steady_clock::time_point t0 =
        std::chrono::steady_clock::now();
    std::atomic<uint64_t> *total;
    std::atomic<uint32_t> *calls;
    ~TryCaptureCensus() {
      total->fetch_add(std::chrono::duration_cast<std::chrono::microseconds>(
                           std::chrono::steady_clock::now() - t0)
                           .count(),
                       std::memory_order_relaxed);
      const uint32_t n = calls->fetch_add(1, std::memory_order_relaxed) + 1;
      if ((n & 2047u) == 0) {
        REXLOG_INFO("Refii try-capture census calls={} total_us={}", n,
                    total->load(std::memory_order_relaxed));
      }
    }
  } census{std::chrono::steady_clock::now(), &try_capture_total_us,
           &try_capture_calls};
  if (!g_fable_direct.render_commands.empty() &&
      !StagedCommandsAreExactNonVideoWrapperBoundaryLocked()) {
    HydratePendingScratchPayloadsFromCacheLocked();
    if (!StagedCommandsAreExactNonVideoWrapperBoundaryLocked()) {
      RetireStaleNonExactStagedDrawsLocked();
    }
  }
  if (g_fable_direct.render_commands.empty() ||
      !StagedCommandsAreExactNonVideoWrapperBoundaryLocked()) {
    HydratePendingScratchPayloadsFromCacheLocked();
  }
  if (g_fable_direct.render_commands.empty() ||
      !StagedCommandsAreExactNonVideoWrapperBoundaryLocked()) {
    static uint32_t logged_exact_nonvideo_capture_misses = 0;
    if (log_miss && RendererDeepObserverHooksEnabled() &&
        !g_fable_direct.render_commands.empty() &&
        g_fable_direct.render_command_direct_event_count != 0 &&
        logged_exact_nonvideo_capture_misses < 64u) {
      const FableDirectRenderCommand* first_direct_command = nullptr;
      for (const FableDirectRenderCommand& command :
           g_fable_direct.render_commands) {
        if (RenderCommandCarriesDirectEvent(command)) {
          first_direct_command = &command;
          break;
        }
      }
      const bool first_is_draw =
          first_direct_command && RenderCommandCarriesDrawEvent(*first_direct_command);
      const bool first_is_clear =
          first_direct_command &&
          first_direct_command->type == FableDirectRenderCommandType::kClear;
      const bool first_exact_clear =
          first_is_clear && DirectCommandIsExactClearAction(*first_direct_command);
      const bool first_pending_scratch =
          first_direct_command &&
          DirectCommandHasPendingScratchPayload(*first_direct_command);
      REXLOG_INFO(
          "Refii direct exact non-video capture awaiting staged action #{} "
          "reason={} submit={} commands={} direct={} draws={} "
          "first_type={} first_source={} first_draw={} first_indexed={} "
          "first_private={} "
          "first_wrapper=0x{:08X}/{} first_context=0x{:016X} "
          "first_lr=0x{:08X} "
          "first_inline_vb={} first_inline_ib={} first_rt=0x{:08X} "
          "first_ds=0x{:08X} first_decl=0x{:08X} first_vs=0x{:08X} "
          "first_ps=0x{:08X} first_tex13=0x{:08X} "
          "first_stream0=0x{:08X}/{} first_ib=0x{:08X}/{} "
          "first_pending_scratch={} first_clear={} exact_clear={} "
          "clear_root=0x{:08X}/{} clear_context=0x{:016X} "
          "clear_flags=0x{:08X} clear_target_root=0x{:08X}/{} "
          "clear_depth_root=0x{:08X}/{}",
          ++logged_exact_nonvideo_capture_misses,
          first_pending_scratch ? "pending-scratch-payload"
                                : "non-exact-boundary",
          static_cast<unsigned long long>(submit),
          g_fable_direct.render_commands.size(),
          g_fable_direct.render_command_direct_event_count,
          g_fable_direct.render_command_draw_event_count.load(),
          first_direct_command
              ? static_cast<uint32_t>(first_direct_command->type)
              : 0u,
          first_is_draw
              ? static_cast<uint32_t>(first_direct_command->draw.source)
              : 0u,
          static_cast<unsigned long long>(
              first_direct_command ? first_direct_command->draw_serial : 0ull),
          first_is_draw && first_direct_command->draw.draw.indexed ? 1 : 0,
          first_is_draw && first_direct_command->draw.private_texture_rectangle
              ? 1
              : 0,
          first_is_draw
              ? first_direct_command->draw.private_texture_wrapper_symbol_address
              : 0u,
          static_cast<unsigned long long>(
              first_is_draw
                  ? first_direct_command->draw.private_texture_wrapper_call_serial
                  : 0ull),
          static_cast<unsigned long long>(
              first_is_draw
                  ? first_direct_command->draw.private_texture_wrapper_context_key
                  : 0ull),
          first_is_draw ? first_direct_command->draw.state_flush_lr : 0u,
          first_direct_command &&
                  first_direct_command->inline_vertex_buffers[0].ready()
              ? 1
              : 0,
          first_direct_command && first_direct_command->inline_index_buffer.ready()
              ? 1
              : 0,
          first_direct_command ? first_direct_command->resources.render_targets[0]
                               : 0u,
          first_direct_command ? first_direct_command->resources.depth_stencil
                               : 0u,
          first_direct_command
              ? first_direct_command->resources.vertex_declaration
              : 0u,
          first_direct_command ? first_direct_command->resources.vertex_shader
                               : 0u,
          first_direct_command ? first_direct_command->resources.pixel_shader
                               : 0u,
          first_direct_command ? first_direct_command->resources.textures[13]
                               : 0u,
          first_direct_command
              ? first_direct_command->resources.streams[0].buffer
              : 0u,
          first_direct_command
              ? first_direct_command->resources.streams[0].stride
              : 0u,
          first_direct_command ? first_direct_command->resources.index_buffer
                               : 0u,
          first_is_draw ? first_direct_command->draw.index_format : 0u,
          first_pending_scratch ? 1 : 0,
          first_is_clear ? 1 : 0, first_exact_clear ? 1 : 0,
          first_is_clear ? first_direct_command->clear.root_symbol_address : 0u,
          static_cast<unsigned long long>(
              first_is_clear ? first_direct_command->clear.root_call_serial
                             : 0ull),
          static_cast<unsigned long long>(
              first_is_clear ? first_direct_command->clear.root_context_key
                             : 0ull),
          first_is_clear ? first_direct_command->clear.flags : 0u,
          first_is_clear
              ? first_direct_command->clear.target_roots.color[0].symbol_address
              : 0u,
          static_cast<unsigned long long>(
              first_is_clear
                  ? first_direct_command->clear.target_roots.color[0].call_serial
                  : 0ull),
          first_is_clear
              ? first_direct_command->clear.target_roots.depth.symbol_address
              : 0u,
          static_cast<unsigned long long>(
              first_is_clear
                  ? first_direct_command->clear.target_roots.depth.call_serial
                  : 0ull));
    }
    return false;
  }

  return StageCurrentRenderCommandsAtBoundaryLocked(submit, publish);
}

bool TryCaptureNonVideoSceneRenderCommandsLocked(uint64_t submit,
                                                 bool publish) {
  if (g_fable_direct.render_commands.empty() ||
      !StagedCommandsAreNonVideoSceneBoundaryLocked()) {
    return false;
  }

  return StageCurrentRenderCommandsAtBoundaryLocked(submit, publish);
}

// WAIT-vs-WORK census for the single most expensive enter feed (2026-07-28).
// Run 398 per-symbol enter census: D3DDevice_BeginVertices 297 calls / 8,393ms
// (28.3ms per call) and D3DDevice_DrawPrimitive 59 calls / 2,295ms (38.9ms per
// call) -- 10.7s of the 15.5s enter phase, in 356 calls. Both funnel here
// (hook_observer.cpp:15243). 28-39ms per call is the shape of the guest thread
// blocking on the render thread's compose, but that is exactly the inference I
// got WRONG about g_observer.mutex (held a lot, contended 0.47%). So measure
// instead of assume: split the lock WAIT from the body.
std::atomic<uint64_t> g_exact_capture_calls{0};
std::atomic<uint64_t> g_exact_capture_wait_us{0};
std::atomic<uint64_t> g_exact_capture_body_us{0};

void GetExactCaptureStats(uint64_t& calls, uint64_t& wait_us,
                          uint64_t& body_us) {
  calls = g_exact_capture_calls.load(std::memory_order_relaxed);
  wait_us = g_exact_capture_wait_us.load(std::memory_order_relaxed);
  body_us = g_exact_capture_body_us.load(std::memory_order_relaxed);
}

bool FableDirectCaptureExactNonVideoRenderCommands(uint64_t submit) {
  const auto wait_t0 = std::chrono::steady_clock::now();
  std::unique_lock<std::mutex> lock(g_fable_direct.mutex);
  const auto body_t0 = std::chrono::steady_clock::now();
  // Wrapper completion proves an exact action, not completion of the guest
  // frame containing it. Keep the action staged until RenderFrame exits.
  const bool captured =
      TryCaptureExactNonVideoRenderCommandsLocked(submit, true, false);
  const auto body_t1 = std::chrono::steady_clock::now();
  g_exact_capture_calls.fetch_add(1, std::memory_order_relaxed);
  g_exact_capture_wait_us.fetch_add(
      static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(body_t0 -
                                                                wait_t0)
              .count()),
      std::memory_order_relaxed);
  g_exact_capture_body_us.fetch_add(
      static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(body_t1 -
                                                                body_t0)
              .count()),
      std::memory_order_relaxed);
  return captured;
}



bool FableDirectSealNonVideoGuestFrame(uint64_t boundary_serial) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);

  // Capture the final exact action (or broader proven scene action) that may
  // still be in the producer queue when RenderFrame returns. Both paths stage
  // only; publication below is the single non-video frame-ownership point.
  bool captured = TryCaptureExactNonVideoRenderCommandsLocked(
      boundary_serial, false, false);
  if (!captured) {
    captured = TryCaptureNonVideoSceneRenderCommandsLocked(boundary_serial,
                                                            false);
  }

  // The boundary happened whether or not anything publishes below: advance
  // the frame ordinal on EVERY seal call, after the tail capture above staged
  // its commands into the ENDING frame's generation. An empty frame must
  // still advance, or two visual frames would merge into one generation; a
  // video-refusal frame's staged commands keep their (correct, old) stamp
  // and publish later via the video producer's own cadence.
  ++g_fable_direct.guest_frame_seal_ordinal;

  if (g_fable_direct.staged_submitted_render_commands.empty()) {
    return captured;
  }

  // A video action owns its own cadence and is published by its producer.
  // Never merge one into a non-video guest-frame seal.
  for (const FableDirectRenderCommand& command :
       g_fable_direct.staged_submitted_render_commands) {
    if (RenderCommandCarriesDrawEvent(command) &&
        command.draw.source == RendererDrawEventSource::kPixVideo) {
      return false;
    }
  }

  const size_t sealed_commands =
      g_fable_direct.staged_submitted_render_commands.size();
  const size_t sealed_direct_events = CountCommandDirectEvents(
      g_fable_direct.staged_submitted_render_commands);
  const uint32_t reordered_inversions = OrderStagedNonVideoGuestFrameLocked();
  PublishStagedSubmittedRenderCommandsLocked();
  // Seal census. With the present-boundary seal site this fires per guest
  // frame (~18/s at the title attract, 54ms guest frames), so the per-seal
  // line is rate-limited to 1/5s after the first 8. Never silent: the
  // cumulative counters always appear in the next periodic line. Statics are
  // guarded by g_fable_direct.mutex (held for this whole function).
  static uint64_t sealed_frames_total = 0;
  static uint64_t sealed_commands_total = 0;
  static std::chrono::steady_clock::time_point last_seal_log{};
  ++sealed_frames_total;
  sealed_commands_total += sealed_commands;
  const auto seal_now = std::chrono::steady_clock::now();
  if (sealed_frames_total <= 8u ||
      seal_now - last_seal_log >= std::chrono::seconds(5)) {
    last_seal_log = seal_now;
    REXLOG_INFO(
        "Refii sealed non-video guest frame #{} boundary={} commands={} "
        "direct={} reordered_inversions={} published_serial={} "
        "total_commands={}",
        static_cast<unsigned long long>(sealed_frames_total),
        static_cast<unsigned long long>(boundary_serial), sealed_commands,
        sealed_direct_events, reordered_inversions,
        static_cast<unsigned long long>(
            g_fable_direct.submitted_render_command_serial),
        static_cast<unsigned long long>(sealed_commands_total));
  }
  return true;
}

uint32_t FableDirectConsumeExactFramebufferStateCommands() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const size_t original_size = g_fable_direct.render_commands.size();
  if (original_size == 0) {
    return 0;
  }

  g_fable_direct.render_commands.erase(
      std::remove_if(g_fable_direct.render_commands.begin(),
                     g_fable_direct.render_commands.end(),
                     [](const FableDirectRenderCommand& command) {
                       return DirectCommandIsExactFramebufferStateAction(command);
                     }),
      g_fable_direct.render_commands.end());

  const size_t removed = original_size - g_fable_direct.render_commands.size();
  if (removed == 0) {
    return 0;
  }

  g_fable_direct.dropped_render_commands += removed;
  g_fable_direct.render_command_draw_event_count =
      CountCommandDrawEvents(g_fable_direct.render_commands);
  g_fable_direct.render_command_direct_event_count =
      CountCommandDirectEvents(g_fable_direct.render_commands);
  return static_cast<uint32_t>(
      std::min<size_t>(removed, std::numeric_limits<uint32_t>::max()));
}

uint32_t FableDirectConsumeExactRootedStateCommands() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const size_t original_size = g_fable_direct.render_commands.size();
  if (original_size == 0) {
    return 0;
  }

  g_fable_direct.render_commands.erase(
      std::remove_if(g_fable_direct.render_commands.begin(),
                     g_fable_direct.render_commands.end(),
                     [](const FableDirectRenderCommand& command) {
                       return DirectCommandIsExactRootedStateAction(command);
                     }),
      g_fable_direct.render_commands.end());

  const size_t removed = original_size - g_fable_direct.render_commands.size();
  if (removed == 0) {
    return 0;
  }

  g_fable_direct.dropped_render_commands += removed;
  g_fable_direct.render_command_draw_event_count =
      CountCommandDrawEvents(g_fable_direct.render_commands);
  g_fable_direct.render_command_direct_event_count =
      CountCommandDirectEvents(g_fable_direct.render_commands);
  return static_cast<uint32_t>(
      std::min<size_t>(removed, std::numeric_limits<uint32_t>::max()));
}

uint32_t FableDirectConsumeStateFlushCommands() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const size_t original_size = g_fable_direct.render_commands.size();
  if (original_size == 0) {
    return 0;
  }

  g_fable_direct.render_commands.erase(
      std::remove_if(g_fable_direct.render_commands.begin(),
                     g_fable_direct.render_commands.end(),
                     [](const FableDirectRenderCommand& command) {
                       return command.type ==
                              FableDirectRenderCommandType::kStateFlush;
                     }),
      g_fable_direct.render_commands.end());

  const size_t removed = original_size - g_fable_direct.render_commands.size();
  if (removed == 0) {
    return 0;
  }

  g_fable_direct.dropped_render_commands += removed;
  g_fable_direct.render_command_draw_event_count =
      CountCommandDrawEvents(g_fable_direct.render_commands);
  g_fable_direct.render_command_direct_event_count =
      CountCommandDirectEvents(g_fable_direct.render_commands);
  return static_cast<uint32_t>(
      std::min<size_t>(removed, std::numeric_limits<uint32_t>::max()));
}

uint32_t FableDirectConsumeStateOnlyCommands() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const size_t original_size = g_fable_direct.render_commands.size();
  if (original_size == 0) {
    return 0;
  }

  const size_t direct_event_count =
      CountCommandDirectEvents(g_fable_direct.render_commands);
  if (direct_event_count != 0) {
    g_fable_direct.render_command_draw_event_count =
        CountCommandDrawEvents(g_fable_direct.render_commands);
    g_fable_direct.render_command_direct_event_count = direct_event_count;
    return 0;
  }

  g_fable_direct.render_commands.clear();
  g_fable_direct.dropped_render_commands += original_size;
  g_fable_direct.render_command_draw_event_count = 0;
  g_fable_direct.render_command_direct_event_count = 0;
  return static_cast<uint32_t>(
      std::min<size_t>(original_size, std::numeric_limits<uint32_t>::max()));
}

bool FableDirectDrawEventsReferenceResource(
    const FableDirectResourceReference& resource) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  return (g_fable_direct.render_command_draw_event_count != 0 &&
          CommandDrawEventsReferenceResource(g_fable_direct.render_commands,
                                             resource)) ||
         CommandDrawEventsReferenceResource(
             g_fable_direct.staged_submitted_render_commands, resource) ||
         CommandDrawEventsReferenceResource(
             g_fable_direct.submitted_render_commands, resource);
}

FableDirectStateSnapshot BuildFableDirectStateSnapshotLocked() {
  FableDirectStateSnapshot snapshot;
  snapshot.serial = g_fable_direct.serial;
  snapshot.draw_event_count = g_fable_direct.render_command_draw_event_count;
  snapshot.direct_event_count = g_fable_direct.render_command_direct_event_count;
  snapshot.render_command_count = g_fable_direct.render_commands.size();
  FillStagedCommandSummaryLocked(snapshot);
  snapshot.submitted_render_command_count =
      g_fable_direct.submitted_render_commands.size();
  snapshot.submitted_render_command_serial =
      g_fable_direct.submitted_render_command_serial;
  snapshot.command_boundary_capture_count =
      g_fable_direct.command_boundary_capture_count;
  snapshot.empty_command_boundary_capture_count =
      g_fable_direct.empty_command_boundary_capture_count;
  snapshot.last_command_boundary_submit =
      g_fable_direct.last_command_boundary_submit;
  snapshot.last_command_boundary_staged_commands =
      g_fable_direct.last_command_boundary_staged_commands;
  snapshot.last_command_boundary_staged_draws =
      g_fable_direct.last_command_boundary_staged_draws;
  snapshot.last_submitted_draw_event_count =
      g_fable_direct.last_submitted_draw_event_count;
  snapshot.dropped_render_commands = g_fable_direct.dropped_render_commands;
  snapshot.funnel_draw_commands_pushed =
      g_fable_direct.funnel_draw_commands_pushed;
  snapshot.funnel_draw_commands_published =
      g_fable_direct.funnel_draw_commands_published;
  snapshot.consumed_exact_state_commands =
      g_fable_direct.consumed_exact_state_commands;
  snapshot.render_target_switch_count =
      g_fable_direct.render_target_switch_count;
  snapshot.depth_target_switch_count = g_fable_direct.depth_target_switch_count;
  snapshot.draw_count = g_fable_direct.draw_count;
  snapshot.indexed_draw_count = g_fable_direct.indexed_draw_count;
  snapshot.state_flush_count = g_fable_direct.state_flush_count;
  snapshot.last_device = g_fable_direct.last_device;
  snapshot.targets = g_fable_direct.targets;
  snapshot.target_roots = g_fable_direct.target_roots;
  snapshot.shaders = g_fable_direct.shaders;
  snapshot.shader_roots = g_fable_direct.shader_roots;
  snapshot.input = g_fable_direct.input;
  snapshot.render_states = g_fable_direct.render_states;
  snapshot.textures = g_fable_direct.textures;
  snapshot.constants = g_fable_direct.constants;
  snapshot.pm4 = g_fable_direct.pm4;
  snapshot.viewport = g_fable_direct.viewport;
  snapshot.scissor = g_fable_direct.scissor;
  snapshot.last_draw = g_fable_direct.last_draw;
  snapshot.scaler = g_fable_direct.scaler;
  snapshot.clear_flags = g_fable_direct.clear_flags;
  snapshot.dirty = g_fable_direct.dirty;
  return snapshot;
}

FableDirectStateSnapshot GetFableDirectStateSnapshot() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  return BuildFableDirectStateSnapshotLocked();
}

bool TryGetFableDirectStateSnapshot(FableDirectStateSnapshot& snapshot) {
  std::unique_lock<std::mutex> lock(g_fable_direct.mutex, std::try_to_lock);
  if (!lock.owns_lock()) {
    return false;
  }
  snapshot = BuildFableDirectStateSnapshotLocked();
  return true;
}

std::vector<FableDirectRenderCommand> FableDirectQueuedRenderCommandSnapshot(
    size_t max_commands) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  std::vector<FableDirectRenderCommand> commands;
  commands.reserve(std::min(
      max_commands, g_fable_direct.render_commands.size() +
                        g_fable_direct.staged_submitted_render_commands.size() +
                        g_fable_direct.submitted_render_commands.size()));

  auto append_commands =
      [&](const std::vector<FableDirectRenderCommand>& source) {
        for (const FableDirectRenderCommand& command : source) {
          if (commands.size() >= max_commands) {
            return;
          }
          commands.push_back(command);
        }
      };

  append_commands(g_fable_direct.submitted_render_commands);
  append_commands(g_fable_direct.staged_submitted_render_commands);
  append_commands(g_fable_direct.render_commands);
  return commands;
}

uint32_t FableDirectCurrentStagingPublishGeneration() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  return g_fable_direct.guest_frame_seal_ordinal + 1u;
}



std::vector<FableDirectRenderCommand>
ConsumeFableDirectSubmittedRenderCommands() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  if (g_fable_direct.submitted_render_command_serial == 0 ||
      g_fable_direct.submitted_render_command_serial ==
          g_fable_direct.consumed_submitted_render_command_serial) {
    return {};
  }

  std::vector<FableDirectRenderCommand> commands;
  commands.swap(g_fable_direct.submitted_render_commands);
  g_fable_direct.consumed_submitted_render_command_serial =
      g_fable_direct.submitted_render_command_serial;
  return commands;
}

}  // namespace refii::renderer
