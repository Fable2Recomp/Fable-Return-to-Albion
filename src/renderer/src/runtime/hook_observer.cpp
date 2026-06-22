#include "runtime/hook_observer.h"

#include "runtime/cvars.h"
#include "runtime/plume/direct/fable_guest_resource_registry.h"
#include "runtime/plume/direct/fable_hook_symbols.h"
#include "runtime/plume/direct/fable_observer_resources.h"
#include "runtime/plume/direct/fable_direct_present_state.h"
#include "runtime/plume/direct/fable_direct_state.h"
#include "runtime/plume/draw/plume_draw_state.h"
#include "runtime/plume/general/plume_backend.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cerrno>
#include <cmath>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <iterator>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

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
constexpr uint32_t kFableTitleBillboardTileFirstBase = 0x14520000u;
constexpr uint32_t kFableTitleBillboardTileLastBase = 0x14840000u;
constexpr uint32_t kFableTitleBillboardTileBaseStride = 0x00020000u;
constexpr uint32_t kFableTitleBillboardTileFirstObject = 0x4C12BAE0u;
constexpr uint32_t kFableTitleBillboardTileLastObject = 0x4C12C508u;
constexpr uint32_t kFableTitleBillboardTileObjectStride = 0x68u;
constexpr uint32_t kFableTitleBillboardTileHeaderOffset = 0x34u;
constexpr uint32_t kFableSceneBatchDrawReturn = 0x822197F8u;
constexpr uint32_t kFableSceneBatchInlineBytes = 48u;
constexpr uint32_t kFableSceneBatchCopiedBytes = 32u;
constexpr uint32_t kFableSceneBatchVertexStride = 16u;
constexpr uint32_t kMaxIndexedScratchStreamPairSpan = 256u * 1024u;
constexpr uint32_t kFableVertexShaderBindDrawReturn = 0x82221DC8u;

struct FableSceneBatchDrawMarker {
  bool valid = false;
  bool queued = false;
  uint32_t submit_context = 0;
  uint32_t manager = 0;
  uint32_t device = 0;
  uint32_t batch_record = 0;
  uint32_t record_address = 0;
  uint32_t stream_address = 0;
  uint32_t byte_start = 0;
  uint32_t byte_count = 0;
  uint32_t stride = 0;
  uint32_t primitive_type = 0;
  std::array<uint8_t, kFableSceneBatchInlineBytes> inline_data = {};
};

struct RecentCpuTiledTextureUpload {
  uint64_t serial = 0;
  uint32_t destination = 0;
  uint32_t source = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint32_t lr = 0;
  uint32_t first_nonzero = UINT32_MAX;
  uint64_t hash = 0;
  std::array<uint8_t, 4> first4 = {};
  std::vector<uint8_t> data;
};

struct ExactTitleTextureRefreshCandidate {
  uint64_t serial = 0;
  uint64_t producer_serial = 0;
  uint64_t claimed_blank_probe_serial = 0;
  uint32_t record_address = 0;
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t texture_object_address = 0;
  uint32_t texture_parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint32_t producer_symbol_address = 0;
  uint32_t producer_lr = 0;
  uint32_t root_header = 0;
  uint32_t lock_address = 0;
  uint32_t lock_write_address = 0;
  uint32_t lock_size = 0;
};

struct ExactTitleTextureBlankProbe {
  uint64_t serial = 0;
  uint32_t record_address = 0;
  uint32_t object_address = 0;
  uint32_t parent_address = 0;
  uint32_t texture_object_address = 0;
  uint32_t texture_parent_address = 0;
  uint32_t base_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
};

struct PrivateTextureRectangleWrapperCapture {
  uint64_t serial = 0;
  uint64_t context_key = 0;
  uint32_t symbol_address = 0;
  uint32_t source_texture = 0;
  uint32_t arg0 = 0;
  uint32_t arg1 = 0;
  uint32_t arg2 = 0;
  uint32_t rect_pointer = 0;
  std::array<uint32_t, 4> rect_dwords = {};
  std::array<uint32_t, 4> setup_globals = {};
  uint32_t frame_index = 0;
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
  bool joined = false;
};

struct PrivateTextureRectangleWrapperMissStats {
  uint64_t total = 0;
  uint32_t logged = 0;
  uint32_t last_frame_index = 0;
  uint32_t last_source_texture = 0;
  uint32_t last_lr = 0;
  uint64_t last_call_serial = 0;
  uint64_t last_context_key = 0;
};

struct GuestPointerTableScan {
  uint32_t nonzero_count = 0;
  uint32_t plausible_object_count = 0;
  uint32_t first_nonzero_index = UINT32_MAX;
  uint32_t first_nonzero_value = 0;
  uint32_t first_plausible_object_index = UINT32_MAX;
  uint32_t first_plausible_object = 0;
};

struct PendingSurfaceViewTextureLink {
  uint64_t call_serial = 0;
  uint64_t key = 0;
  uint32_t symbol_address = 0;
  uint32_t surface_view = 0;
  uint32_t parent_texture = 0;
  uint32_t level = 0;
  uint32_t face_or_slice = 0;
  RendererResourceKind view_kind = RendererResourceKind::kSurfaceView;
  uint32_t d3d_format = 0;
};

enum class PendingTextureIdentityOperationKind {
  kRegister,
  kForget,
};

struct PendingTextureIdentityOperation {
  PendingTextureIdentityOperationKind kind =
      PendingTextureIdentityOperationKind::kRegister;
  uint32_t object_texture = 0;
  FableTextureIdentity identity;
};

enum class PendingShaderDeclarationIdentityOperationKind {
  kRegisterShader,
  kForgetShader,
  kRegisterVertexDeclaration,
  kForgetVertexDeclaration,
};

struct PendingShaderDeclarationIdentityOperation {
  PendingShaderDeclarationIdentityOperationKind kind =
      PendingShaderDeclarationIdentityOperationKind::kForgetShader;
  uint32_t object_address = 0;
  FableShaderIdentity shader;
  FableVertexDeclarationIdentity declaration;
};

struct PendingPrivateTextureCopyAction {
  FablePrivateTextureCopyAction action;
};

enum class PendingExactTextureActionKind {
  kHeader,
  kFetchBinding,
};

struct PendingExactTextureAction {
  PendingExactTextureActionKind kind = PendingExactTextureActionKind::kHeader;
  FableTextureHeaderAction header;
  FableTextureFetchBindingAction fetch_binding;
};

struct RendererHookObserverState {
  std::mutex mutex;
  uint8_t* memory_base = nullptr;
  uint64_t serial = 0;
  uint64_t active_wrapped_draw_serial = 0;
  uint32_t active_wrapped_draw_device = 0;
  RendererHookArgs active_wrapped_draw_args = {};
  RendererDrawEventSource active_wrapped_draw_source =
      RendererDrawEventSource::kUnknown;
  RendererLastDrawSnapshot active_wrapped_draw_draw = {};
  bool active_wrapped_draw_indexed_scratch = false;
  bool active_wrapped_draw_scratch_captured = false;
  bool active_wrapped_draw_pending_materialization = false;
  uint64_t active_wrapped_draw_call_serial = 0;
  uint64_t active_wrapped_draw_context_key = 0;
  uint32_t active_wrapped_draw_symbol_address = 0;
  uint64_t last_queued_draw_serial = 0;
  RendererDrawEventSource last_queued_draw_source =
      RendererDrawEventSource::kUnknown;
  uint32_t last_queued_draw_render_target = 0;
  FableSceneBatchDrawMarker scene_batch_draw_marker;
  uint32_t scene_submit_depth = 0;
  uint64_t draws = 0;
  uint64_t indexed_draws = 0;
  uint64_t scene_walker_calls = 0;
  uint64_t scene_walker_flush_calls = 0;
  uint64_t scene_walker_nonempty_calls = 0;
  uint64_t scene_walker_object_slots_total = 0;
  uint64_t scene_draw_filter_calls = 0;
  uint64_t scene_batch_traversal_calls = 0;
  uint64_t scene_batch_bridge_calls = 0;
  uint64_t scene_batch_bridge_rejected_calls = 0;
  uint64_t scene_batch_bridge_queued_draws = 0;
  uint32_t logged_scene_batch_rejects = 0;
  uint32_t logged_scene_draw_submit_samples = 0;
  uint32_t logged_scene_terminal_missing_markers = 0;
  uint32_t last_scene_walker_list = 0;
  uint32_t last_scene_walker_begin = 0;
  uint32_t last_scene_walker_end = 0;
  uint32_t last_scene_walker_count = 0;
  uint32_t last_scene_walker_first = 0;
  uint32_t last_scene_walker_result = 0;
  uint32_t last_scene_walker_lr = 0;
  uint32_t logged_scene_walker_samples = 0;
  uint32_t logged_scene_submit_scope_samples = 0;
  uint64_t render_passes = 0;
  uint64_t frame_director_observations = 0;
  uint64_t last_frame_director_gate_key[2] = {UINT64_MAX, UINT64_MAX};
  uint64_t render_frame_owner_observations = 0;
  uint64_t last_render_frame_owner_key[2] = {UINT64_MAX, UINT64_MAX};
  uint32_t last_render_frame_current_index = UINT32_MAX;
  uint32_t last_render_frame_previous_index = UINT32_MAX;
  uint32_t last_render_frame_next_index = UINT32_MAX;
  uint32_t last_render_frame_pending_index = UINT32_MAX;
  uint32_t last_render_frame_current_scene = 0;
  uint32_t last_render_frame_previous_scene = 0;
  uint32_t last_render_frame_next_scene = 0;
  uint32_t last_render_frame_pending_scene = 0;
  uint64_t renderer_time_delta_observations = 0;
  uint64_t last_renderer_time_delta_key = UINT64_MAX;
  std::unordered_map<uint32_t, uint32_t> assigned_scene_objects;
  uint64_t scene_table_repairs = 0;
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
  uint64_t scene_object_submit_hooks = 0;
  uint64_t scene_object_draws = 0;
  uint64_t scene_inline_vertex_draws = 0;
  uint64_t last_pm4_resolve_command_write = 0;
  uint64_t last_pm4_resolve_key = 0;
  uint64_t clears = 0;
  uint32_t logged_set_color_clear_roots = 0;
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
  uint64_t get_render_target_calls = 0;
  uint32_t last_get_render_target_slot = 0;
  uint32_t last_get_render_target_surface = 0;
  uint64_t state_changes = 0;
  bool render_state_defaults_initialized = false;
  uint32_t requested_z_enable = 0;
  uint32_t requested_z_write_enable = 0;
  bool requested_z_enable_valid = false;
  bool requested_z_write_enable_valid = false;
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
  uint32_t last_video_device = 0;
  uint32_t logged_pm4_draws = 0;
  uint32_t logged_scene_object_draws = 0;
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
  uint32_t logged_exact_texture_header_actions = 0;
  uint32_t logged_exact_texture_fetch_bindings = 0;
  uint32_t logged_exact_texture_fetch_header_joins = 0;
  uint32_t logged_exact_texture_object_header_actions = 0;
  uint32_t logged_texture_capture_successes = 0;
  uint32_t logged_texture_capture_failures = 0;
  uint32_t logged_title_texture_upload_probes = 0;
  uint32_t logged_title_texture_alias_uploads = 0;
  uint32_t logged_tile_helper_deep_probes = 0;
  uint32_t logged_tile_destination_uploads = 0;
  uint32_t logged_tile_destination_entry_probes = 0;
  uint32_t logged_tile_destination_association_probes = 0;
  uint32_t logged_tile_destination_reject_probes = 0;
  uint32_t logged_tile_destination_mip_skip_probes = 0;
  uint32_t logged_tile_helper_refresh_dispatches = 0;
  uint32_t logged_recent_cpu_tiled_texture_uploads = 0;
  uint32_t zero_texture_refresh_cursor = 0;
  uint32_t zero_texture_refresh_harvest_counter = 0;
  std::unordered_map<uint32_t, ExactTitleTextureRefreshCandidate>
      exact_title_texture_refresh_candidates;
  std::unordered_map<uint32_t, ExactTitleTextureBlankProbe>
      exact_title_texture_blank_probes;
  uint32_t logged_exact_title_texture_refresh_candidates = 0;
  uint32_t logged_exact_title_texture_refresh_candidate_updates = 0;
  uint32_t logged_exact_title_texture_refresh_root_joins = 0;
  uint32_t logged_exact_title_texture_blank_probes = 0;
  uint32_t logged_exact_title_texture_blank_probe_claims = 0;
  uint32_t logged_exact_title_gui_unlock_uploads = 0;
  uint32_t viewport_pointer = 0;
  uint32_t scissor_pointer = 0;
  uint32_t last_state_flush_lr = 0;
  uint32_t last_command_source = 0;
  uint32_t last_command_pointer = 0;
  uint32_t last_command_word_count = 0;
  uint32_t last_command_first_word = 0;
  uint32_t last_command_packet_type = 0;
  uint32_t last_command_type3_opcode = 0;
  uint32_t last_command_preview[8] = {};
  uint32_t logged_post_flush_draw_binding_mutations = 0;
  uint32_t logged_title_draw_binding_checks = 0;
  uint32_t logged_missing_draw_bindings = 0;
  uint32_t logged_private_texture_rectangle_exact_guards = 0;
  uint32_t logged_indexed_scratch_captures = 0;
  uint32_t logged_scratch_memory_copies = 0;
  uint32_t logged_scratch_pair_expansions = 0;
  uint32_t logged_scratch_pair_no_expansions = 0;
  uint32_t logged_nonindexed_scratch_expansions = 0;
  uint32_t logged_scratch_attach_samples = 0;
  uint32_t logged_scratch_attach_failures = 0;
  uint32_t logged_scene_indexed_no_expansions = 0;
  bool logged_draw_vertices_up_capture = false;
  bool logged_private_texture_rectangle_capture = false;
  uint32_t logged_private_texture_rectangle_details = 0;
  uint32_t logged_private_texture_rectangle_aliases = 0;
  uint32_t logged_private_texture_rectangle_clears = 0;
  uint32_t logged_private_texture_rectangle_wrapper_captures = 0;
  uint32_t logged_private_texture_rectangle_wrapper_joins = 0;
  uint32_t logged_private_texture_rectangle_wrapper_misses = 0;
  uint32_t logged_inline_surface_descriptor_builds = 0;
  uint32_t logged_inline_texture_descriptor_builds = 0;
  uint32_t logged_transient_render_target_releases = 0;
  uint32_t last_root_texture_header = 0;
  uint32_t last_root_texture_width = 0;
  uint32_t last_root_texture_height = 0;
  uint32_t last_root_texture_format = 0;
  uint64_t last_root_texture_serial = 0;
  uint32_t logged_pix_conversion_draw_chains = 0;
  uint32_t logged_pix_render_packet_builders = 0;
  uint32_t logged_exact_texture_wrapper_sources = 0;
  uint32_t logged_pix_decode_packet_shapes = 0;
  uint32_t logged_pix_capture_frames = 0;
  std::unordered_map<uint32_t, PendingPixDecodedVideoFrame>
      pending_pix_decoded_video_frames;
  std::vector<RecentCpuTiledTextureUpload> recent_cpu_tiled_texture_uploads;
  std::unordered_map<uint32_t, uint64_t> last_queued_bink_plane_generations;
  std::unordered_map<uint32_t, uint64_t> last_queued_bink_plane_hashes;
  std::unordered_set<uint64_t> expanded_indexed_scratch_draw_serials;
  std::unordered_map<uint32_t, uint64_t> unknown_destroy_counts;
  std::unordered_set<uint32_t> deferred_surface_releases;
  std::unordered_map<uint32_t, RendererResourceRecord> recent_surface_shapes;
  std::unordered_map<uint32_t, uint64_t> state_flush_callers;
  std::unordered_map<uint32_t, uint64_t> state_dispatch_targets;
  std::unordered_map<uint32_t, uint64_t> render_pass_queue_targets;
  std::unordered_set<uint32_t> logged_bound_surface_failures;
  std::unordered_set<uint32_t> logged_vertex_declaration_failures;
  std::unordered_set<uint64_t> logged_private_texture_rectangle_detail_keys;
  std::unordered_map<uint32_t, PrivateTextureRectangleWrapperMissStats>
      private_texture_rectangle_wrapper_miss_stats;
  std::vector<PrivateTextureRectangleWrapperCapture>
      active_private_texture_rectangle_wrappers;
  uint32_t logged_stream_source_shapes = 0;
  uint32_t logged_stream_source_zero_ranges = 0;
  uint32_t logged_stream_upload_aliases = 0;
  uint32_t logged_lock_resource_shapes = 0;
  uint32_t logged_texture_lock_records = 0;
  uint32_t logged_image_lock_uploads = 0;
  uint32_t logged_export_resolves = 0;
  uint32_t logged_export_resolve_rejections = 0;
  uint32_t logged_end_export_resolve_probes = 0;
  uint32_t logged_pm4_resolves = 0;
  uint32_t logged_bink_video_planes = 0;
  uint32_t logged_bink_video_uploads = 0;
  uint64_t bink_video_plane_setups = 0;
  uint64_t bink_video_upload_refreshes = 0;
  std::unordered_map<uint32_t, BinkVideoPlaneBinding> bink_planes_by_surface;
  std::unordered_map<uint32_t, RendererSurfaceCopyAliasSnapshot>
      surface_copy_aliases;
  std::unordered_map<uint32_t, RendererSurfaceCopyAliasSnapshot>
      pending_d3dx_lock_surface_copies;
  std::unordered_map<uint32_t, std::vector<PendingSurfaceViewTextureLink>>
      pending_surface_view_texture_links_by_parent;
  std::unordered_set<uint64_t> registered_surface_view_texture_link_keys;
  uint32_t logged_surface_copy_aliases = 0;
  uint32_t logged_surface_texture_copy_links = 0;
  uint32_t logged_surface_view_texture_links = 0;
  uint32_t logged_pending_surface_view_texture_links = 0;
  uint32_t logged_texture_clone_aliases = 0;
  uint32_t logged_root_surface_texture_siblings = 0;
};

RendererHookObserverState g_observer;
std::atomic<uint64_t> g_pending_fable_direct_resource_ack = 0;
std::mutex g_pending_fable_direct_destroy_ack_mutex;
std::vector<std::pair<uint32_t, uint64_t>>
    g_pending_fable_direct_destroy_acks;
std::mutex g_pending_texture_identity_operations_mutex;
std::vector<PendingTextureIdentityOperation>
    g_pending_texture_identity_operations;
std::mutex g_pending_shader_declaration_identity_operations_mutex;
std::vector<PendingShaderDeclarationIdentityOperation>
    g_pending_shader_declaration_identity_operations;
std::mutex g_pending_private_texture_copy_actions_mutex;
std::vector<PendingPrivateTextureCopyAction>
    g_pending_private_texture_copy_actions;
std::mutex g_pending_exact_texture_actions_mutex;
std::vector<PendingExactTextureAction> g_pending_exact_texture_actions;

constexpr size_t kMaxScratchBufferResources = 256;
constexpr uint32_t kMaxTrackedShaderBytecodeSize = 1u << 20;
constexpr uint32_t kMaxTrackedShaderContainerSize = 2u << 20;
constexpr uint32_t kMaxScratchUploadSnapshotSize = 16u << 20;
constexpr uint32_t kMaxTextureUploadSnapshotSize = 64u << 20;
constexpr uint32_t kMinTextureUploadSnapshotAddress = 0x10000u;
constexpr size_t kMaxPendingPixDecodedVideoFrames = 32;
constexpr size_t kMaxRecentSurfaceShapes = 512;
constexpr size_t kMaxDirectSnapshotCacheEntries = 2048;
constexpr size_t kMaxCachedDirectUploadBytes = 256u * 1024u;
constexpr uint32_t kMaxTrackedVertexDeclarationElements = 64;
constexpr uint32_t kMaxReasonableFableVertexDeclarationElements = 32;
constexpr uint32_t kFableObjectAddressMin = 0x40000000u;
constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
constexpr uint32_t kMaxTitleTextureUploadProbeLogs = 64;
constexpr uint32_t kMaxTitleTextureAliasUploadLogs = 12;
constexpr uint32_t kMaxTileHelperDeepProbeLogs = 24;
constexpr size_t kMaxPendingSurfaceViewTextureLinks = 256;
constexpr size_t kMaxPendingTextureIdentityOperations = 4096;
constexpr size_t kMaxPendingShaderDeclarationIdentityOperations = 4096;
constexpr size_t kMaxPendingPrivateTextureCopyActions = 4096;
constexpr size_t kMaxPendingExactTextureActions = 4096;
constexpr size_t kMaxRecentCpuTiledTextureUploads = 64;
constexpr uint32_t kMaxRecentCpuTiledTextureUploadBytes = 1024u * 1024u;
constexpr uint32_t kMaxRecentCpuTiledTextureUploadLogs = 64;
constexpr uint32_t kMaxZeroTextureRefreshBytes = 512u * 1024u;
constexpr uint32_t kMaxZeroTextureRefreshProbeBytes = 64u * 1024u;
constexpr uint32_t kMaxZeroTextureRefreshCandidatesPerHarvest = 8;
constexpr uint32_t kZeroTextureRefreshHarvestPeriod = 16;

void MarkResourceChangedLocked(uint32_t address, uint64_t serial) {
  if (address == 0) {
    return;
  }
  FableDirectGuestResourceRegistry().changed_serials[address] =
      std::max(FableDirectGuestResourceRegistry().changed_serials[address], serial);
  FableDirectGuestResourceRegistry().destroyed_serials.erase(address);
}

std::mutex g_direct_snapshot_cache_mutex;
std::unordered_map<uint32_t, FableGuestResourceSnapshot>
    g_direct_snapshot_cache;

bool ShouldRetainFablePrivateTitleCopyGraphOnDestroyLocked(uint32_t address);
void EraseDirectSnapshotCacheEntry(uint32_t address);
void RegisterShaderAndDeclarationIdentityForRecordLocked(
    const RendererResourceRecord& record);

void MarkResourceChangedLocked(const RendererResourceRecord& record) {
  const uint32_t object_address = GuestObjectAddress(record);
  const uint32_t address =
      object_address != 0 ? object_address : record.guest_address;
  MarkResourceChangedLocked(address, std::max(g_observer.serial, record.serial));
  EraseDirectSnapshotCacheEntry(address);
  RegisterShaderAndDeclarationIdentityForRecordLocked(record);
  if (IsImageResourceKind(GuestObjectKind(record))) {
    const GuestTextureRecord texture = GuestTextureRecordFor(record);
    EraseDirectSnapshotCacheEntry(texture.parent_address);
    EraseDirectSnapshotCacheEntry(texture.base_address);
  }
}

void MarkResourceDestroyedLocked(uint32_t address, uint64_t serial) {
  if (address == 0) {
    return;
  }
  const bool retain_private_title_copy_graph =
      ShouldRetainFablePrivateTitleCopyGraphOnDestroyLocked(address);
  if (!retain_private_title_copy_graph) {
    FableDirectForgetTextureIdentity(address);
    FableDirectForgetTextureFetchResourceLinksForResource(address);
    FableDirectForgetPrivateTextureCopyActionsForResource(address);
    FableDirectForgetSurfaceTextureLinksForResource(address);
  } else {
    static uint32_t logged_retained_private_title_copy_graph_destroys = 0;
    if (logged_retained_private_title_copy_graph_destroys < 16u) {
      REXLOG_INFO(
          "Refii renderer retained Fable private/title exact copy graph on "
          "destroy #{} address=0x{:08X} serial={}",
          ++logged_retained_private_title_copy_graph_destroys, address,
          static_cast<unsigned long long>(serial));
    }
  }
  FableDirectGuestResourceRegistry().destroyed_serials[address] =
      std::max(FableDirectGuestResourceRegistry().destroyed_serials[address], serial);
  FableDirectGuestResourceRegistry().changed_serials.erase(address);
  EraseDirectSnapshotCacheEntry(address);
  g_observer.exact_title_texture_refresh_candidates.erase(address);
  g_observer.exact_title_texture_blank_probes.erase(address);
  if (!retain_private_title_copy_graph) {
    FableDirectForgetPresentResource(address, address);
  }
}

void ResetObservedRenderStatesToDefaultsLocked() {
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};

  // Fable/Xenos defaults used as a baseline until the title writes explicit
  // state. This keeps explicit zero values, such as ColorWriteEnable=0 for
  // depth-only passes, meaningful instead of conflating them with uninitialized
  // state.
  render_states[kFableRenderStateSlotZFunc] = 3;  // LESSEQUAL
  render_states[kFableRenderStateSlotColorWriteEnable] = 0xFu;
  render_states[kFableRenderStateSlotAlphaBlendEnable] = 0;
  render_states[kFableRenderStateSlotSrcBlend] = 1;   // ONE
  render_states[kFableRenderStateSlotZWriteEnable] = 1;
  render_states[kFableRenderStateSlotDestBlend] = 0;  // ZERO
  render_states[kFableRenderStateSlotAlphaTestEnable] = 0;
  render_states[kFableRenderStateSlotZEnable] = 1;
  render_states[kFableRenderStateSlotHiZWriteEnable] = 0;
  render_states[kFableRenderStateSlotBlendOp] = 0;       // ADD
  render_states[kFableRenderStateSlotBlendOpAlpha] = 0;  // ADD
  render_states[kFableRenderStateSlotSrcBlendAlpha] = 1; // ONE
  render_states[kFableRenderStateSlotDestBlendAlpha] = 0; // ZERO
  render_states[kFableRenderStateSlotSeparateAlphaBlendEnable] = 0;
  render_states[kFableRenderStateSlotStencilFail] = 0;   // KEEP
  render_states[kFableRenderStateSlotStencilZFail] = 0;  // KEEP
  render_states[kFableRenderStateSlotStencilEnable] = 0;
  render_states[kFableRenderStateSlotCullMode] = 6;   // CCW cull
  render_states[kFableRenderStateSlotStencilPass] = 0;   // KEEP
  render_states[kFableRenderStateSlotStencilFunc] = 7;   // ALWAYS
  render_states[kFableRenderStateSlotStencilReadMask] = 0xFFu;
  render_states[kFableRenderStateSlotStencilRef] = 0;
  render_states[kFableRenderStateSlotAlphaFunc] = 6;
  render_states[kFableRenderStateSlotAlphaRef] = 0;
  render_states[kFableRenderStateSlotPixelCenterOffset] = 0;
  render_states[kFableRenderStateSlotStencilWriteMask] = 0xFFu;
  render_states[kFableRenderStateSlotPackedDepthModeBit2] = 0;
  render_states[kFableRenderStateSlotPackedDepthModeBit5] = 0;
  render_states[kFableRenderStateSlotPackedBlendControl20] = 0;
  g_observer.render_state_defaults_initialized = true;
  FableDirectSetRenderStateSnapshot(render_states);
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

bool IsReadableGuestByteRange(uint8_t* base, uint32_t guest_address,
                              uint32_t size);

uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return 0;
  }

  uint32_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap32(raw);
}

void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return;
  }

  const uint32_t raw = __builtin_bswap32(value);
  std::memcpy(base + guest_address + GuestPhysicalHostOffset(guest_address),
              &raw, sizeof(raw));
}

void StoreGuestU64(uint8_t* base, uint32_t guest_address, uint64_t value) {
  StoreGuestU32(base, guest_address, static_cast<uint32_t>(value >> 32u));
  StoreGuestU32(base, guest_address + sizeof(uint32_t),
                static_cast<uint32_t>(value));
}

void StoreGuestF32(uint8_t* base, uint32_t guest_address, float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreGuestU32(base, guest_address, bits);
}

void StoreGuestU8(uint8_t* base, uint32_t guest_address, uint8_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint8_t))) {
    return;
  }

  *(base + guest_address + GuestPhysicalHostOffset(guest_address)) = value;
}

uint16_t LoadGuestU16(uint8_t* base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint16_t))) {
    return 0;
  }

  uint16_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap16(raw);
}

uint16_t LoadGuestGpuU16(uint8_t* base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint16_t))) {
    return 0;
  }

  uint16_t value = 0;
  std::memcpy(&value,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(value));
  return value;
}

uint32_t LoadGuestGpuU32(uint8_t* base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return 0;
  }

  uint32_t value = 0;
  std::memcpy(&value,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(value));
  return value;
}

uint32_t LoadGuestGpuIndex(uint8_t* base, uint32_t guest_address,
                           uint32_t stride, bool byte_swap) {
  if (stride == 4u) {
    uint32_t value = LoadGuestGpuU32(base, guest_address);
    if (byte_swap) {
      value = __builtin_bswap32(value);
    }
    return value;
  }

  uint16_t value = LoadGuestGpuU16(base, guest_address);
  if (byte_swap) {
    value = __builtin_bswap16(value);
  }
  return value;
}

uint64_t LoadGuestU64(uint8_t* base, uint32_t guest_address) {
  const uint64_t high = LoadGuestU32(base, guest_address);
  const uint64_t low = LoadGuestU32(base, guest_address + sizeof(uint32_t));
  return (high << 32u) | low;
}

uint8_t LoadGuestU8(uint8_t* base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint8_t))) {
    return 0;
  }

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

double LoadGuestF64(uint8_t* base, uint32_t guest_address) {
  const uint64_t bits = LoadGuestU64(base, guest_address);
  double value = 0.0;
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

bool IsLikelyScratchBufferAddress(uint32_t guest_address) {
  if (guest_address < 0x1000u || guest_address == 0xFFFFFFFFu ||
      (guest_address & (alignof(uint32_t) - 1u)) != 0) {
    return false;
  }

  return guest_address < kFableObjectAddressMax || guest_address >= 0xE0000000u;
}

void StoreBigEndianDword(std::array<uint8_t, kFableSceneBatchInlineBytes>& data,
                         uint32_t offset, uint32_t value) {
  if (offset > data.size() || data.size() - offset < sizeof(uint32_t)) {
    return;
  }
  data[offset + 0u] = static_cast<uint8_t>(value >> 24u);
  data[offset + 1u] = static_cast<uint8_t>(value >> 16u);
  data[offset + 2u] = static_cast<uint8_t>(value >> 8u);
  data[offset + 3u] = static_cast<uint8_t>(value);
}

void StoreBigEndianQword(std::array<uint8_t, kFableSceneBatchInlineBytes>& data,
                         uint32_t offset, uint64_t value) {
  StoreBigEndianDword(data, offset, static_cast<uint32_t>(value >> 32u));
  StoreBigEndianDword(data, offset + sizeof(uint32_t),
                      static_cast<uint32_t>(value));
}

void BuildSceneBatchInlineRecord(uint8_t* base, const RendererHookArgs& args,
                                 uint32_t record_address,
                                 std::array<uint8_t, kFableSceneBatchInlineBytes>&
                                     data) {
  data.fill(0);
  if (base == nullptr || args.r4 == 0 || record_address == 0 ||
      !IsLikelyGuestDwordAddress(args.r4 + kFableSceneBatchCopiedBytes - 4u)) {
    return;
  }

  const uint64_t first_qword = LoadGuestU64(base, args.r4);
  const uint64_t second_qword = LoadGuestU64(base, args.r4 + 8u);
  StoreBigEndianDword(data, 0u, record_address);
  StoreBigEndianDword(data, 4u, static_cast<uint32_t>(first_qword));
  StoreBigEndianQword(data, 8u, second_qword);

  const uint32_t nested_record = static_cast<uint32_t>(second_qword >> 32u);
  if (IsLikelyGuestDwordAddress(nested_record + 28u)) {
    StoreBigEndianQword(data, 16u, LoadGuestU64(base, nested_record + 16u));
    StoreBigEndianQword(data, 24u, LoadGuestU64(base, nested_record + 24u));
  } else {
    StoreBigEndianQword(data, 16u, LoadGuestU64(base, args.r4 + 16u));
    StoreBigEndianQword(data, 24u, LoadGuestU64(base, args.r4 + 24u));
  }
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

struct ScratchBufferAttachment {
  bool index_buffer = false;
  uint32_t address = 0;
  uint32_t stride_or_format = 0;
  std::vector<uint8_t> data;
};

uint32_t FirstNonZeroByteOffset(const std::vector<uint8_t>& data,
                                uint32_t max_scan = 4096u) {
  const uint32_t limit =
      std::min<uint32_t>(static_cast<uint32_t>(data.size()), max_scan);
  for (uint32_t i = 0; i < limit; ++i) {
    if (data[i] != 0) {
      return i;
    }
  }
  return UINT32_MAX;
}

void AssignScratchAttachmentPrefix(ScratchBufferAttachment& attachment,
                                   const std::vector<uint8_t>& data,
                                   uint32_t required_size) {
  const uint32_t copy_size =
      std::min<uint32_t>(required_size, static_cast<uint32_t>(data.size()));
  attachment.data.assign(data.begin(), data.begin() + copy_size);
}

RendererResourceRecord* FindResourceRecordLocked(uint32_t address);

uint16_t LoadBigEndianU16(const uint8_t* data) {
  return static_cast<uint16_t>((static_cast<uint16_t>(data[0]) << 8u) |
                               static_cast<uint16_t>(data[1]));
}

uint32_t LoadBigEndianU32(const uint8_t* data) {
  return (static_cast<uint32_t>(data[0]) << 24u) |
         (static_cast<uint32_t>(data[1]) << 16u) |
         (static_cast<uint32_t>(data[2]) << 8u) |
         static_cast<uint32_t>(data[3]);
}

uint32_t FableVertexDeclarationElementByteSize(uint32_t type) {
  switch (type) {
    case 0x2C83A4:  // FLOAT1
    case 0x182886:  // D3DCOLOR
    case 0x1A2286:
    case 0x1A2386:
    case 0x1A2086:
    case 0x1A2186:
    case 0x2C82A1:
    case 0x2C83A1:
    case 0x1A2187:
    case 0x1A2287:
    case 0x2A2187:
    case 0x2A2287:
    case 0x2A2190:
    case 0x2A2390:
      return 4u;
    case 0x2C23A5:  // FLOAT2
    case 0x2C2359:
    case 0x2C2159:
    case 0x2C2059:
    case 0x2C235F:
    case 0x2C23A2:
      return 8u;
    case 0x2A23B9:  // FLOAT3
      return 12u;
    case 0x1A23A6:  // FLOAT4
    case 0x1A235A:
    case 0x1A215A:
    case 0x1A205A:
    case 0x1A2360:
    case 0x1A23A3:
      return 16u;
    default:
      return 0u;
  }
}

uint32_t FableVertexDeclarationSlotStrideLocked(uint32_t declaration_address,
                                                uint32_t slot) {
  if (declaration_address == 0) {
    return 0;
  }
  const RendererResourceRecord* declaration_record =
      FindResourceRecordLocked(declaration_address);
  if (!declaration_record ||
      GuestObjectKind(*declaration_record) !=
          RendererResourceKind::kVertexDeclaration ||
      declaration_record->upload_data.empty() ||
      declaration_record->upload_data.size() % 12u != 0) {
    return 0;
  }

  uint32_t stride = 0;
  const uint32_t element_count =
      static_cast<uint32_t>(declaration_record->upload_data.size() / 12u);
  for (uint32_t i = 0; i < element_count; ++i) {
    const uint8_t* element =
        declaration_record->upload_data.data() + static_cast<size_t>(i) * 12u;
    const uint16_t stream = LoadBigEndianU16(element + 0u);
    if (stream != slot) {
      continue;
    }
    const uint16_t offset = LoadBigEndianU16(element + 2u);
    const uint32_t type = LoadBigEndianU32(element + 4u);
    const uint32_t byte_size = FableVertexDeclarationElementByteSize(type);
    if (byte_size != 0) {
      stride = std::max<uint32_t>(stride, uint32_t(offset) + byte_size);
    }
  }
  return stride;
}

bool ResolveDrawEventStreamStridesLocked(RendererDrawEventSnapshot& event) {
  bool changed = false;
  for (uint32_t slot = 0; slot < std::size(event.streams); ++slot) {
    RendererStreamBindingSnapshot& stream = event.streams[slot];
    if (stream.buffer == 0 || stream.stride != 0) {
      continue;
    }

    const uint32_t declaration_stride =
        FableVertexDeclarationSlotStrideLocked(event.vertex_declaration, slot);
    if (declaration_stride == 0) {
      continue;
    }

    stream.stride = declaration_stride;
    changed = true;
  }
  return changed;
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

bool IsLikelyGuestByteRange(uint32_t guest_address, uint32_t size) {
  if (guest_address == 0 || size == 0 ||
      guest_address > UINT32_MAX - (size - 1u)) {
    return false;
  }

  const uint32_t end_address = guest_address + size - 1u;
  if (guest_address >= 0x1000u && end_address < kFableObjectAddressMax) {
    return true;
  }

  return guest_address >= 0x80000000u &&
         size <= kMaxScratchUploadSnapshotSize;
}

bool IsReadableHostByteRange(const uint8_t* data, size_t size) {
  if (!data || size == 0) {
    return false;
  }

  const uintptr_t begin = reinterpret_cast<uintptr_t>(data);
  if (begin > std::numeric_limits<uintptr_t>::max() - size) {
    return false;
  }

  const uintptr_t end = begin + size;
  uintptr_t current = begin;
#if defined(_WIN32)
  while (current < end) {
    MEMORY_BASIC_INFORMATION info = {};
    if (VirtualQuery(reinterpret_cast<const void*>(current), &info,
                     sizeof(info)) == 0) {
      return false;
    }

    const DWORD protect = info.Protect & 0xFFu;
    const bool readable =
        protect == PAGE_READONLY || protect == PAGE_READWRITE ||
        protect == PAGE_WRITECOPY || protect == PAGE_EXECUTE_READ ||
        protect == PAGE_EXECUTE_READWRITE ||
        protect == PAGE_EXECUTE_WRITECOPY;
    if (info.State != MEM_COMMIT || (info.Protect & PAGE_GUARD) != 0 ||
        !readable) {
      return false;
    }

    const uintptr_t region_begin =
        reinterpret_cast<uintptr_t>(info.BaseAddress);
    if (region_begin > std::numeric_limits<uintptr_t>::max() -
                           static_cast<uintptr_t>(info.RegionSize)) {
      return false;
    }
    const uintptr_t region_end =
        region_begin + static_cast<uintptr_t>(info.RegionSize);
    if (region_end <= current) {
      return false;
    }
    current = std::min(region_end, end);
  }
#elif defined(__linux__)
  thread_local uintptr_t cached_readable_begin = 0;
  thread_local uintptr_t cached_readable_end = 0;
  if (begin >= cached_readable_begin && end <= cached_readable_end) {
    return true;
  }

  std::ifstream maps("/proc/self/maps");
  if (!maps) {
    return false;
  }

  std::string line;
  while (current < end && std::getline(maps, line)) {
    const char* text = line.c_str();
    char* after_begin = nullptr;
    errno = 0;
    const unsigned long long region_begin_value =
        std::strtoull(text, &after_begin, 16);
    if (errno != 0 || after_begin == text || *after_begin != '-') {
      continue;
    }

    char* after_end = nullptr;
    errno = 0;
    const unsigned long long region_end_value =
        std::strtoull(after_begin + 1, &after_end, 16);
    if (errno != 0 || after_end == after_begin + 1 || *after_end != ' ') {
      continue;
    }
    while (*after_end == ' ') {
      ++after_end;
    }

    const uintptr_t region_begin =
        static_cast<uintptr_t>(region_begin_value);
    const uintptr_t region_end = static_cast<uintptr_t>(region_end_value);
    if (region_end <= current) {
      continue;
    }
    if (region_begin > current || after_end[0] != 'r') {
      return false;
    }
    if (current == begin && region_end >= end) {
      cached_readable_begin = region_begin;
      cached_readable_end = region_end;
      return true;
    }
    current = std::min(region_end, end);
  }

  if (current < end) {
    return false;
  }
#endif

  return true;
}

bool IsReadableGuestByteRange(uint8_t* base, uint32_t guest_address,
                              uint32_t size) {
  if (!base || !IsLikelyGuestByteRange(guest_address, size)) {
    return false;
  }

  const uint8_t* data =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  return IsReadableHostByteRange(data, size);
}

uint64_t HashGuestBytesFnv1a64(uint8_t* base, uint32_t guest_address,
                               uint32_t size) {
  if (!IsReadableGuestByteRange(base, guest_address, size)) {
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

uint64_t HashBytesFnv1a64(const uint8_t* data, size_t size) {
  if (!IsReadableHostByteRange(data, size)) {
    return 0;
  }

  uint64_t hash = 14695981039346656037ull;
  for (size_t i = 0; i < size; ++i) {
    hash ^= data[i];
    hash *= 1099511628211ull;
  }
  return hash;
}

uint64_t HashZeroBytesFnv1a64(size_t size) {
  if (size == 0) {
    return 0;
  }

  static std::mutex cache_mutex;
  static std::unordered_map<size_t, uint64_t> cache;
  {
    std::lock_guard<std::mutex> lock(cache_mutex);
    const auto it = cache.find(size);
    if (it != cache.end()) {
      return it->second;
    }
  }

  uint64_t hash = 14695981039346656037ull;
  for (size_t i = 0; i < size; ++i) {
    hash *= 1099511628211ull;
  }

  std::lock_guard<std::mutex> lock(cache_mutex);
  return cache.emplace(size, hash).first->second;
}

uint32_t FindFirstNonZeroGuestByte(uint8_t* base, uint32_t guest_address,
                                   uint32_t size, uint32_t max_scan) {
  if (!base || guest_address == 0 || size == 0 || max_scan == 0) {
    return UINT32_MAX;
  }

  const uint32_t scan_size = std::min(size, max_scan);
  if (!IsReadableGuestByteRange(base, guest_address, scan_size)) {
    return UINT32_MAX;
  }

  const uint8_t* data =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  for (uint32_t i = 0; i < scan_size; ++i) {
    if (data[i] != 0) {
      return i;
    }
  }
  return UINT32_MAX;
}

bool IsFableTitleTextureProbeAddress(uint32_t address) {
  const uint32_t normalized_address = NormalizeFableGpuAddress(address);
  switch (address) {
    case 0x40070210u:
    case 0x40070250u:
    case 0x40070310u:
    case 0x404BB310u:
    case 0x4C1312E0u:
    case 0x4C130E00u:
    case 0x4C130E34u:
    case 0x404BB350u:
    case 0x404BB490u:
    case 0x404BB4D0u:
    case 0x404DD2D0u:
    case 0x404DD310u:
    case 0x404DD350u:
    case 0x404DD390u:
    case 0x404DD3D0u:
    case 0x404DD450u:
    case 0x404DD490u:
    case 0x404DD4D0u:
    case 0x404DD510u:
    case 0x404DDC50u:
    case 0x404DDC90u:
    case 0x404DDB50u:
    case 0x404DDB90u:
    case 0x404DDD90u:
	    case 0x404DDDD0u:
	  case 0x404DE450u:
	  case 0x404DE490u:
	  case 0x404DE4D0u:
	  case 0x404DE510u:
    case 0x404DE550u:
    case 0x404DE610u:
    case 0x404DE690u:
    case 0x404DE6D0u:
    case 0x404DE710u:
    case 0x40513BD0u:
    case 0x40513C10u:
    case 0x40513CD0u:
    case 0x40513D10u:
    case 0x40513D90u:
    case 0x40513DD0u:
    case 0x4051B4D0u:
    case 0x4051BB90u:
    case 0x4051CB50u:
    case 0x4051CB90u:
    case 0x4051DB50u:
    case 0x4051DB90u:
    case 0x4051DC10u:
    case 0x4051F650u:
	    case 0x4051F610u:
	    case 0x4051FD90u:
    case 0x4051FF90u:
		    case 0x4C12C1C8u:
		    case 0x4C12C1FCu:
		    case 0x4C12C230u:
		    case 0x4C12C264u:
	    case 0x4C12C298u:
    case 0x4C12C300u:
    case 0x4C12C368u:
    case 0x4C131688u:
    case 0x4C1316BCu:
    case 0x4C132C70u:
    case 0x4C132CA4u:
    case 0x4C132CD8u:
    case 0x4C133150u:
    case 0x4C133184u:
    case 0x44127210u:
    case 0x44127278u:
    case 0x441272ACu:
    case 0x441272E0u:
    case 0x44127314u:
    case 0x44127348u:
    case 0x4412737Cu:
    case 0x44127480u:
    case 0x441274B4u:
    case 0x441274E8u:
    case 0x4412751Cu:
    case 0x13520000u:
    case 0x13530000u:
    case 0x13540000u:
    case 0x13550000u:
    case 0x13580000u:
	    case 0x13590000u:
	    case 0x16C40000u:
	    case 0x14740000u:
	    case 0x14760000u:
	    case 0x14780000u:
    case 0x147A0000u:
    case 0x15FE0000u:
    case 0x1A0D7000u:
    case 0x1A0D9000u:
    case 0x1A0EA000u:
    case 0x15800000u:
    case 0x1A0DB000u:
    case 0x1A0F9000u:
    case 0x1A0EC000u:
    case 0x1A0EE000u:
	  case 0x1A048000u:
	  case 0x1A07E000u:
	  case 0x12656000u:
    case 0x12681000u:
    case 0x12771000u:
    case 0x12759000u:
    case 0x12867000u:
    case 0x1289D000u:
    case 0x128D3000u:
    case 0x128D9000u:
    case 0x1293F000u:
    case 0x1FAB4000u:
    case 0x1FAB5000u:
      return true;
    default:
      break;
  }
  if (((normalized_address >= 0x4051C000u &&
        normalized_address < 0x40522000u) ||
       normalized_address == 0x40743590u) &&
      (normalized_address & (alignof(uint32_t) - 1u)) == 0) {
    return true;
  }
  if (normalized_address >= 0x44127000u &&
      normalized_address < 0x44128000u &&
      (normalized_address & (alignof(uint32_t) - 1u)) == 0) {
    return true;
  }
  if (normalized_address == address) {
    return false;
  }
  return IsFableTitleTextureProbeAddress(normalized_address);
}

bool IsFablePrivateTitleCopyDestinationAddress(uint32_t address) {
  switch (address) {
    case 0x40130C90u:
    case 0x40130CD0u:
    case 0x40131610u:
    case 0x40131650u:
    case 0x40131C90u:
    case 0x40131CD0u:
    case 0x40131D10u:
    case 0x40133CD0u:
    case 0x40133D10u:
    case 0x40137C90u:
    case 0x40137CD0u:
    case 0x40137D10u:
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

bool ShouldRetainFablePrivateTitleCopyGraphOnDestroyLocked(uint32_t address) {
  auto link_belongs_to_private_title_copy_graph =
      [](const FableSurfaceTextureLink& link) {
        if (!link.trusted_destination_edge) {
          return false;
        }
        return IsFablePrivateTitleCopyDestinationAddress(link.texture) ||
               IsFablePrivateTitleCopyDestinationAddress(
                   link.texture_base_address) ||
               IsFablePrivateTitleCopyDestinationAddress(
                   link.backing_texture) ||
               IsFablePrivateTitleCopyDestinationAddress(
                   link.backing_base_address);
      };
  auto action_belongs_to_private_title_copy_graph =
      [](const FablePrivateTextureCopyAction& action) {
        return IsFablePrivateTitleCopyDestinationAddress(
                   action.source_texture) ||
               IsFablePrivateTitleCopyDestinationAddress(
                   action.source_base_address) ||
               IsFablePrivateTitleCopyDestinationAddress(
                   action.backing_texture) ||
               IsFablePrivateTitleCopyDestinationAddress(
                   action.backing_base_address) ||
               IsFablePrivateTitleCopyDestinationAddress(
                   action.target_surface) ||
               IsFablePrivateTitleCopyDestinationAddress(action.render_target) ||
               IsFablePrivateTitleCopyDestinationAddress(action.depth_stencil);
      };
  auto has_private_title_link =
      [&](const std::vector<FableSurfaceTextureLink>* links) {
        if (!links) {
          return false;
        }
        for (const FableSurfaceTextureLink& link : *links) {
          if (link_belongs_to_private_title_copy_graph(link)) {
            return true;
          }
        }
        return false;
      };
  auto has_private_title_action =
      [&](const std::vector<FablePrivateTextureCopyAction>* actions) {
        if (!actions) {
          return false;
        }
        for (const FablePrivateTextureCopyAction& action : *actions) {
          if (action_belongs_to_private_title_copy_graph(action)) {
            return true;
          }
        }
        return false;
      };
  auto has_private_title_link_single =
      [&](const FableSurfaceTextureLink* link) {
        return link && link_belongs_to_private_title_copy_graph(*link);
      };
  auto has_private_title_action_single =
      [&](const FablePrivateTextureCopyAction* action) {
        return action && action_belongs_to_private_title_copy_graph(*action);
      };

  return has_private_title_link_single(
             FableDirectFindSurfaceTextureLinkForDestinationTexture(address)) ||
         has_private_title_link(
             FableDirectFindSurfaceTextureLinksForDestinationTexture(address)) ||
         has_private_title_link_single(
             FableDirectFindSurfaceTextureLinkByTexture(address)) ||
         has_private_title_link(
             FableDirectFindSurfaceTextureLinksByTexture(address)) ||
         has_private_title_link_single(
             FableDirectFindSurfaceTextureLinkBySurface(address)) ||
         has_private_title_link(
             FableDirectFindSurfaceTextureLinksBySurface(address)) ||
         has_private_title_link_single(
             FableDirectFindSurfaceTextureLinkByBase(address)) ||
         has_private_title_link(
             FableDirectFindSurfaceTextureLinksByBase(address)) ||
         has_private_title_action_single(
             FableDirectFindPrivateTextureCopyActionForDestinationTexture(
                 address)) ||
         has_private_title_action(
             FableDirectFindPrivateTextureCopyActionsForDestinationTexture(
                 address)) ||
         has_private_title_action_single(
             FableDirectFindPrivateTextureCopyActionBySource(address)) ||
         has_private_title_action(
             FableDirectFindPrivateTextureCopyActionsBySource(address)) ||
         has_private_title_action_single(
             FableDirectFindPrivateTextureCopyActionByTarget(address)) ||
         has_private_title_action(
             FableDirectFindPrivateTextureCopyActionsByTarget(address)) ||
         has_private_title_action_single(
             FableDirectFindPrivateTextureCopyActionByBase(address)) ||
         has_private_title_action(
             FableDirectFindPrivateTextureCopyActionsByBase(address));
}

bool IsFableTitleGuiTextAtlasBaseAddress(uint32_t address) {
  const uint32_t normalized_address = NormalizeFableGpuAddress(address);
  return normalized_address == 0x13520000u ||
         normalized_address == 0x13530000u ||
         normalized_address == 0x13540000u ||
	         normalized_address == 0x13580000u ||
	         normalized_address == 0x13590000u ||
	         normalized_address == 0x1A07E000u ||
	         normalized_address == 0x1A0D7000u ||
         normalized_address == 0x1A0D9000u ||
         normalized_address == 0x1A0EA000u ||
         normalized_address == 0x1FAB4000u ||
         normalized_address == 0x1FAB5000u;
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

bool IsTextureFetchMipLayoutSupportedForSnapshot(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t& fetch) {
  if (fetch.mip_min_level == 0) {
    return true;
  }

  // rex::graphics::TextureInfo asserts for this layout because it cannot tell
  // which address owns the requested mip window. When scanning live Fable
  // objects some non-texture records can still look texture-ish enough to get
  // here, so reject the snapshot before it reaches the lower-level assert.
  if (fetch.base_address != 0 && fetch.mip_address != 0 &&
      fetch.base_address != fetch.mip_address) {
    return false;
  }

  return true;
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

uint32_t DecodeFableGpuMemoryPointer(uint32_t pointer);

uint32_t EncodeFableHighGpuAliasForDecodedAddress(uint32_t decoded_address) {
  if (decoded_address < 0x1000u || decoded_address >= 0x20000000u) {
    return 0;
  }

  return 0xE0000000u | ((decoded_address - 0x1000u) & 0x1FFFFFFFu);
}

bool FindNonZeroHighGpuAliasForDecodedAddress(uint8_t* base,
                                              uint32_t decoded_address,
                                              uint32_t size,
                                              uint32_t& high_alias) {
  high_alias = 0;
  if (!base || size == 0) {
    return false;
  }

  const uint32_t alias =
      EncodeFableHighGpuAliasForDecodedAddress(decoded_address);
  if (alias == 0 ||
      DecodeFableGpuMemoryPointer(alias) != decoded_address ||
      !IsLikelyHighScratchByteBufferRange(alias, size)) {
    return false;
  }

  const uint32_t scan_size = std::min<uint32_t>(size, 1024u * 1024u);
  if (FindFirstNonZeroGuestByte(base, alias, size, scan_size) ==
      UINT32_MAX) {
    return false;
  }

  high_alias = alias;
  return true;
}

struct TextureFetchSnapshotInfo {
  uint32_t base_address = 0;
  uint32_t size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  const char* failure_reason = nullptr;
};

struct TextureLevelUploadShape {
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t byte_size = 0;
};

bool DecodeXGTextureLevelUploadShape(uint32_t width, uint32_t height,
                                     uint32_t level, uint32_t format,
                                     TextureLevelUploadShape& shape) {
  shape = {};
  if (width == 0 || height == 0) {
    return false;
  }

  const rex::graphics::FormatInfo* format_info =
      rex::graphics::FormatInfo::Get(
          static_cast<rex::graphics::xenos::TextureFormat>(format));
  if (!format_info || format_info->bytes_per_block() == 0 ||
      format_info->block_width == 0 || format_info->block_height == 0) {
    return false;
  }

  shape.width = std::max<uint32_t>(1u, width >> std::min(level, 31u));
  shape.height = std::max<uint32_t>(1u, height >> std::min(level, 31u));
  const uint32_t block_width = format_info->block_width;
  const uint32_t block_height = format_info->block_height;
  const uint32_t width_blocks =
      std::max<uint32_t>(1u, (shape.width + block_width - 1u) / block_width);
  const uint32_t height_blocks =
      std::max<uint32_t>(1u, (shape.height + block_height - 1u) / block_height);
  const uint64_t byte_size =
      uint64_t(width_blocks) * height_blocks * format_info->bytes_per_block();
  if (byte_size == 0 || byte_size > kMaxRecentCpuTiledTextureUploadBytes) {
    shape = {};
    return false;
  }
  shape.byte_size = static_cast<uint32_t>(byte_size);
  return true;
}

std::string_view FableHookDisplayName(std::string_view symbol) {
  if (const char* name = FableHookName(symbol)) {
    return name;
  }
  return symbol;
}

bool RememberExactTitleTextureRefreshCandidateLocked(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info,
    std::string_view producer,
    uint32_t producer_symbol_address = 0,
    uint32_t producer_lr = 0,
    uint32_t root_header = 0);
uint32_t ZeroTextureRefreshCandidateAddress(
    const RendererResourceRecord& record);
bool ExactTitleTextureRefreshCandidateMatchesRecord(
    const ExactTitleTextureRefreshCandidate& candidate,
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info);
bool StoreExactTitleTextureBlankProbeLocked(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info);

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
  if (static_cast<uint32_t>(fetch.format) >= 64u) {
    info.failure_reason = "unknown_format";
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
  if (!IsTextureFetchMipLayoutSupportedForSnapshot(fetch)) {
    info.failure_reason = "unsupported_mip_layout";
    return info;
  }

  texture_info::TextureInfo texture;
  if (!texture_info::TextureInfo::Prepare(fetch, &texture)) {
    info.failure_reason = "prepare_failed";
    return info;
  }

  info.format = static_cast<uint32_t>(fetch.format);
  switch (fetch.dimension) {
    case rex::graphics::xenos::DataDimension::k1D:
      info.width = fetch.size_1d.width + 1u;
      info.height = 1u;
      break;
    case rex::graphics::xenos::DataDimension::k3D:
      info.width = fetch.size_3d.width + 1u;
      info.height = fetch.size_3d.height + 1u;
      break;
    case rex::graphics::xenos::DataDimension::k2DOrStacked:
    case rex::graphics::xenos::DataDimension::kCube:
      info.width = fetch.size_2d.width + 1u;
      info.height = fetch.size_2d.height + 1u;
      break;
    default:
      break;
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

bool TileDestinationMatchesTextureFetchBase(uint32_t destination,
                                            const TextureFetchSnapshotInfo& info) {
  if (!IsUsableTextureFetchSnapshot(info) || destination == 0) {
    return false;
  }

  return destination == info.base_address ||
         DecodeFableGpuMemoryPointer(destination) == info.base_address;
}

bool TextureFetchSnapshotsMatchForAliasUpload(
    const TextureFetchSnapshotInfo& lhs, const TextureFetchSnapshotInfo& rhs) {
  return IsUsableTextureFetchSnapshot(lhs) &&
         IsUsableTextureFetchSnapshot(rhs) &&
         lhs.base_address == rhs.base_address && lhs.size == rhs.size &&
         lhs.width == rhs.width && lhs.height == rhs.height &&
         lhs.format == rhs.format;
}

bool IsNeutralTextureFetchSnapshot(const TextureFetchSnapshotInfo& info) {
  return IsUsableTextureFetchSnapshot(info) &&
         info.base_address == kFableNeutralTextureBaseAddress;
}

void LogFableTitleTextureUploadProbeLocked(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info, uint8_t* base,
    std::string_view source) {
  if (!base || !IsUsableTextureFetchSnapshot(info) ||
      g_observer.logged_title_texture_upload_probes >=
          kMaxTitleTextureUploadProbeLogs) {
    return;
  }

  const uint32_t object_address = GuestObjectAddress(record);
  if (!IsFableTitleTextureProbeAddress(object_address) &&
      !IsFableTitleTextureProbeAddress(info.base_address)) {
    return;
  }

  const uint32_t scan_size = std::min<uint32_t>(info.size, 1024u * 1024u);
  const uint32_t first_nonzero =
      FindFirstNonZeroGuestByte(base, info.base_address, info.size, scan_size);
  const uint32_t hash_size = std::min<uint32_t>(info.size, 64u * 1024u);
  const uint64_t hash =
      HashGuestBytesFnv1a64(base, info.base_address, hash_size);
  std::array<uint8_t, 4> first4 = {};
  if (IsReadableGuestByteRange(base, info.base_address,
                               std::min<uint32_t>(
                                   info.size,
                                   static_cast<uint32_t>(first4.size())))) {
    const uint8_t* bytes =
        base + info.base_address + GuestPhysicalHostOffset(info.base_address);
    for (uint32_t i = 0; i < first4.size() && i < info.size; ++i) {
      first4[i] = bytes[i];
    }
  }
  REXLOG_INFO(
      "Refii renderer title texture upload probe #{} source={} "
      "guest=0x{:08X} base=0x{:08X} size={} {}x{} fmt={} "
      "upload_gen={} record_size={} first_nz={} hash64k=0x{:016X} "
      "first4={:02X} {:02X} {:02X} {:02X}",
      g_observer.logged_title_texture_upload_probes + 1u, source,
      object_address, info.base_address, info.size, info.width, info.height,
      info.format, static_cast<unsigned long long>(record.upload_generation),
      record.upload_data.size(),
      first_nonzero == UINT32_MAX ? -1 : static_cast<int>(first_nonzero),
      hash, first4[0], first4[1], first4[2], first4[3]);
  ++g_observer.logged_title_texture_upload_probes;
}

std::array<uint32_t, 6> TextureFetchDwordsForRecord(
    const RendererResourceRecord& record) {
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const std::array<uint32_t, 6> raw_dwords =
      NormalizeTextureFetchDwords(texture.raw_fetch_dwords);
  if (!HasTextureFetchDwords(texture.normalized_fetch_dwords)) {
    return raw_dwords;
  }
  if (!HasTextureFetchDwords(raw_dwords)) {
    return texture.normalized_fetch_dwords;
  }

  const TextureFetchSnapshotInfo normalized_info =
      GetTextureFetchSnapshotInfo(texture.normalized_fetch_dwords);
  const TextureFetchSnapshotInfo raw_info =
      GetTextureFetchSnapshotInfo(raw_dwords);
  if (raw_info.base_address != 0 && raw_info.size != 0 &&
      (normalized_info.base_address == 0 || normalized_info.size == 0 ||
       normalized_info.base_address != raw_info.base_address ||
       normalized_info.size != raw_info.size)) {
    return raw_dwords;
  }
  return texture.normalized_fetch_dwords;
}

bool IsNeutralTextureResource(const RendererResourceRecord& record) {
  return GuestObjectKind(record) == RendererResourceKind::kTexture &&
         IsNeutralTextureFetchSnapshot(
             GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record)));
}

bool IsFableTitleTextureResource(const RendererResourceRecord& record) {
  if (GuestObjectKind(record) != RendererResourceKind::kTexture) {
    return false;
  }

  const TextureFetchSnapshotInfo info =
      GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record));
  return IsFableTitleTextureProbeAddress(GuestObjectAddress(record)) ||
         IsFableTitleTextureProbeAddress(info.base_address);
}

GuestTextureRecord TextureRecordWithFetchSnapshotIdentity(
    const RendererResourceRecord& record) {
  GuestTextureRecord texture = GuestTextureRecordFor(record);
  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords)) {
    return texture;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (!IsUsableTextureFetchSnapshot(info)) {
    return texture;
  }

  if (texture.base_address == 0) {
    texture.base_address = info.base_address;
  }
  if (texture.byte_size < info.size) {
    texture.byte_size = info.size;
  }
  if (!HasTextureFetchDwords(texture.raw_fetch_dwords)) {
    texture.raw_fetch_dwords = dwords;
  }
  if (!HasTextureFetchDwords(texture.normalized_fetch_dwords)) {
    texture.normalized_fetch_dwords = dwords;
  }
  return texture;
}

bool HasNonZeroUploadData(const RendererResourceRecord& record);
bool ShouldCaptureTextureUploadFromFetchDwords(
    const RendererResourceRecord& record,
    const std::array<uint32_t, 6>& dwords, uint8_t* base);
bool IsRecentCpuTiledTextureUploadShape(uint32_t width, uint32_t height);

bool IsAssetChunkHeaderLike(const uint8_t* data, size_t size) {
  if (!data || size < 4) {
    return false;
  }

  return (data[0] == 0x65u && data[1] == 0x73u && data[2] == 0x49u &&
          data[3] == 0x44u) ||
         (data[0] == 0x73u && data[1] == 0x65u && data[2] == 0x49u &&
          data[3] == 0x44u);
}

bool IsRecentCpuTiledTextureUploadUsableForTitleAlias(
    const RecentCpuTiledTextureUpload& upload,
    const TextureFetchSnapshotInfo& info) {
  if (!IsUsableTextureFetchSnapshot(info) || upload.data.size() < info.size ||
      upload.width != info.width || upload.height != info.height ||
      upload.first_nonzero == UINT32_MAX) {
    return false;
  }

  return std::any_of(upload.data.begin(),
                     upload.data.begin() + info.size,
                     [](uint8_t value) { return value != 0; });
}

bool IsLikelyCpuTiledTextureAliasInfo(const TextureFetchSnapshotInfo& info) {
  return IsUsableTextureFetchSnapshot(info) &&
         info.size <= kMaxRecentCpuTiledTextureUploadBytes &&
         IsRecentCpuTiledTextureUploadShape(info.width, info.height);
}

bool TextureFetchSnapshotMatchesRecentCpuTiledUpload(
    const RecentCpuTiledTextureUpload& upload,
    const TextureFetchSnapshotInfo& info) {
  if (!IsRecentCpuTiledTextureUploadUsableForTitleAlias(upload, info)) {
    return false;
  }

  const uint32_t decoded_destination =
      DecodeFableGpuMemoryPointer(upload.destination);
  return upload.destination == info.base_address ||
         decoded_destination == info.base_address;
}

void CopyPrivateTextureRectangleWrapperActionFieldsLocked(
    FablePrivateTextureCopyAction& action,
    const FablePrivateTextureCopyAction& wrapper_action);

void QueuePendingTextureIdentityOperation(
    PendingTextureIdentityOperation operation) {
  if (operation.object_texture == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(
      g_pending_texture_identity_operations_mutex);
  g_pending_texture_identity_operations.erase(
      std::remove_if(g_pending_texture_identity_operations.begin(),
                     g_pending_texture_identity_operations.end(),
                     [&](const PendingTextureIdentityOperation& pending) {
                       return pending.object_texture == operation.object_texture;
                     }),
      g_pending_texture_identity_operations.end());
  if (g_pending_texture_identity_operations.size() >=
      kMaxPendingTextureIdentityOperations) {
    g_pending_texture_identity_operations.erase(
        g_pending_texture_identity_operations.begin());
  }
  g_pending_texture_identity_operations.push_back(std::move(operation));
}

void QueuePendingTextureIdentityRegister(const FableTextureIdentity& identity) {
  PendingTextureIdentityOperation operation;
  operation.kind = PendingTextureIdentityOperationKind::kRegister;
  operation.object_texture = identity.object_texture;
  operation.identity = identity;
  QueuePendingTextureIdentityOperation(std::move(operation));
}

void QueuePendingTextureIdentityForget(uint32_t object_texture) {
  PendingTextureIdentityOperation operation;
  operation.kind = PendingTextureIdentityOperationKind::kForget;
  operation.object_texture = object_texture;
  QueuePendingTextureIdentityOperation(std::move(operation));
}

void ApplyTextureIdentityForgetWithRegistryLocked(uint32_t object_texture) {
  FableDirectForgetTextureIdentity(object_texture);
  FableDirectForgetTextureFetchResourceLinksForResource(object_texture);
}

void DrainPendingTextureIdentityOperations() {
  std::vector<PendingTextureIdentityOperation> pending;
  {
    std::lock_guard<std::mutex> lock(
        g_pending_texture_identity_operations_mutex);
    pending.swap(g_pending_texture_identity_operations);
  }

  for (const PendingTextureIdentityOperation& operation : pending) {
    if (operation.kind == PendingTextureIdentityOperationKind::kRegister) {
      FableDirectRegisterTextureIdentity(operation.identity);
    } else {
      ApplyTextureIdentityForgetWithRegistryLocked(operation.object_texture);
    }
  }
}

void QueuePendingShaderDeclarationIdentityOperations(
    std::vector<PendingShaderDeclarationIdentityOperation> operations) {
  if (operations.empty()) {
    return;
  }

  const uint32_t object_address = operations.front().object_address;
  if (object_address == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(
      g_pending_shader_declaration_identity_operations_mutex);
  g_pending_shader_declaration_identity_operations.erase(
      std::remove_if(
          g_pending_shader_declaration_identity_operations.begin(),
          g_pending_shader_declaration_identity_operations.end(),
          [object_address](
              const PendingShaderDeclarationIdentityOperation& pending) {
            return pending.object_address == object_address;
          }),
      g_pending_shader_declaration_identity_operations.end());
  while (g_pending_shader_declaration_identity_operations.size() +
             operations.size() >
         kMaxPendingShaderDeclarationIdentityOperations) {
    g_pending_shader_declaration_identity_operations.erase(
        g_pending_shader_declaration_identity_operations.begin());
  }
  g_pending_shader_declaration_identity_operations.insert(
      g_pending_shader_declaration_identity_operations.end(),
      std::make_move_iterator(operations.begin()),
      std::make_move_iterator(operations.end()));
}

void ApplyShaderDeclarationIdentityOperationWithRegistryLocked(
    const PendingShaderDeclarationIdentityOperation& operation) {
  switch (operation.kind) {
    case PendingShaderDeclarationIdentityOperationKind::kRegisterShader:
      FableDirectRegisterShaderIdentity(operation.shader);
      break;
    case PendingShaderDeclarationIdentityOperationKind::kForgetShader:
      FableDirectForgetShaderIdentity(operation.object_address);
      break;
    case PendingShaderDeclarationIdentityOperationKind::
        kRegisterVertexDeclaration:
      FableDirectRegisterVertexDeclarationIdentity(operation.declaration);
      break;
    case PendingShaderDeclarationIdentityOperationKind::
        kForgetVertexDeclaration:
      FableDirectForgetVertexDeclarationIdentity(operation.object_address);
      break;
  }
}

void DrainPendingShaderDeclarationIdentityOperations() {
  std::vector<PendingShaderDeclarationIdentityOperation> pending;
  {
    std::lock_guard<std::mutex> lock(
        g_pending_shader_declaration_identity_operations_mutex);
    pending.swap(g_pending_shader_declaration_identity_operations);
  }

  for (const PendingShaderDeclarationIdentityOperation& operation : pending) {
    ApplyShaderDeclarationIdentityOperationWithRegistryLocked(operation);
  }
}

void QueuePendingPrivateTextureCopyAction(
    const FablePrivateTextureCopyAction& action) {
  std::lock_guard<std::mutex> lock(
      g_pending_private_texture_copy_actions_mutex);
  if (g_pending_private_texture_copy_actions.size() >=
      kMaxPendingPrivateTextureCopyActions) {
    g_pending_private_texture_copy_actions.erase(
        g_pending_private_texture_copy_actions.begin());
  }
  PendingPrivateTextureCopyAction pending;
  pending.action = action;
  g_pending_private_texture_copy_actions.push_back(std::move(pending));
}

void DrainPendingPrivateTextureCopyActions() {
  std::vector<PendingPrivateTextureCopyAction> pending;
  {
    std::lock_guard<std::mutex> lock(
        g_pending_private_texture_copy_actions_mutex);
    pending.swap(g_pending_private_texture_copy_actions);
  }

  for (const PendingPrivateTextureCopyAction& operation : pending) {
    FableDirectRegisterPrivateTextureCopyAction(operation.action);
  }
}

void RegisterPrivateTextureCopyActionFromObserverLocked(
    const FablePrivateTextureCopyAction& action) {
  QueuePendingPrivateTextureCopyAction(action);
}

bool CopyRegisteredPrivateTextureWrapperActionFromRegistryLocked(
    FablePrivateTextureCopyAction& action) {
  std::unique_lock<std::recursive_mutex> registry_lock(
      FableDirectGuestResourceRegistryMutex(), std::try_to_lock);
  if (!registry_lock.owns_lock()) {
    return false;
  }

  auto copy_registered_wrapper_action =
      [&](const FablePrivateTextureCopyAction* wrapper_action) {
        if (!wrapper_action) {
          return false;
        }
        CopyPrivateTextureRectangleWrapperActionFieldsLocked(action,
                                                            *wrapper_action);
        return true;
      };

  if (action.wrapper_call_serial != 0 &&
      copy_registered_wrapper_action(
          FableDirectFindPrivateTextureCopyActionByWrapperCall(
              action.wrapper_call_serial))) {
    return true;
  }
  if (action.wrapper_context_key != 0 &&
      copy_registered_wrapper_action(
          FableDirectFindPrivateTextureCopyActionByWrapperContext(
              action.wrapper_context_key))) {
    return true;
  }
  return false;
}

bool CopyPrivateTextureWrapperActionFromRegistryLocked(
    uint64_t wrapper_call_serial, uint64_t wrapper_context_key,
    FablePrivateTextureCopyAction& action) {
  std::unique_lock<std::recursive_mutex> registry_lock(
      FableDirectGuestResourceRegistryMutex(), std::try_to_lock);
  if (!registry_lock.owns_lock()) {
    return false;
  }

  const FablePrivateTextureCopyAction* found_action = nullptr;
  if (wrapper_call_serial != 0) {
    found_action =
        FableDirectFindPrivateTextureCopyActionByWrapperCall(
            wrapper_call_serial);
  }
  if (!found_action && wrapper_context_key != 0) {
    found_action =
        FableDirectFindPrivateTextureCopyActionByWrapperContext(
            wrapper_context_key);
  }
  if (!found_action) {
    return false;
  }
  action = *found_action;
  return true;
}

void QueuePendingExactTextureHeaderAction(
    const FableTextureHeaderAction& action) {
  std::lock_guard<std::mutex> lock(g_pending_exact_texture_actions_mutex);
  if (g_pending_exact_texture_actions.size() >=
      kMaxPendingExactTextureActions) {
    g_pending_exact_texture_actions.erase(
        g_pending_exact_texture_actions.begin());
  }
  PendingExactTextureAction pending;
  pending.kind = PendingExactTextureActionKind::kHeader;
  pending.header = action;
  g_pending_exact_texture_actions.push_back(std::move(pending));
}

void QueuePendingExactTextureFetchBindingAction(
    const FableTextureFetchBindingAction& action) {
  std::lock_guard<std::mutex> lock(g_pending_exact_texture_actions_mutex);
  if (g_pending_exact_texture_actions.size() >=
      kMaxPendingExactTextureActions) {
    g_pending_exact_texture_actions.erase(
        g_pending_exact_texture_actions.begin());
  }
  PendingExactTextureAction pending;
  pending.kind = PendingExactTextureActionKind::kFetchBinding;
  pending.fetch_binding = action;
  g_pending_exact_texture_actions.push_back(std::move(pending));
}

void DrainPendingExactTextureActions() {
  std::vector<PendingExactTextureAction> pending;
  {
    std::lock_guard<std::mutex> lock(g_pending_exact_texture_actions_mutex);
    pending.swap(g_pending_exact_texture_actions);
  }

  for (const PendingExactTextureAction& action : pending) {
    if (action.kind == PendingExactTextureActionKind::kHeader) {
      FableDirectRegisterTextureHeaderAction(action.header);
    } else {
      FableDirectRegisterTextureFetchBindingAction(action.fetch_binding);
    }
  }
}

void RegisterExactTextureHeaderActionFromObserverLocked(
    const FableTextureHeaderAction& action) {
  QueuePendingExactTextureHeaderAction(action);
}

void RegisterExactTextureFetchBindingActionFromObserverLocked(
    const FableTextureFetchBindingAction& action) {
  QueuePendingExactTextureFetchBindingAction(action);
}

bool CopyTextureHeaderActionForFetchFromRegistryLocked(
    uint32_t fetch_address, const std::array<uint32_t, 6>& dwords,
    FableTextureHeaderAction& action) {
  std::unique_lock<std::recursive_mutex> registry_lock(
      FableDirectGuestResourceRegistryMutex(), std::try_to_lock);
  if (registry_lock.owns_lock()) {
    const FableTextureHeaderAction* found =
        FableDirectFindTextureHeaderActionForFetch(fetch_address, dwords);
    if (found) {
      action = *found;
      return true;
    }
  }

  std::lock_guard<std::mutex> pending_lock(
      g_pending_exact_texture_actions_mutex);
  for (auto it = g_pending_exact_texture_actions.rbegin();
       it != g_pending_exact_texture_actions.rend(); ++it) {
    if (it->kind == PendingExactTextureActionKind::kHeader &&
        it->header.fetch_address == fetch_address &&
        it->header.fetch_dwords == dwords) {
      action = it->header;
      return true;
    }
  }
  return false;
}

void RegisterTextureIdentityForRecordLocked(const RendererResourceRecord& record);
void QueuePendingTextureIdentityRegister(const FableTextureIdentity& identity);
void QueuePendingTextureIdentityForget(uint32_t object_texture);
void DrainPendingTextureIdentityOperations();
void DrainPendingShaderDeclarationIdentityOperations();
void DrainPendingPrivateTextureCopyActions();
void RegisterPrivateTextureCopyActionFromObserverLocked(
    const FablePrivateTextureCopyAction& action);
bool CopyRegisteredPrivateTextureWrapperActionFromRegistryLocked(
    FablePrivateTextureCopyAction& action);
bool CopyPrivateTextureWrapperActionFromRegistryLocked(
    uint64_t wrapper_call_serial, uint64_t wrapper_context_key,
    FablePrivateTextureCopyAction& action);
void DrainPendingExactTextureActions();
void RegisterExactTextureHeaderActionFromObserverLocked(
    const FableTextureHeaderAction& action);
void RegisterExactTextureFetchBindingActionFromObserverLocked(
    const FableTextureFetchBindingAction& action);
bool CopyTextureHeaderActionForFetchFromRegistryLocked(
    uint32_t fetch_address, const std::array<uint32_t, 6>& dwords,
    FableTextureHeaderAction& action);
void StoreTextureObjectFetchDwordsLocked(uint32_t texture_object,
                                         uint32_t source_address,
                                         uint32_t texture_slot,
                                         uint8_t* base, uint32_t log_lr,
                                         bool refresh_upload,
                                         bool update_existing_draws);
void RegisterExactTextureFetchBindingActionLocked(
    const RendererHookArgs& args, uint8_t* base, uint32_t texture_slot,
    uint32_t source_address, const std::array<uint32_t, 6>& dwords,
    uint32_t binding_symbol_address = 0x82BA77D0u);
void RegisterExactTextureHeaderActionLocked(
    std::string_view symbol, const RendererHookArgs& args,
    uint32_t texture_header, uint32_t fetch_address,
    const std::array<uint32_t, 6>& dwords,
    const TextureFetchSnapshotInfo& fetch_info, bool stack_scratch,
    uint32_t texture_object, uint32_t parent_texture);
void UpdateDirectSnapshotCacheLocked(const RendererResourceRecord& record);

uint32_t TextureSnapshotSizeForRecord(const RendererResourceRecord& record) {
  if (!IsImageResourceKind(GuestObjectKind(record))) {
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
  if (!IsImageResourceKind(GuestObjectKind(record)) ||
      slot >= std::size(event.textures)) {
    return false;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const uint32_t object_address = GuestObjectAddress(record);
  const uint32_t parent_address = GuestObjectParentAddress(record);
  const uint32_t texture_object = event.textures[slot];
  if (texture_object != 0 &&
      (texture_object == object_address ||
       texture_object == parent_address ||
       texture_object == texture.base_address)) {
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

  if (slot_info.base_address == object_address ||
      slot_info.base_address == parent_address ||
      slot_info.base_address == texture.base_address) {
    return true;
  }

  const TextureFetchSnapshotInfo record_info =
      GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record));
  return record_info.base_address != 0 &&
         slot_info.base_address == record_info.base_address;
}

void RefreshQueuedDrawTextureResourceSerialLocked(
    const RendererResourceRecord& record) {
  if (!IsImageResourceKind(GuestObjectKind(record)) || record.serial == 0) {
    return;
  }

  FableDirectPatchPendingDrawEvents([&](RendererDrawEventSnapshot& event) {
    for (uint32_t slot = 0; slot < std::size(event.textures); ++slot) {
      if (DrawTextureSlotReferencesResource(event, slot, record)) {
        event.resource_serial =
            std::max(event.resource_serial, record.serial);
        return true;
      }
    }
    return false;
  });
}

bool DrawEventReferencesBufferResource(const RendererDrawEventSnapshot& event,
                                       const RendererResourceRecord& record) {
  if (!IsBufferResourceKind(GuestObjectKind(record))) {
    return false;
  }

  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  const uint32_t object_address = GuestObjectAddress(record);
  const uint32_t parent_address = GuestObjectParentAddress(record);
  const auto matches_record_address = [&](uint32_t address) {
    return address != 0 &&
           (address == object_address ||
            address == parent_address ||
            address == buffer.gpu_address ||
            address == buffer.header_address);
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

FableDirectResourceReference DirectResourceReferenceForRecord(
    const RendererResourceRecord& record) {
  FableDirectResourceReference direct_reference;
  direct_reference.guest_address = GuestObjectAddress(record);
  direct_reference.parent_address = GuestObjectParentAddress(record);
  direct_reference.data_address = GuestResourceDataAddress(record);
  direct_reference.auxiliary_address = GuestResourceAuxiliaryAddress(record);
  direct_reference.byte_size = GuestResourceByteSize(record);
  direct_reference.buffer_resource = IsBufferResourceKind(GuestObjectKind(record));
  return direct_reference;
}

bool PendingDrawEventsReferenceResourceLocked(
    const RendererResourceRecord& record) {
  return FableDirectDrawEventsReferenceResource(
      DirectResourceReferenceForRecord(record));
}

bool BoundSurfaceStateReferencesResourceLocked(
    const RendererResourceRecord& record) {
  if (!IsSurfaceResourceKind(GuestObjectKind(record))) {
    return false;
  }

  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  if (ResourceRecordMatchesAddress(record, direct_snapshot.targets.depth)) {
    return true;
  }

  return std::any_of(
      direct_snapshot.targets.color.begin(),
      direct_snapshot.targets.color.end(), [&](uint32_t render_target) {
        return ResourceRecordMatchesAddress(record, render_target);
      });
}

bool BoundPipelineStateReferencesResourceLocked(
    const RendererResourceRecord& record) {
  const RendererResourceKind kind = GuestObjectKind(record);
  if (!IsShaderResourceKind(kind) &&
      kind != RendererResourceKind::kVertexDeclaration) {
    return false;
  }

  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  if (kind == RendererResourceKind::kVertexShader) {
    return ResourceRecordMatchesAddress(record,
                                        direct_snapshot.shaders.vertex_shader);
  }
  if (kind == RendererResourceKind::kPixelShader) {
    return ResourceRecordMatchesAddress(record,
                                        direct_snapshot.shaders.pixel_shader);
  }
  return ResourceRecordMatchesAddress(
      record, direct_snapshot.shaders.vertex_declaration);
}

bool ActiveSurfaceStateReferencesResourceLocked(
    const RendererResourceRecord& record) {
  return BoundSurfaceStateReferencesResourceLocked(record) ||
         PendingDrawEventsReferenceResourceLocked(record);
}

bool ActivePipelineStateReferencesResourceLocked(
    const RendererResourceRecord& record) {
  return BoundPipelineStateReferencesResourceLocked(record) ||
         PendingDrawEventsReferenceResourceLocked(record);
}

void FlushDeferredSurfaceReleasesLocked() {
  for (auto it = g_observer.deferred_surface_releases.begin();
       it != g_observer.deferred_surface_releases.end();) {
    auto resource_it = FableDirectGuestResourceRegistry().resources.find(*it);
    if (resource_it == FableDirectGuestResourceRegistry().resources.end()) {
      it = g_observer.deferred_surface_releases.erase(it);
      continue;
    }

    if (ActiveSurfaceStateReferencesResourceLocked(resource_it->second) ||
        ActivePipelineStateReferencesResourceLocked(resource_it->second)) {
      ++it;
      continue;
    }

    RememberRecentSurfaceShapeLocked(resource_it->second);
    const uint32_t destroyed_address = GuestObjectAddress(resource_it->second);
    FableDirectGuestResourceRegistry().resources.erase(resource_it);
    ++FableDirectGuestResourceRegistry().destroys;
    ++g_observer.serial;
    MarkResourceDestroyedLocked(destroyed_address, g_observer.serial);
    it = g_observer.deferred_surface_releases.erase(it);
  }
}

void RefreshQueuedDrawBufferResourceSerialLocked(
    const RendererResourceRecord& record) {
  if (!IsBufferResourceKind(GuestObjectKind(record)) || record.serial == 0) {
    return;
  }

  FableDirectPatchPendingDrawEvents([&](RendererDrawEventSnapshot& event) {
    if (DrawEventReferencesBufferResource(event, record)) {
      event.resource_serial = std::max(event.resource_serial, record.serial);
      return true;
    }
    return false;
  });
}

void RefreshDrawEventResourceSerialFromBindingsLocked(
    RendererDrawEventSnapshot& event) {
  auto bump_from_address = [&](uint32_t address) {
    RendererResourceRecord* record = FindResourceRecordLocked(address);
    if (record != nullptr && record->serial != 0) {
      event.resource_serial = std::max(event.resource_serial, record->serial);
    }
  };
  auto bump_from_texture_slot = [&](uint32_t slot) {
    if (slot >= std::size(event.textures)) {
      return;
    }

    bump_from_address(event.textures[slot]);

    constexpr uint32_t kTextureFetchConstantDwords = 6;
    const uint32_t fetch_dword = slot * kTextureFetchConstantDwords;
    if (fetch_dword + kTextureFetchConstantDwords >
        event.fetch_constants.size()) {
      return;
    }

    std::array<uint32_t, kTextureFetchConstantDwords> slot_fetch = {};
    std::copy_n(event.fetch_constants.begin() + fetch_dword,
                kTextureFetchConstantDwords, slot_fetch.begin());
    const TextureFetchSnapshotInfo slot_info =
        GetTextureFetchSnapshotInfo(slot_fetch);
    bump_from_address(slot_info.base_address);
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
    bump_from_texture_slot(slot);
  }
}

bool CaptureTextureUploadFromFetchDwords(RendererResourceRecord& record,
                                         const std::array<uint32_t, 6>& dwords,
                                         uint8_t* base) {
  if (!base || GuestObjectKind(record) != RendererResourceKind::kTexture ||
      !HasTextureFetchDwords(dwords)) {
    return false;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  const bool source_range_valid =
      info.base_address >= kMinTextureUploadSnapshotAddress &&
      info.size <= kMaxTextureUploadSnapshotSize &&
      info.base_address <= UINT32_MAX - info.size &&
      IsReadableGuestByteRange(base, info.base_address, info.size);
  if (info.base_address == 0 || info.size == 0 || !source_range_valid) {
    if (RendererResourceTracingEnabled() &&
        g_observer.logged_texture_capture_failures < 8) {
      REXLOG_INFO(
          "Refii renderer exact texture upload capture reject reason={} "
          "object=0x{:08X} base=0x{:08X} size={} dwords={:08X} {:08X} "
          "{:08X} {:08X} {:08X} {:08X}",
          info.failure_reason ? info.failure_reason
                              : (source_range_valid ? "unknown"
                                                    : "unsafe_or_unmapped_range"),
          GuestObjectAddress(record), info.base_address, info.size, dwords[0],
          dwords[1], dwords[2], dwords[3], dwords[4], dwords[5]);
      ++g_observer.logged_texture_capture_failures;
    }
    return false;
  }

  const uint32_t object_address = GuestObjectAddress(record);
  const bool title_related =
      IsFableTitleTextureProbeAddress(object_address) ||
      IsFableTitleTextureProbeAddress(info.base_address);
  uint32_t source_address = info.base_address;
  bool source_uses_high_alias = false;
  const uint32_t scan_size = std::min<uint32_t>(info.size, 1024u * 1024u);
  const bool source_base_is_blank =
      FindFirstNonZeroGuestByte(base, info.base_address, info.size,
                                scan_size) == UINT32_MAX;
  if (title_related && source_base_is_blank) {
    uint32_t high_alias = 0;
    if (FindNonZeroHighGpuAliasForDecodedAddress(
            base, info.base_address, info.size, high_alias)) {
      source_address = high_alias;
      source_uses_high_alias = true;
    }
  }
  const uint8_t* source =
      base + source_address + GuestPhysicalHostOffset(source_address);
  const bool existing_upload_nonzero =
      std::any_of(record.upload_data.begin(), record.upload_data.end(),
                  [](uint8_t value) { return value != 0; });
  if (title_related && existing_upload_nonzero) {
    if (source_base_is_blank && !source_uses_high_alias) {
      if (RendererResourceTracingEnabled() &&
          g_observer.logged_title_texture_upload_probes <
              kMaxTitleTextureUploadProbeLogs) {
        REXLOG_INFO(
            "Refii renderer preserved nonzero title texture upload #{} "
            "guest=0x{:08X} blank_base=0x{:08X} size={} {}x{} fmt={} "
            "upload_gen={} record_size={}",
            g_observer.logged_title_texture_upload_probes + 1u,
            object_address, info.base_address, info.size, info.width,
            info.height, info.format,
            static_cast<unsigned long long>(record.upload_generation),
            record.upload_data.size());
        ++g_observer.logged_title_texture_upload_probes;
      }
      return true;
    }
  }
  if (title_related && !source_uses_high_alias) {
    if (source_base_is_blank) {
      const uint32_t candidate_address =
          ZeroTextureRefreshCandidateAddress(record);
      const auto exact_candidate =
          g_observer.exact_title_texture_refresh_candidates.find(
              candidate_address);
      const bool exact_candidate_matches =
          exact_candidate !=
              g_observer.exact_title_texture_refresh_candidates.end() &&
          ExactTitleTextureRefreshCandidateMatchesRecord(
              exact_candidate->second, record, info);
      const bool exact_blank_probe =
          exact_candidate_matches ||
          StoreExactTitleTextureBlankProbeLocked(record, info);
      if (RendererResourceTracingEnabled() &&
          g_observer.logged_title_texture_upload_probes <
              kMaxTitleTextureUploadProbeLogs) {
        REXLOG_INFO(
          "Refii renderer {} blank title texture upload #{} "
            "guest=0x{:08X} base=0x{:08X} size={} {}x{} fmt={} "
            "record_size={}",
            exact_candidate_matches
                ? "exact wait for"
                : (exact_blank_probe ? "observed exact-producer-wait for"
                                     : "rejected unproven"),
            g_observer.logged_title_texture_upload_probes + 1u,
            object_address, info.base_address, info.size, info.width,
            info.height, info.format, record.upload_data.size());
        ++g_observer.logged_title_texture_upload_probes;
      }
      return false;
    }
  }
  if (!IsReadableGuestByteRange(base, source_address, info.size)) {
    return false;
  }
  if (record.upload_data.size() == info.size &&
      std::memcmp(record.upload_data.data(), source, info.size) == 0) {
    LogFableTitleTextureUploadProbeLocked(record, info, base, "unchanged");
    return true;
  }

  record.upload_data.resize(info.size);
  std::memcpy(record.upload_data.data(), source, info.size);
  record.raw.hash =
      HashBytesFnv1a64(record.upload_data.data(), record.upload_data.size());
  GuestTextureRecord texture = GuestTextureRecordFor(record);
  texture.byte_size = std::max<uint32_t>(texture.byte_size, info.size);
  texture.upload_generation = record.upload_generation + 1u;
  ApplyGuestTextureRecord(record, texture);
  if (RendererResourceTracingEnabled() &&
      g_observer.logged_texture_capture_successes < 8) {
    REXLOG_INFO(
        "Refii renderer texture upload capture copied guest=0x{:08X} "
        "source=0x{:08X} size={} generation={}",
        GuestObjectAddress(record), source_address, info.size,
        record.upload_generation);
    ++g_observer.logged_texture_capture_successes;
  }
  if (source_uses_high_alias && RendererResourceTracingEnabled() &&
      g_observer.logged_title_texture_upload_probes <
          kMaxTitleTextureUploadProbeLogs) {
    REXLOG_INFO(
        "Refii renderer copied title texture from high GPU alias #{} "
        "guest=0x{:08X} base=0x{:08X} alias=0x{:08X} size={} {}x{} fmt={} "
        "generation={}",
        g_observer.logged_title_texture_upload_probes + 1u, object_address,
        info.base_address, source_address, info.size, info.width, info.height,
        info.format, static_cast<unsigned long long>(record.upload_generation));
    ++g_observer.logged_title_texture_upload_probes;
  }
  LogFableTitleTextureUploadProbeLocked(record, info, base, "copied");
  return true;
}

bool IsFableCpuTextureLayoutHelperHook(std::string_view symbol) {
  return symbol == "sub_8300C290" || // XGRAPHICS__TileSurface
         symbol == "sub_8300CB40" || // j_XGRAPHICS__TileSurface
         symbol == "sub_8300CB48" || // XGTileTextureLevel
         symbol == "sub_83010E60" || // XGRAPHICS__TileVolume
         symbol == "sub_830117B8" || // XGTileVolumeTextureLevel
         symbol == "sub_8300C660" || // XGRAPHICS__UntileSurface
         symbol == "sub_8300CDA0" || // j_XGRAPHICS__UntileSurface
         symbol == "sub_8300CDA8" || // XGUntileTextureLevel
         symbol == "sub_830112F0" || // XGRAPHICS__UntileVolume
         symbol == "sub_83011AE0";   // XGUntileVolumeTextureLevel
}

void RefreshTextureUploadsAfterCpuTextureLayoutHelperLocked(
    std::string_view symbol, uint8_t* base) {
  if (!base) {
    return;
  }

  uint32_t refreshed_textures = 0;
  for (auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    if (GuestObjectKind(record) != RendererResourceKind::kTexture) {
      continue;
    }

    const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
    if (!HasTextureFetchDwords(dwords)) {
      continue;
    }

    const uint64_t previous_generation = record.upload_generation;
    if (!ShouldCaptureTextureUploadFromFetchDwords(record, dwords, base) ||
        !CaptureTextureUploadFromFetchDwords(record, dwords, base) ||
        record.upload_generation == previous_generation) {
      continue;
    }

    RegisterTextureIdentityForRecordLocked(record);
    record.serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = record;
    MarkResourceChangedLocked(record);
    RefreshQueuedDrawTextureResourceSerialLocked(record);
    ++refreshed_textures;
  }

  if (refreshed_textures != 0 && RendererResourceTracingEnabled()) {
    REXLOG_INFO(
        "Refii renderer refreshed {} texture upload(s) after Fable CPU "
        "layout helper {}",
        refreshed_textures, symbol);
  }
}

void RefreshTextureUploadsAfterCpuSurfaceCopyLocked(std::string_view symbol,
                                                    uint8_t* base) {
  if (!base) {
    return;
  }

  uint32_t refreshed_textures = 0;
  for (auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    if (GuestObjectKind(record) != RendererResourceKind::kTexture) {
      continue;
    }

    const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
    if (!HasTextureFetchDwords(dwords)) {
      continue;
    }

    const uint64_t previous_generation = record.upload_generation;
    if (!ShouldCaptureTextureUploadFromFetchDwords(record, dwords, base) ||
        !CaptureTextureUploadFromFetchDwords(record, dwords, base) ||
        record.upload_generation == previous_generation) {
      continue;
    }

    RegisterTextureIdentityForRecordLocked(record);
    record.serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = record;
    MarkResourceChangedLocked(record);
    RefreshQueuedDrawTextureResourceSerialLocked(record);
    ++refreshed_textures;
  }

  if (refreshed_textures != 0 && RendererResourceTracingEnabled()) {
    REXLOG_INFO(
        "Refii renderer refreshed {} texture upload(s) after Fable CPU "
        "surface copy {}",
        refreshed_textures, symbol);
  }
}

bool RefreshTextureUploadFromPendingPixDecodedFrameLocked(
    RendererResourceRecord& record, const std::array<uint32_t, 6>& dwords,
    uint8_t* base) {
  if (!base || GuestObjectKind(record) != RendererResourceKind::kTexture ||
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
  FableDirectGuestResourceRegistry().last_resource = record;
  MarkResourceChangedLocked(record);
  RefreshQueuedDrawTextureResourceSerialLocked(record);
  return true;
}


void SanitizeImageResourceParent(RendererResourceRecord& record) {
  if (!IsImageResourceKind(GuestObjectKind(record))) {
    return;
  }
  if (IsLikelyFableCodeAddress(GuestObjectParentAddress(record))) {
    SetGuestResourceParent(record, 0);
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

bool CopyGuestBufferUploadData(RendererResourceRecord& record, uint8_t* base,
                               uint32_t guest_address, uint32_t size) {
  const RendererResourceKind kind = GuestObjectKind(record);
  if (!base || !IsUploadResourceKind(kind) || guest_address == 0 || size == 0) {
    return false;
  }

  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const uint32_t typed_size =
      IsBufferResourceKind(kind) ? buffer.byte_size : texture.byte_size;
  const uint32_t record_size = typed_size != 0 ? typed_size : size;
  const uint32_t copy_size = std::min(size, record_size);
  const uint32_t max_upload_size =
      IsImageResourceKind(kind) ? kMaxTextureUploadSnapshotSize
                                : kMaxScratchUploadSnapshotSize;
  if (copy_size == 0 || copy_size > max_upload_size) {
    return false;
  }
  if (!IsReadableGuestByteRange(base, guest_address, copy_size)) {
    return false;
  }

  const uint8_t* src =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  const bool image_resource = IsImageResourceKind(kind);
  const uint64_t image_source_hash =
      image_resource ? HashBytesFnv1a64(src, copy_size) : 0;
  if (record.upload_data.size() == copy_size) {
    const uint8_t* current = record.upload_data.data();
    bool unchanged = true;
    if (image_resource) {
      unchanged = record.raw.hash != 0 && record.raw.hash == image_source_hash;
      if (!unchanged && record.raw.hash == 0) {
        unchanged = std::memcmp(current, src, copy_size) == 0;
      }
    } else if (kind == RendererResourceKind::kIndexBuffer &&
               buffer.stride_or_index_format == 2u) {
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

  if (image_resource) {
    std::memcpy(dst, src, copy_size);
  } else if (kind == RendererResourceKind::kIndexBuffer &&
             buffer.stride_or_index_format == 2u) {
    copy_swapped16();
  } else {
    copy_swapped32();
  }

  ++record.upload_generation;
  record.raw.hash =
      image_resource ? image_source_hash
                     : HashBytesFnv1a64(record.upload_data.data(),
                                        record.upload_data.size());
  return true;
}

std::vector<uint8_t> CopyGuestScratchUploadForDirectInline(
    uint8_t* base, uint32_t guest_address, uint32_t size,
    RendererResourceKind kind, uint32_t stride_or_format) {
  std::vector<uint8_t> data;
  if (!base || guest_address == 0 || size == 0 ||
      size > kMaxScratchUploadSnapshotSize) {
    return data;
  }

  const uint8_t* src =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  data.resize(size);
  uint8_t* dst = data.data();

  if (kind == RendererResourceKind::kIndexBuffer && stride_or_format == 2u) {
    uint32_t offset = 0;
    for (; offset + 1u < size; offset += 2u) {
      dst[offset + 0u] = src[offset + 1u];
      dst[offset + 1u] = src[offset + 0u];
    }
    if (offset < size) {
      dst[offset] = src[offset];
    }
    return data;
  }

  uint32_t offset = 0;
  for (; offset + 3u < size; offset += 4u) {
    dst[offset + 0u] = src[offset + 3u];
    dst[offset + 1u] = src[offset + 2u];
    dst[offset + 2u] = src[offset + 1u];
    dst[offset + 3u] = src[offset + 0u];
  }
  for (; offset < size; ++offset) {
    dst[offset] = src[offset];
  }
  return data;
}

bool HasNonZeroUploadData(const RendererResourceRecord& record) {
  if (record.upload_data.empty()) {
    return false;
  }
  if (record.raw.hash != 0) {
    return record.raw.hash != HashZeroBytesFnv1a64(record.upload_data.size());
  }
  return std::any_of(record.upload_data.begin(), record.upload_data.end(),
                     [](uint8_t value) { return value != 0; });
}

bool HasNonZeroUploadPrefixData(const RendererResourceRecord& record,
                                uint32_t max_scan) {
  if (record.upload_data.empty() || max_scan == 0) {
    return false;
  }

  const size_t scan_size =
      std::min<size_t>(record.upload_data.size(), max_scan);
  return std::any_of(record.upload_data.begin(),
                     record.upload_data.begin() + scan_size,
                     [](uint8_t value) { return value != 0; });
}

bool ShouldCaptureTextureUploadFromFetchDwords(
    const RendererResourceRecord& record,
    const std::array<uint32_t, 6>& dwords, uint8_t* base) {
  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (!IsUsableTextureFetchSnapshot(info) ||
      info.size > kMaxTextureUploadSnapshotSize) {
    return false;
  }

  const uint32_t probe_size =
      std::min<uint32_t>(info.size, kMaxZeroTextureRefreshProbeBytes);
  const uint32_t object_address = GuestObjectAddress(record);
  const bool title_related =
      IsFableTitleTextureProbeAddress(object_address) ||
      IsFableTitleTextureProbeAddress(info.base_address);
  const bool source_base_nonzero =
      FindFirstNonZeroGuestByte(base, info.base_address, info.size,
                                probe_size) != UINT32_MAX;
  if (source_base_nonzero) {
    return true;
  }

  if (title_related) {
    uint32_t high_alias = 0;
    if (FindNonZeroHighGpuAliasForDecodedAddress(base, info.base_address,
                                                 info.size, high_alias)) {
      return true;
    }
  }

  if (title_related) {
    return false;
  }

  if (record.upload_data.empty()) {
    return true;
  }

  if (HasNonZeroUploadPrefixData(record, kMaxZeroTextureRefreshProbeBytes)) {
    return false;
  }

  return false;
}

uint32_t PropagateTextureAliasUploadLocked(
    const RendererResourceRecord& source_record, bool require_title_probe) {
  if (!IsImageResourceKind(GuestObjectKind(source_record)) ||
      !HasNonZeroUploadData(source_record)) {
    return 0;
  }

  const std::array<uint32_t, 6> source_dwords =
      TextureFetchDwordsForRecord(source_record);
  const TextureFetchSnapshotInfo source_info =
      GetTextureFetchSnapshotInfo(source_dwords);
  if (!IsUsableTextureFetchSnapshot(source_info) ||
      source_record.upload_data.size() != source_info.size) {
    return 0;
  }

  const uint32_t source_object = GuestObjectAddress(source_record);
  const bool source_title_related =
      IsFableTitleTextureProbeAddress(source_object) ||
      IsFableTitleTextureProbeAddress(source_info.base_address);
  if (require_title_probe && !source_title_related) {
    return 0;
  }
  if (!require_title_probe &&
      !source_title_related &&
      !IsLikelyCpuTiledTextureAliasInfo(source_info)) {
    return 0;
  }

  uint32_t propagated = 0;
  for (auto& [candidate_address, candidate] :
       FableDirectGuestResourceRegistry().resources) {
    if (&candidate == &source_record ||
        !IsImageResourceKind(GuestObjectKind(candidate))) {
      continue;
    }

    const uint32_t candidate_object = GuestObjectAddress(candidate);
    const bool candidate_title_related =
        IsFableTitleTextureProbeAddress(candidate_object) ||
        IsFableTitleTextureProbeAddress(candidate_address);
    if (require_title_probe && !candidate_title_related) {
      continue;
    }

    const TextureFetchSnapshotInfo candidate_info =
        GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(candidate));
    const GuestTextureRecord candidate_texture =
        GuestTextureRecordFor(candidate);
    if (!TextureFetchSnapshotsMatchForAliasUpload(candidate_info, source_info)) {
      continue;
    }
    if (candidate.upload_data.size() == source_record.upload_data.size() &&
        std::memcmp(candidate.upload_data.data(), source_record.upload_data.data(),
                    candidate.upload_data.size()) == 0) {
      continue;
    }

    candidate.upload_data = source_record.upload_data;
    GuestTextureRecord updated_texture = candidate_texture;
    updated_texture.byte_size =
        std::max<uint32_t>(updated_texture.byte_size, source_info.size);
    updated_texture.upload_generation = candidate.upload_generation + 1u;
    ApplyGuestTextureRecord(candidate, updated_texture);
    RegisterTextureIdentityForRecordLocked(candidate);
    candidate.serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = candidate;
    MarkResourceChangedLocked(candidate);
    RefreshQueuedDrawTextureResourceSerialLocked(candidate);
    UpdateDirectSnapshotCacheLocked(candidate);
    ++propagated;

    if (RendererResourceTracingEnabled() &&
        g_observer.logged_title_texture_alias_uploads <
            kMaxTitleTextureAliasUploadLogs) {
      REXLOG_INFO(
          "Refii renderer propagated {}texture alias upload #{} "
          "guest=0x{:08X} base=0x{:08X} size={} from=0x{:08X} generation={}",
          require_title_probe ? "title " : "",
          g_observer.logged_title_texture_alias_uploads + 1u, candidate_object,
          source_info.base_address, source_info.size, source_object,
          static_cast<unsigned long long>(candidate.upload_generation));
      ++g_observer.logged_title_texture_alias_uploads;
    }
  }

  return propagated;
}

uint32_t PropagateTitleTextureAliasUploadLocked(
    const RendererResourceRecord& source_record) {
  return PropagateTextureAliasUploadLocked(source_record, true);
}

uint32_t ExactTitleTextureProducerSymbolAddress(std::string_view symbol) {
  if (symbol == "rex_lhGfx_LockBuffer") {
    return 0x821F6300u;
  }
  if (symbol == "rex_lhGfx_FlushBuffer") {
    return 0x822C2630u;
  }
  if (symbol == "sub_82278138") {
    return 0x82278138u;
  }
  if (symbol == "sub_821FB650") {
    return 0x821FB650u;
  }
  if (symbol == "sub_821FBB78") {
    return 0x821FBB78u;
  }
  if (symbol == "sub_8300CB48") {
    return 0x8300CB48u;
  }
  return 0;
}

bool IsFableTitleBillboardTileUploadBaseAddress(uint32_t base) {
  return base >= kFableTitleBillboardTileFirstBase &&
         base <= kFableTitleBillboardTileLastBase &&
         ((base - kFableTitleBillboardTileFirstBase) %
          kFableTitleBillboardTileBaseStride) == 0;
}

bool IsFableTitleBillboardTileUploadObjectAddress(uint32_t address) {
  return address >= kFableTitleBillboardTileFirstObject &&
         address <= kFableTitleBillboardTileLastObject &&
         ((address - kFableTitleBillboardTileFirstObject) %
          kFableTitleBillboardTileObjectStride) == 0;
}

bool IsFableTitleBillboardTileUploadHeaderAddress(uint32_t address) {
  if (address < kFableTitleBillboardTileFirstObject +
                    kFableTitleBillboardTileHeaderOffset ||
      address > kFableTitleBillboardTileLastObject +
                    kFableTitleBillboardTileHeaderOffset) {
    return false;
  }
  return ((address - kFableTitleBillboardTileFirstObject -
           kFableTitleBillboardTileHeaderOffset) %
          kFableTitleBillboardTileObjectStride) == 0;
}

uint32_t FableTitleBillboardTileHeaderAddressForObject(uint32_t object) {
  return IsFableTitleBillboardTileUploadObjectAddress(object)
             ? object + kFableTitleBillboardTileHeaderOffset
             : 0u;
}

uint32_t FableTitleBillboardTileObjectAddressForHeader(uint32_t header) {
  return IsFableTitleBillboardTileUploadHeaderAddress(header)
             ? header - kFableTitleBillboardTileHeaderOffset
             : 0u;
}

bool IsFableTitleBillboardTileUploadShape(
    const TextureFetchSnapshotInfo& info) {
  if (!IsFableTitleBillboardTileUploadBaseAddress(info.base_address)) {
    return false;
  }
  return (info.size == 131072u && info.width == 512u &&
          info.height == 512u && info.format == 18u) ||
         (info.size == 65536u && info.width == 256u &&
          info.height == 256u && info.format == 20u) ||
         (info.size == 131072u && info.width == 128u &&
          info.height == 1024u && info.format == 20u);
}

bool IsKnownTitleBillboardAtlasTexture(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info) {
  const auto is_title_billboard_atlas_object = [](uint32_t address) {
    return address == 0x4C132C70u || address == 0x4C132CA4u ||
           address == 0x4C132CD8u || address == 0x4C133150u ||
           address == 0x4C133184u || address == 0x16C40000u;
  };
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const bool canonical_atlas =
      info.base_address == 0x16C40000u && info.size == 512u * 1024u &&
      info.width == 1024u && info.height == 512u && info.format == 20u &&
      (is_title_billboard_atlas_object(GuestObjectAddress(record)) ||
       is_title_billboard_atlas_object(GuestObjectParentAddress(record)) ||
       is_title_billboard_atlas_object(texture.object_address) ||
       is_title_billboard_atlas_object(texture.parent_address));
  const bool tile_ring =
      IsFableTitleBillboardTileUploadShape(info) &&
      (IsFableTitleBillboardTileUploadObjectAddress(GuestObjectAddress(record)) ||
       IsFableTitleBillboardTileUploadHeaderAddress(GuestObjectAddress(record)) ||
       IsFableTitleBillboardTileUploadObjectAddress(
           GuestObjectParentAddress(record)) ||
       IsFableTitleBillboardTileUploadHeaderAddress(
           GuestObjectParentAddress(record)) ||
       IsFableTitleBillboardTileUploadObjectAddress(texture.object_address) ||
       IsFableTitleBillboardTileUploadHeaderAddress(texture.object_address) ||
       IsFableTitleBillboardTileUploadObjectAddress(texture.parent_address) ||
       IsFableTitleBillboardTileUploadHeaderAddress(texture.parent_address) ||
       IsFableTitleBillboardTileUploadBaseAddress(GuestObjectAddress(record)) ||
       IsFableTitleBillboardTileUploadBaseAddress(texture.object_address));
  return canonical_atlas || tile_ring;
}

bool IsExactFableTitleGuiTextAtlasTexture(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info) {
  const bool exact_shape =
      (info.base_address == 0x1A0D9000u && info.size == 65536u &&
       info.width == 512u && info.height == 256u && info.format == 59u) ||
      (info.base_address == 0x1A0EA000u && info.size == 131072u &&
       info.width == 512u && info.height == 512u && info.format == 59u);
  if (!exact_shape) {
    return false;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  return IsFableTitleTextureProbeAddress(GuestObjectAddress(record)) ||
         IsFableTitleTextureProbeAddress(GuestObjectParentAddress(record)) ||
         IsFableTitleTextureProbeAddress(texture.object_address) ||
         IsFableTitleTextureProbeAddress(texture.parent_address) ||
         IsFableTitleTextureProbeAddress(info.base_address);
}

bool IsExactKnownTitleTextureRefreshResource(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info) {
  if (!IsUsableTextureFetchSnapshot(info) ||
      info.size > kMaxZeroTextureRefreshBytes) {
    return false;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  return IsFableTitleTextureProbeAddress(GuestObjectAddress(record)) ||
         IsFableTitleTextureProbeAddress(GuestObjectParentAddress(record)) ||
         IsFableTitleTextureProbeAddress(texture.object_address) ||
         IsFableTitleTextureProbeAddress(texture.parent_address) ||
         IsFableTitleTextureProbeAddress(info.base_address) ||
         IsKnownTitleBillboardAtlasTexture(record, info);
}

void MarkTextureResourceChangedIfDirtyLocked(
    const RendererResourceRecord& before, RendererResourceRecord& record);

uint32_t ZeroTextureRefreshCandidateAddress(const RendererResourceRecord& record) {
  const uint32_t object_address = GuestObjectAddress(record);
  return object_address != 0 ? object_address : record.guest_address;
}

bool ExactTitleTextureBlankProbeMatchesRecord(
    const ExactTitleTextureBlankProbe& probe,
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info) {
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  if (probe.object_address != 0 &&
      GuestObjectAddress(record) != probe.object_address) {
    return false;
  }
  if (probe.parent_address != 0 &&
      GuestObjectParentAddress(record) != probe.parent_address) {
    return false;
  }
  if (probe.texture_object_address != 0 &&
      texture.object_address != probe.texture_object_address) {
    return false;
  }
  if (probe.texture_parent_address != 0 &&
      texture.parent_address != probe.texture_parent_address) {
    return false;
  }
  return info.base_address == probe.base_address &&
         info.size == probe.byte_size && info.width == probe.width &&
         info.height == probe.height && info.format == probe.format;
}

bool ExactTitleTextureRefreshCandidateMatchesRecord(
    const ExactTitleTextureRefreshCandidate& candidate,
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info) {
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  if (candidate.object_address != 0 &&
      GuestObjectAddress(record) != candidate.object_address) {
    return false;
  }
  if (candidate.parent_address != 0 &&
      GuestObjectParentAddress(record) != candidate.parent_address) {
    return false;
  }
  if (candidate.texture_object_address != 0 &&
      texture.object_address != candidate.texture_object_address) {
    return false;
  }
  if (candidate.texture_parent_address != 0 &&
      texture.parent_address != candidate.texture_parent_address) {
    return false;
  }
  return info.base_address == candidate.base_address &&
         info.size == candidate.byte_size &&
         info.width == candidate.width &&
         info.height == candidate.height &&
         info.format == candidate.format;
}

uint32_t ExactTitleTextureRefreshRootFromHeaderActionLocked(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info,
    std::string_view producer,
    uint32_t producer_symbol_address) {
  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords)) {
    return 0;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const uint32_t object_address = GuestObjectAddress(record);
  const uint32_t parent_address = GuestObjectParentAddress(record);
  const FableTextureHeaderAction* action =
      FableDirectFindTextureHeaderActionForExactResource(
          record.guest_address,
          object_address != 0 ? object_address : texture.object_address,
          parent_address != 0 ? parent_address : texture.parent_address,
          info.base_address, info.size, info.width, info.height, info.format,
          dwords);
  if (action == nullptr && texture.object_address != object_address) {
    action = FableDirectFindTextureHeaderActionForExactResource(
        record.guest_address, texture.object_address, texture.parent_address,
        info.base_address, info.size, info.width, info.height, info.format,
        dwords);
  }
  if (action == nullptr) {
    return 0;
  }

  uint32_t root = action->texture_object != 0 ? action->texture_object
                                              : action->texture_header;
  if (root == 0) {
    root = action->parent_texture;
  }
  if (root == 0) {
    return 0;
  }

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_exact_title_texture_refresh_root_joins < 32u) {
    REXLOG_INFO(
        "Refii renderer exact title texture refresh root join #{} "
        "producer={} symbol=0x{:08X} record=0x{:08X} root=0x{:08X} "
        "header=0x{:08X} fetch=0x{:08X} object=0x{:08X} "
        "base=0x{:08X} size={} {}x{} fmt={} call={} "
        "context=0x{:016X} lr=0x{:08X}",
        g_observer.logged_exact_title_texture_refresh_root_joins + 1u,
        producer, producer_symbol_address, record.guest_address, root,
        action->texture_header, action->fetch_address, action->texture_object,
        info.base_address, info.size, info.width, info.height, info.format,
        static_cast<unsigned long long>(action->call_serial),
        static_cast<unsigned long long>(action->context_key), action->lr);
    ++g_observer.logged_exact_title_texture_refresh_root_joins;
  }
  return root;
}

bool StoreExactTitleTextureBlankProbeLocked(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info) {
  const uint32_t candidate_address = ZeroTextureRefreshCandidateAddress(record);
  if (candidate_address == 0 ||
      GuestObjectKind(record) != RendererResourceKind::kTexture ||
      !IsExactKnownTitleTextureRefreshResource(record, info)) {
    return false;
  }

  auto existing =
      g_observer.exact_title_texture_blank_probes.find(candidate_address);
  if (existing != g_observer.exact_title_texture_blank_probes.end() &&
      ExactTitleTextureBlankProbeMatchesRecord(existing->second, record, info)) {
    return true;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  ExactTitleTextureBlankProbe probe;
  probe.serial = ++g_observer.serial;
  probe.record_address = record.guest_address;
  probe.object_address = GuestObjectAddress(record);
  probe.parent_address = GuestObjectParentAddress(record);
  probe.texture_object_address = texture.object_address;
  probe.texture_parent_address = texture.parent_address;
  probe.base_address = info.base_address;
  probe.byte_size = info.size;
  probe.width = info.width;
  probe.height = info.height;
  probe.format = info.format;
  g_observer.exact_title_texture_blank_probes[candidate_address] = probe;

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_exact_title_texture_blank_probes < 32u) {
    REXLOG_INFO(
        "Refii renderer exact title texture blank probe #{} guest=0x{:08X} "
        "base=0x{:08X} size={} {}x{} fmt={} record=0x{:08X}",
        g_observer.logged_exact_title_texture_blank_probes + 1u,
        candidate_address, info.base_address, info.size, info.width,
        info.height, info.format, record.guest_address);
    ++g_observer.logged_exact_title_texture_blank_probes;
  }
  return true;
}

bool RememberExactTitleTextureRefreshCandidateLocked(
    const RendererResourceRecord& record,
    const TextureFetchSnapshotInfo& info,
    std::string_view producer,
    uint32_t producer_symbol_address,
    uint32_t producer_lr,
    uint32_t root_header) {
  const uint32_t candidate_address = ZeroTextureRefreshCandidateAddress(record);
  if (candidate_address == 0 ||
      GuestObjectKind(record) != RendererResourceKind::kTexture ||
      !IsExactKnownTitleTextureRefreshResource(record, info)) {
    return false;
  }

  auto claim_blank_probe =
      [&](ExactTitleTextureRefreshCandidate& candidate) -> void {
    if (candidate.claimed_blank_probe_serial != 0) {
      return;
    }
    auto probe =
        g_observer.exact_title_texture_blank_probes.find(candidate_address);
    if (probe == g_observer.exact_title_texture_blank_probes.end() ||
        !ExactTitleTextureBlankProbeMatchesRecord(probe->second, record, info)) {
      return;
    }

    candidate.claimed_blank_probe_serial = probe->second.serial;
    if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
        g_observer.logged_exact_title_texture_blank_probe_claims < 32u) {
      REXLOG_INFO(
          "Refii renderer exact title texture producer claimed blank probe #{} "
          "producer={} symbol=0x{:08X} guest=0x{:08X} probe={} "
          "base=0x{:08X} size={} {}x{} fmt={}",
          g_observer.logged_exact_title_texture_blank_probe_claims + 1u,
          producer, producer_symbol_address, candidate_address,
          static_cast<unsigned long long>(probe->second.serial),
          info.base_address, info.size, info.width, info.height, info.format);
      ++g_observer.logged_exact_title_texture_blank_probe_claims;
    }
    g_observer.exact_title_texture_blank_probes.erase(probe);
  };

  auto existing =
      g_observer.exact_title_texture_refresh_candidates.find(candidate_address);
  if (producer_symbol_address == 0 &&
      (record.locked_address != 0 || record.locked_write_address != 0 ||
       record.locked_size != 0)) {
    producer_symbol_address = 0x821F6300u;
  }
  if (root_header == 0 && producer_symbol_address != 0) {
    root_header = ExactTitleTextureRefreshRootFromHeaderActionLocked(
        record, info, producer, producer_symbol_address);
  }
  if (existing != g_observer.exact_title_texture_refresh_candidates.end() &&
      ExactTitleTextureRefreshCandidateMatchesRecord(existing->second, record,
                                                     info)) {
    ExactTitleTextureRefreshCandidate& candidate = existing->second;
    const bool has_new_producer = producer_symbol_address != 0 &&
                                  candidate.producer_symbol_address !=
                                      producer_symbol_address;
    const bool has_new_lr = producer_lr != 0 && candidate.producer_lr != producer_lr;
    const bool has_new_root = root_header != 0 && candidate.root_header != root_header;
    const bool has_new_lock =
        (record.locked_address != 0 &&
         candidate.lock_address != record.locked_address) ||
        (record.locked_write_address != 0 &&
         candidate.lock_write_address != record.locked_write_address) ||
        (record.locked_size != 0 && candidate.lock_size != record.locked_size);
    if (has_new_producer || has_new_lr || has_new_root || has_new_lock) {
      if (producer_symbol_address != 0) {
        candidate.producer_symbol_address = producer_symbol_address;
      }
      if (producer_lr != 0) {
        candidate.producer_lr = producer_lr;
      }
      if (root_header != 0) {
        candidate.root_header = root_header;
      }
      if (record.locked_address != 0) {
        candidate.lock_address = record.locked_address;
      }
      if (record.locked_write_address != 0) {
        candidate.lock_write_address = record.locked_write_address;
      }
      if (record.locked_size != 0) {
        candidate.lock_size = record.locked_size;
      }
      candidate.producer_serial =
          std::max<uint64_t>(record.serial, ++g_observer.serial);

      if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
          g_observer.logged_exact_title_texture_refresh_candidate_updates < 32u) {
        REXLOG_INFO(
            "Refii renderer exact title texture refresh candidate update #{} "
            "producer={} symbol=0x{:08X} guest=0x{:08X} base=0x{:08X} "
            "size={} {}x{} fmt={} root=0x{:08X} lock=0x{:08X}/0x{:08X}/{}",
            g_observer.logged_exact_title_texture_refresh_candidate_updates + 1u,
            producer, candidate.producer_symbol_address, candidate_address,
            info.base_address, info.size, info.width, info.height, info.format,
            candidate.root_header, candidate.lock_address,
            candidate.lock_write_address, candidate.lock_size);
        ++g_observer.logged_exact_title_texture_refresh_candidate_updates;
      }
    }
    claim_blank_probe(existing->second);
    return true;
  }

  if (producer_symbol_address == 0 || (root_header == 0 &&
                                       record.locked_address == 0 &&
                                       record.locked_write_address == 0 &&
                                       record.locked_size == 0)) {
    return false;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  ExactTitleTextureRefreshCandidate candidate;
  candidate.serial = ++g_observer.serial;
  candidate.producer_serial = std::max<uint64_t>(record.serial,
                                                 g_observer.serial);
  candidate.record_address = record.guest_address;
  candidate.object_address = GuestObjectAddress(record);
  candidate.parent_address = GuestObjectParentAddress(record);
  candidate.texture_object_address = texture.object_address;
  candidate.texture_parent_address = texture.parent_address;
  candidate.base_address = info.base_address;
  candidate.byte_size = info.size;
  candidate.width = info.width;
  candidate.height = info.height;
  candidate.format = info.format;
  candidate.producer_symbol_address = producer_symbol_address;
  candidate.producer_lr = producer_lr;
  candidate.root_header = root_header;
  candidate.lock_address = record.locked_address;
  candidate.lock_write_address = record.locked_write_address;
  candidate.lock_size = record.locked_size;
  claim_blank_probe(candidate);
  g_observer.exact_title_texture_refresh_candidates[candidate_address] =
      candidate;

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_exact_title_texture_refresh_candidates < 32u) {
    REXLOG_INFO(
        "Refii renderer exact title texture refresh candidate #{} "
        "producer={} symbol=0x{:08X} guest=0x{:08X} base=0x{:08X} "
        "size={} {}x{} fmt={} root=0x{:08X} lock=0x{:08X}/0x{:08X}/{}",
        g_observer.logged_exact_title_texture_refresh_candidates + 1u,
        producer, producer_symbol_address, candidate_address,
        info.base_address, info.size, info.width, info.height, info.format,
        root_header, record.locked_address, record.locked_write_address,
        record.locked_size);
    ++g_observer.logged_exact_title_texture_refresh_candidates;
  }
  return true;
}

bool ClaimExactTitleTextureBlankProbeFromRenderActionLocked(
    const FablePrivateTextureCopyAction& action, std::string_view producer) {
  if (action.source_texture == 0 || action.target_surface == 0 ||
      action.wrapper_call_serial == 0 ||
      action.wrapper_context_key == 0 ||
      action.wrapper_symbol_address == 0) {
    return false;
  }

  auto probe =
      g_observer.exact_title_texture_blank_probes.find(action.source_texture);
  if (probe == g_observer.exact_title_texture_blank_probes.end()) {
    return false;
  }

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_exact_title_texture_blank_probe_claims < 32u) {
    REXLOG_INFO(
        "Refii renderer exact title texture render action claimed blank probe "
        "#{} producer={} symbol=0x{:08X} guest=0x{:08X} probe={} "
        "surface=0x{:08X} call={} context=0x{:016X} base=0x{:08X} "
        "size={} {}x{} fmt={}",
        g_observer.logged_exact_title_texture_blank_probe_claims + 1u,
        producer, action.wrapper_symbol_address, action.source_texture,
        static_cast<unsigned long long>(probe->second.serial),
        action.target_surface,
        static_cast<unsigned long long>(action.wrapper_call_serial),
        static_cast<unsigned long long>(action.wrapper_context_key),
        probe->second.base_address, probe->second.byte_size,
        probe->second.width, probe->second.height, probe->second.format);
    ++g_observer.logged_exact_title_texture_blank_probe_claims;
  }

  g_observer.exact_title_texture_blank_probes.erase(probe);
  g_observer.exact_title_texture_refresh_candidates.erase(action.source_texture);
  return true;
}

void TrackZeroTextureRefreshCandidateLocked(const RendererResourceRecord& record) {
  const uint32_t candidate_address = ZeroTextureRefreshCandidateAddress(record);
  if (candidate_address == 0 ||
      GuestObjectKind(record) != RendererResourceKind::kTexture) {
    return;
  }

  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords)) {
    g_observer.exact_title_texture_refresh_candidates.erase(candidate_address);
    g_observer.exact_title_texture_blank_probes.erase(candidate_address);
    return;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (IsExactKnownTitleTextureRefreshResource(record, info) &&
      (record.upload_data.empty() || !HasNonZeroUploadData(record))) {
    if (!RememberExactTitleTextureRefreshCandidateLocked(record, info,
                                                        "state-track")) {
      auto existing =
          g_observer.exact_title_texture_refresh_candidates.find(
              candidate_address);
      if (existing ==
              g_observer.exact_title_texture_refresh_candidates.end() ||
          !ExactTitleTextureRefreshCandidateMatchesRecord(existing->second,
                                                          record, info)) {
        g_observer.exact_title_texture_refresh_candidates.erase(
            candidate_address);
      }
    }
  } else {
    g_observer.exact_title_texture_refresh_candidates.erase(candidate_address);
    g_observer.exact_title_texture_blank_probes.erase(candidate_address);
  }
}

std::unordered_map<uint32_t, RendererResourceRecord>::iterator
FindExactTitleTextureResourceIteratorLocked(uint32_t address);

void RefreshZeroTextureUploadsFromGuestMemoryLocked(std::string_view reason) {
  if (!g_observer.memory_base) {
    return;
  }

  uint32_t refreshed_textures = 0;
  uint32_t candidate_index = 0;
  uint32_t probed_candidates = 0;
  const uint32_t resource_count =
      static_cast<uint32_t>(
          g_observer.exact_title_texture_refresh_candidates.size());
  if (resource_count == 0) {
    g_observer.zero_texture_refresh_cursor = 0;
    return;
  }

  std::vector<uint32_t> completed_candidates;
  completed_candidates.reserve(kMaxZeroTextureRefreshCandidatesPerHarvest);
  for (const auto& [candidate_address, candidate] :
       g_observer.exact_title_texture_refresh_candidates) {
    auto resource_it =
        FindExactTitleTextureResourceIteratorLocked(candidate_address);
    if (resource_it == FableDirectGuestResourceRegistry().resources.end()) {
      completed_candidates.push_back(candidate_address);
      continue;
    }
    RendererResourceRecord& record = resource_it->second;
    if (GuestObjectKind(record) != RendererResourceKind::kTexture) {
      completed_candidates.push_back(candidate_address);
      continue;
    }

    const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
    if (!HasTextureFetchDwords(dwords)) {
      completed_candidates.push_back(candidate_address);
      continue;
    }

    const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
    if (!IsExactKnownTitleTextureRefreshResource(record, info) ||
        !ExactTitleTextureRefreshCandidateMatchesRecord(candidate, record,
                                                        info)) {
      completed_candidates.push_back(candidate_address);
      continue;
    }
    if (candidate_index++ < g_observer.zero_texture_refresh_cursor) {
      continue;
    }
    if (probed_candidates >= kMaxZeroTextureRefreshCandidatesPerHarvest) {
      break;
    }
    ++probed_candidates;

    if (!record.upload_data.empty() && HasNonZeroUploadData(record)) {
      completed_candidates.push_back(candidate_address);
      continue;
    }

    const uint32_t probe_size =
        std::min<uint32_t>(info.size, kMaxZeroTextureRefreshProbeBytes);
    uint32_t upload_address = info.base_address;
    const uint32_t base_nonzero = FindFirstNonZeroGuestByte(
        g_observer.memory_base, info.base_address, info.size, probe_size);
    if (base_nonzero == UINT32_MAX) {
      const uint32_t lock_addresses[] = {
          candidate.lock_write_address,
          candidate.lock_address,
          DecodeFableGpuBufferPointer(candidate.lock_write_address),
          DecodeFableGpuBufferPointer(candidate.lock_address),
      };
      for (uint32_t lock_address : lock_addresses) {
        if (lock_address == 0 ||
            (!IsLikelyGpuByteBufferRange(lock_address, info.size) &&
             !IsLikelyHighScratchByteBufferRange(lock_address, info.size))) {
          continue;
        }
        if (FindFirstNonZeroGuestByte(g_observer.memory_base, lock_address,
                                      info.size, probe_size) != UINT32_MAX) {
          upload_address = lock_address;
          break;
        }
      }
    }
    if (upload_address == info.base_address && base_nonzero == UINT32_MAX) {
      continue;
    }

    const uint64_t previous_generation = record.upload_generation;
    const bool copied_upload =
        upload_address == info.base_address
            ? CaptureTextureUploadFromFetchDwords(record, dwords,
                                                  g_observer.memory_base)
            : CopyGuestBufferUploadData(record, g_observer.memory_base,
                                        upload_address, info.size);
    if (!copied_upload ||
        record.upload_generation == previous_generation ||
        !HasNonZeroUploadData(record)) {
      continue;
    }

    GuestTextureRecord texture = GuestTextureRecordFor(record);
    texture.byte_size = std::max<uint32_t>(texture.byte_size, info.size);
    texture.upload_generation = record.upload_generation;
    ApplyGuestTextureRecord(record, texture);
    RegisterTextureIdentityForRecordLocked(record);
    record.serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = record;
    MarkResourceChangedLocked(record);
    RefreshQueuedDrawTextureResourceSerialLocked(record);
    completed_candidates.push_back(candidate_address);
    ++refreshed_textures;
    if (upload_address != info.base_address &&
        (RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
        g_observer.logged_title_texture_upload_probes <
            kMaxTitleTextureUploadProbeLogs) {
      REXLOG_INFO(
          "Refii renderer refreshed exact title texture upload from lock "
          "alias #{} guest=0x{:08X} base=0x{:08X} alias=0x{:08X} size={} "
          "{}x{} fmt={} gen={}",
          g_observer.logged_title_texture_upload_probes + 1u,
          GuestObjectAddress(record), info.base_address, upload_address,
          info.size, info.width, info.height, info.format,
          static_cast<unsigned long long>(record.upload_generation));
      ++g_observer.logged_title_texture_upload_probes;
    }
  }

  for (uint32_t candidate_address : completed_candidates) {
    g_observer.exact_title_texture_refresh_candidates.erase(candidate_address);
    g_observer.exact_title_texture_blank_probes.erase(candidate_address);
  }

  if (candidate_index == 0 ||
      g_observer.zero_texture_refresh_cursor + probed_candidates >=
          candidate_index) {
    g_observer.zero_texture_refresh_cursor = 0;
  } else {
    g_observer.zero_texture_refresh_cursor += probed_candidates;
  }

  if (refreshed_textures != 0 && RendererResourceTracingEnabled()) {
    REXLOG_INFO(
        "Refii renderer refreshed {} exact title texture upload(s) from live "
        "guest memory before {}",
        refreshed_textures, reason);
  }
}

bool IsPreferredBufferResourceMatch(const RendererResourceRecord& record) {
  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  if (IsBufferResourceKind(GuestObjectKind(record))) {
    return !buffer.scratch && HasNonZeroUploadData(record);
  }
  return !record.scratch;
}

bool CaptureFableBufferUploadFromHeader(RendererResourceRecord& record,
                                        uint8_t* base) {
  const uint32_t object_address = GuestObjectAddress(record);
  if (!base || !IsBufferResourceKind(GuestObjectKind(record)) ||
      object_address == 0) {
    return false;
  }

  // Fable's SetStreamSource resolves D3D buffer wrappers through +0x18/+0x1C
  // with the same PM4/Xenos address transform used by command packets.
  const GuestBufferRecord existing_buffer = GuestBufferRecordFor(record);
  const uint32_t header_candidates[] = {
      object_address,
      object_address >= 8u ? object_address - 8u : 0u,
      object_address + 8u,
      existing_buffer.gpu_address,
      existing_buffer.gpu_address >= 8u ? existing_buffer.gpu_address - 8u
                                        : 0u,
      existing_buffer.gpu_address + 8u,
      GuestObjectParentAddress(record),
      GuestObjectParentAddress(record) >= 8u
          ? GuestObjectParentAddress(record) - 8u
          : 0u,
      GuestObjectParentAddress(record) + 8u,
      existing_buffer.header_address,
      existing_buffer.header_address >= 8u
          ? existing_buffer.header_address - 8u
          : 0u,
      existing_buffer.header_address + 8u,
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

  GuestBufferRecord buffer = GuestBufferRecordFor(record);
  buffer.object_address = object_address;
  buffer.gpu_address = gpu_address;
  buffer.byte_size = gpu_size;
  if (buffer.header_address == 0 ||
      buffer.header_address == buffer.object_address) {
    buffer.header_address = header_address;
  }
  ApplyGuestBufferRecord(record, buffer);
  CopyGuestBufferUploadData(record, base, gpu_address,
                            std::min(buffer.byte_size, gpu_size));
  return !record.upload_data.empty();
}

bool ResourceRecordMatchesAddress(const RendererResourceRecord& record,
                                  uint32_t address) {
  if (address == 0) {
    return false;
  }
  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  const uint32_t data_address = GuestResourceDataAddress(record);
  const uint32_t data_size = GuestResourceByteSize(record);
  if (GuestObjectAddress(record) == address || data_address == address ||
      buffer.header_address == address ||
      GuestObjectParentAddress(record) == address) {
    return true;
  }
  const uint32_t aligned_address = address & ~3u;
  if (IsBufferResourceKind(GuestObjectKind(record)) &&
      aligned_address != address &&
      (data_address == aligned_address ||
       buffer.header_address == aligned_address)) {
    return true;
  }
  if (!IsBufferResourceKind(GuestObjectKind(record)) || data_address == 0 ||
      data_size == 0 || address < data_address) {
    if (aligned_address == address || aligned_address < data_address) {
      return false;
    }
    return static_cast<uint64_t>(aligned_address - data_address) < data_size;
  }
  return static_cast<uint64_t>(address - data_address) < data_size;
}

RendererResourceRecord* FindResourceRecordLocked(uint32_t address) {
  if (address == 0) {
    return nullptr;
  }

  auto it = FableDirectGuestResourceRegistry().resources.find(address);
  if (it != FableDirectGuestResourceRegistry().resources.end() &&
      (!it->second.scratch ||
       !IsBufferResourceKind(GuestObjectKind(it->second)))) {
    if (!IsBufferResourceKind(GuestObjectKind(it->second)) ||
        HasNonZeroUploadData(it->second)) {
      return &it->second;
    }
  }

  RendererResourceRecord* fallback = it != FableDirectGuestResourceRegistry().resources.end()
                                         ? &it->second
                                         : nullptr;
  for (auto& [guest_address, record] : FableDirectGuestResourceRegistry().resources) {
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

RendererResourceRecord* FindLockedResourceRecordLocked(uint32_t address) {
  if (address == 0) {
    return nullptr;
  }

  const uint32_t decoded_address = DecodeFableGpuBufferPointer(address);
  for (auto& [guest_address, record] :
       FableDirectGuestResourceRegistry().resources) {
    (void)guest_address;
    if (record.locked_address == 0 && record.locked_write_address == 0) {
      continue;
    }

    const uint32_t decoded_locked_address =
        DecodeFableGpuBufferPointer(record.locked_address);
    const uint32_t decoded_locked_write_address =
        DecodeFableGpuBufferPointer(record.locked_write_address);
    if (record.locked_address == address ||
        record.locked_write_address == address ||
        decoded_locked_address == address ||
        decoded_locked_write_address == address ||
        (decoded_address != 0 && record.locked_address == decoded_address) ||
        (decoded_address != 0 &&
         record.locked_write_address == decoded_address) ||
        (decoded_address != 0 && decoded_locked_address == decoded_address) ||
        (decoded_address != 0 &&
         decoded_locked_write_address == decoded_address)) {
      return &record;
    }
  }

  return nullptr;
}

std::unordered_map<uint32_t, RendererResourceRecord>::iterator
FindResourceIteratorLocked(uint32_t address) {
  if (address == 0) {
    return FableDirectGuestResourceRegistry().resources.end();
  }

  auto it = FableDirectGuestResourceRegistry().resources.find(address);
  if (it != FableDirectGuestResourceRegistry().resources.end() &&
      (!it->second.scratch ||
       !IsBufferResourceKind(GuestObjectKind(it->second)))) {
    if (!IsBufferResourceKind(GuestObjectKind(it->second)) ||
        HasNonZeroUploadData(it->second)) {
      return it;
    }
  }

  auto fallback = it;
  for (auto candidate = FableDirectGuestResourceRegistry().resources.begin();
       candidate != FableDirectGuestResourceRegistry().resources.end(); ++candidate) {
    if (ResourceRecordMatchesAddress(candidate->second, address)) {
      if (IsPreferredBufferResourceMatch(candidate->second)) {
        return candidate;
      }
      if (fallback == FableDirectGuestResourceRegistry().resources.end()) {
        fallback = candidate;
      }
    }
  }

  return fallback;
}

std::unordered_map<uint32_t, RendererResourceRecord>::iterator
FindExactTitleTextureResourceIteratorLocked(uint32_t address) {
  if (address == 0) {
    return FableDirectGuestResourceRegistry().resources.end();
  }

  auto best = FableDirectGuestResourceRegistry().resources.end();
  uint64_t best_score = 0;
  for (auto candidate = FableDirectGuestResourceRegistry().resources.begin();
       candidate != FableDirectGuestResourceRegistry().resources.end();
       ++candidate) {
    RendererResourceRecord& record = candidate->second;
    if (!IsImageResourceKind(GuestObjectKind(record))) {
      continue;
    }

    const GuestTextureRecord texture = GuestTextureRecordFor(record);
    const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
    const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
    if (!IsExactKnownTitleTextureRefreshResource(record, info)) {
      continue;
    }
    const bool object_match =
        GuestObjectAddress(record) == address ||
        GuestObjectParentAddress(record) == address ||
        texture.object_address == address || texture.parent_address == address ||
        FableTitleBillboardTileHeaderAddressForObject(
            GuestObjectAddress(record)) == address ||
        FableTitleBillboardTileHeaderAddressForObject(
            texture.object_address) == address ||
        FableTitleBillboardTileObjectAddressForHeader(address) ==
            GuestObjectAddress(record) ||
        FableTitleBillboardTileObjectAddressForHeader(address) ==
            texture.object_address;
    const bool base_match =
        texture.base_address == address || info.base_address == address;
    if (!object_match && !base_match) {
      continue;
    }

    uint64_t score = 1;
    if (object_match) {
      score += 1ull << 56;
    }
    if (HasNonZeroUploadData(record)) {
      score += 1ull << 48;
    }
    if (record.upload_generation != 0 || texture.upload_generation != 0) {
      score += 1ull << 40;
    }
    if (IsExactFableTitleGuiTextAtlasTexture(record, info)) {
      score += 1ull << 32;
    }
    score += std::min<uint64_t>(record.serial, 0xFFFFFFFFull);
    if (best == FableDirectGuestResourceRegistry().resources.end() ||
        score > best_score) {
      best = candidate;
      best_score = score;
    }
  }

  if (best != FableDirectGuestResourceRegistry().resources.end()) {
    RendererResourceRecord& selected = best->second;
    const GuestTextureRecord selected_texture =
        GuestTextureRecordFor(selected);
    const std::array<uint32_t, 6> selected_dwords =
        TextureFetchDwordsForRecord(selected);
    const TextureFetchSnapshotInfo selected_info =
        GetTextureFetchSnapshotInfo(selected_dwords);
    if ((IsExactFableTitleGuiTextAtlasTexture(selected, selected_info) ||
         address == 0x1A0D9000u || address == 0x1A0EA000u) &&
        (RendererResourceTracingEnabled() || RendererDrawTracingEnabled())) {
      static uint32_t logged_exact_gui_title_selections = 0;
      if (logged_exact_gui_title_selections < 64u) {
        const bool selected_object_match =
            GuestObjectAddress(selected) == address ||
            GuestObjectParentAddress(selected) == address ||
            selected_texture.object_address == address ||
            selected_texture.parent_address == address;
        const bool selected_base_match =
            selected_texture.base_address == address ||
            selected_info.base_address == address;
        REXLOG_INFO(
            "Refii renderer exact title GUI selection #{} request=0x{:08X} "
            "guest=0x{:08X} object=0x{:08X} parent=0x{:08X} base=0x{:08X} "
            "size={} {}x{} fmt={} upload={} ready={} gen={} serial={} "
            "score=0x{:016X} object_match={} base_match={}",
            ++logged_exact_gui_title_selections, address,
            GuestObjectAddress(selected), selected_texture.object_address,
            GuestObjectParentAddress(selected), selected_info.base_address,
            selected_info.size, selected_info.width, selected_info.height,
            selected_info.format, selected.upload_data.size(),
            HasNonZeroUploadData(selected) ? 1 : 0,
            static_cast<unsigned long long>(selected.upload_generation),
            static_cast<unsigned long long>(selected.serial),
            static_cast<unsigned long long>(best_score),
            selected_object_match ? 1 : 0, selected_base_match ? 1 : 0);
      }
    }
  }

  return best;
}

uint32_t TextureSnapshotSizeForRelatedRecordLocked(
    const RendererResourceRecord& record) {
  const uint32_t direct_size = TextureSnapshotSizeForRecord(record);
  if (direct_size != 0) {
    return direct_size;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  const uint32_t object_address = GuestObjectAddress(record);
  const uint32_t related_addresses[] = {
      GuestObjectParentAddress(record),
      buffer.header_address,
      texture.base_address,
  };
  for (uint32_t related_address : related_addresses) {
    if (related_address == 0 || related_address == object_address) {
      continue;
    }
    auto related_it = FableDirectGuestResourceRegistry().resources.find(related_address);
    if (related_it == FableDirectGuestResourceRegistry().resources.end()) {
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

void CaptureLockedBufferUpload(RendererResourceRecord& record, uint8_t* base,
                               bool clear_lock = true) {
  const RendererResourceKind kind = GuestObjectKind(record);
  if (!base || !IsUploadResourceKind(kind) ||
      record.locked_address == 0 || record.locked_size == 0) {
    return;
  }

  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const uint32_t typed_size =
      IsBufferResourceKind(kind) ? buffer.byte_size : texture.byte_size;
  const uint32_t record_size =
      typed_size != 0 ? typed_size : record.locked_size;
  const uint32_t upload_size = std::min(record.locked_size, record_size);
  if (upload_size == 0) {
    return;
  }

  const uint64_t upload_generation_before = record.upload_generation;
  const uint32_t primary_locked_address =
      record.locked_write_address != 0 ? record.locked_write_address
                                       : record.locked_address;
  uint32_t upload_address = primary_locked_address;
  const uint32_t decoded_address =
      DecodeFableGpuBufferPointer(primary_locked_address);
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
  if (!record.scratch && IsImageResourceKind(kind)) {
    const uint32_t raw_nonzero = first_nonzero(primary_locked_address);
    const uint32_t decoded_nonzero = first_nonzero(decoded_address);
    if (decoded_nonzero != UINT32_MAX) {
      upload_address = decoded_address;
    }
    if (raw_nonzero != UINT32_MAX && decoded_nonzero == UINT32_MAX) {
      upload_address = primary_locked_address;
    }
  } else if (!record.scratch && IsBufferResourceKind(kind)) {
    const uint32_t raw_nonzero = FindFirstNonZeroGuestByte(
        base, primary_locked_address, upload_size, 64u * 1024u);
    const uint32_t decoded_nonzero = FindFirstNonZeroGuestByte(
        base, decoded_address, upload_size, 64u * 1024u);
    if (buffer.gpu_address != 0 && buffer.byte_size != 0 &&
        raw_nonzero == UINT32_MAX && decoded_nonzero != UINT32_MAX &&
        decoded_address >= buffer.gpu_address &&
        static_cast<uint64_t>(decoded_address - buffer.gpu_address) <
            buffer.byte_size) {
      upload_address = decoded_address;
    }
  }
  const GuestTextureRecord texture_for_title_preserve =
      IsImageResourceKind(kind) ? GuestTextureRecordFor(record)
                                : GuestTextureRecord{};
  const bool title_texture_lock =
      IsImageResourceKind(kind) &&
      (IsFableTitleTextureProbeAddress(GuestObjectAddress(record)) ||
       IsFableTitleTextureProbeAddress(texture_for_title_preserve.base_address));
  const bool blank_title_upload_source =
      title_texture_lock && first_nonzero(upload_address) == UINT32_MAX;
  if (blank_title_upload_source && HasNonZeroUploadData(record)) {
    if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
        g_observer.logged_title_texture_upload_probes <
            kMaxTitleTextureUploadProbeLogs) {
      REXLOG_INFO(
          "Refii renderer preserved title lock upload #{} guest=0x{:08X} "
          "blank_source=0x{:08X} decoded=0x{:08X} size={} gen={}",
          g_observer.logged_title_texture_upload_probes + 1u,
          GuestObjectAddress(record), upload_address, decoded_address,
          upload_size,
          static_cast<unsigned long long>(record.upload_generation));
      ++g_observer.logged_title_texture_upload_probes;
    }
    if (clear_lock) {
      record.locked_address = 0;
      record.locked_write_address = 0;
      record.locked_size = 0;
    }
    return;
  }
  if (IsImageResourceKind(kind) &&
      (RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_image_lock_uploads < 192u) {
    const bool title_lock =
        IsFableTitleTextureProbeAddress(GuestObjectAddress(record)) ||
        IsFableTitleTextureProbeAddress(texture.base_address);
    if (title_lock || g_observer.logged_image_lock_uploads < 32u) {
      const uint32_t raw_nonzero = first_nonzero(primary_locked_address);
      const uint32_t decoded_nonzero = first_nonzero(decoded_address);
      const uint32_t chosen_nonzero = first_nonzero(upload_address);
      const uint32_t raw_word0 =
          raw_nonzero != UINT32_MAX
              ? LoadGuestU32(base, primary_locked_address)
              : 0u;
      const uint32_t decoded_word0 =
          decoded_nonzero != UINT32_MAX ? LoadGuestU32(base, decoded_address)
                                        : 0u;
      const uint32_t chosen_word0 =
          chosen_nonzero != UINT32_MAX ? LoadGuestU32(base, upload_address)
                                       : 0u;
      REXLOG_INFO(
          "Refii renderer image lock upload #{} guest=0x{:08X} kind={} "
          "raw=0x{:08X} decoded=0x{:08X} chosen=0x{:08X} size={} "
          "nz raw/decoded/chosen={}/{}/{} word0 raw/decoded/chosen="
          "{:08X}/{:08X}/{:08X} gen_before={}",
          g_observer.logged_image_lock_uploads + 1u,
          GuestObjectAddress(record), static_cast<uint32_t>(kind),
          primary_locked_address, decoded_address, upload_address,
          upload_size,
          raw_nonzero == UINT32_MAX ? -1 : static_cast<int32_t>(raw_nonzero),
          decoded_nonzero == UINT32_MAX
              ? -1
              : static_cast<int32_t>(decoded_nonzero),
          chosen_nonzero == UINT32_MAX ? -1
                                       : static_cast<int32_t>(chosen_nonzero),
          raw_word0, decoded_word0, chosen_word0, upload_generation_before);
      ++g_observer.logged_image_lock_uploads;
    }
  }
  CopyGuestBufferUploadData(record, base, upload_address, upload_size);
  if (record.upload_generation != upload_generation_before) {
    if (IsImageResourceKind(kind)) {
      GuestTextureRecord updated_texture = texture;
      updated_texture.byte_size =
          std::max<uint32_t>(updated_texture.byte_size, upload_size);
      updated_texture.upload_generation = record.upload_generation;
      ApplyGuestTextureRecord(record, updated_texture);
      RegisterTextureIdentityForRecordLocked(record);
    }
    record.serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = record;
    MarkResourceChangedLocked(record);
    if (IsImageResourceKind(kind)) {
      RefreshQueuedDrawTextureResourceSerialLocked(record);
      UpdateDirectSnapshotCacheLocked(record);
      PropagateTitleTextureAliasUploadLocked(record);
    } else {
      RefreshQueuedDrawBufferResourceSerialLocked(record);
    }
  }
	  if (clear_lock) {
	    record.locked_address = 0;
	    record.locked_write_address = 0;
	    record.locked_size = 0;
		  }
		}

void CaptureExactGuiAtlasLockedUploadIfReadyLocked(
    RendererResourceRecord& record, const TextureFetchSnapshotInfo& info,
    uint8_t* base) {
  if (!base || !IsExactFableTitleGuiTextAtlasTexture(record, info) ||
      record.locked_address == 0 || record.locked_size == 0) {
    return;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const uint32_t record_size =
      texture.byte_size != 0 ? texture.byte_size : info.size;
  const uint32_t upload_size =
      std::min(record.locked_size, record_size != 0 ? record_size : info.size);
  if (upload_size == 0) {
    return;
  }

  const uint32_t primary_locked_address =
      record.locked_write_address != 0 ? record.locked_write_address
                                       : record.locked_address;
  const uint32_t decoded_address =
      DecodeFableGpuBufferPointer(primary_locked_address);
  const auto has_nonzero = [&](uint32_t address) {
    if (!IsLikelyGpuByteBufferRange(address, upload_size) &&
        !IsLikelyHighScratchByteBufferRange(address, upload_size)) {
      return false;
    }
    return FindFirstNonZeroGuestByte(base, address, upload_size,
                                     upload_size) != UINT32_MAX;
  };
  if (!has_nonzero(primary_locked_address) && !has_nonzero(decoded_address)) {
    return;
  }

  const uint64_t previous_generation = record.upload_generation;
  CaptureLockedBufferUpload(record, base, false);
  if (record.upload_generation == previous_generation) {
    return;
  }

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled())) {
    static uint32_t logged_exact_gui_lock_uploads = 0;
    if (logged_exact_gui_lock_uploads < 32u) {
      REXLOG_INFO(
          "Refii renderer exact title GUI lock upload #{} guest=0x{:08X} "
          "base=0x{:08X} size={} {}x{} fmt={} raw=0x{:08X} decoded=0x{:08X} "
          "gen={}",
          ++logged_exact_gui_lock_uploads, GuestObjectAddress(record),
          info.base_address, info.size, info.width, info.height, info.format,
          primary_locked_address, decoded_address,
          static_cast<unsigned long long>(record.upload_generation));
    }
  }
}

bool PublishExactGuiAtlasUnlockUploadLocked(std::string_view symbol,
                                            const RendererHookArgs& args,
                                            RendererResourceRecord& record,
                                            uint8_t* base,
                                            bool resolved_from_locked_address) {
  if (!base || !IsImageResourceKind(GuestObjectKind(record)) ||
      record.locked_address == 0 || record.locked_size == 0) {
    return false;
  }

  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords)) {
    return false;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (!IsExactFableTitleGuiTextAtlasTexture(record, info)) {
    return false;
  }

  const uint32_t producer_symbol = ExactTitleTextureProducerSymbolAddress(symbol);
  RememberExactTitleTextureRefreshCandidateLocked(
      record, info, FableHookDisplayName(symbol), producer_symbol, args.lr,
      GuestObjectAddress(record));

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  const uint32_t record_size =
      texture.byte_size != 0 ? texture.byte_size : info.size;
  const uint32_t upload_size =
      std::min(record.locked_size, record_size != 0 ? record_size : info.size);
  if (upload_size == 0) {
    return true;
  }

  const uint32_t decoded_lock =
      DecodeFableGpuBufferPointer(record.locked_address);
  const uint32_t decoded_write =
      DecodeFableGpuBufferPointer(record.locked_write_address);
  const uint32_t address_candidates[] = {
      record.locked_write_address,
      record.locked_address,
      decoded_write,
      decoded_lock,
      info.base_address,
  };
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

  uint32_t upload_address = 0;
  uint32_t upload_first_nonzero = UINT32_MAX;
  for (uint32_t candidate : address_candidates) {
    const uint32_t candidate_first_nonzero = first_nonzero(candidate);
    if (candidate_first_nonzero != UINT32_MAX) {
      upload_address = candidate;
      upload_first_nonzero = candidate_first_nonzero;
      break;
    }
  }

  bool copied_upload = false;
  if (upload_address != 0) {
    const uint64_t previous_generation = record.upload_generation;
    copied_upload =
        CopyGuestBufferUploadData(record, base, upload_address, upload_size);
    if (record.upload_generation != previous_generation ||
        HasNonZeroUploadData(record)) {
      GuestTextureRecord updated_texture = GuestTextureRecordFor(record);
      updated_texture.byte_size =
          std::max<uint32_t>(updated_texture.byte_size, upload_size);
      updated_texture.upload_generation = record.upload_generation;
      ApplyGuestTextureRecord(record, updated_texture);
    }
  }

  const bool upload_ready = HasNonZeroUploadData(record);
  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_exact_title_gui_unlock_uploads < 64u) {
    const uint32_t raw_first_nonzero = first_nonzero(record.locked_address);
    const uint32_t write_first_nonzero =
        first_nonzero(record.locked_write_address);
    const uint32_t decoded_lock_first_nonzero = first_nonzero(decoded_lock);
    const uint32_t decoded_write_first_nonzero = first_nonzero(decoded_write);
    const uint32_t base_first_nonzero = first_nonzero(info.base_address);
    REXLOG_INFO(
        "Refii renderer exact title GUI unlock upload #{} hook={} "
        "guest=0x{:08X} arg=0x{:08X} by_locked={} base=0x{:08X} size={} "
        "{}x{} fmt={} lock=0x{:08X} write=0x{:08X} decoded=0x{:08X}/"
        "0x{:08X} chosen=0x{:08X} first raw/write/decoded/decoded_write/"
        "base/chosen={}/{}/{}/{}/{}/{} copied={} ready={} gen={}",
        g_observer.logged_exact_title_gui_unlock_uploads + 1u,
        FableHookDisplayName(symbol), GuestObjectAddress(record), args.r3,
        resolved_from_locked_address ? 1 : 0, info.base_address, info.size,
        info.width, info.height, info.format, record.locked_address,
        record.locked_write_address, decoded_lock, decoded_write,
        upload_address,
        raw_first_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(raw_first_nonzero),
        write_first_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(write_first_nonzero),
        decoded_lock_first_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(decoded_lock_first_nonzero),
        decoded_write_first_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(decoded_write_first_nonzero),
        base_first_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(base_first_nonzero),
        upload_first_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(upload_first_nonzero),
        copied_upload ? 1 : 0, upload_ready ? 1 : 0,
        static_cast<unsigned long long>(record.upload_generation));
    ++g_observer.logged_exact_title_gui_unlock_uploads;
  }

  if (!upload_ready) {
    return true;
  }

  RegisterTextureIdentityForRecordLocked(record);
  record.serial = ++g_observer.serial;
  FableDirectGuestResourceRegistry().last_resource = record;
  MarkResourceChangedLocked(record);
  RefreshQueuedDrawTextureResourceSerialLocked(record);
  UpdateDirectSnapshotCacheLocked(record);
  PropagateTitleTextureAliasUploadLocked(record);

  const uint32_t candidate_address = ZeroTextureRefreshCandidateAddress(record);
  g_observer.exact_title_texture_refresh_candidates.erase(candidate_address);
  g_observer.exact_title_texture_blank_probes.erase(candidate_address);
  record.locked_address = 0;
  record.locked_write_address = 0;
  record.locked_size = 0;
  return true;
}

void RefreshLockedTitleTextureUploadForSnapshotLocked(
    RendererResourceRecord& record) {
  if (!g_observer.memory_base ||
      !IsImageResourceKind(GuestObjectKind(record)) ||
      record.locked_address == 0 || record.locked_size == 0) {
    return;
  }

  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords)) {
    return;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (!IsExactKnownTitleTextureRefreshResource(record, info)) {
    return;
  }

  const RendererResourceRecord before = record;
  CaptureLockedBufferUpload(record, g_observer.memory_base, false);
  if (!HasNonZeroUploadData(record) &&
      ShouldCaptureTextureUploadFromFetchDwords(record, dwords,
                                                g_observer.memory_base)) {
    CaptureTextureUploadFromFetchDwords(record, dwords, g_observer.memory_base);
  }
  MarkTextureResourceChangedIfDirtyLocked(before, record);
}

void RefreshLockedTextureUploadAfterCpuTextureLayoutHelperLocked(
    std::string_view symbol, uint32_t destination, uint8_t* base) {
  if (!base || destination == 0) {
    return;
  }

  uint32_t refreshed_textures = 0;
  for (auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    if (!IsImageResourceKind(GuestObjectKind(record)) ||
        record.locked_address == 0 || record.locked_size == 0) {
      continue;
    }

    const uint32_t decoded_locked_address =
        DecodeFableGpuBufferPointer(record.locked_address);
    if (record.locked_address != destination &&
        decoded_locked_address != destination) {
      continue;
    }

    const uint64_t previous_generation = record.upload_generation;
    CaptureLockedBufferUpload(record, base);
    if (record.upload_generation != previous_generation) {
      ++refreshed_textures;
    }
  }

  if (refreshed_textures != 0 && RendererResourceTracingEnabled()) {
    REXLOG_INFO(
        "Refii renderer refreshed {} locked texture upload(s) after Fable "
        "CPU layout helper {} destination=0x{:08X}",
        refreshed_textures, symbol, destination);
  }
}

bool IsRecentCpuTiledTextureUploadShape(uint32_t width, uint32_t height) {
  return (width == 16u && height == 16u) ||
         (width == 32u && height == 32u) ||
         (width == 64u && height == 64u) ||
         (width == 128u && height == 128u) ||
         (width == 280u && height == 180u) ||
         (width == 320u && height == 180u) ||
         (width == 256u && height == 256u) ||
         (width == 512u && height == 256u) ||
         (width == 560u && height == 360u) ||
         (width == 512u && height == 512u) ||
         (width == 1024u && height == 512u);
}

uint32_t RefreshBlankTitleTextureRecordsFromRecentCpuTileLocked(
    const RecentCpuTiledTextureUpload& upload) {
  if (upload.data.empty()) {
    return 0;
  }

  uint32_t refreshed = 0;
  for (auto& [unused_address, record] :
       FableDirectGuestResourceRegistry().resources) {
    (void)unused_address;
    if (!IsImageResourceKind(GuestObjectKind(record))) {
      continue;
    }

    const TextureFetchSnapshotInfo info =
        GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record));
    if (!TextureFetchSnapshotMatchesRecentCpuTiledUpload(upload, info)) {
      continue;
    }

    if (record.upload_data.size() == info.size &&
        std::memcmp(record.upload_data.data(), upload.data.data(), info.size) == 0) {
      continue;
    }

    record.upload_data.assign(upload.data.data(), upload.data.data() + info.size);
    GuestTextureRecord texture = GuestTextureRecordFor(record);
    texture.byte_size = std::max<uint32_t>(texture.byte_size, info.size);
    texture.upload_generation = record.upload_generation + 1u;
    ApplyGuestTextureRecord(record, texture);
    RegisterTextureIdentityForRecordLocked(record);
    UpdateDirectSnapshotCacheLocked(record);

    record.serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = record;
    MarkResourceChangedLocked(record);
    RefreshQueuedDrawTextureResourceSerialLocked(record);
    PropagateTextureAliasUploadLocked(record, false);
    ++refreshed;
  }

  return refreshed;
}

void RememberRecentCpuTiledTextureUploadLocked(
    std::string_view symbol, const RendererHookArgs& args,
    uint32_t destination, uint32_t source, uint32_t width, uint32_t height,
    uint32_t format, uint32_t upload_size, uint8_t* base) {
  if (!base || symbol != "sub_8300CB48" || destination == 0 ||
      upload_size == 0 || upload_size > kMaxRecentCpuTiledTextureUploadBytes ||
      !IsRecentCpuTiledTextureUploadShape(width, height) ||
      !(IsLikelyGpuByteBufferRange(destination, upload_size) ||
        IsLikelyHighScratchByteBufferRange(destination, upload_size))) {
    return;
  }

  const uint32_t scan_size = std::min<uint32_t>(upload_size, 64u * 1024u);
  const uint32_t first_nonzero =
      FindFirstNonZeroGuestByte(base, destination, upload_size, scan_size);
  if (first_nonzero == UINT32_MAX) {
    return;
  }

  RecentCpuTiledTextureUpload upload;
  upload.serial = ++g_observer.serial;
  upload.destination = destination;
  upload.source = source;
  upload.width = width;
  upload.height = height;
  upload.format = format;
  upload.lr = args.lr;
  upload.first_nonzero = first_nonzero;
  upload.hash = HashGuestBytesFnv1a64(base, destination, scan_size);
  upload.data.resize(upload_size);

  const uint8_t* bytes =
      base + destination + GuestPhysicalHostOffset(destination);
  std::memcpy(upload.data.data(), bytes, upload.data.size());
  for (uint32_t i = 0; i < upload.first4.size() && i < upload.data.size();
       ++i) {
    upload.first4[i] = upload.data[i];
  }

  if (g_observer.recent_cpu_tiled_texture_uploads.size() >=
      kMaxRecentCpuTiledTextureUploads) {
    g_observer.recent_cpu_tiled_texture_uploads.erase(
        g_observer.recent_cpu_tiled_texture_uploads.begin());
  }

  const bool header_like =
      IsAssetChunkHeaderLike(upload.data.data(), upload.data.size());
  g_observer.recent_cpu_tiled_texture_uploads.push_back(std::move(upload));
  const uint32_t refreshed_title_records =
      RefreshBlankTitleTextureRecordsFromRecentCpuTileLocked(
          g_observer.recent_cpu_tiled_texture_uploads.back());

  if (RendererResourceTracingEnabled() &&
      g_observer.logged_recent_cpu_tiled_texture_uploads <
          kMaxRecentCpuTiledTextureUploadLogs) {
    const RecentCpuTiledTextureUpload& logged =
        g_observer.recent_cpu_tiled_texture_uploads.back();
    REXLOG_INFO(
        "Refii renderer remembered recent tiled texture upload #{} "
        "hook={} dst=0x{:08X} src=0x{:08X} size={} {}x{} fmt={} "
        "lr=0x{:08X} first_nz={} hash64k=0x{:016X} "
        "header_like={} refreshed_title_records={} "
        "first4={:02X} {:02X} {:02X} {:02X}",
        g_observer.logged_recent_cpu_tiled_texture_uploads + 1u, symbol,
        logged.destination, logged.source, logged.data.size(), logged.width,
        logged.height, logged.format, logged.lr,
        static_cast<int>(logged.first_nonzero), logged.hash,
        header_like ? 1 : 0, refreshed_title_records, logged.first4[0], logged.first4[1],
        logged.first4[2], logged.first4[3]);
    ++g_observer.logged_recent_cpu_tiled_texture_uploads;
  }
}

void SnapshotGuestResource(RendererResourceRecord& record, uint8_t* base);
RendererResourceRecord& StoreGuestResourceLocked(RendererResourceRecord record,
                                                 bool count_create,
                                                 bool trace_create);

uint32_t RefreshTextureUploadFromCpuLayoutDestinationCallsiteLocked(
    std::string_view symbol, const RendererHookArgs& args,
    uint32_t destination, uint32_t width, uint32_t height, uint32_t pitch,
    uint32_t bits, uint8_t* base) {
  const bool trace_tile_refresh =
      RendererResourceTracingEnabled() || RendererVerboseTracingEnabled();
  const bool callsite_matches =
      base != nullptr && destination != 0 && symbol == "sub_8300CB48" &&
      (args.lr == 0x82B95FC0u || args.lr == 0x82B95C60u);
  if (trace_tile_refresh &&
      g_observer.logged_tile_destination_entry_probes < 24u &&
      symbol == "sub_8300CB48") {
    REXLOG_INFO(
        "Refii renderer tile destination refresh entry #{} hook={} "
        "accepted={} dst=0x{:08X} {}x{} fmt={} pitch={} bits={} "
        "lr=0x{:08X} src=0x{:08X} r17=0x{:08X} r21=0x{:08X} "
        "r27=0x{:08X} r30=0x{:08X}",
        g_observer.logged_tile_destination_entry_probes + 1u, symbol,
        callsite_matches ? 1 : 0, destination, width, height, args.r6, pitch,
        bits, args.lr, args.r10, args.r17, args.r21, args.r27, args.r30);
    ++g_observer.logged_tile_destination_entry_probes;
  }
  if (!callsite_matches) {
    return 0;
  }

  uint32_t upload_width = width;
  uint32_t upload_height = height;
  uint64_t upload_size64 = 0;
  if (symbol == "sub_8300CB48") {
    TextureLevelUploadShape level_shape;
    if (DecodeXGTextureLevelUploadShape(width, height, args.r5, args.r6,
                                        level_shape)) {
      upload_width = level_shape.width;
      upload_height = level_shape.height;
      upload_size64 = level_shape.byte_size;
    }
  }
  if (upload_size64 == 0) {
    uint32_t bytes_per_unit = bits != 0 ? bits : 4u;
    bytes_per_unit = std::clamp<uint32_t>(bytes_per_unit, 1u, 16u);
    upload_size64 = static_cast<uint64_t>(width) * height * bytes_per_unit;
    if (upload_size64 == 0) {
      upload_size64 = 64u * 1024u;
    }
  }
  const uint32_t fallback_upload_size =
      static_cast<uint32_t>(std::min<uint64_t>(
          upload_size64, kMaxRecentCpuTiledTextureUploadBytes));
  if (fallback_upload_size == 0 ||
      !(IsLikelyGpuByteBufferRange(destination, fallback_upload_size) ||
        IsLikelyHighScratchByteBufferRange(destination, fallback_upload_size))) {
    if (g_observer.logged_tile_destination_reject_probes < 16u) {
      REXLOG_INFO(
          "Refii renderer tile destination refresh reject #{} hook={} "
          "dst=0x{:08X} fallback_size={} {}x{} fmt={} pitch={} "
          "lr=0x{:08X} src=0x{:08X} gpu_range={} high_range={}",
          g_observer.logged_tile_destination_reject_probes + 1u, symbol,
          destination, fallback_upload_size, width, height, args.r6, pitch,
          args.lr, args.r10,
          IsLikelyGpuByteBufferRange(destination, fallback_upload_size) ? 1 : 0,
          IsLikelyHighScratchByteBufferRange(destination, fallback_upload_size)
              ? 1
              : 0);
      ++g_observer.logged_tile_destination_reject_probes;
    }
    return 0;
  }

  RememberRecentCpuTiledTextureUploadLocked(
      symbol, args, destination, args.r10, upload_width, upload_height, args.r6,
      fallback_upload_size, base);

  std::array<uint32_t, 32> candidates = {};
  uint32_t count = 0;
  const auto push_candidate = [&](uint32_t candidate) {
    if (candidate == 0 || count >= candidates.size()) {
      return;
    }
    if (std::find(candidates.begin(), candidates.begin() + count,
                  candidate) != candidates.begin() + count) {
      return;
    }
    candidates[count++] = candidate;
  };
  const auto push_fetch_header_candidate = [&](uint32_t header) {
    if (!IsLikelyGuestDwordAddress(header)) {
      return;
    }

    const std::array<uint32_t, 6> dwords =
        LoadTextureFetchDwords(base, header);
    if (!HasTextureFetchDwords(dwords)) {
      return;
    }

    const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
    if (!IsUsableTextureFetchSnapshot(info)) {
      return;
    }

    auto it = FableDirectGuestResourceRegistry().resources.find(info.base_address);
    RendererResourceRecord* record = nullptr;
    if (it == FableDirectGuestResourceRegistry().resources.end()) {
      RendererResourceRecord created =
          CreateGuestTextureResource(info.base_address, info.base_address);
      GuestTextureRecord texture = GuestTextureRecordFor(created);
      texture.normalized_fetch_dwords = dwords;
      texture.raw_fetch_dwords = dwords;
      texture.byte_size = std::max<uint32_t>(texture.byte_size, info.size);
      ApplyGuestTextureRecord(created, texture);
      created.serial = ++g_observer.serial;
      record = &StoreGuestResourceLocked(std::move(created), true, true);
    } else if (IsImageResourceKind(GuestObjectKind(it->second))) {
      record = &it->second;
      GuestTextureRecord texture = GuestTextureRecordFor(*record);
      texture.normalized_fetch_dwords = dwords;
      if (GuestObjectKind(*record) == RendererResourceKind::kTexture) {
        texture.raw_fetch_dwords = dwords;
      }
      texture.byte_size = std::max<uint32_t>(texture.byte_size, info.size);
      ApplyGuestTextureRecord(*record, texture);
    }

    if (record != nullptr) {
      push_candidate(GuestObjectAddress(*record) != 0
                         ? GuestObjectAddress(*record)
                         : info.base_address);
      push_candidate(info.base_address);
    }
  };
  const auto push_texture_object_candidate = [&](uint32_t object) {
    push_candidate(object);
    if (IsLikelyFableObjectAddress(object)) {
      push_fetch_header_candidate(object + 28u);
    }
  };
  const auto push_loaded_candidate = [&](uint32_t address) {
    if (IsLikelyGuestDwordAddress(address)) {
      push_candidate(LoadGuestU32(base, address));
    }
  };
  const auto push_loaded_texture_object_candidate = [&](uint32_t address) {
    if (!IsLikelyGuestDwordAddress(address)) {
      return;
    }
    push_texture_object_candidate(LoadGuestU32(base, address));
  };

  push_texture_object_candidate(args.r17);
  // In Fable's 2D texture upload helper (caller 0x82B95FBC), r17 is the
  // surface-level wrapper and r21 still carries the owning texture object.
  // The texture may not have been seen by SetTexture yet, so include both the
  // owner and the owner pointers reachable from the surface wrapper.
  push_texture_object_candidate(args.r21);
  push_texture_object_candidate(args.r27);
  push_texture_object_candidate(args.r30);
  push_fetch_header_candidate(args.r17);
  push_fetch_header_candidate(args.r17 + 28u);
  push_fetch_header_candidate(args.r30);
  push_fetch_header_candidate(args.r30 + 28u);
  push_loaded_texture_object_candidate(args.r17 + 24u);
  push_loaded_texture_object_candidate(args.r21 + 24u);
  push_loaded_texture_object_candidate(args.r30 + 4u);
  push_loaded_texture_object_candidate(args.r30 + 24u);

  if (trace_tile_refresh &&
      g_observer.logged_tile_destination_association_probes < 16u) {
    const uint32_t surface_owner =
        IsLikelyGuestDwordAddress(args.r17 + 24u)
            ? LoadGuestU32(base, args.r17 + 24u)
            : 0u;
    const std::array<uint32_t, 6> owner_fetch =
        IsLikelyFableObjectAddress(surface_owner)
            ? LoadTextureFetchDwords(base, surface_owner + 28u)
            : std::array<uint32_t, 6>{};
    const TextureFetchSnapshotInfo owner_info =
        GetTextureFetchSnapshotInfo(owner_fetch);
    const uint32_t scan_size =
        std::min<uint32_t>(fallback_upload_size, 64u * 1024u);
    const uint32_t first_nonzero = FindFirstNonZeroGuestByte(
        base, destination, fallback_upload_size, scan_size);
    const uint64_t destination_hash =
        HashGuestBytesFnv1a64(base, destination, scan_size);
    REXLOG_INFO(
        "Refii renderer tile destination association probe #{} hook={} "
        "dst=0x{:08X} size={} {}x{} fmt={} pitch={} lr=0x{:08X} "
        "src=0x{:08X} r17=0x{:08X} owner=0x{:08X} r21=0x{:08X} "
        "r27=0x{:08X} r30=0x{:08X} candidates={} owner_fetch={} "
        "owner_base=0x{:08X} owner_size={} owner_shape={}x{} "
        "owner_fmt={} dst_nz={} dst_hash64k=0x{:016X}",
        g_observer.logged_tile_destination_association_probes + 1u, symbol,
        destination, fallback_upload_size, width, height, args.r6, pitch,
        args.lr, args.r10, args.r17, surface_owner, args.r21, args.r27,
        args.r30, count, HasTextureFetchDwords(owner_fetch) ? 1 : 0,
        owner_info.base_address, owner_info.size, owner_info.width,
        owner_info.height, owner_info.format,
        first_nonzero == UINT32_MAX ? -1 : static_cast<int>(first_nonzero),
        destination_hash);
    ++g_observer.logged_tile_destination_association_probes;
  }

  uint32_t refreshed_textures = 0;
  std::unordered_set<uint32_t> refreshed_objects;
  for (uint32_t i = 0; i < count; ++i) {
    RendererResourceRecord* record = FindResourceRecordLocked(candidates[i]);
    if (record == nullptr && IsLikelyFableObjectAddress(candidates[i])) {
      RendererResourceRecord created =
          CreateGuestTextureResource(candidates[i], candidates[i]);
      SnapshotGuestResource(created, base);
      if (HasTextureFetchDwords(TextureFetchDwordsForRecord(created))) {
        created.serial = ++g_observer.serial;
        record = &StoreGuestResourceLocked(std::move(created), true, true);
      }
    }
    if (record == nullptr || !IsImageResourceKind(GuestObjectKind(*record))) {
      continue;
    }

    const TextureFetchSnapshotInfo record_info =
        GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(*record));
    if (!TileDestinationMatchesTextureFetchBase(destination, record_info)) {
      if (g_observer.logged_tile_destination_mip_skip_probes < 24u) {
        REXLOG_INFO(
            "Refii renderer tile destination base mismatch #{} hook={} "
            "dst=0x{:08X} decoded=0x{:08X} record=0x{:08X} "
            "base=0x{:08X} size={} shape={}x{} fmt={}",
            g_observer.logged_tile_destination_mip_skip_probes + 1u, symbol,
            destination, DecodeFableGpuMemoryPointer(destination),
            GuestObjectAddress(*record), record_info.base_address,
            record_info.size, record_info.width, record_info.height,
            record_info.format);
        ++g_observer.logged_tile_destination_mip_skip_probes;
      }
      continue;
    }

    const uint32_t object_address = GuestObjectAddress(*record);
    if (!refreshed_objects.insert(object_address).second) {
      continue;
    }

    uint32_t upload_size = TextureSnapshotSizeForRecord(*record);
    if (upload_size == 0 || upload_size > kMaxTextureUploadSnapshotSize ||
        !(IsLikelyGpuByteBufferRange(destination, upload_size) ||
          IsLikelyHighScratchByteBufferRange(destination, upload_size))) {
      upload_size = fallback_upload_size;
    }

    const uint64_t previous_generation = record->upload_generation;
    if (!CopyGuestBufferUploadData(*record, base, destination, upload_size) ||
        record->upload_generation == previous_generation) {
      continue;
    }

    GuestTextureRecord texture = GuestTextureRecordFor(*record);
    texture.byte_size = std::max<uint32_t>(texture.byte_size, upload_size);
    texture.upload_generation = record->upload_generation;
    ApplyGuestTextureRecord(*record, texture);
    RegisterTextureIdentityForRecordLocked(*record);
    record->serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = *record;
    MarkResourceChangedLocked(*record);
    RefreshQueuedDrawTextureResourceSerialLocked(*record);
    PropagateTextureAliasUploadLocked(*record, false);
    if (IsExactKnownTitleTextureRefreshResource(*record, record_info)) {
      const uint32_t root_header =
          object_address != 0 ? object_address : candidates[i];
      RememberExactTitleTextureRefreshCandidateLocked(
          *record, record_info, FableHookDisplayName(symbol),
          ExactTitleTextureProducerSymbolAddress(symbol), args.lr, root_header);
    }
    ++refreshed_textures;

    if (RendererResourceTracingEnabled() &&
        g_observer.logged_tile_destination_uploads < 32u) {
      const uint32_t scan_size = std::min<uint32_t>(upload_size, 64u * 1024u);
      const uint32_t destination_nonzero = FindFirstNonZeroGuestByte(
          base, destination, upload_size, scan_size);
      const uint64_t destination_hash =
          HashGuestBytesFnv1a64(base, destination, scan_size);
      REXLOG_INFO(
          "Refii renderer tile destination upload #{} hook={} "
          "object=0x{:08X} candidate=0x{:08X} dst=0x{:08X} size={} "
          "{}x{} gen={} dst_nz={} dst_hash64k=0x{:016X}",
          g_observer.logged_tile_destination_uploads + 1u, symbol,
          object_address, candidates[i], destination, upload_size, width,
          height, static_cast<unsigned long long>(record->upload_generation),
          destination_nonzero == UINT32_MAX
              ? -1
              : static_cast<int>(destination_nonzero),
          destination_hash);
      ++g_observer.logged_tile_destination_uploads;
    }
  }

  return refreshed_textures;
}

uint32_t LockedResourceSizeForHook(std::string_view symbol,
                                   const RendererHookArgs& args,
                                   const RendererResourceRecord& record) {
  if (symbol == "rex_lhGfx_LockBuffer" && args.r10 != 0) {
    return args.r10;
  }
  if (IsImageResourceKind(GuestObjectKind(record))) {
    const uint32_t texture_size =
        TextureSnapshotSizeForRelatedRecordLocked(record);
    if (texture_size != 0) {
      return texture_size;
    }
  }
  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  if (buffer.byte_size != 0) {
    return buffer.byte_size;
  }
  const GuestShaderRecord shader = GuestShaderRecordFor(record);
  if (shader.bytecode_size != 0) {
    return shader.bytecode_size;
  }
  const GuestVertexDeclarationRecord declaration =
      GuestVertexDeclarationRecordFor(record);
  if (declaration.elements_size != 0) {
    return declaration.elements_size;
  }
  return args.r10;
}

uint32_t LockedResourceWriteAddressForHook(
    std::string_view symbol, const RendererHookArgs& args, uint32_t result,
    uint8_t* base, const RendererResourceRecord& record) {
  uint32_t write_address = result;
  const auto accept_candidate = [&](uint32_t candidate) {
    if (candidate == 0) {
      return false;
    }
    const uint32_t lock_size = LockedResourceSizeForHook(symbol, args, record);
    return IsLikelyGpuByteBufferRange(candidate, lock_size) ||
           IsLikelyHighScratchByteBufferRange(candidate, lock_size);
  };

  if (!base || !IsImageResourceKind(GuestObjectKind(record))) {
    return write_address;
  }

  if (symbol == "sub_82278138" && IsLikelyGuestDwordAddress(args.r8)) {
    const uint32_t surface_write_address = LoadGuestU32(base, args.r8);
    if (accept_candidate(surface_write_address)) {
      write_address = surface_write_address;
    }
  } else if (symbol == "sub_82B986C8" &&
             IsLikelyGuestDwordAddress(args.r5 + 8u)) {
    const uint32_t rect_write_address = LoadGuestU32(base, args.r5 + 8u);
    if (accept_candidate(rect_write_address)) {
      write_address = rect_write_address;
    }
  } else if (symbol == "sub_82B98778" &&
             IsLikelyGuestDwordAddress(args.r5)) {
    const uint32_t surface_rect_write_address = LoadGuestU32(base, args.r5);
    if (accept_candidate(surface_rect_write_address)) {
      write_address = surface_rect_write_address;
    }
  }

  return write_address;
}

void SnapshotGuestResource(RendererResourceRecord& record, uint8_t* base);
void TraceResourceCreate(const RendererResourceRecord& record);
RendererResourceRecord& StoreGuestResourceLocked(RendererResourceRecord record,
                                                 bool count_create,
                                                 bool trace_create = false);

FableGuestResourceSnapshot BuildDirectSnapshotCacheEntryLocked(
    const RendererResourceRecord& record) {
  FableGuestResourceSnapshot snapshot;
  const RendererResourceKind kind = GuestObjectKind(record);
  snapshot.kind = PublicResourceKind(kind);
  snapshot.guest_address = GuestObjectAddress(record);
  snapshot.parent_address = GuestObjectParentAddress(record);
  if (IsBufferResourceKind(kind)) {
    const GuestBufferRecord buffer = GuestBufferRecordFor(record);
    snapshot.buffer = {
        .object_address = buffer.object_address,
        .gpu_address = buffer.gpu_address,
        .byte_size = buffer.byte_size,
        .stride_or_index_format = buffer.stride_or_index_format,
        .header_address = buffer.header_address,
        .upload_hash = buffer.upload_hash,
        .upload_generation = buffer.upload_generation,
        .scratch = buffer.scratch,
    };
  } else if (IsShaderResourceKind(kind)) {
    const GuestShaderRecord shader = GuestShaderRecordFor(record);
    snapshot.shader = {
        .object_address = shader.object_address,
        .bytecode_address = shader.bytecode_address,
        .bytecode_size = shader.bytecode_size,
        .bytecode_offset = shader.bytecode_offset,
        .bytecode_hash = shader.bytecode_hash,
    };
  } else if (kind == RendererResourceKind::kVertexDeclaration) {
    const GuestVertexDeclarationRecord declaration =
        GuestVertexDeclarationRecordFor(record);
    snapshot.vertex_declaration = {
        .object_address = declaration.object_address,
        .elements_address = declaration.elements_address,
        .elements_size = declaration.elements_size,
        .element_count = declaration.element_count,
        .max_stream = declaration.max_stream,
        .stream_mask = declaration.stream_mask,
        .declaration_flags = declaration.declaration_flags,
        .elements_hash = declaration.elements_hash,
    };
  } else if (IsSurfaceResourceKind(kind)) {
    const GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
    snapshot.surface = {
        .object_address = surface.object_address,
        .parent_address = surface.parent_address,
        .render_target_info = surface.render_target_info,
        .depth_stencil_info = surface.depth_stencil_info,
        .packed_extent = surface.packed_extent,
        .d3d_format = surface.d3d_format,
        .pitch_msaa = surface.pitch_msaa,
        .byte_size = surface.byte_size,
    };
  } else if (IsImageResourceKind(kind)) {
    const GuestTextureRecord texture =
        TextureRecordWithFetchSnapshotIdentity(record);
    snapshot.texture = {
        .object_address = texture.object_address,
        .parent_address = texture.parent_address,
        .base_address = texture.base_address,
        .byte_size = texture.byte_size,
        .raw_fetch_dwords = texture.raw_fetch_dwords,
        .normalized_fetch_dwords = texture.normalized_fetch_dwords,
        .upload_generation = texture.upload_generation,
        .upload_hash = record.raw.hash,
    };
  }
  snapshot.upload_generation = record.upload_generation;
  snapshot.serial = record.serial;
  snapshot.scratch = record.scratch;
  return snapshot;
}

bool ShouldCacheDirectSnapshotPayload(
    const FableGuestResourceSnapshot& snapshot,
    const RendererResourceRecord& record) {
  const auto is_fable_title_billboard_atlas_payload =
      [](const FableGuestResourceSnapshot& snapshot, size_t upload_size) {
        if (snapshot.kind != RendererTrackedResourceKind::kTexture ||
            upload_size != 512u * 1024u) {
          return false;
        }

        constexpr uint32_t kTitleBillboardAtlasBase = 0x16C40000u;
	        const auto is_known_object = [](uint32_t address) {
	          return address == 0x4C132C70u || address == 0x4C132CA4u ||
	                 address == 0x4C132CD8u ||
	                 address == 0x4C133150u || address == 0x4C133184u;
	        };
        const bool known_carrier =
               is_known_object(snapshot.guest_address) ||
               is_known_object(snapshot.parent_address) ||
               is_known_object(snapshot.texture.object_address) ||
               is_known_object(snapshot.texture.parent_address) ||
               snapshot.guest_address == kTitleBillboardAtlasBase ||
               snapshot.parent_address == kTitleBillboardAtlasBase ||
               snapshot.texture.object_address == kTitleBillboardAtlasBase ||
               snapshot.texture.parent_address == kTitleBillboardAtlasBase;
        return known_carrier &&
               snapshot.texture.base_address == kTitleBillboardAtlasBase;
      };

  if (record.upload_data.empty()) {
    return false;
  }
  if (record.upload_data.size() > kMaxCachedDirectUploadBytes &&
      !is_fable_title_billboard_atlas_payload(snapshot,
                                             record.upload_data.size())) {
    return false;
  }

  switch (snapshot.kind) {
    case RendererTrackedResourceKind::kVertexBuffer:
    case RendererTrackedResourceKind::kIndexBuffer:
    case RendererTrackedResourceKind::kVertexDeclaration:
      return true;
    case RendererTrackedResourceKind::kTexture:
      return true;
    default:
      return false;
  }
}

void UpdateDirectSnapshotCacheLocked(const RendererResourceRecord& record) {
  FableGuestResourceSnapshot snapshot =
      BuildDirectSnapshotCacheEntryLocked(record);
  if (snapshot.guest_address == 0 ||
      snapshot.kind == RendererTrackedResourceKind::kUnknown) {
    return;
  }

  if (ShouldCacheDirectSnapshotPayload(snapshot, record)) {
    snapshot.upload_data = record.upload_data;
  } else {
    snapshot.upload_data.clear();
  }

  std::lock_guard<std::mutex> cache_lock(g_direct_snapshot_cache_mutex);
  g_direct_snapshot_cache[snapshot.guest_address] = std::move(snapshot);
  while (g_direct_snapshot_cache.size() > kMaxDirectSnapshotCacheEntries) {
    g_direct_snapshot_cache.erase(g_direct_snapshot_cache.begin());
  }
}

void EraseDirectSnapshotCacheEntry(uint32_t address) {
  if (address == 0) {
    return;
  }
  std::lock_guard<std::mutex> cache_lock(g_direct_snapshot_cache_mutex);
  g_direct_snapshot_cache.erase(address);
}

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

bool ResolveTextureLockObject(std::string_view symbol,
                              const RendererHookArgs& args,
                              uint8_t* base,
                              uint32_t& texture_object,
                              uint32_t& surface_object,
                              uint32_t& block_or_level) {
  texture_object = 0;
  surface_object = 0;
  block_or_level = 0;
  if (!base) {
    return false;
  }

  if (symbol == "sub_82B98930") {
    texture_object = args.r3;
    block_or_level = args.r4;
  } else if (symbol == "sub_82B98C48") {
    surface_object = args.r3;
    if (!IsLikelyFableObjectAddress(surface_object)) {
      return false;
    }
    texture_object = LoadGuestU32(base, surface_object + 24u);
    block_or_level = (LoadGuestU32(base, surface_object + 28u) >> 22u) & 0x3Fu;
  } else if (LooksLikeTextureLockResource(symbol, args, base)) {
    texture_object = args.r3;
    block_or_level = args.r5;
  } else {
    return false;
  }

  if (!IsLikelyFableObjectAddress(texture_object)) {
    return false;
  }

  return HasTextureFetchDwords(LoadTextureFetchDwords(base, texture_object + 28u));
}

bool EnsureTextureRecordForLockLocked(std::string_view symbol,
                                      const RendererHookArgs& args,
                                      uint8_t* base,
                                      RendererResourceRecord*& record) {
  uint32_t texture_object = 0;
  uint32_t surface_object = 0;
  uint32_t block_or_level = 0;
  if (!ResolveTextureLockObject(symbol, args, base, texture_object,
                                surface_object, block_or_level)) {
    return false;
  }

  auto it = FableDirectGuestResourceRegistry().resources.find(texture_object);
  if (it != FableDirectGuestResourceRegistry().resources.end() &&
      IsImageResourceKind(GuestObjectKind(it->second))) {
    record = &it->second;
    return true;
  }

  RendererResourceRecord created =
      CreateGuestTextureResource(texture_object, texture_object);
  SnapshotGuestResource(created, base);
  created.serial = ++g_observer.serial;

  RendererResourceRecord& stored =
      StoreGuestResourceLocked(std::move(created), true, true);
  record = &stored;
  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_texture_lock_records < 16u) {
    const GuestTextureRecord texture = GuestTextureRecordFor(*record);
    REXLOG_INFO(
        "Refii renderer texture lock record #{} symbol={} object=0x{:08X} "
        "surface=0x{:08X} block_or_level={} "
        "packed={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        g_observer.logged_texture_lock_records + 1u,
        FableHookDisplayName(symbol),
        texture_object, surface_object, block_or_level,
        texture.raw_fetch_dwords[0], texture.raw_fetch_dwords[1],
        texture.raw_fetch_dwords[2], texture.raw_fetch_dwords[3],
        texture.raw_fetch_dwords[4], texture.raw_fetch_dwords[5]);
    ++g_observer.logged_texture_lock_records;
  }
  return true;
}

bool ValidateVertexDeclarationElements(uint8_t* base, uint32_t elements_address,
                                       uint32_t element_count);

bool PopulateVertexDeclarationRecordFromObject(RendererResourceRecord& record,
                                               uint8_t* base) {
  const uint32_t object_address = GuestObjectAddress(record);
  if (!base || object_address == 0 ||
      GuestObjectKind(record) != RendererResourceKind::kVertexDeclaration) {
    return false;
  }

  const uint32_t element_count = LoadGuestU32(base, object_address + 0x18);
  if (element_count == 0 ||
      element_count > kMaxReasonableFableVertexDeclarationElements) {
    return false;
  }

  const uint32_t element_bytes = element_count * 12u;
  const uint32_t elements_address = object_address + 0x34;
  if (!ValidateVertexDeclarationElements(base, elements_address,
                                         element_count)) {
    return false;
  }

  GuestVertexDeclarationRecord declaration;
  declaration.object_address = object_address;
  declaration.elements_address = elements_address;
  declaration.elements_size = element_bytes;
  declaration.element_count = element_count;
  declaration.max_stream = LoadGuestU32(base, object_address + 0x1C);
  declaration.stream_mask = LoadGuestU32(base, object_address + 0x20);
  declaration.declaration_flags =
      LoadGuestU32(base, object_address + 0x30);
  declaration.elements_hash =
      HashGuestBytesFnv1a64(base, elements_address, element_bytes);
  ApplyGuestVertexDeclarationRecord(record, declaration);
  if (!IsReadableGuestByteRange(base, elements_address, element_bytes)) {
    return false;
  }
  record.upload_data.resize(element_bytes);
  std::memcpy(record.upload_data.data(),
              base + elements_address + GuestPhysicalHostOffset(elements_address),
              element_bytes);
  ++record.upload_generation;
  return declaration.elements_hash != 0;
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
      GuestObjectKind(record) != RendererResourceKind::kVertexDeclaration) {
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
  GuestVertexDeclarationRecord declaration;
  declaration.object_address = GuestObjectAddress(record);
  declaration.elements_address = elements_address;
  declaration.elements_size = element_bytes;
  declaration.element_count = element_count;
  declaration.max_stream = max_stream;
  declaration.stream_mask = stream_mask;
  declaration.elements_hash =
      HashGuestBytesFnv1a64(base, elements_address, element_bytes);
  ApplyGuestVertexDeclarationRecord(record, declaration);
  record.upload_data.resize(element_bytes);
  std::memcpy(record.upload_data.data(),
              base + elements_address +
                  GuestPhysicalHostOffset(elements_address),
              element_bytes);
  ++record.upload_generation;
  return declaration.elements_hash != 0;
}

bool EnsureVertexDeclarationUploadPayload(RendererResourceRecord& record,
                                          uint8_t* base) {
  if (!base || GuestObjectKind(record) != RendererResourceKind::kVertexDeclaration ||
      !record.upload_data.empty()) {
    return false;
  }

  GuestVertexDeclarationRecord declaration =
      GuestVertexDeclarationRecordFor(record);
  if (declaration.object_address == 0 || declaration.elements_address == 0 ||
      declaration.element_count == 0 || declaration.elements_size == 0 ||
      declaration.element_count > kMaxReasonableFableVertexDeclarationElements ||
      declaration.elements_size != declaration.element_count * 12u) {
    return false;
  }

  if (!ValidateVertexDeclarationElements(base, declaration.elements_address,
                                         declaration.element_count)) {
    return false;
  }

  const uint64_t elements_hash = HashGuestBytesFnv1a64(
      base, declaration.elements_address, declaration.elements_size);
  if (elements_hash == 0 ||
      (declaration.elements_hash != 0 &&
       declaration.elements_hash != elements_hash)) {
    return false;
  }

  if (declaration.elements_hash == 0) {
    declaration.elements_hash = elements_hash;
    ApplyGuestVertexDeclarationRecord(record, declaration);
  }

  record.upload_data.resize(declaration.elements_size);
  std::memcpy(record.upload_data.data(),
              base + declaration.elements_address +
                  GuestPhysicalHostOffset(declaration.elements_address),
              declaration.elements_size);
  ++record.upload_generation;
  return true;
}

bool PopulateVertexDeclarationRecordFromNearbyObject(
    RendererResourceRecord& record, uint8_t* base) {
  const uint32_t object_address = GuestObjectAddress(record);
  if (!base || object_address == 0 ||
      GuestObjectKind(record) != RendererResourceKind::kVertexDeclaration) {
    return false;
  }

  auto adopt_candidate = [&](RendererResourceRecord& candidate) {
    ApplyGuestVertexDeclarationRecord(
        record, GuestVertexDeclarationRecordFor(candidate));
    record.upload_generation = candidate.upload_generation;
    record.upload_data = std::move(candidate.upload_data);
  };

  constexpr uint32_t kPointerCandidateOffsets[] = {
      0x00u, 0x04u, 0x08u, 0x10u, 0x18u, 0x20u,
      0x24u, 0x30u, 0x38u, 0x3Cu, 0x40u, 0x44u, 0x48u};
  for (const uint32_t pointer_offset : kPointerCandidateOffsets) {
    const uint32_t candidate_address =
        LoadGuestU32(base, object_address + pointer_offset);
    if (candidate_address == 0 || candidate_address == object_address ||
        !IsLikelyFableObjectAddress(candidate_address)) {
      continue;
    }

    RendererResourceRecord candidate = record;
    SetGuestResourceIdentity(candidate, GuestObjectKind(candidate),
                             candidate_address,
                             GuestObjectParentAddress(candidate));
    if (PopulateVertexDeclarationRecordFromObject(candidate, base) ||
        PopulateVertexDeclarationRecordFromElements(candidate, base,
                                                    candidate_address) ||
        PopulateVertexDeclarationRecordFromElements(candidate, base,
                                                    candidate_address + 0x34u)) {
      adopt_candidate(candidate);
      if (GuestObjectParentAddress(record) == 0) {
        SetGuestResourceParent(record, candidate_address);
      }
      return true;
    }
  }

  constexpr int32_t kCandidateOffsets[] = {
      -0x40, -0x30, -0x20, -0x10, 0x10, 0x20, 0x30, 0x40};
  const uint32_t original_address = object_address;
  for (const int32_t offset : kCandidateOffsets) {
    const uint32_t candidate_address =
        static_cast<uint32_t>(original_address + offset);
    if (candidate_address == 0) {
      continue;
    }

    RendererResourceRecord candidate = record;
    SetGuestResourceIdentity(candidate, GuestObjectKind(candidate),
                             candidate_address,
                             GuestObjectParentAddress(candidate));
    if (!PopulateVertexDeclarationRecordFromObject(candidate, base)) {
      continue;
    }

    adopt_candidate(candidate);
    if (GuestObjectParentAddress(record) == 0) {
      SetGuestResourceParent(record, candidate_address);
    }
    return true;
  }

  return false;
}

bool HasPlausibleSurfaceHeader(const RendererResourceRecord& record);
bool SnapshotInlineSurfaceDescriptor(RendererResourceRecord& record,
                                     uint8_t* base);

void SnapshotGuestResource(RendererResourceRecord& record, uint8_t* base) {
  const RendererResourceKind kind = GuestObjectKind(record);
  const uint32_t object_address = GuestObjectAddress(record);
  if (!base || object_address == 0) {
    return;
  }

  if (IsBufferResourceKind(kind)) {
    GuestBufferRecord buffer = GuestBufferRecordFor(record);
    buffer.object_address = object_address;
    buffer.gpu_address = LoadGuestU32(base, object_address + 0);
    buffer.byte_size = LoadGuestU32(base, object_address + 4);
    buffer.stride_or_index_format =
        kind == RendererResourceKind::kVertexBuffer
            ? LoadGuestU8(base, object_address + 8)
            : LoadGuestU32(base, object_address + 8);
    ApplyGuestBufferRecord(record, buffer);
    CaptureFableBufferUploadFromHeader(record, base);
    return;
  }
  if (IsShaderResourceKind(kind)) {
    return;
  }
  if (kind == RendererResourceKind::kVertexDeclaration) {
    if (PopulateVertexDeclarationRecordFromObject(record, base)) {
      return;
    }
    if (PopulateVertexDeclarationRecordFromElements(record, base,
                                                   object_address)) {
      return;
    }
    if (PopulateVertexDeclarationRecordFromElements(
            record, base, object_address + 0x34u)) {
      return;
    }
    PopulateVertexDeclarationRecordFromNearbyObject(record, base);
    return;
  }

  SetGuestResourceRawHeader(record, LoadGuestU32(base, object_address + 0),
                            LoadGuestU32(base, object_address + 4),
                            LoadGuestU32(base, object_address + 20),
                            LoadGuestU32(base, object_address + 24));
  if (IsSurfaceResourceKind(kind)) {
    // Fable D3DSurface headers don't use texture-fetch dword layout:
    // +18 pitch/MSAA, +1C render-target info, +20 depth/stencil info,
    // +24 packed width/height, +28 D3DFORMAT, +2C byte size.
    GuestSurfaceRecord surface;
    surface.object_address = object_address;
    surface.parent_address = GuestObjectParentAddress(record);
    surface.render_target_info = LoadGuestU32(
        base, object_address + kFableSurfaceRenderTargetInfoOffset);
    surface.depth_stencil_info = LoadGuestU32(
        base, object_address + kFableSurfaceDepthStencilInfoOffset);
    surface.packed_extent = LoadGuestU32(
        base, object_address + kFableSurfacePackedExtentOffset);
    surface.d3d_format = LoadGuestU32(
        base, object_address + kFableSurfaceD3DFormatOffset);
    surface.pitch_msaa = LoadGuestU32(
        base, object_address + kFableSurfacePitchMsaaOffset);
    surface.byte_size =
        LoadGuestU32(base, object_address + kFableSurfaceByteSizeOffset);
    ApplyGuestSurfaceRecord(record, surface);
    if (!HasPlausibleSurfaceHeader(record)) {
      SnapshotInlineSurfaceDescriptor(record, base);
    }
    return;
  }
  if (IsImageResourceKind(kind)) {
    GuestTextureRecord texture = GuestTextureRecordFor(record);
    texture.object_address = object_address;
    texture.parent_address = GuestObjectParentAddress(record);
    texture.raw_fetch_dwords = RawFetchDwordsForRecord(record);
    texture.raw_fetch_dwords[0] = LoadGuestU32(base, object_address + 28);
    texture.raw_fetch_dwords[1] = LoadGuestU32(base, object_address + 32);
    texture.raw_fetch_dwords[2] = LoadGuestU32(base, object_address + 36);
    texture.raw_fetch_dwords[3] = LoadGuestU32(base, object_address + 40);
    texture.raw_fetch_dwords[4] = LoadGuestU32(base, object_address + 44);
    texture.raw_fetch_dwords[5] = LoadGuestU32(base, object_address + 48);
    ApplyGuestTextureRecord(record, texture);
  }
}

bool PopulateShaderRecordFromObject(RendererResourceRecord& record,
                                    uint8_t* base) {
  const RendererResourceKind kind = GuestObjectKind(record);
  const uint32_t object_address = GuestObjectAddress(record);
  if (!base || object_address == 0 || !IsShaderResourceKind(kind)) {
    return false;
  }

  const GuestShaderRecord previous_shader = GuestShaderRecordFor(record);

  uint32_t bytecode_pointer = 0;
  uint32_t bytecode_offset = 0;
  uint32_t bytecode_size = 0;
  if (kind == RendererResourceKind::kVertexShader) {
    bytecode_pointer = LoadGuestU32(base, object_address + 32);
    bytecode_offset = LoadGuestU32(base, object_address + 876);
    bytecode_size = LoadGuestU32(base, object_address + 880);
  } else if (kind == RendererResourceKind::kPixelShader) {
    bytecode_pointer = LoadGuestU32(base, object_address + 24);
    bytecode_offset = LoadGuestU32(base, object_address + 44);
    bytecode_size = LoadGuestU32(base, object_address + 48);
  }

  if (bytecode_pointer == 0 || bytecode_size == 0 ||
      bytecode_size > kMaxTrackedShaderBytecodeSize) {
    return false;
  }

  GuestShaderRecord shader;
  shader.object_address = object_address;
  shader.bytecode_address = bytecode_pointer;
  shader.bytecode_size = bytecode_size;
  shader.bytecode_offset = bytecode_offset;
  if (previous_shader.bytecode_hash != 0 &&
      previous_shader.bytecode_address == bytecode_pointer &&
      previous_shader.bytecode_size == bytecode_size) {
    // Shader create hooks see the full Fable shader container, which is the
    // stable blob shape used by extraction. The live shader object points at
    // the payload. Keep the create-root hash when the object shape still
    // matches so later refreshes do not make a resolvable shader disappear.
    shader.bytecode_hash = previous_shader.bytecode_hash;
  } else {
    shader.bytecode_hash =
        HashGuestBytesFnv1a64(base, bytecode_pointer, bytecode_size);
  }
  ApplyGuestShaderRecord(record, shader);
  return shader.bytecode_hash != 0;
}

bool PopulateShaderRecordFromCreateArgument(RendererResourceRecord& record,
                                            uint8_t* base,
                                            uint32_t container_address) {
  if (!base || container_address == 0 ||
      !IsShaderResourceKind(GuestObjectKind(record))) {
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
  GuestShaderRecord shader;
  shader.object_address = GuestObjectAddress(record);
  shader.bytecode_address = container_address + bytecode_offset;
  shader.bytecode_size = bytecode_size;
  shader.bytecode_offset = bytecode_offset;
  shader.bytecode_hash =
      HashGuestBytesFnv1a64(base, container_address, container_size);
  ApplyGuestShaderRecord(record, shader);
  return shader.bytecode_hash != 0;
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
  if (existing && GuestObjectKind(*existing) != kind) {
    existing = nullptr;
  }

  RendererResourceRecord record =
      CreateGuestShaderResource(kind, guest_address);
  const bool created = existing == nullptr;
  if (existing) {
    record = *existing;
  }

  SetGuestResourceParent(record, parent_address);
  GuestShaderRecord shader;
  shader.object_address = GuestObjectAddress(record);
  shader.bytecode_address = guest_address;
  shader.bytecode_size = byte_size;
  shader.bytecode_offset = 0;
  shader.bytecode_hash = HashGuestBytesFnv1a64(base, guest_address, byte_size);
  ApplyGuestShaderRecord(record, shader);
  if (shader.bytecode_hash == 0) {
    return false;
  }

  record.serial = ++g_observer.serial;
  StoreGuestResourceLocked(std::move(record), created);
  return true;
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
constexpr uint32_t kFableDeviceDepthControlOffset = 10548;
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
constexpr uint32_t kSyntheticFablePrivateRectDepthPixelShader = 0x0E010136u;
constexpr uint32_t kSyntheticFablePrivateRectCopyPixelShader = 0x0E010137u;
constexpr uint32_t kFablePrivateRectCopyTextureSlot = 13u;
constexpr uint64_t kFableGuiQuadVertexShaderHash = 0x4724831317AA37A8ull;
constexpr uint64_t kFableBinkMoviePixelShaderHash = 0x69E9772C76BD6253ull;
constexpr uint64_t kFablePrivateRectDepthPixelShaderHash =
    0xE725F7105B73C0DBull;
constexpr uint64_t kFablePrivateRectCopyPixelShaderHash =
    0x9CEFC0421AC2A68Cull;
constexpr uint64_t kFableBinkMoviePixelShaderPayloadHash =
    0x5DBB593C197FF7EEull;

bool IsBinkMoviePixelShaderHash(uint64_t hash) {
  return hash == kFableBinkMoviePixelShaderHash ||
         hash == kFableBinkMoviePixelShaderPayloadHash;
}

bool CurrentDirectPixelShaderIsBinkMovieLocked() {
  const FableDirectStateSnapshot snapshot = GetFableDirectStateSnapshot();
  const RendererResourceRecord* record =
      FindResourceRecordLocked(snapshot.shaders.pixel_shader);
  if (!record || GuestObjectKind(*record) != RendererResourceKind::kPixelShader) {
    return false;
  }
  const GuestShaderRecord shader = GuestShaderRecordFor(*record);
  return IsBinkMoviePixelShaderHash(shader.bytecode_hash);
}

void SyncDrawEventRenderStatesFromDepthControl(
    RendererDrawEventSnapshot& event) {
  const uint32_t depth_control = event.rb_depthcontrol;
  event.render_states[kFableRenderStateSlotStencilEnable] =
      depth_control & 0x1u;
  event.render_states[kFableRenderStateSlotZEnable] =
      (depth_control >> 1u) & 0x1u;
  event.render_states[kFableRenderStateSlotZWriteEnable] =
      (depth_control >> 2u) & 0x1u;
  event.render_states[kFableRenderStateSlotZFunc] =
      (depth_control >> 4u) & 0x7u;
  event.render_states[kFableRenderStateSlotStencilFunc] =
      (depth_control >> 8u) & 0x7u;
  event.render_states[kFableRenderStateSlotStencilFail] =
      (depth_control >> 11u) & 0x7u;
  event.render_states[kFableRenderStateSlotStencilPass] =
      (depth_control >> 14u) & 0x7u;
  event.render_states[kFableRenderStateSlotStencilZFail] =
      (depth_control >> 17u) & 0x7u;
}

uint64_t QueueDrawEventLocked(
    uint32_t device_pointer = 0, bool draw_vertices_up = false,
    bool refresh_device_bindings = true,
    RendererDrawEventSource source = RendererDrawEventSource::kUnknown,
    const RendererLastDrawSnapshot& draw = RendererLastDrawSnapshot{},
    const RendererHookArgs* wrapper_args = nullptr,
    std::string_view wrapper_symbol = {});
RendererDrawEventSnapshot BuildDrawEventLocked(
    uint32_t device_pointer, bool draw_vertices_up,
    bool refresh_device_bindings, RendererDrawEventSource source,
    const RendererLastDrawSnapshot& draw);
RendererDrawEventSnapshot RecordDrawEventLocked(
    RendererDrawEventSnapshot event);
std::vector<ScratchBufferAttachment> ExpandIndexedScratchStreamsToIndexArenaLocked(
    RendererDrawEventSnapshot& event);
std::vector<ScratchBufferAttachment> EnsureNonIndexedScratchStreamsForDrawLocked(
    RendererDrawEventSnapshot& event);
void QueuePm4DrawEventLocked(uint8_t* base, uint32_t command_pointer,
                             uint32_t packet_offset, uint32_t packet_words,
                             uint32_t opcode, uint32_t source_lr);
void QueuePm4ResolveCopyEventLocked(uint32_t source_lr);
void QueueClearEventLocked(uint32_t flags, uint32_t color_pointer,
                           double depth, uint32_t stencil,
                           RendererScissorSnapshot rect,
                           uint32_t root_symbol_address,
                           uint32_t root_lr);
bool IsDrawVerticesUpInnerScratchSubmit(const RendererHookArgs& args);
void TraceDrawCall(std::string_view symbol,
                   const RendererLastDrawSnapshot& draw);
void TraceResourceCreate(const RendererResourceRecord& record);
RendererResourceRecord& StoreGuestResourceLocked(RendererResourceRecord record,
                                                 bool count_create,
                                                 bool trace_create);
void UpsertScratchBufferResourceLocked(RendererResourceKind kind,
                                       uint32_t address, uint32_t size,
                                       uint32_t stride_or_format,
                                       uint32_t parent_address,
                                       uint32_t upload_source_address = 0,
                                       bool attach_by_address = true);
void AttachScratchBufferPayloadToDrawCommandLocked(
    uint64_t draw_serial, RendererResourceKind kind, uint32_t address);
bool CaptureScratchDrawBuffersLocked(std::string_view symbol,
                                     const RendererHookArgs& args,
                                     uint32_t result, uint8_t* base);
void TrackShaderObjectLocked(RendererResourceKind kind, uint32_t shader_address,
                             uint32_t parent_address, uint8_t* base);
void TrackVertexDeclarationLocked(uint32_t declaration_address,
                                  uint32_t parent_address, uint8_t* base);
bool TrackBoundSurfaceLocked(RendererResourceKind kind, uint32_t surface_address,
                             uint32_t parent_address, uint8_t* base);
uint32_t TextureBaseFromFetchDwords(const std::array<uint32_t, 6>& dwords);
void RemoveDuplicateTextureBaseResourceLocked(
    uint32_t owner_texture_object, uint32_t texture_base,
    const std::array<uint32_t, 6>& dwords);
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

#include "runtime/plume/direct/fable_video_uploads.inl"

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
  FableDirectSetDisplayPresent(
      g_observer.display_present_texture_base,
      g_observer.display_present_width, g_observer.display_present_height,
      g_observer.display_present_format,
      g_observer.display_present_source,
      g_observer.display_present_register,
      g_observer.display_present_register_value,
      fetch_dwords ? &g_observer.display_present_fetch_dwords : nullptr,
      g_observer.display_present_surface_object);
}

uint32_t SurfaceCopyHookAddress(std::string_view symbol) {
  if (symbol == "sub_82D160D8") {
    return 0x82D160D8u;
  }
  if (symbol == "sub_82D21C68") {
    return 0x82D21C68u;
  }
  if (symbol == "sub_82D21D60") {
    return 0x82D21D60u;
  }
  if (symbol == "sub_82D21EC0") {
    return 0x82D21EC0u;
  }
  if (symbol == "sub_82D1EF90") {
    return 0x82D1EF90u;
  }
  if (symbol == "sub_82D14B20") {
    return 0x82D14B20u;
  }
  if (symbol == "sub_83013A70") {
    return 0x83013A70u;
  }
  if (symbol == "sub_82BA95E0") {
    return 0x82BA95E0u;
  }
  return 0;
}

uint32_t SurfaceViewHookAddress(std::string_view symbol) {
  if (symbol == "sub_82B98860") {
    return 0x82B98860u;
  }
  if (symbol == "sub_82B98978") {
    return 0x82B98978u;
  }
  if (symbol == "sub_82B98A58") {
    return 0x82B98A58u;
  }
  return 0;
}

std::string_view SurfaceViewHookDisplayName(uint32_t symbol_address) {
  switch (symbol_address) {
  case 0x82B98860u:
    return "D3DTexture_GetSurfaceLevel";
  case 0x82B98978u:
    return "D3DCubeTexture_GetCubeMapSurface";
  case 0x82B98A58u:
    return "D3DVolumeTexture_GetVolumeLevel";
  default:
    return "surface-view-create";
  }
}

uint64_t SurfaceViewTextureLinkPendingKey(uint32_t symbol_address,
                                          uint32_t parent_texture,
                                          uint32_t surface_view,
                                          uint32_t level,
                                          uint32_t face_or_slice) {
  uint64_t key = 1469598103934665603ull;
  const auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(symbol_address);
  mix(parent_texture);
  mix(surface_view);
  mix(level);
  mix(face_or_slice);
  return key != 0 ? key : 1ull;
}

bool SurfaceAliasSourceIsTrackedColorTargetLocked(uint32_t source_surface) {
  auto source_it = FindResourceIteratorLocked(source_surface);
  return source_it != FableDirectGuestResourceRegistry().resources.end() &&
         GuestObjectKind(source_it->second) ==
             RendererResourceKind::kRenderTargetSurface &&
         HasPlausibleSurfaceHeader(source_it->second);
}

bool SurfaceCopyAliasShouldRegisterTextureLink(std::string_view symbol) {
  return symbol == "sub_82D160D8" || symbol == "sub_82D21C68" ||
         symbol == "sub_82D21D60" || symbol == "sub_82D1EF90" ||
         symbol == "sub_83013A70";
}

RendererResourceRecord* EnsureTextureRecordForSurfaceCopyLinkLocked(
    uint32_t texture_object, uint8_t* base) {
  if (!IsLikelyFableObjectAddress(texture_object)) {
    return nullptr;
  }

  RendererResourceRecord* record = FindResourceRecordLocked(texture_object);
  if (record && GuestObjectKind(*record) == RendererResourceKind::kTexture) {
    RegisterTextureIdentityForRecordLocked(*record);
    return record;
  }
  if (record || !base) {
    return nullptr;
  }

  const std::array<uint32_t, 6> dwords =
      LoadTextureFetchDwords(base, texture_object + 28u);
  if (!HasTextureFetchDwords(dwords) ||
      !IsUsableTextureFetchSnapshot(GetTextureFetchSnapshotInfo(dwords))) {
    return nullptr;
  }

  RendererResourceRecord created =
      CreateGuestTextureResource(texture_object, texture_object);
  SnapshotGuestResource(created, base);
  GuestTextureRecord texture = GuestTextureRecordFor(created);
  texture.raw_fetch_dwords = dwords;
  texture.normalized_fetch_dwords = dwords;
  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  texture.base_address = info.base_address;
  texture.byte_size = std::max<uint32_t>(texture.byte_size, info.size);
  ApplyGuestTextureRecord(created, texture);
  if (ShouldCaptureTextureUploadFromFetchDwords(created, dwords, base)) {
    CaptureTextureUploadFromFetchDwords(created, dwords, base);
  }
  created.serial = ++g_observer.serial;
  RendererResourceRecord& stored =
      StoreGuestResourceLocked(std::move(created), true, true);
  RegisterTextureIdentityForRecordLocked(stored);
  MarkResourceChangedLocked(stored);
  UpdateDirectSnapshotCacheLocked(stored);
  return &stored;
}

uint64_t SurfaceCopyAliasContextKeyLocked(
    const RendererSurfaceCopyAliasSnapshot& alias,
    const FableDirectStateSnapshot& direct_snapshot) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(alias.copy_symbol_address);
  mix(alias.copy_call_serial);
  mix(alias.source_surface);
  mix(alias.destination_surface);
  mix(direct_snapshot.targets.color[0]);
  mix(direct_snapshot.targets.depth);
  mix(direct_snapshot.shaders.vertex_shader);
  mix(direct_snapshot.shaders.pixel_shader);
  mix(CurrentPlumeOutputFrameIndex());
  return key;
}

void StampSurfaceCopyAliasIdentityLocked(
    RendererSurfaceCopyAliasSnapshot& alias,
    const FableDirectStateSnapshot& direct_snapshot) {
  alias.copy_call_serial = alias.serial;
  alias.copy_symbol_address = alias.hook_address;
  alias.copy_context_key =
      SurfaceCopyAliasContextKeyLocked(alias, direct_snapshot);
}

void RegisterSurfaceCopyTextureLinkLocked(
    std::string_view symbol, const RendererSurfaceCopyAliasSnapshot& alias,
    const FableDirectStateSnapshot& direct_snapshot) {
  if (!SurfaceCopyAliasShouldRegisterTextureLink(symbol)) {
    return;
  }

  uint8_t* const base = g_observer.memory_base;
  if (!base || !IsLikelyFableObjectAddress(alias.source_surface) ||
      !IsLikelyFableObjectAddress(alias.destination_surface)) {
    return;
  }

  std::array<uint32_t, 4> destination_texture_candidates = {
      alias.destination_surface, 0u, 0u, 0u};
  if (RendererResourceRecord* destination_record =
          FindResourceRecordLocked(alias.destination_surface)) {
    destination_texture_candidates[1] =
        GuestObjectParentAddress(*destination_record);
  }
  if (IsLikelyGuestDwordAddress(alias.destination_surface + 24u)) {
    destination_texture_candidates[2] =
        LoadGuestU32(base, alias.destination_surface + 24u);
  }

  RendererResourceRecord* destination_texture_record = nullptr;
  const FableTextureIdentity* destination_identity = nullptr;
  for (uint32_t candidate : destination_texture_candidates) {
    if (candidate == 0) {
      continue;
    }
    RendererResourceRecord* record =
        EnsureTextureRecordForSurfaceCopyLinkLocked(candidate, base);
    if (!record) {
      continue;
    }
    const FableTextureIdentity* identity =
        FableDirectFindTextureIdentityByObject(GuestObjectAddress(*record));
    if (!identity ||
        !IsUsableTextureFetchSnapshot(
            GetTextureFetchSnapshotInfo(identity->fetch_dwords))) {
      continue;
    }
    destination_texture_record = record;
    destination_identity = identity;
    break;
  }
  if (!destination_texture_record || !destination_identity) {
    return;
  }

  const RendererResourceRecord* source_record =
      FindResourceRecordLocked(alias.source_surface);
  const RendererResourceKind source_kind =
      source_record ? GuestObjectKind(*source_record)
                    : RendererResourceKind::kUnknown;
  const bool depth_copy =
      source_kind == RendererResourceKind::kDepthSurface ||
      (source_record && IsSurfaceResourceKind(source_kind) &&
       FableSetSurfaceHeaderFormatIsDepth(
           GuestSurfaceRecordFor(*source_record).d3d_format));

  FableSurfaceTextureLink link;
  link.copy_call_serial = alias.copy_call_serial;
  link.copy_context_key = alias.copy_context_key;
  link.copy_symbol_address = alias.copy_symbol_address;
  link.source_slot = 0xFFFFFFFFu;
  link.trusted_destination_edge = true;
  link.texture = destination_identity->object_texture;
  link.texture_base_address = destination_identity->base_address;
  link.texture_width = destination_identity->width;
  link.texture_height = destination_identity->height;
  link.texture_format = destination_identity->format;
  link.texture_fetch_dwords = destination_identity->fetch_dwords;
  link.source_surface = alias.source_surface;
  link.backing_texture = destination_identity->object_texture;
  link.backing_base_address = destination_identity->base_address;
  link.backing_fetch_dwords = destination_identity->fetch_dwords;
  link.render_target = depth_copy ? 0u : alias.source_surface;
  link.depth_stencil =
      depth_copy ? alias.source_surface : direct_snapshot.targets.depth;
  link.vertex_shader = direct_snapshot.shaders.vertex_shader;
  link.pixel_shader = direct_snapshot.shaders.pixel_shader;
  link.frame_index = 0;
  link.draw_serial = 0;
  link.kind = depth_copy ? FablePrivateTextureCopyKind::kDepth
                         : FablePrivateTextureCopyKind::kColor;
  FableDirectRegisterSurfaceTextureLink(link);

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_surface_texture_copy_links < 32u) {
    REXLOG_INFO(
        "Refii renderer surface texture link hook={} source=0x{:08X} "
        "destination=0x{:08X} texture=0x{:08X} base=0x{:08X} "
        "size={} {}x{} fmt={} kind={} call={} context=0x{:016X} upload_gen={}",
        symbol, alias.source_surface, alias.destination_surface, link.texture,
        link.texture_base_address, destination_identity->byte_size,
        link.texture_width, link.texture_height, link.texture_format,
        FablePrivateTextureCopyKindName(link.kind),
        static_cast<unsigned long long>(link.copy_call_serial),
        static_cast<unsigned long long>(link.copy_context_key),
        static_cast<unsigned long long>(
            destination_texture_record->upload_generation));
    ++g_observer.logged_surface_texture_copy_links;
  }
}

uint64_t SurfaceViewTextureLinkContextKeyLocked(
    uint64_t call_serial, uint32_t symbol_address, uint32_t parent_texture,
    uint32_t surface_view, uint32_t level, uint32_t face_or_slice,
    const FableTextureIdentity& parent_identity) {
  uint64_t key = 1469598103934665603ull;
  const auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(call_serial);
  mix(symbol_address);
  mix(parent_texture);
  mix(surface_view);
  mix(level);
  mix(face_or_slice);
  mix(parent_identity.base_address);
  mix(parent_identity.width);
  mix(parent_identity.height);
  mix(parent_identity.format);
  mix(FableDirectTextureFetchKey(parent_identity.fetch_dwords));
  return key != 0 ? key : 1ull;
}

bool TryRegisterSurfaceViewTextureLinkLocked(
    const PendingSurfaceViewTextureLink& pending,
    const FableTextureIdentity& parent_identity) {
  if (pending.surface_view == 0 || pending.parent_texture == 0 ||
      pending.surface_view == pending.parent_texture ||
      parent_identity.object_texture == 0 ||
      !IsUsableTextureFetchSnapshot(
          GetTextureFetchSnapshotInfo(parent_identity.fetch_dwords)) ||
      (pending.parent_texture != parent_identity.object_texture &&
       pending.parent_texture != parent_identity.parent_texture)) {
    return false;
  }
  const uint64_t context_key = SurfaceViewTextureLinkContextKeyLocked(
      pending.call_serial, pending.symbol_address, pending.parent_texture,
      pending.surface_view, pending.level, pending.face_or_slice,
      parent_identity);
  if (!g_observer.registered_surface_view_texture_link_keys.insert(context_key)
           .second) {
    return true;
  }
  const bool depth_view =
      IsSurfaceResourceKind(pending.view_kind) &&
      FableSetSurfaceHeaderFormatIsDepth(pending.d3d_format) &&
      !FableSurfaceHeaderFormatIsAmbiguousColorDepth(pending.d3d_format);
  FableSurfaceTextureLink link;
  link.copy_call_serial = pending.call_serial;
  link.copy_context_key = context_key;
  link.copy_symbol_address = pending.symbol_address;
  link.source_slot = 0xFFFFFFFFu;
  link.trusted_destination_edge = true;
  link.texture = parent_identity.object_texture;
  link.texture_base_address = parent_identity.base_address;
  link.texture_width = parent_identity.width;
  link.texture_height = parent_identity.height;
  link.texture_format = parent_identity.format;
  link.texture_fetch_dwords = parent_identity.fetch_dwords;
  link.source_surface = pending.surface_view;
  link.backing_texture = parent_identity.object_texture;
  link.backing_base_address = parent_identity.base_address;
  link.backing_fetch_dwords = parent_identity.fetch_dwords;
  link.render_target = depth_view ? 0u : pending.surface_view;
  link.depth_stencil = depth_view ? pending.surface_view : 0u;
  link.frame_index = 0;
  link.draw_serial = 0;
  link.kind = depth_view ? FablePrivateTextureCopyKind::kDepth
                         : FablePrivateTextureCopyKind::kColor;
  FableDirectRegisterSurfaceTextureLink(link);

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_surface_view_texture_links < 32u) {
    REXLOG_INFO(
        "Refii renderer exact surface-view texture link #{} hook={} "
        "surface=0x{:08X} texture=0x{:08X} parent=0x{:08X} level={} "
        "face_or_slice={} base=0x{:08X} size={} {}x{} fmt={} kind={} "
        "call={} context=0x{:016X}",
        ++g_observer.logged_surface_view_texture_links,
        SurfaceViewHookDisplayName(pending.symbol_address), pending.surface_view,
        link.texture, pending.parent_texture, pending.level,
        pending.face_or_slice, link.texture_base_address,
        parent_identity.byte_size, link.texture_width, link.texture_height,
        link.texture_format, FablePrivateTextureCopyKindName(link.kind),
        static_cast<unsigned long long>(pending.call_serial),
        static_cast<unsigned long long>(context_key));
  }
  return true;
}

void DrainPendingSurfaceViewTextureLinksForIdentityLocked(
    const FableTextureIdentity& identity) {
  std::array<uint32_t, 2> parents = {identity.object_texture,
                                    identity.parent_texture};
  for (uint32_t parent : parents) {
    if (parent == 0) {
      continue;
    }
    auto pending_it =
        g_observer.pending_surface_view_texture_links_by_parent.find(parent);
    if (pending_it ==
        g_observer.pending_surface_view_texture_links_by_parent.end()) {
      continue;
    }
    auto& pending_links = pending_it->second;
    for (auto it = pending_links.begin(); it != pending_links.end();) {
      if (TryRegisterSurfaceViewTextureLinkLocked(*it, identity)) {
        it = pending_links.erase(it);
      } else {
        ++it;
      }
    }
    if (pending_links.empty()) {
      g_observer.pending_surface_view_texture_links_by_parent.erase(pending_it);
    }
  }
}

void RegisterSurfaceViewTextureLinkLocked(
    std::string_view symbol, const RendererHookArgs& args,
    const RendererResourceRecord& surface_view_record) {
  if (GuestObjectKind(surface_view_record) != RendererResourceKind::kSurfaceView) {
    return;
  }

  uint8_t* const base = g_observer.memory_base;
  const uint32_t surface_view = GuestObjectAddress(surface_view_record);
  const uint32_t parent_texture = GuestObjectParentAddress(surface_view_record);
  const uint32_t symbol_address = SurfaceViewHookAddress(symbol);
  if (!base || symbol_address == 0 ||
      !IsLikelyFableObjectAddress(surface_view) ||
      !IsLikelyFableObjectAddress(parent_texture) ||
      surface_view == parent_texture) {
    return;
  }

  const GuestSurfaceRecord surface = GuestSurfaceRecordFor(surface_view_record);
  PendingSurfaceViewTextureLink pending;
  pending.call_serial = ++g_observer.serial;
  pending.symbol_address = symbol_address;
  pending.surface_view = surface_view;
  pending.parent_texture = parent_texture;
  pending.level = symbol == "sub_82B98978" ? args.r5 : args.r4;
  pending.face_or_slice = symbol == "sub_82B98978" ? args.r4 : 0u;
  pending.view_kind = GuestObjectKind(surface_view_record);
  pending.d3d_format = HasPlausibleSurfaceHeader(surface_view_record)
                           ? surface.d3d_format
                           : 0u;
  pending.key = SurfaceViewTextureLinkPendingKey(
      pending.symbol_address, pending.parent_texture, pending.surface_view,
      pending.level, pending.face_or_slice);

  RendererResourceRecord* parent_record =
      EnsureTextureRecordForSurfaceCopyLinkLocked(parent_texture, base);
  const FableTextureIdentity* parent_identity =
      parent_record && GuestObjectKind(*parent_record) == RendererResourceKind::kTexture
          ? FableDirectFindTextureIdentityByObject(GuestObjectAddress(*parent_record))
          : FableDirectFindTextureIdentityByObject(parent_texture);
  if (parent_identity &&
      TryRegisterSurfaceViewTextureLinkLocked(pending, *parent_identity)) {
    return;
  }

  auto& pending_links =
      g_observer.pending_surface_view_texture_links_by_parent[parent_texture];
  const auto duplicate = std::find_if(
      pending_links.begin(), pending_links.end(),
      [&](const PendingSurfaceViewTextureLink& existing) {
        return existing.key == pending.key;
      });
  if (duplicate == pending_links.end()) {
    if (pending_links.size() >= kMaxPendingSurfaceViewTextureLinks) {
      pending_links.erase(pending_links.begin());
    }
    pending_links.push_back(pending);
    if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
        g_observer.logged_pending_surface_view_texture_links < 16u) {
      REXLOG_INFO(
          "Refii renderer queued exact surface-view texture link #{} hook={} "
          "surface=0x{:08X} parent=0x{:08X} level={} face_or_slice={} "
          "call={}",
          ++g_observer.logged_pending_surface_view_texture_links,
          SurfaceViewHookDisplayName(pending.symbol_address),
          pending.surface_view, pending.parent_texture, pending.level,
          pending.face_or_slice,
          static_cast<unsigned long long>(pending.call_serial));
    }
  }
}

void RecordSurfaceCopyAliasLocked(std::string_view symbol,
                                  uint32_t source_surface,
                                  uint32_t destination_surface) {
  if (!IsLikelyFableObjectAddress(source_surface) ||
      !IsLikelyFableObjectAddress(destination_surface) ||
      source_surface == destination_surface) {
    return;
  }

  const bool incoming_is_color_target =
      SurfaceAliasSourceIsTrackedColorTargetLocked(source_surface);
  auto existing_it = g_observer.surface_copy_aliases.find(destination_surface);
  if (existing_it != g_observer.surface_copy_aliases.end()) {
    const bool existing_is_color_target =
        SurfaceAliasSourceIsTrackedColorTargetLocked(
            existing_it->second.source_surface);
    if (existing_is_color_target && !incoming_is_color_target) {
      return;
    }
  }

  RendererSurfaceCopyAliasSnapshot alias;
  alias.source_surface = source_surface;
  alias.destination_surface = destination_surface;
  alias.hook_address = SurfaceCopyHookAddress(symbol);
  alias.serial = ++g_observer.serial;
  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  StampSurfaceCopyAliasIdentityLocked(alias, direct_snapshot);
  g_observer.surface_copy_aliases[destination_surface] = alias;
  FableDirectTrackSurfaceCopyAlias(alias);
  RegisterSurfaceCopyTextureLinkLocked(symbol, alias, direct_snapshot);

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_surface_copy_aliases < 32u) {
    REXLOG_INFO(
        "Refii renderer surface alias hook={} source=0x{:08X} "
        "destination=0x{:08X} serial={} call={} context=0x{:016X}",
        symbol, source_surface, destination_surface,
        static_cast<unsigned long long>(alias.serial),
        static_cast<unsigned long long>(alias.copy_call_serial),
        static_cast<unsigned long long>(alias.copy_context_key));
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
    return;
  }

  if (symbol == "sub_82D1EF90" || symbol == "sub_83013A70") {
    // CBlt::BltCopy stores its source and destination surfaces in the first
    // two object words, then pulls pixels via source vtable[1] into dest
    // vtable[2]. Keep this as alias-only; native execution still performs the
    // actual CPU copy for odd blit object shapes.
    if (IsLikelyFableObjectAddress(args.r3)) {
      const uint32_t source_surface = LoadGuestU32(g_observer.memory_base, args.r3);
      const uint32_t destination_surface =
          LoadGuestU32(g_observer.memory_base, args.r3 + 4u);
      RecordSurfaceCopyAliasLocked(symbol, source_surface, destination_surface);
    }
  }
}

void ObserveD3DXCreateTextureFromTextureExitLocked(std::string_view symbol,
                                                  const RendererHookArgs& args,
                                                  uint32_t result,
                                                  uint8_t* base) {
  if (result != 0 || !base || !IsLikelyFableObjectAddress(args.r3) ||
      !IsLikelyGuestDwordAddress(args.r5)) {
    return;
  }

  const uint32_t source_texture = args.r3;
  const uint32_t destination_texture = LoadGuestU32(base, args.r5);
  if (!IsLikelyFableObjectAddress(destination_texture) ||
      destination_texture == source_texture) {
    return;
  }

  auto it = FindResourceIteratorLocked(destination_texture);
  const bool created =
      it == FableDirectGuestResourceRegistry().resources.end();
  RendererResourceRecord record =
      created ? CreateGuestTextureResource(destination_texture, source_texture)
              : it->second;
  if (GuestObjectKind(record) != RendererResourceKind::kTexture) {
    return;
  }

  const RendererResourceRecord before = record;
  SetGuestResourceParent(record, source_texture);
  SnapshotGuestResource(record, base);
  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords) ||
      !IsUsableTextureFetchSnapshot(GetTextureFetchSnapshotInfo(dwords))) {
    return;
  }

  if (ShouldCaptureTextureUploadFromFetchDwords(record, dwords, base)) {
    CaptureTextureUploadFromFetchDwords(record, dwords, base);
  }

  record.serial = ++g_observer.serial;
  RendererResourceRecord& stored =
      StoreGuestResourceLocked(std::move(record), created, created);
  RegisterTextureIdentityForRecordLocked(stored);
  FableDirectGuestResourceRegistry().last_resource = stored;
  MarkResourceChangedLocked(stored);
  RefreshQueuedDrawTextureResourceSerialLocked(stored);
  UpdateDirectSnapshotCacheLocked(stored);

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_texture_clone_aliases < 32u) {
    const TextureFetchSnapshotInfo info =
        GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(stored));
    REXLOG_INFO(
        "Refii renderer texture clone hook={} source=0x{:08X} "
        "destination=0x{:08X} out=0x{:08X} base=0x{:08X} size={} "
        "{}x{} fmt={} upload_gen={} changed={}",
        symbol, source_texture, destination_texture, args.r5,
        info.base_address, info.size, info.width, info.height, info.format,
        static_cast<unsigned long long>(stored.upload_generation),
        before.serial != stored.serial ? 1 : 0);
    ++g_observer.logged_texture_clone_aliases;
  }
}

bool ReadD3DXLockSurfaceCopyStateLocked(uint32_t lock_object, uint8_t* base,
                                        RendererSurfaceCopyAliasSnapshot& copy) {
  if (!base || !IsLikelyFableObjectAddress(lock_object)) {
    return false;
  }

  const uint32_t flags = LoadGuestU32(base, lock_object);
  const uint32_t destination_surface = LoadGuestU32(base, lock_object + 4u);
  const uint32_t staging_surface = LoadGuestU32(base, lock_object + 8u);
  const uint32_t scratch_texture = LoadGuestU32(base, lock_object + 12u);
  const uint32_t lock_token = LoadGuestU32(base, lock_object + 16u);
  if (!IsLikelyFableObjectAddress(destination_surface)) {
    return false;
  }

  const bool commits_staging =
      staging_surface != 0 && IsLikelyFableObjectAddress(staging_surface) &&
      lock_token != 0 && (flags & 1u) == 0;
  copy = {};
  copy.source_surface = commits_staging ? staging_surface : destination_surface;
  copy.destination_surface = destination_surface;
  copy.hook_address = 0x82D21D60u;
  copy.serial = ++g_observer.serial;
  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  StampSurfaceCopyAliasIdentityLocked(copy, direct_snapshot);

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_surface_copy_aliases < 64u) {
    REXLOG_INFO(
        "Refii renderer D3DX lock surface state #{} object=0x{:08X} "
        "flags=0x{:08X} dest=0x{:08X} staging=0x{:08X} "
        "scratch_texture=0x{:08X} lock_token=0x{:08X} commits_staging={}",
        g_observer.logged_surface_copy_aliases + 1u, lock_object, flags,
        destination_surface, staging_surface, scratch_texture, lock_token,
        commits_staging ? 1 : 0);
    ++g_observer.logged_surface_copy_aliases;
  }

  return true;
}

void ObserveD3DXLockSurfaceUnlockEnterLocked(std::string_view symbol,
                                             const RendererHookArgs& args,
                                             uint8_t* base) {
  RendererSurfaceCopyAliasSnapshot copy;
  if (!ReadD3DXLockSurfaceCopyStateLocked(args.r3, base, copy)) {
    g_observer.pending_d3dx_lock_surface_copies.erase(args.r3);
    return;
  }

  g_observer.pending_d3dx_lock_surface_copies[args.r3] = copy;
  if (copy.source_surface != copy.destination_surface) {
    RecordSurfaceCopyAliasLocked(symbol, copy.source_surface,
                                 copy.destination_surface);
  }
}

void ObserveD3DXLockSurfaceUnlockExitLocked(std::string_view symbol,
                                            const RendererHookArgs& args,
                                            uint32_t result,
                                            uint8_t* base) {
  auto it = g_observer.pending_d3dx_lock_surface_copies.find(args.r3);
  if (it == g_observer.pending_d3dx_lock_surface_copies.end()) {
    return;
  }

  const RendererSurfaceCopyAliasSnapshot copy = it->second;
  g_observer.pending_d3dx_lock_surface_copies.erase(it);
  if (result != 0) {
    return;
  }

  if (copy.source_surface != copy.destination_surface) {
    RecordSurfaceCopyAliasLocked(symbol, copy.source_surface,
                                 copy.destination_surface);
  }
  RefreshTextureUploadsAfterCpuSurfaceCopyLocked(symbol, base);
}

void ObserveD3DXLockSurfaceLockExitLocked(std::string_view symbol,
                                          const RendererHookArgs& args,
                                          uint32_t result,
                                          uint8_t* base) {
  if (result != 0) {
    return;
  }

  RendererSurfaceCopyAliasSnapshot copy;
  if (!ReadD3DXLockSurfaceCopyStateLocked(args.r3, base, copy)) {
    return;
  }

  if (copy.source_surface != copy.destination_surface) {
    g_observer.pending_d3dx_lock_surface_copies[args.r3] = copy;
  }
}

void RecordRootSurfaceTextureSiblingLocked(uint32_t surface_address,
                                           uint32_t texture_header) {
  if (!IsLikelyFableObjectAddress(surface_address) ||
      !IsLikelyFableObjectAddress(texture_header) ||
      surface_address == texture_header) {
    return;
  }

  auto surface_it = FindResourceIteratorLocked(surface_address);
  if (surface_it == FableDirectGuestResourceRegistry().resources.end() ||
      !IsSurfaceResourceKind(GuestObjectKind(surface_it->second))) {
    return;
  }

  RendererResourceRecord surface = surface_it->second;
  if (GuestObjectParentAddress(surface) == texture_header) {
    return;
  }

  SetGuestResourceParent(surface, texture_header);
  surface.serial = ++g_observer.serial;
  StoreGuestResourceLocked(std::move(surface), false, false);
  MarkResourceChangedLocked(texture_header, g_observer.serial);

  if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
      g_observer.logged_root_surface_texture_siblings < 32u) {
    REXLOG_INFO(
        "Refii renderer root surface/texture sibling #{} surface=0x{:08X} "
        "texture=0x{:08X} serial={}",
        ++g_observer.logged_root_surface_texture_siblings, surface_address,
        texture_header, static_cast<unsigned long long>(g_observer.serial));
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
  const GuestVertexDeclarationRecord declaration =
      GuestVertexDeclarationRecordFor(record);
  if (!base || declaration_address == 0 ||
      GuestObjectKind(record) != RendererResourceKind::kVertexDeclaration ||
      declaration.element_count <= 32u) {
    return;
  }
  if (!g_observer.logged_vertex_declaration_failures.insert(declaration_address)
           .second) {
    return;
  }

  const uint32_t element_address = declaration.elements_address != 0
                                       ? declaration.elements_address
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
      declaration_address, declaration.element_count, declaration.elements_size,
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

void ApplyTextureObjectFetchConstantsToEventLocked(
    RendererDrawEventSnapshot& event);

void ApplyVertexFetchConstantBindingLocked(uint32_t fetch_index) {
  if (fetch_index * 2u + 1u >= kFetchConstantDwordCount) {
    return;
  }

  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  const auto& fetch_constants = direct_snapshot.constants.fetch;
  rex::graphics::xenos::xe_gpu_vertex_fetch_t fetch = {};
  fetch.dword_0 = fetch_constants[fetch_index * 2u + 0u];
  fetch.dword_1 = fetch_constants[fetch_index * 2u + 1u];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kVertex) {
    return;
  }

  const uint32_t buffer_address = fetch.address << 2u;
  const uint32_t buffer_size = fetch.size << 2u;
  if (!IsLikelyGpuBufferRange(buffer_address, buffer_size)) {
    return;
  }

  RendererStreamBindingSnapshot stream = {
      .buffer = buffer_address,
      .offset = 0,
      .stride = 0,
  };
  FableDirectSetStream(0, fetch_index, stream);
  UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                    buffer_address, buffer_size, 0,
                                    kFetchConstantRegisterFirst +
                                        fetch_index * 2u);
}

void ApplyVertexFetchConstantBindingsForFetchSlotLocked(uint32_t fetch_slot) {
  const uint32_t first_fetch_index = fetch_slot * 3u;
  for (uint32_t i = 0; i < 3u; ++i) {
    ApplyVertexFetchConstantBindingLocked(first_fetch_index + i);
  }
}

bool BuildStreamFromVertexFetchDwordsLocked(
    const RendererDrawEventSnapshot& event, uint32_t fetch_index,
    RendererStreamBindingSnapshot& stream) {
  if (fetch_index * 2u + 1u >= event.fetch_constants.size()) {
    return false;
  }

  rex::graphics::xenos::xe_gpu_vertex_fetch_t fetch = {};
  fetch.dword_0 = event.fetch_constants[fetch_index * 2u + 0u];
  fetch.dword_1 = event.fetch_constants[fetch_index * 2u + 1u];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kVertex) {
    return false;
  }

  const uint32_t buffer_address = fetch.address << 2u;
  const uint32_t buffer_size = fetch.size << 2u;
  if (!IsLikelyGpuBufferRange(buffer_address, buffer_size)) {
    return false;
  }

  stream = {
      .buffer = buffer_address,
      .offset = 0,
      .stride = 0,
  };
  UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                    buffer_address, buffer_size, 0,
                                    kFetchConstantRegisterFirst +
                                        fetch_index * 2u);
  return true;
}

void BindDrawEventStreamsFromVertexFetchConstantsLocked(
    RendererDrawEventSnapshot& event) {
  if (event.source != RendererDrawEventSource::kSceneObject &&
      event.source != RendererDrawEventSource::kScratchWrapper) {
    return;
  }

  RendererStreamBindingSnapshot first_valid_stream = {};
  for (uint32_t fetch_index = 0;
       fetch_index < std::size(event.streams) &&
       fetch_index * 2u + 1u < event.fetch_constants.size();
       ++fetch_index) {
    RendererStreamBindingSnapshot stream = {};
    if (!BuildStreamFromVertexFetchDwordsLocked(event, fetch_index, stream)) {
      continue;
    }
    if (!IsLikelyBindableBufferAddress(first_valid_stream.buffer)) {
      first_valid_stream = stream;
    }
    if (!IsLikelyBindableBufferAddress(event.streams[fetch_index].buffer)) {
      event.streams[fetch_index] = stream;
    }
  }

  if (!IsLikelyBindableBufferAddress(event.streams[0].buffer) &&
      IsLikelyBindableBufferAddress(first_valid_stream.buffer)) {
    event.streams[0] = first_valid_stream;
  }
}

void RefreshQueuedDrawTextureStateLocked() {
  FableDirectPatchPendingDrawEvents([&](RendererDrawEventSnapshot& draw_event) {
    ApplyTextureObjectFetchConstantsToEventLocked(draw_event);
    draw_event.resource_serial =
        std::max(draw_event.resource_serial, g_observer.serial);
    return true;
  });
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

  FableDirectSetFetchConstant(fetch_dword, value);
  ++g_observer.fetch_constant_uploads;
  RefreshQueuedDrawTextureStateLocked();
  ApplyVertexFetchConstantBindingLocked(fetch_dword / 2u);
}

uint32_t TextureBaseFromFetchDwords(const std::array<uint32_t, 6>& dwords) {
  if (!HasTextureFetchDwords(dwords)) {
    return 0;
  }

  rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = dwords[0];
  fetch.dword_1 = dwords[1];
  fetch.dword_2 = dwords[2];
  fetch.dword_3 = dwords[3];
  fetch.dword_4 = dwords[4];
  fetch.dword_5 = dwords[5];
  if (fetch.type != rex::graphics::xenos::FetchConstantType::kTexture) {
    return 0;
  }
  return fetch.base_address << 12;
}

bool TextureRecordOwnsFetchBase(const RendererResourceRecord& record,
                                uint32_t texture_base,
                                const std::array<uint32_t, 6>& dwords) {
  if (GuestObjectKind(record) != RendererResourceKind::kTexture ||
      GuestObjectAddress(record) == texture_base) {
    return false;
  }

  const TextureFetchSnapshotInfo target_info =
      GetTextureFetchSnapshotInfo(dwords);
  const TextureFetchSnapshotInfo record_info =
      GetTextureFetchSnapshotInfo(TextureFetchDwordsForRecord(record));
  return IsUsableTextureFetchSnapshot(target_info) &&
         IsUsableTextureFetchSnapshot(record_info) &&
         record_info.base_address == texture_base &&
         record_info.base_address == target_info.base_address &&
         record_info.size == target_info.size &&
         record_info.width == target_info.width &&
         record_info.height == target_info.height &&
         record_info.format == target_info.format;
}

std::unordered_map<uint32_t, RendererResourceRecord>::iterator
FindTextureObjectRecordForFetchBaseLocked(
    uint32_t texture_base, const std::array<uint32_t, 6>& dwords) {
  for (auto it = FableDirectGuestResourceRegistry().resources.begin();
       it != FableDirectGuestResourceRegistry().resources.end(); ++it) {
    if (TextureRecordOwnsFetchBase(it->second, texture_base, dwords)) {
      return it;
    }
  }
  return FableDirectGuestResourceRegistry().resources.end();
}

void RemoveDuplicateTextureBaseResourceLocked(
    uint32_t owner_texture_object, uint32_t texture_base,
    const std::array<uint32_t, 6>& dwords) {
  auto base_it = FableDirectGuestResourceRegistry().resources.find(texture_base);
  const TextureFetchSnapshotInfo target_info =
      GetTextureFetchSnapshotInfo(dwords);
  const TextureFetchSnapshotInfo base_info =
      base_it != FableDirectGuestResourceRegistry().resources.end()
          ? GetTextureFetchSnapshotInfo(
                TextureFetchDwordsForRecord(base_it->second))
          : TextureFetchSnapshotInfo{};
  if (base_it == FableDirectGuestResourceRegistry().resources.end() ||
      base_it->first == owner_texture_object ||
      GuestObjectKind(base_it->second) != RendererResourceKind::kTexture ||
      !IsUsableTextureFetchSnapshot(target_info) ||
      !IsUsableTextureFetchSnapshot(base_info) ||
      base_info.base_address != target_info.base_address ||
      base_info.size != target_info.size || base_info.width != target_info.width ||
      base_info.height != target_info.height ||
      base_info.format != target_info.format) {
    return;
  }

  FableDirectGuestResourceRegistry().resources.erase(base_it);
  ++g_observer.serial;
  ++FableDirectGuestResourceRegistry().destroys;
  MarkResourceDestroyedLocked(texture_base, g_observer.serial);
  ++FableDirectGuestResourceRegistry().destroys;
  ++g_observer.serial;
}

bool TextureResourceObserverStateChanged(
    const RendererResourceRecord& before,
    const RendererResourceRecord& after) {
  if (GuestObjectKind(before) != GuestObjectKind(after) ||
      GuestObjectAddress(before) != GuestObjectAddress(after) ||
      GuestObjectParentAddress(before) != GuestObjectParentAddress(after) ||
      before.upload_generation != after.upload_generation ||
      before.raw.hash != after.raw.hash ||
      before.upload_data.size() != after.upload_data.size()) {
    return true;
  }

  const GuestTextureRecord before_texture = GuestTextureRecordFor(before);
  const GuestTextureRecord after_texture = GuestTextureRecordFor(after);
  return before_texture.object_address != after_texture.object_address ||
         before_texture.parent_address != after_texture.parent_address ||
         before_texture.base_address != after_texture.base_address ||
         before_texture.byte_size != after_texture.byte_size ||
         before_texture.raw_fetch_dwords != after_texture.raw_fetch_dwords ||
         before_texture.normalized_fetch_dwords !=
             after_texture.normalized_fetch_dwords ||
         before_texture.upload_generation != after_texture.upload_generation;
}

void RegisterTextureIdentityForRecordLocked(const RendererResourceRecord& record) {
  const uint32_t object_address = GuestObjectAddress(record);
  if (object_address == 0 ||
      GuestObjectKind(record) != RendererResourceKind::kTexture) {
    return;
  }

  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  if (!HasTextureFetchDwords(dwords)) {
    QueuePendingTextureIdentityForget(object_address);
    return;
  }

  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  if (!IsUsableTextureFetchSnapshot(info)) {
    QueuePendingTextureIdentityForget(object_address);
    return;
  }

  const GuestTextureRecord texture = GuestTextureRecordFor(record);
  FableTextureIdentity identity;
  identity.object_texture = object_address;
  identity.parent_texture = GuestObjectParentAddress(record);
  identity.base_address = info.base_address != 0 ? info.base_address
                                                 : texture.base_address;
  identity.byte_size = std::max<uint32_t>(info.size, texture.byte_size);
  identity.width = info.width;
  identity.height = info.height;
  identity.format = info.format;
  identity.upload_generation =
      std::max<uint64_t>(record.upload_generation, texture.upload_generation);
  identity.meaningful_upload =
      identity.upload_generation != 0 && !record.upload_data.empty() &&
      HasNonZeroUploadData(record);
  identity.fetch_dwords = dwords;
  QueuePendingTextureIdentityRegister(identity);
  DrainPendingSurfaceViewTextureLinksForIdentityLocked(identity);

  if (IsFableTitleBillboardTileUploadShape(info)) {
    const auto queue_alias_identity = [&](uint32_t alias_object) {
      if (alias_object == 0 || alias_object == identity.object_texture) {
        return;
      }
      FableTextureIdentity alias = identity;
      alias.object_texture = alias_object;
      alias.parent_texture = identity.object_texture;
      QueuePendingTextureIdentityRegister(alias);
      DrainPendingSurfaceViewTextureLinksForIdentityLocked(alias);
    };
    queue_alias_identity(
        FableTitleBillboardTileHeaderAddressForObject(identity.object_texture));
    queue_alias_identity(
        FableTitleBillboardTileObjectAddressForHeader(identity.object_texture));
  }
}

void RegisterShaderAndDeclarationIdentityForRecordLocked(
    const RendererResourceRecord& record) {
  const RendererResourceKind kind = GuestObjectKind(record);
  const uint32_t object_address = GuestObjectAddress(record);
  if (object_address == 0) {
    return;
  }

  std::vector<PendingShaderDeclarationIdentityOperation> operations;
  const auto queue_forget_shader = [&]() {
    PendingShaderDeclarationIdentityOperation operation;
    operation.kind = PendingShaderDeclarationIdentityOperationKind::kForgetShader;
    operation.object_address = object_address;
    operations.push_back(operation);
  };
  const auto queue_forget_declaration = [&]() {
    PendingShaderDeclarationIdentityOperation operation;
    operation.kind =
        PendingShaderDeclarationIdentityOperationKind::kForgetVertexDeclaration;
    operation.object_address = object_address;
    operations.push_back(operation);
  };

  if (IsShaderResourceKind(kind)) {
    queue_forget_declaration();
    const GuestShaderRecord shader = GuestShaderRecordFor(record);
    if (shader.bytecode_hash == 0) {
      queue_forget_shader();
    } else {
      PendingShaderDeclarationIdentityOperation operation;
      operation.kind =
          PendingShaderDeclarationIdentityOperationKind::kRegisterShader;
      operation.object_address = object_address;
      operation.shader.kind = PublicResourceKind(kind);
      operation.shader.object_shader = object_address;
      operation.shader.parent_shader = GuestObjectParentAddress(record);
      operation.shader.bytecode_address = shader.bytecode_address;
      operation.shader.bytecode_size = shader.bytecode_size;
      operation.shader.bytecode_offset = shader.bytecode_offset;
      operation.shader.bytecode_hash = shader.bytecode_hash;
      operations.push_back(operation);
    }
  } else if (kind == RendererResourceKind::kVertexDeclaration) {
    queue_forget_shader();
    const GuestVertexDeclarationRecord declaration =
        GuestVertexDeclarationRecordFor(record);
    if (declaration.elements_hash == 0 ||
        declaration.elements_size == 0 ||
        declaration.element_count == 0) {
      queue_forget_declaration();
    } else {
      PendingShaderDeclarationIdentityOperation operation;
      operation.kind = PendingShaderDeclarationIdentityOperationKind::
          kRegisterVertexDeclaration;
      operation.object_address = object_address;
      operation.declaration.object_declaration = object_address;
      operation.declaration.parent_declaration =
          GuestObjectParentAddress(record);
      operation.declaration.elements_address = declaration.elements_address;
      operation.declaration.elements_size = declaration.elements_size;
      operation.declaration.element_count = declaration.element_count;
      operation.declaration.max_stream = declaration.max_stream;
      operation.declaration.stream_mask = declaration.stream_mask;
      operation.declaration.declaration_flags = declaration.declaration_flags;
      operation.declaration.elements_hash = declaration.elements_hash;
      operations.push_back(operation);
    }
  } else {
    queue_forget_shader();
    queue_forget_declaration();
  }

  QueuePendingShaderDeclarationIdentityOperations(std::move(operations));
}

bool EnsureVertexDeclarationSnapshotPayloadLocked(RendererResourceRecord& record) {
  if (GuestObjectKind(record) != RendererResourceKind::kVertexDeclaration) {
    return false;
  }

  const bool had_payload = !record.upload_data.empty();
  const uint64_t before_generation = record.upload_generation;
  EnsureVertexDeclarationUploadPayload(record, g_observer.memory_base);
  if (record.upload_data.empty()) {
    const GuestVertexDeclarationRecord declaration =
        GuestVertexDeclarationRecordFor(record);
    if (declaration.elements_hash != 0 && declaration.elements_size != 0) {
      for (const auto& [candidate_address, candidate] :
           FableDirectGuestResourceRegistry().resources) {
        (void)candidate_address;
        if (&candidate == &record ||
            GuestObjectKind(candidate) !=
                RendererResourceKind::kVertexDeclaration ||
            candidate.upload_data.empty()) {
          continue;
        }

        const GuestVertexDeclarationRecord candidate_declaration =
            GuestVertexDeclarationRecordFor(candidate);
        if (candidate_declaration.elements_hash != declaration.elements_hash ||
            candidate_declaration.elements_size != declaration.elements_size) {
          continue;
        }

        record.upload_data = candidate.upload_data;
        record.upload_generation =
            std::max(record.upload_generation, candidate.upload_generation);
        break;
      }
    }
  }

  if (record.upload_data.empty()) {
    return false;
  }

  if (!had_payload && record.upload_generation == before_generation) {
    ++record.upload_generation;
  }
  RegisterShaderAndDeclarationIdentityForRecordLocked(record);
  UpdateDirectSnapshotCacheLocked(record);
  return true;
}

void MarkTextureResourceChangedIfDirtyLocked(
    const RendererResourceRecord& before, RendererResourceRecord& record) {
  if (!TextureResourceObserverStateChanged(before, record)) {
    return;
  }

  if (record.serial <= before.serial) {
    record.serial = ++g_observer.serial;
  }
  TrackZeroTextureRefreshCandidateLocked(record);
  RegisterTextureIdentityForRecordLocked(record);
  FableDirectGuestResourceRegistry().last_resource = record;
  MarkResourceChangedLocked(record);
  RefreshQueuedDrawTextureResourceSerialLocked(record);
  UpdateDirectSnapshotCacheLocked(record);
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

  auto object_it =
      FindTextureObjectRecordForFetchBaseLocked(texture_base, dwords);
  if (object_it != FableDirectGuestResourceRegistry().resources.end()) {
    RendererResourceRecord& record = object_it->second;
    const RendererResourceRecord before = record;
    if (ShouldCaptureTextureUploadFromFetchDwords(record, dwords, base)) {
      CaptureTextureUploadFromFetchDwords(record, dwords, base);
    }
    RefreshTextureUploadFromPendingPixDecodedFrameLocked(record, dwords, base);
    MarkTextureResourceChangedIfDirtyLocked(before, record);
    return;
  }

  auto it = FableDirectGuestResourceRegistry().resources.find(texture_base);
  const bool created = it == FableDirectGuestResourceRegistry().resources.end();
  RendererResourceRecord* record = nullptr;
  if (created) {
    RendererResourceRecord created_record =
        CreateGuestTextureResource(texture_base, texture_base);
    record = &StoreGuestResourceLocked(std::move(created_record), true);
  } else if (!IsImageResourceKind(GuestObjectKind(it->second))) {
    return;
  } else {
    record = &it->second;
  }

  const RendererResourceRecord before = *record;
  GuestTextureRecord texture = GuestTextureRecordFor(*record);
  texture.normalized_fetch_dwords = dwords;
  if (GuestObjectKind(*record) == RendererResourceKind::kTexture) {
    texture.raw_fetch_dwords = dwords;
  }
  ApplyGuestTextureRecord(*record, texture);
  if (GuestObjectKind(*record) == RendererResourceKind::kTexture &&
      ShouldCaptureTextureUploadFromFetchDwords(*record, dwords, base)) {
    CaptureTextureUploadFromFetchDwords(*record, dwords, base);
  }
  RefreshTextureUploadFromPendingPixDecodedFrameLocked(*record, dwords, base);
  MarkTextureResourceChangedIfDirtyLocked(before, *record);
}

void ObserveTextureFetchConstantLocked(const RendererHookArgs& args,
                                       uint8_t* base) {
  if (!base || args.r5 == 0) {
    return;
  }

  constexpr uint32_t kTextureFetchConstantDwords = 6;
  const uint32_t texture_slot = args.r4 & 0xFFFFu;
  const uint32_t fetch_dword = texture_slot * kTextureFetchConstantDwords;
  if (texture_slot >= 32u ||
      fetch_dword + kTextureFetchConstantDwords > kRendererFetchConstantDwords) {
    return;
  }

  const std::array<uint32_t, 6> dwords = LoadTextureFetchDwords(base, args.r5);
  UpsertTextureResourceFromFetchDwordsLocked(dwords, base);
  RegisterExactTextureFetchBindingActionLocked(args, base, texture_slot,
                                               args.r5, dwords);
  FableDirectSetFetchConstantRange(fetch_dword, dwords.data(),
                                   kTextureFetchConstantDwords);
  ApplyVertexFetchConstantBindingsForFetchSlotLocked(texture_slot);

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
      fetch_dword + kTextureFetchConstantDwords > kRendererFetchConstantDwords) {
    return;
  }

  const std::array<uint32_t, 6> dwords =
      LoadTextureFetchDwords(base, source_address);
  UpsertTextureResourceFromFetchDwordsLocked(dwords, base);
  RendererHookArgs binding_args;
  binding_args.r3 = 0;
  binding_args.r4 = texture_slot;
  binding_args.r5 = source_address;
  binding_args.lr = log_lr;
  RegisterExactTextureFetchBindingActionLocked(binding_args, base, texture_slot,
                                               source_address, dwords);
  FableDirectSetFetchConstantRange(fetch_dword, dwords.data(),
                                   kTextureFetchConstantDwords);
  ApplyVertexFetchConstantBindingsForFetchSlotLocked(texture_slot);

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

uint64_t FableExactTextureFetchBindingContextKey(
    uint64_t call_serial, uint32_t symbol_address, uint32_t lr,
    uint32_t device, uint32_t texture_slot, uint32_t source_address,
    const std::array<uint32_t, 6>& dwords) {
  uint64_t key = 0xD6E8FEB86659FD93ull;
  const auto mix = [&](uint64_t value) {
    key ^= value + 0x9E3779B97F4A7C15ull + (key << 6u) + (key >> 2u);
  };
  mix(call_serial);
  mix(symbol_address);
  mix(lr);
  mix(device);
  mix(texture_slot);
  mix(source_address);
  for (uint32_t dword : dwords) {
    mix(dword);
  }
  return key != 0 ? key : 1ull;
}

uint64_t FableExactTextureHeaderContextKey(
    uint64_t call_serial, uint32_t symbol_address, uint32_t lr,
    uint32_t texture_header, uint32_t fetch_address,
    const std::array<uint32_t, 6>& dwords) {
  uint64_t key = 0xA64F6C975A5D089Bull;
  const auto mix = [&](uint64_t value) {
    key ^= value + 0x9E3779B97F4A7C15ull + (key << 6u) + (key >> 2u);
  };
  mix(call_serial);
  mix(symbol_address);
  mix(lr);
  mix(texture_header);
  mix(fetch_address);
  for (uint32_t dword : dwords) {
    mix(dword);
  }
  return key != 0 ? key : 1ull;
}

void RegisterExactTextureHeaderActionLocked(
    std::string_view symbol, const RendererHookArgs& args,
    uint32_t texture_header, uint32_t fetch_address,
    const std::array<uint32_t, 6>& dwords,
    const TextureFetchSnapshotInfo& fetch_info, bool stack_scratch,
    uint32_t texture_object, uint32_t parent_texture) {
  if (texture_header == 0 || fetch_address == 0 ||
      !HasTextureFetchDwords(dwords)) {
    return;
  }

  const uint32_t symbol_address = ExactTitleTextureProducerSymbolAddress(symbol);
  const uint64_t call_serial = ++g_observer.serial;
  FableTextureHeaderAction action;
  action.call_serial = call_serial;
  action.context_key = FableExactTextureHeaderContextKey(
      call_serial, symbol_address, args.lr, texture_header, fetch_address,
      dwords);
  action.symbol_address = symbol_address;
  action.lr = args.lr;
  action.frame_index = CurrentPlumeOutputFrameIndex();
  action.texture_header = texture_header;
  action.fetch_address = fetch_address;
  action.texture_object = texture_object;
  action.parent_texture = parent_texture;
  action.base_address = fetch_info.base_address;
  action.byte_size = fetch_info.size;
  action.width = fetch_info.width;
  action.height = fetch_info.height;
  action.format = fetch_info.format;
  action.stack_scratch = stack_scratch;
  action.fetch_dwords = dwords;
  RegisterExactTextureHeaderActionFromObserverLocked(action);

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_exact_texture_header_actions < 16u) {
    REXLOG_INFO(
        "Refii renderer exact texture header action #{} symbol={} "
        "header=0x{:08X} fetch=0x{:08X} object=0x{:08X} "
        "parent=0x{:08X} stack={} base=0x{:08X} size={}x{} fmt={} "
        "call={} context=0x{:016X} lr=0x{:08X}",
        ++g_observer.logged_exact_texture_header_actions, symbol,
        texture_header, fetch_address, texture_object, parent_texture,
        stack_scratch ? 1 : 0, action.base_address, action.width,
        action.height, action.format,
        static_cast<unsigned long long>(action.call_serial),
        static_cast<unsigned long long>(action.context_key), args.lr);
  }
}

void RegisterExactTextureFetchBindingActionLocked(
    const RendererHookArgs& args, uint8_t* base, uint32_t texture_slot,
    uint32_t source_address, const std::array<uint32_t, 6>& dwords,
    uint32_t binding_symbol_address) {
  if (!base || texture_slot >= 32u || source_address == 0 ||
      !HasTextureFetchDwords(dwords)) {
    return;
  }

  if (binding_symbol_address == 0) {
    binding_symbol_address = 0x82BA77D0u;
  }
  const uint32_t source_header =
      source_address >= 28u ? source_address - 28u : 0u;
  uint32_t texture_object = 0;
  uint32_t parent_texture = 0;
  if (IsLikelyFableObjectAddress(source_header) &&
      IsLikelyGuestDwordAddress(source_header + 48u)) {
    StoreTextureObjectFetchDwordsLocked(source_header, source_address,
                                        texture_slot, base, args.lr, true,
                                        false);
    texture_object = source_header;
    if (const auto record_it =
            FableDirectGuestResourceRegistry().resources.find(source_header);
        record_it != FableDirectGuestResourceRegistry().resources.end()) {
      parent_texture = GuestObjectParentAddress(record_it->second);
    }
  }

  const TextureFetchSnapshotInfo fetch_info =
      GetTextureFetchSnapshotInfo(dwords);
  FableTextureHeaderAction header_action_storage;
  FableTextureHeaderAction* header_action =
      CopyTextureHeaderActionForFetchFromRegistryLocked(
          source_address, dwords, header_action_storage)
          ? &header_action_storage
          : nullptr;
  if (header_action == nullptr && texture_object != 0 &&
      IsUsableTextureFetchSnapshot(fetch_info) &&
      source_address == texture_object + 28u) {
    const uint64_t header_call_serial = ++g_observer.serial;
    FableTextureHeaderAction object_header_action;
    object_header_action.call_serial = header_call_serial;
    object_header_action.context_key = FableExactTextureHeaderContextKey(
        header_call_serial, binding_symbol_address, args.lr,
        source_header, source_address, dwords);
    object_header_action.symbol_address = binding_symbol_address;
    object_header_action.lr = args.lr;
    object_header_action.frame_index = CurrentPlumeOutputFrameIndex();
    object_header_action.texture_header = source_header;
    object_header_action.fetch_address = source_address;
    object_header_action.texture_object = texture_object;
    object_header_action.parent_texture = parent_texture;
    object_header_action.base_address = fetch_info.base_address;
    object_header_action.byte_size = fetch_info.size;
    object_header_action.width = fetch_info.width;
    object_header_action.height = fetch_info.height;
    object_header_action.format = fetch_info.format;
    object_header_action.stack_scratch = false;
    object_header_action.fetch_dwords = dwords;
    RegisterExactTextureHeaderActionFromObserverLocked(object_header_action);
    header_action_storage = object_header_action;
    header_action = &header_action_storage;
    if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
        g_observer.logged_exact_texture_object_header_actions < 32u) {
      REXLOG_INFO(
          "Refii renderer exact texture object header action #{} slot={} "
          "header=0x{:08X} fetch=0x{:08X} object=0x{:08X} "
          "parent=0x{:08X} base=0x{:08X} size={}x{} fmt={} "
          "call={} context=0x{:016X} lr=0x{:08X}",
          ++g_observer.logged_exact_texture_object_header_actions,
          texture_slot, source_header, source_address, texture_object,
          parent_texture, fetch_info.base_address, fetch_info.width,
          fetch_info.height, fetch_info.format,
          static_cast<unsigned long long>(header_call_serial),
          static_cast<unsigned long long>(object_header_action.context_key),
          args.lr);
    }
  }
  const bool header_fetch_payload_match =
      header_action &&
      FableDirectTextureFetchKey(header_action->fetch_dwords) ==
          FableDirectTextureFetchKey(dwords);
  if (header_action) {
    if (texture_object == 0 && header_action->texture_object != 0) {
      texture_object = header_action->texture_object;
    }
    if (parent_texture == 0 && header_action->parent_texture != 0) {
      parent_texture = header_action->parent_texture;
    }
    if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
        g_observer.logged_exact_texture_fetch_header_joins < 32u) {
      REXLOG_INFO(
          "Refii renderer exact texture fetch/header join #{} slot={} "
          "src=0x{:08X} header=0x{:08X} header_serial={} "
          "header_context=0x{:016X} header_symbol=0x{:08X} stack={} "
          "object=0x{:08X} parent=0x{:08X} base=0x{:08X} size={}x{} "
          "fmt={} payload_match={} lr=0x{:08X}",
          ++g_observer.logged_exact_texture_fetch_header_joins, texture_slot,
          source_address, header_action->texture_header,
          static_cast<unsigned long long>(header_action->serial),
          static_cast<unsigned long long>(header_action->context_key),
          header_action->symbol_address, header_action->stack_scratch ? 1 : 0,
          texture_object, parent_texture, fetch_info.base_address,
          fetch_info.width, fetch_info.height, fetch_info.format,
          header_fetch_payload_match ? 1 : 0, args.lr);
    }
  }
  const uint64_t call_serial = ++g_observer.serial;
  FableTextureFetchBindingAction action;
  action.call_serial = call_serial;
  action.context_key = FableExactTextureFetchBindingContextKey(
      call_serial, binding_symbol_address, args.lr, args.r3,
      texture_slot, source_address, dwords);
  if (header_action) {
    action.header_action_serial = header_action->serial;
    action.header_context_key = header_action->context_key;
    action.header_symbol_address = header_action->symbol_address;
    action.header_stack_scratch = header_action->stack_scratch;
    action.header_fetch_payload_match = header_fetch_payload_match;
  }
  action.texture_slot = texture_slot;
  action.texture_object = texture_object;
  action.parent_texture = parent_texture;
  action.source_address = source_address;
  action.source_header = source_header;
  action.base_address = fetch_info.base_address;
  action.width = fetch_info.width;
  action.height = fetch_info.height;
  action.format = fetch_info.format;
  action.symbol_address = binding_symbol_address;
  action.lr = args.lr;
  action.frame_index = CurrentPlumeOutputFrameIndex();
  action.fetch_dwords = dwords;
  RegisterExactTextureFetchBindingActionFromObserverLocked(action);

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_exact_texture_fetch_bindings < 16u) {
    REXLOG_INFO(
        "Refii renderer exact texture fetch binding action #{} slot={} "
        "object=0x{:08X} parent=0x{:08X} src=0x{:08X} header=0x{:08X} "
        "base=0x{:08X} size={}x{} fmt={} call={} context=0x{:016X} "
        "header_serial={} header_context=0x{:016X} header_stack={} "
        "header_payload_match={} lr=0x{:08X}",
        ++g_observer.logged_exact_texture_fetch_bindings, texture_slot,
        texture_object, parent_texture, source_address, source_header,
        action.base_address, action.width, action.height, action.format,
        static_cast<unsigned long long>(action.call_serial),
        static_cast<unsigned long long>(action.context_key),
        static_cast<unsigned long long>(action.header_action_serial),
        static_cast<unsigned long long>(action.header_context_key),
        action.header_stack_scratch ? 1 : 0,
        action.header_fetch_payload_match ? 1 : 0, args.lr);
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
  if (it == FableDirectGuestResourceRegistry().resources.end()) {
    RendererResourceRecord record =
        CreateGuestTextureResource(texture_object, texture_object);
    SnapshotGuestResource(record, base);
    record.serial = ++g_observer.serial;
    RendererResourceRecord& stored =
        StoreGuestResourceLocked(std::move(record), true);
    it = FableDirectGuestResourceRegistry().resources.find(GuestObjectAddress(stored));
  } else if (!IsImageResourceKind(GuestObjectKind(it->second))) {
    return;
  }

  const std::array<uint32_t, 6> dwords =
      LoadTextureFetchDwords(base, source_address);
  if (!HasTextureFetchDwords(dwords)) {
    return;
  }
  UpsertTextureResourceFromFetchDwordsLocked(dwords, base);

  const RendererResourceRecord before = it->second;
  GuestTextureRecord texture = GuestTextureRecordFor(it->second);
  const bool first_fetch =
      !HasTextureFetchDwords(texture.normalized_fetch_dwords);
  texture.normalized_fetch_dwords = dwords;
  const uint32_t texture_base = TextureBaseFromFetchDwords(dwords);
  if (GuestObjectKind(it->second) == RendererResourceKind::kTexture) {
    texture.raw_fetch_dwords = dwords;
  }
  ApplyGuestTextureRecord(it->second, texture);
  if (GuestObjectKind(it->second) == RendererResourceKind::kTexture &&
      (refresh_upload ||
       ShouldCaptureTextureUploadFromFetchDwords(it->second, dwords, base))) {
    CaptureTextureUploadFromFetchDwords(it->second, dwords, base);
  }
  RefreshTextureUploadFromPendingPixDecodedFrameLocked(it->second, dwords,
                                                      base);
  MarkTextureResourceChangedIfDirtyLocked(before, it->second);
  RegisterTextureIdentityForRecordLocked(it->second);
  if (texture_base != 0 && texture_base != texture_object) {
    RemoveDuplicateTextureBaseResourceLocked(texture_object, texture_base,
                                            dwords);
  }

  if (texture_slot < 32u) {
    const uint32_t fetch_dword = texture_slot * 6u;
    if (fetch_dword + dwords.size() <= kRendererFetchConstantDwords) {
      FableDirectSetFetchConstantRange(
          fetch_dword, dwords.data(), static_cast<uint32_t>(dwords.size()));
      if (update_existing_draws) {
        RefreshQueuedDrawTextureStateLocked();
      }
    }
  }

  if (RendererVerboseTracingEnabled() && first_fetch) {
    REXLOG_INFO(
        "Refii renderer normalized texture fetch object=0x{:08X} slot={} "
        "src=0x{:08X} dwords={:08X} {:08X} {:08X} {:08X} {:08X} {:08X} "
        "lr=0x{:08X}",
        texture_object, texture_slot, source_address, dwords[0], dwords[1],
        dwords[2], dwords[3], dwords[4], dwords[5], log_lr);
  }
}

uint32_t FableStackAddressAfterReturn(const RendererHookArgs& exit_args,
                                      uint32_t frame_size,
                                      uint32_t offset) {
  if (frame_size == 0 || exit_args.r1 < frame_size) {
    return 0;
  }

  const uint32_t local_sp = exit_args.r1 - frame_size;
  if (offset > std::numeric_limits<uint32_t>::max() - local_sp) {
    return 0;
  }

  return local_sp + offset;
}

bool GuestTextureFetchRangeReadable(uint32_t address) {
  return address <= std::numeric_limits<uint32_t>::max() - 20u &&
         IsLikelyGuestDwordAddress(address) &&
         IsLikelyGuestDwordAddress(address + 20u);
}

void LogExactTextureWrapperSourceLocked(std::string_view symbol,
                                        const RendererHookArgs& entry_args,
                                        const RendererHookArgs& exit_args,
                                        uint8_t* base, uint32_t frame_size,
                                        uint32_t header_offset,
                                        uint32_t pm4_source_address,
                                        uint32_t texture_slot) {
  if (!base || frame_size == 0 || pm4_source_address == 0 ||
      exit_args.r1 < frame_size ||
      !(RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) ||
      g_observer.logged_exact_texture_wrapper_sources >= 48u) {
    return;
  }

  const uint32_t local_sp = exit_args.r1 - frame_size;
  const uint32_t header_address =
      FableStackAddressAfterReturn(exit_args, frame_size, header_offset);
  if (header_address == 0 ||
      header_address > std::numeric_limits<uint32_t>::max() - 28u) {
    return;
  }
  const uint32_t fetch_address = header_address + 28u;
  const bool header_readable = GuestTextureFetchRangeReadable(fetch_address);
  const bool source_readable =
      GuestTextureFetchRangeReadable(pm4_source_address);
  if (!header_readable && !source_readable) {
    return;
  }

  const std::array<uint32_t, 6> header_dwords =
      header_readable ? LoadTextureFetchDwords(base, fetch_address)
                      : std::array<uint32_t, 6>{};
  const std::array<uint32_t, 6> source_dwords =
      source_readable ? LoadTextureFetchDwords(base, pm4_source_address)
                      : std::array<uint32_t, 6>{};
  const bool header_valid = HasTextureFetchDwords(header_dwords);
  const bool source_valid = HasTextureFetchDwords(source_dwords);
  const TextureFetchSnapshotInfo header_info =
      GetTextureFetchSnapshotInfo(header_dwords);
  const TextureFetchSnapshotInfo source_info =
      GetTextureFetchSnapshotInfo(source_dwords);
  const bool source_matches_header_fetch = pm4_source_address == fetch_address;
  const bool payload_match =
      header_valid && source_valid &&
      FableDirectTextureFetchKey(header_dwords) ==
          FableDirectTextureFetchKey(source_dwords);

  REXLOG_INFO(
      "Refii renderer exact texture wrapper source #{} symbol={} "
      "local_sp=0x{:08X} header=0x{:08X} fetch=0x{:08X} "
      "pm4_src=0x{:08X} slot={} src_matches={} payload_match={} "
      "header_readable={} pm4_readable={} header_valid={} pm4_valid={} "
      "header_base=0x{:08X} pm4_base=0x{:08X} header_size={}x{} "
      "pm4_size={}x{} header_fmt={} pm4_fmt={} entry_lr=0x{:08X} "
      "exit_lr=0x{:08X}",
      ++g_observer.logged_exact_texture_wrapper_sources, symbol, local_sp,
      header_address, fetch_address, pm4_source_address, texture_slot,
      source_matches_header_fetch ? 1 : 0, payload_match ? 1 : 0,
      header_readable ? 1 : 0, source_readable ? 1 : 0,
      header_valid ? 1 : 0, source_valid ? 1 : 0,
      header_info.base_address, source_info.base_address, header_info.width,
      header_info.height, source_info.width, source_info.height,
      header_info.format, source_info.format, entry_args.lr, exit_args.lr);
}

void StoreBoundTextureObjectFetchDwordsLocked(uint32_t device,
                                              uint32_t texture_slot,
                                              uint32_t texture_object,
                                              uint8_t* base,
                                              uint32_t log_lr,
                                              bool update_existing_draws = true) {
  if (!base || texture_object == 0 || texture_slot >= 16u) {
    return;
  }

  const uint32_t object_fetch_address = texture_object + 0x1Cu;
  const std::array<uint32_t, 6> object_fetch =
      LoadTextureFetchDwords(base, object_fetch_address);
  const uint32_t device_fetch_address =
      device + kFableDeviceTextureFetchBaseOffset +
      texture_slot * kFableDeviceTextureFetchStride;
  const std::array<uint32_t, 6> device_fetch =
      device != 0 ? LoadTextureFetchDwords(base, device_fetch_address)
                  : std::array<uint32_t, 6>{};
  const uint32_t source_fetch_address =
      HasTextureFetchDwords(object_fetch)
          ? object_fetch_address
          : (HasTextureFetchDwords(device_fetch) ? device_fetch_address : 0u);
  if (source_fetch_address == 0) {
    return;
  }

  StoreTextureObjectFetchDwordsLocked(texture_object, source_fetch_address,
                                      texture_slot, base, log_lr, false,
                                      update_existing_draws);
  RendererHookArgs binding_args;
  binding_args.r3 = device;
  binding_args.r4 = texture_slot;
  binding_args.r5 = texture_object;
  binding_args.lr = log_lr;
  RegisterExactTextureFetchBindingActionLocked(
      binding_args, base, texture_slot, source_fetch_address,
      LoadTextureFetchDwords(base, source_fetch_address), 0x821B6EE0u);
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

uint32_t SanitizeFableShaderFloatConstantDword(bool pixel_bank,
                                               uint32_t dword_index,
                                               uint32_t value) {
  // Fable's title/menu tonemap shader uses +inf as a colour sensitivity
  // threshold. Vulkan HLSL can turn inf * 0 into NaN, which then packs to a
  // fullscreen white target. A huge finite value keeps the threshold behavior
  // for positive luminance without poisoning blank/black input.
  constexpr uint32_t kTitleToneMapColourSensitivityDword = 47u * 4u;
  if (!pixel_bank || dword_index != kTitleToneMapColourSensitivityDword) {
    return value;
  }

  const float as_float = std::bit_cast<float>(value);
  if (!std::isinf(as_float) || as_float <= 0.0f) {
    return value;
  }

  static uint32_t logged_sanitizes = 0;
  if (logged_sanitizes < 4u) {
    REXLOG_INFO(
        "Refii renderer sanitized title tonemap shader constant c47.x "
        "from +inf to finite threshold");
    ++logged_sanitizes;
  }
  return std::bit_cast<uint32_t>(1.0e20f);
}

void SanitizeFableShaderFloatConstants(
    bool pixel_bank,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants) {
  for (uint32_t dword = 0; dword < constants.size(); ++dword) {
    constants[dword] =
        SanitizeFableShaderFloatConstantDword(pixel_bank, dword, constants[dword]);
  }
}

void ObserveRegisterWriteLocked(uint32_t register_index, uint32_t value) {
  ObserveDisplayRegisterWriteLocked(register_index, value);
  ObserveFetchConstantWriteLocked(register_index, value);
  switch (register_index) {
    case kXenosRegRbSurfaceInfo:
      FableDirectSetPm4Field(FableDirectPm4Field::kSurfaceInfo, value);
      break;
    case kXenosRegRbColorInfo0:
      FableDirectSetPm4Field(FableDirectPm4Field::kColorInfo0, value);
      break;
    case kXenosRegRbColorInfo1:
      FableDirectSetPm4Field(FableDirectPm4Field::kColorInfo1, value);
      break;
    case kXenosRegRbColorInfo2:
      FableDirectSetPm4Field(FableDirectPm4Field::kColorInfo2, value);
      break;
    case kXenosRegRbColorInfo3:
      FableDirectSetPm4Field(FableDirectPm4Field::kColorInfo3, value);
      break;
    case kXenosRegRbDepthInfo:
      FableDirectSetPm4Field(FableDirectPm4Field::kDepthInfo, value);
      break;
    case kXenosRegRbCopyControl:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopyControl, value);
      break;
    case kXenosRegRbCopyDestBase:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopyDestBase, value);
      break;
    case kXenosRegRbCopyDestPitch:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopyDestPitch, value);
      break;
    case kXenosRegRbCopyDestInfo:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopyDestInfo, value);
      break;
    case kXenosRegRbCopyFunc:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopyFunc, value);
      break;
    case kXenosRegRbCopyRef:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopyRef, value);
      break;
    case kXenosRegRbCopyMask:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopyMask, value);
      break;
    case kXenosRegRbCopySurfaceSlice:
      FableDirectSetPm4Field(FableDirectPm4Field::kCopySurfaceSlice, value);
      break;
    case kXenosRegRbColorMask:
      FableDirectSetPm4Field(FableDirectPm4Field::kColorMask, value);
      break;
    case kXenosRegRbStencilRefMaskBf:
      FableDirectSetPm4Field(FableDirectPm4Field::kStencilRefMaskBackFace,
                             value);
      break;
    case kXenosRegRbStencilRefMask:
      FableDirectSetPm4Field(FableDirectPm4Field::kStencilRefMask, value);
      break;
    case kXenosRegRbAlphaRef:
      FableDirectSetPm4Field(FableDirectPm4Field::kAlphaRef, value);
      break;
    case kXenosRegRbDepthControl:
      FableDirectSetPm4Field(FableDirectPm4Field::kDepthControl, value);
      break;
    case kXenosRegRbBlendControl0:
      FableDirectSetPm4Field(FableDirectPm4Field::kBlendControl0, value);
      break;
    case kXenosRegRbColorControl:
      FableDirectSetPm4Field(FableDirectPm4Field::kColorControl, value);
      break;
    case kXenosRegPaSuScModeCntl:
      FableDirectSetPm4Field(FableDirectPm4Field::kPaSuScModeCntl, value);
      break;
    case kXenosRegRbModeControl:
      FableDirectSetPm4Field(FableDirectPm4Field::kModeControl, value);
      break;
    case kXenosRegRbBlendControl1:
      FableDirectSetPm4Field(FableDirectPm4Field::kBlendControl1, value);
      break;
    case kXenosRegRbBlendControl2:
      FableDirectSetPm4Field(FableDirectPm4Field::kBlendControl2, value);
      break;
    case kXenosRegRbBlendControl3:
      FableDirectSetPm4Field(FableDirectPm4Field::kBlendControl3, value);
      break;
    default:
      break;
  }
  if (register_index < kAluConstantRegisterFirst ||
      register_index > kAluConstantRegisterLast) {
    return;
  }

  const bool pixel_bank = register_index >= kAluConstantRegisterPixelFirst;
  const uint32_t dword_index =
      register_index >= kAluConstantRegisterPixelFirst
          ? register_index - kAluConstantRegisterPixelFirst
          : register_index - kAluConstantRegisterFirst;
  if (dword_index >= kRendererShaderFloatConstantDwords) {
    return;
  }

  const uint32_t sanitized_value =
      SanitizeFableShaderFloatConstantDword(pixel_bank, dword_index, value);
  FableDirectSetShaderFloatConstant(pixel_bank ? 1u : 0u, dword_index,
                                    sanitized_value);
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

void ObservePendingSplitRegisterRangeLocked(const RendererHookArgs& args,
                                            uint8_t* base) {
  if (!base || args.r6 == 0 || args.r7 == 0) {
    return;
  }

  const uint32_t register_base = args.r5;
  const uint32_t source_address = args.r6 + sizeof(uint32_t);
  const uint32_t dword_count = std::min<uint32_t>(args.r7, 256u);
  if (!IsLikelyGuestDwordAddress(source_address + (dword_count - 1u) *
                                                     sizeof(uint32_t))) {
    return;
  }

  uint32_t observed_shader_dwords = 0;
  uint32_t observed_nonzero_shader_dwords = 0;
  uint32_t first_shader_register_index = 0;
  for (uint32_t i = 0; i < dword_count; ++i) {
    const uint32_t register_index = register_base + i;
    const uint32_t value = LoadGuestU32(base, source_address + i * 4u);
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
    g_observer.last_shader_constant_source = source_address;
    g_observer.last_shader_constant_lr = args.lr;
    for (uint32_t i = 0; i < std::size(g_observer.last_shader_constant_preview);
         ++i) {
      g_observer.last_shader_constant_preview[i] =
          i < dword_count ? LoadGuestU32(base, source_address + i * 4u) : 0u;
    }
  }
}

void ObservePendingRegisterRangeFromRootDrawLocked(uint32_t device,
                                                   uint64_t dirty_mask,
                                                   uint32_t register_base,
                                                   uint32_t source_address,
                                                   uint32_t lr) {
  if (dirty_mask == 0 || device == 0 || source_address == 0) {
    return;
  }

  RendererHookArgs pending_args = {};
  pending_args.r3 = device;
  pending_args.r4 = static_cast<uint32_t>(dirty_mask >> 32u);
  pending_args.r5 = register_base;
  pending_args.r6 = source_address;
  pending_args.r4_u64 = dirty_mask;
  pending_args.lr = lr;
  ObservePendingRegisterRangeLocked(pending_args, g_observer.memory_base);
}

void ObservePendingFetchConstantsFromRootDrawLocked(uint32_t device,
                                                    uint64_t dirty_mask,
                                                    uint32_t lr) {
  if (dirty_mask == 0 || device == 0) {
    return;
  }

  RendererHookArgs pending_args = {};
  pending_args.r3 = device;
  pending_args.r4 = static_cast<uint32_t>(dirty_mask >> 32u);
  pending_args.r4_u64 = dirty_mask;
  pending_args.lr = lr;
  ObservePendingFetchConstantsLocked(pending_args, g_observer.memory_base);
}

void FlushPendingDeviceStateForRootDrawLocked(const RendererHookArgs& args,
                                              uint8_t* base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return;
  }

  RememberGuestMemoryBaseLocked(base);
  const uint32_t device = args.r3;

  if (LoadGuestU64(base, device + 0x00u) != 0) {
    StoreGuestU64(base, device + 0x00u, 0);
  }
  if (LoadGuestU64(base, device + 0x08u) != 0) {
    StoreGuestU64(base, device + 0x08u, 0);
  }

  const uint64_t render_state_dirty = LoadGuestU64(base, device + 0x10u);
  if (render_state_dirty != 0) {
    ObservePendingRegisterRangeFromRootDrawLocked(
        device, (render_state_dirty & 0x0000000000000FFFull) << 52u, 0x2200u,
        device + 0x2934u, args.lr);
    ObservePendingRegisterRangeFromRootDrawLocked(
        device, ((render_state_dirty >> 12u) & 0x1Full) << 59u, 0x2180u,
        device + 0x2920u, args.lr);
    ObservePendingRegisterRangeFromRootDrawLocked(
        device, ((render_state_dirty >> 21u) & 0x1FFFFFull) << 43u, 0x2100u,
        device + 0x28CCu, args.lr);
    ObservePendingRegisterRangeFromRootDrawLocked(
        device, ((render_state_dirty >> 42u) & 0xFFFFull) << 48u, 0x2000u,
        device + 0x2880u, args.lr);
    StoreGuestU64(base, device + 0x10u, 0);
  }

  const uint64_t fetch_dirty = LoadGuestU64(base, device + 0x18u);
  if (fetch_dirty != 0) {
    ObservePendingFetchConstantsFromRootDrawLocked(
        device, (fetch_dirty & 0xFFFFFFFFull) << 32u, args.lr);
    ObservePendingRegisterRangeFromRootDrawLocked(
        device, ((fetch_dirty >> 34u) & 0x1FFFFFull) << 43u, 0x2280u,
        device + 0x2964u, args.lr);
    StoreGuestU64(base, device + 0x18u, 0);
  }

  const uint64_t scalar_dirty = LoadGuestU64(base, device + 0x20u);
  if (scalar_dirty != 0) {
    ObservePendingRegisterRangeFromRootDrawLocked(
        device, (scalar_dirty & 0x0000003FFFFFFFFFull) << 26u, 0x2300u,
        device + 0x29B8u, args.lr);
    ObservePendingRegisterRangeFromRootDrawLocked(
        device, ((scalar_dirty >> 38u) & 0xFFull) << 56u, 0x2380u,
        device + 0x2A50u, args.lr);
    if ((scalar_dirty & (1ull << 56u)) != 0) {
      ObservePendingRegisterRangeFromRootDrawLocked(
          device, 0xFF00000000000000ull, 0x4900u, device + 0x2780u, args.lr);
    }
    StoreGuestU64(base, device + 0x20u, 0);
  }
}

enum class ShaderConstantBank {
  kVertex,
  kPixel,
};

ShaderConstantBank ToShaderConstantBank(FableShaderConstantBank bank) {
  return bank == FableShaderConstantBank::kPixel ? ShaderConstantBank::kPixel
                                                 : ShaderConstantBank::kVertex;
}

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
    const FableDirectStateSnapshot direct_snapshot =
        GetFableDirectStateSnapshot();
    const uint32_t vertex_shader = direct_snapshot.shaders.vertex_shader;
    const uint32_t pixel_shader = direct_snapshot.shaders.pixel_shader;
    if (vertex_shader != 0 && args.r4 == vertex_shader + 872u &&
        args.r5 == LoadGuestU32(base, vertex_shader + 32u)) {
      return ShaderConstantBank::kVertex;
    }
    if (pixel_shader != 0 && args.r4 == pixel_shader + 40u &&
        args.r5 == LoadGuestU32(base, pixel_shader + 24u)) {
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

  if (first_component >= 4u) {
    first_register += first_component / 4u;
    first_component %= 4u;
  }
  const uint32_t first_dword = first_register * 4u + first_component;
  if (first_dword >= kRendererShaderFloatConstantDwords) {
    ++g_observer.shader_constant_skipped_uploads;
    return;
  }

  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  auto constants = bank == ShaderConstantBank::kPixel
                       ? direct_snapshot.constants.pixel_float
                       : direct_snapshot.constants.vertex_float;
  const uint32_t writable_dwords =
      std::min<uint32_t>(dword_count,
                         kRendererShaderFloatConstantDwords - first_dword);
  uint32_t upload_nonzero_dwords = 0;
  uint32_t upload_preview[4] = {};
  for (uint32_t dword_index = 0; dword_index < writable_dwords; ++dword_index) {
    uint32_t value =
        LoadGuestU32(base, source_address + dword_index * sizeof(uint32_t));
    value = SanitizeFableShaderFloatConstantDword(
        bank == ShaderConstantBank::kPixel, first_dword + dword_index, value);
    constants[first_dword + dword_index] = value;
    if (dword_index < std::size(upload_preview)) {
      upload_preview[dword_index] = value;
    }
    if (value != 0) {
      ++upload_nonzero_dwords;
    }
  }
  FableDirectSetShaderFloatConstantRange(
      bank == ShaderConstantBank::kPixel ? 1u : 0u, first_dword,
      constants.data() + first_dword, writable_dwords);

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

  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  auto constants = bank == ShaderConstantBank::kPixel
                       ? direct_snapshot.constants.pixel_int
                       : direct_snapshot.constants.vertex_int;
  uint32_t valid_mask = bank == ShaderConstantBank::kPixel
                            ? direct_snapshot.constants.pixel_int_valid_mask
                            : direct_snapshot.constants.vertex_int_valid_mask;
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
  FableDirectSetShaderIntConstantRange(
      bank == ShaderConstantBank::kPixel ? 1u : 0u, first_register * 4u,
      constants.data() + first_register * 4u, writable_vectors * 4u);
  FableDirectSetShaderIntValidMask(
      bank == ShaderConstantBank::kPixel ? 1u : 0u, valid_mask);

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

  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  auto constants = bank == ShaderConstantBank::kPixel
                       ? direct_snapshot.constants.pixel_bool
                       : direct_snapshot.constants.vertex_bool;
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
  FableDirectSetShaderBoolConstantsSnapshot(
      bank == ShaderConstantBank::kPixel ? 1u : 0u, constants);

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
    FableDirectSetVertexShader(0, bytecode_address);
  } else {
    FableDirectSetPixelShader(0, bytecode_address);
  }
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
          if (offset + packet_words <= command_word_count) {
            QueuePm4DrawEventLocked(base, command_pointer, offset,
                                    packet_words, opcode, source);
          }
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
        FableHookName(symbol) ? FableHookName(symbol) : symbol.data(),
        command_word_count, g_observer.last_command_first_word,
        g_observer.last_command_packet_type,
        g_observer.last_command_type3_opcode,
        FablePm4OpcodeName(g_observer.last_command_type3_opcode),
        g_observer.pm4_type0_packets, g_observer.pm4_type3_packets,
        g_observer.pm4_draw_packets, g_observer.pm4_indirect_packets,
        g_observer.pm4_constant_packets, g_observer.pm4_swap_packets,
        g_observer.pm4_parse_errors);
  }
}

void TraceDrawCall(std::string_view symbol,
                   const RendererLastDrawSnapshot& draw) {
  if (!RendererVerboseTracingEnabled()) {
    return;
  }

  REXLOG_INFO(
      "Refii renderer draw {} indexed={} primitive={} base_vertex={} "
      "start={} primitive_count={}",
      FableHookName(symbol) ? FableHookName(symbol) : symbol.data(),
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
      FableHookName(symbol) ? FableHookName(symbol) : symbol.data(),
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

FableDirectPm4Field FableColorInfoFieldForSlot(uint32_t slot) {
  switch (slot) {
    case 0:
      return FableDirectPm4Field::kColorInfo0;
    case 1:
      return FableDirectPm4Field::kColorInfo1;
    case 2:
      return FableDirectPm4Field::kColorInfo2;
    default:
      return FableDirectPm4Field::kColorInfo3;
  }
}

GuestSurfaceRecord ReadTrackedOrGuestSurfaceRecordLocked(
    RendererResourceKind expected_kind, uint32_t surface_address,
    uint8_t* base) {
  GuestSurfaceRecord surface;
  if (surface_address == 0 || !IsLikelyFableObjectAddress(surface_address)) {
    return surface;
  }

  if (auto it = FindResourceIteratorLocked(surface_address);
      it != FableDirectGuestResourceRegistry().resources.end() &&
      GuestObjectKind(it->second) == expected_kind) {
    surface = GuestSurfaceRecordFor(it->second);
  }

  if (base) {
    if (surface.object_address == 0) {
      surface.object_address = surface_address;
    }
    if (surface.pitch_msaa == 0) {
      surface.pitch_msaa =
          LoadGuestU32(base, surface_address + kFableSurfacePitchMsaaOffset);
    }
    if (surface.render_target_info == 0) {
      surface.render_target_info = LoadGuestU32(
          base, surface_address + kFableSurfaceRenderTargetInfoOffset);
    }
    if (surface.depth_stencil_info == 0) {
      surface.depth_stencil_info = LoadGuestU32(
          base, surface_address + kFableSurfaceDepthStencilInfoOffset);
    }
  }

  return surface;
}

void MirrorFableRenderTargetPm4Locked(uint32_t slot, uint32_t render_target,
                                      uint8_t* base,
                                      FableDirectStateRoot root = {},
                                      bool record_action = true) {
  if (slot >= 4u) {
    return;
  }

  auto set_pm4_field = [&](FableDirectPm4Field field, uint32_t value) {
    if (record_action) {
      FableDirectSetPm4Field(field, value, root);
    } else {
      FableDirectMirrorPm4Field(field, value);
    }
  };

  if (render_target == 0) {
    set_pm4_field(FableColorInfoFieldForSlot(slot), 0);
    return;
  }

  const GuestSurfaceRecord surface = ReadTrackedOrGuestSurfaceRecordLocked(
      RendererResourceKind::kRenderTargetSurface, render_target, base);
  if (surface.pitch_msaa != 0) {
    set_pm4_field(FableDirectPm4Field::kSurfaceInfo, surface.pitch_msaa);
  }
  if (surface.render_target_info != 0) {
    set_pm4_field(FableColorInfoFieldForSlot(slot),
                  surface.render_target_info);
  }
}

void MirrorFableDepthSurfacePm4Locked(uint32_t depth_surface, uint8_t* base,
                                      FableDirectStateRoot root = {},
                                      bool record_action = true) {
  auto set_pm4_field = [&](FableDirectPm4Field field, uint32_t value) {
    if (record_action) {
      FableDirectSetPm4Field(field, value, root);
    } else {
      FableDirectMirrorPm4Field(field, value);
    }
  };

  if (depth_surface == 0) {
    set_pm4_field(FableDirectPm4Field::kDepthInfo, 0);
    return;
  }

  const GuestSurfaceRecord surface = ReadTrackedOrGuestSurfaceRecordLocked(
      RendererResourceKind::kDepthSurface, depth_surface, base);
  if (surface.pitch_msaa != 0) {
    const FableDirectStateSnapshot direct_snapshot =
        GetFableDirectStateSnapshot();
    if (direct_snapshot.targets.color[0] == 0 ||
        direct_snapshot.pm4.rb_surface_info == 0) {
      set_pm4_field(FableDirectPm4Field::kSurfaceInfo, surface.pitch_msaa);
    }
  }

  const uint32_t depth_info =
      surface.render_target_info != 0 ? surface.render_target_info
                                      : surface.depth_stencil_info;
  if (depth_info != 0) {
    set_pm4_field(FableDirectPm4Field::kDepthInfo, depth_info);
  }
}

void ApplyDefaultViewportForBoundSurfaceLocked(uint8_t* base, uint32_t device,
                                               uint32_t surface,
                                               FableDirectStateRoot root = {},
                                               bool record_action = true);
bool CurrentColorRenderTargetHasExtentLocked(uint8_t* base, uint32_t device);

uint64_t FableExactFramebufferStateContextKey(uint64_t call_serial,
                                              uint32_t root_symbol_address,
                                              uint32_t root_lr,
                                              uint32_t device, uint32_t slot,
                                              uint32_t surface) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(call_serial);
  mix(root_symbol_address);
  mix(root_lr);
  mix(device);
  mix(slot);
  mix(surface);
  return key;
}

FableDirectStateRoot FableExactFramebufferStateRootLocked(
    uint32_t root_symbol_address, uint32_t root_lr, uint32_t device,
    uint32_t slot, uint32_t surface) {
  if (root_symbol_address == 0) {
    return {};
  }

  FableDirectStateRoot root;
  root.call_serial = ++g_observer.serial;
  root.symbol_address = root_symbol_address;
  root.lr = root_lr;
  root.context_key = FableExactFramebufferStateContextKey(
      root.call_serial, root_symbol_address, root_lr, device, slot, surface);
  return root;
}

uint32_t ParseFableSubSymbolAddress(std::string_view symbol) {
  if (symbol.size() != 12 || symbol.substr(0, 4) != "sub_") {
    return 0;
  }

  uint32_t value = 0;
  for (size_t i = 4; i < symbol.size(); ++i) {
    const char c = symbol[i];
    uint32_t nibble = 0;
    if (c >= '0' && c <= '9') {
      nibble = static_cast<uint32_t>(c - '0');
    } else if (c >= 'A' && c <= 'F') {
      nibble = static_cast<uint32_t>(c - 'A' + 10);
    } else if (c >= 'a' && c <= 'f') {
      nibble = static_cast<uint32_t>(c - 'a' + 10);
    } else {
      return 0;
    }
    value = (value << 4u) | nibble;
  }
  return value;
}

uint32_t FableExactStateSymbolAddress(std::string_view symbol) {
  const uint32_t parsed_address = ParseFableSubSymbolAddress(symbol);
  if (parsed_address != 0) {
    return parsed_address;
  }
  if (symbol == "rex_XGSetVertexDeclaration") {
    return 0x82B99630u;
  }
  return 0;
}

uint64_t FableExactStateContextKey(uint64_t call_serial,
                                   uint32_t root_symbol_address,
                                   uint32_t root_lr, uint32_t device,
                                   uint32_t slot, uint32_t value,
                                   uint32_t auxiliary_value) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t mix_value) {
    key ^= mix_value;
    key *= 1099511628211ull;
  };
  mix(call_serial);
  mix(root_symbol_address);
  mix(root_lr);
  mix(device);
  mix(slot);
  mix(value);
  mix(auxiliary_value);
  return key;
}

FableDirectStateRoot FableExactStateRootLocked(uint32_t root_symbol_address,
                                               uint32_t root_lr,
                                               uint32_t device,
                                               uint32_t slot,
                                               uint32_t value,
                                               uint32_t auxiliary_value = 0) {
  if (root_symbol_address == 0) {
    return {};
  }

  FableDirectStateRoot root;
  root.call_serial = ++g_observer.serial;
  root.symbol_address = root_symbol_address;
  root.lr = root_lr;
  root.context_key =
      FableExactStateContextKey(root.call_serial, root_symbol_address, root_lr,
                                device, slot, value, auxiliary_value);
  return root;
}

void SetObservedRenderTargetLocked(uint32_t slot, uint32_t render_target,
                                   uint32_t device, uint8_t* base,
                                   uint32_t root_symbol_address = 0,
                                   uint32_t root_lr = 0,
                                   FableDirectStateRoot default_viewport_root =
                                       {},
                                   bool record_action = true) {
  if (slot >= 4u) {
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
  const FableDirectStateRoot root =
      requested_render_target == render_target
          ? FableExactFramebufferStateRootLocked(root_symbol_address, root_lr,
                                                device, slot, render_target)
          : FableDirectStateRoot{};
  if (record_action) {
    FableDirectSetRenderTarget(device, slot, render_target, root);
  } else {
    FableDirectMirrorRenderTargetState(device, slot, render_target, root);
  }
  MirrorFableRenderTargetPm4Locked(slot, render_target, base, root,
                                   record_action);
  if (slot == 0 && render_target != 0) {
    const FableDirectStateRoot viewport_root =
        default_viewport_root.call_serial != 0 ? default_viewport_root : root;
    ApplyDefaultViewportForBoundSurfaceLocked(base, device, render_target,
                                              viewport_root, record_action);
  }
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

FableSurfaceExtent CurrentRawFableDrawSurfaceExtent(uint8_t* base,
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

FableSurfaceExtent PromoteMainFableSurfaceExtentToOutput(
    FableSurfaceExtent extent) {
  if (extent.width == 0 || extent.height == 0) {
    return extent;
  }

  const uint32_t output_width = static_cast<uint32_t>(
      std::max(1, RendererOutputWidth()) *
      std::max(1, RendererInternalScale()));
  const uint32_t output_height = static_cast<uint32_t>(
      std::max(1, RendererOutputHeight()) *
      std::max(1, RendererInternalScale()));
  if (!IsPlausibleFableSurfaceExtent(output_width, output_height) ||
      output_width <= extent.width || output_height <= extent.height) {
    return extent;
  }

  auto scaled_output = [&](uint32_t divisor) {
    return FableSurfaceExtent{
        .width = std::max(1u, output_width / std::max(1u, divisor)),
        .height = std::max(1u, output_height / std::max(1u, divisor)),
    };
  };

  const bool same_aspect =
      uint64_t(extent.width) * uint64_t(output_height) ==
      uint64_t(extent.height) * uint64_t(output_width);
  const bool main_720p_surface =
      extent.width == kFableNativeViewportWidth &&
      extent.height == kFableNativeViewportHeight;
  const bool main_edram_slice_surface =
      extent.width == kFableNativeViewportWidth && extent.height == 512u &&
      uint64_t(kFableNativeViewportWidth) * uint64_t(output_height) ==
          uint64_t(kFableNativeViewportHeight) * uint64_t(output_width);
  if ((!same_aspect || !main_720p_surface) && !main_edram_slice_surface) {
    const bool title_full_surface =
        extent.width == 1120u &&
        (extent.height == 720u || extent.height == 576u);
    if (title_full_surface) {
      return scaled_output(1u);
    }
    if (extent.width == 560u && extent.height == 360u) {
      return scaled_output(2u);
    }
    if (extent.width == 280u && extent.height == 180u) {
      return scaled_output(4u);
    }
    return extent;
  }

  return scaled_output(1u);
}

FableSurfaceExtent NormalizeMainFableSurfaceExtentForScaling(
    FableSurfaceExtent extent) {
  if (extent.width == kFableNativeViewportWidth && extent.height == 512u) {
    return {.width = static_cast<uint32_t>(kFableNativeViewportWidth),
            .height = static_cast<uint32_t>(kFableNativeViewportHeight)};
  }
  return extent;
}

FableSurfaceExtent CurrentFableDrawSurfaceExtent(uint8_t* base,
                                                 uint32_t device) {
  const FableSurfaceExtent extent =
      CurrentRawFableDrawSurfaceExtent(base, device);
  if (extent.width != 0 && extent.height != 0) {
    return PromoteMainFableSurfaceExtentToOutput(extent);
  }

  return {};
}

RendererViewportSnapshot ScaleGuestViewportToCurrentDrawSurface(
    uint8_t* base, uint32_t device, RendererViewportSnapshot viewport) {
  if (!viewport.valid) {
    return viewport;
  }

  const FableSurfaceExtent raw_extent =
      NormalizeMainFableSurfaceExtentForScaling(
          CurrentRawFableDrawSurfaceExtent(base, device));
  const FableSurfaceExtent target_extent =
      CurrentFableDrawSurfaceExtent(base, device);
  if (raw_extent.width == 0 || raw_extent.height == 0 ||
      target_extent.width == 0 || target_extent.height == 0 ||
      (raw_extent.width == target_extent.width &&
       raw_extent.height == target_extent.height)) {
    return viewport;
  }

  const float scale_x =
      static_cast<float>(target_extent.width) / static_cast<float>(raw_extent.width);
  const float scale_y =
      static_cast<float>(target_extent.height) / static_cast<float>(raw_extent.height);
  if (!std::isfinite(scale_x) || !std::isfinite(scale_y) ||
      scale_x <= 0.0f || scale_y <= 0.0f) {
    return viewport;
  }

  viewport.x *= scale_x;
  viewport.y *= scale_y;
  viewport.width *= scale_x;
  viewport.height *= scale_y;
  return viewport;
}

RendererScissorSnapshot ScaleGuestScissorToCurrentDrawSurface(
    uint8_t* base, uint32_t device, RendererScissorSnapshot scissor) {
  if (!scissor.valid) {
    return scissor;
  }

  const FableSurfaceExtent raw_extent =
      NormalizeMainFableSurfaceExtentForScaling(
          CurrentRawFableDrawSurfaceExtent(base, device));
  const FableSurfaceExtent target_extent =
      CurrentFableDrawSurfaceExtent(base, device);
  if (raw_extent.width == 0 || raw_extent.height == 0 ||
      target_extent.width == 0 || target_extent.height == 0 ||
      (raw_extent.width == target_extent.width &&
       raw_extent.height == target_extent.height)) {
    return scissor;
  }

  const float scale_x =
      static_cast<float>(target_extent.width) / static_cast<float>(raw_extent.width);
  const float scale_y =
      static_cast<float>(target_extent.height) / static_cast<float>(raw_extent.height);
  if (!std::isfinite(scale_x) || !std::isfinite(scale_y) ||
      scale_x <= 0.0f || scale_y <= 0.0f) {
    return scissor;
  }

  scissor.left = static_cast<int32_t>(std::floor(
      static_cast<float>(scissor.left) * scale_x));
  scissor.top = static_cast<int32_t>(std::floor(
      static_cast<float>(scissor.top) * scale_y));
  scissor.right = static_cast<int32_t>(std::ceil(
      static_cast<float>(scissor.right) * scale_x));
  scissor.bottom = static_cast<int32_t>(std::ceil(
      static_cast<float>(scissor.bottom) * scale_y));
  scissor.valid = scissor.right > scissor.left &&
                  scissor.bottom > scissor.top &&
                  scissor.right <= 16384 && scissor.bottom <= 16384 &&
                  scissor.left > -16384 && scissor.top > -16384;
  if (!scissor.valid) {
    scissor = {};
  }
  return scissor;
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

  FableDirectSetScaler(args.r3, scaler);
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

FableSurfaceExtent DesiredFableNativeRenderExtent() {
  if (!PlumeRendererBackendEnabled()) {
    return {};
  }

  const uint32_t width = static_cast<uint32_t>(
      std::max(1, RendererOutputWidth()) *
      std::max(1, RendererInternalScale()));
  const uint32_t height = static_cast<uint32_t>(
      std::max(1, RendererOutputHeight()) *
      std::max(1, RendererInternalScale()));
  if (!IsPlausibleFableSurfaceExtent(width, height)) {
    return {};
  }
  return {.width = width, .height = height};
}

void PatchPresentationScalerParametersLocked(uint8_t* base,
                                             uint32_t parameter_pointer,
                                             FableSurfaceExtent extent) {
  if (!base || !IsLikelyGuestDwordAddress(parameter_pointer) ||
      extent.width == 0 || extent.height == 0) {
    return;
  }

  const uint32_t source_left = LoadGuestU32(base, parameter_pointer + 0u);
  const uint32_t source_top = LoadGuestU32(base, parameter_pointer + 4u);
  const uint32_t source_right = LoadGuestU32(base, parameter_pointer + 8u);
  const uint32_t source_bottom = LoadGuestU32(base, parameter_pointer + 12u);
  if (source_left == 0 && source_top == 0 &&
      (source_right == 0 || source_right == 1280u) &&
      (source_bottom == 0 || source_bottom == 720u)) {
    StoreGuestU32(base, parameter_pointer + 8u, extent.width);
    StoreGuestU32(base, parameter_pointer + 12u, extent.height);
  }

  const uint32_t output_width = LoadGuestU32(base, parameter_pointer + 16u);
  const uint32_t output_height = LoadGuestU32(base, parameter_pointer + 20u);
  if (output_width == 0 || output_width == 1280u) {
    StoreGuestU32(base, parameter_pointer + 16u, extent.width);
  }
  if (output_height == 0 || output_height == 720u) {
    StoreGuestU32(base, parameter_pointer + 20u, extent.height);
  }
}

void PatchFablePresentationParametersLocked(std::string_view symbol,
                                            const RendererHookArgs& args,
                                            uint8_t* base) {
  if (symbol != "sub_82BA6508" || !base ||
      !IsLikelyGuestDwordAddress(args.r4)) {
    return;
  }

  const FableSurfaceExtent extent = DesiredFableNativeRenderExtent();
  if (extent.width == 0 || extent.height == 0) {
    return;
  }

  const uint32_t old_width = LoadGuestU32(base, args.r4 + 0u);
  const uint32_t old_height = LoadGuestU32(base, args.r4 + 4u);
  static bool logged_native_resolution_observation = false;
  if (!logged_native_resolution_observation) {
    REXLOG_INFO(
        "Refii renderer observed Fable presentation parameters {}x{} "
        "requested {}x{}",
        old_width, old_height, extent.width, extent.height);
    logged_native_resolution_observation = true;
  }

  if (!IsPlausibleFableSurfaceExtent(old_width, old_height)) {
    return;
  }
  if (old_width == extent.width && old_height == extent.height) {
    g_observer.display_present_width = extent.width;
    g_observer.display_present_height = extent.height;
    return;
  }

  StoreGuestU32(base, args.r4 + 0u, extent.width);
  StoreGuestU32(base, args.r4 + 4u, extent.height);
  PatchPresentationScalerParametersLocked(base, args.r4 + 96u, extent);

  if (IsLikelyFableObjectAddress(args.r3)) {
    StoreGuestU32(base, args.r3 + kFableDeviceDefaultDisplayWidthOffset,
                  extent.width);
    StoreGuestU32(base, args.r3 + kFableDeviceDefaultDisplayHeightOffset,
                  extent.height);
    StoreGuestU32(base, args.r3 + kFableDeviceOutputWidthOffset,
                  extent.width);
    StoreGuestU32(base, args.r3 + kFableDeviceOutputHeightOffset,
                  extent.height);
  }

  g_observer.display_present_width = extent.width;
  g_observer.display_present_height = extent.height;

  static bool logged_native_resolution_patch = false;
  if (!logged_native_resolution_patch) {
    REXLOG_INFO(
        "Refii renderer patched Fable presentation parameters {}x{} -> {}x{}",
        old_width, old_height, extent.width, extent.height);
    logged_native_resolution_patch = true;
  }
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

void ApplyEffectiveViewportToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                               RendererViewportSnapshot viewport,
                                               FableDirectStateRoot root = {},
                                               bool record_action = true) {
  if (!viewport.valid) {
    return;
  }

  viewport = ScaleGuestViewportToCurrentDrawSurface(base, device, viewport);
  if (record_action) {
    FableDirectSetViewport(device, viewport, root);
  } else {
    FableDirectMirrorViewportState(device, viewport);
  }

  if (!ShouldMirrorSuppressedDeviceState(base, device)) {
    return;
  }

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
  StoreFableScissorToGuestDeviceLocked(base, device, viewport_scissor);
  if (record_action) {
    FableDirectSetScissor(device, viewport_scissor, root);
  } else {
    FableDirectMirrorScissorState(device, viewport_scissor);
  }
}

void ApplyDefaultViewportForBoundSurfaceLocked(uint8_t* base, uint32_t device,
                                               uint32_t surface,
                                               FableDirectStateRoot root,
                                               bool record_action) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) ||
      !IsLikelyFableObjectAddress(surface)) {
    return;
  }

  FableSurfaceExtent extent =
      NormalizeMainFableSurfaceExtentForScaling(
          DecodeFableSurfaceExtent(base, surface));
  if (extent.width == 0 || extent.height == 0) {
    return;
  }

  // Marathon resets the viewport to the newly bound surface. Feed the guest
  // surface extent through the normal scaler so 720p-era targets become the
  // requested native output size without hard-coding a replacement viewport.
  ApplyEffectiveViewportToGuestDeviceLocked(
      base, device,
      DecodeGuestViewport(0.0f, 0.0f, static_cast<float>(extent.width),
                          static_cast<float>(extent.height), 0.0f, 1.0f),
      root, record_action);
}

bool CurrentColorRenderTargetHasExtentLocked(uint8_t* base, uint32_t device) {
  if (!ShouldMirrorSuppressedDeviceState(base, device)) {
    return false;
  }

  const uint32_t color_surface =
      LoadGuestU32(base, device + kFableDeviceRenderTargetBaseOffset);
  const FableSurfaceExtent extent =
      DecodeFableSurfaceExtent(base, color_surface);
  return extent.width != 0 && extent.height != 0;
}

void MirrorViewportToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                       uint32_t viewport_pointer,
                                       FableDirectStateRoot root = {}) {
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
  ApplyEffectiveViewportToGuestDeviceLocked(base, device, viewport, root);
}

void MirrorScissorToGuestDeviceLocked(uint8_t* base, uint32_t device,
                                      uint32_t scissor_pointer,
                                      FableDirectStateRoot root = {}) {
  if (!ShouldMirrorSuppressedDeviceState(base, device) ||
      !IsLikelyGuestDwordAddress(scissor_pointer)) {
    return;
  }

  RendererScissorSnapshot scissor = DecodeGuestRect(base, scissor_pointer);
  if (!scissor.valid) {
    return;
  }
  scissor =
      ClampScissorToViewport(scissor, GetFableDirectStateSnapshot().viewport);
  scissor = ScaleGuestScissorToCurrentDrawSurface(base, device, scissor);
  StoreFableScissorToGuestDeviceLocked(base, device, scissor);
  FableDirectSetScissor(device, scissor, root);
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
                                          uint32_t depth_surface,
                                          FableDirectStateRoot root = {},
                                          bool record_action = true) {
  (void)base;
  if (record_action) {
    FableDirectSetDepthStencil(device, depth_surface, root);
  } else {
    FableDirectMirrorDepthStencilState(device, depth_surface, root);
  }
  if (depth_surface == 0) {
    if (!record_action) {
      return;
    }
    FableDirectSetRenderState(device, kFableRenderStateSlotZEnable, 0);
    FableDirectSetRenderState(device, kFableRenderStateSlotZWriteEnable, 0);
    FableDirectSetRenderState(device, kFableRenderStateSlotHiZWriteEnable, 0);
    return;
  }
}

void SetObservedDepthSurfaceLocked(uint8_t* base, uint32_t device,
                                   uint32_t depth_surface,
                                   uint32_t root_symbol_address = 0,
                                   uint32_t root_lr = 0,
                                   FableDirectStateRoot default_viewport_root =
                                       {},
                                   bool record_action = true) {
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
  const FableDirectStateRoot root =
      requested_depth_surface == depth_surface
          ? FableExactFramebufferStateRootLocked(root_symbol_address, root_lr,
                                                device, 4u, depth_surface)
          : FableDirectStateRoot{};
  ApplyEffectiveDepthRenderStateLocked(base, device, depth_surface, root,
                                       record_action);
  MirrorFableDepthSurfacePm4Locked(depth_surface, base, root, record_action);
  if (depth_surface != 0 &&
      !CurrentColorRenderTargetHasExtentLocked(base, device)) {
    const FableDirectStateRoot viewport_root =
        default_viewport_root.call_serial != 0 ? default_viewport_root : root;
    ApplyDefaultViewportForBoundSurfaceLocked(base, device, depth_surface,
                                              viewport_root, record_action);
  }
  FlushDeferredSurfaceReleasesLocked();
}

void RefreshBoundRenderTargetsLocked(
    uint8_t* base, uint32_t device,
    FableDirectStateRoot default_viewport_root = {},
    bool record_action = false) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  for (uint32_t slot = 0; slot < 4u; ++slot) {
    const uint32_t render_target =
        LoadGuestU32(base, device + kFableDeviceRenderTargetBaseOffset +
                               slot * sizeof(uint32_t));
    SetObservedRenderTargetLocked(slot, render_target, device, base, 0, 0,
                                  default_viewport_root, record_action);
  }
}

void RefreshEffectiveDepthRenderStateLocked(
    uint8_t* base, uint32_t device,
    FableDirectStateRoot default_viewport_root = {},
    bool record_action = false) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  SetObservedDepthSurfaceLocked(
      base, device, LoadGuestU32(base, device + kFableDeviceDepthStencilOffset),
      0, 0, default_viewport_root, record_action);
}

void RefreshBoundSurfacesLocked(uint8_t* base, uint32_t device,
                                FableDirectStateRoot default_viewport_root =
                                    {},
                                bool record_action = false) {
  RefreshBoundRenderTargetsLocked(base, device, default_viewport_root,
                                  record_action);
  RefreshEffectiveDepthRenderStateLocked(base, device, default_viewport_root,
                                         record_action);
}

void ObserveGetRenderTargetResultLocked(std::string_view symbol,
                                        const RendererHookArgs& args,
                                        uint32_t result, uint8_t* base) {
  if (symbol != "sub_82B981D0" && symbol != "D3DDevice_GetRenderTarget") {
    return;
  }

  ++g_observer.get_render_target_calls;
  g_observer.last_get_render_target_slot = args.r4;
  g_observer.last_get_render_target_surface = result;

  if (args.r4 >= 4u) {
    return;
  }

  // Lumen identifies this as D3DDevice_GetRenderTarget(device, index). IDA
  // shows it returns device + 0x3098 + index * 4 after AddRef, which is the
  // same state block SetRenderTarget mirrors. Treat the returned surface as a
  // readback of the authoritative bound target so Fable GetBackBuffer-like
  // paths do not fall out of Plume's surface model.
  SetObservedRenderTargetLocked(args.r4, result, args.r3, base, 0, 0, {},
                                false);
}

bool GuestDeviceStateReadableForDirectRender() {
  // Plume replaces Fable's D3D9 submission, but the guest device state remains
  // the canonical source for bound RTs, streams, declarations, and constants.
  return true;
}

void SanitizeBoundSurfaceSnapshotLocked(uint32_t* render_targets,
                                        size_t render_target_count,
                                        uint32_t& depth_stencil) {
  if (!render_targets || render_target_count == 0) {
    return;
  }

  const size_t count = std::min(render_target_count, size_t{4});
  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  std::copy_n(direct_snapshot.targets.color.begin(), count, render_targets);
  for (size_t i = count; i < render_target_count; ++i) {
    render_targets[i] = 0;
  }
  depth_stencil = direct_snapshot.targets.depth;

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
      FableDirectSetRenderTarget(0, static_cast<uint32_t>(i), 0);
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
    const GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
    if (kind == RendererResourceKind::kDepthSurface) {
      // Fable's depth surface headers carry the Xenos RB_DEPTH_INFO value in
      // the same +0x1C target-info word used by color surfaces. The +0x20 word
      // is still useful metadata, but it is not the PM4 identity for the target.
      return surface.render_target_info == rb_target_info ||
             surface.depth_stencil_info == rb_target_info;
    }
    return surface.render_target_info == rb_target_info;
  };

  uint32_t best_address = 0;
  uint64_t best_serial = 0;
  for (const auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    const GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
    if (GuestObjectKind(record) != kind ||
        surface.pitch_msaa != rb_surface_info) {
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

void ResolvePm4BoundSurfacesLocked(const RendererDrawEventSnapshot& event,
                                   uint32_t* render_targets,
                                   size_t render_target_count,
                                   uint32_t& depth_stencil) {
  if (!render_targets || !event.pm4_registers_valid) {
    return;
  }

  const size_t count =
      std::min(render_target_count, std::size(event.rb_color_info));
  for (size_t slot = 0; slot < count; ++slot) {
    if (render_targets[slot] != 0) {
      continue;
    }
    const uint32_t slot_write_mask =
        (event.rb_color_mask >> (slot * 4u)) & 0xFu;
    if (slot != 0 && slot_write_mask == 0) {
      continue;
    }
    if (slot != 0 && event.rb_color_info[slot] == 0) {
      continue;
    }
    const uint32_t resolved = FindSurfaceByRbInfoLocked(
        RendererResourceKind::kRenderTargetSurface, event.rb_surface_info,
        event.rb_color_info[slot]);
    if (resolved != 0) {
      render_targets[slot] = resolved;
    }
  }

  if (depth_stencil == 0) {
    depth_stencil = FindSurfaceByRbInfoLocked(
        RendererResourceKind::kDepthSurface, event.rb_surface_info,
        event.rb_depth_info);
  }
}

bool SnapshotDeviceFloatConstantsLocked(
    uint32_t device_pointer,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    uint8_t* base, uint32_t base_register, bool pixel_bank) {
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
      uint32_t value =
          LoadGuestU32(base, source_address + component * sizeof(uint32_t));
      const uint32_t dword_index = register_index * 4u + component;
      value = SanitizeFableShaderFloatConstantDword(pixel_bank, dword_index,
                                                    value);
      snapshot[dword_index] = value;
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
  return changed;
}

bool SnapshotDeviceVertexFloatConstantsLocked(
    uint32_t device_pointer,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    uint8_t* base) {
  constexpr uint32_t kDeviceVertexFloatConstantBaseRegister = 120;
  return SnapshotDeviceFloatConstantsLocked(
      device_pointer, constants, base, kDeviceVertexFloatConstantBaseRegister,
      false);
}

bool SnapshotDevicePixelFloatConstantsLocked(
    uint32_t device_pointer,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants,
    uint8_t* base) {
  constexpr uint32_t kDevicePixelFloatConstantBaseRegister = 376;
  return SnapshotDeviceFloatConstantsLocked(
      device_pointer, constants, base, kDevicePixelFloatConstantBaseRegister,
      true);
}

void RefreshDeviceInputBindingsLocked(uint32_t device_pointer, uint8_t* base,
                                      bool record_actions = false) {
  if (!GuestDeviceStateReadableForDirectRender()) {
    return;
  }
  if (!base || device_pointer == 0) {
    return;
  }

  const uint32_t declaration =
      LoadGuestU32(base, device_pointer + kFableDeviceVertexDeclarationOffset);
  if (declaration != 0) {
    if (record_actions) {
      FableDirectSetVertexDeclaration(device_pointer, declaration);
    } else {
      FableDirectMirrorVertexDeclarationState(device_pointer, declaration);
    }
    TrackVertexDeclarationLocked(declaration, device_pointer, base);
  }

  const uint32_t index_buffer =
      LoadGuestU32(base, device_pointer + kFableDeviceIndexBufferOffset);
  if (index_buffer != 0) {
    if (record_actions) {
      FableDirectSetIndexBuffer(device_pointer, index_buffer);
    } else {
      FableDirectMirrorIndexBufferState(device_pointer, index_buffer);
    }
  }

  const uint32_t pixel_shader =
      LoadGuestU32(base, device_pointer + kFableDevicePixelShaderOffset);
  const uint32_t vertex_shader =
      LoadGuestU32(base, device_pointer + kFableDeviceVertexShaderOffset);
  if (pixel_shader != 0) {
    if (record_actions) {
      FableDirectSetPixelShader(device_pointer, pixel_shader);
    } else {
      FableDirectMirrorPixelShaderState(device_pointer, pixel_shader);
    }
    TrackShaderObjectLocked(RendererResourceKind::kPixelShader, pixel_shader,
                            device_pointer, base);
  }
  if (vertex_shader != 0) {
    if (record_actions) {
      FableDirectSetVertexShader(device_pointer, vertex_shader);
    } else {
      FableDirectMirrorVertexShaderState(device_pointer, vertex_shader);
    }
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, vertex_shader,
                            device_pointer, base);
  }

  for (uint32_t slot = 0; slot < 16u; ++slot) {
    const uint32_t texture_object = LoadGuestU32(
        base, device_pointer + kFableDeviceTextureObjectBaseOffset +
                  slot * sizeof(uint32_t));
    if (texture_object != 0 && IsLikelyFableObjectAddress(texture_object)) {
      if (record_actions) {
        FableDirectSetTexture(device_pointer, slot, texture_object);
      } else {
        FableDirectMirrorTextureState(device_pointer, slot, texture_object);
      }
      StoreTextureObjectFetchDwordsLocked(
          texture_object, texture_object + 28u, slot, base, 0, false, false);
    } else if (texture_object == 0) {
      if (record_actions) {
        FableDirectSetTexture(device_pointer, slot, 0);
      } else {
        FableDirectMirrorTextureState(device_pointer, slot, 0);
      }
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

  for (uint32_t slot = 0; slot < 16u; ++slot) {
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

    RendererStreamBindingSnapshot stream = {
        .buffer = stream_buffer,
        .offset = 0,
        .stride = 0,
    };

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
    if (record_actions) {
      FableDirectSetStream(device_pointer, slot, stream);
    } else {
      FableDirectMirrorStreamState(device_pointer, slot, stream);
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
    if (resource_it == FableDirectGuestResourceRegistry().resources.end() ||
        !IsImageResourceKind(GuestObjectKind(resource_it->second))) {
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
      continue;
    }

    std::copy_n(dwords.begin(), kTextureFetchConstantDwords,
                event.fetch_constants.begin() + fetch_dword);
  }
}

void PatchDrawEventDeviceBindingsLocked(uint64_t draw_serial,
                                        std::string_view symbol,
                                        uint32_t device_pointer,
                                        uint8_t* base) {
  if (!GuestDeviceStateReadableForDirectRender()) {
    return;
  }
  if (draw_serial == 0) {
    return;
  }

  RefreshDeviceInputBindingsLocked(device_pointer, base);
  const FableDirectStateSnapshot direct_state = GetFableDirectStateSnapshot();

  const bool overwrite_root_draw_bindings =
      IsFableRootDrawSubmitHook(symbol);

  std::array<uint32_t, kRendererShaderFloatConstantDwords> vertex_constants = {};
  std::array<uint32_t, kRendererShaderFloatConstantDwords> pixel_constants = {};
  const bool has_vertex_constants =
      device_pointer != 0 &&
      SnapshotDeviceVertexFloatConstantsLocked(
          device_pointer, vertex_constants, base);
  const bool has_pixel_constants =
      device_pointer != 0 &&
      SnapshotDevicePixelFloatConstantsLocked(
          device_pointer, pixel_constants, base);
  if (has_vertex_constants) {
    FableDirectSetShaderFloatConstantsSnapshot(0, vertex_constants);
  }
  if (has_pixel_constants) {
    FableDirectSetShaderFloatConstantsSnapshot(1, pixel_constants);
  }

  bool had_index_buffer = false;
  bool had_vertex_buffer = false;
  bool has_vertex_buffer = false;
  bool has_index_buffer = false;
  bool indexed_draw = false;
  RendererLastDrawSnapshot draw = {};
  RendererStreamBindingSnapshot stream0 = {};
  RendererDrawEventSnapshot patched_event = {};
  uint32_t before_render_target0 = 0;
  uint32_t before_vertex_shader = 0;
  uint32_t before_pixel_shader = 0;
  uint32_t before_vertex_declaration = 0;
  uint32_t before_index_buffer = 0;
  RendererStreamBindingSnapshot before_stream0 = {};
  std::array<uint32_t, kRendererD3DTextureSlots> before_textures = {};
  uint32_t index_buffer = 0;
  uint32_t vertex_declaration = 0;
  bool used_overwrite_event_root_bindings = false;
  const bool patched = FableDirectPatchDrawEvent(
      draw_serial, [&](RendererDrawEventSnapshot& event) {
    if (event.clear) {
      return false;
    }
    if (event.source == RendererDrawEventSource::kPixVideo ||
        event.source == RendererDrawEventSource::kPrivateTextureRectangle ||
        event.private_texture_rectangle) {
      return false;
    }

	    had_index_buffer = event.index_buffer != 0;
	    for (const RendererStreamBindingSnapshot& stream : event.streams) {
	      had_vertex_buffer =
	          had_vertex_buffer || IsLikelyBindableBufferAddress(stream.buffer);
	    }
    before_render_target0 = event.render_targets[0];
    before_vertex_shader = event.vertex_shader;
    before_pixel_shader = event.pixel_shader;
    before_vertex_declaration = event.vertex_declaration;
    before_index_buffer = event.index_buffer;
    before_stream0 = event.streams[0];
    std::copy(std::begin(event.textures), std::end(event.textures),
              before_textures.begin());
    const bool preserve_existing_scene_streams =
        had_vertex_buffer &&
        (event.source == RendererDrawEventSource::kSceneObject ||
         event.source == RendererDrawEventSource::kScratchWrapper);
    const bool preserve_existing_scene_root_bindings =
        event.source == RendererDrawEventSource::kSceneObject &&
        before_vertex_declaration != 0 && before_vertex_shader != 0 &&
        before_pixel_shader != 0 && had_vertex_buffer &&
        (!event.draw.indexed || had_index_buffer);
    const bool overwrite_event_root_bindings =
        overwrite_root_draw_bindings && !preserve_existing_scene_root_bindings;
    used_overwrite_event_root_bindings = overwrite_event_root_bindings;

	    if ((overwrite_event_root_bindings || event.vertex_declaration == 0) &&
	        direct_state.shaders.vertex_declaration != 0) {
	      event.vertex_declaration = direct_state.shaders.vertex_declaration;
	    }
    if (((overwrite_event_root_bindings && !had_index_buffer) ||
         event.index_buffer == 0) &&
        event.draw.indexed && direct_state.input.index_buffer != 0) {
      event.index_buffer = direct_state.input.index_buffer;
    }
    if ((overwrite_event_root_bindings || event.vertex_shader == 0) &&
        direct_state.shaders.vertex_shader != 0) {
      event.vertex_shader = direct_state.shaders.vertex_shader;
    }
    if ((overwrite_event_root_bindings || event.pixel_shader == 0) &&
        direct_state.shaders.pixel_shader != 0) {
      event.pixel_shader = direct_state.shaders.pixel_shader;
    }
    for (uint32_t slot = 0; slot < std::size(event.textures); ++slot) {
      if ((overwrite_event_root_bindings || event.textures[slot] == 0) &&
          direct_state.textures.textures[slot] != 0) {
        event.textures[slot] = direct_state.textures.textures[slot];
      }
    }
    ApplyTextureObjectFetchConstantsToEventLocked(event);
    BindDrawEventStreamsFromVertexFetchConstantsLocked(event);
    if (has_vertex_constants) {
      event.vertex_shader_constants = vertex_constants;
    }
    if (has_pixel_constants) {
      event.pixel_shader_constants = pixel_constants;
    }
	    for (uint32_t slot = 0; slot < std::size(event.streams); ++slot) {
      const RendererStreamBindingSnapshot& source =
	          direct_state.input.streams[slot];
	      RendererStreamBindingSnapshot& target = event.streams[slot];
      if (!preserve_existing_scene_streams &&
	          (overwrite_event_root_bindings ||
	           !IsLikelyBindableBufferAddress(target.buffer)) &&
	          IsLikelyBindableBufferAddress(source.buffer)) {
	        target = source;
	      } else if (IsLikelyBindableBufferAddress(target.buffer) &&
                 target.stride == 0 &&
                 source.buffer == target.buffer && source.stride != 0) {
        target.stride = source.stride;
      }
    }
    ResolveDrawEventStreamStridesLocked(event);
    RefreshDrawEventResourceSerialFromBindingsLocked(event);

    for (const RendererStreamBindingSnapshot& stream : event.streams) {
      has_vertex_buffer =
          has_vertex_buffer || IsLikelyBindableBufferAddress(stream.buffer);
    }
    has_index_buffer = event.index_buffer != 0;
    indexed_draw = event.draw.indexed;
    draw = event.draw;
    stream0 = event.streams[0];
    index_buffer = event.index_buffer;
    vertex_declaration = event.vertex_declaration;
    patched_event = event;
    return true;
  });
  if (!patched) {
    return;
  }

  const bool interesting_title_binding_patch =
      patched_event.source == RendererDrawEventSource::kSceneObject &&
      (before_render_target0 == 0x4011F370u ||
       patched_event.render_targets[0] == 0x4011F370u ||
       before_vertex_shader == 0x4C27A660u ||
       patched_event.vertex_shader == 0x4C27A660u ||
       before_vertex_shader == 0x4C27B140u ||
       patched_event.vertex_shader == 0x4C27B140u ||
       before_vertex_shader == 0x4C2BF7F0u ||
       patched_event.vertex_shader == 0x4C2BF7F0u ||
       before_textures[13] == 0x4C132C70u ||
       patched_event.textures[13] == 0x4C132C70u ||
       before_textures[13] == 0x40845B50u ||
       patched_event.textures[13] == 0x40845B50u ||
       before_textures[13] == 0x40845D50u ||
       patched_event.textures[13] == 0x40845D50u ||
       before_textures[13] == 0x44127210u ||
       patched_event.textures[13] == 0x44127210u ||
       before_textures[0] != patched_event.textures[0] ||
       before_textures[13] != patched_event.textures[13]);
  const bool title_binding_texture_mutated =
      !std::equal(before_textures.begin(), before_textures.end(),
                  std::begin(patched_event.textures));
  const bool title_binding_mutated =
      before_render_target0 != patched_event.render_targets[0] ||
      before_vertex_shader != patched_event.vertex_shader ||
      before_pixel_shader != patched_event.pixel_shader ||
      before_vertex_declaration != patched_event.vertex_declaration ||
      before_stream0.buffer != patched_event.streams[0].buffer ||
      before_stream0.stride != patched_event.streams[0].stride ||
      before_index_buffer != patched_event.index_buffer ||
      title_binding_texture_mutated;
  if (interesting_title_binding_patch &&
      g_observer.logged_title_draw_binding_checks < 24u) {
    if (title_binding_mutated) {
      REXLOG_INFO(
          "Refii renderer title draw binding mutation #{} hook={} serial={} "
          "overwrite={} src={} rt=0x{:08X}->0x{:08X} "
          "vs=0x{:08X}->0x{:08X} ps=0x{:08X}->0x{:08X} "
          "decl=0x{:08X}->0x{:08X} vb0=0x{:08X}/{}->0x{:08X}/{} "
          "ib=0x{:08X}->0x{:08X} "
          "tex0=0x{:08X}->0x{:08X} tex1=0x{:08X}->0x{:08X} "
          "tex2=0x{:08X}->0x{:08X} tex13=0x{:08X}->0x{:08X} "
          "ds_tex0=0x{:08X} ds_tex1=0x{:08X} ds_tex2=0x{:08X} "
          "ds_tex13=0x{:08X} prim={} count={} indexed={}",
          g_observer.logged_title_draw_binding_checks + 1u, symbol,
          draw_serial, used_overwrite_event_root_bindings ? 1 : 0,
          static_cast<uint32_t>(patched_event.source), before_render_target0,
          patched_event.render_targets[0], before_vertex_shader,
          patched_event.vertex_shader, before_pixel_shader,
          patched_event.pixel_shader, before_vertex_declaration,
          patched_event.vertex_declaration, before_stream0.buffer,
          before_stream0.stride, patched_event.streams[0].buffer,
          patched_event.streams[0].stride, before_index_buffer,
          patched_event.index_buffer, before_textures[0],
          patched_event.textures[0], before_textures[1],
          patched_event.textures[1], before_textures[2],
          patched_event.textures[2], before_textures[13],
          patched_event.textures[13], direct_state.textures.textures[0],
          direct_state.textures.textures[1], direct_state.textures.textures[2],
          direct_state.textures.textures[13], patched_event.draw.primitive_type,
          patched_event.draw.primitive_count,
          patched_event.draw.indexed ? 1 : 0);
    } else {
      REXLOG_INFO(
          "Refii renderer verified exact title draw bindings #{} hook={} "
          "serial={} overwrite={} src={} rt=0x{:08X} vs=0x{:08X} "
          "ps=0x{:08X} decl=0x{:08X} vb0=0x{:08X}/{} ib=0x{:08X} "
          "tex0=0x{:08X} tex1=0x{:08X} tex2=0x{:08X} tex13=0x{:08X} "
          "ds_tex0=0x{:08X} ds_tex1=0x{:08X} ds_tex2=0x{:08X} "
          "ds_tex13=0x{:08X} prim={} count={} indexed={}",
          g_observer.logged_title_draw_binding_checks + 1u, symbol,
          draw_serial, used_overwrite_event_root_bindings ? 1 : 0,
          static_cast<uint32_t>(patched_event.source),
          patched_event.render_targets[0], patched_event.vertex_shader,
          patched_event.pixel_shader, patched_event.vertex_declaration,
          patched_event.streams[0].buffer, patched_event.streams[0].stride,
          patched_event.index_buffer, patched_event.textures[0],
          patched_event.textures[1], patched_event.textures[2],
          patched_event.textures[13], direct_state.textures.textures[0],
          direct_state.textures.textures[1], direct_state.textures.textures[2],
          direct_state.textures.textures[13], patched_event.draw.primitive_type,
          patched_event.draw.primitive_count,
          patched_event.draw.indexed ? 1 : 0);
    }
    ++g_observer.logged_title_draw_binding_checks;
  }

  std::vector<ScratchBufferAttachment> expanded_scratch_attachments =
      ExpandIndexedScratchStreamsToIndexArenaLocked(patched_event);
  std::vector<ScratchBufferAttachment> nonindexed_scratch_attachments =
      EnsureNonIndexedScratchStreamsForDrawLocked(patched_event);
  expanded_scratch_attachments.insert(
      expanded_scratch_attachments.end(),
      std::make_move_iterator(nonindexed_scratch_attachments.begin()),
      std::make_move_iterator(nonindexed_scratch_attachments.end()));
  for (const ScratchBufferAttachment& attachment :
       expanded_scratch_attachments) {
    const bool attached = FableDirectAttachDrawScratchBuffer(
        draw_serial, attachment.index_buffer, attachment.address,
        attachment.stride_or_format, attachment.data.data(),
        attachment.data.size());
    if (patched_event.source == RendererDrawEventSource::kSceneObject &&
        patched_event.draw.indexed &&
        g_observer.logged_scratch_attach_samples < 24u) {
      const uint32_t first_nonzero = FirstNonZeroByteOffset(attachment.data);
      REXLOG_INFO(
          "Refii renderer patched draw scratch attachment #{} serial={} "
          "attached={} kind={} addr=0x{:08X} stride_fmt={} bytes={} "
          "first_nz={} vb0=0x{:08X}/{} ib=0x{:08X} prim={} count={}",
          g_observer.logged_scratch_attach_samples + 1u, draw_serial,
          attached ? 1 : 0, attachment.index_buffer ? "ib" : "vb",
          attachment.address, attachment.stride_or_format, attachment.data.size(),
          first_nonzero == UINT32_MAX ? -1 : static_cast<int>(first_nonzero),
          patched_event.streams[0].buffer, patched_event.streams[0].stride,
          patched_event.index_buffer, patched_event.draw.primitive_type,
          patched_event.draw.primitive_count);
      ++g_observer.logged_scratch_attach_samples;
    }
  }

  if ((!had_vertex_buffer && has_vertex_buffer) ||
      (!had_index_buffer && has_index_buffer)) {
    if (g_observer.logged_post_flush_draw_binding_mutations < 8) {
      REXLOG_INFO(
          "Refii renderer mutated post-flush Fable draw bindings "
          "device=0x{:08X} vb0=0x{:08X}/{} ib=0x{:08X} decl=0x{:08X}",
          device_pointer, stream0.buffer, stream0.stride,
          index_buffer, vertex_declaration);
      ++g_observer.logged_post_flush_draw_binding_mutations;
    }
  } else if ((!has_vertex_buffer ||
              (indexed_draw && !has_index_buffer)) &&
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
        index_object, indexed_draw ? 1 : 0, draw.primitive_type,
        draw.base_vertex, draw.start, draw.primitive_count);
    ++g_observer.logged_missing_draw_bindings;
  }
}

void ResolveDrawEventTargetsLocked(RendererDrawEventSnapshot& event) {
  uint32_t depth_stencil = 0;
  SanitizeBoundSurfaceSnapshotLocked(event.render_targets,
                                     std::size(event.render_targets),
                                     depth_stencil);
  ResolvePm4BoundSurfacesLocked(event, event.render_targets,
                                std::size(event.render_targets),
                                depth_stencil);
  event.depth_stencil = depth_stencil;
}

void ApplyDeviceRegistersToDrawEventLocked(RendererDrawEventSnapshot& event,
                                           uint32_t device_pointer) {
  if (!GuestDeviceStateReadableForDirectRender() || device_pointer == 0 ||
      !g_observer.memory_base) {
    return;
  }

  event.device_registers_valid = true;
  const uint32_t device_colorcontrol = LoadGuestU32(
      g_observer.memory_base, device_pointer + kFableDeviceColorControlOffset);
  const uint32_t device_alpha_ref = LoadGuestU32(
      g_observer.memory_base, device_pointer + kFableDeviceAlphaRefOffset);
  event.rb_depthcontrol = LoadGuestU32(
      g_observer.memory_base, device_pointer + kFableDeviceDepthControlOffset);
  if (!event.pm4_registers_valid) {
    event.rb_colorcontrol = device_colorcontrol;
    event.rb_alpha_ref = device_alpha_ref;
  }
  event.rb_stencil_ref = LoadGuestU8(
      g_observer.memory_base, device_pointer + kFableDeviceStencilRefOffset);
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

void RefreshDrawEventShaderFloatConstantsLocked(
    RendererDrawEventSnapshot& event, uint32_t device_pointer) {
  if (!GuestDeviceStateReadableForDirectRender() || device_pointer == 0) {
    return;
  }
  if (!g_observer.memory_base) {
    return;
  }
  if (SnapshotDeviceVertexFloatConstantsLocked(
          device_pointer, event.vertex_shader_constants,
          g_observer.memory_base)) {
    FableDirectSetShaderFloatConstantsSnapshot(
        0, event.vertex_shader_constants);
  }
  if (SnapshotDevicePixelFloatConstantsLocked(
          device_pointer, event.pixel_shader_constants, g_observer.memory_base)) {
    FableDirectSetShaderFloatConstantsSnapshot(1, event.pixel_shader_constants);
  }
}

void StampObserverDrawEventMetadataLocked(RendererDrawEventSnapshot& event,
                                          uint32_t state_flush_lr =
                                              UINT32_MAX) {
  event.viewport_pointer = g_observer.viewport_pointer;
  event.scissor_pointer = g_observer.scissor_pointer;
  event.state_flush_lr = state_flush_lr != UINT32_MAX
                             ? state_flush_lr
                             : g_observer.last_state_flush_lr;
  event.resource_serial = std::max(event.resource_serial, g_observer.serial);
}

std::vector<ScratchBufferAttachment> ExpandIndexedScratchStreamsToIndexArenaLocked(
    RendererDrawEventSnapshot& event) {
  std::vector<ScratchBufferAttachment> attachments;
  if (!event.draw.indexed || event.index_buffer == 0 ||
      g_observer.memory_base == nullptr) {
    if (event.source == RendererDrawEventSource::kSceneObject &&
        event.draw.indexed && g_observer.logged_scene_indexed_no_expansions < 24u) {
      REXLOG_INFO(
          "Refii renderer scene indexed scratch expansion skipped #{} "
          "reason={} serial={} ib=0x{:08X} mem={} vb0=0x{:08X}/{} "
          "prim={} count={} start={}",
          g_observer.logged_scene_indexed_no_expansions + 1u,
          event.index_buffer == 0 ? "no_index_buffer" : "no_memory",
          event.serial, event.index_buffer,
          g_observer.memory_base != nullptr ? 1 : 0, event.streams[0].buffer,
          event.streams[0].stride, event.draw.primitive_type,
          event.draw.primitive_count, event.draw.start);
      ++g_observer.logged_scene_indexed_no_expansions;
    }
    return attachments;
  }

  RendererResourceRecord* index_record = FindResourceRecordLocked(event.index_buffer);
  const GuestBufferRecord index_buffer =
      index_record != nullptr ? GuestBufferRecordFor(*index_record)
                              : GuestBufferRecord{};
  uint32_t index_stride = index_buffer.stride_or_index_format;
  if (index_stride != 2u && index_stride != 4u) {
    index_stride = 2u;
  }
  const uint32_t index_count =
      DrawVertexOrIndexCount(event.draw.primitive_type,
                             event.draw.primitive_count);
  const uint64_t index_end =
      (uint64_t(event.draw.start) + uint64_t(index_count)) *
      uint64_t(index_stride);
  if (index_count != 0 && index_end <= kMaxIndexedScratchStreamPairSpan &&
      index_end <= std::numeric_limits<uint32_t>::max() &&
      IsLikelyScratchBufferAddress(event.index_buffer)) {
    const uint32_t required_index_size = static_cast<uint32_t>(index_end);
    RendererResourceRecord* index_resource =
        FindResourceRecordLocked(event.index_buffer);
    const GuestBufferRecord existing_index_buffer =
        index_resource != nullptr ? GuestBufferRecordFor(*index_resource)
                                  : GuestBufferRecord{};
    const bool index_ready =
        index_resource != nullptr &&
        existing_index_buffer.byte_size >= required_index_size &&
        index_resource->upload_data.size() >= required_index_size;
    if (!index_ready) {
      UpsertScratchBufferResourceLocked(RendererResourceKind::kIndexBuffer,
                                        event.index_buffer, required_index_size,
                                        index_stride, 0, event.index_buffer);
      index_resource = FindResourceRecordLocked(event.index_buffer);
      RefreshDrawEventResourceSerialFromBindingsLocked(event);
    }
    if (index_resource != nullptr &&
        index_resource->upload_data.size() >= required_index_size) {
	      ScratchBufferAttachment& attachment = attachments.emplace_back();
	      attachment.index_buffer = true;
	      attachment.address = event.index_buffer;
	      attachment.stride_or_format = index_stride;
	      AssignScratchAttachmentPrefix(attachment, index_resource->upload_data,
	                                    required_index_size);
    }
  }
  bool index_range_readable =
      index_count != 0 && index_end <= std::numeric_limits<uint32_t>::max() &&
      (IsLikelyGpuByteBufferRange(event.index_buffer,
                                  static_cast<uint32_t>(index_end)) ||
       IsLikelyHighScratchByteBufferRange(event.index_buffer,
                                          static_cast<uint32_t>(index_end)));
  if (index_buffer.byte_size != 0 && index_end > index_buffer.byte_size) {
    index_range_readable = false;
  }

  uint32_t max_index = 0;
  bool index_range_uses_swapped_bytes = false;
  if (index_range_readable) {
    uint32_t little_endian_max_index = 0;
    uint32_t swapped_max_index = 0;
    for (uint32_t i = 0; i < index_count; ++i) {
      const uint32_t index_address =
          event.index_buffer +
          static_cast<uint32_t>((uint64_t(event.draw.start) + i) *
                                uint64_t(index_stride));
      little_endian_max_index = std::max(
          little_endian_max_index,
          LoadGuestGpuIndex(g_observer.memory_base, index_address, index_stride,
                            false));
      swapped_max_index = std::max(
          swapped_max_index,
          LoadGuestGpuIndex(g_observer.memory_base, index_address, index_stride,
                            true));
    }
    const uint32_t vertex_count_hint = index_count;
    const bool little_endian_plausible =
        vertex_count_hint == 0 || little_endian_max_index < vertex_count_hint;
    const bool swapped_plausible =
        vertex_count_hint == 0 || swapped_max_index < vertex_count_hint;
    if (!little_endian_plausible && swapped_plausible) {
      max_index = swapped_max_index;
      index_range_uses_swapped_bytes = true;
    } else {
      max_index = little_endian_max_index;
    }
  }

  for (uint32_t slot = 0; slot < std::size(event.streams); ++slot) {
    RendererStreamBindingSnapshot& stream = event.streams[slot];
    if (stream.buffer != 0 && stream.stride == 0) {
      stream.stride = FableVertexDeclarationSlotStrideLocked(
          event.vertex_declaration, slot);
    }
    if (stream.buffer == 0 || stream.stride == 0 ||
        !IsLikelyScratchBufferAddress(stream.buffer) ||
        !IsLikelyScratchBufferAddress(event.index_buffer)) {
      continue;
    }

    uint32_t required_size = 0;
    if (index_range_readable) {
      const int64_t base_vertex =
          static_cast<int64_t>(static_cast<int32_t>(event.draw.base_vertex));
      const int64_t max_vertex = base_vertex + int64_t(max_index);
      if (max_vertex >= 0) {
        const uint64_t required_from_indices =
            uint64_t(stream.offset) +
            (uint64_t(max_vertex) + 1u) * uint64_t(stream.stride);
        if (required_from_indices <= std::numeric_limits<uint32_t>::max() &&
            required_from_indices <= kMaxIndexedScratchStreamPairSpan) {
          required_size = static_cast<uint32_t>(required_from_indices);
        }
      }
    }
    const int64_t base_vertex =
        static_cast<int64_t>(static_cast<int32_t>(event.draw.base_vertex));
    const uint64_t fallback_first_vertex =
        base_vertex > 0 ? static_cast<uint64_t>(base_vertex) : 0u;
    const uint64_t fallback_size =
        uint64_t(stream.offset) +
        (fallback_first_vertex + uint64_t(index_count)) *
            uint64_t(stream.stride);
    if (fallback_size <= std::numeric_limits<uint32_t>::max() &&
        fallback_size <= kMaxIndexedScratchStreamPairSpan) {
      // The CPU fills BeginIndexedVertices scratch buffers after the helper
      // returns. If we inspect the index buffer too early, max_index can still
      // be zero and would collapse the vertex upload to one vertex. Keep the
      // draw-count span as a floor so later index/sequential repair has the
      // full transient vertex payload available.
      required_size =
          std::max(required_size, static_cast<uint32_t>(fallback_size));
    }
    if (event.index_buffer > stream.buffer) {
      const uint32_t arena_vertex_size = event.index_buffer - stream.buffer;
      if (required_size == 0 || required_size > arena_vertex_size) {
        required_size = arena_vertex_size;
      }
    }

    if (required_size == 0 || required_size > kMaxIndexedScratchStreamPairSpan ||
        required_size < stream.stride) {
      continue;
    }

    auto exact_record_it =
        FableDirectGuestResourceRegistry().resources.find(stream.buffer);
    RendererResourceRecord* exact_record =
        exact_record_it != FableDirectGuestResourceRegistry().resources.end()
            ? &exact_record_it->second
            : nullptr;
    RendererResourceRecord* record =
        exact_record != nullptr ? exact_record
                                : FindResourceRecordLocked(stream.buffer);
    const GuestBufferRecord existing_buffer =
        exact_record != nullptr ? GuestBufferRecordFor(*exact_record)
                                : GuestBufferRecord{};
    if (existing_buffer.byte_size >= required_size && record != nullptr &&
        record->upload_data.size() >= required_size) {
	        ScratchBufferAttachment& attachment = attachments.emplace_back();
	        attachment.index_buffer = false;
	        attachment.address = stream.buffer;
	        attachment.stride_or_format = stream.stride;
	        AssignScratchAttachmentPrefix(attachment, record->upload_data,
	                                      required_size);
	      continue;
	    }

    const uint32_t upload_source =
        exact_record != nullptr &&
                exact_record->scratch_upload_source_address != 0
            ? exact_record->scratch_upload_source_address
            : stream.buffer;
    UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                      stream.buffer, required_size,
                                      stream.stride, 0, upload_source);
    auto expanded_record_it =
        FableDirectGuestResourceRegistry().resources.find(stream.buffer);
    const bool expanded_found =
        expanded_record_it != FableDirectGuestResourceRegistry().resources.end();
    if (RendererResourceRecord* expanded_record =
        expanded_found ? &expanded_record_it->second : nullptr;
        expanded_record != nullptr &&
        expanded_record->upload_data.size() >= required_size) {
	      ScratchBufferAttachment& attachment = attachments.emplace_back();
	      attachment.index_buffer = false;
	      attachment.address = stream.buffer;
	      attachment.stride_or_format = stream.stride;
	      AssignScratchAttachmentPrefix(attachment, expanded_record->upload_data,
	                                    required_size);
    }
    RefreshDrawEventResourceSerialFromBindingsLocked(event);
    if (RendererDrawTracingEnabled() &&
        g_observer.logged_scratch_pair_expansions < 8) {
      REXLOG_INFO(
          "Refii renderer expanded indexed scratch stream #{} "
          "vb=0x{:08X} old_size={} new_size={} stride={} ib=0x{:08X} "
          "source=0x{:08X} draw_count={} index_stride={} max_index={} "
          "range_read={} idx_swap={}",
          g_observer.logged_scratch_pair_expansions + 1u, stream.buffer,
          existing_buffer.byte_size, required_size, stream.stride,
          event.index_buffer, upload_source, event.draw.primitive_count,
          index_stride, max_index, index_range_readable ? 1 : 0,
          index_range_uses_swapped_bytes ? 1 : 0);
      ++g_observer.logged_scratch_pair_expansions;
    }
  }
  const bool log_scene_no_expansion =
      event.source == RendererDrawEventSource::kSceneObject &&
      g_observer.logged_scene_indexed_no_expansions < 24u;
  if (attachments.empty() &&
      ((RendererDrawTracingEnabled() &&
        g_observer.logged_scratch_pair_no_expansions < 16) ||
       log_scene_no_expansion) &&
      event.draw.primitive_type == 4u && index_count >= 6u &&
      IsLikelyScratchBufferAddress(event.index_buffer)) {
    const RendererStreamBindingSnapshot& stream0 = event.streams[0];
    const RendererResourceRecord* stream_record =
        FindResourceRecordLocked(stream0.buffer);
    const GuestBufferRecord stream_buffer =
        stream_record != nullptr ? GuestBufferRecordFor(*stream_record)
                                 : GuestBufferRecord{};
    uint32_t required_size = 0;
    if (stream0.stride != 0) {
      if (index_range_readable) {
        const int64_t base_vertex =
            static_cast<int64_t>(static_cast<int32_t>(event.draw.base_vertex));
        const int64_t max_vertex = base_vertex + int64_t(max_index);
        if (max_vertex >= 0) {
          const uint64_t required_from_indices =
              uint64_t(stream0.offset) +
              (uint64_t(max_vertex) + 1u) * uint64_t(stream0.stride);
          if (required_from_indices <= std::numeric_limits<uint32_t>::max()) {
            required_size = static_cast<uint32_t>(required_from_indices);
          }
        }
      } else if (event.index_buffer > stream0.buffer) {
        required_size = event.index_buffer - stream0.buffer;
      }
    }
    REXLOG_INFO(
        "Refii renderer indexed scratch stream no expansion #{} "
        "prim={} draw_count={} index_count={} start={} base={} "
        "vb0=0x{:08X}+{} stride={} vb0_size={} vb0_scratch={} "
        "vb0_source=0x{:08X} ib=0x{:08X} ib_scratch={} "
        "idx_stride={} idx_end={} idx_read={} max_index={} required={} "
        "span_ok={} existing_ok={}",
        log_scene_no_expansion
            ? g_observer.logged_scene_indexed_no_expansions + 1u
            : g_observer.logged_scratch_pair_no_expansions + 1u,
        event.draw.primitive_type, event.draw.primitive_count, index_count,
        event.draw.start, event.draw.base_vertex, stream0.buffer,
        stream0.offset, stream0.stride, stream_buffer.byte_size,
        IsLikelyScratchBufferAddress(stream0.buffer) ? 1 : 0,
        stream_record != nullptr ? stream_record->scratch_upload_source_address
                                 : 0,
        event.index_buffer, IsLikelyScratchBufferAddress(event.index_buffer) ? 1
                                                                            : 0,
        index_stride, static_cast<unsigned long long>(index_end),
        index_range_readable ? 1 : 0, max_index, required_size,
        required_size != 0 && required_size <= kMaxIndexedScratchStreamPairSpan &&
                required_size >= stream0.stride
            ? 1
            : 0,
        stream_buffer.byte_size >= required_size && required_size != 0 ? 1 : 0);
    if (log_scene_no_expansion) {
      ++g_observer.logged_scene_indexed_no_expansions;
    } else {
      ++g_observer.logged_scratch_pair_no_expansions;
    }
  }
  return attachments;
}

std::vector<ScratchBufferAttachment>
EnsureNonIndexedScratchStreamsForDrawLocked(RendererDrawEventSnapshot& event) {
  std::vector<ScratchBufferAttachment> attachments;
  const bool indexed_without_index_buffer =
      event.draw.indexed && event.index_buffer == 0;
  if (event.draw.indexed || g_observer.memory_base == nullptr) {
    return attachments;
  }

  const uint32_t vertex_count =
      DrawVertexOrIndexCount(event.draw.primitive_type,
                             event.draw.primitive_count);
  if (vertex_count == 0) {
    return attachments;
  }

  for (uint32_t slot = 0; slot < std::size(event.streams); ++slot) {
    RendererStreamBindingSnapshot& stream = event.streams[slot];
    if (stream.buffer != 0 && stream.stride == 0) {
      stream.stride =
          FableVertexDeclarationSlotStrideLocked(event.vertex_declaration, slot);
    }
    if (indexed_without_index_buffer && stream.stride > 20u) {
      continue;
    }
    if (stream.buffer == 0 || stream.stride == 0 ||
        !IsLikelyScratchBufferAddress(stream.buffer)) {
      continue;
    }

    const uint64_t first_vertex = event.draw.start;
    const uint64_t required_size64 =
        uint64_t(stream.offset) +
        (first_vertex + uint64_t(vertex_count)) * uint64_t(stream.stride);
    if (required_size64 == 0 ||
        required_size64 > kMaxIndexedScratchStreamPairSpan ||
        required_size64 > std::numeric_limits<uint32_t>::max()) {
      continue;
    }
    const uint32_t required_size = static_cast<uint32_t>(required_size64);

    auto exact_record_it =
        FableDirectGuestResourceRegistry().resources.find(stream.buffer);
    RendererResourceRecord* exact_record =
        exact_record_it != FableDirectGuestResourceRegistry().resources.end()
            ? &exact_record_it->second
            : nullptr;
    RendererResourceRecord* record =
        exact_record != nullptr ? exact_record
                                : FindResourceRecordLocked(stream.buffer);
    const GuestBufferRecord existing_buffer =
        exact_record != nullptr ? GuestBufferRecordFor(*exact_record)
                                : GuestBufferRecord{};
    const bool existing_ready =
        existing_buffer.byte_size >= required_size && record != nullptr &&
        record->upload_data.size() >= required_size;
    if (!existing_ready) {
      const uint32_t upload_source =
          exact_record != nullptr &&
                  exact_record->scratch_upload_source_address != 0
              ? exact_record->scratch_upload_source_address
              : stream.buffer;
      UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                        stream.buffer, required_size,
                                        stream.stride, 0, upload_source);
      exact_record_it =
          FableDirectGuestResourceRegistry().resources.find(stream.buffer);
      exact_record =
          exact_record_it != FableDirectGuestResourceRegistry().resources.end()
              ? &exact_record_it->second
              : nullptr;
      record = exact_record != nullptr ? exact_record
                                       : FindResourceRecordLocked(stream.buffer);
      RefreshDrawEventResourceSerialFromBindingsLocked(event);
      if (RendererDrawTracingEnabled() &&
          g_observer.logged_nonindexed_scratch_expansions < 16u) {
        const uint32_t first_nonzero =
            FindFirstNonZeroGuestByte(g_observer.memory_base, stream.buffer,
                                      required_size,
                                      std::min<uint32_t>(required_size, 4096u));
        REXLOG_INFO(
            "Refii renderer captured {} scratch stream #{} "
            "prim={} count={} start={} vb{}=0x{:08X}+{} stride={} "
            "old_size={} new_size={} first_nz={}",
            indexed_without_index_buffer ? "indexed-missing-ib"
                                         : "non-indexed",
            g_observer.logged_nonindexed_scratch_expansions + 1u,
            event.draw.primitive_type, event.draw.primitive_count,
            event.draw.start, slot, stream.buffer, stream.offset,
            stream.stride, existing_buffer.byte_size, required_size,
            first_nonzero == UINT32_MAX ? -1
                                        : static_cast<int>(first_nonzero));
        ++g_observer.logged_nonindexed_scratch_expansions;
      }
    }

    if (record == nullptr || record->upload_data.empty()) {
      continue;
    }
	    ScratchBufferAttachment& attachment = attachments.emplace_back();
	    attachment.index_buffer = false;
	    attachment.address = stream.buffer;
	    attachment.stride_or_format = stream.stride;
	    AssignScratchAttachmentPrefix(attachment, record->upload_data,
	                                  required_size);
	  }

  return attachments;
}

RendererDrawEventSnapshot BuildDrawEventLocked(
    uint32_t device_pointer, bool draw_vertices_up,
    bool refresh_device_bindings, RendererDrawEventSource source,
    const RendererLastDrawSnapshot& draw) {
  if (refresh_device_bindings) {
    RefreshDeviceInputBindingsLocked(device_pointer, g_observer.memory_base);
  }

  RendererDrawEventSnapshot event =
      FableDirectBuildDrawEvent(source, draw, draw_vertices_up);
  ResolveDrawEventTargetsLocked(event);
  ApplyDeviceRegistersToDrawEventLocked(event, device_pointer);
  if (event.device_registers_valid) {
    SyncDrawEventRenderStatesFromDepthControl(event);
  }
  ApplyTextureObjectFetchConstantsToEventLocked(event);
  BindDrawEventStreamsFromVertexFetchConstantsLocked(event);
  RefreshDrawEventShaderFloatConstantsLocked(event, device_pointer);
  StampObserverDrawEventMetadataLocked(event);
  ResolveDrawEventStreamStridesLocked(event);
  return event;
}

RendererDrawEventSnapshot RecordDrawEventLocked(
    RendererDrawEventSnapshot event) {
  ResolveDrawEventStreamStridesLocked(event);
  std::vector<ScratchBufferAttachment> expanded_scratch_attachments =
      ExpandIndexedScratchStreamsToIndexArenaLocked(event);
  std::vector<ScratchBufferAttachment> nonindexed_scratch_attachments =
      EnsureNonIndexedScratchStreamsForDrawLocked(event);
  event = FableDirectRecordDrawEvent(std::move(event));
  for (const ScratchBufferAttachment& attachment :
       expanded_scratch_attachments) {
    const bool attached = FableDirectAttachDrawScratchBuffer(
        event.serial, attachment.index_buffer, attachment.address,
        attachment.stride_or_format, attachment.data.data(),
        attachment.data.size());
    if (event.source == RendererDrawEventSource::kSceneObject &&
        event.draw.indexed &&
        (g_observer.logged_scratch_attach_samples < 24u ||
         (!attached && g_observer.logged_scratch_attach_failures < 24u))) {
      const uint32_t first_nonzero = FirstNonZeroByteOffset(attachment.data);
      REXLOG_INFO(
          "Refii renderer scratch draw attachment #{} serial={} attached={} "
          "kind={} addr=0x{:08X} stride_fmt={} bytes={} first_nz={} "
          "vb0=0x{:08X}/{} ib=0x{:08X} prim={} count={} start={}",
          g_observer.logged_scratch_attach_samples + 1u, event.serial,
          attached ? 1 : 0, attachment.index_buffer ? "ib" : "vb",
          attachment.address, attachment.stride_or_format, attachment.data.size(),
          first_nonzero == UINT32_MAX ? -1 : static_cast<int>(first_nonzero),
          event.streams[0].buffer, event.streams[0].stride, event.index_buffer,
          event.draw.primitive_type, event.draw.primitive_count,
          event.draw.start);
      ++g_observer.logged_scratch_attach_samples;
      if (!attached) {
        ++g_observer.logged_scratch_attach_failures;
      }
    }
  }
  for (const ScratchBufferAttachment& attachment :
       nonindexed_scratch_attachments) {
    FableDirectAttachDrawScratchBuffer(
        event.serial, attachment.index_buffer, attachment.address,
        attachment.stride_or_format, attachment.data.data(),
        attachment.data.size());
  }
  g_observer.last_queued_draw_serial = event.serial;
  g_observer.last_queued_draw_source = event.source;
  g_observer.last_queued_draw_render_target = event.render_targets[0];
  return event;
}

bool IsFableSceneSubmitScope(std::string_view symbol) {
  return symbol == "sub_821C3C60" || symbol == "sub_8226E738" ||
         symbol == "sub_8223CFA0" || symbol == "sub_822A8F68";
}

void ObserveFableSceneWalkerEnterLocked(std::string_view symbol,
                                        const RendererHookArgs& args,
                                        uint8_t* base) {
  if (symbol == "sub_822A8F68") {
    ++g_observer.scene_walker_flush_calls;
  } else if (symbol != "sub_8223CFA0") {
    return;
  }

  ++g_observer.scene_walker_calls;
  g_observer.last_scene_walker_list = args.r3;
  g_observer.last_scene_walker_lr = args.lr;

  uint32_t begin = 0;
  uint32_t end = 0;
  uint32_t count = 0;
  uint32_t first = 0;
  if (base != nullptr && IsLikelyGuestDwordAddress(args.r3 + 32u)) {
    begin = LoadGuestU32(base, args.r3 + 28u);
    end = LoadGuestU32(base, args.r3 + 32u);
    if (end >= begin && ((end - begin) & 3u) == 0) {
      count = (end - begin) / 4u;
      if (count != 0 && IsLikelyGuestDwordAddress(begin)) {
        first = LoadGuestU32(base, begin);
      }
    }
  }

  g_observer.last_scene_walker_begin = begin;
  g_observer.last_scene_walker_end = end;
  g_observer.last_scene_walker_count = count;
  g_observer.last_scene_walker_first = first;
  g_observer.scene_walker_object_slots_total += count;
  if (count != 0) {
    ++g_observer.scene_walker_nonempty_calls;
  }

  const bool should_log =
      RendererDrawTracingEnabled() &&
      (g_observer.logged_scene_walker_samples < 32u ||
       count == 0 ||
       IsPowerOfTwo(g_observer.scene_walker_calls));
  if (should_log) {
    REXLOG_INFO(
        "Refii renderer scene walker enter #{} list=0x{:08X} "
        "entries=0x{:08X}-0x{:08X} count={} first=0x{:08X} "
        "ctx=0x{:08X} lr=0x{:08X}",
        g_observer.scene_walker_calls, args.r3, begin, end, count, first,
        args.r4, args.lr);
    ++g_observer.logged_scene_walker_samples;
  }
}

void ObserveFableSceneWalkerExitLocked(std::string_view symbol,
                                       uint32_t result) {
  if (symbol != "sub_8223CFA0" && symbol != "sub_822A8F68") {
    return;
  }
  g_observer.last_scene_walker_result = result;
}

bool IsSceneBatchTerminalDraw(const RendererHookArgs& args) {
  return args.lr == kFableSceneBatchDrawReturn;
}

uint32_t SceneBatchInlineVertexCount(const FableSceneBatchDrawMarker& marker) {
  return marker.stride != 0
             ? std::max<uint32_t>(marker.byte_count / marker.stride, 1u)
             : 1u;
}

uint64_t QueueDrawEventLocked(uint32_t device_pointer, bool draw_vertices_up,
                              bool refresh_device_bindings,
                              RendererDrawEventSource source,
                              const RendererLastDrawSnapshot& draw,
                              const RendererHookArgs* wrapper_args,
                              std::string_view wrapper_symbol);
void StampBeginVerticesWrapperIdentityLocked(RendererDrawEventSnapshot& event,
                                             const RendererHookArgs& args);
void StampRootDrawSubmitIdentityLocked(RendererDrawEventSnapshot& event,
                                       std::string_view symbol,
                                       const RendererHookArgs& args);

void ObserveSceneBatchDrawSubmitLocked(const RendererHookArgs& args,
                                       uint8_t* base) {
  ++g_observer.scene_object_submit_hooks;
  constexpr uint32_t kSubmitManagerOffset = 0x08u;
  constexpr uint32_t kSubmitDeviceOffset = 0x38u;
  constexpr uint32_t kManagerStreamBaseOffset = 0x24u;
  constexpr uint32_t kManagerEntryCountOffset = 0x28u;
  constexpr uint32_t kManagerByteCursorOffset = 0x2Cu;
  constexpr uint32_t kMaxSceneBatchEntries = 0x7D0u;

  auto reject = [&](const char* reason) {
    ++g_observer.scene_batch_bridge_rejected_calls;
    g_observer.scene_batch_draw_marker = {};
    if (RendererDrawTracingEnabled() &&
        g_observer.logged_scene_batch_rejects < 24u) {
      const uint32_t manager =
          base != nullptr && IsLikelyGuestDwordAddress(args.r3 + 8u)
              ? LoadGuestU32(base, args.r3 + 8u)
              : 0u;
      REXLOG_INFO(
          "Refii renderer scene batch bridge rejected #{} reason={} "
          "ctx=0x{:08X} record=0x{:08X} batch=0x{:08X} mgr=0x{:08X} "
          "lr=0x{:08X}",
          g_observer.logged_scene_batch_rejects + 1u, reason, args.r3,
          args.r4, args.r5, manager, args.lr);
      ++g_observer.logged_scene_batch_rejects;
    }
  };

  if (base == nullptr) {
    reject("no_memory");
    return;
  }
  if (!IsLikelyGuestDwordAddress(args.r3 + kSubmitDeviceOffset) ||
      !IsLikelyGuestDwordAddress(args.r4 + kFableSceneBatchCopiedBytes - 4u) ||
      !IsLikelyGuestDwordAddress(args.r5 + 4u)) {
    reject("bad_args");
    return;
  }

  const uint32_t manager = LoadGuestU32(base, args.r3 + kSubmitManagerOffset);
  const uint32_t device = LoadGuestU32(base, args.r3 + kSubmitDeviceOffset);
  if (!IsLikelyGuestDwordAddress(manager + kManagerByteCursorOffset) ||
      !IsLikelyFableObjectAddress(device)) {
    reject("bad_context");
    return;
  }

  const uint32_t entry_count =
      LoadGuestU32(base, manager + kManagerEntryCountOffset);
  if (entry_count >= kMaxSceneBatchEntries) {
    reject("full_batch");
    return;
  }

  const uint32_t stream_base =
      LoadGuestU32(base, manager + kManagerStreamBaseOffset);
  const uint32_t stream_write =
      stream_base <= UINT32_MAX - entry_count * kFableSceneBatchCopiedBytes
          ? stream_base + entry_count * kFableSceneBatchCopiedBytes
          : 0u;
  if (stream_write == 0 ||
      !IsLikelyGuestDwordAddress(stream_write +
                                 kFableSceneBatchCopiedBytes - 4u)) {
    reject("bad_stream");
    g_observer.scene_batch_draw_marker = {};
    return;
  }

  FableSceneBatchDrawMarker marker;
  marker.valid = true;
  marker.submit_context = args.r3;
  marker.manager = manager;
  marker.device = device;
  marker.batch_record = LoadGuestU32(base, args.r5 + 4u);
  marker.primitive_type = 4u;
  marker.byte_count = kFableSceneBatchInlineBytes;
  marker.stride = kFableSceneBatchVertexStride;
  marker.record_address = stream_write;
  marker.stream_address = stream_base;
  marker.byte_start = LoadGuestU32(base, manager + kManagerByteCursorOffset);
  BuildSceneBatchInlineRecord(base, args, marker.record_address,
                              marker.inline_data);
  g_observer.scene_batch_draw_marker = marker;

  if (g_observer.logged_scene_object_draws < 16u) {
    REXLOG_INFO(
        "Refii renderer scene batch submit #{} ctx=0x{:08X} mgr=0x{:08X} "
        "dev=0x{:08X} batch=0x{:08X} record=0x{:08X} stream=0x{:08X} "
        "byte_start={} bytes={} vertices={} lr=0x{:08X}",
        g_observer.logged_scene_object_draws + 1u, marker.submit_context,
        marker.manager, marker.device, marker.batch_record,
        marker.record_address, marker.stream_address, marker.byte_start,
        marker.byte_count, SceneBatchInlineVertexCount(marker), args.lr);
    ++g_observer.logged_scene_object_draws;
  }

  RendererLastDrawSnapshot last_draw = {
      .indexed = false,
      .primitive_type = marker.primitive_type,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = SceneBatchInlineVertexCount(marker),
  };
  ++g_observer.draws;
  ++g_observer.scene_object_draws;
  ++g_observer.scene_batch_bridge_queued_draws;
  QueueDrawEventLocked(marker.device, false, true,
                       RendererDrawEventSource::kSceneObject, last_draw);
  TraceDrawCall("sub_82219740", last_draw);
}

bool IsFableBeginVerticesWrapperSymbol(std::string_view symbol);
bool IsFableBeginVerticesWrapperReturnAddress(uint32_t lr);

bool ApplySceneBatchMarkerToDrawEventLocked(RendererDrawEventSnapshot& event,
                                            std::vector<uint8_t>& inline_data,
                                            uint32_t& inline_address,
                                            uint32_t& inline_stride) {
  if (event.source != RendererDrawEventSource::kSceneObject ||
      !g_observer.scene_batch_draw_marker.valid) {
    return false;
  }

  const FableSceneBatchDrawMarker marker = g_observer.scene_batch_draw_marker;
  event.draw.indexed = false;
  event.draw.primitive_type = marker.primitive_type;
  event.draw.base_vertex = 0;
  event.draw.start = 0;
  event.draw.primitive_count = SceneBatchInlineVertexCount(marker);

  inline_address = marker.record_address != 0
                       ? marker.record_address
                       : (0xFD000000u | static_cast<uint32_t>(
                                            event.serial & 0x00FFFFFFu));
  inline_stride = marker.stride;
  event.streams[0] = {
      .buffer = inline_address,
      .offset = 0,
      .stride = marker.stride,
  };
  inline_data.assign(marker.inline_data.begin(), marker.inline_data.end());
  ++g_observer.scene_inline_vertex_draws;
  g_observer.scene_batch_draw_marker.queued = true;
  return true;
}

uint64_t QueueDrawEventLocked(uint32_t device_pointer, bool draw_vertices_up,
                              bool refresh_device_bindings,
                              RendererDrawEventSource source,
                              const RendererLastDrawSnapshot& draw,
                              const RendererHookArgs* wrapper_args,
                              std::string_view wrapper_symbol) {
  RendererDrawEventSnapshot event =
      BuildDrawEventLocked(device_pointer, draw_vertices_up,
                           refresh_device_bindings, source, draw);
  std::vector<uint8_t> inline_vertex_data;
  uint32_t inline_vertex_address = 0;
  uint32_t inline_vertex_stride = 0;
  ApplySceneBatchMarkerToDrawEventLocked(event, inline_vertex_data,
                                         inline_vertex_address,
                                         inline_vertex_stride);
  if (wrapper_args) {
    StampBeginVerticesWrapperIdentityLocked(event, *wrapper_args);
    StampRootDrawSubmitIdentityLocked(event, wrapper_symbol, *wrapper_args);
  }
  const RendererDrawEventSnapshot recorded_event =
      RecordDrawEventLocked(std::move(event));
  if (!inline_vertex_data.empty() && inline_vertex_address != 0) {
    FableDirectAttachDrawScratchBuffer(
        recorded_event.serial, false, inline_vertex_address,
        inline_vertex_stride, inline_vertex_data.data(),
        inline_vertex_data.size());
  }
  return recorded_event.serial;
}

RendererDrawEventSnapshot BuildDrawEventStatePatchLocked(
    uint32_t device_pointer) {
  RendererDrawEventSnapshot event;
  const FableDirectStateSnapshot direct_state = GetFableDirectStateSnapshot();

  ResolveDrawEventTargetsLocked(event);
  FableDirectApplyStateSnapshotToDrawEvent(direct_state, event, false);

  ApplyDeviceRegistersToDrawEventLocked(event, device_pointer);
  if (event.device_registers_valid) {
    SyncDrawEventRenderStatesFromDepthControl(event);
  }
  ApplyTextureObjectFetchConstantsToEventLocked(event);
  BindDrawEventStreamsFromVertexFetchConstantsLocked(event);
  RefreshDrawEventShaderFloatConstantsLocked(event, device_pointer);
  StampObserverDrawEventMetadataLocked(event);
  return event;
}

void PatchActiveWrappedDrawEventStateLocked() {
  if (g_observer.active_wrapped_draw_serial == 0 ||
      g_observer.active_wrapped_draw_pending_materialization) {
    return;
  }

  const RendererDrawEventSnapshot patch =
      BuildDrawEventStatePatchLocked(g_observer.active_wrapped_draw_device);
  RendererDrawEventSnapshot patched_event;
  bool patched_any = false;
  FableDirectPatchDrawEvent(
      g_observer.active_wrapped_draw_serial,
      [&](RendererDrawEventSnapshot& event) {
    if (event.source == RendererDrawEventSource::kPixVideo ||
        event.source == RendererDrawEventSource::kPrivateTextureRectangle ||
        event.private_texture_rectangle) {
      return false;
    }
    FableDirectApplyWrappedDrawEventStatePatch(event, patch);
    patched_event = event;
    patched_any = true;
    return true;
  });
  if (!patched_any) {
    return;
  }

  if (g_observer.active_wrapped_draw_indexed_scratch &&
      g_observer.active_wrapped_draw_scratch_captured) {
    return;
  }

  std::vector<ScratchBufferAttachment> expanded_scratch_attachments =
      ExpandIndexedScratchStreamsToIndexArenaLocked(patched_event);
  for (const ScratchBufferAttachment& attachment :
       expanded_scratch_attachments) {
    FableDirectAttachDrawScratchBuffer(
        g_observer.active_wrapped_draw_serial, attachment.index_buffer,
        attachment.address, attachment.stride_or_format,
        attachment.data.data(), attachment.data.size());
  }

  for (const RendererStreamBindingSnapshot& stream : patched_event.streams) {
    if (stream.buffer == 0 || !IsLikelyScratchBufferAddress(stream.buffer)) {
      continue;
    }
    AttachScratchBufferPayloadToDrawCommandLocked(
        g_observer.active_wrapped_draw_serial, RendererResourceKind::kVertexBuffer,
        stream.buffer);
  }
  if (patched_event.draw.indexed && patched_event.index_buffer != 0 &&
      IsLikelyScratchBufferAddress(patched_event.index_buffer)) {
    AttachScratchBufferPayloadToDrawCommandLocked(
        g_observer.active_wrapped_draw_serial, RendererResourceKind::kIndexBuffer,
        patched_event.index_buffer);
  }
}

void ResetActiveWrappedDrawStateLocked() {
  g_observer.active_wrapped_draw_serial = 0;
  g_observer.active_wrapped_draw_device = 0;
  g_observer.active_wrapped_draw_args = {};
  g_observer.active_wrapped_draw_source = RendererDrawEventSource::kUnknown;
  g_observer.active_wrapped_draw_draw = {};
  g_observer.active_wrapped_draw_indexed_scratch = false;
  g_observer.active_wrapped_draw_scratch_captured = false;
  g_observer.active_wrapped_draw_pending_materialization = false;
  g_observer.active_wrapped_draw_call_serial = 0;
  g_observer.active_wrapped_draw_context_key = 0;
  g_observer.active_wrapped_draw_symbol_address = 0;
}

void RetireActiveWrappedDrawAfterSyntheticReplacementLocked(
    std::string_view reason) {
  const uint64_t replaced_serial = g_observer.active_wrapped_draw_serial;
  const uint64_t replaced_call_serial =
      g_observer.active_wrapped_draw_call_serial;
  const uint64_t replaced_context_key =
      g_observer.active_wrapped_draw_context_key;
  const uint32_t device = g_observer.active_wrapped_draw_device;
  const bool pending_materialization =
      g_observer.active_wrapped_draw_pending_materialization;
  if (replaced_serial != 0) {
    FableDirectRemoveDrawEvent(replaced_serial);
  }

  static uint32_t logged_synthetic_replacements = 0;
  if (logged_synthetic_replacements < 32u) {
    REXLOG_INFO(
        "Refii renderer retired wrapped draw after synthetic replacement #{} "
        "reason={} serial={} call={} context=0x{:016X} dev=0x{:08X} "
        "pending_materialization={}",
        ++logged_synthetic_replacements, reason,
        static_cast<unsigned long long>(replaced_serial),
        static_cast<unsigned long long>(replaced_call_serial),
        static_cast<unsigned long long>(replaced_context_key), device,
        pending_materialization ? 1u : 0u);
  }

  ResetActiveWrappedDrawStateLocked();
}

void BeginWrappedDrawStatePatchLocked(uint64_t draw_serial,
                                      uint32_t device_pointer,
                                      const RendererHookArgs& args,
                                      bool indexed_scratch_draw) {
  if (draw_serial == 0) {
    return;
  }
  if (g_observer.active_wrapped_draw_serial != 0 &&
      g_observer.active_wrapped_draw_serial != draw_serial) {
    PatchActiveWrappedDrawEventStateLocked();
    g_observer.active_wrapped_draw_serial = 0;
    g_observer.active_wrapped_draw_device = 0;
    g_observer.active_wrapped_draw_args = {};
    g_observer.active_wrapped_draw_source = RendererDrawEventSource::kUnknown;
	    g_observer.active_wrapped_draw_draw = {};
	    g_observer.active_wrapped_draw_indexed_scratch = false;
	    g_observer.active_wrapped_draw_scratch_captured = false;
	    g_observer.active_wrapped_draw_pending_materialization = false;
	    g_observer.active_wrapped_draw_call_serial = 0;
	    g_observer.active_wrapped_draw_context_key = 0;
	    g_observer.active_wrapped_draw_symbol_address = 0;
	  }
  g_observer.active_wrapped_draw_serial = draw_serial;
  g_observer.active_wrapped_draw_device = device_pointer;
  g_observer.active_wrapped_draw_args = args;
  g_observer.active_wrapped_draw_source = RendererDrawEventSource::kUnknown;
	  g_observer.active_wrapped_draw_draw = {};
	  g_observer.active_wrapped_draw_indexed_scratch = indexed_scratch_draw;
	  g_observer.active_wrapped_draw_scratch_captured = false;
	  g_observer.active_wrapped_draw_pending_materialization = false;
	  g_observer.active_wrapped_draw_call_serial = 0;
	  g_observer.active_wrapped_draw_context_key = 0;
	  g_observer.active_wrapped_draw_symbol_address = 0;
		}

uint64_t FableRootDrawSubmitContextKey(uint64_t call_serial,
                                       uint32_t symbol_address,
                                       const RendererHookArgs& args,
                                       const RendererLastDrawSnapshot& draw,
                                       uint32_t frame_index);
void ApplyDrawEventScratchBuffersLocked(RendererDrawEventSnapshot& event,
                                        uint32_t vertex_buffer,
                                        uint32_t vertex_stride,
                                        uint32_t index_buffer);

void BeginPendingIndexedWrapperDrawLocked(
    uint32_t device_pointer, const RendererHookArgs& args,
    RendererDrawEventSource source, const RendererLastDrawSnapshot& draw,
    std::string_view symbol) {
  if (g_observer.active_wrapped_draw_serial != 0 &&
      !g_observer.active_wrapped_draw_pending_materialization) {
    PatchActiveWrappedDrawEventStateLocked();
  }

  const uint32_t symbol_address = FableExactStateSymbolAddress(symbol);
  const uint64_t call_serial = ++g_observer.serial;
  g_observer.active_wrapped_draw_serial = 0;
  g_observer.active_wrapped_draw_device = device_pointer;
  g_observer.active_wrapped_draw_args = args;
  g_observer.active_wrapped_draw_source = source;
  g_observer.active_wrapped_draw_draw = draw;
  g_observer.active_wrapped_draw_indexed_scratch = true;
  g_observer.active_wrapped_draw_scratch_captured = false;
  g_observer.active_wrapped_draw_pending_materialization = true;
  g_observer.active_wrapped_draw_call_serial = call_serial;
  g_observer.active_wrapped_draw_context_key = FableRootDrawSubmitContextKey(
      call_serial, symbol_address, args, draw, CurrentPlumeOutputFrameIndex());
  g_observer.active_wrapped_draw_symbol_address = symbol_address;
}

uint64_t MaterializePendingIndexedWrapperDrawLocked(uint32_t vertex_buffer,
                                                   uint32_t vertex_stride,
                                                   uint32_t index_buffer,
                                                   std::string_view hook_symbol) {
  if (!g_observer.active_wrapped_draw_pending_materialization) {
    return g_observer.active_wrapped_draw_serial;
  }
  RendererDrawEventSnapshot event = BuildDrawEventLocked(
      g_observer.active_wrapped_draw_device, false, true,
      g_observer.active_wrapped_draw_source, g_observer.active_wrapped_draw_draw);
  ApplyDrawEventScratchBuffersLocked(event, vertex_buffer, vertex_stride,
                                     index_buffer);
  const RendererDrawEventSnapshot recorded_event =
      RecordDrawEventLocked(std::move(event));

  g_observer.active_wrapped_draw_serial = recorded_event.serial;
  g_observer.active_wrapped_draw_pending_materialization = false;

  static uint32_t logged_exact_indexed_scratch_wrapper_draws = 0;
  if (logged_exact_indexed_scratch_wrapper_draws < 32u) {
    REXLOG_INFO(
        "Refii renderer exact indexed scratch wrapper draw materialized #{} "
        "serial={} call={} context=0x{:016X} symbol=0x{:08X} hook={} "
        "dev=0x{:08X} source={} prim={} base={} count={} vb=0x{:08X}/{} "
        "ib=0x{:08X} rt=0x{:08X} ds=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        ++logged_exact_indexed_scratch_wrapper_draws,
        static_cast<unsigned long long>(recorded_event.serial),
        static_cast<unsigned long long>(
            g_observer.active_wrapped_draw_call_serial),
        static_cast<unsigned long long>(
            g_observer.active_wrapped_draw_context_key),
        g_observer.active_wrapped_draw_symbol_address, hook_symbol,
        g_observer.active_wrapped_draw_device,
        static_cast<uint32_t>(g_observer.active_wrapped_draw_source),
        g_observer.active_wrapped_draw_draw.primitive_type,
        g_observer.active_wrapped_draw_draw.base_vertex,
        g_observer.active_wrapped_draw_draw.primitive_count, vertex_buffer,
        vertex_stride, index_buffer, recorded_event.render_targets[0],
        recorded_event.depth_stencil, recorded_event.vertex_shader,
        recorded_event.pixel_shader);
  }
  return recorded_event.serial;
}

void EndWrappedDrawStatePatchLocked(std::string_view symbol,
                                    const RendererHookArgs& args) {
  if (symbol == "sub_82205F68" && IsDrawVerticesUpInnerScratchSubmit(args)) {
    return;
  }
  const bool root_draw_submit = IsFableRootDrawSubmitHook(symbol);
  const bool begin_vertices_wrapper = IsFableBeginVerticesWrapperSymbol(symbol);
  if (!root_draw_submit && !begin_vertices_wrapper) {
    return;
  }

  PatchActiveWrappedDrawEventStateLocked();
  if (root_draw_submit && symbol == "sub_82217DB8" &&
      g_observer.active_wrapped_draw_indexed_scratch &&
      !g_observer.active_wrapped_draw_scratch_captured) {
    return;
  }
  if (root_draw_submit && symbol == "sub_82205F68" &&
      !g_observer.active_wrapped_draw_indexed_scratch &&
      !g_observer.active_wrapped_draw_scratch_captured) {
    return;
  }
  if (begin_vertices_wrapper &&
      !g_observer.active_wrapped_draw_indexed_scratch &&
      !g_observer.active_wrapped_draw_scratch_captured) {
    return;
  }
  if (g_observer.active_wrapped_draw_serial != 0) {
    FableDirectCaptureExactNonVideoRenderCommands(
        g_observer.active_wrapped_draw_serial);
  }
  g_observer.active_wrapped_draw_serial = 0;
  g_observer.active_wrapped_draw_device = 0;
  g_observer.active_wrapped_draw_args = {};
	  g_observer.active_wrapped_draw_source = RendererDrawEventSource::kUnknown;
		  g_observer.active_wrapped_draw_draw = {};
		  g_observer.active_wrapped_draw_indexed_scratch = false;
		  g_observer.active_wrapped_draw_scratch_captured = false;
		  g_observer.active_wrapped_draw_pending_materialization = false;
		  g_observer.active_wrapped_draw_call_serial = 0;
		  g_observer.active_wrapped_draw_context_key = 0;
		  g_observer.active_wrapped_draw_symbol_address = 0;
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

  RendererDrawEventSnapshot event =
      FableDirectBuildEvent(RendererDrawEventSource::kPm4Resolve);
  event.pm4_registers_valid = true;
  event.rb_modecontrol = 6u;
  event.rb_copy_dest_base = normalized_base;
  event.state_flush_lr = source_lr;

  ResolveDrawEventTargetsLocked(event);

  const uint32_t source_slot = event.rb_copy_control & 0x7u;
  const bool depth_resolve = source_slot >= std::size(event.render_targets);
  uint32_t source_surface =
      depth_resolve ? event.depth_stencil : event.render_targets[source_slot];
  if (source_surface == 0) {
    if (g_observer.logged_export_resolve_rejections < 16u) {
      REXLOG_INFO(
          "Refii renderer export resolve rejected source missing slot={} "
          "dest=0x{:08X} size={} lr=0x{:08X} rt0=0x{:08X} depth=0x{:08X} "
          "copy_control=0x{:08X}",
          source_slot, normalized_base, byte_size, source_lr,
          event.render_targets[0], event.depth_stencil, event.rb_copy_control);
      ++g_observer.logged_export_resolve_rejections;
    }
    return;
  }

  StampObserverDrawEventMetadataLocked(event, source_lr);
  FableDirectRecordDrawEvent(event);

  if (g_observer.logged_export_resolves < 16u) {
    REXLOG_INFO(
        "Refii renderer queued export invalidate resolve #{} src=0x{:08X} "
        "slot={} dest=0x{:08X} size={} lr=0x{:08X}",
        g_observer.logged_export_resolves + 1u, source_surface,
        event.rb_copy_control & 0x7u, normalized_base, byte_size, source_lr);
    ++g_observer.logged_export_resolves;
  }
}

uint32_t ReadEndExportSurfaceByteSizeLocked(uint8_t* base,
                                            uint32_t surface_address) {
  if (!base || !IsLikelyFableObjectAddress(surface_address)) {
    return 0;
  }

  uint32_t byte_size =
      LoadGuestU32(base, surface_address + kFableSurfaceByteSizeOffset);
  if (byte_size != 0) {
    return byte_size;
  }

  const GuestSurfaceRecord color_surface =
      ReadTrackedOrGuestSurfaceRecordLocked(
          RendererResourceKind::kRenderTargetSurface, surface_address, base);
  if (color_surface.byte_size != 0) {
    return color_surface.byte_size;
  }

  const GuestSurfaceRecord depth_surface = ReadTrackedOrGuestSurfaceRecordLocked(
      RendererResourceKind::kDepthSurface, surface_address, base);
  if (depth_surface.byte_size != 0) {
    return depth_surface.byte_size;
  }

  const FableSurfaceExtent extent =
      DecodeFableSurfaceExtent(base, surface_address);
  if (extent.width == 0 || extent.height == 0) {
    return 0;
  }

  byte_size = extent.width * extent.height * sizeof(uint32_t);
  return (byte_size + 0xFFFu) & ~0xFFFu;
}

void QueueEndExportResolveEventLocked(uint32_t destination_word,
                                      uint32_t destination_mask,
                                      uint32_t byte_size,
                                      uint32_t source_lr) {
  const uint32_t masked_destination = destination_word & destination_mask;
  if (masked_destination == 0) {
    return;
  }
  QueueExportInvalidateResolveEventLocked(
      DecodeFableGpuMemoryPointer(masked_destination), byte_size, source_lr);
}

void ObserveEndExportResolveLocked(const RendererHookArgs& args,
                                   uint8_t* base) {
  if (!base || !IsLikelyFableObjectAddress(args.r5)) {
    if (g_observer.logged_end_export_resolve_probes < 16u) {
      REXLOG_INFO(
          "Refii renderer end export probe skipped base={} device=0x{:08X} "
          "r4=0x{:08X} surface=0x{:08X} lr=0x{:08X}",
          base != nullptr ? 1 : 0, args.r3, args.r4, args.r5, args.lr);
      ++g_observer.logged_end_export_resolve_probes;
    }
    return;
  }

  const uint32_t surface = args.r5;
  const uint32_t descriptor_kind = LoadGuestU32(base, surface) & 0xFu;
  if (g_observer.logged_end_export_resolve_probes < 16u) {
    REXLOG_INFO(
        "Refii renderer end export probe #{} surface=0x{:08X} kind={} "
        "w18=0x{:08X} w1C=0x{:08X} w20=0x{:08X} w2C=0x{:08X} "
        "w30=0x{:08X} device=0x{:08X} lr=0x{:08X}",
        g_observer.logged_end_export_resolve_probes + 1u, surface,
        descriptor_kind,
        LoadGuestU32(base, surface + kFableSurfacePitchMsaaOffset),
        LoadGuestU32(base, surface + kFableSurfaceRenderTargetInfoOffset),
        LoadGuestU32(base, surface + kFableSurfaceDepthStencilInfoOffset),
        LoadGuestU32(base, surface + kFableSurfaceByteSizeOffset),
        LoadGuestU32(base, surface + 0x30u), args.r3, args.lr);
    ++g_observer.logged_end_export_resolve_probes;
  }
  if (descriptor_kind == 1u) {
    QueueEndExportResolveEventLocked(
        LoadGuestU32(base, surface + kFableSurfacePitchMsaaOffset),
        0xFFFFFFFCu,
        LoadGuestU32(base, surface + kFableSurfaceRenderTargetInfoOffset) &
            0x03FFFFFCu,
        0x821968C8u);
    return;
  }

  if (descriptor_kind != 3u) {
    return;
  }

  const uint32_t byte_size = ReadEndExportSurfaceByteSizeLocked(base, surface);
  QueueEndExportResolveEventLocked(
      LoadGuestU32(base, surface + kFableSurfaceDepthStencilInfoOffset),
      0xFFFFF000u, byte_size, 0x821968A4u);
  QueueEndExportResolveEventLocked(LoadGuestU32(base, surface + 0x30u),
                                   0xFFFFF000u, byte_size, 0x821968C8u);
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
  RendererDrawEventSnapshot event =
      FableDirectBuildEvent(RendererDrawEventSource::kPm4Resolve);
  if (!event.pm4_registers_valid ||
      (event.rb_modecontrol & 0x7u) != kXenosEdramModeCopy ||
      event.rb_copy_dest_base == 0) {
    return;
  }

  event.state_flush_lr = source_lr;

  ResolveDrawEventTargetsLocked(event);

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

  StampObserverDrawEventMetadataLocked(event, source_lr);
  FableDirectRecordDrawEvent(event);

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

void QueuePm4DrawEventLocked(uint8_t* base, uint32_t command_pointer,
                             uint32_t packet_offset, uint32_t packet_words,
                             uint32_t opcode, uint32_t source_lr) {
  if (!base || command_pointer == 0 || packet_words < 2u) {
    return;
  }

  uint32_t payload_offset = packet_offset + 1u;
  if (opcode == 0x22u) {
    if (packet_words < 3u) {
      return;
    }
    ++payload_offset;  // PM4_DRAW_INDX starts with a visibility query token.
  }

  const uint32_t payload_end = packet_offset + packet_words;
  if (payload_offset >= payload_end) {
    return;
  }

  const auto load_packet_word = [&](uint32_t word_offset) {
    return LoadGuestU32(base, command_pointer + word_offset * sizeof(uint32_t));
  };

  const uint32_t draw_initiator = load_packet_word(payload_offset++);
  const uint32_t primitive_type = draw_initiator & 0x3Fu;
  const uint32_t source_select = (draw_initiator >> 6u) & 0x3u;
  const bool index_32 = ((draw_initiator >> 11u) & 0x1u) != 0;
  const uint32_t index_or_vertex_count = draw_initiator >> 16u;
  if (primitive_type == 0 || index_or_vertex_count == 0) {
    return;
  }

  RendererDrawEventSnapshot event =
      FableDirectBuildEvent(RendererDrawEventSource::kPm4Draw);
  event.draw = {
      .indexed = source_select == 0u,
      .primitive_type = primitive_type,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = index_or_vertex_count,
  };
  event.state_flush_lr = source_lr;

  if (event.draw.indexed) {
    if (payload_offset + 1u >= payload_end) {
      return;
    }
    const uint32_t dma_base =
        NormalizeGpuPointer(load_packet_word(payload_offset++));
    const uint32_t dma_size = load_packet_word(payload_offset++);
    const uint32_t index_stride = index_32 ? 4u : 2u;
    const uint32_t dma_index_count = dma_size & 0x00FFFFFFu;
    const uint32_t index_byte_size = dma_index_count * index_stride;
    if (dma_base == 0 || index_byte_size == 0) {
      return;
    }
    UpsertScratchBufferResourceLocked(RendererResourceKind::kIndexBuffer,
                                      dma_base, index_byte_size, index_stride,
                                      0, dma_base);
    event.index_buffer = dma_base;
  } else if (source_select != 2u) {
    return;
  }

  ResolveDrawEventTargetsLocked(event);
  StampObserverDrawEventMetadataLocked(event, source_lr);
  event = RecordDrawEventLocked(std::move(event));

  if (RendererDrawTracingEnabled() && g_observer.logged_pm4_draws < 16u) {
    REXLOG_INFO(
        "Refii renderer queued PM4 draw #{} opcode={} prim={} indexed={} "
        "count={} ib=0x{:08X} lr=0x{:08X}",
        g_observer.logged_pm4_draws + 1u, FablePm4OpcodeName(opcode),
        primitive_type, event.draw.indexed ? 1 : 0, index_or_vertex_count,
        event.index_buffer, source_lr);
    ++g_observer.logged_pm4_draws;
  }
}

bool IsDrawVerticesUpInnerScratchSubmit(const RendererHookArgs& args) {
  return args.lr >= kD3DDeviceDrawVerticesUPStart &&
         args.lr < kD3DDeviceDrawVerticesUPEnd;
}

uint64_t FableExactClearContextKey(uint64_t call_serial,
                                   uint32_t root_symbol_address,
                                   uint32_t root_lr, uint32_t flags,
                                   const RendererScissorSnapshot& rect,
                                   const RendererDrawEventSnapshot& event) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(call_serial);
  mix(root_symbol_address);
  mix(root_lr);
  mix(flags);
  mix(event.render_targets[0]);
  mix(event.render_targets[1]);
  mix(event.render_targets[2]);
  mix(event.render_targets[3]);
  mix(event.depth_stencil);
  mix(rect.valid ? 1u : 0u);
  mix(static_cast<uint32_t>(rect.left));
  mix(static_cast<uint32_t>(rect.top));
  mix(static_cast<uint32_t>(rect.right));
  mix(static_cast<uint32_t>(rect.bottom));
  return key;
}

uint64_t FableExactSetColorClearContextKey(uint64_t call_serial,
                                           uint32_t root_symbol_address,
                                           uint32_t root_lr, uint32_t device,
                                           uint32_t params,
                                           uint32_t color_target,
                                           uint32_t packed0,
                                           uint32_t packed1) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(call_serial);
  mix(root_symbol_address);
  mix(root_lr);
  mix(device);
  mix(params);
  mix(color_target);
  mix(packed0);
  mix(packed1);
  return key;
}

void ObserveSetColorClearRootLocked(const RendererHookArgs& args,
                                    uint8_t* base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return;
  }

  constexpr uint32_t kSetColorClearRoot = 0x822A09A0u;
  constexpr uint32_t kFableDeviceColorClearPacked0Offset = 0x2A30u;
  constexpr uint32_t kFableDeviceColorClearPacked1Offset = 0x2A34u;
  const uint32_t packed0 =
      LoadGuestU32(base, args.r3 + kFableDeviceColorClearPacked0Offset);
  const uint32_t packed1 =
      LoadGuestU32(base, args.r3 + kFableDeviceColorClearPacked1Offset);
  const uint64_t call_serial = ++g_observer.serial;
  const uint64_t context_key = FableExactSetColorClearContextKey(
      call_serial, kSetColorClearRoot, args.lr, args.r3, args.r4, args.r5,
      packed0, packed1);

  if (g_observer.logged_set_color_clear_roots < 32u) {
    REXLOG_INFO(
        "Refii renderer exact SetColorClear root #{} call={} "
        "context=0x{:016X} device=0x{:08X} params=0x{:08X} "
        "target={} packed=0x{:08X}/0x{:08X} lr=0x{:08X}",
        ++g_observer.logged_set_color_clear_roots,
        static_cast<unsigned long long>(call_serial),
        static_cast<unsigned long long>(context_key), args.r3, args.r4,
        args.r5, packed0, packed1, args.lr);
  }
}

void QueueClearEventLocked(uint32_t flags, uint32_t color_pointer,
                           double depth, uint32_t stencil,
                           RendererScissorSnapshot rect,
                           uint32_t root_symbol_address,
                           uint32_t root_lr) {
  RendererDrawEventSnapshot event =
      FableDirectBuildEvent(RendererDrawEventSource::kClear);
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

  ResolveDrawEventTargetsLocked(event);
  event.clear_root_call_serial = ++g_observer.serial;
  event.clear_root_symbol_address = root_symbol_address;
  event.clear_root_lr = root_lr;
  event.clear_root_context_key =
      FableExactClearContextKey(event.clear_root_call_serial,
                                root_symbol_address, root_lr, flags, rect,
                                event);
  if (g_observer.memory_base) {
    constexpr uint32_t kClearColorTargetMask = 0xFu;
    constexpr uint32_t kClearDepth = 0x10u;
    constexpr uint32_t kClearStencil = 0x20u;
    const bool clears_any_color =
        (event.clear_flags & kClearColorTargetMask) != 0;
    for (uint32_t slot = 0; slot < 4u; ++slot) {
      const uint32_t render_target = event.render_targets[slot];
      if (render_target == 0) {
        continue;
      }
      if (clears_any_color &&
          (event.clear_flags & (1u << slot)) == 0) {
        continue;
      }
      TrackBoundSurfaceLocked(RendererResourceKind::kRenderTargetSurface,
                              render_target, 0, g_observer.memory_base);
    }
    if ((event.clear_flags & (kClearDepth | kClearStencil)) != 0 &&
        event.depth_stencil != 0) {
      TrackBoundSurfaceLocked(RendererResourceKind::kDepthSurface,
                              event.depth_stencil, 0,
                              g_observer.memory_base);
    }
  }
  StampObserverDrawEventMetadataLocked(event, root_lr);
  FableDirectRecordDrawEvent(event);
}

void ObserveRendererStateOrDrawLocked(std::string_view symbol,
                                      const RendererHookArgs& args,
                                      uint8_t* base) {
  const uint32_t render_state_slot = FableRenderStateSlotForSymbol(symbol);
  if (render_state_slot < kRendererD3DRenderStateSlots) {
    FableDirectSetRenderState(
        args.r3, render_state_slot, args.r4,
        FableExactStateRootLocked(FableExactStateSymbolAddress(symbol), args.lr,
                                  args.r3, render_state_slot, args.r4));
    RememberRequestedDepthRenderStateLocked(render_state_slot, args.r4);
    ++g_observer.state_changes;
    return;
  }

  if (symbol == "sub_8221AE18" && base && args.r3 != 0) {
    RefreshEffectiveDepthRenderStateLocked(base, args.r3);
    ++g_observer.state_changes;
    return;
  }

  const uint32_t sampler_state_slot = FableSamplerStateSlotForSymbol(symbol);
  if (sampler_state_slot != UINT32_MAX) {
    if (args.r4 < kRendererD3DSamplerStateSlots) {
      FableDirectStorePackedSamplerState(
          args.r3, args.r4, sampler_state_slot, args.r5,
          FableExactStateRootLocked(FableExactStateSymbolAddress(symbol),
                                    args.lr, args.r3, args.r4, args.r5,
                                    sampler_state_slot));
    }
    ++g_observer.state_changes;
    return;
  }

  if (symbol == "sub_822869A0" || symbol == "D3DDevice_SetRenderTarget") {
    if (args.r4 < 4) {
      SetObservedRenderTargetLocked(args.r4, args.r5, args.r3, base,
                                    0x822869A0u, args.lr);
      ++g_observer.state_changes;
    }
    return;
  }
  if (symbol == "sub_821FC540") {
    constexpr uint32_t kTransientRenderTargetTable = 0x834A608Cu;
    constexpr uint32_t kTransientRenderTargetEntryStride = 12u;
    constexpr uint32_t kTransientRenderTargetEntryCount = 4u;
    if (base && g_observer.logged_transient_render_target_releases < 32u) {
      std::array<uint32_t, kTransientRenderTargetEntryCount> surfaces = {};
      std::array<uint32_t, kTransientRenderTargetEntryCount> siblings = {};
      bool any_surface = false;
      for (uint32_t slot = 0; slot < kTransientRenderTargetEntryCount; ++slot) {
        const uint32_t entry =
            kTransientRenderTargetTable + slot * kTransientRenderTargetEntryStride;
        surfaces[slot] = LoadGuestU32(base, entry);
        siblings[slot] = LoadGuestU32(base, entry + 4u);
        any_surface = any_surface || surfaces[slot] != 0 || siblings[slot] != 0;
        if (IsLikelyFableObjectAddress(surfaces[slot])) {
          TrackBoundSurfaceLocked(RendererResourceKind::kRenderTargetSurface,
                                  surfaces[slot], 0, base);
        }
      }
      if (any_surface || g_observer.logged_transient_render_target_releases < 8u) {
        const FableDirectStateSnapshot direct_snapshot =
            GetFableDirectStateSnapshot();
        REXLOG_INFO(
            "Refii renderer transient RT release queue #{} device=0x{:08X} "
            "lr=0x{:08X} table=0x{:08X} "
            "any={} "
            "slot0=0x{:08X}/0x{:08X} slot1=0x{:08X}/0x{:08X} "
            "slot2=0x{:08X}/0x{:08X} slot3=0x{:08X}/0x{:08X} "
            "cur_rt={:08X}/{:08X}/{:08X}/{:08X} cur_ds=0x{:08X}",
            ++g_observer.logged_transient_render_target_releases, args.r3,
            args.lr, kTransientRenderTargetTable, any_surface ? 1 : 0,
            surfaces[0], siblings[0], surfaces[1], siblings[1], surfaces[2],
            siblings[2], surfaces[3], siblings[3], direct_snapshot.targets.color[0],
            direct_snapshot.targets.color[1], direct_snapshot.targets.color[2],
            direct_snapshot.targets.color[3], direct_snapshot.targets.depth);
      }
    }
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_82264590" ||
      symbol == "D3DDevice_SetDepthStencilSurface") {
    SetObservedDepthSurfaceLocked(base, args.r3, args.r4, 0x82264590u,
                                  args.lr);
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821F9F58") {
    MirrorViewportToGuestDeviceLocked(
        base, args.r3, args.r4,
        FableExactStateRootLocked(0x821F9F58u, args.lr, args.r3, 0u,
                                  args.r4));
    g_observer.viewport_pointer = args.r4;
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821F9D00" || symbol == "sub_821F9918") {
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821F9C08") {
    MirrorScissorToGuestDeviceLocked(
        base, args.r3, args.r4,
        FableExactStateRootLocked(0x821F9C08u, args.lr, args.r3, 0u,
                                  args.r4));
    g_observer.scissor_pointer = args.r4;
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821B6EE0") {
    if (args.r4 < 16) {
      MirrorTextureBindingToGuestDeviceLocked(base, args.r3, args.r4, args.r5);
      FableDirectSetTexture(
          args.r3, args.r4, args.r5,
          FableExactStateRootLocked(0x821B6EE0u, args.lr, args.r3, args.r4,
                                    args.r5));
      if (args.r5 != 0) {
        StoreBoundTextureObjectFetchDwordsLocked(args.r3, args.r4, args.r5,
                                                 base, args.lr);
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
      const RendererStreamBindingSnapshot stream = {
          .buffer = stream_buffer,
          .offset = stream_offset,
          .stride = args.r7,
      };
      FableDirectSetStream(
          args.r3, args.r4, stream,
          FableExactStateRootLocked(0x821B6C60u, args.lr, args.r3, args.r4,
                                    args.r5, args.r6));
      if (stream_size != 0) {
        const auto exact_stream_resource =
            FableDirectGuestResourceRegistry().resources.find(stream_buffer);
        const RendererResourceRecord* existing_stream_resource =
            exact_stream_resource != FableDirectGuestResourceRegistry().resources.end()
                ? &exact_stream_resource->second
                : nullptr;
        if (existing_stream_resource == nullptr ||
            !HasNonZeroUploadData(*existing_stream_resource)) {
          UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                            stream_buffer, stream_size, args.r7,
                                            args.r5, stream_upload_source);
          if (stream_upload_source != 0 && RendererVerboseTracingEnabled() &&
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
    FableDirectSetIndexBuffer(
        args.r3, args.r4,
        FableExactStateRootLocked(0x8219CC30u, args.lr, args.r3, 0u,
                                  args.r4));
    if (base && IsLikelyFableObjectAddress(args.r4)) {
      RendererResourceRecord& record = FableDirectGuestResourceRegistry().resources[args.r4];
      const bool is_new = GuestObjectAddress(record) == 0;
      if (is_new) {
        record = CreateGuestBufferResource(RendererResourceKind::kIndexBuffer,
                                           args.r4, args.r3);
      }
      SetGuestResourceIdentity(record, RendererResourceKind::kIndexBuffer,
                               args.r4, args.r3);
      SnapshotGuestResource(record, base);
      GuestBufferRecord buffer = GuestBufferRecordFor(record);
      if (buffer.upload_hash == 0 && record.upload_generation != 0) {
        if (buffer.gpu_address != 0) {
          buffer.upload_hash = HashGuestBytesFnv1a64(
              base, buffer.gpu_address, record.upload_data.size());
          ApplyGuestBufferRecord(record, buffer);
        }
      }
      record.serial = ++g_observer.serial;
      RefreshQueuedDrawBufferResourceSerialLocked(record);
      StoreGuestResourceLocked(record, is_new, is_new);
    }
    ++g_observer.state_changes;
    return;
  }
  if (FableVertexDeclarationHookKindForSymbol(symbol) ==
      FableVertexDeclarationHookKind::kSetVertexDeclaration) {
    if (ShouldMirrorSuppressedDeviceState(base, args.r3)) {
      StoreGuestU32(base, args.r3 + kFableDeviceVertexDeclarationOffset,
                    args.r4);
    }
    FableDirectSetVertexDeclaration(
        args.r3, args.r4,
        FableExactStateRootLocked(FableExactStateSymbolAddress(symbol), args.lr,
                                  args.r3, 0u, args.r4));
    ++g_observer.state_changes;
    return;
  }
  const RendererResourceKind bound_shader_kind =
      FableBoundShaderRendererResourceKind(symbol);
  if (bound_shader_kind == RendererResourceKind::kVertexShader ||
      bound_shader_kind == RendererResourceKind::kPixelShader) {
    if (ShouldMirrorSuppressedDeviceState(base, args.r3)) {
      const uint32_t device_offset =
          bound_shader_kind == RendererResourceKind::kVertexShader
              ? kFableDeviceVertexShaderOffset
              : kFableDevicePixelShaderOffset;
      StoreGuestU32(base, args.r3 + device_offset, args.r4);
    }
    if (bound_shader_kind == RendererResourceKind::kVertexShader) {
      FableDirectSetVertexShader(
          args.r3, args.r4,
          FableExactStateRootLocked(FableExactStateSymbolAddress(symbol),
                                    args.lr, args.r3, 0u, args.r4));
    } else {
      FableDirectSetPixelShader(
          args.r3, args.r4,
          FableExactStateRootLocked(FableExactStateSymbolAddress(symbol),
                                    args.lr, args.r3, 0u, args.r4));
    }
    ++g_observer.state_changes;
    return;
  }
  if (IsFableVertexShaderBindHook(symbol)) {
    // D3DVertexShader_Bind patches/binds shader r3 against declaration r5.
    if (args.r5 != 0) {
      TrackVertexDeclarationLocked(args.r5, args.r3, base);
    }
    TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r3,
                            args.r5, base);
    // Only the 0x82221DC4 caller immediately falls through into draw
    // submission. Other callers use this helper to prepare/patch shader
    // objects and must not become Plume's active draw state.
    if (args.lr == kFableVertexShaderBindDrawReturn) {
      FableDirectSetVertexShader(0, args.r3);
      if (args.r5 != 0) {
        FableDirectSetVertexDeclaration(0, args.r5);
      }
    }
    ++g_observer.state_changes;
    return;
  }
  if (IsFableDeviceStateHook(symbol)) {
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
                  FableHookDisplayName(symbol), args.lr, count);
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
    g_observer.last_pm4_resolve_command_write = 0;
    g_observer.last_pm4_resolve_key = 0;
    ResetObservedRenderStatesToDefaultsLocked();
    g_observer.requested_z_enable = 0;
    g_observer.requested_z_write_enable = 0;
    g_observer.requested_z_enable_valid = false;
    g_observer.requested_z_write_enable_valid = false;
    ResetFableDirectState();
    ResetObservedRenderStatesToDefaultsLocked();
    FableDirectSetPackedSamplerStateSnapshot({});
    ++g_observer.state_changes;
    return;
  }
  if (symbol == "sub_821E2FE0") {
    RendererScissorSnapshot rect = DecodeGuestRect(base, args.r5);
    if (rect.valid) {
      rect = ScaleGuestScissorToOutput(rect);
    }
    ++g_observer.clears;
    QueueClearEventLocked(args.r4, args.r6, args.f1, args.r8, rect,
                          0x821E2FE0u, args.lr);
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
    ++g_observer.clears;
    // D3D::ClearRect receives GPU_SURFACEINFO/GPU_COLORINFO in r9/r10, not a
    // D3D clear mask. The hook owns the current framebuffer binding already,
    // so queue the color clear against slot 0 and leave depth/stencil to the
    // high-level D3DDevice_ClearF hook that passes an explicit mask.
    QueueClearEventLocked(1u, args.r8, args.f1, 0, rect, 0x821EF830u,
                          args.lr);
    return;
  }
  if (symbol == "sub_82219740") {
    ++g_observer.scene_batch_bridge_calls;
    ObserveSceneBatchDrawSubmitLocked(args, base);
    return;
  }
  if (symbol == "sub_822194B8" || symbol == "sub_82ABBA60" ||
      symbol == "sub_82ABD088") {
    ++g_observer.scene_object_submit_hooks;
    if (symbol == "sub_82ABBA60") {
      ++g_observer.scene_draw_filter_calls;
    } else if (symbol == "sub_82ABD088") {
      ++g_observer.scene_batch_traversal_calls;
    }
    return;
  }
  if (IsFableRenderPassHook(symbol)) {
    ++g_observer.render_passes;
    return;
  }
  if (symbol == "sub_8221F2E0") {
    ++g_observer.state_flushes;
    g_observer.last_state_flush_lr = args.lr;
    const FableDirectStateRoot root =
        FableExactStateRootLocked(0x8221F2E0u, args.lr, args.r3, 0u,
                                  args.lr);
    FableDirectRecordStateFlush(args.r3, args.lr, root);
    const uint64_t caller_count = ++g_observer.state_flush_callers[args.lr];
    if (RendererVerboseTracingEnabled() &&
        (caller_count <= 3 || IsPowerOfTwo(caller_count))) {
      REXLOG_INFO("Refii renderer state flush caller lr=0x{:08X} count={}",
                  args.lr, caller_count);
    }
    return;
  }
  if (IsFablePendingStateHook(symbol)) {
    ++g_observer.state_changes;
    const uint64_t count = ++g_observer.state_flush_callers[args.lr];
    if (RendererVerboseTracingEnabled() &&
        (count <= 3 || IsPowerOfTwo(count))) {
      REXLOG_INFO(
          "Refii renderer pending state {} mask=0x{:016X} mask_hi=0x{:08X} "
          "mask_lo=0x{:08X} "
          "lr=0x{:08X} count={}",
          FableHookDisplayName(symbol), args.r4_u64,
          static_cast<uint32_t>(args.r4_u64 >> 32u), args.r4, args.lr, count);
    }
    return;
  }
  if (IsFableCommandWriteHook(symbol)) {
    const uint64_t count = ++g_observer.command_writes;
    TraceCommandPath(symbol, args, count);
    DecodePm4WordsLocked(symbol, args, base);
    PatchActiveWrappedDrawEventStateLocked();
    return;
  }
  if (IsFableVideoStateHook(symbol)) {
    ++g_observer.state_changes;
    return;
  }
  if (IsFableCommandSubmitHook(symbol)) {
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
    ObserveEndExportResolveLocked(args, base);
    return;
  }
  if (symbol == "sub_822C6BA8") {
    ++g_observer.state_changes;
    QueueExportInvalidateResolveEventLocked(args.r4, args.r5, args.lr);
    return;
  }
  const FableDrawSubmitKind draw_submit_kind =
      FableDrawSubmitKindForSymbol(symbol);
  if (draw_submit_kind != FableDrawSubmitKind::kNone) {
    if (g_observer.active_wrapped_draw_serial != 0 &&
        !g_observer.active_wrapped_draw_indexed_scratch &&
        !g_observer.active_wrapped_draw_scratch_captured &&
        symbol != "sub_82205F68") {
      g_observer.active_wrapped_draw_scratch_captured =
          CaptureScratchDrawBuffersLocked(
              "sub_82205F68", g_observer.active_wrapped_draw_args, 0, base);
    }

    if (draw_submit_kind == FableDrawSubmitKind::kDrawVerticesUp ||
        (draw_submit_kind == FableDrawSubmitKind::kBeginVertices &&
         IsDrawVerticesUpInnerScratchSubmit(args))) {
      return;
    }

    if (draw_submit_kind == FableDrawSubmitKind::kDrawPrimitive ||
        draw_submit_kind == FableDrawSubmitKind::kDrawIndexedPrimitive) {
      FlushPendingDeviceStateForRootDrawLocked(args, base);
    }
    RefreshBoundSurfacesLocked(
        base, args.r3,
        FableExactStateRootLocked(FableExactStateSymbolAddress(symbol), args.lr,
                                  args.r3, 0u, args.r4, args.r5));
    if (g_observer.scene_submit_depth != 0 &&
        g_observer.logged_scene_draw_submit_samples < 32u) {
      REXLOG_INFO(
          "Refii renderer scene draw submit sample #{} symbol={} kind={} "
          "depth={} dev=0x{:08X} prim={} r5=0x{:08X} r6=0x{:08X} "
          "r7=0x{:08X} lr=0x{:08X} marker_valid={} marker_queued={} "
          "marker_dev=0x{:08X} marker_record=0x{:08X} marker_bytes={} "
          "marker_stride={}",
          g_observer.logged_scene_draw_submit_samples + 1u,
          FableHookName(symbol) ? FableHookName(symbol) : symbol.data(),
          static_cast<uint32_t>(draw_submit_kind),
          g_observer.scene_submit_depth, args.r3, args.r4, args.r5, args.r6,
          args.r7, args.lr, g_observer.scene_batch_draw_marker.valid ? 1 : 0,
          g_observer.scene_batch_draw_marker.queued ? 1 : 0,
          g_observer.scene_batch_draw_marker.device,
          g_observer.scene_batch_draw_marker.record_address,
          g_observer.scene_batch_draw_marker.byte_count,
          g_observer.scene_batch_draw_marker.stride);
      ++g_observer.logged_scene_draw_submit_samples;
    }
    RendererDrawEventSource source = RendererDrawEventSource::kD3DWrapper;
    RendererLastDrawSnapshot last_draw = {};
    switch (draw_submit_kind) {
      case FableDrawSubmitKind::kBeginVertices:
        source = g_observer.scene_submit_depth != 0
                     ? RendererDrawEventSource::kSceneObject
                     : RendererDrawEventSource::kScratchWrapper;
        last_draw = {
            .indexed = false,
            .primitive_type = args.r4,
            .base_vertex = 0,
            .start = 0,
            .primitive_count = args.r5,
        };
        break;
      case FableDrawSubmitKind::kBeginIndexedVertices:
        source = g_observer.scene_submit_depth != 0
                     ? RendererDrawEventSource::kSceneObject
                     : RendererDrawEventSource::kScratchWrapper;
        last_draw = {
            .indexed = true,
            .primitive_type = args.r4,
            .base_vertex = args.r5,
            .start = 0,
            .primitive_count = args.r7,
        };
        break;
      case FableDrawSubmitKind::kDrawPrimitive:
        last_draw = {
            .indexed = false,
            .primitive_type = args.r4,
            .base_vertex = 0,
            .start = args.r5,
            .primitive_count = args.r6,
        };
        if (IsSceneBatchTerminalDraw(args)) {
          if (g_observer.scene_batch_draw_marker.valid &&
              g_observer.scene_batch_draw_marker.queued) {
            return;
          }
          if (!g_observer.scene_batch_draw_marker.valid) {
            if (g_observer.logged_scene_terminal_missing_markers < 16u) {
              REXLOG_INFO(
                  "Refii renderer scene terminal draw missing marker #{} "
                  "dev=0x{:08X} prim={} start=0x{:08X} count=0x{:08X} "
                  "lr=0x{:08X}",
                  g_observer.logged_scene_terminal_missing_markers + 1u,
                  args.r3, args.r4, args.r5, args.r6, args.lr);
              ++g_observer.logged_scene_terminal_missing_markers;
            }
            return;
          }
          source = RendererDrawEventSource::kSceneObject;
          ++g_observer.scene_object_draws;
        }
        break;
      case FableDrawSubmitKind::kDrawIndexedPrimitive:
        if (g_observer.scene_submit_depth != 0) {
          source = RendererDrawEventSource::kSceneObject;
        }
        last_draw = {
            .indexed = true,
            .primitive_type = args.r4,
            .base_vertex = args.r5,
            .start = args.r6,
            .primitive_count = args.r7,
        };
        break;
      case FableDrawSubmitKind::kDrawVerticesUp:
      case FableDrawSubmitKind::kNone:
        return;
    }

    ++g_observer.draws;
    if (last_draw.indexed) {
      ++g_observer.indexed_draws;
    }
	    uint64_t draw_serial = 0;
	    if (draw_submit_kind == FableDrawSubmitKind::kBeginIndexedVertices) {
	      BeginPendingIndexedWrapperDrawLocked(args.r3, args, source, last_draw,
	                                           symbol);
	    } else {
	      draw_serial = QueueDrawEventLocked(args.r3, false, true, source, last_draw,
	                                         &args, symbol);
      BeginWrappedDrawStatePatchLocked(draw_serial, args.r3, args, false);
    }
    TraceDrawCall(symbol, last_draw);
    return;
  }
}

bool IsCreateHook(std::string_view symbol) {
  return FableCreatedRendererResourceKind(symbol) !=
         RendererResourceKind::kUnknown;
}

uint32_t CreatedResourceGuestAddress(std::string_view symbol,
                                     const RendererHookArgs& args,
                                     uint32_t result, uint8_t* base) {
  switch (FableCreatedResourceAddressSourceForSymbol(symbol)) {
    case FableCreatedResourceAddressSource::kR3:
      return args.r3;
    case FableCreatedResourceAddressSource::kR3Plus4:
      return (base && args.r3 != 0) ? LoadGuestU32(base, args.r3 + 4) : result;
    case FableCreatedResourceAddressSource::kR3Plus0x14:
      return (base && args.r3 != 0) ? LoadGuestU32(base, args.r3 + 0x14u)
                                    : result;
    case FableCreatedResourceAddressSource::kResult:
      return result;
  }

  return result;
}

FableGuestResourceCounts CountResourcesLocked() {
  FableGuestResourceCounts counts;
  counts.live_resources = FableDirectGuestResourceRegistry().resources.size();
  for (const auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    switch (GuestObjectKind(record)) {
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

  const RendererResourceKind kind = GuestObjectKind(record);
  const uint32_t object_address = GuestObjectAddress(record);
  const uint32_t parent_address = GuestObjectParentAddress(record);

  if (IsBufferResourceKind(kind)) {
    if (!RendererVerboseTracingEnabled()) {
      return;
    }
    const GuestBufferRecord buffer = GuestBufferRecordFor(record);
    REXLOG_INFO(
        "Refii renderer resource create kind={} guest=0x{:08X} "
        "object=0x{:08X} size={} stride_or_format={} serial={}",
        ResourceKindName(kind), buffer.object_address,
        buffer.gpu_address, buffer.byte_size,
        buffer.stride_or_index_format, record.serial);
    return;
  }
  if (IsShaderResourceKind(kind)) {
    const GuestShaderRecord shader = GuestShaderRecordFor(record);
    REXLOG_INFO(
        "Refii renderer resource create kind={} guest=0x{:08X} "
        "bytecode=0x{:08X} size={} hash=0x{:016X} serial={}",
        ResourceKindName(kind), shader.object_address,
        shader.bytecode_address, shader.bytecode_size, shader.bytecode_hash,
        record.serial);
    return;
  }
  if (kind == RendererResourceKind::kVertexDeclaration) {
    const GuestVertexDeclarationRecord declaration =
        GuestVertexDeclarationRecordFor(record);
    REXLOG_INFO(
        "Refii renderer resource create kind={} guest=0x{:08X} "
        "elements={} max_stream={} stream_mask=0x{:08X} hash=0x{:016X} "
        "serial={}",
        ResourceKindName(kind), declaration.object_address,
        declaration.element_count, declaration.max_stream,
        declaration.stream_mask, declaration.elements_hash, record.serial);
    return;
  }

  if (RendererVerboseTracingEnabled()) {
    const GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
    const GuestTextureRecord texture = GuestTextureRecordFor(record);
    const std::array<uint32_t, 6> packed =
        IsSurfaceResourceKind(kind) ? std::array<uint32_t, 6>{
                                          surface.render_target_info,
                                          surface.depth_stencil_info,
                                          surface.packed_extent,
                                          surface.d3d_format,
                                          surface.pitch_msaa,
                                          surface.byte_size}
                                    : texture.raw_fetch_dwords;
    REXLOG_INFO(
        "Refii renderer resource create kind={} guest=0x{:08X} "
        "parent=0x{:08X} aux=0x{:08X} packed28=0x{:08X} "
        "packed32=0x{:08X} packed36=0x{:08X} packed40=0x{:08X} "
        "packed44=0x{:08X} packed48=0x{:08X} serial={}",
        ResourceKindName(kind), object_address,
        parent_address, GuestResourceAuxiliaryAddress(record), packed[0],
        packed[1], packed[2], packed[3], packed[4], packed[5],
        record.serial);
  }
}

RendererResourceRecord& StoreGuestResourceLocked(RendererResourceRecord record,
                                                 bool count_create,
                                                 bool trace_create) {
  const uint32_t object_address = GuestObjectAddress(record);
  const uint32_t address =
      object_address != 0 ? object_address : record.guest_address;
  EnsureVertexDeclarationUploadPayload(record, g_observer.memory_base);
  auto existing_it = FableDirectGuestResourceRegistry().resources.find(address);
  if (existing_it != FableDirectGuestResourceRegistry().resources.end() &&
      GuestObjectKind(record) == RendererResourceKind::kVertexDeclaration &&
      record.upload_data.empty() && !existing_it->second.upload_data.empty()) {
    const GuestVertexDeclarationRecord incoming_declaration =
        GuestVertexDeclarationRecordFor(record);
    const GuestVertexDeclarationRecord existing_declaration =
        GuestVertexDeclarationRecordFor(existing_it->second);
    if (incoming_declaration.elements_hash != 0 &&
        incoming_declaration.elements_hash == existing_declaration.elements_hash &&
        incoming_declaration.elements_size == existing_declaration.elements_size) {
      record.upload_data = existing_it->second.upload_data;
      record.upload_generation = std::max(record.upload_generation,
                                          existing_it->second.upload_generation);
    }
  }
  auto [it, inserted] =
      FableDirectGuestResourceRegistry().resources.insert_or_assign(address, std::move(record));
  (void)inserted;
  FableDirectGuestResourceRegistry().last_resource = it->second;
  TrackZeroTextureRefreshCandidateLocked(it->second);
  RegisterTextureIdentityForRecordLocked(it->second);
  MarkResourceChangedLocked(it->second);
  UpdateDirectSnapshotCacheLocked(it->second);
  if (count_create) {
    ++FableDirectGuestResourceRegistry().creates;
    if (trace_create) {
      TraceResourceCreate(it->second);
    }
  }
  return it->second;
}

void TraceResourceDestroy(uint32_t guest_address,
                          const RendererResourceRecord* record) {
  if (!RendererResourceTracingEnabled() || !RendererVerboseTracingEnabled()) {
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
      SetGuestResourceParent(refreshed, parent_address);
      if (PopulateShaderRecordFromObject(refreshed, base)) {
        const GuestShaderRecord refreshed_shader =
            GuestShaderRecordFor(refreshed);
        const GuestShaderRecord existing_shader =
            GuestShaderRecordFor(*existing);
        if (refreshed_shader.bytecode_hash != existing_shader.bytecode_hash ||
            refreshed_shader.bytecode_address !=
                existing_shader.bytecode_address ||
            refreshed_shader.bytecode_size != existing_shader.bytecode_size ||
            refreshed_shader.bytecode_offset !=
                existing_shader.bytecode_offset) {
          refreshed.serial = ++g_observer.serial;
          *existing = refreshed;
          FableDirectGuestResourceRegistry().last_resource = refreshed;
          MarkResourceChangedLocked(*existing);
          return;
        }
      }
      if (parent_address != 0 && existing->parent_address != parent_address) {
        SetGuestResourceParent(*existing, parent_address);
        existing->serial = ++g_observer.serial;
        FableDirectGuestResourceRegistry().last_resource = *existing;
        MarkResourceChangedLocked(*existing);
      }
      return;
    }
  }

  RendererResourceRecord record =
      CreateGuestShaderResource(kind, shader_address, parent_address);
  if (!PopulateShaderRecordFromObject(record, base)) {
    return;
  }

  record.serial = ++g_observer.serial;
  StoreGuestResourceLocked(std::move(record), true, true);
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
    const GuestShaderRecord existing_shader = GuestShaderRecordFor(*existing);
    if (existing->kind == kind &&
        existing_shader.bytecode_hash == shader_hash) {
      return;
    }
  }

  RendererResourceRecord record =
      CreateGuestShaderResource(kind, shader_address, parent_address);
  GuestShaderRecord shader;
  shader.object_address = shader_address;
  shader.bytecode_hash = shader_hash;
  ApplyGuestShaderRecord(record, shader);
  record.serial = ++g_observer.serial;
  StoreGuestResourceLocked(std::move(record), true, true);
}

void EnsureBoundShaderTrackedLocked(std::string_view symbol,
                                    const RendererHookArgs& args,
                                    uint8_t* base) {
  const RendererResourceKind kind =
      FableBoundShaderRendererResourceKind(symbol);
  if (kind == RendererResourceKind::kUnknown) {
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
      const uint64_t previous_hash =
          GuestVertexDeclarationRecordFor(*existing).elements_hash;
      if (GuestVertexDeclarationRecordFor(*existing).elements_hash == 0) {
        SnapshotGuestResource(*existing, base);
      }
      const GuestVertexDeclarationRecord refreshed_declaration =
          GuestVertexDeclarationRecordFor(*existing);
      if (previous_hash == 0 &&
          refreshed_declaration.elements_hash != 0 &&
          refreshed_declaration.elements_size != 0 &&
          refreshed_declaration.element_count != 0) {
        existing->serial = ++g_observer.serial;
        FableDirectGuestResourceRegistry().last_resource = *existing;
        RegisterShaderAndDeclarationIdentityForRecordLocked(*existing);
        MarkResourceChangedLocked(*existing);
        UpdateDirectSnapshotCacheLocked(*existing);
      }
      LogSuspiciousVertexDeclarationLocked(declaration_address, *existing, base);
      return;
    }
  }

  RendererResourceRecord record =
      CreateGuestVertexDeclarationResource(declaration_address, parent_address);
  SnapshotGuestResource(record, base);
  LogSuspiciousVertexDeclarationLocked(declaration_address, record, base);
  const GuestVertexDeclarationRecord declaration =
      GuestVertexDeclarationRecordFor(record);
  if (declaration.elements_hash == 0 &&
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
  if (declaration.elements_hash == 0) {
    return;
  }
  record.serial = ++g_observer.serial;
  StoreGuestResourceLocked(std::move(record), true, true);
}

bool HasPlausibleSurfaceHeader(const RendererResourceRecord& record) {
  const GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
  if (surface.packed_extent == 0 ||
      (surface.render_target_info == 0 && surface.depth_stencil_info == 0 &&
       surface.d3d_format == 0 && surface.byte_size == 0)) {
    return false;
  }

  const uint32_t utf16_path_words =
      (WordLooksLikeUtf16LePathText(surface.pitch_msaa) ? 1u : 0u) +
      (WordLooksLikeUtf16LePathText(surface.render_target_info) ? 1u : 0u) +
      (WordLooksLikeUtf16LePathText(surface.depth_stencil_info) ? 1u : 0u);
  if (utf16_path_words >= 2u) {
    return false;
  }
  const uint32_t utf16_descriptor_words =
      (WordLooksLikeUtf16LePathText(surface.packed_extent) ? 1u : 0u) +
      (WordLooksLikeUtf16LePathText(surface.d3d_format) ? 1u : 0u);
  if (utf16_path_words + utf16_descriptor_words >= 2u) {
    return false;
  }

  const uint32_t width = (surface.packed_extent >> 18u) + 1u;
  const uint32_t height = ((surface.packed_extent >> 3u) & 0x7FFFu) + 1u;
  if (!IsPlausibleFableSurfaceExtent(width, height)) {
    return false;
  }
  if (surface.pitch_msaa == 0 && surface.byte_size == 0) {
    return false;
  }

  // Fable frequently reuses transient 0x404D/0x404E scratch objects as small
  // wrapper/list nodes while D3D surface views are being suppressed. Those
  // nodes can accidentally decode as tiny surfaces because +24 contains a
  // small scalar, but they have scalar-looking format/size words rather than
  // usable RT/depth descriptor metadata for Plume.
  const uint64_t surface_pixels = static_cast<uint64_t>(width) * height;
  if (surface_pixels <= 16u && surface.pitch_msaa < 0x1000u &&
      surface.byte_size < 0x1000u && surface.d3d_format < 0x1000u) {
    return false;
  }
  if ((width == 1u || height == 1u) && surface_pixels <= 64u &&
      surface.pitch_msaa < 0x1000u && surface.byte_size < 0x1000u &&
      surface.d3d_format < 0x1000u) {
    return false;
  }

  if (surface.byte_size != 0) {
    const uint64_t max_expected_bytes =
        std::max<uint64_t>(4096u, surface_pixels * 16u);
    if (surface.byte_size > max_expected_bytes) {
      return false;
    }
  }

  if (surface.pitch_msaa != 0) {
    const uint32_t width_and_msaa = surface.pitch_msaa >> 16u;
    const uint32_t multisample_type = width_and_msaa & 0x3u;
    const uint32_t aligned_width = (width_and_msaa & ~0x3u) >> 2u;
    const uint32_t pitch_width = surface.pitch_msaa & 0x3FFFu;
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
  const uint32_t object_address = GuestObjectAddress(record);
  if (!base || object_address == 0 ||
      !IsSurfaceResourceKind(GuestObjectKind(record))) {
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
    ClearGuestResourceRawHeader(record);
    GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
    surface.render_target_info = render_target_info;
    surface.depth_stencil_info = depth_stencil_info;
    surface.packed_extent = packed_extent;
    surface.d3d_format = d3d_format;
    surface.pitch_msaa = pitch_msaa;
    surface.byte_size = byte_size;
    ApplyGuestSurfaceRecord(record, surface);
  };

  // sub_821F5028 builds the temporary render-target descriptors that Fable
  // uses in its Pix/video conversion path. They are not D3DSurface objects:
  // words 6..11 carry pitch/MSAA, RT info, depth info, extent, format, size.
  const uint32_t direct_pitch_msaa =
      LoadGuestU32(base, object_address + 0x18u);
  const uint32_t direct_rt_info =
      LoadGuestU32(base, object_address + 0x1Cu);
  const uint32_t direct_depth_info =
      LoadGuestU32(base, object_address + 0x20u);
  const uint32_t direct_extent =
      LoadGuestU32(base, object_address + 0x24u);
  const uint32_t direct_format =
      LoadGuestU32(base, object_address + 0x28u);
  const uint32_t direct_byte_size =
      LoadGuestU32(base, object_address + 0x2Cu);
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
      LoadGuestU32(base, object_address + 0x14u);
  const uint32_t pair_rt_info =
      LoadGuestU32(base, object_address + 0x1Cu);
  const uint32_t pair_extent =
      LoadGuestU32(base, object_address + 0x24u);
  const uint32_t pair_byte_size =
      LoadGuestU32(base, object_address + 0x2Cu);
  const bool linked_cells =
      LoadGuestU32(base, object_address + 0x00u) ==
          object_address + 0x08u &&
      LoadGuestU32(base, object_address + 0x08u) ==
          object_address + 0x10u &&
      LoadGuestU32(base, object_address + 0x10u) ==
          object_address + 0x18u;
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
  const GuestSurfaceRecord lhs_surface = GuestSurfaceRecordFor(lhs);
  const GuestSurfaceRecord rhs_surface = GuestSurfaceRecordFor(rhs);
  return GuestObjectKind(lhs) == GuestObjectKind(rhs) &&
         lhs_surface.object_address == rhs_surface.object_address &&
         lhs_surface.render_target_info == rhs_surface.render_target_info &&
         lhs_surface.depth_stencil_info == rhs_surface.depth_stencil_info &&
         lhs_surface.packed_extent == rhs_surface.packed_extent &&
         lhs_surface.d3d_format == rhs_surface.d3d_format &&
         lhs_surface.pitch_msaa == rhs_surface.pitch_msaa &&
         lhs_surface.byte_size == rhs_surface.byte_size;
}

void RememberRecentSurfaceShapeLocked(const RendererResourceRecord& record) {
  if (!IsSurfaceResourceKind(GuestObjectKind(record)) ||
      GuestObjectAddress(record) == 0 ||
      !HasPlausibleSurfaceHeader(record)) {
    return;
  }

  g_observer.recent_surface_shapes[GuestObjectAddress(record)] = record;
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
  const uint32_t object_address = GuestObjectAddress(record);
  if (!base || object_address == 0) {
    return false;
  }

  auto adopt_header = [&](const RendererResourceRecord& candidate,
                          uint32_t candidate_address) {
    // Only lift the Xenos surface descriptor words out of wrapper objects.
    // Fable often reuses the wrapper address for path/audio records later;
    // Plume only needs the packed shape, not the wrapper's object fields.
    ApplyGuestSurfaceRecord(record, GuestSurfaceRecordFor(candidate));
    if (candidate_address != object_address) {
      SetGuestResourceParent(record, candidate_address);
    }
  };

  const auto try_adopt_candidate = [&](uint32_t candidate_address) {
    if (candidate_address == 0 ||
        !IsLikelyFableObjectAddress(candidate_address)) {
      return false;
    }
    if (auto recent_it = g_observer.recent_surface_shapes.find(candidate_address);
        recent_it != g_observer.recent_surface_shapes.end() &&
        IsSurfaceResourceKind(GuestObjectKind(recent_it->second)) &&
        HasPlausibleSurfaceHeader(recent_it->second)) {
      adopt_header(recent_it->second, candidate_address);
      return true;
    }
    if (GuestWordsLookLikeInlinePathString(base, candidate_address)) {
      return false;
    }
    RendererResourceRecord candidate = record;
    SetGuestResourceIdentity(candidate, GuestObjectKind(candidate),
                             candidate_address,
                             GuestObjectParentAddress(candidate));
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
          IsSurfaceResourceKind(GuestObjectKind(recent_it->second)) &&
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

  if (try_adopt_graph(try_adopt_graph, object_address, 3)) {
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

  RendererResourceRecord record =
      CreateGuestSurfaceResource(kind, surface_address, parent_address);
  bool is_new = true;
  auto it = FindResourceIteratorLocked(surface_address);
  if (it != FableDirectGuestResourceRegistry().resources.end()) {
    record = it->second;
    is_new = false;
  }

  SetGuestResourceIdentity(record, kind, surface_address,
                           GuestObjectParentAddress(record) != 0
                               ? GuestObjectParentAddress(record)
                               : parent_address);
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

  const GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
  const bool surface_header_is_depth =
      FableSetSurfaceHeaderFormatIsDepth(surface.d3d_format);
  const bool ambiguous_color_depth_format =
      FableSurfaceHeaderFormatIsAmbiguousColorDepth(surface.d3d_format);
  if (kind == RendererResourceKind::kRenderTargetSurface &&
      surface_header_is_depth && !ambiguous_color_depth_format) {
    if (RendererResourceTracingEnabled() &&
        g_observer.logged_bound_surface_failures.insert(surface_address).second) {
      REXLOG_INFO(
          "Refii renderer rejected depth-format surface 0x{:08X} as color RT "
          "fmt=0x{:08X} rt=0x{:08X} depth=0x{:08X}",
          surface_address, surface.d3d_format, surface.render_target_info,
          surface.depth_stencil_info);
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
          surface_address, surface.d3d_format, surface.render_target_info,
          surface.depth_stencil_info);
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
  RendererResourceRecord& stored =
      StoreGuestResourceLocked(std::move(record), is_new, is_new);
  RememberRecentSurfaceShapeLocked(stored);
  return true;
}

void EnsureBoundVertexDeclarationTrackedLocked(std::string_view symbol,
                                               const RendererHookArgs& args,
                                               uint8_t* base) {
  if (!base) {
    return;
  }

  uint32_t declaration_address = 0;
  uint32_t parent_address = args.r3;
  switch (FableVertexDeclarationHookKindForSymbol(symbol)) {
    case FableVertexDeclarationHookKind::kSetVertexDeclaration:
      declaration_address = args.r4;
      break;
    case FableVertexDeclarationHookKind::kIncrementalShaderPatch:
      declaration_address = args.r7;
      TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r5,
                              declaration_address, base);
      break;
    case FableVertexDeclarationHookKind::kDirectShaderPatch:
      declaration_address = args.r5;
      TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r4,
                              declaration_address, base);
      break;
    case FableVertexDeclarationHookKind::kPatchVertexShaderDeclaration:
      declaration_address = args.r5;
      TrackShaderObjectLocked(RendererResourceKind::kVertexShader, args.r3,
                              declaration_address, base);
      break;
    case FableVertexDeclarationHookKind::kNone:
      return;
  }
  if (declaration_address == 0) {
    return;
  }

  TrackVertexDeclarationLocked(declaration_address, parent_address, base);
  FableDirectPatchPendingDrawEvents(
      [&](RendererDrawEventSnapshot& draw_event) {
    if (draw_event.vertex_declaration != 0) {
      return false;
    }
    draw_event.vertex_declaration = declaration_address;
      return true;
  });
}

void EnsureBoundShaderAndVertexDeclarationTrackedLocked(
    std::string_view symbol, const RendererHookArgs& args, uint8_t* base) {
  EnsureBoundShaderTrackedLocked(symbol, args, base);
  EnsureBoundVertexDeclarationTrackedLocked(symbol, args, base);
}

void UpsertScratchBufferResourceLocked(RendererResourceKind kind,
                                       uint32_t address, uint32_t size,
                                       uint32_t stride_or_format,
                                       uint32_t parent_address,
                                       uint32_t upload_source_address,
                                       bool attach_by_address) {
  if (address == 0 || size == 0) {
    return;
  }

  RendererResourceRecord& record = FableDirectGuestResourceRegistry().resources[address];
  const bool is_new = GuestObjectAddress(record) == 0;
  if (is_new) {
    record = CreateGuestBufferResource(kind, address, parent_address);
  }
  const RendererResourceKind previous_kind = GuestObjectKind(record);
  const GuestBufferRecord previous_buffer = GuestBufferRecordFor(record);
  const uint32_t previous_size = previous_buffer.byte_size;
  const uint32_t previous_stride_or_format =
      previous_buffer.stride_or_index_format;
  const uint32_t previous_upload_source = record.scratch_upload_source_address;
  const bool previous_scratch = record.scratch;
  SetGuestResourceIdentity(record, kind, address, parent_address);
  if (upload_source_address != 0) {
    record.scratch_upload_source_address = upload_source_address;
  } else if (record.scratch_upload_source_address == 0) {
    record.scratch_upload_source_address = address;
  }
  GuestBufferRecord buffer = GuestBufferRecordFor(record);
  buffer.object_address = address;
  buffer.gpu_address = address;
  buffer.byte_size = size;
  buffer.stride_or_index_format = stride_or_format;
  buffer.scratch = true;
  ApplyGuestBufferRecord(record, buffer);
  const bool stride_or_format_changes_buffer_shape =
      kind != RendererResourceKind::kVertexBuffer &&
      previous_stride_or_format != stride_or_format;
  const bool shape_changed =
      !previous_scratch || previous_kind != kind || previous_size != size ||
      stride_or_format_changes_buffer_shape ||
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
  RefreshQueuedDrawBufferResourceSerialLocked(record);
  if (attach_by_address && !record.upload_data.empty() &&
      HasNonZeroUploadData(record)) {
    FableDirectAttachScratchBufferByAddress(
        kind == RendererResourceKind::kIndexBuffer, address, stride_or_format,
        record.upload_data.data(), record.upload_data.size());
  }
  StoreGuestResourceLocked(record, is_new, is_new);
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

  RendererResourceRecord& record = FableDirectGuestResourceRegistry().resources[address];
  const bool is_new = GuestObjectAddress(record) == 0;
  if (is_new) {
    record = CreateGuestBufferResource(kind, address, parent_address);
  }
  SetGuestResourceIdentity(record, kind, address, parent_address);
  record.scratch_upload_source_address = 0;
  GuestBufferRecord buffer = GuestBufferRecordFor(record);
  buffer.object_address = address;
  buffer.gpu_address = address;
  buffer.byte_size = size;
  buffer.stride_or_index_format = stride_or_format;
  buffer.scratch = true;
  ApplyGuestBufferRecord(record, buffer);
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
  RefreshQueuedDrawBufferResourceSerialLocked(record);
  StoreGuestResourceLocked(record, is_new, is_new);
  FableDirectAttachScratchBufferByAddress(
      kind == RendererResourceKind::kIndexBuffer, address, stride_or_format,
      record.upload_data.data(), record.upload_data.size());
}

void TrimScratchBufferResourcesLocked() {
  size_t scratch_count = 0;
  for (const auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    if (record.scratch) {
      ++scratch_count;
    }
  }
  if (scratch_count <= kMaxScratchBufferResources) {
    return;
  }

  while (scratch_count > kMaxScratchBufferResources) {
    auto oldest = FableDirectGuestResourceRegistry().resources.end();
    for (auto it = FableDirectGuestResourceRegistry().resources.begin(); it != FableDirectGuestResourceRegistry().resources.end();
         ++it) {
      if (!it->second.scratch) {
        continue;
      }
      if (FableDirectDrawEventsReferenceResource(
              DirectResourceReferenceForRecord(it->second))) {
        continue;
      }
      if (oldest == FableDirectGuestResourceRegistry().resources.end() ||
          it->second.serial < oldest->second.serial) {
        oldest = it;
      }
    }
    if (oldest == FableDirectGuestResourceRegistry().resources.end()) {
      return;
    }
    const uint32_t destroyed_address = GuestObjectAddress(oldest->second);
    FableDirectGuestResourceRegistry().resources.erase(oldest);
    ++FableDirectGuestResourceRegistry().destroys;
    ++g_observer.serial;
    MarkResourceDestroyedLocked(destroyed_address, g_observer.serial);
    --scratch_count;
  }
}

void AttachScratchBufferPayloadToDrawCommandLocked(
    uint64_t draw_serial, RendererResourceKind kind, uint32_t address) {
  if (draw_serial == 0 || address == 0) {
    return;
  }
  const auto it = FableDirectGuestResourceRegistry().resources.find(address);
  if (it == FableDirectGuestResourceRegistry().resources.end() || it->second.upload_data.empty()) {
    return;
  }
  const GuestBufferRecord buffer = GuestBufferRecordFor(it->second);
  FableDirectAttachDrawScratchBuffer(
      draw_serial, kind == RendererResourceKind::kIndexBuffer, address,
      buffer.stride_or_index_format, it->second.upload_data.data(),
      it->second.upload_data.size());
}

void PatchDrawEventScratchBuffersLocked(uint64_t draw_serial,
                                        uint32_t vertex_buffer,
                                        uint32_t vertex_stride,
                                        uint32_t index_buffer) {
  const RendererStreamBindingSnapshot stream0 = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride = vertex_stride,
  };

  if (draw_serial == 0) {
    return;
  }

  FableDirectPatchDrawEvent(draw_serial, [&](RendererDrawEventSnapshot& event) {
    event.streams[0] = stream0;
    if (index_buffer != 0) {
      event.index_buffer = index_buffer;
    }
    event.resource_serial = g_observer.serial;
    RefreshDrawEventResourceSerialFromBindingsLocked(event);
    return true;
  });
  AttachScratchBufferPayloadToDrawCommandLocked(
      draw_serial, RendererResourceKind::kVertexBuffer, vertex_buffer);
  if (index_buffer != 0) {
    AttachScratchBufferPayloadToDrawCommandLocked(
        draw_serial, RendererResourceKind::kIndexBuffer, index_buffer);
  }
}

void ApplyDrawEventScratchBuffersLocked(RendererDrawEventSnapshot& event,
                                        uint32_t vertex_buffer,
                                        uint32_t vertex_stride,
                                        uint32_t index_buffer) {
  event.streams[0] = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride = vertex_stride,
  };
  if (index_buffer != 0) {
    event.index_buffer = index_buffer;
  }
  event.resource_serial = g_observer.serial;
  RefreshDrawEventResourceSerialFromBindingsLocked(event);
}

bool IsFableBeginVerticesWrapperSymbol(std::string_view symbol);
bool IsFableBeginVerticesWrapperReturnAddress(uint32_t lr);

struct FableBeginIndexedWrapperScratchSlots {
  uint32_t frame_size = 0;
  uint32_t vertex_out_offset = 0;
  uint32_t index_out_offset = 0;
};

struct FableBeginIndexedWrapperUploadSources {
  uint32_t vertex_source = 0;
  uint32_t index_source = 0;
  uint32_t vertex_size = 0;
  uint32_t index_size = 0;
};

bool IsFableBeginVerticesWrapperSymbol(std::string_view symbol) {
  return symbol == "sub_821EBF30" || symbol == "sub_82242C58" ||
         symbol == "sub_82A61860" || symbol == "sub_82B667F0";
}

bool IsFableBeginVerticesWrapperReturnAddress(uint32_t lr) {
  switch (lr) {
    case 0x821EC164u:
    case 0x82242DB0u:
    case 0x82A61BE8u:
    case 0x82B66830u:
      return true;
    default:
      return false;
  }
}

uint32_t FableBeginVerticesWrapperSymbolAddressForReturn(uint32_t lr) {
  switch (lr) {
    case 0x821EC164u:
      return 0x821EBF30u;
    case 0x82242DB0u:
      return 0x82242C58u;
    case 0x82A61BE8u:
      return 0x82A61860u;
    case 0x82B66830u:
      return 0x82B667F0u;
    default:
      return 0;
  }
}

uint64_t FableBeginVerticesWrapperContextKey(uint64_t call_serial,
                                             uint32_t symbol_address,
                                             const RendererHookArgs& args,
                                             uint32_t frame_index) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(call_serial);
  mix(symbol_address);
  mix(frame_index);
  mix(args.r3);
  mix(args.r4);
  mix(args.r5);
  mix(args.r6);
  mix(args.lr);
  return key;
}

uint64_t FableRootDrawSubmitContextKey(uint64_t call_serial,
                                       uint32_t symbol_address,
                                       const RendererHookArgs& args,
                                       const RendererLastDrawSnapshot& draw,
                                       uint32_t frame_index) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(call_serial);
  mix(symbol_address);
  mix(frame_index);
  mix(args.r3);
  mix(args.r4);
  mix(args.r5);
  mix(args.r6);
  mix(args.r7);
  mix(args.lr);
  mix(draw.indexed ? 1u : 0u);
  mix(draw.primitive_type);
  mix(draw.base_vertex);
  mix(draw.start);
  mix(draw.primitive_count);
  return key;
}

void StampRootDrawSubmitIdentityLocked(RendererDrawEventSnapshot& event,
                                       std::string_view symbol,
                                       const RendererHookArgs& args) {
  if (event.source != RendererDrawEventSource::kD3DWrapper ||
      FableDrawSubmitKindForSymbol(symbol) !=
          FableDrawSubmitKind::kDrawPrimitive) {
    return;
  }
  if (event.private_texture_wrapper_call_serial != 0 ||
      event.private_texture_wrapper_context_key != 0 ||
      event.private_texture_wrapper_symbol_address != 0) {
    return;
  }

  const uint32_t symbol_address = FableExactStateSymbolAddress(symbol);
  if (symbol_address == 0) {
    return;
  }

  const uint64_t call_serial = ++g_observer.serial;
  event.private_texture_wrapper_call_serial = call_serial;
  event.private_texture_wrapper_context_key = FableRootDrawSubmitContextKey(
      call_serial, symbol_address, args, event.draw,
      CurrentPlumeOutputFrameIndex());
  event.private_texture_wrapper_symbol_address = symbol_address;

  static uint32_t logged_root_draw_submit_identities = 0;
  if (logged_root_draw_submit_identities < 24u) {
    REXLOG_INFO(
        "Refii renderer exact root DrawPrimitive identity #{} "
        "serial={} call={} context=0x{:016X} symbol=0x{:08X} "
        "prim={} start={} count={} lr=0x{:08X} rt=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X} tex0=0x{:08X} tex13=0x{:08X}",
        ++logged_root_draw_submit_identities, event.serial,
        static_cast<unsigned long long>(event.private_texture_wrapper_call_serial),
        static_cast<unsigned long long>(
            event.private_texture_wrapper_context_key),
        event.private_texture_wrapper_symbol_address, event.draw.primitive_type,
        event.draw.start, event.draw.primitive_count, args.lr,
        event.render_targets[0], event.vertex_shader, event.pixel_shader,
        event.textures[0], event.textures[13]);
  }
}

void StampBeginVerticesWrapperIdentityLocked(RendererDrawEventSnapshot& event,
                                             const RendererHookArgs& args) {
  const uint32_t symbol_address =
      FableBeginVerticesWrapperSymbolAddressForReturn(args.lr);
  if (symbol_address == 0 || event.source != RendererDrawEventSource::kSceneObject ||
      event.draw.indexed || args.r4 != 8u || args.r5 != 3u || args.r6 != 8u) {
    return;
  }
  if (event.private_texture_wrapper_call_serial != 0 ||
      event.private_texture_wrapper_context_key != 0 ||
      event.private_texture_wrapper_symbol_address != 0) {
    return;
  }

  const uint64_t call_serial = ++g_observer.serial;
  event.private_texture_wrapper_call_serial = call_serial;
  event.private_texture_wrapper_context_key =
      FableBeginVerticesWrapperContextKey(
          call_serial, symbol_address, args, CurrentPlumeOutputFrameIndex());
  event.private_texture_wrapper_symbol_address = symbol_address;

  static uint32_t logged_begin_vertices_wrapper_identities = 0;
  if (logged_begin_vertices_wrapper_identities < 24u) {
    REXLOG_INFO(
        "Refii renderer exact BeginVertices wrapper identity #{} "
        "serial={} call={} context=0x{:016X} symbol=0x{:08X} "
        "prim={} count={} stride={} rt=0x{:08X} tex2=0x{:08X} tex13=0x{:08X}",
        ++logged_begin_vertices_wrapper_identities, event.serial,
        static_cast<unsigned long long>(event.private_texture_wrapper_call_serial),
        static_cast<unsigned long long>(
            event.private_texture_wrapper_context_key),
        event.private_texture_wrapper_symbol_address, args.r4, args.r5, args.r6,
        event.render_targets[0], event.textures[2], event.textures[13]);
  }
}

FableBeginIndexedWrapperScratchSlots FableBeginIndexedWrapperScratchSlotsForSymbol(
    std::string_view symbol) {
  if (symbol == "sub_821D7550") {
    return {
        .frame_size = 0xF0u,
        .vertex_out_offset = 0x60u,
        .index_out_offset = 0x64u,
    };
  }
  if (symbol == "sub_8220A3F0") {
    return {
        .frame_size = 0xA0u,
        .vertex_out_offset = 0x60u,
        .index_out_offset = 0x64u,
    };
  }
  if (symbol == "sub_8222E120") {
    return {
        .frame_size = 0xB0u,
        .vertex_out_offset = 0x60u,
        .index_out_offset = 0x64u,
    };
  }
  if (symbol == "sub_82AA59B8") {
    return {
        .frame_size = 0x170u,
        .vertex_out_offset = 0x68u,
        .index_out_offset = 0x70u,
    };
  }
  return {};
}

uint32_t AlignGuestCopySize4(uint32_t size) {
  return (size + 3u) & ~3u;
}

uint32_t MaxIndexInGuestBuffer(uint8_t* base, uint32_t address, uint32_t size,
                               uint32_t index_stride) {
  if (!base || address == 0 || size == 0 ||
      (index_stride != 2u && index_stride != 4u) ||
      (!IsLikelyGpuByteBufferRange(address, size) &&
       !IsLikelyHighScratchByteBufferRange(address, size))) {
    return UINT32_MAX;
  }

  const uint32_t index_count = size / index_stride;
  uint32_t max_index = 0;
  bool has_index = false;
  for (uint32_t i = 0; i < index_count; ++i) {
    const uint32_t index_address = address + i * index_stride;
    const uint32_t index =
        index_stride == 4u ? LoadGuestU32(base, index_address)
                           : LoadGuestU16(base, index_address);
    max_index = has_index ? std::max(max_index, index) : index;
    has_index = true;
  }
  return has_index ? max_index : UINT32_MAX;
}

uint32_t PreviewGuestDword(uint8_t* base, uint32_t address,
                           uint32_t dword_index) {
  const uint32_t byte_offset = dword_index * 4u;
  if (!base || address > UINT32_MAX - byte_offset ||
      !IsLikelyGuestDwordAddress(address + byte_offset)) {
    return 0;
  }
  return LoadGuestU32(base, address + byte_offset);
}

uint32_t PreviewGuestIndex(uint8_t* base, uint32_t address,
                           uint32_t index_stride, uint32_t index) {
  if (!base || (index_stride != 2u && index_stride != 4u)) {
    return 0;
  }
  const uint64_t byte_offset = uint64_t(index) * uint64_t(index_stride);
  if (byte_offset > UINT32_MAX || address > UINT32_MAX - byte_offset) {
    return 0;
  }
  const uint32_t index_address = address + static_cast<uint32_t>(byte_offset);
  if (index_stride == 4u) {
    return IsLikelyGuestDwordAddress(index_address)
               ? LoadGuestU32(base, index_address)
               : 0;
  }
  return (IsLikelyGpuByteBufferRange(index_address, sizeof(uint16_t)) ||
          IsLikelyHighScratchByteBufferRange(index_address, sizeof(uint16_t)))
             ? LoadGuestU16(base, index_address)
             : 0;
}

FableBeginIndexedWrapperUploadSources FableBeginIndexedWrapperUploadSourcesForSymbol(
    std::string_view symbol, const RendererHookArgs& wrapper_args,
    const RendererHookArgs& begin_args, uint32_t local_sp,
    uint32_t vertex_size, uint32_t index_size, uint32_t index_stride) {
  FableBeginIndexedWrapperUploadSources sources = {
      .vertex_source = 0,
      .index_source = 0,
      .vertex_size = vertex_size,
      .index_size = index_size,
  };

  if (symbol == "sub_821D7550") {
    sources.vertex_source = 0x83317E00u;
    sources.vertex_size = 0x80u;
    sources.index_source = local_sp + 0x70u;
    sources.index_size = AlignGuestCopySize4(index_size);
    return sources;
  }

  if (symbol == "sub_8220A3F0") {
    sources.vertex_source = wrapper_args.r6;
    const uint64_t wrapper_vertex_size =
        uint64_t(wrapper_args.r3) * uint64_t(wrapper_args.r7);
    sources.vertex_size =
        wrapper_args.r3 != 0 && wrapper_args.r7 != 0 &&
                wrapper_vertex_size <= std::numeric_limits<uint32_t>::max()
            ? static_cast<uint32_t>(wrapper_vertex_size)
            : begin_args.r6 * begin_args.r9;
    sources.index_source = wrapper_args.r5;
    const uint64_t wrapper_index_count = uint64_t(wrapper_args.r4) * 3u;
    const uint64_t wrapper_index_size = wrapper_index_count * index_stride;
    sources.index_size =
        wrapper_args.r4 != 0 &&
                wrapper_index_size <= std::numeric_limits<uint32_t>::max()
            ? AlignGuestCopySize4(static_cast<uint32_t>(wrapper_index_size))
            : AlignGuestCopySize4(begin_args.r7 * index_stride);
    return sources;
  }

  if (symbol == "sub_8222E120") {
    sources.vertex_source = wrapper_args.r10 + wrapper_args.r5 * begin_args.r9;
    sources.vertex_size = begin_args.r6 * begin_args.r9;
    sources.index_source = wrapper_args.r8;
    sources.index_size = AlignGuestCopySize4(begin_args.r7 * index_stride);
    return sources;
  }

  if (symbol == "sub_82AA59B8") {
    sources.vertex_source = local_sp + 0x90u;
    sources.vertex_size = 0x18u;
    sources.index_source = local_sp + 0x64u;
    sources.index_size = 4u;
    return sources;
  }

  return sources;
}

bool CaptureIndexedScratchWrapperCopyLocked(std::string_view symbol,
                                            const RendererHookArgs& args,
                                            uint8_t* base) {
  if (!base || !g_observer.active_wrapped_draw_indexed_scratch ||
      (g_observer.active_wrapped_draw_serial == 0 &&
       !g_observer.active_wrapped_draw_pending_materialization)) {
    return false;
  }

  const FableBeginIndexedWrapperScratchSlots slots =
      FableBeginIndexedWrapperScratchSlotsForSymbol(symbol);
  if (slots.frame_size == 0 || args.r1 < slots.frame_size) {
    return false;
  }

  const uint32_t local_sp = args.r1 - slots.frame_size;
  const uint32_t vertex_out_slot = local_sp + slots.vertex_out_offset;
  const uint32_t index_out_slot = local_sp + slots.index_out_offset;
  if (!IsLikelyGuestDwordAddress(vertex_out_slot) ||
      !IsLikelyGuestDwordAddress(index_out_slot)) {
    return false;
  }

  const RendererHookArgs& begin_args = g_observer.active_wrapped_draw_args;
  const uint32_t vertex_buffer = LoadGuestU32(base, vertex_out_slot);
  const uint32_t index_buffer = LoadGuestU32(base, index_out_slot);
  uint32_t vertex_count = begin_args.r6;
  uint32_t index_count = begin_args.r7;
  uint32_t vertex_stride = begin_args.r9;
  uint32_t index_stride = (begin_args.r8 & 4u) != 0 ? 4u : 2u;
  if (symbol == "sub_8220A3F0" && args.r3 != 0 && args.r4 != 0 &&
      args.r7 != 0 && args.r4 <= (std::numeric_limits<uint32_t>::max() / 3u)) {
    vertex_count = args.r3;
    index_count = args.r4 * 3u;
    vertex_stride = args.r7;
    index_stride = 2u;
  }
  const uint64_t vertex_size64 =
      uint64_t(vertex_count) * uint64_t(vertex_stride);
  const uint64_t index_size64 =
      uint64_t(index_count) * uint64_t(index_stride);
  if (!IsLikelyScratchBufferAddress(vertex_buffer) ||
      !IsLikelyScratchBufferAddress(index_buffer) || vertex_stride == 0 ||
      vertex_size64 == 0 || index_size64 == 0 ||
      vertex_size64 > std::numeric_limits<uint32_t>::max() ||
      index_size64 > std::numeric_limits<uint32_t>::max()) {
    return false;
  }

  uint32_t vertex_size = static_cast<uint32_t>(vertex_size64);
  uint32_t index_size = static_cast<uint32_t>(index_size64);
  const FableBeginIndexedWrapperUploadSources upload_sources =
      FableBeginIndexedWrapperUploadSourcesForSymbol(
          symbol, args, begin_args, local_sp, vertex_size, index_size,
          index_stride);
  uint32_t vertex_upload_source =
      upload_sources.vertex_source != 0 ? upload_sources.vertex_source
                                        : vertex_buffer;
  uint32_t index_upload_source =
      upload_sources.index_source != 0 ? upload_sources.index_source
                                       : index_buffer;
  uint32_t vertex_upload_size =
      upload_sources.vertex_size != 0 ? upload_sources.vertex_size
                                      : vertex_size;
  uint32_t index_upload_size =
      upload_sources.index_size != 0 ? upload_sources.index_size : index_size;
  uint32_t max_index =
      MaxIndexInGuestBuffer(base, index_upload_source, index_upload_size,
                            index_stride);
  if (max_index == UINT32_MAX && index_buffer != index_upload_source) {
    max_index =
        MaxIndexInGuestBuffer(base, index_buffer, index_size, index_stride);
  }
  if (max_index != UINT32_MAX && vertex_stride != 0 &&
      max_index < (std::numeric_limits<uint32_t>::max() / vertex_stride)) {
    const uint32_t required_vertex_size = (max_index + 1u) * vertex_stride;
    if (required_vertex_size > vertex_size &&
        required_vertex_size <= (2u << 20)) {
      vertex_size = required_vertex_size;
    }
    if (required_vertex_size > vertex_upload_size &&
        required_vertex_size <= (2u << 20)) {
      vertex_upload_size = required_vertex_size;
    }
  }
  uint32_t vertex_probe_size =
      std::min<uint32_t>(vertex_upload_size, 64u * 1024u);
  uint32_t index_probe_size =
      std::min<uint32_t>(index_upload_size, 64u * 1024u);
  uint32_t vertex_source_nonzero =
      FindFirstNonZeroGuestByte(base, vertex_upload_source, vertex_upload_size,
                                vertex_probe_size);
  uint32_t index_source_nonzero =
      FindFirstNonZeroGuestByte(base, index_upload_source, index_upload_size,
                                index_probe_size);
  const uint32_t vertex_dest_probe_size =
      std::min<uint32_t>(vertex_size, 64u * 1024u);
  const uint32_t index_dest_probe_size =
      std::min<uint32_t>(index_size, 64u * 1024u);
  const uint32_t vertex_dest_nonzero =
      vertex_buffer != vertex_upload_source
          ? FindFirstNonZeroGuestByte(base, vertex_buffer, vertex_size,
                                      vertex_dest_probe_size)
          : vertex_source_nonzero;
  const uint32_t index_dest_nonzero =
      index_buffer != index_upload_source
          ? FindFirstNonZeroGuestByte(base, index_buffer, index_size,
                                      index_dest_probe_size)
          : index_source_nonzero;
  if (vertex_source_nonzero == UINT32_MAX &&
      vertex_dest_nonzero != UINT32_MAX) {
    vertex_upload_source = vertex_buffer;
    vertex_upload_size = vertex_size;
    vertex_probe_size = vertex_dest_probe_size;
    vertex_source_nonzero = vertex_dest_nonzero;
  }
  if (index_source_nonzero == UINT32_MAX && index_dest_nonzero != UINT32_MAX) {
    index_upload_source = index_buffer;
    index_upload_size = index_size;
    index_probe_size = index_dest_probe_size;
    index_source_nonzero = index_dest_nonzero;
  }
  const bool vertex_has_payload = vertex_source_nonzero != UINT32_MAX;
  const bool index_has_payload = index_source_nonzero != UINT32_MAX;
  if (!vertex_has_payload || !index_has_payload) {
    static uint32_t logged_empty_indexed_wrapper_sources = 0;
    if (logged_empty_indexed_wrapper_sources < 24u) {
      REXLOG_INFO(
          "Refii renderer indexed scratch wrapper capture skipped hook={} "
          "serial={} dev=0x{:08X} vtx_count={} idx_count={} stride={} "
          "idx_stride={} vb=0x{:08X}/{} src=0x{:08X}/{} nz={} dst_nz={} "
          "ib=0x{:08X}/{} src=0x{:08X}/{} nz={} dst_nz={} max_index={} "
          "args={:08X},{:08X},{:08X},{:08X},{:08X}",
          symbol, g_observer.active_wrapped_draw_serial,
          g_observer.active_wrapped_draw_device, vertex_count, index_count,
          vertex_stride, index_stride, vertex_buffer, vertex_size,
          vertex_upload_source, vertex_upload_size,
          vertex_source_nonzero == UINT32_MAX ? -1
                                              : static_cast<int32_t>(
                                                    vertex_source_nonzero),
          vertex_dest_nonzero == UINT32_MAX
              ? -1
              : static_cast<int32_t>(vertex_dest_nonzero),
          index_buffer, index_size, index_upload_source, index_upload_size,
          index_source_nonzero == UINT32_MAX
              ? -1
              : static_cast<int32_t>(index_source_nonzero),
          index_dest_nonzero == UINT32_MAX
              ? -1
              : static_cast<int32_t>(index_dest_nonzero),
          max_index == UINT32_MAX ? -1 : static_cast<int32_t>(max_index),
          args.r3, args.r4, args.r5, args.r6, args.r7);
      ++logged_empty_indexed_wrapper_sources;
    }
    return false;
  }

  const uint32_t direct_vertex_size =
      std::min(vertex_size, vertex_upload_size);
  const uint32_t direct_index_size = std::min(index_size, index_upload_size);
  std::vector<uint8_t> vertex_data;
  if (direct_vertex_size != 0) {
    vertex_data = CopyGuestScratchUploadForDirectInline(
        base, vertex_upload_source, direct_vertex_size,
        RendererResourceKind::kVertexBuffer, vertex_stride);
  }
  std::vector<uint8_t> index_data;
  if (direct_index_size != 0) {
    index_data = CopyGuestScratchUploadForDirectInline(
        base, index_upload_source, direct_index_size,
        RendererResourceKind::kIndexBuffer, index_stride);
  }
  if (!vertex_data.empty()) {
    UpsertSyntheticScratchBufferResourceLocked(
        RendererResourceKind::kVertexBuffer, vertex_buffer,
        vertex_data.data(), static_cast<uint32_t>(vertex_data.size()),
        vertex_stride, g_observer.active_wrapped_draw_device);
  }
	  if (!index_data.empty()) {
	    UpsertSyntheticScratchBufferResourceLocked(
	        RendererResourceKind::kIndexBuffer, index_buffer,
	        index_data.data(), static_cast<uint32_t>(index_data.size()),
	        index_stride, g_observer.active_wrapped_draw_device);
	  }
	  MaterializePendingIndexedWrapperDrawLocked(vertex_buffer, vertex_stride,
	                                             index_buffer, symbol);
	  PatchDrawEventScratchBuffersLocked(g_observer.active_wrapped_draw_serial,
	                                     vertex_buffer, vertex_stride,
	                                     index_buffer);
  bool direct_vertex_attached = false;
  bool direct_index_attached = false;
  if (!vertex_data.empty()) {
    direct_vertex_attached = FableDirectAttachDrawScratchBuffer(
        g_observer.active_wrapped_draw_serial, false, vertex_buffer,
        vertex_stride, vertex_data.data(), vertex_data.size());
  }
  if (!index_data.empty()) {
    direct_index_attached = FableDirectAttachDrawScratchBuffer(
        g_observer.active_wrapped_draw_serial, true, index_buffer,
        index_stride, index_data.data(), index_data.size());
  }
  TrimScratchBufferResourcesLocked();

  static uint32_t logged_indexed_wrapper_sources = 0;
  if ((RendererDrawTracingEnabled() || index_count >= 96u ||
       vertex_count >= 64u) &&
      logged_indexed_wrapper_sources < 48u) {
    REXLOG_INFO(
        "Refii renderer indexed scratch wrapper capture hook={} serial={} "
        "dev=0x{:08X} vtx_count={} idx_count={} stride={} idx_stride={} "
        "captured=0x{:08X}/{}:{} from=0x{:08X}/{} nz={} dst_nz={} "
        "0x{:08X}/{}:{} from=0x{:08X}/{} nz={} dst_nz={} max_index={} "
        "slots=0x{:08X}/0x{:08X}",
        symbol, g_observer.active_wrapped_draw_serial,
        g_observer.active_wrapped_draw_device, vertex_count, index_count,
        vertex_stride, index_stride, vertex_buffer, vertex_size,
        vertex_has_payload ? 1 : 0, vertex_upload_source, vertex_upload_size,
        vertex_source_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(vertex_source_nonzero),
        vertex_dest_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(vertex_dest_nonzero),
        index_buffer, index_size, index_has_payload ? 1 : 0,
        index_upload_source, index_upload_size,
        index_source_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(index_source_nonzero),
        index_dest_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(index_dest_nonzero),
        max_index == UINT32_MAX ? -1 : static_cast<int32_t>(max_index),
        vertex_out_slot,
        index_out_slot);
    ++logged_indexed_wrapper_sources;
  }
  static uint32_t logged_indexed_wrapper_direct_attach = 0;
  if ((index_count >= 96u || vertex_count >= 64u) &&
      logged_indexed_wrapper_direct_attach < 48u) {
    REXLOG_INFO(
        "Refii renderer indexed scratch wrapper direct attach hook={} "
        "serial={} vb=0x{:08X}/{}:{} ib=0x{:08X}/{}:{} max_index={} "
        "sizes={}/{}",
        symbol, g_observer.active_wrapped_draw_serial, vertex_buffer,
        direct_vertex_size, direct_vertex_attached ? 1 : 0, index_buffer,
        direct_index_size, direct_index_attached ? 1 : 0,
        max_index == UINT32_MAX ? -1 : static_cast<int32_t>(max_index),
        vertex_size, index_size);
    ++logged_indexed_wrapper_direct_attach;
  }
  static uint32_t logged_small_indexed_wrapper_sources = 0;
  const bool small_indexed_wrapper = vertex_count <= 16u && index_count <= 12u;
  const bool suspicious_index_wrapper =
      max_index != UINT32_MAX && vertex_count != 0 && max_index >= vertex_count;
  if ((small_indexed_wrapper || suspicious_index_wrapper) &&
      logged_small_indexed_wrapper_sources < 96u) {
    REXLOG_INFO(
        "Refii renderer small indexed wrapper capture #{} hook={} serial={} "
        "src={} draw={} prim={} count={} begin={:08X},{:08X},{:08X},"
        "{:08X},{:08X},{:08X},{:08X},{:08X}/lr={:08X} wrap={:08X},"
        "{:08X},{:08X},{:08X},{:08X},{:08X},{:08X},{:08X}/lr={:08X} "
        "local=0x{:08X} slots=0x{:08X}/0x{:08X} vb=0x{:08X}/{} "
        "stride={} src=0x{:08X}/{} attach={} nz={}/{} vd={:08X},"
        "{:08X},{:08X},{:08X} vs={:08X},{:08X},{:08X},{:08X} "
        "ib=0x{:08X}/{} fmt={} src=0x{:08X}/{} attach={} nz={}/{} "
        "max={} id={},{},{},{} is={},{},{},{}",
        ++logged_small_indexed_wrapper_sources, symbol,
        g_observer.active_wrapped_draw_serial,
        static_cast<uint32_t>(g_observer.active_wrapped_draw_source),
        g_observer.active_wrapped_draw_draw.indexed ? 1u : 0u,
        g_observer.active_wrapped_draw_draw.primitive_type,
        g_observer.active_wrapped_draw_draw.primitive_count, begin_args.r3,
        begin_args.r4, begin_args.r5, begin_args.r6, begin_args.r7,
        begin_args.r8, begin_args.r9, begin_args.r10, begin_args.lr, args.r3,
        args.r4, args.r5, args.r6, args.r7, args.r8, args.r9, args.r10,
        args.lr, local_sp, vertex_out_slot, index_out_slot, vertex_buffer,
        vertex_size, vertex_stride, vertex_upload_source, vertex_upload_size,
        direct_vertex_attached ? 1u : 0u,
        vertex_source_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(vertex_source_nonzero),
        vertex_dest_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(vertex_dest_nonzero),
        PreviewGuestDword(base, vertex_buffer, 0),
        PreviewGuestDword(base, vertex_buffer, 1),
        PreviewGuestDword(base, vertex_buffer, 2),
        PreviewGuestDword(base, vertex_buffer, 3),
        PreviewGuestDword(base, vertex_upload_source, 0),
        PreviewGuestDword(base, vertex_upload_source, 1),
        PreviewGuestDword(base, vertex_upload_source, 2),
        PreviewGuestDword(base, vertex_upload_source, 3), index_buffer,
        index_size, index_stride, index_upload_source, index_upload_size,
        direct_index_attached ? 1u : 0u,
        index_source_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(index_source_nonzero),
        index_dest_nonzero == UINT32_MAX
            ? -1
            : static_cast<int32_t>(index_dest_nonzero),
        max_index == UINT32_MAX ? -1 : static_cast<int32_t>(max_index),
        PreviewGuestIndex(base, index_buffer, index_stride, 0),
        PreviewGuestIndex(base, index_buffer, index_stride, 1),
        PreviewGuestIndex(base, index_buffer, index_stride, 2),
        PreviewGuestIndex(base, index_buffer, index_stride, 3),
        PreviewGuestIndex(base, index_upload_source, index_stride, 0),
        PreviewGuestIndex(base, index_upload_source, index_stride, 1),
        PreviewGuestIndex(base, index_upload_source, index_stride, 2),
        PreviewGuestIndex(base, index_upload_source, index_stride, 3));
  }

  FableDirectCaptureExactNonVideoRenderCommands(
      g_observer.active_wrapped_draw_serial);
  return vertex_has_payload && index_has_payload;
}

bool CaptureBeginVerticesWrapperCopyLocked(std::string_view symbol,
                                           uint8_t* base) {
  if (!base || !IsFableBeginVerticesWrapperSymbol(symbol) ||
      g_observer.active_wrapped_draw_indexed_scratch ||
      g_observer.active_wrapped_draw_serial == 0 ||
      g_observer.active_wrapped_draw_device == 0) {
    return false;
  }

  const RendererHookArgs& begin_args = g_observer.active_wrapped_draw_args;
  if (begin_args.r3 == 0 || begin_args.r5 == 0 || begin_args.r6 == 0) {
    return false;
  }

  const uint32_t vertex_buffer =
      LoadGuestU32(base, g_observer.active_wrapped_draw_device + 0x3488u);
  const uint32_t vertex_stride = begin_args.r6;
  const uint64_t vertex_size64 =
      uint64_t(begin_args.r5) * uint64_t(vertex_stride);
  if (!IsLikelyScratchBufferAddress(vertex_buffer) || vertex_stride == 0 ||
      vertex_size64 == 0 ||
      vertex_size64 > std::numeric_limits<uint32_t>::max()) {
    return false;
  }

  const uint32_t vertex_size = static_cast<uint32_t>(vertex_size64);
  uint32_t vertex_upload_source = vertex_buffer;
  uint32_t vertex_upload_size = vertex_size;
  if (symbol == "sub_821EBF30") {
    // This Fable helper constructs the primitive8 screen rectangle in a
    // persistent three-float2 staging buffer, then copies it into the transient
    // BeginVertices allocation. The transient allocation often still contains
    // stale non-zero bytes when the hook observes it, so capture the actual
    // staging source that IDA shows feeding the copy.
    vertex_upload_source = 0x834C3DB8u;
    vertex_upload_size = std::min<uint32_t>(vertex_upload_size, 24u);
  } else if (symbol == "sub_82B667F0") {
    vertex_upload_source = 0x834C55B8u;
  }
  const uint32_t vertex_probe_size =
      std::min<uint32_t>(vertex_upload_size, 64u * 1024u);
  const bool vertex_has_payload =
      FindFirstNonZeroGuestByte(base, vertex_upload_source, vertex_upload_size,
                                vertex_probe_size) != UINT32_MAX;

  std::vector<uint8_t> direct_vertex_data;
  bool direct_vertex_attached = false;
  if (vertex_has_payload) {
    const uint32_t direct_vertex_size =
        std::min<uint32_t>(vertex_size, vertex_upload_size);
    direct_vertex_data = CopyGuestScratchUploadForDirectInline(
        base, vertex_upload_source, direct_vertex_size,
        RendererResourceKind::kVertexBuffer, vertex_stride);
    if (!direct_vertex_data.empty()) {
      direct_vertex_attached = FableDirectAttachDrawScratchBuffer(
          g_observer.active_wrapped_draw_serial, false, vertex_buffer,
          vertex_stride, direct_vertex_data.data(), direct_vertex_data.size());
    }
  }

  PatchDrawEventScratchBuffersLocked(g_observer.active_wrapped_draw_serial,
                                     vertex_buffer, vertex_stride, 0);
  if (vertex_has_payload) {
    UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                      vertex_buffer, vertex_size,
                                      vertex_stride,
                                      g_observer.active_wrapped_draw_device,
                                      vertex_upload_source);
    PatchDrawEventScratchBuffersLocked(g_observer.active_wrapped_draw_serial,
                                       vertex_buffer, vertex_stride, 0);
    TrimScratchBufferResourcesLocked();
  }

  if (RendererDrawTracingEnabled() &&
      g_observer.logged_indexed_scratch_captures < 24u) {
    REXLOG_INFO(
        "Refii renderer nonindexed scratch wrapper capture hook={} serial={} "
        "dev=0x{:08X} prim={} vtx_count={} stride={} captured=0x{:08X}/{}:{}",
        symbol, g_observer.active_wrapped_draw_serial,
        g_observer.active_wrapped_draw_device, begin_args.r4, begin_args.r5,
        vertex_stride, vertex_buffer, vertex_size,
        direct_vertex_attached ? 1 : (vertex_has_payload ? 1 : 0));
    ++g_observer.logged_indexed_scratch_captures;
  }

  if (vertex_has_payload) {
    FableDirectCaptureExactNonVideoRenderCommands(
        g_observer.active_wrapped_draw_serial);
  }
  return vertex_has_payload;
}

bool CaptureScratchDrawBuffersLocked(std::string_view symbol,
                                     const RendererHookArgs& args,
                                     uint32_t result, uint8_t* base) {
  if (!base || args.r3 == 0) {
    return false;
  }
  g_observer.memory_base = base;

  if (symbol == "sub_82205F68") {
    if (IsDrawVerticesUpInnerScratchSubmit(args)) {
      return false;
    }
    const uint32_t vertex_buffer = result != 0
                                       ? result
                                       : LoadGuestU32(base, args.r3 + 0x3488);
    const uint32_t vertex_size = args.r5 * args.r6;
    const bool exact_screen_rect_wrapper =
        args.lr == 0x821EC164u && args.r4 == 8u && args.r5 == 3u &&
        args.r6 == 8u;
    const uint32_t vertex_upload_source =
        exact_screen_rect_wrapper ? 0x834C3DB8u : vertex_buffer;
    const uint32_t vertex_upload_size =
        exact_screen_rect_wrapper ? std::min<uint32_t>(vertex_size, 24u)
                                  : vertex_size;
    PatchDrawEventScratchBuffersLocked(g_observer.active_wrapped_draw_serial,
                                       vertex_buffer, args.r6, 0);
    if (vertex_buffer == 0 || vertex_size == 0 || vertex_upload_size == 0) {
      return false;
    }
    const uint32_t vertex_probe_size =
        std::min<uint32_t>(vertex_upload_size, 64u * 1024u);
    const bool vertex_has_payload =
        FindFirstNonZeroGuestByte(base, vertex_upload_source, vertex_upload_size,
                                  vertex_probe_size) != UINT32_MAX;
    if (!vertex_has_payload) {
      return false;
    }
    UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                      vertex_buffer, vertex_size, args.r6,
                                      args.r3, vertex_upload_source);
    PatchDrawEventScratchBuffersLocked(g_observer.active_wrapped_draw_serial,
                                       vertex_buffer, args.r6, 0);
    AttachScratchBufferPayloadToDrawCommandLocked(
        g_observer.active_wrapped_draw_serial,
        RendererResourceKind::kVertexBuffer, vertex_buffer);
    if (exact_screen_rect_wrapper && RendererDrawTracingEnabled() &&
        g_observer.logged_indexed_scratch_captures < 24u) {
      REXLOG_INFO(
          "Refii renderer nonindexed scratch begin capture hook=sub_821EBF30 "
          "serial={} dev=0x{:08X} prim={} vtx_count={} stride={} "
          "captured=0x{:08X}/{}:{} source=0x{:08X}/{}",
          g_observer.active_wrapped_draw_serial, args.r3, args.r4, args.r5,
          args.r6, vertex_buffer, vertex_size, 1, vertex_upload_source,
          vertex_upload_size);
      ++g_observer.logged_indexed_scratch_captures;
    }
    TrimScratchBufferResourcesLocked();
    return true;
  }

  if (symbol == "sub_82265CD8") {
    return false;
  }

  if (FableBeginIndexedWrapperScratchSlotsForSymbol(symbol).frame_size != 0) {
    return CaptureIndexedScratchWrapperCopyLocked(symbol, args, base);
  }

  if (IsFableBeginVerticesWrapperSymbol(symbol)) {
    return CaptureBeginVerticesWrapperCopyLocked(symbol, base);
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
    const uint32_t vertex_stride = args.r9;
    const uint32_t index_stride = (args.r8 & 4u) != 0 ? 4u : 2u;
    const uint64_t device_vertex_size =
        uint64_t(LoadGuestU32(base, args.r3 + 0x3490)) * 4u;
    const uint64_t device_index_size =
        uint64_t(LoadGuestU32(base, args.r3 + 0x3494)) * 4u;
    const uint64_t required_vertex_size =
        uint64_t(args.r6) * uint64_t(vertex_stride);
    const uint64_t required_index_size =
        uint64_t(args.r7) * uint64_t(index_stride);
    const uint64_t vertex_size64 =
        required_vertex_size != 0 ? required_vertex_size : device_vertex_size;
    const uint64_t index_size64 =
        required_index_size != 0 ? required_index_size : device_index_size;
    const uint32_t vertex_size =
        vertex_size64 <= std::numeric_limits<uint32_t>::max()
            ? static_cast<uint32_t>(vertex_size64)
            : 0u;
    const uint32_t index_size =
        index_size64 <= std::numeric_limits<uint32_t>::max()
            ? static_cast<uint32_t>(index_size64)
            : 0u;
    const bool vertex_buffer_valid =
        IsLikelyScratchBufferAddress(vertex_buffer) && vertex_size != 0;
    const bool index_buffer_valid =
        IsLikelyScratchBufferAddress(index_buffer) && index_size != 0;
    if (!vertex_buffer_valid || !index_buffer_valid) {
      if (RendererDrawTracingEnabled() &&
          g_observer.logged_indexed_scratch_captures < 8) {
        const uint32_t command_tail = LoadGuestU32(base, args.r3 + 0x3484);
        const uint32_t device_vertex_buffer =
            LoadGuestU32(base, args.r3 + 0x3488);
        const uint32_t device_index_buffer =
            LoadGuestU32(base, args.r3 + 0x348C);
        const uint32_t device_vertex_words =
            LoadGuestU32(base, args.r3 + 0x3490);
        const uint32_t device_index_words =
            LoadGuestU32(base, args.r3 + 0x3494);
        REXLOG_INFO(
            "Refii renderer indexed scratch capture skipped result=0x{:08X} "
            "dev=0x{:08X} prim={} vtx_count={} idx_count={} flags=0x{:08X} "
            "stride={} idx_out_ptr=0x{:08X}->0x{:08X} "
            "vtx_out_slot=0x{:08X}->0x{:08X}->0x{:08X} "
            "dev_tail=0x{:08X} dev_vb=0x{:08X} dev_ib=0x{:08X} "
            "dev_words={}/{} captured=0x{:08X}/{} 0x{:08X}/{}",
            result, args.r3, args.r4, args.r6, args.r7, args.r8, args.r9,
            args.r10,
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
      return false;
    }
    const uint32_t vertex_probe_size =
        std::min<uint32_t>(vertex_size, 64u * 1024u);
    const uint32_t index_probe_size =
        std::min<uint32_t>(index_size, 64u * 1024u);
    const bool vertex_has_payload =
        FindFirstNonZeroGuestByte(base, vertex_buffer, vertex_size,
                                  vertex_probe_size) != UINT32_MAX;
    const bool index_has_payload =
        FindFirstNonZeroGuestByte(base, index_buffer, index_size,
                                  index_probe_size) != UINT32_MAX;
    if (!vertex_has_payload || !index_has_payload) {
      // BeginIndexedVertices returns the transient scratch pointers before the
      // caller writes the vertex/index payload. Patch the draw binding now so
      // the queued command does not collapse to vb0=0, and register refreshable
      // scratch resources so a later hook pass can attach the filled payload
      // once it becomes visible in guest memory.
      PatchDrawEventScratchBuffersLocked(g_observer.active_wrapped_draw_serial,
                                         vertex_buffer, vertex_stride,
                                         index_buffer);
      UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                        vertex_buffer, vertex_size,
                                        vertex_stride, args.r3, vertex_buffer);
      UpsertScratchBufferResourceLocked(RendererResourceKind::kIndexBuffer,
                                        index_buffer, index_size, index_stride,
                                        args.r3, index_buffer);
      if ((RendererDrawTracingEnabled() ||
           g_observer.logged_indexed_scratch_captures < 8) &&
          g_observer.logged_indexed_scratch_captures < 8) {
        REXLOG_INFO(
            "Refii renderer indexed scratch capture delayed dev=0x{:08X} "
            "prim={} base={} vtx_count={} idx_count={} stride={} "
            "captured=0x{:08X}/{}:{} 0x{:08X}/{}:{}",
            args.r3, args.r4, args.r5, args.r6, args.r7, args.r9,
            vertex_buffer, vertex_size, vertex_has_payload ? 1 : 0,
            index_buffer, index_size, index_has_payload ? 1 : 0);
        ++g_observer.logged_indexed_scratch_captures;
      }
      return false;
    }
    uint32_t max_index = 0;
    uint32_t swapped_max_index = 0;
    bool index_range_valid = true;
    bool index_range_uses_swapped_bytes = false;
    if (args.r6 != 0 && args.r7 != 0 &&
        required_index_size <= index_size) {
      for (uint32_t i = 0; i < args.r7; ++i) {
        const uint32_t index_address =
            index_buffer + i * index_stride;
        max_index = std::max(max_index,
                             LoadGuestGpuIndex(base, index_address,
                                               index_stride, false));
        swapped_max_index =
            std::max(swapped_max_index,
                     LoadGuestGpuIndex(base, index_address, index_stride, true));
      }
      index_range_valid = max_index < args.r6;
      if (!index_range_valid && swapped_max_index < args.r6) {
        max_index = swapped_max_index;
        index_range_valid = true;
        index_range_uses_swapped_bytes = true;
      }
    }
    if (!index_range_valid) {
      if (g_observer.logged_indexed_scratch_captures < 8) {
        REXLOG_INFO(
            "Refii renderer indexed scratch capture waiting for valid IB "
            "dev=0x{:08X} prim={} base={} vtx_count={} idx_count={} "
            "stride={} idx_stride={} captured=0x{:08X}/{} 0x{:08X}/{} "
            "max_index={} swapped_max_index={}",
            args.r3, args.r4, args.r5, args.r6, args.r7, args.r9,
            index_stride, vertex_buffer, vertex_size, index_buffer,
            index_size, max_index, swapped_max_index);
        ++g_observer.logged_indexed_scratch_captures;
      }
      return false;
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
          "dev_words={}/{} captured=0x{:08X}/{} 0x{:08X}/{} "
          "idx_swap={}",
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
          index_buffer, index_size, index_range_uses_swapped_bytes ? 1 : 0);
      ++g_observer.logged_indexed_scratch_captures;
    }
    const uint32_t direct_vertex_size =
        static_cast<uint32_t>(std::min<uint64_t>(
            vertex_size, required_vertex_size != 0 ? required_vertex_size
                                                   : vertex_size));
    const uint32_t direct_index_size =
        static_cast<uint32_t>(std::min<uint64_t>(
            index_size, required_index_size != 0 ? required_index_size
                                                 : index_size));
    std::vector<uint8_t> vertex_data;
    if (direct_vertex_size != 0) {
      vertex_data = CopyGuestScratchUploadForDirectInline(
          base, vertex_buffer, direct_vertex_size,
          RendererResourceKind::kVertexBuffer, vertex_stride);
    }
    std::vector<uint8_t> index_data;
    if (direct_index_size != 0) {
      index_data = CopyGuestScratchUploadForDirectInline(
          base, index_buffer, direct_index_size,
          RendererResourceKind::kIndexBuffer, index_stride);
    }
    if (!vertex_data.empty()) {
      UpsertSyntheticScratchBufferResourceLocked(
          RendererResourceKind::kVertexBuffer, vertex_buffer,
          vertex_data.data(), static_cast<uint32_t>(vertex_data.size()),
          vertex_stride, args.r3);
    } else {
      UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                        vertex_buffer, vertex_size,
                                        vertex_stride, args.r3, vertex_buffer);
    }
    if (!index_data.empty()) {
      UpsertSyntheticScratchBufferResourceLocked(
          RendererResourceKind::kIndexBuffer, index_buffer, index_data.data(),
          static_cast<uint32_t>(index_data.size()), index_stride, args.r3);
    } else {
      UpsertScratchBufferResourceLocked(RendererResourceKind::kIndexBuffer,
                                        index_buffer, index_size, index_stride,
                                        args.r3, index_buffer);
    }
    MaterializePendingIndexedWrapperDrawLocked(vertex_buffer, vertex_stride,
                                               index_buffer, symbol);
    PatchDrawEventScratchBuffersLocked(g_observer.active_wrapped_draw_serial,
                                       vertex_buffer, vertex_stride,
                                       index_buffer);
    if (!vertex_data.empty()) {
      FableDirectAttachDrawScratchBuffer(
          g_observer.active_wrapped_draw_serial, false, vertex_buffer,
          vertex_stride, vertex_data.data(), vertex_data.size());
    }
    if (!index_data.empty()) {
      FableDirectAttachDrawScratchBuffer(
          g_observer.active_wrapped_draw_serial, true, index_buffer,
          index_stride, index_data.data(), index_data.size());
    }
    FableDirectCaptureExactNonVideoRenderCommands(
        g_observer.active_wrapped_draw_serial);
    TrimScratchBufferResourcesLocked();
    return true;
  }
  return false;
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
  const RendererLastDrawSnapshot last_draw = {
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
                                    vertex_stride, args.r3, vertex_buffer);
  const uint64_t draw_serial =
      QueueDrawEventLocked(args.r3, true, true,
                           RendererDrawEventSource::kDrawVerticesUp, last_draw);
  PatchDrawEventScratchBuffersLocked(draw_serial, vertex_buffer, vertex_stride,
                                     0);
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
  TraceDrawCall("sub_82265CD8", last_draw);
  TrimScratchBufferResourcesLocked();
}

bool IsPrivateTextureRectangleWrapperSymbol(std::string_view symbol) {
  return symbol == "sub_821ECD30" ||
         symbol == "sub_822326E0" || symbol == "sub_82263D28" ||
         symbol == "sub_8227F3B0" ||
         symbol == "sub_822A0A78" || symbol == "sub_822A0B08" ||
         symbol == "sub_822827B8" ||
         symbol == "sub_822C40C8" || symbol == "sub_822C4290" ||
         symbol == "sub_82A6E698" ||
         symbol == "sub_82A7FDE0" || symbol == "sub_82A7FF20" ||
         symbol == "sub_82A82FE8" ||
         symbol == "sub_82A6A458" || symbol == "sub_82A6A598" ||
         symbol == "sub_82A6AA90" ||
         symbol == "sub_8228AB70" || symbol == "sub_8228ACB8" ||
         symbol == "sub_82242108" || symbol == "sub_82206888";
}

bool IsPrivateTextureRectangleExactCallsiteReturnAddress(uint32_t lr) {
  switch (lr) {
    case 0x8227F510u: // sub_8227F3B0 intermediate rect draw body return
    case 0x8227F540u: // sub_8227F3B0 final rect draw body return
    case 0x822421C4u: // sub_82242108 sibling wrapper inner draw return
    case 0x822C4950u: // sub_822C4290 title/global draw body return
    case 0x822692B4u: // sub_82268C70 local v90 source
    case 0x822779ACu: // sub_82277380 local v65 source
    case 0x82282B6Cu: // sub_822827B8 dword_8331AC0C source + stack rects
    case 0x82B6F368u: // rex_lhRenderer_EndScene/dword_8331B13C source
      return true;
    default:
      return false;
  }
}

bool IsBatchedPrivateTextureRectangleDrawCallsite(uint32_t symbol_address) {
  return symbol_address == 0x8227F510u || symbol_address == 0x8227F540u;
}

bool IsPrivateTextureRectangleExactBodyDrawCallsite(uint32_t symbol_address) {
  return IsBatchedPrivateTextureRectangleDrawCallsite(symbol_address) ||
         IsPrivateTextureRectangleExactCallsiteReturnAddress(symbol_address);
}

bool PrivateTextureRectangleWrapperPublishesTargetAction(
    const PrivateTextureRectangleWrapperCapture& capture) {
  if (capture.render_target == 0 ||
      capture.render_target == capture.source_texture) {
    return false;
  }
  switch (capture.symbol_address) {
    case 0x822326E0u:
    case 0x8228ACB8u:
    case 0x82242108u:
    case 0x822827B8u:
    case 0x822C40C8u:
    case 0x822C4290u:
    case 0x82A7FDE0u:
    case 0x82A7FF20u:
      return true;
    default:
      return IsPrivateTextureRectangleExactBodyDrawCallsite(
          capture.symbol_address);
  }
}

bool IsPrivateTextureRectangleExactStackRectReturnAddress(uint32_t lr) {
  switch (lr) {
    case 0x82232770u: // sub_822326E0 stack copy of r5 rect dwords
    case 0x82282B6Cu: // sub_822827B8 stack rect/source-coordinate dwords
      return true;
    default:
      return false;
  }
}

uint32_t PrivateTextureRectangleWrapperAddress(std::string_view symbol) {
  if (symbol == "sub_821ECD30") {
    return 0x821ECD30u;
  }
  if (symbol == "sub_822326E0") {
    return 0x822326E0u;
  }
  if (symbol == "sub_82263D28") {
    return 0x82263D28u;
  }
  if (symbol == "sub_8227F3B0") {
    return 0x8227F3B0u;
  }
  if (symbol == "sub_822A0A78") {
    return 0x822A0A78u;
  }
  if (symbol == "sub_822A0B08") {
    return 0x822A0B08u;
  }
  if (symbol == "sub_822827B8") {
    return 0x822827B8u;
  }
  if (symbol == "sub_822C40C8") {
    return 0x822C40C8u;
  }
  if (symbol == "sub_822C4290") {
    return 0x822C4290u;
  }
  if (symbol == "sub_82A6E698") {
    return 0x82A6E698u;
  }
  if (symbol == "sub_82A7FDE0") {
    return 0x82A7FDE0u;
  }
  if (symbol == "sub_82A7FF20") {
    return 0x82A7FF20u;
  }
  if (symbol == "sub_82A82FE8") {
    return 0x82A82FE8u;
  }
  if (symbol == "sub_82A6A458") {
    return 0x82A6A458u;
  }
  if (symbol == "sub_82A6A598") {
    return 0x82A6A598u;
  }
  if (symbol == "sub_82A6AA90") {
    return 0x82A6AA90u;
  }
  if (symbol == "sub_8228ACB8") {
    return 0x8228ACB8u;
  }
  if (symbol == "sub_8228AB70") {
    return 0x8228AB70u;
  }
  if (symbol == "sub_82242108") {
    return 0x82242108u;
  }
  if (symbol == "sub_82206888") {
    return 0x82206888u;
  }
  return 0;
}

uint32_t PrivateTextureRectangleProducerAddress(std::string_view symbol,
                                                const RendererHookArgs& args) {
  if (symbol == "sub_82206888" &&
      IsPrivateTextureRectangleExactCallsiteReturnAddress(args.lr)) {
    return args.lr;
  }
  return PrivateTextureRectangleWrapperAddress(symbol);
}

bool PrivateTextureRectangleWrapperReadableRange(uint32_t guest_address,
                                                 uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  const uint32_t end_address = guest_address + byte_size - 1u;
  constexpr uint32_t kFableStaticDataAddressMin = 0x82000000u;
  constexpr uint32_t kFableStaticDataAddressMax = 0x84000000u;
  return (IsLikelyGuestDwordAddress(guest_address) &&
          IsLikelyGuestDwordAddress(end_address)) ||
         (guest_address >= kFableStaticDataAddressMin &&
          end_address < kFableStaticDataAddressMax);
}

bool PrivateTextureRectangleWrapperReadableDword(uint32_t guest_address) {
  if (guest_address > UINT32_MAX - sizeof(uint32_t)) {
    return false;
  }
  constexpr uint32_t kFableStaticDataAddressMin = 0x82000000u;
  constexpr uint32_t kFableStaticDataAddressMax = 0x84000000u;
  return IsLikelyGuestDwordAddress(guest_address) ||
         (guest_address >= kFableStaticDataAddressMin &&
          guest_address + sizeof(uint32_t) - 1u < kFableStaticDataAddressMax);
}

uint64_t PrivateTextureRectangleWrapperContextKey(
    const PrivateTextureRectangleWrapperCapture& capture) {
  uint64_t key = 1469598103934665603ull;
  auto mix = [&](uint64_t value) {
    key ^= value;
    key *= 1099511628211ull;
  };
  mix(capture.symbol_address);
  mix(capture.serial);
  mix(capture.frame_index);
  mix(capture.source_texture);
  mix(capture.arg0);
  mix(capture.arg1);
  mix(capture.arg2);
  mix(capture.rect_pointer);
  mix(capture.lr);
  return key;
}

void CopyPrivateTextureRectangleWrapperCaptureToActionLocked(
    FablePrivateTextureCopyAction& action,
    const PrivateTextureRectangleWrapperCapture& capture, bool source_exact) {
  action.wrapper_call_serial = capture.serial;
  action.wrapper_context_key = capture.context_key;
  action.wrapper_symbol_address = capture.symbol_address;
  action.wrapper_source_texture = capture.source_texture;
  action.wrapper_arg0 = capture.arg0;
  action.wrapper_arg1 = capture.arg1;
  action.wrapper_arg2 = capture.arg2;
  action.wrapper_rect_pointer = capture.rect_pointer;
  action.wrapper_rect_dwords = capture.rect_dwords;
  action.wrapper_setup_globals = capture.setup_globals;
  action.wrapper_source_exact = source_exact;
  action.vertex_shader_root_call_serial =
      capture.vertex_shader_root_call_serial;
  action.vertex_shader_root_context_key =
      capture.vertex_shader_root_context_key;
  action.vertex_shader_root_symbol_address =
      capture.vertex_shader_root_symbol_address;
  action.vertex_shader_root_lr = capture.vertex_shader_root_lr;
  action.pixel_shader_root_call_serial = capture.pixel_shader_root_call_serial;
  action.pixel_shader_root_context_key = capture.pixel_shader_root_context_key;
  action.pixel_shader_root_symbol_address =
      capture.pixel_shader_root_symbol_address;
  action.pixel_shader_root_lr = capture.pixel_shader_root_lr;
}

void CopyPrivateTextureRectangleWrapperActionFieldsLocked(
    FablePrivateTextureCopyAction& action,
    const FablePrivateTextureCopyAction& wrapper_action) {
  action.wrapper_call_serial = wrapper_action.wrapper_call_serial;
  action.wrapper_context_key = wrapper_action.wrapper_context_key;
  action.wrapper_symbol_address = wrapper_action.wrapper_symbol_address;
  action.wrapper_source_texture = wrapper_action.wrapper_source_texture;
  action.wrapper_arg0 = wrapper_action.wrapper_arg0;
  action.wrapper_arg1 = wrapper_action.wrapper_arg1;
  action.wrapper_arg2 = wrapper_action.wrapper_arg2;
  action.wrapper_rect_pointer = wrapper_action.wrapper_rect_pointer;
  action.wrapper_rect_dwords = wrapper_action.wrapper_rect_dwords;
  action.wrapper_setup_globals = wrapper_action.wrapper_setup_globals;
  action.wrapper_source_exact =
      wrapper_action.wrapper_source_texture != 0 &&
      wrapper_action.wrapper_source_texture == action.source_texture;
  if (action.vertex_shader_root_call_serial == 0 &&
      action.vertex_shader_root_context_key == 0 &&
      action.vertex_shader_root_symbol_address == 0 &&
      action.vertex_shader_root_lr == 0) {
    action.vertex_shader_root_call_serial =
        wrapper_action.vertex_shader_root_call_serial;
    action.vertex_shader_root_context_key =
        wrapper_action.vertex_shader_root_context_key;
    action.vertex_shader_root_symbol_address =
        wrapper_action.vertex_shader_root_symbol_address;
    action.vertex_shader_root_lr = wrapper_action.vertex_shader_root_lr;
  }
  if (action.pixel_shader_root_call_serial == 0 &&
      action.pixel_shader_root_context_key == 0 &&
      action.pixel_shader_root_symbol_address == 0 &&
      action.pixel_shader_root_lr == 0) {
    action.pixel_shader_root_call_serial =
        wrapper_action.pixel_shader_root_call_serial;
    action.pixel_shader_root_context_key =
        wrapper_action.pixel_shader_root_context_key;
    action.pixel_shader_root_symbol_address =
        wrapper_action.pixel_shader_root_symbol_address;
    action.pixel_shader_root_lr = wrapper_action.pixel_shader_root_lr;
  }
}

void RegisterPrivateTextureRectangleWrapperActionLocked(
    const PrivateTextureRectangleWrapperCapture& capture) {
  if (capture.source_texture == 0) {
    return;
  }

  FablePrivateTextureCopyAction action;
  action.source_slot = 0xFFFFFFFFu;
  action.source_texture = capture.source_texture;
  action.render_target = capture.render_target;
  action.depth_stencil = capture.depth_stencil;
  action.vertex_shader = capture.vertex_shader;
  action.pixel_shader = capture.pixel_shader;
  action.lr = capture.lr;
  action.frame_index = capture.frame_index;
  action.kind = FablePrivateTextureCopyKind::kColor;
  CopyPrivateTextureRectangleWrapperCaptureToActionLocked(action, capture, true);
  RegisterPrivateTextureCopyActionFromObserverLocked(action);

  auto publish_targeted_wrapper_action =
      [&](const char* label, uint32_t& log_counter) {
    FablePrivateTextureCopyAction targeted_action = action;
    targeted_action.target_surface = capture.render_target;
    targeted_action.sampled_source.kind =
        FableRegisteredSampleSourceKind::kGpuSurface;
    targeted_action.sampled_source.resource = targeted_action.target_surface;
    RegisterPrivateTextureCopyActionFromObserverLocked(targeted_action);
    ClaimExactTitleTextureBlankProbeFromRenderActionLocked(
        targeted_action, label ? label : "private-rect-wrapper");

    if (log_counter < 32u) {
      REXLOG_INFO(
          "Refii renderer exact private rect {} target action #{} "
          "call={} context=0x{:016X} symbol=0x{:08X} "
          "wrapper_source=0x{:08X}/{} setup={:08X}/{:08X}/{:08X}/{:08X} "
          "frame={} source=0x{:08X} target=0x{:08X} rt=0x{:08X} "
          "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} lr=0x{:08X}",
          label ? label : "wrapper", ++log_counter,
          static_cast<unsigned long long>(capture.serial),
          static_cast<unsigned long long>(capture.context_key),
          capture.symbol_address, targeted_action.wrapper_source_texture,
          targeted_action.wrapper_source_exact ? 1 : 0,
          targeted_action.wrapper_setup_globals[0],
          targeted_action.wrapper_setup_globals[1],
          targeted_action.wrapper_setup_globals[2],
          targeted_action.wrapper_setup_globals[3],
          capture.frame_index, capture.source_texture,
          targeted_action.target_surface, capture.render_target,
          capture.depth_stencil, capture.vertex_shader, capture.pixel_shader,
          capture.lr);
    }
  };

  if (!PrivateTextureRectangleWrapperPublishesTargetAction(capture)) {
    return;
  }

  constexpr uint32_t kPrivateTextureRectangleCompactWrapper = 0x822326E0u;
  constexpr uint32_t kPrivateTextureRectangleSetupFlushWrapper = 0x8228ACB8u;
  constexpr uint32_t kPrivateTextureRectangleSiblingWrapper = 0x82242108u;
  constexpr uint32_t kPrivateTextureRectangleStackGlobalWrapper = 0x822827B8u;
  constexpr uint32_t kPrivateTextureRectangleSourceObjectWrapper = 0x822C40C8u;
  constexpr uint32_t kPrivateTextureRectangleTitleGlobalWrapper = 0x822C4290u;
  constexpr uint32_t kPrivateTextureRectangleNativeDepthWrapper = 0x82A7FDE0u;
  constexpr uint32_t kPrivateTextureRectangleNativeDepthBatchWrapper =
      0x82A7FF20u;
  constexpr uint32_t kPrivateTextureRectangleLocalSourceCallsite = 0x822692B4u;
  constexpr uint32_t kPrivateTextureRectangleTitleLocalCallsite = 0x822779ACu;
  constexpr uint32_t kPrivateTextureRectangleSiblingCallsite = 0x822421C4u;
  constexpr uint32_t kPrivateTextureRectangleTitleGlobalCallsite = 0x822C4950u;
  constexpr uint32_t kPrivateTextureRectangleStackGlobalCallsite = 0x82282B6Cu;
  constexpr uint32_t kPrivateTextureRectangleEndSceneCallsite = 0x82B6F368u;
  if (IsBatchedPrivateTextureRectangleDrawCallsite(capture.symbol_address)) {
    static uint32_t logged_batched_targeted_actions = 0;
    publish_targeted_wrapper_action(capture.symbol_address == 0x8227F510u
                                        ? "batched-intermediate"
                                        : "batched-final",
                                    logged_batched_targeted_actions);
  } else if (capture.symbol_address == kPrivateTextureRectangleCompactWrapper) {
    static uint32_t logged_compact_targeted_actions = 0;
    publish_targeted_wrapper_action("compact",
                                    logged_compact_targeted_actions);
  } else if (capture.symbol_address ==
             kPrivateTextureRectangleSetupFlushWrapper) {
    static uint32_t logged_setup_flush_targeted_actions = 0;
    publish_targeted_wrapper_action("setup-flush",
                                    logged_setup_flush_targeted_actions);
  } else if (capture.symbol_address == kPrivateTextureRectangleSiblingWrapper) {
    // The outer sibling wrapper does not identify the command-owned sampled
    // texture. Keep the base action, and let the inner 0x822421C4 callsite
    // publish the exact target edge.
  } else if (capture.symbol_address ==
                 kPrivateTextureRectangleStackGlobalWrapper ||
             capture.symbol_address ==
                 kPrivateTextureRectangleStackGlobalCallsite) {
    static uint32_t logged_stack_global_targeted_actions = 0;
    publish_targeted_wrapper_action("stack-global",
                                    logged_stack_global_targeted_actions);
  } else if (capture.symbol_address ==
             kPrivateTextureRectangleSourceObjectWrapper) {
    static uint32_t logged_source_object_targeted_actions = 0;
    publish_targeted_wrapper_action("source-object",
                                    logged_source_object_targeted_actions);
  } else if (capture.symbol_address ==
             kPrivateTextureRectangleTitleGlobalWrapper) {
    static uint32_t logged_title_global_targeted_actions = 0;
    publish_targeted_wrapper_action("title-global",
                                    logged_title_global_targeted_actions);
  } else if (capture.symbol_address ==
             kPrivateTextureRectangleTitleGlobalCallsite) {
    static uint32_t logged_title_global_callsite_targeted_actions = 0;
    publish_targeted_wrapper_action(
        "title-global-callsite",
        logged_title_global_callsite_targeted_actions);
  } else if (capture.symbol_address ==
             kPrivateTextureRectangleSiblingCallsite) {
    static uint32_t logged_sibling_callsite_targeted_actions = 0;
    publish_targeted_wrapper_action(
        "sibling-callsite",
        logged_sibling_callsite_targeted_actions);
  } else if (capture.symbol_address ==
                 kPrivateTextureRectangleNativeDepthWrapper ||
             capture.symbol_address ==
                 kPrivateTextureRectangleNativeDepthBatchWrapper) {
    static uint32_t logged_native_depth_targeted_actions = 0;
    if (capture.depth_stencil != 0 &&
        capture.depth_stencil != capture.source_texture) {
      FablePrivateTextureCopyAction depth_action = action;
      depth_action.kind = FablePrivateTextureCopyKind::kDepth;
      depth_action.target_surface = capture.depth_stencil;
      depth_action.depth_stencil = capture.depth_stencil;
      depth_action.sampled_source.kind =
          FableRegisteredSampleSourceKind::kGpuSurface;
      depth_action.sampled_source.resource = depth_action.target_surface;
      RegisterPrivateTextureCopyActionFromObserverLocked(depth_action);
      ClaimExactTitleTextureBlankProbeFromRenderActionLocked(
          depth_action, "native-depth");
      if (logged_native_depth_targeted_actions < 32u) {
        REXLOG_INFO(
            "Refii renderer exact private rect native-depth target action #{} "
            "call={} context=0x{:016X} frame={} source=0x{:08X} "
            "target=0x{:08X} rt=0x{:08X} vs=0x{:08X} ps=0x{:08X} "
            "lr=0x{:08X}",
            ++logged_native_depth_targeted_actions,
            static_cast<unsigned long long>(capture.serial),
            static_cast<unsigned long long>(capture.context_key),
            capture.frame_index, capture.source_texture,
            depth_action.target_surface, capture.render_target,
            capture.vertex_shader, capture.pixel_shader, capture.lr);
      }
    }
  } else if (capture.symbol_address ==
                 kPrivateTextureRectangleLocalSourceCallsite ||
             capture.symbol_address ==
                 kPrivateTextureRectangleTitleLocalCallsite ||
             capture.symbol_address ==
                 kPrivateTextureRectangleEndSceneCallsite) {
    static uint32_t logged_body_callsite_targeted_actions = 0;
    publish_targeted_wrapper_action("body-callsite",
                                    logged_body_callsite_targeted_actions);
  }
}

void RefreshTitleGlobalPrivateTextureRectangleWrapperSourceLocked(
    PrivateTextureRectangleWrapperCapture& capture, uint8_t* base,
    const char* phase) {
  constexpr uint32_t kPrivateTextureRectangleTitleGlobalWrapper =
      0x822C4290u;
  constexpr uint32_t kTitlePrivateGlobalSourceTexture = 0x8331AA18u;
  if (!base ||
      capture.symbol_address != kPrivateTextureRectangleTitleGlobalWrapper ||
      !PrivateTextureRectangleWrapperReadableDword(
          kTitlePrivateGlobalSourceTexture)) {
    return;
  }

  const uint32_t source =
      LoadGuestU32(base, kTitlePrivateGlobalSourceTexture);
  if (source == 0 || source == capture.source_texture) {
    return;
  }

  const uint32_t previous_source = capture.source_texture;
  const uint64_t previous_context_key = capture.context_key;
  capture.source_texture = source;
  capture.context_key = PrivateTextureRectangleWrapperContextKey(capture);
  RegisterPrivateTextureRectangleWrapperActionLocked(capture);
  capture.joined =
      capture.joined ||
      PrivateTextureRectangleWrapperPublishesTargetAction(capture);

  static uint32_t logged_title_global_source_refreshes = 0;
  if (logged_title_global_source_refreshes < 32u) {
    REXLOG_INFO(
        "Refii renderer exact private rect title global source refresh #{} "
        "phase={} call={} context=0x{:016X} old_context=0x{:016X} "
        "frame={} old=0x{:08X} new=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X}",
        ++logged_title_global_source_refreshes, phase ? phase : "unknown",
        static_cast<unsigned long long>(capture.serial),
        static_cast<unsigned long long>(capture.context_key),
        static_cast<unsigned long long>(previous_context_key),
        capture.frame_index, previous_source, capture.source_texture,
        capture.render_target, capture.depth_stencil, capture.vertex_shader,
        capture.pixel_shader);
  }
}

void StampPrivateTextureRectangleWrapperEventLocked(
    RendererDrawEventSnapshot& event, uint32_t source_texture) {
  if (g_observer.active_private_texture_rectangle_wrappers.empty()) {
    return;
  }

  constexpr uint32_t kPrivateTextureRectangleDrawBodyAddress = 0x82206888u;
  auto selected = g_observer.active_private_texture_rectangle_wrappers.rend();
  bool selected_source_matches = false;
  for (auto it = g_observer.active_private_texture_rectangle_wrappers.rbegin();
       it != g_observer.active_private_texture_rectangle_wrappers.rend(); ++it) {
    if (it->symbol_address == kPrivateTextureRectangleDrawBodyAddress ||
        it->source_texture == 0 || source_texture == 0 ||
        it->source_texture != source_texture) {
      continue;
    }
    selected = it;
    selected_source_matches = true;
    break;
  }
  if (selected == g_observer.active_private_texture_rectangle_wrappers.rend()) {
    selected = g_observer.active_private_texture_rectangle_wrappers.rbegin();
  }
  event.private_texture_wrapper_call_serial = selected->serial;
  event.private_texture_wrapper_context_key = selected->context_key;
  event.private_texture_wrapper_symbol_address = selected->symbol_address;
  if (selected_source_matches) {
    event.private_texture_wrapper_source_texture = selected->source_texture;
    event.private_texture_wrapper_source_exact = selected->source_texture != 0;
  }
}

void LogPrivateTextureRectangleWrapperMissLocked(
    std::string_view symbol, const PrivateTextureRectangleWrapperCapture& capture) {
  PrivateTextureRectangleWrapperMissStats& stats =
      g_observer.private_texture_rectangle_wrapper_miss_stats[
          capture.symbol_address];
  ++stats.total;
  stats.last_frame_index = capture.frame_index;
  stats.last_source_texture = capture.source_texture;
  stats.last_lr = capture.lr;
  stats.last_call_serial = capture.serial;
  stats.last_context_key = capture.context_key;

  if (g_observer.logged_private_texture_rectangle_wrapper_misses < 32u) {
    REXLOG_INFO(
        "Refii renderer exact private rect wrapper/action miss #{} "
        "symbol={} producer=0x{:08X} name={} total_for_symbol={} call={} "
        "context=0x{:016X} frame={} source=0x{:08X} "
        "args=0x{:08X}/0x{:08X}/0x{:08X} lr=0x{:08X}",
        ++g_observer.logged_private_texture_rectangle_wrapper_misses, symbol,
        capture.symbol_address,
        FableHookAddressName(capture.symbol_address)
            ? FableHookAddressName(capture.symbol_address)
            : FableHookDisplayName(symbol).data(),
        static_cast<unsigned long long>(stats.total),
        static_cast<unsigned long long>(capture.serial),
        static_cast<unsigned long long>(capture.context_key),
        capture.frame_index, capture.source_texture, capture.arg0,
        capture.arg1, capture.arg2, capture.lr);
  }

  if (stats.logged < 8u) {
    REXLOG_INFO(
        "Refii renderer exact private rect wrapper/action miss by symbol #{} "
        "producer=0x{:08X} name={} hook={} total={} last_call={} "
        "last_context=0x{:016X} last_frame={} last_source=0x{:08X} "
        "last_lr=0x{:08X}",
        ++stats.logged, capture.symbol_address,
        FableHookAddressName(capture.symbol_address)
            ? FableHookAddressName(capture.symbol_address)
            : FableHookDisplayName(symbol).data(),
        symbol, static_cast<unsigned long long>(stats.total),
        static_cast<unsigned long long>(stats.last_call_serial),
        static_cast<unsigned long long>(stats.last_context_key),
        stats.last_frame_index, stats.last_source_texture, stats.last_lr);
  }
}

void CapturePrivateTextureRectangleWrapperEnterLocked(
    std::string_view symbol, const RendererHookArgs& args, uint8_t* base) {
  if (!base || !IsPrivateTextureRectangleWrapperSymbol(symbol)) {
    return;
  }
  if (symbol == "sub_82206888" && CurrentDirectPixelShaderIsBinkMovieLocked()) {
    return;
  }

  PrivateTextureRectangleWrapperCapture capture;
  capture.serial = ++g_observer.serial;
  capture.symbol_address = PrivateTextureRectangleProducerAddress(symbol, args);
  capture.arg0 = args.r3;
  capture.arg1 = args.r4;
  capture.arg2 = args.r5;
  capture.lr = args.lr;
  capture.frame_index = CurrentPlumeOutputFrameIndex();
  if (symbol == "sub_821ECD30") {
    constexpr uint32_t kActivePrivateSlotIndex = 0x83496E80u;
    constexpr uint32_t kActivePrivateSlotSources = 0x834A4444u;
    constexpr uint32_t kActivePrivateSlotStride = 96u;
    const uint32_t slot_index =
        PrivateTextureRectangleWrapperReadableDword(kActivePrivateSlotIndex)
            ? LoadGuestU32(base, kActivePrivateSlotIndex)
            : 0u;
    const uint64_t source_address =
        uint64_t(kActivePrivateSlotSources) +
        uint64_t(slot_index) * kActivePrivateSlotStride;
    capture.source_texture =
        source_address <= std::numeric_limits<uint32_t>::max() &&
                PrivateTextureRectangleWrapperReadableDword(
                    static_cast<uint32_t>(source_address))
            ? LoadGuestU32(base, static_cast<uint32_t>(source_address))
            : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_822326E0") {
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r3 + 4u)
                                 ? LoadGuestU32(base, args.r3 + 4u)
                                 : 0u;
    capture.rect_pointer = args.r5;
  } else if (symbol == "sub_82263D28") {
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r5 + 4u)
                                 ? LoadGuestU32(base, args.r5 + 4u)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_8227F3B0") {
    constexpr uint32_t kBatchedPrivateRectDefaultRectsOffset = 12752u;
    capture.source_texture = args.r6;
    capture.rect_pointer =
        args.r5 != 0 ? args.r5 : (args.r6 != 0
                                      ? args.r3 +
                                            kBatchedPrivateRectDefaultRectsOffset
                                      : 0u);
  } else if (symbol == "sub_822A0A78") {
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r3 + 4u)
                                 ? LoadGuestU32(base, args.r3 + 4u)
                                 : 0u;
    capture.rect_pointer = args.r5;
  } else if (symbol == "sub_822A0B08") {
    constexpr uint32_t kSourceFieldOffset = 584u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r3 + kSourceFieldOffset)
                                 ? LoadGuestU32(base,
                                                args.r3 + kSourceFieldOffset)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_822827B8") {
    constexpr uint32_t kStackGlobalSourceTexture = 0x8331AC0Cu;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 kStackGlobalSourceTexture)
                                 ? LoadGuestU32(base, kStackGlobalSourceTexture)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_822C40C8") {
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r4 + 4u)
                                 ? LoadGuestU32(base, args.r4 + 4u)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_822C4290") {
    constexpr uint32_t kTitlePrivateGlobalSourceTexture = 0x8331AA18u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 kTitlePrivateGlobalSourceTexture)
                                 ? LoadGuestU32(base,
                                                kTitlePrivateGlobalSourceTexture)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_82A6E698") {
    constexpr uint32_t kSourceFieldOffset = 284u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r3 + kSourceFieldOffset)
                                 ? LoadGuestU32(base,
                                                args.r3 + kSourceFieldOffset)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_82A7FDE0") {
    constexpr uint32_t kWrapperSharedSourceTexture = 0x8331A984u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 kWrapperSharedSourceTexture)
                                 ? LoadGuestU32(base,
                                                kWrapperSharedSourceTexture)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_82A7FF20") {
    constexpr uint32_t kWrapperSharedSourceTexture = 0x8331A984u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 kWrapperSharedSourceTexture)
                                 ? LoadGuestU32(base,
                                                kWrapperSharedSourceTexture)
                                 : 0u;
    const uint32_t rect_batch =
        PrivateTextureRectangleWrapperReadableDword(args.r4 + 20u)
            ? LoadGuestU32(base, args.r4 + 20u)
            : 0u;
    capture.rect_pointer = rect_batch != 0 ? rect_batch + 24u : 0u;
  } else if (symbol == "sub_82A82FE8") {
    constexpr uint32_t kSourceFieldOffset = 120u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r3 + kSourceFieldOffset)
                                 ? LoadGuestU32(base,
                                                args.r3 + kSourceFieldOffset)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_82A6A458" || symbol == "sub_82A6A598" ||
             symbol == "sub_82A6AA90") {
    constexpr uint32_t kWrapperPostSourceTexture = 0x8331AB30u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 kWrapperPostSourceTexture)
                                 ? LoadGuestU32(base,
                                                kWrapperPostSourceTexture)
                                 : 0u;
    capture.rect_pointer = 0u;
  } else if (symbol == "sub_82206888") {
    capture.source_texture = args.r6;
    capture.rect_pointer = args.r5;
  } else if (symbol == "sub_82242108") {
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r5 + 4u)
                                 ? LoadGuestU32(base, args.r5 + 4u)
                                 : 0u;
  } else if (symbol == "sub_8228AB70") {
    constexpr uint32_t kWrapperGlobalSourceTexture = 0x8331AA60u;
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 kWrapperGlobalSourceTexture)
                                 ? LoadGuestU32(base,
                                                kWrapperGlobalSourceTexture)
                                 : 0u;
  } else {
    capture.source_texture = PrivateTextureRectangleWrapperReadableDword(
                                 args.r5 + 4u)
                                 ? LoadGuestU32(base, args.r5 + 4u)
                                 : 0u;
  }

  if (PrivateTextureRectangleWrapperReadableRange(
          capture.rect_pointer,
          static_cast<uint32_t>(capture.rect_dwords.size() *
                                sizeof(uint32_t)))) {
    for (uint32_t i = 0; i < capture.rect_dwords.size(); ++i) {
      capture.rect_dwords[i] =
          LoadGuestU32(base, capture.rect_pointer + i * sizeof(uint32_t));
    }
  }
  if (symbol == "sub_8228ACB8") {
    constexpr uint32_t kSetupGlobals = 0x834A60B8u;
    for (uint32_t i = 0; i < capture.setup_globals.size(); ++i) {
      capture.setup_globals[i] =
          LoadGuestU32(base, kSetupGlobals + i * sizeof(uint32_t));
    }
  }

  const FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  capture.render_target = direct_snapshot.targets.color[0];
  capture.depth_stencil = direct_snapshot.targets.depth;
  capture.vertex_shader = direct_snapshot.shaders.vertex_shader;
  capture.vertex_shader_root_call_serial =
      direct_snapshot.shader_roots.vertex_shader.call_serial;
  capture.vertex_shader_root_context_key =
      direct_snapshot.shader_roots.vertex_shader.context_key;
  capture.vertex_shader_root_symbol_address =
      direct_snapshot.shader_roots.vertex_shader.symbol_address;
  capture.vertex_shader_root_lr = direct_snapshot.shader_roots.vertex_shader.lr;
  capture.pixel_shader = direct_snapshot.shaders.pixel_shader;
  capture.pixel_shader_root_call_serial =
      direct_snapshot.shader_roots.pixel_shader.call_serial;
  capture.pixel_shader_root_context_key =
      direct_snapshot.shader_roots.pixel_shader.context_key;
  capture.pixel_shader_root_symbol_address =
      direct_snapshot.shader_roots.pixel_shader.symbol_address;
  capture.pixel_shader_root_lr = direct_snapshot.shader_roots.pixel_shader.lr;

  capture.context_key = PrivateTextureRectangleWrapperContextKey(capture);
  RegisterPrivateTextureRectangleWrapperActionLocked(capture);
  capture.joined = PrivateTextureRectangleWrapperPublishesTargetAction(capture);
  constexpr uint32_t kPrivateTextureRectangleSiblingWrapper = 0x82242108u;
  constexpr uint32_t kPrivateTextureRectangleSiblingCallsite = 0x822421C4u;
  if (capture.joined &&
      capture.symbol_address == kPrivateTextureRectangleSiblingCallsite) {
    for (auto outer = g_observer.active_private_texture_rectangle_wrappers.rbegin();
         outer != g_observer.active_private_texture_rectangle_wrappers.rend();
         ++outer) {
      if (outer->symbol_address != kPrivateTextureRectangleSiblingWrapper ||
          outer->joined || outer->source_texture == 0 ||
          outer->source_texture != capture.source_texture) {
        continue;
      }
      outer->joined = true;
      break;
    }
  }
  if (capture.joined &&
      IsBatchedPrivateTextureRectangleDrawCallsite(capture.symbol_address)) {
    constexpr uint32_t kBatchedPrivateTextureRectangleWrapper = 0x8227F3B0u;
    for (auto it = g_observer.active_private_texture_rectangle_wrappers.rbegin();
         it != g_observer.active_private_texture_rectangle_wrappers.rend();
         ++it) {
      if (it->symbol_address != kBatchedPrivateTextureRectangleWrapper ||
          it->joined || it->source_texture == 0 ||
          it->source_texture != capture.source_texture) {
        continue;
      }
      it->joined = true;
      break;
    }
  }

  g_observer.active_private_texture_rectangle_wrappers.push_back(capture);
  constexpr size_t kMaxActivePrivateTextureRectangleWrappers = 16;
  if (g_observer.active_private_texture_rectangle_wrappers.size() >
      kMaxActivePrivateTextureRectangleWrappers) {
    g_observer.active_private_texture_rectangle_wrappers.erase(
        g_observer.active_private_texture_rectangle_wrappers.begin());
  }

  if (g_observer.logged_private_texture_rectangle_wrapper_captures < 32u) {
    REXLOG_INFO(
        "Refii renderer exact private rect wrapper capture #{} "
        "symbol={} call={} context=0x{:016X} frame={} source=0x{:08X} "
        "args=0x{:08X}/0x{:08X}/0x{:08X} rect=0x{:08X} "
        "rect_dwords={:08X}/{:08X}/{:08X}/{:08X} "
        "setup={:08X}/{:08X}/{:08X}/{:08X} rt=0x{:08X} ds=0x{:08X} "
        "vs=0x{:08X} ps=0x{:08X} lr=0x{:08X}",
        ++g_observer.logged_private_texture_rectangle_wrapper_captures,
        symbol, static_cast<unsigned long long>(capture.serial),
        static_cast<unsigned long long>(capture.context_key),
        capture.frame_index, capture.source_texture, capture.arg0,
        capture.arg1, capture.arg2, capture.rect_pointer,
        capture.rect_dwords[0], capture.rect_dwords[1],
        capture.rect_dwords[2], capture.rect_dwords[3],
        capture.setup_globals[0], capture.setup_globals[1],
        capture.setup_globals[2], capture.setup_globals[3],
        capture.render_target, capture.depth_stencil, capture.vertex_shader,
        capture.pixel_shader, capture.lr);
  }
}

void PopPrivateTextureRectangleWrapperExitLocked(std::string_view symbol,
                                                 const RendererHookArgs& args,
                                                 uint8_t* base) {
  if (!IsPrivateTextureRectangleWrapperSymbol(symbol) ||
      g_observer.active_private_texture_rectangle_wrappers.empty()) {
    return;
  }
  const uint32_t symbol_address =
      PrivateTextureRectangleProducerAddress(symbol, args);
  for (auto it = g_observer.active_private_texture_rectangle_wrappers.rbegin();
       it != g_observer.active_private_texture_rectangle_wrappers.rend(); ++it) {
    if (it->symbol_address != symbol_address) {
      continue;
    }
    RefreshTitleGlobalPrivateTextureRectangleWrapperSourceLocked(*it, base,
                                                                 "exit");
    constexpr uint32_t kPrivateTextureRectangleDrawBodyAddress = 0x82206888u;
    bool nested_draw_body_covered = false;
    if (it->symbol_address == kPrivateTextureRectangleDrawBodyAddress) {
      for (auto outer = std::next(it);
           outer !=
           g_observer.active_private_texture_rectangle_wrappers.rend();
           ++outer) {
        if (outer->symbol_address == kPrivateTextureRectangleDrawBodyAddress ||
            !outer->joined) {
          continue;
        }
        if (it->source_texture == 0 || outer->source_texture == 0 ||
            it->source_texture == outer->source_texture) {
          nested_draw_body_covered = true;
          break;
        }
      }
    }
    if (!it->joined && !nested_draw_body_covered) {
      LogPrivateTextureRectangleWrapperMissLocked(symbol, *it);
    }
    g_observer.active_private_texture_rectangle_wrappers.erase(
        std::next(it).base());
    return;
  }
}

void AttachPrivateTextureRectangleWrapperActionLocked(
    FablePrivateTextureCopyAction& action) {
  CopyRegisteredPrivateTextureWrapperActionFromRegistryLocked(action);

  const bool has_wrapper_identity =
      action.wrapper_call_serial != 0 || action.wrapper_context_key != 0;
  if (!has_wrapper_identity) {
    return;
  }

  if (g_observer.active_private_texture_rectangle_wrappers.empty()) {
    return;
  }
  auto attach_wrapper =
      [&](std::vector<PrivateTextureRectangleWrapperCapture>::reverse_iterator
              it,
          bool source_exact) {
        CopyPrivateTextureRectangleWrapperCaptureToActionLocked(action, *it,
                                                                source_exact);
        it->joined = true;
        if (IsBatchedPrivateTextureRectangleDrawCallsite(it->symbol_address)) {
          constexpr uint32_t kBatchedPrivateTextureRectangleWrapper =
              0x8227F3B0u;
          for (auto outer = std::next(it);
               outer !=
               g_observer.active_private_texture_rectangle_wrappers.rend();
               ++outer) {
            if (outer->symbol_address !=
                    kBatchedPrivateTextureRectangleWrapper ||
                outer->joined || outer->source_texture == 0 ||
                it->source_texture != outer->source_texture) {
              continue;
            }
            outer->joined = true;
            break;
          }
        }

        if (g_observer.logged_private_texture_rectangle_wrapper_joins < 32u) {
          REXLOG_INFO(
              "Refii renderer exact private rect wrapper/action join #{} "
              "wrapper=0x{:08X} call={} context=0x{:016X} frame={} "
              "wrapper_source=0x{:08X} action_source=0x{:08X} "
              "source_exact={} target=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
              "vs=0x{:08X} ps=0x{:08X} draw={} slot={} kind={}",
              ++g_observer.logged_private_texture_rectangle_wrapper_joins,
              it->symbol_address, static_cast<unsigned long long>(it->serial),
              static_cast<unsigned long long>(it->context_key),
              it->frame_index, it->source_texture, action.source_texture,
              source_exact ? 1 : 0, action.target_surface,
              action.render_target, action.depth_stencil,
              action.vertex_shader, action.pixel_shader,
              static_cast<unsigned long long>(action.draw_serial),
              action.source_slot, FablePrivateTextureCopyKindName(action.kind));
        }
      };

  for (auto it = g_observer.active_private_texture_rectangle_wrappers.rbegin();
       it != g_observer.active_private_texture_rectangle_wrappers.rend(); ++it) {
    if (action.wrapper_call_serial != 0 &&
        it->serial != action.wrapper_call_serial) {
      continue;
    }
    if (action.wrapper_context_key != 0 &&
        it->context_key != action.wrapper_context_key) {
      continue;
    }
    const bool source_exact =
        it->source_texture != 0 && it->source_texture == action.source_texture;
    attach_wrapper(it, source_exact);
    return;
  }
}

#include "runtime/plume/direct/fable_synthetic_draws.inl"

struct GuestByteProbeSummary {
  bool valid = false;
  uint32_t first_nonzero = UINT32_MAX;
  uint64_t hash = 0;
  std::array<uint8_t, 4> first4 = {};
};

GuestByteProbeSummary ProbeGuestBytes(uint8_t* base, uint32_t guest_address,
                                      uint32_t requested_size) {
  GuestByteProbeSummary summary;
  const bool readable_guest_range =
      IsLikelyGuestDwordAddress(guest_address) ||
      IsLikelyHighScratchByteBufferRange(guest_address, requested_size);
  if (!base || guest_address == 0 || requested_size == 0 ||
      guest_address > UINT32_MAX - requested_size ||
      !readable_guest_range) {
    return summary;
  }

  const uint32_t probe_size =
      std::min<uint32_t>(requested_size, 64u * 1024u);
  if (!IsReadableGuestByteRange(base, guest_address, probe_size)) {
    return summary;
  }

  const uint8_t* bytes =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  summary.valid = true;
  summary.first_nonzero =
      FindFirstNonZeroGuestByte(base, guest_address, probe_size, probe_size);
  summary.hash = HashGuestBytesFnv1a64(base, guest_address, probe_size);
  for (uint32_t i = 0; i < summary.first4.size() && i < probe_size; ++i) {
    summary.first4[i] = bytes[i];
  }
  return summary;
}

bool IsTitleSizedTextureTile(uint32_t width, uint32_t height) {
  return (width == 16u && height == 16u) ||
         (width == 64u && height == 64u) ||
         (width == 128u && height == 128u) ||
         (width == 280u && height == 180u) ||
         (width == 256u && height == 256u) ||
         (width == 480u && height == 270u) ||
         (width == 512u && height == 256u) ||
         (width == 560u && height == 360u) ||
         (width == 512u && height == 512u) ||
         (width == 960u && height == 540u) ||
         (width == 1024u && height == 512u) ||
         (width == 1280u && height == 720u) ||
         (width == 1920u && height == 1080u);
}

uint32_t TileHelperProbeSize(uint32_t width, uint32_t height, uint32_t pitch,
                             uint32_t bits_or_bytes) {
  const uint32_t bytes_per_pixel_or_block =
      std::clamp<uint32_t>(bits_or_bytes, 1u, 16u);
  uint64_t size = 0;
  if (pitch != 0 && height != 0) {
    size = static_cast<uint64_t>(pitch) * height;
  } else {
    size = static_cast<uint64_t>(width) * height * bytes_per_pixel_or_block;
  }
  if (size == 0) {
    size = 64u * 1024u;
  }
  return static_cast<uint32_t>(
      std::min<uint64_t>(size, kMaxRecentCpuTiledTextureUploadBytes));
}

bool ShouldLogTileHelperDeepProbe(uint32_t dst, uint32_t src, uint32_t width,
                                  uint32_t height) {
  if (IsFableTitleTextureProbeAddress(dst) ||
      IsFableTitleTextureProbeAddress(src)) {
    return true;
  }
  if (!IsTitleSizedTextureTile(width, height)) {
    return false;
  }
  return IsLikelyGuestDwordAddress(dst) || IsLikelyGuestDwordAddress(src) ||
         IsLikelyHighScratchByteBufferRange(dst, 1u) ||
         IsLikelyHighScratchByteBufferRange(src, 1u);
}

void LogTileHelperDeepProbeLocked(std::string_view phase,
                                  std::string_view symbol,
                                  const RendererHookArgs& args,
                                  uint8_t* base) {
  if ((!RendererVerboseTracingEnabled() && !RendererResourceTracingEnabled()) ||
      !base ||
      g_observer.logged_tile_helper_deep_probes >=
          kMaxTileHelperDeepProbeLogs) {
    return;
  }

  uint32_t dst = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t point = 0;
  uint32_t src = 0;
  uint32_t pitch = 0;
  uint32_t rect = 0;
  uint32_t bits = 0;
  uint32_t level = 0;
  uint32_t format = 0;
  uint32_t flags = 0;

  if (symbol == "sub_8300CB48") {
    // XGTileTextureLevel(width, height, level, format, flags, dst, point,
    // src, ..., source_pitch at caller stack +0x54, source_rect at +0x5c).
    width = args.r3;
    height = args.r4;
    level = args.r5;
    format = args.r6;
    flags = args.r7;
    dst = args.r8;
    point = args.r9;
    src = args.r10;
    pitch = LoadHookStackU32(base, args, 84u);
    rect = LoadHookStackU32(base, args, 92u);
  } else if (symbol == "sub_8300C290" || symbol == "sub_8300CB40") {
    // XGRAPHICS__TileSurface(dst, width, height, point, src, pitch, rect,
    // bytes_per_tile_unit).
    dst = args.r3;
    width = args.r4;
    height = args.r5;
    point = args.r6;
    src = args.r7;
    pitch = args.r8;
    rect = args.r9;
    bits = args.r10;
  } else {
    return;
  }

  const bool title_probe = ShouldLogTileHelperDeepProbe(dst, src, width, height);
  if (!title_probe && g_observer.logged_tile_helper_deep_probes >= 24u) {
    return;
  }

  const uint32_t source_probe_size =
      TileHelperProbeSize(width, height, pitch, bits);
  const uint32_t dest_probe_size =
      TileHelperProbeSize(width, height, 0, bits);
  const GuestByteProbeSummary source_summary =
      ProbeGuestBytes(base, src, source_probe_size);
  const GuestByteProbeSummary dest_summary =
      ProbeGuestBytes(base, dst, dest_probe_size);
  uint32_t original_source = 0;
  const char* original_source_kind = "none";
  if (symbol == "sub_8300CB48" && args.lr == 0x82B95FC0u &&
      IsLikelyGuestDwordAddress(args.r26)) {
    original_source = LoadGuestU32(base, args.r26) + args.r21;
    original_source_kind = "tex2d";
  } else if (symbol == "sub_8300CB48" && args.lr == 0x82B95C60u &&
             IsLikelyGuestDwordAddress(args.r19) &&
             IsLikelyGuestDwordAddress(args.r27)) {
    original_source =
        LoadGuestU32(base, args.r19) + LoadGuestU32(base, args.r27) + args.r20;
    original_source_kind = "cube";
  }
  const GuestByteProbeSummary original_summary =
      original_source != 0 ? ProbeGuestBytes(base, original_source,
                                             source_probe_size)
                           : GuestByteProbeSummary{};
  const uint32_t original_w0 =
      IsLikelyGuestDwordAddress(original_source)
          ? LoadGuestU32(base, original_source + 0u)
          : 0u;
  const uint32_t original_w1 =
      IsLikelyGuestDwordAddress(original_source + 4u)
          ? LoadGuestU32(base, original_source + 4u)
          : 0u;
  const uint32_t original_w2 =
      IsLikelyGuestDwordAddress(original_source + 8u)
          ? LoadGuestU32(base, original_source + 8u)
          : 0u;
  const uint32_t original_w3 =
      IsLikelyGuestDwordAddress(original_source + 12u)
          ? LoadGuestU32(base, original_source + 12u)
          : 0u;
  uint32_t original_payload = 0;
  if (original_source != 0 && original_w1 < 64u * 1024u * 1024u &&
      original_source <= UINT32_MAX - original_w1) {
    original_payload = original_source + original_w1;
  }
  const GuestByteProbeSummary payload_summary =
      original_payload != 0 ? ProbeGuestBytes(base, original_payload,
                                              source_probe_size)
                            : GuestByteProbeSummary{};
  const uint32_t rect0 =
      IsLikelyGuestDwordAddress(rect) ? LoadGuestU32(base, rect + 0u) : 0;
  const uint32_t rect1 =
      IsLikelyGuestDwordAddress(rect + 4u) ? LoadGuestU32(base, rect + 4u) : 0;
  const uint32_t rect2 =
      IsLikelyGuestDwordAddress(rect + 8u) ? LoadGuestU32(base, rect + 8u) : 0;
  const uint32_t rect3 =
      IsLikelyGuestDwordAddress(rect + 12u) ? LoadGuestU32(base, rect + 12u) : 0;

  REXLOG_INFO(
      "Refii renderer tile helper deep probe #{} phase={} hook={} "
      "lr=0x{:08X} dst=0x{:08X} src=0x{:08X} {}x{} pitch={} "
      "point=0x{:08X} rect=0x{:08X}[{},{},{},{}] bits={} level={} "
      "fmt={} flags=0x{:08X} src_nz={} src_hash64k=0x{:016X} "
      "src4={:02X} {:02X} {:02X} {:02X} dst_nz={} "
      "dst_hash64k=0x{:016X} dst4={:02X} {:02X} {:02X} {:02X} "
      "orig_kind={} orig=0x{:08X} ow={:08X}/{:08X}/{:08X}/{:08X} "
      "orig_nz={} orig_hash64k=0x{:016X} orig4={:02X} {:02X} {:02X} {:02X} "
      "payload=0x{:08X} payload_nz={} payload_hash64k=0x{:016X} "
      "payload4={:02X} {:02X} {:02X} {:02X} "
      "regs14_31={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
      "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/"
      "{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
      g_observer.logged_tile_helper_deep_probes + 1u, phase, symbol, args.lr,
      dst, src, width, height, pitch, point, rect, rect0, rect1, rect2, rect3,
      bits, level, format, flags,
      source_summary.valid
          ? (source_summary.first_nonzero == UINT32_MAX
                 ? -1
                 : static_cast<int>(source_summary.first_nonzero))
          : -2,
      source_summary.hash, source_summary.first4[0], source_summary.first4[1],
      source_summary.first4[2], source_summary.first4[3],
      dest_summary.valid
          ? (dest_summary.first_nonzero == UINT32_MAX
                 ? -1
                 : static_cast<int>(dest_summary.first_nonzero))
          : -2,
      dest_summary.hash, dest_summary.first4[0], dest_summary.first4[1],
      dest_summary.first4[2], dest_summary.first4[3], original_source_kind,
      original_source, original_w0, original_w1, original_w2, original_w3,
      original_summary.valid
          ? (original_summary.first_nonzero == UINT32_MAX
                 ? -1
                 : static_cast<int>(original_summary.first_nonzero))
          : -2,
      original_summary.hash, original_summary.first4[0],
      original_summary.first4[1], original_summary.first4[2],
      original_summary.first4[3], original_payload,
      payload_summary.valid
          ? (payload_summary.first_nonzero == UINT32_MAX
                 ? -1
                 : static_cast<int>(payload_summary.first_nonzero))
          : -2,
      payload_summary.hash, payload_summary.first4[0],
      payload_summary.first4[1], payload_summary.first4[2],
      payload_summary.first4[3], args.r14, args.r15, args.r16, args.r17,
      args.r18, args.r19, args.r20, args.r21, args.r22, args.r23, args.r24,
      args.r25, args.r26, args.r27, args.r28, args.r29, args.r30, args.r31);
  ++g_observer.logged_tile_helper_deep_probes;
}

void LogFableTextureDecoderProbeLocked(std::string_view phase,
                                       std::string_view symbol,
                                       const RendererHookArgs& args,
                                       uint8_t* base) {
  if ((!RendererVerboseTracingEnabled() && !RendererResourceTracingEnabled()) ||
      !base) {
    return;
  }

  static uint32_t logged_decoder_probes = 0;
  if (logged_decoder_probes >= 96u) {
    return;
  }

  const bool body = symbol == "sub_82B94DE0";
  const uint32_t source = body ? args.r3 : args.r4;
  const uint32_t destination = body ? args.r4 : args.r6;
  const uint32_t width = body ? args.r8 : args.r9;
  const uint32_t height = body ? args.r9 : args.r10;
  const uint32_t format = body ? args.r6 : args.r7;
  const uint32_t stride = body ? args.r7 : args.r8;
  const uint32_t mode = args.r5;
  const uint32_t probe_size = TileHelperProbeSize(width, height, stride, 4u);
  const GuestByteProbeSummary source_summary =
      ProbeGuestBytes(base, source, probe_size);
  const GuestByteProbeSummary destination_summary =
      ProbeGuestBytes(base, destination, probe_size);

  std::array<uint32_t, 8> source_words = {};
  for (uint32_t word = 0; word < source_words.size(); ++word) {
    const uint32_t address = source + word * 4u;
    source_words[word] =
        IsLikelyGuestDwordAddress(address) ? LoadGuestU32(base, address) : 0u;
  }
  uint32_t payload = 0;
  if (source != 0 && source_words[1] < 64u * 1024u * 1024u &&
      source <= UINT32_MAX - source_words[1]) {
    payload = source + source_words[1];
  }
  const GuestByteProbeSummary payload_summary =
      payload != 0 ? ProbeGuestBytes(base, payload, probe_size)
                   : GuestByteProbeSummary{};

  REXLOG_INFO(
      "Refii renderer Fable texture decoder probe #{} phase={} hook={} "
      "lr=0x{:08X} ret=0x{:08X} src=0x{:08X} dst=0x{:08X} "
      "mode={} fmt=0x{:08X} "
      "stride={} size={}x{} probe={} words={:08X}/{:08X}/{:08X}/{:08X} "
      "words4={:08X}/{:08X}/{:08X}/{:08X} "
      "src_nz={} src_hash64k=0x{:016X} dst_nz={} dst_hash64k=0x{:016X} "
      "payload=0x{:08X} payload_nz={} payload_hash64k=0x{:016X} "
      "payload4={:02X} {:02X} {:02X} {:02X}",
      logged_decoder_probes + 1u, phase, symbol, args.lr, args.r3, source,
      destination, mode, format, stride, width, height, probe_size, source_words[0],
      source_words[1], source_words[2], source_words[3], source_words[4],
      source_words[5], source_words[6], source_words[7],
      source_summary.valid
          ? (source_summary.first_nonzero == UINT32_MAX
                 ? -1
                 : static_cast<int>(source_summary.first_nonzero))
          : -2,
      source_summary.hash,
      destination_summary.valid
          ? (destination_summary.first_nonzero == UINT32_MAX
                 ? -1
                 : static_cast<int>(destination_summary.first_nonzero))
          : -2,
      destination_summary.hash, payload,
      payload_summary.valid
          ? (payload_summary.first_nonzero == UINT32_MAX
                 ? -1
                 : static_cast<int>(payload_summary.first_nonzero))
          : -2,
      payload_summary.hash, payload_summary.first4[0],
      payload_summary.first4[1], payload_summary.first4[2],
      payload_summary.first4[3]);
  ++logged_decoder_probes;
}

void RefreshScratchResourceUploadLocked(RendererResourceRecord& record) {
  if (g_observer.memory_base == nullptr) {
    return;
  }

  const GuestBufferRecord buffer = GuestBufferRecordFor(record);
  if (!record.scratch || !IsBufferResourceKind(GuestObjectKind(record)) ||
      buffer.gpu_address == 0 || buffer.byte_size == 0) {
    return;
  }
  if (record.scratch_upload_source_address == 0) {
    return;
  }

  const std::vector<uint8_t> previous_upload = record.upload_data;
  const uint64_t previous_generation = record.upload_generation;
  CopyGuestBufferUploadData(record, g_observer.memory_base,
                            record.scratch_upload_source_address != 0
                                ? record.scratch_upload_source_address
                                : buffer.gpu_address,
                            buffer.byte_size);
  if (record.upload_data == previous_upload) {
    record.upload_generation = previous_generation;
    return;
  }

  record.serial = ++g_observer.serial;
  MarkResourceChangedLocked(record);
  RefreshQueuedDrawBufferResourceSerialLocked(record);
  FableDirectGuestResourceRegistry().last_resource = record;
}

bool DrawEventMayNeedIndexedScratchExpansion(
    const RendererDrawEventSnapshot& event) {
  if (!event.draw.indexed || event.index_buffer == 0 ||
      !IsLikelyScratchBufferAddress(event.index_buffer)) {
    return false;
  }
  return std::any_of(std::begin(event.streams), std::end(event.streams),
                     [](const RendererStreamBindingSnapshot& stream) {
                       return stream.buffer != 0 &&
                              IsLikelyScratchBufferAddress(stream.buffer);
                     });
}

void ExpandPendingIndexedScratchDrawsLocked() {
  if (g_observer.memory_base == nullptr ||
      FableDirectPendingDrawEventCount() == 0) {
    return;
  }
  if (g_observer.expanded_indexed_scratch_draw_serials.size() > 8192u) {
    g_observer.expanded_indexed_scratch_draw_serials.clear();
  }

  std::vector<RendererDrawEventSnapshot> candidates;
  FableDirectPatchPendingDrawEvents(
      [&](RendererDrawEventSnapshot& event) {
    if (event.serial == 0 ||
        g_observer.expanded_indexed_scratch_draw_serials.find(event.serial) !=
            g_observer.expanded_indexed_scratch_draw_serials.end() ||
        !DrawEventMayNeedIndexedScratchExpansion(event)) {
      return false;
    }
    candidates.push_back(event);
    return false;
  });

  for (RendererDrawEventSnapshot& event : candidates) {
    const bool stride_changed = ResolveDrawEventStreamStridesLocked(event);
    std::vector<ScratchBufferAttachment> attachments =
        ExpandIndexedScratchStreamsToIndexArenaLocked(event);
    if (attachments.empty() && !stride_changed) {
      continue;
    }
    const bool needs_scratch_index =
        IsLikelyScratchBufferAddress(event.index_buffer);
    bool needs_scratch_vertex = false;
    for (const RendererStreamBindingSnapshot& stream : event.streams) {
      if (stream.buffer != 0 && IsLikelyScratchBufferAddress(stream.buffer)) {
        needs_scratch_vertex = true;
        break;
      }
    }
    const bool has_scratch_index =
        !needs_scratch_index ||
        std::any_of(attachments.begin(), attachments.end(),
                    [](const ScratchBufferAttachment& attachment) {
                      return attachment.index_buffer;
                    });
    const bool has_scratch_vertex =
        !needs_scratch_vertex ||
        std::any_of(attachments.begin(), attachments.end(),
                    [](const ScratchBufferAttachment& attachment) {
                      return !attachment.index_buffer;
                    });
    if (has_scratch_index && has_scratch_vertex) {
      g_observer.expanded_indexed_scratch_draw_serials.insert(event.serial);
    }

    FableDirectPatchDrawEvent(event.serial,
                              [&](RendererDrawEventSnapshot& live_event) {
      std::copy(std::begin(event.streams), std::end(event.streams),
                std::begin(live_event.streams));
      live_event.index_buffer = event.index_buffer;
      live_event.resource_serial =
          std::max(live_event.resource_serial, event.resource_serial);
      RefreshDrawEventResourceSerialFromBindingsLocked(live_event);
      return true;
    });
    for (const ScratchBufferAttachment& attachment : attachments) {
      FableDirectAttachDrawScratchBuffer(
          event.serial, attachment.index_buffer, attachment.address,
          attachment.stride_or_format, attachment.data.data(),
          attachment.data.size());
    }
  }
}

void RefreshPendingDrawScratchResourceUploadsLocked() {
  ExpandPendingIndexedScratchDrawsLocked();
  for (auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    if (!record.scratch || !IsBufferResourceKind(GuestObjectKind(record)) ||
        record.scratch_upload_source_address == 0 ||
        !FableDirectDrawEventsReferenceResource(
            DirectResourceReferenceForRecord(record))) {
      continue;
    }

    const uint64_t prior_serial = record.serial;
    RefreshScratchResourceUploadLocked(record);
    if (record.serial != prior_serial) {
      std::vector<uint64_t> draw_serials;
      FableDirectPatchPendingDrawEvents(
          [&](RendererDrawEventSnapshot& event) {
        const bool references_buffer =
            DrawEventReferencesBufferResource(event, record);
        if (references_buffer) {
          event.resource_serial =
              std::max(event.resource_serial, record.serial);
          draw_serials.push_back(event.serial);
        }
        return references_buffer;
      });
      for (uint64_t draw_serial : draw_serials) {
        AttachScratchBufferPayloadToDrawCommandLocked(
            draw_serial, GuestObjectKind(record), GuestObjectAddress(record));
      }
      const GuestBufferRecord buffer = GuestBufferRecordFor(record);
      if (!record.upload_data.empty()) {
        FableDirectAttachScratchBufferByAddress(
            GuestObjectKind(record) == RendererResourceKind::kIndexBuffer,
            GuestObjectAddress(record), buffer.stride_or_index_format,
            record.upload_data.data(), record.upload_data.size());
      }
    }
  }
}

void SanitizePendingDrawEventsLocked() {
  RefreshPendingDrawScratchResourceUploadsLocked();
  FableDirectPatchPendingDrawEvents([&](RendererDrawEventSnapshot& event) {
    SanitizeEventSurfacesLocked(event);
    return true;
  });
}

uint32_t FableSceneTableCount(uint8_t* base, uint32_t* table_begin_out = nullptr,
                              uint32_t* table_end_out = nullptr) {
  constexpr uint32_t kSceneTableBeginAddress = 0x8349F7ACu;
  constexpr uint32_t kSceneTableEndAddress = 0x8349F7B0u;
  const uint32_t table_begin = LoadGuestU32(base, kSceneTableBeginAddress);
  const uint32_t table_end = LoadGuestU32(base, kSceneTableEndAddress);
  if (table_begin_out != nullptr) {
    *table_begin_out = table_begin;
  }
  if (table_end_out != nullptr) {
    *table_end_out = table_end;
  }
  return table_end >= table_begin ? (table_end - table_begin) / 4u : 0u;
}

bool IsLiveFableAssignedSceneObject(uint8_t* base, uint32_t /*index*/,
                                    uint32_t scene) {
  if (!IsLikelyFableObjectAddress(scene)) {
    return false;
  }

  const uint32_t vtable = LoadGuestU32(base, scene + 0u);
  const uint32_t scene_type = LoadGuestU32(base, scene + 4u);
  const uint32_t world_or_game = LoadGuestU32(base, scene + 20u);
  return vtable != 0 && scene_type == 1u &&
         (world_or_game == 0 || IsLikelyFableObjectAddress(world_or_game));
}

bool RepairFableAssignedSceneSlotLocked(uint8_t* base, uint32_t index,
                                        std::string_view reason,
                                        uint32_t lr) {
  if (base == nullptr || index == UINT32_MAX) {
    return false;
  }

  uint32_t table_begin = 0;
  uint32_t table_end = 0;
  const uint32_t table_count =
      FableSceneTableCount(base, &table_begin, &table_end);
  if (table_count == 0 || index >= table_count ||
      !IsLikelyGuestDwordAddress(table_begin)) {
    return false;
  }

  const uint32_t slot = table_begin + index * sizeof(uint32_t);
  const uint32_t current_scene = LoadGuestU32(base, slot);
  if (current_scene != 0) {
    if (IsLiveFableAssignedSceneObject(base, index, current_scene)) {
      g_observer.assigned_scene_objects[index] = current_scene;
    }
    return false;
  }

  auto assigned_it = g_observer.assigned_scene_objects.find(index);
  if (assigned_it == g_observer.assigned_scene_objects.end()) {
    return false;
  }

  const uint32_t scene = assigned_it->second;
  if (!IsLiveFableAssignedSceneObject(base, index, scene)) {
    g_observer.assigned_scene_objects.erase(assigned_it);
    return false;
  }

  const uint64_t repair_count = ++g_observer.scene_table_repairs;
  if (repair_count <= 32u || IsPowerOfTwo(repair_count)) {
    REXLOG_INFO(
        "Refii renderer Fable scene table native clear observed reason={} count={} "
        "index={} scene=0x{:08X} slot=0x{:08X} table=0x{:08X}-0x{:08X} "
        "lr=0x{:08X} mutate=0",
        reason, repair_count, index, scene, slot, table_begin, table_end, lr);
  }
  return false;
}

uint32_t FableSceneObjectAtIndex(uint8_t* base, uint32_t index) {
  uint32_t table_begin = 0;
  uint32_t table_end = 0;
  const uint32_t table_count =
      FableSceneTableCount(base, &table_begin, &table_end);
  (void)table_end;
  if (index == UINT32_MAX || index >= table_count ||
      !IsLikelyGuestDwordAddress(table_begin)) {
    return 0;
  }
  return LoadGuestU32(base, table_begin + index * sizeof(uint32_t));
}

bool IsRenderableFableSceneObject(uint8_t* base, uint32_t scene) {
  if (!IsLikelyFableObjectAddress(scene) || LoadGuestU32(base, scene + 4u) != 1u) {
    return false;
  }

  // The frame-director bridge is only a repair for scenes Fable has already
  // made drawable. Bridging a registered but disabled scene produces long
  // state/clear-only command streams with no title draw work.
  return LoadGuestU8(base, scene + 72u) != 0;
}

bool IsBridgeableFableSceneObject(uint8_t* base, uint32_t index,
                                  uint32_t scene) {
  return IsLiveFableAssignedSceneObject(base, index, scene);
}

bool IsSubmittableFableSceneObject(uint8_t* base, uint32_t index,
                                   uint32_t scene) {
  // IDA: sub_821C3858 reaches sub_821C3C60 for type-1 scenes, but
  // sub_821C3C60 returns through the default scene path while scene+72 is
  // clear. Only bridge scenes that the native render submitter will actually
  // render.
  return IsLiveFableAssignedSceneObject(base, index, scene) &&
         IsRenderableFableSceneObject(base, scene);
}

bool IsRenderableFableSceneIndex(uint8_t* base, uint32_t index) {
  return IsRenderableFableSceneObject(base, FableSceneObjectAtIndex(base, index));
}

uint32_t FableSceneIndexForObject(uint8_t* base, uint32_t scene) {
  uint32_t table_begin = 0;
  uint32_t table_end = 0;
  const uint32_t table_count =
      FableSceneTableCount(base, &table_begin, &table_end);
  (void)table_end;
  if (scene == 0 || table_count == 0 || !IsLikelyGuestDwordAddress(table_begin)) {
    return UINT32_MAX;
  }
  const uint32_t capped_count = std::min<uint32_t>(table_count, 512u);
  for (uint32_t index = 0; index < capped_count; ++index) {
    if (LoadGuestU32(base, table_begin + index * 4u) == scene) {
      return index;
    }
  }
  return UINT32_MAX;
}

bool IsFableSceneEnableProbeHook(std::string_view symbol) {
  return symbol == "sub_82292AC0" || symbol == "sub_82298998" ||
         symbol == "sub_82299AE8";
}

void ObserveFableSceneEnableUpdateLocked(std::string_view symbol,
                                         const RendererHookArgs& args,
                                         uint32_t result, uint8_t* base,
                                         std::string_view phase) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kSceneEnableGlobalAddress = 0x83496AB8u;
  constexpr uint32_t kSceneReadyClockAddress = 0x83319650u;
  constexpr uint32_t kSceneReadyPeriodAddress = 0x83319660u;

  if (symbol == "sub_82299AE8") {
    if (!IsLikelyGuestDwordAddress(args.r3)) {
      return;
    }
    const uint32_t gate_field = args.r3;
    const uint32_t scene = gate_field >= 32u ? gate_field - 32u : 0u;
    const bool readable_scene = IsLikelyFableObjectAddress(scene);
    const uint32_t scene_type =
        readable_scene ? LoadGuestU32(base, scene + 4u) : UINT32_MAX;
    const uint32_t scene_index =
        readable_scene ? FableSceneIndexForObject(base, scene) : UINT32_MAX;
    if (scene_type != 1u && scene_index == UINT32_MAX) {
      return;
    }

    const uint32_t ready_word = LoadGuestU32(base, gate_field);
    const uint32_t clock_word = LoadGuestU32(base, kSceneReadyClockAddress);
    const double ready_period = LoadGuestF64(base, kSceneReadyPeriodAddress);
    const int32_t ready_delta =
        std::max<int32_t>(0, static_cast<int32_t>(ready_word - clock_word));
    const uint8_t scene_enabled =
        readable_scene ? LoadGuestU8(base, scene + 72u) : 0u;
    const uint8_t scene_secondary =
        readable_scene ? LoadGuestU8(base, scene + 73u) : 0u;
    const uint32_t scene_enable_global =
        LoadGuestU32(base, kSceneEnableGlobalAddress);

    static uint32_t logged_gate_samples = 0;
    const bool should_log =
        RendererDrawTracingEnabled() &&
        (logged_gate_samples < 64u || result == 0u ||
         IsPowerOfTwo(logged_gate_samples + 1u));
    if (should_log) {
      REXLOG_INFO(
          "Refii renderer Fable scene ready gate phase={} count={} field=0x{:08X} "
          "scene=0x{:08X} index={} type=0x{:08X} enabled={} secondary={} "
          "ready_word=0x{:08X} clock=0x{:08X} delta={} period={:.6f} "
          "result={} enable_global=0x{:08X}/{} lr=0x{:08X}",
          phase, logged_gate_samples + 1u, gate_field, scene, scene_index,
          scene_type, scene_enabled, scene_secondary, ready_word, clock_word,
          ready_delta, ready_period, result, scene_enable_global,
          scene_enable_global & 0xFFu, args.lr);
      ++logged_gate_samples;
    }
    return;
  }

  const uint32_t scene = args.r3;
  if (!IsLikelyFableObjectAddress(scene)) {
    return;
  }
  const uint32_t scene_type = LoadGuestU32(base, scene + 4u);
  const uint32_t scene_index = FableSceneIndexForObject(base, scene);
  if (scene_type != 1u && scene_index == UINT32_MAX) {
    return;
  }

  const uint32_t vtable = LoadGuestU32(base, scene + 0u);
  const uint32_t world = LoadGuestU32(base, scene + 20u);
  const uint32_t ready_word = LoadGuestU32(base, scene + 32u);
  const uint32_t clock_word = LoadGuestU32(base, kSceneReadyClockAddress);
  const double ready_period = LoadGuestF64(base, kSceneReadyPeriodAddress);
  const uint8_t scene_enabled = LoadGuestU8(base, scene + 72u);
  const uint8_t scene_secondary = LoadGuestU8(base, scene + 73u);
  const uint32_t scene_enable_global =
      LoadGuestU32(base, kSceneEnableGlobalAddress);
  const int32_t ready_delta =
      std::max<int32_t>(0, static_cast<int32_t>(ready_word - clock_word));

  static uint32_t logged_update_samples = 0;
  static uint64_t last_update_key = UINT64_MAX;
  const uint64_t update_key =
      (static_cast<uint64_t>(scene_index & 0xFFFFu) << 48u) |
      (static_cast<uint64_t>(scene_enabled) << 40u) |
      (static_cast<uint64_t>(scene_secondary) << 32u) |
      (ready_word ^ clock_word);
  const bool should_log =
      RendererDrawTracingEnabled() &&
      (logged_update_samples < 64u || update_key != last_update_key ||
       IsPowerOfTwo(logged_update_samples + 1u));
  if (should_log) {
    REXLOG_INFO(
        "Refii renderer Fable scene enable update phase={} symbol={} count={} "
        "scene=0x{:08X} index={} vtable=0x{:08X} type=0x{:08X} world=0x{:08X} "
        "enabled={} secondary={} ready_word=0x{:08X} clock=0x{:08X} "
        "delta={} period={:.6f} result={} enable_global=0x{:08X}/{} lr=0x{:08X}",
        phase, FableHookName(symbol) ? FableHookName(symbol) : symbol.data(),
        logged_update_samples + 1u, scene, scene_index, vtable, scene_type,
        world, scene_enabled, scene_secondary, ready_word, clock_word,
        ready_delta, ready_period, result, scene_enable_global,
        scene_enable_global & 0xFFu, args.lr);
    ++logged_update_samples;
    last_update_key = update_key;
  }
}

void AdvanceFableRenderFrameOwnerToRenderableSceneLocked(
    uint8_t* base, uint32_t renderer, uint32_t current_index,
    uint32_t previous_index, uint32_t next_index, uint32_t pending_index,
    uint32_t lr) {
  if (base == nullptr || !IsLikelyFableObjectAddress(renderer)) {
    return;
  }

  constexpr uint32_t kTransitionTimeOffset = 152u;
  constexpr uint32_t kTransitionTargetTimeOffset = 160u;

  RepairFableAssignedSceneSlotLocked(base, current_index, "frame-owner-current",
                                     lr);
  RepairFableAssignedSceneSlotLocked(base, next_index, "frame-owner-next", lr);
  RepairFableAssignedSceneSlotLocked(base, pending_index, "frame-owner-pending",
                                     lr);

  const uint32_t current_scene = FableSceneObjectAtIndex(base, current_index);
  const uint32_t next_scene = FableSceneObjectAtIndex(base, next_index);
  const double transition_time =
      LoadGuestF64(base, renderer + kTransitionTimeOffset);
  const double transition_target_time =
      LoadGuestF64(base, renderer + kTransitionTargetTimeOffset);
  if (current_scene == 0 && IsRenderableFableSceneObject(base, next_scene) &&
      std::isfinite(transition_time) &&
      std::isfinite(transition_target_time) &&
      transition_time + 0.001 < transition_target_time &&
      transition_target_time > 1.0) {
    static uint32_t logged_transition_repairs = 0;
    if (logged_transition_repairs < 16u ||
        IsPowerOfTwo(logged_transition_repairs + 1u)) {
      REXLOG_INFO(
          "Refii renderer Fable render frame owner observed stalled scene "
          "transition renderer=0x{:08X} current={} current_scene=0x{:08X} "
          "next={} next_scene=0x{:08X} transition={:.6f}/{:.6f} lr=0x{:08X} "
          "count={} mutate=0",
          renderer, current_index, current_scene, next_index, next_scene,
          transition_time, transition_target_time, lr,
          logged_transition_repairs + 1u);
    }
    ++logged_transition_repairs;
  }

  if (IsRenderableFableSceneIndex(base, current_index) ||
      !IsRenderableFableSceneIndex(base, next_index)) {
    return;
  }

  static uint32_t logged_advances = 0;
  if (logged_advances < 16u || IsPowerOfTwo(logged_advances + 1u)) {
    REXLOG_INFO(
        "Refii renderer Fable render frame owner found later renderable scene "
        "renderer=0x{:08X} current={} previous={} next={} pending={} "
        "next_scene=0x{:08X} lr=0x{:08X} count={} mutate=0",
        renderer, current_index, previous_index, next_index, pending_index,
        FableSceneObjectAtIndex(base, next_index), lr, logged_advances + 1u);
  }
  ++logged_advances;
}

void RepairFableAssignedSceneTableLocked(uint8_t* base, std::string_view reason,
                                         uint32_t lr) {
  if (base == nullptr || g_observer.assigned_scene_objects.empty()) {
    return;
  }

  for (auto it = g_observer.assigned_scene_objects.begin();
       it != g_observer.assigned_scene_objects.end();) {
    const uint32_t index = it->first;
    const uint32_t scene = it->second;
    if (!IsLiveFableAssignedSceneObject(base, index, scene)) {
      it = g_observer.assigned_scene_objects.erase(it);
      continue;
    }
    ++it;
    RepairFableAssignedSceneSlotLocked(base, index, reason, lr);
  }
}

void ObserveFableRenderFrameOwnerLocked(const RendererHookArgs& args,
                                        uint8_t* base,
                                        std::string_view phase) {
  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3)) {
    return;
  }

  constexpr uint32_t kCurrentSceneIndexOffset = 116u;
  constexpr uint32_t kPreviousSceneIndexOffset = 120u;
  constexpr uint32_t kNextSceneIndexOffset = 124u;
  constexpr uint32_t kPendingSceneIndexOffset = 128u;
  constexpr uint32_t kSceneSwapPendingOffset = 132u;
  constexpr uint32_t kTransitionTimeOffset = 152u;
  constexpr uint32_t kTransitionTargetTimeOffset = 160u;
  constexpr uint32_t kFrameAdvancePendingOffset = 168u;
  constexpr uint32_t kWorldOrGameOffset = 592u;
  constexpr uint32_t kFrameTimeReferenceOffset = 596u;
  constexpr uint32_t kSceneTableBeginAddress = 0x8349F7ACu;
  constexpr uint32_t kSceneTableEndAddress = 0x8349F7B0u;
  constexpr uint32_t kSchedulerGlobalAAddress = 0x83496AB8u;
  constexpr uint32_t kSchedulerGlobalBAddress = 0x83496AC0u;
  constexpr uint32_t kSchedulerGlobalCAddress = 0x8335C4C4u;

  const uint32_t renderer = args.r3;
  uint32_t current_index =
      LoadGuestU32(base, renderer + kCurrentSceneIndexOffset);
  uint32_t previous_index =
      LoadGuestU32(base, renderer + kPreviousSceneIndexOffset);
  uint32_t next_index =
      LoadGuestU32(base, renderer + kNextSceneIndexOffset);
  uint32_t pending_index =
      LoadGuestU32(base, renderer + kPendingSceneIndexOffset);
  const uint8_t scene_swap_pending =
      LoadGuestU8(base, renderer + kSceneSwapPendingOffset);
  const uint8_t frame_advance_pending =
      LoadGuestU8(base, renderer + kFrameAdvancePendingOffset);
  AdvanceFableRenderFrameOwnerToRenderableSceneLocked(
      base, renderer, current_index, previous_index, next_index, pending_index,
      args.lr);
  current_index = LoadGuestU32(base, renderer + kCurrentSceneIndexOffset);
  previous_index = LoadGuestU32(base, renderer + kPreviousSceneIndexOffset);
  next_index = LoadGuestU32(base, renderer + kNextSceneIndexOffset);
  pending_index = LoadGuestU32(base, renderer + kPendingSceneIndexOffset);
  const double transition_time =
      LoadGuestF64(base, renderer + kTransitionTimeOffset);
  const double transition_target_time =
      LoadGuestF64(base, renderer + kTransitionTargetTimeOffset);
  const bool transition_ready =
      std::isfinite(transition_time) && std::isfinite(transition_target_time) &&
      transition_time >= transition_target_time;
  const uint32_t world_or_game = LoadGuestU32(base, renderer + kWorldOrGameOffset);
  const float frame_time_reference =
      LoadGuestF32(base, renderer + kFrameTimeReferenceOffset);
  const uint32_t table_begin = LoadGuestU32(base, kSceneTableBeginAddress);
  const uint32_t table_end = LoadGuestU32(base, kSceneTableEndAddress);
  const uint32_t table_count =
      table_end >= table_begin ? (table_end - table_begin) / 4u : 0u;
  const uint32_t scheduler_global_a =
      LoadGuestU32(base, kSchedulerGlobalAAddress);
  const uint32_t scheduler_global_b =
      LoadGuestU32(base, kSchedulerGlobalBAddress);
  const uint32_t scheduler_global_c =
      LoadGuestU32(base, kSchedulerGlobalCAddress);
  const uint32_t scheduler_selected =
      scheduler_global_a != 0 ? scheduler_global_a
                              : (scheduler_global_b != 0 ? scheduler_global_b
                                                         : scheduler_global_c);
  const uint32_t scheduler_selected_inner =
      IsLikelyFableObjectAddress(scheduler_selected)
          ? LoadGuestU32(base, scheduler_selected + 8u)
          : 0u;
  const uint32_t scheduler_holder =
      IsLikelyFableObjectAddress(scheduler_selected_inner)
          ? LoadGuestU32(base, scheduler_selected_inner + 36u)
          : 0u;
  const uint32_t scheduler_holder_owner =
      IsLikelyFableObjectAddress(scheduler_holder)
          ? LoadGuestU32(base, scheduler_holder + 4u)
          : 0u;
  const uint32_t scheduler_holder_lock =
      IsLikelyFableObjectAddress(scheduler_holder)
          ? LoadGuestU32(base, scheduler_holder + 8u)
          : 0u;
  const uint32_t scheduler_holder_mode =
      IsLikelyFableObjectAddress(scheduler_holder)
          ? LoadGuestU32(base, scheduler_holder + 44u)
          : UINT32_MAX;
  struct SceneSlotInfo {
    bool valid_index = false;
    uint32_t scene = 0;
    uint32_t scene_type = UINT32_MAX;
    uint8_t scene_enabled = 0;
    uint32_t scene_world = 0;
  };
  const auto read_slot = [&](uint32_t index) {
    SceneSlotInfo info = {};
    info.valid_index = index != UINT32_MAX && index < table_count &&
                       IsLikelyGuestDwordAddress(table_begin);
    info.scene =
        info.valid_index ? LoadGuestU32(base, table_begin + index * 4u) : 0u;
    const bool readable_scene = IsLikelyFableObjectAddress(info.scene);
    info.scene_type =
        readable_scene ? LoadGuestU32(base, info.scene + 4u) : UINT32_MAX;
    info.scene_enabled =
        readable_scene ? LoadGuestU8(base, info.scene + 72u) : 0u;
    info.scene_world =
        readable_scene ? LoadGuestU32(base, info.scene + 20u) : 0u;
    return info;
  };
  const SceneSlotInfo current_slot = read_slot(current_index);
  const SceneSlotInfo previous_slot = read_slot(previous_index);
  const SceneSlotInfo next_slot = read_slot(next_index);
  const SceneSlotInfo pending_slot = read_slot(pending_index);
  g_observer.last_render_frame_current_index = current_index;
  g_observer.last_render_frame_previous_index = previous_index;
  g_observer.last_render_frame_next_index = next_index;
  g_observer.last_render_frame_pending_index = pending_index;
  g_observer.last_render_frame_current_scene = current_slot.scene;
  g_observer.last_render_frame_previous_scene = previous_slot.scene;
  g_observer.last_render_frame_next_scene = next_slot.scene;
  g_observer.last_render_frame_pending_scene = pending_slot.scene;

  ++g_observer.render_frame_owner_observations;
  const uint32_t phase_index = phase == "exit" ? 1u : 0u;
  const uint64_t key =
      (static_cast<uint64_t>(std::min<uint32_t>(current_index, 0xFFFFu))
       << 48u) |
      (static_cast<uint64_t>(std::min<uint32_t>(next_index, 0xFFFFu))
       << 32u) |
      (static_cast<uint64_t>(std::min<uint32_t>(pending_index, 0xFFFFu))
       << 16u) |
      (static_cast<uint64_t>(std::min<uint32_t>(table_count, 0xFFu))
       << 8u) |
      (static_cast<uint64_t>(scheduler_selected != 0 ? 1u : 0u) << 4u) |
      (static_cast<uint64_t>(scene_swap_pending & 1u) << 7u) |
      (static_cast<uint64_t>(frame_advance_pending & 1u) << 6u) |
      (static_cast<uint64_t>(transition_ready ? 1u : 0u) << 5u) |
      (current_slot.scene_type & 0x1Fu);
  const bool should_log =
      g_observer.render_frame_owner_observations <= 8u ||
      g_observer.last_render_frame_owner_key[phase_index] != key ||
      IsPowerOfTwo(g_observer.render_frame_owner_observations);
  if (should_log) {
    REXLOG_INFO(
        "Refii renderer Fable render frame owner phase={} count={} renderer=0x{:08X} "
        "scene_indices current={} previous={} next={} pending={} swap_pending={} "
        "advance_pending={} transition={:.6f}/{:.6f} ready={} "
        "frame_time_reference={:.6f} "
        "table=0x{:08X}-0x{:08X} table_count={} "
        "current_slot valid={} scene=0x{:08X} type=0x{:08X} enabled={} world=0x{:08X} "
        "previous_slot valid={} scene=0x{:08X} type=0x{:08X} enabled={} world=0x{:08X} "
        "next_slot valid={} scene=0x{:08X} type=0x{:08X} enabled={} world=0x{:08X} "
        "pending_slot valid={} scene=0x{:08X} type=0x{:08X} enabled={} world=0x{:08X} "
        "world_or_game=0x{:08X} scheduler_globals=0x{:08X}/0x{:08X}/0x{:08X} "
        "scheduler_selected=0x{:08X} inner=0x{:08X} holder=0x{:08X} "
        "holder_owner=0x{:08X} holder_lock=0x{:08X} holder_mode=0x{:08X} "
        "lr=0x{:08X}",
        phase, g_observer.render_frame_owner_observations, renderer,
        current_index, previous_index, next_index, pending_index,
        scene_swap_pending, frame_advance_pending, transition_time,
        transition_target_time, transition_ready ? 1 : 0,
        static_cast<double>(frame_time_reference), table_begin, table_end,
        table_count, current_slot.valid_index ? 1 : 0, current_slot.scene,
        current_slot.scene_type, current_slot.scene_enabled,
        current_slot.scene_world, previous_slot.valid_index ? 1 : 0,
        previous_slot.scene, previous_slot.scene_type,
        previous_slot.scene_enabled, previous_slot.scene_world,
        next_slot.valid_index ? 1 : 0, next_slot.scene, next_slot.scene_type,
        next_slot.scene_enabled, next_slot.scene_world,
        pending_slot.valid_index ? 1 : 0, pending_slot.scene,
        pending_slot.scene_type, pending_slot.scene_enabled,
        pending_slot.scene_world,
        world_or_game, scheduler_global_a, scheduler_global_b,
        scheduler_global_c, scheduler_selected, scheduler_selected_inner,
        scheduler_holder, scheduler_holder_owner, scheduler_holder_lock,
        scheduler_holder_mode, args.lr);
    g_observer.last_render_frame_owner_key[phase_index] = key;
  }
}

void ObserveFableFrameDirectorGateLocked(const RendererHookArgs& args,
                                         uint8_t* base,
                                         std::string_view phase) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kSceneTableBeginAddress = 0x8349F7ACu;
  constexpr uint32_t kSceneTableEndAddress = 0x8349F7B0u;
  constexpr uint32_t kSceneFactoryTableBeginAddress = 0x8349F79Cu;
  constexpr uint32_t kSceneFactoryTableEndAddress = 0x8349F7A0u;
  constexpr uint32_t kSchedulerStreamsBeginAddress = 0x8349F9B0u;
  const uint32_t scene_index = args.r3;
  RepairFableAssignedSceneSlotLocked(base, scene_index, "frame-director",
                                     args.lr);
  const uint32_t table_begin = LoadGuestU32(base, kSceneTableBeginAddress);
  const uint32_t table_end = LoadGuestU32(base, kSceneTableEndAddress);
  const uint32_t factory_table_begin =
      LoadGuestU32(base, kSceneFactoryTableBeginAddress);
  const uint32_t factory_table_end =
      LoadGuestU32(base, kSceneFactoryTableEndAddress);
  const uint32_t table_count =
      table_end >= table_begin ? (table_end - table_begin) / 4u : 0u;
  const uint32_t factory_table_count =
      factory_table_end >= factory_table_begin
          ? (factory_table_end - factory_table_begin) / 4u
          : 0u;
  std::array<uint32_t, 4> scheduler_streams = {};
  std::array<double, 4> scheduler_times = {};
  std::array<uint32_t, 4> stream_visible_ends = {};
  std::array<uint32_t, 4> stream_published_ends = {};
  uint32_t scheduler_stream_count = 0;
  for (uint32_t stream_index = 0; stream_index < scheduler_streams.size();
       ++stream_index) {
    scheduler_streams[stream_index] =
        LoadGuestU32(base, kSchedulerStreamsBeginAddress + stream_index * 4u);
    if (scheduler_streams[stream_index] != 0) {
      ++scheduler_stream_count;
    }
    scheduler_times[stream_index] =
        LoadGuestF64(base, 0x8335F7D8u + stream_index * sizeof(double));
    if (IsLikelyFableObjectAddress(scheduler_streams[stream_index])) {
      stream_visible_ends[stream_index] =
          LoadGuestU32(base, scheduler_streams[stream_index] + 16u);
      stream_published_ends[stream_index] =
          LoadGuestU32(base, scheduler_streams[stream_index] + 60u);
    }
  }
  const bool valid_index =
      scene_index != UINT32_MAX && scene_index < table_count;
  const uint32_t scene =
      valid_index ? LoadGuestU32(base, table_begin + scene_index * 4u) : 0u;
  const bool readable_scene = IsLikelyFableObjectAddress(scene);
  const uint32_t scene_type =
      readable_scene ? LoadGuestU32(base, scene + 4u) : UINT32_MAX;
  const uint32_t scene_world =
      readable_scene ? LoadGuestU32(base, scene + 20u) : 0u;
  const uint8_t scene_enabled =
      readable_scene ? LoadGuestU8(base, scene + 72u) : 0u;
  const bool scene_submit_gate = valid_index && scene != 0 && scene_type == 1u;

  ++g_observer.frame_director_observations;
  const uint32_t phase_index = phase == "exit" ? 1u : 0u;
  const uint64_t gate_key =
      (static_cast<uint64_t>(std::min<uint32_t>(scene_index, 0xFFFFu)) << 48u) |
      (static_cast<uint64_t>(std::min<uint32_t>(table_count, 0xFFFFu)) << 32u) |
      (static_cast<uint64_t>(scene_submit_gate ? 1u : 0u) << 31u) |
      (static_cast<uint64_t>(std::min<uint32_t>(factory_table_count, 0xFu))
       << 27u) |
      (static_cast<uint64_t>(std::min<uint32_t>(scheduler_stream_count, 0xFu))
       << 23u) |
      (static_cast<uint64_t>(scene_enabled) << 22u) |
      (static_cast<uint64_t>(scene_type) & 0x3FFFFFu);
  const bool should_log =
      g_observer.frame_director_observations <= 8u ||
      g_observer.last_frame_director_gate_key[phase_index] != gate_key ||
      IsPowerOfTwo(g_observer.frame_director_observations);
  if (should_log) {
    REXLOG_INFO(
        "Refii renderer Fable frame director gate phase={} count={} scene_index={} "
        "table=0x{:08X}-0x{:08X} table_count={} valid_index={} scene=0x{:08X} "
        "scene_type=0x{:08X} scene_enabled={} scene_world=0x{:08X} "
        "submit_gate={} factory_table=0x{:08X}-0x{:08X} factory_count={} "
        "scheduler_streams={}/{}/{}/{} scheduler_stream_count={} "
        "stream_visible={}/{}/{}/{} stream_published={}/{}/{}/{} "
        "frame_time={:.6f} scheduler_times={:.6f}/{:.6f}/{:.6f}/{:.6f} "
        "lr=0x{:08X}",
        phase, g_observer.frame_director_observations, scene_index, table_begin,
        table_end, table_count, valid_index ? 1 : 0, scene, scene_type,
        scene_enabled, scene_world, scene_submit_gate ? 1 : 0,
        factory_table_begin, factory_table_end, factory_table_count,
        scheduler_streams[0], scheduler_streams[1], scheduler_streams[2],
        scheduler_streams[3], scheduler_stream_count, stream_visible_ends[0],
        stream_visible_ends[1], stream_visible_ends[2], stream_visible_ends[3],
        stream_published_ends[0], stream_published_ends[1],
        stream_published_ends[2], stream_published_ends[3], args.f1,
        scheduler_times[0], scheduler_times[1], scheduler_times[2],
        scheduler_times[3], args.lr);
    g_observer.last_frame_director_gate_key[phase_index] = gate_key;
  }
}

void ObserveFableRendererTimeDeltaLocked(const RendererHookArgs& args,
                                         const RendererHookArgs& exit_args,
                                         uint8_t* base) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kTimeDeltaFrameCounterAddress = 0x83360358u;
  constexpr uint32_t kTimeDeltaRateAddress = 0x8336035Cu;
  constexpr uint32_t kTimeSnapshotBaseAddress = 0x834A600Cu;
  constexpr uint32_t kClockSampleLowAddress = 0x83497120u;
  constexpr uint32_t kClockSampleIndexAddress = 0x83497124u;
  constexpr uint32_t kClockOriginAddress = 0x834970A0u;
  constexpr uint32_t kClockRateAddress = 0x834970A8u;
  constexpr uint32_t kClockLastAAddress = 0x83491DC8u;
  constexpr uint32_t kClockLastBAddress = 0x83491DD0u;

  const double result = exit_args.f1;
  const float rate = LoadGuestF32(base, kTimeDeltaRateAddress);
  const uint32_t frame_counter = LoadGuestU32(base, kTimeDeltaFrameCounterAddress);
  const uint32_t snapshot_word0 = LoadGuestU32(base, kTimeSnapshotBaseAddress + 0u);
  const uint32_t snapshot_word1 = LoadGuestU32(base, kTimeSnapshotBaseAddress + 4u);
  const uint32_t snapshot_word2 = LoadGuestU32(base, kTimeSnapshotBaseAddress + 8u);
  const uint32_t sample_low = LoadGuestU32(base, kClockSampleLowAddress);
  const uint32_t sample_index = LoadGuestU32(base, kClockSampleIndexAddress);
  const double clock_origin = LoadGuestF64(base, kClockOriginAddress);
  const double clock_rate = LoadGuestF64(base, kClockRateAddress);
  const uint64_t clock_last_a = LoadGuestU64(base, kClockLastAAddress);
  const uint64_t clock_last_b = LoadGuestU64(base, kClockLastBAddress);

  ++g_observer.renderer_time_delta_observations;
  const int64_t result_us =
      std::isfinite(result) ? static_cast<int64_t>(result * 1000000.0)
                            : INT64_MIN;
  const uint64_t key =
      (static_cast<uint64_t>(result_us) & 0x0000FFFFFFFFFFFFull) ^
      (static_cast<uint64_t>(frame_counter) << 32u) ^
      (static_cast<uint64_t>(sample_low) << 16u) ^
      static_cast<uint64_t>(sample_index);
  const bool should_log =
      g_observer.renderer_time_delta_observations <= 16u ||
      IsPowerOfTwo(g_observer.renderer_time_delta_observations) ||
      (RendererVerboseTracingEnabled() &&
       g_observer.last_renderer_time_delta_key != key);
  if (should_log) {
    REXLOG_INFO(
        "Refii renderer Fable time delta count={} result={:.9f} "
        "entry_f1={:.9f} frame_counter={} rate={:.9f} "
        "snapshot=0x{:08X}/0x{:08X}/0x{:08X} "
        "clock_sample_low={} sample_index={} clock_origin={:.9f} "
        "clock_rate={:.9f} clock_last=0x{:016X}/0x{:016X} lr=0x{:08X}",
        g_observer.renderer_time_delta_observations, result, args.f1,
        frame_counter, static_cast<double>(rate), snapshot_word0,
        snapshot_word1, snapshot_word2, sample_low, sample_index, clock_origin,
        clock_rate, clock_last_a, clock_last_b, args.lr);
    g_observer.last_renderer_time_delta_key = key;
  }
}

int FableSceneFactoryHookIndex(std::string_view symbol) {
  constexpr std::array<std::string_view, 30> kSceneFactoryHooks = {
      "sub_82A4F6A8", "sub_82A4F718", "sub_82A4F858", "sub_82A4EC60",
      "sub_82A4EDF8", "sub_8229BA10", "sub_821CEA80", "sub_822DFE08",
      "sub_8235CA90", "sub_8235CCB8", "sub_8235CED8", "sub_8235D0F8",
      "sub_8235D268", "sub_8235EF70", "sub_8235F108", "sub_8235FDC0",
      "sub_8235E5A0", "sub_82361AF8", "sub_823C1658",
      "sub_82456E60", "sub_826E4E58", "sub_826E4F68", "sub_82870B70",
      "sub_82A77C48", "sub_82A780C0", "sub_82A931E0", "sub_82AB99F8",
      "sub_82AB9CF8", "sub_82ABA800", "sub_82ABAEF0"};
  for (size_t index = 0; index < kSceneFactoryHooks.size(); ++index) {
    if (symbol == kSceneFactoryHooks[index]) {
      return static_cast<int>(index);
    }
  }
  return -1;
}

bool IsFableSceneFactoryHook(std::string_view symbol) {
  return FableSceneFactoryHookIndex(symbol) >= 0;
}

int FableSceneStreamHookIndex(std::string_view symbol) {
  constexpr std::array<std::string_view, 7> kSceneStreamHooks = {
      "sub_821A5BD0", "sub_821A59E0", "sub_822A90B8", "sub_82294830",
      "sub_8222AB78", "rex_lhRenderer_BeginGPUFrame",
      "rex_lhRenderer_EndGPUFrame"};
  for (size_t index = 0; index < kSceneStreamHooks.size(); ++index) {
    if (symbol == kSceneStreamHooks[index]) {
      return static_cast<int>(index);
    }
  }
  return -1;
}

bool IsFableSceneStreamHook(std::string_view symbol) {
  return FableSceneStreamHookIndex(symbol) >= 0;
}

bool ShouldTraceFableScenePipelineHook(std::string_view symbol) {
  if (IsFableSceneStreamHook(symbol)) {
    return RendererVerboseTracingEnabled() || RendererDrawTracingEnabled();
  }

  return RendererVerboseTracingEnabled() ||
         symbol == "sub_822DFE08" ||  // scene stream consumer
         symbol == "sub_821E1F40" ||  // scene table assign/create
         symbol == "sub_82A4F8D8" ||  // scene table insert/resize
         symbol == "sub_832B2CE0" ||  // scene table reset
         symbol == "sub_8235D0F8" ||  // menu scene owner activate
         symbol == "sub_8235D268" ||  // menu/overlay scene factory
         symbol == "sub_8235EF70" ||  // menu textured scene item factory
         symbol == "sub_8235F108" ||  // menu scene item factory
         symbol == "sub_8235FDC0" ||  // title/menu scene setup
         symbol == "sub_8235E5A0" ||  // menu scene factory caller
         symbol == "sub_82361AF8" ||  // menu scene scheduler
         symbol == "sub_823C1658";    // post-frame scene assign caller
}

uint32_t FableSceneStreamSlotForObject(uint8_t* base, uint32_t object) {
  if (base == nullptr || object == 0) {
    return UINT32_MAX;
  }

  constexpr uint32_t kSchedulerStreamsAddress = 0x8349F9B0u;
  for (uint32_t slot = 0; slot < 4u; ++slot) {
    if (LoadGuestU32(base, kSchedulerStreamsAddress + slot * 4u) == object) {
      return slot;
    }
  }
  return UINT32_MAX;
}

struct FableSceneStreamSnapshot {
  uint32_t object = 0;
  uint32_t slot = UINT32_MAX;
  uint32_t stream_base = 0;
  uint32_t cap_total = 0;
  uint32_t read_cursor = 0;
  uint32_t visible_end = 0;
  uint32_t read_limit = 0;
  uint32_t last_read = 0;
  uint32_t read_cap = 0;
  uint32_t write_cursor = 0;
  uint32_t write_visible = 0;
  uint32_t write_mark = 0;
  uint32_t publish_ptr = 0;
  uint32_t wrap_mark = 0;
  uint32_t published_end = 0;
  uint32_t published_cap = 0;
  uint32_t owner_thread = 0;
  uint32_t next0 = 0;
  uint32_t next1 = 0;
  uint32_t next2 = 0;
  uint32_t next3 = 0;
  uint32_t write0 = 0;
  uint32_t publish0 = 0;
};

FableSceneStreamSnapshot ReadFableSceneStreamSnapshot(uint8_t* base,
                                                      uint32_t object) {
  FableSceneStreamSnapshot snapshot = {};
  snapshot.object = object;
  snapshot.slot = FableSceneStreamSlotForObject(base, object);
  if (base == nullptr || !IsLikelyFableObjectAddress(object) ||
      snapshot.slot == UINT32_MAX) {
    return snapshot;
  }

  snapshot.stream_base = LoadGuestU32(base, object + 4u);
  snapshot.cap_total = LoadGuestU32(base, object + 8u);
  snapshot.read_cursor = LoadGuestU32(base, object + 12u);
  snapshot.visible_end = LoadGuestU32(base, object + 16u);
  snapshot.read_limit = LoadGuestU32(base, object + 20u);
  snapshot.last_read = LoadGuestU32(base, object + 24u);
  snapshot.read_cap = LoadGuestU32(base, object + 28u);
  snapshot.write_cursor = LoadGuestU32(base, object + 32u);
  snapshot.write_visible = LoadGuestU32(base, object + 36u);
  snapshot.write_mark = LoadGuestU32(base, object + 40u);
  snapshot.publish_ptr = LoadGuestU32(base, object + 44u);
  snapshot.wrap_mark = LoadGuestU32(base, object + 52u);
  snapshot.published_end = LoadGuestU32(base, object + 60u);
  snapshot.published_cap = LoadGuestU32(base, object + 68u);
  snapshot.owner_thread = LoadGuestU32(base, object + 100u);

  const auto stream_offset_is_sane = [](uint32_t offset, uint32_t cap) {
    constexpr uint32_t kMaxSceneStreamProbeOffset = 16u * 1024u * 1024u;
    return cap != 0 && offset < cap && offset <= kMaxSceneStreamProbeOffset;
  };

  const bool stream_base_readable =
      IsLikelyGuestDwordAddress(snapshot.stream_base);
  const uint32_t read_address = snapshot.stream_base + snapshot.read_cursor;
  if (stream_base_readable &&
      stream_offset_is_sane(snapshot.read_cursor, snapshot.read_cap) &&
      IsLikelyGuestDwordAddress(read_address)) {
    snapshot.next0 = LoadGuestU32(base, read_address);
    snapshot.next1 = LoadGuestU32(base, read_address + 4u);
    snapshot.next2 = LoadGuestU32(base, read_address + 8u);
    snapshot.next3 = LoadGuestU32(base, read_address + 12u);
  }

  const uint32_t write_address = snapshot.stream_base + snapshot.write_mark;
  if (stream_base_readable &&
      stream_offset_is_sane(snapshot.write_mark, snapshot.read_cap) &&
      IsLikelyGuestDwordAddress(write_address)) {
    snapshot.write0 = LoadGuestU32(base, write_address);
  }
  if (stream_base_readable && snapshot.published_cap != 0 &&
      snapshot.published_cap <= snapshot.read_cap &&
      IsLikelyGuestDwordAddress(snapshot.publish_ptr)) {
    snapshot.publish0 = LoadGuestU32(base, snapshot.publish_ptr);
  }
  return snapshot;
}

void ObserveFableSceneStreamCallLocked(std::string_view symbol,
                                       const RendererHookArgs& args,
                                       uint32_t result, uint8_t* base,
                                       std::string_view phase) {
  if (base == nullptr) {
    return;
  }

  const int symbol_index = FableSceneStreamHookIndex(symbol);
  if (symbol_index < 0) {
    return;
  }
  if (!ShouldTraceFableScenePipelineHook(symbol)) {
    return;
  }

  uint32_t object = args.r3;
  if (!IsLikelyFableObjectAddress(object) ||
      FableSceneStreamSlotForObject(base, object) == UINT32_MAX) {
    object = 0;
    constexpr uint32_t kSchedulerStreamsAddress = 0x8349F9B0u;
    for (uint32_t slot = 0; slot < 4u; ++slot) {
      const uint32_t candidate =
          LoadGuestU32(base, kSchedulerStreamsAddress + slot * 4u);
      if (IsLikelyFableObjectAddress(candidate)) {
        object = candidate;
        break;
      }
    }
  }

  const FableSceneStreamSnapshot snapshot =
      ReadFableSceneStreamSnapshot(base, object);
  constexpr uint32_t kSchedulerStreamsAddress = 0x8349F9B0u;
  std::array<uint32_t, 4> slot_objects = {};
  std::array<uint32_t, 4> slot_visible_ends = {};
  std::array<uint32_t, 4> slot_published_ends = {};
  std::array<uint32_t, 4> slot_write_cursors = {};
  std::array<uint32_t, 4> slot_publish_flags = {};
  for (uint32_t slot = 0; slot < 4u; ++slot) {
    const uint32_t slot_object =
        LoadGuestU32(base, kSchedulerStreamsAddress + slot * 4u);
    slot_objects[slot] = slot_object;
    slot_publish_flags[slot] = LoadGuestU8(base, 0x8335F7D4u + slot);
    if (IsLikelyFableObjectAddress(slot_object)) {
      slot_visible_ends[slot] = LoadGuestU32(base, slot_object + 16u);
      slot_write_cursors[slot] = LoadGuestU32(base, slot_object + 32u);
      slot_published_ends[slot] = LoadGuestU32(base, slot_object + 60u);
    }
  }

  static std::array<std::array<uint64_t, 2>, 7> observations = {};
  static std::array<std::array<uint64_t, 2>, 7> last_keys = [] {
    std::array<std::array<uint64_t, 2>, 7> keys = {};
    for (auto& key_pair : keys) {
      key_pair = {UINT64_MAX, UINT64_MAX};
    }
    return keys;
  }();

  const uint32_t phase_index = phase == "exit" ? 1u : 0u;
  uint64_t& count = observations[static_cast<size_t>(symbol_index)][phase_index];
  ++count;
  const uint64_t key =
      (static_cast<uint64_t>(snapshot.object) << 32u) ^
      (static_cast<uint64_t>(snapshot.read_cursor & 0xFFFFu) << 48u) ^
      (static_cast<uint64_t>(snapshot.visible_end & 0xFFFFu) << 16u) ^
      (static_cast<uint64_t>(snapshot.published_end & 0xFFFFu) << 1u) ^
      (static_cast<uint64_t>(snapshot.write_cursor & 0xFFFFu) << 8u) ^
      (static_cast<uint64_t>(snapshot.next0 & 0xFFu) << 56u) ^
      (static_cast<uint64_t>(args.r4 & 0xFFu) << 24u) ^
      (static_cast<uint64_t>(result & 0xFFu));
  uint64_t& last_key = last_keys[static_cast<size_t>(symbol_index)][phase_index];
  static uint32_t logged_scene_stream_events = 0;
  const bool verbose_scene_trace = RendererVerboseTracingEnabled();
  const bool should_log =
      verbose_scene_trace
          ? (count <= 16u || key != last_key || IsPowerOfTwo(count))
          : (logged_scene_stream_events < 64u &&
             (count <= 16u || IsPowerOfTwo(count)));
  if (should_log) {
    REXLOG_INFO(
        "Refii renderer Fable scene stream phase={} symbol={} count={} "
        "args r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
        "result=0x{:08X} object=0x{:08X} slot={} stream=0x{:08X} "
        "cap={} read={}/{}/{} last={} read_cap={} write={}/{}/{} "
        "publish=0x{:08X} wrap={} published={}/{} owner=0x{:08X} "
        "next={:08X}/{:08X}/{:08X}/{:08X} write0={:08X} publish0={:08X} "
        "slots obj={:08X}/{:08X}/{:08X}/{:08X} "
        "visible={}/{}/{}/{} published={}/{}/{}/{} write={}/{}/{}/{} "
        "flags={}/{}/{}/{} "
        "lr=0x{:08X}",
        phase, FableHookName(symbol) ? FableHookName(symbol) : symbol.data(),
        count, args.r3, args.r4, args.r5, args.r6, result, snapshot.object,
        snapshot.slot == UINT32_MAX ? 0xFFFFFFFFu : snapshot.slot,
        snapshot.stream_base, snapshot.cap_total, snapshot.read_cursor,
        snapshot.visible_end, snapshot.read_limit, snapshot.last_read,
        snapshot.read_cap, snapshot.write_cursor, snapshot.write_visible,
        snapshot.write_mark, snapshot.publish_ptr, snapshot.wrap_mark,
        snapshot.published_end, snapshot.published_cap, snapshot.owner_thread,
        snapshot.next0, snapshot.next1, snapshot.next2, snapshot.next3,
        snapshot.write0, snapshot.publish0, slot_objects[0], slot_objects[1],
        slot_objects[2], slot_objects[3], slot_visible_ends[0],
        slot_visible_ends[1], slot_visible_ends[2], slot_visible_ends[3],
        slot_published_ends[0], slot_published_ends[1],
        slot_published_ends[2], slot_published_ends[3],
        slot_write_cursors[0], slot_write_cursors[1], slot_write_cursors[2],
        slot_write_cursors[3], slot_publish_flags[0], slot_publish_flags[1],
        slot_publish_flags[2], slot_publish_flags[3], args.lr);
    ++logged_scene_stream_events;
    last_key = key;
  }
}

GuestPointerTableScan ScanGuestPointerTable(uint8_t* base, uint32_t begin,
                                            uint32_t count,
                                            uint32_t max_entries = 256u) {
  GuestPointerTableScan scan;
  if (base == nullptr || count == 0 ||
      !IsReadableGuestByteRange(base, begin, sizeof(uint32_t))) {
    return scan;
  }

  const uint32_t scanned_entries = std::min(count, max_entries);
  for (uint32_t index = 0; index < scanned_entries; ++index) {
    if (index > (UINT32_MAX - begin) / sizeof(uint32_t)) {
      break;
    }

    const uint32_t slot = begin + index * sizeof(uint32_t);
    if (!IsReadableGuestByteRange(base, slot, sizeof(uint32_t))) {
      break;
    }

    const uint32_t value = LoadGuestU32(base, slot);
    if (value == 0) {
      continue;
    }

    if (scan.first_nonzero_index == UINT32_MAX) {
      scan.first_nonzero_index = index;
      scan.first_nonzero_value = value;
    }
    ++scan.nonzero_count;

    if (IsLikelyFableObjectAddress(value)) {
      if (scan.first_plausible_object_index == UINT32_MAX) {
        scan.first_plausible_object_index = index;
        scan.first_plausible_object = value;
      }
      ++scan.plausible_object_count;
    }
  }

  return scan;
}

void ObserveFableSceneFactoryCallLocked(std::string_view symbol,
                                        const RendererHookArgs& args,
                                        uint32_t result, uint8_t* base,
                                        std::string_view phase) {
  if (base == nullptr) {
    return;
  }

  const int symbol_index = FableSceneFactoryHookIndex(symbol);
  if (symbol_index < 0) {
    return;
  }
  if (!ShouldTraceFableScenePipelineHook(symbol)) {
    return;
  }

  constexpr uint32_t kSceneTableBeginAddress = 0x8349F7ACu;
  constexpr uint32_t kSceneTableEndAddress = 0x8349F7B0u;
  constexpr uint32_t kSceneTableCapacityEndAddress = 0x8349F7B4u;
  constexpr uint32_t kSceneFactoryTableBeginAddress = 0x8349F79Cu;
  constexpr uint32_t kSceneFactoryTableEndAddress = 0x8349F7A0u;
  constexpr uint32_t kSceneFactoryTableCapacityEndAddress = 0x8349F7A4u;
  const uint32_t table_begin = LoadGuestU32(base, kSceneTableBeginAddress);
  const uint32_t table_end = LoadGuestU32(base, kSceneTableEndAddress);
  const uint32_t table_capacity_end =
      LoadGuestU32(base, kSceneTableCapacityEndAddress);
  const uint32_t factory_table_begin =
      LoadGuestU32(base, kSceneFactoryTableBeginAddress);
  const uint32_t factory_table_end =
      LoadGuestU32(base, kSceneFactoryTableEndAddress);
  const uint32_t factory_table_capacity_end =
      LoadGuestU32(base, kSceneFactoryTableCapacityEndAddress);
  const uint32_t table_count =
      table_end >= table_begin ? (table_end - table_begin) / 4u : 0u;
  const uint32_t table_capacity =
      table_capacity_end >= table_begin
          ? (table_capacity_end - table_begin) / 4u
          : 0u;
  const uint32_t factory_table_count =
      factory_table_end >= factory_table_begin
          ? (factory_table_end - factory_table_begin) / 4u
          : 0u;
  const uint32_t factory_table_capacity =
      factory_table_capacity_end >= factory_table_begin
          ? (factory_table_capacity_end - factory_table_begin) / 4u
          : 0u;
  const GuestPointerTableScan scene_table_scan =
      ScanGuestPointerTable(base, table_begin, table_count);
  const GuestPointerTableScan factory_table_scan =
      ScanGuestPointerTable(base, factory_table_begin, factory_table_count);
  uint32_t observed_object = args.r3;
  uint32_t scheduler_owner = 0;
  uint32_t scheduler_lock = 0;
  uint32_t scheduler_slot = UINT32_MAX;
  uint32_t scheduler_stream_base = 0;
  uint32_t scheduler_cursor = 0;
  uint32_t scheduler_loop_cursor = 0;
  uint32_t scheduler_limit = 0;
  uint32_t scheduler_last_cursor = 0;
  uint32_t scheduler_capacity = 0;
  uint32_t scheduler_write_cursor = 0;
  uint32_t scheduler_write_mark = 0;
  uint32_t scheduler_published_end = 0;
  uint32_t scheduler_published_capacity = 0;
  uint32_t scheduler_publish_flag = 0;
  double scheduler_time = 0.0;
  bool scheduler_time_gate_blocked = false;
  uint32_t scheduler_next0 = 0;
  uint32_t scheduler_next1 = 0;
  if (symbol == "sub_82361AF8" && IsLikelyFableObjectAddress(args.r3)) {
    scheduler_owner = LoadGuestU32(base, args.r3 + 4u);
    scheduler_lock = LoadGuestU32(base, args.r3 + 8u);
    if (scheduler_owner != 0) {
      observed_object = scheduler_owner;
    }
  } else if (symbol == "sub_822DFE08") {
    scheduler_slot = args.r4 < 4u ? args.r4 : UINT32_MAX;
    if (scheduler_slot != UINT32_MAX) {
      observed_object = LoadGuestU32(base, 0x8349F9B0u + scheduler_slot * 4u);
    } else {
      for (uint32_t slot = 0; slot < 4u; ++slot) {
        const uint32_t candidate = LoadGuestU32(base, 0x8349F9B0u + slot * 4u);
        if (IsLikelyFableObjectAddress(candidate)) {
          scheduler_slot = slot;
          observed_object = candidate;
          break;
        }
      }
    }
    if (IsLikelyFableObjectAddress(observed_object)) {
      scheduler_stream_base = LoadGuestU32(base, observed_object + 4u);
      scheduler_cursor = LoadGuestU32(base, observed_object + 12u);
      scheduler_loop_cursor = LoadGuestU32(base, observed_object + 16u);
      scheduler_limit = LoadGuestU32(base, observed_object + 20u);
      scheduler_last_cursor = LoadGuestU32(base, observed_object + 24u);
      scheduler_capacity = LoadGuestU32(base, observed_object + 28u);
      scheduler_write_cursor = LoadGuestU32(base, observed_object + 32u);
      scheduler_write_mark = LoadGuestU32(base, observed_object + 40u);
      scheduler_published_end = LoadGuestU32(base, observed_object + 60u);
      scheduler_published_capacity = LoadGuestU32(base, observed_object + 68u);
      scheduler_publish_flag = LoadGuestU8(base, 0x8335F7D4u + scheduler_slot);
      scheduler_time =
          LoadGuestF64(base, 0x8335F7D8u + scheduler_slot * sizeof(double));
      scheduler_time_gate_blocked = args.r5 == 0 && scheduler_time >= args.f1;
      const uint32_t scheduler_next = scheduler_stream_base + scheduler_cursor;
      if (IsLikelyGuestDwordAddress(scheduler_next)) {
        scheduler_next0 = LoadGuestU32(base, scheduler_next);
        scheduler_next1 = LoadGuestU32(base, scheduler_next + 4u);
      }
    }
  }
  const bool object_readable =
      IsLikelyFableObjectAddress(observed_object) &&
      IsReadableGuestByteRange(base, observed_object, 324u);
  const uint32_t object_word0 =
      object_readable ? LoadGuestU32(base, observed_object) : 0u;
  const bool scene_like_object =
      object_readable && object_word0 != scheduler_stream_base;
  const uint8_t close_pending =
      scene_like_object ? LoadGuestU8(base, observed_object + 176u) : 0u;
  const uint8_t closed =
      scene_like_object ? LoadGuestU8(base, observed_object + 177u) : 0u;
  const uint8_t init_done =
      scene_like_object ? LoadGuestU8(base, observed_object + 320u) : 0u;
  const uint8_t factory_pending =
      scene_like_object ? LoadGuestU8(base, observed_object + 321u) : 0u;
  const uint8_t owner_field_322 =
      scene_like_object ? LoadGuestU8(base, observed_object + 322u) : 0u;
  const uint8_t primary_enabled =
      scene_like_object ? LoadGuestU8(base, observed_object + 313u) : 0u;
  const uint8_t primary_ready =
      scene_like_object ? LoadGuestU8(base, observed_object + 308u) : 0u;
  const uint8_t secondary_enabled =
      scene_like_object ? LoadGuestU8(base, observed_object + 314u) : 0u;
  const uint8_t secondary_ready =
      scene_like_object ? LoadGuestU8(base, observed_object + 309u) : 0u;

  static std::array<std::array<uint64_t, 2>, 30> observations = {};
  static std::array<std::array<uint64_t, 2>, 30> last_keys = [] {
    std::array<std::array<uint64_t, 2>, 30> keys = {};
    for (auto& key_pair : keys) {
      key_pair = {UINT64_MAX, UINT64_MAX};
    }
    return keys;
  }();

  const uint32_t phase_index = phase == "exit" ? 1u : 0u;
  uint64_t& count = observations[static_cast<size_t>(symbol_index)][phase_index];
  ++count;
  const uint64_t key =
      (static_cast<uint64_t>(std::min<uint32_t>(table_count, 0xFFFFu)) << 48u) ^
      (static_cast<uint64_t>(std::min<uint32_t>(table_capacity, 0xFFFFu))
       << 32u) ^
      (static_cast<uint64_t>(
           std::min<uint32_t>(factory_table_count, 0xFFu))
       << 24u) ^
      (static_cast<uint64_t>(
           std::min<uint32_t>(factory_table_capacity, 0xFFu))
       << 20u) ^
      (static_cast<uint64_t>(std::min<uint32_t>(result, 0xFFFFu)) << 16u) ^
      (static_cast<uint64_t>(object_word0 & 0xFFu) << 8u) ^
      (static_cast<uint64_t>(scheduler_cursor & 0xFFu) << 56u) ^
      (static_cast<uint64_t>(scheduler_next0 & 0xFFu) << 40u) ^
      (static_cast<uint64_t>(close_pending & 1u) << 7u) ^
      (static_cast<uint64_t>(closed & 1u) << 6u) ^
      (static_cast<uint64_t>(init_done & 1u) << 5u) ^
      (static_cast<uint64_t>(factory_pending & 1u) << 4u) ^
      (static_cast<uint64_t>(owner_field_322 & 1u) << 3u) ^
      (static_cast<uint64_t>(primary_enabled & 1u) << 2u) ^
      (static_cast<uint64_t>(primary_ready & 1u) << 1u) ^
      (secondary_enabled & 1u) ^ (static_cast<uint64_t>(secondary_ready & 1u) << 63u);
  uint64_t& last_key = last_keys[static_cast<size_t>(symbol_index)][phase_index];
  if (count <= 8u || key != last_key || IsPowerOfTwo(count)) {
    REXLOG_INFO(
        "Refii renderer Fable scene factory phase={} symbol={} count={} "
        "args r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} result=0x{:08X} "
        "object=0x{:08X} scheduler_owner=0x{:08X} scheduler_lock=0x{:08X} "
        "scheduler_slot={} stream=0x{:08X} cursor={}/{}/{} last={} cap={} "
        "write={}/{} published={}/{} publish_flag={} "
        "stream_time={:.6f} f1={:.6f} time_gate_blocked={} "
        "next={:08X}/{:08X} "
        "object_word0=0x{:08X} flags closed={}/{} init_done={} "
        "factory_pending={} field322={} primary={}/{} secondary={}/{} "
        "table=0x{:08X}-0x{:08X}/0x{:08X} count={} "
        "capacity={} scene_nonzero={} scene_objects={} first_scene={}:0x{:08X} "
        "first_scene_object={}:0x{:08X} factory_table=0x{:08X}-0x{:08X}/"
        "0x{:08X} factory_count={} factory_capacity={} factory_nonzero={} "
        "factory_objects={} first_factory={}:0x{:08X} "
        "first_factory_object={}:0x{:08X} "
        "lr=0x{:08X}",
        phase, FableHookName(symbol) ? FableHookName(symbol) : symbol.data(),
        count, args.r3, args.r4, args.r5, result, observed_object,
        scheduler_owner, scheduler_lock,
        scheduler_slot == UINT32_MAX ? 0xFFFFFFFFu : scheduler_slot,
        scheduler_stream_base, scheduler_cursor, scheduler_loop_cursor,
        scheduler_limit, scheduler_last_cursor, scheduler_capacity,
        scheduler_write_cursor, scheduler_write_mark, scheduler_published_end,
        scheduler_published_capacity, scheduler_publish_flag, scheduler_time,
        args.f1, scheduler_time_gate_blocked ? 1 : 0, scheduler_next0,
        scheduler_next1, object_word0, close_pending, closed,
        init_done, factory_pending, owner_field_322, primary_enabled,
        primary_ready, secondary_enabled, secondary_ready,
        table_begin, table_end, table_capacity_end, table_count, table_capacity,
        scene_table_scan.nonzero_count, scene_table_scan.plausible_object_count,
        scene_table_scan.first_nonzero_index,
        scene_table_scan.first_nonzero_value,
        scene_table_scan.first_plausible_object_index,
        scene_table_scan.first_plausible_object,
        factory_table_begin, factory_table_end,
        factory_table_capacity_end, factory_table_count, factory_table_capacity,
        factory_table_scan.nonzero_count,
        factory_table_scan.plausible_object_count,
        factory_table_scan.first_nonzero_index,
        factory_table_scan.first_nonzero_value,
        factory_table_scan.first_plausible_object_index,
        factory_table_scan.first_plausible_object, args.lr);
    last_key = key;
  }
}

void ObserveFableSceneTableLifecycleLocked(std::string_view symbol,
                                           const RendererHookArgs& args,
                                           uint32_t result, uint8_t* base) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kSceneTableBeginAddress = 0x8349F7ACu;
  constexpr uint32_t kSceneTableEndAddress = 0x8349F7B0u;
  constexpr uint32_t kSceneTableCapacityEndAddress = 0x8349F7B4u;
  const uint32_t table_begin = LoadGuestU32(base, kSceneTableBeginAddress);
  const uint32_t table_end = LoadGuestU32(base, kSceneTableEndAddress);
  const uint32_t table_capacity_end =
      LoadGuestU32(base, kSceneTableCapacityEndAddress);
  const uint32_t table_count =
      table_end >= table_begin ? (table_end - table_begin) / 4u : 0u;
  const uint32_t table_capacity =
      table_capacity_end >= table_begin
          ? (table_capacity_end - table_begin) / 4u
          : 0u;
  uint32_t inserted_scene = 0;
  if (symbol == "sub_82A4F8D8" && IsLikelyGuestDwordAddress(args.r6)) {
    inserted_scene = LoadGuestU32(base, args.r6);
  } else if (symbol == "sub_821E1F40" && result != UINT32_MAX &&
             result < table_count && IsLikelyGuestDwordAddress(table_begin)) {
    inserted_scene = LoadGuestU32(base, table_begin + result * 4u);
    if (IsLiveFableAssignedSceneObject(base, result, inserted_scene)) {
      g_observer.assigned_scene_objects[result] = inserted_scene;
    }
  } else if (symbol == "sub_832B2CE0") {
    g_observer.assigned_scene_objects.clear();
  }
  if (symbol == "sub_82A4F8D8") {
    RepairFableAssignedSceneTableLocked(base, "scene-table-resize", args.lr);
  }
  const uint32_t first_scene =
      table_count != 0 && IsLikelyGuestDwordAddress(table_begin)
          ? LoadGuestU32(base, table_begin)
          : 0u;
  const bool inserted_scene_readable = IsLikelyFableObjectAddress(inserted_scene);
  const uint32_t inserted_scene_type =
      inserted_scene_readable ? LoadGuestU32(base, inserted_scene + 4u) : UINT32_MAX;
  const uint32_t inserted_scene_index =
      inserted_scene_readable ? LoadGuestU32(base, inserted_scene + 16u) : UINT32_MAX;
  const uint8_t inserted_scene_enabled =
      inserted_scene_readable ? LoadGuestU8(base, inserted_scene + 72u) : 0u;

  static uint32_t logged_scene_table_lifecycle = 0;
  static uint64_t last_scene_table_key = UINT64_MAX;
  const uint64_t key =
      (static_cast<uint64_t>(table_count) << 48u) ^
      (static_cast<uint64_t>(table_capacity) << 32u) ^ table_begin ^ table_end ^
      (static_cast<uint64_t>(first_scene) << 1u) ^
      (static_cast<uint64_t>(inserted_scene) << 17u);
  const bool verbose_scene_trace = RendererVerboseTracingEnabled();
  const bool sampled_scene_trace =
      logged_scene_table_lifecycle < 64u ||
      IsPowerOfTwo(logged_scene_table_lifecycle + 1u);
  if (ShouldTraceFableScenePipelineHook(symbol) &&
      (verbose_scene_trace ? (logged_scene_table_lifecycle < 32u ||
                              key != last_scene_table_key)
                           : sampled_scene_trace)) {
    REXLOG_INFO(
        "Refii renderer Fable scene table lifecycle symbol={} result=0x{:08X} "
        "insert_pos=0x{:08X} insert_count={} insert_scene=0x{:08X} "
        "insert_type=0x{:08X} insert_index={} insert_enabled={} "
        "table=0x{:08X}-0x{:08X}/0x{:08X} count={} capacity={} "
        "first_scene=0x{:08X} lr=0x{:08X}",
        FableHookName(symbol) ? FableHookName(symbol) : symbol.data(), result,
        args.r4, args.r5,
        inserted_scene, inserted_scene_type, inserted_scene_index,
        inserted_scene_enabled, table_begin, table_end, table_capacity_end,
        table_count, table_capacity, first_scene, args.lr);
    ++logged_scene_table_lifecycle;
    last_scene_table_key = key;
  }
}

}  // namespace

void ObserveRendererGuestMemoryCopy(uint32_t dst, uint32_t src, uint32_t size,
                                    uint8_t* base) {
  if (!base || dst == 0 || size == 0 || size > kMaxScratchUploadSnapshotSize ||
      !IsLikelyScratchBufferAddress(dst)) {
    return;
  }

  std::unique_lock<std::mutex> lock(g_observer.mutex, std::try_to_lock);
  if (!lock.owns_lock()) {
    return;
  }
  RememberGuestMemoryBaseLocked(base);

  for (auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    if (!record.scratch || !IsBufferResourceKind(GuestObjectKind(record)) ||
        record.scratch_upload_source_address == 0) {
      continue;
    }

    const GuestBufferRecord buffer = GuestBufferRecordFor(record);
    if (buffer.byte_size == 0) {
      continue;
    }

    if (dst != record.scratch_upload_source_address) {
      continue;
    }

    const uint64_t prior_serial = record.serial;
    RefreshScratchResourceUploadLocked(record);
    if (record.serial == prior_serial) {
      continue;
    }

    std::vector<uint64_t> draw_serials;
    FableDirectPatchPendingDrawEvents([&](RendererDrawEventSnapshot& event) {
      if (!DrawEventReferencesBufferResource(event, record)) {
        return false;
      }
      event.resource_serial = std::max(event.resource_serial, record.serial);
      draw_serials.push_back(event.serial);
      return true;
    });

    for (uint64_t draw_serial : draw_serials) {
      AttachScratchBufferPayloadToDrawCommandLocked(
          draw_serial, GuestObjectKind(record), GuestObjectAddress(record));
    }
    FableDirectAttachScratchBufferByAddress(
        GuestObjectKind(record) == RendererResourceKind::kIndexBuffer,
        GuestObjectAddress(record), buffer.stride_or_index_format,
        record.upload_data.data(), record.upload_data.size());

    if (RendererDrawTracingEnabled() &&
        g_observer.logged_scratch_memory_copies < 12u) {
      const uint32_t sample0 =
          record.upload_data.size() >= 4u
              ? (uint32_t(record.upload_data[0]) << 24u) |
                    (uint32_t(record.upload_data[1]) << 16u) |
                    (uint32_t(record.upload_data[2]) << 8u) |
                    uint32_t(record.upload_data[3])
              : 0u;
      REXLOG_INFO(
          "Refii renderer scratch memory copy refreshed {} 0x{:08X} "
          "source=0x{:08X} copy=0x{:08X}<-0x{:08X} size={} "
          "upload={} serial={} sample0=0x{:08X}",
          GuestObjectKind(record) == RendererResourceKind::kIndexBuffer
              ? "index"
              : "vertex",
          GuestObjectAddress(record), record.scratch_upload_source_address, dst,
          src, size, record.upload_data.size(), record.serial, sample0);
      ++g_observer.logged_scratch_memory_copies;
    }
  }
}

uint32_t FindFableFrameDirectorRenderableSceneIndex(
    const RendererHookArgs& args, uint8_t* base) {
  if (base == nullptr || args.r3 != UINT32_MAX) {
    return UINT32_MAX;
  }

  std::lock_guard<std::mutex> lock(g_observer.mutex);
  RememberGuestMemoryBaseLocked(base);

  uint32_t selected_index = UINT32_MAX;
  uint32_t selected_scene = 0;
  bool selected_scene_enabled = false;
  const auto select_scene = [&](uint32_t index, uint32_t scene) {
    selected_index = index;
    selected_scene = scene;
    selected_scene_enabled = IsRenderableFableSceneObject(base, scene);
  };

  const std::array<std::pair<uint32_t, uint32_t>, 4> owner_candidates = {{
      {g_observer.last_render_frame_current_index,
       g_observer.last_render_frame_current_scene},
      {g_observer.last_render_frame_next_index,
       g_observer.last_render_frame_next_scene},
      {g_observer.last_render_frame_pending_index,
       g_observer.last_render_frame_pending_scene},
      {g_observer.last_render_frame_previous_index,
       g_observer.last_render_frame_previous_scene},
  }};
  const auto select_owner_candidate = [&](bool require_enabled) {
    for (const auto& [index, scene] : owner_candidates) {
      if (!IsBridgeableFableSceneObject(base, index, scene)) {
        continue;
      }
      if (require_enabled && !IsRenderableFableSceneObject(base, scene)) {
        continue;
      }
      select_scene(index, scene);
      return true;
    }
    return false;
  };

  bool selected_from_owner = select_owner_candidate(true);
  if (!selected_from_owner) {
    selected_from_owner = select_owner_candidate(false);
  }

  for (const auto& [index, scene] : g_observer.assigned_scene_objects) {
    if (!IsBridgeableFableSceneObject(base, index, scene)) {
      continue;
    }
    const bool scene_enabled = IsRenderableFableSceneObject(base, scene);
    if (selected_index == UINT32_MAX ||
        (scene_enabled && !selected_scene_enabled) ||
        (!selected_from_owner && scene_enabled == selected_scene_enabled &&
         index > selected_index)) {
      select_scene(index, scene);
      selected_from_owner = false;
    }
  }

  if (selected_index == UINT32_MAX) {
    uint32_t table_begin = 0;
    uint32_t table_end = 0;
    const uint32_t table_count =
        FableSceneTableCount(base, &table_begin, &table_end);
    (void)table_end;
    if (table_count != 0 && IsLikelyGuestDwordAddress(table_begin)) {
      const uint32_t capped_count = std::min<uint32_t>(table_count, 4096u);
      for (uint32_t index = 0; index < capped_count; ++index) {
        const uint32_t scene = LoadGuestU32(base, table_begin + index * 4u);
        if (!IsBridgeableFableSceneObject(base, index, scene)) {
          continue;
        }
        const bool scene_enabled = IsRenderableFableSceneObject(base, scene);
        if (selected_index != UINT32_MAX &&
            !scene_enabled && selected_scene_enabled) {
          continue;
        }
        select_scene(index, scene);
      }
    }
  }

  static uint32_t logged_bridge_candidates = 0;
  if (selected_index != UINT32_MAX &&
      (logged_bridge_candidates < 16u ||
       IsPowerOfTwo(logged_bridge_candidates + 1u))) {
    const uint8_t scene_enabled =
        IsLikelyFableObjectAddress(selected_scene)
            ? LoadGuestU8(base, selected_scene + 72u)
            : 0u;
    REXLOG_INFO(
        "Refii renderer Fable frame director exact scene bridge candidate "
        "index={} scene=0x{:08X} enabled={} assigned_count={} lr=0x{:08X} "
        "owner_current={}:0x{:08X} owner_next={}:0x{:08X} "
        "owner_pending={}:0x{:08X} owner_previous={}:0x{:08X} "
        "count={} relaxed_enabled_gate=1",
        selected_index, selected_scene, scene_enabled,
        g_observer.assigned_scene_objects.size(), args.lr,
        g_observer.last_render_frame_current_index,
        g_observer.last_render_frame_current_scene,
        g_observer.last_render_frame_next_index,
        g_observer.last_render_frame_next_scene,
        g_observer.last_render_frame_pending_index,
        g_observer.last_render_frame_pending_scene,
        g_observer.last_render_frame_previous_index,
        g_observer.last_render_frame_previous_scene,
        logged_bridge_candidates + 1u);
    ++logged_bridge_candidates;
  }

  return selected_index;
}

void ObserveRendererHookEnter(const char* symbol, RendererHookKind kind,
                              const RendererHookArgs& args, uint8_t* base) {
  const std::string_view symbol_view(symbol);
  if (kind == RendererHookKind::kDraw) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    CapturePrivateTextureRectangleWrapperEnterLocked(symbol_view, args, base);
    if (IsFableSceneSubmitScope(symbol_view)) {
      ++g_observer.scene_submit_depth;
      ObserveFableSceneWalkerEnterLocked(symbol_view, args, base);
    }
    if (IsFablePresentHook(symbol_view)) {
      ++g_observer.presents;
      ObserveDisplaySwapSurfaceLocked(symbol_view, args, base);
      if (RendererDrawTracingEnabled() &&
          (g_observer.presents <= 3 || IsPowerOfTwo(g_observer.presents))) {
        REXLOG_INFO("Refii renderer present observed count={}",
                    g_observer.presents);
      }
    } else {
      ObserveRendererStateOrDrawLocked(symbol_view, args, base);
      if (symbol_view == "rex_lhRenderer_RenderFrame") {
        ObserveFableRenderFrameOwnerLocked(args, base, "enter");
      }
      if (symbol_view == "sub_821C3858") {
        ObserveFableFrameDirectorGateLocked(args, base, "enter");
      }
    }
    return;
  }

  if (kind == RendererHookKind::kGeneral &&
      IsFableSceneEnableProbeHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneEnableUpdateLocked(symbol_view, args, UINT32_MAX, base,
                                        "enter");
    return;
  }

  if (kind == RendererHookKind::kGeneral &&
      IsFableSceneFactoryHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneFactoryCallLocked(symbol_view, args, UINT32_MAX, base,
                                       "enter");
    return;
  }

  if (kind == RendererHookKind::kGeneral &&
      IsFableSceneStreamHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneStreamCallLocked(symbol_view, args, UINT32_MAX, base,
                                      "enter");
    return;
  }

  if (kind == RendererHookKind::kGeneral && symbol_view == "sub_82BA6508") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    PatchFablePresentationParametersLocked(symbol_view, args, base);
    return;
  }

  if (IsFableCpuTextureLayoutHelperHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    LogTileHelperDeepProbeLocked("enter", symbol_view, args, base);
    return;
  }

  if (symbol_view == "sub_82B96028" || symbol_view == "sub_82B94DE0") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    LogFableTextureDecoderProbeLocked("enter", symbol_view, args, base);
    return;
  }

  if (kind != RendererHookKind::kResource) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_observer.mutex);
  std::lock_guard<std::recursive_mutex> registry_lock(
      FableDirectGuestResourceRegistryMutex());

  if (symbol_view == "sub_82D21D60") {
    RememberGuestMemoryBaseLocked(base);
    ObserveD3DXLockSurfaceUnlockEnterLocked(symbol_view, args, base);
    return;
  }

  if (IsFableResourceLockHook(symbol_view)) {
    RememberGuestMemoryBaseLocked(base);
    ++FableDirectGuestResourceRegistry().locks;
    return;
  }

  if (IsFableResourceUnlockHook(symbol_view)) {
    RememberGuestMemoryBaseLocked(base);
    ++FableDirectGuestResourceRegistry().unlocks;
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

  const uint32_t current_value = LoadGuestU32(base, args.r3);
  const uint32_t pending_value = LoadGuestU32(base, args.r3 + 4);
  if (current_value == pending_value) {
    return;
  }

  const uint32_t callback_table = LoadGuestU32(base, 0x834970E8u);
  const uint32_t callback_offset = LoadGuestU32(base, args.r3 + 16);
  const uint32_t callback_slot = callback_table + callback_offset + slot_bias;
  const uint32_t callback_target =
      callback_slot != 0 ? LoadGuestU32(base, callback_slot) : 0;
  const uint32_t dispatch_value = pending_value;
  const uint32_t sampler_slot =
      dispatch_kind == 2 ? LoadGuestU8(base, args.r3 + 22) : UINT32_MAX;

  std::lock_guard<std::mutex> lock(g_observer.mutex);
  RememberGuestMemoryBaseLocked(base);
  ++g_observer.state_dispatches;
  bool applied_state = false;
  if (dispatch_kind == 1) {
    const uint32_t render_state_slot =
        FableRenderStateSlotForAddress(callback_target);
    if (render_state_slot < kRendererD3DRenderStateSlots) {
      FableDirectSetRenderState(
          0, render_state_slot, dispatch_value,
          FableExactStateRootLocked(callback_target, args.lr, 0,
                                    render_state_slot, dispatch_value,
                                    args.r3));
      RememberRequestedDepthRenderStateLocked(render_state_slot,
                                             dispatch_value);
      applied_state = true;
    }
  } else if (dispatch_kind == 2 && sampler_slot != UINT32_MAX) {
    const uint32_t sampler_state_slot =
        FableSamplerStateSlotForAddress(callback_target);
    if (sampler_state_slot != UINT32_MAX) {
      if (sampler_slot < kRendererD3DSamplerStateSlots) {
        FableDirectStorePackedSamplerState(
            0, sampler_slot, sampler_state_slot, dispatch_value,
            FableExactStateRootLocked(callback_target, args.lr, 0,
                                      sampler_slot, dispatch_value,
                                      sampler_state_slot));
        applied_state = true;
      }
    }
  }
  ++g_observer.state_changes;
  const uint64_t count = ++g_observer.state_dispatch_targets[callback_target];
  if (RendererDrawTracingEnabled() && callback_target != 0 &&
      (count <= 3 || IsPowerOfTwo(count))) {
    const char* callback_name = FableHookAddressName(callback_target);
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
                             uint8_t* base,
                             const RendererHookArgs* exit_args) {
  const std::string_view symbol_view(symbol);
  const bool is_get_render_target =
      symbol_view == "sub_82B981D0" ||
      symbol_view == "D3DDevice_GetRenderTarget";
  if (kind == RendererHookKind::kGeneral && is_get_render_target) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveGetRenderTargetResultLocked(symbol_view, args, result, base);
  }
  if (kind == RendererHookKind::kGeneral &&
      symbol_view == "rex_lhRenderer_GetTimeDelta" && exit_args != nullptr) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableRendererTimeDeltaLocked(args, *exit_args, base);
  }
  if (kind == RendererHookKind::kGeneral &&
      (symbol_view == "sub_82A4F8D8" || symbol_view == "sub_832B2CE0" ||
       symbol_view == "sub_821E1F40")) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneTableLifecycleLocked(symbol_view, args, result, base);
  }
  if (kind == RendererHookKind::kGeneral &&
      IsFableSceneEnableProbeHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneEnableUpdateLocked(symbol_view, args, result, base, "exit");
  }
  if (kind == RendererHookKind::kGeneral &&
      IsFableSceneFactoryHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneFactoryCallLocked(symbol_view, args, result, base, "exit");
  }
  if (kind == RendererHookKind::kGeneral &&
      IsFableSceneStreamHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneStreamCallLocked(symbol_view, args, result, base, "exit");
  }
  if (kind == RendererHookKind::kDraw) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    ObserveFableSceneWalkerExitLocked(symbol_view, result);
    if (symbol_view == "rex_lhRenderer_RenderFrame") {
      ObserveFableRenderFrameOwnerLocked(args, base, "exit");
    }
    if (symbol_view == "sub_821C3858") {
      ObserveFableFrameDirectorGateLocked(args, base, "exit");
    }
    ObserveGetRenderTargetResultLocked(symbol_view, args, result, base);
    EnsureBoundShaderAndVertexDeclarationTrackedLocked(symbol_view, args, base);
    if (GuestDeviceStateReadableForDirectRender() &&
        (symbol_view == "sub_822869A0" ||
         symbol_view == "D3DDevice_SetRenderTarget")) {
      RefreshBoundRenderTargetsLocked(
          base, args.r3,
          FableExactStateRootLocked(0x822869A0u, args.lr, args.r3, args.r4,
                                    args.r5));
    } else if (GuestDeviceStateReadableForDirectRender() &&
               (symbol_view == "sub_82264590" ||
                symbol_view == "D3DDevice_SetDepthStencilSurface")) {
      RefreshEffectiveDepthRenderStateLocked(
          base, args.r3,
          FableExactStateRootLocked(0x82264590u, args.lr, args.r3, 4u,
                                    args.r4));
    } else if (symbol_view == "sub_822A09A0") {
      ObserveSetColorClearRootLocked(args, base);
    }
    if (symbol_view == "sub_82265CD8") {
      QueueDrawVerticesUpEventLocked(args, result, base);
    }
    if (symbol_view == "sub_82219740" && result != 0 &&
        g_observer.scene_batch_draw_marker.valid &&
        !g_observer.scene_batch_draw_marker.queued) {
      const FableSceneBatchDrawMarker marker =
          g_observer.scene_batch_draw_marker;
      RefreshBoundSurfacesLocked(base, marker.device);
      RendererLastDrawSnapshot last_draw = {
          .indexed = false,
          .primitive_type = marker.primitive_type,
          .base_vertex = 0,
          .start = 0,
          .primitive_count = SceneBatchInlineVertexCount(marker),
      };
      ++g_observer.draws;
      ++g_observer.scene_object_draws;
      ++g_observer.scene_batch_bridge_queued_draws;
      QueueDrawEventLocked(marker.device, false, true,
                           RendererDrawEventSource::kSceneObject, last_draw);
      g_observer.scene_batch_draw_marker.queued = true;
      TraceDrawCall(symbol_view, last_draw);
    }
    const bool patch_scratch_after_pm4 =
        symbol_view == "sub_82217DB8" &&
        !g_observer.active_wrapped_draw_scratch_captured;
    if (!patch_scratch_after_pm4) {
      const bool captured_current_scratch =
          CaptureScratchDrawBuffersLocked(symbol_view, args, result, base);
      if (captured_current_scratch &&
          g_observer.active_wrapped_draw_indexed_scratch) {
        g_observer.active_wrapped_draw_scratch_captured = true;
      }
    }
    if (!IsFableCommandWriteHook(symbol_view)) {
      DecodePm4WordsLocked(symbol, args, base);
    }
    if (symbol_view != "sub_82217DB8" &&
        g_observer.active_wrapped_draw_indexed_scratch &&
        !g_observer.active_wrapped_draw_scratch_captured) {
      g_observer.active_wrapped_draw_scratch_captured =
          CaptureScratchDrawBuffersLocked(
              "sub_82217DB8", g_observer.active_wrapped_draw_args, 0,
              base);
      if (CurrentDirectPixelShaderIsBinkMovieLocked() &&
          QueueBinkDecodedPlaneVideoDrawEventLocked(
              g_observer.active_wrapped_draw_args, base)) {
        RetireActiveWrappedDrawAfterSyntheticReplacementLocked(
            "bink-video-delayed-capture");
      }
    }
    if (IsFableBeginVerticesWrapperSymbol(symbol_view) &&
        !g_observer.active_wrapped_draw_indexed_scratch &&
        !g_observer.active_wrapped_draw_scratch_captured) {
      g_observer.active_wrapped_draw_scratch_captured =
          CaptureScratchDrawBuffersLocked(symbol_view, args, result, base);
    }
    if (patch_scratch_after_pm4) {
      g_observer.active_wrapped_draw_scratch_captured =
          CaptureScratchDrawBuffersLocked(symbol_view, args, result, base);
      if (g_observer.active_wrapped_draw_indexed_scratch &&
          CurrentDirectPixelShaderIsBinkMovieLocked() &&
          QueueBinkDecodedPlaneVideoDrawEventLocked(
              g_observer.active_wrapped_draw_args, base)) {
        RetireActiveWrappedDrawAfterSyntheticReplacementLocked(
            "bink-video-pm4-capture");
      }
    }
    if (IsFableCommandWriteHook(symbol_view)) {
      PatchActiveWrappedDrawEventStateLocked();
    }
    EndWrappedDrawStatePatchLocked(symbol_view, args);
    if (exit_args != nullptr) {
      if (symbol_view == "sub_82BA7B28") {
        const uint32_t source_a =
            FableStackAddressAfterReturn(*exit_args, 864u, 252u);
        LogExactTextureWrapperSourceLocked(
            symbol_view, args, *exit_args, base, 864u, 224u, source_a,
            0xFFFFFFFFu);
        const uint32_t source_b =
            FableStackAddressAfterReturn(*exit_args, 864u, 652u);
        LogExactTextureWrapperSourceLocked(
            symbol_view, args, *exit_args, base, 864u, 624u, source_b,
            0xFFFFFFFFu);
      } else if (symbol_view == "sub_82BA83C0") {
        const uint32_t source =
            args.r8 <= std::numeric_limits<uint32_t>::max() - 28u
                ? args.r8 + 28u
                : 0u;
        LogExactTextureWrapperSourceLocked(symbol_view, args, *exit_args, base,
                                           544u, 336u, source, 0u);
      } else if (symbol_view == "sub_82BA8EF8") {
        const uint32_t source =
            FableStackAddressAfterReturn(*exit_args, 1312u, 236u);
        LogExactTextureWrapperSourceLocked(
            symbol_view, args, *exit_args, base, 1312u, 208u, source,
            0xFFFFFFFFu);
      }
    }
    if (symbol_view == "sub_82BA8928") {
      CapturePixDecodedVideoFrameUploadLocked(args, result, base);
    }
    if (symbol_view == "sub_82BA7B28" &&
        (RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
        g_observer.logged_pix_conversion_draw_chains < 16u) {
      const FableDirectStateSnapshot direct_snapshot =
          GetFableDirectStateSnapshot();
      REXLOG_INFO(
          "Refii renderer Pix/video conversion chain #{} result=0x{:08X} "
          "device=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
          "width={} height={} r9=0x{:08X} r10=0x{:08X} lr=0x{:08X} "
          "rt={:08X}/{:08X}/{:08X}/{:08X} depth=0x{:08X}",
          g_observer.logged_pix_conversion_draw_chains + 1u, result, args.r3,
          args.r4, args.r5, args.r6, args.r7, args.r8, args.r9, args.r10,
          args.lr, direct_snapshot.targets.color[0],
          direct_snapshot.targets.color[1], direct_snapshot.targets.color[2],
          direct_snapshot.targets.color[3], direct_snapshot.targets.depth);
      ++g_observer.logged_pix_conversion_draw_chains;
    }
    if (symbol_view == "sub_82BA8EF8" &&
        (RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
        g_observer.logged_pix_render_packet_builders < 16u) {
      const FableDirectStateSnapshot direct_snapshot =
          GetFableDirectStateSnapshot();
      REXLOG_INFO(
          "Refii renderer Pix/video packet builder #{} result=0x{:08X} "
          "device=0x{:08X} frame=0x{:08X} fmt=0x{:08X} size={}x{} "
          "packet=0x{:08X} aux=0x{:08X} flags=0x{:08X} lr=0x{:08X} "
          "rt={:08X}/{:08X}/{:08X}/{:08X} depth=0x{:08X}",
          g_observer.logged_pix_render_packet_builders + 1u, result, args.r3,
          args.r4, args.r5, args.r6, args.r7, args.r8, args.r9, args.r10,
          args.lr, direct_snapshot.targets.color[0],
          direct_snapshot.targets.color[1], direct_snapshot.targets.color[2],
          direct_snapshot.targets.color[3], direct_snapshot.targets.depth);
      ++g_observer.logged_pix_render_packet_builders;
    }
    if (symbol_view == "sub_82206888") {
      QueuePrivateTextureRectangleDrawEventLocked(
          args, base, true /* prefer_rect_argument */);
      FableDirectCaptureExactNonVideoRenderCommands(
          g_observer.last_queued_draw_serial);
    }
    if (symbol_view == "sub_82BA83C0") {
      QueuePixRenderVideoFrameDrawEventLocked(args, base);
    }
    if (symbol_view == "sub_82BA95E0") {
      ++g_observer.pix_capture_hooks;
      if (IsLikelyFableObjectAddress(args.r3)) {
        g_observer.last_video_device = args.r3;
      }
      const FableDirectStateSnapshot direct_snapshot =
          GetFableDirectStateSnapshot();
      const uint32_t source_target = direct_snapshot.targets.color[0];
      const uint32_t display_surface = g_observer.display_present_surface_object;
      if (source_target != 0 && display_surface != 0) {
        RecordSurfaceCopyAliasLocked(symbol_view, source_target,
                                     display_surface);
      }
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
    PopPrivateTextureRectangleWrapperExitLocked(symbol_view, args, base);
    if (symbol_view == "sub_821F9D00") {
      // D3DDevice_SetViewport forwards here after Fable clamps to the active
      // target. In direct-replacement mode the native body is suppressed, so
      // use the hook arguments first and mirror the effective device fields
      // ourselves instead of reading stale device state.
      g_observer.viewport_pointer = 0;
      RendererViewportSnapshot viewport =
          DecodeGuestViewport(
              static_cast<float>(args.f1), static_cast<float>(args.f2),
              static_cast<float>(args.f3), static_cast<float>(args.f4),
              static_cast<float>(args.f5), static_cast<float>(args.f6));
      if (!viewport.valid) {
        viewport = DecodeGuestViewportFromDevice(base, args.r3);
      }
      if (viewport.valid) {
        const FableSurfaceExtent extent =
            CurrentFableDrawSurfaceExtent(base, args.r3);
        if (extent.width != 0 && extent.height != 0 &&
            viewport.x >= 0.0f && viewport.y >= 0.0f) {
          viewport.width = std::min(
              viewport.width,
              std::max(0.0f, static_cast<float>(extent.width) - viewport.x));
          viewport.height = std::min(
              viewport.height,
              std::max(0.0f, static_cast<float>(extent.height) - viewport.y));
        }
        ApplyEffectiveViewportToGuestDeviceLocked(
            base, args.r3, viewport,
            FableExactStateRootLocked(0x821F9D00u, args.lr, args.r3, 0u,
                                      args.r4));
      }
      const RendererViewportSnapshot effective_viewport =
          GetFableDirectStateSnapshot().viewport;
      if (RendererVerboseTracingEnabled() && effective_viewport.valid) {
        REXLOG_INFO(
            "Refii renderer viewport decoded direct target-local x={} y={} "
            "w={} h={} minz={} maxz={}",
            effective_viewport.x, effective_viewport.y,
            effective_viewport.width, effective_viewport.height,
            effective_viewport.min_depth, effective_viewport.max_depth);
      }
    } else if (symbol_view == "sub_821F9C08" ||
               symbol_view == "sub_821F9918") {
      // D3DDevice_SetScissorRect reaches D3D::SetSurfaceClip after clipping to
      // the current viewport. For the internal SetSurfaceClip hook the native
      // update is suppressed, so the arguments are the authoritative clip.
      g_observer.scissor_pointer = 0;
      RendererScissorSnapshot scissor = {};
      if (symbol_view == "sub_821F9918") {
        scissor = DecodeGuestScissor(
            static_cast<int32_t>(args.r4), static_cast<int32_t>(args.r5),
            static_cast<int32_t>(args.r6), static_cast<int32_t>(args.r7));
      }
      if (!scissor.valid) {
        scissor = DecodeGuestPackedScissorFromDevice(base, args.r3);
      }
      if (!scissor.valid) {
        scissor = DecodeGuestScissor(
            static_cast<int32_t>(args.r4), static_cast<int32_t>(args.r5),
            static_cast<int32_t>(args.r6), static_cast<int32_t>(args.r7));
      }
      RendererScissorSnapshot effective_scissor =
          ClampScissorToViewport(scissor, GetFableDirectStateSnapshot().viewport);
      effective_scissor =
          ScaleGuestScissorToCurrentDrawSurface(base, args.r3, effective_scissor);
      StoreFableScissorToGuestDeviceLocked(base, args.r3, effective_scissor);
      const uint32_t scissor_root_symbol =
          symbol_view == "sub_821F9C08" ? 0x821F9C08u : 0x821F9918u;
      FableDirectSetScissor(
          args.r3, effective_scissor,
          FableExactStateRootLocked(scissor_root_symbol, args.lr, args.r3, 0u,
                                    args.r4, args.r5));
      if (RendererVerboseTracingEnabled() && effective_scissor.valid) {
        REXLOG_INFO(
            "Refii renderer scissor decoded direct target-local l={} t={} "
            "r={} b={}",
            effective_scissor.left, effective_scissor.top,
            effective_scissor.right, effective_scissor.bottom);
      }
    }
    if (symbol_view == "sub_8221D1B0") {
      ObservePendingFetchConstantsLocked(args, base);
    } else if (symbol_view == "sub_8221C7D8") {
      ObservePendingRegisterRangeLocked(args, base);
    } else if (symbol_view == "sub_8228EF80") {
      ObservePendingSplitRegisterRangeLocked(args, base);
    } else if (symbol_view == "sub_821B6EE0" && args.r4 < 16 &&
               args.r5 != 0) {
      StoreBoundTextureObjectFetchDwordsLocked(args.r3, args.r4, args.r5,
                                               base, args.lr);
    }
    if (!IsFablePresentedDrawCaptureHook(symbol_view)) {
      RefreshPendingDrawScratchResourceUploadsLocked();
    }
    if (IsFableWrappedDrawSubmitHook(symbol_view)) {
      PatchDrawEventDeviceBindingsLocked(g_observer.last_queued_draw_serial,
                                         symbol_view, args.r3, base);
      if (symbol_view == "sub_82205F68" &&
          g_observer.last_queued_draw_serial != 0) {
        FableDirectPatchDrawEvent(
            g_observer.last_queued_draw_serial,
            [&](RendererDrawEventSnapshot& event) {
          StampBeginVerticesWrapperIdentityLocked(event, args);
          RefreshDrawEventResourceSerialFromBindingsLocked(event);
          return true;
        });
      }
    }
    if (IsFablePresentedDrawCaptureHook(symbol_view)) {
      SanitizePendingDrawEventsLocked();
    }
    if (IsFableSceneSubmitScope(symbol_view) &&
        g_observer.scene_submit_depth != 0) {
      const FableDirectStateSnapshot direct_snapshot =
          GetFableDirectStateSnapshot();
      const bool interesting_scene_scope =
          g_observer.last_scene_walker_count != 0 ||
          g_observer.scene_batch_draw_marker.valid ||
          direct_snapshot.render_command_count != 0 ||
          direct_snapshot.draw_event_count != 0 ||
          direct_snapshot.last_command_boundary_pending_draws != 0 ||
          direct_snapshot.last_submitted_draw_event_count != 0;
      if (interesting_scene_scope &&
          g_observer.logged_scene_submit_scope_samples < 32u) {
        REXLOG_INFO(
            "Refii renderer scene submit scope exit #{} symbol={} depth={} "
            "walker=0x{:08X} count={} result=0x{:08X} batch_valid={} "
            "batch_dev=0x{:08X} batch_record=0x{:08X} "
            "commands={} draw_events={} direct_events={} lifetime_draws={} indexed={} "
            "state={} clears={} resolves={} sources={} {} {} {} {} {} {} {} {} {} "
            "boundary_draws={} submitted_draws={} last_draw_serial={} "
            "last_draw_source={} last_rt=0x{:08X}",
            ++g_observer.logged_scene_submit_scope_samples, symbol_view,
            g_observer.scene_submit_depth, g_observer.last_scene_walker_list,
            g_observer.last_scene_walker_count,
            g_observer.last_scene_walker_result,
            g_observer.scene_batch_draw_marker.valid ? 1 : 0,
            g_observer.scene_batch_draw_marker.device,
            g_observer.scene_batch_draw_marker.record_address,
            direct_snapshot.render_command_count,
            direct_snapshot.draw_event_count, direct_snapshot.direct_event_count,
            direct_snapshot.draw_count,
            direct_snapshot.indexed_draw_count,
            direct_snapshot.pending_state_command_count,
            direct_snapshot.pending_clear_command_count,
            direct_snapshot.pending_resolve_command_count,
            direct_snapshot.pending_draw_source_counts[0],
            direct_snapshot.pending_draw_source_counts[1],
            direct_snapshot.pending_draw_source_counts[2],
            direct_snapshot.pending_draw_source_counts[3],
            direct_snapshot.pending_draw_source_counts[4],
            direct_snapshot.pending_draw_source_counts[5],
            direct_snapshot.pending_draw_source_counts[6],
            direct_snapshot.pending_draw_source_counts[7],
            direct_snapshot.pending_draw_source_counts[8],
            direct_snapshot.pending_draw_source_counts[9],
            direct_snapshot.last_command_boundary_pending_draws,
            direct_snapshot.last_submitted_draw_event_count,
            static_cast<unsigned long long>(g_observer.last_queued_draw_serial),
            static_cast<uint32_t>(g_observer.last_queued_draw_source),
            g_observer.last_queued_draw_render_target);
      }
      --g_observer.scene_submit_depth;
    }
    return;
  }

  if (IsFableBinkVideoUploadHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    RefreshBinkVideoPlaneUploadsLocked(symbol_view, base);
    return;
  }

  const FableShaderConstantHookKind shader_constant_hook =
      FableShaderConstantHookKindForSymbol(symbol_view);
  if (shader_constant_hook != FableShaderConstantHookKind::kNone) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderAndVertexDeclarationTrackedLocked(symbol_view, args, base);
    const ShaderConstantBank bank = ToShaderConstantBank(
        FableShaderConstantBankForSymbol(symbol_view));
    switch (shader_constant_hook) {
      case FableShaderConstantHookKind::kImmediateFloatVectors:
        ObserveImmediateShaderConstantsLocked(args, base);
        break;
      case FableShaderConstantHookKind::kTextureFetch:
        ObserveTextureFetchConstantLocked(args, base);
        break;
      case FableShaderConstantHookKind::kFloatVectors:
        StoreShaderConstantVectorsLocked(bank, args.r4, args.r6, args.r5,
                                         base, args.lr);
        break;
      case FableShaderConstantHookKind::kIntVectors:
        StoreShaderIntConstantsLocked(bank, args.r4, args.r6, args.r5, base,
                                      args.lr);
        break;
      case FableShaderConstantHookKind::kBoolConstants:
        StoreShaderBoolConstantsLocked(bank, args.r4, args.r6, args.r5, base,
                                       args.lr);
        break;
      case FableShaderConstantHookKind::kLiteralFloatDwords:
        ObserveLiteralShaderConstantsLocked(args, base);
        break;
      case FableShaderConstantHookKind::kNone:
        break;
    }
    RefreshPendingDrawScratchResourceUploadsLocked();
    return;
  }

  const FableRootTextureHeaderHookKind root_texture_header_hook =
      FableRootTextureHeaderHookKindForSymbol(symbol_view);
  if (root_texture_header_hook != FableRootTextureHeaderHookKind::kNone) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderAndVertexDeclarationTrackedLocked(symbol_view, args, base);
    uint32_t texture_header = args.r3;
    const char* source_name = "OffsetBaseTextureAddress";
    if (root_texture_header_hook ==
        FableRootTextureHeaderHookKind::kSetTextureHeader) {
      // sub_821FB650 writes the 52-byte Xenos texture/fetch header through its
      // a34 out pointer, which is stack arg +0x6C from the caller's frame.
      texture_header = LoadHookStackU32(base, args, 108u);
      source_name = "SetTextureHeader";
    } else if (root_texture_header_hook ==
               FableRootTextureHeaderHookKind::kSetBaseTextureHeader) {
      // XGRAPHICS::SetBaseTextureHeader copies the completed 52-byte header to
      // a38, which is stack arg +0x7C from the caller's frame.
      texture_header = LoadHookStackU32(base, args, 124u);
      source_name = symbol_view == "sub_821FBB78" ? "XGSetTextureHeader"
                                                  : "SetBaseTextureHeader";
    } else if (root_texture_header_hook ==
               FableRootTextureHeaderHookKind::kSetLineOrCubeTextureHeader) {
      // XGSetLineTextureHeader/XGSetCubeTextureHeader forward their output
      // header as the eighth argument, which is still in r10 at hook entry.
      texture_header = args.r10;
      source_name = symbol_view == "sub_8300BEE0" ? "XGSetLineTextureHeader"
                                                  : "XGSetCubeTextureHeader";
    }
    CaptureRootTextureHeaderLocked(symbol_view, args, base, texture_header,
                                   source_name);
    return;
  }

  if (symbol_view == "sub_821F5028") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    EnsureBoundShaderAndVertexDeclarationTrackedLocked(symbol_view, args, base);
    if (base && args.r8 != 0 && IsLikelyFableObjectAddress(args.r8)) {
      const RendererResourceKind surface_kind =
          FableSetSurfaceHeaderFormatIsDepth(args.r5) &&
                  !FableSurfaceHeaderFormatIsAmbiguousColorDepth(args.r5)
              ? RendererResourceKind::kDepthSurface
              : RendererResourceKind::kRenderTargetSurface;
      const bool tracked =
          TrackBoundSurfaceLocked(surface_kind, args.r8, 0, base);
      const uint32_t packed_extent = LoadGuestU32(base, args.r8 + 0x24u);
      const uint32_t surface_width = (packed_extent >> 18u) + 1u;
      const uint32_t surface_height = ((packed_extent >> 3u) & 0x7FFFu) + 1u;
      if (tracked && surface_kind == RendererResourceKind::kRenderTargetSurface &&
          g_observer.last_root_texture_header != 0 &&
          args.r8 != g_observer.last_root_texture_header) {
        const bool framebuffer_sized =
            g_observer.last_root_texture_width >= 320u &&
            g_observer.last_root_texture_height >= 180u;
        const bool matches_root_texture =
            framebuffer_sized &&
            surface_width == g_observer.last_root_texture_width &&
            surface_height == g_observer.last_root_texture_height;
        if (matches_root_texture) {
          RecordRootSurfaceTextureSiblingLocked(
              args.r8, g_observer.last_root_texture_header);
        }
        g_observer.last_root_texture_header = 0;
        g_observer.last_root_texture_width = 0;
        g_observer.last_root_texture_height = 0;
        g_observer.last_root_texture_format = 0;
        g_observer.last_root_texture_serial = 0;
      }
      if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
          g_observer.logged_inline_surface_descriptor_builds < 16u) {
        REXLOG_INFO(
            "Refii renderer root surface header #{} desc=0x{:08X} kind={} "
            "tracked={} extent={}x{} fmt=0x{:08X} arg_fmt=0x{:08X} "
            "pitch=0x{:08X} size=0x{:08X} lr=0x{:08X}",
            g_observer.logged_inline_surface_descriptor_builds + 1u, args.r8,
            ResourceKindName(surface_kind), tracked ? 1 : 0, surface_width,
            surface_height,
            LoadGuestU32(base, args.r8 + 0x28u), args.r5,
            LoadGuestU32(base, args.r8 + 0x18u),
            LoadGuestU32(base, args.r8 + 0x2Cu), args.lr);
        ++g_observer.logged_inline_surface_descriptor_builds;
      }
    }
    return;
  }

  if (symbol_view == "sub_82B96028" || symbol_view == "sub_82B94DE0") {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    LogFableTextureDecoderProbeLocked("exit", symbol_view, args, base);
    return;
  }

  if (kind == RendererHookKind::kGeneral) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    EnsureBoundShaderAndVertexDeclarationTrackedLocked(symbol_view, args, base);
    return;
  }

  if (IsFableCpuTextureLayoutHelperHook(symbol_view)) {
    std::lock_guard<std::mutex> lock(g_observer.mutex);
    RememberGuestMemoryBaseLocked(base);
    LogTileHelperDeepProbeLocked("exit", symbol_view, args, base);
    const uint32_t dst =
        symbol_view == "sub_8300CB48" ? args.r8 : args.r3;
    const uint32_t width =
        symbol_view == "sub_8300CB48" ? args.r3 : args.r4;
    const uint32_t height =
        symbol_view == "sub_8300CB48" ? args.r4 : args.r5;
    const uint32_t point =
        symbol_view == "sub_8300CB48" ? args.r9 : args.r6;
    const uint32_t src =
        symbol_view == "sub_8300CB48" ? args.r10 : args.r7;
    const uint32_t pitch =
        symbol_view == "sub_8300CB48"
            ? LoadHookStackU32(base, args, 84u)
            : args.r8;
    const uint32_t rect =
        symbol_view == "sub_8300CB48"
            ? LoadHookStackU32(base, args, 92u)
            : args.r9;
    const uint32_t bits = symbol_view == "sub_8300CB48" ? 0u : args.r10;
    const bool title_layout_probe =
        IsFableTitleTextureProbeAddress(dst) ||
        IsFableTitleTextureProbeAddress(src);
    if (title_layout_probe && RendererResourceTracingEnabled() &&
        g_observer.logged_title_texture_upload_probes <
            kMaxTitleTextureUploadProbeLogs) {
      REXLOG_INFO(
          "Refii renderer title texture layout helper probe #{} hook={} "
          "dst=0x{:08X} width={} height={} point=0x{:08X} src=0x{:08X} "
          "src_pitch={} rect=0x{:08X} bits={}",
          g_observer.logged_title_texture_upload_probes + 1u, symbol_view,
          dst, width, height, point, src, pitch, rect, bits);
      ++g_observer.logged_title_texture_upload_probes;
    }
    const bool refresh_layout_uploads =
        title_layout_probe || IsLikelyGpuByteBufferRange(dst, 1u) ||
        IsLikelyHighScratchByteBufferRange(dst, 1u);
    if (refresh_layout_uploads) {
      const uint32_t refreshed_layout_textures =
          RefreshTextureUploadFromCpuLayoutDestinationCallsiteLocked(
          symbol_view, args, dst, width, height, pitch, bits, base);
      if ((RendererResourceTracingEnabled() || RendererVerboseTracingEnabled()) &&
          g_observer.logged_tile_helper_refresh_dispatches < 24u) {
        REXLOG_INFO(
            "Refii renderer tile helper refresh dispatch #{} hook={} "
            "dst=0x{:08X} src=0x{:08X} {}x{} pitch={} bits={} "
            "lr=0x{:08X} refreshed={} title_probe={}",
            g_observer.logged_tile_helper_refresh_dispatches + 1u, symbol_view,
            dst, src, width, height, pitch, bits, args.lr,
            refreshed_layout_textures, title_layout_probe ? 1 : 0);
        ++g_observer.logged_tile_helper_refresh_dispatches;
      }
      RefreshLockedTextureUploadAfterCpuTextureLayoutHelperLocked(
          symbol_view, dst, base);
      if (title_layout_probe) {
        RefreshTextureUploadsAfterCpuTextureLayoutHelperLocked(symbol_view,
                                                               base);
      }
    }
    return;
  }

  if (kind != RendererHookKind::kResource) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_observer.mutex);
  std::lock_guard<std::recursive_mutex> registry_lock(
      FableDirectGuestResourceRegistryMutex());

  if (symbol_view == "sub_82D21D60") {
    RememberGuestMemoryBaseLocked(base);
    ObserveD3DXLockSurfaceUnlockExitLocked(symbol_view, args, result, base);
    return;
  }

  if (symbol_view == "sub_82D21EC0") {
    RememberGuestMemoryBaseLocked(base);
    ObserveD3DXLockSurfaceLockExitLocked(symbol_view, args, result, base);
    return;
  }

  if (symbol_view == "sub_82D160D8" || symbol_view == "sub_82D21C68" ||
      symbol_view == "sub_82D1EF90" || symbol_view == "sub_82D14B20" ||
      symbol_view == "sub_83013A70") {
    RememberGuestMemoryBaseLocked(base);
    ObserveSurfaceCopyAliasLocked(symbol_view, args, result);
    if (result == 0 &&
        (symbol_view == "sub_82D160D8" ||
         symbol_view == "sub_82D21C68" ||
         symbol_view == "sub_82D14B20")) {
      RefreshTextureUploadsAfterCpuSurfaceCopyLocked(symbol_view, base);
    }
    return;
  }

  if (symbol_view == "sub_82D14378") {
    RememberGuestMemoryBaseLocked(base);
    ObserveD3DXCreateTextureFromTextureExitLocked(symbol_view, args, result,
                                                 base);
    return;
  }

  if (symbol_view == "sub_82A42FA0") {
    RememberGuestMemoryBaseLocked(base);
    ObserveBinkVideoPlaneSetupLocked(args, base);
    return;
  }

  if (symbol_view == "sub_821FC178") {
    RememberGuestMemoryBaseLocked(base);
    if (auto it = FindResourceIteratorLocked(args.r3);
        it != FableDirectGuestResourceRegistry().resources.end()) {
      SetGuestResourceRawRefCount(it->second, result);
      FableDirectGuestResourceRegistry().last_resource = it->second;
    }
    return;
  }

  if (symbol_view == "sub_821FC320") {
    if (result != 0) {
      return;
    }

    RendererResourceRecord erased_record;
    bool had_record = false;
    bool neutral_record = false;
    bool destroyed_now = false;
    {
      RememberGuestMemoryBaseLocked(base);
      auto it = FindResourceIteratorLocked(args.r3);
      if (it != FableDirectGuestResourceRegistry().resources.end()) {
        erased_record = it->second;
        had_record = true;
        neutral_record = IsNeutralTextureResource(erased_record);
        const bool title_texture_record =
            IsFableTitleTextureResource(erased_record);
        const bool defer_surface_release =
            IsSurfaceResourceKind(GuestObjectKind(erased_record)) &&
            ActiveSurfaceStateReferencesResourceLocked(erased_record);
        const bool defer_pipeline_release =
            (IsShaderResourceKind(GuestObjectKind(erased_record)) ||
             GuestObjectKind(erased_record) ==
                 RendererResourceKind::kVertexDeclaration) &&
            ActivePipelineStateReferencesResourceLocked(erased_record);
        const bool defer_queued_image_release =
            IsImageResourceKind(GuestObjectKind(erased_record)) &&
            PendingDrawEventsReferenceResourceLocked(erased_record);
        RememberRecentSurfaceShapeLocked(erased_record);
        if (neutral_record || title_texture_record) {
          SetGuestResourceRawRefCount(it->second, result);
          it->second.serial = ++g_observer.serial;
          FableDirectGuestResourceRegistry().last_resource = it->second;
          MarkResourceChangedLocked(it->second);
        } else if (defer_surface_release || defer_pipeline_release ||
                   defer_queued_image_release) {
          SetGuestResourceRawRefCount(it->second, result);
          g_observer.deferred_surface_releases.insert(
              GuestObjectAddress(erased_record));
          FableDirectGuestResourceRegistry().last_resource = it->second;
          it->second.serial = ++g_observer.serial;
          MarkResourceChangedLocked(it->second);
        } else {
          g_observer.deferred_surface_releases.erase(
              GuestObjectAddress(erased_record));
          FableDirectGuestResourceRegistry().resources.erase(it);
          destroyed_now = true;
        }
      }
      FlushDeferredSurfaceReleasesLocked();
      if (!neutral_record && (destroyed_now || !had_record)) {
        ++FableDirectGuestResourceRegistry().destroys;
        ++g_observer.serial;
        if (destroyed_now) {
          MarkResourceDestroyedLocked(args.r3, g_observer.serial);
        }
        if (had_record && destroyed_now) {
          MarkResourceDestroyedLocked(GuestObjectAddress(erased_record),
                                      g_observer.serial);
        }
      }
    }
    if (!neutral_record) {
      TraceResourceDestroy(args.r3, had_record ? &erased_record : nullptr);
    }
    return;
  }

  if (IsFableResourceLockHook(symbol_view) && result != 0) {
    if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
        g_observer.logged_lock_resource_shapes < 192u) {
      uint32_t texture_object = 0;
      uint32_t surface_object = 0;
      uint32_t block_or_level = 0;
      const bool texture_lock = ResolveTextureLockObject(
          symbol_view, args, base, texture_object, surface_object,
          block_or_level);
      const std::array<uint32_t, 6> dwords =
          texture_lock
              ? LoadTextureFetchDwords(base, texture_object + 28u)
              : base != nullptr && IsLikelyFableObjectAddress(args.r3)
                    ? LoadTextureFetchDwords(base, args.r3 + 28u)
              : std::array<uint32_t, 6>{};
      const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
      const bool title_lock =
          IsFableTitleTextureProbeAddress(texture_object) ||
          IsFableTitleTextureProbeAddress(args.r3) ||
          IsFableTitleTextureProbeAddress(info.base_address);
      if (title_lock || g_observer.logged_lock_resource_shapes < 32u) {
        REXLOG_INFO(
            "Refii renderer lock resource shape #{} symbol={} "
            "object=0x{:08X} texture=0x{:08X} surface=0x{:08X} "
            "result=0x{:08X} block_or_level={} flags=0x{:08X} size_arg={} "
            "tex_header={} base=0x{:08X} size={} {}x{} fmt={} "
            "dwords={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
            g_observer.logged_lock_resource_shapes + 1u,
            FableHookDisplayName(symbol_view), args.r3, texture_object,
            surface_object, result, texture_lock ? block_or_level : args.r5,
            args.r4, args.r10,
            HasTextureFetchDwords(dwords) ? 1 : 0, info.base_address,
            info.size, info.width, info.height, info.format, dwords[0],
            dwords[1], dwords[2], dwords[3], dwords[4], dwords[5]);
        ++g_observer.logged_lock_resource_shapes;
      }
    }
    uint32_t lock_texture_object = 0;
    uint32_t lock_surface_object = 0;
    uint32_t lock_block_or_level = 0;
    const bool exact_texture_lock = ResolveTextureLockObject(
        symbol_view, args, base, lock_texture_object, lock_surface_object,
        lock_block_or_level);
    RendererResourceRecord* record =
        exact_texture_lock ? nullptr : FindResourceRecordLocked(args.r3);
    if (record != nullptr && !IsImageResourceKind(GuestObjectKind(*record)) &&
        (exact_texture_lock ||
         LooksLikeTextureLockResource(symbol_view, args, base))) {
      record = nullptr;
    }
    EnsureTextureRecordForLockLocked(symbol_view, args, base, record);
    if (record == nullptr && base) {
      const RendererResourceKind locked_kind =
          FableLockedBufferRendererResourceKind(symbol_view);
      if (locked_kind == RendererResourceKind::kVertexBuffer ||
          locked_kind == RendererResourceKind::kIndexBuffer) {
        RendererResourceRecord created =
            CreateGuestBufferResource(locked_kind, args.r3);
        SnapshotGuestResource(created, base);
        const GuestBufferRecord created_buffer =
            GuestBufferRecordFor(created);
        if (created_buffer.gpu_address != 0 && created_buffer.byte_size != 0) {
          created.serial = ++g_observer.serial;
          record = &StoreGuestResourceLocked(std::move(created), true, true);
        }
      }
    }
    if (record) {
      record->locked_address = result;
      record->locked_write_address = LockedResourceWriteAddressForHook(
          symbol_view, args, result, base, *record);
      record->locked_size = LockedResourceSizeForHook(symbol_view, args, *record);
      if (IsImageResourceKind(GuestObjectKind(*record)) &&
          record->locked_address != 0 && record->locked_size != 0) {
        const std::array<uint32_t, 6> dwords =
            TextureFetchDwordsForRecord(*record);
        const TextureFetchSnapshotInfo info =
            GetTextureFetchSnapshotInfo(dwords);
        if (IsExactKnownTitleTextureRefreshResource(*record, info)) {
          uint32_t root_header = 0;
          if (exact_texture_lock && lock_texture_object != 0) {
            root_header = lock_texture_object;
          } else if (ExactTitleTextureProducerSymbolAddress(symbol_view) ==
                     0x82278138u) {
            root_header = GuestObjectAddress(*record);
          }
	          RememberExactTitleTextureRefreshCandidateLocked(
	              *record, info, FableHookDisplayName(symbol_view),
	              ExactTitleTextureProducerSymbolAddress(symbol_view), args.lr,
	              root_header);
	          RegisterTextureIdentityForRecordLocked(*record);
	          CaptureExactGuiAtlasLockedUploadIfReadyLocked(*record, info, base);
	          record->serial = ++g_observer.serial;
	          FableDirectGuestResourceRegistry().last_resource = *record;
	          MarkResourceChangedLocked(*record);
          RefreshQueuedDrawTextureResourceSerialLocked(*record);
        }
      }
      if (record->locked_write_address != 0 &&
          record->locked_write_address != record->locked_address &&
          (RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
          IsImageResourceKind(GuestObjectKind(*record)) &&
          g_observer.logged_title_texture_upload_probes <
              kMaxTitleTextureUploadProbeLogs) {
        const GuestTextureRecord texture = GuestTextureRecordFor(*record);
        if (IsFableTitleTextureProbeAddress(GuestObjectAddress(*record)) ||
            IsFableTitleTextureProbeAddress(texture.base_address)) {
          REXLOG_INFO(
              "Refii renderer lock write pointer #{} hook={} guest=0x{:08X} "
              "inner=0x{:08X} write=0x{:08X} size={} base=0x{:08X}",
              g_observer.logged_title_texture_upload_probes + 1u,
              FableHookDisplayName(symbol_view), GuestObjectAddress(*record),
              record->locked_address, record->locked_write_address,
              record->locked_size, texture.base_address);
          ++g_observer.logged_title_texture_upload_probes;
        }
      }
    }
    return;
  }

  if (IsFableResourceUnlockHook(symbol_view)) {
    RendererResourceRecord* record = FindResourceRecordLocked(args.r3);
    bool resolved_from_locked_address = false;
    if (!record) {
      record = FindLockedResourceRecordLocked(args.r3);
      resolved_from_locked_address = record != nullptr;
    }
    if (record) {
      if (resolved_from_locked_address &&
          (RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
          IsImageResourceKind(GuestObjectKind(*record)) &&
          g_observer.logged_title_texture_upload_probes <
              kMaxTitleTextureUploadProbeLogs) {
        const GuestTextureRecord texture = GuestTextureRecordFor(*record);
        if (IsFableTitleTextureProbeAddress(GuestObjectAddress(*record)) ||
            IsFableTitleTextureProbeAddress(texture.base_address)) {
          REXLOG_INFO(
              "Refii renderer unlock matched locked texture #{} hook={} "
              "arg=0x{:08X} guest=0x{:08X} locked=0x{:08X} size={} "
              "base=0x{:08X}",
              g_observer.logged_title_texture_upload_probes + 1u,
              FableHookDisplayName(symbol_view), args.r3,
              GuestObjectAddress(*record), record->locked_address,
              record->locked_size, texture.base_address);
          ++g_observer.logged_title_texture_upload_probes;
        }
      }
      if (symbol_view == "rex_lhGfx_FlushBuffer" &&
          PublishExactGuiAtlasUnlockUploadLocked(
              symbol_view, args, *record, base, resolved_from_locked_address)) {
        return;
      }
      CaptureLockedBufferUpload(*record, base);
    }
    return;
  }

  const RendererResourceKind registered_shader_kind =
      FableRegisteredShaderRendererResourceKind(symbol_view);
  if (registered_shader_kind != RendererResourceKind::kUnknown) {
    RememberGuestMemoryBaseLocked(base);
    TrackShaderObjectLocked(registered_shader_kind, args.r3, args.r4, base);
    return;
  }

  if (!IsCreateHook(symbol_view)) {
    return;
  }

  const RendererResourceKind created_kind =
      FableCreatedRendererResourceKind(symbol_view);
  if (result == 0) {
    TraceResourceCreateFailed(symbol_view, created_kind);
    return;
  }

  uint32_t guest_address =
      CreatedResourceGuestAddress(symbol_view, args, result, base);
  if (guest_address == 0) {
    return;
  }
  RendererResourceKind final_kind = created_kind;
  if (symbol_view == "sub_82B8AB30" && base && args.r5 != 0) {
    const uint32_t surface_format = LoadGuestU32(base, args.r5);
    final_kind = surface_format == 54u
                     ? RendererResourceKind::kDepthSurface
                     : RendererResourceKind::kRenderTargetSurface;
  }
  const bool root_image_create = IsFableRootImageCreateHook(symbol_view);
  // D3DDevice_CreateSurface/CreateTexture are root constructors. Their
  // leading arguments are shape fields such as width/height, not guest object
  // parents, and later registers may contain caller/code-address residue.
  RendererResourceRecord record = CreateGuestResourceRecord(
      final_kind, guest_address, root_image_create ? 0u : args.r3);
  if (IsShaderResourceKind(GuestObjectKind(record)) && base && args.r3 != 0) {
    const bool populated_from_object = PopulateShaderRecordFromObject(record, base);
    if (!PopulateShaderRecordFromCreateArgument(record, base, args.r3) &&
        !populated_from_object) {
      ApplyGuestShaderRecord(record, GuestShaderRecord{});
    }
  }
  SnapshotGuestResource(record, base);
  if (IsSurfaceResourceKind(GuestObjectKind(record)) &&
      GuestObjectKind(record) != RendererResourceKind::kSurfaceView &&
      !HasPlausibleSurfaceHeader(record) &&
      !SnapshotNearbySurfaceHeader(record, base)) {
    return;
  }
  SanitizeImageResourceParent(record);
  if (GuestObjectKind(record) == RendererResourceKind::kVertexDeclaration &&
      GuestVertexDeclarationRecordFor(record).elements_hash == 0 && base &&
      args.r3 != 0) {
    PopulateVertexDeclarationRecordFromElements(record, base, args.r3);
  }
  if (GuestObjectKind(record) == RendererResourceKind::kVertexDeclaration &&
      GuestVertexDeclarationRecordFor(record).elements_hash == 0) {
    return;
  }

  {
    const RendererResourceKind record_kind = GuestObjectKind(record);
    const uint32_t object_address = GuestObjectAddress(record);
    auto existing_it = FableDirectGuestResourceRegistry().resources.find(object_address);
    const bool existing_shape_matches =
        IsSurfaceResourceKind(record_kind) &&
        existing_it != FableDirectGuestResourceRegistry().resources.end() &&
        IsSurfaceResourceKind(GuestObjectKind(existing_it->second)) &&
        SurfaceShapeMatches(record, existing_it->second);
    if (record_kind == RendererResourceKind::kSurfaceView &&
        existing_it != FableDirectGuestResourceRegistry().resources.end() &&
        (GuestObjectKind(existing_it->second) ==
             RendererResourceKind::kDepthSurface ||
         GuestObjectKind(existing_it->second) ==
             RendererResourceKind::kRenderTargetSurface)) {
      const GuestSurfaceRecord existing_surface =
          GuestSurfaceRecordFor(existing_it->second);
      const GuestSurfaceRecord view_surface = GuestSurfaceRecordFor(record);
      const bool existing_depth =
          FableSetSurfaceHeaderFormatIsDepth(existing_surface.d3d_format);
      const bool view_depth =
          FableSetSurfaceHeaderFormatIsDepth(view_surface.d3d_format);
      if (existing_depth != view_depth) {
        if (RendererResourceTracingEnabled() &&
            g_observer.logged_bound_surface_failures.insert(
                object_address)
                .second) {
          REXLOG_INFO(
              "Refii renderer ignored mismatched surface view alias "
              "guest=0x{:08X} view_parent=0x{:08X} view_fmt=0x{:08X} "
              "existing_kind={} existing_fmt=0x{:08X}",
              object_address, GuestObjectParentAddress(record),
              view_surface.d3d_format,
              ResourceKindName(GuestObjectKind(existing_it->second)),
              existing_surface.d3d_format);
        }
        return;
      }
      // Fable builds short-lived D3DSurface views over the same descriptors as
      // real render/depth targets. Keep the root surface authoritative for
      // Plume; the view can be released immediately by the guest while queued
      // draw direct still needs the target shape.
      RegisterSurfaceViewTextureLinkLocked(symbol_view, args, record);
      RememberRecentSurfaceShapeLocked(existing_it->second);
      FableDirectGuestResourceRegistry().last_resource = existing_it->second;
      return;
    }
    LogSuspiciousVertexDeclarationLocked(object_address, record, base);
    if (existing_shape_matches) {
      record.serial = existing_it->second.serial;
      const uint32_t existing_parent =
          GuestObjectParentAddress(existing_it->second);
      if (!root_image_create && existing_parent != 0 &&
          !IsLikelyFableCodeAddress(existing_parent)) {
        SetGuestResourceParent(record, existing_parent);
      }
    } else {
      record.serial = ++g_observer.serial;
    }
    if (root_image_create) {
      // Shape reuse can otherwise carry an older wrapper/code pointer into a
      // direct D3D root allocation. Keep root images parentless so surface-view
      // aliases resolve from the actual guest object rather than stale ABI
      // residue.
      SetGuestResourceParent(record, 0);
    }
    SanitizeImageResourceParent(record);
    const bool inserted_new = existing_it == FableDirectGuestResourceRegistry().resources.end();
    RendererResourceRecord& stored =
        StoreGuestResourceLocked(record, !existing_shape_matches);
    g_observer.deferred_surface_releases.erase(object_address);
    if (IsSurfaceResourceKind(GuestObjectKind(record))) {
      RememberRecentSurfaceShapeLocked(stored);
      RegisterSurfaceViewTextureLinkLocked(symbol_view, args, stored);
    } else {
      g_observer.recent_surface_shapes.erase(object_address);
    }
    if (existing_shape_matches || !inserted_new) {
      return;
    }
  }

  TraceResourceCreate(record);
}

RendererHookObserverSnapshot GetRendererHookObserverSnapshot() {
  static std::mutex snapshot_cache_mutex;
  static RendererHookObserverSnapshot cached_snapshot;
  const auto cached = [&]() {
    std::lock_guard<std::mutex> cache_lock(snapshot_cache_mutex);
    return cached_snapshot;
  };

  std::unique_lock<std::mutex> lock(g_observer.mutex, std::try_to_lock);
  if (!lock.owns_lock()) {
    return cached();
  }

  RendererHookObserverSnapshot snapshot;
  const FableGuestResourceCounts counts = CountResourcesLocked();
  FableDirectStateSnapshot direct_snapshot;
  if (!TryGetFableDirectStateSnapshot(direct_snapshot)) {
    return cached();
  }
  snapshot.live_resources = counts.live_resources;
  snapshot.live_textures = counts.live_textures;
  snapshot.live_surfaces = counts.live_surfaces;
  snapshot.live_vertex_buffers = counts.live_vertex_buffers;
  snapshot.live_index_buffers = counts.live_index_buffers;
  snapshot.live_shaders = counts.live_shaders;
  snapshot.resource_creates = FableDirectGuestResourceRegistry().creates;
  snapshot.resource_destroys = FableDirectGuestResourceRegistry().destroys;
  snapshot.resource_locks = FableDirectGuestResourceRegistry().locks;
  snapshot.resource_unlocks = FableDirectGuestResourceRegistry().unlocks;
  snapshot.draws = g_observer.draws;
  snapshot.indexed_draws = g_observer.indexed_draws;
  snapshot.scene_walker_calls = g_observer.scene_walker_calls;
  snapshot.scene_walker_flush_calls = g_observer.scene_walker_flush_calls;
  snapshot.scene_walker_nonempty_calls =
      g_observer.scene_walker_nonempty_calls;
  snapshot.scene_walker_object_slots_total =
      g_observer.scene_walker_object_slots_total;
  snapshot.scene_draw_filter_calls = g_observer.scene_draw_filter_calls;
  snapshot.scene_batch_traversal_calls =
      g_observer.scene_batch_traversal_calls;
  snapshot.scene_batch_bridge_calls = g_observer.scene_batch_bridge_calls;
  snapshot.scene_batch_bridge_rejected_calls =
      g_observer.scene_batch_bridge_rejected_calls;
  snapshot.scene_batch_bridge_queued_draws =
      g_observer.scene_batch_bridge_queued_draws;
  snapshot.scene_inline_vertex_draws = g_observer.scene_inline_vertex_draws;
  snapshot.last_scene_walker_list = g_observer.last_scene_walker_list;
  snapshot.last_scene_walker_begin = g_observer.last_scene_walker_begin;
  snapshot.last_scene_walker_end = g_observer.last_scene_walker_end;
  snapshot.last_scene_walker_count = g_observer.last_scene_walker_count;
  snapshot.last_scene_walker_first = g_observer.last_scene_walker_first;
  snapshot.last_scene_walker_result = g_observer.last_scene_walker_result;
  snapshot.last_scene_walker_lr = g_observer.last_scene_walker_lr;
  snapshot.pending_draw_events = direct_snapshot.draw_event_count;
  snapshot.dropped_draw_events = direct_snapshot.dropped_render_commands;
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
      CountNonZeroDwords(direct_snapshot.constants.vertex_float);
  snapshot.pixel_shader_constant_nonzero_dwords =
      CountNonZeroDwords(direct_snapshot.constants.pixel_float);
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
      CountNonZeroDwords(direct_snapshot.constants.fetch);
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
  std::vector<RendererSurfaceCopyAliasSnapshot> surface_copy_aliases;
  surface_copy_aliases.reserve(g_observer.surface_copy_aliases.size());
  for (const auto& [destination, alias] : g_observer.surface_copy_aliases) {
    (void)destination;
    surface_copy_aliases.push_back(alias);
  }
  std::sort(surface_copy_aliases.begin(), surface_copy_aliases.end(),
            [](const RendererSurfaceCopyAliasSnapshot& lhs,
               const RendererSurfaceCopyAliasSnapshot& rhs) {
              return lhs.serial > rhs.serial;
            });
  for (const RendererSurfaceCopyAliasSnapshot& alias : surface_copy_aliases) {
    if (snapshot.surface_copy_alias_count >=
        snapshot.surface_copy_aliases.size()) {
      break;
    }
    snapshot.surface_copy_aliases[snapshot.surface_copy_alias_count++] = alias;
  }
  snapshot.get_render_target_calls = g_observer.get_render_target_calls;
  snapshot.last_get_render_target_slot = g_observer.last_get_render_target_slot;
  snapshot.last_get_render_target_surface =
      g_observer.last_get_render_target_surface;
  snapshot.state_changes = g_observer.state_changes;
  std::copy(direct_snapshot.targets.color.begin(),
            direct_snapshot.targets.color.end(), snapshot.render_targets);
  snapshot.depth_stencil = direct_snapshot.targets.depth;
  snapshot.vertex_declaration = direct_snapshot.shaders.vertex_declaration;
  snapshot.vertex_shader = direct_snapshot.shaders.vertex_shader;
  snapshot.pixel_shader = direct_snapshot.shaders.pixel_shader;
  snapshot.index_buffer = direct_snapshot.input.index_buffer;
  std::copy(direct_snapshot.textures.textures.begin(),
            direct_snapshot.textures.textures.end(), snapshot.textures);
  snapshot.render_states = direct_snapshot.render_states;
  snapshot.sampler_states = direct_snapshot.textures.samplers;
  std::copy(direct_snapshot.input.streams.begin(),
            direct_snapshot.input.streams.end(), snapshot.streams);
  snapshot.last_draw = direct_snapshot.last_draw;
  snapshot.viewport_pointer = g_observer.viewport_pointer;
  snapshot.scissor_pointer = g_observer.scissor_pointer;
  snapshot.viewport = direct_snapshot.viewport;
  snapshot.scissor = direct_snapshot.scissor;
  snapshot.scaler = direct_snapshot.scaler;
  snapshot.clear_flags = direct_snapshot.clear_flags;
  snapshot.last_state_flush_lr = g_observer.last_state_flush_lr;
  snapshot.last_resource_address = GuestObjectAddress(FableDirectGuestResourceRegistry().last_resource);
  const GuestBufferRecord last_buffer =
      GuestBufferRecordFor(FableDirectGuestResourceRegistry().last_resource);
  const GuestTextureRecord last_texture =
      GuestTextureRecordFor(FableDirectGuestResourceRegistry().last_resource);
  const GuestShaderRecord last_shader =
      GuestShaderRecordFor(FableDirectGuestResourceRegistry().last_resource);
  const GuestVertexDeclarationRecord last_declaration =
      GuestVertexDeclarationRecordFor(FableDirectGuestResourceRegistry().last_resource);
  snapshot.last_resource_size = last_buffer.byte_size;
  if (snapshot.last_resource_size == 0) {
    snapshot.last_resource_size = last_texture.byte_size;
  }
  if (snapshot.last_resource_size == 0) {
    snapshot.last_resource_size = last_shader.bytecode_size;
  }
  if (snapshot.last_resource_size == 0) {
    snapshot.last_resource_size = last_declaration.elements_size;
  }
  snapshot.last_resource_stride_or_format = last_buffer.stride_or_index_format;
  if (snapshot.last_resource_stride_or_format == 0) {
    snapshot.last_resource_stride_or_format = last_shader.bytecode_offset;
  }
  if (snapshot.last_resource_stride_or_format == 0) {
    snapshot.last_resource_stride_or_format = last_declaration.element_count;
  }
  snapshot.last_shader_guest_hash =
      last_shader.bytecode_hash != 0 ? last_shader.bytecode_hash
                                     : last_declaration.elements_hash;
  snapshot.last_command_source = g_observer.last_command_source;
  snapshot.last_command_pointer = g_observer.last_command_pointer;
  snapshot.last_command_word_count = g_observer.last_command_word_count;
  snapshot.last_command_first_word = g_observer.last_command_first_word;
  snapshot.last_command_packet_type = g_observer.last_command_packet_type;
  snapshot.last_command_type3_opcode = g_observer.last_command_type3_opcode;
  std::memcpy(snapshot.last_command_preview, g_observer.last_command_preview,
              sizeof(snapshot.last_command_preview));
  {
    std::lock_guard<std::mutex> cache_lock(snapshot_cache_mutex);
    cached_snapshot = snapshot;
  }
  return snapshot;
}

void ExportTrackedBufferSnapshot(RendererTrackedResourceSnapshot& snapshot,
                                 const GuestBufferRecord& buffer) {
  snapshot.buffer = {
      .object_address = buffer.object_address,
      .gpu_address = buffer.gpu_address,
      .byte_size = buffer.byte_size,
      .stride_or_index_format = buffer.stride_or_index_format,
      .header_address = buffer.header_address,
      .upload_hash = buffer.upload_hash,
      .upload_generation = buffer.upload_generation,
      .scratch = buffer.scratch,
  };
}

void ExportTrackedTextureSnapshot(RendererTrackedResourceSnapshot& snapshot,
                                  const GuestTextureRecord& texture,
                                  uint64_t upload_hash) {
  snapshot.texture = {
      .object_address = texture.object_address,
      .parent_address = texture.parent_address,
      .base_address = texture.base_address,
      .byte_size = texture.byte_size,
      .raw_fetch_dwords = texture.raw_fetch_dwords,
      .normalized_fetch_dwords = texture.normalized_fetch_dwords,
      .upload_generation = texture.upload_generation,
      .upload_hash = upload_hash,
  };
}

void ExportTrackedSurfaceSnapshot(RendererTrackedResourceSnapshot& snapshot,
                                  const GuestSurfaceRecord& surface) {
  snapshot.surface = {
      .object_address = surface.object_address,
      .parent_address = surface.parent_address,
      .render_target_info = surface.render_target_info,
      .depth_stencil_info = surface.depth_stencil_info,
      .packed_extent = surface.packed_extent,
      .d3d_format = surface.d3d_format,
      .pitch_msaa = surface.pitch_msaa,
      .byte_size = surface.byte_size,
  };
}

void ExportTrackedShaderSnapshot(RendererTrackedResourceSnapshot& snapshot,
                                 const GuestShaderRecord& shader) {
  snapshot.shader = {
      .object_address = shader.object_address,
      .bytecode_address = shader.bytecode_address,
      .bytecode_size = shader.bytecode_size,
      .bytecode_offset = shader.bytecode_offset,
      .bytecode_hash = shader.bytecode_hash,
  };
}

void ExportTrackedVertexDeclarationSnapshot(
    RendererTrackedResourceSnapshot& snapshot,
    const GuestVertexDeclarationRecord& declaration) {
  snapshot.vertex_declaration = {
      .object_address = declaration.object_address,
      .elements_address = declaration.elements_address,
      .elements_size = declaration.elements_size,
      .element_count = declaration.element_count,
      .max_stream = declaration.max_stream,
      .stream_mask = declaration.stream_mask,
      .declaration_flags = declaration.declaration_flags,
      .elements_hash = declaration.elements_hash,
  };
}

RendererTrackedResourceUploadCacheEntry UploadCacheEntryForSnapshot(
    const RendererTrackedResourceSnapshot& snapshot) {
  RendererTrackedResourceUploadCacheEntry entry;
  entry.kind = snapshot.kind;
  entry.upload_generation = snapshot.upload_generation;
  switch (snapshot.kind) {
    case RendererTrackedResourceKind::kVertexBuffer:
    case RendererTrackedResourceKind::kIndexBuffer:
      entry.data_address = snapshot.buffer.gpu_address;
      entry.byte_size = snapshot.buffer.byte_size;
      entry.meta = snapshot.buffer.stride_or_index_format;
      entry.hash = snapshot.buffer.upload_hash;
      break;
    case RendererTrackedResourceKind::kTexture:
      entry.data_address = snapshot.texture.base_address;
      entry.byte_size = snapshot.texture.byte_size;
      entry.meta = snapshot.texture.raw_fetch_dwords[0];
      entry.hash = snapshot.texture.upload_hash;
      break;
    case RendererTrackedResourceKind::kSurface:
    case RendererTrackedResourceKind::kDepthSurface:
      entry.data_address = snapshot.surface.parent_address;
      entry.byte_size = snapshot.surface.byte_size;
      entry.meta = snapshot.surface.d3d_format;
      entry.hash = snapshot.serial;
      break;
    case RendererTrackedResourceKind::kVertexShader:
    case RendererTrackedResourceKind::kPixelShader:
      entry.data_address = snapshot.shader.bytecode_address;
      entry.byte_size = snapshot.shader.bytecode_size;
      entry.meta = snapshot.shader.bytecode_offset;
      entry.hash = snapshot.shader.bytecode_hash;
      break;
    case RendererTrackedResourceKind::kVertexDeclaration:
      entry.data_address = snapshot.vertex_declaration.elements_address;
      entry.byte_size = snapshot.vertex_declaration.elements_size;
      entry.meta = snapshot.vertex_declaration.element_count;
      entry.hash = snapshot.vertex_declaration.elements_hash;
      break;
    case RendererTrackedResourceKind::kUnknown:
      break;
  }
  return entry;
}

bool SameUploadCacheEntry(const RendererTrackedResourceUploadCacheEntry& lhs,
                          const RendererTrackedResourceUploadCacheEntry& rhs) {
  const bool same_upload_generation =
      lhs.upload_generation == rhs.upload_generation ||
      (lhs.hash != 0 && lhs.hash == rhs.hash);
  return lhs.kind == rhs.kind && lhs.data_address == rhs.data_address &&
         lhs.byte_size == rhs.byte_size && lhs.meta == rhs.meta &&
         lhs.hash == rhs.hash && same_upload_generation;
}

bool SnapshotMatchesResourceFilter(
    const RendererTrackedResourceSnapshot& snapshot,
    const std::unordered_set<uint32_t>& resource_filter) {
  const auto contains = [&resource_filter](uint32_t address) {
    return address != 0 && resource_filter.find(address) != resource_filter.end();
  };

  if (contains(snapshot.guest_address) || contains(snapshot.parent_address)) {
    return true;
  }

  switch (snapshot.kind) {
    case RendererTrackedResourceKind::kVertexBuffer:
    case RendererTrackedResourceKind::kIndexBuffer:
      return contains(snapshot.buffer.object_address) ||
             contains(snapshot.buffer.gpu_address) ||
             contains(snapshot.buffer.header_address);
    case RendererTrackedResourceKind::kTexture:
      if (contains(snapshot.texture.object_address) ||
          contains(snapshot.texture.parent_address) ||
          contains(snapshot.texture.base_address)) {
        return true;
      }
      if (IsFableTitleBillboardTileUploadBaseAddress(
              NormalizeFableGpuAddress(snapshot.texture.base_address))) {
        const uint32_t guest_header =
            FableTitleBillboardTileHeaderAddressForObject(snapshot.guest_address);
        const uint32_t object_header =
            FableTitleBillboardTileHeaderAddressForObject(
                snapshot.texture.object_address);
        const uint32_t guest_object =
            FableTitleBillboardTileObjectAddressForHeader(snapshot.guest_address);
        const uint32_t object_object =
            FableTitleBillboardTileObjectAddressForHeader(
                snapshot.texture.object_address);
        return contains(guest_header) || contains(object_header) ||
               contains(guest_object) || contains(object_object);
      }
      return false;
    case RendererTrackedResourceKind::kSurface:
    case RendererTrackedResourceKind::kDepthSurface:
      return contains(snapshot.surface.object_address) ||
             contains(snapshot.surface.parent_address);
    case RendererTrackedResourceKind::kVertexShader:
    case RendererTrackedResourceKind::kPixelShader:
      return contains(snapshot.shader.object_address) ||
             contains(snapshot.shader.bytecode_address);
    case RendererTrackedResourceKind::kVertexDeclaration:
      return contains(snapshot.vertex_declaration.object_address) ||
             contains(snapshot.vertex_declaration.elements_address);
    case RendererTrackedResourceKind::kUnknown:
      break;
  }
  return false;
}

RendererTrackedResourceSnapshot ExportTrackedResourceSnapshotLocked(
    const RendererResourceRecord& record) {
  RendererTrackedResourceSnapshot snapshot;
  const RendererResourceKind kind = GuestObjectKind(record);
  snapshot.kind = PublicResourceKind(kind);
  snapshot.guest_address = GuestObjectAddress(record);
  snapshot.parent_address = GuestObjectParentAddress(record);
  if (IsBufferResourceKind(kind)) {
    ExportTrackedBufferSnapshot(snapshot, GuestBufferRecordFor(record));
  } else if (IsShaderResourceKind(kind)) {
    ExportTrackedShaderSnapshot(snapshot, GuestShaderRecordFor(record));
  } else if (kind == RendererResourceKind::kVertexDeclaration) {
    ExportTrackedVertexDeclarationSnapshot(
        snapshot, GuestVertexDeclarationRecordFor(record));
  } else if (IsSurfaceResourceKind(kind)) {
    ExportTrackedSurfaceSnapshot(snapshot, GuestSurfaceRecordFor(record));
  } else if (IsImageResourceKind(kind)) {
    ExportTrackedTextureSnapshot(
        snapshot, TextureRecordWithFetchSnapshotIdentity(record),
        record.raw.hash);
  }
  snapshot.upload_generation = record.upload_generation;
  snapshot.serial = record.serial;
  snapshot.scratch = record.scratch;
  return snapshot;
}

bool IsFableTitleGuiAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot);
bool IsFableExactTinyTitleUploadSnapshot(
    const RendererTrackedResourceSnapshot& snapshot);
bool IsFableTitleBillboardAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot);

bool ShouldCopyTrackedResourceUploadData(
    const RendererTrackedResourceSnapshot& snapshot,
    const RendererResourceRecord& record,
    const std::unordered_map<uint32_t,
                             RendererTrackedResourceUploadCacheEntry>*
        known_resources,
    const std::unordered_set<uint32_t>* upload_data_filter) {
  if (upload_data_filter && IsImageResourceKind(GuestObjectKind(record)) &&
      !SnapshotMatchesResourceFilter(snapshot, *upload_data_filter)) {
    return false;
  }

  const bool explicit_title_upload_request =
      upload_data_filter &&
      (IsFableTitleGuiAtlasSnapshot(snapshot) ||
       IsFableExactTinyTitleUploadSnapshot(snapshot) ||
       IsFableTitleBillboardAtlasSnapshot(snapshot)) &&
      SnapshotMatchesResourceFilter(snapshot, *upload_data_filter);
  if (explicit_title_upload_request) {
    return true;
  }
  if (snapshot.kind == RendererTrackedResourceKind::kVertexDeclaration) {
    return true;
  }

  if (known_resources) {
    const uint32_t object_address = GuestObjectAddress(record);
    const auto known_it = known_resources->find(object_address);
    if (known_it != known_resources->end() &&
        SameUploadCacheEntry(known_it->second,
                             UploadCacheEntryForSnapshot(snapshot))) {
      return false;
    }
  }

  return true;
}

bool IsFableTitleGuiAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot) {
  return snapshot.kind == RendererTrackedResourceKind::kTexture &&
         (IsFableTitleGuiTextAtlasBaseAddress(snapshot.texture.base_address) ||
          IsFableTitleTextureProbeAddress(snapshot.guest_address) ||
          IsFableTitleTextureProbeAddress(snapshot.texture.object_address));
}

bool IsFableExactTinyTitleUploadSnapshot(
    const RendererTrackedResourceSnapshot& snapshot) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture) {
    return false;
  }

  const auto is_known_object = [](uint32_t address) {
    return address == 0x401317D0u || address == 0x40131810u ||
           address == 0x404DE490u || address == 0x404DE4D0u ||
           address == 0x404DE550u;
  };
  const auto is_exact_tiny_title_shape =
      [](uint32_t base, uint32_t width, uint32_t height, uint32_t format,
         uint32_t byte_size) {
        if (base == 0x1A0D7000u && width == 16u && height == 16u &&
            format == 6u) {
          return byte_size == 0u || byte_size == 4096u;
        }
        if (base == 0x1FAB5000u && width == 64u && height == 1u &&
            format == 29u) {
          return byte_size == 0u || byte_size == 512u;
        }
        if (base == 0x1FAB4000u && width == 256u && height == 1u &&
            format == 6u) {
          return byte_size == 0u || byte_size == 1024u;
        }
        return false;
      };
  const auto is_exact_tiny_title_storage =
      [](uint32_t base, uint32_t byte_size) {
        return (base == 0x1A0D7000u && byte_size == 4096u) ||
               (base == 0x1FAB5000u && byte_size == 512u) ||
               (base == 0x1FAB4000u && byte_size == 1024u);
      };
  const bool known_object =
      is_known_object(snapshot.guest_address) ||
      is_known_object(snapshot.parent_address) ||
      is_known_object(snapshot.texture.object_address) ||
      is_known_object(snapshot.texture.parent_address);

  const std::array<uint32_t, 6>& fetch =
      HasTextureFetchDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(fetch);
  const uint32_t fetch_base = NormalizeFableGpuAddress(info.base_address);
  const uint32_t snapshot_base =
      NormalizeFableGpuAddress(snapshot.texture.base_address);
  const bool exact_fetch_shape = is_exact_tiny_title_shape(
      fetch_base, info.width, info.height, info.format, snapshot.texture.byte_size);
  const bool exact_snapshot_shape =
      is_exact_tiny_title_storage(snapshot_base, snapshot.texture.byte_size);
  return exact_fetch_shape || (known_object && exact_snapshot_shape);
}

bool IsFableTitleBillboardAtlasSnapshot(
    const RendererTrackedResourceSnapshot& snapshot) {
  if (snapshot.kind != RendererTrackedResourceKind::kTexture) {
    return false;
  }

  constexpr uint32_t kTitleBillboardAtlasObject = 0x4C132C70u;
  constexpr uint32_t kTitleBillboardAtlasAlias = 0x4C132CA4u;
  constexpr uint32_t kTitleBillboardAtlasBase = 0x16C40000u;
  const auto is_known_object = [](uint32_t address) {
    return address == kTitleBillboardAtlasObject ||
           address == kTitleBillboardAtlasAlias ||
           address == 0x4C132CD8u ||
           address == 0x4C133150u ||
           address == 0x4C133184u ||
           address == kTitleBillboardAtlasBase ||
           IsFableTitleBillboardTileUploadObjectAddress(address) ||
           IsFableTitleBillboardTileUploadHeaderAddress(address) ||
           IsFableTitleBillboardTileUploadBaseAddress(address);
  };

  const bool known_carrier =
      is_known_object(snapshot.guest_address) ||
      is_known_object(snapshot.parent_address) ||
      is_known_object(snapshot.texture.object_address) ||
      is_known_object(snapshot.texture.parent_address);
  const uint32_t snapshot_base =
      NormalizeFableGpuAddress(snapshot.texture.base_address);
  const bool known_base = snapshot_base == kTitleBillboardAtlasBase ||
                          IsFableTitleBillboardTileUploadBaseAddress(
                              snapshot_base);
  if (!known_carrier || !known_base) {
    return false;
  }

  const std::array<uint32_t, 6>& fetch =
      HasTextureFetchDwords(snapshot.texture.normalized_fetch_dwords)
          ? snapshot.texture.normalized_fetch_dwords
          : snapshot.texture.raw_fetch_dwords;
  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(fetch);
  const bool known_extent =
      info.base_address == kTitleBillboardAtlasBase &&
      info.width == 1024u && info.height == 512u && info.format == 20u;
  const bool known_tile_extent = IsFableTitleBillboardTileUploadShape(info);
  return known_extent || known_tile_extent ||
         snapshot.texture.byte_size == 524288u;
}

void LogFableTitleGuiAtlasSnapshotLocked(
    std::string_view source, const RendererTrackedResourceSnapshot& snapshot,
    const RendererResourceRecord* record,
    const std::unordered_set<uint32_t>* upload_data_filter,
    bool copied_upload) {
  if (!IsFableTitleGuiAtlasSnapshot(snapshot) ||
      !RendererResourceTracingEnabled()) {
    return;
  }

  static uint32_t logged_title_gui_atlas_snapshots = 0;
  if (logged_title_gui_atlas_snapshots >= 64u) {
    return;
  }

  const bool upload_requested =
      upload_data_filter &&
      SnapshotMatchesResourceFilter(snapshot, *upload_data_filter);
  const size_t record_upload_size = record ? record->upload_data.size() : 0u;
  const bool record_nonzero = record && HasNonZeroUploadData(*record);
  REXLOG_INFO(
      "Refii renderer title GUI atlas snapshot #{} source={} "
      "guest=0x{:08X} object=0x{:08X} parent=0x{:08X} base=0x{:08X} "
      "size={} gen={} serial={} hash=0x{:016X} upload_requested={} "
      "copied_upload={} snapshot_upload={} record_upload={} record_nonzero={}",
      ++logged_title_gui_atlas_snapshots, source, snapshot.guest_address,
      snapshot.texture.object_address, snapshot.parent_address,
      snapshot.texture.base_address, snapshot.texture.byte_size,
      static_cast<unsigned long long>(snapshot.upload_generation),
      static_cast<unsigned long long>(snapshot.serial),
      static_cast<unsigned long long>(snapshot.texture.upload_hash),
      upload_requested ? 1 : 0, copied_upload ? 1 : 0,
      snapshot.upload_data.size(), record_upload_size, record_nonzero ? 1 : 0);
}

void LogFableExactTinyTitleUploadSnapshotLocked(
    std::string_view source, const RendererTrackedResourceSnapshot& snapshot,
    const RendererResourceRecord* record,
    const std::unordered_set<uint32_t>* upload_data_filter,
    bool copied_upload) {
  if (!IsFableExactTinyTitleUploadSnapshot(snapshot) ||
      !RendererResourceTracingEnabled()) {
    return;
  }

  static uint32_t logged_exact_tiny_title_upload_snapshots = 0;
  if (logged_exact_tiny_title_upload_snapshots >= 64u) {
    return;
  }

  const bool upload_requested =
      upload_data_filter &&
      SnapshotMatchesResourceFilter(snapshot, *upload_data_filter);
  const size_t record_upload_size = record ? record->upload_data.size() : 0u;
  const bool record_nonzero = record && HasNonZeroUploadData(*record);
  REXLOG_INFO(
      "Refii renderer exact tiny title upload snapshot #{} source={} "
      "guest=0x{:08X} object=0x{:08X} parent=0x{:08X} base=0x{:08X} "
      "size={} gen={} serial={} hash=0x{:016X} upload_requested={} "
      "copied_upload={} snapshot_upload={} record_upload={} record_nonzero={}",
      ++logged_exact_tiny_title_upload_snapshots, source,
      snapshot.guest_address, snapshot.texture.object_address,
      snapshot.parent_address, snapshot.texture.base_address,
      snapshot.texture.byte_size,
      static_cast<unsigned long long>(snapshot.upload_generation),
      static_cast<unsigned long long>(snapshot.serial),
      static_cast<unsigned long long>(snapshot.texture.upload_hash),
      upload_requested ? 1 : 0, copied_upload ? 1 : 0,
      snapshot.upload_data.size(), record_upload_size,
      record_nonzero ? 1 : 0);
}

void LogFableTitleBillboardAtlasSnapshotLocked(
    std::string_view source, const RendererTrackedResourceSnapshot& snapshot,
    const RendererResourceRecord* record,
    const std::unordered_set<uint32_t>* upload_data_filter,
    bool copied_upload) {
  if (!IsFableTitleBillboardAtlasSnapshot(snapshot) ||
      !RendererResourceTracingEnabled()) {
    return;
  }

  static uint32_t logged_title_billboard_atlas_snapshots = 0;
  if (logged_title_billboard_atlas_snapshots >= 64u) {
    return;
  }

  const bool upload_requested =
      upload_data_filter &&
      SnapshotMatchesResourceFilter(snapshot, *upload_data_filter);
  const size_t record_upload_size = record ? record->upload_data.size() : 0u;
  const bool record_nonzero = record && HasNonZeroUploadData(*record);
  REXLOG_INFO(
      "Refii renderer title billboard atlas snapshot #{} source={} "
      "guest=0x{:08X} object=0x{:08X} parent=0x{:08X} base=0x{:08X} "
      "size={} gen={} serial={} hash=0x{:016X} upload_requested={} "
      "copied_upload={} snapshot_upload={} record_upload={} record_nonzero={}",
      ++logged_title_billboard_atlas_snapshots, source, snapshot.guest_address,
      snapshot.texture.object_address, snapshot.parent_address,
      snapshot.texture.base_address, snapshot.texture.byte_size,
      static_cast<unsigned long long>(snapshot.upload_generation),
      static_cast<unsigned long long>(snapshot.serial),
      static_cast<unsigned long long>(snapshot.texture.upload_hash),
      upload_requested ? 1 : 0, copied_upload ? 1 : 0,
      snapshot.upload_data.size(), record_upload_size, record_nonzero ? 1 : 0);
}

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshots(
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources,
    const std::unordered_set<uint32_t>* upload_data_filter,
    const std::unordered_set<uint32_t>* resource_filter) {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  std::vector<FableGuestResourceSnapshot> snapshots;
  snapshots.reserve(resource_filter ? resource_filter->size()
                                    : FableDirectGuestResourceRegistry().resources.size());
  for (auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    EnsureVertexDeclarationSnapshotPayloadLocked(record);
    RendererTrackedResourceSnapshot snapshot =
        ExportTrackedResourceSnapshotLocked(record);
    if (resource_filter &&
        !SnapshotMatchesResourceFilter(snapshot, *resource_filter)) {
      continue;
    }
    if (ShouldCopyTrackedResourceUploadData(snapshot, record, known_resources,
                                            upload_data_filter)) {
      snapshot.upload_data = record.upload_data;
    }
    LogFableTitleGuiAtlasSnapshotLocked(
        "full", snapshot, &record, upload_data_filter,
        !snapshot.upload_data.empty());
    LogFableExactTinyTitleUploadSnapshotLocked(
        "full", snapshot, &record, upload_data_filter,
        !snapshot.upload_data.empty());
    LogFableTitleBillboardAtlasSnapshotLocked(
        "full", snapshot, &record, upload_data_filter,
        !snapshot.upload_data.empty());
    snapshots.push_back(snapshot);
  }
  return snapshots;
}

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddressesLocked(
    const std::unordered_set<uint32_t>& addresses,
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources,
    const std::unordered_set<uint32_t>* upload_data_filter) {
  std::vector<FableGuestResourceSnapshot> snapshots;
  snapshots.reserve(addresses.size());
  std::unordered_set<uint32_t> emitted_objects;
  for (uint32_t address : addresses) {
    auto it = FindResourceIteratorLocked(address);
    if (upload_data_filter &&
        upload_data_filter->find(address) != upload_data_filter->end()) {
      auto title_texture_it = FindExactTitleTextureResourceIteratorLocked(address);
      if (title_texture_it != FableDirectGuestResourceRegistry().resources.end()) {
        it = title_texture_it;
      }
    }
    if (it == FableDirectGuestResourceRegistry().resources.end() &&
        g_observer.memory_base &&
        RestoreBinkVideoPlaneResourceLocked(address, g_observer.memory_base,
                                           "on-demand-resource-sync")) {
      it = FableDirectGuestResourceRegistry().resources.find(address);
    }
    if (it == FableDirectGuestResourceRegistry().resources.end() &&
        g_observer.memory_base && IsLikelyFableObjectAddress(address)) {
      const std::array<uint32_t, 6> dwords =
          LoadTextureFetchDwords(g_observer.memory_base, address + 28u);
      if (HasTextureFetchDwords(dwords)) {
        RendererResourceRecord created =
            CreateGuestTextureResource(address, address);
        SnapshotGuestResource(created, g_observer.memory_base);
        GuestTextureRecord texture = GuestTextureRecordFor(created);
        texture.raw_fetch_dwords = dwords;
        texture.normalized_fetch_dwords = dwords;
        const TextureFetchSnapshotInfo info =
            GetTextureFetchSnapshotInfo(dwords);
        texture.base_address = info.base_address;
        texture.byte_size = std::max<uint32_t>(texture.byte_size, info.size);
        ApplyGuestTextureRecord(created, texture);
        if (ShouldCaptureTextureUploadFromFetchDwords(
                created, dwords, g_observer.memory_base)) {
          CaptureTextureUploadFromFetchDwords(created, dwords,
                                              g_observer.memory_base);
        }
        RefreshTextureUploadFromPendingPixDecodedFrameLocked(
            created, dwords, g_observer.memory_base);
        created.serial = ++g_observer.serial;
        RendererResourceRecord& stored =
            StoreGuestResourceLocked(std::move(created), true, true);
        RefreshQueuedDrawTextureResourceSerialLocked(stored);
        PropagateTextureAliasUploadLocked(stored, false);
        it = FableDirectGuestResourceRegistry().resources.find(
            GuestObjectAddress(stored));

        static uint32_t logged_on_demand_textures = 0;
        if ((RendererResourceTracingEnabled() || RendererDrawTracingEnabled()) &&
            logged_on_demand_textures < 32u) {
          REXLOG_INFO(
              "Refii renderer on-demand texture snapshot #{} "
              "object=0x{:08X} base=0x{:08X} size={} shape={}x{} fmt={} "
              "upload={} serial={}",
              logged_on_demand_textures + 1u, address, info.base_address,
              info.size, info.width, info.height, info.format,
              stored.upload_data.size(),
              static_cast<unsigned long long>(stored.serial));
          ++logged_on_demand_textures;
        }
      }
    }
    if (it == FableDirectGuestResourceRegistry().resources.end()) {
      continue;
    }
    const uint32_t object_address = GuestObjectAddress(it->second);
    const uint32_t dedupe_address = object_address != 0 ? object_address
                                                        : it->first;
    if (!emitted_objects.insert(dedupe_address).second) {
      continue;
    }
    RefreshLockedTitleTextureUploadForSnapshotLocked(it->second);
    EnsureVertexDeclarationSnapshotPayloadLocked(it->second);
    RendererTrackedResourceSnapshot snapshot =
        ExportTrackedResourceSnapshotLocked(it->second);
    if (ShouldCopyTrackedResourceUploadData(snapshot, it->second,
                                            known_resources,
                                            upload_data_filter)) {
      snapshot.upload_data = it->second.upload_data;
    }
    LogFableTitleGuiAtlasSnapshotLocked(
        "live", snapshot, &it->second, upload_data_filter,
        !snapshot.upload_data.empty());
    LogFableExactTinyTitleUploadSnapshotLocked(
        "live", snapshot, &it->second, upload_data_filter,
        !snapshot.upload_data.empty());
    LogFableTitleBillboardAtlasSnapshotLocked(
        "live", snapshot, &it->second, upload_data_filter,
        !snapshot.upload_data.empty());
    snapshots.push_back(std::move(snapshot));
  }
  return snapshots;
}

std::vector<FableGuestResourceSnapshot>
GetFableDirectCachedResourceSnapshotsForAddresses(
    const std::unordered_set<uint32_t>& addresses,
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources,
    const std::unordered_set<uint32_t>* upload_data_filter) {
  std::unique_lock<std::mutex> cache_lock(g_direct_snapshot_cache_mutex,
                                          std::try_to_lock);
  if (!cache_lock.owns_lock()) {
    return {};
  }

  std::vector<FableGuestResourceSnapshot> snapshots;
  snapshots.reserve(addresses.size());
  std::unordered_set<uint32_t> emitted_objects;
  const auto is_gui_title_atlas_snapshot =
      [](const FableGuestResourceSnapshot& snapshot) {
        return snapshot.kind == RendererTrackedResourceKind::kTexture &&
               IsFableTitleGuiTextAtlasBaseAddress(
                   snapshot.texture.base_address);
      };
  const auto is_explicit_title_upload_snapshot =
      [&](const FableGuestResourceSnapshot& snapshot) {
        return IsFableTitleGuiAtlasSnapshot(snapshot) ||
               IsFableExactTinyTitleUploadSnapshot(snapshot) ||
               is_gui_title_atlas_snapshot(snapshot) ||
               IsFableTitleBillboardAtlasSnapshot(snapshot);
      };
  const auto explicitly_requested = [&](uint32_t address) {
    if (address == 0) {
      return false;
    }
    if (addresses.find(address) != addresses.end()) {
      return true;
    }
    return upload_data_filter &&
           upload_data_filter->find(address) != upload_data_filter->end();
  };
  const auto gui_title_atlas_snapshot_allowed =
      [&](const FableGuestResourceSnapshot& snapshot,
          const std::unordered_set<uint32_t>& requested_addresses) {
        if (!is_gui_title_atlas_snapshot(snapshot)) {
          return true;
        }
        if (SnapshotMatchesResourceFilter(snapshot, requested_addresses)) {
          for (uint32_t requested_address : requested_addresses) {
            if (explicitly_requested(requested_address)) {
              return true;
            }
          }
        }
        return upload_data_filter &&
               SnapshotMatchesResourceFilter(snapshot, *upload_data_filter);
      };
  const auto emit_snapshot = [&](const FableGuestResourceSnapshot& cached) {
    const uint32_t object_address = cached.guest_address;
    const uint32_t dedupe_address =
        object_address != 0 ? object_address : cached.parent_address;
    if (dedupe_address == 0 ||
        !emitted_objects.insert(dedupe_address).second) {
      return;
    }

    FableGuestResourceSnapshot snapshot = cached;
    if (snapshot.kind == RendererTrackedResourceKind::kVertexDeclaration &&
        snapshot.upload_data.empty()) {
      return;
    }
	      const bool vertex_declaration_payload =
	          snapshot.kind == RendererTrackedResourceKind::kVertexDeclaration;
    if (upload_data_filter && !vertex_declaration_payload &&
        !SnapshotMatchesResourceFilter(snapshot, *upload_data_filter)) {
      snapshot.upload_data.clear();
    }
	      if (known_resources) {
	        const auto known_it = known_resources->find(snapshot.guest_address);
	        const bool explicit_title_upload_request =
	          upload_data_filter && is_explicit_title_upload_snapshot(snapshot) &&
	          SnapshotMatchesResourceFilter(snapshot, *upload_data_filter);
      if (!explicit_title_upload_request &&
          !vertex_declaration_payload &&
          known_it != known_resources->end() &&
          SameUploadCacheEntry(known_it->second,
                               UploadCacheEntryForSnapshot(snapshot))) {
        snapshot.upload_data.clear();
      }
    }
    LogFableTitleGuiAtlasSnapshotLocked(
        "cache", snapshot, nullptr, upload_data_filter,
        !snapshot.upload_data.empty());
    LogFableExactTinyTitleUploadSnapshotLocked(
        "cache", snapshot, nullptr, upload_data_filter,
        !snapshot.upload_data.empty());
    LogFableTitleBillboardAtlasSnapshotLocked(
        "cache", snapshot, nullptr, upload_data_filter,
        !snapshot.upload_data.empty());
    snapshots.push_back(std::move(snapshot));
  };

  for (uint32_t address : addresses) {
    if (address == 0) {
      continue;
    }
    const std::unordered_set<uint32_t> single_address = {address};
    auto direct_it = g_direct_snapshot_cache.find(address);
    if (direct_it != g_direct_snapshot_cache.end()) {
      if (!gui_title_atlas_snapshot_allowed(direct_it->second,
                                            single_address)) {
        continue;
      }
      emit_snapshot(direct_it->second);
      continue;
    }

    for (const auto& [cached_address, cached] : g_direct_snapshot_cache) {
      (void)cached_address;
      if (SnapshotMatchesResourceFilter(cached, single_address) &&
          gui_title_atlas_snapshot_allowed(cached, single_address)) {
        emit_snapshot(cached);
        break;
      }
    }
  }
  return snapshots;
}

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddresses(
    const std::unordered_set<uint32_t>& addresses,
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources,
    const std::unordered_set<uint32_t>* upload_data_filter,
    bool* observer_busy) {
  if (observer_busy) {
    *observer_busy = false;
  }
  std::unique_lock<std::mutex> lock(g_observer.mutex, std::try_to_lock);
  if (!lock.owns_lock()) {
    if (observer_busy) {
      *observer_busy = true;
    }
    return GetFableDirectCachedResourceSnapshotsForAddresses(
        addresses, known_resources, upload_data_filter);
  }
  return GetFableDirectResourceSnapshotsForAddressesLocked(
      addresses, known_resources, upload_data_filter);
}

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddressesBlocking(
    const std::unordered_set<uint32_t>& addresses,
    const std::unordered_map<uint32_t,
                             FableGuestResourceUploadCacheEntry>*
        known_resources,
    const std::unordered_set<uint32_t>* upload_data_filter) {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  return GetFableDirectResourceSnapshotsForAddressesLocked(
      addresses, known_resources, upload_data_filter);
}

FableGuestResourceVersion GetFableDirectResourceVersion() {
  std::lock_guard<std::mutex> lock(g_observer.mutex);
  FableGuestResourceVersion version;
  version.serial = g_observer.serial;
  version.resource_creates = FableDirectGuestResourceRegistry().creates;
  version.resource_destroys = FableDirectGuestResourceRegistry().destroys;
  version.resource_unlocks = FableDirectGuestResourceRegistry().unlocks;
  return version;
}

void DrainPendingFableDirectTextureIdentityOperations() {
  DrainPendingTextureIdentityOperations();
  DrainPendingShaderDeclarationIdentityOperations();
  DrainPendingPrivateTextureCopyActions();
  DrainPendingExactTextureActions();
}

void ApplyFableDirectResourceAckLocked(uint64_t serial) {
  auto acknowledge_serials = [serial](auto& serials) {
    if (serials.empty()) {
      return;
    }

    bool all_acknowledged = true;
    for (const auto& [address, entry_serial] : serials) {
      (void)address;
      if (entry_serial > serial) {
        all_acknowledged = false;
        break;
      }
    }
    if (all_acknowledged) {
      serials.clear();
      return;
    }

    for (auto it = serials.begin(); it != serials.end();) {
      if (it->second <= serial) {
        it = serials.erase(it);
      } else {
        ++it;
      }
    }
  };

  acknowledge_serials(FableDirectGuestResourceRegistry().changed_serials);
  acknowledge_serials(FableDirectGuestResourceRegistry().destroyed_serials);
}

void DrainPendingFableDirectDestroyedResourceAcksLocked() {
  std::vector<std::pair<uint32_t, uint64_t>> pending;
  {
    std::lock_guard<std::mutex> pending_lock(
        g_pending_fable_direct_destroy_ack_mutex);
    if (g_pending_fable_direct_destroy_acks.empty()) {
      return;
    }
    pending.swap(g_pending_fable_direct_destroy_acks);
  }

  std::unordered_map<uint32_t, uint64_t> highest_serial_by_address;
  highest_serial_by_address.reserve(pending.size());
  for (const auto& [address, serial] : pending) {
    if (address == 0 || serial == 0) {
      continue;
    }
    uint64_t& highest_serial = highest_serial_by_address[address];
    highest_serial = std::max(highest_serial, serial);
  }

  for (const auto& [address, serial] : highest_serial_by_address) {
    auto it = FableDirectGuestResourceRegistry().destroyed_serials.find(address);
    if (it != FableDirectGuestResourceRegistry().destroyed_serials.end() &&
        it->second <= serial) {
      FableDirectGuestResourceRegistry().destroyed_serials.erase(it);
    }
  }
}

void DrainPendingFableDirectResourceAckLocked() {
  const uint64_t pending_ack =
      g_pending_fable_direct_resource_ack.exchange(0, std::memory_order_acq_rel);
  if (pending_ack != 0) {
    ApplyFableDirectResourceAckLocked(pending_ack);
  }
  DrainPendingFableDirectDestroyedResourceAcksLocked();
}

void QueueFableDirectResourceAck(uint64_t serial) {
  uint64_t current =
      g_pending_fable_direct_resource_ack.load(std::memory_order_relaxed);
  while (current < serial &&
         !g_pending_fable_direct_resource_ack.compare_exchange_weak(
             current, serial, std::memory_order_release,
             std::memory_order_relaxed)) {
  }
}

FableGuestResourceChangeSet GetFableDirectResourceChanges() {
  std::unique_lock<std::mutex> lock(g_observer.mutex, std::try_to_lock);
  if (!lock.owns_lock()) {
    FableGuestResourceChangeSet changes;
    changes.observer_busy = true;
    return changes;
  }

  DrainPendingFableDirectResourceAckLocked();
  ++g_observer.zero_texture_refresh_harvest_counter;
  const bool has_pending_resource_changes =
      !FableDirectGuestResourceRegistry().changed_serials.empty() ||
      !FableDirectGuestResourceRegistry().destroyed_serials.empty();
  const bool periodic_zero_texture_refresh =
      (g_observer.zero_texture_refresh_harvest_counter %
       kZeroTextureRefreshHarvestPeriod) == 0;
  if (!g_observer.exact_title_texture_refresh_candidates.empty() &&
      (has_pending_resource_changes || periodic_zero_texture_refresh)) {
    RefreshZeroTextureUploadsFromGuestMemoryLocked("resource change harvest");
  }
  FableGuestResourceChangeSet changes;
  changes.serial = g_observer.serial;
  changes.changed_addresses.reserve(FableDirectGuestResourceRegistry().changed_serials.size());
  changes.destroyed_addresses.reserve(
      FableDirectGuestResourceRegistry().destroyed_serials.size());
  for (const auto& [address, serial] : FableDirectGuestResourceRegistry().changed_serials) {
    changes.serial = std::max(changes.serial, serial);
    changes.changed_addresses.push_back(address);
  }
  for (const auto& [address, serial] : FableDirectGuestResourceRegistry().destroyed_serials) {
    changes.serial = std::max(changes.serial, serial);
    changes.destroyed_addresses.push_back(address);
    changes.destroyed_serials.push_back(serial);
  }
  return changes;
}

void AcknowledgeFableDirectResourceChanges(uint64_t serial) {
  QueueFableDirectResourceAck(serial);
  std::unique_lock<std::mutex> lock(g_observer.mutex, std::try_to_lock);
  if (lock.owns_lock()) {
    DrainPendingFableDirectResourceAckLocked();
  }
}

void AcknowledgeFableDirectDestroyedResources(
    const std::vector<uint32_t>& addresses,
    const std::vector<uint64_t>& serials) {
  if (addresses.empty()) {
    return;
  }

  {
    std::lock_guard<std::mutex> pending_lock(
        g_pending_fable_direct_destroy_ack_mutex);
    g_pending_fable_direct_destroy_acks.reserve(
        g_pending_fable_direct_destroy_acks.size() + addresses.size());
    for (size_t i = 0; i < addresses.size(); ++i) {
      const uint32_t address = addresses[i];
      const uint64_t serial = i < serials.size() ? serials[i] : 0;
      if (address != 0 && serial != 0) {
        g_pending_fable_direct_destroy_acks.emplace_back(address, serial);
      }
    }
  }

  std::unique_lock<std::mutex> lock(g_observer.mutex, std::try_to_lock);
  if (lock.owns_lock()) {
    DrainPendingFableDirectDestroyedResourceAcksLocked();
  }
}

std::vector<uint32_t> GetRendererGuestWords(uint32_t guest_address,
                                            uint32_t word_count) {
  std::unique_lock<std::mutex> lock(g_observer.mutex, std::try_to_lock);
  std::vector<uint32_t> words;
  if (!lock.owns_lock() || g_observer.memory_base == nullptr || guest_address == 0 ||
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

}  // namespace refii::renderer
