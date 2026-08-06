#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"
#include "runtime/refii_render_packets.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {

struct FableDirectFrameDatabaseRoot {
  uint64_t call_serial = 0;
  uint64_t context_key = 0;
  uint32_t symbol_address = 0;
  uint32_t lr = 0;
};

struct FableDirectFrameDatabaseDrawPacket {
  uint64_t packet_id = 0;
  uint64_t command_serial = 0;
  uint32_t command_type = 0;
  uint32_t source = 0;
  std::string pass_name;
  std::string debug_label;
  FableDirectFrameDatabaseRoot draw_root;
  FableDirectFrameDatabaseRoot vertex_shader_root;
  FableDirectFrameDatabaseRoot pixel_shader_root;
  FableDirectFrameDatabaseRoot private_texture_wrapper_root;
  bool indexed = false;
  uint32_t primitive_type = 0;
  uint32_t start = 0;
  uint32_t primitive_count = 0;
  uint32_t vertex_or_index_count = 0;
  int32_t base_vertex = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  uint32_t vertex_declaration = 0;
  std::array<uint32_t, 4> render_targets = {};
  uint32_t depth_stencil = 0;
  bool pipeline_pm4_valid = false;
  uint32_t depth_control = 0;
  uint32_t stencil_ref_mask = 0;
  uint32_t stencil_ref_mask_bf = 0;
  uint32_t cull_control = 0;
  uint32_t pa_su_sc_mode_cntl = 0;
  std::array<uint32_t, 4> blend_controls = {};
  std::array<uint32_t, 4> color_write_masks = {};
  uint32_t color_control = 0;
  uint32_t alpha_ref = 0;
  uint32_t alpha_test_enable = 0;
  uint32_t alpha_func = 0;
  RefiiPrivateTextureRectPayload private_texture;
  RefiiConstantStore constants;
  std::array<uint32_t, 16> textures = {};
  // Filled at execute time by the live resolver: per declared sampler slot,
  // the guest address (and its base) of the record that was actually bound
  // for this draw, 0 when the slot rendered textureless. `textures` above is
  // the game's raw SetTexture state; a draw packet must be a complete,
  // self-contained description (design doc §3.2), and diagnosis/replay must
  // not re-derive bindings the live path already decided.
  std::array<uint32_t, 16> resolved_textures = {};
  std::array<uint32_t, 16> resolved_texture_bases = {};
  bool resolved_bindings_valid = false;
  std::array<uint32_t, 16> sampler_states = {};
  std::array<uint32_t, 16> vertex_buffers = {};
  std::array<uint32_t, 16> vertex_strides = {};
  std::array<uint32_t, 16> vertex_offsets = {};
  uint32_t index_buffer = 0;
  uint32_t index_format = 0;
  std::array<RefiiInlineBufferPayload, 16> inline_vertex_buffers = {};
  RefiiInlineBufferPayload inline_index_buffer;
  std::array<RefiiInlineTexturePayload, 3> pix_video_planes = {};
  bool viewport_valid = false;
  float viewport_x = 0.0f;
  float viewport_y = 0.0f;
  float viewport_width = 0.0f;
  float viewport_height = 0.0f;
  float viewport_min_depth = 0.0f;
  float viewport_max_depth = 1.0f;
  bool scissor_valid = false;
  int32_t scissor_left = 0;
  int32_t scissor_top = 0;
  int32_t scissor_right = 0;
  int32_t scissor_bottom = 0;
};

struct FableDirectFrameDatabaseDrawPackets {
  std::vector<FableDirectFrameDatabaseDrawPacket> packets;
  uint32_t omitted_packets = 0;
};

struct FableDirectFrameDatabaseSurfaceDestinationPacket {
  uint64_t relation_serial = 0;
  uint32_t source_surface = 0;
  uint32_t destination_texture = 0;
  uint32_t source_slot = 0xFFFFFFFFu;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t destination_width = 0;
  uint32_t destination_height = 0;
  uint32_t texture_base = 0;
  uint32_t backing_texture = 0;
  uint32_t backing_base = 0;
  uint64_t backing_content_serial = 0;
  uint64_t target_content_serial = 0;
  bool scaled_copy = false;
  bool trusted_destination_edge = false;
  bool wrapper_source_exact = false;
};

struct FableDirectFrameDatabasePresentBlitPacket {
  uint32_t source_target = 0;
  uint32_t output_resource = 0;
  uint32_t source_width = 0;
  uint32_t source_height = 0;
  uint32_t texture_width = 0;
  uint32_t texture_height = 0;
  uint32_t output_width = 0;
  uint32_t output_height = 0;
  uint64_t source_write_serial = 0;
  uint32_t source_frame = 0;
  std::string reason;
};

struct FableDirectFrameDatabaseCommandPacket {
  uint64_t packet_id = 0;
  uint64_t command_serial = 0;
  uint32_t command_type = 0;
  std::string kind;
  std::string pass_name;
  uint64_t draw_packet_id = 0;
  FableDirectFrameDatabaseRoot root;
  std::array<uint32_t, 4> render_targets = {};
  uint32_t depth_stencil = 0;
  uint32_t clear_flags = 0;
  std::array<float, 4> clear_color = {0.0f, 0.0f, 0.0f, 1.0f};
  float clear_depth = 1.0f;
  uint32_t clear_stencil = 0;
  bool rect_valid = false;
  int32_t rect_left = 0;
  int32_t rect_top = 0;
  int32_t rect_right = 0;
  int32_t rect_bottom = 0;
  uint32_t resolve_surface_info = 0;
  std::array<uint32_t, 4> resolve_color_info = {};
  uint32_t resolve_depth_info = 0;
  uint32_t resolve_copy_control = 0;
  uint32_t resolve_source_surface = 0;
  uint32_t resolve_source_slot = 0;
  bool resolve_source_depth = false;
  uint32_t resolve_dest_base = 0;
  uint32_t resolve_dest_pitch = 0;
  uint32_t resolve_dest_info = 0;
  uint32_t resolve_copy_func = 0;
  uint32_t resolve_copy_ref = 0;
  uint32_t resolve_copy_mask = 0;
  uint32_t resolve_surface_slice = 0;
  FableDirectFrameDatabaseSurfaceDestinationPacket surface_destination;
  FableDirectFrameDatabasePresentBlitPacket present_blit;
};

struct FableDirectFrameDatabaseCommandPackets {
  std::vector<FableDirectFrameDatabaseCommandPacket> packets;
  uint32_t omitted_packets = 0;
};

// Frame-database sidecars need enough of the live resource bank to retain
// long-lived shaders as well as recently updated textures.  A 64-entry,
// recency-sorted snapshot dropped the stable shader records in texture-heavy
// title frames, leaving generic shader names even though the live RenderDoc
// marker had the resolved SBK name.
inline constexpr uint32_t kFableDirectResourceDebugCapacity = 512;
inline constexpr uint32_t kFableDirectResourceAnnotationCapacity = 192;

struct FableDirectResourceDebugEntry {
  bool valid = false;
  bool ready = false;
  bool renderdoc_annotated = false;
  FableGuestResourceKind kind = FableGuestResourceKind::kUnknown;
  uint32_t guest_address = 0;
  uint32_t parent_address = 0;
  uint32_t base_or_gpu_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t depth = 0;
  uint32_t format_or_stride = 0;
  uint64_t serial = 0;
  uint64_t content_serial = 0;
  uint64_t upload_generation = 0;
  uint64_t content_hash = 0;
  std::array<char, kFableDirectResourceAnnotationCapacity> annotation = {};
};

struct FableDirectResourceDebugSnapshot {
  std::array<FableDirectResourceDebugEntry, kFableDirectResourceDebugCapacity>
      entries = {};
  uint32_t count = 0;
  uint32_t omitted = 0;
};

inline constexpr uint32_t kFableDirectRenderGraphPassCapacity = 32;
inline constexpr uint32_t kFableDirectRenderGraphPassNameCapacity = 64;

struct FableDirectRenderGraphPassEntry {
  bool valid = false;
  std::array<char, kFableDirectRenderGraphPassNameCapacity> pass_name = {};
  uint64_t first_packet_id = 0;
  uint64_t first_command_packet_id = 0;
  uint32_t command_packets = 0;
  uint32_t clear_packets = 0;
  uint32_t resolve_packets = 0;
  uint32_t draw_packets = 0;
  uint32_t indexed_draw_packets = 0;
  uint32_t rooted_packets = 0;
  uint32_t fully_shader_rooted_packets = 0;
  uint32_t render_target_count = 0;
  uint32_t depth_target_count = 0;
  uint32_t texture_count = 0;
  uint32_t vertex_buffer_count = 0;
};

struct FableDirectRenderGraphSnapshot {
  std::array<FableDirectRenderGraphPassEntry,
             kFableDirectRenderGraphPassCapacity>
      passes = {};
  uint32_t frame_index = 0;
  uint32_t pass_count = 0;
  uint32_t omitted_passes = 0;
  uint32_t draw_packet_count = 0;
  uint32_t omitted_draw_packets = 0;
  uint32_t command_packet_count = 0;
  uint32_t omitted_command_packets = 0;
  uint32_t resource_reference_count = 0;
};

struct FableDirectRenderGraphExecutionSnapshot {
  uint32_t frame_index = 0;
  uint32_t graph_owned_render_steps = 0;
  uint32_t graph_owned_clear_steps = 0;
  uint32_t graph_owned_clear_commands = 0;
  uint32_t graph_owned_draw_steps = 0;
  uint32_t graph_owned_draw_commands = 0;
  uint32_t graph_owned_resolve_steps = 0;
  uint32_t graph_owned_resolve_commands = 0;
  uint32_t bridge_steps = 0;
  uint32_t bridge_commands = 0;
};

struct FableDirectRendererStats {
  uint64_t frames_begun = 0;
  uint64_t frames_ended = 0;
  uint64_t presents = 0;
  // Replaced texture views released once the frame-context fence guarantees the
  // GPU is done with them, and how many are still held. Before retirement
  // existed these grew for the life of the process and pinned their parent
  // textures (RenderDoc: 1220 -> 2865 host textures over ~100 frames).
  uint64_t retired_replaced_texture_views = 0;
  uint64_t live_replaced_texture_views = 0;
  uint32_t last_frame_index = 0;
  uint32_t draw_count = 0;
  uint32_t indexed_draw_count = 0;
  uint32_t resource_creations = 0;
  uint32_t resource_destroys = 0;
  uint32_t resource_uploads = 0;
  uint32_t live_resources = 0;
  uint32_t live_textures = 0;
  uint32_t live_surfaces = 0;
  uint32_t live_vertex_buffers = 0;
  uint32_t live_index_buffers = 0;
  uint32_t live_shaders = 0;
  uint32_t pipeline_cache_hits = 0;
  uint32_t pipeline_cache_misses = 0;
  uint32_t failed_hooks = 0;
  uint32_t failed_resource_syncs = 0;
  uint32_t attempted_draw_commands = 0;
  uint32_t recorded_draw_commands = 0;
  uint32_t failed_draw_commands = 0;
  uint32_t recorded_clear_commands = 0;
  uint32_t recorded_resolve_commands = 0;
  uint32_t render_target_switches = 0;
  uint32_t depth_target_switches = 0;
  uint32_t packet_queue_depth = 0;
  uint32_t packet_queue_replay_planned = 0;
  uint32_t packet_queue_replay_unsupported = 0;
  bool packet_queue_replay_enabled = false;
  uint32_t last_resource_address = 0;
  uint32_t last_resource_size = 0;
  uint32_t last_present_target = 0;
  uint32_t last_present_width = 0;
  uint32_t last_present_height = 0;
  uint32_t last_render_commands = 0;
  uint32_t last_draw_events = 0;
  uint32_t surface_destination_relations = 0;
  uint32_t surface_destination_omitted_relations = 0;
  uint32_t surface_destination_copied_exact = 0;
  uint32_t surface_destination_copied_scaled = 0;
  uint32_t surface_destination_skipped_invalid = 0;
  uint32_t surface_destination_skipped_zero_endpoint = 0;
  uint32_t surface_destination_skipped_missing_copy_root = 0;
  uint32_t surface_destination_skipped_wrong_frame = 0;
  uint32_t surface_destination_skipped_missing_resource = 0;
  uint32_t surface_destination_skipped_not_image = 0;
  uint32_t surface_destination_skipped_not_ready = 0;
  uint32_t surface_destination_skipped_same_texture = 0;
  uint32_t surface_destination_skipped_inexact_wrapper = 0;
  uint32_t surface_destination_skipped_stale_content = 0;
  uint32_t surface_destination_skipped_copy_failed = 0;
  bool frame_active = false;
  bool presented = false;
  bool renderdoc_capture_started = false;
};

#if REFII_RENDERER_HAS_PLUME
void InitializeFableDirectRenderer(plume::RenderDevice* device,
                                   plume::RenderShaderFormat shader_format);
void RecordFableDirectFrame(plume::RenderDevice* device,
                            plume::RenderCommandList* command_list,
                            plume::RenderFramebuffer* output_framebuffer,
                            plume::RenderTexture* output_texture,
                            plume::RenderFramebuffer* present_framebuffer,
                            plume::RenderShaderFormat shader_format,
                            plume::RenderFormat output_depth_format,
                            uint32_t frame_index);
// Present-decoupling step 3 (re-present fast path). Why the next
// RecordFableDirectFrame could compose output that differs from the last
// record — kNone means no reason exists and the caller may re-present its
// held compose target instead. Conservative by construction: non-
// authoritative mode, the startup-video pump, and a missing boundary all
// force a record. Call only from the present-pump thread (it reads the
// pump-thread-only held/carried packet state). Deferred texture captures and
// Bink uploads are NOT covered here — the backend compares their enqueue
// generations against its last record itself.
enum class FableDirectRecordReason : uint8_t {
  kNone = 0,           // no new content — fast path eligible
  kNotAuthoritative,   // observer-aligned compose has no boundary to compare
  kStartupVideoPump,   // startup video frames advance via the pump itself
  kGuestBoundary,      // no guest Present yet, or a new guest frame completed
  kCarriedPackets,     // drained-but-uncomposed packets await their compose
  kPendingPackets,     // late packets for the held boundary would change it
};
FableDirectRecordReason FableDirectFrameRecordReason();
#endif

void BeginFableDirectFrame(uint32_t frame_index);
void EndFableDirectFrame();
void PresentFableDirectFrame(uint32_t present_target, uint32_t width,
                             uint32_t height);
FableDirectRendererStats GetFableDirectRendererStats();
FableDirectFrameDatabaseDrawPackets GetFableDirectFrameDatabaseDrawPackets();
FableDirectFrameDatabaseCommandPackets
GetFableDirectFrameDatabaseCommandPackets();
FableDirectResourceDebugSnapshot GetFableDirectResourceDebugSnapshot();
FableDirectRenderGraphSnapshot GetFableDirectRenderGraphSnapshot();
FableDirectRenderGraphExecutionSnapshot
GetFableDirectRenderGraphExecutionSnapshot();
RefiiFramePacketDump GetRefiiFramePacketDump();
RefiiFramePacketQueueSnapshot GetRefiiFramePacketQueueSnapshot();
bool FableDirectQueuedDrawPresent(uint64_t draw_id, size_t max_commands);
bool FableDirectQueuedRenderPassPresent(std::string_view normalized_pass_name,
                                        size_t max_commands);
bool FableDirectQueuedRenderDocTargetPresent(std::string_view target,
                                             size_t max_commands);
void ResetFableDirectLivePresentSources();
void ShutdownFableDirectRenderer();

}  // namespace refii::renderer
