#pragma once

#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"

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
  // LIVE RB_DEPTHCONTROL read from the device at the draw root (dev+0x2934).
  // This is the guest's own resolved hardware word, not a reconstruction: IDA
  // 2026-08-02 shows every depth/stencil render-state setter writing straight
  // into it -- SetRenderState_ZFunc `insrwi r11,r4,3,25` (bits 4-6),
  // _ZEnable `insrwi r11,r4,1,30` (bit 1), _ZWriteEnable `insrwi r11,r4,1,29`
  // (bit 2), _StencilEnable `insrwi r11,r4,1,31` (bit 0) -- i.e. canonical
  // Xenos layout. Preferred over the render-state-slot derivation, which is a
  // lossy second reconstruction of the same data (it starts all-zero, is
  // gated on the valid mask, and yields ZFUNC=0=NEVER for any state the guest
  // never dispatches -- the black loading screen / black world, runs 520-530).
  // It also inherits for free the guest's own gating: _ZEnable/_StencilEnable
  // force their bit to 0 when no depth surface is bound (they test dev+0x30A8
  // first).
  uint32_t live_depth_control = 0;
  bool live_depth_control_valid = false;
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
  // GTL ResolvePacket executed in-stream (§3b ownership): the ordered merge
  // inserts one of these at the resolve's packet_id position between the GTL
  // vertex draws, and the executor runs the host copy (formats agree) or the
  // format-converting resolve draw. Carries only gtl_resolve_packet_index into
  // the frame's drained resolve-packet vector.
  kGtlResolve,
  // GTL TessellatedPatchDrawPacket executed in-stream (T2/T3, 2026-08-02):
  // the ordered merge inserts one at the packet's packet_id position exactly
  // like kGtlResolve. The executor decodes the tessellation factor N from the
  // captured VGT register block, binds the TESS-VARIANT vertex shader
  // (`<stem>.tess` artifact), and issues drawIndexedInstanced(6*N*N indices
  // over a cached (N+1)x(N+1) grid IB, patch_count instances). No vertex
  // streams are bound — the guest binds none; geometry is generated in-shader
  // from vertex textures s16-s19 + constants.
  kGtlTessDraw,
};

constexpr size_t kFableDirectRenderCommandTypeCount =
    static_cast<size_t>(FableDirectRenderCommandType::kGtlTessDraw) + 1u;

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
  uint32_t vertex_or_index_count = 0;
  uint32_t index_format = 0;
  int32_t base_vertex = 0;
  bool base_vertex_valid = false;
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
  uint32_t refii_source_surface = 0;
  uint32_t refii_source_slot = UINT32_MAX;
  bool refii_source_depth = false;
};

struct FableDirectInlineBuffer {
  uint32_t address = 0;
  uint32_t byte_size = 0;
  uint32_t stride_or_format = 0;
  // Attached per draw serial (the draw's OWN payload by construction) — the
  // bind may serve it from base even when the stream pointer has drifted
  // outside the payload's address range (long compose bursts).
  bool pinned_per_draw = false;
  std::vector<uint8_t> data;
  // DirectHashBytes(data[0..byte_size)), stamped where the payload bytes are
  // WRITTEN (StoreInlineScratchBuffer, the GTL authority fills) so bind-time
  // upload-cache key derivation never re-hashes an unchanged payload (the old
  // serial-keyed bind memo could never hit — command serials are fresh on
  // every re-record). 0 = not computed; binds hash lazily and census it. The
  // hash must travel WITH the bytes: copy it wherever data is copied
  // (RefiiInlineBufferPayload round-trip included) and reset it to 0 at any
  // site that mutates data without restamping.
  uint64_t content_hash = 0;

  bool ready() const {
    return address != 0 && byte_size != 0 && data.size() >= byte_size;
  }
};

// kGtlTessDraw payload — the compact slice of TessellatedPatchDrawPacket the
// executor needs, BY VALUE for the same reason kGtlResolve carries its packet
// (commands can be held/staged across frames, so indices into per-frame
// drained vectors would dangle). Shaders/targets/textures/constants ride the
// command's ordinary resources/constants fields; this struct carries only the
// tessellation-specific state.
struct FableDirectGtlTessDrawCommand {
  uint32_t source_symbol = 0;   // 0x82207AF8 terrain / 0x8221C898 water
  uint32_t caller_lr = 0;
  bool indexed = false;         // terrain true (per-edge factor DMA), water false
  // Raw call args (terrain r6/r7, water r5/r6 — see TessellatedPatchDrawPacket).
  uint32_t arg_index_offset = 0;
  uint32_t arg_count = 0;
  // First decoded VGT_DRAW_INITIATOR + how many DRAW_INDX packets the call
  // wrote (the emitters chunk at 0xFFFE). 0 when the ring capture failed.
  uint32_t draw_initiator = 0;
  uint32_t draw_packet_count = 0;
  // The VGT tessellation register block (regs 0x2280+i; [5] HOS_CNTL, [6]/[7]
  // HOS_MAX/MIN_TESS_LEVEL as raw float bit patterns, [10]/[11] GROUP decr).
  std::array<uint32_t, 21> vgt_tess_regs = {};
  bool vgt_regs_valid = false;
  // VGT_INDX_OFFSET (reg 0x2102, pa_vgt_regs[2]) — the hardware adds this to
  // every auto-generated index; the tess-variant VS receives it via
  // SharedConstants +564 and adds it to SV_InstanceID (T1 contract).
  uint32_t vgt_indx_offset = 0;
  // GPU predication state at the call (dev+0x2AB4 / 0x31AC / 0x31A4).
  uint32_t predication_mask = 0;
  bool predication_enabled = false;
  uint32_t predication_bin_select = 0;
  bool tiled_variant = false;
};

struct FableDirectRenderCommand {
  uint64_t serial = 0;
  uint64_t draw_serial = 0;
  uint64_t resource_serial = 0;
  // Which SEALED guest-frame publish
  // (PublishStagedSubmittedRenderCommandsLocked) this command rode to the
  // submitted queue; commands sharing a generation belong to one sealed guest
  // frame, and a capture that finds several generations queued is holding
  // superseded-frame backlog (run 474). 0 = pre-publish OR an immediate
  // producer segment (PIX/Bink, synthetic recovery — run 475): never
  // superseded, never superseding.
  uint32_t publish_generation = 0;
  FableDirectRenderCommandType type =
      FableDirectRenderCommandType::kSetRenderTarget;
  uint32_t device = 0;
  uint32_t slot = 0;
  uint32_t value = 0;
  uint32_t value2 = 0;
  FableDirectStateRoot state_root;
  RendererStreamBindingSnapshot stream;
  RendererViewportSnapshot viewport;
  // GTL-submission commands carry the RAW guest viewport/scissor (native 1280x720
  // space); the observer command builder pre-scales to output. When true, the
  // executor scales the viewport/scissor from guest-native space to the output
  // framebuffer (the guest->host translation the design keeps in the executor,
  // not per-source). False for observer-built commands (already output-space).
  bool viewport_guest_space = false;
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
  // Number of exact non-video capture boundaries this command has failed the
  // exact-identity gate at. Draws wait in staging for asynchronous payload
  // patches; commands that never become exact are dropped after a bounded
  // number of attempts so they cannot block exact sibling draws forever.
  uint32_t exact_capture_attempts = 0;
  // kGtlResolve only: the ResolvePacket this command executes, BY VALUE —
  // commands can be held/staged/replayed across frames, so an index into the
  // per-frame drained packet vector would dangle. ~150 bytes next to the
  // constants banks this struct already carries.
  refii::gtl::ResolvePacket gtl_resolve;
  bool gtl_resolve_valid = false;
  // kGtlTessDraw only (see FableDirectGtlTessDrawCommand).
  FableDirectGtlTessDrawCommand gtl_tess;
  bool gtl_tess_valid = false;
  // Built by BuildDirectCommandFromGtlDrawPacketLocked: the packet IS this
  // command's source (identity, banks, geometry all copied at build), so the
  // record-time packet-identity authority pass — a linear identity scan over
  // the whole shadow-packet backlog per draw, with per-slot identity-map
  // lookups — is redundant for it and skipped. Observer-built commands keep
  // the pass (their constants can be a frame stale; the packet banks repair
  // them).
  bool gtl_packet_sourced = false;
  // Dropped by a supersession / record-backlog-cap pass. Marked instead of
  // erased: erase/remove_if compaction of ~12.6KB elements is an O(N)
  // memmove per drop event (the run-321 storm class). Marked commands are
  // swept by the record path's single existing strip compaction and are
  // skipped by the frame-stats snapshot and the scratch-wrapper shader
  // normalizer in between.
  bool record_dropped = false;
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
  uint64_t staged_state_command_count = 0;
  uint64_t staged_clear_command_count = 0;
  uint64_t staged_resolve_command_count = 0;
  std::array<uint32_t, 10> staged_draw_source_counts = {};
  std::array<uint32_t, kFableDirectRenderCommandTypeCount>
      staged_command_type_counts = {};
  std::array<uint32_t, kFableDirectRenderCommandTypeCount>
      staged_rooted_command_type_counts = {};
  std::array<uint32_t, kFableDirectRenderCommandTypeCount>
      staged_framebuffer_rooted_command_type_counts = {};
  uint64_t submitted_render_command_count = 0;
  uint64_t submitted_render_command_serial = 0;
  uint64_t command_boundary_capture_count = 0;
  uint64_t empty_command_boundary_capture_count = 0;
  uint64_t last_command_boundary_submit = 0;
  uint64_t last_command_boundary_staged_commands = 0;
  uint64_t last_command_boundary_staged_draws = 0;
  uint64_t last_submitted_draw_event_count = 0;
  uint64_t dropped_render_commands = 0;
  uint64_t funnel_draw_commands_pushed = 0;
  uint64_t funnel_draw_commands_published = 0;
  uint64_t consumed_exact_state_commands = 0;
  uint64_t render_target_switch_count = 0;
  uint64_t depth_target_switch_count = 0;
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
void FableDirectSetPackedSamplerStateSnapshot(
    const std::array<uint32_t, kRendererD3DSamplerStateSlots>& states);
uint32_t FableDirectGetPackedSamplerState(uint32_t slot);
// PURE packing helper: folds one sampler state field into an existing packed
// value and returns the result. No global state is read or written, so callers
// that maintain their OWN sampler mirror (the GTL does) can pack without going
// through FableDirectStorePackedSamplerState, which additionally mutates
// direct-pipeline state and would double-store.
uint32_t FableDirectPackSamplerStateValue(uint32_t packed, uint32_t state_slot,
                                          uint32_t value);

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
// Snapshot the live bool/int shader-flow constant banks (observer-fed via the
// ledger publish / SET_CONSTANT / register-mirror paths). Added for the GTL
// command builder: GTL draws carried default-zero bools since the builder
// never read g_fable_direct.constants (2026-08-01 state-fidelity audit F1).
void FableDirectCopyShaderFlowConstants(
    std::array<uint32_t, kRendererShaderBoolConstantDwords>& vertex_bool,
    std::array<uint32_t, kRendererShaderBoolConstantDwords>& pixel_bool,
    std::array<uint32_t, kRendererShaderIntConstantDwords>& vertex_int,
    std::array<uint32_t, kRendererShaderIntConstantDwords>& pixel_int,
    uint32_t& vertex_int_valid_mask, uint32_t& pixel_int_valid_mask);
void FableDirectSetShaderBoolConstantsSnapshot(
    uint32_t pixel_bank,
    const std::array<uint32_t, kRendererShaderBoolConstantDwords>& constants);
void FableDirectSetPm4Field(FableDirectPm4Field field, uint32_t value,
                            FableDirectStateRoot root = {});
void FableDirectMirrorPm4Field(FableDirectPm4Field field, uint32_t value);
void FableDirectRecordStateFlush(uint32_t device, uint32_t lr,
                                 FableDirectStateRoot root = {});
bool FableDirectRemoveSupersededScratchDrawEvent(uint64_t serial,
                                                 uint32_t render_target);
size_t FableDirectStagedDrawEventCount();
// Diagnostic: number of per-serial scratch payload entries still cached for
// the draw (0 = no payload was ever captured for it, or it was consumed).
uint32_t FableDirectDrawScratchCacheEntryCount(uint64_t draw_serial,
                                               bool index_buffer);

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
// Wait-vs-work split for the above. It is the single most expensive observer
// ENTER feed (10.7s of a 15.5s phase in 356 calls, run 398) and it takes
// g_fable_direct.mutex, which the render thread holds across compose. Says
// whether those 28-39ms per call are the guest BLOCKING on the renderer or real
// work. Reported by the pump as "Refii exactcapture".
void GetExactCaptureStats(uint64_t& calls, uint64_t& wait_us,
                          uint64_t& body_us);
// Completes the non-video command batch assembled during one guest
// rex_lhRenderer_RenderFrame traversal. Video producers publish explicitly
// and do not wait for this boundary.
bool FableDirectSealNonVideoGuestFrame(uint64_t boundary_serial);
// The stage-time publish_generation commands are receiving RIGHT NOW — i.e.
// the guest frame still being assembled. See FableDirectFrameCommandBatch::
// in_progress_generation for why the supersession filter needs it.
uint32_t FableDirectCurrentStagingPublishGeneration();
uint32_t FableDirectConsumeExactFramebufferStateCommands();
uint32_t FableDirectConsumeExactRootedStateCommands();
uint32_t FableDirectConsumeStateFlushCommands();
uint32_t FableDirectConsumeStateOnlyCommands();
bool FableDirectDrawEventsReferenceResource(
    const FableDirectResourceReference& resource);

FableDirectStateSnapshot GetFableDirectStateSnapshot();
bool TryGetFableDirectStateSnapshot(FableDirectStateSnapshot& snapshot);
std::vector<FableDirectRenderCommand> FableDirectQueuedRenderCommandSnapshot(
    size_t max_commands);
std::vector<FableDirectRenderCommand>
ConsumeFableDirectSubmittedRenderCommands();

}  // namespace refii::renderer
