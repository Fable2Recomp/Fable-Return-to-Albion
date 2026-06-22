#pragma once

#include "runtime/plume/direct/fable_render_types.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <vector>

namespace refii::renderer {

struct FableDirectRenderTargets {
  std::array<uint32_t, 4> color = {};
  uint32_t depth = 0;
};

struct FableDirectStateRoot {
  uint64_t call_serial = 0;
  uint64_t context_key = 0;
  uint32_t symbol_address = 0;
  uint32_t lr = 0;
};

struct FableDirectRenderTargetRoots {
  std::array<FableDirectStateRoot, 4> color = {};
  FableDirectStateRoot depth;
};

struct FableDirectShaderState {
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
};

struct FableDirectShaderRoots {
  FableDirectStateRoot vertex_shader;
  FableDirectStateRoot pixel_shader;
};

struct FableDirectInputState {
  std::array<RendererStreamBindingSnapshot, 16> streams = {};
  uint32_t index_buffer = 0;
};

struct FableDirectTextureState {
  std::array<uint32_t, kRendererD3DTextureSlots> textures = {};
  std::array<uint32_t, kRendererD3DSamplerStateSlots> samplers = {};
};

struct FableDirectConstantState {
  std::array<uint32_t, kRendererFetchConstantDwords> fetch = {};
  std::array<uint32_t, kRendererShaderFloatConstantDwords> vertex_float = {};
  std::array<uint32_t, kRendererShaderFloatConstantDwords> pixel_float = {};
  std::array<uint32_t, kRendererShaderIntConstantDwords> vertex_int = {};
  std::array<uint32_t, kRendererShaderIntConstantDwords> pixel_int = {};
  uint32_t vertex_int_valid_mask = 0;
  uint32_t pixel_int_valid_mask = 0;
  std::array<uint32_t, kRendererShaderBoolConstantDwords> vertex_bool = {};
  std::array<uint32_t, kRendererShaderBoolConstantDwords> pixel_bool = {};
};

struct FableDirectPm4State {
  bool valid = false;
  uint32_t rb_surface_info = 0;
  std::array<uint32_t, 4> rb_color_info = {};
  uint32_t rb_depth_info = 0;
  uint32_t rb_copy_control = 0;
  uint32_t rb_copy_dest_base = 0;
  uint32_t rb_copy_dest_pitch = 0;
  uint32_t rb_copy_dest_info = 0;
  uint32_t rb_copy_func = 0;
  uint32_t rb_copy_ref = 0;
  uint32_t rb_copy_mask = 0;
  uint32_t rb_copy_surface_slice = 0;
  uint32_t rb_modecontrol = 4u;
  uint32_t rb_depthcontrol = 0;
  uint32_t rb_color_mask = 0xFFFFu;
  std::array<uint32_t, 4> rb_blendcontrol = {
      0x00010001u, 0x00010001u, 0x00010001u, 0x00010001u};
  uint32_t rb_colorcontrol = 0;
  uint32_t rb_alpha_ref = 0;
  uint32_t rb_stencilrefmask = 0x00FFFF00u;
  uint32_t rb_stencilrefmask_bf = 0x00FFFF00u;
  uint32_t pa_su_sc_mode_cntl = 0;
};

struct FableDirectPipelineState {
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};
  FableDirectPm4State pm4;
};

enum class FableDirectPm4Field : uint8_t {
  kSurfaceInfo,
  kColorInfo0,
  kColorInfo1,
  kColorInfo2,
  kColorInfo3,
  kDepthInfo,
  kCopyControl,
  kCopyDestBase,
  kCopyDestPitch,
  kCopyDestInfo,
  kCopyFunc,
  kCopyRef,
  kCopyMask,
  kCopySurfaceSlice,
  kModeControl,
  kDepthControl,
  kColorMask,
  kBlendControl0,
  kBlendControl1,
  kBlendControl2,
  kBlendControl3,
  kColorControl,
  kAlphaRef,
  kStencilRefMask,
  kStencilRefMaskBackFace,
  kPaSuScModeCntl,
};

struct FableDirectDirtyState {
  bool targets = true;
  bool shaders = true;
  bool input = true;
  bool textures = true;
  bool constants = true;
  bool pm4 = true;
  bool viewport = true;
  bool scissor = true;
  bool pipeline = true;
};

enum class FableDirectRenderCommandType : uint8_t {
  kSetRenderTarget,
  kSetDepthStencil,
  kSetTexture,
  kSetStreamSource,
  kSetIndices,
  kSetVertexDeclaration,
  kSetVertexShader,
  kSetPixelShader,
  kSetViewport,
  kSetScissor,
  kSetScaler,
  kSetRenderState,
  kSetSamplerState,
  kSetPm4Field,
  kClear,
  kStateFlush,
  kDrawPrimitive,
  kDrawIndexedPrimitive,
  kDrawPrimitiveUP,
  kResolveCopy,
};

constexpr size_t kFableDirectRenderCommandTypeCount =
    static_cast<size_t>(FableDirectRenderCommandType::kResolveCopy) + 1u;

struct FableDirectResourceBindings {
  std::array<uint32_t, 4> render_targets = {};
  uint32_t depth_stencil = 0;
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  FableDirectShaderRoots shader_roots;
  uint32_t index_buffer = 0;
  std::array<uint32_t, kRendererD3DTextureSlots> textures = {};
  std::array<RendererStreamBindingSnapshot, 16> streams = {};
};

struct FableDirectClearCommand {
  uint32_t flags = 0;
  std::array<float, 4> color = {0.0f, 0.0f, 0.0f, 1.0f};
  float depth = 1.0f;
  uint32_t stencil = 0;
  RendererScissorSnapshot rect;
  uint64_t root_call_serial = 0;
  uint64_t root_context_key = 0;
  uint32_t root_symbol_address = 0;
  uint32_t root_lr = 0;
  FableDirectRenderTargetRoots target_roots;
};

struct FableDirectDrawCommand {
  RendererLastDrawSnapshot draw;
  RendererDrawEventSource source = RendererDrawEventSource::kUnknown;
  bool draw_vertices_up = false;
  bool private_texture_rectangle = false;
  uint32_t private_texture_slot = 0;
  float private_texture_source_x = 0.0f;
  float private_texture_source_y = 0.0f;
  bool private_texture_corners_valid = false;
  std::array<float, 6> private_texture_corners = {};
  bool private_texture_uv_override = false;
  float private_texture_u0 = 0.0f;
  float private_texture_v0 = 0.0f;
  float private_texture_u1 = 1.0f;
  float private_texture_v1 = 1.0f;
  uint64_t private_texture_wrapper_call_serial = 0;
  uint64_t private_texture_wrapper_context_key = 0;
  uint32_t private_texture_wrapper_symbol_address = 0;
  uint32_t private_texture_wrapper_source_texture = 0;
  bool private_texture_wrapper_source_exact = false;
  uint32_t state_flush_lr = 0;
};

struct FableDirectResolveCommand {
  uint32_t rb_surface_info = 0;
  std::array<uint32_t, 4> rb_color_info = {};
  uint32_t rb_depth_info = 0;
  uint32_t rb_copy_control = 0;
  uint32_t rb_copy_dest_base = 0;
  uint32_t rb_copy_dest_pitch = 0;
  uint32_t rb_copy_dest_info = 0;
  uint32_t rb_copy_func = 0;
  uint32_t rb_copy_ref = 0;
  uint32_t rb_copy_mask = 0;
  uint32_t rb_copy_surface_slice = 0;
};

struct FableDirectInlineBuffer {
  uint32_t address = 0;
  uint32_t byte_size = 0;
  uint32_t stride_or_format = 0;
  std::vector<uint8_t> data;

  bool ready() const {
    return address != 0 && byte_size != 0 && data.size() >= byte_size;
  }
};

struct FableDirectRenderCommand {
  uint64_t serial = 0;
  uint64_t draw_serial = 0;
  uint64_t resource_serial = 0;
  FableDirectRenderCommandType type =
      FableDirectRenderCommandType::kSetRenderTarget;
  uint32_t device = 0;
  uint32_t slot = 0;
  uint32_t value = 0;
  uint32_t value2 = 0;
  FableDirectStateRoot state_root;
  RendererStreamBindingSnapshot stream;
  RendererViewportSnapshot viewport;
  RendererScissorSnapshot scissor;
  RendererScalerSnapshot scaler;
  FableDirectResourceBindings resources;
  FableDirectPipelineState pipeline;
  std::array<uint32_t, kRendererD3DSamplerStateSlots> sampler_states = {};
  FableDirectConstantState constants;
  FableDirectClearCommand clear;
  FableDirectDrawCommand draw;
  FableDirectResolveCommand resolve;
  std::array<FableDirectInlineBuffer, 16> inline_vertex_buffers = {};
  FableDirectInlineBuffer inline_index_buffer;
  std::array<RendererInlineTextureUploadSnapshot, 3> pix_video_planes = {};
};

bool DirectCommandIsExactClearAction(
    const FableDirectRenderCommand& command);

struct FableDirectResourceReference {
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t data_address = 0;
  uint32_t auxiliary_address = 0;
  uint32_t byte_size = 0;
  bool buffer_resource = false;
};

struct FableDirectStateSnapshot {
  uint64_t serial = 0;
  uint64_t draw_event_count = 0;
  uint64_t direct_event_count = 0;
  uint64_t render_command_count = 0;
  uint64_t pending_state_command_count = 0;
  uint64_t pending_clear_command_count = 0;
  uint64_t pending_resolve_command_count = 0;
  std::array<uint32_t, 10> pending_draw_source_counts = {};
  std::array<uint32_t, kFableDirectRenderCommandTypeCount>
      pending_command_type_counts = {};
  std::array<uint32_t, kFableDirectRenderCommandTypeCount>
      pending_rooted_command_type_counts = {};
  std::array<uint32_t, kFableDirectRenderCommandTypeCount>
      pending_framebuffer_rooted_command_type_counts = {};
  uint64_t submitted_render_command_count = 0;
  uint64_t submitted_render_command_serial = 0;
  uint64_t command_boundary_capture_count = 0;
  uint64_t empty_command_boundary_capture_count = 0;
  uint64_t last_command_boundary_submit = 0;
  uint64_t last_command_boundary_pending_commands = 0;
  uint64_t last_command_boundary_pending_draws = 0;
  uint64_t last_submitted_draw_event_count = 0;
  uint64_t dropped_render_commands = 0;
  uint64_t consumed_exact_state_commands = 0;
  uint64_t draw_count = 0;
  uint64_t indexed_draw_count = 0;
  uint64_t state_flush_count = 0;
  uint32_t last_device = 0;
  FableDirectRenderTargets targets;
  FableDirectRenderTargetRoots target_roots;
  FableDirectShaderState shaders;
  FableDirectShaderRoots shader_roots;
  FableDirectInputState input;
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};
  FableDirectTextureState textures;
  FableDirectConstantState constants;
  FableDirectPm4State pm4;
  RendererViewportSnapshot viewport;
  RendererScissorSnapshot scissor;
  RendererLastDrawSnapshot last_draw;
  RendererScalerSnapshot scaler;
  uint32_t clear_flags = 0;
  FableDirectDirtyState dirty;
};

void ResetFableDirectState();

void FableDirectSetRenderTarget(uint32_t device, uint32_t slot,
                                uint32_t surface,
                                FableDirectStateRoot root = {});
void FableDirectSetDepthStencil(uint32_t device, uint32_t surface,
                                FableDirectStateRoot root = {});
void FableDirectMirrorRenderTargetState(uint32_t device, uint32_t slot,
                                        uint32_t surface,
                                        FableDirectStateRoot root = {});
void FableDirectMirrorDepthStencilState(uint32_t device, uint32_t surface,
                                        FableDirectStateRoot root = {});
void FableDirectSetTexture(uint32_t device, uint32_t slot, uint32_t texture,
                           FableDirectStateRoot root = {});
void FableDirectSetStream(uint32_t device, uint32_t slot,
                          const RendererStreamBindingSnapshot& stream,
                          FableDirectStateRoot root = {});
void FableDirectSetIndexBuffer(uint32_t device, uint32_t index_buffer,
                               FableDirectStateRoot root = {});
void FableDirectSetVertexDeclaration(uint32_t device,
                                     uint32_t vertex_declaration,
                                     FableDirectStateRoot root = {});
void FableDirectSetVertexShader(uint32_t device, uint32_t vertex_shader,
                                FableDirectStateRoot root = {});
void FableDirectSetPixelShader(uint32_t device, uint32_t pixel_shader,
                               FableDirectStateRoot root = {});
void FableDirectMirrorTextureState(uint32_t device, uint32_t slot,
                                   uint32_t texture);
void FableDirectMirrorStreamState(
    uint32_t device, uint32_t slot,
    const RendererStreamBindingSnapshot& stream);
void FableDirectMirrorIndexBufferState(uint32_t device, uint32_t index_buffer);
void FableDirectMirrorVertexDeclarationState(uint32_t device,
                                             uint32_t vertex_declaration);
void FableDirectMirrorVertexShaderState(uint32_t device,
                                        uint32_t vertex_shader);
void FableDirectMirrorPixelShaderState(uint32_t device, uint32_t pixel_shader);
void FableDirectSetViewport(uint32_t device,
                            const RendererViewportSnapshot& viewport,
                            FableDirectStateRoot root = {});
void FableDirectSetScissor(uint32_t device,
                           const RendererScissorSnapshot& scissor,
                           FableDirectStateRoot root = {});
void FableDirectMirrorViewportState(uint32_t device,
                                    const RendererViewportSnapshot& viewport);
void FableDirectMirrorScissorState(uint32_t device,
                                   const RendererScissorSnapshot& scissor);
void FableDirectSetScaler(uint32_t device,
                          const RendererScalerSnapshot& scaler);
void FableDirectSetRenderState(uint32_t device, uint32_t slot, uint32_t value,
                               FableDirectStateRoot root = {});
void FableDirectSetRenderStateSnapshot(
    const std::array<uint32_t, kRendererD3DRenderStateSlots>& states);
uint32_t FableDirectGetRenderState(uint32_t slot);
void FableDirectSetPackedSamplerState(uint32_t device, uint32_t slot,
                                      uint32_t packed_state,
                                      FableDirectStateRoot root = {});
void FableDirectSetPackedSamplerStateSnapshot(
    const std::array<uint32_t, kRendererD3DSamplerStateSlots>& states);
uint32_t FableDirectGetPackedSamplerState(uint32_t slot);
uint32_t FableDirectStorePackedSamplerState(uint32_t device, uint32_t slot,
                                            uint32_t state_slot,
                                            uint32_t value,
                                            FableDirectStateRoot root = {});
void FableDirectSetFetchConstant(uint32_t dword, uint32_t value);
void FableDirectSetFetchConstantRange(uint32_t first_dword,
                                      const uint32_t* values,
                                      uint32_t dword_count);
void FableDirectSetShaderFloatConstant(uint32_t pixel_bank, uint32_t dword,
                                       uint32_t value);
void FableDirectSetShaderFloatConstantRange(uint32_t pixel_bank,
                                            uint32_t first_dword,
                                            const uint32_t* values,
                                            uint32_t dword_count);
void FableDirectSetShaderFloatConstantsSnapshot(
    uint32_t pixel_bank,
    const std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants);
void FableDirectSetShaderIntConstantRange(uint32_t pixel_bank,
                                          uint32_t first_dword,
                                          const uint32_t* values,
                                          uint32_t dword_count);
void FableDirectSetShaderIntValidMask(uint32_t pixel_bank,
                                      uint32_t valid_mask);
void FableDirectSetShaderBoolConstantsSnapshot(
    uint32_t pixel_bank,
    const std::array<uint32_t, kRendererShaderBoolConstantDwords>& constants);
void FableDirectSetPm4Field(FableDirectPm4Field field, uint32_t value,
                            FableDirectStateRoot root = {});
void FableDirectMirrorPm4Field(FableDirectPm4Field field, uint32_t value);
void FableDirectRecordStateFlush(uint32_t device, uint32_t lr,
                                 FableDirectStateRoot root = {});
RendererDrawEventSnapshot FableDirectBuildEvent(RendererDrawEventSource source);
RendererDrawEventSnapshot FableDirectBuildDrawEvent(
    RendererDrawEventSource source, const RendererLastDrawSnapshot& draw,
    bool draw_vertices_up);
void FableDirectApplyStateSnapshotToDrawEvent(
    const FableDirectStateSnapshot& snapshot, RendererDrawEventSnapshot& event,
    bool include_targets);
void FableDirectApplyDrawEventStatePatch(
    RendererDrawEventSnapshot& event,
    const RendererDrawEventSnapshot& patch);
void FableDirectApplyWrappedDrawEventStatePatch(
    RendererDrawEventSnapshot& event,
    const RendererDrawEventSnapshot& patch);
RendererDrawEventSnapshot FableDirectRecordDrawEvent(
    RendererDrawEventSnapshot event);
bool FableDirectPatchDrawEvent(
    uint64_t serial,
    const std::function<bool(RendererDrawEventSnapshot&)>& patch);
bool FableDirectRemoveDrawEvent(uint64_t serial);
bool FableDirectRemoveSupersededScratchDrawEvent(uint64_t serial,
                                                 uint32_t render_target);
size_t FableDirectRemoveSupersededScratchDrawEventsForTarget(
    uint32_t render_target);
size_t FableDirectPatchPendingDrawEvents(
    const std::function<bool(RendererDrawEventSnapshot&)>& patch);
size_t FableDirectPendingDrawEventCount();
bool FableDirectAttachDrawScratchBuffer(uint64_t draw_serial,
                                        bool index_buffer, uint32_t address,
                                        uint32_t stride_or_format,
                                        const void* data, size_t size);
void FableDirectAttachScratchBufferByAddress(bool index_buffer,
                                             uint32_t address,
                                             uint32_t stride_or_format,
                                             const void* data, size_t size);
bool FableDirectAttachCachedScratchBuffers(FableDirectRenderCommand& command);
bool FableDirectIsScratchByteBufferRange(uint32_t guest_address,
                                         uint32_t size);
void FableDirectCaptureSubmittedRenderCommands(uint64_t submit,
                                               bool publish = false);
bool FableDirectCaptureExactNonVideoRenderCommands(uint64_t submit);
bool FableDirectPublishStagedSubmittedRenderCommandsForNonVideo();
uint32_t FableDirectPruneExactFramebufferStateCommands();
uint32_t FableDirectPruneExactRootedStateCommands();
uint32_t FableDirectPruneStateFlushCommands();
uint32_t FableDirectPruneStateOnlyCommands();
bool FableDirectDrawEventsReferenceResource(
    const FableDirectResourceReference& resource);

FableDirectStateSnapshot GetFableDirectStateSnapshot();
bool TryGetFableDirectStateSnapshot(FableDirectStateSnapshot& snapshot);
std::vector<FableDirectRenderCommand> FableDirectPendingRenderCommandSnapshot(
    size_t max_commands);
std::vector<FableDirectRenderCommand> FableDirectQueuedRenderCommandSnapshot(
    size_t max_commands);
std::vector<FableDirectRenderCommand> ConsumeFableDirectRenderCommands();
bool FableDirectSubmittedRenderCommandsAreStronger(uint32_t current_candidates,
                                                   size_t current_size);
std::vector<FableDirectRenderCommand>
ConsumeFableDirectSubmittedRenderCommands();

}  // namespace refii::renderer
