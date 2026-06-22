#include "runtime/plume/general/plume_backend.h"

#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/plume/direct/fable_direct_renderer.h"
#include "runtime/plume/direct/fable_guest_resource_registry.h"
#include "runtime/plume/direct/fable_direct_state.h"
#include "runtime/plume/general/plume_api_bridge.h"
#include "runtime/plume/general/plume_diagnostics.h"
#include "runtime/plume/general/plume_imgui_overlay.h"
#include "runtime/shader_library.h"

#include <atomic>
#include <algorithm>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#include <rex/logging.h>
#include <rex/runtime.h>
#include <rex/ui/window.h>
#include <tracy/Tracy.hpp>

#if !defined(REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE)
#define REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE 0
#endif
#if !defined(REFII_SDL3_WINDOW_ENABLED)
#define REFII_SDL3_WINDOW_ENABLED 0
#endif
#if REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE
#include <X11/Xlib.h>
#endif
#if REFII_SDL3_WINDOW_ENABLED && defined(__linux__)
#include <rex/ui/window_sdl.h>
#endif

#if defined(_WIN64)
#endif

#if REFII_RENDERER_HAS_PLUME
#include <plume_render_interface.h>
#endif

namespace refii::renderer {
namespace {

using PlumeClock = std::chrono::steady_clock;

double PlumeElapsedMs(PlumeClock::time_point begin,
                      PlumeClock::time_point end) {
  return std::chrono::duration<double, std::milli>(end - begin).count();
}

int64_t PlumeElapsedUs(PlumeClock::time_point begin,
                       PlumeClock::time_point end) {
  return std::chrono::duration_cast<std::chrono::microseconds>(end - begin)
      .count();
}

bool HasFableDirectFrameContent(const FableDirectStateSnapshot& snapshot) {
  return snapshot.draw_count != 0 || snapshot.indexed_draw_count != 0 ||
         snapshot.last_command_boundary_pending_draws != 0 ||
         snapshot.last_submitted_draw_event_count != 0;
}

uint64_t FableDirectShaderHash(uint32_t shader) {
  const FableShaderIdentity* identity =
      FableDirectFindShaderIdentityByObject(shader);
  return identity ? identity->bytecode_hash : 0ull;
}

bool FableDirectCommandTypeIsDraw(FableDirectRenderCommandType type) {
  return type == FableDirectRenderCommandType::kDrawPrimitive ||
         type == FableDirectRenderCommandType::kDrawIndexedPrimitive ||
         type == FableDirectRenderCommandType::kDrawPrimitiveUP;
}

bool CommandMatchesTreebillboardCaptureTarget(
    const FableDirectRenderCommand& command) {
  constexpr uint64_t kTreebillboardPixelShaderHash = 0x82B98AA62B081948ull;
  constexpr uint32_t kTreebillboardRenderTarget = 0x4011F370u;
  constexpr uint32_t kTreebillboardColorMask = 0x0000E000u;

  if (command.draw_serial == 0 ||
      !FableDirectCommandTypeIsDraw(command.type) ||
      command.draw.source != RendererDrawEventSource::kSceneObject ||
      command.resources.render_targets[0] != kTreebillboardRenderTarget ||
      command.resources.vertex_shader == 0 ||
      command.resources.pixel_shader == 0 ||
      command.resources.textures[13] == 0 ||
      command.resources.textures[13] < 0x4C000000u) {
    return false;
  }

  if (command.pipeline.pm4.valid &&
      command.pipeline.pm4.rb_color_mask != kTreebillboardColorMask) {
    return false;
  }

  const uint64_t pixel_shader_hash =
      FableDirectShaderHash(command.resources.pixel_shader);
  return pixel_shader_hash == 0 ||
         pixel_shader_hash == kTreebillboardPixelShaderHash;
}

bool QueuedCommandsContainRenderDocDrawTarget(const std::string& target) {
  if (target.empty()) {
    return false;
  }
  if (target != "treebillboard") {
    return false;
  }

  constexpr size_t kMaxRenderDocTargetCommandProbe = 4096;
  const std::vector<FableDirectRenderCommand> commands =
      FableDirectQueuedRenderCommandSnapshot(kMaxRenderDocTargetCommandProbe);
  return std::any_of(commands.begin(), commands.end(),
                     CommandMatchesTreebillboardCaptureTarget);
}

void ConfigurePlumeBackendTracyPlots() {
  static std::once_flag configure_once;
  std::call_once(configure_once, [] {
    TracyPlotConfig("Refii Plume Frame Total Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii Plume Acquire Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii Plume Resource Sync Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii Plume Readiness Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xEAB308);
    TracyPlotConfig("Refii Plume Command Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii Plume ImGui Build Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x06B6D4);
    TracyPlotConfig("Refii Plume ImGui Render Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x3B82F6);
    TracyPlotConfig("Refii Plume Command Record Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x8B5CF6);
    TracyPlotConfig("Refii Plume Submit Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0xA855F7);
    TracyPlotConfig("Refii Plume Present Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii Plume Fence Wait Us",
                    tracy::PlotFormatType::Number,
                    false,
                    false,
                    0x60A5FA);
    TracyPlotConfig("Refii Plume Draw Events",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF97316);
    TracyPlotConfig("Refii Plume Recorded Draws",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x10B981);
    TracyPlotConfig("Refii Plume Missing Resources",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xEF4444);
    TracyPlotConfig("Refii Plume Missing Pipelines",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xF43F5E);
    TracyPlotConfig("Refii Plume Filtered Draws",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFACC15);
    TracyPlotConfig("Refii Plume Texture Uploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x2DD4BF);
    TracyPlotConfig("Refii Plume Pending Texture Uploads",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xFB923C);
    TracyPlotConfig("Refii Plume Pix Video Events",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0xA78BFA);
  });
}

bool ShouldLogPlumeFrameTiming(uint32_t frame_index) {
  return frame_index <= 8 || frame_index % 120 == 0;
}

#if REFII_RENDERER_HAS_PLUME && REFII_SDL3_WINDOW_ENABLED
bool ShouldMarshalPlumeToUIThread() {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->app_context()) {
    return false;
  }
  return !runtime->app_context()->IsInUIThread();
}

bool CallPlumeOnUIThreadSynchronous(std::function<void()> function) {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->app_context()) {
    return false;
  }
  return runtime->app_context()->CallInUIThreadSynchronous(std::move(function));
}

bool CallPlumeOnUIThreadDeferred(std::function<void()> function) {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->app_context()) {
    return false;
  }
  return runtime->app_context()->CallInUIThread(std::move(function));
}
#endif

std::string BuildRenderDocCaptureTitle(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot) {
  char title[256] = {};
  std::snprintf(
      title, sizeof(title),
      "Refii Plume frame %u %ux%u draws %u/%u VS%08X PS%08X RT%08X Pix%08X",
      frame_index, width, height, direct_stats.draw_count,
      direct_stats.last_draw_events, hook_snapshot.vertex_shader,
      hook_snapshot.pixel_shader, direct_stats.last_present_target,
      hook_snapshot.last_pix_texture);
  return title;
}

std::string Hex32(uint32_t value) {
  char text[16] = {};
  std::snprintf(text, sizeof(text), "0x%08X", value);
  return text;
}

std::string BuildRenderDocCaptureComments(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const FableDirectRendererStats& direct_stats,
    const RendererHookObserverSnapshot& hook_snapshot,
    const RendererHookTimingSnapshot& timing_snapshot,
    const FableDirectStateSnapshot& direct_snapshot) {
  std::string comments;
  comments.reserve(2048);
  comments += "Refii Plume RenderDoc capture\n";
  comments += "Frame: " + std::to_string(frame_index) + "\n";
  comments += "Output: " + std::to_string(width) + "x" +
              std::to_string(height) + "\n";
  comments += "Direct draws/indexed/events: " +
              std::to_string(direct_stats.draw_count) + " / " +
              std::to_string(direct_stats.indexed_draw_count) + " / " +
              std::to_string(direct_stats.last_draw_events) + "\n";
  comments += "Direct commands/resources/uploads: " +
              std::to_string(direct_stats.last_render_commands) + " / " +
              std::to_string(direct_stats.resource_creations) + " / " +
              std::to_string(direct_stats.resource_uploads) + "\n";
  comments += "Direct live resources tex/surf/vb/ib/sh: " +
              std::to_string(direct_stats.live_textures) + " / " +
              std::to_string(direct_stats.live_surfaces) + " / " +
              std::to_string(direct_stats.live_vertex_buffers) + " / " +
              std::to_string(direct_stats.live_index_buffers) + " / " +
              std::to_string(direct_stats.live_shaders) + "\n";
  comments += "Pipeline hits/misses failed hooks/resources: " +
              std::to_string(direct_stats.pipeline_cache_hits) + " / " +
              std::to_string(direct_stats.pipeline_cache_misses) + " / " +
              std::to_string(direct_stats.failed_hooks) + " / " +
              std::to_string(direct_stats.failed_resource_syncs) + "\n";
  comments += "Present target: " + Hex32(direct_stats.last_present_target) +
              " " + std::to_string(direct_stats.last_present_width) + "x" +
              std::to_string(direct_stats.last_present_height) + "\n";
  comments += "GetRenderTarget calls/last slot/surface: " +
              std::to_string(hook_snapshot.get_render_target_calls) + " / " +
              std::to_string(hook_snapshot.last_get_render_target_slot) +
              " / " + Hex32(hook_snapshot.last_get_render_target_surface) +
              "\n";
  comments += "Pix decode/pending/render/events/capture: " +
              std::to_string(hook_snapshot.pix_decode_frames) + " / " +
              std::to_string(hook_snapshot.pix_decode_pending_frames) + " / " +
              std::to_string(hook_snapshot.pix_render_hooks) + " / " +
              std::to_string(hook_snapshot.pix_render_events) + " / " +
              std::to_string(hook_snapshot.pix_capture_hooks) + "\n";
  comments += "Pix texture packet/decode/fetch: tex=" +
              Hex32(hook_snapshot.last_pix_texture) + " pkt=" +
              Hex32(hook_snapshot.last_pix_packet) + " dec=" +
              Hex32(hook_snapshot.last_pix_decode_base) + " fetch=" +
              Hex32(hook_snapshot.last_pix_fetch_base) + "\n";
  comments += "Active shader handles VS/PS/Decl/IB: " +
              Hex32(hook_snapshot.vertex_shader) + " / " +
              Hex32(hook_snapshot.pixel_shader) + " / " +
              Hex32(hook_snapshot.vertex_declaration) + " / " +
              Hex32(hook_snapshot.index_buffer) + "\n";
  comments += "Direct state RT0/Depth/VS/PS/Tex0: " +
              Hex32(direct_snapshot.targets.color[0]) + " / " +
              Hex32(direct_snapshot.targets.depth) + " / " +
              Hex32(direct_snapshot.shaders.vertex_shader) + " / " +
              Hex32(direct_snapshot.shaders.pixel_shader) + " / " +
              Hex32(direct_snapshot.textures.textures[0]) + "\n";
  comments += "Present pump requests/advances/submitted/completed/callbacks: " +
              std::to_string(timing_snapshot.plume_present_pump_requests) +
              " / " +
              std::to_string(
                  timing_snapshot.plume_present_pump_fable_advances) +
              " / " +
              std::to_string(timing_snapshot.fable_present_submitted) + " / " +
              std::to_string(timing_snapshot.fable_present_completed) + " / " +
              std::to_string(timing_snapshot.fable_swap_callbacks) + "\n";
  return comments;
}

#if REFII_RENDERER_HAS_PLUME
struct PlumeFrameContext {
  bool submitted = false;
  std::unique_ptr<plume::RenderCommandList> command_list;
  std::unique_ptr<plume::RenderCommandSemaphore> acquire_semaphore;
  std::unique_ptr<plume::RenderCommandFence> fence;
};
#endif

struct PlumeBackendState {
  std::mutex mutex;
  PlumeBackendStatus status = PlumeBackendStatus::kUnavailable;
  RendererGraphicsApi api = RendererGraphicsApi::kVulkan;
  bool attempted = false;
  bool logged_hook_observer = false;
#if REFII_RENDERER_HAS_PLUME
  uint32_t present_count = 0;
  bool logged_frame_after_uploads = false;
  bool logged_frame_after_color_target = false;
  bool logged_frame_after_imgui_build = false;
  bool logged_frame_after_imgui_render = false;
  bool logged_frame_before_submit = false;
  bool logged_frame_after_present = false;
  bool logged_frame_after_wait = false;
  bool logged_first_frame_summary = false;
  bool logged_direct_command_stream = false;
  bool logged_submitted_command_stream = false;
  bool logged_present_pump_pending_command_stream = false;
  bool logged_reused_identical_fable_frame = false;
  bool logged_preserved_fable_direct_output = false;
  bool logged_renderdoc_draw_target_watch = false;
  std::string last_renderdoc_draw_target_capture;
  bool has_fable_direct_output_to_preserve = false;
  std::unique_ptr<plume::RenderInterface> render_interface;
  std::unique_ptr<plume::RenderDevice> device;
  std::unique_ptr<plume::RenderCommandQueue> direct_queue;
  std::unique_ptr<plume::RenderCommandQueue> copy_queue;
  std::unique_ptr<plume::RenderSwapChain> swap_chain;
  bool swap_chain_present_wait_enabled = false;
  uint32_t next_frame_context = 0;
  std::vector<PlumeFrameContext> frame_contexts;
  std::vector<std::unique_ptr<plume::RenderCommandSemaphore>> release_semaphores;
  std::unique_ptr<plume::RenderTexture> depth_texture;
  std::vector<std::unique_ptr<plume::RenderFramebuffer>> framebuffers;
  std::vector<std::unique_ptr<plume::RenderFramebuffer>> depth_framebuffers;
#endif
};

PlumeBackendState g_plume_backend;
std::atomic<int64_t> g_last_plume_marshal_us{0};
std::atomic<int64_t> g_last_plume_frame_total_us{0};
std::atomic<int64_t> g_last_plume_backend_us{0};
std::atomic<int64_t> g_last_plume_output_us{0};
std::atomic<int64_t> g_last_plume_consume_us{0};
std::atomic<int64_t> g_last_plume_sync_us{0};
std::atomic<int64_t> g_last_plume_readiness_us{0};
std::atomic<int64_t> g_last_plume_acquire_us{0};
std::atomic<int64_t> g_last_plume_acquire_texture_us{0};
std::atomic<int64_t> g_last_plume_setup_us{0};
std::atomic<int64_t> g_last_plume_imgui_build_us{0};
std::atomic<int64_t> g_last_plume_direct_us{0};
std::atomic<int64_t> g_last_plume_imgui_render_us{0};
std::atomic<int64_t> g_last_plume_command_record_us{0};
std::atomic<int64_t> g_last_plume_submit_us{0};
std::atomic<int64_t> g_last_plume_present_us{0};
std::atomic<int64_t> g_last_plume_fence_wait_us{0};
std::atomic_bool g_plume_output_ready{false};
std::atomic_bool g_plume_output_vsync_enabled{false};
std::atomic<uint32_t> g_plume_output_width{0};
std::atomic<uint32_t> g_plume_output_height{0};
std::atomic<uint32_t> g_plume_output_texture_count{0};
std::atomic<uint32_t> g_plume_output_present_count{0};
std::atomic<uint32_t> g_last_plume_fable_draw_events{0};
std::atomic_bool g_last_plume_fable_draw_events_fresh{false};
std::atomic<uint32_t> g_last_plume_fable_render_commands{0};
std::atomic<uint32_t> g_last_plume_fable_command_source{
    static_cast<uint32_t>(PlumeFableCommandSource::kNone)};
std::atomic<uint32_t> g_last_plume_fable_direct_source{
    static_cast<uint32_t>(PlumeFableCommandSource::kNone)};
std::atomic_bool g_plume_backend_shutdown_requested{false};
std::atomic_bool g_plume_async_present_pending{false};
std::atomic_bool g_logged_plume_async_present{false};
std::atomic_bool g_logged_plume_async_present_coalesced{false};

#if REFII_RENDERER_HAS_PLUME
bool DrainPlumeBackendGpuLocked();

void StorePlumeOutputSnapshotLocked() {
  if (!g_plume_backend.swap_chain) {
    g_plume_output_ready.store(false, std::memory_order_relaxed);
    g_plume_output_vsync_enabled.store(false, std::memory_order_relaxed);
    g_plume_output_width.store(0, std::memory_order_relaxed);
    g_plume_output_height.store(0, std::memory_order_relaxed);
    g_plume_output_texture_count.store(0, std::memory_order_relaxed);
    return;
  }

  g_plume_output_ready.store(true, std::memory_order_relaxed);
  g_plume_output_vsync_enabled.store(
      g_plume_backend.swap_chain->isVsyncEnabled(), std::memory_order_relaxed);
  g_plume_output_width.store(g_plume_backend.swap_chain->getWidth(),
                             std::memory_order_relaxed);
  g_plume_output_height.store(g_plume_backend.swap_chain->getHeight(),
                              std::memory_order_relaxed);
  g_plume_output_texture_count.store(
      g_plume_backend.swap_chain->getTextureCount(), std::memory_order_relaxed);
}

void ResetPlumeOutputLocked() {
  DrainPlumeBackendGpuLocked();
  StorePlumeOutputSnapshotLocked();
  g_plume_backend.framebuffers.clear();
  g_plume_backend.depth_framebuffers.clear();
  g_plume_backend.depth_texture.reset();
  g_plume_backend.release_semaphores.clear();
  g_plume_backend.logged_direct_command_stream = false;
  g_plume_backend.logged_submitted_command_stream = false;
  g_plume_backend.logged_present_pump_pending_command_stream = false;
  g_plume_backend.logged_reused_identical_fable_frame = false;
  g_plume_backend.logged_preserved_fable_direct_output = false;
  g_plume_backend.has_fable_direct_output_to_preserve = false;
  g_plume_backend.frame_contexts.clear();
  g_plume_backend.next_frame_context = 0;
  g_plume_backend.swap_chain.reset();
  g_plume_backend.swap_chain_present_wait_enabled = false;
  StorePlumeOutputSnapshotLocked();
}

bool WaitPlumeFrameContextLocked(PlumeFrameContext& frame_context) {
  if (g_plume_backend.direct_queue && frame_context.fence &&
      frame_context.submitted) {
    const bool completed = g_plume_backend.direct_queue->waitForCommandFence(
        frame_context.fence.get());
    frame_context.submitted = false;
    return completed;
  }
  return true;
}

bool WaitPlumeFrameFencesLocked() {
  bool completed = true;
  for (PlumeFrameContext& frame_context : g_plume_backend.frame_contexts) {
    completed = WaitPlumeFrameContextLocked(frame_context) && completed;
  }
  return completed;
}

bool DrainPlumeBackendGpuLocked() {
  bool completed = WaitPlumeFrameFencesLocked();
  if (g_plume_backend.direct_queue) {
    completed = g_plume_backend.direct_queue->waitIdle() && completed;
  }
  if (g_plume_backend.copy_queue) {
    completed = g_plume_backend.copy_queue->waitIdle() && completed;
  }
  if (g_plume_backend.swap_chain &&
      g_plume_backend.swap_chain_present_wait_enabled) {
    g_plume_backend.swap_chain->wait();
  }
  return completed;
}

bool CreatePlumeFrameContextsLocked() {
  if (!g_plume_backend.device || !g_plume_backend.direct_queue ||
      !g_plume_backend.swap_chain) {
    return false;
  }

  WaitPlumeFrameFencesLocked();
  g_plume_backend.frame_contexts.clear();
  g_plume_backend.next_frame_context = 0;
  const uint32_t context_count =
      std::max<uint32_t>(2, g_plume_backend.swap_chain->getTextureCount());
  g_plume_backend.frame_contexts.reserve(context_count);
  for (uint32_t i = 0; i < context_count; ++i) {
    PlumeFrameContext& frame_context =
        g_plume_backend.frame_contexts.emplace_back();
    frame_context.command_list =
        g_plume_backend.direct_queue->createCommandList();
    frame_context.acquire_semaphore =
        g_plume_backend.device->createCommandSemaphore();
    frame_context.fence = g_plume_backend.device->createCommandFence();
    if (!frame_context.command_list || !frame_context.acquire_semaphore ||
        !frame_context.fence) {
      g_plume_backend.frame_contexts.clear();
      return false;
    }
  }
  return true;
}

bool GetCurrentGameWindow(plume::RenderWindow& render_window) {
  rex::Runtime* runtime = rex::Runtime::instance();
  if (!runtime || !runtime->display_window()) {
    REXLOG_WARN(
        "Refii plume output could not find the current game window yet");
    return false;
  }

  rex::ui::Window* window = runtime->display_window();
#if defined(_WIN64)
  void* hwnd = window->GetNativeWindowHandle();
  if (!hwnd) {
    REXLOG_WARN("Refii plume output window does not have an HWND yet");
    return false;
  }
  render_window = static_cast<HWND>(hwnd);
  return true;
#elif defined(PLUME_SDL_VULKAN_ENABLED) && defined(__linux__)
  auto* sdl_window = dynamic_cast<rex::ui::SDLWindow*>(window);
  if (!sdl_window || !sdl_window->window()) {
    REXLOG_WARN(
        "Refii plume/vulkan output could not access the ReXGlue SDL window");
    return false;
  }
  render_window = sdl_window->window();
  REXLOG_INFO("Refii plume/vulkan output selected ReXGlue SDL window");
  return true;
#elif REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE
  void* native_display = window->GetNativeDisplayHandle();
  void* native_window = window->GetNativeWindowHandle();
  if (!native_display || !native_window) {
    REXLOG_WARN(
        "Refii plume/vulkan output could not access the ReXGlue X11 window "
        "handoff handles");
    return false;
  }

  switch (window->GetNativeWindowSystem()) {
    case rex::ui::Window::NativeWindowSystem::kX11:
      render_window = plume::RenderWindow::XlibWindow(
          static_cast<Display*>(native_display),
          static_cast<::Window>(reinterpret_cast<uintptr_t>(native_window)));
      REXLOG_INFO(
          "Refii plume/vulkan output selected ReXGlue window XID=0x{:X}",
          static_cast<uint64_t>(render_window.window));
      return true;
    case rex::ui::Window::NativeWindowSystem::kWayland:
      render_window = plume::RenderWindow::WaylandWindow(
          native_display, native_window, window->GetActualPhysicalWidth(),
          window->GetActualPhysicalHeight());
      REXLOG_INFO(
          "Refii plume/vulkan output selected ReXGlue Wayland surface");
      return true;
    default:
      REXLOG_WARN(
          "Refii plume/vulkan output found an unsupported ReXGlue native "
          "window system");
      return false;
  }
#else
  (void)window;
  REXLOG_WARN(
      "Refii plume output is not wired for this platform/window system yet");
  return false;
#endif
}

bool RecreatePlumeFramebuffersLocked() {
  DrainPlumeBackendGpuLocked();
  g_plume_backend.framebuffers.clear();
  g_plume_backend.depth_framebuffers.clear();
  g_plume_backend.depth_texture.reset();
  if (!g_plume_backend.swap_chain || !g_plume_backend.device) {
    return false;
  }

  const uint32_t width = g_plume_backend.swap_chain->getWidth();
  const uint32_t height = g_plume_backend.swap_chain->getHeight();
  if (width == 0 || height == 0) {
    return false;
  }

  g_plume_backend.depth_texture =
      g_plume_backend.device->createTexture(plume::RenderTextureDesc::DepthTarget(
          width, height, plume::RenderFormat::D32_FLOAT_S8_UINT));
  if (!g_plume_backend.depth_texture) {
    REXLOG_WARN(
        "Refii plume output could not create swapchain depth target {}x{}",
        width, height);
    return false;
  }
  g_plume_backend.depth_texture->setName("Refii Plume Output Depth");

  const uint32_t texture_count = g_plume_backend.swap_chain->getTextureCount();
  for (uint32_t i = 0; i < texture_count; ++i) {
    const plume::RenderTexture* color_attachment =
        g_plume_backend.swap_chain->getTexture(i);
    plume::RenderFramebufferDesc framebuffer_desc;
    framebuffer_desc.colorAttachments = &color_attachment;
    framebuffer_desc.colorAttachmentsCount = 1;
    g_plume_backend.framebuffers.emplace_back(
        g_plume_backend.device->createFramebuffer(framebuffer_desc));
    if (!g_plume_backend.framebuffers.back()) {
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      return false;
    }

    plume::RenderFramebufferDesc depth_framebuffer_desc;
    depth_framebuffer_desc.colorAttachments = &color_attachment;
    depth_framebuffer_desc.colorAttachmentsCount = 1;
    depth_framebuffer_desc.depthAttachment = g_plume_backend.depth_texture.get();
    g_plume_backend.depth_framebuffers.emplace_back(
        g_plume_backend.device->createFramebuffer(depth_framebuffer_desc));
    if (!g_plume_backend.depth_framebuffers.back()) {
      g_plume_backend.framebuffers.clear();
      g_plume_backend.depth_framebuffers.clear();
      g_plume_backend.depth_texture.reset();
      return false;
    }
  }

  return true;
}

bool EnsurePlumeOutputLocked() {
  if (!g_plume_backend.device || !g_plume_backend.direct_queue) {
    return false;
  }

  if (!g_plume_backend.swap_chain) {
    plume::RenderWindow render_window = {};
    if (!GetCurrentGameWindow(render_window)) {
      return false;
    }

    const uint32_t texture_count = RendererPlumeSwapchainImageCount();
    const bool enable_present_wait =
        g_plume_backend.device->getCapabilities().presentWait;
    g_plume_backend.swap_chain = g_plume_backend.direct_queue->createSwapChain(
        plume::RenderSwapChainDesc(render_window,
                                   plume::RenderFormat::B8G8R8A8_UNORM,
                                   texture_count, enable_present_wait,
                                   texture_count));
    g_plume_backend.swap_chain_present_wait_enabled =
        g_plume_backend.swap_chain && enable_present_wait;
    if (g_plume_backend.swap_chain) {
      g_plume_backend.swap_chain->setVsyncEnabled(RendererPlumeVsyncEnabled());
    }
    if (!g_plume_backend.swap_chain || !g_plume_backend.swap_chain->resize()) {
      REXLOG_WARN(
          "Refii plume/{} output could not create a swapchain for the selected "
          "output window",
          PlumeApiBridgeName(g_plume_backend.api));
      ResetPlumeOutputLocked();
      return false;
    }

    if (!CreatePlumeFrameContextsLocked() ||
        !RecreatePlumeFramebuffersLocked()) {
      REXLOG_WARN("Refii plume output could not create frame resources");
      ResetPlumeOutputLocked();
      return false;
    }

    StorePlumeOutputSnapshotLocked();
    REXLOG_INFO(
        "Refii plume/{} output attached to selected output window "
        "vsync={} present_wait={} requested_images={} actual_images={}",
        PlumeApiBridgeName(g_plume_backend.api),
        g_plume_backend.swap_chain->isVsyncEnabled(),
        g_plume_backend.swap_chain_present_wait_enabled, texture_count,
        g_plume_backend.swap_chain->getTextureCount());
    return true;
  }

  g_plume_backend.swap_chain->setVsyncEnabled(RendererPlumeVsyncEnabled());
  if (g_plume_backend.swap_chain->getTextureCount() !=
      RendererPlumeSwapchainImageCount()) {
    DrainPlumeBackendGpuLocked();
    ResetPlumeOutputLocked();
    return EnsurePlumeOutputLocked();
  }

  if (g_plume_backend.swap_chain->needsResize()) {
    DrainPlumeBackendGpuLocked();
    g_plume_backend.framebuffers.clear();
    g_plume_backend.depth_framebuffers.clear();
    g_plume_backend.depth_texture.reset();
    if (!g_plume_backend.swap_chain->resize() ||
        !CreatePlumeFrameContextsLocked() ||
        !RecreatePlumeFramebuffersLocked()) {
      REXLOG_WARN("Refii plume output swapchain resize failed");
      ResetPlumeOutputLocked();
      return false;
    }
    StorePlumeOutputSnapshotLocked();
  }

  StorePlumeOutputSnapshotLocked();
  return true;
}
#endif

}  // namespace

const char* PlumeBackendStatusName(PlumeBackendStatus status) {
  switch (status) {
    case PlumeBackendStatus::kUnavailable:
      return "unavailable";
    case PlumeBackendStatus::kUnsupportedApi:
      return "unsupported_api";
    case PlumeBackendStatus::kInterfaceUnavailable:
      return "interface_unavailable";
    case PlumeBackendStatus::kDeviceUnavailable:
      return "device_unavailable";
    case PlumeBackendStatus::kQueueUnavailable:
      return "queue_unavailable";
    case PlumeBackendStatus::kReady:
      return "ready";
  }

  return "unknown";
}

const char* PlumeFableCommandSourceName(PlumeFableCommandSource source) {
  switch (source) {
    case PlumeFableCommandSource::kNone:
      return "none";
    case PlumeFableCommandSource::kPending:
      return "pending";
    case PlumeFableCommandSource::kPresented:
      return "presented";
    case PlumeFableCommandSource::kSubmitted:
      return "submitted";
  }

  return "unknown";
}

void RequestPlumeBackendShutdown() {
  g_plume_backend_shutdown_requested.store(true, std::memory_order_release);
  g_plume_async_present_pending.store(false, std::memory_order_release);
}

void ShutdownPlumeBackend() {
  RequestPlumeBackendShutdown();
#if REFII_RENDERER_HAS_PLUME && REFII_SDL3_WINDOW_ENABLED
  if (ShouldMarshalPlumeToUIThread()) {
    CallPlumeOnUIThreadSynchronous([]() { ShutdownPlumeBackend(); });
    return;
  }
#endif

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
#if REFII_RENDERER_HAS_PLUME
  DrainPlumeBackendGpuLocked();
  ShutdownPlumeImGuiOverlay();
  ShutdownPlumeDiagnostics();
  ShutdownFableDirectRenderer();
  ResetPlumeOutputLocked();
  g_plume_backend.copy_queue.reset();
  g_plume_backend.direct_queue.reset();
  g_plume_backend.device.reset();
  g_plume_backend.render_interface.reset();
#endif
  g_plume_backend.attempted = true;
  g_plume_backend.api = GetRendererGraphicsApi();
  g_plume_backend.status = PlumeBackendStatus::kUnavailable;
  g_plume_backend.logged_hook_observer = false;
}

PlumeBackendStatus EnsurePlumeBackendLocal() {
  if (g_plume_backend_shutdown_requested.load(std::memory_order_acquire)) {
    return PlumeBackendStatus::kUnavailable;
  }
  if (!PlumeRendererBackendEnabled()) {
    return PlumeBackendStatus::kUnavailable;
  }

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
  const RendererGraphicsApi api = GetRendererGraphicsApi();
  if (g_plume_backend.attempted && g_plume_backend.api == api) {
    return g_plume_backend.status;
  }

  g_plume_backend.attempted = true;
  g_plume_backend.api = api;
  g_plume_backend.status = PlumeBackendStatus::kUnavailable;
  g_plume_backend.logged_hook_observer = false;

#if REFII_RENDERER_HAS_PLUME
  DrainPlumeBackendGpuLocked();
  ShutdownPlumeImGuiOverlay();
  ShutdownPlumeDiagnostics();
  ShutdownFableDirectRenderer();
  ResetPlumeOutputLocked();
  g_plume_backend.copy_queue.reset();
  g_plume_backend.direct_queue.reset();
  g_plume_backend.device.reset();
  g_plume_backend.render_interface.reset();

  if (!PlumeApiBridgeSupported(api)) {
    g_plume_backend.status = PlumeBackendStatus::kUnsupportedApi;
    REXLOG_WARN(
        "Refii plume/{} backend was requested, but this build target does "
        "not support that API. Select renderer_backend=xenia to use the "
        "original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  InitializeRenderDocDebugger();
  RefreshRendererShaderLibrary();
  PreparePlumeApiBridge(api);
  g_plume_backend.render_interface = CreatePlumeApiInterface(api);
  if (!g_plume_backend.render_interface) {
    g_plume_backend.status = PlumeBackendStatus::kInterfaceUnavailable;
    REXLOG_WARN(
        "Refii plume/{} backend could not create a Plume interface. Select "
        "renderer_backend=xenia to use the original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.device =
      g_plume_backend.render_interface->createDevice(std::string());
  if (!g_plume_backend.device) {
    g_plume_backend.status = PlumeBackendStatus::kDeviceUnavailable;
    REXLOG_WARN(
        "Refii plume/{} backend could not create a Plume device. Select "
        "renderer_backend=xenia to use the original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.direct_queue = g_plume_backend.device->createCommandQueue(
      plume::RenderCommandListType::DIRECT);
  if (!g_plume_backend.direct_queue) {
    g_plume_backend.status = PlumeBackendStatus::kQueueUnavailable;
    REXLOG_WARN(
        "Refii plume/{} backend could not create a direct command queue. "
        "Select renderer_backend=xenia to use the original Fable 2 renderer.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.copy_queue =
      g_plume_backend.device->createCommandQueue(plume::RenderCommandListType::COPY);
  if (!g_plume_backend.copy_queue) {
    REXLOG_WARN(
        "Refii plume/{} backend could not create a copy command queue. "
        "Uploads will use the direct queue until the backend is expanded.",
        PlumeApiBridgeName(api));
  }

  const plume::RenderDeviceDescription& description =
      g_plume_backend.device->getDescription();
  InitializeFableDirectRenderer(
      g_plume_backend.device.get(),
      g_plume_backend.render_interface->getCapabilities().shaderFormat);
  g_plume_backend.status = PlumeBackendStatus::kReady;
  REXLOG_INFO(
      "Refii plume/{} backend initialized device=\"{}\" vram={} MiB "
      "direct_queue=ready copy_queue={}",
      PlumeApiBridgeName(api), description.name,
      description.dedicatedVideoMemory / (1024 * 1024),
      g_plume_backend.copy_queue ? "ready" : "direct_fallback");
  return g_plume_backend.status;
#else
  g_plume_backend.status = PlumeBackendStatus::kUnavailable;
  return g_plume_backend.status;
#endif
}

PlumeBackendStatus EnsurePlumeBackend() {
#if REFII_RENDERER_HAS_PLUME && REFII_SDL3_WINDOW_ENABLED
  if (ShouldMarshalPlumeToUIThread()) {
    PlumeBackendStatus status = PlumeBackendStatus::kUnavailable;
    if (!CallPlumeOnUIThreadSynchronous(
            [&status]() { status = EnsurePlumeBackendLocal(); })) {
      return PlumeBackendStatus::kUnavailable;
    }
    return status;
  }
#endif

  return EnsurePlumeBackendLocal();
}

bool PlumeOutputReady() {
#if REFII_RENDERER_HAS_PLUME
#if REFII_SDL3_WINDOW_ENABLED
  if (ShouldMarshalPlumeToUIThread()) {
    bool ready = false;
    if (!CallPlumeOnUIThreadSynchronous(
            [&ready]() { ready = PlumeOutputReady(); })) {
      return false;
    }
    return ready;
  }
#endif

  if (EnsurePlumeBackendLocal() != PlumeBackendStatus::kReady) {
    return false;
  }

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
  return EnsurePlumeOutputLocked();
#else
  return false;
#endif
}

uint32_t CurrentPlumeOutputFrameIndex() {
  return g_plume_output_present_count.load(std::memory_order_relaxed);
}

PlumeOutputSnapshot GetPlumeOutputSnapshot() {
  PlumeOutputSnapshot snapshot;
  snapshot.configured_vsync = RendererPlumeVsyncEnabled();
  snapshot.configured_texture_count = RendererPlumeSwapchainImageCount();
  snapshot.last_marshal_us =
      g_last_plume_marshal_us.load(std::memory_order_relaxed);
  snapshot.last_frame_total_us =
      g_last_plume_frame_total_us.load(std::memory_order_relaxed);
  snapshot.last_backend_us =
      g_last_plume_backend_us.load(std::memory_order_relaxed);
  snapshot.last_output_us =
      g_last_plume_output_us.load(std::memory_order_relaxed);
  snapshot.last_consume_us =
      g_last_plume_consume_us.load(std::memory_order_relaxed);
  snapshot.last_sync_us =
      g_last_plume_sync_us.load(std::memory_order_relaxed);
  snapshot.last_readiness_us =
      g_last_plume_readiness_us.load(std::memory_order_relaxed);
  snapshot.last_acquire_us =
      g_last_plume_acquire_us.load(std::memory_order_relaxed);
  snapshot.last_acquire_texture_us =
      g_last_plume_acquire_texture_us.load(std::memory_order_relaxed);
  snapshot.last_setup_us =
      g_last_plume_setup_us.load(std::memory_order_relaxed);
  snapshot.last_imgui_build_us =
      g_last_plume_imgui_build_us.load(std::memory_order_relaxed);
  snapshot.last_direct_us =
      g_last_plume_direct_us.load(std::memory_order_relaxed);
  snapshot.last_imgui_render_us =
      g_last_plume_imgui_render_us.load(std::memory_order_relaxed);
  snapshot.last_command_record_us =
      g_last_plume_command_record_us.load(std::memory_order_relaxed);
  snapshot.last_submit_us =
      g_last_plume_submit_us.load(std::memory_order_relaxed);
  snapshot.last_present_us =
      g_last_plume_present_us.load(std::memory_order_relaxed);
  snapshot.last_fence_wait_us =
      g_last_plume_fence_wait_us.load(std::memory_order_relaxed);
  snapshot.ready = g_plume_output_ready.load(std::memory_order_relaxed);
  snapshot.vsync_enabled =
      g_plume_output_vsync_enabled.load(std::memory_order_relaxed);
  snapshot.width = g_plume_output_width.load(std::memory_order_relaxed);
  snapshot.height = g_plume_output_height.load(std::memory_order_relaxed);
  snapshot.texture_count =
      g_plume_output_texture_count.load(std::memory_order_relaxed);
  snapshot.present_count =
      g_plume_output_present_count.load(std::memory_order_relaxed);
  snapshot.last_fable_render_commands =
      g_last_plume_fable_render_commands.load(std::memory_order_relaxed);
  snapshot.last_fable_draw_events =
      g_last_plume_fable_draw_events.load(std::memory_order_relaxed);
  snapshot.last_fable_command_source = static_cast<PlumeFableCommandSource>(
      g_last_plume_fable_command_source.load(std::memory_order_relaxed));
  snapshot.last_fable_direct_source = static_cast<PlumeFableCommandSource>(
      g_last_plume_fable_direct_source.load(std::memory_order_relaxed));
  snapshot.last_fable_draw_events_fresh =
      g_last_plume_fable_draw_events_fresh.load(std::memory_order_relaxed);
  return snapshot;
}

bool PresentPlumeFrame(bool drive_fable_frame) {
  ZoneNamedN(tracy_frame_zone, "RefiiPlume/PresentFrame", true);
  if (g_plume_backend_shutdown_requested.load(std::memory_order_acquire)) {
    return false;
  }
  if (!RendererReplacePresentEnabled()) {
    return false;
  }
#if REFII_RENDERER_HAS_PLUME
  ConfigurePlumeBackendTracyPlots();
#if REFII_SDL3_WINDOW_ENABLED
  if (ShouldMarshalPlumeToUIThread()) {
    ZoneScopedN("RefiiPlume/PresentFrameMarshalToUI");
    if (drive_fable_frame) {
      bool expected = false;
      if (!g_plume_async_present_pending.compare_exchange_strong(
              expected, true, std::memory_order_acq_rel)) {
        if (!g_logged_plume_async_present_coalesced.exchange(
                true, std::memory_order_relaxed)) {
          REXLOG_INFO(
              "Refii plume present pump is coalescing pending UI-thread "
              "presents");
        }
        return false;
      }

      if (!CallPlumeOnUIThreadDeferred([drive_fable_frame]() {
            if (!g_plume_backend_shutdown_requested.load(
                    std::memory_order_acquire)) {
              PresentPlumeFrame(drive_fable_frame);
            }
            g_plume_async_present_pending.store(false,
                                                std::memory_order_release);
          })) {
        g_plume_async_present_pending.store(false, std::memory_order_release);
        return false;
      }

      if (!g_logged_plume_async_present.exchange(true,
                                                 std::memory_order_relaxed)) {
        REXLOG_INFO(
            "Refii plume present pump is queuing coalesced async UI-thread "
            "presents");
      }
      return true;
    }

    const PlumeClock::time_point marshal_t0 = PlumeClock::now();
    bool presented = false;
    if (!CallPlumeOnUIThreadSynchronous(
            [&presented, drive_fable_frame]() {
              presented = PresentPlumeFrame(drive_fable_frame);
            })) {
      return false;
    }
    const double marshal_ms = PlumeElapsedMs(marshal_t0, PlumeClock::now());
    g_last_plume_marshal_us.store(static_cast<int64_t>(marshal_ms * 1000.0),
                                  std::memory_order_relaxed);
    if (marshal_ms > 25.0) {
      REXLOG_INFO("Refii plume UI-thread present marshal took {:.3f} ms",
                  marshal_ms);
    }
    return presented;
  }
#endif

  const PlumeClock::time_point frame_t0 = PlumeClock::now();
  {
    ZoneScopedN("RefiiPlume/EnsureBackend");
    if (EnsurePlumeBackendLocal() != PlumeBackendStatus::kReady) {
      return false;
    }
  }
  const PlumeClock::time_point backend_t1 = PlumeClock::now();

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
  const plume::RenderShaderFormat shader_format =
      g_plume_backend.render_interface->getCapabilities().shaderFormat;
  {
    ZoneScopedN("RefiiPlume/EnsureOutput");
    if (!EnsurePlumeOutputLocked()) {
      return false;
    }
  }
  const PlumeClock::time_point output_t1 = PlumeClock::now();
  PlumeFableCommandSource command_source = PlumeFableCommandSource::kNone;
  FableDirectStateSnapshot direct_snapshot = GetFableDirectStateSnapshot();
  {
    ZoneScopedN("RefiiPlume/ReadDirectState");
    const bool pending_has_render_work =
        direct_snapshot.pending_clear_command_count != 0 ||
        direct_snapshot.pending_resolve_command_count != 0 ||
        std::any_of(direct_snapshot.pending_draw_source_counts.begin(),
                    direct_snapshot.pending_draw_source_counts.end(),
                    [](uint32_t count) { return count != 0; });
    if (direct_snapshot.submitted_render_command_count != 0) {
      command_source = PlumeFableCommandSource::kSubmitted;
    } else if (pending_has_render_work) {
      command_source = PlumeFableCommandSource::kPending;
    }
  }
  g_last_plume_fable_render_commands.store(
      static_cast<uint32_t>(std::min<uint64_t>(
          direct_snapshot.render_command_count, UINT32_MAX)),
      std::memory_order_relaxed);
  const uint32_t executed_draw_command_count =
      static_cast<uint32_t>(std::min<uint64_t>(
          direct_snapshot.draw_count, UINT32_MAX));
  g_last_plume_fable_draw_events.store(
      executed_draw_command_count, std::memory_order_relaxed);
  g_last_plume_fable_draw_events_fresh.store(true,
                                             std::memory_order_relaxed);
  g_last_plume_fable_command_source.store(
      static_cast<uint32_t>(command_source), std::memory_order_relaxed);
  g_last_plume_fable_direct_source.store(static_cast<uint32_t>(command_source),
                                         std::memory_order_relaxed);
  const PlumeClock::time_point consume_t1 = PlumeClock::now();
  const PlumeClock::time_point sync_t1 = PlumeClock::now();
  const PlumeClock::time_point readiness_t1 = PlumeClock::now();

  const bool renderdoc_ready_to_capture =
      direct_snapshot.draw_count != 0 ||
      direct_snapshot.indexed_draw_count != 0 ||
      direct_snapshot.last_submitted_draw_event_count != 0 ||
      direct_snapshot.last_command_boundary_pending_draws != 0;
  const uint32_t renderdoc_capture_frame = RenderDocCaptureFrameIndex();
  if (renderdoc_capture_frame != 0 &&
      g_plume_backend.present_count + 1u == renderdoc_capture_frame) {
    RequestRenderDocFrameCapture();
  }
  const std::string renderdoc_capture_draw_target =
      RenderDocCaptureDrawTarget();
  if (renderdoc_capture_draw_target.empty()) {
    g_plume_backend.last_renderdoc_draw_target_capture.clear();
    g_plume_backend.logged_renderdoc_draw_target_watch = false;
  } else if (g_plume_backend.last_renderdoc_draw_target_capture !=
                 renderdoc_capture_draw_target) {
    if (!g_plume_backend.logged_renderdoc_draw_target_watch) {
      REXLOG_INFO("Refii plume RenderDoc draw-target watch active target={}",
                  renderdoc_capture_draw_target);
      g_plume_backend.logged_renderdoc_draw_target_watch = true;
    }
    if (QueuedCommandsContainRenderDocDrawTarget(renderdoc_capture_draw_target)) {
      if (RequestRenderDocFrameCapture()) {
        g_plume_backend.last_renderdoc_draw_target_capture =
            renderdoc_capture_draw_target;
        REXLOG_INFO(
            "Refii plume RenderDoc draw-target capture armed target={} "
            "present={}",
            renderdoc_capture_draw_target, g_plume_backend.present_count + 1u);
      }
    }
  }
  const bool renderdoc_capture_started =
      BeginRenderDocFrameCapture("Refii Plume Frame",
                                 renderdoc_ready_to_capture);

  if (g_plume_backend.frame_contexts.empty()) {
    EndRenderDocFrameCapture(renderdoc_capture_started);
    return false;
  }
  PlumeFrameContext& frame_context =
      g_plume_backend.frame_contexts[g_plume_backend.next_frame_context %
                                     g_plume_backend.frame_contexts.size()];
  g_plume_backend.next_frame_context =
      (g_plume_backend.next_frame_context + 1) %
      static_cast<uint32_t>(g_plume_backend.frame_contexts.size());
  const PlumeClock::time_point frame_context_wait_t0 = PlumeClock::now();
  {
    ZoneScopedN("RefiiPlume/WaitReusableFrameContext");
    WaitPlumeFrameContextLocked(frame_context);
  }
  const PlumeClock::time_point frame_context_wait_t1 = PlumeClock::now();

  uint32_t image_index = 0;
  {
    ZoneScopedN("RefiiPlume/AcquireTexture");
    if (!g_plume_backend.swap_chain->acquireTexture(
            frame_context.acquire_semaphore.get(), &image_index)) {
      EndRenderDocFrameCapture(renderdoc_capture_started);
      return false;
    }
  }
  const PlumeClock::time_point acquire_t1 = PlumeClock::now();

  if (image_index >= g_plume_backend.framebuffers.size()) {
    EndRenderDocFrameCapture(renderdoc_capture_started);
    return false;
  }
  plume::RenderFramebuffer* fable_output_framebuffer =
      g_plume_backend.framebuffers[image_index].get();
  if (g_plume_backend.depth_texture) {
    if (image_index >= g_plume_backend.depth_framebuffers.size()) {
      EndRenderDocFrameCapture(renderdoc_capture_started);
      return false;
    }
    fable_output_framebuffer =
        g_plume_backend.depth_framebuffers[image_index].get();
  }

  plume::RenderTexture* swap_chain_texture =
      g_plume_backend.swap_chain->getTexture(image_index);
  plume::RenderCommandList* command_list = frame_context.command_list.get();
  if (!command_list) {
    EndRenderDocFrameCapture(renderdoc_capture_started);
    return false;
  }
  const bool render_imgui_overlay = RendererImGuiEnabled();
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/EnsureImGuiRenderer");
    EnsurePlumeImGuiRenderer(g_plume_backend.device.get(), shader_format);
  }
  command_list->begin();
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/RecordImGuiUploads");
    RecordPlumeImGuiUploads(command_list);
  }
  if (!g_plume_backend.logged_frame_after_uploads) {
    REXLOG_INFO("Refii plume frame recorded pending uploads");
    g_plume_backend.logged_frame_after_uploads = true;
  }
  command_list->barriers(
      plume::RenderBarrierStage::GRAPHICS,
      plume::RenderTextureBarrier(swap_chain_texture,
                                  plume::RenderTextureLayout::COLOR_WRITE));
  if (g_plume_backend.depth_texture) {
    command_list->barriers(
        plume::RenderBarrierStage::GRAPHICS,
        plume::RenderTextureBarrier(g_plume_backend.depth_texture.get(),
                                    plume::RenderTextureLayout::DEPTH_WRITE));
  }
  command_list->setFramebuffer(fable_output_framebuffer);
  if (!g_plume_backend.logged_frame_after_color_target) {
    REXLOG_INFO("Refii plume frame attached swapchain color target");
    g_plume_backend.logged_frame_after_color_target = true;
  }

  const uint32_t width = g_plume_backend.swap_chain->getWidth();
  const uint32_t height = g_plume_backend.swap_chain->getHeight();
  const uint32_t frame_index = ++g_plume_backend.present_count;
  g_plume_output_present_count.store(frame_index, std::memory_order_relaxed);
  const PlumeClock::time_point setup_t1 = PlumeClock::now();
  BeginFableDirectFrame(frame_index);
  direct_snapshot = GetFableDirectStateSnapshot();
  const bool log_frame_summary =
      !g_plume_backend.logged_first_frame_summary ||
      (RendererVerboseTracingEnabled() && frame_index % 120 == 0);
  if (log_frame_summary) {
    const RendererHookObserverSnapshot hook_snapshot =
        GetRendererHookObserverSnapshot();
    const RendererHookTimingSnapshot timing_snapshot =
        GetRendererHookTimingSnapshot();
    const FableDirectStateSnapshot direct_snapshot =
        GetFableDirectStateSnapshot();
    REXLOG_INFO(
        "Refii plume frame summary frame={} output={}x{} live={} vb={} ib={} "
        "shaders={} passes={} flushes={} dispatches={} dispatch_targets={} "
        "queue_callbacks={} queue_targets={} command_writes={} "
        "command_submits={} command_words={} pm4_type0={} pm4_type3={} "
        "pm4_draw={} pm4_indirect={} pm4_constants={} pm4_swap={} "
        "pm4_errors={} shader_uploads={} fetch_uploads={} shader_nz={}/{} "
        "shader_upload_nz={} shader_zero={} shader_skip={} "
        "last_shader={}:r{}+{} dwords={} nz={} src=0x{:08X} lr=0x{:08X} "
        "data={:08X}/{:08X}/{:08X}/{:08X} fetch_nz={} "
        "display_source={} display_base=0x{:08X} display_object=0x{:08X} "
        "display_size={}x{} display_fmt={} display_reg=0x{:04X}/0x{:08X} "
        "draws={} indexed={} executed_draws={} dropped_draws={} "
        "scene_walkers={}/{}/{} scene_slots={} last_scene=0x{:08X}/{} "
        "first=0x{:08X} result=0x{:08X} scene_batches={}/{}/{} "
        "scene_bridge={}/{}/{} scene_inline={} "
        "cmd_source={} exec_source={} cmd_counts={}/{} "
        "submit_serial={} boundary={}/{}/{} submit_pending={}/{} "
        "submit_draws={} "
        "ready_vb={} ready_ib={} ready_decl={} ready_vs={} ready_ps={} "
        "fable_present_hooks={} plume_pump_requests={} "
        "plume_pump_fable_advances={} fable_submitted={} fable_completed={} "
        "fable_callbacks={} locks={}/{} "
        "flush_lr=0x{:08X} last_pm4=0x{:08X}/op=0x{:02X} "
        "last_draw={}/prim:{} base:{} start:{} count:{} vb0=0x{:08X}/{} "
        "ib=0x{:08X} decl=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
        frame_index, width, height, hook_snapshot.live_resources,
        hook_snapshot.live_vertex_buffers, hook_snapshot.live_index_buffers,
        hook_snapshot.live_shaders, hook_snapshot.render_passes,
        hook_snapshot.state_flushes, hook_snapshot.state_dispatches,
        hook_snapshot.state_dispatch_targets,
        hook_snapshot.render_queue_callbacks,
        hook_snapshot.render_queue_callback_targets,
        hook_snapshot.command_writes, hook_snapshot.command_submits,
        hook_snapshot.command_words, hook_snapshot.pm4_type0_packets,
        hook_snapshot.pm4_type3_packets, hook_snapshot.pm4_draw_packets,
        hook_snapshot.pm4_indirect_packets, hook_snapshot.pm4_constant_packets,
        hook_snapshot.pm4_swap_packets, hook_snapshot.pm4_parse_errors,
        hook_snapshot.shader_constant_uploads,
        hook_snapshot.fetch_constant_uploads,
        hook_snapshot.vertex_shader_constant_nonzero_dwords,
        hook_snapshot.pixel_shader_constant_nonzero_dwords,
        hook_snapshot.shader_constant_upload_nonzero_dwords,
        hook_snapshot.shader_constant_zero_uploads,
        hook_snapshot.shader_constant_skipped_uploads,
        hook_snapshot.last_shader_constant_bank != 0u ? "ps" : "vs",
        hook_snapshot.last_shader_constant_first_register,
        hook_snapshot.last_shader_constant_first_component,
        hook_snapshot.last_shader_constant_dwords,
        hook_snapshot.last_shader_constant_nonzero_dwords,
        hook_snapshot.last_shader_constant_source,
        hook_snapshot.last_shader_constant_lr,
        hook_snapshot.last_shader_constant_preview[0],
        hook_snapshot.last_shader_constant_preview[1],
        hook_snapshot.last_shader_constant_preview[2],
        hook_snapshot.last_shader_constant_preview[3],
        hook_snapshot.fetch_constant_nonzero_dwords,
        hook_snapshot.display_present_source,
        hook_snapshot.display_present_texture_base,
        hook_snapshot.display_present_surface_object,
        hook_snapshot.display_present_width,
        hook_snapshot.display_present_height,
        hook_snapshot.display_present_format,
        hook_snapshot.display_present_register,
        hook_snapshot.display_present_register_value,
        hook_snapshot.draws, hook_snapshot.indexed_draws,
        executed_draw_command_count, hook_snapshot.dropped_draw_events,
        hook_snapshot.scene_walker_calls,
        hook_snapshot.scene_walker_flush_calls,
        hook_snapshot.scene_walker_nonempty_calls,
        hook_snapshot.scene_walker_object_slots_total,
        hook_snapshot.last_scene_walker_list,
        hook_snapshot.last_scene_walker_count,
        hook_snapshot.last_scene_walker_first,
        hook_snapshot.last_scene_walker_result,
        hook_snapshot.scene_draw_filter_calls,
        hook_snapshot.scene_batch_traversal_calls,
        hook_snapshot.scene_batch_bridge_calls,
        hook_snapshot.scene_batch_bridge_calls,
        hook_snapshot.scene_batch_bridge_rejected_calls,
        hook_snapshot.scene_batch_bridge_queued_draws,
        hook_snapshot.scene_inline_vertex_draws,
        PlumeFableCommandSourceName(command_source),
        PlumeFableCommandSourceName(command_source),
        direct_snapshot.render_command_count,
        direct_snapshot.submitted_render_command_count,
        direct_snapshot.submitted_render_command_serial,
        direct_snapshot.command_boundary_capture_count,
        direct_snapshot.empty_command_boundary_capture_count,
        direct_snapshot.last_command_boundary_submit,
        direct_snapshot.last_command_boundary_pending_commands,
        direct_snapshot.last_command_boundary_pending_draws,
        direct_snapshot.last_submitted_draw_event_count,
        direct_snapshot.input.streams[0].buffer != 0 ? 1u : 0u,
        direct_snapshot.input.index_buffer != 0 ? 1u : 0u,
        direct_snapshot.shaders.vertex_declaration != 0 ? 1u : 0u,
        direct_snapshot.shaders.vertex_shader != 0 ? 1u : 0u,
        direct_snapshot.shaders.pixel_shader != 0 ? 1u : 0u,
        hook_snapshot.presents, timing_snapshot.plume_present_pump_requests,
        timing_snapshot.plume_present_pump_fable_advances,
        timing_snapshot.fable_present_submitted,
        timing_snapshot.fable_present_completed,
        timing_snapshot.fable_swap_callbacks, hook_snapshot.resource_locks,
        hook_snapshot.resource_unlocks, hook_snapshot.last_state_flush_lr,
        hook_snapshot.last_command_first_word,
        hook_snapshot.last_command_type3_opcode,
	        hook_snapshot.last_draw.indexed
	            ? "indexed"
	            : "direct",
	        hook_snapshot.last_draw.primitive_type,
	        hook_snapshot.last_draw.base_vertex,
	        hook_snapshot.last_draw.start,
	        hook_snapshot.last_draw.primitive_count,
	        hook_snapshot.streams[0].buffer,
	        hook_snapshot.streams[0].stride,
	        hook_snapshot.index_buffer,
	        hook_snapshot.vertex_declaration,
	        hook_snapshot.vertex_shader,
	        hook_snapshot.pixel_shader);
    g_plume_backend.logged_first_frame_summary = true;
  }
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/BuildImGuiOverlay");
    DrawPlumeImGuiOverlay(width, height, frame_index);
  }
  const PlumeClock::time_point imgui_build_t1 = PlumeClock::now();
  if (render_imgui_overlay && !g_plume_backend.logged_frame_after_imgui_build) {
    REXLOG_INFO("Refii plume frame built ImGui draw data");
    g_plume_backend.logged_frame_after_imgui_build = true;
  }
  command_list->setViewports(
      plume::RenderViewport(0.0f, 0.0f, float(width), float(height)));
  command_list->setScissors(plume::RenderRect(0, 0, width, height));
  const bool preserve_direct_output =
      HasFableDirectFrameContent(direct_snapshot) ||
      g_plume_backend.has_fable_direct_output_to_preserve;
  if (!preserve_direct_output) {
    command_list->clearColor(0, plume::RenderColor(0.0f, 0.0f, 0.0f, 1.0f));
    if (g_plume_backend.depth_texture) {
      command_list->clearDepthStencil(true, false, 1.0f, 0);
    }
  } else if (!g_plume_backend.logged_preserved_fable_direct_output) {
    REXLOG_INFO(
        "Refii plume preserving Fable direct output instead of pre-clearing "
        "swapchain draw_count={} commands={} submitted={} serial={}",
        direct_snapshot.draw_count, direct_snapshot.render_command_count,
        direct_snapshot.submitted_render_command_count,
        direct_snapshot.submitted_render_command_serial);
    g_plume_backend.logged_preserved_fable_direct_output = true;
  }
  FableDirectRendererStats direct_stats = {};
  {
    ZoneScopedN("RefiiPlume/RecordFableDirectFrame");
    RecordFableDirectFrame(
        g_plume_backend.device.get(), command_list, fable_output_framebuffer,
        swap_chain_texture, g_plume_backend.framebuffers[image_index].get(),
        shader_format,
        g_plume_backend.depth_texture ? plume::RenderFormat::D32_FLOAT_S8_UINT
                                      : plume::RenderFormat::UNKNOWN,
        frame_index);
    direct_stats = GetFableDirectRendererStats();
  }
  if (direct_stats.draw_count != 0 || direct_stats.last_present_target != 0) {
    g_plume_backend.has_fable_direct_output_to_preserve = true;
  }
  const bool renderdoc_capture_active =
      renderdoc_capture_started || direct_stats.renderdoc_capture_started;
  const PlumeClock::time_point direct_t1 = PlumeClock::now();
  char tracy_frame_summary[256] = {};
  std::snprintf(
      tracy_frame_summary, sizeof(tracy_frame_summary),
      "frame=%u direct_draws=%u indexed=%u commands=%u target=0x%08X "
      "pix=0x%08X",
      frame_index, direct_stats.draw_count, direct_stats.indexed_draw_count,
      direct_stats.last_render_commands, direct_stats.last_present_target,
      GetRendererHookObserverSnapshot().last_pix_texture);
  ZoneTextV(tracy_frame_zone, tracy_frame_summary,
            std::strlen(tracy_frame_summary));
  if (renderdoc_capture_active) {
    const RendererHookObserverSnapshot renderdoc_hook_snapshot =
        GetRendererHookObserverSnapshot();
    const RendererHookTimingSnapshot renderdoc_timing_snapshot =
        GetRendererHookTimingSnapshot();
    SetRenderDocFrameCaptureTitle(
        BuildRenderDocCaptureTitle(frame_index, width, height, direct_stats,
                                   renderdoc_hook_snapshot)
            .c_str());
	    SetRenderDocFrameCaptureComments(
	        BuildRenderDocCaptureComments(frame_index, width, height, direct_stats,
	                                      renderdoc_hook_snapshot,
	                                      renderdoc_timing_snapshot,
	                                      direct_snapshot)
	            .c_str());
  }
  command_list->setFramebuffer(g_plume_backend.framebuffers[image_index].get());
  {
    ZoneScopedN("RefiiPlume/RenderDiagnostics");
    RenderPlumeDiagnostics(g_plume_backend.device.get(), command_list,
                           shader_format, frame_index, width, height);
  }
  if (direct_stats.draw_count != 0 && frame_index % 120 == 0) {
    REXLOG_INFO(
        "Refii plume direct renderer frame={} draws={} indexed={} events={} "
        "commands={} resources={}/{} pipeline={}/{} failed_hooks={} "
        "failed_resources={} "
        "target=0x{:08X} size={}x{} source={}",
        frame_index, direct_stats.draw_count, direct_stats.indexed_draw_count,
        direct_stats.last_draw_events, direct_stats.last_render_commands,
        direct_stats.resource_creations, direct_stats.resource_uploads,
        direct_stats.pipeline_cache_hits, direct_stats.pipeline_cache_misses,
        direct_stats.failed_hooks, direct_stats.failed_resource_syncs,
        direct_stats.last_present_target, direct_stats.last_present_width,
        direct_stats.last_present_height, PlumeFableCommandSourceName(command_source));
  }
  if (render_imgui_overlay) {
    ZoneScopedN("RefiiPlume/RenderImGuiOverlay");
    RenderPlumeImGuiOverlay(g_plume_backend.device.get(), command_list);
  }
  const PlumeClock::time_point imgui_render_t1 = PlumeClock::now();
  if (render_imgui_overlay && !g_plume_backend.logged_frame_after_imgui_render) {
    REXLOG_INFO("Refii plume frame recorded ImGui overlay draws");
    g_plume_backend.logged_frame_after_imgui_render = true;
  }
  command_list->barriers(
      plume::RenderBarrierStage::NONE,
      plume::RenderTextureBarrier(swap_chain_texture,
                                  plume::RenderTextureLayout::PRESENT));
  command_list->end();
  const PlumeClock::time_point command_end_t1 = PlumeClock::now();

  while (g_plume_backend.release_semaphores.size() <= image_index) {
    g_plume_backend.release_semaphores.emplace_back(
        g_plume_backend.device->createCommandSemaphore());
  }

  const plume::RenderCommandList* submitted_list = command_list;
  plume::RenderCommandSemaphore* wait_semaphore =
      frame_context.acquire_semaphore.get();
  plume::RenderCommandSemaphore* signal_semaphore =
      g_plume_backend.release_semaphores[image_index].get();
  if (!g_plume_backend.logged_frame_before_submit) {
    REXLOG_INFO("Refii plume frame submitting command list");
    g_plume_backend.logged_frame_before_submit = true;
  }
  {
    ZoneScopedN("RefiiPlume/SubmitCommandList");
    frame_context.submitted = g_plume_backend.direct_queue->executeCommandLists(
        &submitted_list, 1, &wait_semaphore, 1, &signal_semaphore, 1,
        frame_context.fence.get());
  }
  const PlumeClock::time_point submit_t1 = PlumeClock::now();
  if (!frame_context.submitted) {
    EndRenderDocFrameCapture(renderdoc_capture_active);
    return false;
  }
  bool presented = false;
  {
    ZoneScopedN("RefiiPlume/SwapChainPresent");
    presented = g_plume_backend.swap_chain->present(
        image_index, &signal_semaphore, 1);
    if (presented && g_plume_backend.swap_chain_present_wait_enabled) {
      g_plume_backend.swap_chain->wait();
    }
  }
  const PlumeClock::time_point present_t1 = PlumeClock::now();
  PresentFableDirectFrame(direct_stats.last_present_target, width, height);
  EndFableDirectFrame();
  if (!g_plume_backend.logged_frame_after_present) {
    REXLOG_INFO("Refii plume frame present returned {}", presented);
    g_plume_backend.logged_frame_after_present = true;
  }
  if (!g_plume_backend.logged_frame_after_wait) {
    REXLOG_INFO(
        "Refii plume is using {} frame contexts before reusing command "
        "resources",
        g_plume_backend.frame_contexts.size());
    g_plume_backend.logged_frame_after_wait = true;
  }
  if (ShouldLogPlumeFrameTiming(frame_index)) {
    REXLOG_INFO(
        "Refii plume frame timing frame={} total_ms={:.3f} backend_ms={:.3f} "
        "output_ms={:.3f} consume_ms={:.3f} sync_ms={:.3f} "
        "readiness_ms={:.3f} acquire_ms={:.3f} setup_ms={:.3f} "
        "imgui_build_ms={:.3f} direct_ms={:.3f} imgui_render_ms={:.3f} "
        "cmd_end_ms={:.3f} submit_ms={:.3f} present_ms={:.3f} "
        "context_wait_ms={:.3f} direct_draws={} cmd_source={}",
        frame_index, PlumeElapsedMs(frame_t0, present_t1),
        PlumeElapsedMs(frame_t0, backend_t1),
        PlumeElapsedMs(backend_t1, output_t1),
        PlumeElapsedMs(output_t1, consume_t1),
        PlumeElapsedMs(consume_t1, sync_t1),
        PlumeElapsedMs(sync_t1, readiness_t1),
        PlumeElapsedMs(readiness_t1, acquire_t1),
        PlumeElapsedMs(acquire_t1, setup_t1),
        PlumeElapsedMs(setup_t1, imgui_build_t1),
        PlumeElapsedMs(imgui_build_t1, direct_t1),
        PlumeElapsedMs(direct_t1, imgui_render_t1),
        PlumeElapsedMs(imgui_render_t1, command_end_t1),
        PlumeElapsedMs(command_end_t1, submit_t1),
        PlumeElapsedMs(submit_t1, present_t1),
        PlumeElapsedMs(frame_context_wait_t0, frame_context_wait_t1),
        direct_stats.draw_count, PlumeFableCommandSourceName(command_source));
  }
  const int64_t frame_total_us = PlumeElapsedUs(frame_t0, present_t1);
  const int64_t backend_us = PlumeElapsedUs(frame_t0, backend_t1);
  const int64_t output_us = PlumeElapsedUs(backend_t1, output_t1);
  const int64_t consume_us = PlumeElapsedUs(output_t1, consume_t1);
  const int64_t sync_us = PlumeElapsedUs(consume_t1, sync_t1);
  const int64_t readiness_us = PlumeElapsedUs(sync_t1, readiness_t1);
  const int64_t acquire_us = PlumeElapsedUs(readiness_t1, acquire_t1);
  const int64_t acquire_texture_us =
      PlumeElapsedUs(frame_context_wait_t1, acquire_t1);
  const int64_t setup_us = PlumeElapsedUs(acquire_t1, setup_t1);
  const int64_t imgui_build_us = PlumeElapsedUs(setup_t1, imgui_build_t1);
  const int64_t direct_us = PlumeElapsedUs(imgui_build_t1, direct_t1);
  const int64_t imgui_render_us =
      PlumeElapsedUs(direct_t1, imgui_render_t1);
  const int64_t command_record_us =
      PlumeElapsedUs(imgui_render_t1, command_end_t1);
  const int64_t submit_us = PlumeElapsedUs(command_end_t1, submit_t1);
  const int64_t present_us = PlumeElapsedUs(submit_t1, present_t1);
  const int64_t fence_wait_us =
      PlumeElapsedUs(frame_context_wait_t0, frame_context_wait_t1);
  const RendererHookObserverSnapshot tracy_hook_snapshot =
      GetRendererHookObserverSnapshot();
  g_last_plume_frame_total_us.store(frame_total_us, std::memory_order_relaxed);
  g_last_plume_backend_us.store(backend_us, std::memory_order_relaxed);
  g_last_plume_output_us.store(output_us, std::memory_order_relaxed);
  g_last_plume_consume_us.store(consume_us, std::memory_order_relaxed);
  g_last_plume_sync_us.store(sync_us, std::memory_order_relaxed);
  g_last_plume_readiness_us.store(readiness_us, std::memory_order_relaxed);
  g_last_plume_acquire_us.store(acquire_us, std::memory_order_relaxed);
  g_last_plume_acquire_texture_us.store(acquire_texture_us,
                                        std::memory_order_relaxed);
  g_last_plume_setup_us.store(setup_us, std::memory_order_relaxed);
  g_last_plume_imgui_build_us.store(imgui_build_us, std::memory_order_relaxed);
  g_last_plume_direct_us.store(direct_us, std::memory_order_relaxed);
  g_last_plume_imgui_render_us.store(imgui_render_us,
                                     std::memory_order_relaxed);
  g_last_plume_command_record_us.store(command_record_us,
                                       std::memory_order_relaxed);
  g_last_plume_submit_us.store(submit_us, std::memory_order_relaxed);
  g_last_plume_present_us.store(present_us, std::memory_order_relaxed);
  g_last_plume_fence_wait_us.store(fence_wait_us, std::memory_order_relaxed);
  ZoneValueV(tracy_frame_zone, frame_total_us);
  TracyPlot("Refii Plume Frame Total Us", frame_total_us);
  TracyPlot("Refii Plume Acquire Us", acquire_us);
  TracyPlot("Refii Plume Resource Sync Us", sync_us);
  TracyPlot("Refii Plume Readiness Us", readiness_us);
  TracyPlot("Refii Plume Command Us", direct_us);
  TracyPlot("Refii Plume ImGui Build Us", imgui_build_us);
  TracyPlot("Refii Plume ImGui Render Us", imgui_render_us);
  TracyPlot("Refii Plume Command Record Us", command_record_us);
  TracyPlot("Refii Plume Submit Us", submit_us);
  TracyPlot("Refii Plume Present Us", present_us);
  TracyPlot("Refii Plume Fence Wait Us", fence_wait_us);
  TracyPlot("Refii Plume Executed Draw Commands",
            static_cast<int64_t>(executed_draw_command_count));
  TracyPlot("Refii Plume Direct Draws",
            static_cast<int64_t>(direct_stats.draw_count));
  TracyPlot("Refii Plume Missing Resources", int64_t{0});
  TracyPlot("Refii Plume Missing Pipelines",
            static_cast<int64_t>(direct_stats.pipeline_cache_misses));
  TracyPlot("Refii Plume Texture Uploads",
            static_cast<int64_t>(direct_stats.resource_uploads));
  TracyPlot("Refii Plume Pending Texture Uploads", int64_t{0});
  TracyPlot("Refii Plume Pix Video Events",
            static_cast<int64_t>(tracy_hook_snapshot.pix_render_events));
  EndRenderDocFrameCapture(renderdoc_capture_active);
  return presented;
#else
  return false;
#endif
}

void NotifyPlumeBackendHook(const char* symbol, RendererHookKind kind) {
  if (!PlumeRendererBackendEnabled()) {
    return;
  }

  std::unique_lock<std::mutex> lock(g_plume_backend.mutex, std::try_to_lock);
  if (!lock.owns_lock()) {
    return;
  }

  if (!g_plume_backend.logged_hook_observer) {
    const char* hook_class = kind == RendererHookKind::kDraw ? "draw/state" :
                             kind == RendererHookKind::kResource ? "resource" :
                             "general";
    REXLOG_INFO(
        "Refii plume/{} hook observer is active; {} ({}) is feeding the "
        "static Plume replacement path while native presentation and mirrored "
        "command submission stay suppressed",
        PlumeApiBridgeName(g_plume_backend.api), symbol, hook_class);
    g_plume_backend.logged_hook_observer = true;
  }
}

}  // namespace refii::renderer
