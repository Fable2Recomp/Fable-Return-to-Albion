#include "runtime/plume_backend.h"

#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/plume_api_bridge.h"
#include "runtime/plume_diagnostics.h"
#include "runtime/plume_imgui_overlay.h"
#include "runtime/plume_resource_manager.h"
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
#if !defined(REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED)
#define REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED 0
#endif
#if REFII_RENDERER_HAS_GTK_WINDOW_BRIDGE
#include <X11/Xlib.h>
#endif
#if REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED && defined(__linux__)
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
    TracyPlotConfig("Refii Plume Replay Us",
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

#if REFII_RENDERER_HAS_PLUME && REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED
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

std::string FormatPrimitiveStats(const PlumeDrawReplayStats& stats) {
  std::string summary;
  for (size_t primitive = 0; primitive < stats.primitive_draws.size();
       ++primitive) {
    if (stats.primitive_draws[primitive] == 0) {
      continue;
    }
    if (!summary.empty()) {
      summary += ",";
    }
    summary += "p" + std::to_string(primitive) + "=" +
               std::to_string(stats.primitive_draws[primitive]) + "/" +
               std::to_string(stats.primitive_counts[primitive]);
  }
  return summary.empty() ? "none" : summary;
}

std::string BuildRenderDocCaptureTitle(
    uint32_t frame_index, uint32_t width, uint32_t height,
    const PlumeDrawReplayStats& replay_stats,
    const RendererHookObserverSnapshot& hook_snapshot) {
  char title[256] = {};
  std::snprintf(
      title, sizeof(title),
      "Refii Plume frame %u %ux%u draws %u/%u VS%08X PS%08X RT%08X Pix%08X",
      frame_index, width, height, replay_stats.recorded_draws,
      replay_stats.total_draws, hook_snapshot.vertex_shader,
      hook_snapshot.pixel_shader, replay_stats.present_render_target,
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
    const PlumeDrawReplayStats& replay_stats,
    const RendererHookObserverSnapshot& hook_snapshot,
    const RendererHookTimingSnapshot& timing_snapshot,
    const RendererDrawEventSnapshot* summary_draw_event) {
  std::string comments;
  comments.reserve(2048);
  comments += "Refii Plume RenderDoc capture\n";
  comments += "Frame: " + std::to_string(frame_index) + "\n";
  comments += "Output: " + std::to_string(width) + "x" +
              std::to_string(height) + "\n";
  comments += "Replay draws: " + std::to_string(replay_stats.recorded_draws) +
              " recorded / " + std::to_string(replay_stats.total_draws) +
              " total, ready " +
              std::to_string(replay_stats.resource_ready_draws) + "\n";
  comments += "Missing resources VB/IB/Decl/VS/PS: " +
              std::to_string(replay_stats.missing_vertex_buffer) + " / " +
              std::to_string(replay_stats.missing_index_buffer) + " / " +
              std::to_string(replay_stats.missing_vertex_declaration) + " / " +
              std::to_string(replay_stats.missing_vertex_shader) + " / " +
              std::to_string(replay_stats.missing_pixel_shader) + "\n";
  comments += "Textures object/fetch/missing: " +
              std::to_string(replay_stats.texture_object_bindings) + " / " +
              std::to_string(replay_stats.texture_fetch_bindings) + " / " +
              std::to_string(replay_stats.missing_texture_bindings) + "\n";
  comments += "Texture uploads frame/pending/total decoded/raw/placeholder: " +
              std::to_string(replay_stats.texture_uploads_this_frame) + " / " +
              std::to_string(replay_stats.pending_texture_uploads) + " / " +
              std::to_string(replay_stats.texture_uploads_total) + "  " +
              std::to_string(replay_stats.decoded_texture_uploads_this_frame) +
              " / " +
              std::to_string(replay_stats.raw_texture_uploads_this_frame) +
              " / " +
              std::to_string(
                  replay_stats.placeholder_texture_uploads_this_frame) +
              "\n";
  comments += "Targets present/first/unique/offscreen/filtered/missing: " +
              Hex32(replay_stats.present_render_target) + " / " +
              Hex32(replay_stats.first_render_target) + " / " +
              std::to_string(replay_stats.unique_render_targets) + " / " +
              std::to_string(replay_stats.offscreen_target_draws) + " / " +
              std::to_string(replay_stats.target_filtered_draws) + " / " +
              std::to_string(replay_stats.missing_render_targets) + "\n";
  comments += "Depth bound/tracked/enabled/write/sample unavailable: " +
              std::to_string(replay_stats.depth_bound_draws) + " / " +
              std::to_string(replay_stats.depth_tracked_draws) + " / " +
              std::to_string(replay_stats.depth_enabled_draws) + " / " +
              std::to_string(replay_stats.depth_write_draws) + " / " +
              std::to_string(replay_stats.depth_sample_unavailable_draws) +
              "\n";
  comments += "Primitive stats: " + FormatPrimitiveStats(replay_stats) + "\n";
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
  if (summary_draw_event) {
    comments += "Summary draw prim/indexed/base/start/count: " +
                std::to_string(summary_draw_event->draw.primitive_type) +
                " / " +
                std::to_string(summary_draw_event->draw.indexed ? 1 : 0) +
                " / " + std::to_string(summary_draw_event->draw.base_vertex) +
                " / " + std::to_string(summary_draw_event->draw.start) +
                " / " +
                std::to_string(summary_draw_event->draw.primitive_count) +
                "\n";
    comments += "Summary draw RT0/Depth/VS/PS/Tex0: " +
                Hex32(summary_draw_event->render_targets[0]) + " / " +
                Hex32(summary_draw_event->depth_stencil) + " / " +
                Hex32(summary_draw_event->vertex_shader) + " / " +
                Hex32(summary_draw_event->pixel_shader) + " / " +
                Hex32(summary_draw_event->textures[0]) + "\n";
  }
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
  bool logged_retained_fable_frame = false;
  bool logged_ignored_unreplayable_fable_frame = false;
  std::vector<RendererDrawEventSnapshot> retained_fable_draw_events;
  std::unique_ptr<plume::RenderInterface> render_interface;
  std::unique_ptr<plume::RenderDevice> device;
  std::unique_ptr<plume::RenderCommandQueue> direct_queue;
  std::unique_ptr<plume::RenderCommandQueue> copy_queue;
  std::unique_ptr<plume::RenderSwapChain> swap_chain;
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
std::atomic<int64_t> g_last_plume_replay_us{0};
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
std::atomic<uint32_t> g_retained_plume_fable_draw_events{0};
std::atomic_bool g_last_plume_fable_draw_events_fresh{false};
std::atomic_bool g_plume_backend_shutdown_requested{false};
std::atomic_bool g_plume_async_present_pending{false};
std::atomic_bool g_logged_plume_async_present{false};
std::atomic_bool g_logged_plume_async_present_coalesced{false};

#if REFII_RENDERER_HAS_PLUME
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
  StorePlumeOutputSnapshotLocked();
  g_plume_backend.framebuffers.clear();
  g_plume_backend.depth_framebuffers.clear();
  g_plume_backend.depth_texture.reset();
  g_plume_backend.release_semaphores.clear();
  g_plume_backend.retained_fable_draw_events.clear();
  g_plume_backend.logged_retained_fable_frame = false;
  g_plume_backend.logged_ignored_unreplayable_fable_frame = false;
  g_plume_backend.frame_contexts.clear();
  g_plume_backend.next_frame_context = 0;
  g_plume_backend.swap_chain.reset();
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

void DiscardPlumeFrameFencesLocked() {
  for (PlumeFrameContext& frame_context : g_plume_backend.frame_contexts) {
    frame_context.submitted = false;
  }
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
    g_plume_backend.swap_chain = g_plume_backend.direct_queue->createSwapChain(
        plume::RenderSwapChainDesc(render_window,
                                   plume::RenderFormat::B8G8R8A8_UNORM,
                                   texture_count, false, texture_count));
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
        "vsync={} requested_images={} actual_images={}",
        PlumeApiBridgeName(g_plume_backend.api),
        g_plume_backend.swap_chain->isVsyncEnabled(), texture_count,
        g_plume_backend.swap_chain->getTextureCount());
    return true;
  }

  g_plume_backend.swap_chain->setVsyncEnabled(RendererPlumeVsyncEnabled());
  if (g_plume_backend.swap_chain->getTextureCount() !=
      RendererPlumeSwapchainImageCount()) {
    WaitPlumeFrameFencesLocked();
    ResetPlumeOutputLocked();
    return EnsurePlumeOutputLocked();
  }

  if (g_plume_backend.swap_chain->needsResize()) {
    WaitPlumeFrameFencesLocked();
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

bool DrawEventHasStreamBinding(const RendererDrawEventSnapshot& draw_event) {
  for (const RendererStreamBindingSnapshot& stream : draw_event.streams) {
    if (stream.buffer != 0 && stream.stride != 0) {
      return true;
    }
  }
  return false;
}

bool DrawEventHasTarget(const RendererDrawEventSnapshot& draw_event) {
  if (draw_event.depth_stencil != 0) {
    return true;
  }
  for (uint32_t render_target : draw_event.render_targets) {
    if (render_target != 0) {
      return true;
    }
  }
  return false;
}

uint32_t CountReplayCandidateDrawEvents(
    const std::vector<RendererDrawEventSnapshot>& draw_events) {
  uint32_t count = 0;
  for (const RendererDrawEventSnapshot& draw_event : draw_events) {
    if (draw_event.clear) {
      if (draw_event.clear_flags != 0 && DrawEventHasTarget(draw_event)) {
        ++count;
      }
      continue;
    }

    if (draw_event.draw.primitive_count == 0 ||
        draw_event.vertex_declaration == 0 || draw_event.vertex_shader == 0 ||
        draw_event.pixel_shader == 0 || !DrawEventHasTarget(draw_event)) {
      continue;
    }

    if (!DrawEventHasStreamBinding(draw_event)) {
      continue;
    }
    if (draw_event.draw.indexed && draw_event.index_buffer == 0) {
      continue;
    }
    ++count;
  }
  return count;
}

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

void RequestPlumeBackendShutdown() {
  g_plume_backend_shutdown_requested.store(true, std::memory_order_release);
  g_plume_async_present_pending.store(false, std::memory_order_release);
}

void ShutdownPlumeBackend() {
  RequestPlumeBackendShutdown();
#if REFII_RENDERER_HAS_PLUME && REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED
  if (ShouldMarshalPlumeToUIThread()) {
    CallPlumeOnUIThreadSynchronous([]() { ShutdownPlumeBackend(); });
    return;
  }
#endif

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
#if REFII_RENDERER_HAS_PLUME
  DiscardPlumeFrameFencesLocked();
  ShutdownPlumeImGuiOverlay();
  ShutdownPlumeDiagnostics();
  ShutdownPlumeResourceManager();
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
  WaitPlumeFrameFencesLocked();
  ShutdownPlumeImGuiOverlay();
  ShutdownPlumeDiagnostics();
  ShutdownPlumeResourceManager();
  ResetPlumeOutputLocked();
  g_plume_backend.copy_queue.reset();
  g_plume_backend.direct_queue.reset();
  g_plume_backend.device.reset();
  g_plume_backend.render_interface.reset();

  if (!PlumeApiBridgeSupported(api)) {
    g_plume_backend.status = PlumeBackendStatus::kUnsupportedApi;
    REXLOG_WARN(
        "Refii plume/{} backend was requested, but this build target does "
        "not support that API. Native renderer fallback remains active.",
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
        "Refii plume/{} backend could not create a Plume interface. Native "
        "renderer fallback remains active.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.device =
      g_plume_backend.render_interface->createDevice(std::string());
  if (!g_plume_backend.device) {
    g_plume_backend.status = PlumeBackendStatus::kDeviceUnavailable;
    REXLOG_WARN(
        "Refii plume/{} backend could not create a Plume device. Native "
        "renderer fallback remains active.",
        PlumeApiBridgeName(api));
    return g_plume_backend.status;
  }

  g_plume_backend.direct_queue = g_plume_backend.device->createCommandQueue(
      plume::RenderCommandListType::DIRECT);
  if (!g_plume_backend.direct_queue) {
    g_plume_backend.status = PlumeBackendStatus::kQueueUnavailable;
    REXLOG_WARN(
        "Refii plume/{} backend could not create a direct command queue. "
        "Native renderer fallback remains active.",
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
#if REFII_RENDERER_HAS_PLUME && REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED
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
#if REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED
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
  snapshot.last_replay_us =
      g_last_plume_replay_us.load(std::memory_order_relaxed);
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
  snapshot.last_fable_draw_events =
      g_last_plume_fable_draw_events.load(std::memory_order_relaxed);
  snapshot.retained_fable_draw_events =
      g_retained_plume_fable_draw_events.load(std::memory_order_relaxed);
  snapshot.last_fable_draw_events_fresh =
      g_last_plume_fable_draw_events_fresh.load(std::memory_order_relaxed);
  return snapshot;
}

bool PresentPlumeFrame(bool drive_fable_frame) {
  ZoneNamedN(tracy_frame_zone, "RefiiPlume/PresentFrame", true);
  if (g_plume_backend_shutdown_requested.load(std::memory_order_acquire)) {
    return false;
  }
#if REFII_RENDERER_HAS_PLUME
  ConfigurePlumeBackendTracyPlots();
#if REFII_RENDERER_PLUME_SDL_VULKAN_ENABLED
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
  std::vector<RendererDrawEventSnapshot> draw_events;
  const std::vector<RendererDrawEventSnapshot>* replay_draw_events =
      &draw_events;
  bool replay_draw_events_fresh = false;
  {
    ZoneScopedN("RefiiPlume/ConsumeDrawEvents");
    const bool consume_on_fable_present =
        drive_fable_frame || RendererPlumePresentPumpEnabled();
    draw_events = consume_on_fable_present
                      ? ConsumeRendererPresentedDrawEvents()
                      : ConsumeRendererDrawEvents();
    if (!draw_events.empty()) {
      const uint32_t replay_candidate_draws =
          CountReplayCandidateDrawEvents(draw_events);
      if (replay_candidate_draws != 0 ||
          g_plume_backend.retained_fable_draw_events.empty()) {
        g_plume_backend.retained_fable_draw_events = draw_events;
        replay_draw_events_fresh = true;
        if (drive_fable_frame &&
            !g_plume_backend.logged_retained_fable_frame) {
          REXLOG_INFO(
              "Refii plume present pump is driving Fable frame boundaries and "
              "retaining {} Fable draw events for replay",
              draw_events.size());
          g_plume_backend.logged_retained_fable_frame = true;
        }
      } else {
        replay_draw_events = &g_plume_backend.retained_fable_draw_events;
        if (!g_plume_backend.logged_ignored_unreplayable_fable_frame) {
          REXLOG_INFO(
              "Refii plume ignored {} post-present Fable events without "
              "render-target work; retaining {} previous events",
              draw_events.size(), replay_draw_events->size());
          g_plume_backend.logged_ignored_unreplayable_fable_frame = true;
        }
      }
    } else if ((drive_fable_frame || RendererPlumePresentPumpEnabled()) &&
               !g_plume_backend.retained_fable_draw_events.empty()) {
      replay_draw_events = &g_plume_backend.retained_fable_draw_events;
    }
  }
  g_last_plume_fable_draw_events.store(
      static_cast<uint32_t>(replay_draw_events->size()),
      std::memory_order_relaxed);
  const bool replay_cached_present =
      !replay_draw_events_fresh &&
      replay_draw_events == &g_plume_backend.retained_fable_draw_events &&
      !g_plume_backend.retained_fable_draw_events.empty();
  g_retained_plume_fable_draw_events.store(
      static_cast<uint32_t>(g_plume_backend.retained_fable_draw_events.size()),
      std::memory_order_relaxed);
  g_last_plume_fable_draw_events_fresh.store(replay_draw_events_fresh,
                                             std::memory_order_relaxed);
  const PlumeClock::time_point consume_t1 = PlumeClock::now();
  {
    ZoneScopedN("RefiiPlume/SyncTrackedResources");
    SyncPlumeTrackedResources(g_plume_backend.device.get(), shader_format,
                              replay_draw_events);
  }
  const PlumeClock::time_point sync_t1 = PlumeClock::now();
  PlumeDrawResourceReadiness draw_readiness = {};
  {
    ZoneScopedN("RefiiPlume/AnalyzeDrawReadiness");
    draw_readiness = AnalyzePlumeDrawResourceReadiness(*replay_draw_events);
  }
  const PlumeClock::time_point readiness_t1 = PlumeClock::now();

  const bool renderdoc_capture_started =
      BeginRenderDocFrameCapture("Refii Plume Frame");

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
  {
    ZoneScopedN("RefiiPlume/EnsureImGuiRenderer");
    EnsurePlumeImGuiRenderer(g_plume_backend.device.get(), shader_format);
  }
  command_list->begin();
  {
    ZoneScopedN("RefiiPlume/RecordResourceUploads");
    RecordPlumeResourceUploads(g_plume_backend.device.get(), command_list);
  }
  {
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
  const RendererDrawEventSnapshot* summary_draw_event =
      replay_draw_events->empty() ? nullptr : &replay_draw_events->back();
  if (!g_plume_backend.logged_first_frame_summary || frame_index % 120 == 0) {
    const RendererHookObserverSnapshot hook_snapshot =
        GetRendererHookObserverSnapshot();
    const RendererHookTimingSnapshot timing_snapshot =
        GetRendererHookTimingSnapshot();
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
        "draws={} indexed={} frame_draws={} dropped_draws={} "
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
        replay_draw_events->size(), hook_snapshot.dropped_draw_events,
        draw_readiness.draws_with_vertex_buffer,
        draw_readiness.draws_with_index_buffer,
        draw_readiness.draws_with_vertex_declaration,
        draw_readiness.draws_with_vertex_shader,
        draw_readiness.draws_with_pixel_shader,
        hook_snapshot.presents, timing_snapshot.plume_present_pump_requests,
        timing_snapshot.plume_present_pump_fable_advances,
        timing_snapshot.fable_present_submitted,
        timing_snapshot.fable_present_completed,
        timing_snapshot.fable_swap_callbacks, hook_snapshot.resource_locks,
        hook_snapshot.resource_unlocks, hook_snapshot.last_state_flush_lr,
        hook_snapshot.last_command_first_word,
        hook_snapshot.last_command_type3_opcode,
        (summary_draw_event ? summary_draw_event->draw.indexed
                            : hook_snapshot.last_draw.indexed)
            ? "indexed"
            : "direct",
        summary_draw_event ? summary_draw_event->draw.primitive_type
                           : hook_snapshot.last_draw.primitive_type,
        summary_draw_event ? summary_draw_event->draw.base_vertex
                           : hook_snapshot.last_draw.base_vertex,
        summary_draw_event ? summary_draw_event->draw.start
                           : hook_snapshot.last_draw.start,
        summary_draw_event ? summary_draw_event->draw.primitive_count
                           : hook_snapshot.last_draw.primitive_count,
        summary_draw_event ? summary_draw_event->streams[0].buffer
                           : hook_snapshot.streams[0].buffer,
        summary_draw_event ? summary_draw_event->streams[0].stride
                           : hook_snapshot.streams[0].stride,
        summary_draw_event ? summary_draw_event->index_buffer
                           : hook_snapshot.index_buffer,
        summary_draw_event ? summary_draw_event->vertex_declaration
                           : hook_snapshot.vertex_declaration,
        summary_draw_event ? summary_draw_event->vertex_shader
                           : hook_snapshot.vertex_shader,
        summary_draw_event ? summary_draw_event->pixel_shader
                           : hook_snapshot.pixel_shader);
    g_plume_backend.logged_first_frame_summary = true;
  }
  {
    ZoneScopedN("RefiiPlume/BuildImGuiOverlay");
    DrawPlumeImGuiOverlay(width, height, frame_index);
  }
  const PlumeClock::time_point imgui_build_t1 = PlumeClock::now();
  if (!g_plume_backend.logged_frame_after_imgui_build) {
    REXLOG_INFO("Refii plume frame built ImGui draw data");
    g_plume_backend.logged_frame_after_imgui_build = true;
  }
  command_list->setViewports(
      plume::RenderViewport(0.0f, 0.0f, float(width), float(height)));
  command_list->setScissors(plume::RenderRect(0, 0, width, height));
  command_list->clearColor(0, plume::RenderColor(0.0f, 0.0f, 0.0f, 1.0f));
  if (g_plume_backend.depth_texture) {
    command_list->clearDepthStencil(true, false, 1.0f, 0);
  }
  PlumeDrawReplayStats replay_stats = {};
  {
    ZoneScopedN("RefiiPlume/RecordFableDraws");
    if (replay_cached_present) {
      replay_stats = RecordPlumeCachedFablePresent(
          g_plume_backend.device.get(), command_list, swap_chain_texture,
          g_plume_backend.framebuffers[image_index].get(), shader_format);
    } else {
      replay_stats =
          RecordPlumeFableDraws(g_plume_backend.device.get(), command_list,
                                *replay_draw_events,
                                fable_output_framebuffer,
                                swap_chain_texture,
                                g_plume_backend.framebuffers[image_index].get(),
                                shader_format,
                                g_plume_backend.depth_texture
                                    ? plume::RenderFormat::D32_FLOAT_S8_UINT
                                    : plume::RenderFormat::UNKNOWN);
    }
  }
  const PlumeClock::time_point replay_t1 = PlumeClock::now();
  char tracy_frame_summary[256] = {};
  std::snprintf(
      tracy_frame_summary, sizeof(tracy_frame_summary),
      "frame=%u draws=%u/%u ready=%u missing=%u pipes=%u tex=%u pending=%u "
      "pix=0x%08X",
      frame_index, replay_stats.recorded_draws, replay_stats.total_draws,
      replay_stats.resource_ready_draws,
      replay_stats.missing_vertex_buffer + replay_stats.missing_index_buffer +
          replay_stats.missing_vertex_declaration +
          replay_stats.missing_vertex_shader + replay_stats.missing_pixel_shader,
      replay_stats.missing_pipeline, replay_stats.texture_uploads_this_frame,
      replay_stats.pending_texture_uploads,
      GetRendererHookObserverSnapshot().last_pix_texture);
  ZoneTextV(tracy_frame_zone, tracy_frame_summary,
            std::strlen(tracy_frame_summary));
  if (renderdoc_capture_started) {
    const RendererHookObserverSnapshot renderdoc_hook_snapshot =
        GetRendererHookObserverSnapshot();
    const RendererHookTimingSnapshot renderdoc_timing_snapshot =
        GetRendererHookTimingSnapshot();
    SetRenderDocFrameCaptureTitle(
        BuildRenderDocCaptureTitle(frame_index, width, height, replay_stats,
                                   renderdoc_hook_snapshot)
            .c_str());
    SetRenderDocFrameCaptureComments(
        BuildRenderDocCaptureComments(frame_index, width, height, replay_stats,
                                      renderdoc_hook_snapshot,
                                      renderdoc_timing_snapshot,
                                      summary_draw_event)
            .c_str());
  }
  command_list->setFramebuffer(g_plume_backend.framebuffers[image_index].get());
  {
    ZoneScopedN("RefiiPlume/RenderDiagnostics");
    RenderPlumeDiagnostics(g_plume_backend.device.get(), command_list,
                           shader_format, frame_index, width, height);
  }
  if (replay_stats.total_draws != 0 && frame_index % 120 == 0) {
    REXLOG_INFO(
        "Refii plume draw bridge frame={} draws={} ready={} recorded={} "
        "decl_ready={} missing_vb={} missing_ib={} missing_decl={} "
        "missing_decl_first=0x{:08X} missing_vs={} missing_ps={} "
        "missing_vs_first=0x{:08X} missing_ps_first=0x{:08X} "
        "missing_pipeline={} pipeline_ready={} pipeline_failed={} "
        "tex_object={} tex_fetch={} tex_missing={} "
        "tex_upload_frame={} tex_upload_pending={} tex_upload_total={} "
        "tex_upload_decoded={} tex_upload_raw={} tex_upload_placeholder={} "
        "depth_bound={} depth_requested_no_surface={} z_no_surface={} "
        "hiz_no_surface={} depth_tracked={} depth_only={} "
        "depth_enabled={} depth_write={} "
        "depth_sample_shader={} depth_sample_source={} depth_sample_active={} "
        "depth_sample_alias={} depth_sample_copy={} depth_copy_slots={} "
        "depth_sample_unavailable={} "
        "stencil_enabled={} stencil_clear_req={} stencil_clear={} "
        "feedback_copy={} feedback_slots={} "
        "resolve_copy={} resolve_color={} resolve_depth={} resolve_hits={} "
        "alpha_test={} rect={} rect_direct={} rect_indexed={} rect_count={} "
        "quad={} direct={} indexed={} q_count={} q_indices={} q_split={} "
        "prim_stats={} "
        "rt_present=0x{:08X} rt_first=0x{:08X} rt_unique={} "
        "mrt_draws={} mrt_max={} rt_present_side_slot={} "
        "rt_present_draws={} rt_offscreen={} rt_filtered={} "
        "fb_switches={} rt_copies={} rt_missing={}",
        frame_index, replay_stats.total_draws, replay_stats.resource_ready_draws,
        replay_stats.recorded_draws, replay_stats.declaration_ready_draws,
        replay_stats.missing_vertex_buffer, replay_stats.missing_index_buffer,
        replay_stats.missing_vertex_declaration,
        replay_stats.first_missing_vertex_declaration,
        replay_stats.missing_vertex_shader, replay_stats.missing_pixel_shader,
        replay_stats.first_missing_vertex_shader,
        replay_stats.first_missing_pixel_shader,
        replay_stats.missing_pipeline, replay_stats.pipeline_ready_draws,
        replay_stats.pipeline_create_failed,
        replay_stats.texture_object_bindings,
        replay_stats.texture_fetch_bindings,
        replay_stats.missing_texture_bindings,
        replay_stats.texture_uploads_this_frame,
        replay_stats.pending_texture_uploads,
        replay_stats.texture_uploads_total,
        replay_stats.decoded_texture_uploads_this_frame,
        replay_stats.raw_texture_uploads_this_frame,
        replay_stats.placeholder_texture_uploads_this_frame,
        replay_stats.depth_bound_draws,
        replay_stats.depth_requested_without_surface_draws,
        replay_stats.z_requested_without_surface_draws,
        replay_stats.hiz_requested_without_surface_draws,
        replay_stats.depth_tracked_draws, replay_stats.depth_only_draws,
        replay_stats.depth_enabled_draws, replay_stats.depth_write_draws,
        replay_stats.depth_sample_shader_draws,
        replay_stats.depth_sample_source_draws,
        replay_stats.depth_sample_active_depth_fallback_draws,
        replay_stats.depth_sample_alias_draws,
        replay_stats.depth_sample_copy_draws,
        replay_stats.depth_sample_copy_slots,
        replay_stats.depth_sample_unavailable_draws,
        replay_stats.stencil_enabled_draws,
        replay_stats.stencil_clear_requests,
        replay_stats.stencil_clears,
        replay_stats.target_feedback_copy_draws,
        replay_stats.target_feedback_copy_slots,
        replay_stats.resolve_copy_draws,
        replay_stats.resolve_color_aliases,
        replay_stats.resolve_depth_aliases,
        replay_stats.resolve_texture_alias_hits,
        replay_stats.alpha_test_draws,
        replay_stats.rectangle_list_draws,
        replay_stats.rectangle_list_direct_draws,
        replay_stats.rectangle_list_indexed_draws,
        replay_stats.rectangle_list_vertex_or_index_count,
        replay_stats.quad_list_draws,
        replay_stats.quad_list_direct_draws,
        replay_stats.quad_list_indexed_draws,
        replay_stats.quad_list_vertex_or_index_count,
        replay_stats.quad_list_expanded_index_count,
        RendererQuadListStrip0132Order() ? "strip0132" : "sequential0123",
        FormatPrimitiveStats(replay_stats),
        replay_stats.present_render_target, replay_stats.first_render_target,
        replay_stats.unique_render_targets,
        replay_stats.multi_render_target_draws,
        replay_stats.max_render_targets_per_draw,
        replay_stats.present_target_nonzero_slot_draws,
        replay_stats.present_target_draws,
        replay_stats.offscreen_target_draws,
        replay_stats.target_filtered_draws,
        replay_stats.framebuffer_switches,
        replay_stats.present_target_copies,
        replay_stats.missing_render_targets);
  }
  {
    ZoneScopedN("RefiiPlume/RenderImGuiOverlay");
    RenderPlumeImGuiOverlay(g_plume_backend.device.get(), command_list);
  }
  const PlumeClock::time_point imgui_render_t1 = PlumeClock::now();
  if (!g_plume_backend.logged_frame_after_imgui_render) {
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
    EndRenderDocFrameCapture(renderdoc_capture_started);
    return false;
  }
  bool presented = false;
  {
    ZoneScopedN("RefiiPlume/SwapChainPresent");
    presented = g_plume_backend.swap_chain->present(
        image_index, &signal_semaphore, 1);
  }
  const PlumeClock::time_point present_t1 = PlumeClock::now();
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
        "imgui_build_ms={:.3f} replay_ms={:.3f} imgui_render_ms={:.3f} "
        "cmd_end_ms={:.3f} submit_ms={:.3f} present_ms={:.3f} "
        "context_wait_ms={:.3f} draws={}",
        frame_index, PlumeElapsedMs(frame_t0, present_t1),
        PlumeElapsedMs(frame_t0, backend_t1),
        PlumeElapsedMs(backend_t1, output_t1),
        PlumeElapsedMs(output_t1, consume_t1),
        PlumeElapsedMs(consume_t1, sync_t1),
        PlumeElapsedMs(sync_t1, readiness_t1),
        PlumeElapsedMs(readiness_t1, acquire_t1),
        PlumeElapsedMs(acquire_t1, setup_t1),
        PlumeElapsedMs(setup_t1, imgui_build_t1),
        PlumeElapsedMs(imgui_build_t1, replay_t1),
        PlumeElapsedMs(replay_t1, imgui_render_t1),
        PlumeElapsedMs(imgui_render_t1, command_end_t1),
        PlumeElapsedMs(command_end_t1, submit_t1),
        PlumeElapsedMs(submit_t1, present_t1),
        PlumeElapsedMs(frame_context_wait_t0, frame_context_wait_t1),
        replay_draw_events->size());
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
  const int64_t replay_us = PlumeElapsedUs(imgui_build_t1, replay_t1);
  const int64_t imgui_render_us =
      PlumeElapsedUs(replay_t1, imgui_render_t1);
  const int64_t command_record_us =
      PlumeElapsedUs(imgui_render_t1, command_end_t1);
  const int64_t submit_us = PlumeElapsedUs(command_end_t1, submit_t1);
  const int64_t present_us = PlumeElapsedUs(submit_t1, present_t1);
  const int64_t fence_wait_us =
      PlumeElapsedUs(frame_context_wait_t0, frame_context_wait_t1);
  const RendererHookObserverSnapshot tracy_hook_snapshot =
      GetRendererHookObserverSnapshot();
  const uint32_t missing_resource_draws =
      replay_stats.missing_vertex_buffer + replay_stats.missing_index_buffer +
      replay_stats.missing_vertex_declaration +
      replay_stats.missing_vertex_shader + replay_stats.missing_pixel_shader;
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
  g_last_plume_replay_us.store(replay_us, std::memory_order_relaxed);
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
  TracyPlot("Refii Plume Replay Us", replay_us);
  TracyPlot("Refii Plume ImGui Build Us", imgui_build_us);
  TracyPlot("Refii Plume ImGui Render Us", imgui_render_us);
  TracyPlot("Refii Plume Command Record Us", command_record_us);
  TracyPlot("Refii Plume Submit Us", submit_us);
  TracyPlot("Refii Plume Present Us", present_us);
  TracyPlot("Refii Plume Fence Wait Us", fence_wait_us);
  TracyPlot("Refii Plume Draw Events",
            static_cast<int64_t>(replay_draw_events->size()));
  TracyPlot("Refii Plume Recorded Draws",
            static_cast<int64_t>(replay_stats.recorded_draws));
  TracyPlot("Refii Plume Missing Resources",
            static_cast<int64_t>(missing_resource_draws));
  TracyPlot("Refii Plume Missing Pipelines",
            static_cast<int64_t>(replay_stats.missing_pipeline));
  TracyPlot("Refii Plume Filtered Draws",
            static_cast<int64_t>(replay_stats.target_filtered_draws));
  TracyPlot("Refii Plume Texture Uploads",
            static_cast<int64_t>(replay_stats.texture_uploads_this_frame));
  TracyPlot("Refii Plume Pending Texture Uploads",
            static_cast<int64_t>(replay_stats.pending_texture_uploads));
  TracyPlot("Refii Plume Pix Video Events",
            static_cast<int64_t>(tracy_hook_snapshot.pix_render_events));
  EndRenderDocFrameCapture(renderdoc_capture_started);
  return presented;
#else
  return false;
#endif
}

void NotifyPlumeBackendHook(const char* symbol, RendererHookKind kind) {
  if (!PlumeRendererBackendEnabled()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_plume_backend.mutex);
  if (!g_plume_backend.logged_hook_observer) {
    const char* hook_class = kind == RendererHookKind::kDraw ? "draw/state" :
                             kind == RendererHookKind::kResource ? "resource" :
                             "general";
    if (RendererNativeFallbackAllowed()) {
      REXLOG_INFO(
          "Refii plume/{} hook observer is active; replacement state will be "
          "staged from {} ({}) while native fallback remains active",
          PlumeApiBridgeName(g_plume_backend.api), symbol, hook_class);
    } else {
      REXLOG_INFO(
          "Refii plume/{} hook observer is active; replacement state will be "
          "staged from {} ({}) while host presentation stays suppressed",
          PlumeApiBridgeName(g_plume_backend.api), symbol, hook_class);
    }
    g_plume_backend.logged_hook_observer = true;
  }
}

}  // namespace refii::renderer
