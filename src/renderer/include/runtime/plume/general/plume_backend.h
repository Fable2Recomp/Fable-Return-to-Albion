#pragma once

#include "renderer_runtime.h"

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
  kPending,
  kPresented,
  kSubmitted,
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

PlumeBackendStatus EnsurePlumeBackend();
const char* PlumeBackendStatusName(PlumeBackendStatus status);
const char* PlumeFableCommandSourceName(PlumeFableCommandSource source);
void RequestPlumeBackendShutdown();
void ShutdownPlumeBackend();
void NotifyPlumeBackendHook(const char* symbol, RendererHookKind kind);
bool PlumeOutputReady();
uint32_t CurrentPlumeOutputFrameIndex();
PlumeOutputSnapshot GetPlumeOutputSnapshot();
bool PresentPlumeFrame(bool drive_fable_frame = false);

}  // namespace refii::renderer
