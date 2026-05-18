#include "runtime/hook_observer.h"

#include "runtime/cvars.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#include <rex/logging.h>
#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>

namespace refii::renderer {
namespace {

constexpr float kFableNativeViewportWidth = 1280.0f;
constexpr float kFableNativeViewportHeight = 720.0f;
constexpr uint32_t kD3DDeviceDrawVerticesUPStart = 0x82265CD8u;
constexpr uint32_t kD3DDeviceDrawVerticesUPEnd =
    kD3DDeviceDrawVerticesUPStart + 0x48u;
constexpr uint32_t kFableRenderStateSlotZFunc = 0;
constexpr uint32_t kFableRenderStateSlotColorWriteEnable = 1;
constexpr uint32_t kFableRenderStateSlotAlphaBlendEnable = 2;
constexpr uint32_t kFableRenderStateSlotSrcBlend = 3;
constexpr uint32_t kFableRenderStateSlotZWriteEnable = 4;
constexpr uint32_t kFableRenderStateSlotDestBlend = 5;
constexpr uint32_t kFableRenderStateSlotAlphaTestEnable = 6;
constexpr uint32_t kFableRenderStateSlotZEnable = 7;
constexpr uint32_t kFableRenderStateSlotHiZWriteEnable = 8;
constexpr uint32_t kFableRenderStateSlotBlendOp = 9;
constexpr uint32_t kFableRenderStateSlotBlendOpAlpha = 10;
constexpr uint32_t kFableRenderStateSlotSrcBlendAlpha = 11;
constexpr uint32_t kFableRenderStateSlotDestBlendAlpha = 12;
constexpr uint32_t kFableRenderStateSlotSeparateAlphaBlendEnable = 13;
constexpr uint32_t kFableRenderStateSlotStencilFail = 16;
constexpr uint32_t kFableRenderStateSlotStencilZFail = 17;
constexpr uint32_t kFableRenderStateSlotStencilEnable = 18;
constexpr uint32_t kFableRenderStateSlotCullMode = 19;
constexpr uint32_t kFableRenderStateSlotStencilPass = 20;
constexpr uint32_t kFableRenderStateSlotStencilFunc = 21;
constexpr uint32_t kFableRenderStateSlotStencilReadMask = 22;
constexpr uint32_t kFableRenderStateSlotStencilRef = 23;
constexpr uint32_t kFableRenderStateSlotAlphaFunc = 24;
constexpr uint32_t kFableRenderStateSlotAlphaRef = 25;
constexpr uint32_t kFableRenderStateSlotPixelCenterOffset = 26;
constexpr uint32_t kFableRenderStateSlotStencilWriteMask = 27;
constexpr uint32_t kFableRenderStateSlotPackedDepthModeBit2 = 28;
constexpr uint32_t kFableRenderStateSlotPackedDepthModeBit5 = 29;
constexpr uint32_t kFableRenderStateSlotPackedBlendControl20 = 30;
constexpr uint32_t kXenosRegRbSurfaceInfo = 0x2000u;
constexpr uint32_t kXenosRegRbColorInfo0 = 0x2001u;
constexpr uint32_t kXenosRegRbDepthInfo = 0x2002u;
constexpr uint32_t kXenosRegRbColorInfo1 = 0x2003u;
constexpr uint32_t kXenosRegRbColorInfo2 = 0x2004u;
constexpr uint32_t kXenosRegRbColorInfo3 = 0x2005u;
constexpr uint32_t kXenosRegRbCopyControl = 0x2318u;
constexpr uint32_t kXenosRegRbCopyDestBase = 0x2319u;
constexpr uint32_t kXenosRegRbCopyDestPitch = 0x231Au;
constexpr uint32_t kXenosRegRbCopyDestInfo = 0x231Bu;
constexpr uint32_t kXenosRegRbCopyFunc = 0x2320u;
constexpr uint32_t kXenosRegRbCopyRef = 0x2321u;
constexpr uint32_t kXenosRegRbCopyMask = 0x2322u;
constexpr uint32_t kXenosRegRbCopySurfaceSlice = 0x2323u;
constexpr uint32_t kXenosRegRbColorMask = 0x2104u;
constexpr uint32_t kXenosRegRbStencilRefMaskBf = 0x210Cu;
constexpr uint32_t kXenosRegRbStencilRefMask = 0x210Du;
constexpr uint32_t kXenosRegRbAlphaRef = 0x210Eu;
constexpr uint32_t kXenosRegRbDepthControl = 0x2200u;
constexpr uint32_t kXenosRegRbBlendControl0 = 0x2201u;
constexpr uint32_t kXenosRegRbColorControl = 0x2202u;
constexpr uint32_t kXenosRegPaSuScModeCntl = 0x2205u;
constexpr uint32_t kXenosRegRbModeControl = 0x2208u;
constexpr uint32_t kXenosRegRbBlendControl1 = 0x2209u;
constexpr uint32_t kXenosRegRbBlendControl2 = 0x220Au;
constexpr uint32_t kXenosRegRbBlendControl3 = 0x220Bu;
constexpr uint32_t kFableNeutralTextureBaseAddress = 0x1FC40000u;
constexpr uint32_t kFableSurfacePitchMsaaOffset = 0x18;
constexpr uint32_t kFableSurfaceRenderTargetInfoOffset = 0x1C;
constexpr uint32_t kFableSurfaceDepthStencilInfoOffset = 0x20;
constexpr uint32_t kFableSurfacePackedExtentOffset = 0x24;
constexpr uint32_t kFableSurfaceD3DFormatOffset = 0x28;
constexpr uint32_t kFableSurfaceByteSizeOffset = 0x2C;
constexpr uint32_t kFableTextCodeAddressMin = 0x82000000u;
constexpr uint32_t kFableTextCodeAddressMax = 0x83300000u;

struct FablePrivateTextureRectangleReplayVertex {
  float position[4] = {};
  float texcoord0[4] = {};
  float texcoord1[4] = {};
};

enum class RendererResourceKind {
  kUnknown,
  kTexture,
  kSurfaceView,
  kRenderTargetSurface,
  kDepthSurface,
  kVertexBuffer,
  kIndexBuffer,
  kVertexDeclaration,
  kVertexShader,
  kPixelShader,
};

struct RendererResourceRecord {
  RendererResourceKind kind = RendererResourceKind::kUnknown;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t raw_buffer_object = 0;
  uint32_t raw_size = 0;
  uint32_t raw_stride_or_format = 0;
  uint32_t raw_flags = 0;
  uint32_t raw_ref_count = 0;
  uint32_t raw_aux20 = 0;
  uint32_t raw_parent24 = 0;
  uint32_t scratch_upload_source_address = 0;
  uint32_t raw_packed28 = 0;
  uint32_t raw_packed32 = 0;
  uint32_t raw_packed36 = 0;
  uint32_t raw_packed40 = 0;
  uint32_t raw_packed44 = 0;
  uint32_t raw_packed48 = 0;
  std::array<uint32_t, 6> normalized_fetch_dwords = {};
  uint64_t raw_hash = 0;
  uint32_t locked_address = 0;
  uint32_t locked_size = 0;
  uint64_t upload_generation = 0;
  std::vector<uint8_t> upload_data;
  uint64_t serial = 0;
  bool scratch = false;
};

struct PendingPixDecodedVideoFrame {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t frame_base = 0;
  uint64_t serial = 0;
};

struct BinkVideoPlaneBinding {
  uint32_t surface = 0;
  uint32_t plane_info = 0;
  uint32_t plane_output_slot = 0;
  uint32_t physical_address = 0;
  uint32_t lock_address = 0;
  uint32_t upload_size = 0;
  uint64_t serial = 0;
};

struct RendererResourceCounts {
  uint64_t live_resources = 0;
  uint64_t live_textures = 0;
  uint64_t live_surfaces = 0;
  uint64_t live_vertex_buffers = 0;
  uint64_t live_index_buffers = 0;
  uint64_t live_shaders = 0;
};

struct RendererHookObserverState {
  std::mutex mutex;
  std::unordered_map<uint32_t, RendererResourceRecord> resources;
  uint8_t* memory_base = nullptr;
  uint64_t serial = 0;
  uint64_t draw_event_serial = 0;
  uint64_t dropped_draw_events = 0;
  uint64_t consumed_draw_event_present = 0;
  uint64_t presented_draw_event_present = 0;
  uint64_t active_wrapped_draw_serial = 0;
  uint32_t active_wrapped_draw_device = 0;
  uint64_t resource_creates = 0;
  uint64_t resource_destroys = 0;
  uint64_t resource_locks = 0;
  uint64_t resource_unlocks = 0;
  uint64_t draws = 0;
  uint64_t indexed_draws = 0;
  uint64_t render_passes = 0;
  uint64_t state_flushes = 0;
  uint64_t state_dispatches = 0;
  uint64_t render_queue_callbacks = 0;
  uint64_t command_writes = 0;
  uint64_t command_submits = 0;
  uint64_t command_words = 0;
  uint64_t pm4_type0_packets = 0;
  uint64_t pm4_type3_packets = 0;
  uint64_t pm4_indirect_packets = 0;
  uint64_t pm4_draw_packets = 0;
  uint64_t pm4_swap_packets = 0;
  uint64_t pm4_constant_packets = 0;
  uint64_t pm4_parse_errors = 0;
  uint64_t last_pm4_resolve_command_write = 0;
  uint64_t last_pm4_resolve_key = 0;
  uint64_t clears = 0;
  uint64_t presents = 0;
  uint32_t display_present_texture_base = 0;
  uint32_t display_present_width = 0;
  uint32_t display_present_height = 0;
  uint32_t display_present_format = 0;
  uint32_t display_present_surface_object = 0;
  uint32_t display_present_source = 0;
  uint32_t display_present_register = 0;
  uint32_t display_present_register_value = 0;
  std::array<uint32_t, 6> display_present_fetch_dwords = {};
  uint64_t state_changes = 0;
  uint32_t render_targets[4] = {};
  uint32_t depth_stencil = 0;
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  uint32_t index_buffer = 0;
  uint32_t textures[16] = {};
  bool pm4_registers_valid = false;
  uint32_t rb_surface_info = 0;
  uint32_t rb_color_info[4] = {};
  bool rb_color_info_valid[4] = {};
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
  uint32_t rb_blendcontrol[4] = {0x00010001u, 0x00010001u, 0x00010001u,
                                 0x00010001u};
  uint32_t rb_colorcontrol = 0;
  uint32_t rb_alpha_ref = 0;
  uint32_t rb_stencilrefmask = 0x00FFFF00u;
  uint32_t rb_stencilrefmask_bf = 0x00FFFF00u;
  uint32_t pa_su_sc_mode_cntl = 0;
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};
  bool render_state_defaults_initialized = false;
  uint32_t requested_z_enable = 0;
  uint32_t requested_z_write_enable = 0;
  bool requested_z_enable_valid = false;
  bool requested_z_write_enable_valid = false;
  std::array<uint32_t, kRendererD3DSamplerStateSlots> sampler_states = {};
  RendererStreamBindingSnapshot streams[16] = {};
  std::array<uint32_t, kRendererFetchConstantDwords> fetch_constants = {};
  std::array<uint32_t, kRendererShaderFloatConstantDwords>
      vertex_shader_constants = {};
  std::array<uint32_t, kRendererShaderFloatConstantDwords>
      pixel_shader_constants = {};
  std::array<bool, kRendererShaderFloatConstantDwords>
      vertex_shader_constant_valid = {};
  std::array<bool, kRendererShaderFloatConstantDwords>
      pixel_shader_constant_valid = {};
  std::array<uint32_t, kRendererShaderIntConstantDwords>
      vertex_shader_int_constants = {};
  std::array<uint32_t, kRendererShaderIntConstantDwords>
      pixel_shader_int_constants = {};
  uint32_t vertex_shader_int_constant_valid_mask = 0;
  uint32_t pixel_shader_int_constant_valid_mask = 0;
  std::array<uint32_t, kRendererShaderBoolConstantDwords>
      vertex_shader_bool_constants = {};
  std::array<uint32_t, kRendererShaderBoolConstantDwords>
      pixel_shader_bool_constants = {};
  uint64_t shader_constant_uploads = 0;
  uint64_t fetch_constant_uploads = 0;
  uint64_t shader_constant_upload_nonzero_dwords = 0;
  uint64_t shader_constant_zero_uploads = 0;
  uint64_t shader_constant_skipped_uploads = 0;
  uint64_t pix_decode_frames = 0;
  uint64_t pix_render_hooks = 0;
  uint64_t pix_render_events = 0;
  uint64_t pix_render_uv_overrides = 0;
  uint64_t pix_capture_hooks = 0;
  uint64_t pix_render_bad_packets = 0;
  uint64_t pix_render_bad_vertices = 0;
  uint32_t last_pix_texture = 0;
  uint32_t last_pix_packet = 0;
  uint32_t last_pix_decode_base = 0;
  uint32_t last_pix_decode_width = 0;
  uint32_t last_pix_decode_height = 0;
  uint32_t last_pix_fetch_base = 0;
  uint32_t last_pix_fetch_size = 0;
  uint32_t last_pix_fetch_format = 0;
  float last_pix_output_width = 0.0f;
  float last_pix_output_height = 0.0f;
  float last_pix_decoded_width = 0.0f;
  float last_pix_decoded_height = 0.0f;
  float last_pix_u1 = 0.0f;
  float last_pix_v1 = 0.0f;
  uint32_t last_shader_constant_bank = 0;
  uint32_t last_shader_constant_first_register = 0;
  uint32_t last_shader_constant_first_component = 0;
  uint32_t last_shader_constant_dwords = 0;
  uint32_t last_shader_constant_nonzero_dwords = 0;
  uint32_t last_shader_constant_source = 0;
  uint32_t last_shader_constant_lr = 0;
  uint32_t last_shader_constant_preview[4] = {};
  uint64_t shader_bool_constant_uploads = 0;
  uint32_t last_shader_bool_constant_bank = 0;
  uint32_t last_shader_bool_constant_first_register = 0;
  uint32_t last_shader_bool_constant_count = 0;
  uint32_t last_shader_bool_constant_packed = 0;
  bool logged_shader_constants = false;
  bool logged_shader_bool_constants = false;
  bool logged_fetch_constants = false;
  uint32_t logged_texture_capture_successes = 0;
  uint32_t logged_texture_capture_failures = 0;
  RendererLastDrawSnapshot last_draw;
  std::vector<RendererDrawEventSnapshot> draw_events;
  std::vector<RendererDrawEventSnapshot> presented_draw_events;
  uint32_t viewport_pointer = 0;
  uint32_t scissor_pointer = 0;
  RendererViewportSnapshot viewport;
  RendererScissorSnapshot scissor;
  RendererScalerSnapshot scaler;
  uint32_t clear_flags = 0;
  uint32_t last_state_flush_lr = 0;
  uint32_t last_command_source = 0;
  uint32_t last_command_pointer = 0;
  uint32_t last_command_word_count = 0;
  uint32_t last_command_first_word = 0;
  uint32_t last_command_packet_type = 0;
  uint32_t last_command_type3_opcode = 0;
  uint32_t last_command_preview[8] = {};
  uint32_t logged_late_draw_binding_patches = 0;
  uint32_t logged_missing_draw_bindings = 0;
  uint32_t promoted_private_texture_rectangle_submits = 0;
  uint32_t logged_indexed_scratch_captures = 0;
  bool logged_draw_vertices_up_capture = false;
  bool logged_private_texture_rectangle_capture = false;
  uint32_t logged_private_texture_rectangle_details = 0;
  uint32_t logged_inline_surface_descriptor_builds = 0;
  uint32_t logged_inline_texture_descriptor_builds = 0;
  uint32_t logged_pix_conversion_draw_chains = 0;
  uint32_t logged_pix_render_packet_builders = 0;
  uint32_t logged_pix_decode_packet_shapes = 0;
  uint32_t logged_pix_capture_frames = 0;
  RendererResourceRecord last_resource;
  std::unordered_map<uint32_t, PendingPixDecodedVideoFrame>
      pending_pix_decoded_video_frames;
  std::unordered_map<uint32_t, uint64_t> unknown_destroy_counts;
  std::unordered_set<uint32_t> deferred_surface_releases;
  std::unordered_map<uint32_t, RendererResourceRecord> recent_surface_shapes;
  std::unordered_map<uint32_t, uint64_t> state_flush_callers;
  std::unordered_map<uint32_t, uint64_t> state_dispatch_targets;
  std::unordered_map<uint32_t, uint64_t> render_pass_queue_targets;
  std::unordered_set<uint32_t> logged_bound_surface_failures;
  std::unordered_set<uint32_t> logged_vertex_declaration_failures;
  std::unordered_set<uint64_t> logged_private_texture_rectangle_detail_keys;
  uint32_t logged_stream_source_shapes = 0;
  uint32_t logged_stream_source_zero_ranges = 0;
  uint32_t logged_stream_upload_aliases = 0;
  uint32_t logged_lock_resource_shapes = 0;
  uint32_t logged_texture_lock_records = 0;
  uint32_t logged_image_lock_uploads = 0;
  uint32_t logged_export_resolves = 0;
  uint32_t logged_pm4_resolves = 0;
  uint32_t logged_bink_video_planes = 0;
  uint32_t logged_bink_video_uploads = 0;
  uint64_t bink_video_plane_setups = 0;
  uint64_t bink_video_upload_refreshes = 0;
  std::unordered_map<uint32_t, BinkVideoPlaneBinding> bink_planes_by_surface;
  std::unordered_map<uint32_t, RendererSurfaceCopyAliasSnapshot>
      surface_copy_aliases;
  uint32_t logged_surface_copy_aliases = 0;
};

RendererHookObserverState g_observer;

constexpr size_t kMaxBufferedDrawEvents = 8192;
constexpr size_t kMaxScratchBufferResources = 256;
constexpr uint32_t kMaxTrackedShaderBytecodeSize = 1u << 20;
constexpr uint32_t kMaxTrackedShaderContainerSize = 2u << 20;
constexpr uint32_t kMaxScratchUploadSnapshotSize = 16u << 20;
constexpr uint32_t kMaxTextureUploadSnapshotSize = 64u << 20;
constexpr uint32_t kMinTextureUploadSnapshotAddress = 0x10000u;
constexpr size_t kMaxPendingPixDecodedVideoFrames = 32;
constexpr size_t kMaxRecentSurfaceShapes = 512;
constexpr uint32_t kMaxTrackedVertexDeclarationElements = 64;
constexpr uint32_t kMaxReasonableFableVertexDeclarationElements = 32;
constexpr uint32_t kFableObjectAddressMin = 0x40000000u;
constexpr uint32_t kFableObjectAddressMax = 0x80000000u;

void ResetObservedRenderStatesToDefaultsLocked() {
  g_observer.render_states.fill(0);

  // Fable/Xenos defaults used as a baseline until the title writes explicit
  // state. This keeps explicit zero values, such as ColorWriteEnable=0 for
  // depth-only passes, meaningful instead of conflating them with uninitialized
  // state.
  g_observer.render_states[kFableRenderStateSlotZFunc] = 3;  // LESSEQUAL
  g_observer.render_states[kFableRenderStateSlotColorWriteEnable] = 0xFu;
  g_observer.render_states[kFableRenderStateSlotAlphaBlendEnable] = 0;
  g_observer.render_states[kFableRenderStateSlotSrcBlend] = 1;   // ONE
  g_observer.render_states[kFableRenderStateSlotZWriteEnable] = 1;
  g_observer.render_states[kFableRenderStateSlotDestBlend] = 0;  // ZERO
  g_observer.render_states[kFableRenderStateSlotAlphaTestEnable] = 0;
  g_observer.render_states[kFableRenderStateSlotZEnable] = 1;
  g_observer.render_states[kFableRenderStateSlotHiZWriteEnable] = 0;
  g_observer.render_states[kFableRenderStateSlotBlendOp] = 0;       // ADD
  g_observer.render_states[kFableRenderStateSlotBlendOpAlpha] = 0;  // ADD
  g_observer.render_states[kFableRenderStateSlotSrcBlendAlpha] = 1; // ONE
  g_observer.render_states[kFableRenderStateSlotDestBlendAlpha] = 0; // ZERO
  g_observer.render_states[kFableRenderStateSlotSeparateAlphaBlendEnable] = 0;
  g_observer.render_states[kFableRenderStateSlotStencilFail] = 0;   // KEEP
  g_observer.render_states[kFableRenderStateSlotStencilZFail] = 0;  // KEEP
  g_observer.render_states[kFableRenderStateSlotStencilEnable] = 0;
  g_observer.render_states[kFableRenderStateSlotCullMode] = 6;   // CCW cull
  g_observer.render_states[kFableRenderStateSlotStencilPass] = 0;   // KEEP
  g_observer.render_states[kFableRenderStateSlotStencilFunc] = 7;   // ALWAYS
  g_observer.render_states[kFableRenderStateSlotStencilReadMask] = 0xFFu;
  g_observer.render_states[kFableRenderStateSlotStencilRef] = 0;
  g_observer.render_states[kFableRenderStateSlotAlphaFunc] = 6;
  g_observer.render_states[kFableRenderStateSlotAlphaRef] = 0;
  g_observer.render_states[kFableRenderStateSlotPixelCenterOffset] = 0;
  g_observer.render_states[kFableRenderStateSlotStencilWriteMask] = 0xFFu;
  g_observer.render_states[kFableRenderStateSlotPackedDepthModeBit2] = 0;
  g_observer.render_states[kFableRenderStateSlotPackedDepthModeBit5] = 0;
  g_observer.render_states[kFableRenderStateSlotPackedBlendControl20] = 0;
  g_observer.render_state_defaults_initialized = true;
}

void RememberGuestMemoryBaseLocked(uint8_t* base) {
  if (base != nullptr) {
    g_observer.memory_base = base;
    if (!g_observer.render_state_defaults_initialized) {
      ResetObservedRenderStatesToDefaultsLocked();
    }
  }
}

uint32_t GuestPhysicalHostOffset(uint32_t guest_address) {
#if defined(_WIN32)
  return guest_address >= 0xE0000000u ? 0x1000u : 0u;
#else
  return 0;
#endif
}

uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address) {
  uint32_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap32(raw);
}

void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value) {
  const uint32_t raw = __builtin_bswap32(value);
  std::memcpy(base + guest_address + GuestPhysicalHostOffset(guest_address),
              &raw, sizeof(raw));
}

void StoreGuestF32(uint8_t* base, uint32_t guest_address, float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreGuestU32(base, guest_address, bits);
}

void StoreGuestU8(uint8_t* base, uint32_t guest_address, uint8_t value) {
  *(base + guest_address + GuestPhysicalHostOffset(guest_address)) = value;
}

uint16_t LoadGuestU16(uint8_t* base, uint32_t guest_address) {
  uint16_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap16(raw);
}

uint8_t LoadGuestU8(uint8_t* base, uint32_t guest_address) {
  return *(base + guest_address + GuestPhysicalHostOffset(guest_address));
}

int32_t LoadGuestI32(uint8_t* base, uint32_t guest_address) {
  return static_cast<int32_t>(LoadGuestU32(base, guest_address));
}

float LoadGuestF32(uint8_t* base, uint32_t guest_address) {
  const uint32_t bits = LoadGuestU32(base, guest_address);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

uint32_t FloatToBits(float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

bool IsLikelyFableObjectAddress(uint32_t guest_address) {
  return guest_address >= kFableObjectAddressMin &&
         guest_address < kFableObjectAddressMax &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

bool IsLikelyFableCodeAddress(uint32_t guest_address) {
  return guest_address >= kFableTextCodeAddressMin &&
         guest_address < kFableTextCodeAddressMax &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

bool IsLikelyGuestDwordAddress(uint32_t guest_address) {
  return guest_address >= 0x1000u && guest_address < kFableObjectAddressMax &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

bool IsPackedPrintableAsciiWord(uint32_t word) {
  uint32_t printable_bytes = 0;
  for (uint32_t byte_index = 0; byte_index < 4; ++byte_index) {
    const uint8_t byte =
        static_cast<uint8_t>((word >> (byte_index * 8u)) & 0xFFu);
    if (byte >= 0x20u && byte <= 0x7Eu) {
      ++printable_bytes;
    }
  }
  return printable_bytes >= 3;
}

bool PackedAsciiWordContainsPathByte(uint32_t word) {
  for (uint32_t byte_index = 0; byte_index < 4; ++byte_index) {
    const uint8_t byte =
        static_cast<uint8_t>((word >> (byte_index * 8u)) & 0xFFu);
    if (byte == '\\' || byte == '/' || byte == '.') {
      return true;
    }
  }
  return false;
}

bool GuestWordsLookLikeInlinePathString(uint8_t* base, uint32_t guest_address,
                                        uint32_t word_count = 16) {
  if (!base || !IsLikelyFableObjectAddress(guest_address) || word_count < 4) {
    return false;
  }

  uint32_t printable_words = 0;
  bool has_path_byte = false;
  for (uint32_t i = 1; i < word_count; ++i) {
    const uint32_t word = LoadGuestU32(base, guest_address + i * 4u);
    if (!IsPackedPrintableAsciiWord(word)) {
      continue;
    }
    ++printable_words;
    has_path_byte = has_path_byte || PackedAsciiWordContainsPathByte(word);
  }

  return has_path_byte && printable_words >= 2;
}

bool WordLooksLikeUtf16LePathText(uint32_t word) {
  const uint8_t c0 = static_cast<uint8_t>(word & 0xFFu);
  const uint8_t z0 = static_cast<uint8_t>((word >> 8u) & 0xFFu);
  const uint8_t c1 = static_cast<uint8_t>((word >> 16u) & 0xFFu);
  const uint8_t z1 = static_cast<uint8_t>((word >> 24u) & 0xFFu);
  const bool printable0 = c0 >= 0x20u && c0 <= 0x7Eu;
  const bool printable1 = c1 >= 0x20u && c1 <= 0x7Eu;
  return z0 == 0 && z1 == 0 && printable0 && printable1 &&
         (c0 == '\\' || c0 == '/' || c0 == '.' || c1 == '\\' ||
          c1 == '/' || c1 == '.' || (printable0 && printable1));
}

bool IsLikelyGpuBufferRange(uint32_t guest_address, uint32_t size) {
  return guest_address >= 0x1000u && guest_address < kFableObjectAddressMin &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0 && size != 0 &&
         size <= kMaxScratchUploadSnapshotSize;
}

bool IsLikelyGpuByteBufferRange(uint32_t guest_address, uint32_t size) {
  return guest_address >= 0x1000u && guest_address < kFableObjectAddressMin &&
         size != 0 && size <= kMaxScratchUploadSnapshotSize;
}

bool IsLikelyHighScratchByteBufferRange(uint32_t guest_address, uint32_t size) {
  return guest_address >= 0x80000000u &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0 && size != 0 &&
         size <= kMaxScratchUploadSnapshotSize;
}

uint32_t NormalizeFableGpuAddress(uint32_t pointer) {
  if (pointer >= 0x41000000u && pointer < 0x50000000u) {
    return pointer - 0x41000000u;
  }
  if (pointer >= 0x40000000u && pointer < 0x41000000u) {
    return pointer - 0x40000000u;
  }
  return pointer;
}

uint32_t DecodeFableGpuMemoryPointer(uint32_t pointer) {
  return (((pointer >> 20u) + 512u) & 0x1000u) +
         (pointer & 0x1FFFFFFFu);
}

uint32_t DecodeFableGpuBufferPointer(uint32_t pointer) {
  return DecodeFableGpuMemoryPointer(pointer & ~3u);
}

uint32_t DecodeFablePackedBufferSize(uint32_t value) {
  if (value != 0 && value <= kMaxScratchUploadSnapshotSize) {
    return value;
  }

  const uint32_t encoded_bytes = value & 0x00FFFFFCu;
  if (encoded_bytes != 0 && encoded_bytes <= kMaxScratchUploadSnapshotSize) {
    return encoded_bytes;
  }

  return 0;
}

bool IsLikelyBindableBufferAddress(uint32_t guest_address) {
  return IsLikelyFableObjectAddress(guest_address) ||
         IsLikelyGpuByteBufferRange(guest_address, 1) ||
         IsLikelyHighScratchByteBufferRange(guest_address, 1);
}

uint64_t HashGuestBytesFnv1a64(uint8_t* base, uint32_t guest_address,
                               uint32_t size) {
  if (!base || guest_address == 0 || size == 0) {
    return 0;
  }

  const uint8_t* data =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  uint64_t hash = 14695981039346656037ull;
  for (uint32_t i = 0; i < size; ++i) {
    hash ^= data[i];
    hash *= 1099511628211ull;
  }
  return hash;
}

uint32_t FindFirstNonZeroGuestByte(uint8_t* base, uint32_t guest_address,
                                   uint32_t size, uint32_t max_scan) {
  if (!base || guest_address == 0 || size == 0 || max_scan == 0) {
    return UINT32_MAX;
  }

  const uint32_t scan_size = std::min(size, max_scan);
  const uint8_t* data =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  for (uint32_t i = 0; i < scan_size; ++i) {
    if (data[i] != 0) {
      return i;
    }
  }
  return UINT32_MAX;
}

bool IsPowerOfTwo(uint64_t value) {
  return value != 0 && (value & (value - 1)) == 0;
}

uint32_t NormalizeTextureFetchAddressDword(uint32_t value) {
  return ((((value >> 20u) + 512u) & 0x1000u) +
          (value & 0x1FFFF000u)) |
         (value & 0xFFFu);
}

std::array<uint32_t, 6> NormalizeTextureFetchDwords(
    std::array<uint32_t, 6> dwords) {
  dwords[1] = NormalizeTextureFetchAddressDword(dwords[1]);
  dwords[5] = NormalizeTextureFetchAddressDword(dwords[5]);
  return dwords;
}

std::array<uint32_t, 6> LoadTextureFetchDwords(uint8_t* base,
                                                uint32_t source_address) {
  std::array<uint32_t, 6> dwords = {};
  if (!base || source_address == 0) {
    return dwords;
  }

  for (uint32_t i = 0; i < dwords.size(); ++i) {
    dwords[i] = LoadGuestU32(base, source_address + i * sizeof(uint32_t));
  }

  return NormalizeTextureFetchDwords(dwords);
}

bool HasTextureFetchDwords(const std::array<uint32_t, 6>& dwords) {
  return std::any_of(dwords.begin(), dwords.end(),
                     [](uint32_t dword) { return dword != 0; });
}

bool IsTextureFetchShapeSupportedForSnapshot(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  namespace xenos = rex::graphics::xenos;

  switch (fetch.dimension) {
    case xenos::DataDimension::k1D:
    case xenos::DataDimension::k3D:
      return !fetch.stacked;
    case xenos::DataDimension::k2DOrStacked:
      return true;
    case xenos::DataDimension::kCube:
      return !fetch.stacked && fetch.size_2d.stack_depth == 5;
    default:
      return false;
  }
}

void NormalizeTextureFetchForSnapshotInfo(
    rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  if (fetch.mip_address == 0) {
    fetch.mip_min_level = 0;
    fetch.mip_max_level = 0;
    return;
  }

  if (fetch.mip_max_level < fetch.mip_min_level) {
    fetch.mip_max_level = fetch.mip_min_level;
  }
}

uint32_t ClampTextureSnapshotSize(uint32_t size) {
  return std::min<uint32_t>(size, kMaxTextureUploadSnapshotSize);
}

bool IsImageResourceKind(RendererResourceKind kind);
bool IsBufferResourceKind(RendererResourceKind kind);
bool IsSurfaceResourceKind(RendererResourceKind kind);

struct TextureFetchSnapshotInfo {
  uint32_t base_address = 0;
  uint32_t size = 0;
  const char* failure_reason = nullptr;
};

TextureFetchSnapshotInfo GetTextureFetchSnapshotInfo(
    const std::array<uint32_t, 6>& dwords) {
  TextureFetchSnapshotInfo info;
  if (!HasTextureFetchDwords(dwords)) {
    info.failure_reason = "empty_fetch";
    return info;
  }

  namespace texture_info = rex::graphics;

  rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dwords[0];
  fetch.dword_1 = dwords[1];
  fetch.dword_2 = dwords[2];
  fetch.dword_3 = dwords[3];
  fetch.dword_4 = dwords[4];
  fetch.dword_5 = dwords[5];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kTexture) {
    info.failure_reason = "not_texture";
    return info;
  }
  const texture_info::FormatInfo* format_info =
      texture_info::FormatInfo::Get(fetch.format);
  if (!format_info) {
    info.failure_reason = "unknown_format";
    return info;
  }
  if (format_info->bytes_per_block() == 0) {
    info.failure_reason = "zero_bpb";
    return info;
  }
  if (!IsTextureFetchShapeSupportedForSnapshot(fetch)) {
    info.failure_reason = "unsupported_shape";
    return info;
  }
  NormalizeTextureFetchForSnapshotInfo(fetch);

  texture_info::TextureInfo texture;
  if (!texture_info::TextureInfo::Prepare(fetch, &texture)) {
    info.failure_reason = "prepare_failed";
    return info;
  }

  info.base_address = texture.memory.base_address;
  info.size = ClampTextureSnapshotSize(texture.memory.base_size);
  if (info.base_address == 0 || info.size == 0) {
    info.base_address = texture.memory.mip_address;
    info.size = ClampTextureSnapshotSize(texture.memory.mip_size);
  }
  if (info.base_address == 0) {
    info.failure_reason = "zero_base";
  } else if (info.size == 0) {
    info.failure_reason = "zero_size";
  }
  return info;
}

bool IsUsableTextureFetchSnapshot(const TextureFetchSnapshotInfo& info) {
  return info.base_address != 0 && info.size != 0;
}

bool IsNeutralTextureFetchSnapshot(const TextureFetchSnapshotInfo& info) {
  return IsUsableTextureFetchSnapshot(info) &&
         info.base_address == kFableNeutralTextureBaseAddress;
}

std::array<uint32_t, 6> TextureFetchDwordsForRecord(
    const RendererResourceRecord& record) {
  const std::array<uint32_t, 6> raw_dwords = NormalizeTextureFetchDwords(
      {record.raw_packed28, record.raw_packed32, record.raw_packed36,
       record.raw_packed40, record.raw_packed44, record.raw_packed48});
  if (!HasTextureFetchDwords(record.normalized_fetch_dwords)) {
    return raw_dwords;
  }
  if (!HasTextureFetchDwords(raw_dwords)) {
    return record.normalized_fetch_dwords;
  }

  const TextureFetchSnapshotInfo normalized_info =
      GetTextureFetchSnapshotInfo(record.normalized_fetch_dwords);
  const TextureFetchSnapshotInfo raw_info =
      GetTextureFetchSnapshotInfo(raw_dwords);
  if (raw_info.base_address != 0 && raw_info.size != 0 &&
      (normalized_info.base_address == 0 || normalized_info.size == 0 ||
       normalized_info.base_address != raw_info.base_address ||
       normalized_info.size != raw_info.size)) {
    return raw_dwords;
  }
  return record.normalized_fetch_dwords;
}

uint32_t TextureSnapshotSizeForRecord(const RendererResourceRecord& record) {
  if (!IsImageResourceKind(record.kind)) {
    return 0;
  }

  const TextureFetchSnapshotInfo info =
      GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record));
  return info.size;
}

RendererResourceRecord* FindResourceRecordLocked(uint32_t address);
bool ResourceRecordMatchesAddress(const RendererResourceRecord& record,
                                  uint32_t address);
void RememberRecentSurfaceShapeLocked(const RendererResourceRecord& record);

bool DrawTextureSlotReferencesResource(
    const RendererDrawEventSnapshot& event, uint32_t slot,
    const RendererResourceRecord& record) {
  if (!IsImageResourceKind(record.kind) || slot >= std::size(event.textures)) {
    return false;
  }

  const uint32_t texture_object = event.textures[slot];
  if (texture_object != 0 &&
      (texture_object == record.guest_address ||
       texture_object == record.parent_address ||
       texture_object == record.raw_buffer_object)) {
    return true;
  }

  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
  if (fetch_dword + kTextureFetchConstantDwords >
      event.fetch_constants.size()) {
    return false;
  }

  std::array<uint32_t, kTextureFetchConstantDwords> slot_fetch = {};
  std::copy_n(event.fetch_constants.begin() + fetch_dword,
              kTextureFetchConstantDwords, slot_fetch.begin());
  const TextureFetchSnapshotInfo slot_info =
      GetTextureFetchSnapshotInfo(slot_fetch);
  if (slot_info.base_address == 0) {
    return false;
  }

  if (slot_info.base_address == record.guest_address ||
      slot_info.base_address == record.parent_address ||
      slot_info.base_address == record.raw_buffer_object) {
    return true;
  }

  const TextureFetchSnapshotInfo record_info =
      GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record));
  return record_info.base_address != 0 &&
         slot_info.base_address == record_info.base_address;
}

void RefreshQueuedDrawTextureResourceSerialLocked(
    const RendererResourceRecord& record) {
  if (!IsImageResourceKind(record.kind) || record.serial == 0 ||
      g_observer.draw_events.empty()) {
    return;
  }

  for (RendererDrawEventSnapshot& event : g_observer.draw_events) {
    for (uint32_t slot = 0; slot < std::size(event.textures); ++slot) {
      if (DrawTextureSlotReferencesResource(event, slot, record)) {
        event.resource_serial =
            std::max(event.resource_serial, record.serial);
        break;
      }
    }
  }
}

bool DrawEventReferencesBufferResource(const RendererDrawEventSnapshot& event,
                                       const RendererResourceRecord& record) {
  if (!IsBufferResourceKind(record.kind)) {
    return false;
  }

  const auto matches_record_address = [&](uint32_t address) {
    return address != 0 &&
           (address == record.guest_address ||
            address == record.parent_address ||
            address == record.raw_buffer_object);
  };

  if (matches_record_address(event.index_buffer)) {
    return true;
  }

  for (const RendererStreamBindingSnapshot& stream : event.streams) {
    if (matches_record_address(stream.buffer)) {
      return true;
    }
  }

  return false;
}

bool DrawEventReferencesResource(const RendererDrawEventSnapshot& event,
                                 const RendererResourceRecord& record) {
  const auto matches_record_address = [&](uint32_t address) {
    return address != 0 && ResourceRecordMatchesAddress(record, address);
  };

  if (matches_record_address(event.vertex_declaration) ||
      matches_record_address(event.vertex_shader) ||
      matches_record_address(event.pixel_shader) ||
      matches_record_address(event.index_buffer) ||
      matches_record_address(event.depth_stencil)) {
    return true;
  }

  for (uint32_t render_target : event.render_targets) {
    if (matches_record_address(render_target)) {
      return true;
    }
  }
  for (const RendererStreamBindingSnapshot& stream : event.streams) {
    if (matches_record_address(stream.buffer)) {
      return true;
    }
  }
  for (uint32_t slot = 0; slot < std::size(event.textures); ++slot) {
    if (DrawTextureSlotReferencesResource(event, slot, record)) {
      return true;
    }
  }

  return false;
}

bool DrawEventListReferencesResourceLocked(
    const std::vector<RendererDrawEventSnapshot>& events,
    const RendererResourceRecord& record) {
  return std::any_of(
      events.begin(), events.end(),
      [&](const RendererDrawEventSnapshot& event) {
        return DrawEventReferencesResource(event, record);
      });
}

bool PendingDrawEventsReferenceResourceLocked(
    const RendererResourceRecord& record) {
  return DrawEventListReferencesResourceLocked(g_observer.draw_events, record) ||
         DrawEventListReferencesResourceLocked(g_observer.presented_draw_events,
                                              record);
}

bool BoundSurfaceStateReferencesResourceLocked(
    const RendererResourceRecord& record) {
  if (!IsSurfaceResourceKind(record.kind)) {
    return false;
  }

  if (ResourceRecordMatchesAddress(record, g_observer.depth_stencil)) {
    return true;
  }

  return std::any_of(
      std::begin(g_observer.render_targets),
      std::end(g_observer.render_targets), [&](uint32_t render_target) {
        return ResourceRecordMatchesAddress(record, render_target);
      });
}

bool ActiveSurfaceStateReferencesResourceLocked(
    const RendererResourceRecord& record) {
  return BoundSurfaceStateReferencesResourceLocked(record) ||
         PendingDrawEventsReferenceResourceLocked(record);
}

void FlushDeferredSurfaceReleasesLocked() {
  for (auto it = g_observer.deferred_surface_releases.begin();
       it != g_observer.deferred_surface_releases.end();) {
    auto resource_it = g_observer.resources.find(*it);
    if (resource_it == g_observer.resources.end()) {
      it = g_observer.deferred_surface_releases.erase(it);
      continue;
    }

    if (ActiveSurfaceStateReferencesResourceLocked(resource_it->second)) {
      ++it;
      continue;
    }

    RememberRecentSurfaceShapeLocked(resource_it->second);
    g_observer.resources.erase(resource_it);
    it = g_observer.deferred_surface_releases.erase(it);
  }
}

void RefreshQueuedDrawBufferResourceSerialLocked(
    const RendererResourceRecord& record) {
  if (!IsBufferResourceKind(record.kind) || record.serial == 0 ||
      g_observer.draw_events.empty()) {
    return;
  }

  for (RendererDrawEventSnapshot& event : g_observer.draw_events) {
    if (DrawEventReferencesBufferResource(event, record)) {
      event.resource_serial = std::max(event.resource_serial, record.serial);
    }
  }
}

void RefreshDrawEventResourceSerialFromBindingsLocked(
    RendererDrawEventSnapshot& event) {
  auto bump_from_address = [&](uint32_t address) {
    RendererResourceRecord* record = FindResourceRecordLocked(address);
    if (record != nullptr && record->serial != 0) {
      event.resource_serial = std::max(event.resource_serial, record->serial);
    }
  };

  bump_from_address(event.vertex_declaration);
  bump_from_address(event.vertex_shader);
  bump_from_address(event.pixel_shader);
  bump_from_address(event.index_buffer);
  bump_from_address(event.depth_stencil);
  for (uint32_t render_target : event.render_targets) {
    bump_from_address(render_target);
  }
  for (const RendererStreamBindingSnapshot& stream : event.streams) {
    bump_from_address(stream.buffer);
  }
  for (uint32_t slot = 0; slot < std::size(event.textures); ++slot) {
    bump_from_address(event.textures[slot]);
    for (const auto& [address, record] : g_observer.resources) {
      (void)address;
      if (DrawTextureSlotReferencesResource(event, slot, record) &&
          record.serial != 0) {
        event.resource_serial =
            std::max(event.resource_serial, record.serial);
      }
    }
  }
}

bool CaptureTextureUploadFromFetchDwords(RendererResourceRecord& record,
                                         const std::array<uint32_t, 6>& dwords,
                                         uint8_t* base) {
  if (!base || record.kind != RendererResourceKind::kTexture ||
      !HasTextureFetchDwords(dwords)) {
    return false;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  const bool source_range_valid =
      info.base_address >= kMinTextureUploadSnapshotAddress &&
      info.size <= kMaxTextureUploadSnapshotSize &&
      info.base_address <= UINT32_MAX - info.size;
  if (info.base_address == 0 || info.size == 0 || !source_range_valid) {
    if (RendererResourceTracingEnabled() &&
        g_observer.logged_texture_capture_failures < 8) {
      REXLOG_INFO(
          "Refii renderer texture upload capture skipped reason={} "
          "base=0x{:08X} size={} dwords={:08X} {:08X} {:08X} {:08X} "
          "{:08X} {:08X}",
          info.failure_reason ? info.failure_reason
                              : (source_range_valid ? "unknown"
                                                    : "unsafe_range"),
          info.base_address, info.size, dwords[0], dwords[1], dwords[2],
          dwords[3], dwords[4], dwords[5]);
      ++g_observer.logged_texture_capture_failures;
    }
    return false;
  }

  const uint8_t* source =
      base + info.base_address + GuestPhysicalHostOffset(info.base_address);
  if (record.upload_data.size() == info.size &&
      std::memcmp(record.upload_data.data(), source, info.size) == 0) {
    return true;
  }

  record.upload_data.resize(info.size);
  std::memcpy(record.upload_data.data(), source, info.size);
  ++record.upload_generation;
  record.raw_size = std::max<uint32_t>(record.raw_size, info.size);
  if (RendererResourceTracingEnabled() &&
      g_observer.logged_texture_capture_successes < 8) {
    REXLOG_INFO(
        "Refii renderer texture upload capture copied guest=0x{:08X} "
        "source=0x{:08X} size={} generation={}",
        record.guest_address, info.base_address, info.size,
        record.upload_generation);
    ++g_observer.logged_texture_capture_successes;
  }
  return true;
}

bool RefreshTextureUploadFromPendingPixDecodedFrameLocked(
    RendererResourceRecord& record, const std::array<uint32_t, 6>& dwords,
    uint8_t* base) {
  if (!base || record.kind != RendererResourceKind::kTexture ||
      g_observer.pending_pix_decoded_video_frames.empty()) {
    return false;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (info.base_address == 0 || info.size == 0) {
    return false;
  }

  const auto pending_it =
      g_observer.pending_pix_decoded_video_frames.find(info.base_address);
  if (pending_it == g_observer.pending_pix_decoded_video_frames.end()) {
    return false;
  }

  const uint64_t previous_generation = record.upload_generation;
  if (!CaptureTextureUploadFromFetchDwords(record, dwords, base) ||
      record.upload_generation == previous_generation) {
    return false;
  }

  record.serial = ++g_observer.serial;
  g_observer.last_resource = record;
  RefreshQueuedDrawTextureResourceSerialLocked(record);
  return true;
}

void CapturePixDecodedVideoFrameUploadLocked(const RendererHookArgs& args,
                                             uint32_t result, uint8_t* base) {
  if (!base || result != 0 || args.r8 == 0 ||
      !IsLikelyGuestDwordAddress(args.r8)) {
    return;
  }

  // PixDecodeVideoFrame writes a Pix metadata block, not a texture fetch:
  // dwords 0..5 are marker, flag, width, height, frame base, and alpha.
  // Match the decoded frame base against fetch constants captured from the
  // later PixRender path, then refresh the existing texture upload.
  const uint32_t frame_width = LoadGuestU32(base, args.r8 + 8u);
  const uint32_t frame_height = LoadGuestU32(base, args.r8 + 12u);
  const uint32_t frame_base = LoadGuestU32(base, args.r8 + 16u);
  if (frame_width == 0 || frame_height == 0 || frame_width > 4096u ||
      frame_height > 4096u || frame_base == 0) {
    return;
  }

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_pix_decode_packet_shapes < 12u) {
    const bool chunks_readable = IsLikelyGuestDwordAddress(args.r5);
    const uint32_t first_chunk_pointer =
        chunks_readable ? LoadGuestU32(base, args.r5) : 0;
    const uint32_t chunk_virtual = first_chunk_pointer + args.r7;
    const uint32_t chunk =
        chunks_readable
            ? ((((chunk_virtual >> 20u) + 512u) & 0x1000u) +
               (chunk_virtual & 0x1FFFFFFFu) - 0x40000000u)
            : 0;
    const bool chunk_readable = IsLikelyGuestDwordAddress(chunk + 64u);
    const uint32_t chunk_q0_hi = chunk_readable ? LoadGuestU32(base, chunk) : 0;
    const uint32_t chunk_q0_lo =
        chunk_readable ? LoadGuestU32(base, chunk + 4u) : 0;
    const uint32_t chunk_flags =
        chunk_readable ? LoadGuestU32(base, chunk + 12u) : 0;
    const uint32_t chunk_width =
        chunk_readable ? LoadGuestU32(base, chunk + 16u) : 0;
    const uint32_t chunk_height =
        chunk_readable ? LoadGuestU32(base, chunk + 20u) : 0;
    const uint32_t chunk_scale_u =
        chunk_readable ? LoadGuestU32(base, chunk + 24u) : 0;
    const uint32_t chunk_scale_v =
        chunk_readable ? LoadGuestU32(base, chunk + 28u) : 0;
    const uint32_t chunk_alpha =
        chunk_readable ? LoadGuestU32(base, chunk + 32u) : 0;
    std::array<uint32_t, 8> sample = {};
    if (chunk_readable) {
      for (uint32_t i = 0; i < sample.size(); ++i) {
        sample[i] = LoadGuestU32(base, chunk + 36u + i * sizeof(uint32_t));
      }
    }
    REXLOG_INFO(
        "Refii renderer PixDecode packet #{} result=0x{:08X} "
        "chunks=0x{:08X}/{} first=0x{:08X} offset=0x{:08X} "
        "chunk=0x{:08X} readable={} q0={:08X}/{:08X} flags=0x{:08X} "
        "chunk_size={}x{} scale={:08X}/{:08X} alpha={:08X} "
        "frame=0x{:08X} {}x{} out=0x{:08X}/{} "
        "sample={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        g_observer.logged_pix_decode_packet_shapes + 1u, result, args.r5,
        chunks_readable ? 1 : 0, first_chunk_pointer, args.r7, chunk,
        chunk_readable ? 1 : 0,
        chunk_q0_hi, chunk_q0_lo, chunk_flags, chunk_width, chunk_height,
        chunk_scale_u, chunk_scale_v, chunk_alpha, frame_base, frame_width,
        frame_height, args.r8, args.r9, sample[0], sample[1], sample[2],
        sample[3], sample[4], sample[5], sample[6], sample[7]);
    ++g_observer.logged_pix_decode_packet_shapes;
  }

  ++g_observer.pix_decode_frames;
  g_observer.last_pix_decode_base = frame_base;
  g_observer.last_pix_decode_width = frame_width;
  g_observer.last_pix_decode_height = frame_height;
  g_observer.pending_pix_decoded_video_frames[frame_base] = {
      .width = frame_width,
      .height = frame_height,
      .frame_base = frame_base,
      .serial = ++g_observer.serial,
  };
  while (g_observer.pending_pix_decoded_video_frames.size() >
         kMaxPendingPixDecodedVideoFrames) {
    auto oldest = g_observer.pending_pix_decoded_video_frames.begin();
    for (auto it = std::next(oldest);
         it != g_observer.pending_pix_decoded_video_frames.end(); ++it) {
      if (it->second.serial < oldest->second.serial) {
        oldest = it;
      }
    }
    g_observer.pending_pix_decoded_video_frames.erase(oldest);
  }

  for (auto& [address, record] : g_observer.resources) {
    (void)address;
    if (record.kind != RendererResourceKind::kTexture) {
      continue;
    }

    const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
    RefreshTextureUploadFromPendingPixDecodedFrameLocked(record, dwords, base);
  }
}

bool IsShaderResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kVertexShader ||
         kind == RendererResourceKind::kPixelShader;
}

bool IsBufferResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kVertexBuffer ||
         kind == RendererResourceKind::kIndexBuffer;
}

bool IsImageResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kTexture ||
         kind == RendererResourceKind::kSurfaceView ||
         kind == RendererResourceKind::kRenderTargetSurface ||
         kind == RendererResourceKind::kDepthSurface;
}

bool IsSurfaceResourceKind(RendererResourceKind kind) {
  return kind == RendererResourceKind::kSurfaceView ||
         kind == RendererResourceKind::kRenderTargetSurface ||
         kind == RendererResourceKind::kDepthSurface;
}

void SanitizeImageResourceParent(RendererResourceRecord& record) {
  if (!IsImageResourceKind(record.kind)) {
    return;
  }
  if (IsLikelyFableCodeAddress(record.parent_address)) {
    record.parent_address = 0;
  }
}

bool FableSetSurfaceHeaderFormatIsDepth(uint32_t d3d_format) {
  const uint32_t format = d3d_format & 0x3Fu;
  switch (format) {
    case 7u:   // D3DFMT_D32
    case 9u:   // D3DFMT_D15S1
    case 11u:  // D3DFMT_D24S8
    case 13u:  // D3DFMT_D24X8
    case 15u:  // D3DFMT_D24X4S4
    case 16u:  // D3DFMT_D16
    case 18u:  // D3DFMT_D32F_LOCKABLE
    case 19u:  // D3DFMT_D24FS8
    case 20u:  // D3DFMT_D32_LOCKABLE
    case 22u:
    case 23u:
      return true;
    default:
      return false;
  }
}

bool FableSurfaceHeaderFormatIsAmbiguousColorDepth(uint32_t d3d_format) {
  return (d3d_format & 0x3Fu) == 6u;
}

bool IsPlausibleFableSurfaceExtent(uint32_t width, uint32_t height) {
  constexpr uint32_t kMaxFableSurfaceExtent = 2048;
  constexpr uint64_t kMaxFableSurfacePixels =
      static_cast<uint64_t>(kMaxFableSurfaceExtent) *
      static_cast<uint64_t>(kMaxFableSurfaceExtent);
  return width != 0 && height != 0 && width <= kMaxFableSurfaceExtent &&
         height <= kMaxFableSurfaceExtent &&
         static_cast<uint64_t>(width) * height <= kMaxFableSurfacePixels;
}

bool IsUploadResourceKind(RendererResourceKind kind) {
  return IsBufferResourceKind(kind) || IsImageResourceKind(kind);
}

bool CopyGuestBufferUploadData(RendererResourceRecord& record, uint8_t* base,
                               uint32_t guest_address, uint32_t size) {
  if (!base || !IsUploadResourceKind(record.kind) || guest_address == 0 ||
      size == 0) {
    return false;
  }

  const uint32_t record_size =
      record.raw_size != 0 ? record.raw_size : size;
  const uint32_t copy_size = std::min(size, record_size);
  const uint32_t max_upload_size =
      IsImageResourceKind(record.kind) ? kMaxTextureUploadSnapshotSize
                                       : kMaxScratchUploadSnapshotSize;
  if (copy_size == 0 || copy_size > max_upload_size) {
    return false;
  }

  const uint8_t* src =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  if (record.upload_data.size() == copy_size) {
    const uint8_t* current = record.upload_data.data();
    bool unchanged = true;
    if (IsImageResourceKind(record.kind)) {
      unchanged = std::memcmp(current, src, copy_size) == 0;
    } else if (record.kind == RendererResourceKind::kIndexBuffer &&
               record.raw_stride_or_format == 2u) {
      uint32_t offset = 0;
      for (; offset + 1u < copy_size; offset += 2u) {
        if (current[offset + 0u] != src[offset + 1u] ||
            current[offset + 1u] != src[offset + 0u]) {
          unchanged = false;
          break;
        }
      }
      if (unchanged && offset < copy_size && current[offset] != src[offset]) {
        unchanged = false;
      }
    } else {
      uint32_t offset = 0;
      for (; offset + 3u < copy_size; offset += 4u) {
        if (current[offset + 0u] != src[offset + 3u] ||
            current[offset + 1u] != src[offset + 2u] ||
            current[offset + 2u] != src[offset + 1u] ||
            current[offset + 3u] != src[offset + 0u]) {
          unchanged = false;
          break;
        }
      }
      for (; unchanged && offset < copy_size; ++offset) {
        if (current[offset] != src[offset]) {
          unchanged = false;
        }
      }
    }
    if (unchanged) {
      return false;
    }
  }
  record.upload_data.resize(copy_size);
  uint8_t* dst = record.upload_data.data();

  const auto copy_swapped16 = [&]() {
    uint32_t offset = 0;
    for (; offset + 1u < copy_size; offset += 2u) {
      dst[offset + 0u] = src[offset + 1u];
      dst[offset + 1u] = src[offset + 0u];
    }
    if (offset < copy_size) {
      dst[offset] = src[offset];
    }
  };

  const auto copy_swapped32 = [&]() {
    uint32_t offset = 0;
    for (; offset + 3u < copy_size; offset += 4u) {
      dst[offset + 0u] = src[offset + 3u];
      dst[offset + 1u] = src[offset + 2u];
      dst[offset + 2u] = src[offset + 1u];
      dst[offset + 3u] = src[offset + 0u];
    }
    for (; offset < copy_size; ++offset) {
      dst[offset] = src[offset];
    }
  };

  if (IsImageResourceKind(record.kind)) {
    std::memcpy(dst, src, copy_size);
  } else if (record.kind == RendererResourceKind::kIndexBuffer &&
             record.raw_stride_or_format == 2u) {
    copy_swapped16();
  } else {
    copy_swapped32();
  }

  ++record.upload_generation;
  return true;
}

bool HasNonZeroUploadData(const RendererResourceRecord& record) {
  return std::any_of(record.upload_data.begin(), record.upload_data.end(),
                     [](uint8_t value) { return value != 0; });
}

bool IsPreferredBufferResourceMatch(const RendererResourceRecord& record) {
  if (!IsBufferResourceKind(record.kind)) {
    return !record.scratch;
  }
  return !record.scratch && HasNonZeroUploadData(record);
}

bool CaptureFableBufferUploadFromHeader(RendererResourceRecord& record,
                                        uint8_t* base) {
  if (!base || !IsBufferResourceKind(record.kind) ||
      record.guest_address == 0) {
    return false;
  }

  // Fable's SetStreamSource resolves D3D buffer wrappers through +0x18/+0x1C
  // with the same PM4/Xenos address transform used by command packets.
  const uint32_t header_candidates[] = {
      record.guest_address,
      record.guest_address >= 8u ? record.guest_address - 8u : 0u,
      record.guest_address + 8u,
      record.raw_buffer_object,
      record.raw_buffer_object >= 8u ? record.raw_buffer_object - 8u : 0u,
      record.raw_buffer_object + 8u,
      record.parent_address,
      record.parent_address >= 8u ? record.parent_address - 8u : 0u,
      record.parent_address + 8u,
      record.raw_parent24,
      record.raw_parent24 >= 8u ? record.raw_parent24 - 8u : 0u,
      record.raw_parent24 + 8u,
  };
  uint32_t header_address = 0;
  uint32_t gpu_address = 0;
  uint32_t gpu_size = 0;
  for (uint32_t candidate : header_candidates) {
    if (!IsLikelyFableObjectAddress(candidate)) {
      continue;
    }
    uint32_t candidate_gpu_address = DecodeFableGpuBufferPointer(
        LoadGuestU32(base, candidate + 0x18));
    uint32_t candidate_gpu_size =
        DecodeFablePackedBufferSize(LoadGuestU32(base, candidate + 0x1C));
    if (!IsLikelyGpuByteBufferRange(candidate_gpu_address,
                                    candidate_gpu_size)) {
      // XGSetVertexBufferHeader writes a compact XG-style header where +0x18
      // carries size/flags and +0x1C carries the packed GPU base.
      candidate_gpu_address = DecodeFableGpuBufferPointer(
          LoadGuestU32(base, candidate + 0x1C));
      candidate_gpu_size = DecodeFablePackedBufferSize(
          LoadGuestU32(base, candidate + 0x18) & ~3u);
    }
    if (!IsLikelyGpuByteBufferRange(candidate_gpu_address,
                                    candidate_gpu_size)) {
      continue;
    }
    header_address = candidate;
    gpu_address = candidate_gpu_address;
    gpu_size = candidate_gpu_size;
    break;
  }
  if (header_address == 0) {
    return false;
  }

  if (record.raw_parent24 == 0 || record.raw_parent24 == record.guest_address) {
    record.raw_parent24 = header_address;
  }
  record.raw_buffer_object = gpu_address;
  record.raw_size = gpu_size;
  CopyGuestBufferUploadData(record, base, gpu_address,
                            std::min(record.raw_size, gpu_size));
  return !record.upload_data.empty();
}

bool ResourceRecordMatchesAddress(const RendererResourceRecord& record,
                                  uint32_t address) {
  if (address == 0) {
    return false;
  }
  if (record.guest_address == address || record.raw_buffer_object == address ||
      record.raw_parent24 == address || record.parent_address == address) {
    return true;
  }
  const uint32_t aligned_address = address & ~3u;
  if (IsBufferResourceKind(record.kind) && aligned_address != address &&
      (record.raw_buffer_object == aligned_address ||
       record.raw_parent24 == aligned_address)) {
    return true;
  }
  if (!IsBufferResourceKind(record.kind) || record.raw_buffer_object == 0 ||
      record.raw_size == 0 || address < record.raw_buffer_object) {
    if (aligned_address == address || aligned_address < record.raw_buffer_object) {
      return false;
    }
    return static_cast<uint64_t>(aligned_address - record.raw_buffer_object) <
           record.raw_size;
  }
  return static_cast<uint64_t>(address - record.raw_buffer_object) <
         record.raw_size;
}

RendererResourceRecord* FindResourceRecordLocked(uint32_t address) {
  if (address == 0) {
    return nullptr;
  }

  auto it = g_observer.resources.find(address);
  if (it != g_observer.resources.end() &&
      (!it->second.scratch || !IsBufferResourceKind(it->second.kind))) {
    if (!IsBufferResourceKind(it->second.kind) ||
        HasNonZeroUploadData(it->second)) {
      return &it->second;
    }
  }

  RendererResourceRecord* fallback = it != g_observer.resources.end()
                                         ? &it->second
                                         : nullptr;
  for (auto& [guest_address, record] : g_observer.resources) {
    (void)guest_address;
    if (ResourceRecordMatchesAddress(record, address)) {
      if (IsPreferredBufferResourceMatch(record)) {
        return &record;
      }
      if (fallback == nullptr) {
        fallback = &record;
      }
    }
  }

  return fallback;
}

std::unordered_map<uint32_t, RendererResourceRecord>::iterator
FindResourceIteratorLocked(uint32_t address) {
  if (address == 0) {
    return g_observer.resources.end();
  }

  auto it = g_observer.resources.find(address);
  if (it != g_observer.resources.end() &&
      (!it->second.scratch || !IsBufferResourceKind(it->second.kind))) {
    if (!IsBufferResourceKind(it->second.kind) ||
        HasNonZeroUploadData(it->second)) {
      return it;
    }
  }

  auto fallback = it;
  for (auto candidate = g_observer.resources.begin();
       candidate != g_observer.resources.end(); ++candidate) {
    if (ResourceRecordMatchesAddress(candidate->second, address)) {
      if (IsPreferredBufferResourceMatch(candidate->second)) {
        return candidate;
      }
      if (fallback == g_observer.resources.end()) {
        fallback = candidate;
      }
    }
  }

  return fallback;
}

uint32_t TextureSnapshotSizeForRelatedRecordLocked(
    const RendererResourceRecord& record) {
  const uint32_t direct_size = TextureSnapshotSizeForRecord(record);
  if (direct_size != 0) {
    return direct_size;
  }

  const uint32_t related_addresses[] = {
      record.parent_address,
      record.raw_parent24,
      record.raw_buffer_object,
  };
  for (uint32_t related_address : related_addresses) {
    if (related_address == 0 || related_address == record.guest_address) {
      continue;
    }
    auto related_it = g_observer.resources.find(related_address);
    if (related_it == g_observer.resources.end()) {
      continue;
    }
    const uint32_t related_size =
        TextureSnapshotSizeForRecord(related_it->second);
    if (related_size != 0) {
      return related_size;
    }
  }

  return 0;
}

void CaptureLockedBufferUpload(RendererResourceRecord& record, uint8_t* base) {
  if (!base || !IsUploadResourceKind(record.kind) ||
      record.locked_address == 0 || record.locked_size == 0) {
    return;
  }

  const uint32_t record_size =
      record.raw_size != 0 ? record.raw_size : record.locked_size;
  const uint32_t upload_size = std::min(record.locked_size, record_size);
  if (upload_size == 0) {
    return;
  }

  const uint64_t upload_generation_before = record.upload_generation;
  uint32_t upload_address = record.locked_address;
  const uint32_t decoded_address =
      DecodeFableGpuBufferPointer(record.locked_address);
  const auto is_upload_alias_address = [&](uint32_t address) {
    return IsLikelyGpuByteBufferRange(address, upload_size) ||
           IsLikelyHighScratchByteBufferRange(address, upload_size);
  };
  const auto first_nonzero = [&](uint32_t address) {
    return is_upload_alias_address(address)
               ? FindFirstNonZeroGuestByte(base, address, upload_size,
                                           upload_size)
               : UINT32_MAX;
  };
  if (!record.scratch && IsImageResourceKind(record.kind)) {
    const uint32_t raw_nonzero = first_nonzero(record.locked_address);
    const uint32_t decoded_nonzero = first_nonzero(decoded_address);
    if (IsLikelyGpuByteBufferRange(decoded_address, upload_size)) {
      upload_address = decoded_address;
    }
    if (raw_nonzero != UINT32_MAX && decoded_nonzero == UINT32_MAX) {
      upload_address = record.locked_address;
    }
  } else if (!record.scratch && IsBufferResourceKind(record.kind)) {
    const uint32_t raw_nonzero = FindFirstNonZeroGuestByte(
        base, record.locked_address, upload_size, 64u * 1024u);
    const uint32_t decoded_nonzero = FindFirstNonZeroGuestByte(
        base, decoded_address, upload_size, 64u * 1024u);
    if (record.raw_buffer_object != 0 && record.raw_size != 0 &&
        raw_nonzero == UINT32_MAX && decoded_nonzero != UINT32_MAX &&
        decoded_address >= record.raw_buffer_object &&
        static_cast<uint64_t>(decoded_address - record.raw_buffer_object) <
            record.raw_size) {
      upload_address = decoded_address;
    }
  }
  if (IsImageResourceKind(record.kind) &&
      (RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_image_lock_uploads < 16u) {
    const uint32_t raw_nonzero = first_nonzero(record.locked_address);
    const uint32_t decoded_nonzero = first_nonzero(decoded_address);
    const uint32_t chosen_nonzero = first_nonzero(upload_address);
    const uint32_t raw_word0 =
        raw_nonzero != UINT32_MAX ? LoadGuestU32(base, record.locked_address)
                                  : 0u;
    const uint32_t decoded_word0 =
        decoded_nonzero != UINT32_MAX ? LoadGuestU32(base, decoded_address)
                                      : 0u;
    const uint32_t chosen_word0 =
        chosen_nonzero != UINT32_MAX ? LoadGuestU32(base, upload_address) : 0u;
    REXLOG_INFO(
        "Refii renderer image lock upload #{} guest=0x{:08X} kind={} "
        "raw=0x{:08X} decoded=0x{:08X} chosen=0x{:08X} size={} "
        "nz raw/decoded/chosen={}/{}/{} word0 raw/decoded/chosen="
        "{:08X}/{:08X}/{:08X} gen_before={}",
        g_observer.logged_image_lock_uploads + 1u, record.guest_address,
        static_cast<uint32_t>(record.kind), record.locked_address,
        decoded_address, upload_address, upload_size,
        raw_nonzero == UINT32_MAX ? -1 : static_cast<int32_t>(raw_nonzero),
        decoded_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(decoded_nonzero),
        chosen_nonzero == UINT32_MAX ? -1
                                     : static_cast<int32_t>(chosen_nonzero),
        raw_word0, decoded_word0, chosen_word0, upload_generation_before);
    ++g_observer.logged_image_lock_uploads;
  }
  CopyGuestBufferUploadData(record, base, upload_address, upload_size);
  if (record.upload_generation != upload_generation_before) {
    record.serial = ++g_observer.serial;
    RefreshQueuedDrawBufferResourceSerialLocked(record);
  }
  record.locked_address = 0;
  record.locked_size = 0;
}

uint32_t LockedResourceSizeForHook(std::string_view symbol,
                                   const RendererHookArgs& args,
                                   const RendererResourceRecord& record) {
  if (symbol == "rex_lhGfx_LockBuffer" && args.r10 != 0) {
    return args.r10;
  }
  if (IsImageResourceKind(record.kind)) {
    const uint32_t texture_size =
        TextureSnapshotSizeForRelatedRecordLocked(record);
    if (texture_size != 0) {
      return texture_size;
    }
  }
  if (record.raw_size != 0) {
    return record.raw_size;
  }
  return args.r10;
}

void SnapshotGuestResource(RendererResourceRecord& record, uint8_t* base);
void TraceResourceCreate(const RendererResourceRecord& record);

bool LooksLikeTextureLockResource(std::string_view symbol,
                                  const RendererHookArgs& args,
                                  uint8_t* base) {
  if (!base || symbol != "rex_lhGfx_LockBuffer" ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return false;
  }

  // D3D::LockSurface funnels through LockResource/rex_lhGfx_LockBuffer with
  // block type 14 after resolving the D3D surface to its base texture. Treat
  // that shape as a texture upload producer instead of creating a scratch VB.
  constexpr uint32_t kFableSurfaceLockBlockType = 14;
  if (args.r5 != kFableSurfaceLockBlockType) {
    return false;
  }

  return HasTextureFetchDwords(LoadTextureFetchDwords(base, args.r3 + 28u));
}

bool EnsureTextureRecordForLockLocked(std::string_view symbol,
                                      const RendererHookArgs& args,
                                      uint8_t* base,
                                      RendererResourceRecord*& record) {
  if (!LooksLikeTextureLockResource(symbol, args, base)) {
    return false;
  }

  auto it = g_observer.resources.find(args.r3);
  if (it != g_observer.resources.end() && IsImageResourceKind(it->second.kind)) {
    record = &it->second;
    return true;
  }

  RendererResourceRecord created;
  created.kind = RendererResourceKind::kTexture;
  created.guest_address = args.r3;
  created.parent_address = args.r3;
  SnapshotGuestResource(created, base);
  created.serial = ++g_observer.serial;

  auto [inserted, inserted_new] =
      g_observer.resources.emplace(created.guest_address, created);
  if (!inserted_new) {
    inserted->second = std::move(created);
  } else {
    ++g_observer.resource_creates;
    TraceResourceCreate(inserted->second);
  }

  g_observer.last_resource = inserted->second;
  record = &inserted->second;
  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_texture_lock_records < 16u) {
    REXLOG_INFO(
        "Refii renderer texture lock record #{} object=0x{:08X} "
        "result_block={} packed={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        g_observer.logged_texture_lock_records + 1u, args.r3, args.r5,
        record->raw_packed28, record->raw_packed32, record->raw_packed36,
        record->raw_packed40, record->raw_packed44, record->raw_packed48);
    ++g_observer.logged_texture_lock_records;
  }
  return true;
}

bool ValidateVertexDeclarationElements(uint8_t* base, uint32_t elements_address,
                                       uint32_t element_count);

bool PopulateVertexDeclarationRecordFromObject(RendererResourceRecord& record,
                                               uint8_t* base) {
  if (!base || record.guest_address == 0 ||
      record.kind != RendererResourceKind::kVertexDeclaration) {
    return false;
  }

  const uint32_t element_count = LoadGuestU32(base, record.guest_address + 0x18);
  if (element_count == 0 ||
      element_count > kMaxReasonableFableVertexDeclarationElements) {
    return false;
  }

  const uint32_t element_bytes = element_count * 12u;
  const uint32_t elements_address = record.guest_address + 0x34;
  if (!ValidateVertexDeclarationElements(base, elements_address,
                                         element_count)) {
    return false;
  }

  record.raw_buffer_object = elements_address;
  record.raw_size = element_bytes;
  record.raw_stride_or_format = element_count;
  record.raw_packed28 = LoadGuestU32(base, record.guest_address + 0x1C);
  record.raw_packed32 = LoadGuestU32(base, record.guest_address + 0x20);
  record.raw_packed48 = LoadGuestU32(base, record.guest_address + 0x30);
  record.raw_hash = HashGuestBytesFnv1a64(base, elements_address, element_bytes);
  record.upload_data.resize(element_bytes);
  std::memcpy(record.upload_data.data(),
              base + elements_address + GuestPhysicalHostOffset(elements_address),
              element_bytes);
  ++record.upload_generation;
  return record.raw_hash != 0;
}

bool IsPlausibleVertexDeclarationType(uint32_t type) {
  switch (type) {
    case 0x2C83A4:
    case 0x2C23A5:
    case 0x2A23B9:
    case 0x1A23A6:
    case 0x182886:
    case 0x1A2286:
    case 0x1A2386:
    case 0x2C2359:
    case 0x1A235A:
    case 0x1A2086:
    case 0x1A2186:
    case 0x2C2159:
    case 0x1A215A:
    case 0x2C2059:
    case 0x1A205A:
    case 0x2C82A1:
    case 0x2C83A1:
    case 0x2C23A2:
    case 0x1A23A3:
    case 0x1A2187:
    case 0x1A2287:
    case 0x2A2187:
    case 0x2A2287:
    case 0x2A2190:
    case 0x2A2390:
    case 0x2C235F:
    case 0x1A2360:
      return true;
    default:
      return false;
  }
}

bool ValidateVertexDeclarationElements(uint8_t* base, uint32_t elements_address,
                                       uint32_t element_count) {
  if (!base || elements_address == 0 || element_count == 0 ||
      element_count > kMaxReasonableFableVertexDeclarationElements) {
    return false;
  }

  for (uint32_t i = 0; i < element_count; ++i) {
    const uint32_t element_address = elements_address + i * 12u;
    const uint16_t stream = LoadGuestU16(base, element_address + 0u);
    const uint16_t offset = LoadGuestU16(base, element_address + 2u);
    const uint32_t type = LoadGuestU32(base, element_address + 4u);
    const uint8_t usage = LoadGuestU8(base, element_address + 9u);
    const uint8_t usage_index = LoadGuestU8(base, element_address + 10u);
    if (stream >= 16u || offset > 4096u || usage > 13u ||
        usage_index > 15u || !IsPlausibleVertexDeclarationType(type)) {
      return false;
    }
  }

  return true;
}

bool PopulateVertexDeclarationRecordFromElements(
    RendererResourceRecord& record, uint8_t* base, uint32_t elements_address) {
  if (!base || elements_address == 0 ||
      record.kind != RendererResourceKind::kVertexDeclaration) {
    return false;
  }

  uint32_t element_count = 0;
  uint32_t max_stream = 0;
  uint32_t stream_mask = 0;
  for (; element_count < kMaxTrackedVertexDeclarationElements;
       ++element_count) {
    const uint32_t element_address = elements_address + element_count * 12u;
    const uint16_t stream = LoadGuestU16(base, element_address + 0u);
    const uint16_t offset = LoadGuestU16(base, element_address + 2u);
    const uint32_t type = LoadGuestU32(base, element_address + 4u);
    const uint8_t usage = LoadGuestU8(base, element_address + 9u);
    const uint8_t usage_index = LoadGuestU8(base, element_address + 10u);
    if (stream == 0xFFu || type == 0xFFFFFFFFu) {
      break;
    }
    if (stream >= 16u || offset > 4096u || usage > 13u ||
        usage_index > 15u || !IsPlausibleVertexDeclarationType(type)) {
      return false;
    }
    stream_mask |= 1u << stream;
    max_stream = std::max<uint32_t>(max_stream, stream);
  }

  if (element_count == 0 ||
      element_count >= kMaxTrackedVertexDeclarationElements ||
      element_count > kMaxReasonableFableVertexDeclarationElements) {
    return false;
  }

  const uint32_t element_bytes = element_count * 12u;
  record.raw_buffer_object = elements_address;
  record.raw_size = element_bytes;
  record.raw_stride_or_format = element_count;
  record.raw_packed28 = max_stream;
  record.raw_packed32 = stream_mask;
  record.raw_hash = HashGuestBytesFnv1a64(base, elements_address, element_bytes);
  record.upload_data.resize(element_bytes);
  std::memcpy(record.upload_data.data(),
              base + elements_address +
                  GuestPhysicalHostOffset(elements_address),
              element_bytes);
  ++record.upload_generation;
  return record.raw_hash != 0;
}

bool PopulateVertexDeclarationRecordFromNearbyObject(
    RendererResourceRecord& record, uint8_t* base) {
  if (!base || record.guest_address == 0 ||
      record.kind != RendererResourceKind::kVertexDeclaration) {
    return false;
  }

  auto adopt_candidate = [&](RendererResourceRecord& candidate) {
    record.raw_buffer_object = candidate.raw_buffer_object;
    record.raw_size = candidate.raw_size;
    record.raw_stride_or_format = candidate.raw_stride_or_format;
    record.raw_packed28 = candidate.raw_packed28;
    record.raw_packed32 = candidate.raw_packed32;
    record.raw_packed48 = candidate.raw_packed48;
    record.raw_hash = candidate.raw_hash;
    record.upload_generation = candidate.upload_generation;
    record.upload_data = std::move(candidate.upload_data);
  };

  constexpr uint32_t kPointerCandidateOffsets[] = {
      0x00u, 0x04u, 0x08u, 0x10u, 0x18u, 0x20u,
      0x24u, 0x30u, 0x38u, 0x3Cu, 0x40u, 0x44u, 0x48u};
  for (const uint32_t pointer_offset : kPointerCandidateOffsets) {
    const uint32_t candidate_address =
        LoadGuestU32(base, record.guest_address + pointer_offset);
    if (candidate_address == 0 || candidate_address == record.guest_address ||
        !IsLikelyFableObjectAddress(candidate_address)) {
      continue;
    }

    RendererResourceRecord candidate = record;
    candidate.guest_address = candidate_address;
    if (PopulateVertexDeclarationRecordFromObject(candidate, base) ||
        PopulateVertexDeclarationRecordFromElements(candidate, base,
                                                    candidate_address) ||
        PopulateVertexDeclarationRecordFromElements(candidate, base,
                                                    candidate_address + 0x34u)) {
      adopt_candidate(candidate);
      if (record.parent_address == 0) {
        record.parent_address = candidate_address;
      }
      return true;
    }
  }

  constexpr int32_t kCandidateOffsets[] = {
      -0x40, -0x30, -0x20, -0x10, 0x10, 0x20, 0x30, 0x40};
  const uint32_t original_address = record.guest_address;
  for (const int32_t offset : kCandidateOffsets) {
    const uint32_t candidate_address =
        static_cast<uint32_t>(original_address + offset);
    if (candidate_address == 0) {
      continue;
    }

    RendererResourceRecord candidate = record;
    candidate.guest_address = candidate_address;
    if (!PopulateVertexDeclarationRecordFromObject(candidate, base)) {
      continue;
    }

    adopt_candidate(candidate);
    if (record.parent_address == 0) {
      record.parent_address = candidate_address;
    }
    return true;
  }

  return false;
}

bool HasPlausibleSurfaceHeader(const RendererResourceRecord& record);
bool SnapshotInlineSurfaceDescriptor(RendererResourceRecord& record,
                                     uint8_t* base);

void SnapshotGuestResource(RendererResourceRecord& record, uint8_t* base) {
  if (!base || record.guest_address == 0) {
    return;
  }

  if (IsBufferResourceKind(record.kind)) {
    record.raw_buffer_object = LoadGuestU32(base, record.guest_address + 0);
    record.raw_size = LoadGuestU32(base, record.guest_address + 4);
    record.raw_stride_or_format =
        record.kind == RendererResourceKind::kVertexBuffer
            ? LoadGuestU8(base, record.guest_address + 8)
            : LoadGuestU32(base, record.guest_address + 8);
    CaptureFableBufferUploadFromHeader(record, base);
    return;
  }
  if (IsShaderResourceKind(record.kind)) {
    return;
  }
  if (record.kind == RendererResourceKind::kVertexDeclaration) {
    if (PopulateVertexDeclarationRecordFromObject(record, base)) {
      return;
    }
    if (PopulateVertexDeclarationRecordFromElements(record, base,
                                                   record.guest_address)) {
      return;
    }
    if (PopulateVertexDeclarationRecordFromElements(
            record, base, record.guest_address + 0x34u)) {
      return;
    }
    PopulateVertexDeclarationRecordFromNearbyObject(record, base);
    return;
  }

  record.raw_flags = LoadGuestU32(base, record.guest_address + 0);
  record.raw_ref_count = LoadGuestU32(base, record.guest_address + 4);
  record.raw_aux20 = LoadGuestU32(base, record.guest_address + 20);
  record.raw_parent24 = LoadGuestU32(base, record.guest_address + 24);
  record.raw_packed28 = LoadGuestU32(base, record.guest_address + 28);
  if (IsSurfaceResourceKind(record.kind)) {
    // Fable D3DSurface headers don't use texture-fetch dword layout:
    // +18 pitch/MSAA, +1C render-target info, +20 depth/stencil info,
    // +24 packed width/height, +28 D3DFORMAT, +2C byte size.
    record.raw_packed28 = LoadGuestU32(
        base, record.guest_address + kFableSurfaceRenderTargetInfoOffset);
    record.raw_packed32 = LoadGuestU32(
        base, record.guest_address + kFableSurfaceDepthStencilInfoOffset);
    record.raw_packed36 = LoadGuestU32(
        base, record.guest_address + kFableSurfacePackedExtentOffset);
    record.raw_packed40 = LoadGuestU32(
        base, record.guest_address + kFableSurfaceD3DFormatOffset);
    record.raw_packed44 = LoadGuestU32(
        base, record.guest_address + kFableSurfacePitchMsaaOffset);
    record.raw_packed48 =
        LoadGuestU32(base, record.guest_address + kFableSurfaceByteSizeOffset);
    if (!HasPlausibleSurfaceHeader(record)) {
      SnapshotInlineSurfaceDescriptor(record, base);
    }
    return;
  }
  if (IsImageResourceKind(record.kind)) {
    record.raw_packed32 = LoadGuestU32(base, record.guest_address + 32);
    record.raw_packed36 = LoadGuestU32(base, record.guest_address + 36);
    record.raw_packed40 = LoadGuestU32(base, record.guest_address + 40);
    record.raw_packed44 = LoadGuestU32(base, record.guest_address + 44);
    record.raw_packed48 = LoadGuestU32(base, record.guest_address + 48);
  }
}

bool PopulateShaderRecordFromObject(RendererResourceRecord& record,
                                    uint8_t* base) {
  if (!base || record.guest_address == 0 ||
      !IsShaderResourceKind(record.kind)) {
    return false;
  }

  const uint32_t previous_bytecode_pointer = record.raw_buffer_object;
  const uint32_t previous_bytecode_size = record.raw_size;
  const uint64_t previous_hash = record.raw_hash;

  uint32_t bytecode_pointer = 0;
  uint32_t bytecode_offset = 0;
  uint32_t bytecode_size = 0;
  if (record.kind == RendererResourceKind::kVertexShader) {
    bytecode_pointer = LoadGuestU32(base, record.guest_address + 32);
    bytecode_offset = LoadGuestU32(base, record.guest_address + 876);
    bytecode_size = LoadGuestU32(base, record.guest_address + 880);
  } else if (record.kind == RendererResourceKind::kPixelShader) {
    bytecode_pointer = LoadGuestU32(base, record.guest_address + 24);
    bytecode_offset = LoadGuestU32(base, record.guest_address + 44);
    bytecode_size = LoadGuestU32(base, record.guest_address + 48);
  }

  if (bytecode_pointer == 0 || bytecode_size == 0 ||
      bytecode_size > kMaxTrackedShaderBytecodeSize) {
    return false;
  }

  record.raw_buffer_object = bytecode_pointer;
  record.raw_size = bytecode_size;
  record.raw_stride_or_format = bytecode_offset;
  if (previous_hash != 0 && previous_bytecode_pointer == bytecode_pointer &&
      previous_bytecode_size == bytecode_size) {
    // Shader create hooks see the full Fable shader container, which is the
    // stable blob shape used by extraction. The live shader object points at
    // the payload. Keep the create-root hash when the object shape still
    // matches so later refreshes do not make a resolvable shader disappear.
    record.raw_hash = previous_hash;
  } else {
    record.raw_hash =
        HashGuestBytesFnv1a64(base, bytecode_pointer, bytecode_size);
  }
  return record.raw_hash != 0;
}

bool PopulateShaderRecordFromCreateArgument(RendererResourceRecord& record,
                                            uint8_t* base,
                                            uint32_t container_address) {
  if (!base || container_address == 0 || !IsShaderResourceKind(record.kind)) {
    return false;
  }

  const uint32_t bytecode_offset = LoadGuestU32(base, container_address + 4);
  const uint32_t bytecode_size = LoadGuestU32(base, container_address + 8);
  if (bytecode_offset == 0 || bytecode_size == 0 ||
      bytecode_size > kMaxTrackedShaderBytecodeSize ||
      bytecode_offset > kMaxTrackedShaderContainerSize ||
      bytecode_size > kMaxTrackedShaderContainerSize - bytecode_offset) {
    return false;
  }

  const uint32_t container_size = bytecode_offset + bytecode_size;
  record.raw_buffer_object = container_address + bytecode_offset;
  record.raw_size = bytecode_size;
  record.raw_stride_or_format = bytecode_offset;
  record.raw_hash =
      HashGuestBytesFnv1a64(base, container_address, container_size);
  return record.raw_hash != 0;
}

bool UpsertPm4ShaderResourceLocked(RendererResourceKind kind,
                                   uint32_t guest_address,
                                   uint32_t byte_size,
                                   uint32_t parent_address,
                                   uint8_t* base) {
  if (!base || guest_address == 0 || byte_size == 0 ||
      byte_size > kMaxTrackedShaderBytecodeSize ||
      !IsShaderResourceKind(kind)) {
    return false;
  }

  RendererResourceRecord* existing = FindResourceRecordLocked(guest_address);
  if (existing && existing->kind != kind) {
    existing = nullptr;
  }

  RendererResourceRecord record;
  const bool created = existing == nullptr;
  if (existing) {
    record = *existing;
  } else {
    record.kind = kind;
    record.guest_address = guest_address;
  }

  record.parent_address = parent_address;
  record.raw_buffer_object = guest_address;
  record.raw_size = byte_size;
  record.raw_stride_or_format = 0;
  record.raw_hash = HashGuestBytesFnv1a64(base, guest_address, byte_size);
  if (record.raw_hash == 0) {
    return false;
  }

  record.serial = ++g_observer.serial;
  g_observer.resources[record.guest_address] = record;
  g_observer.last_resource = record;
  if (created) {
    ++g_observer.resource_creates;
  }
  return true;
}

const char* ResourceKindName(RendererResourceKind kind) {
  switch (kind) {
    case RendererResourceKind::kUnknown:
      return "unknown";
    case RendererResourceKind::kTexture:
      return "texture";
    case RendererResourceKind::kSurfaceView:
      return "surface_view";
    case RendererResourceKind::kRenderTargetSurface:
      return "render_target_surface";
    case RendererResourceKind::kDepthSurface:
      return "depth_surface";
    case RendererResourceKind::kVertexBuffer:
      return "vertex_buffer";
    case RendererResourceKind::kIndexBuffer:
      return "index_buffer";
    case RendererResourceKind::kVertexDeclaration:
      return "vertex_declaration";
    case RendererResourceKind::kVertexShader:
      return "vertex_shader";
    case RendererResourceKind::kPixelShader:
      return "pixel_shader";
  }

  return "unknown";
}

RendererTrackedResourceKind PublicResourceKind(RendererResourceKind kind) {
  switch (kind) {
    case RendererResourceKind::kTexture:
      return RendererTrackedResourceKind::kTexture;
    case RendererResourceKind::kSurfaceView:
    case RendererResourceKind::kRenderTargetSurface:
      return RendererTrackedResourceKind::kSurface;
    case RendererResourceKind::kDepthSurface:
      return RendererTrackedResourceKind::kDepthSurface;
    case RendererResourceKind::kVertexBuffer:
      return RendererTrackedResourceKind::kVertexBuffer;
    case RendererResourceKind::kIndexBuffer:
      return RendererTrackedResourceKind::kIndexBuffer;
    case RendererResourceKind::kVertexDeclaration:
      return RendererTrackedResourceKind::kVertexDeclaration;
    case RendererResourceKind::kVertexShader:
      return RendererTrackedResourceKind::kVertexShader;
    case RendererResourceKind::kPixelShader:
      return RendererTrackedResourceKind::kPixelShader;
    case RendererResourceKind::kUnknown:
      return RendererTrackedResourceKind::kUnknown;
  }

  return RendererTrackedResourceKind::kUnknown;
}

RendererResourceKind CreatedResourceKind(std::string_view symbol) {
  if (symbol == "sub_82B98B28") {
    return RendererResourceKind::kTexture;
  }
  if (symbol == "sub_82B98978" || symbol == "sub_82B98A58" ||
      symbol == "sub_82B98860") {
    return RendererResourceKind::kSurfaceView;
  }
  if (symbol == "sub_821FC8D8") {
    return RendererResourceKind::kRenderTargetSurface;
  }
  if (symbol == "rex_lhTexture_Create") {
    return RendererResourceKind::kRenderTargetSurface;
  }
  if (symbol == "sub_82295380") {
    return RendererResourceKind::kDepthSurface;
  }
  if (symbol == "sub_82B8AB30") {
    // Fable's render/depth surface object constructor owns a nested surface
    // holder at +0x10. The exact color/depth kind is refined from the ctor
    // format argument after the child D3DSurface pointer is read.
    return RendererResourceKind::kRenderTargetSurface;
  }
  if (symbol == "rex_lhGfx_CreateVertexBuffer") {
    return RendererResourceKind::kVertexBuffer;
  }
  if (symbol == "rex_lhGfx_CreateIndexBuffer") {
    return RendererResourceKind::kIndexBuffer;
  }
  if (symbol == "rex_lhGfx_CreateGPUState") {
    return RendererResourceKind::kVertexDeclaration;
  }
  if (symbol == "sub_82B994B8") {
    return RendererResourceKind::kVertexShader;
  }
  if (symbol == "sub_82B992B0") {
    return RendererResourceKind::kPixelShader;
  }

  return RendererResourceKind::kUnknown;
}

bool IsRootImageCreateHook(std::string_view symbol) {
  return symbol == "rex_lhTexture_Create" || symbol == "sub_82B98B28";
}

bool IsLockHook(std::string_view symbol) {
  return symbol == "sub_82B98930" || symbol == "sub_8225F980" ||
         symbol == "sub_82196980" || symbol == "rex_lhGfx_LockBuffer" ||
         symbol == "sub_82278138" || symbol == "sub_82B986C8" ||
         symbol == "sub_82B98778" || symbol == "sub_82B98C48";
}

bool IsUnlockHook(std::string_view symbol) {
  return symbol == "sub_82B98950" || symbol == "sub_82B9EEC0" ||
         symbol == "sub_82B9EED0" || symbol == "rex_lhGfx_FlushBuffer";
}

RendererResourceKind LockedBufferResourceKindForHook(std::string_view symbol) {
  if (symbol == "sub_8225F980" || symbol == "rex_lhGfx_LockBuffer") {
    return RendererResourceKind::kVertexBuffer;
  }
  if (symbol == "sub_82196980") {
    return RendererResourceKind::kIndexBuffer;
  }
  return RendererResourceKind::kUnknown;
}

bool IsPresentHook(std::string_view symbol) {
  return symbol == "rex_lhRenderer_EndScene" || symbol == "sub_82BA34D8" ||
         symbol == "sub_82BA32F8";
}

bool IsDisplaySwapHook(std::string_view symbol) {
  return symbol == "sub_82BA34D8" || symbol == "sub_82BA32F8";
}

bool IsPresentedDrawCaptureHook(std::string_view symbol) {
  return symbol == "rex_lhRenderer_EndScene" || IsDisplaySwapHook(symbol);
}

const char* LumenRendererName(std::string_view symbol) {
  if (symbol == "sub_82B98930") {
    return "LockTextureRect";
  }
  if (symbol == "sub_82B98950") {
    return "UnlockTextureRect";
  }
  if (symbol == "sub_82B9EEC0") {
    return "UnlockVertexBuffer";
  }
  if (symbol == "sub_82B9EED0") {
    return "UnlockIndexBuffer";
  }
  if (symbol == "sub_821FC8D8") {
    return "Fable::CreateSurfaceWrapper";
  }
  if (symbol == "sub_82295380") {
    return "Fable::CreateTiledSurfaceWrapper";
  }
  if (symbol == "sub_82B8AB30") {
    return "Fable::RenderDepthSurfaceCtor";
  }
  if (symbol == "rex_lhTexture_Create") {
    return "D3DDevice_CreateSurface";
  }
  if (symbol == "sub_82B98C88") {
    return "D3DSurface_GetContainer";
  }
  if (symbol == "sub_821FA6C0") {
    return "D3D::FindSurfaceWithinTexture";
  }
  if (symbol == "sub_821FAB38") {
    return "D3D::GetTextureDimensions";
  }
  if (symbol == "sub_821FABD0") {
    return "D3D::Get2DSurfaceDesc";
  }
  if (symbol == "sub_82B985C8") {
    return "D3D::Get3DSurfaceDesc";
  }
  if (symbol == "sub_821FAD60") {
    return "D3D::GetLevelSize";
  }
  if (symbol == "sub_82A4E8D0") {
    return "Fable::BinkVideoPlaneSurfaceSetup";
  }
  if (symbol == "sub_82B6F040") {
    return "Fable::PresentationScratchSetup";
  }
  if (symbol == "sub_82B6F5C8") {
    return "Fable::PresentationScratchTeardown";
  }
  if (symbol == "sub_8226B078") {
    return "Fable::BindDepthSurface";
  }
  if (symbol == "sub_8226E180") {
    return "Fable::SceneStateFlush";
  }
  if (symbol == "sub_8226E738") {
    return "Fable::SceneDrawSubmit";
  }
  if (symbol == "sub_82205F68") {
    return "D3DDevice_BeginVertices";
  }
  if (symbol == "sub_82217DB8") {
    return "D3DDevice_BeginIndexedVertices";
  }
  if (symbol == "sub_82206888") {
    return "D3D::PrivateTextureRectangleDraw";
  }
  if (symbol == "sub_8221B010") {
    return "D3D::SetPending_Shaders";
  }
  if (symbol == "sub_8221AE18") {
    return "D3D::SetPending_HiZEnable";
  }
  if (symbol == "sub_8221C7D8") {
    return "D3D::SetPending_RenderStates";
  }
  if (symbol == "sub_8221D1B0") {
    return "D3D::SetPending_FetchConstants";
  }
  if (symbol == "sub_822154B0") {
    return "D3D::SetPending_Predicated";
  }
  if (symbol == "sub_8228EF80") {
    return "D3D::SetPending_Split";
  }
  if (symbol == "sub_822B4008") {
    return "D3D::SetPending_ClipPlanes";
  }
  if (symbol == "sub_82BA25C0") {
    return "D3D::CDevice::AddCommandsToPrimaryBuffer";
  }
  if (symbol == "sub_8221F2E0") {
    return "D3D::FlushPendingGpuState";
  }
  if (symbol == "sub_8221C3E8") {
    return "D3DDevice_DrawPrimitive";
  }
  if (symbol == "sub_8221DFC0") {
    return "D3DDevice_DrawIndexedPrimitive";
  }
  if (symbol == "sub_822224A8") {
    return "D3DDevice_SetVertexDeclaration";
  }
  if (symbol == "sub_82BA8928") {
    return "D3D::PixDecodeVideoFrame";
  }
  if (symbol == "sub_82BA7898") {
    return "D3D::PixEnableVideoPlayback";
  }
  if (symbol == "sub_82BA7928") {
    return "D3D::PixDisableVideoPlayback";
  }
  if (symbol == "sub_82BA7988") {
    return "D3D::PixTimeSinceVBlank";
  }
  if (symbol == "sub_82BA79D0") {
    return "D3D::CodecSetState";
  }
  if (symbol == "sub_82A42FA0") {
    return "Fable::BinkVideoPlaneSetup";
  }
  if (symbol == "sub_832BCB30") {
    return "BinkFileReadFrame";
  }
  if (symbol == "sub_832BCDE8") {
    return "BinkFileReadFrameAsync";
  }
  if (symbol == "sub_832C49E8") {
    return "ExpandBink";
  }
  if (symbol == "sub_82BA83C0") {
    return "D3D::PixRenderVideoFrame";
  }
  if (symbol == "sub_82BA8EF8") {
    return "D3D::PixRenderVideoFramePacket";
  }
  if (symbol == "sub_82BA95E0") {
    return "D3D::PixCaptureVideoFrame";
  }
  if (symbol == "sub_82BAA848") {
    return "D3D::PixSyncronizeToPresentationInterval";
  }
  if (symbol == "sub_82BAA920") {
    return "D3D::PixInitializePerfMemory";
  }
  if (symbol == "sub_822866E0") {
    return "D3D::CDevice::AddCallsToPrimaryBuffer";
  }
  if (symbol == "sub_822868C8") {
    return "D3D::CDevice::QueueIndirectBuffer";
  }
  if (symbol == "sub_821E8D38") {
    return "D3D::CDevice::KickOffSegment";
  }
  if (symbol == "sub_821E8EC0") {
    return "D3D::CDevice::KickOff";
  }
  if (symbol == "sub_82196628") {
    return "D3DDevice_BeginExport";
  }
  if (symbol == "sub_82196750") {
    return "D3DDevice_EndExport";
  }
  if (symbol == "sub_822C6BA8") {
    return "D3D::InvalidateCaches";
  }
  if (symbol == "sub_8219CD68") {
    return "D3DDevice_SetShaderGPRAllocation";
  }
  if (symbol == "sub_821EC798") {
    return "D3DDevice_SetSamplerState_MipMapLodBias";
  }
  if (symbol == "sub_82202D10") {
    return "D3DDevice_SetSamplerState_AddressU";
  }
  if (symbol == "sub_8222C568") {
    return "D3DDevice_SetSamplerState_AddressV";
  }
  if (symbol == "sub_822373F0") {
    return "D3DDevice_SetSamplerState_AddressW";
  }
  if (symbol == "sub_822383A0") {
    return "D3DDevice_SetSamplerState_MagFilter";
  }
  if (symbol == "sub_82238128") {
    return "D3DDevice_SetSamplerState_MinFilter";
  }
  if (symbol == "sub_8222C818") {
    return "D3DDevice_SetSamplerState_MipFilter";
  }
  if (symbol == "sub_822398D0") {
    return "D3DDevice_SetSamplerState_FilterQuality";
  }
  if (symbol == "sub_821B1330") {
    return "D3DDevice_SetRenderState_AlphaFunc";
  }
  if (symbol == "sub_821B1350") {
    return "D3DDevice_SetRenderState_StencilWriteMask";
  }
  if (symbol == "sub_82225E08") {
    return "D3DDevice_SetRenderState_AlphaRef";
  }
  if (symbol == "sub_8229ABB8") {
    return "D3DDevice_SetRenderState_StencilPass";
  }
  if (symbol == "sub_822A6180") {
    return "D3DDevice_SetRenderState_StencilFunc";
  }
  if (symbol == "sub_822C08E8") {
    return "D3DDevice_SetRenderState_StencilMask";
  }
  if (symbol == "sub_82239910") {
    return "D3DDevice_SetRenderState_StencilRef";
  }
  if (symbol == "sub_821D3570") {
    return "D3D::PatchVertexShaderToMatchVertexDeclaration";
  }
  if (symbol == "sub_821E0220") {
    return "D3D::IncrementalShaderPatchAndLoad";
  }
  if (symbol == "sub_821F7650") {
    return "D3D::DirectShaderPatch";
  }
  if (symbol == "sub_82BA3D88") {
    return "D3D::PatchVertexShaderToMatchPixelShader";
  }
  if (symbol == "sub_831CFA58") {
    return "XGRAPHICS::ProcessVertexInputPatch";
  }
  if (symbol == "rex_XGRegisterVertexShader") {
    return "XGRegisterVertexShader";
  }
  if (symbol == "sub_830FE488") {
    return "XGRAPHICS__ThreadSafeSSMPrecompileShader";
  }
  if (symbol == "sub_831CBAF8") {
    return "XGRAPHICS__TranslateShader";
  }
  if (symbol == "sub_821FBB78") {
    return "XGSetTextureHeader";
  }
  if (symbol == "sub_8300BD38") {
    return "XGSetVertexBufferHeader";
  }
  if (symbol == "sub_8300BDD0") {
    return "XGSetIndexBufferHeader";
  }
  if (symbol == "sub_8300BE60") {
    return "XGSetConstantBufferHeader";
  }
  if (symbol == "sub_8300BEE0") {
    return "XGSetLineTextureHeader";
  }
  if (symbol == "sub_8300BF48") {
    return "XGSetCubeTextureHeader";
  }
  if (symbol == "sub_821F52A0") {
    return "XGSurfaceSize";
  }
  if (symbol == "sub_8300C290") {
    return "XGRAPHICS__TileSurface";
  }
  if (symbol == "sub_8300CB40") {
    return "j_XGRAPHICS__TileSurface";
  }
  if (symbol == "sub_8300CB48") {
    return "XGTileTextureLevel";
  }
  if (symbol == "sub_83010E60") {
    return "XGRAPHICS__TileVolume";
  }
  if (symbol == "sub_830117B8") {
    return "XGTileVolumeTextureLevel";
  }
  if (symbol == "sub_8300C660") {
    return "XGRAPHICS__UntileSurface";
  }
  if (symbol == "sub_8300CDA0") {
    return "j_XGRAPHICS__UntileSurface";
  }
  if (symbol == "sub_8300CDA8") {
    return "XGUntileTextureLevel";
  }
  if (symbol == "sub_830112F0") {
    return "XGRAPHICS__UntileVolume";
  }
  if (symbol == "sub_83011AE0") {
    return "XGUntileVolumeTextureLevel";
  }
  if (symbol == "sub_8222BFA0") {
    return "D3D::SetLiteralShaderConstants";
  }
  if (symbol == "sub_82BA76F8") {
    return "D3D::PM4LoadShaderConstantsImmediate";
  }
  if (symbol == "sub_82BA77D0") {
    return "D3D::PM4SetTextureFetchConstant";
  }
  if (symbol == "sub_8221A880") {
    return "D3DDevice_SetVertexShaderConstantF";
  }
  if (symbol == "sub_82214E30") {
    return "D3DDevice_SetPixelShaderConstantF";
  }
  if (symbol == "sub_822655F0") {
    return "D3DDevice_SetPredication";
  }
  if (symbol == "sub_822AF568") {
    return "D3DDevice_SetScreenExtentQueryMode";
  }
  if (symbol == "sub_8220B470") {
    return "D3DDevice_SetRenderState_CullMode";
  }
  if (symbol == "sub_8221D190") {
    return "D3DDevice_SetRenderState_PackedBlendControl20";
  }
  if (symbol == "sub_8226AE78") {
    return "D3DDevice_SetRenderState_PackedDepthModeBit2";
  }
  if (symbol == "sub_821E0BF8") {
    return "D3DDevice_SetRenderState_PackedDepthModeBit5";
  }
  if (symbol == "sub_82272E50") {
    return "D3DDevice_SetRenderState_PixelCenterOffset";
  }
  if (symbol == "sub_82BA23E8") {
    return "D3DDevice_UnsetAll";
  }
  if (symbol == "sub_82BAEA88") {
    return "D3D::SetupScaler";
  }
  if (symbol == "sub_82BAE960") {
    return "D3D::InitializeDefaultScalerParameters";
  }
  if (symbol == "sub_82BAEC38") {
    return "D3DDevice_SetScalerParameters";
  }
  if (symbol == "sub_822A09A0") {
    return "D3D::SetColorClear";
  }
  if (symbol == "sub_82B98248") {
    return "D3DDevice_SetGammaRamp";
  }
  if (symbol == "sub_82BA2748") {
    return "D3DDevice_BlockUntilIdle";
  }
  if (symbol == "sub_8217F3E8") {
    return "D3DDevice_QueryBufferSpace";
  }
  if (symbol == "sub_821F6270") {
    return "D3DDevice_IsFencePending";
  }
  if (symbol == "sub_821FC178") {
    return "D3DResource_AddRef";
  }
  if (symbol == "sub_82BA2218") {
    return "D3DDevice_Release";
  }
  if (symbol == "sub_82BA2270") {
    return "D3DDevice_AcquireThreadOwnership";
  }
  if (symbol == "sub_82BA2500") {
    return "D3DDevice_GetDeviceState";
  }
  if (symbol == "sub_82EF2A90") {
    return "D3DDevice::BeginScene";
  }
  if (symbol == "rex_XGOffsetBaseTextureAddress") {
    return "XGOffsetBaseTextureAddress";
  }
  if (symbol == "sub_82BA40F0") {
    return "D3DVertexShader_Bind";
  }
  if (symbol == "rex_lhRenderer_PrepareFrame") {
    return "lhRenderer_PrepareFrame";
  }
  if (symbol == "rex_lhRenderer_SetupFrame") {
    return "lhRenderer_SetupFrame";
  }

  return nullptr;
}

const char* LumenRendererAddressName(uint32_t address) {
  switch (address) {
    case 0x82B98930:
      return "LockTextureRect";
    case 0x82B98950:
      return "UnlockTextureRect";
    case 0x82B9EEC0:
      return "UnlockVertexBuffer";
    case 0x82B9EED0:
      return "UnlockIndexBuffer";
    case 0x821FC178:
      return "D3DResource_AddRef";
    case 0x821FC8D8:
      return "Fable::CreateSurfaceWrapper";
    case 0x821F4EC0:
      return "D3DDevice_CreateSurface";
    case 0x82B98C88:
      return "D3DSurface_GetContainer";
    case 0x821FA6C0:
      return "D3D::FindSurfaceWithinTexture";
    case 0x821FAB38:
      return "D3D::GetTextureDimensions";
    case 0x821FABD0:
      return "D3D::Get2DSurfaceDesc";
    case 0x82B985C8:
      return "D3D::Get3DSurfaceDesc";
    case 0x821FAD60:
      return "D3D::GetLevelSize";
    case 0x82295380:
      return "Fable::CreateTiledSurfaceWrapper";
    case 0x82B8AB30:
      return "Fable::RenderDepthSurfaceCtor";
    case 0x82A4E8D0:
      return "Fable::BinkVideoPlaneSurfaceSetup";
    case 0x82B6F040:
      return "Fable::PresentationScratchSetup";
    case 0x82B6F5C8:
      return "Fable::PresentationScratchTeardown";
    case 0x8226B078:
      return "Fable::BindDepthSurface";
    case 0x8226E180:
      return "Fable::SceneStateFlush";
    case 0x8226E738:
      return "Fable::SceneDrawSubmit";
    case 0x82EF2A90:
      return "D3DDevice::BeginScene";
    case 0x82B6ED10:
      return "lhRenderer_PrepareFrame";
    case 0x82B6EDE0:
      return "lhRenderer_SetupFrame";
    case 0x821B6C60:
      return "D3DDevice_SetStreamSource";
    case 0x821B6EE0:
      return "D3DDevice_SetTexture";
    case 0x821B1330:
      return "D3DDevice_SetRenderState_AlphaFunc";
    case 0x821B1350:
      return "D3DDevice_SetRenderState_StencilWriteMask";
    case 0x82225E08:
      return "D3DDevice_SetRenderState_AlphaRef";
    case 0x82196628:
      return "D3DDevice_BeginExport";
    case 0x82196750:
      return "D3DDevice_EndExport";
    case 0x822C6BA8:
      return "D3D::InvalidateCaches";
    case 0x8219CD68:
      return "D3DDevice_SetShaderGPRAllocation";
    case 0x821BFDF0:
      return "D3DDevice_SetRenderState_ZFunc";
    case 0x821C91C8:
      return "D3DDevice_SetRenderState_ColorWriteEnable";
    case 0x821DCAA0:
      return "D3DDevice_SetRenderState_AlphaBlendEnable";
    case 0x821DC158:
      return "D3DDevice_SetRenderState_SrcBlend";
    case 0x821E2FE0:
      return "D3DDevice_ClearF";
    case 0x821EF830:
      return "D3D::ClearRect";
    case 0x821E56F8:
      return "D3DDevice_SetRenderState_StencilEnable";
    case 0x821EC798:
      return "D3DDevice_SetSamplerState_MipMapLodBias";
    case 0x821E0BF8:
      return "D3DDevice_SetRenderState_PackedDepthModeBit5";
    case 0x821F0490:
      return "D3DDevice_SetRenderState_DestBlend";
    case 0x8221A880:
      return "D3DDevice_SetVertexShaderConstantF";
    case 0x82214E30:
      return "D3DDevice_SetPixelShaderConstantF";
    case 0x821C6708:
      return "D3DDevice_SetVertexShaderConstantB";
    case 0x82B991B0:
      return "D3DDevice_SetVertexShaderConstantI";
    case 0x8226E120:
      return "D3DDevice_SetPixelShaderConstantB";
    case 0x82B99208:
      return "D3DDevice_SetPixelShaderConstantI";
    case 0x821F9C08:
      return "D3DDevice_SetScissorRect";
    case 0x821F9D00:
      return "D3D::SetViewport";
    case 0x821F9F58:
      return "D3DDevice_SetViewport";
    case 0x821F9918:
      return "D3D::SetSurfaceClip";
    case 0x821FBB78:
      return "XGSetTextureHeader";
    case 0x8225FDF0:
      return "XGOffsetBaseTextureAddress";
    case 0x8300BD38:
      return "XGSetVertexBufferHeader";
    case 0x8300BDD0:
      return "XGSetIndexBufferHeader";
    case 0x8300BE60:
      return "XGSetConstantBufferHeader";
    case 0x8300BEE0:
      return "XGSetLineTextureHeader";
    case 0x8300BF48:
      return "XGSetCubeTextureHeader";
    case 0x821F52A0:
      return "XGSurfaceSize";
    case 0x8300C290:
      return "XGRAPHICS__TileSurface";
    case 0x8300CB40:
      return "j_XGRAPHICS__TileSurface";
    case 0x8300CB48:
      return "XGTileTextureLevel";
    case 0x83010E60:
      return "XGRAPHICS__TileVolume";
    case 0x830117B8:
      return "XGTileVolumeTextureLevel";
    case 0x8300C660:
      return "XGRAPHICS__UntileSurface";
    case 0x8300CDA0:
      return "j_XGRAPHICS__UntileSurface";
    case 0x8300CDA8:
      return "XGUntileTextureLevel";
    case 0x830112F0:
      return "XGRAPHICS__UntileVolume";
    case 0x83011AE0:
      return "XGUntileVolumeTextureLevel";
    case 0x82208BB0:
      return "D3DDevice_SetPixelShader";
    case 0x82215488:
      return "D3DDevice_SetRenderState_AlphaTestEnable";
    case 0x821887D8:
      return "D3DDevice_SetRenderState_StencilFail";
    case 0x82188800:
      return "D3DDevice_SetRenderState_StencilZFail";
    case 0x8229ABB8:
      return "D3DDevice_SetRenderState_StencilPass";
    case 0x822A6180:
      return "D3DDevice_SetRenderState_StencilFunc";
    case 0x822C08E8:
      return "D3DDevice_SetRenderState_StencilMask";
    case 0x82239910:
      return "D3DDevice_SetRenderState_StencilRef";
    case 0x8221E410:
      return "D3DDevice_SetRenderState_HighPrecisionBlendEnable";
    case 0x8220B470:
      return "D3DDevice_SetRenderState_CullMode";
    case 0x82202D10:
      return "D3DDevice_SetSamplerState_AddressU";
    case 0x8226B058:
      return "D3DDevice_SetRenderState_ZWriteEnable";
    case 0x8221D190:
      return "D3DDevice_SetRenderState_PackedBlendControl20";
    case 0x8222C568:
      return "D3DDevice_SetSamplerState_AddressV";
    case 0x8222C818:
      return "D3DDevice_SetSamplerState_MipFilter";
    case 0x8221B010:
      return "D3D::SetPending_Shaders";
    case 0x8221AE18:
      return "D3D::SetPending_HiZEnable";
    case 0x8221C7D8:
      return "D3D::SetPending_RenderStates";
    case 0x8221D1B0:
      return "D3D::SetPending_FetchConstants";
    case 0x822154B0:
      return "D3D::SetPending_Predicated";
    case 0x8228EF80:
      return "D3D::SetPending_Split";
    case 0x822B4008:
      return "D3D::SetPending_ClipPlanes";
    case 0x82232510:
      return "D3DDevice_SetVertexShader";
    case 0x82B993C0:
      return "XGRegisterVertexShader";
    case 0x830FE488:
      return "XGRAPHICS__ThreadSafeSSMPrecompileShader";
    case 0x831CBAF8:
      return "XGRAPHICS__TranslateShader";
    case 0x82264590:
      return "D3DDevice_SetDepthStencilSurface";
    case 0x822655F0:
      return "D3DDevice_SetPredication";
    case 0x822869A0:
      return "D3DDevice_SetRenderTarget";
    case 0x82B981D0:
      return "D3DDevice_GetRenderTarget";
    case 0x822A09A0:
      return "D3D::SetColorClear";
    case 0x82286D78:
      return "D3DDevice_SetRenderState_ZEnable";
    case 0x822373F0:
      return "D3DDevice_SetSamplerState_AddressW";
    case 0x82238128:
      return "D3DDevice_SetSamplerState_MinFilter";
    case 0x822383A0:
      return "D3DDevice_SetSamplerState_MagFilter";
    case 0x822398D0:
      return "D3DDevice_SetSamplerState_FilterQuality";
    case 0x8229F790:
      return "D3DDevice_SetRenderState_HiZWriteEnable";
    case 0x8226AE78:
      return "D3DDevice_SetRenderState_PackedDepthModeBit2";
    case 0x82272E50:
      return "D3DDevice_SetRenderState_PixelCenterOffset";
    case 0x822AF568:
      return "D3DDevice_SetScreenExtentQueryMode";
    case 0x822C0968:
      return "D3DDevice_SetRenderState_BlendOp";
    case 0x82B96EF0:
      return "D3DDevice_SetRenderState_BlendOpAlpha";
    case 0x82B96F60:
      return "D3DDevice_SetRenderState_SrcBlendAlpha";
    case 0x82B96FD0:
      return "D3DDevice_SetRenderState_DestBlendAlpha";
    case 0x82B97040:
      return "D3DDevice_SetRenderState_SeparateAlphaBlendEnable";
    case 0x82B97448:
      return "D3DDevice_SetRenderState_ClipPlaneEnable";
    case 0x82B97BB0:
      return "D3DDevice_SetSamplerState_MinFilterZ";
    case 0x82B97C90:
      return "D3DDevice_SetSamplerState_MagFilterZ";
    case 0x82B97D48:
      return "D3DDevice_SetSamplerState_SeparateZFilterEnable";
    case 0x8217F3E8:
      return "D3DDevice_QueryBufferSpace";
    case 0x821F6270:
      return "D3DDevice_IsFencePending";
    case 0x82BA2218:
      return "D3DDevice_Release";
    case 0x82BA2270:
      return "D3DDevice_AcquireThreadOwnership";
    case 0x82BA2500:
      return "D3DDevice_GetDeviceState";
    case 0x82BA25C0:
      return "D3D::CDevice::AddCommandsToPrimaryBuffer";
    case 0x82BA2748:
      return "D3DDevice_BlockUntilIdle";
    case 0x8221F2E0:
      return "D3D::FlushPendingGpuState";
    case 0x82BA40F0:
      return "D3DVertexShader_Bind";
    case 0x8221C3E8:
      return "D3DDevice_DrawPrimitive";
    case 0x8221DFC0:
      return "D3DDevice_DrawIndexedPrimitive";
    case 0x822224A8:
      return "D3DDevice_SetVertexDeclaration";
    case 0x82B99720:
      return "D3DDevice_CreateVertexDeclaration";
    case 0x82BA23E8:
      return "D3DDevice_UnsetAll";
    case 0x82BA32F8:
      return "D3D::ComplexSwap";
    case 0x82BA34D8:
      return "D3DDevice_Swap";
    case 0x82BA76F8:
      return "D3D::PM4LoadShaderConstantsImmediate";
    case 0x82BA77D0:
      return "D3D::PM4SetTextureFetchConstant";
    case 0x82BA7898:
      return "D3D::PixEnableVideoPlayback";
    case 0x82BA7928:
      return "D3D::PixDisableVideoPlayback";
    case 0x82BA7988:
      return "D3D::PixTimeSinceVBlank";
    case 0x82BA79D0:
      return "D3D::CodecSetState";
    case 0x82BA83C0:
      return "D3D::PixRenderVideoFrame";
    case 0x82BA8928:
      return "D3D::PixDecodeVideoFrame";
    case 0x82B98248:
      return "D3DDevice_SetGammaRamp";
    case 0x82BA95E0:
      return "D3D::PixCaptureVideoFrame";
    case 0x82BAA848:
      return "D3D::PixSyncronizeToPresentationInterval";
    case 0x82BAA920:
      return "D3D::PixInitializePerfMemory";
    case 0x82BAE960:
      return "D3D::InitializeDefaultScalerParameters";
    case 0x82BAEA88:
      return "D3D::SetupScaler";
    case 0x82BAEC38:
      return "D3DDevice_SetScalerParameters";
    default:
      return nullptr;
  }
}

uint32_t RenderStateSlotForSymbol(std::string_view symbol) {
  if (symbol == "sub_821BFDF0") return 0;   // ZFunc
  if (symbol == "sub_821C91C8") return 1;   // ColorWriteEnable
  if (symbol == "sub_821DCAA0") return 2;   // AlphaBlendEnable
  if (symbol == "sub_821DC158") return 3;   // SrcBlend
  if (symbol == "sub_821E56F8") return 18;  // StencilEnable, state10548[0]
  if (symbol == "sub_821F0490") return 5;   // DestBlend
  if (symbol == "sub_82215488") return 6;   // AlphaTestEnable
  if (symbol == "sub_82286D78") return 7;   // ZEnable
  if (symbol == "sub_8229F790") return 8;   // HiZWriteEnable
  if (symbol == "sub_822C0968") return 9;   // BlendOp
  if (symbol == "sub_82B96EF0") return 10;  // BlendOpAlpha
  if (symbol == "sub_82B96F60") return 11;  // SrcBlendAlpha
  if (symbol == "sub_82B96FD0") return 12;  // DestBlendAlpha
  if (symbol == "sub_82B97040") return 13;  // SeparateAlphaBlendEnable
  if (symbol == "sub_82B97448") return 14;  // ClipPlaneEnable
  if (symbol == "sub_8221E410") return 15;  // HighPrecisionBlendEnable
  if (symbol == "sub_8226B058") return 4;   // ZWriteEnable, state10548[2]
  if (symbol == "sub_8220B470") return 19;  // CullMode, state10568[2:0]
  if (symbol == "sub_821887D8") return 16;  // StencilFail
  if (symbol == "sub_82188800") return 17;  // StencilZFail
  if (symbol == "sub_8229ABB8") return 20;  // StencilPass, state10548[16:14]
  if (symbol == "sub_822A6180") return 21;  // StencilFunc, state10548[10:8]
  if (symbol == "sub_822C08E8") return 22;  // StencilMask, state10498
  if (symbol == "sub_82239910") return 23;  // StencilRef, state10499
  if (symbol == "sub_821B1330") return 24;  // AlphaFunc, state10556[2:0]
  if (symbol == "sub_821B1350") return 27;  // StencilWriteMask, state10497
  if (symbol == "sub_82225E08") return 25;  // AlphaRef, state10500
  if (symbol == "sub_82272E50") return 26;  // PixelCenterOffset, state10688[0]
  if (symbol == "sub_8226AE78") return 28;  // PackedDepthModeBit2, state10560[2]
  if (symbol == "sub_821E0BF8") return 29;  // PackedDepthModeBit5, state10560[5]
  if (symbol == "sub_8221D190") return 30;  // PackedBlendControl20, state11852[22:20]
  return UINT32_MAX;
}

uint32_t RenderStateSlotForAddress(uint32_t address) {
  switch (address) {
    case 0x821BFDF0:
      return kFableRenderStateSlotZFunc;
    case 0x821C91C8:
      return kFableRenderStateSlotColorWriteEnable;
    case 0x821DCAA0:
      return kFableRenderStateSlotAlphaBlendEnable;
    case 0x821DC158:
      return kFableRenderStateSlotSrcBlend;
    case 0x821E56F8:
      return kFableRenderStateSlotStencilEnable;
    case 0x821F0490:
      return kFableRenderStateSlotDestBlend;
    case 0x82215488:
      return kFableRenderStateSlotAlphaTestEnable;
    case 0x82286D78:
      return kFableRenderStateSlotZEnable;
    case 0x8229F790:
      return kFableRenderStateSlotHiZWriteEnable;
    case 0x822C0968:
      return kFableRenderStateSlotBlendOp;
    case 0x82B96EF0:
      return kFableRenderStateSlotBlendOpAlpha;
    case 0x82B96F60:
      return kFableRenderStateSlotSrcBlendAlpha;
    case 0x82B96FD0:
      return kFableRenderStateSlotDestBlendAlpha;
    case 0x82B97040:
      return kFableRenderStateSlotSeparateAlphaBlendEnable;
    case 0x82B97448:
      return 14;
    case 0x8221E410:
      return 15;
    case 0x821887D8:
      return kFableRenderStateSlotStencilFail;
    case 0x82188800:
      return kFableRenderStateSlotStencilZFail;
    case 0x8226B058:
      return kFableRenderStateSlotZWriteEnable;
    case 0x8220B470:
      return kFableRenderStateSlotCullMode;
    case 0x8229ABB8:
      return kFableRenderStateSlotStencilPass;
    case 0x822A6180:
      return kFableRenderStateSlotStencilFunc;
    case 0x822C08E8:
      return kFableRenderStateSlotStencilReadMask;
    case 0x82239910:
      return kFableRenderStateSlotStencilRef;
    case 0x821B1330:
      return kFableRenderStateSlotAlphaFunc;
    case 0x821B1350:
      return kFableRenderStateSlotStencilWriteMask;
    case 0x82225E08:
      return kFableRenderStateSlotAlphaRef;
    case 0x82272E50:
      return kFableRenderStateSlotPixelCenterOffset;
    case 0x8226AE78:
      return kFableRenderStateSlotPackedDepthModeBit2;
    case 0x821E0BF8:
      return kFableRenderStateSlotPackedDepthModeBit5;
    case 0x8221D190:
      return kFableRenderStateSlotPackedBlendControl20;
    default:
      return UINT32_MAX;
  }
}

uint32_t SamplerStateSlotForSymbol(std::string_view symbol) {
  if (symbol == "sub_82B97BB0") return 0;  // MinFilterZ
  if (symbol == "sub_82B97C90") return 1;  // MagFilterZ
  if (symbol == "sub_82B97D48") return 2;  // SeparateZFilterEnable
  if (symbol == "sub_821EC798") return 3;  // MipMapLodBias
  if (symbol == "sub_82238128") return 0;  // MinFilter
  if (symbol == "sub_822383A0") return 1;  // MagFilter
  if (symbol == "sub_82202D10") return 4;  // AddressU
  if (symbol == "sub_8222C568") return 5;  // AddressV
  if (symbol == "sub_822373F0") return 6;  // AddressW
  if (symbol == "sub_8222C818") return 7;  // MipFilter
  if (symbol == "sub_822398D0") return 8;  // FilterQuality
  return UINT32_MAX;
}

constexpr uint32_t kPackedSamplerStateValid = 0x80000000u;
constexpr uint32_t kPackedSamplerStateMagLinear = 0x00000001u;
constexpr uint32_t kPackedSamplerStateMinLinear = 0x00000002u;
constexpr uint32_t kPackedSamplerStateSeparateZ = 0x00000004u;
constexpr uint32_t kPackedSamplerStateMipLinear = 0x00000008u;
constexpr uint32_t kPackedSamplerStateAddressUShift = 8u;
constexpr uint32_t kPackedSamplerStateAddressVShift = 11u;
constexpr uint32_t kPackedSamplerStateAddressWShift = 14u;
constexpr uint32_t kPackedSamplerStateAddressMask = 0x7u;
constexpr uint32_t kPackedSamplerStateFilterQualityShift = 24u;
constexpr uint32_t kPackedSamplerStateFilterQualityMask = 0x3u;

uint32_t DefaultPackedSamplerState() {
  return kPackedSamplerStateValid | kPackedSamplerStateMagLinear |
         kPackedSamplerStateMinLinear | kPackedSamplerStateMipLinear;
}

uint32_t SamplerStateSlotForAddress(uint32_t address) {
  switch (address) {
    case 0x82238128:
      return 0;  // MinFilter
    case 0x822383A0:
      return 1;  // MagFilter
    case 0x82202D10:
      return 4;  // AddressU
    case 0x8222C568:
      return 5;  // AddressV
    case 0x822373F0:
      return 6;  // AddressW
    case 0x8222C818:
      return 7;  // MipFilter
    case 0x822398D0:
      return 8;  // FilterQuality
    case 0x82B97BB0:
      return 0;  // MinFilterZ
    case 0x82B97C90:
      return 1;  // MagFilterZ
    case 0x82B97D48:
      return 2;  // SeparateZFilterEnable
    case 0x821EC798:
      return 3;  // MipMapLodBias
    default:
      return UINT32_MAX;
  }
}

void StoreSamplerAddressMode(uint32_t& packed, uint32_t shift,
                             uint32_t value) {
  packed &= ~(kPackedSamplerStateAddressMask << shift);
  packed |= ((value & kPackedSamplerStateAddressMask) << shift);
}

void StoreSamplerStateLocked(uint32_t slot, uint32_t state_slot,
                             uint32_t value) {
  if (slot >= g_observer.sampler_states.size()) {
    return;
  }

  uint32_t& packed = g_observer.sampler_states[slot];
  if ((packed & kPackedSamplerStateValid) == 0) {
    packed = DefaultPackedSamplerState();
  }

  switch (state_slot) {
    case 0:  // MinFilterZ
      if (value != 0) {
        packed |= kPackedSamplerStateMinLinear;
      } else {
        packed &= ~kPackedSamplerStateMinLinear;
      }
      break;
    case 1:  // MagFilterZ
      if (value != 0) {
        packed |= kPackedSamplerStateMagLinear;
      } else {
        packed &= ~kPackedSamplerStateMagLinear;
      }
      break;
    case 2:  // SeparateZFilterEnable
      if (value != 0) {
        packed |= kPackedSamplerStateSeparateZ;
      } else {
        packed &= ~kPackedSamplerStateSeparateZ;
      }
      break;
    case 3:  // MipMapLodBias
      // Keep a coarse signed 10-bit copy for cache key stability. Plume accepts
      // a float bias, but Fable currently only needs distinct sampler identity.
      packed = (packed & 0x803FFFFFu) | ((value >> 10) & 0x7FC00000u);
      break;
    case 4:  // AddressU
      StoreSamplerAddressMode(packed, kPackedSamplerStateAddressUShift, value);
      break;
    case 5:  // AddressV
      StoreSamplerAddressMode(packed, kPackedSamplerStateAddressVShift, value);
      break;
    case 6:  // AddressW
      StoreSamplerAddressMode(packed, kPackedSamplerStateAddressWShift, value);
      break;
    case 7:  // MipFilter
      if (value != 0) {
        packed |= kPackedSamplerStateMipLinear;
      } else {
        packed &= ~kPackedSamplerStateMipLinear;
      }
      break;
    case 8:  // FilterQuality
      packed &= ~(kPackedSamplerStateFilterQualityMask
                  << kPackedSamplerStateFilterQualityShift);
      packed |= ((value & kPackedSamplerStateFilterQualityMask)
                 << kPackedSamplerStateFilterQualityShift);
      break;
    default:
      break;
  }
}

const char* Pm4OpcodeName(uint32_t opcode) {
  switch (opcode) {
    case 0x48:
      return "PM4_ME_INIT";
    case 0x10:
      return "PM4_NOP";
    case 0x22:
      return "PM4_DRAW_INDX";
    case 0x27:
      return "PM4_IM_LOAD";
    case 0x2B:
      return "PM4_IM_LOAD_IMMEDIATE";
    case 0x2D:
      return "PM4_SET_CONSTANT";
    case 0x2F:
      return "PM4_LOAD_ALU_CONSTANT";
    case 0x34:
      return "PM4_DRAW_INDX_BIN";
    case 0x35:
      return "PM4_DRAW_INDX_2_BIN";
    case 0x36:
      return "PM4_DRAW_INDX_2";
    case 0x37:
      return "PM4_INDIRECT_BUFFER_PFD";
    case 0x3B:
      return "PM4_INVALIDATE_STATE";
    case 0x3C:
      return "PM4_WAIT_REG_MEM";
    case 0x3F:
      return "PM4_INDIRECT_BUFFER";
    case 0x46:
      return "PM4_EVENT_WRITE";
    case 0x55:
      return "PM4_SET_CONSTANT2";
    case 0x56:
      return "PM4_SET_SHADER_CONSTANTS";
    case 0x64:
      return "PM4_XE_SWAP";
    default:
      return "PM4_TYPE3_UNKNOWN";
  }
}

bool IsPm4DrawOpcode(uint32_t opcode) {
  return opcode == 0x22 || opcode == 0x34 || opcode == 0x35 ||
         opcode == 0x36;
}

bool IsPm4IndirectOpcode(uint32_t opcode) {
  return opcode == 0x37 || opcode == 0x3F;
}

bool IsPm4ConstantOpcode(uint32_t opcode) {
  return opcode == 0x2D || opcode == 0x55 || opcode == 0x56 ||
         opcode == 0x2F;
}

constexpr uint32_t kMaxPm4DecodeWords = 1u << 20;
constexpr uint32_t kMaxPm4DecodedPackets = 1u << 16;
constexpr uint32_t kMaxPm4IndirectDepth = 8;
constexpr uint32_t kFetchConstantRegisterFirst = 0x4800;
constexpr uint32_t kFetchConstantRegisterLast = 0x48BF;
constexpr uint32_t kAluConstantRegisterFirst = 0x4000;
constexpr uint32_t kAluConstantRegisterPixelFirst = 0x4400;
constexpr uint32_t kAluConstantRegisterLast = 0x47FF;
constexpr uint32_t kFetchConstantDwordCount = kRendererFetchConstantDwords;
constexpr uint32_t kXenosDisplaySurfaceRegister = 0x1844;

enum class DisplayPresentSource : uint32_t {
  kUnknown = 0,
  kD3DSwapSurface = 1,
  kPm4DisplayRegister = 2,
};
constexpr uint32_t kSetConstantFetchType = 1;
constexpr uint32_t kSetConstantRegisterType = 4;
constexpr uint32_t kPm4LoadAluConstantOpcode = 0x2F;
constexpr uint32_t kMaxCallBufferDescriptors = 1024;
constexpr uint32_t kMaxShaderConstantVectorUpload = 256;
constexpr uint32_t kVertexConstantUploadCallerFirst = 0x82BA7B28;
constexpr uint32_t kVertexConstantUploadCallerLast = 0x82BA83BF;
constexpr uint32_t kPixelConstantUploadCallerFirst = 0x82BA83C0;
constexpr uint32_t kPixelConstantUploadCallerLast = 0x82BA8923;
constexpr uint32_t kFableDeviceStencilWriteMaskOffset = 0x2901;
constexpr uint32_t kFableDeviceStencilReadMaskOffset = 0x2902;
constexpr uint32_t kFableDeviceStencilRefOffset = 0x2903;
constexpr uint32_t kFableDeviceAlphaRefOffset = 0x2904;
constexpr uint32_t kFableDeviceColorControlOffset = 0x293C;
constexpr uint32_t kFableDeviceRenderTargetBaseOffset = 0x3098;
constexpr uint32_t kFableDeviceDepthStencilOffset = 0x30A8;
constexpr uint32_t kFableDeviceVertexDeclarationOffset = 11820;
constexpr uint32_t kFableDeviceIndexBufferOffset = 12436;
constexpr uint32_t kFableDevicePixelShaderOffset = 0x3194;
constexpr uint32_t kFableDeviceVertexShaderOffset = 0x3198;
constexpr uint32_t kFableDeviceStreamGpuAddressBaseOffset = 1912;
constexpr uint32_t kFableDeviceStreamGpuSizeBaseOffset = 1916;
constexpr uint32_t kFableDeviceStreamObjectBaseOffset = 12460;
constexpr uint32_t kFableDeviceStreamStrideBaseOffset = 12528;
constexpr uint32_t kFableDeviceTextureFetchBaseOffset = 0x480;
constexpr uint32_t kFableDeviceTextureFetchStride = 24;
constexpr uint32_t kFableDeviceTextureObjectBaseOffset = 0x3100;
constexpr uint32_t kFableDeviceTextureMaxMipOffset = 11950;
constexpr uint32_t kFableDeviceTextureMinMipOffset = 11976;
constexpr uint32_t kFableDeviceViewportTransformHalfWidthOffset = 10504;
constexpr uint32_t kFableDeviceViewportTransformCenterXOffset = 10508;
constexpr uint32_t kFableDeviceViewportTransformNegHalfHeightOffset = 10512;
constexpr uint32_t kFableDeviceViewportTransformCenterYOffset = 10516;
constexpr uint32_t kFableDeviceViewportTransformDepthScaleOffset = 10520;
constexpr uint32_t kFableDeviceViewportTransformDepthBiasOffset = 10524;
constexpr uint32_t kFableDevicePackedScissorMinOffset = 10436;
constexpr uint32_t kFableDevicePackedScissorMaxOffset = 10440;
constexpr uint32_t kFableDeviceViewportXOffset = 12648;
constexpr uint32_t kFableDeviceViewportYOffset = 12652;
constexpr uint32_t kFableDeviceViewportWidthOffset = 12656;
constexpr uint32_t kFableDeviceViewportHeightOffset = 12660;
constexpr uint32_t kFableDeviceViewportMinZOffset = 12664;
constexpr uint32_t kFableDeviceViewportMaxZOffset = 12668;
constexpr uint32_t kFableDeviceViewportUnknownOffset = 12672;
constexpr uint32_t kFableDeviceScissorBaseOffset = 12676;
constexpr uint32_t kFableDeviceScalerSurfaceOffset = 0x39EC;
constexpr uint32_t kFableDeviceDefaultDisplayWidthOffset = 0x34E8;
constexpr uint32_t kFableDeviceDefaultDisplayHeightOffset = 0x34EC;
constexpr uint32_t kFableDeviceScalerParametersOffset = 0x359C;
constexpr uint32_t kFableDeviceScalerDirtyFlagsOffset = 0x2ABE;
constexpr uint32_t kFableDeviceOutputWidthOffset = 0x5428;
constexpr uint32_t kFableDeviceOutputHeightOffset = 0x542C;
constexpr uint32_t kSyntheticFablePrivateRectVertexShader = 0x0E000152u;
constexpr uint32_t kSyntheticFablePrivateRectPixelShader = 0x0E010171u;
constexpr uint64_t kFableGuiQuadVertexShaderHash = 0x4724831317AA37A8ull;
constexpr uint64_t kFableBinkMoviePixelShaderHash = 0x69E9772C76BD6253ull;

void QueueDrawEventLocked(uint32_t device_pointer = 0,
                          bool draw_vertices_up = false,
                          bool refresh_device_bindings = true);
void QueuePm4ResolveCopyEventLocked(uint32_t source_lr);
void QueueClearEventLocked(uint32_t flags, uint32_t color_pointer,
                           double depth, uint32_t stencil,
                           RendererScissorSnapshot rect);
bool IsDrawVerticesUpInnerScratchSubmit(const RendererHookArgs& args);
void TraceDrawCall(std::string_view symbol,
                   const RendererLastDrawSnapshot& draw);
void TraceResourceCreate(const RendererResourceRecord& record);
void UpsertScratchBufferResourceLocked(RendererResourceKind kind,
                                       uint32_t address, uint32_t size,
                                       uint32_t stride_or_format,
                                       uint32_t parent_address,
                                       uint32_t upload_source_address = 0);
void TrackShaderObjectLocked(RendererResourceKind kind, uint32_t shader_address,
                             uint32_t parent_address, uint8_t* base);
void TrackVertexDeclarationLocked(uint32_t declaration_address,
                                  uint32_t parent_address, uint8_t* base);
bool TrackBoundSurfaceLocked(RendererResourceKind kind, uint32_t surface_address,
                             uint32_t parent_address, uint8_t* base);
void ObserveScalerParametersFromPointerLocked(const RendererHookArgs& args,
                                              uint8_t* base,
                                              uint32_t parameter_pointer,
                                              uint32_t source_width_hint = 0,
                                              uint32_t source_height_hint = 0);

uint32_t SelectReadableUploadAddress(uint8_t* base, uint32_t raw_address,
                                     uint32_t upload_size) {
  if (!base || raw_address == 0 || upload_size == 0) {
    return 0;
  }

  const uint32_t decoded_address = DecodeFableGpuBufferPointer(raw_address);
  const auto is_upload_address = [&](uint32_t address) {
    return IsLikelyGpuByteBufferRange(address, upload_size) ||
           IsLikelyHighScratchByteBufferRange(address, upload_size);
  };
  const auto first_nonzero = [&](uint32_t address) {
    return is_upload_address(address)
               ? FindFirstNonZeroGuestByte(base, address, upload_size,
                                           upload_size)
               : UINT32_MAX;
  };

  const uint32_t raw_nonzero = first_nonzero(raw_address);
  const uint32_t decoded_nonzero = first_nonzero(decoded_address);
  if (raw_nonzero != UINT32_MAX) {
    return raw_address;
  }
  if (is_upload_address(decoded_address)) {
    return decoded_address;
  }
  return is_upload_address(raw_address) ? raw_address : 0;
}

bool RefreshBinkVideoPlaneFetchAliasUploadLocked(
    const RendererResourceRecord& surface_record,
    const BinkVideoPlaneBinding& binding, uint8_t* base,
    uint32_t upload_address) {
  if (!base || binding.upload_size == 0 || upload_address == 0 ||
      !IsImageResourceKind(surface_record.kind)) {
    return false;
  }

  const std::array<uint32_t, 6> dwords =
      NormalizeTextureFetchDwords(TextureFetchDwordsForRecord(surface_record));
  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (info.base_address == 0 || info.base_address == surface_record.guest_address) {
    return false;
  }

  auto alias_it = g_observer.resources.find(info.base_address);
  const bool created = alias_it == g_observer.resources.end();
  if (created) {
    RendererResourceRecord alias_record;
    alias_record.kind = RendererResourceKind::kTexture;
    alias_record.guest_address = info.base_address;
    alias_record.scratch = false;
    alias_it =
        g_observer.resources.emplace(alias_record.guest_address,
                                     std::move(alias_record))
            .first;
    ++g_observer.resource_creates;
  }

  RendererResourceRecord& alias_record = alias_it->second;
  alias_record.kind = RendererResourceKind::kTexture;
  alias_record.parent_address = surface_record.guest_address;
  alias_record.raw_buffer_object = upload_address;
  alias_record.raw_size =
      std::max<uint32_t>(binding.upload_size, info.size);
  alias_record.raw_stride_or_format = surface_record.raw_stride_or_format;
  alias_record.raw_flags = surface_record.raw_flags;
  alias_record.raw_ref_count = surface_record.raw_ref_count;
  alias_record.raw_aux20 = surface_record.raw_aux20;
  alias_record.raw_parent24 = surface_record.guest_address;
  alias_record.raw_packed28 = dwords[0];
  alias_record.raw_packed32 = dwords[1];
  alias_record.raw_packed36 = dwords[2];
  alias_record.raw_packed40 = dwords[3];
  alias_record.raw_packed44 = dwords[4];
  alias_record.raw_packed48 = dwords[5];
  alias_record.normalized_fetch_dwords = dwords;
  alias_record.scratch = false;

  const uint64_t previous_generation = alias_record.upload_generation;
  CopyGuestBufferUploadData(alias_record, base, upload_address,
                            binding.upload_size);
  if (alias_record.upload_generation == previous_generation) {
    return false;
  }

  alias_record.serial = ++g_observer.serial;
  g_observer.last_resource = alias_record;
  RefreshQueuedDrawTextureResourceSerialLocked(alias_record);
  if (created) {
    TraceResourceCreate(alias_record);
  }
  return true;
}

bool RefreshBinkVideoPlaneUploadLocked(RendererResourceRecord& record,
                                       const BinkVideoPlaneBinding& binding,
                                       uint8_t* base,
                                       std::string_view source_symbol) {
  if (!base || binding.surface == 0 || binding.upload_size == 0 ||
      !IsImageResourceKind(record.kind)) {
    return false;
  }

  const uint32_t preferred_source =
      binding.lock_address != 0 ? binding.lock_address : binding.physical_address;
  uint32_t upload_address =
      SelectReadableUploadAddress(base, preferred_source, binding.upload_size);
  if (upload_address == 0 && binding.physical_address != preferred_source) {
    upload_address =
        SelectReadableUploadAddress(base, binding.physical_address,
                                    binding.upload_size);
  }
  if (upload_address == 0) {
    return false;
  }

  const uint64_t previous_generation = record.upload_generation;
  CopyGuestBufferUploadData(record, base, upload_address, binding.upload_size);
  if (record.upload_generation == previous_generation) {
    return false;
  }

  record.serial = ++g_observer.serial;
  g_observer.last_resource = record;
  RefreshQueuedDrawTextureResourceSerialLocked(record);
  RefreshBinkVideoPlaneFetchAliasUploadLocked(record, binding, base,
                                             upload_address);
  ++g_observer.bink_video_upload_refreshes;

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_bink_video_uploads < 24u) {
    const uint32_t source_nonzero = FindFirstNonZeroGuestByte(
        base, upload_address, binding.upload_size, binding.upload_size);
    const uint32_t word0 =
        source_nonzero != UINT32_MAX ? LoadGuestU32(base, upload_address) : 0u;
    REXLOG_INFO(
        "Refii renderer Bink video upload #{} symbol={} surface=0x{:08X} "
        "source=0x{:08X} lock=0x{:08X} physical=0x{:08X} size={} "
        "first_nz={} word0={:08X} generation={}",
        g_observer.logged_bink_video_uploads + 1u, source_symbol,
        binding.surface, upload_address, binding.lock_address,
        binding.physical_address, binding.upload_size,
        source_nonzero == UINT32_MAX ? -1
                                     : static_cast<int32_t>(source_nonzero),
        word0, record.upload_generation);
    ++g_observer.logged_bink_video_uploads;
  }

  return true;
}

void RefreshBinkVideoPlaneUploadsLocked(std::string_view source_symbol,
                                        uint8_t* base) {
  if (!base || g_observer.bink_planes_by_surface.empty()) {
    return;
  }

  for (auto& [surface, binding] : g_observer.bink_planes_by_surface) {
    (void)surface;
    auto record_it = g_observer.resources.find(binding.surface);
    if (record_it == g_observer.resources.end()) {
      continue;
    }
    RefreshBinkVideoPlaneUploadLocked(record_it->second, binding, base,
                                      source_symbol);
  }
}

void ObserveBinkVideoPlaneSetupLocked(const RendererHookArgs& args,
                                      uint8_t* base) {
  if (!base || args.r6 == 0 || args.r7 == 0 ||
      !IsLikelyGuestDwordAddress(args.r6) ||
      !IsLikelyGuestDwordAddress(args.r7)) {
    return;
  }

  const uint32_t surface = LoadGuestU32(base, args.r7);
  if (!IsLikelyFableObjectAddress(surface)) {
    return;
  }

  RendererResourceRecord record;
  record.kind = RendererResourceKind::kTexture;
  record.guest_address = surface;
  record.parent_address = args.r3;
  SnapshotGuestResource(record, base);
  record.normalized_fetch_dwords =
      NormalizeTextureFetchDwords(TextureFetchDwordsForRecord(record));
  if (!HasTextureFetchDwords(record.normalized_fetch_dwords)) {
    return;
  }

  const uint32_t upload_size =
      std::max(TextureSnapshotSizeForRecord(record),
               TextureSnapshotSizeForRelatedRecordLocked(record));
  if (upload_size == 0) {
    return;
  }

  record.raw_size = std::max(record.raw_size, upload_size);
  record.serial = ++g_observer.serial;
  auto [record_it, inserted] =
      g_observer.resources.insert_or_assign(record.guest_address, record);
  g_observer.last_resource = record_it->second;
  if (inserted) {
    ++g_observer.resource_creates;
    TraceResourceCreate(record_it->second);
  }

  BinkVideoPlaneBinding binding = {
      .surface = surface,
      .plane_info = args.r6,
      .plane_output_slot = args.r7,
      .physical_address = LoadGuestU32(base, args.r6 + 4u),
      .lock_address = LoadGuestU32(base, args.r6 + 8u),
      .upload_size = upload_size,
      .serial = ++g_observer.serial,
  };
  g_observer.bink_planes_by_surface[surface] = binding;
  ++g_observer.bink_video_plane_setups;

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_bink_video_planes < 16u) {
    const uint32_t selected =
        SelectReadableUploadAddress(base,
                                    binding.lock_address != 0
                                        ? binding.lock_address
                                        : binding.physical_address,
                                    binding.upload_size);
    const uint32_t nonzero =
        selected != 0
            ? FindFirstNonZeroGuestByte(base, selected, binding.upload_size,
                                        binding.upload_size)
            : UINT32_MAX;
    const std::array<uint32_t, 6> dwords =
        TextureFetchDwordsForRecord(record_it->second);
    REXLOG_INFO(
        "Refii renderer Bink video plane #{} movie=0x{:08X} "
        "plane=0x{:08X} output_slot=0x{:08X} surface=0x{:08X} "
        "physical=0x{:08X} lock=0x{:08X} selected=0x{:08X} "
        "size={} nz={} dims={}x{} dwords={:08X}/{:08X}/{:08X}/"
        "{:08X}/{:08X}/{:08X}",
        g_observer.logged_bink_video_planes + 1u, args.r3, args.r6, args.r7,
        surface, binding.physical_address, binding.lock_address, selected,
        binding.upload_size,
        nonzero == UINT32_MAX ? -1 : static_cast<int32_t>(nonzero), args.r4,
        args.r5, dwords[0], dwords[1], dwords[2], dwords[3], dwords[4],
        dwords[5]);
    ++g_observer.logged_bink_video_planes;
  }

  RefreshBinkVideoPlaneUploadLocked(record_it->second, binding, base,
                                    "sub_82A42FA0");
}

uint32_t NormalizeGpuPointer(uint32_t pointer) {
  return NormalizeFableGpuAddress(pointer);
}

bool IsLikelyDisplayTextureBase(uint32_t texture_base) {
  const uint32_t normalized_base = NormalizeGpuPointer(texture_base);
  return IsLikelyGpuByteBufferRange(normalized_base, 1) ||
         IsLikelyFableObjectAddress(normalized_base);
}

void SetObservedDisplayPresentSurfaceLocked(
    uint32_t texture_base, uint32_t width, uint32_t height, uint32_t format,
    DisplayPresentSource source, uint32_t register_index = 0,
    uint32_t register_value = 0,
    const std::array<uint32_t, 6>* fetch_dwords = nullptr,
    uint32_t surface_object = 0) {
  const uint32_t normalized_base = NormalizeGpuPointer(texture_base);
  if (normalized_base != 0 && IsLikelyDisplayTextureBase(normalized_base)) {
    g_observer.display_present_texture_base = normalized_base;
  }
  if (surface_object != 0 && IsLikelyFableObjectAddress(surface_object)) {
    g_observer.display_present_surface_object = surface_object;
  }
  if (width != 0) {
    g_observer.display_present_width = width;
  }
  if (height != 0) {
    g_observer.display_present_height = height;
  }
  if (format != 0) {
    g_observer.display_present_format = format;
  }
  g_observer.display_present_source = static_cast<uint32_t>(source);
  g_observer.display_present_register = register_index;
  g_observer.display_present_register_value = register_value;
  if (fetch_dwords) {
    g_observer.display_present_fetch_dwords = *fetch_dwords;
  }
}

uint32_t SurfaceCopyHookAddress(std::string_view symbol) {
  if (symbol == "sub_82D160D8") {
    return 0x82D160D8u;
  }
  if (symbol == "sub_82D21C68") {
    return 0x82D21C68u;
  }
  if (symbol == "sub_82D14B20") {
    return 0x82D14B20u;
  }
  return 0;
}

void RecordSurfaceCopyAliasLocked(std::string_view symbol,
                                  uint32_t source_surface,
                                  uint32_t destination_surface) {
  if (!IsLikelyFableObjectAddress(source_surface) ||
      !IsLikelyFableObjectAddress(destination_surface) ||
      source_surface == destination_surface) {
    return;
  }

  RendererSurfaceCopyAliasSnapshot alias;
  alias.source_surface = source_surface;
  alias.destination_surface = destination_surface;
  alias.hook_address = SurfaceCopyHookAddress(symbol);
  alias.serial = ++g_observer.serial;
  g_observer.surface_copy_aliases[destination_surface] = alias;

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_surface_copy_aliases < 32u) {
    REXLOG_INFO(
        "Refii renderer surface copy alias hook={} source=0x{:08X} "
        "destination=0x{:08X} serial={}",
        symbol, source_surface, destination_surface,
        static_cast<unsigned long long>(alias.serial));
    ++g_observer.logged_surface_copy_aliases;
  }
}

void ObserveSurfaceCopyAliasLocked(std::string_view symbol,
                                   const RendererHookArgs& args,
                                   uint32_t result) {
  if (result != 0) {
    return;
  }

  if (symbol == "sub_82D160D8") {
    // D3DXLoadSurfaceFromSurface(dst_surface, ..., src_surface, ...).
    RecordSurfaceCopyAliasLocked(symbol, args.r6, args.r3);
    return;
  }

  if (symbol == "sub_82D21C68") {
    // IDA shows InternalCopy locks a1 then a2 and memcpy(dst=a2, src=a1).
    RecordSurfaceCopyAliasLocked(symbol, args.r3, args.r4);
  }
}

void ObserveDisplaySwapSurfaceLocked(std::string_view symbol,
                                     const RendererHookArgs& args,
                                     uint8_t* base) {
  if (!base) {
    return;
  }

  if (symbol == "sub_82BA32F8") {
    // D3D::ComplexSwap(device, flags, base, format, width, height, scaler, ...)
    // is the direct lowered present path used by D3DDevice_Swap after it has
    // normalized the surface fetch into a scan-out base. When it is reached
    // through D3DDevice_Swap, keep the richer fetch-backed source from the
    // outer hook and only refine dimensions/format/scaler here.
    const bool outer_swap_has_fetch =
        g_observer.display_present_source ==
            static_cast<uint32_t>(DisplayPresentSource::kD3DSwapSurface) &&
        g_observer.display_present_texture_base != 0 &&
        HasTextureFetchDwords(g_observer.display_present_fetch_dwords);
    const std::array<uint32_t, 6> empty_fetch = {};
    if (outer_swap_has_fetch || args.r5 != 0) {
      SetObservedDisplayPresentSurfaceLocked(
          outer_swap_has_fetch ? 0u : args.r5, args.r7, args.r8,
          args.r6 & 0x3Fu,
          DisplayPresentSource::kD3DSwapSurface, 0, 0,
          outer_swap_has_fetch ? nullptr : &empty_fetch);
    }
    if (args.r9 != 0) {
      ObserveScalerParametersFromPointerLocked(args, base, args.r9);
    }
    return;
  }

  if (symbol != "sub_82BA34D8" || args.r4 == 0) {
    return;
  }

  std::array<uint32_t, 6> fetch_dwords =
      LoadTextureFetchDwords(base, args.r4 + 28u);

  uint32_t format_word = fetch_dwords[1];
  const uint32_t raw_format = format_word & 0x3Fu;
  if (raw_format == 0x32u) {
    format_word = (format_word & 0xFFFFFFC0u) | 6u;
  } else if (raw_format == 7u) {
    format_word = (format_word & 0xFFFFFFC0u) | 54u;
  }

  const uint32_t size_word = fetch_dwords[2];
  const uint32_t msaa_bias = ((fetch_dwords[3] >> 30u) & 2u) + 1u;
  const TextureFetchSnapshotInfo fetch_info =
      GetTextureFetchSnapshotInfo(fetch_dwords);
  SetObservedDisplayPresentSurfaceLocked(
      fetch_info.base_address, (size_word & 0x1FFFu) + msaa_bias,
      ((size_word >> 13u) & 0x1FFFu) + msaa_bias, format_word & 0x3Fu,
      DisplayPresentSource::kD3DSwapSurface, 0, 0, &fetch_dwords, args.r4);
}

void ObserveDisplayRegisterWriteLocked(uint32_t register_index,
                                       uint32_t value) {
  if (register_index != kXenosDisplaySurfaceRegister) {
    return;
  }

  const bool swap_surface_has_fetch =
      g_observer.display_present_source ==
          static_cast<uint32_t>(DisplayPresentSource::kD3DSwapSurface) &&
      g_observer.display_present_texture_base != 0 &&
      HasTextureFetchDwords(g_observer.display_present_fetch_dwords);
  if (swap_surface_has_fetch) {
    // The PM4 display register is the lowered scan-out base emitted by swap.
    // D3DDevice_Swap still has the richer fetch object, which is required to
    // preserve video/present texture shape. Keep that source and only retain
    // the register metadata for diagnostics.
    g_observer.display_present_register = register_index;
    g_observer.display_present_register_value = value;
    return;
  }

  // The 360 display path writes the current scan-out base through a type-0 PM4
  // register packet after VdSwap/D3DDevice_Swap has normalized the source
  // texture pointer. Keep this as a first-class source so Plume follows the
  // command stream instead of relying only on the title's D3D wrapper state.
  SetObservedDisplayPresentSurfaceLocked(
      value & 0xFFFFF000u, 0, 0, 0, DisplayPresentSource::kPm4DisplayRegister,
      register_index, value);
}

void LogSuspiciousVertexDeclarationLocked(
    uint32_t declaration_address, const RendererResourceRecord& record,
    uint8_t* base) {
  if (!base || declaration_address == 0 ||
      record.kind != RendererResourceKind::kVertexDeclaration ||
      record.raw_stride_or_format <= 32u) {
    return;
  }
  if (!g_observer.logged_vertex_declaration_failures.insert(declaration_address)
           .second) {
    return;
  }

  const uint32_t element_address =
      record.raw_buffer_object != 0 ? record.raw_buffer_object
                                    : declaration_address + 0x34u;
  REXLOG_INFO(
      "Refii renderer vertex declaration 0x{:08X} has suspicious element "
      "count={} bytes={} elements=0x{:08X}; words obj+00/+04/+08/+0C/+10/"
      "+14/+18/+1C/+20/+24/+28/+2C/+30/+34 = "
      "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
      "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}; elem+00..+5C = "
      "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
      "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
      "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
      declaration_address, record.raw_stride_or_format, record.raw_size,
      element_address, LoadGuestU32(base, declaration_address + 0x00),
      LoadGuestU32(base, declaration_address + 0x04),
      LoadGuestU32(base, declaration_address + 0x08),
      LoadGuestU32(base, declaration_address + 0x0C),
      LoadGuestU32(base, declaration_address + 0x10),
      LoadGuestU32(base, declaration_address + 0x14),
      LoadGuestU32(base, declaration_address + 0x18),
      LoadGuestU32(base, declaration_address + 0x1C),
      LoadGuestU32(base, declaration_address + 0x20),
      LoadGuestU32(base, declaration_address + 0x24),
      LoadGuestU32(base, declaration_address + 0x28),
      LoadGuestU32(base, declaration_address + 0x2C),
      LoadGuestU32(base, declaration_address + 0x30),
      LoadGuestU32(base, declaration_address + 0x34),
      LoadGuestU32(base, element_address + 0x00),
      LoadGuestU32(base, element_address + 0x04),
      LoadGuestU32(base, element_address + 0x08),
      LoadGuestU32(base, element_address + 0x0C),
      LoadGuestU32(base, element_address + 0x10),
      LoadGuestU32(base, element_address + 0x14),
      LoadGuestU32(base, element_address + 0x18),
      LoadGuestU32(base, element_address + 0x1C),
      LoadGuestU32(base, element_address + 0x20),
      LoadGuestU32(base, element_address + 0x24),
      LoadGuestU32(base, element_address + 0x28),
      LoadGuestU32(base, element_address + 0x2C),
      LoadGuestU32(base, element_address + 0x30),
      LoadGuestU32(base, element_address + 0x34),
      LoadGuestU32(base, element_address + 0x38),
      LoadGuestU32(base, element_address + 0x3C),
      LoadGuestU32(base, element_address + 0x40),
      LoadGuestU32(base, element_address + 0x44),
      LoadGuestU32(base, element_address + 0x48),
      LoadGuestU32(base, element_address + 0x4C),
      LoadGuestU32(base, element_address + 0x50),
      LoadGuestU32(base, element_address + 0x54),
      LoadGuestU32(base, element_address + 0x58),
      LoadGuestU32(base, element_address + 0x5C));
}

uint32_t DecodePm4MemoryPointer(uint32_t pointer) {
  return DecodeFableGpuMemoryPointer(pointer);
}

bool IsLikelyPm4PacketHeader(uint32_t packet, uint32_t remaining_words) {
  if (packet == 0 || remaining_words == 0) {
    return false;
  }

  switch (packet >> 30) {
    case 0: {
      const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1u;
      return 1u + payload_words <= remaining_words;
    }
    case 1:
      return remaining_words >= 3u;
    case 2:
      return true;
    case 3: {
      const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1u;
      return 1u + payload_words <= remaining_words;
    }
    default:
      return false;
  }
}

bool DecodePm4IndirectTargetLocked(uint8_t* base, uint32_t pointer_raw,
                                   uint32_t word_count, uint32_t source,
                                   uint32_t depth);

uint32_t PrimitiveCountFromPm4IndexCount(uint32_t primitive_type,
                                         uint32_t index_count) {
  switch (primitive_type) {
    case 1:
      return index_count;
    case 2:
      return index_count / 2u;
    case 3:
      return index_count > 1u ? index_count - 1u : 0u;
    case 4:
      return index_count / 3u;
    case 8:
      return index_count;
    case 5:
    case 6:
      return index_count > 2u ? index_count - 2u : 0u;
    case 13:
      // D3DPT_QUADLIST is submitted as groups of four vertices/indices and
      // expanded to two triangles by the renderer replacement.
      return index_count / 4u;
    default:
      return 0;
  }
}

uint32_t VertexOrIndexCountFromD3DPrimitiveCount(uint32_t primitive_type,
                                                 uint32_t primitive_count) {
  switch (primitive_type) {
    case 1:
      return primitive_count;
    case 2:
      return primitive_count * 2u;
    case 3:
      return primitive_count + 1u;
    case 4:
      return primitive_count * 3u;
    case 5:
    case 6:
      return primitive_count + 2u;
    case 8:
      return primitive_count * 3u;
    case 13:
      return primitive_count * 4u;
    default:
      return primitive_count;
  }
}

void ApplyTextureObjectFetchConstantsToEventLocked(
    RendererDrawEventSnapshot& event);

void ApplyVertexFetchConstantBindingLocked(uint32_t fetch_index) {
  if (fetch_index * 2u + 1u >= kFetchConstantDwordCount) {
    return;
  }

  rex::graphics::xenos::xe_gpu_vertex_fetch_t fetch = {};
  fetch.dword_0 = g_observer.fetch_constants[fetch_index * 2u + 0u];
  fetch.dword_1 = g_observer.fetch_constants[fetch_index * 2u + 1u];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kVertex) {
    return;
  }

  const uint32_t buffer_address = fetch.address << 2u;
  const uint32_t buffer_size = fetch.size << 2u;
  if (!IsLikelyGpuBufferRange(buffer_address, buffer_size)) {
    return;
  }

  UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                    buffer_address, buffer_size, 0,
                                    kFetchConstantRegisterFirst +
                                        fetch_index * 2u);
}

void RefreshQueuedDrawTextureStateLocked() {
  for (RendererDrawEventSnapshot& draw_event : g_observer.draw_events) {
    ApplyTextureObjectFetchConstantsToEventLocked(draw_event);
    draw_event.resource_serial =
        std::max(draw_event.resource_serial, g_observer.serial);
  }
}

void ObserveFetchConstantWriteLocked(uint32_t register_index, uint32_t value) {
  if (register_index < kFetchConstantRegisterFirst ||
      register_index > kFetchConstantRegisterLast) {
    return;
  }

  const uint32_t fetch_dword = register_index - kFetchConstantRegisterFirst;
  if (fetch_dword >= kFetchConstantDwordCount) {
    return;
  }

  g_observer.fetch_constants[fetch_dword] = value;
  ++g_observer.fetch_constant_uploads;
  RefreshQueuedDrawTextureStateLocked();
  ApplyVertexFetchConstantBindingLocked(fetch_dword / 2u);
}

void UpsertTextureResourceFromFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords, uint8_t* base) {
  if (!base || !HasTextureFetchDwords(dwords)) {
    return;
  }

  rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dwords[0];
  fetch.dword_1 = dwords[1];
  fetch.dword_2 = dwords[2];
  fetch.dword_3 = dwords[3];
  fetch.dword_4 = dwords[4];
  fetch.dword_5 = dwords[5];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kTexture) {
    return;
  }

  const uint32_t texture_base = fetch.base_address << 12;
  if (texture_base == 0) {
    return;
  }

  auto it = g_observer.resources.find(texture_base);
  const bool created = it == g_observer.resources.end();
  if (created) {
    RendererResourceRecord record;
    record.kind = RendererResourceKind::kTexture;
    record.guest_address = texture_base;
    record.parent_address = texture_base;
    it = g_observer.resources.emplace(texture_base, std::move(record)).first;
    ++g_observer.resource_creates;
  } else if (!IsImageResourceKind(it->second.kind)) {
    return;
  }

  RendererResourceRecord& record = it->second;
  record.normalized_fetch_dwords = dwords;
  if (record.kind == RendererResourceKind::kTexture) {
    record.raw_packed28 = dwords[0];
    record.raw_packed32 = dwords[1];
    record.raw_packed36 = dwords[2];
    record.raw_packed40 = dwords[3];
    record.raw_packed44 = dwords[4];
    record.raw_packed48 = dwords[5];
  }
  if (record.kind == RendererResourceKind::kTexture &&
      record.upload_data.empty()) {
    CaptureTextureUploadFromFetchDwords(record, dwords, base);
  }
  RefreshTextureUploadFromPendingPixDecodedFrameLocked(record, dwords, base);
  record.serial = ++g_observer.serial;
  g_observer.last_resource = record;
  RefreshQueuedDrawTextureResourceSerialLocked(record);
}

void ObserveTextureFetchConstantLocked(const RendererHookArgs& args,
                                       uint8_t* base) {
  if (!base || args.r5 == 0) {
    return;
  }

  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t texture_slot = args.r4;
  const uint32_t fetch_dword = texture_slot * kTextureFetchConstantDwords;
  if (texture_slot >= 32u ||
      fetch_dword + kTextureFetchConstantDwords >
          g_observer.fetch_constants.size()) {
    return;
  }

  const std::array<uint32_t, 6> dwords = LoadTextureFetchDwords(base, args.r5);
  UpsertTextureResourceFromFetchDwordsLocked(dwords, base);
  for (uint32_t i = 0; i < kTextureFetchConstantDwords; ++i) {
    g_observer.fetch_constants[fetch_dword + i] = dwords[i];
  }

  ++g_observer.fetch_constant_uploads;
  RefreshQueuedDrawTextureStateLocked();

  if (RendererDrawTracingEnabled() && !g_observer.logged_fetch_constants) {
    REXLOG_INFO(
        "Refii renderer captured texture fetch constant slot={} src=0x{:08X} "
        "lr=0x{:08X}",
        texture_slot, args.r5, args.lr);
    g_observer.logged_fetch_constants = true;
  }
}

void StoreTextureFetchConstantLocked(uint32_t texture_slot,
                                     uint32_t source_address, uint8_t* base,
                                     uint32_t log_lr,
                                     bool update_existing_draws = true) {
  if (!base || source_address == 0) {
    return;
  }

  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t fetch_dword = texture_slot * kTextureFetchConstantDwords;
  if (texture_slot >= 32u ||
      fetch_dword + kTextureFetchConstantDwords >
          g_observer.fetch_constants.size()) {
    return;
  }

  const std::array<uint32_t, 6> dwords =
      LoadTextureFetchDwords(base, source_address);
  UpsertTextureResourceFromFetchDwordsLocked(dwords, base);
  for (uint32_t i = 0; i < kTextureFetchConstantDwords; ++i) {
    g_observer.fetch_constants[fetch_dword + i] = dwords[i];
  }

  ++g_observer.fetch_constant_uploads;
  if (update_existing_draws) {
    RefreshQueuedDrawTextureStateLocked();
  }

  if (RendererDrawTracingEnabled() && !g_observer.logged_fetch_constants) {
    REXLOG_INFO(
        "Refii renderer captured texture fetch constant slot={} src=0x{:08X} "
        "lr=0x{:08X}",
        texture_slot, source_address, log_lr);
    g_observer.logged_fetch_constants = true;
  }
}

void StoreTextureObjectFetchDwordsLocked(uint32_t texture_object,
                                         uint32_t source_address,
                                         uint32_t texture_slot,
                                         uint8_t* base, uint32_t log_lr,
                                         bool refresh_upload = false,
                                         bool update_existing_draws = true) {
  if (!base || texture_object == 0 || source_address == 0) {
    return;
  }

  auto it = FindResourceIteratorLocked(texture_object);
  if (it == g_observer.resources.end()) {
    RendererResourceRecord record;
    record.kind = RendererResourceKind::kTexture;
    record.guest_address = texture_object;
    record.parent_address = texture_object;
    SnapshotGuestResource(record, base);
    record.serial = ++g_observer.serial;
    it = g_observer.resources.emplace(texture_object, std::move(record)).first;
    ++g_observer.resource_creates;
  } else if (!IsImageResourceKind(it->second.kind)) {
    return;
  }

  const std::array<uint32_t, 6> dwords =
      LoadTextureFetchDwords(base, source_address);
  if (!HasTextureFetchDwords(dwords)) {
    return;
  }

  const bool first_fetch = !HasTextureFetchDwords(it->second.normalized_fetch_dwords);
  it->second.normalized_fetch_dwords = dwords;
  if (it->second.kind == RendererResourceKind::kTexture) {
    it->second.raw_packed28 = dwords[0];
    it->second.raw_packed32 = dwords[1];
    it->second.raw_packed36 = dwords[2];
    it->second.raw_packed40 = dwords[3];
    it->second.raw_packed44 = dwords[4];
    it->second.raw_packed48 = dwords[5];
  }
  if (it->second.kind == RendererResourceKind::kTexture &&
      (refresh_upload || it->second.upload_data.empty())) {
    CaptureTextureUploadFromFetchDwords(it->second, dwords, base);
  }
  RefreshTextureUploadFromPendingPixDecodedFrameLocked(it->second, dwords,
                                                      base);
  it->second.serial = ++g_observer.serial;
  g_observer.last_resource = it->second;
  RefreshQueuedDrawTextureResourceSerialLocked(it->second);

  if (texture_slot < 32u) {
    const uint32_t fetch_dword = texture_slot * 6u;
    if (fetch_dword + dwords.size() <= g_observer.fetch_constants.size()) {
      for (uint32_t i = 0; i < dwords.size(); ++i) {
        g_observer.fetch_constants[fetch_dword + i] = dwords[i];
      }
      if (update_existing_draws) {
        RefreshQueuedDrawTextureStateLocked();
      }
    }
  }

  if (RendererDrawTracingEnabled() && first_fetch) {
    REXLOG_INFO(
        "Refii renderer normalized texture fetch object=0x{:08X} slot={} "
        "src=0x{:08X} dwords={:08X} {:08X} {:08X} {:08X} {:08X} {:08X} "
        "lr=0x{:08X}",
        texture_object, texture_slot, source_address, dwords[0], dwords[1],
        dwords[2], dwords[3], dwords[4], dwords[5], log_lr);
  }
}

void ObservePendingFetchConstantsLocked(const RendererHookArgs& args,
                                        uint8_t* base) {
  if (!base || args.r3 == 0) {
    return;
  }

  uint64_t dirty_mask = args.r4_u64;
  if ((dirty_mask >> 32u) == 0 && args.r4 != 0) {
    dirty_mask = static_cast<uint64_t>(args.r4) << 32u;
  }
  if (dirty_mask == 0) {
    return;
  }

  for (uint32_t slot = 0; slot < 32u; ++slot) {
    if ((dirty_mask & (1ull << (63u - slot))) == 0) {
      continue;
    }
    StoreTextureFetchConstantLocked(slot, args.r3 + 0x480u + slot * 24u, base,
                                    args.lr);
  }
}

void ObserveRegisterWriteLocked(uint32_t register_index, uint32_t value) {
  ObserveDisplayRegisterWriteLocked(register_index, value);
  ObserveFetchConstantWriteLocked(register_index, value);
  switch (register_index) {
    case kXenosRegRbSurfaceInfo:
      g_observer.rb_surface_info = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbColorInfo0:
      g_observer.rb_color_info[0] = value;
      g_observer.rb_color_info_valid[0] = true;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbColorInfo1:
      g_observer.rb_color_info[1] = value;
      g_observer.rb_color_info_valid[1] = true;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbColorInfo2:
      g_observer.rb_color_info[2] = value;
      g_observer.rb_color_info_valid[2] = true;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbColorInfo3:
      g_observer.rb_color_info[3] = value;
      g_observer.rb_color_info_valid[3] = true;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbDepthInfo:
      g_observer.rb_depth_info = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopyControl:
      g_observer.rb_copy_control = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopyDestBase:
      g_observer.rb_copy_dest_base = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopyDestPitch:
      g_observer.rb_copy_dest_pitch = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopyDestInfo:
      g_observer.rb_copy_dest_info = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopyFunc:
      g_observer.rb_copy_func = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopyRef:
      g_observer.rb_copy_ref = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopyMask:
      g_observer.rb_copy_mask = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbCopySurfaceSlice:
      g_observer.rb_copy_surface_slice = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbColorMask:
      g_observer.rb_color_mask = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbStencilRefMaskBf:
      g_observer.rb_stencilrefmask_bf = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbStencilRefMask:
      g_observer.rb_stencilrefmask = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbAlphaRef:
      g_observer.rb_alpha_ref = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbDepthControl:
      g_observer.rb_depthcontrol = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbBlendControl0:
      g_observer.rb_blendcontrol[0] = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbColorControl:
      g_observer.rb_colorcontrol = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegPaSuScModeCntl:
      g_observer.pa_su_sc_mode_cntl = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbModeControl:
      g_observer.rb_modecontrol = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbBlendControl1:
      g_observer.rb_blendcontrol[1] = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbBlendControl2:
      g_observer.rb_blendcontrol[2] = value;
      g_observer.pm4_registers_valid = true;
      break;
    case kXenosRegRbBlendControl3:
      g_observer.rb_blendcontrol[3] = value;
      g_observer.pm4_registers_valid = true;
      break;
    default:
      break;
  }
  if (register_index < kAluConstantRegisterFirst ||
      register_index > kAluConstantRegisterLast) {
    return;
  }

  const bool pixel_bank = register_index >= kAluConstantRegisterPixelFirst;
  auto& constants = pixel_bank ? g_observer.pixel_shader_constants
                               : g_observer.vertex_shader_constants;
  auto& valid_mask = pixel_bank ? g_observer.pixel_shader_constant_valid
                                : g_observer.vertex_shader_constant_valid;
  const uint32_t dword_index =
      register_index >= kAluConstantRegisterPixelFirst
          ? register_index - kAluConstantRegisterPixelFirst
          : register_index - kAluConstantRegisterFirst;
  if (dword_index >= constants.size()) {
    return;
  }

  constants[dword_index] = value;
  valid_mask[dword_index] = true;
}

void ObservePendingRegisterRangeLocked(const RendererHookArgs& args,
                                       uint8_t* base) {
  if (!base || args.r6 == 0) {
    return;
  }

  uint64_t dirty_mask = args.r4_u64;
  if ((dirty_mask >> 32u) == 0 && args.r4 != 0) {
    dirty_mask = static_cast<uint64_t>(args.r4) << 32u;
  }
  if (dirty_mask == 0) {
    return;
  }

  uint32_t observed_shader_dwords = 0;
  uint32_t observed_nonzero_shader_dwords = 0;
  uint32_t first_shader_register_index = 0;
  for (uint32_t slot = 0; slot < 64u; ++slot) {
    if ((dirty_mask & (1ull << (63u - slot))) == 0) {
      continue;
    }

    const uint32_t register_index = args.r5 + slot;
    const uint32_t value = LoadGuestU32(base, args.r6 + slot * 4u);
    ObserveRegisterWriteLocked(register_index, value);
    if (register_index >= kAluConstantRegisterFirst &&
        register_index <= kAluConstantRegisterLast) {
      if (observed_shader_dwords == 0) {
        first_shader_register_index = register_index;
      }
      ++observed_shader_dwords;
      if (value != 0) {
        ++observed_nonzero_shader_dwords;
      }
    }
  }

  if (observed_shader_dwords != 0) {
    ++g_observer.shader_constant_uploads;
    g_observer.shader_constant_upload_nonzero_dwords +=
        observed_nonzero_shader_dwords;
    if (observed_nonzero_shader_dwords == 0) {
      ++g_observer.shader_constant_zero_uploads;
    }
    g_observer.last_shader_constant_bank =
        first_shader_register_index >= kAluConstantRegisterPixelFirst ? 1u
                                                                      : 0u;
    g_observer.last_shader_constant_first_register =
        first_shader_register_index >= kAluConstantRegisterPixelFirst
            ? first_shader_register_index - kAluConstantRegisterPixelFirst
            : first_shader_register_index - kAluConstantRegisterFirst;
    g_observer.last_shader_constant_first_component = 0;
    g_observer.last_shader_constant_dwords = observed_shader_dwords;
    g_observer.last_shader_constant_nonzero_dwords =
        observed_nonzero_shader_dwords;
    g_observer.last_shader_constant_source = args.r6;
    g_observer.last_shader_constant_lr = args.lr;
    for (uint32_t i = 0; i < std::size(g_observer.last_shader_constant_preview);
         ++i) {
      g_observer.last_shader_constant_preview[i] =
          LoadGuestU32(base, args.r6 + i * 4u);
    }
  }
}

enum class ShaderConstantBank {
  kVertex,
  kPixel,
};

void StoreShaderConstantVectorsLocked(ShaderConstantBank bank,
                                      uint32_t first_register,
                                      uint32_t vector_count,
                                      uint32_t source_address,
                                      uint8_t* base, uint32_t log_lr);
void StoreShaderConstantDwordsLocked(ShaderConstantBank bank,
                                     uint32_t first_register,
                                     uint32_t first_component,
                                     uint32_t dword_count,
                                     uint32_t source_address, uint8_t* base,
                                     uint32_t log_lr);
void StoreShaderIntConstantsLocked(ShaderConstantBank bank,
                                   uint32_t first_register,
                                   uint32_t vector_count,
                                   uint32_t source_address, uint8_t* base,
                                   uint32_t log_lr);

constexpr uint32_t kAluConstantPixelVectorOffset =
    (kAluConstantRegisterPixelFirst - kAluConstantRegisterFirst) / 4u;

ShaderConstantBank GuessImmediateShaderConstantBank(
    const RendererHookArgs& args) {
  if (args.r4 >= kAluConstantPixelVectorOffset) {
    return ShaderConstantBank::kPixel;
  }
  if (args.lr >= kPixelConstantUploadCallerFirst &&
      args.lr <= kPixelConstantUploadCallerLast) {
    return ShaderConstantBank::kPixel;
  }
  if (args.lr >= kVertexConstantUploadCallerFirst &&
      args.lr <= kVertexConstantUploadCallerLast) {
    return ShaderConstantBank::kVertex;
  }
  return ShaderConstantBank::kVertex;
}

void ObserveImmediateShaderConstantsLocked(const RendererHookArgs& args,
                                           uint8_t* base) {
  if (!base || args.r5 == 0 || args.r6 == 0) {
    return;
  }

  const ShaderConstantBank bank = GuessImmediateShaderConstantBank(args);
  uint32_t first_register = args.r4;
  if (bank == ShaderConstantBank::kPixel &&
      first_register >= kAluConstantPixelVectorOffset) {
    first_register -= kAluConstantPixelVectorOffset;
  }
  StoreShaderConstantVectorsLocked(bank, first_register, args.r6, args.r5,
                                   base, args.lr);
}

ShaderConstantBank GuessLiteralShaderConstantBank(const RendererHookArgs& args,
                                                  uint8_t* base) {
  if (base) {
    if (g_observer.vertex_shader != 0 &&
        args.r4 == g_observer.vertex_shader + 872u &&
        args.r5 == LoadGuestU32(base, g_observer.vertex_shader + 32u)) {
      return ShaderConstantBank::kVertex;
    }
    if (g_observer.pixel_shader != 0 &&
        args.r4 == g_observer.pixel_shader + 40u &&
        args.r5 == LoadGuestU32(base, g_observer.pixel_shader + 24u)) {
      return ShaderConstantBank::kPixel;
    }
  }

  if (args.lr == 0x8221B1D8u) {
    return ShaderConstantBank::kPixel;
  }
  if (args.lr == 0x8221B3A0u || args.lr == 0x8221B4A8u) {
    return ShaderConstantBank::kVertex;
  }
  return ShaderConstantBank::kVertex;
}

void StoreShaderConstantVectorsLocked(ShaderConstantBank bank,
                                      uint32_t first_register,
                                      uint32_t vector_count,
                                      uint32_t source_address,
                                      uint8_t* base, uint32_t log_lr) {
  const uint32_t clamped_vectors =
      std::min<uint32_t>(vector_count, kMaxShaderConstantVectorUpload);
  StoreShaderConstantDwordsLocked(bank, first_register, 0,
                                  clamped_vectors * 4u, source_address, base,
                                  log_lr);
}

void StoreShaderConstantDwordsLocked(ShaderConstantBank bank,
                                     uint32_t first_register,
                                     uint32_t first_component,
                                     uint32_t dword_count,
                                     uint32_t source_address, uint8_t* base,
                                     uint32_t log_lr) {
  if (!base || source_address == 0 || dword_count == 0) {
    return;
  }

  if (bank == ShaderConstantBank::kPixel && first_register >= 256u) {
    first_register -= 256u;
  }
  if (first_register >= 256u) {
    ++g_observer.shader_constant_skipped_uploads;
    return;
  }

  auto& constants = bank == ShaderConstantBank::kPixel
                        ? g_observer.pixel_shader_constants
                        : g_observer.vertex_shader_constants;
  auto& valid_mask = bank == ShaderConstantBank::kPixel
                         ? g_observer.pixel_shader_constant_valid
                         : g_observer.vertex_shader_constant_valid;
  if (first_component >= 4u) {
    first_register += first_component / 4u;
    first_component %= 4u;
  }
  const uint32_t first_dword = first_register * 4u + first_component;
  if (first_dword >= constants.size()) {
    ++g_observer.shader_constant_skipped_uploads;
    return;
  }

  const uint32_t writable_dwords =
      std::min<uint32_t>(dword_count, constants.size() - first_dword);
  uint32_t upload_nonzero_dwords = 0;
  uint32_t upload_preview[4] = {};
  for (uint32_t dword_index = 0; dword_index < writable_dwords; ++dword_index) {
    const uint32_t value =
        LoadGuestU32(base, source_address + dword_index * sizeof(uint32_t));
    constants[first_dword + dword_index] = value;
    valid_mask[first_dword + dword_index] = true;
    if (dword_index < std::size(upload_preview)) {
      upload_preview[dword_index] = value;
    }
    if (value != 0) {
      ++upload_nonzero_dwords;
    }
  }

  ++g_observer.shader_constant_uploads;
  g_observer.shader_constant_upload_nonzero_dwords += upload_nonzero_dwords;
  if (upload_nonzero_dwords == 0) {
    ++g_observer.shader_constant_zero_uploads;
  }
  g_observer.last_shader_constant_bank =
      bank == ShaderConstantBank::kPixel ? 1u : 0u;
  g_observer.last_shader_constant_first_register = first_register;
  g_observer.last_shader_constant_first_component = first_component;
  g_observer.last_shader_constant_dwords = writable_dwords;
  g_observer.last_shader_constant_nonzero_dwords = upload_nonzero_dwords;
  g_observer.last_shader_constant_source = source_address;
  g_observer.last_shader_constant_lr = log_lr;
  std::memcpy(g_observer.last_shader_constant_preview, upload_preview,
              sizeof(g_observer.last_shader_constant_preview));
  if (RendererDrawTracingEnabled() && !g_observer.logged_shader_constants) {
    REXLOG_INFO(
        "Refii renderer captured {} shader constants first={} dwords={} "
        "nonzero={} src=0x{:08X} lr=0x{:08X} data={:08X} {:08X} {:08X} "
        "{:08X}",
        bank == ShaderConstantBank::kPixel ? "pixel" : "vertex",
        first_register, writable_dwords, upload_nonzero_dwords,
        source_address, log_lr, upload_preview[0], upload_preview[1],
        upload_preview[2], upload_preview[3]);
    g_observer.logged_shader_constants = true;
  }
}

void StoreShaderIntConstantsLocked(ShaderConstantBank bank,
                                   uint32_t first_register,
                                   uint32_t vector_count,
                                   uint32_t source_address, uint8_t* base,
                                   uint32_t log_lr) {
  if (!base || source_address == 0 || vector_count == 0) {
    return;
  }

  if (bank == ShaderConstantBank::kPixel && first_register >= 256u) {
    first_register -= 256u;
  }
  if (first_register >= 32u) {
    ++g_observer.shader_constant_skipped_uploads;
    return;
  }

  auto& constants = bank == ShaderConstantBank::kPixel
                        ? g_observer.pixel_shader_int_constants
                        : g_observer.vertex_shader_int_constants;
  uint32_t& valid_mask = bank == ShaderConstantBank::kPixel
                             ? g_observer.pixel_shader_int_constant_valid_mask
                             : g_observer.vertex_shader_int_constant_valid_mask;
  const uint32_t writable_vectors =
      std::min<uint32_t>(vector_count, 32u - first_register);
  uint32_t upload_nonzero_dwords = 0;
  uint32_t upload_preview[4] = {};
  for (uint32_t vector_index = 0; vector_index < writable_vectors;
       ++vector_index) {
    const uint32_t constant_dword = (first_register + vector_index) * 4u;
    const uint32_t source_vector = source_address + vector_index * 16u;
    for (uint32_t component = 0; component < 4u; ++component) {
      const uint32_t value =
          LoadGuestU32(base, source_vector + component * sizeof(uint32_t));
      constants[constant_dword + component] = value;
      if (vector_index == 0 && component < std::size(upload_preview)) {
        upload_preview[component] = value;
      }
      if (value != 0) {
        ++upload_nonzero_dwords;
      }
    }
    valid_mask |= 1u << (first_register + vector_index);
  }

  ++g_observer.shader_constant_uploads;
  g_observer.shader_constant_upload_nonzero_dwords += upload_nonzero_dwords;
  if (upload_nonzero_dwords == 0) {
    ++g_observer.shader_constant_zero_uploads;
  }
  g_observer.last_shader_constant_bank =
      bank == ShaderConstantBank::kPixel ? 1u : 0u;
  g_observer.last_shader_constant_first_register = first_register;
  g_observer.last_shader_constant_first_component = 0;
  g_observer.last_shader_constant_dwords = writable_vectors * 4u;
  g_observer.last_shader_constant_nonzero_dwords = upload_nonzero_dwords;
  g_observer.last_shader_constant_source = source_address;
  g_observer.last_shader_constant_lr = log_lr;
  std::memcpy(g_observer.last_shader_constant_preview, upload_preview,
              sizeof(g_observer.last_shader_constant_preview));
  if (RendererDrawTracingEnabled() && !g_observer.logged_shader_constants) {
    REXLOG_INFO(
        "Refii renderer captured {} shader integer constants first={} "
        "vectors={} nonzero={} src=0x{:08X} lr=0x{:08X} data={:08X} "
        "{:08X} {:08X} {:08X}",
        bank == ShaderConstantBank::kPixel ? "pixel" : "vertex",
        first_register, writable_vectors, upload_nonzero_dwords,
        source_address, log_lr, upload_preview[0], upload_preview[1],
        upload_preview[2], upload_preview[3]);
    g_observer.logged_shader_constants = true;
  }
}

void StoreShaderBoolConstantsLocked(ShaderConstantBank bank,
                                    uint32_t first_register,
                                    uint32_t bool_count,
                                    uint32_t source_address, uint8_t* base,
                                    uint32_t log_lr) {
  if (!base || source_address == 0 || bool_count == 0) {
    return;
  }

  if (bank == ShaderConstantBank::kPixel && first_register >= 256u) {
    first_register -= 256u;
  }

  auto& constants = bank == ShaderConstantBank::kPixel
                        ? g_observer.pixel_shader_bool_constants
                        : g_observer.vertex_shader_bool_constants;
  uint32_t packed_preview = 0;
  const uint32_t writable_bools =
      std::min<uint32_t>(bool_count,
                         kRendererShaderBoolConstantDwords * 32u);
  for (uint32_t bool_index = 0; bool_index < writable_bools; ++bool_index) {
    const uint32_t register_index = first_register + bool_index;
    const uint32_t dword_index = register_index / 32u;
    if (dword_index >= constants.size()) {
      break;
    }

    const uint32_t bit = 1u << (register_index & 31u);
    const uint32_t value =
        LoadGuestU32(base, source_address + bool_index * sizeof(uint32_t));
    if ((value & 1u) != 0) {
      constants[dword_index] |= bit;
      packed_preview |= bit;
    } else {
      constants[dword_index] &= ~bit;
    }
  }

  ++g_observer.shader_bool_constant_uploads;
  g_observer.last_shader_bool_constant_bank =
      bank == ShaderConstantBank::kPixel ? 1u : 0u;
  g_observer.last_shader_bool_constant_first_register = first_register;
  g_observer.last_shader_bool_constant_count = writable_bools;
  g_observer.last_shader_bool_constant_packed = packed_preview;
  if (!g_observer.logged_shader_bool_constants) {
    REXLOG_INFO(
        "Refii renderer captured {} shader bool constants first={} count={} "
        "packed=0x{:08X} src=0x{:08X} lr=0x{:08X}",
        bank == ShaderConstantBank::kPixel ? "pixel" : "vertex",
        first_register, writable_bools, packed_preview, source_address, log_lr);
    g_observer.logged_shader_bool_constants = true;
  }
}

void ObserveLiteralShaderConstantsLocked(const RendererHookArgs& args,
                                         uint8_t* base) {
  if (!base || args.r4 == 0 || args.r5 == 0) {
    return;
  }

  const uint32_t literal_table_offset = LoadGuestU32(base, args.r4 + 20u);
  if (literal_table_offset == 0) {
    return;
  }

  const ShaderConstantBank bank = GuessLiteralShaderConstantBank(args, base);
  const uint32_t literal_table = args.r4 + literal_table_offset;
  uint32_t entry = literal_table + 20u;
  const uint32_t entry_count_bytes = LoadGuestU32(base, literal_table + 16u);
  if (entry_count_bytes == 0 || entry_count_bytes > (4096u * 8u)) {
    return;
  }

  const uint32_t end = entry + entry_count_bytes;
  while (entry + 8u <= end) {
    const uint16_t first_register = LoadGuestU16(base, entry);
    const uint16_t dword_count = LoadGuestU16(base, entry + 2u);
    const uint32_t source_offset = LoadGuestU32(base, entry + 4u);
    if (dword_count == 0) {
      break;
    }

    StoreShaderConstantDwordsLocked(bank, first_register, 0, dword_count,
                                    args.r5 + source_offset, base, args.lr);
    entry += 8u;
  }
}

void ObserveRegisterRangePayloadLocked(uint8_t* base, uint32_t command_pointer,
                                       uint32_t first_payload_offset,
                                       uint32_t payload_words,
                                       uint32_t register_index) {
  for (uint32_t i = 0; i < payload_words; ++i) {
    const uint32_t value =
        LoadGuestU32(base, command_pointer +
                               (first_payload_offset + i) * sizeof(uint32_t));
    ObserveRegisterWriteLocked(register_index + i, value);
  }
}

void ObserveType3ConstantPacketLocked(uint8_t* base, uint32_t command_pointer,
                                      uint32_t offset, uint32_t opcode,
                                      uint32_t payload_words) {
  if (!base || payload_words < 2) {
    return;
  }

  const uint32_t offset_type =
      LoadGuestU32(base, command_pointer + (offset + 1) * sizeof(uint32_t));
  uint32_t register_index = 0;
  if (opcode == 0x2D) {
    const uint32_t index = offset_type & 0x7FFu;
    const uint32_t constant_type = (offset_type >> 16) & 0xFFu;
    if (constant_type == kSetConstantFetchType) {
      register_index = kFetchConstantRegisterFirst + index;
    } else if (constant_type == kSetConstantRegisterType) {
      register_index = index >= kAluConstantRegisterFirst
                           ? index
                           : kAluConstantRegisterFirst + index;
    } else {
      return;
    }
  } else if (opcode == 0x55 || opcode == 0x56) {
    register_index = offset_type & 0xFFFFu;
  } else {
    return;
  }

  ObserveRegisterRangePayloadLocked(base, command_pointer, offset + 2,
                                    payload_words - 1, register_index);
}

void ObserveType3AluConstantPacketLocked(uint8_t* base, uint32_t command_pointer,
                                         uint32_t offset,
                                         uint32_t payload_words) {
  if (!base || payload_words < 3) {
    return;
  }

  const uint32_t source_address =
      DecodePm4MemoryPointer(LoadGuestU32(
          base, command_pointer + (offset + 1u) * sizeof(uint32_t)));
  const uint32_t destination_offset =
      LoadGuestU32(base, command_pointer + (offset + 2u) * sizeof(uint32_t));
  const uint32_t dword_count =
      LoadGuestU32(base, command_pointer + (offset + 3u) * sizeof(uint32_t));
  const uint32_t first_register = destination_offset / 4u;
  const uint32_t first_component = destination_offset & 3u;
  const ShaderConstantBank bank =
      first_register >= 256u ? ShaderConstantBank::kPixel
                             : ShaderConstantBank::kVertex;
  StoreShaderConstantDwordsLocked(bank, first_register, first_component,
                                  dword_count, source_address, base,
                                  command_pointer);
}

void ObserveType3ShaderLoadPacketLocked(uint8_t* base, uint32_t command_pointer,
                                        uint32_t offset, uint32_t opcode,
                                        uint32_t payload_words) {
  if (!base || payload_words < 2) {
    return;
  }

  uint32_t shader_type = 0;
  uint32_t bytecode_address = 0;
  uint32_t size_dwords = 0;
  if (opcode == 0x27) {
    const uint32_t addr_type =
        LoadGuestU32(base, command_pointer + (offset + 1u) * sizeof(uint32_t));
    const uint32_t start_size =
        LoadGuestU32(base, command_pointer + (offset + 2u) * sizeof(uint32_t));
    shader_type = addr_type & 0x3u;
    bytecode_address = addr_type & ~0x3u;
    size_dwords = start_size & 0xFFFFu;
  } else if (opcode == 0x2B) {
    if (payload_words < 3) {
      return;
    }
    const uint32_t dword0 =
        LoadGuestU32(base, command_pointer + (offset + 1u) * sizeof(uint32_t));
    const uint32_t start_size =
        LoadGuestU32(base, command_pointer + (offset + 2u) * sizeof(uint32_t));
    shader_type = dword0;
    bytecode_address = command_pointer + (offset + 3u) * sizeof(uint32_t);
    size_dwords = start_size & 0xFFFFu;
    if (size_dwords > payload_words - 2u) {
      return;
    }
  } else {
    return;
  }

  if (size_dwords == 0) {
    return;
  }

  RendererResourceKind kind = RendererResourceKind::kUnknown;
  if (shader_type == 0) {
    kind = RendererResourceKind::kVertexShader;
  } else if (shader_type == 1) {
    kind = RendererResourceKind::kPixelShader;
  } else {
    return;
  }

  const uint32_t byte_size = size_dwords * sizeof(uint32_t);
  if (!UpsertPm4ShaderResourceLocked(kind, bytecode_address, byte_size,
                                     command_pointer, base)) {
    return;
  }

  if (kind == RendererResourceKind::kVertexShader) {
    g_observer.vertex_shader = bytecode_address;
  } else {
    g_observer.pixel_shader = bytecode_address;
  }
}

void ObserveType3DrawPacketLocked(uint8_t* base, uint32_t command_pointer,
                                  uint32_t offset, uint32_t opcode,
                                  uint32_t payload_words) {
  if (!base || payload_words == 0) {
    return;
  }

  const bool has_viz_query = opcode == 0x22 || opcode == 0x34;
  const uint32_t initiator_payload_offset = has_viz_query ? 1u : 0u;
  if (payload_words <= initiator_payload_offset) {
    return;
  }

  const uint32_t initiator =
      LoadGuestU32(base, command_pointer +
                             (offset + 1u + initiator_payload_offset) *
                                 sizeof(uint32_t));
  const uint32_t primitive_type = initiator & 0x3Fu;
  const uint32_t source_select = (initiator >> 6) & 0x3u;
  const bool index_32bit = ((initiator >> 11) & 0x1u) != 0;
  const uint32_t index_count = initiator >> 16;
  const uint32_t primitive_count =
      PrimitiveCountFromPm4IndexCount(primitive_type, index_count);
  if (primitive_count == 0) {
    return;
  }

  RendererLastDrawSnapshot draw;
  draw.primitive_type = primitive_type;
  draw.primitive_count = index_count;

  if (source_select == 0) {
    const uint32_t dma_payload_offset = initiator_payload_offset + 1u;
    if (payload_words < dma_payload_offset + 2u) {
      return;
    }

    const uint32_t dma_base_raw =
        LoadGuestU32(base, command_pointer +
                               (offset + 1u + dma_payload_offset) *
                                   sizeof(uint32_t));
    const uint32_t dma_size =
        LoadGuestU32(base, command_pointer +
                               (offset + 2u + dma_payload_offset) *
                                   sizeof(uint32_t));
    const uint32_t index_buffer = NormalizeGpuPointer(dma_base_raw);
    const uint32_t index_stride = index_32bit ? 4u : 2u;
    const uint32_t index_bytes = (dma_size & 0x00FFFFFFu) * index_stride;
    if (index_buffer == 0 || index_bytes == 0) {
      return;
    }

    draw.indexed = true;
    g_observer.index_buffer = index_buffer;
    UpsertScratchBufferResourceLocked(RendererResourceKind::kIndexBuffer,
                                      index_buffer, index_bytes, index_stride,
                                      command_pointer);
  } else if (source_select == 2) {
    draw.indexed = false;
  } else {
    return;
  }

  g_observer.last_draw = draw;
  ++g_observer.draws;
  if (draw.indexed) {
    ++g_observer.indexed_draws;
  }
  QueueDrawEventLocked();
  TraceDrawCall("PM4_DRAW", g_observer.last_draw);
}

void CaptureCommandPreviewLocked(uint8_t* base, uint32_t command_pointer,
                                 uint32_t command_word_count,
                                 uint32_t source) {
  g_observer.last_command_source = source;
  g_observer.last_command_pointer = command_pointer;
  g_observer.last_command_word_count = command_word_count;
  std::fill(std::begin(g_observer.last_command_preview),
            std::end(g_observer.last_command_preview), 0);

  const uint32_t preview_words =
      std::min<uint32_t>(command_word_count,
                         static_cast<uint32_t>(
                             std::size(g_observer.last_command_preview)));
  for (uint32_t i = 0; i < preview_words; ++i) {
    g_observer.last_command_preview[i] =
        LoadGuestU32(base, command_pointer + i * sizeof(uint32_t));
  }
  g_observer.last_command_first_word = g_observer.last_command_preview[0];
}

bool Pm4RegisterRangeTouchesResolveState(uint32_t register_index,
                                         uint32_t payload_words) {
  if (payload_words == 0) {
    return false;
  }
  const uint32_t last_register = register_index + payload_words - 1u;
  const auto touches = [register_index, last_register](uint32_t first,
                                                       uint32_t last) {
    return register_index <= last && last_register >= first;
  };
  return touches(kXenosRegRbCopyControl, kXenosRegRbCopySurfaceSlice) ||
         touches(kXenosRegRbColorInfo0, kXenosRegRbColorInfo3) ||
         touches(kXenosRegRbSurfaceInfo, kXenosRegRbDepthInfo) ||
         touches(kXenosRegRbModeControl, kXenosRegRbModeControl);
}

bool Pm4Type3ConstantTouchesResolveState(uint8_t* base,
                                         uint32_t command_pointer,
                                         uint32_t offset, uint32_t opcode,
                                         uint32_t payload_words) {
  if (!base || payload_words < 2) {
    return false;
  }

  const uint32_t offset_type =
      LoadGuestU32(base, command_pointer + (offset + 1) * sizeof(uint32_t));
  uint32_t register_index = 0;
  if (opcode == 0x2D) {
    const uint32_t index = offset_type & 0x7FFu;
    const uint32_t constant_type = (offset_type >> 16) & 0xFFu;
    if (constant_type != kSetConstantRegisterType) {
      return false;
    }
    register_index = index >= kAluConstantRegisterFirst
                         ? index
                         : kAluConstantRegisterFirst + index;
  } else if (opcode == 0x55 || opcode == 0x56) {
    register_index = offset_type & 0xFFFFu;
  } else {
    return false;
  }

  return Pm4RegisterRangeTouchesResolveState(register_index,
                                             payload_words - 1u);
}

bool DecodePm4BufferLocked(uint8_t* base, uint32_t command_pointer,
                           uint32_t command_word_count, uint32_t source,
                           uint32_t depth) {
  if (!base || command_pointer == 0 || command_word_count == 0) {
    return false;
  }

  const uint32_t first_packet = LoadGuestU32(base, command_pointer);
  if (!IsLikelyPm4PacketHeader(first_packet, command_word_count)) {
    return false;
  }

  g_observer.command_words += command_word_count;
  CaptureCommandPreviewLocked(base, command_pointer, command_word_count,
                              source);

  if (command_word_count > kMaxPm4DecodeWords) {
    command_word_count = kMaxPm4DecodeWords;
    ++g_observer.pm4_parse_errors;
  }

  uint32_t offset = 0;
  uint32_t decoded_packets = 0;
  bool decoded_any_packet = false;
  bool touched_resolve_state = false;
  while (offset < command_word_count &&
         decoded_packets < kMaxPm4DecodedPackets) {
    const uint32_t packet =
        LoadGuestU32(base, command_pointer + offset * sizeof(uint32_t));
    if (packet == 0) {
      ++offset;
      ++decoded_packets;
      continue;
    }

    const uint32_t packet_type = packet >> 30;
    g_observer.last_command_packet_type = packet_type;
    uint32_t packet_words = 1;
    switch (packet_type) {
      case 0:
        ++g_observer.pm4_type0_packets;
        {
          const uint32_t register_index = packet & 0x7FFFu;
          const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1;
          packet_words = 1 + payload_words;
          touched_resolve_state =
              touched_resolve_state ||
              Pm4RegisterRangeTouchesResolveState(register_index,
                                                  payload_words);
          if (offset + packet_words <= command_word_count) {
            ObserveRegisterRangePayloadLocked(base, command_pointer, offset + 1,
                                              payload_words, register_index);
          }
        }
        break;
      case 1:
        packet_words = 3;
        break;
      case 2:
        packet_words = 1;
        break;
      case 3: {
        ++g_observer.pm4_type3_packets;
        const uint32_t opcode = (packet >> 8) & 0x7Fu;
        g_observer.last_command_type3_opcode = opcode;
        packet_words = 1 + ((packet >> 16) & 0x3FFFu) + 1;
        if (IsPm4DrawOpcode(opcode)) {
          ++g_observer.pm4_draw_packets;
        }
        if (IsPm4IndirectOpcode(opcode)) {
          ++g_observer.pm4_indirect_packets;
        }
        if (IsPm4ConstantOpcode(opcode)) {
          ++g_observer.pm4_constant_packets;
        }
        if (opcode == 0x64) {
          ++g_observer.pm4_swap_packets;
        }
        if (opcode == kPm4LoadAluConstantOpcode) {
          const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1;
          if (offset + packet_words <= command_word_count) {
            ObserveType3AluConstantPacketLocked(base, command_pointer, offset,
                                                payload_words);
          }
        } else if (opcode == 0x27 || opcode == 0x2B) {
          const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1;
          if (offset + packet_words <= command_word_count) {
            ObserveType3ShaderLoadPacketLocked(base, command_pointer, offset,
                                               opcode, payload_words);
          }
        } else if (IsPm4ConstantOpcode(opcode)) {
          const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1;
          if (offset + packet_words <= command_word_count) {
            touched_resolve_state =
                touched_resolve_state ||
                Pm4Type3ConstantTouchesResolveState(
                    base, command_pointer, offset, opcode, payload_words);
            ObserveType3ConstantPacketLocked(base, command_pointer, offset,
                                             opcode, payload_words);
          }
        }
        if (IsPm4DrawOpcode(opcode)) {
          const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1;
          if (offset + packet_words <= command_word_count) {
            ObserveType3DrawPacketLocked(base, command_pointer, offset, opcode,
                                         payload_words);
          }
        }
        if (IsPm4IndirectOpcode(opcode)) {
          const uint32_t payload_words = ((packet >> 16) & 0x3FFFu) + 1;
          if (offset + packet_words <= command_word_count &&
              payload_words >= 2 && depth < kMaxPm4IndirectDepth) {
            const uint32_t nested_pointer_raw =
                LoadGuestU32(base, command_pointer +
                                       (offset + 1) * sizeof(uint32_t));
            const uint32_t nested_word_count =
                LoadGuestU32(base, command_pointer +
                                       (offset + 2) * sizeof(uint32_t)) &
                0x000FFFFFu;
            DecodePm4IndirectTargetLocked(base, nested_pointer_raw,
                                          nested_word_count, command_pointer,
                                          depth + 1);
          }
        }
        break;
      }
      default:
        ++g_observer.pm4_parse_errors;
        return decoded_any_packet;
    }

    if (packet_words == 0 || offset + packet_words > command_word_count) {
      ++g_observer.pm4_parse_errors;
      break;
    }

    decoded_any_packet = true;
    offset += packet_words;
    ++decoded_packets;
  }

  if (decoded_packets >= kMaxPm4DecodedPackets) {
    ++g_observer.pm4_parse_errors;
  }

  if (decoded_any_packet && touched_resolve_state) {
    QueuePm4ResolveCopyEventLocked(source);
  }

  return decoded_any_packet;
}

bool DecodePm4IndirectTargetLocked(uint8_t* base, uint32_t pointer_raw,
                                   uint32_t word_count, uint32_t source,
                                   uint32_t depth) {
  if (!base || pointer_raw == 0 || word_count == 0 ||
      depth > kMaxPm4IndirectDepth) {
    return false;
  }

  const uint32_t candidates[] = {
      NormalizeGpuPointer(pointer_raw),
      pointer_raw & 0x1FFFFFFFu,  // ReXGlue CpuToGpu semantics.
      pointer_raw & 0x3FFFFFFFu,
      DecodePm4MemoryPointer(pointer_raw),
      pointer_raw,
  };

  uint32_t tried[std::size(candidates)] = {};
  uint32_t tried_count = 0;
  for (const uint32_t candidate : candidates) {
    if (candidate == 0) {
      continue;
    }

    bool duplicate = false;
    for (uint32_t i = 0; i < tried_count; ++i) {
      if (tried[i] == candidate) {
        duplicate = true;
        break;
      }
    }
    if (duplicate) {
      continue;
    }
    tried[tried_count++] = candidate;

    if (DecodePm4BufferLocked(base, candidate, word_count, source, depth)) {
      return true;
    }
  }

  return false;
}

void DecodePm4WordsLocked(std::string_view symbol, const RendererHookArgs& args,
                          uint8_t* base) {
  if (!base) {
    return;
  }
  g_observer.memory_base = base;

  uint32_t command_pointer = 0;
  uint32_t command_word_count = 0;
  if (symbol == "sub_82BA25C0") {
    command_pointer = args.r4;
    command_word_count = args.r5;
    DecodePm4BufferLocked(base, command_pointer, command_word_count, args.lr,
                          0);
  } else if (symbol == "sub_822868C8") {
    ++g_observer.pm4_indirect_packets;
    const uint32_t command_pointer_raw = args.r5;
    command_word_count = args.r6 & 0x000FFFFFu;
    command_pointer = command_pointer_raw & 0x1FFFFFFFu;
    DecodePm4IndirectTargetLocked(base, command_pointer_raw, command_word_count,
                                  args.lr, 0);
  } else if (symbol == "sub_822866E0") {
    command_pointer = args.r4;
    command_word_count = args.r5 * 2u;
    g_observer.pm4_indirect_packets += args.r5;
    if (command_pointer != 0 && command_word_count != 0) {
      CaptureCommandPreviewLocked(base, command_pointer, command_word_count,
                                  args.lr);
    }
    const uint32_t descriptor_count =
        std::min<uint32_t>(args.r5, kMaxCallBufferDescriptors);
    for (uint32_t descriptor_index = 0; descriptor_index < descriptor_count;
         ++descriptor_index) {
      const uint32_t descriptor = args.r4 + descriptor_index * 8u;
      const uint32_t nested_word_count =
          LoadGuestU32(base, descriptor) & 0x00FFFFFFu;
      const uint32_t nested_pointer_raw =
          LoadGuestU32(base, descriptor + sizeof(uint32_t));
      DecodePm4IndirectTargetLocked(base, nested_pointer_raw, nested_word_count,
                                    args.lr, 0);
    }
  } else {
    return;
  }

  const uint64_t count = g_observer.command_writes;
  if (RendererDrawTracingEnabled() && (count <= 3 || IsPowerOfTwo(count))) {
    REXLOG_INFO(
        "Refii renderer PM4 {} words={} first=0x{:08X} type={} opcode=0x{:02X} "
        "{} type0={} type3={} draw={} indirect={} constants={} swap={} "
        "errors={}",
        LumenRendererName(symbol) ? LumenRendererName(symbol) : symbol.data(),
        command_word_count, g_observer.last_command_first_word,
        g_observer.last_command_packet_type,
        g_observer.last_command_type3_opcode,
        Pm4OpcodeName(g_observer.last_command_type3_opcode),
        g_observer.pm4_type0_packets, g_observer.pm4_type3_packets,
        g_observer.pm4_draw_packets, g_observer.pm4_indirect_packets,
        g_observer.pm4_constant_packets, g_observer.pm4_swap_packets,
        g_observer.pm4_parse_errors);
  }
}

void TraceDrawCall(std::string_view symbol,
                   const RendererLastDrawSnapshot& draw) {
  if (!RendererDrawTracingEnabled()) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer draw {} indexed={} primitive={} base_vertex={} "
      "start={} primitive_count={}",
      LumenRendererName(symbol) ? LumenRendererName(symbol) : symbol.data(),
      draw.indexed, draw.primitive_type, draw.base_vertex, draw.start,
      draw.primitive_count);
}

void TraceCommandPath(std::string_view symbol, const RendererHookArgs& args,
                      uint64_t count) {
  if (!RendererDrawTracingEnabled() || count > 3 && !IsPowerOfTwo(count)) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer command path {} r3=0x{:08X} r4=0x{:08X} "
      "r5=0x{:08X} r6=0x{:08X} lr=0x{:08X} count={}",
      LumenRendererName(symbol) ? LumenRendererName(symbol) : symbol.data(),
      args.r3, args.r4, args.r5, args.r6, args.lr, count);
}

RendererViewportSnapshot DecodeGuestViewport(float x, float y, float width,
                                             float height, float min_depth,
                                             float max_depth) {
  RendererViewportSnapshot viewport;
  viewport.x = x;
  viewport.y = y;
  viewport.width = width;
  viewport.height = height;
  viewport.min_depth = min_depth;
  viewport.max_depth = max_depth;
  viewport.valid = std::isfinite(viewport.x) && std::isfinite(viewport.y) &&
                   std::isfinite(viewport.width) &&
                   std::isfinite(viewport.height) &&
                   std::isfinite(viewport.min_depth) &&
                   std::isfinite(viewport.max_depth) &&
                   viewport.width > 0.0f && viewport.height > 0.0f &&
                   viewport.width <= 16384.0f &&
                   viewport.height <= 16384.0f &&
                   viewport.x >= -16384.0f && viewport.y >= -16384.0f &&
                   viewport.x <= 16384.0f && viewport.y <= 16384.0f;
  if (!viewport.valid) {
    viewport = {};
  }
  return viewport;
}

RendererViewportSnapshot ScaleGuestViewportToOutput(
    RendererViewportSnapshot viewport) {
  // Fable viewports are local to the currently bound guest render target. Keep
  // them in guest surface coordinates; Plume handles host-output scaling during
  // the final present blit.
  return viewport;
}

RendererViewportSnapshot DecodeGuestViewportFromDevice(uint8_t* base,
                                                       uint32_t device) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return {};
  }
  return DecodeGuestViewport(
      LoadGuestF32(base, device + kFableDeviceViewportXOffset),
      LoadGuestF32(base, device + kFableDeviceViewportYOffset),
      LoadGuestF32(base, device + kFableDeviceViewportWidthOffset),
      LoadGuestF32(base, device + kFableDeviceViewportHeightOffset),
      LoadGuestF32(base, device + kFableDeviceViewportMinZOffset),
      LoadGuestF32(base, device + kFableDeviceViewportMaxZOffset));
}

RendererScissorSnapshot DecodeGuestScissor(int32_t left, int32_t top,
                                           int32_t right, int32_t bottom) {
  RendererScissorSnapshot scissor;
  scissor.left = left;
  scissor.top = top;
  scissor.right = right;
  scissor.bottom = bottom;
  scissor.valid = scissor.right > scissor.left &&
                  scissor.bottom > scissor.top &&
                  scissor.right <= 16384 && scissor.bottom <= 16384 &&
                  scissor.left > -16384 && scissor.top > -16384;
  if (!scissor.valid) {
    scissor = {};
  }
  return scissor;
}

RendererScissorSnapshot DecodeGuestRect(uint8_t* base, uint32_t guest_address) {
  if (!base || guest_address == 0) {
    return {};
  }
  return DecodeGuestScissor(LoadGuestI32(base, guest_address + 0),
                            LoadGuestI32(base, guest_address + 4),
                            LoadGuestI32(base, guest_address + 8),
                            LoadGuestI32(base, guest_address + 12));
}

RendererScissorSnapshot ScaleGuestScissorToOutput(
    RendererScissorSnapshot scissor) {
  // Scissors are also guest render-target local. Scaling here would make a
  // 1280x720 Fable target receive 1920x1080 scissors when the host output is
  // larger, which is both inaccurate and risky for offscreen Vulkan framebuffers.
  return scissor;
}

RendererScissorSnapshot DecodeGuestPackedScissorFromDevice(uint8_t* base,
                                                           uint32_t device) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return {};
  }
  const uint32_t packed_min =
      LoadGuestU32(base, device + kFableDevicePackedScissorMinOffset);
  const uint32_t packed_max =
      LoadGuestU32(base, device + kFableDevicePackedScissorMaxOffset);
  return DecodeGuestScissor(
      static_cast<int32_t>(packed_min & 0x7FFFu),
      static_cast<int32_t>((packed_min >> 16u) & 0x7FFFu),
      static_cast<int32_t>(packed_max & 0x7FFFu),
      static_cast<int32_t>((packed_max >> 16u) & 0x7FFFu));
}

void RememberRequestedDepthRenderStateLocked(uint32_t slot, uint32_t value) {
  if (slot == kFableRenderStateSlotZEnable) {
    g_observer.requested_z_enable = value;
    g_observer.requested_z_enable_valid = true;
  } else if (slot == kFableRenderStateSlotZWriteEnable) {
    g_observer.requested_z_write_enable = value;
    g_observer.requested_z_write_enable_valid = true;
  }
}

void SetObservedRenderTargetLocked(uint32_t slot, uint32_t render_target,
                                   uint32_t device, uint8_t* base) {
  if (slot >= std::size(g_observer.render_targets)) {
    return;
  }

  const uint32_t requested_render_target = render_target;
  if (PlumeRendererBackendEnabled() && base &&
      IsLikelyFableObjectAddress(device)) {
    StoreGuestU32(base, device + kFableDeviceRenderTargetBaseOffset +
                            slot * sizeof(uint32_t),
                  requested_render_target);
  }

  if (render_target != 0 && base &&
      !TrackBoundSurfaceLocked(RendererResourceKind::kRenderTargetSurface,
                               render_target, device, base)) {
    render_target = 0;
  }
  g_observer.render_targets[slot] = render_target;
  FlushDeferredSurfaceReleasesLocked();
}

bool ShouldMirrorSuppressedDeviceState(uint8_t* base, uint32_t device) {
  return PlumeRendererBackendEnabled() && base &&
         IsLikelyFableObjectAddress(device);
}

struct FableSurfaceExtent {
  uint32_t width = 0;
  uint32_t height = 0;
};

FableSurfaceExtent DecodeFableSurfaceExtent(uint8_t* base,
                                            uint32_t surface) {
  if (!base || !IsLikelyFableObjectAddress(surface)) {
    return {};
  }

  const uint32_t packed_extent =
      LoadGuestU32(base, surface + kFableSurfacePackedExtentOffset);
  const uint32_t width = (packed_extent >> 18u) + 1u;
  const uint32_t height = ((packed_extent >> 3u) & 0x7FFFu) + 1u;
  if (!IsPlausibleFableSurfaceExtent(width, height)) {
    return {};
  }
  return {.width = width, .height = height};
}

FableSurfaceExtent CurrentFableDrawSurfaceExtent(uint8_t* base,
                                                 uint32_t device) {
  if (!ShouldMirrorSuppressedDeviceState(base, device)) {
    return {};
  }

  const uint32_t color_surface =
      LoadGuestU32(base, device + kFableDeviceRenderTargetBaseOffset);
  FableSurfaceExtent extent = DecodeFableSurfaceExtent(base, color_surface);
  if (extent.width != 0 && extent.height != 0) {
    return extent;
  }

  const uint32_t depth_surface =
      LoadGuestU32(base, device + kFableDeviceDepthStencilOffset);
  return DecodeFableSurfaceExtent(base, depth_surface);
}

FableSurfaceExtent DecodeFableScalerSurfaceExtent(uint8_t* base,
                                                  uint32_t device) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return {};
  }

  const uint32_t surface =
      LoadGuestU32(base, device + kFableDeviceScalerSurfaceOffset);
  if (IsLikelyFableObjectAddress(surface)) {
    const uint32_t size_word = LoadGuestU32(base, surface + 0x24u);
    const uint32_t msaa_bias =
        ((LoadGuestU32(base, surface + 0x28u) >> 30u) & 2u) + 1u;
    const uint32_t width = (size_word & 0x1FFFu) + msaa_bias;
    const uint32_t height = ((size_word >> 13u) & 0x1FFFu) + msaa_bias;
    if (IsPlausibleFableSurfaceExtent(width, height)) {
      return {.width = width, .height = height};
    }
  }

  const uint32_t width =
      LoadGuestU32(base, device + kFableDeviceDefaultDisplayWidthOffset);
  const uint32_t height =
      LoadGuestU32(base, device + kFableDeviceDefaultDisplayHeightOffset);
  if (IsPlausibleFableSurfaceExtent(width, height)) {
    return {.width = width, .height = height};
  }
  return {};
}

RendererScalerSnapshot DecodeGuestScalerParameters(uint8_t* base,
                                                   uint32_t device,
                                                   uint32_t parameter_pointer,
                                                   uint32_t source_width_hint = 0,
                                                   uint32_t source_height_hint = 0) {
  RendererScalerSnapshot scaler;
  scaler.parameter_pointer = parameter_pointer;
  if (!base || !IsLikelyFableObjectAddress(device) ||
      !IsLikelyGuestDwordAddress(parameter_pointer)) {
    return scaler;
  }

  std::array<uint32_t, 7> input = {};
  for (uint32_t i = 0; i < input.size(); ++i) {
    input[i] = LoadGuestU32(base, parameter_pointer + i * sizeof(uint32_t));
  }

  FableSurfaceExtent display_extent =
      DecodeFableScalerSurfaceExtent(base, device);
  const FableSurfaceExtent source_extent_hint =
      IsPlausibleFableSurfaceExtent(source_width_hint, source_height_hint)
          ? FableSurfaceExtent{.width = source_width_hint,
                               .height = source_height_hint}
          : FableSurfaceExtent{};
  uint32_t source_left = input[0];
  uint32_t source_top = input[1];
  uint32_t source_right = input[2];
  uint32_t source_bottom = input[3];
  if (source_left == 0 && source_top == 0 && source_right == 0 &&
      source_bottom == 0) {
    const FableSurfaceExtent source_extent =
        source_extent_hint.width != 0 ? source_extent_hint : display_extent;
    if (source_extent.width != 0 && source_extent.height != 0) {
      source_right = source_extent.width;
      source_bottom = source_extent.height;
    }
  }

  uint32_t output_width = input[4];
  if (output_width == 0) {
    output_width = LoadGuestU32(base, device + kFableDeviceOutputWidthOffset);
  }
  uint32_t output_height = input[5];
  if (output_height == 0 && output_width != 0 &&
      source_right > source_left && source_bottom > source_top) {
    const uint64_t source_width = source_right - source_left;
    const uint64_t source_height = source_bottom - source_top;
    output_height =
        static_cast<uint32_t>((uint64_t(output_width) * source_height) /
                              std::max<uint64_t>(1, source_width));
  }
  if (output_height == 0) {
    output_height = LoadGuestU32(base, device + kFableDeviceOutputHeightOffset);
  }

  scaler.source_left = source_left;
  scaler.source_top = source_top;
  scaler.source_right = source_right;
  scaler.source_bottom = source_bottom;
  scaler.output_width = output_width;
  scaler.output_height = output_height;
  scaler.raw_dwords[0] = source_left;
  scaler.raw_dwords[1] = source_top;
  scaler.raw_dwords[2] = source_right;
  scaler.raw_dwords[3] = source_bottom;
  scaler.raw_dwords[4] = output_width;
  scaler.raw_dwords[5] = output_height;
  scaler.raw_dwords[6] = input[6] != 0 ? 3u : 0u;
  scaler.raw_dwords[10] = input[6] != 0 ? 3u : 0u;
  scaler.valid =
      source_right > source_left && source_bottom > source_top &&
      IsPlausibleFableSurfaceExtent(output_width, output_height);
  if (!scaler.valid) {
    scaler = {};
    scaler.parameter_pointer = parameter_pointer;
  }
  return scaler;
}

void MirrorScalerParametersToGuestDeviceLocked(
    uint8_t* base, uint32_t device, const RendererScalerSnapshot& scaler) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) || !scaler.valid) {
    return;
  }

  for (uint32_t i = 0; i < scaler.raw_dwords.size(); ++i) {
    StoreGuestU32(base, device + kFableDeviceScalerParametersOffset +
                            i * sizeof(uint32_t),
                  scaler.raw_dwords[i]);
  }
  const uint8_t flags =
      LoadGuestU8(base, device + kFableDeviceScalerDirtyFlagsOffset);
  StoreGuestU8(base, device + kFableDeviceScalerDirtyFlagsOffset,
               static_cast<uint8_t>(flags | 0x10u));
}

void ObserveScalerParametersFromPointerLocked(const RendererHookArgs& args,
                                              uint8_t* base,
                                              uint32_t parameter_pointer,
                                              uint32_t source_width_hint,
                                              uint32_t source_height_hint) {
  RendererScalerSnapshot scaler =
      DecodeGuestScalerParameters(base, args.r3, parameter_pointer,
                                  source_width_hint, source_height_hint);
  if (!scaler.valid) {
    return;
  }

  g_observer.scaler = scaler;
  MirrorScalerParametersToGuestDeviceLocked(base, args.r3, scaler);
  if (g_observer.display_present_width == 0) {
    g_observer.display_present_width = scaler.output_width;
  }
  if (g_observer.display_present_height == 0) {
    g_observer.display_present_height = scaler.output_height;
  }
}

void ObserveScalerParametersLocked(const RendererHookArgs& args,
                                   uint8_t* base) {
  ObserveScalerParametersFromPointerLocked(args, base, args.r4);
}

void ObserveSetupScalerLocked(const RendererHookArgs& args, uint8_t* base) {
  // D3D::SetupScaler(device, width, height, params, flags) passes the source
  // extent separately from the parameter block; keep that root shape intact.
  ObserveScalerParametersFromPointerLocked(args, base, args.r6, args.r4,
                                           args.r5);
}

int32_t ClampFableScissorCoordinate(int32_t value) {
  return std::clamp(value, 0, 0x7FFF);
}

uint32_t PackFableScissor(int32_t x, int32_t y) {
  const uint32_t packed_x =
      static_cast<uint32_t>(ClampFableScissorCoordinate(x));
  const uint32_t packed_y =
      static_cast<uint32_t>(ClampFableScissorCoordinate(y));
  return packed_x | (packed_y << 16u);
}

void StoreFableScissorToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                          RendererScissorSnapshot scissor) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) || !scissor.valid) {
    return;
  }

  StoreGuestU32(base, device + kFableDeviceScissorBaseOffset + 0u,
                static_cast<uint32_t>(scissor.left));
  StoreGuestU32(base, device + kFableDeviceScissorBaseOffset + 4u,
                static_cast<uint32_t>(scissor.top));
  StoreGuestU32(base, device + kFableDeviceScissorBaseOffset + 8u,
                static_cast<uint32_t>(scissor.right));
  StoreGuestU32(base, device + kFableDeviceScissorBaseOffset + 12u,
                static_cast<uint32_t>(scissor.bottom));
  StoreGuestU32(base, device + kFableDevicePackedScissorMinOffset,
                PackFableScissor(scissor.left, scissor.top));
  StoreGuestU32(base, device + kFableDevicePackedScissorMaxOffset,
                PackFableScissor(scissor.right, scissor.bottom));
}

RendererScissorSnapshot ClampScissorToViewport(
    RendererScissorSnapshot scissor, RendererViewportSnapshot viewport) {
  if (!scissor.valid || !viewport.valid) {
    return scissor;
  }

  const int32_t viewport_left = static_cast<int32_t>(std::floor(viewport.x));
  const int32_t viewport_top = static_cast<int32_t>(std::floor(viewport.y));
  const int32_t viewport_right =
      static_cast<int32_t>(std::ceil(viewport.x + viewport.width));
  const int32_t viewport_bottom =
      static_cast<int32_t>(std::ceil(viewport.y + viewport.height));

  return DecodeGuestScissor(std::max(scissor.left, viewport_left),
                            std::max(scissor.top, viewport_top),
                            std::min(scissor.right, viewport_right),
                            std::min(scissor.bottom, viewport_bottom));
}

void MirrorViewportToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                       uint32_t viewport_pointer) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) ||
      !IsLikelyGuestDwordAddress(viewport_pointer)) {
    return;
  }

  float x = static_cast<float>(LoadGuestU32(base, viewport_pointer + 0u));
  float y = static_cast<float>(LoadGuestU32(base, viewport_pointer + 4u));
  float width = static_cast<float>(LoadGuestU32(base, viewport_pointer + 8u));
  float height =
      static_cast<float>(LoadGuestU32(base, viewport_pointer + 12u));
  const float min_depth = LoadGuestF32(base, viewport_pointer + 16u);
  const float max_depth = LoadGuestF32(base, viewport_pointer + 20u);

  const FableSurfaceExtent extent = CurrentFableDrawSurfaceExtent(base, device);
  if (extent.width != 0 && extent.height != 0 && std::isfinite(x) &&
      std::isfinite(y) && std::isfinite(width) && std::isfinite(height) &&
      x >= 0.0f && y >= 0.0f) {
    width = std::min(width, std::max(0.0f, static_cast<float>(extent.width) - x));
    height =
        std::min(height, std::max(0.0f, static_cast<float>(extent.height) - y));
  }

  RendererViewportSnapshot viewport =
      DecodeGuestViewport(x, y, width, height, min_depth, max_depth);
  if (!viewport.valid) {
    return;
  }
  viewport = ScaleGuestViewportToOutput(viewport);
  g_observer.viewport = viewport;

  StoreGuestF32(base, device + kFableDeviceViewportXOffset, viewport.x);
  StoreGuestF32(base, device + kFableDeviceViewportYOffset, viewport.y);
  StoreGuestF32(base, device + kFableDeviceViewportWidthOffset,
                viewport.width);
  StoreGuestF32(base, device + kFableDeviceViewportHeightOffset,
                viewport.height);
  StoreGuestF32(base, device + kFableDeviceViewportMinZOffset,
                viewport.min_depth);
  StoreGuestF32(base, device + kFableDeviceViewportMaxZOffset,
                viewport.max_depth);
  StoreGuestU32(base, device + kFableDeviceViewportUnknownOffset, 0);

  const float half_width = viewport.width * 0.5f;
  const float half_height = viewport.height * 0.5f;
  StoreGuestF32(base, device + kFableDeviceViewportTransformHalfWidthOffset,
                half_width);
  StoreGuestF32(base, device + kFableDeviceViewportTransformCenterXOffset,
                half_width + viewport.x);
  StoreGuestF32(base, device + kFableDeviceViewportTransformNegHalfHeightOffset,
                -half_height);
  StoreGuestF32(base, device + kFableDeviceViewportTransformCenterYOffset,
                half_height + viewport.y);
  StoreGuestF32(base, device + kFableDeviceViewportTransformDepthScaleOffset,
                viewport.max_depth - viewport.min_depth);
  StoreGuestF32(base, device + kFableDeviceViewportTransformDepthBiasOffset,
                viewport.min_depth);

  RendererScissorSnapshot viewport_scissor = DecodeGuestScissor(
      static_cast<int32_t>(std::floor(viewport.x)),
      static_cast<int32_t>(std::floor(viewport.y)),
      static_cast<int32_t>(std::ceil(viewport.x + viewport.width)),
      static_cast<int32_t>(std::ceil(viewport.y + viewport.height)));
  viewport_scissor = ScaleGuestScissorToOutput(viewport_scissor);
  StoreFableScissorToGuestDeviceLocked(base, device, viewport_scissor);
  g_observer.scissor = viewport_scissor;
}

void MirrorScissorToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                      uint32_t scissor_pointer) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) ||
      !IsLikelyGuestDwordAddress(scissor_pointer)) {
    return;
  }

  RendererScissorSnapshot scissor = DecodeGuestRect(base, scissor_pointer);
  if (!scissor.valid) {
    return;
  }
  scissor = ClampScissorToViewport(scissor, g_observer.viewport);
  scissor = ScaleGuestScissorToOutput(scissor);
  StoreFableScissorToGuestDeviceLocked(base, device, scissor);
  g_observer.scissor = scissor;
}

void MirrorTextureBindingToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                             uint32_t slot,
                                             uint32_t texture_object) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) || slot >= 16u) {
    return;
  }

  StoreGuestU32(base, device + kFableDeviceTextureObjectBaseOffset +
                          slot * sizeof(uint32_t),
                texture_object);
  if (!IsLikelyFableObjectAddress(texture_object)) {
    return;
  }

  const uint32_t fetch_address =
      device + kFableDeviceTextureFetchBaseOffset +
      slot * kFableDeviceTextureFetchStride;
  const uint32_t old0 = LoadGuestU32(base, fetch_address + 0u);
  const uint32_t old1 = LoadGuestU32(base, fetch_address + 4u);
  const uint32_t old3 = LoadGuestU32(base, fetch_address + 12u);
  const uint32_t old4 = LoadGuestU32(base, fetch_address + 16u);
  const uint32_t old5 = LoadGuestU32(base, fetch_address + 20u);

  const uint32_t raw0 = LoadGuestU32(base, texture_object + 28u);
  const uint32_t raw1 = LoadGuestU32(base, texture_object + 32u);
  const uint32_t raw2 = LoadGuestU32(base, texture_object + 36u);
  const uint32_t raw3 = LoadGuestU32(base, texture_object + 40u);
  const uint32_t raw4 = LoadGuestU32(base, texture_object + 44u);
  const uint32_t raw5 = LoadGuestU32(base, texture_object + 48u);

  uint32_t max_mip = LoadGuestU8(base, device + kFableDeviceTextureMaxMipOffset +
                                           slot);
  const uint32_t raw_max_mip = (raw4 >> 2u) & 0xFu;
  if (raw_max_mip > max_mip) {
    max_mip = raw_max_mip;
  }

  uint32_t min_mip = LoadGuestU8(base, device + kFableDeviceTextureMinMipOffset +
                                           slot);
  const uint32_t raw_min_mip = (raw4 >> 6u) & 0xFu;
  if (raw_min_mip < min_mip) {
    min_mip = raw_min_mip;
  }

  StoreGuestU32(base, fetch_address + 0u,
                (old0 & 0x003FFC00u) | (raw0 & 0xFFC003FFu));
  StoreGuestU32(base, fetch_address + 4u,
                (old1 & 0x00000800u) |
                    (((((raw1 >> 20u) + 512u) & 0x1000u) +
                      (raw1 & 0x1FFFFFFFu)) &
                     0xFFFFF7FFu));
  StoreGuestU32(base, fetch_address + 8u, raw2);
  StoreGuestU32(base, fetch_address + 12u,
                (old3 & 0x7FF80000u) | (raw3 & 0x8007FFFFu));
  StoreGuestU32(base, fetch_address + 16u,
                (old4 & 0xFFFFFC03u) | ((max_mip << 2u) & 0x3Cu) |
                    ((min_mip << 6u) & 0x3C0u));
  StoreGuestU32(base, fetch_address + 20u,
                (old5 & 0x000001FFu) |
                    (((((raw5 >> 20u) + 512u) & 0x1000u) +
                      (raw5 & 0x1FFFFE00u)) &
                     0xFFFFFE00u));
}

void MirrorStreamBindingToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                            uint32_t slot,
                                            uint32_t stream_object,
                                            uint32_t offset,
                                            uint32_t stride,
                                            uint32_t dirty_mask) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) || slot >= 16u) {
    return;
  }

  StoreGuestU32(base, device + kFableDeviceStreamObjectBaseOffset +
                          slot * sizeof(uint32_t),
                stream_object);
  StoreGuestU8(base, device + kFableDeviceStreamStrideBaseOffset + slot,
               static_cast<uint8_t>(stride >> 2));

  if (!IsLikelyFableObjectAddress(stream_object)) {
    return;
  }

  const uint32_t object_gpu_base_raw =
      LoadGuestU32(base, stream_object + 0x18u);
  const uint32_t object_gpu_size =
      DecodeFablePackedBufferSize(LoadGuestU32(base, stream_object + 0x1Cu));
  const uint32_t clamped_offset = std::min(offset, object_gpu_size);
  const uint32_t gpu_address_raw = object_gpu_base_raw + clamped_offset;
  const uint32_t gpu_address =
      DecodeFableGpuBufferPointer(gpu_address_raw);
  const uint32_t gpu_size = object_gpu_size - clamped_offset;
  if (IsLikelyGpuByteBufferRange(gpu_address, gpu_size)) {
    StoreGuestU32(base, device + kFableDeviceStreamGpuAddressBaseOffset -
                            slot * 8u,
                  gpu_address_raw);
    StoreGuestU32(base, device + kFableDeviceStreamGpuSizeBaseOffset -
                            slot * 8u,
                  LoadGuestU32(base, stream_object + 0x1Cu) - clamped_offset);
  }

  (void)dirty_mask;
}

void ApplyEffectiveDepthRenderStateLocked(uint8_t* base, uint32_t device,
                                          uint32_t depth_surface) {
  (void)base;
  (void)device;
  g_observer.depth_stencil = depth_surface;
  if (depth_surface == 0) {
    g_observer.render_states[kFableRenderStateSlotZEnable] = 0;
    g_observer.render_states[kFableRenderStateSlotZWriteEnable] = 0;
    g_observer.render_states[kFableRenderStateSlotHiZWriteEnable] = 0;
    return;
  }
}

void SetObservedDepthSurfaceLocked(uint8_t* base, uint32_t device,
                                   uint32_t depth_surface) {
  const uint32_t requested_depth_surface = depth_surface;
  if (PlumeRendererBackendEnabled() && base &&
      IsLikelyFableObjectAddress(device)) {
    StoreGuestU32(base, device + kFableDeviceDepthStencilOffset,
                  requested_depth_surface);
  }

  if (depth_surface != 0 && base &&
      !TrackBoundSurfaceLocked(RendererResourceKind::kDepthSurface,
                               depth_surface, device, base)) {
    depth_surface = 0;
  }
  ApplyEffectiveDepthRenderStateLocked(base, device, depth_surface);
  FlushDeferredSurfaceReleasesLocked();
}

void RefreshBoundRenderTargetsLocked(uint8_t* base, uint32_t device) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  for (uint32_t slot = 0; slot < std::size(g_observer.render_targets); ++slot) {
    const uint32_t render_target =
        LoadGuestU32(base, device + kFableDeviceRenderTargetBaseOffset +
                               slot * sizeof(uint32_t));
    SetObservedRenderTargetLocked(slot, render_target, device, base);
  }
}

void RefreshEffectiveDepthRenderStateLocked(uint8_t* base, uint32_t device) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  SetObservedDepthSurfaceLocked(
      base, device, LoadGuestU32(base, device + kFableDeviceDepthStencilOffset));
}

void RefreshBoundSurfacesLocked(uint8_t* base, uint32_t device) {
  RefreshBoundRenderTargetsLocked(base, device);
  RefreshEffectiveDepthRenderStateLocked(base, device);
}

bool GuestDeviceStateReadableForReplay() {
  // Plume replaces Fable's D3D9 submission, but the guest device state remains
  // the canonical source for bound RTs, streams, declarations, and constants.
  // Do not tie replay state reads to the native fallback path; fallback decides
  // whether the original renderer is allowed to submit, not whether we can
  // snapshot the D3D9 state block.
  return true;
}

void SanitizeBoundSurfaceSnapshotLocked(uint32_t* render_targets,
                                        size_t render_target_count,
                                        uint32_t& depth_stencil) {
  if (!render_targets || render_target_count == 0) {
    return;
  }

  const size_t count =
      std::min(render_target_count, std::size(g_observer.render_targets));
  std::memcpy(render_targets, g_observer.render_targets,
              count * sizeof(render_targets[0]));
  for (size_t i = count; i < render_target_count; ++i) {
    render_targets[i] = 0;
  }
  depth_stencil = g_observer.depth_stencil;

  if (g_observer.memory_base == nullptr) {
    return;
  }

  for (size_t i = 0; i < count; ++i) {
    if (render_targets[i] == 0) {
      continue;
    }
    if (!TrackBoundSurfaceLocked(RendererResourceKind::kRenderTargetSurface,
                                 render_targets[i], 0,
                                 g_observer.memory_base)) {
      render_targets[i] = 0;
      g_observer.render_targets[i] = 0;
    }
  }

  if (depth_stencil != 0 &&
      !TrackBoundSurfaceLocked(RendererResourceKind::kDepthSurface,
                               depth_stencil, 0, g_observer.memory_base)) {
    depth_stencil = 0;
    ApplyEffectiveDepthRenderStateLocked(g_observer.memory_base, 0, 0);
  }
}

void SanitizeEventSurfacesLocked(RendererDrawEventSnapshot& event) {
  if (g_observer.memory_base == nullptr) {
    return;
  }

  for (uint32_t& render_target : event.render_targets) {
    if (render_target == 0) {
      continue;
    }
    if (!TrackBoundSurfaceLocked(RendererResourceKind::kRenderTargetSurface,
                                 render_target, 0, g_observer.memory_base)) {
      render_target = 0;
    }
  }
  if (event.depth_stencil != 0 &&
      !TrackBoundSurfaceLocked(RendererResourceKind::kDepthSurface,
                               event.depth_stencil, 0,
                               g_observer.memory_base)) {
      event.depth_stencil = 0;
  }

  RefreshDrawEventResourceSerialFromBindingsLocked(event);
}

uint32_t FindSurfaceByRbInfoLocked(RendererResourceKind kind,
                                   uint32_t rb_surface_info,
                                   uint32_t rb_target_info) {
  if (rb_surface_info == 0) {
    return 0;
  }

  const auto target_info_matches =
      [&](const RendererResourceRecord& record) -> bool {
    if (kind == RendererResourceKind::kDepthSurface) {
      // Fable's depth surface headers carry the Xenos RB_DEPTH_INFO value in
      // the same +0x1C target-info word used by color surfaces. The +0x20 word
      // is still useful metadata, but it is not the PM4 identity for the target.
      return record.raw_packed28 == rb_target_info ||
             record.raw_packed32 == rb_target_info;
    }
    return record.raw_packed28 == rb_target_info;
  };

  uint32_t best_address = 0;
  uint64_t best_serial = 0;
  for (const auto& [address, record] : g_observer.resources) {
    if (record.kind != kind || record.raw_packed44 != rb_surface_info) {
      continue;
    }
    if (!target_info_matches(record)) {
      continue;
    }
    if (record.serial >= best_serial) {
      best_address = address;
      best_serial = record.serial;
    }
  }
  return best_address;
}

void ResolvePm4BoundSurfacesLocked(uint32_t* render_targets,
                                   size_t render_target_count,
                                   uint32_t& depth_stencil) {
  if (!render_targets || !g_observer.pm4_registers_valid) {
    return;
  }

  const size_t count =
      std::min(render_target_count, std::size(g_observer.rb_color_info));
  for (size_t slot = 0; slot < count; ++slot) {
    if (render_targets[slot] != 0) {
      continue;
    }
    const uint32_t slot_write_mask =
        (g_observer.rb_color_mask >> (slot * 4u)) & 0xFu;
    if (slot != 0 && slot_write_mask == 0) {
      continue;
    }
    if (slot != 0 && !g_observer.rb_color_info_valid[slot]) {
      continue;
    }
    const uint32_t resolved = FindSurfaceByRbInfoLocked(
        RendererResourceKind::kRenderTargetSurface, g_observer.rb_surface_info,
        g_observer.rb_color_info[slot]);
    if (resolved != 0) {
      render_targets[slot] = resolved;
    }
  }

  if (depth_stencil == 0) {
    depth_stencil = FindSurfaceByRbInfoLocked(
        RendererResourceKind::kDepthSurface, g_observer.rb_surface_info,
        g_observer.rb_depth_info);
  }
}

bool SnapshotDeviceFloatConstantsLocked(
    uint32_t device_pointer,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    std::array<bool, kRendererShaderFloatConstantDwords>& valid_mask,
    uint8_t* base, uint32_t base_register) {
  if (!base || device_pointer == 0) {
    return false;
  }

  std::array<uint32_t, kRendererShaderFloatConstantDwords> snapshot = {};
  uint32_t nonzero_dwords = 0;
  for (uint32_t register_index = 0;
       register_index < kRendererShaderFloatConstantDwords / 4u;
       ++register_index) {
    const uint32_t source_address =
        device_pointer + (register_index + base_register) * 16u;
    for (uint32_t component = 0; component < 4u; ++component) {
      const uint32_t value =
          LoadGuestU32(base, source_address + component * sizeof(uint32_t));
      snapshot[register_index * 4u + component] = value;
      if (value != 0) {
        ++nonzero_dwords;
      }
    }
  }

  if (nonzero_dwords == 0) {
    return false;
  }

  const bool changed = constants != snapshot;
  constants = snapshot;
  valid_mask.fill(true);
  return changed;
}

bool SnapshotDeviceVertexFloatConstantsLocked(
    uint32_t device_pointer,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    std::array<bool, kRendererShaderFloatConstantDwords>& valid_mask,
    uint8_t* base) {
  constexpr uint32_t kDeviceVertexFloatConstantBaseRegister = 120;
  return SnapshotDeviceFloatConstantsLocked(
      device_pointer, constants, valid_mask, base,
      kDeviceVertexFloatConstantBaseRegister);
}

bool SnapshotDevicePixelFloatConstantsLocked(
    uint32_t device_pointer,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    std::array<bool, kRendererShaderFloatConstantDwords>& valid_mask,
    uint8_t* base) {
  constexpr uint32_t kDevicePixelFloatConstantBaseRegister = 376;
  return SnapshotDeviceFloatConstantsLocked(
      device_pointer, constants, valid_mask, base,
      kDevicePixelFloatConstantBaseRegister);
}

void RefreshDeviceInputBindingsLocked(uint32_t device_pointer, uint8_t* base) {
  if (!GuestDeviceStateReadableForReplay()) {
    return;
  }
  if (!base || device_pointer == 0) {
    return;
  }

  const uint32_t declaration =
      LoadGuestU32(base, device_pointer + kFableDeviceVertexDeclarationOffset);
  if (declaration != 0) {
    g_observer.vertex_declaration = declaration;
    TrackVertexDeclarationLocked(declaration, device_pointer, base);
  }

  const uint32_t index_buffer =
      LoadGuestU32(base, device_pointer + kFableDeviceIndexBufferOffset);
  if (index_buffer != 0) {
    g_observer.index_buffer = index_buffer;
  }

  const uint32_t pixel_shader =
      LoadGuestU32(base, device_pointer + kFableDevicePixelShaderOffset);
  const uint32_t vertex_shader =
      LoadGuestU32(base, device_pointer + kFableDeviceVertexShaderOffset);
  if (pixel_shader != 0) {
    g_observer.pixel_shader = pixel_shader;
    TrackShaderObjectLocked(RendererResourceKind::kPixelShader, pixel_shader,
                            device_pointer, base);
  }
  if (vertex_shader != 0) {
    g_observer.vertex_shader = vertex_shader;
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, vertex_shader,
                            device_pointer, base);
  }

  for (uint32_t slot = 0; slot < std::size(g_observer.textures); ++slot) {
    const uint32_t texture_object = LoadGuestU32(
        base, device_pointer + kFableDeviceTextureObjectBaseOffset +
                  slot * sizeof(uint32_t));
    if (texture_object != 0 && IsLikelyFableObjectAddress(texture_object)) {
      g_observer.textures[slot] = texture_object;
      StoreTextureObjectFetchDwordsLocked(
          texture_object, texture_object + 28u, slot, base, 0, false, false);
    } else if (texture_object == 0) {
      g_observer.textures[slot] = 0;
    }
  }

  for (uint32_t slot = 0; slot < 32u; ++slot) {
    const uint32_t fetch_address =
        device_pointer + kFableDeviceTextureFetchBaseOffset +
        slot * kFableDeviceTextureFetchStride;
    const std::array<uint32_t, 6> fetch_dwords =
        LoadTextureFetchDwords(base, fetch_address);
    if (HasTextureFetchDwords(fetch_dwords)) {
      StoreTextureFetchConstantLocked(slot, fetch_address, base, 0, false);
    }
  }

  for (uint32_t slot = 0; slot < std::size(g_observer.streams); ++slot) {
    const uint32_t stream_object = LoadGuestU32(
        base, device_pointer + kFableDeviceStreamObjectBaseOffset +
                  slot * sizeof(uint32_t));
    const uint32_t gpu_address_raw = LoadGuestU32(
        base, device_pointer + kFableDeviceStreamGpuAddressBaseOffset -
                  slot * 8u);
    const uint32_t gpu_address =
        DecodeFableGpuBufferPointer(gpu_address_raw);
    const uint32_t gpu_size_raw = LoadGuestU32(
        base, device_pointer + kFableDeviceStreamGpuSizeBaseOffset -
                  slot * 8u);
    const uint32_t gpu_size = DecodeFablePackedBufferSize(gpu_size_raw);
    const bool has_stream_object = IsLikelyFableObjectAddress(stream_object);
    const bool has_gpu_range =
        IsLikelyGpuByteBufferRange(gpu_address, gpu_size);
    const uint32_t stream_buffer = has_gpu_range ? gpu_address
                                                 : (has_stream_object
                                                        ? stream_object
                                                        : 0u);
    if (stream_buffer == 0) {
      continue;
    }

    RendererStreamBindingSnapshot& stream = g_observer.streams[slot];
    stream.buffer = stream_buffer;
    stream.offset = 0;

    const uint32_t stride_words = LoadGuestU8(
        base, device_pointer + kFableDeviceStreamStrideBaseOffset + slot);
    if (stride_words != 0) {
      stream.stride = stride_words * 4u;
    }
    if (has_gpu_range) {
      UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                        gpu_address, gpu_size, stream.stride,
                                        has_stream_object ? stream_object
                                                          : device_pointer);
    }
  }
}

void ApplyTextureObjectFetchConstantsToEventLocked(
    RendererDrawEventSnapshot& event) {
  constexpr uint32_t kTextureFetchConstantDwords = 6;
  for (uint32_t slot = 0; slot < std::size(event.textures); ++slot) {
    const uint32_t texture_object = event.textures[slot];
    if (texture_object == 0) {
      continue;
    }

    auto resource_it = FindResourceIteratorLocked(texture_object);
    if (resource_it == g_observer.resources.end() ||
        !IsImageResourceKind(resource_it->second.kind)) {
      continue;
    }

    const std::array<uint32_t, kTextureFetchConstantDwords> dwords =
        TextureFetchDwordsForRecord(resource_it->second);
    if (!HasTextureFetchDwords(dwords)) {
      continue;
    }

    const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
    if (fetch_dword + kTextureFetchConstantDwords >
        event.fetch_constants.size()) {
      continue;
    }

    const std::array<uint32_t, kTextureFetchConstantDwords> existing_dwords{
        event.fetch_constants[fetch_dword + 0u],
        event.fetch_constants[fetch_dword + 1u],
        event.fetch_constants[fetch_dword + 2u],
        event.fetch_constants[fetch_dword + 3u],
        event.fetch_constants[fetch_dword + 4u],
        event.fetch_constants[fetch_dword + 5u]};
    if (HasTextureFetchDwords(existing_dwords)) {
      rex::graphics::xenos::xe_gpu_texture_fetch_t existing_fetch = {};
      existing_fetch.dword_0 = existing_dwords[0];
      existing_fetch.dword_1 = existing_dwords[1];
      existing_fetch.dword_2 = existing_dwords[2];
      existing_fetch.dword_3 = existing_dwords[3];
      existing_fetch.dword_4 = existing_dwords[4];
      existing_fetch.dword_5 = existing_dwords[5];
      if (existing_fetch.type ==
          rex::graphics::xenos::FetchConstantType::kTexture) {
        const TextureFetchSnapshotInfo existing_info =
            GetTextureFetchSnapshotInfo(existing_dwords);
        const TextureFetchSnapshotInfo object_info =
            GetTextureFetchSnapshotInfo(dwords);
        if (!IsUsableTextureFetchSnapshot(object_info)) {
          continue;
        }
        if (IsUsableTextureFetchSnapshot(existing_info)) {
          const bool existing_neutral =
              IsNeutralTextureFetchSnapshot(existing_info);
          const bool object_neutral =
              IsNeutralTextureFetchSnapshot(object_info);
          if (existing_info.base_address == object_info.base_address &&
              existing_info.size == object_info.size) {
            continue;
          }
          if (!existing_neutral || object_neutral) {
            continue;
          }
        }
      }
    }

    std::copy_n(dwords.begin(), kTextureFetchConstantDwords,
                event.fetch_constants.begin() + fetch_dword);
  }
}

void PatchLatestDrawEventDeviceBindingsLocked(std::string_view symbol,
                                              uint32_t device_pointer,
                                              uint8_t* base) {
  if (!GuestDeviceStateReadableForReplay()) {
    return;
  }
  if (g_observer.draw_events.empty()) {
    return;
  }

  RendererDrawEventSnapshot* event_pointer = nullptr;
  for (auto it = g_observer.draw_events.rbegin();
       it != g_observer.draw_events.rend(); ++it) {
    if (!it->clear) {
      event_pointer = &*it;
      break;
    }
  }
  if (event_pointer == nullptr) {
    return;
  }
  RendererDrawEventSnapshot& event = *event_pointer;

  const bool had_index_buffer = event.index_buffer != 0;
  bool had_vertex_buffer = false;
  for (const RendererStreamBindingSnapshot& stream : event.streams) {
    had_vertex_buffer =
        had_vertex_buffer || IsLikelyBindableBufferAddress(stream.buffer);
  }

  RefreshDeviceInputBindingsLocked(device_pointer, base);

  const bool overwrite_root_draw_bindings =
      symbol == "sub_82205F68" || symbol == "sub_82217DB8" ||
      symbol == "sub_8221C3E8" || symbol == "sub_8221DFC0";

  if ((overwrite_root_draw_bindings || event.vertex_declaration == 0) &&
      g_observer.vertex_declaration != 0) {
    event.vertex_declaration = g_observer.vertex_declaration;
  }
  if ((overwrite_root_draw_bindings || event.index_buffer == 0) &&
      event.draw.indexed && g_observer.index_buffer != 0) {
    event.index_buffer = g_observer.index_buffer;
  }
  if ((overwrite_root_draw_bindings || event.vertex_shader == 0) &&
      g_observer.vertex_shader != 0) {
    event.vertex_shader = g_observer.vertex_shader;
  }
  if ((overwrite_root_draw_bindings || event.pixel_shader == 0) &&
      g_observer.pixel_shader != 0) {
    event.pixel_shader = g_observer.pixel_shader;
  }
  for (uint32_t slot = 0; slot < std::size(event.textures); ++slot) {
    if ((overwrite_root_draw_bindings || event.textures[slot] == 0) &&
        g_observer.textures[slot] != 0) {
      event.textures[slot] = g_observer.textures[slot];
    }
  }
  event.fetch_constants = g_observer.fetch_constants;
  ApplyTextureObjectFetchConstantsToEventLocked(event);
  event.vertex_shader_constants = g_observer.vertex_shader_constants;
  if (device_pointer != 0 &&
      SnapshotDeviceVertexFloatConstantsLocked(
          device_pointer, event.vertex_shader_constants,
          g_observer.vertex_shader_constant_valid, base)) {
    g_observer.vertex_shader_constants = event.vertex_shader_constants;
  }
  event.pixel_shader_constants = g_observer.pixel_shader_constants;
  if (device_pointer != 0 &&
      SnapshotDevicePixelFloatConstantsLocked(
          device_pointer, event.pixel_shader_constants,
          g_observer.pixel_shader_constant_valid, base)) {
    g_observer.pixel_shader_constants = event.pixel_shader_constants;
  }
  for (uint32_t slot = 0; slot < std::size(event.streams); ++slot) {
    const RendererStreamBindingSnapshot& source = g_observer.streams[slot];
    RendererStreamBindingSnapshot& target = event.streams[slot];
    if ((overwrite_root_draw_bindings ||
         !IsLikelyBindableBufferAddress(target.buffer)) &&
        IsLikelyBindableBufferAddress(source.buffer)) {
      target = source;
    } else if (IsLikelyBindableBufferAddress(target.buffer) &&
               target.stride == 0 &&
               source.buffer == target.buffer && source.stride != 0) {
      target.stride = source.stride;
    }
  }
  RefreshDrawEventResourceSerialFromBindingsLocked(event);

  bool has_vertex_buffer = false;
  for (const RendererStreamBindingSnapshot& stream : event.streams) {
    has_vertex_buffer =
        has_vertex_buffer || IsLikelyBindableBufferAddress(stream.buffer);
  }
  const bool has_index_buffer = event.index_buffer != 0;
  if ((!had_vertex_buffer && has_vertex_buffer) ||
      (!had_index_buffer && has_index_buffer)) {
    if (g_observer.logged_late_draw_binding_patches < 8) {
      REXLOG_INFO(
          "Refii renderer patched post-flush Fable draw bindings device=0x{:08X} "
          "vb0=0x{:08X}/{} ib=0x{:08X} decl=0x{:08X}",
          device_pointer, event.streams[0].buffer, event.streams[0].stride,
          event.index_buffer, event.vertex_declaration);
      ++g_observer.logged_late_draw_binding_patches;
    }
  } else if ((!has_vertex_buffer ||
              (event.draw.indexed && !has_index_buffer)) &&
             g_observer.logged_missing_draw_bindings < 8) {
    const uint32_t stream_object0 =
        LoadGuestU32(base, device_pointer + kFableDeviceStreamObjectBaseOffset);
    const uint32_t stream_gpu0 = NormalizeGpuPointer(LoadGuestU32(
        base, device_pointer + kFableDeviceStreamGpuAddressBaseOffset));
    const uint32_t stream_size0 =
        LoadGuestU32(base, device_pointer + kFableDeviceStreamGpuSizeBaseOffset);
    const uint32_t index_object =
        LoadGuestU32(base, device_pointer + kFableDeviceIndexBufferOffset);
    REXLOG_INFO(
        "Refii renderer draw still missing bindings hook={} device=0x{:08X} "
        "stream0_object=0x{:08X} stream0_gpu=0x{:08X}/{} "
        "index_object=0x{:08X} draw=indexed:{} prim:{} base:{} start:{} "
        "count:{}",
        symbol, device_pointer, stream_object0, stream_gpu0, stream_size0,
        index_object, event.draw.indexed ? 1 : 0, event.draw.primitive_type,
        event.draw.base_vertex, event.draw.start, event.draw.primitive_count);
    ++g_observer.logged_missing_draw_bindings;
  }
}

void QueueDrawEventLocked(uint32_t device_pointer, bool draw_vertices_up,
                          bool refresh_device_bindings) {
  if (g_observer.draw_events.size() >= kMaxBufferedDrawEvents) {
    g_observer.draw_events.erase(g_observer.draw_events.begin());
    ++g_observer.dropped_draw_events;
  }

  if (refresh_device_bindings) {
    RefreshDeviceInputBindingsLocked(device_pointer, g_observer.memory_base);
  }

  RendererDrawEventSnapshot event;
  event.serial = ++g_observer.draw_event_serial;
  event.draw = g_observer.last_draw;
  event.draw_vertices_up = draw_vertices_up;
  uint32_t depth_stencil = 0;
  SanitizeBoundSurfaceSnapshotLocked(event.render_targets,
                                     std::size(event.render_targets),
                                     depth_stencil);
  ResolvePm4BoundSurfacesLocked(event.render_targets,
                                std::size(event.render_targets),
                                depth_stencil);
  event.depth_stencil = depth_stencil;
  event.vertex_declaration = g_observer.vertex_declaration;
  event.vertex_shader = g_observer.vertex_shader;
  event.pixel_shader = g_observer.pixel_shader;
  event.index_buffer = g_observer.index_buffer;
  event.pm4_registers_valid = g_observer.pm4_registers_valid;
  event.rb_modecontrol = g_observer.rb_modecontrol;
  event.rb_surface_info = g_observer.rb_surface_info;
  std::memcpy(event.rb_color_info, g_observer.rb_color_info,
              sizeof(event.rb_color_info));
  event.rb_depth_info = g_observer.rb_depth_info;
  event.rb_copy_control = g_observer.rb_copy_control;
  event.rb_copy_dest_base = g_observer.rb_copy_dest_base;
  event.rb_copy_dest_pitch = g_observer.rb_copy_dest_pitch;
  event.rb_copy_dest_info = g_observer.rb_copy_dest_info;
  event.rb_copy_func = g_observer.rb_copy_func;
  event.rb_copy_ref = g_observer.rb_copy_ref;
  event.rb_copy_mask = g_observer.rb_copy_mask;
  event.rb_copy_surface_slice = g_observer.rb_copy_surface_slice;
  event.rb_depthcontrol = g_observer.rb_depthcontrol;
  event.rb_color_mask = g_observer.rb_color_mask;
  std::memcpy(event.rb_blendcontrol, g_observer.rb_blendcontrol,
              sizeof(event.rb_blendcontrol));
  event.rb_colorcontrol = g_observer.rb_colorcontrol;
  event.rb_alpha_ref = g_observer.rb_alpha_ref;
  event.rb_stencilrefmask = g_observer.rb_stencilrefmask;
  event.rb_stencilrefmask_bf = g_observer.rb_stencilrefmask_bf;
  event.pa_su_sc_mode_cntl = g_observer.pa_su_sc_mode_cntl;
  if (GuestDeviceStateReadableForReplay() && device_pointer != 0 &&
      g_observer.memory_base) {
    event.device_registers_valid = true;
    const uint32_t device_colorcontrol = LoadGuestU32(
        g_observer.memory_base, device_pointer + kFableDeviceColorControlOffset);
    const uint32_t device_alpha_ref = LoadGuestU32(
        g_observer.memory_base, device_pointer + kFableDeviceAlphaRefOffset);
    if (!event.pm4_registers_valid) {
      event.rb_colorcontrol = device_colorcontrol;
      event.rb_alpha_ref = device_alpha_ref;
    }
    event.rb_stencil_ref = LoadGuestU8(
        g_observer.memory_base,
        device_pointer + kFableDeviceStencilRefOffset);
    event.rb_stencil_read_mask = LoadGuestU8(
        g_observer.memory_base,
        device_pointer + kFableDeviceStencilReadMaskOffset);
    event.rb_stencil_write_mask = LoadGuestU8(
        g_observer.memory_base,
        device_pointer + kFableDeviceStencilWriteMaskOffset);
    if (!event.pm4_registers_valid) {
      event.rb_stencilrefmask =
          uint32_t(event.rb_stencil_ref) |
          (uint32_t(event.rb_stencil_read_mask) << 8u) |
          (uint32_t(event.rb_stencil_write_mask) << 16u);
      event.rb_stencilrefmask_bf = event.rb_stencilrefmask;
    }
  }
  std::memcpy(event.textures, g_observer.textures, sizeof(event.textures));
  event.render_states = g_observer.render_states;
  event.sampler_states = g_observer.sampler_states;
  event.fetch_constants = g_observer.fetch_constants;
  ApplyTextureObjectFetchConstantsToEventLocked(event);
  std::memcpy(event.streams, g_observer.streams, sizeof(event.streams));
  event.vertex_shader_constants = g_observer.vertex_shader_constants;
  if (GuestDeviceStateReadableForReplay() &&
      SnapshotDeviceVertexFloatConstantsLocked(
          device_pointer, event.vertex_shader_constants,
          g_observer.vertex_shader_constant_valid,
          g_observer.memory_base)) {
    g_observer.vertex_shader_constants = event.vertex_shader_constants;
  }
  event.pixel_shader_constants = g_observer.pixel_shader_constants;
  if (GuestDeviceStateReadableForReplay() &&
      SnapshotDevicePixelFloatConstantsLocked(
          device_pointer, event.pixel_shader_constants,
          g_observer.pixel_shader_constant_valid, g_observer.memory_base)) {
    g_observer.pixel_shader_constants = event.pixel_shader_constants;
  }
  event.vertex_shader_int_constants = g_observer.vertex_shader_int_constants;
  event.pixel_shader_int_constants = g_observer.pixel_shader_int_constants;
  event.vertex_shader_int_constant_valid_mask =
      g_observer.vertex_shader_int_constant_valid_mask;
  event.pixel_shader_int_constant_valid_mask =
      g_observer.pixel_shader_int_constant_valid_mask;
  event.vertex_shader_bool_constants = g_observer.vertex_shader_bool_constants;
  event.pixel_shader_bool_constants = g_observer.pixel_shader_bool_constants;
  event.viewport_pointer = g_observer.viewport_pointer;
  event.scissor_pointer = g_observer.scissor_pointer;
  event.viewport = g_observer.viewport;
  event.scissor = g_observer.scissor;
  event.scaler = g_observer.scaler;
  event.clear_flags = g_observer.clear_flags;
  event.state_flush_lr = g_observer.last_state_flush_lr;
  event.resource_serial = g_observer.serial;
  g_observer.draw_events.push_back(event);
}

void PatchDrawEventStateFromObserverLocked(RendererDrawEventSnapshot& event,
                                           uint32_t device_pointer) {
  uint32_t depth_stencil = 0;
  SanitizeBoundSurfaceSnapshotLocked(event.render_targets,
                                     std::size(event.render_targets),
                                     depth_stencil);
  ResolvePm4BoundSurfacesLocked(event.render_targets,
                                std::size(event.render_targets),
                                depth_stencil);
  event.depth_stencil = depth_stencil;
  event.vertex_declaration = g_observer.vertex_declaration;
  event.vertex_shader = g_observer.vertex_shader;
  event.pixel_shader = g_observer.pixel_shader;
  event.index_buffer = g_observer.index_buffer;
  event.pm4_registers_valid = g_observer.pm4_registers_valid;
  event.rb_modecontrol = g_observer.rb_modecontrol;
  event.rb_surface_info = g_observer.rb_surface_info;
  std::memcpy(event.rb_color_info, g_observer.rb_color_info,
              sizeof(event.rb_color_info));
  event.rb_depth_info = g_observer.rb_depth_info;
  event.rb_copy_control = g_observer.rb_copy_control;
  event.rb_copy_dest_base = g_observer.rb_copy_dest_base;
  event.rb_copy_dest_pitch = g_observer.rb_copy_dest_pitch;
  event.rb_copy_dest_info = g_observer.rb_copy_dest_info;
  event.rb_copy_func = g_observer.rb_copy_func;
  event.rb_copy_ref = g_observer.rb_copy_ref;
  event.rb_copy_mask = g_observer.rb_copy_mask;
  event.rb_copy_surface_slice = g_observer.rb_copy_surface_slice;
  event.rb_depthcontrol = g_observer.rb_depthcontrol;
  event.rb_color_mask = g_observer.rb_color_mask;
  std::memcpy(event.rb_blendcontrol, g_observer.rb_blendcontrol,
              sizeof(event.rb_blendcontrol));
  event.rb_colorcontrol = g_observer.rb_colorcontrol;
  event.rb_alpha_ref = g_observer.rb_alpha_ref;
  event.rb_stencilrefmask = g_observer.rb_stencilrefmask;
  event.rb_stencilrefmask_bf = g_observer.rb_stencilrefmask_bf;
  event.pa_su_sc_mode_cntl = g_observer.pa_su_sc_mode_cntl;

  if (GuestDeviceStateReadableForReplay() && device_pointer != 0 &&
      g_observer.memory_base) {
    event.device_registers_valid = true;
    const uint32_t device_colorcontrol = LoadGuestU32(
        g_observer.memory_base, device_pointer + kFableDeviceColorControlOffset);
    const uint32_t device_alpha_ref = LoadGuestU32(
        g_observer.memory_base, device_pointer + kFableDeviceAlphaRefOffset);
    if (!event.pm4_registers_valid) {
      event.rb_colorcontrol = device_colorcontrol;
      event.rb_alpha_ref = device_alpha_ref;
    }
    event.rb_stencil_ref = LoadGuestU8(
        g_observer.memory_base,
        device_pointer + kFableDeviceStencilRefOffset);
    event.rb_stencil_read_mask = LoadGuestU8(
        g_observer.memory_base,
        device_pointer + kFableDeviceStencilReadMaskOffset);
    event.rb_stencil_write_mask = LoadGuestU8(
        g_observer.memory_base,
        device_pointer + kFableDeviceStencilWriteMaskOffset);
    if (!event.pm4_registers_valid) {
      event.rb_stencilrefmask =
          uint32_t(event.rb_stencil_ref) |
          (uint32_t(event.rb_stencil_read_mask) << 8u) |
          (uint32_t(event.rb_stencil_write_mask) << 16u);
      event.rb_stencilrefmask_bf = event.rb_stencilrefmask;
    }
  }

  std::memcpy(event.textures, g_observer.textures, sizeof(event.textures));
  event.render_states = g_observer.render_states;
  event.sampler_states = g_observer.sampler_states;
  event.fetch_constants = g_observer.fetch_constants;
  ApplyTextureObjectFetchConstantsToEventLocked(event);
  std::memcpy(event.streams, g_observer.streams, sizeof(event.streams));
  event.vertex_shader_constants = g_observer.vertex_shader_constants;
  if (GuestDeviceStateReadableForReplay() && device_pointer != 0 &&
      SnapshotDeviceVertexFloatConstantsLocked(
          device_pointer, event.vertex_shader_constants,
          g_observer.vertex_shader_constant_valid, g_observer.memory_base)) {
    g_observer.vertex_shader_constants = event.vertex_shader_constants;
  }
  event.pixel_shader_constants = g_observer.pixel_shader_constants;
  if (GuestDeviceStateReadableForReplay() && device_pointer != 0 &&
      SnapshotDevicePixelFloatConstantsLocked(
          device_pointer, event.pixel_shader_constants,
          g_observer.pixel_shader_constant_valid, g_observer.memory_base)) {
    g_observer.pixel_shader_constants = event.pixel_shader_constants;
  }
  event.vertex_shader_int_constants = g_observer.vertex_shader_int_constants;
  event.pixel_shader_int_constants = g_observer.pixel_shader_int_constants;
  event.vertex_shader_int_constant_valid_mask =
      g_observer.vertex_shader_int_constant_valid_mask;
  event.pixel_shader_int_constant_valid_mask =
      g_observer.pixel_shader_int_constant_valid_mask;
  event.vertex_shader_bool_constants = g_observer.vertex_shader_bool_constants;
  event.pixel_shader_bool_constants = g_observer.pixel_shader_bool_constants;
  event.viewport_pointer = g_observer.viewport_pointer;
  event.scissor_pointer = g_observer.scissor_pointer;
  event.viewport = g_observer.viewport;
  event.scissor = g_observer.scissor;
  event.scaler = g_observer.scaler;
  event.clear_flags = g_observer.clear_flags;
  event.state_flush_lr = g_observer.last_state_flush_lr;
  event.resource_serial = std::max(event.resource_serial, g_observer.serial);
}

void PatchActiveWrappedDrawEventStateLocked() {
  if (g_observer.active_wrapped_draw_serial == 0) {
    return;
  }

  for (auto it = g_observer.draw_events.rbegin();
       it != g_observer.draw_events.rend(); ++it) {
    if (it->serial != g_observer.active_wrapped_draw_serial) {
      continue;
    }
    PatchDrawEventStateFromObserverLocked(*it,
                                          g_observer.active_wrapped_draw_device);
    return;
  }
}

void BeginWrappedDrawStatePatchLocked(uint32_t device_pointer) {
  if (g_observer.draw_events.empty()) {
    return;
  }
  g_observer.active_wrapped_draw_serial = g_observer.draw_events.back().serial;
  g_observer.active_wrapped_draw_device = device_pointer;
}

void EndWrappedDrawStatePatchLocked(std::string_view symbol,
                                    const RendererHookArgs& args) {
  if (symbol == "sub_82205F68" && IsDrawVerticesUpInnerScratchSubmit(args)) {
    return;
  }
  if (symbol != "sub_82205F68" && symbol != "sub_82217DB8" &&
      symbol != "sub_8221C3E8" && symbol != "sub_8221DFC0") {
    return;
  }

  PatchActiveWrappedDrawEventStateLocked();
  g_observer.active_wrapped_draw_serial = 0;
  g_observer.active_wrapped_draw_device = 0;
}

bool IsFableEndExportInvalidateCaller(uint32_t source_lr) {
  // EndExport emits D3D::InvalidateCaches for the resolved destination ranges.
  // Hook only those call sites so ordinary cache invalidations don't become
  // synthetic resolve aliases.
  return source_lr == 0x821968A4u || source_lr == 0x821968CCu;
}

void QueueExportInvalidateResolveEventLocked(uint32_t destination_base,
                                             uint32_t byte_size,
                                             uint32_t source_lr) {
  if (!IsFableEndExportInvalidateCaller(source_lr) || destination_base == 0 ||
      byte_size == 0) {
    return;
  }

  const uint32_t normalized_base = NormalizeGpuPointer(destination_base);
  if (normalized_base == 0) {
    return;
  }

  RendererDrawEventSnapshot event;
  event.serial = ++g_observer.draw_event_serial;
  event.pm4_registers_valid = true;
  event.rb_modecontrol = 6u;
  event.rb_surface_info = g_observer.rb_surface_info;
  std::memcpy(event.rb_color_info, g_observer.rb_color_info,
              sizeof(event.rb_color_info));
  event.rb_depth_info = g_observer.rb_depth_info;
  event.rb_copy_control = g_observer.rb_copy_control;
  event.rb_copy_dest_base = normalized_base;
  event.rb_copy_dest_pitch = g_observer.rb_copy_dest_pitch;
  event.rb_copy_dest_info = g_observer.rb_copy_dest_info;
  event.rb_copy_func = g_observer.rb_copy_func;
  event.rb_copy_ref = g_observer.rb_copy_ref;
  event.rb_copy_mask = g_observer.rb_copy_mask;
  event.rb_copy_surface_slice = g_observer.rb_copy_surface_slice;
  event.state_flush_lr = source_lr;

  uint32_t depth_stencil = 0;
  SanitizeBoundSurfaceSnapshotLocked(event.render_targets,
                                     std::size(event.render_targets),
                                     depth_stencil);
  ResolvePm4BoundSurfacesLocked(event.render_targets,
                                std::size(event.render_targets),
                                depth_stencil);
  event.depth_stencil = depth_stencil;

  const uint32_t source_slot = event.rb_copy_control & 0x7u;
  const bool depth_resolve = source_slot >= std::size(event.render_targets);
  uint32_t source_surface =
      depth_resolve ? event.depth_stencil : event.render_targets[source_slot];
  if ((source_surface == 0 || depth_resolve) && event.render_targets[0] != 0) {
    event.rb_copy_control = 0u;
    source_surface = event.render_targets[0];
  }
  if (source_surface == 0) {
    return;
  }

  if (g_observer.draw_events.size() >= kMaxBufferedDrawEvents) {
    g_observer.draw_events.erase(g_observer.draw_events.begin());
    ++g_observer.dropped_draw_events;
  }

  event.viewport_pointer = g_observer.viewport_pointer;
  event.scissor_pointer = g_observer.scissor_pointer;
  event.viewport = g_observer.viewport;
  event.scissor = g_observer.scissor;
  event.scaler = g_observer.scaler;
  event.resource_serial = g_observer.serial;
  g_observer.draw_events.push_back(event);

  if (RendererDrawTracingEnabled() && g_observer.logged_export_resolves < 16u) {
    REXLOG_INFO(
        "Refii renderer queued export invalidate resolve #{} src=0x{:08X} "
        "slot={} dest=0x{:08X} size={} lr=0x{:08X}",
        g_observer.logged_export_resolves + 1u, source_surface,
        event.rb_copy_control & 0x7u, normalized_base, byte_size, source_lr);
    ++g_observer.logged_export_resolves;
  }
}

uint64_t Pm4ResolveCopyKeyLocked(const RendererDrawEventSnapshot& event) {
  uint64_t key = static_cast<uint64_t>(event.rb_copy_dest_base) << 32u;
  key ^= static_cast<uint64_t>(event.rb_copy_control) << 24u;
  key ^= static_cast<uint64_t>(event.rb_copy_dest_pitch) << 8u;
  key ^= static_cast<uint64_t>(event.rb_copy_dest_info);
  key ^= static_cast<uint64_t>(event.rb_surface_info) << 3u;
  key ^= static_cast<uint64_t>(event.rb_depth_info) << 17u;
  for (uint32_t slot = 0; slot < std::size(event.render_targets); ++slot) {
    key ^= static_cast<uint64_t>(event.render_targets[slot])
           << ((slot * 13u) & 31u);
  }
  key ^= static_cast<uint64_t>(event.depth_stencil) << 11u;
  return key;
}

void QueuePm4ResolveCopyEventLocked(uint32_t source_lr) {
  constexpr uint32_t kXenosEdramModeCopy = 6u;
  if (!g_observer.pm4_registers_valid ||
      (g_observer.rb_modecontrol & 0x7u) != kXenosEdramModeCopy ||
      g_observer.rb_copy_dest_base == 0) {
    return;
  }

  RendererDrawEventSnapshot event;
  event.serial = ++g_observer.draw_event_serial;
  event.pm4_registers_valid = true;
  event.rb_modecontrol = g_observer.rb_modecontrol;
  event.rb_surface_info = g_observer.rb_surface_info;
  std::memcpy(event.rb_color_info, g_observer.rb_color_info,
              sizeof(event.rb_color_info));
  event.rb_depth_info = g_observer.rb_depth_info;
  event.rb_copy_control = g_observer.rb_copy_control;
  event.rb_copy_dest_base = g_observer.rb_copy_dest_base;
  event.rb_copy_dest_pitch = g_observer.rb_copy_dest_pitch;
  event.rb_copy_dest_info = g_observer.rb_copy_dest_info;
  event.rb_copy_func = g_observer.rb_copy_func;
  event.rb_copy_ref = g_observer.rb_copy_ref;
  event.rb_copy_mask = g_observer.rb_copy_mask;
  event.rb_copy_surface_slice = g_observer.rb_copy_surface_slice;
  event.state_flush_lr = source_lr;

  uint32_t depth_stencil = 0;
  SanitizeBoundSurfaceSnapshotLocked(event.render_targets,
                                     std::size(event.render_targets),
                                     depth_stencil);
  ResolvePm4BoundSurfacesLocked(event.render_targets,
                                std::size(event.render_targets),
                                depth_stencil);
  event.depth_stencil = depth_stencil;

  const uint32_t source_slot = event.rb_copy_control & 0x7u;
  const bool depth_resolve = source_slot >= std::size(event.render_targets);
  const uint32_t source_surface =
      depth_resolve ? event.depth_stencil : event.render_targets[source_slot];
  if (source_surface == 0) {
    return;
  }

  const uint64_t resolve_key = Pm4ResolveCopyKeyLocked(event);
  if (g_observer.last_pm4_resolve_command_write ==
          g_observer.command_writes &&
      g_observer.last_pm4_resolve_key == resolve_key) {
    return;
  }
  g_observer.last_pm4_resolve_command_write = g_observer.command_writes;
  g_observer.last_pm4_resolve_key = resolve_key;

  if (g_observer.draw_events.size() >= kMaxBufferedDrawEvents) {
    g_observer.draw_events.erase(g_observer.draw_events.begin());
    ++g_observer.dropped_draw_events;
  }

  event.viewport_pointer = g_observer.viewport_pointer;
  event.scissor_pointer = g_observer.scissor_pointer;
  event.viewport = g_observer.viewport;
  event.scissor = g_observer.scissor;
  event.scaler = g_observer.scaler;
  event.resource_serial = g_observer.serial;
  g_observer.draw_events.push_back(event);

  if (RendererDrawTracingEnabled() && g_observer.logged_pm4_resolves < 16u) {
    REXLOG_INFO(
        "Refii renderer queued PM4 resolve #{} src={} 0x{:08X} slot={} "
        "dest=0x{:08X} pitch=0x{:08X} info=0x{:08X} lr=0x{:08X}",
        g_observer.logged_pm4_resolves + 1u,
        depth_resolve ? "depth" : "color", source_surface, source_slot,
        event.rb_copy_dest_base, event.rb_copy_dest_pitch,
        event.rb_copy_dest_info, source_lr);
    ++g_observer.logged_pm4_resolves;
  }
}

bool IsPrivateTextureRectangleInnerSubmitCandidate(
    const RendererDrawEventSnapshot& candidate,
    const RendererDrawEventSnapshot& wrapper_event) {
  if (candidate.clear || candidate.private_texture_rectangle) {
    return false;
  }
  if (candidate.draw.indexed || wrapper_event.draw.indexed) {
    return false;
  }
  if (candidate.draw.primitive_type != wrapper_event.draw.primitive_type ||
      candidate.draw.primitive_type != 8u) {
    return false;
  }
  return candidate.draw.base_vertex == wrapper_event.draw.base_vertex &&
         candidate.draw.start == wrapper_event.draw.start &&
         candidate.draw.primitive_count == wrapper_event.draw.primitive_count &&
         candidate.draw.primitive_count == 3u;
}

bool PromotePreviousPrivateTextureRectangleSubmitLocked(
    size_t prior_event_count, const RendererDrawEventSnapshot& wrapper_event) {
  if (prior_event_count == 0 ||
      prior_event_count > g_observer.draw_events.size()) {
    return false;
  }

  const size_t search_begin =
      prior_event_count > 8u ? prior_event_count - 8u : 0u;
  for (size_t index = prior_event_count; index > search_begin; --index) {
    RendererDrawEventSnapshot& candidate = g_observer.draw_events[index - 1u];
    if (!IsPrivateTextureRectangleInnerSubmitCandidate(candidate,
                                                       wrapper_event)) {
      continue;
    }

    const uint64_t original_serial = candidate.serial;
    candidate = wrapper_event;
    candidate.serial = original_serial;
    ++g_observer.promoted_private_texture_rectangle_submits;
    if (RendererDrawTracingEnabled() &&
        g_observer.promoted_private_texture_rectangle_submits <= 16u) {
      REXLOG_INFO(
          "Refii renderer promoted private texture rectangle submit #{} "
          "serial={} rt0=0x{:08X} depth=0x{:08X} src=0x{:08X} "
          "ps=0x{:08X}",
          g_observer.promoted_private_texture_rectangle_submits,
          original_serial, candidate.render_targets[0],
          candidate.depth_stencil,
          candidate.textures[candidate.private_texture_slot],
          candidate.pixel_shader);
    }
    return true;
  }

  return false;
}

bool IsDrawVerticesUpInnerScratchSubmit(const RendererHookArgs& args) {
  return args.lr >= kD3DDeviceDrawVerticesUPStart &&
         args.lr < kD3DDeviceDrawVerticesUPEnd;
}

bool IsWrappedDrawSubmitHook(std::string_view symbol) {
  return symbol == "sub_82205F68" || symbol == "sub_82217DB8" ||
         symbol == "sub_82206888" || symbol == "sub_8221C3E8" ||
         symbol == "sub_8221DFC0" || symbol == "sub_82265CD8";
}

void QueueClearEventLocked(uint32_t flags, uint32_t color_pointer,
                           double depth, uint32_t stencil,
                           RendererScissorSnapshot rect) {
  if (g_observer.draw_events.size() >= kMaxBufferedDrawEvents) {
    g_observer.draw_events.erase(g_observer.draw_events.begin());
    ++g_observer.dropped_draw_events;
  }

  RendererDrawEventSnapshot event;
  event.serial = ++g_observer.draw_event_serial;
  event.clear = true;
  event.clear_flags = flags;
  event.clear_depth =
      std::isfinite(depth) ? static_cast<float>(depth) : 1.0f;
  event.clear_stencil = stencil;
  event.clear_rect = rect;
  if (g_observer.memory_base && color_pointer != 0) {
    for (uint32_t i = 0; i < 4; ++i) {
      event.clear_color[i] =
          LoadGuestF32(g_observer.memory_base, color_pointer + i * 4u);
    }
  }

  uint32_t depth_stencil = 0;
  SanitizeBoundSurfaceSnapshotLocked(event.render_targets,
                                     std::size(event.render_targets),
                                     depth_stencil);
  ResolvePm4BoundSurfacesLocked(event.render_targets,
                                std::size(event.render_targets),
                                depth_stencil);
  event.depth_stencil = depth_stencil;
  event.viewport_pointer = g_observer.viewport_pointer;
  event.scissor_pointer = g_observer.scissor_pointer;
  event.viewport = g_observer.viewport;
  event.scissor = g_observer.scissor;
  event.scaler = g_observer.scaler;
  event.state_flush_lr = g_observer.last_state_flush_lr;
  event.resource_serial = g_observer.serial;
  g_observer.draw_events.push_back(event);
}

void ObserveRendererStateOrDrawLocked(std::string_view symbol,
                                      const RendererHookArgs& args,
                                      uint8_t* base) {
  const uint32_t render_state_slot = RenderStateSlotForSymbol(symbol);
  if (render_state_slot < g_observer.render_states.size()) {
    g_observer.render_states[render_state_slot] = args.r4;
    RememberRequestedDepthRenderStateLocked(render_state_slot, args.r4);
    ++g_observer.state_changes;
    return;
  }

  if (symbol == "sub_8221AE18" && base && args.r3 != 0) {
    RefreshEffectiveDepthRenderStateLocked(base, args.r3);
    ++g_observer.state_changes;
    return;
  }

  const uint32_t sampler_state_slot = SamplerStateSlotForSymbol(symbol);
  if (sampler_state_slot != UINT32_MAX) {
    StoreSamplerStateLocked(args.r4, sampler_state_slot, args.r5);
    ++g_observer.state_changes;
    return;
  }

  if (symbol == "sub_822869A0" || symbol == "D3DDevice_SetRenderTarget") {
    if (args.r4 < 4) {
      SetObservedRenderTargetLocked(args.r4, args.r5, args.r3, base);
      ++g_observer.state_changes;
    }
    return;
  }
  if (symbol == "sub_82264590" ||
      symbol == "D3DDevice_SetDepthStencilSurface") {
    SetObservedDepthSurfaceLocked(base, args.r3, args.r4);
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821F9F58") {
    MirrorViewportToGuestDeviceLocked(base, args.r3, args.r4);
    g_observer.viewport_pointer = args.r4;
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821F9D00" || symbol == "sub_821F9918") {
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821F9C08") {
    MirrorScissorToGuestDeviceLocked(base, args.r3, args.r4);
    g_observer.scissor_pointer = args.r4;
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821B6EE0") {
    if (args.r4 < 16) {
      MirrorTextureBindingToGuestDeviceLocked(base, args.r3, args.r4, args.r5);
      g_observer.textures[args.r4] = args.r5;
      if (args.r5 != 0) {
        StoreTextureObjectFetchDwordsLocked(
            args.r5,
            args.r3 + kFableDeviceTextureFetchBaseOffset +
                args.r4 * kFableDeviceTextureFetchStride,
            args.r4, base, args.lr);
      }
      ++g_observer.state_changes;
    }
    return;
  }
  if (symbol == "sub_821B6C60") {
    if (args.r4 < 16) {
      MirrorStreamBindingToGuestDeviceLocked(base, args.r3, args.r4, args.r5,
                                             args.r6, args.r7, args.r8);
      uint32_t stream_buffer = args.r5;
      uint32_t stream_offset = args.r6;
      uint32_t stream_size = 0;
      uint32_t stream_upload_source = 0;
      if (base && IsLikelyFableObjectAddress(args.r5)) {
        const uint32_t object_gpu_base_raw =
            LoadGuestU32(base, args.r5 + 0x18);
        const uint32_t object_gpu_base =
            DecodeFableGpuBufferPointer(object_gpu_base_raw + args.r6);
        const uint32_t object_gpu_size =
            DecodeFablePackedBufferSize(LoadGuestU32(base, args.r5 + 0x1C));
        if (args.r6 <= object_gpu_size) {
          const uint32_t gpu_size = object_gpu_size - args.r6;
          if (IsLikelyGpuByteBufferRange(object_gpu_base, gpu_size)) {
            stream_buffer = object_gpu_base;
            stream_offset = 0;
            stream_size = gpu_size;
            const uint32_t raw_upload_source =
                (object_gpu_base_raw & ~3u) + args.r6;
            const uint32_t decoded_nz = FindFirstNonZeroGuestByte(
                base, object_gpu_base, gpu_size, 64u * 1024u);
            const uint32_t raw_nz = FindFirstNonZeroGuestByte(
                base, raw_upload_source, gpu_size, 64u * 1024u);
            if (decoded_nz == UINT32_MAX && raw_nz != UINT32_MAX) {
              stream_upload_source = raw_upload_source;
            }
          }
        }
        if (stream_size == 0) {
          const uint32_t xg_gpu_base_raw =
              LoadGuestU32(base, args.r5 + 0x1C);
          const uint32_t xg_gpu_base =
              DecodeFableGpuBufferPointer(xg_gpu_base_raw + args.r6);
          const uint32_t xg_gpu_size =
              DecodeFablePackedBufferSize(
                  LoadGuestU32(base, args.r5 + 0x18) & ~3u);
          if (args.r6 <= xg_gpu_size) {
            const uint32_t gpu_size = xg_gpu_size - args.r6;
            if (IsLikelyGpuByteBufferRange(xg_gpu_base, gpu_size)) {
              stream_buffer = xg_gpu_base;
              stream_offset = 0;
              stream_size = gpu_size;
              const uint32_t raw_upload_source =
                  (xg_gpu_base_raw & ~3u) + args.r6;
              const uint32_t decoded_nz = FindFirstNonZeroGuestByte(
                  base, xg_gpu_base, gpu_size, 64u * 1024u);
              const uint32_t raw_nz = FindFirstNonZeroGuestByte(
                  base, raw_upload_source, gpu_size, 64u * 1024u);
              if (decoded_nz == UINT32_MAX && raw_nz != UINT32_MAX) {
                stream_upload_source = raw_upload_source;
              }
            }
          }
        }
        if (stream_size == 0 && g_observer.logged_stream_source_shapes < 8) {
          REXLOG_INFO(
              "Refii renderer stream source object needs shape decode "
              "slot={} object=0x{:08X} offset={} stride={} words "
              "+00/+04/+08/+0C/+10/+14/+18/+1C/+20/+24/+28/+2C = "
              "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
              "{:08X}/{:08X}/{:08X}/{:08X}",
              args.r4, args.r5, args.r6, args.r7,
              LoadGuestU32(base, args.r5 + 0x00),
              LoadGuestU32(base, args.r5 + 0x04),
              LoadGuestU32(base, args.r5 + 0x08),
              LoadGuestU32(base, args.r5 + 0x0C),
              LoadGuestU32(base, args.r5 + 0x10),
              LoadGuestU32(base, args.r5 + 0x14),
              LoadGuestU32(base, args.r5 + 0x18),
              LoadGuestU32(base, args.r5 + 0x1C),
              LoadGuestU32(base, args.r5 + 0x20),
              LoadGuestU32(base, args.r5 + 0x24),
              LoadGuestU32(base, args.r5 + 0x28),
              LoadGuestU32(base, args.r5 + 0x2C));
          ++g_observer.logged_stream_source_shapes;
        }
      }
      g_observer.streams[args.r4] = {
          .buffer = stream_buffer,
          .offset = stream_offset,
          .stride = args.r7,
      };
      if (stream_size != 0) {
        const auto exact_stream_resource =
            g_observer.resources.find(stream_buffer);
        const RendererResourceRecord* existing_stream_resource =
            exact_stream_resource != g_observer.resources.end()
                ? &exact_stream_resource->second
                : nullptr;
        if (existing_stream_resource == nullptr ||
            !HasNonZeroUploadData(*existing_stream_resource)) {
          UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                            stream_buffer, stream_size, args.r7,
                                            args.r5, stream_upload_source);
          if (stream_upload_source != 0 &&
              g_observer.logged_stream_upload_aliases < 8) {
            REXLOG_INFO(
                "Refii renderer copied Fable stream upload from high alias "
                "slot={} object=0x{:08X} offset={} stride={} "
                "stream=0x{:08X}/{} source=0x{:08X}",
                args.r4, args.r5, args.r6, args.r7, stream_buffer,
                stream_size, stream_upload_source);
            ++g_observer.logged_stream_upload_aliases;
          }
        }
        if (base && stream_upload_source == 0 &&
            g_observer.logged_stream_source_zero_ranges < 8) {
          constexpr uint32_t kStreamZeroScanBytes = 64u * 1024u;
          const uint32_t current_nz = FindFirstNonZeroGuestByte(
              base, stream_buffer, stream_size, kStreamZeroScanBytes);
          if (current_nz == UINT32_MAX) {
            const uint32_t raw_base = LoadGuestU32(base, args.r5 + 0x18);
            const uint32_t raw_size = LoadGuestU32(base, args.r5 + 0x1C);
            const uint32_t raw_parent24 = LoadGuestU32(base, args.r5 + 0x24);
            const uint32_t raw_alias = (raw_base & ~3u) + args.r6;
            const uint32_t base_no_offset =
                DecodeFableGpuBufferPointer(raw_base);
            const uint32_t plus_1000 = stream_buffer + 0x1000u;
            const uint32_t minus_1000 =
                stream_buffer > 0x1000u ? stream_buffer - 0x1000u : 0u;
            const uint32_t parent24 =
                DecodeFableGpuBufferPointer(raw_parent24 + args.r6);
            const uint32_t no_offset_nz = FindFirstNonZeroGuestByte(
                base, base_no_offset, stream_size, kStreamZeroScanBytes);
            const uint32_t plus_nz = FindFirstNonZeroGuestByte(
                base, plus_1000, stream_size, kStreamZeroScanBytes);
            const uint32_t minus_nz = FindFirstNonZeroGuestByte(
                base, minus_1000, stream_size, kStreamZeroScanBytes);
            const uint32_t parent24_nz = FindFirstNonZeroGuestByte(
                base, parent24, stream_size, kStreamZeroScanBytes);
            const uint32_t raw_alias_nz = FindFirstNonZeroGuestByte(
                base, raw_alias, stream_size, kStreamZeroScanBytes);
            REXLOG_INFO(
                "Refii renderer decoded stream range is zero slot={} "
                "object=0x{:08X} offset={} stride={} stream=0x{:08X}/{} "
                "raw18=0x{:08X} raw1C=0x{:08X} raw24=0x{:08X} "
                "nz current=none/{} base=0x{:08X}:{} +1000=0x{:08X}:{} "
                "-1000=0x{:08X}:{} raw24=0x{:08X}:{} "
                "raw_alias=0x{:08X}:{}",
                args.r4, args.r5, args.r6, args.r7, stream_buffer,
                stream_size, raw_base, raw_size, raw_parent24,
                kStreamZeroScanBytes, base_no_offset, no_offset_nz,
                plus_1000, plus_nz, minus_1000, minus_nz, parent24,
                parent24_nz, raw_alias, raw_alias_nz);
            ++g_observer.logged_stream_source_zero_ranges;
          }
        }
      }
      ++g_observer.state_changes;
    }
    return;
  }
  if (symbol == "sub_8219CC30") {
    if (ShouldMirrorSuppressedDeviceState(base, args.r3)) {
      StoreGuestU32(base, args.r3 + kFableDeviceIndexBufferOffset, args.r4);
    }
    g_observer.index_buffer = args.r4;
    if (base && IsLikelyFableObjectAddress(args.r4)) {
      RendererResourceRecord& record = g_observer.resources[args.r4];
      const bool is_new = record.guest_address == 0;
      record.kind = RendererResourceKind::kIndexBuffer;
      record.guest_address = args.r4;
      record.parent_address = args.r3;
      SnapshotGuestResource(record, base);
      if (record.raw_hash == 0 && record.upload_generation != 0) {
        record.raw_hash = HashGuestBytesFnv1a64(
            base, record.raw_buffer_object, record.upload_data.size());
      }
      record.serial = ++g_observer.serial;
      RefreshQueuedDrawBufferResourceSerialLocked(record);
      if (is_new) {
        ++g_observer.resource_creates;
        TraceResourceCreate(record);
      }
    }
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_822224A8") {
    if (ShouldMirrorSuppressedDeviceState(base, args.r3)) {
      StoreGuestU32(base, args.r3 + kFableDeviceVertexDeclarationOffset,
                    args.r4);
    }
    g_observer.vertex_declaration = args.r4;
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_82232510") {
    if (ShouldMirrorSuppressedDeviceState(base, args.r3)) {
      StoreGuestU32(base, args.r3 + kFableDeviceVertexShaderOffset, args.r4);
    }
    g_observer.vertex_shader = args.r4;
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_82BA40F0") {
    // D3DVertexShader_Bind patches/binds shader r3 against declaration r5.
    // It is not a device SetVertexShader call, so do not mirror r5 into a
    // device slot here.
    g_observer.vertex_shader = args.r3;
    if (args.r5 != 0) {
      g_observer.vertex_declaration = args.r5;
      TrackVertexDeclarationLocked(args.r5, args.r3, base);
    }
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r3,
                            args.r5, base);
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_82208BB0") {
    if (ShouldMirrorSuppressedDeviceState(base, args.r3)) {
      StoreGuestU32(base, args.r3 + kFableDevicePixelShaderOffset, args.r4);
    }
    g_observer.pixel_shader = args.r4;
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_8219CD68" || symbol == "sub_822655F0" ||
      symbol == "sub_822AF568" || symbol == "sub_82BAA848" ||
      symbol == "sub_82BAA920" || symbol == "sub_82BAE960" ||
      symbol == "sub_82BAEA88" || symbol == "sub_82BAEC38") {
    if (symbol == "sub_82BAEA88") {
      ObserveSetupScalerLocked(args, base);
    }
    if (symbol == "sub_82BAEC38") {
      ObserveScalerParametersLocked(args, base);
    }
    ++g_observer.state_changes;
    const uint64_t count = ++g_observer.state_flush_callers[args.lr];
    if (RendererDrawTracingEnabled() &&
        (count <= 3 || IsPowerOfTwo(count))) {
      REXLOG_INFO("Refii renderer device state {} lr=0x{:08X} count={}",
                  LumenRendererName(symbol), args.lr, count);
    }
    return;
  }
  if (symbol == "sub_82BA23E8") {
    if (ShouldMirrorSuppressedDeviceState(base, args.r3)) {
      for (uint32_t slot = 0; slot < 4u; ++slot) {
        StoreGuestU32(base, args.r3 + kFableDeviceRenderTargetBaseOffset +
                              slot * sizeof(uint32_t),
                      0);
      }
      StoreGuestU32(base, args.r3 + kFableDeviceDepthStencilOffset, 0);
      StoreGuestU32(base, args.r3 + kFableDeviceVertexDeclarationOffset, 0);
      StoreGuestU32(base, args.r3 + kFableDeviceIndexBufferOffset, 0);
      StoreGuestU32(base, args.r3 + kFableDevicePixelShaderOffset, 0);
      StoreGuestU32(base, args.r3 + kFableDeviceVertexShaderOffset, 0);
      for (uint32_t slot = 0; slot < 16u; ++slot) {
        StoreGuestU32(base, args.r3 + kFableDeviceTextureObjectBaseOffset +
                              slot * sizeof(uint32_t),
                      0);
      }
      for (uint32_t slot = 0; slot < 32u; ++slot) {
        const uint32_t fetch_address =
            args.r3 + kFableDeviceTextureFetchBaseOffset +
            slot * kFableDeviceTextureFetchStride;
        for (uint32_t dword = 0; dword < 6u; ++dword) {
          StoreGuestU32(base, fetch_address + dword * sizeof(uint32_t), 0);
        }
      }
    }
    std::fill(std::begin(g_observer.render_targets),
              std::end(g_observer.render_targets), 0u);
    g_observer.depth_stencil = 0;
    g_observer.vertex_declaration = 0;
    g_observer.vertex_shader = 0;
    g_observer.pixel_shader = 0;
    g_observer.index_buffer = 0;
    std::fill(std::begin(g_observer.textures), std::end(g_observer.textures),
              0u);
    g_observer.fetch_constants.fill(0);
    g_observer.pm4_registers_valid = false;
    g_observer.rb_surface_info = 0;
    std::fill(std::begin(g_observer.rb_color_info),
              std::end(g_observer.rb_color_info), 0u);
    std::fill(std::begin(g_observer.rb_color_info_valid),
              std::end(g_observer.rb_color_info_valid), false);
    g_observer.rb_depth_info = 0;
    g_observer.rb_copy_control = 0;
    g_observer.rb_copy_dest_base = 0;
    g_observer.rb_copy_dest_pitch = 0;
    g_observer.rb_copy_dest_info = 0;
    g_observer.rb_copy_func = 0;
    g_observer.rb_copy_ref = 0;
    g_observer.rb_copy_mask = 0;
    g_observer.rb_copy_surface_slice = 0;
    g_observer.rb_modecontrol = 4u;
    g_observer.last_pm4_resolve_command_write = 0;
    g_observer.last_pm4_resolve_key = 0;
    g_observer.rb_depthcontrol = 0;
    g_observer.rb_color_mask = 0xFFFFu;
    std::fill(std::begin(g_observer.rb_blendcontrol),
              std::end(g_observer.rb_blendcontrol), 0x00010001u);
    g_observer.rb_colorcontrol = 0;
    g_observer.rb_alpha_ref = 0;
    g_observer.rb_stencilrefmask = 0x00FFFF00u;
    g_observer.rb_stencilrefmask_bf = 0x00FFFF00u;
    g_observer.pa_su_sc_mode_cntl = 0;
    ResetObservedRenderStatesToDefaultsLocked();
    g_observer.requested_z_enable = 0;
    g_observer.requested_z_write_enable = 0;
    g_observer.requested_z_enable_valid = false;
    g_observer.requested_z_write_enable_valid = false;
    g_observer.sampler_states.fill(0);
    g_observer.vertex_shader_bool_constants.fill(0);
    g_observer.pixel_shader_bool_constants.fill(0);
    std::fill(std::begin(g_observer.streams), std::end(g_observer.streams),
              RendererStreamBindingSnapshot{});
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821E2FE0") {
    RendererScissorSnapshot rect = DecodeGuestRect(base, args.r5);
    if (rect.valid) {
      rect = ScaleGuestScissorToOutput(rect);
    }
    g_observer.clear_flags = args.r4;
    ++g_observer.clears;
    QueueClearEventLocked(args.r4, args.r6, args.f1, args.r8, rect);
    return;
  }
  if (symbol == "sub_821EF830") {
    RendererScissorSnapshot rect =
        DecodeGuestScissor(static_cast<int32_t>(args.r4),
                           static_cast<int32_t>(args.r5),
                           static_cast<int32_t>(args.r6),
                           static_cast<int32_t>(args.r7));
    if (rect.valid) {
      rect = ScaleGuestScissorToOutput(rect);
    }
    g_observer.clear_flags = args.r10;
    ++g_observer.clears;
    QueueClearEventLocked(args.r10, args.r8, args.f1, args.r9, rect);
    return;
  }
  if (symbol == "sub_8226E180" || symbol == "sub_82B668D0") {
    ++g_observer.render_passes;
    return;
  }
  if (symbol == "sub_8221F2E0") {
    ++g_observer.state_flushes;
    g_observer.last_state_flush_lr = args.lr;
    const uint64_t caller_count = ++g_observer.state_flush_callers[args.lr];
    if (RendererDrawTracingEnabled() &&
        (caller_count <= 3 || IsPowerOfTwo(caller_count))) {
      REXLOG_INFO("Refii renderer state flush caller lr=0x{:08X} count={}",
                  args.lr, caller_count);
    }
    return;
  }
  if (symbol == "sub_8221B010" || symbol == "sub_8221C7D8" ||
      symbol == "sub_8221D1B0" || symbol == "sub_822154B0" ||
      symbol == "sub_8228EF80" || symbol == "sub_822B4008") {
    ++g_observer.state_changes;
    const uint64_t count = ++g_observer.state_flush_callers[args.lr];
    if (RendererDrawTracingEnabled() &&
        (count <= 3 || IsPowerOfTwo(count))) {
      REXLOG_INFO(
          "Refii renderer pending state {} mask=0x{:016X} mask_hi=0x{:08X} "
          "mask_lo=0x{:08X} "
          "lr=0x{:08X} count={}",
          LumenRendererName(symbol), args.r4_u64,
          static_cast<uint32_t>(args.r4_u64 >> 32u), args.r4, args.lr, count);
    }
    return;
  }
  if (symbol == "sub_82BA25C0" || symbol == "sub_822866E0" ||
      symbol == "sub_822868C8") {
    const uint64_t count = ++g_observer.command_writes;
    TraceCommandPath(symbol, args, count);
    return;
  }
  if (symbol == "sub_82BA7898" || symbol == "sub_82BA7928" ||
      symbol == "sub_82BA79D0") {
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821E8D38" || symbol == "sub_821E8EC0") {
    const uint64_t count = ++g_observer.command_submits;
    TraceCommandPath(symbol, args, count);
    return;
  }
  if (symbol == "sub_82196628") {
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_82196750") {
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_822C6BA8") {
    ++g_observer.state_changes;
    QueueExportInvalidateResolveEventLocked(args.r4, args.r5, args.lr);
    return;
  }
  if (symbol == "sub_82205F68") {
    if (IsDrawVerticesUpInnerScratchSubmit(args)) {
      return;
    }
    RefreshBoundSurfacesLocked(base, args.r3);
    g_observer.last_draw = {
        .indexed = false,
        .primitive_type = args.r4,
        .base_vertex = 0,
        .start = 0,
        .primitive_count = args.r5,
    };
    ++g_observer.draws;
    QueueDrawEventLocked(args.r3);
    BeginWrappedDrawStatePatchLocked(args.r3);
    TraceDrawCall(symbol, g_observer.last_draw);
    return;
  }
  if (symbol == "sub_82217DB8") {
    RefreshBoundSurfacesLocked(base, args.r3);
    g_observer.last_draw = {
        .indexed = true,
        .primitive_type = args.r4,
        // Fable 2's indexed scratch wrapper emits PM4 directly from temporary
        // vertex and index streams. r7 is the index count encoded into the draw
        // initiator; replay starts at the beginning of the captured scratch VB.
        .base_vertex = 0,
        .start = 0,
        .primitive_count = args.r7,
    };
    ++g_observer.draws;
    ++g_observer.indexed_draws;
    QueueDrawEventLocked(args.r3);
    BeginWrappedDrawStatePatchLocked(args.r3);
    TraceDrawCall(symbol, g_observer.last_draw);
    return;
  }
  if (symbol == "sub_8221C3E8") {
    RefreshBoundSurfacesLocked(base, args.r3);
    g_observer.last_draw = {
        .indexed = false,
        .primitive_type = args.r4,
        .base_vertex = 0,
        .start = args.r5,
        // This Fable wrapper has already converted to the PM4 vertex count.
        .primitive_count = args.r6,
    };
    ++g_observer.draws;
    QueueDrawEventLocked(args.r3);
    BeginWrappedDrawStatePatchLocked(args.r3);
    TraceDrawCall(symbol, g_observer.last_draw);
    return;
  }
  if (symbol == "sub_8221DFC0") {
    RefreshBoundSurfacesLocked(base, args.r3);
    g_observer.last_draw = {
        .indexed = true,
        .primitive_type = args.r4,
        .base_vertex = args.r5,
        .start = args.r6,
        // This Fable wrapper has already converted to the PM4 index count.
        .primitive_count = args.r7,
    };
    ++g_observer.draws;
    ++g_observer.indexed_draws;
    QueueDrawEventLocked(args.r3);
    BeginWrappedDrawStatePatchLocked(args.r3);
    TraceDrawCall(symbol, g_observer.last_draw);
    return;
  }
  if (symbol == "sub_82265CD8") {
    // D3DDevice_DrawVerticesUP copies caller-provided vertices into the
    // scratch buffer after the wrapped submit path returns. Queue it on exit
    // so snapshots see the copied Fable 2 vertex data rather than zeros.
    return;
  }
}

bool IsCreateHook(std::string_view symbol) {
  return CreatedResourceKind(symbol) != RendererResourceKind::kUnknown;
}

uint32_t CreatedResourceGuestAddress(std::string_view symbol,
                                     const RendererHookArgs& args,
                                     uint32_t result, uint8_t* base) {
  if (symbol == "rex_lhGfx_CreateVertexBuffer" ||
      symbol == "rex_lhGfx_CreateIndexBuffer") {
    return args.r3;
  }
  if (symbol == "sub_821FC8D8" && base && args.r3 != 0) {
    return LoadGuestU32(base, args.r3 + 4);
  }
  if (symbol == "sub_82295380" && base && args.r3 != 0) {
    return LoadGuestU32(base, args.r3 + 4);
  }
  if (symbol == "sub_82B8AB30" && base && args.r3 != 0) {
    return LoadGuestU32(base, args.r3 + 0x14u);
  }

  return result;
}

RendererResourceCounts CountResourcesLocked() {
  RendererResourceCounts counts;
  counts.live_resources = g_observer.resources.size();
  for (const auto& [address, record] : g_observer.resources) {
    (void)address;
    switch (record.kind) {
      case RendererResourceKind::kTexture:
        ++counts.live_textures;
        break;
      case RendererResourceKind::kSurfaceView:
      case RendererResourceKind::kRenderTargetSurface:
      case RendererResourceKind::kDepthSurface:
        ++counts.live_surfaces;
        break;
      case RendererResourceKind::kVertexBuffer:
        ++counts.live_vertex_buffers;
        break;
      case RendererResourceKind::kIndexBuffer:
        ++counts.live_index_buffers;
        break;
      case RendererResourceKind::kVertexShader:
      case RendererResourceKind::kPixelShader:
        ++counts.live_shaders;
        break;
      case RendererResourceKind::kUnknown:
      case RendererResourceKind::kVertexDeclaration:
        break;
    }
  }
  return counts;
}

template <typename T>
uint32_t CountNonZeroDwords(const T& values) {
  return static_cast<uint32_t>(
      std::count_if(values.begin(), values.end(),
                    [](uint32_t value) { return value != 0; }));
}

void TraceResourceCreate(const RendererResourceRecord& record) {
  if (!RendererResourceTracingEnabled()) {
    return;
  }

  if (IsBufferResourceKind(record.kind)) {
    REXLOG_INFO(
        "Refii renderer resource create kind={} guest=0x{:08X} "
        "object=0x{:08X} size={} stride_or_format={} serial={}",
        ResourceKindName(record.kind), record.guest_address,
        record.raw_buffer_object, record.raw_size,
        record.raw_stride_or_format, record.serial);
    return;
  }
  if (IsShaderResourceKind(record.kind)) {
    REXLOG_INFO(
        "Refii renderer resource create kind={} guest=0x{:08X} "
        "bytecode=0x{:08X} size={} hash=0x{:016X} serial={}",
        ResourceKindName(record.kind), record.guest_address,
        record.raw_buffer_object, record.raw_size, record.raw_hash,
        record.serial);
    return;
  }
  if (record.kind == RendererResourceKind::kVertexDeclaration) {
    REXLOG_INFO(
        "Refii renderer resource create kind={} guest=0x{:08X} "
        "elements={} max_stream={} stream_mask=0x{:08X} hash=0x{:016X} "
        "serial={}",
        ResourceKindName(record.kind), record.guest_address,
        record.raw_stride_or_format, record.raw_packed28,
        record.raw_packed32, record.raw_hash, record.serial);
    return;
  }

  REXLOG_INFO(
      "Refii renderer resource create kind={} guest=0x{:08X} parent=0x{:08X} "
      "raw_parent=0x{:08X} packed28=0x{:08X} packed32=0x{:08X} "
      "packed36=0x{:08X} packed40=0x{:08X} packed44=0x{:08X} "
      "packed48=0x{:08X} serial={}",
      ResourceKindName(record.kind), record.guest_address,
      record.parent_address, record.raw_parent24, record.raw_packed28,
      record.raw_packed32, record.raw_packed36, record.raw_packed40,
      record.raw_packed44, record.raw_packed48, record.serial);
}

void TraceResourceDestroy(uint32_t guest_address,
                          const RendererResourceRecord* record) {
  if (!RendererResourceTracingEnabled()) {
    return;
  }

  if (record) {
    REXLOG_INFO(
        "Refii renderer resource destroy kind={} guest=0x{:08X} "
        "resource=0x{:08X}",
        ResourceKindName(record->kind), guest_address, record->guest_address);
    return;
  }

  const uint64_t count = ++g_observer.unknown_destroy_counts[guest_address];
  if (count <= 3 || IsPowerOfTwo(count)) {
    REXLOG_INFO(
        "Refii renderer resource destroy unknown guest=0x{:08X} count={}",
        guest_address, count);
  }
}

void TraceResourceCreateFailed(std::string_view symbol,
                               RendererResourceKind kind) {
  if (!RendererResourceTracingEnabled()) {
    return;
  }

  REXLOG_WARN("Refii renderer resource create failed hook={} kind={}", symbol,
              ResourceKindName(kind));
}

void TrackShaderObjectLocked(RendererResourceKind kind, uint32_t shader_address,
                             uint32_t parent_address, uint8_t* base) {
  if (shader_address == 0 || !IsShaderResourceKind(kind)) {
    return;
  }

  if (RendererResourceRecord* existing =
          FindResourceRecordLocked(shader_address)) {
    if (existing->kind == kind) {
      RendererResourceRecord refreshed = *existing;
      refreshed.parent_address = parent_address;
      if (PopulateShaderRecordFromObject(refreshed, base)) {
        if (refreshed.raw_hash != existing->raw_hash ||
            refreshed.raw_buffer_object != existing->raw_buffer_object ||
            refreshed.raw_size != existing->raw_size ||
            refreshed.raw_stride_or_format != existing->raw_stride_or_format) {
          refreshed.serial = ++g_observer.serial;
          *existing = refreshed;
          g_observer.last_resource = refreshed;
          return;
        }
      }
      if (parent_address != 0 && existing->parent_address != parent_address) {
        existing->parent_address = parent_address;
        existing->serial = ++g_observer.serial;
        g_observer.last_resource = *existing;
      }
      return;
    }
  }

  RendererResourceRecord record;
  record.kind = kind;
  record.guest_address = shader_address;
  record.parent_address = parent_address;
  if (!PopulateShaderRecordFromObject(record, base)) {
    return;
  }

  record.serial = ++g_observer.serial;
  g_observer.resources[record.guest_address] = record;
  g_observer.last_resource = record;
  ++g_observer.resource_creates;
  TraceResourceCreate(record);
}

void UpsertSyntheticShaderResourceLocked(RendererResourceKind kind,
                                         uint32_t shader_address,
                                         uint64_t shader_hash,
                                         uint32_t parent_address) {
  if (shader_address == 0 || shader_hash == 0 ||
      !IsShaderResourceKind(kind)) {
    return;
  }

  if (RendererResourceRecord* existing =
          FindResourceRecordLocked(shader_address)) {
    if (existing->kind == kind && existing->raw_hash == shader_hash) {
      return;
    }
  }

  RendererResourceRecord record;
  record.kind = kind;
  record.guest_address = shader_address;
  record.parent_address = parent_address;
  record.raw_hash = shader_hash;
  record.serial = ++g_observer.serial;
  g_observer.resources[record.guest_address] = record;
  g_observer.last_resource = record;
  ++g_observer.resource_creates;
  TraceResourceCreate(record);
}

void EnsureBoundShaderTrackedLocked(std::string_view symbol,
                                    const RendererHookArgs& args,
                                    uint8_t* base) {
  RendererResourceKind kind = RendererResourceKind::kUnknown;
  if (symbol == "sub_82232510") {
    kind = RendererResourceKind::kVertexShader;
  } else if (symbol == "sub_82208BB0") {
    kind = RendererResourceKind::kPixelShader;
  } else {
    return;
  }

  TrackShaderObjectLocked(kind, args.r4, args.r3, base);
}

void TrackVertexDeclarationLocked(uint32_t declaration_address,
                                  uint32_t parent_address, uint8_t* base) {
  if (!base || declaration_address == 0) {
    return;
  }

  if (RendererResourceRecord* existing =
          FindResourceRecordLocked(declaration_address)) {
    if (existing->kind == RendererResourceKind::kVertexDeclaration) {
      if (existing->raw_hash == 0) {
        SnapshotGuestResource(*existing, base);
      }
      LogSuspiciousVertexDeclarationLocked(declaration_address, *existing, base);
      return;
    }
  }

  RendererResourceRecord record;
  record.kind = RendererResourceKind::kVertexDeclaration;
  record.guest_address = declaration_address;
  record.parent_address = parent_address;
  SnapshotGuestResource(record, base);
  LogSuspiciousVertexDeclarationLocked(declaration_address, record, base);
  if (record.raw_hash == 0 &&
      g_observer.logged_vertex_declaration_failures.insert(declaration_address)
          .second) {
    REXLOG_INFO(
        "Refii renderer vertex declaration 0x{:08X} has no decoded elements; "
        "words -10/+00/+04/+08/+0C/+10/+14/+18/+1C/+20/+24/+28/+2C/+30/+34/"
        "+38/+3C/+40/+44/+48 = "
        "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
        "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
        "{:08X}/{:08X}",
        declaration_address, LoadGuestU32(base, declaration_address - 0x10),
        LoadGuestU32(base, declaration_address + 0x00),
        LoadGuestU32(base, declaration_address + 0x04),
        LoadGuestU32(base, declaration_address + 0x08),
        LoadGuestU32(base, declaration_address + 0x0C),
        LoadGuestU32(base, declaration_address + 0x10),
        LoadGuestU32(base, declaration_address + 0x14),
        LoadGuestU32(base, declaration_address + 0x18),
        LoadGuestU32(base, declaration_address + 0x1C),
        LoadGuestU32(base, declaration_address + 0x20),
        LoadGuestU32(base, declaration_address + 0x24),
        LoadGuestU32(base, declaration_address + 0x28),
        LoadGuestU32(base, declaration_address + 0x2C),
        LoadGuestU32(base, declaration_address + 0x30),
        LoadGuestU32(base, declaration_address + 0x34),
        LoadGuestU32(base, declaration_address + 0x38),
        LoadGuestU32(base, declaration_address + 0x3C),
        LoadGuestU32(base, declaration_address + 0x40),
        LoadGuestU32(base, declaration_address + 0x44),
        LoadGuestU32(base, declaration_address + 0x48));
  }
  if (record.raw_hash == 0) {
    return;
  }
  record.serial = ++g_observer.serial;
  g_observer.resources[record.guest_address] = record;
  g_observer.last_resource = record;
  ++g_observer.resource_creates;
  TraceResourceCreate(record);
}

bool HasPlausibleSurfaceHeader(const RendererResourceRecord& record) {
  if (record.raw_packed36 == 0 ||
      (record.raw_packed28 == 0 && record.raw_packed32 == 0 &&
       record.raw_packed40 == 0 && record.raw_packed48 == 0)) {
    return false;
  }

  const uint32_t utf16_path_words =
      (WordLooksLikeUtf16LePathText(record.raw_parent24) ? 1u : 0u) +
      (WordLooksLikeUtf16LePathText(record.raw_packed28) ? 1u : 0u) +
      (WordLooksLikeUtf16LePathText(record.raw_packed32) ? 1u : 0u);
  if (utf16_path_words >= 2u) {
    return false;
  }
  const uint32_t utf16_descriptor_words =
      (WordLooksLikeUtf16LePathText(record.raw_packed36) ? 1u : 0u) +
      (WordLooksLikeUtf16LePathText(record.raw_packed40) ? 1u : 0u);
  if (utf16_path_words + utf16_descriptor_words >= 2u) {
    return false;
  }

  const uint32_t width = (record.raw_packed36 >> 18u) + 1u;
  const uint32_t height = ((record.raw_packed36 >> 3u) & 0x7FFFu) + 1u;
  if (!IsPlausibleFableSurfaceExtent(width, height)) {
    return false;
  }
  if (record.raw_packed44 == 0 && record.raw_packed48 == 0) {
    return false;
  }

  // Fable frequently reuses transient 0x404D/0x404E scratch objects as small
  // wrapper/list nodes while D3D surface views are being suppressed. Those
  // nodes can accidentally decode as tiny surfaces because +24 contains a
  // small scalar, but they have scalar-looking format/size words rather than
  // usable RT/depth descriptor metadata for Plume.
  const uint64_t surface_pixels = static_cast<uint64_t>(width) * height;
  if (surface_pixels <= 16u && record.raw_packed44 < 0x1000u &&
      record.raw_packed48 < 0x1000u &&
      record.raw_packed40 < 0x1000u) {
    return false;
  }
  if ((width == 1u || height == 1u) && surface_pixels <= 64u &&
      record.raw_packed44 < 0x1000u && record.raw_packed48 < 0x1000u &&
      record.raw_packed40 < 0x1000u) {
    return false;
  }

  if (record.raw_packed48 != 0) {
    const uint64_t max_expected_bytes =
        std::max<uint64_t>(4096u, surface_pixels * 16u);
    if (record.raw_packed48 > max_expected_bytes) {
      return false;
    }
  }

  if (record.raw_packed44 != 0) {
    const uint32_t width_and_msaa = record.raw_packed44 >> 16u;
    const uint32_t multisample_type = width_and_msaa & 0x3u;
    const uint32_t aligned_width = (width_and_msaa & ~0x3u) >> 2u;
    const uint32_t pitch_width = record.raw_packed44 & 0x3FFFu;
    const uint32_t max_expected_pitch =
        std::max(width * 2u, width + 512u);
    if (multisample_type > 3u ||
        (pitch_width != 0 &&
         (pitch_width < width || pitch_width > max_expected_pitch)) ||
        (aligned_width != 0 &&
         (aligned_width < width || aligned_width > max_expected_pitch))) {
      return false;
    }
  }

  return true;
}

bool SnapshotInlineSurfaceDescriptor(RendererResourceRecord& record,
                                     uint8_t* base) {
  if (!base || record.guest_address == 0 ||
      !IsSurfaceResourceKind(record.kind)) {
    return false;
  }

  const auto plausible_extent = [&](uint32_t packed_extent) {
    if (packed_extent == 0) {
      return false;
    }
    const uint32_t width = (packed_extent >> 18u) + 1u;
    const uint32_t height = ((packed_extent >> 3u) & 0x7FFFu) + 1u;
    return IsPlausibleFableSurfaceExtent(width, height);
  };

  const auto adopt = [&](uint32_t pitch_msaa, uint32_t render_target_info,
                         uint32_t depth_stencil_info,
                         uint32_t packed_extent, uint32_t d3d_format,
                         uint32_t byte_size) {
    record.raw_flags = 0;
    record.raw_ref_count = 0;
    record.raw_aux20 = 0;
    record.raw_parent24 = pitch_msaa;
    record.raw_packed28 = render_target_info;
    record.raw_packed32 = depth_stencil_info;
    record.raw_packed36 = packed_extent;
    record.raw_packed40 = d3d_format;
    record.raw_packed44 = pitch_msaa;
    record.raw_packed48 = byte_size;
  };

  // sub_821F5028 builds the temporary render-target descriptors that Fable
  // uses in its Pix/video conversion path. They are not D3DSurface objects:
  // words 6..11 carry pitch/MSAA, RT info, depth info, extent, format, size.
  const uint32_t direct_pitch_msaa =
      LoadGuestU32(base, record.guest_address + 0x18u);
  const uint32_t direct_rt_info =
      LoadGuestU32(base, record.guest_address + 0x1Cu);
  const uint32_t direct_depth_info =
      LoadGuestU32(base, record.guest_address + 0x20u);
  const uint32_t direct_extent =
      LoadGuestU32(base, record.guest_address + 0x24u);
  const uint32_t direct_format =
      LoadGuestU32(base, record.guest_address + 0x28u);
  const uint32_t direct_byte_size =
      LoadGuestU32(base, record.guest_address + 0x2Cu);
  if (plausible_extent(direct_extent) &&
      (direct_rt_info != 0 || direct_depth_info != 0 ||
       direct_format != 0) &&
      (direct_byte_size != 0 || direct_pitch_msaa != 0)) {
    const RendererResourceRecord before_direct = record;
    adopt(direct_pitch_msaa, direct_rt_info, direct_depth_info, direct_extent,
          direct_format, direct_byte_size);
    if (HasPlausibleSurfaceHeader(record)) {
      return true;
    }
    record = before_direct;
  }

  // Some stack-built Fable descriptors are passed through a compact linked
  // value list where each cell stores next,value. Preserve the same surface
  // shape from those value words so temporary video targets don't collapse
  // back to the previously-bound present target.
  const uint32_t pair_flags =
      LoadGuestU32(base, record.guest_address + 0x14u);
  const uint32_t pair_rt_info =
      LoadGuestU32(base, record.guest_address + 0x1Cu);
  const uint32_t pair_extent =
      LoadGuestU32(base, record.guest_address + 0x24u);
  const uint32_t pair_byte_size =
      LoadGuestU32(base, record.guest_address + 0x2Cu);
  const bool linked_cells =
      LoadGuestU32(base, record.guest_address + 0x00u) ==
          record.guest_address + 0x08u &&
      LoadGuestU32(base, record.guest_address + 0x08u) ==
          record.guest_address + 0x10u &&
      LoadGuestU32(base, record.guest_address + 0x10u) ==
          record.guest_address + 0x18u;
  if (linked_cells && pair_flags == 0xFFFF0000u &&
      plausible_extent(pair_extent) &&
      (pair_rt_info != 0 || pair_byte_size != 0)) {
    const RendererResourceRecord before_linked = record;
    adopt(0, pair_rt_info, 0, pair_extent, 0, pair_byte_size);
    if (HasPlausibleSurfaceHeader(record)) {
      return true;
    }
    record = before_linked;
  }

  return false;
}

bool SurfaceShapeMatches(const RendererResourceRecord& lhs,
                         const RendererResourceRecord& rhs) {
  return lhs.kind == rhs.kind && lhs.guest_address == rhs.guest_address &&
         lhs.raw_parent24 == rhs.raw_parent24 &&
         lhs.raw_packed28 == rhs.raw_packed28 &&
         lhs.raw_packed32 == rhs.raw_packed32 &&
         lhs.raw_packed36 == rhs.raw_packed36 &&
         lhs.raw_packed40 == rhs.raw_packed40 &&
         lhs.raw_packed44 == rhs.raw_packed44 &&
         lhs.raw_packed48 == rhs.raw_packed48;
}

void RememberRecentSurfaceShapeLocked(const RendererResourceRecord& record) {
  if (!IsSurfaceResourceKind(record.kind) || record.guest_address == 0 ||
      !HasPlausibleSurfaceHeader(record)) {
    return;
  }

  g_observer.recent_surface_shapes[record.guest_address] = record;
  while (g_observer.recent_surface_shapes.size() > kMaxRecentSurfaceShapes) {
    auto oldest = g_observer.recent_surface_shapes.begin();
    for (auto it = g_observer.recent_surface_shapes.begin();
         it != g_observer.recent_surface_shapes.end(); ++it) {
      if (it->second.serial < oldest->second.serial) {
        oldest = it;
      }
    }
    g_observer.recent_surface_shapes.erase(oldest);
  }
}

bool SnapshotNearbySurfaceHeader(RendererResourceRecord& record, uint8_t* base) {
  if (!base || record.guest_address == 0) {
    return false;
  }

  auto adopt_header = [&](const RendererResourceRecord& candidate,
                          uint32_t candidate_address) {
    // Only lift the Xenos surface descriptor words out of wrapper objects.
    // Fable often reuses the wrapper address for path/audio records later;
    // Plume only needs the packed shape, not the wrapper's object fields.
    record.raw_parent24 = candidate.raw_parent24;
    record.raw_packed28 = candidate.raw_packed28;
    record.raw_packed32 = candidate.raw_packed32;
    record.raw_packed36 = candidate.raw_packed36;
    record.raw_packed40 = candidate.raw_packed40;
    record.raw_packed44 = candidate.raw_packed44;
    record.raw_packed48 = candidate.raw_packed48;
    if (candidate_address != record.guest_address) {
      record.parent_address = candidate_address;
    }
  };

  const auto try_adopt_candidate = [&](uint32_t candidate_address) {
    if (candidate_address == 0 ||
        !IsLikelyFableObjectAddress(candidate_address)) {
      return false;
    }
    if (auto recent_it = g_observer.recent_surface_shapes.find(candidate_address);
        recent_it != g_observer.recent_surface_shapes.end() &&
        IsSurfaceResourceKind(recent_it->second.kind) &&
        HasPlausibleSurfaceHeader(recent_it->second)) {
      adopt_header(recent_it->second, candidate_address);
      return true;
    }
    if (GuestWordsLookLikeInlinePathString(base, candidate_address)) {
      return false;
    }
    RendererResourceRecord candidate = record;
    candidate.guest_address = candidate_address;
    SnapshotGuestResource(candidate, base);
    if (!HasPlausibleSurfaceHeader(candidate)) {
      return false;
    }

    adopt_header(candidate, candidate_address);
    return true;
  };

  constexpr int32_t kSearchOffsets[] = {
      0,      -0x30, 0x30,  -0x60, 0x60,  -0x90, 0x90,
      -0xC0,  0xC0,  -0xF0, 0xF0,  -0x120, 0x120,
      -0x150, 0x150, -0x180, 0x180};
  std::vector<uint32_t> visited_addresses;
  const auto try_adopt_recent_nearby = [&](uint32_t center_address) {
    if (!IsLikelyFableObjectAddress(center_address)) {
      return false;
    }
    for (const int32_t offset : kSearchOffsets) {
      const uint32_t candidate_address =
          static_cast<uint32_t>(center_address + offset);
      if (auto recent_it =
              g_observer.recent_surface_shapes.find(candidate_address);
          recent_it != g_observer.recent_surface_shapes.end() &&
          IsSurfaceResourceKind(recent_it->second.kind) &&
          HasPlausibleSurfaceHeader(recent_it->second)) {
        adopt_header(recent_it->second, candidate_address);
        return true;
      }
    }
    return false;
  };
  const auto try_adopt_nearby = [&](uint32_t center_address) {
    if (!IsLikelyFableObjectAddress(center_address)) {
      return false;
    }
    if (GuestWordsLookLikeInlinePathString(base, center_address)) {
      return false;
    }
    for (const int32_t offset : kSearchOffsets) {
      const uint32_t candidate_address =
          static_cast<uint32_t>(center_address + offset);
      if (try_adopt_candidate(candidate_address)) {
        return true;
      }
    }
    return false;
  };

  // Fable surface views are small wrapper objects: the view constructors at
  // 0x82B98978/0x82B98A58 store the base surface at +0x18 and view slice bits
  // at +0x1C. Some engine objects add another wrapper/list node before that,
  // so walk a bounded pointer graph but only accept a real 48-byte surface
  // header with plausible Fable extent/pitch fields.
  constexpr uint32_t kSurfacePointerOffsets[] = {
      0u,    4u,    8u,    0x10u, 0x14u, 0x18u, 0x1Cu,
      0x20u, 0x24u, 0x28u, 0x2Cu, 0x30u, 0x3Cu};
  const auto try_adopt_graph = [&](auto&& self, uint32_t center_address,
                                   uint32_t depth) -> bool {
    if (!IsLikelyFableObjectAddress(center_address)) {
      return false;
    }
    if (std::find(visited_addresses.begin(), visited_addresses.end(),
                  center_address) != visited_addresses.end()) {
      return false;
    }
    visited_addresses.push_back(center_address);

    const bool inline_path =
        GuestWordsLookLikeInlinePathString(base, center_address);
    if (try_adopt_recent_nearby(center_address)) {
      return true;
    }
    if (!inline_path && try_adopt_nearby(center_address)) {
      return true;
    }
    if (depth == 0) {
      return false;
    }

    constexpr uint32_t kInlinePathPointerOffsets[] = {
        0u,    // Next wrapper/list node.
        0x30u, // Secondary payload pointer after the inline path payload.
    };
    const uint32_t* pointer_offsets =
        inline_path ? kInlinePathPointerOffsets : kSurfacePointerOffsets;
    const size_t pointer_offset_count =
        inline_path ? std::size(kInlinePathPointerOffsets)
                    : std::size(kSurfacePointerOffsets);
    for (size_t i = 0; i < pointer_offset_count; ++i) {
      const uint32_t pointer_offset = pointer_offsets[i];
      const uint32_t candidate_address =
          LoadGuestU32(base, center_address + pointer_offset);
      if (candidate_address == center_address) {
        continue;
      }
      if (self(self, candidate_address, depth - 1u)) {
        return true;
      }
    }
    return false;
  };

  if (try_adopt_graph(try_adopt_graph, record.guest_address, 3)) {
    return true;
  }

  return false;
}

bool TrackBoundSurfaceLocked(RendererResourceKind kind, uint32_t surface_address,
                             uint32_t parent_address, uint8_t* base) {
  if (!base || surface_address == 0) {
    return false;
  }
  if (!IsLikelyFableObjectAddress(surface_address)) {
    if (RendererResourceTracingEnabled() &&
        g_observer.logged_bound_surface_failures.insert(surface_address).second) {
      REXLOG_INFO(
          "Refii renderer ignored implausible bound surface pointer 0x{:08X}",
          surface_address);
    }
    return false;
  }

  RendererResourceRecord record;
  bool is_new = true;
  auto it = FindResourceIteratorLocked(surface_address);
  if (it != g_observer.resources.end()) {
    record = it->second;
    is_new = false;
  }

  record.kind = kind;
  record.guest_address = surface_address;
  if (record.parent_address == 0) {
    record.parent_address = parent_address;
  }
  SnapshotGuestResource(record, base);
  SanitizeImageResourceParent(record);

  if (!HasPlausibleSurfaceHeader(record) &&
      !SnapshotNearbySurfaceHeader(record, base)) {
    if (RendererResourceTracingEnabled() &&
        g_observer.logged_bound_surface_failures.insert(surface_address).second) {
      REXLOG_INFO(
          "Refii renderer bound surface 0x{:08X} has no direct header; "
          "words +00/+04/+08/+0C/+10/+14/+18/+1C/+20/+24/+28/+2C = "
          "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
          "{:08X}/{:08X}/{:08X}/{:08X}",
          surface_address, LoadGuestU32(base, surface_address + 0x00),
          LoadGuestU32(base, surface_address + 0x04),
          LoadGuestU32(base, surface_address + 0x08),
          LoadGuestU32(base, surface_address + 0x0C),
          LoadGuestU32(base, surface_address + 0x10),
          LoadGuestU32(base, surface_address + 0x14),
          LoadGuestU32(base, surface_address + 0x18),
          LoadGuestU32(base, surface_address + 0x1C),
          LoadGuestU32(base, surface_address + 0x20),
          LoadGuestU32(base, surface_address + 0x24),
          LoadGuestU32(base, surface_address + 0x28),
          LoadGuestU32(base, surface_address + 0x2C));
    }
    return false;
  }

  const bool surface_header_is_depth =
      FableSetSurfaceHeaderFormatIsDepth(record.raw_packed40);
  const bool ambiguous_color_depth_format =
      FableSurfaceHeaderFormatIsAmbiguousColorDepth(record.raw_packed40);
  if (kind == RendererResourceKind::kRenderTargetSurface &&
      surface_header_is_depth && !ambiguous_color_depth_format) {
    if (RendererResourceTracingEnabled() &&
        g_observer.logged_bound_surface_failures.insert(surface_address).second) {
      REXLOG_INFO(
          "Refii renderer rejected depth-format surface 0x{:08X} as color RT "
          "fmt=0x{:08X} rt=0x{:08X} depth=0x{:08X}",
          surface_address, record.raw_packed40, record.raw_packed28,
          record.raw_packed32);
    }
    return false;
  }
  if (kind == RendererResourceKind::kDepthSurface &&
      !surface_header_is_depth && !ambiguous_color_depth_format) {
    if (RendererResourceTracingEnabled() &&
        g_observer.logged_bound_surface_failures.insert(surface_address).second) {
      REXLOG_INFO(
          "Refii renderer rejected color-format surface 0x{:08X} as depth "
          "fmt=0x{:08X} rt=0x{:08X} depth=0x{:08X}",
          surface_address, record.raw_packed40, record.raw_packed28,
          record.raw_packed32);
    }
    return false;
  }
  SanitizeImageResourceParent(record);

  const bool shape_changed =
      is_new || !SurfaceShapeMatches(record, it->second);
  if (shape_changed) {
    record.serial = ++g_observer.serial;
  } else {
    record.serial = it->second.serial;
  }
  g_observer.resources[record.guest_address] = record;
  g_observer.last_resource = record;
  if (is_new) {
    ++g_observer.resource_creates;
    TraceResourceCreate(record);
  }
  return true;
}

void EnsureBoundVertexDeclarationTrackedLocked(std::string_view symbol,
                                               const RendererHookArgs& args,
                                               uint8_t* base) {
  if (!base) {
    return;
  }

  if (symbol == "sub_822224A8" && args.r4 != 0) {
    TrackVertexDeclarationLocked(args.r4, args.r3, base);
    return;
  }

  if (symbol == "rex_XGSetVertexDeclaration" && args.r4 != 0) {
    TrackVertexDeclarationLocked(args.r4, args.r3, base);
    return;
  }

  uint32_t declaration_address = 0;
  uint32_t parent_address = args.r3;
  if (symbol == "sub_821E0220") {
    declaration_address = args.r7;
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r5,
                            declaration_address, base);
  } else if (symbol == "sub_821F7650") {
    declaration_address = args.r5;
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r4,
                            declaration_address, base);
  } else if (symbol == "sub_821D3570") {
    declaration_address = args.r5;
    parent_address = args.r3;
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r3,
                            declaration_address, base);
  }
  if (declaration_address == 0) {
    return;
  }

  g_observer.vertex_declaration = declaration_address;
  TrackVertexDeclarationLocked(declaration_address, parent_address, base);
  for (RendererDrawEventSnapshot& draw_event : g_observer.draw_events) {
    if (draw_event.vertex_declaration == 0) {
      draw_event.vertex_declaration = declaration_address;
    }
  }
}

void UpsertScratchBufferResourceLocked(RendererResourceKind kind,
                                       uint32_t address, uint32_t size,
                                       uint32_t stride_or_format,
                                       uint32_t parent_address,
                                       uint32_t upload_source_address) {
  if (address == 0 || size == 0) {
    return;
  }

  RendererResourceRecord& record = g_observer.resources[address];
  const bool is_new = record.guest_address == 0;
  const RendererResourceKind previous_kind = record.kind;
  const uint32_t previous_size = record.raw_size;
  const uint32_t previous_stride_or_format = record.raw_stride_or_format;
  const uint32_t previous_upload_source = record.scratch_upload_source_address;
  const bool previous_scratch = record.scratch;
  record.kind = kind;
  record.guest_address = address;
  record.parent_address = parent_address;
  record.raw_buffer_object = address;
  if (upload_source_address != 0) {
    record.scratch_upload_source_address = upload_source_address;
  } else if (record.scratch_upload_source_address == 0) {
    record.scratch_upload_source_address = address;
  }
  record.raw_size = size;
  record.raw_stride_or_format = stride_or_format;
  const bool shape_changed =
      !previous_scratch || previous_kind != kind || previous_size != size ||
      previous_stride_or_format != stride_or_format ||
      previous_upload_source != record.scratch_upload_source_address;
  bool upload_changed = false;
  if (g_observer.memory_base != nullptr) {
    upload_changed = CopyGuestBufferUploadData(
        record, g_observer.memory_base,
        record.scratch_upload_source_address != 0
            ? record.scratch_upload_source_address
            : address,
        size);
  }
  if (is_new || shape_changed || upload_changed) {
    record.serial = ++g_observer.serial;
  }
  record.scratch = true;
  RefreshQueuedDrawBufferResourceSerialLocked(record);
  g_observer.last_resource = record;
  if (is_new) {
    ++g_observer.resource_creates;
    TraceResourceCreate(record);
  }
}

void UpsertSyntheticScratchBufferResourceLocked(RendererResourceKind kind,
                                                uint32_t address,
                                                const void* data,
                                                uint32_t size,
                                                uint32_t stride_or_format,
                                                uint32_t parent_address) {
  if (address == 0 || !data || size == 0) {
    return;
  }

  RendererResourceRecord& record = g_observer.resources[address];
  const bool is_new = record.guest_address == 0;
  record.kind = kind;
  record.guest_address = address;
  record.parent_address = parent_address;
  record.raw_buffer_object = address;
  record.scratch_upload_source_address = 0;
  record.raw_size = size;
  record.raw_stride_or_format = stride_or_format;
  const bool upload_changed =
      record.upload_data.size() != size ||
      std::memcmp(record.upload_data.data(), data, size) != 0;
  if (upload_changed) {
    record.upload_data.resize(size);
    std::memcpy(record.upload_data.data(), data, size);
    ++record.upload_generation;
    record.serial = ++g_observer.serial;
  } else if (is_new) {
    record.serial = ++g_observer.serial;
  }
  record.scratch = true;
  RefreshQueuedDrawBufferResourceSerialLocked(record);
  g_observer.last_resource = record;
  if (is_new) {
    ++g_observer.resource_creates;
    TraceResourceCreate(record);
  }
}

void TrimScratchBufferResourcesLocked() {
  std::unordered_set<uint32_t> protected_scratch_resources;
  for (const RendererDrawEventSnapshot& event : g_observer.draw_events) {
    if (event.index_buffer != 0) {
      protected_scratch_resources.insert(event.index_buffer);
    }
    for (const RendererStreamBindingSnapshot& stream : event.streams) {
      if (stream.buffer != 0) {
        protected_scratch_resources.insert(stream.buffer);
      }
    }
  }

  size_t scratch_count = 0;
  for (const auto& [address, record] : g_observer.resources) {
    (void)address;
    if (record.scratch) {
      ++scratch_count;
    }
  }
  if (scratch_count <= kMaxScratchBufferResources) {
    return;
  }

  while (scratch_count > kMaxScratchBufferResources) {
    auto oldest = g_observer.resources.end();
    for (auto it = g_observer.resources.begin(); it != g_observer.resources.end();
         ++it) {
      if (!it->second.scratch) {
        continue;
      }
      if (protected_scratch_resources.find(it->first) !=
          protected_scratch_resources.end()) {
        continue;
      }
      if (oldest == g_observer.resources.end() ||
          it->second.serial < oldest->second.serial) {
        oldest = it;
      }
    }
    if (oldest == g_observer.resources.end()) {
      return;
    }
    g_observer.resources.erase(oldest);
    ++g_observer.resource_destroys;
    ++g_observer.serial;
    --scratch_count;
  }
}

void PatchLatestDrawEventScratchBuffersLocked(uint32_t vertex_buffer,
                                              uint32_t vertex_stride,
                                              uint32_t index_buffer) {
  g_observer.streams[0].buffer = vertex_buffer;
  g_observer.streams[0].offset = 0;
  g_observer.streams[0].stride = vertex_stride;
  if (index_buffer != 0) {
    g_observer.index_buffer = index_buffer;
  }

  if (g_observer.draw_events.empty()) {
    return;
  }

  RendererDrawEventSnapshot& event = g_observer.draw_events.back();
  event.streams[0] = g_observer.streams[0];
  if (index_buffer != 0) {
    event.index_buffer = index_buffer;
  }
  event.resource_serial = g_observer.serial;
  RefreshDrawEventResourceSerialFromBindingsLocked(event);
}

void CaptureScratchDrawBuffersLocked(std::string_view symbol,
                                     const RendererHookArgs& args,
                                     uint32_t result, uint8_t* base) {
  if (!base || args.r3 == 0) {
    return;
  }
  g_observer.memory_base = base;

  if (symbol == "sub_82205F68") {
    if (IsDrawVerticesUpInnerScratchSubmit(args)) {
      return;
    }
    const uint32_t vertex_buffer = result != 0
                                       ? result
                                       : LoadGuestU32(base, args.r3 + 0x3488);
    const uint32_t vertex_size = args.r5 * args.r6;
    UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                      vertex_buffer, vertex_size, args.r6,
                                      args.r3);
    PatchLatestDrawEventScratchBuffersLocked(vertex_buffer, args.r6, 0);
    TrimScratchBufferResourcesLocked();
    return;
  }

  if (symbol == "sub_82265CD8") {
    return;
  }

  if (symbol == "sub_82217DB8") {
    const uint32_t vertex_out_pointer =
        IsLikelyGuestDwordAddress(args.r1 + 84u)
            ? LoadGuestU32(base, args.r1 + 84u)
            : 0;
    uint32_t vertex_buffer =
        IsLikelyGuestDwordAddress(vertex_out_pointer)
            ? LoadGuestU32(base, vertex_out_pointer)
            : 0;
    uint32_t index_buffer =
        IsLikelyGuestDwordAddress(args.r10) ? LoadGuestU32(base, args.r10) : 0;
    if (vertex_buffer == 0) {
      vertex_buffer = LoadGuestU32(base, args.r3 + 0x3488);
    }
    if (index_buffer == 0) {
      index_buffer = LoadGuestU32(base, args.r3 + 0x348C);
    }
    uint32_t vertex_size = LoadGuestU32(base, args.r3 + 0x3490) * 4u;
    uint32_t index_size = LoadGuestU32(base, args.r3 + 0x3494) * 4u;
    const uint32_t vertex_stride = args.r9;
    const uint32_t index_stride = (args.r8 & 4u) != 0 ? 4u : 2u;
    if (vertex_size == 0 && args.r6 != 0 && vertex_stride != 0) {
      vertex_size = args.r6 * vertex_stride;
    }
    if (index_size == 0 && args.r7 != 0) {
      index_size = (args.r8 & 4u) != 0 ? args.r7 * 4u
                                       : ((args.r7 + 1u) / 2u) * 4u;
    }
    if (RendererDrawTracingEnabled() &&
        g_observer.logged_indexed_scratch_captures < 8) {
      const uint32_t command_tail = LoadGuestU32(base, args.r3 + 0x3484);
      const uint32_t device_vertex_buffer = LoadGuestU32(base, args.r3 + 0x3488);
      const uint32_t device_index_buffer = LoadGuestU32(base, args.r3 + 0x348C);
      const uint32_t device_vertex_words = LoadGuestU32(base, args.r3 + 0x3490);
      const uint32_t device_index_words = LoadGuestU32(base, args.r3 + 0x3494);
      REXLOG_INFO(
          "Refii renderer indexed scratch capture result=0x{:08X} "
          "dev=0x{:08X} prim={} base={} vtx_count={} idx_count={} "
          "flags=0x{:08X} stride={} idx_out_ptr=0x{:08X}->0x{:08X} "
          "vtx_out_slot=0x{:08X}->0x{:08X}->0x{:08X} "
          "dev_tail=0x{:08X} dev_vb=0x{:08X} dev_ib=0x{:08X} "
          "dev_words={}/{} captured=0x{:08X}/{} 0x{:08X}/{}",
          result, args.r3, args.r4, args.r5, args.r6, args.r7, args.r8,
          args.r9, args.r10,
          IsLikelyGuestDwordAddress(args.r10) ? LoadGuestU32(base, args.r10)
                                              : 0,
          args.r1 + 84u, vertex_out_pointer,
          IsLikelyGuestDwordAddress(vertex_out_pointer)
              ? LoadGuestU32(base, vertex_out_pointer)
              : 0,
          command_tail, device_vertex_buffer, device_index_buffer,
          device_vertex_words, device_index_words, vertex_buffer, vertex_size,
          index_buffer, index_size);
      ++g_observer.logged_indexed_scratch_captures;
    }
    UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                      vertex_buffer, vertex_size,
                                      vertex_stride,
                                      args.r3);
    UpsertScratchBufferResourceLocked(RendererResourceKind::kIndexBuffer,
                                      index_buffer, index_size, index_stride,
                                      args.r3);
    PatchLatestDrawEventScratchBuffersLocked(vertex_buffer, vertex_stride,
                                             index_buffer);
    TrimScratchBufferResourcesLocked();
  }
}

void QueueDrawVerticesUpEventLocked(const RendererHookArgs& args,
                                    uint32_t result, uint8_t* base) {
  if (!base || args.r3 == 0 || args.r5 == 0 || args.r7 == 0) {
    return;
  }

  const uint32_t vertex_buffer = result != 0 ? result : args.r6;
  const uint32_t vertex_stride = args.r7;
  const uint64_t vertex_size64 =
      static_cast<uint64_t>(args.r5) * static_cast<uint64_t>(vertex_stride);
  if (vertex_buffer == 0 || vertex_size64 == 0 ||
      vertex_size64 > std::numeric_limits<uint32_t>::max()) {
    return;
  }

  RefreshBoundSurfacesLocked(base, args.r3);
  g_observer.last_draw = {
      .indexed = false,
      .primitive_type = args.r4,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = args.r5,
  };
  ++g_observer.draws;

  UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                    vertex_buffer,
                                    static_cast<uint32_t>(vertex_size64),
                                    vertex_stride, args.r3);
  QueueDrawEventLocked(args.r3, true);
  PatchLatestDrawEventScratchBuffersLocked(vertex_buffer, vertex_stride, 0);
  if (!g_observer.logged_draw_vertices_up_capture) {
    REXLOG_INFO(
        "Refii renderer captured DrawVerticesUP after Fable copy prim={} "
        "count={} stride={} source=0x{:08X} result=0x{:08X} "
        "stream=0x{:08X} sample={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        args.r4, args.r5, vertex_stride, args.r6, result, vertex_buffer,
        LoadGuestU32(base, vertex_buffer + 0),
        LoadGuestU32(base, vertex_buffer + 4),
        LoadGuestU32(base, vertex_buffer + 8),
        LoadGuestU32(base, vertex_buffer + 12),
        LoadGuestU32(base, vertex_buffer + 16),
        LoadGuestU32(base, vertex_buffer + 20));
    g_observer.logged_draw_vertices_up_capture = true;
  }
  TraceDrawCall("sub_82265CD8", g_observer.last_draw);
  TrimScratchBufferResourcesLocked();
}

bool IsReasonablePrivateRectangleVertexBuffer(uint8_t* base,
                                              uint32_t vertex_buffer) {
  if (!base || vertex_buffer == 0) {
    return false;
  }
  if (!IsLikelyGpuByteBufferRange(vertex_buffer, sizeof(float) * 6u) &&
      !IsLikelyFableObjectAddress(vertex_buffer)) {
    return false;
  }

  for (uint32_t i = 0; i < 6; ++i) {
    const float value = LoadGuestF32(base, vertex_buffer + i * sizeof(float));
    if (!std::isfinite(value) || std::fabs(value) > 32768.0f) {
      return false;
    }
  }
  return true;
}

uint32_t FindPrivateTextureRectangleVertexBuffer(uint8_t* base,
                                                 uint32_t device_pointer) {
  if (!base || !IsLikelyFableObjectAddress(device_pointer)) {
    return 0;
  }

  const uint32_t command_end = LoadGuestU32(base, device_pointer + 48u);
  if (!IsLikelyGpuByteBufferRange(command_end, sizeof(uint32_t))) {
    return 0;
  }

  constexpr uint32_t kPrivateRectangleVertexFetchPacket = 346112u;
  constexpr uint32_t kScanBackBytes = 1024u;
  const uint32_t scan_begin =
      command_end > kScanBackBytes ? command_end - kScanBackBytes : 0u;
  for (uint32_t command = command_end & ~3u; command >= scan_begin + 4u;
       command -= 4u) {
    if (!IsLikelyGpuByteBufferRange(command - 4u, sizeof(uint32_t) * 2u)) {
      break;
    }
    if (LoadGuestU32(base, command - 4u) !=
        kPrivateRectangleVertexFetchPacket) {
      continue;
    }

    const uint32_t encoded_vertex_buffer = LoadGuestU32(base, command);
    const uint32_t vertex_buffer =
        DecodeFableGpuBufferPointer(encoded_vertex_buffer);
    if (IsReasonablePrivateRectangleVertexBuffer(base, vertex_buffer)) {
      return vertex_buffer;
    }
  }
  return 0;
}

bool BuildPrivateTextureRectangleVerticesFromArgs(
    const RendererHookArgs& args, uint8_t* base,
    std::array<float, 6>& vertices) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return false;
  }

  uint32_t left = 0;
  uint32_t top = 0;
  uint32_t right = 0;
  uint32_t bottom = 0;
  if (IsLikelyFableObjectAddress(args.r5)) {
    left = LoadGuestU32(base, args.r5 + 0u);
    top = LoadGuestU32(base, args.r5 + 4u);
    right = LoadGuestU32(base, args.r5 + 8u);
    bottom = LoadGuestU32(base, args.r5 + 12u);
  } else {
    if (!IsLikelyFableObjectAddress(args.r6)) {
      return false;
    }

    const std::array<uint32_t, 6> dwords =
        LoadTextureFetchDwords(base, args.r6 + 28u);
    if (!HasTextureFetchDwords(dwords)) {
      return false;
    }

    rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
    fetch.dword_0 = dwords[0];
    fetch.dword_1 = dwords[1];
    fetch.dword_2 = dwords[2];
    fetch.dword_3 = dwords[3];
    fetch.dword_4 = dwords[4];
    fetch.dword_5 = dwords[5];
    if (fetch.type != rex::graphics::xenos::FetchConstantType::kTexture) {
      return false;
    }

    if (fetch.dimension == rex::graphics::xenos::DataDimension::k1D) {
      right = fetch.size_1d.width + 1u;
      bottom = 1u;
    } else if (fetch.dimension == rex::graphics::xenos::DataDimension::k3D) {
      right = fetch.size_3d.width + 1u;
      bottom = fetch.size_3d.height + 1u;
    } else {
      right = fetch.size_2d.width + 1u;
      bottom = fetch.size_2d.height + 1u;
    }
  }

  right = (right + 7u) & ~7u;
  bottom = (bottom + 7u) & ~7u;
  if (right < left || bottom < top || right > 32768u || bottom > 32768u) {
    return false;
  }

  const float bias =
      (g_observer.render_states[kFableRenderStateSlotPixelCenterOffset] & 1u)
          ? 0.0f
          : 0.5f;
  const float x0 = static_cast<float>(left) - bias;
  const float y0 = static_cast<float>(top) - bias;
  const float x1 = static_cast<float>(right) - bias;
  const float y1 = static_cast<float>(bottom) - bias;

  // Fable primitive 8 feeds three rectangle corners in hardware order:
  // top-left, top-right, bottom-right. The replacement rectangle-list geometry
  // shader reconstructs the missing bottom-left corner to match that shape.
  // Keep the values in Fable's pixel-space form. The translated GUI vertex
  // shader applies the guest matrices and half-pixel rules, so pre-normalizing
  // here distorts Bink/video and intro quad draws before replay.
  vertices = {x0, y0, x1, y0, x1, y1};
  return std::all_of(vertices.begin(), vertices.end(), [](float value) {
    return std::isfinite(value) && std::fabs(value) <= 32768.0f;
  });
}

uint32_t SyntheticPrivateTextureRectangleVertexBufferAddress(
    const RendererHookArgs& args) {
  const uint32_t source = args.r5 != 0 ? args.r5 : args.r6;
  return 0x0F000000u | (source & 0x00FFFFF0u);
}

std::array<FablePrivateTextureRectangleReplayVertex, 3>
BuildPrivateTextureRectangleReplayVertices(
    const std::array<float, 6>& corners, float u0 = 0.0f, float v0 = 0.0f,
    float u1 = 1.0f, float v1 = 1.0f) {
  const float x0 = corners[0];
  const float y0 = corners[1];
  const float x1 = corners[2];
  const float y1 = corners[5];

  const auto make_vertex = [](float x, float y, float u, float v) {
    FablePrivateTextureRectangleReplayVertex vertex;
    vertex.position[0] = x;
    vertex.position[1] = 0.0f;
    vertex.position[2] = y;
    vertex.position[3] = 1.0f;
    vertex.texcoord0[0] = u;
    vertex.texcoord0[1] = v;
    vertex.texcoord0[2] = 0.0f;
    vertex.texcoord0[3] = 1.0f;
    vertex.texcoord1[0] = u;
    vertex.texcoord1[1] = v;
    vertex.texcoord1[2] = 0.0f;
    vertex.texcoord1[3] = 1.0f;
    return vertex;
  };

  return {
      make_vertex(x0, y0, u0, v0),
      make_vertex(x0, y1, u0, v1),
      make_vertex(x1, y0, u1, v0),
  };
}

void ConfigureGuiQuadVertexConstantsLocked(RendererDrawEventSnapshot& event) {
  auto& constants = event.vertex_shader_constants;
  constants.fill(0);

  float viewport_x = 0.0f;
  float viewport_y = 0.0f;
  float viewport_width = kFableNativeViewportWidth;
  float viewport_height = kFableNativeViewportHeight;
  if (event.viewport.valid && event.viewport.width > 0.0f &&
      event.viewport.height > 0.0f) {
    viewport_x = event.viewport.x;
    viewport_y = event.viewport.y;
    viewport_width = event.viewport.width;
    viewport_height = event.viewport.height;
  } else {
    viewport_width = static_cast<float>(
        std::max(1, RendererOutputWidth()) *
        std::max(1, RendererInternalScale()));
    viewport_height = static_cast<float>(
        std::max(1, RendererOutputHeight()) *
        std::max(1, RendererInternalScale()));
  }
  if (!std::isfinite(viewport_width) || viewport_width <= 0.0f) {
    viewport_width = kFableNativeViewportWidth;
  }
  if (!std::isfinite(viewport_height) || viewport_height <= 0.0f) {
    viewport_height = kFableNativeViewportHeight;
  }

  const auto store_constant = [&](uint32_t reg, float x, float y, float z,
                                  float w) {
    const uint32_t dword = reg * 4u;
    if (dword + 3u >= constants.size()) {
      return;
    }
    constants[dword + 0u] = FloatToBits(x);
    constants[dword + 1u] = FloatToBits(y);
    constants[dword + 2u] = FloatToBits(z);
    constants[dword + 3u] = FloatToBits(w);
  };

  const float clip_x_scale = 2.0f / viewport_width;
  const float clip_y_scale = -2.0f / viewport_height;
  const float clip_x_bias = -1.0f - viewport_x * clip_x_scale;
  const float clip_y_bias = 1.0f - viewport_y * clip_y_scale;

  // 0152_vshader_guiquad reconstructs POSITION.x/z into an intermediate
  // target-local vector before applying g_WorldViewProjection. Keep that
  // reconstruction as identity, then apply the orthographic output transform
  // through the WVP constants.
  store_constant(0, clip_x_scale, 0.0f, 0.0f, clip_x_bias);
  store_constant(1, 0.0f, clip_y_scale, 0.0f, clip_y_bias);
  store_constant(2, 0.0f, 0.0f, 0.0f, 0.0f);
  store_constant(3, 0.0f, 0.0f, 0.0f, 1.0f);
  store_constant(113, 1.0f, 0.0f, 0.0f, 0.0f);
  store_constant(114, 0.0f, 1.0f, 0.0f, 0.0f);
  store_constant(115, 0.0f, 0.0f, 0.0f, 0.0f);
}

void QueuePrivateTextureRectangleDrawEventLocked(const RendererHookArgs& args,
                                                 uint8_t* base,
                                                 bool prefer_rect_argument) {
  if (!base || args.r3 == 0) {
    return;
  }

  uint32_t vertex_buffer = 0;
  uint32_t upload_source = 0;
  std::array<float, 6> synthetic_vertices = {};
  const bool has_synthetic_vertices =
      prefer_rect_argument &&
      BuildPrivateTextureRectangleVerticesFromArgs(args, base,
                                                   synthetic_vertices);
  if (has_synthetic_vertices) {
    vertex_buffer = SyntheticPrivateTextureRectangleVertexBufferAddress(args);
  }
  if (prefer_rect_argument &&
      vertex_buffer == 0 &&
      IsReasonablePrivateRectangleVertexBuffer(base, args.r5)) {
    vertex_buffer = args.r5;
    upload_source = args.r5;
  }
  if (vertex_buffer == 0) {
    vertex_buffer = FindPrivateTextureRectangleVertexBuffer(base, args.r3);
  }
  if (vertex_buffer == 0) {
    return;
  }

  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t texture_slot = args.r4 & 7u;
  uint32_t previous_texture = 0;
  std::array<uint32_t, kTextureFetchConstantDwords> previous_fetch = {};
  bool scoped_private_texture = false;
  if (args.r6 != 0 && IsLikelyFableObjectAddress(args.r6) &&
      texture_slot < std::size(g_observer.textures)) {
    const uint32_t fetch_dword = texture_slot * kTextureFetchConstantDwords;
    previous_texture = g_observer.textures[texture_slot];
    if (fetch_dword + previous_fetch.size() <=
        g_observer.fetch_constants.size()) {
      std::copy_n(g_observer.fetch_constants.begin() + fetch_dword,
                  previous_fetch.size(), previous_fetch.begin());
    }
    g_observer.textures[texture_slot] = args.r6;
    StoreTextureObjectFetchDwordsLocked(args.r6, args.r6 + 28u, texture_slot,
                                        base, args.lr, true, false);
    scoped_private_texture = true;
  }

  RefreshBoundSurfacesLocked(base, args.r3);
  g_observer.last_draw = {
      .indexed = false,
      .primitive_type = 8,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = 3,
  };
  ++g_observer.draws;

  if (has_synthetic_vertices) {
    const auto replay_vertices =
        BuildPrivateTextureRectangleReplayVertices(synthetic_vertices);
    UpsertSyntheticScratchBufferResourceLocked(
        RendererResourceKind::kVertexBuffer, vertex_buffer,
        replay_vertices.data(),
        static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleReplayVertex) *
                              replay_vertices.size()),
        static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleReplayVertex)),
        args.r3);
  } else {
    UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                      vertex_buffer, sizeof(float) * 6u,
                                      sizeof(float) * 2u, args.r3,
                                      upload_source);
  }
  g_observer.streams[0] = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride = has_synthetic_vertices
                    ? static_cast<uint32_t>(
                          sizeof(FablePrivateTextureRectangleReplayVertex))
                    : static_cast<uint32_t>(sizeof(float) * 2u),
  };
  RefreshBinkVideoPlaneUploadsLocked("private-texture-rect", base);
  const size_t prior_event_count = g_observer.draw_events.size();
  QueueDrawEventLocked(args.r3, false, false);
  PatchLatestDrawEventScratchBuffersLocked(vertex_buffer,
                                           g_observer.streams[0].stride, 0);
  bool promoted_inner_submit = false;
  if (g_observer.draw_events.size() > prior_event_count) {
    RendererDrawEventSnapshot& event = g_observer.draw_events.back();
    event.private_texture_rectangle = true;
    event.private_texture_slot = texture_slot;
    if (has_synthetic_vertices) {
      event.private_texture_corners = synthetic_vertices;
      event.private_texture_corners_valid = true;
    } else {
      event.private_texture_corners = {
          LoadGuestF32(base, vertex_buffer + 0u),
          LoadGuestF32(base, vertex_buffer + 4u),
          LoadGuestF32(base, vertex_buffer + 8u),
          LoadGuestF32(base, vertex_buffer + 12u),
          LoadGuestF32(base, vertex_buffer + 16u),
          LoadGuestF32(base, vertex_buffer + 20u),
      };
      event.private_texture_corners_valid =
          std::all_of(event.private_texture_corners.begin(),
                      event.private_texture_corners.end(),
                      [](float value) {
                        return std::isfinite(value) &&
                               std::fabs(value) <= 32768.0f;
                      });
    }
    // This root submits its own rectangle-shaped input. Do not let a stale
    // device VS leak into the replay; the synthetic stream layout is paired
    // with the GUI quad vertex shader shape.
    UpsertSyntheticShaderResourceLocked(
        RendererResourceKind::kVertexShader,
        kSyntheticFablePrivateRectVertexShader, kFableGuiQuadVertexShaderHash,
        args.r3);
    event.vertex_shader = kSyntheticFablePrivateRectVertexShader;
    if (event.pixel_shader == 0) {
      UpsertSyntheticShaderResourceLocked(
          RendererResourceKind::kPixelShader,
          kSyntheticFablePrivateRectPixelShader, kFableBinkMoviePixelShaderHash,
          args.r3);
      event.pixel_shader = kSyntheticFablePrivateRectPixelShader;
    }
    event.resource_serial = g_observer.serial;
    if (event.vertex_shader == kSyntheticFablePrivateRectVertexShader &&
        CountNonZeroDwords(event.vertex_shader_constants) == 0) {
      ConfigureGuiQuadVertexConstantsLocked(event);
      event.device_registers_valid = false;
    }
    if (args.r7 != 0 && IsLikelyFableObjectAddress(args.r7)) {
      event.private_texture_source_x = LoadGuestU32(base, args.r7 + 0u);
      event.private_texture_source_y = LoadGuestU32(base, args.r7 + 4u);
    }
    const uint64_t private_texture_detail_key =
        (static_cast<uint64_t>(args.lr) << 32u) ^
        (static_cast<uint64_t>(args.r6) << 1u) ^
        (static_cast<uint64_t>(event.render_targets[0]) << 17u) ^
        (static_cast<uint64_t>(event.pixel_shader) << 3u) ^
        texture_slot;
    const bool log_private_texture_detail =
        g_observer.logged_private_texture_rectangle_detail_keys.size() < 96u &&
        (RendererDrawTracingEnabled() || g_observer.pix_decode_frames != 0) &&
        g_observer.logged_private_texture_rectangle_detail_keys
            .insert(private_texture_detail_key)
            .second;
    if (log_private_texture_detail) {
      const std::array<uint32_t, kTextureFetchConstantDwords> source_fetch =
          IsLikelyFableObjectAddress(args.r6)
              ? LoadTextureFetchDwords(base, args.r6 + 28u)
              : std::array<uint32_t, kTextureFetchConstantDwords>{};
      const TextureFetchSnapshotInfo source_fetch_info =
          GetTextureFetchSnapshotInfo(source_fetch);
      rex::graphics::xenos::xe_gpu_texture_fetch_t source_fetch_shape = {};
      source_fetch_shape.dword_0 = source_fetch[0];
      source_fetch_shape.dword_1 = source_fetch[1];
      source_fetch_shape.dword_2 = source_fetch[2];
      source_fetch_shape.dword_3 = source_fetch[3];
      source_fetch_shape.dword_4 = source_fetch[4];
      source_fetch_shape.dword_5 = source_fetch[5];
      REXLOG_INFO(
          "Refii renderer private texture rect detail #{} lr=0x{:08X} "
          "flags=0x{:08X} slot={} src=0x{:08X} fetch_base=0x{:08X} "
          "fetch_size={} fetch_fmt={} fetch_reason={} r5=0x{:08X} "
          "r7=0x{:08X} r8=0x{:08X} rt={:08X}/{:08X}/{:08X}/{:08X} "
          "depth=0x{:08X} vs=0x{:08X} ps=0x{:08X} decl=0x{:08X} "
          "vb=0x{:08X} rect={:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f}",
          ++g_observer.logged_private_texture_rectangle_details, args.lr,
          args.r4, texture_slot, args.r6, source_fetch_info.base_address,
          source_fetch_info.size, static_cast<uint32_t>(source_fetch_shape.format),
          source_fetch_info.failure_reason ? source_fetch_info.failure_reason
                                           : "ok",
          args.r5, args.r7, args.r8, event.render_targets[0],
          event.render_targets[1], event.render_targets[2],
          event.render_targets[3], event.depth_stencil, event.vertex_shader,
          event.pixel_shader, event.vertex_declaration, vertex_buffer,
          event.private_texture_corners[0], event.private_texture_corners[1],
          event.private_texture_corners[2], event.private_texture_corners[3],
          event.private_texture_corners[4], event.private_texture_corners[5]);
    }
    promoted_inner_submit =
        PromotePreviousPrivateTextureRectangleSubmitLocked(prior_event_count,
                                                           event);
    if (promoted_inner_submit) {
      g_observer.draw_events.pop_back();
    }
  }
  if (scoped_private_texture) {
    const uint32_t fetch_dword = texture_slot * kTextureFetchConstantDwords;
    g_observer.textures[texture_slot] = previous_texture;
    if (fetch_dword + previous_fetch.size() <=
        g_observer.fetch_constants.size()) {
      std::copy_n(previous_fetch.begin(), previous_fetch.size(),
                  g_observer.fetch_constants.begin() + fetch_dword);
    }
  }
  if (!g_observer.logged_private_texture_rectangle_capture) {
    const float sample0 =
        has_synthetic_vertices ? synthetic_vertices[0]
                               : LoadGuestF32(base, vertex_buffer + 0);
    const float sample1 =
        has_synthetic_vertices ? synthetic_vertices[1]
                               : LoadGuestF32(base, vertex_buffer + 4);
    const float sample2 =
        has_synthetic_vertices ? synthetic_vertices[2]
                               : LoadGuestF32(base, vertex_buffer + 8);
    const float sample3 =
        has_synthetic_vertices ? synthetic_vertices[3]
                               : LoadGuestF32(base, vertex_buffer + 12);
    const float sample4 =
        has_synthetic_vertices ? synthetic_vertices[4]
                               : LoadGuestF32(base, vertex_buffer + 16);
    const float sample5 =
        has_synthetic_vertices ? synthetic_vertices[5]
                               : LoadGuestF32(base, vertex_buffer + 20);
    REXLOG_INFO(
        "Refii renderer captured private texture rectangle draw "
        "device=0x{:08X} flags=0x{:08X} slot={} source=0x{:08X} vb=0x{:08X} "
        "sample={:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f}",
        args.r3, args.r4, texture_slot, args.r6, vertex_buffer, sample0,
        sample1, sample2, sample3, sample4, sample5);
    g_observer.logged_private_texture_rectangle_capture = true;
  }
  TraceDrawCall("sub_82206888", g_observer.last_draw);
  TrimScratchBufferResourcesLocked();
}

bool BuildPixRenderVideoFrameVerticesFromArgs(
    const RendererHookArgs& args, uint8_t* base,
    std::array<float, 6>& vertices) {
  if (!base || !IsLikelyGuestDwordAddress(args.r9) ||
      !IsLikelyGuestDwordAddress(args.r10)) {
    return false;
  }

  const float width = LoadGuestF32(base, args.r9 + 0u);
  const float height = LoadGuestF32(base, args.r9 + 4u);
  const float left = LoadGuestF32(base, args.r10 + 0u);
  const float top = LoadGuestF32(base, args.r10 + 4u);
  if (!std::isfinite(width) || !std::isfinite(height) ||
      !std::isfinite(left) || !std::isfinite(top) || width <= 0.0f ||
      height <= 0.0f || width > 32768.0f || height > 32768.0f ||
      std::fabs(left) > 32768.0f || std::fabs(top) > 32768.0f) {
    return false;
  }

  const float x0 = left - 0.5f;
  const float y0 = top - 0.5f;
  const float x1 = left + width - 0.5f;
  const float y1 = top + height - 0.5f;
  vertices = {x0, y0, x1, y0, x1, y1};
  return std::all_of(vertices.begin(), vertices.end(), [](float value) {
    return std::isfinite(value) && std::fabs(value) <= 32768.0f;
  });
}

void ConfigurePixRenderVideoFrameVertexConstantsLocked(
    RendererDrawEventSnapshot& event) {
  ConfigureGuiQuadVertexConstantsLocked(event);
}

void ConfigurePixRenderVideoFrameTextureCoordinatesLocked(
    const RendererHookArgs& args, uint8_t* base,
    const std::array<float, 6>& vertices, RendererDrawEventSnapshot& event) {
  if (!base || !IsLikelyGuestDwordAddress(args.r1 + 84u)) {
    return;
  }

  // PixRenderVideoFrame receives the decoded-frame extent in the later PPC
  // stack arguments. The caller stores the pointer at SP+84 before branching
  // to sub_82BA83C0; IDA shows the native path using it as a28.
  const uint32_t extent_pointer = LoadGuestU32(base, args.r1 + 84u);
  if (!IsLikelyGuestDwordAddress(extent_pointer)) {
    return;
  }

  const float decoded_width = LoadGuestF32(base, extent_pointer + 0u);
  const float decoded_height = LoadGuestF32(base, extent_pointer + 4u);
  const float output_width = std::fabs(vertices[2] - vertices[0]);
  const float output_height = std::fabs(vertices[5] - vertices[1]);
  if (!std::isfinite(decoded_width) || !std::isfinite(decoded_height) ||
      !std::isfinite(output_width) || !std::isfinite(output_height) ||
      decoded_width <= 0.0f || decoded_height <= 0.0f ||
      output_width <= 0.0f || output_height <= 0.0f) {
    return;
  }

  const float u1 = output_width / (decoded_width * 4.0f);
  const float v1 = output_height / (decoded_height * 4.0f);
  if (!std::isfinite(u1) || !std::isfinite(v1) || u1 <= 0.0f ||
      v1 <= 0.0f || u1 > 4.0f || v1 > 4.0f) {
    return;
  }

  event.private_texture_uv_override = true;
  event.private_texture_u0 = 0.0f;
  event.private_texture_v0 = 0.0f;
  event.private_texture_u1 = u1;
  event.private_texture_v1 = v1;
  ++g_observer.pix_render_uv_overrides;
  g_observer.last_pix_output_width = output_width;
  g_observer.last_pix_output_height = output_height;
  g_observer.last_pix_decoded_width = decoded_width;
  g_observer.last_pix_decoded_height = decoded_height;
  g_observer.last_pix_u1 = u1;
  g_observer.last_pix_v1 = v1;
}

uint32_t LoadHookStackU32(uint8_t* base, const RendererHookArgs& args,
                          uint32_t offset) {
  const uint32_t address = args.r1 + offset;
  return base && IsLikelyGuestDwordAddress(address) ? LoadGuestU32(base, address)
                                                    : 0u;
}

bool IsNearHookStackAddress(const RendererHookArgs& args, uint32_t address) {
  if (args.r1 == 0 || address == 0) {
    return false;
  }

  constexpr uint32_t kStackScratchWindow = 0x4000u;
  const uint32_t low =
      args.r1 > kStackScratchWindow ? args.r1 - kStackScratchWindow : 0u;
  const uint32_t high =
      args.r1 <= UINT32_MAX - kStackScratchWindow ? args.r1 + kStackScratchWindow
                                                  : UINT32_MAX;
  return address >= low && address <= high;
}

void CaptureRootTextureHeaderLocked(std::string_view symbol,
                                    const RendererHookArgs& args,
                                    uint8_t* base,
                                    uint32_t texture_header,
                                    std::string_view source_name) {
  if (!base || texture_header == 0 ||
      !IsLikelyGuestDwordAddress(texture_header) ||
      !IsLikelyGuestDwordAddress(texture_header + 48u)) {
    return;
  }

  constexpr uint32_t kTextureFetchOffset = 28u;
  const uint32_t fetch_address = texture_header + kTextureFetchOffset;
  const std::array<uint32_t, 6> dwords =
      LoadTextureFetchDwords(base, fetch_address);
  if (!HasTextureFetchDwords(dwords)) {
    return;
  }

  UpsertTextureResourceFromFetchDwordsLocked(dwords, base);

  const bool stack_scratch = IsNearHookStackAddress(args, texture_header);
  bool tracked_object = false;
  if (!stack_scratch && IsLikelyFableObjectAddress(texture_header)) {
    StoreTextureObjectFetchDwordsLocked(texture_header, fetch_address, 32u,
                                        base, args.lr, true, false);
    tracked_object = true;
  }

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_inline_texture_descriptor_builds < 16u) {
    const TextureFetchSnapshotInfo fetch_info =
        GetTextureFetchSnapshotInfo(dwords);
    REXLOG_INFO(
        "Refii renderer root texture header #{} symbol={} source={} "
        "header=0x{:08X} stack={} object={} base=0x{:08X} size={} "
        "dwords={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} lr=0x{:08X}",
        g_observer.logged_inline_texture_descriptor_builds + 1u, symbol,
        source_name, texture_header, stack_scratch ? 1 : 0,
        tracked_object ? 1 : 0, fetch_info.base_address, fetch_info.size,
        dwords[0], dwords[1], dwords[2], dwords[3], dwords[4], dwords[5],
        args.lr);
    ++g_observer.logged_inline_texture_descriptor_builds;
  }
}

void QueuePixRenderVideoFrameDrawEventLocked(const RendererHookArgs& args,
                                             uint8_t* base) {
  ++g_observer.pix_render_hooks;
  g_observer.last_pix_packet = args.r8;

  if (!base || args.r3 == 0 || !IsLikelyGuestDwordAddress(args.r8 + 28u)) {
    ++g_observer.pix_render_bad_packets;
    return;
  }

  const std::array<uint32_t, 6> pix_fetch_dwords =
      LoadTextureFetchDwords(base, args.r8 + 28u);
  const TextureFetchSnapshotInfo pix_fetch_info =
      GetTextureFetchSnapshotInfo(pix_fetch_dwords);
  g_observer.last_pix_fetch_base = pix_fetch_info.base_address;
  g_observer.last_pix_fetch_size = pix_fetch_info.size;
  rex::graphics::xenos::xe_gpu_texture_fetch_t pix_fetch = {};
  pix_fetch.dword_0 = pix_fetch_dwords[0];
  pix_fetch.dword_1 = pix_fetch_dwords[1];
  pix_fetch.dword_2 = pix_fetch_dwords[2];
  pix_fetch.dword_3 = pix_fetch_dwords[3];
  pix_fetch.dword_4 = pix_fetch_dwords[4];
  pix_fetch.dword_5 = pix_fetch_dwords[5];
  g_observer.last_pix_fetch_format =
      static_cast<uint32_t>(pix_fetch.format);
  if (!HasTextureFetchDwords(pix_fetch_dwords) ||
      pix_fetch_info.base_address == 0 || pix_fetch_info.size == 0) {
    ++g_observer.pix_render_bad_packets;
    return;
  }
  const uint32_t pix_texture_handle = pix_fetch_info.base_address;
  g_observer.last_pix_texture = pix_texture_handle;

  std::array<float, 6> synthetic_vertices = {};
  if (!BuildPixRenderVideoFrameVerticesFromArgs(args, base,
                                                synthetic_vertices)) {
    ++g_observer.pix_render_bad_vertices;
    return;
  }

  constexpr uint32_t kTextureSlot = 0;
  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t vertex_buffer = 0x0F000000u | (args.r8 & 0x00FFFFF0u);

  uint32_t previous_texture = g_observer.textures[kTextureSlot];
  std::array<uint32_t, kTextureFetchConstantDwords> previous_fetch = {};
  std::copy_n(g_observer.fetch_constants.begin(),
              previous_fetch.size(), previous_fetch.begin());
  const RendererStreamBindingSnapshot previous_stream0 = g_observer.streams[0];
  g_observer.textures[kTextureSlot] = pix_texture_handle;
  // PixRenderVideoFrame consumes a dynamic texture object whose backing memory
  // is updated by the Bink/Pix decode path. Key it by the fetch base rather
  // than the transient stack packet so the draw samples the decoded frame
  // resource directly.
  StoreTextureObjectFetchDwordsLocked(pix_texture_handle, args.r8 + 28u,
                                      kTextureSlot, base, args.lr, true,
                                      false);

  RefreshBoundSurfacesLocked(base, args.r3);
  g_observer.last_draw = {
      .indexed = false,
      .primitive_type = 8,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = 3,
  };
  ++g_observer.draws;

  const auto replay_vertices = BuildPrivateTextureRectangleReplayVertices(
      synthetic_vertices);
  UpsertSyntheticScratchBufferResourceLocked(
      RendererResourceKind::kVertexBuffer, vertex_buffer, replay_vertices.data(),
      static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleReplayVertex) *
                            replay_vertices.size()),
      static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleReplayVertex)),
      args.r3);
  g_observer.streams[0] = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride =
          static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleReplayVertex)),
  };
  QueueDrawEventLocked(args.r3, false, false);
  PatchLatestDrawEventScratchBuffersLocked(vertex_buffer,
                                           g_observer.streams[0].stride, 0);
  if (!g_observer.draw_events.empty()) {
    RendererDrawEventSnapshot& event = g_observer.draw_events.back();
    event.private_texture_rectangle = true;
    event.private_texture_slot = kTextureSlot;
    event.private_texture_corners = synthetic_vertices;
    event.private_texture_corners_valid = true;
    event.device_registers_valid = false;
    ConfigurePixRenderVideoFrameVertexConstantsLocked(event);
    ConfigurePixRenderVideoFrameTextureCoordinatesLocked(
        args, base, synthetic_vertices, event);
    event.render_states[kFableRenderStateSlotColorWriteEnable] = 0xFu;
    event.render_states[kFableRenderStateSlotAlphaBlendEnable] = 0u;
    event.render_states[kFableRenderStateSlotSrcBlend] = 1u;
    event.render_states[kFableRenderStateSlotDestBlend] = 0u;
    event.render_states[kFableRenderStateSlotAlphaTestEnable] = 0u;
    event.render_states[kFableRenderStateSlotZEnable] = 0u;
    event.render_states[kFableRenderStateSlotZWriteEnable] = 0u;
    event.render_states[kFableRenderStateSlotHiZWriteEnable] = 0u;
    event.render_states[kFableRenderStateSlotStencilEnable] = 0u;
    event.render_states[kFableRenderStateSlotSeparateAlphaBlendEnable] = 0u;
    UpsertSyntheticShaderResourceLocked(
        RendererResourceKind::kVertexShader,
        kSyntheticFablePrivateRectVertexShader, kFableGuiQuadVertexShaderHash,
        args.r3);
    UpsertSyntheticShaderResourceLocked(
        RendererResourceKind::kPixelShader,
        kSyntheticFablePrivateRectPixelShader, kFableBinkMoviePixelShaderHash,
        args.r3);
    event.vertex_shader = kSyntheticFablePrivateRectVertexShader;
    event.pixel_shader = kSyntheticFablePrivateRectPixelShader;
    event.resource_serial = g_observer.serial;
    ++g_observer.pix_render_events;
  }

  g_observer.textures[kTextureSlot] = previous_texture;
  std::copy_n(previous_fetch.begin(), previous_fetch.size(),
              g_observer.fetch_constants.begin());
  g_observer.streams[0] = previous_stream0;
  TraceDrawCall("sub_82BA83C0", g_observer.last_draw);
  TrimScratchBufferResourcesLocked();
}

void RefreshScratchResourceUploadLocked(RendererResourceRecord& record) {
  if (g_observer.memory_base == nullptr) {
    return;
  }

  if (!record.scratch || !IsBufferResourceKind(record.kind) ||
      record.raw_buffer_object == 0 || record.raw_size == 0) {
    return;
  }

  const std::vector<uint8_t> previous_upload = record.upload_data;
  const uint64_t previous_generation = record.upload_generation;
  CopyGuestBufferUploadData(record, g_observer.memory_base,
                            record.scratch_upload_source_address != 0
                                ? record.scratch_upload_source_address
                                : record.raw_buffer_object,
                            record.raw_size);
  if (record.upload_data == previous_upload) {
    record.upload_generation = previous_generation;
    return;
  }

  record.serial = ++g_observer.serial;
  RefreshQueuedDrawBufferResourceSerialLocked(record);
  g_observer.last_resource = record;
}

void RefreshPendingDrawScratchResourceUploadsLocked() {
  std::unordered_set<uint32_t> referenced_buffers;
  for (const RendererDrawEventSnapshot& event : g_observer.draw_events) {
    if (event.index_buffer != 0) {
      referenced_buffers.insert(event.index_buffer);
    }
    for (const RendererStreamBindingSnapshot& stream : event.streams) {
      if (stream.buffer != 0) {
        referenced_buffers.insert(stream.buffer);
      }
    }
  }

  for (uint32_t buffer : referenced_buffers) {
    auto exact_record = g_observer.resources.find(buffer);
    RendererResourceRecord* record =
        exact_record != g_observer.resources.end()
            ? &exact_record->second
            : FindResourceRecordLocked(buffer);
    if (record) {
      const uint64_t prior_serial = record->serial;
      RefreshScratchResourceUploadLocked(*record);
      if (record->serial != prior_serial) {
        for (RendererDrawEventSnapshot& event : g_observer.draw_events) {
          bool references_buffer = event.index_buffer == buffer;
          for (const RendererStreamBindingSnapshot& stream : event.streams) {
            references_buffer = references_buffer || stream.buffer == buffer;
          }
          if (references_buffer) {
            event.resource_serial =
                std::max(event.resource_serial, record->serial);
          }
        }
      }
    }
  }
}

void SanitizePendingDrawEventsLocked() {
  RefreshPendingDrawScratchResourceUploadsLocked();
  for (RendererDrawEventSnapshot& event : g_observer.draw_events) {
    SanitizeEventSurfacesLocked(event);
  }
}

bool PresentedDrawEventHasTarget(const RendererDrawEventSnapshot& event) {
  if (event.depth_stencil != 0) {
    return true;
  }
  for (uint32_t render_target : event.render_targets) {
    if (render_target != 0) {
      return true;
    }
  }
  return false;
}

bool PresentedDrawEventHasStreamBinding(const RendererDrawEventSnapshot& event) {
  for (const RendererStreamBindingSnapshot& stream : event.streams) {
    if (stream.buffer != 0 && stream.stride != 0) {
      return true;
    }
  }
  return false;
}

uint32_t CountPresentedReplayCandidateDrawEvents(
    const std::vector<RendererDrawEventSnapshot>& events) {
  uint32_t count = 0;
  for (const RendererDrawEventSnapshot& event : events) {
    if (event.clear) {
      if (event.clear_flags != 0 && PresentedDrawEventHasTarget(event)) {
        ++count;
      }
      continue;
    }
    if (event.draw.primitive_count == 0 || event.vertex_declaration == 0 ||
        event.vertex_shader == 0 || event.pixel_shader == 0 ||
        !PresentedDrawEventHasTarget(event) ||
        !PresentedDrawEventHasStreamBinding(event)) {
      continue;
    }
    if (event.draw.indexed && event.index_buffer == 0) {
      continue;
    }
    ++count;
  }
  return count;
}

bool IsResolveCopyEvent(const RendererDrawEventSnapshot& event) {
  constexpr uint32_t kXenosEdramModeCopy = 6u;
  return event.pm4_registers_valid &&
         (event.rb_modecontrol & 0x7u) == kXenosEdramModeCopy &&
         event.rb_copy_dest_base != 0;
}

bool PresentedEventsContainResolveCopy(
    const std::vector<RendererDrawEventSnapshot>& events,
    const RendererDrawEventSnapshot& resolve_event) {
  const uint64_t resolve_key = Pm4ResolveCopyKeyLocked(resolve_event);
  for (const RendererDrawEventSnapshot& event : events) {
    if (IsResolveCopyEvent(event) &&
        Pm4ResolveCopyKeyLocked(event) == resolve_key) {
      return true;
    }
  }
  return false;
}

void MergePendingResolveCopiesIntoPresentedFrameLocked() {
  for (const RendererDrawEventSnapshot& event : g_observer.draw_events) {
    if (!IsResolveCopyEvent(event) ||
        PresentedEventsContainResolveCopy(g_observer.presented_draw_events,
                                          event)) {
      continue;
    }
    g_observer.presented_draw_events.push_back(event);
  }
}

void CapturePresentedDrawEventsLocked() {
  SanitizePendingDrawEventsLocked();
  if (g_observer.draw_events.empty()) {
    return;
  }

  const bool has_unconsumed_presented_events =
      g_observer.presented_draw_event_present != 0 &&
      g_observer.presented_draw_event_present !=
          g_observer.consumed_draw_event_present &&
      !g_observer.presented_draw_events.empty();
  if (has_unconsumed_presented_events) {
    const uint32_t existing_candidates =
        CountPresentedReplayCandidateDrawEvents(
            g_observer.presented_draw_events);
    const uint32_t pending_candidates =
        CountPresentedReplayCandidateDrawEvents(g_observer.draw_events);
    if (existing_candidates > pending_candidates &&
        pending_candidates <= 1u) {
      // The full Fable frame is usually visible at EndScene, while the lower
      // swap hook may only contain the final present rectangle. Keep the richer
      // frame batch for Plume instead of replacing it with that thin packet,
      // but preserve late resolve/export packets so Plume can map the final
      // display base back to the source render target.
      MergePendingResolveCopiesIntoPresentedFrameLocked();
      g_observer.presented_draw_event_present = g_observer.presents;
      g_observer.draw_events.clear();
      return;
    }
  }

  g_observer.presented_draw_events = g_observer.draw_events;
  g_observer.presented_draw_event_present = g_observer.presents;
  g_observer.draw_events.clear();
}

}  // namespace

void ObserveRendererHookEnter(const char* symbol, RendererHookKind kind,
                              const RendererHookArgs& args, uint8_t* base) {
  const std::string_view symbol_view(symbol);
  if (kind == RendererHookKind::kDraw) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    if (IsPresentHook(symbol_view)) {
      ++g_observer.presents;
      ObserveDisplaySwapSurfaceLocked(symbol_view, args, base);
      if (RendererDrawTracingEnabled() &&
          (g_observer.presents <= 3 || IsPowerOfTwo(g_observer.presents))) {
        REXLOG_INFO("Refii renderer present observed count={}",
                    g_observer.presents);
      }
    } else {
      ObserveRendererStateOrDrawLocked(symbol_view, args, base);
    }
    return;
  }

  if (kind != RendererHookKind::kResource) {
    return;
  }

  if (IsLockHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ++g_observer.resource_locks;
    return;
  }

  if (IsUnlockHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ++g_observer.resource_unlocks;
    return;
  }
}

void ObserveRendererStateDispatch(const RendererHookArgs& args, uint8_t* base) {
  if (!base || args.r3 == 0) {
    return;
  }

  const uint8_t dispatch_kind = LoadGuestU8(base, args.r3 + 21);
  uint32_t slot_bias = 0;
  if (dispatch_kind == 1) {
    slot_bias = 64;
  } else if (dispatch_kind == 2) {
    slot_bias = 468;
  } else {
    return;
  }

  const uint32_t callback_table = LoadGuestU32(base, 0x834970E8u);
  const uint32_t callback_offset = LoadGuestU32(base, args.r3 + 16);
  const uint32_t callback_slot = callback_table + callback_offset + slot_bias;
  const uint32_t callback_target =
      callback_slot != 0 ? LoadGuestU32(base, callback_slot) : 0;
  const uint32_t pending_value = LoadGuestU32(base, args.r3 + 4);
  const uint32_t dispatch_value = pending_value;
  const uint32_t sampler_slot =
      dispatch_kind == 2 ? LoadGuestU8(base, args.r3 + 22) : UINT32_MAX;

  std::lock_guard<std::mutex> lock(g_observer.mutex);
  RememberGuestMemoryBaseLocked(base);
  ++g_observer.state_dispatches;
  bool applied_state = false;
  if (dispatch_kind == 1) {
    const uint32_t render_state_slot =
        RenderStateSlotForAddress(callback_target);
    if (render_state_slot < g_observer.render_states.size()) {
      g_observer.render_states[render_state_slot] = dispatch_value;
      RememberRequestedDepthRenderStateLocked(render_state_slot,
                                             dispatch_value);
      applied_state = true;
    }
  } else if (dispatch_kind == 2 && sampler_slot != UINT32_MAX) {
    const uint32_t sampler_state_slot =
        SamplerStateSlotForAddress(callback_target);
    if (sampler_state_slot != UINT32_MAX) {
      StoreSamplerStateLocked(sampler_slot, sampler_state_slot, dispatch_value);
      applied_state = true;
    }
  }
  ++g_observer.state_changes;
  const uint64_t count = ++g_observer.state_dispatch_targets[callback_target];
  if (RendererDrawTracingEnabled() && callback_target != 0 &&
      (count <= 3 || IsPowerOfTwo(count))) {
    const char* callback_name = LumenRendererAddressName(callback_target);
    REXLOG_INFO(
        "Refii renderer state dispatch kind={} record=0x{:08X} "
        "table=0x{:08X} offset=0x{:08X} slot=0x{:08X} target=0x{:08X} "
        "target_name={} value=0x{:08X} sampler={} applied={} lr=0x{:08X} "
        "count={}",
        dispatch_kind, args.r3, callback_table, callback_offset,
        callback_slot, callback_target, callback_name ? callback_name : "unknown",
        dispatch_value, sampler_slot == UINT32_MAX ? 0xFFFFFFFFu : sampler_slot,
        applied_state ? 1 : 0, args.lr, count);
  }
}

void ObserveRendererPassQueue(const RendererHookArgs& args, uint8_t* base) {
  if (!base || args.r3 == 0) {
    return;
  }

  const uint32_t groups_begin = LoadGuestU32(base, args.r3 + 24);
  const uint32_t groups_end = LoadGuestU32(base, args.r3 + 28);
  if (groups_begin == 0 || groups_end < groups_begin) {
    return;
  }

  const uint32_t group_count = std::min<uint32_t>(
      (groups_end - groups_begin) / 32u, 256u);
  if (group_count == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_observer.mutex);
  RememberGuestMemoryBaseLocked(base);
  for (uint32_t group_index = 0; group_index < group_count; ++group_index) {
    const uint32_t group = groups_begin + group_index * 32u;
    const uint32_t entries_begin = LoadGuestU32(base, group + 20);
    const uint32_t entries_end = LoadGuestU32(base, group + 24);
    if (entries_begin == 0 || entries_end < entries_begin) {
      continue;
    }

    const uint32_t entry_count =
        std::min<uint32_t>((entries_end - entries_begin) / 8u, 4096u);
    for (uint32_t entry_index = 0; entry_index < entry_count; ++entry_index) {
      const uint32_t entry = entries_begin + entry_index * 8u;
      const uint32_t object = LoadGuestU32(base, entry);
      if (object == 0) {
        continue;
      }

      const uint32_t vtable = LoadGuestU32(base, object);
      const uint32_t target = vtable != 0 ? LoadGuestU32(base, vtable + 4) : 0;
      if (target == 0) {
        continue;
      }

      ++g_observer.render_queue_callbacks;
      const uint64_t count = ++g_observer.render_pass_queue_targets[target];
      if (RendererDrawTracingEnabled() &&
          (count <= 3 || IsPowerOfTwo(count))) {
        REXLOG_INFO(
            "Refii renderer pass queue group={} entry={} object=0x{:08X} "
            "vtable=0x{:08X} target=0x{:08X} count={}",
            group_index, entry_index, object, vtable, target, count);
      }
    }
  }
}

void ObserveRendererHookExit(const char* symbol, RendererHookKind kind,
                             const RendererHookArgs& args, uint32_t result,
                             uint8_t* base) {
  const std::string_view symbol_view(symbol);
  if (kind == RendererHookKind::kDraw) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    if (GuestDeviceStateReadableForReplay() &&
        (symbol_view == "sub_822869A0" ||
         symbol_view == "D3DDevice_SetRenderTarget")) {
      RefreshBoundRenderTargetsLocked(base, args.r3);
    } else if (GuestDeviceStateReadableForReplay() &&
               (symbol_view == "sub_82264590" ||
                symbol_view == "D3DDevice_SetDepthStencilSurface")) {
      RefreshEffectiveDepthRenderStateLocked(base, args.r3);
    }
    if (symbol_view == "sub_82265CD8") {
      QueueDrawVerticesUpEventLocked(args, result, base);
    }
    const bool patch_scratch_after_pm4 = symbol_view == "sub_82217DB8";
    if (!patch_scratch_after_pm4) {
      CaptureScratchDrawBuffersLocked(symbol_view, args, result, base);
    }
    DecodePm4WordsLocked(symbol, args, base);
    if (patch_scratch_after_pm4) {
      CaptureScratchDrawBuffersLocked(symbol_view, args, result, base);
    }
    if (symbol_view == "sub_82BA25C0" || symbol_view == "sub_822866E0" ||
        symbol_view == "sub_822868C8") {
      PatchActiveWrappedDrawEventStateLocked();
    }
    EndWrappedDrawStatePatchLocked(symbol_view, args);
    if (symbol_view == "sub_82BA8928") {
      CapturePixDecodedVideoFrameUploadLocked(args, result, base);
    }
    if (symbol_view == "sub_82BA7B28" &&
        (RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
        g_observer.logged_pix_conversion_draw_chains < 16u) {
      REXLOG_INFO(
          "Refii renderer Pix/video conversion chain #{} result=0x{:08X} "
          "device=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
          "width={} height={} r9=0x{:08X} r10=0x{:08X} lr=0x{:08X} "
          "rt={:08X}/{:08X}/{:08X}/{:08X} depth=0x{:08X}",
          g_observer.logged_pix_conversion_draw_chains + 1u, result, args.r3,
          args.r4, args.r5, args.r6, args.r7, args.r8, args.r9, args.r10,
          args.lr, g_observer.render_targets[0], g_observer.render_targets[1],
          g_observer.render_targets[2], g_observer.render_targets[3],
          g_observer.depth_stencil);
      ++g_observer.logged_pix_conversion_draw_chains;
    }
    if (symbol_view == "sub_82BA8EF8" &&
        (RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
        g_observer.logged_pix_render_packet_builders < 16u) {
      REXLOG_INFO(
          "Refii renderer Pix/video packet builder #{} result=0x{:08X} "
          "device=0x{:08X} frame=0x{:08X} fmt=0x{:08X} size={}x{} "
          "packet=0x{:08X} aux=0x{:08X} flags=0x{:08X} lr=0x{:08X} "
          "rt={:08X}/{:08X}/{:08X}/{:08X} depth=0x{:08X}",
          g_observer.logged_pix_render_packet_builders + 1u, result, args.r3,
          args.r4, args.r5, args.r6, args.r7, args.r8, args.r9, args.r10,
          args.lr, g_observer.render_targets[0], g_observer.render_targets[1],
          g_observer.render_targets[2], g_observer.render_targets[3],
          g_observer.depth_stencil);
      ++g_observer.logged_pix_render_packet_builders;
    }
    if (symbol_view == "sub_82206888") {
      QueuePrivateTextureRectangleDrawEventLocked(
          args, base, true /* prefer_rect_argument */);
    }
    if (symbol_view == "sub_82BA83C0") {
      QueuePixRenderVideoFrameDrawEventLocked(args, base);
    }
    if (symbol_view == "sub_82BA95E0") {
      ++g_observer.pix_capture_hooks;
      if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
          g_observer.logged_pix_capture_frames < 16u) {
        const uint32_t stack_arg0 = LoadHookStackU32(base, args, 84u);
        const uint32_t stack_arg1 = LoadHookStackU32(base, args, 88u);
        const uint32_t stack_arg2 = LoadHookStackU32(base, args, 92u);
        REXLOG_INFO(
            "Refii renderer PixCaptureVideoFrame #{} result=0x{:08X} "
            "device=0x{:08X} flags=0x{:08X} r5=0x{:08X} frame=0x{:08X} "
            "r7=0x{:08X} r8=0x{:08X} r9=0x{:08X} r10=0x{:08X} "
            "stack=0x{:08X}/0x{:08X}/0x{:08X} lr=0x{:08X}",
            g_observer.logged_pix_capture_frames + 1u, result, args.r3,
            args.r4, args.r5, args.r6, args.r7, args.r8, args.r9, args.r10,
            stack_arg0, stack_arg1, stack_arg2, args.lr);
        ++g_observer.logged_pix_capture_frames;
      }
    }
    if (symbol_view == "sub_821F9D00") {
      // D3DDevice_SetViewport forwards here after Fable clamps to the active
      // target. Read the post-call device state instead of the original
      // arguments so Plume replays the clamped effective viewport.
      g_observer.viewport_pointer = 0;
      RendererViewportSnapshot viewport =
          DecodeGuestViewportFromDevice(base, args.r3);
      if (!viewport.valid) {
        viewport = DecodeGuestViewport(
            static_cast<float>(args.f1), static_cast<float>(args.f2),
            static_cast<float>(args.f3), static_cast<float>(args.f4),
            static_cast<float>(args.f5), static_cast<float>(args.f6));
      }
      g_observer.viewport = ScaleGuestViewportToOutput(viewport);
      if (RendererDrawTracingEnabled() && g_observer.viewport.valid) {
        REXLOG_INFO(
            "Refii renderer viewport decoded direct target-local x={} y={} "
            "w={} h={} minz={} maxz={}",
            g_observer.viewport.x, g_observer.viewport.y,
            g_observer.viewport.width, g_observer.viewport.height,
            g_observer.viewport.min_depth, g_observer.viewport.max_depth);
      }
    } else if (symbol_view == "sub_821F9C08" ||
               symbol_view == "sub_821F9918") {
      // D3DDevice_SetScissorRect reaches D3D::SetSurfaceClip after clipping to
      // the current viewport. Use the effective device clip when available.
      g_observer.scissor_pointer = 0;
      RendererScissorSnapshot scissor =
          DecodeGuestPackedScissorFromDevice(base, args.r3);
      if (!scissor.valid) {
        scissor = DecodeGuestScissor(
            static_cast<int32_t>(args.r4), static_cast<int32_t>(args.r5),
            static_cast<int32_t>(args.r6), static_cast<int32_t>(args.r7));
      }
      g_observer.scissor = ScaleGuestScissorToOutput(scissor);
      if (RendererDrawTracingEnabled() && g_observer.scissor.valid) {
        REXLOG_INFO(
            "Refii renderer scissor decoded direct target-local l={} t={} "
            "r={} b={}",
            g_observer.scissor.left, g_observer.scissor.top,
            g_observer.scissor.right, g_observer.scissor.bottom);
      }
    }
    if (symbol_view == "sub_8221D1B0") {
      ObservePendingFetchConstantsLocked(args, base);
    } else if (symbol_view == "sub_8221C7D8") {
      ObservePendingRegisterRangeLocked(args, base);
    } else if (symbol_view == "sub_821B6EE0" && args.r4 < 16 &&
               args.r5 != 0) {
      const uint32_t source_address = args.r3 + 0x480u + args.r4 * 24u;
      StoreTextureObjectFetchDwordsLocked(args.r5, source_address, args.r4,
                                          base, args.lr);
    }
    if (IsWrappedDrawSubmitHook(symbol_view)) {
      PatchLatestDrawEventDeviceBindingsLocked(symbol_view, args.r3, base);
    }
    if (IsPresentedDrawCaptureHook(symbol_view)) {
      CapturePresentedDrawEventsLocked();
    }
    return;
  }

  if (symbol_view == "sub_832BCB30" || symbol_view == "sub_832BCDE8" ||
      symbol_view == "sub_832C49E8") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    RefreshBinkVideoPlaneUploadsLocked(symbol_view, base);
    return;
  }

  if (symbol_view == "sub_82BA76F8") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    ObserveImmediateShaderConstantsLocked(args, base);
    return;
  }

  if (symbol_view == "sub_82BA77D0") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    ObserveTextureFetchConstantLocked(args, base);
    return;
  }

  if (symbol_view == "sub_8221A880" || symbol_view == "sub_82214E30") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    StoreShaderConstantVectorsLocked(
        symbol_view == "sub_82214E30" ? ShaderConstantBank::kPixel
                                      : ShaderConstantBank::kVertex,
        args.r4, args.r6, args.r5, base, args.lr);
    return;
  }

  if (symbol_view == "sub_82B991B0" || symbol_view == "sub_82B99208") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    StoreShaderIntConstantsLocked(
        symbol_view == "sub_82B99208" ? ShaderConstantBank::kPixel
                                      : ShaderConstantBank::kVertex,
        args.r4, args.r6, args.r5, base, args.lr);
    return;
  }

  if (symbol_view == "sub_821C6708" || symbol_view == "sub_8226E120") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    StoreShaderBoolConstantsLocked(
        symbol_view == "sub_8226E120" ? ShaderConstantBank::kPixel
                                      : ShaderConstantBank::kVertex,
        args.r4, args.r6, args.r5, base, args.lr);
    return;
  }

  if (symbol_view == "sub_8222BFA0") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    ObserveLiteralShaderConstantsLocked(args, base);
    return;
  }

  if (symbol_view == "sub_821FB650") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    // sub_821FB650 writes the 52-byte Xenos texture/fetch header through its
    // a34 out pointer, which is stack arg +0x6C from the caller's frame.
    const uint32_t texture_header = LoadHookStackU32(base, args, 108u);
    CaptureRootTextureHeaderLocked(symbol_view, args, base, texture_header,
                                   "SetTextureHeader");
    return;
  }

  if (symbol_view == "sub_821FB968") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    // XGRAPHICS::SetBaseTextureHeader copies the completed 52-byte header to
    // a38, which is stack arg +0x7C from the caller's frame.
    const uint32_t texture_header = LoadHookStackU32(base, args, 124u);
    CaptureRootTextureHeaderLocked(symbol_view, args, base, texture_header,
                                   "SetBaseTextureHeader");
    return;
  }

  if (symbol_view == "rex_XGOffsetBaseTextureAddress") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    CaptureRootTextureHeaderLocked(symbol_view, args, base, args.r3,
                                   "OffsetBaseTextureAddress");
    return;
  }

  if (symbol_view == "sub_821F5028") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    if (base && args.r8 != 0 && IsLikelyFableObjectAddress(args.r8)) {
      const RendererResourceKind surface_kind =
          FableSetSurfaceHeaderFormatIsDepth(args.r5) &&
                  !FableSurfaceHeaderFormatIsAmbiguousColorDepth(args.r5)
              ? RendererResourceKind::kDepthSurface
              : RendererResourceKind::kRenderTargetSurface;
      const bool tracked =
          TrackBoundSurfaceLocked(surface_kind, args.r8, 0, base);
      if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
          g_observer.logged_inline_surface_descriptor_builds < 16u) {
        const uint32_t packed_extent = LoadGuestU32(base, args.r8 + 0x24u);
        REXLOG_INFO(
            "Refii renderer root surface header #{} desc=0x{:08X} kind={} "
            "tracked={} extent={}x{} fmt=0x{:08X} arg_fmt=0x{:08X} "
            "pitch=0x{:08X} size=0x{:08X} lr=0x{:08X}",
            g_observer.logged_inline_surface_descriptor_builds + 1u, args.r8,
            ResourceKindName(surface_kind), tracked ? 1 : 0,
            (packed_extent >> 18u) + 1u,
            ((packed_extent >> 3u) & 0x7FFFu) + 1u,
            LoadGuestU32(base, args.r8 + 0x28u), args.r5,
            LoadGuestU32(base, args.r8 + 0x18u),
            LoadGuestU32(base, args.r8 + 0x2Cu), args.lr);
        ++g_observer.logged_inline_surface_descriptor_builds;
      }
    }
    return;
  }

  if (kind == RendererHookKind::kGeneral) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderTrackedLocked(symbol_view, args, base);
    EnsureBoundVertexDeclarationTrackedLocked(symbol_view, args, base);
    return;
  }

  if (kind != RendererHookKind::kResource) {
    return;
  }

  if (symbol_view == "sub_82D160D8" || symbol_view == "sub_82D21C68") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveSurfaceCopyAliasLocked(symbol_view, args, result);
    return;
  }

  if (symbol_view == "sub_82A42FA0") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveBinkVideoPlaneSetupLocked(args, base);
    return;
  }

  if (symbol_view == "sub_821FC178") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    if (auto it = FindResourceIteratorLocked(args.r3);
        it != g_observer.resources.end()) {
      it->second.raw_ref_count = result;
      g_observer.last_resource = it->second;
    }
    return;
  }

  if (symbol_view == "sub_821FC320") {
    if (result != 0) {
      return;
    }

    RendererResourceRecord erased_record;
    bool had_record = false;
    {
      std::lock_guard<std::mutex> lock(g_observer.mutex);
      RememberGuestMemoryBaseLocked(base);
      auto it = FindResourceIteratorLocked(args.r3);
      if (it != g_observer.resources.end()) {
        erased_record = it->second;
        had_record = true;
        const bool defer_surface_release =
            IsSurfaceResourceKind(erased_record.kind) &&
            ActiveSurfaceStateReferencesResourceLocked(erased_record);
        RememberRecentSurfaceShapeLocked(erased_record);
        if (defer_surface_release) {
          it->second.raw_ref_count = result;
          g_observer.deferred_surface_releases.insert(
              erased_record.guest_address);
          g_observer.last_resource = it->second;
        } else {
          g_observer.deferred_surface_releases.erase(
              erased_record.guest_address);
          g_observer.resources.erase(it);
        }
      }
      FlushDeferredSurfaceReleasesLocked();
      ++g_observer.resource_destroys;
    }
    TraceResourceDestroy(args.r3, had_record ? &erased_record : nullptr);
    return;
  }

  if (IsLockHook(symbol_view) && result != 0) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    if (symbol_view == "rex_lhGfx_LockBuffer" &&
        (RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
        g_observer.logged_lock_resource_shapes < 32u) {
      const std::array<uint32_t, 6> dwords =
          base != nullptr && IsLikelyFableObjectAddress(args.r3)
              ? LoadTextureFetchDwords(base, args.r3 + 28u)
              : std::array<uint32_t, 6>{};
      REXLOG_INFO(
          "Refii renderer lock resource shape #{} object=0x{:08X} "
          "result=0x{:08X} block={} flags=0x{:08X} size_arg={} "
          "tex_header={} dwords={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
          g_observer.logged_lock_resource_shapes + 1u, args.r3, result,
          args.r5, args.r4, args.r10,
          HasTextureFetchDwords(dwords) ? 1 : 0, dwords[0], dwords[1],
          dwords[2], dwords[3], dwords[4], dwords[5]);
      ++g_observer.logged_lock_resource_shapes;
    }
    RendererResourceRecord* record = FindResourceRecordLocked(args.r3);
    if (record != nullptr && !IsImageResourceKind(record->kind) &&
        LooksLikeTextureLockResource(symbol_view, args, base)) {
      record = nullptr;
    }
    EnsureTextureRecordForLockLocked(symbol_view, args, base, record);
    if (record == nullptr && base) {
      const RendererResourceKind locked_kind =
          LockedBufferResourceKindForHook(symbol_view);
      if (locked_kind == RendererResourceKind::kVertexBuffer ||
          locked_kind == RendererResourceKind::kIndexBuffer) {
        RendererResourceRecord created;
        created.kind = locked_kind;
        created.guest_address = args.r3;
        SnapshotGuestResource(created, base);
        if (created.raw_buffer_object != 0 && created.raw_size != 0) {
          created.serial = ++g_observer.serial;
          auto [inserted, inserted_new] =
              g_observer.resources.emplace(created.guest_address, created);
          if (!inserted_new) {
            inserted->second = created;
          } else {
            ++g_observer.resource_creates;
            TraceResourceCreate(inserted->second);
          }
          g_observer.last_resource = inserted->second;
          record = &inserted->second;
        }
      }
    }
    if (record) {
      record->locked_address = result;
      record->locked_size = LockedResourceSizeForHook(symbol_view, args, *record);
    }
    return;
  }

  if (IsUnlockHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    if (RendererResourceRecord* record = FindResourceRecordLocked(args.r3)) {
      CaptureLockedBufferUpload(*record, base);
    }
    return;
  }

  if (symbol_view == "rex_XGRegisterVertexShader") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r3,
                            args.r4, base);
    return;
  }

  if (!IsCreateHook(symbol_view)) {
    return;
  }

  const RendererResourceKind created_kind = CreatedResourceKind(symbol_view);
  if (result == 0) {
    TraceResourceCreateFailed(symbol_view, created_kind);
    return;
  }

  RendererResourceRecord record;
  record.kind = created_kind;
  record.guest_address =
      CreatedResourceGuestAddress(symbol_view, args, result, base);
  if (record.guest_address == 0) {
    return;
  }
  if (symbol_view == "sub_82B8AB30" && base && args.r5 != 0) {
    const uint32_t surface_format = LoadGuestU32(base, args.r5);
    record.kind =
        surface_format == 54u
            ? RendererResourceKind::kDepthSurface
            : RendererResourceKind::kRenderTargetSurface;
  }
  const bool root_image_create = IsRootImageCreateHook(symbol_view);
  // D3DDevice_CreateSurface/CreateTexture are root constructors. Their
  // leading arguments are shape fields such as width/height, not guest object
  // parents, and later registers may contain caller/code-address residue.
  record.parent_address = root_image_create ? 0u : args.r3;
  if (IsShaderResourceKind(record.kind) && base && args.r3 != 0) {
    const bool populated_from_object = PopulateShaderRecordFromObject(record, base);
    if (!PopulateShaderRecordFromCreateArgument(record, base, args.r3) &&
        !populated_from_object) {
      record.raw_buffer_object = 0;
      record.raw_size = 0;
      record.raw_stride_or_format = 0;
      record.raw_hash = 0;
    }
  }
  SnapshotGuestResource(record, base);
  if (symbol_view == "rex_lhTexture_Create") {
    // The returned Fable D3DSurface already contains the authoritative packed
    // header: +18 pitch/MSAA, +1C color info, +20 depth info, +24 extent,
    // +28 D3DFORMAT, +2C byte size. Keep those fields from the snapshot and
    // retain the call arguments only as plain width/height hints.
    record.raw_size = args.r3;
    record.raw_stride_or_format = args.r4;
  }
  if (IsSurfaceResourceKind(record.kind) &&
      !HasPlausibleSurfaceHeader(record) &&
      !SnapshotNearbySurfaceHeader(record, base)) {
    return;
  }
  SanitizeImageResourceParent(record);
  if (record.kind == RendererResourceKind::kVertexDeclaration &&
      record.raw_hash == 0 && base && args.r3 != 0) {
    PopulateVertexDeclarationRecordFromElements(record, base, args.r3);
  }
  if (record.kind == RendererResourceKind::kVertexDeclaration &&
      record.raw_hash == 0) {
    return;
  }

  {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    auto existing_it = g_observer.resources.find(record.guest_address);
    const bool existing_shape_matches =
        IsSurfaceResourceKind(record.kind) &&
        existing_it != g_observer.resources.end() &&
        IsSurfaceResourceKind(existing_it->second.kind) &&
        SurfaceShapeMatches(record, existing_it->second);
    if (record.kind == RendererResourceKind::kSurfaceView &&
        existing_it != g_observer.resources.end() &&
        (existing_it->second.kind == RendererResourceKind::kDepthSurface ||
         existing_it->second.kind ==
             RendererResourceKind::kRenderTargetSurface)) {
      const bool existing_depth =
          FableSetSurfaceHeaderFormatIsDepth(existing_it->second.raw_packed40);
      const bool view_depth =
          FableSetSurfaceHeaderFormatIsDepth(record.raw_packed40);
      if (existing_depth != view_depth) {
        if (RendererResourceTracingEnabled() &&
            g_observer.logged_bound_surface_failures.insert(
                record.guest_address)
                .second) {
          REXLOG_INFO(
              "Refii renderer ignored mismatched surface view alias "
              "guest=0x{:08X} view_parent=0x{:08X} view_fmt=0x{:08X} "
              "existing_kind={} existing_fmt=0x{:08X}",
              record.guest_address, record.parent_address,
              record.raw_packed40,
              ResourceKindName(existing_it->second.kind),
              existing_it->second.raw_packed40);
        }
        return;
      }
      // Fable builds short-lived D3DSurface views over the same descriptors as
      // real render/depth targets. Keep the root surface authoritative for
      // Plume; the view can be released immediately by the guest while queued
      // draw replay still needs the target shape.
      RememberRecentSurfaceShapeLocked(existing_it->second);
      g_observer.last_resource = existing_it->second;
      return;
    }
    LogSuspiciousVertexDeclarationLocked(record.guest_address, record, base);
    if (existing_shape_matches) {
      record.serial = existing_it->second.serial;
      if (!root_image_create && existing_it->second.parent_address != 0 &&
          !IsLikelyFableCodeAddress(existing_it->second.parent_address)) {
        record.parent_address = existing_it->second.parent_address;
      }
    } else {
      record.serial = ++g_observer.serial;
    }
    if (root_image_create) {
      // Shape reuse can otherwise carry an older wrapper/code pointer into a
      // direct D3D root allocation. Keep root images parentless so surface-view
      // aliases resolve from the actual guest object rather than stale ABI
      // residue.
      record.parent_address = 0;
    }
    SanitizeImageResourceParent(record);
    const bool inserted_new = existing_it == g_observer.resources.end();
    g_observer.resources[record.guest_address] = record;
    g_observer.deferred_surface_releases.erase(record.guest_address);
    if (IsSurfaceResourceKind(record.kind)) {
      RememberRecentSurfaceShapeLocked(record);
    } else {
      g_observer.recent_surface_shapes.erase(record.guest_address);
    }
    g_observer.last_resource = record;
    if (!existing_shape_matches) {
      ++g_observer.resource_creates;
    }
    if (existing_shape_matches || !inserted_new) {
      return;
    }
  }

  TraceResourceCreate(record);
}

RendererHookObserverSnapshot GetRendererHookObserverSnapshot() {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  RendererHookObserverSnapshot snapshot;
  const RendererResourceCounts counts = CountResourcesLocked();
  snapshot.live_resources = counts.live_resources;
  snapshot.live_textures = counts.live_textures;
  snapshot.live_surfaces = counts.live_surfaces;
  snapshot.live_vertex_buffers = counts.live_vertex_buffers;
  snapshot.live_index_buffers = counts.live_index_buffers;
  snapshot.live_shaders = counts.live_shaders;
  snapshot.resource_creates = g_observer.resource_creates;
  snapshot.resource_destroys = g_observer.resource_destroys;
  snapshot.resource_locks = g_observer.resource_locks;
  snapshot.resource_unlocks = g_observer.resource_unlocks;
  snapshot.draws = g_observer.draws;
  snapshot.indexed_draws = g_observer.indexed_draws;
  snapshot.pending_draw_events = g_observer.draw_events.size();
  snapshot.dropped_draw_events = g_observer.dropped_draw_events;
  snapshot.render_passes = g_observer.render_passes;
  snapshot.state_flushes = g_observer.state_flushes;
  snapshot.state_dispatches = g_observer.state_dispatches;
  snapshot.state_dispatch_targets = g_observer.state_dispatch_targets.size();
  snapshot.render_queue_callbacks = g_observer.render_queue_callbacks;
  snapshot.render_queue_callback_targets =
      g_observer.render_pass_queue_targets.size();
  snapshot.command_writes = g_observer.command_writes;
  snapshot.command_submits = g_observer.command_submits;
  snapshot.command_words = g_observer.command_words;
  snapshot.pm4_type0_packets = g_observer.pm4_type0_packets;
  snapshot.pm4_type3_packets = g_observer.pm4_type3_packets;
  snapshot.pm4_indirect_packets = g_observer.pm4_indirect_packets;
  snapshot.pm4_draw_packets = g_observer.pm4_draw_packets;
  snapshot.pm4_swap_packets = g_observer.pm4_swap_packets;
  snapshot.pm4_constant_packets = g_observer.pm4_constant_packets;
  snapshot.pm4_parse_errors = g_observer.pm4_parse_errors;
  snapshot.shader_constant_uploads = g_observer.shader_constant_uploads;
  snapshot.fetch_constant_uploads = g_observer.fetch_constant_uploads;
  snapshot.shader_constant_upload_nonzero_dwords =
      g_observer.shader_constant_upload_nonzero_dwords;
  snapshot.shader_constant_zero_uploads =
      g_observer.shader_constant_zero_uploads;
  snapshot.shader_constant_skipped_uploads =
      g_observer.shader_constant_skipped_uploads;
  snapshot.pix_decode_frames = g_observer.pix_decode_frames;
  snapshot.pix_decode_pending_frames =
      g_observer.pending_pix_decoded_video_frames.size();
  snapshot.pix_render_hooks = g_observer.pix_render_hooks;
  snapshot.pix_render_events = g_observer.pix_render_events;
  snapshot.pix_render_uv_overrides = g_observer.pix_render_uv_overrides;
  snapshot.pix_capture_hooks = g_observer.pix_capture_hooks;
  snapshot.pix_render_bad_packets = g_observer.pix_render_bad_packets;
  snapshot.pix_render_bad_vertices = g_observer.pix_render_bad_vertices;
  snapshot.last_pix_texture = g_observer.last_pix_texture;
  snapshot.last_pix_packet = g_observer.last_pix_packet;
  snapshot.last_pix_decode_base = g_observer.last_pix_decode_base;
  snapshot.last_pix_decode_width = g_observer.last_pix_decode_width;
  snapshot.last_pix_decode_height = g_observer.last_pix_decode_height;
  snapshot.last_pix_fetch_base = g_observer.last_pix_fetch_base;
  snapshot.last_pix_fetch_size = g_observer.last_pix_fetch_size;
  snapshot.last_pix_fetch_format = g_observer.last_pix_fetch_format;
  snapshot.last_pix_output_width = g_observer.last_pix_output_width;
  snapshot.last_pix_output_height = g_observer.last_pix_output_height;
  snapshot.last_pix_decoded_width = g_observer.last_pix_decoded_width;
  snapshot.last_pix_decoded_height = g_observer.last_pix_decoded_height;
  snapshot.last_pix_u1 = g_observer.last_pix_u1;
  snapshot.last_pix_v1 = g_observer.last_pix_v1;
  snapshot.vertex_shader_constant_nonzero_dwords =
      CountNonZeroDwords(g_observer.vertex_shader_constants);
  snapshot.pixel_shader_constant_nonzero_dwords =
      CountNonZeroDwords(g_observer.pixel_shader_constants);
  snapshot.last_shader_constant_bank = g_observer.last_shader_constant_bank;
  snapshot.last_shader_constant_first_register =
      g_observer.last_shader_constant_first_register;
  snapshot.last_shader_constant_first_component =
      g_observer.last_shader_constant_first_component;
  snapshot.last_shader_constant_dwords = g_observer.last_shader_constant_dwords;
  snapshot.last_shader_constant_nonzero_dwords =
      g_observer.last_shader_constant_nonzero_dwords;
  snapshot.last_shader_constant_source = g_observer.last_shader_constant_source;
  snapshot.last_shader_constant_lr = g_observer.last_shader_constant_lr;
  std::memcpy(snapshot.last_shader_constant_preview,
              g_observer.last_shader_constant_preview,
              sizeof(snapshot.last_shader_constant_preview));
  snapshot.fetch_constant_nonzero_dwords =
      CountNonZeroDwords(g_observer.fetch_constants);
  snapshot.clears = g_observer.clears;
  snapshot.presents = g_observer.presents;
  snapshot.display_present_texture_base =
      g_observer.display_present_texture_base;
  snapshot.display_present_width = g_observer.display_present_width;
  snapshot.display_present_height = g_observer.display_present_height;
  snapshot.display_present_format = g_observer.display_present_format;
  snapshot.display_present_surface_object =
      g_observer.display_present_surface_object;
  snapshot.display_present_source = g_observer.display_present_source;
  snapshot.display_present_register = g_observer.display_present_register;
  snapshot.display_present_register_value =
      g_observer.display_present_register_value;
  snapshot.display_present_fetch_dwords =
      g_observer.display_present_fetch_dwords;
  for (const auto& [destination, alias] : g_observer.surface_copy_aliases) {
    (void)destination;
    if (snapshot.surface_copy_alias_count >=
        snapshot.surface_copy_aliases.size()) {
      break;
    }
    snapshot.surface_copy_aliases[snapshot.surface_copy_alias_count++] = alias;
  }
  snapshot.state_changes = g_observer.state_changes;
  std::memcpy(snapshot.render_targets, g_observer.render_targets,
              sizeof(snapshot.render_targets));
  snapshot.depth_stencil = g_observer.depth_stencil;
  snapshot.vertex_declaration = g_observer.vertex_declaration;
  snapshot.vertex_shader = g_observer.vertex_shader;
  snapshot.pixel_shader = g_observer.pixel_shader;
  snapshot.index_buffer = g_observer.index_buffer;
  std::memcpy(snapshot.textures, g_observer.textures,
              sizeof(snapshot.textures));
  snapshot.render_states = g_observer.render_states;
  snapshot.sampler_states = g_observer.sampler_states;
  std::memcpy(snapshot.streams, g_observer.streams, sizeof(snapshot.streams));
  snapshot.last_draw = g_observer.last_draw;
  snapshot.viewport_pointer = g_observer.viewport_pointer;
  snapshot.scissor_pointer = g_observer.scissor_pointer;
  snapshot.viewport = g_observer.viewport;
  snapshot.scissor = g_observer.scissor;
  snapshot.scaler = g_observer.scaler;
  snapshot.clear_flags = g_observer.clear_flags;
  snapshot.last_state_flush_lr = g_observer.last_state_flush_lr;
  snapshot.last_resource_address = g_observer.last_resource.guest_address;
  snapshot.last_resource_size = g_observer.last_resource.raw_size;
  snapshot.last_resource_stride_or_format =
      g_observer.last_resource.raw_stride_or_format;
  snapshot.last_shader_guest_hash = g_observer.last_resource.raw_hash;
  snapshot.last_command_source = g_observer.last_command_source;
  snapshot.last_command_pointer = g_observer.last_command_pointer;
  snapshot.last_command_word_count = g_observer.last_command_word_count;
  snapshot.last_command_first_word = g_observer.last_command_first_word;
  snapshot.last_command_packet_type = g_observer.last_command_packet_type;
  snapshot.last_command_type3_opcode = g_observer.last_command_type3_opcode;
  std::memcpy(snapshot.last_command_preview, g_observer.last_command_preview,
              sizeof(snapshot.last_command_preview));
  return snapshot;
}

std::vector<RendererTrackedResourceSnapshot>
GetRendererTrackedResourceSnapshots(
    const std::unordered_map<uint32_t,
                             RendererTrackedResourceUploadCacheEntry>*
        known_resources) {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  std::vector<RendererTrackedResourceSnapshot> snapshots;
  snapshots.reserve(g_observer.resources.size());
  for (const auto& [address, record] : g_observer.resources) {
    (void)address;
    RendererTrackedResourceSnapshot snapshot;
    snapshot.kind = PublicResourceKind(record.kind);
    snapshot.guest_address = record.guest_address;
    snapshot.parent_address = record.parent_address;
    snapshot.size = record.raw_size;
    snapshot.stride_or_format = record.raw_stride_or_format;
    snapshot.buffer_object = record.raw_buffer_object;
    snapshot.raw_flags = record.raw_flags;
    snapshot.raw_packed28 = record.raw_packed28;
    snapshot.raw_packed32 = record.raw_packed32;
    snapshot.raw_packed36 = record.raw_packed36;
    snapshot.raw_packed40 = record.raw_packed40;
    snapshot.raw_packed44 = record.raw_packed44;
    snapshot.raw_packed48 = record.raw_packed48;
    snapshot.normalized_fetch_dwords = record.normalized_fetch_dwords;
    snapshot.guest_bytecode_hash = record.raw_hash;
    snapshot.upload_generation = record.upload_generation;
    snapshot.serial = record.serial;
    snapshot.scratch = record.scratch;
    bool copy_upload_data = true;
    if (known_resources) {
      const auto known_it = known_resources->find(record.guest_address);
      if (known_it != known_resources->end() &&
          known_it->second.kind == snapshot.kind &&
          known_it->second.upload_generation == record.upload_generation &&
          known_it->second.serial == record.serial) {
        copy_upload_data = false;
      }
    }
    if (copy_upload_data) {
      snapshot.upload_data = record.upload_data;
    }
    snapshots.push_back(snapshot);
  }
  return snapshots;
}

RendererTrackedResourceVersion GetRendererTrackedResourceVersion() {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  RendererTrackedResourceVersion version;
  version.serial = g_observer.serial;
  version.resource_creates = g_observer.resource_creates;
  version.resource_destroys = g_observer.resource_destroys;
  version.resource_unlocks = g_observer.resource_unlocks;
  return version;
}

std::vector<uint32_t> GetRendererGuestWords(uint32_t guest_address,
                                            uint32_t word_count) {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  std::vector<uint32_t> words;
  if (g_observer.memory_base == nullptr || guest_address == 0 ||
      word_count == 0 || word_count > 64) {
    return words;
  }

  words.reserve(word_count);
  for (uint32_t i = 0; i < word_count; ++i) {
    words.push_back(
        LoadGuestU32(g_observer.memory_base, guest_address + i * 4u));
  }
  return words;
}

std::vector<RendererDrawEventSnapshot> ConsumeRendererDrawEvents() {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  SanitizePendingDrawEventsLocked();

  std::vector<RendererDrawEventSnapshot> events;
  events.swap(g_observer.draw_events);
  g_observer.consumed_draw_event_present = g_observer.presents;
  return events;
}

std::vector<RendererDrawEventSnapshot> ConsumeRendererPresentedDrawEvents() {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  if (g_observer.presented_draw_event_present == 0 ||
      g_observer.presented_draw_event_present ==
          g_observer.consumed_draw_event_present) {
    return {};
  }

  std::vector<RendererDrawEventSnapshot> events;
  events.swap(g_observer.presented_draw_events);
  g_observer.consumed_draw_event_present =
      g_observer.presented_draw_event_present;
  return events;
}

}  // namespace refii::renderer
