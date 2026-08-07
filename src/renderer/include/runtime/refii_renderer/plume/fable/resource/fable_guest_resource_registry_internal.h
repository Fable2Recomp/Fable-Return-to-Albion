#pragma once

#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace refii::renderer {

using FableGuestResourceRegistryClock = std::chrono::steady_clock;

enum class FableRegistryRepairMode {
  kPropagateRepairs,
  kSkipBackfillRepairs,
  kDeferRepairs,
};

struct TextureIdentityBackfillStats {
  size_t actions = 0;
  size_t links = 0;
  size_t destination_candidates = 0;
  double collect_actions_ms = 0.0;
  double collect_links_ms = 0.0;
  double collect_destinations_ms = 0.0;
  double register_actions_ms = 0.0;
  double register_links_ms = 0.0;
};

struct SingleResourceMatcher {
  uint32_t resource;
  bool operator()(uint32_t value) const { return value == resource; }
};

void FableDirectRegisterSurfaceTextureLinkInternal(
    const FableSurfaceTextureLink& link, FableRegistryRepairMode repair_mode);
void FableDirectRegisterPrivateTextureCopyActionInternal(
    const FablePrivateTextureCopyAction& action,
    FableRegistryRepairMode repair_mode);

extern double g_inline_identity_backfill_ms_used;
extern uint32_t g_texture_header_action_touch_frame;

TextureIdentityBackfillStats BackfillTextureIdentityDependentIndexesLocked(
    FableGuestResourceRegistry& registry,
    const FableTextureIdentity& identity);

void CollectTextureIdentityIndexKeysForBase(
    const FableGuestResourceRegistry& registry, uint32_t base_address,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys);

void CollectTextureIdentityIndexKeysForFetch(
    const FableGuestResourceRegistry& registry, uint64_t fetch_key,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys);

void CollectTextureIdentityIndexKeysForObject(
    const FableGuestResourceRegistry& registry, uint32_t object,
    std::vector<uint32_t>& object_keys, std::vector<uint32_t>& base_keys,
    std::vector<uint64_t>& fetch_keys);

void EnrichSurfaceTextureLinkShaderRootsFromRegisteredActions(
    const FableGuestResourceRegistry& registry,
    FableSurfaceTextureLink& link);

double FableGuestResourceRegistryElapsedMs(
    FableGuestResourceRegistryClock::time_point begin,
    FableGuestResourceRegistryClock::time_point end);

void ForgetPrivateRegistrationsForResourceSetsLocked(
    FableGuestResourceRegistry& registry,
    const std::unordered_set<uint32_t>& action_matched,
    const std::unordered_set<uint32_t>& link_matched,
    const std::unordered_set<uint32_t>& derived_matched);

bool HasIndexedPrivateTextureCopyActionLocked(
    const FableGuestResourceRegistry& registry, uint32_t resource);

bool HasIndexedSurfaceTextureLinkLocked(
    const FableGuestResourceRegistry& registry, uint32_t resource);

FableSurfaceTextureLink MergeSurfaceTextureLinkMetadata(
    const FableSurfaceTextureLink& candidate,
    const FableSurfaceTextureLink& current);

void NormalizePrivateTextureCopySampleSource(
    FablePrivateTextureCopyAction& action);

void NormalizeSurfaceTextureLinkSampleSource(FableSurfaceTextureLink& link);

void NoteSurfaceTextureLinkWrapperIdentitySeen(
    FableGuestResourceRegistry& registry, const FableSurfaceTextureLink& link);

uint64_t PrivateTextureCopyDestinationSlotKey(uint32_t destination_texture,
                                              uint32_t source_slot);

template <typename ResourceMatcher>
bool PrivateTextureCopyActionReferencesMatched(
    const FablePrivateTextureCopyAction& action,
    const ResourceMatcher& matches) {
  return matches(action.source_texture) ||
         matches(action.source_base_address) ||
         matches(action.backing_texture) ||
         matches(action.backing_base_address) ||
         matches(action.target_surface) || matches(action.render_target) ||
         matches(action.depth_stencil);
}

template <typename ResourceMatcher>
bool SurfaceTextureLinkReferencesMatched(const FableSurfaceTextureLink& link,
                                         const ResourceMatcher& matches) {
  return matches(link.texture) || matches(link.texture_base_address) ||
         matches(link.source_surface) || matches(link.backing_texture) ||
         matches(link.backing_base_address);
}


template <typename ResourceMatcher>
bool PrunePrimaryPrivateTextureCopyActionsMatchedLocked(
    FableGuestResourceRegistry& registry, const ResourceMatcher& matches);

template <typename ResourceMatcher>
bool PrunePrimarySurfaceTextureLinksMatchedLocked(
    FableGuestResourceRegistry& registry, const ResourceMatcher& matches);

void PrunePrivateTextureCopyDirectActionsForResource(
    FableGuestResourceRegistry& registry, uint32_t resource);

void PruneSurfaceTextureDirectEdgesForResource(
    FableGuestResourceRegistry& registry, uint32_t resource);

void RemoveTextureIdentityIndexesLocked(FableGuestResourceRegistry& registry,
                                        const FableTextureIdentity& identity);

void RepairSurfaceTextureLinksForPrivateCopyActionsLocked(
    FableGuestResourceRegistry& registry,
    const std::vector<const FablePrivateTextureCopyAction*>& actions);

void RepairSurfaceTextureLinksFromSurfaceTextureLinkSample(
    FableGuestResourceRegistry& registry,
    const FableSurfaceTextureLink& sample_link);

bool SamePrivateTextureCopyActionIdentity(
    const FablePrivateTextureCopyAction& lhs,
    const FablePrivateTextureCopyAction& rhs);

bool SameShaderIdentity(const FableShaderIdentity& lhs,
                        const FableShaderIdentity& rhs);

bool SameSurfaceTextureLinkIdentity(const FableSurfaceTextureLink& lhs,
                                    const FableSurfaceTextureLink& rhs);

bool SameVertexDeclarationIdentity(
    const FableVertexDeclarationIdentity& lhs,
    const FableVertexDeclarationIdentity& rhs);

const FableSurfaceTextureLink* SelectBestSurfaceTextureLink(
    const std::vector<FableSurfaceTextureLink>* links);

const FablePrivateTextureCopyAction* SelectVisiblePrivateTextureCopyAction(
    const std::vector<FablePrivateTextureCopyAction>* actions,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot);

const FableSurfaceTextureLink* SelectVisibleSurfaceTextureLink(
    const std::vector<FableSurfaceTextureLink>* links, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot);

void StorePrivateTextureCopyDirectAction(
    FableGuestResourceRegistry& registry,
    const FablePrivateTextureCopyAction& action,
    FableRegistryRepairMode repair_mode);

void StoreSurfaceTextureDirectEdge(FableGuestResourceRegistry& registry,
                                   const FableSurfaceTextureLink& link);

bool SurfaceDestinationRelationIsVisibleToDraw(
    const FableSurfaceDestinationTextureRelation& relation,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot);

bool SurfaceDestinationRelationShouldPrefer(
    const FableSurfaceDestinationTextureRelation& candidate,
    const FableSurfaceDestinationTextureRelation* current);

uint64_t SurfaceTextureDestinationSlotKey(uint32_t destination_texture,
                                          uint32_t source_slot);

bool SurfaceTextureLinkIsTrivialSelfEdge(
    const FableSurfaceTextureLink& link);

bool SurfaceTextureLinkRequiresExactWrapperLookup(
    const FableSurfaceTextureLink& link);

uint64_t SurfaceTextureSourceSurfaceSlotKey(uint32_t source_surface,
                                            uint32_t source_slot);

extern std::atomic<uint64_t> g_surface_destination_relation_generation_mirror;
extern std::vector<uint32_t> g_pending_identity_backfill_objects;

const FableSurfaceDestinationTextureRelation*
SelectVisibleSurfaceDestinationRelation(
    const std::vector<FableSurfaceDestinationTextureRelation>* relations,
    uint32_t frame_index, uint64_t draw_serial, uint32_t source_slot);

extern FablePrivateBackfillCensus g_private_backfill_census;

template <typename ResourceMatcher>
bool PrunePrimaryPrivateTextureCopyActionsMatchedLocked(
    FableGuestResourceRegistry& registry, const ResourceMatcher& matches) {
  const auto action_matches =
      [&](const FablePrivateTextureCopyAction& action) {
    // Keep exact copy actions keyed by the real source/backing/target endpoints.
    // Shader/depth/RT context participates in matching and scoring elsewhere,
    // but it must not make the object graph forget a still-live copy relation.
    return PrivateTextureCopyActionReferencesMatched(action, matches);
  };

  bool removed_any = false;
  const auto prune_index = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          matches(static_cast<uint32_t>(it->first))) {
        it = index.erase(it);
        removed_any = true;
        continue;
      }
      std::vector<FablePrivateTextureCopyAction>& actions = it->second;
      const size_t before = actions.size();
      actions.erase(std::remove_if(actions.begin(), actions.end(),
                                   action_matches),
                    actions.end());
      removed_any = removed_any || actions.size() != before;
      if (actions.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };

  prune_index(registry.private_texture_copies_by_source, true);
  prune_index(registry.private_texture_copies_by_target, true);
  prune_index(registry.private_texture_copies_by_base, true);
  prune_index(registry.private_texture_copies_by_fetch_key, false);
  for (auto it = registry.private_texture_copy_source_by_base.begin();
       it != registry.private_texture_copy_source_by_base.end();) {
    if (matches(it->first) || matches(it->second)) {
      it = registry.private_texture_copy_source_by_base.erase(it);
      removed_any = true;
    } else {
      ++it;
    }
  }
  return removed_any;
}

template <typename ResourceMatcher>
bool PrunePrimarySurfaceTextureLinksMatchedLocked(
    FableGuestResourceRegistry& registry, const ResourceMatcher& matches) {
  const auto link_matches = [&](const FableSurfaceTextureLink& link) {
    // Shader, depth, and render-target fields describe the context where Fable
    // discovered the edge. They are not ownership endpoints for the
    // texture/surface relationship, and pruning on them can erase a valid title
    // copy edge between adjacent GUI draws.
    return SurfaceTextureLinkReferencesMatched(link, matches);
  };

  bool removed_any = false;
  const auto prune_index = [&](auto& index, bool prune_resource_key) {
    for (auto it = index.begin(); it != index.end();) {
      if (prune_resource_key &&
          matches(static_cast<uint32_t>(it->first))) {
        it = index.erase(it);
        removed_any = true;
        continue;
      }
      std::vector<FableSurfaceTextureLink>& links = it->second;
      const size_t before = links.size();
      links.erase(std::remove_if(links.begin(), links.end(), link_matches),
                  links.end());
      removed_any = removed_any || links.size() != before;
      if (links.empty()) {
        it = index.erase(it);
      } else {
        ++it;
      }
    }
  };

  prune_index(registry.surface_texture_links_by_texture, true);
  prune_index(registry.surface_texture_links_by_surface, true);
  prune_index(registry.surface_texture_links_by_base, true);
  prune_index(registry.surface_texture_links_by_fetch_key, false);
  return removed_any;
}

}  // namespace refii::renderer
