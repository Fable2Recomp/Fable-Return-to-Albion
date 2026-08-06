#pragma once

#include "renderer_runtime.h"
#include "runtime/cvars.h"

#include <array>
#include <cstdint>

namespace refii::renderer {

enum class PlumeBackendStatus {
  kUnavailable,
  kUnsupportedApi,
  kInterfaceUnavailable,
  kDeviceUnavailable,
  kQueueUnavailable,
  kReady,
};

enum class PlumeFableCommandSource : uint32_t {
  kNone,
  kStaged,
  kPresented,
  kSubmitted,
};

inline constexpr uint32_t kPlumeBackendDebugDeviceNameCapacity = 128;
inline constexpr uint32_t kPlumeFrameHistoryCapacity = 64;

struct PlumeBackendDebugSnapshot {
  PlumeBackendStatus status = PlumeBackendStatus::kUnavailable;
  RendererGraphicsApi api = RendererGraphicsApi::kVulkan;
  bool attempted = false;
  bool direct_queue_ready = false;
  bool copy_queue_ready = false;
  bool swapchain_ready = false;
  bool swapchain_present_wait_enabled = false;
  uint32_t next_frame_context = 0;
  uint32_t frame_context_count = 0;
  uint32_t release_semaphore_count = 0;
  uint32_t framebuffer_count = 0;
  uint32_t depth_framebuffer_count = 0;
  uint64_t dedicated_video_memory = 0;
  std::array<char, kPlumeBackendDebugDeviceNameCapacity> device_name = {};
};

struct PlumeFrameHistoryEntry {
  bool valid = false;
  bool presented = false;
  bool renderdoc_capture_active = false;
  uint32_t frame_index = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t draw_count = 0;
  uint32_t indexed_draw_count = 0;
  uint32_t render_command_count = 0;
  uint32_t resource_upload_count = 0;
  uint32_t pipeline_cache_miss_count = 0;
  uint32_t failed_resource_sync_count = 0;
  uint32_t present_target = 0;
  int64_t frame_total_us = 0;
  int64_t direct_us = 0;
  int64_t submit_us = 0;
  int64_t present_us = 0;
};

struct PlumeFrameHistorySnapshot {
  std::array<PlumeFrameHistoryEntry, kPlumeFrameHistoryCapacity> entries = {};
  uint32_t count = 0;
};

struct PlumeOutputSnapshot {
  bool ready = false;
  bool vsync_enabled = false;
  bool configured_vsync = false;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t texture_count = 0;
  uint32_t configured_texture_count = 0;
  uint32_t present_count = 0;
  uint32_t last_fable_render_commands = 0;
  uint32_t last_fable_draw_events = 0;
  PlumeFableCommandSource last_fable_command_source =
      PlumeFableCommandSource::kNone;
  PlumeFableCommandSource last_fable_direct_source =
      PlumeFableCommandSource::kNone;
  bool last_fable_draw_events_fresh = false;
  int64_t last_marshal_us = 0;
  int64_t last_frame_total_us = 0;
  int64_t last_backend_us = 0;
  int64_t last_output_us = 0;
  int64_t last_consume_us = 0;
  int64_t last_sync_us = 0;
  int64_t last_readiness_us = 0;
  int64_t last_acquire_us = 0;
  int64_t last_acquire_texture_us = 0;
  int64_t last_setup_us = 0;
  int64_t last_imgui_build_us = 0;
  int64_t last_direct_us = 0;
  int64_t last_imgui_render_us = 0;
  int64_t last_command_record_us = 0;
  int64_t last_submit_us = 0;
  int64_t last_present_us = 0;
  int64_t last_fence_wait_us = 0;
};

const char* PlumeBackendStatusName(PlumeBackendStatus status);
const char* PlumeFableCommandSourceName(PlumeFableCommandSource source);
void RequestPlumeBackendShutdown();
void ShutdownPlumeBackend();
void NotifyPlumeBackendHook(const char* symbol, RendererHookKind kind);
bool PlumeOutputReady();
uint32_t CurrentPlumeOutputFrameIndex();
// Live swapchain extent (lock-free, two relaxed atomic loads). False until the
// swapchain exists. This is the authority for host render-surface promotion --
// see the note at the definition for why the video_mode cvars are not.
bool CurrentPlumeOutputExtent(uint32_t& width, uint32_t& height);
PlumeBackendDebugSnapshot GetPlumeBackendDebugSnapshot();
PlumeOutputSnapshot GetPlumeOutputSnapshot();
PlumeFrameHistorySnapshot GetPlumeFrameHistorySnapshot();
bool PresentPlumeFrame(bool drive_fable_frame = false);

}  // namespace refii::renderer
