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

void PrepareDirectExactCopyDestinationsForCommandLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command) {
  if (!device || command.draw.source == RendererDrawEventSource::kPixVideo) {
    return;
  }

  const uint64_t draw_serial =
      command.draw_serial != 0 ? command.draw_serial : command.serial;
  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  const auto exact_title_copy_symbol_for_link =
      [](const FableSurfaceTextureLink& link) {
        return link.copy_symbol_address ==
                   kFableBeginVerticesTitleWrapperSymbol ||
               link.copy_symbol_address ==
                   kFablePrivateRectSiblingWrapperSymbol ||
               link.copy_symbol_address ==
                   kFablePrivateRectSiblingWrapperCallsite;
      };

  auto link_visible_to_command = [&](const FableSurfaceTextureLink* link) {
    return link && DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
                       command, *link);
  };
  auto find_link_by_destination =
      [&](uint32_t texture, uint32_t slot) -> const FableSurfaceTextureLink* {
    if (texture == 0) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
            texture, g_direct_renderer.current_frame_index, draw_serial, slot);
    return link_visible_to_command(link) ? link : nullptr;
  };
  auto find_link_by_surface_alias_destination =
      [&](uint32_t texture, uint32_t slot) -> const FableSurfaceTextureLink* {
    RendererSurfaceCopyAliasSnapshot alias;
    if (!FindDirectSurfaceCopyAliasByDestinationAddressLocked(texture,
                                                             &alias)) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FindDirectSurfaceTextureLinkByAliasLocked(alias);
    if (!link_visible_to_command(link)) {
      return nullptr;
    }
    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    if (link->source_slot != slot && link->source_slot != kUnknownSlot) {
      const bool exact_private_title_cross_slot =
          exact_title_copy_symbol_for_link(*link) &&
          DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr);
      if (!exact_private_title_cross_slot) {
        return nullptr;
      }
    }
    return link;
  };
  auto find_link_by_base =
      [&](uint32_t base, uint32_t slot) -> const FableSurfaceTextureLink* {
    if (base == 0) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindVisibleSurfaceTextureLinkForBase(
            base, g_direct_renderer.current_frame_index, draw_serial, slot);
    return link_visible_to_command(link) ? link : nullptr;
  };
  auto find_link_by_fetch =
      [&](const std::array<uint32_t, 6>& fetch,
          uint32_t slot) -> const FableSurfaceTextureLink* {
    if (!HasTrackedFetchDwords(fetch)) {
      return nullptr;
    }
    const FableSurfaceTextureLink* link =
        FableDirectFindVisibleSurfaceTextureLinkForFetch(
            fetch, g_direct_renderer.current_frame_index, draw_serial, slot);
    return link_visible_to_command(link) ? link : nullptr;
  };

  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }

    const uint32_t guest = command.resources.textures[slot];
    if (guest == 0) {
      continue;
    }
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const DirectTextureFetchInfo command_fetch_info =
        DirectTextureFetchInfoFromDwords(command_fetch);

    const FableSurfaceTextureLink* link =
        find_link_by_surface_alias_destination(guest, slot);
    if (!link) {
      link = find_link_by_destination(guest, slot);
    }
    if (!link && command_fetch_info.valid) {
      link = find_link_by_base(command_fetch_info.base_address, slot);
    }
    if (!link && command_fetch_info.valid) {
      link = find_link_by_fetch(command_fetch, slot);
    }
    if (!link) {
      continue;
    }

    const bool link_has_exact_texture_fetch =
        HasTrackedFetchDwords(link->texture_fetch_dwords);
    if (!command_fetch_info.valid && !link_has_exact_texture_fetch) {
      continue;
    }
    const uint32_t destination_base =
        command_fetch_info.valid ? command_fetch_info.base_address
                                 : link->texture_base_address;
    const std::array<uint32_t, 6>& destination_fetch =
        command_fetch_info.valid ? command_fetch : link->texture_fetch_dwords;

    const bool exact_sibling_title_copy_symbol =
        link->copy_symbol_address == kFablePrivateRectSiblingWrapperSymbol ||
        link->copy_symbol_address == kFablePrivateRectSiblingWrapperCallsite;
    const bool exact_title_copy_symbol =
        link->copy_symbol_address == kFableBeginVerticesTitleWrapperSymbol ||
        exact_sibling_title_copy_symbol;
    const bool exact_command_destination =
        DirectResourceAddressMatchesLocked(link->texture, guest) ||
        (exact_title_copy_symbol &&
         IsFableTitleTextureIndirectionChainAddress(guest) &&
         DirectSurfaceTextureLinkTouchesFablePrivateChain(*link, nullptr));
    if (!link->trusted_destination_edge ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(*link) ||
        !DirectPrivateTextureCopyKindCanBlitToColorTarget(link->kind) ||
        !exact_command_destination) {
      continue;
    }

    PlumeTrackedResource* destination = FindDirectResourceLocked(guest);
    if (destination && IsImageResource(destination->kind) &&
        PlumeGuestImageReady(*destination)) {
      PromoteDirectExactCopyDestinationToColorTargetLocked(device, *destination,
                                                           *link);
      continue;
    }

    FableGuestResourceSnapshot snapshot;
    snapshot.kind = FableGuestResourceKind::kTexture;
    snapshot.guest_address = guest;
    snapshot.parent_address = guest;
    snapshot.texture.object_address = guest;
    snapshot.texture.parent_address = guest;
    snapshot.texture.base_address = destination_base;
    snapshot.texture.raw_fetch_dwords = destination_fetch;
    snapshot.texture.normalized_fetch_dwords = destination_fetch;
    snapshot.serial = std::max<uint64_t>(
        1ull, std::max(command.resource_serial,
                       std::max(command.serial, draw_serial)));
    if (!SyncTrackedResourceLocked(device, snapshot, nullptr)) {
      ++g_direct_renderer.failed_resource_syncs;
      LogDirectResourceSyncFailureLocked(snapshot,
                                         "exact_copy_destination_preflight");
      continue;
    }

    destination = FindDirectResourceLocked(guest);
    if (!destination || !IsImageResource(destination->kind) ||
        !PlumeGuestImageReady(*destination)) {
      continue;
    }

    RegisterDirectTextureIdentityFromResourceLocked(*destination,
                                                    destination_fetch);
    PromoteDirectExactCopyDestinationToColorTargetLocked(device, *destination,
                                                         *link);
    static uint32_t logged_exact_destination_preparations = 0;
    if (logged_exact_destination_preparations < 48u) {
      REXLOG_INFO(
          "Refii plume prepared exact copy destination from command #{} "
          "frame={} slot={} guest=0x{:08X} base=0x{:08X} {}x{} fmt={} "
          "link_surface=0x{:08X} symbol=0x{:08X} call={} "
          "context=0x{:016X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X}",
          ++logged_exact_destination_preparations,
          g_direct_renderer.current_frame_index, slot, guest,
          PlumeGuestTextureBaseAddress(*destination),
          PlumeGuestTextureWidth(*destination),
          PlumeGuestTextureHeight(*destination),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*destination)),
          link->source_surface, link->copy_symbol_address,
          static_cast<unsigned long long>(link->copy_call_serial),
          static_cast<unsigned long long>(link->copy_context_key),
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }
}

bool DirectTitleBillboardCommandHasAuthoritativeAtlasSampleLocked(
    const FableDirectRenderCommand& command) {
  if (!IsFableTitleBillboardAtlasCommandSlotLocked(
          command, kFablePrivateRectCopyTextureSlot)) {
    return false;
  }

  const uint32_t guest =
      command.resources.textures[kFablePrivateRectCopyTextureSlot];
  PlumeTrackedResource* direct_texture = FindDirectResourceLocked(guest);
  PlumeTrackedResource* atlas =
      FindExactFableTitleBillboardAtlasForCommandSlotLocked(
          command, kFablePrivateRectCopyTextureSlot, direct_texture);
  if (!atlas || !PlumeGuestImageReady(*atlas) ||
      DirectCommandBindsResourceAsTargetLocked(command, *atlas) ||
      (!FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*atlas) &&
       !DirectFableTitleBillboardAtlasHasAuthoritativeUpload(*atlas))) {
    return false;
  }

  return true;
}

bool DirectTrackedResourceLooksLikeBinkPlaneSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& resource) {
  if (slot >= 3u || !IsImageResource(resource.kind) ||
      !PlumeGuestImageReady(resource) ||
      !FableVideoBinkPlaneHasDecodedUpload(resource) ||
      DirectCommandBindsResourceAsTargetLocked(command, resource)) {
    return false;
  }

  const uint32_t width = PlumeGuestTextureWidth(resource);
  const uint32_t height = PlumeGuestTextureHeight(resource);
  if (slot == 0u) {
    return width >= 64u && height >= 64u && (width & 1u) == 0u &&
           (height & 1u) == 0u;
  }

  PlumeTrackedResource* y_resource =
      FindDirectResourceLocked(command.resources.textures[0]);
  // CONTENT-keyed fallback: Bink Y planes are the movie's authored
  // 1280x720 content resolution, independent of the guest display dims —
  // do NOT couple to FableGuestDisplayWidth/Height.
  const uint32_t y_width =
      y_resource && IsImageResource(y_resource->kind)
          ? PlumeGuestTextureWidth(*y_resource)
          : 1280u;
  const uint32_t y_height =
      y_resource && IsImageResource(y_resource->kind)
          ? PlumeGuestTextureHeight(*y_resource)
          : 720u;
  return width * 2u == y_width && height * 2u == y_height;
}

PlumeTrackedResource* FindDirectExactBinkPlaneResourceForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= 3u || slot >= command.resources.textures.size()) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch);
  auto resource_matches_slot_fetch =
      [&](const PlumeTrackedResource& resource) -> bool {
    if (!DirectTrackedResourceLooksLikeBinkPlaneSlotLocked(command, slot,
                                                          resource)) {
      return false;
    }
    if (!fetch_info.valid) {
      return true;
    }
    return PlumeGuestTextureWidth(resource) == fetch_info.width &&
           PlumeGuestTextureHeight(resource) == fetch_info.height &&
           PlumeGuestTextureBaseAddress(resource) == fetch_info.base_address &&
           DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(resource),
                                         fetch);
  };
  auto try_resource = [&](uint32_t address,
                          bool allow_alias) -> PlumeTrackedResource* {
    PlumeTrackedResource* resource =
        FindDirectResourceByExactObjectLocked(address);
    if (!resource && allow_alias) {
      resource = FindDirectResourceLocked(address);
    }
    return resource && resource_matches_slot_fetch(*resource) ? resource
                                                              : nullptr;
  };
  auto try_identity = [&](const FableTextureIdentity* identity)
      -> PlumeTrackedResource* {
    if (!identity) {
      return nullptr;
    }
    if (PlumeTrackedResource* resource =
            try_resource(identity->object_texture, true)) {
      return resource;
    }
    if (PlumeTrackedResource* resource =
            try_resource(identity->parent_texture, true)) {
      return resource;
    }
    return try_resource(identity->base_address, true);
  };

  if (PlumeTrackedResource* resource = try_resource(guest, false)) {
    return resource;
  }
  if (PlumeTrackedResource* resource =
          try_identity(FableDirectFindTextureIdentityByObject(guest))) {
    return resource;
  }

  if (fetch_info.valid) {
    if (PlumeTrackedResource* resource =
            try_resource(fetch_info.base_address, true)) {
      return resource;
    }
    if (PlumeTrackedResource* resource =
            try_resource(fetch_info.base_address & 0xFFFFF000u, true)) {
      return resource;
    }
  }

  return nullptr;
}

bool DirectCommandHasReadyExactBinkWrapperPlanesLocked(
    const FableDirectRenderCommand& command) {
  PlumeTrackedResource* y =
      FindDirectExactBinkPlaneResourceForSlotLocked(command, 0u);
  PlumeTrackedResource* cr =
      FindDirectExactBinkPlaneResourceForSlotLocked(command, 1u);
  PlumeTrackedResource* cb =
      FindDirectExactBinkPlaneResourceForSlotLocked(command, 2u);
  if (!y || !cr || !cb) {
    return false;
  }
  return FableVideoBinkPlaneChromaShapeMatchesLuma(*y, *cr, *cb);
}

bool DirectBinkWrapperPlaneAddressSetReadyLocked(
    const std::array<uint32_t, 3>& planes) {
  PlumeTrackedResource* y = FindDirectResourceLocked(planes[0]);
  PlumeTrackedResource* cr = FindDirectResourceLocked(planes[1]);
  PlumeTrackedResource* cb = FindDirectResourceLocked(planes[2]);
  if (!y || !cr || !cb ||
      !FableVideoBinkPlaneResourcesReadyDecoded(*y, *cr, *cb)) {
    return false;
  }
  return FableVideoBinkPlaneChromaShapeMatchesLuma(*y, *cr, *cb);
}

bool DirectBinkWrapperPlaneResourceSetReadyLocked(
    const FableDirectRenderCommand& command,
    const std::array<PlumeTrackedResource*, 3>& planes) {
  PlumeTrackedResource* y = planes[0];
  PlumeTrackedResource* cr = planes[1];
  PlumeTrackedResource* cb = planes[2];
  if (!y || !cr || !cb ||
      !FableVideoBinkPlaneResourcesReadyDecoded(*y, *cr, *cb) ||
      DirectCommandBindsResourceAsTargetLocked(command, *y) ||
      DirectCommandBindsResourceAsTargetLocked(command, *cr) ||
      DirectCommandBindsResourceAsTargetLocked(command, *cb)) {
    return false;
  }

  const uint32_t y_width = PlumeGuestTextureWidth(*y);
  const uint32_t y_height = PlumeGuestTextureHeight(*y);
  return y_width >= 64u && y_height >= 64u && (y_width & 1u) == 0u &&
         (y_height & 1u) == 0u &&
         FableVideoBinkPlaneChromaShapeMatchesLuma(*y, *cr, *cb);
}

bool DirectBinkWrapperCommandPlaneSetReadyLocked(
    const FableDirectRenderCommand& command,
    std::array<uint32_t, 3>& ready_planes) {
  if (command.resources.textures.size() < 3u) {
    return false;
  }

  std::array<PlumeTrackedResource*, 3> resources = {};
  for (uint32_t slot = 0; slot < 3u; ++slot) {
    const uint32_t guest = command.resources.textures[slot];
    if (guest == 0) {
      return false;
    }
    resources[slot] = FindDirectExactBinkPlaneResourceForSlotLocked(command,
                                                                    slot);
  }

  if (!DirectBinkWrapperPlaneResourceSetReadyLocked(command, resources)) {
    return false;
  }

  for (uint32_t slot = 0; slot < 3u; ++slot) {
    ready_planes[slot] = resources[slot]->guest_address;
  }
  return true;
}

void LogExactBinkIndexedWrapperFilterLocked(
    const FableDirectRenderCommand& command, std::string_view reason,
    const DirectTextureFetchInfo* y,
    const DirectTextureFetchInfo* cr,
    const DirectTextureFetchInfo* cb) {
  static uint32_t logged_filters = 0;
  static std::unordered_set<uint64_t> logged_filter_keys;
  if (logged_filters >= 32u ||
      (!RendererDrawTracingEnabled() && !RendererVerboseTracingEnabled())) {
    return;
  }
  if (command.draw.source != RendererDrawEventSource::kScratchWrapper ||
      command.resources.pixel_shader == 0) {
    return;
  }
  const uint64_t pixel_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  // Dedup before the ready-planes probe: the probe walks the tracked-resource
  // map per plane slot, and rejected candidates (e.g. storybook scratch
  // wrappers with a non-video pixel shader) repeat identically every frame.
  // The key deliberately omits command serials so those repeats collapse.
  size_t key_seed = 0;
  DirectHashCombine(key_seed, std::hash<std::string_view>{}(reason));
  DirectHashCombine(key_seed, std::hash<uint32_t>{}(
                            command.draw.private_texture_wrapper_symbol_address));
  DirectHashCombine(key_seed, std::hash<uint32_t>{}(command.resources.pixel_shader));
  DirectHashCombine(key_seed, std::hash<uint64_t>{}(pixel_hash));
  DirectHashCombine(key_seed, std::hash<uint32_t>{}(command.resources.textures[0]));
  DirectHashCombine(key_seed, std::hash<uint32_t>{}(command.resources.textures[1]));
  DirectHashCombine(key_seed, std::hash<uint32_t>{}(command.resources.textures[2]));
  DirectHashCombine(key_seed, std::hash<uint32_t>{}(command.resources.textures[3]));
  const uint64_t filter_key =
      key_seed != 0 ? static_cast<uint64_t>(key_seed) : 1ull;
  if (logged_filter_keys.size() >= 512u ||
      !logged_filter_keys.insert(filter_key).second) {
    return;
  }
  const bool ready_planes =
      DirectCommandHasReadyExactBinkWrapperPlanesLocked(command);
  if (!FableVideoBinkWrapperPixelShaderHashAllowed(pixel_hash) &&
      !ready_planes) {
    return;
  }

  const auto format_fetch = [](const DirectTextureFetchInfo* info,
                               uint32_t texture) {
    char text[96] = {};
    std::snprintf(text, sizeof(text),
                  "tex=0x%08X valid=%u base=0x%08X %ux%u fmt=%u", texture,
                  info && info->valid ? 1u : 0u,
                  info ? info->base_address : 0u, info ? info->width : 0u,
                  info ? info->height : 0u, info ? info->format : 0u);
    return std::string(text);
  };
  REXLOG_INFO(
      "Refii plume exact Bink wrapper candidate filter #{} "
      "reason={} serial={} draw={} "
      "source={} wrapper=0x{:08X} indexed={} prim={} base={} start={} count={} "
      "rt=0x{:08X} vs=0x{:08X} ps=0x{:08X} pshash=0x{:016X} "
      "planes_ready={} vb_ready={} ib_ready={} "
      "tex0=0x{:08X} tex1=0x{:08X} tex2=0x{:08X} tex3=0x{:08X} "
      "slot0=[{}] slot1=[{}] slot2=[{}]",
      ++logged_filters, reason, static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial),
      static_cast<uint32_t>(command.draw.source),
      command.draw.private_texture_wrapper_symbol_address,
      command.draw.draw.indexed ? 1 : 0, command.draw.draw.primitive_type,
      command.draw.draw.base_vertex, command.draw.draw.start,
      command.draw.draw.primitive_count, command.resources.render_targets[0],
      command.resources.vertex_shader, command.resources.pixel_shader,
      static_cast<unsigned long long>(pixel_hash),
      ready_planes ? 1 : 0,
      command.inline_vertex_buffers[0].ready() ? 1 : 0,
      command.inline_index_buffer.ready() ? 1 : 0,
      command.resources.textures[0], command.resources.textures[1],
      command.resources.textures[2], command.resources.textures[3],
      format_fetch(y, command.resources.textures[0]),
      format_fetch(cr, command.resources.textures[1]),
      format_fetch(cb, command.resources.textures[2]));
}

bool DirectCommandIsExactBinkIndexedWrapperDrawLocked(
    const FableDirectRenderCommand& command) {
  // Memoized per recorded command instance: the classifier is re-invoked per
  // texture slot and per record pass, and its ready-planes probe walks the
  // tracked-resource map. Textures and pixel shader participate in the key so
  // staged-draw patches (RefreshQueuedDrawTextureStateLocked) re-evaluate.
  size_t memo_seed = 0;
  DirectHashCombine(memo_seed, std::hash<uint64_t>{}(command.serial));
  DirectHashCombine(memo_seed, std::hash<uint64_t>{}(command.draw_serial));
  DirectHashCombine(memo_seed,
                    std::hash<uint32_t>{}(command.resources.pixel_shader));
  DirectHashCombine(memo_seed,
                    std::hash<uint32_t>{}(command.resources.textures[0]));
  DirectHashCombine(memo_seed,
                    std::hash<uint32_t>{}(command.resources.textures[1]));
  DirectHashCombine(memo_seed,
                    std::hash<uint32_t>{}(command.resources.textures[2]));
  const uint64_t memo_key =
      memo_seed != 0 ? static_cast<uint64_t>(memo_seed) : 1ull;
  static std::unordered_map<uint64_t, bool> classification_memo;
  if (auto it = classification_memo.find(memo_key);
      it != classification_memo.end()) {
    return it->second;
  }
  const auto memoize = [&](bool value) -> bool {
    if (classification_memo.size() >= 8192u) {
      classification_memo.clear();
    }
    classification_memo.emplace(memo_key, value);
    return value;
  };
  const bool bink_wrapper_symbol =
      FableVideoCommandHasBinkIndexedWrapperSymbol(command);
  if (!FableVideoCommandHasBinkIndexedWrapperDrawShape(command)) {
    LogExactBinkIndexedWrapperFilterLocked(command, "shape");
    return memoize(false);
  }

  const uint64_t pixel_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  if (!bink_wrapper_symbol) {
    if (FableVideoCommandUsesKnownVideoPixelShader(pixel_hash) &&
        DirectCommandHasReadyExactBinkWrapperPlanesLocked(command)) {
      return memoize(true);
    }
    LogExactBinkIndexedWrapperFilterLocked(command, "wrapper");
    return memoize(false);
  }
  if (!FableVideoBinkWrapperPixelShaderHashAllowed(pixel_hash)) {
    LogExactBinkIndexedWrapperFilterLocked(command, "pixel-shader");
    return memoize(false);
  }

  const DirectTextureFetchInfo y =
      DirectTextureFetchInfoFromDwords(
          DirectCommandTextureFetchDwords(command, 0));
  const DirectTextureFetchInfo cr =
      DirectTextureFetchInfoFromDwords(
          DirectCommandTextureFetchDwords(command, 1));
  const DirectTextureFetchInfo cb =
      DirectTextureFetchInfoFromDwords(
          DirectCommandTextureFetchDwords(command, 2));
  const bool fetches_match =
      FableVideoCommandFetchesMatchBinkWrapperPlaneSet(command);
  if (!fetches_match) {
    const bool exact_bink_identity =
        bink_wrapper_symbol &&
        FableVideoBinkWrapperPixelShaderHashAllowed(pixel_hash);
    if (exact_bink_identity) {
      return memoize(DirectCommandHasReadyExactBinkWrapperPlanesLocked(command));
    }
    LogExactBinkIndexedWrapperFilterLocked(command, "fetch", &y, &cr, &cb);
  }
  return memoize(fetches_match);
}

bool DirectCommandSlotUsesExactBinkWrapperPlaneLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  return slot < 3u && DirectCommandIsExactBinkIndexedWrapperDrawLocked(command);
}

bool DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(
    const FableDirectRenderCommand& command) {
  FableVideoBinkIndexedWrapperCandidateFacts facts;
  facts.has_draw_shape =
      FableVideoCommandHasBinkIndexedWrapperDrawShape(command);
  if (!facts.has_draw_shape) {
    return FableVideoCommandLooksLikeBinkIndexedWrapperCandidate(facts);
  }

  facts.has_wrapper_symbol =
      FableVideoCommandHasBinkIndexedWrapperSymbol(command);
  const uint64_t pixel_hash =
      DirectPixelShaderHashLocked(command.resources.pixel_shader);
  facts.uses_known_video_pixel_shader =
      FableVideoCommandUsesKnownVideoPixelShader(pixel_hash);
  if (!facts.has_wrapper_symbol && !facts.uses_known_video_pixel_shader) {
    return FableVideoCommandLooksLikeBinkIndexedWrapperCandidate(facts);
  }

  facts.pixel_shader_allowed =
      FableVideoBinkWrapperPixelShaderHashAllowed(pixel_hash);
  facts.fetches_match_plane_set =
      FableVideoCommandFetchesMatchBinkWrapperPlaneSet(command);
  facts.has_ready_exact_planes =
      DirectCommandHasReadyExactBinkWrapperPlanesLocked(command);
  return FableVideoCommandLooksLikeBinkIndexedWrapperCandidate(facts);
}

bool DirectResourceMatchesExactBeginVerticesTitleStripFetch(
    const PlumeTrackedResource* resource,
    const DirectTextureFetchInfo& fetch_info,
    const std::array<uint32_t, 6>& fetch_dwords) {
  return resource && IsImageResource(resource->kind) &&
         FableTitleResourceMatchesExactBeginVerticesTitleStripFetch(
             PlumeGuestImageReady(*resource),
             PlumeGuestTextureBaseAddress(*resource),
             PlumeGuestTextureWidth(*resource),
             PlumeGuestTextureHeight(*resource),
             PlumeGuestTextureGuestFormat(*resource),
             DirectTextureFetchDwordsMatch(
                 PlumeGuestTextureFetchDwords(*resource), fetch_dwords),
             fetch_info);
}

bool DirectResourceIsExactBeginVerticesTitleStripSampleLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource* resource) {
  return resource && IsImageResource(resource->kind) &&
         FableTitleResourceIsExactBeginVerticesTitleStripSample(
             PlumeGuestImageReady(*resource),
             PlumeGuestTextureBaseAddress(*resource),
             PlumeGuestTextureWidth(*resource),
             PlumeGuestTextureHeight(*resource),
             PlumeGuestTextureGuestFormat(*resource),
             FableTrackedResourceHasSampleableContent(*resource) ||
                 FableTrackedResourceHasGpuProducedContent(*resource) ||
                 FableTrackedResourceVisibleContentSerial(*resource) != 0,
             DirectCommandBindsResourceAsTargetLocked(command, *resource));
}

bool DirectTrackedResourceMatchesExactBinkPlaneSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const DirectTextureFetchInfo& fetch_info,
    const PlumeTrackedResource& resource) {
  if (!DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot) ||
      !IsImageResource(resource.kind) || !PlumeGuestImageReady(resource) ||
      !FableVideoBinkPlaneHasDecodedUpload(resource) ||
      DirectCommandBindsResourceAsTargetLocked(command, resource)) {
    return false;
  }

  const uint32_t resource_base = PlumeGuestTextureBaseAddress(resource);
  const bool address_matches =
      resource.guest_address == command.resources.textures[slot] ||
      resource.parent_address == command.resources.textures[slot] ||
      resource_base == command.resources.textures[slot] ||
      resource.guest_address == fetch_info.base_address ||
      resource.parent_address == fetch_info.base_address ||
      resource_base == fetch_info.base_address;
  if (!address_matches ||
      PlumeGuestTextureWidth(resource) != fetch_info.width ||
      PlumeGuestTextureHeight(resource) != fetch_info.height ||
      PlumeGuestTextureBaseAddress(resource) != fetch_info.base_address ||
      !DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(resource),
                                     DirectCommandTextureFetchDwords(command,
                                                                     slot))) {
    return false;
  }

  return true;
}

PlumeTrackedResource* ResolveDirectExactBinkWrapperPlaneForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot < 3u &&
      DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(command)) {
    std::array<uint32_t, 3> ready_planes = {};
    if (DirectBinkWrapperCommandPlaneSetReadyLocked(command, ready_planes)) {
      return FindDirectResourceLocked(ready_planes[slot]);
    }
    // Transient plane-set race: all three plane records resolve and the set
    // is structurally sound, but one plane's decoded upload hasn't landed
    // yet (a fresh texture samples zero — chroma-only zeros render as the
    // green strip). Refuse the bind rather than falling through to the
    // per-slot loose paths below: the held composite keeps the previous
    // complete frame and the set completes within a frame or two.
    std::array<PlumeTrackedResource*, 3> set_resources = {};
    bool set_structurally_ready = true;
    for (uint32_t plane_slot = 0; plane_slot < 3u; ++plane_slot) {
      set_resources[plane_slot] =
          FindDirectExactBinkPlaneResourceForSlotLocked(command, plane_slot);
      set_structurally_ready &= set_resources[plane_slot] != nullptr;
    }
    if (set_structurally_ready &&
        FableVideoBinkPlaneChromaShapeMatchesLuma(
            *set_resources[0], *set_resources[1], *set_resources[2]) &&
        (!FableVideoBinkPlaneHasDecodedUpload(*set_resources[0]) ||
         !FableVideoBinkPlaneHasDecodedUpload(*set_resources[1]) ||
         !FableVideoBinkPlaneHasDecodedUpload(*set_resources[2]))) {
      static uint32_t logged_plane_set_content_races = 0;
      if (logged_plane_set_content_races < 16u) {
        REXLOG_INFO(
            "Refii plume bink plane-set content race #{} serial={} "
            "y=0x{:08X}/{} cr=0x{:08X}/{} cb=0x{:08X}/{}",
            ++logged_plane_set_content_races, command.serial,
            set_resources[0]->guest_address,
            FableVideoBinkPlaneHasDecodedUpload(*set_resources[0]) ? 1 : 0,
            set_resources[1]->guest_address,
            FableVideoBinkPlaneHasDecodedUpload(*set_resources[1]) ? 1 : 0,
            set_resources[2]->guest_address,
            FableVideoBinkPlaneHasDecodedUpload(*set_resources[2]) ? 1 : 0);
      }
      return nullptr;
    }
  }
  if (!DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
    return nullptr;
  }
  if (!FableVideoCommandSlotUsesFetchDecodedBinkWrapperPlane(command,
                                                                    slot)) {
    return nullptr;
  }
  if (PlumeTrackedResource* resource =
          FindDirectExactBinkPlaneResourceForSlotLocked(command, slot)) {
    return resource;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch);
  if (!fetch_info.valid) {
    return nullptr;
  }

  auto try_resource = [&](uint32_t address) -> PlumeTrackedResource* {
    PlumeTrackedResource* resource = FindDirectResourceLocked(address);
    return resource &&
                   DirectTrackedResourceMatchesExactBinkPlaneSlotLocked(
                       command, slot, fetch_info, *resource)
               ? resource
               : nullptr;
  };
  auto try_identity = [&](const FableTextureIdentity* identity)
      -> PlumeTrackedResource* {
    if (!identity) {
      return nullptr;
    }
    if (PlumeTrackedResource* resource = try_resource(identity->object_texture)) {
      return resource;
    }
    if (PlumeTrackedResource* resource = try_resource(identity->parent_texture)) {
      return resource;
    }
    return try_resource(identity->base_address);
  };

  if (PlumeTrackedResource* resource = try_resource(guest)) {
    return resource;
  }
  if (PlumeTrackedResource* resource = try_resource(fetch_info.base_address)) {
    return resource;
  }
  if (PlumeTrackedResource* resource =
          try_resource(fetch_info.base_address & 0xFFFFF000u)) {
    return resource;
  }
  if (PlumeTrackedResource* resource =
          try_identity(FableDirectFindTextureIdentityByObject(guest))) {
    return resource;
  }

  for (auto& [address, resource] : g_direct_renderer.resources) {
    (void)address;
    if (DirectTrackedResourceMatchesExactBinkPlaneSlotLocked(
            command, slot, fetch_info, resource)) {
      return &resource;
    }
  }

  return nullptr;
}

void AddDirectExactBinkWrapperPlaneResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (slot >= 3u ||
      !DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(command)) {
    return;
  }
  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch);
  const FableVideoBinkWrapperPlaneResourceInterests interests =
      FableVideoCollectBinkWrapperPlaneResourceInterests(
          FableVideoBinkWrapperPlaneResourceInterestFacts{
              .wrapper_candidate = true,
              .guest = guest,
              .fetch_valid = fetch_info.valid,
              .fetch_base_address =
                  fetch_info.valid ? fetch_info.base_address : 0u,
          });
  for (uint32_t index = 0; index < interests.resource_count; ++index) {
    AddDirectResourceInterest(resource_filter,
                              interests.resource_addresses[index]);
  }
  for (uint32_t index = 0; index < interests.texture_upload_count; ++index) {
    AddDirectResourceInterest(texture_upload_filter,
                              interests.texture_upload_addresses[index]);
  }
  if (interests.include_texture_slot_identity) {
    AddDirectTextureSlotIdentityResourceInterestsLocked(
        resource_filter, texture_upload_filter, command, slot);
  }
}

bool IsFableExactTitleBillboardTileUploadIdentity(
    uint32_t command_guest, const DirectTextureFetchInfo& command_fetch_info,
    const PlumeTrackedResource& candidate) {
  return FableTitleBillboardTileUploadIdentity(
      FableTitleBillboardTileUploadIdentityFacts{
          .command_fetch_valid = command_fetch_info.valid,
          .command_guest = command_guest,
          .command_fetch_base_address = command_fetch_info.base_address,
          .command_fetch_width = command_fetch_info.width,
          .command_fetch_height = command_fetch_info.height,
          .guest_address = candidate.guest_address,
          .parent_address = candidate.parent_address,
          .base_address = PlumeGuestTextureBaseAddress(candidate),
          .width = PlumeGuestTextureWidth(candidate),
          .height = PlumeGuestTextureHeight(candidate),
          .authoritative_upload =
              DirectFableTitleBillboardAtlasHasAuthoritativeUpload(candidate),
      });
}

// IsExactSetTextureSampledTitleBillboardAtlasFetch/Snapshot/Resource deleted
// 2026-07-18 (migration step 3): the 0x821B6EE0-symbol binding-action scan
// they wrapped was census-dead (runs 538/539) so all three classifiers
// answered constant-false; both veto call sites removed with them.

void LogTitleTextureSlotDiagnosticLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer, uint32_t texture_slot_mask,
    double draw_record_ms, double title_sync_ms, double alias_ms) {
  if (!RendererDeepObserverHooksEnabled()) {
    return;
  }
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      (!command.draw.private_texture_rectangle &&
       !IsFableTitleSceneVertexShaderCommandLocked(command) &&
       !IsFableTitleSceneDrawTargetLocked(command, framebuffer))) {
    return;
  }
  if (draw_record_ms < 4.0 && title_sync_ms < 1.0 && alias_ms < 1.0) {
    return;
  }

  static uint32_t logged_title_texture_slot_diagnostics = 0;
  if (logged_title_texture_slot_diagnostics >= 96u) {
    return;
  }

  std::string slots;
  uint32_t used_slots = 0;
  uint32_t exact_slots = 0;
  uint32_t exact_producer_slots = 0;
  uint32_t exact_wrapper_slots = 0;
  uint32_t direct_slots = 0;
  uint32_t unresolved_slots = 0;
  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    if (!DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                   slot)) {
      continue;
    }
    ++used_slots;
    if (used_slots > 12u) {
      slots += " ...";
      break;
    }

    const uint32_t guest = command.resources.textures[slot];
    PlumeTrackedResource* texture =
        guest != 0 ? FindDirectResourceLocked(guest) : nullptr;
    const std::array<uint32_t, 6> command_fetch =
        DirectCommandTextureFetchDwords(command, slot);
    const DirectTextureFetchInfo command_fetch_info =
        DirectTextureFetchInfoFromDwords(command_fetch);
    const uint64_t command_fetch_key =
        FableDirectTextureFetchKey(command_fetch);
    const uint32_t texture_base =
        texture ? PlumeGuestTextureBaseAddress(*texture)
                : command_fetch_info.base_address;
    const uint32_t texture_width =
        texture ? PlumeGuestTextureWidth(*texture) : command_fetch_info.width;
    const uint32_t texture_height =
        texture ? PlumeGuestTextureHeight(*texture) : command_fetch_info.height;
    const uint32_t texture_format =
        texture ? PlumeGuestTextureGuestFormat(*texture)
                : command_fetch_info.format;
    const bool exact_bink_plane_slot =
        DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot);
    PlumeTrackedResource* registered_fetch_sample = nullptr;
    if (!exact_bink_plane_slot) {
      registered_fetch_sample =
          FindDirectReadyRegisteredTextureByFetchDwordsLocked(
              command_fetch,
              DirectRegisteredFetchPreferredGuestForCommandSlotLocked(
                  command, slot, command_fetch));
    }
    if (registered_fetch_sample &&
        !DirectTitleSyncFetchTextureMatchesCommandLocked(
            command, slot, *registered_fetch_sample)) {
      registered_fetch_sample = nullptr;
    }
    const bool exact_title_sync_sample =
        (texture && DirectTitleSyncFetchTextureMatchesCommandLocked(
                        command, slot, *texture)) ||
        registered_fetch_sample != nullptr;
    const bool exact_begin_vertices_title_owner_probe =
        command.draw.private_texture_wrapper_symbol_address ==
            kFableBeginVerticesTitleWrapperSymbol &&
        (command.draw.source == RendererDrawEventSource::kSceneObject ||
         command.draw.source == RendererDrawEventSource::kScratchWrapper) &&
        !command.draw.draw.indexed && command.draw.draw.primitive_type == 8u &&
        command.draw.draw.primitive_count == 3u;
    const bool run_expensive_exact_probe =
        RendererVerboseTracingEnabled() ||
        exact_begin_vertices_title_owner_probe;
    const bool exact_producer =
        !exact_bink_plane_slot &&
        DirectCommandTextureSlotExactProducerProvidesSampleLocked(command, slot,
                                                                  texture);
    PlumeTrackedResource* exact_wrapper_sample =
        !exact_bink_plane_slot
            ? ResolveDirectExactWrapperSourceSampleForSlotLocked(command, slot,
                                                                 texture)
            : nullptr;
    const bool exact_owner_probe =
        run_expensive_exact_probe &&
        DirectCommandTextureSlotHasExactProducerOwnerProbeLocked(command, slot,
                                                                 texture);
    const bool exact =
        exact_bink_plane_slot || exact_title_sync_sample || exact_producer ||
        (exact_wrapper_sample != nullptr) || exact_owner_probe;
    const bool direct = exact_bink_plane_slot ||
        (exact_wrapper_sample == nullptr &&
         DirectCommandTextureSlotDirectResourceProvidesSampleLocked(command, slot,
                                                                    texture));
    if (exact) {
      ++exact_slots;
      if (exact_producer) {
        ++exact_producer_slots;
      }
      if (exact_wrapper_sample) {
        ++exact_wrapper_slots;
      }
    } else if (direct) {
      ++direct_slots;
    } else {
      ++unresolved_slots;
    }

    const PlumeTrackedResource* diagnostic_sample =
        registered_fetch_sample
            ? registered_fetch_sample
            : (exact_wrapper_sample ? exact_wrapper_sample : texture);
    uint64_t diagnostic_content_serial = 0;
    if (diagnostic_sample) {
      diagnostic_content_serial =
          FableTrackedResourceVisibleContentSerial(*diagnostic_sample);
    }
    char slot_buffer[384];
    std::snprintf(
        slot_buffer, sizeof(slot_buffer),
        " s%u:g=0x%08X tr=0x%08X p=0x%08X base=0x%08X %ux%u fmt=%u "
        "fetch=%016llX ex=%u prod=%u wrap=%u dir=%u sample=0x%08X c=%llu",
        slot, guest, texture ? texture->guest_address : 0u,
        texture ? texture->parent_address : 0u, texture_base, texture_width,
        texture_height, texture_format,
        static_cast<unsigned long long>(command_fetch_key), exact ? 1u : 0u,
        exact_producer ? 1u : 0u, exact_wrapper_sample ? 1u : 0u,
        direct ? 1u : 0u,
        diagnostic_sample ? diagnostic_sample->guest_address : 0u,
        static_cast<unsigned long long>(diagnostic_content_serial));
    slots += slot_buffer;
  }

  if (used_slots == 0) {
    return;
  }

  REXLOG_INFO(
      "Refii plume title texture slot diagnostic #{} frame={} serial={} "
      "draw={} src={} mask=0x{:08X} used={} exact={} producer={} "
      "wrapper={} direct={} unresolved={} total_ms={:.3f} "
      "title_sync_ms={:.3f} alias_ms={:.3f} "
      "rt=0x{:08X}/0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
      "decl=0x{:08X} slots:{}",
      ++logged_title_texture_slot_diagnostics,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial),
      static_cast<uint32_t>(command.draw.source), texture_slot_mask, used_slots,
      exact_slots, exact_producer_slots, exact_wrapper_slots, direct_slots,
      unresolved_slots, draw_record_ms, title_sync_ms, alias_ms,
      command.resources.render_targets[0],
      framebuffer.color_address, command.resources.depth_stencil,
      command.resources.vertex_shader, command.resources.pixel_shader,
      command.resources.vertex_declaration, slots);
}

PlumeTrackedResource* FindDirectReadyRegisteredTextureByFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords, uint32_t preferred_guest) {
  if (!HasTrackedFetchDwords(dwords)) {
    return nullptr;
  }

  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(dwords);
  auto fetch_resource_dimensions_match =
      [&](const PlumeTrackedResource& resource) {
    if (!fetch_info.valid) {
      return true;
    }
    if (fetch_info.width != 0 &&
        PlumeGuestTextureWidth(resource) != fetch_info.width) {
      return false;
    }
    if (fetch_info.height != 0 &&
        PlumeGuestTextureHeight(resource) != fetch_info.height) {
      return false;
    }
    return true;
  };

  if (const std::vector<uint32_t>* objects =
          FableDirectFindTextureIdentityObjectsByFetch(dwords)) {
    PlumeTrackedResource* exact = nullptr;
    PlumeTrackedResource* preferred = nullptr;
    PlumeTrackedResource* newest_visible = nullptr;
    uint64_t newest_visible_serial = 0;
    bool newest_visible_unique = false;
    uint32_t object_match_count = 0;
    uint32_t resource_match_count = 0;
    auto matches_exact_resource = [](const PlumeTrackedResource& lhs,
                                     const PlumeTrackedResource& rhs) {
      if (lhs.guest_texture.texture && rhs.guest_texture.texture) {
        return lhs.guest_texture.texture.get() ==
               rhs.guest_texture.texture.get();
      }
      return lhs.guest_address != 0 && lhs.guest_address == rhs.guest_address;
    };
    for (uint32_t object : *objects) {
      const FableTextureIdentity* identity =
          FableDirectFindTextureIdentityByObject(object);
      if (!identity) {
        continue;
      }
      if (PlumeTrackedResource* resource =
              FindDirectResourceLocked(identity->object_texture)) {
        if (IsImageResource(resource->kind) && PlumeGuestImageReady(*resource) &&
            DirectMutablePrivateFetchCandidateHasContent(*resource) &&
            DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(*resource),
                                          dwords) &&
            fetch_resource_dimensions_match(*resource)) {
          ++object_match_count;
          if (preferred_guest != 0 &&
              (resource->guest_address == preferred_guest ||
               resource->parent_address == preferred_guest ||
               PlumeGuestTextureBaseAddress(*resource) == preferred_guest ||
               identity->object_texture == preferred_guest ||
               identity->parent_texture == preferred_guest ||
               identity->base_address == preferred_guest)) {
            preferred = resource;
          }
          if (!exact) {
            exact = resource;
            ++resource_match_count;
          } else if (!matches_exact_resource(*exact, *resource)) {
            ++resource_match_count;
          }
          const uint64_t visible_serial =
              FableTrackedResourceVisibleContentSerial(*resource);
          if (visible_serial != 0) {
            if (!newest_visible || visible_serial > newest_visible_serial) {
              newest_visible = resource;
              newest_visible_serial = visible_serial;
              newest_visible_unique = true;
            } else if (visible_serial == newest_visible_serial &&
                       !matches_exact_resource(*newest_visible, *resource)) {
              newest_visible_unique = false;
            }
          }
        }
      }
    }
    if (resource_match_count == 1u) {
      return exact;
    }
    if (resource_match_count > 1u) {
      if (preferred) {
        static uint32_t logged_exact_registered_fetch_bound_resolves = 0;
        if (logged_exact_registered_fetch_bound_resolves < 32u) {
          REXLOG_INFO(
              "Refii plume exact bound fetch texture identity #{} "
              "role=bound "
              "matches={} objects={} preferred=0x{:08X} "
              "tracked=0x{:08X} base=0x{:08X} size={}x{} "
              "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
              ++logged_exact_registered_fetch_bound_resolves,
              resource_match_count, object_match_count, preferred_guest,
              preferred->guest_address, PlumeGuestTextureBaseAddress(*preferred),
              PlumeGuestTextureWidth(*preferred),
              PlumeGuestTextureHeight(*preferred), dwords[0], dwords[1],
              dwords[2], dwords[3], dwords[4], dwords[5]);
        }
        return preferred;
      }
      if (newest_visible && newest_visible_unique) {
        static uint32_t logged_exact_registered_fetch_newest_resolves = 0;
        if (logged_exact_registered_fetch_newest_resolves < 32u) {
          REXLOG_INFO(
              "Refii plume exact registered fetch newest texture resolve #{} "
              "matches={} objects={} tracked=0x{:08X} base=0x{:08X} "
              "size={}x{} serial={} "
              "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
              ++logged_exact_registered_fetch_newest_resolves,
              resource_match_count, object_match_count,
              newest_visible->guest_address,
              PlumeGuestTextureBaseAddress(*newest_visible),
              PlumeGuestTextureWidth(*newest_visible),
              PlumeGuestTextureHeight(*newest_visible),
              static_cast<unsigned long long>(newest_visible_serial),
              dwords[0], dwords[1], dwords[2], dwords[3], dwords[4],
              dwords[5]);
        }
        return newest_visible;
      }
      if (exact) {
        static uint32_t logged_exact_registered_fetch_first_resolves = 0;
        if (logged_exact_registered_fetch_first_resolves < 32u) {
          const uint64_t visible_serial =
              FableTrackedResourceVisibleContentSerial(*exact);
          REXLOG_INFO(
              "Refii plume exact registered fetch first texture resolve #{} "
              "matches={} objects={} tracked=0x{:08X} base=0x{:08X} "
              "size={}x{} serial={} "
              "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} "
              "preferred=0x{:08X}",
              ++logged_exact_registered_fetch_first_resolves,
              resource_match_count, object_match_count,
              exact->guest_address, PlumeGuestTextureBaseAddress(*exact),
              PlumeGuestTextureWidth(*exact), PlumeGuestTextureHeight(*exact),
              static_cast<unsigned long long>(visible_serial),
              dwords[0], dwords[1], dwords[2], dwords[3], dwords[4], dwords[5],
              preferred_guest);
        }
        return exact;
      }
    }
  }

  return nullptr;
}

uint32_t DirectRegisteredFetchPreferredGuestForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const std::array<uint32_t, 6>& dwords) {
  if (command.draw.source == RendererDrawEventSource::kPixVideo ||
      DirectCommandIsExactBinkIndexedWrapperDrawLocked(command)) {
    return 0;
  }

  if (slot < command.resources.textures.size()) {
    const uint32_t command_guest = command.resources.textures[slot];
    if (command_guest != 0) {
      return command_guest;
    }
  }

  // Census-dead (runs 538/539): the binding-action fallback that lived here
  // never hit — the command's own texture slot above is the answer or there
  // is none. Deleted per migration step 3.
  (void)dwords;
  return 0;
}

PlumeTrackedResource* ResolveFableTitleUploadedIdentityTextureLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* skip_texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo ||
      (!IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) &&
       !IsFableTitleSceneVertexShaderCommandLocked(command))) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const bool base_only_title_fetch =
      guest == 0u && FableTitleGuiFetchBackedTextureSlotLocked(command) == slot &&
      command_fetch_info.valid &&
      IsFableTitleUploadedTextureIdentityBase(command_fetch_info.base_address,
                                             command_fetch_info.width,
                                             command_fetch_info.height);
  const FableTextureIdentity* title_identity =
      FableDirectFindTextureIdentityByObject(guest);
  if (!base_only_title_fetch &&
      !IsFableTitleUploadedTextureIdentity(title_identity)) {
    return nullptr;
  }

  const uint32_t title_base = title_identity ? title_identity->base_address
                                             : command_fetch_info.base_address;
  const uint32_t title_width =
      title_identity ? title_identity->width : command_fetch_info.width;
  const uint32_t title_height =
      title_identity ? title_identity->height : command_fetch_info.height;
  const uint32_t title_format =
      title_identity ? title_identity->format : command_fetch_info.format;

  auto valid_uploaded_title_resource = [&](PlumeTrackedResource* candidate) {
    if (!candidate || candidate == skip_texture ||
        !IsImageResource(candidate->kind) || !PlumeGuestImageReady(*candidate) ||
        !DirectExactTitleUploadedResourceHasAuthoredPayload(*candidate) ||
        candidate->texture_render_target || candidate->texture_depth_target ||
        DirectCommandBindsResourceAsTargetLocked(command, *candidate)) {
      return false;
    }
    if (PlumeGuestTextureBaseAddress(*candidate) != title_base ||
        PlumeGuestTextureWidth(*candidate) != title_width ||
        PlumeGuestTextureHeight(*candidate) != title_height) {
      return false;
    }
    const uint32_t candidate_guest_format =
        PlumeGuestTextureGuestFormat(*candidate);
    const uint32_t candidate_render_format =
        static_cast<uint32_t>(PlumeGuestTextureFormat(*candidate));
    return title_format == 0 || candidate_guest_format == title_format ||
           candidate_render_format == title_format;
  };

  if (!title_identity || base_only_title_fetch) {
    return nullptr;
  }
  PlumeTrackedResource* exact =
      FindDirectResourceLocked(title_identity->object_texture);
  if (!valid_uploaded_title_resource(exact)) {
    exact = nullptr;
  }
  if (!exact && title_identity->parent_texture != 0 &&
      title_identity->parent_texture != title_identity->object_texture) {
    exact = FindDirectResourceLocked(title_identity->parent_texture);
    if (!valid_uploaded_title_resource(exact)) {
      exact = nullptr;
    }
  }
  if (!exact) {
    return nullptr;
  }

  static uint32_t logged_title_uploaded_identity_resolves = 0;
  if (logged_title_uploaded_identity_resolves < 32u) {
    REXLOG_INFO(
        "Refii plume title uploaded identity resolve candidate #{} frame={} slot={} "
        "guest=0x{:08X} alias=0x{:08X} base=0x{:08X} size={}x{} fmt={} "
        "gen={} hash=0x{:016X} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        ++logged_title_uploaded_identity_resolves,
        g_direct_renderer.current_frame_index, slot, guest, exact->guest_address,
        PlumeGuestTextureBaseAddress(*exact), PlumeGuestTextureWidth(*exact),
        PlumeGuestTextureHeight(*exact), PlumeGuestTextureGuestFormat(*exact),
        static_cast<unsigned long long>(exact->upload_generation),
        static_cast<unsigned long long>(exact->texture_upload_hash),
        command.resources.render_targets[0], command.resources.vertex_shader,
        command.resources.pixel_shader, command_fetch[0], command_fetch[1],
        command_fetch[2], command_fetch[3], command_fetch[4], command_fetch[5]);
  }
  return exact;
}

PlumeTrackedResource* ResolveDirectSampledTextureFromFetchConstantsLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
    return nullptr;
  }

  const std::array<uint32_t, 6> dwords =
      DirectCommandTextureFetchDwords(command, slot);
  if (PlumeTrackedResource* registered_texture =
          FindDirectReadyRegisteredTextureByFetchDwordsLocked(
              dwords,
              DirectRegisteredFetchPreferredGuestForCommandSlotLocked(
                  command, slot, dwords))) {
    static uint32_t logged_registered_fetch_texture_resolves = 0;
    if (logged_registered_fetch_texture_resolves < 32u) {
      REXLOG_INFO(
          "Refii plume exact fetch texture identity #{} "
          "frame={} serial={} draw={} slot={} "
          "guest=0x{:08X} tracked=0x{:08X} base=0x{:08X} "
          "size={}x{} fmt={} hash=0x{:016X} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          logged_registered_fetch_texture_resolves + 1u,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot,
          command.resources.textures[slot], registered_texture->guest_address,
          PlumeGuestTextureBaseAddress(*registered_texture),
          PlumeGuestTextureWidth(*registered_texture),
          PlumeGuestTextureHeight(*registered_texture),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*registered_texture)),
          static_cast<unsigned long long>(
              registered_texture->texture_upload_hash),
          dwords[0], dwords[1], dwords[2], dwords[3], dwords[4], dwords[5]);
      ++logged_registered_fetch_texture_resolves;
    }
    return registered_texture;
  }

  // Fetch constants are the canonical texture identity: the game rebinds a
  // shared texture header object per draw, so the header address can resolve
  // to nothing while the fetch constant names a tracked texture by its base
  // address (e.g. the title water/glyph textures streamed via TileSurface).
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(dwords);
  if (fetch_info.valid && fetch_info.base_address != 0) {
    PlumeTrackedResource* by_base =
        FindDirectResourceLocked(fetch_info.base_address);
    if (by_base && IsImageResource(by_base->kind) &&
        PlumeGuestImageReady(*by_base) &&
        !DirectCommandBindsResourceAsTargetLocked(command, *by_base) &&
        !FableTrackedResourceIsBlankUploadedSampleProxy(*by_base) &&
        PlumeGuestTextureWidth(*by_base) == fetch_info.width &&
        PlumeGuestTextureHeight(*by_base) == fetch_info.height) {
      static uint32_t logged_fetch_base_texture_resolves = 0;
      if (logged_fetch_base_texture_resolves < 32u) {
        REXLOG_INFO(
            "Refii plume fetch base texture identity #{} frame={} serial={} "
            "draw={} slot={} guest=0x{:08X} tracked=0x{:08X} base=0x{:08X} "
            "size={}x{} fmt={} gen={} "
            "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
            ++logged_fetch_base_texture_resolves,
            g_direct_renderer.current_frame_index,
            static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial), slot,
            command.resources.textures[slot], by_base->guest_address,
            PlumeGuestTextureBaseAddress(*by_base),
            PlumeGuestTextureWidth(*by_base),
            PlumeGuestTextureHeight(*by_base),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*by_base)),
            static_cast<unsigned long long>(by_base->upload_generation),
            dwords[0], dwords[1], dwords[2], dwords[3], dwords[4], dwords[5]);
      }
      return by_base;
    }
  }

  return nullptr;
}

bool DirectTitleSyncFetchTextureMatchesCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& texture) {
  if (!IsFableTitleSceneTextureSyncCommandLocked(command) ||
      DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot) ||
      slot >= command.resources.textures.size() ||
      !IsImageResource(texture.kind) || !PlumeGuestImageReady(texture) ||
      !DirectMutablePrivateFetchCandidateHasContent(texture) ||
      DirectCommandBindsResourceAsTargetLocked(command, texture)) {
    return false;
  }

  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  return command_fetch_info.valid &&
         DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(texture),
                                       command_fetch) &&
         PlumeGuestTextureBaseAddress(texture) ==
             command_fetch_info.base_address &&
         PlumeGuestTextureWidth(texture) == command_fetch_info.width &&
         PlumeGuestTextureHeight(texture) == command_fetch_info.height;
}

void LogDirectTitleSyncFetchTextureIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& texture, const char* message) {
  static uint32_t logged_exact_title_sync_fetch_texture_identities = 0;
  if (logged_exact_title_sync_fetch_texture_identities >= 32u) {
    return;
  }
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  REXLOG_INFO(
      "{} #{} frame={} serial={} draw={} slot={} guest=0x{:08X} "
      "tracked=0x{:08X} base=0x{:08X} {}x{} fmt={} gen={} content={} "
      "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
      "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
      message, ++logged_exact_title_sync_fetch_texture_identities,
      g_direct_renderer.current_frame_index,
      static_cast<unsigned long long>(command.serial),
      static_cast<unsigned long long>(command.draw_serial), slot,
      command.resources.textures[slot], texture.guest_address,
      PlumeGuestTextureBaseAddress(texture), PlumeGuestTextureWidth(texture),
      PlumeGuestTextureHeight(texture), PlumeGuestTextureGuestFormat(texture),
      static_cast<unsigned long long>(texture.upload_generation),
      static_cast<unsigned long long>(FableTrackedResourceVisibleContentSerial(texture)),
      command.resources.render_targets[0], command.resources.depth_stencil,
      command.resources.vertex_shader, command.resources.pixel_shader,
      command_fetch[0], command_fetch[1], command_fetch[2], command_fetch[3],
      command_fetch[4], command_fetch[5]);
}

bool DirectCommandSlotUsesMutablePrivateHeaderExactSamplePathLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (guest == 0 || !fetch_info.valid) {
    return false;
  }

  const bool title_or_private_command =
      command.draw.private_texture_rectangle ||
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneVertexShaderCommandLocked(command) ||
      IsFableTitleSceneTextureSyncCommandLocked(command);
  if (!title_or_private_command) {
    return false;
  }

  return IsFableTitlePrivateTextureProxyAddress(guest) ||
         IsFableMutablePrivateTextureHeaderAddress(guest) ||
         IsFableTitlePrivateTextureProxyAddress(fetch_info.base_address) ||
         IsFableMutablePrivateTextureHeaderAddress(fetch_info.base_address) ||
         (texture &&
          (IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
           IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
           IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
           IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
           IsFableTitlePrivateTextureProxyAddress(
               PlumeGuestTextureBaseAddress(*texture)) ||
           IsFableMutablePrivateTextureHeaderAddress(
               PlumeGuestTextureBaseAddress(*texture)) ||
           DirectPrivateTextureHeaderNeedsAlias(command, slot, texture)));
}

uint64_t DirectMutablePrivateHeaderExactSampleKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    uint32_t source_texture, uint32_t source_base,
    const std::array<uint32_t, 6>& source_fetch_dwords) {
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(source_fetch_dwords);
  const uint64_t fetch_key = FableDirectTextureFetchKey(source_fetch_dwords);
  size_t seed = 0;
  DirectHashCombine(seed, std::hash<uint32_t>{}(slot));
  DirectHashCombine(seed, std::hash<uint32_t>{}(source_texture));
  DirectHashCombine(seed, std::hash<uint32_t>{}(
                        source_base != 0 ? source_base
                                         : fetch_info.base_address));
  DirectHashCombine(seed, std::hash<uint32_t>{}(fetch_info.width));
  DirectHashCombine(seed, std::hash<uint32_t>{}(fetch_info.height));
  DirectHashCombine(seed, std::hash<uint32_t>{}(fetch_info.format));
  DirectHashCombine(seed, std::hash<uint64_t>{}(fetch_key));
  DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.render_targets[0]));
  DirectHashCombine(seed, std::hash<uint32_t>{}(command.resources.depth_stencil));
  DirectHashCombine(seed, std::hash<uint32_t>{}(
                        static_cast<uint32_t>(command.draw.source)));
  return seed != 0 ? static_cast<uint64_t>(seed) : 1ull;
}

bool DirectPrivateTextureCopyActionSourceMatchesCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FablePrivateTextureCopyAction& action) {
  if (slot >= command.resources.textures.size()) {
    return false;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const uint64_t command_fetch_key = FableDirectTextureFetchKey(command_fetch);
  if (action.source_texture != 0 && action.source_texture == guest) {
    return true;
  }
  if (action.backing_texture != 0 && action.backing_texture == guest) {
    return true;
  }
  if (fetch_info.valid &&
      ((action.source_base_address != 0 &&
        action.source_base_address == fetch_info.base_address) ||
       (action.backing_base_address != 0 &&
        action.backing_base_address == fetch_info.base_address))) {
    return true;
  }
  return command_fetch_key != 0 &&
         (FableDirectTextureFetchKey(action.source_fetch_dwords) ==
              command_fetch_key ||
          FableDirectTextureFetchKey(action.backing_fetch_dwords) ==
              command_fetch_key);
}

void RememberDirectMutablePrivateHeaderExactSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& sample, const char* reason) {
  if (slot >= command.resources.textures.size()) {
    return;
  }
  const PlumeTrackedResource* bound_texture =
      FindDirectResourceLocked(command.resources.textures[slot]);
  if (!DirectCommandSlotUsesMutablePrivateHeaderExactSamplePathLocked(
          command, slot, bound_texture) ||
      action.kind == FablePrivateTextureCopyKind::kDepth ||
      !DirectPrivateTextureCopyActionSourceMatchesCommandSlotLocked(command, slot,
                                                                    action) ||
      sample.guest_address == 0 || !IsImageResource(sample.kind) ||
      !PlumeGuestImageReady(sample) ||
      DirectResourceIsPrivateFeedbackAlias(sample) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(sample) ||
      (!FableTrackedResourceHasSampleableContent(sample) &&
       !FableTrackedResourceHasGpuProducedContent(sample)) ||
      DirectCommandBindsResourceAsTargetLocked(command, sample)) {
    return;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const uint64_t cache_key = DirectMutablePrivateHeaderExactSampleKeyLocked(
      command, slot, guest, fetch_info.base_address, command_fetch);
  constexpr size_t kMaxMutablePrivateHeaderExactSamples = 4096u;
  if (g_direct_renderer.mutable_private_header_exact_samples.size() >=
      kMaxMutablePrivateHeaderExactSamples) {
    static uint32_t logged_mutable_header_exact_sample_cache_clears = 0;
    if (logged_mutable_header_exact_sample_cache_clears < 8u) {
      REXLOG_INFO(
          "Refii plume mutable private header exact sample cache clear #{} "
          "frame={} size={} slot={} guest=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_mutable_header_exact_sample_cache_clears,
          g_direct_renderer.current_frame_index,
          g_direct_renderer.mutable_private_header_exact_samples.size(), slot,
          guest, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    g_direct_renderer.mutable_private_header_exact_samples.clear();
  }
  g_direct_renderer.mutable_private_header_exact_samples[cache_key] = {
      sample.guest_address,
      g_direct_renderer.current_frame_index,
      command.draw_serial != 0 ? command.draw_serial : command.serial,
      FableTrackedResourceVisibleContentSerial(sample),
      DirectPrivateTextureCopySerialLocked()};

  static uint32_t logged_mutable_header_exact_sample_actions = 0;
  if (RendererVerboseTracingEnabled() &&
      logged_mutable_header_exact_sample_actions < 48u) {
    REXLOG_INFO(
        "Refii plume exact mutable private header action registration #{} "
        "frame={} reason={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
        "content={} action_source=0x{:08X} action_base=0x{:08X} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_mutable_header_exact_sample_actions,
        g_direct_renderer.current_frame_index, reason ? reason : "exact",
        slot, guest, sample.guest_address, PlumeGuestTextureWidth(sample),
        PlumeGuestTextureHeight(sample),
        static_cast<unsigned long long>(FableTrackedResourceVisibleContentSerial(sample)),
        action.source_texture, action.source_base_address,
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
}

PlumeTrackedResource* ResolveDirectMutablePrivateHeaderExactSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  if (!DirectCommandSlotUsesMutablePrivateHeaderExactSamplePathLocked(
          command, slot, texture)) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  const uint64_t cache_key = DirectMutablePrivateHeaderExactSampleKeyLocked(
      command, slot, guest, fetch_info.base_address, command_fetch);
  auto cached_it =
      g_direct_renderer.mutable_private_header_exact_samples.find(cache_key);
  if (cached_it ==
      g_direct_renderer.mutable_private_header_exact_samples.end()) {
    return nullptr;
  }

  const DirectMutablePrivateHeaderExactSampleEntry& entry = cached_it->second;
  // Exact mutable-header samples are invalidated by the resource/content checks
  // below; a frame-age cutoff makes the same identity intermittently unresolved.
  PlumeTrackedResource* sample = FindDirectResourceLocked(entry.sample_address);
  if (!sample || !IsImageResource(sample->kind) ||
      !PlumeGuestImageReady(*sample) ||
      DirectResourceIsPrivateFeedbackAlias(*sample) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
      FableTrackedResourceVisibleContentSerial(*sample) < entry.sample_content_serial ||
      (!FableTrackedResourceHasSampleableContent(*sample) &&
       !FableTrackedResourceHasGpuProducedContent(*sample)) ||
      DirectCommandBindsResourceAsTargetLocked(command, *sample)) {
    g_direct_renderer.mutable_private_header_exact_samples.erase(cached_it);
    return nullptr;
  }

  static uint32_t logged_mutable_header_exact_samples = 0;
  if (RendererVerboseTracingEnabled() &&
      logged_mutable_header_exact_samples < 48u) {
    REXLOG_INFO(
        "Refii plume exact mutable private header action sample #{} "
        "frame={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
        "registered_frame={} registered_draw={} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_mutable_header_exact_samples,
        g_direct_renderer.current_frame_index, slot, guest,
        sample->guest_address, PlumeGuestTextureWidth(*sample),
        PlumeGuestTextureHeight(*sample), entry.frame_index,
        static_cast<unsigned long long>(entry.draw_serial),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader);
  }
  return sample;
}

PlumeTrackedResource* ResolveDirectMutablePrivateHeaderSampleByIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture,
    bool require_exact_surface_link) {
  if (slot >= command.resources.textures.size() ||
      command.draw.source == RendererDrawEventSource::kPixVideo) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  if (guest == 0 || !fetch_info.valid) {
    return nullptr;
  }

  const bool title_or_private_command =
      command.draw.private_texture_rectangle ||
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneVertexShaderCommandLocked(command) ||
      IsFableTitleSceneTextureSyncCommandLocked(command);
  if (!title_or_private_command) {
    return nullptr;
  }

  const bool mutable_header_slot =
      IsFableTitlePrivateTextureProxyAddress(guest) ||
      IsFableMutablePrivateTextureHeaderAddress(guest) ||
      IsFableTitlePrivateTextureProxyAddress(fetch_info.base_address) ||
      IsFableMutablePrivateTextureHeaderAddress(fetch_info.base_address) ||
      (direct_texture &&
       (IsFableTitlePrivateTextureProxyAddress(direct_texture->guest_address) ||
        IsFableMutablePrivateTextureHeaderAddress(
            direct_texture->guest_address) ||
        IsFableTitlePrivateTextureProxyAddress(direct_texture->parent_address) ||
        IsFableMutablePrivateTextureHeaderAddress(
            direct_texture->parent_address) ||
        IsFableTitlePrivateTextureProxyAddress(
            PlumeGuestTextureBaseAddress(*direct_texture)) ||
        IsFableMutablePrivateTextureHeaderAddress(
            PlumeGuestTextureBaseAddress(*direct_texture)) ||
        DirectPrivateTextureHeaderNeedsAlias(command, slot, direct_texture)));
  if (!mutable_header_slot) {
    return nullptr;
  }

  auto link_visible_to_command =
      [&](const FableSurfaceTextureLink& link) {
    if (!link.trusted_destination_edge) {
      return false;
    }
    const uint64_t draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    if (link.frame_index != 0 &&
        link.frame_index > g_direct_renderer.current_frame_index) {
      return false;
    }
    if (link.frame_index != 0 &&
        link.frame_index == g_direct_renderer.current_frame_index &&
        link.draw_serial != 0 && draw_serial != 0 &&
        link.draw_serial > draw_serial) {
      return false;
    }
    return DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                       link);
  };
  auto remember_exact_link_sample =
      [&](const FableSurfaceTextureLink& link, PlumeTrackedResource& sample,
          const char* reason) {

    FablePrivateTextureCopyAction action;
    action.source_slot = slot;
    action.source_texture = guest;
    action.source_base_address = fetch_info.base_address;
    action.source_width = fetch_info.width;
    action.source_height = fetch_info.height;
    action.source_format = fetch_info.format;
    action.source_fetch_dwords = command_fetch;
    PopulateDirectPrivateTextureCopyTargetLocked(action, sample);
    action.backing_texture = link.backing_texture != 0 ? link.backing_texture
                                                       : link.texture;
    action.backing_base_address = link.backing_base_address != 0
                                      ? link.backing_base_address
                                      : link.texture_base_address;
    action.backing_width = link.texture_width;
    action.backing_height = link.texture_height;
    action.backing_format = link.texture_format;
    action.backing_fetch_dwords = link.backing_texture != 0
                                      ? link.backing_fetch_dwords
                                      : link.texture_fetch_dwords;
    action.backing_content_serial =
        link.backing_content_serial != 0 ? link.backing_content_serial
                                         : link.target_content_serial;
    action.render_target = command.resources.render_targets[0];
    action.depth_stencil = command.resources.depth_stencil;
    action.vertex_shader = command.resources.vertex_shader;
    action.pixel_shader = command.resources.pixel_shader;
    PopulateDirectPrivateTextureCopyShaderRoots(action, command);
    action.lr = command.draw.state_flush_lr;
    action.wrapper_call_serial =
        command.draw.private_texture_wrapper_call_serial != 0
            ? command.draw.private_texture_wrapper_call_serial
            : link.copy_call_serial;
    action.wrapper_context_key =
        command.draw.private_texture_wrapper_context_key != 0
            ? command.draw.private_texture_wrapper_context_key
            : link.copy_context_key;
    action.wrapper_symbol_address =
        command.draw.private_texture_wrapper_symbol_address != 0
            ? command.draw.private_texture_wrapper_symbol_address
            : link.copy_symbol_address;
    PopulateDirectPrivateTextureWrapperSourceIdentity(action, command, &link);
    action.frame_index = g_direct_renderer.current_frame_index;
    action.draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    action.kind = link.kind != FablePrivateTextureCopyKind::kDepth
                      ? link.kind
                      : FablePrivateTextureCopyKind::kColor;
    action.sampled_source = link.sampled_source;
    if (action.sampled_source.kind ==
        FableRegisteredSampleSourceKind::kUnknown) {
      action.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
      action.sampled_source.resource = sample.guest_address;
      action.sampled_source.content_serial =
          FableTrackedResourceVisibleContentSerial(sample);
    }

    RegisterDirectPrivateTextureCopyActionLocked(action);
    RememberDirectPrivateRectExactActionLocked(action);
    RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
        action, reason ? reason : "mutable-private-header-destination-link");
    TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
        action, reason ? reason : "mutable-private-header-destination-link");
    RememberDirectPrivateTextureAliasIndexLocked(
        action.source_texture, action.source_base_address,
        action.source_fetch_dwords, sample);
    RememberDirectMutablePrivateHeaderExactSampleLocked(
        command, slot, action, sample,
        reason ? reason : "mutable-private-header-destination-link");
  };
  auto resolve_exact_destination_link_sample =
      [&](const FableSurfaceTextureLink* link,
          const char* reason) -> PlumeTrackedResource* {
    if (!link || !link_visible_to_command(*link) ||
        !DirectSurfaceTextureLinkHasExactCopyIdentity(*link) ||
        !DirectPrivateTextureCopyKindUsesColorBacking(link->kind)) {
      return nullptr;
    }
    constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
    if (link->source_slot != kUnknownSlot && link->source_slot != slot &&
        !DirectSurfaceTextureLinkTouchesFablePrivateChain(*link,
                                                          direct_texture)) {
      return nullptr;
    }
    PlumeTrackedResource* sample =
        ResolveDirectSurfaceTextureLinkResourceLocked(command, *link);
    if (!sample || !IsImageResource(sample->kind) ||
        !PlumeGuestImageReady(*sample) ||
        DirectResourceIsPrivateFeedbackAlias(*sample) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
        DirectCommandBindsResourceAsTargetLocked(command, *sample) ||
        !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*sample)) {
      return nullptr;
    }
    remember_exact_link_sample(*link, *sample, reason);
    static uint32_t logged_mutable_header_destination_link_samples = 0;
    if (logged_mutable_header_destination_link_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact mutable private header destination-link sample #{} "
          "frame={} reason={} slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} "
          "link_tex=0x{:08X} link_base=0x{:08X} link_surface=0x{:08X} "
          "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_mutable_header_destination_link_samples,
          g_direct_renderer.current_frame_index, reason ? reason : "link",
          slot, guest, sample->guest_address, PlumeGuestTextureWidth(*sample),
          PlumeGuestTextureHeight(*sample), link->texture,
          link->texture_base_address, link->source_surface,
          static_cast<unsigned long long>(link->copy_call_serial),
          static_cast<unsigned long long>(link->copy_context_key),
          link->copy_symbol_address, command.resources.render_targets[0],
          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return sample;
  };
  auto resolve_exact_destination =
      [&](uint32_t destination,
          const char* reason) -> PlumeTrackedResource* {
    if (destination == 0) {
      return nullptr;
    }
    const uint64_t draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    if (PlumeTrackedResource* sample =
            resolve_exact_destination_link_sample(
                FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
                    destination, g_direct_renderer.current_frame_index,
                    draw_serial, slot),
                reason)) {
      return sample;
    }
    if (PlumeTrackedResource* sample =
            resolve_exact_destination_link_sample(
                FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
                    destination, slot),
                reason)) {
      return sample;
    }
    return resolve_exact_destination_link_sample(
        FableDirectFindSurfaceTextureLinkForDestinationTexture(destination),
        reason);
  };
  if (require_exact_surface_link) {
    std::array<std::pair<uint32_t, const char*>, 16> destinations = {};
    uint32_t destination_count = 0;
    auto add_destination = [&](uint32_t destination, const char* reason) {
      if (destination == 0) {
        return;
      }
      for (uint32_t i = 0; i < destination_count; ++i) {
        if (destinations[i].first == destination) {
          return;
        }
      }
      if (destination_count < destinations.size()) {
        destinations[destination_count++] = {destination, reason};
      }
    };
    add_destination(guest, "guest-destination");
    add_destination(fetch_info.base_address, "command-base-destination");
    if (direct_texture) {
      add_destination(direct_texture->guest_address, "tracked-destination");
      add_destination(direct_texture->parent_address, "tracked-parent-destination");
      add_destination(PlumeGuestTextureBaseAddress(*direct_texture),
                      "tracked-base-destination");
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByObject(guest)) {
      add_destination(identity->object_texture, "identity-object-destination");
      add_destination(identity->parent_texture, "identity-parent-destination");
      add_destination(identity->base_address, "identity-base-destination");
    }
    if (const FableTextureIdentity* identity =
            FableDirectFindTextureIdentityByBase(fetch_info.base_address)) {
      add_destination(identity->object_texture,
                      "command-base-object-destination");
      add_destination(identity->parent_texture,
                      "command-base-parent-destination");
      add_destination(identity->base_address, "command-base-destination");
    }
    if (HasTrackedFetchDwords(command_fetch)) {
      if (const FableTextureIdentity* identity =
              FableDirectFindTextureIdentityByFetch(command_fetch)) {
        add_destination(identity->object_texture,
                        "command-fetch-object-destination");
        add_destination(identity->parent_texture,
                        "command-fetch-parent-destination");
        add_destination(identity->base_address, "command-fetch-base-destination");
      }
    }
    for (uint32_t i = 0; i < destination_count; ++i) {
      if (PlumeTrackedResource* sample =
              resolve_exact_destination(destinations[i].first,
                                        destinations[i].second)) {
        return sample;
      }
    }
  }
  struct PendingMutableHeaderAmbiguity {
    bool valid = false;
    const char* reason = nullptr;
    uint32_t match_count = 0;
    uint64_t command_serial = 0;
    uint64_t draw_serial = 0;
    uint32_t slot = 0;
    uint32_t guest = 0;
    uint32_t sample = 0;
    uint32_t sample_width = 0;
    uint32_t sample_height = 0;
    uint32_t sample_format = 0;
    uint32_t link_texture = 0;
    uint32_t link_base = 0;
    uint32_t link_surface = 0;
    uint64_t target_content_serial = 0;
    uint64_t backing_content_serial = 0;
    uint64_t sample_content_serial = 0;
    uint32_t sampled_kind = 0;
    uint32_t sampled_resource = 0;
    uint64_t sampled_content_serial = 0;
    uint32_t link_wrapper_source = 0;
    bool link_wrapper_source_exact = false;
    uint32_t command_wrapper_source = 0;
    bool command_wrapper_source_exact = false;
    uint32_t render_target = 0;
    uint32_t depth_stencil = 0;
    uint32_t vertex_shader = 0;
    uint32_t pixel_shader = 0;
  };
  PendingMutableHeaderAmbiguity pending_mutable_header_ambiguity;
  auto remember_pending_mutable_header_ambiguity =
      [&](const char* reason, uint32_t match_count,
          const FableSurfaceTextureLink& link,
          const PlumeTrackedResource& sample) {
    pending_mutable_header_ambiguity.valid = true;
    pending_mutable_header_ambiguity.reason = reason;
    pending_mutable_header_ambiguity.match_count = match_count;
    pending_mutable_header_ambiguity.command_serial = command.serial;
    pending_mutable_header_ambiguity.draw_serial = command.draw_serial;
    pending_mutable_header_ambiguity.slot = slot;
    pending_mutable_header_ambiguity.guest = guest;
    pending_mutable_header_ambiguity.sample = sample.guest_address;
    pending_mutable_header_ambiguity.sample_width =
        PlumeGuestTextureWidth(sample);
    pending_mutable_header_ambiguity.sample_height =
        PlumeGuestTextureHeight(sample);
    pending_mutable_header_ambiguity.sample_format =
        static_cast<uint32_t>(PlumeGuestTextureFormat(sample));
    pending_mutable_header_ambiguity.link_texture = link.texture;
    pending_mutable_header_ambiguity.link_base = link.texture_base_address;
    pending_mutable_header_ambiguity.link_surface = link.source_surface;
    pending_mutable_header_ambiguity.target_content_serial =
        link.target_content_serial;
    pending_mutable_header_ambiguity.backing_content_serial =
        link.backing_content_serial;
    pending_mutable_header_ambiguity.sample_content_serial =
        FableTrackedResourceVisibleContentSerial(sample);
    pending_mutable_header_ambiguity.sampled_kind =
        static_cast<uint32_t>(link.sampled_source.kind);
    pending_mutable_header_ambiguity.sampled_resource =
        link.sampled_source.resource;
    pending_mutable_header_ambiguity.sampled_content_serial =
        link.sampled_source.content_serial;
    pending_mutable_header_ambiguity.link_wrapper_source =
        link.wrapper_source_texture;
    pending_mutable_header_ambiguity.link_wrapper_source_exact =
        link.wrapper_source_exact;
    pending_mutable_header_ambiguity.command_wrapper_source =
        command.draw.private_texture_wrapper_source_texture;
    pending_mutable_header_ambiguity.command_wrapper_source_exact =
        command.draw.private_texture_wrapper_source_exact;
    pending_mutable_header_ambiguity.render_target =
        command.resources.render_targets[0];
    pending_mutable_header_ambiguity.depth_stencil =
        command.resources.depth_stencil;
    pending_mutable_header_ambiguity.vertex_shader =
        command.resources.vertex_shader;
    pending_mutable_header_ambiguity.pixel_shader =
        command.resources.pixel_shader;
  };
  auto log_pending_mutable_header_ambiguity = [&]() {
    if (!pending_mutable_header_ambiguity.valid) {
      return;
    }
    static uint32_t logged_exact_mutable_header_surface_link_ambiguity_rejects =
        0;
    if (logged_exact_mutable_header_surface_link_ambiguity_rejects < 32u) {
      const PendingMutableHeaderAmbiguity& pending =
          pending_mutable_header_ambiguity;
      REXLOG_INFO(
          "Refii plume exact mutable private header surface-link ambiguity "
          "reject #{} frame={} reason={} matches={} serial={} draw={} "
          "slot={} guest=0x{:08X} sample=0x{:08X}/{}x{} fmt={} "
          "link_tex=0x{:08X} link_base=0x{:08X} "
          "link_surface=0x{:08X} target_serial={} backing_serial={} "
          "sample_serial={} sampled={} sample_resource=0x{:08X} "
          "sampled_serial={} link_wrapper_source=0x{:08X}/{} "
          "command_wrapper_source=0x{:08X}/{} rt=0x{:08X} ds=0x{:08X} "
          "vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_mutable_header_surface_link_ambiguity_rejects,
          g_direct_renderer.current_frame_index,
          pending.reason ? pending.reason : "link", pending.match_count,
          static_cast<unsigned long long>(pending.command_serial),
          static_cast<unsigned long long>(pending.draw_serial), pending.slot,
          pending.guest, pending.sample, pending.sample_width,
          pending.sample_height, pending.sample_format, pending.link_texture,
          pending.link_base, pending.link_surface,
          static_cast<unsigned long long>(pending.target_content_serial),
          static_cast<unsigned long long>(pending.backing_content_serial),
          static_cast<unsigned long long>(pending.sample_content_serial),
          pending.sampled_kind, pending.sampled_resource,
          static_cast<unsigned long long>(pending.sampled_content_serial),
          pending.link_wrapper_source,
          pending.link_wrapper_source_exact ? 1 : 0,
          pending.command_wrapper_source,
          pending.command_wrapper_source_exact ? 1 : 0,
          pending.render_target, pending.depth_stencil, pending.vertex_shader,
          pending.pixel_shader);
    }
    pending_mutable_header_ambiguity.valid = false;
  };

  auto try_fetch_or_base_links =
      [&](const std::vector<FableSurfaceTextureLink>* links,
          const char* reason) -> PlumeTrackedResource* {
    if (!links || links->empty()) {
      return nullptr;
    }
    const FableSurfaceTextureLink* exact_link = nullptr;
    PlumeTrackedResource* exact_sample = nullptr;
    uint32_t match_count = 0;
    uint64_t best_score = 0;
    uint32_t unique_sample_address = 0;
    uint64_t unique_sample_content_serial = 0;
    bool has_distinct_sample = false;
    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    auto same_link_identity = [](const FableSurfaceTextureLink& a,
                                 const FableSurfaceTextureLink& b) {
      return a.source_surface == b.source_surface && a.texture == b.texture &&
             a.texture_base_address == b.texture_base_address &&
             a.backing_texture == b.backing_texture &&
             a.backing_base_address == b.backing_base_address &&
             a.target_content_serial == b.target_content_serial &&
             a.backing_content_serial == b.backing_content_serial &&
             a.wrapper_source_texture == b.wrapper_source_texture &&
             a.source_slot == b.source_slot &&
             DirectTextureFetchDwordsMatch(a.texture_fetch_dwords,
                                           b.texture_fetch_dwords) &&
             DirectTextureFetchDwordsMatch(a.backing_fetch_dwords,
                                           b.backing_fetch_dwords);
    };
    auto score_link = [&](const FableSurfaceTextureLink& link,
                          const PlumeTrackedResource& sample) {
      uint64_t score = 1;
      const uint32_t command_wrapper_source =
          command.draw.private_texture_wrapper_source_texture;
      if (command_wrapper_source != 0) {
        if (link.wrapper_source_texture == command_wrapper_source) {
          score += 1ull << 56;
        } else if (link.wrapper_source_texture == 0) {
          score += 1ull << 52;
        }
      } else if (link.wrapper_source_texture != 0) {
        score += 1ull << 51;
      }
      if (link.texture == guest) {
        score += 1ull << 50;
      }
      if (link.texture_base_address != 0 &&
          link.texture_base_address == fetch_info.base_address) {
        score += 1ull << 49;
      }
      if (DirectTextureFetchDwordsMatch(link.texture_fetch_dwords,
                                        command_fetch)) {
        score += 1ull << 48;
      }
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      if (link.source_slot == slot) {
        score += 1ull << 47;
      } else if (link.source_slot == kUnknownSlot) {
        score += 1ull << 43;
      }
      const uint64_t sample_serial = FableTrackedResourceVisibleContentSerial(sample);
      if (link.sampled_source.resource != 0 &&
          link.sampled_source.resource == sample.guest_address) {
        score += 1ull << 46;
      }
      if (link.sampled_source.content_serial != 0 &&
          link.sampled_source.content_serial == sample_serial) {
        score += 1ull << 45;
      }
      if (link.target_content_serial != 0 &&
          link.target_content_serial == sample_serial) {
        score += 1ull << 44;
      }
      if (link.backing_content_serial != 0 &&
          link.backing_content_serial == sample_serial) {
        score += 1ull << 42;
      }
      if (link.vertex_shader != 0 &&
          link.vertex_shader == command.resources.vertex_shader) {
        score += 1ull << 41;
      }
      if (link.pixel_shader != 0 &&
          link.pixel_shader == command.resources.pixel_shader) {
        score += 1ull << 40;
      }
      if (link.render_target != 0 &&
          link.render_target == command.resources.render_targets[0]) {
        score += 1ull << 39;
      }
      if (link.depth_stencil != 0 &&
          link.depth_stencil == command.resources.depth_stencil) {
        score += 1ull << 38;
      }
      if (link.texture_width != 0 &&
          link.texture_width == PlumeGuestTextureWidth(sample)) {
        score += 1ull << 37;
      }
      if (link.texture_height != 0 &&
          link.texture_height == PlumeGuestTextureHeight(sample)) {
        score += 1ull << 36;
      }
      if (link.texture_format != 0 &&
          link.texture_format ==
              static_cast<uint32_t>(PlumeGuestTextureFormat(sample))) {
        score += 1ull << 35;
      }
      if (link.draw_serial != 0 && command_draw_serial != 0 &&
          link.draw_serial <= command_draw_serial) {
        score += link.draw_serial & 0xFFFFFull;
      } else {
        score += link.serial & 0xFFFFull;
      }
      return score;
    };
    for (const FableSurfaceTextureLink& link : *links) {
      if (!link_visible_to_command(link)) {
        continue;
      }
      if (require_exact_surface_link &&
          !DirectSurfaceTextureLinkHasExactCopyIdentity(link)) {
        continue;
      }
      if (require_exact_surface_link &&
          !DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(command,
                                                                        link)) {
        continue;
      }
      if (require_exact_surface_link &&
          DirectSurfaceTextureLinkRequiresExactWrapperSourceForMutableHeaderResolve(
              link)) {
        const uint32_t command_wrapper_source =
            command.draw.private_texture_wrapper_source_texture;
        if (command_wrapper_source == 0 || link.wrapper_source_texture == 0 ||
            link.wrapper_source_texture != command_wrapper_source) {
          continue;
        }
      }
      PlumeTrackedResource* sample =
          ResolveDirectSurfaceTextureLinkResourceLocked(command, link);
      if (!sample || DirectResourceIsPrivateFeedbackAlias(*sample)) {
        continue;
      }
      if (require_exact_surface_link &&
          (!IsImageResource(sample->kind) || !PlumeGuestImageReady(*sample) ||
           FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
           DirectCommandBindsResourceAsTargetLocked(command, *sample) ||
           !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*sample))) {
        continue;
      }
      constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
      if (link.source_slot != kUnknownSlot && link.source_slot != slot &&
          !DirectSurfaceTextureLinkTouchesFablePrivateChain(link, sample)) {
        continue;
      }
      if (sample == exact_sample && exact_link &&
          same_link_identity(link, *exact_link)) {
        continue;
      }
      const uint64_t sample_content_serial =
          FableTrackedResourceVisibleContentSerial(*sample);
      if (unique_sample_address == 0) {
        unique_sample_address = sample->guest_address;
        unique_sample_content_serial = sample_content_serial;
      } else if (!DirectResourceAddressMatchesLocked(sample->guest_address,
                                                     unique_sample_address) ||
                 sample_content_serial != unique_sample_content_serial) {
        has_distinct_sample = true;
      }
      ++match_count;
      const uint64_t score = score_link(link, *sample);
      if (!exact_link || score > best_score) {
        exact_link = &link;
        exact_sample = sample;
        best_score = score;
        continue;
      }
    }
    if (match_count == 0u || !exact_sample || !exact_link) {
      return nullptr;
    }
    const bool same_sample_surface_link_identity =
        match_count > 1u && !has_distinct_sample &&
        unique_sample_address != 0 && unique_sample_content_serial != 0;
    if (match_count > 1u && !same_sample_surface_link_identity) {
      remember_pending_mutable_header_ambiguity(reason, match_count,
                                                *exact_link, *exact_sample);
      return nullptr;
    }
    const FableSurfaceTextureLink exact_link_snapshot = *exact_link;
    exact_link = &exact_link_snapshot;
    if (same_sample_surface_link_identity) {
      static uint32_t logged_mutable_header_same_sample_link_identities = 0;
      if (logged_mutable_header_same_sample_link_identities < 32u) {
        REXLOG_INFO(
            "Refii plume exact mutable private header same-sample "
            "surface-link texture identity #{} frame={} reason={} "
            "matches={} serial={} draw={} slot={} guest=0x{:08X} "
            "sample=0x{:08X}/{}x{} fmt={} link_tex=0x{:08X} "
            "link_base=0x{:08X} link_surface=0x{:08X} sample_serial={} "
            "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
            ++logged_mutable_header_same_sample_link_identities,
            g_direct_renderer.current_frame_index, reason ? reason : "link",
            match_count, static_cast<unsigned long long>(command.serial),
            static_cast<unsigned long long>(command.draw_serial), slot, guest,
            exact_sample->guest_address, PlumeGuestTextureWidth(*exact_sample),
            PlumeGuestTextureHeight(*exact_sample),
            static_cast<uint32_t>(PlumeGuestTextureFormat(*exact_sample)),
            exact_link->texture, exact_link->texture_base_address,
            exact_link->source_surface,
            static_cast<unsigned long long>(
                FableTrackedResourceVisibleContentSerial(*exact_sample)),
            command.resources.render_targets[0],
            command.resources.depth_stencil, command.resources.vertex_shader,
            command.resources.pixel_shader);
      }
    }
    if (require_exact_surface_link) {
      FablePrivateTextureCopyAction action;
      action.source_slot = slot;
      action.source_texture = guest;
      action.source_base_address = fetch_info.base_address;
      action.source_width = fetch_info.width;
      action.source_height = fetch_info.height;
      action.source_format = fetch_info.format;
      action.source_fetch_dwords = command_fetch;
      PopulateDirectPrivateTextureCopyTargetLocked(action, *exact_sample);
      action.backing_texture = exact_link->backing_texture;
      action.backing_base_address = exact_link->backing_base_address;
      action.backing_fetch_dwords = exact_link->backing_fetch_dwords;
      action.backing_content_serial = exact_link->backing_content_serial;
      action.render_target = command.resources.render_targets[0];
      action.depth_stencil = command.resources.depth_stencil;
      action.vertex_shader = command.resources.vertex_shader;
      action.pixel_shader = command.resources.pixel_shader;
      PopulateDirectPrivateTextureCopyShaderRoots(action, command);
      action.lr = command.draw.state_flush_lr;
      action.wrapper_call_serial =
          command.draw.private_texture_wrapper_call_serial != 0
              ? command.draw.private_texture_wrapper_call_serial
              : exact_link->copy_call_serial;
      action.wrapper_context_key =
          command.draw.private_texture_wrapper_context_key != 0
              ? command.draw.private_texture_wrapper_context_key
              : exact_link->copy_context_key;
      action.wrapper_symbol_address =
          command.draw.private_texture_wrapper_symbol_address != 0
              ? command.draw.private_texture_wrapper_symbol_address
              : exact_link->copy_symbol_address;
      PopulateDirectPrivateTextureWrapperSourceIdentity(action, command,
                                                       exact_link);
      action.frame_index = g_direct_renderer.current_frame_index;
      action.draw_serial =
          command.draw_serial != 0 ? command.draw_serial : command.serial;
      action.kind = exact_link->kind != FablePrivateTextureCopyKind::kDepth
                        ? exact_link->kind
                        : FablePrivateTextureCopyKind::kColor;
      action.sampled_source = exact_link->sampled_source;

      RegisterDirectPrivateTextureCopyActionLocked(action);
      RememberDirectPrivateRectExactActionLocked(action);
      RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
          action, "mutable-private-header-exact-link");
      TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
          action, "mutable-private-header-exact-link");
      RememberDirectPrivateTextureAliasIndexLocked(
          action.source_texture, action.source_base_address,
          action.source_fetch_dwords, *exact_sample);
      RememberDirectMutablePrivateHeaderExactSampleLocked(
          command, slot, action, *exact_sample,
          "mutable-private-header-exact-link");
    }
	    static uint32_t logged_mutable_header_surface_link_resolves = 0;
	    if (logged_mutable_header_surface_link_resolves < 32u) {
	      REXLOG_INFO(
          "Refii plume mutable private header surface-link sample #{} "
          "frame={} reason={} serial={} draw={} slot={} guest=0x{:08X} "
          "sample=0x{:08X}/{}x{} fmt={} link_tex=0x{:08X} "
	          "link_base=0x{:08X} link_surface=0x{:08X} sample_serial={} "
	          "target_serial={} backing_serial={} sampled={} "
	          "sample_resource=0x{:08X} sampled_serial={} "
	          "link_wrapper_source=0x{:08X}/{} command_wrapper_source=0x{:08X}/{} "
	          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
	          ++logged_mutable_header_surface_link_resolves,
          g_direct_renderer.current_frame_index, reason ? reason : "link",
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial), slot, guest,
          exact_sample->guest_address, PlumeGuestTextureWidth(*exact_sample),
          PlumeGuestTextureHeight(*exact_sample),
	          static_cast<uint32_t>(PlumeGuestTextureFormat(*exact_sample)),
	          exact_link->texture, exact_link->texture_base_address,
	          exact_link->source_surface,
	          static_cast<unsigned long long>(
	              FableTrackedResourceVisibleContentSerial(*exact_sample)),
	          static_cast<unsigned long long>(exact_link->target_content_serial),
	          static_cast<unsigned long long>(exact_link->backing_content_serial),
	          static_cast<uint32_t>(exact_link->sampled_source.kind),
	          exact_link->sampled_source.resource,
	          static_cast<unsigned long long>(
	              exact_link->sampled_source.content_serial),
	          exact_link->wrapper_source_texture,
	          exact_link->wrapper_source_exact ? 1 : 0,
	          command.draw.private_texture_wrapper_source_texture,
	          command.draw.private_texture_wrapper_source_exact ? 1 : 0,
	          command.resources.render_targets[0],
	          command.resources.depth_stencil, command.resources.vertex_shader,
          command.resources.pixel_shader);
    }
    return exact_sample;
  };

  auto resolve_link_source_action =
      [&](const std::vector<FableSurfaceTextureLink>* links,
          const char* reason) -> PlumeTrackedResource* {
    if (!links || links->empty()) {
      return nullptr;
    }

    const uint64_t command_draw_serial =
        command.draw_serial != 0 ? command.draw_serial : command.serial;
    const FableSurfaceTextureLink* best_link = nullptr;
    const FablePrivateTextureCopyAction* best_action = nullptr;
    PlumeTrackedResource* best_sample = nullptr;
    uint64_t best_score = 0;

    auto visible_action =
        [&](const FablePrivateTextureCopyAction& action) {
      if (action.frame_index != 0 &&
          action.frame_index > g_direct_renderer.current_frame_index) {
        return false;
      }
      if (action.frame_index != 0 &&
          action.frame_index == g_direct_renderer.current_frame_index &&
          action.draw_serial != 0 && command_draw_serial != 0 &&
          action.draw_serial > command_draw_serial) {
        return false;
      }
      return true;
    };

    auto valid_action_sample =
        [&](const FablePrivateTextureCopyAction& action)
            -> PlumeTrackedResource* {
      if (!visible_action(action) ||
          action.kind == FablePrivateTextureCopyKind::kDepth ||
          !DirectPrivateTextureCopyKindUsesColorBacking(action.kind) ||
          action.target_surface == 0) {
        return nullptr;
      }
      PlumeTrackedResource* sample = FindDirectResourceLocked(action.target_surface);
      if (!sample || !IsImageResource(sample->kind) ||
          !PlumeGuestImageReady(*sample) ||
          DirectResourceIsPrivateFeedbackAlias(*sample) ||
          FableTrackedResourceIsBlankUploadedSampleProxy(*sample) ||
          DirectCommandBindsResourceAsTargetLocked(command, *sample) ||
          (!FableTrackedResourceHasSampleableContent(*sample) &&
           !FableTrackedResourceHasGpuProducedContent(*sample))) {
        return nullptr;
      }
      const uint64_t sample_content_serial =
          FableTrackedResourceVisibleContentSerial(*sample);
      if (sample_content_serial == 0 ||
          (action.target_content_serial != 0 &&
           sample_content_serial < action.target_content_serial)) {
        return nullptr;
      }
      return sample;
    };

    auto consider_link_action =
        [&](const FableSurfaceTextureLink& link,
            const FablePrivateTextureCopyAction& action) {
      PlumeTrackedResource* sample = valid_action_sample(action);
      if (!sample) {
        return;
      }
      uint64_t score =
          FableTrackedResourceVisibleContentSerial(*sample) + action.draw_serial +
          static_cast<uint64_t>(action.frame_index) * 1000000ull;
      if (action.source_texture == link.source_surface) {
        score += 1000000000000ull;
      }
      if (action.target_width == PlumeGuestTextureWidth(*sample) &&
          action.target_height == PlumeGuestTextureHeight(*sample)) {
        score += 1000000000ull;
      }
      if (!best_action || score > best_score) {
        best_link = &link;
        best_action = &action;
        best_sample = sample;
        best_score = score;
      }
    };

    for (const FableSurfaceTextureLink& link : *links) {
      if (!link_visible_to_command(link) || !link.trusted_destination_edge ||
          !DirectSurfaceTextureLinkHasExactCopyIdentity(link) ||
          !DirectPrivateTextureCopyKindUsesColorBacking(link.kind) ||
          link.source_surface == 0 ||
          DirectResourceAddressMatchesLocked(link.source_surface, guest)) {
        continue;
      }
      if (const PlumeTrackedResource* link_source =
              FindDirectResourceLocked(link.source_surface);
          link_source &&
          DirectCommandBindsResourceAsTargetLocked(command, *link_source)) {
        continue;
      }

      const std::vector<FablePrivateTextureCopyAction>* source_actions =
          FableDirectFindPrivateTextureCopyActionsBySource(link.source_surface);
      if (!source_actions || source_actions->empty()) {
        continue;
      }
      for (auto action_it = source_actions->rbegin();
           action_it != source_actions->rend(); ++action_it) {
        consider_link_action(link, *action_it);
      }
    }

    if (!best_link || !best_action || !best_sample) {
      return nullptr;
    }

    const FableSurfaceTextureLink selected_link = *best_link;
    const FablePrivateTextureCopyAction selected_action = *best_action;
    FablePrivateTextureCopyAction proxy_action = selected_action;
    proxy_action.source_slot = slot;
    proxy_action.source_texture = guest;
    proxy_action.source_base_address = fetch_info.base_address;
    proxy_action.source_width = fetch_info.width;
    proxy_action.source_height = fetch_info.height;
    proxy_action.source_format = fetch_info.format;
    proxy_action.source_fetch_dwords = command_fetch;
    proxy_action.backing_texture = selected_link.texture;
    proxy_action.backing_base_address = selected_link.texture_base_address;
    proxy_action.backing_width = selected_link.texture_width;
    proxy_action.backing_height = selected_link.texture_height;
    proxy_action.backing_format = selected_link.texture_format;
    proxy_action.backing_fetch_dwords = selected_link.texture_fetch_dwords;
    proxy_action.backing_content_serial = selected_link.target_content_serial;
    proxy_action.target_surface = best_sample->guest_address;
    proxy_action.target_width = PlumeGuestTextureWidth(*best_sample);
    proxy_action.target_height = PlumeGuestTextureHeight(*best_sample);
    proxy_action.target_format = PlumeGuestTextureGuestFormat(*best_sample);
    proxy_action.target_content_serial =
        FableTrackedResourceVisibleContentSerial(*best_sample);
    proxy_action.render_target = command.resources.render_targets[0];
    proxy_action.depth_stencil = command.resources.depth_stencil;
    proxy_action.vertex_shader = command.resources.vertex_shader;
    proxy_action.pixel_shader = command.resources.pixel_shader;
    PopulateDirectPrivateTextureCopyShaderRoots(proxy_action, command);
    proxy_action.wrapper_call_serial = selected_link.copy_call_serial;
    proxy_action.wrapper_context_key = selected_link.copy_context_key;
    proxy_action.wrapper_symbol_address = selected_link.copy_symbol_address;
    proxy_action.wrapper_source_texture = guest;
    proxy_action.wrapper_source_exact = guest != 0;
    proxy_action.frame_index = g_direct_renderer.current_frame_index;
    proxy_action.draw_serial = command_draw_serial;
    proxy_action.kind = selected_link.kind;
    PopulateDirectPrivateTextureCopyRectangleFromCommand(proxy_action,
                                                         command);
    proxy_action.sampled_source = {};
    proxy_action.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
	    proxy_action.sampled_source.resource = best_sample->guest_address;
	    proxy_action.sampled_source.content_serial =
	        proxy_action.target_content_serial;
	
	    if (DirectResourceAddressMatchesLocked(selected_link.source_surface,
	                                           best_sample->guest_address)) {
	      FableSurfaceTextureLink sampled_link = selected_link;
	      PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(sampled_link,
	                                                            *best_sample);
	      RegisterDirectSurfaceTextureLinkPreparedLocked(
	          sampled_link, "mutable-private-header-link-source-sample");
	    }
	    RegisterDirectPrivateTextureCopyActionLocked(proxy_action);
	    RememberDirectPrivateRectExactActionLocked(proxy_action);
	    RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
        proxy_action, "mutable-private-header-link-source-action");
    TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
        proxy_action, "mutable-private-header-link-source-action");
    RememberDirectMutablePrivateHeaderExactSampleLocked(
        command, slot, proxy_action, *best_sample,
        "mutable-private-header-link-source-action");

    static uint32_t logged_exact_private_header_source_action_textures = 0;
    if (logged_exact_private_header_source_action_textures < 32u) {
      REXLOG_INFO(
          "Refii plume exact private-header link-source action texture "
          "identity #{} frame={} serial={} draw={} reason={} slot={} "
          "guest=0x{:08X} "
          "link_tex=0x{:08X} link_source=0x{:08X} "
          "action_source=0x{:08X} tracked=0x{:08X} base=0x{:08X} "
          "{}x{} fmt={} gen={} content={} "
          "call={} context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
          "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_exact_private_header_source_action_textures,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.serial),
          static_cast<unsigned long long>(command.draw_serial),
          reason ? reason : "link",
          slot, guest, selected_link.texture, selected_link.source_surface,
          selected_action.source_texture, best_sample->guest_address,
          PlumeGuestTextureBaseAddress(*best_sample),
          PlumeGuestTextureWidth(*best_sample),
          PlumeGuestTextureHeight(*best_sample),
          static_cast<uint32_t>(PlumeGuestTextureFormat(*best_sample)),
          static_cast<unsigned long long>(best_sample->upload_generation),
          static_cast<unsigned long long>(
              FableTrackedResourceVisibleContentSerial(*best_sample)),
          static_cast<unsigned long long>(selected_link.copy_call_serial),
          static_cast<unsigned long long>(selected_link.copy_context_key),
          selected_link.copy_symbol_address,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader,
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return best_sample;
  };
  if (PlumeTrackedResource* linked_action_sample =
          resolve_link_source_action(
              FableDirectFindSurfaceTextureLinksByFetch(command_fetch),
              "command-fetch")) {
    return linked_action_sample;
  }
  if (PlumeTrackedResource* linked_action_sample =
          resolve_link_source_action(
              FableDirectFindSurfaceTextureLinksByBase(fetch_info.base_address),
              "command-base")) {
    return linked_action_sample;
  }

  if (PlumeTrackedResource* exact_sample =
          ResolveDirectMutablePrivateHeaderExactSampleLocked(
              command, slot, direct_texture)) {
    return exact_sample;
  }
  if (PlumeTrackedResource* linked_sample = try_fetch_or_base_links(
          FableDirectFindSurfaceTextureLinksByFetch(command_fetch),
          "command-fetch")) {
    return linked_sample;
  }
  if (PlumeTrackedResource* linked_sample = try_fetch_or_base_links(
          FableDirectFindSurfaceTextureLinksByBase(fetch_info.base_address),
          "command-base")) {
    return linked_sample;
  }
  if (require_exact_surface_link) {
    log_pending_mutable_header_ambiguity();
    return nullptr;
  }

  PlumeTrackedResource* best = nullptr;
  uint64_t best_score = 0;
  auto consider_identity_object = [&](uint32_t object,
                                      uint64_t identity_bonus) {
    const FableTextureIdentity* identity =
        FableDirectFindTextureIdentityByObject(object);
    if (!identity) {
      return;
    }
    PlumeTrackedResource* resource =
        FindDirectResourceLocked(identity->object_texture);
    if (!resource || resource == direct_texture ||
        !IsImageResource(resource->kind) || !PlumeGuestImageReady(*resource) ||
        DirectResourceIsPrivateFeedbackAlias(*resource) ||
        !FableTrackedResourceHasNonBlankUploadOrGpuProducedContent(*resource) ||
        FableTrackedResourceIsBlankUploadedSampleProxy(*resource) ||
        DirectCommandBindsResourceAsTargetLocked(command, *resource) ||
        DirectPrivateTextureHeaderNeedsAlias(command, slot, resource)) {
      return;
    }

    if (identity->base_address != 0 &&
        identity->base_address != fetch_info.base_address) {
      return;
    }
    if (fetch_info.width != 0 &&
        PlumeGuestTextureWidth(*resource) != fetch_info.width) {
      return;
    }
    if (fetch_info.height != 0 &&
        PlumeGuestTextureHeight(*resource) != fetch_info.height) {
      return;
    }

    uint64_t score = identity_bonus + identity->serial + resource->serial +
                     resource->upload_generation;
    if (DirectTextureFetchDwordsMatch(identity->fetch_dwords, command_fetch)) {
      score += 1'000'000'000ull;
    }
    if (DirectTextureFetchDwordsMatch(PlumeGuestTextureFetchDwords(*resource),
                                      command_fetch)) {
      score += 100'000'000ull;
    }
    if (identity->meaningful_upload ||
        FableTrackedResourceHasMeaningfulTextureUpload(*resource)) {
      score += 10'000'000ull;
    }
    if (PlumeGuestTextureBaseAddress(*resource) == fetch_info.base_address) {
      score += 1'000'000ull;
    }
    if (!best || score > best_score) {
      best = resource;
      best_score = score;
    }
  };

  if (const std::vector<uint32_t>* fetch_objects =
          FableDirectFindTextureIdentityObjectsByFetch(command_fetch)) {
    for (uint32_t object : *fetch_objects) {
      consider_identity_object(object, 10'000'000'000ull);
    }
  }
  if (!best) {
    if (const std::vector<uint32_t>* base_objects =
            FableDirectFindTextureIdentityObjectsByBase(
                fetch_info.base_address)) {
      for (uint32_t object : *base_objects) {
        consider_identity_object(object, 1'000'000'000ull);
      }
    }
  }

  if (!best) {
    return nullptr;
  }

  static uint32_t logged_exact_mutable_header_identity_resolves = 0;
  if (logged_exact_mutable_header_identity_resolves < 32u) {
    REXLOG_INFO(
        "Refii plume exact mutable private header identity sample #{} frame={} "
        "serial={} draw={} slot={} guest=0x{:08X} sample=0x{:08X} "
        "base=0x{:08X} {}x{} fmt={} gen={} hash=0x{:016X} "
        "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
        "fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        ++logged_exact_mutable_header_identity_resolves,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.serial),
        static_cast<unsigned long long>(command.draw_serial), slot, guest,
        best->guest_address, PlumeGuestTextureBaseAddress(*best),
        PlumeGuestTextureWidth(*best), PlumeGuestTextureHeight(*best),
        static_cast<uint32_t>(PlumeGuestTextureFormat(*best)),
        static_cast<unsigned long long>(best->upload_generation),
        static_cast<unsigned long long>(best->texture_upload_hash),
        command.resources.render_targets[0], command.resources.depth_stencil,
        command.resources.vertex_shader, command.resources.pixel_shader,
        command_fetch[0], command_fetch[1], command_fetch[2],
        command_fetch[3], command_fetch[4], command_fetch[5]);
  }
  return best;
}

bool IsFableTitleBillboardAtlasResource(const PlumeTrackedResource& texture) {
  return FableTitleBillboardAtlasResource(
      DirectTitleResourceFactsForResource(texture));
}

bool DirectSnapshotMatchesResourceFilter(
    const FableGuestResourceSnapshot& snapshot,
    const std::unordered_set<uint32_t>& resource_filter) {
  const auto contains = [&resource_filter](uint32_t address) {
    return address != 0 && resource_filter.find(address) != resource_filter.end();
  };

  if (contains(snapshot.guest_address) || contains(snapshot.parent_address)) {
    return true;
  }

  switch (snapshot.kind) {
    case FableGuestResourceKind::kVertexBuffer:
    case FableGuestResourceKind::kIndexBuffer:
      return contains(snapshot.buffer.object_address) ||
             contains(snapshot.buffer.gpu_address) ||
             contains(snapshot.buffer.header_address);
    case FableGuestResourceKind::kTexture:
      if (contains(snapshot.texture.object_address) ||
          contains(snapshot.texture.parent_address) ||
          contains(snapshot.texture.base_address)) {
        return true;
      }
      if (IsFableTitleBillboardTileUploadBaseAddress(
              snapshot.texture.base_address)) {
        const uint32_t base_object = FableTitleBillboardTileObjectAddressForBase(
            snapshot.texture.base_address);
        const uint32_t base_header =
            FableTitleBillboardTileHeaderAddressForObject(base_object);
        const uint32_t guest_header =
            FableTitleBillboardTileHeaderAddressForObject(
                snapshot.guest_address);
        const uint32_t object_header =
            FableTitleBillboardTileHeaderAddressForObject(
                snapshot.texture.object_address);
        const uint32_t guest_object =
            FableTitleBillboardTileObjectAddressForHeader(snapshot.guest_address);
        const uint32_t object_object =
            FableTitleBillboardTileObjectAddressForHeader(
                snapshot.texture.object_address);
        return contains(base_object) || contains(base_header) ||
               contains(guest_header) || contains(object_header) ||
               contains(guest_object) || contains(object_object);
      }
      return false;
    case FableGuestResourceKind::kSurface:
    case FableGuestResourceKind::kDepthSurface:
      return contains(snapshot.surface.object_address) ||
             contains(snapshot.surface.parent_address);
    case FableGuestResourceKind::kVertexShader:
    case FableGuestResourceKind::kPixelShader:
      return contains(snapshot.shader.object_address) ||
             contains(snapshot.shader.bytecode_address);
    case FableGuestResourceKind::kVertexDeclaration:
      return contains(snapshot.vertex_declaration.object_address) ||
             contains(snapshot.vertex_declaration.elements_address);
    case FableGuestResourceKind::kUnknown:
      break;
  }
  return false;
}

bool IsFableDirectTitleBillboardAtlasSnapshot(
    const FableGuestResourceSnapshot& snapshot) {
  if (snapshot.kind != FableGuestResourceKind::kTexture) {
    return false;
  }

  const auto is_known_object = [](uint32_t address) {
    // ADDRESS GATE (census: billboard_atlas_snapshot_known_object).
    //
    // PHASE 2 (2026-07-26): 0x4C132C70, 0x4C132CA4 and 0x4C132CD8 were DELETED
    // from this list as provably redundant — not replaced by a guess. The
    // header-object strided range is
    // [kFableTitleBillboardTileHeaderFirstObject = 0x4C132C70 ..
    //  kFableTitleBillboardTileHeaderLastObject  = 0x4C132D40]
    // step kFableTitleBillboardTileObjectStride = 0x68, i.e. objects
    // {0x4C132C70, 0x4C132CD8, 0x4C132D40}, each with a header alias at
    // object + kFableTitleBillboardTileHeaderOffset (0x34), i.e.
    // {0x4C132CA4, 0x4C132D0C, 0x4C132D74}. So
    // IsFableTitleBillboardTileUploadObjectAddress already answers yes for
    // 0x4C132C70 and 0x4C132CD8, and IsFableTitleBillboardTileUploadHeader
    // Address already answers yes for 0x4C132CA4 — and both predicates are in
    // this very || chain. Removing the three literals is therefore a pure
    // boolean simplification with an identical result for every input on every
    // run, not an inference.
    //
    // IDA EVIDENCE (2026-07-26, default.xex IDB): sizeof(D3DTexture) == 52 ==
    // 0x34 (Common/ReferenceCount/Fence/ReadFence/Identifier/BaseFlush/MipFlush
    // + GPUTEXTURE_FETCH_CONSTANT[24 bytes]), which is exactly
    // kFableTitleBillboardTileHeaderOffset, and
    // kFableTitleBillboardTileObjectStride 0x68 == 2 * 0x34. So "object" and
    // "header" are two ADJACENT D3DTexture records in one pooled array — the
    // stride is a real D3D ABI fact, not a run artifact. NOTE the limit of
    // that: only the stride is structural. The array's BASE (0x4C132C70) is
    // still a per-run pool address, so this whole family remains run-dependent
    // and must eventually be anchored on something the guest states.
    //
    // PHASE 3 (2026-07-26): 0x4C133150 and 0x4C133184 (= 0x4C133150 + 0x34)
    // are now DELETED too. They sat 0x4E0 past the range start, beyond
    // kFableTitleBillboardTileHeaderLastObject, so no structural predicate
    // covered them and they were kept with a census attached. Run 288
    // census[final] (frame=2952) lists billboard_atlas_snapshot_known_object
    // under tested_but_never_matched: control reached the literal and it never
    // once said yes, so the two addresses classified nothing. They were the
    // leading term of this `||` chain, so their removal cannot change the
    // result for any input that the remaining structural predicates accept.
    return address == kFableCanonicalTitleBillboardBase ||
           IsFableTitleBillboardTileUploadObjectAddress(address) ||
           IsFableTitleBillboardTileUploadHeaderAddress(address) ||
           IsFableTitleBillboardTileUploadBaseAddress(address);
  };
  const bool known_carrier =
      is_known_object(snapshot.guest_address) ||
      is_known_object(snapshot.parent_address) ||
      is_known_object(snapshot.texture.object_address) ||
      is_known_object(snapshot.texture.parent_address);
  const bool known_base =
      snapshot.texture.base_address == kFableCanonicalTitleBillboardBase ||
      IsFableTitleBillboardTileUploadBaseAddress(snapshot.texture.base_address);
  if (!known_carrier || !known_base) {
    return false;
  }

  const std::array<uint32_t, 6>& fetch_dwords =
      HasTrackedFetchDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch_dwords);
  const bool known_fetch =
      fetch_info.valid &&
      fetch_info.base_address == kFableCanonicalTitleBillboardBase &&
      fetch_info.width == 1024u && fetch_info.height == 512u;
  const bool known_tile_fetch =
      fetch_info.valid &&
      IsFableTitleBillboardTileUploadBase(fetch_info.base_address,
                                          fetch_info.width,
                                          fetch_info.height);
  const bool known_shape =
      snapshot.texture.byte_size >= 524288u;
  return known_fetch || known_tile_fetch || known_shape;
}

bool IsFableExactTitleUploadedSnapshot(
    const FableGuestResourceSnapshot& snapshot) {
  if (snapshot.kind != FableGuestResourceKind::kTexture) {
    return false;
  }

  const uint32_t base = snapshot.texture.base_address;
  const std::array<uint32_t, 6>& fetch_dwords =
      HasTrackedFetchDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch_dwords);
  const uint32_t width = fetch_info.valid ? fetch_info.width : 0u;
  const uint32_t height = fetch_info.valid ? fetch_info.height : 0u;

  return IsFableTitleUploadedTextureIdentityBase(base, width, height) ||
         IsFableTitleGuiTextAtlasObjectAddress(snapshot.guest_address) ||
         IsFableTitleGuiTextAtlasObjectAddress(snapshot.parent_address) ||
         IsFableTitleGuiTextAtlasObjectAddress(
             snapshot.texture.object_address) ||
         IsFableTitleGuiTextAtlasObjectAddress(
             snapshot.texture.parent_address);
}

bool IsFableExactTitleGuiTextAtlasSnapshot(
    const FableGuestResourceSnapshot& snapshot) {
  if (snapshot.kind != FableGuestResourceKind::kTexture) {
    return false;
  }

  const std::array<uint32_t, 6>& fetch_dwords =
      HasTrackedFetchDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch_dwords);
  if (!fetch_info.valid) {
    return false;
  }

  return ExactFableTitleGuiTextAtlasUploadSize(
             fetch_info.base_address, fetch_info.width, fetch_info.height,
             fetch_info.format) != 0;
}

bool DirectCommandTextureSlotHasExactTitleGuiTextAtlasIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FableTextureIdentity* identity,
    const DirectTextureFetchInfo& fetch_info) {
  FableTitleGuiAtlasSlotIdentityFacts facts;
  facts.identity = identity;
  facts.fetch_valid = fetch_info.valid;
  facts.fetch_base_address = fetch_info.base_address;
  facts.fetch_width = fetch_info.width;
  facts.fetch_height = fetch_info.height;
  facts.fetch_format = fetch_info.format;

  const PlumeTrackedResource* texture =
      slot < command.resources.textures.size()
          ? FindDirectResourceLocked(command.resources.textures[slot])
          : nullptr;
  facts.bound_image_resource = texture && IsImageResource(texture->kind);
  if (texture) {
    facts.bound_base_address = PlumeGuestTextureBaseAddress(*texture);
    facts.bound_width = PlumeGuestTextureWidth(*texture);
    facts.bound_height = PlumeGuestTextureHeight(*texture);
    facts.bound_format = PlumeGuestTextureGuestFormat(*texture);
  }
  return FableTitleCommandSlotHasExactGuiTextAtlasIdentity(facts);
}

bool IsFableTitleGuiTextAtlasCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  const bool slot_in_range = slot < command.resources.textures.size();
  if (!slot_in_range ||
      command.draw.source != RendererDrawEventSource::kSceneObject) {
    return false;
  }
  const uint32_t guest = slot_in_range ? command.resources.textures[slot] : 0;
  const FableTextureIdentity* identity =
      FableDirectFindTextureIdentityByObject(guest);
  const std::array<uint32_t, 6> fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo fetch_info =
      DirectTextureFetchInfoFromDwords(fetch);

  FableTitleGuiTextAtlasCommandFacts facts;
  facts.slot_in_range = slot_in_range;
  facts.scene_object_source =
      command.draw.source == RendererDrawEventSource::kSceneObject;
  facts.slot = slot;
  facts.private_rect_source_slot = source_slot;
  facts.guest = guest;
  facts.tiny_title_upload_identity =
      IsFableTinyTitleUploadedTextureIdentity(guest, identity) ||
      (fetch_info.valid &&
       IsFableTinyTitleUploadedTextureIdentityBase(fetch_info.base_address,
                                                   fetch_info.width,
                                                   fetch_info.height));
  facts.repeated_mesh_declaration = DirectVertexDeclarationMatchesHashLocked(
      command.resources.vertex_declaration, kFableRepeatedMeshDeclarationHash);
  facts.primitive_type = command.draw.draw.primitive_type;
  facts.title_scene_target =
      !command.resources.render_targets.empty() &&
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]);
  facts.exact_gui_text_atlas_identity =
      DirectCommandTextureSlotHasExactTitleGuiTextAtlasIdentityLocked(
          command, slot, identity, fetch_info);
  facts.private_rect_declaration = DirectVertexDeclarationMatchesHashLocked(
      command.resources.vertex_declaration, kFablePrivateRectDeclarationHash);
  facts.input_slot0_stride = ResolveDirectInputSlotStrideLocked(command, 0);
  facts.gui_text_atlas_object =
      IsFableTitleGuiTextAtlasObjectAddress(guest);
  facts.vertex_hash = DirectVertexShaderHashLocked(command.resources.vertex_shader);
  facts.pixel_hash = DirectPixelShaderHashLocked(command.resources.pixel_shader);
  facts.pixel_shader = command.resources.pixel_shader;
  facts.title_scene_vertex_shader_command =
      IsFableTitleSceneVertexShaderCommandLocked(command);
  return FableTitleGuiTextAtlasCommand(facts);
}


PlumeTrackedResource* ResolveFableTitleGuiTextAtlasTextureLocked(
    const FableDirectRenderCommand& command, uint32_t slot) {
  if (!IsFableTitleGuiTextAtlasCommandLocked(command, slot)) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  PlumeTrackedResource* texture = FindDirectResourceLocked(guest);
  const auto gui_atlas_candidate_facts =
      [&](const PlumeTrackedResource* candidate,
          const FableTextureIdentity* identity = nullptr)
      -> FableTitleGuiAtlasCandidateFacts {
    FableTitleGuiAtlasCandidateFacts facts;
    facts.command_fetch_valid = command_fetch_info.valid;
    facts.command_guest = guest;
    facts.command_fetch_base_address = command_fetch_info.base_address;
    facts.command_fetch_width = command_fetch_info.width;
    facts.command_fetch_height = command_fetch_info.height;
    facts.command_fetch_format = command_fetch_info.format;
    facts.identity = identity;
    facts.bound_texture_ready = texture && PlumeGuestImageReady(*texture);
    if (texture) {
      facts.bound_guest_address = texture->guest_address;
      facts.bound_parent_address = texture->parent_address;
      facts.bound_base_address = PlumeGuestTextureBaseAddress(*texture);
    }
    if (!candidate) {
      return facts;
    }

    facts.image_ready =
        IsImageResource(candidate->kind) && PlumeGuestImageReady(*candidate);
    facts.texture_depth_target = candidate->texture_depth_target;
    facts.command_binds_candidate_as_target =
        DirectCommandBindsResourceAsTargetLocked(command, *candidate);
    facts.guest_address = candidate->guest_address;
    facts.parent_address = candidate->parent_address;
    facts.base_address = PlumeGuestTextureBaseAddress(*candidate);
    facts.width = PlumeGuestTextureWidth(*candidate);
    facts.height = PlumeGuestTextureHeight(*candidate);
    facts.format = PlumeGuestTextureGuestFormat(*candidate);
    facts.texture_upload_size = candidate->texture_upload_size;
    facts.texture_upload_hash = candidate->texture_upload_hash;
    facts.meaningful_upload =
        FableTrackedResourceHasMeaningfulTextureUpload(*candidate);
    facts.gui_text_atlas_object =
        IsFableTitleGuiTextAtlasObjectAddress(candidate->guest_address);
    facts.title_gui_atlas_upload =
        FableTitleGuiAtlasResourceHasUpload(
            DirectTitleResourceFactsForResource(*candidate));
    return facts;
  };

  const auto valid_exact_command_gui_atlas =
      [&](PlumeTrackedResource* candidate) -> PlumeTrackedResource* {
    const auto log_pending_payload = [&](PlumeTrackedResource* pending,
                                         uint32_t exact_upload_size) {
      static uint32_t logged_pending_exact_gui_atlas_payload_syncs = 0;
      if (logged_pending_exact_gui_atlas_payload_syncs >= 32u) {
        return;
      }
      const uint32_t candidate_base =
          pending ? PlumeGuestTextureBaseAddress(*pending) : 0u;
      const uint32_t candidate_width =
          pending ? PlumeGuestTextureWidth(*pending) : 0u;
      const uint32_t candidate_height =
          pending ? PlumeGuestTextureHeight(*pending) : 0u;
      const uint32_t candidate_format =
          pending ? PlumeGuestTextureGuestFormat(*pending) : 0u;
      REXLOG_INFO(
          "Refii plume pending exact title GUI atlas payload sync #{} frame={} "
          "slot={} guest=0x{:08X} candidate=0x{:08X} parent=0x{:08X} "
          "kind={} ready={} rt={} ds={} binds={} cmd_valid={} "
          "cmd_base=0x{:08X} cmd={}x{} fmt={} cand_base=0x{:08X} "
          "cand={}x{} fmt={} exact_size={} upload_size={} "
          "upload_hash=0x{:016X} upload_source={} gen={} blank={}",
          ++logged_pending_exact_gui_atlas_payload_syncs,
          g_direct_renderer.current_frame_index, slot, guest,
          pending ? pending->guest_address : 0u,
          pending ? pending->parent_address : 0u,
          pending ? static_cast<uint32_t>(pending->kind) : 0u,
          pending && PlumeGuestImageReady(*pending) ? 1 : 0,
          pending && pending->texture_render_target ? 1 : 0,
          pending && pending->texture_depth_target ? 1 : 0,
          pending && DirectCommandBindsResourceAsTargetLocked(command, *pending)
              ? 1
              : 0,
          command_fetch_info.valid ? 1 : 0, command_fetch_info.base_address,
          command_fetch_info.width, command_fetch_info.height,
          command_fetch_info.format, candidate_base, candidate_width,
          candidate_height, candidate_format, exact_upload_size,
          pending ? static_cast<unsigned long long>(
                        pending->texture_upload_size)
                  : 0ull,
          pending ? static_cast<unsigned long long>(
                        pending->texture_upload_hash)
                  : 0ull,
          pending && pending->texture_upload_has_source ? 1 : 0,
          pending ? static_cast<unsigned long long>(pending->upload_generation)
                  : 0ull,
          pending && FableTrackedResourceIsBlankUploadedSampleProxy(*pending) ? 1 : 0);
    };
    if (!candidate) {
      return nullptr;
    }

    const FableTitleGuiAtlasCandidateFacts facts =
        gui_atlas_candidate_facts(candidate);
    if (!FableTitleGuiAtlasCandidateCanBeExactCommandUpload(facts)) {
      return nullptr;
    }
    const uint32_t exact_upload_size =
        FableTitleGuiAtlasCandidateExactUploadSize(facts);
    if (!FableTitleGuiAtlasCandidateIsExactCommandUpload(facts)) {
      log_pending_payload(candidate, exact_upload_size);
      return nullptr;
    }

    static uint32_t logged_exact_gui_command_atlas_samples = 0;
    if (logged_exact_gui_command_atlas_samples < 32u) {
      REXLOG_INFO(
          "Refii plume exact title GUI command atlas #{} frame={} slot={} "
          "guest=0x{:08X} sample=0x{:08X} base=0x{:08X} {}x{} fmt={} "
          "gen={} size={} fetch={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          ++logged_exact_gui_command_atlas_samples,
          g_direct_renderer.current_frame_index, slot, guest,
          candidate->guest_address, facts.base_address, facts.width,
          facts.height, facts.format,
          static_cast<unsigned long long>(candidate->upload_generation),
          static_cast<unsigned long long>(candidate->texture_upload_size),
          command_fetch[0], command_fetch[1], command_fetch[2],
          command_fetch[3], command_fetch[4], command_fetch[5]);
    }
    return candidate;
  };

  auto valid_gui_atlas = [&](PlumeTrackedResource* candidate,
                             const FableTextureIdentity* identity) {
    return candidate &&
           FableTitleGuiAtlasCandidateMatchesIdentityOrBoundResource(
               gui_atlas_candidate_facts(candidate, identity));
  };
  const auto try_address = [&](uint32_t address,
                               const FableTextureIdentity* identity = nullptr)
      -> PlumeTrackedResource* {
    if (address == 0) {
      return nullptr;
    }
    PlumeTrackedResource* candidate = FindDirectResourceLocked(address);
    if (candidate && candidate->guest_address == guest) {
      if (PlumeTrackedResource* exact =
              valid_exact_command_gui_atlas(candidate)) {
        return exact;
      }
    }
    return valid_gui_atlas(candidate, identity) ? candidate : nullptr;
  };
  const auto try_exact_identity =
      [&](const FableTextureIdentity* identity) -> PlumeTrackedResource* {
    return TryDirectTitleUploadIdentityAddresses(
        identity, [&](uint32_t address) {
          return try_address(address, identity);
        });
  };

  if (texture) {
    if (PlumeTrackedResource* exact =
            valid_exact_command_gui_atlas(texture)) {
      return exact;
    }
  }
  if (valid_gui_atlas(texture, FableDirectFindTextureIdentityByObject(guest))) {
    return texture;
  }
  if (PlumeTrackedResource* candidate =
          try_exact_identity(FableDirectFindTextureIdentityByObject(guest))) {
    return candidate;
  }
  if (command_fetch_info.valid &&
      IsFableTitleUploadedTextureIdentityBase(command_fetch_info.base_address,
                                             command_fetch_info.width,
                                             command_fetch_info.height)) {
    if (PlumeTrackedResource* candidate =
            try_address(command_fetch_info.base_address)) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByBase(command_fetch_info.base_address))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
            FableDirectFindTextureIdentityObjectsByBase(
                command_fetch_info.base_address),
            try_exact_identity)) {
      return candidate;
    }
  }
  if (HasTrackedFetchDwords(command_fetch)) {
    if (PlumeTrackedResource* candidate =
            try_exact_identity(FableDirectFindTextureIdentityByFetch(command_fetch))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
            FableDirectFindTextureIdentityObjectsByFetch(command_fetch),
            try_exact_identity)) {
      return candidate;
    }
  }
  if (texture) {
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByObject(texture->guest_address))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = try_exact_identity(
            FableDirectFindTextureIdentityByObject(texture->parent_address))) {
      return candidate;
    }
    const uint32_t texture_base = PlumeGuestTextureBaseAddress(*texture);
    if (PlumeTrackedResource* candidate =
            try_exact_identity(FableDirectFindTextureIdentityByBase(texture_base))) {
      return candidate;
    }
    if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
            FableDirectFindTextureIdentityObjectsByBase(texture_base),
            try_exact_identity)) {
      return candidate;
    }
    const std::array<uint32_t, 6> texture_fetch =
        PlumeGuestTextureFetchDwords(*texture);
    if (HasTrackedFetchDwords(texture_fetch)) {
      if (PlumeTrackedResource* candidate = try_exact_identity(
              FableDirectFindTextureIdentityByFetch(texture_fetch))) {
        return candidate;
      }
      if (PlumeTrackedResource* candidate = TryDirectTitleUploadIdentityObjects(
              FableDirectFindTextureIdentityObjectsByFetch(texture_fetch),
              try_exact_identity)) {
        return candidate;
      }
    }
  }

  return nullptr;
}

bool DirectResourceAddressMatchesLocked(uint32_t resource_address,
                                        uint32_t address) {
  if (resource_address == 0 || address == 0) {
    return false;
  }
  if (resource_address == address) {
    return true;
  }
  if (const PlumeTrackedResource* resource =
          FindDirectResourceLocked(resource_address)) {
    if (DirectResourceMatchesAddress(*resource, address)) {
      return true;
    }
  }
  if (const PlumeTrackedResource* resource = FindDirectResourceLocked(address)) {
    return DirectResourceMatchesAddress(*resource, resource_address);
  }
  return false;
}

const plume::RenderTexture* DirectResourceNativeTexture(
    const PlumeTrackedResource& resource) {
  if (resource.guest_texture.texture) {
    return resource.guest_texture.texture.get();
  }
  if (resource.guest_surface.texture) {
    return resource.guest_surface.texture;
  }
  return nullptr;
}
bool DirectResourceAddressMatchesCommandTargetLocked(
    const FableDirectRenderCommand& command, uint32_t address) {
  if (address == 0) {
    return false;
  }
  for (uint32_t target : command.resources.render_targets) {
    if (DirectResourceAddressMatchesLocked(address, target)) {
      return true;
    }
  }
  return DirectResourceAddressMatchesLocked(address,
                                           command.resources.depth_stencil);
}
bool DirectCommandBindsExactResourceAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource) {
  auto exact_target_match = [&](uint32_t target) {
    if (target == 0) {
      return false;
    }

    if (target == resource.guest_address || target == resource.parent_address ||
        target == resource.guest_texture.object_address ||
        target == resource.guest_texture.parent_address) {
      return true;
    }

    const auto target_it = g_direct_renderer.resources.find(target);
    if (target_it == g_direct_renderer.resources.end()) {
      return false;
    }

    const PlumeTrackedResource& target_resource = target_it->second;
    if (&target_resource == &resource ||
        target_resource.guest_address == resource.guest_address ||
        (target_resource.parent_address != 0 &&
         target_resource.parent_address == resource.guest_address) ||
        (resource.parent_address != 0 &&
         target_resource.guest_address == resource.parent_address) ||
        (resource.parent_address != 0 &&
         target_resource.parent_address == resource.parent_address)) {
      return true;
    }

    return resource.guest_texture.texture &&
           target_resource.guest_texture.texture &&
           resource.guest_texture.texture.get() ==
               target_resource.guest_texture.texture.get();
  };

  for (uint32_t target : command.resources.render_targets) {
    if (exact_target_match(target)) {
      return true;
    }
  }
  return exact_target_match(command.resources.depth_stencil);
}

bool DirectCommandBindsNativeImageAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource) {
  auto exact_native_target_match = [&](uint32_t target) {
    if (target == 0) {
      return false;
    }

    if (target == resource.guest_address ||
        target == resource.guest_texture.object_address ||
        target == resource.guest_surface.object_address) {
      return true;
    }

    const auto target_it = g_direct_renderer.resources.find(target);
    if (target_it == g_direct_renderer.resources.end()) {
      return false;
    }

    const PlumeTrackedResource& target_resource = target_it->second;
    if (&target_resource == &resource ||
        target_resource.guest_address == resource.guest_address ||
        target_resource.guest_texture.object_address ==
            resource.guest_texture.object_address ||
        target_resource.guest_surface.object_address ==
            resource.guest_surface.object_address) {
      return true;
    }

    const plume::RenderTexture* resource_texture =
        DirectResourceNativeTexture(resource);
    const plume::RenderTexture* target_texture =
        DirectResourceNativeTexture(target_resource);
    return resource_texture && target_texture &&
           resource_texture == target_texture;
  };

  for (uint32_t target : command.resources.render_targets) {
    if (exact_native_target_match(target)) {
      return true;
    }
  }
  return exact_native_target_match(command.resources.depth_stencil);
}

bool DirectCommandBindsResourceAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource) {
  const bool plume_private_feedback_alias =
      resource.plume_owned &&
      (resource.guest_address & 0xFF000000u) == 0xF1000000u &&
      !resource.texture_render_target && !resource.texture_depth_target;
  if (plume_private_feedback_alias) {
    for (uint32_t target : command.resources.render_targets) {
      if (target != 0 && target == resource.guest_address) {
        return true;
      }
    }
    return command.resources.depth_stencil != 0 &&
           command.resources.depth_stencil == resource.guest_address;
  }

  // Only the target the draw actually writes counts. Higher RT slots carry
  // STALE device state from earlier passes (a GUI text surface stays in an
  // MRT slot after its compose pass ends), and treating those as feedback
  // hazards nulls legitimate sample binds (missing Press-A glyph: text
  // surface 0x40513C10 ready but "bound" via a stale slot). Fable draws
  // write RT0 only.
  const uint32_t written_target = command.resources.render_targets[0];
  if (DirectResourceAddressMatchesLocked(resource.guest_address,
                                         written_target) ||
      DirectResourceAddressMatchesLocked(resource.parent_address,
                                         written_target) ||
      DirectResourceAddressMatchesLocked(
          PlumeGuestTextureBaseAddress(resource), written_target)) {
    // Xenos: the bound color target is EDRAM storage; a main-memory texture
    // record whose base merely aliases it is a DIFFERENT physical store, and
    // the GUI ping-pong compose legitimately samples it while rendering
    // (missing Press-A glyph: 0x40513C10 rejected against its 0x404DAxxx
    // surface twin). In our model the real hazard is a shared HOST image —
    // distinct host images can never feed back.
    bool distinct_host_images = false;
    if (written_target != resource.guest_address) {
      const PlumeTrackedResource* rt_record =
          FindDirectResourceLocked(written_target);
      if (rt_record != nullptr && rt_record != &resource) {
        const plume::RenderTexture* rt_native =
            rt_record->guest_texture.texture
                ? rt_record->guest_texture.texture.get()
                : rt_record->guest_surface.texture;
        const plume::RenderTexture* sampled_native =
            resource.guest_texture.texture
                ? resource.guest_texture.texture.get()
                : resource.guest_surface.texture;
        distinct_host_images = rt_native != nullptr &&
                               sampled_native != nullptr &&
                               rt_native != sampled_native;
      }
    }
    if (!distinct_host_images) {
      return true;
    }
  }

  const uint32_t bound_depth = command.resources.depth_stencil;
  if (DirectResourceAddressMatchesLocked(resource.guest_address, bound_depth) ||
      DirectResourceAddressMatchesLocked(resource.parent_address,
                                         bound_depth) ||
      DirectResourceAddressMatchesLocked(PlumeGuestTextureBaseAddress(resource),
                                         bound_depth)) {
    // Same Xenos store distinction as RT0 above: bound depth is EDRAM; a
    // main-memory texture record aliasing its base is a different physical
    // store (shadow-map sampling with the stale depth surface still bound —
    // post-title scene draws, slot 13). A shared HOST image is the only
    // real feedback hazard.
    if (bound_depth != resource.guest_address) {
      const PlumeTrackedResource* depth_record =
          FindDirectResourceLocked(bound_depth);
      if (depth_record != nullptr && depth_record != &resource) {
        const plume::RenderTexture* depth_native =
            depth_record->guest_texture.texture
                ? depth_record->guest_texture.texture.get()
                : depth_record->guest_surface.texture;
        const plume::RenderTexture* sampled_native =
            resource.guest_texture.texture
                ? resource.guest_texture.texture.get()
                : resource.guest_surface.texture;
        if (depth_native != nullptr && sampled_native != nullptr &&
            depth_native != sampled_native) {
          return false;
        }
      }
    }
    return true;
  }
  return false;
}

bool DirectCommandWritesSurfaceAliasSourceLocked(
    const FableDirectRenderCommand& command,
    const RendererSurfaceCopyAliasSnapshot& alias) {
  if (alias.source_surface == 0) {
    return false;
  }
  for (uint32_t target : command.resources.render_targets) {
    if (DirectResourceAddressMatchesLocked(alias.source_surface, target)) {
      return true;
    }
  }
  return DirectResourceAddressMatchesLocked(alias.source_surface,
                                           command.resources.depth_stencil);
}

// ZERO-CALLER DirectResourceIsUnbackedTextureProxy — DELETED 2026-07-26.
// A predicate for "ready image, not a target, no upload, no base address, but
// nonzero extent". Compiler-verified unused (-Wunused-function) plus
// grep-verified: only its forward declaration and definition existed, both in
// this file, both removed. Not an overload set. NOTE the differently-named
// FableTrackedResourceIsUnbackedSampleProxy is a DIFFERENT, live predicate.
bool FablePrivateTextureProxyRoleAllowsTarget(
    FablePrivateTextureProxyRole role,
    const PlumeTrackedResource& target) {
  switch (role) {
    case FablePrivateTextureProxyRole::kDepth:
      return target.texture_depth_target;
    case FablePrivateTextureProxyRole::kColor:
      return target.texture_render_target;
    case FablePrivateTextureProxyRole::kUnknown:
      return true;
  }
  return true;
}

FableTitleResourceFacts DirectTitleResourceFactsForResource(
    const PlumeTrackedResource& resource,
    const FableTextureIdentity* identity) {
  return FableTitleResourceFactsForTrackedResource(
      resource, FableTitleTrackedResourceFactInputs{
                    .identity = identity,
                    .sampleable_content =
                        FableTrackedResourceHasSampleableContent(resource),
                    .visible_content_serial =
                        FableTrackedResourceVisibleContentSerial(resource),
                });
}

bool DirectResourceUploadDefinesTextureIdentity(
    const PlumeTrackedResource& resource) {
  return FableTitleResourceUploadDefinesTextureIdentity(
      DirectTitleResourceFactsForResource(resource));
}

bool DirectExactTitleUploadedResourceHasAuthoredPayload(
    const PlumeTrackedResource& resource) {
  const FableTextureIdentity* identity =
      FableDirectFindTextureIdentityByObject(resource.guest_address);
  return FableTitleExactUploadedResourceHasAuthoredPayload(
      DirectTitleResourceFactsForResource(resource, identity));
}

bool DirectResourceHasTextureSampleContentForExactProducer(
    const PlumeTrackedResource& resource) {
  const FableTextureIdentity* identity =
      FableDirectFindTextureIdentityByObject(resource.guest_address);
  return FableTitleResourceHasTextureSampleContentForExactProducer(
      DirectTitleResourceFactsForResource(resource, identity));
}

bool DirectFableTitleBillboardAtlasHasAuthoritativeUpload(
    const PlumeTrackedResource& resource) {
  FableTitleResourceFacts facts = DirectTitleResourceFactsForResource(resource);
  facts.texture_sample_content_for_exact_producer =
      DirectResourceHasTextureSampleContentForExactProducer(resource);
  return FableTitleBillboardAtlasHasAuthoritativeUpload(facts);
}

bool DirectFableTitleIndirectionHasAuthoritativeUpload(
    const PlumeTrackedResource& resource) {
  return FableTitleIndirectionHasAuthoritativeUpload(
      DirectTitleResourceFactsForResource(resource));
}

bool DirectResourceIsPersistentPrivateSampleBacking(
    const PlumeTrackedResource& resource) {
  return FableTrackedResourceHasMeaningfulTextureUpload(resource) ||
         DirectResourceIsPrivateFeedbackAlias(resource) ||
         (resource.plume_owned && !resource.texture_render_target &&
          !resource.texture_depth_target &&
          FableTrackedResourceHasSampleableContent(resource));
}

bool DirectTinyTitleCachedTextureCanBindLocked(
    const FableDirectRenderCommand& command, uint32_t guest,
    const PlumeTrackedResource* texture) {
  if (!texture || !IsImageResource(texture->kind) ||
      !PlumeGuestImageReady(*texture) || texture->texture_render_target ||
      texture->texture_depth_target ||
      DirectCommandBindsExactResourceAsTargetLocked(command, *texture) ||
      FableTrackedResourceIsBlankUploadedSampleProxy(*texture) ||
      (!IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) &&
       !IsFableTitleSceneVertexShaderCommandLocked(command) &&
       !IsFableFinalTitlePrivateIndirectionCommandLocked(command) &&
       !IsFableTitleSceneTextureSyncCommandLocked(command))) {
    return false;
  }

  const FableTextureIdentity* guest_identity =
      FableDirectFindTextureIdentityByObject(guest);
  const FableTextureIdentity* texture_identity =
      FableDirectFindTextureIdentityByObject(texture->guest_address);
  return FableTinyTitleCachedTextureHasUploadIdentity(
      guest, guest_identity,
      DirectTitleResourceFactsForResource(*texture, texture_identity));
}

bool DirectTinyTitleResourceMatchesFetchInfo(
    const DirectTextureFetchInfo& fetch_info,
    const PlumeTrackedResource* texture) {
  return texture != nullptr &&
         FableTinyTitleResourceMatchesFetchInfo(
             FableTinyTitleResourceFetchMatchFacts{
                 .fetch_valid = fetch_info.valid,
                 .fetch_base_address = fetch_info.base_address,
                 .fetch_width = fetch_info.width,
                 .fetch_height = fetch_info.height,
                 .fetch_format = fetch_info.format,
                 .resource_image_ready =
                     IsImageResource(texture->kind) &&
                     PlumeGuestImageReady(*texture),
                 .resource_base_address = PlumeGuestTextureBaseAddress(*texture),
                 .resource_width = PlumeGuestTextureWidth(*texture),
                 .resource_height = PlumeGuestTextureHeight(*texture),
                 .resource_guest_format =
                     PlumeGuestTextureGuestFormat(*texture),
                 .resource_render_format = static_cast<uint32_t>(
                     PlumeGuestTextureFormat(*texture)),
             });
}

PlumeTrackedResource* ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* skip_texture) {
  if (slot >= command.resources.textures.size()) {
    return nullptr;
  }

  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(
          DirectCommandTextureFetchDwords(command, slot));
  if (!command_fetch_info.valid ||
      !IsFableTinyTitleUploadedTextureIdentityBase(
          command_fetch_info.base_address, command_fetch_info.width,
          command_fetch_info.height)) {
    return nullptr;
  }

  const uint32_t guest = command.resources.textures[slot];
  auto bindable_exact_tiny_upload =
      [&](PlumeTrackedResource* candidate) -> PlumeTrackedResource* {
    return DirectTinyTitleResourceMatchesFetchInfo(command_fetch_info, candidate) &&
                   DirectTinyTitleCachedTextureCanBindLocked(command, guest,
                                                             candidate)
               ? candidate
               : nullptr;
  };

  if (PlumeTrackedResource* sample =
          bindable_exact_tiny_upload(FindDirectResourceLocked(guest))) {
    return sample;
  }
  if (PlumeTrackedResource* sample =
          bindable_exact_tiny_upload(
              skip_texture ? FindDirectResourceLocked(skip_texture->guest_address)
                           : nullptr)) {
    return sample;
  }

  auto bindable_identity = [&](const FableTextureIdentity* identity)
      -> PlumeTrackedResource* {
    if (!IsFableTinyTitleUploadedTextureIdentity(0, identity)) {
      return nullptr;
    }
    const uint32_t identity_addresses[] = {
        identity->object_texture, identity->parent_texture,
        identity->base_address};
    for (uint32_t address : identity_addresses) {
      if (PlumeTrackedResource* sample =
              bindable_exact_tiny_upload(FindDirectResourceLocked(address))) {
        return sample;
      }
    }
    return nullptr;
  };

  if (PlumeTrackedResource* sample =
          bindable_identity(FableDirectFindTextureIdentityByBase(
              command_fetch_info.base_address))) {
    return sample;
  }
  if (PlumeTrackedResource* sample =
          bindable_identity(FableDirectFindTextureIdentityByFetch(
              DirectCommandTextureFetchDwords(command, slot)))) {
    return sample;
  }
  if (const std::vector<uint32_t>* base_objects =
          FableDirectFindTextureIdentityObjectsByBase(
              command_fetch_info.base_address)) {
    for (uint32_t object : *base_objects) {
      if (PlumeTrackedResource* sample = bindable_identity(
              FableDirectFindTextureIdentityByObject(object))) {
        return sample;
      }
    }
  }
  if (const std::vector<uint32_t>* fetch_objects =
          FableDirectFindTextureIdentityObjectsByFetch(
              DirectCommandTextureFetchDwords(command, slot))) {
    for (uint32_t object : *fetch_objects) {
      if (PlumeTrackedResource* sample = bindable_identity(
              FableDirectFindTextureIdentityByObject(object))) {
        return sample;
      }
    }
  }

  const FableTextureHeaderAction* action =
      FableDirectFindTextureHeaderActionForExactFetchIdentity(
          command_fetch_info.base_address, command_fetch_info.width,
          command_fetch_info.height, command_fetch_info.format,
          DirectCommandTextureFetchDwords(command, slot));
  if (action && action->serial != 0 && action->call_serial != 0 &&
      action->context_key != 0 && action->symbol_address != 0 &&
      !action->stack_scratch &&
      DirectTextureFetchDwordsMatch(action->fetch_dwords,
                                    DirectCommandTextureFetchDwords(command,
                                                                    slot)) &&
      action->base_address == command_fetch_info.base_address &&
      action->width == command_fetch_info.width &&
      action->height == command_fetch_info.height &&
      action->format == command_fetch_info.format) {
    const uint32_t action_addresses[] = {
        action->texture_object, action->parent_texture, action->texture_header,
        action->base_address};
    for (uint32_t address : action_addresses) {
      if (PlumeTrackedResource* sample =
              bindable_exact_tiny_upload(FindDirectResourceLocked(address))) {
        return sample;
      }
    }
  }

  // DEAD ADDRESS GATE tiny_title_16x16_upload_fallback — DELETED 2026-07-26.
  // A last-resort branch fired on fetch base 0x1A0D7000 at 16x16 and then tried
  // three one-run object pointers (0x404DE490/4D0/550) as bind sources. Run 288
  // census[final] (frame=2952) lists it under tested_but_never_matched: the
  // branch condition itself never became true across the whole run, so the
  // three literals were never even consulted. Deleted; the resolver's real
  // identity/fetch/header paths above are unchanged.

  return nullptr;
}

bool IsFablePrivateTextureChainExtent(uint32_t width, uint32_t height) {
  return (width == 256u && height == 256u) ||
         (width == 280u && height == 180u) ||
         (width == 320u && height == 180u) ||
         (width == 480u && height == 270u) ||
         (width == 560u && height == 360u) ||
         (width == 960u && height == 540u);
}

bool IsFablePrivateTextureChainResource(
    const PlumeTrackedResource& resource) {
  if (IsFableTitlePrivateTextureProxyAddress(resource.guest_address) ||
      IsFableTitlePrivateTextureProxyAddress(
          PlumeGuestTextureBaseAddress(resource))) {
    return true;
  }
  if (!IsImageResource(resource.kind) || !PlumeGuestImageReady(resource)) {
    return false;
  }

  const uint32_t width = PlumeGuestTextureWidth(resource);
  const uint32_t height = PlumeGuestTextureHeight(resource);
  if (!IsFablePrivateTextureChainExtent(width, height)) {
    return false;
  }

  if (width == 256u && height == 256u) {
    return FableTrackedResourceIsUnbackedSampleProxy(resource);
  }

  return resource.texture_render_target || resource.texture_depth_target ||
         FableTrackedResourceHasGpuProducedContent(resource) ||
         FableTrackedResourceIsUnbackedSampleProxy(resource) ||
         FableTrackedResourceHasMeaningfulTextureUpload(resource);
}

const char* DirectPrivateTextureCopyActionMatchName(
    DirectPrivateTextureCopyActionMatch match) {
  switch (match) {
    case DirectPrivateTextureCopyActionMatch::kNone:
      return "none";
    case DirectPrivateTextureCopyActionMatch::kExactDestination:
      return "exact-destination";
    case DirectPrivateTextureCopyActionMatch::kExactTrackedDestination:
      return "exact-tracked-destination";
    case DirectPrivateTextureCopyActionMatch::kExactParentDestination:
      return "exact-parent-destination";
    case DirectPrivateTextureCopyActionMatch::kExactIdentityDestination:
      return "exact-identity-destination";
    case DirectPrivateTextureCopyActionMatch::kExactSurfaceTextureLink:
      return "exact-surface-link";
    case DirectPrivateTextureCopyActionMatch::kExactCurrentColorAction:
      return "exact-current-color-action";
  }
  return "unknown";
}

void CountDirectPrivateTextureCopyActionMatchLocked(
    DirectPrivateTextureCopyActionMatch match) {
  switch (match) {
    case DirectPrivateTextureCopyActionMatch::kExactDestination:
    case DirectPrivateTextureCopyActionMatch::kExactTrackedDestination:
    case DirectPrivateTextureCopyActionMatch::kExactParentDestination:
    case DirectPrivateTextureCopyActionMatch::kExactIdentityDestination:
    case DirectPrivateTextureCopyActionMatch::kExactSurfaceTextureLink:
    case DirectPrivateTextureCopyActionMatch::kExactCurrentColorAction:
      ++g_direct_renderer.frame_private_copy_exact_matches;
      break;
    case DirectPrivateTextureCopyActionMatch::kNone:
      break;
  }
}

bool NearlyEqualPrivateTextureActionFloat(float lhs, float rhs, float epsilon) {
  return std::fabs(lhs - rhs) <= epsilon;
}

bool DirectPrivateTextureCopyActionHasExactWrapperIdentity(
    const FablePrivateTextureCopyAction& action) {
  return action.wrapper_call_serial != 0 || action.wrapper_context_key != 0 ||
         action.wrapper_symbol_address != 0;
}

void PopulateDirectPrivateTextureCopyShaderRoots(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command) {
  const FableDirectShaderRoots& roots = command.resources.shader_roots;
  action.vertex_shader_root_call_serial =
      roots.vertex_shader.call_serial;
  action.vertex_shader_root_context_key =
      roots.vertex_shader.context_key;
  action.vertex_shader_root_symbol_address =
      roots.vertex_shader.symbol_address;
  action.vertex_shader_root_lr = roots.vertex_shader.lr;
  action.pixel_shader_root_call_serial =
      roots.pixel_shader.call_serial;
  action.pixel_shader_root_context_key =
      roots.pixel_shader.context_key;
  action.pixel_shader_root_symbol_address =
      roots.pixel_shader.symbol_address;
  action.pixel_shader_root_lr = roots.pixel_shader.lr;
}

void PopulateDirectPrivateTextureCopyRectangleFromCommand(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command) {
  action.rectangle_valid =
      command.draw.private_texture_rectangle &&
      command.draw.private_texture_corners_valid;
  action.rect_width = 0.0f;
  action.rect_height = 0.0f;
  if (action.rectangle_valid) {
    const auto& corners = command.draw.private_texture_corners;
    action.rect_width = std::fabs(corners[2] - corners[0]);
    action.rect_height = std::fabs(corners[5] - corners[1]);
  }
  action.source_x = command.draw.private_texture_source_x;
  action.source_y = command.draw.private_texture_source_y;
  action.uv_override = command.draw.private_texture_uv_override;
  action.u0 = command.draw.private_texture_u0;
  action.v0 = command.draw.private_texture_v0;
  action.u1 = command.draw.private_texture_u1;
  action.v1 = command.draw.private_texture_v1;
}

void PopulateDirectSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  link.vertex_shader_root_call_serial =
      action.vertex_shader_root_call_serial;
  link.vertex_shader_root_context_key =
      action.vertex_shader_root_context_key;
  link.vertex_shader_root_symbol_address =
      action.vertex_shader_root_symbol_address;
  link.vertex_shader_root_lr = action.vertex_shader_root_lr;
  link.pixel_shader_root_call_serial =
      action.pixel_shader_root_call_serial;
  link.pixel_shader_root_context_key =
      action.pixel_shader_root_context_key;
  link.pixel_shader_root_symbol_address =
      action.pixel_shader_root_symbol_address;
  link.pixel_shader_root_lr = action.pixel_shader_root_lr;
}

void PopulateDirectSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& link,
    const FableDirectRenderCommand& command) {
  const FableDirectShaderRoots& roots = command.resources.shader_roots;
  link.vertex_shader_root_call_serial =
      roots.vertex_shader.call_serial;
  link.vertex_shader_root_context_key =
      roots.vertex_shader.context_key;
  link.vertex_shader_root_symbol_address =
      roots.vertex_shader.symbol_address;
  link.vertex_shader_root_lr = roots.vertex_shader.lr;
  link.pixel_shader_root_call_serial =
      roots.pixel_shader.call_serial;
  link.pixel_shader_root_context_key =
      roots.pixel_shader.context_key;
  link.pixel_shader_root_symbol_address =
      roots.pixel_shader.symbol_address;
  link.pixel_shader_root_lr = roots.pixel_shader.lr;
}


bool DirectPrivateTextureCopyActionWrapperMatchesCommand(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action) {
  if (!DirectPrivateTextureCopyActionHasExactWrapperIdentity(action)) {
    return false;
  }

  const bool command_has_wrapper_identity =
      command.draw.private_texture_wrapper_call_serial != 0 ||
      command.draw.private_texture_wrapper_context_key != 0 ||
      command.draw.private_texture_wrapper_symbol_address != 0;
  if (!command_has_wrapper_identity) {
    return false;
  }

  if (action.wrapper_call_serial != 0 &&
      command.draw.private_texture_wrapper_call_serial != 0 &&
      action.wrapper_call_serial !=
          command.draw.private_texture_wrapper_call_serial) {
    return false;
  }
  if (action.wrapper_context_key != 0 &&
      command.draw.private_texture_wrapper_context_key != 0 &&
      action.wrapper_context_key !=
          command.draw.private_texture_wrapper_context_key) {
    return false;
  }
  if (action.wrapper_symbol_address != 0 &&
      command.draw.private_texture_wrapper_symbol_address != 0 &&
      !FablePrivateRectWrapperSymbolsCompatible(
          action.wrapper_symbol_address,
          command.draw.private_texture_wrapper_symbol_address)) {
    return false;
  }
  return true;
}

bool DirectPrivateTextureCopyActionSourceIsTitleChainLocked(
    const FablePrivateTextureCopyAction& action) {
  if (IsFableTitlePrivateTextureProxyAddress(action.source_texture) ||
      IsFableTitlePrivateTextureProxyAddress(action.source_base_address)) {
    return true;
  }

  if (const PlumeTrackedResource* source =
          FindDirectResourceLocked(action.source_texture)) {
    if (IsFablePrivateTextureChainResource(*source)) {
      return true;
    }
  }

  return IsFablePrivateTextureChainExtent(action.source_width,
                                         action.source_height) &&
         action.source_format != 0;
}

bool DirectCommandTextureSlotHasPrivateTitleIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture) {
  const uint32_t guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  const std::array<uint32_t, 6> command_fetch =
      DirectCommandTextureFetchDwords(command, slot);
  const DirectTextureFetchInfo command_fetch_info =
      DirectTextureFetchInfoFromDwords(command_fetch);
  return IsFableTitlePrivateTextureProxyAddress(guest) ||
         IsFableMutablePrivateTextureHeaderAddress(guest) ||
         (command_fetch_info.valid &&
          (IsFableTitlePrivateTextureProxyAddress(
               command_fetch_info.base_address) ||
           IsFableMutablePrivateTextureHeaderAddress(
               command_fetch_info.base_address))) ||
         (texture &&
          (IsFableTitlePrivateTextureProxyAddress(texture->guest_address) ||
           IsFableMutablePrivateTextureHeaderAddress(texture->guest_address) ||
           IsFableTitlePrivateTextureProxyAddress(texture->parent_address) ||
           IsFableMutablePrivateTextureHeaderAddress(texture->parent_address) ||
           IsFableTitlePrivateTextureProxyAddress(
               PlumeGuestTextureBaseAddress(*texture)) ||
           IsFableMutablePrivateTextureHeaderAddress(
               PlumeGuestTextureBaseAddress(*texture))));
}

bool DirectCommandRequiresExactPrivateTitleCopyActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, bool prefer_fetch_actions) {
  if (command.draw.source == RendererDrawEventSource::kPixVideo) {
    return false;
  }
  if (DirectCommandSlotUsesExactBinkWrapperPlaneLocked(command, slot)) {
    return true;
  }

  const uint32_t texture_slot_mask =
      DirectCommandPixelTextureSlotMaskLocked(command);
  if (texture_slot_mask != 0u &&
      !DirectCommandUsesTextureSlotForSyncLocked(command, texture_slot_mask,
                                                 slot)) {
    return false;
  }

  const uint32_t guest =
      slot < command.resources.textures.size() ? command.resources.textures[slot]
                                               : 0u;
  const bool private_header_identity =
      DirectCommandTextureSlotHasPrivateTitleIdentityLocked(command, slot,
                                                            texture);

  return DirectCommandUsesFablePrivateTexturePath(command, slot, texture) ||
         DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(command) ||
         IsFableTitleSceneTextureSyncCommandLocked(command) ||
         DirectPrivateTextureHeaderNeedsAlias(command, slot, texture) ||
         private_header_identity || prefer_fetch_actions;
}

// ZERO-CALLER DirectCommandHasPendingSameCommandPrivateTitleProducerLocked —
// DELETED 2026-07-26. It classified a draw as a same-command private-title
// producer (title target/shader + private-title identity + exact wrapper
// identity + a distinct ready color target). Compiler-verified unused
// (-Wunused-function) plus grep-verified: exactly ONE occurrence repo-wide,
// the definition, with no forward declaration and no overloads.
bool DirectPrivateTextureCopyActionMatchesTitleScaleUpLocked(
    const FableDirectRenderCommand& /*command*/,
    const FablePrivateTextureCopyAction& /*action*/, float /*rect_width*/,
    float /*rect_height*/) {
  // RETIRED 2026-07-24 (native-rendering + GTL draw ownership). This was a
  // whitelist-style heuristic: it matched a specific 480x276 half-title private
  // copy scaled x2 to a 960x540 title target using hardcoded magic dimension
  // bands (400-520 x 250-290 source, 900-1000 x 520-560 target). It existed only
  // because the old path composed the title at half resolution and needed to
  // recognise the scale-up. Under native 1280x720 rendering there is no half-
  // title scale-up at all, so it matched 0 times (run 212 census). The decision
  // logic is deleted per the no-whitelists rule; the call plumbing at its ~5
  // callers is left returning false (unchanged behaviour) until the private-rect
  // class is GTL-owned and the callers can be removed wholesale. See
  // [[refii-no-whitelists-principle]], [[refii-gtl-draw-ownership]].
  return false;
}

bool DirectPrivateTextureCopyActionMatchesTitleScaleDownLocked(
    const FableDirectRenderCommand& /*command*/,
    const FablePrivateTextureCopyAction& /*action*/, float /*rect_width*/,
    float /*rect_height*/) {
  // RETIRED 2026-07-24 — scale-down twin of the scale-up heuristic above (title
  // target 960x540 down to a 480x276 half-title, hardcoded magic bands). Same
  // rationale: obsolete under native 1280x720 rendering, 0 matches in run 212,
  // decision logic deleted per no-whitelists, callers left returning false until
  // the private-rect class is GTL-owned. See [[refii-gtl-draw-ownership]].
  return false;
}

bool DirectPrivateTextureCopyActionIsTitleScaleUpLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action) {
  if (!command.draw.private_texture_corners_valid) {
    return false;
  }
  const auto& corners = command.draw.private_texture_corners;
  const float rect_width = std::fabs(corners[2] - corners[0]);
  const float rect_height = std::fabs(corners[5] - corners[1]);
  return DirectPrivateTextureCopyActionMatchesTitleScaleUpLocked(
      command, action, rect_width, rect_height);
}

bool DirectPrivateTextureCopyActionIsTitleScaleDownLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action) {
  if (!command.draw.private_texture_corners_valid) {
    return false;
  }
  const auto& corners = command.draw.private_texture_corners;
  const float rect_width = std::fabs(corners[2] - corners[0]);
  const float rect_height = std::fabs(corners[5] - corners[1]);
  return DirectPrivateTextureCopyActionMatchesTitleScaleDownLocked(
      command, action, rect_width, rect_height);
}

bool DirectPrivateTextureCopyActionTargetMatchesTitleHalfTargetLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action, uint32_t target_width,
    uint32_t target_height) {
  if ((action.kind != FablePrivateTextureCopyKind::kColor &&
       action.kind != FablePrivateTextureCopyKind::kColorSibling) ||
      action.target_width == 0 || action.target_height == 0 ||
      target_width == 0 || target_height == 0 ||
      !IsFableTitleSceneTextureSyncCommandLocked(command) ||
      !DirectPrivateTextureCopyActionSourceIsTitleChainLocked(action) ||
      !IsFablePrivateTextureChainExtent(action.target_width,
                                        action.target_height) ||
      !IsFablePrivateTextureChainExtent(target_width, target_height)) {
    return false;
  }

  return action.target_width == target_width * 2u &&
         action.target_height == target_height * 2u;
}

bool DirectPrivateTextureCopyActionRectangleMatchesTitleClampLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action, float rect_width,
    float rect_height) {
  if (DirectPrivateTextureCopyActionMatchesTitleScaleUpLocked(
          command, action, rect_width, rect_height) ||
      DirectPrivateTextureCopyActionMatchesTitleScaleDownLocked(
          command, action, rect_width, rect_height)) {
    return true;
  }

  const PlumeTrackedResource* target =
      FindDirectResourceLocked(action.target_surface);
  if (!target || !IsImageResource(target->kind) ||
      !PlumeGuestImageReady(*target)) {
    return false;
  }

  const uint32_t target_width = PlumeGuestTextureWidth(*target);
  const uint32_t target_height = PlumeGuestTextureHeight(*target);
  if (!IsFablePrivateTextureChainExtent(target_width, target_height)) {
    return false;
  }

  const bool title_or_private_copy_context =
      IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
      IsFableTitleSceneTargetLocked(action.render_target) ||
      IsFableTitleSceneTargetLocked(action.target_surface) ||
      (command.draw.private_texture_rectangle &&
       IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader));
  if (!title_or_private_copy_context ||
      !DirectPrivateTextureCopyActionWrapperMatchesCommand(command, action)) {
    return false;
  }

  const float target_width_float = static_cast<float>(target_width);
  const float target_height_float = static_cast<float>(target_height);
  const bool width_matches =
      NearlyEqualPrivateTextureActionFloat(rect_width, action.rect_width,
                                           1.0f) &&
      NearlyEqualPrivateTextureActionFloat(rect_width, target_width_float,
                                           1.0f);
  if (!width_matches) {
    return false;
  }

  bool clamp_match =
      NearlyEqualPrivateTextureActionFloat(rect_height, action.rect_height,
                                           1.0f);

  const bool command_matches_target =
      NearlyEqualPrivateTextureActionFloat(rect_height, target_height_float,
                                           1.0f);
  const bool action_matches_target =
      NearlyEqualPrivateTextureActionFloat(action.rect_height,
                                           target_height_float, 1.0f);
  const float height_delta = std::fabs(rect_height - action.rect_height);
  const float max_title_clamp_delta =
      std::max(8.0f, target_height_float * 0.025f);
  const bool command_near_target =
      std::fabs(rect_height - target_height_float) <= max_title_clamp_delta;
  const bool action_near_target =
      std::fabs(action.rect_height - target_height_float) <=
      max_title_clamp_delta;
  clamp_match =
      clamp_match ||
      (height_delta <= max_title_clamp_delta &&
       (command_matches_target || action_matches_target ||
        (command_near_target && action_near_target)));
  if (!clamp_match) {
    return false;
  }
  return true;
}

bool DirectPrivateTextureCopyActionMatchesPendingTitleClampLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action, float rect_width,
    float rect_height) {
  if (!action.rectangle_valid || !command.draw.private_texture_rectangle ||
      !command.draw.private_texture_corners_valid ||
      !IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
      (action.kind != FablePrivateTextureCopyKind::kColor &&
       action.kind != FablePrivateTextureCopyKind::kColorSibling) ||
      action.frame_index != 0 || action.draw_serial != 0 ||
      action.target_surface == 0 ||
      !DirectPrivateTextureCopyActionWrapperMatchesCommand(command, action)) {
    return false;
  }

  if (command.resources.render_targets[0] != action.target_surface &&
      (action.render_target == 0 ||
       command.resources.render_targets[0] != action.render_target)) {
    return false;
  }

  if (!DirectPrivateTextureCopyActionSourceIsTitleChainLocked(action)) {
    return false;
  }

  const bool action_looks_like_half_title =
      action.rect_width >= 400.0f && action.rect_width <= 520.0f &&
      action.rect_height >= 250.0f && action.rect_height <= 290.0f;
  if (!action_looks_like_half_title) {
    return false;
  }

  if (!NearlyEqualPrivateTextureActionFloat(rect_width, action.rect_width,
                                            1.5f)) {
    return false;
  }

  const float height_delta = std::fabs(rect_height - action.rect_height);
  const float height_tolerance = std::max(8.0f, action.rect_height * 0.025f);
  if (height_delta > height_tolerance) {
    return false;
  }

  if (!NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_source_x,
                                            action.source_x, 1.0f) ||
      !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_source_y,
                                            action.source_y, 1.0f)) {
    return false;
  }

  if (command.draw.private_texture_uv_override) {
    constexpr float kUvEpsilon = 1.0f / 2048.0f;
    if (!NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_u0,
                                              0.0f, kUvEpsilon) ||
        !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_v0,
                                              0.0f, kUvEpsilon) ||
        !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_u1,
                                              1.0f, kUvEpsilon) ||
        !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_v1,
                                              1.0f, kUvEpsilon)) {
      return false;
    }
  }

  static uint32_t logged_exact_title_private_rect_shape_matches = 0;
  if (logged_exact_title_private_rect_shape_matches < 32u) {
    REXLOG_INFO(
        "Refii plume exact title/private rect shape match #{} "
        "frame={} command_draw={} source=0x{:08X} target=0x{:08X} "
        "command_rect={:.2f}x{:.2f} action_rect={}x{} call={} "
        "context=0x{:016X} symbol=0x{:08X} rt=0x{:08X} ps=0x{:08X}",
        ++logged_exact_title_private_rect_shape_matches,
        g_direct_renderer.current_frame_index,
        static_cast<unsigned long long>(command.draw_serial),
        action.source_texture, action.target_surface, rect_width, rect_height,
        action.rect_width, action.rect_height,
        static_cast<unsigned long long>(action.wrapper_call_serial),
        static_cast<unsigned long long>(action.wrapper_context_key),
        action.wrapper_symbol_address, command.resources.render_targets[0],
        command.resources.pixel_shader);
  }
  return true;
}

bool DirectPrivateTextureCopyActionShapeMatches(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action) {
  if (!action.rectangle_valid || !command.draw.private_texture_rectangle) {
    return true;
  }
  if (!command.draw.private_texture_corners_valid) {
    return false;
  }

  const auto& corners = command.draw.private_texture_corners;
  const float rect_width = std::fabs(corners[2] - corners[0]);
  const float rect_height = std::fabs(corners[5] - corners[1]);
  const bool title_clamp_rectangle_match =
      DirectPrivateTextureCopyActionRectangleMatchesTitleClampLocked(
          command, action, rect_width, rect_height);
  const bool pending_title_clamp_rectangle_match =
      DirectPrivateTextureCopyActionMatchesPendingTitleClampLocked(
          command, action, rect_width, rect_height);
  const bool rectangle_matches =
      (NearlyEqualPrivateTextureActionFloat(rect_width, action.rect_width,
                                            1.0f) &&
       NearlyEqualPrivateTextureActionFloat(rect_height, action.rect_height,
                                            1.0f)) ||
      title_clamp_rectangle_match || pending_title_clamp_rectangle_match;
  if (!rectangle_matches) {
    return false;
  }
  if (title_clamp_rectangle_match || pending_title_clamp_rectangle_match) {
    return true;
  }
  if (!NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_source_x,
                                            action.source_x, 1.0f) ||
      !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_source_y,
                                            action.source_y, 1.0f)) {
    return false;
  }
  const bool command_full_texture_uv =
      command.draw.private_texture_uv_override &&
      NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_u0, 0.0f,
                                           1.0f / 2048.0f) &&
      NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_v0, 0.0f,
                                           1.0f / 2048.0f) &&
      NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_u1, 1.0f,
                                           1.0f / 2048.0f) &&
      NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_v1, 1.0f,
                                           1.0f / 2048.0f);
  if (command_full_texture_uv && !action.uv_override &&
      command.draw.private_texture_rectangle &&
      IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader)) {
    static uint32_t logged_exact_full_texture_uv_mismatch_rejects = 0;
    if (logged_exact_full_texture_uv_mismatch_rejects < 32u) {
      REXLOG_INFO(
          "Refii plume exact private rect full-texture UV mismatch reject #{} frame={} "
          "command_draw={} source=0x{:08X} target=0x{:08X} "
          "command_rect={:.2f}x{:.2f} action_rect={}x{} "
          "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
          ++logged_exact_full_texture_uv_mismatch_rejects,
          g_direct_renderer.current_frame_index,
          static_cast<unsigned long long>(command.draw_serial),
          action.source_texture, action.target_surface, rect_width, rect_height,
          action.rect_width, action.rect_height,
          command.resources.render_targets[0], command.resources.depth_stencil,
          command.resources.vertex_shader, command.resources.pixel_shader);
    }
  }
  if (command.draw.private_texture_uv_override != action.uv_override) {
    return false;
  }
  if (action.uv_override) {
    constexpr float kUvEpsilon = 1.0f / 2048.0f;
    if (!NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_u0,
                                              action.u0, kUvEpsilon) ||
        !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_v0,
                                              action.v0, kUvEpsilon) ||
        !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_u1,
                                              action.u1, kUvEpsilon) ||
        !NearlyEqualPrivateTextureActionFloat(command.draw.private_texture_v1,
                                              action.v1, kUvEpsilon)) {
      return false;
    }
  }
  return true;
}

bool DirectPrivateTextureCopyActionCommandSlotMatches(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action) {
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  auto command_binds_private_title_header = [&]() {
    if (command.draw.source == RendererDrawEventSource::kPixVideo) {
      return false;
    }
    for (uint32_t texture : command.resources.textures) {
      if (IsFableMutablePrivateTextureHeaderAddress(texture) ||
          IsFableTitlePrivateTextureProxyAddress(texture)) {
        return true;
      }
    }
    return false;
  };
  const bool private_or_title_context =
      command.draw.source != RendererDrawEventSource::kPixVideo &&
      (command.draw.private_texture_rectangle ||
       IsFableSyntheticPrivateRectCopyShader(command.resources.pixel_shader) ||
       IsFableTitleSceneTargetLocked(command.resources.render_targets[0]) ||
       IsFableTitleSceneVertexShaderCommandLocked(command) ||
       (command.draw.source == RendererDrawEventSource::kSceneObject &&
        command_binds_private_title_header()));
  const bool allow_pix_video_exact_texture_match =
      command.draw.source == RendererDrawEventSource::kPixVideo;
  auto exact_texture_matches_action = [&](uint32_t texture) {
    return texture != 0 &&
           (texture == action.source_texture ||
            texture == action.source_base_address ||
            texture == action.backing_texture ||
            texture == action.backing_base_address);
  };
  auto pix_video_exact_texture_matches_action = [&](uint32_t texture) {
    return texture != 0 &&
           (DirectResourceAddressMatchesLocked(texture, action.source_texture) ||
            DirectResourceAddressMatchesLocked(texture,
                                              action.source_base_address) ||
            DirectResourceAddressMatchesLocked(texture, action.backing_texture) ||
            DirectResourceAddressMatchesLocked(texture,
                                              action.backing_base_address));
  };
  auto fetch_matches_action =
      [&](const std::array<uint32_t, 6>& fetch_dwords) {
        if (!HasTrackedFetchDwords(fetch_dwords)) {
          return false;
        }
        const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
        return fetch_key != 0 &&
               (fetch_key ==
                    FableDirectTextureFetchKey(action.source_fetch_dwords) ||
                fetch_key ==
                    FableDirectTextureFetchKey(action.backing_fetch_dwords));
      };

  if (action.source_slot != kUnknownSlot) {
    if (action.source_slot >= command.resources.textures.size()) {
      return false;
    }
    const uint32_t texture = command.resources.textures[action.source_slot];
    if (exact_texture_matches_action(texture) ||
        fetch_matches_action(
            DirectCommandTextureFetchDwords(command, action.source_slot))) {
      return true;
    }
    if (private_or_title_context) {
      for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
        if (slot == action.source_slot) {
          continue;
        }
        const uint32_t other_texture = command.resources.textures[slot];
        if (exact_texture_matches_action(other_texture) ||
            fetch_matches_action(DirectCommandTextureFetchDwords(command, slot))) {
          static uint32_t logged_exact_cross_slot_action_contexts = 0;
          if (logged_exact_cross_slot_action_contexts < 64u) {
            REXLOG_INFO(
                "Refii plume exact private action context cross-slot #{} "
                "frame={} action_slot={} command_slot={} texture=0x{:08X} "
                "action_src=0x{:08X} base=0x{:08X} target=0x{:08X} "
                "rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
                ++logged_exact_cross_slot_action_contexts,
                g_direct_renderer.current_frame_index, action.source_slot, slot,
                other_texture, action.source_texture, action.source_base_address,
                action.target_surface, command.resources.render_targets[0],
                command.resources.depth_stencil, command.resources.vertex_shader,
                command.resources.pixel_shader);
          }
          return true;
        }
      }
    }
    if (allow_pix_video_exact_texture_match && !private_or_title_context &&
        pix_video_exact_texture_matches_action(texture)) {
      return true;
    }
    return false;
  }

  for (uint32_t slot = 0; slot < command.resources.textures.size(); ++slot) {
    const uint32_t texture = command.resources.textures[slot];
    if (exact_texture_matches_action(texture) ||
        fetch_matches_action(DirectCommandTextureFetchDwords(command, slot))) {
      return true;
    }
    if (allow_pix_video_exact_texture_match && !private_or_title_context &&
        pix_video_exact_texture_matches_action(texture)) {
      return true;
    }
  }
  return false;
}

#endif

}  // namespace refii::renderer
