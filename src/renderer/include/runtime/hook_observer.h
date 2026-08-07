#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/bik/bink_video_uploads.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_types.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"

#include <array>
#include <cstdint>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::renderer {

struct RendererHookObserverSnapshot {
  uint64_t live_resources = 0;
  uint64_t live_textures = 0;
  uint64_t live_surfaces = 0;
  uint64_t live_vertex_buffers = 0;
  uint64_t live_index_buffers = 0;
  uint64_t live_shaders = 0;
  uint64_t resource_creates = 0;
  uint64_t resource_destroys = 0;
  uint64_t resource_locks = 0;
  uint64_t resource_unlocks = 0;
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
  uint64_t scene_inline_vertex_draws = 0;
  uint64_t title_render_list_dispatches = 0;
  uint64_t title_render_list_insert_callbacks = 0;
  uint64_t title_render_list_sorted_inserts = 0;
  uint64_t title_render_list_inserted_nodes = 0;
  uint64_t title_world_registry_observations = 0;
  uint32_t last_title_render_list = 0;
  uint32_t last_title_render_list_owner = 0;
  uint32_t last_title_render_object = 0;
  uint32_t last_title_render_object_vtable = 0;
  uint32_t last_title_render_node = 0;
  uint32_t last_title_render_node_vtable = 0;
  uint32_t last_title_render_sort_key = 0;
  uint32_t last_title_render_flags = 0;
  uint32_t last_title_world = 0;
  uint32_t last_title_world_registry = 0;
  uint32_t last_title_world_registry_begin = 0;
  uint32_t last_title_world_registry_end = 0;
  uint32_t last_title_world_registry_count = 0;
  uint32_t last_title_world_registry_search_key = 0;
  uint32_t last_title_world_registry_matched_entry = 0;
  uint32_t last_title_world_registry_matched_object_count = 0;
  uint32_t last_title_world_registry_first_object = 0;
  uint32_t last_title_world_registry_first_object_vtable = 0;
  uint32_t last_scene_walker_list = 0;
  uint32_t last_scene_walker_begin = 0;
  uint32_t last_scene_walker_end = 0;
  uint32_t last_scene_walker_count = 0;
  uint32_t last_scene_walker_first = 0;
  uint32_t last_scene_walker_result = 0;
  uint32_t last_scene_walker_lr = 0;
  uint64_t pending_draw_events = 0;
  uint64_t dropped_draw_events = 0;
  uint64_t render_passes = 0;
  uint64_t state_flushes = 0;
  uint64_t state_dispatches = 0;
  uint64_t state_dispatch_targets = 0;
  uint64_t render_queue_callbacks = 0;
  uint64_t render_queue_callback_targets = 0;
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
  uint64_t shader_constant_uploads = 0;
  uint64_t fetch_constant_uploads = 0;
  uint64_t shader_constant_upload_nonzero_dwords = 0;
  uint64_t shader_constant_zero_uploads = 0;
  uint64_t shader_constant_rejected_uploads = 0;
  uint64_t pix_decode_frames = 0;
  uint64_t pix_decode_pending_frames = 0;
  uint64_t pix_render_hooks = 0;
  uint64_t pix_render_events = 0;
  uint64_t pix_render_uv_overrides = 0;
  uint64_t pix_capture_hooks = 0;
  uint64_t pix_capture_packet_builder_paths = 0;
  uint64_t pix_capture_suppressed_paths = 0;
  uint64_t pix_capture_suppressed_missing_device_memory = 0;
  uint64_t pix_capture_suppressed_control_disabled = 0;
  uint64_t pix_capture_suppressed_nonzero_r5 = 0;
  uint64_t pix_capture_suppressed_alternate_display_path = 0;
  uint64_t pix_render_bad_packets = 0;
  uint64_t pix_render_bad_vertices = 0;
  uint32_t last_pix_capture_frame = 0;
  uint32_t last_pix_capture_format = 0;
  uint32_t last_pix_capture_width = 0;
  uint32_t last_pix_capture_height = 0;
  uint32_t last_pix_capture_scaler = 0;
  uint32_t last_pix_capture_gamma = 0;
  uint32_t last_pix_capture_control = 0;
  uint32_t last_pix_capture_display_flags = 0;
  uint32_t last_pix_capture_r5 = 0;
  uint32_t last_pix_capture_packet_branch_candidate = 0;
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
  uint32_t vertex_shader_constant_nonzero_dwords = 0;
  uint32_t pixel_shader_constant_nonzero_dwords = 0;
  uint32_t last_shader_constant_bank = 0;
  uint32_t last_shader_constant_first_register = 0;
  uint32_t last_shader_constant_first_component = 0;
  uint32_t last_shader_constant_dwords = 0;
  uint32_t last_shader_constant_nonzero_dwords = 0;
  uint32_t last_shader_constant_source = 0;
  uint32_t last_shader_constant_lr = 0;
  uint32_t last_shader_constant_preview[4] = {};
  uint32_t fetch_constant_nonzero_dwords = 0;
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
  uint32_t surface_copy_alias_count = 0;
  std::array<RendererSurfaceCopyAliasSnapshot, kRendererSurfaceCopyAliasSlots>
      surface_copy_aliases = {};
  uint64_t get_render_target_calls = 0;
  uint32_t last_get_render_target_slot = 0;
  uint32_t last_get_render_target_surface = 0;
  uint64_t state_changes = 0;
  uint32_t render_targets[4] = {};
  uint32_t depth_stencil = 0;
  uint32_t vertex_declaration = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  uint32_t index_buffer = 0;
  uint32_t textures[kRendererD3DTextureSlots] = {};
  std::array<uint32_t, kRendererD3DRenderStateSlots> render_states = {};
  std::array<uint32_t, kRendererD3DSamplerStateSlots> sampler_states = {};
  RendererStreamBindingSnapshot streams[16] = {};
  RendererLastDrawSnapshot last_draw;
  uint32_t viewport_pointer = 0;
  uint32_t scissor_pointer = 0;
  RendererViewportSnapshot viewport;
  RendererScissorSnapshot scissor;
  RendererScalerSnapshot scaler;
  uint32_t clear_flags = 0;
  uint32_t last_state_flush_lr = 0;
  uint32_t last_resource_address = 0;
  uint32_t last_resource_size = 0;
  uint32_t last_resource_stride_or_format = 0;
  uint64_t last_shader_guest_hash = 0;
  uint32_t last_command_source = 0;
  uint32_t last_command_pointer = 0;
  uint32_t last_command_word_count = 0;
  uint32_t last_command_first_word = 0;
  uint32_t last_command_packet_type = 0;
  uint32_t last_command_type3_opcode = 0;
  uint32_t last_command_preview[8] = {};
};

void ObserveRendererHookEnter(const char* symbol, RendererHookKind kind,
                              const RendererHookArgs& args, uint8_t* base,
                              const FableHookSymbolDesc* desc);
void ObserveRendererStateDispatch(const RendererHookArgs& args, uint8_t* base);
void ObserveEmulatedFablePm4SubmitSegment(uint32_t device, uint32_t lr,
                                          uint32_t segment_start,
                                          uint32_t word_count, uint8_t* base);
void ObserveRendererPassQueue(const RendererHookArgs& args, uint8_t* base);
void ObserveRendererHookExit(const char* symbol, RendererHookKind kind,
                             const RendererHookArgs& args, uint32_t result,
                             uint8_t* base, const RendererHookArgs* exit_args,
                             const FableHookSymbolDesc* desc);
// Scene-walk probe entry for native-backend traversal diagnostics
// (renderer_observe_hooks_on_native); deep-observer gated internally.
void ObserveNativeSceneWalkerProbe(const RendererHookArgs& args,
                                   uint8_t* base);
void RefreshBinkVideoUploadsFromRendererHook(std::string_view source_symbol,
                                             uint8_t* base);
void RefreshBinkVideoUploadsFromRendererHook(std::string_view source_symbol,
                                             const RendererHookArgs& args,
                                             uint8_t* base);
// Called from the streamed-texture wrapper touch hook (sub_8221EAF0) the
// first time a pool wrapper is seen resident: the streamed texel bytes landed
// via async file I/O with no observable D3D call, so re-capture texture
// uploads from guest memory.
void NotifyFableStreamedWrapperResident(uint32_t wrapper, uint8_t* base);
// §3.2 capture deferral: drains guest-queued texel-content captures; called
// by the present pump before recording the Fable direct frame.
void DrainDeferredTextureCaptures();
// Mutex-free probe for the present-pump re-present fast path: cumulative
// count of GUEST-SIDE deferred-capture enqueues (relaxed atomic). The pump
// compares it against the value it saw at its last record — a change means
// fresh texel content is queued and the record path must run. The drain's own
// blank-record re-arm does NOT count (see the run-460 note at the counter):
// a retry that captured nothing changes no composed output.
uint64_t DeferredTextureCaptureEnqueueGeneration();

// Guest-byte-scan cost probe (2026-07-28, temporary). FindFirstNonZeroGuestByte
// runs under the observer + registry mutexes, so it cannot log in place.
void GetGuestByteScanStats(uint64_t& calls, uint64_t& ns, uint64_t& bytes);
// Release-at-zero disposition census (2026-07-28, temporary). Answers why the
// direct resource map grows despite the destroy signal being wired: at refcount
// zero a record is either erased, kept alive as neutral/title, deferred because
// something still references it, or was never registered. Reported by the pump.
// drain_batched/drain_walks (2026-08-01, run 493 follow-up): queued teardowns
// resolved through the drain's one-pass batch resolver vs residual scalar
// fallback walks (batch-resolved record erased mid-batch). Expected shape:
// drain_batched ~= teardowns_deferred, drain_walks ~= 0.
void GetResourceReleaseDispositionStats(uint64_t& neutral, uint64_t& deferred,
                                        uint64_t& erased, uint64_t& norecord,
                                        uint64_t& drain_batched,
                                        uint64_t& drain_walks);
// Release-hook cost split (2026-07-30, temporary). Run 465: Release stayed at
// ~178us/call after its body cuts landed while sibling cuts proved out
// (Get2DSurfaceDesc 61 -> 20us). Splits the suppressed Release exit into
// mutex-wait vs under-lock body time and carries execution-proof counters for
// each cut (payload-strip copy, keyed memo invalidation, iterator pure-miss
// cache, desc-address early-out). Read by the per-symbol hook census dump.
struct ResourceReleaseHookCostStats {
  uint64_t locked_calls = 0;       // result==0 exits that took both mutexes
  uint64_t observer_wait_us = 0;   // wait on g_observer.mutex (string path only)
  uint64_t registry_wait_us = 0;   // wait on the registry mutex (string path only)
  uint64_t body_us = 0;            // teardown work under both locks (all paths)
  uint64_t exit_early_outs = 0;    // result!=0 integer early-outs (parse proof)
  uint64_t payload_strips = 0;     // payload-stripped record copies
  uint64_t keyed_invalidations = 0; // targeted memo invalidations (all sites)
  uint64_t iterator_pure_miss_hits = 0; // FindResourceIteratorLocked fast path
  // Deferred release-at-zero teardown (2026-07-30, run 466 follow-up:
  // registry_wait was 92% of Release cost — the guest queued behind the
  // record thread's composes). inline = both try_locks succeeded and the
  // teardown ran in the hook; deferred = queued for the next drain
  // chokepoint. inline + deferred == release-at-zero calls; the
  // g_release_zero_* disposition census still counts each exactly once, at
  // processing time.
  uint64_t teardowns_inline = 0;
  uint64_t teardowns_deferred = 0;
  uint64_t teardown_drains = 0;           // non-empty drain batches
  uint64_t teardown_drain_depth_sum = 0;  // sum of batch sizes (avg = sum/drains)
  uint64_t teardown_drain_depth_max = 0;  // largest single batch
  // Registration-funnel hazard gate (runs 467-470: the unconditional funnel
  // drain blocked every store behind the record thread's compose). skips =
  // queue non-empty + registry contended but no queued address could reach
  // the entry being registered; hazard_waits = genuine recycled-address
  // race, blocking drain taken.
  uint64_t teardown_gate_skips = 0;
  uint64_t teardown_gate_hazard_waits = 0;
};
ResourceReleaseHookCostStats GetResourceReleaseHookCostStats();
// Observer mutex WAIT census. The long-hold log proves fetch-capture and
// deferred-capture-drain hold this mutex 2-44ms at a time (422ms held inside a
// 1.25s window, run 397). These counters say whether anyone is actually queued
// behind those holds -- i.e. whether the observer mutex is the frame, or merely
// busy. Uncontended acquisitions cost one try_lock and no clock read.
void GetObserverMutexContentionStats(uint64_t& uncontended, uint64_t& contended,
                                     uint64_t& wait_us);
// Snapshot-getter attribution: wait (blocking variant's observer-mutex
// acquire) vs locked body vs upload_data bytes copied vs on-demand mints done
// under the mutex. busy counts try_lock failures that fell to the cached path.
void GetSnapshotFetchStats(uint64_t& calls, uint64_t& busy, uint64_t& wait_us,
                           uint64_t& body_us, uint64_t& upload_bytes,
                           uint64_t& mints);
// Sub-feed attribution inside the observer ENTER path (2026-07-29, temporary).
// Four symbols carry ~52s of enter cost and all run the same draw-submit chain;
// this times every candidate helper in one pass instead of one per run.
std::string FormatFeedCensus();
// Called from the direct renderer when a queued draw fails to bind a scratch
// vertex/index buffer: BeginIndexedVertices hands out ring pointers before
// the CPU writes the payload, so re-read the ring from guest memory now.
// Returns true when the record has content afterwards.
bool NotifyFableScratchDrawInputMissing(uint32_t buffer_address);
RendererHookObserverSnapshot GetRendererHookObserverSnapshot();
// Sample-time revalidation for CPU-authored textures (plain CPU writes fire
// no hook): re-captures the record from its own fetch dwords. Returns true
// when content changed. `caller` tags decline-reason logs (nullptr callers
// stay silent — the per-draw revalidator retries every frame).
// `allow_blocking` may only be true when the caller holds no renderer locks
// (frame boundary). `fetch_override` substitutes the consuming draw's fetch
// dwords for the record's own (stale-tenant layouts capture wrong extents).
bool RefreshRendererTextureFromFetchBase(
    uint32_t guest_address, const char* caller = nullptr,
    bool allow_blocking = false,
    const std::array<uint32_t, 6>* fetch_override = nullptr);

// Per-dword float-constant sanitization applied to every constants delivery
// path (currently: the title tonemap's +inf colour-sensitivity threshold,
// which Vulkan turns into NaN white-outs). Packet-bank appliers must run
// values through this too, or the executor reintroduces the white target.
uint32_t RendererSanitizeShaderFloatConstantDword(bool pixel_bank,
                                                  uint32_t dword_index,
                                                  uint32_t value);

}  // namespace refii::renderer
