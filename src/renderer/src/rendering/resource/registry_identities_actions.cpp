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

void FableDirectRegisterShaderIdentity(
    const FableShaderIdentity& identity) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (identity.object_shader == 0 || identity.bytecode_hash == 0 ||
      identity.kind == FableGuestResourceKind::kUnknown) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto existing_it =
      registry.shader_identities_by_object.find(identity.object_shader);
  if (existing_it != registry.shader_identities_by_object.end() &&
      SameShaderIdentity(existing_it->second, identity)) {
    return;
  }

  if (existing_it != registry.shader_identities_by_object.end() &&
      existing_it->second.bytecode_hash != 0 &&
      existing_it->second.bytecode_hash != identity.bytecode_hash) {
    const auto hash_it = registry.shader_identity_objects_by_hash.find(
        existing_it->second.bytecode_hash);
    if (hash_it != registry.shader_identity_objects_by_hash.end()) {
      std::vector<uint32_t>& objects = hash_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(),
                                identity.object_shader),
                    objects.end());
      if (objects.empty()) {
        registry.shader_identity_objects_by_hash.erase(hash_it);
      }
    }
  }

  FableShaderIdentity stored = identity;
  stored.serial = ++registry.shader_identity_serial;
  registry.shader_identities_by_object[stored.object_shader] = stored;

  std::vector<uint32_t>& objects =
      registry.shader_identity_objects_by_hash[stored.bytecode_hash];
  if (std::find(objects.begin(), objects.end(), stored.object_shader) ==
      objects.end()) {
    objects.push_back(stored.object_shader);
  }
}

void FableDirectForgetShaderIdentity(uint32_t object_shader) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_shader == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto identity_it =
      registry.shader_identities_by_object.find(object_shader);
  if (identity_it == registry.shader_identities_by_object.end()) {
    return;
  }

  const uint64_t bytecode_hash = identity_it->second.bytecode_hash;
  registry.shader_identities_by_object.erase(identity_it);
  if (bytecode_hash == 0) {
    return;
  }

  const auto hash_it =
      registry.shader_identity_objects_by_hash.find(bytecode_hash);
  if (hash_it == registry.shader_identity_objects_by_hash.end()) {
    return;
  }
  std::vector<uint32_t>& objects = hash_it->second;
  objects.erase(std::remove(objects.begin(), objects.end(), object_shader),
                objects.end());
  if (objects.empty()) {
    registry.shader_identity_objects_by_hash.erase(hash_it);
  }
}

void FableDirectClearShaderIdentities() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.shader_identity_serial = 0;
  registry.shader_identities_by_object.clear();
  registry.shader_identity_objects_by_hash.clear();
}

const FableShaderIdentity* FableDirectFindShaderIdentityByObject(
    uint32_t object_shader) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_shader == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.shader_identities_by_object.find(object_shader);
  return it != registry.shader_identities_by_object.end() ? &it->second
                                                          : nullptr;
}

const std::vector<uint32_t>* FableDirectFindShaderIdentityObjectsByHash(
    uint64_t bytecode_hash) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (bytecode_hash == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.shader_identity_objects_by_hash.find(bytecode_hash);
  return it != registry.shader_identity_objects_by_hash.end() ? &it->second
                                                              : nullptr;
}

void FableDirectRegisterVertexDeclarationIdentity(
    const FableVertexDeclarationIdentity& identity) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (identity.object_declaration == 0 || identity.elements_hash == 0 ||
      identity.elements_size == 0 || identity.element_count == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto existing_it =
      registry.vertex_declaration_identities_by_object.find(
          identity.object_declaration);
  if (existing_it !=
          registry.vertex_declaration_identities_by_object.end() &&
      SameVertexDeclarationIdentity(existing_it->second, identity)) {
    return;
  }

  if (existing_it !=
          registry.vertex_declaration_identities_by_object.end() &&
      existing_it->second.elements_hash != 0 &&
      existing_it->second.elements_hash != identity.elements_hash) {
    const auto hash_it =
        registry.vertex_declaration_identity_objects_by_hash.find(
            existing_it->second.elements_hash);
    if (hash_it !=
        registry.vertex_declaration_identity_objects_by_hash.end()) {
      std::vector<uint32_t>& objects = hash_it->second;
      objects.erase(std::remove(objects.begin(), objects.end(),
                                identity.object_declaration),
                    objects.end());
      if (objects.empty()) {
        registry.vertex_declaration_identity_objects_by_hash.erase(hash_it);
      }
    }
  }

  FableVertexDeclarationIdentity stored = identity;
  stored.serial = ++registry.vertex_declaration_identity_serial;
  registry.vertex_declaration_identities_by_object[stored.object_declaration] =
      stored;

  std::vector<uint32_t>& objects =
      registry.vertex_declaration_identity_objects_by_hash[stored.elements_hash];
  if (std::find(objects.begin(), objects.end(), stored.object_declaration) ==
      objects.end()) {
    objects.push_back(stored.object_declaration);
  }
}

void FableDirectForgetVertexDeclarationIdentity(
    uint32_t object_declaration) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_declaration == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto identity_it =
      registry.vertex_declaration_identities_by_object.find(
          object_declaration);
  if (identity_it ==
      registry.vertex_declaration_identities_by_object.end()) {
    return;
  }

  const uint64_t elements_hash = identity_it->second.elements_hash;
  registry.vertex_declaration_identities_by_object.erase(identity_it);
  if (elements_hash == 0) {
    return;
  }

  const auto hash_it =
      registry.vertex_declaration_identity_objects_by_hash.find(elements_hash);
  if (hash_it ==
      registry.vertex_declaration_identity_objects_by_hash.end()) {
    return;
  }
  std::vector<uint32_t>& objects = hash_it->second;
  objects.erase(std::remove(objects.begin(), objects.end(),
                            object_declaration),
                objects.end());
  if (objects.empty()) {
    registry.vertex_declaration_identity_objects_by_hash.erase(hash_it);
  }
}

void FableDirectClearVertexDeclarationIdentities() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.vertex_declaration_identity_serial = 0;
  registry.vertex_declaration_identities_by_object.clear();
  registry.vertex_declaration_identity_objects_by_hash.clear();
}

const FableVertexDeclarationIdentity*
FableDirectFindVertexDeclarationIdentityByObject(uint32_t object_declaration) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (object_declaration == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.vertex_declaration_identities_by_object.find(object_declaration);
  return it != registry.vertex_declaration_identities_by_object.end()
             ? &it->second
             : nullptr;
}

const std::vector<uint32_t>*
FableDirectFindVertexDeclarationIdentityObjectsByHash(uint64_t elements_hash) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (elements_hash == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.vertex_declaration_identity_objects_by_hash.find(elements_hash);
  return it != registry.vertex_declaration_identity_objects_by_hash.end()
             ? &it->second
             : nullptr;
}

const char* FablePrivateTextureCopyKindName(FablePrivateTextureCopyKind kind) {
  switch (kind) {
    case FablePrivateTextureCopyKind::kColor:
      return "color";
    case FablePrivateTextureCopyKind::kDepth:
      return "depth";
    case FablePrivateTextureCopyKind::kColorSibling:
      return "color-sibling";
  }
  return "unknown";
}

const char* FableRegisteredSampleSourceKindName(
    FableRegisteredSampleSourceKind kind) {
  switch (kind) {
    case FableRegisteredSampleSourceKind::kUnknown:
      return "unknown";
    case FableRegisteredSampleSourceKind::kGuestTexture:
      return "guest-texture";
    case FableRegisteredSampleSourceKind::kBackingTexture:
      return "backing-texture";
    case FableRegisteredSampleSourceKind::kGpuSurface:
      return "gpu-surface";
  }
  return "unknown";
}

void FableDirectRegisterPrivateTextureCopyAction(
    const FablePrivateTextureCopyAction& action) {
  FableDirectRegisterPrivateTextureCopyActionInternal(
      action, FableRegistryRepairMode::kPropagateRepairs);
}

void FableDirectRegisterPrivateTextureCopyActionsBatch(
    const std::vector<FablePrivateTextureCopyAction>& actions) {
  if (actions.empty()) {
    return;
  }
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  for (const FablePrivateTextureCopyAction& action : actions) {
    FableDirectRegisterPrivateTextureCopyActionInternal(
        action, FableRegistryRepairMode::kDeferRepairs);
  }
  std::vector<const FablePrivateTextureCopyAction*> pointers;
  pointers.reserve(actions.size());
  for (const FablePrivateTextureCopyAction& action : actions) {
    pointers.push_back(&action);
  }
  RepairSurfaceTextureLinksForPrivateCopyActionsLocked(
      FableDirectGuestResourceRegistry(), pointers);
}

void FableDirectRegisterPrivateTextureCopyActionInternal(
    const FablePrivateTextureCopyAction& action,
    FableRegistryRepairMode repair_mode) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const bool wrapper_context_action =
      action.wrapper_call_serial != 0 || action.wrapper_context_key != 0;
  if (action.source_texture == 0 ||
      (action.target_surface == 0 && !wrapper_context_action)) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();

  FablePrivateTextureCopyAction stored = action;
  NormalizePrivateTextureCopySampleSource(stored);
  auto find_existing_serial =
      [&](const std::vector<FablePrivateTextureCopyAction>* actions)
      -> uint64_t {
        if (!actions) {
          return 0;
        }
        for (auto it = actions->rbegin(); it != actions->rend(); ++it) {
          if (!SamePrivateTextureCopyActionIdentity(*it, stored)) {
            continue;
          }
          const bool same_static_action =
              stored.frame_index == 0 && stored.draw_serial == 0;
          const bool same_producer_action = stored.frame_index != 0;
          if (same_static_action ||
              (same_producer_action &&
               it->frame_index == stored.frame_index &&
               it->draw_serial == stored.draw_serial)) {
            return it->serial;
          }
        }
        return 0;
      };
  auto find_actions_by_key =
      [](const auto& index, auto key)
      -> const std::vector<FablePrivateTextureCopyAction>* {
    const auto it = index.find(key);
    return it != index.end() ? &it->second : nullptr;
  };

  const uint64_t source_fetch_key =
      FableDirectTextureFetchKey(stored.source_fetch_dwords);
  const uint64_t backing_fetch_key =
      FableDirectTextureFetchKey(stored.backing_fetch_dwords);
  std::vector<uint32_t> source_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  CollectTextureIdentityIndexKeysForObject(registry, stored.source_texture,
                                           source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForObject(registry, stored.backing_texture,
                                           source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.source_base_address,
                                         source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.backing_base_address,
                                         source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(registry, source_fetch_key,
                                          source_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(registry, backing_fetch_key,
                                          source_keys, base_keys, fetch_keys);
  const bool static_alias_action =
      stored.frame_index == 0 && stored.draw_serial == 0;
  const bool repeated_producer_action = stored.frame_index != 0;
  uint64_t existing_serial = 0;
  if (static_alias_action || repeated_producer_action) {
    for (uint32_t source_key : source_keys) {
      existing_serial = find_existing_serial(find_actions_by_key(
          registry.private_texture_copies_by_source, source_key));
      if (existing_serial != 0) {
        break;
      }
    }
    if (existing_serial == 0 && stored.target_surface != 0) {
      existing_serial =
          find_existing_serial(find_actions_by_key(
              registry.private_texture_copies_by_target, stored.target_surface));
    }
    if (existing_serial == 0 && stored.render_target != 0) {
      existing_serial =
          find_existing_serial(find_actions_by_key(
              registry.private_texture_copies_by_target, stored.render_target));
    }
    for (uint32_t base_key : base_keys) {
      if (existing_serial != 0) {
        break;
      }
      existing_serial = find_existing_serial(find_actions_by_key(
          registry.private_texture_copies_by_base, base_key));
    }
    for (uint64_t fetch_key : fetch_keys) {
      if (existing_serial != 0) {
        break;
      }
      existing_serial = find_existing_serial(find_actions_by_key(
          registry.private_texture_copies_by_fetch_key, fetch_key));
    }
  }

  stored.serial =
      existing_serial != 0 ? existing_serial
                           : ++registry.private_texture_copy_serial;

  constexpr size_t kMaxPrivateTextureCopyActionsPerBucket = 32;
  auto upsert_action = [&](std::vector<FablePrivateTextureCopyAction>& actions) {
    for (FablePrivateTextureCopyAction& existing : actions) {
      if (SamePrivateTextureCopyActionIdentity(existing, stored)) {
        existing = stored;
        return;
      }
    }
    if (actions.size() >= kMaxPrivateTextureCopyActionsPerBucket) {
      actions.erase(actions.begin());
    }
    actions.push_back(stored);
  };

  for (uint32_t source_key : source_keys) {
    upsert_action(registry.private_texture_copies_by_source[source_key]);
  }
  if (stored.target_surface != 0) {
    upsert_action(
        registry.private_texture_copies_by_target[stored.target_surface]);
  }
  if (stored.render_target != 0 &&
      stored.render_target != stored.target_surface) {
    upsert_action(
        registry.private_texture_copies_by_target[stored.render_target]);
  }

  for (uint32_t base_key : base_keys) {
    registry.private_texture_copy_source_by_base[base_key] =
        stored.source_texture;
    upsert_action(registry.private_texture_copies_by_base[base_key]);
  }
  for (uint64_t fetch_key : fetch_keys) {
    upsert_action(registry.private_texture_copies_by_fetch_key[fetch_key]);
  }
  StorePrivateTextureCopyDirectAction(registry, stored, repair_mode);
  if (stored.target_surface == 0) {
    return;
  }

  FableSurfaceTextureLink link;
  link.source_slot = stored.source_slot;
  link.trusted_destination_edge = true;
  link.texture = stored.source_texture;
  link.texture_base_address = stored.source_base_address;
  link.texture_width = stored.source_width;
  link.texture_height = stored.source_height;
  link.texture_format = stored.source_format;
  link.texture_fetch_dwords = stored.source_fetch_dwords;
  link.source_surface = stored.target_surface;
  link.backing_texture = stored.backing_texture;
  link.backing_base_address = stored.backing_base_address;
  link.backing_fetch_dwords = stored.backing_fetch_dwords;
  link.backing_content_serial = stored.backing_content_serial;
  link.target_content_serial = stored.target_content_serial;
  link.render_target = stored.render_target;
  link.depth_stencil = stored.depth_stencil;
  link.vertex_shader = stored.vertex_shader;
  link.pixel_shader = stored.pixel_shader;
  link.vertex_shader_root_call_serial = stored.vertex_shader_root_call_serial;
  link.vertex_shader_root_context_key = stored.vertex_shader_root_context_key;
  link.vertex_shader_root_symbol_address =
      stored.vertex_shader_root_symbol_address;
  link.vertex_shader_root_lr = stored.vertex_shader_root_lr;
  link.pixel_shader_root_call_serial = stored.pixel_shader_root_call_serial;
  link.pixel_shader_root_context_key = stored.pixel_shader_root_context_key;
  link.pixel_shader_root_symbol_address =
      stored.pixel_shader_root_symbol_address;
  link.pixel_shader_root_lr = stored.pixel_shader_root_lr;
  link.copy_call_serial = stored.wrapper_call_serial;
  link.copy_context_key = stored.wrapper_context_key;
  link.copy_symbol_address = stored.wrapper_symbol_address;
  link.wrapper_source_texture = stored.wrapper_source_texture;
  link.wrapper_source_exact = stored.wrapper_source_exact;
  link.wrapper_setup_globals = stored.wrapper_setup_globals;
  link.frame_index = stored.frame_index;
  link.draw_serial = stored.frame_index != 0 ? stored.draw_serial : 0;
  if (stored.sampled_source.kind ==
      FableRegisteredSampleSourceKind::kBackingTexture) {
    link.sampled_source = stored.sampled_source;
  } else {
    link.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
    link.sampled_source.resource = stored.target_surface;
    link.sampled_source.content_serial = stored.target_content_serial;
  }
  link.kind = stored.kind;
  FableDirectRegisterSurfaceTextureLinkInternal(link, repair_mode);
}

void FableDirectForgetPrivateTextureCopyActionsForResource(uint32_t resource) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (resource == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  if (HasIndexedPrivateTextureCopyActionLocked(registry, resource)) {
    if (PrunePrimaryPrivateTextureCopyActionsMatchedLocked(
            registry, SingleResourceMatcher{resource})) {
      PrunePrivateTextureCopyDirectActionsForResource(registry, resource);
      ++registry.private_texture_copy_serial;
    }
  }
  FableDirectForgetSurfaceTextureLinksForResource(resource);
}

// Batched retirement flush (2026-08-02, run 513) — see
// ForgetPrivateRegistrationsForResourceSetsLocked for the mechanism and the
// parity argument. Per address this also erases the texture identity
// exact-key (the FableDirectForgetTextureIdentity recipe), folding that
// recipe's unconditional derived-index prunes into the same single walk.
// Correctness precondition (owned by the caller, the sync path): every
// address in the batch is being retired in the SAME sync call, and the flush
// runs before any re-registration for a batched address can sync — the
// snapshot loop runs after it, so retire-before-re-register order holds
// exactly as it did with inline forgets.
void FableDirectForgetResourceRegistrationsBatch(
    const std::vector<uint32_t>& resources) {
  if (resources.empty()) {
    return;
  }
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();

  std::unordered_set<uint32_t> action_matched;
  std::unordered_set<uint32_t> link_matched;
  std::unordered_set<uint32_t> derived_matched;
  for (uint32_t resource : resources) {
    if (resource == 0) {
      continue;
    }
    const auto identity_it =
        registry.texture_identities_by_object.find(resource);
    if (identity_it != registry.texture_identities_by_object.end()) {
      RemoveTextureIdentityIndexesLocked(registry, identity_it->second);
      registry.texture_identities_by_object.erase(identity_it);
      derived_matched.insert(resource);
    }
    if (HasIndexedPrivateTextureCopyActionLocked(registry, resource)) {
      action_matched.insert(resource);
      derived_matched.insert(resource);
    }
    if (HasIndexedSurfaceTextureLinkLocked(registry, resource)) {
      link_matched.insert(resource);
      derived_matched.insert(resource);
    }
  }
  ForgetPrivateRegistrationsForResourceSetsLocked(
      registry, action_matched, link_matched, derived_matched);
}

void FableDirectClearPrivateTextureCopyActions() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.private_texture_copy_serial = 0;
  registry.private_texture_copy_index_serial = 0;
  registry.private_texture_copy_logs = 0;
  registry.private_texture_copies_by_source.clear();
  registry.private_texture_copies_by_target.clear();
  registry.private_texture_copy_source_by_base.clear();
  registry.private_texture_copies_by_base.clear();
  registry.private_texture_copies_by_fetch_key.clear();
  registry.private_texture_copy_action_by_destination_texture.clear();
  registry.private_texture_copy_action_by_destination_texture_slot.clear();
  registry.private_texture_copy_actions_by_destination_texture.clear();
  registry.private_texture_copy_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_output_actions_by_destination_texture.clear();
  registry.private_texture_copy_output_actions_by_destination_texture_slot.clear();
  registry.private_texture_copy_action_by_wrapper_call.clear();
  registry.private_texture_copy_action_by_wrapper_context.clear();
  FableDirectClearSurfaceTextureLinks();
}

void FableDirectRetireStalePrivateRegistrations(uint32_t current_frame_index) {
  // Run 473 record-loop runaway: the private-rect action / surface-link
  // registrations are keyed by one-shot title panel header addresses (the
  // fresh-heap-address population class the stale-RESOURCE sweep bounds), but
  // most keys are never tracked resources, so the destroy/evict-driven forget
  // recipes can never fire for them — private_targets grew 169->225+ within
  // one title sitting with no removal path at all. Producer registrations
  // (frame_index != 0) are re-registered by the recurring guest private-rect
  // wrappers every frame the copy is live, so a key whose NEWEST entry is
  // older than the TTL belongs to an abandoned header and is fully
  // re-derivable if the guest ever draws through it again. Static/latched
  // registrations (frame_index == 0) are exempt: they are registered once and
  // eviction could not re-derive them.
  //
  // TTL (run 474): 240 frames NEVER fired before the spiral — both run 473
  // and run 474 entered the title only ~150-300 frames before the record
  // runaway (run 474: resources=11 at frame 2464, 1503 at frame 3712, spiral
  // at 3732), so every registration was younger than 240 and the sweep
  // correctly retired nothing while private_targets hit 605. 64 frames is the
  // sound bound: the longest cross-frame reach any consumer makes into an old
  // producer action is kMaxSetupClearActionFrameGap = 32 frames
  // (BackfillDirectPrivateTextureSetupClearActionsLocked), and live copies
  // re-register with fresh stamps every frame they recur.
  constexpr uint32_t kStalePrivateRegistrationFrames = 64;
  constexpr size_t kMaxRetiredKeysPerSweep = 64;
  if (current_frame_index <= kStalePrivateRegistrationFrames) {
    return;
  }
  const uint32_t stale_before =
      current_frame_index - kStalePrivateRegistrationFrames;
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  // Refresh the stamp mirror for texture-header-action bucket touches; the
  // guest-side register/find paths stamp with this value between sweeps.
  g_texture_header_action_touch_frame = current_frame_index;

  const auto frame_is_stale = [stale_before](uint32_t frame_index) {
    return frame_index != 0 && frame_index < stale_before;
  };

  // Address-keyed populations retire whole keys through the existing forget
  // recipes (primary buckets + every derived direct index + serial bump), so
  // this sweep introduces no second removal path. Only the uint32 address
  // indexes are collected; the fetch-key buckets hold the same actions/links
  // and the forget matchers clean them alongside.
  std::vector<uint32_t> stale_action_keys;
  std::vector<uint32_t> stale_link_keys;
  const auto collect_stale_keys = [&](const auto& index, auto&& entry_frame,
                                      std::vector<uint32_t>& stale_keys) {
    for (const auto& [key, entries] : index) {
      if (stale_keys.size() >= kMaxRetiredKeysPerSweep) {
        return;
      }
      bool all_stale = !entries.empty();
      for (const auto& entry : entries) {
        if (!frame_is_stale(entry_frame(entry))) {
          all_stale = false;
          break;
        }
      }
      if (all_stale &&
          std::find(stale_keys.begin(), stale_keys.end(), key) ==
              stale_keys.end()) {
        stale_keys.push_back(key);
      }
    }
  };
  const auto action_frame = [](const FablePrivateTextureCopyAction& action) {
    return action.frame_index;
  };
  const auto link_frame = [](const FableSurfaceTextureLink& link) {
    return link.frame_index;
  };
  collect_stale_keys(registry.private_texture_copies_by_source, action_frame,
                     stale_action_keys);
  collect_stale_keys(registry.private_texture_copies_by_target, action_frame,
                     stale_action_keys);
  collect_stale_keys(registry.private_texture_copies_by_base, action_frame,
                     stale_action_keys);
  collect_stale_keys(registry.surface_texture_links_by_texture, link_frame,
                     stale_link_keys);
  collect_stale_keys(registry.surface_texture_links_by_surface, link_frame,
                     stale_link_keys);
  collect_stale_keys(registry.surface_texture_links_by_base, link_frame,
                     stale_link_keys);
  // Batched (2026-08-02): the run-489 per-key forget budget is gone with the
  // per-key walks — the batched core prunes every collected key in ONE pass
  // over the tables (O(tables + keys), not keys x walk), so 64+64 keys cost
  // one walk and nothing needs deferring. Per-key parity: the action-key
  // forget recipe tail-forgets surface links for the same key, so action keys
  // join the link set; texture identities are NOT touched here (these keys
  // are registration keys, not necessarily retired resources — erasing a
  // still-live identity through one would strand it).
  {
    std::unordered_set<uint32_t> action_matched;
    std::unordered_set<uint32_t> link_matched;
    std::unordered_set<uint32_t> derived_matched;
    const auto add_key = [&](uint32_t key, bool as_action) {
      if (key == 0) {
        return;
      }
      if (as_action &&
          HasIndexedPrivateTextureCopyActionLocked(registry, key)) {
        action_matched.insert(key);
        derived_matched.insert(key);
      }
      if (HasIndexedSurfaceTextureLinkLocked(registry, key)) {
        link_matched.insert(key);
        derived_matched.insert(key);
      }
    };
    for (uint32_t key : stale_action_keys) {
      add_key(key, /*as_action=*/true);
    }
    for (uint32_t key : stale_link_keys) {
      add_key(key, /*as_action=*/false);
    }
    ForgetPrivateRegistrationsForResourceSetsLocked(
        registry, action_matched, link_matched, derived_matched);
  }

  // The serial-keyed exact-wrapper maps grow one entry per wrapper CALL and
  // the address-keyed recipes only reach entries referencing a retired
  // address, so entries referencing still-hot endpoints accumulate forever.
  // Their consumers look up by a command's own wrapper serial / context key,
  // and no command older than the TTL is ever recorded again, so frame-stale
  // entries are dead weight. Erase directly; one serial bump covers pointer
  // invalidation for the record-side caches.
  size_t retired_serial_entries = 0;
  const auto retire_stale_serial_entries = [&](auto& index, auto&& entry_frame) {
    for (auto it = index.begin(); it != index.end();) {
      if (frame_is_stale(entry_frame(it->second))) {
        it = index.erase(it);
        ++retired_serial_entries;
      } else {
        ++it;
      }
    }
  };
  retire_stale_serial_entries(registry.private_texture_copy_action_by_wrapper_call,
                              action_frame);
  retire_stale_serial_entries(
      registry.private_texture_copy_action_by_wrapper_context, action_frame);
  if (retired_serial_entries != 0) {
    ++registry.private_texture_copy_index_serial;
  }
  size_t retired_link_serial_entries = 0;
  const auto retire_stale_link_serial_entries = [&](auto& index) {
    for (auto it = index.begin(); it != index.end();) {
      if (frame_is_stale(it->second.frame_index)) {
        it = index.erase(it);
        ++retired_link_serial_entries;
      } else {
        ++it;
      }
    }
  };
  retire_stale_link_serial_entries(registry.surface_texture_link_by_copy_call);
  retire_stale_link_serial_entries(
      registry.surface_texture_link_by_copy_context);
  if (retired_link_serial_entries != 0) {
    ++registry.surface_texture_link_serial;
  }

  // Texture-header-action buckets: keyed by guest fetch addresses, which
  // churn over a title sitting. The 128-entry cap bounds each bucket, but the
  // bucket COUNT had no retirement short of the full clear -- the same
  // population class as the four leak classes above. Buckets are stamped on
  // real store and on lookup hit (TouchTextureHeaderActionBucketLocked); one
  // untouched for the whole TTL belongs to an abandoned fetch address and is
  // fully re-derivable if the guest ever registers or samples through it
  // again. The TTL is deliberately more generous than the 64-frame private
  // window: header actions are registered at creation and can be consumed
  // long after, and every consumption refreshes the stamp.
  constexpr uint32_t kStaleTextureHeaderActionFrames = 240;
  size_t retired_header_buckets = 0;
  if (current_frame_index > kStaleTextureHeaderActionFrames) {
    const uint32_t header_stale_before =
        current_frame_index - kStaleTextureHeaderActionFrames;
    auto& header_buckets = registry.texture_header_actions_by_fetch_address;
    auto& header_stamps =
        registry.texture_header_action_bucket_last_touch_frame;
    for (auto it = header_buckets.begin(); it != header_buckets.end();) {
      uint32_t& stamp = header_stamps[it->first];
      if (stamp == 0) {
        // Registered before any sweep ran: adopt into the TTL domain now
        // rather than retiring on sight.
        stamp = current_frame_index;
        ++it;
        continue;
      }
      if (stamp >= header_stale_before) {
        ++it;
        continue;
      }
      header_stamps.erase(it->first);
      it = header_buckets.erase(it);
      ++retired_header_buckets;
    }
    if (retired_header_buckets != 0) {
      registry.texture_header_action_bucket_evictions += retired_header_buckets;
      // Prune the fetch-key index of addresses whose bucket just retired so
      // it cannot accumulate churned addresses forever; a stale entry is only
      // a wasted map probe in the finders, never a wrong match.
      auto& key_index = registry.texture_header_action_addresses_by_fetch_key;
      for (auto key_it = key_index.begin(); key_it != key_index.end();) {
        std::vector<uint32_t>& addresses = key_it->second;
        addresses.erase(
            std::remove_if(addresses.begin(), addresses.end(),
                           [&](uint32_t address) {
                             return header_buckets.find(address) ==
                                    header_buckets.end();
                           }),
            addresses.end());
        if (addresses.empty()) {
          key_it = key_index.erase(key_it);
        } else {
          ++key_it;
        }
      }
    }
  }

  // Surface-destination target index: drop bucket keys whose relation bucket
  // no longer exists in either by-destination map (relations are pruned
  // per-resource by PruneSurfaceTextureDirectEdgesForResource, which does not
  // touch this index — stale entries are only wasted probes, but unbounded
  // accumulation of churned addresses is the same population class as the
  // header buckets above). O(index) per sweep.
  {
    const auto bucket_live = [&](uint32_t bucket) {
      return registry.surface_destination_relations_by_destination_texture
                     .find(bucket) !=
                 registry.surface_destination_relations_by_destination_texture
                     .end() ||
             registry.surface_destination_relation_by_destination_texture
                     .find(bucket) !=
                 registry.surface_destination_relation_by_destination_texture
                     .end();
    };
    const auto prune_bucket_index = [&](auto& index) {
      for (auto it = index.begin(); it != index.end();) {
        std::vector<uint32_t>& buckets = it->second;
        buckets.erase(std::remove_if(buckets.begin(), buckets.end(),
                                     [&](uint32_t bucket) {
                                       return !bucket_live(bucket);
                                     }),
                      buckets.end());
        if (buckets.empty()) {
          it = index.erase(it);
        } else {
          ++it;
        }
      }
    };
    prune_bucket_index(
        registry.surface_destination_relation_buckets_by_target_address);
    prune_bucket_index(
        registry.surface_destination_relation_buckets_by_target_fetch_key);
  }

  // Unconditional on a time limit (run-455/474 lesson twice over): run 474
  // had ZERO sweep lines because this logged only when something retired, so
  // "ran, nothing eligible yet" was indistinguishable from "never ran". A
  // sweep against a growing population must say so either way.
  {
    static std::chrono::steady_clock::time_point last_sweep_log{};
    const auto sweep_now = std::chrono::steady_clock::now();
    const bool retired_any =
        !stale_action_keys.empty() || !stale_link_keys.empty() ||
        retired_serial_entries != 0 || retired_link_serial_entries != 0 ||
        retired_header_buckets != 0;
    if (retired_any || sweep_now - last_sweep_log >= std::chrono::seconds(5)) {
      last_sweep_log = sweep_now;
      static uint32_t logged_private_registration_sweeps = 0;
      REXLOG_INFO(
          "Refii plume private-registration sweep #{} frame={} "
          "action_keys={} link_keys={} wrapper_entries={} "
          "link_call_entries={} "
          "private_sources={} private_targets={} links_by_surface={} "
          "action_bank={} header_buckets={} header_retired={} "
          "header_evictions={}",
          ++logged_private_registration_sweeps, current_frame_index,
          stale_action_keys.size(), stale_link_keys.size(),
          retired_serial_entries, retired_link_serial_entries,
          registry.private_texture_copies_by_source.size(),
          registry.private_texture_copies_by_target.size(),
          registry.surface_texture_links_by_surface.size(),
          registry.private_texture_copy_action_by_wrapper_call.size() +
              registry.private_texture_copy_action_by_wrapper_context.size(),
          registry.texture_header_actions_by_fetch_address.size(),
          retired_header_buckets,
          registry.texture_header_action_bucket_evictions);
    }
  }
}

void FableDirectDrainPendingTextureIdentityBackfills(double budget_ms) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  // New compose window: registrations get a fresh inline budget.
  g_inline_identity_backfill_ms_used = 0.0;
  if (g_pending_identity_backfill_objects.empty()) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const FableGuestResourceRegistryClock::time_point drain_t0 =
      FableGuestResourceRegistryClock::now();
  while (!g_pending_identity_backfill_objects.empty()) {
    const double elapsed_ms = FableGuestResourceRegistryElapsedMs(
        drain_t0, FableGuestResourceRegistryClock::now());
    if (elapsed_ms >= budget_ms) {
      break;
    }
    const uint32_t object_texture =
        g_pending_identity_backfill_objects.front();
    g_pending_identity_backfill_objects.erase(
        g_pending_identity_backfill_objects.begin());
    // Backfill against the object's CURRENT identity — it may have
    // re-registered since deferral, and the newest identity subsumes any
    // older one's alias closure. Objects whose identity was forgotten in the
    // meantime have nothing left to propagate.
    const auto identity_it =
        registry.texture_identities_by_object.find(object_texture);
    if (identity_it == registry.texture_identities_by_object.end()) {
      continue;
    }
    const TextureIdentityBackfillStats stats =
        BackfillTextureIdentityDependentIndexesLocked(registry,
                                                      identity_it->second);
    ++g_private_backfill_census.drained;
    g_private_backfill_census.actions_examined += stats.actions;
    g_private_backfill_census.links_examined += stats.links;
  }
  g_private_backfill_census.backfill_ms += FableGuestResourceRegistryElapsedMs(
      drain_t0, FableGuestResourceRegistryClock::now());
}

FablePrivateBackfillCensus FableDirectConsumePrivateBackfillCensus() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FablePrivateBackfillCensus census = g_private_backfill_census;
  census.pending =
      static_cast<uint32_t>(g_pending_identity_backfill_objects.size());
  g_private_backfill_census = FablePrivateBackfillCensus{};
  return census;
}

// Stage-2 deletion census (private-rect action registry): per-finder lookups
// vs hits. The kill-switch A/B only covered the select maze; these direct
// finders need their own liveness verdict before the storage dies.
// DEFAULT OFF. This is instrumentation, and it sat on one of the hottest paths
// in the renderer: every private-copy-action finder routed through it, finders
// are called in batches of 5-9 per site and several sites per draw, and each
// call took a GLOBAL mutex, constructed a std::string temporary (the map is
// keyed by std::string, the callers pass const char*), and did an O(log n)
// string-compare lookup. Set REFII_RENDERER_PRIVATE_COPY_CENSUS=1 to collect
// the liveness verdict this exists for; it is not needed on ordinary runs.
static bool PrivateCopyActionFinderCensusEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_RENDERER_PRIVATE_COPY_CENSUS");
    return value != nullptr && value[0] != '\0' && value[0] != '0';
  }();
  return enabled;
}

static void CountPrivateCopyActionFinderCensus(const char* finder, bool hit) {
  if (!PrivateCopyActionFinderCensusEnabled()) {
    return;
  }
  static std::mutex census_mutex;
  static std::map<std::string, std::pair<uint64_t, uint64_t>> census;
  std::lock_guard<std::mutex> lock(census_mutex);
  auto& entry = census[finder];
  ++entry.first;
  if (hit) {
    ++entry.second;
  }
  static uint64_t total = 0;
  if ((++total % 16384u) == 0u || (hit && entry.second <= 4u)) {
    std::string tally;
    for (const auto& [name, counts] : census) {
      tally += name;
      tally += "=";
      tally += std::to_string(counts.first);
      tally += "/";
      tally += std::to_string(counts.second);
      tally += " ";
    }
    REXLOG_INFO("Refii private-copy-action finder census [lookups/hits]: {}",
                tally);
  }
}

template <typename T>
static const T* ReportPrivateCopyActionFinderCensus(const char* finder,
                                                    const T* result) {
  CountPrivateCopyActionFinderCensus(finder, result != nullptr);
  return result;
}

static std::nullptr_t ReportPrivateCopyActionFinderCensus(const char* finder,
                                                          std::nullptr_t) {
  CountPrivateCopyActionFinderCensus(finder, false);
  return nullptr;
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsBySource(uint32_t source_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.private_texture_copies_by_source.find(source_texture);
  return ReportPrivateCopyActionFinderCensus("FindsBySource", it != registry.private_texture_copies_by_source.end() ? &it->second
                                                               : nullptr);
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByTarget(uint32_t target_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (target_surface == 0) {
    return ReportPrivateCopyActionFinderCensus("FindsByTarget", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto target_it = registry.private_texture_copies_by_target.find(
      target_surface);
  return ReportPrivateCopyActionFinderCensus("FindsByTarget", target_it != registry.private_texture_copies_by_target.end()
             ? &target_it->second
             : nullptr);
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByBase(uint32_t source_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_base_address == 0) {
    return ReportPrivateCopyActionFinderCensus("FindsByBase", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto base_it =
      registry.private_texture_copies_by_base.find(source_base_address);
  return ReportPrivateCopyActionFinderCensus("FindsByBase", base_it != registry.private_texture_copies_by_base.end()
             ? &base_it->second
             : nullptr);
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByFetch(
    const std::array<uint32_t, 6>& source_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(source_fetch_dwords);
  if (fetch_key == 0) {
    return ReportPrivateCopyActionFinderCensus("FindsByFetch", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto fetch_it =
      registry.private_texture_copies_by_fetch_key.find(fetch_key);
  if (fetch_it == registry.private_texture_copies_by_fetch_key.end()) {
    return ReportPrivateCopyActionFinderCensus("FindsByFetch", nullptr);
  }
  return ReportPrivateCopyActionFinderCensus("FindsByFetch", &fetch_it->second);
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByWrapperCall(
    uint64_t wrapper_call_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (wrapper_call_serial == 0) {
    return ReportPrivateCopyActionFinderCensus("FindByWrapperCall", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_action_by_wrapper_call.find(
          wrapper_call_serial);
  return ReportPrivateCopyActionFinderCensus("FindByWrapperCall", it != registry.private_texture_copy_action_by_wrapper_call.end()
             ? &it->second
             : nullptr);
}

// FableDirectFindPrivateTextureCopyActionByWrapperContext deleted
// 2026-07-19: finder census measured 0 hits over 4968 lookups across
// boot->title (577) and a user in-game session (579). The
// by_wrapper_context map stays -- a registry-internal reader remains.

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return ReportPrivateCopyActionFinderCensus("FindForDestinationTexture", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_action_by_destination_texture.find(texture);
  return ReportPrivateCopyActionFinderCensus("FindForDestinationTexture", it != registry.private_texture_copy_action_by_destination_texture.end()
             ? &it->second
             : nullptr);
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return ReportPrivateCopyActionFinderCensus("FindsForDestinationTexture", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture.find(texture);
  return ReportPrivateCopyActionFinderCensus("FindsForDestinationTexture", it !=
                 registry.private_texture_copy_actions_by_destination_texture
                     .end()
             ? &it->second
             : nullptr);
}

const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return ReportPrivateCopyActionFinderCensus("FindForDestinationTextureSlot", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_action_by_destination_texture_slot.find(
          slot_key);
  return ReportPrivateCopyActionFinderCensus("FindForDestinationTextureSlot", it !=
                 registry
                     .private_texture_copy_action_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr);
}

const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return ReportPrivateCopyActionFinderCensus("FindsForDestinationTextureSlot", nullptr);
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture_slot
          .find(slot_key);
  return ReportPrivateCopyActionFinderCensus("FindsForDestinationTextureSlot", it !=
                 registry
                     .private_texture_copy_actions_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr);
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyActionForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture.find(texture);
  return it !=
                 registry.private_texture_copy_actions_by_destination_texture
                     .end()
             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
                                                     draw_serial, source_slot)
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot, uint32_t frame_index,
    uint64_t draw_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_actions_by_destination_texture_slot
          .find(slot_key);
  return it !=
                 registry
                     .private_texture_copy_actions_by_destination_texture_slot
                     .end()
	             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
	                                                     draw_serial, source_slot)
	             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.private_texture_copy_output_actions_by_destination_texture.find(
          texture);
  return it !=
                 registry.private_texture_copy_output_actions_by_destination_texture
                     .end()
             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
                                                     draw_serial, source_slot)
             : nullptr;
}

const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot, uint32_t frame_index,
    uint64_t draw_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      PrivateTextureCopyDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.private_texture_copy_output_actions_by_destination_texture_slot
          .find(slot_key);
  return it !=
                 registry
                     .private_texture_copy_output_actions_by_destination_texture_slot
                     .end()
             ? SelectVisiblePrivateTextureCopyAction(&it->second, frame_index,
                                                     draw_serial, source_slot)
             : nullptr;
}


}  // namespace refii::renderer
