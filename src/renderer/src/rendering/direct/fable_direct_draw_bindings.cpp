#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer_internal.h"

#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/bik/bink_plane_publish.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "renderdoc_debugger.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_surface_texture_link_classification.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_cache_keys.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_batch.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_readback.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_index_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_inline_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_primitive8.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_render_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_scratch_wrapper_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_database_command_packets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_actions.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_analysis.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_replay_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_texture_slot_masks.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_capture.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_resource_debug_snapshot.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_draw_targets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_draw_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_frame_packet_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_resource_facts.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_plane_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_vertex_transform.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_classification.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"
#include "runtime/plume/draw/plume_draw_state.h"
#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/plume/resource/plume_vertex_declaration.h"
#include "renderer_runtime.h"
#include "runtime/shader_library.h"

#include <algorithm>
#include <atomic>
#include <array>
#include <chrono>
#include <cctype>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <rex/logging.h>
#include <tracy/Tracy.hpp>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface_builders.h>
#if defined(_WIN64)
#include "plume_present_blit_ps.hlsl.dxil.h"
#include "plume_present_blit_vs.hlsl.dxil.h"
#endif
#include "plume_present_blit_ps.hlsl.spirv.h"
#include "plume_present_blit_vs.hlsl.spirv.h"
#endif

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME

bool BindDirectIndexBufferLocked(plume::RenderDevice* device,
                                 plume::RenderCommandList* command_list,
                                 const FableDirectRenderCommand& command) {
  if (!command.draw.draw.indexed) {
    return true;
  }
  if (command.resources.index_buffer == 0 &&
      !command.inline_index_buffer.ready()) {
    return false;
  }
  const uint32_t vertex_capacity = DirectPrimaryVertexCapacityLocked(command);
  uint32_t max_index = 0;
  const FableDirectInlineBuffer& inline_buffer = command.inline_index_buffer;
  const uint32_t inline_offset =
      DirectInlineBufferOffsetForCommandAddress(command, inline_buffer,
                                                command.resources.index_buffer);
  if (inline_offset != UINT32_MAX) {
    const uint32_t stride =
        command.draw.index_format != 0
            ? NormalizeDirectIndexFormat(command.draw.index_format)
            : NormalizeDirectIndexFormat(inline_buffer.stride_or_format);
    const uint8_t* inline_index_data =
        inline_buffer.data.data() + inline_offset;
    const size_t inline_index_size =
        inline_buffer.data.size() > inline_offset
            ? inline_buffer.data.size() - inline_offset
            : 0u;
    const bool index_valid = DirectIndexBufferLooksValidForCommand(
        command, inline_index_data, inline_index_size, stride,
        vertex_capacity, max_index);
    if (!index_valid) {
      uint32_t swapped_max_index = 0;
      const bool swapped_index_valid = DirectIndexBufferLooksValidForCommand(
          command, inline_index_data, inline_index_size, stride,
          vertex_capacity, swapped_max_index, true);
      if (swapped_index_valid) {
        std::vector<uint8_t> swapped_indices(
            inline_buffer.data.begin() + inline_offset,
            inline_buffer.data.end());
        DirectSwapIndexBytes(swapped_indices.data(), swapped_indices.size(),
                             stride);
        if (g_direct_renderer.diagnostics.logged_index_endian_repairs < 24u) {
          REXLOG_INFO(
              "Refii plume direct renderer byte-swapped inline IB #{} "
              "guest=0x{:08X} bytes={} stride={} old_max={} new_max={} "
              "vertex_capacity={} prim={} count={} start={} vb0=0x{:08X}",
              g_direct_renderer.diagnostics.logged_index_endian_repairs + 1u,
              inline_buffer.address, inline_buffer.byte_size, stride,
              max_index, swapped_max_index, vertex_capacity,
              command.draw.draw.primitive_type, command.draw.draw.primitive_count,
              command.draw.draw.start, command.resources.streams[0].buffer);
          ++g_direct_renderer.diagnostics.logged_index_endian_repairs;
        }
        plume::RenderBuffer* upload = GetOrCreateCachedUploadBufferWithDataLocked(
            device, swapped_indices.data(),
            static_cast<uint32_t>(swapped_indices.size()),
            "Refii Fable byte-swapped inline IB",
            plume::RenderBufferFlag::INDEX);
        if (!upload) {
          return false;
        }
        const plume::RenderFormat format =
            stride == 4u ? plume::RenderFormat::R32_UINT
                         : plume::RenderFormat::R16_UINT;
        plume::RenderIndexBufferView view(
            upload->at(0), static_cast<uint32_t>(swapped_indices.size()),
            format);
        command_list->setIndexBuffer(&view);
        return true;
      }
      static uint32_t logged_inline_index_rejects = 0;
      if (command.draw.source == RendererDrawEventSource::kScratchWrapper &&
          logged_inline_index_rejects < 16u) {
        const auto read_index = [&](uint32_t index, bool byte_swap) {
          const uint64_t byte_offset =
              uint64_t(command.draw.draw.start + index) * uint64_t(stride);
          if (byte_offset + stride > inline_index_size) {
            return UINT32_MAX;
          }
          return DirectReadIndexValue(
              inline_index_data + static_cast<size_t>(byte_offset), stride,
              byte_swap);
        };
        REXLOG_WARN(
            "Refii plume inline scratch IB rejected #{} frame={} serial={} "
            "draw={} wrapper=0x{:08X} ib=0x{:08X} inline=0x{:08X}/{} "
            "offset={} size={} stride={} vertex_capacity={} start={} "
            "count={} max={} swapped_max={} "
            "idx={},{},{},{},{},{} swapped={},{},{},{},{},{} "
            "vb0=0x{:08X}/{} vb0_inline=0x{:08X}/{}",
            ++logged_inline_index_rejects,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial),
            command.draw.private_texture_wrapper_symbol_address,
            command.resources.index_buffer, inline_buffer.address,
            inline_buffer.byte_size, inline_offset, inline_index_size, stride,
            vertex_capacity, command.draw.draw.start,
            DirectDrawVertexOrIndexCount(command), max_index,
            swapped_max_index, read_index(0, false), read_index(1, false),
            read_index(2, false), read_index(3, false),
            read_index(4, false), read_index(5, false),
            read_index(0, true), read_index(1, true), read_index(2, true),
            read_index(3, true), read_index(4, true), read_index(5, true),
            command.resources.streams[0].buffer,
            command.resources.streams[0].stride,
            command.inline_vertex_buffers[0].address,
            command.inline_vertex_buffers[0].byte_size);
      }
      return false;
    }
    char name[128] = {};
    std::snprintf(name, sizeof(name),
                  "Refii Fable cached inline IB guest=0x%08X bytes=%u",
                  inline_buffer.address, inline_buffer.byte_size);
    // Stored-hash keying, same contract as
    // GetOrCreateCachedInlineVertexBufferLocked: the content hash was stamped
    // where the payload bytes were written; unstamped payloads hash lazily.
    // Guards mirror the GetOrCreateCachedUploadBufferWithDataLocked wrapper
    // this replaced (64 KiB cache ceiling).
    constexpr size_t kMaxCachedInlineIndexBytes = 64u * 1024u;
    if (!device || inline_buffer.byte_size > kMaxCachedInlineIndexBytes) {
      return false;
    }
    const DirectUploadBufferKey inline_index_key{
        inline_buffer.content_hash != 0
            ? inline_buffer.content_hash
            : DirectHashBytes(inline_buffer.data.data(),
                              inline_buffer.byte_size),
        inline_buffer.byte_size,
        static_cast<uint32_t>(plume::RenderBufferFlag::INDEX)};
    plume::RenderBuffer* upload = GetOrCreateCachedUploadBufferForKeyLocked(
        device, inline_index_key, inline_buffer.data.data(),
        inline_buffer.byte_size, name, plume::RenderBufferFlag::INDEX);
    if (!upload) {
      return false;
    }
    const plume::RenderFormat format =
        stride == 4u ? plume::RenderFormat::R32_UINT
                     : plume::RenderFormat::R16_UINT;
    plume::RenderIndexBufferView view(
        upload->at(inline_offset), inline_buffer.byte_size - inline_offset,
        format);
    command_list->setIndexBuffer(&view);
    return true;
  }

  PlumeTrackedResource* index_buffer =
      FindDirectResourceLocked(command.resources.index_buffer);
  if (index_buffer && PlumeGuestBufferReady(*index_buffer)) {
    if (command.draw.source != RendererDrawEventSource::kScratchWrapper &&
        (!DirectTrackedScratchBufferUploadLooksReady(*index_buffer) ||
         !DirectBytesHaveNonZero(index_buffer->index_shadow_upload.data(),
                                 index_buffer->index_shadow_upload.size()))) {
      return false;
    }
    const uint32_t stride =
        command.draw.index_format != 0
            ? NormalizeDirectIndexFormat(command.draw.index_format)
            : NormalizeDirectIndexFormat(
                  PlumeGuestBufferStrideOrFormat(*index_buffer));
    const bool index_valid = DirectIndexBufferLooksValidForCommand(
        command, index_buffer->index_shadow_upload.data(),
        index_buffer->index_shadow_upload.size(), stride, vertex_capacity,
        max_index);
    if (!index_valid) {
      uint32_t swapped_max_index = 0;
      const bool swapped_index_valid = DirectIndexBufferLooksValidForCommand(
          command, index_buffer->index_shadow_upload.data(),
          index_buffer->index_shadow_upload.size(), stride, vertex_capacity,
          swapped_max_index, true);
      if (swapped_index_valid && !index_buffer->index_shadow_upload.empty()) {
        std::vector<uint8_t> swapped_indices =
            index_buffer->index_shadow_upload;
        DirectSwapIndexBytes(swapped_indices.data(), swapped_indices.size(),
                             stride);
        if (g_direct_renderer.diagnostics.logged_index_endian_repairs < 24u) {
          REXLOG_INFO(
              "Refii plume direct renderer byte-swapped tracked IB #{} "
              "guest=0x{:08X} bytes={} stride={} old_max={} new_max={} "
              "vertex_capacity={} prim={} count={} start={} vb0=0x{:08X}",
              g_direct_renderer.diagnostics.logged_index_endian_repairs + 1u,
              command.resources.index_buffer, PlumeGuestBufferByteSize(*index_buffer),
              stride, max_index, swapped_max_index, vertex_capacity,
              command.draw.draw.primitive_type, command.draw.draw.primitive_count,
              command.draw.draw.start, command.resources.streams[0].buffer);
          ++g_direct_renderer.diagnostics.logged_index_endian_repairs;
        }
        plume::RenderBuffer* upload = GetOrCreateCachedUploadBufferWithDataLocked(
            device, swapped_indices.data(),
            static_cast<uint32_t>(swapped_indices.size()),
            "Refii Fable byte-swapped tracked IB",
            plume::RenderBufferFlag::INDEX);
        if (upload) {
          const plume::RenderFormat format =
              stride == 4u ? plume::RenderFormat::R32_UINT
                           : plume::RenderFormat::R16_UINT;
          plume::RenderIndexBufferView view(
              upload->at(0), static_cast<uint32_t>(swapped_indices.size()),
              format);
          command_list->setIndexBuffer(&view);
          return true;
        }
      }
      return false;
    }
    return BindDirectTrackedIndexBufferLocked(command_list, command,
                                             *index_buffer, vertex_capacity,
                                             false);
  }
  return false;
}

bool BindDirectQuadListIndexBufferLocked(plume::RenderDevice* device,
                                         plume::RenderCommandList* command_list,
                                         uint32_t vertex_count) {
  if (!device || !command_list) {
    return false;
  }
  const uint32_t index_count = DirectQuadListIndexCount(vertex_count);
  if (index_count == 0) {
    return false;
  }

  PlumeCachedIndexBuffer* cached_buffer = nullptr;
  for (PlumeCachedIndexBuffer& candidate :
       g_plume_resources.quad_list_index_buffers) {
    if (candidate.index_count >= index_count && candidate.buffer) {
      cached_buffer = &candidate;
      break;
    }
  }
  if (!cached_buffer) {
    const uint32_t quad_count = vertex_count / 4u;
    std::vector<uint32_t> indices(static_cast<size_t>(quad_count) * 6u);
    for (uint32_t quad = 0; quad < quad_count; ++quad) {
      const uint32_t vertex = quad * 4u;
      const uint32_t index = quad * 6u;
      indices[index + 0u] = vertex + 0u;
      indices[index + 1u] = vertex + 1u;
      indices[index + 2u] = vertex + 2u;
      indices[index + 3u] = vertex + 0u;
      indices[index + 4u] = vertex + 2u;
      indices[index + 5u] = vertex + 3u;
    }

    char name[128] = {};
    std::snprintf(name, sizeof(name),
                  "Refii Fable direct quad-list indices vertices=%u",
                  vertex_count);
    PlumeCachedIndexBuffer entry;
    entry.index_count = index_count;
    entry.buffer = CreateUploadBufferWithData(
        device, indices.data(),
        static_cast<uint32_t>(indices.size() * sizeof(indices[0])), name,
        plume::RenderBufferFlag::INDEX);
    if (!entry.buffer) {
      return false;
    }
    g_plume_resources.quad_list_index_buffers.emplace_back(std::move(entry));
    cached_buffer = &g_plume_resources.quad_list_index_buffers.back();
  }

  plume::RenderIndexBufferView view(
      cached_buffer->buffer->at(0),
      static_cast<uint64_t>(index_count) * sizeof(uint32_t),
      plume::RenderFormat::R32_UINT);
  command_list->setIndexBuffer(&view);
  return true;
}

void TransitionDirectSampledTexturesForDrawLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer) {
  if (!command_list) {
    return;
  }
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  std::unordered_set<plume::RenderTexture*> transitioned;
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    PlumeTrackedResource* texture =
        ResolveDirectSampledTextureForCommandLocked(command, slot);

    // §3 BINDING-AUTHORITY SHADOW (measurement only, binds nothing).
    //
    // The migration's step 3 is "descriptor build consumes packet slots via
    // the registry; delete the sampling resolvers", and its delete-gate is
    // "sampled-slot binding reads GTL". This is the one place that decides
    // WHAT a slot samples, so it is where the gap is measurable: compare the
    // maze's answer against the straightforward stated answer — bind exactly
    // the guest address the command names, which is what packet.textures[slot]
    // carries.
    //
    // Read the agreement rate before touching the binding path. High agreement
    // means the maze is mostly redundant and the cutover is small; low
    // agreement means it is doing real work that has to be understood first,
    // because getting this wrong shows up as WRONG TEXTURES, not a log line.
    {
      const uint32_t stated_guest = command.resources.textures[slot];
      PlumeTrackedResource* stated =
          stated_guest != 0 ? FindDirectResourceLocked(stated_guest) : nullptr;
      const bool maze_usable = texture && PlumeGuestImageReady(*texture);
      const bool stated_usable = stated && PlumeGuestImageReady(*stated);
      ++g_direct_renderer.gtl_binding_shadow_slots;
      if (maze_usable && stated_usable) {
        if (texture == stated) {
          ++g_direct_renderer.gtl_binding_shadow_agree;
        } else if (ResolveDirectExactBinkWrapperPlaneForSlotLocked(
                       command, slot) == texture) {
          // Video plane substitution, not a disagreement. Re-resolved only on
          // the rare non-agreeing slot, so this costs nothing on the common
          // path.
          ++g_direct_renderer.gtl_binding_shadow_bink_override;
        } else {
          ++g_direct_renderer.gtl_binding_shadow_differ;
        }
      } else if (maze_usable) {
        ++g_direct_renderer.gtl_binding_shadow_maze_only;
      } else if (stated_usable) {
        ++g_direct_renderer.gtl_binding_shadow_stated_only;
      } else {
        ++g_direct_renderer.gtl_binding_shadow_neither;
      }
    }

    if (!texture || !PlumeGuestImageReady(*texture)) {
      continue;
    }
    plume::RenderTexture* plume_texture = texture->guest_texture.texture.get();
    bool bound_as_color_target = false;
    for (uint32_t slot = 0; slot < framebuffer.color_count; ++slot) {
      bound_as_color_target =
          bound_as_color_target ||
          plume_texture == framebuffer.color_textures[slot];
    }
    if (!plume_texture || bound_as_color_target ||
        plume_texture == framebuffer.depth_texture ||
        !transitioned.insert(plume_texture).second) {
      continue;
    }
    RecordPendingTextureUploadLocked(command_list, *texture);
    RecordDirectTextureBarrierLocked(command_list,
                                     plume::RenderBarrierStage::GRAPHICS,
                                     plume_texture,
                                     plume::RenderTextureLayout::SHADER_READ);
    if (plume_texture == g_direct_renderer.last_scene_texture) {
      g_direct_renderer.last_scene_layout = plume::RenderTextureLayout::SHADER_READ;
    }
  }
}

void RecordDirectClearCommand(plume::RenderDevice* device,
                              plume::RenderCommandList* command_list,
                              plume::RenderFramebuffer* output_framebuffer,
                              plume::RenderFormat output_depth_format,
                              const FableDirectRenderCommand& command) {
  if (!device || !command_list || !output_framebuffer) {
    return;
  }
  // Per-compose clear framebuffer memo — see DirectClearFramebufferMemoEntry.
  // The Ensure*/Resolve callees are SHARED with the draw path, so the memo
  // wraps them here at the clear call site only. Not memoized (fail open):
  // commands carrying the synthetic private-rect copy/depth shaders, the only
  // class for which the skipped callees register per-command edges
  // (RememberMaterializedPrivateRectTargetSourcesLocked requires the copy
  // shader; the depth-edge branch requires UsesDepthTarget, which requires
  // the depth shader). For everything else the skipped work is idempotent
  // re-resolution: Ensure* early-outs on an already-ready target without
  // registering anything, and readiness cannot regress while the validity
  // set below is unchanged (retire/recreate bumps the invalidation
  // generation; a creation matters only at a watched record-less requested
  // address; content uploads deliberately do NOT invalidate — they cannot
  // move the binding. See DirectClearFramebufferMemoEntry).
  const bool clear_memo_eligible =
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  uint64_t clear_memo_key = 0;
  if (clear_memo_eligible) {
    uint64_t key = 1469598103934665603ull;
    const auto mix = [&](uint64_t value) {
      key ^= value;
      key *= 1099511628211ull;
    };
    for (uint32_t render_target : command.resources.render_targets) {
      mix(render_target);
    }
    mix(command.resources.depth_stencil);
    mix(command.clear.flags);
    mix(command.draw.state_flush_lr);
    mix(command.draw.private_texture_rectangle ? 1u : 0u);
    // Extent inputs of the private-rect materialize path
    // (ResolvePrivateRectTargetExtent reads viewport/scissor): two clears
    // sharing targets but differing here may materialize differently, so
    // they must not share a memo entry.
    mix(command.viewport.valid ? 1u : 0u);
    mix(static_cast<uint64_t>(std::bit_cast<uint32_t>(command.viewport.width)));
    mix(static_cast<uint64_t>(
        std::bit_cast<uint32_t>(command.viewport.height)));
    mix(command.scissor.valid ? 1u : 0u);
    mix(static_cast<uint64_t>(static_cast<uint32_t>(command.scissor.left)));
    mix(static_cast<uint64_t>(static_cast<uint32_t>(command.scissor.top)));
    mix(static_cast<uint64_t>(static_cast<uint32_t>(command.scissor.right)));
    mix(static_cast<uint64_t>(static_cast<uint32_t>(command.scissor.bottom)));
    mix(command.draw.private_texture_corners_valid ? 1u : 0u);
    if (command.draw.private_texture_corners_valid) {
      for (float corner : command.draw.private_texture_corners) {
        mix(static_cast<uint64_t>(std::bit_cast<uint32_t>(corner)));
      }
    }
    mix(static_cast<uint64_t>(
        reinterpret_cast<uintptr_t>(output_framebuffer)));
    mix(static_cast<uint32_t>(output_depth_format));
    clear_memo_key = key != 0 ? key : 1ull;
  }
  DirectFramebufferBinding framebuffer;
  bool clear_memo_hit = false;
  if (clear_memo_key != 0) {
    const auto memo_it =
        g_direct_renderer.clear_framebuffer_memo.find(clear_memo_key);
    if (memo_it != g_direct_renderer.clear_framebuffer_memo.end()) {
      const DirectClearFramebufferMemoEntry& memo = memo_it->second;
      bool memo_valid = memo.framebuffer_invalidation_generation ==
                        g_plume_resources.framebuffer_invalidation_generation;
      // RUN-494 narrowing (see DirectClearFramebufferMemoEntry): global
      // creations/destroys churn voided the memo at transitions; only a
      // record appearing at a previously record-less requested address can
      // change this resolution.
      for (uint32_t i = 0; memo_valid && i < memo.record_less_count; ++i) {
        if (FindDirectResourceLocked(memo.record_less_addresses[i]) !=
            nullptr) {
          memo_valid = false;
        }
      }
      if (memo_valid) {
        framebuffer = memo.binding;
        clear_memo_hit = true;
        ++g_direct_draw_phases.record_clear_memo_hits;
      } else {
        g_direct_renderer.clear_framebuffer_memo.erase(memo_it);
      }
    }
  }
  if (!clear_memo_hit) {
    ++g_direct_draw_phases.record_clear_memo_runs;
    // clearsplit[ensure= resolve= sync=]: memo-miss attribution. sync counts
    // the failed-resolve retry INCLUDING its second resolve, so resolve= is
    // the first-attempt cost only.
    const FableDirectClock::time_point clear_ensure_t0 =
        FableDirectClock::now();
    EnsurePrivateRectSetupClearSurfacesLocked(device, command);
    const FableDirectClock::time_point clear_resolve_t0 =
        FableDirectClock::now();
    g_direct_draw_phases.clear_ensure_ms +=
        FableDirectElapsedMs(clear_ensure_t0, clear_resolve_t0);
    framebuffer = ResolveDirectFramebufferLocked(
        device, output_framebuffer, output_depth_format, command);
    const FableDirectClock::time_point clear_sync_t0 = FableDirectClock::now();
    g_direct_draw_phases.clear_resolve_ms +=
        FableDirectElapsedMs(clear_resolve_t0, clear_sync_t0);
    if (!framebuffer.framebuffer) {
      // RUN-491: this retry was 5.85s of the 6.07s clear cost — superseded
      // stale-frame clears reference dead recycled panel headers that can
      // NEVER resolve, and each such clear paid a full sync (~11ms × ~530).
      // Negative retry memo: a key that sync-retried and STILL failed cannot
      // succeed until the record-presence pattern over its framebuffer
      // interest set changes (see RUN-495 below — framebuffer invalidations
      // do not affect the retry's outcome, only the positive memo's).
      //
      // RUN-494: validity used to also require GLOBAL resource_creations
      // equality — which churns every frame at transitions (new panel
      // headers), so skip_retry never held and the attract burst re-paid the
      // full sync per stale clear (clearsplit sync=2,704 of clear=2,760 in a
      // 3,394ms record window; 1,704 of 1,711 on the next burst line). The
      // retry's outcome can only change when a record appears at one of the
      // stored record-less addresses (sync exists to materialize exactly
      // those) — so watch those addresses, not the global counter.
      //
      // RUN-495 (retry=32/0, zero skips every compose): the RUN-494 fix
      // swapped resource_creations for framebuffer_invalidation_generation —
      // which has the SAME per-compose churn: RetirePlumeResourceLocked bumps
      // it on EVERY texture/surface retirement (even with no live
      // framebuffer entry), and the per-sync destroyed pass + stale-eviction
      // sweep retire panel-header textures every menu compose. Every entry
      // was voided on lookup, erased, and re-stored. Framebuffer-side
      // invalidation matters for the POSITIVE memo above (its binding holds
      // views that retirement dangles); the negative memo gates only the
      // filtered sync retry, whose filter — interest minus record-having
      // addresses — is a pure function of record PRESENCE over the interest
      // set. So validity is exactly "the presence pattern is unchanged":
      // store every interest address with a had-record bit and invalidate on
      // a flip in EITHER direction (a record appearing at a watched
      // record-less address is the legitimate retry trigger; a record
      // vanishing changes the filter too). No global term at all.
      struct DirectClearFailedRetryWatch {
        std::array<uint32_t, 8> addresses = {};
        uint32_t address_count = 0;
        // Bit i set = addresses[i] had a record when the watch was stored
        // (post-sync). Bits pair with the array slots, so the interest set's
        // iteration order is irrelevant.
        uint8_t present_mask = 0;
      };
      static std::unordered_map<uint64_t, DirectClearFailedRetryWatch>
          clear_failed_retry_memo;
      // RUN-496: keyed on the INTEREST-ADDRESS SET, not the semantic clear
      // key — at the attract transition the panels animate, so viewport /
      // corner inputs make every stale clear's semantic key unique
      // (retry=240/0/0: 240 runs, zero skips, zero invalidations = pure key
      // churn) while the retry outcome depends ONLY on record presence at
      // the framebuffer interest addresses, which repeat across all of them.
      std::unordered_set<uint32_t> retry_interest;
      AddDirectFramebufferResourceInterest(command, retry_interest);
      uint64_t retry_watch_key = 0;
      {
        std::array<uint32_t, 8> sorted = {};
        uint32_t count = 0;
        for (const uint32_t address : retry_interest) {
          if (count >= sorted.size()) {
            break;
          }
          sorted[count++] = address;
        }
        std::sort(sorted.begin(), sorted.begin() + count);
        uint64_t key = 1469598103934665603ull;
        for (uint32_t i = 0; i < count; ++i) {
          key ^= sorted[i];
          key *= 1099511628211ull;
        }
        retry_watch_key = count != 0 ? key : 0;
      }
      bool skip_retry = false;
      if (retry_watch_key != 0) {
        const auto failed_it = clear_failed_retry_memo.find(retry_watch_key);
        if (failed_it != clear_failed_retry_memo.end()) {
          const DirectClearFailedRetryWatch& watch = failed_it->second;
          skip_retry = true;
          for (uint32_t i = 0; skip_retry && i < watch.address_count; ++i) {
            const bool present =
                FindDirectResourceLocked(watch.addresses[i]) != nullptr;
            if (present != (((watch.present_mask >> i) & 1u) != 0u)) {
              skip_retry = false;
            }
          }
          if (!skip_retry) {
            clear_failed_retry_memo.erase(failed_it);
            ++g_direct_draw_phases.clear_retry_invalidated;
          }
        }
      }
      if (skip_retry) {
        ++g_direct_draw_phases.clear_retry_skips;
      }
      if (!skip_retry) {
        // Reuse the interest set computed for the watch key above.
        std::unordered_set<uint32_t> resource_filter = retry_interest;
        // RUN-492: the retry's only legitimate job is materializing MISSING
        // records — targets that already have a record but cannot resolve a
        // framebuffer gain nothing from a re-sync (content updates arrive via
        // the changed stream), yet each paid ~11ms of snapshot machinery ×
        // ~830 clears at the deep menu (clearsplit sync=6,074 even with the
        // dead-address filter, which only catches record-LESS addresses).
        for (auto it = resource_filter.begin(); it != resource_filter.end();) {
          if (FindDirectResourceLocked(*it) != nullptr) {
            it = resource_filter.erase(it);
          } else {
            ++it;
          }
        }
        if (!resource_filter.empty()) {
          SyncDirectResourcesLocked(device, command_list, nullptr,
                                    &resource_filter);
          framebuffer = ResolveDirectFramebufferLocked(
              device, output_framebuffer, output_depth_format, command);
        }
        if (!framebuffer.framebuffer && retry_watch_key != 0) {
          if (clear_failed_retry_memo.size() >= 4096u) {
            clear_failed_retry_memo.clear();
          }
          DirectClearFailedRetryWatch watch;
          // Capture the presence pattern NOW (post-sync): an address whose
          // record materialized during the sync must be stored as PRESENT,
          // or the entry would self-invalidate on its first lookup and the
          // burst loop would return.
          for (const uint32_t address : retry_interest) {
            if (watch.address_count >= watch.addresses.size()) {
              break;
            }
            if (FindDirectResourceLocked(address) != nullptr) {
              watch.present_mask |=
                  static_cast<uint8_t>(1u << watch.address_count);
            }
            watch.addresses[watch.address_count++] = address;
          }
          clear_failed_retry_memo[retry_watch_key] = watch;
        }
        ++g_direct_draw_phases.clear_retry_runs;
      }
      g_direct_draw_phases.clear_sync_ms +=
          FableDirectElapsedMs(clear_sync_t0, FableDirectClock::now());
    }
    // Successful resolutions only — a failed resolve must retry (sync) on the
    // next clear, so negative results are never memoized. TTL/size: the memo
    // is frame-scoped (cleared at frame begin) and capped like its siblings.
    if (clear_memo_key != 0 && framebuffer.framebuffer) {
      if (g_direct_renderer.clear_framebuffer_memo.size() >= 4096u) {
        g_direct_renderer.clear_framebuffer_memo.clear();
      }
      DirectClearFramebufferMemoEntry memo;
      memo.binding = framebuffer;
      memo.framebuffer_invalidation_generation =
          g_plume_resources.framebuffer_invalidation_generation;
      const auto watch_if_record_less = [&](uint32_t address) {
        if (address == 0 || FindDirectResourceLocked(address) != nullptr ||
            memo.record_less_count >= memo.record_less_addresses.size()) {
          return;
        }
        memo.record_less_addresses[memo.record_less_count++] = address;
      };
      for (uint32_t render_target : command.resources.render_targets) {
        watch_if_record_less(render_target);
      }
      watch_if_record_less(command.resources.depth_stencil);
      g_direct_renderer.clear_framebuffer_memo[clear_memo_key] = memo;
    }
  }
  if (!framebuffer.framebuffer) {
    return;
  }
  // clearsplit vk=: transition + setFramebuffer + the clearColor/DepthStencil
  // loop — the residual per-clear cost paid on memo hits too.
  const FableDirectClock::time_point clear_vk_t0 = FableDirectClock::now();
  TransitionDirectFramebufferForWrite(command_list, framebuffer);
  command_list->setFramebuffer(framebuffer.framebuffer);

  plume::RenderRect rect;
  const plume::RenderRect* rect_ptr = nullptr;
  if (command.clear.rect.valid) {
    const int32_t left = std::clamp(command.clear.rect.left, int32_t(0),
                                    static_cast<int32_t>(framebuffer.width));
    const int32_t top = std::clamp(command.clear.rect.top, int32_t(0),
                                   static_cast<int32_t>(framebuffer.height));
    const int32_t right = std::clamp(command.clear.rect.right, int32_t(0),
                                     static_cast<int32_t>(framebuffer.width));
    const int32_t bottom = std::clamp(command.clear.rect.bottom, int32_t(0),
                                      static_cast<int32_t>(framebuffer.height));
    if (right > left && bottom > top) {
      rect = plume::RenderRect(left, top, right, bottom);
      rect_ptr = &rect;
    }
  }
  plume::RenderRect guest_scaled_rect;
  bool guest_rect_scaled = false;
  const auto scaled_guest_rect = [&](const bool title_scene_target)
      -> const plume::RenderRect* {
    guest_rect_scaled = false;
    // Guest clear rects arrive in the guest space of THIS ATTACHMENT, so the
    // denominator is the attachment's own guest extent — the same
    // `framebuffer.guest_*` (promoted_from_guest_*) the ONE SCALER uses for
    // viewport and scissor. It used to divide by the global display extent
    // (qword_834A5FE8) instead, so a clear into a half- or quarter-res scene
    // target, or into the 1120x720 view family, got a different factor than
    // the draws into that same target. Fall back to the display extent only
    // when the attachment carries no guest extent (i.e. it was never
    // promoted, where the factor is 1 anyway).
    const uint32_t guest_w = framebuffer.guest_width != 0
                                 ? framebuffer.guest_width
                                 : FableGuestDisplayWidth();
    const uint32_t guest_h = framebuffer.guest_height != 0
                                 ? framebuffer.guest_height
                                 : FableGuestDisplayHeight();
    if (!command.clear.rect.valid || framebuffer.width == 0 ||
        framebuffer.height == 0 ||
        (framebuffer.width == guest_w && framebuffer.height == guest_h)) {
      return rect_ptr;
    }
    const RendererScissorSnapshot& source = command.clear.rect;
    if (source.left < 0 || source.top < 0 || source.right < source.left ||
        source.bottom < source.top ||
        source.right > static_cast<int32_t>(guest_w) ||
        source.bottom > static_cast<int32_t>(guest_h)) {
      return rect_ptr;
    }
    const bool guest_aspect_target =
        uint64_t(framebuffer.width) * uint64_t(guest_h) ==
        uint64_t(framebuffer.height) * uint64_t(guest_w);
    if (!title_scene_target && !guest_aspect_target) {
      return rect_ptr;
    }

    const auto scale_x = [&](int32_t value) {
      return static_cast<int32_t>(
          std::lround(static_cast<double>(value) *
                      static_cast<double>(framebuffer.width) /
                      static_cast<double>(guest_w)));
    };
    const auto scale_y = [&](int32_t value) {
      return static_cast<int32_t>(
          std::lround(static_cast<double>(value) *
                      static_cast<double>(framebuffer.height) /
                      static_cast<double>(guest_h)));
    };
    guest_scaled_rect = plume::RenderRect(
        std::clamp(scale_x(source.left), 0,
                   static_cast<int32_t>(framebuffer.width)),
        std::clamp(scale_y(source.top), 0,
                   static_cast<int32_t>(framebuffer.height)),
        std::clamp(scale_x(source.right), 0,
                   static_cast<int32_t>(framebuffer.width)),
        std::clamp(scale_y(source.bottom), 0,
                   static_cast<int32_t>(framebuffer.height)));
    guest_rect_scaled = true;
    return &guest_scaled_rect;
  };
  for (uint32_t slot = 0; slot < framebuffer.color_count; ++slot) {
    if ((command.clear.flags & (1u << slot)) == 0) {
      continue;
    }
    const bool title_scene_target =
        slot < framebuffer.color_addresses.size() &&
        IsFableTitleSceneTargetLocked(framebuffer.color_addresses[slot]);
    const plume::RenderRect* color_rect_ptr =
        scaled_guest_rect(title_scene_target);
    command_list->clearColor(
        slot,
        plume::RenderColor(command.clear.color[0], command.clear.color[1],
                           command.clear.color[2], command.clear.color[3]),
        color_rect_ptr, color_rect_ptr ? 1u : 0u);
    MarkDirectResourceGpuProducedContentLocked(
        framebuffer.color_addresses[slot], ++g_direct_renderer.scene_write_serial,
        true);
    if (title_scene_target) {
      g_direct_renderer.initialized_title_scene_targets_this_frame.insert(
          framebuffer.color_addresses[slot]);
    }
    if (title_scene_target &&
        g_direct_renderer.diagnostics.logged_title_scene_clears < 8u) {
      REXLOG_INFO(
          "Refii plume title scene clear #{} frame={} serial={} slot={} "
          "target=0x{:08X}/{}x{} flags=0x{:08X} color={:.4f},{:.4f},"
          "{:.4f},{:.4f} rect={} {} {} {} {} scaled={} effective={} {} {} {}",
          ++g_direct_renderer.diagnostics.logged_title_scene_clears,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial), slot,
          framebuffer.color_addresses[slot], framebuffer.width,
          framebuffer.height, command.clear.flags, command.clear.color[0],
          command.clear.color[1], command.clear.color[2],
          command.clear.color[3], command.clear.rect.valid ? 1 : 0,
          command.clear.rect.left, command.clear.rect.top,
          command.clear.rect.right, command.clear.rect.bottom,
          guest_rect_scaled ? 1 : 0,
          color_rect_ptr ? color_rect_ptr->left : -1,
          color_rect_ptr ? color_rect_ptr->top : -1,
          color_rect_ptr ? color_rect_ptr->right : -1,
          color_rect_ptr ? color_rect_ptr->bottom : -1);
    }
    InvalidateRememberedSceneTargetForClearLocked(
        framebuffer.color_addresses[slot], framebuffer.color_textures[slot],
        framebuffer.width, framebuffer.height);
  }
  if ((command.clear.flags & (kFableClearDepth | kFableClearStencil)) != 0 &&
      framebuffer.depth_texture != nullptr) {
    command_list->clearDepthStencil(
        (command.clear.flags & kFableClearDepth) != 0,
        (command.clear.flags & kFableClearStencil) != 0,
        command.clear.depth, command.clear.stencil, rect_ptr,
        rect_ptr ? 1u : 0u);
    MarkDirectResourceGpuProducedContentLocked(
        framebuffer.depth_address, ++g_direct_renderer.scene_write_serial, true);
  }
  g_direct_draw_phases.clear_vk_ms +=
      FableDirectElapsedMs(clear_vk_t0, FableDirectClock::now());
}

void LogFailedDirectDrawLocked(const char* reason,
                               const FableDirectRenderCommand& command) {
  g_direct_renderer.last_failed_direct_draw_reason =
      reason ? reason : "unknown";
  g_direct_renderer.last_failed_direct_draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  if (g_direct_renderer.diagnostics.logged_failed_direct_draws >=
      kMaxLoggedFailedDirectDraws) {
    return;
  }
  ++g_direct_renderer.diagnostics.logged_failed_direct_draws;
  const RendererStreamBindingSnapshot& stream0 = command.resources.streams[0];
  const FableDirectInlineBuffer& inline_vb0 = command.inline_vertex_buffers[0];
  const bool vb0_inline =
      inline_vb0.ready() &&
      DirectInlineBufferOffsetForCommandAddress(command, inline_vb0,
                                                stream0.buffer) !=
          UINT32_MAX;
  const PlumeTrackedResource* tracked_vb0 =
      stream0.buffer != 0 ? FindDirectResourceLocked(stream0.buffer) : nullptr;
  const bool vb0_tracked =
      tracked_vb0 != nullptr && PlumeGuestBufferReady(*tracked_vb0);
  const FableDirectInlineBuffer& inline_ib = command.inline_index_buffer;
  const bool ib_inline =
      inline_ib.ready() &&
      DirectInlineBufferOffsetForCommandAddress(command, inline_ib,
                                                command.resources.index_buffer) !=
          UINT32_MAX;
  const PlumeTrackedResource* tracked_ib =
      command.resources.index_buffer != 0
          ? FindDirectResourceLocked(command.resources.index_buffer)
          : nullptr;
  const bool ib_tracked =
      tracked_ib != nullptr && PlumeGuestBufferReady(*tracked_ib);
  REXLOG_WARN(
      "Refii plume exact direct draw reject reason={} serial={} source={} type={} "
      "prim={} base={} start={} count={} indexed={} rt=0x{:08X} "
      "depth=0x{:08X} vb0=0x{:08X}/{} ib=0x{:08X} decl=0x{:08X} "
      "vs=0x{:08X} ps=0x{:08X} private_rect={} "
      "wrapper=0x{:08X} call={} context=0x{:016X} "
      "vb0_inline={}/{} vb0_tracked={}/{} "
      "ib_inline={}/{} ib_tracked={}/{}",
      reason ? reason : "unknown", command.draw_serial,
      static_cast<uint32_t>(command.draw.source),
      static_cast<uint32_t>(command.type), command.draw.draw.primitive_type,
      command.draw.draw.base_vertex, command.draw.draw.start,
      command.draw.draw.primitive_count, command.draw.draw.indexed,
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.resources.streams[0].buffer, command.resources.streams[0].stride,
      command.resources.index_buffer, command.resources.vertex_declaration,
      command.resources.vertex_shader, command.resources.pixel_shader,
      command.draw.private_texture_rectangle,
      command.draw.private_texture_wrapper_symbol_address,
      static_cast<unsigned long long>(
          command.draw.private_texture_wrapper_call_serial),
      static_cast<unsigned long long>(
          command.draw.private_texture_wrapper_context_key),
      vb0_inline ? 1u : 0u, inline_vb0.ready() ? inline_vb0.byte_size : 0u,
      vb0_tracked ? 1u : 0u,
      tracked_vb0 ? PlumeGuestBufferByteSize(*tracked_vb0) : 0u,
      ib_inline ? 1u : 0u, inline_ib.ready() ? inline_ib.byte_size : 0u,
      ib_tracked ? 1u : 0u,
      tracked_ib ? PlumeGuestBufferByteSize(*tracked_ib) : 0u);
}

void LogPixVideoTextureBindingLocked(const FableDirectRenderCommand& command,
                                     uint32_t slot) {
  PlumeTrackedResource* texture =
      ResolveDirectSampledTextureLocked(command.resources.textures[slot]);
  if (!texture) {
    REXLOG_INFO(
        "Refii plume PixVideo binding #{} slot={} guest=0x{:08X} missing",
        g_direct_renderer.diagnostics.logged_pix_video_draw_bindings + 1u, slot,
        command.resources.textures[slot]);
    return;
  }
  const auto& fetch = PlumeGuestTextureFetchDwords(*texture);
  REXLOG_INFO(
      "Refii plume PixVideo binding #{} slot={} guest=0x{:08X} "
      "tracked=0x{:08X} ready={} fmt={} size={}x{} upload_gen={} "
      "upload_size={} upload_source={} decoded={} hash=0x{:016X} "
      "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
      g_direct_renderer.diagnostics.logged_pix_video_draw_bindings + 1u, slot,
      command.resources.textures[slot], texture->guest_address,
      PlumeGuestImageReady(*texture) ? 1 : 0,
      static_cast<uint32_t>(PlumeGuestTextureFormat(*texture)),
      PlumeGuestTextureWidth(*texture), PlumeGuestTextureHeight(*texture),
      static_cast<unsigned long long>(texture->upload_generation),
      static_cast<unsigned long long>(texture->texture_upload_size),
      texture->texture_upload_has_source ? 1 : 0,
      texture->texture_upload_decoded ? 1 : 0,
      static_cast<unsigned long long>(texture->texture_upload_hash), fetch[0],
      fetch[1], fetch[2], fetch[3], fetch[4], fetch[5]);
}

bool DirectPixVideoPlaneSetReadyLocked(const FableDirectRenderCommand& command,
                                       uint32_t* missing_slot,
                                       uint32_t* missing_guest) {
  FablePixVideoPlaneReadinessFacts facts;
  facts.pix_video_draw =
      command.draw.source == RendererDrawEventSource::kPixVideo;
  if (!facts.pix_video_draw) {
    return FablePixVideoPlaneSetReady(facts, missing_slot, missing_guest);
  }
  for (uint32_t slot = 0; slot < kFablePixVideoPlaneCount; ++slot) {
    const uint32_t guest = command.resources.textures[slot];
    facts.guests[slot] = guest;
    PlumeTrackedResource* texture =
        ResolveDirectSampledTextureForCommandLocked(command, slot);
    facts.ready[slot] = texture != nullptr && PlumeGuestImageReady(*texture);
  }
  return FablePixVideoPlaneSetReady(facts, missing_slot, missing_guest);
}

bool DirectBinkWrapperPlaneSetReadyLocked(
    const FableDirectRenderCommand& command, uint32_t* missing_slot,
    uint32_t* missing_guest) {
  FableVideoBinkWrapperPlaneSetReadinessFacts facts;
  facts.wrapper_candidate =
      DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(command);
  if (!facts.wrapper_candidate) {
    return FableVideoBinkWrapperPlaneSetReady(facts, missing_slot,
                                              missing_guest);
  }

  std::array<uint32_t, kFableBinkVideoPlaneCount> ready_planes = {};
  facts.command_plane_set_ready =
      DirectBinkWrapperCommandPlaneSetReadyLocked(command, ready_planes);
  if (!facts.command_plane_set_ready) {
    for (uint32_t slot = 0; slot < kFableBinkVideoPlaneCount; ++slot) {
      const uint32_t guest = command.resources.textures[slot];
      facts.guests[slot] = guest;
      facts.fetch_decoded[slot] =
          FableVideoCommandSlotUsesFetchDecodedBinkWrapperPlane(command,
                                                                       slot);
      if (!facts.fetch_decoded[slot]) {
        break;
      }
      PlumeTrackedResource* texture =
          FindDirectExactBinkPlaneResourceForSlotLocked(command, slot);
      facts.resource_image_ready[slot] =
          guest != 0 && texture != nullptr && PlumeGuestImageReady(*texture);
      if (!facts.resource_image_ready[slot]) {
        break;
      }
      ready_planes[slot] = texture->guest_address;
      if (slot + 1u == kFableBinkVideoPlaneCount) {
        facts.plane_address_set_ready =
            DirectBinkWrapperPlaneAddressSetReadyLocked(ready_planes);
      }
    }
  }
  return FableVideoBinkWrapperPlaneSetReady(facts, missing_slot,
                                            missing_guest);
}

bool SyncPixVideoInlinePlanesForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command) {
  if (command.draw.source != RendererDrawEventSource::kPixVideo) {
    return true;
  }

  for (const RendererInlineTextureUploadSnapshot& upload :
       command.pix_video_planes) {
    if (!upload.ready()) {
      return false;
    }
  }

  bool synced_any = false;
  for (const RendererInlineTextureUploadSnapshot& upload :
       command.pix_video_planes) {
    FableGuestResourceSnapshot snapshot =
        FablePixVideoBuildInlineUploadResourceSnapshot(upload);

    const bool inserted =
        g_direct_renderer.resources.find(snapshot.guest_address) ==
        g_direct_renderer.resources.end();
    if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
      ++g_direct_renderer.failed_resource_syncs;
      LogDirectResourceSyncFailureLocked(snapshot, "pix_video_inline_plane");
      return false;
    }
    if (inserted) {
      ++g_direct_renderer.resource_creations;
    }
    g_direct_renderer.known_uploads[snapshot.guest_address] =
        UploadCacheEntryForSnapshot(snapshot);
    synced_any = true;
  }

  if (synced_any) {
    RecordPendingTextureUploadsLocked(command_list);
  }
  RecordPendingSurfaceContentCarriesLocked(command_list);
  return synced_any;
}

void SyncPixVideoPlanesForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command) {
  if (command.draw.source != RendererDrawEventSource::kPixVideo) {
    return;
  }

  if (SyncPixVideoInlinePlanesForDrawLocked(device, command_list, command)) {
    return;
  }

  std::unordered_set<uint32_t> resource_filter;
  std::unordered_set<uint32_t> texture_upload_filter;
  const FablePixVideoPlaneTextureInterests plane_interests =
      FablePixVideoCollectPlaneTextureInterests(command.resources.textures);
  for (uint32_t index = 0; index < plane_interests.count; ++index) {
    const uint32_t plane = plane_interests.textures[index];
    AddDirectResourceInterest(resource_filter, plane);
    AddDirectResourceInterest(texture_upload_filter, plane);
  }
  if (resource_filter.empty()) {
    return;
  }

  SyncDirectResourcesLocked(device, command_list, &texture_upload_filter,
                            &resource_filter, true);
}

void SyncBinkWrapperPlanesForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command, bool wait_for_observer) {
  if (!DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(command)) {
    return;
  }

  std::unordered_set<uint32_t> resource_filter;
  std::unordered_set<uint32_t> texture_upload_filter;
  for (uint32_t slot = 0; slot < 3u; ++slot) {
    AddDirectExactBinkWrapperPlaneResourceInterestsLocked(
        resource_filter, texture_upload_filter, command, slot);
  }
  if (resource_filter.empty() && texture_upload_filter.empty()) {
    return;
  }

  SyncDirectResourcesLocked(device, command_list, &texture_upload_filter,
                            &resource_filter, wait_for_observer);
}

bool EnsurePixVideoPlanesReadyForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    FableDirectRenderCommand& command) {
  SyncPixVideoPlanesForDrawLocked(device, command_list, command);

  uint32_t missing_slot = 0;
  uint32_t missing_guest = 0;
  if (!DirectPixVideoPlaneSetReadyLocked(command, &missing_slot,
                                         &missing_guest)) {
    SyncPixVideoPlanesForDrawLocked(device, command_list, command);
  }
  if (DirectPixVideoPlaneSetReadyLocked(command, &missing_slot,
                                        &missing_guest)) {
    return true;
  }

  if (g_direct_renderer.diagnostics.logged_skipped_pix_video_missing_planes < 32u) {
    REXLOG_INFO(
        "Refii plume PixVideo draw held #{} frame={} serial={} "
        "missing_slot={} guest=0x{:08X} rt=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_skipped_pix_video_missing_planes,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial), missing_slot,
        missing_guest, command.resources.render_targets[0]);
  }
  return false;
}

bool EnsureBinkWrapperPlanesReadyForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    FableDirectRenderCommand& command) {
  uint32_t missing_slot = 0;
  uint32_t missing_guest = 0;
  bool planes_ready = true;
  if (DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(command)) {
    planes_ready = DirectBinkWrapperPlaneSetReadyLocked(
        command, &missing_slot, &missing_guest);
    if (!planes_ready) {
      SyncBinkWrapperPlanesForDrawLocked(device, command_list, command, false);
      planes_ready = DirectBinkWrapperPlaneSetReadyLocked(
          command, &missing_slot, &missing_guest);
    }
  }
  if (!planes_ready) {
    SyncBinkWrapperPlanesForDrawLocked(device, command_list, command);
    planes_ready = DirectBinkWrapperPlaneSetReadyLocked(
        command, &missing_slot, &missing_guest);
  }
  if (planes_ready) {
    return true;
  }

  if (g_direct_renderer.diagnostics.logged_skipped_bink_wrapper_missing_planes < 32u) {
    REXLOG_INFO(
        "Refii plume exact Bink wrapper draw held #{} frame={} serial={} "
        "missing_slot={} guest=0x{:08X} rt=0x{:08X}",
        ++g_direct_renderer.diagnostics.logged_skipped_bink_wrapper_missing_planes,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial), missing_slot,
        missing_guest, command.resources.render_targets[0]);
  }
  return false;
}

// FindExactTextureFetchBindingForResourceLocked +
// RegisterExactTextureFetchResourceLinkForPrivateRectLocked deleted
// 2026-07-18 (migration step 3): the binding-action format-match was
// census-dead (runs 538/539), leaving the register a constant no-op — the
// whole texture-fetch-resource-link family lost its only writer with it.

// Family-3 forensics (2026-07-20): the Press-A glyph renders as a gray
// Scaleform image placeholder; frame captures keep missing the well-compose
// frames (recompose is event-driven, idle frames carry no GUI draws). This
// names every draw-time binding of a TINY texture (<=64px) in the log
// instead — identity-map lookup only (FindDirectResourceLocked), NOT the
// sampling resolver, so the step-3 binding-authority census counters stay
// unskewed. Bounded; delete with the family-3 fix.
void LogSmallGuiTextureBindingsLocked(const FableDirectRenderCommand& command) {
  static uint32_t logged_small_texture_bindings = 0;
  // One line per DISTINCT texture object: the 1x1 neutral texture fills
  // every unused slot of early draws and burned the whole cap in one draw
  // (run 038) — dedupe keeps the cap for real identities.
  static std::unordered_set<uint32_t> logged_small_texture_objects;
  if (logged_small_texture_bindings >= 96u) {
    return;
  }
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    const uint32_t guest = command.resources.textures[slot];
    if (guest == 0) {
      continue;
    }
    const PlumeTrackedResource* texture = FindDirectResourceLocked(guest);
    if (!texture) {
      continue;
    }
    const uint32_t width = PlumeGuestTextureWidth(*texture);
    const uint32_t height = PlumeGuestTextureHeight(*texture);
    if (width == 0 || height == 0 || width > 64u || height > 64u) {
      continue;
    }
    if (!logged_small_texture_objects.insert(guest).second) {
      continue;
    }
    if (logged_small_texture_bindings >= 96u) {
      return;
    }
    REXLOG_INFO(
        "Refii plume small texture binding #{} slot={} guest=0x{:08X} "
        "tracked=0x{:08X} {}x{} fmt={} guest_fmt={} ready={} upload_gen={} "
        "upload_size={} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_small_texture_bindings, slot, guest, texture->guest_address,
        width, height,
        static_cast<uint32_t>(PlumeGuestTextureFormat(*texture)),
        static_cast<uint32_t>(PlumeGuestTextureGuestFormat(*texture)),
        PlumeGuestImageReady(*texture) ? 1 : 0,
        static_cast<unsigned long long>(texture->upload_generation),
        static_cast<unsigned long long>(texture->texture_upload_size),
        command.resources.render_targets[0], command.resources.vertex_shader,
        command.resources.pixel_shader);
  }
}

void LogPrivateRectTextureBindingLocked(const FableDirectRenderCommand& command) {
  const bool copy_shader =
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  const uint32_t log_index =
      copy_shader ? g_direct_renderer.diagnostics.logged_private_rect_copy_draw_bindings + 1u
                  : g_direct_renderer.diagnostics.logged_private_rect_video_draw_bindings + 1u;
  const uint32_t slot =
      copy_shader ? source_slot
                  : (command.draw.private_texture_slot &
                     (kMaxFableTextureSlots - 1u));
  PlumeTrackedResource* texture =
      ResolveDirectSampledTextureForCommandLocked(command, slot);
  if (!texture) {
    if (!RendererDeepObserverHooksEnabled()) {
      return;
    }
    const uint32_t guest = command.resources.textures[slot];
    const PlumeTrackedResource* producer_texture =
        guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
    if (DirectCommandTextureSlotExactProducerProvidesSampleLocked(
            command, slot, producer_texture)) {
      REXLOG_INFO(
          "Refii plume private rect binding #{} slot={} guest=0x{:08X} "
          "producer-owned tracked=0x{:08X}/{}x{} rt=0x{:08X} size={}x{} "
          "vs=0x{:08X} ps=0x{:08X}",
          log_index, slot, guest,
          producer_texture ? producer_texture->guest_address : 0u,
          producer_texture ? PlumeGuestTextureWidth(*producer_texture) : 0u,
          producer_texture ? PlumeGuestTextureHeight(*producer_texture) : 0u,
          command.resources.render_targets[0],
          command.draw.private_texture_corners_valid
              ? uint32_t(std::fabs(command.draw.private_texture_corners[2] -
                                    command.draw.private_texture_corners[0]))
              : 0u,
          command.draw.private_texture_corners_valid
              ? uint32_t(std::fabs(command.draw.private_texture_corners[5] -
                                    command.draw.private_texture_corners[1]))
              : 0u,
          command.resources.vertex_shader, command.resources.pixel_shader);
      return;
    }
    REXLOG_INFO(
        "Refii plume private rect binding #{} slot={} guest=0x{:08X} missing "
        "rt=0x{:08X} size={}x{} vs=0x{:08X} ps=0x{:08X}",
        log_index, slot,
        guest, command.resources.render_targets[0],
        command.draw.private_texture_corners_valid
            ? uint32_t(std::fabs(command.draw.private_texture_corners[2] -
                                  command.draw.private_texture_corners[0]))
            : 0u,
        command.draw.private_texture_corners_valid
            ? uint32_t(std::fabs(command.draw.private_texture_corners[5] -
                                  command.draw.private_texture_corners[1]))
            : 0u,
        command.resources.vertex_shader, command.resources.pixel_shader);
    return;
  }
  const PlumeTrackedResource* direct_texture =
      FindDirectResourceLocked(command.resources.textures[slot]);
  const bool aliased_source =
      direct_texture != nullptr && direct_texture != texture;
  const auto& fetch = PlumeGuestTextureFetchDwords(*texture);
  REXLOG_INFO(
      "Refii plume private rect binding #{} slot={} guest=0x{:08X} "
      "tracked=0x{:08X} source={} ready={} fmt={} guest_fmt={} size={}x{} "
      "upload_gen={} upload_size={} upload_source={} decoded={} "
      "hash=0x{:016X} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
      "uv={} {:.4f}/{:.4f}->{:.4f}/{:.4f} "
      "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
      log_index, slot,
      command.resources.textures[slot], texture->guest_address,
      aliased_source ? "alias" : "direct", PlumeGuestImageReady(*texture) ? 1 : 0,
      static_cast<uint32_t>(PlumeGuestTextureFormat(*texture)),
      static_cast<uint32_t>(PlumeGuestTextureGuestFormat(*texture)),
      PlumeGuestTextureWidth(*texture), PlumeGuestTextureHeight(*texture),
      static_cast<unsigned long long>(texture->upload_generation),
      static_cast<unsigned long long>(texture->texture_upload_size),
      texture->texture_upload_has_source ? 1 : 0,
      texture->texture_upload_decoded ? 1 : 0,
      static_cast<unsigned long long>(texture->texture_upload_hash),
      command.resources.render_targets[0], command.resources.vertex_shader,
      command.resources.pixel_shader, command.draw.private_texture_uv_override ? 1 : 0,
      command.draw.private_texture_u0, command.draw.private_texture_v0,
      command.draw.private_texture_u1, command.draw.private_texture_v1,
      fetch[0], fetch[1], fetch[2], fetch[3], fetch[4], fetch[5]);

  if (direct_texture == texture && !texture->texture_render_target &&
      !texture->texture_depth_target && texture->texture_upload_size == 0 &&
      !texture->texture_upload_has_source &&
      g_direct_renderer.diagnostics.logged_private_rect_unbacked_samples < 16u) {
    const PlumeTrackedResource* render_target =
        FindDirectResourceLocked(command.resources.render_targets[0]);
    REXLOG_INFO(
        "Refii plume private rect unbacked sample #{} guest=0x{:08X} "
        "tracked=0x{:08X} parent=0x{:08X} base=0x{:08X} mip=0x{:08X} "
        "pitch={} gfmt={} dim={} size={}x{} rt=0x{:08X} rt_tracked=0x{:08X} "
        "rt_parent=0x{:08X} rt_size={}x{} rt_fmt={} ps=0x{:08X}",
        g_direct_renderer.diagnostics.logged_private_rect_unbacked_samples + 1u,
        command.resources.textures[slot], texture->guest_address,
        texture->parent_address, PlumeGuestTextureBaseAddress(*texture),
        PlumeGuestTextureMipAddress(*texture), PlumeGuestTexturePitch(*texture),
        PlumeGuestTextureGuestFormat(*texture),
        PlumeGuestTextureDimension(*texture), PlumeGuestTextureWidth(*texture),
        PlumeGuestTextureHeight(*texture), command.resources.render_targets[0],
        render_target ? render_target->guest_address : 0u,
        render_target ? render_target->parent_address : 0u,
        render_target ? PlumeGuestTextureWidth(*render_target) : 0u,
        render_target ? PlumeGuestTextureHeight(*render_target) : 0u,
        render_target ? static_cast<uint32_t>(
                            PlumeGuestTextureFormat(*render_target))
                      : 0u,
        command.resources.pixel_shader);

    ++g_direct_renderer.diagnostics.logged_private_rect_unbacked_samples;
  }

  const bool title_or_private_target =
      command.draw.private_texture_rectangle ||
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]);
  if (direct_texture == texture && title_or_private_target &&
      !texture->texture_render_target && !texture->texture_depth_target &&
      texture->texture_upload_has_source && texture->texture_upload_size != 0 &&
      g_direct_renderer.diagnostics.logged_private_rect_blank_upload_samples < 24u) {
    const uint32_t sample_width = PlumeGuestTextureWidth(*texture);
    const uint32_t sample_height = PlumeGuestTextureHeight(*texture);
    const uint32_t sample_base = PlumeGuestTextureBaseAddress(*texture);
    const PlumeTrackedResource* render_target =
        FindDirectResourceLocked(command.resources.render_targets[0]);
    REXLOG_INFO(
        "Refii plume private/title direct upload sample #{} guest=0x{:08X} "
        "tracked=0x{:08X} parent=0x{:08X} base=0x{:08X} size={}x{} "
        "fmt={} guest_fmt={} upload_gen={} upload_size={} hash=0x{:016X} "
        "content_serial={} rt=0x{:08X} rt_tracked=0x{:08X} rt_parent=0x{:08X} "
        "rt_size={}x{} rt_serial={} ps=0x{:08X}",
        g_direct_renderer.diagnostics.logged_private_rect_blank_upload_samples + 1u,
        command.resources.textures[slot], texture->guest_address,
        texture->parent_address, sample_base, sample_width, sample_height,
        static_cast<uint32_t>(PlumeGuestTextureFormat(*texture)),
        PlumeGuestTextureGuestFormat(*texture),
        static_cast<unsigned long long>(texture->upload_generation),
        static_cast<unsigned long long>(texture->texture_upload_size),
        static_cast<unsigned long long>(texture->texture_upload_hash),
        static_cast<unsigned long long>(texture->content_serial),
        command.resources.render_targets[0],
        render_target ? render_target->guest_address : 0u,
        render_target ? render_target->parent_address : 0u,
        render_target ? PlumeGuestTextureWidth(*render_target) : 0u,
        render_target ? PlumeGuestTextureHeight(*render_target) : 0u,
        render_target
            ? static_cast<unsigned long long>(render_target->content_serial)
            : 0ull,
        command.resources.pixel_shader);

    ++g_direct_renderer.diagnostics.logged_private_rect_blank_upload_samples;
  }
}


DirectTextureDebugBinding DirectTextureDebugBindingForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  DirectTextureDebugBinding binding;
  binding.slot = slot;
  if (slot >= command.resources.textures.size()) {
    return binding;
  }
  binding.guest = command.resources.textures[slot];
  if (binding.guest == 0) {
    return binding;
  }
  PlumeTrackedResource* texture =
      ResolveDirectSampledTextureForCommandLocked(command, slot);
  binding.ready = texture && PlumeGuestImageReady(*texture);
  if (!texture) {
    return binding;
  }
  const PlumeTrackedResource* direct = FindDirectResourceLocked(binding.guest);
  binding.tracked = texture->guest_address;
  binding.parent = texture->parent_address;
  binding.base = PlumeGuestTextureBaseAddress(*texture);
  binding.mip = PlumeGuestTextureMipAddress(*texture);
  binding.pitch = PlumeGuestTexturePitch(*texture);
  binding.width = PlumeGuestTextureWidth(*texture);
  binding.height = PlumeGuestTextureHeight(*texture);
  binding.format = static_cast<uint32_t>(PlumeGuestTextureFormat(*texture));
  binding.guest_format = PlumeGuestTextureGuestFormat(*texture);
  binding.dimension = PlumeGuestTextureDimension(*texture);
  binding.content_serial = texture->content_serial;
  binding.generation = texture->upload_generation;
  binding.upload_size = texture->texture_upload_size;
  binding.hash = texture->texture_upload_hash;
  binding.upload_source = texture->texture_upload_has_source;
  binding.upload_decoded = texture->texture_upload_decoded;
  binding.first_nonzero = texture->debug_first_nonzero_offset;
  binding.sample_size = std::min<uint32_t>(
      static_cast<uint32_t>(binding.sample.size()),
      texture->debug_first_nonzero_sample_size);
  if (binding.sample_size != 0) {
    std::memcpy(binding.sample.data(), texture->debug_first_nonzero_sample.data(),
                binding.sample_size);
  }
  binding.aliased = direct != nullptr && direct != texture;
  return binding;
}

DirectTextureDebugBinding FirstDirectTextureDebugBindingLocked(
    const FableDirectRenderCommand& command) {
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (command.resources.textures[slot] != 0) {
      return DirectTextureDebugBindingForSlotLocked(command, slot);
    }
  }
  return {};
}

FableTitleTextureBindingFacts TitleTextureBindingFacts(
    const DirectTextureDebugBinding& binding) {
  FableTitleTextureBindingFacts facts;
  facts.guest = binding.guest;
  facts.tracked = binding.tracked;
  facts.base = binding.base;
  facts.scene_scaler_texture =
      binding.guest == kFableTitleSceneScalerTexture ||
      binding.guest == kFableTitleSceneScalerDepthTexture;
  facts.private_texture_proxy =
      IsFableTitlePrivateTextureProxyAddress(binding.guest);
  return facts;
}

FableTitleRenderPass ClassifyFableTitleRenderPassLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const DirectTextureDebugBinding& tex_first,
    const DirectTextureDebugBinding& tex13) {
  FableTitleRenderPassFacts facts;
  facts.synthetic_private_rect_copy =
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader);
  facts.vertex_hash =
      DirectVertexShaderHashLocked(command.resources.vertex_shader);
  facts.pixel_hash = DirectPixelShaderHashLocked(command.resources.pixel_shader);
  facts.gui_text_declaration =
      IsFableTitleGuiQuadDeclaration(command.resources.vertex_declaration);
  facts.title_indexed_declaration = DirectVertexDeclarationMatchesHashLocked(
      command.resources.vertex_declaration, kFableTitleIndexedDeclarationHash);
  facts.gui_text_3d_pixel_shader_address =
      IsFableTitleGuiText3DPixelShaderAddress(command.resources.pixel_shader);
  facts.source_slot_gui_text_atlas =
      IsFableTitleGuiTextAtlasCommandLocked(command, source_slot);
  facts.tone_map_draw = IsFableTitleToneMapDrawLocked(command);
  facts.title_scene_draw_target =
      IsFableTitleSceneDrawTargetLocked(command, framebuffer);
  facts.first_texture = TitleTextureBindingFacts(tex_first);
  facts.source_texture = TitleTextureBindingFacts(tex13);
  return FableTitleRenderPassForCommand(command, facts);
}

void EnsureTitleSceneTargetInitializedForDrawLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer) {
  if (!command_list || framebuffer.color_count == 0 ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return;
  }

  const DirectTextureDebugBinding tex_first =
      FirstDirectTextureDebugBindingLocked(command);
  const DirectTextureDebugBinding tex13 =
      DirectTextureDebugBindingForSlotLocked(command, source_slot);
  const FableTitleRenderPass title_pass =
      ClassifyFableTitleRenderPassLocked(command, framebuffer, tex_first, tex13);
  if (!FableTitleRenderPassNeedsInitializedTarget(title_pass) &&
      !DirectCommandIsTitleVisibleProofCandidateLocked(command)) {
    return;
  }

  for (uint32_t slot = 0; slot < framebuffer.color_count; ++slot) {
    if (slot >= framebuffer.color_addresses.size()) {
      continue;
    }
    const uint32_t target = framebuffer.color_addresses[slot];
    const uint32_t command_rt =
        slot < command.resources.render_targets.size()
            ? command.resources.render_targets[slot]
            : 0u;
    if (!FableTitleTargetIsExactVisibleOrScene(target) &&
        !FableTitleTargetIsExactVisibleOrScene(command_rt)) {
      continue;
    }
    if (!g_direct_renderer.initialized_title_scene_targets_this_frame
             .insert(target)
             .second) {
      continue;
    }

    if (target == kFableTitleVisibleRenderTarget ||
        command_rt == kFableTitleVisibleRenderTarget) {
      continue;
    }

    command_list->clearColor(slot, plume::RenderColor(0.0f, 0.0f, 0.0f, 1.0f),
                             nullptr, 0u);
    MarkDirectResourceGpuProducedContentLocked(
        target, ++g_direct_renderer.scene_write_serial, true);
    plume::RenderTexture* color_texture =
        slot < framebuffer.color_textures.size()
            ? framebuffer.color_textures[slot]
            : nullptr;
    InvalidateRememberedSceneTargetForClearLocked(
        target, color_texture, framebuffer.width, framebuffer.height);
    if (g_direct_renderer.diagnostics.logged_title_scene_implicit_clears < 16u) {
      REXLOG_INFO(
          "Refii plume title visible target implicit clear #{} frame={} "
          "serial={} draw={} slot={} target=0x{:08X}/{}x{} "
          "command_rt=0x{:08X} pass={} vs=0x{:08X} ps=0x{:08X}",
          ++g_direct_renderer.diagnostics.logged_title_scene_implicit_clears,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot, target,
          framebuffer.width, framebuffer.height,
          command_rt, FableTitleRenderPassName(title_pass),
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }
}

bool ShouldProbeTitleTextureBinding(const FableDirectRenderCommand& command,
                                    const DirectTextureDebugBinding& binding) {
  if (binding.guest == 0) {
    return false;
  }
  if (IsFableTitleGuiTextAtlasCommandLocked(command, binding.slot)) {
    return true;
  }
  // ADDRESS GATE (census: title_texture_binding_probe_atlas). DIAGNOSTIC ONLY —
  // widens a probe log line. Left as a literal: 0x404DE550 is one of the three
  // 16x16 tiny-title upload objects and has no derived discriminator here.
  if (DirectAddressGateTest(DirectAddressGate::kTitleTextureBindingProbeAtlas,
                            binding.guest == 0x404DE550u ||
                                binding.tracked == 0x404DE550u,
                            binding.guest)) {
    return true;
  }
  if (command.resources.pixel_shader == kFableTitleSceneTextPixelShader &&
      binding.width <= 64u && binding.height <= 64u) {
    return true;
  }
  return false;
}

void LogTitleTextureBindingProbeLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const DirectTextureDebugBinding& binding) {
  if (g_direct_renderer.diagnostics.logged_title_texture_binding_probes >= 24u ||
      !ShouldProbeTitleTextureBinding(command, binding)) {
    return;
  }

  REXLOG_INFO(
      "Refii plume title texture binding probe #{} frame={} serial={} "
      "draw={} slot={} guest=0x{:08X} tracked=0x{:08X} parent=0x{:08X} "
      "base=0x{:08X} mip=0x{:08X} pitch={} size={}x{} fmt={} gfmt={} "
      "dim={} ready={} alias={} content={} upload_gen={} upload_size={} "
      "upload_source={} decoded={} hash=0x{:016X} first_nz={} "
      "sample={:02X} {:02X} {:02X} {:02X} sample_size={} "
      "rt=0x{:08X}/{}x{} vs=0x{:08X} ps=0x{:08X}",
      g_direct_renderer.diagnostics.logged_title_texture_binding_probes + 1u,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial), binding.slot,
      binding.guest, binding.tracked, binding.parent, binding.base, binding.mip,
      binding.pitch, binding.width, binding.height, binding.format,
      binding.guest_format, binding.dimension, binding.ready ? 1 : 0,
      binding.aliased ? 1 : 0,
      static_cast<unsigned long long>(binding.content_serial),
      static_cast<unsigned long long>(binding.generation),
      static_cast<unsigned long long>(binding.upload_size),
      binding.upload_source ? 1 : 0, binding.upload_decoded ? 1 : 0,
      static_cast<unsigned long long>(binding.hash), binding.first_nonzero,
      binding.sample[0], binding.sample[1], binding.sample[2], binding.sample[3],
      binding.sample_size, framebuffer.color_address, framebuffer.width,
      framebuffer.height, command.resources.vertex_shader,
      command.resources.pixel_shader);
  ++g_direct_renderer.diagnostics.logged_title_texture_binding_probes;
}

void LogTitleSceneSampleOwnershipProofLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    uint32_t slot) {
  if (!RendererDeepObserverHooksEnabled() ||
      g_direct_renderer.diagnostics.logged_title_scene_sample_ownership_proofs >= 4u ||
      slot >= command.resources.textures.size()) {
    return;
  }

  const uint32_t guest = command.resources.textures[slot];
  if (guest == 0) {
    return;
  }

  const PlumeTrackedResource* direct = FindDirectResourceLocked(guest);
  if (!direct || !IsImageResource(direct->kind) ||
      !PlumeGuestImageReady(*direct)) {
    return;
  }

  const PlumeTrackedResource* resolved =
      ResolveDirectSampledTextureForCommandLocked(command, slot);
  const uint32_t width = PlumeGuestTextureWidth(*direct);
  const uint32_t height = PlumeGuestTextureHeight(*direct);
  const bool fable_sized =
      width == framebuffer.width && height == framebuffer.height;
  const bool blank_no_upload =
      direct->content_serial == 0 && direct->upload_generation == 0 &&
      direct->texture_upload_hash == 0 && direct->texture_upload_size == 0 &&
      !direct->texture_upload_has_source;
  const bool unresolved_direct = resolved == nullptr || resolved == direct;
  const bool private_rect_proxy =
      direct->parent_address != 0 && PlumeGuestTextureBaseAddress(*direct) == 0 &&
      direct->texture_upload_size == 0 && !direct->texture_upload_has_source;
  if (!fable_sized && !private_rect_proxy &&
      !(blank_no_upload && unresolved_direct)) {
    return;
  }

  const uint32_t sample_base = PlumeGuestTextureBaseAddress(*direct);
  const uint32_t sample_mip = PlumeGuestTextureMipAddress(*direct);
  const uint32_t sample_pitch = PlumeGuestTexturePitch(*direct);
  const uint32_t sample_format =
      static_cast<uint32_t>(PlumeGuestTextureFormat(*direct));
  const uint32_t sample_guest_format = PlumeGuestTextureGuestFormat(*direct);
  REXLOG_INFO(
      "Refii plume exact title scene sample ownership proof #{} frame={} "
      "serial={} slot={} "
      "guest=0x{:08X} tracked=0x{:08X} resolved=0x{:08X} parent=0x{:08X} "
      "base=0x{:08X} mip=0x{:08X} pitch={} size={}x{} fmt={} gfmt={} "
      "rt={} depth={} content_serial={} draw_serial={} clear_serial={} "
      "upload_gen={} upload_size={} upload_source={} upload_hash=0x{:016X} "
      "rt=0x{:08X}/{}x{} "
      "depth_rt=0x{:08X} fable_scene_target=0x{:08X}/{}x{} "
      "last_scene=0x{:08X}/{}x{}",
      g_direct_renderer.diagnostics.logged_title_scene_sample_ownership_proofs + 1u,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial), slot, guest,
      direct->guest_address, resolved ? resolved->guest_address : 0u,
      direct->parent_address, sample_base, sample_mip, sample_pitch, width,
      height, sample_format, sample_guest_format,
      direct->texture_render_target ? 1 : 0,
      direct->texture_depth_target ? 1 : 0,
      static_cast<unsigned long long>(direct->content_serial),
      static_cast<unsigned long long>(direct->draw_content_serial),
      static_cast<unsigned long long>(direct->clear_content_serial),
      static_cast<unsigned long long>(direct->upload_generation),
      static_cast<unsigned long long>(direct->texture_upload_size),
      direct->texture_upload_has_source ? 1 : 0,
      static_cast<unsigned long long>(direct->texture_upload_hash),
      framebuffer.color_address, framebuffer.width, framebuffer.height,
      framebuffer.depth_address, g_direct_renderer.fable_scene_target,
      g_direct_renderer.fable_scene_width, g_direct_renderer.fable_scene_height,
      g_direct_renderer.last_submitted_scene_target,
      g_direct_renderer.last_submitted_scene_width,
      g_direct_renderer.last_submitted_scene_height);

  ++g_direct_renderer.diagnostics.logged_title_scene_sample_ownership_proofs;
}

void LogExactBinkWrapperPayloadLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const PlumeTrackedResource* y,
    const PlumeTrackedResource* cr,
    const PlumeTrackedResource* cb) {
  static uint32_t logged_exact_bink_wrapper_payloads = 0;
  if (!RendererVerboseTracingEnabled() ||
      logged_exact_bink_wrapper_payloads >= 24u ||
      !DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return;
  }

  const FableDirectInlineBuffer& vb = command.inline_vertex_buffers[0];
  const FableDirectInlineBuffer& ib = command.inline_index_buffer;
  const RendererStreamBindingSnapshot& stream = command.resources.streams[0];
  const uint32_t stride = ResolveDirectInputSlotStrideLocked(command, 0);
  const uint32_t vb_offset = DirectInlineBufferOffsetForAddress(vb, stream.buffer);
  const uint32_t ib_offset =
      DirectInlineBufferOffsetForAddress(ib, command.resources.index_buffer);
  if (!vb.ready() || !ib.ready() || stride == 0 || vb_offset == UINT32_MAX ||
      ib_offset == UINT32_MAX) {
    return;
  }

  const auto read_float = [&](uint32_t vertex, uint32_t byte_offset) {
    return ReadDirectInlineFloat(vb, vb_offset + vertex * stride + byte_offset);
  };
  const auto read_dword = [&](uint32_t vertex, uint32_t byte_offset) {
    const uint32_t byte = vb_offset + vertex * stride + byte_offset;
    if (byte + sizeof(uint32_t) > vb.data.size()) {
      return 0u;
    }
    uint32_t value = 0;
    std::memcpy(&value, vb.data.data() + byte, sizeof(value));
    return value;
  };
  const auto read_index = [&](uint32_t index) {
    const uint32_t stride_or_format = ib.stride_or_format == 4u ? 4u : 2u;
    const uint32_t byte = ib_offset + index * stride_or_format;
    if (byte + stride_or_format > ib.data.size()) {
      return 0u;
    }
    if (stride_or_format == 4u) {
      uint32_t value = 0;
      std::memcpy(&value, ib.data.data() + byte, sizeof(value));
      return value;
    }
    uint16_t value = 0;
    std::memcpy(&value, ib.data.data() + byte, sizeof(value));
    return static_cast<uint32_t>(value);
  };

  const PlumeTrackedResource* declaration =
      FindDirectReadyDeclarationResourceByIdentityLocked(
          command.resources.vertex_declaration);
  REXLOG_INFO(
      "Refii plume exact Bink wrapper payload #{} frame={} serial={} draw={} "
      "rt=0x{:08X}/{}x{} vb=0x{:08X}+{} bytes={} stride={} "
      "ib=0x{:08X}+{} bytes={} fmt={} decl=0x{:08X} hash=0x{:016X} "
      "swapped=0x{:08X} vp={}:{:.1f},{:.1f},{:.1f},{:.1f} "
      "sc={}:{},{},{},{} "
      "v0=({:.3f},{:.3f},{:.3f}) uv=({:.4f},{:.4f}) raw={:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
      "v1=({:.3f},{:.3f},{:.3f}) uv=({:.4f},{:.4f}) raw={:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
      "v2=({:.3f},{:.3f},{:.3f}) uv=({:.4f},{:.4f}) raw={:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
      "v3=({:.3f},{:.3f},{:.3f}) uv=({:.4f},{:.4f}) raw={:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
      "idx={},{},{},{},{},{} "
      "vc0=({:.4f},{:.4f},{:.4f},{:.4f}) vc1=({:.4f},{:.4f},{:.4f},{:.4f}) "
      "vc2=({:.4f},{:.4f},{:.4f},{:.4f}) vc3=({:.4f},{:.4f},{:.4f},{:.4f}) "
      "y=0x{:08X}/{}x{} cr=0x{:08X}/{}x{} cb=0x{:08X}/{}x{}",
      ++logged_exact_bink_wrapper_payloads,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial),
      framebuffer.color_address, framebuffer.width, framebuffer.height,
      stream.buffer, stream.offset, vb.byte_size, stride,
      command.resources.index_buffer, ib_offset, ib.byte_size,
      ib.stride_or_format, command.resources.vertex_declaration,
      static_cast<unsigned long long>(
          declaration ? declaration->guest_vertex_declaration.elements_hash
                      : 0ull),
      declaration ? declaration->swapped_texcoords : 0u,
      command.viewport.valid ? 1 : 0, command.viewport.x, command.viewport.y,
      command.viewport.width, command.viewport.height,
      command.scissor.valid ? 1 : 0, command.scissor.left,
      command.scissor.top, command.scissor.right, command.scissor.bottom,
      read_float(0, 0u), read_float(0, 4u), read_float(0, 8u),
      read_float(0, 12u), read_float(0, 16u), read_dword(0, 0u),
      read_dword(0, 4u), read_dword(0, 8u), read_dword(0, 12u),
      read_dword(0, 16u), read_float(1, 0u), read_float(1, 4u),
      read_float(1, 8u), read_float(1, 12u), read_float(1, 16u),
      read_dword(1, 0u), read_dword(1, 4u), read_dword(1, 8u),
      read_dword(1, 12u), read_dword(1, 16u), read_float(2, 0u),
      read_float(2, 4u), read_float(2, 8u), read_float(2, 12u),
      read_float(2, 16u), read_dword(2, 0u), read_dword(2, 4u),
      read_dword(2, 8u), read_dword(2, 12u), read_dword(2, 16u),
      read_float(3, 0u), read_float(3, 4u), read_float(3, 8u),
      read_float(3, 12u), read_float(3, 16u), read_dword(3, 0u),
      read_dword(3, 4u), read_dword(3, 8u), read_dword(3, 12u),
      read_dword(3, 16u), read_index(0u), read_index(1u), read_index(2u),
      read_index(3u), read_index(4u), read_index(5u),
      DwordAsFloat(DirectVertexConstantDword(command, 0, 0)),
      DwordAsFloat(DirectVertexConstantDword(command, 0, 1)),
      DwordAsFloat(DirectVertexConstantDword(command, 0, 2)),
      DwordAsFloat(DirectVertexConstantDword(command, 0, 3)),
      DwordAsFloat(DirectVertexConstantDword(command, 1, 0)),
      DwordAsFloat(DirectVertexConstantDword(command, 1, 1)),
      DwordAsFloat(DirectVertexConstantDword(command, 1, 2)),
      DwordAsFloat(DirectVertexConstantDword(command, 1, 3)),
      DwordAsFloat(DirectVertexConstantDword(command, 2, 0)),
      DwordAsFloat(DirectVertexConstantDword(command, 2, 1)),
      DwordAsFloat(DirectVertexConstantDword(command, 2, 2)),
      DwordAsFloat(DirectVertexConstantDword(command, 2, 3)),
      DwordAsFloat(DirectVertexConstantDword(command, 3, 0)),
      DwordAsFloat(DirectVertexConstantDword(command, 3, 1)),
      DwordAsFloat(DirectVertexConstantDword(command, 3, 2)),
      DwordAsFloat(DirectVertexConstantDword(command, 3, 3)),
      y ? y->guest_address : 0u, y ? PlumeGuestTextureWidth(*y) : 0u,
      y ? PlumeGuestTextureHeight(*y) : 0u,
      cr ? cr->guest_address : 0u, cr ? PlumeGuestTextureWidth(*cr) : 0u,
      cr ? PlumeGuestTextureHeight(*cr) : 0u,
      cb ? cb->guest_address : 0u, cb ? PlumeGuestTextureWidth(*cb) : 0u,
      cb ? PlumeGuestTextureHeight(*cb) : 0u);
}

void LogTitleSceneDrawLocked(const FableDirectRenderCommand& command,
                             const DirectFramebufferBinding& framebuffer) {
  if (framebuffer.color_count == 0) {
    return;
  }
  if (DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    static uint32_t logged_exact_bink_wrapper_video_draws = 0;
    const PlumeTrackedResource* y =
        ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, 0);
    const PlumeTrackedResource* cr =
        ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, 1);
    const PlumeTrackedResource* cb =
        ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, 2);
    const uint32_t bink_wrapper_video_draw_count =
        ++logged_exact_bink_wrapper_video_draws;
    if (bink_wrapper_video_draw_count <= 32u ||
        (bink_wrapper_video_draw_count &
         (bink_wrapper_video_draw_count - 1u)) == 0u) {
      REXLOG_INFO(
          "Refii plume exact Bink wrapper video draw #{} frame={} serial={} "
          "draw={} src={} rt=0x{:08X}/{}x{} fb_rt=0x{:08X}/{}x{} "
          "ds=0x{:08X} vs=0x{:08X}/0x{:016X} ps=0x{:08X}/0x{:016X} "
          "decl=0x{:08X} y=0x{:08X}/{}x{} ready={} "
          "cr=0x{:08X}/{}x{} ready={} cb=0x{:08X}/{}x{} ready={}",
          bink_wrapper_video_draw_count,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial),
          static_cast<uint32_t>(command.draw.source),
          command.resources.render_targets[0],
          framebuffer.width, framebuffer.height, framebuffer.color_address,
          framebuffer.width, framebuffer.height, framebuffer.depth_address,
          command.resources.vertex_shader,
          static_cast<unsigned long long>(
              DirectVertexShaderHashLocked(command.resources.vertex_shader)),
          command.resources.pixel_shader,
          static_cast<unsigned long long>(
              DirectPixelShaderHashLocked(command.resources.pixel_shader)),
          command.resources.vertex_declaration,
          y ? y->guest_address : command.resources.textures[0],
          y ? PlumeGuestTextureWidth(*y) : 0u,
          y ? PlumeGuestTextureHeight(*y) : 0u,
          y && PlumeGuestImageReady(*y) ? 1 : 0,
          cr ? cr->guest_address : command.resources.textures[1],
          cr ? PlumeGuestTextureWidth(*cr) : 0u,
          cr ? PlumeGuestTextureHeight(*cr) : 0u,
          cr && PlumeGuestImageReady(*cr) ? 1 : 0,
          cb ? cb->guest_address : command.resources.textures[2],
          cb ? PlumeGuestTextureWidth(*cb) : 0u,
          cb ? PlumeGuestTextureHeight(*cb) : 0u,
          cb && PlumeGuestImageReady(*cb) ? 1 : 0);
    }
    LogExactBinkWrapperPayloadLocked(command, framebuffer, y, cr, cb);
    return;
  }
  const bool likely_title_scene_target =
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneTargetLocked(framebuffer.color_address);
  const bool final_title_private_indirection =
      IsFableFinalTitlePrivateIndirectionCommandLocked(command);
  const bool likely_title_scene_shader =
      final_title_private_indirection ||
      command.resources.pixel_shader == kFableTitleSceneTextPixelShader ||
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
      IsFableTitleSceneVertexShaderCommandLocked(command) ||
      command.draw.source == RendererDrawEventSource::kSceneObject;
  const bool hardcoded_title_target =
      command.resources.render_targets[0] == kFableTitleSceneRenderTarget ||
      framebuffer.color_address == kFableTitleSceneRenderTarget;
  if (!likely_title_scene_shader && !hardcoded_title_target) {
    static uint32_t logged_fable_unclassified_title_draws = 0;
    const uint32_t unclassified_count =
        likely_title_scene_target &&
                command.draw.source != RendererDrawEventSource::kPixVideo
            ? ++logged_fable_unclassified_title_draws
            : 0u;
    if (unclassified_count != 0u &&
        (unclassified_count <= 32u ||
         (unclassified_count & (unclassified_count - 1u)) == 0u)) {
      const uint32_t tex0 =
          command.resources.textures.empty() ? 0u : command.resources.textures[0];
      const uint32_t tex13 =
          source_slot < command.resources.textures.size()
              ? command.resources.textures[source_slot]
              : 0u;
      const PlumeTrackedResource* render_target =
          FindDirectResourceLocked(command.resources.render_targets[0]);
      const PlumeTrackedResource* tex0_resource = FindDirectResourceLocked(tex0);
      const PlumeTrackedResource* tex13_resource = FindDirectResourceLocked(tex13);
      const RendererStreamBindingSnapshot& stream0 =
          command.resources.streams[0];
      REXLOG_INFO(
          "Refii plume Fable target unclassified draw #{} frame={} "
          "serial={} draw={} src={} idx={} prim={} start={} count={} "
          "rt=0x{:08X}/{}x{} fb_rt=0x{:08X}/{}x{} ds=0x{:08X} "
          "vs=0x{:08X}/0x{:016X} ps=0x{:08X}/0x{:016X} decl=0x{:08X} "
          "vb0=0x{:08X}+{} stride={} stride_resolved={} "
          "tex0=0x{:08X}/{}x{} fmt={} ready={} gen={} "
          "tex13=0x{:08X}/{}x{} fmt={} ready={} gen={}",
          unclassified_count,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial),
          static_cast<uint32_t>(command.draw.source),
          command.draw.draw.indexed ? 1 : 0,
          command.draw.draw.primitive_type, command.draw.draw.start,
          command.draw.draw.primitive_count,
          command.resources.render_targets[0],
          render_target ? PlumeGuestTextureWidth(*render_target) : 0u,
          render_target ? PlumeGuestTextureHeight(*render_target) : 0u,
          framebuffer.color_address, framebuffer.width, framebuffer.height,
          framebuffer.depth_address, command.resources.vertex_shader,
          static_cast<unsigned long long>(
              DirectVertexShaderHashLocked(command.resources.vertex_shader)),
          command.resources.pixel_shader,
          static_cast<unsigned long long>(
              DirectPixelShaderHashLocked(command.resources.pixel_shader)),
          command.resources.vertex_declaration, stream0.buffer, stream0.offset,
          stream0.stride, ResolveDirectInputSlotStrideLocked(command, 0),
          tex0, tex0_resource ? PlumeGuestTextureWidth(*tex0_resource) : 0u,
          tex0_resource ? PlumeGuestTextureHeight(*tex0_resource) : 0u,
          tex0_resource ? static_cast<uint32_t>(
                              PlumeGuestTextureFormat(*tex0_resource))
                        : 0u,
          tex0_resource && PlumeGuestImageReady(*tex0_resource) ? 1 : 0,
          tex0_resource ? static_cast<unsigned long long>(
                              tex0_resource->upload_generation)
                        : 0ull,
          tex13, tex13_resource ? PlumeGuestTextureWidth(*tex13_resource) : 0u,
          tex13_resource ? PlumeGuestTextureHeight(*tex13_resource) : 0u,
          tex13_resource ? static_cast<uint32_t>(
                               PlumeGuestTextureFormat(*tex13_resource))
                         : 0u,
          tex13_resource && PlumeGuestImageReady(*tex13_resource) ? 1 : 0,
          tex13_resource ? static_cast<unsigned long long>(
                               tex13_resource->upload_generation)
                         : 0ull);
    }
    return;
  }
  if (!likely_title_scene_target && !likely_title_scene_shader) {
    return;
  }
  constexpr uint32_t kNonVerboseTitleSceneDrawLogLimit = 2;

  // Everything below here exists solely to build ONE log line, and the cap that
  // suppresses it was tested at the very END -- after nine
  // DirectTextureDebugBindingForSlotLocked resolver walks, four
  // FindDirectResourceLocked calls and a full title-pass classification. So
  // every scene draw kept paying that price forever, for a line capped at 2.
  //
  // The cap cannot simply be hoisted, because `title_draw_log_limit` is chosen
  // from `important_late_title_draw`, which is exactly what those walks compute.
  // But the limit is BOUNDED: it is at most 64 when verbose diagnostics are on
  // and at most 32 otherwise. Once the counter has passed that bound the
  // function must return no matter which branch the walks would have selected,
  // so returning early here is provably equivalent -- and it makes the whole
  // block cost nothing once the log has gone quiet.
  constexpr uint32_t kMaxTitleSceneDrawLogLimit =
      kFableVerboseTitleSceneDiagnostics ? 64u : 32u;
  if (g_direct_renderer.diagnostics.logged_title_scene_draws >=
      kMaxTitleSceneDrawLogLimit) {
    return;
  }

  const RendererStreamBindingSnapshot& stream0 = command.resources.streams[0];
  const RendererStreamBindingSnapshot& stream1 = command.resources.streams[1];
  const PlumeTrackedResource* vb0 = FindDirectResourceLocked(stream0.buffer);
  const PlumeTrackedResource* vb1 = FindDirectResourceLocked(stream1.buffer);
  const PlumeTrackedResource* ib =
      FindDirectResourceLocked(command.resources.index_buffer);
  const PlumeTrackedResource* declaration =
      FindDirectReadyDeclarationResourceByIdentityLocked(
          command.resources.vertex_declaration);
  const DirectTextureDebugBinding tex_first =
      FirstDirectTextureDebugBindingLocked(command);
  const DirectTextureDebugBinding tex0 =
      DirectTextureDebugBindingForSlotLocked(command, 0);
  const DirectTextureDebugBinding tex1 =
      DirectTextureDebugBindingForSlotLocked(command, 1);
  const DirectTextureDebugBinding tex2 =
      DirectTextureDebugBindingForSlotLocked(command, 2);
  const DirectTextureDebugBinding tex3 =
      DirectTextureDebugBindingForSlotLocked(command, 3);
  const DirectTextureDebugBinding tex13 =
      DirectTextureDebugBindingForSlotLocked(command, source_slot);
  const DirectTextureDebugBinding tex14 =
      DirectTextureDebugBindingForSlotLocked(command, 14);
  const DirectTextureDebugBinding tex15 =
      DirectTextureDebugBindingForSlotLocked(command, 15);
  const FableTitleRenderPass title_pass =
      ClassifyFableTitleRenderPassLocked(command, framebuffer, tex_first, tex13);
  auto touches_title_chain = [](const DirectTextureDebugBinding& binding) {
    return FableTitleTextureBindingTouchesPrivateChain(
        TitleTextureBindingFacts(binding));
  };
  // ADDRESS GATE (census: final_title_target_diagnostic). DIAGNOSTIC ONLY —
  // selects the title-scene draw log verbosity and cap. 0x4011F370 substituted
  // for kFableTitleVisibleRenderTarget (identical value, no behaviour change).
  const bool final_title_target = DirectAddressGateTest(
      DirectAddressGate::kFinalTitleTargetDiagnostic,
      framebuffer.color_address == kFableTitleVisibleRenderTarget ||
          command.resources.render_targets[0] == kFableTitleVisibleRenderTarget,
      command.resources.render_targets[0]);
  const bool important_late_title_draw =
      final_title_target ||
      touches_title_chain(tex_first) || touches_title_chain(tex0) ||
      touches_title_chain(tex1) || touches_title_chain(tex2) ||
      touches_title_chain(tex3) || touches_title_chain(tex13) ||
      touches_title_chain(tex14) || touches_title_chain(tex15);
  const bool emit_detailed_title_diagnostics =
      RendererDeepObserverHooksEnabled() &&
      (kFableVerboseTitleSceneDiagnostics || important_late_title_draw);
  const uint32_t title_draw_log_limit =
      kFableVerboseTitleSceneDiagnostics
          ? (important_late_title_draw ? 64u : 8u)
          : (important_late_title_draw ? 32u
                                       : kNonVerboseTitleSceneDrawLogLimit);
  if (g_direct_renderer.diagnostics.logged_title_scene_draws >= title_draw_log_limit) {
    return;
  }
  const uint32_t count = DrawVertexOrIndexCount(
      command.draw.draw.primitive_type, command.draw.draw.primitive_count);
  const uint32_t color_mask = command.pipeline.pm4.valid
                                  ? command.pipeline.pm4.rb_color_mask
                                  : command.pipeline.render_states
                                        [kFableRenderStateColorWriteEnable];
  if (emit_detailed_title_diagnostics) {
    LogTitleSceneSampleOwnershipProofLocked(command, framebuffer, tex_first.slot);
    LogTitleSceneSampleOwnershipProofLocked(command, framebuffer, 0);
    LogTitleSceneSampleOwnershipProofLocked(command, framebuffer, 1);
    LogTitleSceneSampleOwnershipProofLocked(command, framebuffer, source_slot);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex_first);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex0);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex1);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex2);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex3);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex13);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex14);
    LogTitleTextureBindingProbeLocked(command, framebuffer, tex15);
  }
  REXLOG_INFO(
      "Refii plume title scene draw #{} pass={} frame={} serial={} draw={} src={} "
      "idx={} prim={} start={} count={} base={} rt=0x{:08X}/{}x{} "
      "depth=0x{:08X} vs=0x{:08X} ps=0x{:08X} decl=0x{:08X} "
      "vb0=0x{:08X}+{} stride={} ready={} sz={} gen={} hash=0x{:016X} "
      "vb0_inline={}/{}/{} "
      "vb1=0x{:08X}+{} stride={} ready={} sz={} "
      "ib=0x{:08X} ready={} sz={} fmt={} "
      "vp={}:{:.1f},{:.1f},{:.1f},{:.1f},{:.3f},{:.3f} "
      "sc={}:{},{},{},{} pm4={} colorctl=0x{:08X} atest={} "
      "depth_ctl=0x{:08X} color_mask=0x{:08X} "
      "blend0=0x{:08X} swapped=0x{:08X} "
      "tex_first={}:0x{:08X}->0x{:08X}/{}x{} fmt={} "
      "ready={} gen={} hash=0x{:016X} alias={} "
      "tex0=0x{:08X}->0x{:08X}/{}x{} ready={} "
      "tex1=0x{:08X}->0x{:08X}/{}x{} ready={} "
      "tex13=0x{:08X}->0x{:08X}/{}x{} fmt={} ready={} gen={} "
      "hash=0x{:016X} alias={}",
      g_direct_renderer.diagnostics.logged_title_scene_draws + 1u,
      FableTitleRenderPassName(title_pass),
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial),
      static_cast<uint32_t>(command.draw.source), command.draw.draw.indexed ? 1 : 0,
      command.draw.draw.primitive_type, command.draw.draw.start, count,
      command.draw.draw.base_vertex, framebuffer.color_address,
      framebuffer.width, framebuffer.height, framebuffer.depth_address,
      command.resources.vertex_shader, command.resources.pixel_shader,
      command.resources.vertex_declaration, stream0.buffer, stream0.offset,
      stream0.stride, vb0 && PlumeGuestBufferReady(*vb0) ? 1 : 0,
      vb0 ? PlumeGuestBufferByteSize(*vb0) : 0u,
      vb0 ? static_cast<unsigned long long>(vb0->upload_generation) : 0ull,
      vb0 ? static_cast<unsigned long long>(vb0->guest_buffer.upload_hash) : 0ull,
      command.inline_vertex_buffers[0].ready() ? 1 : 0,
      command.inline_vertex_buffers[0].byte_size,
      std::any_of(command.inline_vertex_buffers[0].data.begin(),
                  command.inline_vertex_buffers[0].data.end(),
                  [](uint8_t inline_byte) { return inline_byte != 0; })
          ? 1
          : 0,
      stream1.buffer, stream1.offset, stream1.stride,
      vb1 && PlumeGuestBufferReady(*vb1) ? 1 : 0,
      vb1 ? PlumeGuestBufferByteSize(*vb1) : 0u,
      command.resources.index_buffer, ib && PlumeGuestBufferReady(*ib) ? 1 : 0,
      ib ? PlumeGuestBufferByteSize(*ib) : 0u,
      ib ? PlumeGuestBufferStrideOrFormat(*ib) : 0u,
      command.viewport.valid ? 1 : 0, command.viewport.x, command.viewport.y,
      command.viewport.width, command.viewport.height, command.viewport.min_depth,
      command.viewport.max_depth, command.scissor.valid ? 1 : 0,
      command.scissor.left, command.scissor.top, command.scissor.right,
      command.scissor.bottom, command.pipeline.pm4.valid ? 1 : 0,
      command.pipeline.pm4.valid ? command.pipeline.pm4.rb_colorcontrol : 0u,
      FableAlphaTestEnabled(BuildDrawEventFromCommandForShaderState(command))
          ? 1
          : 0,
      DirectDepthControl(command), color_mask,
      DirectBlendControlForTarget(command, 0),
      declaration ? declaration->swapped_texcoords : 0u, tex_first.slot,
      tex_first.guest, tex_first.tracked, tex_first.width,
      tex_first.height, tex_first.format,
      tex_first.ready ? 1 : 0,
      static_cast<unsigned long long>(tex_first.generation),
      static_cast<unsigned long long>(tex_first.hash),
      tex_first.aliased ? 1 : 0, tex0.guest, tex0.tracked, tex0.width,
      tex0.height, tex0.ready ? 1 : 0, tex1.guest, tex1.tracked, tex1.width,
      tex1.height, tex1.ready ? 1 : 0, tex13.guest, tex13.tracked,
      tex13.width, tex13.height, tex13.format, tex13.ready ? 1 : 0,
      static_cast<unsigned long long>(tex13.generation),
      static_cast<unsigned long long>(tex13.hash),
      tex13.aliased ? 1 : 0);
  if (!emit_detailed_title_diagnostics) {
    ++g_direct_renderer.diagnostics.logged_title_scene_draws;
    return;
  }
  // ADDRESS GATE (census: title_scene_declaration_diagnostic). DIAGNOSTIC ONLY.
  // `derived` here compares against the same two declaration hashes the decode
  // fallbacks use, which is what these two object pointers stand for.
  if (declaration && !declaration->debug_summary.empty() &&
      DirectAddressGateTest(
          DirectAddressGate::kTitleSceneDeclarationDiagnostic,
          command.resources.vertex_declaration == 0x400216B0u ||
              command.resources.vertex_declaration == 0x40021910u ||
              IsFableTitleGuiQuadDeclaration(
                  command.resources.vertex_declaration),
          command.resources.vertex_declaration,
          declaration->guest_vertex_declaration.elements_hash,
          declaration->guest_vertex_declaration.elements_hash ==
                  kFablePrivateRectDeclarationHash ||
              declaration->guest_vertex_declaration.elements_hash ==
                  kFableTitleIndexedDeclarationHash)) {
    REXLOG_INFO(
        "Refii plume title scene declaration decl=0x{:08X} hash=0x{:016X} "
        "elements={}",
        command.resources.vertex_declaration,
        static_cast<unsigned long long>(
            declaration->guest_vertex_declaration.elements_hash),
        declaration->debug_summary);
  }

  const FableDirectInlineBuffer& inline_vb0 = command.inline_vertex_buffers[0];
  const FableDirectInlineBuffer& inline_vb1 = command.inline_vertex_buffers[1];
  const FableDirectInlineBuffer& inline_ib = command.inline_index_buffer;
  const uint32_t c20x = DirectPixelConstantDword(command, 20, 0);
  const uint32_t c20y = DirectPixelConstantDword(command, 20, 1);
  const uint32_t c20z = DirectPixelConstantDword(command, 20, 2);
  const uint32_t c20w = DirectPixelConstantDword(command, 20, 3);
  const uint32_t c31x = DirectPixelConstantDword(command, 31, 0);
  const uint32_t c31y = DirectPixelConstantDword(command, 31, 1);
  const uint32_t c31z = DirectPixelConstantDword(command, 31, 2);
  const uint32_t c31w = DirectPixelConstantDword(command, 31, 3);
  const uint32_t c46x = DirectPixelConstantDword(command, 46, 0);
  const uint32_t c46y = DirectPixelConstantDword(command, 46, 1);
  const uint32_t c46z = DirectPixelConstantDword(command, 46, 2);
  const uint32_t c46w = DirectPixelConstantDword(command, 46, 3);
  const uint32_t c47x = DirectPixelConstantDword(command, 47, 0);
  const uint32_t c47y = DirectPixelConstantDword(command, 47, 1);
  const uint32_t c47z = DirectPixelConstantDword(command, 47, 2);
  const uint32_t c47w = DirectPixelConstantDword(command, 47, 3);
  const uint32_t c77x = DirectPixelConstantDword(command, 77, 0);
  const uint32_t c77y = DirectPixelConstantDword(command, 77, 1);
  const uint32_t c77z = DirectPixelConstantDword(command, 77, 2);
  const uint32_t c77w = DirectPixelConstantDword(command, 77, 3);
  const uint32_t c78x = DirectPixelConstantDword(command, 78, 0);
  const uint32_t c78y = DirectPixelConstantDword(command, 78, 1);
  const uint32_t c78z = DirectPixelConstantDword(command, 78, 2);
  const uint32_t c78w = DirectPixelConstantDword(command, 78, 3);
  const uint32_t vc8x = DirectVertexConstantDword(command, 8, 0);
  const uint32_t vc8y = DirectVertexConstantDword(command, 8, 1);
  const uint32_t vc8z = DirectVertexConstantDword(command, 8, 2);
  const uint32_t vc8w = DirectVertexConstantDword(command, 8, 3);
  const uint32_t vc0x = DirectVertexConstantDword(command, 0, 0);
  const uint32_t vc0y = DirectVertexConstantDword(command, 0, 1);
  const uint32_t vc0z = DirectVertexConstantDword(command, 0, 2);
  const uint32_t vc0w = DirectVertexConstantDword(command, 0, 3);
  const uint32_t vc1x = DirectVertexConstantDword(command, 1, 0);
  const uint32_t vc1y = DirectVertexConstantDword(command, 1, 1);
  const uint32_t vc1z = DirectVertexConstantDword(command, 1, 2);
  const uint32_t vc1w = DirectVertexConstantDword(command, 1, 3);
  const uint32_t vc2x = DirectVertexConstantDword(command, 2, 0);
  const uint32_t vc2y = DirectVertexConstantDword(command, 2, 1);
  const uint32_t vc2z = DirectVertexConstantDword(command, 2, 2);
  const uint32_t vc2w = DirectVertexConstantDword(command, 2, 3);
  const uint32_t vc3x = DirectVertexConstantDword(command, 3, 0);
  const uint32_t vc3y = DirectVertexConstantDword(command, 3, 1);
  const uint32_t vc3z = DirectVertexConstantDword(command, 3, 2);
  const uint32_t vc3w = DirectVertexConstantDword(command, 3, 3);
  REXLOG_INFO(
      "Refii plume title scene draw detail #{} frame={} serial={} "
      "inline_vb0={} addr=0x{:08X} bytes={} stride={} hash=0x{:016X} "
      "dwords={:08X},{:08X},{:08X},{:08X} "
      "floats={:.4f},{:.4f},{:.4f},{:.4f} "
      "inline_vb1={} addr=0x{:08X} bytes={} stride={} hash=0x{:016X} "
      "inline_ib={} addr=0x{:08X} bytes={} fmt={} hash=0x{:016X} "
      "indices={},{},{},{} "
      "tex14=0x{:08X}->0x{:08X}/{}x{} ready={} "
      "tex15=0x{:08X}->0x{:08X}/{}x{} ready={} "
      "ps_c0_nz={} ps_c20={:08X},{:08X},{:08X},{:08X}/"
      "{:.4f},{:.4f},{:.4f},{:.4f} "
      "ps_c30_nz={} ps_c31={:08X},{:08X},{:08X},{:08X}/"
      "{:.4f},{:.4f},{:.4f},{:.4f} "
      "ps_c46_nz={} ps_c47_nz={} ps_c77_nz={} "
      "vs_c8={:08X},{:08X},{:08X},{:08X}/"
      "{:.6f},{:.6f},{:.6f},{:.6f}",
      g_direct_renderer.diagnostics.logged_title_scene_draws + 1u,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial),
      inline_vb0.ready() ? 1 : 0, inline_vb0.address,
      inline_vb0.byte_size, inline_vb0.stride_or_format,
      static_cast<unsigned long long>(HashDirectInlineBufferPrefix(inline_vb0)),
      ReadDirectInlineDword(inline_vb0, 0), ReadDirectInlineDword(inline_vb0, 1),
      ReadDirectInlineDword(inline_vb0, 2), ReadDirectInlineDword(inline_vb0, 3),
      DwordAsFloat(ReadDirectInlineDword(inline_vb0, 0)),
      DwordAsFloat(ReadDirectInlineDword(inline_vb0, 1)),
      DwordAsFloat(ReadDirectInlineDword(inline_vb0, 2)),
      DwordAsFloat(ReadDirectInlineDword(inline_vb0, 3)),
      inline_vb1.ready() ? 1 : 0, inline_vb1.address,
      inline_vb1.byte_size, inline_vb1.stride_or_format,
      static_cast<unsigned long long>(HashDirectInlineBufferPrefix(inline_vb1)),
      inline_ib.ready() ? 1 : 0, inline_ib.address, inline_ib.byte_size,
      inline_ib.stride_or_format,
      static_cast<unsigned long long>(HashDirectInlineBufferPrefix(inline_ib)),
      ReadDirectInlineIndex(inline_ib, 0), ReadDirectInlineIndex(inline_ib, 1),
      ReadDirectInlineIndex(inline_ib, 2), ReadDirectInlineIndex(inline_ib, 3),
      tex14.guest, tex14.tracked, tex14.width, tex14.height,
      tex14.ready ? 1 : 0, tex15.guest, tex15.tracked, tex15.width,
      tex15.height, tex15.ready ? 1 : 0,
      DirectPixelConstantQuadNonZero(command, 0) ? 1 : 0, c20x, c20y, c20z,
      c20w, DwordAsFloat(c20x), DwordAsFloat(c20y), DwordAsFloat(c20z),
      DwordAsFloat(c20w), DirectPixelConstantQuadNonZero(command, 30) ? 1 : 0,
      c31x, c31y, c31z, c31w, DwordAsFloat(c31x), DwordAsFloat(c31y),
      DwordAsFloat(c31z), DwordAsFloat(c31w),
      DirectPixelConstantQuadNonZero(command, 46) ? 1 : 0,
      DirectPixelConstantQuadNonZero(command, 47) ? 1 : 0,
      DirectPixelConstantQuadNonZero(command, 77) ? 1 : 0, vc8x, vc8y, vc8z,
      vc8w, DwordAsFloat(vc8x), DwordAsFloat(vc8y), DwordAsFloat(vc8z),
      DwordAsFloat(vc8w));
  // ADDRESS GATE (census: title_billboard_payload_diagnostic). DIAGNOSTIC ONLY.
  // 0x40021910 is the title indexed declaration object; `derived` compares the
  // decoded elements hash, which is the run-stable form of the same test.
  const bool title_billboard_payload_literal =
      command.resources.vertex_declaration == 0x40021910u;
  const uint64_t title_billboard_payload_declaration_hash =
      title_billboard_payload_literal
          ? DirectVertexDeclarationHashLocked(
                command.resources.vertex_declaration)
          : 0ull;
  if (DirectAddressGateTest(
          DirectAddressGate::kTitleBillboardPayloadDiagnostic,
          title_billboard_payload_literal,
          command.resources.vertex_declaration,
          title_billboard_payload_declaration_hash,
          title_billboard_payload_declaration_hash ==
              kFableTitleIndexedDeclarationHash) &&
      stream0.stride == 72u && inline_vb0.ready() &&
      inline_vb0.byte_size >= 72u) {
    auto read_vertex_dword = [&](uint32_t vertex, uint32_t byte_offset) {
      return ReadDirectInlineDword(inline_vb0,
                                   vertex * 72u / sizeof(uint32_t) +
                                       byte_offset / sizeof(uint32_t));
    };
    auto read_vertex_float = [&](uint32_t vertex, uint32_t byte_offset) {
      return DwordAsFloat(read_vertex_dword(vertex, byte_offset));
    };
    REXLOG_INFO(
        "Refii plume title billboard payload #{} frame={} serial={} "
        "vs=0x{:08X} ps=0x{:08X} tex13=0x{:08X}/{}x{} fmt={} "
        "idx={},{},{},{},{},{} "
        "v0=({:.3f},{:.3f},{:.3f}) uv0=({:.4f},{:.4f}) "
        "uv1=({:.4f},{:.4f}) a={:.4f} c=0x{:08X} "
        "v1=({:.3f},{:.3f},{:.3f}) uv0=({:.4f},{:.4f}) "
        "uv1=({:.4f},{:.4f}) a={:.4f} c=0x{:08X} "
        "v2=({:.3f},{:.3f},{:.3f}) uv0=({:.4f},{:.4f}) "
        "uv1=({:.4f},{:.4f}) a={:.4f} c=0x{:08X} "
        "v3=({:.3f},{:.3f},{:.3f}) uv0=({:.4f},{:.4f}) "
        "uv1=({:.4f},{:.4f}) a={:.4f} c=0x{:08X}",
        g_direct_renderer.diagnostics.logged_title_scene_draws + 1u,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        command.resources.vertex_shader, command.resources.pixel_shader,
        tex13.guest, tex13.width, tex13.height, tex13.format,
        ReadDirectInlineIndex(inline_ib, 0), ReadDirectInlineIndex(inline_ib, 1),
        ReadDirectInlineIndex(inline_ib, 2), ReadDirectInlineIndex(inline_ib, 3),
        ReadDirectInlineIndex(inline_ib, 4), ReadDirectInlineIndex(inline_ib, 5),
        read_vertex_float(0, 0u), read_vertex_float(0, 4u),
        read_vertex_float(0, 8u), read_vertex_float(0, 48u),
        read_vertex_float(0, 52u), read_vertex_float(0, 56u),
        read_vertex_float(0, 60u), read_vertex_float(0, 64u),
        read_vertex_dword(0, 68u), read_vertex_float(1, 0u),
        read_vertex_float(1, 4u), read_vertex_float(1, 8u),
        read_vertex_float(1, 48u), read_vertex_float(1, 52u),
        read_vertex_float(1, 56u), read_vertex_float(1, 60u),
        read_vertex_float(1, 64u), read_vertex_dword(1, 68u),
        read_vertex_float(2, 0u), read_vertex_float(2, 4u),
        read_vertex_float(2, 8u), read_vertex_float(2, 48u),
        read_vertex_float(2, 52u), read_vertex_float(2, 56u),
        read_vertex_float(2, 60u), read_vertex_float(2, 64u),
        read_vertex_dword(2, 68u), read_vertex_float(3, 0u),
        read_vertex_float(3, 4u), read_vertex_float(3, 8u),
        read_vertex_float(3, 48u), read_vertex_float(3, 52u),
        read_vertex_float(3, 56u), read_vertex_float(3, 60u),
        read_vertex_float(3, 64u), read_vertex_dword(3, 68u));
  }
  REXLOG_INFO(
      "Refii plume title scene vertex constants #{} frame={} serial={} "
      "vs_c0_nz={} vs_c1_nz={} vs_c2_nz={} vs_c3_nz={} "
      "c0={:08X},{:08X},{:08X},{:08X}/{:.6f},{:.6f},{:.6f},{:.6f} "
      "c1={:08X},{:08X},{:08X},{:08X}/{:.6f},{:.6f},{:.6f},{:.6f} "
      "c2={:08X},{:08X},{:08X},{:08X}/{:.6f},{:.6f},{:.6f},{:.6f} "
      "c3={:08X},{:08X},{:08X},{:08X}/{:.6f},{:.6f},{:.6f},{:.6f}",
      g_direct_renderer.diagnostics.logged_title_scene_draws + 1u,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial),
      DirectVertexConstantQuadNonZero(command, 0) ? 1 : 0,
      DirectVertexConstantQuadNonZero(command, 1) ? 1 : 0,
      DirectVertexConstantQuadNonZero(command, 2) ? 1 : 0,
      DirectVertexConstantQuadNonZero(command, 3) ? 1 : 0,
      vc0x, vc0y, vc0z, vc0w, DwordAsFloat(vc0x), DwordAsFloat(vc0y),
      DwordAsFloat(vc0z), DwordAsFloat(vc0w), vc1x, vc1y, vc1z, vc1w,
      DwordAsFloat(vc1x), DwordAsFloat(vc1y), DwordAsFloat(vc1z),
      DwordAsFloat(vc1w), vc2x, vc2y, vc2z, vc2w, DwordAsFloat(vc2x),
      DwordAsFloat(vc2y), DwordAsFloat(vc2z), DwordAsFloat(vc2w), vc3x,
      vc3y, vc3z, vc3w, DwordAsFloat(vc3x), DwordAsFloat(vc3y),
      DwordAsFloat(vc3z), DwordAsFloat(vc3w));
  REXLOG_INFO(
      "Refii plume title scene draw extra #{} frame={} serial={} "
      "tex2=0x{:08X}->0x{:08X}/{}x{} fmt={} ready={} gen={} "
      "hash=0x{:016X} alias={} "
      "tex3=0x{:08X}->0x{:08X}/{}x{} fmt={} ready={} gen={} "
      "hash=0x{:016X} alias={} "
      "ps_c46={:08X},{:08X},{:08X},{:08X}/"
      "{:.4f},{:.4f},{:.4f},{:.4f} "
      "ps_c47={:08X},{:08X},{:08X},{:08X}/"
      "{:.4f},{:.4f},{:.4f},{:.4f} "
      "ps_c77={:08X},{:08X},{:08X},{:08X}/"
      "{:.4f},{:.4f},{:.4f},{:.4f} "
      "ps_c78={:08X},{:08X},{:08X},{:08X}/"
      "{:.4f},{:.4f},{:.4f},{:.4f}",
      g_direct_renderer.diagnostics.logged_title_scene_draws + 1u,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial), tex2.guest, tex2.tracked,
      tex2.width, tex2.height, tex2.format, tex2.ready ? 1 : 0,
      static_cast<unsigned long long>(tex2.generation),
      static_cast<unsigned long long>(tex2.hash), tex2.aliased ? 1 : 0,
      tex3.guest, tex3.tracked, tex3.width, tex3.height, tex3.format,
      tex3.ready ? 1 : 0,
      static_cast<unsigned long long>(tex3.generation),
      static_cast<unsigned long long>(tex3.hash), tex3.aliased ? 1 : 0,
      c46x, c46y, c46z, c46w, DwordAsFloat(c46x), DwordAsFloat(c46y),
      DwordAsFloat(c46z), DwordAsFloat(c46w), c47x, c47y, c47z, c47w,
      DwordAsFloat(c47x), DwordAsFloat(c47y), DwordAsFloat(c47z),
      DwordAsFloat(c47w), c77x, c77y, c77z, c77w, DwordAsFloat(c77x),
      DwordAsFloat(c77y), DwordAsFloat(c77z), DwordAsFloat(c77w), c78x,
      c78y, c78z, c78w, DwordAsFloat(c78x), DwordAsFloat(c78y),
      DwordAsFloat(c78z), DwordAsFloat(c78w));
  ++g_direct_renderer.diagnostics.logged_title_scene_draws;
}

// ZERO-CALLER FUNCTION ResolveDirectRegisteredPrivateAliasForSourceLocked —
// DELETED 2026-07-26 (~242 lines). It searched the registered private-texture
// alias maps (by fetch key, by base address) and the surface->destination
// relation tables for a substitute sampleable resource, validating candidates
// against feedback-alias and proxy-role rules. Verified zero-caller across the
// WHOLE repo (src/, tools/, both rexglue shadows, headers included): the only
// two occurrences were its forward declaration and its definition, both in
// this file, and both are removed. Internal linkage (anonymous namespace), so
// no cross-TU call path exists either.
void ResolveUnbackedTitlePrivateProxyCopyLocked(
    const FableDirectRenderCommand& command) {
  if (!command.draw.private_texture_rectangle ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
      source_slot >= command.resources.textures.size()) {
    return;
  }

  PlumeTrackedResource* source = ResolveDirectSampledTextureForCommandLocked(
      command, source_slot);
  if (!source || !FableTrackedResourceIsUnbackedSampleProxy(*source)) {
    return;
  }

  if (DirectSyntheticPrivateRectDepthActionCanExecuteLocked(command,
                                                            source_slot,
                                                            source)) {
    return;
  }

  if (PlumeTrackedResource* exact_backing =
          ResolveDirectPrivateTextureCopyActionLocked(
              command, source_slot, source)) {
    if (exact_backing != source && IsImageResource(exact_backing->kind) &&
        PlumeGuestImageReady(*exact_backing) &&
        FableTrackedResourceHasSampleableContent(*exact_backing) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*exact_backing) &&
        !DirectCommandBindsResourceAsTargetLocked(command, *exact_backing)) {
      static uint32_t logged_unbacked_title_private_exact_allows = 0;
      const uint32_t guest =
          command.resources.textures[source_slot];
      if (logged_unbacked_title_private_exact_allows < 32u) {
        REXLOG_INFO(
            "Refii plume allowed exact title/private proxy backing #{} "
            "frame={} guest=0x{:08X} tracked=0x{:08X}/{}x{} fmt={} "
            "backing=0x{:08X}/{}x{} fmt={} content={} rt=0x{:08X} "
            "ps=0x{:08X}",
            ++logged_unbacked_title_private_exact_allows,
            g_direct_renderer.current_frame_index, guest,
            source->guest_address, PlumeGuestTextureWidth(*source),
            PlumeGuestTextureHeight(*source),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*source)),
            exact_backing->guest_address,
            PlumeGuestTextureWidth(*exact_backing),
            PlumeGuestTextureHeight(*exact_backing),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*exact_backing)),
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*exact_backing)),
            command.resources.render_targets[0],
            command.resources.pixel_shader);
      }
      return;
    }
  }

  DirectPrivateTextureCopyActionMatch exact_action_match =
      DirectPrivateTextureCopyActionMatch::kNone;
  const FablePrivateTextureCopyAction* exact_current_action =
      SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
          command, source_slot, source, &exact_action_match);
  if (exact_current_action &&
      exact_action_match ==
          DirectPrivateTextureCopyActionMatch::kExactCurrentColorAction &&
      exact_current_action->kind != FablePrivateTextureCopyKind::kDepth &&
      DirectPrivateTextureCopyActionProvidesSampleLocked(
          command, *exact_current_action) &&
      ((exact_current_action->target_surface != 0 &&
        exact_current_action->target_surface ==
            command.resources.render_targets[0]) ||
       (exact_current_action->render_target != 0 &&
        exact_current_action->render_target ==
            command.resources.render_targets[0]))) {
    static uint32_t logged_unbacked_title_private_current_action_allows = 0;
    const uint32_t guest = command.resources.textures[source_slot];
    if (logged_unbacked_title_private_current_action_allows < 32u) {
      REXLOG_INFO(
          "Refii plume allowed exact current title/private action #{} "
          "frame={} guest=0x{:08X} tracked=0x{:08X}/{}x{} fmt={} "
          "target=0x{:08X} kind={} serial={} draw={} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_unbacked_title_private_current_action_allows,
          g_direct_renderer.current_frame_index, guest, source->guest_address,
          PlumeGuestTextureWidth(*source), PlumeGuestTextureHeight(*source),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*source)),
          exact_current_action->target_surface,
          FablePrivateTextureCopyKindName(exact_current_action->kind),
          static_cast<unsigned long long>(exact_current_action->serial),
          static_cast<unsigned long long>(exact_current_action->draw_serial),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
    return;
  }
}

bool NearlyEqualExtent(float value, float expected) {
  return std::fabs(value - expected) < 0.5f;
}

bool ShouldRememberDrawnSceneTargetForCommand(
    const FableDirectRenderCommand& command) {
  const bool title_particle_scene_draw = FableTitleCommandIsParticleSceneDraw(
      command, DirectCommandWritesTitleVisibleTargetLocked(command),
      DirectVertexShaderHashLocked(command.resources.vertex_shader),
      DirectPixelShaderHashLocked(command.resources.pixel_shader));
  return !title_particle_scene_draw &&
         !(command.draw.private_texture_rectangle &&
           IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader));
}

bool IsFableTitleGuestViewportExtent(const RendererViewportSnapshot& viewport) {
  if (!viewport.valid) {
    return false;
  }
  // Full display viewport tracks the live guest dims (qword_834A5FE8).
  // The 280/560/1120 title private-rect chain extents are left literal:
  // whether the guest derives that chain from the display dims under a
  // native-resolution override is unverified (RESOLUTION-UNSAFE for
  // supersampling of the title compose chain only — at plain native res the
  // caller's framebuffer==viewport early-out makes them moot).
  return (NearlyEqualExtent(viewport.width, 280.0f) &&
          NearlyEqualExtent(viewport.height, 180.0f)) ||
         (NearlyEqualExtent(viewport.width, 560.0f) &&
          NearlyEqualExtent(viewport.height, 360.0f)) ||
         (NearlyEqualExtent(viewport.width, 1120.0f) &&
          (NearlyEqualExtent(viewport.height, 720.0f) ||
           NearlyEqualExtent(viewport.height, 576.0f))) ||
         (NearlyEqualExtent(viewport.width, FableGuestDisplayWidthF()) &&
          NearlyEqualExtent(viewport.height, FableGuestDisplayHeightF()));
}

void ScaleFableTitleDrawStateToFramebuffer(
    FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer) {
  // ONE SCALER, DERIVED FROM THE TARGET (rewritten 2026-08-04).
  //
  // What this used to do, and why everything was mis-placed: it matched
  // "title-like" draws by shader/source predicates AND required the viewport to
  // be one of a handful of literal guest extents, then scaled by
  // `framebuffer_extent / VIEWPORT_extent` — i.e. it forced whatever viewport it
  // recognised to fill the whole attachment. Two failures follow directly:
  //   * a draw the predicates did not recognise kept a GUEST-space viewport on
  //     an output-sized attachment, so it landed in the top-left corner at the
  //     wrong size (the 1120x720-in-1920x1080 shape RenderDoc showed);
  //   * a draw that legitimately renders into a SUB-RECT of its target had that
  //     sub-rect stretched to full size, because the viewport was used as the
  //     denominator instead of the target.
  // It was also a whitelist of literal extents, which is exactly what this
  // project has repeatedly had to unwind.
  //
  // The exact factor is `attachment_extent / guest_target_extent`. That is a
  // property of the TARGET, not of the draw, so it needs no predicate: every
  // draw into a given attachment gets the same factor, and a surface that was
  // never promoted reports guest == host and scales by 1. The guest extent
  // survives promotion now because PromoteFableRenderSurfaceToOutputExtent
  // records it (promoted_from_guest_*).
  //
  // Still gated on `viewport_guest_space`: a viewport that is already in
  // attachment space must not be scaled again. That is the double-scale this
  // codebase hit before (run 552: scissor 3292x2025 on a 480x270 target).
  if (framebuffer.width == 0 || framebuffer.height == 0 ||
      framebuffer.guest_width == 0 || framebuffer.guest_height == 0 ||
      !command.viewport_guest_space) {
    return;
  }
  const float scale_x = static_cast<float>(framebuffer.width) /
                        static_cast<float>(framebuffer.guest_width);
  const float scale_y = static_cast<float>(framebuffer.height) /
                        static_cast<float>(framebuffer.guest_height);
  if (!std::isfinite(scale_x) || !std::isfinite(scale_y) || scale_x <= 0.0f ||
      scale_y <= 0.0f) {
    return;
  }
  if (!command.viewport.valid || command.viewport.width <= 0.0f ||
      command.viewport.height <= 0.0f) {
    return;
  }
  // A GUEST-SPACE VIEWPORT CANNOT EXCEED ITS GUEST RENDER TARGET (2026-08-04).
  //
  // D3D9 requires the viewport to lie within the bound render target, so a
  // guest-space viewport larger than the guest target extent is not a viewport
  // we read correctly — it is the fabricated number. Run 563, exactly:
  //   SCALE #15 fb=480x270 guest=280x180 vp 1433.0x737.0 -> 2456.6x1105.5
  // 1433x737 is a decoded ALLOCATOR ADDRESS, not an extent (see the
  // private-rect extent note), and scaling it produced a viewport five times
  // its own attachment. Clamp to the guest target first; the scale then maps a
  // legal guest rect onto a legal attachment rect by construction, and the
  // scissor clamp below never has to fire for this cause again.
  const float guest_w = static_cast<float>(framebuffer.guest_width);
  const float guest_h = static_cast<float>(framebuffer.guest_height);
  if (command.viewport.x < 0.0f || command.viewport.y < 0.0f ||
      command.viewport.x + command.viewport.width > guest_w ||
      command.viewport.y + command.viewport.height > guest_h) {
    static uint32_t logged_viewport_clamp = 0;
    if (logged_viewport_clamp < 24u) {
      ++logged_viewport_clamp;
      REXLOG_WARN(
          "Refii plume viewport CLAMPED to guest target #{} frame={} "
          "guest={}x{} vp {:.1f},{:.1f} {:.1f}x{:.1f} rt=0x{:08X} — a "
          "guest-space viewport outside its own target is a fabricated extent, "
          "not a viewport",
          logged_viewport_clamp, g_direct_renderer.current_frame_index,
          framebuffer.guest_width, framebuffer.guest_height, command.viewport.x,
          command.viewport.y, command.viewport.width, command.viewport.height,
          framebuffer.color_address);
    }
    command.viewport.x = std::clamp(command.viewport.x, 0.0f, guest_w);
    command.viewport.y = std::clamp(command.viewport.y, 0.0f, guest_h);
    command.viewport.width =
        std::clamp(command.viewport.width, 0.0f, guest_w - command.viewport.x);
    command.viewport.height =
        std::clamp(command.viewport.height, 0.0f, guest_h - command.viewport.y);
    if (command.viewport.width <= 0.0f || command.viewport.height <= 0.0f) {
      return;
    }
  }
  // The scale==1 early-out MUST come after the clamp above, never before it
  // (moved 2026-08-04). A private-rect target minted by
  // EnsurePrivateRectRenderTargetSurfaceLocked is sized FROM the command
  // viewport, so its `guest_width == width` and the factor is exactly 1 — which
  // used to return here before the viewport was ever validated against its own
  // target. That is precisely the case where validation matters most: the whole
  // point is that the viewport was fabricated. Clamp first, then decide there is
  // no scaling to do.
  if (scale_x == 1.0f && scale_y == 1.0f) {
    return;
  }

  static uint32_t logged_scale = 0;
  if (logged_scale < 16u) {
    ++logged_scale;
    REXLOG_INFO(
        "Refii plume viewport SCALE #{} frame={} fb={}x{} guest={}x{} "
        "scale={:.4f}/{:.4f} vp {:.1f},{:.1f} {:.1f}x{:.1f} -> {:.1f},{:.1f} "
        "{:.1f}x{:.1f} rt=0x{:08X}",
        logged_scale, g_direct_renderer.current_frame_index, framebuffer.width,
        framebuffer.height, framebuffer.guest_width, framebuffer.guest_height,
        scale_x, scale_y, command.viewport.x, command.viewport.y,
        command.viewport.width, command.viewport.height,
        command.viewport.x * scale_x, command.viewport.y * scale_y,
        command.viewport.width * scale_x, command.viewport.height * scale_y,
        framebuffer.color_address);
  }
  command.viewport.x *= scale_x;
  command.viewport.y *= scale_y;
  command.viewport.width *= scale_x;
  command.viewport.height *= scale_y;
  if (command.scissor.valid) {
    // The scissor shares the viewport's space — we returned above unless
    // `viewport_guest_space`, so both are guest-space and take the same factor.
    // Scaling only one of them is what produced `sc=1:0,0,3292,2025` on a
    // 480x270 framebuffer in run 552 (the scissor had already been scaled once
    // by the observer, then again here by viewport-derived factors).
    command.scissor.left = static_cast<int32_t>(
        std::floor(static_cast<float>(command.scissor.left) * scale_x));
    command.scissor.top = static_cast<int32_t>(
        std::floor(static_cast<float>(command.scissor.top) * scale_y));
    command.scissor.right = static_cast<int32_t>(
        std::ceil(static_cast<float>(command.scissor.right) * scale_x));
    command.scissor.bottom = static_cast<int32_t>(
        std::ceil(static_cast<float>(command.scissor.bottom) * scale_y));
    // CLAMP UNCONDITIONALLY. A scissor can never legitimately exceed its own
    // render target, whatever produced it — and one that does is a direct
    // candidate for the Xid 13 ZT violation, since it lets rasterization
    // address outside the bound attachments. Clamping only ever removes
    // clipping we had no right to skip.
    const int32_t max_x = static_cast<int32_t>(framebuffer.width);
    const int32_t max_y = static_cast<int32_t>(framebuffer.height);
    const bool overflowed =
        command.scissor.right > max_x || command.scissor.bottom > max_y;
    command.scissor.left = std::clamp(command.scissor.left, 0, max_x);
    command.scissor.top = std::clamp(command.scissor.top, 0, max_y);
    command.scissor.right = std::clamp(command.scissor.right, 0, max_x);
    command.scissor.bottom = std::clamp(command.scissor.bottom, 0, max_y);
    if (overflowed) {
      static uint32_t logged_scissor_overflow = 0;
      if (logged_scissor_overflow < 24u) {
        REXLOG_WARN(
            "Refii plume scissor CLAMPED to framebuffer #{} frame={} "
            "fb={}x{} guest_space={} scale={:.3f}/{:.3f} -> {},{}..{},{} "
            "(a scissor larger than its target is a ZT-violation candidate)",
            ++logged_scissor_overflow, g_direct_renderer.current_frame_index,
            framebuffer.width, framebuffer.height,
            command.viewport_guest_space ? 1 : 0, scale_x, scale_y,
            command.scissor.left, command.scissor.top, command.scissor.right,
            command.scissor.bottom);
      }
    }
  }
}

PlumeTrackedResource* FindDirectReadyShaderResourceByIdentityLocked(
    uint32_t shader, FableGuestResourceKind expected_kind) {
  PlumeTrackedResource* direct = FindDirectResourceLocked(shader);
  if (direct && PlumeGuestShaderReady(*direct) &&
      direct->kind == expected_kind) {
    return direct;
  }
  if (EnsureDirectKnownShaderResourceLocked(shader)) {
    direct = FindDirectResourceLocked(shader);
    if (direct && PlumeGuestShaderReady(*direct) &&
        direct->kind == expected_kind) {
      return direct;
    }
  }

  const FableShaderIdentity* identity =
      FableDirectFindShaderIdentityByObject(shader);
  if (!identity || identity->kind != expected_kind ||
      identity->bytecode_hash == 0) {
    return nullptr;
  }

  PlumeTrackedResource* exact = nullptr;
  uint32_t match_count = 0;
  if (const std::vector<uint32_t>* objects =
          FableDirectFindShaderIdentityObjectsByHash(identity->bytecode_hash)) {
    for (uint32_t object : *objects) {
      const FableShaderIdentity* sibling =
          FableDirectFindShaderIdentityByObject(object);
      if (!sibling || sibling->kind != expected_kind ||
          sibling->bytecode_hash != identity->bytecode_hash) {
        continue;
      }
      PlumeTrackedResource* candidate = FindDirectResourceLocked(object);
      if (!candidate || !PlumeGuestShaderReady(*candidate) ||
          candidate->kind != expected_kind ||
          candidate->guest_shader.bytecode_hash != identity->bytecode_hash) {
        continue;
      }
      exact = candidate;
      ++match_count;
    }
  }
  if (match_count == 1u) {
    return exact;
  }
  if (match_count > 1u) {
    static uint32_t logged_exact_ambiguous_shader_identity_rejects = 0;
    if (logged_exact_ambiguous_shader_identity_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact ambiguous shader identity resource reject #{} "
          "shader=0x{:08X} kind={} matches={} hash=0x{:016X} "
          "candidate=0x{:08X}",
          ++logged_exact_ambiguous_shader_identity_rejects, shader,
          TrackedResourceKindName(expected_kind), match_count,
          identity->bytecode_hash, exact ? exact->guest_address : 0u);
    }
  }
  return nullptr;
}

PlumeTrackedResource* FindDirectReadyDeclarationResourceByIdentityLocked(
    uint32_t declaration) {
  PlumeTrackedResource* direct = FindDirectResourceLocked(declaration);
  if (direct && PlumeGuestDeclarationReady(*direct)) {
    return direct;
  }

  const FableVertexDeclarationIdentity* identity =
      FableDirectFindVertexDeclarationIdentityByObject(declaration);
  if (!identity || identity->elements_hash == 0) {
    return nullptr;
  }

  PlumeTrackedResource* exact = nullptr;
  uint32_t match_count = 0;
  if (const std::vector<uint32_t>* objects =
          FableDirectFindVertexDeclarationIdentityObjectsByHash(
              identity->elements_hash)) {
    for (uint32_t object : *objects) {
      const FableVertexDeclarationIdentity* sibling =
          FableDirectFindVertexDeclarationIdentityByObject(object);
      if (!sibling || sibling->elements_hash != identity->elements_hash) {
        continue;
      }
      PlumeTrackedResource* candidate = FindDirectResourceLocked(object);
      if (!candidate || !PlumeGuestDeclarationReady(*candidate) ||
          candidate->guest_vertex_declaration.elements_hash !=
              identity->elements_hash) {
        continue;
      }
      exact = candidate;
      ++match_count;
    }
  }
  if (match_count == 1u) {
    return exact;
  }
  if (match_count > 1u) {
    static uint32_t logged_exact_ambiguous_declaration_identity_rejects = 0;
    if (logged_exact_ambiguous_declaration_identity_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact ambiguous declaration identity resource reject #{} "
          "declaration=0x{:08X} matches={} hash=0x{:016X} candidate=0x{:08X}",
          ++logged_exact_ambiguous_declaration_identity_rejects,
          declaration, match_count, identity->elements_hash,
          exact ? exact->guest_address : 0u);
    }
  }
  return nullptr;
}

void EnsureDirectKnownShaderResourcesForCommandLocked(
    const FableDirectRenderCommand& command) {
  EnsureDirectKnownShaderResourceLocked(command.resources.vertex_shader);
  EnsureDirectKnownShaderResourceLocked(command.resources.pixel_shader);
  EnsureDirectKnownVertexDeclarationResourceLocked(
      command.resources.vertex_declaration);
}


bool IsGtlPreDispatchBeginPacket(const refii::gtl::DrawPacket& packet) {
  return packet.source_symbol == 0x82205F68u ||
         packet.source_symbol == 0x82217DB8u;
}

#endif

}  // namespace refii::renderer
