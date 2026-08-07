#pragma once

#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/bik/bink_plane_publish.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "renderdoc_debugger.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_guest_resource_registry.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_private_rect_symbols.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_surface_texture_link_classification.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_private_rect_geometry.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_cache_keys.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_batch.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_classification.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_command_readback.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_index_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_inline_data.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_primitive8.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_render_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_scratch_wrapper_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_direct_resource_readiness.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"
#include "runtime/refii_renderer/plume/fable/private_rect/fable_direct_synthetic_private_rect.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_database_command_packets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_frame_actions.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_analysis.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_packet_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_refii_replay_builders.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_texture_slot_masks.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_capture.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_resource_debug_snapshot.h"
#include "runtime/refii_renderer/plume/fable/debug/fable_renderdoc_draw_targets.h"
#include "runtime/refii_renderer/plume/fable/packets/fable_render_graph_snapshot.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_constants.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_draw_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_frame_packet_classification.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_resource_facts.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_plane_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_bink_vertex_transform.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_readiness.h"
#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_uploads.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_classification.h"
#include "runtime/refii_renderer/plume/fable/video/fable_video_constants.h"
#include "runtime/plume/draw/plume_draw_state.h"
#include "runtime/refii_renderer/plume/general/plume_renderdoc_capture.h"
#include "runtime/plume/resource/plume_resource_manager_internal.h"
#include "runtime/plume/resource/plume_vertex_declaration.h"
#include "renderer_runtime.h"
#include "runtime/shader_library.h"
#include <algorithm>
#include <atomic>
#include <array>
#include <chrono>
#include <cctype>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include <rex/logging.h>
#include <tracy/Tracy.hpp>
#include <plume_render_interface_builders.h>
#include "plume_present_blit_ps.hlsl.dxil.h"
#include "plume_present_blit_vs.hlsl.dxil.h"
#include "plume_present_blit_ps.hlsl.spirv.h"
#include "plume_present_blit_vs.hlsl.spirv.h"

#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"

namespace refii::renderer {

#if REFII_RENDERER_HAS_PLUME

class ScopedFableDirectRegistryUnlock {
 public:
  ScopedFableDirectRegistryUnlock()
      : mutex_(FableDirectGuestResourceRegistryMutex()) {
    mutex_.unlock();
  }

  ~ScopedFableDirectRegistryUnlock() {
    mutex_.lock();
  }

  ScopedFableDirectRegistryUnlock(const ScopedFableDirectRegistryUnlock&) =
      delete;
  ScopedFableDirectRegistryUnlock& operator=(
      const ScopedFableDirectRegistryUnlock&) = delete;

 private:
  std::recursive_mutex& mutex_;
};

enum class DirectPrivateTextureResolveSource : uint8_t {
  kNone,
  kIdentityCommandFetch,
  kIdentityResourceFetch,
  kIdentityBase,
};

enum class DirectPrivateTextureCopyActionMatch : uint8_t {
  kNone,
  kExactDestination,
  kExactTrackedDestination,
  kExactParentDestination,
  kExactIdentityDestination,
  kExactSurfaceTextureLink,
  kExactCurrentColorAction,
};

struct DirectPrivateTextureCopyActionCacheEntry {
  const FablePrivateTextureCopyAction* action = nullptr;
  DirectPrivateTextureCopyActionMatch match =
      DirectPrivateTextureCopyActionMatch::kNone;
  uint64_t private_copy_serial = 0;
};

struct DirectPrivateTextureCopyActionSampleCacheEntry {
  uint32_t sample_address = 0;
  bool resolved = false;
  uint64_t action_serial = 0;
  uint64_t private_copy_serial = 0;
};

struct DirectPrivateTextureExactInterestCacheEntry {
  const FablePrivateTextureCopyAction* action = nullptr;
  uint64_t private_copy_serial = 0;
  uint64_t texture_identity_serial = 0;
  uint32_t frame_index = 0;
  uint64_t draw_serial = 0;
};

struct DirectMutablePrivateHeaderExactSampleEntry {
  uint32_t sample_address = 0;
  uint32_t frame_index = 0;
  uint64_t draw_serial = 0;
  uint64_t sample_content_serial = 0;
  uint64_t private_copy_serial = 0;
};

struct DirectPrivateTextureBackingMissCacheEntry {
  uint64_t private_copy_serial = 0;
  uint64_t texture_identity_serial = 0;
  uint64_t private_texture_alias_serial = 0;
  uint64_t source_content_serial = 0;
  uint64_t target_content_serial = 0;
  uint32_t frame_index = 0;
};

struct DirectRegisteredPrivateRectExecuteDecisionCacheEntry {
  uint64_t private_copy_serial = 0;
};

struct DirectPrivateFeedbackEnsureMemoEntry {
  uint32_t frame_index = 0;
  uint64_t relation_generation = 0;
  uint64_t private_copy_serial = 0;
  uint64_t texture_identity_serial = 0;
  uint64_t private_texture_alias_serial = 0;
  uint64_t source_content_serial = 0;
  uint64_t target_content_serial = 0;
  bool source_is_bound_target = false;
};

struct DirectCommandInputReadyCacheEntry {
  uint32_t frame_index = 0;
  uint64_t resource_serial = 0;
  uint64_t resource_destroys = 0;
  uint64_t private_copy_serial = 0;
  uint64_t surface_texture_link_serial = 0;
  uint64_t texture_identity_serial = 0;
  uint64_t private_texture_alias_serial = 0;
};

struct DirectExactCurrentProducerOwnerCacheEntry {
  bool owned = false;
  uint32_t frame_index = 0;
  uint64_t command_serial = 0;
  uint64_t draw_serial = 0;
  uint64_t resource_serial = 0;
  uint64_t resource_destroys = 0;
  uint64_t private_copy_serial = 0;
  uint64_t surface_texture_link_serial = 0;
  uint64_t surface_destination_relation_serial = 0;
  uint64_t texture_identity_serial = 0;
  uint64_t private_texture_alias_serial = 0;
};

struct DirectScaledPrivateRectBlitResources {
  plume::RenderShaderFormat shader_format = plume::RenderShaderFormat::UNKNOWN;
  std::unique_ptr<plume::RenderPipelineLayout> pipeline_layout;
  std::unordered_map<uint32_t, std::unique_ptr<plume::RenderPipeline>>
      pipelines_by_format;
  std::vector<std::unique_ptr<plume::RenderDescriptorSet>> texture_sets;
  uint32_t texture_set_index = 0;
  std::array<const plume::RenderTexture*, kPresentBlitDescriptorSetRingSize>
      texture_set_textures = {};
  std::array<const plume::RenderTextureView*, kPresentBlitDescriptorSetRingSize>
      texture_set_views = {};
  std::array<plume::RenderTextureLayout, kPresentBlitDescriptorSetRingSize>
      texture_set_layouts = {};
  std::unique_ptr<plume::RenderDescriptorSet> sampler_set;
  std::unique_ptr<plume::RenderSampler> sampler;
};

struct DirectPrivateTextureSetupClearIntent {
  uint32_t frame_index = 0;
  uint64_t serial = 0;
  uint64_t draw_serial = 0;
  uint32_t lr = 0;
  uint32_t target = 0;
  uint32_t target_width = 0;
  uint32_t target_height = 0;
  uint32_t target_format = 0;
  bool depth_target = false;
  uint64_t clear_root_call_serial = 0;
  uint64_t clear_root_context_key = 0;
  uint32_t clear_root_symbol_address = 0;
  uint64_t target_root_call_serial = 0;
  uint64_t target_root_context_key = 0;
  uint32_t target_root_symbol_address = 0;
};

struct DirectPrivateTextureSetupClearAction {
  uint32_t setup_frame_index = 0;
  uint64_t setup_serial = 0;
  uint64_t setup_draw_serial = 0;
  uint32_t setup_lr = 0;
  uint32_t setup_target = 0;
  uint32_t setup_target_width = 0;
  uint32_t setup_target_height = 0;
  uint32_t setup_target_format = 0;
  bool depth_target = false;
  uint64_t clear_root_call_serial = 0;
  uint64_t clear_root_context_key = 0;
  uint32_t clear_root_symbol_address = 0;
  uint64_t target_root_call_serial = 0;
  uint64_t target_root_context_key = 0;
  uint32_t target_root_symbol_address = 0;
  FablePrivateTextureCopyAction action;
};

struct PendingSurfaceContentCarry {
  std::unique_ptr<plume::RenderTexture> old_texture;
  uint32_t guest_address = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t queued_frame = 0;
  uint64_t content_serial = 0;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
};

struct DirectClearFramebufferMemoEntry {
  DirectFramebufferBinding binding;
  uint64_t framebuffer_invalidation_generation = 0;
  // Requested addresses (render targets + depth) with no tracked record at
  // store time; capacity covers the full universe (4 color + 1 depth).
  std::array<uint32_t, 8> record_less_addresses = {};
  uint32_t record_less_count = 0;
};

struct FableDirectRendererState {
  std::mutex mutex;
  FableDirectRendererStats stats;
  std::vector<FableDirectFrameDatabaseDrawPacket> frame_database_draw_packets;
  // command_serial -> index into frame_database_draw_packets, so the execute
  // path can write resolved bindings back into the stored packet without a
  // linear scan. Cleared wherever the packet vector is cleared.
  std::unordered_map<uint64_t, uint32_t>
      frame_database_draw_packet_index_by_serial;
  std::vector<FableDirectFrameDatabaseCommandPacket>
      frame_database_command_packets;
  uint32_t frame_database_omitted_draw_packets = 0;
  uint32_t frame_database_omitted_command_packets = 0;
  RefiiFramePacketQueueSnapshot refii_packet_queue;
  RendererHookObserverSnapshot cached_hook_stats;
  uint64_t resource_serial = 0;
  uint64_t resource_creations = 0;
  uint64_t resource_destroys = 0;
  uint64_t resource_uploads = 0;
  uint64_t failed_resource_syncs = 0;
  bool have_cached_hook_stats = false;
  uint32_t last_hook_stats_frame = 0;
  uint64_t last_render_target_switch_count = 0;
  uint64_t last_depth_target_switch_count = 0;
#if REFII_RENDERER_HAS_PLUME
  plume::RenderDevice* device = nullptr;
  plume::RenderShaderFormat shader_format = plume::RenderShaderFormat::UNKNOWN;
  std::unordered_map<uint32_t, PlumeTrackedResource> resources;
  std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
      known_uploads;
  std::unordered_map<DirectPipelineKey, std::unique_ptr<plume::RenderPipeline>,
                     DirectPipelineKeyHash>
      pipelines;
  std::vector<std::unique_ptr<plume::RenderBuffer>> frame_upload_buffers;
  std::unordered_map<DirectUploadBufferKey,
                     std::vector<DirectCachedUploadBuffer>,
                     DirectUploadBufferKeyHash>
      cached_upload_buffers;
  // Clear-path framebuffer memo; see DirectClearFramebufferMemoEntry.
  // Frame-scoped: cleared where the drawphase accumulators reset.
  std::unordered_map<uint64_t, DirectClearFramebufferMemoEntry>
      clear_framebuffer_memo;
  // Per-frame shader-constant arena (see DirectConstantArenaChunk). Indexed by
  // frame_index % kDirectConstantArenaSlots.
  std::array<DirectConstantArenaSlot, 4> constant_arena_slots;
  size_t constant_arena_active_slot = 0;
  // Diagnostics for the 120-frame cadence line: how much the arena served and
  // how many chunks it needed.
  uint32_t constant_arena_bytes_this_frame = 0;
  uint32_t constant_arena_allocs_this_frame = 0;
  uint32_t constant_arena_chunks_created = 0;
  std::unordered_map<uint64_t, uint32_t> private_texture_aliases_by_fetch;
  std::unordered_map<uint32_t, uint32_t> private_texture_aliases_by_base;
  std::unordered_map<uint32_t, uint32_t> private_texture_aliases_by_object;
  // Guest base address -> tracked texture our GPU last drew into at that
  // base. Console aliasing rule: a fetch whose base the GPU wrote reads the
  // GPU's texels, so sampling must prefer these over CPU round-trip captures
  // (composed billboard pages are GPU-authoritative).
  std::unordered_map<uint32_t, uint32_t> gpu_drawn_textures_by_base;
  uint64_t private_texture_alias_serial = 0;
  std::unordered_map<uint64_t, uint32_t> private_texture_feedback_aliases;
  std::unordered_map<uint64_t, uint32_t>
      private_texture_feedback_aliases_by_target_identity;
  std::unordered_map<uint32_t, uint64_t>
      private_texture_feedback_alias_target_serial;
  std::deque<uint32_t> private_texture_feedback_alias_order;
  // (draw-key ^ site/slot salt) -> generation snapshot; see
  // DirectPrivateFeedbackEnsureMemoEntry. Frame-scoped.
  std::unordered_map<uint64_t, DirectPrivateFeedbackEnsureMemoEntry>
      private_feedback_ensure_memo;
  std::unordered_map<uint64_t, DirectPrivateTextureCopyActionCacheEntry>
      private_texture_copy_action_cache;
  std::unordered_map<uint64_t, DirectPrivateTextureCopyActionSampleCacheEntry>
      private_texture_copy_action_sample_cache;
  std::unordered_map<uint64_t, DirectPrivateTextureExactInterestCacheEntry>
      private_texture_exact_interest_cache;
  std::unordered_map<uint64_t, DirectMutablePrivateHeaderExactSampleEntry>
      mutable_private_header_exact_samples;
  std::unordered_map<uint64_t, DirectPrivateTextureBackingMissCacheEntry>
      private_texture_backing_miss_cache;
  std::unordered_map<uint64_t,
                     DirectRegisteredPrivateRectExecuteDecisionCacheEntry>
      registered_private_rect_execute_decision_cache;
  std::unordered_map<uint64_t, DirectCommandInputReadyCacheEntry>
      command_input_ready_cache;
  std::unordered_map<uint64_t, DirectExactCurrentProducerOwnerCacheEntry>
      exact_current_producer_owner_cache;
  std::unordered_map<uint64_t, DirectSurfaceAliasCopyCacheEntry>
      surface_alias_copy_cache;
  std::unordered_set<uint64_t> surface_destination_copy_frame_keys;
  std::unordered_map<uint32_t, DirectRegisteredTextureIdentityCacheEntry>
      registered_texture_identity_cache;
  std::unordered_map<uint64_t, uint32_t> surface_texture_link_sample_frame_cache;
  std::unordered_set<uint64_t> prepared_exact_copy_destination_keys;
  std::unordered_set<uint64_t> published_stable_surface_texture_link_keys;
  std::unordered_set<uint64_t> materialized_private_rect_action_frame_keys;
  std::unordered_set<uint64_t> begin_vertices_title_action_frame_keys;
  std::unordered_set<uint64_t> preflight_private_copy_action_frame_keys;
  std::unordered_set<uint64_t> remembered_private_copy_action_frame_keys;
  std::unordered_set<uint64_t> stable_private_copy_action_keys;
  std::unordered_set<uint64_t> remembered_private_rect_target_source_frame_keys;
  std::unordered_set<uint64_t> private_copy_action_surface_link_frame_keys;
  std::unordered_set<uint64_t> materialized_private_surface_link_frame_keys;
  std::unordered_set<uint64_t> remembered_private_feedback_surface_link_frame_keys;
  std::unordered_set<uint64_t> exact_private_rect_edge_frame_keys;
  std::unordered_set<uint64_t> current_target_surface_link_frame_keys;
  std::unordered_set<uint64_t> sibling_source_action_owner_frame_keys;
  std::unordered_set<uint64_t> surface_link_command_identity_exclusion_frame_keys;
  std::unordered_set<uint32_t> initialized_title_scene_targets_this_frame;
  std::unordered_set<uint64_t> duplicate_exact_private_rect_surface_link_keys;
  std::unordered_set<uint64_t> retired_stale_surface_link_log_keys;
  std::unordered_set<uint64_t>
      targetless_wrapper_materialization_reject_frame_keys;
  std::unordered_map<uint64_t, FablePrivateTextureCopyAction>
      materialized_private_rect_actions_by_exact_key;
  std::deque<DirectPrivateTextureSetupClearIntent>
      private_texture_setup_clear_intents;
  std::deque<DirectPrivateTextureSetupClearAction>
      private_texture_setup_clear_actions;
  std::unordered_set<uint64_t> private_texture_setup_clear_intent_keys;
  std::unordered_set<uint64_t> private_texture_setup_clear_surface_link_keys;
  DirectScaledPrivateRectBlitResources scaled_private_rect_blit;
  uint32_t next_private_texture_upload_alias = 0xF1000000u;
  uint32_t next_title_billboard_upload_alias = 0xF2000000u;
  uint32_t cached_upload_buffer_count = 0;
  // Frame index of the last TrimCachedUploadBuffersLocked sweep, so the sweep
  // runs at most once per frame instead of once per cache miss.
  uint32_t cached_upload_buffer_trim_frame = UINT32_MAX;
  // Frame + registry content generation at the last per-draw
  // surface-destination scan, so a scan is only rebuilt when the relation
  // registry content has actually changed.
  uint32_t last_surface_destination_scan_frame = UINT32_MAX;
  uint64_t last_surface_destination_scan_generation = 0;
  // Per-frame, per-destination-bucket attempt marks for the TARGETED
  // destination scan: bucket (relation destination_texture) -> relation
  // generation at its last visit this frame. A targeted run no longer proves
  // "everything is recorded" the way the old global run did, so the skip
  // decision moves to bucket granularity: a draw whose relevant buckets were
  // all visited at the current generation needs nothing; one that binds a
  // bucket the previous (differently-targeted) runs never visited triggers a
  // catch-up run for exactly that bucket. Cleared lazily on frame change.
  std::unordered_map<uint32_t, uint64_t>
      surface_destination_bucket_attempt_generation;
  uint32_t surface_destination_bucket_attempt_frame = UINT32_MAX;
  std::unique_ptr<plume::RenderSampler> default_sampler;
  plume::RenderTexture* last_scene_texture = nullptr;
  plume::RenderTextureView* last_scene_view = nullptr;
  plume::RenderTextureLayout last_scene_layout =
      plume::RenderTextureLayout::UNKNOWN;
  uint32_t last_scene_target = 0;
  uint32_t last_scene_width = 0;
  uint32_t last_scene_height = 0;
  uint64_t last_scene_write_serial = 0;
  uint32_t last_scene_frame_index = 0;
  // The depth surface most recently BOUND as a framebuffer attachment, and the
  // frame it was bound on. This is the depth analogue of last_scene_target and
  // exists for exactly one consumer: the stale-resource sweep's eligibility
  // test. See the depth-eviction note there for why a blanket
  // `texture_depth_target` exemption could not stay.
  uint32_t last_bound_depth_target = 0;
  uint64_t last_bound_depth_frame = 0;
  plume::RenderTexture* last_submitted_scene_texture = nullptr;
  plume::RenderTextureView* last_submitted_scene_view = nullptr;
  plume::RenderTextureLayout last_submitted_scene_layout =
      plume::RenderTextureLayout::UNKNOWN;
  std::unordered_map<plume::RenderTexture*, plume::RenderTextureLayout>
      known_texture_layouts;
  uint32_t last_submitted_scene_target = 0;
  uint32_t last_submitted_scene_width = 0;
  uint32_t last_submitted_scene_height = 0;
  uint64_t last_submitted_scene_write_serial = 0;
  uint32_t last_submitted_scene_frame_index = 0;
  DirectScratchWrapperShaderBinding last_scratch_wrapper_shader_binding;
  // Present-path tracking: the held present sources plus the bookkeeping that
  // only the present path reads. Grouped so the subsystem resets as one unit
  // (ResetDirectPresentTrackingLocked) and so present ownership can leave this
  // TU as one piece rather than as 23 scattered fields.
  //
  // DECLARATION ORDER IS LOAD-BEARING: each view is declared BEFORE the texture
  // it references, so `present = {}` assigns (and therefore releases) the view
  // first. Vulkan forbids destroying an image while a view of it is alive, and
  // VMA asserts on leaked blocks at exit; the open-coded shutdown reset this
  // replaces relied on the same view-before-texture order.
  struct DirectPresentTrackingState {
    // Snapshot of the video composite taken right after each display-sized
    // video draw. Present samples this copy: the live composite gets cleared
    // by interleaved title work between ~25fps movie draws while the pump
    // blits at display rate, so presenting the live texture shows black on
    // most ticks.
    std::unique_ptr<plume::RenderTextureView> video_hold_view;
    std::unique_ptr<plume::RenderTexture> video_hold_texture;
    uint32_t video_hold_width = 0;
    uint32_t video_hold_height = 0;
    plume::RenderFormat video_hold_format = plume::RenderFormat::UNKNOWN;
    uint32_t video_hold_target = 0;
    uint32_t video_hold_frame = 0;
    uint64_t video_hold_serial = 0;
    std::unique_ptr<plume::RenderTextureView> hold_view;
    std::unique_ptr<plume::RenderTexture> hold_texture;
    uint32_t hold_width = 0;
    uint32_t hold_height = 0;
    plume::RenderFormat hold_format = plume::RenderFormat::UNKNOWN;
    uint32_t hold_target = 0;
    uint32_t hold_frame = 0;
    uint64_t hold_serial = 0;
    uint64_t consecutive_resolve_failures = 0;
    uint64_t no_blit_frames = 0;
    uint32_t last_logged_source = 0;
    uint32_t last_logged_width = 0;
    uint32_t last_logged_height = 0;
    uint64_t last_logged_detail_serial = 0;
    uint32_t last_logged_detail_target = 0;
  };
  DirectPresentTrackingState present;
  // Pass-topology history for render pass classification: color targets
  // written so far this frame (reset per frame), plus sticky cross-frame sets
  // of every depth target seen and every depth target later sampled as a
  // texture (a depth-only pass writing a sampled depth target is a shadow
  // pass).
  std::unordered_set<uint32_t> frame_written_color_targets;
  std::unordered_set<uint32_t> known_depth_targets;
  std::unordered_set<uint32_t> sampled_depth_targets;
  plume::RenderTexture* pending_video_hold_texture = nullptr;
  // Frame index of the last clear-triggered hold snapshot: a clear on the
  // composite marks the previous compose as COMPLETE (console presents only
  // completed composes); while these fire, per-batch snapshots stand down so
  // intermediate compose states (strip repeats/garbage flashes) never
  // present.
  uint32_t video_hold_clear_snapshot_frame = 0;
  uint32_t pending_video_hold_target = 0;
  uint32_t pending_video_hold_width = 0;
  uint32_t pending_video_hold_height = 0;
  plume::RenderFormat pending_video_hold_format = plume::RenderFormat::UNKNOWN;
  // Views replaced while the GPU may still reference them from an in-flight
  // descriptor set. Retaining them is CORRECT -- destroying a view a recorded
  // command still points at is a use-after-free -- but they were only ever
  // released at device re-init or shutdown, so this grew for the life of the
  // process on a PER-FRAME path.
  //
  // Measured cost (RenderDoc, run 316): host textures climbed 1220 -> 2865 over
  // ~100 frames, ~16 per frame, against ~393 live guest resources. A retained
  // view keeps its parent texture alive, so this list was pinning them. The
  // alias phase grew 8.0 -> 18.6 -> 40.8 ms over the same window because every
  // resource-shaped cost scales with that population.
  //
  // Now RETIRED on the same argument the constant arena uses: plume waits on
  // the frame-context fence from N frames ago before recording begins, and the
  // run log reports 3 frame contexts, so a view replaced 4+ frames ago cannot
  // still be referenced. Same 4-vs-3 margin as the arena.
  struct RetainedTextureView {
    uint32_t frame_index = 0;
    std::unique_ptr<plume::RenderTextureView> view;
  };
  std::vector<RetainedTextureView> retained_replaced_texture_views;
  uint32_t fable_scene_target = 0;
  uint32_t fable_scene_width = 0;
  uint32_t fable_scene_height = 0;
  uint32_t title_scene_target = 0;
  uint32_t title_scene_width = 0;
  uint32_t title_scene_height = 0;
  uint32_t current_frame_index = 0;
  bool current_frame_has_pix_video_draw = false;
  uint32_t render_graph_graph_owned_render_steps = 0;
  uint32_t render_graph_graph_owned_clear_steps = 0;
  uint32_t render_graph_graph_owned_clear_commands = 0;
  uint32_t render_graph_graph_owned_draw_steps = 0;
  uint32_t render_graph_graph_owned_draw_commands = 0;
  uint32_t render_graph_graph_owned_resolve_steps = 0;
  uint32_t render_graph_graph_owned_resolve_commands = 0;
  uint32_t packet_queue_replayed_surface_destination_actions = 0;
  bool packet_queue_owns_surface_destination = false;
  uint32_t packet_queue_replayed_present_blit_actions = 0;
  uint32_t render_graph_bridge_steps = 0;
  uint32_t render_graph_bridge_commands = 0;
  uint32_t frame_private_fetch_identity_aliases = 0;
  uint32_t frame_private_copy_exact_matches = 0;
  uint32_t last_pix_video_target = 0;
  uint32_t last_pix_video_frame = 0;
  uint64_t scene_write_serial = 0;
  bool draw_resources_ready = false;
  std::string last_failed_direct_draw_reason;
  uint64_t last_failed_direct_draw_serial = 0;
  std::vector<PendingSurfaceContentCarry> pending_surface_content_carries;
  // Guest texture objects whose execute-time resolve failed while the
  // identity registry holds a meaningful CPU upload for them: the next
  // frame's interest phase pulls these through the observer sync (most
  // title-sync gates never see these commands, so the resolver tail is the
  // only reliable detection point).
  std::unordered_set<uint32_t> deferred_resolve_interest;
  // Per-frame memo: CPU-authored textures already revalidated this frame
  // (sample-time re-capture; plain CPU writes fire no hook).
  std::unordered_set<uint32_t> revalidated_cpu_textures;
  // Frame-scoped: composite targets whose compose bound the null texture for
  // a populated slot this frame — presenting them paints the black
  // mid-compose panels; the present prefers the held complete composite.
  std::unordered_set<uint32_t> textureless_compose_targets;
  // Shader-used texture slots that bound the null descriptor because the
  // command stated NOTHING for them. Distinct from textureless_compose_targets,
  // which only counts slots the game DID populate and we then failed to
  // resolve. This one is the silent class — see the note at the bind site.
  uint64_t unstated_slot_null_binds = 0;
  // GTL shadow validation (§3.4 pre-cutover): each frame's drained packets
  // are matched against the direct draw commands as they record — the match
  // rate is the executor cutover-readiness metric.
  std::vector<refii::gtl::DrawPacket> gtl_shadow_packets;
  std::vector<bool> gtl_shadow_matched;
  // Previous generation kept matchable one extra frame: the direct pipeline
  // stages commands, so draws often record a frame after their packets drain.
  std::vector<refii::gtl::DrawPacket> gtl_shadow_packets_prev;
  std::vector<bool> gtl_shadow_matched_prev;
  // Capture-only shadow stream for the EDRAM resolve family (sub_82206888 =
  // D3DDevice_Resolve). ResolvePacket is a distinct primitive class (no
  // shader/geometry identity — a resolve authors its own PM4 quad), so keep it
  // separate from draw matching until the §3b census passes.
  std::vector<refii::gtl::ResolvePacket> gtl_resolve_shadow_packets;
  std::vector<refii::gtl::ResolvePacket> gtl_resolve_shadow_packets_prev;
  std::vector<refii::gtl::ClearPacket> gtl_clear_shadow_packets;
  // T2 (2026-08-02): tessellated patch draw stream (terrain 0x82207AF8 /
  // water 0x8221C898), drained per frame and interleaved at packet_id
  // position alongside the resolve/clear streams (kGtlTessDraw commands).
  std::vector<refii::gtl::TessellatedPatchDrawPacket> gtl_tess_shadow_packets;
  // T3: tess-variant vertex shaders (`<stem>.tess` artifacts, T1) keyed by
  // the BASE guest VS bytecode hash. Only successful creations are stored;
  // misses are remembered separately so the LOUD miss log fires exactly once
  // per hash (risk R4: a tess-variant miss must never silent-drop).
  std::unordered_map<uint64_t, std::unique_ptr<plume::RenderShader>>
      tess_vertex_shader_variants;
  std::unordered_set<uint64_t> tess_vertex_shader_variant_misses;
  // T3: cached host index buffers for the synthetic (N+1)x(N+1) domain grid,
  // keyed by N. 6*N*N R32 indices, two triangles per cell; the tess-variant
  // VS derives domain UV from the vertex id (T1 r0 seed contract).
  std::unordered_map<uint32_t, std::unique_ptr<plume::RenderBuffer>>
      tess_grid_index_buffers;
  // NOTE (2026-07-17): the durable "what did the blit through this proxy
  // sample" index that used to live here is DELETED. It answered a question
  // that does not exist: sub_82206888 is D3DDevice_Resolve, whose source is
  // EDRAM (selected by Flags&7), not a texture. The index keyed on r6, which
  // the superseded arg map called the source header but is actually the
  // DESTINATION texture. Its only consumer was inert (hard-coded usable=false
  // since run 432), so removing both is a no-op at runtime. Run 432's
  // conclusion — "the source memory is transient shared scratch, so no address
  // identity can name it" — was measuring destination memory and should not be
  // carried forward as a fact about sources.
  uint32_t gtl_shadow_direct_draws = 0;
  // Per-frame cost of the proof-only GTL shadow validation, reported in the
  // command timing breakdown so its price is measured, not assumed.
  double gtl_shadow_ms_accum = 0.0;
  // §3 binding-authority shadow: does the sampling maze agree with binding the
  // texture the command STATES for the slot? Run-cumulative; this is the gate
  // metric for "sampled-slot binding reads GTL".
  //
  // `bink_override` is split out of `differ` because it is not a
  // disagreement — it is the video path deliberately substituting a decoded
  // plane for the surface object the guest named. Run 149 proved every
  // divergence was that substitution (both bursts land inside the two Bink
  // windows), so with it separated `differ` is a real alarm: nonzero means
  // the maze bound something the packet did not state, for a reason nobody
  // has accounted for.
  uint64_t gtl_binding_shadow_slots = 0;
  uint64_t gtl_binding_shadow_agree = 0;
  uint64_t gtl_binding_shadow_bink_override = 0;
  uint64_t gtl_binding_shadow_differ = 0;
  uint64_t gtl_binding_shadow_maze_only = 0;
  uint64_t gtl_binding_shadow_stated_only = 0;
  uint64_t gtl_binding_shadow_neither = 0;
  // Direct draws whose shader pair exists in NO packet of the current or
  // previous frame: the true GTL completeness gap — must be ~0 before cutover.
  uint32_t gtl_shadow_direct_unmatched = 0;
  // Guest's own DisplaySwap frontbuffer (fetch decoded at the swap root),
  // cached lock-free at frame start. Present resolution falls back to it
  // when every scene-candidate tier starves (post-title phases render into
  // targets the heuristics don't classify).
  uint32_t guest_swap_frontbuffer_base = 0;
  uint32_t guest_swap_frontbuffer_object = 0;
  // GTL's own statement of the same front buffer, taken from D3DDevice_Swap's
  // r4 at the present root. Migration step 4 runs this IN PARALLEL with the two
  // observer-derived values above rather than cutting over: the plan's rule is
  // that both write the same value and are compared before either is deleted.
  // IDA (agent pass 2026-07-26) confirms they are the same object —
  // D3DDevice_Swap(dev, g_FrontBufferTexture, NULL), and g_FrontBufferTexture
  // (0x8331B13C) is the single front buffer; Fable does NOT double-buffer.
  uint32_t gtl_swap_frontbuffer_object = 0;
  // Observer draw-dispatch total, cached at frame top with the frontbuffer
  // snapshot (observer mutex must never be taken under the renderer mutex) —
  // the top of the draw funnel ledger.
  uint64_t observer_draws_at_frame_top = 0;
  // Pump frame of the last video-latch copy (one copy per frame max).
  uint32_t video_latch_frame_index = UINT32_MAX;
  // Blocking-retry budget per address, after a contention-starved request.
  //
  // WAS a one-shot set, on the stated assumption that "the changed stream
  // covers records that appear later". Run 550 disproves that: every
  // `fetch_decode=ok` resolver null carried `tracked=0` with `base_objs>=1`,
  // i.e. the identity map knew the object and no host image ever appeared for
  // it, and the starved-request log reads `busy=1` with `returned=0` — the
  // observer was contended, the cache-only path cannot mint, and the single
  // retry was therefore spent on a transient failure. `recovered=0` then burned
  // the address FOREVER (the set had no clear site anywhere in the tree).
  //
  // A count, not an unbounded retry: run 487 is the other lesson — re-blocking
  // on never-materializing endpoints cost 7.8s/compose. Recovery erases the
  // entry, so an address that proves real gets a fresh budget next time it
  // starves.
  std::unordered_map<uint32_t, uint32_t> starved_blocking_retry_attempts;
  // Run-489 destroyed-retirement carry-over (destroyed_ms=626 in ONE sync
  // call): destroyed addresses whose full teardown fan-out did not fit the
  // per-call retire budget, keyed by address with the newest destroyed
  // serial. The wholesale serial ack erases observer destroyed_serials, so
  // once deferred the queue OWNS the retirement; drained a budget's worth
  // per sync call. Per-address ordering (retire before a re-registration at
  // the same address) is preserved by the hazard override in the destroyed
  // pass; cross-address order is immaterial for dead records.
  std::unordered_map<uint32_t, uint64_t> pending_destroyed_retirements;
  // Stale-eviction carry-over: remainder of the every-16th-frame staleness
  // sweep's candidate list that did not fit the eviction budget. Drained
  // (with re-validation) a budget's worth per sync call and REBUILT by the
  // next sweep, so no dedup bookkeeping is needed.
  std::vector<uint32_t> pending_stale_evictions;
  // Cumulative §3.4 flip gate: matched packet/command pairs whose render
  // states were compared, and how many pairs diverged in any slot.
  uint64_t gtl_state_compared = 0;
  uint64_t gtl_state_diverged = 0;
  // Constants flip gate: matched pairs whose packet-captured float banks
  // were compared against the observer command's, and diverged pairs.
  uint64_t gtl_const_compared = 0;
  uint64_t gtl_const_diverged = 0;
  // Viewport/scissor flip gate: packet raw guest structs (decoded at the
  // SetViewport/SetScissorRect calls) vs the observer command's snapshots.
  uint64_t gtl_viewport_compared = 0;
  uint64_t gtl_viewport_diverged = 0;
  uint64_t gtl_scissor_compared = 0;
  uint64_t gtl_scissor_diverged = 0;
  // §3.4 completeness: draw events synthesized from unmatched packets.
  // Executor identity authority: per-generation consumed flags (parallel to
  // the shadow matcher's, so the metric stays independent) + counters.
  std::vector<bool> gtl_authority_consumed;
  std::vector<bool> gtl_authority_consumed_prev;
  uint64_t gtl_authority_applied = 0;
  // Matched commands whose float-constant banks were replaced by the
  // packet's at-dispatch banks (sanitized). const_cmp in the exec shadow
  // measures ~0 by construction once this is live — its verification job
  // ended with run 092.
  uint64_t gtl_constants_authority_applied = 0;
  // Draws whose cull state came from the LIVE device control packet, and the
  // subset where that disagreed with the render-state mirror it replaced. A
  // large `diverged` is the direct measure of how wrong the mirror was — see
  // the live-cull authority block in BuildDirectCommandFromGtlDrawPacketLocked.
  uint64_t gtl_live_cull_applied = 0;
  uint64_t gtl_live_cull_diverged = 0;
  // Texture slots rewritten from a raw fetch BASE to the guest OBJECT that owns
  // it, so the sampling resolver's single exact route can answer them.
  uint64_t gtl_texture_base_normalised = 0;
  // (gtl_submission_eligible / gtl_submission_paired removed 2026-07-26 with
  // ReorderDirectDrawCommandsToPacketOrderLocked, their only reader/writer.)
  // Paired commands whose inline geometry was replaced by the packet's
  // at-enter pinned copy (8220A3F0 family).
  uint64_t gtl_geometry_authority_applied = 0;
  // Order flip gate (§3.4 "packets execute in order"): packet_ids of
  // current-generation matches in the order the direct pipeline recorded
  // their draws. packet_id is strictly monotonic at dispatch, so a sorted
  // sequence proves execution already follows guest draw order; inversions
  // localize the reordering the executor cutover must impose.
  std::vector<uint64_t> gtl_order_sequence;
  uint64_t gtl_order_frames = 0;
  uint64_t gtl_order_draws = 0;
  uint64_t gtl_order_inversions = 0;
  uint64_t gtl_order_disordered_frames = 0;
  // Draw commands moved into packet order before recording (step-5
  // ordering cutover); the order gate above measures the EXECUTED order,
  // so cum_inversions should pin at 0 once this is live.
#endif

  // Log-cap counters ONLY. Every field here exists to bound a `if (n < N)`
  // guard; none of them influences a rendering decision. Grouping them makes
  // the reset a single `= {}` and takes 104 of this struct's ~200 fields out
  // of the way of reading the state that actually matters.
  struct DirectDiagnosticsState {
    bool logged_first_frame = false;
    bool logged_first_draw = false;
    bool logged_missing_draw_resource = false;
    uint32_t logged_failed_direct_draws = 0;
    uint32_t logged_final_failed_direct_commands = 0;
    uint32_t logged_pipeline_not_ready_details = 0;
    uint32_t logged_effective_pipeline_states = 0;
    uint32_t logged_pix_video_draw_bindings = 0;
    uint32_t logged_skipped_pix_video_missing_planes = 0;
    uint32_t logged_skipped_bink_wrapper_missing_planes = 0;
    uint32_t logged_private_rect_video_draw_bindings = 0;
    // Draws whose viewport had to be clamped to the bound attachment at submit
    // time. With one scaler and the guest-target clamp upstream this should
    // trend to 0; anything left is a path that reaches the executor without
    // going through ScaleFableTitleDrawStateToFramebuffer.
    uint64_t viewport_clamped_to_attachment = 0;
    uint32_t logged_private_rect_copy_draw_bindings = 0;
    uint32_t logged_private_rect_unbacked_samples = 0;
    uint32_t logged_private_rect_produced_aliases = 0;
    uint32_t logged_private_rect_produced_alias_misses = 0;
    uint32_t logged_private_rect_backing_aliases = 0;
    uint32_t logged_private_rect_registered_produced_aliases = 0;
    uint32_t logged_private_rect_wrapper_target_materializations = 0;
    uint32_t logged_private_rect_cross_slot_action_matches = 0;
    uint32_t logged_private_rect_blank_upload_samples = 0;
    uint32_t logged_private_rect_sibling_alias_hits = 0;
    uint32_t logged_private_rect_exact_alias_index_remembers = 0;
    uint32_t logged_private_rect_preserved_upload_aliases = 0;
    uint32_t logged_private_rect_identity_bucket_choices = 0;
    uint32_t logged_private_rect_shape_mismatch_rejects = 0;
    uint32_t logged_private_rect_surface_texture_ownership_proofs = 0;
    uint32_t logged_private_rect_exact_destination_link_sources = 0;
    uint32_t logged_private_chain_surface_links = 0;
    uint32_t logged_unresolved_registered_private_rect_rejects = 0;
    uint32_t logged_private_rect_exact_identity_aliases = 0;
    uint32_t logged_synthetic_private_rect_targets = 0;
    uint32_t logged_setup_clear_private_rect_targets = 0;
    uint32_t logged_materialized_private_rect_copy_actions = 0;
    uint32_t logged_title_scene_draws = 0;
    uint32_t logged_title_scene_sample_ownership_proofs = 0;
    uint32_t logged_title_texture_binding_probes = 0;
    uint32_t logged_title_scene_clears = 0;
    uint32_t logged_title_scene_implicit_clears = 0;
    uint32_t logged_bink_command_order = 0;
    uint32_t logged_command_source_summaries = 0;
    uint32_t logged_command_record_summaries = 0;
    uint32_t logged_private_rect_clear_action_ownership_proofs = 0;
    uint32_t logged_private_rect_setup_clear_intents = 0;
    uint32_t logged_private_rect_setup_clear_backfills = 0;
    uint32_t logged_private_rect_setup_clear_surface_links = 0;
    uint32_t logged_exact_title_gui_atlas_missing_upload_rejects = 0;
    uint32_t logged_exact_current_producer_owner_cache_hits = 0;
    uint32_t logged_begin_vertices_title_action_cache_hits = 0;
    uint32_t logged_direct_input_ready_cache_hits = 0;
    uint32_t logged_private_rect_feedback_registered_reuses = 0;
    uint32_t logged_private_rect_materialized_edge_reuse_rejections = 0;
    uint32_t logged_current_title_color_feedback_materialized_edges = 0;
    uint32_t logged_private_rect_materialized_current_link_rejections = 0;
    uint32_t logged_private_rect_feedback_stale_backings = 0;
    uint32_t logged_primitive8_expansions = 0;
    uint32_t logged_scene_clear_invalidations = 0;
    uint32_t logged_fable_scene_targets = 0;
    uint32_t logged_undersized_vertex_buffers = 0;
    uint32_t logged_index_endian_repairs = 0;
    uint32_t logged_resource_upload_syncs = 0;
    uint32_t logged_resource_sync_failures = 0;
    uint32_t logged_observed_shader_identity_hits = 0;
    uint32_t logged_observed_declaration_identity_hits = 0;
    std::unordered_set<std::string> logged_pipeline_not_ready_keys;
    std::unordered_set<uint64_t> logged_shader_sync_hashes;
    std::unordered_set<uint64_t> logged_shader_sync_failures;
    bool logged_first_scene_framebuffer = false;
    bool logged_first_present_copy = false;
    bool logged_missing_present_source = false;
    uint32_t logged_present_source_changes = 0;
    uint32_t logged_present_source_details = 0;
    bool logged_first_resolve_alias = false;
    uint32_t logged_resolve_copy_failures = 0;
    uint32_t logged_surface_alias_hazard_copies = 0;
    uint32_t logged_surface_alias_hazard_copy_owners = 0;
    uint32_t logged_private_rect_feedback_events = 0;
    uint32_t logged_private_rect_feedback_unresolved = 0;
    uint32_t logged_private_rect_exact_source_misses = 0;
    uint32_t logged_private_rect_feedback_exact_source_holds = 0;
    uint32_t logged_private_rect_copy_action_exact_source_holds = 0;
    uint32_t logged_private_rect_registered_produced_exact_source_holds = 0;
    uint32_t logged_private_rect_mutable_alias_holds = 0;
    uint32_t logged_private_rect_feedback_action_backings = 0;
    uint32_t logged_private_rect_feedback_timing_breakdowns = 0;
    uint32_t logged_private_rect_feedback_alias_prunes = 0;
    uint32_t logged_private_rect_current_target_owners = 0;
    uint32_t logged_private_rect_static_context_rejects = 0;
    uint32_t logged_private_texture_header_materializations = 0;
    uint32_t logged_private_texture_header_timing_breakdowns = 0;
    uint32_t logged_private_rect_action_preflights = 0;
    uint32_t logged_private_rect_preflight_timing_breakdowns = 0;
    uint32_t logged_private_copy_action_timing_breakdowns = 0;
    uint32_t logged_private_action_backing_timing_breakdowns = 0;
    uint32_t logged_private_action_backing_miss_cache_hits = 0;
    uint32_t logged_private_action_exact_gpu_surface_no_backing = 0;
    uint32_t logged_private_action_current_gpu_surface_sources = 0;
    uint32_t logged_title_exact_producer_sync_cache_hits = 0;
    uint32_t logged_scaled_private_rect_texture_identities = 0;
    uint32_t logged_title_private_producer_waits = 0;
    uint32_t logged_exact_title_static_scene_producer_waits = 0;
    uint32_t logged_preserved_zero_shape_snapshots = 0;
    uint32_t logged_surface_shape_retires = 0;
    uint32_t logged_surface_texture_creates = 0;
    uint32_t logged_surface_content_carries = 0;
  };
  DirectDiagnosticsState diagnostics;

};

enum class DirectAddressGate : uint32_t {
  kPrimitive8CopyTargetDeclaration,
  kTitleScenePrivateTargetSceneExclusion,
  kScenePrivateCopyPixelShader,
  kTextureSlotMaskSlot0PixelShader,
  kTitleGuiAtlasHeapPixelShader,
  kBillboardTileHeaderAliasObject,
  kDeclarationFallbackGuiQuad,
  kDeclarationFallbackPrivateRect,
  kDeclarationFallbackTitleIndexed,
  kDeclarationFallbackRepeatedMesh,
  kDeclarationFallbackTitleBillboardMesh,
  kBillboardAtlasAliasRegistration,
  kGuiTextAtlasAliasRegistration,
  kTinyTitle16x16AliasRegistration,
  kOneDimensionalTitleAliasRegistration,
  kBillboardTileAliasRegistration,
  kBillboardAtlasSnapshotKnownObject,
  kTinyTitle16x16UploadFallback,
  kFinalTitleIndirectionPixelShader,
  kFinalTitleIndirectionVertexShader,
  kFinalTitleIndirectionRenderTarget,
  kBillboardCommandHeaderAddress,
  kGeneratedSingleSlot0PixelShader,
  kTitleTextureBindingProbeAtlas,
  kFinalTitleTargetDiagnostic,
  kTitleSceneDeclarationDiagnostic,
  kTitleBillboardPayloadDiagnostic,
  kPreBindEntryProbePixelShader,
  kBillboardAtlasImageReady,
  kTitleUploadInterestSeed,
  kExactTitleUpload1x1Alias,
  kExactTitleUpload1DAlias,
  kExactTitleUpload16x16Alias,
  kTreeBillboardHeapTexture13,
  kTreeBillboardProbeHeapTexture13,
  kCount
};

struct DirectAddressGateDescriptor {
  const char* name;
  const char* literals;
  const char* gates;
};

constexpr size_t kDirectAddressGateCount =
    static_cast<size_t>(DirectAddressGate::kCount);

bool DirectDebugLabelsEnabled();

struct DirectAddressGateCensusSlot {
  std::atomic<uint64_t> tested{0};
  std::atomic<uint64_t> matched{0};
  std::atomic<uint64_t> derived_covered{0};
  std::atomic<uint64_t> literal_only{0};
  std::atomic<uint64_t> observed_key{0};
  std::atomic<uint32_t> observed_address{0};
  std::atomic<uint32_t> last_match_frame{0};
};

class PlumeDirectDebugLabelScope {
 public:
  PlumeDirectDebugLabelScope(plume::RenderCommandList* command_list,
                             const std::string& label)
      : command_list_(command_list),
        active_(command_list != nullptr && DirectDebugLabelsEnabled()) {
    if (active_) {
      command_list_->beginDebugLabel(label);
    }
  }

  ~PlumeDirectDebugLabelScope() {
    End();
  }

  PlumeDirectDebugLabelScope(const PlumeDirectDebugLabelScope&) = delete;
  PlumeDirectDebugLabelScope& operator=(const PlumeDirectDebugLabelScope&) =
      delete;

  void End() {
    if (!active_) {
      return;
    }
    command_list_->endDebugLabel();
    active_ = false;
  }

 private:
  plume::RenderCommandList* command_list_ = nullptr;
  bool active_ = false;
};

struct DirectResourceSyncBreakdown {
  std::array<double, 9> kind_ms = {};
  std::array<uint32_t, 9> kind_calls = {};
  double image_shape_ms = 0.0;
  double image_metadata_ms = 0.0;
  double image_create_ms = 0.0;
  double image_view_ms = 0.0;
  double image_upload_ms = 0.0;
  double snapshot_classify_ms = 0.0;
  double tracked_sync_ms = 0.0;
  double post_sync_ms = 0.0;
  double deferred_snapshot_ms = 0.0;
  double resource_lookup_ms = 0.0;
  double texture_identity_ms = 0.0;
  double shader_decl_identity_ms = 0.0;
  uint32_t image_upload_calls = 0;
  uint32_t deferred_snapshot_calls = 0;
  uint32_t texture_identity_calls = 0;
  uint32_t texture_identity_skips = 0;
  uint32_t shader_decl_identity_calls = 0;
  uint32_t resources_size = 0;
};

struct DirectPresentSource {
  plume::RenderTexture* texture = nullptr;
  plume::RenderTextureView* view = nullptr;
  plume::RenderTextureLayout layout = plume::RenderTextureLayout::SHADER_READ;
  plume::RenderFormat format = plume::RenderFormat::UNKNOWN;
  const char* reason = "none";
  uint32_t target = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t texture_width = 0;
  uint32_t texture_height = 0;
  uint64_t write_serial = 0;
  uint32_t frame_index = 0;
};

enum class DirectFrameDatabaseDrawPacketProducerKind {
  kPresentColor,
  kTitlePresent,
  kFullScene,
  kTitleVisible,
};

struct GtlResolveHostCopyStats {
  uint32_t recorded = 0;
  uint32_t no_source_resource = 0;
  uint32_t no_dest_resource = 0;
  uint32_t not_image = 0;
  uint32_t not_ready = 0;
  uint32_t same_texture = 0;
  uint32_t format_mismatch = 0;
  // The copy box does not fit inside the destination (host RT surfaces are
  // promoted to output extent while destinations keep guest dims, so e.g. a
  // full 480x270 source into a 280x180 dest is common). An unclipped
  // vkCmdCopyImage writes out of bounds and WEDGES THE DEVICE (run 489: the
  // first such copy stopped all presents). The conversion draw owns these —
  // its viewport/scissor clip correctly.
  uint32_t extent_mismatch = 0;
  uint32_t empty_rect = 0;
  // Format-converting resolve DRAW (the fmt_mismatch majority): per-cause
  // split for the draw path the copy cannot express.
  uint32_t draw_recorded = 0;
  uint32_t draw_depth_deferred = 0;
  uint32_t draw_no_framebuffer = 0;
  uint32_t draw_no_pipeline = 0;
};

struct GtlVideoMergeStats {
  uint64_t video_matched = 0;   // observer video commands GTL-sourced in place
  uint64_t video_inserted = 0;  // dropped compose packets inserted at merge tail
  uint64_t insert_candidates = 0;  // unconsumed 0x8220A3F0 packets examined
  uint64_t insert_no_video_ps = 0;
  uint64_t insert_no_geometry = 0;
};

struct GtlClearMergeStats {
  // kClear commands whose root is NOT one of the two D3D clear wrappers —
  // the "synthetic clear" share the run-379 collapse was blamed on. Zero at
  // title would mean the observer clear stream is entirely root-produced and
  // full clear ownership only needs the packet stream + resolve clears.
  uint64_t other_root = 0;
  // Resolve-source clears emitted from ResolvePacket flags 0x100/0x200.
  uint64_t resolve_clears = 0;
  // Authoritative mode: ClearPackets emitted at packet_id positions, and
  // observer kClear commands dropped as redundant.
  uint64_t authoritative_emitted = 0;
  uint64_t observer_dropped = 0;
  // Predicated ClearF calls (predication_mask != 0) skipped rather than
  // submitted as unconditional full-target clears (2026-07-25) — see
  // refii-predicated-clear-white-screen.md.
  uint64_t predicated_skipped = 0;
};

struct GtlResolveEndpoints {
  PlumeTrackedResource* source = nullptr;
  PlumeTrackedResource* destination = nullptr;
};

struct DirectSurfaceDestinationSplit {
  uint32_t calls = 0;
  double snapshot_ms = 0.0;  // registry relation snapshot build
  double interest_ms = 0.0;  // first interest pass over relations
  double sync_ms = 0.0;      // non-blocking sync of relation interests
  double catchup_ms = 0.0;   // missing-endpoint pass + BLOCKING observer sync
  double record_ms = 0.0;    // copy recording pass
};

struct DirectSurfaceDestinationScanPlan {
  // Relation generation the plan was built against; buckets are marked
  // attempted at this value.
  uint64_t generation = 0;
  // Destination buckets (relation destination_texture keys) matched for the
  // draw AND not yet visited at `generation` this frame.
  std::vector<uint32_t> buckets;
  // Index-matched buckets before the attempt-mark filter (census relevant=).
  uint32_t relevant = 0;
  // Bounded fallback: the relation population is small enough that the old
  // global walk is cheaper and provably exact — targeting is skipped and the
  // run behaves exactly as before this index existed.
  bool global = false;
};

struct DirectResolveCopyRecordResult {
  bool handled;
  bool copied;
};

struct DirectResolveCopySourceSelection {
  uint32_t source_slot = 0;
  uint32_t source_address = 0;
  bool depth_resolve = false;
  PlumeTrackedResource* source = nullptr;
  bool ready = false;
};

struct DirectResourceLookupCensus {
  uint32_t calls = 0;
  uint32_t fast_hits = 0;
  uint32_t map_hits = 0;
  uint32_t memo_hits = 0;
  uint32_t scans = 0;
  uint32_t scan_hits = 0;
  uint32_t negative_hits = 0;
  uint64_t entries_scanned = 0;
  // Alias-ambiguity sampling: see kDirectAliasAmbiguitySamplesPerFrame.
  uint32_t ambiguity_samples = 0;
  uint32_t ambiguous_scans = 0;
  uint32_t last_ambiguous_address = 0;
};

struct DirectFindCumulativeCensus {
  uint64_t calls = 0;
  uint64_t pos_hits = 0;  // fast + map + memo hits
  uint64_t neg_hits = 0;
  uint64_t scans = 0;
  uint64_t scan_entries = 0;
  uint64_t key_invalidations = 0;
};

struct DirectDrawPhaseAccumulator {
  // Caller-side per-command cost BEFORE RecordDirectDrawCommandLocked's own
  // clock starts: cached-scratch attach and (observer commands only) GTL
  // identity authority. The per-command deep copy into a replay scratch that
  // used to dominate this bucket (run 486: 1534ms/2375 commands at the menu)
  // was deleted 2026-08-01 — the record now normalizes the batch's own
  // commands in place.
  double copy_ms = 0.0;
  // Full-command copy/relocation census (copies=/copy_kb= in the drawphase
  // line): every ~12.6KB FableDirectRenderCommand copied, moved or relocated
  // by the merge builds and the batch strip compaction. A regression that
  // reintroduces per-command copies or an erase/remove_if memmove storm
  // shows up here next to the copy= ms it costs.
  uint64_t copy_count = 0;
  uint64_t copy_bytes = 0;
  double preamble_ms = 0.0;    // entry -> video/plane sync
  double title_sync_ms = 0.0;  // video/plane sync -> pipeline lookup
  double pipeline_ms = 0.0;    // EnsurePipelineForDirectDrawLocked
  double interior_ms = 0.0;    // pipeline -> input/stream binding
  double alias_ms = 0.0;       // input binding -> alias/surface-destination
  double submit_ms = 0.0;      // framebuffer+constants+descriptors+draw call
  // alias_ms subdivided. It is the dominant phase in the menu (7-65ms of a
  // 63-150ms compose for only ~164 draws) and it SWINGS by 9x between
  // consecutive frames with identical draw counts, so the cost is an
  // intermittent slow path, not a uniform per-draw overhead. These four say
  // which one. The surface-destination scan was measured separately at 0-2ms
  // with only ~50 relations, so it is already excluded as the cause.
  double alias_feedback_ms = 0.0;   // private-rect feedback slot ensure loops
  double alias_proxy_ms = 0.0;      // ResolveUnbackedTitlePrivateProxyCopy
  double alias_hazard_ms = 0.0;     // RecordDirectSurfaceAliasHazardCopies
  double alias_destination_ms = 0.0;  // surface-destination relation scan
  // Destination-scan generation-gate census (aliassplit destscan=runs/skips
  // scanned=/relevant=): runs = a scan executed (generation change or a
  // per-bucket catch-up for a bucket earlier targeted runs never visited),
  // skips = the generation gate plus the bucket attempt marks proved the scan
  // a no-op. Run 479's burst was runs==draws; healthy is a handful of runs
  // per compose with skips carrying the rest.
  uint32_t alias_destination_scan_runs = 0;
  uint32_t alias_destination_scan_skips = 0;
  // Targeted-run volume census: scanned = relations actually visited across
  // this frame's per-draw runs (a global-fallback run counts its whole
  // snapshot), relevant = index-matched destination buckets those runs were
  // planned against. Run 486's cost was 16 runs x ~1478-relation population;
  // the win is scanned collapsing to ~relevant instead of runs x population.
  uint32_t alias_destination_scan_scanned = 0;
  uint32_t alias_destination_scan_relevant = 0;
  // Feedback-ensure memo census (aliassplit fbmemo=hits/runs): hits = the
  // ensure/prewarm body was skipped because the generation snapshot matched,
  // runs = the full body executed (miss or stale). Covers BOTH call sites
  // (the title_sync prewarm and the alias_feedback ensure). Healthy is runs
  // on the first draw touching a (key, slot) and hits carrying the rest of
  // the compose.
  uint32_t alias_feedback_memo_hits = 0;
  uint32_t alias_feedback_memo_runs = 0;
  // interior_ms subdivided (run 454: interior=70us/draw with the two binds
  // measured jointly at ~48us — these five name the remainder and split the
  // binds from the pre-bind repair loops).
  double interior_reval_ms = 0.0;   // sample-time CPU-texture revalidation
  double interior_streams_ms = 0.0; // ring-inline/scratch stream refresh loop
  double interior_vbind_ms = 0.0;   // BindDirectVertexInputLocked (1st try)
  double interior_ibind_ms = 0.0;   // BindDirectIndexBufferLocked (1st try)
  double interior_retry_ms = 0.0;   // failed-bind sync + rebind path
  // The missing-pipeline recovery (interest collect + sync + re-ensure)
  // between the pipeline lookup and the interior repair loops — previously
  // the unnamed remainder of interior_ms.
  double interior_pipefix_ms = 0.0;
  // submit_ms subdivided (run 454: submit=92us/draw, the largest phase, with
  // no single obvious owner — these name it). Accumulated only for draws that
  // reach the draw call, matching submit_ms itself.
  double submit_fb_ms = 0.0;     // fb transition + setFramebuffer + title init
  double submit_tex_ms = 0.0;    // sampled-texture resolve maze + barriers
  double submit_const_ms = 0.0;  // pipeline layout + constants build/upload
  double submit_bind_ms = 0.0;   // descriptor sets + setPipeline
  double submit_log_ms = 0.0;    // per-draw log gates (video/private/title)
  double submit_vp_ms = 0.0;     // viewport/scissor + quad-list bind + diag
  double submit_label_ms = 0.0;  // pass classify + debug label build/begin
  double submit_draw_ms = 0.0;   // the draw call + label end
  double submit_post_ms = 0.0;   // post-draw target/video bookkeeping
  // Float-bank upload dedup census (submitsplit const_reuse=/const_uploads=):
  // reuse = a 4 KiB vertex/pixel float bank matched the previous upload's
  // source content and rebound the same arena allocation; uploads = the bank
  // was copied into the arena. Counted per BANK (two per draw), so
  // reuse+uploads ~= 2x recorded draws. Healthy is pixel-bank reuse near the
  // draw count (material batches repeat the pixel bank) with vertex-bank
  // reuse lower (c0-c3 carries the per-object WVP).
  uint32_t submit_const_bank_reuses = 0;
  uint32_t submit_const_bank_uploads = 0;
  // title_sync_ms subdivided (syncsplit): the four blocks between the
  // video-plane sync and the pipeline lookup.
  double sync_flush_ms = 0.0;    // FlushExactSurfaceCopyForSlotLocked loop
  double sync_prewarm_ms = 0.0;  // feedback prewarm slot passes
  double sync_header_ms = 0.0;   // EnsureMissingDirectPrivateTextureHeader...
  double sync_bink_ms = 0.0;     // Bink wrapper plane-set readiness check
  // BindDirectVertexInputLocked subdivided (vbindsplit). Accumulated inside
  // the function, so retry-path rebinds count here while interior_vbind_ms
  // covers only the first attempt; failure exits drop the unfinished phase
  // (same convention as submit_ms).
  double vbind_layout_ms = 0.0;  // declaration lookup + input-layout rebuild
  double vbind_ready_ms = 0.0;   // per-slot readiness pass
  double vbind_upload_ms = 0.0;  // sort + view build incl. inline-VB cache
  double vbind_setvb_ms = 0.0;   // setVertexBuffers runs + upload retain
  // Inline-VB stored-hash census (vbindsplit upload_hits=/upload_misses=):
  // hits = the bind used the content hash stamped at the payload's write site
  // (FableDirectInlineBuffer::content_hash), misses = the payload arrived
  // unstamped (content_hash == 0) and the bind hashed it lazily. Healthy is
  // ~all hits; sustained misses mean a payload writer bypasses the stamping
  // paths (StoreInlineScratchBuffer / GTL authority fills / packet
  // round-trip).
  uint32_t vbind_upload_stored_hash = 0;
  uint32_t vbind_upload_lazy_hash = 0;
  // Record-loop caller-side cost outside RecordDirectDrawCommandLocked
  // (recordsplit) — the blind spot between copy_ms and record_ms.
  double record_gate_ms = 0.0;    // copy end -> the draw record call
  double record_latch_ms = 0.0;   // draw return -> record_command exit
  double record_clear_ms = 0.0;   // RecordDirectClearCommand dispatch
  // Clear framebuffer memo census (recordsplit clearmemo=hits/runs): hits =
  // Ensure*+Resolve skipped via the per-compose memo
  // (DirectClearFramebufferMemoEntry), runs = the full resolution executed
  // (first clear of a config this compose, generation change, or a
  // non-memoizable private-rect copy/depth-shader clear).
  uint32_t record_clear_memo_hits = 0;
  uint32_t record_clear_memo_runs = 0;
  // RecordDirectClearCommand subdivided (clearsplit). ensure/resolve/sync run
  // only on memo misses; vk (transition + setFramebuffer + clear calls) is
  // paid by every recorded clear, hits included.
  double clear_ensure_ms = 0.0;   // EnsurePrivateRectSetupClearSurfacesLocked
  double clear_resolve_ms = 0.0;  // first ResolveDirectFramebufferLocked
  double clear_sync_ms = 0.0;     // failed-resolve sync retry incl. re-resolve
  double clear_vk_ms = 0.0;       // transition/setFramebuffer/clear* calls
  // Failed-resolve retry census (clearsplit retry=runs/skips/invalidated):
  // runs = the sync retry executed, skips = the per-key negative memo
  // suppressed it, invalidated = an entry existed but its record-presence
  // pattern changed so it was erased and the retry re-ran. Run 494's burst
  // was sync=2,704ms with the old global-counter memo never holding; run
  // 495 (retry=32/0) showed framebuffer_invalidation_generation churning
  // identically. Healthy is runs only on first failures, skips carrying the
  // rest, invalidated near zero.
  uint32_t clear_retry_runs = 0;
  uint32_t clear_retry_skips = 0;
  uint32_t clear_retry_invalidated = 0;
  // Pre-record clear coalescing (see the pass above strip_state_only_commands
  // in RecordDirectRenderCommandsLocked): duplicate clears dropped because a
  // later clear with an identical semantic key obliterates them. wipes =
  // full pending-map wipes (packet-less kGtlResolve only); evictions =
  // per-target pending-entry erasures at interposing actions.
  uint32_t record_clears_coalesced = 0;
  uint32_t record_coalesce_wipes = 0;
  uint32_t record_coalesce_evictions = 0;
  double record_resolve_ms = 0.0; // GTL resolve live-execution block
  // record_resolve_ms subdivided (resolvesplit) — run 513 measured
  // resolve=8-15ms per settled-title compose for a CONSTANT 17 resolves
  // (~610us each) with no attribution below the block timer. find covers the
  // once-per-execute endpoint resolution (source exact-object + hybrid dest,
  // shared by classifier and draw path since the 2026-08-02 one-pass peel),
  // classify the host-copy buildability ladder, fb the destination
  // framebuffer/view ensure, draw the descriptor/barrier/state/draw tail.
  double resolve_find_ms = 0.0;
  double resolve_classify_ms = 0.0;
  double resolve_fb_ms = 0.0;
  double resolve_draw_ms = 0.0;
  uint32_t resolve_execs = 0;
  // Pipeline ensure memo census (pipememo=hits/misses/bypass): hits = the
  // full layout rebuild + shader-state snapshot + DirectPipelineKey build +
  // string-keyed map find were all skipped for a previously proven
  // (content-key -> pipeline) pair; misses = full path ran and stored;
  // bypass = command shape excluded from memoization (tracked-buffer stride
  // fallback possible), full path ran without storing.
  uint32_t pipeline_memo_hits = 0;
  uint32_t pipeline_memo_misses = 0;
  uint32_t pipeline_memo_bypass = 0;
  // Synthetic private-rect vertex upload cache (rectvb=hits/creates): hits =
  // the 288-byte regenerated corner quad came from the content-keyed upload
  // cache; creates = a fresh device upload buffer was built (previously EVERY
  // regen draw paid a createBuffer+map+copy per compose).
  uint32_t rect_vb_cache_hits = 0;
  uint32_t rect_vb_cache_creates = 0;
  double record_label_ms = 0.0;   // pass scopes + step-label build/end
  double record_post_ms = 0.0;    // record_commands return -> record_t1
  // Gap triangulation (run 493: record_ms=139.8 with all buckets summing to
  // ~62 — ~78ms was outside every bucket). These three tile the remainder
  // without guessing:
  //   draw  = the FULL RecordDirectDrawCommandLocked call interval, success
  //           and failure alike (existing timestamps, zero new clock reads).
  //           draw - (pre+sync+pipe+interior+alias+submit) is the cost of
  //           early-exit paths, which lose everything since their last
  //           top-level accumulation (preamble does not accumulate until
  //           AFTER the framebuffer resolve + plane-ready ensures, so a draw
  //           failing or plane-skipping in there books NOTHING today).
  //   state = record_command for commands that are not draw/clear/resolve
  //           (the ~100+ kSet*/other commands of a 511-command menu batch).
  //   iter  = the whole record-loop body per command. record_ms - iter - post
  //           is the outside-loop residue (batch label + scope only).
  double record_draw_ms = 0.0;
  double record_state_ms = 0.0;
  double record_iter_ms = 0.0;
};

struct DirectResourceFastCacheEntry {
  uint32_t address = 0;
  PlumeTrackedResource* resource = nullptr;
};

struct ResolveDestLookupStats {
  uint32_t candidates = 0;
  uint32_t no_identity_record = 0;
  uint32_t no_resource = 0;
  uint32_t not_image = 0;
  uint32_t not_ready = 0;
  uint32_t fetch_mismatch = 0;
  uint32_t matched = 0;
};

struct GtlTessDrawStats {
  uint64_t execs = 0;            // draws actually recorded
  uint64_t patches = 0;          // instances summed over execs
  uint64_t n2 = 0;               // execs at N=2 (terrain per-edge default)
  uint64_t n16 = 0;              // execs at N=16 (water continuous 15+1)
  uint64_t n_other = 0;          // execs at any other decoded N
  uint64_t vs_tess_miss = 0;     // tess-variant artifact missing (LOUD, R4)
  uint64_t pred_skips = 0;       // predicated submissions skipped (T4-lite)
  uint64_t zero_patches = 0;     // decode produced 0 patches; skipped
  uint64_t no_framebuffer = 0;   // framebuffer resolution failed
  uint64_t no_pipeline = 0;      // pipeline/PS/tess-VS not ready
  uint64_t no_constants = 0;     // constant arena bind failed
  uint64_t no_index_buffer = 0;  // grid IB creation failed
};

struct DirectTextureDebugBinding {
  uint32_t slot = UINT32_MAX;
  uint32_t guest = 0;
  uint32_t tracked = 0;
  uint32_t parent = 0;
  uint32_t base = 0;
  uint32_t mip = 0;
  uint32_t pitch = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint32_t guest_format = 0;
  uint32_t dimension = 0;
  uint64_t content_serial = 0;
  uint64_t generation = 0;
  uint64_t upload_size = 0;
  uint64_t hash = 0;
  uint32_t first_nonzero = UINT32_MAX;
  uint32_t sample_size = 0;
  std::array<uint8_t, 4> sample = {};
  bool ready = false;
  bool aliased = false;
  bool upload_source = false;
  bool upload_decoded = false;
};

struct DirectCommandInputWaitReason {
  const char* reason = "ready";
  uint32_t slot = UINT32_MAX;
  uint32_t guest = 0;
  uint32_t direct = 0;
  uint32_t sampled = 0;
};

struct ExactTitlePreflightBackoff {
  uint32_t next_retry_frame = 0;
  uint32_t delay_frames = 0;
};

struct DirectTitleVisibleFramePacketProbe {
  bool packet_seen = false;
  bool candidate = false;
  FableTitleFramePacketDerivedFacts derived;
  uint32_t rt0 = 0;
};



using DirectTextureFetchInfo = FableTextureFetchInfo;

using FableDirectClock = std::chrono::steady_clock;

double FableDirectElapsedMs(FableDirectClock::time_point begin,
                            FableDirectClock::time_point end);

inline constexpr uint64_t kFablePostprocessRadialBlurPixelHash =
    kFablePostprocessingGaussBlurPixelHash;  // 0170_pshader_..._radialblur
inline constexpr uint64_t kFablePostprocessDownscale2PixelHash =
    kFableTitleScreenFacingAtlasSpritePixelHash;  // 0160_..._downscale2
inline constexpr uint64_t kFablePostprocessDownscale4SiblingPixelHash =
    kFableScreenFacingSpritePixelHash;  // 0161_..._downscale4
constexpr std::array<DirectAddressGateDescriptor, kDirectAddressGateCount>
    kDirectAddressGateDescriptors = {{
        // NOTE: seven slots below read "[DELETED 2026-07-26 ...]". Their call
        // sites were removed because run 288's census[final] (frame=2952) put
        // them in tested_but_never_matched. The enum/descriptor entries are
        // retained so census slot indices stay stable across log comparisons;
        // they will now report as never_tested, which for THESE seven means
        // "no call site exists" and NOT "unreachable on title content".
        {"primitive8_copy_target_decl", "[DELETED 2026-07-26: never matched]",
         "primitive8 rectangle copy target materialization"},
        {"title_scene_private_target_scene_exclusion",
         "[DELETED 2026-07-26: never matched]",
         "title-scene private target materialization"},
        // The two entries below are INVERTED census calls (2026-07-26 Phase 2):
        // the derived bytecode-hash test decides, and the retired address list
        // is reported in the `derived` column. matched must hold at its run-288
        // baseline (2304 / 40245); lit_only is now the hash-only widening, not
        // an "undeducible" signal.
        {"scene_private_copy_ps",
         "DERIVED(radialblur/downscale2/downscale4 ps hashes; retired "
         "ps=0x4215A710/0x4216D090/0x42220410)",
         "remembers drawn scene private-texture copy actions"},
        {"texture_slot_mask_slot0_ps",
         "DERIVED(radialblur/downscale2 ps hashes; retired "
         "ps=0x4215A710/0x4216D090)",
         "collapses the pixel texture slot mask to slot 0"},
        {"title_gui_atlas_heap_ps", "DERIVED(overdraw_visualise_ps_hash)",
         "title GUI-text atlas slot-13 mask"},
        {"billboard_tile_header_alias_object",
         "obj=0x4C132C70/CA4/CD8/0x4C133150/3184",
         "billboard tile object alias acceptance for canonical base"},
        // The five decl_fallback gates were DE-LITERALISED 2026-08-02: the
        // guest scratch addresses were dropped (never_tested across every
        // default-census run AND resolution-unsafe — scratch layout shifts
        // with the native-resolution override); the elements hash decides.
        {"decl_fallback_gui_quad",
         "DERIVED(gui-quad/single-stream-quad decl hashes; retired "
         "decl=0x40070190/01D0/0x40073780/0x400715D0/0x40021CA0)",
         "GUI-quad declaration decode fallback (1 element)"},
        {"decl_fallback_private_rect",
         "DERIVED(private-rect decl hash; retired decl=0x400216B0)",
         "private-rect declaration decode fallback (2 elements)"},
        {"decl_fallback_title_indexed",
         "DERIVED(title-indexed decl hash; retired decl=0x40021910)",
         "title indexed declaration decode fallback (8 elements)"},
        {"decl_fallback_repeated_mesh",
         "DERIVED(repeated-mesh decl hash; retired decl=0x40021D00)",
         "repeated-mesh declaration decode fallback (3 elements)"},
        {"decl_fallback_title_billboard_mesh",
         "DERIVED(title-billboard-mesh decl hash; retired decl=0x40023D20)",
         "title billboard-mesh declaration decode fallback (4 elements)"},
        {"billboard_atlas_alias_registration",
         "alias=0x4C132C70/CA4/CD8/0x4C133150/3184",
         "canonical billboard atlas identity aliases"},
        {"gui_text_atlas_alias_registration",
         "alias=0x4051D610/0x4051F650/0x4051F610/0x4051FF90",
         "gui-text 512x256 identity aliases"},
        {"tiny_title_16x16_alias_registration",
         "alias=0x404DE490/4D0/550",
         "16x16 tiny-title identity aliases (base 0x1A0D7000)"},
        {"one_dimensional_title_alias_registration",
         "alias=0x401317D0/810/850/890/8D0/910",
         "1-D gradient identity aliases (bases 0x1FAB0000..5000)"},
        {"billboard_tile_alias_registration",
         "alias=0x44127210..0x4412751C",
         "billboard tile identity aliases (bases 0x13520000..0x13590000)"},
        {"billboard_atlas_snapshot_known_object",
         "[DELETED 2026-07-26: never matched]",
         "classifies a guest snapshot as the title billboard atlas"},
        {"tiny_title_16x16_upload_fallback",
         "[DELETED 2026-07-26: never matched]",
         "last-resort 16x16 tiny-upload bind source"},
        {"final_title_indirection_ps", "DERIVED(3 ps bytecode hashes)",
         "final title private-indirection command classification"},
        {"final_title_indirection_vs",
         "DERIVED(repeated_meshes_alpha_nostipple vs hash)",
         "final title private-indirection command classification"},
        {"final_title_indirection_rt",
         "rt0=0x4011F370(kFableTitleVisibleRenderTarget)",
         "final title private-indirection command classification"},
        {"billboard_command_header_address",
         "addr=0x4C132C70/CA4/CD8/0x4C133150/3184",
         "billboard command header address membership"},
        {"generated_single_slot0_ps", "DERIVED(4 ps bytecode hashes)",
         "single-texture descriptor probe logging for slot 0"},
        {"title_texture_binding_probe_atlas", "tex=0x404DE550",
         "DIAGNOSTIC ONLY: title texture binding probe"},
        {"final_title_target_diagnostic", "rt=0x4011F370",
         "DIAGNOSTIC ONLY: title scene draw log verbosity/limit"},
        {"title_scene_declaration_diagnostic",
         "decl=0x400216B0/0x40021910",
         "DIAGNOSTIC ONLY: title scene declaration log line"},
        {"title_billboard_payload_diagnostic", "decl=0x40021910",
         "DIAGNOSTIC ONLY: title billboard vertex payload log line"},
        {"prebind_entry_probe_ps", "[DELETED 2026-07-26: never matched]",
         "DIAGNOSTIC ONLY: pre-bind entry probe log line"},
        {"billboard_atlas_image_ready", "[DELETED 2026-07-26: never matched]",
         "short-circuits image-ready for the billboard atlas"},
        {"title_upload_interest_seed",
         "seed=0x401317D0..910/0x404DE490/4D0/510/550",
         "unconditional per-slot resource-interest seeding"},
        {"exact_title_upload_1x1_alias", "[DELETED 2026-07-26: never matched]",
         "exact 1x1 (base 0x1FC40000) preflight interest"},
        {"exact_title_upload_1d_alias",
         "alias=0x401317D0/810/850/890/8D0/910",
         "exact 1-D gradient preflight interest"},
        {"exact_title_upload_16x16_alias", "alias=0x404DE490/4D0/550",
         "exact 16x16 (base 0x1A0D7000) preflight interest"},
        {"tree_billboard_heap_tex13", "tex13>=0x4C000000",
         "tree-billboard RenderDoc draw-target classification"},
        {"tree_billboard_probe_heap_tex13", "tex13>=0x4C000000",
         "DIAGNOSTIC ONLY: tree-billboard target probe log line"},
    }};
constexpr uint32_t kDirectTextureDescriptorSets = 3;
constexpr uint32_t kDirectSamplerDescriptorSet = 3;
constexpr uint32_t kMaxLoggedFailedDirectDraws = 24;
constexpr uint32_t kMaxLoggedFinalFailedDirectCommands = 96;
constexpr bool kFableVerboseTitleSceneDiagnostics = false;
constexpr size_t kMaxFrameDatabaseDrawPackets = 4096u;
constexpr size_t kMaxFrameDatabaseCommandPackets = 4096u;
constexpr uint32_t source_slot = kFablePrivateRectCopyTextureSlot;
constexpr uint32_t kDirectGeneratedSharedConstantsBytes = 1600;
constexpr uint32_t kDirectVertexIntegerConstantByteBase = 576;
constexpr uint32_t kDirectPixelIntegerConstantByteBase = 1088;
constexpr uint32_t kDirectSpecConstantAlphaTest = 1u << 1u;
constexpr uint32_t kDirectSpecConstantReverseZ = 1u << 4u;
constexpr uint32_t kDirectResolveMipAttachmentViewKey = 0xA77C0000u;
constexpr uint32_t kDirectAliasAmbiguitySamplesPerFrame = 64u;
constexpr size_t kMaxDirectResourceAliasMemo = 8192u;
constexpr size_t kMaxDirectResourceNegativeMemo = 4096u;
constexpr size_t kDirectResourceFastCacheSize = 64u;  // power of two
constexpr size_t kDirectConstantArenaSlots = 4u;
constexpr size_t kDirectConstantArenaChunkBytes = 4u * 1024u * 1024u;
constexpr size_t kDirectConstantArenaAlignment = 256u;
constexpr size_t kMaxDirectPipelineEnsureMemo = 8192u;
constexpr uint32_t kMaxExactTitlePreflightBackoffFrames = 32u;
constexpr size_t kMaxExactTitlePreflightBackoffEntries = 512u;
constexpr uint64_t kStaleResourceFrames = 240;

extern PlumeResourceManagerState g_plume_resources;
extern FableDirectRendererState g_direct_renderer;
extern DirectAddressGateCensusSlot g_direct_address_gate_census[kDirectAddressGateCount];
extern GtlResolveHostCopyStats g_gtl_resolve_live_stats;
extern uint64_t g_gtl_resolve_repoint_refreshes;
extern uint64_t g_gtl_resolve_repoint_recreates;
extern GtlVideoMergeStats g_gtl_video_merge_stats;
extern GtlClearMergeStats g_gtl_clear_merge_stats;
extern DirectSurfaceDestinationSplit g_direct_surface_destination_split;
extern DirectResourceLookupCensus g_direct_resource_lookup_census;
extern std::unordered_map<uint32_t, uint32_t> g_direct_resource_alias_memo;
extern std::unordered_set<uint32_t> g_direct_resource_alias_ambiguous;
extern std::unordered_set<uint32_t> g_direct_resource_negative_memo;
extern DirectFindCumulativeCensus g_direct_find_cumulative_census;
extern DirectDrawPhaseAccumulator g_direct_draw_phases;
extern uint64_t g_direct_resource_map_generation;
extern uint64_t g_direct_resource_fast_cache_generation;
extern GtlTessDrawStats g_gtl_tess_draw_stats;
extern uint32_t g_direct_sync_calls_this_compose;
extern uint32_t g_direct_sync_early_outs_this_compose;
extern std::vector<refii::gtl::DrawPacket> g_gtl_carried_draw_packets;
extern uint64_t g_gtl_held_guest_frame_boundary;
extern std::vector<refii::gtl::DrawPacket> g_gtl_held_draw_packets;
extern std::vector<refii::gtl::ResolvePacket> g_gtl_held_resolve_packets;
extern std::vector<refii::gtl::ClearPacket> g_gtl_held_clear_packets;
extern std::vector<refii::gtl::TessellatedPatchDrawPacket> g_gtl_held_tess_packets;

extern DirectResourceFastCacheEntry g_direct_resource_fast_cache[kDirectResourceFastCacheSize];
extern std::unordered_map<uint64_t, plume::RenderPipeline*> g_direct_pipeline_ensure_memo;
extern std::unordered_map<uint32_t, ExactTitlePreflightBackoff> g_exact_title_preflight_backoff;

PlumeTrackedResource* FindDirectResourceLocked(uint32_t address);
void AddDirectClearPrivateTextureCopyOutputInterestLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target, std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
PlumeTrackedResource* FindDirectResourceLocked(uint32_t address);
PlumeTrackedResource* FindDirectResourceByExactObjectLocked(uint32_t address);
bool DirectResourceMatchesAddress(const PlumeTrackedResource& resource,
                                  uint32_t address);
void InvalidateDirectResourceMemoKeyLocked(uint32_t address);
void InvalidateDirectResourceMemosForResourceLocked(
    const PlumeTrackedResource& resource);
void ClearDirectResourceMemosLocked();
void ClearDirectPipelineEnsureMemoLocked();
bool RefreshDirectTextureSampleViewLocked(PlumeTrackedResource& tracked);
bool RecordDirectTextureBarrierLocked(plume::RenderCommandList* command_list,
                                      plume::RenderBarrierStages stages,
                                      plume::RenderTexture* texture,
                                      plume::RenderTextureLayout new_layout);
void PopulateDirectPrivateTextureCopyTargetLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& target);
void PopulateDirectPrivateTextureCopyShaderRoots(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command);
void PopulateDirectPrivateTextureCopyRectangleFromCommand(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command);
void PopulateDirectSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action);
void PopulateDirectSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& link,
    const FableDirectRenderCommand& command);
uint64_t DirectPrivateRectExactActionLookupKey(
    const FablePrivateTextureCopyAction& action);
uint64_t DirectStablePrivateCopyActionKey(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    bool include_wrapper_sample_identity, uint64_t domain);
bool RememberDirectStablePrivateCopyActionKeyLocked(uint64_t stable_key);
bool DirectSurfaceAliasDestinationMatchesAddress(
    const RendererSurfaceCopyAliasSnapshot& alias, uint32_t address,
    const PlumeTrackedResource* sampled_resource);
uint32_t DirectCommandPixelTextureSlotMaskLocked(
    const FableDirectRenderCommand& command);
std::unique_ptr<plume::RenderBuffer> CreateUploadBufferWithData(
    plume::RenderDevice* device, const void* data, size_t size,
    const char* name, plume::RenderBufferFlags flags);
plume::RenderBuffer* GetOrCreateCachedUploadBufferWithDataLocked(
    plume::RenderDevice* device, const void* data, size_t size,
    const char* name, plume::RenderBufferFlags flags);
void RetainFrameUploadBuffersLocked(
    std::vector<std::unique_ptr<plume::RenderBuffer>> buffers);
void ResetDirectScaledPrivateRectBlitResourcesLocked();
bool RecordDirectScaledPrivateRectCopyLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const FablePrivateTextureCopyAction& action,
    PlumeTrackedResource& source, PlumeTrackedResource& target,
    uint32_t source_slot, const char* reason);
uint64_t DirectRegisteredPrivateRectScaledDecisionCacheKey(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& target,
    uint32_t slot);
bool DirectRegisteredPrivateRectExecuteDecisionCacheHit(uint64_t cache_key);
void RememberDirectRegisteredPrivateRectExecuteDecision(uint64_t cache_key);
void SyncDirectResourcesLocked(plume::RenderDevice* device,
                               plume::RenderCommandList* command_list,
                               const std::unordered_set<uint32_t>*
                                   texture_upload_filter = nullptr,
                               const std::unordered_set<uint32_t>*
                                   resource_filter = nullptr,
                               bool wait_for_observer = false,
                               bool only_changed_resource_filter = false);
bool SyncTrackedResourceLocked(plume::RenderDevice* device,
                               const FableGuestResourceSnapshot& snapshot,
                               DirectResourceSyncBreakdown* sync_timing);
void RememberMaterializedPrivateRectTargetSourcesLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target);
bool RememberDirectExactPrivateRectSurfaceTextureEdgesLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target, FablePrivateTextureCopyKind kind);
bool DirectResourceHasTextureSampleContentForExactProducer(
    const PlumeTrackedResource& resource);
bool IsFablePrivateTextureChainResource(const PlumeTrackedResource& resource);
bool DirectResourceAddressMatchesLocked(uint32_t resource_address,
                                        uint32_t address);
bool DirectCommandBindsResourceAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource);
bool DirectCommandUsesFablePrivateTexturePath(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
void RememberDirectPrivateTextureCopyActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind,
    bool allow_materialized_surface_link = true);
uint64_t DirectPrivateTextureCopySerialLocked();
void RememberDirectPrivateFeedbackActionBackingLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction* exact_action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    const PlumeTrackedResource& alias);
void RememberDirectPrivateRectExactActionLocked(
    const FablePrivateTextureCopyAction& action);
const FablePrivateTextureCopyAction*
FindDirectPrivateRectExactActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
void RememberDirectMutablePrivateHeaderExactSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& sample, const char* reason);
PlumeTrackedResource* ResolveDirectMutablePrivateHeaderExactSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandTextureSlotHasExactProducerOwnerProbeLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandTextureSlotHasExactRegisteredProducerLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, bool log_misses = true);
bool DirectCommandTextureSlotExactProducerProvidesSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectExactWrapperSourceSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* source);
bool DirectCommandHasExactSyntheticPrivateRectCopyInputsLocked(
    const FableDirectRenderCommand& command);
PlumeTrackedResource* ResolveDirectCommandTextureSlotExactProducerLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource*
ResolveDirectDestinationEdgeSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectSampledTextureForCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
PlumeTrackedResource*
ResolveDirectPrivateCopyOutputSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectSyntheticPrivateRectDepthActionCanExecuteLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture);
bool DirectDepthTargetHasExactPrivateRectActionLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& depth_target);
bool DirectSyntheticPrivateRectColorActionCanExecuteLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture);
PlumeTrackedResource*
ResolveDirectExactProducerSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    bool allow_private_title_exact_sample = false);
bool DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandCanBootstrapRegisteredPrivateRectMaterializationLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool IsFableTitleGuiTextAtlasCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectTinyTitleCachedTextureCanBindLocked(
    const FableDirectRenderCommand& command, uint32_t guest,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* skip_texture);
PlumeTrackedResource* ResolveDirectExactTitleUploadSourceForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* FindDirectReadyRegisteredTextureByFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords, uint32_t preferred_guest = 0);
uint32_t DirectRegisteredFetchPreferredGuestForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const std::array<uint32_t, 6>& dwords);
PlumeTrackedResource* ResolveDirectSampledTextureFromFetchConstantsLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectTitleSyncFetchTextureMatchesCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& texture);
bool DirectPrivateTextureCopyActionProvidesSampleLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
bool RegisterDirectTextureIdentityFromResourceLocked(
    const PlumeTrackedResource& resource,
    const std::array<uint32_t, 6>& fetch_dwords);
std::array<uint32_t, 6> DirectCommandTextureFetchDwords(
    const FableDirectRenderCommand& command, uint32_t slot);
void RememberDirectPrivateTextureAliasIndexLocked(
    uint32_t source_guest, uint32_t source_base,
    const std::array<uint32_t, 6>& source_fetch_dwords,
    const PlumeTrackedResource& target,
    bool include_equivalent_identity_aliases = true);
void TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
    const FablePrivateTextureCopyAction& action, const char* reason);
void RememberDirectMaterializedPrivateSourceSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind, const char* reason,
    const PlumeTrackedResource* known_backing,
    bool backing_resolved);
bool DirectSurfaceTextureLinkTouchesFablePrivateChain(
    const FableSurfaceTextureLink& link,
    const PlumeTrackedResource* sample);
bool DirectSurfaceTextureLinkIsCurrentTargetSelfEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
bool DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
bool DirectPrivateTextureBackingCandidateValidLocked(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target, const PlumeTrackedResource* candidate);
bool DirectCurrentPrivateTextureActionCanSampleTargetSurfaceLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
void PopulateDirectPrivateTextureCopyBackingLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& backing);
void PopulateDirectSurfaceTextureLinkBackingLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& backing);
void PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& source);
void PopulateDirectPrivateTextureCopySourceLocked(
    FablePrivateTextureCopyAction& action, const PlumeTrackedResource& source);
PlumeTrackedResource* ResolveDirectPrivateTextureActionBackingLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
PlumeTrackedResource* ResolveDirectRegisteredSampleSourceLocked(
    const FableDirectRenderCommand& command,
    const FableRegisteredSampleSource& sampled_source);
PlumeTrackedResource* ResolveDirectSurfaceTextureLinkResourceLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
bool PublishDirectBeginVerticesTitleTextureActionForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
void ForgetDirectTextureLayoutLocked(plume::RenderTexture* texture);
void RegisterDirectSurfaceTextureLinkPreparedLocked(
    const FableSurfaceTextureLink& link, const char* reason = nullptr);
void RegisterDirectPrivateTextureCopyActionLocked(
    const FablePrivateTextureCopyAction& action);
void RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
    const FablePrivateTextureCopyAction& action, const char* reason);
void RegisterDirectScaledPrivateRectSurfaceTextureLinkLocked(
    const FablePrivateTextureCopyAction& action, const char* reason);
void RememberDirectPrivateTextureSetupClearIntentsLocked(
    const std::vector<FableDirectRenderCommand>& commands);
bool IsFablePrivateTextureRectangleSetupClearLr(uint32_t lr);
void ClearDirectFramePrivateRectCachesLocked();
uint32_t ResolveDirectInputSlotStrideLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool PromoteDirectExactCopyDestinationToColorTargetLocked(
    plume::RenderDevice* device, PlumeTrackedResource& target,
    FableSurfaceTextureLink link);
bool ShouldExpandPrimitive8Rectangle(const FableDirectRenderCommand& command,
                                     uint32_t slot);
bool IsFableTitleSceneVertexShaderCommandLocked(
    const FableDirectRenderCommand& command);
bool IsDisplaySurfaceAliasTargetLocked(uint32_t address);
PlumeTrackedResource* FindDirectReadyDeclarationResourceByIdentityLocked(
    uint32_t declaration);
bool IsFableTitleGuiQuadDeclaration(uint32_t declaration);
bool IsFableTitleGuiSingleTexturePostprocessPixelShader(uint32_t shader);
bool IsFableTitleSceneTargetLocked(uint32_t address);
uint64_t DirectPixelShaderHashLocked(uint32_t shader);
PlumeTrackedResource* FindDirectReadyShaderResourceByIdentityLocked(
    uint32_t shader, FableGuestResourceKind expected_kind);
bool EnsureDirectKnownVertexDeclarationResourceLocked(uint32_t declaration);
bool IsFableFinalTitlePrivateIndirectionCommandLocked(
    const FableDirectRenderCommand& command);
bool IsFableTitleBillboardAtlasCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool IsFableTitleSceneTextureSyncCommandLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandSlotUsesExactTitleUploadSourceLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectCommandIsExactBinkIndexedWrapperDrawLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(
    const FableDirectRenderCommand& command);
bool IsRenderDocEarlyBinkWrapperDrawTargetLocked(
    const FableDirectRenderCommand& command);
bool IsRenderDocStartupVideoCopyDrawTargetLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandSlotUsesExactBinkWrapperPlaneLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
PlumeTrackedResource* ResolveDirectExactBinkWrapperPlaneForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
void AddDirectExactBinkWrapperPlaneResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
FableTitleFramePacketTargetFactsList DirectTitleFramePacketTargetFactsLocked(
    const FableDirectFrameDatabaseDrawPacket& packet);
FableTitleFramePacketShaderFacts DirectTitleFramePacketShaderFactsLocked(
    const FableDirectFrameDatabaseDrawPacket& packet);
void AddDirectTextureBaseAddressResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t base_address);
bool ShouldReplaceTrackedSceneTarget(uint32_t candidate_width,
                                        uint32_t candidate_height,
                                        uint32_t remembered_width,
                                        uint32_t remembered_height,
                                        uint32_t candidate_target,
                                        uint32_t remembered_target);
void AccumulateGtlResolveFbMs(double elapsed_ms);
void AccumulateGtlResolveDrawMs(double elapsed_ms);
bool DirectCommandWritesSurfaceAliasSourceLocked(
    const FableDirectRenderCommand& command,
    const RendererSurfaceCopyAliasSnapshot& alias);
bool RecordDirectPresentBlitActionPacketLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const RefiiFrameActionPacket& action,
    DirectPresentSource& source);
void RecordDirectClearCommand(plume::RenderDevice* device,
                              plume::RenderCommandList* command_list,
                              plume::RenderFramebuffer* output_framebuffer,
                              plume::RenderFormat output_depth_format,
                              const FableDirectRenderCommand& command);
void AccumulateDestScanCensus(uint64_t scanned, uint64_t relevant);
bool DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
    const FableSurfaceTextureLink& link, uint32_t surface_address,
    uint32_t depth_target_address);
void MaybeBeginRenderDocResolveTargetCaptureLocked(
    const FableDirectRenderCommand& command);
void LogDirectResourceSyncFailureLocked(
    const FableGuestResourceSnapshot& snapshot, const char* context);
std::array<uint32_t, 6> DirectCommandTextureFetchDwords(
    const FableDirectRenderCommand& command, uint32_t slot);
void AddDirectTextureFetchResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::array<uint32_t, 6>& fetch_dwords);
void AddDirectTextureSlotIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
std::array<uint32_t, 6> DirectCommandTextureFetchDwords(
    const FableDirectRenderCommand& command, uint32_t slot);
bool IsFableTitleBillboardAtlasResource(const PlumeTrackedResource& texture);
FableTitleResourceFacts DirectTitleResourceFactsForResource(
    const PlumeTrackedResource& resource,
    const FableTextureIdentity* identity = nullptr);
bool IsFableTitleBillboardAtlasCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
PlumeTrackedResource* FindExactFableTitleBillboardAtlasForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* RegisterFableTitleBillboardBackingLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, const char* source_name);
PlumeTrackedResource* ResolveFableTitleBillboardAtlasUploadSourceForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectPrivateTextureHeaderNeedsAlias(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectExactTitleUploadedResourceHasAuthoredPayload(
    const PlumeTrackedResource& resource);
bool DirectFableTitleIndirectionHasAuthoritativeUpload(
    const PlumeTrackedResource& resource);
bool DirectFableTitleBillboardAtlasHasAuthoritativeUpload(
    const PlumeTrackedResource& resource);
bool RegisterDirectTextureIdentityFromResourceLocked(
    const PlumeTrackedResource& resource,
    const std::array<uint32_t, 6>& fetch_dwords);
bool DirectResourceUploadDefinesTextureIdentity(
    const PlumeTrackedResource& resource);
bool DirectCommandBindsResourceAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource);
bool DirectCommandBindsResourceAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource);
void RememberDirectMaterializedPrivateSourceSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind, const char* reason,
    const PlumeTrackedResource* known_backing = nullptr,
    bool backing_resolved = false);
PlumeTrackedResource* ResolveDirectSurfaceTextureLinkResourceLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
PlumeTrackedResource* ResolveDirectTrustedSurfaceTextureEdgeLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
uint64_t DirectMaterializedPrivateRectActionFrameKey(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind);
uint64_t DirectPrivateCopyActionContentEdgeKey(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
PlumeTrackedResource* ResolveDirectRegisteredSampleSourceLocked(
    const FableDirectRenderCommand& command,
    const FableRegisteredSampleSource& sampled_source);
void AddFableTitleBillboardAtlasResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
void AddFableTinyTitleTextureResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
void AddDirectTextureSlotIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
uint32_t DirectInlineBufferOffsetForCommandAddress(
    const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& buffer, uint32_t address);
std::unique_ptr<plume::RenderBuffer> CreateSyntheticPrivateRectVertexUpload(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot);
plume::RenderBuffer* GetOrCreateCachedSyntheticPrivateRectVertexBufferLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command);
void MaybeBeginRenderDocDrawTargetCaptureLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding* framebuffer = nullptr);
bool GtlPacketIdentityAgreesWithCommandLocked(
    const FableDirectRenderCommand& command,
    const refii::gtl::DrawPacket& packet);
void SyncDirectResourcesLocked(plume::RenderDevice* device,
                               plume::RenderCommandList* command_list,
                               const std::unordered_set<uint32_t>*
                                   texture_upload_filter,
                               const std::unordered_set<uint32_t>*
                                   resource_filter,
                               bool wait_for_observer,
                               bool only_changed_resource_filter);
void ResetFableDirectLivePresentSourcesLocked(bool reset_stats = true);

const char* DirectPrivateTextureResolveSourceName(
    DirectPrivateTextureResolveSource source);
bool DirectPrivateTextureCopyActionMatchIsExactRegistered(
    DirectPrivateTextureCopyActionMatch match);
void ResetDirectDiagnosticsLocked();
void ResetDirectPresentTrackingLocked();
bool IsFableSingleSourcePostprocessPixelShaderHash(uint64_t pixel_shader_hash);
bool IsFableScenePrivateCopyPixelShaderHash(uint64_t pixel_shader_hash);
bool DirectAddressGateCensusLoggingEnabled();
bool DirectAddressGateShouldReport(uint64_t tested);
bool DirectAddressGateTest(DirectAddressGate gate, bool literal_hit,
                           uint32_t observed_address = 0,
                           uint64_t derived_key = 0, bool derived_hit = false);
void ReportDirectAddressGateCensus(const char* reason);
bool RequestGtlDefectCaptureLocked(const char* defect_class,
                                   uint32_t& fired_count, uint32_t cap);
void ResetDirectRenderGraphExecutionSnapshotLocked(uint32_t frame_index);
bool DecodeDirectSyntheticGeneratedShaderId(uint32_t shader_id,
                                            uint32_t& generated_id);
std::string DirectResourceMarkerNameLocked(uint32_t address);
void AppendDirectMarkerName(std::string& label, std::string_view prefix,
                            std::string_view name);
bool DirectDebugLabelsEnabled();
std::string BuildDirectDrawDebugLabelLocked(
    const FableDirectRenderCommand& command,
    uint32_t draw_count,
    bool synthetic_quad_list,
    std::string_view pass_name);
std::string BuildDirectPassDebugLabelLocked(std::string_view pass_name);
std::string NormalizeDirectRenderPassName(std::string_view pass_name);
void MarkDirectPrivateTextureAliasStateChangedLocked();
void ResetDirectPrivateFetchAliasFrameCountersLocked();
uint32_t ClampCounter(uint64_t value);
void RefreshDirectStatsFromHooksLocked(
    uint32_t frame_index, bool force_hook_snapshot = false,
    const FableDirectStateSnapshot* direct_snapshot_override = nullptr);
bool DirectCommandUsesTextureSlotForSyncLocked(
    const FableDirectRenderCommand& command, uint32_t texture_slot_mask,
    uint32_t slot);
bool DirectSurfaceTextureLinkExactLocalWrapperVisibleToCommandLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
bool DirectSurfaceTextureLinkPrivateWrapperVisibleToCommandLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
bool DirectSurfaceTextureLinkHasExactWrapperProducerSurfaceIdentity(
    const FableSurfaceTextureLink& link);
void PopulateDirectPrivateTextureWrapperSourceIdentity(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink* link = nullptr);
bool DirectSurfaceTextureLinkHasCommandConsumableSampleIdentity(
    const FableSurfaceTextureLink& link);
bool DirectSurfaceTextureLinkHasExactCopyPayloadIdentity(
    const FableSurfaceTextureLink& link);
FableGuestResourceUploadCacheEntry UploadCacheEntryForSnapshot(
    const FableGuestResourceSnapshot& snapshot);
bool SameDirectUploadCacheEntry(
    const FableGuestResourceUploadCacheEntry& lhs,
    const FableGuestResourceUploadCacheEntry& rhs);
void LogDirectResourceUploadSyncLocked(
    const FableGuestResourceSnapshot& snapshot,
    const FableGuestResourceUploadCacheEntry& upload_entry);
bool DirectAddressInFableD3DDeviceMirror(uint32_t address);
bool DirectImageSnapshotReferencesD3DDeviceMirror(
    const FableGuestResourceSnapshot& snapshot);
bool DirectTextureSnapshotHasStorageIdentity(
    const FableGuestResourceSnapshot& snapshot);
const char* DirectImageSnapshotQuarantineReason(
    const FableGuestResourceSnapshot& snapshot);
void LogDirectImageSnapshotQuarantineLocked(
    const FableGuestResourceSnapshot& snapshot, const char* reason);
bool ReadBinaryFile(const std::string& path, std::vector<uint8_t>& bytes);
bool CreateNullTextureLocked(plume::RenderDevice* device, uint32_t set_index,
                             plume::RenderTextureDimension dimension,
                             plume::RenderTextureViewDimension view_dimension,
                             plume::RenderTextureFlags flags =
                                 plume::RenderTextureFlag::NONE,
                             uint32_t depth = 1,
                             uint32_t array_size = 1);
void ResetPlumeResourceManagerObjectsLocked();
void ResetLiveScenePresentSourceLocked();
void ResetSubmittedScenePresentSourceLocked();
void ResetFableSceneTargetLocked();
void ResetPixVideoPresentTargetLocked();
bool EnsureDirectDrawResourcesLocked(plume::RenderDevice* device,
                                     plume::RenderShaderFormat shader_format);
plume::RenderTextureAddressMode ConvertDirectSamplerAddressMode(uint32_t mode);
plume::RenderSampler* GetOrCreateDirectSamplerLocked(uint32_t packed_state);
PlumeColorAttachmentPadding* GetOrCreateDirectColorAttachmentPaddingLocked(
    plume::RenderDevice* device, uint32_t width, uint32_t height,
    plume::RenderFormat format);
bool UseSyntheticPrivateRectInputLayout(
    const FableDirectRenderCommand& command);
bool DirectCommandCanMaterializePrivateRectTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target);
void BuildSyntheticPrivateRectInputLayout(
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements);
void BuildSyntheticPrimitive8RectangleInputLayout(
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements);
bool DirectCommandUsesExactBeginVerticesInlineStride(
    const FableDirectRenderCommand& command, uint32_t slot);
uint32_t ResolveDirectInputSlotStrideLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
void ResolveDirectInputSlotStridesLocked(
    const FableDirectRenderCommand& command,
    std::vector<plume::RenderInputSlot>& input_slots);
uint64_t DirectVertexShaderHashLocked(uint32_t shader);
bool DirectShaderObjectsEquivalentLocked(uint32_t lhs, uint32_t rhs,
                                         FableGuestResourceKind kind);
bool DirectVertexShaderObjectsEquivalentLocked(uint32_t lhs, uint32_t rhs);
bool DirectPixelShaderObjectsEquivalentLocked(uint32_t lhs, uint32_t rhs);
bool DirectActionRootIsOlderThanCommand(uint64_t action_call_serial,
                                        uint32_t action_symbol,
                                        const FableDirectStateRoot& command_root);
bool DirectActionRootIsUnkeyedForCommand(
    uint64_t action_call_serial, uint64_t action_context_key,
    uint32_t action_symbol, uint32_t action_lr,
    const FableDirectStateRoot& command_root);
bool DirectPrivateTextureCopyActionShaderRootsSupersededLocked(
    const FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command);
bool DirectPrivateTextureCopyActionTargetShapeCompatibleWithResource(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& target);
uint64_t DirectVertexDeclarationHashLocked(uint32_t declaration);
bool DirectVertexDeclarationMatchesHashLocked(uint32_t declaration,
                                              uint64_t expected_hash);
bool IsFableTitleSceneVertexShaderCommandLocked(
    const FableDirectRenderCommand& command);
bool IsFableTitleSceneFramebuffer(const DirectFramebufferBinding& framebuffer);
std::string DirectFramebufferKey(
                                 const std::array<PlumeTrackedResource*,
                                                  kMaxFableRenderTargets>& colors,
                                 uint32_t color_count,
                                 const PlumeTrackedResource* depth,
                                 plume::RenderFramebuffer* output_framebuffer);
std::string DirectStableAttachmentFramebufferKey(
    const DirectFramebufferBinding& binding,
    plume::RenderFramebuffer* output_framebuffer);
bool IsDisplaySurfaceAliasTargetLocked(uint32_t address);
bool IsFableSceneTargetCandidate(uint32_t address,
                                  uint32_t width,
                                  uint32_t height);
bool IsFablePresentTargetCandidate(uint32_t address,
                                    uint32_t width,
                                    uint32_t height);
void RememberFableSceneTargetLocked(uint32_t address,
                                     uint32_t width,
                                     uint32_t height,
                                     const char* reason);
bool IsFableTitleSceneTargetLocked(uint32_t address);
bool DirectResourceIsTrackedSceneOutputLocked(
    const PlumeTrackedResource& resource);
bool IsFableTitleSceneDrawTargetLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer);
bool ShouldPreserveTrackedSceneOverDisplayAliasLocked(
    uint32_t candidate_width,
    uint32_t candidate_height,
    uint32_t candidate_target,
    uint32_t remembered_width,
    uint32_t remembered_height,
    uint32_t remembered_target);
bool ShouldReplaceDisplayAliasWithSceneTargetLocked(uint32_t candidate_width,
                                                    uint32_t candidate_height,
                                                    uint32_t candidate_target,
                                                    uint32_t remembered_target);
void MarkDirectResourceGpuProducedContentLocked(
    uint32_t address, uint64_t write_serial, bool clear_only = false,
    const plume::RenderTexture* attachment = nullptr);
void RememberDrawnScenePrivateTextureCopyActionsLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& binding);
void RememberDrawnSceneTargetLocked(const FableDirectRenderCommand& command,
                                    const DirectFramebufferBinding& binding,
                                    bool submitted_command_source);
void InvalidateRememberedSceneTargetForClearLocked(
    uint32_t address,
    plume::RenderTexture* texture,
    uint32_t width,
    uint32_t height);
void ForgetDrawnSceneTargetLocked(
    uint32_t address, const PlumeTrackedResource* destroyed_resource);
uint32_t PackFableSurfaceExtent(uint32_t width, uint32_t height);
bool ResolvePrivateRectTargetExtent(const FableDirectRenderCommand& command,
                                    uint32_t& width, uint32_t& height);
bool DirectSetupClearCanMaterializePrivateRectTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target);
void RegisterMaterializedPrivateRectTargetEdgeLocked(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& target,
    uint32_t slot);
bool EnsurePrivateRectRenderTargetSurfaceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot, bool remember_source_edges = true,
    const char* reason = nullptr);
void PrepareDirectPrivateRectRenderTargetsForCommandLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command);
bool EnsurePrivateRectSetupClearSurfaceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot);
bool PromoteDirectResourceToDepthTargetLocked(
    plume::RenderDevice* device, PlumeTrackedResource& target,
    const FableDirectRenderCommand& command, const char* reason);
bool EnsureDirectPrivateRectDepthTargetSurfaceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const char* reason);
void EnsurePrivateRectSetupClearSurfacesLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command);
DirectFramebufferBinding ResolveDirectFramebufferLocked(
    plume::RenderDevice* device, plume::RenderFramebuffer* output_framebuffer,
    plume::RenderFormat output_depth_format,
    const FableDirectRenderCommand& command);
bool ResolveDirectSingleColorFramebufferForResourceLocked(
    plume::RenderDevice* device, PlumeTrackedResource& target,
    DirectFramebufferBinding& binding, uint32_t mip_level = 0);
bool CreatePresentBlitShaderForFormat(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format,
    bool vertex_shader, std::unique_ptr<plume::RenderShader>& shader);
bool EnsurePresentBlitResourcesLocked(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format);
DirectPresentSource PresentSourceFromResourceLocked(uint32_t address);
FableTitleFramePacketTargetFactsList DirectTitleFramePacketTargetFactsLocked(
    const FableDirectFrameDatabaseDrawPacket& packet);
FableTitleFramePacketShaderFacts DirectTitleFramePacketShaderFactsLocked(
    const FableDirectFrameDatabaseDrawPacket& packet);
FableTitlePresentSourceFacts DirectTitlePresentSourceFactsLocked(
    const DirectPresentSource& source);
bool DirectPresentSourceHasSameFrameDrawProducerLocked(
    const DirectPresentSource& source,
    DirectFrameDatabaseDrawPacketProducerKind producer_kind);
bool DirectPresentSourceHasSameFrameResolveProducerLocked(
    const DirectPresentSource& source);
bool DirectPresentSourceHasSameFrameTitlePresentProducerLocked(
    const DirectPresentSource& source);
bool DirectPresentSourceHasSameFrameFullSceneProducerLocked(
    const DirectPresentSource& source);
bool DirectPresentSourceHasSameFrameTitleVisibleProducerLocked(
    const DirectPresentSource& source);
plume::RenderTextureViewDesc SampleTextureViewDescForTrackedResource(
    const PlumeTrackedResource& tracked);
uint32_t TextureViewMappingKeyForTrackedResource(
    const PlumeTrackedResource& tracked);
bool RefreshDirectTextureSampleViewLocked(PlumeTrackedResource& tracked);
plume::RenderTextureView* DirectBinkPlaneIdentitySampleViewLocked(
    PlumeTrackedResource& tracked);
bool PromoteDirectExactCopyDestinationToColorTargetLocked(
    plume::RenderDevice* device, PlumeTrackedResource& target,
    FableSurfaceTextureLink link);
uint64_t DirectPreparedExactCopyDestinationKey(
    const FableSurfaceTextureLink& link,
    const PlumeTrackedResource& destination);
bool RememberDirectPreparedExactCopyDestinationLocked(uint64_t key);
bool DirectPreparedExactCopyDestinationCachedLocked(uint64_t key);
bool DirectSurfaceTextureLinkStablePublicationReason(const char* reason);
uint64_t DirectStableSurfaceTextureLinkPublicationKey(
    const FableSurfaceTextureLink& link);
bool DirectStableSurfaceTextureLinkPublicationRegisteredLocked(
    uint64_t key, const FableSurfaceTextureLink& link);
bool DirectStableSurfaceTextureLinkPublicationCachedLocked(
    uint64_t key, const FableSurfaceTextureLink& link);
void RememberDirectStableSurfaceTextureLinkPublicationLocked(uint64_t key);
void PrepareDirectExactCopyDestinationFromLinkLocked(
    const FableSurfaceTextureLink& link, const char* reason);
void RegisterDirectSurfaceTextureLinkPreparedLocked(
    const FableSurfaceTextureLink& link, const char* reason);
DirectPresentSource
DirectPresentSourceFromCurrentFrameSurfaceDestinationLocked();
DirectPresentSource DirectPresentSourceFromCurrentFrameTitleDrawsLocked();
void ClearDirectFramePrivateRectCachesLocked();
DirectPresentSource ResolveDirectPresentSourceLocked(
    const FableDirectStateSnapshot& frame_snapshot);
void CountRefiiFrameActionPacketLocked(const RefiiFrameActionPacket& action);
void RefreshRefiiPacketQueueReplayReadyLocked();
void AppendRefiiFrameActionPacketLocked(RefiiFrameActionPacket action);
FableDirectFrameDatabaseCommandPacket BuildFrameDatabasePresentBlitCommandPacket(
    const DirectPresentSource& source, uint32_t output_width,
    uint32_t output_height);
void AppendFrameDatabasePresentBlitCommandPacketLocked(
    FableDirectFrameDatabaseCommandPacket packet);
void AppendFrameDatabasePresentBlitCommandPacketLocked(
    const DirectPresentSource& source, uint32_t output_width,
    uint32_t output_height);
DirectPresentSource ResolveDirectPresentSourceFromRefiiPresentBlitPacketLocked(
    const RefiiPresentBlitPacket& packet);
bool DirectTextureCopySizeMatches(const PlumeTrackedResource& source,
                                  const PlumeTrackedResource& destination);
bool DirectTextureCopyMipCountsMatch(const PlumeTrackedResource& source,
                                     const PlumeTrackedResource& destination);
void MaybeBeginRenderDocSurfaceDestinationCaptureLocked(
    const char* copy_kind,
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
void MarkDirectTextureLayoutLocked(plume::RenderTexture* texture,
                                   plume::RenderTextureLayout layout);
void ForgetDirectTextureLayoutLocked(plume::RenderTexture* texture);
bool RecordDirectTextureBarrierLocked(plume::RenderCommandList* command_list,
                                      plume::RenderBarrierStages stages,
                                      plume::RenderTexture* texture,
                                      plume::RenderTextureLayout layout);
bool RecordDirectTextureBarrierForcedLocked(
    plume::RenderCommandList* command_list, plume::RenderBarrierStages stages,
    plume::RenderTexture* texture, plume::RenderTextureLayout layout);
uint64_t DirectSurfaceAliasCopyCacheKey(uint32_t source_surface,
                                        uint32_t destination_surface);
uint64_t DirectSurfaceAliasCopyCacheKey(
    const RendererSurfaceCopyAliasSnapshot& alias);
uint64_t DirectSurfaceDestinationCopyFrameKey(
    const FableSurfaceDestinationTextureRelation& relation);
bool DirectSurfaceDestinationCopyRecordedThisFrameLocked(
    const FableSurfaceDestinationTextureRelation& relation);
void RememberDirectSurfaceDestinationCopyThisFrameLocked(
    const FableSurfaceDestinationTextureRelation& relation);
uint64_t DirectCoveredDuplicatePrivateRectSurfaceLinkKey(
    const FableSurfaceTextureLink& link);
bool DirectResourceIsPrivateFeedbackAlias(
    const PlumeTrackedResource& resource);
bool DirectPrivateFeedbackAliasContainsTargetContentLocked(
    const PlumeTrackedResource& alias, const PlumeTrackedResource& target);
bool DirectPrivateFeedbackAliasContainsTargetSerialLocked(
    const PlumeTrackedResource& alias, uint64_t target_content_serial);
bool DirectPrivateFeedbackAliasCanSampleTargetLocked(
    const PlumeTrackedResource& alias, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target);
bool DirectPrivateTextureCopyActionBackingContainsTargetContentLocked(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& alias, const PlumeTrackedResource& target);
void RetirePresentHoldAllocationLocked(
    std::unique_ptr<plume::RenderTexture>& texture,
    std::unique_ptr<plume::RenderTextureView>& view);
void RecordVideoPresentHoldSnapshotLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* color_texture, uint32_t color_address,
    uint32_t width, uint32_t height, plume::RenderFormat color_format);
void RecordGenericPresentHoldSnapshotLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const DirectPresentSource& source);
bool DirectTextureCopyCanRecordLocked(plume::RenderCommandList* command_list,
                                      const PlumeTrackedResource& source,
                                      const PlumeTrackedResource& destination);
bool RecordDirectTextureCopyLocked(plume::RenderCommandList* command_list,
                                   PlumeTrackedResource& source,
                                   PlumeTrackedResource& destination);
void CountFetchBindingActionSiteHit(const char* site, bool hit);
PlumeTrackedResource* FindGtlResolveDestinationHybridLocked(
    const refii::gtl::ResolvePacket& packet);
bool IsGtlResolveHostCopyBuildableLocked(
    const refii::gtl::ResolvePacket& packet, GtlResolveHostCopyStats* stats,
    const GtlResolveEndpoints* endpoints = nullptr);
void ResetDirectScaledPrivateRectBlitResourcesLocked();
bool EnsureDirectScaledPrivateRectBlitResourcesLocked(
    plume::RenderDevice* device, plume::RenderShaderFormat shader_format,
    plume::RenderFormat target_format);
bool RecordDirectScaledPrivateRectCopyLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const FablePrivateTextureCopyAction& action,
    PlumeTrackedResource& source, PlumeTrackedResource& target,
    uint32_t source_slot, const char* reason);
bool RecordGtlResolveDrawLocked(plume::RenderCommandList* command_list,
                                const refii::gtl::ResolvePacket& packet,
                                GtlResolveHostCopyStats* stats, bool dry_run,
                                const GtlResolveEndpoints* endpoints = nullptr);
FableDirectFrameDatabaseCommandPacket
BuildFrameDatabaseSurfaceDestinationCommandPacketLocked(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    bool copy_used_scaled_path);
void AppendFrameDatabaseSurfaceDestinationCommandPacketLocked(
    FableDirectFrameDatabaseCommandPacket packet);
void AppendFrameDatabaseSurfaceDestinationCommandPacketLocked(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    bool scaled_copy);
void AppendFrameDatabaseSurfaceDestinationResolvePacketLocked(
    const FableSurfaceDestinationTextureRelation& relation,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
bool RecordDirectSurfaceDestinationScaledCopyLocked(
    plume::RenderCommandList* command_list,
    const RefiiSurfaceDestinationPacket& packet,
    const FableSurfaceDestinationTextureRelation& relation,
    PlumeTrackedResource& source, PlumeTrackedResource& target);
bool RecordDirectSurfaceDestinationExactCopyLocked(
    plume::RenderCommandList* command_list,
    const RefiiSurfaceDestinationPacket& packet,
    const FableSurfaceDestinationTextureRelation& relation,
    PlumeTrackedResource& source, PlumeTrackedResource& target);
bool RecordDirectSurfaceDestinationActionPacketLocked(
    plume::RenderCommandList* command_list,
    const RefiiFrameActionPacket& action,
    const FableSurfaceDestinationTextureRelation& relation,
    PlumeTrackedResource& source, PlumeTrackedResource& target,
    bool* scaled_copy);
bool RecordDirectFrameActionPacketLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const RefiiFrameActionPacket& action,
    const FableSurfaceDestinationTextureRelation* surface_relation,
    PlumeTrackedResource* surface_source, PlumeTrackedResource* surface_target,
    DirectPresentSource* present_source, bool* surface_scaled_copy);
bool DirectSurfaceDestinationSourceHasDrawnOrAuthoredPayload(
    const PlumeTrackedResource& source);
void ResetDirectSurfaceDestinationStatsLocked(uint32_t relations = 0,
                                              uint32_t omitted = 0);
void NormalizeDirectSurfaceDestinationStatsLocked();
std::unordered_map<uint32_t, uint64_t>&
DirectSurfaceDestinationBucketAttemptsThisFrameLocked();
DirectSurfaceDestinationScanPlan
PlanDirectSurfaceDestinationScanForDrawLocked(
    const FableDirectRenderCommand& draw_command);
uint32_t RecordDirectSurfaceDestinationCopiesLocked(
    plume::RenderCommandList* command_list, bool reset_stats = true,
    const DirectSurfaceDestinationScanPlan* scan_plan = nullptr);
bool DirectCommandSamplesSurfaceAliasDestinationLocked(
    const FableDirectRenderCommand& command,
    const RendererSurfaceCopyAliasSnapshot& alias);
const FableSurfaceTextureLink* FindDirectSurfaceTextureLinkByAliasLocked(
    const RendererSurfaceCopyAliasSnapshot& alias);
bool DirectSurfaceCopyAliasHasExactIdentity(
    const RendererSurfaceCopyAliasSnapshot& alias);
bool DirectSurfaceTextureLinkHasExactCopyIdentity(
    const FableSurfaceTextureLink& link);
bool DirectCommandUsesPrivateOrTitleSurfaceCopyPathLocked(
    const FableDirectRenderCommand& command);
bool FindDirectSurfaceCopyAliasByDestinationAddressLocked(
    uint32_t address, RendererSurfaceCopyAliasSnapshot* alias);
bool RecordDirectSurfaceAliasHazardCopyForAliasLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const RendererSurfaceCopyAliasSnapshot& alias);
void RecordDirectSurfaceAliasHazardCopiesForCommandLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command);
void RegisterDirectPm4ResolveSurfaceTextureLinkLocked(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& source,
    const PlumeTrackedResource* destination, uint32_t destination_base,
    uint32_t normalized_base, uint32_t source_slot, bool depth_resolve);
uint32_t DirectResolveCopyDestinationBase(
    const FableDirectRenderCommand& command);
uint32_t DirectResolveCopyControl(const FableDirectRenderCommand& command);
DirectResolveCopySourceSelection SelectDirectResolveCopySourceLocked(
    const FableDirectRenderCommand& command);
PlumeTrackedResource* EnsureDirectResolveDestinationResourceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const DirectResolveCopySourceSelection& selection,
    uint32_t destination_base, uint32_t normalized_base,
    bool rematerialize_incompatible = false);
DirectResolveCopyRecordResult RecordDirectResolveCopyCommandLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command);
void AddDirectResourceInterest(std::unordered_set<uint32_t>& resources,
                               uint32_t address);
void AddDirectShaderIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter, uint32_t shader);
void AddDirectExactTextureActionInterestsLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
void AddDirectVertexDeclarationIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter, uint32_t declaration);
bool ShouldReplaceTrackedSceneTarget(uint32_t candidate_width,
                                        uint32_t candidate_height,
                                        uint32_t remembered_width,
                                        uint32_t remembered_height,
                                        uint32_t candidate_target,
                                        uint32_t remembered_target);
bool IsFableTitleGuiQuadDeclaration(uint32_t declaration);
bool IsFableTitleGuiSingleTexturePostprocessPixelShader(uint32_t shader);
uint32_t FableTitleGuiFetchBackedTextureSlotLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandWritesTitleVisibleTargetLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandIsExactTitleBillboardAtlasLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandIsTitleRepeatedMeshOverlayLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandIsTitleGuiStripProofCandidateLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandIsTitleGuiOverdrawVisualiseLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandIsTitleVisibleProofCandidateLocked(
    const FableDirectRenderCommand& command);
uint32_t DirectCommandPixelTextureSlotMaskLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandSlotUsesReadyInlinePixVideoPlaneLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    uint32_t texture_guest);
void AddDirectCommandResourceInterest(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    bool include_exact_texture_action_interests = true);
void AddDirectFramebufferResourceInterest(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter);
bool RecordDirectPresentBlitActionPacketLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const RefiiFrameActionPacket& action,
    DirectPresentSource& source);
bool RefiiSurfaceDestinationActionMatchesRelation(
    const RefiiSurfaceDestinationPacket& packet,
    const FableSurfaceDestinationTextureRelation& relation);
const FableSurfaceDestinationTextureRelation*
ResolveRefiiSurfaceDestinationActionRelationLocked(
    const RefiiSurfaceDestinationPacket& packet);
void AddRefiiFrameActionResourceInterestsLocked(
    const RefiiFrameActionPacket& action,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
uint32_t RecordDirectPacketQueueFrameActionsLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderFramebuffer* output_framebuffer,
    plume::RenderFormat output_depth_format,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const RefiiFramePacketQueueSnapshot& queue);
void RecordDirectPresentBlitLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    plume::RenderTexture* output_texture,
    plume::RenderFramebuffer* present_framebuffer,
    plume::RenderShaderFormat shader_format,
    const FableDirectStateSnapshot& frame_snapshot);
void TransitionDirectFramebufferForWrite(
    plume::RenderCommandList* command_list,
    const DirectFramebufferBinding& binding);
void NameTrackedBuffer(plume::RenderBuffer* buffer,
                       const FableGuestResourceSnapshot& snapshot,
                       PlumeTrackedResource& tracked);
void PopulateTrackedIdentity(PlumeTrackedResource& tracked,
                             const FableGuestResourceSnapshot& snapshot);
const FableSurfaceTextureLink*
FindDirectExactColorCopyDestinationLinkForTextureLocked(
    const FableGuestResourceSnapshot& snapshot,
    const PlumeTrackedResource& decoded_shape);
void ApplyDirectExactCopyDestinationCreateHintLocked(
    const FableGuestResourceSnapshot& snapshot,
    PlumeTrackedResource& decoded_shape);
bool IsFableTitleBillboardTileUploadAddressForResourceBase(
    uint32_t address, uint32_t base, const PlumeTrackedResource& resource);
uint32_t FableObservedTitleBillboardTileObjectAddressForBaseLocked(
    uint32_t base, uint32_t width, uint32_t height);
void PreserveFableTitleBillboardAtlasUploadIdentityLocked(
    PlumeTrackedResource& tracked, uint32_t previous_base,
    const std::array<uint32_t, 6>& previous_fetch_dwords);
void PreserveFableTitleBillboardTileUploadAliasLocked(
    const FableGuestResourceSnapshot& replacement_snapshot,
    PlumeTrackedResource& old_resource);
void PreserveDirectPrivateTextureUploadAliasLocked(
    const FableGuestResourceSnapshot& replacement_snapshot,
    PlumeTrackedResource& old_resource);
bool SyncBufferResourceLocked(plume::RenderDevice* device,
                              const FableGuestResourceSnapshot& snapshot,
                              PlumeTrackedResource& tracked,
                              DirectResourceSyncBreakdown* sync_timing);
bool SyncImageResourceLocked(plume::RenderDevice* device,
                             const FableGuestResourceSnapshot& snapshot,
                             PlumeTrackedResource& tracked,
                             DirectResourceSyncBreakdown* sync_timing);
bool SyncDeclarationResourceLocked(const FableGuestResourceSnapshot& snapshot,
                                   PlumeTrackedResource& tracked,
                                   DirectResourceSyncBreakdown* sync_timing);
bool SyncShaderResourceLocked(const FableGuestResourceSnapshot& snapshot,
                              PlumeTrackedResource& tracked,
                              DirectResourceSyncBreakdown* sync_timing);
void RegisterDirectShaderAndDeclarationIdentityFromSnapshotLocked(
    const FableGuestResourceSnapshot& snapshot);
bool EnsureDirectShaderResourceWithKnownHashLocked(
    uint32_t shader_address, FableGuestResourceKind kind, uint64_t hash);
bool EnsureDirectShaderResourceFromObservedIdentityLocked(
    uint32_t shader_address);
bool EnsureDirectShaderResourceFromRegistryRecordLocked(
    uint32_t shader_address);
bool EnsureDirectVertexDeclarationResourceFromObservedIdentityLocked(
    uint32_t declaration);
bool EnsureDirectVertexDeclarationResourceFromRegistryRecordLocked(
    uint32_t declaration);
bool EnsureDirectKnownVertexDeclarationResourceLocked(uint32_t declaration);
bool EnsureDirectKnownShaderResourceLocked(uint32_t shader_address);
bool DirectTextureIdentityStructureMatches(
    const FableTextureIdentity& lhs, const FableTextureIdentity& rhs);
bool DirectTextureIdentityCacheCoversRegistration(
    const FableTextureIdentity& cached, const FableTextureIdentity& identity);
bool SyncTrackedResourceLocked(plume::RenderDevice* device,
                               const FableGuestResourceSnapshot& snapshot,
                               DirectResourceSyncBreakdown* sync_timing);
void LogDirectResourceSyncFailureLocked(
    const FableGuestResourceSnapshot& snapshot, const char* context);
bool DirectResourceMatchesAddress(const PlumeTrackedResource& resource,
                                  uint32_t address);
bool RecordPendingTextureUploadLocked(plume::RenderCommandList* command_list,
                                      PlumeTrackedResource& tracked);
void RecordNullTextureContentInitLocked(plume::RenderCommandList* command_list);
void RecordPendingTextureUploadsLocked(
    plume::RenderCommandList* command_list,
    const std::unordered_set<uint32_t>* texture_upload_filter = nullptr);
void RecordPendingSurfaceContentCarriesLocked(
    plume::RenderCommandList* command_list);
void InvalidateDirectResourceMemoKeyLocked(uint32_t address);
void InvalidateDirectResourceMemosForResourceLocked(
    const PlumeTrackedResource& resource);
void ClearDirectResourceMemosLocked();
void AccumulateGtlResolveFbMs(double elapsed_ms);
void AccumulateGtlResolveDrawMs(double elapsed_ms);
void AccumulateDestScanCensus(uint64_t scanned, uint64_t relevant);
void InvalidateDirectResourceFastCacheLocked();
PlumeTrackedResource* FindDirectResourceLocked(uint32_t address);
PlumeTrackedResource* FindDirectResourceByExactObjectLocked(uint32_t address);
bool DirectSurfaceAliasDestinationMatchesAddress(
    const RendererSurfaceCopyAliasSnapshot& alias, uint32_t address,
    const PlumeTrackedResource* sampled_resource);
PlumeTrackedResource* FindDirectSurfaceAliasSourceLocked(uint32_t address);
PlumeTrackedResource* FindDirectResolveAliasSourceLocked(uint32_t address);
PlumeTrackedResource* ResolveDirectSampledTextureLocked(
    uint32_t address, bool allow_alias_lookup = true);
std::array<uint32_t, 6> DirectCommandTextureFetchDwords(
    const FableDirectRenderCommand& command, uint32_t slot);
bool RegisterDirectTextureIdentityFromResourceLocked(
    const PlumeTrackedResource& resource,
    const std::array<uint32_t, 6>& fetch_dwords);
bool DirectTextureFetchDwordsMatch(
    const std::array<uint32_t, 6>& lhs,
    const std::array<uint32_t, 6>& rhs);
bool DirectMutablePrivateFetchCandidateHasContent(
    const PlumeTrackedResource& resource);
PlumeTrackedResource* FindDirectReadyUploadedTextureByFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords,
    const PlumeTrackedResource* skip_resource,
    bool* used_identity_bucket = nullptr);
PlumeTrackedResource* FindDirectResolveDestinationByFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords, uint32_t preferred_guest,
    bool* ambiguous = nullptr, ResolveDestLookupStats* stats = nullptr);
PlumeTrackedResource* FindDirectReadyUploadedTextureByBaseLocked(
    uint32_t base_address, uint32_t proxy_width, uint32_t proxy_height,
    uint32_t proxy_format, const PlumeTrackedResource* skip_resource,
    bool* used_identity_bucket = nullptr);
PlumeTrackedResource* FindDirectReadyUploadedTextureByBaseLocked(
    uint32_t base_address,
    const PlumeTrackedResource& proxy,
    bool* used_identity_bucket = nullptr);
DirectTextureFetchInfo DirectTextureFetchInfoFromDwords(
    const std::array<uint32_t, 6>& dwords);
void PrepareDirectExactCopyDestinationsForCommandLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command);
bool DirectTitleBillboardCommandHasAuthoritativeAtlasSampleLocked(
    const FableDirectRenderCommand& command);
bool DirectTrackedResourceLooksLikeBinkPlaneSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& resource);
PlumeTrackedResource* FindDirectExactBinkPlaneResourceForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectCommandHasReadyExactBinkWrapperPlanesLocked(
    const FableDirectRenderCommand& command);
bool DirectBinkWrapperPlaneAddressSetReadyLocked(
    const std::array<uint32_t, 3>& planes);
bool DirectBinkWrapperPlaneResourceSetReadyLocked(
    const FableDirectRenderCommand& command,
    const std::array<PlumeTrackedResource*, 3>& planes);
bool DirectBinkWrapperCommandPlaneSetReadyLocked(
    const FableDirectRenderCommand& command,
    std::array<uint32_t, 3>& ready_planes);
void LogExactBinkIndexedWrapperFilterLocked(
    const FableDirectRenderCommand& command, std::string_view reason,
    const DirectTextureFetchInfo* y = nullptr,
    const DirectTextureFetchInfo* cr = nullptr,
    const DirectTextureFetchInfo* cb = nullptr);
bool DirectCommandIsExactBinkIndexedWrapperDrawLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandSlotUsesExactBinkWrapperPlaneLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectCommandLooksLikeBinkIndexedWrapperCandidateLocked(
    const FableDirectRenderCommand& command);
bool DirectResourceMatchesExactBeginVerticesTitleStripFetch(
    const PlumeTrackedResource* resource,
    const DirectTextureFetchInfo& fetch_info,
    const std::array<uint32_t, 6>& fetch_dwords);
bool DirectResourceIsExactBeginVerticesTitleStripSampleLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource* resource);
bool DirectTrackedResourceMatchesExactBinkPlaneSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const DirectTextureFetchInfo& fetch_info,
    const PlumeTrackedResource& resource);
PlumeTrackedResource* ResolveDirectExactBinkWrapperPlaneForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
void AddDirectExactBinkWrapperPlaneResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
bool IsFableExactTitleBillboardTileUploadIdentity(
    uint32_t command_guest, const DirectTextureFetchInfo& command_fetch_info,
    const PlumeTrackedResource& candidate);
void LogTitleTextureSlotDiagnosticLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer, uint32_t texture_slot_mask,
    double draw_record_ms, double title_sync_ms, double alias_ms);
PlumeTrackedResource* FindDirectReadyRegisteredTextureByFetchDwordsLocked(
    const std::array<uint32_t, 6>& dwords, uint32_t preferred_guest);
uint32_t DirectRegisteredFetchPreferredGuestForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const std::array<uint32_t, 6>& dwords);
PlumeTrackedResource* ResolveFableTitleUploadedIdentityTextureLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* skip_texture);
PlumeTrackedResource* ResolveDirectSampledTextureFromFetchConstantsLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectTitleSyncFetchTextureMatchesCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& texture);
void LogDirectTitleSyncFetchTextureIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& texture, const char* message);
bool DirectCommandSlotUsesMutablePrivateHeaderExactSamplePathLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
uint64_t DirectMutablePrivateHeaderExactSampleKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    uint32_t source_texture, uint32_t source_base,
    const std::array<uint32_t, 6>& source_fetch_dwords);
bool DirectPrivateTextureCopyActionSourceMatchesCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FablePrivateTextureCopyAction& action);
void RememberDirectMutablePrivateHeaderExactSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& sample, const char* reason);
PlumeTrackedResource* ResolveDirectMutablePrivateHeaderExactSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectMutablePrivateHeaderSampleByIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture,
    bool require_exact_surface_link = false);
bool IsFableTitleBillboardAtlasResource(const PlumeTrackedResource& texture);
bool DirectSnapshotMatchesResourceFilter(
    const FableGuestResourceSnapshot& snapshot,
    const std::unordered_set<uint32_t>& resource_filter);
bool IsFableDirectTitleBillboardAtlasSnapshot(
    const FableGuestResourceSnapshot& snapshot);
bool IsFableExactTitleUploadedSnapshot(
    const FableGuestResourceSnapshot& snapshot);
bool IsFableExactTitleGuiTextAtlasSnapshot(
    const FableGuestResourceSnapshot& snapshot);
bool DirectCommandTextureSlotHasExactTitleGuiTextAtlasIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FableTextureIdentity* identity,
    const DirectTextureFetchInfo& fetch_info);
bool IsFableTitleGuiTextAtlasCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
PlumeTrackedResource* ResolveFableTitleGuiTextAtlasTextureLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectResourceAddressMatchesLocked(uint32_t resource_address,
                                        uint32_t address);
const plume::RenderTexture* DirectResourceNativeTexture(
    const PlumeTrackedResource& resource);
bool DirectResourceAddressMatchesCommandTargetLocked(
    const FableDirectRenderCommand& command, uint32_t address);
bool DirectCommandBindsExactResourceAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource);
bool DirectCommandBindsNativeImageAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource);
bool DirectCommandBindsResourceAsTargetLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& resource);
bool DirectCommandWritesSurfaceAliasSourceLocked(
    const FableDirectRenderCommand& command,
    const RendererSurfaceCopyAliasSnapshot& alias);
bool FablePrivateTextureProxyRoleAllowsTarget(
    FablePrivateTextureProxyRole role,
    const PlumeTrackedResource& target);
FableTitleResourceFacts DirectTitleResourceFactsForResource(
    const PlumeTrackedResource& resource,
    const FableTextureIdentity* identity);
bool DirectResourceUploadDefinesTextureIdentity(
    const PlumeTrackedResource& resource);
bool DirectExactTitleUploadedResourceHasAuthoredPayload(
    const PlumeTrackedResource& resource);
bool DirectResourceHasTextureSampleContentForExactProducer(
    const PlumeTrackedResource& resource);
bool DirectFableTitleBillboardAtlasHasAuthoritativeUpload(
    const PlumeTrackedResource& resource);
bool DirectFableTitleIndirectionHasAuthoritativeUpload(
    const PlumeTrackedResource& resource);
bool DirectResourceIsPersistentPrivateSampleBacking(
    const PlumeTrackedResource& resource);
bool DirectTinyTitleCachedTextureCanBindLocked(
    const FableDirectRenderCommand& command, uint32_t guest,
    const PlumeTrackedResource* texture);
bool DirectTinyTitleResourceMatchesFetchInfo(
    const DirectTextureFetchInfo& fetch_info,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectTinyTitleUploadedTextureForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* skip_texture);
bool IsFablePrivateTextureChainExtent(uint32_t width, uint32_t height);
bool IsFablePrivateTextureChainResource(
    const PlumeTrackedResource& resource);
const char* DirectPrivateTextureCopyActionMatchName(
    DirectPrivateTextureCopyActionMatch match);
void CountDirectPrivateTextureCopyActionMatchLocked(
    DirectPrivateTextureCopyActionMatch match);
bool NearlyEqualPrivateTextureActionFloat(float lhs, float rhs, float epsilon);
bool DirectPrivateTextureCopyActionHasExactWrapperIdentity(
    const FablePrivateTextureCopyAction& action);
void PopulateDirectPrivateTextureCopyShaderRoots(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command);
void PopulateDirectPrivateTextureCopyRectangleFromCommand(
    FablePrivateTextureCopyAction& action,
    const FableDirectRenderCommand& command);
void PopulateDirectSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& link,
    const FablePrivateTextureCopyAction& action);
void PopulateDirectSurfaceTextureLinkShaderRoots(
    FableSurfaceTextureLink& link,
    const FableDirectRenderCommand& command);
bool DirectPrivateTextureCopyActionWrapperMatchesCommand(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
bool DirectPrivateTextureCopyActionSourceIsTitleChainLocked(
    const FablePrivateTextureCopyAction& action);
bool DirectCommandTextureSlotHasPrivateTitleIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandRequiresExactPrivateTitleCopyActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, bool prefer_fetch_actions);
bool DirectPrivateTextureCopyActionMatchesTitleScaleUpLocked(
    const FableDirectRenderCommand& /*command*/,
    const FablePrivateTextureCopyAction& /*action*/, float /*rect_width*/,
    float /*rect_height*/);
bool DirectPrivateTextureCopyActionMatchesTitleScaleDownLocked(
    const FableDirectRenderCommand& /*command*/,
    const FablePrivateTextureCopyAction& /*action*/, float /*rect_width*/,
    float /*rect_height*/);
bool DirectPrivateTextureCopyActionIsTitleScaleUpLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
bool DirectPrivateTextureCopyActionIsTitleScaleDownLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
bool DirectPrivateTextureCopyActionTargetMatchesTitleHalfTargetLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action, uint32_t target_width,
    uint32_t target_height);
bool DirectPrivateTextureCopyActionRectangleMatchesTitleClampLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action, float rect_width,
    float rect_height);
bool DirectPrivateTextureCopyActionMatchesPendingTitleClampLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action, float rect_width,
    float rect_height);
bool DirectPrivateTextureCopyActionShapeMatches(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
bool DirectPrivateTextureCopyActionCommandSlotMatches(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
bool DirectPrivateTextureCopyActionContextMatches(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
const FablePrivateTextureCopyAction*
FindDirectPrivateRectMaterializationActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* source, const PlumeTrackedResource& target);
PlumeTrackedResource* FindDirectPrivateRectUploadedActionSourceLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource* source);
PlumeTrackedResource* FindDirectPrivateRectExactUploadedBackingLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* source);
bool DirectCommandUsesFablePrivateTexturePath(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool EnsureDirectPrivateTextureHeaderResourceLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot);
void EnsureMissingDirectPrivateTextureHeaderResourcesForCommandLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command);
void PreflightDirectPrivateRectCopyActionsLocked(
    plume::RenderDevice* device,
    const std::vector<FableDirectRenderCommand>& commands);
bool DirectPrivateTextureHeaderNeedsAlias(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectPrivateTextureCopyActionShouldPreferFetchLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
uint64_t DirectPrivateTextureCopySerialLocked();
uint64_t DirectPrivateTextureCopyActionCacheKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
uint64_t DirectPrivateTextureCopyActionSampleCacheKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    const FablePrivateTextureCopyAction& action);
const FablePrivateTextureCopyAction*
SelectDirectExactPrivateTextureCopyActionForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    DirectPrivateTextureCopyActionMatch* action_match);
bool DirectDepthTargetHasExactPrivateRectActionLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& depth_target);
bool DirectCommandCanBindMaterializedPrivateTextureHeaderLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandCanSnapshotPrivateTextureFeedbackLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* source);
bool DirectCommandCanBootstrapRegisteredPrivateRectMaterializationLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* FindDirectReadyUploadedTextureByIdentityLocked(
    const FableDirectRenderCommand& command,
    const FableTextureIdentity* identity,
    const PlumeTrackedResource* skip_resource);
PlumeTrackedResource* FindDirectReadyUploadedTextureByIdentityObjectsLocked(
    const FableDirectRenderCommand& command,
    const std::vector<uint32_t>* identity_objects,
    const PlumeTrackedResource* skip_resource);
bool DirectPrivateTextureFetchAliasIsAmbiguousHeaderProxyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* proxy, const PlumeTrackedResource* alias,
    DirectPrivateTextureResolveSource source);
PlumeTrackedResource* ResolveDirectPrivateTextureIdentityLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    DirectPrivateTextureResolveSource* source = nullptr);
bool DirectPrivateTextureCopyActionAllowsUploadedSource(
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind);
void RememberDirectPrivateTextureSiblingAliasAddressLocked(
    uint32_t source_guest, const PlumeTrackedResource& target);
void RememberDirectPrivateTextureSiblingAliasLocked(
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
void RememberDirectPrivateTextureAliasIndexLocked(
    uint32_t source_guest, uint32_t source_base,
    const std::array<uint32_t, 6>& source_fetch_dwords,
    const PlumeTrackedResource& target,
    bool include_equivalent_identity_aliases);
bool DirectPrivateTextureBackingCandidateValidLocked(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target, const PlumeTrackedResource* candidate);
bool DirectCurrentPrivateTextureActionCanSampleTargetSurfaceLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
void PopulateDirectPrivateTextureCopyBackingLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& backing);
bool DirectPrivateTextureCopyActionCanSampleUploadedSource(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind);
void PopulateDirectPrivateTextureCopySourceLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source);
void PopulateDirectPrivateTextureCopyTargetLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& target);
bool RefreshDirectSetupFlushPrivateCopyActionGpuSurfaceSampleLocked(
    FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& target);
void PopulateDirectSurfaceTextureLinkBackingLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& backing);
void PopulateDirectSurfaceTextureLinkSourceLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& source);
void PopulateDirectSurfaceTextureLinkGpuSurfaceSampleLocked(
    FableSurfaceTextureLink& link, const PlumeTrackedResource& source);
void TrackDirectExactSurfaceAliasForPrivateCopyActionLocked(
    const FablePrivateTextureCopyAction& action, const char* reason);
bool DirectPrivateTextureActionHasExactGpuSurfaceNoBackingLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
PlumeTrackedResource*
ResolveDirectPrivateTextureActionBackingFromOutputActionsLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
uint64_t DirectPrivateTextureBackingMissCacheKeyLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
bool FindCachedDirectPrivateTextureBackingMissLocked(
    uint64_t cache_key, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target);
void RememberDirectPrivateTextureBackingMissLocked(
    uint64_t cache_key, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target);
bool DirectPrivateFeedbackEnsureMemoHitLocked(
    uint64_t memo_key, const PlumeTrackedResource* source,
    const PlumeTrackedResource* target);
void RememberDirectPrivateFeedbackEnsureMemoLocked(
    uint64_t memo_key, const PlumeTrackedResource* source,
    bool source_is_bound_target, const PlumeTrackedResource* target);
PlumeTrackedResource* ResolveDirectPrivateTextureActionBackingLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
void RegisterDirectSurfaceTextureLinkForPrivateCopyActionLocked(
    const FablePrivateTextureCopyAction& action, const char* reason);
void RegisterDirectScaledPrivateRectSurfaceTextureLinkLocked(
    const FablePrivateTextureCopyAction& action, const char* reason);
void RememberDirectPrivateTextureCopyActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind, bool allow_materialized_surface_link);
void RememberDirectMaterializedPrivateSourceSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind, const char* reason,
    const PlumeTrackedResource* known_backing, bool backing_resolved);
bool RememberDirectExactPrivateRectSurfaceTextureEdgesLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target, FablePrivateTextureCopyKind kind);
void RememberMaterializedPrivateRectTargetSourcesLocked(
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& target);
uint64_t DirectPrivateFeedbackAliasKey(uint32_t source_guest,
                                       const PlumeTrackedResource& source,
                                       const PlumeTrackedResource& target);
uint64_t DirectPrivateFeedbackAliasTargetIdentityKey(
    uint32_t source_guest, const PlumeTrackedResource& source,
    const PlumeTrackedResource& target);
uint64_t DirectMaterializedPrivateRectActionFrameKey(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind);
uint64_t DirectStablePrivateCopyActionKey(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    bool include_wrapper_sample_identity, uint64_t domain);
bool RememberDirectStablePrivateCopyActionKeyLocked(uint64_t stable_key);
uint64_t DirectPrivateCopyActionContentEdgeKey(
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
uint64_t DirectPrivateRectExactActionLookupKey(
    uint32_t slot, uint32_t source_texture, uint32_t source_base_address,
    const std::array<uint32_t, 6>& source_fetch_dwords, uint32_t source_width,
    uint32_t source_height, uint32_t source_format, uint32_t target_surface,
    uint32_t render_target, uint32_t depth_stencil, uint32_t vertex_shader,
    uint32_t pixel_shader, FablePrivateTextureCopyKind kind,
    uint64_t vertex_shader_root_call_serial = 0,
    uint64_t vertex_shader_root_context_key = 0,
    uint32_t vertex_shader_root_symbol_address = 0,
    uint32_t vertex_shader_root_lr = 0,
    uint64_t pixel_shader_root_call_serial = 0,
    uint64_t pixel_shader_root_context_key = 0,
    uint32_t pixel_shader_root_symbol_address = 0,
    uint32_t pixel_shader_root_lr = 0);
uint64_t DirectPrivateRectExactActionLookupKey(
    const FablePrivateTextureCopyAction& action);
uint64_t DirectPrivateRectExactActionLookupKey(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    FablePrivateTextureCopyKind kind);
uint32_t DirectPrivateRectExactTargetShapeKey(uint32_t width, uint32_t height,
                                              uint32_t format);
uint32_t DirectPrivateRectExactTargetShapeKey(
    const PlumeTrackedResource& target);
bool DirectPrivateRectExactActionCacheEntryEquivalent(
    const FablePrivateTextureCopyAction& existing,
    const FablePrivateTextureCopyAction& action);
void StoreDirectPrivateRectExactActionLocked(
    uint64_t key, const FablePrivateTextureCopyAction& action);
void RememberDirectPrivateRectExactActionEndpointLocked(
    const FablePrivateTextureCopyAction& action, uint32_t source_texture,
    uint32_t source_base_address,
    const std::array<uint32_t, 6>& source_fetch_dwords,
    uint32_t source_width, uint32_t source_height, uint32_t source_format,
    uint32_t target_surface, bool allow_render_target_wildcard);
void RememberDirectPrivateRectExactActionLocked(
    const FablePrivateTextureCopyAction& action);
const FablePrivateTextureCopyAction*
FindDirectPrivateRectExactActionLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
void RememberDirectPrivateFeedbackAliasKeyLocked(uint64_t key,
                                                uint32_t alias_address);
void RememberDirectPrivateFeedbackAliasTargetIdentityKeyLocked(
    uint64_t key, uint32_t alias_address);
PlumeTrackedResource* ResolveDirectRegisteredFeedbackSurfaceLinkSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target);
bool DirectPrivateFeedbackBackingActionAlreadyRegisteredLocked(
    const FablePrivateTextureCopyAction& backed_action);
void RememberDirectPrivateFeedbackActionBackingLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction* exact_action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    const PlumeTrackedResource& alias);
void RememberDirectPrivateFeedbackSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t source_slot,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    const PlumeTrackedResource& alias);
bool DirectPrivateRectExactSurfaceLinkCoveredByExactLinkLocked(
    const FableDirectRenderCommand& command, const FableSurfaceTextureLink& link);
bool DirectPrivateRectSetupFlushActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link);
bool DirectPrivateRectSiblingActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link);
bool DirectPrivateRectCompactActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link);
bool DirectPrivateRectDrawHelperActionCanOwnExactSurfaceLinkLocked(
    const FablePrivateTextureCopyAction& action,
    const FableSurfaceTextureLink& link);
void RememberDirectPrivateRectActionExactSurfaceLinkLocked(
    const FableDirectRenderCommand& command, uint32_t source_slot,
    const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource& source, const PlumeTrackedResource& target,
    const char* reason);
PlumeTrackedResource* EnsureDirectPrivateRectFeedbackAliasLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    uint32_t source_slot = kFablePrivateRectCopyTextureSlot);
bool FindCachedDirectPrivateTextureCopyActionSampleLocked(
    uint64_t cache_key, const FablePrivateTextureCopyAction& action,
    PlumeTrackedResource** sample);
void RememberDirectPrivateTextureCopyActionSampleLocked(
    uint64_t cache_key, const FablePrivateTextureCopyAction& action,
    const PlumeTrackedResource* sample);
PlumeTrackedResource *ResolveDirectPrivateTextureCopyActionLocked(
    const FableDirectRenderCommand &command, uint32_t slot,
    PlumeTrackedResource *texture);
bool IsFableFinalTitlePrivateIndirectionCommandLocked(
    const FableDirectRenderCommand& command);
bool IsFableTitleBillboardAtlasCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool DirectFableTitleBillboardRegisteredFetchAtlasMatchesCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource& candidate);
PlumeTrackedResource* FindExactFableTitleBillboardAtlasForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandSlotUsesExactTitleUploadSourceLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
PlumeTrackedResource* ResolveDirectExactTitleUploadSourceForCommandSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectAuthoritativeTitleSyncSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* RegisterFableTitleBillboardBackingLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, const char* source_name);
bool IsFableTitleBillboardCommandHeaderAddress(uint32_t address);
PlumeTrackedResource* ResolveFableTitleBillboardAtlasUploadSourceForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource*
ResolveDirectDestinationEdgeSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource*
ResolveDirectPrivateCopyOutputSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource*
ResolveDirectExactProducerSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectRegisteredSampleSourceLocked(
    const FableDirectRenderCommand& command,
    const FableRegisteredSampleSource& sampled_source);
bool DirectSurfaceTextureLinkIsRetiredByNewerExactEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link,
    FableSurfaceTextureLink* versioned_retired_sibling_shell = nullptr);
PlumeTrackedResource* ResolveDirectSurfaceTextureLinkResourceLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
bool DirectSurfaceTextureLinkTouchesFablePrivateChain(
    const FableSurfaceTextureLink& link,
    const PlumeTrackedResource* sample);
bool DirectSurfaceTextureLinkIsCurrentTargetSelfEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
bool DirectExactPrivateDepthSurfaceFamilyMatchesCurrentTargetLocked(
    const FableSurfaceTextureLink& link, uint32_t surface_address,
    uint32_t depth_target_address);
bool DirectSurfaceTextureLinkIsCurrentTargetExactEdgeLocked(
    const FableDirectRenderCommand& command,
    const FableSurfaceTextureLink& link);
void LogDirectPrivateChainSurfaceTextureLinkLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const FableSurfaceTextureLink& link, const PlumeTrackedResource& sample,
    const char* source_name);
PlumeTrackedResource* ResolveDirectTrustedSurfaceTextureEdgeLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectCommandTextureSlotExactProducerLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectSampledTextureForCommandLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
bool IsFableTitleSceneTextureSyncCommandLocked(
    const FableDirectRenderCommand& command);
bool DirectPrivateTextureCopyActionProvidesSampleLocked(
    const FableDirectRenderCommand& command,
    const FablePrivateTextureCopyAction& action);
bool DirectSyntheticPrivateRectCopyHasRegisteredInputsLocked(
    const FableDirectRenderCommand& command);
bool DirectSyntheticPrivateRectDepthActionCanExecuteLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture);
bool DirectSyntheticPrivateRectColorActionCanExecuteLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* direct_texture);
bool DirectSyntheticPrivateRectCopyCanExecuteFromConcreteSourceLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandHasExactSyntheticPrivateRectCopyInputsLocked(
    const FableDirectRenderCommand& command);
uint64_t DirectRegisteredPrivateRectScaledDecisionCacheKey(
    const FableDirectRenderCommand& command, const PlumeTrackedResource& target,
    uint32_t slot);
bool DirectRegisteredPrivateRectExecuteDecisionCacheHit(uint64_t cache_key);
void RememberDirectRegisteredPrivateRectExecuteDecision(uint64_t cache_key);
std::unique_ptr<plume::RenderBuffer> CreateUploadBufferWithData(
    plume::RenderDevice* device, const void* data, size_t size,
    const char* name,
    plume::RenderBufferFlags flags = plume::RenderBufferFlag::NONE);
size_t AlignDirectConstantOffset(size_t offset);
bool EnsureDirectConstantChunkMappedLocked(DirectConstantArenaChunk& chunk);
DirectConstantAllocation AllocateDirectConstantsLocked(
    plume::RenderDevice* device, const void* data, size_t size);
void ResetDirectConstantArenaForFrameLocked(uint32_t frame_index);
void FlushDirectConstantArenaLocked();
void DestroyDirectConstantArenaLocked();
void TrimCachedUploadBuffersLocked();
plume::RenderBuffer* FindCachedUploadBufferForKeyLocked(
    const DirectUploadBufferKey& key, const void* data, size_t size);
plume::RenderBuffer* GetOrCreateCachedUploadBufferForKeyLocked(
    plume::RenderDevice* device, const DirectUploadBufferKey& key,
    const void* data, size_t size, const char* name,
    plume::RenderBufferFlags flags);
plume::RenderBuffer* GetOrCreateCachedUploadBufferWithDataLocked(
    plume::RenderDevice* device, const void* data, size_t size,
    const char* name,
    plume::RenderBufferFlags flags = plume::RenderBufferFlag::NONE);
void RetainFrameUploadBuffersLocked(
    std::vector<std::unique_ptr<plume::RenderBuffer>> buffers);
void DecodeGtlTessDrawShape(const FableDirectGtlTessDrawCommand& tess,
                            uint32_t& n_out, uint32_t& patches_out,
                            uint32_t& mode_out);
uint32_t GtlTessIndexOffset(const FableDirectGtlTessDrawCommand& tess);
void FillDirectSharedConstants(const FableDirectRenderCommand& command,
                               const DirectFramebufferBinding& framebuffer,
                               std::array<uint8_t,
                                          kDirectGeneratedSharedConstantsBytes>&
                                   shared);
bool IsFableTitleToneMapDrawLocked(const FableDirectRenderCommand& command);
bool InferExactBinkWrapperVertexExtentLocked(
    const FableDirectRenderCommand& command, float& width, float& height);
void ApplyExactBinkWrapperVertexTransformLocked(
    const FableDirectRenderCommand& command,
    std::array<uint32_t, kRendererShaderFloatConstantDwords>& constants);
bool BindDirectDrawConstantsLocked(plume::RenderDevice* device,
                                   plume::RenderCommandList* command_list,
                                   const FableDirectRenderCommand& command,
                                   const DirectFramebufferBinding& framebuffer);
DirectPipelineKey BuildDirectPipelineKey(
    const FableDirectRenderCommand& command,
    const std::vector<plume::RenderInputSlot>& input_slots,
    const std::vector<plume::RenderInputElement>& input_elements,
    const DirectFramebufferBinding& framebuffer, bool force_no_cull);
std::string DirectPipelineDiagnosticKey(
    const char* reason, const FableDirectRenderCommand& command);
void LogDirectPipelineNotReadyLocked(
    const char* reason, const FableDirectRenderCommand& command,
    const PlumeTrackedResource* vertex_shader,
    const PlumeTrackedResource* pixel_shader,
    const PlumeTrackedResource* declaration, bool synthetic_private_rect,
    size_t layout_slot_count = SIZE_MAX,
    size_t layout_element_count = SIZE_MAX);
const std::vector<plume::RenderInputElement>& DirectVertexShaderInputsLocked(
    const PlumeTrackedResource& vertex_shader);
void AlignDirectInputLayoutToVertexShaderLocked(
    const PlumeTrackedResource& vertex_shader,
    std::vector<plume::RenderInputSlot>& input_slots,
    std::vector<plume::RenderInputElement>& input_elements);
void ClearDirectPipelineEnsureMemoLocked();
plume::RenderPipeline* EnsurePipelineForDirectDrawLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer);
std::unique_ptr<plume::RenderBuffer> CreateInlineDrawBufferLocked(
    plume::RenderDevice* device, const FableDirectInlineBuffer& inline_buffer,
    bool index_buffer, uint32_t slot);
plume::RenderBuffer* EnsureDirectDefaultVertexInputBufferLocked(
    plume::RenderDevice* device);
uint64_t DirectPixelShaderHashLocked(uint32_t shader);
std::unique_ptr<plume::RenderBuffer> CreateInlineVertexBufferLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer, uint32_t slot,
    uint32_t view_offset);
plume::RenderBuffer* GetOrCreateCachedInlineVertexBufferLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer, uint32_t slot);
bool ShouldExpandInlinePrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot);
bool ShouldExpandPrimitive8Rectangle(
    const FableDirectRenderCommand& command, uint32_t slot);
uint32_t DirectDrawVertexOrIndexCount(const FableDirectRenderCommand& command);
uint32_t DirectDrawVertexInputCount(const FableDirectRenderCommand& command);
bool DirectCommandIsTitleWrapperCopyProofDrawLocked(
    const FableDirectRenderCommand& command);
const char* ClassifyDirectRenderPassLocked(
    const FableDirectRenderCommand& command);
uint32_t ResolveFrameDatabaseIndexFormatLocked(
    const FableDirectRenderCommand& command);
FableDirectFrameDatabaseDrawPacket BuildFrameDatabaseDrawPacketLocked(
    const FableDirectRenderCommand& command);
FableDirectFrameDatabaseCommandPacket BuildFrameDatabaseCommandPacketLocked(
    const FableDirectRenderCommand& command);
RefiiClearPacket BuildRefiiClearPacket(uint64_t frame_id,
                                       const FableDirectRenderCommand& command);
RefiiResolvePacket BuildRefiiResolvePacket(
    uint64_t frame_id,
    const FableDirectRenderCommand& command);
RefiiFrameActionPacket BuildRefiiFrameActionPacket(
    uint64_t frame_id,
    const FableDirectRenderCommand& command,
    uint32_t draw_packet_index);
void CaptureFrameDatabaseDrawPacketsLocked(
    const std::vector<FableDirectRenderCommand>& commands);
uint32_t DirectMinimumVertexBufferViewBytes(
    const FableDirectRenderCommand& command, uint32_t stride);
bool DirectInlineIndexReferencedVertexCount(
    const FableDirectRenderCommand& command, uint32_t& vertex_count);
bool DirectShouldBindInlineScratchVertexFromBase(
    const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer,
    const RendererStreamBindingSnapshot& stream, uint32_t stride);
std::vector<uint8_t> CopyLiveGuestStreamBytes(uint32_t guest_address,
                                                     uint32_t byte_size);
bool DirectInlineVertexBufferCoversDraw(
    const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& inline_buffer,
    const RendererStreamBindingSnapshot& stream, uint32_t slot,
    uint32_t required_view_bytes);
uint32_t DirectPrimaryVertexCapacityLocked(
    const FableDirectRenderCommand& command);
bool DirectIndexBufferLooksValidForCommand(
    const FableDirectRenderCommand& command, const uint8_t* data,
    size_t byte_size, uint32_t stride, uint32_t vertex_capacity,
    uint32_t& max_index, bool byte_swap = false);
uint32_t DirectInlineBufferOffsetForCommandAddress(
    const FableDirectRenderCommand& command,
    const FableDirectInlineBuffer& buffer, uint32_t address);
bool BindDirectTrackedIndexBufferLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const PlumeTrackedResource& index_buffer, uint32_t vertex_capacity,
    bool validate_shadow);
bool BindDirectVertexInputLocked(plume::RenderDevice* device,
                                 plume::RenderCommandList* command_list,
                                 const FableDirectRenderCommand& command);
bool BindDirectIndexBufferLocked(plume::RenderDevice* device,
                                 plume::RenderCommandList* command_list,
                                 const FableDirectRenderCommand& command);
bool BindDirectQuadListIndexBufferLocked(plume::RenderDevice* device,
                                         plume::RenderCommandList* command_list,
                                         uint32_t vertex_count);
void TransitionDirectSampledTexturesForDrawLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer);
void RecordDirectClearCommand(plume::RenderDevice* device,
                              plume::RenderCommandList* command_list,
                              plume::RenderFramebuffer* output_framebuffer,
                              plume::RenderFormat output_depth_format,
                              const FableDirectRenderCommand& command);
void LogFailedDirectDrawLocked(const char* reason,
                               const FableDirectRenderCommand& command);
void LogPixVideoTextureBindingLocked(const FableDirectRenderCommand& command,
                                     uint32_t slot);
bool DirectPixVideoPlaneSetReadyLocked(const FableDirectRenderCommand& command,
                                       uint32_t* missing_slot,
                                       uint32_t* missing_guest);
bool DirectBinkWrapperPlaneSetReadyLocked(
    const FableDirectRenderCommand& command, uint32_t* missing_slot,
    uint32_t* missing_guest);
bool SyncPixVideoInlinePlanesForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command);
void SyncPixVideoPlanesForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command);
void SyncBinkWrapperPlanesForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command, bool wait_for_observer = true);
bool EnsurePixVideoPlanesReadyForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    FableDirectRenderCommand& command);
bool EnsureBinkWrapperPlanesReadyForDrawLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    FableDirectRenderCommand& command);
void LogSmallGuiTextureBindingsLocked(const FableDirectRenderCommand& command);
void LogPrivateRectTextureBindingLocked(const FableDirectRenderCommand& command);
DirectTextureDebugBinding DirectTextureDebugBindingForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot);
DirectTextureDebugBinding FirstDirectTextureDebugBindingLocked(
    const FableDirectRenderCommand& command);
FableTitleTextureBindingFacts TitleTextureBindingFacts(
    const DirectTextureDebugBinding& binding);
FableTitleRenderPass ClassifyFableTitleRenderPassLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const DirectTextureDebugBinding& tex_first,
    const DirectTextureDebugBinding& tex13);
void EnsureTitleSceneTargetInitializedForDrawLocked(
    plume::RenderCommandList* command_list,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer);
bool ShouldProbeTitleTextureBinding(const FableDirectRenderCommand& command,
                                    const DirectTextureDebugBinding& binding);
void LogTitleTextureBindingProbeLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const DirectTextureDebugBinding& binding);
void LogTitleSceneSampleOwnershipProofLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    uint32_t slot);
void LogExactBinkWrapperPayloadLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer,
    const PlumeTrackedResource* y,
    const PlumeTrackedResource* cr,
    const PlumeTrackedResource* cb);
void LogTitleSceneDrawLocked(const FableDirectRenderCommand& command,
                             const DirectFramebufferBinding& framebuffer);
void ResolveUnbackedTitlePrivateProxyCopyLocked(
    const FableDirectRenderCommand& command);
bool NearlyEqualExtent(float value, float expected);
bool ShouldRememberDrawnSceneTargetForCommand(
    const FableDirectRenderCommand& command);
bool IsFableTitleGuestViewportExtent(const RendererViewportSnapshot& viewport);
void ScaleFableTitleDrawStateToFramebuffer(
    FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer);
PlumeTrackedResource* FindDirectReadyShaderResourceByIdentityLocked(
    uint32_t shader, FableGuestResourceKind expected_kind);
PlumeTrackedResource* FindDirectReadyDeclarationResourceByIdentityLocked(
    uint32_t declaration);
void EnsureDirectKnownShaderResourcesForCommandLocked(
    const FableDirectRenderCommand& command);
bool IsGtlPreDispatchBeginPacket(const refii::gtl::DrawPacket& packet);
bool FlushExactSurfaceCopyForSlotLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    FableDirectRenderCommand& draw_command, uint32_t pixel_texture_slot_mask,
    uint32_t slot);
bool RecordDirectDrawCommandLocked(plume::RenderDevice* device,
                                   plume::RenderCommandList* command_list,
                                   plume::RenderFramebuffer* output_framebuffer,
                                   plume::RenderFormat output_depth_format,
                                   FableDirectRenderCommand& command,
                                   bool submitted_command_source);
std::unique_ptr<plume::RenderBuffer> CreateSyntheticPrivateRectVertexUpload(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    uint32_t slot);
plume::RenderBuffer* GetOrCreateCachedSyntheticPrivateRectVertexBufferLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command);
bool DirectShaderResourceReadyLocked(uint32_t address);
bool DirectBufferResourceReadyLocked(uint32_t address);
bool DirectTextureIdentityImageReadyLocked(const FableTextureIdentity* identity);
bool DirectImageResourceReadyLocked(uint32_t address);
void AddDirectTextureIdentityResourceInterestLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableTextureIdentity* identity);
void AddDirectTextureIdentityObjectsResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::vector<uint32_t>* objects);
void AddDirectTextureBaseIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t base_address);
void AddDirectTextureBaseAddressResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t base_address);
void AddFableTitleBillboardTileUploadAliasResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t base_address, uint32_t width, uint32_t height);
void AddDirectTextureFetchIdentityResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::array<uint32_t, 6>& fetch_dwords);
void AddDirectTextureFetchResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::array<uint32_t, 6>& fetch_dwords);
bool PublishDirectBeginVerticesTitleTextureActionForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandTextureSlotHasExactProducerOwnerProbeLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandTextureSlotHasExactRegisteredProducerLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture, bool log_misses);
bool DirectCommandTextureSlotExactProducerProvidesSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
PlumeTrackedResource* ResolveDirectExactWrapperSourceSampleForSlotLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture);
bool DirectCommandTextureSlotDirectResourceProvidesSampleLocked(
    const FableDirectRenderCommand& command, uint32_t slot,
    const PlumeTrackedResource* texture,
    bool allow_private_title_exact_sample);
void AddDirectCompactPrivateTextureCopyActionResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FablePrivateTextureCopyAction& action);
const FablePrivateTextureCopyAction*
FindDirectPrivateTextureCopyOutputActionForClearTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target);
bool IsFablePrivateTextureRectangleSetupClearLr(uint32_t lr);
bool DirectPrivateTextureSetupActionTargetExactMatchesLocked(
    uint32_t setup_target, const FablePrivateTextureCopyAction& action,
    bool depth_target);
bool DirectPrivateTextureSetupClearIntentMatchesActionTargetLocked(
    const DirectPrivateTextureSetupClearIntent& intent,
    const FablePrivateTextureCopyAction& action);
bool DirectPrivateTextureSetupClearActionMatchesActionTargetLocked(
    const DirectPrivateTextureSetupClearAction& setup_action,
    uint32_t target);
bool DirectPrivateTextureSetupClearIntentHasExactRoots(
    const DirectPrivateTextureSetupClearIntent& intent);
bool DirectPrivateTextureCopyActionHasCompleteWrapperIdentity(
    const FablePrivateTextureCopyAction& action);
bool DirectPrivateTextureCopyActionHasCompleteIdaWrapperIdentity(
    const FablePrivateTextureCopyAction& action);
uint64_t DirectPrivateTextureSetupClearIntentKey(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target);
bool DirectPrivateTextureSetupClearActionMatchesLocked(
    const DirectPrivateTextureSetupClearAction& setup_action,
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target);
void RegisterDirectPrivateTextureSetupClearSurfaceLinkLocked(
    const DirectPrivateTextureSetupClearIntent& intent,
    const FablePrivateTextureCopyAction& action, uint32_t action_target);
void RememberDirectPrivateTextureSetupClearIntentLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target);
void RememberDirectPrivateTextureSetupClearIntentsLocked(
    const std::vector<FableDirectRenderCommand>& commands);
void BackfillDirectPrivateTextureSetupClearActionsLocked(
    const FablePrivateTextureCopyAction& action);
void RegisterDirectPrivateTextureCopyActionLocked(
    const FablePrivateTextureCopyAction& action);
const FablePrivateTextureCopyAction*
FindDirectPrivateTextureCopySetupActionForClearTargetLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target);
void AddDirectClearPrivateTextureCopyOutputInterestLocked(
    const FableDirectRenderCommand& command, uint32_t target,
    bool depth_target, std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
void AddDirectSurfaceTextureLinkResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableSurfaceTextureLink* link);
void AddDirectCompactSurfaceTextureLinkResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableSurfaceTextureLink* link);
void AddDirectSurfaceTextureLinksResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::vector<FableSurfaceTextureLink>* links);
void AddDirectSurfaceDestinationRelationsResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const std::vector<FableSurfaceDestinationTextureRelation>* relations);
void AddDirectSourceSurfaceDestinationResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    uint32_t source_surface);
void AddFableTinyTitleTextureResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
void AddFableTitleBillboardAtlasResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter,
    const FableDirectRenderCommand& command, uint32_t slot);
void AddMissingDirectResourceInterest(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter, uint32_t address,
    bool texture_upload);
bool DirectCommandInputWaitReasonReady(
    const DirectCommandInputWaitReason& reason);
DirectCommandInputWaitReason FindDirectDrawNonTextureInputWaitReasonLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandInputReadyCacheEligibleLocked(
    const FableDirectRenderCommand& command);
uint64_t DirectCommandInputReadyCacheKeyLocked(
    const FableDirectRenderCommand& command);
bool FindCachedDirectCommandInputReadyLocked(uint64_t cache_key);
void RememberCachedDirectCommandInputReadyLocked(uint64_t cache_key);
bool DirectCommandNonTextureInputsReadyLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandInputsReadyWithoutMissingInterestLocked(
    const FableDirectRenderCommand& command);
void CollectDirectVideoCommandNonPlaneResourceInterestLocked(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
void CollectDirectExactTitleUploadPreflightInterestsLocked(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
void CollectDirectExactTitleUploadPreflightInterestsLocked(
    const std::vector<FableDirectRenderCommand>& commands,
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
void LogFinalFailedDirectCommandLocked(const char* phase,
                                       const FableDirectRenderCommand& command);
void LogSkippedUnsupportedDirectDrawLocked(
    const FableDirectRenderCommand& command,
    const char* reason);
bool IsRenderDocEarlyBinkWrapperDrawTargetLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandHasRenderDocTitleHandoffSampleLocked(
    const FableDirectRenderCommand& command);
bool IsRenderDocStartupVideoCopyDrawTargetLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandIsTreeBillboardDrawTargetLocked(
    const FableDirectRenderCommand& command);
DirectTitleVisibleFramePacketProbe DirectTitleVisibleFramePacketProbeLocked(
    const FableDirectRenderCommand& command);
bool DirectCommandIsTitleVisibleFramePacketCandidateLocked(
    const FableDirectRenderCommand& command);
bool IsRenderDocDrawTargetMatchLocked(
    const std::string& target,
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding* framebuffer);
bool IsRenderDocResolveTargetMatch(const std::string& target,
                                   const FableDirectRenderCommand& command);
void MaybeBeginRenderDocDrawTargetCaptureLocked(
    const FableDirectRenderCommand& command,
    const DirectFramebufferBinding* framebuffer);
void MaybeBeginRenderDocResolveTargetCaptureLocked(
    const FableDirectRenderCommand& command);
bool GtlPacketIdentityAgreesWithCommandLocked(
    const FableDirectRenderCommand& command,
    const refii::gtl::DrawPacket& packet);
FableDirectRenderCommand BuildDirectCommandFromGtlClearPacketLocked(
    const refii::gtl::ClearPacket& packet);
FableDirectRenderCommand BuildDirectCommandFromGtlResolveClearLocked(
    const refii::gtl::ResolvePacket& packet);
FableDirectRenderCommand BuildDirectCommandFromGtlDrawPacketLocked(
    const refii::gtl::DrawPacket& packet);
void NormalizeDirectTextureSlotsToObjectsLocked(
    FableDirectRenderCommand& command);
void ApplyGtlPacketIdentityAuthorityLocked(FableDirectRenderCommand& command);
void AddGtlPacketResourceInterestsLocked(
    std::unordered_set<uint32_t>& resource_filter,
    std::unordered_set<uint32_t>& texture_upload_filter);
void AddDirectCommandMissingVertexInputInterestLocked(
    const FableDirectRenderCommand& command,
    std::unordered_set<uint32_t>& resource_filter);
std::string DeriveGtlTessVariantArtifactPath(const std::string& base_path);
plume::RenderShader* EnsureGtlTessVertexShaderVariantLocked(
    plume::RenderDevice* device, uint32_t vertex_shader_object);
plume::RenderPipeline* EnsureGtlTessPipelineLocked(
    plume::RenderDevice* device, const FableDirectRenderCommand& command,
    const DirectFramebufferBinding& framebuffer);
bool BindGtlTessGridIndexBufferLocked(plume::RenderDevice* device,
                                      plume::RenderCommandList* command_list,
                                      uint32_t n);
FableDirectRenderCommand BuildDirectCommandFromGtlTessPacketLocked(
    const refii::gtl::TessellatedPatchDrawPacket& packet);
bool RecordGtlTessDrawLocked(plume::RenderDevice* device,
                             plume::RenderCommandList* command_list,
                             plume::RenderFramebuffer* output_framebuffer,
                             plume::RenderFormat output_depth_format,
                             FableDirectRenderCommand& command);
void RecordDirectRenderCommandsLocked(plume::RenderDevice* device,
                                      plume::RenderCommandList* command_list,
                                      plume::RenderFramebuffer* output_framebuffer,
                                      plume::RenderTexture* output_texture,
                                      plume::RenderFramebuffer* present_framebuffer,
                                      plume::RenderFormat output_depth_format,
                                      FableDirectFrameCommandBatch command_batch);
bool IsLiveBoundDepthTargetLocked(uint32_t guest_address,
                                  const PlumeTrackedResource& entry,
                                  uint64_t sweep_frame);
void SyncDirectResourcesLocked(
    plume::RenderDevice* device, plume::RenderCommandList* command_list,
    const std::unordered_set<uint32_t>* texture_upload_filter,
    const std::unordered_set<uint32_t>* resource_filter,
    bool wait_for_observer, bool only_changed_resource_filter);
void InitializeFableDirectRenderer(plume::RenderDevice* device,
                                   plume::RenderShaderFormat shader_format);
FableDirectRecordReason FableDirectFrameRecordReason();
void RecordFableDirectFrame(plume::RenderDevice* device,
                            plume::RenderCommandList* command_list,
                            plume::RenderFramebuffer* output_framebuffer,
                            plume::RenderTexture* output_texture,
                            plume::RenderFramebuffer* present_framebuffer,
                            plume::RenderShaderFormat shader_format,
                            plume::RenderFormat output_depth_format,
                            uint32_t frame_index);
void BeginFableDirectFrame(uint32_t frame_index);
void EndFableDirectFrame();
void PresentFableDirectFrame(uint32_t present_target, uint32_t width,
                             uint32_t height);
void ResetFableDirectLivePresentSourcesLocked(bool reset_stats);
FableDirectRendererStats GetFableDirectRendererStats();
FableDirectFrameDatabaseDrawPackets GetFableDirectFrameDatabaseDrawPackets();
FableDirectFrameDatabaseCommandPackets
GetFableDirectFrameDatabaseCommandPackets();
RefiiFramePacketDump GetRefiiFramePacketDump();
RefiiFramePacketQueueSnapshot GetRefiiFramePacketQueueSnapshot();
FableDirectResourceDebugSnapshot GetFableDirectResourceDebugSnapshot();
FableDirectRenderGraphSnapshot GetFableDirectRenderGraphSnapshot();
FableDirectRenderGraphExecutionSnapshot
GetFableDirectRenderGraphExecutionSnapshot();
bool FableDirectQueuedDrawPresent(uint64_t draw_id, size_t max_commands);
bool FableDirectQueuedRenderPassPresent(std::string_view normalized_pass_name,
                                        size_t max_commands);
bool FableDirectQueuedRenderDocTargetPresent(std::string_view target,
                                             size_t max_commands);
void ResetFableDirectLivePresentSources();
void ShutdownFableDirectRenderer();

template <typename TryAddress>
PlumeTrackedResource* TryDirectTitleUploadIdentityAddresses(
    const FableTextureIdentity* identity, const TryAddress& try_address) {
  if (!IsFableTitleUploadedTextureIdentity(identity)) {
    return nullptr;
  }
  if (PlumeTrackedResource* candidate = try_address(identity->object_texture)) {
    return candidate;
  }
  if (PlumeTrackedResource* candidate = try_address(identity->parent_texture)) {
    return candidate;
  }
  return try_address(identity->base_address);
}
template <typename TryIdentity>
PlumeTrackedResource* TryDirectTitleUploadIdentityObjects(
    const std::vector<uint32_t>* objects, const TryIdentity& try_identity) {
  if (!objects) {
    return nullptr;
  }
  for (uint32_t object : *objects) {
    if (PlumeTrackedResource* candidate =
            try_identity(FableDirectFindTextureIdentityByObject(object))) {
      return candidate;
    }
  }
  return nullptr;
}
template <typename MixFn>
void MixDirectShaderRoots(MixFn&& mix, uint64_t vertex_call_serial,
                          uint64_t vertex_context_key,
                          uint32_t vertex_symbol_address, uint32_t vertex_lr,
                          uint64_t pixel_call_serial,
                          uint64_t pixel_context_key,
                          uint32_t pixel_symbol_address, uint32_t pixel_lr) {
  mix(vertex_call_serial);
  mix(vertex_context_key);
  mix(vertex_symbol_address);
  mix(vertex_lr);
  mix(pixel_call_serial);
  mix(pixel_context_key);
  mix(pixel_symbol_address);
  mix(pixel_lr);
}
template <typename MixFn>
void MixDirectShaderRoots(MixFn&& mix,
                          const FableDirectRenderCommand& command) {
  const FableDirectShaderRoots& roots = command.resources.shader_roots;
  MixDirectShaderRoots(mix, roots.vertex_shader.call_serial,
                       roots.vertex_shader.context_key,
                       roots.vertex_shader.symbol_address,
                       roots.vertex_shader.lr,
                       roots.pixel_shader.call_serial,
                       roots.pixel_shader.context_key,
                       roots.pixel_shader.symbol_address,
                       roots.pixel_shader.lr);
}

#endif

}  // namespace refii::renderer
