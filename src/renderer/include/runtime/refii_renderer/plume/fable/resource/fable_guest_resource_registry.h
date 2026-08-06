#pragma once

#include "runtime/refii_renderer/hooks/resource/fable_observer_resources.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <source_location>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::renderer {

enum class FablePrivateTextureCopyKind : uint8_t {
  kColor,
  kDepth,
  kColorSibling,
};

enum class FableRegisteredSampleSourceKind : uint8_t {
  kUnknown,
  kGuestTexture,
  kBackingTexture,
  kGpuSurface,
};

struct FableRegisteredSampleSource {
  FableRegisteredSampleSourceKind kind =
      FableRegisteredSampleSourceKind::kUnknown;
  uint32_t resource = 0;
  uint32_t base_address = 0;
  std::array<uint32_t, 6> fetch_dwords = {};
  uint64_t content_serial = 0;
};

struct FablePrivateTextureCopyAction {
  uint64_t serial = 0;
  uint32_t source_slot = 0xFFFFFFFFu;
  uint32_t source_texture = 0;
  uint32_t source_base_address = 0;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t source_format = 0;
  std::array<uint32_t, 6> source_fetch_dwords = {};
  uint32_t backing_texture = 0;
  uint32_t backing_base_address = 0;
  uint32_t backing_width = 0;
  uint32_t backing_height = 0;
  uint32_t backing_format = 0;
  std::array<uint32_t, 6> backing_fetch_dwords = {};
  uint64_t backing_content_serial = 0;
  uint32_t target_surface = 0;
  uint32_t target_width = 0;
  uint32_t target_height = 0;
  uint32_t target_format = 0;
  uint64_t target_content_serial = 0;
  uint32_t render_target = 0;
  uint32_t depth_stencil = 0;
  uint32_t vertex_shader = 0;
  uint64_t vertex_shader_root_call_serial = 0;
  uint64_t vertex_shader_root_context_key = 0;
  uint32_t vertex_shader_root_symbol_address = 0;
  uint32_t vertex_shader_root_lr = 0;
  uint32_t pixel_shader = 0;
  uint64_t pixel_shader_root_call_serial = 0;
  uint64_t pixel_shader_root_context_key = 0;
  uint32_t pixel_shader_root_symbol_address = 0;
  uint32_t pixel_shader_root_lr = 0;
  uint32_t lr = 0;
  uint32_t frame_index = 0;
  uint64_t draw_serial = 0;
  uint64_t wrapper_call_serial = 0;
  uint64_t wrapper_context_key = 0;
  uint32_t wrapper_symbol_address = 0;
  uint32_t wrapper_source_texture = 0;
  uint32_t wrapper_arg0 = 0;
  uint32_t wrapper_arg1 = 0;
  uint32_t wrapper_arg2 = 0;
  uint32_t wrapper_rect_pointer = 0;
  std::array<uint32_t, 4> wrapper_rect_dwords = {};
  std::array<uint32_t, 4> wrapper_setup_globals = {};
  bool wrapper_source_exact = false;
  FableRegisteredSampleSource sampled_source;
  FablePrivateTextureCopyKind kind = FablePrivateTextureCopyKind::kColor;
  bool rectangle_valid = false;
  bool uv_override = false;
  float rect_width = 0.0f;
  float rect_height = 0.0f;
  float source_x = 0.0f;
  float source_y = 0.0f;
  float u0 = 0.0f;
  float v0 = 0.0f;
  float u1 = 1.0f;
  float v1 = 1.0f;
};

struct FableTextureIdentity {
  uint64_t serial = 0;
  uint32_t object_texture = 0;
  uint32_t parent_texture = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint64_t upload_generation = 0;
  bool meaningful_upload = false;
  std::array<uint32_t, 6> fetch_dwords = {};
};

struct FableTextureFetchBindingAction {
  uint64_t serial = 0;
  uint64_t call_serial = 0;
  uint64_t context_key = 0;
  uint64_t header_action_serial = 0;
  uint64_t header_context_key = 0;
  uint32_t texture_slot = 0xFFFFFFFFu;
  uint32_t texture_object = 0;
  uint32_t parent_texture = 0;
  uint32_t source_address = 0;
  uint32_t source_header = 0;
  uint32_t base_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint32_t symbol_address = 0;
  uint32_t header_symbol_address = 0;
  uint32_t lr = 0;
  uint32_t frame_index = 0;
  bool header_stack_scratch = false;
  bool header_fetch_payload_match = false;
  std::array<uint32_t, 6> fetch_dwords = {};
};

struct FableTextureHeaderAction {
  uint64_t serial = 0;
  uint64_t call_serial = 0;
  uint64_t context_key = 0;
  uint32_t symbol_address = 0;
  uint32_t lr = 0;
  uint32_t frame_index = 0;
  uint32_t texture_header = 0;
  uint32_t fetch_address = 0;
  uint32_t texture_object = 0;
  uint32_t parent_texture = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  bool stack_scratch = false;
  std::array<uint32_t, 6> fetch_dwords = {};
};

struct FableSurfaceTextureLink {
  uint64_t serial = 0;
  uint64_t copy_call_serial = 0;
  uint64_t copy_context_key = 0;
  uint32_t copy_symbol_address = 0;
  uint32_t source_slot = 0xFFFFFFFFu;
  bool trusted_destination_edge = false;
  uint32_t texture = 0;
  uint32_t texture_base_address = 0;
  uint32_t texture_width = 0;
  uint32_t texture_height = 0;
  uint32_t texture_format = 0;
  std::array<uint32_t, 6> texture_fetch_dwords = {};
  uint32_t source_surface = 0;
  uint32_t backing_texture = 0;
  uint32_t backing_base_address = 0;
  std::array<uint32_t, 6> backing_fetch_dwords = {};
  uint64_t backing_content_serial = 0;
  uint64_t target_content_serial = 0;
  uint32_t render_target = 0;
  uint32_t depth_stencil = 0;
  uint32_t vertex_shader = 0;
  uint64_t vertex_shader_root_call_serial = 0;
  uint64_t vertex_shader_root_context_key = 0;
  uint32_t vertex_shader_root_symbol_address = 0;
  uint32_t vertex_shader_root_lr = 0;
  uint32_t pixel_shader = 0;
  uint64_t pixel_shader_root_call_serial = 0;
  uint64_t pixel_shader_root_context_key = 0;
  uint32_t pixel_shader_root_symbol_address = 0;
  uint32_t pixel_shader_root_lr = 0;
  uint32_t frame_index = 0;
  uint64_t draw_serial = 0;
  uint32_t wrapper_source_texture = 0;
  bool wrapper_source_exact = false;
  std::array<uint32_t, 4> wrapper_setup_globals = {};
  FableRegisteredSampleSource sampled_source;
  FablePrivateTextureCopyKind kind = FablePrivateTextureCopyKind::kColor;
};

struct FableSurfaceDestinationTextureRelation {
  uint64_t serial = 0;
  uint32_t source_surface = 0;
  uint32_t destination_texture = 0;
  uint32_t source_slot = 0xFFFFFFFFu;
  FableSurfaceTextureLink link;
};

struct FableSurfaceDestinationRelationSnapshot {
  uint64_t serial = 0;
  std::vector<FableSurfaceDestinationTextureRelation> relations;
  uint32_t omitted_relations = 0;
};

struct FableShaderIdentity {
  uint64_t serial = 0;
  FableGuestResourceKind kind = FableGuestResourceKind::kUnknown;
  uint32_t object_shader = 0;
  uint32_t parent_shader = 0;
  uint32_t bytecode_address = 0;
  uint32_t bytecode_size = 0;
  uint32_t bytecode_offset = 0;
  uint64_t bytecode_hash = 0;
};

struct FableVertexDeclarationIdentity {
  uint64_t serial = 0;
  uint32_t object_declaration = 0;
  uint32_t parent_declaration = 0;
  uint32_t elements_address = 0;
  uint32_t elements_size = 0;
  uint32_t element_count = 0;
  uint32_t max_stream = 0;
  uint32_t stream_mask = 0;
  uint32_t declaration_flags = 0;
  uint64_t elements_hash = 0;
};

struct FableGuestResourceCounts {
  uint64_t live_resources = 0;
  uint64_t live_textures = 0;
  uint64_t live_surfaces = 0;
  uint64_t live_vertex_buffers = 0;
  uint64_t live_index_buffers = 0;
  uint64_t live_shaders = 0;
};

struct FableGuestResourceRegistry {
  std::unordered_map<uint32_t, RendererResourceRecord> resources;
  RendererResourceRecord last_resource;
  uint64_t creates = 0;
  uint64_t destroys = 0;
  std::unordered_map<uint32_t, uint64_t> changed_serials;
  std::unordered_map<uint32_t, uint64_t> destroyed_serials;
  uint64_t private_texture_copy_serial = 0;
  uint64_t private_texture_copy_index_serial = 0;
  uint32_t private_texture_copy_logs = 0;
  uint64_t texture_identity_serial = 0;
  std::unordered_map<uint32_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copies_by_source;
  std::unordered_map<uint32_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copies_by_target;
  std::unordered_map<uint32_t, uint32_t> private_texture_copy_source_by_base;
  std::unordered_map<uint32_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copies_by_base;
  std::unordered_map<uint64_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copies_by_fetch_key;
  std::unordered_map<uint32_t, FablePrivateTextureCopyAction>
      private_texture_copy_action_by_destination_texture;
  std::unordered_map<uint64_t, FablePrivateTextureCopyAction>
      private_texture_copy_action_by_destination_texture_slot;
  std::unordered_map<uint32_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copy_actions_by_destination_texture;
  std::unordered_map<uint64_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copy_actions_by_destination_texture_slot;
  std::unordered_map<uint32_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copy_output_actions_by_destination_texture;
  std::unordered_map<uint64_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copy_output_actions_by_destination_texture_slot;
  std::unordered_map<uint64_t, FablePrivateTextureCopyAction>
      private_texture_copy_action_by_wrapper_call;
  std::unordered_map<uint64_t, FablePrivateTextureCopyAction>
      private_texture_copy_action_by_wrapper_context;
  std::unordered_map<uint32_t, FableTextureIdentity>
      texture_identities_by_object;
  std::unordered_map<uint32_t, std::vector<uint32_t>>
      texture_identity_objects_by_base;
  std::unordered_map<uint64_t, std::vector<uint32_t>>
      texture_identity_objects_by_fetch_key;
  uint64_t texture_header_action_serial = 0;
  std::unordered_map<uint32_t, std::vector<FableTextureHeaderAction>>
      texture_header_actions_by_fetch_address;
  // Secondary index for the exact-fetch-identity/-resource finders: fetch key
  // -> the fetch-address bucket keys that have stored an action with that key.
  // Lets the finders scan only the relevant buckets instead of every bucket
  // (run 480: full map x 128 actions/bucket under the registry mutex).
  // Entries can go stale when a bucket drops its last action with the key;
  // the finders treat that as a miss and the retirement sweep prunes them.
  std::unordered_map<uint64_t, std::vector<uint32_t>>
      texture_header_action_addresses_by_fetch_key;
  // Last-touch frame stamp per fetch-address bucket (store or lookup hit).
  // Title fetch addresses churn, so buckets untouched for the TTL are retired
  // by FableDirectRetireStalePrivateRegistrations; 0 = not yet stamped
  // (adopted into the TTL domain at the next sweep).
  std::unordered_map<uint32_t, uint32_t>
      texture_header_action_bucket_last_touch_frame;
  uint64_t texture_header_action_bucket_evictions = 0;
  uint64_t surface_texture_link_serial = 0;
  std::unordered_map<uint32_t, std::vector<FableSurfaceTextureLink>>
      surface_texture_links_by_texture;
  std::unordered_map<uint32_t, std::vector<FableSurfaceTextureLink>>
      surface_texture_links_by_surface;
  std::unordered_map<uint32_t, std::vector<FableSurfaceTextureLink>>
      surface_texture_links_by_base;
  std::unordered_map<uint64_t, std::vector<FableSurfaceTextureLink>>
      surface_texture_links_by_fetch_key;
  std::unordered_map<uint32_t, FableSurfaceTextureLink>
      surface_texture_link_by_destination_texture;
  std::unordered_map<uint64_t, FableSurfaceTextureLink>
      surface_texture_link_by_destination_texture_slot;
  std::unordered_map<uint32_t, std::vector<uint32_t>>
      surface_destination_texture_links_by_object;
  std::unordered_map<uint32_t, std::vector<uint32_t>>
      surface_destination_texture_links_by_base;
  std::unordered_map<uint64_t, std::vector<uint32_t>>
      surface_destination_texture_links_by_fetch_key;
  std::unordered_map<uint32_t, FableSurfaceTextureLink>
      surface_texture_exact_link_by_destination_texture;
  std::unordered_map<uint64_t, FableSurfaceTextureLink>
      surface_texture_exact_link_by_destination_texture_slot;
  std::unordered_map<uint32_t, std::vector<FableSurfaceTextureLink>>
      surface_texture_exact_links_by_destination_texture;
  std::unordered_map<uint64_t, std::vector<FableSurfaceTextureLink>>
      surface_texture_exact_links_by_destination_texture_slot;
  std::unordered_map<uint32_t, FableSurfaceTextureLink>
      surface_texture_link_by_source_surface;
  std::unordered_map<uint64_t, FableSurfaceTextureLink>
      surface_texture_link_by_source_surface_slot;
  std::unordered_map<uint32_t, FableSurfaceTextureLink>
      surface_texture_link_by_base;
  std::unordered_map<uint64_t, FableSurfaceTextureLink>
      surface_texture_link_by_fetch_key;
  std::unordered_map<uint64_t, FableSurfaceTextureLink>
      surface_texture_link_by_copy_call;
  std::unordered_map<uint64_t, FableSurfaceTextureLink>
      surface_texture_link_by_copy_context;
  // Monotone membership of every wrapper-identity key (copy_call_serial /
  // copy_context_key) ever STORED on a link (and thus on every relation link
  // copy, which is always derived from a stored link) in the tables above.
  // Both sample/action repair predicates REQUIRE an exact key match
  // (SurfaceTextureLinkMatchesSurfaceTextureLinkSample /
  // ...MatchesPrivateCopyActionWrapper), so a sample or action whose keys were
  // never stored cannot repair anything and its full-table walk is a proven
  // no-op. Run 510 (title): every new surface-view / private-copy link burst
  // paid the ~19-map walk under BOTH the observer and registry mutexes for a
  // freshly minted context key that could not match — those observer holds
  // were the ~200ms blocking waits in SyncDirectResourcesLocked's starved
  // retry (resource_ms=194-230 with a ~9ms accounted loop). Never pruned:
  // false positives after a retirement only re-run a walk, false negatives
  // would skip a real repair, and 8-byte keys grow by a few thousand per
  // title sit.
  std::unordered_set<uint64_t> surface_texture_link_copy_call_serials_seen;
  std::unordered_set<uint64_t> surface_texture_link_copy_context_keys_seen;
  uint64_t surface_destination_relation_serial = 0;
  // Bumped only when a relation store or repair actually changes stored
  // relation bytes; no-op re-stores of an identical relation leave it alone.
  // The per-draw destination-scan gate keys on this, not on the raw serial
  // above (which advances on every store attempt).
  uint64_t surface_destination_relation_generation = 0;
  std::unordered_map<uint32_t,
                     std::vector<FableSurfaceDestinationTextureRelation>>
      surface_destination_relations_by_source_surface;
  std::unordered_map<uint32_t,
                     std::vector<FableSurfaceDestinationTextureRelation>>
      surface_destination_relations_by_destination_texture;
  std::unordered_map<uint64_t,
                     std::vector<FableSurfaceDestinationTextureRelation>>
      surface_destination_relations_by_destination_texture_slot;
  std::unordered_map<uint32_t, FableSurfaceDestinationTextureRelation>
      surface_destination_relation_by_destination_texture;
  std::unordered_map<uint64_t, FableSurfaceDestinationTextureRelation>
      surface_destination_relation_by_destination_texture_slot;
  // Secondary index for the per-draw destination scan (same idiom as
  // texture_header_action_addresses_by_fetch_key): every address / fetch key
  // the scan's destination-endpoint resolution could match for a relation ->
  // the destination_texture bucket keys holding such relations. Holds bucket
  // KEYS, never relation copies; a stale entry is a wasted probe against a
  // vanished bucket, never a wrong result. Maintained at store and repair
  // (with store-time identity expansion), pruned by the private-registration
  // sweep, cleared/rebuilt alongside the relation maps above.
  std::unordered_map<uint32_t, std::vector<uint32_t>>
      surface_destination_relation_buckets_by_target_address;
  std::unordered_map<uint64_t, std::vector<uint32_t>>
      surface_destination_relation_buckets_by_target_fetch_key;
  uint64_t shader_identity_serial = 0;
  std::unordered_map<uint32_t, FableShaderIdentity>
      shader_identities_by_object;
  std::unordered_map<uint64_t, std::vector<uint32_t>>
      shader_identity_objects_by_hash;
  uint64_t vertex_declaration_identity_serial = 0;
  std::unordered_map<uint32_t, FableVertexDeclarationIdentity>
      vertex_declaration_identities_by_object;
  std::unordered_map<uint64_t, std::vector<uint32_t>>
      vertex_declaration_identity_objects_by_hash;
};

FableGuestResourceRegistry& FableDirectGuestResourceRegistry();
std::recursive_mutex& FableDirectGuestResourceRegistryMutex();
uint64_t FableDirectTextureFetchKey(
    const std::array<uint32_t, 6>& fetch_dwords);
void FableDirectRegisterTextureIdentity(
    const FableTextureIdentity& identity);
void FableDirectForgetTextureIdentity(uint32_t object_texture);
void FableDirectClearTextureIdentities();
// Migration step 6 shadow readout: observer identity registry vs
// gtl::QueryResourceIdentity. Logged periodically by the direct renderer.
void FableDirectLogGtlIdentityShadow();

const FableTextureIdentity* FableDirectFindTextureIdentityByObject(
    uint32_t object_texture);
const FableTextureIdentity* FableDirectFindTextureIdentityByBase(
    uint32_t base_address,
    std::source_location site = std::source_location::current());
// The defaulted source_location is call-site attribution for the identity
// census (see the .cpp): it names WHICH of the ~38 consumers a hit belongs to
// without editing a single call site. Nothing reads it outside the census, and
// it should come out with the census.
const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByBase(
    uint32_t base_address,
    std::source_location site = std::source_location::current());
const FableTextureIdentity* FableDirectFindTextureIdentityByFetch(
    const std::array<uint32_t, 6>& fetch_dwords,
    std::source_location site = std::source_location::current());
const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByFetch(
    const std::array<uint32_t, 6>& fetch_dwords,
    std::source_location site = std::source_location::current());
void FableDirectRegisterTextureFetchBindingAction(
    const FableTextureFetchBindingAction& action);
void FableDirectRegisterTextureHeaderAction(
    const FableTextureHeaderAction& action);
void FableDirectClearTextureHeaderActions();
const FableTextureHeaderAction* FableDirectFindTextureHeaderActionForFetch(
    uint32_t fetch_address, const std::array<uint32_t, 6>& fetch_dwords);
const FableTextureHeaderAction*
FableDirectFindTextureHeaderActionForExactResource(
    uint32_t texture_header, uint32_t texture_object, uint32_t parent_texture,
    uint32_t base_address, uint32_t byte_size, uint32_t width, uint32_t height,
    uint32_t format, const std::array<uint32_t, 6>& fetch_dwords);
const FableTextureHeaderAction*
FableDirectFindTextureHeaderActionForExactFetchIdentity(
    uint32_t base_address, uint32_t width, uint32_t height, uint32_t format,
    const std::array<uint32_t, 6>& fetch_dwords);
void FableDirectRegisterSurfaceTextureLink(
    const FableSurfaceTextureLink& link);
void FableDirectForgetSurfaceTextureLinksForResource(uint32_t resource);
void FableDirectClearSurfaceTextureLinks();
const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForDestinationTexture(uint32_t texture);
const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksForDestinationTexture(uint32_t texture);
const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot);
const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot);
const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForSourceSurface(uint32_t source_surface);
const FableSurfaceTextureLink*
FableDirectFindSurfaceTextureLinkForSourceSurfaceSlot(
    uint32_t source_surface, uint32_t source_slot);
const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForSourceSurface(
    uint32_t source_surface, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot);
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkForBase(
    uint32_t texture_base_address);
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkForFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords);
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByCopyCall(
    uint64_t copy_call_serial);
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByCopyContext(
    uint64_t copy_context_key);
const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForBase(
    uint32_t texture_base_address, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot);
const FableSurfaceTextureLink*
FableDirectFindVisibleSurfaceTextureLinkForFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords, uint32_t frame_index,
    uint64_t draw_serial, uint32_t source_slot);
const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByTexture(uint32_t texture);
const std::vector<FableSurfaceDestinationTextureRelation>*
FableDirectFindSurfaceDestinationRelationsBySourceSurface(
    uint32_t source_surface);
const FableSurfaceDestinationTextureRelation*
FableDirectFindSurfaceDestinationRelationForDestinationTexture(
    uint32_t destination_texture);
const FableSurfaceDestinationTextureRelation*
FableDirectFindSurfaceDestinationRelationForDestinationTextureSlot(
    uint32_t destination_texture, uint32_t source_slot);
const FableSurfaceDestinationTextureRelation*
FableDirectFindVisibleSurfaceDestinationRelationForDestinationTexture(
    uint32_t destination_texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot);
FableSurfaceDestinationRelationSnapshot
FableDirectGetSurfaceDestinationRelationSnapshot(
    size_t max_relations, uint32_t frame_index = UINT32_MAX,
    uint64_t draw_serial = 0, uint32_t source_slot = UINT32_MAX);
// O(1) read of the relation registry's content generation. Callers that would
// otherwise rebuild a whole snapshot just to discover nothing changed can gate
// on this instead -- taking the snapshot walks two registry maps, copies every
// visible relation twice and sorts the result. Unlike the raw relation serial,
// the generation only moves when a store/repair changed stored relation bytes,
// so per-draw link re-registrations that re-store identical relations do not
// defeat the gate.
uint64_t FableDirectGetSurfaceDestinationRelationGeneration();
// Targeted variant of the snapshot above: visits only the listed
// destination_texture buckets instead of the whole relation population, with
// identical visibility/preference/sort semantics. Callers obtain the bucket
// list from the target-address/fetch-key index via the finders below.
FableSurfaceDestinationRelationSnapshot
FableDirectGetSurfaceDestinationRelationSnapshotForBuckets(
    const std::vector<uint32_t>& destination_buckets, size_t max_relations,
    uint32_t frame_index = UINT32_MAX, uint64_t draw_serial = 0,
    uint32_t source_slot = UINT32_MAX);
// Probes into the surface_destination_relation_buckets_by_target_* index.
// Returned bucket keys may be stale (bucket since pruned); the targeted
// snapshot treats a missing bucket as empty, so staleness only wastes a probe.
const std::vector<uint32_t>*
FableDirectFindSurfaceDestinationRelationBucketsByTargetAddress(
    uint32_t address);
const std::vector<uint32_t>*
FableDirectFindSurfaceDestinationRelationBucketsByTargetFetchKey(
    uint64_t fetch_key);
// O(1) size of the by-destination relation population, for the targeted
// scan's small-population global fallback.
size_t FableDirectGetSurfaceDestinationRelationBucketCount();
const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksBySurface(uint32_t source_surface);
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByBase(
    uint32_t texture_base_address);
const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByBase(uint32_t texture_base_address);
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords);
const std::vector<FableSurfaceTextureLink>*
FableDirectFindSurfaceTextureLinksByFetch(
    const std::array<uint32_t, 6>& texture_fetch_dwords);
void FableDirectRegisterShaderIdentity(const FableShaderIdentity& identity);
void FableDirectForgetShaderIdentity(uint32_t object_shader);
void FableDirectClearShaderIdentities();
const FableShaderIdentity* FableDirectFindShaderIdentityByObject(
    uint32_t object_shader);
const std::vector<uint32_t>* FableDirectFindShaderIdentityObjectsByHash(
    uint64_t bytecode_hash);
void FableDirectRegisterVertexDeclarationIdentity(
    const FableVertexDeclarationIdentity& identity);
void FableDirectForgetVertexDeclarationIdentity(uint32_t object_declaration);
void FableDirectClearVertexDeclarationIdentities();
const FableVertexDeclarationIdentity*
FableDirectFindVertexDeclarationIdentityByObject(uint32_t object_declaration);
const std::vector<uint32_t>*
FableDirectFindVertexDeclarationIdentityObjectsByHash(uint64_t elements_hash);
void FableDirectRegisterPrivateTextureCopyAction(
    const FablePrivateTextureCopyAction& action);
// Batched form for the per-frame drain: registers every action, then runs
// ONE link-repair walk with per-link hash lookups instead of a full table
// walk per action (the per-action walk was the growing multi-second title
// stall, GDB-proven 2026-08-02).
void FableDirectRegisterPrivateTextureCopyActionsBatch(
    const std::vector<FablePrivateTextureCopyAction>& actions);
void FableDirectForgetPrivateTextureCopyActionsForResource(uint32_t resource);
// Batched form of the full forget recipe (texture identity + private copy
// actions + surface links) for the sync path's retirement drains (run 513).
// The per-address forgets walk every primary/derived registry index
// end-to-end PER ADDRESS (~60-85us each); this runs the walks ONCE with
// set-membership predicates — O(tables + batch). Caller contract: every
// address is being retired in the same sync call, and the flush runs before
// any re-registration for a batched address can sync.
void FableDirectForgetResourceRegistrationsBatch(
    const std::vector<uint32_t>& resources);
void FableDirectClearPrivateTextureCopyActions();
// Frame-TTL retirement for the private-rect action / surface-link
// registrations (run 473). Their keys are the same one-shot title panel
// header addresses the stale-RESOURCE sweep bounds, but most of them are
// never tracked resources, so destroy/evict-driven forgetting can never fire
// for them and the populations grow without bound. Retires every key whose
// NEWEST registration is older than the TTL; keys with any static
// (frame_index == 0) registration are exempt. Cheap when nothing is stale —
// call it on the stale-resource sweep cadence.
void FableDirectRetireStalePrivateRegistrations(uint32_t current_frame_index);

// Identity-backfill budget census (run 477). Window counters, consumed (and
// zeroed) once per compose for the compose lock census.
struct FablePrivateBackfillCensus {
  uint32_t inline_backfills = 0;   // ran at registration, within budget
  uint32_t deferred = 0;           // pushed to the carry-over queue
  uint32_t drained = 0;            // carry-over entries completed
  uint32_t dropped = 0;            // carry-over entries lost to the queue cap
  uint64_t actions_examined = 0;   // collected candidates (actions)
  uint64_t links_examined = 0;     // collected candidates (links)
  double backfill_ms = 0.0;        // total backfill wall time this window
  uint32_t pending = 0;            // carry-over queue depth at consume time
};
FablePrivateBackfillCensus FableDirectConsumePrivateBackfillCensus();

// Drains the identity-backfill carry-over queue within budget_ms and resets
// the per-frame inline backfill budget. Call once per compose, under or
// taking the registry mutex. Run 477: the eager per-registration backfill is
// O(registrations x bank) with millisecond constants — a transition frame
// after an idle TTL-swept menu re-registers everything first-sight and spent
// multi-second composes purely in backfill. The budget bounds any single
// frame's backfill work; the remainder completes here over following frames.
void FableDirectDrainPendingTextureIdentityBackfills(double budget_ms);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsBySource(uint32_t source_texture);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByTarget(uint32_t target_surface);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByBase(uint32_t source_base_address);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByFetch(
    const std::array<uint32_t, 6>& source_fetch_dwords);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByWrapperCall(uint64_t wrapper_call_serial);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionForDestinationTexture(uint32_t texture);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsForDestinationTexture(uint32_t texture);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot);
const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyActionForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot);
const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot, uint32_t frame_index,
    uint64_t draw_serial);
const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTexture(
    uint32_t texture, uint32_t frame_index, uint64_t draw_serial,
    uint32_t source_slot);
const FablePrivateTextureCopyAction*
FableDirectFindVisiblePrivateTextureCopyOutputActionForDestinationTextureSlot(
    uint32_t texture, uint32_t source_slot, uint32_t frame_index,
    uint64_t draw_serial);
const char* FablePrivateTextureCopyKindName(FablePrivateTextureCopyKind kind);
const char* FableRegisteredSampleSourceKindName(
    FableRegisteredSampleSourceKind kind);

}  // namespace refii::renderer
