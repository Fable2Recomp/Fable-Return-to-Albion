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

void FableDirectRegisterSurfaceTextureLink(
    const FableSurfaceTextureLink& link) {
  FableDirectRegisterSurfaceTextureLinkInternal(
      link, FableRegistryRepairMode::kPropagateRepairs);
}

void FableDirectRegisterSurfaceTextureLinkInternal(
    const FableSurfaceTextureLink& link, FableRegistryRepairMode repair_mode) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (link.texture == 0 || link.source_surface == 0) {
    return;
  }
  if ((link.source_surface & 0xFF000000u) == 0xF1000000u) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  FableSurfaceTextureLink stored = link;
  EnrichSurfaceTextureLinkShaderRootsFromRegisteredActions(registry, stored);
  NormalizeSurfaceTextureLinkSampleSource(stored);
  if (repair_mode == FableRegistryRepairMode::kPropagateRepairs) {
    RepairSurfaceTextureLinksFromSurfaceTextureLinkSample(registry, stored);
  }
  if (SurfaceTextureLinkIsTrivialSelfEdge(stored)) {
    return;
  }
  const bool has_exact_copy_identity =
      stored.copy_call_serial != 0 || stored.copy_context_key != 0 ||
      stored.copy_symbol_address != 0;
  if (stored.trusted_destination_edge && !has_exact_copy_identity &&
      stored.backing_texture == 0 &&
      stored.backing_base_address == 0 && stored.target_content_serial == 0 &&
      stored.sampled_source.kind == FableRegisteredSampleSourceKind::kGpuSurface) {
    return;
  }
  if (SurfaceTextureLinkRequiresExactWrapperLookup(stored)) {
    if (stored.serial == 0) {
      stored.serial = ++registry.surface_texture_link_serial;
    }
    StoreSurfaceTextureDirectEdge(registry, stored);
    return;
  }
  // Generic-branch store: record the wrapper keys for the exact-key repair
  // gate (the exact branch records inside StoreSurfaceTextureDirectEdge).
  // Must stay AFTER the registration-order repair walk above so a link's own
  // first registration does not defeat the gate.
  NoteSurfaceTextureLinkWrapperIdentitySeen(registry, stored);
  constexpr size_t kMaxSurfaceTextureLinksPerBucket = 32;
  auto upsert_link = [&](std::vector<FableSurfaceTextureLink>& links) {
    for (FableSurfaceTextureLink& existing : links) {
      if (SameSurfaceTextureLinkIdentity(existing, stored)) {
        stored = MergeSurfaceTextureLinkMetadata(stored, existing);
        existing = stored;
        return;
      }
    }
    if (stored.serial == 0) {
      stored.serial = ++registry.surface_texture_link_serial;
    }
    if (links.size() >= kMaxSurfaceTextureLinksPerBucket) {
      links.erase(links.begin());
    }
    links.push_back(stored);
  };

  const auto upsert_u32_key = [&](auto& index, uint32_t key) {
    if (key != 0) {
      upsert_link(index[key]);
    }
  };
  const auto upsert_fetch_key_value = [&](uint64_t key) {
    if (key != 0) {
      upsert_link(registry.surface_texture_links_by_fetch_key[key]);
    }
  };

  std::vector<uint32_t> texture_keys;
  std::vector<uint32_t> base_keys;
  std::vector<uint64_t> fetch_keys;
  CollectTextureIdentityIndexKeysForObject(registry, stored.texture,
                                           texture_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForObject(registry, stored.backing_texture,
                                           texture_keys, base_keys,
                                           fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.texture_base_address,
                                         texture_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForBase(registry, stored.backing_base_address,
                                         texture_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(stored.texture_fetch_dwords),
      texture_keys, base_keys, fetch_keys);
  CollectTextureIdentityIndexKeysForFetch(
      registry, FableDirectTextureFetchKey(stored.backing_fetch_dwords),
      texture_keys, base_keys, fetch_keys);

  // Treat a Fable surface/texture link as an edge in the object graph. Index it
  // from both the proxy/source side and the backing side so direct rendering can
  // resolve by whichever object/base/fetch the title/menu draw binds.
  for (uint32_t texture_key : texture_keys) {
    upsert_u32_key(registry.surface_texture_links_by_texture, texture_key);
  }
  upsert_u32_key(registry.surface_texture_links_by_surface,
                 stored.source_surface);
  upsert_u32_key(registry.surface_texture_links_by_surface,
                 stored.render_target);
  for (uint32_t base_key : base_keys) {
    upsert_u32_key(registry.surface_texture_links_by_base, base_key);
  }
  for (uint64_t fetch_key : fetch_keys) {
    upsert_fetch_key_value(fetch_key);
  }
  StoreSurfaceTextureDirectEdge(registry, stored);
}

void FableDirectForgetSurfaceTextureLinksForResource(uint32_t resource) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (resource == 0) {
    return;
  }

  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  if (!HasIndexedSurfaceTextureLinkLocked(registry, resource)) {
    return;
  }

  if (PrunePrimarySurfaceTextureLinksMatchedLocked(
          registry, SingleResourceMatcher{resource})) {
    PruneSurfaceTextureDirectEdgesForResource(registry, resource);
    ++registry.surface_texture_link_serial;
  }
}

void FableDirectClearSurfaceTextureLinks() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  registry.surface_texture_link_serial = 0;
  registry.surface_texture_links_by_texture.clear();
  registry.surface_texture_links_by_surface.clear();
  registry.surface_texture_links_by_base.clear();
  registry.surface_texture_links_by_fetch_key.clear();
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
  registry.surface_destination_relation_serial = 0;
  registry.surface_destination_relation_generation = 0;
  g_surface_destination_relation_generation_mirror.store(
      0, std::memory_order_relaxed);
  registry.surface_destination_relations_by_source_surface.clear();
  registry.surface_destination_relations_by_destination_texture.clear();
  registry.surface_destination_relations_by_destination_texture_slot.clear();
  registry.surface_destination_relation_by_destination_texture.clear();
  registry.surface_destination_relation_by_destination_texture_slot.clear();
  registry.surface_destination_relation_buckets_by_target_address.clear();
  registry.surface_destination_relation_buckets_by_target_fetch_key.clear();
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_texture.find(texture);
  return it != registry.surface_texture_links_by_texture.end() ? &it->second
                                                               : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_exact_link_by_destination_texture.find(texture);
  return it != registry.surface_texture_exact_link_by_destination_texture.end()
             ? &it->second
             : nullptr;
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksForDestinationTexture(uint32_t texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_exact_links_by_destination_texture.find(texture);
  return it != registry.surface_texture_exact_links_by_destination_texture.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      SurfaceTextureDestinationSlotKey(texture, source_slot);
  const auto it =
      registry.surface_texture_exact_link_by_destination_texture_slot.find(
          slot_key);
  return it !=
                 registry.surface_texture_exact_link_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_exact_links_by_destination_texture.find(texture);
  return it != registry.surface_texture_exact_links_by_destination_texture.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForSourceSurface(uint32_t source_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_source_surface.find(source_surface);
  return it != registry.surface_texture_link_by_source_surface.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForSourceSurfaceSlot(
    uint32_t source_surface, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      SurfaceTextureSourceSurfaceSlotKey(source_surface, source_slot);
  const auto it =
      registry.surface_texture_link_by_source_surface_slot.find(slot_key);
  return it != registry.surface_texture_link_by_source_surface_slot.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForSourceSurface(
    uint32_t source_surface, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_surface.find(source_surface);
  return it != registry.surface_texture_links_by_surface.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkForBase(
    uint32_t texture_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture_base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_base.find(texture_base_address);
  return it != registry.surface_texture_link_by_base.end() ? &it->second
                                                           : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkForFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(texture_fetch_dwords);
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_link_by_fetch_key.find(fetch_key);
  return it != registry.surface_texture_link_by_fetch_key.end() ? &it->second
                                                                : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByCopyCall(
    uint64_t copy_call_serial) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (copy_call_serial == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_copy_call.find(copy_call_serial);
  return it != registry.surface_texture_link_by_copy_call.end() ? &it->second
                                                                : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByCopyContext(
    uint64_t copy_context_key) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (copy_context_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_link_by_copy_context.find(copy_context_key);
  return it != registry.surface_texture_link_by_copy_context.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForBase(
    uint32_t texture_base_address, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture_base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_links_by_base.find(texture_base_address);
  return it != registry.surface_texture_links_by_base.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(texture_fetch_dwords);
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_fetch_key.find(fetch_key);
  return it != registry.surface_texture_links_by_fetch_key.end()
             ? SelectVisibleSurfaceTextureLink(&it->second, frame_index,
                                               draw_serial, source_slot)
             : nullptr;
}

const std::vector<FableSurfaceDestinationTextureRelation>*
FableDirectFindSurfaceDestinationRelationsBySourceSurface(
    uint32_t source_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relations_by_source_surface.find(
          source_surface);
  return it != registry.surface_destination_relations_by_source_surface.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceDestinationTextureRelation*
FableDirectFindSurfaceDestinationRelationForDestinationTexture(
    uint32_t destination_texture) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (destination_texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relation_by_destination_texture.find(
          destination_texture);
  return it != registry.surface_destination_relation_by_destination_texture.end()
             ? &it->second
             : nullptr;
}

const FableSurfaceDestinationTextureRelation*
FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
    uint32_t destination_texture, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (destination_texture == 0 || source_slot == 0xFFFFFFFFu) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const uint64_t slot_key =
      SurfaceTextureDestinationSlotKey(destination_texture, source_slot);
  const auto it =
      registry.surface_destination_relation_by_destination_texture_slot.find(
          slot_key);
  return it !=
                 registry.surface_destination_relation_by_destination_texture_slot
                     .end()
             ? &it->second
             : nullptr;
}

const FableSurfaceDestinationTextureRelation*
FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
    uint32_t destination_texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (destination_texture == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relations_by_destination_texture.find(
          destination_texture);
  return it !=
                 registry.surface_destination_relations_by_destination_texture.end()
             ? SelectVisibleSurfaceDestinationRelation(
                   &it->second, frame_index, draw_serial, source_slot)
             : nullptr;
}

uint64_t FableDirectGetSurfaceDestinationRelationGeneration() {
  // Deliberately UNLOCKED — see
  // g_surface_destination_relation_generation_mirror. This is called once per
  // draw purely as a change-detector; taking the registry-wide mutex for it
  // cost more than the scan it guards.
  return g_surface_destination_relation_generation_mirror.load(
      std::memory_order_relaxed);
}

namespace {

// Shared visibility/preference rules for the global and targeted relation
// snapshot builders — one definition so the targeted scan can never drift
// from the full-population semantics.
bool SnapshotRelationVisible(
    const FableSurfaceDestinationTextureRelation& relation, bool filter_visible,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  if (!filter_visible) {
    return true;
  }
  if (relation.link.frame_index != 0 &&
      relation.link.frame_index != frame_index) {
    return false;
  }
  return SurfaceDestinationRelationIsVisibleToDraw(relation, frame_index,
                                                   draw_serial, source_slot);
}

bool SnapshotRelationPreferred(
    const FableSurfaceDestinationTextureRelation& candidate,
    const FableSurfaceDestinationTextureRelation* current, bool filter_visible,
    uint32_t frame_index) {
  if (!current) {
    return true;
  }
  if (filter_visible && candidate.link.frame_index == frame_index &&
      current->link.frame_index != frame_index) {
    return true;
  }
  if (filter_visible && candidate.link.frame_index != frame_index &&
      current->link.frame_index == frame_index) {
    return false;
  }
  return SurfaceDestinationRelationShouldPrefer(candidate, current);
}

void SortAndTakeSnapshotRelations(
    std::vector<FableSurfaceDestinationTextureRelation>& relations,
    size_t max_relations, FableSurfaceDestinationRelationSnapshot& snapshot) {
  std::sort(relations.begin(), relations.end(),
            [](const FableSurfaceDestinationTextureRelation& lhs,
               const FableSurfaceDestinationTextureRelation& rhs) {
              if (lhs.serial != rhs.serial) {
                return lhs.serial > rhs.serial;
              }
              if (lhs.link.draw_serial != rhs.link.draw_serial) {
                return lhs.link.draw_serial > rhs.link.draw_serial;
              }
              return lhs.destination_texture < rhs.destination_texture;
            });
  const size_t take_count = std::min(relations.size(), max_relations);
  snapshot.relations.insert(snapshot.relations.end(), relations.begin(),
                            relations.begin() + take_count);
  snapshot.omitted_relations = static_cast<uint32_t>(
      std::min<size_t>(relations.size() - take_count, UINT32_MAX));
}

}  // namespace

FableSurfaceDestinationRelationSnapshot
FableDirectGetSurfaceDestinationRelationSnapshot(size_t max_relations,
                                                 uint32_t frame_index,
                                                 uint64_t draw_serial,
                                                 uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableSurfaceDestinationRelationSnapshot snapshot;
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  snapshot.serial = registry.surface_destination_relation_serial;
  const bool filter_visible = frame_index != UINT32_MAX;
  const auto relation_visible =
      [&](const FableSurfaceDestinationTextureRelation& relation) {
        return SnapshotRelationVisible(relation, filter_visible, frame_index,
                                       draw_serial, source_slot);
      };
  const auto relation_preferred =
      [&](const FableSurfaceDestinationTextureRelation& candidate,
          const FableSurfaceDestinationTextureRelation* current) {
        return SnapshotRelationPreferred(candidate, current, filter_visible,
                                         frame_index);
      };
  if (max_relations == 0) {
    size_t visible_count = 0;
    for (const auto& [destination_texture, relations] :
         registry.surface_destination_relations_by_destination_texture) {
      (void)destination_texture;
      for (const FableSurfaceDestinationTextureRelation& relation :
           relations) {
        visible_count += relation_visible(relation) ? 1u : 0u;
      }
    }
    snapshot.omitted_relations =
        static_cast<uint32_t>(std::min<size_t>(visible_count, UINT32_MAX));
    return snapshot;
  }

  std::vector<FableSurfaceDestinationTextureRelation> relations;
  relations.reserve(std::min(
      registry.surface_destination_relations_by_destination_texture.size(),
      max_relations));
  std::unordered_map<uint32_t, FableSurfaceDestinationTextureRelation>
      best_by_destination;
  for (const auto& [destination_texture, bucket] :
       registry.surface_destination_relations_by_destination_texture) {
    for (const FableSurfaceDestinationTextureRelation& relation : bucket) {
      if (!relation_visible(relation)) {
        continue;
      }
      auto [it, inserted] =
          best_by_destination.emplace(destination_texture, relation);
      if (!inserted && relation_preferred(relation, &it->second)) {
        it->second = relation;
      }
    }
  }
  for (const auto& [destination_texture, relation] :
       registry.surface_destination_relation_by_destination_texture) {
    if (!relation_visible(relation)) {
      continue;
    }
    auto [it, inserted] =
        best_by_destination.emplace(destination_texture, relation);
    if (!inserted && relation_preferred(relation, &it->second)) {
      it->second = relation;
    }
  }
  relations.reserve(std::min(best_by_destination.size(), max_relations));
  for (const auto& [destination_texture, relation] : best_by_destination) {
    (void)destination_texture;
    relations.push_back(relation);
  }
  SortAndTakeSnapshotRelations(relations, max_relations, snapshot);
  return snapshot;
}

FableSurfaceDestinationRelationSnapshot
FableDirectGetSurfaceDestinationRelationSnapshotForBuckets(
    const std::vector<uint32_t>& destination_buckets, size_t max_relations,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  FableSurfaceDestinationRelationSnapshot snapshot;
  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  snapshot.serial = registry.surface_destination_relation_serial;
  if (max_relations == 0 || destination_buckets.empty()) {
    return snapshot;
  }
  const bool filter_visible = frame_index != UINT32_MAX;
  std::unordered_map<uint32_t, FableSurfaceDestinationTextureRelation>
      best_by_destination;
  const auto accumulate =
      [&](uint32_t destination_texture,
          const FableSurfaceDestinationTextureRelation& relation) {
        if (!SnapshotRelationVisible(relation, filter_visible, frame_index,
                                     draw_serial, source_slot)) {
          return;
        }
        auto [it, inserted] =
            best_by_destination.emplace(destination_texture, relation);
        if (!inserted && SnapshotRelationPreferred(relation, &it->second,
                                                   filter_visible,
                                                   frame_index)) {
          it->second = relation;
        }
      };
  for (uint32_t bucket : destination_buckets) {
    // A missing bucket is a stale index entry: the relations were pruned
    // after the index pointed here. Nothing to visit, nothing wrong.
    const auto bucket_it =
        registry.surface_destination_relations_by_destination_texture.find(
            bucket);
    if (bucket_it !=
        registry.surface_destination_relations_by_destination_texture.end()) {
      for (const FableSurfaceDestinationTextureRelation& relation :
           bucket_it->second) {
        accumulate(bucket, relation);
      }
    }
    const auto direct_it =
        registry.surface_destination_relation_by_destination_texture.find(
            bucket);
    if (direct_it !=
        registry.surface_destination_relation_by_destination_texture.end()) {
      accumulate(bucket, direct_it->second);
    }
  }
  std::vector<FableSurfaceDestinationTextureRelation> relations;
  relations.reserve(std::min(best_by_destination.size(), max_relations));
  for (const auto& [destination_texture, relation] : best_by_destination) {
    (void)destination_texture;
    relations.push_back(relation);
  }
  SortAndTakeSnapshotRelations(relations, max_relations, snapshot);
  return snapshot;
}

const std::vector<uint32_t>*
FableDirectFindSurfaceDestinationRelationBucketsByTargetAddress(
    uint32_t address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relation_buckets_by_target_address.find(
          address);
  return it !=
                 registry.surface_destination_relation_buckets_by_target_address
                     .end()
             ? &it->second
             : nullptr;
}

const std::vector<uint32_t>*
FableDirectFindSurfaceDestinationRelationBucketsByTargetFetchKey(
    uint64_t fetch_key) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry =
      FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_destination_relation_buckets_by_target_fetch_key.find(
          fetch_key);
  return it != registry.surface_destination_relation_buckets_by_target_fetch_key
                   .end()
             ? &it->second
             : nullptr;
}

size_t FableDirectGetSurfaceDestinationRelationBucketCount() {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return FableDirectGuestResourceRegistry()
      .surface_destination_relations_by_destination_texture.size();
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksBySurface(uint32_t source_surface) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (source_surface == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_links_by_surface.find(source_surface);
  return it != registry.surface_texture_links_by_surface.end() ? &it->second
                                                               : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByBase(
    uint32_t texture_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestSurfaceTextureLink(
      FableDirectFindSurfaceTextureLinksByBase(texture_base_address));
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByBase(uint32_t texture_base_address) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  if (texture_base_address == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it =
      registry.surface_texture_links_by_base.find(texture_base_address);
  return it != registry.surface_texture_links_by_base.end() ? &it->second
                                                            : nullptr;
}

const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  return SelectBestSurfaceTextureLink(
      FableDirectFindSurfaceTextureLinksByFetch(texture_fetch_dwords));
}

const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords) {
  std::lock_guard<std::recursive_mutex> lock(
      FableDirectGuestResourceRegistryMutex());
  const uint64_t fetch_key = FableDirectTextureFetchKey(texture_fetch_dwords);
  if (fetch_key == 0) {
    return nullptr;
  }
  const FableGuestResourceRegistry& registry = FableDirectGuestResourceRegistry();
  const auto it = registry.surface_texture_links_by_fetch_key.find(fetch_key);
  return it != registry.surface_texture_links_by_fetch_key.end() ? &it->second
                                                                 : nullptr;
}


}  // namespace refii::renderer
