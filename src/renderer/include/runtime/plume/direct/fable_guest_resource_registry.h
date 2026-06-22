#pragma once

#include "runtime/plume/direct/fable_observer_resources.h"

#include <array>
#include <cstdint>
#include <mutex>
#include <unordered_map>
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

struct FableTextureFetchResourceLink {
  uint64_t serial = 0;
  uint64_t binding_serial = 0;
  uint64_t binding_call_serial = 0;
  uint64_t binding_context_key = 0;
  uint64_t header_action_serial = 0;
  uint64_t header_context_key = 0;
  uint32_t texture_slot = 0xFFFFFFFFu;
  uint32_t texture_resource = 0;
  uint32_t parent_texture = 0;
  uint32_t base_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
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
  uint64_t upload_generation = 0;
  uint64_t content_serial = 0;
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
  uint64_t locks = 0;
  uint64_t unlocks = 0;
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
  std::unordered_map<uint32_t, std::vector<FablePrivateTextureCopyAction>>
      private_texture_copy_actions_by_frame;
  std::unordered_map<uint32_t, FableTextureIdentity>
      texture_identities_by_object;
  std::unordered_map<uint32_t, std::vector<uint32_t>>
      texture_identity_objects_by_base;
  std::unordered_map<uint64_t, std::vector<uint32_t>>
      texture_identity_objects_by_fetch_key;
  uint64_t texture_fetch_binding_serial = 0;
  std::unordered_map<uint64_t, std::vector<FableTextureFetchBindingAction>>
      texture_fetch_bindings_by_slot_fetch_key;
  uint64_t texture_fetch_resource_link_serial = 0;
  std::unordered_map<uint32_t, std::vector<FableTextureFetchResourceLink>>
      texture_fetch_resource_links_by_resource;
  std::unordered_map<uint64_t, std::vector<FableTextureFetchResourceLink>>
      texture_fetch_resource_links_by_slot_resource;
  std::unordered_map<uint32_t, std::vector<FableTextureFetchResourceLink>>
      texture_fetch_resource_links_by_base;
  std::unordered_map<uint64_t, std::vector<FableTextureFetchResourceLink>>
      texture_fetch_resource_links_by_fetch_key;
  std::unordered_map<uint64_t, std::vector<FableTextureFetchResourceLink>>
      texture_fetch_resource_links_by_slot_fetch_key;
  uint64_t texture_header_action_serial = 0;
  std::unordered_map<uint32_t, std::vector<FableTextureHeaderAction>>
      texture_header_actions_by_fetch_address;
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
  uint64_t surface_destination_relation_serial = 0;
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
const FableTextureIdentity* FableDirectFindTextureIdentityByObject(
    uint32_t object_texture);
const FableTextureIdentity* FableDirectFindTextureIdentityByBase(
    uint32_t base_address);
const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByBase(
    uint32_t base_address);
const FableTextureIdentity* FableDirectFindTextureIdentityByFetch(
    const std::array<uint32_t, 6>& fetch_dwords);
const std::vector<uint32_t>* FableDirectFindTextureIdentityObjectsByFetch(
    const std::array<uint32_t, 6>& fetch_dwords);
void FableDirectRegisterTextureFetchBindingAction(
    const FableTextureFetchBindingAction& action);
void FableDirectClearTextureFetchBindingActions();
const FableTextureFetchBindingAction*
FableDirectFindTextureFetchBindingActionForSlotFetch(
    uint32_t texture_slot, const std::array<uint32_t, 6>& fetch_dwords);
const FableTextureFetchBindingAction*
FableDirectFindTextureFetchBindingActionForSlotFetchSymbol(
    uint32_t texture_slot, const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t symbol_address);
void FableDirectRegisterTextureFetchResourceLink(
    const FableTextureFetchResourceLink& link);
void FableDirectForgetTextureFetchResourceLinksForResource(uint32_t resource);
void FableDirectClearTextureFetchResourceLinks();
const FableTextureFetchResourceLink*
FableDirectFindVisibleTextureFetchResourceLinkForResourceSlot(
    uint32_t resource, uint32_t texture_slot, uint32_t frame_index,
    uint64_t draw_serial);
const FableTextureFetchResourceLink*
FableDirectFindVisibleTextureFetchResourceLinkForSlotFetch(
    uint32_t texture_slot, const std::array<uint32_t, 6>& fetch_dwords,
    uint32_t frame_index, uint64_t draw_serial);
const FableTextureFetchResourceLink*
FableDirectFindVisibleTextureFetchResourceLinkForBase(
    uint32_t base_address, uint32_t texture_slot, uint32_t frame_index,
    uint64_t draw_serial);
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
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkByTexture(
    uint32_t texture);
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
const FableSurfaceTextureLink* FableDirectFindSurfaceTextureLinkBySurface(
    uint32_t source_surface);
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
const FableShaderIdentity* FableDirectFindShaderIdentityByHash(
    uint64_t bytecode_hash);
const std::vector<uint32_t>* FableDirectFindShaderIdentityObjectsByHash(
    uint64_t bytecode_hash);
void FableDirectRegisterVertexDeclarationIdentity(
    const FableVertexDeclarationIdentity& identity);
void FableDirectForgetVertexDeclarationIdentity(uint32_t object_declaration);
void FableDirectClearVertexDeclarationIdentities();
const FableVertexDeclarationIdentity*
FableDirectFindVertexDeclarationIdentityByObject(uint32_t object_declaration);
const FableVertexDeclarationIdentity*
FableDirectFindVertexDeclarationIdentityByHash(uint64_t elements_hash);
const std::vector<uint32_t>*
FableDirectFindVertexDeclarationIdentityObjectsByHash(uint64_t elements_hash);
void FableDirectRegisterPrivateTextureCopyAction(
    const FablePrivateTextureCopyAction& action);
void FableDirectForgetPrivateTextureCopyActionsForResource(uint32_t resource);
void FableDirectClearPrivateTextureCopyActions();
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionBySource(uint32_t source_texture);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsBySource(uint32_t source_texture);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByTarget(uint32_t target_surface);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByTarget(uint32_t target_surface);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByBase(uint32_t source_base_address);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByBase(uint32_t source_base_address);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByFetch(
    const std::array<uint32_t, 6>& source_fetch_dwords);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByFetch(
    const std::array<uint32_t, 6>& source_fetch_dwords);
const std::vector<FablePrivateTextureCopyAction>*
FableDirectFindPrivateTextureCopyActionsByFrame(uint32_t frame_index);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByWrapperCall(uint64_t wrapper_call_serial);
const FablePrivateTextureCopyAction*
FableDirectFindPrivateTextureCopyActionByWrapperContext(
    uint64_t wrapper_context_key);
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
