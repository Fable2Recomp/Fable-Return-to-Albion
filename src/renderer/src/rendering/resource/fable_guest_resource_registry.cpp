#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry_internal.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <mutex>
#include <string>
#include <unordered_set>
#include <utility>

#include <rex/logging.h>

#include "runtime/refii_renderer/gtl/refii_gtl.h"

namespace refii::renderer {




FableGuestResourceRegistry g_fable_guest_resources;
std::recursive_mutex g_fable_guest_resource_registry_mutex;
// Lock-free mirror of registry.surface_destination_relation_generation.
//
// The draw-record alias phase reads this ONCE PER DRAW to decide whether the
// surface-destination scan can be skipped — and taking the registry-wide
// recursive mutex to read one counter cost more than the work it guards. Run 331
// measured that phase (`aliassplit dest=`) at 5.7-34.5ms over ~164 draws/frame,
// tracking every spike in the frame total: the cost was CONTENTION with the
// guest thread registering resources, not the scan itself (omitted=0, ~50
// relations — the walk is trivial).
//
// This mirrors the CONTENT GENERATION, not the raw relation serial. The serial
// bumps on every StoreSurfaceDestinationRelation call, including the dominant
// case: a per-draw link re-registration re-storing a byte-identical relation.
// During the run-479 title attract burst that defeated the per-draw gate on
// every one of 3,280 draws (aliassplit dest=3807.7ms, 1.16ms PER DRAW). The
// generation moves only when a store or repair actually changes stored
// relation bytes, so an unchanged generation proves the snapshot the scan
// would rebuild is byte-identical to the one already processed.
//
// Every writer already holds the mutex, so publishing here keeps the mirror
// consistent while letting readers skip the lock entirely. The guard it feeds is
// inherently racy either way (a relation can be added the instant after the
// generation is read, mutex or not), so a relaxed load is exactly as correct as
// the locked read was — just ~164 uncontended atomic loads per frame instead of
// 164 contended acquisitions of a registry-wide lock.
std::atomic<uint64_t> g_surface_destination_relation_generation_mirror{0};

void PublishSurfaceDestinationRelationGeneration(
    FableGuestResourceRegistry& registry) {
  ++registry.surface_destination_relation_generation;
  g_surface_destination_relation_generation_mirror.store(
      registry.surface_destination_relation_generation,
      std::memory_order_relaxed);
}


double FableGuestResourceRegistryElapsedMs(
    FableGuestResourceRegistryClock::time_point begin,
    FableGuestResourceRegistryClock::time_point end) {
  return std::chrono::duration<double, std::milli>(end - begin).count();
}

bool SurfaceTextureLinkRequiresExactWrapperLookup(
    const FableSurfaceTextureLink& link) {
  switch (link.copy_symbol_address) {
    case 0x8228ACB8u:
    case 0x822C4290u:
    case 0x822C4950u:
      return link.copy_call_serial != 0 || link.copy_context_key != 0;
    default:
      return false;
  }
}

// Feeds the exact-key repair gate (see the header comment on
// surface_texture_link_copy_call_serials_seen). Called at every path that can
// store a link (or a relation carrying a link copy) into the tables:
// StoreSurfaceTextureDirectEdge covers the exact-wrapper branch, backfills and
// the index rebuild; FableDirectRegisterSurfaceTextureLinkInternal covers the
// generic upsert branch. Recording a key for a link that then dedupes into an
// existing entry is a harmless over-approximation.
void NoteSurfaceTextureLinkWrapperIdentitySeen(
    FableGuestResourceRegistry& registry, const FableSurfaceTextureLink& link) {
  if (link.copy_call_serial != 0) {
    registry.surface_texture_link_copy_call_serials_seen.insert(
        link.copy_call_serial);
  }
  if (link.copy_context_key != 0) {
    registry.surface_texture_link_copy_context_keys_seen.insert(
        link.copy_context_key);
  }
}

bool ShaderObjectsEquivalentByIdentity(uint32_t lhs, uint32_t rhs,
                                       FableGuestResourceKind kind) {
  if (lhs == rhs) {
    return true;
  }
  if (lhs == 0 || rhs == 0) {
    return false;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto lhs_it = registry.shader_identities_by_object.find(lhs);
  const auto rhs_it = registry.shader_identities_by_object.find(rhs);
  if (lhs_it == registry.shader_identities_by_object.end() ||
      rhs_it == registry.shader_identities_by_object.end()) {
    return false;
  }

  const FableShaderIdentity& lhs_identity = lhs_it->second;
  const FableShaderIdentity& rhs_identity = rhs_it->second;
  return lhs_identity.kind == kind && rhs_identity.kind == kind &&
         lhs_identity.bytecode_hash != 0 &&
         lhs_identity.bytecode_hash == rhs_identity.bytecode_hash;
}

bool SamePrivateTextureCopyActionIdentity(
    const FablePrivateTextureCopyAction& lhs,
    const FablePrivateTextureCopyAction& rhs) {
  return lhs.source_slot == rhs.source_slot &&
         lhs.source_texture == rhs.source_texture &&
         lhs.source_base_address == rhs.source_base_address &&
         lhs.source_width == rhs.source_width &&
         lhs.source_height == rhs.source_height &&
         lhs.source_format == rhs.source_format &&
         FableDirectTextureFetchKey(lhs.source_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.source_fetch_dwords) &&
         lhs.backing_texture == rhs.backing_texture &&
         lhs.backing_base_address == rhs.backing_base_address &&
         lhs.backing_width == rhs.backing_width &&
         lhs.backing_height == rhs.backing_height &&
         lhs.backing_format == rhs.backing_format &&
         FableDirectTextureFetchKey(lhs.backing_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.backing_fetch_dwords) &&
         lhs.target_surface == rhs.target_surface &&
         lhs.target_width == rhs.target_width &&
         lhs.target_height == rhs.target_height &&
         lhs.target_format == rhs.target_format &&
         lhs.render_target == rhs.render_target &&
         lhs.depth_stencil == rhs.depth_stencil &&
         ShaderObjectsEquivalentByIdentity(
             lhs.vertex_shader, rhs.vertex_shader,
             FableGuestResourceKind::kVertexShader) &&
         ShaderObjectsEquivalentByIdentity(
             lhs.pixel_shader, rhs.pixel_shader,
             FableGuestResourceKind::kPixelShader) &&
         lhs.lr == rhs.lr &&
         lhs.wrapper_symbol_address == rhs.wrapper_symbol_address &&
         lhs.wrapper_context_key == rhs.wrapper_context_key &&
         lhs.wrapper_source_texture == rhs.wrapper_source_texture &&
         lhs.kind == rhs.kind &&
         lhs.rectangle_valid == rhs.rectangle_valid &&
         lhs.uv_override == rhs.uv_override &&
         lhs.rect_width == rhs.rect_width &&
         lhs.rect_height == rhs.rect_height &&
         lhs.source_x == rhs.source_x &&
         lhs.source_y == rhs.source_y &&
         lhs.u0 == rhs.u0 &&
         lhs.v0 == rhs.v0 &&
	         lhs.u1 == rhs.u1 &&
	         lhs.v1 == rhs.v1;
}

// True when re-storing `candidate` over `current` changes nothing any consumer
// keys off. StorePrivateTextureCopyDirectAction re-runs for every texture
// identity backfill (~50 actions per registration at title, ~8 registrations
// per frame — run 473), and each unconditional overwrite bumped
// private_texture_copy_index_serial, which is hashed into the record side's
// action-cache guard (DirectPrivateTextureCopySerialLocked) — so the compose's
// per-draw action cache never survived a frame and every draw re-ran the full
// selection maze. No-op re-stores must not bump; any real change still does.
bool PrivateTextureCopyActionStoreEquivalent(
    const FablePrivateTextureCopyAction& candidate,
    const FablePrivateTextureCopyAction& current) {
  return candidate.serial == current.serial &&
         candidate.frame_index == current.frame_index &&
         candidate.draw_serial == current.draw_serial &&
         candidate.wrapper_call_serial == current.wrapper_call_serial &&
         candidate.backing_content_serial == current.backing_content_serial &&
         candidate.target_content_serial == current.target_content_serial &&
         candidate.sampled_source.content_serial ==
             current.sampled_source.content_serial &&
         SamePrivateTextureCopyActionIdentity(candidate, current);
}




bool SameTextureHeaderAction(const FableTextureHeaderAction& lhs,
                             const FableTextureHeaderAction& rhs) {
  return lhs.call_serial == rhs.call_serial &&
         lhs.context_key == rhs.context_key &&
         lhs.symbol_address == rhs.symbol_address && lhs.lr == rhs.lr &&
         lhs.texture_header == rhs.texture_header &&
         lhs.fetch_address == rhs.fetch_address &&
         lhs.texture_object == rhs.texture_object &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.base_address == rhs.base_address &&
         lhs.byte_size == rhs.byte_size &&
         lhs.width == rhs.width &&
         lhs.height == rhs.height &&
         lhs.format == rhs.format &&
         lhs.stack_scratch == rhs.stack_scratch &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool HasTextureIdentityFetchDwords(
    const std::array<uint32_t, 6>& fetch_dwords) {
  for (uint32_t dword : fetch_dwords) {
    if (dword != 0) {
      return true;
    }
  }
  return false;
}

namespace {
uint32_t NormalizeTextureFetchAddressDword(uint32_t value) {
  return ((((value >> 20u) + 512u) & 0x1000u) +
          (value & 0x1FFFF000u)) |
         (value & 0xFFFu);
}
}  // namespace

std::array<uint32_t, 6> NormalizeTextureFetchKeyDwords(
    std::array<uint32_t, 6> dwords) {
  dwords[1] = NormalizeTextureFetchAddressDword(dwords[1]);
  dwords[5] = NormalizeTextureFetchAddressDword(dwords[5]);
  // Strip the SAMPLER-mutable fields (xe_gpu_texture_fetch_t, xenos.h): they
  // are set per-bind by the D3DSAMP setters and are NOT texture identity.
  // With the sampler-setter hooks removed (2026-07-29) their writes reach the
  // fetch dwords again — as on hardware — and keying on them made every
  // SetTexture look like a new texture (run 416: observe segment 19.7s→31s,
  // exit 6.3s→28.2s of pure memo-miss re-capture churn).
  //   dword_0: clamp_x/y/z            (bits 10..18)
  //   dword_3: mag/min/mip/aniso/arbitrary filter + border_size (bits 19..31)
  //   dword_4: everything EXCEPT mip_min_level/mip_max_level (bits 2..9) —
  //            vol filters, aniso walks, lod_bias, grad_exp_adjust
  //   dword_5: border_color, force_bc_w_to_max, tri_clamp, aniso_bias
  //            (bits 0..8; dimension/packed_mips/mip_address stay)
  dwords[0] &= ~0x0007FC00u;
  dwords[3] &= ~0xFFF80000u;
  dwords[4] &= 0x000003FCu;
  dwords[5] &= ~0x000001FFu;
  return dwords;
}

bool SameTextureIdentity(const FableTextureIdentity& lhs,
                         const FableTextureIdentity& rhs) {
  return lhs.object_texture == rhs.object_texture &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.base_address == rhs.base_address &&
         lhs.byte_size == rhs.byte_size && lhs.width == rhs.width &&
         lhs.height == rhs.height && lhs.format == rhs.format &&
         lhs.upload_generation == rhs.upload_generation &&
         lhs.meaningful_upload == rhs.meaningful_upload &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
		             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool SameTextureIdentityIndexKeys(const FableTextureIdentity& lhs,
                                  const FableTextureIdentity& rhs) {
  return lhs.object_texture == rhs.object_texture &&
         lhs.parent_texture == rhs.parent_texture &&
         lhs.base_address == rhs.base_address &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool SameRegisteredSampleSourceIdentity(
    const FableRegisteredSampleSource& lhs,
    const FableRegisteredSampleSource& rhs) {
  return lhs.kind == rhs.kind && lhs.resource == rhs.resource &&
         lhs.base_address == rhs.base_address &&
         FableDirectTextureFetchKey(lhs.fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.fetch_dwords);
}

bool SurfaceTextureLinkShouldPrefer(const FableSurfaceTextureLink& candidate,
                                    const FableSurfaceTextureLink* current);
bool HasRegisteredSampleSource(
    const FableRegisteredSampleSource& sampled_source);
bool RepairSurfaceTextureLinkSampleIdentityFromAction(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action);
bool RepairSurfaceTextureLinkSampleIdentityFromLink(
    FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link);

bool SameSurfaceTextureLinkIdentity(const FableSurfaceTextureLink& lhs,
                                    const FableSurfaceTextureLink& rhs) {
  return lhs.copy_call_serial == rhs.copy_call_serial &&
         lhs.copy_context_key == rhs.copy_context_key &&
         lhs.copy_symbol_address == rhs.copy_symbol_address &&
         lhs.source_slot == rhs.source_slot &&
         lhs.trusted_destination_edge == rhs.trusted_destination_edge &&
         lhs.texture == rhs.texture &&
         lhs.texture_base_address == rhs.texture_base_address &&
         lhs.texture_width == rhs.texture_width &&
         lhs.texture_height == rhs.texture_height &&
         lhs.texture_format == rhs.texture_format &&
         FableDirectTextureFetchKey(lhs.texture_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.texture_fetch_dwords) &&
         lhs.source_surface == rhs.source_surface &&
         lhs.backing_texture == rhs.backing_texture &&
         lhs.backing_base_address == rhs.backing_base_address &&
         FableDirectTextureFetchKey(lhs.backing_fetch_dwords) ==
             FableDirectTextureFetchKey(rhs.backing_fetch_dwords) &&
         lhs.render_target == rhs.render_target &&
         lhs.depth_stencil == rhs.depth_stencil &&
         ShaderObjectsEquivalentByIdentity(
             lhs.vertex_shader, rhs.vertex_shader,
             FableGuestResourceKind::kVertexShader) &&
         ShaderObjectsEquivalentByIdentity(
             lhs.pixel_shader, rhs.pixel_shader,
             FableGuestResourceKind::kPixelShader) &&
         lhs.wrapper_source_texture == rhs.wrapper_source_texture &&
         lhs.wrapper_source_exact == rhs.wrapper_source_exact &&
         lhs.wrapper_setup_globals == rhs.wrapper_setup_globals &&
         SameRegisteredSampleSourceIdentity(lhs.sampled_source,
                                            rhs.sampled_source) &&
         lhs.kind == rhs.kind;
}

// True when re-storing `candidate` over `current` leaves the stored bytes
// unchanged. This is deliberately FULL-VALUE equality (raw fetch dwords, both
// content serials, frame/draw serials, shader roots), not the normalized
// identity above: the surface-destination scan snapshots relations wholesale,
// so the generation gate that keys off this may only stand still when every
// byte the scan could read is provably identical. Same pattern as
// PrivateTextureCopyActionStoreEquivalent.
bool SurfaceTextureLinkStoreEquivalent(const FableSurfaceTextureLink& lhs,
                                       const FableSurfaceTextureLink& rhs) {
  return lhs.serial == rhs.serial &&
         lhs.copy_call_serial == rhs.copy_call_serial &&
         lhs.copy_context_key == rhs.copy_context_key &&
         lhs.copy_symbol_address == rhs.copy_symbol_address &&
         lhs.source_slot == rhs.source_slot &&
         lhs.trusted_destination_edge == rhs.trusted_destination_edge &&
         lhs.texture == rhs.texture &&
         lhs.texture_base_address == rhs.texture_base_address &&
         lhs.texture_width == rhs.texture_width &&
         lhs.texture_height == rhs.texture_height &&
         lhs.texture_format == rhs.texture_format &&
         lhs.texture_fetch_dwords == rhs.texture_fetch_dwords &&
         lhs.source_surface == rhs.source_surface &&
         lhs.backing_texture == rhs.backing_texture &&
         lhs.backing_base_address == rhs.backing_base_address &&
         lhs.backing_fetch_dwords == rhs.backing_fetch_dwords &&
         lhs.backing_content_serial == rhs.backing_content_serial &&
         lhs.target_content_serial == rhs.target_content_serial &&
         lhs.render_target == rhs.render_target &&
         lhs.depth_stencil == rhs.depth_stencil &&
         lhs.vertex_shader == rhs.vertex_shader &&
         lhs.vertex_shader_root_call_serial ==
             rhs.vertex_shader_root_call_serial &&
         lhs.vertex_shader_root_context_key ==
             rhs.vertex_shader_root_context_key &&
         lhs.vertex_shader_root_symbol_address ==
             rhs.vertex_shader_root_symbol_address &&
         lhs.vertex_shader_root_lr == rhs.vertex_shader_root_lr &&
         lhs.pixel_shader == rhs.pixel_shader &&
         lhs.pixel_shader_root_call_serial ==
             rhs.pixel_shader_root_call_serial &&
         lhs.pixel_shader_root_context_key ==
             rhs.pixel_shader_root_context_key &&
         lhs.pixel_shader_root_symbol_address ==
             rhs.pixel_shader_root_symbol_address &&
         lhs.pixel_shader_root_lr == rhs.pixel_shader_root_lr &&
         lhs.frame_index == rhs.frame_index &&
         lhs.draw_serial == rhs.draw_serial &&
         lhs.wrapper_source_texture == rhs.wrapper_source_texture &&
         lhs.wrapper_source_exact == rhs.wrapper_source_exact &&
         lhs.wrapper_setup_globals == rhs.wrapper_setup_globals &&
         lhs.sampled_source.kind == rhs.sampled_source.kind &&
         lhs.sampled_source.resource == rhs.sampled_source.resource &&
         lhs.sampled_source.base_address == rhs.sampled_source.base_address &&
         lhs.sampled_source.fetch_dwords == rhs.sampled_source.fetch_dwords &&
         lhs.sampled_source.content_serial ==
             rhs.sampled_source.content_serial &&
         lhs.kind == rhs.kind;
}

bool SurfaceDestinationRelationStoreEquivalent(
    const FableSurfaceDestinationTextureRelation& candidate,
    const FableSurfaceDestinationTextureRelation& current) {
  return candidate.serial == current.serial &&
         candidate.source_surface == current.source_surface &&
         candidate.destination_texture == current.destination_texture &&
         candidate.source_slot == current.source_slot &&
         SurfaceTextureLinkStoreEquivalent(candidate.link, current.link);
}

bool SurfaceTextureLinkHasVertexShaderRoot(
    const FableSurfaceTextureLink& link) {
  return link.vertex_shader_root_call_serial != 0 ||
         link.vertex_shader_root_context_key != 0 ||
         link.vertex_shader_root_symbol_address != 0 ||
         link.vertex_shader_root_lr != 0;
}

bool SurfaceTextureLinkHasPixelShaderRoot(
    const FableSurfaceTextureLink& link) {
  return link.pixel_shader_root_call_serial != 0 ||
         link.pixel_shader_root_context_key != 0 ||
         link.pixel_shader_root_symbol_address != 0 ||
         link.pixel_shader_root_lr != 0;
}

bool SurfaceTextureLinkHasShaderRoots(const FableSurfaceTextureLink& link) {
  return SurfaceTextureLinkHasVertexShaderRoot(link) ||
         SurfaceTextureLinkHasPixelShaderRoot(link);
}

void CopyMissingSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& destination,
    const FableSurfaceTextureLink& source) {
  if (!SurfaceTextureLinkHasVertexShaderRoot(destination) &&
      SurfaceTextureLinkHasVertexShaderRoot(source)) {
    destination.vertex_shader_root_call_serial =
        source.vertex_shader_root_call_serial;
    destination.vertex_shader_root_context_key =
        source.vertex_shader_root_context_key;
    destination.vertex_shader_root_symbol_address =
        source.vertex_shader_root_symbol_address;
    destination.vertex_shader_root_lr = source.vertex_shader_root_lr;
  }
  if (!SurfaceTextureLinkHasPixelShaderRoot(destination) &&
      SurfaceTextureLinkHasPixelShaderRoot(source)) {
    destination.pixel_shader_root_call_serial =
        source.pixel_shader_root_call_serial;
    destination.pixel_shader_root_context_key =
        source.pixel_shader_root_context_key;
    destination.pixel_shader_root_symbol_address =
        source.pixel_shader_root_symbol_address;
    destination.pixel_shader_root_lr = source.pixel_shader_root_lr;
  }
}

bool PrivateTextureCopyActionHasVertexShaderRoot(
    const FablePrivateTextureCopyAction& action) {
  return action.vertex_shader_root_call_serial != 0 ||
         action.vertex_shader_root_context_key != 0 ||
         action.vertex_shader_root_symbol_address != 0 ||
         action.vertex_shader_root_lr != 0;
}

bool PrivateTextureCopyActionHasPixelShaderRoot(
    const FablePrivateTextureCopyAction& action) {
  return action.pixel_shader_root_call_serial != 0 ||
         action.pixel_shader_root_context_key != 0 ||
         action.pixel_shader_root_symbol_address != 0 ||
         action.pixel_shader_root_lr != 0;
}

void CopyMissingSurfaceTextureLinkShaderRootsFromAction(
    FableSurfaceTextureLink& destination,
    const FablePrivateTextureCopyAction& source) {
  if (destination.vertex_shader == 0 && source.vertex_shader != 0) {
    destination.vertex_shader = source.vertex_shader;
  }
  if (destination.pixel_shader == 0 && source.pixel_shader != 0) {
    destination.pixel_shader = source.pixel_shader;
  }
  if (!SurfaceTextureLinkHasVertexShaderRoot(destination) &&
      PrivateTextureCopyActionHasVertexShaderRoot(source)) {
    destination.vertex_shader_root_call_serial =
        source.vertex_shader_root_call_serial;
    destination.vertex_shader_root_context_key =
        source.vertex_shader_root_context_key;
    destination.vertex_shader_root_symbol_address =
        source.vertex_shader_root_symbol_address;
    destination.vertex_shader_root_lr = source.vertex_shader_root_lr;
  }
  if (!SurfaceTextureLinkHasPixelShaderRoot(destination) &&
      PrivateTextureCopyActionHasPixelShaderRoot(source)) {
    destination.pixel_shader_root_call_serial =
        source.pixel_shader_root_call_serial;
    destination.pixel_shader_root_context_key =
        source.pixel_shader_root_context_key;
    destination.pixel_shader_root_symbol_address =
        source.pixel_shader_root_symbol_address;
    destination.pixel_shader_root_lr = source.pixel_shader_root_lr;
  }
}

void EnrichSurfaceTextureLinkShaderRootsFromRegisteredActions(
    const FableGuestResourceRegistry& registry,
    FableSurfaceTextureLink& link) {
  if (link.copy_call_serial != 0) {
    const auto call_it =
        registry.private_texture_copy_action_by_wrapper_call.find(
            link.copy_call_serial);
	    if (call_it !=
	        registry.private_texture_copy_action_by_wrapper_call.end()) {
	      CopyMissingSurfaceTextureLinkShaderRootsFromAction(link,
	                                                         call_it->second);
	      RepairSurfaceTextureLinkSampleIdentityFromAction(link,
	                                                       call_it->second);
	    }
	  }
	  if (link.copy_context_key != 0) {
    const auto context_it =
        registry.private_texture_copy_action_by_wrapper_context.find(
            link.copy_context_key);
	    if (context_it !=
	        registry.private_texture_copy_action_by_wrapper_context.end()) {
	      CopyMissingSurfaceTextureLinkShaderRootsFromAction(link,
	                                                         context_it->second);
	      RepairSurfaceTextureLinkSampleIdentityFromAction(link,
	                                                       context_it->second);
	    }
	  }
	}

bool SurfaceTextureLinkMatchesPrivateCopyActionWrapper(
    const FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (link.copy_call_serial != 0 && action.wrapper_call_serial != 0 &&
      link.copy_call_serial == action.wrapper_call_serial) {
    return true;
  }
  return link.copy_context_key != 0 && action.wrapper_context_key != 0 &&
         link.copy_context_key == action.wrapper_context_key;
}

bool SurfaceTextureLinkMatchesPrivateCopyActionSource(
    const FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (link.source_slot != 0xFFFFFFFFu && action.source_slot != 0xFFFFFFFFu &&
      link.source_slot != action.source_slot) {
    return false;
  }
  if (link.texture != 0 && action.source_texture != 0 &&
      link.texture == action.source_texture) {
    return true;
  }
  if (link.texture_base_address != 0 && action.source_base_address != 0 &&
      link.texture_base_address == action.source_base_address) {
    return true;
  }
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  const uint64_t action_fetch_key =
      FableDirectTextureFetchKey(action.source_fetch_dwords);
  return link_fetch_key != 0 && action_fetch_key != 0 &&
         link_fetch_key == action_fetch_key;
}

bool SurfaceTextureLinkSampleSourceCanAdoptActionSample(
    const FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (!HasRegisteredSampleSource(action.sampled_source)) {
    return false;
  }
  if (!HasRegisteredSampleSource(link.sampled_source) ||
      link.sampled_source.content_serial == 0) {
    return true;
  }
  if (link.sampled_source.kind != action.sampled_source.kind) {
    return false;
  }
  if (link.sampled_source.resource != 0 &&
      action.sampled_source.resource != 0 &&
      link.sampled_source.resource != action.sampled_source.resource) {
    return false;
  }
  if (link.sampled_source.base_address != 0 &&
      action.sampled_source.base_address != 0 &&
      link.sampled_source.base_address != action.sampled_source.base_address) {
    return false;
  }
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.sampled_source.fetch_dwords);
  const uint64_t action_fetch_key =
      FableDirectTextureFetchKey(action.sampled_source.fetch_dwords);
  if (link_fetch_key != 0 && action_fetch_key != 0 &&
      link_fetch_key != action_fetch_key) {
    return false;
  }
  return action.sampled_source.content_serial >
         link.sampled_source.content_serial;
}

bool SurfaceTextureLinkMatchesSurfaceTextureLinkSample(
    const FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link) {
  if (!HasRegisteredSampleSource(sample_link.sampled_source) ||
      link.source_surface == 0 || sample_link.source_surface == 0 ||
      link.source_surface != sample_link.source_surface) {
    return false;
  }
  if (link.copy_symbol_address != 0 && sample_link.copy_symbol_address != 0 &&
      link.copy_symbol_address != sample_link.copy_symbol_address) {
    return false;
  }
  const bool same_wrapper_call =
      link.copy_call_serial != 0 && sample_link.copy_call_serial != 0 &&
      link.copy_call_serial == sample_link.copy_call_serial;
  const bool same_wrapper_context =
      link.copy_context_key != 0 && sample_link.copy_context_key != 0 &&
      link.copy_context_key == sample_link.copy_context_key;
  if (!same_wrapper_call && !same_wrapper_context) {
    return false;
  }
  if (link.source_slot != 0xFFFFFFFFu &&
      sample_link.source_slot != 0xFFFFFFFFu &&
      link.source_slot != sample_link.source_slot) {
    return false;
  }
  const bool same_source_texture =
      link.texture != 0 && sample_link.texture != 0 &&
      link.texture == sample_link.texture;
  const bool same_source_base =
      link.texture_base_address != 0 &&
      sample_link.texture_base_address != 0 &&
      link.texture_base_address == sample_link.texture_base_address;
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  const uint64_t sample_fetch_key =
      FableDirectTextureFetchKey(sample_link.texture_fetch_dwords);
  const bool same_source_fetch =
      link_fetch_key != 0 && sample_fetch_key != 0 &&
      link_fetch_key == sample_fetch_key;
  if (!same_source_texture && !same_source_base && !same_source_fetch) {
    return false;
  }
  const bool same_kind =
      link.kind == sample_link.kind ||
      (link.kind != FablePrivateTextureCopyKind::kDepth &&
       sample_link.kind != FablePrivateTextureCopyKind::kDepth);
  if (!same_kind) {
    return false;
  }
  if (link.wrapper_source_texture != 0 &&
      sample_link.wrapper_source_texture != 0 &&
      link.wrapper_source_texture != sample_link.wrapper_source_texture) {
    return false;
  }
  return true;
}

bool SurfaceTextureLinkSampleSourceCanAdoptLinkSample(
    const FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link) {
  if (!HasRegisteredSampleSource(sample_link.sampled_source)) {
    return false;
  }
  if (!HasRegisteredSampleSource(link.sampled_source) ||
      link.sampled_source.content_serial == 0) {
    return true;
  }
  if (link.sampled_source.kind != sample_link.sampled_source.kind) {
    return false;
  }
  if (link.sampled_source.resource != 0 &&
      sample_link.sampled_source.resource != 0 &&
      link.sampled_source.resource != sample_link.sampled_source.resource) {
    return false;
  }
  if (link.sampled_source.base_address != 0 &&
      sample_link.sampled_source.base_address != 0 &&
      link.sampled_source.base_address != sample_link.sampled_source.base_address) {
    return false;
  }
  const uint64_t link_fetch_key =
      FableDirectTextureFetchKey(link.sampled_source.fetch_dwords);
  const uint64_t sample_fetch_key =
      FableDirectTextureFetchKey(sample_link.sampled_source.fetch_dwords);
  if (link_fetch_key != 0 && sample_fetch_key != 0 &&
      link_fetch_key != sample_fetch_key) {
    return false;
  }
  return sample_link.sampled_source.content_serial >
         link.sampled_source.content_serial;
}

bool RepairSurfaceTextureLinkSampleIdentityFromAction(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (!SurfaceTextureLinkMatchesPrivateCopyActionWrapper(link, action) ||
      !SurfaceTextureLinkMatchesPrivateCopyActionSource(link, action) ||
      action.target_surface == 0 || link.source_surface != action.target_surface) {
    return false;
  }

  bool repaired = false;
  auto copy_if_zero = [&](uint32_t& destination, uint32_t source) {
    if (destination == 0 && source != 0) {
      destination = source;
      repaired = true;
    }
  };
  auto copy_fetch_if_zero =
      [&](std::array<uint32_t, 6>& destination,
          const std::array<uint32_t, 6>& source) {
    if (FableDirectTextureFetchKey(destination) == 0 &&
        FableDirectTextureFetchKey(source) != 0) {
      destination = source;
      repaired = true;
    }
  };

  copy_if_zero(link.backing_texture, action.backing_texture);
  copy_if_zero(link.backing_base_address, action.backing_base_address);
  copy_fetch_if_zero(link.backing_fetch_dwords, action.backing_fetch_dwords);
  if (action.backing_content_serial != 0 &&
      link.backing_content_serial < action.backing_content_serial) {
    link.backing_content_serial = action.backing_content_serial;
    repaired = true;
  }
  if (action.target_content_serial != 0 &&
      link.target_content_serial < action.target_content_serial) {
    link.target_content_serial = action.target_content_serial;
    repaired = true;
  }
  if (SurfaceTextureLinkSampleSourceCanAdoptActionSample(link, action)) {
    link.sampled_source = action.sampled_source;
    repaired = true;
  }
  if (link.target_content_serial != 0 &&
      (!HasRegisteredSampleSource(link.sampled_source) ||
       (link.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface &&
        link.sampled_source.resource == link.source_surface &&
        link.sampled_source.content_serial < link.target_content_serial))) {
    link.sampled_source = {};
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    link.sampled_source.resource = link.source_surface;
    link.sampled_source.content_serial = link.target_content_serial;
    repaired = true;
  }
  return repaired;
}

bool RepairSurfaceTextureLinkSampleIdentityFromLink(
    FableSurfaceTextureLink& link,
    const FableSurfaceTextureLink& sample_link) {
  if (!SurfaceTextureLinkMatchesSurfaceTextureLinkSample(link, sample_link)) {
    return false;
  }

  bool repaired = false;
  if (sample_link.backing_content_serial != 0 &&
      link.backing_content_serial < sample_link.backing_content_serial) {
    link.backing_content_serial = sample_link.backing_content_serial;
    repaired = true;
  }
  if (sample_link.target_content_serial != 0 &&
      link.target_content_serial < sample_link.target_content_serial) {
    link.target_content_serial = sample_link.target_content_serial;
    repaired = true;
  }
  if (SurfaceTextureLinkSampleSourceCanAdoptLinkSample(link, sample_link)) {
    link.sampled_source = sample_link.sampled_source;
    repaired = true;
  }
  return repaired;
}

bool RepairSurfaceTextureLinkShaderRootsFromAction(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action) {
  if (!SurfaceTextureLinkMatchesPrivateCopyActionWrapper(link, action)) {
    return false;
  }
  const uint32_t previous_vertex_shader = link.vertex_shader;
  const uint32_t previous_pixel_shader = link.pixel_shader;
  const uint64_t previous_vertex_shader_root_call_serial =
      link.vertex_shader_root_call_serial;
  const uint64_t previous_vertex_shader_root_context_key =
      link.vertex_shader_root_context_key;
  const uint32_t previous_vertex_shader_root_symbol_address =
      link.vertex_shader_root_symbol_address;
  const uint32_t previous_vertex_shader_root_lr =
      link.vertex_shader_root_lr;
  const uint64_t previous_pixel_shader_root_call_serial =
      link.pixel_shader_root_call_serial;
  const uint64_t previous_pixel_shader_root_context_key =
      link.pixel_shader_root_context_key;
  const uint32_t previous_pixel_shader_root_symbol_address =
      link.pixel_shader_root_symbol_address;
  const uint32_t previous_pixel_shader_root_lr = link.pixel_shader_root_lr;

  CopyMissingSurfaceTextureLinkShaderRootsFromAction(link, action);
  return link.vertex_shader != previous_vertex_shader ||
         link.pixel_shader != previous_pixel_shader ||
         link.vertex_shader_root_call_serial !=
             previous_vertex_shader_root_call_serial ||
         link.vertex_shader_root_context_key !=
             previous_vertex_shader_root_context_key ||
         link.vertex_shader_root_symbol_address !=
             previous_vertex_shader_root_symbol_address ||
         link.vertex_shader_root_lr != previous_vertex_shader_root_lr ||
         link.pixel_shader_root_call_serial !=
             previous_pixel_shader_root_call_serial ||
         link.pixel_shader_root_context_key !=
             previous_pixel_shader_root_context_key ||
         link.pixel_shader_root_symbol_address !=
             previous_pixel_shader_root_symbol_address ||
         link.pixel_shader_root_lr != previous_pixel_shader_root_lr;
}

void IndexSurfaceDestinationRelationTargets(
    FableGuestResourceRegistry& registry,
    const FableSurfaceDestinationTextureRelation& relation);

// Multi-action core (2026-08-02). The per-action form of this repair was
// caught red-handed as THE title stall: GDB mid-freeze showed the present
// thread inside this walk (3,126 link buckets) from RecordFableDirectFrame's
// identity drain — one FULL walk of every link/relation map PER pending copy
// action, O(actions x whole table), growing with the registry (4s -> 13s
// frame peaks). Both repair predicates REQUIRE the exact wrapper match
// (link.copy_call_serial == action.wrapper_call_serial ||
// link.copy_context_key == action.wrapper_context_key), so a batch walks the
// table ONCE and finds each link's candidate actions by O(1) hash lookup —
// O(table + actions). Per-link application preserves registration order, and
// double-application of an action reachable via both keys is safe: the
// repairs are copy-if-missing, hence idempotent.
void RepairSurfaceTextureLinksForPrivateCopyActionsLocked(
    FableGuestResourceRegistry& registry,
    const std::vector<const FablePrivateTextureCopyAction*>& actions) {
  std::vector<const FablePrivateTextureCopyAction*> live_actions;
  live_actions.reserve(actions.size());
  for (const FablePrivateTextureCopyAction* action : actions) {
    if (action == nullptr) {
      continue;
    }
    const bool has_sample_identity =
        HasRegisteredSampleSource(action->sampled_source) ||
        action->target_content_serial != 0 ||
        action->backing_content_serial != 0;
    if (!PrivateTextureCopyActionHasVertexShaderRoot(*action) &&
        !PrivateTextureCopyActionHasPixelShaderRoot(*action) &&
        action->vertex_shader == 0 && action->pixel_shader == 0 &&
        !has_sample_identity) {
      continue;
    }
    // EXACT-KEY GATE (run 510): both repair predicates require
    // link.copy_call_serial == action.wrapper_call_serial ||
    // link.copy_context_key == action.wrapper_context_key. An action whose
    // wrapper keys were never stored on any link cannot repair anything
    // (an action that registered its own link records them at that store).
    const bool wrapper_call_seen =
        action->wrapper_call_serial != 0 &&
        registry.surface_texture_link_copy_call_serials_seen.count(
            action->wrapper_call_serial) != 0;
    const bool wrapper_context_seen =
        action->wrapper_context_key != 0 &&
        registry.surface_texture_link_copy_context_keys_seen.count(
            action->wrapper_context_key) != 0;
    if (!wrapper_call_seen && !wrapper_context_seen) {
      continue;
    }
    live_actions.push_back(action);
  }
  if (live_actions.empty()) {
    return;
  }

  std::unordered_map<uint64_t, std::vector<const FablePrivateTextureCopyAction*>>
      actions_by_wrapper_serial;
  std::unordered_map<uint64_t, std::vector<const FablePrivateTextureCopyAction*>>
      actions_by_wrapper_context;
  for (const FablePrivateTextureCopyAction* action : live_actions) {
    if (action->wrapper_call_serial != 0) {
      actions_by_wrapper_serial[action->wrapper_call_serial].push_back(action);
    }
    if (action->wrapper_context_key != 0) {
      actions_by_wrapper_context[action->wrapper_context_key].push_back(action);
    }
  }

	  bool repaired = false;
	  auto repair_link = [&](FableSurfaceTextureLink& link) {
	    const std::vector<const FablePrivateTextureCopyAction*>* by_serial =
	        nullptr;
	    if (link.copy_call_serial != 0) {
	      const auto it = actions_by_wrapper_serial.find(link.copy_call_serial);
	      if (it != actions_by_wrapper_serial.end()) {
	        by_serial = &it->second;
	      }
	    }
	    const std::vector<const FablePrivateTextureCopyAction*>* by_context =
	        nullptr;
	    if (link.copy_context_key != 0) {
	      const auto it = actions_by_wrapper_context.find(link.copy_context_key);
	      if (it != actions_by_wrapper_context.end()) {
	        by_context = &it->second;
	      }
	    }
	    if (by_serial == nullptr && by_context == nullptr) {
	      return false;
	    }
	    bool changed = false;
	    auto apply_actions =
	        [&](const std::vector<const FablePrivateTextureCopyAction*>* list) {
	          if (list == nullptr) {
	            return;
	          }
	          for (const FablePrivateTextureCopyAction* action : *list) {
	            changed |=
	                RepairSurfaceTextureLinkShaderRootsFromAction(link, *action);
	            changed |=
	                RepairSurfaceTextureLinkSampleIdentityFromAction(link, *action);
	          }
	        };
	    apply_actions(by_serial);
	    if (by_context != by_serial) {
	      apply_actions(by_context);
	    }
	    repaired |= changed;
	    return changed;
	  };
  auto repair_link_map = [&](auto& links) {
    for (auto& [key, link] : links) {
      (void)key;
      repair_link(link);
    }
  };
  auto repair_link_vector_map = [&](auto& buckets) {
    for (auto& [key, links] : buckets) {
      (void)key;
      for (FableSurfaceTextureLink& link : links) {
        repair_link(link);
      }
    }
  };
  auto repair_relation_vector_map = [&](auto& buckets) {
    for (auto& [key, relations] : buckets) {
      (void)key;
      for (FableSurfaceDestinationTextureRelation& relation : relations) {
        if (repair_link(relation.link)) {
          // Sample-identity repairs can write backing_texture /
          // backing_base_address / backing_fetch_dwords — target-index keys —
          // so a repaired relation must be re-indexed.
          IndexSurfaceDestinationRelationTargets(registry, relation);
        }
      }
    }
  };
  auto repair_relation_map = [&](auto& relations) {
    for (auto& [key, relation] : relations) {
      (void)key;
      if (repair_link(relation.link)) {
        IndexSurfaceDestinationRelationTargets(registry, relation);
      }
    }
  };

  repair_link_vector_map(registry.surface_texture_links_by_texture);
  repair_link_vector_map(registry.surface_texture_links_by_surface);
  repair_link_vector_map(registry.surface_texture_links_by_base);
  repair_link_vector_map(registry.surface_texture_links_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_destination_texture);
  repair_link_map(registry.surface_texture_link_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_exact_link_by_destination_texture);
  repair_link_map(
      registry.surface_texture_exact_link_by_destination_texture_slot);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_link_by_source_surface);
  repair_link_map(registry.surface_texture_link_by_source_surface_slot);
  repair_link_map(registry.surface_texture_link_by_base);
  repair_link_map(registry.surface_texture_link_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_copy_call);
  repair_link_map(registry.surface_texture_link_by_copy_context);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_source_surface);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture_slot);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture_slot);

  if (repaired) {
    ++registry.surface_texture_link_serial;
    ++registry.surface_destination_relation_serial;
    PublishSurfaceDestinationRelationGeneration(registry);
  }
}

void RepairSurfaceTextureLinksForPrivateCopyAction(
    FableGuestResourceRegistry& registry,
    const FablePrivateTextureCopyAction& action) {
  const std::vector<const FablePrivateTextureCopyAction*> actions = {&action};
  RepairSurfaceTextureLinksForPrivateCopyActionsLocked(registry, actions);
}

void RepairSurfaceTextureLinksFromSurfaceTextureLinkSample(
    FableGuestResourceRegistry& registry,
    const FableSurfaceTextureLink& sample_link) {
  if (!HasRegisteredSampleSource(sample_link.sampled_source) ||
      (sample_link.target_content_serial == 0 &&
       sample_link.backing_content_serial == 0 &&
       sample_link.sampled_source.content_serial == 0) ||
      sample_link.copy_symbol_address == 0 ||
      (sample_link.copy_call_serial == 0 &&
       sample_link.copy_context_key == 0)) {
    return;
  }
  // EXACT-KEY GATE (2026-08-02, run 510 title spikes): this walk is the OPEN
  // SIBLING of the per-action walk GDB caught as the title stall — the same
  // full sweep of every link/relation map, run per registered SAMPLE LINK.
  // SurfaceTextureLinkMatchesSurfaceTextureLinkSample requires
  // link.copy_call_serial == sample.copy_call_serial ||
  // link.copy_context_key == sample.copy_context_key, and every stored link
  // records its keys in the seen-sets, so a sample whose keys were never
  // stored (the dominant case: TryRegisterSurfaceViewTextureLinkLocked mints
  // a FRESH context key per new surface view, and the registration-order walk
  // runs BEFORE the link is stored) provably repairs nothing — skip the walk.
  // At title this walk ran per recycled panel-header surface view under both
  // the observer and registry mutexes; the resulting observer holds were the
  // ~200ms blocking waits in the compose syncs (sync timing resource_ms).
  const bool call_key_seen =
      sample_link.copy_call_serial != 0 &&
      registry.surface_texture_link_copy_call_serials_seen.count(
          sample_link.copy_call_serial) != 0;
  const bool context_key_seen =
      sample_link.copy_context_key != 0 &&
      registry.surface_texture_link_copy_context_keys_seen.count(
          sample_link.copy_context_key) != 0;
  if (!call_key_seen && !context_key_seen) {
    return;
  }

  bool repaired = false;
  auto repair_link = [&](FableSurfaceTextureLink& link) {
    const bool changed =
        RepairSurfaceTextureLinkSampleIdentityFromLink(link, sample_link);
    repaired |= changed;
    return changed;
  };
  auto repair_link_map = [&](auto& links) {
    for (auto& [key, link] : links) {
      (void)key;
      repair_link(link);
    }
  };
  auto repair_link_vector_map = [&](auto& buckets) {
    for (auto& [key, links] : buckets) {
      (void)key;
      for (FableSurfaceTextureLink& link : links) {
        repair_link(link);
      }
    }
  };
  auto repair_relation_vector_map = [&](auto& buckets) {
    for (auto& [key, relations] : buckets) {
      (void)key;
      for (FableSurfaceDestinationTextureRelation& relation : relations) {
        if (repair_link(relation.link)) {
          // Repairs can rewrite target-index keys (backing fields); keep the
          // destination-bucket index in step.
          IndexSurfaceDestinationRelationTargets(registry, relation);
        }
      }
    }
  };
  auto repair_relation_map = [&](auto& relations) {
    for (auto& [key, relation] : relations) {
      (void)key;
      if (repair_link(relation.link)) {
        IndexSurfaceDestinationRelationTargets(registry, relation);
      }
    }
  };

  repair_link_vector_map(registry.surface_texture_links_by_texture);
  repair_link_vector_map(registry.surface_texture_links_by_surface);
  repair_link_vector_map(registry.surface_texture_links_by_base);
  repair_link_vector_map(registry.surface_texture_links_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_destination_texture);
  repair_link_map(registry.surface_texture_link_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_exact_link_by_destination_texture);
  repair_link_map(
      registry.surface_texture_exact_link_by_destination_texture_slot);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture);
  repair_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture_slot);
  repair_link_map(registry.surface_texture_link_by_source_surface);
  repair_link_map(registry.surface_texture_link_by_source_surface_slot);
  repair_link_map(registry.surface_texture_link_by_base);
  repair_link_map(registry.surface_texture_link_by_fetch_key);
  repair_link_map(registry.surface_texture_link_by_copy_call);
  repair_link_map(registry.surface_texture_link_by_copy_context);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_source_surface);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture);
  repair_relation_map(
      registry.surface_destination_relation_by_destination_texture_slot);
  repair_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture_slot);

  if (repaired) {
    ++registry.surface_texture_link_serial;
    ++registry.surface_destination_relation_serial;
    PublishSurfaceDestinationRelationGeneration(registry);
  }
}

FableSurfaceTextureLink MergeSurfaceTextureLinkMetadata(
    const FableSurfaceTextureLink& candidate,
    const FableSurfaceTextureLink& current) {
  const bool candidate_preferred =
      SurfaceTextureLinkShouldPrefer(candidate, &current);
  FableSurfaceTextureLink merged = candidate_preferred ? candidate : current;
  const FableSurfaceTextureLink& metadata_source =
      candidate_preferred ? current : candidate;
  if (merged.serial == 0) {
    merged.serial = current.serial != 0 ? current.serial : candidate.serial;
  }
  if (merged.vertex_shader == 0) {
    merged.vertex_shader =
        current.vertex_shader != 0 ? current.vertex_shader
                                   : candidate.vertex_shader;
  }
  if (merged.pixel_shader == 0) {
    merged.pixel_shader = current.pixel_shader != 0 ? current.pixel_shader
                                                    : candidate.pixel_shader;
  }
  CopyMissingSurfaceTextureLinkShaderRoots(merged, metadata_source);
  return merged;
}

bool SurfaceTextureLinkIsTrivialSelfEdge(
    const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge || link.texture == 0 ||
      link.source_surface == 0 || link.texture != link.source_surface) {
    return false;
  }

  const uint64_t texture_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  const uint64_t backing_fetch_key =
      FableDirectTextureFetchKey(link.backing_fetch_dwords);
  const uint64_t sampled_fetch_key =
      FableDirectTextureFetchKey(link.sampled_source.fetch_dwords);

  const bool has_distinct_backing_texture =
      link.backing_texture != 0 && link.backing_texture != link.texture &&
      link.backing_texture != link.source_surface;
  const bool has_distinct_backing_base =
      link.backing_base_address != 0 &&
      (link.texture_base_address == 0 ||
       link.backing_base_address != link.texture_base_address);
  const bool has_distinct_backing_fetch =
      backing_fetch_key != 0 &&
      (texture_fetch_key == 0 || backing_fetch_key != texture_fetch_key);
  if (has_distinct_backing_texture || has_distinct_backing_base ||
      has_distinct_backing_fetch) {
    return false;
  }

  const bool has_distinct_sample_resource =
      link.sampled_source.resource != 0 &&
      link.sampled_source.resource != link.texture &&
      link.sampled_source.resource != link.source_surface;
  const bool has_distinct_sample_base =
      link.sampled_source.base_address != 0 &&
      (link.texture_base_address == 0 ||
       link.sampled_source.base_address != link.texture_base_address);
  const bool has_distinct_sample_fetch =
      sampled_fetch_key != 0 &&
      (texture_fetch_key == 0 || sampled_fetch_key != texture_fetch_key);
  return !has_distinct_sample_resource && !has_distinct_sample_base &&
         !has_distinct_sample_fetch;
}

bool IsFableRegisteredPrivateTextureAddress(uint32_t address) {
  switch (address) {
    case 0x40130C90u:
    case 0x40130CD0u:
    case 0x40131610u:
    case 0x40131650u:
    case 0x40131C90u:
    case 0x40131CD0u:
    case 0x40131D10u:
    case 0x40137C90u:
    case 0x40137CD0u:
    case 0x40137D10u:
    case 0x40133CD0u:
    case 0x40133D10u:
    case 0x404DE450u:
    case 0x404DE4D0u:
    case 0x404DE510u:
    case 0x404DE550u:
    case 0x404DE590u:
    case 0x404DE5D0u:
    case 0x404DE610u:
    case 0x404DE650u:
    case 0x404DE690u:
    case 0x404DE6D0u:
    case 0x404DE710u:
    case 0x40513AD0u:
    case 0x40513B10u:
    case 0x40513B50u:
    case 0x40513B90u:
    case 0x40513BD0u:
    case 0x40513C10u:
    case 0x40513C50u:
    case 0x40513C90u:
    case 0x40513CD0u:
    case 0x40513D10u:
    case 0x40513D90u:
    case 0x40513DD0u:
    case 0x12656000u:
    case 0x12681000u:
    case 0x12759000u:
    case 0x12771000u:
    case 0x12867000u:
    case 0x1289D000u:
    case 0x128D3000u:
    case 0x128D9000u:
    case 0x1293F000u:
      return true;
    default:
      return false;
  }
}
namespace {
bool SurfaceTextureLinkTouchesFablePrivateChain(
    const FableSurfaceTextureLink& link) {
  return IsFableRegisteredPrivateTextureAddress(link.texture) ||
         IsFableRegisteredPrivateTextureAddress(link.texture_base_address) ||
         IsFableRegisteredPrivateTextureAddress(link.source_surface) ||
         IsFableRegisteredPrivateTextureAddress(link.backing_texture) ||
         IsFableRegisteredPrivateTextureAddress(link.backing_base_address);
}
}  // namespace

bool PrivateTextureCopyActionTouchesFablePrivateChain(
    const FablePrivateTextureCopyAction& action) {
  return action.kind != FablePrivateTextureCopyKind::kDepth &&
         (IsFableRegisteredPrivateTextureAddress(action.source_texture) ||
          IsFableRegisteredPrivateTextureAddress(action.source_base_address) ||
          IsFableRegisteredPrivateTextureAddress(action.backing_texture) ||
          IsFableRegisteredPrivateTextureAddress(action.backing_base_address) ||
          IsFableRegisteredPrivateTextureAddress(action.target_surface) ||
          IsFableRegisteredPrivateTextureAddress(action.render_target));
}

bool SameShaderIdentity(const FableShaderIdentity& lhs,
                        const FableShaderIdentity& rhs) {
  return lhs.kind == rhs.kind && lhs.object_shader == rhs.object_shader &&
         lhs.parent_shader == rhs.parent_shader &&
         lhs.bytecode_address == rhs.bytecode_address &&
         lhs.bytecode_size == rhs.bytecode_size &&
         lhs.bytecode_offset == rhs.bytecode_offset &&
         lhs.bytecode_hash == rhs.bytecode_hash;
}

bool SameVertexDeclarationIdentity(
    const FableVertexDeclarationIdentity& lhs,
    const FableVertexDeclarationIdentity& rhs) {
  return lhs.object_declaration == rhs.object_declaration &&
         lhs.parent_declaration == rhs.parent_declaration &&
         lhs.elements_address == rhs.elements_address &&
         lhs.elements_size == rhs.elements_size &&
         lhs.element_count == rhs.element_count &&
         lhs.max_stream == rhs.max_stream &&
         lhs.stream_mask == rhs.stream_mask &&
         lhs.declaration_flags == rhs.declaration_flags &&
         lhs.elements_hash == rhs.elements_hash;
}

// ShaderIdentityIndexShouldPrefer / VertexDeclarationIdentityIndexShouldPrefer
// were removed with SelectBest{Shader,VertexDeclaration}Identity, their only
// callers, which in turn only served the deleted zero-consumer *ByHash lookups.

const FableTextureIdentity* SelectUniqueTextureIdentity(
    const std::vector<uint32_t>* objects) {
  if (!objects) {
    return nullptr;
  }

  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableTextureIdentity* unique = nullptr;
  for (uint32_t object : *objects) {
    const auto it = registry.texture_identities_by_object.find(object);
    if (it == registry.texture_identities_by_object.end()) {
      continue;
    }
    if (unique) {
      return nullptr;
    }
    unique = &it->second;
  }
  return unique;
}

template <typename T>
void AddUnique(std::vector<T>& values, T value) {
  if (value == T{}) {
    return;
  }
  if (std::find(values.begin(), values.end(), value) == values.end()) {
    values.push_back(value);
  }
}

void RemoveUnique(std::vector<uint32_t>& values, uint32_t value) {
  values.erase(std::remove(values.begin(), values.end(), value), values.end());
}

void RemoveSurfaceDestinationTextureLinkReverseIndex(
    FableGuestResourceRegistry& registry, uint32_t destination_texture,
    const FableSurfaceTextureLink& link) {
  if (destination_texture == 0) {
    return;
  }
  auto remove_u32_key = [&](auto& index, uint32_t key) {
    if (key == 0) {
      return;
    }
    const auto it = index.find(key);
    if (it == index.end()) {
      return;
    }
    RemoveUnique(it->second, destination_texture);
    if (it->second.empty()) {
      index.erase(it);
    }
  };
  auto remove_fetch_key = [&](uint64_t key) {
    if (key == 0) {
      return;
    }
    const auto it =
        registry.surface_destination_texture_links_by_fetch_key.find(key);
    if (it == registry.surface_destination_texture_links_by_fetch_key.end()) {
      return;
    }
    RemoveUnique(it->second, destination_texture);
    if (it->second.empty()) {
      registry.surface_destination_texture_links_by_fetch_key.erase(it);
    }
  };

  remove_u32_key(registry.surface_destination_texture_links_by_object,
                 link.texture);
  remove_u32_key(registry.surface_destination_texture_links_by_object,
                 link.backing_texture);
  remove_u32_key(registry.surface_destination_texture_links_by_base,
                 link.texture_base_address);
  remove_u32_key(registry.surface_destination_texture_links_by_base,
                 link.backing_base_address);
  remove_fetch_key(FableDirectTextureFetchKey(link.texture_fetch_dwords));
  remove_fetch_key(FableDirectTextureFetchKey(link.backing_fetch_dwords));
}

void AddSurfaceDestinationTextureLinkReverseIndex(
    FableGuestResourceRegistry& registry, uint32_t destination_texture,
    const FableSurfaceTextureLink& link) {
  if (destination_texture == 0) {
    return;
  }
  auto add_u32_key = [&](auto& index, uint32_t key) {
    if (key != 0) {
      AddUnique(index[key], destination_texture);
    }
  };
  add_u32_key(registry.surface_destination_texture_links_by_object,
              link.texture);
  add_u32_key(registry.surface_destination_texture_links_by_object,
              link.backing_texture);
  add_u32_key(registry.surface_destination_texture_links_by_base,
              link.texture_base_address);
  add_u32_key(registry.surface_destination_texture_links_by_base,
              link.backing_base_address);
  const uint64_t texture_fetch_key =
      FableDirectTextureFetchKey(link.texture_fetch_dwords);
  if (texture_fetch_key != 0) {
    AddUnique(registry.surface_destination_texture_links_by_fetch_key
                  [texture_fetch_key],
              destination_texture);
  }
  const uint64_t backing_fetch_key =
      FableDirectTextureFetchKey(link.backing_fetch_dwords);
  if (backing_fetch_key != 0) {
    AddUnique(registry.surface_destination_texture_links_by_fetch_key
                  [backing_fetch_key],
              destination_texture);
  }
}

void ReplaceSurfaceDestinationTextureLinkLocked(
    FableGuestResourceRegistry& registry, uint32_t destination_texture,
    const FableSurfaceTextureLink& link) {
  if (destination_texture == 0) {
    return;
  }
  const auto direct_it =
      registry.surface_texture_link_by_destination_texture.find(
          destination_texture);
  if (direct_it !=
      registry.surface_texture_link_by_destination_texture.end()) {
    RemoveSurfaceDestinationTextureLinkReverseIndex(registry,
                                                   destination_texture,
                                                   direct_it->second);
  }
  registry.surface_texture_link_by_destination_texture[destination_texture] =
      link;
  AddSurfaceDestinationTextureLinkReverseIndex(registry, destination_texture,
                                              link);
}

void CollectTextureIdentityIndexKeys(
    const FableTextureIdentity& identity, std::vector<uint32_t>& object_keys,
    std::vector<uint32_t>& base_keys, std::vector<uint64_t>& fetch_keys) {
  AddUnique(object_keys, identity.object_texture);
  AddUnique(object_keys, identity.parent_texture);
  AddUnique(base_keys, identity.base_address);
  AddUnique(fetch_keys, FableDirectTextureFetchKey(identity.fetch_dwords));
}

void CollectTextureIdentityIndexKeysForObject(
    const FableGuestResourceRegistry& registry, uint32_t object,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys) {
  AddUnique(object_keys, object);
  const auto it = registry.texture_identities_by_object.find(object);
  if (it != registry.texture_identities_by_object.end()) {
    CollectTextureIdentityIndexKeys(it->second, object_keys, base_keys,
                                    fetch_keys);
  }
}

void CollectTextureIdentityIndexKeysForBase(
    const FableGuestResourceRegistry& registry, uint32_t base_address,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys) {
  AddUnique(base_keys, base_address);
  const auto objects_it =
      registry.texture_identity_objects_by_base.find(base_address);
  if (objects_it == registry.texture_identity_objects_by_base.end()) {
    return;
  }
  for (uint32_t object : objects_it->second) {
    CollectTextureIdentityIndexKeysForObject(registry, object, object_keys,
                                             base_keys, fetch_keys);
  }
}

void CollectTextureIdentityIndexKeysForFetch(
    const FableGuestResourceRegistry& registry, uint64_t fetch_key,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys) {
  AddUnique(fetch_keys, fetch_key);
  const auto objects_it =
      registry.texture_identity_objects_by_fetch_key.find(fetch_key);
  if (objects_it == registry.texture_identity_objects_by_fetch_key.end()) {
    return;
  }
  for (uint32_t object : objects_it->second) {
    CollectTextureIdentityIndexKeysForObject(registry, object, object_keys,
                                             base_keys, fetch_keys);
  }
}

bool HasRegisteredSampleSource(
    const FableRegisteredSampleSource& sampled_source) {
  return sampled_source.kind != FableRegisteredSampleSourceKind::kUnknown &&
         sampled_source.resource != 0;
}

void NormalizePrivateTextureCopySampleSource(
    FablePrivateTextureCopyAction& action) {
  if (HasRegisteredSampleSource(action.sampled_source)) {
    if (action.sampled_source.kind ==
        FableRegisteredSampleSourceKind::kBackingTexture) {
      if (action.backing_texture == 0) {
        action.backing_texture = action.sampled_source.resource;
      }
      if (action.backing_base_address == 0) {
        action.backing_base_address = action.sampled_source.base_address;
      }
      if (FableDirectTextureFetchKey(action.backing_fetch_dwords) == 0) {
        action.backing_fetch_dwords = action.sampled_source.fetch_dwords;
      }
      if (action.backing_content_serial == 0) {
        action.backing_content_serial = action.sampled_source.content_serial;
      }
    } else if (action.sampled_source.kind ==
                   FableRegisteredSampleSourceKind::kGpuSurface &&
               action.sampled_source.resource == action.target_surface &&
               action.target_content_serial == 0) {
      action.target_content_serial = action.sampled_source.content_serial;
    }
    return;
  }
  if (action.backing_texture != 0 || action.backing_base_address != 0 ||
      FableDirectTextureFetchKey(action.backing_fetch_dwords) != 0) {
    action.sampled_source.kind =
        FableRegisteredSampleSourceKind::kBackingTexture;
    action.sampled_source.resource = action.backing_texture;
    action.sampled_source.base_address = action.backing_base_address;
    action.sampled_source.fetch_dwords = action.backing_fetch_dwords;
    action.sampled_source.content_serial = action.backing_content_serial;
    return;
  }
  action.sampled_source.kind = FableRegisteredSampleSourceKind::kGuestTexture;
  action.sampled_source.resource = action.source_texture;
  action.sampled_source.base_address = action.source_base_address;
  action.sampled_source.fetch_dwords = action.source_fetch_dwords;
}

void NormalizeSurfaceTextureLinkSampleSource(FableSurfaceTextureLink& link) {
  if (HasRegisteredSampleSource(link.sampled_source)) {
    if (link.sampled_source.kind ==
        FableRegisteredSampleSourceKind::kBackingTexture) {
      if (link.backing_texture == 0) {
        link.backing_texture = link.sampled_source.resource;
      }
      if (link.backing_base_address == 0) {
        link.backing_base_address = link.sampled_source.base_address;
      }
      if (FableDirectTextureFetchKey(link.backing_fetch_dwords) == 0) {
        link.backing_fetch_dwords = link.sampled_source.fetch_dwords;
      }
      if (link.backing_content_serial == 0) {
        link.backing_content_serial = link.sampled_source.content_serial;
      }
    } else if (link.sampled_source.kind ==
                   FableRegisteredSampleSourceKind::kGpuSurface &&
               link.sampled_source.resource == link.source_surface &&
               link.target_content_serial == 0) {
      link.target_content_serial = link.sampled_source.content_serial;
    }
    return;
  }
  if (link.backing_texture != 0 || link.backing_base_address != 0 ||
      FableDirectTextureFetchKey(link.backing_fetch_dwords) != 0) {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kBackingTexture;
    link.sampled_source.resource = link.backing_texture;
    link.sampled_source.base_address = link.backing_base_address;
    link.sampled_source.fetch_dwords = link.backing_fetch_dwords;
    link.sampled_source.content_serial = link.backing_content_serial;
    return;
  }
  if (link.source_surface != 0) {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    link.sampled_source.resource = link.source_surface;
    link.sampled_source.content_serial = link.target_content_serial;
    return;
  }
}

// SelectBestShaderIdentity / SelectBestVertexDeclarationIdentity were removed
// alongside the zero-consumer FableDirectFind*IdentityByHash singular lookups
// they exclusively served. The plural ...IdentityObjectsByHash finders remain
// live.

bool SurfaceTextureLinkShouldPrefer(const FableSurfaceTextureLink& candidate,
                                    const FableSurfaceTextureLink* current) {
  if (!current) {
    return true;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  const bool candidate_has_slot = candidate.source_slot != kUnknownSlot;
  const bool current_has_slot = current->source_slot != kUnknownSlot;
  if (candidate_has_slot != current_has_slot) {
    return candidate_has_slot;
  }
  if (candidate.trusted_destination_edge != current->trusted_destination_edge) {
    return candidate.trusted_destination_edge;
  }
  const bool candidate_has_copy_identity =
      candidate.copy_call_serial != 0 || candidate.copy_context_key != 0 ||
      candidate.copy_symbol_address != 0;
  const bool current_has_copy_identity =
      current->copy_call_serial != 0 || current->copy_context_key != 0 ||
      current->copy_symbol_address != 0;
  if (candidate_has_copy_identity != current_has_copy_identity) {
    return candidate_has_copy_identity;
  }
  if (candidate.copy_call_serial != current->copy_call_serial) {
    return candidate.copy_call_serial > current->copy_call_serial;
  }
  if (candidate.copy_context_key != current->copy_context_key) {
    return candidate.copy_context_key > current->copy_context_key;
  }
  if (candidate.wrapper_source_exact != current->wrapper_source_exact) {
    return candidate.wrapper_source_exact;
  }
  if (candidate.wrapper_source_texture != current->wrapper_source_texture) {
    return candidate.wrapper_source_texture != 0;
  }
  const bool candidate_has_proven_backing =
      candidate.backing_texture != 0 &&
      (candidate.backing_content_serial != 0 ||
       candidate.target_content_serial != 0);
  const bool current_has_proven_backing =
      current->backing_texture != 0 &&
      (current->backing_content_serial != 0 ||
       current->target_content_serial != 0);
  if (candidate_has_proven_backing != current_has_proven_backing) {
    return candidate_has_proven_backing;
  }
  const bool candidate_has_sample_content =
      candidate.sampled_source.resource != 0 &&
      candidate.sampled_source.content_serial != 0;
  const bool current_has_sample_content =
      current->sampled_source.resource != 0 &&
      current->sampled_source.content_serial != 0;
  if (candidate_has_sample_content != current_has_sample_content) {
    return candidate_has_sample_content;
  }
  const bool candidate_has_shader_roots =
      SurfaceTextureLinkHasShaderRoots(candidate);
  const bool current_has_shader_roots =
      SurfaceTextureLinkHasShaderRoots(*current);
  if (candidate_has_shader_roots != current_has_shader_roots) {
    return candidate_has_shader_roots;
  }
  const bool candidate_has_shader_pair =
      candidate.vertex_shader != 0 && candidate.pixel_shader != 0;
  const bool current_has_shader_pair =
      current->vertex_shader != 0 && current->pixel_shader != 0;
  if (candidate_has_shader_pair != current_has_shader_pair) {
    return candidate_has_shader_pair;
  }
  if (candidate.frame_index != current->frame_index) {
    return candidate.frame_index > current->frame_index;
  }
  if (candidate.draw_serial != current->draw_serial) {
    return candidate.draw_serial > current->draw_serial;
  }
  if (candidate.target_content_serial != current->target_content_serial) {
    return candidate.target_content_serial > current->target_content_serial;
  }
  if (candidate.backing_content_serial != current->backing_content_serial) {
    return candidate.backing_content_serial > current->backing_content_serial;
  }
  return candidate.serial > current->serial;
}

const FableSurfaceTextureLink* SelectBestSurfaceTextureLink(
    const std::vector<FableSurfaceTextureLink>* links) {
  if (!links || links->empty()) {
    return nullptr;
  }
  const FableSurfaceTextureLink* best = &links->front();
  for (const FableSurfaceTextureLink& link : *links) {
    if (SurfaceTextureLinkShouldPrefer(link, best)) {
      best = &link;
    }
  }
  return best;
}

bool SurfaceTextureLinkIsVisibleToDraw(const FableSurfaceTextureLink& link,
                                       uint32_t frame_index,
                                       uint64_t draw_serial,
                                       uint32_t source_slot) {
  if (!link.trusted_destination_edge) {
    return false;
  }
  if (link.frame_index != 0 && link.frame_index > frame_index) {
    return false;
  }
  if (link.frame_index != 0 && link.frame_index == frame_index &&
      link.draw_serial != 0 && draw_serial != 0 &&
      link.draw_serial > draw_serial) {
    return false;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  return link.source_slot == kUnknownSlot || link.source_slot == source_slot ||
         (source_slot != kUnknownSlot &&
          SurfaceTextureLinkTouchesFablePrivateChain(link));
}

const FableSurfaceTextureLink* SelectVisibleSurfaceTextureLink(
    const std::vector<FableSurfaceTextureLink>* links, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot) {
  if (!links || links->empty()) {
    return nullptr;
  }
  const FableSurfaceTextureLink* best = nullptr;
  for (const FableSurfaceTextureLink& link : *links) {
    if (!SurfaceTextureLinkIsVisibleToDraw(link, frame_index, draw_serial,
                                           source_slot)) {
      continue;
    }
    if (SurfaceTextureLinkShouldPrefer(link, best)) {
      best = &link;
    }
  }
  return best;
}

bool SameSurfaceDestinationRelationIdentity(
    const FableSurfaceDestinationTextureRelation& lhs,
    const FableSurfaceDestinationTextureRelation& rhs) {
  return lhs.source_surface == rhs.source_surface &&
         lhs.destination_texture == rhs.destination_texture &&
         lhs.source_slot == rhs.source_slot &&
         SameSurfaceTextureLinkIdentity(lhs.link, rhs.link);
}

bool SurfaceDestinationRelationShouldPrefer(
    const FableSurfaceDestinationTextureRelation& candidate,
    const FableSurfaceDestinationTextureRelation* current) {
  if (!current) {
    return true;
  }
  return SurfaceTextureLinkShouldPrefer(candidate.link, &current->link);
}

bool SurfaceDestinationRelationIsVisibleToDraw(
    const FableSurfaceDestinationTextureRelation& relation,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  return SurfaceTextureLinkIsVisibleToDraw(relation.link, frame_index,
                                           draw_serial, source_slot);
}

const FableSurfaceDestinationTextureRelation*
SelectVisibleSurfaceDestinationRelation(
    const std::vector<FableSurfaceDestinationTextureRelation>* relations,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  if (!relations || relations->empty()) {
    return nullptr;
  }
  const FableSurfaceDestinationTextureRelation* best = nullptr;
  for (const FableSurfaceDestinationTextureRelation& relation : *relations) {
    if (!SurfaceDestinationRelationIsVisibleToDraw(
            relation, frame_index, draw_serial, source_slot)) {
      continue;
    }
    if (SurfaceDestinationRelationShouldPrefer(relation, best)) {
      best = &relation;
    }
  }
  return best;
}

bool PrivateTextureCopyActionShouldPrefer(
    const FablePrivateTextureCopyAction& candidate,
    const FablePrivateTextureCopyAction* current) {
  if (!current) {
    return true;
  }
  const auto has_exact_color_backing =
      [](const FablePrivateTextureCopyAction& action) {
        return action.kind != FablePrivateTextureCopyKind::kDepth &&
               action.backing_texture != 0;
      };
  const bool candidate_has_backing = has_exact_color_backing(candidate);
  const bool current_has_backing = has_exact_color_backing(*current);
  if (candidate_has_backing != current_has_backing) {
    return candidate_has_backing;
  }
  if (candidate.frame_index != current->frame_index) {
    return candidate.frame_index > current->frame_index;
  }
  if (candidate.draw_serial != current->draw_serial) {
    return candidate.draw_serial > current->draw_serial;
  }
  return candidate.serial > current->serial;
}

bool PrivateTextureCopyActionIsVisibleToDraw(
    const FablePrivateTextureCopyAction& action, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot) {
  if (action.frame_index != 0 && action.frame_index > frame_index) {
    return false;
  }
  if (action.frame_index != 0 && action.frame_index == frame_index &&
      action.draw_serial != 0 && draw_serial != 0 &&
      action.draw_serial > draw_serial) {
    return false;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  return action.source_slot == kUnknownSlot || source_slot == kUnknownSlot ||
         action.source_slot == source_slot ||
         PrivateTextureCopyActionTouchesFablePrivateChain(action);
}

const FablePrivateTextureCopyAction* SelectVisiblePrivateTextureCopyAction(
    const std::vector<FablePrivateTextureCopyAction>* actions,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  if (!actions || actions->empty()) {
    return nullptr;
  }
  const FablePrivateTextureCopyAction* best = nullptr;
  for (const FablePrivateTextureCopyAction& action : *actions) {
    if (!PrivateTextureCopyActionIsVisibleToDraw(action, frame_index,
                                                 draw_serial, source_slot)) {
      continue;
    }
    if (PrivateTextureCopyActionShouldPrefer(action, best)) {
      best = &action;
    }
  }
  return best;
}

uint64_t PrivateTextureCopyDestinationSlotKey(uint32_t destination_texture,
                                              uint32_t source_slot) {
  return (static_cast<uint64_t>(source_slot) << 32u) | destination_texture;
}

uint64_t SurfaceTextureDestinationSlotKey(uint32_t destination_texture,
                                          uint32_t source_slot) {
  return (static_cast<uint64_t>(source_slot) << 32u) | destination_texture;
}

uint64_t SurfaceTextureSourceSurfaceSlotKey(uint32_t source_surface,
                                            uint32_t source_slot) {
  return (static_cast<uint64_t>(source_slot) << 32u) | source_surface;
}

// Upserts the target-address/fetch-key -> destination-bucket index for one
// relation (surface_destination_relation_buckets_by_target_*). Indexes every
// address the per-draw scan's target-endpoint resolution can match — the raw
// destination/link/backing addresses, their identity expansions (object /
// parent / base, objects-by-base) and the two target fetch keys — so a draw
// binding ANY alias of the destination finds the bucket. Expansion uses the
// identities known at store time; an identity edge registered later is
// bridged by the query-side expansion in the renderer's scan planner (which
// expands the draw's bound addresses through the same maps). Values are
// bucket keys only; over-inclusion and staleness cost a wasted probe, never
// a wrong match.
void IndexSurfaceDestinationRelationTargets(
    FableGuestResourceRegistry& registry,
    const FableSurfaceDestinationTextureRelation& relation) {
  const uint32_t bucket = relation.destination_texture;
  if (bucket == 0) {
    return;
  }
  auto add_bucket = [&](std::vector<uint32_t>& buckets) {
    if (std::find(buckets.begin(), buckets.end(), bucket) == buckets.end()) {
      buckets.push_back(bucket);
    }
  };
  auto add_address = [&](uint32_t address) {
    if (address != 0) {
      add_bucket(
          registry
              .surface_destination_relation_buckets_by_target_address[address]);
    }
  };
  auto add_identity = [&](const FableTextureIdentity& identity) {
    add_address(identity.object_texture);
    add_address(identity.parent_texture);
    add_address(identity.base_address);
  };
  auto expand_address = [&](uint32_t address) {
    if (address == 0) {
      return;
    }
    add_address(address);
    const auto object_it = registry.texture_identities_by_object.find(address);
    if (object_it != registry.texture_identities_by_object.end()) {
      add_identity(object_it->second);
    }
    const auto base_it = registry.texture_identity_objects_by_base.find(address);
    if (base_it != registry.texture_identity_objects_by_base.end()) {
      for (uint32_t object : base_it->second) {
        add_address(object);
        const auto base_object_it =
            registry.texture_identities_by_object.find(object);
        if (base_object_it != registry.texture_identities_by_object.end()) {
          add_identity(base_object_it->second);
        }
      }
    }
  };
  expand_address(relation.destination_texture);
  expand_address(relation.link.texture);
  expand_address(relation.link.texture_base_address);
  expand_address(relation.link.backing_texture);
  expand_address(relation.link.backing_base_address);
  auto add_fetch = [&](const std::array<uint32_t, 6>& fetch_dwords) {
    const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
    if (fetch_key == 0) {
      return;
    }
    add_bucket(registry.surface_destination_relation_buckets_by_target_fetch_key
                   [fetch_key]);
    const auto fetch_it =
        registry.texture_identity_objects_by_fetch_key.find(fetch_key);
    if (fetch_it != registry.texture_identity_objects_by_fetch_key.end()) {
      for (uint32_t object : fetch_it->second) {
        add_address(object);
        const auto fetch_object_it =
            registry.texture_identities_by_object.find(object);
        if (fetch_object_it != registry.texture_identities_by_object.end()) {
          add_identity(fetch_object_it->second);
        }
      }
    }
  };
  add_fetch(relation.link.texture_fetch_dwords);
  add_fetch(relation.link.backing_fetch_dwords);
}

void StoreSurfaceDestinationRelation(
    FableGuestResourceRegistry& registry, const FableSurfaceTextureLink& link,
    uint32_t destination_texture) {
  if (!link.trusted_destination_edge || link.source_surface == 0 ||
      destination_texture == 0 || SurfaceTextureLinkIsTrivialSelfEdge(link)) {
    return;
  }

  FableSurfaceDestinationTextureRelation relation;
  relation.serial = ++registry.surface_destination_relation_serial;
  relation.source_surface = link.source_surface;
  relation.destination_texture = destination_texture;
  relation.source_slot = link.source_slot;
  relation.link = link;

  // Most calls here are per-draw re-registrations of a link the registry
  // already holds verbatim: the identity match below keeps the existing
  // per-relation serial and re-stores identical bytes. Only bump the content
  // generation when a store actually changes something, so the per-draw
  // destination-scan gate can prove its snapshot unchanged (run 479: the
  // unconditional serial bump defeated that gate on every draw of a
  // 3,280-draw burst — aliassplit dest=3807.7ms).
  bool mutated = false;
  constexpr size_t kMaxSurfaceDestinationRelationsPerBucket = 32;
  auto upsert_relation =
      [&](std::vector<FableSurfaceDestinationTextureRelation>& relations) {
        for (FableSurfaceDestinationTextureRelation& existing : relations) {
          if (SameSurfaceDestinationRelationIdentity(existing, relation)) {
            relation.serial = existing.serial;
            if (!SurfaceDestinationRelationStoreEquivalent(relation,
                                                           existing)) {
              existing = relation;
              mutated = true;
            }
            return;
          }
        }
        if (relations.size() >= kMaxSurfaceDestinationRelationsPerBucket) {
          relations.erase(relations.begin());
        }
        relations.push_back(relation);
        mutated = true;
      };

  upsert_relation(
      registry.surface_destination_relations_by_source_surface
          [relation.source_surface]);
  upsert_relation(
      registry.surface_destination_relations_by_destination_texture
          [relation.destination_texture]);

  const auto direct_it =
      registry.surface_destination_relation_by_destination_texture.find(
          relation.destination_texture);
  if (direct_it ==
          registry.surface_destination_relation_by_destination_texture.end() ||
      SurfaceDestinationRelationShouldPrefer(relation, &direct_it->second)) {
    if (direct_it ==
            registry.surface_destination_relation_by_destination_texture
                .end() ||
        !SurfaceDestinationRelationStoreEquivalent(relation,
                                                   direct_it->second)) {
      mutated = true;
    }
    registry.surface_destination_relation_by_destination_texture
        [relation.destination_texture] = relation;
  }

  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  if (relation.source_slot != kUnknownSlot) {
    const uint64_t slot_key = SurfaceTextureDestinationSlotKey(
        relation.destination_texture, relation.source_slot);
    upsert_relation(
        registry.surface_destination_relations_by_destination_texture_slot
            [slot_key]);
    const auto slot_it =
        registry.surface_destination_relation_by_destination_texture_slot.find(
            slot_key);
    if (slot_it ==
            registry.surface_destination_relation_by_destination_texture_slot
                .end() ||
        SurfaceDestinationRelationShouldPrefer(relation, &slot_it->second)) {
      if (slot_it ==
              registry.surface_destination_relation_by_destination_texture_slot
                  .end() ||
          !SurfaceDestinationRelationStoreEquivalent(relation,
                                                     slot_it->second)) {
        mutated = true;
      }
      registry.surface_destination_relation_by_destination_texture_slot
          [slot_key] = relation;
    }
  }

  if (mutated) {
    // Only mutating stores need the target index refreshed: a byte-identical
    // re-store cannot introduce a new target address, and the dominant
    // per-draw re-registration path stays index-free.
    IndexSurfaceDestinationRelationTargets(registry, relation);
    PublishSurfaceDestinationRelationGeneration(registry);
  }
}

void StorePrivateTextureCopyDirectAction(
    FableGuestResourceRegistry& registry,
    const FablePrivateTextureCopyAction& action,
    FableRegistryRepairMode repair_mode) {
  if (action.source_texture == 0) {
    return;
  }

  // Direct single-value stores below only bump the index serial when the
  // stored value meaningfully changed — see PrivateTextureCopyActionStoreEquivalent.
  const auto store_direct_action =
      [&](std::unordered_map<uint64_t, FablePrivateTextureCopyAction>& index,
          uint64_t key) {
        const auto it = index.find(key);
        if (it != index.end() &&
            PrivateTextureCopyActionStoreEquivalent(action, it->second)) {
          it->second = action;
          return;
        }
        index[key] = action;
        ++registry.private_texture_copy_index_serial;
      };

  if (action.target_surface == 0) {
    if (action.wrapper_call_serial != 0) {
      store_direct_action(registry.private_texture_copy_action_by_wrapper_call,
                          action.wrapper_call_serial);
    }
    if (action.wrapper_context_key != 0) {
      store_direct_action(
          registry.private_texture_copy_action_by_wrapper_context,
          action.wrapper_context_key);
    }
    if (repair_mode == FableRegistryRepairMode::kPropagateRepairs) {
      RepairSurfaceTextureLinksForPrivateCopyAction(registry, action);
    }
    return;
  }

  std::vector<uint32_t> destination_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  destination_keys.reserve(8);
  base_keys.reserve(8);
  fetch_keys.reserve(8);
  CollectTextureIdentityIndexKeysForObject(registry, action.source_texture,
                                           destination_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForObject(registry, action.backing_texture,
                                           destination_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, action.source_base_address,
                                         destination_keys, base_keys,
                                         fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, action.backing_base_address,
                                         destination_keys, base_keys,
                                         fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(action.source_fetch_dwords),
      destination_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(action.backing_fetch_dwords),
      destination_keys, base_keys, fetch_keys);
  auto add_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(destination_keys.begin(), destination_keys.end(), key) ==
        destination_keys.end()) {
      destination_keys.push_back(key);
    }
  };
  add_destination_key(action.target_surface);
  switch (action.kind) {
    case FablePrivateTextureCopyKind::kColor:
    case FablePrivateTextureCopyKind::kColorSibling:
      add_destination_key(action.render_target);
      break;
    case FablePrivateTextureCopyKind::kDepth:
      add_destination_key(action.depth_stencil);
      break;
  }

  constexpr size_t kMaxPrivateTextureCopyActionsPerBucket = 32;
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  auto upsert_action = [&](std::vector<FablePrivateTextureCopyAction>& actions) {
    for (FablePrivateTextureCopyAction& existing : actions) {
      if (SamePrivateTextureCopyActionIdentity(existing, action)) {
        existing = action;
        return;
      }
    }
    if (actions.size() >= kMaxPrivateTextureCopyActionsPerBucket) {
      actions.erase(actions.begin());
    }
    actions.push_back(action);
  };
  std::vector<uint32_t> output_destination_keys;
  output_destination_keys.reserve(2);
  auto add_output_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(output_destination_keys.begin(),
                  output_destination_keys.end(),
                  key) == output_destination_keys.end()) {
      output_destination_keys.push_back(key);
    }
  };
  add_output_destination_key(action.target_surface);
  switch (action.kind) {
    case FablePrivateTextureCopyKind::kColor:
    case FablePrivateTextureCopyKind::kColorSibling:
      add_output_destination_key(action.render_target);
      break;
    case FablePrivateTextureCopyKind::kDepth:
      add_output_destination_key(action.depth_stencil);
      break;
  }
  for (uint32_t destination_texture : output_destination_keys) {
    upsert_action(
        registry.private_texture_copy_output_actions_by_destination_texture
            [destination_texture]);
    if (action.source_slot != kUnknownSlot) {
      const uint64_t slot_key = PrivateTextureCopyDestinationSlotKey(
          destination_texture, action.source_slot);
      upsert_action(
          registry
              .private_texture_copy_output_actions_by_destination_texture_slot
                  [slot_key]);
    }
  }

  for (uint32_t destination_texture : destination_keys) {
    upsert_action(
        registry
            .private_texture_copy_actions_by_destination_texture
                [destination_texture]);
    auto direct_it =
        registry.private_texture_copy_action_by_destination_texture.find(
            destination_texture);
    if (direct_it !=
            registry.private_texture_copy_action_by_destination_texture.end() &&
        PrivateTextureCopyActionStoreEquivalent(action, direct_it->second)) {
      direct_it->second = action;
    } else if (
        direct_it ==
            registry.private_texture_copy_action_by_destination_texture.end() ||
        PrivateTextureCopyActionShouldPrefer(action, &direct_it->second)) {
      registry
          .private_texture_copy_action_by_destination_texture
              [destination_texture] = action;
      ++registry.private_texture_copy_index_serial;
    }
    if (action.source_slot != kUnknownSlot) {
      const uint64_t slot_key = PrivateTextureCopyDestinationSlotKey(
          destination_texture, action.source_slot);
      upsert_action(
          registry
              .private_texture_copy_actions_by_destination_texture_slot
                  [slot_key]);
      auto slot_it =
          registry.private_texture_copy_action_by_destination_texture_slot.find(
              slot_key);
      if (slot_it !=
              registry.private_texture_copy_action_by_destination_texture_slot
                  .end() &&
          PrivateTextureCopyActionStoreEquivalent(action, slot_it->second)) {
        slot_it->second = action;
      } else if (
          slot_it ==
              registry.private_texture_copy_action_by_destination_texture_slot
                  .end() ||
          PrivateTextureCopyActionShouldPrefer(action, &slot_it->second)) {
        registry.private_texture_copy_action_by_destination_texture_slot
            [slot_key] = action;
        ++registry.private_texture_copy_index_serial;
      }
    }
  }
  if (action.wrapper_call_serial != 0) {
    store_direct_action(registry.private_texture_copy_action_by_wrapper_call,
                        action.wrapper_call_serial);
  }
  if (action.wrapper_context_key != 0) {
    store_direct_action(registry.private_texture_copy_action_by_wrapper_context,
                        action.wrapper_context_key);
  }
  if (repair_mode == FableRegistryRepairMode::kPropagateRepairs) {
    RepairSurfaceTextureLinksForPrivateCopyAction(registry, action);
  }
}

void RebuildPrivateTextureCopyDirectActions(FableGuestResourceRegistry& registry) {
  registry.private_texture_copy_action_by_destination_texture.clear();
  registry.private_texture_copy_action_by_destination_texture_slot.clear();
  registry.private_texture_copy_actions_by_destination_texture.clear();
  registry.private_texture_copy_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_output_actions_by_destination_texture.clear();
  registry.private_texture_copy_output_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_action_by_wrapper_call.clear();
  registry.private_texture_copy_action_by_wrapper_context.clear();
  ++registry.private_texture_copy_index_serial;
  for (const auto& [key, actions] : registry.private_texture_copies_by_source) {
    (void)key;
    for (const FablePrivateTextureCopyAction& action : actions) {
      StorePrivateTextureCopyDirectAction(
          registry, action, FableRegistryRepairMode::kPropagateRepairs);
    }
  }
}

void StoreSurfaceTextureDirectEdge(FableGuestResourceRegistry& registry,
                                   const FableSurfaceTextureLink& link) {
  if (!link.trusted_destination_edge || link.texture == 0 ||
      link.source_surface == 0 || SurfaceTextureLinkIsTrivialSelfEdge(link)) {
    return;
  }
  NoteSurfaceTextureLinkWrapperIdentitySeen(registry, link);
  if (link.copy_call_serial != 0) {
    const auto call_it =
        registry.surface_texture_link_by_copy_call.find(
            link.copy_call_serial);
    if (call_it == registry.surface_texture_link_by_copy_call.end() ||
        SurfaceTextureLinkShouldPrefer(link, &call_it->second)) {
      registry.surface_texture_link_by_copy_call[link.copy_call_serial] =
          link;
    }
  }
  if (link.copy_context_key != 0) {
    const auto context_it =
        registry.surface_texture_link_by_copy_context.find(
            link.copy_context_key);
    if (context_it == registry.surface_texture_link_by_copy_context.end() ||
        SurfaceTextureLinkShouldPrefer(link, &context_it->second)) {
      registry.surface_texture_link_by_copy_context[link.copy_context_key] =
          link;
    }
  }
  const bool requires_exact_wrapper_lookup =
      SurfaceTextureLinkRequiresExactWrapperLookup(link);
  const auto source_surface_it =
      registry.surface_texture_link_by_source_surface.find(link.source_surface);
  if (source_surface_it ==
          registry.surface_texture_link_by_source_surface.end() ||
      SurfaceTextureLinkShouldPrefer(link, &source_surface_it->second)) {
    registry.surface_texture_link_by_source_surface[link.source_surface] =
        link;
  }
  constexpr uint32_t kUnknownSlot = 0xFFFFFFFFu;
  if (link.source_slot != kUnknownSlot) {
    const uint64_t source_slot_key =
        SurfaceTextureSourceSurfaceSlotKey(link.source_surface,
                                           link.source_slot);
    const auto source_slot_it =
        registry.surface_texture_link_by_source_surface_slot.find(
            source_slot_key);
    if (source_slot_it ==
            registry.surface_texture_link_by_source_surface_slot.end() ||
        SurfaceTextureLinkShouldPrefer(link, &source_slot_it->second)) {
      registry.surface_texture_link_by_source_surface_slot[source_slot_key] =
          link;
    }
  }
  if (requires_exact_wrapper_lookup) {
    return;
  }

  std::vector<uint32_t> texture_destination_keys;
  std::vector<uint32_t> texture_base_keys;
  std::vector<uint64_t> texture_fetch_keys;
  texture_destination_keys.reserve(8);
  texture_base_keys.reserve(8);
  texture_fetch_keys.reserve(8);
  CollectTextureIdentityIndexKeysForObject(registry, link.texture,
                                           texture_destination_keys,
                                           texture_base_keys,
                                           texture_fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, link.texture_base_address,
                                         texture_destination_keys,
                                         texture_base_keys,
                                         texture_fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(link.texture_fetch_dwords),
      texture_destination_keys, texture_base_keys, texture_fetch_keys);
  std::vector<uint32_t> exact_destination_keys = texture_destination_keys;
  exact_destination_keys.reserve(texture_destination_keys.size() + 2);
  auto add_exact_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(exact_destination_keys.begin(),
                  exact_destination_keys.end(),
                  key) == exact_destination_keys.end()) {
      exact_destination_keys.push_back(key);
    }
  };
  add_exact_destination_key(link.texture);
  add_exact_destination_key(link.texture_base_address);
  auto upsert_exact_destination_link =
      [&](std::vector<FableSurfaceTextureLink>& links) {
        for (FableSurfaceTextureLink& existing : links) {
          if (SameSurfaceTextureLinkIdentity(existing, link)) {
            existing = MergeSurfaceTextureLinkMetadata(link, existing);
            return;
          }
        }
        constexpr size_t kMaxSurfaceTextureLinksPerBucket = 32;
        if (links.size() >= kMaxSurfaceTextureLinksPerBucket) {
          links.erase(links.begin());
        }
        links.push_back(link);
      };
  for (uint32_t destination_texture : exact_destination_keys) {
    upsert_exact_destination_link(
        registry
            .surface_texture_exact_links_by_destination_texture
                [destination_texture]);
    const auto direct_it =
        registry.surface_texture_exact_link_by_destination_texture.find(
            destination_texture);
    if (direct_it ==
            registry.surface_texture_exact_link_by_destination_texture.end() ||
        SurfaceTextureLinkShouldPrefer(link, &direct_it->second)) {
      registry.surface_texture_exact_link_by_destination_texture
          [destination_texture] = link;
    }
    if (link.source_slot != kUnknownSlot) {
      const uint64_t slot_key =
          SurfaceTextureDestinationSlotKey(destination_texture,
                                           link.source_slot);
      upsert_exact_destination_link(
          registry
              .surface_texture_exact_links_by_destination_texture_slot
                  [slot_key]);
      const auto slot_it =
          registry.surface_texture_exact_link_by_destination_texture_slot.find(
              slot_key);
      if (slot_it ==
              registry.surface_texture_exact_link_by_destination_texture_slot
                  .end() ||
          SurfaceTextureLinkShouldPrefer(link, &slot_it->second)) {
        registry.surface_texture_exact_link_by_destination_texture_slot
            [slot_key] = link;
      }
    }
    StoreSurfaceDestinationRelation(registry, link, destination_texture);
  }

  std::vector<uint32_t> destination_keys = texture_destination_keys;
  std::vector<uint32_t> base_keys = texture_base_keys;
  std::vector<uint64_t> fetch_keys = texture_fetch_keys;
  destination_keys.reserve(texture_destination_keys.size() + 4);
  base_keys.reserve(texture_base_keys.size() + 4);
  fetch_keys.reserve(texture_fetch_keys.size() + 2);
  CollectTextureIdentityIndexKeysForObject(registry, link.backing_texture,
                                           destination_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, link.backing_base_address,
                                         destination_keys, base_keys,
                                         fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(link.backing_fetch_dwords),
      destination_keys, base_keys, fetch_keys);
  auto add_destination_key = [&](uint32_t key) {
    if (key == 0) {
      return;
    }
    if (std::find(destination_keys.begin(), destination_keys.end(), key) ==
        destination_keys.end()) {
      destination_keys.push_back(key);
    }
  };
  add_destination_key(link.texture);
  add_destination_key(link.backing_texture);

  for (uint32_t destination_texture : destination_keys) {
    const auto direct_it =
        registry.surface_texture_link_by_destination_texture.find(
            destination_texture);
    if (direct_it ==
            registry.surface_texture_link_by_destination_texture.end() ||
        SurfaceTextureLinkShouldPrefer(link, &direct_it->second)) {
      ReplaceSurfaceDestinationTextureLinkLocked(registry, destination_texture,
                                                link);
    }
    if (link.source_slot != kUnknownSlot) {
      const uint64_t slot_key =
          SurfaceTextureDestinationSlotKey(destination_texture,
                                           link.source_slot);
      const auto slot_it =
          registry.surface_texture_link_by_destination_texture_slot.find(
              slot_key);
      if (slot_it ==
              registry.surface_texture_link_by_destination_texture_slot.end() ||
          SurfaceTextureLinkShouldPrefer(link, &slot_it->second)) {
        registry.surface_texture_link_by_destination_texture_slot[slot_key] =
            link;
      }
    }
  }
  for (uint32_t base_key : base_keys) {
    if (base_key == 0) {
      continue;
    }
    const auto base_it = registry.surface_texture_link_by_base.find(base_key);
    if (base_it == registry.surface_texture_link_by_base.end() ||
        SurfaceTextureLinkShouldPrefer(link, &base_it->second)) {
      registry.surface_texture_link_by_base[base_key] = link;
    }
  }
  for (uint64_t fetch_key : fetch_keys) {
    if (fetch_key == 0) {
      continue;
    }
    const auto fetch_it =
        registry.surface_texture_link_by_fetch_key.find(fetch_key);
    if (fetch_it == registry.surface_texture_link_by_fetch_key.end() ||
        SurfaceTextureLinkShouldPrefer(link, &fetch_it->second)) {
      registry.surface_texture_link_by_fetch_key[fetch_key] = link;
    }
  }

}

void RebuildSurfaceTextureDirectEdges(FableGuestResourceRegistry& registry) {
  registry.surface_texture_link_by_destination_texture.clear();
  registry.surface_texture_link_by_destination_texture_slot.clear();
  registry.surface_destination_texture_links_by_object.clear();
  registry.surface_destination_texture_links_by_base.clear();
  registry.surface_destination_texture_links_by_fetch_key.clear();
  registry.surface_texture_exact_link_by_destination_texture.clear();
  registry.surface_texture_exact_link_by_destination_texture_slot.clear();
  registry.surface_texture_exact_links_by_destination_texture.clear();
  registry.surface_texture_exact_links_by_destination_texture_slot.clear();
  registry.surface_texture_link_by_source_surface.clear();
  registry.surface_texture_link_by_source_surface_slot.clear();
  registry.surface_texture_link_by_base.clear();
  registry.surface_texture_link_by_fetch_key.clear();
  registry.surface_texture_link_by_copy_call.clear();
  registry.surface_texture_link_by_copy_context.clear();
  registry.surface_destination_relations_by_source_surface.clear();
  registry.surface_destination_relations_by_destination_texture.clear();
  registry.surface_destination_relations_by_destination_texture_slot.clear();
  registry.surface_destination_relation_by_destination_texture.clear();
  registry.surface_destination_relation_by_destination_texture_slot.clear();
  registry.surface_destination_relation_buckets_by_target_address.clear();
  registry.surface_destination_relation_buckets_by_target_fetch_key.clear();
  for (const auto& [key, links] : registry.surface_texture_links_by_texture) {
    (void)key;
    for (const FableSurfaceTextureLink& link : links) {
      StoreSurfaceTextureDirectEdge(registry, link);
    }
  }
}

bool TextureIdentityMayRekeyPrivateCopyDirectActions(
    const FableGuestResourceRegistry& registry,
    const FableTextureIdentity& identity,
    uint64_t fetch_key) {
  if (identity.object_texture != 0 &&
      (registry.private_texture_copies_by_source.find(identity.object_texture) !=
           registry.private_texture_copies_by_source.end() ||
       registry.private_texture_copies_by_target.find(identity.object_texture) !=
           registry.private_texture_copies_by_target.end())) {
    return true;
  }
  if (identity.parent_texture != 0 &&
      identity.parent_texture != identity.object_texture &&
      (registry.private_texture_copies_by_source.find(identity.parent_texture) !=
           registry.private_texture_copies_by_source.end() ||
       registry.private_texture_copies_by_target.find(identity.parent_texture) !=
           registry.private_texture_copies_by_target.end())) {
    return true;
  }
  if (identity.base_address != 0 &&
      registry.private_texture_copies_by_base.find(identity.base_address) !=
          registry.private_texture_copies_by_base.end()) {
    return true;
  }
  return fetch_key != 0 &&
         registry.private_texture_copies_by_fetch_key.find(fetch_key) !=
             registry.private_texture_copies_by_fetch_key.end();
}

bool TextureIdentityMayRekeySurfaceTextureDirectEdges(
    const FableGuestResourceRegistry& registry,
    const FableTextureIdentity& identity,
    uint64_t fetch_key) {
  if (identity.object_texture != 0 &&
      (registry.surface_texture_links_by_texture.find(identity.object_texture) !=
           registry.surface_texture_links_by_texture.end() ||
       registry.surface_texture_links_by_surface.find(identity.object_texture) !=
           registry.surface_texture_links_by_surface.end())) {
    return true;
  }
  if (identity.parent_texture != 0 &&
      identity.parent_texture != identity.object_texture &&
      (registry.surface_texture_links_by_texture.find(identity.parent_texture) !=
           registry.surface_texture_links_by_texture.end() ||
       registry.surface_texture_links_by_surface.find(identity.parent_texture) !=
           registry.surface_texture_links_by_surface.end())) {
    return true;
  }
  if (identity.base_address != 0 &&
      registry.surface_texture_links_by_base.find(identity.base_address) !=
          registry.surface_texture_links_by_base.end()) {
    return true;
  }
  return fetch_key != 0 &&
         registry.surface_texture_links_by_fetch_key.find(fetch_key) !=
             registry.surface_texture_links_by_fetch_key.end();
}

// Matcher-parameterized forms of the reference predicates (2026-08-02, run
// 513). The per-resource forget recipes and the batched retirement flush
// (FableDirectForgetResourceRegistrationsBatch) share the same field
// semantics; only the membership test differs ("== resource" vs "in the
// retired set"), so the field list lives here exactly once.
template <typename ResourceMatcher>
bool SurfaceDestinationRelationReferencesMatched(
    const FableSurfaceDestinationTextureRelation& relation,
    const ResourceMatcher& matches) {
  return matches(relation.source_surface) ||
         matches(relation.destination_texture) ||
         SurfaceTextureLinkReferencesMatched(relation.link, matches);
}


// Membership in a retired-address set. The sets never contain 0 (the batch
// builders skip it), so zero-valued fields (unset render_target /
// depth_stencil / ...) can never match — same guarantee the per-resource
// callers get from their resource != 0 early-outs.
struct ResourceSetMatcher {
  const std::unordered_set<uint32_t>* resources;
  bool operator()(uint32_t value) const {
    return resources->count(value) != 0;
  }
};

template <typename ResourceMatcher>
void PrunePrivateTextureCopyDirectActionsMatchedLocked(
    FableGuestResourceRegistry& registry, const ResourceMatcher& matches) {
  for (auto it =
           registry.private_texture_copy_action_by_destination_texture.begin();
       it != registry.private_texture_copy_action_by_destination_texture.end();) {
    if (matches(it->first) ||
        PrivateTextureCopyActionReferencesMatched(it->second, matches)) {
      it = registry.private_texture_copy_action_by_destination_texture.erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
  for (auto it =
           registry.private_texture_copy_action_by_destination_texture_slot
               .begin();
       it != registry.private_texture_copy_action_by_destination_texture_slot
                 .end();) {
    if (PrivateTextureCopyActionReferencesMatched(it->second, matches)) {
      it = registry.private_texture_copy_action_by_destination_texture_slot
               .erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
  auto prune_action_index =
      [&](auto& index, bool key_is_resource) {
        for (auto it = index.begin(); it != index.end();) {
          auto& actions = it->second;
          actions.erase(
              std::remove_if(actions.begin(), actions.end(),
                             [&](const FablePrivateTextureCopyAction& action) {
                               return PrivateTextureCopyActionReferencesMatched(
                                   action, matches);
                             }),
              actions.end());
          if (actions.empty() ||
              (key_is_resource &&
               matches(static_cast<uint32_t>(it->first)))) {
            it = index.erase(it);
            ++registry.private_texture_copy_index_serial;
          } else {
            ++it;
          }
        }
      };
  prune_action_index(
      registry.private_texture_copy_actions_by_destination_texture, true);
  prune_action_index(
      registry.private_texture_copy_actions_by_destination_texture_slot, false);
  prune_action_index(
      registry.private_texture_copy_output_actions_by_destination_texture, true);
  prune_action_index(
      registry.private_texture_copy_output_actions_by_destination_texture_slot,
      false);
  for (auto it =
           registry.private_texture_copy_action_by_wrapper_call.begin();
       it != registry.private_texture_copy_action_by_wrapper_call.end();) {
    if (PrivateTextureCopyActionReferencesMatched(it->second, matches)) {
      it = registry.private_texture_copy_action_by_wrapper_call.erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
  for (auto it =
           registry.private_texture_copy_action_by_wrapper_context.begin();
       it != registry.private_texture_copy_action_by_wrapper_context.end();) {
    if (PrivateTextureCopyActionReferencesMatched(it->second, matches)) {
      it = registry.private_texture_copy_action_by_wrapper_context.erase(it);
      ++registry.private_texture_copy_index_serial;
    } else {
      ++it;
    }
  }
}

void PrunePrivateTextureCopyDirectActionsForResource(
    FableGuestResourceRegistry& registry, uint32_t resource) {
  PrunePrivateTextureCopyDirectActionsMatchedLocked(
      registry, SingleResourceMatcher{resource});
}

template <typename ResourceMatcher>
void PruneSurfaceTextureDirectEdgesMatchedLocked(
    FableGuestResourceRegistry& registry, const ResourceMatcher& matches) {
  auto prune_link_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if ((prune_resource_key &&
           matches(static_cast<uint32_t>(it->first))) ||
          SurfaceTextureLinkReferencesMatched(it->second, matches)) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  auto prune_link_vector_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          matches(static_cast<uint32_t>(it->first))) {
        it = index.erase(it);
        continue;
      }
      std::vector<FableSurfaceTextureLink>& links = it->second;
      links.erase(
          std::remove_if(links.begin(), links.end(),
                         [&](const FableSurfaceTextureLink& link) {
                           return SurfaceTextureLinkReferencesMatched(
                               link, matches);
                         }),
          links.end());
      if (links.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  for (auto it = registry.surface_texture_link_by_destination_texture.begin();
       it != registry.surface_texture_link_by_destination_texture.end();) {
    if (matches(it->first) ||
        SurfaceTextureLinkReferencesMatched(it->second, matches)) {
      RemoveSurfaceDestinationTextureLinkReverseIndex(registry, it->first,
                                                     it->second);
      it = registry.surface_texture_link_by_destination_texture.erase(it);
    } else {
      ++it;
    }
  }
  for (auto it =
           registry.surface_texture_link_by_destination_texture_slot.begin();
       it != registry.surface_texture_link_by_destination_texture_slot.end();) {
    if (SurfaceTextureLinkReferencesMatched(it->second, matches)) {
      it = registry.surface_texture_link_by_destination_texture_slot.erase(it);
    } else {
      ++it;
    }
  }
  prune_link_map(registry.surface_texture_exact_link_by_destination_texture,
                 true);
  prune_link_map(registry.surface_texture_exact_link_by_destination_texture_slot,
                 false);
  prune_link_map(registry.surface_texture_link_by_copy_call, false);
  prune_link_map(registry.surface_texture_link_by_copy_context, false);
  prune_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture, true);
  prune_link_vector_map(
      registry.surface_texture_exact_links_by_destination_texture_slot, false);
  prune_link_map(registry.surface_texture_link_by_source_surface, true);
  prune_link_map(registry.surface_texture_link_by_source_surface_slot, false);
  prune_link_map(registry.surface_texture_link_by_base, true);
  prune_link_map(registry.surface_texture_link_by_fetch_key, false);

  auto prune_relation_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if ((prune_resource_key &&
           matches(static_cast<uint32_t>(it->first))) ||
          SurfaceDestinationRelationReferencesMatched(it->second, matches)) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  auto prune_relation_vector_map = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          matches(static_cast<uint32_t>(it->first))) {
        it = index.erase(it);
        continue;
      }
      std::vector<FableSurfaceDestinationTextureRelation>& relations =
          it->second;
      relations.erase(
          std::remove_if(
              relations.begin(), relations.end(),
              [&](const FableSurfaceDestinationTextureRelation& relation) {
                return SurfaceDestinationRelationReferencesMatched(relation,
                                                                   matches);
              }),
          relations.end());
      if (relations.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };
  prune_relation_vector_map(
      registry.surface_destination_relations_by_source_surface, true);
  prune_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture, true);
  prune_relation_vector_map(
      registry.surface_destination_relations_by_destination_texture_slot, false);
  prune_relation_map(registry.surface_destination_relation_by_destination_texture,
                     true);
  prune_relation_map(
      registry.surface_destination_relation_by_destination_texture_slot, false);
}

void PruneSurfaceTextureDirectEdgesForResource(
    FableGuestResourceRegistry& registry, uint32_t resource) {
  PruneSurfaceTextureDirectEdgesMatchedLocked(registry,
                                              SingleResourceMatcher{resource});
}

// The cheap keyed-presence gates the per-resource forgets use to skip their
// full-table walks, extracted so the batched flush can apply the SAME
// eligibility per address. An address that never appears as a key in any of
// these maps skips the walks; entries referencing it only through non-key
// fields are left to the private-registration TTL sweep — exactly the
// per-resource behaviour.
bool HasIndexedPrivateTextureCopyActionLocked(
    const FableGuestResourceRegistry& registry, uint32_t resource) {
  return registry.private_texture_copies_by_source.find(resource) !=
             registry.private_texture_copies_by_source.end() ||
         registry.private_texture_copies_by_target.find(resource) !=
             registry.private_texture_copies_by_target.end() ||
         registry.private_texture_copies_by_base.find(resource) !=
             registry.private_texture_copies_by_base.end() ||
         registry.private_texture_copy_source_by_base.find(resource) !=
             registry.private_texture_copy_source_by_base.end() ||
         registry.private_texture_copy_action_by_destination_texture.find(
             resource) !=
             registry.private_texture_copy_action_by_destination_texture.end();
}

bool HasIndexedSurfaceTextureLinkLocked(
    const FableGuestResourceRegistry& registry, uint32_t resource) {
  return registry.surface_texture_links_by_texture.find(resource) !=
             registry.surface_texture_links_by_texture.end() ||
         registry.surface_texture_links_by_surface.find(resource) !=
             registry.surface_texture_links_by_surface.end() ||
         registry.surface_texture_links_by_base.find(resource) !=
             registry.surface_texture_links_by_base.end() ||
         registry.surface_texture_link_by_source_surface.find(resource) !=
             registry.surface_texture_link_by_source_surface.end() ||
         registry.surface_destination_relations_by_source_surface.find(
             resource) !=
             registry.surface_destination_relations_by_source_surface.end() ||
         registry.surface_destination_relations_by_destination_texture.find(
             resource) !=
             registry.surface_destination_relations_by_destination_texture
                 .end() ||
         registry.surface_destination_relation_by_destination_texture.find(
             resource) !=
             registry.surface_destination_relation_by_destination_texture
                 .end();
}

// Primary-multimap walks of the two forget recipes, matcher-parameterized so
// one walk can serve a whole retirement batch. Returns whether anything was
// removed (the callers own the serial bumps).

// Shared core of the batched forget (2026-08-02, run 513): prunes the action
// and link registrations for whole SETS of addresses in ONE pass over the
// tables. The per-resource forgets walk every primary multimap plus every
// derived private-copy/link index end-to-end PER ADDRESS (~35 map walks,
// 60-85us each), so the sync path's destroyed retirements + staleness
// evictions — ~100-150 per-frame synthetic surface mints at a settled title —
// paid O(batch x tables) and ran into their retirement budgets every compose
// (run 513: destroyed_sweep=8.4-8.9ms + destroyed_retire=2-3.3ms). Same shape
// as the batched link repair (RepairSurfaceTextureLinksForPrivateCopyActions-
// Locked above): the walk predicates are pure per-entry membership tests, so
// forgetting a set in one pass removes exactly the union of what per-address
// forgets would remove — O(tables + batch) instead of O(batch x tables).
//
// The three sets carry the per-resource recipes' eligibility, superset-safe:
// - action_matched / link_matched: addresses that passed the respective
//   has-indexed gate; the primary walks run once over each union.
// - derived_matched: identity holders plus every gated address. The
//   per-resource path gated derived pruning on "primary removal happened";
//   pruning for the whole gated set can only remove MORE entries referencing
//   addresses being retired in this same call — the gate was a cost
//   heuristic, not an eligibility rule.
void ForgetPrivateRegistrationsForResourceSetsLocked(
    FableGuestResourceRegistry& registry,
    const std::unordered_set<uint32_t>& action_matched,
    const std::unordered_set<uint32_t>& link_matched,
    const std::unordered_set<uint32_t>& derived_matched) {
  bool removed_actions = false;
  if (!action_matched.empty()) {
    removed_actions = PrunePrimaryPrivateTextureCopyActionsMatchedLocked(
        registry, ResourceSetMatcher{&action_matched});
  }
  bool removed_links = false;
  if (!link_matched.empty()) {
    removed_links = PrunePrimarySurfaceTextureLinksMatchedLocked(
        registry, ResourceSetMatcher{&link_matched});
  }
  if (!derived_matched.empty()) {
    const ResourceSetMatcher derived{&derived_matched};
    PrunePrivateTextureCopyDirectActionsMatchedLocked(registry, derived);
    PruneSurfaceTextureDirectEdgesMatchedLocked(registry, derived);
  }
  if (removed_actions) {
    ++registry.private_texture_copy_serial;
  }
  if (removed_links) {
    ++registry.surface_texture_link_serial;
  }
}

void PrunePrivateTextureCopyDirectActionsForTextureIdentityLocked(
    FableGuestResourceRegistry& registry, const FableTextureIdentity& identity) {
  std::vector<uint32_t> resource_keys;
  AddUnique(resource_keys, identity.object_texture);
  AddUnique(resource_keys, identity.parent_texture);
  AddUnique(resource_keys, identity.base_address);
  for (uint32_t resource : resource_keys) {
    PrunePrivateTextureCopyDirectActionsForResource(registry, resource);
  }
}

void PruneSurfaceTextureDirectEdgesForTextureIdentityLocked(
    FableGuestResourceRegistry& registry, const FableTextureIdentity& identity) {
  std::vector<uint32_t> resource_keys;
  AddUnique(resource_keys, identity.object_texture);
  AddUnique(resource_keys, identity.parent_texture);
  AddUnique(resource_keys, identity.base_address);
  for (uint32_t resource : resource_keys) {
    PruneSurfaceTextureDirectEdgesForResource(registry, resource);
  }

  const uint64_t fetch_key = FableDirectTextureFetchKey(identity.fetch_dwords);
  if (fetch_key != 0) {
    registry.surface_texture_link_by_fetch_key.erase(fetch_key);
  }
}


// Run-477 identity-backfill budget (all under the registry mutex). The eager
// per-registration backfill is O(collected bank) with millisecond constants
// (run 473 measured 3-6ms per registration), and a transition frame following
// an idle TTL-swept menu re-registers EVERYTHING first-sight: ~100+
// registrations x 3-6ms x banks growing as they re-fill produced the
// multi-second composes of runs 473/474/477. The compose resets this budget
// once per frame (FableDirectDrainPendingTextureIdentityBackfills);
// registrations arriving after it is spent defer their OBJECT key to the
// carry-over queue instead of backfilling inline, and the drain completes
// them (against the object's CURRENT identity) over the following frames.
// Deferral is the pipeline's existing propagation model, not a new
// approximation class: identity/action registrations already arrive through
// drained pending queues one compose late.
constexpr double kInlineIdentityBackfillBudgetMs = 2.0;
constexpr size_t kMaxPendingIdentityBackfills = 256;
double g_inline_identity_backfill_ms_used = 0.0;
std::vector<uint32_t> g_pending_identity_backfill_objects;
FablePrivateBackfillCensus g_private_backfill_census;

void EnqueuePendingIdentityBackfillLocked(uint32_t object_texture) {
  if (object_texture == 0) {
    return;
  }
  std::vector<uint32_t>& pending = g_pending_identity_backfill_objects;
  if (std::find(pending.begin(), pending.end(), object_texture) !=
      pending.end()) {
    return;
  }
  if (pending.size() >= kMaxPendingIdentityBackfills) {
    // Drop-oldest: recurring content re-registers every frame, so a dropped
    // entry self-heals on its object's next registration.
    pending.erase(pending.begin());
    ++g_private_backfill_census.dropped;
  }
  pending.push_back(object_texture);
  ++g_private_backfill_census.deferred;
}

void BackfillPrivateTextureCopyActionDirectIndexesLocked(
    FableGuestResourceRegistry& registry,
    const FablePrivateTextureCopyAction& action) {
  FablePrivateTextureCopyAction stored = action;
  NormalizePrivateTextureCopySampleSource(stored);
  if (stored.serial == 0) {
    stored.serial = ++registry.private_texture_copy_serial;
  }
  StorePrivateTextureCopyDirectAction(
      registry, stored, FableRegistryRepairMode::kSkipBackfillRepairs);
}

void BackfillSurfaceTextureLinkDirectIndexesLocked(
    FableGuestResourceRegistry& registry, const FableSurfaceTextureLink& link) {
  if (link.texture == 0 || link.source_surface == 0) {
    return;
  }
  if ((link.source_surface & 0xFF000000u) == 0xF1000000u) {
    return;
  }

  FableSurfaceTextureLink stored = link;
  EnrichSurfaceTextureLinkShaderRootsFromRegisteredActions(registry, stored);
  NormalizeSurfaceTextureLinkSampleSource(stored);
  if (SurfaceTextureLinkIsTrivialSelfEdge(stored)) {
    return;
  }
  const bool has_exact_copy_identity =
      stored.copy_call_serial != 0 || stored.copy_context_key != 0 ||
      stored.copy_symbol_address != 0;
  if (stored.trusted_destination_edge && !has_exact_copy_identity &&
      stored.backing_texture == 0 &&
      stored.backing_base_address == 0 && stored.target_content_serial == 0 &&
      stored.sampled_source.kind ==
          FableRegisteredSampleSourceKind::kGpuSurface) {
    return;
  }
  if (stored.serial == 0) {
    stored.serial = ++registry.surface_texture_link_serial;
  }
  StoreSurfaceTextureDirectEdge(registry, stored);
}

TextureIdentityBackfillStats BackfillTextureIdentityDependentIndexesLocked(
    FableGuestResourceRegistry& registry,
    const FableTextureIdentity& identity) {
  std::vector<uint32_t> object_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  object_keys.reserve(8);
  base_keys.reserve(8);
  fetch_keys.reserve(8);
  CollectTextureIdentityIndexKeys(identity, object_keys, base_keys, fetch_keys);

  std::vector<FablePrivateTextureCopyAction> actions;
  actions.reserve(64);
  const FableGuestResourceRegistryClock::time_point collect_actions_t0 =
      FableGuestResourceRegistryClock::now();
  const auto collect_action_bucket =
      [&](const std::vector<FablePrivateTextureCopyAction>* bucket) {
        if (!bucket) {
          return;
        }
        for (const FablePrivateTextureCopyAction& action : *bucket) {
          const auto existing = std::find_if(
              actions.begin(), actions.end(),
              [&](const FablePrivateTextureCopyAction& candidate) {
                return SamePrivateTextureCopyActionIdentity(candidate, action);
              });
          if (existing == actions.end()) {
            actions.push_back(action);
          }
        }
      };
  const auto find_bucket = [](const auto& index, auto key) {
    const auto it = index.find(key);
    return it != index.end() ? &it->second : nullptr;
  };
  for (uint32_t object_key : object_keys) {
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_source, object_key));
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_target, object_key));
  }
  for (uint32_t base_key : base_keys) {
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_base, base_key));
  }
  for (uint64_t fetch_key : fetch_keys) {
    collect_action_bucket(
        find_bucket(registry.private_texture_copies_by_fetch_key, fetch_key));
  }
  const FableGuestResourceRegistryClock::time_point collect_actions_t1 =
      FableGuestResourceRegistryClock::now();

  std::vector<FableSurfaceTextureLink> links;
  links.reserve(96);
  const FableGuestResourceRegistryClock::time_point collect_links_t0 =
      FableGuestResourceRegistryClock::now();
  const auto collect_link_bucket =
      [&](const std::vector<FableSurfaceTextureLink>* bucket) {
        if (!bucket) {
          return;
        }
        for (const FableSurfaceTextureLink& link : *bucket) {
          const auto existing = std::find_if(
              links.begin(), links.end(),
              [&](const FableSurfaceTextureLink& candidate) {
                return SameSurfaceTextureLinkIdentity(candidate, link);
              });
          if (existing == links.end()) {
            links.push_back(link);
          }
        }
      };
  for (uint32_t object_key : object_keys) {
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_texture, object_key));
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_surface, object_key));
  }
  for (uint32_t base_key : base_keys) {
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_base, base_key));
  }
  for (uint64_t fetch_key : fetch_keys) {
    collect_link_bucket(
        find_bucket(registry.surface_texture_links_by_fetch_key, fetch_key));
  }
  const FableGuestResourceRegistryClock::time_point collect_links_t1 =
      FableGuestResourceRegistryClock::now();

  std::vector<uint32_t> destination_candidates;
  destination_candidates.reserve(32);
  const FableGuestResourceRegistryClock::time_point collect_destinations_t0 =
      FableGuestResourceRegistryClock::now();
  const auto collect_destination_bucket =
      [&](const std::vector<uint32_t>* bucket) {
        if (!bucket) {
          return;
        }
        for (uint32_t destination : *bucket) {
          AddUnique(destination_candidates, destination);
        }
      };
  for (uint32_t object_key : object_keys) {
    collect_destination_bucket(find_bucket(
        registry.surface_destination_texture_links_by_object, object_key));
  }
  for (uint32_t base_key : base_keys) {
    collect_destination_bucket(find_bucket(
        registry.surface_destination_texture_links_by_base, base_key));
  }
  for (uint64_t fetch_key : fetch_keys) {
    collect_destination_bucket(find_bucket(
        registry.surface_destination_texture_links_by_fetch_key, fetch_key));
  }
  for (uint32_t destination : destination_candidates) {
    const auto destination_it =
        registry.surface_texture_link_by_destination_texture.find(destination);
    if (destination_it ==
        registry.surface_texture_link_by_destination_texture.end()) {
      continue;
    }
    const FableSurfaceTextureLink& link = destination_it->second;
    const bool endpoint_match =
        std::find(object_keys.begin(), object_keys.end(), link.texture) !=
            object_keys.end() ||
        std::find(object_keys.begin(), object_keys.end(),
                  link.backing_texture) != object_keys.end() ||
        std::find(base_keys.begin(), base_keys.end(),
                  link.texture_base_address) != base_keys.end() ||
        std::find(base_keys.begin(), base_keys.end(),
                  link.backing_base_address) != base_keys.end() ||
        std::find(fetch_keys.begin(), fetch_keys.end(),
                  FableDirectTextureFetchKey(link.texture_fetch_dwords)) !=
            fetch_keys.end() ||
        std::find(fetch_keys.begin(), fetch_keys.end(),
                  FableDirectTextureFetchKey(link.backing_fetch_dwords)) !=
            fetch_keys.end();
    if (!endpoint_match) {
      continue;
    }
    const auto existing = std::find_if(
        links.begin(), links.end(),
        [&](const FableSurfaceTextureLink& candidate) {
          return SameSurfaceTextureLinkIdentity(candidate, link);
        });
    if (existing == links.end()) {
      links.push_back(link);
    }
  }
  const FableGuestResourceRegistryClock::time_point collect_destinations_t1 =
      FableGuestResourceRegistryClock::now();

  const FableGuestResourceRegistryClock::time_point register_actions_t0 =
      FableGuestResourceRegistryClock::now();
  for (const FablePrivateTextureCopyAction& action : actions) {
    BackfillPrivateTextureCopyActionDirectIndexesLocked(registry, action);
  }
  const FableGuestResourceRegistryClock::time_point register_actions_t1 =
      FableGuestResourceRegistryClock::now();

  const FableGuestResourceRegistryClock::time_point register_links_t0 =
      FableGuestResourceRegistryClock::now();
  for (const FableSurfaceTextureLink& link : links) {
    BackfillSurfaceTextureLinkDirectIndexesLocked(registry, link);
  }
  const FableGuestResourceRegistryClock::time_point register_links_t1 =
      FableGuestResourceRegistryClock::now();

  return TextureIdentityBackfillStats{
      actions.size(),
      links.size(),
      destination_candidates.size(),
      FableGuestResourceRegistryElapsedMs(collect_actions_t0,
                                          collect_actions_t1),
      FableGuestResourceRegistryElapsedMs(collect_links_t0, collect_links_t1),
      FableGuestResourceRegistryElapsedMs(collect_destinations_t0,
                                          collect_destinations_t1),
      FableGuestResourceRegistryElapsedMs(register_actions_t0,
                                          register_actions_t1),
      FableGuestResourceRegistryElapsedMs(register_links_t0,
                                          register_links_t1)};
}

void RemoveTextureIdentityIndexesLocked(FableGuestResourceRegistry& registry,
                                        const FableTextureIdentity& identity) {
  const uint32_t object_texture = identity.object_texture;
  const uint32_t base_address = identity.base_address;
  const uint64_t fetch_key = FableDirectTextureFetchKey(identity.fetch_dwords);

  if (base_address != 0) {
    const auto base_objects_it =
        registry.texture_identity_objects_by_base.find(base_address);
    if (base_objects_it != registry.texture_identity_objects_by_base.end()) {
      std::vector<uint32_t>& objects = base_objects_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(), object_texture),
                    objects.end());
      if (objects.empty()) {
        registry.texture_identity_objects_by_base.erase(base_objects_it);
      }
    }
  }

  if (fetch_key != 0) {
    const auto fetch_it =
        registry.texture_identity_objects_by_fetch_key.find(fetch_key);
    if (fetch_it != registry.texture_identity_objects_by_fetch_key.end()) {
      std::vector<uint32_t>& objects = fetch_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(), object_texture),
                    objects.end());
      if (objects.empty()) {
        registry.texture_identity_objects_by_fetch_key.erase(fetch_it);
      }
    }
  }
}

void RegisterTextureActionIdentityIfUnclaimedLocked(
    FableGuestResourceRegistry& registry, uint32_t object_texture,
    uint32_t parent_texture, uint32_t base_address, uint32_t byte_size,
    uint32_t width, uint32_t height, uint32_t format,
    const std::array<uint32_t, 6>& fetch_dwords) {
  if (object_texture == 0 || base_address == 0 || width == 0 ||
      height == 0 || !HasTextureIdentityFetchDwords(fetch_dwords)) {
    return;
  }

  const auto existing_it =
      registry.texture_identities_by_object.find(object_texture);
  if (existing_it != registry.texture_identities_by_object.end()) {
    if (existing_it->second.meaningful_upload ||
        existing_it->second.upload_generation != 0) {
      return;
    }
    FableTextureIdentity candidate;
    candidate.object_texture = object_texture;
    candidate.parent_texture =
        parent_texture != 0 ? parent_texture : object_texture;
    candidate.base_address = base_address;
    candidate.byte_size = byte_size;
    candidate.width = width;
    candidate.height = height;
    candidate.format = format;
    candidate.fetch_dwords = fetch_dwords;
    if (SameTextureIdentity(existing_it->second, candidate)) {
      return;
    }
    if (SameTextureIdentityIndexKeys(existing_it->second, candidate)) {
      candidate.serial = ++registry.texture_identity_serial;
      registry.texture_identities_by_object[candidate.object_texture] =
          candidate;
      return;
    }
    RemoveTextureIdentityIndexesLocked(registry, existing_it->second);
  }

  FableTextureIdentity identity;
  identity.object_texture = object_texture;
  identity.parent_texture = parent_texture != 0 ? parent_texture : object_texture;
  identity.base_address = base_address;
  identity.byte_size = byte_size;
  identity.width = width;
  identity.height = height;
  identity.format = format;
  identity.fetch_dwords = fetch_dwords;
  identity.serial = ++registry.texture_identity_serial;
  registry.texture_identities_by_object[identity.object_texture] = identity;
  if (identity.base_address != 0) {
    AddUnique(registry.texture_identity_objects_by_base[identity.base_address],
              identity.object_texture);
  }
  const uint64_t fetch_key = FableDirectTextureFetchKey(identity.fetch_dwords);
  if (fetch_key != 0) {
    AddUnique(registry.texture_identity_objects_by_fetch_key[fetch_key],
              identity.object_texture);
  }
}



FableGuestResourceRegistry& FableDirectGuestResourceRegistry() {
  return g_fable_guest_resources;
}

std::recursive_mutex& FableDirectGuestResourceRegistryMutex() {
  return g_fable_guest_resource_registry_mutex;
}


uint64_t FableDirectTextureFetchKey(
    const std::array<uint32_t, 6>& fetch_dwords) {
  if (!HasTextureIdentityFetchDwords(fetch_dwords)) {
    return 0;
  }

  const std::array<uint32_t, 6> key_dwords =
      NormalizeTextureFetchKeyDwords(fetch_dwords);
  uint64_t hash = 1469598103934665603ull;
  for (uint32_t dword : key_dwords) {
    hash ^= dword;
    hash *= 1099511628211ull;
  }
  return hash != 0 ? hash : 1;
}

void FableDirectRegisterTextureIdentity(
    const FableTextureIdentity& identity) {
  const FableGuestResourceRegistryClock::time_point total_t0 =
      FableGuestResourceRegistryClock::now();
  std::unique_lock<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const FableGuestResourceRegistryClock::time_point lock_t1 =
      FableGuestResourceRegistryClock::now();
  if (identity.object_texture == 0 ||
      !HasTextureIdentityFetchDwords(identity.fetch_dwords)) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableGuestResourceRegistryClock::time_point lookup_t0 =
      FableGuestResourceRegistryClock::now();
  const auto existing_it =
      registry.texture_identities_by_object.find(identity.object_texture);
  if (existing_it != registry.texture_identities_by_object.end() &&
      SameTextureIdentity(existing_it->second, identity)) {
    return;
  }
  const FableGuestResourceRegistryClock::time_point lookup_t1 =
      FableGuestResourceRegistryClock::now();
  if (existing_it != registry.texture_identities_by_object.end() &&
      SameTextureIdentityIndexKeys(existing_it->second, identity)) {
    FableTextureIdentity stored = identity;
    stored.serial = ++registry.texture_identity_serial;
    registry.texture_identities_by_object[stored.object_texture] = stored;
    return;
  }
  FableTextureIdentity previous_identity;
  bool had_previous_identity = false;
  bool previous_identity_may_rekey_private = false;
  bool previous_identity_may_rekey_surface = false;
  const FableGuestResourceRegistryClock::time_point previous_t0 =
      FableGuestResourceRegistryClock::now();
  if (existing_it != registry.texture_identities_by_object.end()) {
    previous_identity = existing_it->second;
    had_previous_identity = true;
    const uint64_t previous_fetch_key =
        FableDirectTextureFetchKey(previous_identity.fetch_dwords);
    previous_identity_may_rekey_private =
        TextureIdentityMayRekeyPrivateCopyDirectActions(
            registry, previous_identity, previous_fetch_key);
    previous_identity_may_rekey_surface =
        TextureIdentityMayRekeySurfaceTextureDirectEdges(
            registry, previous_identity, previous_fetch_key);
    RemoveTextureIdentityIndexesLocked(registry, previous_identity);
  }
  const FableGuestResourceRegistryClock::time_point previous_t1 =
      FableGuestResourceRegistryClock::now();

  FableTextureIdentity stored = identity;
  stored.serial = ++registry.texture_identity_serial;
  const FableGuestResourceRegistryClock::time_point store_t0 =
      FableGuestResourceRegistryClock::now();
  registry.texture_identities_by_object[stored.object_texture] = stored;

  if (stored.base_address != 0) {
    std::vector<uint32_t>& objects =
        registry.texture_identity_objects_by_base[stored.base_address];
    if (std::find(objects.begin(), objects.end(), stored.object_texture) ==
        objects.end()) {
      objects.push_back(stored.object_texture);
    }
  }
  const uint64_t fetch_key = FableDirectTextureFetchKey(stored.fetch_dwords);
  if (fetch_key != 0) {
    std::vector<uint32_t>& objects =
        registry.texture_identity_objects_by_fetch_key[fetch_key];
    if (std::find(objects.begin(), objects.end(), stored.object_texture) ==
        objects.end()) {
      objects.push_back(stored.object_texture);
    }
  }
  const FableGuestResourceRegistryClock::time_point store_t1 =
      FableGuestResourceRegistryClock::now();

  const FableGuestResourceRegistryClock::time_point rekey_t0 =
      FableGuestResourceRegistryClock::now();

  const bool stored_identity_may_rekey_private =
      TextureIdentityMayRekeyPrivateCopyDirectActions(registry, stored,
                                                      fetch_key);
  const bool stored_identity_may_rekey_surface =
      TextureIdentityMayRekeySurfaceTextureDirectEdges(registry, stored,
                                                       fetch_key);
  const FableGuestResourceRegistryClock::time_point rekey_t1 =
      FableGuestResourceRegistryClock::now();
  const FableGuestResourceRegistryClock::time_point prune_t0 =
      FableGuestResourceRegistryClock::now();
  if (had_previous_identity &&
      (previous_identity_may_rekey_private ||
       stored_identity_may_rekey_private)) {
    PrunePrivateTextureCopyDirectActionsForTextureIdentityLocked(
        registry, previous_identity);
  }
  if (had_previous_identity &&
      (previous_identity_may_rekey_surface ||
       stored_identity_may_rekey_surface)) {
    PruneSurfaceTextureDirectEdgesForTextureIdentityLocked(
        registry, previous_identity);
  }
  const FableGuestResourceRegistryClock::time_point prune_t1 =
      FableGuestResourceRegistryClock::now();

  const FableGuestResourceRegistryClock::time_point backfill_t0 =
      FableGuestResourceRegistryClock::now();
  TextureIdentityBackfillStats backfill_stats{};
  if (g_inline_identity_backfill_ms_used < kInlineIdentityBackfillBudgetMs) {
    backfill_stats =
        BackfillTextureIdentityDependentIndexesLocked(registry, stored);
    ++g_private_backfill_census.inline_backfills;
    g_private_backfill_census.actions_examined += backfill_stats.actions;
    g_private_backfill_census.links_examined += backfill_stats.links;
  } else {
    // Budget spent this frame (run 477): defer instead of running the
    // O(bank) re-store inline. The drain completes it within the next
    // frames' budgets; until then, object-keyed lookups for this identity's
    // fresh aliases see the pre-backfill state — the same one-compose lag
    // the pending-registration queues already impose.
    EnqueuePendingIdentityBackfillLocked(stored.object_texture);
  }
  const FableGuestResourceRegistryClock::time_point backfill_t1 =
      FableGuestResourceRegistryClock::now();
  g_inline_identity_backfill_ms_used +=
      FableGuestResourceRegistryElapsedMs(backfill_t0, backfill_t1);
  g_private_backfill_census.backfill_ms +=
      FableGuestResourceRegistryElapsedMs(backfill_t0, backfill_t1);
  const FableGuestResourceRegistryClock::time_point total_t1 =
      FableGuestResourceRegistryClock::now();
  const double total_ms =
      FableGuestResourceRegistryElapsedMs(total_t0, total_t1);
  if (total_ms >= 4.0) {
    static uint32_t logged_slow_texture_identity_registrations = 0;
    if (logged_slow_texture_identity_registrations < 96u) {
      REXLOG_INFO(
          "Refii plume direct texture identity registry timing #{} "
          "total_ms={:.3f} lock_ms={:.3f} lookup_ms={:.3f} "
          "previous_ms={:.3f} store_ms={:.3f} rekey_ms={:.3f} "
          "prune_ms={:.3f} backfill_ms={:.3f} object=0x{:08X} "
          "parent=0x{:08X} base=0x{:08X} size={}x{} fmt={} gen={} "
          "had_previous={} rekey_private={}/{} rekey_surface={}/{} "
          "backfill_actions={} backfill_links={} backfill_destinations={} "
          "backfill_collect_ms={:.3f}/{:.3f}/{:.3f} "
          "backfill_register_ms={:.3f}/{:.3f} "
          "private_sources={} private_targets={} private_bases={} "
          "private_fetch={} surface_textures={} surface_surfaces={} "
          "surface_bases={} surface_fetch={} surface_destinations={}",
          ++logged_slow_texture_identity_registrations, total_ms,
          FableGuestResourceRegistryElapsedMs(total_t0, lock_t1),
          FableGuestResourceRegistryElapsedMs(lookup_t0, lookup_t1),
          FableGuestResourceRegistryElapsedMs(previous_t0, previous_t1),
          FableGuestResourceRegistryElapsedMs(store_t0, store_t1),
          FableGuestResourceRegistryElapsedMs(rekey_t0, rekey_t1),
          FableGuestResourceRegistryElapsedMs(prune_t0, prune_t1),
          FableGuestResourceRegistryElapsedMs(backfill_t0, backfill_t1),
          stored.object_texture, stored.parent_texture, stored.base_address,
          stored.width, stored.height, stored.format,
          static_cast<unsigned long long>(stored.upload_generation),
          had_previous_identity ? 1 : 0,
          previous_identity_may_rekey_private ? 1 : 0,
          stored_identity_may_rekey_private ? 1 : 0,
          previous_identity_may_rekey_surface ? 1 : 0,
          stored_identity_may_rekey_surface ? 1 : 0,
          backfill_stats.actions, backfill_stats.links,
          backfill_stats.destination_candidates,
          backfill_stats.collect_actions_ms,
          backfill_stats.collect_links_ms,
          backfill_stats.collect_destinations_ms,
          backfill_stats.register_actions_ms,
          backfill_stats.register_links_ms,
          registry.private_texture_copies_by_source.size(),
          registry.private_texture_copies_by_target.size(),
          registry.private_texture_copies_by_base.size(),
          registry.private_texture_copies_by_fetch_key.size(),
          registry.surface_texture_links_by_texture.size(),
          registry.surface_texture_links_by_surface.size(),
          registry.surface_texture_links_by_base.size(),
          registry.surface_texture_links_by_fetch_key.size(),
          registry.surface_texture_link_by_destination_texture.size());
    }
  }
}

void FableDirectForgetTextureIdentity(uint32_t object_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_texture == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto identity_it =
      registry.texture_identities_by_object.find(object_texture);
  if (identity_it == registry.texture_identities_by_object.end()) {
    return;
  }

  const FableTextureIdentity removed_identity = identity_it->second;
  RemoveTextureIdentityIndexesLocked(registry, removed_identity);
  registry.texture_identities_by_object.erase(identity_it);
  PrunePrivateTextureCopyDirectActionsForResource(registry, object_texture);
  PruneSurfaceTextureDirectEdgesForResource(registry, object_texture);
}

void FableDirectClearTextureIdentities() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.texture_identity_serial = 0;
  registry.texture_identities_by_object.clear();
  registry.texture_identity_objects_by_base.clear();
  registry.texture_identity_objects_by_fetch_key.clear();
  RebuildPrivateTextureCopyDirectActions(registry);
  RebuildSurfaceTextureDirectEdges(registry);
}

// MIGRATION STEP 6 SHADOW. Instrumented HERE rather than at the ~145 call
// sites: one edit covers every consumer, and the answer we need is a property
// of the lookup, not of any individual caller.
//
// Compares the observer-fed identity registry against gtl::QueryResourceIdentity
// for the SAME object. Binds nothing and changes no behaviour.
//
// READ THE BUCKETS, NOT A RATE. The step-3 maze looked load-bearing at 79.7%
// agreement and was in fact empty — the divergence was bimodal, arriving in two
// Bink-window bursts and frozen otherwise. So these are counted as separate
// populations, and `both_*` disagreements are logged with their distinct shapes
// rather than summarised:
//   both_agree        - both know it, and every ported field matches
//   both_differ       - both know it, a ported field disagrees (THE alarm)
//   registry_only     - observer knows it, GTL does not (a porting BLOCKER)
//   gtl_only          - GTL knows it, observer does not (GTL is ahead; fine)
//   neither           - nobody knows it; the caller gets nullptr either way
// `fetch_unstated` is tracked apart from both_differ: a GTL record that simply
// never had a header-init is a COVERAGE gap (the 203/1911 seen in run 305), not
// a correctness disagreement, and conflating them would hide the real alarm.
namespace {
struct GtlIdentityShadow {
  uint64_t both_agree = 0;
  uint64_t both_differ = 0;
  uint64_t both_differ_geometry = 0;
  uint64_t both_differ_base_only = 0;
  // Of the base-only rows, how many match the exact skew seen in run 306.
  // If this tracks both_differ_base_only, the defect is ONE decode bug.
  uint64_t base_matches_known_transform = 0;
  // Rows where the GTL's base is the HIGH GPU ALIAS of the registry's decoded
  // base. Agreement, not divergence -- same memory, different view.
  uint64_t base_alias_view_agree = 0;
  // Bounded on purpose: this runs under the registry mutex, and an unbounded
  // set on a per-lookup path is the cost mistake this shadow is already gated
  // for. 4096 distinct objects is far above the ~290 live resources seen in
  // run 305, so saturation itself would be the finding.
  std::unordered_set<uint32_t> registry_only_objects;
  uint64_t registry_only = 0;
  uint64_t gtl_only = 0;
  uint64_t neither = 0;
  uint64_t fetch_unstated = 0;
  // APPORTIONMENT of both_differ_geometry, added 2026-08-03. The old single
  // bucket could not distinguish three mechanisms that were all landing in it,
  // and its own comment already warned not to report it as "the GTL is wrong"
  // without separating them first. The split is by BASE, which is the only
  // field that answers "is this even the same memory":
  //
  //   tenant         bases differ after normalising BOTH sides -> the header
  //                  was RECYCLED and the two sides describe different
  //                  resources. Expected to GROW during a run; the alias
  //                  -ambiguity pileup (one base claimed by 2,3,4 objects) is
  //                  the same phenomenon seen from the other end.
  //   promoted_extent  bases and formats agree, extents differ -> the registry
  //                  stores the PROMOTED HOST extent (FablePromotedRenderSurface
  //                  Extent rewrites a 1120x720 guest surface to the swapchain
  //                  extent) while the GTL stores the guest header extent.
  //                  Both correct, different quantities. Constant rate.
  //   format_variant bases and extents agree, formats differ only by the Xenos
  //                  EDRAM-expand pairing (X <-> X_AS_16_16_16_16). Same
  //                  format, different dword read at a different time.
  //   other          residue -- if this is large the split is wrong.
  //
  // A fixed per-lookup transform yields a CONSTANT fraction; an accumulating
  // stale population yields a GROWING one. Reading the deltas between two
  // samples is what tells them apart, so keep sampling more than once.
  uint64_t differ_tenant = 0;
  uint64_t differ_promoted_extent = 0;
  uint64_t differ_format_variant = 0;
  uint64_t differ_other = 0;
};
GtlIdentityShadow g_gtl_identity_shadow;

// DEFAULT ON (changed 2026-07-26 at the user's request — no env var needed to
// get migration-step-6 numbers). Set REFII_RENDERER_GTL_IDENTITY_SHADOW=0 to
// opt OUT.
//
// The cost is real but measured, not feared: this lookup runs from ~145 call
// sites, and the shadow adds one GTL-mutex acquisition plus a hash lookup and a
// struct copy to each. Run 306 ran with it enabled end-to-end and completed a
// normal ~60s drive, so it is tolerable. The reason to keep an opt-out at all
// is CONTENTION rather than raw cost: the registry mutex is held across the
// whole record pass while the guest render thread try_to_locks and silently
// gives up, so time added under it is dropped observer data. If a run ever
// looks starved, set the var to 0 and compare before blaming anything else.
bool GtlIdentityShadowEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_RENDERER_GTL_IDENTITY_SHADOW");
    // Absent or empty => ON. Only an explicit "0" turns it off.
    return value == nullptr || value[0] == '\0' || value[0] != '0';
  }();
  return enabled;
}

void RecordGtlIdentityShadow(uint32_t object_texture,
                             const FableTextureIdentity* registry_identity) {
  if (!GtlIdentityShadowEnabled()) {
    return;
  }
  const refii::gtl::ResourceIdentity gtl_identity =
      refii::gtl::QueryResourceIdentity(object_texture);
  if (registry_identity == nullptr) {
    if (gtl_identity.known) {
      ++g_gtl_identity_shadow.gtl_only;
    } else {
      ++g_gtl_identity_shadow.neither;
    }
    return;
  }
  if (!gtl_identity.known) {
    // READ THIS BEFORE TREATING registry_only AS A COVERAGE GAP. IDA resolved
    // the whole population 2026-07-27 and only a fifth of it is real:
    //
    //  ~31 are BASE ADDRESSES (0x1xxxxxxx), not objects. The observer minted
    //      identities whose object_texture field holds a base; all four of its
    //      map insertions key on that field. The GTL is keyed by guest object
    //      address and is CORRECT to miss them -- do not make
    //      QueryResourceIdentity permissive to "close" this, that would bake a
    //      caller bug into the GTL.
    //
    //  ~16 are PHANTOMS WE MANUFACTURE. RegisterExactTextureFetchBinding
    //      ActionLocked does `source_header = source_address - 28` and
    //      registers the result as a texture object, while its caller feeds it
    //      the DEVICE's own fetch-constant mirror (device + 0x480 + 24*slot).
    //      Since D3DBaseTexture.Format sits at +0x1C = 28, that manufactures a
    //      pseudo-object at device+0x480+24*slot-28. Verified exactly: device
    //      0x44142480 -> 0x441428E4 (slot 0) .. 0x44142A4C (slot 15). Not
    //      fixed here: it is observer code being deleted, and the GTL is
    //      already right (see the no-observer-patching rule).
    //
    //  ~13 are GENUINE 52-byte D3DBaseTexture headers created without
    //      D3DDevice_CreateTexture -- Render_TextureHolder_Create2D's
    //      physical-pool branch (0x82B81B28), Texture_InitHeaderPair
    //      (0x8225FBD0), the caller-owned-header sites, and header RECYCLING
    //      via the free list at 0x821EE848. That last one also means a texture
    //      ADDRESS IS NOT A STABLE IDENTITY: the same block returns as a
    //      different texture with no create event.
    //
    // The 0x40-byte spacing of that last group is an ALLOCATOR artifact (52/56/
    // 60/64-byte requests all bucket to one 64-byte class at page+0x10+64k), not
    // a table -- do not read a stride as evidence of a data structure.
    ++g_gtl_identity_shadow.registry_only;
    // DISTINCT objects, not lookups. run 306 reported registry_only=67604, but
    // that is a LOOKUP count: a handful of hot objects polled every draw looks
    // identical to a broad registration gap, and the two imply completely
    // different work (chase a few objects vs. add a registration root). Same
    // distinction that made the per-site identity census readable where the
    // aggregate was not.
    if (g_gtl_identity_shadow.registry_only_objects.size() < 4096u) {
      g_gtl_identity_shadow.registry_only_objects.insert(object_texture);
    }
    return;
  }
  if (!gtl_identity.fetch_dwords_stated) {
    ++g_gtl_identity_shadow.fetch_unstated;
  }
  // Only the fields a ported consumer would actually read. base_address is
  // compared only when the GTL has one: it is zero until a re-point states it,
  // and zero means "unknown", not "address 0".
  const bool geometry_differs =
      (registry_identity->width != gtl_identity.width) ||
      (registry_identity->height != gtl_identity.height) ||
      (registry_identity->format != gtl_identity.format);
  // THE TWO SIDES USE DIFFERENT ADDRESS VIEWS — comparing them raw was a bug in
  // THIS SHADOW, not a defect in the GTL. Run 306 reported differ=117689 largely
  // on that mistake.
  //
  // The registry stores the DECODED (physical) base. The GTL stores what the
  // guest texture header actually holds, which is the HIGH GPU ALIAS of the
  // same memory. The encoding is hook_observer.cpp's
  // EncodeFableHighGpuAliasForDecodedAddress:
  //
  //     alias = 0xE0000000 | ((decoded - 0x1000) & 0x1FFFFFFF)
  //
  // The -0x1000 is NOT an off-by-one-page bug: the 0xE0 view is mapped from
  // file offset 0x100001000 while physical is mapped at 0x100000000, so the
  // view carries a genuine one-page skew (see plume_fable_addressing.cpp and
  // GuestPhysicalHostOffset). Verified against all five sampled rows from run
  // 306, e.g. 0x1A537000 -> 0xFA536000 and 0x14980000 -> 0xF497F000.
  //
  // So accept EITHER view as agreement, and count the alias case separately so
  // we can still see which view each side is speaking.
  // Translate the GTL's GUEST-VIRTUAL base to physical rather than encoding the
  // registry's physical into one specific window. IDA (0x8225FDF0, 2026-07-26)
  // verified the guest stores r4 verbatim, so the header holds a windowed
  // guest-virtual address, and the general translation is:
  //
  //     phys = va & 0x1FFFFFFF ; if (va >= 0xE0000000) phys += 0x1000
  //
  // THE +0x1000 IS WINDOW-SPECIFIC AND MUST NOT BE APPLIED BLANKET. Only the
  // 0xE0000000 (4 KiB-aligned) window carries the one-page skew. The
  // 0xA0000000 (64 KiB) and 0xC0000000 (16 MiB) physical windows are a plain
  // mask with NO offset — and Fable 2 does use 64 KiB-aligned physical
  // allocations for vertex/index buffers (via XGOffsetResourceAddress), so an
  // unconditional adjustment would silently mis-address exactly those.
  const uint32_t decoded_base = registry_identity->base_address;
  const uint32_t gtl_base = gtl_identity.base_address;
  // NORMALISE BOTH SIDES (2026-08-03). This used to translate only the GTL's
  // base, on the assumption that the registry's was always already physical.
  // Run 538 disproves that: the registry carries `fetch.base_address << 12`,
  // i.e. whatever view the guest wrote, and it appears BOTH ways in one run --
  // 0x12681000 for some rows and 0xF2680000 for others, for the same memory.
  // So the reciprocal case (registry high-alias, GTL physical) was being
  // scored as a divergence with no bucket to catch it.
  const auto to_physical = [](uint32_t address) {
    uint32_t physical = address & 0x1FFFFFFFu;
    if (address >= 0xE0000000u) {
      physical += 0x1000u;
    }
    return physical;
  };
  const uint32_t gtl_base_phys = to_physical(gtl_base);
  const uint32_t decoded_base_phys = to_physical(decoded_base);
  const bool base_same_view = gtl_base == decoded_base;
  const bool base_alias_view = !base_same_view && gtl_base != 0 &&
                               gtl_base_phys == decoded_base_phys;
  if (base_alias_view) {
    ++g_gtl_identity_shadow.base_alias_view_agree;
  }
  const bool base_differs = gtl_identity.base_address != 0 &&
                            !base_same_view && !base_alias_view;
  if (!geometry_differs && !base_differs) {
    ++g_gtl_identity_shadow.both_agree;
    return;
  }
  ++g_gtl_identity_shadow.both_differ;
  // Split the alarm, because runs 306/307 showed these are TWO different
  // defects and lumping them hid both:
  //
  //  base_only  - geometry matches exactly, only the base disagrees, and in
  //               run 306 EVERY sampled row followed one transform:
  //               gtl == (registry - 0x1000) + 0xE0000000. A systematic,
  //               decodable skew (one 4 KiB page low, plus a different address
  //               -space view), NOT noise. The GTL writes base_address at
  //               exactly one site -- the XGOffsetBaseTextureAddress re-point,
  //               computed additively at hook ENTER, i.e. PRE-commit.
  //  geometry   - width/height/format disagree, often by an exact power of two
  //               (128x128 vs 32x32, 560x360 vs 280x180). Beware: the SAME
  //               object was seen carrying two different registry geometries
  //               within one run (0x40513C50), which is pooled-header REUSE.
  //               So an unknown share of this bucket is two registries sampled
  //               at different times, i.e. TIMING, not a decode error. Do not
  //               report this bucket as "the GTL is wrong" without separating
  //               that first.
  if (geometry_differs) {
    ++g_gtl_identity_shadow.both_differ_geometry;
    // Apportion by BASE first -- it is the only field that answers "is this
    // even the same memory". See the bucket comments on GtlIdentityShadow.
    if (base_differs) {
      ++g_gtl_identity_shadow.differ_tenant;
    } else {
      const bool extent_differs =
          registry_identity->width != gtl_identity.width ||
          registry_identity->height != gtl_identity.height;
      // Xenos EDRAM-expand pairing: X and X_AS_16_16_16_16 are the SAME
      // surface format, differing only in the expand variant bit. The registry
      // reads the live fetch constant (which a resolve re-states with the
      // _AS_ variant); the GTL reads the D3DFORMAT creation argument, which
      // never changes. Neither is wrong -- the comparison is.
      const auto canonical_format = [](uint32_t format) {
        switch (format) {
          case 50u: return 6u;
          case 51u: return 18u;
          case 52u: return 19u;
          case 53u: return 20u;
          case 54u: return 7u;
          case 55u: return 16u;
          case 56u: return 17u;
          default: return format;
        }
      };
      const bool format_variant_only =
          registry_identity->format != gtl_identity.format &&
          canonical_format(registry_identity->format) ==
              canonical_format(gtl_identity.format);
      if (!extent_differs && format_variant_only) {
        ++g_gtl_identity_shadow.differ_format_variant;
      } else if (extent_differs &&
                 registry_identity->format == gtl_identity.format) {
        ++g_gtl_identity_shadow.differ_promoted_extent;
      } else {
        ++g_gtl_identity_shadow.differ_other;
      }
    }
  } else {
    // Anything landing here is now a GENUINE base disagreement: both the
    // same-view and alias-view checks above already failed.
    ++g_gtl_identity_shadow.both_differ_base_only;
  }
  // Keyed per distinct SHAPE, not a flat cap: a flat cap fills with whatever
  // fires first and hides every later shape.
  // SHAPE KEY WIDENED 2026-08-03. It used to be four DIFF BITS plus the
  // registry's format byte -- no object, no extents, no GTL format. That key
  // saturates at ~15 combinations no matter how many distinct objects or
  // geometries are diverging, so "15 shapes" was a property of the key, not of
  // the defect, and it must NOT be read as "15 cases". Now it fingerprints the
  // object and both sides' geometry, so the count is meaningful and the cap is
  // reachable (saturating at 32 is then itself the finding).
  static std::unordered_set<uint64_t> logged_shapes;
  const auto mix = [](uint64_t seed, uint64_t value) {
    // Plain 64-bit mixer. The fields below overlap in width, so OR-ing or
    // shifting them into disjoint slots would either collide or not fit;
    // hashing keeps every field contributing.
    seed ^= value + 0x9E3779B97F4A7C15ull + (seed << 6) + (seed >> 2);
    return seed;
  };
  uint64_t shape = mix(0, object_texture);
  shape = mix(shape, registry_identity->width);
  shape = mix(shape, registry_identity->height);
  shape = mix(shape, registry_identity->format);
  shape = mix(shape, gtl_identity.width);
  shape = mix(shape, gtl_identity.height);
  shape = mix(shape, gtl_identity.format);
  shape = mix(shape, base_differs ? 1u : 0u);
  if (logged_shapes.size() < 32u && logged_shapes.insert(shape).second) {
    REXLOG_INFO(
        "Refii GTL identity shadow DIFFER object=0x{:08X} "
        "registry[{}x{} fmt={} base=0x{:08X}] gtl[{}x{} fmt={} base=0x{:08X}] "
        "fetch_stated={} shapes={}",
        object_texture, registry_identity->width, registry_identity->height,
        registry_identity->format, registry_identity->base_address,
        gtl_identity.width, gtl_identity.height, gtl_identity.format,
        gtl_identity.base_address, gtl_identity.fetch_dwords_stated ? 1 : 0,
        logged_shapes.size());
  }
}
}  // namespace

void FableDirectLogGtlIdentityShadow() {
  // Say ONCE whether the gate is on. Without this, "no shadow lines in the log"
  // is ambiguous between "env var never reached the process" and "enabled but
  // the counters never fired", and runs 308/309 burned a cycle on exactly that
  // ambiguity. Logged unconditionally, before the gate.
  static bool logged_gate_state = false;
  if (!logged_gate_state) {
    logged_gate_state = true;
    REXLOG_INFO("Refii GTL identity shadow enabled={} (default ON; set "
                "REFII_RENDERER_GTL_IDENTITY_SHADOW=0 to opt out)",
                GtlIdentityShadowEnabled() ? 1 : 0);
  }
  if (!GtlIdentityShadowEnabled()) {
    return;
  }
  // Name the registry_only set, do not just count it. Run 311 measured
  // registry_only=199487 LOOKUPS but only 41 distinct OBJECTS -- an enumerable
  // set, not a coverage wall. Printing the addresses turns "41 unknown objects"
  // into a list we can trace to whichever creation path the GTL does not hook.
  // Bounded and one-shot-ish: only when the set has grown, and capped, because
  // this runs under the registry mutex.
  {
    static size_t logged_registry_only_size = 0;
    const size_t current = g_gtl_identity_shadow.registry_only_objects.size();
    // `current <= 64u` REMOVED 2026-08-03. Run 538's set went 0 -> 92 between
    // two consecutive census emissions, skipping the <=64 window entirely, so
    // the dump NEVER fired and the 99 objects were never named -- the gate
    // silently defeated the diagnostic in exactly the case it was needed. The
    // set is already bounded at 4096, and this only prints when it GROWS, so
    // chunking is enough to keep the line lengths sane.
    if (current != logged_registry_only_size) {
      logged_registry_only_size = current;
      std::string addresses;
      uint32_t in_chunk = 0;
      uint32_t emitted = 0;
      for (const uint32_t object : g_gtl_identity_shadow.registry_only_objects) {
        addresses += fmt::format("{:08X} ", object);
        if (++in_chunk == 32u) {
          REXLOG_INFO(
              "Refii GTL identity shadow registry_only objects ({} of {}) "
              "[{}..]: {}",
              emitted + in_chunk, current, emitted, addresses);
          emitted += in_chunk;
          in_chunk = 0;
          addresses.clear();
        }
      }
      if (in_chunk != 0) {
        REXLOG_INFO(
            "Refii GTL identity shadow registry_only objects ({} of {}) "
            "[{}..]: {}",
            emitted + in_chunk, current, emitted, addresses);
      }
    }
  }
  REXLOG_INFO("Refii GTL identity shadow both[agree={} differ={} "
              "differ_geometry={} differ_base_only={}] base_alias_view={} "
              "registry_only={} registry_only_objects={} gtl_only={} "
              "neither={} fetch_unstated={} "
              "geom[tenant={} promoted_extent={} format_variant={} other={}]",
              static_cast<unsigned long long>(g_gtl_identity_shadow.both_agree),
              static_cast<unsigned long long>(g_gtl_identity_shadow.both_differ),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.both_differ_geometry),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.both_differ_base_only),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.base_alias_view_agree),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.registry_only),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.registry_only_objects.size()),
              static_cast<unsigned long long>(g_gtl_identity_shadow.gtl_only),
              static_cast<unsigned long long>(g_gtl_identity_shadow.neither),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.fetch_unstated),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.differ_tenant),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.differ_promoted_extent),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.differ_format_variant),
              static_cast<unsigned long long>(
                  g_gtl_identity_shadow.differ_other));
}

const FableTextureIdentity* FableDirectFindTextureIdentityByObject(
    uint32_t object_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.texture_identities_by_object.find(object_texture);
  const FableTextureIdentity* result =
      it != registry.texture_identities_by_object.end() ? &it->second : nullptr;
  RecordGtlIdentityShadow(object_texture, result);
  return result;
}

const FableTextureIdentity* FableDirectFindTextureIdentityByBase(
    uint32_t base_address, std::source_location site) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  // Forward the CALLER's site (see the ByFetch twin) — this delegator has
  // callers in both fable_direct_renderer.cpp and hook_observer.cpp, so
  // charging them all to this line would erase the only distinction that
  // matters.
  return SelectUniqueTextureIdentity(
      FableDirectFindTextureIdentityObjectsByBase(base_address, site));
}

// Replacement-map row "identity registries / texture-identity-by-object/base/
// fetch": its delete-gate ("sampled-slot binding reads GTL") was satisfied
// 2026-07-22, so the question is now which of these ~38 call sites still get an
// answer worth acting on. Aggregate first — the resolver census settled its
// whole family this way, and per-site attribution costs 38 edits that a single
// zero would make pointless.
//
// `ambiguous` is the field to read first, not `hit`. These maps store a LIST of
// objects per key; SelectUniqueTextureIdentity refuses anything but a
// singleton, so a multi-object answer tells a consumer nothing it can bind on.
// hit==ambiguous would mean the family is decorative even though it is busy.
namespace {

struct TextureIdentityLookupCensus {
  uint64_t base_calls = 0;
  uint64_t base_hits = 0;
  uint64_t base_ambiguous = 0;
  uint64_t fetch_calls = 0;
  uint64_t fetch_hits = 0;
  uint64_t fetch_ambiguous = 0;
};

// Per-call-site tallies. Run 151 answered the aggregate question — the family
// is load-bearing, not decorative (by_base 266,968 hits of which 179,908
// ambiguous; by_fetch 128,699 of 44,487) — so the next question is WHICH
// consumers earn those hits. `useful` is the one that decides a deletion: a
// singleton answer the caller can actually act on. A site whose hits are all
// ambiguous is doing lookup work that resolves to nothing.
struct TextureIdentityLookupSite {
  uint64_t calls = 0;
  uint64_t hits = 0;
  uint64_t useful = 0;
};

// Keyed by (file, line), not line alone: callers live in BOTH
// fable_direct_renderer.cpp and hook_observer.cpp, and two same-numbered lines
// in different files would otherwise silently merge into one bogus row. The
// file_name() pointer is a static literal, so comparing pointers groups
// correctly and costs nothing per call.
using TextureIdentityLookupSiteKey = std::pair<const char*, uint32_t>;

std::map<TextureIdentityLookupSiteKey, TextureIdentityLookupSite>
    g_texture_identity_lookup_sites_by_base;
std::map<TextureIdentityLookupSiteKey, TextureIdentityLookupSite>
    g_texture_identity_lookup_sites_by_fetch;

// Per-lookup site attribution. OPT-IN since 2026-07-28: this is telemetry, and
// it was doing a std::map (red-black tree) operator[] on EVERY texture-identity
// lookup. Those lookups run in loops inside
// BuildDirectCommandFromGtlDrawPacketLocked -- up to 16 texture slots per draw
// at submitted=189 draws -- and RecordFableDirectFrame is 79% of the
// direct-command frame (record_ms=60.7 of total_ms=76.4, ~320us per draw),
// confirmed by a core-dump stack that caught the main thread inside exactly
// this call chain.
//
// The site map answers "which source line does the lookups", which is a
// question you ask once. Set REFII_RENDERER_TEXTURE_IDENTITY_SITES=1 to re-arm.
bool TextureIdentityLookupSiteTrackingEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_RENDERER_TEXTURE_IDENTITY_SITES");
    return value != nullptr && value[0] != '\0' && value[0] != '0';
  }();
  return enabled;
}

void RecordTextureIdentityLookupSiteLocked(
    std::map<TextureIdentityLookupSiteKey, TextureIdentityLookupSite>& sites,
    const std::source_location& site, bool hit, bool ambiguous) {
  if (!TextureIdentityLookupSiteTrackingEnabled()) {
    return;
  }
  TextureIdentityLookupSite& entry =
      sites[TextureIdentityLookupSiteKey(site.file_name(), site.line())];
  ++entry.calls;
  if (hit) {
    ++entry.hits;
    if (!ambiguous) {
      ++entry.useful;
    }
  }
}

const char* TextureIdentityLookupSiteBasename(const char* path) {
  if (path == nullptr) {
    return "?";
  }
  const char* slash = std::strrchr(path, '/');
  return slash != nullptr ? slash + 1 : path;
}

void ReportTextureIdentityLookupSitesLocked(
    const char* label,
    const std::map<TextureIdentityLookupSiteKey, TextureIdentityLookupSite>&
        sites) {
  // Ranked by `useful` — the sites worth keeping float to the top, and the
  // tail is the deletion candidate list.
  std::vector<std::pair<TextureIdentityLookupSiteKey,
                        TextureIdentityLookupSite>>
      ranked(sites.begin(), sites.end());
  std::sort(ranked.begin(), ranked.end(),
            [](const auto& lhs, const auto& rhs) {
              if (lhs.second.useful != rhs.second.useful) {
                return lhs.second.useful > rhs.second.useful;
              }
              return lhs.second.calls > rhs.second.calls;
            });
  std::string row;
  for (const auto& [key, entry] : ranked) {
    char buffer[160] = {};
    std::snprintf(buffer, sizeof(buffer), " %s:%u[c=%llu h=%llu u=%llu]",
                  TextureIdentityLookupSiteBasename(key.first), key.second,
                  static_cast<unsigned long long>(entry.calls),
                  static_cast<unsigned long long>(entry.hits),
                  static_cast<unsigned long long>(entry.useful));
    row += buffer;
  }
  REXLOG_INFO("Refii texture-identity {} sites:{}", label, row);
}

// Guarded by FableDirectGuestResourceRegistryMutex — every caller below holds
// it, so plain counters are correct here.
TextureIdentityLookupCensus g_texture_identity_lookup_census;

void ReportTextureIdentityLookupCensusLocked(
    const FableGuestResourceRegistry& registry) {
  const TextureIdentityLookupCensus& census =
      g_texture_identity_lookup_census;
  const uint64_t total = census.base_calls + census.fetch_calls;
  if (total == 0 || (total % 16384u) != 0u) {
    return;
  }
  // KEY COUNT IS THE WRONG SIZE METRIC. These indexes map base -> a VECTOR of
  // object addresses, and both the insert (std::find before push_back) and every
  // consumer walk that vector linearly. Under pooled-header reuse many distinct
  // objects share one base over the life of a run, so the KEY count stays flat
  // while the LISTS grow without bound -- which is quadratic, and matches the
  // measured alias-phase growth (8.0 -> 18.6 -> 40.8 ms across run 316 while
  // map_sizes[base] sat at 131/136/132). Report entries and the worst list so
  // the growth is visible instead of inferred.
  uint64_t base_entries = 0;
  uint64_t base_worst = 0;
  for (const auto& [base, objects] : registry.texture_identity_objects_by_base) {
    base_entries += objects.size();
    base_worst = std::max<uint64_t>(base_worst, objects.size());
  }
  uint64_t fetch_entries = 0;
  uint64_t fetch_worst = 0;
  for (const auto& [key, objects] :
       registry.texture_identity_objects_by_fetch_key) {
    fetch_entries += objects.size();
    fetch_worst = std::max<uint64_t>(fetch_worst, objects.size());
  }
  REXLOG_INFO(
      "Refii texture-identity lookup census by_base[calls={} hit={} "
      "ambiguous={}] by_fetch[calls={} hit={} ambiguous={}] "
      "map_sizes[base={} fetch={}] "
      "entries[base={} base_worst={} fetch={} fetch_worst={}]",
      static_cast<unsigned long long>(census.base_calls),
      static_cast<unsigned long long>(census.base_hits),
      static_cast<unsigned long long>(census.base_ambiguous),
      static_cast<unsigned long long>(census.fetch_calls),
      static_cast<unsigned long long>(census.fetch_hits),
      static_cast<unsigned long long>(census.fetch_ambiguous),
      static_cast<unsigned long long>(
          registry.texture_identity_objects_by_base.size()),
      static_cast<unsigned long long>(
          registry.texture_identity_objects_by_fetch_key.size()),
      static_cast<unsigned long long>(base_entries),
      static_cast<unsigned long long>(base_worst),
      static_cast<unsigned long long>(fetch_entries),
      static_cast<unsigned long long>(fetch_worst));
  ReportTextureIdentityLookupSitesLocked(
      "by_base", g_texture_identity_lookup_sites_by_base);
  ReportTextureIdentityLookupSitesLocked(
      "by_fetch", g_texture_identity_lookup_sites_by_fetch);
}

}  // namespace

const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByBase(
    uint32_t base_address, std::source_location site) {
  // COST PROBE (2026-07-28, temporary). record_ms regresses to 238us/draw with
  // a ~zero intercept, i.e. it is purely per-draw, and a core-dump stack caught
  // the main thread inside this exact function via
  // BuildDirectCommandFromGtlDrawPacketLocked. This function is called in loops
  // over 16 texture slots per draw from several sites, so it is the prime
  // suspect for that 238us. Measure it rather than assume: total wall time and
  // call count, reported every 8192 calls. Remove once the number is known.
  const auto probe_t0 = std::chrono::steady_clock::now();
  struct ProbeScope {
    std::chrono::steady_clock::time_point t0;
    ~ProbeScope() {
      static std::atomic<uint64_t> total_ns{0};
      static std::atomic<uint64_t> calls{0};
      const uint64_t ns = static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::nanoseconds>(
              std::chrono::steady_clock::now() - t0)
              .count());
      const uint64_t t = total_ns.fetch_add(ns, std::memory_order_relaxed) + ns;
      const uint64_t n = calls.fetch_add(1, std::memory_order_relaxed) + 1u;
      if ((n % 8192u) == 0u) {
        REXLOG_INFO(
            "Refii texture-identity lookup COST calls={} total_ms={:.1f} "
            "avg_us={:.2f}",
            static_cast<unsigned long long>(n), t / 1.0e6,
            static_cast<double>(t) / static_cast<double>(n) / 1000.0);
      }
    }
  } probe_scope{probe_t0};
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  ++g_texture_identity_lookup_census.base_calls;
  const auto base_objects_it =
      registry.texture_identity_objects_by_base.find(base_address);
  if (base_objects_it == registry.texture_identity_objects_by_base.end()) {
    RecordTextureIdentityLookupSiteLocked(
        g_texture_identity_lookup_sites_by_base, site, false, false);
    ReportTextureIdentityLookupCensusLocked(registry);
    return nullptr;
  }
  ++g_texture_identity_lookup_census.base_hits;
  const bool ambiguous = base_objects_it->second.size() > 1u;
  if (ambiguous) {
    ++g_texture_identity_lookup_census.base_ambiguous;
  }
  RecordTextureIdentityLookupSiteLocked(
      g_texture_identity_lookup_sites_by_base, site, true, ambiguous);
  ReportTextureIdentityLookupCensusLocked(registry);
  return &base_objects_it->second;
}

const FableTextureIdentity* FableDirectFindTextureIdentityByFetch(
    const std::array<uint32_t, 6>& fetch_dwords, std::source_location site) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  // Forward the CALLER's site, not this line — otherwise every lookup routed
  // through here piles onto one meaningless row inside this file.
  return SelectUniqueTextureIdentity(
      FableDirectFindTextureIdentityObjectsByFetch(fetch_dwords, site));
}

const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByFetch(
    const std::array<uint32_t, 6>& fetch_dwords, std::source_location site) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t key = FableDirectTextureFetchKey(fetch_dwords);
  if (key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  ++g_texture_identity_lookup_census.fetch_calls;
  const auto fetch_it =
      registry.texture_identity_objects_by_fetch_key.find(key);
  if (fetch_it == registry.texture_identity_objects_by_fetch_key.end()) {
    RecordTextureIdentityLookupSiteLocked(
        g_texture_identity_lookup_sites_by_fetch, site, false, false);
    ReportTextureIdentityLookupCensusLocked(registry);
    return nullptr;
  }
  ++g_texture_identity_lookup_census.fetch_hits;
  const bool ambiguous = fetch_it->second.size() > 1u;
  if (ambiguous) {
    ++g_texture_identity_lookup_census.fetch_ambiguous;
  }
  RecordTextureIdentityLookupSiteLocked(
      g_texture_identity_lookup_sites_by_fetch, site, true, ambiguous);
  ReportTextureIdentityLookupCensusLocked(registry);
  return &fetch_it->second;
}

// The fetch-binding-action STORE (slot-fetch map + finder + dedupe + census)
// was deleted 2026-07-18 after run 542 proved its one remaining reader
// produced zero samples. The register keeps only its live side effect:
// seeding the texture-identity maps from observer-captured header actions.
void FableDirectRegisterTextureFetchBindingAction(
    const FableTextureFetchBindingAction& action) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (action.texture_slot >= 32u ||
      FableDirectTextureFetchKey(action.fetch_dwords) == 0) {
    return;
  }
  if (!action.header_stack_scratch && action.header_fetch_payload_match) {
    RegisterTextureActionIdentityIfUnclaimedLocked(
        FableDirectGuestResourceRegistry(), action.texture_object,
        action.parent_texture != 0 ? action.parent_texture
                                   : action.source_header,
        action.base_address, 0, action.width, action.height, action.format,
        action.fetch_dwords);
  }
}

// FableDirectFindTextureFetchBindingActionForSlotFetchSymbol deleted
// 2026-07-18 (migration step 3): both of its consumers (the title billboard
// action scan and the sampled-candidate symbol route) were census-dead —
// zero hits across full boot->title runs 538/539.

// The FableTextureFetchResourceLink family (register/forget/clear/find)
// was deleted 2026-07-18 (migration step 3): its only writer sat behind
// the census-dead binding-action format-match (runs 538/539), so the
// link maps had been permanently empty.



// Mirror of the sweep's frame index, for stamping header-action bucket touches
// that happen on guest/observer threads (which do not know the frame index).
// Written under the registry mutex by FableDirectRetireStalePrivateRegistrations
// each time the renderer invokes the sweep; a stamp is therefore at most one
// sweep cadence stale, which is noise against the 240-frame bucket TTL.
uint32_t g_texture_header_action_touch_frame = 0;

// Bump a bucket's last-touch stamp on a real store or a lookup hit. Buckets
// whose stamp goes untouched for kStaleTextureHeaderActionFrames are retired
// by the private-registration sweep -- title fetch addresses churn, so without
// this the bucket COUNT only ever grew (the 128 cap bounds each bucket, not
// the map).
void TouchTextureHeaderActionBucketLocked(FableGuestResourceRegistry& registry,
                                          uint32_t fetch_address) {
  registry.texture_header_action_bucket_last_touch_frame[fetch_address] =
      g_texture_header_action_touch_frame;
}



void FableDirectRegisterTextureHeaderAction(
    const FableTextureHeaderAction& action) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (action.fetch_address == 0 ||
      !HasTextureIdentityFetchDwords(action.fetch_dwords)) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  FableTextureHeaderAction stored = action;
  stored.serial = ++registry.texture_header_action_serial;
  constexpr size_t kMaxTextureHeaderActionsPerBucket = 128u;
  auto upsert_action = [&](std::vector<FableTextureHeaderAction>& actions) {
    if (!actions.empty() &&
        SameTextureHeaderAction(actions.back(), stored)) {
      return;
    }
    actions.push_back(stored);
    if (actions.size() > kMaxTextureHeaderActionsPerBucket) {
      actions.erase(actions.begin(),
                    actions.begin() +
                        (actions.size() - kMaxTextureHeaderActionsPerBucket));
    }
  };

  upsert_action(
      registry.texture_header_actions_by_fetch_address[stored.fetch_address]);
  TouchTextureHeaderActionBucketLocked(registry, stored.fetch_address);
  // Keep the fetch-key -> bucket-address index in step: the exact-fetch
  // finders scan only the buckets listed for the key. A dedupe-skipped store
  // re-adds nothing (same action => same key, recorded when first pushed).
  std::vector<uint32_t>& key_addresses =
      registry.texture_header_action_addresses_by_fetch_key
          [FableDirectTextureFetchKey(stored.fetch_dwords)];
  if (std::find(key_addresses.begin(), key_addresses.end(),
                stored.fetch_address) == key_addresses.end()) {
    key_addresses.push_back(stored.fetch_address);
  }
  if (!stored.stack_scratch) {
    RegisterTextureActionIdentityIfUnclaimedLocked(
        registry, stored.texture_object,
        stored.parent_texture != 0 ? stored.parent_texture
                                   : stored.texture_header,
        stored.base_address, stored.byte_size, stored.width, stored.height,
        stored.format, stored.fetch_dwords);
  }
}

void FableDirectClearTextureHeaderActions() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.texture_header_action_serial = 0;
  registry.texture_header_actions_by_fetch_address.clear();
  registry.texture_header_action_addresses_by_fetch_key.clear();
  registry.texture_header_action_bucket_last_touch_frame.clear();
  registry.texture_header_action_bucket_evictions = 0;
}

const FableTextureHeaderAction* FableDirectFindTextureHeaderActionForFetch(
    uint32_t fetch_address, const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (fetch_address == 0 || !HasTextureIdentityFetchDwords(fetch_dwords)) {
    return nullptr;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto address_it =
      registry.texture_header_actions_by_fetch_address.find(fetch_address);
  if (address_it != registry.texture_header_actions_by_fetch_address.end()) {
    const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
    for (auto it = address_it->second.rbegin();
         it != address_it->second.rend(); ++it) {
      if (FableDirectTextureFetchKey(it->fetch_dwords) == fetch_key) {
        TouchTextureHeaderActionBucketLocked(registry, fetch_address);
        return &*it;
      }
    }
    for (auto it = address_it->second.rbegin();
         it != address_it->second.rend(); ++it) {
      if (it->stack_scratch) {
        TouchTextureHeaderActionBucketLocked(registry, fetch_address);
        return &*it;
      }
    }
  }
  return nullptr;
}

const FableTextureHeaderAction*
FableDirectFindTextureHeaderActionForExactResource(
    uint32_t texture_header, uint32_t texture_object, uint32_t parent_texture,
    uint32_t base_address, uint32_t byte_size, uint32_t width, uint32_t height,
    uint32_t format, const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (base_address == 0 || byte_size == 0 || width == 0 || height == 0 ||
      !HasTextureIdentityFetchDwords(fetch_dwords)) {
    return nullptr;
  }

  const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableTextureHeaderAction* best = nullptr;
  uint32_t best_bucket_address = 0;
  const auto action_matches_address =
      [&](const FableTextureHeaderAction& action, uint32_t address) {
        return address != 0 &&
               (action.texture_header == address ||
                action.texture_object == address ||
                action.parent_texture == address);
      };

  // The match predicate requires fetch-key equality, so only the buckets the
  // key index lists can contain a match -- no fallback cross-bucket scan
  // exists in the semantics (this used to walk EVERY bucket x 128 actions
  // under the registry mutex).
  const auto key_it =
      registry.texture_header_action_addresses_by_fetch_key.find(fetch_key);
  if (key_it == registry.texture_header_action_addresses_by_fetch_key.end()) {
    return nullptr;
  }
  for (const uint32_t bucket_address : key_it->second) {
    const auto bucket_it =
        registry.texture_header_actions_by_fetch_address.find(bucket_address);
    if (bucket_it == registry.texture_header_actions_by_fetch_address.end()) {
      continue;
    }
    const auto& actions = bucket_it->second;
    for (auto it = actions.rbegin(); it != actions.rend(); ++it) {
      const FableTextureHeaderAction& action = *it;
      if (action.stack_scratch ||
          FableDirectTextureFetchKey(action.fetch_dwords) != fetch_key ||
          action.base_address != base_address ||
          action.byte_size != byte_size ||
          action.width != width ||
          action.height != height ||
          action.format != format) {
        continue;
      }
      if (!action_matches_address(action, texture_header) &&
          !action_matches_address(action, texture_object) &&
          !action_matches_address(action, parent_texture)) {
        continue;
      }
      if (best == nullptr || action.serial > best->serial) {
        best = &action;
        best_bucket_address = bucket_address;
      }
    }
  }
  if (best != nullptr) {
    TouchTextureHeaderActionBucketLocked(registry, best_bucket_address);
  }
  return best;
}

const FableTextureHeaderAction*
FableDirectFindTextureHeaderActionForExactFetchIdentity(
    uint32_t base_address, uint32_t width, uint32_t height, uint32_t format,
    const std::array<uint32_t, 6>& fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (base_address == 0 || width == 0 || height == 0 ||
      !HasTextureIdentityFetchDwords(fetch_dwords)) {
    return nullptr;
  }

  const uint64_t fetch_key = FableDirectTextureFetchKey(fetch_dwords);
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableTextureHeaderAction* best = nullptr;
  uint32_t best_bucket_address = 0;
  // Keyed lookup: the predicate requires fetch-key equality, so the key index
  // lists every bucket that can match (this used to walk EVERY bucket x 128
  // actions under the registry mutex on each call).
  const auto key_it =
      registry.texture_header_action_addresses_by_fetch_key.find(fetch_key);
  if (key_it == registry.texture_header_action_addresses_by_fetch_key.end()) {
    return nullptr;
  }
  for (const uint32_t bucket_address : key_it->second) {
    const auto bucket_it =
        registry.texture_header_actions_by_fetch_address.find(bucket_address);
    if (bucket_it == registry.texture_header_actions_by_fetch_address.end()) {
      continue;
    }
    const auto& actions = bucket_it->second;
    for (auto it = actions.rbegin(); it != actions.rend(); ++it) {
      const FableTextureHeaderAction& action = *it;
      if (action.stack_scratch ||
          FableDirectTextureFetchKey(action.fetch_dwords) != fetch_key ||
          action.base_address != base_address || action.width != width ||
          action.height != height || action.format != format) {
        continue;
      }
      if (best == nullptr || action.serial > best->serial) {
        best = &action;
        best_bucket_address = bucket_address;
      }
    }
  }
  if (best != nullptr) {
    TouchTextureHeaderActionBucketLocked(registry, best_bucket_address);
  }
  return best;
}


}  // namespace refii::renderer
