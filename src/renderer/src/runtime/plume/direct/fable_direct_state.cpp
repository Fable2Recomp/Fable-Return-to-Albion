#include "runtime/plume/direct/fable_direct_state.h"
#include "runtime/plume/draw/plume_draw_state.h"

#include <rex/logging.h>

#include <algorithm>
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
                              size_t size);

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
constexpr uint32_t kPackedSamplerStateFilterQualityShift = 24u;
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
constexpr uint32_t kFableSyntheticPrivateRectVertexShader = 0x0E000152u;
constexpr uint32_t kFableSyntheticPrivateRectCopyPixelShader = 0x0E010137u;
constexpr size_t kMaxBufferedFableDirectStateOnlyCommands = 512;
constexpr size_t kKeepBufferedFableDirectStateOnlyCommands = 128;
constexpr uint32_t kFableDirectGpuScratchMin = 0x1000u;
constexpr uint32_t kFableDirectObjectAddressMin = 0x40000000u;
constexpr size_t kMaxBufferedFableDirectDrawScratchCaches = 2048;

struct FableDirectState {
  std::mutex mutex;
  uint64_t serial = 0;
  uint64_t dropped_render_commands = 0;
  uint64_t draw_count = 0;
  uint64_t indexed_draw_count = 0;
  uint64_t state_flush_count = 0;
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
  size_t render_command_draw_event_count = 0;
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
  uint64_t last_command_boundary_pending_commands = 0;
  uint64_t last_command_boundary_pending_draws = 0;
  uint64_t last_submitted_draw_event_count = 0;
  uint64_t consumed_exact_state_commands = 0;
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

const FableDirectInlineBuffer* FindNearestPriorScratchCacheForAddressLocked(
    const std::unordered_map<uint32_t, FableDirectInlineBuffer>& cache,
    uint32_t address, uint32_t max_distance) {
  const FableDirectInlineBuffer* best = nullptr;
  uint32_t best_distance = std::numeric_limits<uint32_t>::max();
  for (const auto& [cached_address, cached] : cache) {
    if (!cached.ready() || cached_address >= address) {
      continue;
    }
    const uint32_t distance = address - cached_address;
    if (distance > max_distance || distance >= best_distance) {
      continue;
    }
    best = &cached;
    best_distance = distance;
  }
  return best;
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
        return stride_matches &&
               (address == inline_buffer.address ||
                (address > inline_buffer.address &&
                 uint64_t(address - inline_buffer.address) <
                     uint64_t(inline_buffer.byte_size)));
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
                                     upload.data.size()) ||
            stored_any;
      }
      if (stored_any) {
        StoreCommandVertexScratchCachesByInferredStrideLocked(command);
      }
      g_fable_direct.draw_vertex_scratch_cache.erase(vertex_cache_it);
    }
    const auto index_cache_it =
        g_fable_direct.draw_index_scratch_cache.find(command.draw_serial);
    if (index_cache_it != g_fable_direct.draw_index_scratch_cache.end()) {
      for (const FableDirectInlineBuffer& upload : index_cache_it->second) {
        StoreInlineScratchBuffer(command, true, upload.address,
                                 upload.stride_or_format, upload.data.data(),
                                 upload.data.size());
      }
      g_fable_direct.draw_index_scratch_cache.erase(index_cache_it);
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
      packed = (packed & 0x803FFFFFu) | ((value >> 10) & 0x7FC00000u);
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
  g_fable_direct.last_command_boundary_pending_commands = 0;
  g_fable_direct.last_command_boundary_pending_draws = 0;
  g_fable_direct.last_submitted_draw_event_count = 0;
  g_fable_direct.consumed_exact_state_commands = 0;
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
      return false;
  }
  return false;
}

bool DirectCommandIsDrawActionType(const FableDirectRenderCommand& command) {
  return command.type == FableDirectRenderCommandType::kDrawPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawIndexedPrimitive ||
         command.type == FableDirectRenderCommandType::kDrawPrimitiveUP;
}

bool DirectCommandIsExactFramebufferStateAction(
    const FableDirectRenderCommand& command) {
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

  if (command.type == FableDirectRenderCommandType::kSetRenderTarget) {
    return exact_root(command.state_root, 0x822869A0u);
  }
  if (command.type == FableDirectRenderCommandType::kSetDepthStencil) {
    return exact_root(command.state_root, 0x82264590u);
  }
  if (command.type == FableDirectRenderCommandType::kSetViewport ||
      command.type == FableDirectRenderCommandType::kSetScissor ||
      command.type == FableDirectRenderCommandType::kSetPm4Field) {
    return exact_framebuffer_root(command.state_root);
  }
  return false;
}

bool DirectCommandHasExactStateRoot(const FableDirectRenderCommand& command) {
  return command.state_root.call_serial != 0 &&
         command.state_root.context_key != 0 &&
         command.state_root.symbol_address != 0;
}

bool DirectCommandIsExactRootedStateAction(
    const FableDirectRenderCommand& command) {
  if (!DirectCommandHasExactStateRoot(command)) {
    return false;
  }

  switch (command.type) {
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
      return false;
  }
  return false;
}

bool DirectCommandIsExactConsumedStateAction(
    const FableDirectRenderCommand& command) {
  return DirectCommandIsExactRootedStateAction(command) ||
         DirectCommandIsExactFramebufferStateAction(command);
}

size_t CountCommandDrawEvents(
    const std::vector<FableDirectRenderCommand>& commands);
size_t CountCommandDirectEvents(
    const std::vector<FableDirectRenderCommand>& commands);

void TrimBufferedStateOnlyRenderCommandsLocked(size_t keep_state_commands) {
  std::vector<FableDirectRenderCommand>& commands =
      g_fable_direct.render_commands;
  if (commands.size() < kMaxBufferedFableDirectStateOnlyCommands) {
    return;
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

void PushRenderCommandLocked(FableDirectRenderCommand command) {
  if (DirectCommandIsExactConsumedStateAction(command)) {
    ++g_fable_direct.consumed_exact_state_commands;
    ++g_fable_direct.dropped_render_commands;
    return;
  }

  command.serial = ++g_fable_direct.next_render_command_serial;
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
    if (RenderCommandCarriesDrawEvent(g_fable_direct.render_commands.front()) &&
        g_fable_direct.render_command_draw_event_count != 0) {
      --g_fable_direct.render_command_draw_event_count;
    }
    if (RenderCommandCarriesDirectEvent(
            g_fable_direct.render_commands.front()) &&
        g_fable_direct.render_command_direct_event_count != 0) {
      --g_fable_direct.render_command_direct_event_count;
    }
    g_fable_direct.render_commands.erase(g_fable_direct.render_commands.begin());
    ++g_fable_direct.dropped_render_commands;
  }

  AttachCachedScratchBuffersToCommandLocked(command);
  g_fable_direct.render_commands.push_back(std::move(command));
  if (carries_draw_event) {
    ++g_fable_direct.render_command_draw_event_count;
  }
  if (carries_direct_event) {
    ++g_fable_direct.render_command_direct_event_count;
  }
}

bool DirectDrawEventReferencesResource(
    const RendererDrawEventSnapshot& event,
    const FableDirectResourceReference& resource);

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

RendererDrawEventSnapshot BuildDrawEventFromRenderCommand(
    const FableDirectRenderCommand& command) {
  RendererDrawEventSnapshot event;
  event.serial = command.draw_serial != 0 ? command.draw_serial : command.serial;
  event.resource_serial = command.resource_serial != 0
                              ? command.resource_serial
                              : event.serial;
  event.source = command.draw.source;
  event.clear = command.type == FableDirectRenderCommandType::kClear;
  event.clear_flags = command.clear.flags;
  event.clear_color[0] = command.clear.color[0];
  event.clear_color[1] = command.clear.color[1];
  event.clear_color[2] = command.clear.color[2];
  event.clear_color[3] = command.clear.color[3];
  event.clear_depth = command.clear.depth;
  event.clear_stencil = command.clear.stencil;
  event.clear_rect = command.clear.rect;
  event.clear_root_call_serial = command.clear.root_call_serial;
  event.clear_root_context_key = command.clear.root_context_key;
  event.clear_root_symbol_address = command.clear.root_symbol_address;
  event.clear_root_lr = command.clear.root_lr;
  for (uint32_t slot = 0; slot < command.clear.target_roots.color.size();
       ++slot) {
    event.clear_target_root_call_serial[slot] =
        command.clear.target_roots.color[slot].call_serial;
    event.clear_target_root_context_key[slot] =
        command.clear.target_roots.color[slot].context_key;
    event.clear_target_root_symbol_address[slot] =
        command.clear.target_roots.color[slot].symbol_address;
    event.clear_target_root_lr[slot] = command.clear.target_roots.color[slot].lr;
  }
  event.clear_depth_root_call_serial =
      command.clear.target_roots.depth.call_serial;
  event.clear_depth_root_context_key =
      command.clear.target_roots.depth.context_key;
  event.clear_depth_root_symbol_address =
      command.clear.target_roots.depth.symbol_address;
  event.clear_depth_root_lr = command.clear.target_roots.depth.lr;
  event.draw = command.draw.draw;
  event.draw_vertices_up = command.draw.draw_vertices_up;
  event.private_texture_rectangle = command.draw.private_texture_rectangle;
  event.private_texture_slot = command.draw.private_texture_slot;
  event.private_texture_source_x = command.draw.private_texture_source_x;
  event.private_texture_source_y = command.draw.private_texture_source_y;
  event.private_texture_corners_valid =
      command.draw.private_texture_corners_valid;
  event.private_texture_corners = command.draw.private_texture_corners;
  event.private_texture_uv_override = command.draw.private_texture_uv_override;
  event.private_texture_u0 = command.draw.private_texture_u0;
  event.private_texture_v0 = command.draw.private_texture_v0;
  event.private_texture_u1 = command.draw.private_texture_u1;
  event.private_texture_v1 = command.draw.private_texture_v1;
  event.private_texture_wrapper_call_serial =
      command.draw.private_texture_wrapper_call_serial;
  event.private_texture_wrapper_context_key =
      command.draw.private_texture_wrapper_context_key;
  event.private_texture_wrapper_symbol_address =
      command.draw.private_texture_wrapper_symbol_address;
  event.private_texture_wrapper_source_texture =
      command.draw.private_texture_wrapper_source_texture;
  event.private_texture_wrapper_source_exact =
      command.draw.private_texture_wrapper_source_exact;
  std::copy(command.resources.render_targets.begin(),
            command.resources.render_targets.end(), event.render_targets);
  event.depth_stencil = command.resources.depth_stencil;
  event.vertex_declaration = command.resources.vertex_declaration;
  event.vertex_shader = command.resources.vertex_shader;
  event.vertex_shader_root_call_serial =
      command.resources.shader_roots.vertex_shader.call_serial;
  event.vertex_shader_root_context_key =
      command.resources.shader_roots.vertex_shader.context_key;
  event.vertex_shader_root_symbol_address =
      command.resources.shader_roots.vertex_shader.symbol_address;
  event.vertex_shader_root_lr = command.resources.shader_roots.vertex_shader.lr;
  event.pixel_shader = command.resources.pixel_shader;
  event.pixel_shader_root_call_serial =
      command.resources.shader_roots.pixel_shader.call_serial;
  event.pixel_shader_root_context_key =
      command.resources.shader_roots.pixel_shader.context_key;
  event.pixel_shader_root_symbol_address =
      command.resources.shader_roots.pixel_shader.symbol_address;
  event.pixel_shader_root_lr = command.resources.shader_roots.pixel_shader.lr;
  event.index_buffer = command.resources.index_buffer;
  std::copy(command.resources.textures.begin(), command.resources.textures.end(),
            event.textures);
  event.pm4_registers_valid = command.pipeline.pm4.valid;
  event.rb_surface_info = command.pipeline.pm4.rb_surface_info;
  std::copy(command.pipeline.pm4.rb_color_info.begin(),
            command.pipeline.pm4.rb_color_info.end(), event.rb_color_info);
  event.rb_depth_info = command.pipeline.pm4.rb_depth_info;
  event.rb_copy_control = command.resolve.rb_copy_control != 0
                              ? command.resolve.rb_copy_control
                              : command.pipeline.pm4.rb_copy_control;
  event.rb_copy_dest_base = command.resolve.rb_copy_dest_base != 0
                                ? command.resolve.rb_copy_dest_base
                                : command.pipeline.pm4.rb_copy_dest_base;
  event.rb_copy_dest_pitch = command.resolve.rb_copy_dest_pitch != 0
                                 ? command.resolve.rb_copy_dest_pitch
                                 : command.pipeline.pm4.rb_copy_dest_pitch;
  event.rb_copy_dest_info = command.resolve.rb_copy_dest_info != 0
                                ? command.resolve.rb_copy_dest_info
                                : command.pipeline.pm4.rb_copy_dest_info;
  event.rb_copy_func = command.resolve.rb_copy_func != 0
                           ? command.resolve.rb_copy_func
                           : command.pipeline.pm4.rb_copy_func;
  event.rb_copy_ref = command.resolve.rb_copy_ref != 0
                          ? command.resolve.rb_copy_ref
                          : command.pipeline.pm4.rb_copy_ref;
  event.rb_copy_mask = command.resolve.rb_copy_mask != 0
                           ? command.resolve.rb_copy_mask
                           : command.pipeline.pm4.rb_copy_mask;
  event.rb_copy_surface_slice =
      command.resolve.rb_copy_surface_slice != 0
          ? command.resolve.rb_copy_surface_slice
          : command.pipeline.pm4.rb_copy_surface_slice;
  event.rb_modecontrol = command.pipeline.pm4.rb_modecontrol;
  event.rb_depthcontrol = command.pipeline.pm4.rb_depthcontrol;
  event.rb_color_mask = command.pipeline.pm4.rb_color_mask;
  std::copy(command.pipeline.pm4.rb_blendcontrol.begin(),
            command.pipeline.pm4.rb_blendcontrol.end(), event.rb_blendcontrol);
  event.rb_colorcontrol = command.pipeline.pm4.rb_colorcontrol;
  event.rb_alpha_ref = command.pipeline.pm4.rb_alpha_ref;
  event.rb_stencilrefmask = command.pipeline.pm4.rb_stencilrefmask;
  event.rb_stencilrefmask_bf = command.pipeline.pm4.rb_stencilrefmask_bf;
  event.pa_su_sc_mode_cntl = command.pipeline.pm4.pa_su_sc_mode_cntl;
  event.rb_stencil_ref =
      static_cast<uint8_t>((event.rb_stencilrefmask >> 8u) & 0xFFu);
  event.rb_stencil_read_mask =
      static_cast<uint8_t>((event.rb_stencilrefmask >> 16u) & 0xFFu);
  event.rb_stencil_write_mask =
      static_cast<uint8_t>(event.rb_stencilrefmask & 0xFFu);
  event.render_states = command.pipeline.render_states;
  event.sampler_states = command.sampler_states;
  event.fetch_constants = command.constants.fetch;
  for (uint32_t slot = 0; slot < command.resources.streams.size(); ++slot) {
    event.streams[slot] = command.resources.streams[slot];
  }
  event.vertex_shader_constants = command.constants.vertex_float;
  event.pixel_shader_constants = command.constants.pixel_float;
  event.vertex_shader_int_constants = command.constants.vertex_int;
  event.pixel_shader_int_constants = command.constants.pixel_int;
  event.vertex_shader_int_constant_valid_mask =
      command.constants.vertex_int_valid_mask;
  event.pixel_shader_int_constant_valid_mask =
      command.constants.pixel_int_valid_mask;
  event.vertex_shader_bool_constants = command.constants.vertex_bool;
  event.pixel_shader_bool_constants = command.constants.pixel_bool;
  event.viewport = command.viewport;
  event.scissor = command.scissor;
  event.scaler = command.scaler;
  event.pix_video_planes = command.pix_video_planes;
  event.state_flush_lr = command.draw.state_flush_lr;
  NormalizePrivateTextureRectangleDrawEvent(event);
  return event;
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

void RefreshRenderCommandPayloadFromEvent(
    FableDirectRenderCommand& command,
    const RendererDrawEventSnapshot& event) {
  RendererDrawEventSnapshot normalized_event = event;
  NormalizePrivateTextureRectangleDrawEvent(normalized_event);

  command.draw_serial = event.serial;
  command.resource_serial =
      event.resource_serial != 0 ? event.resource_serial : event.serial;
  command.type = RenderCommandTypeForEvent(normalized_event);
  command.resources.render_targets = {
      normalized_event.render_targets[0], normalized_event.render_targets[1],
      normalized_event.render_targets[2], normalized_event.render_targets[3]};
  command.resources.depth_stencil = normalized_event.depth_stencil;
  command.resources.vertex_declaration = normalized_event.vertex_declaration;
  command.resources.vertex_shader = normalized_event.vertex_shader;
  command.resources.shader_roots.vertex_shader.call_serial =
      normalized_event.vertex_shader_root_call_serial;
  command.resources.shader_roots.vertex_shader.context_key =
      normalized_event.vertex_shader_root_context_key;
  command.resources.shader_roots.vertex_shader.symbol_address =
      normalized_event.vertex_shader_root_symbol_address;
  command.resources.shader_roots.vertex_shader.lr =
      normalized_event.vertex_shader_root_lr;
  command.resources.pixel_shader = normalized_event.pixel_shader;
  command.resources.shader_roots.pixel_shader.call_serial =
      normalized_event.pixel_shader_root_call_serial;
  command.resources.shader_roots.pixel_shader.context_key =
      normalized_event.pixel_shader_root_context_key;
  command.resources.shader_roots.pixel_shader.symbol_address =
      normalized_event.pixel_shader_root_symbol_address;
  command.resources.shader_roots.pixel_shader.lr =
      normalized_event.pixel_shader_root_lr;
  command.resources.index_buffer = normalized_event.index_buffer;
  for (uint32_t i = 0; i < command.resources.textures.size(); ++i) {
    command.resources.textures[i] = normalized_event.textures[i];
  }
  for (uint32_t i = 0; i < command.resources.streams.size(); ++i) {
    command.resources.streams[i] = normalized_event.streams[i];
  }
  for (uint32_t i = 0; i < command.inline_vertex_buffers.size(); ++i) {
    FableDirectInlineBuffer& inline_buffer = command.inline_vertex_buffers[i];
    if (!inline_buffer.ready()) {
      continue;
    }
    const RendererStreamBindingSnapshot& stream =
        i < command.resources.streams.size() ? command.resources.streams[i]
                                             : RendererStreamBindingSnapshot{};
    const uint32_t effective_stride =
        stream.stride != 0 ? stream.stride : inline_buffer.stride_or_format;
    const bool stride_matches =
        effective_stride == 0 || inline_buffer.stride_or_format == 0 ||
        inline_buffer.stride_or_format == effective_stride;
    const bool covers_stream =
        stream.buffer != 0 && stride_matches &&
        (stream.buffer == inline_buffer.address ||
         (stream.buffer > inline_buffer.address &&
          uint64_t(stream.buffer - inline_buffer.address) <
              uint64_t(inline_buffer.byte_size)));
    if (!covers_stream) {
      inline_buffer = {};
    }
  }
  if (command.inline_index_buffer.ready()) {
    const bool covers_index =
        command.resources.index_buffer != 0 &&
        (command.resources.index_buffer == command.inline_index_buffer.address ||
         (command.resources.index_buffer > command.inline_index_buffer.address &&
          uint64_t(command.resources.index_buffer -
                   command.inline_index_buffer.address) <
              uint64_t(command.inline_index_buffer.byte_size)));
    if (!covers_index) {
      command.inline_index_buffer = {};
    }
  }
  command.viewport = normalized_event.viewport;
  command.scissor = normalized_event.scissor;
  command.scaler = normalized_event.scaler;
  command.pix_video_planes =
      normalized_event.source == RendererDrawEventSource::kPixVideo
          ? normalized_event.pix_video_planes
          : std::array<RendererInlineTextureUploadSnapshot, 3>{};
  command.pipeline.render_states = normalized_event.render_states;
  command.pipeline.pm4 = Pm4StateFromDrawEvent(normalized_event);
  command.sampler_states = normalized_event.sampler_states;
  command.constants.fetch = normalized_event.fetch_constants;
  command.constants.vertex_float = normalized_event.vertex_shader_constants;
  command.constants.pixel_float = normalized_event.pixel_shader_constants;
  command.constants.vertex_int = normalized_event.vertex_shader_int_constants;
  command.constants.pixel_int = normalized_event.pixel_shader_int_constants;
  command.constants.vertex_int_valid_mask =
      normalized_event.vertex_shader_int_constant_valid_mask;
  command.constants.pixel_int_valid_mask =
      normalized_event.pixel_shader_int_constant_valid_mask;
  command.constants.vertex_bool = normalized_event.vertex_shader_bool_constants;
  command.constants.pixel_bool = normalized_event.pixel_shader_bool_constants;

  command.clear.flags = normalized_event.clear_flags;
  command.clear.color = {normalized_event.clear_color[0],
                         normalized_event.clear_color[1],
                         normalized_event.clear_color[2],
                         normalized_event.clear_color[3]};
  command.clear.depth = normalized_event.clear_depth;
  command.clear.stencil = normalized_event.clear_stencil;
  command.clear.rect = normalized_event.clear_rect;
  command.clear.root_call_serial = normalized_event.clear_root_call_serial;
  command.clear.root_context_key = normalized_event.clear_root_context_key;
  command.clear.root_symbol_address = normalized_event.clear_root_symbol_address;
  command.clear.root_lr = normalized_event.clear_root_lr;
  for (uint32_t slot = 0; slot < command.clear.target_roots.color.size();
       ++slot) {
    command.clear.target_roots.color[slot].call_serial =
        normalized_event.clear_target_root_call_serial[slot];
    command.clear.target_roots.color[slot].context_key =
        normalized_event.clear_target_root_context_key[slot];
    command.clear.target_roots.color[slot].symbol_address =
        normalized_event.clear_target_root_symbol_address[slot];
    command.clear.target_roots.color[slot].lr =
        normalized_event.clear_target_root_lr[slot];
  }
  command.clear.target_roots.depth.call_serial =
      normalized_event.clear_depth_root_call_serial;
  command.clear.target_roots.depth.context_key =
      normalized_event.clear_depth_root_context_key;
  command.clear.target_roots.depth.symbol_address =
      normalized_event.clear_depth_root_symbol_address;
  command.clear.target_roots.depth.lr = normalized_event.clear_depth_root_lr;

  command.draw.draw = normalized_event.draw;
  command.draw.source = normalized_event.source;
  command.draw.draw_vertices_up = normalized_event.draw_vertices_up;
  command.draw.private_texture_rectangle =
      normalized_event.private_texture_rectangle;
  command.draw.private_texture_slot = normalized_event.private_texture_slot;
  command.draw.private_texture_source_x = event.private_texture_source_x;
  command.draw.private_texture_source_y = event.private_texture_source_y;
  command.draw.private_texture_corners_valid =
      normalized_event.private_texture_corners_valid;
  command.draw.private_texture_corners =
      normalized_event.private_texture_corners;
  command.draw.private_texture_uv_override =
      normalized_event.private_texture_uv_override;
  command.draw.private_texture_u0 = normalized_event.private_texture_u0;
  command.draw.private_texture_v0 = normalized_event.private_texture_v0;
  command.draw.private_texture_u1 = normalized_event.private_texture_u1;
  command.draw.private_texture_v1 = normalized_event.private_texture_v1;
  command.draw.private_texture_wrapper_call_serial =
      normalized_event.private_texture_wrapper_call_serial;
  command.draw.private_texture_wrapper_context_key =
      normalized_event.private_texture_wrapper_context_key;
  command.draw.private_texture_wrapper_symbol_address =
      normalized_event.private_texture_wrapper_symbol_address;
  command.draw.private_texture_wrapper_source_texture =
      normalized_event.private_texture_wrapper_source_texture;
  command.draw.private_texture_wrapper_source_exact =
      normalized_event.private_texture_wrapper_source_exact;
  command.draw.state_flush_lr = normalized_event.state_flush_lr;

  command.resolve.rb_surface_info = normalized_event.rb_surface_info;
  command.resolve.rb_color_info = {normalized_event.rb_color_info[0],
                                   normalized_event.rb_color_info[1],
                                   normalized_event.rb_color_info[2],
                                   normalized_event.rb_color_info[3]};
  command.resolve.rb_depth_info = normalized_event.rb_depth_info;
  command.resolve.rb_copy_control = normalized_event.rb_copy_control;
  command.resolve.rb_copy_dest_base = normalized_event.rb_copy_dest_base;
  command.resolve.rb_copy_dest_pitch = normalized_event.rb_copy_dest_pitch;
  command.resolve.rb_copy_dest_info = normalized_event.rb_copy_dest_info;
  command.resolve.rb_copy_func = normalized_event.rb_copy_func;
  command.resolve.rb_copy_ref = normalized_event.rb_copy_ref;
  command.resolve.rb_copy_mask = normalized_event.rb_copy_mask;
  command.resolve.rb_copy_surface_slice =
      normalized_event.rb_copy_surface_slice;
}

void PushDrawCommandLocked(const RendererDrawEventSnapshot& event) {
  FableDirectRenderCommand command;
  command.device = g_fable_direct.last_device;
  command.value = event.draw.primitive_type;
  command.value2 = event.draw.primitive_count;
  RefreshRenderCommandPayloadFromEvent(command, event);
  PushRenderCommandLocked(std::move(command));
}

template <typename Fn>
void ForEachCommandDrawEvent(std::vector<FableDirectRenderCommand>& commands,
                             Fn&& fn) {
  for (FableDirectRenderCommand& command : commands) {
    if (RenderCommandCarriesDrawEvent(command)) {
      fn(command);
    }
  }
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
  const auto scratch_draw_targets = [](const FableDirectRenderCommand& command,
                                       uint32_t render_target) {
    if (!RenderCommandCarriesDrawEvent(command) ||
        command.draw.source != RendererDrawEventSource::kScratchWrapper ||
        render_target == 0) {
      return false;
    }
    return std::any_of(command.resources.render_targets.begin(),
                       command.resources.render_targets.end(),
                       [render_target](uint32_t target) {
                         return target == render_target;
                       });
  };
  for (FableDirectRenderCommand& command : commands) {
    AttachCachedScratchBuffersToCommandLocked(command);
    if (RenderCommandCarriesDrawEvent(command) &&
        command.draw.source == RendererDrawEventSource::kScratchWrapper) {
      for (const uint32_t target : command.resources.render_targets) {
        if (target == 0) {
          continue;
        }
        g_fable_direct.submitted_render_commands.erase(
            std::remove_if(g_fable_direct.submitted_render_commands.begin(),
                           g_fable_direct.submitted_render_commands.end(),
                           [&](const FableDirectRenderCommand& existing) {
                             return scratch_draw_targets(existing, target);
                           }),
            g_fable_direct.submitted_render_commands.end());
      }
    }
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

  AppendSubmittedRenderCommandsLocked(
      std::move(g_fable_direct.staged_submitted_render_commands));
  g_fable_direct.staged_submitted_render_commands.clear();
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

FableDirectRenderCommand* CommandDrawEventAt(
    std::vector<FableDirectRenderCommand>& commands, size_t event_index) {
  size_t seen = 0;
  for (FableDirectRenderCommand& command : commands) {
    if (!RenderCommandCarriesDrawEvent(command)) {
      continue;
    }
    if (seen == event_index) {
      return &command;
    }
    ++seen;
  }
  return nullptr;
}

bool RemoveCommandDrawEventBySerial(
    std::vector<FableDirectRenderCommand>& commands, uint64_t serial) {
  if (serial == 0) {
    return false;
  }
  const auto it = std::find_if(
      commands.begin(), commands.end(),
      [&](const FableDirectRenderCommand& command) {
        return RenderCommandCarriesDrawEvent(command) &&
               command.draw_serial == serial;
      });
  if (it == commands.end()) {
    return false;
  }
  commands.erase(it);
  return true;
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

bool ScratchDrawTargetsRenderTarget(const FableDirectRenderCommand& command,
                                    uint32_t render_target) {
  if (!RenderCommandCarriesDrawEvent(command) ||
      command.draw.source != RendererDrawEventSource::kScratchWrapper ||
      render_target == 0) {
    return false;
  }
  for (const uint32_t target : command.resources.render_targets) {
    if (target == render_target) {
      return true;
    }
  }
  return false;
}

size_t RemoveSupersededScratchDrawsForTarget(
    std::vector<FableDirectRenderCommand>& commands, uint32_t render_target) {
  if (render_target == 0) {
    return 0;
  }
  const size_t old_size = commands.size();
  commands.erase(
      std::remove_if(commands.begin(), commands.end(),
                     [&](const FableDirectRenderCommand& command) {
                       return ScratchDrawTargetsRenderTarget(command,
                                                            render_target);
                     }),
      commands.end());
  return old_size - commands.size();
}

template <typename PatchFn>
bool ApplyDrawEventPatch(RendererDrawEventSnapshot& event, PatchFn&& patch) {
  if constexpr (std::is_same_v<
                    std::invoke_result_t<PatchFn&, RendererDrawEventSnapshot&>,
                    bool>) {
    return patch(event);
  } else {
    patch(event);
    return true;
  }
}

template <typename PatchFn>
void PatchRenderCommandEventsLocked(uint64_t draw_serial, PatchFn&& patch) {
  if (draw_serial == 0) {
    return;
  }
  for (FableDirectRenderCommand& command : g_fable_direct.render_commands) {
    if (command.draw_serial == draw_serial) {
      RendererDrawEventSnapshot event = BuildDrawEventFromRenderCommand(command);
      if (ApplyDrawEventPatch(event, patch)) {
        RefreshRenderCommandPayloadFromEvent(command, event);
      }
    }
  }
  for (FableDirectRenderCommand& command :
       g_fable_direct.staged_submitted_render_commands) {
    if (command.draw_serial == draw_serial) {
      RendererDrawEventSnapshot event = BuildDrawEventFromRenderCommand(command);
      if (ApplyDrawEventPatch(event, patch)) {
        RefreshRenderCommandPayloadFromEvent(command, event);
      }
    }
  }
  for (FableDirectRenderCommand& command :
       g_fable_direct.submitted_render_commands) {
    if (command.draw_serial == draw_serial) {
      RendererDrawEventSnapshot event = BuildDrawEventFromRenderCommand(command);
      if (ApplyDrawEventPatch(event, patch)) {
        RefreshRenderCommandPayloadFromEvent(command, event);
      }
    }
  }
}

bool PresentedRenderCommandHasTarget(const FableDirectRenderCommand& command) {
  for (uint32_t render_target : command.resources.render_targets) {
    if (render_target != 0) {
      return true;
    }
  }
  return command.resources.depth_stencil != 0 ||
         command.resolve.rb_copy_dest_base != 0;
}

bool PresentedRenderCommandHasStreamBinding(
    const FableDirectRenderCommand& command) {
  for (const RendererStreamBindingSnapshot& stream : command.resources.streams) {
    if (stream.buffer != 0) {
      return true;
    }
  }
  return false;
}

bool PresentedRenderCommandCanGenerateMissingIndexBuffer(
    const FableDirectRenderCommand& command) {
  const bool source_can_generate =
      command.draw.source == RendererDrawEventSource::kSceneObject ||
      command.draw.source == RendererDrawEventSource::kScratchWrapper;
  if (!source_can_generate || !command.draw.draw.indexed ||
      command.draw.draw.primitive_type != 4u ||
      command.draw.draw.start != 0u ||
      command.draw.draw.base_vertex != 0u) {
    return false;
  }
  if (command.resources.index_buffer != 0 ||
      command.inline_index_buffer.ready()) {
    return false;
  }

  const FableDirectInlineBuffer& inline_vb0 = command.inline_vertex_buffers[0];
  const RendererStreamBindingSnapshot& stream = command.resources.streams[0];
  return stream.buffer != 0 || inline_vb0.ready();
}

bool PresentedRenderCommandIsDrawableCandidate(
    const FableDirectRenderCommand& command) {
  if (!RenderCommandCarriesDrawEvent(command) ||
      command.type == FableDirectRenderCommandType::kClear ||
      command.type == FableDirectRenderCommandType::kResolveCopy) {
    return false;
  }
  if (command.draw.draw.primitive_count == 0 ||
      command.resources.vertex_declaration == 0 ||
      command.resources.vertex_shader == 0 ||
      command.resources.pixel_shader == 0 ||
      !PresentedRenderCommandHasTarget(command)) {
    return false;
  }
  if (command.draw.draw.indexed && command.resources.index_buffer == 0 &&
      !command.inline_index_buffer.ready() &&
      !PresentedRenderCommandCanGenerateMissingIndexBuffer(command)) {
    return false;
  }
  if (!PresentedRenderCommandHasStreamBinding(command) &&
      command.draw.source != RendererDrawEventSource::kPrivateTextureRectangle &&
      command.draw.source != RendererDrawEventSource::kPixVideo) {
    return false;
  }
  return true;
}

void FillPendingCommandSummaryLocked(FableDirectStateSnapshot& snapshot) {
  for (const FableDirectRenderCommand& command :
       g_fable_direct.render_commands) {
    const size_t type_index = static_cast<size_t>(command.type);
    if (type_index < snapshot.pending_command_type_counts.size()) {
      ++snapshot.pending_command_type_counts[type_index];
      if (command.state_root.call_serial != 0 &&
          command.state_root.context_key != 0) {
        ++snapshot.pending_rooted_command_type_counts[type_index];
        if (command.state_root.symbol_address == 0x822869A0u ||
            command.state_root.symbol_address == 0x82264590u) {
          ++snapshot.pending_framebuffer_rooted_command_type_counts[type_index];
        }
      }
    }
    switch (command.type) {
      case FableDirectRenderCommandType::kClear:
        ++snapshot.pending_clear_command_count;
        break;
      case FableDirectRenderCommandType::kResolveCopy:
        ++snapshot.pending_resolve_command_count;
        break;
      case FableDirectRenderCommandType::kDrawPrimitive:
      case FableDirectRenderCommandType::kDrawIndexedPrimitive:
      case FableDirectRenderCommandType::kDrawPrimitiveUP: {
        const size_t source = static_cast<size_t>(command.draw.source);
        if (source < snapshot.pending_draw_source_counts.size()) {
          ++snapshot.pending_draw_source_counts[source];
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
        ++snapshot.pending_state_command_count;
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

bool DirectDrawEventReferencesResource(
    const RendererDrawEventSnapshot& event,
    const FableDirectResourceReference& resource) {
  const auto matches = [&](uint32_t address) {
    return DirectResourceMatchesAddress(resource, address);
  };

  if (matches(event.vertex_declaration) || matches(event.vertex_shader) ||
      matches(event.pixel_shader) || matches(event.index_buffer) ||
      matches(event.depth_stencil)) {
    return true;
  }

  for (uint32_t render_target : event.render_targets) {
    if (matches(render_target)) {
      return true;
    }
  }
  for (const RendererStreamBindingSnapshot& stream : event.streams) {
    if (matches(stream.buffer)) {
      return true;
    }
  }
  for (uint32_t texture : event.textures) {
    if (matches(texture)) {
      return true;
    }
  }
  return false;
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

void ApplyStateSnapshotToDrawEvent(const FableDirectStateSnapshot& snapshot,
                                   RendererDrawEventSnapshot& event,
                                   bool include_targets) {
  if (include_targets) {
    std::copy(snapshot.targets.color.begin(), snapshot.targets.color.end(),
              event.render_targets);
    event.depth_stencil = snapshot.targets.depth;
    for (uint32_t slot = 0; slot < snapshot.target_roots.color.size(); ++slot) {
      event.clear_target_root_call_serial[slot] =
          snapshot.target_roots.color[slot].call_serial;
      event.clear_target_root_context_key[slot] =
          snapshot.target_roots.color[slot].context_key;
      event.clear_target_root_symbol_address[slot] =
          snapshot.target_roots.color[slot].symbol_address;
      event.clear_target_root_lr[slot] = snapshot.target_roots.color[slot].lr;
    }
    event.clear_depth_root_call_serial =
        snapshot.target_roots.depth.call_serial;
    event.clear_depth_root_context_key =
        snapshot.target_roots.depth.context_key;
    event.clear_depth_root_symbol_address =
        snapshot.target_roots.depth.symbol_address;
    event.clear_depth_root_lr = snapshot.target_roots.depth.lr;
  }
  event.vertex_declaration = snapshot.shaders.vertex_declaration;
  event.vertex_shader = snapshot.shaders.vertex_shader;
  event.vertex_shader_root_call_serial =
      snapshot.shader_roots.vertex_shader.call_serial;
  event.vertex_shader_root_context_key =
      snapshot.shader_roots.vertex_shader.context_key;
  event.vertex_shader_root_symbol_address =
      snapshot.shader_roots.vertex_shader.symbol_address;
  event.vertex_shader_root_lr = snapshot.shader_roots.vertex_shader.lr;
  event.pixel_shader = snapshot.shaders.pixel_shader;
  event.pixel_shader_root_call_serial =
      snapshot.shader_roots.pixel_shader.call_serial;
  event.pixel_shader_root_context_key =
      snapshot.shader_roots.pixel_shader.context_key;
  event.pixel_shader_root_symbol_address =
      snapshot.shader_roots.pixel_shader.symbol_address;
  event.pixel_shader_root_lr = snapshot.shader_roots.pixel_shader.lr;
  event.index_buffer = snapshot.input.index_buffer;
  event.render_states = snapshot.render_states;
  std::copy(snapshot.input.streams.begin(), snapshot.input.streams.end(),
            event.streams);
  std::copy(snapshot.textures.textures.begin(), snapshot.textures.textures.end(),
            event.textures);
  event.sampler_states = snapshot.textures.samplers;
  event.fetch_constants = snapshot.constants.fetch;
  event.vertex_shader_constants = snapshot.constants.vertex_float;
  event.pixel_shader_constants = snapshot.constants.pixel_float;
  event.vertex_shader_int_constants = snapshot.constants.vertex_int;
  event.pixel_shader_int_constants = snapshot.constants.pixel_int;
  event.vertex_shader_int_constant_valid_mask =
      snapshot.constants.vertex_int_valid_mask;
  event.pixel_shader_int_constant_valid_mask =
      snapshot.constants.pixel_int_valid_mask;
  event.vertex_shader_bool_constants = snapshot.constants.vertex_bool;
  event.pixel_shader_bool_constants = snapshot.constants.pixel_bool;
  event.clear_flags = snapshot.clear_flags;
  CopyPm4StateToDrawEvent(snapshot.pm4, event);
  event.viewport = snapshot.viewport;
  event.scissor = snapshot.scissor;
  event.scaler = snapshot.scaler;
}

uint32_t CountPresentedDrawableCandidateRenderCommands(
    const std::vector<FableDirectRenderCommand>& commands) {
  uint32_t count = 0;
  for (const FableDirectRenderCommand& command : commands) {
    if (PresentedRenderCommandIsDrawableCandidate(command)) {
      ++count;
    }
  }
  return count;
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetRenderTarget;
  command.device = device;
  command.slot = slot;
  command.value = surface;
  command.state_root = g_fable_direct.target_roots.color[slot];
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetDepthStencil;
  command.device = device;
  command.value = surface;
  command.state_root = g_fable_direct.target_roots.depth;
  PushRenderCommandLocked(std::move(command));
  TouchStateLocked(device);
}

void FableDirectMirrorDepthStencilState(uint32_t device, uint32_t surface,
                                        FableDirectStateRoot root) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const uint32_t previous_surface = g_fable_direct.targets.depth;
  const FableDirectStateRoot previous_root = g_fable_direct.target_roots.depth;
  SetDirtyValue(g_fable_direct.dirty.targets, g_fable_direct.targets.depth,
                surface);
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetTexture;
  command.device = device;
  command.slot = slot;
  command.value = texture;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetStreamSource;
  command.device = device;
  command.slot = slot;
  command.stream = stream;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetIndices;
  command.device = device;
  command.value = index_buffer;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetVertexDeclaration;
  command.device = device;
  command.value = vertex_declaration;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetVertexShader;
  command.device = device;
  command.value = vertex_shader;
  command.state_root = g_fable_direct.shader_roots.vertex_shader;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetPixelShader;
  command.device = device;
  command.value = pixel_shader;
  command.state_root = g_fable_direct.shader_roots.pixel_shader;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetViewport;
  command.device = device;
  command.viewport = viewport;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetScissor;
  command.device = device;
  command.scissor = scissor;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetRenderState;
  command.device = device;
  command.slot = slot;
  command.value = value;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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

void FableDirectSetPackedSamplerState(uint32_t device, uint32_t slot,
                                      uint32_t packed_state,
                                      FableDirectStateRoot root) {
  if (slot >= g_fable_direct.textures.samplers.size()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  SetDirtyValue(g_fable_direct.dirty.textures,
                g_fable_direct.textures.samplers[slot], packed_state);
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetSamplerState;
  command.device = device;
  command.slot = slot;
  command.value = packed_state;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
  TouchStateLocked(device);
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetSamplerState;
  command.device = device;
  command.slot = slot;
  command.value = packed_state;
  command.value2 = state_slot;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kSetPm4Field;
  command.slot = static_cast<uint32_t>(field);
  command.value = value;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
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
  FableDirectRenderCommand command;
  command.type = FableDirectRenderCommandType::kStateFlush;
  command.device = device;
  command.value = lr;
  command.state_root = root;
  PushRenderCommandLocked(std::move(command));
  TouchStateLocked(device);
}

RendererDrawEventSnapshot FableDirectBuildEvent(RendererDrawEventSource source) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  RendererDrawEventSnapshot event;
  event.source = source;
  PopulateDrawEventStateLocked(event);
  return event;
}

RendererDrawEventSnapshot FableDirectBuildDrawEvent(
    RendererDrawEventSource source, const RendererLastDrawSnapshot& draw,
    bool draw_vertices_up) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  RendererDrawEventSnapshot event;
  event.source = source;
  event.draw = draw;
  event.draw_vertices_up = draw_vertices_up;
  PopulateDrawEventStateLocked(event);
  return event;
}

void FableDirectApplyStateSnapshotToDrawEvent(
    const FableDirectStateSnapshot& snapshot, RendererDrawEventSnapshot& event,
    bool include_targets) {
  ApplyStateSnapshotToDrawEvent(snapshot, event, include_targets);
}

void FableDirectApplyDrawEventStatePatch(
    RendererDrawEventSnapshot& event,
    const RendererDrawEventSnapshot& patch) {
  const uint64_t serial = event.serial;
  const uint64_t resource_serial =
      std::max(event.resource_serial, patch.resource_serial);
  const RendererDrawEventSource source = event.source;
  const bool clear = event.clear;
  const uint32_t clear_flags = event.clear_flags;
  const float clear_color[4] = {event.clear_color[0], event.clear_color[1],
                                event.clear_color[2], event.clear_color[3]};
  const float clear_depth = event.clear_depth;
  const uint32_t clear_stencil = event.clear_stencil;
  const RendererScissorSnapshot clear_rect = event.clear_rect;
  const uint64_t clear_root_call_serial = event.clear_root_call_serial;
  const uint64_t clear_root_context_key = event.clear_root_context_key;
  const uint32_t clear_root_symbol_address = event.clear_root_symbol_address;
  const uint32_t clear_root_lr = event.clear_root_lr;
  const RendererLastDrawSnapshot draw = event.draw;
  const bool draw_vertices_up = event.draw_vertices_up;
  const bool private_texture_rectangle = event.private_texture_rectangle;
  const uint32_t private_texture_slot = event.private_texture_slot;
  const float private_texture_source_x = event.private_texture_source_x;
  const float private_texture_source_y = event.private_texture_source_y;
  const bool private_texture_corners_valid =
      event.private_texture_corners_valid;
  const std::array<float, 6> private_texture_corners =
      event.private_texture_corners;
  const bool private_texture_uv_override = event.private_texture_uv_override;
  const float private_texture_u0 = event.private_texture_u0;
  const float private_texture_v0 = event.private_texture_v0;
  const float private_texture_u1 = event.private_texture_u1;
  const float private_texture_v1 = event.private_texture_v1;
  const uint64_t private_texture_wrapper_call_serial =
      event.private_texture_wrapper_call_serial;
  const uint64_t private_texture_wrapper_context_key =
      event.private_texture_wrapper_context_key;
  const uint32_t private_texture_wrapper_symbol_address =
      event.private_texture_wrapper_symbol_address;
  const uint32_t private_texture_wrapper_source_texture =
      event.private_texture_wrapper_source_texture;
  const bool private_texture_wrapper_source_exact =
      event.private_texture_wrapper_source_exact;

  event = patch;
  event.serial = serial;
  event.resource_serial = resource_serial;
  event.source = source;
  event.clear = clear;
  event.clear_flags = clear_flags;
  event.clear_color[0] = clear_color[0];
  event.clear_color[1] = clear_color[1];
  event.clear_color[2] = clear_color[2];
  event.clear_color[3] = clear_color[3];
  event.clear_depth = clear_depth;
  event.clear_stencil = clear_stencil;
  event.clear_rect = clear_rect;
  event.clear_root_call_serial = clear_root_call_serial;
  event.clear_root_context_key = clear_root_context_key;
  event.clear_root_symbol_address = clear_root_symbol_address;
  event.clear_root_lr = clear_root_lr;
  event.draw = draw;
  event.draw_vertices_up = draw_vertices_up;
  event.private_texture_rectangle = private_texture_rectangle;
  event.private_texture_slot = private_texture_slot;
  event.private_texture_source_x = private_texture_source_x;
  event.private_texture_source_y = private_texture_source_y;
  event.private_texture_corners_valid = private_texture_corners_valid;
  event.private_texture_corners = private_texture_corners;
  event.private_texture_uv_override = private_texture_uv_override;
  event.private_texture_u0 = private_texture_u0;
  event.private_texture_v0 = private_texture_v0;
  event.private_texture_u1 = private_texture_u1;
  event.private_texture_v1 = private_texture_v1;
  event.private_texture_wrapper_call_serial =
      private_texture_wrapper_call_serial;
  event.private_texture_wrapper_context_key =
      private_texture_wrapper_context_key;
  event.private_texture_wrapper_symbol_address =
      private_texture_wrapper_symbol_address;
  event.private_texture_wrapper_source_texture =
      private_texture_wrapper_source_texture;
  event.private_texture_wrapper_source_exact =
      private_texture_wrapper_source_exact;
}

void FableDirectApplyWrappedDrawEventStatePatch(
    RendererDrawEventSnapshot& event,
    const RendererDrawEventSnapshot& patch) {
  if (event.source == RendererDrawEventSource::kPixVideo ||
      event.source == RendererDrawEventSource::kPm4Draw ||
      event.source == RendererDrawEventSource::kSceneObject ||
      event.source == RendererDrawEventSource::kPrivateTextureRectangle ||
      event.private_texture_rectangle) {
    return;
  }

  std::array<RendererStreamBindingSnapshot, 16> scratch_streams = {};
  std::copy(std::begin(event.streams), std::end(event.streams),
            scratch_streams.begin());
  const uint32_t scratch_index_buffer = event.index_buffer;
  const bool preserve_scratch_bindings =
      event.source == RendererDrawEventSource::kScratchWrapper &&
      (std::any_of(scratch_streams.begin(), scratch_streams.end(),
                   [](const RendererStreamBindingSnapshot& stream) {
                     return stream.buffer != 0;
                   }) ||
       scratch_index_buffer != 0);

  FableDirectApplyDrawEventStatePatch(event, patch);
  if (preserve_scratch_bindings) {
    for (uint32_t slot = 0; slot < std::size(event.streams); ++slot) {
      if (scratch_streams[slot].buffer != 0) {
        event.streams[slot] = scratch_streams[slot];
      }
    }
    if (event.draw.indexed && scratch_index_buffer != 0) {
      event.index_buffer = scratch_index_buffer;
    }
  }
}

bool TryCaptureExactNonVideoRenderCommandsLocked(uint64_t submit,
                                                 bool log_miss);

RendererDrawEventSnapshot FableDirectRecordDrawEvent(
    RendererDrawEventSnapshot event) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  if (event.serial == 0) {
    event.serial = ++g_fable_direct.next_draw_event_serial;
  } else {
    g_fable_direct.next_draw_event_serial =
        std::max(g_fable_direct.next_draw_event_serial, event.serial);
  }
  g_fable_direct.last_draw = event.draw;
  if (!event.clear) {
    ++g_fable_direct.draw_count;
    if (event.draw.indexed) {
      ++g_fable_direct.indexed_draw_count;
    }
  }
  PushDrawCommandLocked(event);
  TouchStateLocked();
  TryCaptureExactNonVideoRenderCommandsLocked(event.serial, false);
  return event;
}

bool FableDirectPatchDrawEvent(
    uint64_t serial,
    const std::function<bool(RendererDrawEventSnapshot&)>& patch) {
  if (serial == 0 || !patch) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  bool patched_any = false;
  FableDirectPatchScope patch_scope;
  PatchRenderCommandEventsLocked(
      serial, [&](RendererDrawEventSnapshot& event) {
        const bool patched = patch(event);
        patched_any = patched_any || patched;
        return patched;
      });
  if (patched_any) {
    TouchStateLocked();
    TryCaptureExactNonVideoRenderCommandsLocked(serial, false);
  }
  return patched_any;
}

bool FableDirectRemoveDrawEvent(uint64_t serial) {
  if (serial == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const bool removed_pending =
      RemoveCommandDrawEventBySerial(g_fable_direct.render_commands, serial);
  const bool removed =
      removed_pending |
      RemoveCommandDrawEventBySerial(g_fable_direct.staged_submitted_render_commands,
                                     serial) |
      RemoveCommandDrawEventBySerial(g_fable_direct.submitted_render_commands,
                                     serial);
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

size_t FableDirectRemoveSupersededScratchDrawEventsForTarget(
    uint32_t render_target) {
  if (render_target == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const size_t removed_pending =
      RemoveSupersededScratchDrawsForTarget(g_fable_direct.render_commands,
                                            render_target);
  const size_t removed =
      removed_pending +
      RemoveSupersededScratchDrawsForTarget(
          g_fable_direct.staged_submitted_render_commands, render_target) +
      RemoveSupersededScratchDrawsForTarget(
          g_fable_direct.submitted_render_commands, render_target);
  if (removed != 0) {
    if (removed_pending != 0) {
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
                              size_t size) {
  if (address == 0 || !ScratchUploadSnapshotValid(data, size)) {
    return false;
  }
  const uint8_t* bytes = static_cast<const uint8_t*>(data);

  FableDirectInlineBuffer* inline_buffer = nullptr;
  uint32_t inline_address = address;
  uint32_t inline_stride_or_format = stride_or_format;
  uint32_t matched_stream_slot = UINT32_MAX;
  size_t upload_offset = 0;
  if (index_buffer) {
    const uint32_t command_address = command.resources.index_buffer;
    if (command_address == address) {
      inline_buffer = &command.inline_index_buffer;
    } else if (command_address > address &&
               uint64_t(command_address - address) < uint64_t(size)) {
      inline_buffer = &command.inline_index_buffer;
      inline_address = command_address;
      upload_offset = command_address - address;
    } else {
      return false;
    }
  } else {
    for (uint32_t slot = 0; slot < command.resources.streams.size(); ++slot) {
      const uint32_t command_stride = command.resources.streams[slot].stride;
      const uint32_t effective_stride =
          stride_or_format != 0 ? stride_or_format : command_stride;
      if (effective_stride == 0 ||
          (command_stride != 0 && command_stride != effective_stride)) {
        continue;
      }
      const uint32_t command_address = command.resources.streams[slot].buffer;
      if (command_address == address) {
        inline_buffer = &command.inline_vertex_buffers[slot];
        inline_address = command_address;
        inline_stride_or_format = effective_stride;
        matched_stream_slot = slot;
        upload_offset = 0;
        break;
      }
      if (command_address > address &&
          uint64_t(command_address - address) < uint64_t(size)) {
        inline_buffer = &command.inline_vertex_buffers[slot];
        inline_address = command_address;
        inline_stride_or_format = effective_stride;
        matched_stream_slot = slot;
        upload_offset = command_address - address;
        break;
      }
    }
    if (!inline_buffer) {
      return false;
    }
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
  inline_buffer->address = inline_address;
  inline_buffer->byte_size = static_cast<uint32_t>(inline_size);
  inline_buffer->stride_or_format = inline_stride_or_format;
  inline_buffer->data.resize(inline_size);
  std::memcpy(inline_buffer->data.data(), bytes + upload_offset, inline_size);
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
    TryCaptureExactNonVideoRenderCommandsLocked(draw_serial, false);
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
    TryCaptureExactNonVideoRenderCommandsLocked(0, false);
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

size_t FableDirectPatchPendingDrawEvents(
    const std::function<bool(RendererDrawEventSnapshot&)>& patch) {
  if (!patch) {
    return 0;
  }
  if (g_fable_direct_patch_depth != 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  if (g_fable_direct.render_command_draw_event_count == 0) {
    return 0;
  }
  size_t patched_count = 0;
	  ForEachCommandDrawEvent(
	      g_fable_direct.render_commands,
	      [&](FableDirectRenderCommand& command) {
	    RendererDrawEventSnapshot event = BuildDrawEventFromRenderCommand(command);
	    if (patch(event)) {
	      RefreshRenderCommandPayloadFromEvent(command, event);
	      const uint64_t patched_serial = command.draw_serial;
	      PatchRenderCommandEventsLocked(patched_serial, patch);
	      ++patched_count;
	    }
	  });
  if (patched_count != 0) {
    TouchStateLocked();
    TryCaptureExactNonVideoRenderCommandsLocked(0, false);
  }
  return patched_count;
}

size_t FableDirectPendingDrawEventCount() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  return g_fable_direct.render_command_draw_event_count;
}

void FableDirectCaptureSubmittedRenderCommands(uint64_t submit, bool publish) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  ++g_fable_direct.command_boundary_capture_count;
  g_fable_direct.last_command_boundary_submit = submit;
  g_fable_direct.last_command_boundary_pending_commands =
      g_fable_direct.render_commands.size();
  g_fable_direct.last_command_boundary_pending_draws =
      g_fable_direct.render_command_direct_event_count;
  if (g_fable_direct.render_commands.empty()) {
    ++g_fable_direct.empty_command_boundary_capture_count;
    g_fable_direct.last_submitted_draw_event_count =
        StagedAndSubmittedDirectEventCountLocked();
    if (publish) {
      PublishStagedSubmittedRenderCommandsLocked();
    }
    return;
  }

  std::vector<FableDirectRenderCommand> pending_commands =
      std::move(g_fable_direct.render_commands);
  const size_t pending_direct_event_count =
      g_fable_direct.render_command_direct_event_count;
  g_fable_direct.render_commands.clear();
  g_fable_direct.render_command_draw_event_count = 0;
  g_fable_direct.render_command_direct_event_count = 0;
  if (pending_direct_event_count == 0) {
    g_fable_direct.last_submitted_draw_event_count =
        StagedAndSubmittedDirectEventCountLocked();
    if (publish) {
      PublishStagedSubmittedRenderCommandsLocked();
    }
    return;
  }

  StageSubmittedRenderCommandsLocked(std::move(pending_commands));
  if (publish) {
    PublishStagedSubmittedRenderCommandsLocked();
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

bool DirectCommandIsExactClearAction(
    const FableDirectRenderCommand& command) {
  const auto is_exact_clear_root = [](uint32_t symbol_address) {
    return symbol_address == 0x821E2FE0u || symbol_address == 0x821EF830u ||
           symbol_address == 0x8227F510u || symbol_address == 0x8227F540u;
  };
  if (command.type != FableDirectRenderCommandType::kClear ||
      command.clear.flags == 0 || command.clear.root_call_serial == 0 ||
      command.clear.root_context_key == 0 ||
      !is_exact_clear_root(command.clear.root_symbol_address)) {
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

bool PendingCommandsAreExactNonVideoWrapperBoundaryLocked() {
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

bool TryCaptureExactNonVideoRenderCommandsLocked(uint64_t submit,
                                                 bool log_miss) {
  if (g_fable_direct.render_commands.empty() ||
      !PendingCommandsAreExactNonVideoWrapperBoundaryLocked()) {
    static uint32_t logged_exact_nonvideo_capture_misses = 0;
    if (log_miss && !g_fable_direct.render_commands.empty() &&
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
      REXLOG_INFO(
          "Refii direct exact non-video capture miss #{} submit={} "
          "commands={} direct={} draws={} first_type={} first_source={} "
          "first_draw={} first_indexed={} first_private={} "
          "first_wrapper=0x{:08X}/{} first_context=0x{:016X} "
          "first_lr=0x{:08X} "
          "first_inline_vb={} first_inline_ib={} first_rt=0x{:08X} "
          "first_ds=0x{:08X} first_vs=0x{:08X} first_ps=0x{:08X} "
          "first_tex13=0x{:08X} first_clear={} exact_clear={} "
          "clear_root=0x{:08X}/{} clear_context=0x{:016X} "
          "clear_flags=0x{:08X} clear_target_root=0x{:08X}/{} "
          "clear_depth_root=0x{:08X}/{}",
          ++logged_exact_nonvideo_capture_misses,
          static_cast<unsigned long long>(submit),
          g_fable_direct.render_commands.size(),
          g_fable_direct.render_command_direct_event_count,
          g_fable_direct.render_command_draw_event_count,
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
          first_direct_command ? first_direct_command->resources.vertex_shader
                               : 0u,
          first_direct_command ? first_direct_command->resources.pixel_shader
                               : 0u,
          first_direct_command ? first_direct_command->resources.textures[13]
                               : 0u,
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

  ++g_fable_direct.command_boundary_capture_count;
  g_fable_direct.last_command_boundary_submit = submit;
  g_fable_direct.last_command_boundary_pending_commands =
      g_fable_direct.render_commands.size();
  g_fable_direct.last_command_boundary_pending_draws =
      g_fable_direct.render_command_direct_event_count;

  std::vector<FableDirectRenderCommand> pending_commands =
      std::move(g_fable_direct.render_commands);
  const size_t pending_direct_event_count =
      g_fable_direct.render_command_direct_event_count;
  g_fable_direct.render_commands.clear();
  g_fable_direct.render_command_draw_event_count = 0;
  g_fable_direct.render_command_direct_event_count = 0;
  if (pending_direct_event_count == 0) {
    g_fable_direct.last_submitted_draw_event_count =
        StagedAndSubmittedDirectEventCountLocked();
    return false;
  }

  StageSubmittedRenderCommandsLocked(std::move(pending_commands));
  PublishStagedSubmittedRenderCommandsLocked();
  return true;
}

bool FableDirectCaptureExactNonVideoRenderCommands(uint64_t submit) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  return TryCaptureExactNonVideoRenderCommandsLocked(submit, true);
}

bool FableDirectPublishStagedSubmittedRenderCommandsForNonVideo() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  if (g_fable_direct.staged_submitted_render_commands.empty()) {
    return false;
  }

  bool has_direct_work = false;
  for (const FableDirectRenderCommand& command :
       g_fable_direct.staged_submitted_render_commands) {
    if (!RenderCommandCarriesDirectEvent(command)) {
      continue;
    }
    has_direct_work = true;
    if (RenderCommandCarriesDrawEvent(command) &&
        command.draw.source == RendererDrawEventSource::kPixVideo) {
      return false;
    }
  }

  if (!has_direct_work) {
    return false;
  }

  PublishStagedSubmittedRenderCommandsLocked();
  return true;
}

uint32_t FableDirectPruneExactFramebufferStateCommands() {
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

uint32_t FableDirectPruneExactRootedStateCommands() {
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

uint32_t FableDirectPruneStateFlushCommands() {
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

uint32_t FableDirectPruneStateOnlyCommands() {
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
  FillPendingCommandSummaryLocked(snapshot);
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
  snapshot.last_command_boundary_pending_commands =
      g_fable_direct.last_command_boundary_pending_commands;
  snapshot.last_command_boundary_pending_draws =
      g_fable_direct.last_command_boundary_pending_draws;
  snapshot.last_submitted_draw_event_count =
      g_fable_direct.last_submitted_draw_event_count;
  snapshot.dropped_render_commands = g_fable_direct.dropped_render_commands;
  snapshot.consumed_exact_state_commands =
      g_fable_direct.consumed_exact_state_commands;
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

std::vector<FableDirectRenderCommand> FableDirectPendingRenderCommandSnapshot(
    size_t max_commands) {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  const size_t count =
      std::min(max_commands, g_fable_direct.render_commands.size());
  return std::vector<FableDirectRenderCommand>(
      g_fable_direct.render_commands.begin(),
      g_fable_direct.render_commands.begin() + count);
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

std::vector<FableDirectRenderCommand> ConsumeFableDirectRenderCommands() {
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  std::vector<FableDirectRenderCommand> commands;
  commands.swap(g_fable_direct.render_commands);
  g_fable_direct.render_command_draw_event_count = 0;
  g_fable_direct.render_command_direct_event_count = 0;
  return commands;
}

bool FableDirectSubmittedRenderCommandsAreStronger(uint32_t current_candidates,
                                                   size_t current_size) {
  (void)current_candidates;
  (void)current_size;
  std::lock_guard<std::mutex> lock(g_fable_direct.mutex);
  if (g_fable_direct.submitted_render_command_serial == 0 ||
      g_fable_direct.submitted_render_command_serial ==
          g_fable_direct.consumed_submitted_render_command_serial ||
      g_fable_direct.submitted_render_commands.empty()) {
    return false;
  }

  const uint32_t submitted_candidates =
      CountPresentedDrawableCandidateRenderCommands(
          g_fable_direct.submitted_render_commands);
  const size_t submitted_direct_events =
      CountCommandDirectEvents(g_fable_direct.submitted_render_commands);
  (void)submitted_candidates;
  return submitted_direct_events != 0;
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
