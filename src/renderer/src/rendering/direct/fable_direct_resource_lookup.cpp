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

DirectResolveCopyRecordResult RecordDirectResolveCopyCommandLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command) {
  auto log_resolve_failure = [&](const char* reason, uint32_t destination_base,
                                 uint32_t copy_control,
                                 uint32_t source_slot,
                                 uint32_t source_address,
                                 const PlumeTrackedResource* source) {
    if (g_direct_renderer.diagnostics.logged_resolve_copy_failures >= 32u) {
      return;
    }
    REXLOG_INFO(
        "Refii plume direct resolve copy failed #{} frame={} reason={} "
        "dest=0x{:08X} normalized=0x{:08X} copy_control=0x{:08X} "
        "slot={} source=0x{:08X} source_ready={} rt0=0x{:08X} "
        "rt1=0x{:08X} rt2=0x{:08X} rt3=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X} serial={}",
        ++g_direct_renderer.diagnostics.logged_resolve_copy_failures,
        g_direct_renderer.current_frame_index, reason, destination_base,
        NormalizeFableResolveDestinationBase(destination_base), copy_control,
        source_slot, source_address,
        source && PlumeGuestImageReady(*source) ? 1 : 0,
        command.resources.render_targets[0], command.resources.render_targets[1],
        command.resources.render_targets[2], command.resources.render_targets[3],
        command.resources.depth_stencil, command.resources.vertex_shader,
        command.resources.pixel_shader,
        static_cast<unsigned long long>(command.serial));
  };

  const uint32_t destination_base = DirectResolveCopyDestinationBase(command);
  if (destination_base == 0) {
    log_resolve_failure("missing-destination", destination_base, 0, 0, 0,
                        nullptr);
    return {false, false};
  }

  const uint32_t copy_control = DirectResolveCopyControl(command);
  const DirectResolveCopySourceSelection selection =
      SelectDirectResolveCopySourceLocked(command);
  if (!selection.ready) {
    log_resolve_failure(!selection.source ? "missing-source" : "source-not-ready",
                        destination_base, copy_control, selection.source_slot,
                        selection.source_address, selection.source);
    return {false, false};
  }
  const uint32_t effective_copy_control =
      (copy_control & ~0x7u) | (selection.source_slot & 0x7u);

  const uint32_t normalized_base =
      NormalizeFableResolveDestinationBase(destination_base);
  FableRenderTargetResolveAlias alias;
  alias.destination_base = destination_base;
  alias.normalized_destination_base = normalized_base;
  alias.source_address = selection.source->guest_address;
  alias.source_slot = selection.source_slot;
  alias.destination_pitch = command.resolve.rb_copy_dest_pitch != 0
                                ? command.resolve.rb_copy_dest_pitch
                                : command.pipeline.pm4.rb_copy_dest_pitch;
  alias.destination_info = command.resolve.rb_copy_dest_info != 0
                               ? command.resolve.rb_copy_dest_info
                               : command.pipeline.pm4.rb_copy_dest_info;
  alias.copy_control = effective_copy_control;
  alias.source_width = PlumeGuestTextureWidth(*selection.source);
  alias.source_height = PlumeGuestTextureHeight(*selection.source);
  alias.display_width = alias.source_width;
  alias.display_height = alias.source_height;
  alias.serial = command.serial;
  alias.depth = selection.depth_resolve;
  alias.display_qualified = !selection.depth_resolve;
  g_plume_resources.render_target_resolve_aliases[normalized_base] = alias;
  g_plume_resources.render_target_resolve_aliases[destination_base] = alias;
  // §4 bring-up probe: do resolves write the scratch bases the private-rect
  // blits sample (0x12AE1000 / 0x12CC7000 / 0x12CFD000, run 432)? If so the
  // resolve alias — updated in command order — already names this page's
  // content, and the blit's source can come from it instead of the proxy.
  static uint32_t logged_resolve_destinations = 0;
  if (logged_resolve_destinations < 32u) {
    REXLOG_INFO(
        "Refii resolve dest probe #{} frame={} serial={} dest=0x{:08X} "
        "normalized=0x{:08X} source=0x{:08X}/{}x{} depth={}",
        ++logged_resolve_destinations, g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial), destination_base,
        normalized_base, selection.source->guest_address, alias.source_width,
        alias.source_height, selection.depth_resolve ? 1 : 0);
  }

  PlumeTrackedResource* destination =
      FindDirectResourceLocked(destination_base);
  if (!destination) {
    destination = FindDirectResourceLocked(normalized_base);
  }
  // A resolve destination is a memory base, not a D3D object address, so the
  // address lookup can land on an unrelated record aliasing that memory (with
  // an arbitrary shape) while the texture the game will sample sits at the
  // same base under another object address. When the picked record's extent
  // does not match the resolve source, prefer an image resource at the same
  // base whose extent does.
  if (!selection.depth_resolve &&
      (!destination ||
       !DirectTextureCopySizeMatches(*selection.source, *destination))) {
    for (auto& [resource_address, resource] : g_direct_renderer.resources) {
      (void)resource_address;
      if (&resource == selection.source || !IsImageResource(resource.kind) ||
          resource.texture_render_target) {
        continue;
      }
      if (!DirectResourceMatchesAddress(resource, destination_base) &&
          !DirectResourceMatchesAddress(resource, normalized_base)) {
        continue;
      }
      if (!DirectTextureCopySizeMatches(*selection.source, resource)) {
        continue;
      }
      if (g_direct_renderer.diagnostics.logged_resolve_copy_failures < 32u) {
        REXLOG_INFO(
            "Refii plume direct resolve destination base-match correction "
            "frame={} dest=0x{:08X} normalized=0x{:08X} previous=0x{:08X}/{}x{} "
            "corrected=0x{:08X}/{}x{} source=0x{:08X}/{}x{}",
            g_direct_renderer.current_frame_index, destination_base,
            normalized_base, destination ? destination->guest_address : 0u,
            destination ? PlumeGuestTextureWidth(*destination) : 0u,
            destination ? PlumeGuestTextureHeight(*destination) : 0u,
            resource.guest_address, PlumeGuestTextureWidth(resource),
            PlumeGuestTextureHeight(resource), selection.source->guest_address,
            PlumeGuestTextureWidth(*selection.source),
            PlumeGuestTextureHeight(*selection.source));
      }
      destination = &resource;
      break;
    }
  }
  if (!destination && command_list) {
    destination = EnsureDirectResolveDestinationResourceLocked(
        g_direct_renderer.device, command, selection, destination_base,
        normalized_base);
  }
  if (destination && command_list &&
      !DirectTextureCopyCanRecordLocked(command_list, *selection.source,
                                        *destination)) {
    destination = EnsureDirectResolveDestinationResourceLocked(
        g_direct_renderer.device, command, selection, destination_base,
        normalized_base, true);
  }
  bool copied_destination = false;
  if (destination) {
    if (DirectTextureCopyCanRecordLocked(command_list, *selection.source,
                                         *destination)) {
      MaybeBeginRenderDocResolveTargetCaptureLocked(command);
    }
    copied_destination =
        RecordDirectTextureCopyLocked(command_list, *selection.source,
                                      *destination);
    if (copied_destination) {
      RegisterDirectTextureIdentityFromResourceLocked(
          *destination, PlumeGuestTextureFetchDwords(*destination));
    }
  }
  RegisterDirectPm4ResolveSurfaceTextureLinkLocked(
      command, *selection.source, copied_destination ? destination : nullptr,
      destination_base, normalized_base, selection.source_slot,
      selection.depth_resolve);

  if (!g_direct_renderer.diagnostics.logged_first_resolve_alias) {
    REXLOG_INFO(
        "Refii plume direct renderer tracked PM4 resolve source=0x{:08X} "
        "dest=0x{:08X} normalized=0x{:08X} slot={}",
        selection.source->guest_address, destination_base, normalized_base,
        selection.source_slot);
    g_direct_renderer.diagnostics.logged_first_resolve_alias = true;
  }
  return {true, copied_destination};
}

void AddDirectResourceInterest(std::unordered_set<uint32_t>& resources,
                               uint32_t address) {
  if (address != 0) {
    if (DirectAddressInFableD3DDeviceMirror(address)) {
      return;
    }
    if (address >= 0xF1000000u) {
      const PlumeTrackedResource* local_resource =
          FindDirectResourceLocked(address);
      if (local_resource && local_resource->plume_owned &&
          PlumeGuestImageReady(*local_resource)) {
        return;
      }
    }
    resources.insert(address);
  }
}

void AddDirectShaderIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter, uint32_t shader) {
  if (shader == 0) {
    return;
  }
  const FableShaderIdentity* identity =
      FableDirectFindShaderIdentityByObject(shader);
  if (!identity) {
    return;
  }
  AddDirectResourceInterest(resource_filter, identity->object_shader);
  AddDirectResourceInterest(resource_filter, identity->parent_shader);
  AddDirectResourceInterest(resource_filter, identity->bytecode_address);
}


void AddDirectExactTextureActionInterestsLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter) {
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      slot >= command.resources.textures.size()) {
    return;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const uint32_t guest = command.resources.textures[slot];
  if (guest == 0) {
    return;
  }

  auto add_sample_interest = [&](uint32_t address) {
    AddDirectResourceInterest(resource_filter, address);
    AddDirectResourceInterest(texture_upload_filter, address);
  };

  auto add_surface_link_interest = [&](const FableSurfaceTextureLink* link)
      -> bool {
    if (!link || (link->source_slot != kUnknownSlot &&
                  link->source_slot != slot)) {
      return false;
    }
    if (link->frame_index != 0 &&
        link->frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (link->frame_index != 0 &&
        link->frame_index == g_direct_renderer.current_frame_index &&
        link->draw_serial != 0 && command.draw_serial != 0 &&
        link->draw_serial > command.draw_serial) {
      return false;
    }

    add_sample_interest(link->source_surface);
    if (link->backing_texture != 0 || link->backing_base_address != 0 ||
        FableDirectTextureFetchKey(link->backing_fetch_dwords) != 0) {
      add_sample_interest(link->backing_texture);
      add_sample_interest(link->backing_base_address);
      return true;
    }

    add_sample_interest(link->texture);
    add_sample_interest(link->texture_base_address);
    return true;
  };

  PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);
  if (!DirectCommandUsesFablePrivateTexturePath(command, slot,
                                                direct_texture)) {
    return;
  }

  bool added_exact_interest = false;
  auto add_destination_link_interest = [&](uint32_t destination_texture) {
    if (destination_texture == 0) {
      return;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
            destination_texture, slot);
    if (!link) {
      link = FableDirectFindSurfaceTextureLinkForDestinationTexture(
          destination_texture);
    }
    if (add_surface_link_interest(link)) {
      added_exact_interest = true;
    }
  };

  add_destination_link_interest(guest);
  if (direct_texture) {
    add_destination_link_interest(direct_texture->guest_address);
    add_destination_link_interest(direct_texture->parent_address);
    if (!added_exact_interest) {
      add_sample_interest(PlumeGuestTextureBaseAddress(*direct_texture));
    }
  }
  if (const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(guest)) {
    add_destination_link_interest(identity->object_texture);
    add_destination_link_interest(identity->parent_texture);
    if (!added_exact_interest) {
      if (add_surface_link_interest(
              FableDirectFindSurfaceTextureLinkByBase(identity->base_address)) ||
          add_surface_link_interest(
              FableDirectFindSurfaceTextureLinkByFetch(identity->fetch_dwords))) {
        added_exact_interest = true;
      }
    }
  }
  if (!added_exact_interest) {
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    add_surface_link_interest(
        FableDirectFindSurfaceTextureLinkByFetch(command_fetch));
  }
}

void AddDirectVertexDeclarationIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter, uint32_t declaration) {
  if (declaration == 0) {
    return;
  }
  const FableVertexDeclarationIdentity* identity =
      FableDirectFindVertexDeclarationIdentityByObject(declaration);
  if (!identity) {
    return;
  }
  AddDirectResourceInterest(resource_filter, identity->object_declaration);
  AddDirectResourceInterest(resource_filter, identity->parent_declaration);
  AddDirectResourceInterest(resource_filter, identity->elements_address);
}

bool ShouldReplaceTrackedSceneTarget(uint32_t candidate_width,
                                        uint32_t candidate_height,
                                        uint32_t remembered_width,
                                        uint32_t remembered_height,
                                        uint32_t candidate_target,
                                        uint32_t remembered_target) {
  if (remembered_target == 0 || remembered_width == 0 ||
      remembered_height == 0 || candidate_target == remembered_target) {
    return true;
  }

  const uint64_t candidate_area =
      uint64_t(candidate_width) * uint64_t(candidate_height);
  const uint64_t remembered_area =
      uint64_t(remembered_width) * uint64_t(remembered_height);
  if (candidate_area >= remembered_area) {
    return true;
  }

  // Fable often draws small helper/private-rect targets after the actual
  // scene. Keep those from becoming the present source once a real scene
  // target has been seen, while still allowing moderate resolution changes.
  return candidate_area * 4u >= remembered_area;
}


bool IsFableTitleGuiQuadDeclaration(uint32_t declaration) {
  return DirectVertexDeclarationMatchesHashLocked(
      declaration, kFableGuiQuadDeclarationHash);
}

bool IsFableTitleGuiSingleTexturePostprocessPixelShader(uint32_t shader) {
  return ::refii::renderer::IsFableTitleGuiSingleTexturePostprocessPixelShader(
      shader, DirectPixelShaderHashLocked(shader));
}

uint32_t FableTitleGuiFetchBackedTextureSlotLocked(
    const FableDirectRenderCommand& command) {
  constexpr uint32_t kFetchBackedTitleGuiSlot = 8u;
  constexpr uint32_t kSlot0Mask = 1u << 0;
  if (command.draw.source != RendererDrawEventSource::kSceneObject ||
      !IsFableTitleGuiQuadDeclaration(command.resources.vertex_declaration) ||
      ResolveDirectInputSlotStrideLocked(command, 0) != 8u ||
      !IsFableTitleGuiSingleTexturePostprocessPixelShader(
          command.resources.pixel_shader) ||
      DirectVertexShaderHashLocked(command.resources.vertex_shader) !=
          kFableTitleGuiText3DVertexShaderHash ||
      kFetchBackedTitleGuiSlot >= command.resources.textures.size()) {
    return UINT32_MAX;
  }

  const uint64_t pixel_shader_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  if (FableKnownPixelShaderTextureSlotMask(command.resources.pixel_shader,
                                           pixel_shader_hash) == kSlot0Mask) {
    return UINT32_MAX;
  }

  const std::array<uint32_t, 6> fetch =
      DirectCommandTextureFetchDwords(command, kFetchBackedTitleGuiSlot);
  return HasTrackedFetchDwords(fetch) ? kFetchBackedTitleGuiSlot : UINT32_MAX;
}

bool DirectCommandWritesTitleVisibleTargetLocked(
    const FableDirectRenderCommand& command) {
  return FableTitleCommandWritesVisibleTarget(
      command,
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]));
}

bool DirectCommandIsExactTitleBillboardAtlasLocked(
    const FableDirectRenderCommand& command) {
  if (command.draw.source != RendererDrawEventSource::kSceneObject ||
      !DirectCommandWritesTitleVisibleTargetLocked(command) ||
      !IsFableTitleSceneVertexShaderCommandLocked(command)) {
    return false;
  }
  if (DirectPixelShaderHashLocked(command.resources.pixel_shader) !=
      kFableTitleTreeBillboardPixelHash) {
    return false;
  }
  if (!DirectVertexDeclarationMatchesHashLocked(
          command.resources.vertex_declaration,
          kFableTitleIndexedDeclarationHash)) {
    return false;
  }
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (command.resources.textures[slot] != 0 &&
        IsFableTitleBillboardAtlasCommandSlotLocked(command, slot)) {
      return true;
    }
  }
  return false;
}

bool DirectCommandIsTitleRepeatedMeshOverlayLocked(
    const FableDirectRenderCommand& command) {
  return FableTitleCommandIsRepeatedMeshOverlay(
      command, DirectCommandWritesTitleVisibleTargetLocked(command),
      DirectVertexShaderHashLocked(command.resources.vertex_shader),
      DirectPixelShaderHashLocked(command.resources.pixel_shader));
}

bool DirectCommandIsTitleGuiStripProofCandidateLocked(
    const FableDirectRenderCommand& command) {
  return FableTitleCommandIsGuiStripProofCandidate(
      command,
      IsFableTitleGuiQuadDeclaration(command.resources.vertex_declaration),
      DirectVertexShaderHashLocked(command.resources.vertex_shader),
      DirectPixelShaderHashLocked(command.resources.pixel_shader));
}

bool DirectCommandIsTitleGuiOverdrawVisualiseLocked(
    const FableDirectRenderCommand& command) {
  return FableTitleCommandIsGuiOverdrawVisualise(
      command, IsDirectDrawCommand(command),
      IsFableTitleGuiTextAtlasCommandLocked(command, source_slot),
      DirectVertexShaderHashLocked(command.resources.vertex_shader),
      DirectPixelShaderHashLocked(command.resources.pixel_shader));
}

bool DirectCommandIsTitleVisibleProofCandidateLocked(
    const FableDirectRenderCommand& command) {
  return FableTitleCommandIsVisibleProofCandidate(
      command, IsDirectDrawCommand(command),
      DirectCommandWritesTitleVisibleTargetLocked(command),
      DirectCommandIsTitleGuiOverdrawVisualiseLocked(command),
      DirectCommandIsTitleRepeatedMeshOverlayLocked(command),
      DirectVertexShaderHashLocked(command.resources.vertex_shader),
      DirectPixelShaderHashLocked(command.resources.pixel_shader));
}

uint32_t DirectCommandPixelTextureSlotMaskLocked(
    const FableDirectRenderCommand& command) {
  // T3 kGtlTessDraw (risk R3 fix, 2026-08-02): every mask this function
  // derives comes from the PIXEL shader's declared slots, but tessellated
  // patch draws are driven by VERTEX-stage textures s16-s19 (the heightfield/
  // normal sources the guest VS tfetches — no vertex streams exist on these
  // draws), which no pixel-derived mask can ever contain. Treat every slot as
  // live, the kPixVideo precedent below: the sync/transition paths and
  // FillDirectSharedConstants only act on slots that actually resolve to a
  // tracked texture, so an all-ones mask binds exactly the stated slots.
  if (command.type == FableDirectRenderCommandType::kGtlTessDraw) {
    return std::numeric_limits<uint32_t>::max();
  }
  // TREE-CHECKPOINT (2026-07-25): unconditional, content-identity-gated (pixel
  // shader hash only -- no draw-order/counter dependence, per the lesson in
  // refii-tree-billboard-stale-default-override.md that shared/ordered
  // diagnostics get starved by unrelated high-frequency draws). Answers
  // whether genuine tree-billboard packets even reach this function and, if
  // so, why the outer gate (indexed-declaration + scene-target/vertex-shader
  // check) does or doesn't let them through to kTreeBillboardSlots.
  if (DirectPixelShaderHashLocked(command.resources.pixel_shader) ==
      kFableTitleTreeBillboardPixelHash) {
    static uint32_t logged_tree_checkpoint = 0;
    // Cap raised 200->8000 (2026-07-25): the 200-entry cap exhausted by
    // frame~2113 in run250, going silent well before the RenderDoc capture
    // frame (2156) this checkpoint exists to explain -- see
    // refii-tree-billboard-stale-default-override.md follow-up. Need
    // coverage that survives to whatever frame the next capture lands on.
    if (logged_tree_checkpoint < 64u) {
      ++logged_tree_checkpoint;
      const uint32_t tex13_probe =
          source_slot < command.resources.textures.size()
              ? command.resources.textures[source_slot]
              : 0u;
      REXLOG_INFO(
          "Refii GTL TREE-CHECKPOINT #{} source={} tex13={:#010x} "
          "scene_target={} scene_vs={} indexed_decl={}",
          logged_tree_checkpoint, static_cast<int>(command.draw.source),
          tex13_probe,
          IsFableTitleSceneTargetLocked(command.resources.render_targets[0])
              ? 1
              : 0,
          IsFableTitleSceneVertexShaderCommandLocked(command) ? 1 : 0,
          DirectVertexDeclarationMatchesHashLocked(
              command.resources.vertex_declaration,
              kFableTitleIndexedDeclarationHash)
              ? 1
              : 0);
    }
  }
  if (command.draw.source == RendererDrawEventSource::kPixVideo) {
    return std::numeric_limits<uint32_t>::max();
  }
  constexpr uint32_t kSlot0 = 1u << 0;
  constexpr uint32_t kSlot13 = 1u << source_slot;
  constexpr uint32_t kSlots012 = (1u << 0) | (1u << 1) | (1u << 2);
  constexpr uint32_t kTreeBillboardSlots =
      (1u << 13) | (1u << 14) | (1u << 15);
  constexpr uint32_t kOverdrawVisualiseSlots = (1u << 13) | (1u << 14);
  constexpr uint32_t kPrivateRectCopySlot =
      kSlot13;
  if (DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return kSlots012;
  }
  if (IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
    return kPrivateRectCopySlot;
  }
  if (DirectCommandIsTitleRepeatedMeshOverlayLocked(command)) {
    return kSlot0 | (1u << 1);
  }

  // DERIVED 2026-07-26 (census: texture_slot_mask_slot0_ps). Was two
  // pixel-shader OBJECT pointers captured from one title run. Collapsing the
  // mask to slot 0 is real behaviour — it decides which guest texture slots are
  // synced and bound for the draw — so at gameplay the address form would have
  // stopped applying silently.
  //
  // The derived condition is the shader's own payload identity:
  //   0x4215A710 -> 0170_pshader_postprocessing_radialblur
  //   0x4216D090 -> 0160_pshader_postprocessing_downscale2
  // Both are single-source full-screen postprocess passes; their translated
  // HLSL declares one named sampler (g_TextureSampler) at shared-constant
  // offset 0 and no other, i.e. guest texture slot 0 is the ONLY slot either
  // shader fetches from. Slot usage is a property of the microcode, not of
  // where the runtime happened to allocate the shader object.
  //
  // Census call INVERTED, same convention as scene_private_copy_ps above:
  //   matched  — hash decisions (run 288 baseline: 40245; a DROP is a defect)
  //   derived  — matches the retired address list also covered (expect 40245)
  //   lit_only — hash-only matches, i.e. the widening (expect 0 at title)
  const uint64_t slot0_pixel_shader_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  const bool slot0_pixel_shader_derived =
      IsFableSingleSourcePostprocessPixelShaderHash(slot0_pixel_shader_hash);
  const bool slot0_pixel_shader_retired_literal =
      command.resources.pixel_shader == 0x4215A710u ||
      command.resources.pixel_shader == 0x4216D090u;
  if (DirectAddressGateTest(
          DirectAddressGate::kTextureSlotMaskSlot0PixelShader,
          slot0_pixel_shader_derived, command.resources.pixel_shader,
          slot0_pixel_shader_hash, slot0_pixel_shader_retired_literal)) {
    return kSlot0;
  }

  if (const uint32_t fetch_backed_slot =
          FableTitleGuiFetchBackedTextureSlotLocked(command);
      fetch_backed_slot != UINT32_MAX && fetch_backed_slot < 32u) {
    return 1u << fetch_backed_slot;
  }

  // Fable's title/logo/text helpers bind their atlas through guest texture
  // slot 13 even when the replacement HLSL samples the descriptor slot used by
  // the translated shader. Prefer the guest binding for the proven title draw
  // layouts before the generic artifact mask can collapse the command to slot 0.
  //
  // kScratchWrapper accepted alongside kSceneObject: IDA-traced (2026-07-25)
  // the tree-billboard draw's only BeginIndexedVertices caller is
  // Render_DrawIndexedQuadUP_INFERRED (sub_8220A3F0) -- the SAME generic
  // "draw one indexed quad" utility used for Bink video and UI composites.
  // The root symbol carries no per-purpose identity (every caller supplies
  // its own vertex format/stride/shader pair), so this draw is ALWAYS
  // kScratchWrapper, never kSceneObject, by construction on both the observer
  // and GTL sides -- there is no separate "tree renderer" function to
  // classify differently. DirectCommandIsTreeBillboardDrawTargetLocked
  // (below) already independently accepted kScratchWrapper for exactly this
  // reason; this gate needed to match so slot 13 actually gets populated for
  // that check to have anything to look at. The specific vertex+pixel shader
  // hash checks inside this block (title_billboard_atlas etc.) are the real
  // discriminator and are unaffected by widening the source check -- unrelated
  // kScratchWrapper draws (Bink, UI panels) simply won't match those hashes.
  if (source_slot < command.resources.textures.size() &&
      (command.draw.source == RendererDrawEventSource::kSceneObject ||
       command.draw.source == RendererDrawEventSource::kScratchWrapper) &&
      (IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command))) {
    const uint32_t tex13 =
        command.resources.textures[source_slot];
    const uint64_t pixel_hash =
        DirectPixelShaderHashLocked(command.resources.pixel_shader);
    const bool title_private_rect_declaration =
        DirectVertexDeclarationMatchesHashLocked(
            command.resources.vertex_declaration,
            kFablePrivateRectDeclarationHash);
    const bool title_indexed_declaration =
        DirectVertexDeclarationMatchesHashLocked(
            command.resources.vertex_declaration,
            kFableTitleIndexedDeclarationHash);
    const bool title_text_2d =
        title_private_rect_declaration &&
        command.resources.pixel_shader == kFableTitleSceneTextPixelShader &&
        tex13 != 0;
    // DERIVED (census: title_gui_atlas_heap_ps). The pool-boundary guess
    // "pixel shader object lives above 0x4C000000" was DELETED 2026-07-26:
    // run 288 measured tested=30 matched=30 derived=30 lit_only=0, so the
    // overdraw-visualise bytecode hash sitting beside it decided every single
    // match and the heap-range test never contributed. Pool layout is a
    // per-run property; the hash is content-derived. Census call KEPT so a
    // drop from 30 matches falsifies this immediately. (Sample size is small
    // — 30 — but the term removed was a heap-range guess, not a semantic
    // condition, so there is nothing for it to have been standing in for.)
    const bool title_gui_atlas =
        title_private_rect_declaration &&
        ResolveDirectInputSlotStrideLocked(command, 0) == 20u &&
        IsFableTitleGuiTextAtlasObjectAddress(tex13) &&
        DirectAddressGateTest(
            DirectAddressGate::kTitleGuiAtlasHeapPixelShader,
            pixel_hash == kFableTitleOverdrawVisualisePixelHash,
            command.resources.pixel_shader, pixel_hash,
            pixel_hash == kFableTitleOverdrawVisualisePixelHash);
    const bool title_billboard_atlas =
        title_indexed_declaration &&
        (pixel_hash == kFableTitleTreeBillboardPixelHash ||
         IsFableTitleTextureIndirectionChainAddress(tex13));
    if (title_billboard_atlas &&
        pixel_hash == kFableTitleTreeBillboardPixelHash) {
      return kTreeBillboardSlots;
    }
    if (title_gui_atlas &&
        pixel_hash == kFableTitleOverdrawVisualisePixelHash) {
      return kOverdrawVisualiseSlots;
    }
    if (title_text_2d || title_gui_atlas || title_billboard_atlas) {
      return kSlot13;
    }
  }

  const PlumeTrackedResource* pixel_shader =
      FindDirectResourceLocked(command.resources.pixel_shader);
  uint64_t pixel_shader_hash =
      pixel_shader ? pixel_shader->guest_bytecode_hash : 0ull;
  if (pixel_shader_hash == 0) {
    pixel_shader_hash =
        DirectPixelShaderHashLocked(command.resources.pixel_shader);
  }

  const uint32_t known_mask = FableKnownPixelShaderTextureSlotMask(
      command.resources.pixel_shader, pixel_shader_hash);
  if (known_mask != 0) {
    return known_mask;
  }

  if (pixel_shader_hash != 0) {
    RendererShaderLibraryEntry shader_entry;
    if (FindRendererShaderLibraryEntryByGuestHash(pixel_shader_hash,
                                                  shader_entry) &&
        shader_entry.texture_slot_mask_valid &&
        shader_entry.texture_slot_mask != 0) {
      return shader_entry.texture_slot_mask;
    }
  }

  if (pixel_shader && pixel_shader->pixel_shader_texture_slot_mask_valid &&
      pixel_shader->pixel_shader_texture_slot_mask != 0) {
    return pixel_shader->pixel_shader_texture_slot_mask;
  }

  return std::numeric_limits<uint32_t>::max();
}

bool DirectCommandSlotUsesReadyInlinePixVideoPlaneLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    uint32_t texture_guest) {
  return command.draw.source == RendererDrawEventSource::kPixVideo &&
         slot < command.pix_video_planes.size() &&
         command.pix_video_planes[slot].ready() &&
         command.pix_video_planes[slot].address == texture_guest;
}

void AddDirectCommandResourceInterest(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    bool include_exact_texture_action_interests) {
  switch (command.type) {
    case FableDirectRenderCommandType::kClear:
      for (uint32_t render_target : command.resources.render_targets) {
        if (render_target == 0) {
          continue;
        }
        AddDirectResourceInterest(resource_filter, render_target);
        AddDirectClearPrivateTextureCopyOutputInterestLocked(
            command, render_target, false, resource_filter,
            texture_upload_filter);
      }
      if ((command.clear.flags & (kFableClearDepth | kFableClearStencil)) !=
          0) {
        AddDirectResourceInterest(resource_filter,
                                  command.resources.depth_stencil);
        AddDirectClearPrivateTextureCopyOutputInterestLocked(
            command, command.resources.depth_stencil, true, resource_filter,
            texture_upload_filter);
      }
      break;
    case FableDirectRenderCommandType::kDrawPrimitive:
    case FableDirectRenderCommandType::kDrawIndexedPrimitive:
    case FableDirectRenderCommandType::kDrawPrimitiveUP: {
      for (uint32_t render_target : command.resources.render_targets) {
        if (DirectCommandCanMaterializePrivateRectTargetLocked(command,
                                                               render_target)) {
          continue;
        }
        AddDirectResourceInterest(resource_filter, render_target);
      }
      AddDirectResourceInterest(resource_filter, command.resources.depth_stencil);
      AddDirectResourceInterest(resource_filter,
                                command.resources.vertex_declaration);
      AddDirectResourceInterest(resource_filter, command.resources.vertex_shader);
      AddDirectResourceInterest(resource_filter, command.resources.pixel_shader);
      AddDirectVertexDeclarationIdentityResourceInterestsLocked(
          resource_filter, command.resources.vertex_declaration);
      AddDirectShaderIdentityResourceInterestsLocked(
          resource_filter, command.resources.vertex_shader);
      AddDirectShaderIdentityResourceInterestsLocked(
          resource_filter, command.resources.pixel_shader);
      if (!DirectInlineBufferCoversAddress(command.inline_index_buffer,
                                           command.resources.index_buffer)) {
        AddDirectResourceInterest(resource_filter, command.resources.index_buffer);
      }
      for (size_t slot = 0; slot < command.resources.streams.size(); ++slot) {
        const RendererStreamBindingSnapshot& stream =
            command.resources.streams[slot];
        if (ShouldExpandPrimitive8Rectangle(command,
                                            static_cast<uint32_t>(slot)) &&
            command.draw.source == RendererDrawEventSource::kSceneObject) {
          continue;
        }
        const bool inline_ready =
            DirectInlineBufferCoversAddress(command.inline_vertex_buffers[slot],
                                           stream.buffer);
        if (!inline_ready) {
          AddDirectResourceInterest(resource_filter, stream.buffer);
        }
      }
      const uint32_t texture_slot_mask =
          DirectCommandPixelTextureSlotMaskLocked(command);
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (!DirectCommandUsesTextureSlotForSyncLocked(
                command, texture_slot_mask, slot)) {
          continue;
        }
        const uint32_t texture = command.resources.textures[slot];
        if (DirectCommandSlotUsesReadyInlinePixVideoPlaneLocked(
                command, slot, texture)) {
          continue;
        }
        if (PlumeTrackedResource* bink_plane =
                ResolveDirectExactBinkWrapperPlaneForSlotLocked(command, slot)) {
          // Bink planes are re-published on every decoded movie frame; a
          // resolved plane still needs per-frame sync or its GPU texture
          // freezes on the last synced frame (black/stale movie).
          AddDirectResourceInterest(resource_filter,
                                    bink_plane->guest_address);
          AddDirectResourceInterest(texture_upload_filter,
                                    bink_plane->guest_address);
          continue;
        }
        if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
          AddDirectExactBinkWrapperPlaneResourceInterestsLocked(
              resource_filter, texture_upload_filter, command, slot);
          continue;
        }
        PlumeTrackedResource* direct_texture = FindDirectResourceLocked(texture);
        if (include_exact_texture_action_interests) {
          if (DirectCommandTextureSlotHasExactRegisteredProducerLocked(
                  command, slot, direct_texture)) {
            AddDirectExactTextureActionInterestsLocked(command, slot,
                                                       resource_filter,
                                                       texture_upload_filter);
            continue;
          }
        }
        AddDirectResourceInterest(resource_filter, texture);
        AddDirectResourceInterest(texture_upload_filter, texture);
        if (include_exact_texture_action_interests) {
          AddDirectExactTextureActionInterestsLocked(command, slot,
                                                     resource_filter,
                                                     texture_upload_filter);
        }
      }
      break;
    }
    case FableDirectRenderCommandType::kResolveCopy: {
      AddDirectResourceInterest(resource_filter,
                                command.resolve.refii_source_surface);
      for (uint32_t render_target : command.resources.render_targets) {
        AddDirectResourceInterest(resource_filter, render_target);
      }
      AddDirectResourceInterest(resource_filter, command.resources.depth_stencil);
      const uint32_t destination_base =
          command.resolve.rb_copy_dest_base != 0
              ? command.resolve.rb_copy_dest_base
              : command.pipeline.pm4.rb_copy_dest_base;
      AddDirectResourceInterest(resource_filter, destination_base);
      AddDirectResourceInterest(
          resource_filter, NormalizeFableResolveDestinationBase(destination_base));
      break;
    }
    default:
      break;
  }
}

void AddDirectFramebufferResourceInterest(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter) {
  for (uint32_t render_target : command.resources.render_targets) {
    if (DirectCommandCanMaterializePrivateRectTargetLocked(command,
                                                           render_target)) {
      continue;
    }
    AddDirectResourceInterest(resource_filter, render_target);
  }
  AddDirectResourceInterest(resource_filter, command.resources.depth_stencil);
}

bool RecordDirectPresentBlitActionPacketLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const RefiiFrameActionPacket& action,
    DirectPresentSource& source) {
  if (action.type != RefiiFrameActionType::kPresentBlit || !device ||
      !command_list || !output_texture || !present_framebuffer ||
      !source.texture || !source.view) {
    return false;
  }
  if (!EnsurePresentBlitResourcesLocked(device, shader_format)) {
    return false;
  }
  const RefiiPresentBlitPacket& packet = action.present_blit;
  const uint32_t output_width =
      packet.output_width != 0 ? packet.output_width
                               : present_framebuffer->getWidth();
  const uint32_t output_height =
      packet.output_height != 0 ? packet.output_height
                                : present_framebuffer->getHeight();
  if (output_width == 0 || output_height == 0) {
    return false;
  }

  char present_label[160] = {};
  std::snprintf(present_label, sizeof(present_label),
                "RefiiRenderer::PresentBlit Frame %u Target_%08X %ux%u "
                "Reason_%s",
                g_direct_renderer.current_frame_index, source.target,
                source.width, source.height, source.reason);
  PlumeDirectDebugLabelScope present_scope(command_list, present_label);

  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   source.texture,
                                   plume::RenderTextureLayout::SHADER_READ);
  RecordDirectTextureBarrierLocked(command_list,
                                   plume::RenderBarrierStage::GRAPHICS,
                                   output_texture,
                                   plume::RenderTextureLayout::COLOR_WRITE);
  source.layout = plume::RenderTextureLayout::SHADER_READ;
  if (source.texture == g_direct_renderer.last_scene_texture) {
    g_direct_renderer.last_scene_layout = source.layout;
  }
  if (source.texture == g_direct_renderer.last_submitted_scene_texture) {
    g_direct_renderer.last_submitted_scene_layout = source.layout;
  }
  const uint32_t descriptor_index =
      g_plume_resources.present_blit_texture_set_index++ %
      kPresentBlitDescriptorSetRingSize;
  plume::RenderDescriptorSet* texture_set =
      g_plume_resources.present_blit_texture_sets[descriptor_index].get();
  texture_set->setTexture(0, source.texture, source.layout, source.view);
  g_plume_resources.present_blit_texture_set_textures[descriptor_index] =
      source.texture;
  g_plume_resources.present_blit_texture_set_views[descriptor_index] =
      source.view;
  g_plume_resources.present_blit_texture_set_layouts[descriptor_index] =
      source.layout;

  command_list->setFramebuffer(present_framebuffer);
  command_list->setGraphicsPipelineLayout(
      g_plume_resources.present_blit_pipeline_layout.get());
  command_list->setPipeline(g_plume_resources.present_blit_pipeline.get());
  command_list->setGraphicsDescriptorSet(texture_set, 0);
  command_list->setGraphicsDescriptorSet(
      g_plume_resources.present_blit_sampler_set.get(), 1);
  PlumePresentBlitPushConstants push_constants{};
  if (source.width != 0 && source.height != 0 &&
      source.texture_width != 0 && source.texture_height != 0) {
    push_constants.uv_scale[0] =
        std::min(1.0f, static_cast<float>(source.width) /
                           static_cast<float>(source.texture_width));
    push_constants.uv_scale[1] =
        std::min(1.0f, static_cast<float>(source.height) /
                           static_cast<float>(source.texture_height));
  }
  if (shader_format == plume::RenderShaderFormat::SPIRV) {
    command_list->setGraphicsPushConstants(0, &push_constants, 0,
                                           sizeof(push_constants));
  } else {
    auto constants = CreateUploadBufferWithData(
        device, &push_constants, sizeof(push_constants),
        "Refii Fable direct present constants",
        plume::RenderBufferFlag::CONSTANT);
    if (!constants) {
      return false;
    }
    const plume::RenderBuffer* constants_buffer = constants.get();
    command_list->setGraphicsRootDescriptor(constants_buffer->at(0), 0);
    std::vector<std::unique_ptr<plume::RenderBuffer>> uploads;
    uploads.emplace_back(std::move(constants));
    RetainFrameUploadBuffersLocked(std::move(uploads));
  }

  command_list->setViewports(plume::RenderViewport(
      0.0f, 0.0f, static_cast<float>(output_width),
      static_cast<float>(output_height)));
  command_list->setScissors(
      plume::RenderRect(0, 0, static_cast<int32_t>(output_width),
                        static_cast<int32_t>(output_height)));
  command_list->drawInstanced(3, 1, 0, 0);
  return true;
}

bool RefiiSurfaceDestinationActionMatchesRelation(
    const RefiiSurfaceDestinationPacket& packet,
    const FableSurfaceDestinationTextureRelation& relation) {
  if (packet.relation_serial != 0 && relation.serial != 0 &&
      packet.relation_serial != relation.serial) {
    return false;
  }
  if (packet.source_surface != 0 &&
      packet.source_surface != relation.source_surface &&
      packet.source_surface != relation.link.source_surface &&
      packet.source_surface != relation.link.render_target) {
    return false;
  }
  if (packet.destination_texture != 0 &&
      packet.destination_texture != relation.destination_texture &&
      packet.destination_texture != relation.link.texture &&
      packet.destination_texture != relation.link.texture_base_address) {
    return false;
  }
  if (packet.source_slot != UINT32_MAX &&
      relation.source_slot != UINT32_MAX &&
      packet.source_slot != relation.source_slot) {
    return false;
  }
  return true;
}

const FableSurfaceDestinationTextureRelation*
ResolveRefiiSurfaceDestinationActionRelationLocked(
    const RefiiSurfaceDestinationPacket& packet) {
  auto accept =
      [&](const FableSurfaceDestinationTextureRelation* relation)
          -> const FableSurfaceDestinationTextureRelation* {
    return relation &&
                   RefiiSurfaceDestinationActionMatchesRelation(packet,
                                                                *relation)
               ? relation
               : nullptr;
  };
  if (const FableSurfaceDestinationTextureRelation* relation = accept(
          FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
              packet.destination_texture, g_direct_renderer.current_frame_index,
              packet.command_serial, packet.source_slot))) {
    return relation;
  }
  if (const FableSurfaceDestinationTextureRelation* relation = accept(
          FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
              packet.destination_texture, packet.source_slot))) {
    return relation;
  }
  if (const FableSurfaceDestinationTextureRelation* relation = accept(
          FableDirectFindSurfaceDestinationRelationForDestinationTexture(
              packet.destination_texture))) {
    return relation;
  }
  const std::vector<FableSurfaceDestinationTextureRelation>* relations =
      FableDirectFindSurfaceDestinationRelationsBySourceSurface(
          packet.source_surface);
  if (!relations) {
    return nullptr;
  }
  for (const FableSurfaceDestinationTextureRelation& relation : *relations) {
    if (RefiiSurfaceDestinationActionMatchesRelation(packet, relation)) {
      return &relation;
    }
  }
  return nullptr;
}

void AddRefiiFrameActionResourceInterestsLocked(
    const RefiiFrameActionPacket& action,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter) {
  auto add_texture_interest = [&](uint32_t address) {
    AddDirectResourceInterest(resource_filter, address);
    AddDirectResourceInterest(texture_upload_filter, address);
  };
  switch (action.type) {
    case RefiiFrameActionType::kSurfaceDestination: {
      const RefiiSurfaceDestinationPacket& packet =
          action.surface_destination;
      add_texture_interest(packet.source_surface);
      add_texture_interest(packet.destination_texture);
      add_texture_interest(packet.backing_texture);
      add_texture_interest(packet.texture_base);
      add_texture_interest(packet.backing_base);
      break;
    }
    case RefiiFrameActionType::kPresentBlit:
      add_texture_interest(action.present_blit.source_target);
      break;
    default:
      break;
  }
}

uint32_t RecordDirectPacketQueueFrameActionsLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderFramebuffer* output_framebuffer,
    plume::RenderFormat output_depth_format,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const RefiiFramePacketQueueSnapshot& queue) {
  if (!queue.replay_ready || queue.actions.empty()) {
    return 0;
  }

  PlumeDirectDebugLabelScope action_scope(
      command_list, "RefiiRenderer::ReplayPacketActions");
  const std::vector<FableDirectRenderCommand> replay_commands =
      BuildDirectReplayCommandsFromRefiiQueue(queue);
  size_t replay_command_index = 0;
  auto next_replay_command = [&]() -> const FableDirectRenderCommand* {
    if (replay_command_index >= replay_commands.size()) {
      return nullptr;
    }
    return &replay_commands[replay_command_index++];
  };

  uint32_t recorded_actions = 0;
  for (const RefiiFrameActionPacket& action : queue.actions) {
    switch (action.type) {
      case RefiiFrameActionType::kDraw:
        (void)next_replay_command();
        break;
      case RefiiFrameActionType::kClear: {
        const FableDirectRenderCommand* clear_command = next_replay_command();
        if (!clear_command || clear_command->clear.flags == 0) {
          break;
        }
        RecordDirectClearCommand(device, command_list, output_framebuffer,
                                 output_depth_format, *clear_command);
        ++recorded_actions;
        break;
      }
      case RefiiFrameActionType::kResolve: {
        const FableDirectRenderCommand* resolve_command = next_replay_command();
        if (!resolve_command) {
          break;
        }
        const DirectResolveCopyRecordResult resolve_result =
            RecordDirectResolveCopyCommandLocked(command_list,
                                                *resolve_command);
        if (resolve_result.handled) {
          ++recorded_actions;
        }
        break;
      }
      case RefiiFrameActionType::kSurfaceDestination: {
        const RefiiSurfaceDestinationPacket& packet =
            action.surface_destination;
        const FableSurfaceDestinationTextureRelation* relation =
            ResolveRefiiSurfaceDestinationActionRelationLocked(packet);
        PlumeTrackedResource* source =
            FindDirectResourceLocked(packet.source_surface);
        PlumeTrackedResource* target =
            FindDirectResourceLocked(packet.destination_texture);
        if (!relation || !source || !target) {
          break;
        }
        bool scaled_copy = false;
        if (!RecordDirectFrameActionPacketLocked(
                device, command_list, nullptr, nullptr, shader_format, action,
                relation, source, target, nullptr, &scaled_copy)) {
          break;
        }
        ++recorded_actions;
        ++g_direct_renderer.packet_queue_replayed_surface_destination_actions;
        if (scaled_copy) {
          ++g_direct_renderer.stats.surface_destination_copied_scaled;
        } else {
          ++g_direct_renderer.stats.surface_destination_copied_exact;
        }
        if (g_direct_renderer.frame_database_command_packets.size() <
            kMaxFrameDatabaseCommandPackets) {
          g_direct_renderer.frame_database_command_packets.push_back(
              BuildFrameDatabaseSurfaceDestinationCommandPacketLocked(
                  *relation, *source, *target, scaled_copy));
        } else {
          ++g_direct_renderer.frame_database_omitted_command_packets;
        }
        AppendFrameDatabaseSurfaceDestinationResolvePacketLocked(*relation,
                                                                 *source,
                                                                 *target);
        break;
      }
      case RefiiFrameActionType::kPresentBlit: {
        DirectPresentSource source =
            ResolveDirectPresentSourceFromRefiiPresentBlitPacketLocked(
                action.present_blit);
        if (!source.texture || !source.view ||
            !RecordDirectFrameActionPacketLocked(
                device, command_list, output_texture, present_framebuffer,
                shader_format, action, nullptr, nullptr, nullptr, &source,
                nullptr)) {
          break;
        }
        const uint32_t output_width =
            present_framebuffer != nullptr
                ? present_framebuffer->getWidth()
                : action.present_blit.output_width;
        const uint32_t output_height =
            present_framebuffer != nullptr
                ? present_framebuffer->getHeight()
                : action.present_blit.output_height;
        FableDirectFrameDatabaseCommandPacket present_packet =
            BuildFrameDatabasePresentBlitCommandPacket(source, output_width,
                                                       output_height);
        if (action.present_blit.packet_id != 0) {
          present_packet.packet_id = action.present_blit.packet_id;
        }
        if (action.command_serial != 0) {
          present_packet.command_serial = action.command_serial;
        }
        if (present_packet.present_blit.source_target != 0 &&
            present_packet.present_blit.output_width != 0 &&
            present_packet.present_blit.output_height != 0) {
          if (g_direct_renderer.frame_database_command_packets.size() <
              kMaxFrameDatabaseCommandPackets) {
            g_direct_renderer.frame_database_command_packets.push_back(
                std::move(present_packet));
          } else {
            ++g_direct_renderer.frame_database_omitted_command_packets;
          }
        }
        ++recorded_actions;
        ++g_direct_renderer.packet_queue_replayed_present_blit_actions;
        // The held-present fallback re-blits last_present_target on pump
        // ticks with no live source; recording a sub-display helper blit
        // (960x540 legal insets, 480x270 previews) here makes the fallback
        // stretch it fullscreen over the real composite. "Display-sized"
        // tracks the live guest dims (qword_834A5FE8).
        if (source.width >= FableGuestDisplayWidth() &&
            source.height >= FableGuestDisplayHeight()) {
          g_direct_renderer.stats.last_present_target = source.target;
          g_direct_renderer.stats.last_present_width = source.width;
          g_direct_renderer.stats.last_present_height = source.height;
        }
        break;
      }
      case RefiiFrameActionType::kUnknown:
      default:
        break;
    }
  }
  return recorded_actions;
}

void RecordDirectPresentBlitLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const FableDirectStateSnapshot& frame_snapshot) {
  if (!device || !command_list || !output_texture || !present_framebuffer) {
    static uint32_t logged_missing_present_blit_inputs = 0;
    if (logged_missing_present_blit_inputs < 16u) {
      REXLOG_WARN(
          "Refii plume direct renderer cannot present blit; missing input "
          "device={} command_list={} output_texture={} framebuffer={}",
          device != nullptr, command_list != nullptr, output_texture != nullptr,
          present_framebuffer != nullptr);
      ++logged_missing_present_blit_inputs;
    }
    return;
  }
  const uint32_t output_width = present_framebuffer->getWidth();
  const uint32_t output_height = present_framebuffer->getHeight();
  DirectPresentSource source = ResolveDirectPresentSourceLocked(frame_snapshot);
  // Compose-completion presentation (§3.4 forerunner): this frame's compose
  // into the chosen target bound the null texture for a populated slot —
  // presenting it paints the black mid-compose panels. Show the last
  // complete composite instead, bounded so a permanently unresolved slot
  // can't freeze the screen on a stale hold.
  if (source.texture && source.view &&
      g_direct_renderer.textureless_compose_targets.count(source.target) !=
          0u &&
      g_direct_renderer.present.hold_texture &&
      g_direct_renderer.present.hold_view &&
      g_direct_renderer.current_frame_index >=
          g_direct_renderer.present.hold_frame &&
      g_direct_renderer.current_frame_index -
              g_direct_renderer.present.hold_frame <=
          90u) {
    const uint32_t deferred_target = source.target;
    source.texture = g_direct_renderer.present.hold_texture.get();
    source.view = g_direct_renderer.present.hold_view.get();
    source.target = g_direct_renderer.present.hold_target;
    source.width = g_direct_renderer.present.hold_width;
    source.height = g_direct_renderer.present.hold_height;
    source.texture_width = g_direct_renderer.present.hold_width;
    source.texture_height = g_direct_renderer.present.hold_height;
    source.frame_index = g_direct_renderer.present.hold_frame;
    source.write_serial = g_direct_renderer.present.hold_serial;
    source.reason = "compose_incomplete_hold";
    static uint64_t logged_compose_incomplete_holds = 0;
    ++logged_compose_incomplete_holds;
    if (logged_compose_incomplete_holds <= 8u ||
        (logged_compose_incomplete_holds % 512u) == 0u) {
      REXLOG_INFO(
          "Refii plume compose-incomplete hold #{} frame={} deferred=0x{:08X} "
          "held=0x{:08X}",
          logged_compose_incomplete_holds,
          g_direct_renderer.current_frame_index, deferred_target,
          g_direct_renderer.present.hold_target);
    }
    g_direct_renderer.present.consecutive_resolve_failures = 0;
  } else if (source.texture && source.view) {
    g_direct_renderer.present.consecutive_resolve_failures = 0;
    // Snapshot every fresh present: starved ticks re-present this copy, so
    // interleaved wipes of live targets can never paint the screen black.
    RecordGenericPresentHoldSnapshotLocked(device, command_list, source);
  } else {
    const uint64_t failure_streak =
        ++g_direct_renderer.present.consecutive_resolve_failures;
    // A pump tick without a same-frame producer is normal (display rate >
    // game rate); minutes of them means every resolver branch is starving.
    if (failure_streak % 600u == 0u) {
      REXLOG_INFO(
          "Refii plume direct renderer present resolve failing streak={} "
          "frame={} held=0x{:08X}/{}x{} scene=0x{:08X} scene_frame={} "
          "submitted=0x{:08X} submitted_frame={} fable=0x{:08X} "
          "has_video_draw={} video_target=0x{:08X} video_frame={}",
          failure_streak, g_direct_renderer.current_frame_index,
          g_direct_renderer.stats.last_present_target,
          g_direct_renderer.stats.last_present_width,
          g_direct_renderer.stats.last_present_height,
          g_direct_renderer.last_scene_target,
          g_direct_renderer.last_scene_frame_index,
          g_direct_renderer.last_submitted_scene_target,
          g_direct_renderer.last_submitted_scene_frame_index,
          g_direct_renderer.fable_scene_target,
          g_direct_renderer.current_frame_has_pix_video_draw ? 1 : 0,
          g_direct_renderer.last_pix_video_target,
          g_direct_renderer.last_pix_video_frame);
    }
    // Hold the last presented image: the pump runs at display rate and most
    // ticks resolve no same-frame producer; without re-blitting the previous
    // source every swapchain image cycles back to its stale (black) content.
    // When the held target is the video composite, hold its post-draw
    // snapshot: the live texture is cleared by interleaved title work, so
    // re-resolving it holds black instead of the last movie frame. Bounded:
    // an unbounded video hold latches the movie's final (often black) frame
    // across the whole title-menu phase.
    constexpr uint32_t kHeldVideoHoldMaxFrames = 120u;
    // The target check is what makes "recent" mean anything: with no video
    // hold ever recorded, hold_frame is 0 and at boot the window arithmetic
    // (0 >= 0, 0 - 0 <= 120) reports RECENT for a hold that does not exist.
    // Both consumers happen to re-guard on present.video_hold_target != 0, so
    // this was cosmetic — but it reads as a live video hold in the no-present-
    // source warning and costs a reader time to rule out.
    const bool video_hold_recent =
        g_direct_renderer.present.video_hold_target != 0 &&
        g_direct_renderer.current_frame_index >=
            g_direct_renderer.present.video_hold_frame &&
        g_direct_renderer.current_frame_index -
                g_direct_renderer.present.video_hold_frame <=
            kHeldVideoHoldMaxFrames;
    if (video_hold_recent && g_direct_renderer.present.video_hold_texture &&
        g_direct_renderer.present.video_hold_view &&
        g_direct_renderer.stats.last_present_target != 0 &&
        g_direct_renderer.stats.last_present_target ==
            g_direct_renderer.present.video_hold_target) {
      source.texture = g_direct_renderer.present.video_hold_texture.get();
      source.view = g_direct_renderer.present.video_hold_view.get();
      source.target = g_direct_renderer.present.video_hold_target;
      source.width = g_direct_renderer.present.video_hold_width;
      source.height = g_direct_renderer.present.video_hold_height;
      source.texture_width = g_direct_renderer.present.video_hold_width;
      source.texture_height = g_direct_renderer.present.video_hold_height;
      source.frame_index = g_direct_renderer.present.video_hold_frame;
      source.write_serial = g_direct_renderer.present.video_hold_serial;
      source.reason = "held_video_hold";
    }
    // Starvation fallback: a sustained failure streak with the guest still
    // drawing means the phase's output target satisfies no title-era
    // candidate/producer proof (post-title handoff renders into a fresh
    // target the heuristics never learned). Present the most recently
    // written color target of this frame — real output beats holding a
    // stale composite forever. Dimension floor keeps helper targets out.
    if ((!source.texture || !source.view) && failure_streak > 120u) {
      DirectPresentSource starved_fallback;
      // Rank by DRAW content first: write_serial counts clears too, and the
      // per-frame wipe of a stale composite outranks the target the game is
      // actually drawing into (post-title black screen, run 989 class).
      uint64_t starved_fallback_draw_serial = 0;
      for (uint32_t drawn_target : frame_snapshot.targets.color) {
        if (drawn_target == 0) {
          continue;
        }
        DirectPresentSource candidate =
            PresentSourceFromResourceLocked(drawn_target);
        if (!candidate.texture || !candidate.view ||
            candidate.write_serial == 0 || candidate.width < 640u ||
            candidate.height < 360u) {
          continue;
        }
        PlumeTrackedResource* candidate_resource =
            FindDirectResourceLocked(drawn_target);
        const uint64_t candidate_draw_serial =
            candidate_resource ? candidate_resource->draw_content_serial : 0;
        const bool better =
            candidate_draw_serial != starved_fallback_draw_serial
                ? candidate_draw_serial > starved_fallback_draw_serial
                : candidate.write_serial > starved_fallback.write_serial;
        if (better) {
          starved_fallback = candidate;
          starved_fallback_draw_serial = candidate_draw_serial;
        }
      }
      if (starved_fallback.texture && starved_fallback.view) {
        starved_fallback.reason = "starvation_fallback_drawn_target";
        source = starved_fallback;
        RecordGenericPresentHoldSnapshotLocked(device, command_list, source);
        static uint32_t logged_starvation_fallbacks = 0;
        ++logged_starvation_fallbacks;
        if (logged_starvation_fallbacks <= 16u ||
            (logged_starvation_fallbacks % 512u) == 0u) {
          REXLOG_INFO(
              "Refii plume present starvation fallback #{} frame={} "
              "target=0x{:08X} {}x{} serial={}",
              logged_starvation_fallbacks,
              g_direct_renderer.current_frame_index, source.target,
              source.width, source.height,
              static_cast<unsigned long long>(source.write_serial));
        }
      }
    }
    if ((!source.texture || !source.view) &&
        g_direct_renderer.present.hold_texture &&
        g_direct_renderer.present.hold_view) {
      source.texture = g_direct_renderer.present.hold_texture.get();
      source.view = g_direct_renderer.present.hold_view.get();
      source.target = g_direct_renderer.present.hold_target;
      source.width = g_direct_renderer.present.hold_width;
      source.height = g_direct_renderer.present.hold_height;
      source.texture_width = g_direct_renderer.present.hold_width;
      source.texture_height = g_direct_renderer.present.hold_height;
      source.frame_index = g_direct_renderer.present.hold_frame;
      source.write_serial = g_direct_renderer.present.hold_serial;
      source.reason = "held_present_hold";
    }
    // Only display-sized targets may be held: the packet-replay path also
    // records small helper targets into last_present_target. Never re-latch
    // the expired video target through the generic held: its live texture is
    // the wiped composite, and holding it paints the title-menu window black;
    // leaving the swapchain untouched keeps the last movie frames up until a
    // title branch resolves the menu.
    const bool held_target_is_expired_video =
        !video_hold_recent &&
        g_direct_renderer.stats.last_present_target ==
            g_direct_renderer.present.video_hold_target &&
        g_direct_renderer.present.video_hold_target != 0;
    if ((!source.texture || !source.view) && !held_target_is_expired_video &&
        g_direct_renderer.stats.last_present_target != 0 &&
        g_direct_renderer.stats.last_present_width >=
            FableGuestDisplayWidth() &&
        g_direct_renderer.stats.last_present_height >=
            FableGuestDisplayHeight()) {
      DirectPresentSource held = PresentSourceFromResourceLocked(
          g_direct_renderer.stats.last_present_target);
      if (held.texture && held.view &&
          held.width >= FableGuestDisplayWidth() &&
          held.height >= FableGuestDisplayHeight()) {
        held.reason = "held_last_present";
        source = held;
      }
    }
  }
  if (!source.texture || !source.view) {
    const uint64_t no_blit_count =
        ++g_direct_renderer.present.no_blit_frames;
    if (no_blit_count <= 8u || no_blit_count % 300u == 0u) {
      const FableDirectPresentSnapshot present =
          GetFableDirectPresentSnapshot();
      constexpr uint32_t kHeldVideoHoldMaxFramesForLog = 120u;
      // Mirrors the decision expression above, target guard included.
      const bool video_hold_recent =
          g_direct_renderer.present.video_hold_target != 0 &&
          g_direct_renderer.current_frame_index >=
              g_direct_renderer.present.video_hold_frame &&
          g_direct_renderer.current_frame_index -
                  g_direct_renderer.present.video_hold_frame <=
              kHeldVideoHoldMaxFramesForLog;
      const bool held_target_is_expired_video =
          !video_hold_recent &&
          g_direct_renderer.stats.last_present_target ==
              g_direct_renderer.present.video_hold_target &&
          g_direct_renderer.present.video_hold_target != 0;
      REXLOG_WARN(
          "Refii plume direct renderer has no live Fable present source #{} "
          "frame={}; swapchain image not updated display_surface=0x{:08X} "
          "display_texture=0x{:08X} frame_targets=0x{:08X}/0x{:08X}/"
          "0x{:08X}/0x{:08X} stats_target=0x{:08X}/{}x{} scene=0x{:08X} "
          "submitted=0x{:08X} fable=0x{:08X} hold_tex={} hold_target=0x{:08X} "
          "hold_frame={} video_hold_recent={} video_target=0x{:08X} "
          "video_frame={} expired_video_veto={}",
          static_cast<unsigned long long>(no_blit_count),
          g_direct_renderer.current_frame_index,
          present.display_surface_object, present.display_texture_base,
          frame_snapshot.targets.color[0], frame_snapshot.targets.color[1],
          frame_snapshot.targets.color[2], frame_snapshot.targets.color[3],
          g_direct_renderer.stats.last_present_target,
          g_direct_renderer.stats.last_present_width,
          g_direct_renderer.stats.last_present_height,
          g_direct_renderer.last_scene_target,
          g_direct_renderer.last_submitted_scene_target,
          g_direct_renderer.fable_scene_target,
          g_direct_renderer.present.hold_texture ? 1 : 0,
          g_direct_renderer.present.hold_target,
          g_direct_renderer.present.hold_frame,
          video_hold_recent ? 1 : 0,
          g_direct_renderer.present.video_hold_target,
          g_direct_renderer.present.video_hold_frame,
          held_target_is_expired_video ? 1 : 0);
    }
    return;
  }
  FableDirectFrameDatabaseCommandPacket present_packet =
      BuildFrameDatabasePresentBlitCommandPacket(source, output_width,
                                                 output_height);
  const RefiiFrameActionPacket present_action =
      BuildRefiiPresentBlitActionPacket(
          g_direct_renderer.refii_packet_queue.frame_id, present_packet);
  if (RendererDrawTracingEnabled() &&
      (g_direct_renderer.current_frame_index % 120u) == 0u) {
    REXLOG_INFO(
        "Refii plume present sample frame={} reason={} target=0x{:08X} "
        "size={}x{} serial={}",
        g_direct_renderer.current_frame_index, source.reason, source.target,
        source.width, source.height,
        static_cast<unsigned long long>(source.write_serial));
  }
  const bool present_source_changed =
      source.target != g_direct_renderer.present.last_logged_source ||
      source.width != g_direct_renderer.present.last_logged_width ||
      source.height != g_direct_renderer.present.last_logged_height;
  if (present_source_changed) {
    const uint32_t change_count =
        ++g_direct_renderer.diagnostics.logged_present_source_changes;
    if (change_count <= 64u ||
        (change_count & (change_count - 1u)) == 0u) {
      REXLOG_INFO(
          "Refii plume direct renderer present source change #{} reason={} "
          "target=0x{:08X} size={}x{} texture={}x{} scene=0x{:08X}/{}x{} "
          "submitted=0x{:08X}/{}x{} serials={}/{}",
          change_count, source.reason,
          source.target, source.width, source.height, source.texture_width,
          source.texture_height, g_direct_renderer.last_scene_target,
          g_direct_renderer.last_scene_width, g_direct_renderer.last_scene_height,
          g_direct_renderer.last_submitted_scene_target,
          g_direct_renderer.last_submitted_scene_width,
          g_direct_renderer.last_submitted_scene_height,
          g_direct_renderer.last_scene_write_serial,
          g_direct_renderer.last_submitted_scene_write_serial);
    }
    g_direct_renderer.present.last_logged_source = source.target;
    g_direct_renderer.present.last_logged_width = source.width;
    g_direct_renderer.present.last_logged_height = source.height;
  }
  const bool title_present_detail =
      (IsFableTitleSceneTargetLocked(source.target) &&
       (g_direct_renderer.last_scene_write_serial != 0 ||
        g_direct_renderer.last_submitted_scene_write_serial != 0)) ||
      (IsFableTitleSceneTargetLocked(g_direct_renderer.last_scene_target) &&
       g_direct_renderer.last_scene_write_serial != 0) ||
      (IsFableTitleSceneTargetLocked(g_direct_renderer.last_submitted_scene_target) &&
       g_direct_renderer.last_submitted_scene_write_serial != 0);
  const bool should_log_present_detail =
      RendererVerboseTracingEnabled() &&
      g_direct_renderer.diagnostics.logged_present_source_details < 64u &&
      (g_direct_renderer.diagnostics.logged_present_source_details < 16u ||
       (title_present_detail &&
        (source.write_serial !=
             g_direct_renderer.present.last_logged_detail_serial ||
         (g_direct_renderer.current_frame_index % 60u) == 0u)) ||
       source.target != g_direct_renderer.present.last_logged_detail_target ||
       (g_direct_renderer.current_frame_index % 120u) == 0u);
  if (should_log_present_detail) {
    REXLOG_INFO(
        "Refii plume direct renderer present detail #{} frame={} reason={} "
        "target=0x{:08X} size={}x{} texture={}x{} serial={} tex=0x{:016X} "
        "view=0x{:016X} scene=0x{:08X}/{}x{} scene_serial={} "
        "submitted=0x{:08X}/{}x{} submitted_serial={} "
        "fable=0x{:08X}/{}x{}",
        ++g_direct_renderer.diagnostics.logged_present_source_details,
        g_direct_renderer.current_frame_index, source.reason, source.target,
        source.width, source.height, source.texture_width, source.texture_height,
        static_cast<unsigned long long>(source.write_serial),
        static_cast<unsigned long long>(
            reinterpret_cast<uintptr_t>(source.texture)),
        static_cast<unsigned long long>(
            reinterpret_cast<uintptr_t>(source.view)),
        g_direct_renderer.last_scene_target, g_direct_renderer.last_scene_width,
        g_direct_renderer.last_scene_height,
        static_cast<unsigned long long>(
            g_direct_renderer.last_scene_write_serial),
        g_direct_renderer.last_submitted_scene_target,
        g_direct_renderer.last_submitted_scene_width,
        g_direct_renderer.last_submitted_scene_height,
        static_cast<unsigned long long>(
            g_direct_renderer.last_submitted_scene_write_serial),
        g_direct_renderer.fable_scene_target, g_direct_renderer.fable_scene_width,
        g_direct_renderer.fable_scene_height);
    g_direct_renderer.present.last_logged_detail_serial = source.write_serial;
    g_direct_renderer.present.last_logged_detail_target = source.target;
  }

  if (!RecordDirectFrameActionPacketLocked(
          device, command_list, output_texture, present_framebuffer,
          shader_format, present_action, nullptr, nullptr, nullptr, &source,
          nullptr)) {
    return;
  }
  AppendFrameDatabasePresentBlitCommandPacketLocked(std::move(present_packet));
  g_direct_renderer.stats.last_present_target = source.target;
  if (source.width != 0 && source.height != 0) {
    g_direct_renderer.stats.last_present_width = source.width;
    g_direct_renderer.stats.last_present_height = source.height;
  }
  if (!g_direct_renderer.diagnostics.logged_first_present_copy) {
    REXLOG_INFO(
        "Refii plume direct renderer blitted Fable present target 0x{:08X} "
        "{}x{} to output {}x{} reason={}",
        source.target, source.width, source.height, output_width,
        output_height, source.reason);
    g_direct_renderer.diagnostics.logged_first_present_copy = true;
  }
}

void TransitionDirectFramebufferForWrite(
    plume::RenderCommandList* command_list,
    const DirectFramebufferBinding& binding) {
  if (!command_list) {
    return;
  }
  for (uint32_t slot = 0; slot < binding.color_count; ++slot) {
    plume::RenderTexture* color_texture = binding.color_textures[slot];
    if (!color_texture) {
      continue;
    }
    RecordDirectTextureBarrierLocked(command_list,
                                     plume::RenderBarrierStage::GRAPHICS,
                                     color_texture,
                                     plume::RenderTextureLayout::COLOR_WRITE);
  }
  if (binding.depth_texture) {
    RecordDirectTextureBarrierLocked(command_list,
                                     plume::RenderBarrierStage::GRAPHICS,
                                     binding.depth_texture,
                                     plume::RenderTextureLayout::DEPTH_WRITE);
  }
}

void NameTrackedBuffer(plume::RenderBuffer* buffer,
                       const FableGuestResourceSnapshot& snapshot,
                       PlumeTrackedResource& tracked) {
  if (!buffer) {
    return;
  }

  char name[128] = {};
  std::snprintf(name, sizeof(name),
                "Refii Fable %s guest=0x%08X gpu=0x%08X bytes=%u stride=%u",
                TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
                PlumeGuestBufferGpuAddress(tracked),
                PlumeGuestBufferByteSize(tracked),
                PlumeGuestBufferStrideOrFormat(tracked));
  tracked.debug_summary = name;
  buffer->setName(name);
}

void PopulateTrackedIdentity(PlumeTrackedResource& tracked,
                             const FableGuestResourceSnapshot& snapshot) {
  tracked.kind = snapshot.kind;
  tracked.guest_address = snapshot.guest_address;
  tracked.parent_address = snapshot.parent_address;
  tracked.serial = snapshot.serial;
  tracked.content_serial = std::max(tracked.content_serial, snapshot.serial);
  tracked.scratch = snapshot.scratch;
  // Sticky: a snapshot taken before the channel registration must not clear
  // ownership an earlier snapshot established.
  if (snapshot.content_channel != FableGuestContentChannel::kNone) {
    tracked.content_channel = snapshot.content_channel;
  }
  tracked.plume_owned = true;
}

const FableSurfaceTextureLink*
FindDirectExactColorCopyDestinationLinkForTextureLocked(
    const FableGuestResourceSnapshot& snapshot,
    const PlumeTrackedResource& decoded_shape) {
  if (snapshot.kind != FableGuestResourceKind::kTexture ||
      decoded_shape.texture_depth_target) {
    return nullptr;
  }

  auto usable_link = [](const FableSurfaceTextureLink* link,
                        uint32_t expected_base,
                        const std::array<uint32_t, 6>* expected_fetch) {
    if (!link || !link->trusted_destination_edge ||
        link->source_surface == 0 ||
        !DirectPrivateTextureCopyKindUsesColorBacking(link->kind)) {
      return false;
    }
    if (expected_base != 0 && link->texture_base_address != 0 &&
        link->texture_base_address != expected_base) {
      return false;
    }
    if (expected_fetch && HasTrackedFetchDwords(*expected_fetch) &&
        HasTrackedFetchDwords(link->texture_fetch_dwords) &&
        FableDirectTextureFetchKey(link->texture_fetch_dwords) !=
            FableDirectTextureFetchKey(*expected_fetch)) {
      return false;
    }
    return true;
  };

  const uint32_t texture_base = PlumeGuestTextureBaseAddress(decoded_shape);
  const std::array<uint32_t, 6>& texture_fetch =
      PlumeGuestTextureFetchDwords(decoded_shape);

  auto by_texture = [&](uint32_t texture) -> const FableSurfaceTextureLink* {
    if (texture == 0) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindSurfaceTextureLinkForDestinationTexture(texture);
    return usable_link(link, texture_base, nullptr) ? link : nullptr;
  };

  if (const FableSurfaceTextureLink* link = by_texture(snapshot.guest_address)) {
    return link;
  }
  if (const FableSurfaceTextureLink* link = by_texture(snapshot.parent_address)) {
    return link;
  }

  if (texture_base != 0) {
    const FableSurfaceTextureLink* link =
        FableDirectFindSurfaceTextureLinkForBase(texture_base);
    if (usable_link(link, texture_base, &texture_fetch)) {
      return link;
    }
  }

  if (HasTrackedFetchDwords(texture_fetch)) {
    const FableSurfaceTextureLink* link =
        FableDirectFindSurfaceTextureLinkForFetch(texture_fetch);
    if (usable_link(link, texture_base, &texture_fetch)) {
      return link;
    }
  }
  return nullptr;
}

void ApplyDirectExactCopyDestinationCreateHintLocked(
    const FableGuestResourceSnapshot& snapshot,
    PlumeTrackedResource& decoded_shape) {
  const uint32_t decoded_base = PlumeGuestTextureBaseAddress(decoded_shape);
  const uint32_t decoded_width = PlumeGuestTextureWidth(decoded_shape);
  const uint32_t decoded_height = PlumeGuestTextureHeight(decoded_shape);
  const uint32_t decoded_format =
      PlumeGuestTextureGuestFormat(decoded_shape);
  if (snapshot.kind == FableGuestResourceKind::kTexture &&
      IsFableTinyTitleUploadedTextureIdentityBase(decoded_base, decoded_width,
                                                  decoded_height) &&
      snapshot.texture.upload_generation != 0 &&
      snapshot.texture.upload_hash != 0) {
    static uint32_t logged_exact_tiny_title_create_hint_covers = 0;
    if (logged_exact_tiny_title_create_hint_covers < 16u) {
      REXLOG_INFO(
          "Refii plume exact tiny-title uploaded texture identity #{} frame={} "
          "guest=0x{:08X} parent=0x{:08X} base=0x{:08X} {}x{} fmt={} "
          "gen={} hash=0x{:016X}",
          ++logged_exact_tiny_title_create_hint_covers,
          g_direct_renderer.current_frame_index, snapshot.guest_address,
          snapshot.parent_address, decoded_base, decoded_width, decoded_height,
          decoded_format,
          static_cast<unsigned long long>(snapshot.texture.upload_generation),
          static_cast<unsigned long long>(snapshot.texture.upload_hash));
    }
    return;
  }

  const uint32_t exact_gui_upload_size =
      snapshot.kind == FableGuestResourceKind::kTexture
          ? ExactFableTitleGuiTextAtlasUploadSize(
                decoded_base, decoded_width, decoded_height, decoded_format)
          : 0u;

  // A Xenos color copy/resolve can only write color-renderable formats — a
  // block-compressed texture can never be a copy destination. A link matched
  // against BC metadata is a stale alias (recycled pool memory); honoring it
  // forces an RGBA8 image and turns the DXT payload into noise static.
  if (IsBlockCompressedFormat(decoded_shape.texture_format)) {
    return;
  }

  const FableSurfaceTextureLink* link =
      FindDirectExactColorCopyDestinationLinkForTextureLocked(snapshot,
                                                             decoded_shape);
  if (!link ||
      decoded_shape.texture_dimension !=
          plume::RenderTextureDimension::TEXTURE_2D ||
      decoded_shape.texture_view_dimension !=
          plume::RenderTextureViewDimension::TEXTURE_2D ||
      PlumeGuestTextureWidth(decoded_shape) == 0 ||
      PlumeGuestTextureHeight(decoded_shape) == 0) {
    return;
  }
  if (exact_gui_upload_size != 0u) {
    static uint32_t logged_exact_gui_create_hint_upload_owners = 0;
    if (logged_exact_gui_create_hint_upload_owners < 32u) {
      REXLOG_INFO(
          "Refii plume exact title GUI upload identity create-hint ownership "
          "proof #{} frame={} guest=0x{:08X} parent=0x{:08X} base=0x{:08X} "
          "{}x{} fmt={} exact_size={} gen={} hash=0x{:016X} link_source=0x{:08X} "
          "link_texture=0x{:08X} link_rt=0x{:08X} link_call={} "
          "link_context=0x{:016X} link_symbol=0x{:08X}",
          ++logged_exact_gui_create_hint_upload_owners,
          g_direct_renderer.current_frame_index, snapshot.guest_address,
          snapshot.parent_address, decoded_base, decoded_width, decoded_height,
          decoded_format, exact_gui_upload_size,
          static_cast<unsigned long long>(snapshot.texture.upload_generation),
          static_cast<unsigned long long>(snapshot.texture.upload_hash),
          link->source_surface, link->texture, link->render_target,
          static_cast<unsigned long long>(link->copy_call_serial),
          static_cast<unsigned long long>(link->copy_context_key),
          link->copy_symbol_address);
    }
    return;
  }

  decoded_shape.texture_render_target = true;
  decoded_shape.texture_depth_target = false;
  if (!IsUsablePlumeColorTargetFormat(decoded_shape.texture_format)) {
    decoded_shape.texture_format = plume::RenderFormat::R8G8B8A8_UNORM;
  }
  RefreshPlumeGuestImageObjectsFromTrackedFields(decoded_shape);

}

bool IsFableTitleBillboardTileUploadAddressForResourceBase(
    uint32_t address, uint32_t base, const PlumeTrackedResource& resource) {
  return FableTitleBillboardTileUploadAddressMatchesResourceBase(
      address, base, resource.guest_address, resource.parent_address);
}

uint32_t FableObservedTitleBillboardTileObjectAddressForBaseLocked(
    uint32_t base, uint32_t width, uint32_t height) {
  if (!IsFableTitleBillboardTileUploadBase(base, width, height)) {
    return 0;
  }

  const uint32_t canonical_object =
      FableTitleBillboardTileObjectAddressForBase(base);
  const std::vector<uint32_t>* objects =
      FableDirectFindTextureIdentityObjectsByBase(base);
  if (!objects) {
    return canonical_object;
  }

  const auto object_matches_base_alias = [&](uint32_t object) {
    if (object == canonical_object ||
        IsFableTitleBillboardTileUploadAliasForBase(object, base)) {
      return true;
    }
    if ((base == 0x13520000u && width == 64u && height == 64u) ||
        (base >= 0x13530000u && base <= 0x135B0000u &&
         ((width == 128u && height == 128u) ||
          (base == 0x13580000u && width == 64u && height == 64u)))) {
      return true;
    }
    if (base != kFableCanonicalTitleBillboardBase) {
      return false;
    }
    // ADDRESS GATE (census: billboard_tile_header_alias_object). Three of these
    // five are already covered structurally: the header-object strided range is
    // [0x4C132C70, 0x4C132D40] step 0x68 (objects 0x4C132C70/0x4C132CD8/
    // 0x4C132D40) with the header at object+0x34 (0x4C132CA4/...), so
    // IsFableTitleBillboardTileUploadObjectAddress/HeaderAddress answer yes for
    // 0x4C132C70, 0x4C132CA4 and 0x4C132CD8. 0x4C133150/0x4C133184 sit past the
    // range end and have no structural cover — they are the two that make this
    // list irreducible today. `derived` counts the covered three.
    return DirectAddressGateTest(
        DirectAddressGate::kBillboardTileHeaderAliasObject,
        object == 0x4C132C70u || object == 0x4C132CA4u ||
            object == 0x4C132CD8u || object == 0x4C133150u ||
            object == 0x4C133184u,
        object, 0ull,
        IsFableTitleBillboardTileUploadObjectAddress(object) ||
            IsFableTitleBillboardTileUploadHeaderAddress(object));
  };

  uint32_t identity_match = 0;
  uint32_t newest_ready_match = 0;
  uint64_t newest_ready_serial = 0;
  for (uint32_t object : *objects) {
    const FableTextureIdentity* identity =
        FableDirectFindTextureIdentityByObject(object);
    if (!identity || identity->base_address != base ||
        identity->width != width || identity->height != height) {
      continue;
    }
    if (!object_matches_base_alias(identity->object_texture)) {
      continue;
    }
    if (identity_match == 0) {
      identity_match = identity->object_texture;
    }
    const PlumeTrackedResource* resource =
        FindDirectResourceLocked(identity->object_texture);
    if (!resource || !IsImageResource(resource->kind) ||
        !PlumeGuestImageReady(*resource) ||
        PlumeGuestTextureBaseAddress(*resource) != base ||
        PlumeGuestTextureWidth(*resource) != width ||
        PlumeGuestTextureHeight(*resource) != height ||
        !FableTrackedResourceHasMeaningfulTextureUpload(*resource)) {
      continue;
    }
    const uint64_t serial = FableTrackedResourceVisibleContentSerial(*resource);
    if (newest_ready_match == 0 || serial >= newest_ready_serial) {
      newest_ready_match = identity->object_texture;
      newest_ready_serial = serial;
    }
  }

  if (newest_ready_match != 0) {
    if (newest_ready_match != canonical_object) {
      static uint32_t logged_title_alias_base_ownership = 0;
      if (logged_title_alias_base_ownership < 24u) {
        REXLOG_INFO(
            "Refii selected uploaded Fable title atlas alias #{} "
            "frame={} base=0x{:08X} canonical=0x{:08X} alias=0x{:08X} "
            "{}x{} serial={}",
            ++logged_title_alias_base_ownership,
            g_direct_renderer.current_frame_index, base, canonical_object,
            newest_ready_match, width, height,
            static_cast<unsigned long long>(newest_ready_serial));
      }
    }
    return newest_ready_match;
  }
  if (identity_match != 0) {
    return identity_match;
  }
  return canonical_object;
}

void PreserveFableTitleBillboardAtlasUploadIdentityLocked(
    PlumeTrackedResource& tracked, uint32_t previous_base,
    const std::array<uint32_t, 6>& previous_fetch_dwords) {
  if (!IsFableTitleBillboardAtlasUploadCarrierAddress(tracked.guest_address) ||
      previous_base != 0x16C40000u ||
      PlumeGuestTextureBaseAddress(tracked) != 0 ||
      !HasTrackedFetchDwords(previous_fetch_dwords) ||
      !PlumeGuestImageReady(tracked) ||
      !tracked.texture_upload_has_source || tracked.upload_generation == 0 ||
      tracked.texture_upload_hash == 0 ||
      PlumeGuestTextureWidth(tracked) != 1024u ||
      PlumeGuestTextureHeight(tracked) != 512u) {
    return;
  }

  tracked.texture_guest_base = previous_base;
  tracked.texture_fetch_dwords = previous_fetch_dwords;
  RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
  RegisterDirectTextureIdentityFromResourceLocked(tracked,
                                                  previous_fetch_dwords);

  static uint32_t logged_preserved_title_atlas_identity = 0;
  if (logged_preserved_title_atlas_identity < 8) {
    REXLOG_INFO(
        "Refii preserved Fable title atlas identity #{} guest=0x{:08X} "
        "base=0x{:08X} {}x{} gen={} hash=0x{:016X}",
        ++logged_preserved_title_atlas_identity, tracked.guest_address,
        previous_base, PlumeGuestTextureWidth(tracked),
        PlumeGuestTextureHeight(tracked),
        static_cast<uint64_t>(tracked.upload_generation),
        static_cast<uint64_t>(tracked.texture_upload_hash));
  }
}

void PreserveFableTitleBillboardTileUploadAliasLocked(
    const FableGuestResourceSnapshot& replacement_snapshot,
    PlumeTrackedResource& old_resource) {
  if (!IsImageResource(replacement_snapshot.kind) ||
      !PlumeGuestImageReady(old_resource) ||
      !FableTrackedResourceHasMeaningfulTextureUpload(old_resource) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(old_resource) ||
      old_resource.texture_depth_target || old_resource.texture_upload_hash == 0) {
    return;
  }

  const uint32_t old_base = PlumeGuestTextureBaseAddress(old_resource);
  const uint32_t old_width = PlumeGuestTextureWidth(old_resource);
  const uint32_t old_height = PlumeGuestTextureHeight(old_resource);
  const std::array<uint32_t, 6> old_fetch =
      PlumeGuestTextureFetchDwords(old_resource);
  if (!HasTrackedFetchDwords(old_fetch) ||
      !IsFableTitleBillboardTileUploadBase(old_base, old_width, old_height)) {
    return;
  }

  const bool old_title_tile_address =
      IsFableTitleTextureIndirectionChainAddress(old_resource.guest_address) ||
      IsFableTitleTextureIndirectionChainAddress(old_resource.parent_address) ||
      IsFableTitleBillboardTileUploadAddressForResourceBase(
          old_resource.guest_address, old_base, old_resource) ||
      IsFableTitleBillboardTileUploadAddressForResourceBase(
          old_resource.parent_address, old_base, old_resource);
  if (!old_title_tile_address) {
    return;
  }

  PlumeTrackedResource replacement_shape;
  PopulateTrackedIdentity(replacement_shape, replacement_snapshot);
  DecodeFableTextureMetadata(replacement_snapshot, replacement_shape);
  replacement_shape.texture_render_target =
      replacement_snapshot.kind == FableGuestResourceKind::kSurface;
  replacement_shape.texture_depth_target =
      replacement_snapshot.kind == FableGuestResourceKind::kDepthSurface;
  const bool replacement_keeps_title_identity =
      IsFableTitleTextureIndirectionChainAddress(
          replacement_snapshot.guest_address) ||
      IsFableTitleTextureIndirectionChainAddress(
          replacement_snapshot.parent_address) ||
      IsFableTitleBillboardTileUploadAddressForResourceBase(
          replacement_snapshot.guest_address, old_base, old_resource) ||
      IsFableTitleBillboardTileUploadAddressForResourceBase(
          replacement_snapshot.parent_address, old_base, old_resource) ||
      (PlumeGuestTextureBaseAddress(replacement_shape) == old_base &&
       PlumeGuestTextureWidth(replacement_shape) == old_width &&
       PlumeGuestTextureHeight(replacement_shape) == old_height);
  if (!replacement_keeps_title_identity) {
    return;
  }

  uint32_t alias_address = g_direct_renderer.next_title_billboard_upload_alias++;
  if (alias_address < 0xF2000000u) {
    alias_address = 0xF2000000u;
    g_direct_renderer.next_title_billboard_upload_alias = alias_address + 1u;
  }
  while (g_direct_renderer.resources.find(alias_address) !=
         g_direct_renderer.resources.end()) {
    alias_address = g_direct_renderer.next_title_billboard_upload_alias++;
  }

  const uint32_t original_address = old_resource.guest_address;
  PlumeTrackedResource preserved = std::move(old_resource);
  preserved.guest_address = alias_address;
  preserved.parent_address = original_address;
  preserved.serial = std::max<uint64_t>(preserved.serial,
                                        replacement_snapshot.serial);
  preserved.content_serial = std::max<uint64_t>(preserved.content_serial,
                                                replacement_snapshot.serial);
  preserved.guest_texture.object_address = alias_address;
  preserved.guest_texture.parent_address = original_address;
  preserved.plume_owned = true;
  preserved.guest_texture.plume_owned = true;
  RefreshPlumeGuestResourceHandlesFromTrackedFields(preserved);

  RegisterDirectTextureIdentityFromResourceLocked(preserved, old_fetch);
  g_direct_renderer.resources[alias_address] = std::move(preserved);
  InvalidateDirectResourceMemosForResourceLocked(
      g_direct_renderer.resources[alias_address]);

  static uint32_t logged_preserved_title_tile_upload_aliases = 0;
  if (logged_preserved_title_tile_upload_aliases < 32u) {
    const PlumeTrackedResource& alias_resource =
        g_direct_renderer.resources[alias_address];
    REXLOG_INFO(
        "Refii plume preserved title billboard tile upload alias #{} frame={} "
        "source=0x{:08X} alias=0x{:08X} parent=0x{:08X} base=0x{:08X} "
        "{}x{} fmt={} gen={} size={} hash=0x{:016X} replacement_kind={} "
        "replacement=0x{:08X}/0x{:08X}",
        ++logged_preserved_title_tile_upload_aliases,
        g_direct_renderer.current_frame_index, original_address, alias_address,
        alias_resource.parent_address,
        PlumeGuestTextureBaseAddress(alias_resource),
        PlumeGuestTextureWidth(alias_resource),
        PlumeGuestTextureHeight(alias_resource),
        PlumeGuestTextureGuestFormat(alias_resource),
        static_cast<unsigned long long>(alias_resource.upload_generation),
        static_cast<unsigned long long>(alias_resource.texture_upload_size),
        static_cast<unsigned long long>(alias_resource.texture_upload_hash),
        TrackedResourceKindName(replacement_snapshot.kind),
        replacement_snapshot.guest_address, replacement_snapshot.parent_address);
  }
}

void PreserveDirectPrivateTextureUploadAliasLocked(
    const FableGuestResourceSnapshot& replacement_snapshot,
    PlumeTrackedResource& old_resource) {
  if (!IsSurfaceResource(replacement_snapshot.kind) ||
      !PlumeGuestImageReady(old_resource) ||
      !FableTrackedResourceHasMeaningfulTextureUpload(old_resource) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(old_resource) ||
      !IsFablePrivateTextureChainResource(old_resource)) {
    return;
  }

  PlumeTrackedResource replacement_shape;
  PopulateTrackedIdentity(replacement_shape, replacement_snapshot);
  DecodeFableTextureMetadata(replacement_snapshot, replacement_shape);
  replacement_shape.texture_render_target =
      replacement_snapshot.kind == FableGuestResourceKind::kSurface;
  replacement_shape.texture_depth_target =
      replacement_snapshot.kind == FableGuestResourceKind::kDepthSurface;
  if (!IsFablePrivateTextureChainResource(replacement_shape)) {
    return;
  }

  uint32_t alias_address = g_direct_renderer.next_private_texture_upload_alias++;
  if (alias_address < 0xF1000000u) {
    alias_address = 0xF1000000u;
    g_direct_renderer.next_private_texture_upload_alias = alias_address + 1u;
  }
  while (g_direct_renderer.resources.find(alias_address) !=
         g_direct_renderer.resources.end()) {
    alias_address = g_direct_renderer.next_private_texture_upload_alias++;
  }

  const uint32_t original_address = old_resource.guest_address;
  const uint32_t original_parent = old_resource.parent_address;
  PlumeTrackedResource preserved = std::move(old_resource);
  preserved.guest_address = alias_address;
  preserved.parent_address =
      preserved.parent_address == original_address ? alias_address
                                                   : preserved.parent_address;
  preserved.serial = std::max<uint64_t>(preserved.serial,
                                        replacement_snapshot.serial);
  preserved.content_serial = std::max<uint64_t>(preserved.content_serial,
                                                replacement_snapshot.serial);
  preserved.guest_texture.object_address = alias_address;
  if (preserved.guest_texture.parent_address == original_address ||
      preserved.guest_texture.parent_address == 0) {
    preserved.guest_texture.parent_address = alias_address;
  }
  RefreshPlumeGuestResourceHandlesFromTrackedFields(preserved);

  RegisterDirectTextureIdentityFromResourceLocked(
      preserved, PlumeGuestTextureFetchDwords(preserved));
  g_direct_renderer.resources[alias_address] = std::move(preserved);
  InvalidateDirectResourceMemosForResourceLocked(
      g_direct_renderer.resources[alias_address]);

  if (g_direct_renderer.diagnostics.logged_private_rect_preserved_upload_aliases < 32u) {
    const PlumeTrackedResource& alias_resource =
        g_direct_renderer.resources[alias_address];
    REXLOG_INFO(
        "Refii plume preserved private upload alias #{} frame={} "
        "source=0x{:08X} alias=0x{:08X}/{}x{} fmt={} gen={} "
        "replacement={}x{} fmt={} kind={}",
        ++g_direct_renderer.diagnostics.logged_private_rect_preserved_upload_aliases,
        g_direct_renderer.current_frame_index, original_address, alias_address,
        PlumeGuestTextureWidth(alias_resource),
        PlumeGuestTextureHeight(alias_resource),
        static_cast<uint32_t>(PlumeGuestTextureFormat(alias_resource)),
        static_cast<unsigned long long>(alias_resource.upload_generation),
        replacement_shape.texture_width, replacement_shape.texture_height,
        static_cast<uint32_t>(replacement_shape.texture_format),
        TrackedResourceKindName(replacement_snapshot.kind));
  }
}

bool SyncBufferResourceLocked(plume::RenderDevice* device,
                              const FableGuestResourceSnapshot& snapshot,
                              PlumeTrackedResource& tracked,
                              DirectResourceSyncBreakdown* sync_timing) {
  (void)sync_timing;
  if (!device) {
    return false;
  }

  PopulateTrackedIdentity(tracked, snapshot);
  const uint32_t buffer_size = std::max<uint32_t>(snapshot.buffer.byte_size, 4u);
  const uint32_t previous_size =
      tracked.guest_buffer.byte_size != 0 ? tracked.guest_buffer.byte_size
                                          : tracked.size;
  tracked.size = buffer_size;
  tracked.buffer_object = snapshot.buffer.gpu_address;
  tracked.stride_or_format = snapshot.buffer.stride_or_index_format;
  tracked.guest_buffer.object_address = snapshot.buffer.object_address;
  tracked.guest_buffer.gpu_address = snapshot.buffer.gpu_address;
  tracked.guest_buffer.byte_size = buffer_size;
  tracked.guest_buffer.stride_or_index_format =
      snapshot.buffer.stride_or_index_format;
  tracked.guest_buffer.header_address = snapshot.buffer.header_address;
  tracked.guest_buffer.upload_hash = snapshot.buffer.upload_hash;
  tracked.guest_buffer.upload_generation = snapshot.buffer.upload_generation;
  tracked.guest_buffer.create_serial = snapshot.serial;
  tracked.guest_buffer.content_serial =
      std::max(tracked.guest_buffer.content_serial, snapshot.serial);
  tracked.guest_buffer.scratch = snapshot.buffer.scratch || snapshot.scratch;
  tracked.guest_buffer.plume_owned = true;

  const bool needs_buffer =
      !tracked.guest_buffer.buffer || previous_size != buffer_size;
  if (needs_buffer) {
    plume::RenderBufferDesc desc =
        snapshot.kind == FableGuestResourceKind::kIndexBuffer
            ? plume::RenderBufferDesc::IndexBuffer(buffer_size,
                                                   plume::RenderHeapType::UPLOAD)
            : plume::RenderBufferDesc::VertexBuffer(buffer_size,
                                                    plume::RenderHeapType::UPLOAD);
    tracked.guest_buffer.buffer = device->createBuffer(desc);
    NameTrackedBuffer(tracked.guest_buffer.buffer.get(), snapshot, tracked);
  }

  if (!tracked.guest_buffer.buffer) {
    return false;
  }
  const uint64_t before_generation = tracked.upload_generation;
  UploadTrackedBuffer(tracked, snapshot);
  if (tracked.upload_generation != before_generation) {
    ++g_direct_renderer.resource_uploads;
    LogDirectResourceUploadSyncLocked(snapshot,
                                      UploadCacheEntryForSnapshot(snapshot));
  }
  return true;
}

bool SyncImageResourceLocked(plume::RenderDevice* device,
                             const FableGuestResourceSnapshot& snapshot,
                             PlumeTrackedResource& tracked,
                             DirectResourceSyncBreakdown* sync_timing) {
  if (!device) {
    return false;
  }

  const FableDirectClock::time_point shape_t0 = FableDirectClock::now();
  if (tracked.guest_texture.texture) {
    PlumeTrackedResource decoded_shape;
    PopulateTrackedIdentity(decoded_shape, snapshot);
    // THE PROBE MUST MIRROR THE CREATE PATH, ROLE AND ALL (2026-08-04).
    //
    // This is the "what shape would we build now?" side of the comparison, so
    // it has to be computed the way CreateTextureForTrackedResource computes
    // it. Two things were wrong:
    //   1. it decoded BEFORE setting the role, and DecodeFableTextureMetadata
    //      promotes to output extent off exactly those flags, so the probe
    //      never promoted;
    //   2. it derived the role from snapshot.kind ALONE, so a render target the
    //      guest allocated as a TEXTURE (most of the scene chain — see
    //      Render_AllocateSceneRenderTargetSet 0x82A4E6D0) read rt=false.
    // Once kTexture render targets began promoting, `tracked` was promoted and
    // this probe was not, so the shapes could NEVER agree and the resource
    // retired on every sync — measured run 599: 51 of 64 logged retires were
    // cause=shape_changed with old and new IDENTICAL on every printed field,
    // the difference being the role flag alone.
    //
    // `requested_color_target` mirrors CreateTextureForTrackedResource: a
    // texture only keeps the render-target role if it already had it.
    const bool probe_requested_color_target =
        snapshot.kind == FableGuestResourceKind::kTexture &&
        tracked.texture_render_target && !tracked.texture_depth_target;
    decoded_shape.texture_render_target =
        snapshot.kind == FableGuestResourceKind::kSurface ||
        probe_requested_color_target;
    decoded_shape.texture_depth_target =
        snapshot.kind == FableGuestResourceKind::kDepthSurface;
    DecodeFableTextureMetadata(snapshot, decoded_shape);
    if (probe_requested_color_target &&
        IsBlockCompressedFormat(decoded_shape.texture_format)) {
      decoded_shape.texture_render_target =
          snapshot.kind == FableGuestResourceKind::kSurface;
    }
    ApplyDirectExactCopyDestinationCreateHintLocked(snapshot, decoded_shape);

    const DirectImageShape old_shape =
        DirectImageShapeForTrackedResource(tracked);
    const DirectImageShape new_shape =
        DirectImageShapeForTrackedResource(decoded_shape);
    const bool preserve_existing_shape =
        DirectImageShapeUsable(old_shape) && !DirectImageShapeUsable(new_shape) &&
        (tracked.texture_render_target || tracked.texture_depth_target ||
         IsFableTitleSceneTargetLocked(tracked.guest_address) ||
         DirectResourceIsTrackedSceneOutputLocked(tracked) ||
         IsFablePrivateTextureChainResource(tracked));
    if (preserve_existing_shape) {
      tracked.serial = std::max<uint64_t>(tracked.serial, snapshot.serial);
      tracked.content_serial =
          std::max<uint64_t>(tracked.content_serial, snapshot.serial);
      tracked.scratch = tracked.scratch || snapshot.scratch;
      if (g_direct_renderer.diagnostics.logged_preserved_zero_shape_snapshots < 32u) {
        REXLOG_INFO(
            "Refii plume preserved zero-shape image snapshot #{} frame={} "
            "guest=0x{:08X} kind={} kept={}x{} fmt={} rt={} ds={} "
            "snapshot_serial={}",
            ++g_direct_renderer.diagnostics.logged_preserved_zero_shape_snapshots,
            g_direct_renderer.current_frame_index, snapshot.guest_address,
            TrackedResourceKindName(snapshot.kind), old_shape.width,
            old_shape.height, static_cast<uint32_t>(old_shape.format),
            tracked.texture_render_target ? 1 : 0,
            tracked.texture_depth_target ? 1 : 0, snapshot.serial);
      }
      if (sync_timing) {
        sync_timing->image_shape_ms += FableDirectElapsedMs(
            shape_t0, FableDirectClock::now());
      }
      return true;
    }
    const bool live_extent_stale =
        DirectTrackedImageExtentStale(tracked, new_shape);
    if (!DirectImageShapeEquals(old_shape, new_shape) || live_extent_stale) {
      if ((tracked.texture_render_target || tracked.texture_depth_target ||
           IsSurfaceResource(snapshot.kind)) &&
          g_direct_renderer.diagnostics.logged_surface_shape_retires < 64u) {
        REXLOG_INFO(
            "Refii plume surface shape retire #{} frame={} guest=0x{:08X} "
            "kind={} old={}x{} fmt={} new={}x{} fmt={} live_image={}x{} "
            "cause={} rt={} ds={} content_serial={}",
            ++g_direct_renderer.diagnostics.logged_surface_shape_retires,
            g_direct_renderer.current_frame_index, snapshot.guest_address,
            TrackedResourceKindName(snapshot.kind), old_shape.width,
            old_shape.height, static_cast<uint32_t>(old_shape.format),
            new_shape.width, new_shape.height,
            static_cast<uint32_t>(new_shape.format),
            tracked.guest_texture.image_width,
            tracked.guest_texture.image_height,
            live_extent_stale ? "live_extent_stale" : "shape_changed",
            tracked.texture_render_target ? 1 : 0,
            tracked.texture_depth_target ? 1 : 0, tracked.content_serial);
      }
      PreserveFableTitleBillboardTileUploadAliasLocked(snapshot, tracked);
      PreserveDirectPrivateTextureUploadAliasLocked(snapshot, tracked);
      ForgetDrawnSceneTargetLocked(snapshot.guest_address, &tracked);
      InvalidateFableDescriptorCacheLocked();
      // Same-extent recreation of a color target: queue the pixels for a
      // GPU copy into the replacement (recorded before the next draws).
      // Keep the old texture's known-layout entry so the carry barrier
      // transitions from its real layout instead of UNDEFINED.
      const bool carry_content =
          (tracked.texture_render_target ||
           IsSurfaceResource(snapshot.kind)) &&
          !tracked.texture_depth_target && tracked.guest_texture.texture &&
          tracked.content_serial != 0 &&
          old_shape.width == new_shape.width &&
          old_shape.height == new_shape.height &&
          old_shape.depth == new_shape.depth &&
          old_shape.format == new_shape.format &&
          g_direct_renderer.pending_surface_content_carries.size() < 32u;
      if (carry_content) {
        // Shape ping-pong can retire the just-created intermediate before
        // any flush; it holds no pixels, so the oldest pending carry per
        // address keeps authority (fresh content_serial values lie — they
        // inherit snapshot.serial at create).
        bool already_pending = false;
        for (const PendingSurfaceContentCarry& pending :
             g_direct_renderer.pending_surface_content_carries) {
          if (pending.guest_address == snapshot.guest_address) {
            already_pending = true;
            break;
          }
        }
        if (!already_pending) {
          PendingSurfaceContentCarry carry;
          carry.old_texture = std::move(tracked.guest_texture.texture);
          carry.guest_address = snapshot.guest_address;
          carry.width = old_shape.width;
          carry.height = old_shape.height;
          carry.format = old_shape.format;
          carry.queued_frame = g_direct_renderer.current_frame_index;
          carry.content_serial = tracked.content_serial;
          g_direct_renderer.pending_surface_content_carries.push_back(
              std::move(carry));
        }
      }
      ForgetDirectTextureLayoutLocked(tracked.guest_texture.texture.get());
      RetirePlumeResourceLocked(std::move(tracked), snapshot.serial);
      tracked = PlumeTrackedResource{};
    }
  }
  const FableDirectClock::time_point shape_t1 = FableDirectClock::now();

  const FableDirectClock::time_point metadata_t0 = shape_t1;
  PopulateTrackedIdentity(tracked, snapshot);
  PlumeTrackedResource decoded_shape_probe;
  bool exact_copy_destination_create_hint = false;
  if (snapshot.kind == FableGuestResourceKind::kTexture) {
    PopulateTrackedIdentity(decoded_shape_probe, snapshot);
    DecodeFableTextureMetadata(snapshot, decoded_shape_probe);
    decoded_shape_probe.texture_render_target = false;
    decoded_shape_probe.texture_depth_target = false;
    ApplyDirectExactCopyDestinationCreateHintLocked(snapshot,
                                                   decoded_shape_probe);
    exact_copy_destination_create_hint =
        decoded_shape_probe.texture_render_target &&
        !decoded_shape_probe.texture_depth_target;
    if (exact_copy_destination_create_hint && !tracked.guest_texture.texture) {
      tracked.texture_render_target = true;
      tracked.texture_depth_target = false;
    }
  }
  const bool first_texture = !tracked.guest_texture.texture;
  const FableDirectClock::time_point metadata_t1 = FableDirectClock::now();
	  FableDirectClock::time_point create_t0 = metadata_t1;
	  FableDirectClock::time_point create_t1 = create_t0;
	  FableDirectClock::time_point view_t0 = create_t0;
	  FableDirectClock::time_point view_t1 = view_t0;
	  if (first_texture) {
    create_t0 = FableDirectClock::now();
    tracked.guest_texture.texture =
        CreateTextureForTrackedResource(device, snapshot, tracked);
    create_t1 = FableDirectClock::now();
    if (!tracked.guest_texture.texture) {
      return false;
    }
    // CreateTextureForTrackedResource builds its desc from these accessors, so
    // they are exactly the image's real shape at this instant. Record it before
    // any later decode can relabel the metadata out from under the image.
    StampPlumeLiveImageShape(tracked, PlumeGuestTextureWidth(tracked),
                             PlumeGuestTextureHeight(tracked));
    ForgetDirectTextureLayoutLocked(tracked.guest_texture.texture.get());
    NameTrackedTexture(tracked.guest_texture.texture.get(), snapshot, tracked);
    if ((tracked.texture_render_target || tracked.texture_depth_target ||
         IsSurfaceResource(snapshot.kind)) &&
        g_direct_renderer.diagnostics.logged_surface_texture_creates < 128u) {
      REXLOG_INFO(
          "Refii plume surface texture create #{} frame={} guest=0x{:08X} "
          "kind={} {}x{} fmt={} rt={} ds={} serial={}",
          ++g_direct_renderer.diagnostics.logged_surface_texture_creates,
          g_direct_renderer.current_frame_index, snapshot.guest_address,
          TrackedResourceKindName(snapshot.kind), tracked.texture_width,
          tracked.texture_height,
          static_cast<uint32_t>(tracked.texture_format),
          tracked.texture_render_target ? 1 : 0,
          tracked.texture_depth_target ? 1 : 0, snapshot.serial);
    }
    view_t0 = FableDirectClock::now();
    if (!RefreshDirectTextureSampleViewLocked(tracked)) {
      return false;
    }
    if (IsSurfaceResource(snapshot.kind) ||
        (tracked.texture_render_target && !tracked.texture_depth_target)) {
      tracked.guest_surface.attachment_view =
          tracked.guest_texture.texture->createTextureView(
              plume::RenderTextureViewDesc::Texture2D(tracked.texture_format));
    }
    RefreshPlumeGuestResourceHandlesFromTrackedFields(tracked);
    view_t1 = FableDirectClock::now();
  } else {
    const FableDirectClock::time_point metadata_decode_t0 =
        FableDirectClock::now();
    const uint32_t previous_texture_base =
        PlumeGuestTextureBaseAddress(tracked);
	    const std::array<uint32_t, 6> previous_fetch_dwords =
	        PlumeGuestTextureFetchDwords(tracked);
	    DecodeFableTextureMetadata(snapshot, tracked);
    ApplyDirectExactCopyDestinationCreateHintLocked(snapshot, tracked);
    PreserveFableTitleBillboardAtlasUploadIdentityLocked(
        tracked, previous_texture_base, previous_fetch_dwords);
    const FableDirectClock::time_point metadata_decode_t1 =
        FableDirectClock::now();
    view_t0 = metadata_decode_t1;
    if (!RefreshDirectTextureSampleViewLocked(tracked)) {
      return false;
    }
    view_t1 = FableDirectClock::now();
    if (sync_timing) {
      sync_timing->image_metadata_ms +=
          FableDirectElapsedMs(metadata_decode_t0, metadata_decode_t1);
    }
  }

  const uint64_t before_generation = tracked.upload_generation;
  const FableDirectClock::time_point upload_t0 = FableDirectClock::now();
  const bool upload_sync_ok =
      RefreshTrackedTextureUpload(device, snapshot, tracked);
  if (upload_sync_ok && tracked.upload_generation != before_generation) {
    ++g_direct_renderer.resource_uploads;
    LogDirectResourceUploadSyncLocked(snapshot,
                                      UploadCacheEntryForSnapshot(snapshot));
  }
  const FableDirectClock::time_point upload_t1 = FableDirectClock::now();
  if (sync_timing) {
    sync_timing->image_shape_ms += FableDirectElapsedMs(shape_t0, shape_t1);
    sync_timing->image_metadata_ms +=
        FableDirectElapsedMs(metadata_t0, metadata_t1);
    sync_timing->image_create_ms += FableDirectElapsedMs(create_t0, create_t1);
    sync_timing->image_view_ms += FableDirectElapsedMs(view_t0, view_t1);
    sync_timing->image_upload_ms += FableDirectElapsedMs(upload_t0, upload_t1);
    ++sync_timing->image_upload_calls;
  }
  return true;
}

bool SyncDeclarationResourceLocked(const FableGuestResourceSnapshot& snapshot,
                                   PlumeTrackedResource& tracked,
                                   DirectResourceSyncBreakdown* sync_timing) {
  (void)sync_timing;
  const bool can_reuse_decoded_declaration =
      snapshot.upload_data.empty() &&
      tracked.guest_vertex_declaration.elements_hash ==
          snapshot.vertex_declaration.elements_hash &&
      !tracked.guest_vertex_declaration.input_elements.empty();
  PopulateTrackedIdentity(tracked, snapshot);
  tracked.size = snapshot.vertex_declaration.elements_size;
  tracked.guest_vertex_declaration.object_address =
      snapshot.vertex_declaration.object_address;
  tracked.guest_vertex_declaration.elements_address =
      snapshot.vertex_declaration.elements_address;
  tracked.guest_vertex_declaration.elements_size =
      snapshot.vertex_declaration.elements_size;
  tracked.guest_vertex_declaration.element_count =
      snapshot.vertex_declaration.element_count;
  tracked.guest_vertex_declaration.elements_hash =
      snapshot.vertex_declaration.elements_hash;
  tracked.guest_vertex_declaration.create_serial = snapshot.serial;
  tracked.guest_vertex_declaration.content_serial =
      std::max(tracked.guest_vertex_declaration.content_serial,
               snapshot.serial);
  tracked.guest_vertex_declaration.plume_owned = true;
  if (can_reuse_decoded_declaration) {
    return true;
  }
  if (DecodeVertexDeclaration(tracked, snapshot)) {
    return true;
  }

  // ADDRESS GATE FAMILY (census: decl_fallback_*) — DE-LITERALISED
  // 2026-08-02. Each block was `object address == <one-run literal> ||
  // elements hash == <known hash>`. The guest-address halves were dropped:
  //  (1) census evidence — in the default-on census era (runs through 288,
  //      full title runs to frame ~2952) all five decl_fallback gates
  //      reported in `never_tested`: DecodeVertexDeclaration never failed,
  //      so the fallback path never executed and the address literals
  //      guarded nothing observed;
  //  (2) resolution safety — the literals are guest scratch-heap object
  //      addresses whose layout shifts when the native-resolution override
  //      resizes the RT set, so they could only ever FALSELY match under a
  //      changed resolution.
  // The elements hash (content identity) plus element count now decides
  // alone; a fallback need whose hash was unreadable will surface as a
  // DecodeVertexDeclaration failure, which is the honest signal.
  const uint64_t declaration_elements_hash =
      snapshot.vertex_declaration.elements_hash;
  const bool gui_quad_declaration_hash =
      declaration_elements_hash == kFableGuiQuadDeclarationHash ||
      declaration_elements_hash == kFableSingleStreamQuadDeclarationHash;
  if (DirectAddressGateTest(
          DirectAddressGate::kDeclarationFallbackGuiQuad,
          gui_quad_declaration_hash &&
              snapshot.vertex_declaration.element_count == 1u,
          snapshot.guest_address, declaration_elements_hash,
          gui_quad_declaration_hash)) {
    FableGuestResourceSnapshot known = snapshot;
    static constexpr std::array<uint8_t, 12> kGuiQuadDeclaration = {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x2C,
        0x23, 0xA5, 0x00, 0x00, 0x00, 0x00};
    known.upload_data.assign(kGuiQuadDeclaration.begin(),
                             kGuiQuadDeclaration.end());
    known.vertex_declaration.elements_size =
        static_cast<uint32_t>(known.upload_data.size());
    return DecodeVertexDeclaration(tracked, known);
  }

  if (DirectAddressGateTest(
          DirectAddressGate::kDeclarationFallbackPrivateRect,
          declaration_elements_hash == kFablePrivateRectDeclarationHash &&
              snapshot.vertex_declaration.element_count == 2u,
          snapshot.guest_address, declaration_elements_hash,
          declaration_elements_hash == kFablePrivateRectDeclarationHash)) {
    FableGuestResourceSnapshot known = snapshot;
    static constexpr std::array<uint8_t, 24> kPrivateRectDeclaration = {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x23, 0xB9,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C,
        0x00, 0x2C, 0x23, 0xA5, 0x00, 0x05, 0x00, 0x00};
    known.upload_data.assign(kPrivateRectDeclaration.begin(),
                             kPrivateRectDeclaration.end());
    known.vertex_declaration.elements_size =
        static_cast<uint32_t>(known.upload_data.size());
    return DecodeVertexDeclaration(tracked, known);
  }

  if (DirectAddressGateTest(
          DirectAddressGate::kDeclarationFallbackTitleIndexed,
          declaration_elements_hash == kFableTitleIndexedDeclarationHash &&
              snapshot.vertex_declaration.element_count == 8u,
          snapshot.guest_address, declaration_elements_hash,
          declaration_elements_hash == kFableTitleIndexedDeclarationHash)) {
    FableGuestResourceSnapshot known = snapshot;
    static constexpr std::array<uint8_t, 96> kTitleIndexedDeclaration = {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x23, 0xB9, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x00, 0x2A, 0x23, 0xB9,
        0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x18, 0x00, 0x2A,
        0x23, 0xB9, 0x00, 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x24,
        0x00, 0x2A, 0x23, 0xB9, 0x00, 0x07, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x30, 0x00, 0x2C, 0x23, 0xA5, 0x00, 0x05, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x38, 0x00, 0x2C, 0x23, 0xA5, 0x00, 0x05,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x2C, 0x83, 0xA4,
        0x00, 0x05, 0x02, 0x00, 0x00, 0x00, 0x00, 0x44, 0x00, 0x18,
        0x28, 0x86, 0x00, 0x0A, 0x00, 0x00};
    known.upload_data.assign(kTitleIndexedDeclaration.begin(),
                             kTitleIndexedDeclaration.end());
    known.vertex_declaration.elements_size =
        static_cast<uint32_t>(known.upload_data.size());
    return DecodeVertexDeclaration(tracked, known);
  }

  if (DirectAddressGateTest(
          DirectAddressGate::kDeclarationFallbackRepeatedMesh,
          declaration_elements_hash == kFableRepeatedMeshDeclarationHash &&
              snapshot.vertex_declaration.element_count == 3u,
          snapshot.guest_address, declaration_elements_hash,
          declaration_elements_hash == kFableRepeatedMeshDeclarationHash)) {
    FableGuestResourceSnapshot known = snapshot;
    static constexpr std::array<uint8_t, 36> kRepeatedMeshDeclaration = {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x1A, 0x23, 0xA6, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x1A, 0x23, 0xA6,
        0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x1A,
        0x23, 0xA6, 0x00, 0x05, 0x01, 0x00};
    known.upload_data.assign(kRepeatedMeshDeclaration.begin(),
                             kRepeatedMeshDeclaration.end());
    known.vertex_declaration.elements_size =
        static_cast<uint32_t>(known.upload_data.size());
    return DecodeVertexDeclaration(tracked, known);
  }

  if (DirectAddressGateTest(
          DirectAddressGate::kDeclarationFallbackTitleBillboardMesh,
          declaration_elements_hash ==
                  kFableTitleBillboardMeshDeclarationHash &&
              snapshot.vertex_declaration.element_count == 4u,
          snapshot.guest_address, declaration_elements_hash,
          declaration_elements_hash ==
              kFableTitleBillboardMeshDeclarationHash)) {
    FableGuestResourceSnapshot known = snapshot;
    static constexpr std::array<uint8_t, 48> kTitleBillboardMeshDeclaration = {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x1A, 0x23, 0xA6, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x1A, 0x23, 0xA6,
        0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x00, 0x1A,
        0x23, 0xA6, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x30,
        0x00, 0x1A, 0x23, 0xA6, 0x00, 0x05, 0x01, 0x00};
    known.upload_data.assign(kTitleBillboardMeshDeclaration.begin(),
                             kTitleBillboardMeshDeclaration.end());
    known.vertex_declaration.elements_size =
        static_cast<uint32_t>(known.upload_data.size());
    return DecodeVertexDeclaration(tracked, known);
  }

  return false;
}

bool SyncShaderResourceLocked(const FableGuestResourceSnapshot& snapshot,
                              PlumeTrackedResource& tracked,
                              DirectResourceSyncBreakdown* sync_timing) {
  (void)sync_timing;
  PopulateTrackedIdentity(tracked, snapshot);
  tracked.size = snapshot.shader.bytecode_size;
  tracked.guest_bytecode_hash = snapshot.shader.bytecode_hash;
  tracked.guest_shader.object_address = snapshot.shader.object_address;
  tracked.guest_shader.bytecode_address = snapshot.shader.bytecode_address;
  tracked.guest_shader.bytecode_size = snapshot.shader.bytecode_size;
  tracked.guest_shader.bytecode_offset = snapshot.shader.bytecode_offset;
  tracked.guest_shader.bytecode_hash = snapshot.shader.bytecode_hash;
  tracked.guest_shader.create_serial = snapshot.serial;
  tracked.guest_shader.content_serial =
      std::max(tracked.guest_shader.content_serial, snapshot.serial);
  tracked.guest_shader.plume_owned = true;
  tracked.pixel_shader_texture_slot_mask_valid = false;
  tracked.pixel_shader_texture_slot_mask = 0;
  char name[128] = {};
  std::snprintf(name, sizeof(name), "Refii Fable %s guest=0x%08X hash=%016llX",
                TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
                static_cast<unsigned long long>(snapshot.shader.bytecode_hash));
  tracked.guest_shader.name = name;
  tracked.guest_shader.shader.reset();
  RendererShaderLibraryEntry entry;
  if (!FindRendererShaderLibraryEntryByGuestHash(snapshot.shader.bytecode_hash,
                                                entry) ||
      entry.binary_path.empty()) {
    if (g_direct_renderer.diagnostics.logged_shader_sync_failures
            .insert(snapshot.shader.bytecode_hash)
            .second) {
      REXLOG_WARN(
          "Refii plume direct shader has no generated artifact guest=0x{:08X} "
          "kind={} hash=0x{:016X} size={}",
          snapshot.guest_address, TrackedResourceKindName(snapshot.kind),
          snapshot.shader.bytecode_hash, snapshot.shader.bytecode_size);
    }
    return true;
  }
  tracked.pixel_shader_texture_slot_mask_valid =
      snapshot.kind == FableGuestResourceKind::kPixelShader &&
      entry.texture_slot_mask_valid;
  tracked.pixel_shader_texture_slot_mask =
      tracked.pixel_shader_texture_slot_mask_valid ? entry.texture_slot_mask
                                                   : 0;
  std::vector<uint8_t> bytes;
  if (!ReadBinaryFile(entry.binary_path, bytes)) {
    if (g_direct_renderer.diagnostics.logged_shader_sync_failures
            .insert(snapshot.shader.bytecode_hash)
            .second) {
      REXLOG_WARN(
          "Refii plume direct shader artifact read failed guest=0x{:08X} "
          "hash=0x{:016X} path={}",
          snapshot.guest_address, snapshot.shader.bytecode_hash,
          entry.binary_path);
    }
    return true;
  }
  tracked.guest_shader.shader =
      g_direct_renderer.device
          ? g_direct_renderer.device->createShader(
                bytes.data(), bytes.size(), "main",
                g_direct_renderer.shader_format)
          : nullptr;
  if (tracked.guest_shader.shader) {
    tracked.guest_shader.shader->setName(entry.name);
    tracked.guest_shader.name = entry.name;
    tracked.debug_summary = entry.name;
    if (g_direct_renderer.diagnostics.logged_shader_sync_hashes
            .insert(snapshot.shader.bytecode_hash)
            .second &&
        g_direct_renderer.diagnostics.logged_shader_sync_hashes.size() <= 64) {
      REXLOG_INFO(
          "Refii plume direct shader linked guest=0x{:08X} kind={} "
          "hash=0x{:016X} entry={} bytes={}",
          snapshot.guest_address, TrackedResourceKindName(snapshot.kind),
          snapshot.shader.bytecode_hash, entry.name, bytes.size());
    }
  } else if (g_direct_renderer.diagnostics.logged_shader_sync_failures
                 .insert(snapshot.shader.bytecode_hash)
                 .second) {
    REXLOG_WARN(
        "Refii plume direct shader creation failed guest=0x{:08X} kind={} "
        "hash=0x{:016X} entry={} bytes={}",
        snapshot.guest_address, TrackedResourceKindName(snapshot.kind),
        snapshot.shader.bytecode_hash, entry.name, bytes.size());
  }
  return true;
}

void RegisterDirectShaderAndDeclarationIdentityFromSnapshotLocked(
    const FableGuestResourceSnapshot& snapshot) {
  switch (snapshot.kind) {
    case FableGuestResourceKind::kVertexShader:
    case FableGuestResourceKind::kPixelShader: {
      FableDirectForgetVertexDeclarationIdentity(snapshot.guest_address);
      if (snapshot.guest_address == 0 ||
          snapshot.shader.bytecode_hash == 0) {
        FableDirectForgetShaderIdentity(snapshot.guest_address);
        return;
      }
      FableShaderIdentity identity;
      identity.kind = snapshot.kind;
      identity.object_shader = snapshot.shader.object_address != 0
                                   ? snapshot.shader.object_address
                                   : snapshot.guest_address;
      identity.parent_shader = snapshot.parent_address;
      identity.bytecode_address = snapshot.shader.bytecode_address;
      identity.bytecode_size = snapshot.shader.bytecode_size;
      identity.bytecode_offset = snapshot.shader.bytecode_offset;
      identity.bytecode_hash = snapshot.shader.bytecode_hash;
      FableDirectRegisterShaderIdentity(identity);
      return;
    }
    case FableGuestResourceKind::kVertexDeclaration: {
      FableDirectForgetShaderIdentity(snapshot.guest_address);
      if (snapshot.guest_address == 0 ||
          snapshot.vertex_declaration.elements_hash == 0 ||
          snapshot.vertex_declaration.elements_size == 0 ||
          snapshot.vertex_declaration.element_count == 0) {
        FableDirectForgetVertexDeclarationIdentity(snapshot.guest_address);
        return;
      }
      FableVertexDeclarationIdentity identity;
      identity.object_declaration =
          snapshot.vertex_declaration.object_address != 0
              ? snapshot.vertex_declaration.object_address
              : snapshot.guest_address;
      identity.parent_declaration = snapshot.parent_address;
      identity.elements_address =
          snapshot.vertex_declaration.elements_address;
      identity.elements_size = snapshot.vertex_declaration.elements_size;
      identity.element_count = snapshot.vertex_declaration.element_count;
      identity.max_stream = snapshot.vertex_declaration.max_stream;
      identity.stream_mask = snapshot.vertex_declaration.stream_mask;
      identity.declaration_flags =
          snapshot.vertex_declaration.declaration_flags;
      identity.elements_hash = snapshot.vertex_declaration.elements_hash;
      FableDirectRegisterVertexDeclarationIdentity(identity);
      return;
    }
    default:
      FableDirectForgetShaderIdentity(snapshot.guest_address);
      FableDirectForgetVertexDeclarationIdentity(snapshot.guest_address);
      return;
  }
}

bool EnsureDirectShaderResourceWithKnownHashLocked(
    uint32_t shader_address, FableGuestResourceKind kind, uint64_t hash) {
  if (shader_address == 0 || hash == 0 || !g_direct_renderer.device) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(shader_address);
      existing && PlumeGuestShaderReady(*existing)) {
    return true;
  }

  FableGuestResourceSnapshot snapshot;
  snapshot.kind = kind;
  snapshot.guest_address = shader_address;
  snapshot.parent_address = shader_address;
  snapshot.serial = std::max<uint64_t>(1ull, g_direct_renderer.resource_serial);
  snapshot.shader.object_address = shader_address;
  snapshot.shader.bytecode_hash = hash;
  RegisterDirectShaderAndDeclarationIdentityFromSnapshotLocked(snapshot);
  PlumeTrackedResource& tracked = g_direct_renderer.resources[shader_address];
  const bool inserted = tracked.guest_address == 0;
  const bool synced = SyncShaderResourceLocked(snapshot, tracked, nullptr);
  InvalidateDirectResourceMemoKeyLocked(shader_address);
  InvalidateDirectResourceMemosForResourceLocked(tracked);
  if (!synced) {
    return false;
  }
  if (inserted) {
    ++g_direct_renderer.resource_creations;
  }
  return PlumeGuestShaderReady(tracked);
}

bool EnsureDirectShaderResourceFromObservedIdentityLocked(
    uint32_t shader_address) {
  const FableShaderIdentity* identity =
      FableDirectFindShaderIdentityByObject(shader_address);
  if (!identity || identity->bytecode_hash == 0 ||
      identity->kind == FableGuestResourceKind::kUnknown) {
    return false;
  }
  const bool ready = EnsureDirectShaderResourceWithKnownHashLocked(
      shader_address, identity->kind, identity->bytecode_hash);
  if (ready &&
      g_direct_renderer.diagnostics.logged_observed_shader_identity_hits < 64u) {
    REXLOG_INFO(
        "Refii plume direct observed shader identity #{} guest=0x{:08X} "
        "kind={} hash=0x{:016X} bytecode=0x{:08X} bytes={} serial={}",
        ++g_direct_renderer.diagnostics.logged_observed_shader_identity_hits,
        shader_address, TrackedResourceKindName(identity->kind),
        identity->bytecode_hash, identity->bytecode_address,
        identity->bytecode_size,
        static_cast<unsigned long long>(identity->serial));
  }
  return ready;
}

bool EnsureDirectShaderResourceFromRegistryRecordLocked(
    uint32_t shader_address) {
  if (shader_address == 0 || !g_direct_renderer.device) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(shader_address);
      existing && PlumeGuestShaderReady(*existing)) {
    return true;
  }

  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  const auto record_it = registry.resources.find(shader_address);
  if (record_it == registry.resources.end() ||
      !IsShaderResourceKind(GuestObjectKind(record_it->second))) {
    return false;
  }

  const GuestShaderRecord shader_record =
      GuestShaderRecordFor(record_it->second);
  if (shader_record.bytecode_hash == 0) {
    return false;
  }

  FableGuestResourceKind kind = FableGuestResourceKind::kUnknown;
  switch (GuestObjectKind(record_it->second)) {
    case RendererResourceKind::kVertexShader:
      kind = FableGuestResourceKind::kVertexShader;
      break;
    case RendererResourceKind::kPixelShader:
      kind = FableGuestResourceKind::kPixelShader;
      break;
    default:
      return false;
  }

  FableGuestResourceSnapshot snapshot;
  snapshot.kind = kind;
  snapshot.guest_address =
      shader_record.object_address != 0 ? shader_record.object_address
                                        : shader_address;
  snapshot.parent_address = GuestObjectParentAddress(record_it->second);
  snapshot.serial = std::max<uint64_t>(1ull, record_it->second.serial);
  snapshot.shader.object_address = shader_record.object_address != 0
                                       ? shader_record.object_address
                                       : shader_address;
  snapshot.shader.bytecode_address = shader_record.bytecode_address;
  snapshot.shader.bytecode_size = shader_record.bytecode_size;
  snapshot.shader.bytecode_offset = shader_record.bytecode_offset;
  snapshot.shader.bytecode_hash = shader_record.bytecode_hash;

  RegisterDirectShaderAndDeclarationIdentityFromSnapshotLocked(snapshot);
  PlumeTrackedResource& tracked =
      g_direct_renderer.resources[snapshot.guest_address];
  tracked.last_referenced_frame = g_direct_renderer.current_frame_index;
  const bool inserted = tracked.guest_address == 0;
  const bool synced = SyncShaderResourceLocked(snapshot, tracked, nullptr);
  InvalidateDirectResourceMemoKeyLocked(snapshot.guest_address);
  InvalidateDirectResourceMemosForResourceLocked(tracked);
  if (!synced) {
    return false;
  }
  if (inserted) {
    ++g_direct_renderer.resource_creations;
  }
  if (PlumeGuestShaderReady(tracked) &&
      g_direct_renderer.diagnostics.logged_observed_shader_identity_hits < 64u) {
    REXLOG_INFO(
        "Refii plume direct registry shader identity #{} guest=0x{:08X} "
        "kind={} bytecode=0x{:08X} bytes={} hash=0x{:016X} serial={}",
        ++g_direct_renderer.diagnostics.logged_observed_shader_identity_hits,
        shader_address, TrackedResourceKindName(kind),
        shader_record.bytecode_address, shader_record.bytecode_size,
        shader_record.bytecode_hash,
        static_cast<unsigned long long>(record_it->second.serial));
  }
  return PlumeGuestShaderReady(tracked);
}

bool EnsureDirectVertexDeclarationResourceFromObservedIdentityLocked(
    uint32_t declaration) {
  if (declaration == 0 || !g_direct_renderer.device) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(declaration);
      existing && PlumeGuestDeclarationReady(*existing)) {
    return true;
  }

  const FableVertexDeclarationIdentity* identity =
      FableDirectFindVertexDeclarationIdentityByObject(declaration);
  if (!identity || identity->elements_hash == 0 ||
      identity->elements_size == 0 || identity->element_count == 0) {
    return false;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto record_it = registry.resources.find(identity->object_declaration);
  if (record_it == registry.resources.end() ||
      GuestObjectKind(record_it->second) !=
          RendererResourceKind::kVertexDeclaration ||
      record_it->second.upload_data.empty()) {
    return false;
  }

  const GuestVertexDeclarationRecord declaration_record =
      GuestVertexDeclarationRecordFor(record_it->second);
  if (declaration_record.elements_hash != identity->elements_hash ||
      declaration_record.elements_size == 0 ||
      declaration_record.element_count == 0) {
    return false;
  }

  FableGuestResourceSnapshot snapshot;
  snapshot.kind = FableGuestResourceKind::kVertexDeclaration;
  snapshot.guest_address = identity->object_declaration;
  snapshot.parent_address = identity->parent_declaration;
  snapshot.serial = std::max<uint64_t>(1ull, record_it->second.serial);
  snapshot.upload_generation = record_it->second.upload_generation;
  snapshot.vertex_declaration.object_address =
      declaration_record.object_address;
  snapshot.vertex_declaration.elements_address =
      declaration_record.elements_address;
  snapshot.vertex_declaration.elements_size =
      declaration_record.elements_size;
  snapshot.vertex_declaration.element_count =
      declaration_record.element_count;
  snapshot.vertex_declaration.max_stream = declaration_record.max_stream;
  snapshot.vertex_declaration.stream_mask = declaration_record.stream_mask;
  snapshot.vertex_declaration.declaration_flags =
      declaration_record.declaration_flags;
  snapshot.vertex_declaration.elements_hash =
      declaration_record.elements_hash;
  snapshot.upload_data = record_it->second.upload_data;

  PlumeTrackedResource& tracked =
      g_direct_renderer.resources[identity->object_declaration];
  const bool inserted = tracked.guest_address == 0;
  const bool synced = SyncDeclarationResourceLocked(snapshot, tracked, nullptr);
  InvalidateDirectResourceMemoKeyLocked(identity->object_declaration);
  InvalidateDirectResourceMemosForResourceLocked(tracked);
  if (!synced) {
    return false;
  }
  if (inserted) {
    ++g_direct_renderer.resource_creations;
  }
  if (PlumeGuestDeclarationReady(tracked) &&
      g_direct_renderer.diagnostics.logged_observed_declaration_identity_hits < 64u) {
    REXLOG_INFO(
        "Refii plume direct observed declaration identity #{} "
        "guest=0x{:08X} elements=0x{:08X} bytes={} count={} "
        "hash=0x{:016X} serial={}",
        ++g_direct_renderer.diagnostics.logged_observed_declaration_identity_hits,
        declaration, declaration_record.elements_address,
        declaration_record.elements_size, declaration_record.element_count,
        declaration_record.elements_hash,
        static_cast<unsigned long long>(identity->serial));
  }
  return PlumeGuestDeclarationReady(tracked);
}

bool EnsureDirectVertexDeclarationResourceFromRegistryRecordLocked(
    uint32_t declaration) {
  if (declaration == 0 || !g_direct_renderer.device) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(declaration);
      existing && PlumeGuestDeclarationReady(*existing)) {
    return true;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto record_it = registry.resources.find(declaration);
  if (record_it == registry.resources.end() ||
      GuestObjectKind(record_it->second) !=
          RendererResourceKind::kVertexDeclaration ||
      record_it->second.upload_data.empty()) {
    return false;
  }

  const GuestVertexDeclarationRecord declaration_record =
      GuestVertexDeclarationRecordFor(record_it->second);
  if (declaration_record.elements_hash == 0 ||
      declaration_record.elements_size == 0 ||
      declaration_record.element_count == 0) {
    return false;
  }

  FableVertexDeclarationIdentity identity;
  identity.object_declaration =
      declaration_record.object_address != 0 ? declaration_record.object_address
                                             : declaration;
  identity.parent_declaration = GuestObjectParentAddress(record_it->second);
  identity.elements_address = declaration_record.elements_address;
  identity.elements_size = declaration_record.elements_size;
  identity.element_count = declaration_record.element_count;
  identity.max_stream = declaration_record.max_stream;
  identity.stream_mask = declaration_record.stream_mask;
  identity.declaration_flags = declaration_record.declaration_flags;
  identity.elements_hash = declaration_record.elements_hash;
  FableDirectRegisterVertexDeclarationIdentity(identity);

  FableGuestResourceSnapshot snapshot;
  snapshot.kind = FableGuestResourceKind::kVertexDeclaration;
  snapshot.guest_address = identity.object_declaration;
  snapshot.parent_address = identity.parent_declaration;
  snapshot.serial = std::max<uint64_t>(1ull, record_it->second.serial);
  snapshot.upload_generation = record_it->second.upload_generation;
  snapshot.vertex_declaration.object_address =
      declaration_record.object_address;
  snapshot.vertex_declaration.elements_address =
      declaration_record.elements_address;
  snapshot.vertex_declaration.elements_size =
      declaration_record.elements_size;
  snapshot.vertex_declaration.element_count =
      declaration_record.element_count;
  snapshot.vertex_declaration.max_stream = declaration_record.max_stream;
  snapshot.vertex_declaration.stream_mask = declaration_record.stream_mask;
  snapshot.vertex_declaration.declaration_flags =
      declaration_record.declaration_flags;
  snapshot.vertex_declaration.elements_hash =
      declaration_record.elements_hash;
  snapshot.upload_data = record_it->second.upload_data;

  PlumeTrackedResource& tracked =
      g_direct_renderer.resources[identity.object_declaration];
  const bool inserted = tracked.guest_address == 0;
  const bool synced = SyncDeclarationResourceLocked(snapshot, tracked, nullptr);
  InvalidateDirectResourceMemoKeyLocked(identity.object_declaration);
  InvalidateDirectResourceMemosForResourceLocked(tracked);
  if (!synced) {
    return false;
  }
  if (inserted) {
    ++g_direct_renderer.resource_creations;
  }
  if (PlumeGuestDeclarationReady(tracked) &&
      g_direct_renderer.diagnostics.logged_observed_declaration_identity_hits < 64u) {
    REXLOG_INFO(
        "Refii plume direct registry declaration identity #{} "
        "guest=0x{:08X} elements=0x{:08X} bytes={} count={} "
        "hash=0x{:016X} serial={}",
        ++g_direct_renderer.diagnostics.logged_observed_declaration_identity_hits,
        declaration, declaration_record.elements_address,
        declaration_record.elements_size, declaration_record.element_count,
        declaration_record.elements_hash,
        static_cast<unsigned long long>(record_it->second.serial));
  }
  return PlumeGuestDeclarationReady(tracked);
}

bool EnsureDirectKnownVertexDeclarationResourceLocked(uint32_t declaration) {
  if (declaration == 0 || !g_direct_renderer.device) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(declaration);
      existing && PlumeGuestDeclarationReady(*existing)) {
    return true;
  }
  if (EnsureDirectVertexDeclarationResourceFromObservedIdentityLocked(
          declaration)) {
    return true;
  }
  if (EnsureDirectVertexDeclarationResourceFromRegistryRecordLocked(
          declaration)) {
    return true;
  }
  return false;
}

bool EnsureDirectKnownShaderResourceLocked(uint32_t shader_address) {
  if (shader_address == 0 || !g_direct_renderer.device) {
    return false;
  }
  if (PlumeTrackedResource* existing = FindDirectResourceLocked(shader_address);
      existing && PlumeGuestShaderReady(*existing)) {
    return true;
  }
  if (EnsureDirectShaderResourceFromObservedIdentityLocked(shader_address)) {
    return true;
  }
  if (EnsureDirectShaderResourceFromRegistryRecordLocked(shader_address)) {
    return true;
  }
  return false;
}


bool DirectTextureIdentityStructureMatches(
    const FableTextureIdentity& lhs, const FableTextureIdentity& rhs) {
  return lhs.object_texture == rhs.object_texture &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.base_address == rhs.base_address &&
         lhs.byte_size == rhs.byte_size && lhs.width == rhs.width &&
         lhs.height == rhs.height && lhs.format == rhs.format &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool DirectTextureIdentityCacheCoversRegistration(
    const FableTextureIdentity& cached, const FableTextureIdentity& identity) {
  if (!DirectTextureIdentityStructureMatches(cached, identity)) {
    return false;
  }
  if (cached.meaningful_upload || identity.meaningful_upload) {
    return cached.meaningful_upload == identity.meaningful_upload &&
           cached.upload_generation == identity.upload_generation;
  }
  return true;
}

bool SyncTrackedResourceLocked(plume::RenderDevice* device,
                               const FableGuestResourceSnapshot& snapshot,
                               DirectResourceSyncBreakdown* sync_timing) {
  if (snapshot.guest_address == 0) {
    return false;
  }
  if (const char* quarantine_reason =
          DirectImageSnapshotQuarantineReason(snapshot)) {
    LogDirectImageSnapshotQuarantineLocked(snapshot, quarantine_reason);
    return false;
  }

  const FableDirectClock::time_point lookup_t0 = FableDirectClock::now();
  PlumeTrackedResource& tracked =
      g_direct_renderer.resources[snapshot.guest_address];
  tracked.last_referenced_frame = g_direct_renderer.current_frame_index;
  const FableDirectClock::time_point resource_t0 = FableDirectClock::now();
  if (sync_timing) {
    sync_timing->resource_lookup_ms +=
        FableDirectElapsedMs(lookup_t0, resource_t0);
    sync_timing->resources_size =
        static_cast<uint32_t>(g_direct_renderer.resources.size());
  }
  bool result = false;
  switch (snapshot.kind) {
    case FableGuestResourceKind::kVertexBuffer:
    case FableGuestResourceKind::kIndexBuffer:
      result = SyncBufferResourceLocked(device, snapshot, tracked, sync_timing);
      break;
    case FableGuestResourceKind::kTexture:
    case FableGuestResourceKind::kSurface:
    case FableGuestResourceKind::kDepthSurface:
      result = SyncImageResourceLocked(device, snapshot, tracked, sync_timing);
      break;
    case FableGuestResourceKind::kVertexDeclaration:
      result = SyncDeclarationResourceLocked(snapshot, tracked, sync_timing);
      break;
    case FableGuestResourceKind::kVertexShader:
    case FableGuestResourceKind::kPixelShader:
      result = SyncShaderResourceLocked(snapshot, tracked, sync_timing);
      break;
    case FableGuestResourceKind::kUnknown:
      break;
  }
  const FableDirectClock::time_point resource_t1 = FableDirectClock::now();
  if (sync_timing) {
    const size_t kind_index = static_cast<size_t>(snapshot.kind);
    if (kind_index < sync_timing->kind_ms.size()) {
      sync_timing->kind_ms[kind_index] +=
          FableDirectElapsedMs(resource_t0, resource_t1);
      ++sync_timing->kind_calls[kind_index];
    }
  }
  if (result && IsImageResource(snapshot.kind)) {
    const FableDirectClock::time_point identity_t0 = FableDirectClock::now();
    bool registered_texture_identity =
        RegisterDirectTextureIdentityFromResourceLocked(
            tracked, PlumeGuestTextureFetchDwords(tracked));
    // A GTL-content-fed record can carry bytes without ever having had an
    // observer fetch capture; with no fetch dwords the registration above is a
    // guaranteed skip and the record stays invisible to every ObjectsByBase
    // consumer (the exact-title resolvers), which fabricates alias records
    // for its base every frame. The header-init roots stated the fetch to the
    // GTL — adopt it. The GTL's base_address field is the header's verbatim
    // guest-virtual view and must not be registered raw (view mixing); the
    // decoded physical base falls out of the normalized address dword, the
    // same decode the command fetch comparison uses.
    if (!registered_texture_identity &&
        tracked.texture_upload_gtl_content &&
        !HasTrackedFetchDwords(PlumeGuestTextureFetchDwords(tracked))) {
      const refii::gtl::ResourceIdentity gtl_identity =
          refii::gtl::QueryResourceIdentity(tracked.guest_address);
      if (gtl_identity.known && gtl_identity.fetch_dwords_stated) {
        const std::array<uint32_t, 6> stated_fetch =
            FableNormalizeTextureFetchDwords(gtl_identity.fetch_dwords);
        tracked.texture_fetch_dwords = stated_fetch;
        if (tracked.texture_guest_base == 0) {
          tracked.texture_guest_base =
              FableDecodeTextureFetchAddressDword(stated_fetch[1]);
        }
        RefreshPlumeGuestImageObjectsFromTrackedFields(tracked);
        registered_texture_identity =
            RegisterDirectTextureIdentityFromResourceLocked(tracked,
                                                            stated_fetch);
      }
    }
    if (registered_texture_identity && tracked.texture_upload_gtl_content &&
        FableTrackedResourceHasMeaningfulTextureUpload(tracked)) {
      static uint32_t logged_gtl_fed_identity_registrations = 0;
      if (logged_gtl_fed_identity_registrations < 32u) {
        REXLOG_INFO(
            "Refii GTL-fed identity registered #{} guest=0x{:08X} "
            "base=0x{:08X} {}x{} fmt={} gen={}",
            ++logged_gtl_fed_identity_registrations, tracked.guest_address,
            PlumeGuestTextureBaseAddress(tracked),
            PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
            PlumeGuestTextureGuestFormat(tracked),
            static_cast<unsigned long long>(tracked.upload_generation));
      }
    }
    const FableDirectClock::time_point identity_t1 = FableDirectClock::now();
    const double texture_identity_ms =
        FableDirectElapsedMs(identity_t0, identity_t1);
    if (sync_timing) {
      sync_timing->texture_identity_ms += texture_identity_ms;
      ++sync_timing->texture_identity_calls;
      if (!registered_texture_identity) {
        ++sync_timing->texture_identity_skips;
      }
    }
  }
  if (result) {
    const FableDirectClock::time_point identity_t0 = FableDirectClock::now();
    RegisterDirectShaderAndDeclarationIdentityFromSnapshotLocked(snapshot);
    const FableDirectClock::time_point identity_t1 = FableDirectClock::now();
    if (sync_timing) {
      sync_timing->shader_decl_identity_ms +=
          FableDirectElapsedMs(identity_t0, identity_t1);
      ++sync_timing->shader_decl_identity_calls;
    }
  }
  // The operator[] above may have inserted the record, and the sync (or the
  // GTL fetch adoption) may have rewritten its match-bearing fields; drop
  // every memoized lookup answer the record can now satisfy — including the
  // failed-sync case, which still leaves an entry under snapshot.guest_address
  // that the map-hit path will find.
  InvalidateDirectResourceMemoKeyLocked(snapshot.guest_address);
  InvalidateDirectResourceMemosForResourceLocked(tracked);
  return result;
}

void LogDirectResourceSyncFailureLocked(
    const FableGuestResourceSnapshot& snapshot, const char* context) {
  if (g_direct_renderer.diagnostics.logged_resource_sync_failures >= 64u) {
    return;
  }

  ++g_direct_renderer.diagnostics.logged_resource_sync_failures;
  switch (snapshot.kind) {
    case FableGuestResourceKind::kTexture:
      REXLOG_WARN(
          "Refii plume direct resource sync failed #{} context={} kind={} "
          "guest=0x{:08X} parent=0x{:08X} obj=0x{:08X} base=0x{:08X} "
          "bytes={} upload_bytes={} gen={} hash=0x{:016X} fetch0={:08X}",
          g_direct_renderer.diagnostics.logged_resource_sync_failures, context,
          TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
          snapshot.parent_address, snapshot.texture.object_address,
          snapshot.texture.base_address, snapshot.texture.byte_size,
          snapshot.upload_data.size(),
          static_cast<unsigned long long>(snapshot.texture.upload_generation),
          static_cast<unsigned long long>(snapshot.texture.upload_hash),
          snapshot.texture.normalized_fetch_dwords[0]);
      break;
    case FableGuestResourceKind::kSurface:
    case FableGuestResourceKind::kDepthSurface:
      REXLOG_WARN(
          "Refii plume direct resource sync failed #{} context={} kind={} "
          "guest=0x{:08X} parent=0x{:08X} obj=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} extent=0x{:08X} fmt={} pitch=0x{:08X} bytes={}",
          g_direct_renderer.diagnostics.logged_resource_sync_failures, context,
          TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
          snapshot.parent_address, snapshot.surface.object_address,
          snapshot.surface.render_target_info,
          snapshot.surface.depth_stencil_info, snapshot.surface.packed_extent,
          snapshot.surface.d3d_format, snapshot.surface.pitch_msaa,
          snapshot.surface.byte_size);
      break;
    case FableGuestResourceKind::kVertexBuffer:
    case FableGuestResourceKind::kIndexBuffer:
      REXLOG_WARN(
          "Refii plume direct resource sync failed #{} context={} kind={} "
          "guest=0x{:08X} parent=0x{:08X} obj=0x{:08X} gpu=0x{:08X} "
          "bytes={} stride_fmt={} hash=0x{:016X} upload_bytes={}",
          g_direct_renderer.diagnostics.logged_resource_sync_failures, context,
          TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
          snapshot.parent_address, snapshot.buffer.object_address,
          snapshot.buffer.gpu_address, snapshot.buffer.byte_size,
          snapshot.buffer.stride_or_index_format,
          static_cast<unsigned long long>(snapshot.buffer.upload_hash),
          snapshot.upload_data.size());
      break;
    case FableGuestResourceKind::kVertexDeclaration:
      REXLOG_WARN(
          "Refii plume direct resource sync failed #{} context={} kind={} "
          "guest=0x{:08X} obj=0x{:08X} elements=0x{:08X} bytes={} "
          "count={} hash=0x{:016X} upload_bytes={}",
          g_direct_renderer.diagnostics.logged_resource_sync_failures, context,
          TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
          snapshot.vertex_declaration.object_address,
          snapshot.vertex_declaration.elements_address,
          snapshot.vertex_declaration.elements_size,
          snapshot.vertex_declaration.element_count,
          static_cast<unsigned long long>(
              snapshot.vertex_declaration.elements_hash),
          snapshot.upload_data.size());
      break;
    case FableGuestResourceKind::kVertexShader:
    case FableGuestResourceKind::kPixelShader:
      REXLOG_WARN(
          "Refii plume direct resource sync failed #{} context={} kind={} "
          "guest=0x{:08X} obj=0x{:08X} bytecode=0x{:08X} bytes={} "
          "offset={} hash=0x{:016X}",
          g_direct_renderer.diagnostics.logged_resource_sync_failures, context,
          TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
          snapshot.shader.object_address, snapshot.shader.bytecode_address,
          snapshot.shader.bytecode_size, snapshot.shader.bytecode_offset,
          static_cast<unsigned long long>(snapshot.shader.bytecode_hash));
      break;
    case FableGuestResourceKind::kUnknown:
      REXLOG_WARN(
          "Refii plume direct resource sync failed #{} context={} kind={} "
          "guest=0x{:08X} parent=0x{:08X} upload_bytes={}",
          g_direct_renderer.diagnostics.logged_resource_sync_failures, context,
          TrackedResourceKindName(snapshot.kind), snapshot.guest_address,
          snapshot.parent_address, snapshot.upload_data.size());
      break;
  }
}

bool DirectResourceMatchesAddress(const PlumeTrackedResource& resource,
                                  uint32_t address) {
  if (address == 0) {
    return false;
  }
  return resource.guest_address == address ||
         resource.parent_address == address ||
         PlumeGuestResourceDataAddress(resource) == address ||
         PlumeGuestBufferGpuAddress(resource) == address ||
         PlumeGuestTextureBaseAddress(resource) == address;
}

bool RecordPendingTextureUploadLocked(plume::RenderCommandList* command_list,
                                      PlumeTrackedResource& tracked) {
  if (!command_list || !tracked.texture_upload_pending ||
      !tracked.guest_texture.texture || !tracked.guest_texture.upload_buffer) {
    return false;
  }
  if (!IsUsablePlumeTextureFormat(PlumeGuestTextureFormat(tracked)) ||
      PlumeGuestTextureWidth(tracked) == 0 ||
      PlumeGuestTextureHeight(tracked) == 0) {
    return false;
  }
  // GPU-authoritative content (§3.2): when our GPU is the registered drawer
  // at this texture's base, the guest's CPU bytes for the same base are a
  // round-trip of a resolve our replacement GPU never wrote back — applying
  // them overwrites the real composed page with garbage. On console the CPU
  // tile step's source IS the GPU resolve, so keeping the GPU texels matches
  // hardware semantics.
  if (tracked.draw_content_serial != 0 &&
      tracked.upload_generation <= tracked.upload_generation_at_gpu_write) {
    const uint32_t drawn_base = PlumeGuestTextureBaseAddress(tracked);
    const auto drawn_it =
        drawn_base != 0
            ? g_direct_renderer.gpu_drawn_textures_by_base.find(drawn_base)
            : g_direct_renderer.gpu_drawn_textures_by_base.end();
    if (drawn_it != g_direct_renderer.gpu_drawn_textures_by_base.end() &&
        drawn_it->second == tracked.guest_address) {
      static uint32_t logged_gpu_authoritative_upload_drops = 0;
      if (logged_gpu_authoritative_upload_drops < 32u) {
        REXLOG_INFO(
            "Refii plume gpu-authoritative upload drop #{} frame={} "
            "guest=0x{:08X} base=0x{:08X} {}x{} draw_serial={} gen={} "
            "gpu_write_gen={}",
            ++logged_gpu_authoritative_upload_drops,
            g_direct_renderer.current_frame_index, tracked.guest_address,
            drawn_base, PlumeGuestTextureWidth(tracked),
            PlumeGuestTextureHeight(tracked),
            static_cast<unsigned long long>(tracked.draw_content_serial),
            static_cast<unsigned long long>(tracked.upload_generation),
            static_cast<unsigned long long>(
                tracked.upload_generation_at_gpu_write));
      }
      tracked.texture_upload_pending = false;
      return false;
    }
  }

  const plume::RenderTextureLayout target_layout =
      tracked.texture_depth_target
          ? plume::RenderTextureLayout::DEPTH_WRITE
          : (tracked.texture_render_target
                 ? plume::RenderTextureLayout::COLOR_WRITE
                 : plume::RenderTextureLayout::SHADER_READ);
  RecordDirectTextureBarrierForcedLocked(
      command_list, plume::RenderBarrierStage::COPY,
      tracked.guest_texture.texture.get(), plume::RenderTextureLayout::COPY_DEST);
  command_list->copyTextureRegion(
      plume::RenderTextureCopyLocation::Subresource(
          tracked.guest_texture.texture.get(), 0),
      plume::RenderTextureCopyLocation::PlacedFootprint(
          tracked.guest_texture.upload_buffer, tracked.texture_format,
          PlumeGuestTextureWidth(tracked), PlumeGuestTextureHeight(tracked),
          PlumeGuestTextureDepth(tracked),
          std::max<uint32_t>(tracked.texture_upload_row_width,
                             PlumeGuestTextureWidth(tracked)),
          tracked.guest_texture.upload_offset));
  RecordDirectTextureBarrierForcedLocked(
      command_list,
      plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
      tracked.guest_texture.texture.get(), target_layout);
  tracked.texture_upload_pending = false;
  return true;
}

// One-shot: push the staged white texel into the null textures the first time a
// command list is available. See the staging note in CreateNullTextureLocked —
// before this, every unresolved texture slot sampled undefined device memory.
void RecordNullTextureContentInitLocked(plume::RenderCommandList* command_list) {
  if (!command_list || g_plume_resources.null_texture_content_initialized) {
    return;
  }
  // The cube null (set 2) is 6 array slices; the 2D and 3D nulls are single.
  constexpr uint32_t kNullTextureArraySlices[3] = {1u, 1u, 6u};
  bool initialized_any = false;
  for (uint32_t set = 0; set < std::size(g_plume_resources.null_textures);
       ++set) {
    plume::RenderBuffer* upload =
        g_plume_resources.null_texture_uploads[set].get();
    plume::RenderTexture* texture = g_plume_resources.null_textures[set].get();
    if (upload == nullptr || texture == nullptr) {
      continue;
    }
    RecordDirectTextureBarrierForcedLocked(
        command_list, plume::RenderBarrierStage::COPY, texture,
        plume::RenderTextureLayout::COPY_DEST);
    for (uint32_t slice = 0; slice < kNullTextureArraySlices[set]; ++slice) {
      command_list->copyTextureRegion(
          plume::RenderTextureCopyLocation::Subresource(texture, 0, slice),
          plume::RenderTextureCopyLocation::PlacedFootprint(
              upload, plume::RenderFormat::R8G8B8A8_UNORM, 1, 1, 1, 1, 0));
    }
    RecordDirectTextureBarrierForcedLocked(
        command_list,
        plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
        texture, plume::RenderTextureLayout::SHADER_READ);
    initialized_any = true;
  }
  if (initialized_any) {
    g_plume_resources.null_texture_content_initialized = true;
    REXLOG_INFO(
        "Refii plume null textures initialized to opaque white (missing "
        "textures previously sampled undefined memory)");
  }
}

void RecordPendingTextureUploadsLocked(
    plume::RenderCommandList* command_list,
    const std::unordered_set<uint32_t>* texture_upload_filter) {
  if (!command_list) {
    return;
  }
  RecordNullTextureContentInitLocked(command_list);

  for (auto& [address, tracked] : g_direct_renderer.resources) {
    if (texture_upload_filter) {
      bool matched = texture_upload_filter->contains(address);
      if (!matched) {
        for (uint32_t filter_address : *texture_upload_filter) {
          if (DirectResourceMatchesAddress(tracked, filter_address)) {
            matched = true;
            break;
          }
        }
      }
      if (!matched && tracked.texture_upload_pending &&
          tracked.guest_texture.texture && tracked.guest_texture.upload_buffer &&
          IsUsablePlumeTextureFormat(PlumeGuestTextureFormat(tracked)) &&
          IsFableTitleBillboardTileUploadObjectAddress(
              tracked.guest_address)) {
        matched = true;
      }
      if (!matched) {
        continue;
      }
    }
    RecordPendingTextureUploadLocked(command_list, tracked);
  }
}

// Copies queued surface pixels into the recreated texture at the same guest
// address (see PendingSurfaceContentCarry). Raw texel copy is the honest
// Xbox semantic: a format re-describe reinterprets the same physical bits.
void RecordPendingSurfaceContentCarriesLocked(
    plume::RenderCommandList* command_list) {
  if (!command_list ||
      g_direct_renderer.pending_surface_content_carries.empty()) {
    return;
  }
  auto& carries = g_direct_renderer.pending_surface_content_carries;
  for (auto it = carries.begin(); it != carries.end();) {
    PendingSurfaceContentCarry& carry = *it;
    PlumeTrackedResource* replacement =
        FindDirectResourceLocked(carry.guest_address);
    plume::RenderTexture* new_texture =
        replacement != nullptr ? replacement->guest_texture.texture.get()
                               : nullptr;
    // A CPU upload on the replacement is newer guest intent than the stashed
    // RT pixels (orphan-adoption re-fills recreated textures) — upload wins.
    const bool replacement_ready =
        new_texture != nullptr && new_texture != carry.old_texture.get() &&
        PlumeGuestTextureWidth(*replacement) == carry.width &&
        PlumeGuestTextureHeight(*replacement) == carry.height &&
        PlumeGuestTextureFormat(*replacement) == carry.format &&
        replacement->guest_texture.upload_generation == 0 &&
        !replacement->texture_upload_pending;
    if (!replacement_ready) {
      const bool expired =
          g_direct_renderer.current_frame_index >= carry.queued_frame &&
          g_direct_renderer.current_frame_index - carry.queued_frame > 240u;
      // The replacement moved on (different shape, or it took a CPU upload
      // instead) — the carry is stale, drop it.
      const bool replaced_with_other_shape =
          replacement != nullptr && new_texture != nullptr &&
          new_texture != carry.old_texture.get();
      if (expired || replaced_with_other_shape) {
        PlumeTrackedResource retired;
        ForgetDirectTextureLayoutLocked(carry.old_texture.get());
        retired.guest_texture.texture = std::move(carry.old_texture);
        RetirePlumeResourceLocked(std::move(retired),
                                  g_direct_renderer.resource_serial);
        it = carries.erase(it);
      } else {
        ++it;
      }
      continue;
    }
    RecordDirectTextureBarrierForcedLocked(
        command_list, plume::RenderBarrierStage::COPY,
        carry.old_texture.get(), plume::RenderTextureLayout::COPY_SOURCE);
    RecordDirectTextureBarrierForcedLocked(
        command_list, plume::RenderBarrierStage::COPY, new_texture,
        plume::RenderTextureLayout::COPY_DEST);
    command_list->copyTextureRegion(
        plume::RenderTextureCopyLocation::Subresource(new_texture, 0),
        plume::RenderTextureCopyLocation::Subresource(
            carry.old_texture.get(), 0));
    const plume::RenderTextureLayout target_layout =
        replacement->texture_render_target
            ? plume::RenderTextureLayout::COLOR_WRITE
            : plume::RenderTextureLayout::SHADER_READ;
    RecordDirectTextureBarrierForcedLocked(
        command_list,
        plume::RenderBarrierStage::GRAPHICS | plume::RenderBarrierStage::COPY,
        new_texture, target_layout);
    replacement->content_serial =
        std::max(replacement->content_serial, carry.content_serial);
    replacement->guest_texture.content_serial =
        std::max(replacement->guest_texture.content_serial,
                 carry.content_serial);
    if (g_direct_renderer.diagnostics.logged_surface_content_carries < 64u) {
      REXLOG_INFO(
          "Refii plume surface content carry #{} frame={} guest=0x{:08X} "
          "{}x{} queued_frame={} content_serial={}",
          ++g_direct_renderer.diagnostics.logged_surface_content_carries,
          g_direct_renderer.current_frame_index, carry.guest_address,
          carry.width, carry.height, carry.queued_frame,
          static_cast<unsigned long long>(carry.content_serial));
    }
    PlumeTrackedResource retired;
    ForgetDirectTextureLayoutLocked(carry.old_texture.get());
    retired.guest_texture.texture = std::move(carry.old_texture);
    RetirePlumeResourceLocked(std::move(retired),
                              g_direct_renderer.resource_serial);
    it = carries.erase(it);
  }
}

// Per-frame census of the alias fallback. The direct map hit is O(1); the
// fallback below is a LINEAR SCAN of every tracked resource, and a genuine miss
// scans to completion. Whether that matters depends on how often the fallback
// is entered and how many entries it touches -- both unknown until measured, so
// measure rather than guess. Reset each frame in BeginFableDirectFrame.
DirectResourceLookupCensus g_direct_resource_lookup_census;

// MEASURED 2026-07-26 (run 296): the "one resource per address" assumption is
// FALSE. Address 0x4260851C is matched by SIX tracked resources, on ~22 of every
// 64 sampled scans. The scan returns the first match in unordered_map iteration
// order, so Refii is ALREADY choosing arbitrarily among those six today -- that
// is a pre-existing defect this census exposed, not one the memo introduced.
//
// The memo must therefore never pin an ambiguous address. The rule is:
//
//   * an address with exactly ONE match is memoized. Uniqueness means there is
//     no ordering question to get wrong, so the memo returns the only answer a
//     rescan could return.
//   * an address with MORE THAN ONE match is never memoized. Those lookups keep
//     doing exactly what they did before -- scan, return first match -- so
//     behaviour for the ambiguous case is byte-identical to the old code.
//
// Establishing uniqueness costs one full scan per new address, which is why
// population always runs to completion instead of stopping at the first hit.
// Ambiguous addresses are remembered until a covering resource mutates, so
// their lookups can stop early again, exactly as before.

// Alias memo: alias address -> guest_address of the resource that matched it.
//
// Measured (run 295, 59 draws): 4513 fallback scans per frame walking 345,473
// resource rows, of which 4282 (95%) SUCCEED. They are alias hits, not misses,
// so almost all of that scanning is re-deriving an answer we already found.
//
// Correctness does not depend on this memo being right:
//   - a hit is re-verified with DirectResourceMatchesAddress before it is
//     returned, so a false positive is impossible;
//   - a stale or missing entry simply falls through to the authoritative scan,
//     which then repopulates it;
//   - entries store a guest_address, not a pointer, so a destroyed resource
//     fails the map lookup and falls through rather than dangling.
//
// PERSISTENT ACROSS FRAMES with per-key invalidation. Run 453 measured ~63k
// lookups and ~560k scanned entries per frame at the title menu because the
// memos restarted from empty every frame (and the negative memo additionally
// died on ANY map insert): every unique alias address paid a full uniqueness
// scan per frame and every ring-cursor miss re-proved absence per bind. A
// memoized answer can only change when a resource covering that address is
// created, refreshed, re-pointed or destroyed, so those sites — and only
// those sites — erase the covered keys via
// InvalidateDirectResourceMemosForResourceLocked. The "newer resource should
// win" hazard of pinning a choice across frames is closed by the same rule: a
// resource GAINING a match address passes through one of the hooked mutation
// funnels, which drops the memoized key and forces a fresh uniqueness scan.
std::unordered_map<uint32_t, uint32_t> g_direct_resource_alias_memo;
// Addresses proven to have >1 match. Never memoized; their lookups reproduce
// the original scan-and-take-first behaviour exactly. Per-key invalidation
// applies here too, so an address whose duplicate claimants die can be
// re-proven unique instead of early-out scanning forever.
std::unordered_set<uint32_t> g_direct_resource_alias_ambiguous;

// NEGATIVE memo: addresses a full scan proved ABSENT. Run 410 measured 192 of
// 283 scans/frame re-proving the same misses against a map that grew to ~3000
// entries (entries_scanned 143k -> 819k/frame within one title run) — the
// miss is the expensive case and it repeats every frame for the same
// addresses (the BeginVertices ring cursors, see BindDirectVertexInputLocked).
// An absence can only become wrong when a resource MATCHING that address
// appears: an insert or a match-field rewrite, both of which pass through the
// hooked mutation funnels and erase the exact keys the new record covers.
// Erases never invalidate a negative (removing a resource cannot make an
// absent address present), so the memo survives destruction and frame
// boundaries untouched.
std::unordered_set<uint32_t> g_direct_resource_negative_memo;

// Cumulative direct-find census (never reset; logged every 64k calls from
// FindDirectResourceLocked). The per-frame reslookup[] census above shows one
// frame; this one proves the steady state across a run: pos_hits+neg_hits
// should converge to ~100% of calls with scans and scan_entries flat, while a
// climbing scans/scan_entries means the per-key invalidation is thrashing.
DirectFindCumulativeCensus g_direct_find_cumulative_census;

void InvalidateDirectResourceMemoKeyLocked(uint32_t address) {
  if (address == 0) {
    return;
  }
  const size_t erased = g_direct_resource_alias_memo.erase(address) +
                        g_direct_resource_alias_ambiguous.erase(address) +
                        g_direct_resource_negative_memo.erase(address);
  if (erased != 0) {
    ++g_direct_find_cumulative_census.key_invalidations;
  }
}

// Erases every memo key the resource can satisfy, mirroring
// DirectResourceMatchesAddress. Called wherever a tracked resource is
// inserted, has its match-bearing fields rewritten, or is retired.
void InvalidateDirectResourceMemosForResourceLocked(
    const PlumeTrackedResource& resource) {
  InvalidateDirectResourceMemoKeyLocked(resource.guest_address);
  InvalidateDirectResourceMemoKeyLocked(resource.parent_address);
  InvalidateDirectResourceMemoKeyLocked(
      PlumeGuestResourceDataAddress(resource));
  InvalidateDirectResourceMemoKeyLocked(PlumeGuestBufferGpuAddress(resource));
  InvalidateDirectResourceMemoKeyLocked(
      PlumeGuestTextureBaseAddress(resource));
}

void ClearDirectResourceMemosLocked() {
  g_direct_resource_alias_memo.clear();
  g_direct_resource_alias_ambiguous.clear();
  g_direct_resource_negative_memo.clear();
}

// Frame-level attribution of record_ms, summed across every recorded draw.
// The draw record path already had phase anchors (video_t1 / input_t1 /
// alias_t1) but they were only consumed under RendererDeepObserverHooksEnabled
// (default OFF) and behind a >=4ms per-draw threshold that no longer trips.
// These accumulators are always on -- six clock reads per draw is nothing
// against a 0.8ms draw -- so the next run says where the time went instead of
// us inferring it. Reset each frame in BeginFableDirectFrame.
DirectDrawPhaseAccumulator g_direct_draw_phases;

// Out-of-line accumulators for RecordGtlResolveDrawLocked, which is defined
// above this struct.
void AccumulateGtlResolveFbMs(double elapsed_ms) {
  g_direct_draw_phases.resolve_fb_ms += elapsed_ms;
}

void AccumulateGtlResolveDrawMs(double elapsed_ms) {
  g_direct_draw_phases.resolve_draw_ms += elapsed_ms;
}

void AccumulateDestScanCensus(uint64_t scanned, uint64_t relevant) {
  g_direct_draw_phases.alias_destination_scan_scanned += scanned;
  g_direct_draw_phases.alias_destination_scan_relevant += relevant;
}

// Direct-mapped front cache for the resource map.
//
// Measured 2026-07-28: ~63,900 lookups per compose for 189 draws = ~338 PER
// DRAW, against only ~16 distinct addresses. 20 call sites sit in the per-draw
// record path and two of them loop over 16 texture slots, so the SAME handful
// of addresses is re-looked-up ~20 times per draw. 93% are plain map hits
// (maphit=59693/63867).
//
// Cutting lookup COUNT is the right lever here specifically because this ships
// as a Debug build: no inlining and debug STL iterators make each
// unordered_map::find far more expensive than in an optimised build, so
// operation count dominates. This also helps in an optimised build; it is not a
// debug-only workaround.
//
// SAFETY: std::unordered_map guarantees pointers/references to elements remain
// valid across insertion and rehash — only ERASING an element invalidates a
// pointer to it. The map has exactly three such sites (one erase, two clears),
// each of which bumps the generation below. Only the plain map-hit path is
// cached; the memoized-alias path is left completely untouched because it
// re-verifies before use and caching it would change behaviour.
DirectResourceFastCacheEntry
    g_direct_resource_fast_cache[kDirectResourceFastCacheSize];
uint64_t g_direct_resource_map_generation = 0;
uint64_t g_direct_resource_fast_cache_generation = 0;

void InvalidateDirectResourceFastCacheLocked() {
  ++g_direct_resource_map_generation;
}

PlumeTrackedResource* FindDirectResourceLocked(uint32_t address) {
  if (address == 0) {
    return nullptr;
  }
  ++g_direct_resource_lookup_census.calls;
  ++g_direct_find_cumulative_census.calls;
  if ((g_direct_find_cumulative_census.calls % 65536u) == 0u) {
    REXLOG_INFO(
        "Refii direct-find census calls={} pos_hits={} neg_hits={} scans={} "
        "scan_entries={} key_invalidations={} memo={} neg={} ambig={} "
        "resources={}",
        g_direct_find_cumulative_census.calls,
        g_direct_find_cumulative_census.pos_hits,
        g_direct_find_cumulative_census.neg_hits,
        g_direct_find_cumulative_census.scans,
        g_direct_find_cumulative_census.scan_entries,
        g_direct_find_cumulative_census.key_invalidations,
        static_cast<uint32_t>(g_direct_resource_alias_memo.size()),
        static_cast<uint32_t>(g_direct_resource_negative_memo.size()),
        static_cast<uint32_t>(g_direct_resource_alias_ambiguous.size()),
        static_cast<uint32_t>(g_direct_renderer.resources.size()));
  }
  if (g_direct_resource_fast_cache_generation !=
      g_direct_resource_map_generation) {
    for (DirectResourceFastCacheEntry& entry : g_direct_resource_fast_cache) {
      entry.address = 0;
      entry.resource = nullptr;
    }
    g_direct_resource_fast_cache_generation = g_direct_resource_map_generation;
  }
  // Guest addresses are heavily aligned, so drop the dead low bits before
  // indexing or every entry collides on one slot.
  DirectResourceFastCacheEntry& cache_entry =
      g_direct_resource_fast_cache[(address >> 4u) &
                                   (kDirectResourceFastCacheSize - 1u)];
  if (cache_entry.address == address && cache_entry.resource != nullptr) {
    ++g_direct_resource_lookup_census.fast_hits;
    ++g_direct_find_cumulative_census.pos_hits;
    cache_entry.resource->last_referenced_frame =
        g_direct_renderer.current_frame_index;
    return cache_entry.resource;
  }
  auto it = g_direct_renderer.resources.find(address);
  if (it != g_direct_renderer.resources.end()) {
    ++g_direct_resource_lookup_census.map_hits;
    ++g_direct_find_cumulative_census.pos_hits;
    cache_entry.address = address;
    cache_entry.resource = &it->second;
    it->second.last_referenced_frame = g_direct_renderer.current_frame_index;
    return &it->second;
  }
  // Memoized alias, re-verified before use (see kMaxDirectResourceAliasMemo).
  const auto memo_it = g_direct_resource_alias_memo.find(address);
  if (memo_it != g_direct_resource_alias_memo.end()) {
    const auto resource_it =
        g_direct_renderer.resources.find(memo_it->second);
    if (resource_it != g_direct_renderer.resources.end() &&
        DirectResourceMatchesAddress(resource_it->second, address)) {
      ++g_direct_resource_lookup_census.memo_hits;
      ++g_direct_find_cumulative_census.pos_hits;
      resource_it->second.last_referenced_frame =
          g_direct_renderer.current_frame_index;
      return &resource_it->second;
    }
    // Resource gone, or no longer carries this alias -- drop and fall through
    // to the authoritative scan.
    g_direct_resource_alias_memo.erase(memo_it);
  }

  // Negative memo: an entry here is a standing absence proof. Every mutation
  // that could make this address present erases the key (see
  // InvalidateDirectResourceMemosForResourceLocked), so presence in the set is
  // sufficient — no generation or map-size check.
  if (g_direct_resource_negative_memo.count(address) != 0) {
    ++g_direct_resource_lookup_census.negative_hits;
    ++g_direct_find_cumulative_census.neg_hits;
    return nullptr;
  }

  ++g_direct_resource_lookup_census.scans;
  ++g_direct_find_cumulative_census.scans;
  // Known-ambiguous addresses reproduce the original behaviour exactly: stop at
  // the first match, memoize nothing. Everything else runs to completion so we
  // can PROVE the match is unique before memoizing it.
  const bool known_ambiguous =
      g_direct_resource_alias_ambiguous.count(address) != 0;
  PlumeTrackedResource* first_match = nullptr;
  uint32_t first_match_key = 0;
  uint32_t match_count = 0;
  uint32_t match_keys[4] = {};
  for (auto& [guest_address, resource] : g_direct_renderer.resources) {
    ++g_direct_resource_lookup_census.entries_scanned;
    ++g_direct_find_cumulative_census.scan_entries;
    if (!DirectResourceMatchesAddress(resource, address)) {
      continue;
    }
    if (match_count < 4u) {
      match_keys[match_count] = guest_address;
    }
    ++match_count;
    if (first_match == nullptr) {
      first_match = &resource;
      first_match_key = guest_address;
      if (known_ambiguous) {
        break;
      }
    }
  }
  if (!known_ambiguous) {
    ++g_direct_resource_lookup_census.ambiguity_samples;
    if (match_count > 1u) {
      ++g_direct_resource_lookup_census.ambiguous_scans;
      g_direct_resource_lookup_census.last_ambiguous_address = address;
      g_direct_resource_alias_ambiguous.insert(address);
      // Several tracked resources claim one address, so "first match in hash
      // order" is a real choice Refii is making arbitrarily -- independent of
      // the memo. Log WHICH resources collide: that distinguishes a legitimate
      // parent/child fan-out from stale records the retire path should have
      // dropped (the guest allocator cannot hand the same base to two LIVE
      // textures -- D3DDevice_CreateTexture @0x82B98B28 takes a fresh
      // allocation per texture -- so a base collision means one of them is
      // stale).
      static uint32_t logged_alias_ambiguity = 0;
      if (logged_alias_ambiguity < 24u) {
        REXLOG_WARN(
            "Refii plume ALIAS AMBIGUITY #{} frame={} address=0x{:08X} "
            "matches={} first=0x{:08X} others=[0x{:08X} 0x{:08X} 0x{:08X}] "
            "— not memoized, scan-and-take-first preserved",
            ++logged_alias_ambiguity, g_direct_renderer.current_frame_index,
            address, match_count, first_match_key, match_keys[1],
            match_keys[2], match_keys[3]);
      }
    }
  }
  if (first_match == nullptr) {
    // Record the proven absence. The cap bounds memory against cursor churn;
    // restarting on overflow keeps hot keys memoizable instead of freezing a
    // full set forever.
    if (g_direct_resource_negative_memo.size() >=
        kMaxDirectResourceNegativeMemo) {
      g_direct_resource_negative_memo.clear();
    }
    g_direct_resource_negative_memo.insert(address);
    // MISS CENSUS. ~95% of the remaining full-map scans end here, and a miss is
    // the expensive case (it walks every entry to prove absence). Knowing WHICH
    // addresses these are decides what to cut: if they are ring cursors the
    // observer's scratch registry has already evicted or overwritten, the fix is
    // to stop asking it; if they are texture identities, the surface-link
    // registry is the target. Bounded to a handful of distinct addresses so the
    // census cannot itself become a cost.
    static std::unordered_map<uint32_t, uint32_t> miss_counts;
    static uint32_t logged_miss_census = 0;
    if (miss_counts.size() < 64u || miss_counts.count(address) != 0) {
      const uint32_t hits = ++miss_counts[address];
      if (hits == 256u && logged_miss_census < 24u) {
        REXLOG_INFO(
            "Refii plume resource-lookup MISS census #{} frame={} "
            "address=0x{:08X} misses={} distinct_missing={} — full-map scan "
            "proving absence; names the registry to delete",
            ++logged_miss_census, g_direct_renderer.current_frame_index,
            address, hits, static_cast<uint32_t>(miss_counts.size()));
      }
    }
  }
  if (first_match != nullptr) {
    ++g_direct_resource_lookup_census.scan_hits;
    first_match->last_referenced_frame = g_direct_renderer.current_frame_index;
    // Memoize ONLY a proven-unique match. match_count is exact here because the
    // loop above ran to completion for every address not already known to be
    // ambiguous.
    if (match_count == 1u) {
      if (g_direct_resource_alias_memo.size() >= kMaxDirectResourceAliasMemo) {
        g_direct_resource_alias_memo.clear();
      }
      g_direct_resource_alias_memo.emplace(address, first_match_key);
    }
    return first_match;
  }
  return nullptr;
}

PlumeTrackedResource* FindDirectResourceByExactObjectLocked(uint32_t address) {
  if (address == 0) {
    return nullptr;
  }
  auto it = g_direct_renderer.resources.find(address);
  if (it != g_direct_renderer.resources.end()) {
    return &it->second;
  }
  for (auto& [guest_address, resource] : g_direct_renderer.resources) {
    (void)guest_address;
    if (resource.guest_address == address ||
        resource.parent_address == address) {
      return &resource;
    }
  }
  return nullptr;
}

bool DirectSurfaceAliasDestinationMatchesAddress(
    const RendererSurfaceCopyAliasSnapshot& alias, uint32_t address,
    const PlumeTrackedResource* sampled_resource) {
  if (alias.destination_surface == address) {
    return true;
  }
  return sampled_resource &&
         DirectResourceMatchesAddress(*sampled_resource,
                                      alias.destination_surface);
}

PlumeTrackedResource* FindDirectSurfaceAliasSourceLocked(uint32_t address) {
  if (address == 0) {
    return nullptr;
  }
  const PlumeTrackedResource* sampled_resource = FindDirectResourceLocked(address);
  const FableDirectPresentSnapshot present =
      GetFableDirectPresentSnapshot();
  for (uint32_t i = 0; i < present.surface_copy_alias_count; ++i) {
    const RendererSurfaceCopyAliasSnapshot& alias =
        present.surface_copy_aliases[i];
    if (alias.copy_call_serial == 0 && alias.copy_context_key == 0 &&
        alias.copy_symbol_address == 0) {
      continue;
    }
    if (!DirectSurfaceAliasDestinationMatchesAddress(alias, address,
                                                    sampled_resource)) {
      continue;
    }
    PlumeTrackedResource* source = FindDirectResourceLocked(alias.source_surface);
    if (source && PlumeGuestImageReady(*source)) {
      return source;
    }
  }
  return nullptr;
}

PlumeTrackedResource* FindDirectResolveAliasSourceLocked(uint32_t address) {
  const FableRenderTargetResolveAlias* alias =
      FindFableResolveAliasLocked(address);
  if (!alias) {
    return nullptr;
  }
  PlumeTrackedResource* source = FindDirectResourceLocked(alias->source_address);
  return source && PlumeGuestImageReady(*source) ? source : nullptr;
}


PlumeTrackedResource* ResolveDirectSampledTextureLocked(
    uint32_t address, bool allow_alias_lookup) {
  if (address == 0) {
    return nullptr;
  }

  PlumeTrackedResource* direct = FindDirectResourceLocked(address);
  if (direct && PlumeGuestImageReady(*direct) &&
      FableTrackedResourceHasMeaningfulTextureUpload(*direct) &&
      !FableTrackedResourceIsBlankUploadedSampleProxy(*direct)) {
    return direct;
  }

  if (allow_alias_lookup) {
    if (PlumeTrackedResource* resolve_source =
            FindDirectResolveAliasSourceLocked(address)) {
      return resolve_source;
    }
    if (PlumeTrackedResource* alias_source =
            FindDirectSurfaceAliasSourceLocked(address)) {
      return alias_source;
    }
  }
  return direct && PlumeGuestImageReady(*direct) ? direct : nullptr;
}

std::array<uint32_t, 6> DirectCommandTextureFetchDwords(
    const FableDirectRenderCommand& command, uint32_t slot) {
  return FableCommandTextureFetchDwords(command, slot);
}

bool RegisterDirectTextureIdentityFromResourceLocked(
    const PlumeTrackedResource& resource,
    const std::array<uint32_t, 6>& fetch_dwords) {
  if (!IsImageResource(resource.kind) || !PlumeGuestImageReady(resource) ||
      resource.guest_address == 0 || !HasTrackedFetchDwords(fetch_dwords)) {
    return false;
  }
  if (IsFableTinyTitleUploadedTextureAddress(resource.guest_address)) {
    const FableTextureIdentity* existing_identity =
        FableDirectFindTextureIdentityByObject(resource.guest_address);
    if (existing_identity && existing_identity->base_address != 0 &&
        existing_identity->upload_generation != 0 &&
        existing_identity->width <= 64u && existing_identity->height <= 64u &&
        PlumeGuestTextureBaseAddress(resource) == 0 &&
        !FableTrackedResourceHasMeaningfulTextureUpload(resource)) {
      return false;
    }
  }
  if (IsFableHeaderOnlyPrivateTextureProxyAddress(resource.guest_address) &&
      !FableTrackedResourceHasMeaningfulTextureUpload(resource) &&
      resource.content_serial == 0 && resource.draw_content_serial == 0) {
    return false;
  }
  // COLOUR RENDER TARGETS MAY NOW REGISTER (2026-08-03). This used to refuse
  // BOTH colour and depth targets whenever they carried no meaningful CPU
  // upload, which is a CONTENT test blocking a GEOMETRY registration — and the
  // content property is already carried per-field, independently, by
  // `identity.meaningful_upload` a few lines below (:14618, from
  // DirectResourceUploadDefinesTextureIdentity). Consumers that care about
  // content read that flag (:13475, :17803, registry:3333); nothing needed the
  // whole registration destroyed to learn it.
  //
  // Why it matters: a render target's content arrives from the GPU and never as
  // a CPU upload, so this returned false on EVERY attempt for every resolve
  // destination — which meant a colour RT could never be found through the
  // base->objects identity map. Gameplay samples exactly that class: the scene
  // colour slot (g_PostProcessResolveTexture, 12 fullscreen/composite/resolve
  // passes) and the 1x1 g_DefaultRTTexture the guest uses as its "unbound"
  // encoding are both colour render targets. It also froze the identity entry
  // immutable while the guest recycled the header underneath it, which is the
  // dominant, GROWING share of the GTL/registry divergence (7.4% -> 17.7% ->
  // 33.6% across one run's three census samples).
  //
  // Depth targets keep the refusal: the sampled-texture resolver's own usable()
  // predicate rejects `texture_depth_target` outright, so a depth identity
  // could not be consumed today. These two are a PAIR — revisit both together
  // when depth sampling (shadow maps, g_ResolvedDepthTexture) lands, not one
  // alone.
  if (resource.texture_depth_target &&
      !FableTrackedResourceHasMeaningfulTextureUpload(resource)) {
    return false;
  }

  FableTextureIdentity identity;
  identity.object_texture = resource.guest_address;
  identity.parent_texture = resource.parent_address;
  identity.base_address = PlumeGuestTextureBaseAddress(resource);
  identity.byte_size = static_cast<uint32_t>(
      std::min<uint64_t>(PlumeGuestResourceByteSize(resource), UINT32_MAX));
  identity.width = PlumeGuestTextureWidth(resource);
  identity.height = PlumeGuestTextureHeight(resource);
  identity.format = PlumeGuestTextureGuestFormat(resource);
  identity.upload_generation = resource.upload_generation;
  identity.meaningful_upload =
      DirectResourceUploadDefinesTextureIdentity(resource);
  identity.fetch_dwords = fetch_dwords;

  const bool may_register_alias_identity =
      (identity.base_address == kFableCanonicalTitleBillboardBase &&
       identity.width == 1024u && identity.height == 512u) ||
      identity.base_address == kFableTitleBillboardCommandSourceBase ||
      IsFableTitleBillboardTileUploadBase(identity.base_address, identity.width,
                                          identity.height) ||
      identity.base_address == kFableTitlePrivateIndirectionCommandBase ||
      identity.base_address == kFableTitlePrivateIndirectionHeader0Base ||
      identity.base_address == kFableTitlePrivateIndirectionHeader1Base ||
      identity.base_address == kFableTitlePrivateIndirectionSourceBase ||
      (identity.base_address == 0x1A0D9000u && identity.width == 512u &&
       identity.height == 256u) ||
      (identity.base_address == 0x1A0D7000u && identity.width == 16u &&
       identity.height == 16u) ||
      (identity.base_address == 0x1FAB5000u && identity.width == 64u &&
       identity.height == 1u) ||
      (identity.base_address == 0x1FAB4000u && identity.width == 256u &&
       identity.height == 1u) ||
      (identity.base_address == 0x1FAB3000u && identity.width == 64u &&
       identity.height == 1u) ||
      (identity.base_address == 0x1FAB2000u && identity.width == 256u &&
       identity.height == 1u) ||
      (identity.base_address == 0x1FAB1000u && identity.width == 64u &&
       identity.height == 1u) ||
      (identity.base_address == 0x1FAB0000u && identity.width == 256u &&
       identity.height == 1u) ||
      (identity.base_address == 0x13520000u && identity.width == 64u &&
       identity.height == 64u) ||
      (identity.base_address == 0x13530000u && identity.width == 128u &&
       identity.height == 128u) ||
      (identity.base_address == 0x13540000u && identity.width == 128u &&
       identity.height == 128u) ||
      (identity.base_address == 0x13550000u && identity.width == 128u &&
       identity.height == 128u) ||
      (identity.base_address == 0x13580000u &&
       ((identity.width == 128u && identity.height == 128u) ||
        (identity.width == 64u && identity.height == 64u))) ||
      (identity.base_address == 0x13590000u && identity.width == 128u &&
       identity.height == 128u);
  auto identity_registration_cached = [&](const FableTextureIdentity& candidate) {
    const auto cached_it =
        g_direct_renderer.registered_texture_identity_cache.find(
            candidate.object_texture);
    if (cached_it == g_direct_renderer.registered_texture_identity_cache.end()) {
      return false;
    }
    return DirectTextureIdentityCacheCoversRegistration(
        cached_it->second.identity, candidate);
  };

  bool registered_any_identity = false;
  const bool main_identity_cached = identity_registration_cached(identity);
  if (main_identity_cached && !may_register_alias_identity) {
    return false;
  }
  if (!main_identity_cached) {
    FableDirectRegisterTextureIdentity(identity);
    g_direct_renderer
        .registered_texture_identity_cache[identity.object_texture] =
        DirectRegisteredTextureIdentityCacheEntry{identity};
    registered_any_identity = true;
  }

  const auto register_alias_identity = [&](uint32_t alias_object) {
    if (alias_object == 0 || alias_object == resource.guest_address) {
      return;
    }
    if (const FableTextureIdentity* existing =
            FableDirectFindTextureIdentityByObject(alias_object)) {
      const bool same_exact_identity =
          existing->base_address == identity.base_address &&
          existing->width == identity.width &&
          existing->height == identity.height &&
          existing->format == identity.format &&
          FableDirectTextureFetchKey(existing->fetch_dwords) ==
              FableDirectTextureFetchKey(identity.fetch_dwords);
      if (!same_exact_identity &&
          (existing->base_address != 0 || existing->width != 0 ||
           existing->height != 0 ||
           FableDirectTextureFetchKey(existing->fetch_dwords) != 0)) {
        return;
      }
    }
    FableTextureIdentity alias_identity = identity;
    alias_identity.object_texture = alias_object;
    alias_identity.parent_texture = resource.guest_address;
    if (identity_registration_cached(alias_identity)) {
      return;
    }
    FableDirectRegisterTextureIdentity(alias_identity);
    g_direct_renderer.registered_texture_identity_cache[alias_object] =
        DirectRegisteredTextureIdentityCacheEntry{alias_identity};
    registered_any_identity = true;
  };

  const uint32_t base = identity.base_address;
  if (base == kFableCanonicalTitleBillboardBase &&
      identity.width == 1024u && identity.height == 512u) {
    // ADDRESS GATE (census: billboard_atlas_alias_registration). Here the
    // literals are the PAYLOAD, not the test — the branch key (base + extent)
    // is derived, but the five alias object pointers it publishes are one-run
    // captures. If the branch fires at gameplay it will publish identities for
    // objects that no longer exist, which is silent and unmeasured; the census
    // records how often it fires and against which base.
    DirectAddressGateTest(DirectAddressGate::kBillboardAtlasAliasRegistration,
                          true, base,
                          (static_cast<uint64_t>(identity.width) << 32) |
                              identity.height);
    register_alias_identity(0x4C132C70u);
    register_alias_identity(0x4C132CA4u);
    register_alias_identity(0x4C132CD8u);
    register_alias_identity(0x4C133150u);
    register_alias_identity(0x4C133184u);
  } else if (base == kFableTitleBillboardCommandSourceBase) {
    register_alias_identity(kFableTitleBillboardCommandSource);
    register_alias_identity(kFableTitleBillboardCommandSourceAlias);
  } else if (IsFableTitleBillboardTileUploadBase(base, identity.width,
                                                identity.height)) {
    const uint32_t base_object =
        FableObservedTitleBillboardTileObjectAddressForBaseLocked(
            base, identity.width, identity.height);
    register_alias_identity(base_object);
    register_alias_identity(
        FableTitleBillboardTileHeaderAddressForObject(base_object));
    if (IsFableTitleBillboardTileUploadObjectAddress(resource.guest_address)) {
      register_alias_identity(
          FableTitleBillboardTileHeaderAddressForObject(resource.guest_address));
    }
    if (IsFableTitleBillboardTileUploadHeaderAddress(resource.guest_address)) {
      register_alias_identity(
          FableTitleBillboardTileObjectAddressForHeader(resource.guest_address));
    }
  } else if (base == kFableTitlePrivateIndirectionCommandBase) {
    register_alias_identity(kFableTitleBillboardCommandTexture);
    register_alias_identity(kFableTitleBillboardCommandTextureAlias);
  } else if (base == kFableTitlePrivateIndirectionHeader0Base) {
    register_alias_identity(kFableTitlePrivateIndirectionHeader0);
  } else if (base == kFableTitlePrivateIndirectionHeader1Base) {
    register_alias_identity(kFableTitlePrivateIndirectionHeader1);
  } else if (base == kFableTitlePrivateIndirectionSourceBase) {
    register_alias_identity(kFableTitlePrivateIndirectionSource);
    register_alias_identity(kFableTitlePrivateIndirectionSourceAlias);
  } else if (base == 0x1A0D9000u && identity.width == 512u &&
             identity.height == 256u) {
    // ADDRESS GATE (census: gui_text_atlas_alias_registration) — payload
    // literals under a derived base+extent key, same class as above.
    DirectAddressGateTest(DirectAddressGate::kGuiTextAtlasAliasRegistration,
                          true, base,
                          (static_cast<uint64_t>(identity.width) << 32) |
                              identity.height);
    register_alias_identity(0x4051D610u);
    register_alias_identity(0x4051F650u);
    register_alias_identity(0x4051F610u);
    register_alias_identity(0x4051FF90u);
  } else if (base == 0x1A0D7000u && identity.width == 16u &&
             identity.height == 16u) {
    // ADDRESS GATE (census: tiny_title_16x16_alias_registration).
    DirectAddressGateTest(DirectAddressGate::kTinyTitle16x16AliasRegistration,
                          true, base,
                          (static_cast<uint64_t>(identity.width) << 32) |
                              identity.height);
    register_alias_identity(0x404DE490u);
    register_alias_identity(0x404DE4D0u);
    register_alias_identity(0x404DE550u);
  } else if (base == 0x1FAB5000u && identity.width == 64u &&
             identity.height == 1u) {
    // ADDRESS GATE (census: one_dimensional_title_alias_registration) — six
    // branches, one gate; `addr` in the census names which base last fired.
    DirectAddressGateTest(
        DirectAddressGate::kOneDimensionalTitleAliasRegistration, true, base);
    register_alias_identity(0x401317D0u);
  } else if (base == 0x1FAB4000u && identity.width == 256u &&
             identity.height == 1u) {
    DirectAddressGateTest(
        DirectAddressGate::kOneDimensionalTitleAliasRegistration, true, base);
    register_alias_identity(0x40131810u);
  } else if (base == 0x1FAB3000u && identity.width == 64u &&
             identity.height == 1u) {
    DirectAddressGateTest(
        DirectAddressGate::kOneDimensionalTitleAliasRegistration, true, base);
    register_alias_identity(0x40131850u);
  } else if (base == 0x1FAB2000u && identity.width == 256u &&
             identity.height == 1u) {
    DirectAddressGateTest(
        DirectAddressGate::kOneDimensionalTitleAliasRegistration, true, base);
    register_alias_identity(0x40131890u);
  } else if (base == 0x1FAB1000u && identity.width == 64u &&
             identity.height == 1u) {
    DirectAddressGateTest(
        DirectAddressGate::kOneDimensionalTitleAliasRegistration, true, base);
    register_alias_identity(0x401318D0u);
  } else if (base == 0x1FAB0000u && identity.width == 256u &&
             identity.height == 1u) {
    DirectAddressGateTest(
        DirectAddressGate::kOneDimensionalTitleAliasRegistration, true, base);
    register_alias_identity(0x40131910u);
  } else if (base == 0x13520000u && identity.width == 64u &&
             identity.height == 64u) {
    // ADDRESS GATE (census: billboard_tile_alias_registration) — six branches,
    // one gate; `addr` names which tile base last fired.
    DirectAddressGateTest(DirectAddressGate::kBillboardTileAliasRegistration,
                          true, base);
    register_alias_identity(0x44127210u);
  } else if (base == 0x13530000u && identity.width == 128u &&
             identity.height == 128u) {
    DirectAddressGateTest(DirectAddressGate::kBillboardTileAliasRegistration,
                          true, base);
    register_alias_identity(0x44127278u);
    register_alias_identity(0x441272ACu);
  } else if (base == 0x13540000u && identity.width == 128u &&
             identity.height == 128u) {
    DirectAddressGateTest(DirectAddressGate::kBillboardTileAliasRegistration,
                          true, base);
    register_alias_identity(0x441272E0u);
    register_alias_identity(0x44127314u);
  } else if (base == 0x13550000u && identity.width == 128u &&
             identity.height == 128u) {
    DirectAddressGateTest(DirectAddressGate::kBillboardTileAliasRegistration,
                          true, base);
    register_alias_identity(0x44127348u);
    register_alias_identity(0x4412737Cu);
  } else if (base == 0x13580000u &&
             ((identity.width == 128u && identity.height == 128u) ||
              (identity.width == 64u && identity.height == 64u))) {
    DirectAddressGateTest(DirectAddressGate::kBillboardTileAliasRegistration,
                          true, base);
    register_alias_identity(0x44127480u);
    register_alias_identity(0x441274B4u);
  } else if (base == 0x13590000u && identity.width == 128u &&
             identity.height == 128u) {
    DirectAddressGateTest(DirectAddressGate::kBillboardTileAliasRegistration,
                          true, base);
    register_alias_identity(0x441274E8u);
    register_alias_identity(0x4412751Cu);
  }
  return registered_any_identity;
}

bool DirectTextureFetchDwordsMatch(
    const std::array<uint32_t, 6>& lhs,
    const std::array<uint32_t, 6>& rhs) {
  return FableTextureFetchDwordsMatch(lhs, rhs);
}

bool DirectMutablePrivateFetchCandidateHasContent(
    const PlumeTrackedResource& resource) {
  const bool mutable_private_header =
      IsFableMutablePrivateTextureHeaderAddress(resource.guest_address) ||
      IsFableMutablePrivateTextureHeaderAddress(resource.parent_address) ||
      IsFableMutablePrivateTextureHeaderAddress(
          PlumeGuestTextureBaseAddress(resource));
  return !mutable_private_header ||
         FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(resource);
}

PlumeTrackedResource* FindDirectReadyUploadedTextureByFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords,
    const PlumeTrackedResource* skip_resource,
    bool* used_identity_bucket) {
  if (!HasTrackedFetchDwords(dwords)) {
    return nullptr;
  }
  if (used_identity_bucket) {
    *used_identity_bucket = false;
  }

  if (const std::vector<uint32_t>* objects =
          FableDirectFindTextureIdentityObjectsByFetch(dwords)) {
    PlumeTrackedResource* exact = nullptr;
    uint32_t match_count = 0;
    for (uint32_t object : *objects) {
      const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(object);
      if (!identity) {
        continue;
      }
	    if (PlumeTrackedResource* resource =
	            FindDirectResourceLocked(identity->object_texture)) {
	      if (resource != skip_resource && IsImageResource(resource->kind) &&
	          PlumeGuestImageReady(*resource) &&
	          FableTrackedResourceHasMeaningfulTextureUpload(*resource) &&
	          DirectTextureFetchDwordsMatch(identity->fetch_dwords, dwords)) {
	        exact = resource;
	        ++match_count;
	      }
	    }
	  }
	  if (match_count == 1u) {
	    if (used_identity_bucket) {
	      *used_identity_bucket = true;
	    }
	    return exact;
	  }
	  if (match_count > 1u) {
	    static uint32_t logged_exact_uploaded_fetch_texture_ambiguity_rejects = 0;
	    if (logged_exact_uploaded_fetch_texture_ambiguity_rejects < 32u) {
	      REXLOG_INFO(
	          "Refii plume exact uploaded fetch texture ambiguity reject #{} "
	          "matches={} fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
	          "candidate=0x{:08X}/{}x{}",
	          ++logged_exact_uploaded_fetch_texture_ambiguity_rejects,
	          match_count,
	          dwords[0], dwords[1], dwords[2], dwords[3], dwords[4], dwords[5],
	          exact ? exact->guest_address : 0u,
	          exact ? PlumeGuestTextureWidth(*exact) : 0u,
	          exact ? PlumeGuestTextureHeight(*exact) : 0u);
	    }
	  }
	}

  return nullptr;
}

// Destination lookup for an EDRAM resolve: fetch constant -> the host resource
// that owns that console memory. Deliberately NOT one of the two lookups above.
//
// Both of those are SAMPLING lookups — they answer "give me a texture worth
// READING", and so they gate on content:
//   * FindDirectReadyRegisteredTextureByFetchDwordsLocked ->
//       DirectMutablePrivateFetchCandidateHasContent, which for a
//       mutable-private header requires HasNonBlankUploadOrGpuProducedContent.
//   * FindDirectReadyUploadedTextureByFetchDwordsLocked ->
//       FableTrackedResourceHasMeaningfulTextureUpload.
// A resolve DESTINATION is blank precisely because we are about to write it,
// and its header IS a mutable-private one, so it fails both by construction.
// Measured (run 446): using the sampling lookup as a destination probe missed
// ~94% of destinations, and the cause split was filtered_out ~80% (the resource
// EXISTS and is thrown away) vs no_identity ~20% (genuinely absent) with
// dwords_rejected=0 (our fetch capture is correct).
//
// Those content gates are RIGHT for sampling — relaxing them would hand blank
// proxies to draws and break the path that renders the game today. So this is a
// separate predicate rather than a loosened shared one: a destination only needs
// to exist and have a host image.
//
// No candidate ranking (design doc invariant): on ambiguity we prefer the
// EXACT object the resolve named (preferred_guest = the packet's r6
// dest_texture), and otherwise refuse rather than guess. `ambiguous` reports
// the refusal so it can be measured instead of silently becoming a miss.
// Per-gate rejection tally, so a miss says WHICH gate refused rather than just
// "no". Run 447 measured this lookup recovering exactly HALF the destinations
// the sampling lookup discarded — so a second rejection lives in here, and a
// bare null cannot say which of the four gates it is.

PlumeTrackedResource* FindDirectResolveDestinationByFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords, uint32_t preferred_guest,
    bool* ambiguous, ResolveDestLookupStats* stats) {
  if (ambiguous) {
    *ambiguous = false;
  }
  if (!HasTrackedFetchDwords(dwords)) {
    return nullptr;
  }
  const std::vector<uint32_t>* objects =
      FableDirectFindTextureIdentityObjectsByFetch(dwords);
  if (!objects) {
    return nullptr;
  }
  PlumeTrackedResource* preferred = nullptr;
  PlumeTrackedResource* single = nullptr;
  uint32_t match_count = 0;
  for (uint32_t object : *objects) {
    if (stats) {
      ++stats->candidates;
    }
    const FableTextureIdentity* identity =
        FableDirectFindTextureIdentityByObject(object);
    if (!identity) {
      if (stats) {
        ++stats->no_identity_record;
      }
      continue;
    }
    PlumeTrackedResource* resource =
        FindDirectResourceLocked(identity->object_texture);
    if (!resource) {
      if (stats) {
        ++stats->no_resource;
      }
      continue;
    }
    if (!IsImageResource(resource->kind)) {
      if (stats) {
        ++stats->not_image;
      }
      continue;
    }
    if (!PlumeGuestImageReady(*resource)) {
      if (stats) {
        ++stats->not_ready;
      }
      continue;
    }
    // NOTE: this is an EXACT six-dword match, which is stricter than the
    // sampling lookup's dimensions-only test. Fable re-points pooled headers
    // (see Resource::address_generation), so a stored identity can legitimately
    // carry an older fetch constant for the same object — which made this the
    // prime suspect for the half this lookup misses. MEASURED AND EXONERATED
    // (run 448): fetch_mismatch was 0 across the whole run, as was
    // dwords_rejected. The misses land one gate earlier, on no_resource (7 of
    // 10) and on objects==nullptr (3 of 10). Do not relax this match.
    if (!DirectTextureFetchDwordsMatch(identity->fetch_dwords, dwords)) {
      if (stats) {
        ++stats->fetch_mismatch;
      }
      continue;
    }
    ++match_count;
    if (stats) {
      ++stats->matched;
    }
    single = resource;
    if (preferred_guest != 0 && resource->guest_address == preferred_guest) {
      preferred = resource;
    }
  }
  if (preferred) {
    return preferred;
  }
  if (match_count == 1u) {
    return single;
  }
  if (match_count > 1u && ambiguous) {
    *ambiguous = true;
  }
  return nullptr;
}

PlumeTrackedResource* FindDirectReadyUploadedTextureByBaseLocked(
    uint32_t base_address, uint32_t proxy_width, uint32_t proxy_height,
    uint32_t proxy_format, const PlumeTrackedResource* skip_resource,
    bool* used_identity_bucket) {
  if (base_address == 0) {
    return nullptr;
  }
  if (used_identity_bucket) {
    *used_identity_bucket = false;
  }

  if (const std::vector<uint32_t>* objects =
          FableDirectFindTextureIdentityObjectsByBase(base_address)) {
    PlumeTrackedResource* exact = nullptr;
    PlumeTrackedResource* ready_sample = nullptr;
    uint32_t exact_match_count = 0;
    uint32_t ready_count = 0;
    for (uint32_t object : *objects) {
      const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(object);
      if (!identity || identity->base_address != base_address) {
        continue;
      }
      PlumeTrackedResource* resource =
          FindDirectResourceLocked(identity->object_texture);
      if (!resource || resource == skip_resource ||
          !IsImageResource(resource->kind) ||
          !PlumeGuestImageReady(*resource) ||
          !FableTrackedResourceHasMeaningfulTextureUpload(*resource) ||
          PlumeGuestTextureBaseAddress(*resource) != base_address) {
        continue;
      }
      if (!ready_sample) {
        ready_sample = resource;
      }
      ++ready_count;
      const uint32_t resource_guest_format =
          PlumeGuestTextureGuestFormat(*resource);
      const uint32_t resource_render_format =
          static_cast<uint32_t>(PlumeGuestTextureFormat(*resource));
      const bool format_matches =
          proxy_format == 0 || resource_guest_format == proxy_format ||
          resource_render_format == proxy_format;
      if (PlumeGuestTextureWidth(*resource) == proxy_width &&
          PlumeGuestTextureHeight(*resource) == proxy_height &&
          format_matches &&
          resource != exact) {
        exact = resource;
        ++exact_match_count;
      }
    }
    if (exact_match_count == 1u) {
      if (used_identity_bucket) {
        *used_identity_bucket = true;
      }
      return exact;
    }
    if (exact_match_count > 1u) {
      static uint32_t logged_exact_uploaded_base_texture_ambiguity_rejects = 0;
      if (logged_exact_uploaded_base_texture_ambiguity_rejects < 32u) {
        REXLOG_INFO(
            "Refii plume exact uploaded base texture ambiguity reject #{} "
            "matches={} ready={} base=0x{:08X} request={}x{} fmt={} "
            "candidate=0x{:08X}/{}x{} guest_fmt={} render_fmt={} gen={}",
            ++logged_exact_uploaded_base_texture_ambiguity_rejects,
            exact_match_count, ready_count, base_address, proxy_width,
            proxy_height, proxy_format, exact ? exact->guest_address : 0u,
            exact ? PlumeGuestTextureWidth(*exact) : 0u,
            exact ? PlumeGuestTextureHeight(*exact) : 0u,
            exact ? PlumeGuestTextureGuestFormat(*exact) : 0u,
            exact ? static_cast<uint32_t>(PlumeGuestTextureFormat(*exact)) : 0u,
            exact ? static_cast<unsigned long long>(exact->upload_generation)
                  : 0ull);
      }
    } else if (ready_count > 0u) {
      static uint32_t logged_exact_uploaded_base_texture_shape_rejects = 0;
      if (logged_exact_uploaded_base_texture_shape_rejects < 32u) {
        REXLOG_INFO(
            "Refii plume exact uploaded base texture shape reject #{} "
            "ready={} base=0x{:08X} request={}x{} fmt={} "
            "candidate=0x{:08X}/{}x{} guest_fmt={} render_fmt={} gen={}",
            ++logged_exact_uploaded_base_texture_shape_rejects, ready_count,
            base_address, proxy_width, proxy_height, proxy_format,
            ready_sample ? ready_sample->guest_address : 0u,
            ready_sample ? PlumeGuestTextureWidth(*ready_sample) : 0u,
            ready_sample ? PlumeGuestTextureHeight(*ready_sample) : 0u,
            ready_sample ? PlumeGuestTextureGuestFormat(*ready_sample) : 0u,
            ready_sample
                ? static_cast<uint32_t>(PlumeGuestTextureFormat(*ready_sample))
                : 0u,
            ready_sample ? static_cast<unsigned long long>(
                               ready_sample->upload_generation)
                         : 0ull);
      }
    }
  }

  return nullptr;
}

PlumeTrackedResource* FindDirectReadyUploadedTextureByBaseLocked(
    uint32_t base_address,
    const PlumeTrackedResource& proxy,
    bool* used_identity_bucket) {
  return FindDirectReadyUploadedTextureByBaseLocked(
      base_address, PlumeGuestTextureWidth(proxy), PlumeGuestTextureHeight(proxy),
      static_cast<uint32_t>(PlumeGuestTextureFormat(proxy)), &proxy,
      used_identity_bucket);
}

DirectTextureFetchInfo DirectTextureFetchInfoFromDwords(
    const std::array<uint32_t, 6>& dwords) {
  return FableTextureFetchInfoFromDwords(dwords);
}

#endif

}  // namespace refii::renderer
