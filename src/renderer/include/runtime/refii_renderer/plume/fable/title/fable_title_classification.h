#pragma once

#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"

#include <cstdint>

namespace refii::renderer {

struct FableTextureIdentity;

struct FableTitleResourceFacts {
  bool image_ready = false;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t base_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  bool texture_render_target = false;
  bool texture_depth_target = false;
  bool texture_upload_has_source = false;
  uint64_t upload_generation = 0;
  uint64_t texture_upload_hash = 0;
  uint64_t texture_upload_size = 0;
  uint32_t debug_first_nonzero_offset = 0;
  bool meaningful_upload = false;
  bool blank_uploaded_sample_proxy = false;
  bool sampleable_content = false;
  bool gpu_produced_content = false;
  uint64_t visible_content_serial = 0;
  uint64_t draw_content_serial = 0;
  uint64_t clear_content_serial = 0;
  bool texture_sample_content_for_exact_producer = false;
  const FableTextureIdentity* identity = nullptr;
};

struct FableTitleGuiAtlasSlotIdentityFacts {
  const FableTextureIdentity* identity = nullptr;
  bool fetch_valid = false;
  uint32_t fetch_base_address = 0;
  uint32_t fetch_width = 0;
  uint32_t fetch_height = 0;
  uint32_t fetch_format = 0;
  bool bound_image_resource = false;
  uint32_t bound_base_address = 0;
  uint32_t bound_width = 0;
  uint32_t bound_height = 0;
  uint32_t bound_format = 0;
};

struct FableTitleGuiTextAtlasCommandFacts {
  bool slot_in_range = false;
  bool scene_object_source = false;
  uint32_t slot = 0;
  uint32_t private_rect_source_slot = 0;
  uint32_t guest = 0;
  bool tiny_title_upload_identity = false;
  bool repeated_mesh_declaration = false;
  uint32_t primitive_type = 0;
  bool title_scene_target = false;
  bool exact_gui_text_atlas_identity = false;
  bool private_rect_declaration = false;
  uint32_t input_slot0_stride = 0;
  bool gui_text_atlas_object = false;
  uint64_t vertex_hash = 0;
  uint64_t pixel_hash = 0;
  uint32_t pixel_shader = 0;
  bool title_scene_vertex_shader_command = false;
};

struct FableTitleGuiAtlasCandidateFacts {
  bool image_ready = false;
  bool texture_depth_target = false;
  bool command_binds_candidate_as_target = false;
  bool command_fetch_valid = false;
  uint32_t command_guest = 0;
  uint32_t command_fetch_base_address = 0;
  uint32_t command_fetch_width = 0;
  uint32_t command_fetch_height = 0;
  uint32_t command_fetch_format = 0;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t base_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint64_t texture_upload_size = 0;
  uint64_t texture_upload_hash = 0;
  bool meaningful_upload = false;
  bool gui_text_atlas_object = false;
  bool title_gui_atlas_upload = false;
  const FableTextureIdentity* identity = nullptr;
  bool bound_texture_ready = false;
  uint32_t bound_guest_address = 0;
  uint32_t bound_parent_address = 0;
  uint32_t bound_base_address = 0;
};

struct FableTitleBillboardTileUploadIdentityFacts {
  bool command_fetch_valid = false;
  uint32_t command_guest = 0;
  uint32_t command_fetch_base_address = 0;
  uint32_t command_fetch_width = 0;
  uint32_t command_fetch_height = 0;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t base_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  bool authoritative_upload = false;
};

struct FableTinyTitleResourceFetchMatchFacts {
  bool fetch_valid = false;
  uint32_t fetch_base_address = 0;
  uint32_t fetch_width = 0;
  uint32_t fetch_height = 0;
  uint32_t fetch_format = 0;
  bool resource_image_ready = false;
  uint32_t resource_base_address = 0;
  uint32_t resource_width = 0;
  uint32_t resource_height = 0;
  uint32_t resource_guest_format = 0;
  uint32_t resource_render_format = 0;
};

enum class FablePrivateTextureProxyRole : uint8_t {
  kUnknown,
  kDepth,
  kColor
};

bool IsFableTitleSceneVertexShader(uint32_t shader);
bool IsFableTitleGuiSingleTexturePostprocessPixelShader(uint32_t shader,
                                                        uint64_t pixel_hash);
bool IsFableTitleGuiText3DPixelShaderAddress(uint32_t shader);

bool IsFableTitleTextureIndirectionChainAddress(uint32_t address);
bool IsFableTitleGuiTextAtlasObjectAddress(uint32_t address);
bool IsFableTitleTextureProbeAddress(uint32_t address);
bool IsFableTitleGuiTextAtlasBaseAddress(uint32_t address);
bool IsFableTitlePrivateTextureProxyAddress(uint32_t address);
bool IsFablePrivateTextureCopyProxyAddress(uint32_t address);
bool IsFableHeaderOnlyPrivateTextureProxyAddress(uint32_t address);
bool IsFableMutablePrivateTextureHeaderAddress(uint32_t address);
bool IsFableTinyTitleUploadedTextureAddress(uint32_t address);
bool IsFableTinyTitleUploadedTextureFilterAddress(uint32_t address);
bool IsFableExactTitleUploadedTextureFilterAddress(uint32_t address);
FablePrivateTextureProxyRole FablePrivateTextureProxyRoleForAddress(
    uint32_t address);

bool IsFableTitleBillboardAtlasUploadCarrierAddress(uint32_t address);
bool IsFableTitleBillboardAtlasUploadFilterAddress(uint32_t address);
bool IsFableTitleGuiTextAtlasUploadFilterAddress(uint32_t address);
bool IsFableTitleBillboardTileUploadBaseAddress(uint32_t base);
bool IsFableTitleBillboardTileUploadObjectAddress(uint32_t address);
bool IsFableTitleBillboardTileUploadHeaderAddress(uint32_t address);
uint32_t FableTitleBillboardTileHeaderAddressForObject(uint32_t object);
uint32_t FableTitleBillboardTileObjectAddressForHeader(uint32_t header);
uint32_t FableTitleBillboardTileObjectAddressForBase(uint32_t base);
bool IsFableTitleBillboardTileUploadAliasForBase(uint32_t address,
                                                 uint32_t base);
bool IsFableTitleBillboardTileUploadBase(uint32_t base, uint32_t width,
                                         uint32_t height);
bool IsFableTitleBillboardTileUploadShape(uint32_t base, uint32_t width,
                                          uint32_t height, uint32_t format,
                                          uint32_t byte_size);
bool IsFableTitleGuiAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot);
bool IsFableExactTinyTitleUploadSnapshot(
    const RendererTrackedResourceSnapshot& snapshot);
bool IsFableTitleBillboardAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot);
bool IsFableTitleIndirectionTextureBase(uint32_t base_address);
bool IsFableOneDimensionalTitleUploadedTextureBase(uint32_t base,
                                                   uint32_t width,
                                                   uint32_t height);
bool IsFableTitleUploadedTextureIdentityBase(uint32_t base, uint32_t width,
                                             uint32_t height);
bool IsFableTinyTitleUploadedTextureIdentityBase(uint32_t base, uint32_t width,
                                                 uint32_t height);
bool IsFableOneDimensionalTitleUploadedTextureIdentityBase(uint32_t base,
                                                           uint32_t width,
                                                           uint32_t height);
bool IsFableTitleUploadedTextureIdentity(
    const FableTextureIdentity* identity);
bool IsFableTinyTitleUploadedTextureIdentity(
    uint32_t guest, const FableTextureIdentity* identity);
uint32_t ExactFableTitleGuiTextAtlasUploadSize(uint32_t base, uint32_t width,
                                               uint32_t height,
                                               uint32_t format);
bool IsFableExactTitleGuiTextAtlasIdentity(
    const FableTextureIdentity* identity);
bool FableTitleCommandSlotHasExactGuiTextAtlasIdentity(
    const FableTitleGuiAtlasSlotIdentityFacts& facts);
bool FableTitleGuiTextAtlasCommand(
    const FableTitleGuiTextAtlasCommandFacts& facts);
uint32_t FableTitleGuiAtlasCandidateExactUploadSize(
    const FableTitleGuiAtlasCandidateFacts& facts);
bool FableTitleGuiAtlasCandidateCanBeExactCommandUpload(
    const FableTitleGuiAtlasCandidateFacts& facts);
bool FableTitleGuiAtlasCandidateIsExactCommandUpload(
    const FableTitleGuiAtlasCandidateFacts& facts);
bool FableTitleGuiAtlasCandidateMatchesIdentityOrBoundResource(
    const FableTitleGuiAtlasCandidateFacts& facts);
bool FableTitleResourceUploadDefinesTextureIdentity(
    const FableTitleResourceFacts& facts);
bool FableTitleResourceHasExactUploadIdentity(
    const FableTitleResourceFacts& facts);
bool FableTitleExactUploadedResourceHasAuthoredPayload(
    const FableTitleResourceFacts& facts);
bool FableTitleResourceHasTextureSampleContentForExactProducer(
    const FableTitleResourceFacts& facts);
bool FableTitleBillboardAtlasResource(const FableTitleResourceFacts& facts);
bool FableTitleBillboardTileHasAuthoritativeContent(
    const FableTitleResourceFacts& facts);
bool FableTitleBillboardAtlasHasAuthoritativeUpload(
    const FableTitleResourceFacts& facts);
bool FableTitleBillboardTileUploadAddressMatchesResourceBase(
    uint32_t address,
    uint32_t base,
    uint32_t guest_address,
    uint32_t parent_address);
bool FableTitleBillboardTileUploadIdentity(
    const FableTitleBillboardTileUploadIdentityFacts& facts);
bool FableTitleIndirectionHasAuthoritativeUpload(
    const FableTitleResourceFacts& facts);
bool FableTitleGuiAtlasResourceHasUpload(
    const FableTitleResourceFacts& facts);
bool FableTinyTitleCachedTextureHasUploadIdentity(
    uint32_t guest, const FableTextureIdentity* guest_identity,
    const FableTitleResourceFacts& facts);
bool FableTinyTitleResourceMatchesFetchInfo(
    const FableTinyTitleResourceFetchMatchFacts& facts);

}  // namespace refii::renderer
